using System;
using System.Collections.Generic;

using ItemCatalog.Catalog;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 枪械配件：原版枪械改装窗口的 8 个槽位。
    /// 槽位标题直接用原版本地化键（枪托 / 导轨 / 弹匣 / 下挂 / 枪管 / 特殊槽位 1~3）。
    ///
    /// 「能不能装」按游戏的两层规则判定：
    ///   ① 配件自身带对应槽位标签（<c>MOD_STOCK_TAG</c> / <c>MOD_RAIL_TAG</c> …）；
    ///   ② 配件自己的 `mayInventoryAddItemFunc` 若带「只允许某些枪」的限制
    ///      （`GunModHelper.AllowOnlyWeapon` 写进闭包 `weaponIds`），枪必须在那份名单里。
    /// 互斥组（同槽只能装一个）不影响「列出所有可装的配件」，这里不判。
    /// </summary>
    internal static class GunMods
    {
        internal const string StockTag = "MOD_STOCK_TAG";
        internal const string RailTag = "MOD_RAIL_TAG";
        internal const string MagazineTag = "MOD_MAGAZINE_TAG";
        internal const string UnderbarrelTag = "MOD_UNDER_RAIL_TAG";
        internal const string BarrelTag = "MOD_BARREL_TAG";
        internal const string SpecialTag = "MOD_SPECIAL_TAG";

        /// <summary>原版 `CreateGunModWindow` 的面板顺序（8 格，5 列）。</summary>
        internal static readonly string[] SlotKeys =
        {
            "mech_gun_tag_stock",
            "mech_gun_tag_rail",
            "mech_gun_tag_magazine",
            "mech_gun_tag_underbarrel",
            "mech_gun_tag_barrel",
            "mech_gun_tag_special1",
            "mech_gun_tag_special2",
            "mech_gun_tag_special3"
        };

        private static readonly string[] SlotTags =
        {
            StockTag, RailTag, MagazineTag, UnderbarrelTag, BarrelTag,
            SpecialTag, SpecialTag, SpecialTag
        };

        /// <summary>拿不到原版显示名时的兜底（与其它 Loc 兜底一样只用中文）。</summary>
        private static readonly string[] FallbackTitles =
        {
            "枪托", "导轨", "弹匣", "下挂", "枪管", "特殊槽位1", "特殊槽位2", "特殊槽位3"
        };

        internal static int SlotCount { get { return SlotKeys.Length; } }

        internal static string SlotTitle(int slot)
        {
            if (slot < 0 || slot >= FallbackTitles.Length) return string.Empty;
            return VanillaMachinePanel.MechLabel(SlotKeys[slot], FallbackTitles[slot]);
        }

        /// <summary>这把枪有没有改装槽（原版 `InitGun(..., moddable)` 给非左轮枪加的类型）。</summary>
        internal static bool IsModdable(GameItem gun)
        {
            if (gun == null) return false;

            try { return gun.IsGameItemType("MODDABLE"); }
            catch { return false; }
        }

        /// <summary>这件物品是不是枪械配件（带任一槽位标签）。</summary>
        internal static bool IsMod(GameItem item)
        {
            if (item == null) return false;

            for (int i = 0; i < SlotTags.Length; i++)
            {
                try { if (item.IsTag(SlotTags[i])) return true; }
                catch { }
            }

            return false;
        }

        /// <summary>这件配件能不能装到这把枪上（任一槽位接受即算）。</summary>
        internal static bool FitsAnySlot(string gunId, string modId)
        {
            if (string.IsNullOrEmpty(gunId) || string.IsNullOrEmpty(modId)) return false;

            for (int i = 0; i < SlotTags.Length; i++)
                if (Compatible(gunId, i).Contains(modId)) return true;

            return false;
        }

        /// <summary>某个槽位能装的配件 id（按目录顺序，已按枪的限制过滤）。</summary>
        internal static List<string> Compatible(string gunId, int slot)
        {
            if (string.IsNullOrEmpty(gunId) || slot < 0 || slot >= SlotTags.Length)
                return new List<string>();

            string key = gunId + "|" + SlotTags[slot];
            if (_cacheGeneration != CatalogDataBuilder.Generation)
            {
                _cache.Clear();
                _cacheGeneration = CatalogDataBuilder.Generation;
            }

            List<string> cached;
            if (_cache.TryGetValue(key, out cached) && cached != null) return cached;

            var ids = new List<string>();
            string tag = SlotTags[slot];

            foreach (var entry in RecipeIndex.AllEntries)
            {
                if (entry == null || entry.Unused || string.IsNullOrEmpty(entry.Id)) continue;

                var item = entry.Item;
                if (item == null) continue;

                bool hasTag;
                try { hasTag = item.IsTag(tag); }
                catch { hasTag = false; }
                if (!hasTag) continue;

                if (!Fits(item, gunId)) continue;
                if (!ids.Contains(entry.Id)) ids.Add(entry.Id);
            }

            ids.Sort(StringComparer.OrdinalIgnoreCase);
            _cache[key] = ids;
            return ids;
        }

        private static readonly Dictionary<string, List<string>> _cache =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private static int _cacheGeneration = -1;

        /// <summary>
        /// 配件自己的「仅限这些枪」限制。`GunModHelper.AllowOnlyWeapon` 把枪 id 数组
        /// 塞进委托闭包字段 `weaponIds`；没有这个字段就表示不限枪。
        /// 读不到时按「允许」处理，宁可多列也不漏列。
        ///
        /// 闭包类型是编译器生成的，编译期拿不到字面类型，所以这里走 `ModReflect`
        /// （游戏提供的替身：由游戏检查成员归属，不需要任何能力）。
        /// 以前是 `target.GetType().GetField(...)`，会被扫描器判成 reflection-external，
        /// 重新授权时模组会被拒绝加载。
        /// </summary>
        private static bool Fits(GameItem mod, string gunId)
        {
            try
            {
                var func = mod.mayInventoryAddItemFunc;
                if (func == null) return true;

                var list = func.GetInvocationList();
                for (int i = 0; i < list.Length; i++)
                {
                    var target = list[i] == null ? null : list[i].Target;
                    if (target == null) continue;

                    if (!ModReflect.Has(target, "weaponIds")) continue;

                    var allowed = ModReflect.Get<string[]>(target, "weaponIds");
                    if (allowed == null) continue;

                    for (int k = 0; k < allowed.Length; k++)
                        if (string.Equals(allowed[k], gunId, StringComparison.Ordinal)) return true;

                    return false;
                }
            }
            catch { }

            return true;
        }
    }
}
