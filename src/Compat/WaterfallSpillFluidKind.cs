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
        if (existingFluid == null || incomingFluid == null)
        {
            return false;
        }

        if (ArchimedesWaterFamilies.TryResolveManagedFamily(existingFluid, out ArchimedesWaterFamily managedExisting) &&
            ArchimedesWaterFamilies.TryResolveVanillaFamily(incomingFluid, out ArchimedesWaterFamily vanillaIncoming))
        {
            return managedExisting.Id == vanillaIncoming.Id;
        }

        if (ArchimedesWaterFamilies.TryResolveVanillaFamily(existingFluid, out ArchimedesWaterFamily vanillaExisting) &&
            ArchimedesWaterFamilies.TryResolveManagedFamily(incomingFluid, out ArchimedesWaterFamily managedIncoming))
        {
            return vanillaExisting.Id == managedIncoming.Id;
        }

        if (ArchimedesWaterFamilies.TryResolveManagedFamily(existingFluid, out ArchimedesWaterFamily managedA) &&
            ArchimedesWaterFamilies.TryResolveManagedFamily(incomingFluid, out ArchimedesWaterFamily managedB))
        {
            return managedA.Id == managedB.Id;
        }

        return string.Equals(
            existingFluid.FirstCodePart(0),
            incomingFluid.FirstCodePart(0),
            System.StringComparison.Ordinal);
    }
}
