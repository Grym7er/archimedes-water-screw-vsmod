# Archimedes Screw Mod

This mod adds a mechanically powered water Archimedes screw for Vintage Story. An intake screw is placed inside vanilla water, stacked upward with additional screw blocks, and when powered it maintains exactly one Archimedes source block at its output. Vanilla water logic then handles the downstream flow from that source.

## Project Layout

Source code lives under `src/`:

- `src/ModSystem/ArchimedesScrewModSystem.cs`
  Registers blocks and block entities, loads config, creates the server-side water manager, and registers the `/archscrew` admin commands.
- `src/Config/ArchimedesScrewConfig.cs`
  Defines the runtime config model loaded from `assets/archimedes_screw/config/settings.json`.
- `src/Systems/ArchimedesWaterNetworkManager.cs`
  Tracks Archimedes source nodes, family resolution, persistence, conversion of placed vanilla sources, and purge operations.
- `src/Systems/ArchimedesWaterFamilies.cs`
  Defines the supported vanilla water families and maps them to the managed Archimedes liquid families.
- `src/Systems/ArchimedesScrewAssemblyAnalyzer.cs`
  Validates the screw stack and resolves the intake/output positions used by the controller.
- `src/Blocks/BlockWaterArchimedesScrew.cs`
  Implements screw placement rules, mechanical connectors, and intake validation for the custom water screw block.
- `src/Blocks/BlockArchimedesWater.cs`
  Defines the custom water block classes, forwards removal notifications to the manager, and converts placed vanilla source blocks inside Archimedes water.
- `src/BlockEntities/BlockEntityWaterArchimedesScrew.cs`
  Runs the powered screw logic: validates the assembly, maintains the single output source, adopts connected Archimedes source nodes, and deletes unsupported/disconnected sources from the outside inward.

Asset and config files live under `assets/archimedes_screw/`:

- `blocktypes/metal/waterarchimedesscrew.json`: intake and straight screw block asset using vanilla screw visuals.
- `blocktypes/metal/waterarchimedesscrew-outlet.json`: upside-down outlet block asset for the top of the screw stack.
- `blocktypes/liquid/archimedes-water.json`: managed fresh-water family.
- `blocktypes/liquid/archimedes-saltwater.json`: managed salt-water family.
- `blocktypes/liquid/archimedes-boilingwater.json`: managed boiling-water family.
- `config/settings.json`: default runtime settings used by the code.
- `config/configlib-patches.json`: Config Lib integration for editing settings in-game.
- `lang/en.json`: English names and config labels.

## Build

Requirements:

- .NET 8 SDK
- Vintage Story 1.21.6 installed

This project defaults to using `/home/dewet/Games/vintagestory` as the game path. If your game is elsewhere, set `VINTAGE_STORY` before building.

Build command:

```bash
dotnet build
```

Build output:

- `bin/Debug/Mods/mod/archimedes_screw.dll`
- `bin/Debug/Mods/mod/modinfo.json`
- `bin/Debug/Mods/mod/assets/...`

To install the build into the game, copy the contents of `bin/Debug/Mods/mod/` into your Vintage Story mods folder, or package that folder as a zip mod.

## In-Game Test Plan

See [tests.md](tests.md) for the current source-model test plan.

## Admin Commands

- `/archscrew purge`: remove all mod water and screw blocks
- `/archscrew purgewater`: remove all managed Archimedes water
- `/archscrew purgescrews`: remove all custom screw blocks

## Current Notes

- Crafting recipes are not added yet; this build is creative/admin placement only.
- The screw currently expects vertical mechanical connections only.
- The intake accepts vanilla water-family blocks at the intake position.
- Each active screw maintains one Archimedes source at its output.
- Additional vanilla source blocks placed into Archimedes water are converted into the connected Archimedes family.
- If an assembly becomes non-functional, the mod removes connected Archimedes source nodes and lets vanilla liquid propagation dry up the rest of the flow.
