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
