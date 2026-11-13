# 维护来源表

[English](DATA.md) | **简体中文**

[← 返回 README](../README.zh-CN.md)

图鉴对每件物品回答两个问题：*从哪来*、*能干什么*。后者在运行时由配方与掉落数据推导，前者不行——游戏没有给
模组任何「这个客人卖什么」的接口，那些物品清单只以字符串常量的形式存在于 `StoreClientList*.CreateXxx()` 与
编译器生成的回调的 IL 里。

所以客人这一侧的数据是**离线从游戏 IL 提取**后存成 CSV 的。改表 + 重启游戏即可生效，不用重新编译。

> 提取脚本（`tools/audit/`）与审计记录（`SOURCES_AUDIT.md`）位于开发工作区，不在本仓库内。
> 这里的 CSV 是那套流程的输出，逐行证据记录在审计表里。

## 文件

| 文件 | 内容 |
|---|---|
| `Data/clients.csv` | 谁（阵营 → 客人）**以及这个客人卖什么**——第 12~16 列 |
| `Data/uses.csv` | 对物品本身动手的转化——开箱、撕包装、喝掉、涂抹、组合——与机器配方索引分开 |
| `Data/box_contents.csv` | 箱子真正能开出什么，从 `PreBuiltItemHelper.Fill*` 反汇编得到；只列可能出现的物品，不记概率 |
| `Data/box_pools.csv` | 开箱要的卡，以及没有内容物表时才用的兜底池 |

`Data/goods.csv` 已不再随包分发：它的列并入了 `clients.csv` 第 12~16 列。若文件夹里还残留一份，仍会被解析并与
新列叠加（见文末），但新数据只写 `clients.csv`。

每个文件头部都有 `#` 注释说明自己的列；本页只讲容易写错的部分。

## `clients.csv` — 谁，以及他卖什么

按阵营分节：以 `# ===== 阵营 =====` 开头的注释行 + 一行 `faction,<key>` 开启一个阵营，下面逐行写属于它的客人，
客人行因此不重复写阵营。

必填列：

```
kind,id,name_key,location_id,fallback_key,profession,sprite
```

可选列：

| 列 | 位置 | 含义 |
|---|---|---|
| `water` | 8 | 该客人的容器灌什么水：`pure` / `hq` / `base` / `rust` / `ghost` / `gutterflow`。**留空 = 完全没水，只卖空瓶。** 不要兜底成 `base`——把只卖空瓶的客人画成满瓶是错的。只有 IL 里确实调了 `FillWith*` / `WaterPremadeHelper` 的客人才填。写成 `水种@物品id\|物品id` 可以只给列出的容器灌水（例如供应商只灌 `large_bottled_water`） |
| `empty` | 9 | `1` = **既卖水又卖瓶**，来源页会把每件容器拆成「原始空瓶」+「灌好的满瓶」两格。纯空瓶客人（第 8 列留空）不要填，没水可拆 |
| `buys_tags` | 10 | 这个客人收购的种类标签（`WATER` / `WEAPON` …，`|` 分隔） |
| `buys_items` | 11 | 这个客人收购的具体物品 id（`|` 分隔） |
| `sells` | 12 | 他摆出来卖的货 |
| `base` | 13 | 先继承另一个来源的清单，再应用本行的改动 |
| `exclude` | 14 | 从继承来的清单里剔掉指定 id |
| `random_items` | 15 | IL 里只在部分情况下出现的 id（受随机数或天数条件保护），单独存放以便界面标成「可能出现」 |
| `dynamic` | 16 | `commissary` = 商品运行时从存档货架读，本行只提供显示名 |

注意：

- `sells` / `buys_items` / `random_items` 里的物品 id 用 `|` 分隔。
- 多个客人共用同一个 `name_*` key 时会合并成一行来源，图鉴里不会出现重名来源。
- `clients.csv` 的**物理行顺序**就是图鉴里的显示顺序——调顺序要挪行，不要靠别的手段。
- 格式错的行只会在 `Player.log` 里记一条 `[sources]` 警告并跳过，图鉴照常工作。

## 改表流程

1. 直接编辑 CSV（UTF-8，不需要 BOM）
2. 重启游戏（数据按会话缓存）
3. 打开图鉴点开对应来源页核对；按 `F8` 看有没有 `[sources]` 警告
4. 如果客人开始卖带水的容器，记得填 `water` 列——这是最常见的错

## 残留的 `goods.csv`

如果 `Data/goods.csv` 还在，它会被按旧的 6 列格式（`id,base,exclude,items,random_items,dynamic`）解析，并**叠加**
到上面的列之上；两边都有值时以 `clients.csv` 为准。`id` 在客人表里找不到的行会报
`[sources] ... is not in Data/clients.csv, ignored`。这个文件不是必需的，也不随包分发；新数据放 `clients.csv`。

## 表里没有的东西

- **配方与用途**：运行时从游戏读取。
- **配给站存货**：从存档读取，`clients.csv` 只贡献显示名。
- **变体展开**（这件物品有多少种状态）：由代码判定，见 [API.zh-CN.md](API.zh-CN.md) §4.1。
