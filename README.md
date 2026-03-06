# Compost Bin

A Vintage Story mod that adds a craftable compost bin — a barrel-style block that accelerates organic decay and converts rot into compost.

## Features

- **8 inventory slots** — accepts any perishable item, plus dry grass, cattail tops, papyrus tops, and thatch
- **1.5x rot speed** — unsealed bins accelerate the Perish transition by 50%
- **Dry offering decomposition** — dry grass, cattail tops, papyrus tops, and thatch decompose into rot over 48 hours (32 effective hours at accelerated speed)
- **Sealing mechanic** — when all slots contain rot and the total reaches 64+, the bin can be sealed to begin composting
- **4:1 compost ratio** — after 480 game hours sealed, consumes 64 rot and produces 16 compost
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

## Installation

1. Download the latest release (`compostbin-v1.0.0.zip`) from the [Releases](https://github.com/Mysticode-Games/VS-Compost-Bin/releases) page
2. Place the zip file in your Vintage Story `Mods` folder:
   - Windows: `%appdata%/VintagestoryData/Mods/`
   - Linux: `~/.config/VintagestoryData/Mods/`
   - macOS: `~/Library/Application Support/VintagestoryData/Mods/`
3. Launch the game — no extraction needed, VS loads zips directly

## Compatibility

- Vintage Story **1.20.0** or later
- Works in both singleplayer and multiplayer (required on client and server)

## License

[MIT](LICENSE)
