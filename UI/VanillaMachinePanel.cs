using System;
using System.Collections.Generic;
using UnityEngine;

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

        // 各 inventory 由谁装填（面板角色 → inventory）
        private GameGridInventory _moduleGrid;
        private GameGridInventory _inputGrid;
        private GameGridInventory _outputGrid;

        private RectTransform _attachedRect;

        internal RectTransform AttachedRect { get { return _attachedRect; } }

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

                // 保留游戏原版的窗口尺寸（widthPixels 由 UISettings.gridSize × 格数算出），
                // 不拉伸、不缩放——缩放会破坏格子里物品的坐标。
                // 只把锚点设成顶部居中，让原版面板在详情窗口里居中显示。
                rect.SetParent(host, false);
                rect.localScale = Vector3.one;
                rect.anchorMin = new Vector2(0.5f, 1f);
                rect.anchorMax = new Vector2(0.5f, 1f);
                rect.pivot = new Vector2(0.5f, 1f);
                rect.anchoredPosition = Vector2.zero;
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
        /// 布局定稿后往真 inventory 塞样本物品。
        /// 必须走 GraphUtils.TryAccept（不是 inventory.UncheckedAccept）：
        /// 原版入格流程 TryFindOneValidInventorySlot → SlotMarker.TryAcceptOnce 会用
        /// GridShapeBuilder.SetTransform 算出物品在网格里的坐标并 set_modifiedShape，
        /// 直接 UncheckedAccept 跳过这步，物品坐标停在 (0,0) 堆在角落、浮到格子外。
        /// 走完整流程后物品渲染节点按格子坐标摆放，tooltip 也由原版 GameItem 接管。
        /// </summary>
        internal void Fill(string[] inputs, string moduleId, string[] outputs)
        {
            ClearSamples();
            if (!string.IsNullOrEmpty(moduleId)) Accept(_moduleGrid, moduleId);
            if (inputs != null)
                for (int i = 0; i < inputs.Length; i++) Accept(_inputGrid, inputs[i]);
            if (outputs != null)
                for (int i = 0; i < outputs.Length; i++) Accept(_outputGrid, outputs[i]);
        }

        private void ClearSamples()
        {
            for (int i = 0; i < _samples.Count; i++)
            {
                var it = _samples[i];
                if (it == null) continue;
                try { if (it.parentInventory != null) it.parentInventory.Expel(it); } catch { }
                try { it.Destroy(); } catch { }
            }
            _samples.Clear();
        }

        private void Accept(GameInventory inv, string id)
        {
            if (inv == null || string.IsNullOrEmpty(id)) return;
            try
            {
                var item = DirectoryMaster.Item(id, false);   // isOwned=false：不写玩家存档
                if (item == null) return;

                // 原版入格：算格子坐标 + 形状变换 + SetRenderingParent + tooltip 链
                int moved = GraphUtils.TryAccept(inv, item);
                if (moved > 0)
                {
                    _samples.Add(item);
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

        internal void Destroy()
        {
            try
            {
                ClearSamples();
                _moduleGrid = null;
                _inputGrid = null;
                _outputGrid = null;
                if (_window != null && _window.rectTransform != null)
                    UnityEngine.Object.Destroy(_window.rectTransform.gameObject);
            }
            catch { }
            _window = null;
            _attachedRect = null;
        }

        private static string MechLabel(string key, string fallback)
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
