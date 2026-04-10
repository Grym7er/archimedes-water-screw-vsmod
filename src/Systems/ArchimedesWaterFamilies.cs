using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ArchimedesScrew;

public readonly record struct ArchimedesWaterFamily(string Id, string VanillaCode, string ManagedCode)
{
    public string VanillaPrefix => VanillaCode + "-";
    public string ManagedPrefix => ManagedCode + "-";
}

public readonly record struct ArchimedesOutletState(string ControllerId, BlockPos SeedPos, string FamilyId);

public static class ArchimedesWaterFamilies
{
    public static readonly ArchimedesWaterFamily Fresh = new("fresh", "water", "archimedes-water");
    public static readonly ArchimedesWaterFamily Salt = new("salt", "saltwater", "archimedes-saltwater");
    public static readonly ArchimedesWaterFamily Boiling = new("boiling", "boilingwater", "archimedes-boilingwater");

    public static IReadOnlyList<ArchimedesWaterFamily> All { get; } = new[]
    {
        Fresh,
        Salt,
        Boiling
    };

    public static bool TryResolveVanillaFamily(Block block, out ArchimedesWaterFamily family)
    {
        string? path = block.Code?.Path;
        if (path != null)
        {
            foreach (ArchimedesWaterFamily candidate in All)
            {
                if (path.StartsWith(candidate.VanillaPrefix, StringComparison.Ordinal))
                {
                    family = candidate;
                    return true;
                }
            }
        }

        family = default;
        return false;
    }

    public static bool TryResolveManagedFamily(Block block, out ArchimedesWaterFamily family)
    {
        string? path = block.Code?.Path;
        if (path != null)
        {
            foreach (ArchimedesWaterFamily candidate in All)
            {
                if (path.StartsWith(candidate.ManagedPrefix, StringComparison.Ordinal))
                {
                    family = candidate;
                    return true;
                }
            }
        }

        family = default;
        return false;
    }

    public static bool IsManagedWater(Block block)
    {
        return TryResolveManagedFamily(block, out _);
    }

    public static ArchimedesWaterFamily GetById(string familyId)
    {
        foreach (ArchimedesWaterFamily family in All)
        {
            if (string.Equals(family.Id, familyId, StringComparison.Ordinal))
            {
                return family;
            }
        }

        string validIds = string.Join(", ", All.Select(f => f.Id));
        throw new InvalidOperationException(
            $"No Archimedes water family with id '{familyId}'. Valid ids: {validIds}");
    }

    public static AssetLocation GetManagedBlockCode(string familyId, string flow, int height)
    {
        ArchimedesWaterFamily family = GetById(familyId);
        return new AssetLocation(ArchimedesScrewModSystem.ModId, $"{family.ManagedCode}-{flow}-{height}");
    }
}
