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
        /// 电池 / 模块(形状网格) / 投入(9x6) / 产出(9x6) / 纸条。
        /// 纸条槽按用户要求不显示；面板行 SetColumns(1) 竖排换行。
        /// </summary>
        internal bool BuildFurnaceShell()
        {
            try
            {
                var battery = new GameSlotInventory();

                _moduleGrid = new GameGridInventory("10000010001000001110000010001000001", 7);
                _moduleGrid.SetMachineryStyle();
                _inputGrid = new GameGridInventory(9, 6);
                _outputGrid = new GameGridInventory(9, 6);

                var panels = new[]
                {
                    MachineHelper.PowerPanel(battery),
                    MachineHelper.Panel(MechLabel("mech_furnace_label_module", "模块"), _moduleGrid, "module", true),
                    MachineHelper.Panel(MechLabel("mech_furnace_label_input", "投入"), _inputGrid, "input", true),
                    MachineHelper.Panel(MechLabel("mech_furnace_label_output", "产出"), _outputGrid, "output", true)
                    // 纸条槽不显示（用户要求）
                };

                var row = new MachineryPanelRow(panels);
                row.SetColumns(1);   // 竖排

                _window = new PixelWindow(false, null, WindowStyle.Machinery);
                _window.Attach(row);
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

                rect.SetParent(host, false);
                rect.localScale = Vector3.one;
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(1f, 1f);
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

        /// <summary>把原版窗口内容等比放大到指定宽度并左右贴边（原版固定宽约 130px，太窄）。</summary>
        internal void ScaleToWidth(float width)
        {
            if (_attachedRect == null || width <= 0f) return;
            try
            {
                float native = _attachedRect.rect.width;
                if (native <= 1f) return;
                float scale = width / native;
                if (scale < 1f) scale = 1f;   // 只放大，不缩小
                _attachedRect.localScale = new Vector3(scale, scale, 1f);

                // 缩放后 pivot 在顶部中央，居中贴齐左右边缘
                _attachedRect.anchorMin = new Vector2(0.5f, 1f);
                _attachedRect.anchorMax = new Vector2(0.5f, 1f);
                _attachedRect.anchoredPosition = new Vector2(0f, _attachedRect.anchoredPosition.y);
            }
            catch (Exception e) { Warn("scale: " + e.Message); }
        }

        /// <summary>
        /// 布局定稿后往真 inventory 塞样本物品。UncheckedAccept 会走原版的
        /// SetRenderingParent / onSlotAddItemFunc，物品节点按格子坐标摆放，
        /// 悬停 tooltip 也由原版 GameItem 事件链接管。
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
                if (inv.UncheckedAccept(item)) _samples.Add(item);
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
