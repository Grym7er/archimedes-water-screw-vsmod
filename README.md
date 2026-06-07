# Archimedes Screw

Mechanically powered water lifting for Vintage Story.

## Features

- Vertical Archimedes screw multiblock with intake, straight segments, and outlet.
- Intake can be placed before water arrives; activation still requires water at the intake.
- Places a single regular level-7 water source at the outlet when the assembly is valid, powered, and the intake sits in water.
- The output source matches the intake fluid family (water, salt water, or boiling water).
- Removes the created source as soon as the assembly is invalidated (power lost, structure broken, or intake out of water).
- Optional RealisticWater compatibility (places and sustains a realistic-water outlet when that mod is installed).

## Build

The project multi-targets two frameworks, each matched to a Vintage Story release:

| Target framework | Vintage Story | Default game path |
| --- | --- | --- |
| `net10.0` | 1.22+ | `~/Games/vintagestory` |
| `net8.0` | 1.21 | `~/.local/share/vintagestory` |

Each target references the game assemblies from its own install, so building one
framework requires the matching game version to be present at its path. Pick the
framework for the Vintage Story version you are targeting:

```bash
dotnet build -f net10.0   # Vintage Story 1.22+
dotnet build -f net8.0    # Vintage Story 1.21
```

If your game is installed elsewhere, set `VINTAGE_STORY` before building; it
overrides the per-framework default path:

```bash
export VINTAGE_STORY="/path/to/Vintage Story"
```

The .NET SDK is pinned via `global.json`. Build output is under `bin/Debug/Mods/mod/`.
The helper scripts `build_net8.sh` and `build_net10.sh` build and deploy a zip to
your mods folder (override the destination with `VINTAGE_STORY_MODS_DIR`).

## Install

Copy the contents of `bin/Debug/Mods/mod/` into your Vintage Story mods folder, or zip that folder for distribution.
