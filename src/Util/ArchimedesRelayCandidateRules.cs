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

        Block solid = accessor.GetBlock(pos);
        bool isAqueduct = IsHardcoreWaterAqueduct(solid);
        if (isAqueduct)
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
        else if (!HasHorizontalManagedSameFamilyNeighborOrWhitelist(world, pos, candidateFluid, manager))
        {
            return false;
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

    /// <summary>
    /// In-aqueduct: an orientation-aligned neighbor qualifies if it is a fully empty cell (solid and fluid air)
    /// or solid air with liquid matching the candidate intake family.
    /// </summary>
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
        Block neighborFluid = accessor.GetBlock(neighborPos, BlockLayersAccess.Fluid);
        if (neighborSolid.Id == 0 && neighborFluid.Id == 0)
        {
            return true;
        }

        if (neighborSolid.Id == 0 &&
            manager.TryResolveIntakeWaterFamily(neighborFluid, out string neighborFamilyId) &&
            string.Equals(neighborFamilyId, candidateFamilyId, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static bool HasHorizontalManagedSameFamilyNeighborOrWhitelist(
        IWorldAccessor world,
        BlockPos pos,
        Block candidateFluid,
        ArchimedesWaterNetworkManager manager)
    {
        if (ArchimedesRelayAdjacency.IsRelaySupportAndAdjacentWhitelistSatisfied(world, pos))
        {
            return true;
        }

        if (!manager.TryResolveManagedWaterFamily(candidateFluid, out string candidateFamilyId))
        {
            return false;
        }

        IBlockAccessor accessor = world.BlockAccessor;
        BlockPos neighborPos = new(0);
        foreach (BlockFacing face in BlockFacing.HORIZONTALS)
        {
            neighborPos.Set(pos.X + face.Normali.X, pos.Y + face.Normali.Y, pos.Z + face.Normali.Z);
            Block neighborFluid = accessor.GetBlock(neighborPos, BlockLayersAccess.Fluid);
            if (!manager.TryResolveManagedWaterFamily(neighborFluid, out string neighborFamilyId))
            {
                continue;
            }

            if (string.Equals(neighborFamilyId, candidateFamilyId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
