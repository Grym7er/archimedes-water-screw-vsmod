# How It Works Under The Hood

This chapter walks through what code executes for a typical Archimedes screw setup.

Assume server side, because water ownership and ticking are server-authoritative.

## Preconditions: world/system startup

Before any player action below, these are already active:

1. `ArchimedesScrewModSystem.StartServerSide(...)` creates `ArchimedesWaterNetworkManager`.
2. `ArchimedesWaterNetworkManager.StartCentralWaterTick()` registers one global listener.
3. Every placed screw BE (`BlockEntityWaterArchimedesScrew.Initialize(...)`) registers itself with manager structures.
4. Only intake blocks are put into central dispatch via `UpdateCentralTickRegistration()` -> `RegisterForCentralWaterTick(...)`.

---

## 1) Player places endcap in water

Path starts in `BlockWaterArchimedesScrew.TryPlaceBlock(...)`.

1. `TryPlaceBlock(...)` calls `ResolveBlockToPlace(...)`.
2. `ResolveBlockToPlace(...)` checks endcap context:
   - `TryResolveOutletPlacement(...)` first (outlet case),
   - else `HasValidWaterIntake(...)` (intake case).
3. If the endcap is in water, `HasValidWaterIntake(...)` reads fluid layer and validates through `IsValidIntakeFluidBlock(...)`.
4. Endcap gets resolved to a concrete intake variant (`ported-*`) and is placed by `DoPlaceBlock(...)`.
5. BE lifecycle runs:
   - `BlockEntityWaterArchimedesScrew.OnBlockPlaced(...)`
   - then `Initialize(...)` (if not already loaded)
   - registration calls:
     - `waterManager.RegisterScrewBlock(Pos)`
     - `waterManager.RegisterLoadedController(this)`
     - `UpdateCentralTickRegistration()` (intake -> manager tick list)

Key effect: this block becomes the controller candidate, but it still needs valid assembly + power to pump.

---

## 2) Player places N straight sections

Each straight section follows the same placement path:

1. `BlockWaterArchimedesScrew.TryPlaceBlock(...)`
2. `ResolveBlockToPlace(...)` returns `this` for straight blocks (`IsStraightSegment()`).
3. `DoPlaceBlock(...)` writes block.
4. BE `OnBlockPlaced(...)` and `Initialize(...)` run, register block/controller metadata.
5. `UpdateCentralTickRegistration()` does **not** central-register non-intake segments.

Why this matters: straight blocks participate in assembly shape validation, but only intake drives controller ticks.

---

## 3) Player places outlet

Again starts in `BlockWaterArchimedesScrew.TryPlaceBlock(...)`.

1. `ResolveBlockToPlace(...)` detects outlet context via `TryResolveOutletPlacement(...)`:
   - below block must be straight or intake,
   - below cannot already be outlet.
2. Variant resolves to `end-outlet-*` using player orientation.
3. Block is placed with `DoPlaceBlock(...)`.
4. BE registration runs as usual, but outlet itself is not the ticking intake controller.

How outlet changes behavior later:

- `ArchimedesScrewAssemblyAnalyzer.Analyze(...)` calls `GetPortFacing()` on outlet to compute lateral `OutputPos` instead of default `top.UpCopy()`.

---

## 4) Mechanical power attached with speed > minimum

There are two pieces: power network state + periodic controller evaluation.

1. On subsequent global dispatch cycles:
   - `ArchimedesWaterNetworkManager.OnGlobalWaterTick(...)`
   - selected intake controller `RunCentralWaterTick()`
   - `BlockEntityWaterArchimedesScrew.OnWaterControllerTick()`
2. Tick calls `EvaluateController()`.
3. `EvaluateController()` runs `ArchimedesScrewAssemblyAnalyzer.Analyze(Api.World, Pos, MinimumNetworkSpeed)`.
4. Analyzer reads MP behavior from intake BE:
   - `intakeBe.GetBehavior<BEBehaviorMPArchimedesScrew>()`
   - checks `Math.Abs(behavior.Network.Speed) >= minimumNetworkSpeed`
5. If assembly valid and powered:
   - `ControllerEvaluation.IsController = true`
   - `IsPowered = true`
   - includes resolved fluid family and seed/output position.

Then pump logic executes:

1. `EnsureSeedSource(...)` -> manager `AssignOwnedSourceForController(...)` (wrapper over `EnsureSourceOwned(...)`).
2. Connected managed region gathered:
   - `CollectConnectedManagedWaterCachedDetailed(...)`
3. Vanilla neighbors converted:
   - `ConvertAdjacentVanillaSourcesIteratively(...)`
4. Relay maintenance:
   - `CreateRelaySources(...)` promotes eligible managed-water cells to sources.
   - Inside an aqueduct, a candidate qualifies if an orientation-aligned neighbor is either:
     - fully empty (air solid + air fluid) — i.e. an open pipe end, or
     - solid air with liquid of the candidate's family, or
     - another aqueduct cell whose fluid layer is same-family managed water (propagation along the pipe).
   - Outside an aqueduct, the candidate needs a same-family managed horizontal neighbor (with passable barrier) OR the whitelisted flat terrain layout. Either branch additionally requires the block directly below the candidate to be dry (not water).
   - `TrimRelaySourcesToCap(...)` releases excess relays, preferring the newest promotions (age-based trim).
   - Ownership is stored in a manager-authoritative relay index (`relayOwnerByPos`) that survives BE chunk unload; BEs write-through via `AssignRelaySourceForController` / `ReleaseRelaySourceForController`.
5. Unsupported/disconnected sources drained:
   - `DrainUnsupportedSources(...)` — skipped for the current tick if the connectivity BFS was truncated, to avoid releasing relays that were merely beyond the BFS budget.

---

## 5) Water flows freely down a channel

Once powered and valid, water propagation is maintained by repeated controller ticks and fluid neighbor updates.

Controller loop responsibilities:

1. Maintain owned seed/source cells (`EnsureSourceOwned` path).
2. Expand eligible adjacent vanilla sources into managed sources:
   - `ConvertAdjacentVanillaSourcesIteratively(...)`
   - internally `TryConvertVanillaSource(...)`
3. Trigger fluid reactions after placement:
   - manager `TriggerLiquidUpdates(...)`
   - neighbor callbacks fired for solid/fluid neighbors.

Block-level managed water hooks (`BlockArchimedesWater*` classes):

1. On neighbor changes:
   - `OnNeighbourBlockChange(...)` -> `ArchimedesWaterBlockHelper.TryConvertNeighbourSource(...)`
2. On removal:
   - `OnBlockRemoved(...)` -> `NotifyManagerOnRemoval(...)` -> manager `OnManagedWaterRemoved(...)`

So free-flow behavior is a combination of:

- periodic controller decisions,
- managed-source ownership bookkeeping,
- vanilla/managed fluid engine neighbor reactions.

---

## 6) Mechanical power detached

Detaching power does not instantly delete all managed water; it transitions through invalid/not-powered controller handling.

1. Next global dispatch calls controller `OnWaterControllerTick()`.
2. `EvaluateController()` now reports `IsPowered = false` (or assembly invalid, depending on topology).
3. `HandleInvalidControllerState(...)` runs:
   - arms/uses grace windows for transient topology cases,
   - calls `DrainUnsupportedSources(...)` (budgeted release),
   - calls `CleanupUnownedManagedSourcesForControllerState(...)`.
4. Release path uses manager wrapper:
   - `ReleaseOwnedSourceForController(...)` -> `ReleaseSourceOwner(...)`
   - manager removes ownership and may remove managed fluid block.
5. If screw blocks are broken/unloaded, stronger cleanup executes:
   - `OnBlockRemoved()` -> `ReleaseAllManagedWater("block removed")`
   - unregisters tick/controller snapshots.

Net effect: water ownership collapses in controlled, budgeted steps instead of chaotic immediate deletion.

---

## Fast call graph summary

1. Place/shape:
   - `BlockWaterArchimedesScrew.TryPlaceBlock` -> `ResolveBlockToPlace` -> `DoPlaceBlock`
2. Register:
   - `BlockEntityWaterArchimedesScrew.Initialize` -> manager register methods
3. Tick dispatch:
   - manager `OnGlobalWaterTick` -> controller `RunCentralWaterTick` -> `OnWaterControllerTick`
4. Validate:
   - `EvaluateController` -> `ArchimedesScrewAssemblyAnalyzer.Analyze`
5. Grow/maintain:
   - `EnsureSeedSource`, conversion, relay pass, cleanup
6. Power loss:
   - invalid-state handling -> budgeted release/cleanup.
