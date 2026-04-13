# Chapter 04: Block vs BlockEntity Design

Vintage Story commonly splits behavior between `Block` and `BlockEntity`. This mod is a strong example.

## `BlockWaterArchimedesScrew` (stateless interaction logic)

In `src/Blocks/BlockWaterArchimedesScrew.cs`, the block class handles:

- placement logic (`TryPlaceBlock`),
- variant resolution for endcaps (`ResolveBlockToPlace`),
- intake/outlet helper checks (`IsIntakeBlock`, `GetPortFacing`),
- player interaction status checks (`OnBlockInteractStart`),
- mechanical connector compatibility.

Think of this as "world interaction and rules at click/place time."

Important code points:

- `TryPlaceBlock(...)` lines 24-107: validates placement context, resolves correct variant, and handles mechanical connection handoff.
- `ResolveBlockToPlace(...)` lines 109-157: chooses intake/outlet variant based on world context.
- `OnBlockInteractStart(...)` lines 206-239: gives player status feedback and optional debug stats.

## `BlockEntityWaterArchimedesScrew` (stateful runtime logic)

The block entity handles ongoing controller state:

- tracked ownership/snapshot state,
- runtime ticking decisions,
- source creation/cleanup coordination through manager.

Think of this as "long-lived simulation state attached to one block position."

To study this, open `src/BlockEntities/BlockEntityWaterArchimedesScrew.cs` and trace:

1. initialization and manager registration,
2. periodic tick logic or manager-dispatched execution,
3. ownership snapshot read/write paths.

This file is where machine behavior persists across save/load and chunk unload/reload.

## Why the Split Is Good

- Block classes stay lightweight and reusable across variants.
- Stateful logic is saved/loaded through BE lifecycle naturally.
- Easier debugging: placement bugs vs runtime simulation bugs are separated.

Why this matters in practice: if machine state desyncs, you inspect BE + manager paths; if item placement is wrong, you inspect block class and JSON variants.

## Reusable Pattern for Your Own Mod

1. Put player-triggered checks and orientation in `Block`.
2. Put persistent per-instance data in `BlockEntity`.
3. Keep complex shared logic in a manager/service class.

Step-by-step implementation sequence:

1. Implement a basic block that places successfully.
2. Add block entity class and register it.
3. Store one persistent test field in the BE to confirm save/load works.
4. Move evolving state from block into BE.
5. Introduce a manager only when multiple BEs need shared coordination.

## Beginner C# Tip

If you are unsure where code belongs, ask:

- "Does this run only during placement/interact?" -> `Block`.
- "Does this need to persist and update over time?" -> `BlockEntity`.

Also ask: "Does this involve cross-machine ownership/global limits?" If yes, it likely belongs in a manager, not a single BE.

Next: enforcing multiblock validity and gameplay constraints.
