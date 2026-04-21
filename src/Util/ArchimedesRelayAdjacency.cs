using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ArchimedesScrew;

/// <summary>
/// Shared geometry for Archimedes relay placement and debug overlay (below-block support + horizontal whitelist).
/// </summary>
public static class ArchimedesRelayAdjacency
{
    /// <summary>
    /// True when the cell under <paramref name="relayPos"/> has vanilla or managed water in fluid or solid layer
    /// (same rule as relay whitelist support).
    /// </summary>
    public static bool IsRelayBelowBlockedByWater(IWorldAccessor world, BlockPos relayPos)
    {
        IBlockAccessor accessor = world.BlockAccessor;
        BlockPos belowPos = relayPos.DownCopy();
        Block belowFluid = accessor.GetBlock(belowPos, BlockLayersAccess.Fluid);
        if (IsWaterBlock(belowFluid))
        {
            return true;
        }

        Block belowSolid = accessor.GetBlock(belowPos);
        return IsWaterBlock(belowSolid);
    }

    /// <summary>
    /// Aqueduct-branch variant of <see cref="IsRelayBelowBlockedByWater"/>. Returns false (not blocked)
    /// only when the cell directly below is itself an HCW aqueduct carrying same-family managed water,
    /// i.e. a legitimate vertical cascade. Anything else (vanilla water, cross-family managed water,
    /// natural lake, unmanaged fluid) keeps blocking exactly like the strict guard.
    /// </summary>
    public static bool IsRelayBelowBlockedByNonAqueductWater(
        IWorldAccessor world,
        BlockPos relayPos,
        string candidateFamilyId,
        ArchimedesWaterNetworkManager manager)
    {
        if (!IsRelayBelowBlockedByWater(world, relayPos))
        {
            return false;
        }

        BlockPos belowPos = relayPos.DownCopy();
        Block belowSolid = world.BlockAccessor.GetBlock(belowPos);
        if (!ArchimedesAqueductDetector.IsHardcoreWaterAqueduct(belowSolid))
        {
            return true;
        }

        Block belowFluid = world.BlockAccessor.GetBlock(belowPos, BlockLayersAccess.Fluid);
        if (!manager.TryResolveManagedWaterFamily(belowFluid, out string belowFamilyId) ||
            !string.Equals(belowFamilyId, candidateFamilyId, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    public static bool IsRelaySupportAndAdjacentWhitelistSatisfied(IWorldAccessor world, BlockPos relayPos)
    {
        IBlockAccessor accessor = world.BlockAccessor;
        if (IsRelayBelowBlockedByWater(world, relayPos))
        {
            return false;
        }

        Block belowSolid = accessor.GetBlock(relayPos.DownCopy());
        AssetLocation? belowCode = belowSolid.Code;
        bool belowIsTallgrass = belowCode != null &&
                                string.Equals(belowCode.Domain, "game", StringComparison.Ordinal) &&
                                belowCode.Path.StartsWith("tallgrass-", StringComparison.Ordinal);
        if (belowSolid.Id == 0 && !belowIsTallgrass)
        {
            return false;
        }

        foreach (BlockFacing face in BlockFacing.HORIZONTALS)
        {
            BlockPos adjacentPos = relayPos.AddCopy(face);
            Block adjacentSolid = accessor.GetBlock(adjacentPos);
            Block adjacentFluid = accessor.GetBlock(adjacentPos, BlockLayersAccess.Fluid);
            bool isAirCell = adjacentSolid.Id == 0 && adjacentFluid.Id == 0;
            bool isIntake = adjacentSolid is BlockWaterArchimedesScrew screw && screw.IsIntakeBlock();
            AssetLocation? adjacentCode = adjacentSolid.Code;
            bool isTallgrass = adjacentCode != null &&
                               string.Equals(adjacentCode.Domain, "game", StringComparison.Ordinal) &&
                               adjacentCode.Path.StartsWith("tallgrass-", StringComparison.Ordinal);
            bool isWhitelisted = isIntake || isTallgrass;
            if (isAirCell || isWhitelisted)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWaterBlock(Block block)
    {
        return block.IsLiquid() &&
               (ArchimedesWaterFamilies.TryResolveVanillaFamily(block, out _) ||
                ArchimedesWaterFamilies.TryResolveManagedFamily(block, out _));
    }
}
