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

        bool solidClear = targetSolid.Id == 0 || targetSolid.ForFluidsLayer || ArchimedesWaterFamilies.IsManagedWater(targetSolid);
        bool fluidClear = targetFluid.Id == 0 ||
                          ArchimedesWaterFamilies.IsManagedWater(targetFluid) ||
                          IsVanillaWaterBlock(targetFluid);
        bool hasDirectionalContext = sourcePos != null && sourceFacing != null;
        bool allowSolidByDirectionalBarrier = !solidClear && hasDirectionalContext;
        if (!fluidClear || (!solidClear && !allowSolidByDirectionalBarrier))
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
