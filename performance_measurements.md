# Packed Position Key and Traversal Allocation Notes

## Scope

This note captures lightweight verification after migrating hot-path runtime position keys to packed `long` keys and reducing traversal `BlockPos` allocations for Improvement #2.

## Verification run

- Command: `dotnet build`
- Result: build succeeded for all target frameworks, `0` warnings / `0` errors.

## Migration evidence (static counters)

- Remaining `PosKey(...)` usages in `src/`: `3`
  - `ArchimedesWaterNetworkManager.PosKey` method itself
  - `ArchimedesWaterNetworkManager.TryParsePosKey` boundary parser
  - one ownership log formatting call in `ArchimedesWaterNetworkManager.Ownership.cs`
- Packed-key usage in systems (`ArchimedesPosKey.Pack`, `Dictionary<long,...>`, `HashSet<long,...>`): broadly adopted across manager, policy, purge, ownership, and debug paths.
- Traversal safety/optimization updates:
  - `CollectConnectedManagedWaterDetailed` now uses `Queue<long>` with reusable scratch `BlockPos` and bounds-safe `TryPack`.
  - `BuildDistanceMap` now uses `Queue<long>` with reusable scratch `BlockPos` and bounds-safe `TryPack`.
  - `SeizeVanillaSourcesInConnectedFamilyFluid` enqueue/dequeue flow now uses packed keys and bounds-safe `TryPack`.
  - Secondary neighbor loops (`ConvertAdjacentVanillaSources`, `HasAtLeastTwoOwnedManagedCardinalSourceNeighbors`, `NotifyNeighboursOfFluidRemoval`, `TriggerLiquidUpdates`, and `CountManagedSourceHeightCardinalNeighbors`) now reuse scratch positions instead of per-iteration `AddCopy`.

## Notes

- This repository does not include a separate test project; runtime safety checks were added in `ArchimedesPosKey.InitializeForWorld()` to validate pack/unpack bijection on initialization boundaries.
- Hot-path functional verification currently relies on successful compile, deterministic comparator preservation, and explicit world-bounds guards in traversal pack paths.
- Performance delta is assessed structurally in this pass (allocation-site reduction), with runtime GC/tick profiling still recommended on representative worlds for numeric before/after metrics.
