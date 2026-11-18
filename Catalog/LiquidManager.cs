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

        private static bool IsLiquidTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return false;

            for (int i = 0; i < LiquidTags.Length; i++)
            {
                if (string.Equals(tag, LiquidTags[i], StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>
        /// 容器成分文本，照游戏成分检测器（ChemScannerUIManager / WaterScannerUIManager）：
        /// 第一行总体积，后面每种液体一行「本地化名: 体积 (百分比)」。
        /// mainOnly = true 时只写总体积 + 主要成分名（成分随品质变化的化学产品用）。
        /// </summary>
        internal static string FormatComposition(GameItem container, bool mainOnly = false, string mainName = null)
        {
            if (container == null) return null;

            try
            {
                var liquids = Liquid.Liquids;
                if (liquids == null) return null;

                int total = 0;
                for (int i = 0; i < liquids.Count; i++)
                {
                    if (liquids[i] == null) continue;
                    total += container.GetTagReadonly(liquids[i].currentPart).GetInt();
                }

                if (total <= 0) return null;

                // 第一行总体积，后面每种液体一行
                string totalText = null;
                try
                {
                    totalText = LocHelper.GetLocalizedMechanic("mech_scanner_total_volume",
                        new object[] { WaterScannerUIManager.FormatAmount(total) });
                }
                catch { }

                if (string.IsNullOrEmpty(totalText))
                    totalText = "Total: " + WaterScannerUIManager.FormatAmount(total);

                var sb = new System.Text.StringBuilder();
                sb.Append(totalText);

                if (mainOnly)
                {
                    // 药物这类成分可能随品质变化的，只写主要成分名
                    string name = mainName;
                    if (string.IsNullOrEmpty(name))
                    {
                        int best = 0;
                        for (int i = 0; i < liquids.Count; i++)
                        {
                            var l = liquids[i];
                            if (l == null) continue;

                            int amount = container.GetTagReadonly(l.currentPart).GetInt();
                            if (amount > best)
                            {
                                best = amount;
                                name = string.IsNullOrEmpty(l.displayName) ? l.identifier : l.displayName;
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(name)) sb.Append('\n').Append(name);
                    return sb.ToString();
                }

                for (int i = 0; i < liquids.Count; i++)
                {
                    var liquid = liquids[i];
                    if (liquid == null) continue;

                    int amount = container.GetTagReadonly(liquid.currentPart).GetInt();
                    if (amount <= 0) continue;

                    string name = string.IsNullOrEmpty(liquid.displayName)
                        ? liquid.identifier : liquid.displayName;
                    float percent = (float)amount / total * 100f;

                    sb.Append('\n')
                      .Append(name).Append(": ")
                      .Append(WaterScannerUIManager.FormatAmount(amount))
                      .Append(" (").Append(string.Format("{0:F2}", percent)).Append("%)");
                }

                return sb.ToString();
            }
            catch { return null; }
        }

        /// <summary>游戏写在 ACTIVATE_INGREDIENT_TAG 里的有效成分显示名。</summary>
        internal static string MainIngredientName(GameItem item)
        {
            if (item == null) return null;

            try
            {
                string liquidId = item.GetTagReadonly("ACTIVATE_INGREDIENT_TAG").GetString();
                var liquid = Liquid.GetLiquidFromId(liquidId);
                if (liquid == null) return null;

                return string.IsNullOrEmpty(liquid.displayName)
                    ? liquid.identifier : liquid.displayName;
            }
            catch { return null; }
        }
    }
}
