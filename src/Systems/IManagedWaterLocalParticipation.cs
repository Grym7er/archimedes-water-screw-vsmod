using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ArchimedesScrew;

/// <summary>
/// Local, controller-side extension seam for managed-water participation checks.
/// Keeps global ownership and BFS logic in the manager unchanged.
/// </summary>
internal interface IManagedWaterLocalParticipation
{
    bool IsRelayCreationCandidate(
        IWorldAccessor world,
        BlockPos pos,
        ArchimedesWaterNetworkManager manager
    );
}

internal sealed class DefaultManagedWaterLocalParticipation : IManagedWaterLocalParticipation
{
    public bool IsRelayCreationCandidate(
        IWorldAccessor world,
        BlockPos pos,
        ArchimedesWaterNetworkManager manager
    )
    {
        return ArchimedesRelayCandidateRules.IsPromotableRelayCandidate(world, pos, manager);
    }
}
