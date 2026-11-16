using System;
using System.Collections.Generic;
using UnityEngine;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 用游戏自己的控件造一台机器的原版界面：GameGridInventory / GameSlotInventory
    /// + MachineHelper.Panel/PowerPanel/NotePanel + CreateMachineryWindow，
    /// 再往真 inventory 里塞样本 GameItem——格子、物品图标、堆叠、提示、拖拽全是原生的。
    ///
    /// 这是「照抄机器」：不自己画格子框，直接复用游戏窗口那套控件。
    /// </summary>
    internal sealed class VanillaMachinePanel
    {
        private PixelWindow _window;
        private readonly List<GameItem> _samples = new List<GameItem>();
        private readonly List<GameInventory> _inventories = new List<GameInventory>();

        internal bool IsAlive { get { return _window != null && !IsDestroyed(_window.rectTransform); } }

        /// <summary>
        /// 造熔炉的原版窗口并塞入一条配方的物品。
        /// 布局完全照 MachineFurnace.CreateMachineInventoryWindow 的 IL：
        ///   PowerPanel(电池) / Panel(模块, 7 列形状网格) / Panel(投入, 9x6) /
        ///   Panel(产出, 9x6) / NotePanel(纸条)。
        /// </summary>
        internal bool BuildFurnace(string[] inputs, string moduleId, string[] outputs)
        {
            try
            {
                // 电池 / 纸条：空槽
                var battery = new GameSlotInventory();
                var note = new GameSlotInventory();

                // 模块槽：7 列形状网格（熔炉 IL 里的形状串）
                var moduleGrid = new GameGridInventory("10000010001000001110000010001000001", 7);
                moduleGrid.SetMachineryStyle();

                // 投入 / 产出：9x6 网格
                var inputGrid = new GameGridInventory(9, 6);
                var outputGrid = new GameGridInventory(9, 6);

                _inventories.Add(moduleGrid);
                _inventories.Add(inputGrid);
                _inventories.Add(outputGrid);

                // 样本物品：按角色塞进对应 inventory（UncheckedAccept 不看形状）
                if (!string.IsNullOrEmpty(moduleId)) Accept(moduleGrid, moduleId);
                if (inputs != null)
                    for (int i = 0; i < inputs.Length; i++) Accept(inputGrid, inputs[i]);
                if (outputs != null)
                    for (int i = 0; i < outputs.Length; i++) Accept(outputGrid, outputs[i]);

                var panels = new[]
                {
                    MachineHelper.PowerPanel(battery),
                    MachineHelper.Panel(Loc("mech_furnace_label_module", "模块"), moduleGrid, "module", true),
                    MachineHelper.Panel(Loc("mech_furnace_label_input", "投入"), inputGrid, "input", true),
                    MachineHelper.Panel(Loc("mech_furnace_label_output", "产出"), outputGrid, "output", true),
                    MachineHelper.NotePanel(note)
                };

                _window = MachineHelper.CreateMachineryWindow(false, panels);
                return _window != null;
            }
            catch (Exception e)
            {
                RecipeUIWarn("vanilla furnace: " + e.Message);
                return false;
            }
        }

        /// <summary>按 id 造一个不拥有的样本 GameItem，塞进 inventory。失败只警告，不影响其它物品。</summary>
        private void Accept(GameInventory inv, string id)
        {
            if (inv == null || string.IsNullOrEmpty(id)) return;
            try
            {
                var item = DirectoryMaster.Item(id, false);
                if (item == null) return;
                if (inv.UncheckedAccept(item)) _samples.Add(item);
            }
            catch (Exception e) { RecipeUIWarn("vanilla accept '" + id + "': " + e.Message); }
        }

        /// <summary>把原版窗口的内容节点挂到指定容器里（窗口自身的框/标题栏不要，只嵌面板行）。</summary>
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
                rect.anchoredPosition = Vector2.zero;
                return rect;
            }
            catch (Exception e)
            {
                RecipeUIWarn("vanilla attach: " + e.Message);
                return null;
            }
        }

        /// <summary>销毁窗口与样本，释放渲染节点。</summary>
        internal void Destroy()
        {
            try
            {
                for (int i = 0; i < _samples.Count; i++)
                {
                    var it = _samples[i];
                    if (it == null) continue;
                    try { it.parentInventory.Expel(it); } catch { }
                    try { it.Destroy(); } catch { }
                }
                _samples.Clear();
                _inventories.Clear();

                if (_window != null && !IsDestroyed(_window.rectTransform))
                    UnityEngine.Object.Destroy(_window.rectTransform.gameObject);
            }
            catch { }
            _window = null;
        }

        private static bool IsDestroyed(UnityEngine.Object o)
        {
            if (o == null) return true;
            try { return !o; } catch { return true; }
        }

        private static string Loc(string key, string fallback)
        {
            string s = null;
            try { s = ModEntry.T(key); } catch { }
            return string.IsNullOrEmpty(s) ? fallback : s;
        }

        private static void RecipeUIWarn(string msg)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("[vanilla-machine] " + msg);
        }
    }
}
