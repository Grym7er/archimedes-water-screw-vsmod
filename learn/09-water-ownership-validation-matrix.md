# Water Ownership Validation Matrix

This matrix validates the unified (non-legacy) water ownership architecture:

- strict first-touch vanilla locking,
- barrier-passability-based touching (blocked liquid interfaces are non-touching),
- territorial first-claim ownership (sticky until drained; lowest controller id breaks same-tick contention),
- provenance tracking,
- conversion-intent queue processing.

## Baseline setup

- Enable server logs.
- Use at least two powered controllers with the same family in one world.
- Keep `debugwater` commands available for spot checks.

## Scenarios

1. Single controller, small channel expansion
   - Expect nearby vanilla sources to convert when supported by managed frontier.
   - Expect converted cells to receive ownership and provenance.

2. Single controller, flow into large lake
   - Expect lake body to remain completely vanilla/unowned at lock-captured boundary.
   - Expect lock rejection counters (`lockedVanilla`) to rise.

3. Drain after lake contact
   - Run stream until contact with pre-existing lake, then remove power/force drain.
   - Expect pre-existing locked lake cells to remain unchanged before/after drain.

4. Two controllers in shared component (same family)
   - Expect deterministic single-writer claim behavior: whichever controller claims a cell first retains it until it drains.
   - Expect no ownership flip-flop between controllers over repeated ticks.
   - On simultaneous same-tick claims, expect the lowest-ordinal controller id to win.

5. Two controllers different families nearby
   - Expect no cross-family claim assignment.
   - Expect each family remains isolated under policy checks.

6. Player bucket placement next to managed water (passable interface)
   - Expect conversion intent queued, then converted by central tick.
   - Expect ownership assignment without immediate reversion churn.

7. Player bucket placement with blocked interface (aqueduct wall/floor separation)
   - Place vanilla source coordinate-adjacent to managed water but with a non-passable surface between them.
   - Expect no conversion intent side effects and no claim/conversion.

8. Power loss and recovery
   - Expect unsupported managed sources to drain.
   - Expect resumed claims after power restore to follow frontier policy.

9. Chunk boundary behavior
   - Move player to unload/reload neighboring chunks around managed network.
   - Expect ownership/provenance/vanilla-lock persistence and deterministic reassignment.

10. Save/reload cycle
   - Save world with active managed network.
   - Reload and verify ownership/provenance/vanilla-lock persistence consistency.

11. Corrupt block entity payload resilience
   - Inject malformed BE arrays (or reproduce from broken save) for owned/relay/seed payloads.
   - Expect decode to skip corrupt payload with warning and no client crash.

## Pass criteria

- Build succeeds with zero compile errors.
- No persistent ownership oscillation under contention.
- Locked vanilla bodies are never converted.
- Blocked interfaces (side or vertical) never count as touching for convert/seize/connectivity.
- Player-intent conversions continue to work.
- No unowned managed-source accumulation over steady-state runtime.
- Malformed BE payloads do not crash client chunk packet handling.
