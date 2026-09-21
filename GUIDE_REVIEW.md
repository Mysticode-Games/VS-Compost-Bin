# Guide review — verified in 1.3.5

Reviewed against `../ref/vs-modding-reference.md` (Vintage Story Modding Technical Reference, February 2026). The guide is a reference and convention document; some examples target older game versions. API-sensitive changes were compiled and tested against the installed Vintage Story 1.22.7 assemblies. Example identifiers belonging to other mods were not copied into this mod.

## Correctness findings fixed

| Finding | Guide basis | Resolution |
|---|---|---|
| Inventory/open/seal packets trusted the normal right-click claim check; direct packets bypassed it. Missing players were also dereferenced. | 1.2 server authority; 8.1 side awareness; 8.2 API null checks | Packet handlers now require server execution and a valid player inventory manager, and recheck claim access before forwarding inventory data or opening/sealing. Closing remains allowed. Matches the native openable-container permission pattern. |
| Barrel breaking spawned inventory drops without a side check. | 8.1 world mutations belong on the server | Inventory drops occur only on the server. Client cleanup still closes the window. |
| Each ModSystem disposal unpatched the shared Harmony owner, even if another instance was still active in the same process. | 4.1 lifecycle; 7.2 patch scope and disposal | Mod instances acquire/release a shared patch lease. Only the last release unpatches. Repeated disposal is safe. Both patches have an explicit `compostbin` category and source references. |
| Conversion deleted rot before validating the compost item; a nonpositive output stack size could prevent the output loop from progressing. | 8.2 graceful failure and API checks | Validate the output first. Preserve rot and unlock the barrel with a warning if it is unavailable or invalid. |
| Math.Clamp did not reject NaN saved temperatures, oxygen, consumption fractions, or moisture, allowing invalid values into neighboring heat calculations. | 5.2 persistence recovery; 8.2 graceful failure | Recover non-finite stored values to defaults, bound valid numeric state, and sanitize portable moisture. Invalid block state produces warnings. |

## Patterns checked and retained

- **Loading:** class registration in Start; collectible modifications in AssetsFinalize; world settings applied after SaveGameLoaded; calendar first accessed after it becomes available.
- **Client/server:** the coordinator simulates on the server; particles and GUI run on the client. Native item transition evaluation remains available on both sides as required by inventory display/prediction.
- **Persistence and synchronization:** block state uses tree serialization; settings use the native config file and synchronized world configuration; inventory state uses native serialization.
- **Resources:** game tick listeners and event handlers are removed; windows dispose after close callbacks; block entities unregister on removal/unload/break. Tessellation uses the supplied worker tessellator and fresh mesh data.
- **Dependencies and packaging:** game-provided assemblies have Private=false; distributable ZIP contains the built DLL, modinfo, assets, and documentation with forward-slash paths. No game/Harmony assemblies are bundled.
- **Native behavior reuse:** native perish progress, item heat, merging, burning, environmental cooling, and inventory networking remain in use.
- **Localization:** GUI/hover/interaction messages use language keys. Logger messages identify this mod.

## Guide-conformance cleanup completed

- Organized source into Block, BlockEntity, Client, Configuration, Inventory, Patches, Physics, and Systems directories.
- Separated the Harmony patch classes and setting-range attribute into their own files.
- Extracted server simulation into CompostPhysicsCoordinator and collectible setup into CompostMaterialRegistry. The ModSystem now delegates those responsibilities.
- Standardized using order, explicit nullable directives, Allman formatting, API field names, and lifecycle handler names. Added .editorconfig and verified it with dotnet format.
- Replaced decorative GUI comments with descriptions of behavior. The turning timestamp is now a private implementation field.

The block entity intentionally retains a partial physics file so inventory/presentation and simulation code remain separately readable; both files contain the same type. Nested state structs remain with their owning classes. Existing namespaces, registered class identifiers, config names, packet IDs, and saved-state keys are preserved for compatibility.

## Validation and limits

Release build: zero warnings/errors. All physics/native transition, fire, item packet, lifecycle, neighbor transfer, config, custom-settings, and smoldering checks pass after the refactor, including the 16 review checks covering shared patch lifetime, denied/allowed packets, server-only drops, missing/invalid output items, and non-finite saved values. The physics tuning harness retains all six expected cluster outcomes and the previous printed temperature/ignition results. `dotnet format whitespace compostbin.csproj --no-restore --verify-no-changes` passes. Tests use actual game APIs and selected native implementations with stubbed world services; they do not replace an in-game multiplayer regression pass. No gameplay coefficients or config defaults changed.
