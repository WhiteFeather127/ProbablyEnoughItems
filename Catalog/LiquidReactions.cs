using System;
using System.Collections.Generic;

namespace ItemCatalog.Catalog
{
    /// <summary>
    /// 一条成分反应（从 ChemicalHelper.Handle* 反编译收录）。
    /// 展示基准是 **10 ml 单位体积**：用量按理想配比换算，每条反应独立成页，
    /// 不模拟同一容器里同时发生的其它反应。
    /// </summary>
    internal sealed class LiquidReaction
    {
        internal string Id;

        internal string[] Reactants;
        internal float[] ReactantMl;

        internal string[] Products;
        internal float[] ProductMl;

        /// <summary>溶剂 / 催化剂（液体 id）；null = 不需要。</summary>
        internal string Solvent;
        internal bool SolventIsCatalyst;

        /// <summary>
        /// 装进容器的最佳溶剂量（ml）。0 = 不装进容器（不限量的催化剂只写中间那列）。
        /// </summary>
        internal float SolventMl;

        internal float TempMin;    // °C；0 = 常温
        internal float TempMax;    // °C；<=0 = 只有下限

        /// <summary>额外条件（本地化键 + 兜底文本），可为空。</summary>
        internal string ConditionKey;
        internal string ConditionFallback;

        internal bool HasReactant(string liquidId)
        {
            return Contains(Reactants, liquidId);
        }

        internal bool HasProduct(string liquidId)
        {
            return Contains(Products, liquidId);
        }

        private static bool Contains(string[] ids, string liquidId)
        {
            if (ids == null || string.IsNullOrEmpty(liquidId)) return false;

            for (int i = 0; i < ids.Length; i++)
                if (string.Equals(ids[i], liquidId, StringComparison.Ordinal)) return true;

            return false;
        }
    }

    /// <summary>
    /// 液体之间的全部成分反应。数据抄自 ChemicalHelper 的 Handle* 规则：
    /// 只用公开可见的液体 identifier，不依赖任何 id 猜测。
    /// 溶剂装进容器（按最佳量）；不限量的催化剂只写中间条件列。
    /// </summary>
    internal static class LiquidReactions
    {
        internal static readonly LiquidReaction[] All = Build();

        /// <summary>
        /// 和某种液体相关的反应。
        /// asUse=false（来源）：它是产物的反应；asUse=true（用途）：它是反应物或溶剂/催化剂的反应。
        /// </summary>
        internal static List<LiquidReaction> For(string liquidId, bool asUse)
        {
            var result = new List<LiquidReaction>();
            if (string.IsNullOrEmpty(liquidId)) return result;

            for (int i = 0; i < All.Length; i++)
            {
                var reaction = All[i];
                if (reaction == null) continue;

                bool hit = asUse
                    ? reaction.HasReactant(liquidId) ||
                      string.Equals(reaction.Solvent, liquidId, StringComparison.Ordinal)
                    : reaction.HasProduct(liquidId);

                if (hit) result.Add(reaction);
            }

            return result;
        }

        /// <summary>需要温度的反应（加热板页用）。</summary>
        internal static List<LiquidReaction> Heated()
        {
            var result = new List<LiquidReaction>();

            for (int i = 0; i < All.Length; i++)
            {
                var reaction = All[i];
                if (reaction != null && reaction.TempMin > 0f) result.Add(reaction);
            }

            return result;
        }

        private static LiquidReaction[] Build()
        {
            return new[]
            {
                // 蛋白质 + 过量酸/碱 → 残渣（蛋白质 1:1 变性，多余的酸被消耗）
                R("protein", new[] { "protein", "acid" }, new[] { 10f, 10f },
                  new[] { "debris" }, new[] { 10f },
                  null, false, 0f, 0f, 0f,
                  "react_cond_excess", "酸/碱过量时发生"),

                // 稳定剂 + 酸/碱 → 残渣（酸/碱都行）
                R("stabilizer", new[] { "stabilizing_agent", "acid" }, new[] { 10f, 10f },
                  new[] { "debris" }, new[] { 10f },
                  null, false, 0f, 0f, 0f,
                  "react_cond_acid_or_base", "酸或碱均可"),

                // 有机废物矿化：废物 + 酸（消耗型，1 酸 : 40 废物）→ 矿物
                R("mineralize", new[] { "organic_waste", "acid" }, new[] { 10f, 0.25f },
                  new[] { "mineral" }, new[] { 1f },
                  null, false, 0f, 0f, 0f,
                  null, null),

                // 咖啡菇 + 酒精 → 咖啡因（每 tick 处理 1.5ml，溶剂最佳量 = 5 × 1.5ml）
                R("coffee_alcohol", new[] { "coffee_mush" }, new[] { 10f },
                  new[] { "caffeine", "plant_matter" }, new[] { 7.5f, 2.5f },
                  "alcohol", false, 7.5f, 50f, 0f,
                  "react_cond_solvent_ratio", "溶剂足量（≥5:1）"),

                // 咖啡菇 + 水 → 咖啡因（每 tick 处理 0.5ml，水最佳量 = 5 × 0.5ml）
                R("coffee_water", new[] { "coffee_mush" }, new[] { 10f },
                  new[] { "caffeine", "plant_matter" }, new[] { 1.25f, 8.75f },
                  "water", false, 2.5f, 80f, 0f,
                  "react_cond_solvent_ratio", "水足量（≥5:1）"),

                // 梦帽菇 + 酒精 → 梦帽提取物（比例按整份菇算，最佳量 = 5 × 10ml）
                R("dream_alcohol", new[] { "dream_cap_mush" }, new[] { 10f },
                  new[] { "dream_cap_extract", "plant_matter" }, new[] { 5f, 5f },
                  "alcohol", false, 50f, 40f, 0f,
                  "react_cond_solvent_ratio", "酒精足量（≥5:1）"),

                // 梦魇菇 + 酒精 → 梦魇提取物
                R("nightmare_alcohol", new[] { "nightmare_cap_mush" }, new[] { 10f },
                  new[] { "nightmare_extract", "plant_matter" }, new[] { 5f, 5f },
                  "alcohol", false, 50f, 40f, 0f,
                  "react_cond_solvent_ratio", "酒精足量（≥5:1）"),

                // nomore：梦帽提取物 + 还原剂（脂粘合剂是不限量催化剂，只写中间）
                R("nomore", new[] { "dream_cap_extract", "reducing_agent" }, new[] { 10f, 10f },
                  new[] { "nomore", "debris" }, new[] { 6f, 4f },
                  "lipobond_agent", true, 0f, 45f, 55f,
                  "react_cond_nomore_alcohol", "酒精 ≤ 总体积 10%"),

                // 芬达：甲基醇 + 还原剂 → 10% 芬达（还原剂消耗 75%）
                R("fanta", new[] { "methylol", "reducing_agent" }, new[] { 10f, 7.5f },
                  new[] { "fanta" }, new[] { 1f },
                  null, false, 0f, 110f, 130f,
                  "react_cond_fanta_order", "甲基醇 > 稳定剂"),

                // 梦帽菇受热分解 → 植物纤维
                R("dream_mush_heat", new[] { "dream_cap_mush" }, new[] { 10f },
                  new[] { "plant_matter" }, new[] { 10f },
                  null, false, 0f, 50f, 0f,
                  null, null),

                // 梦魇菇受热分解 → 植物纤维
                R("nightmare_mush_heat", new[] { "nightmare_cap_mush" }, new[] { 10f },
                  new[] { "plant_matter" }, new[] { 10f },
                  null, false, 0f, 50f, 0f,
                  null, null),

                // 有机废物受热焦化 → 物理污染物（处理量的 33%）
                R("waste_heat", new[] { "organic_waste" }, new[] { 10f },
                  new[] { "physical_contaminant" }, new[] { 3.3f },
                  null, false, 0f, 80f, 0f,
                  "react_cond_waste_full", "≥100 °C 时全部转化"),

                // 酸碱中和：两者等量消耗，没有产物
                RN("neutralize", new[] { "acid", "base" }, new[] { 10f, 10f },
                   null, false, 0f, 0f,
                   null, null),

                // 酸碱杀菌：每 100 只微生物消耗 1 单位酸/碱
                RN("microbe_acid", new[] { "microbe", "acid" }, new[] { 10f, 0.1f },
                   null, false, 0f, 0f,
                   "react_cond_acid_or_base", "酸或碱均可"),

                // 酒精杀菌：酒精 ≥ 总体积 40%
                RN("microbe_alcohol", new[] { "microbe" }, new[] { 10f },
                   "alcohol", false, 7f, 0f,
                   "react_cond_alcohol_ratio", "酒精 ≥ 总体积 40%"),

                // 加热杀菌：≥70 °C 开始减少，≥100 °C 全灭
                RN("microbe_heat", new[] { "microbe" }, new[] { 10f },
                   null, false, 0f, 70f,
                   "react_cond_microbe_heat", "≥100 °C 时全灭"),

                // 氯消毒：杀微生物 + 去除化学污染物
                RN("chlorine", new[] { "chlorine", "microbe" }, new[] { 10f, 10f },
                   null, false, 0f, 0f,
                   null, null),

                // 梦帽提取物受热分解
                RN("dream_extract_heat", new[] { "dream_cap_extract" }, new[] { 10f },
                   null, false, 0f, 60f,
                   null, null),

                // 梦魇提取物受热分解
                RN("nightmare_extract_heat", new[] { "nightmare_extract" }, new[] { 10f },
                   null, false, 0f, 50f,
                   null, null),

                // 芬达受热：≥130 °C 每秒损失 2%
                RN("fanta_heat", new[] { "fanta" }, new[] { 10f },
                   null, false, 0f, 130f,
                   null, null),

                // 沸腾：温度超过沸点后该液体每秒减少 2%
                RN("boil_water", new[] { "water" }, new[] { 10f },
                   null, false, 0f, 100f,
                   null, null),
                RN("boil_alcohol", new[] { "alcohol" }, new[] { 10f },
                   null, false, 0f, 78.37f,
                   null, null),
                RN("boil_lipobond", new[] { "lipobond_agent" }, new[] { 10f },
                   null, false, 0f, 55f,
                   null, null)
            };
        }

        private static LiquidReaction R(string id,
            string[] reactants, float[] reactantMl,
            string[] products, float[] productMl,
            string solvent, bool catalyst, float solventMl,
            float tempMin, float tempMax,
            string conditionKey, string conditionFallback)
        {
            return new LiquidReaction
            {
                Id = id,
                Reactants = reactants ?? new string[0],
                ReactantMl = reactantMl ?? new float[0],
                Products = products ?? new string[0],
                ProductMl = productMl ?? new float[0],
                Solvent = solvent,
                SolventIsCatalyst = catalyst,
                SolventMl = solventMl,
                TempMin = tempMin,
                TempMax = tempMax,
                ConditionKey = conditionKey,
                ConditionFallback = conditionFallback
            };
        }

        /// <summary>没有实物产物的反应：右侧放空容器，底下写「无」或残留的溶剂。</summary>
        private static LiquidReaction RN(string id,
            string[] reactants, float[] reactantMl,
            string solvent, bool catalyst, float solventMl,
            float tempMin,
            string conditionKey, string conditionFallback)
        {
            return R(id, reactants, reactantMl, null, null,
                     solvent, catalyst, solventMl, tempMin, 0f,
                     conditionKey, conditionFallback);
        }
    }
}
