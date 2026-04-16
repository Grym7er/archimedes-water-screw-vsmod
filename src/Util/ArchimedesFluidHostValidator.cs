using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ArchimedesScrew;

internal static class ArchimedesFluidHostValidator
{
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
        if (!solidClear || !fluidClear)
        {
            return false;
        }

        if (sourcePos == null || sourceFacing == null)
        {
            return true;
        }

        Block sourceSolid = ba.GetBlock(sourcePos);
        float sourceBarrier = sourceSolid.GetLiquidBarrierHeightOnSide(sourceFacing, sourcePos);
        float targetBarrier = targetSolid.GetLiquidBarrierHeightOnSide(sourceFacing.Opposite, targetPos);
        return sourceBarrier < 1f && targetBarrier < 1f;
    }

    private static bool IsVanillaWaterBlock(Block block)
    {
        return block.IsLiquid() && ArchimedesWaterFamilies.TryResolveVanillaFamily(block, out _);
    }
}
