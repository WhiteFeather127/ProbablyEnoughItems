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
        internal string[] Items;      // 该来源可能摆出的全部物品 id（固定 + 随机，已去重）
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

        // ---------------------------------------------------------------- 建索引

        internal static void Build()
        {
            if (Built) return;

            _byItem.Clear();
            AllSources.Clear();
            ClientTypeCount = 0;
            LocationCount = 0;
            RandomItemCount = 0;

            var watch = System.Diagnostics.Stopwatch.StartNew();

            AddHardcoded();

            int commissary = 0;
            try { commissary = AddCommissary(); }
            catch (Exception e) { Warn("commissary: " + e.Message); }

            _lastCommissaryCount = CurrentCommissaryCount();

            watch.Stop();
            Built = true;

            if (ModEntry.Log != null)
            {
                ModEntry.Log.Log(string.Format(
                    "[sources] clients={0} locations={1} commissary={2} items={3} random={4} ({5} ms)",
                    ClientTypeCount, LocationCount, commissary, _byItem.Count, RandomItemCount,
                    watch.ElapsedMilliseconds));
            }
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
                    BuysTags = SplitIds(def.BuysTags),
                    BuysItems = SplitIds(def.BuysItems),
                    Items = CombineItems(def.Items, def.RandomItems)
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

                // 空货单的静态行没有意义，跳过；dynamic 行本来就不该有 items
                if (!dynamic && def.Items.Length == 0 && def.RandomItems.Length == 0)
                {
                    Warn("'" + def.Id + "' has no items, skipped");
                    continue;
                }

                AllSources.Add(source);

                if (def.Kind == SourceKind.Client) ClientTypeCount++;
                else LocationCount++;

                for (int k = 0; k < def.Items.Length; k++) Add(def.Items[k], source);

                for (int k = 0; k < def.RandomItems.Length; k++)
                {
                    Add(def.RandomItems[k], source);
                    RandomItemCount++;
                }

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
            return true;
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
                        try { hit = item.IsTag(word) || item.IsGameItemType(word); }
                        catch { hit = false; }
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
