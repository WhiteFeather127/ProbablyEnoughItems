# Probably Enough Items — Extension API

**English** | [简体中文](API.zh-CN.md)

[← Back to README](../README.md)

Other mods use this API to tell the catalog two things:

1. **how many states this item has** (enumerable states are expanded into separate cells, one fixed variant each);
2. **which entries outside the registries** should appear in the catalog.

| Item | Value |
|---|---|
| Namespace | `ProbablyEnoughItems.Api` |
| Contract files | [`Api/ItemCatalogApi.cs`](../Api/ItemCatalogApi.cs) (static class name is `ItemCatalogApi`), [`Api/CatalogVariantKit.cs`](../Api/CatalogVariantKit.cs) |
| API version | `ItemCatalogApi.ApiVersion == 4` |
| Zero-dependency template | [`examples/ProbablyEnoughItemsBridge.cs`](../examples/ProbablyEnoughItemsBridge.cs) |
| Mod id | `probably_enough_items` |

**Compatibility promise**: this is a long-term contract, additive only. New capabilities mean new members plus
`ApiVersion + 1`; if a breaking change ever becomes unavoidable, a type such as `ItemCatalogApi2` /
`ICatalogVariantProvider2` is added alongside and the old members are kept.

---

## 1. 30-second start (zero dependencies)

You do not need to reference `ProbablyEnoughItems.dll`, you do not need `<Prerequisites>` in your manifest, and you
must not bundle the other mod's DLL into your own.

**Step 1**: copy [`examples/ProbablyEnoughItemsBridge.cs`](../examples/ProbablyEnoughItemsBridge.cs) into your project
and change its `namespace`.

**Step 2**: register.

```csharp
public void Init(ModManifest manifest)
{
    _log = new ModLog(manifest);

    if (ProbablyEnoughItemsBridge.IsAvailable)
    {
        // the glowing berry has 3 states; index 0/1/2 map to them in order
        ProbablyEnoughItemsBridge.RegisterVariants("wine_berry", 3, CreateVariant);

        // items that are not in any registry but should still appear in the catalog
        ProbablyEnoughItemsBridge.RegisterItems("my_mod", BuildExtras);
    }
}
```

When `IsAvailable == false` (catalog not installed) both `Register*` calls simply return `false` — no exception, no
error log entry, your mod keeps running.

**Step 3** (optional): the catalog may load after your mod; retry once on failure.

```csharp
if (!ProbablyEnoughItemsBridge.IsAvailable) ModHook.OnGameLoadedLate += OnGameLoaded;
```

---

## 2. Two ways to integrate

| Approach | When to use | Dependency |
|---|---|---|
| Delegates + reflection (bridge template) | **Across mods, recommended** | Zero dependencies; only `GameItem` from `Assembly-CSharp` |
| Implement the interfaces (`ICatalogVariantProvider`) | Same repository as the catalog, or you accept a source-level reference | Needs the `ProbablyEnoughItems.Api` namespace at compile time |

Both approaches register into the same priority chain and are treated identically by the catalog.

### 2.1 Delegates + reflection

The bridge does the equivalent of:

```csharp
var api = AppDomain.CurrentDomain.GetAssemblies()
    .Select(a => a.GetType("ProbablyEnoughItems.Api.ItemCatalogApi", false))
    .FirstOrDefault(t => t != null);

api.GetMethod("RegisterVariantProvider",
        new[] { typeof(string), typeof(int), typeof(Func<int, GameItem>) })
   .Invoke(null, new object[] { "wine_berry", 3, new Func<int, GameItem>(CreateVariant) });

api.GetMethod("RegisterItemProvider", new[] { typeof(string), typeof(Func<List<GameItem>>) })
   .Invoke(null, new object[] { "my_mod", new Func<List<GameItem>>(BuildExtras) });
```

This only works because the delegate parameter types (`GameItem`, `List<GameItem>`) come from the game assembly, so
the two mods share no custom types; interface implementations cannot cross assembly boundaries without a reference.

### 2.2 Implementing the interfaces

```csharp
internal sealed class BerryVariants : ProbablyEnoughItems.Api.ICatalogVariantProvider
{
    public string ItemId { get { return "wine_berry"; } }
    public int VariantCount { get { return 3; } }

    public GameItem CreateVariant(int index)
    {
        var item = DirectoryMaster.Item("wine_berry");   // fresh instance; never keep or cache it
        ApplyQuality(item, index);      // must be deterministic: same index, equivalent result
        return item;
    }
}
```

---

## 3. API reference

### 3.1 `ItemCatalogApi` (static class)

| Member | Description |
|---|---|
| `const int ApiVersion` | API version, currently `4`; incremented when members are added |
| `RegisterVariantProvider(ICatalogVariantProvider)` | Register a variant provider (interface overload) |
| `RegisterVariantProvider(string itemId, int count, Func<int, GameItem> factory)` | Register a variant provider (delegate overload, reflection-friendly) |
| `RegisterVariantProvider(string itemId, int count, Func<int, GameItem> factory, Func<List<string>> labels)` | Same as above; the `labels` parameter is **currently ignored** (the catalog does not draw text on cells) and only kept so old callers keep compiling |
| `RegisterItemProvider(ICatalogItemProvider)` | Register an extra-items provider (interface overload) |
| `RegisterItemProvider(string modId, Func<List<GameItem>> factory)` | Register an extra-items provider (delegate overload) |
| `NotifyItemsChanged()` / `NotifyItemsChanged(string reason)` | Tell the catalog the item list changed: a new item was created at runtime, or an item's variant space grew |
| `event Action<int> CatalogRefreshed` | Raised after an incremental merge or a full rebuild; the argument is the current visible entry count |
| `FindVariantProvider(string itemId)` | Look up a registered variant provider by id; returns `null` if none |
| `GetModuleShapes(string itemId)` | List the footprint shapes a module actually has (sprite names, in catalog expansion order); returns an empty list for non-modules |

Rules:

- registering a second variant provider for the same `itemId` is ignored — **first registration wins**, nothing is overwritten;
- an empty `itemId` or a `null` `factory` throws `ArgumentException`;
- all members must be called on the **main thread** (ideally from `Init` / `OnEnable`);
- exceptions thrown inside `CatalogRefreshed` only produce a warning log entry and never interrupt the refresh.

What `NotifyItemsChanged` means:

- the catalog does **not** do the work synchronously inside that call (you may be inside the game's registry
  enumeration / generation flow at that moment). It only records a flag and merges incrementally, warming icons, on a
  later idle tick; multiple calls in the same frame collapse into one.
- the catalog does **not register directories for you**. For the game to rebuild an item from its id (save loading and
  drag-and-drop both rely on it) you must call `DirectoryMaster.AddDirectory` + `Add(id, factory)` yourself.
- only "new items / more variants" is supported; removing an existing entry triggers a full rebuild on the next idle
  tick (slower).

### 3.2 `ICatalogVariantProvider`

| Member | Description |
|---|---|
| `string ItemId` | Target item id, identical to the registry id |
| `int VariantCount` | Number of variants; `<= 1` means the provider does not expand this item (the catalog's built-in branches may still apply). The catalog never auto-probes unknown items |
| `GameItem CreateVariant(int index)` | Build the variant at `index` (`0 <= index < VariantCount`); return `null` if that index is unavailable |

`CreateVariant` must be **deterministic**: the catalog expands states in order, so the same `index` must produce an
equivalent result every time. Do not roll random numbers in there — that defeats the purpose of multiple cells.

> The label member (`GetVariantLabels`) was removed after v3: the catalog no longer draws text on cells. Do not rely on it.

### 3.3 `ICatalogItemProvider`

| Member | Description |
|---|---|
| `string ModId` | Owning mod id, used for logging only |
| `List<GameItem> GetExtraItems()` | Items to display; may return `null` or an empty list |

`GetExtraItems` is called when **a snapshot is built or extended** (startup warm-up, the change detection performed
when the catalog opens, and the idle-tick merge after `NotifyItemsChanged`). Keep it cheap: if items must be created,
create and cache them during `Init`.

You own the returned items; the catalog only reads them and never destroys them.

**Fill in these fields**, otherwise the tooltip will look empty:

| Field | Purpose |
|---|---|
| `name` | Title line of the tooltip |
| `shortDescription` (falls back to `flavorText` when empty) | Description line under the name |
| `unitValue` | The "value" line |
| `itemTypes` | Type badge (e.g. `HOUSEHOLD_GOOD` → `[Household good]`) |

The reason: injected entries are not in any registry, so the game's native tooltip chain degrades to "type + value
only". The catalog therefore uses **its own builder** for these entries (name → type → value → description → sources
→ ID) and still fires the `OnCreateTooltipEarly/Late` hooks as usual.

### 3.4 `CatalogVariantKit` (enum tables + isolation helpers)

The tables behind the catalog's own expansion logic. **The catalog's internal branches read these same fields**, so
third parties do not need to copy them and will not drift when the game updates.

Enum tables:

| Member | Description |
|---|---|
| `LearningEffectId` | Self-learning algorithm effect id (special: two extra cells, "0 stat" and "max stat") |
| `ModuleEffectIds` | Complete list of module effects, in the game's static construction order |
| `ModuleShapeSuffixes` | Candidate sprite suffixes for module footprints (only ones that actually exist are used) |
| `ModuleEffectCells` | Cell order inside one footprint shape: no special effect first, then each effect |
| `BerryForms` | Berry forms (condition + visual variant + display label) |
| `CigaretteIssueTags` | The four cigarette issue tags (stamp / logo / font / box colour) |
| `InjectorIssueTags` | The five immunity-injector issue tags |
| `StampIssueTags` | The five food-stamp issue tags |
| `StampAmounts` | All food-stamp denominations `{40, 80, 120, 160, 200}` |
| `StampStateCount` | States per denomination = 1 genuine + 1 counterfeit per issue tag |

Data structures:

```csharp
public sealed class ModuleEffectCellInfo { public string Effect; public int Bonus; }
// Effect: effect tag id, null = the "no special effect" cell
// Bonus : -1 = not self-learning; 0 = self-learning with a 0 stat; -2 = self-learning at max stat

public sealed class BerryFormInfo { public int Condition; public int Visual; public string Label; }
// Condition: CONDITION tag value (-2..2); Visual: VISUAL tag value; Label: short display label
```

Isolation helpers:

| Member | Description |
|---|---|
| `SnapshotModulePool()` / `RestoreModulePool(object)` | Wrap item creation so the game's shared random effect pool is not drained. **Note: in the current version `ModuleEffectHelper.moduleEffects` holds non-collection values, so the snapshot is empty — the calls are harmless but provide **no** protection. Do not rely on them.** |
| `IsolateTagState(GameItem)` | Swap the tag storage for a private copy. `TagSystem.Clone()` is shallow (the inner dictionaries are shared), so multiple variants built from one template instance overwrite each other's tags; call this once per variant |
| `NormalizeIssues(GameItem, int variant, string[] issueTags)` | Make the actual quality match the cell: `variant = 0` has no issues, `variant = k` keeps only the k-th issue (1-based) |
| `ForceStampAmount(GameItem, int amount)` | Force a food stamp's denomination, updating its name and value together |
| `StripAuthenticityLabels(GameItem)` | Remove the genuine/counterfeit `ItemFeature` added by the label printer, leaving classification tags untouched |

---

## 4. How the catalog consumes this data

### 4.1 Variant expansion: finite sets become multiple cells

Items with `VariantCellCount > 1` are **expanded into N cells** (JEI-style matching stacks), one fixed variant per
cell. The decision order (`CatalogWindow.VariantCellCount`) is entirely hardcoded — there is no sampling and no
automatic state discovery:

| Order | Source | Cell count |
|---|---|---|
| ① | `ICatalogVariantProvider` | `VariantCount` (**no upper clamp**; keep the number sane yourself) |
| ② | Crop quality tiers (`CropVariants`) | 2–4 |
| ③ | Scratch cards | 1 (their prize is random, so they are never expanded) |
| ④ | Cigarettes (`packet_red_cigarette`) | 5 (genuine + 4 issue variants) |
| ⑤ | Immunity injectors (`large_purple_injector`) | 6 (genuine + 5 issue variants) |
| ⑥ | Food stamps (`food_stamp`) | 30 (5 denominations × 6 states) |
| ⑦ | Modules (`system_module_*` in the built-in generator table) | footprint shape count × `ModuleEffectCells.Length` (11) |
| ⑧ | Berries (`BERRY_TAG`) | `BerryForms.Length` (6) |
| ⑨ | Hardcoded generator table (`ribwich`, `node_medium`) | table length (expanded only when > 1) |
| — | Everything else (continuously random) | 1 fixed cell; it is **not** re-rolled |

So **registering a provider takes over the display of that item**; it is more accurate than the built-in rules and
wastes fewer cells. Variant instances are created lazily while rendering and cached per cell (startup warm-up does
not mass-create items). A single-cell entry keeps the one instance it was built with.

### 4.2 How extra entries are merged

When a snapshot is built (or extended):

1. entries are built from the registries (each `ItemDirectory`) first;
2. then the results of `GetExtraItems()` are merged in, skipping:
   - ids that already exist in a registry → **the registry wins**;
   - ids supplied by several providers → only the first occurrence is kept;
3. the merged list is sorted by id, so extra entries naturally land next to their prefix.

Late additions reuse the same rules: only the new ids are created, existing entries are left untouched.

### 4.3 When data is pulled again

| Moment | Behaviour |
|---|---|
| Game finished loading | Snapshot + indexes + variant expansion + first-page icons, all in one pass |
| Catalog opened | `HasNewItems()` check: registry ids changed, provider count changed, or the extra-item set changed → new items are appended incrementally; a full rebuild only happens when entries disappear |
| While running | No periodic refresh: the per-frame tick only finishes pending redraws, sliced cell building and overlay retries |
| `NotifyItemsChanged` called | Incremental merge on the next idle tick, then `CatalogRefreshed` |

Registering late (for example from `OnGameLoadedLate`) is safe: the provider-count change triggers an incremental
append the next time the catalog checks for changes.

---

## 5. Constraints (important)

1. **Do not bundle `ProbablyEnoughItems.dll`.**
   The scanner requires non-game assemblies you reference to be listed in `<Assemblies>`, but once listed, the loader
   walks that DLL looking for `IMod` implementations and instantiates `ItemCatalog.ModEntry` a second time with your
   manifest — every hook subscription doubles, and the most visible symptom is two `ID: xxx` lines in the tooltip.
2. **Do not cache `GameItem` references.** Build a fresh item for each variant.
3. **Do not mutate global state in `CreateVariant`** (RNG, `PlayerStore`, static tables).
4. **Call on the main thread**; callbacks also run on the main thread, so do not spawn threads.
5. Preview instances are created with an isolated random stream (`UnityEngine.Random.state` is restored afterwards);
   there is no periodic re-roll, and your implementation should not advance the game's random stream either.
6. Do not call `DirectoryMaster.Item` and then freely mutate the shared instance — the catalog goes through
   `CreatePreviewItem` (restoring RNG / uniqueId). Third parties should at least "create fresh, do not retain, do not
   mutate globals".

---

## 6. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Two `ID: xxx` lines in the tooltip | You bundled `ProbablyEnoughItems.dll` and listed it in `<Assemblies>` | Use the bridge instead; remove that `<Assembly>` and the bundled copy |
| Log says `unlisted-reference: xxx.dll -> references ProbablyEnoughItems` | Compile-time reference without an `<Assemblies>` entry | Switch to reflection (recommended) or drop the compile-time reference |
| Registration succeeds but the catalog does not change | `VariantCount` returns 0 or 1; id typo; or the catalog already built its snapshot and the provider count did not change | Check the id; register during `Init`, or call `NotifyItemsChanged` |
| Extra items never appear | That id already exists in a registry (the registry wins) | Use a dedicated id |
| Missing icon | Wrong `spriteAtlasPath` / `spritePath` | Copy the values from a vanilla item |
| `GetMethod("RegisterVariantProvider", …)` returns null | Catalog older than v2 | The delegate overload requires `ApiVersion >= 2` |
| One variant instance is shared by every cell and they overwrite each other | You reused a single template instance | Build a fresh item per variant and call `CatalogVariantKit.IsolateTagState` |

---

## 7. Version history

| Version | Content |
|---|---|
| v1 | `ICatalogVariantProvider` / `ICatalogItemProvider` plus the interface-based registration methods |
| v2 | Added the delegate overloads `RegisterVariantProvider(string,int,Func<int,GameItem>[,Func<List<string>>])` and `RegisterItemProvider(string,Func<List<GameItem>>)` so plain reflection (zero dependencies) can call them |
| v3 | Added `CatalogVariantKit` (enum tables + isolation helpers) and `GetModuleShapes`; removed `GetVariantLabels` (the catalog no longer draws text on cells) |
| v4 | Added `NotifyItemsChanged` (add items at runtime) and the `CatalogRefreshed` event |

Callers should stay compatible with future versions like this:

```csharp
var method = api.GetMethod("RegisterVariantProvider",
    new[] { typeof(string), typeof(int), typeof(Func<int, GameItem>) });

if (method == null) return false;   // catalog too old: skip silently, don't report an error
```

---

## 8. License

This repository is released under [GNU LGPL v3.0](../LICENSE) (additional terms in [LICENSE.GPL](../LICENSE.GPL)).
Under that license you may use this API in your own mods and keep them closed-source, including commercial releases;
only if you modify this repository's code must those changes be published under LGPL-3.0, and distribution requires
providing the source and allowing users to replace the dll.
