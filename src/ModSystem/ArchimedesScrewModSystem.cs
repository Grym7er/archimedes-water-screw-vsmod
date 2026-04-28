using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
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
    private ICoreClientAPI? capi;
    private ICoreServerAPI? sapi;
    private IServerNetworkChannel? serverChannel;
    private IClientNetworkChannel? clientChannel;
    private EventBusListenerDelegate? configLibConfigSavedHandler;
    private EventBusListenerDelegate? configLibSettingChangedHandler;
    private ArchimedesScrewConfig.WaterConfig? pendingWaterConfig;
    private bool pendingRequiresCentralTickRestart;
    private WaterfallCompatBridge? waterfallCompatBridge;
    private WaterSourceRegenCompatBridge? waterSourceRegenCompatBridge;
    private ArchimedesWaterDebugOverlay? waterDebugOverlay;
    private long waterDebugTickListenerId;
    private bool waterDebugEnabled;

    private const string NetworkChannelName = ModId;
    private const int WaterDebugRadius = 32;
    public static bool VerboseDebugEnabled { get; private set; }

    public ArchimedesScrewConfig Config { get; private set; } = new();

    public ArchimedesWaterNetworkManager? WaterManager { get; private set; }

    /// <summary>Client: whether the last water debug snapshot had <c>Enabled=true</c>.</summary>
    public bool IsWaterDebugOverlayEnabled => waterDebugOverlay?.IsOverlayEnabled ?? false;

    /// <summary>Client-only: appendix text for Archimedes fluid tooltips when water debug overlay is on.</summary>
    public string? TryBuildWaterDebugTooltipAppendix(BlockPos pos, Block fluidBlock)
    {
        return waterDebugOverlay?.BuildWaterDebugTooltipAppendix(pos, fluidBlock);
    }

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
            VerboseDebugEnabled = Config.Water.VerboseDebug;
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
            "{0} Effective config: fastTickMs={1}, idleTickMs={2}, globalTickMs={3}, maxControllersPerGlobalTick={4}, assemblyAnalysisCacheMs={5}, maxBlocksPerStep={6}, enableIncrementalSourceDrain={7}, incrementalDrainStepIntervalMs={8}, maxIncrementalDrainStepsPerGlobalTick={9}, maxScrewLength={10}, minNetworkSpeed={11}, vanillaClaimHaloDepth={12}, intentQueueMaxPerGlobalTick={13}, enableRelaySources={14}, maxRelayPromotionsPerTick={15}, maxRelaySourcesPerController={16}, requiredMechPowerForMaxRelay={17}, relayPowerHysteresisPct={18}, relayCandidateOrderingMode={19}, debugControllerStatsOnInteract={20}, enableWaterfallCompat={21}, waterfallCompatDebug={22}, verboseDebug={23}",
            LogPrefix,
            w.FastTickMs,
            w.IdleTickMs,
            w.GlobalTickMs,
            w.MaxControllersPerGlobalTick,
            w.AssemblyAnalysisCacheMs,
            w.MaxBlocksPerStep,
            w.EnableIncrementalSourceDrain,
            w.IncrementalDrainStepIntervalMs,
            w.MaxIncrementalDrainStepsPerGlobalTick,
            w.MaxScrewLength,
            w.MinimumNetworkSpeed,
            w.VanillaClaimHaloDepth,
            w.IntentQueueMaxPerGlobalTick,
            w.EnableRelaySources,
            w.MaxRelayPromotionsPerTick,
            w.MaxRelaySourcesPerController,
            w.RequiredMechPowerForMaxRelay,
            w.RelayPowerHysteresisPct,
            w.RelayCandidateOrderingMode,
            w.DebugControllerStatsOnInteract,
            w.EnableWaterfallCompat,
            w.WaterfallCompatDebug,
            w.VerboseDebug
        );
    }

    public static void LogVerbose(ILogger? logger, string message, params object?[] args)
    {
        if (logger == null || !VerboseDebugEnabled)
        {
            return;
        }

        logger.Event("[VerboseDebug] " + message, args);
    }

    public static void LogVerboseOrNotification(ILogger? logger, string message, params object?[] args)
    {
        if (logger == null)
        {
            return;
        }

        if (VerboseDebugEnabled)
        {
            logger.Event("[VerboseDebug] " + message, args);
            return;
        }

        logger.Notification(message, args);
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        serverChannel = api.Network
            .RegisterChannel(NetworkChannelName)
            .RegisterMessageType<ArchimedesWaterDebugSnapshotPacket>()
            .RegisterMessageType<WaterDebugTooltipQueryPacket>()
            .RegisterMessageType<WaterDebugTooltipResponsePacket>();
        serverChannel.SetMessageHandler<WaterDebugTooltipQueryPacket>(OnWaterDebugTooltipQuery);

        WaterManager = new ArchimedesWaterNetworkManager(api, Config);
        WaterManager.StartCentralWaterTick();
        waterSourceRegenCompatBridge = new WaterSourceRegenCompatBridge(api);
        waterSourceRegenCompatBridge.EnsurePatched();
        waterfallCompatBridge = new WaterfallCompatBridge(api);
        waterfallCompatBridge.RefreshForConfig(Config.Water);
        api.Logger.Notification("{0} Server side initialized (central water tick)", LogPrefix);

        api.Event.SaveGameLoaded += OnSaveGameLoaded;
        api.Event.GameWorldSave += OnGameWorldSave;

        // vsmod_configlib pushes this after writing YAML; game API may not expose UnregisterEventBusListener on all builds.
        configLibConfigSavedHandler = OnConfigLibConfigSaved;
        api.Event.RegisterEventBusListener(configLibConfigSavedHandler, filterByEventName: ConfigLibSavedEventName);
        configLibSettingChangedHandler = OnConfigLibSettingChanged;
        api.Event.RegisterEventBusListener(configLibSettingChangedHandler, filterByEventName: ConfigLibSettingChangedEventName);

        RegisterCommands(api);
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        waterDebugOverlay = new ArchimedesWaterDebugOverlay(api, NetworkChannelName);
        clientChannel = api.Network
            .RegisterChannel(NetworkChannelName)
            .RegisterMessageType<ArchimedesWaterDebugSnapshotPacket>()
            .RegisterMessageType<WaterDebugTooltipQueryPacket>()
            .RegisterMessageType<WaterDebugTooltipResponsePacket>();
        clientChannel.SetMessageHandler<ArchimedesWaterDebugSnapshotPacket>(packet =>
        {
            waterDebugOverlay?.ApplySnapshot(packet);
        });
        clientChannel.SetMessageHandler<WaterDebugTooltipResponsePacket>(packet =>
        {
            waterDebugOverlay?.ApplyTooltipResponse(packet);
        });
    }

    public override void Dispose()
    {
        if (sapi != null)
        {
            sapi.Event.SaveGameLoaded -= OnSaveGameLoaded;
            sapi.Event.GameWorldSave -= OnGameWorldSave;
        }

        WaterManager?.Dispose();
        ArchimedesPosKey.ResetForWorldUnload();
        waterSourceRegenCompatBridge?.Dispose();
        waterfallCompatBridge?.Dispose();
        if (sapi != null && waterDebugTickListenerId != 0)
        {
            sapi.Event.UnregisterGameTickListener(waterDebugTickListenerId);
            waterDebugTickListenerId = 0;
        }
        waterSourceRegenCompatBridge = null;
        waterfallCompatBridge = null;
        WaterManager = null;
        base.Dispose();
    }

    private void OnSaveGameLoaded()
    {
        sapi?.Logger.Notification("{0} SaveGameLoaded: loading mod water state (then re-merge chunk ownership if controllers initialized early)", LogPrefix);
        WaterManager?.Load();
        WaterManager?.ReapplyOwnershipFromLoadedControllers();
        WaterManager?.BeginPostLoadReactivation();
        if (sapi != null)
        {
            // Retry compat resolution after world/mod systems are fully active.
            for (int i = 1; i <= 6; i++)
            {
                int delayMs = 250 * i;
                sapi.Event.RegisterCallback(_ => waterfallCompatBridge?.RefreshForConfig(Config.Water), delayMs);
            }
        }
    }

    private void OnGameWorldSave()
    {
        if (WaterManager != null)
        {
            sapi?.Logger.Notification(
                "{0} GameWorldSave: persisting water manager (weak controller refs={1}; chunk BE data saves with map chunks)",
                LogPrefix,
                WaterManager.LoadedControllerWeakReferenceCount);
        }
        else
        {
            sapi?.Logger.Notification("{0} GameWorldSave: water manager unavailable", LogPrefix);
        }

        WaterManager?.Save();
    }

    private void RegisterCommands(ICoreServerAPI api)
    {
        api.ChatCommands
            .Create("archscrew")
            .WithDescription("Administrative commands for the Archimedes Screw mod.")
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("purge")
                .WithDescription("Delete all Archimedes screw blocks and all known Archimedes water.")
                .HandleWith(_ =>
                {
                    int removed = WaterManager?.PurgeAll() ?? 0;
                    api.Logger.Notification("{0} Command purge removed {1} mod blocks", LogPrefix, removed);
                    return TextCommandResult.Success($"Removed {removed} mod blocks.");
                })
            .EndSubCommand()
            .BeginSubCommand("purgewater")
                .WithDescription("Delete all known Archimedes water blocks.")
                .HandleWith(_ =>
                {
                    int removed = WaterManager?.PurgeManagedWater() ?? 0;
                    api.Logger.Notification("{0} Command purgewater removed {1} Archimedes water blocks", LogPrefix, removed);
                    return TextCommandResult.Success($"Removed {removed} Archimedes water blocks.");
                })
            .EndSubCommand()
            .BeginSubCommand("purgescrews")
                .WithDescription("Delete all Archimedes screw blocks.")
                .HandleWith(_ =>
                {
                    int removed = WaterManager?.PurgeScrewsOnly() ?? 0;
                    api.Logger.Notification("{0} Command purgescrews removed {1} screw blocks", LogPrefix, removed);
                    return TextCommandResult.Success($"Removed {removed} screw blocks.");
                })
            .EndSubCommand()
            .BeginSubCommand("purgewaterscan")
                .WithDescription("Chunk-scan delete of Archimedes water around online players (loaded area).")
                .HandleWith(_ =>
                {
                    int removed = WaterManager?.PurgeArchimedesWaterByChunkScan() ?? 0;
                    api.Logger.Notification("{0} Command purgewaterscan removed {1} Archimedes water blocks", LogPrefix, removed);
                    return TextCommandResult.Success($"Removed {removed} Archimedes water blocks via chunk scan.");
                })
            .EndSubCommand()
            .BeginSubCommand("perf")
                .WithDescription("Control Archimedes profiling logs (on/off/flush/status).")
                .BeginSubCommand("on")
                    .WithDescription("Enable periodic profiling logs.")
                    .HandleWith(_ =>
                    {
                        if (ArchimedesPerf.IsEnabled)
                        {
                            int pending = ArchimedesPerf.GetPendingMetricCount();
                            return TextCommandResult.Success(
                                $"Profiling is already enabled (interval={ArchimedesPerf.FlushIntervalMs}ms, pendingMetrics={pending}).");
                        }

                        ArchimedesPerf.SetEnabled(true);
                        api.Logger.Notification("{0} Profiling enabled (interval={1}ms)", LogPrefix, ArchimedesPerf.FlushIntervalMs);
                        return TextCommandResult.Success($"Profiling enabled (interval={ArchimedesPerf.FlushIntervalMs}ms).");
                    })
                .EndSubCommand()
                .BeginSubCommand("off")
                    .WithDescription("Disable profiling logs.")
                    .HandleWith(_ =>
                    {
                        if (!ArchimedesPerf.IsEnabled)
                        {
                            return TextCommandResult.Success("Profiling is already disabled.");
                        }

                        int pending = ArchimedesPerf.GetPendingMetricCount();
                        if (pending > 0)
                        {
                            ArchimedesPerf.FlushNow(api);
                        }

                        ArchimedesPerf.SetEnabled(false);
                        api.Logger.Notification("{0} Profiling disabled (final flush={1})", LogPrefix, pending > 0 ? "yes" : "no");
                        return TextCommandResult.Success(
                            $"Profiling disabled (final flush={(pending > 0 ? "yes" : "no")}, pendingMetrics={pending}).");
                    })
                .EndSubCommand()
                .BeginSubCommand("flush")
                    .WithDescription("Immediately flush current profiling aggregates to the server log.")
                    .HandleWith(_ =>
                    {
                        if (!ArchimedesPerf.IsEnabled)
                        {
                            return TextCommandResult.Success("Profiling is disabled.");
                        }

                        ArchimedesPerf.FlushNow(api);
                        return TextCommandResult.Success("Profiling stats flushed to server log.");
                    })
                .EndSubCommand()
                .BeginSubCommand("status")
                    .WithDescription("Show profiler status and current interval.")
                    .HandleWith(_ =>
                    {
                        string state = ArchimedesPerf.IsEnabled ? "enabled" : "disabled";
                        int pending = ArchimedesPerf.GetPendingMetricCount();
                        return TextCommandResult.Success(
                            $"Profiling is {state} (interval={ArchimedesPerf.FlushIntervalMs}ms, pendingMetrics={pending}).");
                    })
                .EndSubCommand()
            .EndSubCommand()
            .BeginSubCommand("debugwater")
                .WithDescription("Visualize water debug info (green=consistent owned, orange=owned inconsistent, red=unowned, purple=relay candidates).")
                .BeginSubCommand("on")
                    .WithDescription("Enable periodic water ownership overlay for all connected clients.")
                    .HandleWith(_ =>
                    {
                        waterDebugEnabled = true;
                        EnsureWaterDebugTickListener(api);
                        SendWaterDebugSnapshotToAllPlayers();
                        return TextCommandResult.Success("Water debug overlay enabled (green=consistent owned, orange=owned inconsistent, red=unowned, purple=relay candidates).");
                    })
                .EndSubCommand()
                .BeginSubCommand("off")
                    .WithDescription("Disable ownership overlay and clear highlights.")
                    .HandleWith(_ =>
                    {
                        waterDebugEnabled = false;
                        if (waterDebugTickListenerId != 0)
                        {
                            api.Event.UnregisterGameTickListener(waterDebugTickListenerId);
                            waterDebugTickListenerId = 0;
                        }

                        if (sapi != null && serverChannel != null)
                        {
                            var clearPacket = new ArchimedesWaterDebugSnapshotPacket { Enabled = false };
                            foreach (IPlayer onlinePlayer in sapi.World.AllOnlinePlayers)
                            {
                                if (onlinePlayer is IServerPlayer serverPlayer)
                                {
                                    serverChannel.SendPacket(clearPacket, serverPlayer);
                                }
                            }
                        }
                        return TextCommandResult.Success("Water debug overlay disabled.");
                    })
                .EndSubCommand()
                .BeginSubCommand("scan")
                    .WithDescription("Print nearby managed sources and ownership in chat.")
                    .HandleWith(args =>
                    {
                        if (WaterManager == null || args.Caller.Player is not IServerPlayer player)
                        {
                            return TextCommandResult.Success("No active water manager or player context.");
                        }

                        IReadOnlyList<ManagedSourceDebugInfo> sources =
                            WaterManager.CollectManagedSourceDebug(player.Entity.Pos.AsBlockPos, WaterDebugRadius);
                        int owned = sources.Count(s => s.IsOwned);
                        int unowned = sources.Count - owned;
                        int inconsistentOwned = sources.Count(s => s.IsOwned && !s.IsOwnershipConsistent);
                        player.SendMessage(
                            GlobalConstants.InfoLogChatGroup,
                            $"Managed sources nearby (r={WaterDebugRadius}): total={sources.Count}, owned={owned}, ownedInconsistent={inconsistentOwned}, unowned={unowned}",
                            EnumChatType.Notification
                        );

                        foreach (ManagedSourceDebugInfo source in sources.Take(24))
                        {
                            string state = source.IsOwned
                                ? (source.IsOwnershipConsistent
                                    ? $"owned by {source.OwnerId} (consistent)"
                                    : $"owned by {source.OwnerId} (INCONSISTENT snapshot={source.OwnerSnapshotContainsPos}, loaded={source.OwnerControllerLoaded}, beTracks={source.OwnerLoadedControllerTracksPos})")
                                : "UNOWNED";
                            player.SendMessage(
                                GlobalConstants.InfoLogChatGroup,
                                $"{source.Pos}: {state}",
                                EnumChatType.Notification
                            );
                        }

                        if (sources.Count > 24)
                        {
                            player.SendMessage(
                                GlobalConstants.InfoLogChatGroup,
                                $"...and {sources.Count - 24} more.",
                                EnumChatType.Notification
                            );
                        }

                        return TextCommandResult.Success("Water ownership scan complete.");
                    })
                .EndSubCommand()
            .EndSubCommand();
    }

    private void OnWaterDebugTooltipQuery(IServerPlayer fromPlayer, WaterDebugTooltipQueryPacket packet)
    {
        if (!waterDebugEnabled || WaterManager == null || serverChannel == null)
        {
            return;
        }

        if (fromPlayer.Entity?.Pos == null)
        {
            return;
        }

        BlockPos pos = new(packet.X, packet.Y, packet.Z);
        BlockPos playerPos = fromPlayer.Entity.Pos.AsBlockPos;
        int dx = pos.X - playerPos.X;
        int dy = pos.Y - playerPos.Y;
        int dz = pos.Z - playerPos.Z;
        if (dx * dx + dy * dy + dz * dz > (WaterDebugRadius + 8) * (WaterDebugRadius + 8))
        {
            return;
        }

        ArchimedesWaterDebugTooltipFlags flags = WaterManager.CollectWaterDebugTooltipFlags(pos);
        var response = new WaterDebugTooltipResponsePacket
        {
            X = pos.X,
            Y = pos.Y,
            Z = pos.Z,
            ManagedWaterBlock = flags.ManagedWaterBlock,
            Height7SourceBlock = flags.Height7SourceBlock,
            OwnedManagedSource = flags.OwnedManagedSource,
            RelayOwned = flags.RelayOwned,
            RelayCandidate = flags.RelayCandidate
        };
        serverChannel.SendPacket(response, fromPlayer);
    }

    private void EnsureWaterDebugTickListener(ICoreServerAPI api)
    {
        if (waterDebugTickListenerId != 0)
        {
            return;
        }

        waterDebugTickListenerId = api.Event.RegisterGameTickListener(_ => SendWaterDebugSnapshotToAllPlayers(), 500);
    }

    private void SendWaterDebugSnapshotToAllPlayers()
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("net.debugwater.broadcast");
        if (!waterDebugEnabled || sapi == null || WaterManager == null || serverChannel == null)
        {
            return;
        }

        int playersTargeted = 0;
        int totalSourcesPacked = 0;
        int totalRelayCandidatesPacked = 0;
        foreach (IPlayer onlinePlayer in sapi.World.AllOnlinePlayers)
        {
            if (onlinePlayer is not IServerPlayer serverPlayer || serverPlayer.Entity == null)
            {
                continue;
            }

            IReadOnlyList<ManagedSourceDebugInfo> sources =
                WaterManager.CollectManagedSourceDebug(serverPlayer.Entity.Pos.AsBlockPos, WaterDebugRadius);
            IReadOnlyList<BlockPos> relayCandidates =
                WaterManager.CollectRelayCandidateDebug(serverPlayer.Entity.Pos.AsBlockPos, WaterDebugRadius);
            playersTargeted++;
            totalSourcesPacked += sources.Count;
            totalRelayCandidatesPacked += relayCandidates.Count;
            var packet = new ArchimedesWaterDebugSnapshotPacket
            {
                Enabled = true
            };

            foreach (ManagedSourceDebugInfo source in sources)
            {
                packet.Sources.Add(new ArchimedesWaterDebugSourcePacket
                {
                    X = source.Pos.X,
                    Y = source.Pos.Y,
                    Z = source.Pos.Z,
                    IsOwned = source.IsOwned,
                    OwnerId = source.OwnerId,
                    IsOwnershipConsistent = source.IsOwnershipConsistent,
                    IsRelay = WaterManager.IsRelayOwnedPosition(source.Pos),
                    IsHeight7Source = source.IsHeight7Source
                });
            }

            foreach (BlockPos relayCandidate in relayCandidates)
            {
                packet.RelayCandidates.Add(new ArchimedesWaterDebugPosPacket
                {
                    X = relayCandidate.X,
                    Y = relayCandidate.Y,
                    Z = relayCandidate.Z
                });
            }

            serverChannel.SendPacket(packet, serverPlayer);
        }

        ArchimedesPerf.AddCount("net.debugwater.playersTargeted", playersTargeted);
        ArchimedesPerf.AddCount("net.debugwater.sourcesPacked", totalSourcesPacked);
        ArchimedesPerf.AddCount("net.debugwater.relayCandidatesPacked", totalRelayCandidatesPacked);
    }

    private void OnConfigLibConfigSaved(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (sapi == null)
        {
            return;
        }

        if (pendingWaterConfig == null)
        {
            sapi.Logger.Notification("{0} Config Lib save detected; no pending runtime changes.", LogPrefix);
            return;
        }

        Config.Water.CopyValuesFrom(pendingWaterConfig);
        VerboseDebugEnabled = Config.Water.VerboseDebug;
        pendingWaterConfig = null;

        if (pendingRequiresCentralTickRestart)
        {
            WaterManager?.RestartCentralWaterTickForCurrentConfig();
            pendingRequiresCentralTickRestart = false;
        }

        waterfallCompatBridge?.RefreshForConfig(Config.Water);

        sapi.Logger.Notification("{0} Applied pending Config Lib settings on save", LogPrefix);
        LogEffectiveConfig(sapi, Config);
    }

    private void OnConfigLibSettingChanged(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (sapi == null || data is not TreeAttribute tree)
        {
            return;
        }

        string settingCode = tree.GetAsString("setting");
        pendingWaterConfig ??= CloneWaterConfig(Config.Water);
        bool changed = TryApplySetting(pendingWaterConfig, settingCode, tree, out bool requiresCentralTickRestart);

        if (!changed)
        {
            return;
        }

        if (requiresCentralTickRestart)
        {
            pendingRequiresCentralTickRestart = true;
        }

        sapi.Logger.Notification("{0} Queued Config Lib setting (applies on save): {1}", LogPrefix, settingCode);
    }

    private static ArchimedesScrewConfig.WaterConfig CloneWaterConfig(ArchimedesScrewConfig.WaterConfig source)
    {
        var clone = new ArchimedesScrewConfig.WaterConfig();
        clone.CopyValuesFrom(source);
        return clone;
    }

    private static bool TryApplySetting(
        ArchimedesScrewConfig.WaterConfig target,
        string settingCode,
        TreeAttribute tree,
        out bool requiresCentralTickRestart)
    {
        requiresCentralTickRestart = false;
        switch (settingCode)
        {
            case "FAST_TICK_MS":
                target.FastTickMs = tree.GetInt("value");
                return true;
            case "IDLE_TICK_MS":
                target.IdleTickMs = tree.GetInt("value");
                return true;
            case "GLOBAL_TICK_MS":
                target.GlobalTickMs = tree.GetInt("value");
                requiresCentralTickRestart = true;
                return true;
            case "MAX_CONTROLLERS_PER_GLOBAL_TICK":
                target.MaxControllersPerGlobalTick = tree.GetInt("value");
                requiresCentralTickRestart = true;
                return true;
            case "ASSEMBLY_ANALYSIS_CACHE_MS":
                target.AssemblyAnalysisCacheMs = tree.GetInt("value");
                return true;
            case "MAX_BLOCKS_PER_STEP":
                target.MaxBlocksPerStep = tree.GetInt("value");
                return true;
            case "ENABLE_INCREMENTAL_SOURCE_DRAIN":
                target.EnableIncrementalSourceDrain = tree.GetBool("value");
                return true;
            case "INCREMENTAL_DRAIN_STEP_INTERVAL_MS":
                target.IncrementalDrainStepIntervalMs = tree.GetInt("value");
                return true;
            case "MAX_INCREMENTAL_DRAIN_STEPS_PER_GLOBAL_TICK":
                target.MaxIncrementalDrainStepsPerGlobalTick = tree.GetInt("value");
                return true;
            case "MAX_SCREW_LENGTH":
                target.MaxScrewLength = tree.GetInt("value");
                return true;
            case "VANILLA_CLAIM_HALO_DEPTH":
                target.VanillaClaimHaloDepth = tree.GetInt("value");
                return true;
            case "INTENT_QUEUE_MAX_PER_GLOBAL_TICK":
                target.IntentQueueMaxPerGlobalTick = tree.GetInt("value");
                return true;
            case "ENABLE_RELAY_SOURCES":
                target.EnableRelaySources = tree.GetBool("value");
                return true;
            case "MAX_RELAY_PROMOTIONS_PER_TICK":
                target.MaxRelayPromotionsPerTick = tree.GetInt("value");
                return true;
            case "MAX_RELAY_SOURCES_PER_CONTROLLER":
                target.MaxRelaySourcesPerController = tree.GetInt("value");
                return true;
            case "REQUIRED_MECH_POWER_FOR_MAX_RELAY":
                target.RequiredMechPowerForMaxRelay = tree.GetFloat("value");
                return true;
            case "RELAY_POWER_HYSTERESIS_PCT":
                target.RelayPowerHysteresisPct = tree.GetFloat("value");
                return true;
            case "RELAY_CANDIDATE_ORDERING_MODE":
                target.RelayCandidateOrderingMode = tree.GetString("value", "deterministic");
                return true;
            case "MINIMUM_NETWORK_SPEED":
                target.MinimumNetworkSpeed = tree.GetFloat("value");
                return true;
            case "DEBUG_CONTROLLER_STATS_ON_INTERACT":
                target.DebugControllerStatsOnInteract = tree.GetBool("value");
                return true;
            case "ENABLE_WATERFALL_COMPAT":
                target.EnableWaterfallCompat = tree.GetBool("value");
                return true;
            case "WATERFALL_COMPAT_DEBUG":
                target.WaterfallCompatDebug = tree.GetBool("value");
                return true;
            case "VERBOSE_DEBUG":
                target.VerboseDebug = tree.GetBool("value");
                return true;
            default:
                return false;
        }
    }
}
