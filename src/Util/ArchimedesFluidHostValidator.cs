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

        Block sourceSolid = ba.GetBlock(sourcePos);
        bool skipSourceBarrierCheck =
            sourceSolid is BlockWaterArchimedesScrew screw && screw.IsOutletBlock();
        float sourceBarrier = skipSourceBarrierCheck
            ? 0f
            : sourceSolid.GetLiquidBarrierHeightOnSide(sourceFacing, sourcePos);
        float targetBarrier = targetSolid.GetLiquidBarrierHeightOnSide(sourceFacing.Opposite, targetPos);
        return sourceBarrier < 1f && targetBarrier < 1f;
    }

    private static bool IsVanillaWaterBlock(Block block)
    {
        return block.IsLiquid() && ArchimedesWaterFamilies.TryResolveVanillaFamily(block, out _);
    }

}
