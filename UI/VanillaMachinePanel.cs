﻿﻿﻿﻿﻿using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

using ItemCatalog.Catalog;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 用游戏自己的控件造一台机器的原版界面：GameGridInventory / GameSlotInventory
    /// + MachineryPanelRow + PixelWindow，再往真 inventory 里塞样本 GameItem——
    /// 格子、物品图标、悬停提示全是原生的。
    ///
    /// 两步走：BuildFurnaceShell() 造窗口并挂进详情容器，布局定稿后
    /// Fill(recipe) 再塞物品——否则 inventory 的 inner rect 还是 0，
    /// 物品渲染节点会定位到格子外面（截图里图标浮在面板上方）。
    /// </summary>
    internal sealed class VanillaMachinePanel
    {
        private PixelWindow _window;
        private readonly List<GameItem> _samples = new List<GameItem>();

        /// <summary>当前鼠标下的样本物品；轮播换物品时靠它判断要不要重新显示。</summary>
        private GameItemElement _hoverElement;

        // 作弊模式拖拽：按下位置超过阈值后，走图鉴的拿取流程复制一份拖走
        private GameItemElement _pressElement;
        private Vector2 _pressPos;
        private bool _pressing;
        private bool _takeDragStarted;

        /// <summary>
        /// 提示框当前是否由机器面板显示。不能拿 _hoverElement 判断：
        /// 轮播换物品时会把 _hoverElement 清空，但提示框要保留到鼠标真正离开。
        /// </summary>
        private bool _tooltipShown;

        // 诊断日志限流：tooltip 排查时每 0.5 秒最多打一行，整块面板最多 40 行
        private float _hoverLogAt;
        private int _hoverLogs;

        // 各 inventory 由谁装填（面板角色 → inventory）
        private GameGridInventory _moduleGrid;
        private GameGridInventory _inputGrid;
        private GameGridInventory _outputGrid;

        /// <summary>借用窗口里的单格投入 / 产出（解序列器、水培种子这类不是网格）。</summary>
        private GameSlotInventory _inputSlot;
        private GameSlotInventory _outputSlot;

        // 搅拌机 / 化学制剂成型机 / 蒸发器的容器槽（GameSlotInventory，原版放液体容器的那一格）
        private GameSlotInventory _containerSlot;

        /// <summary>蒸发器的馏出槽：蒸馏出来的液体会装进这里的容器。</summary>
        private GameSlotInventory _distillateSlot;

        /// <summary>成分反应页的产物槽（反应页不是机器，单独一个槽）。</summary>
        private GameSlotInventory _productSlot;

        /// <summary>DNA 编辑器的输出磁盘槽（放空白盘，extract 方向用）。</summary>
        private GameSlotInventory _outputDiskSlot;

        /// <summary>DNA 编辑器的种子单槽（Fill 的 inputs 放这里；网格机器仍用 _inputGrid）。</summary>
        private GameSlotInventory _seedSlot;

        /// <summary>解序列器的芯片槽（加密模块）。</summary>
        private GameSlotInventory _chipSlot;

        /// <summary>解序列器的输入槽（权限卡）与输出槽（解出的卡）。</summary>
        private GameSlotInventory _desqInputSlot;
        private GameSlotInventory _desqOutputSlot;

        /// <summary>枪械配件页的 8 个改装槽（枪托 / 导轨 / 弹匣 / 下挂 / 枪管 / 特殊 1~3）。</summary>
        private GameSlotInventory[] _gunSlots;

        /// <summary>只有 Fill 自己往预览格塞物品时允许入格；外部拖拽一律拒绝。</summary>
        private bool _filling;

        /// <summary>当前物品在面板里的绿底（直接用原版 HighlightSlot 生成的节点）。</summary>
        private readonly List<TreeNodeRender> _matchHighlights = new List<TreeNodeRender>();

        private RectTransform _attachedRect;

        /// <summary>机器面板窗口宽度上限：超过这个宽度就换行，不再加宽详情窗口。</summary>
        private const float MaxWinWidth = 1000f;

        // 借用窗口（预览实例的 contentWindow）：页面销毁时只还原挂载，不销毁窗口
        private bool _borrowed;
        private MachineryPanelRow _borrowedRow;
        private readonly List<MachineryPanel> _hiddenByAdopt = new List<MachineryPanel>();
        private readonly List<HookSnapshot> _borrowedHooks = new List<HookSnapshot>();

        /// <summary>
        /// 借用窗口里格子的回调快照：真实机器会在 onSlotAddItemFunc 里播放入格音效
        /// （电池是 battery_insert，模块仓是模块音效），预览填样本时必须临时摘掉。
        /// </summary>
        private sealed class HookSnapshot
        {
            internal GameInventory Inv;
            internal Func<GameItem, GameInventory, bool> May;
            internal Action<GameItem, GameInventory, SlotMarker> Add;
            internal Action<GameItem, GameInventory, SlotMarker> Remove;
        }
        private RectTransform _adoptedRect;
        private bool _loggedAdopt;

        /// <summary>诊断用：格子类型 + 尺寸，定位「某类槽位没显示」时看日志。</summary>
        private static string Name(GameInventory inv)
        {
            if (inv == null) return "-";

            var element = inv as PixelElement;
            var rt = element == null || element.handler == null ? null : element.handler.rectTransform;
            string size = rt == null ? "?" : (rt.rect.width.ToString("F0") + "x" + rt.rect.height.ToString("F0"));
            return inv.GetType().Name + "(" + size + ")";
        }
        private RectTransform _adoptedParent;
        private Vector2 _adoptedAnchorMin;
        private Vector2 _adoptedAnchorMax;
        private Vector2 _adoptedPivot;
        private Vector2 _adoptedSize;
        private Vector2 _adoptedPosition;
        private Vector3 _adoptedScale;

        internal RectTransform AttachedRect { get { return _attachedRect; } }

        /// <summary>
        /// 原版面板经 Canvas scale 补偿后的显示尺寸（造完壳就能算，用来给 AddSpace 占位）。
        /// maxHeight &gt; 0 时按可用高度封顶：窗口装不下的比例要往回收，
        /// 否则 802px 的面板会把机器样式窗口撑高、挤扁标题栏和关闭按钮。
        /// </summary>
        internal Vector2 ScaledSize(RectTransform host)
        {
            float ratio = CanvasRatio(host);
            float w = (_window != null && _window.widthPixels > 1f) ? _window.widthPixels : 160f;
            float h = (_window != null && _window.heightPixels > 1f) ? _window.heightPixels : 401f;
            return new Vector2(w * ratio, h * ratio);
        }

        /// <summary>
        /// 只做画布比例补偿：图鉴窗口和游戏窗口的画布 scaleFactor 不同，
        /// 乘上 game/ours 后格子像素与游戏里完全一致。
        /// 不再按页面可用空间封顶——超高由页面滚动条处理，硬缩会让格子比游戏里小。
        /// </summary>
        private float CanvasRatio(RectTransform host)
        {
            try
            {
                var ourCanvas = host != null ? host.GetComponentInParent<Canvas>() : null;
                Canvas gameCanvas = null;
                var handler = RenderHandler.current;
                if (handler != null) gameCanvas = handler.windowUICanvas;
                float ours = ourCanvas != null ? ourCanvas.scaleFactor : 1f;
                float game = gameCanvas != null ? gameCanvas.scaleFactor : 1f;
                if (ours <= 0.001f || game <= 0.001f) return 1f;
                float ratio = game / ours;
                if (ratio < 0.2f || ratio > 6f) ratio = 1f;
                return ratio;
            }
            catch { return 1f; }
        }

        internal bool IsAlive
        {
            get
            {
                return _window != null && _window.rectTransform != null;
            }
        }

        /// <summary>
        /// 造熔炉的原版窗口外壳（不含物品）。照 MachineFurnace.CreateMachineInventoryWindow 的 IL：
        /// 模块(形状网格) / 投入(9x6) / 产出(9x6)。
        /// 电池 / 纸条槽按用户要求不显示；面板行 SetColumns(1) 竖排换行。
        /// </summary>
        internal bool BuildFurnaceShell()
        {
            try
            {
                _moduleGrid = new GameGridInventory("10000010001000001110000010001000001", 7);
                _moduleGrid.SetMachineryStyle();
                _inputGrid = new GameGridInventory(9, 6);
                _outputGrid = new GameGridInventory(9, 6);

                // 预览面板不是真实机器：外部拖拽不允许放进去，只有 Fill 自己可以塞样本
                _moduleGrid.mayInventoryAddItemFunc = AllowOnlyWhileFilling;
                _inputGrid.mayInventoryAddItemFunc = AllowOnlyWhileFilling;
                _outputGrid.mayInventoryAddItemFunc = AllowOnlyWhileFilling;

                var panels = new[]
                {
                    MachineHelper.Panel(MechLabel("mech_furnace_label_module", "模块"), _moduleGrid, "module", true),
                    MachineHelper.Panel(MechLabel("mech_furnace_label_input", "投入"), _inputGrid, "input", true),
                    MachineHelper.Panel(MechLabel("mech_furnace_label_output", "产出"), _outputGrid, "output", true)
                };

                var row = new MachineryPanelRow(panels);
                row.SetColumns(1);   // 竖排

                _window = new PixelWindow(false, null, WindowStyle.Machinery);
                _window.Attach(row);
                // 游戏正常窗口在 Show() 里会 Validate()，由 childElement 的像素尺寸
                // 算出 widthPixels/heightPixels 并定稿所有面板坐标。
                // 我们直接抠内容挂进详情窗口，必须手动调一次，否则尺寸/坐标未定稿。
                _window.Validate();
                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[vanilla-machine] shell size="
                                     + _window.widthPixels + "x" + _window.heightPixels);
                return _window != null;
            }
            catch (Exception e)
            {
                Warn("build shell: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 造搅拌机的原版窗口外壳（不含物品）。照 MachineBlender.CreateMachineInventoryWindow 的 IL：
        /// 输入（4×4 网格）+ 容器（单槽，原版带 water_container4 底图）。
        /// 模块 / 电池 / 控制不参与配方展示，按用户要求只保留输入和容器。
        /// </summary>
        internal bool BuildBlenderShell()
        {
            try
            {
                _inputGrid = new GameGridInventory(4, 4);
                _containerSlot = new GameSlotInventory();

                // 预览面板不是真实机器：外部拖拽不允许放进去，只有 Fill 自己可以塞样本
                _inputGrid.mayInventoryAddItemFunc = AllowOnlyWhileFilling;
                _containerSlot.mayInventoryAddItemFunc = AllowOnlyWhileFilling;

                // 和原版 SetupWaterSlot 一致：容器槽垫一张大号液体容器底图
                try { _containerSlot.SetBackgroundFadeSprite("Items/water_container4", "xl", 0.5f, 0.5f); }
                catch { }

                var panels = new[]
                {
                    MachineHelper.Panel(MechLabel("mech_blender_label_input", "输入"), _inputGrid, "input", true),
                    MachineHelper.Panel(MechLabel("mech_blender_label_container", "容器"), _containerSlot, "container", true)
                };

                var row = new MachineryPanelRow(panels);
                // 两个槽都很窄，横向能放下：输入在左、容器在右。
                row.SetColumns(2);

                _window = new PixelWindow(false, null, WindowStyle.Machinery);
                _window.Attach(row);
                _window.Validate();
                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[vanilla-machine] blender shell size="
                                     + _window.widthPixels + "x" + _window.heightPixels);
                return _window != null;
            }
            catch (Exception e)
            {
                Warn("build blender shell: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 造化学制剂成型机的原版窗口外壳（不含物品）。照 MachineChemFinisher.CreateMachineInventoryWindow：
        /// 容器（单槽，原版带 water_container4 xl 底图）+ 产出（6×4 网格）。
        /// 模块 / 电池 / 控制不参与配方展示，和搅拌机一样只保留与配方直接相关的槽。
        /// </summary>
        internal bool BuildChemFinisherShell()
        {
            try
            {
                _containerSlot = new GameSlotInventory();
                _outputGrid = new GameGridInventory(6, 4);

                _containerSlot.mayInventoryAddItemFunc = AllowOnlyWhileFilling;
                _outputGrid.mayInventoryAddItemFunc = AllowOnlyWhileFilling;

                // 和原版 SetupWaterSlot 一致：容器槽垫一张大号液体容器底图
                try { _containerSlot.SetBackgroundFadeSprite("Items/water_container4", "xl", 0.5f, 0.5f); }
                catch { }

                var panels = new[]
                {
                    MachineHelper.Panel(MechLabel("mech_chem_finisher_label_container", "容器"), _containerSlot, "container", true),
                    MachineHelper.Panel(MechLabel("mech_chem_finisher_label_output", "产出"), _outputGrid, "output", true)
                };

                var row = new MachineryPanelRow(panels);
                row.SetColumns(2);

                _window = new PixelWindow(false, null, WindowStyle.Machinery);
                _window.Attach(row);
                _window.Validate();
                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[vanilla-machine] chem_finisher shell size="
                                     + _window.widthPixels + "x" + _window.heightPixels);
                return _window != null;
            }
            catch (Exception e)
            {
                Warn("build chem_finisher shell: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 造水培机的原版窗口外壳（不含物品）。照 MachineHydroponic.CreateMachineInventoryWindow：
        /// 模块 + 种子（单格）+ 水（单槽，原版带 water_container4 xl 底图）+ 产出（6×4 网格）。
        /// 电池 / 纸条不参与配方展示。
        /// </summary>
        internal bool BuildHydroponicShell()
        {
            try
            {
                // 原版模块仓就是熔炉那块 7×5 机械网格
                _moduleGrid = new GameGridInventory("10000010001000001110000010001000001", 7);
                _moduleGrid.SetMachineryStyle();
                _inputGrid = new GameGridInventory(1, 1);
                _containerSlot = new GameSlotInventory();
                _outputGrid = new GameGridInventory(9, 6);

                _moduleGrid.mayInventoryAddItemFunc = AllowOnlyWhileFilling;
                _inputGrid.mayInventoryAddItemFunc = AllowOnlyWhileFilling;
                _containerSlot.mayInventoryAddItemFunc = AllowOnlyWhileFilling;
                _outputGrid.mayInventoryAddItemFunc = AllowOnlyWhileFilling;

                // 和原版 SetupWaterSlot 一致：水槽垫一张大号液体容器底图
                try { _containerSlot.SetBackgroundFadeSprite("Items/water_container4", "xl", 0.5f, 0.5f); }
                catch { }

                var panels = new[]
                {
                    MachineHelper.Panel(MechLabel("mech_hydroponic_label_seed", "种子"), _inputGrid, "seed", true),
                    MachineHelper.Panel(MechLabel("mech_hydroponic_label_water", "水"), _containerSlot, "water", true),
                    MachineHelper.Panel(MechLabel("mech_hydroponic_label_module", "模块"), _moduleGrid, "module", true),
                    MachineHelper.Panel(MechLabel("mech_hydroponic_label_output", "产出"), _outputGrid, "output", true)
                };

                var row = new MachineryPanelRow(panels);
                row.SetColumns(2);

                _window = new PixelWindow(false, null, WindowStyle.Machinery);
                _window.Attach(row);
                _window.Validate();
                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[vanilla-machine] hydroponic shell size="
                                     + _window.widthPixels + "x" + _window.heightPixels);
                return _window != null;
            }
            catch (Exception e)
            {
                Warn("build hydroponic shell: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 造 DNA 编辑器的原版窗口外壳（只保留「提取 DNA」需要的部分）。
        /// 种子 / 空白盘 / 产出三块；模块与特性数据盘面板按用户要求不显示。
        /// </summary>
        internal bool BuildDnaManipulatorShell()
        {
            try
            {
                _seedSlot = new GameSlotInventory();            // 种子
                _outputDiskSlot = new GameSlotInventory();      // 空白盘
                _outputGrid = new GameGridInventory(2, 2);      // 弹出

                _seedSlot.mayInventoryAddItemFunc = AllowOnlyWhileFilling;
                _outputDiskSlot.mayInventoryAddItemFunc = AllowOnlyWhileFilling;
                _outputGrid.mayInventoryAddItemFunc = AllowOnlyWhileFilling;

                // 槽底图照原版 Setup*Slot：输出磁盘=空白盘，种子=营养果种子
                try { _outputDiskSlot.SetBackgroundFadeSprite("Items/items_hydroponic_plant", "dna_disk_black_empty", 0.5f, 0.5f); }
                catch { }
                try { _seedSlot.SetBackgroundFadeSprite("Items/items_hydroponic_plant", "nutrifruit_seed", 0.5f, 0.5f); }
                catch { }

                var panels = new[]
                {
                    MachineHelper.Panel(PlantGeneticsText.LabelTarget(), _seedSlot, "seed", true),
                    MachineHelper.Panel(PlantGeneticsText.LabelBlankDisk(), _outputDiskSlot, "output_disk", true),
                    MachineHelper.Panel(PlantGeneticsText.LabelOutput(), _outputGrid, "eject", true)
                };

                var row = new MachineryPanelRow(panels);
                row.SetColumns(3);   // 种子 / 空白盘 / 产出 横向一排

                _window = new PixelWindow(false, null, WindowStyle.Machinery);
                _window.Attach(row);
                _window.Validate();
                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[vanilla-machine] dna shell size="
                                     + _window.widthPixels + "x" + _window.heightPixels);
                return _window != null;
            }
            catch (Exception e)
            {
                Warn("build dna shell: " + e.Message);
                return false;
            }
        }

        /// <summary>DNA 页：输出磁盘槽放一只空白盘（extract 的写入目标）。</summary>
        internal void FillDnaBlankDisk()
        {
            if (_outputDiskSlot == null) return;

            var blank = DnaManager.CreateBlankDisk();
            if (blank == null) return;

            _filling = true;
            try { Accept(_outputDiskSlot, blank); }
            finally { _filling = false; }
        }

        /// <summary>
        /// 造 3D 打印机的原版窗口外壳（不含物品）。照 MachinePrinter.CreateMachineInventoryWindow：
        /// 模块(7×5) / 输入(9×6) / 芯片 / 产出(9×5)。电池与纸条不参与配方展示。
        /// </summary>
        internal bool BuildPrinterShell()
        {
            try
            {
                _moduleGrid = new GameGridInventory("10000010001000001110000010001000001", 7);
                _moduleGrid.SetMachineryStyle();
                _inputGrid = new GameGridInventory(9, 6);
                _outputGrid = new GameGridInventory(9, 5);
                _chipSlot = new GameSlotInventory();

                _moduleGrid.mayInventoryAddItemFunc = AllowOnlyWhileFilling;
                _inputGrid.mayInventoryAddItemFunc = AllowOnlyWhileFilling;
                _outputGrid.mayInventoryAddItemFunc = AllowOnlyWhileFilling;
                _chipSlot.mayInventoryAddItemFunc = AllowOnlyWhileFilling;

                // 芯片槽底图照原版：Items/items_tool → printer_chip_pistol
                try { _chipSlot.SetBackgroundFadeSprite("Items/items_tool", "printer_chip_pistol", 0.5f, 0.5f); }
                catch { }

                var panels = new[]
                {
                    MachineHelper.Panel(MechLabel("mech_printer_label_module", "模块"), _moduleGrid, "module", true),
                    MachineHelper.Panel(MechLabel("mech_printer_label_input", "输入"), _inputGrid, "input", true),
                    MachineHelper.Panel(MechLabel("mech_printer_label_chip", "芯片"), _chipSlot, "chip", true),
                    MachineHelper.Panel(MechLabel("mech_printer_label_output", "产出"), _outputGrid, "output", true)
                };

                var row = new MachineryPanelRow(panels);
                row.SetColumns(2);

                _window = new PixelWindow(false, null, WindowStyle.Machinery);
                _window.Attach(row);
                _window.Validate();
                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[vanilla-machine] printer shell size="
                                     + _window.widthPixels + "x" + _window.heightPixels);
                return _window != null;
            }
            catch (Exception e)
            {
                Warn("build printer shell: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 造枪械改装窗口的原版外壳（不含物品）：8 个单格槽，5 列排布，
        /// 顺序与 <c>GunModHelper.CreateGunModWindow</c> 一致：
        /// 枪托 / 导轨 / 弹匣 / 下挂 / 枪管 / 特殊槽位 1~3。
        /// 槽位标题直接读原版本地化键（`mech_gun_tag_*`）。
        /// </summary>
        internal bool BuildGunModShell()
        {
            try
            {
                int count = GunMods.SlotCount;
                _gunSlots = new GameSlotInventory[count];

                var panels = new MachineryPanel[count];

                for (int i = 0; i < count; i++)
                {
                    _gunSlots[i] = new GameSlotInventory();
                    _gunSlots[i].mayInventoryAddItemFunc = AllowOnlyWhileFilling;

                    panels[i] = MachineHelper.Panel(GunMods.SlotTitle(i), _gunSlots[i],
                                                    "gunmod" + i, true);
                }

                var row = new MachineryPanelRow(panels);
                row.SetColumns(5);

                _window = new PixelWindow(false, null, WindowStyle.Machinery);
                _window.Attach(row);
                _window.Validate();
                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[vanilla-machine] gun mod shell size="
                                     + _window.widthPixels + "x" + _window.heightPixels);
                return _window != null;
            }
            catch (Exception e)
            {
                Warn("build gun mod shell: " + e.Message);
                return false;
            }
        }

        /// <summary>按槽位顺序把配件 id 填进枪械改装面板（空字符串 = 该槽留空）。</summary>
        internal void FillGunMods(string[] slotIds)
        {
            ClearSamples();

            _filling = true;
            try
            {
                if (slotIds == null || _gunSlots == null) return;

                int count = Mathf.Min(slotIds.Length, _gunSlots.Length);
                for (int i = 0; i < count; i++)
                {
                    if (string.IsNullOrEmpty(slotIds[i])) continue;
                    Accept(_gunSlots[i], slotIds[i]);
                }
            }
            finally { _filling = false; }
        }

        /// <summary>
        /// 造解序列器的原版窗口外壳（不含物品）：芯片 / 输入(权限卡) / 产出，三块横向排开。
        /// 模块与电池不参与配方展示。
        /// </summary>
        internal bool BuildDesequencerShell()
        {
            try
            {
                _chipSlot = new GameSlotInventory();
                _desqInputSlot = new GameSlotInventory();
                _desqOutputSlot = new GameSlotInventory();

                _chipSlot.mayInventoryAddItemFunc = AllowOnlyWhileFilling;
                _desqInputSlot.mayInventoryAddItemFunc = AllowOnlyWhileFilling;
                _desqOutputSlot.mayInventoryAddItemFunc = AllowOnlyWhileFilling;

                // 槽底图照原版：芯片=sec_module，输入=blank_card，产出=sec_keycard
                try { _chipSlot.SetBackgroundFadeSprite("Items/items_tool", "sec_module", 0.5f, 0.5f); }
                catch { }
                try { _desqInputSlot.SetBackgroundFadeSprite("Items/items_misc", "blank_card", 0.5f, 0.5f); }
                catch { }
                try { _desqOutputSlot.SetBackgroundFadeSprite("Items/items_misc", "sec_keycard", 0.5f, 0.5f); }
                catch { }

                var panels = new[]
                {
                    MachineHelper.Panel(MechLabel("mech_desequencer_label_chip", "芯片"), _chipSlot, "chip", true),
                    MachineHelper.Panel(MechLabel("mech_desequencer_label_input", "输入"), _desqInputSlot, "input", true),
                    MachineHelper.Panel(MechLabel("mech_desequencer_label_output", "产出"), _desqOutputSlot, "output", true)
                };

                var row = new MachineryPanelRow(panels);
                row.SetColumns(3);

                _window = new PixelWindow(false, null, WindowStyle.Machinery);
                _window.Attach(row);
                _window.Validate();
                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[vanilla-machine] desequencer shell size="
                                     + _window.widthPixels + "x" + _window.heightPixels);
                return _window != null;
            }
            catch (Exception e)
            {
                Warn("build desequencer shell: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 造蒸发器的原版窗口外壳（不含物品）。照 MachineEvaporator.CreateMachineInventoryWindow：
        /// 容器（单槽，原版带 water_container4 xl 底图）+ 馏出（同一种水槽）。
        /// 电池 / 模块 / 纸条不参与展示。
        /// </summary>
        internal bool BuildEvaporatorShell()
        {
            try
            {
                _containerSlot = new GameSlotInventory();
                _distillateSlot = new GameSlotInventory();

                _containerSlot.mayInventoryAddItemFunc = AllowOnlyWhileFilling;
                _distillateSlot.mayInventoryAddItemFunc = AllowOnlyWhileFilling;

                // 和原版 SetupWaterSlot 一致：两个槽都垫大号液体容器底图
                try { _containerSlot.SetBackgroundFadeSprite("Items/water_container4", "xl", 0.5f, 0.5f); }
                catch { }
                try { _distillateSlot.SetBackgroundFadeSprite("Items/water_container4", "xl", 0.5f, 0.5f); }
                catch { }

                var panels = new[]
                {
                    MachineHelper.Panel(MechLabel("mech_evaporator_label_container", "容器"), _containerSlot, "container", true),
                    MachineHelper.Panel(MechLabel("mech_evaporator_label_distillate", "馏出"), _distillateSlot, "distillate", true)
                };

                var row = new MachineryPanelRow(panels);
                row.SetColumns(2);

                _window = new PixelWindow(false, null, WindowStyle.Machinery);
                _window.Attach(row);
                _window.Validate();
                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[vanilla-machine] evaporator shell size="
                                     + _window.widthPixels + "x" + _window.heightPixels);
                return _window != null;
            }
            catch (Exception e)
            {
                Warn("build evaporator shell: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 造成分反应页的面板（不是机器）：左 = 反应物容器，中间 = 条件文字，右 = 产物容器。
        /// 三块都是原版控件，容器里的混合液体由 <see cref="FillReaction"/> 灌入。
        /// </summary>
        internal bool BuildReactionShell(string[] conditionLines, string highlightLiquidId = null)
        {
            try
            {
                _containerSlot = new GameSlotInventory();
                _productSlot = new GameSlotInventory();

                _containerSlot.mayInventoryAddItemFunc = AllowOnlyWhileFilling;
                _productSlot.mayInventoryAddItemFunc = AllowOnlyWhileFilling;

                try { _containerSlot.SetBackgroundFadeSprite("Items/water_container4", "xl", 0.5f, 0.5f); }
                catch { }
                try { _productSlot.SetBackgroundFadeSprite("Items/water_container4", "xl", 0.5f, 0.5f); }
                catch { }

                var panels = new[]
                {
                    MachineHelper.Panel(ModEntry.T("react_input"), _containerSlot, "react_input", true),
                    MachineHelper.Panel(ModEntry.T("react_condition"), BuildTextStack(conditionLines, highlightLiquidId), "react_condition", true),
                    MachineHelper.Panel(ModEntry.T("react_output"), _productSlot, "react_output", true)
                };

                var row = new MachineryPanelRow(panels);
                row.SetColumns(3);

                _window = new PixelWindow(false, null, WindowStyle.Machinery);
                _window.Attach(row);
                _window.Validate();
                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[vanilla-machine] reaction shell size="
                                     + _window.widthPixels + "x" + _window.heightPixels);
                return _window != null;
            }
            catch (Exception e)
            {
                Warn("build reaction shell: " + e.Message);
                return false;
            }
        }

        /// <summary>一列原版小字（温度 / 条件 / 结果），每行一个 MachineryText；液体名可点击。</summary>
        private static MachineryStack BuildTextStack(string[] lines, string highlightLiquidId)
        {
            var elements = new PixelElement[Mathf.Max(1, lines == null ? 0 : lines.Length)];
            for (int i = 0; i < elements.Length; i++)
            {
                string text = lines != null && i < lines.Length ? lines[i] : string.Empty;
                var label = new MachineryText(text, 8, HorizontalAlignmentOptions.Center, false);
                AttachConditionLinks(label, highlightLiquidId);
                elements[i] = label;
            }

            return MachineryStack.Vertical(elements);
        }

        /// <summary>给条件文字的 &lt;link&gt; 片段挂点击；只给正在检索的液体名画绿底。</summary>
        private static void AttachConditionLinks(MachineryText label, string highlightLiquidId)
        {
            if (label == null) return;

            try
            {
                var rect = label.rectTransform;
                var tmp = rect == null ? null : rect.GetComponentInChildren<TMP_Text>(true);
                if (tmp == null) return;

                var handler = tmp.gameObject.GetComponent<ConditionLinkHandler>();
                if (handler == null) handler = tmp.gameObject.AddComponent<ConditionLinkHandler>();

                handler.Text = tmp;
                handler.HighlightLinkId = string.IsNullOrEmpty(highlightLiquidId)
                    ? null
                    : LiquidCatalog.EntryPrefix + highlightLiquidId;
                handler.OnLink = delegate (string linkId, bool asUse)
                {
                    var entry = LiquidCatalog.Find(linkId);
                    if (entry != null) RecipeWindow.OpenFor(entry, asUse);
                };

                tmp.raycastTarget = true;
            }
            catch { }
        }

        /// <summary>
        /// 反应页装填：两侧各造一只 bottled_water，按 10 ml 基准灌入对应液体。
        /// 同一容器里多个液体各写各的 CURRENT_PART，不会互相覆盖。
        /// </summary>
        internal void FillReaction(string[] reactantIds, float[] reactantMl,
                                   string[] productIds, float[] productMl)
        {
            ClearSamples();

            _filling = true;
            try
            {
                AcceptMixture(_containerSlot, reactantIds, reactantMl);

                // 没有实物产物时也给一只空容器（反应完留下的空瓶）
                if (productIds == null || productIds.Length == 0)
                    AcceptEmptyContainer(_productSlot);
                else
                    AcceptMixture(_productSlot, productIds, productMl);
            }
            finally { _filling = false; }
        }

        /// <summary>往槽里放一只空的 bottled_water（无产物反应的「空瓶」）。</summary>
        private void AcceptEmptyContainer(GameSlotInventory slot)
        {
            if (slot == null) return;

            GameItem item = null;
            try
            {
                item = CatalogWindow.CreatePreviewItem(LiquidManager.ContainerId);
                if (item == null) return;

                Accept(slot, item);
                item = null;   // Accept 成功后样本由面板管理
            }
            catch (Exception e) { Warn("empty container: " + e.Message); }
            finally
            {
                if (item != null) { try { item.Destroy(); } catch { } }
            }
        }

        private void AcceptMixture(GameSlotInventory slot, string[] liquidIds, float[] ml)
        {
            if (slot == null || liquidIds == null || liquidIds.Length == 0) return;

            GameItem item = null;
            try
            {
                item = CatalogWindow.CreatePreviewItem(LiquidManager.ContainerId);
                if (item == null) return;

                for (int i = 0; i < liquidIds.Length; i++)
                {
                    if (string.IsNullOrEmpty(liquidIds[i])) continue;

                    float amount = i < ml.Length ? ml[i] : 0f;
                    int units = Mathf.RoundToInt(amount * 1000f);
                    if (units <= 0) units = 1000;
                    ChemicalHelper.SetChemContentUL(item, liquidIds[i], units);
                }

                try { WaterFeatureHelper.UpdateWaterFeature(item, false); } catch { }
                try { SpriteHelper.UpdateWaterContainerSprite(item); } catch { }

                Accept(slot, item);
                item = null;   // Accept 成功后样本由面板管理
            }
            catch (Exception e) { Warn("fill reaction: " + e.Message); }
            finally
            {
                if (item != null) { try { item.Destroy(); } catch { } }
            }
        }

        /// <summary>
        /// 实测面板里**内容**的真实高度（世界包围盒）。
        /// 面板的 heightPixels 含底部留白（面板行 padding / yPadding），
        /// 按 rect 高度布局会让内容只占上半部分，多出来的空白就落在面板与翻页条之间。
        /// </summary>
        internal float MeasureContentHeight()
        {
            return NativeSize(false);
        }

        internal float MeasureContentWidth()
        {
            return NativeSize(true);
        }

        /// <summary>内容包围盒的世界范围；有面板行时只量可见面板及其子节点。</summary>
        internal bool MeasuredBounds(out float minX, out float maxX, out float minY, out float maxY)
        {
            minX = minY = float.MaxValue;
            maxX = maxY = float.MinValue;

            var row = _borrowedRow;
            if (row == null && _window != null) row = _window.childElement as MachineryPanelRow;
            if (row != null)
            {
                var panels = row.Panels;
                for (int i = 0; i < panels.Count; i++)
                {
                    var panel = panels[i];
                    if (panel == null || panel.isHidden) continue;

                    var rt = panel.handler == null ? null : panel.handler.rectTransform;
                    if (rt == null || !rt.gameObject.activeInHierarchy) continue;

                    // 面板的 handler 是壳，标签和内容在它的子节点里，必须一起量；
                    // 但只量「有 Image 的可见节点」：物品元素自带宽度 1000 的堆叠数字文本，
                    // 把文本节点算进来会让内容宽高离谱（枪械模组页曾因此被判成 1200+ 宽）。
                    ExtendBounds(rt, ref minX, ref maxX, ref minY, ref maxY);

                    var all = rt.GetComponentsInChildren<RectTransform>(true);
                    for (int k = 0; k < all.Length; k++)
                    {
                        var child = all[k];
                        if (child == null || child == rt || !child.gameObject.activeInHierarchy) continue;
                        if (child.GetComponent<UnityEngine.UI.Image>() == null) continue;

                        ExtendBounds(child, ref minX, ref maxX, ref minY, ref maxY);
                    }
                }

                return maxX > minX && maxY > minY;
            }

            if (_attachedRect == null) return false;

            try
            {
                var all = _attachedRect.GetComponentsInChildren<RectTransform>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    var rt = all[i];
                    if (rt == null || rt == _attachedRect) continue;
                    if (!rt.gameObject.activeInHierarchy) continue;

                    ExtendBounds(rt, ref minX, ref maxX, ref minY, ref maxY);
                }
                return maxX > minX && maxY > minY;
            }
            catch { return false; }
        }

        private RectTransform Row()
        {
            var row = _borrowedRow;
            if (row == null && _window != null) row = _window.childElement as MachineryPanelRow;
            return row == null || row.handler == null ? null : row.handler.rectTransform;
        }

        /// <summary>把世界尺寸换算回面板自身坐标系（除以当前缩放）。</summary>
        private float NativeSize(bool width)
        {
            float minX, maxX, minY, maxY;
            if (!MeasuredBounds(out minX, out maxX, out minY, out maxY)) return 0f;

            var rt = Row() ?? _attachedRect;
            if (rt == null) return 0f;

            float s = Mathf.Abs(width ? rt.lossyScale.x : rt.lossyScale.y);
            if (s < 0.001f) s = 1f;

            float measured = (width ? maxX - minX : maxY - minY) / s;
            return (measured < 10f || measured > 4000f) ? 0f : measured;
        }

        private static void ExtendBounds(RectTransform rt,
                                         ref float minX, ref float maxX,
                                         ref float minY, ref float maxY)
        {
            var c = new Vector3[4];
            rt.GetWorldCorners(c);
            for (int i = 0; i < 4; i++)
            {
                if (c[i].x < minX) minX = c[i].x;
                if (c[i].x > maxX) maxX = c[i].x;
                if (c[i].y < minY) minY = c[i].y;
                if (c[i].y > maxY) maxY = c[i].y;
            }
        }

        /// <summary>把原版窗口内容挂到指定容器（窗口框/标题栏不要，只嵌面板行）。</summary>
        internal RectTransform AttachContentTo(RectTransform host)
        {
            if (_window == null || host == null) return null;
            try
            {
                var element = _window.childElement;
                var node = element != null ? element.handler : null;
                var rect = node != null ? node.rectTransform : _window.rectTransform;
                if (rect == null) return null;

                // host 里的 AddSpace 负责撑开布局高度，面板本身 ignoreLayout + 绝对居中，
                // 不与布局组冲突，也不要销毁布局组（销毁会让 bodyHost 算不出占位的高度）。
                // 兜底：host 加 RectMask2D，面板超出 host 矩形的部分直接裁掉，
                // 绝不会盖住上方的标题行 / 下方的翻页条（之前溢出遮挡就是没裁剪）。
                var mask = host.GetComponent<UnityEngine.UI.RectMask2D>();
                if (mask == null) mask = host.gameObject.AddComponent<UnityEngine.UI.RectMask2D>();

                rect.SetParent(host, false);
                rect.localScale = Vector3.one;
                // 正中心锚点：host 高度已显式设成面板缩放后的高度，面板上下正好填满；
                // 配合 host 上的 RectMask2D，即使有毫米级误差也只会被裁、不会遮挡上下内容。
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = Vector2.zero;
                // 明确设尺寸：handler 是空布局壳，rect.size 常为 0，
                // 不设 sizeDelta 面板会缩成一点。用 Validate() 算好的像素尺寸。
                float w = _window != null && _window.widthPixels > 1f ? _window.widthPixels : 200f;
                float h = _window != null && _window.heightPixels > 1f ? _window.heightPixels : 300f;
                rect.sizeDelta = new Vector2(w, h);

                // 面板自己也不参与任何布局（防御：万一父级还有别的布局组件）
                var le = rect.GetComponent<UnityEngine.UI.LayoutElement>();
                if (le == null) le = rect.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
                le.ignoreLayout = true;

                _attachedRect = rect;
                return rect;
            }
            catch (Exception e)
            {
                Warn("attach: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// 让嵌在详情窗口里的原版面板显示尺寸和游戏里正常机器窗口一致。
        /// 游戏窗口在 RenderHandler.windowUICanvas 下，详情窗口在 CustomUIManager.overlayCanvas
        /// 下，两个 Canvas 的 CanvasScaler 不同（overlay 是 ScaleWithScreenSize 1920×1080），
        /// 所以同样的 gridSize×格数 像素显示出来一大一小。
        /// 取两个 Canvas 的 scaleFactor 比例，等比补偿到内容节点上（只改 localScale，
        /// 物品是 grid 子节点会一起缩放，格子内相对位置不变）。
        /// </summary>
        internal float MatchGameCanvasScale(RectTransform host)
        {
            if (_attachedRect == null || host == null) return 0f;
            try
            {
                float ratio = CanvasRatio(host);

                // 只改缩放，anchor/pivot 已在 AttachContentTo 设成正中心，这里不碰
                _attachedRect.localScale = new Vector3(ratio, ratio, 1f);

                // 真实尺寸优先用**实测内容包围盒**：widthPixels/heightPixels 含面板行与
                // 窗口的左右/底部留白，按它布局会让内容周围多出一圈空白。
                float boxW = _window != null && _window.widthPixels > 1f
                    ? _window.widthPixels : _attachedRect.rect.width;
                float boxH = _window != null && _window.heightPixels > 1f
                    ? _window.heightPixels : _attachedRect.rect.height;
                float measuredW = MeasureContentWidth();
                float measuredH = MeasureContentHeight();
                bool useMeasuredW = measuredW > 10f && measuredW < boxW;
                bool useMeasuredH = measuredH > 10f && measuredH < boxH;

                // ① 把 rect 收到「实测内容 + 一圈边框」：完全按内容收会切掉面板自己的
                //    左右/上下边框；完全按窗口尺寸又会留一大圈空白。取窗口留白的一半，
                //    封顶 16px，既保住边框也不会多出明显空档。
                float padW = useMeasuredW ? Mathf.Clamp((boxW - measuredW) * 0.5f, 0f, 16f) : 0f;
                float padH = useMeasuredH ? Mathf.Clamp((boxH - measuredH) * 0.5f, 0f, 16f) : 0f;
                float usedW = (useMeasuredW ? measuredW : boxW) + padW * 2f;
                float usedH = (useMeasuredH ? measuredH : boxH) + padH * 2f;

                // ② 再把面板整体平移，让内容包围盒中心与容器中心重合
                //    （面板行有 padding，内容在 rect 里并不居中）。
                if (useMeasuredW || useMeasuredH)
                {
                    _attachedRect.sizeDelta = new Vector2(usedW, usedH);

                    float minX, maxX, minY, maxY;
                    if (MeasuredBounds(out minX, out maxX, out minY, out maxY))
                    {
                        var c = new Vector3[4];
                        _attachedRect.GetWorldCorners(c);
                        float rectCenterX = (c[0].x + c[3].x) * 0.5f;
                        float rectCenterY = (c[0].y + c[1].y) * 0.5f;
                        float dx = rectCenterX - (minX + maxX) * 0.5f;
                        float dy = rectCenterY - (minY + maxY) * 0.5f;

                        if (Mathf.Abs(dx) > 0.5f || Mathf.Abs(dy) > 0.5f)
                        {
                            var pos = _attachedRect.position;
                            _attachedRect.position = new Vector3(pos.x + dx, pos.y + dy, pos.z);
                        }
                    }
                }

                float scaledHeight = usedH * ratio;

                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[vanilla-machine] ratio=" + ratio
                                     + " box=" + boxW + "x" + boxH
                                     + " content=" + measuredW + "x" + measuredH
                                     + " scaledH=" + scaledHeight);
                return scaledHeight;
            }
            catch (Exception e) { Warn("canvas scale: " + e.Message); return 0f; }
        }

        /// <summary>
        /// 布局定稿后往真 inventory 塞样本物品。
        /// 必须走 GraphUtils.TryAccept（不是 inventory.UncheckedAccept）：
        /// 原版入格流程 TryFindOneValidInventorySlot → SlotMarker.TryAcceptOnce 会用
        /// GridShapeBuilder.SetTransform 算出物品在网格里的坐标并 set_modifiedShape，
        /// 直接 UncheckedAccept 跳过这步，物品坐标停在 (0,0) 堆在角落、浮到格子外。
        /// 走完整流程后物品渲染节点按格子坐标摆放，tooltip 也由原版 GameItem 接管。
        /// </summary>
        internal void Fill(string[] inputs, string[] modules, string[] outputs,
                           string purity = null, string container = null,
                           string containerLiquid = null, int containerVolume = 0,
                           Func<int, GameItem> outputItems = null,
                           Func<int, GameItem> inputItems = null,
                           string chip = null)
        {
            ClearSamples();

            _filling = true;
            try
            {
                if (modules != null)
                    for (int i = 0; i < modules.Length; i++) Accept(_moduleGrid, modules[i]);

                // 解序列器的芯片槽（加密模块）：单独一格，不进模块网格
                if (!string.IsNullOrEmpty(chip) && _chipSlot != null)
                    Accept(_chipSlot, chip);
                if (inputs != null)
                {
                    // 单格投入优先：DNA 种子槽 / 解序列器权限卡 / 借用窗口里的单格 input
                    GameInventory inputTarget = _seedSlot != null
                        ? (GameInventory)_seedSlot
                        : (_desqInputSlot != null
                            ? (GameInventory)_desqInputSlot
                            : (_inputSlot != null ? (GameInventory)_inputSlot : _inputGrid));

                    for (int i = 0; i < inputs.Length; i++)
                    {
                        GameItem sample = null;
                        if (inputItems != null)
                        {
                            try { sample = inputItems(i); }
                            catch (Exception e) { Warn("input item '" + inputs[i] + "': " + e.Message); }
                        }

                        if (sample != null) Accept(inputTarget, sample);
                        else Accept(inputTarget, inputs[i]);
                    }
                }
                if (outputs != null)
                {
                    // 单格产出优先：解序列器单槽 / 借用窗口里的单格 output
                    GameInventory outputTarget = _desqOutputSlot != null
                        ? (GameInventory)_desqOutputSlot
                        : (_outputSlot != null ? (GameInventory)_outputSlot : _outputGrid);

                    for (int i = 0; i < outputs.Length; i++)
                    {
                        GameItem sample = null;
                        if (outputItems != null)
                        {
                            try { sample = outputItems(i); }
                            catch (Exception e) { Warn("output item '" + outputs[i] + "': " + e.Message); }
                        }

                        if (sample != null) Accept(outputTarget, sample, purity);
                        else Accept(outputTarget, outputs[i], purity);
                    }
                }

                // 搅拌机 / 化学制剂成型机 / 蒸发器：容器槽放一只液体容器（默认 bottled_water）。
                // 搅拌机把投入的液体成分灌进去；化学制剂成型机与蒸发器按配方灌入指定液体。
                if (!string.IsNullOrEmpty(container) && _containerSlot != null)
                {
                    var liquidContainer = CatalogWindow.CreatePreviewItem(container);
                    if (liquidContainer == null)
                    {
                        Accept(_containerSlot, container);
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(containerLiquid))
                            FillContainerPreview(liquidContainer, containerLiquid, containerVolume);
                        else if (inputs != null && inputs.Length > 0)
                        {
                            // 品质档作物：搅拌成分也按这一档的实例算，不能退回本体
                            GameItem blendSample = null;
                            if (inputItems != null)
                            {
                                try { blendSample = inputItems(0); }
                                catch (Exception e) { Warn("blend sample '" + inputs[0] + "': " + e.Message); }
                            }

                            BlendIntoPreview(liquidContainer, inputs[0], blendSample);
                        }

                        Accept(_containerSlot, liquidContainer);
                    }
                }

                // 蒸发器：馏出槽里也放一只容器，装的就是蒸出来的那种液体。
                if (_distillateSlot != null && !string.IsNullOrEmpty(container) &&
                    !string.IsNullOrEmpty(containerLiquid))
                {
                    var distillate = CatalogWindow.CreatePreviewItem(container);
                    if (distillate != null)
                    {
                        FillContainerPreview(distillate, containerLiquid, containerVolume);
                        Accept(_distillateSlot, distillate);
                    }
                }
            }
            finally { _filling = false; }
        }

        /// <summary>
        /// 把投入物品的液体成分灌进容器样本。直接用游戏自己的 BlendInto，
        /// 保证容器图标 / 提示框显示的是搅拌后的内容物。
        /// chemical 非空时直接用该实例（品质档作物），不再按 id 造本体。
        /// loss = 0：图鉴只展示成分，不模拟模块损失。
        /// </summary>
        private static void BlendIntoPreview(GameItem container, string inputId, GameItem chemical = null)
        {
            if (container == null || string.IsNullOrEmpty(inputId)) return;

            try
            {
                if (chemical == null) chemical = CatalogWindow.CreatePreviewItem(inputId);
                if (chemical == null) return;
                if (!chemical.IsTag("BLENDABLE_TAG")) return;

                // 药品用实验室纯级（100% 活性物质），产物里的成分也按 100% 纯度灌入
                if (CatalogWindow.IsFullPurityItem(inputId))
                    CatalogWindow.ApplyFullPurity(chemical, inputId);

                ChemicalHelper.BlendInto(chemical, container, 0);
                chemical = null;   // BlendInto 内部已经 Destroy
            }
            catch (Exception e) { Warn("blend preview '" + inputId + "': " + e.Message); }
            finally
            {
                if (chemical != null) { try { chemical.Destroy(); } catch { } }
            }
        }

        /// <summary>
        /// 按配方表把指定液体灌进容器样本（化学制剂成型机）。
        /// 直接写液体标签，不走 AddLiquid 的随机纯度 / 温度管线——和液体管理器造代表瓶一致。
        /// </summary>
        private static void FillContainerPreview(GameItem container, string liquidId, int volume)
        {
            if (container == null || string.IsNullOrEmpty(liquidId)) return;

            // volume <= 0 表示「按容器容量装满」（蒸发器页只要看得出装的是什么液体）。
            if (volume <= 0)
            {
                try { volume = container.GetTagReadonly("LIQUID_CONTAINER_CAPACITY").GetInt(); }
                catch { volume = 0; }
            }
            if (volume <= 0) volume = 1000;

            try
            {
                ChemicalHelper.SetChemContentUL(container, liquidId, volume);
                try { WaterFeatureHelper.UpdateWaterFeature(container, false); } catch { }
                try { SpriteHelper.UpdateWaterContainerSprite(container); } catch { }
            }
            catch (Exception e) { Warn("fill container preview '" + liquidId + "': " + e.Message); }
        }

        /// <summary>
        /// 水培水槽：按最佳养分浓度重灌容器（水 + 矿物），总量 = 容器容量。
        /// 矿物浓度是千分比换算后的百分比（nutrimentLevel/10）。
        /// </summary>
        /// <param name="mineralBasisPoints">
        /// 矿物占**水量**的万分比，与游戏 <c>WaterHelper.GetContaminantPercentage</c> 同口径
        /// （150 = 1.5%）。内容物总量正好等于容器容量，矿物 = 总量 × bp / (10000 + bp)。
        /// </param>
        internal void FillHydroponicWater(int mineralBasisPoints)
        {
            var container = ContainerSample;
            if (container == null) return;

            int total = 0;
            try { total = container.GetTagReadonly("LIQUID_CONTAINER_CAPACITY").GetInt(); } catch { }
            if (total <= 0) total = LiquidManager.WaterCapacityUl();

            int bp = mineralBasisPoints;
            if (bp < 0) bp = 0;
            if (bp > 10000) bp = 10000;

            int mineral = bp <= 0 ? 0 : (int)((long)total * bp / (10000 + bp));
            int water = total - mineral;

            try
            {
                WaterHelper.EmptyContainer(container);
                if (water > 0) ChemicalHelper.SetChemContentUL(container, LiquidManager.WaterId, water);
                if (mineral > 0) ChemicalHelper.SetChemContentUL(container, "mineral", mineral);
                try { WaterFeatureHelper.UpdateWaterFeature(container, false); } catch { }
                try { SpriteHelper.UpdateWaterContainerSprite(container); } catch { }
            }
            catch (Exception e) { Warn("fill hydroponic water: " + e.Message); }
        }

        /// <summary>当前配方预览里容器槽中的样本（没有则 null）。</summary>
        internal GameItem ContainerSample
        {
            get
            {
                if (_containerSlot == null) return null;

                for (int i = 0; i < _samples.Count; i++)
                {
                    var it = _samples[i];
                    if (it != null && it.parentInventory == _containerSlot) return it;
                }

                return null;
            }
        }

        /// <summary>成分反应页产物槽里的样本（没有则 null）。</summary>
        internal GameItem ProductSample
        {
            get
            {
                if (_productSlot == null) return null;

                for (int i = 0; i < _samples.Count; i++)
                {
                    var it = _samples[i];
                    if (it != null && it.parentInventory == _productSlot) return it;
                }

                return null;
            }
        }

        private bool AllowOnlyWhileFilling(GameItem item, GameInventory inventory)
        {
            return _filling;
        }

        private void ClearMatchHighlights()
        {
            // 原版拖拽高亮也是通过 image.enabled = false 回收的，不销毁节点
            for (int i = 0; i < _matchHighlights.Count; i++)
            {
                var node = _matchHighlights[i];
                if (node != null && node.image != null) node.image.enabled = false;
            }
            _matchHighlights.Clear();
        }

        /// <summary>
        /// 把当前物品在面板里的那些格子垫上原版放置绿底。
        /// 直接调用 GameInventory.HighlightSlot（和原版拖拽放置的绿底同一个通道），
        /// 绿底画在格子底下，不会盖住格子边框或物品图标。
        /// </summary>
        internal void HighlightItem(string id)
        {
            ClearMatchHighlights();
            if (string.IsNullOrEmpty(id)) return;

            for (int i = 0; i < _samples.Count; i++)
            {
                var element = _samples[i] as GameItemElement;
                if (element == null) continue;
                if (!string.Equals(element.identifier, id, StringComparison.Ordinal)) continue;

                HighlightElement(element);
            }
        }

        /// <summary>
        /// 容器里含有某种液体时把它标绿（成分判定交给液体管理器）。
        /// 机器页查询的是液体条目（liquid:xxx）时用：容器样本是 bottled_water，
        /// 按物品 id 匹配不到，必须按 CURRENT_PART 成分匹配。
        /// 不清旧绿底——调用方通常先 HighlightItem，叠加即可。
        /// </summary>
        internal void HighlightLiquid(string liquidId)
        {
            if (string.IsNullOrEmpty(liquidId)) return;

            for (int i = 0; i < _samples.Count; i++)
            {
                var element = _samples[i] as GameItemElement;
                if (element == null) continue;

                bool contains = false;
                try { contains = LiquidManager.Parts(element).ContainsKey(liquidId); }
                catch { }
                if (!contains) continue;

                HighlightElement(element);
            }
        }

        private int HighlightElement(GameItemElement element,
                                     RenderHandler.BackgroundColor color = RenderHandler.BackgroundColor.Green)
        {
            var inventory = element == null ? null : element.parentInventory;
            if (inventory == null) return 0;

            try
            {
                var slot = inventory.GetIncompleteItemInventorySlot(element);
                var nodes = inventory.HighlightSlot(slot, color, true);
                if (nodes != null && nodes.Count > 0)
                {
                    _matchHighlights.AddRange(nodes);
                    return nodes.Count;
                }
            }
            catch (Exception e) { Warn("highlight element: " + e.Message); }

            return 0;
        }

        /// <summary>
        /// 概率产物：**具体的那个产物样本** → 提示框末尾追加的说明（例如「生成概率：50%」）。
        /// 必须按元素记，不能按 id：同一 id 的另一份是必定产出的（例如第一颗种子），
        /// 按 id 记会让它也显示概率。
        /// </summary>
        private readonly List<KeyValuePair<GameItemElement, string>> _tooltipNotes =
            new List<KeyValuePair<GameItemElement, string>>();

        /// <summary>产物格里放入的样本元素（按放置顺序，概率产物按这个下标定位）。</summary>
        private readonly List<GameItemElement> _outputElements = new List<GameItemElement>();

        /// <summary>
        /// 标出概率产物：格子上黄底（原版没有 Yellow，用最接近的 Tan），提示框末尾追加概率。
        /// 不能按 <c>Outputs</c> 的下标取 <c>_outputElements</c>：样本按形状排布，
        /// 被网格拒绝或堆叠合并时两者会错位（首颗种子与第二颗同 id 时尤其明显）。
        /// 这里按「这个 id 在 Outputs 里第几次出现」去找对应的产物样本。
        /// </summary>
        internal void MarkUncertain(string[] outputs, int[] indexes, string note)
        {
            if (outputs == null || indexes == null || indexes.Length == 0 || string.IsNullOrEmpty(note)) return;

            for (int i = 0; i < indexes.Length; i++)
            {
                int index = indexes[i];
                if (index < 0 || index >= outputs.Length) continue;

                string id = outputs[index];
                if (string.IsNullOrEmpty(id)) continue;

                int occurrence = 0;
                for (int k = 0; k <= index && k < outputs.Length; k++)
                    if (string.Equals(outputs[k], id, StringComparison.Ordinal)) occurrence++;

                var el = FindOutputElement(id, occurrence);
                if (el == null)
                {
                    if (ModEntry.Log != null && _uncertainLogs < 8)
                    {
                        _uncertainLogs++;
                        ModEntry.Log.Warning("[hydro] uncertain '" + id + "' #" + occurrence
                                             + " has no output element (samples=" + _outputElements.Count + ")");
                    }
                    continue;
                }

                SetTooltipNote(el, note);

                // 原版的 HighlightSlot 在这个网格里画出来也看不见（节点存在但被底板盖住），
                // 直接用一层纯色块：Image 不挂 sprite 时就是纯色，不依赖图集里有没有 tan。
                int marks = OverlayUncertain(el);

                if (ModEntry.Log != null && _uncertainLogs < 8)
                {
                    _uncertainLogs++;
                    ModEntry.Log.Log("[hydro] uncertain '" + id + "' #" + occurrence
                                     + " elements=" + _outputElements.Count + " marks=" + marks);
                }
            }
        }

        private int _uncertainLogs;

        /// <summary>把概率说明挂到这一个样本上（同一元素重复标注时覆盖）。</summary>
        private void SetTooltipNote(GameItemElement element, string note)
        {
            if (element == null || string.IsNullOrEmpty(note)) return;

            for (int i = _tooltipNotes.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_tooltipNotes[i].Key, element)) _tooltipNotes.RemoveAt(i);
            }

            _tooltipNotes.Add(new KeyValuePair<GameItemElement, string>(element, note));
        }

        /// <summary>
        /// 概率产物格子的黄底：在物品图标上叠一层半透明纯色块。
        /// 做成子节点是为了保证可见（兄弟节点会被格子底板盖住），并且跟随图标一起移动、销毁。
        /// </summary>
        private int OverlayUncertain(GameItemElement element)
        {
            try
            {
                if (element == null) return 0;

                var target = element.background != null
                    ? element.background.rectTransform
                    : element.rectTransform;
                if (target == null) return 0;

                var go = new GameObject("hydroUncertain",
                    typeof(RectTransform), typeof(UnityEngine.UI.Image));
                go.transform.SetParent(target, false);

                var rect = go.GetComponent<RectTransform>();
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                rect.localScale = Vector3.one;

                var image = go.GetComponent<UnityEngine.UI.Image>();
                image.sprite = null;                  // 无贴图 = 纯色块
                image.color = new Color(1f, 0.82f, 0.22f, 0.42f);
                image.raycastTarget = false;

                _highlightOverlays.Add(go);
                return 1;
            }
            catch (Exception e)
            {
                Warn("uncertain overlay: " + e.Message);
                return 0;
            }
        }

        private readonly List<GameObject> _highlightOverlays = new List<GameObject>();

        /// <summary>按 id 找第 occurrence 个产物样本；数量对不上时退回该 id 的最后一格。</summary>
        private GameItemElement FindOutputElement(string id, int occurrence)
        {
            GameItemElement last = null;
            int seen = 0;

            for (int i = 0; i < _outputElements.Count; i++)
            {
                var el = _outputElements[i];
                if (el == null || !string.Equals(el.identifier, id, StringComparison.Ordinal)) continue;

                seen++;
                last = el;
                if (occurrence > 0 && seen == occurrence) return el;
            }

            return last;
        }

        /// <summary>
        /// 概率说明挂到原版提示框末尾（提示框每帧重建，所以每次刷新都要挂）。
        /// RichText 的属性只读，只能按原值建一个新对象。
        /// </summary>
        private RichText WithTooltipNote(RichText rich, GameItemElement element)
        {
            if (rich == null || element == null) return rich;

            string note = null;
            for (int i = 0; i < _tooltipNotes.Count; i++)
            {
                if (!ReferenceEquals(_tooltipNotes[i].Key, element)) continue;
                note = _tooltipNotes[i].Value;
                break;
            }
            if (string.IsNullOrEmpty(note)) return rich;

            try
            {
                return new RichText(
                    (rich.nakedText ?? string.Empty) + "\n" + note,
                    rich.richTextStrikethrough,
                    rich.richTextUnderline,
                    (rich.richTextForeground ?? string.Empty) + "\n" + note,
                    rich.richTextBackground,
                    rich.recommendedWidth,
                    rich.recommendedHeight,
                    rich.isTruncated);
            }
            catch { return rich; }
        }

        private void ClearSamples()
        {
            ClearMatchHighlights();

            // 轮播换物品时不要结束提示框：否则每秒都会闪一下。
            // 新物品建好后 TickHover 会直接把内容换成鼠标下那件；
            // 真正关窗/离场时由 Destroy() 统一结束。
            _hoverElement = null;

            for (int i = 0; i < _samples.Count; i++)
            {
                var it = _samples[i];
                if (it == null) continue;
                try { if (it.parentInventory != null) it.parentInventory.Expel(it); } catch { }
                try { it.Destroy(); } catch { }
            }
            _samples.Clear();
            _outputElements.Clear();
            _tooltipNotes.Clear();

            // 兜底底色是物品图标的兄弟节点，不会随样本销毁，必须自己收
            for (int i = 0; i < _highlightOverlays.Count; i++)
            {
                if (_highlightOverlays[i] != null) UnityEngine.Object.Destroy(_highlightOverlays[i]);
            }
            _highlightOverlays.Clear();
        }

        /// <summary>
        /// 指针是否落在可视裁剪区（ScrollView 的 viewport / 面板宿主）内。
        /// 没有裁剪层时不做限制，保持原行为。
        /// </summary>
        internal bool PointerInsideViewport(Vector2 mouse, Camera cam)
        {
            if (_attachedRect == null) return false;

            var mask = _attachedRect.GetComponentInParent<UnityEngine.UI.RectMask2D>();
            var clip = mask != null ? mask.transform as RectTransform : null;
            if (clip == null) return true;

            return RectTransformUtility.RectangleContainsScreenPoint(clip, mouse, cam);
        }

        /// <summary>
        /// 每帧检查鼠标下的样本物品。
        /// 不能只靠 EventTrigger：配方每秒轮播，鼠标不动时旧物品被销毁、
        /// 新物品不会补发 PointerEnter，提示框就会消失或停在旧物品上。
        /// 这里按鼠标位置重新命中当前这批物品，轮播后也能立刻换成新物品的提示框。
        /// </summary>
        internal void TickHover()
        {
            try
            {
                Vector2 mouse = Input.mousePosition;
                bool hasPanel = _attachedRect != null && _samples.Count > 0;
                GameItemElement hit = null;

                if (hasPanel)
                {
                    Camera cam = null;
                    var canvas = _attachedRect.GetComponentInParent<Canvas>();
                    if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                        cam = canvas.worldCamera;

                    // 先确认指针在可视裁剪区内：滚出视口的物品仍然存在，
                    // 不拦一层会让画面外的格子照样能选中 / 弹提示框。
                    if (PointerInsideViewport(mouse, cam))
                    {
                        for (int i = 0; i < _samples.Count; i++)
                        {
                            var element = _samples[i] as GameItemElement;
                            if (element == null) continue;

                            // 真正带图标的是 background；handler 是整格根节点，尺寸可能不是物品大小
                            var rect = element.background != null
                                ? element.background.rectTransform
                                : element.rectTransform;
                            if (rect == null) continue;

                            if (RectTransformUtility.RectangleContainsScreenPoint(rect, mouse, cam))
                            {
                                hit = element;
                                break;
                            }
                        }
                    }
                }

                if (ModEntry.Log != null && Time.unscaledTime >= _hoverLogAt && _hoverLogs < 40)
                {
                    _hoverLogAt = Time.unscaledTime + 0.5f;
                    _hoverLogs++;
                    LogHoverState(mouse, hit);
                }

                if (hit == null)
                {
                    if (_tooltipShown)
                    {
                        _hoverElement = null;
                        _tooltipShown = false;
                        HideItemTooltip();
                    }
                }
                else if (!ReferenceEquals(hit, _hoverElement))
                {
                    _hoverElement = hit;
                    ShowItemTooltip(hit);
                }

                // 作弊模式：拖动复制一份物品，原物品留在格子里继续轮播。
                // 原版拖拽已通过 DisableVanillaDrag 关掉，这里只负责起手判定。
                if (Input.GetMouseButtonDown(0))
                {
                    _pressing = hit != null;
                    _pressElement = hit;
                    _pressPos = Input.mousePosition;
                    _takeDragStarted = false;
                }

                if (Input.GetMouseButtonUp(0))
                {
                    var clicked = _pressElement;
                    bool dragged = _takeDragStarted;

                    _pressing = false;
                    _pressElement = null;
                    _takeDragStarted = false;

                    // 没有拖动 = 左键点击：查来源
                    if (clicked != null && !dragged)
                        OpenLookup(clicked, false);
                }
                else if (_pressing && !_takeDragStarted && _pressElement != null)
                {
                    float dx = Input.mousePosition.x - _pressPos.x;
                    float dy = Input.mousePosition.y - _pressPos.y;
                    if (dx * dx + dy * dy >= 14f * 14f)
                    {
                        _takeDragStarted = true;
                        if (CatalogWindow.TakeMode)
                            CatalogWindow.BeginTakeDrag(_pressElement);
                    }
                }

                // 右键点击：查用途（和物品栏/图鉴的左右键语义一致）
                if (Input.GetMouseButtonUp(1) && hit != null)
                    OpenLookup(hit, true);
            }
            catch (Exception e) { Warn("tick hover: " + e.Message); }
        }

        /// <summary>诊断：打印面板/样本/鼠标/命中，每 0.5 秒最多一次。</summary>
        private void LogHoverState(Vector2 mouse, GameItemElement hit)
        {
            if (ModEntry.Log == null) return;

            var sb = new System.Text.StringBuilder();
            sb.Append("[mach-hover] panel=");
            if (_attachedRect == null) sb.Append("null");
            else sb.Append(_attachedRect.rect.size.ToString("F0"));
            sb.Append(" samples=").Append(_samples.Count);
            sb.Append(" mouse=").Append(mouse.ToString("F0"));
            sb.Append(" hit=").Append(hit == null ? "none" : hit.identifier);

            int n = Math.Min(3, _samples.Count);
            for (int i = 0; i < n; i++)
            {
                var element = _samples[i] as GameItemElement;
                sb.Append(" | ").Append(i).Append(':');
                if (element == null) { sb.Append("not-element"); continue; }

                sb.Append(element.identifier);

                var rect = element.background != null
                    ? element.background.rectTransform
                    : element.rectTransform;
                if (rect == null) { sb.Append(" rect=null"); continue; }

                var corners = new Vector3[4];
                rect.GetWorldCorners(corners);
                Canvas canvas = rect.GetComponentInParent<Canvas>();
                Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                    ? canvas.worldCamera : null;
                Vector2 min = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
                Vector2 max = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
                sb.Append(" scr=").Append(min.ToString("F0"))
                  .Append("..").Append(max.ToString("F0"))
                  .Append(" active=").Append(rect.gameObject.activeInHierarchy);
            }

            ModEntry.Log.Log(sb.ToString());
        }

        private void OpenLookup(GameItemElement element, bool asUse)
        {
            if (element == null) return;

            try
            {
                var entry = RecipeIndex.FindEntry(element.identifier);

                // 容器格：可查对象换成它灌装的液体（液体管理器统一判定）
                string liquid = LiquidManager.ContentLiquid(element);
                if (liquid != null)
                {
                    var liquidEntry = LiquidCatalog.Find(LiquidCatalog.EntryPrefix + liquid);
                    if (liquidEntry != null) entry = liquidEntry;
                }

                if (entry == null) return;

                RecipeWindow.OpenFor(entry, asUse);
            }
            catch (Exception e) { Warn("open lookup: " + e.Message); }
        }

        /// <summary>
        /// 预览物品只用来展示：关掉原版的点击 / 拖拽事件处理器，
        /// 避免把样本从预览 inventory 里拖走。作弊模式的复制拖拽由 TickHover 自己起手。
        /// </summary>
        private static void DisableVanillaDrag(GameItem item)
        {
            try
            {
                var element = item as GameItemElement;
                if (element == null || element.handler == null) return;

                var nodes = element.handler.rectTransform.GetComponentsInChildren<TreeNodeRender>(true);
                for (int i = 0; i < nodes.Length; i++)
                {
                    var node = nodes[i];
                    if (node == null) continue;

                    node.eventHandler = null;
                    if (node.image != null) node.image.raycastTarget = false;
                }
            }
            catch (Exception e) { Warn("disable drag: " + e.Message); }
        }

        /// <summary>
        /// 纯度的本地化显示名（游戏原版 mech_condition_*_purity）。
        /// 机器页左上角配方名用它拼「物品名（纯度）」。
        /// </summary>
        internal static string PurityDisplayName(string purity)
        {
            string key = PurityLocalizationKey(purity);
            if (key == null) return null;

            return MechLabel(key, FallbackPurityName(purity));
        }

        private static string PurityLocalizationKey(string purity)
        {
            // INGOT_PURITY_PERFECT → mech_condition_perfect_purity
            // 键名完全由纯度常量推导，游戏新增档位时不用改表。
            const string prefix = "INGOT_PURITY_";
            if (string.IsNullOrEmpty(purity) ||
                !purity.StartsWith(prefix, StringComparison.Ordinal))
                return null;

            return "mech_condition_" + purity.Substring(prefix.Length).ToLowerInvariant() + "_purity";
        }

        private static string FallbackPurityName(string purity)
        {
            // 游戏公开字典里的英文显示名（新增档位也能兜底）
            try
            {
                IngotPurityHelper.IngotPurity info;
                var table = IngotPurityHelper.ingotPurities;
                if (table != null && table.TryGetValue(purity, out info) && info != null)
                    return info.PurityDisplay;
            }
            catch { }

            return purity;
        }

        private void ShowItemTooltip(GameItemElement element)
        {
            if (element == null) return;

            try
            {
                RichText rich = element.GetTooltip();

                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[mach-tooltip] show id=" + element.identifier
                                     + " rich=" + (rich != null)
                                     + " overlay=" + (OverlayHandler.current != null));

                if (rich == null) return;
                rich = WithTooltipNote(rich, element);

                // 每帧重建：原版徽章 / 其它模组追加内容靠这个时间戳保持最新
                TooltipFollower.Begin(rich, ItemTooltipHandler.BASE_TOOLTIP_CHAR_WIDTH,
                    delegate
                    {
                        try
                        {
                            var rebuilt = element != null ? element.GetTooltip() : null;
                            if (element != null) rebuilt = WithTooltipNote(rebuilt, element);
                            return rebuilt;
                        }
                        catch { return null; }
                    });

                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[mach-tooltip] begin active=" + TooltipFollower.IsActive);

                _tooltipShown = true;
            }
            catch (Exception e) { Warn("show item tooltip: " + e); }
        }

        private static void HideItemTooltip()
        {
            // 只停 TooltipFollower 不够：OverlayHandler 已经把提示框显示出来了，
            // 必须显式 HideTooltip，否则鼠标移开后那块框会一直留在屏幕上。
            try { TooltipFollower.End(); } catch { }

            var overlay = OverlayHandler.current;
            if (overlay != null)
            {
                try { overlay.HideTooltip(); } catch { }
            }
        }

        private void Accept(GameInventory inv, string id, string purity = null)
        {
            if (inv == null || string.IsNullOrEmpty(id)) return;

            GameItem item = null;
            try { item = DirectoryMaster.Item(id, false); }   // isOwned=false：不写玩家存档
            catch (Exception e) { Warn("create '" + id + "': " + e.Message); }

            if (item != null) Accept(inv, item, purity);
        }

        /// <summary>把已经造好的预览实例（例如灌好液体的容器）放进指定 inventory。</summary>
        private void Accept(GameInventory inv, GameItem item, string purity = null)
        {
            if (inv == null || item == null) return;

            string id = item.identifier;
            try
            {
                // 预览实例仍不写存档，但提示框里不能显示「未拥有」——
                // 图鉴的探针也是用 SetItemOwned 标记成已拥有的。
                try { GeneralHelper.SetItemOwned(item, true); } catch { }

                // 成分随机的药品：机器面板里也统一显示实验室纯级（100% 活性物质），
                // 拿取和面板展示保持一致。
                try
                {
                    if (CatalogWindow.IsFullPurityItem(id))
                        CatalogWindow.ApplyFullPurity(item, id);
                }
                catch { }

                // 熔炉锭：把这一档的纯度写进样本物品（特性 / tooltip 由原版 SetPurity 接管）
                if (!string.IsNullOrEmpty(purity) &&
                    string.Equals(id, "metal_ingot", StringComparison.Ordinal))
                {
                    try { IngotPurityHelper.SetPurity(item, purity); }
                    catch (Exception e) { Warn("set purity '" + purity + "': " + e.Message); }
                }

                // 预览样本不执行「放入后」的副作用（模组/原版可能在这里放音效、改状态）
                try { item.onSlotAddItemFunc = null; } catch { }

                // 原版入格：算格子坐标 + 形状变换 + SetRenderingParent + tooltip 链
                int moved = GraphUtils.TryAccept(inv, item);
                if (moved > 0)
                {
                    _samples.Add(item);

                    var element = item as GameItemElement;
                    if (element != null && inv == _outputGrid) _outputElements.Add(element);

                    DisableVanillaDrag(item);
                }
                else
                {
                    // 没放进去的样本别泄漏（没 parentInventory，Destroy 自己）
                    try { item.Destroy(); } catch { }
                    if (ModEntry.Log != null)
                        ModEntry.Log.Warning("[vanilla-machine] item refused by grid: " + id);
                }
            }
            catch (Exception e) { Warn("accept '" + id + "': " + e.Message); }
        }

        /// <summary>
        /// 图鉴自己现建的演示机器实例（<see cref="AdoptOwned"/> 传入）；
        /// 面板销毁时一起销毁，不碰图鉴条目里的那份 Item。
        /// </summary>
        private GameItem _ownedItem;

        /// <summary>
        /// 借用**独立实例**的机器窗口：`item` 必须是调用方现建的演示样本，
        /// 面板销毁时会连实例一起 Destroy。Adopt 失败时立刻把实例销毁。
        /// </summary>
        internal bool AdoptOwned(GameItem item, string[] roles)
        {
            if (item == null) return false;

            _ownedItem = item;

            var window = item.contentWindow;
            if (Adopt(window, roles)) return true;

            DestroyOwnedItem();
            return false;
        }

        private void DestroyOwnedItem()
        {
            if (_ownedItem == null) return;

            try { _ownedItem.Destroy(); } catch { }
            _ownedItem = null;
        }

        /// <summary>
        /// 借用工厂造好的机器窗口（含模组对原版面板的修改，如枪匠改打印机输出网格）。
        ///
        /// <paramref name="roles"/> 是「只保留哪些面板」的白名单（沿用各机器手写壳的取法）；
        /// 传 null 表示保留所有有角色的面板，仅隐藏电池 / 纸条 / 控制按钮。
        /// 隐藏用原版 <c>SetHidden</c>，不动 panels 列表，<c>GetStorage</c> 索引不变。
        /// </summary>
        internal bool Adopt(PixelWindow window, string[] roles)
        {
            if (window == null) return false;

            var row = window.childElement as MachineryPanelRow;
            if (row == null) return false;

            try
            {
                _window = window;
                _borrowed = true;
                _borrowedRow = row;

                // 角色 → 面板里的容器（顺序与各机器 CreateMachineInventoryWindow 一致）
                _moduleGrid = ContentOf<GameGridInventory>(row, "module");
                _inputGrid = ContentOf<GameGridInventory>(row, "input");
                _outputGrid = ContentOf<GameGridInventory>(row, "output")
                              ?? ContentOf<GameGridInventory>(row, "eject");
                _inputSlot = ContentOf<GameSlotInventory>(row, "input");
                _outputSlot = ContentOf<GameSlotInventory>(row, "output")
                              ?? ContentOf<GameSlotInventory>(row, "eject");
                _chipSlot = ContentOf<GameSlotInventory>(row, "chip");
                _containerSlot = ContentOf<GameSlotInventory>(row, "container")
                                 ?? ContentOf<GameSlotInventory>(row, "water");
                _distillateSlot = ContentOf<GameSlotInventory>(row, "distillate");
                _seedSlot = ContentOf<GameSlotInventory>(row, "seed");
            }
            catch (Exception e)
            {
                // 连角色都读不到就真的不能借用，退回手写壳
                Warn("adopt machine window: " + e.GetType().Name + ": " + e.Message);
                _window = null;
                _borrowed = false;
                _borrowedRow = null;
                return false;
            }

            // 下面几步只影响外观 / 入格校验：失败也继续借用，不要退回手写壳
            try
            {
                var keep = new List<MachineryPanel>();
                if (roles != null && roles.Length > 0)
                {
                    for (int i = 0; i < roles.Length; i++)
                    {
                        var panel = row.GetPanel(roles[i]);
                        if (panel != null && !keep.Contains(panel)) keep.Add(panel);
                    }
                }

                var panels = row.Panels;
                for (int i = 0; i < panels.Count; i++)
                {
                    var panel = panels[i];
                    if (panel == null || panel.isHidden) continue;

                    // 白名单模式：不在名单里的一律隐藏；
                    // 默认模式：只隐藏没有角色或控制按钮的面板。
                    bool show = roles != null && roles.Length > 0
                        ? keep.Contains(panel)
                        : (!string.IsNullOrEmpty(panel.role) && panel.role != "control");
                    if (show) continue;

                    panel.SetHidden(true);
                    _hiddenByAdopt.Add(panel);
                }
            }
            catch (Exception e) { Warn("adopt hide panels: " + e.GetType().Name + ": " + e.Message); }

            try
            {
                // 真实机器格子会按功能过滤物品（打印机的输入仓只收特定标签），
                // 预览时统一放开，让配方样本能摆进去；Destroy 时还原。
                RelaxFilters(_moduleGrid);
                RelaxFilters(_inputGrid);
                RelaxFilters(_outputGrid);
                RelaxFilters(_inputSlot);
                RelaxFilters(_outputSlot);
                RelaxFilters(_chipSlot);
                RelaxFilters(_containerSlot);
                RelaxFilters(_distillateSlot);
                RelaxFilters(_seedSlot);

                // 工厂窗口可能被模组改过形状（枪匠 PrinterOutputPatch 就是改完再 Validate）
                window.Validate();

                if (ModEntry.Log != null && !_loggedAdopt)
                {
                    _loggedAdopt = true;
                    ModEntry.Log.Log("[adopt] module=" + Name(_moduleGrid)
                                     + " input=" + Name(_inputGrid) + "/" + Name(_inputSlot)
                                     + " output=" + Name(_outputGrid) + "/" + Name(_outputSlot)
                                     + " chip=" + Name(_chipSlot)
                                     + " container=" + Name(_containerSlot)
                                     + " distillate=" + Name(_distillateSlot)
                                     + " seed=" + Name(_seedSlot));
                }

                // AttachContentTo 会把内容搬进图鉴页面，先把原挂载点与布局记下来
                var node = window.childElement == null ? null : window.childElement.handler;
                _adoptedRect = node == null ? null : node.rectTransform;
                if (_adoptedRect != null)
                {
                    _adoptedParent = _adoptedRect.parent as RectTransform;
                    _adoptedAnchorMin = _adoptedRect.anchorMin;
                    _adoptedAnchorMax = _adoptedRect.anchorMax;
                    _adoptedPivot = _adoptedRect.pivot;
                    _adoptedSize = _adoptedRect.sizeDelta;
                    _adoptedPosition = _adoptedRect.anchoredPosition;
                    _adoptedScale = _adoptedRect.localScale;
                }
            }
            catch (Exception e) { Warn("adopt layout: " + e.GetType().Name + ": " + e.Message); }

            return true;
        }

        /// <summary>
        /// 按 role 取面板里的容器。
        /// 注意 <c>MachineHelper.Panel</c> 会把格子包一层 <see cref="MachineryIntake"/>，
        /// 所以 panel.content 通常不是 inventory 本身，必须再往里取一层。
        /// </summary>
        private static T ContentOf<T>(MachineryPanelRow row, string role) where T : class
        {
            var panel = row.GetPanel(role);
            var content = panel == null ? null : panel.content;

            var intake = content as MachineryIntake;
            if (intake != null) content = intake.content;

            return content as T;
        }

        /// <summary>
        /// 临时放开真实机器格子的入格过滤，并摘掉入格/出格回调：
        /// 那些回调会播放入格音效（battery_insert / 模块音效），预览时不该出声。
        /// Destroy 时全部还原。
        /// </summary>
        private void RelaxFilters(GameInventory inv)
        {
            if (inv == null) return;

            _borrowedHooks.Add(new HookSnapshot
            {
                Inv = inv,
                May = inv.mayInventoryAddItemFunc,
                Add = inv.onSlotAddItemFunc,
                Remove = inv.onSlotRemoveItemFunc
            });

            inv.mayInventoryAddItemFunc = AllowOnlyWhileFilling;
            inv.onSlotAddItemFunc = null;
            inv.onSlotRemoveItemFunc = null;
        }

        /// <summary>
        /// 机器面板统一排布（手写壳与借用窗口同一套）：
        ///   · 面板宽度基准 W = max(默认内容宽, 最宽的那一块面板)——
        ///     有比窗口更宽的面板时，窗口跟着加宽；
        ///   · 面板一个一个往里放，放不下就换行（用列数逼近：取总宽 ≤ W 的最大列数）。
        /// 返回面板在宿主坐标系里的宽度（≥ 默认宽）。
        /// </summary>
        internal float FitWidth(RectTransform host, float available)
        {
            var row = _borrowedRow;
            if (row == null && _window != null) row = _window.childElement as MachineryPanelRow;
            if (row == null || _window == null) return available;

            int visible = 0;
            var panels = row.Panels;
            for (int i = 0; i < panels.Count; i++)
                if (panels[i] != null && !panels[i].isHidden) visible++;

            if (visible < 1) return available;

            float ratio = CanvasRatio(host);
            if (ratio <= 0.001f) ratio = 1f;

            // 单列时的宽度 = 最宽的那一块面板（实测内容宽，不含窗口留白）
            row.SetColumns(1);
            _window.Validate();
            float widest = ContentWidthOrBox(ratio);

            float cap = MaxWinWidth - 40f;

            // 目标 = 页面可用宽（单块面板更宽时取它），再留 8% 的余量给列等宽造成的临界差。
            float target = Mathf.Min(Mathf.Max(available, widest), cap) * 1.08f;

            // 从「全部排一行」往下试，取总宽不超过目标的最大列数；
            // 实在放不下（单块就比上限还宽）才退到单列。
            int chosen = 1;
            float contentW = Mathf.Min(widest, cap);
            for (int cols = visible; cols >= 1; cols--)
            {
                row.SetColumns(cols);
                _window.Validate();
                float w = ContentWidthOrBox(ratio);
                if (w <= target + 0.5f || cols == 1)
                {
                    chosen = cols;
                    contentW = Mathf.Min(w, cap);
                    break;
                }
            }

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[machine-fit] panels=" + visible + " widest=" + widest.ToString("F1")
                                 + " cols=" + chosen + " content=" + contentW.ToString("F1")
                                 + " host=" + available.ToString("F1")
                                 + " target=" + target.ToString("F1"));

            return Mathf.Max(contentW, available);
        }

        /// <summary>内容实测宽度（乘上画布比例）；量不到时退回 widthPixels。</summary>
        private float ContentWidthOrBox(float ratio)
        {
            float measured = MeasureContentWidth();
            if (measured > 10f) return measured * ratio;

            return _window == null ? 0f : _window.widthPixels * ratio;
        }

        /// <summary>
        /// 把借用的内容根还回原窗口并恢复布局，但不销毁窗口。
        /// 图鉴每轮渲染都会 ClearContainer 销毁旧节点——借来的面板行必须在
        /// 那之前还回去，否则工厂窗口的 UI 会跟着一起被销毁，下次就借不到了。
        /// </summary>
        internal void RestoreContent()
        {
            if (!_borrowed) return;
            RestoreAdopted();
        }

        /// <summary>借用窗口的内容根被挂进了图鉴页面：还原挂载点与布局，绝不销毁窗口。</summary>
        private void RestoreAdopted()
        {
            // 还原入格过滤与入格/出格回调
            for (int i = 0; i < _borrowedHooks.Count; i++)
            {
                var h = _borrowedHooks[i];
                if (h.Inv == null) continue;

                h.Inv.mayInventoryAddItemFunc = h.May;
                h.Inv.onSlotAddItemFunc = h.Add;
                h.Inv.onSlotRemoveItemFunc = h.Remove;
            }
            _borrowedHooks.Clear();

            // 先把借用时隐藏的面板恢复（SetHidden 会自行触发行重排）
            for (int i = 0; i < _hiddenByAdopt.Count; i++)
                if (_hiddenByAdopt[i] != null) _hiddenByAdopt[i].SetHidden(false);
            _hiddenByAdopt.Clear();

            if (_adoptedRect == null) return;

            try
            {
                var parent = _adoptedParent != null
                    ? _adoptedParent
                    : (_window != null && _window.handler != null ? _window.handler.rectTransform : null);

                _adoptedRect.SetParent(parent, false);
                _adoptedRect.anchorMin = _adoptedAnchorMin;
                _adoptedRect.anchorMax = _adoptedAnchorMax;
                _adoptedRect.pivot = _adoptedPivot;
                _adoptedRect.sizeDelta = _adoptedSize;
                _adoptedRect.anchoredPosition = _adoptedPosition;
                _adoptedRect.localScale = _adoptedScale;
            }
            catch { }
        }

        internal void Destroy()
        {
            try
            {
                ClearSamples();
                _tooltipShown = false;
                HideItemTooltip();
                _moduleGrid = null;
                _inputGrid = null;
                _outputGrid = null;
                _inputSlot = null;
                _outputSlot = null;
                _containerSlot = null;
                _distillateSlot = null;
                _productSlot = null;
                _outputDiskSlot = null;
                _seedSlot = null;
                _chipSlot = null;
                _desqInputSlot = null;
                _desqOutputSlot = null;
                _gunSlots = null;
                if (_borrowed) RestoreAdopted();
                else if (_window != null && _window.rectTransform != null)
                    UnityEngine.Object.Destroy(_window.rectTransform.gameObject);

                // 独立演示实例：窗口已还回去，现在可以安全销毁
                DestroyOwnedItem();
            }
            catch { }
            _window = null;
            _borrowed = false;
            _borrowedRow = null;
            _hiddenByAdopt.Clear();
            _adoptedRect = null;
            _adoptedParent = null;
            _attachedRect = null;
        }

        internal static string MechLabel(string key, string fallback)
        {
            string s = null;
            try { s = LocHelper.GetLocalizedMechanicOrDefault(key, null); } catch { }
            if (string.IsNullOrEmpty(s) ||
                string.Equals(s, key, StringComparison.OrdinalIgnoreCase) ||
                s.IndexOf("Translation Error", StringComparison.OrdinalIgnoreCase) >= 0)
                return fallback;
            return s;
        }

        private static void Warn(string msg)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("[vanilla-machine] " + msg);
        }
    }
}
