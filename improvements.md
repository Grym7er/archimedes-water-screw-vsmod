# Archimedes Screw Repo Deep Dive: Improvements

This document lists concrete improvements found during a deep review of `src/`, config assets, and docs.

## High-priority bug fixes

1. **Wire `maxVanillaConversionPasses` into runtime behavior (currently unused)**
   - **Why it matters:** The config exists in `settings.json`, Config Lib, and C# model, but no controller path calls `ConvertAdjacentVanillaSourcesIteratively(...)`, so the setting has no effect.
   - **Evidence:** `MaxVanillaConversionPasses` is read and copied, but only referenced in config handling, not in controller tick logic.
   - **Suggested fix:** In controller tick flow (likely after ensuring seed and before drain), call iterative conversion using `waterConfig.MaxVanillaConversionPasses`, with perf counters and a guard to avoid redundant BFS when idle.

2. **Fix mismatched default values between code and asset/configlib**
   - **Why it matters:** Different defaults can produce surprising behavior depending on whether values come from raw assets, code defaults, or Config Lib overlays.
   - **Mismatches found:**
     - `relayStrideBlocks`: `settings.json=7`, `WaterConfig=14`, `configlib-patches default=14`
     - `requiredMechPowerForMaxRelay`: `settings.json=0.5`, `WaterConfig=0.02`, `configlib-patches default=0.02`
   - **Suggested fix:** Decide canonical defaults, then align all three sources (`settings.json`, `ArchimedesScrewConfig`, `configlib-patches.json`) and note in release notes.

3. **Harden position-key parsing to avoid server crashes on corrupt save data**
   - **Why it matters:** `ParsePosKey` throws `FormatException`; if stored key data is malformed, load/purge/tick paths could throw and destabilize server startup.
   - **Evidence:** Throwing parse methods in `ArchimedesWaterNetworkManager` and `BlockEntityWaterArchimedesScrew`.
   - **Suggested fix:** Add `TryParsePosKey(string, out BlockPos)` and skip/log bad entries instead of throwing.

4. **Avoid heavy reflection scan every compat patch attempt**
   - **Why it matters:** Waterfall compat fallback scans all loaded assemblies/types searching for `SpillContents`, which can be expensive and brittle.
   - **Evidence:** `ResolveWaterfallSpillMethod()` loops every assembly + `GetTypes()`.
   - **Suggested fix:** Cache successful target by full type name, constrain scan scope (known namespaces/assembly names), and degrade to once-per-session warning on failure.

## Medium-priority reliability and correctness improvements

5. **Validate/clamp config values centrally**
   - **Why it matters:** Config Lib ranges help in UI, but runtime should still defend against invalid/malformed values from file edits or other mods.
   - **Suggested fix:** Add `Normalize()` in `WaterConfig` or in mod system load path to clamp all values (tick rates, caps, hysteresis, speed thresholds) with warning logs on correction.

6. **Use unload-safe event listener cleanup for Config Lib handlers**
   - **Why it matters:** `RegisterEventBusListener` handlers are added, but there is no explicit unregister path; on long sessions/reload scenarios this can risk duplicate handling.
   - **Suggested fix:** If API supports unregister in target game version, store IDs and unregister in `Dispose`; otherwise gate handlers with a disposed flag.

7. **Stabilize central tick cursor advancement fairness**
   - **Why it matters:** Cursor increments by one each global tick regardless of processed count. Under heavy due-load + low budget this can skew fairness or increase latency variance.
   - **Suggested fix:** Advance cursor by number of inspected or processed entries with wrap-around and benchmark with 100+ controllers.

8. **Avoid repeated string key generation in hot loops**
   - **Why it matters:** `PosKey(...)` string creation is frequent in BFS, relay creation, ownership checks, and drain routines.
   - **Suggested fix:** Consider lightweight struct key (`(int x,int y,int z)` or custom comparer) for internal dictionaries; keep string serialization only for persistence/logging.

9. **Reduce temporary allocations in controller hot paths**
   - **Why it matters:** Frequent `.ToList()`, `.ToArray()`, LINQ sorting, and copied `BlockPos` objects may increase GC pressure in busy servers.
   - **Suggested fix:** Replace hot-path LINQ with pooled lists/manual loops where practical; batch snapshot updates only when changed.

10. **Guard against stale `WeakReference` buildup outside compaction windows**
    - **Why it matters:** Central tick list compacts every 20 cycles; under high churn this can still accumulate stale entries temporarily.
    - **Suggested fix:** Opportunistic cleanup when stale ratio exceeds threshold during dispatch.

## Performance and scalability opportunities

11. **Cap/partition BFS work per tick for extreme water networks**
    - **Why it matters:** `CollectConnectedManagedWater` has `MaxBfsVisited=4096`, but large connected networks can still consume substantial CPU bursts.
    - **Suggested fix:** Add optional per-tick BFS budget or progressive scan mode for very large components, with debug counters to track truncation impact.

12. **Improve relay candidate selection complexity**
    - **Why it matters:** Relay creation sorts full distance map and performs multiple ownership checks; complexity grows with network size.
    - **Suggested fix:** Introduce bounded candidate heap or early filter pipeline to avoid sorting all nodes every attempt.

13. **Add perf metric aggregation by controller count tiers**
    - **Why it matters:** Existing profiler is useful but mostly operation-centric. Capacity planning benefits from metrics normalized by active controller counts.
    - **Suggested fix:** Log active controllers, due controllers, processed controllers, avg queue latency, and skipped scans per interval.

## Code cleanup and maintainability

14. **Consolidate duplicate position encode/decode helpers**
    - **Why it matters:** Similar encode/decode/parse logic exists in multiple classes, increasing drift risk.
    - **Suggested fix:** Move to a shared utility class (`ArchimedesPosCodec`) with tested parse and serialization methods.

15. **Consolidate managed water checks into one policy helper**
    - **Why it matters:** Validity checks for solid/fluid output/intake clear conditions are duplicated between analyzer/controller/manager.
    - **Suggested fix:** Extract canonical predicates for “output cell usable”, “intake fluid valid”, “source replacable”.

16. **Review logging verbosity in production paths**
    - **Why it matters:** `Notification` level is used frequently in placement/status/control flows; this can flood logs on active servers.
    - **Suggested fix:** Move repetitive logs to debug-gated path (`DebugControllerStatsOnInteract`-style or dedicated `water.debugLogs` setting), keep key lifecycle logs at notification.

17. **Prefer explicit naming around “source ownership” semantics**
    - **Why it matters:** Some methods (“EnsureSourceOwned”, “AssignConnectedSource...”, “TrackAssignedSource...”) are close in purpose but semantically distinct.
    - **Suggested fix:** Tighten naming/docs around “ownership assignment”, “state snapshot”, and “fluid placement” phases.

18. **Clean minor dead/placeholder code**
    - **Example:** Empty override `DidConnectAt(...)` can be removed (if not required) or commented with intent.

## Test coverage improvements

19. **Add automated tests around ownership determinism and reassignment**
    - **Why it matters:** These behaviors are central and easy to regress with performance changes.
    - **Suggested scenarios:** symmetric tie-breaks, invalidation handoff, merge/split network ownership transitions.

20. **Add tests for config reload semantics**
    - **Why it matters:** Deferred apply-on-save behavior is intentional; regressions here are subtle.
    - **Suggested scenarios:** setting changed event queueing, save event application, central tick restart-required values.

21. **Add persistence corruption-resilience tests**
    - **Why it matters:** Save data is long-lived in multiplayer worlds.
    - **Suggested scenarios:** malformed keys, partial owned arrays, duplicate source owner conflicts, stale controller IDs.

22. **Add compatibility contract tests for Waterfall hook resolution**
    - **Why it matters:** Reflection-based targeting is fragile across mod updates.
    - **Suggested scenarios:** method found directly, fallback found, not found (graceful no-op), debug logging path.

## Documentation and ops improvements

23. **Document config source-of-truth and precedence**
    - **Why it matters:** Current setup uses defaults + config asset + Config Lib patching + save-apply behavior.
    - **Suggested fix:** Add a short section in `README.md` that explains precedence and when values take effect.

24. **Versioned migration notes for saved-state format changes**
    - **Why it matters:** Future changes to ownership/snapshots/parsing benefit from explicit migration handling.
    - **Suggested fix:** Add save schema version key and migration hooks in load path.

25. **Expand troubleshooting guide**
    - **Why it matters:** Admin users need rapid diagnosis for “assembly valid but dry”, “relay not creating”, “compat inactive”.
    - **Suggested fix:** Add symptom -> probable cause -> command/check matrix in docs.

## Suggested implementation order

1. Fix config/behavior correctness: items **1-3**.
2. Reliability hardening: items **5-7**.
3. Performance improvements: items **8-13**.
4. Cleanup and test expansion: items **14-22**.
5. Documentation/ops polish: items **23-25**.

## Quick wins (low effort, high value)

- Align mismatched defaults across config files and C# model.
- Wire `MaxVanillaConversionPasses` into controller tick behavior.
- Replace throwing `ParsePosKey` with `TryParse` + warning logs.
- Add config value normalization/clamping at startup and on config save.
