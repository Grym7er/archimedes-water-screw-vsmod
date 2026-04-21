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
        bool flowCandidate = manager.IsArchimedesRelayFlowCandidate(candidateFluid);
        Block solid = accessor.GetBlock(pos);
        bool isAqueduct = IsHardcoreWaterAqueduct(solid);

        if (!flowCandidate)
        {
            return false;
        }

        if (isAqueduct)
        {
            if (!manager.TryResolveIntakeWaterFamily(candidateFluid, out string candidateFamilyId))
            {
                return false;
            }

            if (ArchimedesRelayAdjacency.IsRelayBelowBlockedByNonAqueductWater(world, pos, candidateFamilyId, manager))
            {
                return false;
            }

            if (!HasOrientationAlignedQualifyingNeighbor(world, pos, solid, candidateFamilyId, manager) &&
                !HasAqueductFeedFromAbove(world, pos, candidateFamilyId, manager))
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
        return ArchimedesAqueductDetector.IsHardcoreWaterAqueduct(solid);
    }

    private static bool HasOrientationAlignedQualifyingNeighbor(
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

        return IsQualifyingAlignedNeighbor(world, pos, first, candidateFamilyId, manager) ||
               IsQualifyingAlignedNeighbor(world, pos, second, candidateFamilyId, manager);
    }

    /// <summary>
    /// In-aqueduct vertical cascade: an aqueduct candidate qualifies if the cell directly above is
    /// another aqueduct holding same-family managed water and the barrier between the two cells
    /// allows liquid transfer. Mirrors the along-the-pipe rule on the vertical axis so HCW step-down
    /// cascades can propagate ownership downward.
    /// </summary>
    private static bool HasAqueductFeedFromAbove(
        IWorldAccessor world,
        BlockPos pos,
        string candidateFamilyId,
        ArchimedesWaterNetworkManager manager)
    {
        BlockPos above = pos.UpCopy();
        IBlockAccessor accessor = world.BlockAccessor;
        Block aboveSolid = accessor.GetBlock(above);
        if (!IsHardcoreWaterAqueduct(aboveSolid))
        {
            return false;
        }

        Block aboveFluid = accessor.GetBlock(above, BlockLayersAccess.Fluid);
        if (!manager.TryResolveManagedWaterFamily(aboveFluid, out string aboveFamilyId) ||
            !string.Equals(aboveFamilyId, candidateFamilyId, StringComparison.Ordinal))
        {
            return false;
        }

        return ArchimedesFluidHostValidator.CanLiquidsTouchByBarrier(world, pos, above);
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
    /// In-aqueduct: an orientation-aligned neighbor qualifies if it is one of:
    ///   1. A fully empty cell (solid and fluid air) - aqueduct opens onto open air.
    ///   2. Solid air with liquid matching the candidate intake family - aqueduct opens onto a same-family pool.
    ///   3. Another aqueduct cell whose fluid layer is same-family managed water - propagation along a continuous pipe.
    /// In all cases, the barrier between the candidate cell and the neighbor must allow liquid transfer.
    /// </summary>
    private static bool IsQualifyingAlignedNeighbor(
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

        bool qualifies = false;
        if (neighborSolid.Id == 0 && neighborFluid.Id == 0)
        {
            qualifies = true;
        }
        else if (neighborSolid.Id == 0 &&
            manager.TryResolveIntakeWaterFamily(neighborFluid, out string neighborFamilyId) &&
            string.Equals(neighborFamilyId, candidateFamilyId, StringComparison.Ordinal))
        {
            qualifies = true;
        }
        else if (IsHardcoreWaterAqueduct(neighborSolid) &&
            manager.TryResolveManagedWaterFamily(neighborFluid, out string aqueductNeighborFamilyId) &&
            string.Equals(aqueductNeighborFamilyId, candidateFamilyId, StringComparison.Ordinal))
        {
            qualifies = true;
        }

        if (!qualifies)
        {
            return false;
        }

        return ArchimedesFluidHostValidator.CanLiquidsTouchByBarrier(world, origin, neighborPos);
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

        if (ArchimedesRelayAdjacency.IsRelayBelowBlockedByWater(world, pos))
        {
            return false;
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

            if (!string.Equals(neighborFamilyId, candidateFamilyId, StringComparison.Ordinal))
            {
                continue;
            }

            if (ArchimedesFluidHostValidator.CanLiquidsTouchByBarrier(world, pos, neighborPos))
            {
                return true;
            }
        }

        return false;
    }
}
