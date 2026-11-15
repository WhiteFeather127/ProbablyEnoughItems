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

        internal sealed class Panel
        {
            internal char Kind;
            internal string GameLabelKey;   // 游戏 mech_*_label_* 键；电池 / 模块 / 纸条用兜底文案
            internal string Fallback;       // 游戏键取不到时的兜底（中文）

            internal Panel(char kind, string gameLabelKey, string fallback)
            {
                Kind = kind;
                GameLabelKey = gameLabelKey;
                Fallback = fallback;
            }
        }

        /// <summary>机器物品 id → 面板序列（从各机器类 IL 抄的，顺序即游戏窗口顺序）。</summary>
        private static readonly Dictionary<string, Panel[]> Panels =
            new Dictionary<string, Panel[]>(StringComparer.Ordinal)
            {
                { "furnace", new[] {
                    P(Grid, null, "容器网格"),
                    P(Battery, null, "电池"),
                    P(Module, "mech_furnace_label_module", "模块"),
                    P(Slot, "mech_furnace_label_input", "投入"),
                    P(Slot, "mech_furnace_label_output", "产出"),
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
                    P(Module, "mech_evaporator_label_module", "模块"),
                    P(Slot, "mech_evaporator_label_container", "容器"),
                    P(Slot, "mech_evaporator_label_distillate", "馏出"),
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

        /// <summary>这台机器的面板序列；不是已知机器（或没有界面数据）返回 null。</summary>
        internal static Panel[] For(string machineId)
        {
            if (string.IsNullOrEmpty(machineId)) return null;

            Panel[] panels;
            return Panels.TryGetValue(machineId, out panels) ? panels : null;
        }
    }
}
