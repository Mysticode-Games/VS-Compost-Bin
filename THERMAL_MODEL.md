# Compost physics (1.3.0)

One temperature per bin. The model uses consistent game units, not measured material properties. It borrows the heat-generation, heat-loss, moisture and aeration principles described by [Cornell](https://compost.css.cornell.edu/physics.html). Achievable self-ignition in barrel clusters and the 120 C handoff to vanilla fire are deliberate game abstractions.

## State and integration

The server coordinates loaded bins every two real seconds. It advances elapsed game hours in steps no larger than 1/120 hour, calculating all neighboring temperatures from the same snapshot before applying results. A maximum of two game hours is processed per callback after exceptional calendar jumps. Unloaded bins do not receive offline heat or brown-decomposition catch-up. Greens retain native calendar-based spoilage, including native catch-up; sealed conversion still uses its existing absolute calendar deadline.

Each item is 1/64 dry-mass unit, independent of its stack limit, so splitting, merging and one-for-one conversion to rot do not change mass. This intentionally avoids pretending to know ingredient densities. The wood barrel contributes 2 heat-capacity units. Contents contribute `1.5 * dryMass + 4.2 * water`. Initial water/dry-mass ratios are 0.12 for supported browns and 0.8 for other accepted material.

Each step computes biological heat + chemical heat - face losses - evaporative losses, then divides by heat capacity to change temperature. Microbial activity depends on actual contents temperature, oxygen, moisture and the green/brown mixture. It peaks around 50-65 C and reaches zero at 75 C, consistent with the game's native hot-item perish cutoff. Chemical heating begins at 60 C, accelerates with temperature, requires oxygen and relatively dry browns, and consumes finite browns fuel. Fractional fuel use is accumulated until a whole item can be removed; no endless heat from an inexhaustible fuel flag.

## Six faces

Exposed-face coefficients: top 1.1, each side 1.0, bottom 0.1. These model an open top, barrel walls, and an insulated base, respectively. Non-bin neighbors use ambient climate temperature. Solid terrain does not get its own thermal state.

Adjacent loaded bins exchange heat using the two face resistances in series: `1 / (1/k1 + 1/k2)`. Equal and opposite energy transfers conserve heat, including vertically stacked bins. Cooler or empty bins can absorb heat. No neighbor-count ignition gate exists. Burning bins hand over to vanilla burning and stop participating in this compost simulation.

## Items, actions and native systems

- Greens retain native perish transitions; the bin supplies their decomposition multiplier.
- Supported vanilla browns without an existing perish definition receive a native 144-effective-hour transition to rot. Its rate is zero outside an unsealed compost bin. Its clock is frozen during outside/transfer updates, so chest time cannot become composting time on reinsertion. Existing perish definitions from other mods are respected.
- Native temperature and transition state travel with stacks. Native merges average temperature and decomposition progress. Large differences can require direct/manual merging under vanilla rules.
- Portable moisture is an additional value inside the native temperature subtree, which is already excluded from stack equality. A narrow merge patch averages it without bypassing native merge rules.
- Two narrow Harmony patches use the game's bundled Harmony: pause our added brown transition outside the bin; preserve moisture during native merging. Client setup repeats after server collectibles arrive, using a synchronized collectible attribute to identify the added brown transition.
- Removed items use native item cooling, not a second ambient-aware cooling simulation. Native cooling can slightly lower stack temperatures between server updates; this is an accepted simplification.
- Turning exchanges 15% of the temperature difference with outside air and replenishes oxygen. The hint appears when aeration is below 90% and greens remain.
- Watering adds up to 2 water units at ambient temperature, mixed by heat capacity. It grants no timer immunity. Water/dry-mass ratio is capped at 4.
- Native `TemperatureSensitive` rain/immersion callbacks add water and cooling for hot unsealed bins. As in that native behavior, cold bins do not accumulate rain moisture. The existing watering-can interaction and water cost are retained.
- Sealing still requires only rot, at least 64 items, and takes 480 game hours at a 4:1 rot/compost ratio. Sealed bins exchange residual heat but generate no biological/chemical heat and cannot self-ignite.
- At 120 C, moisture below 35%, oxygen at least 10%, and sufficient remaining browns, the bin starts the already-tested native burning behavior and fire above where possible.

## Calibration

Reference scenarios use full bins with 5.3 green and 2.7 brown mass units, 4.56 water units, 80% initial aeration, no rain or intervention, and continued availability of greens. Native food with shorter freshness can finish rotting earlier and prevent ignition. These are simulation results, not guarantees for every ingredient mix.

| Actual layout | Ambient | Seven-day outcome / first ignition |
|---|---:|---|
| 2 x 2 (two shared sides each) | 20 C | No ignition; peak about 59 C |
| 2 x 2 | 35 C | No ignition; peak about 74 C |
| 2 x 2 | 45 C | Ignition about 15 game hours |
| 2 x 8 (interior bins have three shared sides) | 20 C | Ignition about 35 game hours |
| 2 x 3 | 20 C | No ignition; cooler edges limit peak to about 73 C |
| 3 x 3 | 20 C | Ignition about 24 game hours |

Identical warm neighbors on three sides reach ignition after about 34 hours at 20 C. Actual edge cooling matters, as the 2 x 3 case demonstrates.

## Migration and validation

Old stored pile temperatures survive. Old slot `dryDecomposition` progress migrates into native stack transition state; earlier stack-based progress is also recognized. Old countdown/protection fields are retired. Moisture and aeration use documented defaults when absent.

Build the release DLL first, then run from the project directory:

```powershell
dotnet build -c Release "-p:VintageStoryPath=$env:APPDATA\VintagestoryPre"
dotnet run --project tests/PhysicsChecks
dotnet run --project tests/PhysicsTuning
```

The checks exercise native brown pause/resume and splitting/merging, retained heat, moisture averaging, old-progress migration, sealing locks, energy conservation, integration step convergence, watering/turning and native burning with open/blocked tops. Startup and server-update checks cover calendar availability and neighboring-barrel heat transfer in all six directions. The tuning harness prints both shared-face reference scenarios and actual rectangular clusters. In-game testing of the release candidate confirmed that a loaded 3 x 3 cluster ignited after about two game days of neglect, while a 2 x 3 cluster became hot without igniting. These observations apply to the tested conditions, not every ingredient mix or climate.
