# Chapter 08: Persistence, Config, and Compatibility

Great mods are configurable and robust across save/load cycles. This mod does both.

## Persistence Strategy

Manager state is stored in save data keys:

- screw block positions,
- controller positions,
- controller-owned source snapshots.

Block entities also keep local tracking state.  
This "dual persistence" protects against chunk load order issues.

Manager save/load methods:

- `Load()` in `src/Systems/ArchimedesWaterNetworkManager.cs` lines 289-444,
- `Save()` lines 446-460.

Notice the defensive parsing and malformed-key handling around lines 297-353 and 426-435.

## Post-Load Reactivation

After `SaveGameLoaded`, the manager:

1. loads saved state,
2. reapplies ownership from already-loaded controllers,
3. runs post-load reactivation passes.

Reactivation triggers fluid updates so managed water resumes expected behavior after world load.

Follow this call chain:

- `OnSaveGameLoaded()` in `src/ModSystem/ArchimedesScrewModSystem.cs` lines 209-224,
- `BeginPostLoadReactivation(...)` manager lines 87-98,
- `OnPostLoadReactivationTick(...)` lines 109-136,
- `ReactivateManagedFluidsFromTrackedAnchors()` lines 138-186.

Why retries are used: not all chunks and BEs are guaranteed to be loaded at the same instant after world start.

## Typed Config

`src/Config/ArchimedesScrewConfig.cs` defines `WaterConfig` with defaults and docs.  
`AssetsFinalize` deserializes `assets/archimedes_screw/config/settings.json`.

Benefits:

- one typed source of truth in code,
- straightforward tuning and validation,
- clear default values for users.

Deserialization happens in `ArchimedesScrewModSystem.AssetsFinalize(...)` lines 68-95.  
When parsing fails, the mod logs and falls back to defaults (lines 90-94) instead of crashing.

## Config Lib Integration

`assets/archimedes_screw/config/configlib-patches.json` maps JSON paths to UI/setting keys.  
The mod listens for Config Lib events, queues changes, and applies them on save.

Why apply on save:

- avoids partial runtime state mutation from half-completed UI edits.

Read these methods:

- `OnConfigLibSettingChanged(...)` lines 502-524,
- `OnConfigLibConfigSaved(...)` lines 473-500.

The flow is deliberate:

1. setting changes update `pendingWaterConfig`,
2. values become active only on save,
3. central tick restarts only if required by changed settings.

## Optional Compatibility Bridge

Compatibility classes under `src/Compat/` integrate with Waterfall if present and enabled.

Best practice shown here:

- no hard dependency at runtime,
- feature toggled by config,
- retries after load so mod ordering does not break integration.

See server startup + refresh in `StartServerSide(...)` lines 160-162 and delayed refresh callbacks in lines 217-223.

Next: debugging workflow, testing checklist, and packaging.
