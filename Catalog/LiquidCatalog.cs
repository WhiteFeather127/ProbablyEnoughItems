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

            LiquidManager.TagItems();

            var liquids = Liquid.Liquids;
            if (liquids == null) return;

            for (int i = 0; i < liquids.Count; i++)
            {
                var liquid = liquids[i];
                if (liquid == null || string.IsNullOrEmpty(liquid.identifier)) continue;

                // 代表瓶装多少：查物品管理器里该液体的实际总量（药里有效成分的毫升数），
                // 不再无脑灌满整瓶。
                int amount = RepresentativeAmount(liquid.identifier);
                var bottle = CreateBottle(liquid.identifier, amount);
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
                        return CreateBottle(liquid.identifier, amount);
                    }
                };
            }

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[liquid] entries=" + _entries.Count);
        }

        /// <summary>造一只装指定体积该液体的默认容器（搜索展示 / 拿取都用它）。</summary>
        internal static GameItem CreateBottle(string liquidId, int amount)
        {
            GameItem bottle = null;
            try
            {
                bottle = CatalogWindow.CreatePreviewItem(LiquidManager.ContainerId);
                if (bottle == null) return null;

                if (amount <= 0) amount = 1000;   // 没有来源数据时给 1 ml 作为代表

                // 直接写液体标签，内容是物品管理器里记录的该液体总量，
                // 不走 AddLiquid 的温度/特性管线，避免灌进别的东西。
                ChemicalHelper.SetChemContentUL(bottle, liquidId, amount);
                try { WaterFeatureHelper.UpdateWaterFeature(bottle, false); } catch { }
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
            LiquidManager.TagItems();

            var items = CatalogDataBuilder.Visible;

            for (int i = 0; i < items.Count; i++)
            {
                var entry = items[i];
                var item = entry == null ? null : entry.Item;
                if (item == null) continue;

                // 物品管理器里的液体标签（不是游戏标签）
                if (!string.Equals(entry.LiquidTag, liquidId, StringComparison.Ordinal)) continue;

                result.Add(new UseInfo
                {
                    Sources = new[] { entry.Id },
                    Kind = "extract",
                    Produces = new[] { liquidEntryId }
                });
            }

            return result;
        }

        /// <summary>
        /// 物品管理器里这种液体的代表总量：取所有主成分为它的物品里最大的那份
        /// （例如避孕药里的 1 ml 避孕激素），没有来源时退回 1 ml。
        /// </summary>
        private static int RepresentativeAmount(string liquidId)
        {
            LiquidManager.TagItems();

            string part = Liquid.GetCurrentPartFromId(liquidId);
            if (string.IsNullOrEmpty(part)) return 0;

            var items = CatalogDataBuilder.Visible;
            int best = 0;

            for (int i = 0; i < items.Count; i++)
            {
                var entry = items[i];
                var item = entry == null ? null : entry.Item;
                if (item == null) continue;
                if (!string.Equals(entry.LiquidTag, liquidId, StringComparison.Ordinal)) continue;

                int amount;
                try { amount = item.GetTagReadonly(part).GetInt(); }
                catch { continue; }

                if (amount > best) best = amount;
            }

            return best > 0 ? best : 1000;
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
