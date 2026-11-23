using System;
using System.Collections.Generic;

namespace ItemCatalog.Catalog
{
    /// <summary>
    /// 通用关系索引：一条记录带「来源 id」与「产物 id」两组，按 id 同时建
    /// 正向（来源 → 记录）与反向（产物 → 记录）索引。
    ///
    /// 所有配方类数据（uses.csv、箱子/卡的开启、机器配方）都共用这一份结构：
    /// 建表时一次性登记，查询只做字典直查（O(1)），不再在打开页面时线性扫描。
    /// </summary>
    internal sealed class RelationIndex<T> where T : class
    {
        private readonly List<T> _all = new List<T>();
        private readonly Dictionary<string, List<T>> _bySource = new Dictionary<string, List<T>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<T>> _byProduct = new Dictionary<string, List<T>>(StringComparer.Ordinal);

        private static readonly List<T> Empty = new List<T>();

        internal IReadOnlyList<T> All { get { return _all; } }

        internal int Count { get { return _all.Count; } }

        /// <summary>
        /// 登记一条记录（同一条记录只应 Add 一次）。sources / products 传 null
        /// 表示该方向不建索引（例如「物品 → 液体」只登记在液体条目的产物侧）。
        /// 数组内的重复 id 用 HashSet 去重，桶直接追加——建表严格线性。
        /// </summary>
        internal void Add(T entry, string[] sources, string[] products)
        {
            if (entry == null) return;

            _all.Add(entry);
            Link(_bySource, sources, entry);
            Link(_byProduct, products, entry);
        }

        internal List<T> BySource(string id)
        {
            return Lookup(_bySource, id);
        }

        internal List<T> ByProduct(string id)
        {
            return Lookup(_byProduct, id);
        }

        private static void Link(Dictionary<string, List<T>> map, string[] ids, T entry)
        {
            if (ids == null || ids.Length == 0) return;

            HashSet<string> seen = null;

            for (int i = 0; i < ids.Length; i++)
            {
                string id = ids[i];
                if (string.IsNullOrEmpty(id)) continue;

                // 同一条记录的数组里同一 id 可能出现多次（如 2 废金属）：只登记一次
                if (seen == null) seen = new HashSet<string>(StringComparer.Ordinal);
                if (!seen.Add(id)) continue;

                List<T> bucket;
                if (!map.TryGetValue(id, out bucket)) map[id] = bucket = new List<T>();
                bucket.Add(entry);
            }
        }

        private static List<T> Lookup(Dictionary<string, List<T>> map, string id)
        {
            if (string.IsNullOrEmpty(id)) return Empty;

            List<T> list;
            return map.TryGetValue(id, out list) && list != null ? list : Empty;
        }
    }
}
