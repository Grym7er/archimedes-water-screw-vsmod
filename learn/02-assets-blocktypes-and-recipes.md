# Chapter 02: Assets, Blocktypes, and Recipes

Build your data layer first. In Vintage Story, assets define most "what exists" behavior, and C# adds advanced runtime logic.

## Core Asset Files in This Example

- Screw block definition: `assets/archimedes_screw/blocktypes/metal/waterarchimedesscrew.json`
- Managed fluid blocktypes: `assets/archimedes_screw/blocktypes/liquid/...`
- Recipes: `assets/archimedes_screw/recipes/...`
- Localization: `assets/archimedes_screw/lang/en.json`
- Config defaults: `assets/archimedes_screw/config/settings.json`

Read these side-by-side with `src/ModSystem/ArchimedesScrewModSystem.cs` lines 61-65, where the block and block entity classes are registered.

## Designing a Blocktype

In `waterarchimedesscrew.json`:

- `code` is the base block code.
- `class` maps to C# block class.
- `entityclass` maps runtime state logic to the block entity class.
- `variantgroups` creates multiple block variants from one template.
- `shape...` fields define rendering.
- `sidesolidByType` controls placement and fluid interaction behavior.

Step-by-step for your own blocktype:

1. Set `code` and `class`.
2. Add one `variantgroups` entry (`type` is common).
3. Add one shape mapping and test in-game.
4. Add additional variants only after base one works.
5. If the block needs runtime state, set `entityclass`.

## Why Variants Are Used Here

The mod models multiple physical states with one block family:

- straight segment,
- intake endcap (`ported-*`),
- outlet endcap (`end-outlet-*`).

This reduces duplicated JSON and keeps logic centralized in one class.  
You can see variant interpretation in `src/Blocks/BlockWaterArchimedesScrew.cs`:

- `IsStraightSegment()` lines 177-181,
- `IsIntakeBlock()` lines 241-245,
- `IsOutletBlock()` lines 247-251.

That is a strong pattern: let JSON declare variants, let C# classify and enforce behavior.

## Recipes and Language

Keep player-facing workflow coherent:

- add crafting/smithing recipes,
- add all display names and failure messages in `lang/en.json`,
- include creative tab labels and config strings.

Notice how placement failure keys in `lang/en.json` match failure codes set in C#:

- `archimedes-screw-requires-water` (C# lines 46 and 150 in `BlockWaterArchimedesScrew.cs`),
- corresponding translated strings in `assets/archimedes_screw/lang/en.json`.

This is why localization should be done together with placement rules, not as an afterthought.

## Build-From-Scratch Order

1. Define a simple block with one variant.
2. Add a second variant and confirm it appears correctly.
3. Add localization for names/errors.
4. Add recipe so it is obtainable in survival.
5. Only then connect advanced C# behavior.

## Why Asset-First Works Better

- You get immediate visual feedback in game.
- You can verify naming and domain correctness early.
- You reduce debugging scope: "asset load issue" vs "runtime logic issue."

## Common Pitfalls

- Variant names in JSON not matching code checks.
- Missing language keys for placement errors.
- Asset path typos in shape references.
- Adding many variants before validating one end-to-end.

Next chapter: wire these assets into the `ModSystem` lifecycle and server/client startup flow.
