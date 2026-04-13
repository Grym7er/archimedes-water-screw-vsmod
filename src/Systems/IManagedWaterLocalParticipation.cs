using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ArchimedesScrew;

/// <summary>
/// Local, controller-side extension seam for managed-water participation checks.
/// Keeps global ownership and BFS logic in the manager unchanged.
/// </summary>
internal interface IManagedWaterLocalParticipation
{
    bool IsRelayCreationCandidate(
        IWorldAccessor world,
        BlockPos pos,
        ArchimedesWaterNetworkManager manager
    );
}

internal sealed class DefaultManagedWaterLocalParticipation : IManagedWaterLocalParticipation
{
    public bool IsRelayCreationCandidate(
        IWorldAccessor world,
        BlockPos pos,
        ArchimedesWaterNetworkManager manager
    )
    {
        Block fluid = world.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!manager.IsArchimedesLowestFlowingBlock(fluid))
        {
            return false;
        }

        return ArchimedesRelayAdjacency.IsRelaySupportAndAdjacentWhitelistSatisfied(world, pos);
    }
}
