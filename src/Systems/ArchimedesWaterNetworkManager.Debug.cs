using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ArchimedesScrew;

public sealed partial class ArchimedesWaterNetworkManager
{
    public IReadOnlyList<ManagedSourceDebugInfo> CollectManagedSourceDebug(BlockPos center, int radius)
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("water.debug.collectManagedSources");
        int clampedRadius = Math.Clamp(radius, 1, 128);
        var result = new List<ManagedSourceDebugInfo>();
        int visitedCells = 0;

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
                    visitedCells++;
                    BlockPos pos = new(x, y, z);
                    Block fluid = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
                    bool isDebugSource =
                        IsArchimedesSelfSustainingSourceBlock(fluid) ||
                        IsArchimedesRelayFlowCandidate(fluid);
                    if (!isDebugSource)
                    {
                        continue;
                    }

                    long key = ArchimedesPosKey.Pack(pos);
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
                    bool isHeight7Source = IsArchimedesSourceBlock(fluid);
                    result.Add(new ManagedSourceDebugInfo(
                        pos.Copy(),
                        isOwned,
                        ownerId ?? string.Empty,
                        isOwnershipConsistent,
                        ownerSnapshotContainsPos,
                        ownerControllerLoaded,
                        ownerLoadedControllerTracksPos,
                        isHeight7Source
                    ));
                }
            }
        }

        ArchimedesPerf.AddCount("water.debug.collectManagedSources.visitedCells", visitedCells);
        ArchimedesPerf.AddCount("water.debug.collectManagedSources.matches", result.Count);
        return result;
    }

    public IReadOnlyList<BlockPos> CollectRelayCandidateDebug(BlockPos center, int radius)
    {
        using ArchimedesPerf.PerfScope _perf = ArchimedesPerf.Measure("water.debug.collectRelayCandidates");
        int clampedRadius = Math.Clamp(radius, 1, 128);
        var result = new List<BlockPos>();
        HashSet<long> seen = new();
        int visitedCells = 0;

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
                    visitedCells++;
                    BlockPos pos = new(x, y, z);
                    if (!ArchimedesRelayCandidateRules.IsPromotableRelayCandidate(api.World, pos, this))
                    {
                        continue;
                    }

                    long key = ArchimedesPosKey.Pack(pos);
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    result.Add(pos.Copy());
                }
            }
        }

        ArchimedesPerf.AddCount("water.debug.collectRelayCandidates.visitedCells", visitedCells);
        ArchimedesPerf.AddCount("water.debug.collectRelayCandidates.matches", result.Count);
        return result;
    }
}
