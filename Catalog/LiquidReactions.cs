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

        /// <summary>
        /// 条件里点名、需要按液体管理器取名字的液体（也是 {0}/{1} 的参数顺序）。
        /// 用途反查时这些液体也会命中这条反应。
        /// </summary>
        internal string[] ConditionLiquidIds
        {
            get
            {
                switch (ConditionKey)
                {
                    case "react_cond_excess":
                    case "react_cond_acid_or_base":
                        return new[] { "acid", "base" };
                    case "react_cond_nomore_alcohol":
                    case "react_cond_alcohol_ratio":
                        return new[] { "alcohol" };
                    case "react_cond_fanta_order":
                        return new[] { "methylol", "stabilizing_agent" };
                    case "react_cond_solvent_ratio":
                        if (string.IsNullOrEmpty(Solvent)) return null;
                        return new[]
                        {
                            Solvent,
                            Reactants != null && Reactants.Length > 0 ? Reactants[0] : Solvent
                        };
                    default:
                        return null;
                }
            }
        }

        internal bool HasReactant(string liquidId)
        {
            return Contains(Reactants, liquidId);
        }

        internal bool HasProduct(string liquidId)
        {
            return Contains(Products, liquidId);
        }

        internal static bool Contains(string[] ids, string liquidId)
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
        private static LiquidReaction[] _all;

        /// <summary>全部反应（惰性构建：沸腾页要读游戏的 Liquid.Liquids，必须等系统就绪）。</summary>
        internal static LiquidReaction[] All
        {
            get
            {
                if (_all == null) _all = Build();
                return _all;
            }
        }

        /// <summary>
        /// 和某种液体相关的反应。
        /// asUse=false（来源）：它是产物的反应；asUse=true（用途）：它是反应物或溶剂/催化剂的反应。
        /// 溶剂前后不变（没被消耗、只是留在产物里）时只按用途算，详见
        /// <see cref="LiquidManager.IsUnchangedSolvent"/>。
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
                      string.Equals(reaction.Solvent, liquidId, StringComparison.Ordinal) ||
                      LiquidReaction.Contains(reaction.ConditionLiquidIds, liquidId)
                    : reaction.HasProduct(liquidId) &&
                      !LiquidManager.IsUnchangedSolvent(reaction, liquidId);

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
            var list = new List<LiquidReaction>
            {
                // 蛋白质 + 过量酸/碱 → 残渣（蛋白质 1:1 变性，多余的酸被消耗）
                R("protein", new[] { "protein", "acid" }, new[] { 10f, 10f },
                  new[] { "debris" }, new[] { 10f },
                  null, false, 0f, 0f, 0f,
                  "react_cond_excess", "{0}/{1} 过量"),

                // 稳定剂 + 酸/碱 → 残渣（酸/碱都行）
                R("stabilizer", new[] { "stabilizing_agent", "acid" }, new[] { 10f, 10f },
                  new[] { "debris" }, new[] { 10f },
                  null, false, 0f, 0f, 0f,
                  "react_cond_acid_or_base", "{0}/{1}"),

                // 有机废物矿化：废物 + 酸（消耗型，1 酸 : 40 废物）→ 矿物
                R("mineralize", new[] { "organic_waste", "acid" }, new[] { 10f, 0.25f },
                  new[] { "mineral" }, new[] { 1f },
                  null, false, 0f, 0f, 0f,
                  null, null),

                // 咖啡菇 + 酒精 → 咖啡因（每 tick 处理 1.5ml，溶剂最佳量 = 5 × 1.5ml）
                // 酒精只作溶剂、不被消耗（IL 里没有 ModifyTag alcohol），所以留在产物容器里
                R("coffee_alcohol", new[] { "coffee_mush" }, new[] { 10f },
                  new[] { "caffeine", "plant_matter", "alcohol" }, new[] { 7.5f, 2.5f, 7.5f },
                  "alcohol", false, 7.5f, 50f, 0f,
                  "react_cond_solvent_ratio", "{0}:{1} ≥ 5:1"),

                // 咖啡菇 + 水 → 咖啡因（每 tick 处理 0.5ml，水最佳量 = 5 × 0.5ml）
                R("coffee_water", new[] { "coffee_mush" }, new[] { 10f },
                  new[] { "caffeine", "plant_matter", "water" }, new[] { 1.25f, 8.75f, 2.5f },
                  "water", false, 2.5f, 80f, 0f,
                  "react_cond_solvent_ratio", "水足量（≥5:1）"),

                // 梦帽菇 + 酒精 → 梦帽提取物（比例按整份菇算，最佳量 = 5 × 10ml）
                R("dream_alcohol", new[] { "dream_cap_mush" }, new[] { 10f },
                  new[] { "dream_cap_extract", "plant_matter", "alcohol" }, new[] { 5f, 5f, 50f },
                  "alcohol", false, 50f, 40f, 0f,
                  "react_cond_solvent_ratio", "酒精足量（≥5:1）"),

                // 梦魇菇 + 酒精 → 梦魇提取物
                R("nightmare_alcohol", new[] { "nightmare_cap_mush" }, new[] { 10f },
                  new[] { "nightmare_extract", "plant_matter", "alcohol" }, new[] { 5f, 5f, 50f },
                  "alcohol", false, 50f, 40f, 0f,
                  "react_cond_solvent_ratio", "酒精足量（≥5:1）"),

                // nomore：梦帽提取物 + 还原剂（脂粘合剂是不限量催化剂，只写中间）
                R("nomore", new[] { "dream_cap_extract", "reducing_agent" }, new[] { 10f, 10f },
                  new[] { "nomore", "debris" }, new[] { 6f, 4f },
                  "lipobond_agent", true, 0f, 45f, 55f,
                  "react_cond_nomore_alcohol", "{0} ≤ 10%"),

                // 芬达：甲基醇 + 还原剂 → 10% 芬达（还原剂消耗 75%）
                R("fanta", new[] { "methylol", "reducing_agent" }, new[] { 10f, 7.5f },
                  new[] { "fanta" }, new[] { 1f },
                  null, false, 0f, 110f, 130f,
                  "react_cond_fanta_order", "{0} > {1}"),

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
                  "react_cond_waste_full", "≥100 °C 完全反应"),

                // 酸碱中和：两者等量消耗，没有产物
                RN("neutralize", new[] { "acid", "base" }, new[] { 10f, 10f },
                   null, false, 0f, 0f,
                   null, null),

                // 酸碱杀菌：每 100 只微生物消耗 1 单位酸/碱
                RN("microbe_acid", new[] { "microbe", "acid" }, new[] { 10f, 0.1f },
                   null, false, 0f, 0f,
                   "react_cond_acid_or_base", "{0}/{1}"),

                // 酒精杀菌：酒精 ≥ 总体积 40%
                RN("microbe_alcohol", new[] { "microbe" }, new[] { 10f },
                   "alcohol", false, 7f, 0f,
                   "react_cond_alcohol_ratio", "{0} ≥ 40%"),

                // 加热杀菌：≥70 °C 开始减少，≥100 °C 全灭
                RN("microbe_heat", new[] { "microbe" }, new[] { 10f },
                   null, false, 0f, 70f,
                   "react_cond_microbe_heat", "≥100 °C 完全反应"),

                // 氯消毒：氯 + 水（水必须存在、不被消耗）→ 杀微生物 + 去除化学污染物
                RN("chlorine", new[] { "chlorine", "microbe" }, new[] { 10f, 10f },
                   "water", true, 0f, 0f,
                   null, null),

                // 脂粘合剂沉降：油脂 + 脂粘合剂 → 残渣（8 油 : 1 脂粘合剂，常温）
                R("lipobond_fat", new[] { "fat", "lipobond_agent" }, new[] { 10f, 1.25f },
                  new[] { "debris" }, new[] { 11.25f },
                  null, false, 0f, 0f, 0f,
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
                   null, null)
            };

            // 沸腾：温度超过沸点后该液体每秒减少 2%。
            // IL 里是遍历 Liquid.Liquids 的**所有** isEvaporative 液体按各自 boilingPoint 判定，
            // 所以这里同样动态生成，不维护名单。
            AppendBoiling(list);

            return list.ToArray();
        }

        private static void AppendBoiling(List<LiquidReaction> list)
        {
            var liquids = Liquid.Liquids;
            if (liquids == null) return;

            for (int i = 0; i < liquids.Count; i++)
            {
                var liquid = liquids[i];
                if (liquid == null || !liquid.isEvaporative) continue;
                if (string.IsNullOrEmpty(liquid.identifier)) continue;

                list.Add(RN("boil_" + liquid.identifier,
                    new[] { liquid.identifier }, new[] { 10f },
                    null, false, 0f, (float)liquid.boilingPoint,
                    null, null));
            }
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
