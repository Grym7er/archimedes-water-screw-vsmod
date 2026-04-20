# Packed Position Key Migration Notes

## Scope

This note captures lightweight verification after migrating hot-path runtime position keys to packed `long` keys.

## Verification run

- Command: `dotnet build`
- Result: build succeeded for all target frameworks, `0` warnings / `0` errors.

## Migration evidence (static counters)

- Remaining `PosKey(...)` usages in `src/`: `3`
  - `ArchimedesWaterNetworkManager.PosKey` method itself
  - `ArchimedesWaterNetworkManager.TryParsePosKey` boundary parser
  - one ownership log formatting call in `ArchimedesWaterNetworkManager.Ownership.cs`
- Packed-key usage in systems (`ArchimedesPosKey.Pack`, `Dictionary<long,...>`, `HashSet<long,...>`): broadly adopted across manager, policy, purge, ownership, and debug paths.

## Notes

- This repository does not include a separate test project; runtime safety checks were added in `ArchimedesPosKey.InitializeForWorld()` to validate pack/unpack bijection on initialization boundaries.
- Hot-path functional verification currently relies on successful compile and existing runtime behavior guards.
