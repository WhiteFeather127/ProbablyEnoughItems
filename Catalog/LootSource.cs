using System;
using System.Collections.Generic;

namespace ItemCatalog.Catalog
{
    /// <summary>战利品表里的一条：能开出的物品 + 权重。</summary>
    internal sealed class LootItemRef
    {
        internal string Id;
        internal float Weight;
    }

    /// <summary>一整张战利品表：表 id + 它能开出的全部物品。</summary>
    internal sealed class LootTableInfo
    {
        internal string TableId;
        internal List<LootItemRef> Items = new List<LootItemRef>();

        private List<ItemPart> _parts;

        /// <summary>和其它来源 / 配方 / 使用同一套角色视图：表里能开出的物品 → Output。</summary>
        internal List<ItemPart> Parts
        {
            get
            {
                if (_parts != null) return _parts;

                _parts = new List<ItemPart>();
                if (Items == null) return _parts;

                for (int i = 0; i < Items.Count; i++)
                {
                    var item = Items[i];
                    if (item == null || string.IsNullOrEmpty(item.Id)) continue;

                    _parts.Add(new ItemPart
                    {
                        Role = PartRole.Output,
                        Id = item.Id,
                        Amount = 1
                    });
                }

                return _parts;
            }
        }
    }

    /// <summary>
    /// 「这个箱子能开出什么」的索引。
    ///
    /// 数据全部来自游戏自己的 API，没有任何反射：
    /// <c>LootRegistry.TableIds</c> 列出所有表 id，
    /// <c>LootRegistry.GetTableEntries(id)</c> 返回该表的 <c>List&lt;LootEntry&gt;</c>
    /// （<c>LootEntry.id</c> 就是物品 id，<c>LootEntry.weight</c> 是权重）。
    ///
    /// 表 id 往往就是**箱子/容器的物品 id**（`med_box`、`expedition_box`…），
    /// 所以来源页可以画成「使用这个箱子 → 下面铺开它可能开出的全部物品」。
    /// 给不出「哪个箱子」的情况只有一种：表 id 不是物品 id（纯场景掉落组），
    /// 那时前面那格会退化成文字。
    /// </summary>
    internal static class LootSource
    {
        /// <summary>本索引在 <see cref="IndexManager"/> 里的名字 / 建好后的占位值。</summary>
        private const string IndexName = "loot-tables";
        private static readonly object IndexMarker = new object();

        /// <summary>
        /// 物品 id → 含它的表（同一个物品在每张表里只出现一次）。
        /// 与 uses.csv / 机器配方共用 <see cref="RelationIndex{T}"/>：
        /// 只有产物方向，来源方向传 null。
        /// </summary>
        private static RelationIndex<LootTableInfo> _lootIndex = new RelationIndex<LootTableInfo>();

        private static LootTableInfo[] _tables = new LootTableInfo[0];

        internal static LootTableInfo[] Tables { get { EnsureBuilt(); return _tables; } }

        /// <summary>这件物品能从哪些表里开出来；没有就返回空列表。</summary>
        internal static List<LootTableInfo> For(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return Empty;

            EnsureBuilt();
            return _lootIndex.ByProduct(itemId);
        }

        private static readonly List<LootTableInfo> Empty = new List<LootTableInfo>();

        /// <summary>
        /// 反查：哪些箱子里能开出这件东西。两条路：
        ///   1. 加载期自动探测出的箱子内容物（<see cref="BoxProbe"/>）；
        ///   2. `box_pools.csv` 指向的池（运行时掉落表）里有它的箱子。
        /// 来源页用它把「箱子」补成 <c>item:&lt;箱子&gt;</c> 站点——
        /// 箱内物品与箱子从两个方向都能找到对方。
        /// </summary>
        internal static List<string> BoxesForItem(string itemId)
        {
            var boxes = new List<string>();
            if (string.IsNullOrEmpty(itemId)) return boxes;

            EnsureBuilt();
            EnsureBoxPools();

            foreach (var pair in _boxContents)
            {
                var items = pair.Value;
                for (int i = 0; i < items.Count; i++)
                {
                    // 箱内容物里的占位 id（random_quality_module 这类）按展开后的
                    // 本体 id 对上——具体模块的页才能反查到装它的箱子
                    if (items[i] != null &&
                        (string.Equals(items[i].Id, itemId, StringComparison.Ordinal) ||
                         string.Equals(ExpandPlaceholder(items[i].Id), itemId, StringComparison.Ordinal)))
                    {
                        if (!boxes.Contains(pair.Key)) boxes.Add(pair.Key);
                        break;
                    }
                }
            }

            var tables = _lootIndex.ByProduct(itemId);
            if (tables != null && tables.Count > 0)
            {
                foreach (var pair in _boxPools)
                {
                    if (boxes.Contains(pair.Key)) continue;

                    for (int i = 0; i < tables.Count; i++)
                    {
                        var t = tables[i];
                        if (t != null && string.Equals(t.TableId, pair.Value, StringComparison.Ordinal))
                        {
                            boxes.Add(pair.Key);
                            break;
                        }
                    }
                }
            }

            return boxes;
        }

        /// <summary>
        /// 这件物品是箱子时，返回它开出的那张表。
        ///
        /// 两条来源：
        ///   1. 表 id 恰好等于物品 id（少数情况）；
        ///   2. `Data/box_pools.csv` 里手写的「箱子 id → 池 id」——
        ///      游戏代码里没有这个绑定（`Directory.Item*` 无掉落字段，
        ///      池是在场景/POI 数据里绑的），只能落一张表。
        /// </summary>
        internal static List<LootTableInfo> AsBox(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return Empty;

            // 工具箱 / 远征箱 / 奢华补给箱不是随机掉落容器，不给「用途」页
            if (IsNonLootContainer(itemId)) return Empty;

            // 1) 有真实内容物就用它 —— 这是从 `PreBuiltItemHelper.Fill*`
            //    反汇编出来的，箱子生成时确实会塞这些东西，优先级最高。
            var contents = ContentsFor(itemId);
            if (contents != null && contents.Count > 0)
            {
                return new List<LootTableInfo>
                {
                    new LootTableInfo { TableId = itemId, Items = new List<LootItemRef>(contents) }
                };
            }

            EnsureBuilt();
            EnsureBoxPools();

            string pool = null;
            if (_boxPools != null && _boxPools.Count > 0) _boxPools.TryGetValue(itemId, out pool);

            List<LootTableInfo> list = null;

            for (int i = 0; i < _tables.Length; i++)
            {
                var table = _tables[i];

                bool hit = string.Equals(table.TableId, itemId, StringComparison.Ordinal)
                           || PoolMatches(pool, table.TableId);

                if (!hit) continue;

                if (list == null) list = new List<LootTableInfo>();
                list.Add(table);
            }

            if (list == null && IsBoxKnown(itemId))
            {
                // 登记过但还没填池：也要让「开启」页出现，先只画箱子 + 卡
                list = new List<LootTableInfo>
                {
                    new LootTableInfo { TableId = pool ?? string.Empty }
                };
            }

            return list ?? Empty;
        }

        /// <summary>池列允许写多个，用 `|` 分隔。</summary>
        private static bool PoolMatches(string poolCell, string tableId)
        {
            if (string.IsNullOrEmpty(poolCell) || string.IsNullOrEmpty(tableId)) return false;

            if (string.Equals(poolCell, tableId, StringComparison.Ordinal)) return true;

            var parts = poolCell.Split('|');
            for (int i = 0; i < parts.Length; i++)
                if (string.Equals(parts[i].Trim(), tableId, StringComparison.Ordinal))
                    return true;

            return false;
        }

        /// <summary>这件物品在 `box_pools.csv` 里登记过（不管池填没填）。</summary>
        internal static bool IsBoxKnown(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return false;

            EnsureBoxPools();

            return _boxPools.ContainsKey(itemId)
                   || _boxKeys.ContainsKey(itemId)
                   || _boxContents.ContainsKey(itemId);
        }

        private static readonly Dictionary<string, string> _boxPools =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private static readonly Dictionary<string, string> _boxKeys =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private static bool _boxPoolsLoaded;

        /// <summary>
        /// 开这个箱子要哪张卡：自动探测（<see cref="ApplyBoxKeycards"/>）在前，
        /// `box_pools.csv` 第 3 列作为手工覆盖。没写返回 null。
        /// </summary>
        internal static string KeyForBoxId(string boxId)
        {
            if (string.IsNullOrEmpty(boxId)) return null;

            EnsureBoxPools();

            string key;
            return _boxKeys.TryGetValue(boxId, out key) ? key : null;
        }

        /// <summary>箱子对应的池 id（来自 `box_pools.csv` 第 2 列）；没写返回 null。</summary>
        internal static string PoolForBoxId(string boxId)
        {
            if (string.IsNullOrEmpty(boxId)) return null;

            EnsureBoxPools();

            string pool;
            return _boxPools.TryGetValue(boxId, out pool) ? pool : null;
        }

        /// <summary>
        /// 池 id → 它属于哪个「地方」（`Data/loot_pools.csv`）。
        ///
        /// 掉落池不是给玩家看的名字：玩家遇到的是「垃圾倾倒场里翻出来的」「小偷身上带的」。
        /// 这张表把池挂到那个地方上，来源页就以那个地方为一个大类。
        ///
        /// 值有两种写法：`item:&lt;箱子 id&gt;` 或游戏本地化键（`location_dumping_grounds`）。
        /// </summary>
        private static readonly Dictionary<string, string> _poolSites =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private static bool _poolSitesLoaded;

        /// <summary>
        /// 这个池属于哪个地方；返回 null 表示「玩家遇不到」——
        /// 既没有箱子能开，也没写在 `loot_pools.csv` 里（只埋在场景数据里的那几个）。
        /// </summary>
        internal static string SiteFor(string poolId)
        {
            if (string.IsNullOrEmpty(poolId)) return null;

            // 能开出来的箱子本身就是最具体的地方，优先用它
            string box = BoxForPool(poolId);
            if (!string.IsNullOrEmpty(box)) return "item:" + box;

            EnsurePoolSites();

            string site;
            return _poolSites.TryGetValue(poolId, out site) ? site : null;
        }

        internal static bool IsUsed(LootTableInfo table)
        {
            return table != null && !string.IsNullOrEmpty(SiteFor(table.TableId));
        }

        private static void EnsurePoolSites()
        {
            if (_poolSitesLoaded) return;
            _poolSitesLoaded = true;

            string text = null;
            try { text = ModHelper.ReadModFileText(ModEntry.MOD_ID, "Data/loot_pools.csv"); }
            catch (Exception e) { Warn("loot_pools: " + e.Message); }

            if (string.IsNullOrEmpty(text)) return;

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                string[] cols;
                try { cols = SourceIndex.ParseCsvRow(line); }
                catch { continue; }

                if (cols.Length < 2) continue;

                string pool = cols[0].Trim();
                string site = cols[1].Trim();

                if (pool.Length == 0 || site.Length == 0) continue;
                if (string.Equals(pool, "pool", StringComparison.OrdinalIgnoreCase)) continue;   // 表头

                _poolSites[pool] = site;
            }

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[loot] pool->site rows=" + _poolSites.Count);
        }

        /// <summary>反查：这个池是哪个箱子开出来的（取第一个登记的箱子）；没有返回 null。</summary>
        internal static string BoxForPool(string poolId)
        {
            if (string.IsNullOrEmpty(poolId)) return null;

            EnsureBoxPools();

            foreach (var pair in _boxPools)
                if (PoolMatches(pair.Value, poolId))
                    return pair.Key;

            return null;
        }

        /// <summary>
        /// 这几个不是随机掉落容器：工具箱是收纳装置，远征箱是发给雇员的装备箱，
        /// 奢华补给箱的内容由剧情给。它们的玩法说明在物品自己的 tooltip 里，
        /// 所以不给「用途」页。
        /// </summary>
        internal static bool IsNonLootContainer(string boxId)
        {
            switch ((boxId ?? string.Empty).Trim())
            {
                case "toolbox":
                case "expedition_box":
                case "luxury_supply_crate":
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// 这张卡能开哪些箱子（= 那些箱子的池）。
        /// 数据来自 `box_pools.csv` 第 3 列：谁写了这张卡，就把那个箱子的池列出来。
        /// 锁标签那套（`LockHelper`）在游戏里只对场景实例生效，图鉴里查不到，所以以这张表为准。
        /// </summary>
        internal static List<LootTableInfo> BoxesForKeyId(string keyId)
        {
            if (string.IsNullOrEmpty(keyId)) return Empty;

            EnsureBoxPools();

            var list = new List<LootTableInfo>();

            foreach (var pair in _boxKeys)
            {
                if (!string.Equals(pair.Value, keyId, StringComparison.Ordinal)) continue;

                var tables = AsBox(pair.Key);

                for (int i = 0; i < tables.Count; i++)
                {
                    bool exists = false;
                    for (int k = 0; k < list.Count; k++)
                        if (string.Equals(list[k].TableId, tables[i].TableId, StringComparison.Ordinal)) { exists = true; break; }

                    if (!exists) list.Add(tables[i]);
                }
            }

            return list;
        }

        /// <summary>
        /// 箱子 → 真实内容物。
        ///
        /// 唯一数据源：加载期的自动探测（<see cref="BoxProbe"/>：让游戏自己的
        /// `PreBuiltItemHelper.crateFillers` 跑一遍，钩 `TryAdd` 截获）。
        /// 不再维护手工 CSV——探测会跟着游戏版本自动更新。
        /// </summary>
        private static readonly Dictionary<string, List<LootItemRef>> _boxContents =
            new Dictionary<string, List<LootItemRef>>(StringComparer.Ordinal);

        /// <summary>这个箱子的内容物；没登记返回 null。</summary>
        internal static List<LootItemRef> ContentsFor(string boxId)
        {
            if (string.IsNullOrEmpty(boxId)) return null;

            List<LootItemRef> items;
            return _boxContents.TryGetValue(boxId, out items) ? items : null;
        }

        /// <summary>
        /// 展开「随机 XXX」占位 id。
        ///
        /// `Fill*` 里塞的是 `random_performance_module` 这类 key，
        /// `ItemSpawner.Spawn` 会拿它去 `PreBuiltItemHelper.preBuiltItems` 里查工厂函数，
        /// 实际造出来的是固定物品 + 随机词条：
        ///
        ///   RandomPerformanceModule() → Directory.Item("system_module_performance") + InitRandomEffect
        ///   RandomNode()              → Directory.Item("node_medium")              + InitRandomEffect
        ///
        /// 所以这里直接换成那个固定物品，图鉴里才有图有名。
        /// </summary>
        internal static string ExpandPlaceholder(string id)
        {
            if (string.IsNullOrEmpty(id)) return id;

            switch (id.Trim())
            {
                case "random_performance_module": return "system_module_performance";
                case "random_efficiency_module": return "system_module_efficiancy";
                case "random_quality_module": return "system_module_quality";
                case "random_overclock_module": return "system_module_overclock";
                case "random_eco_module": return "system_module_eco";
                case "random_fineness_module": return "system_module_fineness";
                case "random_node": return "node_medium";
                default: return id;
            }
        }

        /// <summary>变体状态后缀：去掉之后通常就是本体的目录 id（与 SourceData 同表）。</summary>
        private static readonly string[] AliasSuffixes =
        {
            "_used", "_broken", "_ruined", "_dirty", "_empty", "_full", "_wet", "_dry", "_charged", "_dead"
        };

        /// <summary>满装盒（掉落表旧命名）→ 目录里的真身容器物品。</summary>
        private static readonly Dictionary<string, string> FullBoxAliases =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "full_water_purification_tablet_box", "water_tablet_container" },
                { "full_nutrient_tablet_box", "nutrient_tablet_container" },
                { "full_water_test_strip_box", "water_test_container" }
            };

        /// <summary>
        /// **真实物品 id 解析的唯一入口**：特殊物品 id → 本体物品 id。
        /// random_* 展开占位（→ 同族默认模块 / 默认节点）、满装盒旧命名对上
        /// 游戏自己的 *_container、运行时改名瓶（mini_alcohol_bottle）借本体、
        /// full_ 前缀与状态后缀（_used 等）逐层剥。
        /// 只在剥出来的 id 真的是目录物品时才返回；正反查索引 / 图标 / 名字共用这一份。
        /// 数据层规则（AGENTS.md）：**任何 id 索引的正反查都必须按这里解析出的
        /// 真实物品 id 登记**——占位 / 变体 id 只能作为附加键，不能是唯一键。
        /// </summary>
        internal static string BaseItemId(string id)
        {
            string text = (id ?? string.Empty).Trim();
            if (text.Length == 0) return null;

            if (text.StartsWith("random_", StringComparison.Ordinal))
            {
                string expanded = ExpandPlaceholder(text);
                if (expanded != text && RecipeIndex.FindEntry(expanded) != null) return expanded;
            }

            string fullBoxBase;
            if (FullBoxAliases.TryGetValue(text, out fullBoxBase) &&
                RecipeIndex.FindEntry(fullBoxBase) != null)
            {
                return fullBoxBase;
            }

            // 运行时改名的瓶：目录里没有这个 id（游戏把 mini_bottle 改名灌酒，
            // PreBuildChemHelper.CreateMiniAlcoholBottle），本体是 mini_bottle
            if (string.Equals(text, "mini_alcohol_bottle", StringComparison.Ordinal))
            {
                if (RecipeIndex.FindEntry("mini_bottle") != null) return "mini_bottle";
            }

            // 满装容器：full_beer → beer；full_water_test_strip_box 先剥 full_
            // 再逐层剥后缀，命中目录条目为止
            if (text.StartsWith("full_", StringComparison.Ordinal))
            {
                string baseId = StripSuffix(text.Substring(5));
                if (baseId != null) return baseId;
            }

            return StripSuffix(text);
        }

        /// <summary>剥一层状态/包装后缀，剥出的 id 是目录条目才返回。</summary>
        private static string StripSuffix(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            if (RecipeIndex.FindEntry(text) != null) return text;

            for (int i = 0; i < AliasSuffixes.Length; i++)
            {
                if (!text.EndsWith(AliasSuffixes[i], StringComparison.Ordinal)) continue;

                string baseId = text.Substring(0, text.Length - AliasSuffixes[i].Length);
                if (baseId.Length > 0 && RecipeIndex.FindEntry(baseId) != null) return baseId;
            }

            return null;
        }

        /// <summary>往反查索引登记一张表（键不存在就建桶）。</summary>
        private static void RegisterHit(Dictionary<string, List<LootTableInfo>> map, string itemId, LootTableInfo table)
        {
            if (string.IsNullOrEmpty(itemId)) return;

            List<LootTableInfo> hit;
            if (!map.TryGetValue(itemId, out hit))
            {
                hit = new List<LootTableInfo>();
                map[itemId] = hit;
            }

            if (!hit.Contains(table)) hit.Add(table);
        }

        /// <summary>
        /// 池内 id 的别名解析（镜像 <c>SourceData.ResolveAlias</c>）：
        /// <c>random_*</c> 展开成同族默认模块；状态后缀剥到本体。
        /// 不是别名返回 null。
        /// </summary>
        private static string ResolveAlias(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return null;

            if (itemId.StartsWith("random_", StringComparison.Ordinal))
                return ExpandPlaceholder(itemId);

            for (int i = 0; i < AliasSuffixes.Length; i++)
            {
                if (!itemId.EndsWith(AliasSuffixes[i], StringComparison.Ordinal)) continue;
                return itemId.Substring(0, itemId.Length - AliasSuffixes[i].Length);
            }

            return null;
        }

        /// <summary>
        /// 加载期自动探测的结果（唯一数据源）。重复调用只做去重合并，
        /// `ContentsFor` / `AsBox` / `BoxesForItem` 都是现读 `_boxContents`。
        /// </summary>
        internal static void ApplyBoxProbe(Dictionary<string, List<string>> results)
        {
            if (results == null || results.Count == 0) return;

            foreach (var pair in results)
            {
                List<LootItemRef> list;
                if (!_boxContents.TryGetValue(pair.Key, out list))
                    _boxContents[pair.Key] = list = new List<LootItemRef>();

                var ids = pair.Value;
                if (ids == null) continue;

                for (int i = 0; i < ids.Count; i++)
                {
                    string id = ExpandPlaceholder(ids[i]);
                    if (string.IsNullOrEmpty(id)) continue;

                    bool exists = false;
                    for (int k = 0; k < list.Count; k++)
                        if (string.Equals(list[k].Id, id, StringComparison.Ordinal)) { exists = true; break; }

                    if (!exists) list.Add(new LootItemRef { Id = id, Weight = 1f });
                }
            }

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[loot] box auto: " + _boxContents.Count + " boxes");
        }

        /// <summary>
        /// 自动探测出的「箱子 → 卡」（<see cref="BoxProbe"/> 扫 `LOCKED_CONTAINER_TAG`
        /// 物品的 `LOCK_ID`，再查游戏的锁→卡字典）。
        /// `box_pools.csv` 的 key 列是手工覆盖层：这里只补它没写的箱子。
        /// </summary>
        internal static void ApplyBoxKeycards(Dictionary<string, string> keycards)
        {
            if (keycards == null || keycards.Count == 0) return;

            EnsureBoxPools();

            int added = 0;

            foreach (var pair in keycards)
            {
                if (string.IsNullOrEmpty(pair.Key) || string.IsNullOrEmpty(pair.Value)) continue;
                if (_boxKeys.ContainsKey(pair.Key)) continue;

                _boxKeys[pair.Key] = pair.Value;
                added++;
            }

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[loot] box->key auto: +" + added + " (total " + _boxKeys.Count + ")");
        }

        /// <summary>
        /// 所有登记过内容物或开启卡的箱子 id（自动探测 + CSV 覆盖）。
        /// 开启配方（箱 + 卡 → 内容物）按这份清单建一次全局表。
        /// </summary>
        internal static List<string> KnownBoxes()
        {
            EnsureBoxPools();

            var list = new List<string>();

            foreach (var pair in _boxContents)
                if (!list.Contains(pair.Key)) list.Add(pair.Key);

            foreach (var pair in _boxKeys)
                if (!list.Contains(pair.Key)) list.Add(pair.Key);

            list.Sort(StringComparer.Ordinal);
            return list;
        }

        /// <summary>读 `Data/box_pools.csv`：箱子物品 id → 掉落池 id。</summary>
        private static void EnsureBoxPools()
        {
            if (_boxPoolsLoaded) return;
            _boxPoolsLoaded = true;

            string text = null;
            try { text = ModHelper.ReadModFileText(ModEntry.MOD_ID, "Data/box_pools.csv"); }
            catch (Exception e) { Warn("box_pools: " + e.Message); }

            if (string.IsNullOrEmpty(text)) return;

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                string[] cols;
                try { cols = SourceIndex.ParseCsvRow(line); }
                catch { continue; }

                if (cols.Length < 2) continue;

                string box = cols[0].Trim();
                string pool = cols[1].Trim();

                if (box.Length == 0) continue;
                if (string.Equals(box, "box", StringComparison.OrdinalIgnoreCase)) continue;   // 表头

                if (pool.Length > 0) _boxPools[box] = pool;

                if (cols.Length > 2)
                {
                    string key = cols[2].Trim();
                    if (key.Length > 0) _boxKeys[box] = key;
                }
            }

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[loot] box->pool rows=" + _boxPools.Count);
        }

        /// <summary>
        /// 这张卡能开哪些箱子（= 哪些表）。
        /// 规则同 `LockHelper.CanOpenLock`：卡的 `LOCK_ID` 与箱子的锁 id 相等，命令卡万能。
        /// </summary>
        internal static List<LootTableInfo> ForKey(GameItem keyItem)
        {
            string keyLockId = LockKeys.TagLockId(keyItem);
            if (string.IsNullOrEmpty(keyLockId)) return Empty;

            EnsureBuilt();

            List<LootTableInfo> cached;
            if (_byKeyLockId.TryGetValue(keyLockId, out cached)) return cached;

            var list = new List<LootTableInfo>();

            for (int i = 0; i < _tables.Length; i++)
            {
                var table = _tables[i];

                var entry = RecipeIndex.FindEntry(table.TableId);
                if (entry == null || entry.Item == null) continue;

                string boxLockId = LockKeys.LockIdOf(entry.Item);
                if (!LockKeys.CanOpen(keyLockId, boxLockId)) continue;

                list.Add(table);
            }

            _byKeyLockId[keyLockId] = list;

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[lock] key " + keyLockId + " opens " + list.Count + " tables");

            return list;
        }

        private static readonly Dictionary<string, List<LootTableInfo>> _byKeyLockId =
            new Dictionary<string, List<LootTableInfo>>(StringComparer.Ordinal);

        /// <summary>
        /// 惰性建索引（交给 <see cref="IndexManager"/>）。战利品表是读档 / 进场景时才注册的，
        /// 所以建不出东西时返回 null、不缓存，下次进关再来一次。
        /// </summary>
        private static void EnsureBuilt()
        {
            IndexManager.Get(IndexName, BuildCore);
        }

        private static object BuildCore()
        {
            var tables = new List<LootTableInfo>();
            var map = new Dictionary<string, List<LootTableInfo>>(StringComparer.Ordinal);
            int entries = 0;
            int groupCount = 0;

            try
            {
                var tableIds = LootRegistry.TableIds;
                if (tableIds == null) return null;

                foreach (var tableId in tableIds)
                {
                    if (string.IsNullOrEmpty(tableId)) continue;

                    List<LootEntry> list;
                    try { list = LootRegistry.GetTableEntries(tableId); }
                    catch { continue; }

                    if (list == null || list.Count == 0) continue;

                    var table = new LootTableInfo { TableId = tableId };

                    for (int i = 0; i < list.Count; i++)
                    {
                        var entry = list[i];
                        if (entry == null || string.IsNullOrEmpty(entry.id)) continue;

                        entries++;

                        // **正反查都按真实物品 id 记录**（与 SourceData.AddAliasSource
                        // 同一条规则）：占位 id（random_performance_module 这类）在
                        // 生成时才被换成同族默认模块——池子记录、反查索引都用展开后
                        // 的本体 id，具体模块的页才能查到装着它的池子（倾倒场等）。
                        string realId = entry.id.StartsWith("random_", StringComparison.Ordinal)
                            ? ExpandPlaceholder(entry.id)
                            : entry.id;

                        table.Items.Add(new LootItemRef { Id = realId, Weight = entry.weight });

                        List<LootTableInfo> hit;
                        if (!map.TryGetValue(realId, out hit))
                        {
                            hit = new List<LootTableInfo>();
                            map[realId] = hit;
                        }

                        if (!hit.Contains(table)) hit.Add(table);

                        // 原始 id 也登记一份（占位格 / 兼容旧调用）；变体后缀的本体再补一份
                        if (realId != entry.id) RegisterHit(map, entry.id, table);

                        string alias = ResolveAlias(entry.id);
                        if (!string.IsNullOrEmpty(alias) && alias != entry.id && alias != realId)
                            RegisterHit(map, alias, table);
                    }

                    tables.Add(table);
                }
            }
            catch (Exception e)
            {
                Warn("scan failed: " + e.Message);
                return null;
            }

            // 组：`TableGroup` 是「表 → 表」的集合（POI 常用组），
            // 展开成「组 id → 组内所有表的物品并集」，这样勾池子时可以填组 id。
            try
            {
                var byTable = new Dictionary<string, LootTableInfo>(StringComparer.Ordinal);
                for (int i = 0; i < tables.Count; i++) byTable[tables[i].TableId] = tables[i];

                var groupIds = LootRegistry.GroupIds;
                if (groupIds != null)
                {
                    foreach (var groupId in groupIds)
                    {
                        if (string.IsNullOrEmpty(groupId)) continue;

                        // `GetGroupEntries` 返回的是 LootEntry 列表，
                        // 组里的每一条 `id` 是一个**表 id**，要再展开成物品。
                        List<LootEntry> members;
                        try { members = LootRegistry.GetGroupEntries(groupId); }
                        catch { continue; }

                        if (members == null || members.Count == 0) continue;

                        var group = new LootTableInfo { TableId = groupId };
                        var seen = new HashSet<string>(StringComparer.Ordinal);

                        for (int m = 0; m < members.Count; m++)
                        {
                            var member = members[m];
                            if (member == null || string.IsNullOrEmpty(member.id)) continue;

                            LootTableInfo memberTable;

                            // 组里的 id 通常是表 id；万一是物品 id 就直接收
                            if (!byTable.TryGetValue(member.id, out memberTable))
                            {
                                if (seen.Add(member.id))
                                    group.Items.Add(new LootItemRef { Id = member.id, Weight = member.weight });
                                continue;
                            }

                            for (int k = 0; k < memberTable.Items.Count; k++)
                            {
                                var item = memberTable.Items[k];
                                if (item == null || string.IsNullOrEmpty(item.Id)) continue;
                                if (!seen.Add(item.Id)) continue;

                                group.Items.Add(item);
                                entries++;
                            }
                        }

                        if (group.Items.Count > 0) { tables.Add(group); groupCount++; }
                    }
                }
            }
            catch (Exception e)
            {
                Warn("group scan: " + e.Message);
            }

            if (tables.Count == 0) return null;   // 表还没加载，下次再说

            // ===== 统一性保证：反查索引从正查数据直接推导，映射与显示不能分离 =====
            // AGENTS.md 规则：站点页（正查并集）上出现的**每个**物品 id，必须能从
            // 该物品的来源页反查回这个站点。这一遍把「tables × items」重放一遍：
            // 组表里直接塞的物品 id、手工池、占位展开、任何未来新增的池来源，
            // 不管从哪条路进的池子，反查登记都在这里兜底补齐——
            // 两边数据同源，构造上不可能再出现「页面上有、物品页查不到」。
            for (int i = 0; i < tables.Count; i++)
            {
                var t = tables[i];
                if (t == null || t.Items == null) continue;

                for (int k = 0; k < t.Items.Count; k++)
                {
                    var it = t.Items[k];
                    if (it == null || string.IsNullOrEmpty(it.Id)) continue;

                    RegisterHit(map, it.Id, t);

                    // 别名 id（变体后缀 / 满装盒 / 改名瓶）的本体同样保证可达
                    string baseId = BaseItemId(it.Id);
                    if (!string.IsNullOrEmpty(baseId) && baseId != it.Id)
                        RegisterHit(map, baseId, t);
                }
            }

            _tables = tables.ToArray();

            // 把「物品 → 表」的临时 map 转成统一的 RelationIndex（只建产物方向）
            var index = new RelationIndex<LootTableInfo>();
            var perTable = new Dictionary<LootTableInfo, HashSet<string>>();

            foreach (var pair in map)
            {
                var list = pair.Value;
                if (list == null) continue;

                for (int i = 0; i < list.Count; i++)
                {
                    var table = list[i];
                    if (table == null) continue;

                    HashSet<string> ids;
                    if (!perTable.TryGetValue(table, out ids))
                        perTable[table] = ids = new HashSet<string>(StringComparer.Ordinal);

                    ids.Add(pair.Key);
                }
            }

            foreach (var pair in perTable) index.Add(pair.Key, null, new List<string>(pair.Value).ToArray());

            _lootIndex = index;

            if (ModEntry.Log != null)
            {
                // 全量打出来：只有十几个，方便照着填 box_pools.csv
                var all = new List<string>(_tables.Length);
                for (int i = 0; i < _tables.Length; i++) all.Add(_tables[i].TableId);

                ModEntry.Log.Log("[loot] tables=" + (_tables.Length - groupCount)
                                 + " groups=" + groupCount
                                 + " entries=" + entries
                                 + " ids=" + string.Join(",", all.ToArray()));

                // 每张池的完整物品清单（真实 id）：对账用——
                // 「某物品反查不到来源」时直接对照这份清单和 _lootIndex 的键
                for (int i = 0; i < _tables.Length; i++)
                {
                    var t = _tables[i];
                    if (t == null || t.Items == null || t.Items.Count == 0) continue;

                    var parts = new List<string>(t.Items.Count);
                    for (int k = 0; k < t.Items.Count; k++)
                    {
                        var it = t.Items[k];
                        if (it == null || string.IsNullOrEmpty(it.Id)) continue;
                        parts.Add(it.Id + (it.Weight != 1f ? "(" + it.Weight + ")" : ""));
                    }

                    ModEntry.Log.Log("[loot] pool " + t.TableId + " (" + parts.Count + "): "
                                     + string.Join(", ", parts.ToArray()));
                }
            }

            return IndexMarker;
        }

        private static void Warn(string message)
        {
            if (ModEntry.Log != null) ModEntry.Log.Log("[loot] warn: " + message);
        }
    }
}
