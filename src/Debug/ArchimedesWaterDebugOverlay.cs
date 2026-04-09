using System.Collections.Generic;
using System.Linq;
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

internal sealed class ArchimedesWaterDebugOverlay
{
    private const int SourceHighlightSlot = 76031;
    private const int RelayHighlightSlot = 76032;

    /// <summary>
    /// <see cref="ICoreClientAPI.World.HighlightBlocks"/> expects packed <b>RGBA</b> (R = least-significant byte, A = most-significant),
    /// i.e. <c>r | (g &lt;&lt; 8) | (b &lt;&lt; 16) | (a &lt;&lt; 24)</c> in little-endian. Using ARGB-style literals like <c>0xAAFF0000</c>
    /// puts full red in the wrong byte and reads as blue.
    /// </summary>
    private static int PackHighlightRgba(byte r, byte g, byte b, byte a = 0xAA) =>
        r | (g << 8) | (b << 16) | (a << 24);

    private static readonly int OwnedColor = PackHighlightRgba(0x00, 0xFF, 0x00);
    private static readonly int UnownedColor = PackHighlightRgba(0xFF, 0x00, 0x00);
    private static readonly int InconsistentOwnedColor = PackHighlightRgba(0xFF, 0xCC, 0x00);
    private static readonly int RelayCandidateColor = PackHighlightRgba(0xB0, 0x40, 0xFF);

    private readonly ICoreClientAPI capi;

    public ArchimedesWaterDebugOverlay(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public void ApplySnapshot(ArchimedesWaterDebugSnapshotPacket packet)
    {
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
            .Select(s => s.IsOwned
                ? (s.IsOwnershipConsistent ? OwnedColor : InconsistentOwnedColor)
                : UnownedColor)
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
}
