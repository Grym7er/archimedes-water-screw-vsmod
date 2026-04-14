# Pipes

## Overview

Add lead pipes to create water transporation networks and multiblock water containment structures:

1. Simplified network pressure (not in this update)
  - Perform a simplified conversion/calculation to check how we can convert wind power to a rough pressure
    - Pressure needs to be maintained to move water up.
    - You need X pressure to move water upwards Y blocks
    - Tie this in with the current water transport system.
    - Let pressure function as a currency: Start at pressure X, every pipe block away from the pipe the pressure decays, either linearly or whatever.
    - Gravity creates pressure based on height above sea level.
2. Lead pipes:
  - Create pipe network
    - Pipes can transport water at all angles.
    - Relies on pressure to move water
    - taps: can be used to fill buckets and other containers by placing them underneath the tap or something
3. Reservoir:
  - Multiblock Structure
    - Volume is as in IRL/10, i.e 100L per 1 cubic meter (otherwise OP)
    - Can be fed with pipes
    - Can be used as a water battery for when wind is low

# Next update:

1. Reservoirs
2. Taps

## Reservoirs:

1. Multiblock structure.
2. User can construct a dynamically sized structure, as long as the reservoir container is valid:
  - specific block types
    - maximum size
    - Requires output valve.
3. Fills up with archimedes source blocks

### Output Valve:

Allows gravity-fed pipes. Connect to a reservoir and open, works like an archimedes screw, but displaces water, i.e takes from input and moves to output, Uses relays, coupled to input sources

# Fill up plan:

1. When no more relays have been available for N propagation ticks, but before becoming idle, search for valid fill-up-relay-positions
2. fill-up-relay-position rules:
  - block needs to be archimedes still level 7 managed owned source
    - block above needs to be air
    - at least 1 block that is above and off to one of the cardinal directions needs to be a solid block
3. At the same rate as relay propagation, find positions attached to the managed water network that satisfies the above, but do not spawn water. Only find the positions, and mark them/keep track of them.
4. When using the debug command, make those positions dark blue.
5. Once no more of those have been found for N propagation ticks:
6. At the same rate as relay propagation, spawn level-7 managed archimedes sources in those positions.
7. These relays should obey the same draining rules as standard relays (they are standard relays, for all intents and purposes).

# Suggested Fill Plan

## Fill Rules (recommended)

1. Keep one relay system, but add a second candidate strategy: fill-frontier candidates.
2. A fill-frontier candidate starts from an owned managed source (still-7) at `basePos`, and proposes `targetPos = basePos.UpCopy()`.
3. Candidate validity checks:
  - `basePos` fluid is managed still-7 and is owned by this controller.
  - `targetPos` host-cell check (practical replaceability test):
    - Solid layer at `targetPos` must be air (`Id == 0`) or fluid-passable (`ForFluidsLayer == true`).
    - Fluid layer at `targetPos` must be empty (`Id == 0`) before spawn.
    - This prevents assigning ownership to an occupied/blocked cell that cannot hold new managed fluid.
  - `targetPos` must be in/adjacent to this controller's connected managed component this tick (same family/component context as relay logic).
  - `targetPos` must not already be owned by another controller.
  - Mini-room validator (inspired by RoomRegistry BFS, but localized and cheaper for relay cadence):
    - Add config `reservoirMaxVolume` (recommended range ~32-256, hard clamp maybe 1024).
    - Run a bounded flood fill from `targetPos` through passable cells (`air` + fluid-passable solids + optional empty fluid cells).
    - Track visited count only; stop early when:
      - flood queue empties -> enclosed/limited pocket, candidate can pass this rule.
      - visited count exceeds `reservoirMaxVolume` -> treat as open/too-large cavity, reject candidate.
    - Use this as the primary "is this a suitable cavity/room pocket?" rule for fill.
    - Keep this validator volume-only for now (no cooling-wall/skylight scoring), to match practical fill behavior and low tick cost.
4. Optional safety checks to avoid open-world overfill:
  - reject if no solid exists within `N` blocks above `targetPos` (open sky guard).
    - reject if local cavity scan exceeds configured max volume/depth.
5. Do not spawn during discovery. Discovery only builds a persistent queue/set.
6. Spawn phase consumes discovered positions with the same per-tick relay budget and same ownership path (`AssignOwnedSourceForController`).
7. Spawn must follow the existing race-safe order: place managed still-7 without immediate updates, assign ownership, then trigger liquid updates.
8. Fill-spawned sources are standard relays for draining, trimming, and ownership.

## State Machine (controller side)

1. `NormalRelayExpansion`
  - Current behavior: expand normal relay candidates while work exists.
    - Transition to `FillDiscovering` when no normal relay created for `IdleProbeTicksThreshold` fast ticks, but relay expansion is still desired.
2. `FillDiscovering`
  - Each relay cadence tick, scan connected managed component for new fill candidates and enqueue deduplicated targets.
    - Track whether this tick found new candidates.
    - Transition to `FillSpawning` after `FillDiscoverStallTicksThreshold` consecutive discover ticks with zero new candidates, if queue not empty.
    - Transition back to `NormalRelayExpansion` immediately if normal relay opportunities reappear.
3. `FillSpawning`
  - Consume queued candidates at `min(MaxRelayPromotionsPerTick, remaining relay cap)` per tick.
    - Validate each candidate again before spawn (world may have changed).
    - Transition to `FillDiscovering` if queue empties but fill mode remains enabled.
    - Transition to `NormalRelayExpansion` if normal relay opportunities resume.
4. `SteadyIdle`
  - Enter when no normal relay work, no fill discovery progress, and no fill queue work.
    - Existing low-cadence controller scheduling applies.
5. Global pause rule:
  - If connected managed BFS is truncated, pause discovery/spawn for that tick (same as current relay automation guard).

## Data Model (persisted in BE + runtime caches)

1. Persisted fields on `BlockEntityWaterArchimedesScrew`:
  - `fillState` enum: `NormalRelayExpansion`, `FillDiscovering`, `FillSpawning`, `SteadyIdle`
    - `fillIdleProbeTicks` (int)
    - `fillDiscoverStallTicks` (int)
    - `fillCandidateQueue` (encoded ordered list of `BlockPos`)
    - `fillCandidateSet` (or reconstruct set from queue on load for dedupe)
2. Runtime-only fields:
  - `lastFillDiscoveryTickMs`
    - `fillRejectedRecentlyByReason` counters (debug/perf visibility)
3. Config additions:
  - `EnableFillFrontier` (bool)
    - `FillIdleProbeTicksThreshold` (int)
    - `FillDiscoverStallTicksThreshold` (int)
    - `MaxFillPromotionsPerTick` (int, default to relay promotion budget if absent)
    - optional guards: `FillMaxSkyProbe`, `FillMaxCavityVolume`, `FillMaxCavityDepth`
4. Debug model additions:
  - Add `FillCandidates` list to debug snapshot packet.
    - Overlay color for fill candidates: dark blue.
    - Optional chat scan summary: queued, discovered this tick, spawned this tick, rejected by reason.

## Integration Notes

1. Keep fill logic inside relay maintenance path so ownership, cap, and cadence remain unified.
2. Reuse manager APIs (`AssignOwnedSourceForController`, ownership snapshot updates, drain rules) instead of a separate fill-specific ownership path.
3. Make all transitions deterministic and monotonic per tick to avoid controller thrash in multi-controller connected components.

