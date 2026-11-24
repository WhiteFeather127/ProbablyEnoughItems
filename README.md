# Probably Enough Items

**English** | [简体中文](README.zh-CN.md)

A JEI-style item catalog for *Probably Stolen*. It lays every item in the game — including items added by other mods — into a panel docked to the right of the inventory, so you can see at a glance what each item looks like, where it comes from, and what it is used for.

> [!WARNING]
> **This project is heavily vibe-coded.** Most of it was produced by describing the intended behaviour, running the
> game and iterating, rather than by reading every line of the result. The code, the source tables in `Data/`, and
> these documents can contain wrong assumptions, outdated facts and plain mistakes — the item/source data is
> reverse-engineered from a moving target (game 0.46D). **Verify anything you intend to rely on**, and treat the
> documentation as a description of intent rather than a guarantee. Corrections and bug reports are welcome.

- Mod id: `probably_enough_items` · Assembly: `ProbablyEnoughItems.dll`
- Tested with game version **0.46D**

## Features

- **Full catalog**: scans every `ItemDirectory` the game registers, plus whatever other mods inject through the API; `UnusedDirectory` and debug items are hidden by default. Liquids (one full bottle each) and written DNA disks also get their own entries.
- **Search**: filters by name, item id, tag or item type as you type.
- **Native tooltips**: hovering goes through the game's own tooltip chain, with an extra `ID: xxx` line; containers also show their content and impurity ranges.
- **Sources and uses**: in the detail window, left-click shows *where this item comes from* (customers, shops, loot tables, machines, farming …), right-click shows *what it is used for*.
- **State expansion**: enumerable states become separate cells instead of cycling — cigarettes 5 (genuine + 4 flaw variants), immunity injectors 6 (genuine + 5 flaws), food stamps 30 (5 denominations × 6 states), crops 2–4 quality tiers, modules "footprint shape × effect" (11 effect cells), berries 6 forms. Continuously random values stay in one fixed cell and are **not** re-rolled.
- **Source pages**: one page per source, with the faction heading, the customer portrait (multiple looks cycling every second) and every item that source can put on the table; containers reflect the water **that specific customer** fills them with, down to the bottle body.
- **Cheat mode**: tick it and drag an item straight out of the catalog; drag an item onto the catalog to delete it.
- **Extension API**: other mods can register variants and extra entries through plain reflection, with zero dependencies; runtime-added items are picked up via `NotifyItemsChanged`, and `CatalogVariantKit` exposes the same enum tables and isolation helpers the catalog itself uses.

## Install

1. Copy (or build) the mod folder into `<game>\Mods\ProbablyEnoughItems\`
2. Launch the game → "Mods" menu → activate **Probably Enough Items** → restart
3. Load a save → a catalog button appears at the lower-right corner of the inventory

Building from source, folder layout, upgrading from the old version and log keywords are covered in [docs/BUILDING.md](docs/BUILDING.md).

## Documentation

| Document | Content |
|---|---|
| [docs/BUILDING.md](docs/BUILDING.md) | Build, install, enable, upgrade, troubleshooting |
| [docs/DATA.md](docs/DATA.md) | Maintaining the source table (`clients.csv`) |
| [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) | Repository layout, architecture, coding constraints |
| [docs/API.md](docs/API.md) | Extension API for other mods (v4) |

## License

Released under **LGPL-3.0-only** — see [LICENSE](LICENSE) and the additional GPL-3.0 terms in [LICENSE.GPL](LICENSE.GPL).
Other mods may depend on, bundle or close-source against this mod, including commercially; only modifications to this
repository's own code must be published under LGPL-3.0.
