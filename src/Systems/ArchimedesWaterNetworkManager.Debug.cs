using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ArchimedesScrew;

public sealed partial class ArchimedesWaterNetworkManager
{
    public IReadOnlyList<ManagedSourceDebugInfo> CollectManagedSourceDebug(BlockPos center, int radius)
    {
        int clampedRadius = Math.Clamp(radius, 1, 128);
        var result = new List<ManagedSourceDebugInfo>();

        int minX = center.X - clampedRadius;
        int maxX = center.X + clampedRadius;
        int minY = center.Y - clampedRadius;
        int maxY = center.Y + clampedRadius;
        int minZ = center.Z - clampedRadius;
        int maxZ = center.Z + clampedRadius;

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    BlockPos pos = new(x, y, z);
                    Block fluid = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
                    if (!IsArchimedesSelfSustainingSourceBlock(fluid))
                    {
                        continue;
                    }

                    string key = PosKey(pos);
                    bool isOwned = sourceOwnerByPos.TryGetValue(key, out string? ownerId);
                    bool ownerSnapshotContainsPos = false;
                    bool ownerControllerLoaded = false;
                    bool ownerLoadedControllerTracksPos = false;
                    if (isOwned && ownerId != null)
                    {
                        ownerSnapshotContainsPos = ControllerSnapshotContainsPos(ownerId, key);
                        ownerControllerLoaded = loadedControllers.TryGetValue(ownerId, out WeakReference<BlockEntityWaterArchimedesScrew>? wr) &&
                                                wr.TryGetTarget(out _);
                        ownerLoadedControllerTracksPos = IsLoadedControllerTrackingPos(ownerId, pos);
                    }

                    bool isOwnershipConsistent = isOwned &&
                                                 ownerSnapshotContainsPos &&
                                                 (!ownerControllerLoaded || ownerLoadedControllerTracksPos);
                    result.Add(new ManagedSourceDebugInfo(
                        pos.Copy(),
                        isOwned,
                        ownerId ?? string.Empty,
                        isOwnershipConsistent,
                        ownerSnapshotContainsPos,
                        ownerControllerLoaded,
                        ownerLoadedControllerTracksPos
                    ));
                }
            }
        }

        return result;
    }

    public IReadOnlyList<BlockPos> CollectRelayCandidateDebug(BlockPos center, int radius)
    {
        int clampedRadius = Math.Clamp(radius, 1, 128);
        var result = new List<BlockPos>();
        HashSet<string> seen = new(StringComparer.Ordinal);

        int minX = center.X - clampedRadius;
        int maxX = center.X + clampedRadius;
        int minY = center.Y - clampedRadius;
        int maxY = center.Y + clampedRadius;
        int minZ = center.Z - clampedRadius;
        int maxZ = center.Z + clampedRadius;

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    BlockPos pos = new(x, y, z);
                    Block fluid = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
                    if (!IsArchimedesLowestFlowingBlock(fluid) ||
                        !ArchimedesRelayAdjacency.IsRelaySupportAndAdjacentWhitelistSatisfied(api.World, pos))
                    {
                        continue;
                    }

                    string key = PosKey(pos);
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    result.Add(pos.Copy());
                }
            }
        }

        return result;
    }
}
