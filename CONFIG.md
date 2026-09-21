# Compost Bin configuration

Start a world/server once to create `ModConfig/compostbin.json` inside its Vintage Story data directory. Stop the world/server before editing; reload it to apply changes. The server's settings are sent to clients by the game's world configuration, including values used by the window, hover text, thermometer, and interaction hints. Clients do not load their own config in multiplayer.

Version 1.3.6 adds a normal-spoilage floor for unsealed bins below 75°C. Existing valid configs are expanded with new fields while retaining customized values. Invalid numbers fall back to the affected setting's default and leave the file unchanged, with warnings in the log. Unreadable JSON falls back to all defaults and is also left unchanged. See [config.example.json](config.example.json) for a complete default file.

## Compost and decomposition

| Setting | Default | Range | Effect |
|---|---:|---|---|
| `CompostingDurationHours` | 480 | 1–87600 | In-game hours from sealing to finished compost. |
| `MinimumRotToSeal` | 64 | 1–512 | Minimum rot required, with no other items present. Also requires enough rot for at least one compost. |
| `RotPerCompost` | 4 | 1–64 | Rot consumed per compost produced. Smaller values increase yield. Output rounds down, with at least one compost for an already-sealed batch containing rot. |
| `DecompositionSpeedMultiplier` | 1 | 0–20 | Multiplies decomposition of greens, browns, and peat; 2 doubles speed at the same pile conditions. 0 pauses decomposition. |
| `BrownDecompositionSpeedMultiplier` | 1 | 0–20 | Additional multiplier for the native brown transition added by this mod. 0 pauses that transition. Does not override another mod's existing perish definition. |
| `IdealGreenBrownRatioMin` | 1 | 0.1–20 | Lower end of the green/brown ratio with full biological activity. |
| `IdealGreenBrownRatioMax` | 4 | 0.1–20 | Upper end of that range; must be at least the minimum. |

Speed multipliers change rot production, not heat production directly; heat has its own settings below. Below 75°C, unsealed bins use at least 1× base spoilage before applying the configured speed multipliers. Cold, dry, poorly aerated, or unbalanced contents therefore cannot reduce the rate below normal with default settings. Admin multipliers below 1 can deliberately slow or pause it. Temperature, moisture, aeration, and mixture still affect biological heating without this floor. Spoilage still stops at 75°C and above, while sealed, burning, or smoldering. Supported browns still pause outside the bin. Duration and yield changes apply to existing sealed batches; elapsed time is retained, so reducing the duration can finish a batch on its next update.

## Peat booster

Peat bricks are a separate additive, not greens or browns. Dosage counts each stack as `item count / that item's maximum stack size`, then divides peat stack equivalents by the total green and brown stack equivalents. Rot and compost are excluded. Vanilla peat stacks to 32: 16 bricks with seven full organic stacks is `0.5 / 7 = 0.0714285714`. Smaller batches scale proportionally; four bricks with 1.75 organic stacks gives the same dose. Splitting a stack across slots does not change the ratio.

| Setting | Default | Range | Effect |
|---|---:|---|---|
| `PeatOptimalRatio` | 0.07142857142857142 | 0.0001–10 | Peat stacks / organic stacks at which useful bonuses reach their cap and excess heating starts. Set to `1 / 7 = 0.14285714285714285` to move the target to a full peat stack per seven organic stacks. Enter the decimal value in JSON, not the division expression. |
| `PeatDecompositionBonus` | 0.25 | 0–20 | Maximum additive bonus to the decomposition multiplier: 0.25 gives 25% faster rot production at the same conditions. |
| `PeatBiologicalHeatBonus` | 0.25 | 0–20 | Maximum additional fraction of biological heat. Inactive material still generates no biological heat. |
| `PeatAerationDurationHours` | 12 | 0–87600 | Game hours after turning over which the aeration benefit fades linearly. Zero disables it. |
| `PeatAerationRetention` | 0.5 | 0–1 | Maximum reduction in biological oxygen consumption immediately after turning; 0.5 halves it. Chemical oxygen consumption is unchanged. |
| `PeatExcessHeatMultiplier` | 3 | 0–100 | Extra chemical heating per unit of dose above the target. At twice the target, 3 gives `1 + 3 = 4` times chemical heating and brown fuel consumption. Zero disables the excess penalty. |
| `PeatDecompositionHours` | 6 | 0.1–87600 | Effective decomposition hours for peat to become rot. Actual loaded time is divided by the bin's decomposition rate, including its peat bonus. |
| `PeatRotYield` | 0.0625 | 0–1 | Rot produced per decomposed peat brick. Default gives 1 rot from 16 bricks, or 2 from 32. Fractional output uses the game's randomized rounding; zero produces no rot. |

For calculation, let `dose = (peatStacks / organicStacks) / PeatOptimalRatio`. Speed and biological heat multipliers are `1 + min(dose, 1) * bonus`. Excess chemical heat is multiplied by `1 + max(dose - 1, 0) * PeatExcessHeatMultiplier`, under the existing hot/dry/brown-fuel/oxygen conditions. Peat alone grants no bonus. Set both bonus settings, aeration retention, and excess heat multiplier to zero to disable the effects while retaining peat decomposition.

Peat uses portable native perish progress: it advances only in a loaded, unsealed, non-burning, non-smoldering bin below 75°C. The full remaining stack converts when its progress completes; fresh additions dilute progress through native stack merging. It does not rot in a chest. Its dose-based benefits stop when it finishes or is removed; turning again refreshes, rather than stacks, the aeration window. At default settings an optimal dose lasts at most 4.8 loaded game hours below 75°C (6 / 1.25), and about 1.6 at peak activity (6 / 3.75). The 12-hour turning window is an upper bound if peat remains or is replenished.

These are gameplay coefficients. A warm dry reference bin at 65°C distinguishes the half-stack and full-stack hazard; a full stack is not an unconditional ignition trigger. Wetting, ambient temperature, neighbors, organic decay, and the peat's short lifetime all affect the result. Smoldering also consumes peat when self-ignition is disabled. Sealed composting duration and yield are unaffected. If another mod already supplies peat's perish transition, its transition and yield are respected instead of replaced.

## Heat, moisture, and aeration

| Setting | Default | Range | Effect |
|---|---:|---|---|
| `BiologicalHeatMultiplier` | 1 | 0–5 | Scales heat generated by biological activity; 0 disables this source. |
| `ChemicalHeatMultiplier` | 1 | 0–5 | Scales high-temperature heating and the brown fuel it consumes; 0 disables both. |
| `TopHeatConductance` | 1.1 | 0.01–10 | Heat transfer through the top face. Higher values transfer heat faster. |
| `SideHeatConductance` | 1 | 0.01–10 | Same coefficient for each of the four side faces. |
| `BottomHeatConductance` | 0.1 | 0.01–10 | Heat transfer through the bottom face. |
| `EnableNeighborHeatExchange` | true | true/false | If false, every face exchanges with ambient air instead of neighboring barrels; this also removes cluster insulation. |
| `NeighborHeatExchangeMultiplier` | 1 | 0–5 | Scales transfer across shared faces. A value of 0 makes shared faces insulating; use the toggle above to make barrels behave independently. |
| `EvaporationMultiplier` | 1 | 0–10 | Scales moisture loss and the cooling caused by evaporation. |
| `AerationRecoveryMultiplier` | 1 | 0–10 | Scales passive oxygen replenishment. 0 leaves turning as the replenishment mechanism. |

Face coefficients affect ambient heating as well as cooling. Shared-face transfer remains equal and opposite, including between the different top and bottom coefficients. Increasing heat production or insulation can substantially change which cluster sizes ignite. Heat and brown decomposition are simulated only for loaded barrels.

## Fire and warning

| Setting | Default | Range | Effect |
|---|---:|---|---|
| `EnableSelfIgnition` | true | true/false | True starts native fire at ignition conditions. False replaces that fire with internal smoldering: greens, browns, and peat are destroyed, smoke is emitted, and the barrel survives. External fires and existing native fires are unaffected. |
| `SmolderConsumptionItemsPerHour` | 64 | 1–4096 | Total items destroyed per game hour by internal smoldering, shared across eligible slots. Only applies with self-ignition disabled; chemical brown fuel consumption is additional. |
| `IgnitionTemperature` | 120 | 80–400 °C | Minimum temperature for self-ignition; also sets the thermometer's full scale. |
| `IgnitionMaxMoisture` | 0.35 | 0.01–0.8 | Moisture must be below this fraction to ignite; 0.35 means 35%. |
| `IgnitionMinAeration` | 0.1 | 0–1 | Minimum oxygen fraction for ignition; 0.1 means 10%. |
| `OverheatingTemperature` | 70 | 40–120 °C | Warning and smoke threshold. Must not exceed ignition temperature. Does not itself trigger fire. |

Self-ignition still requires an unsealed barrel and remaining brown fuel. A blocked top does not prevent the barrel itself from burning. Native fire spread and burning behavior remain controlled by the game.

Internal smoldering begins at the same temperature, moisture, oxygen, and brown-fuel conditions as ignition. It continues while fuel remains, the pile stays at or above the overheating warning temperature, and moisture/oxygen still permit it. Cooling or wetting can quench it; after quenching, it must reach ignition conditions again to restart. Smoldering destroys greens, browns, and peat without producing rot, preserves existing rot and compost, and does not spawn a native fire in or above the barrel. Its state and fractional item consumption survive saves. The barrel remains accessible for watering or removing contents.

## Turning and watering

| Setting | Default | Range | Effect |
|---|---:|---|---|
| `TurningCoolingFraction` | 0.15 | 0–1 | Fraction of the difference from ambient temperature exchanged when turning; 0.15 means 15%. |
| `TurningAeration` | 1 | 0–1 | Oxygen level restored by turning. Turning never reduces an already higher oxygen level. |
| `TurningHintAerationThreshold` | 0.9 | 0–1 | Show the turning hint below this oxygen level when greens remain. Set at or below TurningAeration so a turn clears the hint. |
| `TurningDurabilityCost` | 1 | 0–100 | Shovel durability consumed per turn; 0 makes turning free. |
| `WateringAmount` | 2 | 0–32 | Water mass units added per watering-can interaction, subject to the pile's capacity. |
| `WateringCanSeconds` | 2 | 0.1–20 | Watering-can charge consumed per interaction, in the game's watering seconds. |
| `EnvironmentalWaterMultiplier` | 1 | 0–10 | Scales water/cooling supplied by native rain and immersion callbacks for hot unsealed bins. |

Examples: set `CompostingDurationHours` to 240 to halve the sealed wait; set `DecompositionSpeedMultiplier` to 2 to double rot production at a given activity; set `EnableSelfIgnition` to false to replace self-started fires with smoky destruction of the contents. `ChemicalHeatMultiplier = 0` disables the separate chemical heating/fuel-consumption process, but does not disable smoldering if the pile reaches its ignition conditions through other heat sources.
