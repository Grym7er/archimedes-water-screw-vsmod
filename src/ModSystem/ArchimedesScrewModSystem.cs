using System;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace ArchimedesScrew;

public sealed class ArchimedesScrewModSystem : ModSystem
{
    public const string LogPrefix = "[archimedes_screw]";
    public const string ModId = "archimedes_screw";
    public const string ScrewBlockCode = "water-archimedesscrew";

    /// <summary>
    /// Config asset patched at load time by Config Lib (from ModConfig YAML) when Config Lib is installed.
    /// </summary>
    public const string ConfigAssetPath = "config/settings.json";

    /// <summary>Fired by Config Lib after writing <c>ModConfig/{ModId}.yaml</c> (see vsmod_configlib).</summary>
    public static string ConfigLibSavedEventName => $"configlib:{ModId}:config-saved";
    public static string ConfigLibSettingChangedEventName => $"configlib:{ModId}:setting-changed";
    public static string ConfigLibReloadEventName => "configlib:config-reload";

    private ICoreAPI? api;
    private ICoreServerAPI? sapi;
    private EventBusListenerDelegate? configLibConfigSavedHandler;
    private EventBusListenerDelegate? configLibSettingChangedHandler;

    public ArchimedesScrewConfig Config { get; private set; } = new();

    public ArchimedesWaterNetworkManager? WaterManager { get; private set; }

    /// <summary>
    /// Runs after Config Lib (0.01) so Start/early hooks see consistent ordering when relevant.
    /// </summary>
    public override double ExecuteOrder() => 0.2;

    public override void Start(ICoreAPI api)
    {
        this.api = api;

        api.RegisterBlockClass(nameof(BlockWaterArchimedesScrew), typeof(BlockWaterArchimedesScrew));
        api.RegisterBlockClass(nameof(BlockArchimedesWaterStill), typeof(BlockArchimedesWaterStill));
        api.RegisterBlockClass(nameof(BlockArchimedesWaterFlowing), typeof(BlockArchimedesWaterFlowing));
        api.RegisterBlockClass(nameof(BlockArchimedesWaterfall), typeof(BlockArchimedesWaterfall));
        api.RegisterBlockEntityClass(nameof(BlockEntityWaterArchimedesScrew), typeof(BlockEntityWaterArchimedesScrew));
    }

    public override void AssetsFinalize(ICoreAPI api)
    {
        var asset = api.Assets.TryGet(new AssetLocation(ModId, ConfigAssetPath));
        if (asset == null)
        {
            api.Logger.Warning("{0} Missing config asset {1}, using defaults.", LogPrefix, ConfigAssetPath);
            Config = new ArchimedesScrewConfig();
            return;
        }

        try
        {
            Config = JsonConvert.DeserializeObject<ArchimedesScrewConfig>(asset.ToText()) ?? new ArchimedesScrewConfig();
            LogEffectiveConfig(api, Config);
            api.Logger.Notification(
                "{0} Loaded {1} (with Config Lib: edit ModConfig/{2}.yaml or in-game; without Config Lib: edit mod asset defaults only)",
                LogPrefix,
                ConfigAssetPath,
                ModId
            );
        }
        catch (JsonException ex)
        {
            api.Logger.Error("{0} Failed to parse config asset {1}: {2}", LogPrefix, ConfigAssetPath, ex);
            Config = new ArchimedesScrewConfig();
        }
    }

    private static void LogEffectiveConfig(ICoreAPI api, ArchimedesScrewConfig config)
    {
        ArchimedesScrewConfig.WaterConfig w = config.Water;
        api.Logger.Notification(
            "{0} Effective config: fastTickMs={1}, idleTickMs={2}, globalTickMs={3}, maxControllersPerGlobalTick={4}, assemblyAnalysisCacheMs={5}, maxBlocksPerStep={6}, maxScrewLength={7}, minNetworkSpeed={8}, maxVanillaConversionPasses={9}",
            LogPrefix,
            w.FastTickMs,
            w.IdleTickMs,
            w.GlobalTickMs,
            w.MaxControllersPerGlobalTick,
            w.AssemblyAnalysisCacheMs,
            w.MaxBlocksPerStep,
            w.MaxScrewLength,
            w.MinimumNetworkSpeed,
            w.MaxVanillaConversionPasses
        );
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;

        WaterManager = new ArchimedesWaterNetworkManager(api, Config);
        WaterManager.StartCentralWaterTick();
        api.Logger.Notification("{0} Server side initialized (central water tick)", LogPrefix);

        api.Event.SaveGameLoaded += OnSaveGameLoaded;
        api.Event.GameWorldSave += OnGameWorldSave;

        // vsmod_configlib pushes this after writing YAML; game API may not expose UnregisterEventBusListener on all builds.
        configLibConfigSavedHandler = OnConfigLibConfigSaved;
        api.Event.RegisterEventBusListener(configLibConfigSavedHandler, filterByEventName: ConfigLibSavedEventName);
        configLibSettingChangedHandler = OnConfigLibSettingChanged;
        api.Event.RegisterEventBusListener(configLibSettingChangedHandler, filterByEventName: ConfigLibSettingChangedEventName);
        api.Event.RegisterEventBusListener(OnConfigLibReloadRequested, filterByEventName: ConfigLibReloadEventName);

        RegisterCommands(api);
    }

    public override void Dispose()
    {
        if (sapi != null)
        {
            sapi.Event.SaveGameLoaded -= OnSaveGameLoaded;
            sapi.Event.GameWorldSave -= OnGameWorldSave;
        }

        WaterManager?.Dispose();
        WaterManager = null;
        base.Dispose();
    }

    private void OnSaveGameLoaded()
    {
        sapi?.Logger.Notification("{0} Save game loaded, restoring water manager state", LogPrefix);
        WaterManager?.Load();
        WaterManager?.BeginPostLoadReactivation();
    }

    private void OnGameWorldSave()
    {
        sapi?.Logger.Notification("{0} Saving water manager state", LogPrefix);
        WaterManager?.Save();
    }

    private void RegisterCommands(ICoreServerAPI api)
    {
        api.ChatCommands
            .Create("archscrew")
            .WithDescription("Administrative commands for the Archimedes Screw mod.")
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("purge")
                .WithDescription("Remove all Archimedes screw blocks and managed water.")
                .HandleWith(_ =>
                {
                    int removed = WaterManager?.PurgeAll() ?? 0;
                    api.Logger.Notification("{0} Command purge removed {1} mod blocks", LogPrefix, removed);
                    return TextCommandResult.Success($"Removed {removed} mod blocks.");
                })
            .EndSubCommand()
            .BeginSubCommand("purgewater")
                .WithDescription("Remove all managed Archimedes water blocks.")
                .HandleWith(_ =>
                {
                    int removed = WaterManager?.PurgeManagedWater() ?? 0;
                    api.Logger.Notification("{0} Command purgewater removed {1} managed water blocks", LogPrefix, removed);
                    return TextCommandResult.Success($"Removed {removed} managed water blocks.");
                })
            .EndSubCommand()
            .BeginSubCommand("purgescrews")
                .WithDescription("Remove all Archimedes screw blocks.")
                .HandleWith(_ =>
                {
                    int removed = WaterManager?.PurgeScrewsOnly() ?? 0;
                    api.Logger.Notification("{0} Command purgescrews removed {1} screw blocks", LogPrefix, removed);
                    return TextCommandResult.Success($"Removed {removed} screw blocks.");
                })
            .EndSubCommand();
    }

    private void OnConfigLibConfigSaved(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (sapi == null)
        {
            return;
        }

        ICoreServerAPI serverApi = sapi;
        serverApi.Event.RegisterCallback(
            _ =>
            {
                try
                {
                    ApplyLiveReloadFromPatchedConfigAsset(serverApi);
                }
                catch (Exception ex)
                {
                    serverApi.Logger.Error("{0} Live config reload failed: {1}", LogPrefix, ex);
                }
            },
            50);
    }

    private void OnConfigLibReloadRequested(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (sapi == null)
        {
            return;
        }

        // Config lib emits a global reload event; re-read our patched asset.
        sapi.Event.RegisterCallback(_ => ApplyLiveReloadFromPatchedConfigAsset(sapi), 50);
    }

    private void OnConfigLibSettingChanged(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (sapi == null || data is not TreeAttribute tree)
        {
            return;
        }

        string settingCode = tree.GetAsString("setting");
        bool changed = false;

        switch (settingCode)
        {
            case "FAST_TICK_MS":
                Config.Water.FastTickMs = tree.GetInt("value");
                changed = true;
                break;
            case "IDLE_TICK_MS":
                Config.Water.IdleTickMs = tree.GetInt("value");
                changed = true;
                break;
            case "GLOBAL_TICK_MS":
                Config.Water.GlobalTickMs = tree.GetInt("value");
                changed = true;
                break;
            case "MAX_CONTROLLERS_PER_GLOBAL_TICK":
                Config.Water.MaxControllersPerGlobalTick = tree.GetInt("value");
                changed = true;
                break;
            case "ASSEMBLY_ANALYSIS_CACHE_MS":
                Config.Water.AssemblyAnalysisCacheMs = tree.GetInt("value");
                changed = true;
                break;
            case "MAX_BLOCKS_PER_STEP":
                Config.Water.MaxBlocksPerStep = tree.GetInt("value");
                changed = true;
                break;
            case "MAX_SCREW_LENGTH":
                Config.Water.MaxScrewLength = tree.GetInt("value");
                changed = true;
                break;
            case "MAX_VANILLA_CONVERSION_PASSES":
                Config.Water.MaxVanillaConversionPasses = tree.GetInt("value");
                changed = true;
                break;
            case "MINIMUM_NETWORK_SPEED":
                Config.Water.MinimumNetworkSpeed = tree.GetFloat("value");
                changed = true;
                break;
        }

        if (!changed)
        {
            return;
        }

        if (settingCode is "GLOBAL_TICK_MS" or "MAX_CONTROLLERS_PER_GLOBAL_TICK")
        {
            WaterManager?.RestartCentralWaterTickForCurrentConfig();
        }

        sapi.Logger.Notification("{0} Live setting applied from Config Lib: {1}", LogPrefix, settingCode);
        LogEffectiveConfig(sapi, Config);
    }

    private void ApplyLiveReloadFromPatchedConfigAsset(ICoreServerAPI api)
    {
        IAsset? asset = api.Assets.TryGet(new AssetLocation(ModId, ConfigAssetPath));
        if (asset == null)
        {
            api.Logger.Warning("{0} Live config reload skipped: missing asset {1}", LogPrefix, ConfigAssetPath);
            return;
        }

        ArchimedesScrewConfig? fresh;
        try
        {
            fresh = JsonConvert.DeserializeObject<ArchimedesScrewConfig>(asset.ToText());
        }
        catch (JsonException ex)
        {
            api.Logger.Error("{0} Live config reload: bad JSON in {1}: {2}", LogPrefix, ConfigAssetPath, ex);
            return;
        }

        if (fresh == null)
        {
            return;
        }

        Config.Water.CopyValuesFrom(fresh.Water);
        WaterManager?.RestartCentralWaterTickForCurrentConfig();
        LogEffectiveConfig(api, Config);
        api.Logger.Notification("{0} Applied live config reload (Config Lib)", LogPrefix);
    }
}
