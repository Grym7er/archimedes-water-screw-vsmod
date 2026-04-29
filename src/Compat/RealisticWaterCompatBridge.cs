using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace ArchimedesScrew;

internal sealed class RealisticWaterCompatBridge : IDisposable
{
    private const string RealisticWaterModId = "realisticwater";
    private const string RealisticWaterFreshPath = "realisticwater-still-6-20";
    private const int OutletSustainTtlMs = 10000;
    private const int ShutdownDrainTtlMs = OutletSustainTtlMs;
    private const int ShutdownDrainCascadeRadius = 8;
    private const int RealisticWaterSustainedLevel = 6;
    private const int RealisticWaterSustainedSublevel = 20;

    private static readonly Dictionary<long, SustainedOutlet> sustainedOutletsByKey = new();
    private static readonly Dictionary<long, ShutdownDrainOrigin> shutdownDrainOriginsByKey = new();

    private readonly ICoreServerAPI api;
    private readonly Harmony harmony;
    private bool isPatched;

    public RealisticWaterCompatBridge(ICoreServerAPI api)
    {
        this.api = api;
        harmony = new Harmony($"{ArchimedesScrewModSystem.ModId}.compat.realisticwater");
        IsActive = api.ModLoader.IsModEnabled(RealisticWaterModId);
        RefreshPatchState();
        ArchimedesScrewModSystem.LogVerboseOrNotification(
            api.Logger,
            IsActive
                ? "{0} [compat/realisticwater] Compat active; relay sources disabled and outlet sustain patch enabled"
                : "{0} [compat/realisticwater] Mod not installed; compat inactive",
            ArchimedesScrewModSystem.LogPrefix);
    }

    public bool IsActive { get; }

    public void Dispose()
    {
        Unpatch();
        sustainedOutletsByKey.Clear();
        shutdownDrainOriginsByKey.Clear();
    }

    public bool TryResolveOutletBlock(string familyId, out Block outletBlock)
    {
        outletBlock = null!;
        if (!IsActive)
        {
            return false;
        }

        if (string.Equals(familyId, ArchimedesWaterFamilies.Fresh.Id, StringComparison.Ordinal) &&
            (TryGetBlock(new AssetLocation(RealisticWaterModId, RealisticWaterFreshPath), out outletBlock) ||
             TryGetBlock(new AssetLocation("game", RealisticWaterFreshPath), out outletBlock)))
        {
            return true;
        }

        ArchimedesWaterFamily family = ArchimedesWaterFamilies.GetById(familyId);
        return TryGetBlock(new AssetLocation("game", $"{family.VanillaCode}-still-6"), out outletBlock);
    }

    public bool TryResolveIntakeFamily(Block block, out string familyId)
    {
        if (IsActive &&
            block.IsLiquid() &&
            block.Code?.Path.StartsWith("realisticwater-", StringComparison.Ordinal) == true)
        {
            familyId = ArchimedesWaterFamilies.Fresh.Id;
            return true;
        }

        familyId = string.Empty;
        return false;
    }

    public void RefreshSustainedOutlet(BlockPos pos, string familyId, Block outletBlock)
    {
        if (!IsActive)
        {
            return;
        }

        long key = ArchimedesPosKey.Pack(pos);
        sustainedOutletsByKey[key] = new SustainedOutlet(outletBlock.Id, familyId, Environment.TickCount64 + OutletSustainTtlMs);
        ArchimedesPerf.AddCount("compat.realisticwater.outletSustain.refresh");
    }

    public void UnregisterSustainedOutlet(BlockPos? pos)
    {
        if (pos == null)
        {
            return;
        }

        long key = ArchimedesPosKey.Pack(pos);
        bool removed = sustainedOutletsByKey.Remove(key);
        if (removed)
        {
            ArchimedesPerf.AddCount("compat.realisticwater.outletSustain.unregister");
            Block currentFluid = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
            if (currentFluid.Code?.Path.StartsWith("realisticwater-", StringComparison.Ordinal) == true)
            {
                RegisterShutdownDrainOrigin(pos);
                currentFluid.OnNeighbourBlockChange(api.World, pos, pos);
                api.World.BlockAccessor.MarkBlockDirty(pos);
            }
        }
    }

    internal static bool IsRecentShutdownDrainCascadeCell(BlockPos pos)
    {
        long nowMs = Environment.TickCount64;
        PruneExpiredShutdownDrainOrigins(nowMs);

        foreach (ShutdownDrainOrigin origin in shutdownDrainOriginsByKey.Values)
        {
            if (origin.Dimension == pos.dimension &&
                Math.Abs(origin.X - pos.X) + Math.Abs(origin.Y - pos.Y) + Math.Abs(origin.Z - pos.Z) <= ShutdownDrainCascadeRadius)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool ShouldSustainOutlet(Block block, IWorldAccessor world, BlockPos pos)
    {
        long key = ArchimedesPosKey.Pack(pos);
        if (!sustainedOutletsByKey.TryGetValue(key, out SustainedOutlet outlet))
        {
            return false;
        }

        long nowMs = Environment.TickCount64;
        if (outlet.ExpiresAtMs <= nowMs)
        {
            sustainedOutletsByKey.Remove(key);
            ArchimedesPerf.AddCount("compat.realisticwater.outletSustain.expired");
            return false;
        }

        if (!IsExpectedSustainedOutletBlock(block, outlet))
        {
            return false;
        }

        Block currentFluid = world.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        return IsExpectedSustainedOutletBlock(currentFluid, outlet);
    }

    private static void RegisterShutdownDrainOrigin(BlockPos pos)
    {
        long nowMs = Environment.TickCount64;
        PruneExpiredShutdownDrainOrigins(nowMs);
        shutdownDrainOriginsByKey[ArchimedesPosKey.Pack(pos)] = new ShutdownDrainOrigin(
            pos.X,
            pos.Y,
            pos.Z,
            pos.dimension,
            nowMs + ShutdownDrainTtlMs);
        ArchimedesPerf.AddCount("compat.realisticwater.shutdownDrain.register");
    }

    private static void PruneExpiredShutdownDrainOrigins(long nowMs)
    {
        foreach (long key in shutdownDrainOriginsByKey.Keys.ToArray())
        {
            if (shutdownDrainOriginsByKey[key].ExpiresAtMs <= nowMs)
            {
                shutdownDrainOriginsByKey.Remove(key);
            }
        }
    }

    private static bool IsExpectedSustainedOutletBlock(Block block, SustainedOutlet outlet)
    {
        if (block.LiquidLevel >= 7)
        {
            return false;
        }

        if (string.Equals(outlet.FamilyId, ArchimedesWaterFamilies.Fresh.Id, StringComparison.Ordinal))
        {
            return block.Code?.Path.StartsWith("realisticwater-", StringComparison.Ordinal) == true &&
                   block.LiquidLevel == RealisticWaterSustainedLevel &&
                   string.Equals(block.Variant?["height"], RealisticWaterSustainedLevel.ToString(), StringComparison.Ordinal) &&
                   string.Equals(block.Variant?["sublevel"], RealisticWaterSustainedSublevel.ToString(), StringComparison.Ordinal);
        }

        ArchimedesWaterFamily family = ArchimedesWaterFamilies.GetById(outlet.FamilyId);
        return string.Equals(block.LiquidCode, family.VanillaCode, StringComparison.Ordinal) &&
               block.LiquidLevel == RealisticWaterSustainedLevel;
    }

    public bool IsCompatibleOutletBlock(Block block, string familyId)
    {
        if (!IsActive)
        {
            return false;
        }

        return IsExpectedSustainedOutletBlock(
            block,
            new SustainedOutlet(block.Id, familyId, Environment.TickCount64 + OutletSustainTtlMs));
    }

    private void RefreshPatchState()
    {
        RealisticWaterOutletSustainPatch.Api = api;
        if (!IsActive)
        {
            Unpatch();
            return;
        }

        if (isPatched)
        {
            return;
        }

        harmony.CreateClassProcessor(typeof(RealisticWaterOutletSustainPatch)).Patch();
        if (!RealisticWaterOutletSustainPatch.LastPrepareSucceeded)
        {
            ArchimedesScrewModSystem.LogVerboseOrNotification(
                api.Logger,
                "{0} [compat/realisticwater] Harmony skipped outlet sustain patch (target method not resolved)",
                ArchimedesScrewModSystem.LogPrefix);
            return;
        }

        isPatched = true;
        ArchimedesScrewModSystem.LogVerboseOrNotification(api.Logger, "{0} [compat/realisticwater] Outlet sustain patch active", ArchimedesScrewModSystem.LogPrefix);
    }

    private void Unpatch()
    {
        if (!isPatched)
        {
            return;
        }

        harmony.UnpatchAll(harmony.Id);
        isPatched = false;
        ArchimedesScrewModSystem.LogVerboseOrNotification(api.Logger, "{0} [compat/realisticwater] Outlet sustain patch unpatched", ArchimedesScrewModSystem.LogPrefix);
    }

    private bool TryGetBlock(AssetLocation code, out Block block)
    {
        Block? resolved = api.World.GetBlock(code);
        if (resolved == null)
        {
            block = null!;
            return false;
        }

        block = resolved;
        return true;
    }

    private readonly record struct SustainedOutlet(int BlockId, string FamilyId, long ExpiresAtMs);

    private readonly record struct ShutdownDrainOrigin(int X, int Y, int Z, int Dimension, long ExpiresAtMs);
}
