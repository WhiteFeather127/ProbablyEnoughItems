using System;
using System.Collections.Generic;

using ItemCatalog.UI;

namespace ItemCatalog.Catalog
{
    /// <summary>
    /// 液体管理器（可查对象部分）：每种液体对应一只**装满该纯净液体**的默认容器
    /// （<see cref="LiquidManager.ContainerId"/>），作为图鉴里常驻的条目参与浏览、搜索、
    /// 来源/去处与拿取。
    ///
    /// 以前按「物品管理器里该液体的实际总量」灌一个代表量（常常只有 1 ml），
    /// 现在是满瓶纯净液体：容量取容器自身的 LIQUID_CONTAINER_CAPACITY。
    /// </summary>
    internal static class LiquidCatalog
    {
        internal const string EntryPrefix = "liquid:";

        private static readonly Dictionary<string, ItemEntry> _entries =
            new Dictionary<string, ItemEntry>(StringComparer.Ordinal);

        private static readonly List<ItemEntry> _ordered = new List<ItemEntry>();

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

        /// <summary>图鉴常驻条目：每种液体一只满瓶（按液体注册顺序）。</summary>
        internal static List<ItemEntry> VisibleEntries()
        {
            EnsureBuilt();
            return _ordered;
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

                string liquidId = liquid.identifier;
                string id = EntryPrefix + liquidId;
                var entry = new ItemEntry
                {
                    Id = id,
                    Name = liquid.displayName,
                    Item = bottle,
                    TakeFactory = delegate { return CreateBottle(liquidId); }
                };

                _entries[id] = entry;
                _ordered.Add(entry);
            }

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[liquid] entries=" + _entries.Count);
        }

        /// <summary>造一只装满该纯净液体的默认容器（常驻展示 / 拿取都用它）。</summary>
        internal static GameItem CreateBottle(string liquidId)
        {
            GameItem bottle = null;
            try
            {
                bottle = CatalogWindow.CreatePreviewItem(LiquidManager.ContainerId);
                if (bottle == null) return null;

                int capacity = 0;
                try { capacity = bottle.GetTagReadonly("LIQUID_CONTAINER_CAPACITY").GetInt(); }
                catch { }
                if (capacity <= 0) capacity = LiquidManager.WaterCapacityUl();

                // 直接写液体标签，且只装这一种液体 —— 满瓶、纯净，
                // 不走 AddLiquid 的温度/特性管线，避免灌进别的东西。
                ChemicalHelper.SetChemContentUL(bottle, liquidId, capacity);
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

        /// <summary>换场景时销毁代表容器，下次浏览重新建。</summary>
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
