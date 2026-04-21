using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.MathTools;

namespace ArchimedesScrew;

public sealed partial class ArchimedesWaterNetworkManager
{
    public void RegisterRestoredOwnership(string controllerId, BlockPos controllerPos, IReadOnlyCollection<BlockPos> sourcePositions)
    {
        ReplaceSourceOwnershipForController(controllerId, controllerPos, sourcePositions, out int removedStale);
        api.Logger.Debug(
            "{0} RegisterRestoredOwnership controller={1} blockPos={2} sources={3} removedStaleSourceOwnerKeys={4}",
            ArchimedesScrewModSystem.LogPrefix,
            controllerId,
            PosKey(controllerPos),
            sourcePositions.Count,
            removedStale
        );
    }

    public void UpdateControllerSnapshot(string controllerId, BlockPos controllerPos, IReadOnlyCollection<BlockPos> sourcePositions)
    {
        ReplaceSourceOwnershipForController(controllerId, controllerPos, sourcePositions, out int removedStale);
        if (removedStale > 0)
        {
            api.Logger.Debug(
                "{0} UpdateControllerSnapshot controller={1} removed {2} stale sourceOwnerByPos entr(y/ies) not in BE snapshot",
                ArchimedesScrewModSystem.LogPrefix,
                controllerId,
                removedStale
            );
        }
    }

    /// <summary>
    /// After <see cref="Load"/>, re-apply ownership from block entities that initialized before <c>SaveGameLoaded</c>
    /// (so their <see cref="RegisterRestoredOwnership"/> was wiped by the clear + mod blob merge).
    /// </summary>
    public void ReapplyOwnershipFromLoadedControllers()
    {
        int deadRefs = 0;
        int reappliedControllers = 0;
        int reappliedSources = 0;
        List<string> samples = new();

        foreach (WeakReference<BlockEntityWaterArchimedesScrew> wr in loadedControllers.Values.ToList())
        {
            if (!wr.TryGetTarget(out BlockEntityWaterArchimedesScrew? be))
            {
                deadRefs++;
                continue;
            }

            if (!be.TryReapplyStoredOwnershipToWaterManager(out int sourceCount))
            {
                continue;
            }

            reappliedControllers++;
            reappliedSources += sourceCount;
            if (samples.Count < 4)
            {
                samples.Add($"{be.ControllerId}:{sourceCount}");
            }
        }

        if (reappliedControllers > 0)
        {
            api.Logger.Notification(
                "{0} Post-Load reapply from already-loaded block entities: controllersWithChunkOwnership={1}, totalSourcesReapplied={2}, deadWeakRefsSkipped={3}, sample controllerId:counts=[{4}]",
                ArchimedesScrewModSystem.LogPrefix,
                reappliedControllers,
                reappliedSources,
                deadRefs,
                samples.Count > 0 ? string.Join(", ", samples) : "\u2014"
            );
        }
        else if (deadRefs > 0)
        {
            api.Logger.Debug(
                "{0} Post-Load reapply: no chunk ownership merged; skipped {1} dead controller weak ref(s)",
                ArchimedesScrewModSystem.LogPrefix,
                deadRefs
            );
        }
        else
        {
            api.Logger.Debug(
                "{0} Post-Load reapply: no early-initialized controllers with chunk ownership (normal if chunks load after SaveGameLoaded)",
                ArchimedesScrewModSystem.LogPrefix
            );
        }
    }

    /// <summary>
    /// Sets controller snapshot and aligns <see cref="sourceOwnerByPos"/> so stale cells are not left pointing at this controller.
    /// </summary>
    private void ReplaceSourceOwnershipForController(
        string controllerId,
        BlockPos controllerPos,
        IReadOnlyCollection<BlockPos> sourcePositions,
        out int removedStaleSourceOwnerKeys)
    {
        controllerPosById[controllerId] = ArchimedesPosKey.Pack(controllerPos);
        controllerOwnedById[controllerId] = ArchimedesPositionCodec.EncodePositions(sourcePositions);

        HashSet<long> newKeys = new();
        foreach (BlockPos pos in sourcePositions)
        {
            newKeys.Add(ArchimedesPosKey.Pack(pos));
        }

        removedStaleSourceOwnerKeys = 0;
        if (ownedKeysByController.TryGetValue(controllerId, out HashSet<long>? existingKeys))
        {
            List<long> toRemove = replaceOwnershipStaleScratch;
            toRemove.Clear();
            foreach (long key in existingKeys)
            {
                if (!newKeys.Contains(key))
                {
                    toRemove.Add(key);
                }
            }

            removedStaleSourceOwnerKeys = toRemove.Count;
            foreach (long key in toRemove)
            {
                sourceOwnerByPos.Remove(key);
                existingKeys.Remove(key);
            }

            toRemove.Clear();
        }

        foreach (long key in newKeys)
        {
            AssignSourceOwnerInternal(key, controllerId);
            if (!sourceProvenanceByPos.ContainsKey(key))
            {
                sourceProvenanceByPos[key] = ManagedSourceProvenance.ControllerSeedOrRelay;
            }
        }
    }

    public void RemoveControllerSnapshot(string controllerId)
    {
        controllerPosById.Remove(controllerId);
        controllerOwnedById.Remove(controllerId);
        loadedControllers.Remove(controllerId);
        UnregisterFromCentralWaterTick(controllerId);

        if (controllerRelaySourceKeys.Remove(controllerId, out HashSet<long>? removedRelayKeys))
        {
            foreach (long relayKey in removedRelayKeys)
            {
                if (relayOwnerByPos.TryGetValue(relayKey, out string? owner) &&
                    string.Equals(owner, controllerId, StringComparison.Ordinal))
                {
                    relayOwnerByPos.Remove(relayKey);
                }
            }
        }

        int clearedCount = 0;
        if (ownedKeysByController.Remove(controllerId, out HashSet<long>? ownedKeys))
        {
            clearedCount = ownedKeys.Count;
            foreach (long key in ownedKeys)
            {
                sourceOwnerByPos.Remove(key);
                sourceProvenanceByPos.Remove(key);
            }
        }

        if (clearedCount > 0)
        {
            api.Logger.Debug(
                "{0} RemoveControllerSnapshot: cleared {1} sourceOwnerByPos entr(y/ies) for controller={2}",
                ArchimedesScrewModSystem.LogPrefix,
                clearedCount,
                controllerId);
        }
    }
}
