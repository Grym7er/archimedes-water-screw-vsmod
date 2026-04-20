# Server performance audit: `archimedes_screw`

This document records a **read-only** performance review of the mod’s **server-side** hot paths. All items below aim at **lower CPU, fewer allocations, and less GC pressure** while **preserving current gameplay and persistence semantics**.

**Scope:** central water tick, per-controller ticks, ownership/snapshot updates, BFS/connectivity-style scans, relay maintenance, optional profiling, and debug overlay traffic.

**Method:** static review of frequently executed code paths, allocation patterns (strings, LINQ, `BlockPos` copies), and algorithmic complexity on large connected components or many active controllers.

---

## Executive summary

The largest wins come from:

1. **Replacing string position keys** (`PosKey` / `TryParsePosKey`) with **value-typed keys** (e.g. packed `long` or a small `readonly struct` of three `int`s) in hot `Dictionary` / `HashSet` usage, and from **allocation-free parsing** where keys must remain strings at boundaries (save/load, logging).
2. **Reducing per-tick work inside `OnWaterControllerTick`** by reusing connectivity/component results across sub-phases in the same tick where safe.
3. **Avoiding full decode–rebuild–encode cycles** on ownership snapshots for every small mutation; batch or keep a mutable runtime model and serialize on save (or on explicit dirty batches).
4. **Replacing “sort everything to pick one”** LINQ chains with **single-pass selection** where ordering rules are deterministic and equivalent.

Optional debug and profiling paths can dominate a server if left enabled; treat them as **operational** performance risks, not just dev tools.

---

## Severity legend

| Level | Meaning |
|-------|---------|
| **High** | Likely material impact on busy servers (many controllers, large water components, frequent ticks). |
| **Medium** | Noticeable under load or misconfiguration; good ROI. |
| **Low** | Minor or rare; fix when touching related code. |

---

## High priority

### 1. String position keys and parsing (`PosKey`, `TryParsePosKey`)

**Where**

- `src/Systems/ArchimedesWaterNetworkManager.cs` — `PosKey`, `TryParsePosKey`, and all call sites using `Dictionary<string, …>`, `HashSet<string>` for block identities.
- `src/BlockEntities/BlockEntityWaterArchimedesScrew.cs` — BFS, ownership maps keyed by string, relay ordering keyed by string.

**Issue**

- Every `PosKey` constructs a **new string** (interpolation + allocation).
- `TryParsePosKey` uses `string.Split(',')`, which **allocates an array** per parse.
- These run inside **tight loops** (BFS neighbors, relay candidate walks, ownership checks), causing **GC churn** and extra hashing cost vs. integer keys.

**Proposed improvement (no intended behavior change)**

- Introduce an internal **exact, discrete** key type for hot structures, e.g.:
  - **Packed `long`** if world bounds allow a known-safe packing; or
  - **`readonly struct`** with `(int X, int Y, int Z)` and correct `Equals`/`GetHashCode`.
- Keep **string keys only at boundaries** that must remain human-readable or compatible (save blobs, logs, admin commands), converting once when crossing that boundary.
- Replace `Split`-based parsing with **span/manual parse** for any remaining string keys in warm paths.

**Note on `Vec3d`:** block identity is **integer grid** data. **`Vec3d` (floating-point) is a poor fit** for dictionary keys and for exact cell identity; prefer integer packing or an integer triple struct.

---

### 2. `BlockPos` allocations in BFS and neighbor expansion

**Where**

- `src/BlockEntities/BlockEntityWaterArchimedesScrew.cs` — e.g. `BuildDistanceMap`: `Queue<BlockPos>`, `current.AddCopy(face)`, enqueue copies.
- `src/Systems/ArchimedesWaterNetworkManager.cs` — similar `AddCopy` / queue patterns in connectivity-style walks.

**Issue**

- `AddCopy` / `Copy` in **inner loops** allocate **many short-lived `BlockPos` objects** per tick per large component.

**Proposed improvement**

- Use **integer coordinate queues** for BFS internals, or **reuse a small pool** of mutable scratch positions where the API allows (careful with reentrancy and API thread rules).
- Defer `BlockPos` materialization to **API calls that require `BlockPos`** (e.g. `GetBlock`), not for every dictionary step.

---

### 3. Snapshot encode/decode on every ownership mutation

**Where**

- `src/Systems/ArchimedesWaterNetworkManager.cs` — `AddOwnedPosToSnapshot`, `RemoveOwnedPosFromSnapshot` (decode → mutate → encode full `int[]` snapshot).
- `src/Systems/ArchimedesWaterNetworkManager.Ownership.cs` — `ReplaceSourceOwnershipForController` replaces full encoded snapshot and scans `sourceOwnerByPos` for stale keys.

**Issue**

- Each add/remove can **decode the entire controller list** and **re-encode** it.
- Under churn (many sources/relays), cost trends toward **O(changes × snapshot size)** and increases allocations (intermediate lists, LINQ in remove path).

**Proposed improvement**

- Maintain a **mutable per-controller set/list in memory** (e.g. `HashSet` of packed keys + optional ordered list if needed for determinism).
- **Serialize to `ArchimedesPositionCodec` format only** on world save, mod blob write, or **batched** “dirty snapshot” flush (preserving identical on-disk semantics).
- For `RemoveOwnedPosFromSnapshot`, avoid LINQ chains that rebuild lists; use a single pass copy-remove.

---

### 4. Controller tick composes multiple heavy phases per activation

**Where**

- `src/BlockEntities/BlockEntityWaterArchimedesScrew.cs` — `OnWaterControllerTick`: connectivity collection, vanilla conversion, seizure pass, relay maintenance, drain pass, optional snapshot update.

**Issue**

- A single tick can trigger **several graph walks and scans**. With many controllers or large components, server time stacks even when each sub-step is “correct.”

**Proposed improvement**

- **Reuse** `CollectConnectedManagedWaterCachedDetailed` results (and derived structures) across **all sub-steps in the same tick** where ordering and side effects allow.
- Tighten **per-phase budgets** (already partially present via config); ensure budgets apply consistently so one phase cannot starve others without changing overall outcomes.
- Document invariants so future changes do not accidentally **re-query** the world for the same component twice per tick.

---

## Medium priority

### 5. LINQ sorting and grouping in near-hot paths

**Where**

- `src/BlockEntities/BlockEntityWaterArchimedesScrew.cs` — `OrderRelayPromotionCandidates`, `OrderRelayPromotionCandidatesRandomWithinDistanceBucket` (`OrderBy`, `GroupBy`, `ToList`, shuffles).
- `src/BlockEntities/BlockEntityWaterArchimedesScrew.cs` — `TrimRelaySourcesToCap`: `OrderByDescending` + `Take` + `ToList`.

**Issue**

- Full sorts and `GroupBy` allocate and sort **more than needed** when only **top-k** or **ordered iteration** within buckets is required.

**Proposed improvement**

- For relay trim: select **farthest `removeCount`** with a **partial selection** or one-pass bucket strategy matching current tie-break rules.
- For deterministic relay ordering: **stable bucket sort by distance** (small integer distances) or sort keys in a pooled array instead of LINQ over `KeyValuePair`.

---

### 6. `FindNearestActiveControllerId` sorts all candidates to return one

**Where**

- `src/Systems/ArchimedesWaterNetworkManager.cs` — `FindNearestActiveControllerId`: builds `List<ArchimedesOutletState>` then `OrderBy`…`FirstOrDefault`.

**Issue**

- **O(n log n)** sort + allocations for **O(n)** scan that only needs the **minimum** under a known comparator.

**Proposed improvement**

- Single-pass **argmin** over `candidates` with the **same comparator** as the current `OrderBy` chain (distance², then Y, X, Z, then controller id).
- Optionally avoid building a full candidate list if seeds can be filtered cheaply first (measure before complicating).

---

### 7. Profiler lock contention when profiling is enabled

**Where**

- `src/Systems/ArchimedesPerf.cs` — global `lock` in `AddCount`, `EndMeasure`, `MaybeFlush`.

**Issue**

- When `ArchimedesPerf.Enabled` is true, **every measured hot path** contends on one lock, which can **distort timings** and add overhead under parallel work (if any exists on server threads touching these paths).

**Proposed improvement**

- **Thread-local accumulators** flushed periodically, or coarse per-metric `Interlocked` updates where correctness allows.
- Keep default **disabled** in release builds; document that profiling can skew hot paths.

---

### 8. Water debug overlay: scan volume and network fan-out

**Where**

- `src/ModSystem/ArchimedesScrewModSystem.cs` — `EnsureWaterDebugTickListener`, `SendWaterDebugSnapshotToAllPlayers` (500 ms tick), `WaterDebugRadius` (32 → large cube per player).
- `src/Systems/ArchimedesWaterNetworkManager.Debug.cs` — `CollectManagedSourceDebug`, `CollectRelayCandidateDebug` (triple nested loops, `new BlockPos` per cell, `PosKey` per hit).

**Issue**

- **Per-player** full cube scans and **full snapshot packets** every 500 ms can dominate CPU and bandwidth if debug is left on with multiple players.

**Proposed improvement**

- **Throttle** by online player count, lower default cadence/radius, or **cap cells examined per tick** with round-robin across the cube.
- Send **deltas** (added/removed positions) instead of full lists if the protocol can be extended without breaking clients (or version the packet).

---

### 9. `ReplaceSourceOwnershipForController` full-map scan for stale keys

**Where**

- `src/Systems/ArchimedesWaterNetworkManager.Ownership.cs` — iterates all `sourceOwnerByPos` entries to find keys owned by controller but not in new snapshot.

**Issue**

- **O(global ownership map)** per controller snapshot update. With a very large `sourceOwnerByPos`, this is costly during mass updates.

**Proposed improvement**

- Maintain a **reverse index**: `controllerId → HashSet<positionKey>` (or packed keys) updated on assign/release, so stale cleanup is **O(controller’s footprint)** instead of **O(all sources)**.
- Must stay consistent with all code paths that mutate ownership.

---

## Low priority

### 10. `UnregisterFromCentralWaterTick` uses `List.RemoveAll`

**Where**

- `src/Systems/ArchimedesWaterNetworkManager.cs` — `centralWaterTickOrder.RemoveAll(...)`.

**Issue**

- **O(n)** per removal; usually infrequent (unload/remove). Minor unless churn is extreme.

**Proposed improvement**

- Swap-remove with **index map** (`Dictionary<string,int>`) if profiling shows this matters.

---

### 11. Dictionary key snapshots for pruning (`Keys.ToList()`)

**Where**

- `src/BlockEntities/BlockEntityWaterArchimedesScrew.cs` — e.g. `PruneExpiredLocalCooldowns`, `ReconcileRelayOwnedPositions` iterate `Keys.ToList()`.

**Issue**

- Allocates a **temporary list of keys** to allow removal during enumeration. Usually small; cost scales with map size.

**Proposed improvement**

- Collect expired keys in a **reused scratch list**, or use **reverse iteration** patterns that do not allocate if collection semantics allow.

---

### 12. ConfigLib event bus listeners not unsubscribed in `Dispose`

**Where**

- `src/ModSystem/ArchimedesScrewModSystem.cs` — `RegisterEventBusListener` for Config Lib; comment notes API may lack unregister.

**Issue**

- If mod systems are **recreated** without full process restart, **duplicate handlers** can fire, doubling work and log noise.

**Proposed improvement**

- If the game API exposes unregister for event bus listeners, **unregister in `Dispose`**.
- Otherwise guard with an **instance generation id** or static “registered once” gate compatible with Vintage Story lifecycle.

---

## Quick wins vs deeper refactors

### Quick wins (smaller change surface, good ROI)

- Allocation-free **`TryParsePosKey`** (no `Split`).
- **`FindNearestActiveControllerId`**: replace full sort with **single-pass min**.
- **Relay trim / partial ordering**: avoid full `OrderBy` when only **top-k** removal is needed.
- **Debug mode**: throttle scans and packet size when multiple players are online.

### Deeper refactors (largest upside, more risk and testing)

- Replace **string-keyed** spatial identity across manager + block entity with **packed keys** or a dedicated struct + comparer; keep strings only at persistence and command boundaries.
- **Mutable snapshot model** with deferred encoding to disk/mod blob.
- **Single graph pass per controller tick** with shared intermediate structures across sub-phases.

---

## Assumptions and validation

- Actual hotness depends on **world size**, **number of active Archimedes controllers**, **relay settings**, and tick intervals (`FastTickMs`, `IdleTickMs`, `GlobalTickMs`, `MaxControllersPerGlobalTick`, etc.).
- **Debug/profiling** paths are negligible when disabled; they become dominant when enabled on production-like servers.
- Any optimization must preserve:
  - **Deterministic ordering** where the mod currently defines tie-breaks.
  - **Save-game compatibility** for serialized keys and blobs.
  - **Ownership semantics** described in existing design docs under `learn/`.

**Recommended validation:** run a dedicated server with `ArchimedesPerf.Enabled = true` (short sessions) plus GC/CPU sampling, under scenarios with (a) one large connected managed lake, (b) many small controllers, (c) relay promotion enabled at high cap.

---

## Related files (non-exhaustive)

| Area | Files |
|------|--------|
| Global tick, caches, ownership helpers | `src/Systems/ArchimedesWaterNetworkManager.cs` |
| Ownership snapshot alignment | `src/Systems/ArchimedesWaterNetworkManager.Ownership.cs` |
| Controller tick, relay, distance map | `src/BlockEntities/BlockEntityWaterArchimedesScrew.cs` |
| Debug scans | `src/Systems/ArchimedesWaterNetworkManager.Debug.cs` |
| Debug packets / tick listener | `src/ModSystem/ArchimedesScrewModSystem.cs` |
| Opt-in profiler | `src/Systems/ArchimedesPerf.cs` |
| Barrier checks (per neighbor) | `src/Util/ArchimedesFluidHostValidator.cs` |

---

*Generated from an internal performance review of this repository. Update this document when implementations land or when measured profiling contradicts static estimates.*
