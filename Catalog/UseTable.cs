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
                    Append(_using, info.Sources[k], info);
                for (int k = 0; k < info.SourceTags.Length; k++)
                    Append(_tagUsing, info.SourceTags[k], info);

                if (info.Produces != null)
                {
                    for (int k = 0; k < info.Produces.Length; k++)
                        Append(_producing, info.Produces[k], info);
                }
            }

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[uses] loaded " + _all.Count + " rows from " + UsesCsvPath);
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
