using System;
using System.Collections.Generic;

using ItemCatalog.Catalog;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 机器界面的静态还原数据。
    ///
    /// 游戏里每台机器的窗口由 <c>CreateMachineInventoryWindow</c> 用同一套积木拼装
    /// （反编译 21 个机器类确认，面板顺序就是玩家看到的从上到下）：
    ///
    ///   MachineHelper.PowerPanel(slot)        → 电池槽
    ///   MachineHelper.Panel(label, slot, …)   → 带标签的功能槽（模块 / 输入 / 产出 …）
    ///   new GameGridInventory(shape, …)       → 容器网格，形状是一串 0/1 码
    ///   MachineHelper.NotePanel(slot)         → 纸条槽
    ///   MachineHelper.CreateMachineryWindow() → 拼成 PixelWindow
    ///
    /// 这里只还原**结构**（有哪些槽、格子形状），不还原运行状态（运转进度、耗电）。
    /// 标签用游戏自己的 mech_*_label_* 键，本地化自动跟随游戏语言。
    /// </summary>
    internal static class MachinePanels
    {
        /// <summary>面板种类。</summary>
        internal const char Battery = 'B';   // 电池槽（PowerPanel）
        internal const char Module = 'M';    // 模块槽
        internal const char Note = 'N';      // 纸条槽（NotePanel）
        internal const char Slot = 'S';      // 带标签的单格功能槽
        internal const char Grid = 'G';      // 容器网格（形状码）

        /// <summary>容器网格形状：7 列 × 5 行，'1' = 有格子。所有带网格的机器共用这一个。</summary>
        internal const string ContainerShape = "10000010001000001110000010001000001";
        internal const int ShapeCols = 7;

        /// <summary>
        /// 面板在配方里的角色：画一条配方时，物品按角色放进对应格子。
        /// Input=投料格、Module=模块格、Output=产物格、Passive=不参与配方
        /// （电池、纸条这类只还原界面结构）。
        /// </summary>
        internal const char RoleInput = 'I';
        internal const char RoleModule = 'M';
        internal const char RoleOutput = 'O';
        internal const char RolePassive = 'P';

        internal sealed class Panel
        {
            internal char Kind;
            internal string GameLabelKey;   // 游戏 mech_*_label_* 键；电池 / 模块 / 纸条用兜底文案
            internal string Fallback;       // 游戏键取不到时的兜底（中文）
            internal char Role;             // 配方物品放进哪类格子（默认 Passive）

            internal Panel(char kind, string gameLabelKey, string fallback, char role = RolePassive)
            {
                Kind = kind;
                GameLabelKey = gameLabelKey;
                Fallback = fallback;
                Role = role;
            }
        }

        /// <summary>机器物品 id → 面板序列（从各机器类 IL 抄的，顺序即游戏窗口顺序）。</summary>
        private static readonly Dictionary<string, Panel[]> Panels =
            new Dictionary<string, Panel[]>(StringComparer.Ordinal)
            {
                { "mouse_trap", new[] {
                    // RatTrap::CreateMouseTrapWindow：只有一个「trap」槽
                    PR(Slot, "mech_mouse_trap_label_trap", "捕鼠槽", RoleInput) } },
                { "furnace", new[] {
                    PR(Grid, null, "容器网格", RoleInput),
                    P(Battery, null, "电池"),
                    PR(Module, "mech_furnace_label_module", "模块", RoleModule),
                    PR(Slot, "mech_furnace_label_input", "投入", RoleInput),
                    PR(Slot, "mech_furnace_label_output", "产出", RoleOutput),
                    P(Note, null, "纸条") } },
                { "moisture_farm", new[] {
                    P(Grid, null, "容器网格"),
                    P(Battery, null, "电池"),
                    P(Module, "mech_moisture_farm_label_module", "模块"),
                    P(Slot, "mech_moisture_farm_label_container", "容器"),
                    P(Note, null, "纸条") } },
                { "purifier", new[] {
                    P(Grid, null, "容器网格"),
                    P(Battery, null, "电池"),
                    P(Module, "mech_purifier_label_module", "模块"),
                    P(Slot, "mech_purifier_label_container", "容器"),
                    P(Note, null, "纸条") } },
                { "projector", new[] {
                    P(Grid, null, "容器网格"),
                    P(Battery, null, "电池"),
                    P(Module, "mech_projector_label_module", "模块"),
                    P(Note, null, "纸条") } },
                { "printer", new[] {
                    P(Grid, null, "容器网格"),
                    P(Battery, null, "电池"),
                    P(Module, "mech_printer_label_module", "模块"),
                    P(Slot, "mech_printer_label_input", "输入"),
                    P(Slot, "mech_printer_label_chip", "芯片"),
                    P(Slot, "mech_printer_label_output", "产出"),
                    P(Note, null, "纸条") } },
                { "hydroponic", new[] {
                    P(Grid, null, "容器网格"),
                    P(Battery, null, "电池"),
                    P(Module, "mech_hydroponic_label_module", "模块"),
                    P(Slot, "mech_hydroponic_label_water", "水"),
                    P(Slot, "mech_hydroponic_label_seed", "种子"),
                    P(Slot, "mech_hydroponic_label_output", "产出"),
                    P(Note, null, "纸条") } },
                { "heating_plate", new[] {
                    P(Battery, null, "电池"),
                    P(Module, "mech_heating_plate_label_module", "模块"),
                    P(Slot, "mech_heating_plate_label_container", "容器"),
                    P(Slot, "mech_heating_plate_label_temp", "温度"),
                    P(Slot, "mech_heating_plate_label_control", "控制") } },
                { "wine_rack", new[] {
                    P(Grid, null, "容器网格"),
                    P(Battery, null, "电池"),
                    P(Module, "mech_purifier_label_module", "模块"),
                    P(Slot, "mech_evaporator_label_container", "容器"),
                    P(Note, null, "纸条") } },
                { "recharger", new[] {
                    P(Battery, null, "电池"),
                    P(Slot, "mech_recharger_label_battery", "电池组") } },
                { "evaporator", new[] {
                    P(Battery, null, "电池"),
                    PR(Module, "mech_evaporator_label_module", "模块", RoleModule),
                    PR(Slot, "mech_evaporator_label_container", "容器", RoleInput),
                    PR(Slot, "mech_evaporator_label_distillate", "馏出", RoleOutput),
                    P(Note, null, "纸条") } },
                { "dna_manipulator", new[] {
                    P(Grid, null, "容器网格"),
                    P(Battery, null, "电池"),
                    P(Module, null, "模块"),
                    P(Slot, null, "输入磁盘"),
                    P(Slot, null, "种子"),
                    P(Slot, null, "输出磁盘"),
                    P(Note, null, "纸条"),
                    P(Slot, null, "弹出") } },
                { "desequencer", new[] {
                    P(Grid, null, "容器网格"),
                    P(Battery, null, "电池"),
                    P(Module, "mech_desequencer_label_module", "模块"),
                    P(Slot, "mech_desequencer_label_chip", "芯片"),
                    P(Slot, "mech_desequencer_label_input", "输入"),
                    P(Slot, "mech_desequencer_label_output", "产出") } },
                { "deal_maker", new[] {
                    P(Grid, null, "容器网格"),
                    P(Battery, null, "电池"),
                    P(Module, null, "模块"),
                    P(Slot, null, "控制"),
                    P(Note, null, "纸条") } },
                { "chem_finisher", new[] {
                    P(Battery, null, "电池"),
                    P(Module, "mech_chem_finisher_label_module", "模块"),
                    P(Slot, "mech_chem_finisher_label_container", "容器"),
                    P(Slot, "mech_chem_finisher_label_output", "产出"),
                    P(Slot, "mech_chem_finisher_label_control", "控制") } },
                { "cassette_player", new[] {
                    P(Slot, "mech_cassette_player_label_storage", "磁带仓"),
                    P(Slot, "mech_cassette_player_label_cassette", "磁带"),
                    P(Slot, "mech_cassette_player_label_controls", "控制"),
                    P(Slot, "mech_cassette_player_label_volume", "音量") } },
                { "dispenser", new[] {
                    P(Battery, null, "电池"),
                    P(Slot, null, "产出"),
                    P(Slot, null, "控制") } },
                { "bottle_printer", new[] {
                    P(Battery, null, "电池"),
                    P(Slot, "mech_bottle_printer_label_output", "产出"),
                    P(Slot, "mech_bottle_printer_label_control", "控制") } },
                { "blender", new[] {
                    P(Battery, null, "电池"),
                    P(Module, "mech_blender_label_module", "模块"),
                    P(Slot, "mech_blender_label_input", "输入"),
                    P(Slot, "mech_blender_label_container", "容器"),
                    P(Slot, "mech_blender_label_control", "控制") } },
                { "alarm", new[] {
                    P(Grid, null, "容器网格"),
                    P(Battery, null, "电池"),
                    P(Module, "mech_alarm_label_module", "模块"),
                    P(Note, null, "纸条") } },
                { "vending_machine", new[] {
                    P(Battery, null, "电池"),
                    P(Slot, "mech_vending_label_inventory", "货道"),
                    P(Slot, "mech_vending_label_control", "控制"),
                    P(Slot, "mech_vending_label_core", "核心") } },
                { "vending_fountain", new[] {
                    P(Battery, null, "电池"),
                    P(Slot, "mech_fountain_label_basewater", "基础水"),
                    P(Slot, "mech_fountain_label_hq_water", "优质水"),
                    P(Slot, "mech_fountain_label_pure_water", "纯水"),
                    P(Slot, "mech_fountain_label_control", "控制"),
                    P(Slot, "mech_fountain_label_core", "核心") } }
            };

        private static Panel P(char kind, string gameLabelKey, string fallback)
        {
            return new Panel(kind, gameLabelKey, fallback);
        }

        private static Panel PR(char kind, string gameLabelKey, string fallback, char role)
        {
            return new Panel(kind, gameLabelKey, fallback, role);
        }

        /// <summary>这台机器的面板序列；不是已知机器（或没有界面数据）返回 null。</summary>
        internal static Panel[] For(string machineId)
        {
            if (string.IsNullOrEmpty(machineId)) return null;

            Panel[] panels;
            return Panels.TryGetValue(machineId, out panels) ? panels : null;
        }

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
            /// 达成这一条配方还需要的模块品质加成（百分比）。
            /// 熔炉页顶部显示「需品质加成 +X%」，和下面的材料 / 助熔剂一起轮播。
            /// </summary>
            internal int RequiredQualityPercent;

            internal Recipe(string[] inputs, string[] modules, string[] outputs,
                            string noteKey, string noteFallback,
                            int requiredQualityPercent = 0, string container = null)
            {
                Inputs = inputs ?? empty;
                Modules = modules ?? empty;
                Outputs = outputs ?? empty;
                NoteKey = noteKey;
                NoteFallback = noteFallback;
                RequiredQualityPercent = requiredQualityPercent;
                Container = container;
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
                    // 一矿 +1，助熔剂保底 +1 → +2
                    new Recipe(new[] { "common_ore", "flux_agent" },
                               null, new[] { "metal_ingot" },
                               "mach_note_ore_flux", "一矿 +1，助熔剂保底 +1（会被消耗）"),
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
        /// 蒸发器：把容器里的液体蒸干，产出溶剂回收模块 + 酒精。
        /// 依据 MachineEvaporator/<>c__DisplayClass0_0::&lt;Evaporator&gt;b__7
        /// （AddLiquid("alcohol") + 生成 evaporator_module_solvent_recovery）。
        /// 投料是装了酒的液体容器（游戏按 LIQUID_CONTAINER_TAG 收），
        /// 具体哪个容器由玩家决定，所以这里用标签格之外的「酒类容器」概括。
        /// </summary>
        private static readonly RecipeGroup[] EvaporatorGroups =
        {
            new RecipeGroup
            {
                TitleKey = "mach_evap_output", TitleFallback = "蒸馏产出",
                Variants = new[]
                {
                    new Recipe(new[] { "alcohol" },
                               new[] { "evaporator_module_solvent_recovery" },
                               new[] { "evaporator_module_solvent_recovery", "alcohol" },
                               "mach_note_evap", "把含酒精的容器蒸干：留下溶剂回收模块，馏出酒精"),
                    new Recipe(new[] { "red_beer" },
                               null,
                               new[] { "evaporator_module_solvent_recovery", "alcohol" },
                               "mach_note_evap_beer", "任何含酒精的液体容器都能蒸出酒精")
                }
            }
        };

        /// <summary>
        /// 捕鼠夹：放食物后周期产出加工奶酪（并捕鼠，老鼠是活体不是配方产物）。
        /// 依据 RatTrap/<>c__DisplayClass1_0::&lt;CreateMouseTrap&gt;b__3。
        /// </summary>
        private static readonly RecipeGroup[] MouseTrapGroups =
        {
            new RecipeGroup
            {
                TitleKey = "mach_trap_output", TitleFallback = "捕鼠夹产出",
                Variants = new[]
                {
                    new Recipe(new[] { "cheese" },
                               null,
                               new[] { "processed_cheese" },
                               "mach_note_trap", "放上奶酪作诱饵，周期结束后得到加工奶酪"),
                    new Recipe(new[] { "FOOD" },
                               null,
                               new[] { "processed_cheese" },
                               "mach_note_trap_food", "任何食物都能当诱饵")
                }
            }
        };

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
        /// 搅拌机配方：一物品一页。配方面板只还原输入与容器两个槽——
        /// 容器用液体管理器的默认容器（bottled_water），模块/电池/控制不参与展示。
        /// </summary>
        private static RecipeGroup[] BuildBlenderGroups(string[] ids)
        {
            var groups = new RecipeGroup[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                string id = ids[i];
                groups[i] = new RecipeGroup
                {
                    TitleItemId = id,
                    TitleKey = "mach_blender_recipe",
                    TitleFallback = "可搅拌物品",
                    Variants = new[]
                    {
                        new Recipe(new[] { id }, null, null, null, null,
                                   0, LiquidManager.ContainerId)
                    }
                };
            }

            return groups;
        }

        private static RecipeGroup[] BlenderGroups = BuildBlenderGroups(BlenderInputs);

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
                { "blender", BlenderGroups }
            };

        /// <summary>这台机器的静态产出配方组；没有静态数据时返回 null。</summary>
        internal static RecipeGroup[] RecipesFor(string machineId)
        {
            if (string.IsNullOrEmpty(machineId)) return null;

            RecipeGroup[] groups;
            return Recipes.TryGetValue(machineId, out groups) ? groups : null;
        }
    }
}
