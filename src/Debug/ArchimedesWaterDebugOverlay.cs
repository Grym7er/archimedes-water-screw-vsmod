using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ArchimedesScrew;

[ProtoContract]
public sealed class ArchimedesWaterDebugSnapshotPacket
{
    [ProtoMember(1)]
    public bool Enabled { get; set; }

    [ProtoMember(2)]
    public List<ArchimedesWaterDebugSourcePacket> Sources { get; set; } = new();

    [ProtoMember(3)]
    public List<ArchimedesWaterDebugPosPacket> RelayCandidates { get; set; } = new();
}

[ProtoContract]
public sealed class ArchimedesWaterDebugSourcePacket
{
    [ProtoMember(1)]
    public int X { get; set; }

    [ProtoMember(2)]
    public int Y { get; set; }

    [ProtoMember(3)]
    public int Z { get; set; }

    [ProtoMember(4)]
    public bool IsOwned { get; set; }

    [ProtoMember(5)]
    public string OwnerId { get; set; } = string.Empty;

    [ProtoMember(6)]
    public bool IsOwnershipConsistent { get; set; }

    [ProtoMember(7)]
    public bool IsRelay { get; set; }

    /// <summary>
    /// True if the fluid at this pos is an Archimedes managed height-7 (self-sustaining) source block.
    /// Height-6 managed water cells are still reported in the snapshot (for ownership visualization),
    /// but must not be treated as true sources by downstream consumers.
    /// </summary>
    [ProtoMember(8)]
    public bool IsHeight7Source { get; set; }
}

[ProtoContract]
public sealed class ArchimedesWaterDebugPosPacket
{
    [ProtoMember(1)]
    public int X { get; set; }

    [ProtoMember(2)]
    public int Y { get; set; }

    [ProtoMember(3)]
    public int Z { get; set; }
}

[ProtoContract]
public sealed class WaterDebugTooltipQueryPacket
{
    [ProtoMember(1)]
    public int X { get; set; }

    [ProtoMember(2)]
    public int Y { get; set; }

    [ProtoMember(3)]
    public int Z { get; set; }
}

[ProtoContract]
public sealed class WaterDebugTooltipResponsePacket
{
    [ProtoMember(1)]
    public int X { get; set; }

    [ProtoMember(2)]
    public int Y { get; set; }

    [ProtoMember(3)]
    public int Z { get; set; }

    [ProtoMember(4)]
    public bool ManagedWaterBlock { get; set; }

    [ProtoMember(5)]
    public bool Height7SourceBlock { get; set; }

    [ProtoMember(6)]
    public bool OwnedManagedSource { get; set; }

    [ProtoMember(7)]
    public bool RelayOwned { get; set; }

    [ProtoMember(8)]
    public bool RelayCandidate { get; set; }
}

internal sealed class ArchimedesWaterDebugOverlay
{
    private const int SourceHighlightSlot = 76031;
    private const int RelayHighlightSlot = 76032;
    private const int QueryThrottleMs = 300;
    private const int FlagCacheTtlMs = 4000;

    private static int PackHighlightRgba(byte r, byte g, byte b, byte a = 0xAA) =>
        r | (g << 8) | (b << 16) | (a << 24);

    private static readonly int OwnedColor = PackHighlightRgba(0x00, 0xFF, 0x00);
    private static readonly int UnownedColor = PackHighlightRgba(0xFF, 0x00, 0x00);
    private static readonly int InconsistentOwnedColor = PackHighlightRgba(0xFF, 0xCC, 0x00);
    private static readonly int RelayCandidateColor = PackHighlightRgba(0xB0, 0x40, 0xFF);
    /// <summary>Dimmer cyan cube for owned height-6 managed water (flowing, not a true self-sustaining source).</summary>
    private static readonly int FlowCellOwnedColor = PackHighlightRgba(0x00, 0xB0, 0xC0, 0x88);
    /// <summary>Dimmer magenta cube for unowned height-6 managed water.</summary>
    private static readonly int FlowCellUnownedColor = PackHighlightRgba(0xC0, 0x00, 0x80, 0x88);

    private readonly ICoreClientAPI capi;
    private readonly string networkChannelName;

    private readonly object cacheLock = new();
    private bool overlayEnabled;
    private HashSet<(int X, int Y, int Z)> relayCandidateKeys = new();

    /// <summary>Keyed by block position; value is flags and expiry time (Environment.TickCount64).</summary>
    private readonly Dictionary<(int X, int Y, int Z), (ArchimedesWaterDebugTooltipFlags Flags, long ExpiryTickMs)> serverFlagCache = new();

    private readonly Dictionary<(int X, int Y, int Z), long> lastQuerySentTickMs = new();

    private static (int X, int Y, int Z) PosKey(BlockPos pos) => (pos.X, pos.Y, pos.Z);

    public ArchimedesWaterDebugOverlay(ICoreClientAPI capi, string networkChannelName)
    {
        this.capi = capi;
        this.networkChannelName = networkChannelName;
    }

    public bool IsOverlayEnabled
    {
        get
        {
            lock (cacheLock)
            {
                return overlayEnabled;
            }
        }
    }

    public void ApplySnapshot(ArchimedesWaterDebugSnapshotPacket packet)
    {
        lock (cacheLock)
        {
            overlayEnabled = packet.Enabled;
            PruneExpiredLocked(Environment.TickCount64);
            if (!packet.Enabled)
            {
                serverFlagCache.Clear();
                relayCandidateKeys = new HashSet<(int X, int Y, int Z)>();
                lastQuerySentTickMs.Clear();
            }
            else
            {
                relayCandidateKeys = new HashSet<(int X, int Y, int Z)>();
                foreach (ArchimedesWaterDebugPosPacket rc in packet.RelayCandidates)
                {
                    relayCandidateKeys.Add((rc.X, rc.Y, rc.Z));
                }

                long now = Environment.TickCount64;
                long expiry = now + FlagCacheTtlMs;
                foreach (ArchimedesWaterDebugSourcePacket s in packet.Sources)
                {
                    BlockPos pos = new(s.X, s.Y, s.Z);
                    (int X, int Y, int Z) key = PosKey(pos);
                    Block fluid = capi.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
                    ArchimedesWaterDebugTooltipFlags merged = MergeClientAndServerFields(
                        fluid,
                        s.IsOwned,
                        s.IsRelay,
                        relayCandidateKeys.Contains(key));
                    serverFlagCache[key] = (merged, expiry);
                }

                foreach (ArchimedesWaterDebugPosPacket rc in packet.RelayCandidates)
                {
                    BlockPos pos = new(rc.X, rc.Y, rc.Z);
                    (int X, int Y, int Z) key = PosKey(pos);
                    if (serverFlagCache.ContainsKey(key))
                    {
                        continue;
                    }

                    Block fluid = capi.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
                    if (!IsArchimedesManagedWaterBlock(fluid))
                    {
                        continue;
                    }

                    ArchimedesWaterDebugTooltipFlags merged = MergeClientAndServerFields(
                        fluid,
                        owned: false,
                        relay: false,
                        relayCandidate: true);
                    serverFlagCache[key] = (merged, expiry);
                }
            }
        }

        if (!packet.Enabled)
        {
            capi.World.HighlightBlocks(capi.World.Player, SourceHighlightSlot, new List<BlockPos>(), new List<int>());
            capi.World.HighlightBlocks(capi.World.Player, RelayHighlightSlot, new List<BlockPos>(), new List<int>());
            return;
        }

        List<BlockPos> sourcePositions = packet.Sources
            .Select(s => new BlockPos(s.X, s.Y, s.Z))
            .ToList();
        List<int> sourceColors = packet.Sources
            .Select(s =>
            {
                if (!s.IsHeight7Source)
                {
                    return s.IsOwned ? FlowCellOwnedColor : FlowCellUnownedColor;
                }
                return s.IsOwned
                    ? (s.IsOwnershipConsistent ? OwnedColor : InconsistentOwnedColor)
                    : UnownedColor;
            })
            .ToList();

        capi.World.HighlightBlocks(
            capi.World.Player,
            SourceHighlightSlot,
            sourcePositions,
            sourceColors,
            EnumHighlightBlocksMode.Absolute,
            EnumHighlightShape.Cube
        );

        List<BlockPos> relayPositions = packet.RelayCandidates
            .Select(s => new BlockPos(s.X, s.Y, s.Z))
            .ToList();
        List<int> relayColors = packet.RelayCandidates
            .Select(_ => RelayCandidateColor)
            .ToList();

        capi.World.HighlightBlocks(
            capi.World.Player,
            RelayHighlightSlot,
            relayPositions,
            relayColors,
            EnumHighlightBlocksMode.Absolute,
            EnumHighlightShape.Cube
        );
    }

    public void ApplyTooltipResponse(WaterDebugTooltipResponsePacket packet)
    {
        var flags = new ArchimedesWaterDebugTooltipFlags(
            packet.ManagedWaterBlock,
            packet.Height7SourceBlock,
            packet.OwnedManagedSource,
            packet.RelayOwned,
            packet.RelayCandidate);
        BlockPos pos = new(packet.X, packet.Y, packet.Z);
        (int X, int Y, int Z) key = PosKey(pos);
        long expiry = Environment.TickCount64 + FlagCacheTtlMs;
        lock (cacheLock)
        {
            serverFlagCache[key] = (flags, expiry);
        }
    }

    /// <summary>Builds debug appendix for Archimedes fluid blocks when overlay is enabled.</summary>
    public string? BuildWaterDebugTooltipAppendix(BlockPos pos, Block fluidBlock)
    {
        if (!IsOverlayEnabled)
        {
            return null;
        }

        ArchimedesWaterDebugTooltipFlags flags;
        bool haveServer;
        lock (cacheLock)
        {
            long now = Environment.TickCount64;
            PruneExpiredLocked(now);
            (int X, int Y, int Z) key = PosKey(pos);
            bool managed = IsArchimedesManagedWaterBlock(fluidBlock);
            bool height7 = IsArchimedesHeight7SourceBlock(fluidBlock);
            if (serverFlagCache.TryGetValue(key, out var cached) && now < cached.ExpiryTickMs)
            {
                flags = new ArchimedesWaterDebugTooltipFlags(
                    managed,
                    height7,
                    cached.Flags.OwnedManagedSource,
                    cached.Flags.RelayOwned,
                    cached.Flags.RelayCandidate);
                haveServer = true;
            }
            else
            {
                flags = new ArchimedesWaterDebugTooltipFlags(
                    managed,
                    height7,
                    OwnedManagedSource: false,
                    RelayOwned: false,
                    RelayCandidate: relayCandidateKeys.Contains(key));
                haveServer = false;
            }
        }

        if (!haveServer)
        {
            RequestTooltipFlagsIfNeeded(pos);
        }

        return FormatFlags(flags, haveServer);
    }

    private void RequestTooltipFlagsIfNeeded(BlockPos pos)
    {
        (int X, int Y, int Z) key = PosKey(pos);
        long now = Environment.TickCount64;
        lock (cacheLock)
        {
            if (!overlayEnabled)
            {
                return;
            }

            if (lastQuerySentTickMs.TryGetValue(key, out long last) && now - last < QueryThrottleMs)
            {
                return;
            }

            lastQuerySentTickMs[key] = now;
        }

        IClientNetworkChannel? ch = capi.Network.GetChannel(networkChannelName);
        ch?.SendPacket(new WaterDebugTooltipQueryPacket { X = pos.X, Y = pos.Y, Z = pos.Z });
    }

    private void PruneExpiredLocked(long nowMs)
    {
        List<(int X, int Y, int Z)> remove = new();
        foreach (KeyValuePair<(int X, int Y, int Z), (ArchimedesWaterDebugTooltipFlags Flags, long ExpiryTickMs)> pair in serverFlagCache)
        {
            if (nowMs >= pair.Value.ExpiryTickMs)
            {
                remove.Add(pair.Key);
            }
        }

        foreach ((int X, int Y, int Z) key in remove)
        {
            serverFlagCache.Remove(key);
        }
    }

    private static ArchimedesWaterDebugTooltipFlags MergeClientAndServerFields(
        Block fluid,
        bool owned,
        bool relay,
        bool relayCandidate)
    {
        bool managed = IsArchimedesManagedWaterBlock(fluid);
        bool height7 = IsArchimedesHeight7SourceBlock(fluid);
        return new ArchimedesWaterDebugTooltipFlags(managed, height7, owned, relay, relayCandidate);
    }

    private static bool IsArchimedesManagedWaterBlock(Block fluid)
    {
        return fluid.Code?.Domain == ArchimedesScrewModSystem.ModId &&
               ArchimedesWaterFamilies.IsManagedWater(fluid);
    }

    private static bool IsArchimedesHeight7SourceBlock(Block fluid)
    {
        return IsArchimedesManagedWaterBlock(fluid) &&
               string.Equals(fluid.Variant?["height"], "7", StringComparison.Ordinal);
    }

    private static string FormatFlags(ArchimedesWaterDebugTooltipFlags flags, bool serverSynced)
    {
        string pending = serverSynced ? string.Empty : "\n(server fields: requesting…)";
        return new StringBuilder()
            .AppendLine()
            .AppendLine("[archimedes_screw water debug]")
            .Append("Managed water block: ").Append(YesNo(flags.ManagedWaterBlock)).AppendLine()
            .Append("Height-7 source block: ").Append(YesNo(flags.Height7SourceBlock)).AppendLine()
            .Append("Owned managed source: ").Append(YesNo(flags.OwnedManagedSource)).AppendLine()
            .Append("Relay owned: ").Append(YesNo(flags.RelayOwned)).AppendLine()
            .Append("Relay candidate: ").Append(YesNo(flags.RelayCandidate))
            .Append(pending)
            .ToString();
    }

    private static string YesNo(bool v) => v ? "yes" : "no";
}
