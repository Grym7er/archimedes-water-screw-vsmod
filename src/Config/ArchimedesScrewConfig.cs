namespace ArchimedesScrew;

public sealed class ArchimedesScrewConfig
{
    public WaterConfig Water { get; set; } = new();

    public sealed class WaterConfig
    {
        public int FastTickMs { get; set; } = 250;

        public int IdleTickMs { get; set; } = 2000;

        /// <summary>
        /// Server-wide game tick interval (ms) that dispatches intake controller work.
        /// </summary>
        public int GlobalTickMs { get; set; } = 50;

        /// <summary>
        /// Max intake controllers processed per global tick (round-robin when many are due).
        /// </summary>
        public int MaxControllersPerGlobalTick { get; set; } = 48;

        /// <summary>
        /// Reuse assembly analysis within this window (ms) to avoid duplicate scans.
        /// </summary>
        public int AssemblyAnalysisCacheMs { get; set; } = 120;

        /// <summary>
        /// How many owned Archimedes sources to remove per fast tick when draining (nearest to reference first).
        /// </summary>
        public int MaxBlocksPerStep { get; set; } = 1;

        /// <summary>
        /// If true, managed source drain requests decay height step-by-step instead of instant deletion.
        /// </summary>
        public bool EnableIncrementalSourceDrain { get; set; } = true;

        /// <summary>
        /// Milliseconds between source-height decrement steps while incremental drain is active.
        /// </summary>
        public int IncrementalDrainStepIntervalMs { get; set; } = 120;

        /// <summary>
        /// Max incremental drain steps processed per global water tick.
        /// </summary>
        public int MaxIncrementalDrainStepsPerGlobalTick { get; set; } = 32;

        public int MaxScrewLength { get; set; } = 32;

        public float MinimumNetworkSpeed { get; set; } = 0.001f;

        /// <summary>
        /// Max vanilla traversal depth away from the managed frontier when evaluating claims.
        /// </summary>
        public int VanillaClaimHaloDepth { get; set; } = 2;

        /// <summary>
        /// Max queued conversion intents processed each global water tick.
        /// </summary>
        public int IntentQueueMaxPerGlobalTick { get; set; } = 96;

        /// <summary>
        /// Enables/disables relay source creation for long-distance aqueduct support.
        /// </summary>
        public bool EnableRelaySources { get; set; } = true;

        /// <summary>
        /// Max relay source promotions (and trims) per controller tick.
        /// </summary>
        public int MaxRelayPromotionsPerTick { get; set; } = 12;

        /// <summary>
        /// Absolute max relay-created sources one controller may own at full power.
        /// </summary>
        public int MaxRelaySourcesPerController { get; set; } = 12;

        /// <summary>
        /// Mechanical power needed to unlock full relay cap.
        /// </summary>
        public float RequiredMechPowerForMaxRelay { get; set; } = 0.5f;

        /// <summary>
        /// Fractional hysteresis around relay-cap transitions to avoid add/remove thrash.
        /// </summary>
        public float RelayPowerHysteresisPct { get; set; } = 0.05f;

        /// <summary>
        /// Relay promotion candidate ordering mode. Supported values: deterministic, randomWithinDistanceBucket.
        /// </summary>
        public string RelayCandidateOrderingMode { get; set; } = "deterministic";

        /// <summary>
        /// Enables verbose per-controller diagnostics on right-click status checks.
        /// </summary>
        public bool DebugControllerStatsOnInteract { get; set; } = false;

        /// <summary>
        /// Enables optional compatibility hooks for the Waterfall mod when installed.
        /// </summary>
        public bool EnableWaterfallCompat { get; set; } = true;

        /// <summary>
        /// Emits verbose logging for Waterfall compatibility hook decisions.
        /// </summary>
        public bool WaterfallCompatDebug { get; set; } = false;

        /// <summary>
        /// Routes non-essential mod diagnostics to verbose debug log entries.
        /// </summary>
        public bool VerboseDebug { get; set; } = false;

        /// <summary>
        /// Copies tunable fields onto this instance so existing references (e.g. block entities) stay valid.
        /// </summary>
        public void CopyValuesFrom(WaterConfig source)
        {
            FastTickMs = source.FastTickMs;
            IdleTickMs = source.IdleTickMs;
            GlobalTickMs = source.GlobalTickMs;
            MaxControllersPerGlobalTick = source.MaxControllersPerGlobalTick;
            AssemblyAnalysisCacheMs = source.AssemblyAnalysisCacheMs;
            MaxBlocksPerStep = source.MaxBlocksPerStep;
            EnableIncrementalSourceDrain = source.EnableIncrementalSourceDrain;
            IncrementalDrainStepIntervalMs = source.IncrementalDrainStepIntervalMs;
            MaxIncrementalDrainStepsPerGlobalTick = source.MaxIncrementalDrainStepsPerGlobalTick;
            MaxScrewLength = source.MaxScrewLength;
            MinimumNetworkSpeed = source.MinimumNetworkSpeed;
            VanillaClaimHaloDepth = source.VanillaClaimHaloDepth;
            IntentQueueMaxPerGlobalTick = source.IntentQueueMaxPerGlobalTick;
            EnableRelaySources = source.EnableRelaySources;
            MaxRelayPromotionsPerTick = source.MaxRelayPromotionsPerTick;
            MaxRelaySourcesPerController = source.MaxRelaySourcesPerController;
            RequiredMechPowerForMaxRelay = source.RequiredMechPowerForMaxRelay;
            RelayPowerHysteresisPct = source.RelayPowerHysteresisPct;
            RelayCandidateOrderingMode = source.RelayCandidateOrderingMode;
            DebugControllerStatsOnInteract = source.DebugControllerStatsOnInteract;
            EnableWaterfallCompat = source.EnableWaterfallCompat;
            WaterfallCompatDebug = source.WaterfallCompatDebug;
            VerboseDebug = source.VerboseDebug;
        }
    }
}
