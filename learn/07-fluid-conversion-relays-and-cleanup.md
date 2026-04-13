# Chapter 07: Fluid Conversion, Relays, and Cleanup

This chapter covers the water simulation strategy that makes the screw feel reliable in gameplay.

## Managed vs Vanilla Water

The mod distinguishes:

- vanilla water blocks (game-owned behavior),
- managed Archimedes water blocks (mod-owned behavior).

`ArchimedesWaterFamilies` maps fresh/salt/boiling families so conversions keep fluid type consistent.

Family resolution is used throughout manager methods, including:

- `TryResolveVanillaWaterFamily(...)` lines 507-517,
- `TryResolveManagedWaterFamily(...)` lines 519-529,
- intake resolution helper `TryResolveIntakeWaterFamily(...)` lines 531-546.

## Conversion Pipeline

Key flow in manager methods:

1. ensure outlet source exists as managed water,
2. collect connected managed component (BFS),
3. convert adjacent vanilla sources that satisfy neighbor rules,
4. repeat conversion passes (`MaxVanillaConversionPasses`) for source chains.

Concrete flow to trace:

- iterative conversion entry: `ConvertAdjacentVanillaSourcesIteratively(...)` lines 971-987,
- one pass: `ConvertAdjacentVanillaSources(...)` lines 918-964,
- source conversion rule: `TryConvertVanillaSource(...)` lines 989-1028.

## Important Race Condition Handling

In conversion methods, the mod sets managed source first with updates disabled, assigns ownership, then triggers liquid updates.

Why:

- avoids immediate source collapse before ownership metadata is written.

This pattern is explicitly documented in code comments and is worth copying.

See the critical order in `TryConvertVanillaSource(...)`:

1. `SetManagedWaterVariant(..., triggerUpdates: false)` (line 1004),
2. assign owner (lines 1006-1019),
3. trigger liquid updates (lines 1021-1025).

## Relay Sources (Long-Distance Support)

Relay logic is configurable and allows long aqueduct-like flows:

- spacing via `RelayStrideBlocks`,
- cap via `MaxRelaySourcesPerController`,
- scaling with mechanical power (`RequiredMechPowerForMaxRelay`),
- anti-thrash via hysteresis (`RelayPowerHysteresisPct`).

These tunables are defined in `src/Config/ArchimedesScrewConfig.cs` lines 45-71 and mirrored in `assets/archimedes_screw/config/settings.json`.

## Cleanup Paths

The system removes:

- orphaned managed sources with no owner,
- sources released by controllers,
- stale cells after structure/power loss.

Cleanup is budgeted and uses cooldowns for retry to avoid expensive thrashing.

Relevant methods:

- unowned cleanup around anchors: `CleanupUnownedManagedSourcesAroundAnchors(...)` lines 853-916,
- orphan fallback removal: `RemoveOrphanedManagedSource(...)` lines 1318-1328,
- fluid removal notification path: `OnManagedWaterRemoved(...)` lines 1095-1114.

## Player Interaction Hooks

`BlockArchimedesWater` subclasses notify manager on removal and attempt smart adjacent conversion for player-placed sources.  
This keeps ownership and fluid state coherent when players edit active water networks.

Open `src/Blocks/BlockArchimedesWater.cs` and trace:

- neighbor-triggered conversion in `TryConvertNeighbourSource(...)` lines 40-64,
- removal-to-manager notification in `NotifyManagerOnRemoval(...)` lines 9-38.

Next: persistence, config live-tuning, and compatibility.
