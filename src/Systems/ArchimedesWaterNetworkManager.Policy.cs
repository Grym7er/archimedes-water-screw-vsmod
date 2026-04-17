using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ArchimedesScrew;

public sealed partial class ArchimedesWaterNetworkManager
{
    private readonly Dictionary<string, ManagedSourceProvenance> sourceProvenanceByPos = new(StringComparer.Ordinal);
    private readonly Queue<ConversionIntent> conversionIntentQueue = new();
    private readonly HashSet<string> queuedIntentKeys = new(StringComparer.Ordinal);

    public void EnqueueConversionIntent(
        BlockPos pos,
        string familyId,
        string? ownerHintControllerId,
        string reason,
        bool playerIntent = false)
    {
        string key = PosKey(pos);
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
            queuedIntentKeys.Remove(PosKey(intent.Pos));
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
        return sourceProvenanceByPos.TryGetValue(PosKey(pos), out provenance);
    }

    private void SetSourceProvenance(BlockPos pos, ManagedSourceProvenance provenance)
    {
        sourceProvenanceByPos[PosKey(pos)] = provenance;
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
        if (!playerIntent && !HasAtLeastTwoOwnedManagedCardinalSourceNeighbors(pos, familyId))
        {
            ArchimedesPerf.AddCount("water.claims.rejected.notFrontier");
            return false;
        }

        if (!playerIntent && LooksLikeNaturalVanillaBody(pos, familyId))
        {
            ArchimedesPerf.AddCount("water.claims.rejected.detectedVanillaBody");
            return false;
        }

        SetManagedWaterVariant(pos, familyId, "still", 7, triggerUpdates: false);

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

    private bool LooksLikeNaturalVanillaBody(BlockPos pos, string familyId)
    {
        int count = 0;
        foreach (BlockFacing face in BlockFacing.ALLFACES)
        {
            Block fluid = api.World.BlockAccessor.GetBlock(pos.AddCopy(face), BlockLayersAccess.Fluid);
            if (IsVanillaSelfSustainingSourceForFamily(fluid, familyId))
            {
                count++;
            }
        }

        return count >= Math.Max(1, config.Water.VanillaBodyNeighborThreshold);
    }

    private string? ResolveDomainLeaderControllerId(BlockPos sourcePos, string familyId, IEnumerable<string> connectedWaterKeys)
    {
        HashSet<string> connected = new(connectedWaterKeys, StringComparer.Ordinal);
        List<ArchimedesOutletState> candidates = new();
        foreach (ArchimedesOutletState seed in GetActiveSeedStatesCached())
        {
            if (!string.Equals(seed.FamilyId, familyId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!connected.Contains(PosKey(seed.SeedPos)))
            {
                continue;
            }

            candidates.Add(seed);
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates
            .OrderBy(seed => seed.ControllerId, StringComparer.Ordinal)
            .ThenBy(seed => ArchimedesPositionCodec.DistanceSquared(sourcePos, seed.SeedPos))
            .Select(seed => seed.ControllerId)
            .FirstOrDefault();
    }

    private bool CanControllerClaimInDomain(string controllerId, BlockPos sourcePos, string familyId)
    {
        ConnectedManagedWaterResult connectedResult = CollectConnectedManagedWaterCachedDetailed(sourcePos);
        if (connectedResult.IsTruncated)
        {
            ArchimedesPerf.AddCount("water.claims.rejected.truncatedComponent");
            return false;
        }

        string? leader = ResolveDomainLeaderControllerId(sourcePos, familyId, connectedResult.VisitedKeys);
        if (leader == null)
        {
            return true;
        }

        bool allowed = string.Equals(controllerId, leader, StringComparison.Ordinal);
        if (!allowed)
        {
            ArchimedesPerf.AddCount("water.claims.rejected.domainNotLeader");
        }

        return allowed;
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
