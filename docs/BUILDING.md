# Building & Installing

**English** | [简体中文](BUILDING.zh-CN.md)

[← Back to README](../README.md)

## Requirements

- *Probably Stolen* installed. The game folder is resolved automatically — Steam registry, or the `PROBABLY_STOLEN_DIR` environment variable; override with `-p:GameDir=<game folder>`
- .NET SDK ≥ 6
- Compile-time references, all taken from the game's `Probably Stolen_Data\Managed` folder and never copied into the
  output: `Assembly-CSharp`, `UnityEngine`, `UnityEngine.CoreModule`, `Unity.TextMeshPro`, `Newtonsoft.Json`, `0Harmony`

## Build

```powershell
# Debug: compile only, nothing is copied into the game folder
dotnet build

# Release: compile, then copy dll + manifest + Localization\ + Data\ into <game>\Mods\ProbablyEnoughItems
dotnet build -c Release

# Game installed somewhere else (works for both configurations)
dotnet build -c Release -p:GameDir="<game folder>"
```

The install step is an MSBuild target (`InstallMod`) that only runs for `Configuration == Release`; it is a
best-effort copy (`ContinueOnError`), so building from a shell without write access to the game folder will silently
leave the old files in place. If that happens, use the script instead:

```powershell
# Right-click PowerShell → Run as Administrator
.\install.ps1

# or with explicit paths
.\install.ps1 -GameDir "<game folder>" -Configuration Release
```

`install.ps1` builds first when the dll is missing, then copies `ProbablyEnoughItems.dll`, `manifest.xml`,
`Localization\*.csv` and `Data\*.csv` into `<game>\Mods\ProbablyEnoughItems\`.

## What gets installed

```
<game>\Mods\ProbablyEnoughItems\
├── ProbablyEnoughItems.dll
├── manifest.xml
├── preview.png
├── Localization\{en,zh,ru}.csv
└── Data\{clients,uses,box_pools}.csv
```

`preview.png` is the banner shown in the mods menu; once the mod is published to the Workshop, the item's
`previewfile` image takes priority and this file is only the offline/fallback copy.

Nothing else belongs in that folder — in particular do **not** drop another copy of the dll there under a different
name, the loader will try to load it again.

## Enabling

1. Launch the game → "Mods" menu → select **Probably Enough Items** → activate → restart the game
2. Load a save → a catalog button appears at the lower-right corner of the inventory
3. Click it; the item panel opens on the right. `<` `>` page through it, click an icon to select it, hover for the tooltip

## Upgrading from Item Catalog

This mod used to be called *Item Catalog* (id `item_catalog`, folder `Mods\ItemCatalog`). The id changed, so the old
install is **not** replaced automatically:

1. Delete `Mods\ItemCatalog\`
2. Install the new folder as described above
3. Re-activate the mod in the mods menu

If both folders are present, both mods load and every hook runs twice — the visible symptom is two `ID: xxx` lines in
every tooltip.

## Verifying the install

Press `F8` in game to open the console. Useful lines (all prefixed with the mod name):

| Keyword | Meaning |
|---|---|
| `[warmup]` | catalog snapshot built and icons warmed up; includes timings |
| `[variants]` | variant expansion decisions |
| `[sprite]` | sprite resolution problems |
| `[panelwatch]` | Harmony patch for the inventory button was installed (or failed) |
| `[sources]` | a malformed row in `clients.csv` was skipped |
| `[probe]` | runtime source report written to `Data`-adjacent probe output |

A healthy start logs `Probably Enough Items initializing...`, then `Probably Enough Items enabled.` after the game
finishes loading.

## Uninstall

Delete `Mods\ProbablyEnoughItems\` and restart. No files outside the mod folder are written, except the probe cache
next to the game's own data (only when the probe runs).

## Publishing to the Steam Workshop

The game has no in-game upload button; Workshop items are published with Valve's `steamcmd`. `tools/publish/publish.ps1`
builds a clean staging folder (the installer target is redirected to `.publish\stage`, so the game folder is untouched),
validates it, and writes `.publish\workshop.vdf`:

```powershell
# package only: build + stage + vdf
.\tools\publish\publish.ps1

# hidden test upload
.\tools\publish\publish.ps1 -Upload -Username <account> -Visibility 2

# update an already published item
.\tools\publish\publish.ps1 -Upload -Username <account> -PublishedFileId <id> -ChangeNote "fix sources"
```

Output defaults to `<repo>\publish\` — `stage\` (the upload content) plus `workshop.vdf`.
Keep that path ASCII-only: steamcmd handles non-ASCII `contentfolder` values badly and fails with
`Timeout uploading manifest`. If your checkout lives under a non-ASCII path, override per run, e.g.
`-StageDir "$HOME\Documents\ProbablyEnoughItems-publish\stage"`.

App id is `4348910`; `visibility` 0 = public, 1 = friends only, 2 = hidden. The very first upload also requires
accepting the Workshop legal agreement on the Steam website. Your account must own the game. A local
`Mods\ProbablyEnoughItems\` folder with the same `<ID>` overrides the Workshop copy, which is exactly how you test a
build before publishing it.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| The mod is not listed in the mods menu | Wrong folder name or missing `manifest.xml` | The folder must be `<game>\Mods\ProbablyEnoughItems\` with the manifest directly inside |
| Listed but nothing happens after activating | The game applies activation on restart | Restart the game |
| Two `ID: xxx` lines in every tooltip | Both `Mods\ItemCatalog\` and `Mods\ProbablyEnoughItems\` exist, or the dll was copied twice | Keep exactly one mod folder, one dll |
| "not made for this game version" note in the mods list | `<GameVersions>` in `manifest.xml` does not match the game | Update `<Version>`; comparison only keeps digits and dots, so `0.46D` matches `0.46` |
| Build succeeds but the game still runs the old code | Release install target could not write to the game folder | Run `install.ps1` as Administrator |
| No catalog button | Another mod moved/docked the inventory panel, or the `panelwatch` patch failed | Check the log for `[panelwatch]`; the button does not follow panel geometry when the patch is missing |
