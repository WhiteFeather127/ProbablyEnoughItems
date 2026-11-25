﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿using System;
using System.Collections.Generic;

using ItemCatalog.Catalog;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 机器页的静态数据。
    ///
    /// **面板结构不再在这里维护**：机器窗口直接借用工厂造好的 <c>contentWindow</c>
    /// （模组补丁过的版本），按面板 role 取格子填样本，见 VanillaMachinePanel.Adopt。
    /// 这里只剩游戏 IL 里写死、运行时拿不到的那部分：熔炉 / 搅拌机等的固定产出配方。
    /// </summary>
    internal static class MachinePanels
    {
        // ===================== 产出配方（静态） =====================
        //
        // 游戏机器没有 recipeManager/CraftingRecipe：熔炉是 MachineFurnace 的周期回调
        // （<Furnace>b__5），蒸发器等是自己的工艺方法——产出逻辑写死在 IL 里。
        // 所以机器页的配方和面板一样静态收录，不再依赖运行时配方索引。

        /// <summary>一个配方变体：投入 / 用到的模块 / 产物（都是物品 id）。</summary>
        internal sealed class Recipe
        {
            internal string[] Inputs;
            internal string[] Modules;
            internal string[] Outputs;
            internal string NoteKey;      // 本地化键（mach_furnace_*）；取不到时显示 NoteFallback
            internal string NoteFallback;

            /// <summary>
            /// 容器槽里放的物品 id。搅拌机这类「把投入倒进液体容器」的机器用它；
            /// 其他机器为 null。展示用 LiquidManager.ContainerId（bottled_water）。
            /// </summary>
            internal string Container;

            /// <summary>
            /// 容器里预先灌入的液体 id（化学制剂成型机用）：容器预览按这个液体装填，
            /// 量和游戏配方表的 requiredVolume 一致（单位 1/1000 ml）。
            /// </summary>
            internal string ContainerLiquid;

            internal int ContainerVolume;

            /// <summary>概率产物在 <see cref="Outputs"/> 里的下标（图鉴标黄底、提示框写概率）。</summary>
            internal int[] Uncertain;

            /// <summary>概率产物的提示文字（例如「50%」）。</summary>
            internal string UncertainNote;

            /// <summary>水培机每周期耗水（ml），用于面板下面的成分变化。</summary>
            internal int WaterUse;

            /// <summary>水槽里矿物的最佳浓度（万分比：矿物占水量的 1/10000，150 = 1.5%）。</summary>
            internal int NutrientPercent;

            /// <summary>每周期消耗的矿物（绝对量，单位 1/1000 ml，与 RemoveContaminantFromContainer 一致）。</summary>
            internal int MineralUse;

            /// <summary>
            /// 产物样本的实例工厂（下标 = <see cref="Outputs"/> 的下标）。
            /// 返回 null 时按 Outputs 里的 id 造普通实例；作物用它显示最佳品质（大果）。
            /// </summary>
            internal Func<int, GameItem> OutputItem;

            /// <summary>
            /// 投入样本的实例工厂（下标 = <see cref="Inputs"/> 的下标）。
            /// 返回 null 时按 Inputs 里的 id 造普通实例；搅拌机用它显示作物的某一品质档。
            /// </summary>
            internal Func<int, GameItem> InputItem;

            /// <summary>单独的芯片槽里的物品 id（解序列器：加密模块）；null = 没有该槽。</summary>
            internal string Chip;

            /// <summary>动物笼：这一条配方展示的动物 id（同类一雄一雌各一只）。</summary>
            internal string CageAnimal;

            /// <summary>动物笼：喂食器槽展示的物品 id（多个候选时随配方轮播）。</summary>
            internal string CageDispenser;

            /// <summary>动物笼：自动屠宰槽展示的物品 id（多个候选时随配方轮播）。</summary>
            internal string CageButcher;

            /// <summary>
            /// 达成这一条配方还需要的模块品质加成（百分比）。
            /// 熔炉页顶部显示「需品质加成 +X%」，和下面的材料 / 助熔剂一起轮播。
            /// </summary>
            internal int RequiredQualityPercent;

            internal Recipe(string[] inputs, string[] modules, string[] outputs,
                            string noteKey, string noteFallback,
                            int requiredQualityPercent = 0, string container = null,
                            string containerLiquid = null, int containerVolume = 0,
                            int[] uncertain = null, string uncertainNote = null,
                            int waterUse = 0, string chip = null)
            {
                Inputs = inputs ?? empty;
                Modules = modules ?? empty;
                Outputs = outputs ?? empty;
                NoteKey = noteKey;
                NoteFallback = noteFallback;
                RequiredQualityPercent = requiredQualityPercent;
                Container = container;
                ContainerLiquid = containerLiquid;
                ContainerVolume = containerVolume;
                Uncertain = uncertain;
                UncertainNote = uncertainNote;
                WaterUse = waterUse;
                Chip = chip;
            }
        }

        private static readonly string[] empty = new string[0];

        /// <summary>
        /// 一组轮播配方：同一纯度 / 同一类产出下，能达成它的不同投料方式
        /// （以及模块带来的不同效果）各是一条，页面每秒轮播一条。
        /// TitleKey 是这一组的名字（纯度档位 / 特殊产出）。
        /// </summary>
        internal sealed class RecipeGroup
        {
            internal string TitleKey;
            internal string TitleFallback;
            internal Recipe[] Variants;

            /// <summary>标题直接用这个物品的显示名（搅拌机一物品一页）；null = 用 TitleKey。</summary>
            internal string TitleItemId;

            /// <summary>标题后缀（作物品质档用，例如「（大）」）。</summary>
            internal string TitleSuffix;

            /// <summary>熔炉纯度常量（INGOT_PURITY_*）：预览产物按它写纯度特性；其他机器为 null。</summary>
            internal string Purity;
        }

        /// <summary>
        /// 熔炉产出：按 MachineFurnace/&lt;&gt;c__DisplayClass0_0::&lt;Furnace&gt;b__5
        /// 与 IngotPurityHelper.SetPurityFromInput 的反编译结果复刻。
        ///
        /// 真实规则（2026-11 反编译）：
        /// · 投入格只收 scrap_metal / common_ore / flux_agent / advanced_flux_agent
        ///   （垃圾只在装了 furnace_module_junk 时可放入）。
        /// · 消耗的是「scrap 列表 + ore 列表」合并后的**最后 num 个**，
        ///   num 默认 2，装 furnace_module_blast 时为 3。
        /// · 纯度 = LOW + floor(2 × 消耗里的矿石数 / num) + 品质加成。
        /// · 品质加成 = 模块品质总百分比 ÷ 100（整数除法）。
        ///   system_module_quality 本体只有 8%，单块/少量不产生任何档位加成；
        ///   只有随机模块把总品质堆到 100% 以上才会 +1 档。
        /// · furnace_module_blast：品质加成 -1，但每次产出 2 个锭。
        /// · advanced_flux_agent：品质加成不足 1 时补到 1，且代码里**不销毁**。
        /// · flux_agent：同上，但会 Destroy（一次性）。
        /// · furnace_module_junk：不产锭；6 个垃圾 → 4 个废金属。
        ///
        /// 档位：VERY_LOW(0) / LOW(1) / FAIR(2) / HIGH(3) / VERY_HIGH(4) / PERFECT(5)。
        /// 下面每组列的是按上述公式能稳定落在该档的组合。
        /// </summary>
        private static readonly RecipeGroup[] HandFurnaceGroups =
        {
            // 完美：需要品质加成 +2（总品质 ≥200%），基础两矿 + 高级助熔剂再 +1。
            // 标准品质模块（8%）做不到，只有随机高品模块叠出来；条件写在备注里。
            new RecipeGroup
            {
                TitleKey = "mach_furnace_perfect", TitleFallback = "完美纯度金属锭",
                Purity = "INGOT_PURITY_PERFECT",
                Variants = new[]
                {
                    // 两矿 +2，还需要品质加成 +2（200%）
                    new Recipe(new[] { "common_ore", "common_ore" },
                               null, new[] { "metal_ingot" },
                               "mach_note_perfect_core", "两矿 +2，需品质加成 +200% → 完美", 200),
                    // 三矿 +2，爆燃 -1，还需要品质加成 +3（300%）；双锭
                    new Recipe(new[] { "common_ore", "common_ore", "common_ore" },
                               new[] { "furnace_module_blast" },
                               new[] { "metal_ingot", "metal_ingot" },
                               "mach_note_perfect_core_blast", "三矿 +2，爆燃 -1，需品质加成 +300% → 完美；双锭", 300)
                }
            },
            new RecipeGroup
            {
                TitleKey = "mach_furnace_very_high", TitleFallback = "极高纯度金属锭",
                Purity = "INGOT_PURITY_VERY_HIGH",
                Variants = new[]
                {
                    // 两矿 +2，助熔剂保底 +1（一次性）
                    new Recipe(new[] { "common_ore", "common_ore", "flux_agent" },
                               null, new[] { "metal_ingot" },
                               "mach_note_flux", "两份矿石 +2，助熔剂保底 +1（会被消耗）"),
                    // 两矿 +2，高级助熔剂保底 +1（IL 不销毁）
                    new Recipe(new[] { "common_ore", "common_ore", "advanced_flux_agent" },
                               null, new[] { "metal_ingot" },
                               "mach_note_adv_flux", "两份矿石 +2，高级助熔剂保底 +1（代码不消耗）"),
                    // 两矿 +2，还需要品质加成 +1（100%）
                    new Recipe(new[] { "common_ore", "common_ore" },
                               null, new[] { "metal_ingot" },
                               "mach_note_uncapped", "两矿 +2，需品质加成 +100% → 极高", 100),
                    // 三矿 +2，爆燃 -1，助熔剂 +1 → +3；双锭
                    new Recipe(new[] { "common_ore", "common_ore", "common_ore", "flux_agent" },
                               new[] { "furnace_module_blast" },
                               new[] { "metal_ingot", "metal_ingot" },
                               "mach_note_blast_flux", "三矿 +2，爆燃 -1、助熔剂 +1 抵消；双锭"),
                    new Recipe(new[] { "common_ore", "common_ore", "common_ore", "advanced_flux_agent" },
                               new[] { "furnace_module_blast" },
                               new[] { "metal_ingot", "metal_ingot" },
                               "mach_note_adv_flux_blast", "三矿 +2，爆燃 -1、高级助熔剂 +1；双锭"),
                    // 三矿 +2，爆燃 -1，还需要品质加成 +2（200%）；双锭
                    new Recipe(new[] { "common_ore", "common_ore", "common_ore" },
                               new[] { "furnace_module_blast" },
                               new[] { "metal_ingot", "metal_ingot" },
                               "mach_note_blast_core2", "三矿 +2，爆燃 -1，需品质加成 +200% → 极高；双锭", 200)
                }
            },
            new RecipeGroup
            {
                TitleKey = "mach_furnace_high", TitleFallback = "高纯度金属锭",
                Purity = "INGOT_PURITY_HIGH",
                Variants = new[]
                {
                    // 两矿 → +2
                    new Recipe(new[] { "common_ore", "common_ore" },
                               null, new[] { "metal_ingot" },
                               "mach_note_ore2", "两份矿石 → 基础 +2 档"),
                    // 一矿一废金属 +1，助熔剂保底 +1 → +2。
                    // 助熔剂放在投入格里但**不算消耗个数**（消耗列表只取废金属 + 矿石的最后 2 个），
                    // 所以必须再配一份废金属，只有「一矿 + 助熔剂」时数量不够、熔炉不会产出。
                    new Recipe(new[] { "common_ore", "scrap_metal", "flux_agent" },
                               null, new[] { "metal_ingot" },
                               "mach_note_ore_flux", "一矿一废金属 +1，助熔剂保底 +1（会被消耗）"),
                    // 同上，高级助熔剂：效果一样，只是不被消耗
                    new Recipe(new[] { "common_ore", "scrap_metal", "advanced_flux_agent" },
                               null, new[] { "metal_ingot" },
                               "mach_note_ore_flux", "一矿一废金属 +1，高级助熔剂保底 +1（代码不消耗）"),
                    // 三矿 +2，爆燃 -1，还需要品质加成 +1（100%）；双锭
                    new Recipe(new[] { "common_ore", "common_ore", "common_ore" },
                               new[] { "furnace_module_blast" },
                               new[] { "metal_ingot", "metal_ingot" },
                               "mach_note_blast_core1", "三矿 +2，爆燃 -1，需品质加成 +100% → 高；双锭", 100)
                }
            },
            new RecipeGroup
            {
                TitleKey = "mach_furnace_fair", TitleFallback = "一般纯度金属锭",
                Purity = "INGOT_PURITY_FAIR",
                Variants = new[]
                {
                    // 一矿一废金属 → +1
                    new Recipe(new[] { "common_ore", "scrap_metal" },
                               null, new[] { "metal_ingot" },
                               "mach_note_ore1", "矿石与废金属各一份 → 基础 +1 档"),
                    // 两废金属 + 助熔剂保底 +1
                    new Recipe(new[] { "scrap_metal", "scrap_metal", "flux_agent" },
                               null, new[] { "metal_ingot" },
                               "mach_note_flux2", "废金属 + 助熔剂保底 +1（会被消耗）"),
                    // 两废金属 + 高级助熔剂：效果一样，只是不被消耗
                    new Recipe(new[] { "scrap_metal", "scrap_metal", "advanced_flux_agent" },
                               null, new[] { "metal_ingot" },
                               "mach_note_flux2", "废金属 + 高级助熔剂保底 +1（代码不消耗）"),
                    // 三矿 +2，爆燃 -1 → +1；双锭
                    new Recipe(new[] { "common_ore", "common_ore", "common_ore" },
                               new[] { "furnace_module_blast" },
                               new[] { "metal_ingot", "metal_ingot" },
                               "mach_note_blast", "三矿 +2，爆燃 -1 → 一般纯度；双锭")
                }
            },
            new RecipeGroup
            {
                TitleKey = "mach_furnace_low", TitleFallback = "低纯度金属锭",
                Purity = "INGOT_PURITY_LOW",
                Variants = new[]
                {
                    // 两废金属 → 0
                    new Recipe(new[] { "scrap_metal", "scrap_metal" },
                               null, new[] { "metal_ingot" },
                               "mach_note_scrap", "纯废金属 → 基础档"),
                    // 两矿一废金属：floor(4/3)=1，爆燃 -1 → 0；双锭
                    new Recipe(new[] { "common_ore", "common_ore", "scrap_metal" },
                               new[] { "furnace_module_blast" },
                               new[] { "metal_ingot", "metal_ingot" },
                               "mach_note_blast", "两矿一废金属 +1，爆燃 -1 → 基础档；双锭")
                }
            },
            new RecipeGroup
            {
                TitleKey = "mach_furnace_very_low", TitleFallback = "极低纯度金属锭",
                Purity = "INGOT_PURITY_VERY_LOW",
                Variants = new[]
                {
                    // 三废金属：0，爆燃 -1 → -1；双锭
                    new Recipe(new[] { "scrap_metal", "scrap_metal", "scrap_metal" },
                               new[] { "furnace_module_blast" },
                               new[] { "metal_ingot", "metal_ingot" },
                               "mach_note_blast_low", "纯废金属 + 爆燃 -1 档；双锭"),
                    // 一矿两废金属：floor(2/3)=0，爆燃 -1 → -1；双锭
                    new Recipe(new[] { "common_ore", "scrap_metal", "scrap_metal" },
                               new[] { "furnace_module_blast" },
                               new[] { "metal_ingot", "metal_ingot" },
                               "mach_note_blast_low", "一矿两废金属 + 爆燃 → 极低纯度；双锭")
                }
            },
            new RecipeGroup
            {
                TitleKey = "mach_furnace_junk", TitleFallback = "垃圾处理",
                Variants = new[]
                {
                    // 6 垃圾 → 4 废金属（不产锭）
                    new Recipe(new[] { "junk", "junk", "junk", "junk", "junk", "junk" },
                               new[] { "furnace_module_junk" },
                               new[] { "scrap_metal", "scrap_metal", "scrap_metal", "scrap_metal" },
                               "mach_note_junk", "装上垃圾模块后，6 个垃圾 → 4 份废金属")
                }
            }
        };

        /// <summary>
        /// 蒸发器：没有固定配方表——任何装了可蒸发液体的容器都能蒸。
        /// 依据 MachineEvaporator/&lt;Evaporator&gt;b__7（反编译）：
        ///   · 只有 <c>Liquid.isEvaporative</c> 的成分会被蒸出（当前是水 / 酒精 / 脂粘合剂）；
        ///   · 馏出量 = 蒸出量 × 质量加成% / 100，质量加成被钳在 0..100；
        ///   · 装了溶剂回收模块时只有酒精能进馏出瓶。
        /// 每个可蒸出的液体一页：机器页画原版「容器 + 馏出」两个槽，
        /// 投入容器里装该液体，馏出槽里装蒸出来的同一种液体（体积按容器容量装满，便于辨认）。
        /// </summary>
        private static readonly RecipeGroup[] EvaporatorGroups = BuildEvaporatorGroups();

        private static RecipeGroup[] BuildEvaporatorGroups()
        {
            var ids = EvaporativeLiquidIds();
            var groups = new RecipeGroup[ids.Count];

            for (int i = 0; i < ids.Count; i++)
            {
                string entry = LiquidCatalog.EntryPrefix + ids[i];

                groups[i] = new RecipeGroup
                {
                    // 标题直接用液体名（水 / 酒精 / 脂粘合剂）
                    TitleItemId = entry,
                    TitleKey = "mach_evap_output",
                    TitleFallback = "蒸馏产出",
                    Variants = new[]
                    {
                        // 输入 / 产出登记液体条目：液体页正反查都能看到蒸发器。
                        // 面板上没有输入 / 产物网格，这两个列表只参与反查匹配。
                        new Recipe(new[] { entry }, null, new[] { entry },
                                   null, null, 0, LiquidManager.ContainerId, ids[i])
                    }
                };
            }

            return groups;
        }

        /// <summary>能蒸出的液体 id：直接读游戏的 <c>Liquid.isEvaporative</c>，不维护名单。</summary>
        internal static List<string> EvaporativeLiquidIds()
        {
            var ids = new List<string>();

            try
            {
                var liquids = Liquid.Liquids;
                if (liquids == null) return ids;

                for (int i = 0; i < liquids.Count; i++)
                {
                    var liquid = liquids[i];
                    if (liquid == null || !liquid.isEvaporative) continue;
                    if (!string.IsNullOrEmpty(liquid.identifier)) ids.Add(liquid.identifier);
                }
            }
            catch { }

            return ids;
        }

        /// <summary>
        /// 捕鼠夹：每页左右两块捕鼠夹面板，中间箭头表示「周期结束后」。
        /// 左边是起始状态（有奶酪 / 没奶酪），右边是抓到之后（槽里一只鼠）。
        /// 依据 RatTrap.CreateMouseTrap 的槽过滤与 onCycleEndSlotItemFunc。
        /// 两个变体不写概率：只有加工奶酪能当诱饵，空夹子也会自己抓到。
        /// </summary>
        private static readonly RecipeGroup[] MouseTrapGroups =
        {
            new RecipeGroup
            {
                TitleKey = "mach_trap_output", TitleFallback = "捕鼠夹",
                Variants = new[]
                {
                    new Recipe(new[] { "processed_cheese" }, null, new[] { "rat" }, null, null),
                    new Recipe(null, null, new[] { "rat" }, null, null)
                }
            }
        };

        /// <summary>动物笼（animal_cage）：机器 id，同时用作页面分支的判定。</summary>
        internal const string AnimalCageId = "animal_cage";

        private static RecipeGroup[] _animalCageGroups;
        private static int _animalCageGeneration = -1;

        /// <summary>
        /// 动物笼：一个物种一条配方（一类算一页），笼内一雄一雌；
        /// 能放进笼子的东西不维护 id 清单，按标签扫目录：
        ///   · ANIMAL_ITEM_TAG —— 动物；同类一雄一雌 = 一条配方，一类动物一页；
        ///   · FEED_DISPENSER_TAG / AUTO_BUTCHER_TAG —— 能插两个槽的机器。
        /// 喂食器 / 自动屠宰各有多个候选时，在这一页内轮播（变体数取两者候选数的最大值，
        /// 第 v 条取 dispenser[v % M]、butcher[v % N]），每个槽互不牵连。
        /// 动物同时算产出（笼里繁殖同种动物），屠宰产物不写。
        /// 依据 AnimalCage.CreateCageWindow、HusbandryHelper.CheckMate/GiveBirth、
        /// MachineFeedDispenser.SetupHostSlot 与 MachineAutoButcher.SetupHostSlot。
        /// </summary>
        private static RecipeGroup[] AnimalCageRecipes()
        {
            if (_animalCageGroups != null && _animalCageGeneration == CatalogDataBuilder.Generation)
                return _animalCageGroups;

            var animals = new List<string>();
            var dispensers = new List<string>();
            var butchers = new List<string>();

            var items = CatalogDataBuilder.Visible;
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var entry = items[i];
                    var item = entry == null ? null : entry.Item;
                    if (item == null || string.IsNullOrEmpty(entry.Id)) continue;

                    if (SafeTag(item, "ANIMAL_ITEM_TAG")) animals.Add(entry.Id);
                    else if (SafeTag(item, "FEED_DISPENSER_TAG")) dispensers.Add(entry.Id);
                    else if (SafeTag(item, "AUTO_BUTCHER_TAG")) butchers.Add(entry.Id);
                }
            }

            // 目录还没建好时先不缓存，等下一次调用重试
            if (animals.Count == 0)
            {
                _animalCageGeneration = -1;
                return new RecipeGroup[0];
            }

            _animalCageGeneration = CatalogDataBuilder.Generation;

            // 喂食器 / 屠宰机各自独立轮播：变体数取两者候选数的最大值
            int variantCount = Math.Max(1, Math.Max(dispensers.Count, butchers.Count));

            var groups = new List<RecipeGroup>(animals.Count);
            for (int a = 0; a < animals.Count; a++)
            {
                string animal = animals[a];
                var variants = new Recipe[variantCount];

                for (int v = 0; v < variantCount; v++)
                {
                    string dispenser = dispensers.Count > 0 ? dispensers[v % dispensers.Count] : null;
                    string butcher = butchers.Count > 0 ? butchers[v % butchers.Count] : null;

                    // 投入：同类一雄一雌（填充时按性别各造一只）；
                    // 两个槽的机器也登记成投入，查它们时能反查到这一页
                    var inputs = new List<string> { animal, animal };
                    if (!string.IsNullOrEmpty(dispenser)) inputs.Add(dispenser);
                    if (!string.IsNullOrEmpty(butcher)) inputs.Add(butcher);

                    var recipe = new Recipe(inputs.ToArray(), null, new[] { animal }, null, null);
                    recipe.CageAnimal = animal;
                    recipe.CageDispenser = dispenser;
                    recipe.CageButcher = butcher;
                    variants[v] = recipe;
                }

                groups.Add(new RecipeGroup
                {
                    TitleItemId = animal,
                    TitleKey = "mach_cage_husbandry", TitleFallback = "饲养",
                    Variants = variants
                });
            }

            _animalCageGroups = groups.ToArray();
            return _animalCageGroups;
        }

        private static bool SafeTag(GameItem item, string tag)
        {
            try { return item.IsTag(tag); }
            catch { return false; }
        }

        /// <summary>
        /// 当前生效的熔炉配方组。手写表是精选展示；运行期探测发现矿石/废金属 id
        /// 改名或失效时，会把整表里的对应 id 换掉（见 DiscoverFurnaceRecipes）。
        /// </summary>
        private static RecipeGroup[] FurnaceGroups = HandFurnaceGroups;

        /// <summary>
        /// 搅拌机可搅拌的物品（手写清单）。游戏里由
        /// <c>ChemicalHelper.InitChemItem(item, isBlendable: true, …)</c> 打上 BLENDABLE_TAG
        /// （全程序集只有这一处设置），按钮回调
        /// <c>MachineBlender.&lt;Blender&gt;b__2</c> 只消耗带该标签的投入。
        /// 这里按工厂逐一反编译收录：
        ///   HydroponicDirectory：nutrifruit / dream_cap / meal_cap / nightmare_cap / coffee_bean
        ///   AmenitiesItemDirectory：water_tablet / nutrient_tablet
        ///   HusbandryDirectory：feces_small / feces_medium / feces_large
        ///   MedsItemDirectory：birth_control_pill / fanta_pill / zyanide_pill /
        ///                      caffeine_pill / dream_dust / nightmare_dust / pink_alt_injector
        /// 手写项优先（顺序固定、不被自动发现覆盖）；运行时会由
        /// <see cref="DiscoverBlenderInputs"/> 扫 BLENDABLE_TAG 补齐模组物品。
        /// （PreBuiltItemHelper 的 nutrifruit_l 等是 nutrifruit 的预构建变体，id 仍是本体，
        ///  PlantTraitList.Infuse 是运行时给任意物品灌成分，不产生新的固定条目。）
        /// </summary>
        private static readonly string[] BlenderInputs =
        {
            "nutrifruit",
            "dream_cap",
            "meal_cap",
            "nightmare_cap",
            "coffee_bean",
            "water_tablet",
            "nutrient_tablet",
            "feces_small",
            "feces_medium",
            "feces_large",
            "birth_control_pill",
            "fanta_pill",
            "zyanide_pill",
            "caffeine_pill",
            "dream_dust",
            "nightmare_dust",
            "pink_alt_injector"
        };

        /// <summary>当前生效的可搅拌 id（手写 + 自动发现）。</summary>
        private static string[] _blenderIds = BlenderInputs;

        /// <summary>自动发现所依据的目录 generation，没变就不重扫。</summary>
        private static int _blenderDiscoveryGeneration = -1;

        /// <summary>
        /// 搅拌机配方：一物品一页；有品质档的作物（营养果 / 梦帽菇 / 食用菇）每档各一页，
        /// 投入格与搅拌前成分都用该档实例，其余物品仍是一页。
        /// 容器用液体管理器的默认容器（bottled_water），模块/电池/控制不参与展示。
        /// </summary>
        private static RecipeGroup[] BuildBlenderGroups(string[] ids)
        {
            var groups = new List<RecipeGroup>(ids.Length);

            for (int i = 0; i < ids.Length; i++)
            {
                string id = ids[i];
                int tiers = CropVariants.Count(id);

                if (tiers <= 1)
                {
                    groups.Add(new RecipeGroup
                    {
                        TitleItemId = id,
                        TitleKey = "mach_blender_recipe",
                        TitleFallback = "可搅拌物品",
                        Variants = new[]
                        {
                            new Recipe(new[] { id }, null, null, null, null,
                                       0, LiquidManager.ContainerId)
                        }
                    });
                    continue;
                }

                for (int v = 0; v < tiers; v++)
                {
                    int tier = v;
                    var recipe = new Recipe(new[] { id }, null, null, null, null,
                                            0, LiquidManager.ContainerId);

                    // 投入格与「挤进容器的成分」都按这一档的实例来
                    recipe.InputItem = delegate(int index)
                    {
                        return index == 0 ? CropVariants.Create(id, tier) : null;
                    };

                    groups.Add(new RecipeGroup
                    {
                        TitleItemId = id,
                        TitleKey = "mach_blender_recipe",
                        TitleFallback = "可搅拌物品",
                        TitleSuffix = "（" + CropVariants.Label(id, v) + "）",
                        Variants = new[] { recipe }
                    });
                }
            }

            return groups.ToArray();
        }

        private static RecipeGroup[] BlenderGroups = BuildBlenderGroups(BlenderInputs);

        /// <summary>
        /// 化学制剂成型机：配方直接读游戏公开表 <c>ChemFinisherRecipe.chemFinisherRecipes</c>
        /// （梦尘 / 梦魇尘 / 咖啡因片 / 氰化物片 / 避孕药 / 芬太片 / 粉色注射剂）。
        /// 每条 = 容器里装入活性成分（requiredVolume，单位 1/1000 ml）后压制成型；
        /// 所有配方都要求容器干燥（isRequireDry）。
        /// 投入用液体条目（liquid:*），容器槽放灌好该液体的 bottled_water。
        /// </summary>
        private static RecipeGroup[] BuildChemFinisherGroups()
        {
            var groups = new List<RecipeGroup>();

            try
            {
                var list = ChemFinisherRecipe.chemFinisherRecipes;
                if (list == null) return groups.ToArray();

                for (int i = 0; i < list.Count; i++)
                {
                    var recipe = list[i];
                    if (recipe == null || string.IsNullOrEmpty(recipe.identifier)) continue;

                    string liquid = recipe.activeIngredientId;
                    string[] inputs = string.IsNullOrEmpty(liquid)
                        ? empty
                        : new[] { LiquidCatalog.EntryPrefix + liquid };

                    groups.Add(new RecipeGroup
                    {
                        TitleItemId = recipe.identifier,
                        TitleKey = "mach_chem_finisher_recipe",
                        TitleFallback = "化学制剂",
                        Variants = new[]
                        {
                            new Recipe(inputs, null, new[] { recipe.identifier },
                                       null, null, 0, LiquidManager.ContainerId,
                                       liquid, recipe.requiredVolume)
                        }
                    });
                }
            }
            catch (Exception e)
            {
                if (ModEntry.Log != null)
                    ModEntry.Log.Warning("[chem-finisher] recipes: " + e.Message);
            }

            return groups.ToArray();
        }

        private static readonly RecipeGroup[] ChemFinisherGroups = BuildChemFinisherGroups();

        /// <summary>
        /// 自动补齐可搅拌清单：扫描目录快照里每件物品的 BLENDABLE_TAG，
        /// 手写表里的 id 优先保留，自动发现只追加未知 id（供后加载的模组物品使用）。
        /// 目录 generation 没变时直接返回，重复调用无开销。
        /// 必须在数据建好之后调用（Warmup / 图鉴打开时已建表的路径）。
        /// </summary>
        internal static void DiscoverBlenderInputs()
        {
            if (CatalogDataBuilder.Generation == _blenderDiscoveryGeneration) return;
            _blenderDiscoveryGeneration = CatalogDataBuilder.Generation;

            var ids = new List<string>(BlenderInputs);
            var seen = new HashSet<string>(BlenderInputs, StringComparer.Ordinal);
            int handCount = BlenderInputs.Length;

            try
            {
                var items = CatalogDataBuilder.Visible;
                for (int i = 0; i < items.Count; i++)
                {
                    var entry = items[i];
                    if (entry == null || entry.Item == null) continue;
                    if (seen.Contains(entry.Id)) continue;

                    bool blendable = false;
                    try { blendable = entry.Item.IsTag("BLENDABLE_TAG"); }
                    catch { }

                    if (!blendable) continue;

                    ids.Add(entry.Id);
                    seen.Add(entry.Id);
                }
            }
            catch (Exception e)
            {
                if (ModEntry.Log != null)
                    ModEntry.Log.Warning("[blender] discover: " + e.Message);
            }

            if (SameIds(ids, _blenderIds)) return;

            _blenderIds = ids.ToArray();
            BlenderGroups = BuildBlenderGroups(_blenderIds);
            Recipes["blender"] = BlenderGroups;

            if (ModEntry.Log != null)
            {
                ModEntry.Log.Log("[blender] blendable=" + _blenderIds.Length
                                 + " hand=" + handCount
                                 + " auto=" + (_blenderIds.Length - handCount));
            }
        }

        private static bool SameIds(List<string> a, string[] b)
        {
            if (b == null || a.Count != b.Length) return false;
            for (int i = 0; i < b.Length; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        // ===================== 熔炉：运行期探测（适配更新 / 补丁） =====================

        private const string FurnaceOreId = "common_ore";
        private const string FurnaceScrapId = "scrap_metal";

        /// <summary>熔炉输入里不按普通材料参与档位计算的特殊物品。</summary>
        private static readonly string[] FurnaceSpecialInputs =
        {
            "flux_agent", "advanced_flux_agent", "junk"
        };

        private static int _furnaceDiscoveryGeneration = -1;
        private static string _furnaceOreId = FurnaceOreId;
        private static string _furnaceScrapId = FurnaceScrapId;

        /// <summary>
        /// 熔炉没有配方数据表：输入白名单和产出规则都写在
        /// <c>MachineFurnace.Furnace</c> 的 onCycleEndSlotItemFunc 闭包里。
        /// 这里用游戏公开入口做运行期探测：
        ///   · <c>MachineFurnace.CreateMachineInventoryWindow</c> 造一台临时熔炉，
        ///     用输入格 / 模块格的 mayInventoryAddItemFunc 实测当前版本（或补丁后）接受哪些物品；
        ///   · <c>IngotPurityHelper.SetPurityFromInput</c> + <c>ingotPurities</c> 判断
        ///     哪些输入按矿石计数，并校验档位覆盖；
        ///   · 手写的 common_ore / scrap_metal 若已不被接受，就替换成探测到的同类代表。
        /// 组合规则（爆燃 num=3、双锭、助熔剂保底、垃圾 6→4）仍来自手写表。
        /// </summary>
        internal static void DiscoverFurnaceRecipes()
        {
            if (CatalogDataBuilder.Generation == _furnaceDiscoveryGeneration) return;
            _furnaceDiscoveryGeneration = CatalogDataBuilder.Generation;

            var accepted = new HashSet<string>(StringComparer.Ordinal);
            var oreLike = new List<string>();
            var scrapLike = new List<string>();
            var modules = new HashSet<string>(StringComparer.Ordinal);

            PixelWindow probe = null;
            try
            {
                var tuple = MachineFurnace.CreateMachineInventoryWindow(8, 8, true, true, 5);
                probe = tuple.Item1;
                var moduleGrid = tuple.Item3;
                var inputGrid = tuple.Item4;

                var items = CatalogDataBuilder.Visible;
                for (int i = 0; i < items.Count; i++)
                {
                    var entry = items[i];
                    if (entry == null || entry.Item == null) continue;

                    // 原版过滤器第一步就是 IsItemOwned：探测实例统一标成已拥有
                    try { GeneralHelper.SetItemOwned(entry.Item, true); } catch { }

                    bool inputOk = false;
                    try
                    {
                        inputOk = inputGrid.mayInventoryAddItemFunc != null &&
                                  inputGrid.mayInventoryAddItemFunc(entry.Item, inputGrid);
                    }
                    catch { }

                    if (inputOk)
                    {
                        accepted.Add(entry.Id);

                        bool special = false;
                        for (int k = 0; k < FurnaceSpecialInputs.Length; k++)
                        {
                            if (string.Equals(entry.Id, FurnaceSpecialInputs[k], StringComparison.Ordinal))
                            {
                                special = true;
                                break;
                            }
                        }

                        if (!special)
                        {
                            if (IsOreLikeInput(entry.Item)) oreLike.Add(entry.Id);
                            else scrapLike.Add(entry.Id);
                        }
                    }

                    bool moduleOk = false;
                    try
                    {
                        moduleOk = moduleGrid.mayInventoryAddItemFunc != null &&
                                   moduleGrid.mayInventoryAddItemFunc(entry.Item, moduleGrid);
                    }
                    catch { }

                    if (moduleOk) modules.Add(entry.Id);
                }
            }
            catch (Exception e)
            {
                if (ModEntry.Log != null)
                    ModEntry.Log.Warning("[furnace] probe failed: " + e.Message);
            }
            finally
            {
                if (probe != null && probe.rectTransform != null)
                {
                    try { UnityEngine.Object.Destroy(probe.rectTransform.gameObject); } catch { }
                }
            }

            if (accepted.Count == 0 && modules.Count == 0)
            {
                if (ModEntry.Log != null)
                    ModEntry.Log.Warning("[furnace] probe returned nothing; keep hand table");
                return;
            }

            // 手写 id 仍被接受就继续用；否则换探测到的同类代表
            string ore = accepted.Contains(FurnaceOreId) ? FurnaceOreId : FirstOrNull(oreLike);
            string scrap = accepted.Contains(FurnaceScrapId) ? FurnaceScrapId : FirstOrNull(scrapLike);

            if (string.IsNullOrEmpty(ore))
            {
                ore = FurnaceOreId;
                if (ModEntry.Log != null) ModEntry.Log.Warning("[furnace] no ore-like input accepted");
            }

            if (string.IsNullOrEmpty(scrap))
            {
                scrap = FurnaceScrapId;
                if (ModEntry.Log != null) ModEntry.Log.Warning("[furnace] no scrap-like input accepted");
            }

            if (!string.Equals(ore, _furnaceOreId, StringComparison.Ordinal) ||
                !string.Equals(scrap, _furnaceScrapId, StringComparison.Ordinal))
            {
                _furnaceOreId = ore;
                _furnaceScrapId = scrap;
                FurnaceGroups = RebuildFurnaceGroups(ore, scrap);
                Recipes["furnace"] = FurnaceGroups;
            }

            if (ModEntry.Log == null) return;

            ModEntry.Log.Log("[furnace] accepted=" + accepted.Count
                             + " ore=" + _furnaceOreId
                             + " scrap=" + _furnaceScrapId
                             + " flux=" + accepted.Contains("flux_agent")
                             + " advFlux=" + accepted.Contains("advanced_flux_agent")
                             + " blast=" + modules.Contains("furnace_module_blast")
                             + " junkModule=" + modules.Contains("furnace_module_junk")
                             + " oreLike=" + oreLike.Count
                             + " scrapLike=" + scrapLike.Count);

            LogPurityCoverage();
        }

        private static string FirstOrNull(List<string> ids)
        {
            return ids != null && ids.Count > 0 ? ids[0] : null;
        }

        /// <summary>
        /// 用公开的 SetPurityFromInput 判断某个输入是否按“矿石”计数：
        /// 两份它应产出高于 LOW 档。已经不再按矿石计的旧 id 会被判成普通废料。
        /// </summary>
        private static bool IsOreLikeInput(GameItem sample)
        {
            GameItem ingot = null;
            try
            {
                ingot = CatalogWindow.CreatePreviewItem("metal_ingot");
                if (ingot == null) return false;

                // SetPurityFromInput 只按 identifier 计数，同一实例传两次即可
                var inputs = new List<GameItem> { sample, sample };
                IngotPurityHelper.SetPurityFromInput(ingot, inputs, 0);

                int level, low;
                if (!TryGetPurityLevel(IngotPurityHelper.GetPurity(ingot), out level)) return false;
                if (!TryGetPurityLevel("INGOT_PURITY_LOW", out low)) return false;

                return level > low;
            }
            catch { return false; }
            finally
            {
                if (ingot != null) { try { ingot.Destroy(); } catch { } }
            }
        }

        private static bool TryGetPurityLevel(string purity, out int level)
        {
            level = 0;

            var table = IngotPurityHelper.ingotPurities;
            if (table == null || string.IsNullOrEmpty(purity)) return false;

            IngotPurityHelper.IngotPurity info;
            if (!table.TryGetValue(purity, out info) || info == null) return false;

            level = info.PurityLevel;
            return true;
        }

        /// <summary>把整张手写表里的 common_ore / scrap_metal 换成探测到的代表 id。</summary>
        private static RecipeGroup[] RebuildFurnaceGroups(string oreId, string scrapId)
        {
            var groups = new RecipeGroup[HandFurnaceGroups.Length];
            for (int g = 0; g < HandFurnaceGroups.Length; g++)
            {
                var src = HandFurnaceGroups[g];
                if (src == null) continue;

                Recipe[] variants = null;
                if (src.Variants != null)
                {
                    variants = new Recipe[src.Variants.Length];
                    for (int v = 0; v < src.Variants.Length; v++)
                    {
                        var r = src.Variants[v];
                        if (r == null) continue;

                        variants[v] = new Recipe(
                            ReplaceFurnaceIds(r.Inputs, oreId, scrapId),
                            r.Modules, r.Outputs, r.NoteKey, r.NoteFallback,
                            r.RequiredQualityPercent, r.Container);
                    }
                }

                groups[g] = new RecipeGroup
                {
                    TitleKey = src.TitleKey,
                    TitleFallback = src.TitleFallback,
                    TitleItemId = src.TitleItemId,
                    Purity = src.Purity,
                    Variants = variants
                };
            }

            return groups;
        }

        private static string[] ReplaceFurnaceIds(string[] inputs, string oreId, string scrapId)
        {
            if (inputs == null || inputs.Length == 0) return empty;

            var result = new string[inputs.Length];
            for (int i = 0; i < inputs.Length; i++)
            {
                string id = inputs[i];
                if (string.Equals(id, FurnaceOreId, StringComparison.Ordinal)) result[i] = oreId;
                else if (string.Equals(id, FurnaceScrapId, StringComparison.Ordinal)) result[i] = scrapId;
                else result[i] = id;
            }

            return result;
        }

        /// <summary>档位覆盖自检：ingotPurities 里出现手写表没有的档位时写日志。</summary>
        private static void LogPurityCoverage()
        {
            if (ModEntry.Log == null) return;

            var table = IngotPurityHelper.ingotPurities;
            if (table == null) return;

            foreach (var pair in table)
            {
                bool covered = false;
                for (int g = 0; g < FurnaceGroups.Length; g++)
                {
                    var group = FurnaceGroups[g];
                    if (group != null &&
                        string.Equals(group.Purity, pair.Key, StringComparison.Ordinal))
                    {
                        covered = true;
                        break;
                    }
                }

                if (!covered)
                    ModEntry.Log.Warning("[furnace] purity not covered by recipe table: " + pair.Key);
            }
        }

        private static readonly Dictionary<string, RecipeGroup[]> Recipes =
            new Dictionary<string, RecipeGroup[]>(StringComparer.Ordinal)
            {
                { "furnace", FurnaceGroups },
                { "evaporator", EvaporatorGroups },
                { "mouse_trap", MouseTrapGroups },
                { "blender", BlenderGroups },
                { "chem_finisher", ChemFinisherGroups }
            };

        /// <summary>这台机器的静态产出配方组；没有静态数据时返回 null。</summary>
        internal static RecipeGroup[] RecipesFor(string machineId)
        {
            if (string.IsNullOrEmpty(machineId)) return null;

            // 水培机没有固定配方表：一个能种的种子一页，数据全部读种子标签，按目录代号缓存
            if (string.Equals(machineId, "hydroponic", StringComparison.Ordinal))
                return HydroponicRecipes();

            // DNA 编辑器同样没有固定配方：一种盘一页，写它的可能来源
            if (string.Equals(machineId, "plant_dna_manipulator", StringComparison.Ordinal))
                return DnaRecipes();

            // 解序列器：一种加密模块一页（芯片编码哪张卡，就解出哪张卡）
            if (string.Equals(machineId, "desequencer", StringComparison.Ordinal))
                return DesequencerRecipes();

            // 3D 打印机：一种打印芯片一页，数据全部从芯片标签读取
            if (string.Equals(machineId, "printer", StringComparison.Ordinal))
                return PrinterRecipes();

            // 动物笼：只有一页，能放进笼子的东西按标签扫目录（动物 / 喂食器 / 自动屠宰机）
            if (string.Equals(machineId, AnimalCageId, StringComparison.Ordinal))
                return AnimalCageRecipes();

            RecipeGroup[] groups;
            return Recipes.TryGetValue(machineId, out groups) ? groups : null;
        }

        /// <summary>所有机器配方（静态表 + 动态机器），给统一关系索引聚合用。</summary>
        internal static List<KeyValuePair<string, Recipe>> AllRecipes()
        {
            var list = new List<KeyValuePair<string, Recipe>>();
            var seen = new HashSet<Recipe>();

            foreach (var pair in Recipes)
                CollectRecipes(list, seen, pair.Key, pair.Value);

            for (int i = 0; i < DynamicMachineIds.Length; i++)
                CollectRecipes(list, seen, DynamicMachineIds[i], RecipesFor(DynamicMachineIds[i]));

            return list;
        }

        /// <summary>没有固定表、靠标签 / 目录动态生成的机器。</summary>
        private static readonly string[] DynamicMachineIds =
        {
            "hydroponic",
            "plant_dna_manipulator",
            "desequencer",
            "printer",
            AnimalCageId
        };

        private static void CollectRecipes(List<KeyValuePair<string, Recipe>> list,
                                           HashSet<Recipe> seen,
                                           string machineId,
                                           RecipeGroup[] groups)
        {
            if (string.IsNullOrEmpty(machineId) || groups == null) return;

            for (int g = 0; g < groups.Length; g++)
            {
                var group = groups[g];
                if (group == null || group.Variants == null) continue;

                for (int v = 0; v < group.Variants.Length; v++)
                {
                    var recipe = group.Variants[v];
                    if (recipe == null || !seen.Add(recipe)) continue;
                    list.Add(new KeyValuePair<string, Recipe>(machineId, recipe));
                }
            }
        }

        private static RecipeGroup[] _hydroponicGroups;
        private static int _hydroponicGeneration = -1;

        /// <summary>
        /// 水培机配方：一个种子一页。
        /// 数据不手写——<c>HydroponicHelper.InitSeedItem</c> 把 PlantDefinition 的
        /// 进度 / 产量 / 耗水 / 养分 / 矿物速率 / 污染物影响全写进种子标签，
        /// 这里直接用 HydroponicHelper 的读取接口反推，模组物品也能自动跟上。
        /// </summary>
        private static RecipeGroup[] HydroponicRecipes()
        {
            if (_hydroponicGroups != null && _hydroponicGeneration == CatalogDataBuilder.Generation)
                return _hydroponicGroups;

            _hydroponicGeneration = CatalogDataBuilder.Generation;

            var groups = new List<RecipeGroup>();
            var items = CatalogDataBuilder.Visible;

            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var entry = items[i];
                    var item = entry == null ? null : entry.Item;
                    if (item == null || string.IsNullOrEmpty(entry.Id)) continue;

                    bool seed;
                    try { seed = item.IsTag("SEED_ITEM_TAG"); }
                    catch { seed = false; }
                    if (!seed) continue;

                    var built = BuildHydroponicGroups(entry.Id, item);
                    if (built == null) continue;

                    // 养分等级是万分比（与 WaterHelper.GetContaminantPercentage 同口径：150 = 矿物占水量的 1.5%）；
                    // 矿物消耗是绝对量（1/1000 ml，机器用 RemoveContaminantFromContainer 按它扣）。
                    int percent = 0;
                    int mineralUse = 0;
                    try { percent = HydroponicHelper.GetNutrimentLevel(item); } catch { }
                    try { mineralUse = HydroponicHelper.GetMineralUseRate(item); } catch { }
                    if (percent < 0) percent = 0;
                    if (percent > 10000) percent = 10000;

                    for (int g = 0; g < built.Length; g++)
                    {
                        var recipe = built[g] != null && built[g].Variants != null && built[g].Variants.Length > 0
                            ? built[g].Variants[0] : null;
                        if (recipe == null) continue;
                        recipe.NutrientPercent = percent;
                        recipe.MineralUse = mineralUse;
                    }

                    groups.AddRange(built);
                }
            }

            return _hydroponicGroups = groups.ToArray();
        }

        private static RecipeGroup[] _dnaGroups;
        private static int _dnaGeneration = -1;

        /// <summary>
        /// DNA 编辑器：一种盘一页（页序 = <see cref="DnaManager"/> 的特质顺序）。
        /// 每页就是那条「提取 DNA」的配方：种子 + 空白盘 → 该特质的盘。
        /// 从盘反查来源时直接指向对应的这一页，不再另开「来源」页。
        /// </summary>
        private static RecipeGroup[] DnaRecipes()
        {
            if (_dnaGroups != null && _dnaGeneration == CatalogDataBuilder.Generation)
                return _dnaGroups;

            _dnaGeneration = CatalogDataBuilder.Generation;

            var groups = new List<RecipeGroup>();

            int count = DnaManager.Count;
            for (int i = 0; i < count; i++)
            {
                var trait = DnaManager.TraitAt(i);
                if (trait == null) continue;

                // 种子格放一个能提供该特质的作物；没有兼容作物就留空
                var sources = DnaManager.Sources(trait);
                string seedId = sources.Count > 0 ? sources[0] : null;

                var recipe = new Recipe(
                    string.IsNullOrEmpty(seedId) ? null : new[] { seedId },
                    null,
                    new[] { DnaManager.DiskId },
                    "mach_dna_extract", "从植株提取该特质，写入空白盘",
                    0, null, null, 0);

                int traitIndex = i;
                recipe.OutputItem = delegate(int outputIndex)
                {
                    return DnaManager.CreateDisk(DnaManager.TraitAt(traitIndex));
                };

                groups.Add(new RecipeGroup
                {
                    TitleKey = "mach_dna_recipe",
                    TitleFallback = trait.displayName + " DNA",
                    Variants = new[] { recipe }
                });
            }

            return _dnaGroups = groups.ToArray();
        }

        // ------------------------------------------------------------------ 解序列器
        //
        // MachineDesequencer 的规则（反编译 <Desequencer>b__5 / FinishDesequencer）：
        //   · 输入槽只收 ACCESS_CARD 类型的卡片，芯片槽只收带 DESEQUENCER_TARGET_ID_TAG 的加密模块；
        //   · 有电、输出槽为空时开始，进度时间由**芯片**决定（InitProgressSourceItem）；
        //   · 完成时把芯片里编码的目标 id 造出来放进输出槽，输入卡被销毁。
        // 六种加密模块固定编码六张钥匙卡（ModuleDirectory.CrypoModule* → <>c::<CrypoModule*>b__*）。

        private sealed class DesequencerUnit
        {
            internal string Chip;
            internal string Keycard;
            internal int Seconds;
        }

        private static readonly DesequencerUnit[] DesequencerUnits =
        {
            new DesequencerUnit { Chip = "crypto_module_cmd", Keycard = "cmd_keycard", Seconds = 150 },
            new DesequencerUnit { Chip = "crypto_module_sec", Keycard = "sec_keycard", Seconds = 125 },
            new DesequencerUnit { Chip = "crypto_module_med", Keycard = "med_keycard", Seconds = 100 },
            new DesequencerUnit { Chip = "crypto_module_eng", Keycard = "eng_keycard", Seconds = 100 },
            new DesequencerUnit { Chip = "crypto_module_sup", Keycard = "sup_keycard", Seconds = 75 },
            new DesequencerUnit { Chip = "crypto_module_ser", Keycard = "ser_keycard", Seconds = 75 }
        };

        private static RecipeGroup[] _desequencerGroups;

        /// <summary>解序列器：一种加密模块一页，输入一张空白权限卡，产出芯片编码的那张卡。</summary>
        private static RecipeGroup[] DesequencerRecipes()
        {
            if (_desequencerGroups != null) return _desequencerGroups;

            var groups = new List<RecipeGroup>(DesequencerUnits.Length);

            for (int i = 0; i < DesequencerUnits.Length; i++)
            {
                var unit = DesequencerUnits[i];
                if (string.IsNullOrEmpty(unit.Chip) || string.IsNullOrEmpty(unit.Keycard)) continue;

                var recipe = new Recipe(
                    new[] { "blank_keycard" },
                    null,
                    new[] { unit.Keycard },
                    "mach_desequencer_note", "周期 " + unit.Seconds + " 秒；需要供电，输入卡会被消耗",
                    0, null, null, 0, null, null, 0, unit.Chip);

                groups.Add(new RecipeGroup
                {
                    TitleItemId = unit.Chip,
                    TitleKey = "mach_desequencer_recipe",
                    TitleFallback = "解序列",
                    Variants = new[] { recipe }
                });
            }

            return _desequencerGroups = groups.ToArray();
        }

        // ------------------------------------------------------------------ 3D 打印机
        //
        // MachinePrinter 的规则（反编译 Printer<b__4 / FinishPrinting / MachineProgressHelper）：
        //   · 芯片槽只收带 PRINTER_CHIP_TAG 的芯片，芯片上写明「产物 + 数量 + 附加材料 + 周期」；
        //   · 输入网格按芯片的附加材料需求扣料（REQUIRED_ITEM_ID_i / REQUIRED_ITEM_COUNT_i）；
        //   · 产出网格放芯片编码的产物（PROGRESS_ITEM_TARGET_ITEM_ID_TAG / _COUNT_TAG）。
        // 所以配方完全从芯片上的标签读取，不手写清单，新芯片自动跟上。

        private static RecipeGroup[] _printerGroups;
        private static int _printerGeneration = -1;

        private static RecipeGroup[] PrinterRecipes()
        {
            if (_printerGroups != null && _printerGeneration == CatalogDataBuilder.Generation)
                return _printerGroups;

            _printerGeneration = CatalogDataBuilder.Generation;

            var groups = new List<RecipeGroup>();
            var items = CatalogDataBuilder.Visible;

            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var entry = items[i];
                    var chip = entry == null ? null : entry.Item;
                    if (chip == null || string.IsNullOrEmpty(entry.Id)) continue;

                    bool isChip = false;
                    try { isChip = chip.IsTag("PRINTER_CHIP_TAG"); }
                    catch { }
                    if (!isChip) continue;

                    string targetId = ReadStringTag(chip, "PROGRESS_ITEM_TARGET_ITEM_ID_TAG");
                    if (string.IsNullOrEmpty(targetId)) continue;

                    int targetCount = Math.Max(1, ReadIntTag(chip, "PROGRESS_ITEM_TARGET_ITEM_COUNT_TAG"));
                    int seconds = ReadIntTag(chip, "PROGRESS_ITEM_TARGET_TAG");

                    var inputs = new List<string>();
                    int types = ReadIntTag(chip, "REQUIRED_ITEM_TYPE_COUNT_TAG");
                    for (int r = 0; r < types && r < 8; r++)
                    {
                        string reqId = ReadStringTag(chip, "REQUIRED_ITEM_ID_" + r);
                        if (string.IsNullOrEmpty(reqId)) continue;

                        int count = Math.Max(1, ReadIntTag(chip, "REQUIRED_ITEM_COUNT_" + r));
                        for (int c = 0; c < count; c++) inputs.Add(reqId);
                    }

                    var outputs = new List<string>();
                    for (int c = 0; c < targetCount; c++) outputs.Add(targetId);

                    string note = seconds > 0
                        ? "周期 " + seconds + " 秒；需要供电"
                        : "需要供电";

                    var recipe = new Recipe(
                        inputs.ToArray(), null, outputs.ToArray(),
                        "mach_printer_note", note,
                        0, null, null, 0, null, null, 0, entry.Id);

                    groups.Add(new RecipeGroup
                    {
                        TitleItemId = entry.Id,
                        TitleKey = "mach_printer_recipe",
                        TitleFallback = "3D 打印",
                        Variants = new[] { recipe }
                    });
                }
            }

            return _printerGroups = groups.ToArray();
        }

        /// <summary>读 int 标签；标签不存在或读失败返回 0。</summary>
        private static int ReadIntTag(GameItem item, string tag)
        {
            if (item == null || string.IsNullOrEmpty(tag)) return 0;

            try
            {
                var state = item.GetTagReadonly(tag);
                return state == null ? 0 : state.GetInt();
            }
            catch { return 0; }
        }

        /// <summary>读 string 标签；读不到返回 null。</summary>
        private static string ReadStringTag(GameItem item, string tag)
        {
            if (item == null || string.IsNullOrEmpty(tag)) return null;

            try
            {
                var state = item.GetTagReadonly(tag);
                string value = state == null ? null : state.GetString();
                return string.IsNullOrEmpty(value) ? null : value;
            }
            catch { return null; }
        }

        /// <summary>水培的一个配方变体：模块（可空）+ 产物 + 说明 + 概率产物下标 + 每周期耗水。</summary>
        private sealed class HydroVariant
        {
            internal string Module;
            internal string[] Outputs;
            internal string Note;
            internal int[] Uncertain;
            internal string UncertainNote;
            internal int WaterUse;
        }

        private static HydroVariant HV(string module, string[] outputs, string note,
                                       int[] uncertain = null, string uncertainNote = null,
                                       int waterUse = 0)
        {
            return new HydroVariant
            {
                Module = module,
                Outputs = outputs,
                Note = note,
                Uncertain = uncertain,
                UncertainNote = uncertainNote,
                WaterUse = waterUse
            };
        }

        /// <summary>
        /// 一个种子一页。图鉴统一按**满健康（最大产量 / 最好品质）**写：
        /// 产量不打折、尺寸全按大果，概率产物照最大产量列出（黄底 + 提示框写概率）。
        /// 收成规则没有运行时接口（写在 `PlantList.Harvest*` 的回调 IL 里），按种子 id 静态收录；
        /// 改变产物的模块各出一条变体。未知种子（模组作物）退回通用规则。
        /// </summary>
        private static RecipeGroup[] BuildHydroponicGroups(string seedId, GameItem seed)
        {
            string env = HydroEnvNote(seed);

            switch (seedId)
            {
                // 营养果：固定返 3 颗种子
                case "nutrifruit_seed":
                    return H(seedId, env,
                        HV(null, HydroOut("nutrifruit", 5, "nutrifruit_seed", 3),
                           "满健康：5 个大果 + 3 颗种子", null, null, 100),
                        HV("farm_module_nutrifruit",
                           HydroOut("nutrifruit", 6, "nutrifruit_seed", 3),
                           "每装 1 个营养果模块 +1 个果实", null, null, 100));

                // 棉花：最多返 2 颗种子（第二颗 50%）
                case "kotton_seed":
                    return H(seedId, env,
                        HV(null, HydroOut("kotton_fiber", 9, "kotton_seed", 2),
                           "满健康：9 个大果 + 返种", new[] { 10 }, null, 176),
                        HV("farm_module_kotton_loom",
                           HydroOut("kotton_fabric", 9, "kotton_seed", 2),
                           "织机模块直接出布料", new[] { 10 }, null, 176));

                // 梦帽菇：返 1 颗种子。装了梦帽模块后收成变成**食用帽菇**（CreateDreamCap(true)）
                case "dream_cap_seed":
                    return H(seedId, env,
                        HV(null, HydroOut("dream_cap", 8, "dream_cap_seed", 1),
                           "满健康：8 个大果 + 1 颗种子", null, null, 100),
                        HV("farm_module_dream_cap",
                           HydroOut("meal_cap", 10, "dream_cap_seed", 1),
                           "每装 1 个梦帽模块 +2 个，并变成食用帽菇", null, null, 100),
                        HV("farm_module_nightmare_cap",
                           HydroOut("nightmare_cap", 8, "dream_cap_seed", 1),
                           "改装梦魇模块后整株改出梦魇菇", null, null, 100));

                // 咖啡豆：不返种
                case "coffee_bean":
                    return H(seedId, env,
                        HV(null, HydroOut("coffee_bean", 8, null, 0),
                           "满健康：8 颗", null, null, 100));

                // 杂草：最多返 2 颗种子（第二颗 50%）
                case "fillerweed_seed":
                    return H(seedId, env,
                        HV(null, HydroOut("fillerweed", 4, "fillerweed_seed", 2),
                           "满健康：4 个 + 返种", new[] { 5 }, null, 287),
                        HV("farm_module_fillerweed_production",
                           HydroOut("fillerweed", 8, null, 0),
                           "生产模块 +4 个，不返种", null, null, 287));

                // 水芦：无主产物；返种最多 2 颗（第二颗 50%）
                case "hydroreed_seed":
                    return H(seedId, env,
                        HV(null, HydroOut(null, 0, "hydroreed_seed", 2),
                           "只返种；滤芯要靠过滤模块", new[] { 1 }, null, 20),
                        HV("farm_module_hydroreed_filter",
                           HydroOut("hydroreed_filter", 1, "hydroreed_seed", 2),
                           "每个过滤模块 +1 个滤芯", new[] { 2 }, null, 20));

                // 花莓：收成是酿酒浆果
                case "bloomberry_seed":
                    return H(seedId, env,
                        HV(null, HydroOut("wine_berry", 8, null, 0),
                           "满健康：8 颗", new[] { 0 },
                           "1 级 60% / 2 级 40%（满健康）", 100));
            }

            return GenericHydroponicGroups(seedId, seed, env);
        }

        /// <summary>
        /// 每个变体**各占一页**（不在同一页轮播）：无模块一页、每个改产物的模块一页。
        /// 水槽统一放一只装了水的容器。
        /// </summary>
        private static RecipeGroup[] H(string seedId, string env, params HydroVariant[] variants)
        {
            var groups = new List<RecipeGroup>(variants.Length);

            for (int i = 0; i < variants.Length; i++)
            {
                var v = variants[i];

                // 最下面那行只写生长条件（养分 / 污染物影响），不写产物数量和种类：
                // 产物已经逐格画在面板里了，再写一遍只是噪音。
                string note = env;
                // 概率说明统一写「生成概率：X%」；必定产出的物品不带这句（见 VanillaMachinePanel 的按元素标注）
                string uncertainNote = v.Uncertain != null && v.Uncertain.Length > 0
                    ? ModEntry.T("mach_hydro_chance",
                                 string.IsNullOrEmpty(v.UncertainNote) ? "50%" : v.UncertainNote)
                    : null;

                var recipe = new Recipe(new[] { seedId },
                                        string.IsNullOrEmpty(v.Module) ? null : new[] { v.Module },
                                        v.Outputs,
                                        "mach_hydroponic_note", note,
                                        0, LiquidManager.ContainerId, LiquidManager.WaterId, 0,
                                        v.Uncertain, uncertainNote, v.WaterUse);

                // 作物产物按最佳品质（大果）显示：与「图鉴分格列出的最佳那一格」完全一致
                recipe.OutputItem = BestQualityOutputs(v.Outputs);

                groups.Add(new RecipeGroup
                {
                    TitleItemId = seedId,
                    TitleKey = "mach_hydroponic_recipe",
                    TitleFallback = "可种植作物",
                    Variants = new[] { recipe }
                });
            }

            return groups.ToArray();
        }

        /// <summary>
        /// 产物样本工厂：作物按最佳品质档（大果）造实例，其余产物返回 null（按 id 造普通实例）。
        /// </summary>
        private static Func<int, GameItem> BestQualityOutputs(string[] outputs)
        {
            return delegate (int index)
            {
                if (outputs == null || index < 0 || index >= outputs.Length) return null;

                string id = outputs[index];
                int best = CropVariants.BestIndex(id);
                return best < 0 ? null : CropVariants.Create(id, best);
            };
        }

        /// <summary>产物列表：主产物 × count + 返种 × seeds。</summary>
        private static string[] HydroOut(string harvest, int count, string seedBack, int seeds)
        {
            var list = new List<string>(count + seeds);

            for (int i = 0; i < count && !string.IsNullOrEmpty(harvest); i++) list.Add(harvest);
            for (int i = 0; i < seeds && !string.IsNullOrEmpty(seedBack); i++) list.Add(seedBack);

            return list.ToArray();
        }

        /// <summary>生长条件（耗水 / 养分 / 矿物 / 污染物影响），追加在每条变体说明后面。</summary>
        private static string HydroEnvNote(GameItem seed)
        {
            int waterUse = 0, level = 0, range = 0, mineral = 0;
            double contaminant = 1.0;

            try { waterUse = HydroponicHelper.GetWaterUsePerCycle(seed); } catch { }
            try { level = HydroponicHelper.GetNutrimentLevel(seed); } catch { }
            try { range = HydroponicHelper.GetNutrimentRange(seed); } catch { }
            try { mineral = HydroponicHelper.GetMineralUseRate(seed); } catch { }
            try { contaminant = HydroponicHelper.GetContaminantEffect(seed); } catch { contaminant = 1.0; }

            // 耗水 / 矿物消耗由面板下面的成分变化体现，这里只写矿物质浓度与污染物影响。
            // 矿物质名用液体管理器里的本地化名（和成分列表、提示框是同一个来源）；
            // 浓度是万分比，展示成百分比（150 → 1.5%）。
            string best = (level / 100.0).ToString("0.##") + "%";
            string span = (range / 100.0).ToString("0.##") + "%";
            return LiquidManager.NameOf("mineral") + " " + best + "±" + span
                   + " · 污染物影响 ×" + contaminant.ToString("0.##");
        }

        /// <summary>未知种子（模组作物）：按标签里的收成产物 × 数量兜底。</summary>
        private static RecipeGroup[] GenericHydroponicGroups(string seedId, GameItem seed, string env)
        {
            string harvest = null;
            int quantity = 1;

            try { harvest = HydroponicHelper.GetHarvestItemId(seed); } catch { }
            if (string.IsNullOrEmpty(harvest)) return null;

            try { quantity = HydroponicHelper.GetBaseHarvestQuantity(seed); } catch { }
            if (quantity <= 0) quantity = 1;

            return H(seedId, env,
                HV(null, HydroOut(harvest, quantity, null, 0), "收 " + quantity + " 个"));
        }
    }
}
