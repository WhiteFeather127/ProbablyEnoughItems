# Development notes

**English** | [简体中文](DEVELOPMENT.zh-CN.md)

[← Back to README](../README.md)

Code conventions, the runtime pipeline, and the rules that keep the mod loadable by future game builds.
Build instructions live in [BUILDING.md](BUILDING.md).

## Layout

```
ProbablyEnoughItems/
├── ProbablyEnoughItems.csproj
├── manifest.xml                  # id / name / assembly / (empty) capability declarations
├── ModEntry.cs                   # IMod entry point: log, hook subscriptions, F9 debug items
├── Api/
│   ├── ItemCatalogApi.cs         # public contract (additive only, ApiVersion = 4)
│   └── CatalogVariantKit.cs      # enum tables + isolation helpers shared with third parties
├── Catalog/
│   ├── ItemEntry.cs              # item data model (Key = Id or Id#variant)
│   ├── CatalogDataBuilder.cs     # registry scan, snapshot, incremental merge
│   ├── RecipeData.cs             # recipe index
│   ├── SourceData.cs             # source index + commissary
│   ├── SourceTable.cs            # clients.csv / legacy goods.csv parsing
│   ├── SourceProbe.cs            # manual runtime source report (F9)
│   ├── LootSource.cs             # loot pools, boxes, keys, pool→site table
│   ├── BoxProbe.cs               # load-time box-content capture (box_probe.txt)
│   ├── LoadCapture.cs            # load-time capture hooks (LuckRoll / TryAdd / EmporiumTryAdd)
│   ├── IsTagCapture.cs           # gun-slot tag capture
│   ├── UseTable.cs               # uses (transformations)
│   ├── LiquidManager.cs          # liquid composition / water grades
│   ├── LiquidCatalog.cs          # one full bottle per liquid
│   ├── LiquidReactions.cs        # liquid reactions
│   ├── DnaManager.cs             # DNA traits / disks
│   ├── DnaCatalog.cs             # one entry per written DNA disk
│   ├── GunDisassembly.cs         # IL fallback table
│   ├── GunDisassemblyProbe.cs    # real disassembly probe (gun_disassembly.txt)
│   ├── RelationIndex.cs          # generic bidirectional index
│   └── LockKeys.cs
├── Data/                         # source tables (edit + restart, no rebuild)
├── UI/
│   ├── CatalogWindow.cs          # panel, paged grid, variant decisions, tooltip, warm-up
│   ├── RecipeWindow.cs           # detail window (source / recipe / uses / machine / gun / liquid / DNA)
│   ├── MachinePanels.cs          # static machine recipes (from IL)
│   ├── VanillaMachinePanel.cs    # borrows the native machine window to show samples
│   ├── CatalogTicker.cs          # resident Update driver
│   ├── TooltipFollower.cs        # mouse-following tooltip, rebuilt every frame
│   ├── LauncherButton.cs         # inventory corner button
│   ├── ClientSprite.cs           # customer portrait resolution
│   ├── AnchorTarget.cs           # panel docking target
│   ├── Carousel.cs               # every carousel cursor
│   ├── CropVariants.cs           # crop quality-tier table
│   ├── PreBuiltItems.cs          # pre-built key aliases + durability ranges
│   ├── PanelWatchPatch.cs        # window lifecycle postfix (button re-anchor)
│   ├── DragToDeletePatch.cs      # drag an item onto the catalog → delete
│   ├── GunSoundPatch.cs          # silence the ratchet during the disassembly probe
│   └── DockedLayout.cs           # docked layout helpers
├── Localization/{en,zh,ru}.csv
├── docs/
├── examples/ProbablyEnoughItemsBridge.cs
└── install.ps1
```

## Lifecycle

`ModEntry` implements `IMod`:

| Callback | What happens |
|---|---|
| `Init` | creates the `ModLog`; nothing else — the UI is data-driven and resolves its dock target at runtime |
| `OnEnable` | subscribes `OnGameLoadedLate`, `OnCreateTooltipLate` (×2), `OnLootTablesLoaded`, `OnModItemDirectoryInit`; starts `CatalogTicker`; installs `PanelWatchPatch`, `DragToDeletePatch`, `GunSoundPatch`, `LoadCapture`, `IsTagCapture`; registers the F9 debug entries |
| `OnDisable` | unsubscribes everything and removes the debug entries |

Load-time work happens in `OnGameLoadedLate` → `CatalogWindow.Warmup()`: build the snapshot, discover blender /
furnace recipes, probe gun disassembly, expand variant cells, warm the first page's icons, then build `RecipeIndex`,
probe every moddable gun's mod slots (`GunMods.Warmup`) and build `SourceIndex`. `BoxProbe` runs there too, and again
on `OnLootTablesLoaded` if loot tables were not ready yet.

## Runtime pipeline

1. **Snapshot** — `CatalogDataBuilder` walks every `ItemDirectory` the game registers plus every entry injected by
   `ICatalogItemProvider`, skips `UnusedDirectory` and debug items, and sorts by id.
2. **Indexes** — `RecipeIndex` (recipes), `UseTable` (`Data/uses.csv`), `SourceIndex` (`Data/clients.csv`, columns
   12–17), `LootSource` (loot tables + `Data/loot_pools.csv` + `Data/box_pools.csv`), `LiquidCatalog` / `DnaCatalog`.
3. **Warm-up** — all of the above plus variant flattening and first-page icons run once after game load, so opening
   the panel does not freeze the game.
4. **Tick** — `CatalogTicker` drives `AutoRefreshTick` every frame: finish a pending redraw, build the remaining grid
   cells in slices (`CellsPerFrame = 8`), retry failed container-overlay captures (max 2 per cell), keep
   drag/tooltip/portrait alive, and apply `NotifyItemsChanged` merges. There is **no** periodic re-roll and no
   background probing.
5. **Change detection** — opening the panel calls `HasNewItems()` (registry ids, provider counts, extra-item set).
   New items are appended incrementally; only removals / provider-set changes trigger a full rebuild.

## Intervening in the game

| Need | Mechanism |
|---|---|
| Run after the game is loaded | `ModHook.OnGameLoadedLate` |
| Run once loot tables are ready | `ModHook.OnLootTablesLoaded` |
| Append to tooltips (and keep working with other mods' entries) | `ModHook.OnCreateTooltipLate` |
| React to other mods registering item directories | `ModHook.OnModItemDirectoryInit` → `ItemCatalogApi.NotifyItemsChanged` |
| Read window geometry, drag state, sprite modifiers | public members (`PixelWindow.rectTransform`, `getItemSpriteModifierForItemFunc`, `ItemMouseDragHandler`, `TreeNodeRender`) |
| Track window show/hide and layout changes | `PanelWatchPatch` — postfixes on `PixelWindow` lifecycle methods |
| Intercept box contents / gun disassembly products | `LoadCapture` — prefixes only inside an explicit `Begin()` / `End()` window |
| Learn which tags a gun slot accepts | `IsTagCapture` — same window pattern |

The rule of thumb: **if an official hook exists, use it; patch only what has no hook.** Every patch is narrow and has
its own Harmony id (`modId + ".panelwatch"`, `".dragdelete"`, `".gunsound"`, `".loadcapture"`, `".istagcapture"`);
none of them change game return values outside their capture windows.

## Constraints

1. **Reflection must stay scanner-free.** Use literal type names and literal member names only:
   `typeof(SomeType).GetField("someField")` is fine, `obj.GetType().GetMember(name)` is not. Never pass a `Type` or a
   member name into a helper, never build a member name by concatenation. Otherwise the mod must declare the
   `reflection-external` capability, which is being retired — the game's 2026-12-10 update refuses to load mods that
   declare it. See the comment block in `manifest.xml`.
2. **Never bundle `ProbablyEnoughItems.dll` into another mod.** Listing it in `<Assemblies>` makes the loader
   instantiate `ItemCatalog.ModEntry` again with that manifest, doubling every hook subscription.
3. **Main thread only.** All game APIs used here are not thread safe.
4. **Do not re-introduce periodic re-rolling or background probing.** Randomness is fixed when variant cells are
   expanded; a single-cell entry keeps the instance it was built with. `AutoRefreshTick` exists only for pending
   redraws, sliced cell building and overlay retries. Per-frame probing was explicitly rejected by users (stutter).
5. **Harmony patches need an id and a narrow scope.** `new Harmony(modId + ".panelwatch")`; no `UnpatchAll()` without
   an id; no patching of methods whose behaviour we would have to preserve. Capture windows (`LoadCapture`) must be
   closed in a `finally`, otherwise later game calls are silently swallowed.

## Debug aids

- `F8` — console, log lines prefixed with the mod name.
- `F9` → Special — `Catalog: Dump Data`, `Dump Recipes`, `Dump Sources`, `Probe Sources`, `Reopen`.
- Files written through `ModHelper.WriteModDataFileText`: `box_probe.txt` (box contents), `gun_disassembly.txt`
  (disassembly probe cache), `gun_slots.txt` (gun slot → accepted tags), `tutorial_items.txt`
  (items granted by `HandleSkipIntro`), `environment.txt` (shared game/mod signature) and
  `index_<name>.txt` (indexes cached by `IndexManager`, e.g. `index_gun-mod-guns.txt`); the manual
  source probe writes `source-probe.txt`.

## Extending

- Item states / variants → [API.md](API.md)
- Source tables → [DATA.md](DATA.md)
