# Maintaining the source tables

**English** | [简体中文](DATA.zh-CN.md)

[← Back to README](../README.md)

The catalog answers two questions per item: *where does it come from* and *what is it used for*. The second one is
derived from the game's recipe and loot data at runtime, but the first one cannot be: the game gives mods no way to
ask "what does this customer sell". Those item lists only exist as string literals inside the IL of
`StoreClientList*.CreateXxx()` and their compiler-generated callbacks.

So the customer side of the data is **extracted offline from the game's IL** and stored as CSV. Editing a CSV and
restarting the game is enough — no rebuild, no code change.

> The extraction scripts (`tools/audit/`) and the audit log (`SOURCES_AUDIT.md`) live in the development workspace,
> not in this repository. The CSVs here are the output of that process, with the per-row evidence recorded in the
> audit log.

## Files

| File | Content |
|---|---|
| `Data/clients.csv` | who (faction → customer) **and what each customer sells** — columns 12–16 |
| `Data/uses.csv` | transformations that act on the item itself — open a box, tear a package, drink a bottle, apply a substance, combine two items — kept separate from the machine recipe index |
| `Data/box_contents.csv` | what a box can actually contain, extracted from `PreBuiltItemHelper.Fill*`; possible items only, probabilities are not recorded |
| `Data/box_pools.csv` | cards required to open a box, plus fallback pools for boxes with no contents table |

`Data/goods.csv` no longer ships. Its columns were folded into `clients.csv` 12–16; a leftover copy is still parsed
and merged for compatibility (see the end of this page), but new data belongs in `clients.csv`.

Every file documents its own columns in a `#` comment block at the top; this page explains the parts that are easy
to get wrong.

## `clients.csv` — who, and what they sell

The table is grouped by faction. A row starting with `# ===== faction =====` and one `faction,<key>` row open a
faction; the rows below describe its customers and therefore do not repeat the faction column.

Required columns:

```
kind,id,name_key,location_id,fallback_key,profession,sprite
```

Optional columns:

| Column | Position | Meaning |
|---|---|---|
| `water` | 8 | What water this customer's containers are filled with: `pure` / `hq` / `base` / `rust` / `ghost` / `gutterflow`. **Empty means no water at all — empty bottles only.** Never fall back to `base`: drawing a full bottle for a customer who sells empty ones is wrong. Only fill this in when the IL actually calls `FillWith*` / `WaterPremadeHelper`. A `type@item_id\|item_id` form fills only the listed containers (e.g. the supplier fills `large_bottled_water` only) |
| `empty` | 9 | `1` = **sells both water and bottles**, so the source page splits each container into an "original empty bottle" cell plus the filled one. Do not set it on pure empty-bottle customers (column 8 empty) — there is nothing to split out |
| `buys_tags` | 10 | item-type tags this customer buys (`WATER` / `WEAPON` …, `|`-separated) |
| `buys_items` | 11 | specific item ids this customer buys (`|`-separated) |
| `sells` | 12 | the goods this customer puts on the table |
| `base` | 13 | inherit another source's item list before applying this row's own changes |
| `exclude` | 14 | ids to drop from the inherited list |
| `random_items` | 15 | ids that appear only sometimes in the IL (guarded by RNG rolls or day counters); kept separate so the UI can present them as "may appear" |
| `dynamic` | 16 | `commissary` = the goods are read from the save's shelf at runtime; the row only supplies the display name |

Notes:

- Item ids in `sells` / `buys_items` / `random_items` are separated by `|`.
- Customers sharing the same `name_*` key are merged into a single source row, so the catalog never shows duplicate
  source names.
- Physical row order in `clients.csv` is the display order in the catalog — move rows to reorder, not to add data.
- A malformed row only logs one `[sources]` warning in `Player.log` and is skipped; the catalog keeps working.

## Editing workflow

1. Edit the CSV in place (UTF-8, no BOM needed).
2. Restart the game (values are cached per session).
3. Open the catalog, pick a source page and check the result; look for `[sources]` warnings in the console (`F8`).
4. If a customer now sells a container with water, remember the `water` column — that is the most common mistake.

## Legacy `goods.csv`

If a `Data/goods.csv` is still present it is parsed as the old six-column format
(`id,base,exclude,items,random_items,dynamic`) and **merged on top of** the columns above, with `clients.csv` winning
where both are set. Rows whose `id` has no matching customer are reported as `[sources] ... is not in
Data/clients.csv, ignored`. The file is not required and is not shipped; keep new data in `clients.csv`.

## What is *not* in these tables

- **Recipes and uses**: read from the game at runtime.
- **Commissary stock**: read from the save; only the display name comes from `clients.csv`.
- **Variant expansion** (which item states exist): decided in code, see [API.md](API.md) §4.1.
