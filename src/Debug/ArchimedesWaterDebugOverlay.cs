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

internal sealed class ArchimedesWaterDebugOverlay
{
    private const int HighlightSlot = 76031;
    private const int OwnedColor = unchecked((int)0xAA00FF00);
    private const int UnownedColor = unchecked((int)0xAAFF0000);
    private const int InconsistentOwnedColor = unchecked((int)0xAAFFAA00);

    private readonly ICoreClientAPI capi;

    public ArchimedesWaterDebugOverlay(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public void ApplySnapshot(ArchimedesWaterDebugSnapshotPacket packet)
    {
        if (!packet.Enabled || packet.Sources.Count == 0)
        {
            capi.World.HighlightBlocks(capi.World.Player, HighlightSlot, new List<BlockPos>(), new List<int>());
            return;
        }

        List<BlockPos> positions = packet.Sources
            .Select(s => new BlockPos(s.X, s.Y, s.Z))
            .ToList();
        List<int> colors = packet.Sources
            .Select(s => s.IsOwned
                ? (s.IsOwnershipConsistent ? OwnedColor : InconsistentOwnedColor)
                : UnownedColor)
            .ToList();

        capi.World.HighlightBlocks(
            capi.World.Player,
            HighlightSlot,
            positions,
            colors,
            EnumHighlightBlocksMode.Absolute,
            EnumHighlightShape.Cube
        );
    }
}
