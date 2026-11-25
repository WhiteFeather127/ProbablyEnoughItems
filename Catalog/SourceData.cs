using System;
using System.Collections.Generic;

namespace ItemCatalog.Catalog
{
    /// <summary>物品获取途径的种类。</summary>
    internal enum SourceKind
    {
        /// <summary>客人（按类型分组）。</summary>
        Client = 0,

        /// <summary>出门后的地图地点（博士、配给站…）。</summary>
        Location = 1,

        /// <summary>太空站点商贩（当前版本无入口，预留）。</summary>
        SpaceStation = 2,

        /// <summary>太空随机遭遇（当前版本无入口，预留）。</summary>
        SpaceEncounter = 3
    }

    /// <summary>「这件物品可以从哪里得到」的一条记录。</summary>
    internal sealed class SourceInfo
    {
        internal SourceKind Kind;
        internal string SourceId;     // 客人类型 id / 地点 id
        internal string Name;         // 兜底显示名（游戏本地化取不到时用）
        internal string NameKey;      // 客人名的游戏 key（name_dr._jackson 等），渲染时现场取
        internal string LocationId;   // 地点 id，用地点自己的 GetTitle() 取名
        internal string Faction;      // 阵营显示名的本地化 key（大标题用，来自 CSV faction 列）
        internal string Sprite;       // 客人立绘：固定 key 或 "pool:方法名"
        internal string Water;        // 该客人的容器灌什么水：pure/hq/base/rust/ghost/gutterflow
        internal bool SellsEmpty;     // 他是否也摆空瓶（拾荒者这类：有水也有原始容器）
        internal string[] BuysTags;   // 他收购的物品**种类标签**（WATER / FOOD / WEAPON…）
        internal string[] BuysItems;  // 他收购的**具体物品 id**（例如 bottled_water_premium）

        /// <summary>
        /// 收购 WATER 时承认的水质档位（pure / hq / base…），来自 willAcceptItem 谓词；
        /// 空 = 任意水。标签格下面写这几个档位。
        /// </summary>
        internal string[] BuysWater;
        internal string[] Items;      // 该来源可能摆出的全部物品 id（固定 + 随机，已去重）

        /// <summary>
        /// 以物易物：他摆出来的每一组货（`clients.csv` 第 18 列）。
        /// 每组是展开后的物品 id 列表，**保留重复**（wire*4 就是四个 wire 格子）；
        /// 空 = 不是以物易物的客人。
        /// </summary>
        internal List<string[]> BarterCombos;

        /// <summary>以物易物他索要的东西（第 19 列），带数量。</summary>
        internal List<BarterWant> BarterWants;

        internal bool IsBarter
        {
            get { return BarterCombos != null && BarterCombos.Count > 0; }
        }

        /// <summary>以物易物的一格：具体物品或种类标签 + 数量。</summary>
        internal sealed class BarterWant
        {
            internal string Id;         // 具体物品（pink_injector…），与 Tag 二选一
            internal string Tag;        // 种类标签（FOOD / NARCOTIC…）
            internal int Amount;
            internal string[] Blacklist;  // 该标签里游戏不认的品种（starch_grade / junk_grade）
        }

        private List<ItemPart> _parts;

        /// <summary>
        /// 和其它关系同一套角色视图：他卖的货 → Output；他收购的具体物品 / 标签 → Material。
        /// 惰性生成，Items / BuysItems 仍是查询用的主数据。
        /// </summary>
        internal List<ItemPart> Parts
        {
            get
            {
                if (_parts != null) return _parts;

                _parts = new List<ItemPart>();

                if (Items != null)
                {
                    for (int i = 0; i < Items.Length; i++)
                    {
                        string id = Items[i];
                        if (string.IsNullOrEmpty(id)) continue;

                        _parts.Add(new ItemPart
                        {
                            Role = PartRole.Output,
                            Id = id,
                            Amount = 1
                        });
                    }
                }

                if (BuysItems != null)
                {
                    // 以物易物的索要清单按数量重复写了多次（页面要画多个格子），
                    // 关系本体只留一份：同一件物品不重复登记 Material。
                    var seenItems = new HashSet<string>(StringComparer.Ordinal);

                    for (int i = 0; i < BuysItems.Length; i++)
                    {
                        string id = BuysItems[i];
                        if (string.IsNullOrEmpty(id)) continue;
                        if (!seenItems.Add(id)) continue;

                        _parts.Add(new ItemPart
                        {
                            Role = PartRole.Material,
                            Id = id,
                            Amount = 1
                        });
                    }
                }

                if (BuysTags != null)
                {
                    var seenTags = new HashSet<string>(StringComparer.Ordinal);

                    for (int i = 0; i < BuysTags.Length; i++)
                    {
                        string tag = BuysTags[i];
                        if (string.IsNullOrEmpty(tag)) continue;
                        if (!seenTags.Add(tag)) continue;

                        _parts.Add(new ItemPart
                        {
                            Role = PartRole.Material,
                            Tags = new List<string> { tag },
                            DisplayName = tag,
                            Amount = 1
                        });

                        // 「收 WATER」这类液体标签：液体条目本身也算他收的东西，
                        // 否则查 water / 杂质时看不到这些客人（和 WantedBy 的液体规则对齐）。
                        string liquidId = MatchLiquidId(tag);
                        if (!string.IsNullOrEmpty(liquidId))
                        {
                            _parts.Add(new ItemPart
                            {
                                Role = PartRole.Material,
                                Id = LiquidCatalog.EntryPrefix + liquidId,
                                Amount = 1
                            });
                        }
                    }
                }

                // 卖水的容器：液体条目本身也是一条来源（查 water / 水里混的杂质时能看到这家店）。
                var waterLiquids = LiquidManager.WaterLiquids(Water);
                for (int w = 0; w < waterLiquids.Count; w++)
                {
                    string liquidId = waterLiquids[w];
                    if (string.IsNullOrEmpty(liquidId)) continue;

                    _parts.Add(new ItemPart
                    {
                        Role = PartRole.Output,
                        Id = LiquidCatalog.EntryPrefix + liquidId,
                        Amount = 1
                    });
                }

                return _parts;
            }
        }

        /// <summary>标签是不是某种液体（WATER → water）；是就返回液体 id。</summary>
        private static string MatchLiquidId(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;

            try
            {
                var liquids = Liquid.Liquids;
                if (liquids == null) return null;

                for (int i = 0; i < liquids.Count; i++)
                {
                    var liquid = liquids[i];
                    if (liquid != null &&
                        string.Equals(liquid.identifier, tag, StringComparison.OrdinalIgnoreCase))
                    {
                        return liquid.identifier;
                    }
                }
            }
            catch { }

            return null;
        }
    }

    /// <summary>
    /// 全量「获取途径」索引：物品 id → 能从哪得到。
    ///
    /// 数据来源分两种：
    ///   ① **配置表**（<c>Data/clients.csv</c> + <c>Data/goods.csv</c>，解析见 <c>SourceTable.cs</c>）：
    ///      客人 / 博士 / 供应商的货。这些 id 写死在游戏的 <c>StoreClientList</c> 方法里，
    ///      游戏没有给模组数据接口（只有 OnGenerateCustomer* 事件），只能离线抄成一张表；
    ///      放在 CSV 里是为了改表不用重新编译。
    ///   ② **运行时读取**：配给站读存档里的货架（<c>PlayerStore.commissaryData</c>），
    ///      每天只会变库存数字，商品身份固定；数量变化时自动重建。
    /// </summary>
    internal static partial class SourceIndex
    {
        /// <summary>配置表里的一行：一个来源 + 它提供的全部物品。</summary>
        private sealed class HardcodedSource
        {
            internal SourceKind Kind;
            internal string Id;
            internal string ClientNameKey;   // 客人名的游戏 key（LocHelper.GetLocalizedClientName）
            internal string LocationId;      // 地点 id（AfterhourLocation.GetTitle）
            internal string FallbackKey;     // 取不到游戏名时用我们自己的本地化 key
            internal string Faction;         // 阵营显示名的本地化 key（原版 ClientName 表 / 模组兜底）
            internal string Sprite;          // 客人立绘 key 或 pool:方法名
            internal string Water;           // 该客人的容器灌什么水（第 8 列，留空=空瓶）
            internal bool Empty;             // 第 9 列：他也摆空瓶（1/yes）
            internal string BuysTags;        // 第 10 列：收购的标签，| 分隔
            internal string BuysItems;       // 第 11 列：收购的具体物品 id，| 分隔
            internal string Sells;           // 第 12 列：他摆出来卖的货（合并原 goods.csv 的 items）
            internal string Base;            // 继承另一个 id 的货单（解析后清空）
            internal string[] Exclude;       // 从继承结果里剔除的 id
            internal string[] Items;         // 固定会摆出的货
            internal string[] RandomItems;   // 随机分支的所有可能产出（全部列出，不漏）
            internal string Dynamic;         // non-null 表示商品运行时补，自己不列物品
            internal string BuysWater;       // 收购 WATER 时要的水质档位（pure|hq…）

            // 第 18 / 19 列：以物易物。组合里允许重复（wire*4），索要项带数量。
            internal string BarterCell;
            internal string BarterWantsCell;
            internal List<string[]> BarterCombos;
            internal List<SourceInfo.BarterWant> BarterWants;

            /// <summary>
            /// 解析后的收购清单。以物易物的客人用第 19 列（可含重复，页面按数量画格子），
            /// 其余客人用第 10 / 11 列（去重）。AddDefinitions 直接取这两份。
            /// </summary>
            internal string[] BuyTags;
            internal string[] BuyItems;
            internal string[] BuyWaterTags;
        }

        /// <summary>物品 id → 来源列表。</summary>
        private static readonly Dictionary<string, List<SourceInfo>> _byItem =
            new Dictionary<string, List<SourceInfo>>(StringComparer.Ordinal);

        internal static readonly List<SourceInfo> AllSources = new List<SourceInfo>();

        internal static bool Built { get; private set; }
        internal static int ClientTypeCount { get; private set; }
        internal static int LocationCount { get; private set; }

        /// <summary>随机分支可能产出的物品条目总数（日志用）。</summary>
        internal static int RandomItemCount { get; private set; }

        private static int _lastCommissaryCount = -1;

        /// <summary>本索引在 <see cref="IndexManager"/> 里的名字 / 建好后的占位值。</summary>
        private const string IndexName = "sources";
        private static readonly object IndexMarker = new object();

        // ---------------------------------------------------------------- 建索引

        internal static void Build()
        {
            IndexManager.Get(IndexName, BuildCore);
        }

        private static object BuildCore()
        {
            _byItem.Clear();
            AllSources.Clear();
            ClientTypeCount = 0;
            LocationCount = 0;
            RandomItemCount = 0;

            var watch = System.Diagnostics.Stopwatch.StartNew();

            AddHardcoded();

            // 教程物品：跳过开场时 HandleSkipIntro 直接塞进背包的固定一批。
            // 清单只存在于方法体的 IL 里，由 TutorialProbe 截获并缓存（无缓存时用回退表）。
            try { AddTutorialSource(); }
            catch (Exception e) { Warn("tutorial: " + e.Message); }

            int commissary = 0;
            try { commissary = AddCommissary(); }
            catch (Exception e) { Warn("commissary: " + e.Message); }

            _lastCommissaryCount = CurrentCommissaryCount();

            watch.Stop();
            Built = true;

            AuditLookup();

            if (ModEntry.Log != null)
            {
                ModEntry.Log.Log(string.Format(
                    "[sources] clients={0} locations={1} commissary={2} items={3} random={4} ({5} ms)",
                    ClientTypeCount, LocationCount, commissary, _byItem.Count, RandomItemCount,
                    watch.ElapsedMilliseconds));
            }

            return IndexMarker;
        }

        /// <summary>
        /// 反查保证：货单里每个 id 要么本身就是目录物品，要么能通过别名
        /// （状态后缀 / random_* 展开）落到一个目录物品上。两条都不满足的
        /// id 玩家永远查不到来源，这里逐个记日志暴露出来。
        /// </summary>
        private static void AuditLookup()
        {
            int gaps = 0;
            foreach (var pair in _byItem)
            {
                if (RecipeIndex.FindEntry(pair.Key) != null) continue;
                if (HasResolvableAlias(pair.Key)) continue;

                gaps++;
                if (gaps <= 12) Warn("反查缺口 '" + pair.Key + "'（" + pair.Value.Count + " 个来源挂在这里）");
            }

            if (gaps > 0) Warn("共 " + gaps + " 个 id 无法从目录物品反查到来源（见上）");
        }

        /// <summary>这个 id 是否能通过别名落到一个目录物品上。</summary>
        private static bool HasResolvableAlias(string id)
        {
            string alias = LootSource.BaseItemId(id);
            return !string.IsNullOrEmpty(alias) && alias != id
                   && RecipeIndex.FindEntry(alias) != null;
        }

        private static void AddHardcoded()
        {
            // 物理行顺序即图鉴显示顺序（CSV 已按 阵营 → 职业 → 注册名 排好）。
            AddDefinitions(LoadDefinitions());
        }

        private static void AddDefinitions(HardcodedSource[] defs)
        {
            if (defs == null) return;

            for (int i = 0; i < defs.Length; i++)
            {
                var def = defs[i];
                if (def == null) continue;
                if (def.Items == null) def.Items = new string[0];
                if (def.RandomItems == null) def.RandomItems = new string[0];

                // 配置里绝大多数条目只有游戏自己的 name_* key、没有模组兜底文案；
                // 这时用注册名兜底，避免本地化表没加载时显示成 "?"。
                string fallback = string.IsNullOrEmpty(def.FallbackKey)
                    ? def.Id
                    : ModEntry.T(def.FallbackKey);

                var source = new SourceInfo
                {
                    Kind = def.Kind,
                    SourceId = def.Id,
                    NameKey = def.ClientNameKey,
                    LocationId = def.LocationId,
                    Name = fallback,
                    Faction = def.Faction,
                    Sprite = def.Sprite,
                    Water = def.Water,
                    SellsEmpty = def.Empty,
                    BuysTags = def.BuyTags ?? new string[0],
                    BuysItems = def.BuyItems ?? new string[0],
                    BuysWater = def.BuyWaterTags ?? new string[0],
                    Items = CombineItems(def.Items, def.RandomItems),
                    BarterCombos = def.BarterCombos,
                    BarterWants = def.BarterWants
                };

                // dynamic=commissary：商品运行时从存档货架读，这里只登记来源元信息。
                // 其它 dynamic 值不认识，记日志后当普通行处理（至少不会静默丢数据）。
                bool dynamic = !string.IsNullOrEmpty(def.Dynamic);
                if (dynamic)
                {
                    if (string.Equals(def.Dynamic, "commissary", StringComparison.OrdinalIgnoreCase))
                    {
                        _commissarySource = source;
                    }
                    else
                    {
                        Warn("unknown dynamic '" + def.Dynamic + "' on '" + def.Id + "'");
                        dynamic = false;
                    }
                }

                // 只买不卖的客人（woundedMan / clinicNurse / pharmacist / cartelWoundedSoldier …）
                // 货单是空的，但它要出现在「求购」里。以前这里一律跳过，
                // 于是这些买家整个被丢掉，医疗品、毒品的求购列表就空了。
                bool buys = (def.BuyTags != null && def.BuyTags.Length > 0)
                         || (def.BuyItems != null && def.BuyItems.Length > 0);

                // 既不卖也不买的静态行没有意义；dynamic 行本来就不该有 items
                if (!dynamic && !buys && def.Items.Length == 0 && def.RandomItems.Length == 0)
                {
                    Warn("'" + def.Id + "' has no items, skipped");
                    continue;
                }

                AllSources.Add(source);

                if (def.Kind == SourceKind.Client) ClientTypeCount++;
                else LocationCount++;

                for (int k = 0; k < def.Items.Length; k++)
                {
                    Add(def.Items[k], source);
                    AddAliasSource(def.Items[k], source);
                }

                for (int k = 0; k < def.RandomItems.Length; k++)
                {
                    Add(def.RandomItems[k], source);
                    AddAliasSource(def.RandomItems[k], source);
                    RandomItemCount++;
                }

                // 卖水的来源也挂到液体条目名下：`water` 列说明他把水灌进这些容器，
                // 液体页的「来源」就能反查到这些客人 / 地点（水质档位由 water 列给出）。
                // 水里混的杂质（重金属 / 微生物…）同样登记——它们也是容器里真实装着的东西，
                // 和成分行 / 提示框列出来的一致。
                var waterLiquids = LiquidManager.WaterLiquids(def.Water);
                for (int w = 0; w < waterLiquids.Count; w++)
                    Add(LiquidCatalog.EntryPrefix + waterLiquids[w], source);

                // 收购清单**不进** _byItem：那是「谁卖给我」的索引，
                // 收购是「谁从我这儿买」，属于用途侧，走 WantedBy 单独查。
            }
        }

        /// <summary>固定货 + 随机候选合并成「该来源所有可能物品」，去重、保持配置顺序。</summary>
        private static string[] CombineItems(string[] items, string[] randomItems)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>();

            for (int pass = 0; pass < 2; pass++)
            {
                string[] part = pass == 0 ? items : randomItems;
                if (part == null) continue;
                for (int i = 0; i < part.Length; i++)
                {
                    string id = part[i];
                    if (!string.IsNullOrEmpty(id) && seen.Add(id)) result.Add(id);
                }
            }
            return result.ToArray();
        }

        /// <summary>
        /// 来源的显示名：每次都现场问一遍游戏的本地化表（ClientName 表，与
        /// <c>DrJacksonLocation.GetTitle</c> 用的是同一个接口），拿不到才用我们的兜底文本。
        ///
        /// 不在建表时缓存名字的原因：Build 发生在读档预热阶段，
        /// 那时本地化表可能还没加载完，缓存下来就永远是兜底名。
        /// </summary>
        internal static string DisplayName(SourceInfo source)
        {
            if (source == null) return "?";

            // 地点：直接问游戏自己的地点对象要标题（DrJacksonLocation /
            // CommissaryLocation.GetTitle，内部走 LocHelper.GetLocalizedUI）
            if (!string.IsNullOrEmpty(source.LocationId))
            {
                try
                {
                    string title = LocationTitle(source.LocationId);
                    if (IsUsableName(title)) return title;
                }
                catch { }
            }

            // 客人：游戏 ClientName 表
            if (!string.IsNullOrEmpty(source.NameKey))
            {
                try
                {
                    string name = LocHelper.GetLocalizedClientName(source.NameKey);
                    if (IsUsableName(name)) return name;
                }
                catch { }

                // 游戏没有这个客人名键（Abby、教会工程师这类只有英文字面量的客人）：
                // 用模组自己的本地化补上，否则会直接显示 id
                try
                {
                    string own = ModEntry.T(source.NameKey);
                    if (IsUsableName(own) &&
                        !string.Equals(own, source.NameKey, StringComparison.OrdinalIgnoreCase))
                    {
                        return own;
                    }
                }
                catch { }
            }

            return string.IsNullOrEmpty(source.Name) ? "?" : source.Name;
        }

        /// <summary>
        /// 地点名走原版各自 GetTitle() 用的那条本地化：
        /// DrJacksonLocation.GetTitle → ClientName 表的 name_dr._jackson；
        /// CommissaryLocation.GetTitle → UI 表的 commissary_title。
        /// </summary>
        private static string LocationTitle(string id)
        {
            if (string.Equals(id, "dr_jackson", StringComparison.Ordinal))
                return LocHelper.GetLocalizedClientName("name_dr._jackson");

            if (string.Equals(id, "commissary", StringComparison.Ordinal))
                return LocHelper.GetLocalizedUI("commissary_title", new object[0]);

            return null;
        }

        /// <summary>没翻译时游戏常常回显 key（name_xxx / xxx_title）或空串，都当失败。</summary>
        private static bool IsUsableName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.IndexOf("name_", StringComparison.Ordinal) >= 0) return false;
            if (name.IndexOf("_title", StringComparison.Ordinal) >= 0) return false;

            // 缺翻译时游戏返回 `Translation Error"xxx" in yyy` 这种提示串，
            // 当成名字显示会在界面上直接露出内部错误文案。
            if (name.IndexOf("Translation Error", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            return true;
        }

        /// <summary>
        /// 教程物品：跳过开场时 <c>PlayerStore.HandleSkipIntro</c> 直接塞进背包的那一批。
        ///
        /// 这份清单不在任何数据表里——只以 <c>ldstr</c> 形式写在方法体里，游戏也没有
        /// 可安全调用的读取入口。所以清单来自 <see cref="TutorialProbe"/>
        /// （真实跳过开场时截获 + 缓存；没捕获过时用 IL 抄录的回退表）。
        ///
        /// 归到 <see cref="SourceKind.Location"/>：它没有客人身份，但确实是
        /// 「从一个固定来源拿到」，来源页上以「教程物品」单独一页出现。
        /// </summary>
        private static void AddTutorialSource()
        {
            string[] ids = TutorialProbe.Items;
            if (ids == null || ids.Length == 0) return;

            var source = new SourceInfo
            {
                Kind = SourceKind.Location,
                SourceId = "tutorial_grant",
                Name = ModEntry.T("src_tutorial"),
                Items = ids
            };

            AllSources.Add(source);
            LocationCount++;

            for (int i = 0; i < ids.Length; i++)
            {
                Add(ids[i], source);
                AddAliasSource(ids[i], source);
            }

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[sources] tutorial items=" + ids.Length);
        }

        /// <summary>配给站：读存档里的货架（价格 / 库存 / 补货都有）。</summary>
        /// <returns>加进来的商品数。</returns>
        private static int AddCommissary()
        {
            var store = PlayerStore.Instance;
            if (store == null || store.commissaryData == null) return 0;

            var list = store.commissaryData.commissaryItemDatas;
            if (list == null || list.Count == 0) return 0;

            // 配置里 dynamic=commissary 那行已登记来源元信息并计入 LocationCount；
            // 配置缺失时这里补一个最小兜底，保证配给站仍能查到（图鉴不能因缺表变空）。
            var source = _commissarySource;
            if (source == null)
            {
                source = new SourceInfo
                {
                    Kind = SourceKind.Location,
                    SourceId = "commissary",
                    LocationId = "commissary",
                    Name = ModEntry.T("src_commissary")
                };
                AllSources.Add(source);
                LocationCount++;
            }

            int added = 0;
            var goods = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < list.Count; i++)
            {
                var item = list[i];
                if (item == null || string.IsNullOrEmpty(item.identifier)) continue;

                Add(item.identifier, source);
                if (seen.Add(item.identifier)) goods.Add(item.identifier);
                added++;
            }

            // 配给站商品来自存档，配置表里没有；补进来让地点页也能列出全部商品
            source.Items = goods.ToArray();
            return added;
        }

        private static int CurrentCommissaryCount()
        {
            try
            {
                var store = PlayerStore.Instance;
                if (store == null || store.commissaryData == null) return -1;

                var list = store.commissaryData.commissaryItemDatas;
                return list == null ? -1 : list.Count;
            }
            catch { return -1; }
        }

        // ---------------------------------------------------------------- 写入 / 查询

        private static void Add(string itemId, SourceInfo source)
        {
            if (string.IsNullOrEmpty(itemId) || source == null) return;

            List<SourceInfo> list;
            if (!_byItem.TryGetValue(itemId, out list))
            {
                list = new List<SourceInfo>();
                _byItem[itemId] = list;
            }

            if (!list.Contains(source)) list.Add(source);
        }

        /// <summary>
        /// 预构建键（portable_water_purifier_used 这类）不是目录物品，货单只能记别名 id；
        /// 玩家反查的是本体（净水器）。解析出本体 id（数据层统一入口
        /// <see cref="LootSource.BaseItemId"/>：random_* 展开 / 满装盒对上 _container /
        /// 状态后缀剥离）再登记一份，本体的来源里就能看到这家店。
        /// 多余的登记无害：没有目录条目的 id 永远不会被查询。
        /// </summary>
        private static void AddAliasSource(string itemId, SourceInfo source)
        {
            string alias = LootSource.BaseItemId(itemId);
            if (!string.IsNullOrEmpty(alias) && alias != itemId) Add(alias, source);
        }

        internal static List<SourceInfo> For(string itemId)
        {
            RefreshIfNeeded();
            Build();

            List<SourceInfo> list;
            if (string.IsNullOrEmpty(itemId) || !_byItem.TryGetValue(itemId, out list)) return new List<SourceInfo>();
            return list;
        }

        /// <summary>
        /// 谁**收**这件东西 —— 用途侧的「求购」，和来源是两回事：
        /// 来源是「谁卖给我」，求购是「谁从我这儿买」。
        ///
        /// 命中两条：收购清单里的具体 id，或「按标签收购」。
        /// 收购词分两种：`WATER` 走 `IsTag`，`WEAPON` / `FOOD` 这类走
        /// `itemTypes`（`SetGameItemType`），所以两个都要试。
        /// </summary>
        internal static List<SourceInfo> WantedBy(string itemId, GameItem item)
        {
            RefreshIfNeeded();
            Build();

            var result = new List<SourceInfo>();

            for (int i = 0; i < AllSources.Count; i++)
            {
                var source = AllSources[i];
                if (source == null || source.Kind != SourceKind.Client) continue;

                bool hit = false;

                // 查液体条目时只按液体本身匹配标签：被查询的样本瓶是 bottled_water，
                // 直接 IsTag("WATER") 会把收水的客人带到所有液体（酒精、酸…）的用途里。
                string liquidId = LiquidCatalog.IsLiquidEntry(itemId)
                    ? itemId.Substring(LiquidCatalog.EntryPrefix.Length)
                    : null;

                if (!string.IsNullOrEmpty(itemId) && source.BuysItems != null)
                {
                    for (int k = 0; k < source.BuysItems.Length; k++)
                    {
                        if (string.Equals(source.BuysItems[k], itemId, StringComparison.Ordinal))
                        {
                            hit = true;
                            break;
                        }
                    }
                }

                if (!hit && item != null && source.BuysTags != null)
                {
                    for (int k = 0; k < source.BuysTags.Length; k++)
                    {
                        string word = source.BuysTags[k];

                        if (liquidId != null)
                        {
                            // 只有标签就是这种液体才命中：水 → WATER。
                            hit = string.Equals(word, liquidId, StringComparison.OrdinalIgnoreCase);
                        }
                        else
                        {
                            try { hit = item.IsTag(word) || item.IsGameItemType(word); }
                            catch { hit = false; }
                        }

                        if (hit) break;
                    }
                }

                if (hit && !result.Contains(source)) result.Add(source);
            }

            return result;
        }

        /// <summary>配给站货架在换档 / 解锁后会变，数量对不上就整张重建（很便宜）。</summary>
        private static void RefreshIfNeeded()
        {
            if (!Built) return;

            int count = CurrentCommissaryCount();
            if (count == _lastCommissaryCount) return;

            Invalidate();
        }

        internal static void Invalidate()
        {
            Built = false;
            IndexManager.Invalidate(IndexName);
        }

        // ---------------------------------------------------------------- 日志

        internal static void Dump()
        {
            Build();

            if (ModEntry.Log == null) return;

            ModEntry.Log.Log(string.Format("[sources] clients={0} locations={1} items={2} entries={3}",
                ClientTypeCount, LocationCount, _byItem.Count, AllSources.Count));

            int shown = 0;
            foreach (var pair in _byItem)
            {
                if (shown++ >= 25) break;

                var names = new List<string>();
                for (int i = 0; i < pair.Value.Count && i < 6; i++)
                {
                    var s = pair.Value[i];
                    if (s != null && !names.Contains(s.Name)) names.Add(s.Name);
                }

                ModEntry.Log.Log("[sources] " + pair.Key + "  <-  " + string.Join(", ", names.ToArray()));
            }
        }

        private static void Warn(string message)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("[sources] " + message);
        }
    }
}
