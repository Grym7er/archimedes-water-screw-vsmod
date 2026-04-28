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

    private const int MaxBfsVisited = 16384;
    private const int UnownedCleanupRetryCooldownMs = 3000;
    private const int DrainQuarantineMs = 1500;
    private const int DrainQuarantinePruneIntervalMs = 1000;
    /// <summary>
    /// Soft cap on the number of entries in <see cref="drainQuarantineUntilMsByKey"/>. When
    /// <see cref="MarkDrainQuarantine"/> is called at or above this threshold for a key not already
    /// tracked, a forced prune runs (bypassing <see cref="DrainQuarantinePruneIntervalMs"/>); if the
    /// map is still saturated, the entry whose <c>until</c> is smallest (soonest-to-expire) is
    /// evicted before insertion. Aqueduct entries (with the <see cref="AqueductDrainQuarantineMultiplier"/>
    /// applied) tend to survive eviction naturally because their <c>until</c> values are larger.
    /// </summary>
    private const int DrainQuarantineSoftCap = 8192;
    private const int HardGlobalTickBudgetMs = 10;
    private const bool AggressiveDrainEnabled = true;
    private const bool DrainQueueCoalesceEnabled = true;
    private const bool DeferredNeighborUpdatesEnabled = true;
    private const int DeferredNeighborUpdateIntervalMs = 200;
    private const int DeferredNeighborFanoutPerGlobalTick = 32;
    private const int DrainQueueMaxProcessMsPerGlobalTick = 2;
    private const int LargeComponentSamplingThreshold = 2048;
    private const int DrainConnectivitySampleStride = 4;

    /// <summary>
    /// Multiplier applied to <see cref="DrainQuarantineMs"/> for cells inside HardcoreWater aqueducts,
    /// to absorb the HCW refill cycle without triggering re-promotion churn.
    /// TODO: expose via config if players need to tune this.
    /// </summary>
    private const int AqueductDrainQuarantineMultiplier = 2;

    private readonly ICoreServerAPI api;
    private readonly ArchimedesScrewConfig config;

    private readonly Dictionary<long, string> sourceOwnerByPos = new();
    /// <summary>
    /// Reverse index of <see cref="sourceOwnerByPos"/>: controllerId -> set of owned packed pos keys.
    /// In-memory only; the on-disk save format is <see cref="controllerOwnedById"/>, which is rebuilt
    /// from this map at <see cref="Save"/> time.
    /// Every mutation of <see cref="sourceOwnerByPos"/> must keep this index in sync (use
    /// <see cref="AssignSourceOwnerInternal"/>/<see cref="ClearSourceOwnerInternal"/>).
    /// </summary>
    private readonly Dictionary<string, HashSet<long>> ownedKeysByController = new(StringComparer.Ordinal);
    /// <summary>Reusable scratch list for <c>ReplaceSourceOwnershipForController</c> stale-key collection.</summary>
    private readonly List<long> replaceOwnershipStaleScratch = new();
    private readonly Dictionary<long, long> unownedCleanupCooldownUntilMsByKey = new();
    private readonly Dictionary<long, long> drainQuarantineUntilMsByKey = new();
    /// <summary>Reusable scratch list used by <see cref="PruneExpiredDrainQuarantine"/> to avoid
    /// allocating a new <see cref="List{T}"/> every prune.</summary>
    private readonly List<long> drainQuarantinePruneScratch = new();
    /// <summary>Throttling timestamp for <see cref="PruneExpiredDrainQuarantine"/>: the next wall
    /// time (ms) at which a prune is allowed to run. Set to 0 to force an immediate prune on the
    /// next call.</summary>
    private long nextDrainQuarantinePruneAtMs;
    private readonly HashSet<long> screwBlockKeys = new();
    private readonly Dictionary<string, long> controllerPosById = new(StringComparer.Ordinal);
    /// <summary>
    /// Persistence-only cache of owned source positions per controller. Rebuilt from
    /// <see cref="ownedKeysByController"/> at <see cref="Save"/> time and at explicit BE-driven
    /// snapshot updates (<see cref="ReplaceSourceOwnershipForController"/>). Do not read for
    /// runtime ownership decisions; consult <see cref="ownedKeysByController"/> instead.
    /// </summary>
    private readonly Dictionary<string, int[]> controllerOwnedById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WeakReference<BlockEntityWaterArchimedesScrew>> loadedControllers = new(StringComparer.Ordinal);
    /// <summary>
    /// Set of pos keys whose next <see cref="OnManagedWaterRemoved"/> call should be ignored.
    /// All access sites (<see cref="SuppressRemovalNotification"/> writers and the
    /// <see cref="OnManagedWaterRemoved"/> reader) run on the server's main tick thread, so a
    /// plain <see cref="HashSet{T}"/> suffices and avoids the per-op locking overhead of a
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/>.
    /// </summary>
    private readonly HashSet<long> suppressedRemovalNotifications = new();
    private readonly Dictionary<string, Block> managedBlockCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<long>> controllerRelaySourceKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Block-id sets for hot-path classification. Avoids per-call <c>block.Code?.Domain</c> string
    /// compares plus <c>block.Variant?["height"]</c> dictionary lookups inside neighbour loops.
    /// Lazily populated on first hot-path call via <see cref="EnsureManagedBlockIdSetsInitialized"/>.
    /// </summary>
    private readonly HashSet<int> managedSourceHeightBlockIds = new();
    private readonly HashSet<int> relayFlowCandidateBlockIds = new();
    private readonly HashSet<int> managedFlowOrSourceHeightBlockIds = new();
    private bool managedBlockIdSetsInitialized;

    /// <summary>Reusable scratch for <see cref="ReplaceRelaySnapshotForController"/>.</summary>
    private readonly List<long> replaceRelayStaleScratch = new();

    /// <summary>
    /// Reverse index of <see cref="controllerRelaySourceKeys"/>: packed pos key -> owning controllerId.
    /// Authoritative source of truth for "is this cell relay-owned" queries; survives BE chunk unload.
    /// </summary>
    private readonly Dictionary<long, string> relayOwnerByPos = new();

    private readonly Dictionary<string, WeakReference<BlockEntityWaterArchimedesScrew>> centralWaterTickControllers = new(StringComparer.Ordinal);
    /// <summary>
    /// Min-heap keyed on each controller's <c>nextCentralWaterTickDueMs</c>. The dispatch loop
    /// pops the soonest-due controller first instead of cursor-stepping, which prevents tail-of-list
    /// starvation when <see cref="HardGlobalTickBudgetMs"/> cuts a tick short. Stale entries (left
    /// behind when a controller reschedules itself outside the dispatch path) are detected and
    /// reconciled lazily via <see cref="centralWaterTickEnqueuedPriorityById"/>.
    /// </summary>
    private readonly PriorityQueue<string, long> centralWaterTickQueue = new();
    /// <summary>
    /// Tracks the priority each id was last enqueued under, so the dispatcher can spot stale heap
    /// entries (lazy decrease-key pattern). Authoritative when a controller's
    /// <c>nextCentralWaterTickDueMs</c> moves between enqueue and dequeue.
    /// </summary>
    private readonly Dictionary<string, long> centralWaterTickEnqueuedPriorityById = new(StringComparer.Ordinal);
    private readonly HashSet<string> centralWaterTickSet = new(StringComparer.Ordinal);
    private readonly Dictionary<long, IncrementalDrainState> incrementalDrainStateByKey = new();
    private readonly Queue<long> incrementalDrainQueue = new();
    private readonly HashSet<long> incrementalDrainQueuedKeys = new();
    private readonly Queue<long> deferredNeighborFanoutQueue = new();
    private readonly HashSet<long> deferredNeighborFanoutQueuedKeys = new();
    private readonly BlockPos incrementalDrainScratchPos = new(0);
    private readonly Dictionary<long, ConnectedManagedComponentCacheEntry> connectedManagedComponentCache = new();
    /// <summary>Reusable BFS queue scratch. Server-tick single-threaded. Cleared on entry to every BFS.</summary>
    private readonly Queue<long> bfsQueueScratch = new();
    private readonly List<ArchimedesOutletState> activeSeedStatesCache = new();
    private int centralWaterTickCountDownToCompaction = 20;
    private int connectedManagedComponentCacheGeneration;
    private int activeSeedStatesCacheGeneration = -1;
    private bool isInGlobalWaterTickDispatch;
    private bool lastGlobalTickExceededBudget;
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
        ArchimedesPosKey.ResetForWorldUnload();
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
        if (!centralWaterTickSet.Add(id))
        {
            return;
        }

        long priority = controller.GetNextCentralWaterTickDueMs();
        if (priority == 0)
        {
            priority = Environment.TickCount64;
        }

        centralWaterTickQueue.Enqueue(id, priority);
        centralWaterTickEnqueuedPriorityById[id] = priority;
    }

    public void UnregisterFromCentralWaterTick(string controllerId)
    {
        centralWaterTickControllers.Remove(controllerId);
        if (!centralWaterTickSet.Remove(controllerId))
        {
            return;
        }

        centralWaterTickEnqueuedPriorityById.Remove(controllerId);
    }

    private void OnGlobalWaterTick(float dt)
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("water.globalTick");
        long tickStartedAtMs = Environment.TickCount64;
        isInGlobalWaterTickDispatch = true;
        try
        {
            connectedManagedComponentCacheGeneration++;
            connectedManagedComponentCache.Clear();
            activeSeedStatesCacheGeneration = -1;
            activeSeedStatesCache.Clear();
            ProcessConversionIntentQueue();
            if (--centralWaterTickCountDownToCompaction <= 0)
            {
                CompactCentralWaterTickList();
                centralWaterTickCountDownToCompaction = 20;
            }

            long now = Environment.TickCount64;
            PruneExpiredDrainQuarantine(now);
            ProcessIncrementalDrainQueue(now);
            ProcessDeferredNeighborFanoutQueue(now);
            ArchimedesPerf.AddCount("water.incrementalDrain.queueDepth", incrementalDrainQueue.Count);
            ArchimedesPerf.AddCount("water.incrementalDrain.activeStates", incrementalDrainStateByKey.Count);
            ArchimedesPerf.AddCount("water.incrementalDrain.deferredFanoutBacklog", deferredNeighborFanoutQueue.Count);
            ArchimedesPerf.AddCount("water.drainQuarantine.size", drainQuarantineUntilMsByKey.Count);
            int budget = Math.Max(1, config.Water.MaxControllersPerGlobalTick);
            ArchimedesPerf.AddCount("water.globalTick.queueDepth", centralWaterTickQueue.Count);
            if (centralWaterTickQueue.Count == 0)
            {
                return;
            }

            int processed = 0;
            int deferred = 0;
            int staleHeapSkips = 0;
            int tombstoneSkips = 0;
            bool budgetBreak = false;
            while (processed < budget && centralWaterTickQueue.Count > 0)
            {
                if (!centralWaterTickQueue.TryPeek(out string? peekedId, out long priority) || peekedId == null)
                {
                    break;
                }

                if (priority > now)
                {
                    break;
                }

                centralWaterTickQueue.Dequeue();
                string id = peekedId;

                if (!centralWaterTickSet.Contains(id))
                {
                    centralWaterTickEnqueuedPriorityById.Remove(id);
                    tombstoneSkips++;
                    continue;
                }

                if (!centralWaterTickEnqueuedPriorityById.TryGetValue(id, out long currentPriority) ||
                    currentPriority != priority)
                {
                    staleHeapSkips++;
                    continue;
                }

                if (!centralWaterTickControllers.TryGetValue(id, out WeakReference<BlockEntityWaterArchimedesScrew>? wr) ||
                    !wr.TryGetTarget(out BlockEntityWaterArchimedesScrew? be))
                {
                    centralWaterTickSet.Remove(id);
                    centralWaterTickControllers.Remove(id);
                    centralWaterTickEnqueuedPriorityById.Remove(id);
                    tombstoneSkips++;
                    continue;
                }

                if (!be.IsCentralWaterTickDue(now))
                {
                    long reEnqueueAt = be.GetNextCentralWaterTickDueMs();
                    if (reEnqueueAt == 0)
                    {
                        reEnqueueAt = now;
                    }
                    centralWaterTickEnqueuedPriorityById[id] = reEnqueueAt;
                    centralWaterTickQueue.Enqueue(id, reEnqueueAt);
                    continue;
                }

                be.RunCentralWaterTick();
                processed++;

                long updatedPriority = be.GetNextCentralWaterTickDueMs();
                if (updatedPriority == 0)
                {
                    updatedPriority = now;
                }
                centralWaterTickEnqueuedPriorityById[id] = updatedPriority;
                centralWaterTickQueue.Enqueue(id, updatedPriority);

                long elapsedMs = Math.Max(0, Environment.TickCount64 - tickStartedAtMs);
                if (elapsedMs >= HardGlobalTickBudgetMs)
                {
                    deferred = centralWaterTickQueue.Count;
                    budgetBreak = true;
                    break;
                }
            }

            ArchimedesPerf.AddCount("water.globalTick.processedControllers", processed);
            if (budgetBreak)
            {
                ArchimedesPerf.AddCount("water.globalTick.budgetBreaks");
            }
            ArchimedesPerf.AddCount("water.globalTick.deferredControllers", deferred);
            ArchimedesPerf.AddCount("water.globalTick.elapsedMs", Math.Max(0, Environment.TickCount64 - tickStartedAtMs));
            ArchimedesPerf.AddCount("water.globalTick.staleHeapSkips", staleHeapSkips);
            ArchimedesPerf.AddCount("water.globalTick.tombstoneSkips", tombstoneSkips);
            lastGlobalTickExceededBudget = budgetBreak;
        }
        finally
        {
            isInGlobalWaterTickDispatch = false;
            ArchimedesPerf.MaybeFlush(api);
        }
    }

    /// <summary>
    /// Periodic compaction pass: drops dead WeakRefs from <see cref="centralWaterTickControllers"/>
    /// and rebuilds <see cref="centralWaterTickQueue"/> from scratch so accumulated tombstones
    /// (entries left behind by <see cref="UnregisterFromCentralWaterTick"/> and lazy-decrease-key
    /// reschedules) are physically removed from the heap. Runs every
    /// <c>centralWaterTickCountDownToCompaction</c> global ticks.
    /// </summary>
    private void CompactCentralWaterTickList()
    {
        List<string>? deadIds = null;
        foreach (KeyValuePair<string, WeakReference<BlockEntityWaterArchimedesScrew>> kvp in centralWaterTickControllers)
        {
            if (!kvp.Value.TryGetTarget(out _))
            {
                deadIds ??= new List<string>();
                deadIds.Add(kvp.Key);
            }
        }

        if (deadIds != null)
        {
            for (int i = 0; i < deadIds.Count; i++)
            {
                string id = deadIds[i];
                centralWaterTickControllers.Remove(id);
                centralWaterTickSet.Remove(id);
                centralWaterTickEnqueuedPriorityById.Remove(id);
            }
        }

        centralWaterTickQueue.Clear();
        long now = Environment.TickCount64;
        foreach (KeyValuePair<string, WeakReference<BlockEntityWaterArchimedesScrew>> kvp in centralWaterTickControllers)
        {
            string id = kvp.Key;
            if (!centralWaterTickSet.Contains(id))
            {
                continue;
            }

            long priority;
            if (kvp.Value.TryGetTarget(out BlockEntityWaterArchimedesScrew? be))
            {
                priority = be.GetNextCentralWaterTickDueMs();
                if (priority == 0)
                {
                    priority = now;
                }
            }
            else
            {
                priority = now;
            }

            centralWaterTickQueue.Enqueue(id, priority);
            centralWaterTickEnqueuedPriorityById[id] = priority;
        }
    }

    public void Load()
    {
        screwBlockKeys.Clear();
        controllerPosById.Clear();
        controllerOwnedById.Clear();
        ownedKeysByController.Clear();
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
                ownedKeysByController[controllerId] = new HashSet<long>();
                continue;
            }

            controllerOwnedById[controllerId] = flatPositions;
            foreach (BlockPos pos in ArchimedesPositionCodec.DecodePositions(flatPositions))
            {
                long key = ArchimedesPosKey.Pack(pos);
                if (!sourceOwnerByPos.TryGetValue(key, out string? existing))
                {
                    AssignSourceOwnerInternal(key, controllerId);
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
                    AssignSourceOwnerInternal(key, controllerId);
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
        RebuildControllerOwnedSnapshotsForPersistence();
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
        EnsureManagedBlockIdSetsInitialized();
        return managedSourceHeightBlockIds.Contains(block.Id);
    }

    public bool IsArchimedesRelayFlowCandidate(Block block)
    {
        EnsureManagedBlockIdSetsInitialized();
        return relayFlowCandidateBlockIds.Contains(block.Id);
    }

    /// <summary>
    /// Returns true when <paramref name="block"/> is a managed Archimedes water block at height
    /// 6 (relay-flow candidate) or 7 (full source). Hot-path neighbour scans use this instead of
    /// the equivalent string-compare combination.
    /// </summary>
    public bool IsArchimedesManagedFlowOrSourceHeight(Block block)
    {
        EnsureManagedBlockIdSetsInitialized();
        return managedFlowOrSourceHeightBlockIds.Contains(block.Id);
    }

    private void EnsureManagedBlockIdSetsInitialized()
    {
        if (managedBlockIdSetsInitialized)
        {
            return;
        }

        // Walk the world block registry once and bucket every managed Archimedes water variant by
        // its height. Subsequent classification calls become O(1) HashSet<int> lookups keyed on
        // block.Id (no string allocation, no variant dictionary access).
        IList<Block> worldBlocks = api.World.Blocks;
        for (int i = 0; i < worldBlocks.Count; i++)
        {
            Block? candidate = worldBlocks[i];
            if (candidate == null || candidate.Id == 0)
            {
                continue;
            }

            if (candidate.Code?.Domain != ArchimedesScrewModSystem.ModId ||
                !ArchimedesWaterFamilies.IsManagedWater(candidate))
            {
                continue;
            }

            string? height = candidate.Variant?["height"];
            if (string.Equals(height, "7", StringComparison.Ordinal))
            {
                managedSourceHeightBlockIds.Add(candidate.Id);
                managedFlowOrSourceHeightBlockIds.Add(candidate.Id);
            }
            else if (string.Equals(height, "6", StringComparison.Ordinal))
            {
                relayFlowCandidateBlockIds.Add(candidate.Id);
                managedFlowOrSourceHeightBlockIds.Add(candidate.Id);
            }
        }

        managedBlockIdSetsInitialized = true;
    }

    public bool TryGetSourceOwner(BlockPos pos, out string ownerId)
    {
        return sourceOwnerByPos.TryGetValue(ArchimedesPosKey.Pack(pos), out ownerId!);
    }

    /// <summary>
    /// Single authoritative writer for source ownership. Updates both <see cref="sourceOwnerByPos"/>
    /// and the reverse index <see cref="ownedKeysByController"/>. If the cell already has a different
    /// owner, the old owner's reverse-index entry is removed first.
    /// </summary>
    private void AssignSourceOwnerInternal(long key, string ownerId)
    {
        if (sourceOwnerByPos.TryGetValue(key, out string? previousOwner) &&
            !string.Equals(previousOwner, ownerId, StringComparison.Ordinal) &&
            ownedKeysByController.TryGetValue(previousOwner, out HashSet<long>? previousKeys))
        {
            previousKeys.Remove(key);
        }

        sourceOwnerByPos[key] = ownerId;
        if (!ownedKeysByController.TryGetValue(ownerId, out HashSet<long>? keys))
        {
            keys = new HashSet<long>();
            ownedKeysByController[ownerId] = keys;
        }

        keys.Add(key);
    }

    /// <summary>
    /// Single authoritative remover. Returns the previous owner via <paramref name="ownerId"/> when
    /// the cell was owned. Updates both <see cref="sourceOwnerByPos"/> and the reverse index.
    /// </summary>
    private bool ClearSourceOwnerInternal(long key, out string ownerId)
    {
        if (!sourceOwnerByPos.Remove(key, out string? owner))
        {
            ownerId = string.Empty;
            return false;
        }

        ownerId = owner;
        if (ownedKeysByController.TryGetValue(owner, out HashSet<long>? keys))
        {
            keys.Remove(key);
        }

        return true;
    }

    /// <summary>
    /// Rebuilds <see cref="controllerOwnedById"/> (the persistence cache) from the in-memory
    /// reverse index <see cref="ownedKeysByController"/>. Call before serialising or when the
    /// persistence shape is needed externally.
    /// </summary>
    private void RebuildControllerOwnedSnapshotsForPersistence()
    {
        controllerOwnedById.Clear();
        foreach ((string controllerId, HashSet<long> keys) in ownedKeysByController)
        {
            if (keys.Count == 0)
            {
                controllerOwnedById[controllerId] = Array.Empty<int>();
                continue;
            }

            int[] flat = new int[keys.Count * 3];
            int writeIndex = 0;
            BlockPos scratch = new(0);
            foreach (long key in keys)
            {
                ArchimedesPosKey.Unpack(key, scratch);
                flat[writeIndex++] = scratch.X;
                flat[writeIndex++] = scratch.Y;
                flat[writeIndex++] = scratch.Z;
            }

            controllerOwnedById[controllerId] = flat;
        }
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
        long now = Environment.TickCount64;
        long until = now + Math.Max(0, effectiveDurationMs);
        bool keyAlreadyTracked = drainQuarantineUntilMsByKey.TryGetValue(key, out long existingUntil);
        if (keyAlreadyTracked && existingUntil >= until)
        {
            return;
        }

        if (!keyAlreadyTracked && drainQuarantineUntilMsByKey.Count >= DrainQuarantineSoftCap)
        {
            PruneExpiredDrainQuarantine(now, force: true);
            if (drainQuarantineUntilMsByKey.Count >= DrainQuarantineSoftCap)
            {
                EvictSmallestUntilDrainQuarantineEntry();
                ArchimedesPerf.AddCount("water.drainQuarantine.capEvictions");
            }
        }

        drainQuarantineUntilMsByKey[key] = until;
    }

    /// <summary>
    /// Removes the entry from <see cref="drainQuarantineUntilMsByKey"/> whose <c>until</c> value is
    /// smallest (soonest-to-expire). Called only when the soft cap is hit and a forced prune did
    /// not free space, so the O(N) scan runs at most once per cap-saturating insertion.
    /// </summary>
    private void EvictSmallestUntilDrainQuarantineEntry()
    {
        if (drainQuarantineUntilMsByKey.Count == 0)
        {
            return;
        }

        long victimKey = 0;
        long victimUntil = long.MaxValue;
        bool hasVictim = false;
        foreach (KeyValuePair<long, long> entry in drainQuarantineUntilMsByKey)
        {
            if (!hasVictim || entry.Value < victimUntil)
            {
                victimKey = entry.Key;
                victimUntil = entry.Value;
                hasVictim = true;
            }
        }

        if (hasVictim)
        {
            drainQuarantineUntilMsByKey.Remove(victimKey);
        }
    }

    public bool IsDrainQuarantined(BlockPos pos)
    {
        return IsDrainQuarantined(ArchimedesPosKey.Pack(pos));
    }

    public bool IsDrainQuarantined(long key)
    {
        return drainQuarantineUntilMsByKey.TryGetValue(key, out long until) &&
               Environment.TickCount64 < until;
    }

    public int GetIncrementalDrainBacklogCount()
    {
        return incrementalDrainStateByKey.Count + incrementalDrainQueue.Count;
    }

    /// <summary>
    /// Removes expired entries from <see cref="drainQuarantineUntilMsByKey"/>. Throttled to run at
    /// most once per <see cref="DrainQuarantinePruneIntervalMs"/>; invoked from the global water
    /// tick. <see cref="IsDrainQuarantined(long)"/> deliberately does not call this anymore so that
    /// hot-path claim checks stay allocation-free. Pass <paramref name="force"/>=true to bypass the
    /// throttle (used when the soft cap is hit during <see cref="MarkDrainQuarantine"/>); a forced
    /// pass does not advance <see cref="nextDrainQuarantinePruneAtMs"/> so the regular cadence is
    /// preserved.
    /// </summary>
    private void PruneExpiredDrainQuarantine(long nowMs, bool force = false)
    {
        if (drainQuarantineUntilMsByKey.Count == 0)
        {
            return;
        }

        if (!force && nowMs < nextDrainQuarantinePruneAtMs)
        {
            return;
        }

        if (!force)
        {
            nextDrainQuarantinePruneAtMs = nowMs + DrainQuarantinePruneIntervalMs;
        }

        List<long> scratch = drainQuarantinePruneScratch;
        scratch.Clear();
        foreach (KeyValuePair<long, long> entry in drainQuarantineUntilMsByKey)
        {
            if (entry.Value <= nowMs)
            {
                scratch.Add(entry.Key);
            }
        }

        for (int i = 0; i < scratch.Count; i++)
        {
            drainQuarantineUntilMsByKey.Remove(scratch[i]);
        }

        scratch.Clear();
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
        Dictionary<long, int> distanceByKey = new();

        Block startFluid = api.World.BlockAccessor.GetBlock(startPos, BlockLayersAccess.Fluid);
        if (!IsArchimedesWaterBlock(startFluid))
        {
            return new ConnectedManagedWaterResult(visited, positionsByKey, distanceByKey, startPos.Copy(), false, 0);
        }

        Queue<long> queue = bfsQueueScratch;
        queue.Clear();
        long startKey = ArchimedesPosKey.Pack(startPos);
        queue.Enqueue(startKey);
        visited.Add(startKey);
        distanceByKey[startKey] = 0;
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
            int currentDistance = distanceByKey[key];
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
                distanceByKey[nextKey] = currentDistance + 1;
                queue.Enqueue(nextKey);
            }
        }

        queue.Clear();
        ArchimedesPerf.AddCount("water.collectConnectedManaged.visited", visitedManagedCount);
        return new ConnectedManagedWaterResult(visited, positionsByKey, distanceByKey, startPos.Copy(), isTruncated, visitedManagedCount);
    }

    /// <summary>
    /// Keys-only BFS: visits the managed-water component around <paramref name="startPos"/> and
    /// fills <paramref name="visited"/> with the discovered cells. Skips the per-cell
    /// <see cref="BlockPos"/> and distance allocations that <see cref="CollectConnectedManagedWaterDetailed"/>
    /// produces. Use when the caller only needs membership/ordering decisions (purge scans,
    /// cleanup-around-anchors, find-nearest-active-controller connectivity checks).
    /// </summary>
    public void CollectConnectedManagedWaterKeysOnly(BlockPos startPos, HashSet<long> visited)
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("water.collectConnectedManagedKeysOnly");
        visited.Clear();

        Block startFluid = api.World.BlockAccessor.GetBlock(startPos, BlockLayersAccess.Fluid);
        if (!IsArchimedesWaterBlock(startFluid))
        {
            return;
        }

        Queue<long> queue = bfsQueueScratch;
        queue.Clear();
        long startKey = ArchimedesPosKey.Pack(startPos);
        queue.Enqueue(startKey);
        visited.Add(startKey);
        BlockPos currentPos = new(0);
        BlockPos nextPos = new(0);
        int visitedManagedCount = 0;

        while (queue.Count > 0)
        {
            if (visitedManagedCount >= MaxBfsVisited)
            {
                api.Logger.Warning(
                    "{0} BFS in CollectConnectedManagedWaterKeysOnly hit limit of {1} blocks starting at {2}",
                    ArchimedesScrewModSystem.LogPrefix,
                    MaxBfsVisited,
                    startPos
                );
                break;
            }

            long key = queue.Dequeue();
            ArchimedesPosKey.Unpack(key, currentPos);
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

        queue.Clear();
        ArchimedesPerf.AddCount("water.collectConnectedManagedKeysOnly.visited", visitedManagedCount);
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
                cached.DistanceByKey,
                cached.StartPos,
                cached.IsTruncated,
                cached.VisitedManagedCount
            );
        }

        ConnectedManagedWaterResult result = CollectConnectedManagedWaterDetailed(startPos);
        // Distance values in the cached entry are anchored at result.StartPos (the initial BFS seed).
        // Consumers that need distances from a different seed must recompute; the cached distance is
        // exposed as an aid for same-seed queries.
        ConnectedManagedComponentCacheEntry entry = new(
            connectedManagedComponentCacheGeneration,
            result.VisitedKeys,
            result.PositionsByKey,
            result.DistanceByKey ?? new Dictionary<long, int>(),
            result.StartPos ?? startPos.Copy(),
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
        if (!sourceOwnerByPos.TryGetValue(key, out string? existingOwner) ||
            !string.Equals(existingOwner, ownerId, StringComparison.Ordinal))
        {
            AssignSourceOwnerInternal(key, ownerId);
            changed = true;
        }

        CancelIncrementalDrainByKey(key);

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
        if (!ownedKeysByController.TryGetValue(controllerId, out HashSet<long>? keys) || keys.Count == 0)
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

    public bool IsOwnedByController(string controllerId, BlockPos pos)
    {
        long key = ArchimedesPosKey.Pack(pos);
        return sourceOwnerByPos.TryGetValue(key, out string? ownerId) &&
               string.Equals(ownerId, controllerId, StringComparison.Ordinal);
    }

    public int GetOwnedCountForController(string controllerId)
    {
        return ownedKeysByController.TryGetValue(controllerId, out HashSet<long>? keys) ? keys.Count : 0;
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

    public ReleaseOutcome ReleaseRelaySourceForController(string controllerId, BlockPos pos)
    {
        // ReleaseSourceOwner already cleans up controllerRelaySourceKeys and relayOwnerByPos when
        // the source ownership is dropped (see the controllerRelaySourceKeys block inside that
        // method). The previous explicit cleanup duplicated that work for every trim/drain call.
        return ReleaseOwnedSourceForController(controllerId, pos);
    }

    /// <summary>
    /// Replaces the entire relay snapshot for a controller (used by BE post-load hydration).
    /// Adds entries for new positions and removes entries for stale ones, syncing the reverse index.
    /// </summary>
    public void ReplaceRelaySnapshotForController(string controllerId, IReadOnlyCollection<BlockPos> relayPositions)
    {
        HashSet<long> newKeys = new(relayPositions.Count);
        foreach (BlockPos pos in relayPositions)
        {
            newKeys.Add(ArchimedesPosKey.Pack(pos));
        }

        if (controllerRelaySourceKeys.TryGetValue(controllerId, out HashSet<long>? existingKeys))
        {
            // Two-pass: gather stale keys into a reusable scratch (avoids the LINQ Where(...).ToList()
            // chain) before removing from existingKeys / relayOwnerByPos.
            replaceRelayStaleScratch.Clear();
            foreach (long key in existingKeys)
            {
                if (!newKeys.Contains(key))
                {
                    replaceRelayStaleScratch.Add(key);
                }
            }

            for (int i = 0; i < replaceRelayStaleScratch.Count; i++)
            {
                long staleKey = replaceRelayStaleScratch[i];
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
            existingKeys.Add(key);
            relayOwnerByPos[key] = controllerId;
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

        AssignSourceOwnerInternal(key, ownerId);
        AddOwnedPosToSnapshot(ownerId, pos);
        return true;
    }

    public ReleaseOutcome ReleaseSourceOwner(string ownerId, BlockPos pos)
    {
        long key = ArchimedesPosKey.Pack(pos);
        if (!sourceOwnerByPos.TryGetValue(key, out string? owner))
        {
            Block orphanFluid = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
            if (!IsArchimedesSourceBlock(orphanFluid))
            {
                return ReleaseOutcome.NotOwned;
            }

            SuppressRemovalNotification(key);
            if (!TryEnqueueIncrementalDrain(pos, ownerId, "release orphan source"))
            {
                RemoveFluidAndNotifyNeighbours(pos);
            }
            return ReleaseOutcome.OrphanRemoved;
        }

        if (!string.Equals(owner, ownerId, StringComparison.Ordinal))
        {
            return ReleaseOutcome.OwnedByOtherController;
        }

        ClearSourceOwnerInternal(key, out _);
        sourceProvenanceByPos.Remove(key);
        if (controllerRelaySourceKeys.TryGetValue(owner, out HashSet<long>? ownerRelayKeys) &&
            ownerRelayKeys.Remove(key) &&
            relayOwnerByPos.TryGetValue(key, out string? currentRelayOwner) &&
            string.Equals(currentRelayOwner, owner, StringComparison.Ordinal))
        {
            relayOwnerByPos.Remove(key);
        }

        RemoveOwnedPosFromSnapshot(ownerId, pos);
        Block fluidBlock = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (IsArchimedesWaterBlock(fluidBlock))
        {
            SuppressRemovalNotification(key);
            if (!TryEnqueueIncrementalDrain(pos, ownerId, "release owned source"))
            {
                RemoveFluidAndNotifyNeighbours(pos);
            }
        }

        return ReleaseOutcome.Released;
    }

    /// <summary>
    /// Intent-revealing wrapper for controller-driven source release.
    /// Preserves existing release semantics from <see cref="ReleaseSourceOwner"/>.
    /// </summary>
    public ReleaseOutcome ReleaseOwnedSourceForController(string controllerId, BlockPos pos)
    {
        return ReleaseSourceOwner(controllerId, pos);
    }

    /// <summary>
    /// Attempts adoption by the nearest active controller, otherwise removes unowned managed source fluid.
    /// Skips cells that are still owned or relay-tracked in the manager.
    /// </summary>
    /// <returns>True if managed source fluid was removed from the world.</returns>
    public bool TryAdoptOrRemoveUnownedArchimedesSource(BlockPos pos)
    {
        long key = ArchimedesPosKey.Pack(pos);
        if (sourceOwnerByPos.ContainsKey(key))
        {
            unownedCleanupCooldownUntilMsByKey.Remove(key);
            return false;
        }

        if (relayOwnerByPos.ContainsKey(key))
        {
            return false;
        }

        long nowMs = Environment.TickCount64;
        if (unownedCleanupCooldownUntilMsByKey.TryGetValue(key, out long retryAtMs) && nowMs < retryAtMs)
        {
            return false;
        }

        Block fluid = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!IsArchimedesSourceBlock(fluid))
        {
            unownedCleanupCooldownUntilMsByKey.Remove(key);
            return false;
        }

        if (TryResolveManagedWaterFamily(fluid, out string familyId) &&
            AssignNearestActiveControllerForNewSource(
                pos,
                familyId,
                reason: "cleanup-unowned adoption"))
        {
            unownedCleanupCooldownUntilMsByKey.Remove(key);
            return false;
        }

        SuppressRemovalNotification(key);
        if (!TryEnqueueIncrementalDrain(pos, ownerHintControllerId: null, reason: "cleanup unowned source"))
        {
            RemoveFluidAndNotifyNeighbours(pos);
        }
        unownedCleanupCooldownUntilMsByKey[key] = nowMs + UnownedCleanupRetryCooldownMs;
        return true;
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

            HashSet<long> connectedKeys = new();
            if (AggressiveDrainEnabled)
            {
                CollectConnectedManagedWaterKeysOnly(anchor, connectedKeys);
            }
            else
            {
                CollectConnectedManagedWater(anchor, out Dictionary<long, BlockPos> connectedWater);
                connectedKeys = connectedWater.Keys.ToHashSet();
            }

            int scanStride = 1;
            if (AggressiveDrainEnabled &&
                connectedKeys.Count > Math.Max(1, LargeComponentSamplingThreshold))
            {
                scanStride = Math.Max(1, DrainConnectivitySampleStride);
                ArchimedesPerf.AddCount("water.cleanupUnowned.sampleStrideActive");
                ArchimedesPerf.AddCount("water.cleanupUnowned.sampleStride", scanStride);
            }

            int scanIndex = 0;
            foreach (long key in connectedKeys)
            {
                if (scanStride > 1 && (scanIndex++ % scanStride) != 0)
                {
                    continue;
                }

                if (removed >= budget || !seenKeys.Add(key))
                {
                    continue;
                }

                BlockPos pos = ArchimedesPosKey.UnpackToNew(key);
                if (TryAdoptOrRemoveUnownedArchimedesSource(pos))
                {
                    removed++;
                }
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
        return ConvertAdjacentVanillaSources(connectedResult, ownerHintControllerId);
    }

    public int ConvertAdjacentVanillaSources(ConnectedManagedWaterResult connectedResult, string? ownerHintControllerId = null)
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("water.convertAdjacentVanilla");
        if (connectedResult.IsTruncated)
        {
            BlockPos logPos = connectedResult.StartPos ?? new BlockPos(0, 0, 0);
            LogTruncationPausedAutomation("ConvertAdjacentVanillaSources", logPos, connectedResult.VisitedManagedCount);
            return 0;
        }

        int converted = 0;
        HashSet<long> convertedKeys = new();
        Dictionary<long, BlockPos> connectedWater = connectedResult.PositionsByKey;
        BlockPos adjacentPos = new(0);
        int neighborsVisited = 0;
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
                neighborsVisited++;
                if (!CanLiquidsTouch(pos, adjacentPos))
                {
                    continue;
                }

                if (TryConvertVanillaSource(adjacentPos, familyId, ownerHintControllerId))
                {
                    converted++;
                }
            }
        }

        ArchimedesPerf.AddCount("water.convertAdjacentVanilla.connectedNodes", connectedWater.Count);
        ArchimedesPerf.AddCount("water.convertAdjacentVanilla.neighborsVisited", neighborsVisited);
        ArchimedesPerf.AddCount("water.convertAdjacentVanilla.converted", converted);
        return converted;
    }

    /// <summary>
    /// Repeats <see cref="ConvertAdjacentVanillaSources"/> until no vanilla sources remain adjacent to the
    /// growing managed-water component, or <paramref name="maxPasses"/> is reached. A single pass only converts
    /// neighbours of the current BFS set, so a chain of bucket-placed sources needs multiple passes.
    /// </summary>
    public int ConvertAdjacentVanillaSourcesIteratively(BlockPos startPos, int maxPasses, string? ownerHintControllerId = null)
    {
        ConnectedManagedWaterResult connectedResult = CollectConnectedManagedWaterDetailed(startPos);
        return ConvertAdjacentVanillaSourcesIteratively(connectedResult, maxPasses, ownerHintControllerId);
    }

    public int ConvertAdjacentVanillaSourcesIteratively(
        ConnectedManagedWaterResult initialConnectedResult,
        int maxPasses,
        string? ownerHintControllerId = null)
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("water.convertAdjacentVanillaIterative");
        int capped = Math.Clamp(maxPasses, 1, 256);
        int total = 0;
        int passesRun = 0;
        ConnectedManagedWaterResult current = initialConnectedResult;
        for (int pass = 0; pass < capped; pass++)
        {
            passesRun++;
            int batch = ConvertAdjacentVanillaSources(current, ownerHintControllerId);
            if (batch == 0)
            {
                break;
            }

            total += batch;
            BlockPos reseed = current.StartPos ?? ArchimedesPosKey.UnpackToNew(current.VisitedKeys.First());
            current = CollectConnectedManagedWaterDetailed(reseed);
        }

        ArchimedesPerf.AddCount("water.convertAdjacentVanillaIterative.passes", passesRun);
        ArchimedesPerf.AddCount("water.convertAdjacentVanillaIterative.converted", total);
        return total;
    }

    internal bool WasLastGlobalTickBudgetExceeded()
    {
        return lastGlobalTickExceededBudget;
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
        int maxVisit = MaxBfsVisited)
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("water.seizeConnectedFamilyFluid");
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
            int lockedNeighbors = CountLockedVanillaNeighbors(posScratch, familyId);

            if (IsManagedSelfSustainingSourceForFamily(fluid, familyId) &&
                !sourceOwnerByPos.ContainsKey(pkey) &&
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

        ArchimedesPerf.AddCount("water.seizeConnectedFamilyFluid.visited", visited.Count);
        ArchimedesPerf.AddCount("water.seizeConnectedFamilyFluid.dequeued", dequeued);
        ArchimedesPerf.AddCount("water.seizeConnectedFamilyFluid.seized", seized);
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
        if (suppressedRemovalNotifications.Remove(key))
        {
            return;
        }

        if (!ClearSourceOwnerInternal(key, out string ownerId))
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
        EnsureManagedBlockIdSetsInitialized();
        return managedSourceHeightBlockIds.Contains(block.Id);
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

    private T? LoadSerialized<T>(string key)
    {
        byte[] data = api.WorldManager.SaveGame.GetData(key);
        return data == null ? default : SerializerUtil.Deserialize<T>(data);
    }

    private bool AssignNearestActiveControllerForNewSource(BlockPos sourcePos, string familyId, string reason = "new source assignment")
    {
        if (IsDrainQuarantined(sourcePos))
        {
            ArchimedesPerf.AddCount("water.claims.rejected.drainQuarantined");
            return false;
        }

        IReadOnlyCollection<long> connectedKeys;
        if (AggressiveDrainEnabled)
        {
            HashSet<long> sampledConnectedKeys = new();
            CollectConnectedManagedWaterKeysOnly(sourcePos, sampledConnectedKeys);
            connectedKeys = sampledConnectedKeys;
            if (sampledConnectedKeys.Count > Math.Max(1, LargeComponentSamplingThreshold))
            {
                HashSet<long> downsampled = new();
                int stride = Math.Max(1, DrainConnectivitySampleStride);
                int i = 0;
                foreach (long sampledKey in sampledConnectedKeys)
                {
                    if ((i++ % stride) == 0)
                    {
                        downsampled.Add(sampledKey);
                    }
                }

                connectedKeys = downsampled;
                ArchimedesPerf.AddCount("water.findNearestActiveController.sampledConnectedSet");
                ArchimedesPerf.AddCount("water.findNearestActiveController.sampledConnectedSet.size", downsampled.Count);
            }
        }
        else
        {
            ConnectedManagedWaterResult connectedResult = CollectConnectedManagedWaterCachedDetailed(sourcePos);
            if (connectedResult.IsTruncated)
            {
                LogTruncationPausedAutomation("AssignNearestActiveControllerForNewSource", sourcePos, connectedResult.VisitedManagedCount);
                return false;
            }

            connectedKeys = connectedResult.VisitedKeys;
        }

        string? nearest = FindNearestActiveControllerId(
            sourcePos,
            connectedKeys,
            familyId,
            excludedControllerId: null,
            requireConnected: true
        );
        if (nearest == null)
        {
            return false;
        }

        long key = ArchimedesPosKey.Pack(sourcePos);
        AssignSourceOwnerInternal(key, nearest);
        AddOwnedPosToSnapshot(nearest, sourcePos);
        NotifyControllerSourceAssigned(nearest, sourcePos, reason);
        return true;
    }

    private string? FindNearestActiveControllerId(
        BlockPos sourcePos,
        IReadOnlyCollection<long> connectedWaterKeys,
        string familyId,
        string? excludedControllerId,
        bool requireConnected
    )
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("water.findNearestActiveController");
        // Avoid copying when caller already hands us a HashSet; preserves O(1) Contains checks.
        HashSet<long>? connected = requireConnected
            ? (connectedWaterKeys as HashSet<long> ?? new HashSet<long>(connectedWaterKeys))
            : null;

        int candidateCount = 0;
        ArchimedesOutletState? best = null;
        long bestDistSq = long.MaxValue;
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

            candidateCount++;
            long distanceSq = ArchimedesPositionCodec.DistanceSquared(sourcePos, seed.SeedPos);
            if (best == null || distanceSq < bestDistSq || (distanceSq == bestDistSq && IsPreferredControllerCandidate(seed, best.Value)))
            {
                bestDistSq = distanceSq;
                best = seed;
            }
        }

        ArchimedesPerf.AddCount("water.findNearestActiveController.candidates", candidateCount);
        return best?.ControllerId;
    }

    private static bool IsPreferredControllerCandidate(ArchimedesOutletState candidate, ArchimedesOutletState incumbent)
    {
        int yCmp = candidate.SeedPos.Y.CompareTo(incumbent.SeedPos.Y);
        if (yCmp != 0)
        {
            return yCmp < 0;
        }

        int xCmp = candidate.SeedPos.X.CompareTo(incumbent.SeedPos.X);
        if (xCmp != 0)
        {
            return xCmp < 0;
        }

        int zCmp = candidate.SeedPos.Z.CompareTo(incumbent.SeedPos.Z);
        if (zCmp != 0)
        {
            return zCmp < 0;
        }

        return string.CompareOrdinal(candidate.ControllerId, incumbent.ControllerId) < 0;
    }

    private void NotifyControllerSourceAssigned(string controllerId, BlockPos sourcePos, string reason)
    {
        if (loadedControllers.TryGetValue(controllerId, out WeakReference<BlockEntityWaterArchimedesScrew>? reference) &&
            reference.TryGetTarget(out BlockEntityWaterArchimedesScrew? controller))
        {
            controller.TrackAssignedSourceFromManager(sourcePos, reason);
        }
    }

    /// <summary>
    /// Adds <paramref name="pos"/> to the in-memory reverse index for <paramref name="controllerId"/>.
    /// The on-disk <see cref="controllerOwnedById"/> blob is not touched here; it is rebuilt from the
    /// reverse index at <see cref="Save"/> time and on explicit BE-driven snapshot updates via
    /// <see cref="ReplaceSourceOwnershipForController"/>.
    /// </summary>
    private void AddOwnedPosToSnapshot(string controllerId, BlockPos pos)
    {
        long key = ArchimedesPosKey.Pack(pos);
        if (!ownedKeysByController.TryGetValue(controllerId, out HashSet<long>? keys))
        {
            keys = new HashSet<long>();
            ownedKeysByController[controllerId] = keys;
        }

        keys.Add(key);
    }

    /// <summary>
    /// Removes <paramref name="pos"/> from the in-memory reverse index for <paramref name="controllerId"/>.
    /// The on-disk <see cref="controllerOwnedById"/> blob is not touched here; see
    /// <see cref="AddOwnedPosToSnapshot"/> for the persistence contract.
    /// </summary>
    private void RemoveOwnedPosFromSnapshot(string controllerId, BlockPos pos)
    {
        if (!ownedKeysByController.TryGetValue(controllerId, out HashSet<long>? keys))
        {
            return;
        }

        keys.Remove(ArchimedesPosKey.Pack(pos));
    }

    private void SuppressRemovalNotification(long key)
    {
        suppressedRemovalNotifications.Add(key);
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

    private bool TryEnqueueIncrementalDrain(BlockPos pos, string? ownerHintControllerId, string reason)
    {
        if (!config.Water.EnableIncrementalSourceDrain)
        {
            return false;
        }

        long key = ArchimedesPosKey.Pack(pos);
        if (!ArchimedesPosKey.IsInBounds(pos.X, pos.Y, pos.Z))
        {
            return false;
        }

        Block fluid = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!IsArchimedesWaterBlock(fluid) ||
            !TryResolveManagedWaterFamily(fluid, out string familyId))
        {
            return false;
        }

        int currentHeight = GetManagedWaterHeight(fluid);
        if (currentHeight <= 0)
        {
            return false;
        }

        long nowMs = Environment.TickCount64;
        if (incrementalDrainStateByKey.TryGetValue(key, out IncrementalDrainState existing))
        {
            long nextStep = Math.Min(existing.NextStepAtMs, nowMs);
            incrementalDrainStateByKey[key] = existing with
            {
                LastObservedHeight = Math.Max(existing.LastObservedHeight, currentHeight),
                NextStepAtMs = nextStep
            };

            if (DrainQueueCoalesceEnabled && incrementalDrainQueuedKeys.Contains(key))
            {
                ArchimedesPerf.AddCount("water.incrementalDrain.coalesced");
                return true;
            }
        }

        incrementalDrainStateByKey[key] = new IncrementalDrainState(
            familyId,
            currentHeight,
            nowMs,
            nowMs,
            ownerHintControllerId,
            reason
        );
        incrementalDrainQueue.Enqueue(key);
        incrementalDrainQueuedKeys.Add(key);
        ArchimedesPerf.AddCount("water.incrementalDrain.enqueued");
        return true;
    }

    private void CancelIncrementalDrainByKey(long key)
    {
        if (incrementalDrainStateByKey.Remove(key))
        {
            ArchimedesPerf.AddCount("water.incrementalDrain.cancelled");
        }

        incrementalDrainQueuedKeys.Remove(key);
        deferredNeighborFanoutQueuedKeys.Remove(key);
    }

    private void ProcessIncrementalDrainQueue(long nowMs)
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("water.incrementalDrain.processQueue");
        if (!config.Water.EnableIncrementalSourceDrain || incrementalDrainStateByKey.Count == 0 || incrementalDrainQueue.Count == 0)
        {
            return;
        }

        int budget = Math.Max(1, config.Water.MaxIncrementalDrainStepsPerGlobalTick);
        int stepIntervalMs = Math.Max(10, config.Water.IncrementalDrainStepIntervalMs);
        int maxProcessMs = Math.Max(1, DrainQueueMaxProcessMsPerGlobalTick);
        bool deferNeighborUpdates = AggressiveDrainEnabled && DeferredNeighborUpdatesEnabled;
        long startedAtMs = Environment.TickCount64;
        int processed = 0;
        int dequeues = Math.Min(incrementalDrainQueue.Count, budget * 4);
        for (int i = 0; i < dequeues && processed < budget; i++)
        {
            if (Environment.TickCount64 - startedAtMs >= maxProcessMs)
            {
                ArchimedesPerf.AddCount("water.incrementalDrain.budgetBreakMs");
                break;
            }

            long key = incrementalDrainQueue.Dequeue();
            incrementalDrainQueuedKeys.Remove(key);
            if (!incrementalDrainStateByKey.TryGetValue(key, out IncrementalDrainState state))
            {
                continue;
            }

            if (state.NextStepAtMs > nowMs)
            {
                incrementalDrainQueue.Enqueue(key);
                incrementalDrainQueuedKeys.Add(key);
                continue;
            }

            if (sourceOwnerByPos.ContainsKey(key) || relayOwnerByPos.ContainsKey(key))
            {
                incrementalDrainStateByKey.Remove(key);
                continue;
            }

            ArchimedesPosKey.Unpack(key, incrementalDrainScratchPos);
            if (!ArchimedesPosKey.IsInBounds(incrementalDrainScratchPos.X, incrementalDrainScratchPos.Y, incrementalDrainScratchPos.Z))
            {
                incrementalDrainStateByKey.Remove(key);
                continue;
            }

            Block fluid = api.World.BlockAccessor.GetBlock(incrementalDrainScratchPos, BlockLayersAccess.Fluid);
            if (!IsArchimedesWaterBlock(fluid) ||
                !TryResolveManagedWaterFamily(fluid, out string familyId) ||
                !string.Equals(familyId, state.FamilyId, StringComparison.Ordinal))
            {
                incrementalDrainStateByKey.Remove(key);
                continue;
            }

            int currentHeight = GetManagedWaterHeight(fluid);
            if (currentHeight <= 1)
            {
                SuppressRemovalNotification(key);
                RemoveFluidAndNotifyNeighbours(incrementalDrainScratchPos);
                incrementalDrainStateByKey.Remove(key);
                processed++;
                ArchimedesPerf.AddCount("water.incrementalDrain.completed");
                continue;
            }

            string flow = fluid.Variant?["flow"] ?? "still";
            int nextHeight = Math.Max(1, currentHeight - 1);
            SuppressRemovalNotification(key);
            SetManagedWaterVariant(incrementalDrainScratchPos, state.FamilyId, flow, nextHeight, triggerUpdates: !deferNeighborUpdates);
            if (deferNeighborUpdates && nowMs >= state.NextFanoutAtMs)
            {
                EnqueueDeferredNeighborFanout(key);
            }

            incrementalDrainStateByKey[key] = state with
            {
                LastObservedHeight = nextHeight,
                NextStepAtMs = nowMs + stepIntervalMs,
                NextFanoutAtMs = nowMs + Math.Max(20, DeferredNeighborUpdateIntervalMs)
            };
            incrementalDrainQueue.Enqueue(key);
            incrementalDrainQueuedKeys.Add(key);
            processed++;
            ArchimedesPerf.AddCount("water.incrementalDrain.step");
        }
    }

    private void EnqueueDeferredNeighborFanout(long key)
    {
        if (deferredNeighborFanoutQueuedKeys.Add(key))
        {
            deferredNeighborFanoutQueue.Enqueue(key);
            ArchimedesPerf.AddCount("water.incrementalDrain.deferredFanout.enqueued");
        }
    }

    private void ProcessDeferredNeighborFanoutQueue(long nowMs)
    {
        if (!AggressiveDrainEnabled || !DeferredNeighborUpdatesEnabled || deferredNeighborFanoutQueue.Count == 0)
        {
            return;
        }

        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("water.incrementalDrain.processDeferredFanout");
        int budget = Math.Max(1, DeferredNeighborFanoutPerGlobalTick);
        int processed = 0;
        while (processed < budget && deferredNeighborFanoutQueue.Count > 0)
        {
            long key = deferredNeighborFanoutQueue.Dequeue();
            deferredNeighborFanoutQueuedKeys.Remove(key);

            ArchimedesPosKey.Unpack(key, incrementalDrainScratchPos);
            if (!ArchimedesPosKey.IsInBounds(incrementalDrainScratchPos.X, incrementalDrainScratchPos.Y, incrementalDrainScratchPos.Z))
            {
                continue;
            }

            Block fluid = api.World.BlockAccessor.GetBlock(incrementalDrainScratchPos, BlockLayersAccess.Fluid);
            if (fluid.Id == 0)
            {
                continue;
            }

            TriggerLiquidUpdates(incrementalDrainScratchPos, fluid);
            if (incrementalDrainStateByKey.TryGetValue(key, out IncrementalDrainState state))
            {
                incrementalDrainStateByKey[key] = state with
                {
                    NextFanoutAtMs = nowMs + Math.Max(20, DeferredNeighborUpdateIntervalMs)
                };
            }

            processed++;
            ArchimedesPerf.AddCount("water.incrementalDrain.deferredFanout.processed");
        }
    }

    private static int GetManagedWaterHeight(Block fluidBlock)
    {
        string? heightText = fluidBlock.Variant?["height"];
        if (int.TryParse(heightText, out int parsedHeight))
        {
            return parsedHeight;
        }

        return fluidBlock.LiquidLevel;
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
        Dictionary<long, int> DistanceByKey,
        BlockPos StartPos,
        bool IsTruncated,
        int VisitedManagedCount
    );

    private readonly record struct IncrementalDrainState(
        string FamilyId,
        int LastObservedHeight,
        long NextStepAtMs,
        long NextFanoutAtMs,
        string? OwnerHintControllerId,
        string Reason
    );

    private bool ControllerSnapshotContainsPos(string controllerId, long key)
    {
        return ownedKeysByController.TryGetValue(controllerId, out HashSet<long>? keys) &&
               keys.Contains(key);
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

/// <summary>
/// Result of a managed-water BFS. When <see cref="DistanceByKey"/> is non-null it carries the BFS
/// distance (in steps, rooted at the BFS start cell) for every key in <see cref="VisitedKeys"/>.
/// When populated it replaces the need for a second BFS pass (for example in relay candidate
/// selection). May be null for keys-only overloads that skip distance recording.
/// </summary>
public readonly record struct ConnectedManagedWaterResult(
    HashSet<long> VisitedKeys,
    Dictionary<long, BlockPos> PositionsByKey,
    Dictionary<long, int>? DistanceByKey,
    BlockPos? StartPos,
    bool IsTruncated,
    int VisitedManagedCount
);

/// <summary>
/// Precise outcome of a source release attempt so callers can distinguish
/// genuine releases, orphan cleanups, and drift (cell owned by a different controller).
/// </summary>
public enum ReleaseOutcome
{
    /// <summary>The cell was owned by the requesting controller; tracking cleared and managed fluid removed.</summary>
    Released,

    /// <summary>No owner was recorded but a managed source block existed; fluid removed directly.</summary>
    OrphanRemoved,

    /// <summary>sourceOwnerByPos[key] belongs to a different controller; no side effects.</summary>
    OwnedByOtherController,

    /// <summary>No owner recorded and no managed fluid present; nothing to do.</summary>
    NotOwned
}
