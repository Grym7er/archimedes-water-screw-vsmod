using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

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
        resolvedTargetMethod = ResolveWaterfallSpillMethod();
        if (resolvedTargetMethod != null)
        {
            loggedResolveFailure = false;
            return true;
        }

        if (!loggedResolveFailure)
        {
            Api?.Logger.Warning(
                "{0} [compat/waterfall] Could not resolve Waterfall spill target; patch class skipped this pass",
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

    [HarmonyPrefix]
    // Patch target: Waterfall.SpillContents(ItemSlot, EntityAgent, BlockSelection, ref bool)
    // If this returns false, Waterfall's prefix body is skipped; setting __result=true keeps original spill flow alive.
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

    private static MethodInfo? ResolveWaterfallSpillMethod()
    {
        Type? waterfallType = AccessTools.TypeByName("Waterfall");
        if (waterfallType == null)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                waterfallType = assembly.GetType("Waterfall", throwOnError: false, ignoreCase: false);
                if (waterfallType != null)
                {
                    break;
                }
            }
        }

        if (waterfallType != null)
        {
            MethodInfo? direct = FindSpillMethodOnType(waterfallType);
            if (direct != null)
            {
                return direct;
            }
        }

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
            }

            foreach (Type type in types)
            {
                MethodInfo? candidate = FindSpillMethodOnType(type);
                if (candidate != null)
                {
                    if (DebugLoggingEnabled)
                    {
                        Api?.Logger.Notification(
                            "{0} [compat/waterfall] Fallback target resolution succeeded: {1}.{2}",
                            ArchimedesScrewModSystem.LogPrefix,
                            type.FullName ?? "<unknown>",
                            candidate.Name
                        );
                    }
                    return candidate;
                }
            }
        }

        return null;
    }

    private static MethodInfo? FindSpillMethodOnType(Type type)
    {
        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
        {
            if (!string.Equals(method.Name, "SpillContents", StringComparison.Ordinal))
            {
                continue;
            }

            if (IsSpillMethodSignatureMatch(method))
            {
                return method;
            }
        }

        return null;
    }

    private static bool IsSpillMethodSignatureMatch(MethodInfo method)
    {
        ParameterInfo[] p = method.GetParameters();
        if (p.Length != 4 || method.ReturnType != typeof(bool))
        {
            return false;
        }

        if (p[0].ParameterType.Name != "ItemSlot" ||
            p[1].ParameterType.Name != "EntityAgent" ||
            p[2].ParameterType.Name != "BlockSelection")
        {
            return false;
        }

        return p[3].ParameterType.IsByRef && p[3].ParameterType.GetElementType() == typeof(bool);
    }
}
