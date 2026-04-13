# Chapter 03: ModSystem Lifecycle

`ModSystem` is your runtime entry point. This example uses it well by separating responsibilities by phase.

## Lifecycle Breakdown

From `src/ModSystem/ArchimedesScrewModSystem.cs`:

- `Start(ICoreAPI api)`
  - Registers block classes and block entity classes (lines 61-65).
  - Keep this for class registration and basic setup.
- `AssetsFinalize(ICoreAPI api)`
  - Loads `config/settings.json` into typed config (lines 68-95).
  - Runs after assets are available.
- `StartServerSide(ICoreServerAPI api)`
  - Creates server-only systems (`ArchimedesWaterNetworkManager`) (line 158).
  - Starts central tick dispatch (line 159).
  - Registers save/load hooks and admin commands (lines 164-174 and 243-410).
- `StartClientSide(ICoreClientAPI api)`
  - Registers client networking and debug overlay (lines 176-187).
- `Dispose()`
  - Unsubscribes listeners and disposes managers (lines 189-207).

## Step-by-Step: Build Your Own ModSystem

1. Create class inheriting `ModSystem`.
2. Add constants for `ModId` and shared codes.
3. Register block/blockentity classes in `Start`.
4. Read config in `AssetsFinalize`.
5. Initialize server manager(s) in `StartServerSide`.
6. Wire client-only visual/debug features in `StartClientSide`.
7. Always unsubscribe/unregister in `Dispose`.

## Why This Separation Is Important

- Prevents client/server API misuse.
- Avoids loading assets before they exist.
- Keeps performance systems server-authoritative.
- Ensures clean teardown when worlds unload.

## Network and Commands

This mod registers channel `archimedes_screw` for debug packets and a command tree under `/archscrew`.

Pattern to reuse:

1. register channel,
2. define packet type(s),
3. set handler on client,
4. emit packets from server.

Concrete references:

- server channel registration: lines 154-156,
- client channel registration + handler: lines 180-186,
- periodic debug snapshot sender: lines 423-470.

## Save/Load Hooks

`SaveGameLoaded` and `GameWorldSave` are used to persist/restore custom manager state.  
This is essential for systems that maintain world ownership maps outside vanilla blocks.

See:

- `OnSaveGameLoaded()` lines 209-224,
- `OnGameWorldSave()` lines 226-241.

Notice the post-load sequence: load -> reapply ownership -> reactivation retries.  
That order is deliberate to handle chunk and block-entity load timing.

## Beginner C# Tip

Look for "stateful fields + lifecycle methods":

- fields: `WaterManager`, channels, config,
- methods: where each field is initialized, used, and cleaned up.

If a field is initialized in server startup, ask where it is disposed. This habit prevents listener leaks and duplicated ticks.

Next: block class vs block entity class responsibilities.
