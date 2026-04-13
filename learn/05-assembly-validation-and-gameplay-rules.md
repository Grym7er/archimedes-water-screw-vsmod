# Chapter 05: Assembly Validation and Gameplay Rules

Multiblock validation is centralized in `src/Systems/ArchimedesScrewAssemblyAnalyzer.cs`.

## What `Analyze(...)` Validates

Given any screw block position, it checks:

1. resolve bottom and full vertical stack,
2. exactly one intake, and it must be bottom-most,
3. exactly one outlet (optional in some messaging paths), and if present it must be top-most,
4. middle sections must be straight-only,
5. output cell must be clear,
6. mechanical network speed must exceed minimum.

It returns an `AssemblyStatus` object with booleans and a user-readable message.

Walkthrough of the method:

1. Verify clicked block type (`Analyze` lines 22-27).
2. Find bottom of stack by scanning downward (lines 29-33).
3. Build entire vertical stack by scanning upward (lines 35-41).
4. Validate intake/outlet cardinal rules (lines 48-123).
5. Validate middle segments are not directional end blocks (lines 125-139).
6. Compute output position (lines 141-159).
7. Validate output cell fluid/solid compatibility (lines 161-176 and helper at 225-233).
8. Evaluate power state from mechanical behavior (lines 178-199).
9. Return functional/non-functional status with rich message (lines 201-223).

## Why a Dedicated Analyzer Exists

- Same rules are needed in multiple places (interaction, runtime).
- Keeps validation deterministic and testable.
- Prevents duplicated "almost same but slightly different" rule code.

You can see this reused from interaction in `BlockWaterArchimedesScrew.OnBlockInteractStart(...)` at lines 214-217.

## Practical Gameplay Effects

- Players can right-click to get assembly status quickly.
- Misconfigured builds fail with specific messages.
- Runtime logic only pumps when structure and power are valid.

This improves player trust: failures are not silent, and the message explains what to fix.

## Design Tip for Your Mod

For any non-trivial machine:

- write one analyzer/service that computes "valid + actionable output",
- return structured status (not just `true/false`),
- include failure messages for user feedback and logs.

Use status objects with fields like:

- `IsAssemblyValid`,
- `IsFunctional`,
- `Message`,
- positional hints (`InputPos`, `OutputPos`).

That gives both user-facing and system-facing context without repeated scans.

## Common Error Cases to Test

- two intakes in one stack,
- outlet not at top,
- blocked output position,
- powered off mechanical network.
- intake in wrong fluid type.

Next: the global manager that schedules all active controllers.
