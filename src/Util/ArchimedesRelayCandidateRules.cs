using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ArchimedesScrew;

internal static class ArchimedesRelayCandidateRules
{
    public static bool IsPromotableRelayCandidate(IWorldAccessor world, BlockPos pos, ArchimedesWaterNetworkManager manager)
    {
        IBlockAccessor accessor = world.BlockAccessor;
        Block candidateFluid = accessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!manager.IsArchimedesRelayFlowCandidate(candidateFluid))
        {
            return false;
        }

        if (!ArchimedesRelayAdjacency.IsRelaySupportAndAdjacentWhitelistSatisfied(world, pos))
        {
            return false;
        }

        Block solid = accessor.GetBlock(pos);
        if (IsHardcoreWaterAqueduct(solid))
        {
            if (!manager.TryResolveIntakeWaterFamily(candidateFluid, out string candidateFamilyId))
            {
                return false;
            }

            if (!HasOrientationAlignedSameFamilyWaterOutsideAqueduct(world, pos, solid, candidateFamilyId, manager))
            {
                return false;
            }
        }

        return ArchimedesFluidHostValidator.IsFluidHostCellCompatible(world, pos);
    }

    private static bool IsHardcoreWaterAqueduct(Block solid)
    {
        AssetLocation? code = solid.Code;
        if (code == null || !string.Equals(code.Domain, "hardcorewater", StringComparison.Ordinal))
        {
            return false;
        }

        return code.Path.Contains("aqueduct", StringComparison.Ordinal);
    }

    private static bool HasOrientationAlignedSameFamilyWaterOutsideAqueduct(
        IWorldAccessor world,
        BlockPos pos,
        Block aqueductBlock,
        string candidateFamilyId,
        ArchimedesWaterNetworkManager manager)
    {
        if (!TryGetOrientationAlignedFacings(aqueductBlock, out BlockFacing first, out BlockFacing second))
        {
            return false;
        }

        return IsSameFamilyWaterOutsideAqueduct(world, pos, first, candidateFamilyId, manager) ||
               IsSameFamilyWaterOutsideAqueduct(world, pos, second, candidateFamilyId, manager);
    }

    private static bool TryGetOrientationAlignedFacings(Block aqueductBlock, out BlockFacing first, out BlockFacing second)
    {
        string? orientation = aqueductBlock.Variant?["orientation"];
        if (string.IsNullOrEmpty(orientation))
        {
            first = second = null!;
            return false;
        }

        switch (orientation)
        {
            case "ns":
                first = BlockFacing.NORTH;
                second = BlockFacing.SOUTH;
                return true;
            case "sn":
                first = BlockFacing.SOUTH;
                second = BlockFacing.NORTH;
                return true;
            case "we":
                first = BlockFacing.WEST;
                second = BlockFacing.EAST;
                return true;
            case "ew":
                first = BlockFacing.EAST;
                second = BlockFacing.WEST;
                return true;
            default:
                first = second = null!;
                return false;
        }
    }

    private static bool IsSameFamilyWaterOutsideAqueduct(
        IWorldAccessor world,
        BlockPos origin,
        BlockFacing facing,
        string candidateFamilyId,
        ArchimedesWaterNetworkManager manager)
    {
        BlockPos neighborPos = origin.AddCopy(facing);
        IBlockAccessor accessor = world.BlockAccessor;
        Block neighborSolid = accessor.GetBlock(neighborPos);
        if (IsHardcoreWaterAqueduct(neighborSolid))
        {
            return false;
        }

        Block neighborFluid = accessor.GetBlock(neighborPos, BlockLayersAccess.Fluid);
        return manager.TryResolveIntakeWaterFamily(neighborFluid, out string neighborFamilyId) &&
               string.Equals(neighborFamilyId, candidateFamilyId, StringComparison.Ordinal);
    }
}
