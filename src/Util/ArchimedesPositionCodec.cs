using System.Collections.Generic;
using Vintagestory.API.MathTools;

namespace ArchimedesScrew;

public static class ArchimedesPositionCodec
{
    public static int[] EncodePositions(IReadOnlyCollection<BlockPos> positions)
    {
        int[] flat = new int[positions.Count * 3];
        int index = 0;
        foreach (BlockPos pos in positions)
        {
            flat[index++] = pos.X;
            flat[index++] = pos.Y;
            flat[index++] = pos.Z;
        }

        return flat;
    }

    public static int[] EncodePositions(IEnumerable<BlockPos> positions)
    {
        if (positions is IReadOnlyCollection<BlockPos> coll)
        {
            return EncodePositions(coll);
        }

        List<int> flat = new();
        foreach (BlockPos pos in positions)
        {
            flat.Add(pos.X);
            flat.Add(pos.Y);
            flat.Add(pos.Z);
        }

        return flat.ToArray();
    }

    public static IEnumerable<BlockPos> DecodePositions(int[]? flatPositions)
    {
        if (flatPositions == null || flatPositions.Length < 3)
        {
            yield break;
        }

        for (int i = 0; i + 2 < flatPositions.Length; i += 3)
        {
            yield return new BlockPos(flatPositions[i], flatPositions[i + 1], flatPositions[i + 2]);
        }
    }

    public static BlockPos? DecodeSinglePos(int[]? values)
    {
        if (values == null || values.Length < 3)
        {
            return null;
        }

        return new BlockPos(values[0], values[1], values[2]);
    }

    public static int DistanceSquared(BlockPos a, BlockPos b)
    {
        int dx = a.X - b.X;
        int dy = a.Y - b.Y;
        int dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }
}
