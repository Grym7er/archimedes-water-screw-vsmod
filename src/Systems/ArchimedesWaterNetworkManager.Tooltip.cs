using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ArchimedesScrew;

public readonly record struct ArchimedesWaterDebugTooltipFlags(
    bool ManagedWaterBlock,
    bool Height7SourceBlock,
    bool OwnedManagedSource,
    bool RelayOwned,
    bool RelayCandidate
);

public sealed partial class ArchimedesWaterNetworkManager
{
    /// <summary>Server-only: full tooltip flags for water debug (single source of truth for snapshot + network query).</summary>
    public ArchimedesWaterDebugTooltipFlags CollectWaterDebugTooltipFlags(BlockPos pos)
    {
        Block fluid = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        bool managedWater = IsArchimedesWaterBlock(fluid);
        bool height7 = IsArchimedesSourceBlock(fluid);
        bool owned = TryGetSourceOwner(pos, out _);
        bool relay = IsRelayOwnedPosition(pos);
        bool candidate = managedWater &&
                          ArchimedesRelayCandidateRules.IsPromotableRelayCandidate(api.World, pos, this);
        return new ArchimedesWaterDebugTooltipFlags(managedWater, height7, owned, relay, candidate);
    }

}
