# Chapter 06: Global Water Network Manager

The heart of this mod is `ArchimedesWaterNetworkManager` in `src/Systems/ArchimedesWaterNetworkManager*.cs`.

## Why a Global Manager Instead of Per-BE Tick

The mod uses one server dispatcher (`StartCentralWaterTick`) that:

- runs at configurable interval (`GlobalTickMs`),
- processes only up to `MaxControllersPerGlobalTick`,
- pulls controllers from a min-heap priority queue keyed on each
  controller's `nextCentralWaterTickDueMs`, so the most-overdue
  controller always runs first.

This gives hard performance control when many machines are loaded
without round-robin starvation: when `HardGlobalTickBudgetMs` cuts a
tick short, the controllers left unprocessed are by definition the
ones that became due most recently, so the next tick naturally picks
up the longest-waiting work first.

See:

- start tick listener in `StartCentralWaterTick`,
- queue/dispatch logic in `OnGlobalWaterTick`,
- registration in `RegisterForCentralWaterTick`,
- compaction (rebuilds the heap and drops tombstones) in
  `CompactCentralWaterTickList`.

### Lazy decrease-key

`PriorityQueue<TElement, TPriority>` does not support decrease-key, so
controllers may sit in the heap with a priority that has since been
overwritten by `ScheduleNextWaterTick` (e.g. after interaction or a
config reload). The dispatcher reconciles this lazily: each id also
records its currently-enqueued priority in
`centralWaterTickEnqueuedPriorityById`. On dequeue, if the popped
priority no longer matches that record, the entry is treated as a
tombstone and discarded; the canonical entry remains in the heap and
will be picked up later. `UnregisterFromCentralWaterTick` uses the
same pattern - it only edits the side dictionaries and lets the heap
drain its tombstones on the next dequeue or compaction pass.

## Core Responsibilities

- Controller registration/unregistration.
- Ownership map (`sourceOwnerByPos`) for managed source blocks.
- Connected-water traversal (BFS with `MaxBfsVisited` safety cap).
- Conversion and cleanup primitives.
- Save/load of manager-owned state.

Key data structures (lines 24-37):

- `sourceOwnerByPos`: source ownership authority map.
- `controllerOwnedById`: persisted ownership snapshots.
- `loadedControllers`: weak refs to active block entities.
- cache collections for per-dispatch BFS reuse.

## Ownership Model

Each managed source position maps to exactly one controller ID.

Why:

- prevents two controllers fighting over the same source,
- supports deterministic cleanup when machines unload or break,
- allows diagnostics (`owned`, `unowned`, inconsistent states).

Assignment/release flow to study:

- `EnsureSourceOwned(...)` lines 725-765,
- `ReleaseSourceOwner(...)` lines 827-851,
- `OnManagedWaterRemoved(...)` lines 1095-1114.

## Caching Strategy

During a global tick dispatch:

- connected-component BFS results are cached per generation,
- active seed states are cached too.

This avoids recomputing expensive graph scans for neighboring operations in the same tick cycle.

Cache paths:

- component cache generation bump: lines 215-217,
- cache lookup/fanout: lines 661-698,
- active seed cache reuse: lines 563-587.

Why it matters: graph traversal is one of the most expensive operations in fluid-like systems.

## Save/Load and Resilience

Manager persistence:

- `Load()` lines 289-444,
- `Save()` lines 446-460.

It validates keys, handles malformed entries safely, and resolves duplicate owner claims deterministically (lines 395-399).

This is production-minded design: corrupt or legacy data does not crash the whole mod.

## Architecture Pattern to Reuse

For your own automation mod:

1. create a global manager service,
2. have BEs register/deregister,
3. centralize expensive shared graph/state logic,
4. use budgeted dispatch and explicit caps.

Add these two guardrails from this project:

- hard BFS cap (`MaxBfsVisited`, line 18),
- periodic compaction of weak references (lines 269-287).

Next: how fluid conversion, relay sources, and cleanup are implemented.

## Updated Boundary Model (Global + Local)

The manager remains authoritative for world truth and persistence, while controllers now own more transient decision logic.

Global manager remains responsible for:

- ownership authority and snapshots,
- deterministic owner conflict resolution,
- BFS traversal, caching, and global tick budgets,
- save/load and post-load reactivation.

Controller-local decision logic now includes:

- relay promotion ordering,
- unsupported-source release ordering,
- local anti-oscillation cooldown gating for candidate reuse.

This split keeps correctness centralized while making mechanic-specific behavior easier to extend.
