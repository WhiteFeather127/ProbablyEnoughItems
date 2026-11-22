# Development notes

**English** | [简体中文](DEVELOPMENT.zh-CN.md)

[← Back to README](../README.md)

Code conventions, the runtime pipeline, and the rules that keep the mod loadable by future game builds.
Build instructions live in [BUILDING.md](BUILDING.md).

## Layout

```
ProbablyEnoughItems/
├── ProbablyEnoughItems.csproj
├── manifest.xml                  # id / name / assembly / capability declarations
├── ModEntry.cs                   # IMod entry point: log, hook subscriptions, F9 debug items
├── Api/
│   ├── ItemCatalogApi.cs         # public contract (additive only, ApiVersion = 4)
│   └── CatalogVariantKit.cs      # enum tables + isolation helpers shared with third parties
├── Catalog/
│   ├── ItemEntry.cs              # item data model
│   ├── CatalogDataBuilder.cs     # registry scan, snapshot, incremental merge
│   ├── RecipeData.cs             # recipe index
│   ├── SourceData.cs             # source index
│   ├── SourceTable.cs            # CSV parsing (base inheritance, tolerance, caching)
│   ├── SourceProbe.cs            # runtime source probe / report
│   ├── LootSource.cs             # loot sources
│   ├── UseTable.cs               # uses (transformations)
│   └── LockKeys.cs
├── Data/                         # source tables (edit + restart, no rebuild)
├── UI/
│   ├── CatalogWindow.cs          # panel, paged grid, variant decisions, tooltip, warm-up
│   ├── RecipeWindow.cs           # detail window (source / recipe / uses pages)
│   ├── CatalogTicker.cs          # resident refresh tick
│   ├── TooltipFollower.cs        # mouse-following tooltip
│   ├── LauncherButton.cs         # inventory corner button
│   ├── ClientSprite.cs           # customer portrait resolution
│   ├── AnchorTarget.cs           # panel docking target
│   ├── PanelWatchPatch.cs        # the single Harmony patch
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
| `OnEnable` | subscribes `OnGameLoadedLate`, `OnCreateTooltipLate` (×2), `OnModItemDirectoryInit`; starts `CatalogTicker`; installs `PanelWatchPatch`; registers the F9 debug entries |
| `OnDisable` | unsubscribes everything and removes the debug entries |

## Runtime pipeline

1. **Snapshot** — `CatalogDataBuilder` walks the game's 27 `ItemDirectory` instances plus every entry injected by
   `ICatalogItemProvider`, skips `UnusedDirectory` and debug items, and sorts by id.
2. **Indexes** — `RecipeIndex` (recipes), `UseTable` (`Data/uses.csv`), `SourceIndex` (`Data/clients.csv`, whose
   columns 12–16 carry what each source sells), `LootSource` (loot tables) build alongside the snapshot.
3. **Warm-up** — icons are resolved once after game load, so opening the panel does not freeze the game.
4. **Refresh** — `CatalogTicker` ticks every frame; a full page refresh happens once per second (`CatalogWindow`),
   re-rolling continuously random values and swapping icons as needed.
5. **Change detection** — opening the panel calls `HasNewItems()` (registry ids, provider count, extra-item set); any
   change triggers a full rebuild, otherwise the existing snapshot is reused.

## Intervening in the game

| Need | Mechanism |
|---|---|
| Run after the game is loaded | `ModHook.OnGameLoadedLate` |
| Append to tooltips (and keep working with other mods' entries) | `ModHook.OnCreateTooltipLate` |
| React to other mods registering item directories | `ModHook.OnModItemDirectoryInit` |
| Read window geometry, drag state, sprite modifiers | public members (`PixelWindow.rectTransform`, `getItemSpriteModifierForItemFunc`, `ItemMouseDragHandler`, `TreeNodeRender`) |
| Track window show/hide and layout changes | `PanelWatchPatch` — postfixes on `PixelWindow` lifecycle methods |

The rule of thumb: **if an official hook exists, use it; patch only what has no hook.** The one patch exists because
the game offers no event for "a window rect changed", and polling it cost up to a second of latency plus missed
changes made by other mods. The postfix only repositions our own button — it never changes game state or return values.

## Constraints

1. **Reflection must stay scanner-free.** Use literal type names and literal member names only:
   `typeof(SomeType).GetField("someField")` is fine, `obj.GetType().GetMember(name)` is not. Never pass a `Type` or a
   member name into a helper, never build a member name by concatenation. Otherwise the mod must declare the
   `reflection-external` capability, which is being retired — the game's 2026-12-10 update refuses to load mods that
   declare it. See the comment block in `manifest.xml`.
2. **Never bundle `ProbablyEnoughItems.dll` into another mod.** Listing it in `<Assemblies>` makes the loader
   instantiate `ItemCatalog.ModEntry` again with that manifest, doubling every hook subscription.
3. **Main thread only.** All game APIs used here are not thread safe.
4. **The refresh interval stays at one second.** When the panel feels heavy, reduce the work done per tick — do not
   batch or lower the frequency, or items stop tracking their live state.
5. **Harmony patches need an id and a narrow scope.** `new Harmony(modId + ".panelwatch")`; no `UnpatchAll()` without
   an id; no patching of methods whose behaviour we would have to preserve.

## Debug aids

- `F8` — console, log lines prefixed with the mod name.
- `F9` → Special — `Catalog: Dump UI`, `Dump Data`, `Dump Recipes`, `Dump Sources`, `Probe Sources`, `Reopen`.

## Extending

- Item states / variants → [API.md](API.md)
- Source tables → [DATA.md](DATA.md)
