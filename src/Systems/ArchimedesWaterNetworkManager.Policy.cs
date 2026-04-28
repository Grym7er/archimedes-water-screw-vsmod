using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ArchimedesScrew;

public sealed partial class ArchimedesWaterNetworkManager
{
    private readonly Dictionary<long, ManagedSourceProvenance> sourceProvenanceByPos = new();
    private readonly Dictionary<long, string> lockedVanillaFamilyByPos = new();
    private readonly Queue<ConversionIntent> conversionIntentQueue = new();
    private readonly HashSet<long> queuedIntentKeys = new();
    private static readonly List<BlockPos> LockCaptureOffsets = BuildLockCaptureOffsets();

    public void EnqueueConversionIntent(
        BlockPos pos,
        string familyId,
        string? ownerHintControllerId,
        string reason,
        bool playerIntent = false)
    {
        long key = ArchimedesPosKey.Pack(pos);
        if (!queuedIntentKeys.Add(key))
        {
            return;
        }

        conversionIntentQueue.Enqueue(
            new ConversionIntent(
                pos.Copy(),
                familyId,
                ownerHintControllerId,
                reason,
                playerIntent,
                Environment.TickCount64
            )
        );
        ArchimedesPerf.AddCount("water.intent.enqueued");
    }

    private void ProcessConversionIntentQueue()
    {
        if (conversionIntentQueue.Count == 0)
        {
            return;
        }

        int budget = Math.Max(1, config.Water.IntentQueueMaxPerGlobalTick);
        int processed = 0;
        while (processed < budget && conversionIntentQueue.Count > 0)
        {
            ConversionIntent intent = conversionIntentQueue.Dequeue();
            queuedIntentKeys.Remove(ArchimedesPosKey.Pack(intent.Pos));
            TryClaimVanillaSourceWithPolicy(
                intent.Pos,
                intent.FamilyId,
                intent.OwnerHintControllerId,
                intent.Reason,
                intent.PlayerIntent
            );
            processed++;
        }

        ArchimedesPerf.AddCount("water.intent.dequeued", processed);
        ArchimedesPerf.AddCount("water.intent.remaining", conversionIntentQueue.Count);
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
        if (IsVanillaLocked(pos, familyId))
        {
            ArchimedesPerf.AddCount("water.claims.rejected.lockedVanilla");
            CaptureVanillaLocksAround(pos, familyId);
            return false;
        }

        if (!playerIntent && !HasAtLeastTwoOwnedManagedCardinalFrontierNeighbors(pos, familyId))
        {
            ArchimedesPerf.AddCount("water.claims.rejected.notFrontier");
            return false;
        }

        SetManagedWaterVariant(pos, familyId, "still", 7, triggerUpdates: false);
        CaptureVanillaLocksAround(pos, familyId);

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
        foreach (BlockPos delta in LockCaptureOffsets)
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

    private readonly record struct ConversionIntent(
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
