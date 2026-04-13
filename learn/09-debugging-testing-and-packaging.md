# Chapter 09: Debugging, Testing, and Packaging

This chapter turns your implementation into a maintainable mod.

## Debug Tooling in This Example

Admin commands under `/archscrew` include:

- purge flows/screws,
- profiling (`perf on|off|flush|status`),
- water debug overlay (`debugwater on|off|scan`).

Use this pattern early in your own mod. Internal visibility saves many hours.

Command registration is in `src/ModSystem/ArchimedesScrewModSystem.cs` lines 243-410.

What to inspect:

- `purge`/`purgewater`/`purgescrews` for emergency cleanup,
- `perf` subtree for runtime cost visibility,
- `debugwater` subtree for ownership diagnostics.

## Suggested Test Loop

After each significant change:

1. build mod,
2. load test world,
3. place valid screw assembly (intake + straight segments + outlet),
4. provide/remove mechanical power,
5. verify outlet source creation/removal,
6. break parts to verify cleanup and ownership updates,
7. reload save to verify persistence.

Add this advanced loop for manager-heavy mods:

8. place multiple machines in loaded chunks,
9. confirm tick budget behavior under load,
10. test admin recovery commands.

## High-Value Regression Cases

- many controllers loaded at once (dispatcher budget behavior),
- long connected water lines (BFS cap behavior),
- player manually altering fluid blocks near managed water,
- config changes applied during server runtime.

Map each regression to a code area:

- dispatcher/load behavior -> `ArchimedesWaterNetworkManager` global tick and load methods,
- fluid ownership behavior -> conversion/cleanup methods,
- runtime config behavior -> Config Lib handlers in `ArchimedesScrewModSystem`.

## Packaging Path

From `archimedes_screw.csproj`:

- build output goes to `bin/<Config>/Mods/mod/`,
- `modinfo.json` and `assets/**` are copied with build.

For release:

1. clean build,
2. verify output folder contents,
3. zip mod folder structure exactly as expected by Vintage Story.

Step-by-step release sanity check:

1. delete old release zip and old build output,
2. run fresh build,
3. verify blocktype/lang/config assets are present,
4. launch game with only your mod enabled,
5. run one in-game smoke test before publishing.

## Practical Advice

Keep debug commands even in release builds for server admins.  
Guard noisy logs behind config flags (`VerboseDebug` style).

The pattern in this mod (`LogVerbose` and `LogVerboseOrNotification` around lines 125-149 in `ArchimedesScrewModSystem.cs`) is a good template.

Next: build your own variant using this architecture.
