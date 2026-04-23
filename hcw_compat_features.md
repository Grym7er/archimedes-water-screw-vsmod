# HardcoreWater Compatibility Review for Archimedes Screw

## Goal

Enable robust interoperability with `hardcorewater` so that:

1. HardcoreWater aqueducts can transport Archimedes-managed water without silently converting it to vanilla water.
2. Water that exits aqueducts remains attributable to the correct Archimedes controller (ownership continuity).
3. Compatibility is optional and only active when HardcoreWater is installed.

---

## What I Reviewed

### Archimedes Screw (`archimedes_screw`)

- `src/BlockEntities/BlockEntityWaterArchimedesScrew.cs`
- `src/Systems/ArchimedesWaterNetworkManager.cs`
- `src/Systems/ArchimedesWaterNetworkManager.Ownership.cs`
- `src/Blocks/BlockArchimedesWater.cs`
- `src/Config/ArchimedesScrewConfig.cs`
- `src/ModSystem/ArchimedesScrewModSystem.cs`
- existing compat pattern in:
  - `src/Compat/WaterfallCompatBridge.cs`
  - `src/Compat/WaterfallSpillTranspilerPatch.cs`
  - `src/Compat/WaterfallSpillFluidKind.cs`

### HardcoreWater (`vs-hardcorewater`)

- `HardcoreWater/ModBlockEntity/BlockEntityAqueduct.cs`
- `HardcoreWater/ModPatches/PatchBlockBehaviorFiniteSpreadingLiquid.cs`
- `HardcoreWater/HardcoreWaterModSystem.cs`
- `HardcoreWater/ModBlock/BlockAqueduct.cs`
- `HardcoreWater/ModBlock/BlockEnclosedAqueduct.cs`

---

## High-Level Compatibility Assessment

## Already Compatible

- Archimedes water block classes inherit vanilla water classes (`BlockWater`, `BlockWaterflowing`, `BlockWaterfall`), so HardcoreWater source detection methods (`is BlockWater`, `is BlockWaterflowing`, `is BlockWaterfall`) already recognize Archimedes fluids as "water-like".
- HardcoreWater's aqueduct logic can therefore *discover* Archimedes fluids and treat them as valid source candidates.

## Not Yet Compatible (Critical)

- HardcoreWater refill logic writes only `game:*` fluid codes (`game:water-still-*`, `game:saltwater-still-*`, `game:boilingwater-still-*`).
- If aqueduct cells currently contain Archimedes-managed fluid, the refill step can replace it with vanilla fluid.
- Result: ownership continuity is broken, and downstream egress may no longer remain in Archimedes controller ownership.

---

## Full Intersection Matrix

## 1) Mod Detection / Lifecycle

**Intersection:** startup and runtime compat enablement.

- Archimedes already has optional compat infrastructure (`WaterfallCompatBridge`) with install checks and config toggles.
- HardcoreWater installs Harmony patches in its own startup path and has no direct dependency hooks for Archimedes.

**Implementation approach:**

- Add new `HcwCompatBridge` in `archimedes_screw/src/Compat/` following the Waterfall pattern:
  - check `api.ModLoader.IsModEnabled("hardcorewater")`
  - config-gate behavior (`EnableHardcoreWaterCompat`)
  - apply Harmony patch set only when enabled.

---

## 2) Fluid Family Identity Through Aqueduct Refill

**Intersection:** `BlockEntityAqueduct.onServerTick1s()`.

**Current behavior:**

- Refill target is selected by crude family check:
  - if fluid path starts with `salt` -> `game:saltwater-still-*`
  - if starts with `boiling` -> `game:boilingwater-still-*`
  - else -> `game:water-still-*`

**Compatibility issue:**

- Archimedes fluids (`archimedes_screw:archimedes-water-*`) fail those checks and fall into default freshwater path, converting managed fluid to vanilla.

**Implementation approach (recommended):**

- Harmony prefix/postfix patch around aqueduct refill branch that:
  - inspects source-side fluid family via Archimedes manager (`TryResolveManagedWaterFamily`, `TryResolveVanillaWaterFamily`),
  - resolves desired replacement block through Archimedes manager when source family is Archimedes-managed (`GetManagedBlock(familyId, "still", level)`),
  - preserves vanilla behavior for non-Archimedes fluids.

**Alternative (lower safety):**

- Detect Archimedes fluid by code domain/path and replace string-prefix fallback with explicit mapping.

---

## 3) Ownership Assignment Inside Aqueduct Cells

**Intersection:** when aqueduct cell is filled/refilled and becomes a managed still cell.

**Current behavior:**

- HardcoreWater updates fluid block in aqueduct cell but does not call Archimedes ownership APIs.
- Ownership currently depends on adjacency conversion and controller ticks, which can lag or miss edge cases.

**Implementation approach (recommended):**

- During compat refill handling:
  - resolve controller owner from source context (see section 4),
  - call `AssignOwnedSourceForController(ownerId, aqueductPos, familyId)` after setting managed source,
  - trigger liquid neighbor updates in same ordering used by Archimedes source assignment path.

**Why this matters:**

- Ensures aqueduct segment itself remains owned while acting as transport conduit.

---

## 4) Ownership Propagation Along Aqueduct Chains

**Intersection:** HardcoreWater stores only `WaterSourcePos` + `HasWaterSource`; source can be another aqueduct BE.

**Challenge:**

- A downstream aqueduct may source from an upstream aqueduct, not directly from a fluid source cell owned by a controller.

**Implementation approach (recommended):**

- Add compat resolver method:
  - `TryResolveControllerOwnerForAqueduct(BlockEntityAqueduct be, out string ownerId, out string familyId)`.
- Resolution order:
  1. if `WaterSourcePos` fluid is Archimedes source and owned -> use that owner.
  2. if `WaterSourcePos` is aqueduct BE -> walk upstream via `WaterSourcePos` links with cycle protection + max depth (e.g. 64).
  3. fallback to nearest active controller in connected managed component via existing manager logic.

**Safety guards:**

- detect loops with visited set,
- short-circuit when chunk unavailable,
- abort to no-op if no deterministic owner found.

---

## 5) Aqueduct Outlet Egress (Your Core Requirement)

**Intersection:** HardcoreWater extends `FindDownwardPaths` and can push fluid from aqueduct into open world cells.

**Current risk:**

- Outflow cells can appear without explicit ownership transfer call at egress time.
- Depending on spread path and timing, those cells may become unowned managed fluid or vanilla fluid.

**Implementation approach (recommended):**

- Add egress handoff patch tied to aqueduct outflow opportunity:
  - when aqueduct has managed Archimedes fluid and finds an open outlet candidate, register pending ownership hint keyed by target position + ttl (short, e.g. 1-2 seconds),
  - when fluid appears in target cell, consume hint and call `AssignOwnedSourceForController` for still-source cases or queue adoption scan for flowing cases.

**Pragmatic simpler version:**

- On each compat tick for active aqueducts with managed fluid:
  - scan immediate open outlet positions (same orientation ends),
  - if fluid at outlet is Archimedes managed and unowned, assign owner using resolved upstream controller.

This simpler loop is easier to implement and should satisfy "water leaves aqueduct and still belongs to controller."

---

## 6) Interaction with Archimedes Drain / Cleanup

**Intersection:** Archimedes actively drains unsupported sources and removes unowned managed fluid.

**Risk without compat ownership:**

- Newly egressed aqueduct water can be classified unowned and cleaned up.
- This causes visible churn (spawn -> cleanup -> respawn).

**Implementation approach:**

- Ensure egress and in-aqueduct cells are promptly assigned owner.
- Reuse existing anti-thrash signals:
  - `MarkDrainQuarantine` - cells inside HCW aqueducts use a 2x quarantine TTL (`AqueductDrainQuarantineMultiplier`) to absorb the HCW refill cycle without triggering re-promotion churn.
  - local source cooldown and adoption cooldown semantics.
- Avoid creating a separate ownership model in compat layer.

**Defensive handshake (active):** `ArchimedesWaterBlockHelper.NotifyManagerOnRemoval` detects the HCW refill pattern where an aqueduct cell flips from same-family managed water to same-family vanilla water. In that case the manager enqueues a reconversion intent with the prior owner supplied as `ownerHintControllerId`, so the cell is reclaimed by its original controller on the next intent pass rather than entering the generic relay-promotion queue. Combined with the 2x aqueduct quarantine TTL this eliminates the ownership-loss flicker observed during HCW source-pulse cycles without requiring any Harmony patches.

**Vertical aqueduct cascades (active):** Relay promotion now propagates downward through stacked HCW aqueducts. An aqueduct candidate qualifies if either an orientation-aligned horizontal neighbor satisfies the existing rules (open end / same-family pool / same-family aqueduct along the pipe) **or** the cell directly above is another HCW aqueduct carrying same-family managed water with a passable liquid barrier between the two cells. The aqueduct-branch water-below guard (`ArchimedesRelayAdjacency.IsRelayBelowBlockedByNonAqueductWater`) is correspondingly relaxed: water below only blocks when it is **not** inside a same-family aqueduct, so cascades stay stable rather than collapsing as soon as the lower cell fills. Both relaxations are gated on family equality and barrier passability, so an aqueduct sitting over a natural lake (or a cross-family / vanilla-water flicker during an HCW refill pulse) still rejects exactly as before.

---

## 7) Save/Load Continuity

**Intersection:** aqueduct state persists (`HasWaterSource`, `WaterSourcePos`), Archimedes ownership persists in manager + BE snapshots.

**Compatibility concern:**

- After world load, aqueduct refill may run before all Archimedes ownership state fully rehydrated.

**Implementation approach:**

- Delay HCW compat activation similarly to existing post-load compat refresh callbacks.
- During first N ticks after load:
  - only apply ownership assignment when manager confirms loaded controller exists,
  - otherwise defer and retry (small bounded retry count).

---

## 8) Config Surface

Add config keys in `ArchimedesScrewConfig.WaterConfig`:

- `EnableHardcoreWaterCompat` (default `true`)
- `HardcoreWaterCompatDebug` (default `false`)
- optional:
  - `HardcoreWaterCompatOwnerResolveMaxDepth` (default `64`)
  - `HardcoreWaterCompatEgressAdoptionRadius` (default `1`)

Also add settings entries in `assets/archimedes_screw/config/settings.json` and ConfigLib patch mapping file.

---

## 9) Debug / Observability

Add compat logging and diagnostics:

- one-line startup status:
  - installed/not installed
  - enabled/disabled by config
  - patch apply success/failure
- debug counters:
  - aqueduct refill overrides
  - owner resolutions (direct, upstream walk, fallback nearest controller)
  - egress assignments
  - unresolved owner skips

Optional chat/debug command extension:

- `/archscrew debugwater scan` could include "source=hardcorewater-aqueduct" reason tags for recently assigned positions.

---

## 10) Multiplayer and Chunk Boundaries

**Intersection:** HardcoreWater already treats unloaded source chunk as temporarily valid.

**Compat recommendation:**

- If upstream owner cannot be resolved due to chunk unload:
  - do not assign to arbitrary controller,
  - mark as deferred with short retry window,
  - if still unresolved, allow existing Archimedes nearest-active fallback only when connectivity is clear.

This avoids cross-controller ownership theft in border cases.

---

## Recommended Implementation Plan (Phased)

## Phase 1 - Safe Core Compatibility (must-have)

1. Add HCW compat bridge + config flags.
2. Preserve Archimedes fluid family during aqueduct refill.
3. Assign ownership for aqueduct cell when managed fluid is set.
4. Add simple outlet scan assignment so egressed managed water inherits owner.

**Outcome:** aqueduct transfer works, and outflow generally remains owned by originating controller.

## Phase 2 - Robust Ownership Tracing

1. Implement upstream aqueduct source-chain owner resolution with cycle protection.
2. Add deferred retry for chunk-boundary unresolved ownership.
3. Improve logs/counters for diagnostics.

**Outcome:** stable behavior in long and complex aqueduct networks.

## Phase 3 - Hardening / Testing

1. Add integration tests/manual checklist for:
   - freshwater/salt/boiling families
   - multi-controller proximity
   - chunk unload/reload
   - aqueduct chain loops
   - unpowered/invalid controller drain behavior
2. Tune compatibility defaults and debug docs.

---

## Concrete Test Scenarios You Should Run

1. **Basic transfer**
   - screw outputs managed water into HCW aqueduct line
   - verify aqueduct cells remain Archimedes-managed, not `game:*` replacements.

2. **Outlet ownership continuity**
   - aqueduct ends into open air
   - verify spawned outflow/source cells are owned by the same controller (`debugwater`).

3. **Two-controller contention**
   - two screw systems near one aqueduct branch
   - verify deterministic ownership, no flip-flop every tick.

4. **Chunk edge**
   - source aqueduct chunk unloads while downstream remains loaded
   - verify no mass ownership loss and proper recovery after reload.

5. **Family preservation**
   - repeat with saltwater and boiling water variants.

---

## Key Risks and Mitigations

- **Risk:** ownership churn loops from repeated assign/release  
  **Mitigation:** reuse manager cooldown/quarantine logic; assign only on state transitions or unresolved->resolved changes.

- **Risk:** wrong controller attribution in complex networks  
  **Mitigation:** upstream source-chain resolution first, nearest-active fallback only as last resort.

- **Risk:** performance overhead from per-aqueduct scans  
  **Mitigation:** keep scans local (endpoints only), tick-gate with config interval, cache recent owner resolutions with short TTL.

---

## Final Recommendation

The compatibility is very feasible with your current architecture.  
The most important part is to intercept HardcoreWater's refill/outlet behavior so Archimedes-managed family and ownership metadata are preserved end-to-end.

If you implement only one slice first, implement **Phase 1** exactly: it directly addresses your two stated goals (aqueduct transfer + ownership continuity after egress) with minimal architectural risk.

---

## Implementation Status (Completed)

The phased plan in this document has been implemented with direct changes in `vs-hardcorewater`:

- Added Archimedes interop service:
  - `HardcoreWater/Compat/ArchimedesCompatService.cs`
  - reflection-based, optional activation when `archimedes_screw` is installed.
- Wired lifecycle initialization:
  - `HardcoreWater/HardcoreWaterModSystem.cs`
  - service created on server startup and cleared on dispose.
- Implemented managed-family-preserving refill in aqueduct tick:
  - `HardcoreWater/ModBlockEntity/BlockEntityAqueduct.cs`
  - uses Archimedes family resolution and managed block resolution when strict owner trace succeeds.
- Implemented strict owner trace and fallback:
  - owner is resolved only through upstream `WaterSourcePos` chain (with loop/depth/chunk guards),
  - unresolved owner forces vanilla refill/outflow behavior.
- Implemented outlet/source ownership handoff:
  - assigns ownership for source-height managed cells at aqueduct position and immediate outlet positions.
- Added guardrails and instrumentation counters:
  - depth cap, visited-loop detection, chunk-unloaded safe exits, compat counters.

## Verification Performed

- Built HardcoreWater after changes:
  - `dotnet build HardcoreWater/HardcoreWater.csproj -c Debug`
  - result: success, 0 warnings, 0 errors.

## Remaining Runtime Validation

In-game matrix still recommended to validate behavior under simulation timing:

1. Fresh/salt/boiling Archimedes transfer through aqueduct chains.
2. Strict fallback to vanilla when owner trace cannot be resolved.
3. Multi-controller proximity and chunk-border behavior.
4. Aqueduct loops and dependency-invalid layouts.

---

## Generic Outlet Compatibility (Implemented)

The outlet host-cell validation path is now generic and barrier-driven in `archimedes_screw`:

- Added shared validator:
  - `src/Util/ArchimedesFluidHostValidator.cs`
  - Uses vanilla-style liquid barrier semantics (`GetLiquidBarrierHeightOnSide`) plus fluid-layer compatibility.
- Wired into assembly validation:
  - `src/Systems/ArchimedesScrewAssemblyAnalyzer.cs`
  - Output-position checks now use the shared validator; outlet mode provides directional source/facing context.
- Wired into runtime controller validation:
  - `src/BlockEntities/BlockEntityWaterArchimedesScrew.cs`
  - `CanUseSeedPosition` now uses the same shared validator and outlet-direction context where applicable.

### Practical Effect

- Archimedes outlet output is no longer hard-coupled to “air-only” host cells.
- Any adjacent target cell in front of outlet can be valid if:
  1. host fluid cell is empty/compatible, and
  2. both source and target barrier sides allow liquid passage.
- This naturally supports HardcoreWater aqueducts when their barrier configuration allows flow, without hardcoding aqueduct types in Archimedes.
