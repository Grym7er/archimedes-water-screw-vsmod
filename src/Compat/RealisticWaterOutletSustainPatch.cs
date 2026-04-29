using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace ArchimedesScrew;

[HarmonyPatch]
internal static class RealisticWaterOutletSustainPatch
{
    private const string RealisticWaterBehaviorType = "RealisticWater.BlockBehaviorRealisticSpreadingLiquid";

    public static ICoreServerAPI? Api;

    private static bool loggedResolveFailure;
    private static MethodInfo? resolvedTargetMethod;

    internal static bool LastPrepareSucceeded { get; private set; }

    [HarmonyPrepare]
    private static bool Prepare()
    {
        LastPrepareSucceeded = false;

        Type? behaviorType = AccessTools.TypeByName(RealisticWaterBehaviorType);
        if (behaviorType == null)
        {
            LogResolveFailureOnce("could not resolve type " + RealisticWaterBehaviorType);
            return false;
        }

        resolvedTargetMethod = AccessTools.Method(
            behaviorType,
            "TryLoweringLiquidLevel",
            new[] { typeof(Block), typeof(IWorldAccessor), typeof(BlockPos) });

        if (resolvedTargetMethod == null)
        {
            LogResolveFailureOnce("could not resolve TryLoweringLiquidLevel(Block, IWorldAccessor, BlockPos)");
            return false;
        }

        loggedResolveFailure = false;
        LastPrepareSucceeded = true;
        return true;
    }

    [HarmonyTargetMethod]
    private static MethodBase TargetMethod()
    {
        return resolvedTargetMethod!;
    }

    [HarmonyPrefix]
    private static bool Prefix(Block ourBlock, IWorldAccessor world, BlockPos pos, ref bool __result)
    {
        bool sustain = RealisticWaterCompatBridge.ShouldSustainOutlet(ourBlock, world, pos);
        if (!sustain)
        {
            if (TryForceLowerOrphanedFallingBlock(ourBlock, world, pos))
            {
                __result = true;
                return false;
            }

            return true;
        }

        ArchimedesPerf.AddCount("compat.realisticwater.outletSustain.preventedLowering");
        __result = false;
        return false;
    }

    private static bool TryForceLowerOrphanedFallingBlock(Block ourBlock, IWorldAccessor world, BlockPos pos)
    {
        if (ourBlock.Code?.Path.StartsWith("realisticwater-d-", StringComparison.Ordinal) != true ||
            ourBlock.LiquidLevel <= 1 ||
            !RealisticWaterCompatBridge.IsRecentShutdownDrainCascadeCell(pos))
        {
            return false;
        }

        BlockPos abovePos = pos.UpCopy();
        Block aboveFluid = world.BlockAccessor.GetBlock(abovePos, BlockLayersAccess.Fluid);
        if (!IsSameRealisticWaterFamily(ourBlock, aboveFluid) || aboveFluid.LiquidLevel >= ourBlock.LiquidLevel)
        {
            return false;
        }

        foreach (BlockFacing facing in BlockFacing.HORIZONTALS)
        {
            BlockPos neighborPos = pos.AddCopy(facing);
            Block neighborFluid = world.BlockAccessor.GetBlock(neighborPos, BlockLayersAccess.Fluid);
            if (IsSameRealisticWaterFamily(ourBlock, neighborFluid) && neighborFluid.LiquidLevel >= ourBlock.LiquidLevel)
            {
                return false;
            }
        }

        string lowerPath = $"realisticwater-d-{ourBlock.LiquidLevel - 1}-0";
        Block? lowerBlock = world.GetBlock(new AssetLocation(ourBlock.Code.Domain, lowerPath));
        if (lowerBlock == null)
        {
            return false;
        }

        world.BlockAccessor.SetBlock(lowerBlock.BlockId, pos, BlockLayersAccess.Fluid);
        lowerBlock.OnNeighbourBlockChange(world, pos, abovePos);
        NotifyRealisticWaterNeighbors(world, pos);
        world.BlockAccessor.MarkBlockDirty(pos);
        ArchimedesPerf.AddCount("compat.realisticwater.shutdownDrain.forceLower");
        return true;
    }

    private static bool IsSameRealisticWaterFamily(Block block, Block other)
    {
        return block.Code?.Path.StartsWith("realisticwater-", StringComparison.Ordinal) == true &&
               other.Code?.Path.StartsWith("realisticwater-", StringComparison.Ordinal) == true &&
               string.Equals(block.LiquidCode, other.LiquidCode, StringComparison.Ordinal);
    }

    private static void NotifyRealisticWaterNeighbors(IWorldAccessor world, BlockPos pos)
    {
        foreach (BlockFacing facing in BlockFacing.ALLFACES)
        {
            BlockPos neighborPos = pos.AddCopy(facing);
            Block neighborFluid = world.BlockAccessor.GetBlock(neighborPos, BlockLayersAccess.Fluid);
            if (neighborFluid.Code?.Path.StartsWith("realisticwater-", StringComparison.Ordinal) == true)
            {
                neighborFluid.OnNeighbourBlockChange(world, neighborPos, pos);
            }
        }
    }

    private static void LogResolveFailureOnce(string detail)
    {
        if (loggedResolveFailure)
        {
            return;
        }

        loggedResolveFailure = true;
        Api?.Logger.Warning(
            "{0} [compat/realisticwater] {1}; outlet sustain patch skipped",
            ArchimedesScrewModSystem.LogPrefix,
            detail);
    }
}
