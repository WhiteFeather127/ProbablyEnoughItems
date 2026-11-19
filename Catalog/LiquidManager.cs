using System;
using System.Collections.Generic;

namespace ItemCatalog.Catalog
{
    /// <summary>
    /// 液体管理器：配方里的「液体需求」在游戏里不是一件物品——
    /// 水 / 酒精 / 酸 / 碱这些液体是灌进容器（<c>LIQUID_CONTAINER</c>）的**内容物**，
    /// 配方过滤器用标签（WATER / ALCOHOL…）或显示名来描述「要一点这种液体」。
    ///
    /// 图鉴展示时这类需求没有实体物品可画，只会落成一行没图标的文字。
    /// 这里把它们统一映射到一只**具体容器**（<see cref="ContainerId"/>，
    /// 默认 bottled_water）：玩家看到的格子可以直接点进容器详情，
    /// 也知道「往机器里塞一瓶这种水就行」。
    ///
    /// 要扩充液体清单：往 <see cref="LiquidTags"/>（标签 / 物品类型）或
    /// <see cref="LiquidNames"/>（显示名，双语）里加一行即可，渲染自动跟随。
    /// </summary>
    internal static class LiquidManager
    {
        /// <summary>展示液体需求用的容器物品 id（可整只买 / 整只拿的实体）。</summary>
        internal const string ContainerId = "bottled_water";

        /// <summary>液体需求的标签 / 物品类型（配方过滤器里的写法）。</summary>
        private static readonly string[] LiquidTags =
        {
            "WATER",            // 物品类型：一切装着水的容器
            "WATER_RATION",     // 水配给
            "ALCOHOL",          // 物品类型：酒类
            "LIQUID_CONTAINER"  // 泛指：任意液体容器（兜底）
        };

        /// <summary>液体需求的显示名（小写比较；游戏是英文，模组表里可能是中文）。</summary>
        private static readonly string[] LiquidNames =
        {
            "water", "alcohol", "acid", "base", "solvent", "chemical",
            "水", "酒精", "酒", "酸", "碱", "溶剂", "化学品"
        };

        /// <summary>这条配方成分是不是「液体需求」。</summary>
        internal static bool IsLiquidRequirement(RecipeIngredient ing)
        {
            if (ing == null) return false;

            if (ing.Tags != null)
            {
                for (int i = 0; i < ing.Tags.Count; i++)
                {
                    if (IsLiquidTag(ing.Tags[i])) return true;
                }
            }

            if (ing.Ids != null)
            {
                for (int i = 0; i < ing.Ids.Count; i++)
                {
                    if (IsLiquidTag(ing.Ids[i])) return true;
                }
            }

            // 最后一道：按显示名认（Opaque 成分只有名字可看）
            string name = ing.DisplayName;
            if (!string.IsNullOrEmpty(name))
            {
                string lower = name.Trim().ToLowerInvariant();
                for (int i = 0; i < LiquidNames.Length; i++)
                {
                    // 相等才认 —— 「Water Ration」这类包含关系的交给标签判定
                    if (string.Equals(lower, LiquidNames[i], StringComparison.Ordinal)) return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 液体需求应展示的物品 id（bottled_water）；不是液体需求返回 null，
        /// 调用方继续走普通 id 解析。
        /// </summary>
        internal static string Represent(RecipeIngredient ing)
        {
            return IsLiquidRequirement(ing) ? ContainerId : null;
        }

        /// <summary>
        /// 液体需求指向的具体液体 id（water / alcohol / acid…）。
        /// 认得出就返回，展示层可以画成「灌了这种液体的普通容器」；
        /// 只知道是液体、认不出种类时返回 null（仍用容器代表）。
        /// </summary>
        internal static string LiquidFor(RecipeIngredient ing)
        {
            if (ing == null || !IsLiquidRequirement(ing)) return null;

            string known = FirstKnownLiquid(ing.Tags) ?? FirstKnownLiquid(ing.Ids);
            if (known != null) return known;

            // 标签里没有具体液体 id 时按标签语义归类
            if (HasTag(ing, "WATER") || HasTag(ing, "WATER_RATION")) return WaterId;
            if (HasTag(ing, "ALCOHOL")) return "alcohol";

            // 最后按显示名（acid / base / solvent…）匹配液体表
            string name = ing.DisplayName;
            if (!string.IsNullOrEmpty(name))
            {
                string lower = name.Trim().ToLowerInvariant();
                if (IsKnownLiquid(lower)) return lower;
            }

            return null;
        }

        private static bool HasTag(RecipeIngredient ing, string tag)
        {
            if (ing == null || string.IsNullOrEmpty(tag)) return false;

            if (ing.Tags != null)
            {
                for (int i = 0; i < ing.Tags.Count; i++)
                    if (string.Equals(ing.Tags[i], tag, StringComparison.OrdinalIgnoreCase)) return true;
            }

            if (ing.Ids != null)
            {
                for (int i = 0; i < ing.Ids.Count; i++)
                    if (string.Equals(ing.Ids[i], tag, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static string FirstKnownLiquid(IList<string> ids)
        {
            if (ids == null) return null;

            for (int i = 0; i < ids.Count; i++)
            {
                string id = ids[i];
                if (!string.IsNullOrEmpty(id) && IsKnownLiquid(id))
                    return id.Trim().ToLowerInvariant();
            }

            return null;
        }

        private static bool IsKnownLiquid(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;

            try
            {
                var dict = Liquid.LiquidDict;
                return dict != null && dict.ContainsKey(id);
            }
            catch { return false; }
        }

        private static bool IsLiquidTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return false;

            for (int i = 0; i < LiquidTags.Length; i++)
            {
                if (string.Equals(tag, LiquidTags[i], StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static int _tagGeneration = -1;

        /// <summary>
        /// 给物品管理器里的每条记录打上「主要液体成分」标签（<see cref="ItemEntry.LiquidTag"/>）。
        /// 化学产品读游戏的有效成分 <c>ACTIVATE_INGREDIENT_TAG</c>，
        /// 其他物品取 CURRENT_PART_* 里量最大的一种。按目录 generation 缓存。
        /// </summary>
        internal static void TagItems()
        {
            if (_tagGeneration == CatalogDataBuilder.Generation) return;
            _tagGeneration = CatalogDataBuilder.Generation;

            var items = CatalogDataBuilder.Visible;
            for (int i = 0; i < items.Count; i++)
            {
                var entry = items[i];
                if (entry == null) continue;
                entry.LiquidTag = MainComponent(entry.Item);
            }
        }

        /// <summary>物品的主要液体成分 id。</summary>
        internal static string MainComponent(GameItem item)
        {
            if (item == null) return null;

            try
            {
                string active = item.GetTagReadonly("ACTIVATE_INGREDIENT_TAG").GetString();
                if (!string.IsNullOrEmpty(active)) return active;
            }
            catch { }

            var liquids = Liquid.Liquids;
            if (liquids == null) return null;

            string major = null;
            int best = 0;
            for (int i = 0; i < liquids.Count; i++)
            {
                var liquid = liquids[i];
                if (liquid == null) continue;

                int amount;
                try { amount = item.GetTagReadonly(liquid.currentPart).GetInt(); }
                catch { continue; }

                if (amount > best) { best = amount; major = liquid.identifier; }
            }

            return major;
        }

        /// <summary>是不是液体容器（有容量标签 / 是液体容器类型 / 已经有内容物）。</summary>
        internal static bool IsContainer(GameItem item)
        {
            if (item == null) return false;

            try
            {
                if (item.GetTagReadonly("LIQUID_CONTAINER_CAPACITY").GetInt() > 0) return true;
            }
            catch { }

            try
            {
                if (item.IsGameItemType("LIQUID_CONTAINER")) return true;
            }
            catch { }

            var liquids = Liquid.Liquids;
            if (liquids == null) return false;

            for (int i = 0; i < liquids.Count; i++)
            {
                var liquid = liquids[i];
                if (liquid == null) continue;

                try { if (item.GetTagReadonly(liquid.currentPart).GetInt() > 0) return true; }
                catch { }
            }

            return false;
        }

        /// <summary>容器里灌着的主要液体 id；不是容器 / 空容器返回 null。</summary>
        internal static string ContentLiquid(GameItem item)
        {
            return IsContainer(item) ? MainComponent(item) : null;
        }

        // ------------------------------------------------------- 容器成分

        /// <summary>一行成分：总体积、液体 id / 本地化名、含量。Summary = 只写名字。</summary>
        internal struct Component
        {
            internal string Id;
            internal string Name;
            internal int Amount;
            internal int Total;
            internal bool Summary;
        }

        /// <summary>液体 id → 显示名（优先游戏 Liquid 数据里的 displayName，其次本地化键）。</summary>
        internal static string NameOf(string liquidId)
        {
            if (string.IsNullOrEmpty(liquidId)) return "?";

            try
            {
                var liquid = Liquid.LiquidDict == null ? null : Liquid.LiquidDict[liquidId];
                if (liquid != null && !string.IsNullOrEmpty(liquid.displayName))
                    return liquid.displayName;
            }
            catch { }

            try
            {
                string text = LocHelper.GetLocalizedMechanic("liquid_name_" + liquidId, liquidId);
                return string.IsNullOrEmpty(text) ? liquidId : text;
            }
            catch { return liquidId; }
        }

        /// <summary>读一个容器当前的液体成分：液体 identifier → 千分比（0..10000）。</summary>
        internal static Dictionary<string, int> Parts(GameItem item)
        {
            var parts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (item == null) return parts;

            try
            {
                var liquids = Liquid.Liquids;
                if (liquids == null) return parts;

                for (int i = 0; i < liquids.Count; i++)
                {
                    var liquid = liquids[i];
                    if (liquid == null) continue;

                    string id = liquid.identifier;
                    if (string.IsNullOrEmpty(id)) continue;

                    int value;
                    try { value = item.GetTagReadonly(liquid.currentPart).GetInt(); }
                    catch { continue; }

                    if (value > 0) parts[id] = value;
                }
            }
            catch { }

            return parts;
        }

        /// <summary>
        /// 容器成分列表。mainOnly = true 时只返回一项（总体积 + 主要成分名），
        /// 用于成分随品质变化的化学产品；否则按 Liquid.Liquids 顺序列全部成分。
        /// </summary>
        internal static List<Component> Components(GameItem container,
                                                   bool mainOnly = false, string mainName = null)
        {
            var result = new List<Component>();
            var parts = Parts(container);
            if (parts.Count == 0) return result;

            int total = 0;
            foreach (var pair in parts) total += pair.Value;
            if (total <= 0) return result;

            string majorId = null;
            int major = 0;
            foreach (var pair in parts)
            {
                if (pair.Value > major) { major = pair.Value; majorId = pair.Key; }
            }

            if (mainOnly)
            {
                result.Add(new Component
                {
                    Id = majorId,
                    Name = string.IsNullOrEmpty(mainName) ? NameOf(majorId) : mainName,
                    Amount = total,
                    Total = total,
                    Summary = true
                });
                return result;
            }

            var liquids = Liquid.Liquids;
            if (liquids == null) return result;

            for (int i = 0; i < liquids.Count; i++)
            {
                var liquid = liquids[i];
                if (liquid == null) continue;

                int amount;
                if (!parts.TryGetValue(liquid.identifier, out amount) || amount <= 0) continue;

                result.Add(new Component
                {
                    Id = liquid.identifier,
                    Name = NameOf(liquid.identifier),
                    Amount = amount,
                    Total = total
                });
            }

            return result;
        }

        /// <summary>液体条目（liquid:*）的成分列表。</summary>
        internal static List<Component> Components(ItemEntry entry)
        {
            return entry == null ? new List<Component>() : Components(entry.Item);
        }

        /// <summary>成分检测器风格的总体积行。</summary>
        internal static string TotalText(int total)
        {
            try
            {
                string text = LocHelper.GetLocalizedMechanic("mech_scanner_total_volume",
                    new object[] { WaterScannerUIManager.FormatAmount(total) });
                if (!string.IsNullOrEmpty(text)) return text;
            }
            catch { }

            return "Total volume: " + WaterScannerUIManager.FormatAmount(total);
        }

        // ------------------------------------------------------- 卖水的容器

        /// <summary>
        /// 水的液体 id：pure / hq / base / rust / ghost / gutterflow
        /// 都是同一种液体，差别只在纯度与杂质。
        /// </summary>
        internal const string WaterId = "water";

        /// <summary>来源配置的 `water` 列是不是「不灌水」（留空 / empty / none / no_water / -）。</summary>
        internal static bool IsEmptyWater(string water)
        {
            string kind = (water ?? string.Empty).Trim().ToLowerInvariant();

            return kind.Length == 0
                   || kind == "empty"
                   || kind == "none"
                   || kind == "no_water"
                   || kind == "nowater"
                   || kind == "-";
        }

        /// <summary>
        /// `water` 列允许限定「只有哪几件容器有货水」，写法：
        ///   `base@large_bottled_water`            只给这一件灌
        ///   `base@large_bottled_water|water_pitcher`  多件用 `|` 分隔
        /// 不带 `@` 就是该来源所有容器统一按这个水种算；不在名单里的返回 null。
        /// </summary>
        internal static string WaterFor(string water, string itemId)
        {
            if (string.IsNullOrEmpty(water)) return water;

            int at = water.IndexOf('@');
            if (at < 0) return water;

            string grade = water.Substring(0, at).Trim();
            string list = water.Substring(at + 1);

            if (string.IsNullOrEmpty(itemId)) return null;

            string[] ids = list.Split('|');
            for (int i = 0; i < ids.Length; i++)
            {
                string id = ids[i].Trim();
                if (id.Length > 0 && string.Equals(id, itemId, StringComparison.Ordinal))
                    return grade;
            }

            return null;
        }

        /// <summary>
        /// `WaterHelper.AddWater` 里按 grade 索引的纯度档位表（千分比，照 IL 抄的）：
        /// 0 → 9990~9999、1 → 9900~9989、2 → 9800~9899、
        /// 3 → 9600~9799、4 → 9000~9599、5 → 3500~8999。
        /// 各水种调的 grade：pure=0、hq=1、base=2、ghost=3、rust=4、gutterflow=5，
        /// 所以水的占比是**算出来的**，不需要采样去猜。
        /// </summary>
        internal static void PurityBand(string water, out int lo, out int hi)
        {
            switch ((water ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "pure": lo = 9990; hi = 9999; return;
                case "hq": lo = 9900; hi = 9989; return;
                case "ghost": lo = 9600; hi = 9799; return;
                case "rust": lo = 9000; hi = 9599; return;
                case "gutterflow":
                case "gutter": lo = 3500; hi = 8999; return;
                default: lo = 9800; hi = 9899; return;      // base
            }
        }

        /// <summary>水质 → 对应的杂质分布表（原版 5 张，按水种区分）。</summary>
        internal static Dictionary<string, float> Distribution(string water)
        {
            string kind = (water ?? string.Empty).Trim().ToLowerInvariant();

            try
            {
                switch (kind)
                {
                    case "pure":
                    case "hq":
                        return WaterPremadeHelper.waterContaminantDistribution;
                    case "rust":
                        return WaterPremadeHelper.rustWaterContaminantDistribution;
                    case "ghost":
                        return WaterPremadeHelper.ghostwaterContaminantDistribution;
                    case "gutterflow":
                    case "gutter":
                        return WaterPremadeHelper.gutterflowContaminantDistribution;
                    default:
                        return WaterPremadeHelper.baseWaterContaminantDistribution;
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// 卖水的容器成分（区间文本，供来源页与提示框）：水占比按档位表、
        /// 杂质按该水种的分布表分摊剩余容量。**是区间不是实例值**——
        /// 同一件容器在不同来源灌的水不同，不能当成某一瓶的真实含量。
        /// </summary>
        internal static string WaterComposition(int capacity, string water)
        {
            int lo, hi;
            PurityBand(water, out lo, out hi);

            string head = ModEntry.T("tt_ingredient") + ": " + NameOf(WaterId)
                          + " " + PercentRange(lo / 100f, hi / 100f);

            var dist = Distribution(water);
            if (capacity <= 0 || dist == null || dist.Count == 0) return head;

            float sum = 0f;
            foreach (var pair in dist)
            {
                if (pair.Value > 0f) sum += pair.Value;
            }
            if (sum <= 0f) return head;

            // 非水部分：水最多时最少，水最少时最多（容量与 CURRENT_PART 同一单位）
            int nonWaterMin = (int)(capacity * (100f - hi / 100f) / 100f);
            int nonWaterMax = (int)(capacity * (100f - lo / 100f) / 100f);
            if (nonWaterMax < nonWaterMin) nonWaterMax = nonWaterMin;

            var impurities = new List<string>();
            foreach (var pair in dist)
            {
                if (pair.Value <= 0f) continue;

                float share = pair.Value / sum;
                int min = (int)(nonWaterMin * share);
                int max = (int)(nonWaterMax * share);
                if (max <= 0) continue;

                // 分布表的键是标签名（`CURRENT_PART_ORGANIC_WASTE`），
                // 直接拿去查液体表查不到，会原样打印出标签名。
                string line = NameOf(PartToLiquidId(pair.Key)) + " " + FormatAmount(min);
                if (max != min) line += "~" + FormatAmount(max);

                impurities.Add(line);
            }

            return AppendImpurityLines(head, impurities);
        }

        /// <summary>杂质分布表的键是标签名（`CURRENT_PART_HEAVY_METAL`），液体表用的是 id。</summary>
        private static string PartToLiquidId(string part)
        {
            if (string.IsNullOrEmpty(part)) return part;

            const string prefix = "CURRENT_PART_";

            return part.StartsWith(prefix, StringComparison.Ordinal)
                ? part.Substring(prefix.Length).ToLowerInvariant()
                : part;
        }

        /// <summary>主成分的百分比区间；单值就只写一个。</summary>
        private static string PercentRange(float lo, float hi)
        {
            if (hi <= 0f) return string.Empty;
            if (lo <= 0f || lo > hi) lo = hi;

            if (hi - lo < 0.005f) return hi.ToString("0.#") + "%";
            return lo.ToString("0.#") + "%~" + hi.ToString("0.#") + "%";
        }

        /// <summary>`CURRENT_PART_*` 是绝对量（单位 1/1000 ml），不是千分比。</summary>
        private static string FormatAmount(int thousandths)
        {
            if (thousandths <= 0) return "0ml";

            float ml = thousandths / 1000f;

            if (ml >= 1f) return ml.ToString("0.#") + "ml";
            if (ml >= 0.01f) return ml.ToString("0.##") + "ml";
            return thousandths + "µl";
        }

        private static string AppendImpurityLines(string head, List<string> impurities)
        {
            if (impurities == null || impurities.Count == 0) return head;

            var sb = new System.Text.StringBuilder(head);
            sb.Append('\n').Append(ModEntry.T("tt_impurity")).Append(':');

            for (int i = 0; i < impurities.Count; i++)
            {
                sb.Append('\n').Append(impurities[i]);
            }

            return sb.ToString();
        }
    }
}
