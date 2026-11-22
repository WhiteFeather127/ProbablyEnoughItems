# 构建与安装

[English](BUILDING.md) | **简体中文**

[← 返回 README](../README.zh-CN.md)

## 环境要求

- 已安装《Probably Stolen》（游戏目录自动解析：优先 `PROBABLY_STOLEN_DIR` 环境变量，其次 Steam 注册表；也可用 `-p:GameDir=<游戏目录>` 覆盖）
- .NET SDK ≥ 6
- 编译期引用全部取自游戏的 `Probably Stolen_Data\Managed`，且不会复制进产物：
  `Assembly-CSharp`、`UnityEngine`、`UnityEngine.CoreModule`、`Unity.TextMeshPro`、`Newtonsoft.Json`、`0Harmony`

## 构建

```powershell
# Debug：只编译，不往游戏目录写任何东西
dotnet build

# Release：编译后把 dll + manifest + Localization\ + Data\ 复制到 <游戏>\Mods\ProbablyEnoughItems
dotnet build -c Release

# 游戏装在别处（两种配置都适用）
dotnet build -c Release -p:GameDir="<游戏目录>"
```

安装是 MSBuild 里的 `InstallMod` 目标，只在 `Configuration == Release` 时执行，且是「尽力而为」的复制
（`ContinueOnError`）——所以从没有写权限的终端构建时，它会静默失败、游戏里还是旧文件。这种情况改用脚本：

```powershell
# 右键 PowerShell → 以管理员身份运行
.\install.ps1

# 或者显式指定路径
.\install.ps1 -GameDir "<游戏目录>" -Configuration Release
```

`install.ps1` 发现 dll 不存在时会先编译，然后把 `ProbablyEnoughItems.dll`、`manifest.xml`、
`Localization\*.csv` 与 `Data\*.csv` 复制到 `<游戏>\Mods\ProbablyEnoughItems\`。

## 安装后的目录

```
<游戏>\Mods\ProbablyEnoughItems\
├── ProbablyEnoughItems.dll
├── manifest.xml
├── preview.png
├── Localization\{en,zh,ru}.csv
└── Data\{clients,uses,box_pools}.csv
```

`preview.png` 是模组菜单里的横幅；发布到创意工坊后优先显示物品的 `previewfile` 图，这一份只在离线或没有工坊图时使用。

这个目录里不应该有别的东西——尤其不要把 dll 换个名字再放一份，加载器会把它当成另一个模组再加载一次。

## 启用

1. 启动游戏 →「模组」菜单 → 选中 **Probably Enough Items** → 激活 → 重启游戏
2. 载入存档 → 物品栏右下角出现「图鉴」按钮
3. 点击按钮，右侧出现物品面板；`<` `>` 翻页，点击图标选中，悬停看提示框

## 从旧版 Item Catalog 升级

本模组原名 *Item Catalog*（id `item_catalog`，目录 `Mods\ItemCatalog`）。id 变了，旧安装**不会**被自动覆盖：

1. 删除 `Mods\ItemCatalog\`
2. 按上面的步骤安装新目录
3. 在模组菜单里重新激活

两个目录同时存在时两个模组都会加载、所有钩子执行两遍——最直观的表现就是提示框里出现两行 `ID: xxx`。

## 验证安装

游戏内按 `F8` 打开控制台，有用的日志都以模组名开头：

| 关键字 | 含义 |
|---|---|
| `[warmup]` | 图鉴快照已建好、图标预热完成（含耗时） |
| `[variants]` | 变体展开判定 |
| `[sprite]` | 精灵解析异常 |
| `[panelwatch]` | 物品栏按钮的 Harmony 补丁已安装（或失败） |
| `[sources]` | `clients.csv` 里有格式错误的行被跳过 |
| `[probe]` | 运行时来源报告已写出 |

正常启动会先出现 `Probably Enough Items initializing...`，游戏加载完成后出现 `Probably Enough Items enabled.`。

## 卸载

删掉 `Mods\ProbablyEnoughItems\` 后重启。模组不会在自身目录之外留下文件（探针运行时会在游戏数据旁写一份缓存）。

## 发布到 Steam 创意工坊

游戏内没有上传按钮，工坊物品用 Valve 的 `steamcmd` 发布。`tools/publish/publish.ps1` 会构建一个干净的发布目录
（安装目标被重定向到 `.publish\stage`，不碰游戏目录）、校验内容，并生成 `.publish\workshop.vdf`：

```powershell
# 只打包：构建 + staging + vdf
.\tools\publish\publish.ps1

# 隐藏上传，自测用
.\tools\publish\publish.ps1 -Upload -Username <账号> -Visibility 2

# 更新已发布的物品
.\tools\publish\publish.ps1 -Upload -Username <账号> -PublishedFileId <id> -ChangeNote "fix sources"
```

产物默认输出到 `<仓库根>\publish\`——`stage\`（上传内容）与 `workshop.vdf`。这个路径必须保持纯 ASCII：
steamcmd 处理非 ASCII 的 `contentfolder` 会失败（`Timeout uploading manifest`）。如果你的仓库目录本身含中文，
每次发布时用 `-StageDir` 指到一个 ASCII 目录，例如 `-StageDir "$HOME\Documents\ProbablyEnoughItems-publish\stage"`。

App ID 是 `4348910`；`visibility` 0 = 公开、1 = 仅好友、2 = 隐藏。首次上传还需要在 Steam 网页上接受一次创意工坊
法律协议，且登录的账号必须拥有本游戏。本地 `Mods\ProbablyEnoughItems\` 与工坊物品同 `<ID>` 时本地优先，
正好用来在发布前验证新版本。

## 排错

| 现象 | 原因 | 处理 |
|---|---|---|
| 模组菜单里没有它 | 目录名不对，或缺 `manifest.xml` | 必须是 `<游戏>\Mods\ProbablyEnoughItems\`，清单直接放在这一层 |
| 能列出，激活后没反应 | 激活在重启后生效 | 重启游戏 |
| 每个提示框都有两行 `ID: xxx` | `Mods\ItemCatalog\` 与 `Mods\ProbablyEnoughItems\` 同时存在，或 dll 被放了两份 | 只保留一个模组目录、一份 dll |
| 模组列表提示「不是为这个游戏版本做的」 | `manifest.xml` 的 `<GameVersions>` 与游戏版本不匹配 | 改 `<Version>`；比较时只保留数字和点，所以 `0.46D` 与 `0.46` 等价 |
| 编译成功但游戏里还是旧代码 | Release 的安装目标没写进游戏目录 | 用管理员权限运行 `install.ps1` |
| 没有图鉴按钮 | 别的模组挪动了物品栏，或 `panelwatch` 补丁安装失败 | 查日志里的 `[panelwatch]`；补丁缺失时按钮不会跟随面板移动 |
