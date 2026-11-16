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

        /// <summary>
        /// 原版面板经 Canvas scale 补偿后的显示尺寸（造完壳就能算，用来给 AddSpace 占位）。
        /// maxHeight &gt; 0 时按可用高度封顶：窗口装不下的比例要往回收，
        /// 否则 802px 的面板会把机器样式窗口撑高、挤扁标题栏和关闭按钮。
        /// </summary>
        internal Vector2 ScaledSize(RectTransform host, float maxHeight = 0f)
        {
            float ratio = CanvasRatio(host, maxHeight);
            float w = (_window != null && _window.widthPixels > 1f) ? _window.widthPixels : 160f;
            float h = (_window != null && _window.heightPixels > 1f) ? _window.heightPixels : 401f;
            return new Vector2(w * ratio, h * ratio);
        }

        private float CanvasRatio(RectTransform host, float maxHeight = 0f)
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

                // 按可用高度封顶：面板缩放后不能高过窗口能给出的空间
                if (maxHeight > 0f && _window != null && _window.heightPixels > 1f)
                {
                    float limit = maxHeight / _window.heightPixels;
                    if (limit > 0.2f && limit < ratio) ratio = limit;
                }
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
        internal float MatchGameCanvasScale(RectTransform host, float maxHeight = 0f)
        {
            if (_attachedRect == null || host == null) return 0f;
            try
            {
                // 比例走 CanvasRatio：Canvas 差异补偿 + 可用高度封顶（maxHeight>0 时），
                // 保证面板缩放后不会高过窗口能给出的空间、把标题栏挤扁。
                float ratio = CanvasRatio(host, maxHeight);

                // 只改缩放，anchor/pivot 已在 AttachContentTo 设成正中心，这里不碰
                _attachedRect.localScale = new Vector3(ratio, ratio, 1f);

                // 用 Validate() 算好的 widthPixels/heightPixels 作为面板真实尺寸——
                // _attachedRect（childElement.handler）是空布局壳，rect.size 常为 0，
                // 之前用它算高度 → host≈0 → 窗口特别矮。
                float nativeW = _window != null && _window.widthPixels > 1f
                    ? _window.widthPixels : _attachedRect.rect.width;
                float nativeH = _window != null && _window.heightPixels > 1f
                    ? _window.heightPixels : _attachedRect.rect.height;
                float scaledHeight = nativeH * ratio;

                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[vanilla-machine] ratio=" + ratio
                                     + " maxH=" + maxHeight
                                     + " native=" + nativeW + "x" + nativeH
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
