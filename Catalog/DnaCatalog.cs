using System;
using System.Collections.Generic;

namespace ItemCatalog.Catalog
{
    /// <summary>
    /// 已写入的 DNA 盘作为常驻条目（对应 <see cref="LiquidCatalog"/> 的角色）：
    /// 每一种盘（每种未锁定特质一只）在图鉴里常显示，参与浏览、来源/去处与拿取。
    /// 空白盘不在这里——它走注册表本体 <c>plant_dna_disk</c>，两者分开显示。
    /// 条目 Id 用 <c>dna:&lt;traitId&gt;</c>，物品本体仍是 <c>plant_dna_disk</c>。
    /// </summary>
    internal static class DnaCatalog
    {
        private static readonly Dictionary<string, ItemEntry> _entries =
            new Dictionary<string, ItemEntry>(StringComparer.Ordinal);

        private static readonly List<ItemEntry> _ordered = new List<ItemEntry>();

        private static bool _built;

        internal static ItemEntry Find(string id)
        {
            if (!DnaManager.IsDnaEntry(id)) return null;

            EnsureBuilt();
            ItemEntry entry;
            return _entries.TryGetValue(id, out entry) ? entry : null;
        }

        /// <summary>图鉴常驻条目：每种已写入的盘一只（按特质注册顺序）。</summary>
        internal static List<ItemEntry> VisibleEntries()
        {
            EnsureBuilt();
            return _ordered;
        }

        internal static void EnsureBuilt()
        {
            if (_built) return;
            _built = true;

            int count = DnaManager.Count;
            for (int i = 0; i < count; i++)
            {
                var trait = DnaManager.TraitAt(i);
                if (trait == null) continue;

                var disk = DnaManager.CreateDisk(trait);
                if (disk == null) continue;

                string name = null;
                try { name = disk.name; } catch { }
                if (string.IsNullOrEmpty(name)) name = DnaManager.Label(trait);

                string id = DnaManager.EntryIdFor(trait);
                var kind = trait;
                var entry = new ItemEntry
                {
                    Id = id,
                    Name = name,
                    Item = disk,
                    TakeFactory = delegate { return DnaManager.CreateDisk(kind); }
                };

                _entries[id] = entry;
                _ordered.Add(entry);
            }

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[dna] entries=" + _entries.Count);
        }

        /// <summary>换场景时销毁代表盘，下次浏览重新建。</summary>
        internal static void Reset()
        {
            foreach (var pair in _entries)
            {
                var item = pair.Value == null ? null : pair.Value.Item;
                if (item != null) { try { item.Destroy(); } catch { } }
            }

            _entries.Clear();
            _ordered.Clear();
            _built = false;
        }
    }
}
