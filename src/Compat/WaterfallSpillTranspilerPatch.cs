using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace ArchimedesScrew;

/// <summary>
/// Waterfall 1.1.0 replaces <see cref="BlockLiquidContainerBase.SpillContents"/> and compares fluid kinds via
/// <c>FirstCodePart(0)</c> only; Archimedes blocks use a different first segment. Transpile those checks to
/// <see cref="WaterfallSpillFluidKind.AreCompatibleForSpill"/>.
/// </summary>
[HarmonyPatch]
internal static class WaterfallSpillTranspilerPatch
{
    public const string WaterfallSpillNestedType = "WaterfallMod.Waterfall+SpillContentsModification";

    public static ICoreServerAPI? Api;
    public static bool DebugLoggingEnabled;

    private static bool loggedResolveFailure;
    private static bool loggedTranspilerMismatch;
    private static MethodInfo? resolvedTargetMethod;

    /// <summary>Set during <see cref="Prepare"/>; used by <see cref="WaterfallCompatBridge"/> so <c>isPatched</c> is only true when Harmony actually applied.</summary>
    internal static bool LastPrepareSucceeded { get; private set; }

    [HarmonyPrepare]
    private static bool Prepare()
    {
        LastPrepareSucceeded = false;
        Type? nested = AccessTools.TypeByName(WaterfallSpillNestedType);
        if (nested == null)
        {
            LogResolveFailureOnce("could not resolve nested type " + WaterfallSpillNestedType);
            return false;
        }

        resolvedTargetMethod = AccessTools.Method(
            nested,
            "Prefix",
            new[]
            {
                typeof(ItemSlot),
                typeof(EntityAgent),
                typeof(BlockSelection),
                typeof(bool).MakeByRefType(),
            }
        );

        if (resolvedTargetMethod == null)
        {
            LogResolveFailureOnce("could not resolve Waterfall SpillContentsModification.Prefix");
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
        MethodInfo? firstCodePart = AccessTools.Method(
            typeof(RegistryObject),
            nameof(RegistryObject.FirstCodePart),
            new[] { typeof(int) }
        );

        MethodInfo? stringEq = AccessTools.Method(
            typeof(string),
            "op_Equality",
            new[] { typeof(string), typeof(string) }
        );

        MethodInfo? compat = AccessTools.Method(
            typeof(WaterfallSpillFluidKind),
            nameof(WaterfallSpillFluidKind.AreCompatibleForSpill),
            new[] { typeof(Block), typeof(Block) }
        );

        if (firstCodePart == null || stringEq == null || compat == null)
        {
            return instructions;
        }

        var codes = new List<CodeInstruction>(instructions);
        int replaced = 0;
        for (int i = 0; i <= codes.Count - 6; i++)
        {
            if (!IsFirstCodePartEqualitySequence(codes, i, firstCodePart, stringEq))
            {
                continue;
            }

            object? block2Local = codes[i + 2].operand;
            codes.RemoveRange(i, 6);
            codes.Insert(i, new CodeInstruction(OpCodes.Ldloc_S, block2Local));
            codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, compat));
            replaced++;
            i++;
        }

        if (replaced != 2 && Api != null && !loggedTranspilerMismatch)
        {
            loggedTranspilerMismatch = true;
            Api.Logger.Warning(
                "{0} [compat/waterfall] Spill transpiler expected 2 FirstCodePart equality sites (Waterfall 1.1.0); found {1}. Update patch if Waterfall changed.",
                ArchimedesScrewModSystem.LogPrefix,
                replaced
            );
        }

        if (DebugLoggingEnabled && Api != null && replaced > 0)
        {
            Api.Logger.Notification(
                "{0} [compat/waterfall] Spill transpiler applied {1} replacement(s)",
                ArchimedesScrewModSystem.LogPrefix,
                replaced
            );
        }

        return codes;
    }

    private static bool IsFirstCodePartEqualitySequence(
        List<CodeInstruction> codes,
        int i,
        MethodInfo firstCodePart,
        MethodInfo stringEq
    )
    {
        return codes[i].opcode == OpCodes.Ldc_I4_0
            && codes[i + 1].opcode == OpCodes.Callvirt && Equals(codes[i + 1].operand, firstCodePart)
            && codes[i + 2].opcode == OpCodes.Ldloc_S
            && codes[i + 3].opcode == OpCodes.Ldc_I4_0
            && codes[i + 4].opcode == OpCodes.Callvirt && Equals(codes[i + 4].operand, firstCodePart)
            && codes[i + 5].opcode == OpCodes.Call && Equals(codes[i + 5].operand, stringEq);
    }

    private static void LogResolveFailureOnce(string detail)
    {
        if (loggedResolveFailure)
        {
            return;
        }

        loggedResolveFailure = true;
        Api?.Logger.Warning(
            "{0} [compat/waterfall] {1}; Waterfall spill transpiler skipped",
            ArchimedesScrewModSystem.LogPrefix,
            detail
        );
    }
}
