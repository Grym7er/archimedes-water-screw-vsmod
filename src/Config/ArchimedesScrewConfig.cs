namespace ArchimedesScrew;

public sealed class ArchimedesScrewConfig
{
    public WaterConfig Water { get; set; } = new();

    public sealed class WaterConfig
    {
        public int FastTickMs { get; set; } = 250;

        public int IdleTickMs { get; set; } = 2000;

        /// <summary>
        /// How many owned Archimedes sources to remove per fast tick when draining (farthest from reference first).
        /// </summary>
        public int MaxBlocksPerStep { get; set; } = 1;

        public int MaxScrewLength { get; set; } = 32;

        public float MinimumNetworkSpeed { get; set; } = 0.001f;

        /// <summary>
        /// Max rounds of vanilla-source conversion per controller tick (each round expands the managed-water BFS).
        /// </summary>
        public int MaxVanillaConversionPasses { get; set; } = 32;
    }
}
