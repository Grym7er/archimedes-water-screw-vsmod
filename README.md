# Archimedes Screw

Mechanically powered water lifting for Vintage Story.

## Features

- Vertical Archimedes screw multiblock with intake, straight segments, and outlet.
- Intake can be placed before water arrives; activation still requires valid intake fluid.
- Pumps and maintains managed water at the outlet while powered.
- Supports relay sources for long-distance aqueduct flow.
- Relay promotion ordering is configurable (deterministic or random within equal-distance buckets).
- Handles ownership and cleanup of managed sources when assemblies become invalid.
- Save/load-safe controller ownership restore and relay stabilization.
- Truncation-safe behavior for very large connected water networks.
- Optional Waterfall compatibility hooks.
- Configurable runtime tuning (including Config Lib support) and optional verbose debug logging.

## Build

Requirements:

- .NET 8 SDK
- Vintage Story 1.21.6

Build:

```bash
dotnet build
```

If your game path is not auto-detected, set `VINTAGE_STORY` before building.

Build output is under `bin/Debug/Mods/mod/`.

## Install

Copy the contents of `bin/Debug/Mods/mod/` into your Vintage Story mods folder, or zip that folder for distribution.

## Admin commands

- `/archscrew purge`
- `/archscrew purgewater`
- `/archscrew purgescrews`
