using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace ArchimedesScrew;

[HarmonyPatch]
internal static class WaterfallCompatPatch
{
    public static ICoreServerAPI? Api;
    public static bool DebugLoggingEnabled;
    private static bool loggedResolveFailure;
    private static MethodInfo? resolvedTargetMethod;

    [HarmonyPrepare]
    static bool Prepare()
    {
        resolvedTargetMethod = ResolveBlockLiquidContainerSpillContents();
        if (resolvedTargetMethod != null)
        {
            loggedResolveFailure = false;
            return true;
        }

        if (!loggedResolveFailure)
        {
            Api?.Logger.Warning(
                "{0} [compat/waterfall] Could not resolve BlockLiquidContainerBase.SpillContents; patch class skipped this pass",
                ArchimedesScrewModSystem.LogPrefix
            );
            loggedResolveFailure = true;
        }

        return false;
    }

    static MethodBase TargetMethod()
    {
        return resolvedTargetMethod!;
    }

    // Waterfall 1.1.0 patches BlockLiquidContainerBase.SpillContents; we patch the same method.
    // Returning false skips later prefixes (including Waterfall) and the original; __result is the bool return value.
    [HarmonyPrefix]
    [HarmonyPriority(-100)]
    public static bool Prefix(ItemSlot containerSlot, EntityAgent byEntity, BlockSelection blockSel, ref bool __result)
    {
        if (byEntity?.World == null || blockSel == null)
        {
            return true;
        }

        if (!IsArchimedesManagedWaterNearby(byEntity.World, blockSel))
        {
            return true;
        }

        if (DebugLoggingEnabled)
        {
            byEntity.World.Logger.Notification(
                "{0} [compat/waterfall] Bypassing Waterfall spill prefix near managed Archimedes water at {1}",
                ArchimedesScrewModSystem.LogPrefix,
                blockSel.Position
            );
        }

        __result = true;
        return false;
    }

    private static bool IsArchimedesManagedWaterNearby(IWorldAccessor world, BlockSelection blockSel)
    {
        BlockPos placePos = blockSel.Position;
        BlockPos targetPos = blockSel.Position.AddCopy(blockSel.Face);

        Block placeFluid = world.BlockAccessor.GetBlock(placePos, BlockLayersAccess.Fluid);
        if (IsArchimedesManagedFluid(placeFluid))
        {
            return true;
        }

        Block targetFluid = world.BlockAccessor.GetBlock(targetPos, BlockLayersAccess.Fluid);
        return IsArchimedesManagedFluid(targetFluid);
    }

    private static bool IsArchimedesManagedFluid(Block block)
    {
        return block.Code?.Domain == ArchimedesScrewModSystem.ModId &&
               ArchimedesWaterFamilies.IsManagedWater(block);
    }

    private static MethodInfo? ResolveBlockLiquidContainerSpillContents()
    {
        // SpillContents exists on the game DLL type but is not in the compile-time API surface here.
        return AccessTools.Method(
            typeof(BlockLiquidContainerBase),
            "SpillContents",
            new[]
            {
                typeof(ItemSlot),
                typeof(EntityAgent),
                typeof(BlockSelection),
            }
        );
    }
}
