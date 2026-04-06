using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent.Mechanics;

namespace ArchimedesScrew;

public sealed class BlockEntityWaterArchimedesScrew : BlockEntity
{
    private const string OwnedPositionsKey = "ownedPositions";
    private const string ControllerIdKey = "controllerId";
    private const string LastSeedKey = "lastSeed";
    private const string WasControllerKey = "wasController";

    private readonly Dictionary<string, BlockPos> ownedPositions = new(StringComparer.Ordinal);

    private ArchimedesWaterNetworkManager? waterManager;
    private ArchimedesScrewConfig.WaterConfig? waterConfig;

    private long tickListenerId;
    private int currentIntervalMs;
    private bool wasController;
    private BlockPos? lastSeedPos;
    private bool? lastLoggedControllerState;
    private bool? lastLoggedPowerState;
    private string? lastLoggedSeedKey;
    private string? lastLoggedSourceSummary;

    public string ControllerId { get; private set; } = Guid.NewGuid().ToString("N");

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);

        if (api.Side != EnumAppSide.Server)
        {
            return;
        }

        ArchimedesScrewModSystem modSystem = api.ModLoader.GetModSystem<ArchimedesScrewModSystem>();
        waterManager = modSystem.WaterManager;
        waterConfig = modSystem.Config.Water;

        waterManager?.RegisterScrewBlock(Pos);
        waterManager?.RegisterLoadedController(this);
        Log("Initialized controller {0} for block {1} at {2}", ControllerId, Block?.Code, Pos);

        if (ownedPositions.Count > 0)
        {
            waterManager?.RegisterRestoredOwnership(ControllerId, Pos, ownedPositions.Values.ToList());
            Log("Restored {0} owned Archimedes source positions from save", ownedPositions.Count);
        }

        EnsureTickListener(waterConfig?.FastTickMs ?? 250);
    }

    public override void OnBlockPlaced(ItemStack? byItemStack = null)
    {
        base.OnBlockPlaced(byItemStack);
        waterManager?.RegisterScrewBlock(Pos);
        waterManager?.RegisterLoadedController(this);
        Log("Block placed at {0}: {1}", Pos, Block?.Code);
    }

    public override void OnBlockRemoved()
    {
        Log("Block removed at {0}: {1}", Pos, Block?.Code);
        if (tickListenerId != 0)
        {
            UnregisterGameTickListener(tickListenerId);
            tickListenerId = 0;
        }
        ReleaseAllManagedWater("block removed");
        waterManager?.UnregisterScrewBlock(Pos);
        waterManager?.RemoveControllerSnapshot(ControllerId);
        base.OnBlockRemoved();
    }

    public override void OnBlockUnloaded()
    {
        Log("Block unloaded at {0}", Pos);
        if (tickListenerId != 0)
        {
            UnregisterGameTickListener(tickListenerId);
            tickListenerId = 0;
        }
        waterManager?.UnregisterLoadedController(ControllerId);
        base.OnBlockUnloaded();
    }

    public void NotifyManagedWaterRemoved(BlockPos pos)
    {
        ownedPositions.Remove(ArchimedesWaterNetworkManager.PosKey(pos));
        MarkDirty();
        Log("Tracked Archimedes source removed externally at {0}; ownership updated", pos);
    }

    public bool TryAdoptSource(BlockPos pos, string reason)
    {
        if (waterManager == null)
        {
            return false;
        }

        string key = ArchimedesWaterNetworkManager.PosKey(pos);
        bool ownerAdded = waterManager.EnsureSourceOwnership(ControllerId, pos);
        bool wasTracked = ownedPositions.ContainsKey(key);
        ownedPositions[key] = pos.Copy();

        if (!ownerAdded && wasTracked)
        {
            return false;
        }

        UpdateSnapshot();
        Log("Owned source at {0} ({1})", pos, reason);
        return true;
    }

    public void ClearOwnedStateAfterPurge()
    {
        ownedPositions.Clear();
        wasController = false;
        lastSeedPos = null;
        lastLoggedSeedKey = null;
        lastLoggedSourceSummary = null;
        MarkDirty();
        Log("Cleared owned source state after purge");
    }

    public void ReleaseAllManagedWater(string reason = "unspecified")
    {
        if (waterManager == null)
        {
            ownedPositions.Clear();
            Log("Release requested for reason '{0}', but water manager is null", reason);
            return;
        }

        AdoptAllConnectedSources();

        if (ownedPositions.Count == 0)
        {
            waterManager.UpdateControllerSnapshot(ControllerId, Pos, Array.Empty<BlockPos>());
            Log("Release requested for reason '{0}', but no Archimedes sources were owned", reason);
            return;
        }

        int count = ownedPositions.Count;
        foreach (BlockPos ownedPos in ownedPositions.Values.ToArray())
        {
            waterManager.ReleaseSourceOwner(ControllerId, ownedPos);
        }

        ownedPositions.Clear();
        waterManager.UpdateControllerSnapshot(ControllerId, Pos, Array.Empty<BlockPos>());
        MarkDirty();
        Log("Released {0} Archimedes source blocks because {1}", count, reason);
    }

    public bool TryGetActiveSeedState(out ArchimedesOutletState state)
    {
        state = default;
        ControllerEvaluation evaluation = EvaluateController();
        if (!evaluation.IsController ||
            !evaluation.IsPowered ||
            evaluation.FamilyId == null ||
            evaluation.SeedPos == null)
        {
            return false;
        }

        state = new ArchimedesOutletState(ControllerId, evaluation.SeedPos.Copy(), evaluation.FamilyId);
        return true;
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);

        ControllerId = tree.GetString(ControllerIdKey, ControllerId);
        wasController = tree.GetBool(WasControllerKey, false);

        byte[]? ownedBytes = tree.GetBytes(OwnedPositionsKey);
        ownedPositions.Clear();
        if (ownedBytes != null)
        {
            int[] flatPositions = SerializerUtil.Deserialize<int[]>(ownedBytes);
            for (int i = 0; i + 2 < flatPositions.Length; i += 3)
            {
                BlockPos pos = new(flatPositions[i], flatPositions[i + 1], flatPositions[i + 2]);
                ownedPositions[ArchimedesWaterNetworkManager.PosKey(pos)] = pos;
            }
        }

        byte[]? seedBytes = tree.GetBytes(LastSeedKey);
        lastSeedPos = seedBytes == null ? null : DecodeSinglePos(SerializerUtil.Deserialize<int[]>(seedBytes));
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);

        tree.SetString(ControllerIdKey, ControllerId);
        tree.SetBool(WasControllerKey, wasController);
        tree.SetBytes(OwnedPositionsKey, SerializerUtil.Serialize(EncodePositions(ownedPositions.Values)));

        if (lastSeedPos == null)
        {
            tree.RemoveAttribute(LastSeedKey);
        }
        else
        {
            tree.SetBytes(LastSeedKey, SerializerUtil.Serialize(new[] { lastSeedPos.X, lastSeedPos.Y, lastSeedPos.Z }));
        }
    }

    private void OnTick(float dt)
    {
        if (Api == null || Api.Side != EnumAppSide.Server || waterManager == null || waterConfig == null)
        {
            return;
        }

        ControllerEvaluation evaluation = EvaluateController();
        LogStateChange("controller validity", ref lastLoggedControllerState, evaluation.IsController);
        LogStateChange("powered state", ref lastLoggedPowerState, evaluation.IsPowered);

        if (!evaluation.IsController)
        {
            wasController = false;
            AdoptAllConnectedSources();
            int removed = DrainUnsupportedSources(Array.Empty<BlockPos>(), Array.Empty<string>(), evaluation.FailureReason);
            EnsureTickListener(ownedPositions.Count > 0 || removed > 0 ? waterConfig.FastTickMs : waterConfig.IdleTickMs);
            return;
        }

        wasController = true;

        if (!evaluation.IsPowered || evaluation.FamilyId == null || evaluation.SeedPos == null)
        {
            AdoptAllConnectedSources();
            int removed = DrainUnsupportedSources(Array.Empty<BlockPos>(), Array.Empty<string>(), evaluation.FailureReason);
            EnsureTickListener(ownedPositions.Count > 0 || removed > 0 ? waterConfig.FastTickMs : waterConfig.IdleTickMs);
            return;
        }

        BlockPos seedPos = evaluation.SeedPos.Copy();
        string familyId = evaluation.FamilyId;
        string seedKey = ArchimedesWaterNetworkManager.PosKey(seedPos);
        if (lastLoggedSeedKey != seedKey)
        {
            lastLoggedSeedKey = seedKey;
            Log("Seed/output position is now {0}", seedPos);
        }

        lastSeedPos = seedPos.Copy();

        bool ensuredSeed = EnsureSeedSource(seedPos, familyId);
        int convertedSources = waterManager.ConvertAdjacentVanillaSourcesIteratively(
            seedPos,
            waterConfig.MaxVanillaConversionPasses
        );

        waterManager.CollectConnectedManagedWater(seedPos, out Dictionary<string, BlockPos> connectedWater);
        int adoptedSources = AdoptSourcesFromWaterMap(connectedWater);

        List<ArchimedesOutletState> supportingSeeds = ResolveSupportingSeeds(seedPos, connectedWater.Keys);
        int removedDisconnected = DrainUnsupportedSources(supportingSeeds.Select(seed => seed.SeedPos), connectedWater.Keys, string.Empty);

        if (adoptedSources > 0 || ensuredSeed)
        {
            UpdateSnapshot();
        }

        bool shouldFastTick = ensuredSeed || convertedSources > 0 || removedDisconnected > 0 || adoptedSources > 0 || ownedPositions.Count == 0;
        EnsureTickListener(shouldFastTick ? waterConfig.FastTickMs : waterConfig.IdleTickMs);

        string sourceSummary =
            $"seed={seedPos};connectedWater={connectedWater.Count};ownedSources={ownedPositions.Count};supportingSeeds={supportingSeeds.Count};convertedSources={convertedSources};adoptedSources={adoptedSources};removedDisconnected={removedDisconnected};nextIntervalMs={(shouldFastTick ? waterConfig.FastTickMs : waterConfig.IdleTickMs)}";
        if (!string.Equals(lastLoggedSourceSummary, sourceSummary, StringComparison.Ordinal))
        {
            lastLoggedSourceSummary = sourceSummary;
            Log(
                "Source tick at {0}: connectedWater={1}, ownedSources={2}, supportingSeeds={3}, convertedSources={4}, adoptedSources={5}, removedDisconnected={6}, nextIntervalMs={7}",
                seedPos,
                connectedWater.Count,
                ownedPositions.Count,
                supportingSeeds.Count,
                convertedSources,
                adoptedSources,
                removedDisconnected,
                shouldFastTick ? waterConfig.FastTickMs : waterConfig.IdleTickMs
            );
        }
    }

    private ControllerEvaluation EvaluateController()
    {
        if (Api == null || waterManager == null || waterConfig == null)
        {
            return new ControllerEvaluation(false, false, "controller not initialized", null, null);
        }

        BlockWaterArchimedesScrew? screwBlock = Block as BlockWaterArchimedesScrew;
        if (screwBlock == null || !screwBlock.IsIntakeBlock())
        {
            return new ControllerEvaluation(false, false, "block is not an intake controller", null, null);
        }

        ArchimedesScrewAssemblyAnalyzer.AssemblyStatus assemblyStatus = ArchimedesScrewAssemblyAnalyzer.Analyze(Api.World, Pos, waterConfig.MinimumNetworkSpeed);
        if (!assemblyStatus.IsAssemblyValid)
        {
            return new ControllerEvaluation(false, false, $"assembly invalid: {assemblyStatus.Message}", null, assemblyStatus.OutputPos?.Copy());
        }

        Block intakeFluid = Api.World.BlockAccessor.GetBlock(Pos, BlockLayersAccess.Fluid);
        if (!waterManager.TryResolveIntakeWaterFamily(intakeFluid, out string familyId))
        {
            return new ControllerEvaluation(false, assemblyStatus.IsPowered, $"unsupported intake fluid: {intakeFluid.Code}", null, assemblyStatus.OutputPos?.Copy());
        }

        BlockPos seedPos = assemblyStatus.OutputPos?.Copy() ?? GetSeedPosition();
        if (!CanUseSeedPosition(seedPos))
        {
            return new ControllerEvaluation(false, assemblyStatus.IsPowered, $"seed/output position {seedPos} is blocked", familyId, seedPos);
        }

        return new ControllerEvaluation(true, assemblyStatus.IsPowered, string.Empty, familyId, seedPos);
    }

    private List<ArchimedesOutletState> ResolveSupportingSeeds(BlockPos seedPos, IEnumerable<string> connectedWaterKeys)
    {
        HashSet<string> connectedKeySet = new(connectedWaterKeys, StringComparer.Ordinal);
        List<ArchimedesOutletState> supporting = new();

        foreach (ArchimedesOutletState activeSeed in waterManager!.GetActiveSeedStates())
        {
            if (activeSeed.ControllerId == ControllerId ||
                activeSeed.SeedPos.Equals(seedPos) ||
                connectedKeySet.Contains(ArchimedesWaterNetworkManager.PosKey(activeSeed.SeedPos)))
            {
                supporting.Add(new ArchimedesOutletState(activeSeed.ControllerId, activeSeed.SeedPos.Copy(), activeSeed.FamilyId));
            }
        }

        if (supporting.Count == 0)
        {
            supporting.Add(new ArchimedesOutletState(ControllerId, seedPos.Copy(), string.Empty));
        }

        return supporting
            .GroupBy(seed => seed.ControllerId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private bool EnsureSeedSource(BlockPos seedPos, string familyId)
    {
        if (waterManager == null)
        {
            return false;
        }

        bool changed = waterManager.EnsureSourceOwned(ControllerId, seedPos, familyId);
        ownedPositions[ArchimedesWaterNetworkManager.PosKey(seedPos)] = seedPos.Copy();
        return changed;
    }

    private int AdoptSourcesFromWaterMap(Dictionary<string, BlockPos> connectedWater)
    {
        if (waterManager == null)
        {
            return 0;
        }

        int adopted = 0;
        foreach ((string key, BlockPos pos) in connectedWater)
        {
            Block fluidBlock = Api!.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
            if (!waterManager.IsArchimedesSourceBlock(fluidBlock))
            {
                continue;
            }

            if (TryAdoptSource(pos, "connected managed water"))
            {
                adopted++;
            }
        }

        return adopted;
    }

    private void AdoptAllConnectedSources()
    {
        if (waterManager == null)
        {
            return;
        }

        HashSet<string> scanStarts = new(StringComparer.Ordinal);
        if (lastSeedPos != null)
        {
            scanStarts.Add(ArchimedesWaterNetworkManager.PosKey(lastSeedPos));
        }

        foreach (BlockPos pos in ownedPositions.Values)
        {
            scanStarts.Add(ArchimedesWaterNetworkManager.PosKey(pos));
        }

        HashSet<string> allVisited = new(StringComparer.Ordinal);
        foreach (string key in scanStarts)
        {
            if (allVisited.Contains(key))
            {
                continue;
            }

            BlockPos startPos = ParsePosKey(key);
            waterManager.CollectConnectedManagedWater(startPos, out Dictionary<string, BlockPos> connectedWater);

            foreach (string visitedKey in connectedWater.Keys)
            {
                allVisited.Add(visitedKey);
            }

            foreach ((string sourceKey, BlockPos sourcePos) in connectedWater)
            {
                Block fluidBlock = Api!.World.BlockAccessor.GetBlock(sourcePos, BlockLayersAccess.Fluid);
                if (!waterManager.IsArchimedesSourceBlock(fluidBlock))
                {
                    continue;
                }

                TryAdoptSource(sourcePos, "controller deactivation scan");
            }
        }
    }

    private int DrainUnsupportedSources(IEnumerable<BlockPos> referenceSeeds, IEnumerable<string> supportedKeys, string reason)
    {
        if (waterManager == null || waterConfig == null)
        {
            return 0;
        }

        HashSet<string> supportedKeySet = new(supportedKeys, StringComparer.Ordinal);
        List<BlockPos> toRelease = ownedPositions.Values
            .Where(pos => !supportedKeySet.Contains(ArchimedesWaterNetworkManager.PosKey(pos)))
            .ToList();

        if (toRelease.Count == 0)
        {
            return 0;
        }

        List<BlockPos> origins = referenceSeeds.Select(pos => pos.Copy()).ToList();
        if (origins.Count == 0)
        {
            origins.Add(lastSeedPos?.Copy() ?? Pos.Copy());
        }

        int perTick = Math.Max(1, waterConfig.MaxBlocksPerStep);
        List<BlockPos> releaseStep = toRelease
            .OrderByDescending(pos => MinDistanceSquared(pos, origins))
            .ThenByDescending(pos => pos.Y)
            .Take(perTick)
            .ToList();

        foreach (BlockPos pos in releaseStep)
        {
            ownedPositions.Remove(ArchimedesWaterNetworkManager.PosKey(pos));
            waterManager.ReleaseSourceOwner(ControllerId, pos);
        }

        UpdateSnapshot();
        Log(
            "Drain tick toward {0}: removedSources={1}, remainingSources={2}, reason={3}",
            origins[0],
            releaseStep.Count,
            ownedPositions.Count,
            reason
        );

        return releaseStep.Count;
    }

    private void UpdateSnapshot()
    {
        waterManager?.UpdateControllerSnapshot(ControllerId, Pos, ownedPositions.Values.ToList());
        MarkDirty();
    }

    private bool CanUseSeedPosition(BlockPos seedPos)
    {
        if (Api == null)
        {
            return false;
        }

        Block solidBlock = Api.World.BlockAccessor.GetBlock(seedPos);
        Block fluidBlock = Api.World.BlockAccessor.GetBlock(seedPos, BlockLayersAccess.Fluid);

        bool solidClear = solidBlock.Id == 0 || solidBlock.ForFluidsLayer || waterManager?.IsArchimedesWaterBlock(solidBlock) == true;
        bool fluidClear = fluidBlock.Id == 0 ||
                          waterManager?.IsArchimedesWaterBlock(fluidBlock) == true ||
                          (fluidBlock.IsLiquid() && ArchimedesWaterFamilies.TryResolveVanillaFamily(fluidBlock, out _));

        return solidClear && fluidClear;
    }

    private BlockPos GetSeedPosition()
    {
        BlockPos topPos = FindTopScrewPos();
        if (Api?.World.BlockAccessor.GetBlock(topPos) is BlockWaterArchimedesScrew topScrew && topScrew.IsOutletBlock())
        {
            BlockFacing? facing = topScrew.GetPortFacing();
            if (facing != null)
            {
                return topPos.AddCopy(facing);
            }
        }

        return topPos.UpCopy();
    }

    private BlockPos FindTopScrewPos()
    {
        BlockPos top = Pos.Copy();
        int maxLength = waterConfig?.MaxScrewLength ?? 32;

        for (int i = 0; i < maxLength; i++)
        {
            BlockPos above = top.UpCopy();
            if (Api?.World.BlockAccessor.GetBlock(above) is not BlockWaterArchimedesScrew)
            {
                break;
            }

            top = above;
        }

        return top;
    }

    private void EnsureTickListener(int intervalMs)
    {
        if (Api == null)
        {
            return;
        }

        if (tickListenerId != 0 && currentIntervalMs == intervalMs)
        {
            return;
        }

        if (tickListenerId != 0)
        {
            UnregisterGameTickListener(tickListenerId);
        }

        currentIntervalMs = intervalMs;
        tickListenerId = RegisterGameTickListener(OnTick, intervalMs);
        Log("Registered tick listener at {0} ms", intervalMs);
    }

    private void Log(string message, params object?[] args)
    {
        Api?.Logger.Notification($"{ArchimedesScrewModSystem.LogPrefix} [controller:{ControllerId}] {message}", args);
    }

    private void LogStateChange(string name, ref bool? lastValue, bool value)
    {
        if (lastValue == value)
        {
            return;
        }

        lastValue = value;
        Log("{0} changed to {1}", name, value);
    }

    private static int MinDistanceSquared(BlockPos pos, IEnumerable<BlockPos> origins)
    {
        return origins.Min(origin => DistanceSquared(pos, origin));
    }

    private static int DistanceSquared(BlockPos a, BlockPos b)
    {
        int dx = a.X - b.X;
        int dy = a.Y - b.Y;
        int dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static int[] EncodePositions(IEnumerable<BlockPos> positions)
    {
        List<int> flat = new();
        foreach (BlockPos pos in positions)
        {
            flat.Add(pos.X);
            flat.Add(pos.Y);
            flat.Add(pos.Z);
        }

        return flat.ToArray();
    }

    private static BlockPos? DecodeSinglePos(int[]? values)
    {
        if (values == null || values.Length < 3)
        {
            return null;
        }

        return new BlockPos(values[0], values[1], values[2]);
    }

    private static BlockPos ParsePosKey(string key)
    {
        string[] parts = key.Split(',');
        if (parts.Length < 3 ||
            !int.TryParse(parts[0], out int x) ||
            !int.TryParse(parts[1], out int y) ||
            !int.TryParse(parts[2], out int z))
        {
            throw new FormatException($"Invalid position key format: '{key}'");
        }

        return new BlockPos(x, y, z);
    }

    private readonly record struct ControllerEvaluation(
        bool IsController,
        bool IsPowered,
        string FailureReason,
        string? FamilyId,
        BlockPos? SeedPos
    );
}
