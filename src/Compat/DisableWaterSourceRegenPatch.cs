using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace ArchimedesScrew;

/// <summary>
/// Disables dynamic source regeneration for water by preventing the source-upgrade call
/// in BlockBehaviorFiniteSpreadingLiquid.SpreadAndUpdateLiquidLevels().
/// </summary>
[HarmonyPatch]
internal static class DisableWaterSourceRegenPatch
{
    public static ICoreServerAPI? Api;

    private static bool loggedResolveFailure;
    private static bool loggedTranspilerMismatch;
    private static MethodInfo? resolvedTargetMethod;

    internal static bool LastPrepareSucceeded { get; private set; }

    [HarmonyPrepare]
    private static bool Prepare()
    {
        LastPrepareSucceeded = false;

        resolvedTargetMethod = AccessTools.Method(
            typeof(BlockBehaviorFiniteSpreadingLiquid),
            "SpreadAndUpdateLiquidLevels",
            new[] { typeof(IWorldAccessor), typeof(BlockPos) }
        );

        if (resolvedTargetMethod == null)
        {
            LogResolveFailureOnce("could not resolve BlockBehaviorFiniteSpreadingLiquid.SpreadAndUpdateLiquidLevels");
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

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo? originalCall = AccessTools.Method(
            typeof(BlockBehaviorFiniteSpreadingLiquid),
            nameof(BlockBehaviorFiniteSpreadingLiquid.GetMoreLiquidBlockId),
            new[] { typeof(IWorldAccessor), typeof(BlockPos), typeof(Block) }
        );

        MethodInfo? replacementCall = AccessTools.Method(
            typeof(DisableWaterSourceRegenPatch),
            nameof(GetMoreLiquidBlockIdNoWaterRegen)
        );

        if (originalCall == null || replacementCall == null)
        {
            return instructions;
        }

        List<CodeInstruction> codes = new(instructions);
        int replaced = 0;

        for (int i = 0; i < codes.Count; i++)
        {
            if ((codes[i].opcode == OpCodes.Call || codes[i].opcode == OpCodes.Callvirt) && Equals(codes[i].operand, originalCall))
            {
                codes[i] = new CodeInstruction(OpCodes.Call, replacementCall);
                replaced++;
            }
        }

        if (replaced != 1 && Api != null && !loggedTranspilerMismatch)
        {
            loggedTranspilerMismatch = true;
            Api.Logger.Warning(
                "{0} [compat/source-regen] Expected exactly 1 source-upgrade call in SpreadAndUpdateLiquidLevels; found {1}. Upstream method may have changed.",
                ArchimedesScrewModSystem.LogPrefix,
                replaced
            );
        }

        return codes;
    }

    // Harmony replacement method signature must match transpiled call target plus instance.
    private static int GetMoreLiquidBlockIdNoWaterRegen(
        BlockBehaviorFiniteSpreadingLiquid instance,
        IWorldAccessor world,
        BlockPos pos,
        Block block
    )
    {
        ArchimedesPerf.AddCount("compat.sourceRegen.call");
        if (block.LiquidCode == "water")
        {
            // Prevent water source promotion by keeping current block id.
            ArchimedesPerf.AddCount("compat.sourceRegen.waterPrevented");
            return block.BlockId;
        }

        ArchimedesPerf.AddCount("compat.sourceRegen.passThrough");
        return instance.GetMoreLiquidBlockId(world, pos, block);
    }

    private static void LogResolveFailureOnce(string detail)
    {
        if (loggedResolveFailure)
        {
            return;
        }

        loggedResolveFailure = true;
        Api?.Logger.Warning(
            "{0} [compat/source-regen] {1}; patch skipped",
            ArchimedesScrewModSystem.LogPrefix,
            detail
        );
    }
}
