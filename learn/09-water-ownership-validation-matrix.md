# Water Ownership Validation Matrix

This matrix validates the unified (non-legacy) water ownership architecture:

- frontier-limited vanilla claiming,
- deterministic claim-domain arbitration,
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
   - Expect lake body to remain largely vanilla/unowned.
   - Expect policy rejection counters (`detectedVanillaBody`) to rise.

3. Two controllers in shared component (same family)
   - Expect deterministic single-writer claim behavior.
   - Expect no ownership flip-flop between controllers over repeated ticks.

4. Two controllers different families nearby
   - Expect no cross-family claim assignment.
   - Expect each family remains isolated under policy checks.

5. Player bucket placement next to managed water
   - Expect conversion intent queued, then converted by central tick.
   - Expect ownership assignment without immediate reversion churn.

6. Power loss and recovery
   - Expect unsupported managed sources to drain.
   - Expect resumed claims after power restore to follow frontier policy.

7. Chunk boundary behavior
   - Move player to unload/reload neighboring chunks around managed network.
   - Expect ownership/provenance persistence and deterministic reassignment.

8. Save/reload cycle
   - Save world with active managed network.
   - Reload and verify ownership/provenance persistence consistency.

## Pass criteria

- Build succeeds with zero compile errors.
- No persistent ownership oscillation under contention.
- Natural vanilla bodies are not broadly seized.
- Player-intent conversions continue to work.
- No unowned managed-source accumulation over steady-state runtime.
