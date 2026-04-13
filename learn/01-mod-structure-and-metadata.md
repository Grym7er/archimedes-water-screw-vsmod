# Chapter 01: Mod Structure and Metadata

Before writing gameplay logic, make sure the game can identify, load, and namespace your mod correctly.

## `modinfo.json` Essentials

Read `modinfo.json` first:

- `"type": "code"`: tells Vintage Story this includes compiled C#.
- `"modid": "thetruearchimedesscrew"`: unique mod package identifier.
- `"dependencies"`: game compatibility (`"game": "1.21.6"`).

For your own mod, choose a stable lowercase `modid` early. Renaming later affects assets, config files, and saved worlds.

## Domain vs Package ID

In this project, code often uses `ArchimedesScrewModSystem.ModId = "archimedes_screw"` for asset lookup and block codes, while `modinfo.json` has a different modid string.

That can work, but it increases cognitive load. For a new mod, keep these aligned:

- `modinfo.json` modid
- asset folder domain (`assets/<domain>/...`)
- code constant used in `AssetLocation`

You can see the domain constant in `src/ModSystem/ArchimedesScrewModSystem.cs` (line 17), and config loading using that domain at lines 68-71.

## Directory Conventions

Recommended structure:

- `assets/<modid>/blocktypes/...`
- `assets/<modid>/itemtypes/...`
- `assets/<modid>/recipes/...`
- `assets/<modid>/lang/en.json`
- `assets/<modid>/config/...`
- `src/ModSystem/...`, `src/Blocks/...`, `src/BlockEntities/...`, `src/Systems/...`

Step-by-step scaffold:

1. Create `modinfo.json`.
2. Create `assets/<modid>/lang/en.json`.
3. Add first `blocktypes` JSON.
4. Add `src/ModSystem/<YourModSystem>.cs`.
5. Register one block class in `Start(...)`.

## Why This Structure Matters

- Vintage Story discovers assets by convention.
- Code registration looks up block/entity classes by name.
- Clear separation of concerns helps debugging:
  - assets define what exists,
  - code defines how it behaves.

## Practical Rule

If something "doesn't load," first check naming consistency:

- JSON `code`
- class registration names
- asset domain
- block/entity class names

## Quick Validation Pass (Do This Early)

After scaffolding:

1. Build project.
2. Start game with only your mod enabled.
3. Check logs for missing asset/class messages.
4. Fix naming mismatches immediately before adding more systems.

Why: naming consistency bugs compound as files multiply.

Next chapter: build the asset side (blocktypes, recipes, language, and shapes) before deep C# logic.
