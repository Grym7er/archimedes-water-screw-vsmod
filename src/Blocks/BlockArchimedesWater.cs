using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ArchimedesScrew;

internal static class ArchimedesWaterBlockHelper
{
    internal static string AppendWaterDebugTooltip(IWorldAccessor world, BlockPos pos, Block fluidBlock, string baseInfo)
    {
        if (world.Side != EnumAppSide.Client)
        {
            return baseInfo;
        }

        ArchimedesScrewModSystem? sys = world.Api.ModLoader.GetModSystem<ArchimedesScrewModSystem>();
        string? appendix = sys?.TryBuildWaterDebugTooltipAppendix(pos, fluidBlock);
        return appendix == null ? baseInfo : baseInfo + appendix;
    }

    public static void NotifyManagerOnRemoval(IWorldAccessor world, BlockPos pos, Block removedBlock)
    {
        if (world.Side != EnumAppSide.Server)
        {
            return;
        }

        ArchimedesWaterNetworkManager? manager = world.Api.ModLoader.GetModSystem<ArchimedesScrewModSystem>().WaterManager;
        if (manager == null)
        {
            return;
        }

        Block replacementFluid = world.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        // Keep ownership stable across managed variant transitions (e.g. still-7 -> still-6).
        // The old block is "removed" by fluid simulation, but this is not true source loss.
        if (manager.IsArchimedesWaterBlock(replacementFluid) &&
            manager.TryResolveManagedWaterFamily(removedBlock, out string removedManagedFamilyId) &&
            manager.TryResolveManagedWaterFamily(replacementFluid, out string replacementManagedFamilyId) &&
            string.Equals(removedManagedFamilyId, replacementManagedFamilyId, StringComparison.Ordinal))
        {
            return;
        }

        // Defensive HCW interop: when an aqueduct cell flips from managed water to vanilla water of the same
        // family (typical HCW refill cycle), queue a fast reclaim with the prior owner as a hint so the
        // ownership "drop" is observable for at most one intent-tick instead of triggering a full re-promotion.
        bool removedWasManaged = manager.TryResolveManagedWaterFamily(removedBlock, out string removedFamilyForHandoff);
        if (removedWasManaged &&
            ArchimedesAqueductDetector.IsAqueductCell(world, pos) &&
            manager.TryResolveVanillaWaterFamily(replacementFluid, out string vanillaInAqueductFamily) &&
            string.Equals(removedFamilyForHandoff, vanillaInAqueductFamily, StringComparison.Ordinal))
        {
            manager.TryGetSourceOwner(pos, out string aqueductOwnerHint);
            manager.OnManagedWaterRemoved(pos);
            manager.EnqueueConversionIntent(
                pos,
                vanillaInAqueductFamily,
                ownerHintControllerId: string.IsNullOrEmpty(aqueductOwnerHint) ? null : aqueductOwnerHint,
                reason: "aqueduct refill: same-family vanilla replaced managed water",
                playerIntent: false
            );
            return;
        }

        manager.OnManagedWaterRemoved(pos);
        Block fluid = world.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (manager.TryResolveVanillaWaterFamily(fluid, out string vanillaFamilyId))
        {
            manager.EnqueueConversionIntent(
                pos,
                vanillaFamilyId,
                ownerHintControllerId: null,
                reason: "player-placed source replaced managed water",
                playerIntent: true
            );
        }
    }

    public static void TryConvertNeighbourSource(IWorldAccessor world, BlockPos neibpos, BlockPos referencePos)
    {
        if (world.Side != EnumAppSide.Server)
        {
            return;
        }

        ArchimedesWaterNetworkManager? manager = world.Api.ModLoader.GetModSystem<ArchimedesScrewModSystem>().WaterManager;
        if (manager == null)
        {
            return;
        }

        Block referenceFluid = world.BlockAccessor.GetBlock(referencePos, BlockLayersAccess.Fluid);
        if (!manager.TryResolveManagedWaterFamily(referenceFluid, out string familyId))
        {
            return;
        }

        if (!ArchimedesFluidHostValidator.CanLiquidsTouchByBarrier(world, referencePos, neibpos))
        {
            return;
        }

        manager.EnqueueConversionIntent(
            neibpos,
            familyId,
            ownerHintControllerId: null,
            reason: "player-placed source adjacent to managed water",
            playerIntent: true
        );
    }
}

public sealed class BlockArchimedesWaterStill : BlockWater
{
    public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
    {
        string baseInfo = base.GetPlacedBlockInfo(world, pos, forPlayer) ?? string.Empty;
        return ArchimedesWaterBlockHelper.AppendWaterDebugTooltip(world, pos, this, baseInfo);
    }

    public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neibpos)
    {
        base.OnNeighbourBlockChange(world, pos, neibpos);
        ArchimedesWaterBlockHelper.TryConvertNeighbourSource(world, neibpos, pos);
    }

    public override void OnBlockRemoved(IWorldAccessor world, BlockPos pos)
    {
        base.OnBlockRemoved(world, pos);
        ArchimedesWaterBlockHelper.NotifyManagerOnRemoval(world, pos, this);
    }
}

public sealed class BlockArchimedesWaterFlowing : BlockWaterflowing
{
    public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
    {
        string baseInfo = base.GetPlacedBlockInfo(world, pos, forPlayer) ?? string.Empty;
        return ArchimedesWaterBlockHelper.AppendWaterDebugTooltip(world, pos, this, baseInfo);
    }

    public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neibpos)
    {
        base.OnNeighbourBlockChange(world, pos, neibpos);
        ArchimedesWaterBlockHelper.TryConvertNeighbourSource(world, neibpos, pos);
    }

    public override void OnBlockRemoved(IWorldAccessor world, BlockPos pos)
    {
        base.OnBlockRemoved(world, pos);
        ArchimedesWaterBlockHelper.NotifyManagerOnRemoval(world, pos, this);
    }
}

public sealed class BlockArchimedesWaterfall : BlockWaterfall
{
    public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
    {
        string baseInfo = base.GetPlacedBlockInfo(world, pos, forPlayer) ?? string.Empty;
        return ArchimedesWaterBlockHelper.AppendWaterDebugTooltip(world, pos, this, baseInfo);
    }

    public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neibpos)
    {
        base.OnNeighbourBlockChange(world, pos, neibpos);
        ArchimedesWaterBlockHelper.TryConvertNeighbourSource(world, neibpos, pos);
    }

    public override void OnBlockRemoved(IWorldAccessor world, BlockPos pos)
    {
        base.OnBlockRemoved(world, pos);
        ArchimedesWaterBlockHelper.NotifyManagerOnRemoval(world, pos, this);
    }
}
