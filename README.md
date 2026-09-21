# Compost Bin

A Vintage Story mod that adds a craftable compost bin — a barrel-style block that accelerates organic decay and converts rot into compost.

## Features

- **8 inventory slots** — accepts any perishable item, plus dry grass, cattail tops, papyrus tops, and thatch
- **Physics-based decomposition** — temperature, moisture, aeration and the mixture determine the native spoilage multiplier
- **Portable heat and decomposition** — supported browns decompose only in the bin, but retain native progress when removed; item heat and moisture also survive transfers
- **Neighboring-bin heat exchange** — heat escapes through six faces or transfers to adjacent compost bins; neglected clusters can ignite
- **Configurable peat booster** — peat bricks briefly accelerate decomposition, increase heat, and retain aeration after turning; excess peat makes hot, dry piles more dangerous. Peat decomposes quickly with very little rot output.
- **Sealing mechanic** — when all slots contain rot and the total reaches 64+, the bin can be sealed to begin composting
- **4:1 compost ratio** — after 480 game hours sealed, converts the rot into compost (64 rot yields 16 compost)
- **Sealed bins pause decay** — sealing halts all perish transitions while composting proceeds

## Crafting Recipe

```
S P S
G P G
S P S
```

- **S** = Stick
- **P** = Any plank
- **G** = Dry grass

## Accepted Items

| Category | Items |
|----------|-------|
| Perishable | Any item with a Perish transition (meat, fruit, vegetables, grain, etc.) |
| Dry offerings | Dry grass, cattail tops, papyrus tops, thatch |
| Decay products | Rot, compost |
| Booster | Peat bricks |

## Installation

1. Obtain the `compostbin-v1.3.8.zip` package for Vintage Story 1.22.
2. Place the zip file in your Vintage Story `Mods` folder:
   - Windows: `%appdata%/VintagestoryData/Mods/`
   - Linux: `~/.config/VintagestoryData/Mods/`
   - macOS: `~/Library/Application Support/VintagestoryData/Mods/`
3. Launch the game — no extraction needed, VS loads zips directly

## Configuration

Starting a world or dedicated server creates `ModConfig/compostbin.json` in its Vintage Story data folder. On a standard Windows installation this is `%APPDATA%/VintagestoryData/ModConfig/compostbin.json`; on Linux it is `~/.config/VintagestoryData/ModConfig/compostbin.json`. A custom data folder changes this location.

```json
{
  "CompostingDurationHours": 480.0
}
```

This minimal example retains all other defaults. The generated file includes 37 controls for compost time and yield, decomposition rates, green/brown balance, peat dosage and effects, heat sources, face conductance, neighboring heat exchange, evaporation, aeration, self-ignition, smoldering, turning, and watering. Disabling self-ignition replaces spreading fire with smoky destruction of greens, browns, and peat inside the barrel. See [CONFIG.md](CONFIG.md) for every default, allowed range, formula, and effect, or [config.example.json](config.example.json) for the complete file. Bins without peat retain their existing behavior.

Edit the file with the world/server stopped, then reload it. In multiplayer, edit the server's file; clients receive its settings through the game's world configuration. Duration and yield settings apply to existing sealed batches too. Existing valid configs gain missing fields while retaining custom values. Invalid settings produce log warnings and fall back to defaults without overwriting the file.

## Compatibility

If editing a saved world's settings crashes with a JSON error at `compostbin:settings`, install 1.3.7 or later, enter the world normally, then save and exit before opening its edit screen. Loading the world replaces the old settings value with a safe encoding; the next save persists it. Back up the save first. The mod cannot repair it from the main-menu edit screen because it has not loaded there yet.

- Current builds target Vintage Story **1.22.x** (built and API-checked against **1.22.7**).
- Use the older 1.2.0 release for Vintage Story 1.21.x.
- Works in both singleplayer and multiplayer (required on client and server)

## Building

Requires the .NET 10 SDK and a Vintage Story 1.22 installation. The default game path is `%APPDATA%\Vintagestory`. To build against a separate installation in PowerShell:

```powershell
dotnet build -c Release "-p:VintageStoryPath=$env:APPDATA\VintagestoryPre"
```

Package `bin/Release/compostbin.dll` with `modinfo.json`, `modicon.png`, and `assets/`. Do not use the old DLL in the project root.

See [THERMAL_MODEL.md](THERMAL_MODEL.md) for the physics assumptions, calibration results, save migration, and test commands. The game supplies Harmony and Newtonsoft.Json; neither library is bundled in the mod ZIP.

## License

[MIT](LICENSE)
