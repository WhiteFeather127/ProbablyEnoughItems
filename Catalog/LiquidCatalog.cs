using System;
using System.Collections.Generic;

using ItemCatalog.UI;

namespace ItemCatalog.Catalog
{
    /// <summary>
    /// 液体作为可查对象：每种液体对应一只装满它的默认容器
    /// （<see cref="LiquidManager.ContainerId"/>），作为图鉴里的合成条目参与搜索、
    /// 来源/去处与拿取。第一版只把化学产品（药）登记为液体的来源；
    /// 以后可以扩展成独立的液体详情页。
    /// </summary>
    internal static class LiquidCatalog
    {
        internal const string EntryPrefix = "liquid:";

        private static readonly Dictionary<string, ItemEntry> _entries =
            new Dictionary<string, ItemEntry>(StringComparer.Ordinal);

        private static bool _built;

        internal static bool IsLiquidEntry(string id)
        {
            return !string.IsNullOrEmpty(id) &&
                   id.StartsWith(EntryPrefix, StringComparison.Ordinal);
        }

        internal static ItemEntry Find(string id)
        {
            if (!IsLiquidEntry(id)) return null;

            EnsureBuilt();
            ItemEntry entry;
            return _entries.TryGetValue(id, out entry) ? entry : null;
        }

        /// <summary>搜索：液体本地化名 / 液体 id 命中时返回对应的瓶子条目。</summary>
        internal static List<ItemEntry> Search(string query)
        {
            var result = new List<ItemEntry>();
            if (string.IsNullOrEmpty(query)) return result;

            EnsureBuilt();
            foreach (var pair in _entries)
            {
                var entry = pair.Value;
                if (entry == null) continue;

                bool hit = (!string.IsNullOrEmpty(entry.DisplayName) &&
                            entry.DisplayName.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)
                           || (!string.IsNullOrEmpty(entry.Id) &&
                               entry.Id.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);

                if (hit) result.Add(entry);
            }

            return result;
        }

        internal static void EnsureBuilt()
        {
            if (_built) return;
            _built = true;

            var liquids = Liquid.Liquids;
            if (liquids == null) return;

            for (int i = 0; i < liquids.Count; i++)
            {
                var liquid = liquids[i];
                if (liquid == null || string.IsNullOrEmpty(liquid.identifier)) continue;

                var bottle = CreateBottle(liquid.identifier);
                if (bottle == null) continue;

                // 瓶子代表这种液体本身：名字用液体本地化名
                if (!string.IsNullOrEmpty(liquid.displayName))
                {
                    try { bottle.name = liquid.displayName; } catch { }
                }

                string id = EntryPrefix + liquid.identifier;
                _entries[id] = new ItemEntry
                {
                    Id = id,
                    Name = liquid.displayName,
                    Item = bottle,
                    TakeFactory = delegate
                    {
                        return CreateBottle(liquid.identifier);
                    }
                };
            }

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[liquid] entries=" + _entries.Count);
        }

        /// <summary>造一只装该液体到满的默认容器（搜索展示 / 拿取都用它）。</summary>
        internal static GameItem CreateBottle(string liquidId)
        {
            GameItem bottle = null;
            try
            {
                bottle = CatalogWindow.CreatePreviewItem(LiquidManager.ContainerId);
                if (bottle == null) return null;

                int capacity = bottle.GetTagReadonly("LIQUID_CONTAINER_CAPACITY").GetInt();
                if (capacity <= 0) capacity = 1;

                WaterHelper.AddLiquid(bottle, liquidId, capacity);
                try { SpriteHelper.UpdateWaterContainerSprite(bottle); } catch { }
                return bottle;
            }
            catch (Exception e)
            {
                if (bottle != null) { try { bottle.Destroy(); } catch { } }
                if (ModEntry.Log != null)
                    ModEntry.Log.Warning("[liquid] create '" + liquidId + "': " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// 这种液体的来源：第一版只收化学产品（药）里有效成分为它的物品。
        /// 生成一条「药 → 液体瓶」的使用记录，来源页就能从液体查到药。
        /// </summary>
        internal static List<UseInfo> SourceUses(string liquidEntryId)
        {
            var result = new List<UseInfo>();
            if (!IsLiquidEntry(liquidEntryId)) return result;

            string liquidId = liquidEntryId.Substring(EntryPrefix.Length);
            var items = CatalogDataBuilder.Visible;

            for (int i = 0; i < items.Count; i++)
            {
                var entry = items[i];
                var item = entry == null ? null : entry.Item;
                if (item == null) continue;

                bool product;
                try { product = item.IsTag("CHEMICAL_PRODUCT_TAG"); }
                catch { product = false; }
                if (!product) continue;

                if (!string.Equals(MainLiquidId(item), liquidId, StringComparison.Ordinal)) continue;

                result.Add(new UseInfo
                {
                    Sources = new[] { entry.Id },
                    Kind = "extract",
                    Produces = new[] { liquidEntryId }
                });
            }

            return result;
        }

        internal static string MainLiquidId(GameItem item)
        {
            if (item == null) return null;

            try
            {
                string active = item.GetTagReadonly("ACTIVATE_INGREDIENT_TAG").GetString();
                if (!string.IsNullOrEmpty(active)) return active;
            }
            catch { }

            var parts = CatalogWindow.LiquidParts(item);
            string major = null;
            int best = 0;
            foreach (var pair in parts)
            {
                if (pair.Value > best) { best = pair.Value; major = pair.Key; }
            }

            return major;
        }

        /// <summary>换场景时销毁代表容器，下次搜索重新建。</summary>
        internal static void Reset()
        {
            foreach (var pair in _entries)
            {
                var item = pair.Value == null ? null : pair.Value.Item;
                if (item != null) { try { item.Destroy(); } catch { } }
            }

            _entries.Clear();
            _built = false;
        }
    }
}
