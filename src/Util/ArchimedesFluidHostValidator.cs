using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ArchimedesScrew;

internal static class ArchimedesFluidHostValidator
{
    public static bool CanLiquidsTouchByBarrier(IWorldAccessor world, BlockPos fromPos, BlockPos toPos)
    {
        if (!TryGetCardinalFacing(fromPos, toPos, out BlockFacing facing))
        {
            return false;
        }

        IBlockAccessor ba = world.BlockAccessor;
        Block fromSolid = ba.GetBlock(fromPos);
        Block toSolid = ba.GetBlock(toPos);
        float fromBarrier = fromSolid.GetLiquidBarrierHeightOnSide(facing, fromPos);
        float toBarrier = toSolid.GetLiquidBarrierHeightOnSide(facing.Opposite, toPos);
        return fromBarrier < 1f && toBarrier < 1f;
    }


    public static bool IsFluidHostCellCompatible(
        IWorldAccessor world,
        BlockPos targetPos,
        BlockPos? sourcePos = null,
        BlockFacing? sourceFacing = null)
    {
        IBlockAccessor ba = world.BlockAccessor;
        Block targetSolid = ba.GetBlock(targetPos);
        Block targetFluid = ba.GetBlock(targetPos, BlockLayersAccess.Fluid);

        bool fluidClear = targetFluid.Id == 0 ||
                          ArchimedesWaterFamilies.IsManagedWater(targetFluid) ||
                          IsVanillaWaterBlock(targetFluid);
        bool hostableCell = IsHostableCellByGeometry(targetSolid, targetPos);
        bool hasDirectionalContext = sourcePos != null && sourceFacing != null;
        if (!fluidClear || !hostableCell)
        {
            return false;
        }

        if (!hasDirectionalContext)
        {
            return true;
        }

        Block sourceSolid = ba.GetBlock(sourcePos!);
        bool skipSourceBarrierCheck =
            sourceSolid is BlockWaterArchimedesScrew screw && screw.IsOutletBlock();
        float sourceBarrier = skipSourceBarrierCheck
            ? 0f
            : sourceSolid.GetLiquidBarrierHeightOnSide(sourceFacing!, sourcePos!);
        float targetBarrier = targetSolid.GetLiquidBarrierHeightOnSide(sourceFacing!.Opposite, targetPos);
        return sourceBarrier < 1f && targetBarrier < 1f;
    }

    // Use physical liquid-hostability instead of block allowlists:
    // if any face permits liquid transfer, the cell can host managed water.
    private static bool IsHostableCellByGeometry(Block solidBlock, BlockPos pos)
    {
        if (solidBlock.Id == 0 || solidBlock.ForFluidsLayer || ArchimedesWaterFamilies.IsManagedWater(solidBlock))
        {
            return true;
        }

        foreach (BlockFacing face in BlockFacing.ALLFACES)
        {
            if (solidBlock.GetLiquidBarrierHeightOnSide(face, pos) < 1f)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsVanillaWaterBlock(Block block)
    {
        return block.IsLiquid() && ArchimedesWaterFamilies.TryResolveVanillaFamily(block, out _);
    }

    private static bool TryGetCardinalFacing(BlockPos fromPos, BlockPos toPos, out BlockFacing facing)
    {
        int dx = toPos.X - fromPos.X;
        int dy = toPos.Y - fromPos.Y;
        int dz = toPos.Z - fromPos.Z;

        facing = null!;
        if (dx == 1 && dy == 0 && dz == 0)
        {
            facing = BlockFacing.EAST;
            return true;
        }

        if (dx == -1 && dy == 0 && dz == 0)
        {
            facing = BlockFacing.WEST;
            return true;
        }

        if (dx == 0 && dy == 1 && dz == 0)
        {
            facing = BlockFacing.UP;
            return true;
        }

        if (dx == 0 && dy == -1 && dz == 0)
        {
            facing = BlockFacing.DOWN;
            return true;
        }

        if (dx == 0 && dy == 0 && dz == 1)
        {
            facing = BlockFacing.SOUTH;
            return true;
        }

        if (dx == 0 && dy == 0 && dz == -1)
        {
            facing = BlockFacing.NORTH;
            return true;
        }

        return false;
    }

}
