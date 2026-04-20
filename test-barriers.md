# Relay Barrier Rule In-Game Test Plan

This plan verifies relay candidate gating after the barrier alignment change:

- below block UP-face barrier must be `>= 1f`,
- below solid and below fluid layers must both be non-water (vanilla and Archimedes families),
- existing relay creation pipeline (ordering/caps/cooldowns) remains unchanged for still-valid candidates.

## Prerequisites

- Use a test world with creative access.
- Enable debug overlay tools used by the mod (`debugwater` command path in this repo).
- Use a known config profile (record values before each run):
  - `EnableRelaySources=true`
  - `MaxRelayPromotionsPerTick=1` (or known value)
  - `MaxRelaySourcesPerController` high enough to observe multiple promotions
  - `FastTickMs` low enough for quick iteration
- Keep world/chunk loading stable (avoid cross-chunk unload while executing single-case assertions).

## Global Observability

For every case, capture:

1. screenshot/video with debug overlay around the candidate cell,
2. whether candidate appears as relay-eligible in overlay,
3. whether relay promotion occurs within expected ticks,
4. controller stats/log lines if available (`relayPromotions`, rejection counters),
5. final fluid state at candidate and below-candidate cells.

Use the same observation window per test (for example, 20-30 fast ticks).

## Test Matrix

### Case A: Positive baseline (sealed floor, no below water)

Setup:

- Build a normal channel where relay previously worked.
- Candidate cell has valid relay fluid form.
- Below solid block exists and is a full barrier floor.
- Below fluid layer is empty.

Expected:

- Candidate is shown as relay-eligible.
- Relay promotes within cap/budget timing.
- No regressions in ordering or cadence.

### Case B: Reject when below is air

Setup:

- Same as Case A, but remove block directly below candidate.

Expected:

- Candidate is not relay-eligible.
- No promotion at this cell.

### Case C: Reject when below UP-face is fluid-passable

Setup:

- Use a below block known to have a passable UP face (slab/geometry block or other leaky host).
- Keep all other conditions same as Case A.

Expected:

- Candidate is not relay-eligible.
- Overlay should not mark this location as candidate.
- Relay should skip this cell and only promote elsewhere if alternatives exist.

### Case D: Reject when below solid is vanilla water

Setup:

- Force below solid block to a vanilla water block family cell.

Expected:

- Candidate is not relay-eligible.
- No promotion at this location.

### Case E: Reject when below solid is Archimedes managed water

Setup:

- Force below solid to managed Archimedes water family.

Expected:

- Candidate is not relay-eligible.

### Case F: Reject when below fluid layer has vanilla water but solid layer is non-water

Setup:

- Keep below solid as sealed non-water block.
- Put vanilla water in below fluid layer only.

Expected:

- Candidate is not relay-eligible (new behavior).

### Case G: Reject when below fluid layer has managed Archimedes water but solid layer is non-water

Setup:

- Keep below solid as sealed non-water block.
- Put managed Archimedes water in below fluid layer only.

Expected:

- Candidate is not relay-eligible (new behavior).

### Case H: Multi-family coverage

Repeat Cases D-G for each family:

- fresh,
- salt,
- boiling.

Expected:

- Same rejection behavior across all families.

## Regression Checks (unchanged systems)

Run these on a topology that still satisfies new candidate rules.

### Case R1: Promotion ordering unchanged

Setup:

- Create multiple valid candidates at varying distances.
- Run in deterministic ordering mode.

Expected:

- Promotion order still follows existing distance/ordering rules.

### Case R2: Cap and trim unchanged

Setup:

- Start with enough valid candidates to exceed current effective cap.
- Adjust power to force trim and then re-expand.

Expected:

- Trim still follows existing logic.
- Re-promotion still follows existing cooldown and budget behavior.

### Case R3: Cooldown + quarantine behavior unchanged

Setup:

- Force release and quick re-attempt promotion at same position.

Expected:

- Local cooldown/quarantine suppression still occurs exactly as before.

## Stress and Edge Tests

### Case S1: Mixed floor striping

Setup:

- Alternate sealed and leaky blocks under a long candidate path.

Expected:

- Promotions only occur above sealed non-water-below cells.
- Pattern is stable across repeated runs.

### Case S2: Chunk boundary stability

Setup:

- Place candidate paths across chunk boundaries.
- Keep both chunks loaded during assertion, then repeat with unload/reload.

Expected:

- Eligibility decisions remain consistent after reload.
- No false positives introduced by cache/load transitions.

### Case S3: Truncation environment sanity

Setup:

- Large network near BFS cap where relay maintenance may pause.

Expected:

- New barrier rule does not produce unexpected promotions when maintenance resumes.
- Behavior matches existing truncation semantics plus stricter eligibility.

## Pass/Fail Criteria

A build passes when all are true:

- All positive cases promote as expected.
- All negative cases never promote at disallowed locations.
- Family variants behave identically.
- No regressions detected in ordering/cap/cooldown/quarantine behavior on valid candidates.
- No visual desync between debug overlay candidate state and actual promotion outcomes.

## Suggested Evidence Bundle

- One short clip per matrix case (A-H, R1-R3, S1-S3).
- A table with columns:
  - case id,
  - setup hash/config,
  - expected,
  - actual,
  - pass/fail,
  - notes.
- Any relevant server logs or perf counters attached to failed cases.
