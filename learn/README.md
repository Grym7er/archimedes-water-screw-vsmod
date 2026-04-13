# Learn: Vintage Story Modding with Archimedes Screw

This folder is a step-by-step tutorial for building a mod like this one from scratch.  
Audience: developers who know programming, but may be new to C# and Vintage Story APIs.

## Recommended Learning Path

1. [00 - Prerequisites and Tooling](./00-prerequisites-and-tooling.md)
2. [01 - Mod Structure and Metadata](./01-mod-structure-and-metadata.md)
3. [02 - Assets, Blocktypes, and Recipes](./02-assets-blocktypes-and-recipes.md)
4. [03 - ModSystem Lifecycle](./03-modsystem-lifecycle.md)
5. [04 - Block vs BlockEntity Design](./04-block-vs-blockentity-design.md)
6. [05 - Assembly Validation and Gameplay Rules](./05-assembly-validation-and-gameplay-rules.md)
7. [06 - Global Water Network Manager](./06-global-water-network-manager.md)
8. [07 - Fluid Conversion, Relays, and Cleanup](./07-fluid-conversion-relays-and-cleanup.md)
9. [08 - Persistence, Config, and Compatibility](./08-persistence-config-and-compatibility.md)
10. [09 - Debugging, Testing, and Packaging](./09-debugging-testing-and-packaging.md)
11. [10 - Build Your Own Variant](./10-build-your-own-variant.md)

## How to Use These Chapters

- Read chapters in order.
- Implement each chapter's concepts in your own small test mod.
- Validate in-game frequently instead of waiting until the end.
- Keep one terminal for build output and one for notes/checklists.

## Outcome

By the end, you should be able to:

- structure a Vintage Story code mod,
- combine JSON assets with C# runtime systems,
- build machine-style gameplay logic,
- persist custom world state safely,
- ship a debuggable, configurable mod.
