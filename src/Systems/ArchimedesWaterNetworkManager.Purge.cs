using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace ArchimedesScrew;

public sealed partial class ArchimedesWaterNetworkManager
{
    /// <summary>
    /// Removes Archimedes managed fluid at <paramref name="pos"/> with <see cref="SuppressRemovalNotification"/> applied for <paramref name="posKey"/>.
    /// </summary>
    /// <returns><c>true</c> if a managed Archimedes water fluid block was deleted.</returns>
    private bool TryRemoveArchimedesManagedFluidAt(BlockPos pos, long posKey)
    {
        Block block = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!IsArchimedesWaterBlock(block))
        {
            return false;
        }

        SuppressRemovalNotification(posKey);
        api.World.BlockAccessor.SetBlock(0, pos, BlockLayersAccess.Fluid);
        return true;
    }

    public int PurgeAll()
    {
        int removed = PurgeManagedWater();
        removed += PurgeScrewsOnly();
        api.Logger.Notification("{0} PurgeAll removed {1} blocks", ArchimedesScrewModSystem.LogPrefix, removed);
        return removed;
    }

    public int PurgeManagedWater()
    {
        HashSet<long> anchorKeys = BuildAllArchimedesWaterAnchorKeys();

        foreach (WeakReference<BlockEntityWaterArchimedesScrew> pair in loadedControllers.Values)
        {
            if (pair.TryGetTarget(out BlockEntityWaterArchimedesScrew? controller))
            {
                controller.ClearOwnedStateAfterPurge();
            }
        }

        HashSet<long> allWaterKeys = new();
        foreach (long key in anchorKeys)
        {
            BlockPos pos = ArchimedesPosKey.UnpackToNew(key);
            CollectManagedComponentKeysAroundAnchor(pos, allWaterKeys);
        }

        int removed = 0;
        List<BlockPos> removedPositions = new();
        foreach (long key in allWaterKeys)
        {
            BlockPos pos = ArchimedesPosKey.UnpackToNew(key);
            if (TryRemoveArchimedesManagedFluidAt(pos, key))
            {
                removedPositions.Add(pos);
                removed++;
            }
        }

        foreach (BlockPos pos in removedPositions)
        {
            NotifyNeighboursOfFluidRemoval(pos);
        }

        sourceOwnerByPos.Clear();
        unownedCleanupCooldownUntilMsByKey.Clear();
        controllerOwnedById.Clear();
        controllerRelaySourceKeys.Clear();
        relayOwnerByPos.Clear();

        api.Logger.Notification(
            "{0} PurgeManagedWater deleted {1} Archimedes water blocks",
            ArchimedesScrewModSystem.LogPrefix,
            removed
        );
        return removed;
    }

    public int PurgeScrewsOnly()
    {
        foreach (WeakReference<BlockEntityWaterArchimedesScrew> pair in loadedControllers.Values)
        {
            if (pair.TryGetTarget(out BlockEntityWaterArchimedesScrew? controller))
            {
                controller.ReleaseAllManagedWater();
            }
        }

        int removed = 0;
        foreach (long key in screwBlockKeys.ToArray())
        {
            BlockPos pos = ArchimedesPosKey.UnpackToNew(key);
            Block block = api.World.BlockAccessor.GetBlock(pos);
            if (block is not BlockWaterArchimedesScrew)
            {
                continue;
            }

            api.World.BlockAccessor.SetBlock(0, pos);
            removed++;
        }

        screwBlockKeys.Clear();
        controllerPosById.Clear();

        api.Logger.Notification("{0} PurgeScrewsOnly removed {1} screw blocks", ArchimedesScrewModSystem.LogPrefix, removed);
        return removed;
    }

    /// <summary>
    /// Chunk-scan purge: scans loaded chunks around online players and removes/reverts any Archimedes fluid it finds,
    /// even if not present in manager ownership/snapshot state.
    /// </summary>
    public int PurgeArchimedesWaterByChunkScan(int chunkRadius = 8)
    {
        foreach (WeakReference<BlockEntityWaterArchimedesScrew> pair in loadedControllers.Values)
        {
            if (pair.TryGetTarget(out BlockEntityWaterArchimedesScrew? controller))
            {
                controller.ClearOwnedStateAfterPurge();
            }
        }

        int radius = Math.Clamp(chunkRadius, 1, 64);
        const int chunkSize = 32;
        int mapHeight = Math.Max(1, api.WorldManager.MapSizeY);

        HashSet<(int ChunkX, int ChunkZ)> chunks = new();
        foreach (IPlayer player in api.World.AllOnlinePlayers)
        {
            if (player?.Entity == null)
            {
                continue;
            }

            int centerChunkX = (int)Math.Floor(player.Entity.ServerPos.X / chunkSize);
            int centerChunkZ = (int)Math.Floor(player.Entity.ServerPos.Z / chunkSize);
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    chunks.Add((centerChunkX + dx, centerChunkZ + dz));
                }
            }
        }

        int removed = 0;
        List<BlockPos> removedPositions = new();
        foreach ((int chunkX, int chunkZ) in chunks)
        {
            int minX = chunkX * chunkSize;
            int minZ = chunkZ * chunkSize;
            for (int lx = 0; lx < chunkSize; lx++)
            {
                for (int lz = 0; lz < chunkSize; lz++)
                {
                    int x = minX + lx;
                    int z = minZ + lz;
                    for (int y = 0; y < mapHeight; y++)
                    {
                        BlockPos pos = new(x, y, z);
                        long key = ArchimedesPosKey.Pack(pos);
                        if (TryRemoveArchimedesManagedFluidAt(pos, key))
                        {
                            removedPositions.Add(pos.Copy());
                            removed++;
                        }
                    }
                }
            }
        }

        foreach (BlockPos pos in removedPositions)
        {
            NotifyNeighboursOfFluidRemoval(pos);
        }

        sourceOwnerByPos.Clear();
        unownedCleanupCooldownUntilMsByKey.Clear();
        controllerOwnedById.Clear();
        controllerRelaySourceKeys.Clear();
        relayOwnerByPos.Clear();

        api.Logger.Notification(
            "{0} PurgeArchimedesWaterByChunkScan deleted {1} Archimedes water blocks (scannedChunks={2}, radius={3})",
            ArchimedesScrewModSystem.LogPrefix,
            removed,
            chunks.Count,
            radius
        );
        return removed;
    }

    private void CollectManagedComponentKeysAroundAnchor(BlockPos anchor, HashSet<long> allWaterKeys)
    {
        TryCollectManagedComponent(anchor, allWaterKeys);
        foreach (BlockFacing face in BlockFacing.ALLFACES)
        {
            TryCollectManagedComponent(anchor.AddCopy(face), allWaterKeys);
        }
    }

    private HashSet<long> BuildManagedWaterAnchorKeys()
    {
        HashSet<long> anchorKeys = new();

        foreach (long key in sourceOwnerByPos.Keys)
        {
            anchorKeys.Add(key);
        }

        foreach (int[] flatPositions in controllerOwnedById.Values)
        {
            foreach (BlockPos pos in ArchimedesPositionCodec.DecodePositions(flatPositions))
            {
                anchorKeys.Add(ArchimedesPosKey.Pack(pos));
            }
        }

        foreach (long screwKey in screwBlockKeys)
        {
            anchorKeys.Add(screwKey);
        }

        foreach (long controllerPosKey in controllerPosById.Values)
        {
            anchorKeys.Add(controllerPosKey);
        }

        foreach (WeakReference<BlockEntityWaterArchimedesScrew> wr in loadedControllers.Values)
        {
            if (wr.TryGetTarget(out BlockEntityWaterArchimedesScrew? controller))
            {
                anchorKeys.Add(ArchimedesPosKey.Pack(controller.Pos));
            }
        }

        return anchorKeys;
    }

    private HashSet<long> BuildAllArchimedesWaterAnchorKeys()
    {
        HashSet<long> anchorKeys = BuildManagedWaterAnchorKeys();

        foreach (long key in screwBlockKeys)
        {
            anchorKeys.Add(key);
        }

        foreach (long key in controllerPosById.Values)
        {
            anchorKeys.Add(key);
        }

        foreach (long key in sourceOwnerByPos.Keys)
        {
            anchorKeys.Add(key);
        }

        return anchorKeys;
    }

    private void TryCollectManagedComponent(BlockPos pos, HashSet<long> allWaterKeys)
    {
        long posKey = ArchimedesPosKey.Pack(pos);
        if (allWaterKeys.Contains(posKey))
        {
            return;
        }

        Block fluid = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (!IsArchimedesWaterBlock(fluid))
        {
            return;
        }

        CollectConnectedManagedWater(pos, out Dictionary<long, BlockPos> connectedWater);
        foreach (long key in connectedWater.Keys)
        {
            allWaterKeys.Add(key);
        }
    }
}
