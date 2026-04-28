using Vintagestory.API.Common;

namespace ArchimedesScrew;

/// <summary>
/// Fluid identity checks for Waterfall 1.1.0 spill logic (replaces FirstCodePart-only equality).
/// </summary>
public static class WaterfallSpillFluidKind
{
    /// <summary>
    /// Whether <paramref name="incomingFluid"/> may merge into a cell that already holds <paramref name="existingFluid"/>,
    /// matching Waterfall's intent but allowing Archimedes managed blocks vs same-family vanilla.
    /// </summary>
    public static bool AreCompatibleForSpill(Block existingFluid, Block incomingFluid)
    {
        ArchimedesPerf.AddCount("compat.waterfallSpill.compatChecks");
        if (existingFluid == null || incomingFluid == null)
        {
            ArchimedesPerf.AddCount("compat.waterfallSpill.nullInput");
            return false;
        }

        if (ArchimedesWaterFamilies.TryResolveManagedFamily(existingFluid, out ArchimedesWaterFamily managedExisting) &&
            ArchimedesWaterFamilies.TryResolveVanillaFamily(incomingFluid, out ArchimedesWaterFamily vanillaIncoming))
        {
            bool matches = managedExisting.Id == vanillaIncoming.Id;
            ArchimedesPerf.AddCount(matches
                ? "compat.waterfallSpill.managedExisting.vanillaIncoming.match"
                : "compat.waterfallSpill.managedExisting.vanillaIncoming.miss");
            return matches;
        }

        if (ArchimedesWaterFamilies.TryResolveVanillaFamily(existingFluid, out ArchimedesWaterFamily vanillaExisting) &&
            ArchimedesWaterFamilies.TryResolveManagedFamily(incomingFluid, out ArchimedesWaterFamily managedIncoming))
        {
            bool matches = vanillaExisting.Id == managedIncoming.Id;
            ArchimedesPerf.AddCount(matches
                ? "compat.waterfallSpill.vanillaExisting.managedIncoming.match"
                : "compat.waterfallSpill.vanillaExisting.managedIncoming.miss");
            return matches;
        }

        if (ArchimedesWaterFamilies.TryResolveManagedFamily(existingFluid, out ArchimedesWaterFamily managedA) &&
            ArchimedesWaterFamilies.TryResolveManagedFamily(incomingFluid, out ArchimedesWaterFamily managedB))
        {
            bool matches = managedA.Id == managedB.Id;
            ArchimedesPerf.AddCount(matches
                ? "compat.waterfallSpill.managedToManaged.match"
                : "compat.waterfallSpill.managedToManaged.miss");
            return matches;
        }

        bool fallback = string.Equals(
            existingFluid.FirstCodePart(0),
            incomingFluid.FirstCodePart(0),
            System.StringComparison.Ordinal);
        ArchimedesPerf.AddCount(fallback
            ? "compat.waterfallSpill.fallback.match"
            : "compat.waterfallSpill.fallback.miss");
        return fallback;
    }
}
