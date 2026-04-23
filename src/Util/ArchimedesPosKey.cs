using System;
using Vintagestory.API.MathTools;

namespace ArchimedesScrew;

/// <summary>
/// Packed runtime position key for hot-path dictionary/set usage.
/// Layout is initialized per loaded world from runtime map sizes. Call <see cref="ResetForWorldUnload"/>
/// when the server world is torn down so a later world with different map bounds can re-initialize.
/// </summary>
public static class ArchimedesPosKey
{
    private static bool initialized;
    private static int mapSizeX;
    private static int mapSizeY;
    private static int mapSizeZ;

    private static int zBits;
    private static int yBits;
    private static int xBits;
    private static int yShift;
    private static int xShift;
    private static long zMask;
    private static long yMask;
    private static long xMask;

    public static void InitializeForWorld(int sizeX, int sizeY, int sizeZ)
    {
        if (sizeX <= 0 || sizeY <= 0 || sizeZ <= 0)
        {
            throw new InvalidOperationException($"Invalid world map size for ArchimedesPosKey: ({sizeX},{sizeY},{sizeZ}).");
        }

        int newZBits = BitsRequired(sizeZ - 1);
        int newYBits = BitsRequired(sizeY - 1);
        int newXBits = BitsRequired(sizeX - 1);
        int totalBits = newXBits + newYBits + newZBits;
        if (totalBits > 63)
        {
            throw new InvalidOperationException(
                $"World map size ({sizeX},{sizeY},{sizeZ}) requires {totalBits} bits (>63) and cannot be packed into a long.");
        }

        if (initialized)
        {
            if (sizeX != mapSizeX || sizeY != mapSizeY || sizeZ != mapSizeZ)
            {
                throw new InvalidOperationException(
                    $"ArchimedesPosKey already initialized for ({mapSizeX},{mapSizeY},{mapSizeZ}), cannot reinitialize for ({sizeX},{sizeY},{sizeZ}).");
            }

            return;
        }

        mapSizeX = sizeX;
        mapSizeY = sizeY;
        mapSizeZ = sizeZ;
        zBits = newZBits;
        yBits = newYBits;
        xBits = newXBits;
        yShift = zBits;
        xShift = zBits + yBits;
        zMask = MaskForBits(zBits);
        yMask = MaskForBits(yBits);
        xMask = MaskForBits(xBits);
        initialized = true;
        RunInitializationSelfChecks();
    }

    /// <summary>
    /// Clears static layout state after server world unload. Safe to call multiple times (idempotent).
    /// Call only when no code still relies on packed keys from the previous world (typically after
    /// <see cref="ArchimedesWaterNetworkManager"/> disposal).
    /// </summary>
    public static void ResetForWorldUnload()
    {
        initialized = false;
        mapSizeX = 0;
        mapSizeY = 0;
        mapSizeZ = 0;
        zBits = 0;
        yBits = 0;
        xBits = 0;
        yShift = 0;
        xShift = 0;
        zMask = 0;
        yMask = 0;
        xMask = 0;
    }

    public static long Pack(BlockPos pos)
    {
        return Pack(pos.X, pos.Y, pos.Z);
    }

    public static long Pack(int x, int y, int z)
    {
        EnsureInitialized();
        ValidateRange(x, y, z);
        return ((long)x << xShift) | ((long)y << yShift) | (long)z;
    }

    public static bool TryPack(int x, int y, int z, out long packed)
    {
        EnsureInitialized();
        // Inlined bounds check: avoids the second EnsureInitialized() inside IsInBounds() in the
        // hot path (BFS neighbour loops call TryPack tens of thousands of times per tick).
        if ((uint)x >= (uint)mapSizeX || (uint)y >= (uint)mapSizeY || (uint)z >= (uint)mapSizeZ)
        {
            packed = 0;
            return false;
        }

        packed = ((long)x << xShift) | ((long)y << yShift) | (long)z;
        return true;
    }

    public static bool IsInBounds(int x, int y, int z)
    {
        EnsureInitialized();
        return (uint)x < (uint)mapSizeX &&
               (uint)y < (uint)mapSizeY &&
               (uint)z < (uint)mapSizeZ;
    }

    public static void Unpack(long key, BlockPos target)
    {
        EnsureInitialized();
        target.Set(
            (int)((key >> xShift) & xMask),
            (int)((key >> yShift) & yMask),
            (int)(key & zMask)
        );
    }

    public static BlockPos UnpackToNew(long key)
    {
        EnsureInitialized();
        return new BlockPos(
            (int)((key >> xShift) & xMask),
            (int)((key >> yShift) & yMask),
            (int)(key & zMask)
        );
    }

    public static int ExtractY(long key)
    {
        EnsureInitialized();
        return (int)((key >> yShift) & yMask);
    }

    public static bool TryPackFromString(string key, out long packed)
    {
        packed = 0;
        if (!ArchimedesWaterNetworkManager.TryParsePosKey(key, out BlockPos pos))
        {
            return false;
        }

        packed = Pack(pos);
        return true;
    }

    public static string ToDebugString(long key)
    {
        EnsureInitialized();
        int x = (int)((key >> xShift) & xMask);
        int y = (int)((key >> yShift) & yMask);
        int z = (int)(key & zMask);
        return $"{x},{y},{z}";
    }

    private static void ValidateRange(int x, int y, int z)
    {
        // Cast-to-uint trick collapses the negative + over-bound checks into a single comparison.
        if ((uint)x >= (uint)mapSizeX || (uint)y >= (uint)mapSizeY || (uint)z >= (uint)mapSizeZ)
        {
            throw new InvalidOperationException(
                $"Out-of-range world position ({x},{y},{z}) for map bounds [0..{mapSizeX - 1}], [0..{mapSizeY - 1}], [0..{mapSizeZ - 1}].");
        }
    }

    private static void EnsureInitialized()
    {
        if (!initialized)
        {
            throw new InvalidOperationException("ArchimedesPosKey is not initialized. Call InitializeForWorld() at manager startup.");
        }
    }

    private static int BitsRequired(int maxValue)
    {
        int bits = 0;
        do
        {
            bits++;
            maxValue >>= 1;
        } while (maxValue > 0);

        return bits;
    }

    private static long MaskForBits(int bits)
    {
        return bits >= 63 ? long.MaxValue : ((1L << bits) - 1L);
    }

    private static void RunInitializationSelfChecks()
    {
        // Basic pack/unpack bijection checks at canonical bounds.
        AssertRoundTrip(0, 0, 0);
        AssertRoundTrip(mapSizeX - 1, mapSizeY - 1, mapSizeZ - 1);
        AssertRoundTrip(Math.Min(mapSizeX - 1, 1), Math.Min(mapSizeY - 1, 1), Math.Min(mapSizeZ - 1, 1));
    }

    private static void AssertRoundTrip(int x, int y, int z)
    {
        long packed = Pack(x, y, z);
        BlockPos unpacked = UnpackToNew(packed);
        if (unpacked.X != x || unpacked.Y != y || unpacked.Z != z)
        {
            throw new InvalidOperationException(
                $"ArchimedesPosKey round-trip failure: ({x},{y},{z}) -> {packed} -> ({unpacked.X},{unpacked.Y},{unpacked.Z}).");
        }
    }
}
