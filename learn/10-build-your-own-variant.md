# Chapter 10: Build Your Own Variant

Now apply the same architecture to a new machine-style mod.

## Pick a Variant Idea

Choose one:

- a different fluid mover,
- a powered heat/cooling conduit,
- a mechanical item transporter with ownership rules.

Keep scope close to this project for your first version.

Best first-project rule: keep one clear input, one clear output, and one global manager.

## Minimal Scaffold Order

1. Define `modinfo.json` and `assets/<modid>/` structure.
2. Create one blocktype + localization.
3. Add `ModSystem` with class registrations.
4. Add `Block` placement/interact rules.
5. Add `BlockEntity` for persistent state.
6. Add a manager for global scheduling/shared state.
7. Add save/load logic.
8. Add one debug command.
9. Add recipes and polish.

Expanded implementation checklist:

1. Start with one block variant and one simple interaction message.
2. Add one analyzer method that returns a structured status object.
3. Add block entity persistence for a tiny piece of state.
4. Add manager registration/unregistration and a basic global tick.
5. Add ownership map only when multiple machines can touch the same world cells.
6. Add cleanup path first, then growth/expansion path.
7. Add config defaults with safe bounds.
8. Add command for visibility and emergency cleanup.

## What to Keep From This Mod

- Central dispatcher instead of per-instance heavy ticking.
- Explicit ownership map for world resources.
- Analyzer class for machine validity.
- Configurable budgets/caps for server safety.
- Debug command path for operators.

Where these patterns live in this codebase:

- dispatcher: `ArchimedesWaterNetworkManager.StartCentralWaterTick()` and `OnGlobalWaterTick()`,
- analyzer: `ArchimedesScrewAssemblyAnalyzer.Analyze(...)`,
- ownership authority: `sourceOwnerByPos` and ownership methods in manager,
- admin operations: `/archscrew` registration in `ArchimedesScrewModSystem.RegisterCommands(...)`.

## What to Simplify Initially

- Skip compatibility bridges until core behavior is stable.
- Start with one fluid family before multiple families.
- Keep relay mechanics off until base conversion is reliable.

You can always add advanced behavior in V2:

- multiple fluid families,
- compatibility hooks,
- visual overlays,
- live config mutation.

## Definition of Done (V1)

- Players can build the machine and get clear status messages.
- Machine performs expected world changes while powered.
- State survives save/reload.
- Breaking machine cleans up its owned world state.
- Admin has at least one command to inspect/reset the system.

## Step-by-Step Capstone Exercise

Use these actions to build your own mod from scratch:

1. Create a new repository with this folder structure.
2. Copy only high-level patterns, not names, from this mod.
3. Implement one feature slice end-to-end:
   - asset definition,
   - class registration,
   - block interaction,
   - manager update,
   - persistence,
   - one debug command.
4. Playtest that slice before implementing the next one.
5. Add load/performance guardrails before release.

You now have a reusable blueprint for advanced Vintage Story code mods.
