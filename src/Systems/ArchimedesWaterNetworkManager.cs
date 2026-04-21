using System;
using System.Collections.Concurrent;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace ArchimedesScrew;

public sealed partial class ArchimedesWaterNetworkManager : IDisposable
{
    private const string SaveKeyScrewBlocks = "archimedes_screw/screwblocks";
    private const string SaveKeyControllerPositions = "archimedes_screw/controllerpositions";
    private const string SaveKeyControllerOwned = "archimedes_screw/controllerowned";
    private const string SaveKeyControllerRelayOwned = "archimedes_screw/controllerrelayowned";
    private const string SaveKeySourceProvenance = "archimedes_screw/sourceprovenance";
    private const string SaveKeyLockedVanilla = "archimedes_screw/lockedvanilla";

    private const int MaxBfsVisited = 4096;
    private const int UnownedCleanupRetryCooldownMs = 3000;
    private const int ManagedAdoptionCooldownAfterReleaseMs = 2500;
    private const int DrainQuarantineMs = 1500;

    /// <summary>
    /// Multiplier applied to <see cref="DrainQuarantineMs"/> for cells inside HardcoreWater aqueducts,
    /// to absorb the HCW refill cycle without triggering re-promotion churn.
    /// TODO: expose via config if players need to tune this.
    /// </summary>
    private const int AqueductDrainQuarantineMultiplier = 2;

    private readonly ICoreServerAPI api;
    private readonly ArchimedesScrewConfig config;

    private readonly Dictionary<long, string> sourceOwnerByPos = new();
    private readonly Dictionary<long, long> managedAdoptionCooldownUntilMsByKey = new();
    private readonly Dictionary<long, long> unownedCleanupCooldownUntilMsByKey = new();
    private readonly Dictionary<long, long> drainQuarantineUntilMsByKey = new();
    private readonly HashSet<long> screwBlockKeys = new();
    private readonly Dictionary<string, long> controllerPosById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int[]> controllerOwnedById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WeakReference<BlockEntityWaterArchimedesScrew>> loadedControllers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, byte> suppressedRemovalNotifications = new();
    private readonly Dictionary<string, Block> managedBlockCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<long>> controllerRelaySourceKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Reverse index of <see cref="controllerRelaySourceKeys"/>: packed pos key -> owning controllerId.
    /// Authoritative source of truth for "is this cell relay-owned" queries; survives BE chunk unload.
    /// </summary>
    private readonly Dictionary<long, string> relayOwnerByPos = new();

    private readonly Dictionary<string, WeakReference<BlockEntityWaterArchimedesScrew>> centralWaterTickControllers = new(StringComparer.Ordinal);
    private readonly List<string> centralWaterTickOrder = new();
    private readonly HashSet<string> centralWaterTickSet = new(StringComparer.Ordinal);
    private readonly Dictionary<long, ConnectedManagedComponentCacheEntry> connectedManagedComponentCache = new();
    private readonly List<ArchimedesOutletState> activeSeedStatesCache = new();
    private int centralWaterTickCursor;
    private int centralWaterTickCountDownToCompaction = 20;
    private int connectedManagedComponentCacheGeneration;
    private int activeSeedStatesCacheGeneration = -1;
    private bool isInGlobalWaterTickDispatch;
    private long lastTruncationPauseLogAtMs;
    private long globalWaterTickListenerId;
    private long postLoadReactivationListenerId;
    private int postLoadReactivationAttemptsRemaining;

    /// <summary>Count of registered weak refs (includes unloaded targets); for save/load diagnostics only.</summary>
    public int LoadedControllerWeakReferenceCount => loadedControllers.Count;

    public ArchimedesWaterNetworkManager(ICoreServerAPI api, ArchimedesScrewConfig config)
    {
        this.api = api;
        this.config = config;
        ArchimedesPosKey.InitializeForWorld(api.WorldManager.MapSizeX, api.WorldManager.MapSizeY, api.WorldManager.MapSizeZ);
    }

    public void Dispose()
    {
        StopCentralWaterTick();
        StopPostLoadReactivation();
        GC.SuppressFinalize(this);
    }

    /// <summary>Registers the single server tick that runs intake water logic (staggered, budgeted).</summary>
    public void StartCentralWaterTick()
    {
        StopCentralWaterTick();
        int interval = Math.Max(5, config.Water.GlobalTickMs);
        globalWaterTickListenerId = api.Event.RegisterGameTickListener(OnGlobalWaterTick, interval);
    }

    /// <summary>Call after <see cref="ArchimedesScrewConfig.Water"/> fields change (e.g. Config Lib live reload).</summary>
    public void RestartCentralWaterTickForCurrentConfig()
    {
        StartCentralWaterTick();
    }

    public void StopCentralWaterTick()
    {
        if (globalWaterTickListenerId != 0)
        {
            api.Event.UnregisterGameTickListener(globalWaterTickListenerId);
            globalWaterTickListenerId = 0;
        }
    }

    public void BeginPostLoadReactivation(int initialDelayMs = 300, int retryIntervalMs = 700, int maxAttempts = 8)
    {
        StopPostLoadReactivation();
        postLoadReactivationAttemptsRemaining = Math.Max(1, maxAttempts);

        int initialDelay = Math.Max(50, initialDelayMs);
        int retryInterval = Math.Max(100, retryIntervalMs);
        postLoadReactivationListenerId = api.Event.RegisterGameTickListener(
            _ => OnPostLoadReactivationTick(retryInterval),
            initialDelay
        );
    }

    private void StopPostLoadReactivation()
    {
        if (postLoadReactivationListenerId != 0)
        {
            api.Event.UnregisterGameTickListener(postLoadReactivationListenerId);
            postLoadReactivationListenerId = 0;
        }
    }

    private void OnPostLoadReactivationTick(int retryIntervalMs)
    {
        if (postLoadReactivationAttemptsRemaining <= 0)
        {
            StopPostLoadReactivation();
            return;
        }

        postLoadReactivationAttemptsRemaining--;
        int touched = ReactivateManagedFluidsFromTrackedAnchors();

        if (postLoadReactivationAttemptsRemaining <= 0)
        {
            StopPostLoadReactivation();
            api.Logger.Notification(
                "{0} Post-load managed fluid reactivation finished; touched={1}",
                ArchimedesScrewModSystem.LogPrefix,
                touched
            );
            return;
        }

        StopPostLoadReactivation();
        postLoadReactivationListenerId = api.Event.RegisterGameTickListener(
            _ => OnPostLoadReactivationTick(retryIntervalMs),
            retryIntervalMs
        );
    }

    public int ReactivateManagedFluidsFromTrackedAnchors()
    {
        HashSet<long> anchors = BuildManagedWaterAnchorKeys();
        HashSet<long> allWaterKeys = new();
        foreach (long key in anchors)
        {
            BlockPos pos = ArchimedesPosKey.UnpackToNew(key);
            CollectManagedComponentKeysAroundAnchor(pos, allWaterKeys);
        }

        int touched = 0;
        foreach (long key in allWaterKeys)
        {
            BlockPos pos = ArchimedesPosKey.UnpackToNew(key);
            Block fluid = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
            if (!IsArchimedesWaterBlock(fluid))
            {
                continue;
            }

            TriggerLiquidUpdates(pos, fluid);
            touched++;
        }

        api.Logger.Notification(
            "{0} Post-load managed fluid reactivation pass touched={1} (anchors={2})",
            ArchimedesScrewModSystem.LogPrefix,
            touched,
            anchors.Count
        );

        return touched;
    }

    public void RegisterForCentralWaterTick(BlockEntityWaterArchimedesScrew controller)
    {
        string id = controller.ControllerId;
        centralWaterTickControllers[id] = new WeakReference<BlockEntityWaterArchimedesScrew>(controller);
        if (centralWaterTickSet.Add(id))
        {
            int insertAt = centralWaterTickOrder.BinarySearch(id, StringComparer.Ordinal);
            if (insertAt < 0)
            {
                insertAt = ~insertAt;
            }
            centralWaterTickOrder.Insert(insertAt, id);

            if (insertAt <= centralWaterTickCursor && centralWaterTickOrder.Count > 1)
            {
                centralWaterTickCursor++;
            }
        }
    }

    public void UnregisterFromCentralWaterTick(string controllerId)
    {
        centralWaterTickControllers.Remove(controllerId);
        if (!centralWaterTickSet.Remove(controllerId))
        {
            return;
        }

        centralWaterTickOrder.RemoveAll(s => string.Equals(s, controllerId, StringComparison.Ordinal));
    }

    private void OnGlobalWaterTick(float dt)
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("water.globalTick");
        isInGlobalWaterTickDispatch = true;
        try
        {
            ProcessConversionIntentQueue();
            connectedManagedComponentCacheGeneration++;
            connectedManagedComponentCache.Clear();
            activeSeedStatesCacheGeneration = -1;
            activeSeedStatesCache.Clear();
            if (--centralWaterTickCountDownToCompaction <= 0)
            {
                CompactCentralWaterTickList();
                centralWaterTickCountDownToCompaction = 20;
            }

            long now = Environment.TickCount64;
            int budget = Math.Max(1, config.Water.MaxControllersPerGlobalTick);
            int n = centralWaterTickOrder.Count;
            if (n == 0)
            {
                return;
            }

            int processed = 0;
            for (int step = 0; step < n; step++)
            {
                if (processed >= budget)
                {
                    break;
                }

                int idx = (centralWaterTickCursor + step) % n;
                string id = centralWaterTickOrder[idx];

                if (!centralWaterTickControllers.TryGetValue(id, out WeakReference<BlockEntityWaterArchimedesScrew>? wr) ||
                    !wr.TryGetTarget(out BlockEntityWaterArchimedesScrew? be))
                {
                    continue;
                }

                if (!be.IsCentralWaterTickDue(now))
                {
                    continue;
                }

                be.RunCentralWaterTick();
                processed++;
            }

            ArchimedesPerf.AddCount("water.globalTick.processedControllers", processed);
            centralWaterTickCursor = (centralWaterTickCursor + 1) % n;
        }
        finally
        {
            isInGlobalWaterTickDispatch = false;
            ArchimedesPerf.MaybeFlush(api);
        }
    }

    private void CompactCentralWaterTickList()
    {
        for (int i = centralWaterTickOrder.Count - 1; i >= 0; i--)
        {
            string id = centralWaterTickOrder[i];
            if (!centralWaterTickControllers.TryGetValue(id, out WeakReference<BlockEntityWaterArchimedesScrew>? wr) ||
                !wr.TryGetTarget(out _))
            {
                centralWaterTickOrder.RemoveAt(i);
                centralWaterTickControllers.Remove(id);
                centralWaterTickSet.Remove(id);
            }
        }

        if (centralWaterTickCursor >= centralWaterTickOrder.Count && centralWaterTickOrder.Count > 0)
        {
            centralWaterTickCursor %= centralWaterTickOrder.Count;
        }
    }

    public void Load()
    {
        screwBlockKeys.Clear();
        controllerPosById.Clear();
        controllerOwnedById.Clear();
        controllerRelaySourceKeys.Clear();
        relayOwnerByPos.Clear();
        sourceOwnerByPos.Clear();
        sourceProvenanceByPos.Clear();
        lockedVanillaFamilyByPos.Clear();
        unownedCleanupCooldownUntilMsByKey.Clear();

        int droppedScrewKeys = 0;
        List<string> screwKeySamples = new();
        string[]? screwKeys = LoadSerialized<string[]>(SaveKeyScrewBlocks);
        if (screwKeys != null)
        {
            foreach (string key in screwKeys)
            {
                if (ArchimedesPosKey.TryPackFromString(key, out long packed))
                {
                    screwBlockKeys.Add(packed);
                }
                else
                {
                    droppedScrewKeys++;
                    AddInvalidPosKeySample(screwKeySamples, key);
                }
            }
        }

        int droppedControllerPositions = 0;
        List<string> controllerPosSamples = new();
        Dictionary<string, string>? controllerPositions = LoadSerialized<Dictionary<string, string>>(SaveKeyControllerPositions);
        if (controllerPositions != null)
        {
            foreach ((string id, string posKey) in controllerPositions)
            {
                if (ArchimedesPosKey.TryPackFromString(posKey, out long packed))
                {
                    controllerPosById[id] = packed;
                }
                else
                {
                    droppedControllerPositions++;
                    AddInvalidPosKeySample(controllerPosSamples, posKey);
                }
            }
        }

        Dictionary<string, int[]>? ownedSources = LoadSerialized<Dictionary<string, int[]>>(SaveKeyControllerOwned);
        if (ownedSources == null)
        {
            api.Logger.Notification(
                "{0} Load: no mod blob {1} (first run or legacy save); ownership will come from block entities when chunks load",
                ArchimedesScrewModSystem.LogPrefix,
                SaveKeyControllerOwned
            );

            if (droppedScrewKeys > 0 || droppedControllerPositions > 0)
            {
                api.Logger.Warning(
                    "{0} Load: dropped {1} invalid screw key(s) and {2} invalid controller position entr(y/ies) from save. Screw sample(s): {3}; controller sample(s): {4}",
                    ArchimedesScrewModSystem.LogPrefix,
                    droppedScrewKeys,
                    droppedControllerPositions,
                    screwKeySamples.Count > 0 ? string.Join(", ", screwKeySamples) : "—",
                    controllerPosSamples.Count > 0 ? string.Join(", ", controllerPosSamples) : "—");
            }

            api.Logger.Notification(
                "{0} Loaded water manager state: screws={1}, controllers={2}, trackedSources={3}, lockedVanilla={4}",
                ArchimedesScrewModSystem.LogPrefix,
                screwBlockKeys.Count,
                controllerPosById.Count,
                sourceOwnerByPos.Count,
                lockedVanillaFamilyByPos.Count
            );
            return;
        }

        int modBlobControllerRows = ownedSources.Count;
        int duplicateSourceClaims = 0;
        List<(string ControllerId, string PosKey, string PreviousOwner)> conflictSamples = new();

        foreach (string controllerId in ownedSources.Keys.OrderBy(id => id, StringComparer.Ordinal))
        {
            int[]? flatPositions = ownedSources[controllerId];
            if (flatPositions == null || flatPositions.Length == 0)
            {
                controllerOwnedById[controllerId] = Array.Empty<int>();
                continue;
            }

            controllerOwnedById[controllerId] = flatPositions;
            foreach (BlockPos pos in ArchimedesPositionCodec.DecodePositions(flatPositions))
            {
                long key = ArchimedesPosKey.Pack(pos);
                if (!sourceOwnerByPos.TryGetValue(key, out string? existing))
                {
                    sourceOwnerByPos[key] = controllerId;
                    continue;
                }

                duplicateSourceClaims++;
                if (conflictSamples.Count < 5 &&
                    !string.Equals(existing, controllerId, StringComparison.Ordinal))
                {
                    conflictSamples.Add((controllerId, ArchimedesPosKey.ToDebugString(key), existing));
                }

                // Deterministic conflict resolution for old saves that had multi-owner snapshots.
                if (string.CompareOrdinal(controllerId, existing) < 0)
                {
                    sourceOwnerByPos[key] = controllerId;
                }
            }
        }

        Dictionary<string, int[]>? relayOwnedSources = LoadSerialized<Dictionary<string, int[]>>(SaveKeyControllerRelayOwned);
        if (relayOwnedSources != null)
        {
            foreach ((string cId, int[]? flatPositions) in relayOwnedSources)
            {
                if (flatPositions == null || flatPositions.Length == 0)
                {
                    controllerRelaySourceKeys[cId] = new HashSet<long>();
                    continue;
                }

                HashSet<long> relayKeys = new();
                foreach (BlockPos pos in ArchimedesPositionCodec.DecodePositions(flatPositions))
                {
                    long packed = ArchimedesPosKey.Pack(pos);
                    relayKeys.Add(packed);
                    relayOwnerByPos[packed] = cId;
                }

                controllerRelaySourceKeys[cId] = relayKeys;
            }
        }

        Dictionary<string, int>? provenanceRaw = LoadSerialized<Dictionary<string, int>>(SaveKeySourceProvenance);
        if (provenanceRaw != null)
        {
            foreach ((string key, int rawValue) in provenanceRaw)
            {
                if (!ArchimedesPosKey.TryPackFromString(key, out long packed))
                {
                    continue;
                }

                if (Enum.IsDefined(typeof(ManagedSourceProvenance), rawValue))
                {
                    sourceProvenanceByPos[packed] = (ManagedSourceProvenance)rawValue;
                }
            }
        }

        foreach ((long key, _) in sourceOwnerByPos)
        {
            if (!sourceProvenanceByPos.ContainsKey(key))
            {
                sourceProvenanceByPos[key] = ManagedSourceProvenance.ControllerSeedOrRelay;
            }
        }

        Dictionary<string, string>? lockedVanillaRaw = LoadSerialized<Dictionary<string, string>>(SaveKeyLockedVanilla);
        if (lockedVanillaRaw != null)
        {
            foreach ((string key, string familyId) in lockedVanillaRaw)
            {
                if (!ArchimedesPosKey.TryPackFromString(key, out long packed))
                {
                    continue;
                }

                if (ArchimedesWaterFamilies.All.Any(f => string.Equals(f.Id, familyId, StringComparison.Ordinal)))
                {
                    lockedVanillaFamilyByPos[packed] = familyId;
                }
            }
        }

        api.Logger.Notification(
            "{0} Load: merged mod ownership blob rows={1}, uniqueTrackedSources={2}, duplicatePositionClaimsWhileMerging={3}",
            ArchimedesScrewModSystem.LogPrefix,
            modBlobControllerRows,
            sourceOwnerByPos.Count,
            duplicateSourceClaims
        );

        if (duplicateSourceClaims > 0)
        {
            string sampleText = conflictSamples.Count > 0
                ? string.Join(
                    "; ",
                    conflictSamples.Select(c =>
                        $"pos={c.PosKey} keptLowerIdWinner claimant={c.ControllerId} other={c.PreviousOwner}"))
                : "—";
            api.Logger.Warning(
                "{0} Load: {1} duplicate source position claim(s) while merging controllerowned (same cell listed for multiple controllers). Sample: {2}",
                ArchimedesScrewModSystem.LogPrefix,
                duplicateSourceClaims,
                sampleText);
        }

        if (droppedScrewKeys > 0 || droppedControllerPositions > 0)
        {
            api.Logger.Warning(
                "{0} Load: dropped {1} invalid screw key(s) and {2} invalid controller position entr(y/ies) from save. Screw sample(s): {3}; controller sample(s): {4}",
                ArchimedesScrewModSystem.LogPrefix,
                droppedScrewKeys,
                droppedControllerPositions,
                screwKeySamples.Count > 0 ? string.Join(", ", screwKeySamples) : "—",
                controllerPosSamples.Count > 0 ? string.Join(", ", controllerPosSamples) : "—");
        }

        api.Logger.Notification(
            "{0} Loaded water manager state: screws={1}, controllers={2}, trackedSources={3}, lockedVanilla={4}",
            ArchimedesScrewModSystem.LogPrefix,
            screwBlockKeys.Count,
            controllerPosById.Count,
            sourceOwnerByPos.Count,
            lockedVanillaFamilyByPos.Count
        );
    }

    public void Save()
    {
        api.WorldManager.SaveGame.StoreData(
            SaveKeyScrewBlocks,
            SerializerUtil.Serialize(screwBlockKeys.Select(ArchimedesPosKey.ToDebugString).ToArray())
        );
        api.WorldManager.SaveGame.StoreData(
            SaveKeyControllerPositions,
            SerializerUtil.Serialize(controllerPosById.ToDictionary(kvp => kvp.Key, kvp => ArchimedesPosKey.ToDebugString(kvp.Value), StringComparer.Ordinal))
        );
        api.WorldManager.SaveGame.StoreData(SaveKeyControllerOwned, SerializerUtil.Serialize(controllerOwnedById));
        api.WorldManager.SaveGame.StoreData(
            SaveKeyControllerRelayOwned,
            SerializerUtil.Serialize(
                controllerRelaySourceKeys.ToDictionary(
                    kvp => kvp.Key,
                    kvp => ArchimedesPositionCodec.EncodePositions(kvp.Value.Select(ArchimedesPosKey.UnpackToNew).ToList()),
                    StringComparer.Ordinal))
        );
        api.WorldManager.SaveGame.StoreData(
            SaveKeySourceProvenance,
            SerializerUtil.Serialize(sourceProvenanceByPos.ToDictionary(kvp => ArchimedesPosKey.ToDebugString(kvp.Key), kvp => (int)kvp.Value, StringComparer.Ordinal))
        );
        api.WorldManager.SaveGame.StoreData(
            SaveKeyLockedVanilla,
            SerializerUtil.Serialize(lockedVanillaFamilyByPos.ToDictionary(kvp => ArchimedesPosKey.ToDebugString(kvp.Key), kvp => kvp.Value, StringComparer.Ordinal))
        );
        api.Logger.Notification(
            "{0} Saved water manager state: screws={1}, controllerPosEntries={2}, controllerOwnedSnapshots={3}, trackedSources={4}, trackedProvenance={5}, lockedVanilla={6}, loadedControllerWeakRefs={7}",
            ArchimedesScrewModSystem.LogPrefix,
            screwBlockKeys.Count,
            controllerPosById.Count,
            controllerOwnedById.Count,
            sourceOwnerByPos.Count,
            sourceProvenanceByPos.Count,
            lockedVanillaFamilyByPos.Count,
            loadedControllers.Count
        );
    }

    public void RegisterLoadedController(BlockEntityWaterArchimedesScrew controller)
    {
        loadedControllers[controller.ControllerId] = new WeakReference<BlockEntityWaterArchimedesScrew>(controller);
        controllerPosById[controller.ControllerId] = ArchimedesPosKey.Pack(controller.Pos);
    }

    public void UnregisterLoadedController(string controllerId)
    {
        loadedControllers.Remove(controllerId);
    }

    public void RegisterScrewBlock(BlockPos pos)
    {
        screwBlockKeys.Add(ArchimedesPosKey.Pack(pos));
    }

    public void UnregisterScrewBlock(BlockPos pos)
    {
        screwBlockKeys.Remove(ArchimedesPosKey.Pack(pos));
    }

    public bool IsArchimedesWaterBlock(Block block)
    {
        return block.Code?.Domain == ArchimedesScrewModSystem.ModId &&
               ArchimedesWaterFamilies.IsManagedWater(block);
    }

    public bool IsArchimedesSourceBlock(Block block)
    {
        return IsArchimedesWaterBlock(block) &&
               IsSourceHeight(block.Variant?["height"]);
    }

    public bool IsArchimedesRelayFlowCandidate(Block block)
    {
        return IsArchimedesWaterBlock(block) &&
               string.Equals(block.Variant?["height"], "6", StringComparison.Ordinal);
    }

    public bool TryGetSourceOwner(BlockPos pos, out string ownerId)
    {
        return sourceOwnerByPos.TryGetValue(ArchimedesPosKey.Pack(pos), out ownerId!);
    }

    public bool IsControllerLoaded(string controllerId)
    {
        return loadedControllers.TryGetValue(controllerId, out WeakReference<BlockEntityWaterArchimedesScrew>? wr) &&
               wr.TryGetTarget(out _);
    }

    public void MarkDrainQuarantine(BlockPos pos, int durationMs = DrainQuarantineMs)
    {
        long key = ArchimedesPosKey.Pack(pos);
        int effectiveDurationMs = durationMs;
        if (ArchimedesAqueductDetector.IsAqueductCell(api.World, pos))
        {
            effectiveDurationMs = Math.Max(durationMs, durationMs * AqueductDrainQuarantineMultiplier);
        }
        long until = Environment.TickCount64 + Math.Max(0, effectiveDurationMs);
        if (drainQuarantineUntilMsByKey.TryGetValue(key, out long existingUntil) && existingUntil >= until)
        {
            return;
        }

        drainQuarantineUntilMsByKey[key] = until;
    }

    public bool IsDrainQuarantined(BlockPos pos)
    {
        return IsDrainQuarantined(ArchimedesPosKey.Pack(pos));
    }

    public bool IsDrainQuarantined(long key)
    {
        long now = Environment.TickCount64;
        PruneExpiredDrainQuarantine(now);
        return drainQuarantineUntilMsByKey.TryGetValue(key, out long until) && now < until;
    }

    private void PruneExpiredDrainQuarantine(long nowMs)
    {
        if (drainQuarantineUntilMsByKey.Count == 0)
        {
            return;
        }

        foreach (long key in drainQuarantineUntilMsByKey.Keys.ToList())
        {
            if (drainQuarantineUntilMsByKey[key] <= nowMs)
            {
                drainQuarantineUntilMsByKey.Remove(key);
            }
        }
    }

    public bool TryResolveVanillaWaterFamily(Block block, out string familyId)
    {
        if (ArchimedesWaterFamilies.TryResolveVanillaFamily(block, out ArchimedesWaterFamily family))
        {
            familyId = family.Id;
            return true;
        }

        familyId = string.Empty;
        return false;
    }

    public bool TryResolveManagedWaterFamily(Block block, out string familyId)
    {
        if (ArchimedesWaterFamilies.TryResolveManagedFamily(block, out ArchimedesWaterFamily family))
        {
            familyId = family.Id;
            return true;
        }

        familyId = string.Empty;
        return false;
    }

    /// <summary>Vanilla or mod-managed Archimedes liquid at an intake cell (any flow/height).</summary>
    public bool TryResolveIntakeWaterFamily(Block block, out string familyId)
    {
        if (TryResolveVanillaWaterFamily(block, out familyId))
        {
            return true;
        }

        if (TryResolveManagedWaterFamily(block, out familyId))
        {
            return true;
        }

        familyId = string.Empty;
        return false;
    }

    public List<ArchimedesOutletState> GetActiveSeedStates()
    {
        List<ArchimedesOutletState> states = new();
        foreach (WeakReference<BlockEntityWaterArchimedesScrew> reference in loadedControllers.Values)
        {
            if (reference.TryGetTarget(out BlockEntityWaterArchimedesScrew? controller) &&
                controller.TryGetActiveSeedState(out ArchimedesOutletState state))
            {
                states.Add(state);
            }
        }

        return states;
    }

    public IReadOnlyList<ArchimedesOutletState> GetActiveSeedStatesCached()
    {
        if (!isInGlobalWaterTickDispatch)
        {
            return GetActiveSeedStates();
        }

        if (activeSeedStatesCacheGeneration == connectedManagedComponentCacheGeneration)
        {
            return activeSeedStatesCache;
        }

        activeSeedStatesCache.Clear();
        foreach (WeakReference<BlockEntityWaterArchimedesScrew> reference in loadedControllers.Values)
        {
            if (reference.TryGetTarget(out BlockEntityWaterArchimedesScrew? controller) &&
                controller.TryGetActiveSeedState(out ArchimedesOutletState state))
            {
                activeSeedStatesCache.Add(state);
            }
        }

        activeSeedStatesCacheGeneration = connectedManagedComponentCacheGeneration;
        return activeSeedStatesCache;
    }

    public ConnectedManagedWaterResult CollectConnectedManagedWaterDetailed(BlockPos startPos)
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("water.collectConnectedManaged");
        Dictionary<long, BlockPos> positionsByKey = new();
        HashSet<long> visited = new();

        Block startFluid = api.World.BlockAccessor.GetBlock(startPos, BlockLayersAccess.Fluid);
        if (!IsArchimedesWaterBlock(startFluid))
        {
            return new ConnectedManagedWaterResult(visited, positionsByKey, false, 0);
        }

        Queue<long> queue = new();
        long startKey = ArchimedesPosKey.Pack(startPos);
        queue.Enqueue(startKey);
        visited.Add(startKey);
        BlockPos currentPos = new(0);
        BlockPos nextPos = new(0);
        int visitedManagedCount = 0;
        bool isTruncated = false;

        while (queue.Count > 0)
        {
            if (visitedManagedCount >= MaxBfsVisited)
            {
                api.Logger.Warning(
                    "{0} BFS in CollectConnectedManagedWater hit limit of {1} blocks starting at {2}",
                    ArchimedesScrewModSystem.LogPrefix,
                    MaxBfsVisited,
                    startPos
                );
                isTruncated = true;
                break;
            }

            long key = queue.Dequeue();
            ArchimedesPosKey.Unpack(key, currentPos);
            positionsByKey[key] = new BlockPos(currentPos.X, currentPos.Y, currentPos.Z);
            visitedManagedCount++;

            foreach (BlockFacing face in BlockFacing.ALLFACES)
            {
                int nextX = currentPos.X + face.Normali.X;
                int nextY = currentPos.Y + face.Normali.Y;
                int nextZ = currentPos.Z + face.Normali.Z;
                if (!ArchimedesPosKey.TryPack(nextX, nextY, nextZ, out long nextKey))
                {
                    continue;
                }

                if (visited.Contains(nextKey))
                {
                    continue;
                }

                nextPos.Set(nextX, nextY, nextZ);

                Block fluidBlock = api.World.BlockAccessor.GetBlock(nextPos, BlockLayersAccess.Fluid);
                if (!IsArchimedesWaterBlock(fluidBlock))
                {
                    continue;
                }

                if (!CanLiquidsTouch(currentPos, nextPos))
                {
                    // HCW aqueducts present a solid top/bottom face to the vanilla barrier API, but HCW itself
                    // propagates water vertically between stacked aqueducts. Trust HCW's routing: if either
                    // endpoint is an aqueduct and the destination already holds managed water, the BFS may cross.
                    Block fromSolidBlk = api.World.BlockAccessor.GetBlock(currentPos);
                    Block toSolidBlk = api.World.BlockAccessor.GetBlock(nextPos);
                    bool aqueductBoundary = ArchimedesAqueductDetector.IsHardcoreWaterAqueduct(fromSolidBlk) ||
                                            ArchimedesAqueductDetector.IsHardcoreWaterAqueduct(toSolidBlk);
                    if (!aqueductBoundary)
                    {
                        continue;
                    }
                }

                visited.Add(nextKey);
                queue.Enqueue(nextKey);
            }
        }

        ArchimedesPerf.AddCount("water.collectConnectedManaged.visited", visitedManagedCount);
        return new ConnectedManagedWaterResult(visited, positionsByKey, isTruncated, visitedManagedCount);
    }

    public HashSet<long> CollectConnectedManagedWater(BlockPos startPos, out Dictionary<long, BlockPos> positionsByKey)
    {
        ConnectedManagedWaterResult result = CollectConnectedManagedWaterDetailed(startPos);
        positionsByKey = result.PositionsByKey;
        return result.VisitedKeys;
    }

    /// <summary>
    /// Returns connected managed-water component for <paramref name="startPos"/> using a per-global-tick cache.
    /// Consumers must treat returned collections as read-only.
    /// </summary>
    public ConnectedManagedWaterResult CollectConnectedManagedWaterCachedDetailed(BlockPos startPos)
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("water.collectConnectedManagedCached");
        if (!isInGlobalWaterTickDispatch)
        {
            return CollectConnectedManagedWaterDetailed(startPos);
        }

        long startKey = ArchimedesPosKey.Pack(startPos);
        if (connectedManagedComponentCache.TryGetValue(startKey, out ConnectedManagedComponentCacheEntry? cached) &&
            cached.Generation == connectedManagedComponentCacheGeneration)
        {
            ArchimedesPerf.AddCount("water.collectConnectedManagedCached.hit");
            return new ConnectedManagedWaterResult(
                cached.Visited,
                cached.PositionsByKey,
                cached.IsTruncated,
                cached.VisitedManagedCount
            );
        }

        ConnectedManagedWaterResult result = CollectConnectedManagedWaterDetailed(startPos);
        ConnectedManagedComponentCacheEntry entry = new(
            connectedManagedComponentCacheGeneration,
            result.VisitedKeys,
            result.PositionsByKey,
            result.IsTruncated,
            result.VisitedManagedCount
        );
        // Component-level fanout: any position in this connected region should hit cache this tick.
        foreach (long key in result.VisitedKeys)
        {
            connectedManagedComponentCache[key] = entry;
        }
        ArchimedesPerf.AddCount("water.collectConnectedManagedCached.miss");
        ArchimedesPerf.AddCount("water.collectConnectedManagedCached.fanoutKeys", result.VisitedKeys.Count);
        return result;
    }

    public HashSet<long> CollectConnectedManagedWaterCached(BlockPos startPos, out Dictionary<long, BlockPos> positionsByKey)
    {
        ConnectedManagedWaterResult result = CollectConnectedManagedWaterCachedDetailed(startPos);
        positionsByKey = result.PositionsByKey;
        return result.VisitedKeys;
    }

    public HashSet<long> CollectConnectedArchimedesSources(BlockPos startPos, out Dictionary<long, BlockPos> sourcePositionsByKey)
    {
        sourcePositionsByKey = new Dictionary<long, BlockPos>();
        HashSet<long> connectedWater = CollectConnectedManagedWater(startPos, out Dictionary<long, BlockPos> waterPositions);
        foreach ((long key, BlockPos pos) in waterPositions)
        {
            Block fluidBlock = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
            if (!IsArchimedesSourceBlock(fluidBlock))
            {
                continue;
            }

            sourcePositionsByKey[key] = pos.Copy();
        }

        return new HashSet<long>(sourcePositionsByKey.Keys);
    }

    public bool EnsureSourceOwned(
        string ownerId,
        BlockPos pos,
        string familyId,
        BlockPos? sourcePos = null,
        BlockFacing? sourceFacing = null)
    {
        long key = ArchimedesPosKey.Pack(pos);
        if (IsDrainQuarantined(key))
        {
            return false;
        }

        Block existingFluid = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (IsVanillaSelfSustainingSourceForFamily(existingFluid, familyId) &&
            IsVanillaLocked(pos, familyId))
        {
            ArchimedesPerf.AddCount("water.claims.rejected.lockedVanilla");
            return false;
        }

        if (!CanControllerClaimInDomain(ownerId, pos, familyId))
        {
            return false;
        }

        Block solidBlock = api.World.BlockAccessor.GetBlock(pos);
        Block fluidBlock = existingFluid;
        bool solidClear = solidBlock.Id == 0 || solidBlock.ForFluidsLayer;
        bool directionalHostCompatible = ArchimedesFluidHostValidator.IsFluidHostCellCompatible(
            api.World,
            pos,
            sourcePos,
            sourceFacing
        );
        if (!solidClear && !directionalHostCompatible)
        {
            return false;
        }

        bool fluidClear = fluidBlock.Id == 0 ||
                          IsArchimedesWaterBlock(fluidBlock) ||
                          IsVanillaSourceBlock(fluidBlock);
        if (!fluidClear)
        {
            return false;
        }

        bool changed = false;
        managedAdoptionCooldownUntilMsByKey.Remove(key);
        if (!sourceOwnerByPos.TryGetValue(key, out string? existingOwner) ||
            !string.Equals(existingOwner, ownerId, StringComparison.Ordinal))
        {
            sourceOwnerByPos[key] = ownerId;
            changed = true;
        }

        AddOwnedPosToSnapshot(ownerId, pos);
        NotifyControllerSourceAssigned(ownerId, pos, "ensure source owned");

        Block currentFluid = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!IsArchimedesSourceBlock(currentFluid) ||
            !TryResolveManagedWaterFamily(currentFluid, out string currentFamilyId) ||
            !string.Equals(currentFamilyId, familyId, StringComparison.Ordinal))
        {
            SetManagedSource(pos, familyId);
            CaptureVanillaLocksAround(pos, familyId);
            changed = true;
        }

        SetSourceProvenance(pos, ManagedSourceProvenance.ControllerSeedOrRelay);

        return changed;
    }

    /// <summary>
    /// Intent-revealing wrapper for controller-driven source assignment.
    /// Preserves existing ownership semantics from <see cref="EnsureSourceOwned"/>.
    /// </summary>
    public bool AssignOwnedSourceForController(
        string controllerId,
        BlockPos pos,
        string familyId,
        BlockPos? sourcePos = null,
        BlockFacing? sourceFacing = null)
    {
        return EnsureSourceOwned(controllerId, pos, familyId, sourcePos, sourceFacing);
    }

    public IReadOnlyList<BlockPos> GetOwnedSourcePositionsForController(string controllerId)
    {
        List<BlockPos> result = new();
        foreach ((long key, string ownerId) in sourceOwnerByPos)
        {
            if (string.Equals(ownerId, controllerId, StringComparison.Ordinal))
            {
                result.Add(ArchimedesPosKey.UnpackToNew(key));
            }
        }

        return result;
    }

    public bool IsOwnedByController(string controllerId, BlockPos pos)
    {
        long key = ArchimedesPosKey.Pack(pos);
        return sourceOwnerByPos.TryGetValue(key, out string? ownerId) &&
               string.Equals(ownerId, controllerId, StringComparison.Ordinal);
    }

    public int GetOwnedCountForController(string controllerId)
    {
        int count = 0;
        foreach (string ownerId in sourceOwnerByPos.Values)
        {
            if (string.Equals(ownerId, controllerId, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    public bool IsRelayOwnedByController(string controllerId, BlockPos pos)
    {
        long key = ArchimedesPosKey.Pack(pos);
        return controllerRelaySourceKeys.TryGetValue(controllerId, out HashSet<long>? keys) &&
               keys.Contains(key);
    }

    public int GetRelayOwnedCountForController(string controllerId)
    {
        return controllerRelaySourceKeys.TryGetValue(controllerId, out HashSet<long>? keys) ? keys.Count : 0;
    }

    public IReadOnlyList<BlockPos> GetRelayOwnedPositionsForController(string controllerId)
    {
        if (!controllerRelaySourceKeys.TryGetValue(controllerId, out HashSet<long>? keys))
        {
            return Array.Empty<BlockPos>();
        }

        List<BlockPos> result = new(keys.Count);
        foreach (long key in keys)
        {
            result.Add(ArchimedesPosKey.UnpackToNew(key));
        }

        return result;
    }

    public bool AssignRelaySourceForController(string controllerId, BlockPos pos, string familyId)
    {
        if (!AssignOwnedSourceForController(controllerId, pos, familyId))
        {
            return false;
        }

        long key = ArchimedesPosKey.Pack(pos);
        if (!controllerRelaySourceKeys.TryGetValue(controllerId, out HashSet<long>? relayKeys))
        {
            relayKeys = new HashSet<long>();
            controllerRelaySourceKeys[controllerId] = relayKeys;
        }

        if (relayKeys.Add(key))
        {
            relayOwnerByPos[key] = controllerId;
        }
        return true;
    }

    public void ReleaseRelaySourceForController(string controllerId, BlockPos pos)
    {
        long key = ArchimedesPosKey.Pack(pos);
        if (controllerRelaySourceKeys.TryGetValue(controllerId, out HashSet<long>? relayKeys) &&
            relayKeys.Remove(key) &&
            relayOwnerByPos.TryGetValue(key, out string? currentOwner) &&
            string.Equals(currentOwner, controllerId, StringComparison.Ordinal))
        {
            relayOwnerByPos.Remove(key);
        }

        ReleaseOwnedSourceForController(controllerId, pos);
    }

    /// <summary>
    /// Replaces the entire relay snapshot for a controller (used by BE post-load hydration).
    /// Adds entries for new positions and removes entries for stale ones, syncing the reverse index.
    /// </summary>
    public void ReplaceRelaySnapshotForController(string controllerId, IReadOnlyCollection<BlockPos> relayPositions)
    {
        HashSet<long> newKeys = new();
        foreach (BlockPos pos in relayPositions)
        {
            newKeys.Add(ArchimedesPosKey.Pack(pos));
        }

        if (controllerRelaySourceKeys.TryGetValue(controllerId, out HashSet<long>? existingKeys))
        {
            foreach (long staleKey in existingKeys.Where(k => !newKeys.Contains(k)).ToList())
            {
                existingKeys.Remove(staleKey);
                if (relayOwnerByPos.TryGetValue(staleKey, out string? owner) &&
                    string.Equals(owner, controllerId, StringComparison.Ordinal))
                {
                    relayOwnerByPos.Remove(staleKey);
                }
            }
        }
        else
        {
            existingKeys = new HashSet<long>();
            controllerRelaySourceKeys[controllerId] = existingKeys;
        }

        foreach (long key in newKeys)
        {
            if (existingKeys.Add(key))
            {
                relayOwnerByPos[key] = controllerId;
            }
            else
            {
                relayOwnerByPos[key] = controllerId;
            }
        }
    }

    /// <summary>True if any controller has claimed <paramref name="pos"/> as a relay-owned source.</summary>
    public bool IsRelayOwnedPosition(BlockPos pos)
    {
        return relayOwnerByPos.ContainsKey(ArchimedesPosKey.Pack(pos));
    }

    /// <summary>Returns the controller id that owns <paramref name="pos"/> as a relay, or null.</summary>
    public bool TryGetRelayOwner(BlockPos pos, out string controllerId)
    {
        if (relayOwnerByPos.TryGetValue(ArchimedesPosKey.Pack(pos), out string? owner))
        {
            controllerId = owner;
            return true;
        }

        controllerId = string.Empty;
        return false;
    }

    public bool EnsureSourceOwnership(string ownerId, BlockPos pos)
    {
        Block fluidBlock = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!IsArchimedesSourceBlock(fluidBlock))
        {
            return false;
        }

        long key = ArchimedesPosKey.Pack(pos);
        if (sourceOwnerByPos.TryGetValue(key, out _))
        {
            return false;
        }

        sourceOwnerByPos[key] = ownerId;
        AddOwnedPosToSnapshot(ownerId, pos);
        return true;
    }

    public void ReleaseSourceOwner(string ownerId, BlockPos pos)
    {
        long key = ArchimedesPosKey.Pack(pos);
        if (!sourceOwnerByPos.TryGetValue(key, out string? owner))
        {
            RemoveOrphanedManagedSource(pos, key);
            return;
        }

        if (!string.Equals(owner, ownerId, StringComparison.Ordinal))
        {
            return;
        }

        sourceOwnerByPos.Remove(key);
        sourceProvenanceByPos.Remove(key);
        managedAdoptionCooldownUntilMsByKey[key] = Environment.TickCount64 + ManagedAdoptionCooldownAfterReleaseMs;
        if (controllerRelaySourceKeys.TryGetValue(owner, out HashSet<long>? ownerRelayKeys) &&
            ownerRelayKeys.Remove(key) &&
            relayOwnerByPos.TryGetValue(key, out string? currentRelayOwner) &&
            string.Equals(currentRelayOwner, owner, StringComparison.Ordinal))
        {
            relayOwnerByPos.Remove(key);
        }

        RemoveOwnedPosFromSnapshot(ownerId, pos);
        Block fluidBlock = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!IsArchimedesWaterBlock(fluidBlock))
        {
            return;
        }

        SuppressRemovalNotification(key);
        RemoveFluidAndNotifyNeighbours(pos);
    }

    /// <summary>
    /// Intent-revealing wrapper for controller-driven source release.
    /// Preserves existing release semantics from <see cref="ReleaseSourceOwner"/>.
    /// </summary>
    public void ReleaseOwnedSourceForController(string controllerId, BlockPos pos)
    {
        ReleaseSourceOwner(controllerId, pos);
    }

    public int CleanupUnownedManagedSourcesAroundAnchors(IReadOnlyCollection<BlockPos> anchors, int maxRemovals)
    {
        int budget = Math.Max(0, maxRemovals);
        if (budget == 0 || anchors.Count == 0)
        {
            return 0;
        }

        int removed = 0;
        HashSet<long> seenKeys = new();
        foreach (BlockPos anchor in anchors)
        {
            if (removed >= budget)
            {
                break;
            }

            CollectConnectedManagedWater(anchor, out Dictionary<long, BlockPos> connectedWater);
            foreach ((long key, BlockPos pos) in connectedWater)
            {
                if (removed >= budget || !seenKeys.Add(key))
                {
                    continue;
                }

                if (sourceOwnerByPos.ContainsKey(key))
                {
                    unownedCleanupCooldownUntilMsByKey.Remove(key);
                    continue;
                }

                long nowMs = Environment.TickCount64;
                if (unownedCleanupCooldownUntilMsByKey.TryGetValue(key, out long retryAtMs) && nowMs < retryAtMs)
                {
                    continue;
                }

                Block fluid = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
                if (!IsArchimedesSourceBlock(fluid))
                {
                    unownedCleanupCooldownUntilMsByKey.Remove(key);
                    continue;
                }

                // If this source is connected to active controller output, prefer ownership adoption
                // over destructive cleanup to avoid visible delete/recreate churn.
                if (TryResolveManagedWaterFamily(fluid, out string familyId) &&
                    AssignNearestActiveControllerForNewSource(
                        pos,
                        familyId,
                        reason: "cleanup-unowned adoption"))
                {
                    unownedCleanupCooldownUntilMsByKey.Remove(key);
                    continue;
                }

                SuppressRemovalNotification(key);
                RemoveFluidAndNotifyNeighbours(pos);
                unownedCleanupCooldownUntilMsByKey[key] = nowMs + UnownedCleanupRetryCooldownMs;
                removed++;
            }
        }

        if (removed > 0)
        {
            api.Logger.Warning(
                "{0} CleanupUnownedManagedSourcesAroundAnchors removed {1} unowned managed source(s) (anchors={2}, budget={3})",
                ArchimedesScrewModSystem.LogPrefix,
                removed,
                anchors.Count,
                budget
            );
        }

        return removed;
    }

    public int ConvertAdjacentVanillaSources(BlockPos startPos, string? ownerHintControllerId = null)
    {
        ConnectedManagedWaterResult connectedResult = CollectConnectedManagedWaterDetailed(startPos);
        if (connectedResult.IsTruncated)
        {
            LogTruncationPausedAutomation("ConvertAdjacentVanillaSources", startPos, connectedResult.VisitedManagedCount);
            return 0;
        }

        int converted = 0;
        HashSet<long> convertedKeys = new();
        Dictionary<long, BlockPos> connectedWater = connectedResult.PositionsByKey;
        BlockPos adjacentPos = new(0);
        foreach (BlockPos pos in connectedWater.Values)
        {
            Block currentFluid = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
            if (!TryResolveManagedWaterFamily(currentFluid, out string familyId))
            {
                continue;
            }

            foreach (BlockFacing face in BlockFacing.ALLFACES)
            {
                int ax = pos.X + face.Normali.X;
                int ay = pos.Y + face.Normali.Y;
                int az = pos.Z + face.Normali.Z;
                if (!ArchimedesPosKey.TryPack(ax, ay, az, out long adjacentKey))
                {
                    continue;
                }

                if (!convertedKeys.Add(adjacentKey))
                {
                    continue;
                }

                adjacentPos.Set(ax, ay, az);
                if (!CanLiquidsTouch(pos, adjacentPos))
                {
                    continue;
                }

                if (!TryConvertVanillaSource(adjacentPos, familyId, ownerHintControllerId))
                {
                    // A self-sustaining managed source (height 6/7) can appear via fluid simulation
                    // without passing through our vanilla-conversion path. Adopt ownership
                    // here so it does not remain unmanaged.
                    if (TryAdoptManagedSelfSustainingSource(adjacentPos, familyId, ownerHintControllerId))
                    {
                        converted++;
                    }
                    continue;
                }

                converted++;
            }
        }

        return converted;
    }

    /// <summary>
    /// Repeats <see cref="ConvertAdjacentVanillaSources"/> until no vanilla sources remain adjacent to the
    /// growing managed-water component, or <paramref name="maxPasses"/> is reached. A single pass only converts
    /// neighbours of the current BFS set, so a chain of bucket-placed sources needs multiple passes.
    /// </summary>
    public int ConvertAdjacentVanillaSourcesIteratively(BlockPos startPos, int maxPasses, string? ownerHintControllerId = null)
    {
        int capped = Math.Clamp(maxPasses, 1, 256);
        int total = 0;
        for (int pass = 0; pass < capped; pass++)
        {
            int batch = ConvertAdjacentVanillaSources(startPos, ownerHintControllerId);
            if (batch == 0)
            {
                break;
            }

            total += batch;
        }

        return total;
    }

    /// <summary>
    /// BFS over the connected fluid component (vanilla or managed) for <paramref name="familyId"/>, starting from
    /// probe origins and their face neighbors. Claims vanilla height-7 sources via the player path, and assigns
    /// <paramref name="ownerHintControllerId"/> to <b>unowned</b> managed self-sustaining sources (fluid sim / partial
    /// conversion can leave <c>archimedes-water-*</c> still blocks that <see cref="TryConvertVanillaSourceForPlayer"/>
    /// skips as "not vanilla").
    /// </summary>
    public int SeizeVanillaSourcesInConnectedFamilyFluid(
        IReadOnlyList<BlockPos> probeOrigins,
        string familyId,
        string? ownerHintControllerId,
        bool adoptManagedSelfSustaining = true,
        int maxVisit = MaxBfsVisited)
    {
        if (probeOrigins == null || probeOrigins.Count == 0)
        {
            return 0;
        }

        IBlockAccessor ba = api.World.BlockAccessor;
        HashSet<long> visited = new();
        Queue<(long Key, int VanillaDepth)> queue = new();
        BlockPos fromScratch = new(0);
        BlockPos posScratch = new(0);
        int haloDepth = Math.Max(0, config.Water.VanillaClaimHaloDepth);

        void TryEnqueue(int x, int y, int z, int vanillaDepth, long? fromKey = null)
        {
            if (!ArchimedesPosKey.TryPack(x, y, z, out long key))
            {
                return;
            }

            ArchimedesPosKey.Unpack(key, posScratch);
            if (fromKey != null)
            {
                ArchimedesPosKey.Unpack(fromKey.Value, fromScratch);
                if (!CanLiquidsTouch(fromScratch, posScratch))
                {
                    return;
                }
            }

            if (visited.Contains(key))
            {
                return;
            }

            Block fluid = ba.GetBlock(posScratch, BlockLayersAccess.Fluid);
            if (!IsFamilyLiquidBlock(fluid, familyId))
            {
                return;
            }

            bool isVanilla = TryResolveVanillaWaterFamily(fluid, out string vanillaId) &&
                             string.Equals(vanillaId, familyId, StringComparison.Ordinal);
            int nextDepth = isVanilla ? vanillaDepth : 0;
            if (isVanilla && nextDepth > haloDepth)
            {
                return;
            }

            visited.Add(key);
            queue.Enqueue((key, nextDepth));
        }

        foreach (BlockPos origin in probeOrigins)
        {
            if (!ArchimedesPosKey.TryPack(origin.X, origin.Y, origin.Z, out long originKey))
            {
                continue;
            }

            TryEnqueue(origin.X, origin.Y, origin.Z, 0);
            foreach (BlockFacing face in BlockFacing.ALLFACES)
            {
                TryEnqueue(
                    origin.X + face.Normali.X,
                    origin.Y + face.Normali.Y,
                    origin.Z + face.Normali.Z,
                    0,
                    originKey
                );
            }
        }

        if (queue.Count == 0)
        {
            return 0;
        }

        int seized = 0;
        int dequeued = 0;
        while (queue.Count > 0 && dequeued < maxVisit)
        {
            (long pkey, int vanillaDepth) = queue.Dequeue();
            ArchimedesPosKey.Unpack(pkey, posScratch);
            dequeued++;

            Block fluid = ba.GetBlock(posScratch, BlockLayersAccess.Fluid);
            bool isUnownedManagedSelfSustaining =
                IsManagedSelfSustainingSourceForFamily(fluid, familyId) &&
                !sourceOwnerByPos.ContainsKey(pkey);
            int lockedNeighbors = CountLockedVanillaNeighbors(posScratch, familyId);

            if (isUnownedManagedSelfSustaining &&
                (IsVanillaLocked(posScratch, familyId) || lockedNeighbors > 0))
            {
                if (TryGetVanillaEquivalent(fluid, out Block vanillaEquivalent))
                {
                    api.World.BlockAccessor.SetBlock(vanillaEquivalent.Id, posScratch, BlockLayersAccess.Fluid);
                    TriggerLiquidUpdates(posScratch, vanillaEquivalent);
                    CaptureVanillaLocksAround(posScratch, familyId);
                }

                continue;
            }

            bool claimed = false;
            if (IsVanillaSelfSustainingSourceForFamily(fluid, familyId) &&
                TryConvertVanillaSourceForPlayer(posScratch, familyId, "connected-family-fluid sweep (drain context)"))
            {
                claimed = true;
            }
            else if (adoptManagedSelfSustaining &&
                     !string.IsNullOrWhiteSpace(ownerHintControllerId) &&
                     isUnownedManagedSelfSustaining)
            {
                if (IsManagedAdoptionCoolingDown(pkey, out _))
                {
                }
                else
                {
                    // Unowned archimedes still blocks (not vanilla water-*) need ownership so drain can release them.
                    if (EnsureSourceOwned(ownerHintControllerId, posScratch, familyId))
                    {
                        claimed = true;
                    }
                }
            }

            if (claimed)
            {
                seized++;
            }

            foreach (BlockFacing face in BlockFacing.ALLFACES)
            {
                TryEnqueue(
                    posScratch.X + face.Normali.X,
                    posScratch.Y + face.Normali.Y,
                    posScratch.Z + face.Normali.Z,
                    vanillaDepth + 1,
                    pkey
                );
            }
        }

        return seized;
    }

    private bool IsFamilyLiquidBlock(Block block, string familyId)
    {
        return (TryResolveManagedWaterFamily(block, out string managedId) &&
                string.Equals(managedId, familyId, StringComparison.Ordinal)) ||
               (TryResolveVanillaWaterFamily(block, out string vanillaId) &&
                string.Equals(vanillaId, familyId, StringComparison.Ordinal));
    }

    public bool TryConvertVanillaSource(BlockPos pos, string familyId, string? ownerHintControllerId = null)
    {
        return TryClaimVanillaSourceWithPolicy(
            pos,
            familyId,
            ownerHintControllerId,
            reason: "controller-converted source",
            playerIntent: false
        );
    }

    /// <summary>
    /// Player-placement path: convert to managed source and assign ownership before neighbour updates
    /// can immediately turn the cell into flowing water.
    /// </summary>
    public bool TryConvertVanillaSourceForPlayer(BlockPos pos, string familyId, string reason)
    {
        return TryClaimVanillaSourceWithPolicy(
            pos,
            familyId,
            ownerHintControllerId: null,
            reason,
            playerIntent: true
        );
    }

    public bool TryConvertVanillaSourceUsingAdjacentManagedFamilyForPlayer(BlockPos pos, string reason)
    {
        Block fluidBlock = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!TryResolveVanillaWaterFamily(fluidBlock, out _))
        {
            return false;
        }

        foreach (BlockFacing face in BlockFacing.ALLFACES)
        {
            BlockPos neighbourPos = pos.AddCopy(face);
            if (!CanLiquidsTouch(pos, neighbourPos))
            {
                continue;
            }

            Block neighbourFluid = api.World.BlockAccessor.GetBlock(neighbourPos, BlockLayersAccess.Fluid);
            if (!TryResolveManagedWaterFamily(neighbourFluid, out string familyId))
            {
                continue;
            }

            return TryConvertVanillaSourceForPlayer(pos, familyId, reason);
        }

        return false;
    }

    public int AssignConnectedSourceToActiveControllers(BlockPos pos, string reason)
    {
        long key = ArchimedesPosKey.Pack(pos);
        Block fluidBlock = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!IsArchimedesSourceBlock(fluidBlock))
        {
            return 0;
        }

        if (!TryResolveManagedWaterFamily(fluidBlock, out string familyId))
        {
            return 0;
        }

        return AssignNearestActiveControllerForNewSource(pos, familyId, reason) ? 1 : 0;
    }

    public void OnManagedWaterRemoved(BlockPos pos)
    {
        long key = ArchimedesPosKey.Pack(pos);
        if (suppressedRemovalNotifications.TryRemove(key, out _))
        {
            return;
        }

        if (!sourceOwnerByPos.Remove(key, out string? ownerId))
        {
            sourceProvenanceByPos.Remove(key);
            return;
        }

        sourceProvenanceByPos.Remove(key);
        RemoveOwnedPosFromSnapshot(ownerId, pos);
        if (controllerRelaySourceKeys.TryGetValue(ownerId, out HashSet<long>? removedRelayKeys) &&
            removedRelayKeys.Remove(key) &&
            relayOwnerByPos.TryGetValue(key, out string? currentRemovedOwner) &&
            string.Equals(currentRemovedOwner, ownerId, StringComparison.Ordinal))
        {
            relayOwnerByPos.Remove(key);
        }

        if (loadedControllers.TryGetValue(ownerId, out WeakReference<BlockEntityWaterArchimedesScrew>? reference) &&
            reference.TryGetTarget(out BlockEntityWaterArchimedesScrew? controller))
        {
            controller.NotifyManagedWaterRemoved(pos);
        }
    }

    public Block GetManagedBlock(string familyId, string flow, int height)
    {
        string cacheKey = $"{familyId}:{flow}:{height}";
        if (managedBlockCache.TryGetValue(cacheKey, out Block? cached))
        {
            return cached;
        }

        AssetLocation code = ArchimedesWaterFamilies.GetManagedBlockCode(familyId, flow, height);
        Block? block = api.World.GetBlock(code);
        if (block == null)
        {
            throw new InvalidOperationException($"Managed Archimedes water block could not be resolved for {code}.");
        }

        managedBlockCache[cacheKey] = block;
        return block;
    }

    public void SetManagedSource(BlockPos pos, string familyId)
    {
        SetManagedWaterVariant(pos, familyId, "still", 7);
    }

    public void SetManagedWaterVariant(BlockPos pos, string familyId, string flow, int height, bool triggerUpdates = true)
    {
        Block desiredBlock = GetManagedBlock(familyId, flow, height);
        Block currentFluid = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (currentFluid.Id == desiredBlock.Id)
        {
            return;
        }

        if (IsArchimedesWaterBlock(currentFluid))
        {
            SuppressRemovalNotification(ArchimedesPosKey.Pack(pos));
        }

        bool transitionedFromNonLiquid = currentFluid.Id == 0 || !currentFluid.IsLiquid();
        api.World.BlockAccessor.SetBlock(desiredBlock.Id, pos, BlockLayersAccess.Fluid);
        if (transitionedFromNonLiquid)
        {
            CaptureVanillaLocksAround(pos, familyId);
        }
        if (triggerUpdates)
        {
            TriggerLiquidUpdates(pos, desiredBlock);
        }
    }

    public static string PosKey(BlockPos pos)
    {
        return $"{pos.X},{pos.Y},{pos.Z}";
    }

    /// <summary>
    /// Inverse of <see cref="PosKey"/>: exactly three comma-separated integers (no extra segments).
    /// </summary>
    public static bool TryParsePosKey(string? key, out BlockPos pos)
    {
        pos = new BlockPos(0, 0, 0);
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        string[] parts = key.Split(',');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out int x) ||
            !int.TryParse(parts[1], out int y) ||
            !int.TryParse(parts[2], out int z))
        {
            return false;
        }

        pos = new BlockPos(x, y, z);
        return true;
    }

    private static void AddInvalidPosKeySample(List<string> samples, string? key)
    {
        if (samples.Count >= 3)
        {
            return;
        }

        if (key == null)
        {
            samples.Add("(null)");
            return;
        }

        const int maxLen = 80;
        samples.Add(key.Length <= maxLen ? key : key.Substring(0, maxLen) + "...");
    }

    private void LogSkippedInvalidPosKeys(string operation, int skipped, List<string> samples)
    {
        if (skipped <= 0)
        {
            return;
        }

        string sampleText = samples.Count > 0 ? string.Join(", ", samples) : "—";
        api.Logger.Warning(
            "{0} {1}: skipped {2} invalid position key(s). Sample(s): {3}",
            ArchimedesScrewModSystem.LogPrefix,
            operation,
            skipped,
            sampleText);
    }

    private bool IsVanillaSourceBlock(Block block)
    {
        return block.IsLiquid() &&
               ArchimedesWaterFamilies.TryResolveVanillaFamily(block, out _) &&
               IsSourceHeight(block.Variant?["height"]);
    }

    private bool IsVanillaSelfSustainingSourceForFamily(Block block, string familyId)
    {
        if (!block.IsLiquid() ||
            !TryResolveVanillaWaterFamily(block, out string vanillaFamilyId) ||
            !string.Equals(vanillaFamilyId, familyId, StringComparison.Ordinal))
        {
            return false;
        }

        return IsSourceHeight(block.Variant?["height"]);
    }

    private bool IsArchimedesSelfSustainingSourceBlock(Block block)
    {
        return IsArchimedesWaterBlock(block) &&
               IsSourceHeight(block.Variant?["height"]);
    }

    private static bool IsSourceHeight(string? heightText)
    {
        // Temporary behavior: only full-height cells are treated as sources.
        return string.Equals(heightText, "7", StringComparison.Ordinal);
    }

    private bool IsManagedSelfSustainingSourceForFamily(Block block, string familyId)
    {
        return IsArchimedesSelfSustainingSourceBlock(block) &&
               TryResolveManagedWaterFamily(block, out string managedFamilyId) &&
               string.Equals(managedFamilyId, familyId, StringComparison.Ordinal);
    }

    private bool CanLiquidsTouch(BlockPos fromPos, BlockPos toPos)
    {
        return ArchimedesFluidHostValidator.CanLiquidsTouchByBarrier(api.World, fromPos, toPos);
    }

    private bool HasAtLeastTwoOwnedManagedCardinalSourceNeighbors(BlockPos pos, string familyId)
    {
        int ownedMatches = 0;
        BlockPos adjacentPos = new(0);
        foreach (BlockFacing face in BlockFacing.HORIZONTALS)
        {
            int ax = pos.X + face.Normali.X;
            int ay = pos.Y + face.Normali.Y;
            int az = pos.Z + face.Normali.Z;
            if (!ArchimedesPosKey.TryPack(ax, ay, az, out long adjacentKey))
            {
                continue;
            }

            adjacentPos.Set(ax, ay, az);
            if (!CanLiquidsTouch(pos, adjacentPos))
            {
                continue;
            }

            Block adjacentFluid = api.World.BlockAccessor.GetBlock(adjacentPos, BlockLayersAccess.Fluid);
            if (!IsArchimedesSelfSustainingSourceBlock(adjacentFluid) ||
                !TryResolveManagedWaterFamily(adjacentFluid, out string managedFamilyId) ||
                !string.Equals(managedFamilyId, familyId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!sourceOwnerByPos.ContainsKey(adjacentKey))
            {
                continue;
            }

            ownedMatches++;
            if (ownedMatches >= 2)
            {
                return true;
            }
        }

        return false;
    }

    private bool TryAdoptManagedSelfSustainingSource(BlockPos pos, string familyId, string? ownerHintControllerId)
    {
        long key = ArchimedesPosKey.Pack(pos);
        Block fluidBlock = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!IsManagedSelfSustainingSourceForFamily(fluidBlock, familyId) ||
            sourceOwnerByPos.ContainsKey(key) ||
            !HasAtLeastTwoOwnedManagedCardinalSourceNeighbors(pos, familyId))
        {
            return false;
        }

        if (IsManagedAdoptionCoolingDown(key, out _))
        {
            return false;
        }

        bool assigned = false;
        if (!string.IsNullOrWhiteSpace(ownerHintControllerId))
        {
            assigned = EnsureSourceOwned(ownerHintControllerId, pos, familyId);
        }

        if (!assigned)
        {
            assigned = AssignNearestActiveControllerForNewSource(
                pos,
                familyId,
                reason: "managed-self-sustaining ownership adoption"
            );
        }

        return assigned;
    }

    private bool IsManagedAdoptionCoolingDown(long key, out long cooldownRemainingMs)
    {
        cooldownRemainingMs = 0;
        if (!managedAdoptionCooldownUntilMsByKey.TryGetValue(key, out long untilMs))
        {
            return false;
        }

        long nowMs = Environment.TickCount64;
        if (untilMs <= nowMs)
        {
            managedAdoptionCooldownUntilMsByKey.Remove(key);
            return false;
        }

        cooldownRemainingMs = untilMs - nowMs;
        return true;
    }

    private void RemoveOrphanedManagedSource(BlockPos pos, long key)
    {
        Block fluidBlock = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!IsArchimedesSourceBlock(fluidBlock))
        {
            return;
        }

        SuppressRemovalNotification(key);
        RemoveFluidAndNotifyNeighbours(pos);
    }

    private T? LoadSerialized<T>(string key)
    {
        byte[] data = api.WorldManager.SaveGame.GetData(key);
        return data == null ? default : SerializerUtil.Deserialize<T>(data);
    }

    private bool AssignNearestActiveControllerForNewSource(BlockPos sourcePos, string familyId, string reason = "new source assignment")
    {
        if (IsDrainQuarantined(sourcePos))
        {
            return false;
        }

        ConnectedManagedWaterResult connectedResult = CollectConnectedManagedWaterCachedDetailed(sourcePos);
        if (connectedResult.IsTruncated)
        {
            LogTruncationPausedAutomation("AssignNearestActiveControllerForNewSource", sourcePos, connectedResult.VisitedManagedCount);
            return false;
        }

        Dictionary<long, BlockPos> connectedWater = connectedResult.PositionsByKey;
        string? nearest = FindNearestActiveControllerId(
            sourcePos,
            connectedWater.Keys,
            familyId,
            excludedControllerId: null,
            requireConnected: true
        );
        if (nearest == null)
        {
            return false;
        }

        long key = ArchimedesPosKey.Pack(sourcePos);
        sourceOwnerByPos[key] = nearest;
        AddOwnedPosToSnapshot(nearest, sourcePos);
        NotifyControllerSourceAssigned(nearest, sourcePos, reason);
        return true;
    }

    private string? FindNearestActiveControllerId(
        BlockPos sourcePos,
        IEnumerable<long> connectedWaterKeys,
        string familyId,
        string? excludedControllerId,
        bool requireConnected
    )
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("water.findNearestActiveController");
        HashSet<long>? connected = requireConnected
            ? new HashSet<long>(connectedWaterKeys)
            : null;

        List<ArchimedesOutletState> candidates = new();
        foreach (ArchimedesOutletState seed in GetActiveSeedStatesCached())
        {
            if (excludedControllerId != null &&
                string.Equals(seed.ControllerId, excludedControllerId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(seed.FamilyId, familyId, StringComparison.Ordinal))
            {
                continue;
            }

            if (requireConnected)
            {
                if (connected == null || connected.Count == 0)
                {
                    continue;
                }

                // Source component has already been collected by the caller.
                // If a candidate seed's position is part of that same component, it is connected.
                if (!connected.Contains(ArchimedesPosKey.Pack(seed.SeedPos)))
                {
                    continue;
                }
            }

            candidates.Add(seed);
        }

        string? resolved = candidates
            .OrderBy(seed => ArchimedesPositionCodec.DistanceSquared(sourcePos, seed.SeedPos))
            .ThenBy(seed => seed.SeedPos.Y)
            .ThenBy(seed => seed.SeedPos.X)
            .ThenBy(seed => seed.SeedPos.Z)
            .ThenBy(seed => seed.ControllerId, StringComparer.Ordinal)
            .Select(seed => seed.ControllerId)
            .FirstOrDefault();
        ArchimedesPerf.AddCount("water.findNearestActiveController.candidates", candidates.Count);
        return resolved;
    }

    private void NotifyControllerSourceAssigned(string controllerId, BlockPos sourcePos, string reason)
    {
        if (loadedControllers.TryGetValue(controllerId, out WeakReference<BlockEntityWaterArchimedesScrew>? reference) &&
            reference.TryGetTarget(out BlockEntityWaterArchimedesScrew? controller))
        {
            controller.TrackAssignedSourceFromManager(sourcePos, reason);
        }
    }

    private void AddOwnedPosToSnapshot(string controllerId, BlockPos pos)
    {
        HashSet<long> keys = new();
        List<BlockPos> list = new();
        if (controllerOwnedById.TryGetValue(controllerId, out int[]? existing))
        {
            foreach (BlockPos ex in ArchimedesPositionCodec.DecodePositions(existing))
            {
                if (keys.Add(ArchimedesPosKey.Pack(ex)))
                {
                    list.Add(ex);
                }
            }
        }

        if (keys.Add(ArchimedesPosKey.Pack(pos)))
        {
            list.Add(pos.Copy());
        }

        controllerOwnedById[controllerId] = ArchimedesPositionCodec.EncodePositions(list);
    }

    private void RemoveOwnedPosFromSnapshot(string controllerId, BlockPos pos)
    {
        if (!controllerOwnedById.TryGetValue(controllerId, out int[]? existing))
        {
            return;
        }

        long removeKey = ArchimedesPosKey.Pack(pos);
        List<BlockPos> kept = ArchimedesPositionCodec.DecodePositions(existing)
            .Where(p => ArchimedesPosKey.Pack(p) != removeKey)
            .Select(p => p.Copy())
            .ToList();
        controllerOwnedById[controllerId] = ArchimedesPositionCodec.EncodePositions(kept);
    }

    private void SuppressRemovalNotification(long key)
    {
        suppressedRemovalNotifications[key] = 1;
    }

    private void RemoveFluidAndNotifyNeighbours(BlockPos pos)
    {
        api.World.BlockAccessor.SetBlock(0, pos, BlockLayersAccess.Fluid);
        NotifyNeighboursOfFluidRemoval(pos);
    }

    private void NotifyNeighboursOfFluidRemoval(BlockPos pos)
    {
        api.World.BlockAccessor.TriggerNeighbourBlockUpdate(pos);
        api.World.BlockAccessor.MarkBlockDirty(pos);

        BlockPos neighbourPos = new(0);
        foreach (BlockFacing face in BlockFacing.ALLFACES)
        {
            neighbourPos.Set(pos.X + face.Normali.X, pos.Y + face.Normali.Y, pos.Z + face.Normali.Z);

            Block neighbourSolid = api.World.BlockAccessor.GetBlock(neighbourPos);
            if (neighbourSolid.Id != 0)
            {
                neighbourSolid.OnNeighbourBlockChange(api.World, neighbourPos, pos);
            }

            Block neighbourFluid = api.World.BlockAccessor.GetBlock(neighbourPos, BlockLayersAccess.Fluid);
            if (neighbourFluid.Id != 0)
            {
                neighbourFluid.OnNeighbourBlockChange(api.World, neighbourPos, pos);
            }
        }
    }

    private void TriggerLiquidUpdates(BlockPos pos, Block placedFluid)
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("water.triggerLiquidUpdates");
        api.World.BlockAccessor.TriggerNeighbourBlockUpdate(pos);
        api.World.BlockAccessor.MarkBlockDirty(pos);

        placedFluid.OnNeighbourBlockChange(api.World, pos, pos);

        BlockPos neighbourPos = new(0);
        foreach (BlockFacing face in BlockFacing.ALLFACES)
        {
            neighbourPos.Set(pos.X + face.Normali.X, pos.Y + face.Normali.Y, pos.Z + face.Normali.Z);

            Block neighbourSolid = api.World.BlockAccessor.GetBlock(neighbourPos);
            if (neighbourSolid.Id != 0)
            {
                neighbourSolid.OnNeighbourBlockChange(api.World, neighbourPos, pos);
            }

            Block neighbourFluid = api.World.BlockAccessor.GetBlock(neighbourPos, BlockLayersAccess.Fluid);
            if (neighbourFluid.Id != 0)
            {
                neighbourFluid.OnNeighbourBlockChange(api.World, neighbourPos, pos);
            }
        }
    }

    private bool TryGetVanillaEquivalent(Block managedBlock, out Block vanillaBlock)
    {
        vanillaBlock = null!;
        if (!TryResolveManagedWaterFamily(managedBlock, out string familyId))
        {
            return false;
        }

        ArchimedesWaterFamily family = ArchimedesWaterFamilies.GetById(familyId);
        string flow = managedBlock.Variant?["flow"] ?? "still";
        string heightText = managedBlock.Variant?["height"] ?? "7";
        if (!int.TryParse(heightText, out int height))
        {
            return false;
        }

        Block? resolved = api.World.GetBlock(new AssetLocation("game", $"{family.VanillaCode}-{flow}-{height}"));
        if (resolved == null)
        {
            return false;
        }

        vanillaBlock = resolved;
        return true;
    }

    private void LogTruncationPausedAutomation(string operation, BlockPos startPos, int visitedManagedCount)
    {
        long now = Environment.TickCount64;
        if (now - lastTruncationPauseLogAtMs < 5000)
        {
            return;
        }

        lastTruncationPauseLogAtMs = now;
        api.Logger.Warning(
            "{0} {1} paused: connected managed component truncated near {2} (visitedManaged={3}, cap={4})",
            ArchimedesScrewModSystem.LogPrefix,
            operation,
            startPos,
            visitedManagedCount,
            MaxBfsVisited
        );
    }

    private sealed record ConnectedManagedComponentCacheEntry(
        int Generation,
        HashSet<long> Visited,
        Dictionary<long, BlockPos> PositionsByKey,
        bool IsTruncated,
        int VisitedManagedCount
    );

    private bool ControllerSnapshotContainsPos(string controllerId, long key)
    {
        if (!controllerOwnedById.TryGetValue(controllerId, out int[]? encoded))
        {
            return false;
        }

        foreach (BlockPos pos in ArchimedesPositionCodec.DecodePositions(encoded))
        {
            if (ArchimedesPosKey.Pack(pos) == key)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsLoadedControllerTrackingPos(string controllerId, BlockPos pos)
    {
        if (!loadedControllers.TryGetValue(controllerId, out WeakReference<BlockEntityWaterArchimedesScrew>? wr) ||
            !wr.TryGetTarget(out BlockEntityWaterArchimedesScrew? controller))
        {
            return false;
        }

        return controller.IsTrackingSource(pos);
    }

}

public readonly record struct ManagedSourceDebugInfo(
    BlockPos Pos,
    bool IsOwned,
    string OwnerId,
    bool IsOwnershipConsistent,
    bool OwnerSnapshotContainsPos,
    bool OwnerControllerLoaded,
    bool OwnerLoadedControllerTracksPos,
    bool IsHeight7Source
);

public readonly record struct ConnectedManagedWaterResult(
    HashSet<long> VisitedKeys,
    Dictionary<long, BlockPos> PositionsByKey,
    bool IsTruncated,
    int VisitedManagedCount
);
