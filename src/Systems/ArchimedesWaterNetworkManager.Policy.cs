using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ArchimedesScrew;

public sealed partial class ArchimedesWaterNetworkManager
{
    private readonly Dictionary<long, ManagedSourceProvenance> sourceProvenanceByPos = new();
    private readonly Dictionary<long, string> lockedVanillaFamilyByPos = new();
    private readonly Queue<long> playerIntentQueue = new();
    private readonly Queue<long> nonPlayerIntentQueue = new();
    private readonly Dictionary<long, ConversionIntent> queuedIntentByKey = new();
    private static readonly List<BlockPos> LockCaptureOffsets = BuildLockCaptureOffsets();
    private static readonly List<BlockPos> LockCaptureOffsetsAggressive = BuildAggressiveLockCaptureOffsets();
    private const int IntentCoalesceWindowMs = 500;
    private const bool AggressivePolicyEnabled = true;
    private const int ClaimDeferDrainBacklogThreshold = 256;
    private const bool ReducedLockCaptureRadiusWhenAggressive = true;
    private const bool RelaxedFrontierChecksUnderDrainPressure = true;

    public void EnqueueConversionIntent(
        BlockPos pos,
        string familyId,
        string? ownerHintControllerId,
        string reason,
        bool playerIntent = false)
    {
        long key = ArchimedesPosKey.Pack(pos);
        long nowMs = Environment.TickCount64;
        if (queuedIntentByKey.TryGetValue(key, out ConversionIntent existing))
        {
            bool recentlyQueued = nowMs - existing.EnqueuedAtMs <= IntentCoalesceWindowMs;
            if (recentlyQueued &&
                string.Equals(existing.FamilyId, familyId, StringComparison.Ordinal) &&
                string.Equals(existing.OwnerHintControllerId, ownerHintControllerId, StringComparison.Ordinal) &&
                existing.PlayerIntent == playerIntent)
            {
                ArchimedesPerf.AddCount("water.intent.coalesced");
                return;
            }

            bool promoteToPlayer = playerIntent && !existing.PlayerIntent;
            ConversionIntent updated = existing with
            {
                FamilyId = familyId,
                OwnerHintControllerId = ownerHintControllerId,
                Reason = reason,
                PlayerIntent = existing.PlayerIntent || playerIntent,
                EnqueuedAtMs = nowMs
            };
            queuedIntentByKey[key] = updated;
            if (promoteToPlayer)
            {
                playerIntentQueue.Enqueue(key);
            }
            ArchimedesPerf.AddCount("water.intent.coalesced");
            return;
        }

        ConversionIntent fresh = new(
            pos.Copy(),
            familyId,
            ownerHintControllerId,
            reason,
            playerIntent,
            nowMs
        );
        queuedIntentByKey[key] = fresh;
        if (playerIntent)
        {
            playerIntentQueue.Enqueue(key);
        }
        else
        {
            nonPlayerIntentQueue.Enqueue(key);
        }
        ArchimedesPerf.AddCount("water.intent.enqueued");
    }

    private void ProcessConversionIntentQueue()
    {
        if (queuedIntentByKey.Count == 0)
        {
            return;
        }

        int budget = Math.Max(1, config.Water.IntentQueueMaxPerGlobalTick);
        int playerBudget = budget;
        int nonPlayerBudget = budget;
        if (WasLastGlobalTickBudgetExceeded())
        {
            nonPlayerBudget = Math.Max(1, budget / 4);
            ArchimedesPerf.AddCount("water.intent.lagThrottleActive");
        }

        if (AggressivePolicyEnabled &&
            GetIncrementalDrainBacklogCount() >= Math.Max(1, ClaimDeferDrainBacklogThreshold))
        {
            nonPlayerBudget = 0;
            ArchimedesPerf.AddCount("water.intent.deferredForDrainBacklog");
        }

        int processed = 0;
        int processedPlayer = 0;
        while (processedPlayer < playerBudget && processed < budget &&
               TryDequeueNextIntent(playerIntentQueue, playerIntentOnly: true, out ConversionIntent playerIntent))
        {
            ProcessIntent(playerIntent);
            processed++;
            processedPlayer++;
        }

        int processedNonPlayer = 0;
        while (processedNonPlayer < nonPlayerBudget && processed < budget &&
               TryDequeueNextIntent(nonPlayerIntentQueue, playerIntentOnly: false, out ConversionIntent nonPlayerIntent))
        {
            ProcessIntent(nonPlayerIntent);
            processed++;
            processedNonPlayer++;
        }

        while (processed < budget &&
               TryDequeueNextIntent(playerIntentQueue, playerIntentOnly: true, out ConversionIntent fallbackPlayer))
        {
            ProcessIntent(fallbackPlayer);
            processed++;
            processedPlayer++;
        }

        ArchimedesPerf.AddCount("water.intent.dequeued", processed);
        ArchimedesPerf.AddCount("water.intent.dequeued.player", processedPlayer);
        ArchimedesPerf.AddCount("water.intent.dequeued.nonPlayer", processedNonPlayer);
        ArchimedesPerf.AddCount("water.intent.remaining", queuedIntentByKey.Count);
    }

    private bool TryDequeueNextIntent(Queue<long> queue, bool playerIntentOnly, out ConversionIntent intent)
    {
        while (queue.Count > 0)
        {
            long key = queue.Dequeue();
            if (!queuedIntentByKey.TryGetValue(key, out ConversionIntent candidate))
            {
                continue;
            }

            if (playerIntentOnly && !candidate.PlayerIntent)
            {
                continue;
            }

            if (!playerIntentOnly && candidate.PlayerIntent)
            {
                continue;
            }

            queuedIntentByKey.Remove(key);
            intent = candidate;
            return true;
        }

        intent = default;
        return false;
    }

    private void ProcessIntent(ConversionIntent intent)
    {
        TryClaimVanillaSourceWithPolicy(
            intent.Pos,
            intent.FamilyId,
            intent.OwnerHintControllerId,
            intent.Reason,
            intent.PlayerIntent
        );
    }

    public bool TryGetSourceProvenance(BlockPos pos, out ManagedSourceProvenance provenance)
    {
        return sourceProvenanceByPos.TryGetValue(ArchimedesPosKey.Pack(pos), out provenance);
    }

    private void SetSourceProvenance(BlockPos pos, ManagedSourceProvenance provenance)
    {
        sourceProvenanceByPos[ArchimedesPosKey.Pack(pos)] = provenance;
    }

    private bool TryClaimVanillaSourceWithPolicy(
        BlockPos pos,
        string familyId,
        string? ownerHintControllerId,
        string reason,
        bool playerIntent)
    {
        Block fluidBlock = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!IsVanillaSelfSustainingSourceForFamily(fluidBlock, familyId))
        {
            return false;
        }

        ArchimedesPerf.AddCount("water.claims.attempted");
        int drainBacklog = GetIncrementalDrainBacklogCount();
        bool aggressiveMode = AggressivePolicyEnabled;
        bool drainPressure = aggressiveMode &&
                            drainBacklog >= Math.Max(1, ClaimDeferDrainBacklogThreshold);
        if (drainPressure && !playerIntent)
        {
            ArchimedesPerf.AddCount("water.claims.rejected.deferredByDrainBacklog");
            return false;
        }

        if (IsVanillaLocked(pos, familyId))
        {
            ArchimedesPerf.AddCount("water.claims.rejected.lockedVanilla");
            CaptureVanillaLocksAround(pos, familyId);
            return false;
        }

        bool requireFrontier = !playerIntent &&
                               (!drainPressure || !RelaxedFrontierChecksUnderDrainPressure);
        if (requireFrontier && !HasAtLeastTwoOwnedManagedCardinalFrontierNeighbors(pos, familyId))
        {
            ArchimedesPerf.AddCount("water.claims.rejected.notFrontier");
            return false;
        }

        SetManagedWaterVariant(pos, familyId, "still", 7, triggerUpdates: false);
        CaptureVanillaLocksAround(pos, familyId);

        bool assigned = false;
        if (!string.IsNullOrWhiteSpace(ownerHintControllerId))
        {
            if (CanControllerClaimInDomain(ownerHintControllerId, pos, familyId))
            {
                assigned = EnsureSourceOwned(ownerHintControllerId, pos, familyId);
                if (assigned)
                {
                    ArchimedesPerf.AddCount("water.claims.hintFastPathHit");
                }
            }
            else
            {
                ArchimedesPerf.AddCount("water.claims.hintFastPathRejectedDomain");
            }
        }

        if (!assigned)
        {
            assigned = AssignNearestActiveControllerForNewSource(
                pos,
                familyId,
                reason: $"policy-claim:{reason}"
            );
        }

        Block placed = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (placed.Id != 0)
        {
            TriggerLiquidUpdates(pos, placed);
        }

        if (assigned)
        {
            SetSourceProvenance(
                pos,
                playerIntent
                    ? ManagedSourceProvenance.ConvertedFromVanillaIntentional
                    : ManagedSourceProvenance.ManagedSimDerived
            );
            ArchimedesPerf.AddCount("water.claims.accepted");
        }
        else
        {
            ArchimedesPerf.AddCount("water.claims.rejected.unassigned");
        }

        return true;
    }

    public bool IsVanillaLocked(BlockPos pos, string familyId)
    {
        long key = ArchimedesPosKey.Pack(pos);
        return lockedVanillaFamilyByPos.TryGetValue(key, out string? lockedFamily) &&
               string.Equals(lockedFamily, familyId, StringComparison.Ordinal);
    }

    public int CountLockedVanillaNeighbors(BlockPos pos, string familyId)
    {
        int count = 0;
        foreach (BlockPos delta in LockCaptureOffsets)
        {
            BlockPos neighbor = new(pos.X + delta.X, pos.Y + delta.Y, pos.Z + delta.Z);
            if (IsVanillaLocked(neighbor, familyId))
            {
                count++;
            }
        }

        return count;
    }

    public void CaptureVanillaLocksAround(BlockPos centerPos, string familyId)
    {
        List<BlockPos> offsets = AggressivePolicyEnabled &&
                                 ReducedLockCaptureRadiusWhenAggressive
            ? LockCaptureOffsetsAggressive
            : LockCaptureOffsets;
        foreach (BlockPos delta in offsets)
        {
            BlockPos candidate = new(centerPos.X + delta.X, centerPos.Y + delta.Y, centerPos.Z + delta.Z);
            Block fluid = api.World.BlockAccessor.GetBlock(candidate, BlockLayersAccess.Fluid);
            if (!TryResolveVanillaWaterFamily(fluid, out string vanillaFamilyId) ||
                !string.Equals(vanillaFamilyId, familyId, StringComparison.Ordinal))
            {
                continue;
            }

            lockedVanillaFamilyByPos[ArchimedesPosKey.Pack(candidate)] = familyId;
            ArchimedesPerf.AddCount("water.vanillaLocks.captured");
        }
    }

    private static List<BlockPos> BuildLockCaptureOffsets()
    {
        List<BlockPos> offsets = new();
        for (int x = -1; x <= 1; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                for (int z = -1; z <= 1; z++)
                {
                    if (x == 0 && y == 0 && z == 0)
                    {
                        continue;
                    }

                    offsets.Add(new BlockPos(x, y, z));
                }
            }
        }

        return offsets;
    }

    private static List<BlockPos> BuildAggressiveLockCaptureOffsets()
    {
        return new List<BlockPos>
        {
            new(1, 0, 0),
            new(-1, 0, 0),
            new(0, 0, 1),
            new(0, 0, -1),
            new(0, 1, 0),
            new(0, -1, 0)
        };
    }

    /// <summary>
    /// Territorial first-claim policy: a controller may claim <paramref name="sourcePos"/> if it is unowned,
    /// or if this controller already owns it. No leader arbitration; ownership is sticky until the cell drains.
    /// Same-tick contention is broken by lowest controller id via the pre-sorted dispatch order.
    /// <paramref name="familyId"/> is reserved for future per-family arbitration but ignored today.
    /// </summary>
    private bool CanControllerClaimInDomain(string controllerId, BlockPos sourcePos, string familyId)
    {
        long key = ArchimedesPosKey.Pack(sourcePos);
        if (sourceOwnerByPos.TryGetValue(key, out string? currentOwner))
        {
            bool sameOwner = string.Equals(currentOwner, controllerId, StringComparison.Ordinal);
            if (!sameOwner)
            {
                ArchimedesPerf.AddCount("water.claims.rejected.alreadyOwned");
            }
            return sameOwner;
        }

        return true;
    }

    private bool HasAtLeastTwoOwnedManagedCardinalFrontierNeighbors(BlockPos pos, string familyId)
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
            if (!IsManagedSelfSustainingSourceForFamily(adjacentFluid, familyId))
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

    private record struct ConversionIntent(
        BlockPos Pos,
        string FamilyId,
        string? OwnerHintControllerId,
        string Reason,
        bool PlayerIntent,
        long EnqueuedAtMs
    );
}

public enum ManagedSourceProvenance
{
    Unknown = 0,
    ControllerSeedOrRelay = 1,
    ConvertedFromVanillaIntentional = 2,
    ManagedSimDerived = 3
}
