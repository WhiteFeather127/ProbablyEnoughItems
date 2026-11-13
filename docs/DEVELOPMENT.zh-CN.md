# 开发说明

[English](DEVELOPMENT.md) | **简体中文**

[← 返回 README](../README.zh-CN.md)

代码约定、运行时管线，以及「让模组在以后的游戏版本里仍能被加载」的硬性规则。构建方式见 [BUILDING.zh-CN.md](BUILDING.zh-CN.md)。

## 目录结构

```
ProbablyEnoughItems/
├── ProbablyEnoughItems.csproj
├── manifest.xml                  # id / 名称 / 程序集 / 能力声明
├── ModEntry.cs                   # IMod 入口：日志、钩子订阅、F9 调试项
├── Api/
│   ├── ItemCatalogApi.cs         # 对外契约（只增不改，ApiVersion = 4）
│   └── CatalogVariantKit.cs      # 枚举表 + 隔离工具，与第三方共用
├── Catalog/
│   ├── ItemEntry.cs              # 物品数据模型
│   ├── CatalogDataBuilder.cs     # 注册表扫描、快照、增量补入
│   ├── RecipeData.cs             # 配方索引
│   ├── SourceData.cs             # 来源索引
│   ├── SourceTable.cs            # CSV 解析（base 继承、容错、缓存）
│   ├── SourceProbe.cs            # 运行时来源探针 / 报告
│   ├── LootSource.cs             # 战利品来源
│   ├── UseTable.cs               # 用途（转化）
│   └── LockKeys.cs
├── Data/                         # 来源表（改表重启生效，不用重编译）
├── UI/
│   ├── CatalogWindow.cs          # 面板、分页网格、变体判定、提示框、预热
│   ├── RecipeWindow.cs           # 详情窗口（来源页 / 配方页 / 用途页）
│   ├── CatalogTicker.cs          # 常驻刷新节拍
│   ├── TooltipFollower.cs        # 跟随鼠标的提示框
│   ├── LauncherButton.cs         # 物品栏右下角按钮
│   ├── ClientSprite.cs           # 客人立绘解析
│   ├── AnchorTarget.cs           # 面板贴靠目标
│   ├── PanelWatchPatch.cs        # 唯一的 Harmony 补丁
│   ├── DockedLayout.cs           # 停靠布局辅助
│   └── UiDump.cs                 # UI 结构导出
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
| `OnEnable` | 订阅 `OnGameLoadedLate`、`OnCreateTooltipLate`（两次）、`OnModItemDirectoryInit`；启动 `CatalogTicker`；安装 `PanelWatchPatch`；注册 F9 调试项 |
| `OnDisable` | 全部退订，并移除调试项 |

## 运行时管线

1. **建快照** —— `CatalogDataBuilder` 遍历游戏的 27 个 `ItemDirectory`，并入所有 `ICatalogItemProvider` 注入的条目，
   跳过 `UnusedDirectory` 与调试物品，按 id 排序。
2. **建索引** —— `RecipeIndex`（配方）、`UseTable`（`Data/uses.csv`）、`SourceIndex`（`Data/clients.csv`，
   卖什么在第 12~16 列）、`LootSource`（掉落表）与快照一起建好。
3. **预热** —— 游戏加载完成后统一解析图标，避免点开面板时卡顿。
4. **刷新** —— `CatalogTicker` 每帧跑节拍，`CatalogWindow` 每秒整页刷新一次：连续随机值重掷、图标按需换图。
5. **变化检测** —— 打开面板时调用 `HasNewItems()`（注册表 id、提供者数量、额外条目集合）；有变化就整表重建，
   否则复用现有快照。

## 介入游戏的方式

| 需求 | 手段 |
|---|---|
| 游戏加载完成后执行 | `ModHook.OnGameLoadedLate` |
| 往提示框追加内容（且能与其它模组的条目共存） | `ModHook.OnCreateTooltipLate` |
| 其它模组注册物品目录时跟上 | `ModHook.OnModItemDirectoryInit` |
| 读窗口矩形、拖拽状态、精灵修饰器 | 公开成员（`PixelWindow.rectTransform`、`getItemSpriteModifierForItemFunc`、`ItemMouseDragHandler`、`TreeNodeRender`） |
| 窗口显隐与布局变化 | `PanelWatchPatch` —— 给 `PixelWindow` 生命周期方法挂 postfix |

判断标准：**有官方钩子就用钩子，没有才打补丁。** 唯一的补丁之所以存在，是因为游戏没有「窗口矩形变了」的事件，
而轮询的代价是多达一秒的延迟、以及完全捕捉不到别的模组挪窗口。postfix 里只重排我们自己的按钮，不改游戏状态、
不改返回值。

## 硬性约束

1. **反射要保持在扫描器的豁免范围内。** 只用字面类型名 + 字面成员名：`typeof(SomeType).GetField("someField")` 可以，
   `obj.GetType().GetMember(name)` 不行；不要把 `Type` 或成员名当参数传进辅助函数，不要用拼接出来的字符串查成员。
   否则就必须声明 `reflection-external` 能力，而这个能力正在退役——游戏 2026-12-10 的更新会直接拒绝加载声明了它的模组。
   细节见 `manifest.xml` 里的注释块。
2. **绝对不要把 `ProbablyEnoughItems.dll` 打包进别的模组。** 列进 `<Assemblies>` 后，加载器会用你的清单把
   `ItemCatalog.ModEntry` 再实例化一次，所有钩子订阅翻倍。
3. **只在主线程调用。** 这里用到的游戏接口都不是线程安全的。
4. **刷新间隔固定为 1 秒。** 面板觉得重，就减少每次刷新的工作量；不要分批、不要降频，否则物品跟不上实时状态。
5. **Harmony 补丁必须带 id、范围要窄。** 用 `new Harmony(modId + ".panelwatch")`；不调用不带 id 的 `UnpatchAll()`；
   不去补丁那些「一旦补了就得替它维护行为」的方法。

## 调试手段

- `F8` —— 控制台，日志以模组名开头。
- `F9` → Special —— `Catalog: Dump UI`、`Dump Data`、`Dump Recipes`、`Dump Sources`、`Probe Sources`、`Reopen`。

## 扩展

- 物品状态 / 变体 → [API.zh-CN.md](API.zh-CN.md)
- 来源表 → [DATA.zh-CN.md](DATA.zh-CN.md)
