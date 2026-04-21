using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ArchimedesScrew;

/// <summary>
/// Centralized aqueduct detection for HardcoreWater interop. Today the check is a substring match on the block path;
/// callers should funnel through this helper so a future migration to a block attribute / tag is a single edit.
/// </summary>
public static class ArchimedesAqueductDetector
{
    public static bool IsHardcoreWaterAqueduct(Block solid)
    {
        AssetLocation? code = solid.Code;
        if (code == null || !string.Equals(code.Domain, "hardcorewater", StringComparison.Ordinal))
        {
            return false;
        }

        return code.Path.Contains("aqueduct", StringComparison.Ordinal);
    }

    public static bool IsAqueductCell(IWorldAccessor world, BlockPos pos)
    {
        Block solid = world.BlockAccessor.GetBlock(pos);
        return IsHardcoreWaterAqueduct(solid);
    }
}
