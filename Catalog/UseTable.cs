using System;
using System.Collections.Generic;

namespace ItemCatalog.Catalog
{
    /// <summary>
    /// 一次「使用掉某件东西，换回另一件东西」的关系。
    /// 和合成配方不同：配方在机器/工作台上跑，这里是**直接对物品本身动手**——
    /// 开箱子拿到内容物、把液体喝完剩个空瓶、把绷带缠在玻璃片上……
    /// </summary>
    internal sealed class UseInfo
    {
        /// <summary>
        /// 参与这次操作的物品。一般只有一件（开箱、喝掉）；
        /// 两件之间的交互（绷带 + 玻璃片）用 `+` 连，会同时出现在两件物品的去处里。
        /// </summary>
        internal string[] Sources;

        /// <summary>
        /// 用「标签」而不是具体物品描述的使用源（`tag:ADV_GUN_TAG` 这种）。
        /// 拆解、屠宰这类操作的目标是**一整类**物品，没有单一 id 可写，
        /// 所以单独存标签，查询时用物品自己的 IsTag 匹配。
        /// </summary>
        internal string[] SourceTags;

        internal string Kind;           // open / drink / apply / combine，用于标题文案
        internal string[] Produces;     // 产出的物品（可能多个）
        internal string Note;           // 备注本地化 key（可选）
    }

    /// <summary>
    /// `Data/uses.csv` 的解析与索引。
    /// 表结构（注释行以 # 开头）：
    /// <code>
    /// uses,输入物品id,方式,产出物品id(|分隔),备注key
    /// </code>
    /// 产出为空表示「这件东西用掉就没了」，只作为去处显示。
    /// </summary>
    internal static class UseIndex
    {
        private const string UsesCsvPath = "Data/uses.csv";

        private static List<UseInfo> _all;
        private static Dictionary<string, List<UseInfo>> _producing;   // 产物 id → 这些「使用」关系
        private static Dictionary<string, List<UseInfo>> _using;       // 使用源 id → 这些「使用」关系
        private static Dictionary<string, List<UseInfo>> _tagUsing;    // 标签（不带 tag: 前缀）→ 这些「使用」关系

        /// <summary>谁能让我得到这件物品（来源侧）。</summary>
        internal static List<UseInfo> Producing(string itemId)
        {
            EnsureLoaded();
            return Lookup(_producing, itemId);
        }

        /// <summary>这件物品被使用后能得到什么（去处侧），只按 id 精确匹配。</summary>
        internal static List<UseInfo> Using(string itemId)
        {
            EnsureLoaded();
            return Lookup(_using, itemId);
        }

        /// <summary>
        /// 去处侧完整查询：先按 id 精确匹配，再按这件物品自带的标签匹配
        /// （`tag:ADV_GUN_TAG` 这类「一整类目标」的关系）。
        /// 同一条关系可能既命中 id 又命中标签，这里去重。
        /// </summary>
        internal static List<UseInfo> Using(string itemId, GameItem item)
        {
            EnsureLoaded();

            var byId = Lookup(_using, itemId);
            if (item == null || _tagUsing == null || _tagUsing.Count == 0)
                return byId;

            List<UseInfo> merged = null;
            foreach (var pair in _tagUsing)
            {
                if (pair.Value == null || pair.Value.Count == 0) continue;

                bool hit;
                try { hit = item.IsTag(pair.Key); }
                catch { hit = false; }
                if (!hit) continue;

                if (merged == null) merged = new List<UseInfo>(byId);
                for (int i = 0; i < pair.Value.Count; i++)
                {
                    if (!merged.Contains(pair.Value[i])) merged.Add(pair.Value[i]);
                }
            }

            return merged != null && merged.Count > 0 ? merged : byId;
        }

        private static List<UseInfo> Lookup(Dictionary<string, List<UseInfo>> map, string key)
        {
            if (map == null || string.IsNullOrEmpty(key)) return Empty;

            List<UseInfo> list;
            return map.TryGetValue(key, out list) && list != null ? list : Empty;
        }

        private static readonly List<UseInfo> Empty = new List<UseInfo>();

        private static void EnsureLoaded()
        {
            if (_all != null) return;

            _all = new List<UseInfo>();
            _producing = new Dictionary<string, List<UseInfo>>(StringComparer.Ordinal);
            _using = new Dictionary<string, List<UseInfo>>(StringComparer.Ordinal);
            _tagUsing = new Dictionary<string, List<UseInfo>>(StringComparer.Ordinal);

            string text = null;

            try { text = ModHelper.ReadModFileText(ModEntry.MOD_ID, UsesCsvPath); }
            catch (Exception e) { Warn("cannot read " + UsesCsvPath + ": " + e.Message); }

            if (string.IsNullOrEmpty(text)) return;

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                string[] cols;
                try { cols = SourceIndex.ParseCsvRow(line); }
                catch { continue; }

                if (cols.Length < 3) continue;
                if (!string.Equals(cols[0].Trim(), "uses", StringComparison.OrdinalIgnoreCase)) continue;

                string source = Cell(cols, 1);
                if (source == null) continue;

                // 第 2 列里 `tag:XXX` 表示「任意带这个标签的物品」，
                // 其余是具体 id。拆解 / 屠宰这类操作的目标是一整类，只能这么写。
                var rawSources = SplitIds(source.Replace('+', '|'));
                var idSources = new List<string>(rawSources.Length);
                var tagSources = new List<string>();

                for (int k = 0; k < rawSources.Length; k++)
                {
                    string raw = rawSources[k];
                    if (raw.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
                    {
                        string tag = raw.Substring(4).Trim();
                        if (tag.Length > 0) tagSources.Add(tag);
                    }
                    else
                    {
                        idSources.Add(raw);
                    }
                }

                var info = new UseInfo
                {
                    Sources = idSources.ToArray(),
                    SourceTags = tagSources.ToArray(),
                    Kind = Cell(cols, 2),
                    Produces = SplitIds(Cell(cols, 3)),
                    Note = Cell(cols, 4)
                };

                if (info.Sources.Length == 0 && info.SourceTags.Length == 0) continue;

                _all.Add(info);

                for (int k = 0; k < info.Sources.Length; k++)
                {
                    Append(_using, info.Sources[k], info);
                    AppendAlias(_using, info.Sources[k], info);
                }
                for (int k = 0; k < info.SourceTags.Length; k++)
                    Append(_tagUsing, info.SourceTags[k], info);

                if (info.Produces != null)
                {
                    for (int k = 0; k < info.Produces.Length; k++)
                    {
                        Append(_producing, info.Produces[k], info);
                        AppendAlias(_producing, info.Produces[k], info);
                    }
                }
            }

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[uses] loaded " + _all.Count + " rows from " + UsesCsvPath);

            // 可溶化学品（SOLUBLE_TAG）在游戏里可以直接对任意液体容器使用
            // （ChemicalHelper.InitChemItem 注册的 onItemTargetItemFunc → DissolveInto）；
            // 其它装液体的容器则通过内容物标签说明「里面是什么」。
            // 这两类关系都没有配方表可读，按标签 / 内容物自动补一条使用记录。
            AppendDissolveUses();
        }

        /// <summary>
        /// 物品 → 液体条目的关系，液体管理器统一判定：
        /// - 可溶化学品（SOLUBLE_TAG）：dissolve，产物是该液体主成分的条目；
        /// - 其它带液体成分的物品（容器、能装液体的日用品）：contain，产物同样是主成分条目。
        /// 记录**只登记在液体条目一侧**（液体页的「来源」）：图鉴里的示例液体可以按成分
        /// 反查谁装着 / 谁能分解出它；物品自己发起的反查一律只按 id 匹配，
        /// 不能因为内容物标签就把固体（净水药片 → 液体氯）当成它的用途。
        /// </summary>
        private static void AppendDissolveUses()
        {
            LiquidManager.TagItems();

            var items = CatalogDataBuilder.Visible;
            if (items == null) return;

            for (int i = 0; i < items.Count; i++)
            {
                var entry = items[i];
                var item = entry == null ? null : entry.Item;
                if (item == null || string.IsNullOrEmpty(entry.Id)) continue;

                bool soluble;
                try { soluble = item.IsTag("SOLUBLE_TAG"); }
                catch { soluble = false; }

                // 有液体成分标签的物品都算（容器、能装液体的日用品）。
                // 不按物品类型筛——家居用品里也有能装液体的。
                bool filled = !soluble && LiquidManager.Parts(item).Count > 0;

                if (!soluble && !filled) continue;

                // 产物 = 代表该液体主成分的液体条目。
                string product = string.IsNullOrEmpty(entry.LiquidTag)
                    ? null
                    : LiquidCatalog.EntryPrefix + entry.LiquidTag;
                if (product == null) continue;

                if (HasProduceSource(product, entry.Id)) continue;

                var info = new UseInfo
                {
                    Sources = new[] { entry.Id },
                    SourceTags = new string[0],
                    Kind = soluble ? "dissolve" : "contain",
                    Produces = string.IsNullOrEmpty(product) ? new string[0] : new[] { product },
                    Note = null
                };

                _all.Add(info);

                // 只进液体条目的「来源」索引：物品侧的正反查保持 id 精确匹配
                // （配方 / uses.csv / 货单），不掺成分推导出来的关系。
                Append(_producing, product, info);
                AppendAlias(_producing, product, info);
            }
        }

        /// <summary>液体条目的来源里是否已经登记过这件物品（避免和 uses.csv 重复）。</summary>
        private static bool HasProduceSource(string product, string id)
        {
            List<UseInfo> list;
            if (_producing == null || !_producing.TryGetValue(product, out list) || list == null) return false;

            for (int i = 0; i < list.Count; i++)
            {
                var u = list[i];
                if (u == null || u.Sources == null) continue;

                for (int k = 0; k < u.Sources.Length; k++)
                    if (string.Equals(u.Sources[k], id, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>
        /// **正反查按真实物品 id**：别名 id（运行时改名瓶 mini_alcohol_bottle、
        /// 满装盒 full_*、占位 random_*、状态变体 _used…）额外以解析出的本体 id
        /// 再登记一份（统一走 <see cref="LootSource.BaseItemId"/>）——
        /// 否则本体页（迷你瓶的「能从化学品拆包得到」）查不到这条记录。
        /// 多余登记无害：解析不出本体的 id 不会被查询。
        /// </summary>
        private static void AppendAlias(Dictionary<string, List<UseInfo>> map, string id, UseInfo info)
        {
            string baseId = LootSource.BaseItemId(id);
            if (!string.IsNullOrEmpty(baseId) && baseId != id) Append(map, baseId, info);
        }

        private static void Append(Dictionary<string, List<UseInfo>> map, string key, UseInfo info)
        {
            if (map == null || string.IsNullOrEmpty(key)) return;

            List<UseInfo> list;
            if (!map.TryGetValue(key, out list))
            {
                list = new List<UseInfo>();
                map[key] = list;
            }

            list.Add(info);
        }

        private static string Cell(string[] cols, int index)
        {
            if (cols == null || index < 0 || index >= cols.Length) return null;

            string value = cols[index] == null ? null : cols[index].Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private static string[] SplitIds(string cell)
        {
            if (string.IsNullOrEmpty(cell)) return new string[0];

            var parts = cell.Split('|');
            var list = new List<string>(parts.Length);

            for (int i = 0; i < parts.Length; i++)
            {
                string id = parts[i].Trim();
                if (id.Length > 0) list.Add(id);
            }

            return list.ToArray();
        }

        private static void Warn(string message)
        {
            if (ModEntry.Log != null) ModEntry.Log.Log("[uses] warn: " + message);
        }
    }
}
