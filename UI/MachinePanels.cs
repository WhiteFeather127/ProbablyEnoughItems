using System;
using System.Collections.Generic;

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
                    PR(Module, "mech_furnace_label_module", "模块", RoleModule),
                    PR(Slot, "mech_furnace_label_input", "投入", RoleInput),
                    PR(Slot, "mech_furnace_label_output", "产出", RoleOutput),
                    P(Note, null, "纸条") } },
                { "moisture_farm", new[] {
                    P(Grid, null, "容器网格"),
                    P(Module, "mech_moisture_farm_label_module", "模块"),
                    P(Slot, "mech_moisture_farm_label_container", "容器"),
                    P(Note, null, "纸条") } },
                { "purifier", new[] {
                    P(Grid, null, "容器网格"),
                    P(Module, "mech_purifier_label_module", "模块"),
                    P(Slot, "mech_purifier_label_container", "容器"),
                    P(Note, null, "纸条") } },
                { "projector", new[] {
                    P(Grid, null, "容器网格"),
                    P(Module, "mech_projector_label_module", "模块"),
                    P(Note, null, "纸条") } },
                { "printer", new[] {
                    P(Grid, null, "容器网格"),
                    P(Module, "mech_printer_label_module", "模块"),
                    P(Slot, "mech_printer_label_input", "输入"),
                    P(Slot, "mech_printer_label_chip", "芯片"),
                    P(Slot, "mech_printer_label_output", "产出"),
                    P(Note, null, "纸条") } },
                { "hydroponic", new[] {
                    P(Grid, null, "容器网格"),
                    P(Module, "mech_hydroponic_label_module", "模块"),
                    P(Slot, "mech_hydroponic_label_water", "水"),
                    P(Slot, "mech_hydroponic_label_seed", "种子"),
                    P(Slot, "mech_hydroponic_label_output", "产出"),
                    P(Note, null, "纸条") } },
                { "heating_plate", new[] {
                    P(Module, "mech_heating_plate_label_module", "模块"),
                    P(Slot, "mech_heating_plate_label_container", "容器"),
                    P(Slot, "mech_heating_plate_label_temp", "温度"),
                    P(Slot, "mech_heating_plate_label_control", "控制") } },
                { "wine_rack", new[] {
                    P(Grid, null, "容器网格"),
                    P(Module, "mech_purifier_label_module", "模块"),
                    P(Slot, "mech_evaporator_label_container", "容器"),
                    P(Note, null, "纸条") } },
                { "recharger", new[] {
                    P(Slot, "mech_recharger_label_battery", "电池组") } },
                { "evaporator", new[] {
                    PR(Module, "mech_evaporator_label_module", "模块", RoleModule),
                    PR(Slot, "mech_evaporator_label_container", "容器", RoleInput),
                    PR(Slot, "mech_evaporator_label_distillate", "馏出", RoleOutput),
                    P(Note, null, "纸条") } },
                { "dna_manipulator", new[] {
                    P(Grid, null, "容器网格"),
                    P(Module, null, "模块"),
                    P(Slot, null, "输入磁盘"),
                    P(Slot, null, "种子"),
                    P(Slot, null, "输出磁盘"),
                    P(Note, null, "纸条"),
                    P(Slot, null, "弹出") } },
                { "desequencer", new[] {
                    P(Grid, null, "容器网格"),
                    P(Module, "mech_desequencer_label_module", "模块"),
                    P(Slot, "mech_desequencer_label_chip", "芯片"),
                    P(Slot, "mech_desequencer_label_input", "输入"),
                    P(Slot, "mech_desequencer_label_output", "产出") } },
                { "deal_maker", new[] {
                    P(Grid, null, "容器网格"),
                    P(Module, null, "模块"),
                    P(Slot, null, "控制"),
                    P(Note, null, "纸条") } },
                { "chem_finisher", new[] {
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
                    P(Slot, null, "产出"),
                    P(Slot, null, "控制") } },
                { "bottle_printer", new[] {
                    P(Slot, "mech_bottle_printer_label_output", "产出"),
                    P(Slot, "mech_bottle_printer_label_control", "控制") } },
                { "blender", new[] {
                    P(Module, "mech_blender_label_module", "模块"),
                    P(Slot, "mech_blender_label_input", "输入"),
                    P(Slot, "mech_blender_label_container", "容器"),
                    P(Slot, "mech_blender_label_control", "控制") } },
                { "alarm", new[] {
                    P(Grid, null, "容器网格"),
                    P(Module, "mech_alarm_label_module", "模块"),
                    P(Note, null, "纸条") } },
                { "vending_machine", new[] {
                    P(Slot, "mech_vending_label_inventory", "货道"),
                    P(Slot, "mech_vending_label_control", "控制"),
                    P(Slot, "mech_vending_label_core", "核心") } },
                { "vending_fountain", new[] {
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

            internal Recipe(string[] inputs, string[] modules, string[] outputs,
                            string noteKey, string noteFallback)
            {
                Inputs = inputs ?? empty;
                Modules = modules ?? empty;
                Outputs = outputs ?? empty;
                NoteKey = noteKey;
                NoteFallback = noteFallback;
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
        }

        /// <summary>
        /// 熔炉产出：金属锭按纯度分档，档内不同投料方式轮播。
        ///
        /// 反编译依据（MachineFurnace + IngotPurityHelper）：
        /// · 基础纯度 = LOW；锭的档位 = LOW + floor(2 × 普通矿石数 ÷ 投料数)
        ///   + 品质加成（机器模块的 GetCurrentQualityBonus ÷ 100）。
        /// · flux_agent / advanced_flux_agent：保证品质加成至少 +1（会被消耗）。
        /// · furnace_module_blast：每周期多熔一份投料（2 → 3），但品质加成 -1。
        /// · furnace_module_junk：垃圾 → 废金属（另一条产出，不产锭）。
        /// 档位全集：VERY_LOW / LOW / FAIR / HIGH / VERY_HIGH / PERFECT。
        /// 这里每组列的是「能稳定落在这一档」的投料组合；轮播展示不同方案。
        /// </summary>
        private static readonly RecipeGroup[] FurnaceGroups =
        {
            new RecipeGroup
            {
                TitleKey = "mach_furnace_perfect", TitleFallback = "完美纯度金属锭",
                Variants = new[]
                {
                    // 2 矿石 + 高级助熔剂 + 优质模块（+2 档）
                    new Recipe(new[] { "common_ore", "common_ore", "advanced_flux_agent" },
                               new[] { "system_module_quality" },
                               new[] { "metal_ingot" },
                               "mach_note_perfect", "高纯矿石 + 高级助熔剂，品质模块再提一档"),
                    // 全矿石 + 高品质加成
                    new Recipe(new[] { "common_ore", "common_ore", "common_ore" },
                               new[] { "system_module_quality", "furnace_module_blast" },
                               new[] { "metal_ingot" },
                               "mach_note_perfect_alt", "全矿石 + 品质模块；爆燃模块虽 -1 档但每周期多熔一份")
                }
            },
            new RecipeGroup
            {
                TitleKey = "mach_furnace_very_high", TitleFallback = "极高纯度金属锭",
                Variants = new[]
                {
                    new Recipe(new[] { "common_ore", "common_ore", "flux_agent" },
                               null, new[] { "metal_ingot" },
                               "mach_note_flux", "矿石占多数 + 助熔剂保证 +1 档"),
                    new Recipe(new[] { "common_ore", "common_ore" },
                               new[] { "system_module_quality" },
                               new[] { "metal_ingot" },
                               "mach_note_quality", "全矿石 + 品质模块的加成")
                }
            },
            new RecipeGroup
            {
                TitleKey = "mach_furnace_high", TitleFallback = "高纯度金属锭",
                Variants = new[]
                {
                    new Recipe(new[] { "common_ore", "common_ore" },
                               null, new[] { "metal_ingot" },
                               "mach_note_ore2", "两份矿石 → 基础 +2 档"),
                    new Recipe(new[] { "common_ore", "scrap_metal", "flux_agent" },
                               null, new[] { "metal_ingot" },
                               "mach_note_ore_flux", "一份矿石 + 助熔剂")
                }
            },
            new RecipeGroup
            {
                TitleKey = "mach_furnace_fair", TitleFallback = "一般纯度金属锭",
                Variants = new[]
                {
                    new Recipe(new[] { "common_ore", "scrap_metal" },
                               null, new[] { "metal_ingot" },
                               "mach_note_ore1", "矿石与废金属各一份 → 基础 +1 档"),
                    new Recipe(new[] { "scrap_metal", "scrap_metal", "flux_agent" },
                               null, new[] { "metal_ingot" },
                               "mach_note_flux2", "废金属 + 助熔剂也能提一档")
                }
            },
            new RecipeGroup
            {
                TitleKey = "mach_furnace_low", TitleFallback = "低纯度金属锭",
                Variants = new[]
                {
                    new Recipe(new[] { "scrap_metal", "scrap_metal" },
                               null, new[] { "metal_ingot" },
                               "mach_note_scrap", "纯废金属 → 基础档"),
                    new Recipe(new[] { "common_ore", "scrap_metal" },
                               new[] { "furnace_module_blast" },
                               new[] { "metal_ingot" },
                               "mach_note_blast", "爆燃模块多熔一份但 -1 档")
                }
            },
            new RecipeGroup
            {
                TitleKey = "mach_furnace_very_low", TitleFallback = "极低纯度金属锭",
                Variants = new[]
                {
                    new Recipe(new[] { "scrap_metal", "scrap_metal" },
                               new[] { "furnace_module_blast" },
                               new[] { "metal_ingot" },
                               "mach_note_blast_low", "爆燃模块 -1 档；仍可多熔一份投料"),
                    new Recipe(new[] { "common_ore", "scrap_metal", "flux_agent" },
                               new[] { "furnace_module_blast" },
                               new[] { "metal_ingot" },
                               "mach_note_blast_flux", "助熔剂与爆燃模块互相抵消")
                }
            },
            new RecipeGroup
            {
                TitleKey = "mach_furnace_junk", TitleFallback = "废料回收（需垃圾模块）",
                Variants = new[]
                {
                    new Recipe(new[] { "junk" },
                               new[] { "furnace_module_junk" },
                               new[] { "scrap_metal" },
                               "mach_note_junk", "装上垃圾模块后，垃圾可以直接熔成废金属")
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

        private static readonly Dictionary<string, RecipeGroup[]> Recipes =
            new Dictionary<string, RecipeGroup[]>(StringComparer.Ordinal)
            {
                { "furnace", FurnaceGroups },
                { "evaporator", EvaporatorGroups },
                { "mouse_trap", MouseTrapGroups }
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
