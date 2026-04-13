# Chapter 00: Prerequisites and Tooling

This chapter gets your machine and mental model ready before you write gameplay logic.

## What You Need Installed

- Vintage Story (match the game dependency in `modinfo.json`).
- .NET SDK. This mod targets `net8.0;net10.0` in `archimedes_screw.csproj` (line 3).
- A C# editor with good navigation (go-to-definition, symbol search, quick peek).
- Terminal basics: `dotnet build`, `dotnet clean`, and file navigation.

## Understand How This Mod Builds

Read `archimedes_screw.csproj` first:

- Output path is `bin\$(Configuration)\Mods\mod\` (line 7), which is already a mod-folder layout.
- Vintage Story DLL references come from `$(VintageStoryPath)` (lines 8-40).
- `modinfo.json` and `assets\**\*` are copied into output automatically (lines 44-49).

Why this matters: if your `.csproj` is wrong, your game can fail to load the mod even when code compiles.

## Step-by-Step Environment Setup

1. Confirm your game install path.
2. Set `VINTAGE_STORY` environment variable so the csproj can resolve DLL paths.
3. Run `dotnet --info` and confirm the SDK is installed.
4. Run `dotnet build` in mod root.
5. Inspect `bin/Release/Mods/mod/` and ensure both `modinfo.json` and `assets/` are present.

## C# Refresher for This Codebase

You know programming already; map key C# idioms used here:

- Inheritance: `ArchimedesScrewModSystem : ModSystem` in `src/ModSystem/ArchimedesScrewModSystem.cs` (line 14).
- Auto-properties: `public ArchimedesScrewConfig Config { get; private set; } = new();` (line 48).
- Nullable references: `ICoreAPI? api` (line 30).
- Partial classes: `ArchimedesWaterNetworkManager` is split across multiple files to keep responsibilities manageable.
- Records/record structs: compact data containers in manager debug/result types.

## How to Read This Repo Efficiently

Use this reading order:

1. `modinfo.json` for identity/dependencies.
2. `archimedes_screw.csproj` for build/deploy behavior.
3. `src/ModSystem/ArchimedesScrewModSystem.cs` for lifecycle and architecture.
4. `assets/archimedes_screw/blocktypes/...` for content definitions.
5. `src/Blocks`, `src/BlockEntities`, `src/Systems` for runtime behavior.

## Common Setup Mistakes and Why They Happen

- Missing game DLL references: usually `VintageStoryPath` is wrong.
- Build succeeds but mod seems missing: output copied to wrong folder or old build still loaded.
- Runtime asset errors: domain/path mismatch between code and asset folders.

Next chapter: define mod metadata and folder conventions so your project is loadable before you add complex behavior.
