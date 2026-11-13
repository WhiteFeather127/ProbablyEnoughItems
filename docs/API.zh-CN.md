# Probably Enough Items 扩展接口

[English](API.md) | **简体中文**

[← 返回 README](../README.zh-CN.md)

其它模组用这份接口告诉图鉴两件事：

1. **这件物品一共有几种状态**（能枚举就按顺序展开成多格，不能枚举才随机取样）；
2. **注册表之外还有哪些条目**该出现在图鉴里。

| 项 | 值 |
|---|---|
| 命名空间 | `ProbablyEnoughItems.Api` |
| 契约文件 | [`Api/ItemCatalogApi.cs`](../Api/ItemCatalogApi.cs)（静态类名 `ItemCatalogApi`）、[`Api/CatalogVariantKit.cs`](../Api/CatalogVariantKit.cs) |
| 接口版本 | `ItemCatalogApi.ApiVersion == 4` |
| 零依赖模板 | [`examples/ProbablyEnoughItemsBridge.cs`](../examples/ProbablyEnoughItemsBridge.cs) |
| 模组 id | `probably_enough_items` |

**兼容承诺**：这里是长期契约，只增不改。新增能力 → 加成员并把 `ApiVersion` +1；必须不兼容时并排新增
`ItemCatalogApi2` / `ICatalogVariantProvider2` 之类的类型，旧成员保留。

---

## 1. 30 秒上手（零依赖）

不需要引用 `ProbablyEnoughItems.dll`，不需要在清单里写 `<Prerequisites>`，也不要把对方的 DLL 打包进自己的模组。

**第一步**：把 [`examples/ProbablyEnoughItemsBridge.cs`](../examples/ProbablyEnoughItemsBridge.cs) 拷进你的工程，改掉 `namespace`。

**第二步**：注册。

```csharp
public void Init(ModManifest manifest)
{
    _log = new ModLog(manifest);

    if (ProbablyEnoughItemsBridge.IsAvailable)
    {
        // 荧光莓一共 3 种状态，index 0/1/2 依次对应
        ProbablyEnoughItemsBridge.RegisterVariants("wine_berry", 3, CreateVariant);

        // 注册表里不存在、但想出现在图鉴里的物品
        ProbablyEnoughItemsBridge.RegisterItems("my_mod", BuildExtras);
    }
}
```

`IsAvailable == false`（图鉴没装）时，两个 `Register*` 直接返回 `false`，不抛异常、不刷错误日志，你的模组照常运行。

**第三步**（可选）：图鉴可能比你的模组后加载，失败时重试一次。

```csharp
if (!ProbablyEnoughItemsBridge.IsAvailable) ModHook.OnGameLoadedLate += OnGameLoaded;
```

---

## 2. 两种接入方式

| 方式 | 适用场景 | 依赖 |
|---|---|---|
| 委托 + 反射（桥接模板） | **跨模组，推荐** | 零依赖，只用 `Assembly-CSharp` 的 `GameItem` |
| 实现接口（`ICatalogVariantProvider`） | 与图鉴同仓库、或愿意做源码级引用 | 编译期需要 `ProbablyEnoughItems.Api` 命名空间 |

两种方式注册进去的东西在图鉴里地位相同，走同一条优先级链。

### 2.1 委托 + 反射

桥接内部的等价代码：

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

能这样做的前提是委托参数类型（`GameItem`、`List<GameItem>`）来自游戏程序集，双方不共享自定义类型；
否则接口实现无法跨程序集传递。

### 2.2 实现接口

```csharp
internal sealed class BerryVariants : ProbablyEnoughItems.Api.ICatalogVariantProvider
{
    public string ItemId { get { return "wine_berry"; } }
    public int VariantCount { get { return 3; } }

    public GameItem CreateVariant(int index)
    {
        var item = DirectoryMaster.Item("wine_berry");
        ApplyQuality(item, index);      // 必须确定：同一 index 结果等价
        return item;
    }
}
```

---

## 3. API 参考

### 3.1 `ItemCatalogApi`（静态类）

| 成员 | 说明 |
|---|---|
| `const int ApiVersion` | 接口版本，当前为 `4`；新增成员时 +1 |
| `RegisterVariantProvider(ICatalogVariantProvider)` | 注册变体提供者（接口版） |
| `RegisterVariantProvider(string itemId, int count, Func<int, GameItem> factory)` | 注册变体提供者（委托版，反射友好） |
| `RegisterVariantProvider(string itemId, int count, Func<int, GameItem> factory, Func<List<string>> labels)` | 同上；`labels` 参数**当前被忽略**（图鉴不在格子上写字），仅为不破坏旧调用方而保留 |
| `RegisterItemProvider(ICatalogItemProvider)` | 注册额外条目提供者（接口版） |
| `RegisterItemProvider(string modId, Func<List<GameItem>> factory)` | 注册额外条目提供者（委托版） |
| `NotifyItemsChanged()` / `NotifyItemsChanged(string reason)` | 通知「物品清单变了」：运行中新建物品、或某件物品的变体空间变大时调用 |
| `event Action<int> CatalogRefreshed` | 图鉴完成一次增量补入或整表重建后触发，参数为当前可见条目数 |
| `FindVariantProvider(string itemId)` | 按 id 查已注册的变体提供者，没有返回 `null` |
| `GetModuleShapes(string itemId)` | 查某件模块实际存在的占格形状（精灵名列表，顺序即图鉴展开顺序）；非模块返回空列表 |

规则：

- 同一个 `itemId` 重复注册变体提供者时**先注册者优先**，不做覆盖；
- `itemId` 为空、`factory` 为 `null` 会抛 `ArgumentException`；
- 全部成员必须在**主线程**调用（建议在 `Init` / `OnEnable`）；
- `CatalogRefreshed` 里抛出的异常只会写一条警告日志，不会打断图鉴刷新。

`NotifyItemsChanged` 的语义：

- 图鉴**不会**在这行调用里同步干活（这时你可能正处在游戏的目录遍历 / 生成流程中），只记一个标记，
  在之后的空闲节拍里增量补入并预热图标；同一帧内多次调用会合并成一次。
- 图鉴**不替你注册目录**。要让游戏能按 id 重建物品（存档读回、拖拽落地都依赖它），
  必须自己 `DirectoryMaster.AddDirectory` + `Add(id, factory)`。
- 只支持「新增 / 变体变多」；删掉已有物品会在下次打开图鉴时整表重建（较慢）。

### 3.2 `ICatalogVariantProvider`

| 成员 | 说明 |
|---|---|
| `string ItemId` | 目标物品 id，与注册表一致 |
| `int VariantCount` | 变体数量；**返回 0 表示未知**，图鉴退回内置判定 |
| `GameItem CreateVariant(int index)` | 生成第 `index` 个变体（`0 <= index < VariantCount`）；返回 `null` 表示该索引不可用 |

`CreateVariant` 必须是**确定函数**：图鉴靠它做顺序展开，同一 `index` 每次调用应产出等价结果。
不要在里面掷随机数——那会让多格展示失去意义。

> 标签成员（`GetVariantLabels`）在 v3 之后已被移除：图鉴不在格子上写字。不要依赖它。

### 3.3 `ICatalogItemProvider`

| 成员 | 说明 |
|---|---|
| `string ModId` | 来源模组 id，仅用于日志 |
| `List<GameItem> GetExtraItems()` | 要展示的物品列表，允许返回 `null` 或空列表 |

`GetExtraItems` 在**构建快照时**被调用（启动预热、以及打开图鉴时的变化检测）。请保持轻量：
需要现造物品的话，自己在 `Init` 阶段缓存好再返回。

返回的物品由你负责构造，图鉴只读取、不销毁。

**务必填好这几个字段**，否则提示框会很空：

| 字段 | 作用 |
|---|---|
| `name` | 提示框首行的名称 |
| `shortDescription`（空则回退 `flavorText`） | 名称下方的描述行 |
| `unitValue` | 「售价」行 |
| `itemTypes` | 类型徽章（如 `HOUSEHOLD_GOOD` → `[日用品]`） |

原因：注入条目在注册表里查不到，游戏原生的悬停提示框链路会退化成「只有类型 + 价格」，
所以图鉴对这些条目**直接使用自己的构建器**（名称 → 类型 → 售价 → 描述 → 来源 → ID），
并照常触发 `OnCreateTooltipEarly/Late` 钩子。

### 3.4 `CatalogVariantKit`（枚举表 + 隔离工具）

图鉴内置展开逻辑用的表，**与图鉴自己的分支共用同一份数据**（内部直接引用这些字段），
所以第三方不必抄一份、也不会因为游戏更新而失同步。

枚举表：

| 成员 | 说明 |
|---|---|
| `LearningEffectId` | 自学习算法效果 id（特殊：额外多出「0 属性 / 满属性」两格） |
| `ModuleEffectIds` | 模块效果完整清单，按游戏静态构造顺序 |
| `ModuleShapeSuffixes` | 模块占格精灵的后缀枚举（实际存在的才会被采用） |
| `ModuleEffectCells` | 一个占格形状内的格子顺序：无特殊效果 → 各效果 |
| `BerryForms` | 浆果形态（品相 + 视觉变体 + 展示标签） |
| `CigaretteIssueTags` | 香烟四处瑕疵标签（印章 / logo / 字体 / 盒色） |
| `InjectorIssueTags` | 免疫宁注射器五处瑕疵标签 |
| `StampIssueTags` | 食品券五处瑕疵标签 |
| `StampAmounts` | 食品券全部面额 `{40, 80, 120, 160, 200}` |
| `StampStateCount` | 每种面额的状态数 = 1 张真券 + 每种瑕疵 1 张假券 |

数据结构：

```csharp
public sealed class ModuleEffectCellInfo { public string Effect; public int Bonus; }
// Effect: 效果标签 id，null = 「无特殊效果」那一格
// Bonus : -1 = 非自学习；0 = 自学习但属性为 0；-2 = 自学习满属性

public sealed class BerryFormInfo { public int Condition; public int Visual; public string Label; }
// Condition: CONDITION 标签值（-2..2）；Visual: VISUAL 标签值；Label: 展示用短标签
```

隔离工具：

| 成员 | 说明 |
|---|---|
| `SnapshotModulePool()` / `RestoreModulePool(object)` | 造物品前后包一层，避免把游戏共享的随机词条池抽干。**注意：当前版本 `ModuleEffectHelper.moduleEffects` 的值不是集合，快照实际为空，调用无害但**不**产生保护效果**——不要依赖它 |
| `IsolateTagState(GameItem)` | 把标签存储换成独立副本。`TagSystem.Clone()` 是浅拷贝（内部字典共用），复用同一模板实例造出的多个变体会互相覆盖标签，造完每个变体都调一次 |
| `NormalizeIssues(GameItem, int variant, string[] issueTags)` | 让「实际品质」与格子表示一致：`variant = 0` 无瑕疵，`variant = k` 只保留第 k 处瑕疵（1-based） |
| `ForceStampAmount(GameItem, int amount)` | 把食品券面额改成指定值，名称与售价一起改 |
| `StripAuthenticityLabels(GameItem)` | 去掉标签打印机贴上去的真品/赝品那条 `ItemFeature`，不动分类标签 |

---

## 4. 图鉴如何使用这些数据

### 4.1 变体展开：有限集合展开成多格

`VariantCellCount > 1` 的物品会**展开成 N 个格子**（JEI 的 matching stacks），每格固定一个变体，
显示名带 `#N` 后缀。判定顺序（`CatalogWindow.VariantCellCount`）：

| 顺序 | 来源 | 格数 |
|---|---|---|
| ① | `ICatalogVariantProvider` | `VariantCount`（**不做上限截断**，请自己控制数量） |
| ② | 内置特殊形态 | 刮刮乐 1 格；香烟 5 格；免疫宁注射器 5 格；食品券 30 格（5 面额 × 6 状态） |
| ③ | 模块 | 占格形状数 × `ModuleEffectCells.Length` |
| ④ | 浆果 | `BerryForms.Length` |
| ⑤ | 硬编码生成器表 | 表长（>1 才展开） |
| — | 其他（连续随机值） | 1 格，每秒重掷 |

所以**注册提供者就等于接管了这件物品的展示方式**，比内置判定更准，也不会浪费格子。
变体实例在渲染时按需创建并缓存到该格（启动预热不会批量造物品）。

### 4.2 额外条目的合并规则

构建快照时：

1. 先按注册表（各 `ItemDirectory`）建条目；
2. 再并入 `GetExtraItems()` 的结果，遇到以下情况跳过：
   - id 已存在于注册表 → **注册表优先**；
   - 多个提供者给出同一个 id → 只收录第一次出现的；
3. 合并后按 id 字典序排序，额外条目自然落到对应前缀位置。

### 4.3 什么时候会重新拉取

| 时机 | 行为 |
|---|---|
| 游戏加载完成 | 预构建快照 + 预热图标 |
| 打开图鉴 | `HasNewItems()` 检测：注册表 id 变化、提供者数量变化、额外条目集合变化 → 整表重建 |
| 运行中 | 每秒整页刷新一次，连续随机值重掷、图标按需换图 |
| 调用 `NotifyItemsChanged` | 下一个空闲节拍增量补入，然后触发 `CatalogRefreshed` |

注册得晚（比如挂到 `OnGameLoadedLate`）不会丢：提供者数量变化会触发下次打开图鉴时重建。

---

## 5. 约束（重要）

1. **不要捆绑 `ProbablyEnoughItems.dll`。**
   扫描器要求「引用的非游戏程序集写进 `<Assemblies>`」，但列进去之后，加载器会遍历该 DLL 找 `IMod` 实现，
   用你的清单再实例化一次 `ItemCatalog.ModEntry` —— 所有钩子订阅翻倍，
   最直观的表现是提示框里 `ID: xxx` 出现两次。
2. **不要缓存 `GameItem` 引用长期持有。** 变体请每次现造。
3. **不要在 `CreateVariant` 里改全局状态**（RNG、`PlayerStore`、静态表）。
4. **主线程调用**，回调也在主线程执行，别开线程。
5. 图鉴重掷用的随机流是隔离的（`UnityEngine.Random.state` 会原样还原），你的实现也不应该推进游戏随机流。
6. 造物品不要裸调 `DirectoryMaster.Item` 之后就乱改共享实例——图鉴自己走 `CreatePreviewItem`（RNG / uniqueId 还原）；
   第三方至少要做到「每次现造、不持有、不改全局」。

---

## 6. 常见问题

| 症状 | 原因 | 处理 |
|---|---|---|
| 提示框里 `ID: xxx` 出现两行 | 捆绑了 `ProbablyEnoughItems.dll` 并写进 `<Assemblies>` | 改走桥接，删掉那个 `<Assembly>` 与随包副本 |
| 日志 `unlisted-reference: xxx.dll -> references ProbablyEnoughItems` | 编译期引用了图鉴但没写进 `<Assemblies>` | 改用反射（推荐），或放弃编译期引用 |
| 注册成功但图鉴没有变化 | `VariantCount` 返回 0；或 id 拼错；或图鉴已构建过且提供者数量没变 | 检查 id；在 `Init` 里注册，或调用 `NotifyItemsChanged` |
| 额外条目没出现 | 该 id 已在注册表里（注册表优先） | 换一个专属 id |
| 图标缺失 | `spriteAtlasPath` / `spritePath` 不对 | 参照原版物品的值填 |
| `GetMethod("RegisterVariantProvider", …)` 返回 null | 图鉴版本低于 v2 | `ApiVersion >= 2` 才有委托版重载 |
| 只有一个变体的实例被所有格子共用、互相覆盖 | 复用了同一个模板实例 | 每个变体都现造，并调 `CatalogVariantKit.IsolateTagState` |

---

## 7. 版本记录

| 版本 | 内容 |
|---|---|
| v1 | `ICatalogVariantProvider` / `ICatalogItemProvider` + 接口版注册方法 |
| v2 | 新增委托版重载 `RegisterVariantProvider(string,int,Func<int,GameItem>[,Func<List<string>>])`、`RegisterItemProvider(string,Func<List<GameItem>>)`，供纯反射（零依赖）调用 |
| v3 | 新增 `CatalogVariantKit`（枚举表 + 隔离工具）与 `GetModuleShapes`；移除 `GetVariantLabels`（图鉴不再在格子上写字） |
| v4 | 新增 `NotifyItemsChanged`（运行中补物品）与 `CatalogRefreshed` 事件 |

调用方建议这样兼容未来版本：

```csharp
var method = api.GetMethod("RegisterVariantProvider",
    new[] { typeof(string), typeof(int), typeof(Func<int, GameItem>) });

if (method == null) return false;   // 图鉴版本过低，静默跳过，不要报错
```

---

## 8. 许可

本仓库以 [GNU LGPL v3.0](../LICENSE) 发布（附加条款见 [LICENSE.GPL](../LICENSE.GPL)）。
在这份许可下，你可以依赖本模组做自己的模组并闭源、含商业发行；只有在你修改了本仓库代码时，
改动部分才需要以 LGPL-3.0 公开，并且分发时要提供对应源码、允许使用者替换 dll。
