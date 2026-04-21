using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent.Mechanics;

namespace ArchimedesScrew;

public sealed class BlockEntityWaterArchimedesScrew : BlockEntity
{
    private const string OwnedPositionsKey = "ownedPositions";
    private const string RelayPositionsKey = "relayPositions";
    private const string ControllerIdKey = "controllerId";
    private const string LastSeedKey = "lastSeed";
    private const string WasControllerKey = "wasController";
    private const string LastEffectiveRelayCapKey = "lastEffectiveRelayCap";
    private const int LowCadenceConnectivityScanStride = 3;

    /// <summary>
    /// After world load, BFS from the seed often does not yet include the full managed-water chain (fluid updates / reactivation still running).
    /// Skipping <see cref="DrainUnsupportedSources"/> briefly prevents false "disconnected" releases of valid relays.
    /// </summary>
    private const int PostLoadDrainUnsupportedGraceMs = 12000;
    private const int TopologyChangeDrainUnsupportedGraceMs = 4000;
    private const int LocalSourceCooldownMs = 2500;
    private const int CooldownPruneIntervalMs = 5000;

    private readonly Dictionary<long, BlockPos> ownedPositions = new();
    private readonly Dictionary<long, BlockPos> relayOwnedPositions = new();

    // Reusable scratch for top-K relay-candidate selection. Reverse comparer turns the priority
    // queue into a bounded max-heap: we only keep the K best candidates (smallest natural rank),
    // so the worst entry sits at the top and gets evicted when a better candidate arrives.
    private readonly PriorityQueue<long, RelayCandidateRank> relayTopKScratch = new(new ReverseRelayCandidateRankComparer());
    private readonly List<RelayCandidateEntry> relayCandidateOrderedScratch = new();

    // Reusable scratch for top-K trim selection. Default min-heap behaviour: we keep the K worst
    // (largest natural rank) entries; the smallest of that set sits at the top and is evicted
    // when a strictly worse incumbent arrives.
    private readonly PriorityQueue<BlockPos, TrimCandidateRank> trimTopKScratch = new();
    private readonly List<BlockPos> trimOrderedScratch = new();

    // Reusable decorate-sort-undecorate scratch for unsupported-release ordering. MinDistanceSquared
    // is precomputed once per candidate (vs O(N log N) recomputations inside the comparer).
    private readonly List<(int MinDistSq, BlockPos Pos)> releaseDecoratedScratch = new();

    // Reusable origin list for BuildDrainProbeOriginPositions. Consumers (drain/seize) treat the
    // list as read-only and don't retain references across the call, so we can share BlockPos
    // instances without defensive copies.
    private readonly List<BlockPos> drainProbeOriginScratch = new();

    /// <summary>
    /// Per-relay promotion timestamp (Environment.TickCount64). Used by age-based trim to drop newest first.
    /// Entries restored from save are stamped 0 so legacy relays are treated as oldest (kept preferentially).
    /// </summary>
    private readonly Dictionary<long, long> relayPromotedAtMsByKey = new();
    private readonly Dictionary<long, long> sourceCooldownUntilMsByKey = new();
    private static readonly HashSet<long> EmptyKeySet = new();

    private ArchimedesWaterNetworkManager? waterManager;
    private ArchimedesScrewConfig.WaterConfig? waterConfig;

    private long nextCentralWaterTickDueMs;
    private long nextRelayCreationDueMs;
    private ArchimedesScrewControllerSchedule lastScheduledCadence = ArchimedesScrewControllerSchedule.HighCadence;
    private int lowCadenceScanSkipsRemaining;
    private int lastEffectiveRelayCap;

    /// <summary>-1 = attribute absent in save (use power/relay-count seed on init).</summary>
    private int deserializedLastEffectiveRelayCap = -1;

    /// <summary>While <see cref="Environment.TickCount64"/> is less than this, drain is skipped (0 = inactive).</summary>
    private long drainUnsupportedGraceUntilMs;

    private long assemblyAnalysisCachedAtMs = long.MinValue;
    private ArchimedesScrewAssemblyAnalyzer.AssemblyStatus? cachedAssemblyAnalysis;

    private bool wasController;
    private BlockPos? lastSeedPos;
    private bool? lastLoggedControllerState;
    private bool? lastLoggedPowerState;
    private string? lastLoggedSeedKey;
    private long nextTruncationPauseLogAtMs;
    private long nextCooldownPruneAtMs;
    private long localCooldownSuppressedTotal;
    private long ownershipChurnTotal;
    private long lastOwnershipChurnSample;
    private readonly IManagedWaterLocalParticipation localParticipation = new DefaultManagedWaterLocalParticipation();

    public string ControllerId { get; private set; } = Guid.NewGuid().ToString("N");

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);

        if (api.Side != EnumAppSide.Server)
        {
            return;
        }

        ArchimedesScrewModSystem modSystem = api.ModLoader.GetModSystem<ArchimedesScrewModSystem>();
        waterManager = modSystem.WaterManager;
        waterConfig = modSystem.Config.Water;

        waterManager?.RegisterScrewBlock(Pos);
        waterManager?.RegisterLoadedController(this);
        Log("Initialized controller {0} for block {1} at {2}", ControllerId, Block?.Code, Pos);

        if (ownedPositions.Count > 0)
        {
            drainUnsupportedGraceUntilMs = Environment.TickCount64 + PostLoadDrainUnsupportedGraceMs;
            waterManager?.RegisterRestoredOwnership(ControllerId, Pos, ownedPositions.Values.ToList());
            Log("Restored {0} owned Archimedes source positions from save (chunk NBT → manager)", ownedPositions.Count);
        }
        if (relayOwnedPositions.Count > 0)
        {
            waterManager?.ReplaceRelaySnapshotForController(ControllerId, relayOwnedPositions.Values.ToList());
            Log("Restored {0} relay-owned source positions from save", relayOwnedPositions.Count);
        }

        ApplyRelayCapStateAfterLoadOrPlacement();

        nextCentralWaterTickDueMs = 0;
        nextRelayCreationDueMs = 0;
        UpdateCentralTickRegistration();
    }

    public override void OnBlockPlaced(ItemStack? byItemStack = null)
    {
        base.OnBlockPlaced(byItemStack);
        ArmDrainUnsupportedGrace(TopologyChangeDrainUnsupportedGraceMs);
        waterManager?.RegisterScrewBlock(Pos);
        waterManager?.RegisterLoadedController(this);
        InvalidateAssemblyAnalysisCache();
        nextCentralWaterTickDueMs = 0;
        nextRelayCreationDueMs = 0;
        UpdateCentralTickRegistration();
        Log("Block placed at {0}: {1}", Pos, Block?.Code);
    }

    public override void OnBlockRemoved()
    {
        Log("Block removed at {0}: {1}", Pos, Block?.Code);
        waterManager?.UnregisterFromCentralWaterTick(ControllerId);
        ReleaseAllManagedWater("block removed");
        waterManager?.UnregisterScrewBlock(Pos);
        waterManager?.RemoveControllerSnapshot(ControllerId);
        base.OnBlockRemoved();
    }

    public override void OnBlockUnloaded()
    {
        Log("Block unloaded at {0}", Pos);
        waterManager?.UnregisterFromCentralWaterTick(ControllerId);
        waterManager?.UnregisterLoadedController(ControllerId);
        base.OnBlockUnloaded();
    }

    /// <summary>
    /// Re-runs chunk → manager ownership sync after <see cref="ArchimedesWaterNetworkManager.Load"/> may have cleared manager state
    /// for controllers that initialized before <c>SaveGameLoaded</c>.
    /// </summary>
    public bool TryReapplyStoredOwnershipToWaterManager(out int sourceCount)
    {
        sourceCount = 0;
        if (Api?.Side != EnumAppSide.Server || ownedPositions.Count == 0)
        {
            return false;
        }

        ArchimedesScrewModSystem modSystem = Api.ModLoader.GetModSystem<ArchimedesScrewModSystem>();
        ArchimedesWaterNetworkManager? mgr = modSystem.WaterManager;
        if (mgr == null)
        {
            return false;
        }

        waterManager = mgr;
        waterConfig ??= modSystem.Config.Water;
        drainUnsupportedGraceUntilMs = Environment.TickCount64 + PostLoadDrainUnsupportedGraceMs;
        mgr.RegisterRestoredOwnership(ControllerId, Pos, ownedPositions.Values.ToList());
        if (relayOwnedPositions.Count > 0)
        {
            mgr.ReplaceRelaySnapshotForController(ControllerId, relayOwnedPositions.Values.ToList());
        }
        sourceCount = ownedPositions.Count;
        return true;
    }

    public void LogDebugControllerStats()
    {
        if (Api == null || Api.Side != EnumAppSide.Server || waterConfig == null)
        {
            return;
        }

        int relayCount = relayOwnedPositions.Count;
        int managedCount = ownedPositions.Count;
        int seedOwnedCount = Math.Max(0, managedCount - relayCount);
        int relayCap = Math.Max(0, waterConfig.MaxRelaySourcesPerController);
        string relayOrderingMode = GetNormalizedRelayCandidateOrderingMode();
        float power = GetCurrentMechanicalPower();
        bool relayDue = IsRelayCreationDue();

        Log(
            "Debug controller stats: pos={0}, managedSources={1}, relaySources={2}, nonRelaySources={3}, relayCapConfigured={4}, relayCapEffective={5}, relayCreateDue={6}, nextRelayCreationDueInMs={7}, relayOrderingMode={8}, cadence={9}, nextControllerTickDueInMs={10}, lowCadenceSkipsRemaining={11}, mechPower={12:0.#####}, cooldownTracked={13}, cooldownSuppressedTotal={14}, ownershipChurnTotal={15}",
            Pos,
            managedCount,
            relayCount,
            seedOwnedCount,
            relayCap,
            lastEffectiveRelayCap,
            relayDue,
            Math.Max(0, nextRelayCreationDueMs - Environment.TickCount64),
            relayOrderingMode,
            lastScheduledCadence,
            Math.Max(0, nextCentralWaterTickDueMs - Environment.TickCount64),
            lowCadenceScanSkipsRemaining,
            power,
            sourceCooldownUntilMsByKey.Count,
            localCooldownSuppressedTotal,
            ownershipChurnTotal
        );
    }

    public string GetCompactControllerStatusLine()
    {
        int relayCount = relayOwnedPositions.Count;
        int managedCount = ownedPositions.Count;
        int graceMs = Math.Max(0, (int)(drainUnsupportedGraceUntilMs - Environment.TickCount64));
        int relayDueMs = Math.Max(0, (int)(nextRelayCreationDueMs - Environment.TickCount64));
        int tickDueMs = Math.Max(0, (int)(nextCentralWaterTickDueMs - Environment.TickCount64));
        string seedText = lastSeedPos == null ? "-" : lastSeedPos.ToString();
        return $"Controller {ControllerId}: seed={seedText}, owned={managedCount}, relays={relayCount}, relayOrderingMode={GetNormalizedRelayCandidateOrderingMode()}, cadence={lastScheduledCadence}, tickDueMs={tickDueMs}, relayDueMs={relayDueMs}, graceMs={graceMs}, cooldownTracked={sourceCooldownUntilMsByKey.Count}";
    }

    public void InvalidateAssemblyAnalysisCache()
    {
        cachedAssemblyAnalysis = null;
        assemblyAnalysisCachedAtMs = long.MinValue;
    }

    internal bool IsCentralWaterTickDue(long nowMs)
    {
        return nowMs >= nextCentralWaterTickDueMs;
    }

    internal void RunCentralWaterTick()
    {
        OnWaterControllerTick();
    }

    private void UpdateCentralTickRegistration()
    {
        if (Api?.Side != EnumAppSide.Server || waterManager == null)
        {
            return;
        }

        if (Block is BlockWaterArchimedesScrew s && s.IsIntakeBlock())
        {
            waterManager.RegisterForCentralWaterTick(this);
        }
        else
        {
            waterManager.UnregisterFromCentralWaterTick(ControllerId);
        }
    }

    private void ScheduleNextWaterTick(ArchimedesScrewControllerSchedule schedule, int fastMs, int idleMs)
    {
        int interval = schedule == ArchimedesScrewControllerSchedule.HighCadence ? fastMs : idleMs;
        nextCentralWaterTickDueMs = Environment.TickCount64 + Math.Max(1, interval);
        lastScheduledCadence = schedule;
    }

    private ArchimedesScrewAssemblyAnalyzer.AssemblyStatus GetOrRefreshAssemblyAnalysis()
    {
        if (Api == null || waterConfig == null)
        {
            return new ArchimedesScrewAssemblyAnalyzer.AssemblyStatus
            {
                IsAssemblyValid = false,
                IsFunctional = false,
                Message = "controller not initialized"
            };
        }

        long now = Environment.TickCount64;
        int ttl = Math.Max(0, waterConfig.AssemblyAnalysisCacheMs);
        if (cachedAssemblyAnalysis != null && now - assemblyAnalysisCachedAtMs < ttl)
        {
            return cachedAssemblyAnalysis;
        }

        ArchimedesScrewAssemblyAnalyzer.AssemblyStatus fresh =
            ArchimedesScrewAssemblyAnalyzer.Analyze(Api.World, Pos, waterConfig.MinimumNetworkSpeed);
        cachedAssemblyAnalysis = fresh;
        assemblyAnalysisCachedAtMs = now;
        return fresh;
    }

    public void NotifyManagedWaterRemoved(BlockPos pos)
    {
        long key = ArchimedesPosKey.Pack(pos);
        bool hadLocalOwned = ownedPositions.Remove(key);
        bool hadLocalRelay = relayOwnedPositions.Remove(key);
        relayPromotedAtMsByKey.Remove(key);

        if (waterManager != null && (hadLocalOwned || hadLocalRelay))
        {
            waterManager.ReleaseOwnedSourceForController(ControllerId, pos);
        }

        MarkDirty();
        Log("Tracked Archimedes source removed externally at {0}; ownership updated", pos);
    }

    internal void TrackAssignedSourceFromManager(BlockPos pos, string reason)
    {
        long key = ArchimedesPosKey.Pack(pos);
        if (ownedPositions.ContainsKey(key))
        {
            return;
        }

        ownedPositions[key] = pos.Copy();
        UpdateSnapshot();
        Log("Assigned source at {0} ({1})", pos, reason);
    }

    internal bool IsTrackingSource(BlockPos pos)
    {
        return ownedPositions.ContainsKey(ArchimedesPosKey.Pack(pos));
    }

    internal bool IsRelayOwnedSource(BlockPos pos)
    {
        return relayOwnedPositions.ContainsKey(ArchimedesPosKey.Pack(pos));
    }

    private void AddRelayOwnership(long key, BlockPos pos, long promotedAtMs)
    {
        relayOwnedPositions[key] = pos;
        relayPromotedAtMsByKey[key] = promotedAtMs;
    }

    private void RemoveRelayOwnership(long key)
    {
        relayOwnedPositions.Remove(key);
        relayPromotedAtMsByKey.Remove(key);
    }

    private void ClearAllRelayOwnership()
    {
        relayOwnedPositions.Clear();
        relayPromotedAtMsByKey.Clear();
    }

    public void ClearOwnedStateAfterPurge()
    {
        ownedPositions.Clear();
        ClearAllRelayOwnership();
        wasController = false;
        lastSeedPos = null;
        lastLoggedSeedKey = null;
        InvalidateAssemblyAnalysisCache();
        MarkDirty();
        Log("Cleared owned source state after purge");
    }

    public void ReleaseAllManagedWater(string reason = "unspecified")
    {
        if (waterManager == null)
        {
            ownedPositions.Clear();
            ClearAllRelayOwnership();
            Log("Release requested for reason '{0}', but water manager is null", reason);
            return;
        }

        HashSet<long> releaseKeys = new();
        List<BlockPos> releaseList = new();
        foreach (BlockPos ownedPos in ownedPositions.Values)
        {
            if (releaseKeys.Add(ArchimedesPosKey.Pack(ownedPos)))
            {
                releaseList.Add(ownedPos.Copy());
            }
        }

        foreach (BlockPos ownedPos in waterManager.GetOwnedSourcePositionsForController(ControllerId))
        {
            if (releaseKeys.Add(ArchimedesPosKey.Pack(ownedPos)))
            {
                releaseList.Add(ownedPos.Copy());
            }
        }

        if (releaseList.Count == 0)
        {
            waterManager.UpdateControllerSnapshot(ControllerId, Pos, Array.Empty<BlockPos>());
            CleanupUnownedManagedSourcesForControllerState();
            Log("Release requested for reason '{0}', but no Archimedes sources were owned", reason);
            return;
        }

        int count = releaseList.Count;
        foreach (BlockPos ownedPos in releaseList)
        {
            long key = ArchimedesPosKey.Pack(ownedPos);
            NoteLocalSourceCooldown(key);
            waterManager.MarkDrainQuarantine(ownedPos);
            waterManager.ReleaseOwnedSourceForController(ControllerId, ownedPos);
            ownershipChurnTotal++;
        }

        ownedPositions.Clear();
        ClearAllRelayOwnership();
        waterManager.UpdateControllerSnapshot(ControllerId, Pos, Array.Empty<BlockPos>());
        CleanupUnownedManagedSourcesForControllerState();
        MarkDirty();
        Log("Released {0} Archimedes source blocks because {1}", count, reason);
    }

    public bool TryGetActiveSeedState(out ArchimedesOutletState state)
    {
        state = default;
        ControllerEvaluation evaluation = EvaluateController();
        if (!evaluation.IsController ||
            !evaluation.IsPowered ||
            evaluation.FamilyId == null ||
            evaluation.SeedPos == null)
        {
            return false;
        }

        state = new ArchimedesOutletState(ControllerId, evaluation.SeedPos.Copy(), evaluation.FamilyId);
        return true;
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);

        ControllerId = tree.GetString(ControllerIdKey, ControllerId);
        wasController = tree.GetBool(WasControllerKey, false);

        byte[]? ownedBytes = tree.GetBytes(OwnedPositionsKey);
        ownedPositions.Clear();
        if (ownedBytes != null)
        {
            foreach (BlockPos pos in SafeDecodePositionArray(ownedBytes, OwnedPositionsKey))
            {
                ownedPositions[ArchimedesPosKey.Pack(pos)] = pos;
            }
        }

        byte[]? relayBytes = tree.GetBytes(RelayPositionsKey);
        ClearAllRelayOwnership();
        if (relayBytes != null)
        {
            foreach (BlockPos pos in SafeDecodePositionArray(relayBytes, RelayPositionsKey))
            {
                long key = ArchimedesPosKey.Pack(pos);
                AddRelayOwnership(key, pos, promotedAtMs: 0L);
            }
        }

        byte[]? seedBytes = tree.GetBytes(LastSeedKey);
        lastSeedPos = seedBytes == null ? null : SafeDecodeSinglePos(seedBytes, LastSeedKey);

        deserializedLastEffectiveRelayCap = tree.GetInt(LastEffectiveRelayCapKey, -1);
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);

        tree.SetString(ControllerIdKey, ControllerId);
        tree.SetBool(WasControllerKey, wasController);
        tree.SetBytes(OwnedPositionsKey, SerializerUtil.Serialize(ArchimedesPositionCodec.EncodePositions(ownedPositions.Values)));
        tree.SetBytes(RelayPositionsKey, SerializerUtil.Serialize(ArchimedesPositionCodec.EncodePositions(relayOwnedPositions.Values)));
        tree.SetInt(LastEffectiveRelayCapKey, lastEffectiveRelayCap);

        if (lastSeedPos == null)
        {
            tree.RemoveAttribute(LastSeedKey);
        }
        else
        {
            tree.SetBytes(LastSeedKey, SerializerUtil.Serialize(new[] { lastSeedPos.X, lastSeedPos.Y, lastSeedPos.Z }));
        }
    }

    private void OnWaterControllerTick()
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("controller.tick");
        if (Api == null || Api.Side != EnumAppSide.Server || waterManager == null || waterConfig == null)
        {
            return;
        }

        int fastMs = waterConfig.FastTickMs;
        int idleMs = waterConfig.IdleTickMs;

        ControllerEvaluation evaluation = EvaluateController();
        LogStateChange("controller validity", ref lastLoggedControllerState, evaluation.IsController);
        LogStateChange("powered state", ref lastLoggedPowerState, evaluation.IsPowered);

        if (!evaluation.IsController)
        {
            wasController = false;
            HandleInvalidControllerState(evaluation, fastMs, idleMs, forceDrainWhenInvalid: true);
            return;
        }

        wasController = true;

        if (!evaluation.IsPowered || evaluation.FamilyId == null || evaluation.SeedPos == null)
        {
            // Unpowered draining releases owned managed sources each tick. Keep seizure active for vanilla
            // sources (e.g. player bucket placements) but disable managed self-sustaining adoption here to
            // prevent immediate re-ownership thrash of freshly drained cells.
            // Do NOT call EnsureSeedSource here: it refills the outlet via SetManagedSource and blocks drain.
            if (!evaluation.IsPowered &&
                evaluation.FamilyId != null &&
                evaluation.SeedPos != null &&
                waterManager != null &&
                waterConfig != null)
            {
                // The seize result was previously assigned to a local that nothing read; downstream
                // scheduling for the unpowered branch is decided entirely by HandleInvalidControllerState.
                waterManager.SeizeVanillaSourcesInConnectedFamilyFluid(
                    BuildDrainProbeOriginPositions(evaluation.SeedPos),
                    evaluation.FamilyId,
                    ControllerId,
                    adoptManagedSelfSustaining: false);
            }

            HandleInvalidControllerState(evaluation, fastMs, idleMs, forceDrainWhenInvalid: false);
            return;
        }

        BlockPos seedPos = evaluation.SeedPos.Copy();
        string familyId = evaluation.FamilyId;
        string seedKey = ArchimedesPosKey.ToDebugString(ArchimedesPosKey.Pack(seedPos));
        if (lastLoggedSeedKey != seedKey)
        {
            lastLoggedSeedKey = seedKey;
            Log("Seed/output position is now {0}", seedPos);
        }

        lastSeedPos = seedPos.Copy();
        PruneExpiredLocalCooldowns();

        bool ensuredSeed = EnsureSeedSource(seedPos, familyId);
        ConnectedManagedWaterResult connectedResult = waterManager.CollectConnectedManagedWaterCachedDetailed(seedPos);
        bool isTruncated = connectedResult.IsTruncated;
        int convertedVanilla = isTruncated
            ? 0
            : waterManager.ConvertAdjacentVanillaSourcesIteratively(
                seedPos,
                waterConfig.MaxVanillaConversionPasses,
                ControllerId
            );

        convertedVanilla += waterManager.SeizeVanillaSourcesInConnectedFamilyFluid(
            BuildDrainProbeOriginPositions(seedPos),
            familyId,
            ControllerId);

        bool canSkipConnectivityScan =
            CanSkipConnectivityScan(ensuredSeed, convertedVanilla);

        if (canSkipConnectivityScan)
        {
            // Low-cadence skips relay/full scan but must still drain: otherwise consistent owned
            // sources (debugwater green) can sit forever with no release tick.
            // Skip drain when BFS truncated - we would otherwise release relays beyond the BFS budget.
            if (!isTruncated)
            {
                List<ArchimedesOutletState> skipPathSeeds =
                    ResolveSupportingSeeds(seedPos, connectedResult.VisitedKeys);
                DrainUnsupportedSources(skipPathSeeds, connectedResult.VisitedKeys, string.Empty);
            }
            else
            {
                ArchimedesPerf.AddCount("controller.drainUnsupported.skippedTruncated");
            }
            lowCadenceScanSkipsRemaining--;
            ArchimedesPerf.AddCount("controller.connectivityScan.skipped");
            ScheduleNextWaterTick(ArchimedesScrewControllerSchedule.LowCadence, fastMs, idleMs);
            ArchimedesPerf.MaybeFlush(Api);
            return;
        }

        HashSet<long> connectedWaterKeys = connectedResult.VisitedKeys;
        Dictionary<long, BlockPos> connectedWater = connectedResult.PositionsByKey;
        ReconcileRelayOwnedPositions();
        int relayCap = ComputeEffectiveRelayCap(evaluation.CurrentPower);
        int relayWorkBudget = Math.Max(1, waterConfig.MaxRelayPromotionsPerTick);
        (int relayCreated, int relayTrimmed) = RunRelayMaintenance(
            seedPos,
            familyId,
            connectedResult,
            relayCap,
            relayWorkBudget,
            fastMs,
            isTruncated
        );

        int removedDisconnected = 0;
        List<ArchimedesOutletState> supportingSeeds = ResolveSupportingSeeds(seedPos, connectedWaterKeys);
        if (isTruncated)
        {
            // Skip drain when BFS could not enumerate the full component: any "unsupported" entries
            // we'd find are likely just beyond the BFS budget, not genuinely disconnected. They will
            // be reconsidered next tick.
            ArchimedesPerf.AddCount("controller.drainUnsupported.skippedTruncated");
        }
        else
        {
            removedDisconnected = DrainUnsupportedSources(
                supportingSeeds,
                connectedWaterKeys,
                string.Empty);
        }

        if (ensuredSeed)
        {
            UpdateSnapshot();
        }

        ArchimedesScrewControllerSchedule nextSchedule = ResolveNextSchedule(
            ensuredSeed,
            convertedVanilla,
            removedDisconnected,
            relayCreated,
            relayTrimmed,
            relayCap
        );
        long churnDelta = Math.Max(0, ownershipChurnTotal - lastOwnershipChurnSample);
        lastOwnershipChurnSample = ownershipChurnTotal;
        ArchimedesPerf.AddCount("controller.localCooldown.tracked", sourceCooldownUntilMsByKey.Count);
        ArchimedesPerf.AddCount("controller.localCooldown.suppressedTotal", localCooldownSuppressedTotal);
        ArchimedesPerf.AddCount("controller.ownershipChurn.total", ownershipChurnTotal);
        ArchimedesPerf.AddCount("controller.ownershipChurn.perTick", churnDelta);
        lowCadenceScanSkipsRemaining = nextSchedule == ArchimedesScrewControllerSchedule.LowCadence
            ? LowCadenceConnectivityScanStride - 1
            : 0;
        ScheduleNextWaterTick(nextSchedule, fastMs, idleMs);

        ArchimedesPerf.MaybeFlush(Api);
    }

    private bool CanSkipConnectivityScan(bool ensuredSeed, int convertedVanilla)
    {
        return lastScheduledCadence == ArchimedesScrewControllerSchedule.LowCadence &&
               convertedVanilla == 0 &&
               !ensuredSeed &&
               lowCadenceScanSkipsRemaining > 0;
    }

    private (int RelayCreated, int RelayTrimmed) RunRelayMaintenance(
        BlockPos seedPos,
        string familyId,
        ConnectedManagedWaterResult connectedResult,
        int relayCap,
        int relayWorkBudget,
        int fastMs,
        bool isTruncated
    )
    {
        int relayCreated = 0;
        int relayTrimmed = 0;
        bool relayDue = IsRelayCreationDue();
        if (waterConfig!.EnableRelaySources && !isTruncated)
        {
            if (relayDue)
            {
                relayCreated = CreateRelaySources(familyId, connectedResult, relayCap, relayWorkBudget);
                ScheduleNextRelayCreationTick(fastMs);
            }

            relayTrimmed = TrimRelaySourcesToCap(seedPos, relayCap, relayWorkBudget);
        }
        else if (isTruncated)
        {
            LogTruncationPause(seedPos, connectedResult.VisitedManagedCount);
        }

        return (relayCreated, relayTrimmed);
    }

    private ArchimedesScrewControllerSchedule ResolveNextSchedule(
        bool ensuredSeed,
        int convertedVanilla,
        int removedDisconnected,
        int relayCreated,
        int relayTrimmed,
        int relayCap
    )
    {
        bool relayExpansionPending =
            waterConfig != null &&
            waterConfig.EnableRelaySources &&
            relayOwnedPositions.Count < relayCap;
        bool busyWork =
            ensuredSeed ||
            convertedVanilla > 0 ||
            removedDisconnected > 0 ||
            relayCreated > 0 ||
            relayTrimmed > 0 ||
            relayExpansionPending ||
            ownedPositions.Count == 0;
        return busyWork
            ? ArchimedesScrewControllerSchedule.HighCadence
            : ArchimedesScrewControllerSchedule.LowCadence;
    }

    private ControllerEvaluation EvaluateController()
    {
        if (Api == null || waterManager == null || waterConfig == null)
        {
            return new ControllerEvaluation(false, false, 0f, "controller not initialized", null, null);
        }

        BlockWaterArchimedesScrew? screwBlock = Block as BlockWaterArchimedesScrew;
        if (screwBlock == null || !screwBlock.IsIntakeBlock())
        {
            return new ControllerEvaluation(false, false, 0f, "block is not an intake controller", null, null);
        }

        ArchimedesScrewAssemblyAnalyzer.AssemblyStatus assemblyStatus = GetOrRefreshAssemblyAnalysis();
        if (!assemblyStatus.IsAssemblyValid)
        {
            return new ControllerEvaluation(false, false, 0f, $"assembly invalid: {assemblyStatus.Message}", null, assemblyStatus.OutputPos?.Copy());
        }

        Block intakeFluid = Api.World.BlockAccessor.GetBlock(Pos, BlockLayersAccess.Fluid);
        if (!waterManager.TryResolveIntakeWaterFamily(intakeFluid, out string familyId))
        {
            return new ControllerEvaluation(false, assemblyStatus.IsPowered, 0f, $"unsupported intake fluid: {intakeFluid.Code}", null, assemblyStatus.OutputPos?.Copy());
        }

        BlockPos seedPos = assemblyStatus.OutputPos?.Copy() ?? GetSeedPosition();
        if (!CanUseSeedPosition(seedPos))
        {
            return new ControllerEvaluation(false, assemblyStatus.IsPowered, 0f, $"seed/output position {seedPos} is blocked", familyId, seedPos);
        }

        float currentPower = GetCurrentMechanicalPower();
        return new ControllerEvaluation(true, assemblyStatus.IsPowered, currentPower, string.Empty, familyId, seedPos);
    }

    private List<ArchimedesOutletState> ResolveSupportingSeeds(BlockPos seedPos, HashSet<long> connectedKeySet)
    {
        List<ArchimedesOutletState> supporting = new();
        HashSet<string> seenControllerIds = new(StringComparer.Ordinal);

        foreach (ArchimedesOutletState activeSeed in waterManager!.GetActiveSeedStatesCached())
        {
            if (!seenControllerIds.Add(activeSeed.ControllerId))
            {
                continue;
            }

            if (activeSeed.ControllerId == ControllerId ||
                activeSeed.SeedPos.Equals(seedPos) ||
                connectedKeySet.Contains(ArchimedesPosKey.Pack(activeSeed.SeedPos)))
            {
                supporting.Add(new ArchimedesOutletState(activeSeed.ControllerId, activeSeed.SeedPos.Copy(), activeSeed.FamilyId));
            }
        }

        if (supporting.Count == 0)
        {
            supporting.Add(new ArchimedesOutletState(ControllerId, seedPos.Copy(), string.Empty));
        }

        return supporting;
    }

    private List<BlockPos> BuildDrainProbeOriginPositions(BlockPos primarySeed)
    {
        // Consumers (DrainUnsupportedSources, SeizeVanillaSourcesInConnectedFamilyFluid) treat the
        // returned list as read-only and don't retain references after returning. Reusing a single
        // scratch list and skipping defensive .Copy() avoids per-tick allocation proportional to
        // ownedPositions.Count for a controller's hot path.
        drainProbeOriginScratch.Clear();
        drainProbeOriginScratch.Add(primarySeed);
        if (lastSeedPos != null)
        {
            drainProbeOriginScratch.Add(lastSeedPos);
        }

        foreach (BlockPos p in ownedPositions.Values)
        {
            drainProbeOriginScratch.Add(p);
        }

        return drainProbeOriginScratch;
    }

    private bool EnsureSeedSource(BlockPos seedPos, string familyId)
    {
        if (waterManager == null)
        {
            return false;
        }

        BlockPos? sourcePos = null;
        BlockFacing? sourceFacing = null;
        BlockPos topPos = FindTopScrewPos();
        if (Api.World.BlockAccessor.GetBlock(topPos) is BlockWaterArchimedesScrew topScrew &&
            topScrew.IsOutletBlock())
        {
            BlockFacing? facing = topScrew.GetPortFacing();
            if (facing != null)
            {
                sourcePos = topPos;
                sourceFacing = facing;
            }
        }

        bool changed = waterManager.AssignOwnedSourceForController(
            ControllerId,
            seedPos,
            familyId,
            sourcePos,
            sourceFacing
        );
        ownedPositions[ArchimedesPosKey.Pack(seedPos)] = seedPos.Copy();
        return changed;
    }

    /// <summary>
    /// Hysteresis-free cap from mechanical power (matches the target inside <see cref="ComputeEffectiveRelayCap"/>).
    /// </summary>
    private int ComputeInstantaneousRelayCapForPower(float currentPower)
    {
        if (waterConfig == null || !waterConfig.EnableRelaySources)
        {
            return 0;
        }

        int configuredMax = Math.Max(0, waterConfig.MaxRelaySourcesPerController);
        if (configuredMax == 0)
        {
            return 0;
        }

        float minPower = Math.Max(0f, waterConfig.MinimumNetworkSpeed);
        float maxPower = Math.Max(minPower + 0.000001f, waterConfig.RequiredMechPowerForMaxRelay);
        if (currentPower <= minPower)
        {
            return 0;
        }

        float normalized = Math.Clamp((currentPower - minPower) / (maxPower - minPower), 0f, 1f);
        int targetCap = (int)MathF.Floor(configuredMax * normalized);
        return Math.Clamp(targetCap, 0, configuredMax);
    }

    /// <summary>
    /// Restores relay trim cap from save and/or prevents immediate mass trim when <see cref="lastEffectiveRelayCap"/> was 0
    /// but <see cref="relayOwnedPositions"/> still lists many saved relays.
    /// </summary>
    private void ApplyRelayCapStateAfterLoadOrPlacement()
    {
        if (waterConfig == null || !waterConfig.EnableRelaySources)
        {
            lastEffectiveRelayCap = 0;
            deserializedLastEffectiveRelayCap = -1;
            return;
        }

        int configuredMax = Math.Max(0, waterConfig.MaxRelaySourcesPerController);
        int relayCount = relayOwnedPositions.Count;
        bool hadPersistedCap = deserializedLastEffectiveRelayCap >= 0;

        if (hadPersistedCap)
        {
            lastEffectiveRelayCap = Math.Clamp(deserializedLastEffectiveRelayCap, 0, configuredMax);
        }
        else
        {
            lastEffectiveRelayCap = ComputeInstantaneousRelayCapForPower(GetCurrentMechanicalPower());
        }

        int beforeFloor = lastEffectiveRelayCap;
        lastEffectiveRelayCap = Math.Min(configuredMax, Math.Max(relayCount, lastEffectiveRelayCap));
        deserializedLastEffectiveRelayCap = -1;

        if (relayCount > 0)
        {
            Log(
                "Relay cap init: effective={0} (floorSavedRelays from {1}), configuredMax={2}, relayMarkers={3}, persistedNbt={4}",
                lastEffectiveRelayCap,
                beforeFloor,
                configuredMax,
                relayCount,
                hadPersistedCap);
        }
    }

    private int ComputeEffectiveRelayCap(float currentPower)
    {
        if (waterConfig == null || !waterConfig.EnableRelaySources)
        {
            lastEffectiveRelayCap = 0;
            return 0;
        }

        int configuredMax = Math.Max(0, waterConfig.MaxRelaySourcesPerController);
        if (configuredMax == 0)
        {
            lastEffectiveRelayCap = 0;
            return 0;
        }

        float minPower = Math.Max(0f, waterConfig.MinimumNetworkSpeed);
        float maxPower = Math.Max(minPower + 0.000001f, waterConfig.RequiredMechPowerForMaxRelay);
        if (currentPower <= minPower)
        {
            lastEffectiveRelayCap = 0;
            return 0;
        }

        float normalized = Math.Clamp((currentPower - minPower) / (maxPower - minPower), 0f, 1f);
        int targetCap = (int)MathF.Floor(configuredMax * normalized);
        targetCap = Math.Clamp(targetCap, 0, configuredMax);

        if (targetCap > lastEffectiveRelayCap)
        {
            float upThreshold = Math.Clamp(((lastEffectiveRelayCap + 1f) / configuredMax) + Math.Max(0f, waterConfig.RelayPowerHysteresisPct), 0f, 1f);
            if (normalized >= upThreshold)
            {
                lastEffectiveRelayCap = Math.Min(configuredMax, lastEffectiveRelayCap + 1);
            }
        }
        else if (targetCap < lastEffectiveRelayCap)
        {
            float downThreshold = Math.Clamp((lastEffectiveRelayCap / (float)configuredMax) - Math.Max(0f, waterConfig.RelayPowerHysteresisPct), 0f, 1f);
            if (normalized <= downThreshold)
            {
                lastEffectiveRelayCap = Math.Max(0, lastEffectiveRelayCap - 1);
            }
        }

        return lastEffectiveRelayCap;
    }

    private int CreateRelaySources(
        string familyId,
        ConnectedManagedWaterResult connectedResult,
        int relayCap,
        int perTickBudget)
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("controller.relayPass");
        if (Api == null || waterManager == null || waterConfig == null || relayCap <= relayOwnedPositions.Count)
        {
            return 0;
        }

        Dictionary<long, BlockPos> connectedWater = connectedResult.PositionsByKey;
        // Distances are recorded inline by the manager's BFS at component-collection time and cached
        // alongside the component, eliminating the second BFS this method used to run. DistanceByKey
        // is anchored at connectedResult.StartPos (the first caller that populated the cache this
        // tick), which may or may not equal this controller's seedPos - see plan phase 3 exit
        // criteria: relay selection is the same "modulo the now-shared distance map".
        Dictionary<long, int>? distanceByKey = connectedResult.DistanceByKey;
        if (distanceByKey == null || distanceByKey.Count == 0)
        {
            return 0;
        }

        int budget = Math.Max(1, perTickBudget);
        int maxCreateAllowed = Math.Min(budget, relayCap - relayOwnedPositions.Count);
        if (maxCreateAllowed <= 0)
        {
            return 0;
        }

        bool useRandomBucket = IsRandomWithinDistanceBucketMode();
        IEnumerable<RelayCandidateEntry> orderedCandidates = useRandomBucket
            ? EnumerateRelayCandidatesRandomBucket(distanceByKey, connectedWater, BuildRelayOrderingSeed())
            : SelectTopKRelayCandidatesDeterministic(distanceByKey, connectedWater, maxCreateAllowed);

        int candidatesExamined = 0;
        int rejectedCooldown = 0;
        int rejectedOtherOwner = 0;
        int rejectedNotCandidate = 0;
        int rejectedAssignFailed = 0;
        int created = 0;
        foreach (RelayCandidateEntry candidate in orderedCandidates)
        {
            if (created >= maxCreateAllowed)
            {
                break;
            }

            BlockPos pos = candidate.Pos;
            long key = candidate.Key;
            candidatesExamined++;
            if (!IsRelayCreationCandidate(pos))
            {
                rejectedNotCandidate++;
                continue;
            }

            if (waterManager.TryGetSourceOwner(pos, out string ownerId) &&
                !string.Equals(ownerId, ControllerId, StringComparison.Ordinal))
            {
                // Skip cells already owned by a different controller. The previous "takeover when
                // prior owner isn't loaded" branch was dead code: AssignRelaySourceForController ->
                // AssignOwnedSourceForController rejects re-assignment regardless of loaded state,
                // so the assign would just fail later. Reject up front to avoid the wasted call.
                rejectedOtherOwner++;
                continue;
            }

            if (IsLocalSourceCooldownActive(key))
            {
                rejectedCooldown++;
                localCooldownSuppressedTotal++;
                continue;
            }

            if (!waterManager.AssignRelaySourceForController(ControllerId, pos, familyId))
            {
                rejectedAssignFailed++;
                continue;
            }

            ownedPositions[key] = pos.Copy();
            AddRelayOwnership(key, pos.Copy(), Environment.TickCount64);
            created++;
            ownershipChurnTotal++;
        }

        ArchimedesPerf.AddCount("controller.relayCandidates", candidatesExamined);
        ArchimedesPerf.AddCount("controller.relayPromotions", created);
        ArchimedesPerf.AddCount("controller.relayCandidates.rejectedCooldown", rejectedCooldown);
        ArchimedesPerf.AddCount("controller.relayCandidates.rejectedOtherOwner", rejectedOtherOwner);
        ArchimedesPerf.AddCount("controller.relayCandidates.rejectedNotCandidate", rejectedNotCandidate);
        ArchimedesPerf.AddCount("controller.relayCandidates.rejectedAssignFailed", rejectedAssignFailed);
        if (created > 0)
        {
            UpdateSnapshot();
        }

        return created;
    }

    /// <summary>
    /// Single-pass top-K selection over <paramref name="distanceByKey"/> using a bounded max-heap of
    /// size <paramref name="maxCreateAllowed"/>. Cheap pre-filter (distance &gt; 0, present in the
    /// connected map, not already promoted) prunes the candidate set before the heap. The heap is
    /// then drained and sorted ascending so callers iterate best-first.
    /// </summary>
    private List<RelayCandidateEntry> SelectTopKRelayCandidatesDeterministic(
        Dictionary<long, int> distanceByKey,
        Dictionary<long, BlockPos> connectedWater,
        int maxCreateAllowed)
    {
        relayTopKScratch.Clear();
        relayCandidateOrderedScratch.Clear();
        foreach ((long key, int distance) in distanceByKey)
        {
            if (distance <= 0)
            {
                continue;
            }

            if (!connectedWater.TryGetValue(key, out BlockPos? pos))
            {
                continue;
            }

            if (relayOwnedPositions.ContainsKey(key))
            {
                continue;
            }

            RelayCandidateRank rank = new(distance, -pos.Y, key);
            if (relayTopKScratch.Count < maxCreateAllowed)
            {
                relayTopKScratch.Enqueue(key, rank);
                continue;
            }

            // Reverse comparer makes Peek() return the *worst* of the current top-K; only enqueue
            // when the incoming candidate would displace it.
            if (relayTopKScratch.TryPeek(out _, out RelayCandidateRank worst) &&
                rank.CompareTo(worst) < 0)
            {
                relayTopKScratch.EnqueueDequeue(key, rank);
            }
        }

        while (relayTopKScratch.TryDequeue(out long key, out RelayCandidateRank rank))
        {
            if (!connectedWater.TryGetValue(key, out BlockPos? pos))
            {
                continue;
            }

            relayCandidateOrderedScratch.Add(new RelayCandidateEntry(key, pos, rank));
        }

        // Heap dequeue order is reverse (worst-first) under our comparer; sort to natural order.
        relayCandidateOrderedScratch.Sort(static (a, b) => a.Rank.CompareTo(b.Rank));
        return relayCandidateOrderedScratch;
    }

    /// <summary>
    /// Random-within-distance-bucket enumeration: walks distance buckets in ascending order, shuffles
    /// each bucket with the supplied seed, and yields entries lazily so consumers can stop after the
    /// per-tick budget is filled (the original eager GroupBy materialised every bucket up front).
    /// </summary>
    private IEnumerable<RelayCandidateEntry> EnumerateRelayCandidatesRandomBucket(
        Dictionary<long, int> distanceByKey,
        Dictionary<long, BlockPos> connectedWater,
        int randomSeed)
    {
        Random random = new(randomSeed);
        // Group by distance ascending; each bucket is shuffled lazily on demand. GroupBy still
        // performs a single eager pass over the source, but per-bucket shuffling now happens only
        // for the buckets the consumer actually walks.
        foreach (IGrouping<int, KeyValuePair<long, int>> bucket in distanceByKey
                     .Where(p => p.Value > 0)
                     .GroupBy(p => p.Value)
                     .OrderBy(g => g.Key))
        {
            List<KeyValuePair<long, int>> shuffled = bucket.ToList();
            for (int i = shuffled.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }

            foreach (KeyValuePair<long, int> pair in shuffled)
            {
                long key = pair.Key;
                if (relayOwnedPositions.ContainsKey(key))
                {
                    continue;
                }

                if (!connectedWater.TryGetValue(key, out BlockPos? pos))
                {
                    continue;
                }

                yield return new RelayCandidateEntry(key, pos, new RelayCandidateRank(pair.Value, -pos.Y, key));
            }
        }
    }

    private int TrimRelaySourcesToCap(BlockPos seedPos, int relayCap, int perTickBudget)
    {
        if (waterManager == null || relayOwnedPositions.Count <= relayCap)
        {
            return 0;
        }

        int overflow = relayOwnedPositions.Count - relayCap;
        int removeCount = Math.Min(Math.Max(1, perTickBudget), overflow);
        // Age-based trim: newest promotions (largest PromotedAtMs) are released first; legacy
        // entries (PromotedAtMs == 0) sort to the bottom and are kept preferentially. Bounded
        // min-heap of size removeCount collects the K worst entries in a single pass (no full sort).
        trimTopKScratch.Clear();
        trimOrderedScratch.Clear();
        foreach ((long key, BlockPos pos) in relayOwnedPositions)
        {
            long promotedAt = relayPromotedAtMsByKey.TryGetValue(key, out long ts) ? ts : 0L;
            long distSq = ArchimedesPositionCodec.DistanceSquared(pos, seedPos);
            TrimCandidateRank rank = new(promotedAt, distSq);
            if (trimTopKScratch.Count < removeCount)
            {
                trimTopKScratch.Enqueue(pos, rank);
                continue;
            }

            // Default comparer = min-heap; Peek returns the smallest of the current top-K.
            // Only swap when the new candidate is strictly worse (larger rank) than the incumbent.
            if (trimTopKScratch.TryPeek(out _, out TrimCandidateRank smallest) &&
                rank.CompareTo(smallest) > 0)
            {
                trimTopKScratch.EnqueueDequeue(pos, rank);
            }
        }

        while (trimTopKScratch.TryDequeue(out BlockPos? pos, out _))
        {
            trimOrderedScratch.Add(pos);
        }

        foreach (BlockPos pos in trimOrderedScratch)
        {
            long key = ArchimedesPosKey.Pack(pos);
            RemoveRelayOwnership(key);
            ownedPositions.Remove(key);
            NoteLocalSourceCooldown(key);
            waterManager.MarkDrainQuarantine(pos);
            waterManager.ReleaseRelaySourceForController(ControllerId, pos);
            ownershipChurnTotal++;
        }

        int trimmed = trimOrderedScratch.Count;
        ArchimedesPerf.AddCount("controller.relayTrimmed", trimmed);
        if (trimmed > 0)
        {
            UpdateSnapshot();
        }

        return trimmed;
    }

    private bool IsRelayCreationCandidate(BlockPos pos)
    {
        if (Api == null || waterManager == null)
        {
            return false;
        }

        return localParticipation.IsRelayCreationCandidate(Api.World, pos, waterManager);
    }

    private void HandleInvalidControllerState(
        ControllerEvaluation evaluation,
        int fastMs,
        int idleMs,
        bool forceDrainWhenInvalid)
    {
        TryArmTopologyChangeGrace(evaluation.FailureReason);
        bool forceDrain = forceDrainWhenInvalid && ShouldForceDrainWhenControllerInvalid(evaluation.FailureReason);
        if (forceDrain)
        {
            ReconcileOwnedPositionsFromManager();
        }

        // Post-load grace (12s) is a defence against fluid settling on freshly-loaded *active* networks.
        // Pure-unpowered controllers (empty FailureReason) have no active fluid dynamics to protect;
        // bypassing the grace here prevents derelict screws from leaving stale owned sources in the world
        // when their chunk unloads before the 12s grace expires.
        bool bypassGraceForPureUnpowered = !forceDrain
            && string.IsNullOrWhiteSpace(evaluation.FailureReason);

        // Derelict short-circuit: if there is nothing owned, no seed memory, and no prior controller
        // history, drain/orphan-cleanup cannot find anything to act on. Skip directly to scheduling.
        bool hasAnyStateToRecover = ownedPositions.Count > 0 || lastSeedPos != null || wasController;
        int removed = 0;
        int orphanRemoved = 0;
        if (forceDrain || hasAnyStateToRecover)
        {
            removed = DrainUnsupportedSources(
                Array.Empty<ArchimedesOutletState>(),
                EmptyKeySet,
                evaluation.FailureReason,
                ignoreGrace: forceDrain || bypassGraceForPureUnpowered);
            // Orphan managed-water cleanup must follow the same bypass rule; otherwise post-load grace
            // blocks unowned-managed removals for the same 12s window, leaving orphan fluid in the world.
            orphanRemoved = CleanupUnownedManagedSourcesForControllerState(
                ignoreGrace: forceDrain || bypassGraceForPureUnpowered);
        }

        ArchimedesScrewControllerSchedule schedule = ownedPositions.Count > 0 || removed > 0
            ? ArchimedesScrewControllerSchedule.HighCadence
            : ArchimedesScrewControllerSchedule.LowCadence;
        if (orphanRemoved > 0)
        {
            schedule = ArchimedesScrewControllerSchedule.HighCadence;
        }

        ScheduleNextWaterTick(schedule, fastMs, idleMs);
    }

    private bool IsRandomWithinDistanceBucketMode()
    {
        return string.Equals(
            GetNormalizedRelayCandidateOrderingMode(),
            "randomwithindistancebucket",
            StringComparison.Ordinal
        );
    }

    private string GetNormalizedRelayCandidateOrderingMode()
    {
        string mode = waterConfig?.RelayCandidateOrderingMode ?? "deterministic";
        mode = mode.Trim();
        if (string.IsNullOrEmpty(mode))
        {
            return "deterministic";
        }

        return mode.Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
    }

    private int BuildRelayOrderingSeed()
    {
        int tickBucket = (int)(Environment.TickCount64 / Math.Max(1, waterConfig?.FastTickMs ?? 250));
        return HashCode.Combine(ControllerId, tickBucket, relayOwnedPositions.Count, ownedPositions.Count);
    }

    private List<BlockPos> OrderUnsupportedReleaseCandidates(List<BlockPos> releaseCandidates, List<BlockPos> origins)
    {
        releaseDecoratedScratch.Clear();
        for (int i = 0; i < releaseCandidates.Count; i++)
        {
            BlockPos pos = releaseCandidates[i];
            releaseDecoratedScratch.Add((MinDistanceSquared(pos, origins), pos));
        }

        releaseDecoratedScratch.Sort(static (left, right) =>
        {
            int distCmp = left.MinDistSq.CompareTo(right.MinDistSq);
            if (distCmp != 0)
            {
                return distCmp;
            }

            BlockPos lp = left.Pos;
            BlockPos rp = right.Pos;
            int yCmp = rp.Y.CompareTo(lp.Y);
            if (yCmp != 0)
            {
                return yCmp;
            }

            int xCmp = rp.X.CompareTo(lp.X);
            if (xCmp != 0)
            {
                return xCmp;
            }

            return rp.Z.CompareTo(lp.Z);
        });

        releaseCandidates.Clear();
        for (int i = 0; i < releaseDecoratedScratch.Count; i++)
        {
            releaseCandidates.Add(releaseDecoratedScratch[i].Pos);
        }

        return releaseCandidates;
    }

    private void NoteLocalSourceCooldown(long key)
    {
        sourceCooldownUntilMsByKey[key] = Environment.TickCount64 + LocalSourceCooldownMs;
    }

    private bool IsLocalSourceCooldownActive(long key)
    {
        if (!sourceCooldownUntilMsByKey.TryGetValue(key, out long until))
        {
            return false;
        }

        return Environment.TickCount64 < until;
    }

    private void PruneExpiredLocalCooldowns()
    {
        long now = Environment.TickCount64;
        if (now < nextCooldownPruneAtMs || sourceCooldownUntilMsByKey.Count == 0)
        {
            return;
        }

        foreach (long key in sourceCooldownUntilMsByKey.Keys.ToList())
        {
            if (sourceCooldownUntilMsByKey[key] <= now)
            {
                sourceCooldownUntilMsByKey.Remove(key);
            }
        }

        nextCooldownPruneAtMs = now + CooldownPruneIntervalMs;
    }

    private bool IsRelayCreationDue()
    {
        return Environment.TickCount64 >= nextRelayCreationDueMs;
    }

    private void ScheduleNextRelayCreationTick(int intervalMs)
    {
        nextRelayCreationDueMs = Environment.TickCount64 + Math.Max(1, intervalMs);
    }

    private void ReconcileRelayOwnedPositions()
    {
        if (Api == null || waterManager == null || relayOwnedPositions.Count == 0)
        {
            return;
        }

        // Two-pass: first walk relayOwnedPositions in-place to count stale keys (those whose owning
        // BE state has dropped them from ownedPositions). Stale entries are rare in steady state
        // (existing removal call sites in TrimRelaySourcesToCap and DrainUnsupportedSources keep
        // them in sync), so we skip allocating any scratch list when nothing needs reconciling.
        int staleCount = 0;
        foreach (long key in relayOwnedPositions.Keys)
        {
            if (!ownedPositions.ContainsKey(key))
            {
                staleCount++;
            }
        }

        if (staleCount == 0)
        {
            return;
        }

        const int maxSampleCount = 5;
        int removedInvalidKeys = 0;
        List<string> invalidSamples = new();
        // Allocate the removal scratch only when at least one stale key is present.
        List<long> staleKeys = new(staleCount);
        foreach (long key in relayOwnedPositions.Keys)
        {
            if (!ownedPositions.ContainsKey(key))
            {
                staleKeys.Add(key);
            }
        }

        foreach (long key in staleKeys)
        {
            // Keep relay markers stable across temporary source/flow transitions,
            // especially around save/load when fluid state can lag ownership restore.
            // Remove only when ownership no longer includes this relay position.
            RemoveRelayOwnership(key);
            removedInvalidKeys++;
            if (invalidSamples.Count < maxSampleCount)
            {
                invalidSamples.Add(ArchimedesPosKey.ToDebugString(key));
            }
        }

        if (removedInvalidKeys > 0)
        {
            string sampleText = invalidSamples.Count > 0 ? string.Join(", ", invalidSamples) : "—";
            Api.Logger.Warning(
                "{0} [controller:{1}] ReconcileRelayOwnedPositions removed {2} entr(y/ies) with invalid position key(s). Sample(s): {3}",
                ArchimedesScrewModSystem.LogPrefix,
                ControllerId,
                removedInvalidKeys,
                sampleText);
        }
    }

    private float GetCurrentMechanicalPower()
    {
        if (Api == null)
        {
            return 0f;
        }

        BlockEntity? intakeBe = Api.World.BlockAccessor.GetBlockEntity(Pos);
        BEBehaviorMPArchimedesScrew? behavior = intakeBe?.GetBehavior<BEBehaviorMPArchimedesScrew>();
        return behavior?.Network == null ? 0f : Math.Abs(behavior.Network.Speed);
    }

    private int DrainUnsupportedSources(IReadOnlyCollection<ArchimedesOutletState> referenceSeeds, HashSet<long> supportedKeySet, string reason, bool ignoreGrace = false)
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("controller.drainUnsupported");
        if (Api == null || waterManager == null || waterConfig == null)
        {
            return 0;
        }

        long now = Environment.TickCount64;
        if (!ignoreGrace && now < drainUnsupportedGraceUntilMs)
        {
            return 0;
        }

        // Idle/derelict short-circuit: avoid reconcile overhead when this controller has nothing to
        // drain and no seed memory of a previously-active state. When ignoreGrace is requested we
        // still run through reconcile so post-load forced drains remain authoritative.
        if (!ignoreGrace && ownedPositions.Count == 0 && lastSeedPos == null)
        {
            return 0;
        }

        ReconcileOwnedPositionsFromManager();
        ReconcileRelayOwnedPositionsFromManager();

        List<BlockPos> toRelease = new();
        foreach (BlockPos pos in ownedPositions.Values)
        {
            long key = ArchimedesPosKey.Pack(pos);
            if (!supportedKeySet.Contains(key))
            {
                toRelease.Add(pos);
            }
        }

        if (toRelease.Count == 0)
        {
            return 0;
        }

        ArchimedesPerf.AddCount("controller.drainUnsupported.candidates", toRelease.Count);

        List<BlockPos> origins = new(referenceSeeds.Count);
        foreach (ArchimedesOutletState seed in referenceSeeds)
        {
            origins.Add(seed.SeedPos.Copy());
        }

        if (origins.Count == 0)
        {
            origins.Add(lastSeedPos?.Copy() ?? Pos.Copy());
        }

        int perTick = Math.Max(1, waterConfig.MaxBlocksPerStep);
        List<BlockPos> unstableCandidates = new();
        foreach (BlockPos pos in toRelease)
        {
            if (CountManagedSourceHeightCardinalNeighbors(pos) < 3)
            {
                unstableCandidates.Add(pos);
            }
        }

        List<BlockPos> releaseCandidates = OrderUnsupportedReleaseCandidates(
            unstableCandidates.Count > 0 ? unstableCandidates : toRelease,
            origins
        );
        int releaseCount = Math.Min(perTick, releaseCandidates.Count);
        ArchimedesPerf.AddCount("controller.drainUnsupported.releaseCount", releaseCount);
        ArchimedesPerf.AddCount("controller.drainUnsupported.unstableCandidates", unstableCandidates.Count);
        if (unstableCandidates.Count == 0)
        {
            ArchimedesPerf.AddCount("controller.drainUnsupported.fallbackNoUnstable");
        }
        bool mutated = false;
        for (int i = 0; i < releaseCount; i++)
        {
            BlockPos pos = releaseCandidates[i];
            long key = ArchimedesPosKey.Pack(pos);
            ReleaseOutcome outcome = waterManager.ReleaseOwnedSourceForController(ControllerId, pos);

            if (ownedPositions.Remove(key))
            {
                mutated = true;
            }
            if (relayOwnedPositions.Remove(key))
            {
                mutated = true;
            }
            relayPromotedAtMsByKey.Remove(key);

            if (outcome == ReleaseOutcome.OwnedByOtherController)
            {
                ArchimedesPerf.AddCount("controller.drainUnsupported.ownerDrift");
                continue;
            }

            if (outcome == ReleaseOutcome.NotOwned)
            {
                ArchimedesPerf.AddCount("controller.drainUnsupported.notOwned");
                continue;
            }

            NoteLocalSourceCooldown(key);
            waterManager.MarkDrainQuarantine(pos);
            ownershipChurnTotal++;
        }

        if (mutated)
        {
            UpdateSnapshot();
        }
        Log(
            "Drain tick toward {0}: removedSources={1}, remainingSources={2}, reason={3}",
            origins[0],
            releaseCount,
            ownedPositions.Count,
            reason
        );

        return releaseCount;
    }

    private int CountManagedSourceHeightCardinalNeighbors(BlockPos pos)
    {
        if (Api == null || waterManager == null)
        {
            return 0;
        }

        int count = 0;
        BlockPos neighbourPos = new(0);
        foreach (BlockFacing face in BlockFacing.HORIZONTALS)
        {
            neighbourPos.Set(pos.X + face.Normali.X, pos.Y + face.Normali.Y, pos.Z + face.Normali.Z);
            if (!ArchimedesFluidHostValidator.CanLiquidsTouchByBarrier(Api.World, pos, neighbourPos))
            {
                continue;
            }

            Block fluid = Api.World.BlockAccessor.GetBlock(neighbourPos, BlockLayersAccess.Fluid);
            if (waterManager.IsArchimedesManagedFlowOrSourceHeight(fluid))
            {
                count++;
            }
        }

        return count;
    }

    private void UpdateSnapshot()
    {
        waterManager?.UpdateControllerSnapshot(ControllerId, Pos, ownedPositions.Values.ToList());
        MarkDirty();
    }

    private int CleanupUnownedManagedSourcesForControllerState(bool ignoreGrace = false)
    {
        if (waterManager == null || waterConfig == null)
        {
            return 0;
        }

        if (!ignoreGrace && Environment.TickCount64 < drainUnsupportedGraceUntilMs)
        {
            return 0;
        }

        // No owned/seed context means this controller cannot safely attribute nearby unowned
        // managed sources to itself; skipping prevents cross-controller delete loops.
        if (ownedPositions.Count == 0 && lastSeedPos == null && !ignoreGrace)
        {
            return 0;
        }

        // Orphan managed water is connected to the same body the controller owned/owns.
        // Use every owned position plus the last/current seed as BFS anchors so the cleanup
        // actually reaches into the body of fluid. Previously only [lastSeedPos, Pos] were
        // used; Pos (the controller block) is never water, and lastSeedPos was sometimes
        // null or stranded, so cleanup BFS returned 0 cells even when the body had hundreds
        // of orphans (verified via seize-summary vs orphan-cleanup-summary mismatch).
        List<BlockPos> anchors = new();
        HashSet<long> anchorSeen = new();
        void AddAnchor(BlockPos p)
        {
            long k = ArchimedesPosKey.Pack(p);
            if (anchorSeen.Add(k))
            {
                anchors.Add(p.Copy());
            }
        }

        if (lastSeedPos != null)
        {
            AddAnchor(lastSeedPos);
        }

        foreach (BlockPos owned in ownedPositions.Values)
        {
            AddAnchor(owned);
        }

        if (anchors.Count == 0)
        {
            anchors.Add(Pos.Copy());
        }

        int budget = Math.Max(1, waterConfig.MaxBlocksPerStep * (ignoreGrace ? 8 : 2));
        int removed = waterManager.CleanupUnownedManagedSourcesAroundAnchors(anchors, budget);
        return removed;
    }

    private void ReconcileOwnedPositionsFromManager()
    {
        if (waterManager == null)
        {
            return;
        }

        int added = 0;
        foreach (BlockPos pos in waterManager.GetOwnedSourcePositionsForController(ControllerId))
        {
            long key = ArchimedesPosKey.Pack(pos);
            if (ownedPositions.ContainsKey(key))
            {
                continue;
            }

            ownedPositions[key] = pos.Copy();
            added++;
        }

        if (added > 0)
        {
            UpdateSnapshot();
            Log("Reconciled {0} manager-owned source(s) into local ownership before force-drain", added);
        }
    }

    private void ReconcileRelayOwnedPositionsFromManager()
    {
        if (waterManager == null)
        {
            return;
        }

        int added = 0;
        foreach (BlockPos pos in waterManager.GetRelayOwnedPositionsForController(ControllerId))
        {
            long key = ArchimedesPosKey.Pack(pos);
            if (relayOwnedPositions.ContainsKey(key))
            {
                continue;
            }

            relayOwnedPositions[key] = pos.Copy();
            // 0 marks "legacy / unknown promotion time" - protected from age-based trim, same as existing restore path.
            relayPromotedAtMsByKey[key] = 0L;
            added++;
        }

        if (added > 0)
        {
            MarkDirty();
            Log("Reconciled {0} manager-owned relay source(s) into local tracking", added);
        }
    }

    private void LogTruncationPause(BlockPos seedPos, int visitedManagedCount)
    {
        long now = Environment.TickCount64;
        if (now < nextTruncationPauseLogAtMs)
        {
            return;
        }

        nextTruncationPauseLogAtMs = now + 5000;
        Log(
            "Automation paused this tick because connected managed water was truncated near seed {0} (visitedManaged={1})",
            seedPos,
            visitedManagedCount
        );
    }

    private bool CanUseSeedPosition(BlockPos seedPos)
    {
        if (Api == null)
        {
            return false;
        }
        BlockPos topPos = FindTopScrewPos();
        if (Api.World.BlockAccessor.GetBlock(topPos) is BlockWaterArchimedesScrew topScrew &&
            topScrew.IsOutletBlock())
        {
            BlockFacing? facing = topScrew.GetPortFacing();
            if (facing != null)
            {
                BlockPos expectedOutputPos = topPos.AddCopy(facing);
                if (seedPos.Equals(expectedOutputPos))
                {
                    return ArchimedesFluidHostValidator.IsFluidHostCellCompatible(
                        Api.World,
                        seedPos,
                        topPos,
                        facing
                    );
                }
            }
        }

        return ArchimedesFluidHostValidator.IsFluidHostCellCompatible(Api.World, seedPos);
    }

    private BlockPos GetSeedPosition()
    {
        BlockPos topPos = FindTopScrewPos();
        if (Api?.World.BlockAccessor.GetBlock(topPos) is BlockWaterArchimedesScrew topScrew && topScrew.IsOutletBlock())
        {
            BlockFacing? facing = topScrew.GetPortFacing();
            if (facing != null)
            {
                return topPos.AddCopy(facing);
            }
        }

        return topPos.UpCopy();
    }

    private BlockPos FindTopScrewPos()
    {
        BlockPos top = Pos.Copy();
        int maxLength = waterConfig?.MaxScrewLength ?? 32;

        for (int i = 0; i < maxLength; i++)
        {
            BlockPos above = top.UpCopy();
            if (Api?.World.BlockAccessor.GetBlock(above) is not BlockWaterArchimedesScrew)
            {
                break;
            }

            top = above;
        }

        return top;
    }

    private void Log(string message, params object?[] args)
    {
        ArchimedesScrewModSystem.LogVerbose(
            Api?.Logger,
            $"{ArchimedesScrewModSystem.LogPrefix} [controller:{ControllerId}] {message}",
            args
        );
    }

    private void TryArmTopologyChangeGrace(string failureReason)
    {
        if (string.IsNullOrWhiteSpace(failureReason))
        {
            return;
        }

        // Assembly topology failures use ShouldForceDrainWhenControllerInvalid → immediate drain
        // (ignoreGrace). Grace here is only for transient blocked seed/output while fluids settle.
        if (!failureReason.StartsWith("seed/output position ", StringComparison.Ordinal))
        {
            return;
        }

        ArmDrainUnsupportedGrace(TopologyChangeDrainUnsupportedGraceMs);
    }

    private void ArmDrainUnsupportedGrace(int durationMs)
    {
        long now = Environment.TickCount64;
        long until = now + Math.Max(0, durationMs);
        if (until > drainUnsupportedGraceUntilMs)
        {
            drainUnsupportedGraceUntilMs = until;
        }
    }

    private static bool ShouldForceDrainWhenControllerInvalid(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        return reason.StartsWith("unsupported intake fluid:", StringComparison.Ordinal) ||
               reason.StartsWith("assembly invalid:", StringComparison.Ordinal) ||
               string.Equals(reason, "block is not an intake controller", StringComparison.Ordinal);
    }

    private void LogStateChange(string name, ref bool? lastValue, bool value)
    {
        if (lastValue == value)
        {
            return;
        }

        lastValue = value;
        Log("{0} changed to {1}", name, value);
    }

    private static int MinDistanceSquared(BlockPos pos, List<BlockPos> origins)
    {
        // Manual loop avoids per-call enumerator allocation when called N times during the
        // unsupported-release decorate pass.
        if (origins.Count == 0)
        {
            return 0;
        }

        int min = ArchimedesPositionCodec.DistanceSquared(pos, origins[0]);
        for (int i = 1; i < origins.Count; i++)
        {
            int d = ArchimedesPositionCodec.DistanceSquared(pos, origins[i]);
            if (d < min)
            {
                min = d;
            }
        }

        return min;
    }

    private IEnumerable<BlockPos> SafeDecodePositionArray(byte[] encodedBytes, string fieldName)
    {
        int[]? flat = null;
        try
        {
            flat = SerializerUtil.Deserialize<int[]>(encodedBytes);
        }
        catch (Exception ex)
        {
            Log("Corrupt {0} payload ignored (byteLength={1}, error={2})", fieldName, encodedBytes.Length, ex.Message);
            yield break;
        }

        if (flat == null || flat.Length == 0)
        {
            yield break;
        }

        if (flat.Length % 3 != 0)
        {
            Log("Invalid {0} payload shape ignored (intLength={1}, expected multiple of 3)", fieldName, flat.Length);
            yield break;
        }

        foreach (BlockPos pos in ArchimedesPositionCodec.DecodePositions(flat))
        {
            yield return pos;
        }
    }

    private BlockPos? SafeDecodeSinglePos(byte[] encodedBytes, string fieldName)
    {
        int[]? flat = null;
        try
        {
            flat = SerializerUtil.Deserialize<int[]>(encodedBytes);
        }
        catch (Exception ex)
        {
            Log("Corrupt {0} payload ignored (byteLength={1}, error={2})", fieldName, encodedBytes.Length, ex.Message);
            return null;
        }

        if (flat == null || flat.Length != 3)
        {
            Log("Invalid {0} payload shape ignored (intLength={1}, expected 3)", fieldName, flat?.Length ?? 0);
            return null;
        }

        return ArchimedesPositionCodec.DecodeSinglePos(flat);
    }

    private readonly record struct ControllerEvaluation(
        bool IsController,
        bool IsPowered,
        float CurrentPower,
        string FailureReason,
        string? FamilyId,
        BlockPos? SeedPos
    );

    // Mirror the legacy LINQ ordering used by deterministic relay selection:
    //   primary  : distance ascending (closer to seed first)
    //   secondary: Y descending       (higher cells first)  -> stored as -Y to keep ascending compare
    //   tertiary : packed key ascending (stable tiebreak)
    private readonly record struct RelayCandidateRank(int Distance, int NegY, long Key) : IComparable<RelayCandidateRank>
    {
        public int CompareTo(RelayCandidateRank other)
        {
            int c = Distance.CompareTo(other.Distance);
            if (c != 0) return c;
            c = NegY.CompareTo(other.NegY);
            if (c != 0) return c;
            return Key.CompareTo(other.Key);
        }
    }

    private sealed class ReverseRelayCandidateRankComparer : IComparer<RelayCandidateRank>
    {
        public int Compare(RelayCandidateRank x, RelayCandidateRank y) => y.CompareTo(x);
    }

    private readonly record struct RelayCandidateEntry(long Key, BlockPos Pos, RelayCandidateRank Rank);

    // Trim ordering: newest promotions (largest PromotedAtMs) and farthest-from-seed (largest
    // distance squared) are released first. Natural CompareTo order is ascending; with a default
    // PriorityQueue (min-heap) of size K, the K largest entries (= worst, evict first) survive
    // and the smallest of those sits at the root for cheap eviction comparisons.
    private readonly record struct TrimCandidateRank(long PromotedAtMs, long DistSq) : IComparable<TrimCandidateRank>
    {
        public int CompareTo(TrimCandidateRank other)
        {
            int c = PromotedAtMs.CompareTo(other.PromotedAtMs);
            if (c != 0) return c;
            return DistSq.CompareTo(other.DistSq);
        }
    }
}
