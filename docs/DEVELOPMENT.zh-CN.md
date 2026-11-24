# 开发说明

[English](DEVELOPMENT.md) | **简体中文**

[← 返回 README](../README.zh-CN.md)

代码约定、运行时管线，以及「让模组在以后的游戏版本里仍能被加载」的硬性规则。构建方式见 [BUILDING.zh-CN.md](BUILDING.zh-CN.md)。

## 目录结构

```
ProbablyEnoughItems/
├── ProbablyEnoughItems.csproj
├── manifest.xml                  # id / 名称 / 程序集 / （空的）能力声明
├── ModEntry.cs                   # IMod 入口：日志、钩子订阅、F9 调试项
├── Api/
│   ├── ItemCatalogApi.cs         # 对外契约（只增不改，ApiVersion = 4）
│   └── CatalogVariantKit.cs      # 枚举表 + 隔离工具，与第三方共用
├── Catalog/
│   ├── ItemEntry.cs              # 物品数据模型（Key = Id 或 Id#变体）
│   ├── CatalogDataBuilder.cs     # 注册表扫描、快照、增量补入
│   ├── RecipeData.cs             # 配方索引
│   ├── SourceData.cs             # 来源索引 + 配给站
│   ├── SourceTable.cs            # clients.csv / 旧 goods.csv 解析
│   ├── SourceProbe.cs            # 手动运行时来源报告（F9）
│   ├── LootSource.cs             # 掉落池、箱子、钥匙、池→地点表
│   ├── BoxProbe.cs               # 加载期箱子内容捕获（box_probe.txt）
│   ├── LoadCapture.cs            # 加载期捕获钩子（LuckRoll / TryAdd / EmporiumTryAdd）
│   ├── IsTagCapture.cs           # 枪械槽 tag 捕获
│   ├── UseTable.cs               # 用途（转化）
│   ├── LiquidManager.cs          # 液体成分 / 水质档
│   ├── LiquidCatalog.cs          # 每种液体一只满瓶
│   ├── LiquidReactions.cs        # 液体反应
│   ├── DnaManager.cs             # DNA 性状 / 磁盘
│   ├── DnaCatalog.cs             # 每种已写入盘一个条目
│   ├── GunDisassembly.cs         # IL 兜底表
│   ├── GunDisassemblyProbe.cs    # 真拆一次探测（gun_disassembly.txt）
│   ├── RelationIndex.cs          # 通用双向索引
│   └── LockKeys.cs
├── Data/                         # 来源表（改表重启生效，不用重编译）
├── UI/
│   ├── CatalogWindow.cs          # 面板、分页网格、变体判定、提示框、预热
│   ├── RecipeWindow.cs           # 详情窗口（来源页 / 配方页 / 用途页 / 机器 / 枪 / 液体 / DNA）
│   ├── MachinePanels.cs          # 机器静态配方（来自 IL）
│   ├── VanillaMachinePanel.cs    # 借用原版机器窗口塞样本
│   ├── CatalogTicker.cs          # 常驻 Update 驱动
│   ├── TooltipFollower.cs        # 跟随鼠标的提示框，每帧重建
│   ├── LauncherButton.cs         # 物品栏右下角按钮
│   ├── ClientSprite.cs           # 客人立绘解析
│   ├── AnchorTarget.cs           # 面板贴靠目标
│   ├── Carousel.cs               # 全部轮播游标
│   ├── CropVariants.cs           # 作物品质档表
│   ├── PreBuiltItems.cs          # 预构建键别名 + 耐久范围
│   ├── PanelWatchPatch.cs        # 窗口生命周期 postfix（按钮重锚）
│   ├── DragToDeletePatch.cs      # 把物品拖到图鉴上 → 删除
│   ├── GunSoundPatch.cs          # 拆枪探测期间静音 ratchet
│   └── DockedLayout.cs           # 停靠布局辅助
├── Localization/{en,zh,ru}.csv
├── docs/
├── examples/ProbablyEnoughItemsBridge.cs
└── install.ps1
```

## 生命周期

`ModEntry` 实现 `IMod`：

| 回调 | 做了什么 |
|---|---|
| `Init` | 建 `ModLog`，仅此而已——界面由数据驱动，贴靠目标在运行时解析 |
| `OnEnable` | 订阅 `OnGameLoadedLate`、`OnCreateTooltipLate`（两次）、`OnLootTablesLoaded`、`OnModItemDirectoryInit`；启动 `CatalogTicker`；安装 `PanelWatchPatch`、`DragToDeletePatch`、`GunSoundPatch`、`LoadCapture`、`IsTagCapture`；注册 F9 调试项 |
| `OnDisable` | 全部退订，并移除调试项 |

加载期的工作在 `OnGameLoadedLate` → `CatalogWindow.Warmup()` 里一次做完：建快照、发现搅拌机/熔炉配方、
探测拆枪产出、展开变体格子、预热第一页图标，再建 `RecipeIndex`、探测全部可改装枪的改装槽
（`GunMods.Warmup`）并建 `SourceIndex`。
`BoxProbe` 也在这里跑；掉落表还没就绪时，会在 `OnLootTablesLoaded` 再补一次。

## 运行时管线

1. **建快照** —— `CatalogDataBuilder` 遍历游戏注册的全部 `ItemDirectory`，并入所有 `ICatalogItemProvider`
   注入的条目，跳过 `UnusedDirectory` 与调试物品，按 id 排序。
2. **建索引** —— `RecipeIndex`（配方）、`UseTable`（`Data/uses.csv`）、`SourceIndex`（`Data/clients.csv`，
   第 12~17 列）、`LootSource`（掉落表 + `Data/loot_pools.csv` + `Data/box_pools.csv`）、
   `LiquidCatalog` / `DnaCatalog`。
3. **预热** —— 以上全部 + 变体展开 + 第一页图标，在游戏加载完成后一次做完，打开面板不再卡顿。
4. **节拍** —— `CatalogTicker` 每帧驱动 `AutoRefreshTick`：处理待重绘、分片建剩余格子
   （`CellsPerFrame = 8`）、重试失败的容器覆盖层采集（每格最多 2 次）、维持拖拽/提示框/立绘，
   并应用 `NotifyItemsChanged` 的增量补入。**没有**周期性重掷，也没有后台探测。
5. **变化检测** —— 打开面板时调用 `HasNewItems()`（注册表 id、提供者数量、额外条目集合）。
   新增物品走增量补入；只有条目被删 / 提供者集合变化才整表重建。

## 介入游戏的方式

| 需求 | 手段 |
|---|---|
| 游戏加载完成后执行 | `ModHook.OnGameLoadedLate` |
| 掉落表就绪后执行一次 | `ModHook.OnLootTablesLoaded` |
| 往提示框追加内容（且能与其它模组的条目共存） | `ModHook.OnCreateTooltipLate` |
| 其它模组注册物品目录时跟上 | `ModHook.OnModItemDirectoryInit` → `ItemCatalogApi.NotifyItemsChanged` |
| 读窗口矩形、拖拽状态、精灵修饰器 | 公开成员（`PixelWindow.rectTransform`、`getItemSpriteModifierForItemFunc`、`ItemMouseDragHandler`、`TreeNodeRender`） |
| 窗口显隐与布局变化 | `PanelWatchPatch` —— 给 `PixelWindow` 生命周期方法挂 postfix |
| 截获箱子内容 / 拆枪产物 | `LoadCapture` —— 只在显式 `Begin()` / `End()` 窗口内生效的 prefix |
| 学习枪械槽接受哪些 tag | `IsTagCapture` —— 同样的窗口机制 |

判断标准：**有官方钩子就用钩子，没有才打补丁。** 每个补丁范围都很窄，并各自带 Harmony id
（`modId + ".panelwatch"`、`".dragdelete"`、`".gunsound"`、`".loadcapture"`、`".istagcapture"`）；
捕获窗口之外不改变游戏的返回值。

## 硬性约束

1. **反射要保持在扫描器的豁免范围内。** 只用字面类型名 + 字面成员名：`typeof(SomeType).GetField("someField")` 可以，
   `obj.GetType().GetMember(name)` 不行；不要把 `Type` 或成员名当参数传进辅助函数，不要用拼接出来的字符串查成员。
   否则就必须声明 `reflection-external` 能力，而这个能力正在退役——游戏 2026-12-10 的更新会直接拒绝加载声明了它的模组。
   细节见 `manifest.xml` 里的注释块。
2. **绝对不要把 `ProbablyEnoughItems.dll` 打包进别的模组。** 列进 `<Assemblies>` 后，加载器会用你的清单把
   `ItemCatalog.ModEntry` 再实例化一次，所有钩子订阅翻倍。
3. **只在主线程调用。** 这里用到的游戏接口都不是线程安全的。
4. **不要重新引入周期性重掷或后台探测。** 随机性在变体格子展开时就固定了；单格条目一直用建格时那一份实例。
   `AutoRefreshTick` 只负责待重绘、分片建格与覆盖层重试。进游戏后持续分批探测会掉帧，用户已明确否决。
5. **Harmony 补丁必须带 id、范围要窄。** 用 `new Harmony(modId + ".panelwatch")`；不调用不带 id 的 `UnpatchAll()`；
   不去补丁那些「一旦补了就得替它维护行为」的方法。捕获窗口（`LoadCapture`）必须在 `finally` 里关闭，
   否则后续同类游戏调用会被静默吞掉。

## 调试手段

- `F8` —— 控制台，日志以模组名开头。
- `F9` → Special —— `Catalog: Dump Data`、`Dump Recipes`、`Dump Sources`、`Probe Sources`、`Reopen`。
- 通过 `ModHelper.WriteModDataFileText` 写出的文件：`box_probe.txt`（箱子内容）、
  `gun_disassembly.txt`（拆枪探测缓存）、`gun_slots.txt`（枪械槽 → 接受的 tag）、
  `tutorial_items.txt`（`HandleSkipIntro` 给予的教程物品）、`environment.txt`（游戏 / 模组共用签名）
  以及 `index_<名字>.txt`（`IndexManager` 缓存的索引，如 `index_gun-mod-guns.txt`）；
  手动来源探测另写 `source-probe.txt`。

## 扩展

- 物品状态 / 变体 → [API.zh-CN.md](API.zh-CN.md)
- 来源表 → [DATA.zh-CN.md](DATA.zh-CN.md)
