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
        if (!manager.IsArchimedesRelayFlowCandidate(fluid))
        {
            return false;
        }

        BlockPos belowPos = pos.DownCopy();
        Block belowSolid = world.BlockAccessor.GetBlock(belowPos);
        if (belowSolid.Id == 0)
        {
            return false;
        }

        if (!HasSealedUpperBarrier(belowSolid, belowPos))
        {
            return false;
        }

        Block belowFluid = world.BlockAccessor.GetBlock(belowPos, BlockLayersAccess.Fluid);
        if (IsWaterBlock(belowSolid, manager) || IsWaterBlock(belowFluid, manager))
        {
            return false;
        }

        return true;
    }

    private static bool HasSealedUpperBarrier(Block block, BlockPos pos)
    {
        return block.GetLiquidBarrierHeightOnSide(BlockFacing.UP, pos) >= 1f;
    }

    private static bool IsWaterBlock(Block block, ArchimedesWaterNetworkManager manager)
    {
        return block.IsLiquid() && manager.TryResolveIntakeWaterFamily(block, out _);
    }
}
