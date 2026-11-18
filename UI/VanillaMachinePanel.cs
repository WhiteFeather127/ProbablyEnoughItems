using System;
using System.Collections.Generic;
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

        // 诊断日志限流：tooltip 排查时每 0.5 秒最多打一行
        private float _hoverLogAt;

        // 各 inventory 由谁装填（面板角色 → inventory）
        private GameGridInventory _moduleGrid;
        private GameGridInventory _inputGrid;
        private GameGridInventory _outputGrid;

        // 搅拌机的容器槽（GameSlotInventory，原版放液体容器的那一格）
        private GameSlotInventory _containerSlot;

        /// <summary>只有 Fill 自己往预览格塞物品时允许入格；外部拖拽一律拒绝。</summary>
        private bool _filling;

        /// <summary>当前物品在面板里的绿底（直接用原版 HighlightSlot 生成的节点）。</summary>
        private readonly List<TreeNodeRender> _matchHighlights = new List<TreeNodeRender>();

        private RectTransform _attachedRect;

        internal RectTransform AttachedRect { get { return _attachedRect; } }

        /// <summary>
        /// 原版面板经 Canvas scale 补偿后的显示尺寸（造完壳就能算，用来给 AddSpace 占位）。
        /// maxHeight &gt; 0 时按可用高度封顶：窗口装不下的比例要往回收，
        /// 否则 802px 的面板会把机器样式窗口撑高、挤扁标题栏和关闭按钮。
        /// </summary>
        internal Vector2 ScaledSize(RectTransform host, float maxHeight = 0f, float maxWidth = 0f)
        {
            float ratio = CanvasRatio(host, maxHeight, maxWidth);
            float w = (_window != null && _window.widthPixels > 1f) ? _window.widthPixels : 160f;
            float h = (_window != null && _window.heightPixels > 1f) ? _window.heightPixels : 401f;
            return new Vector2(w * ratio, h * ratio);
        }

        private float CanvasRatio(RectTransform host, float maxHeight = 0f, float maxWidth = 0f)
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

                // 按可用宽度封顶：横向并排（搅拌机两槽）时不能宽过内容列
                if (maxWidth > 0f && _window != null && _window.widthPixels > 1f)
                {
                    float limit = maxWidth / _window.widthPixels;
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
        /// 实测面板里**内容**的真实高度（世界包围盒）。
        /// 面板的 heightPixels 含底部留白（面板行 padding / yPadding），
        /// 按 rect 高度布局会让内容只占上半部分，多出来的空白就落在面板与翻页条之间。
        /// </summary>
        internal float MeasureContentHeight()
        {
            float minY, maxY;
            if (!MeasureContentBounds(out minY, out maxY)) return 0f;
            float scaleY = Mathf.Abs(_attachedRect.lossyScale.y) > 0.001f
                ? _attachedRect.lossyScale.y : 1f;
            float measured = (maxY - minY) / scaleY;
            return (measured < 10f || measured > 4000f) ? 0f : measured;
        }

        /// <summary>内容包围盒的世界 Y 范围（用来把内容对齐到容器中心）。</summary>
        internal bool MeasureContentBounds(out float minY, out float maxY)
        {
            minY = float.MaxValue;
            maxY = float.MinValue;
            if (_attachedRect == null) return false;
            try
            {
                var all = _attachedRect.GetComponentsInChildren<RectTransform>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    var rt = all[i];
                    if (rt == null || rt == _attachedRect) continue;
                    if (!rt.gameObject.activeInHierarchy) continue;

                    var c = new Vector3[4];
                    rt.GetWorldCorners(c);
                    if (c[0].y < minY) minY = c[0].y;
                    if (c[1].y > maxY) maxY = c[1].y;
                }
                return maxY > minY;
            }
            catch { return false; }
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
        internal float MatchGameCanvasScale(RectTransform host, float maxHeight = 0f, float maxWidth = 0f)
        {
            if (_attachedRect == null || host == null) return 0f;
            try
            {
                // 比例走 CanvasRatio：Canvas 差异补偿 + 可用高度封顶（maxHeight>0 时），
                // 保证面板缩放后不会高过窗口能给出的空间、把标题栏挤扁。
                float ratio = CanvasRatio(host, maxHeight, maxWidth);

                // 只改缩放，anchor/pivot 已在 AttachContentTo 设成正中心，这里不碰
                _attachedRect.localScale = new Vector3(ratio, ratio, 1f);

                // 真实高度优先用**实测内容包围盒**：heightPixels 含底部留白，
                // 按它布局会让内容只占上半部分、多出来的空白落在面板与翻页条之间。
                float nativeW = _window != null && _window.widthPixels > 1f
                    ? _window.widthPixels : _attachedRect.rect.width;
                float boxH = _window != null && _window.heightPixels > 1f
                    ? _window.heightPixels : _attachedRect.rect.height;
                float measuredH = MeasureContentHeight();
                bool useMeasured = measuredH > 10f && measuredH < boxH;
                float nativeH = useMeasured ? measuredH : boxH;

                // ① 先把 rect 高度压到实测内容高度：rect 比内容高时，
                //    rect × ratio 会大于容器高度、向下溢出被 RectMask2D 裁（面板被遮）。
                // ② 再把面板整体平移，让内容包围盒中心与容器中心重合
                //    （面板行有 padding，内容在 rect 里并不居中）。
                if (useMeasured)
                {
                    var sd = _attachedRect.sizeDelta;
                    _attachedRect.sizeDelta = new Vector2(sd.x, measuredH);

                    float minY, maxY;
                    if (MeasureContentBounds(out minY, out maxY) && maxY > minY)
                    {
                        var c = new Vector3[4];
                        _attachedRect.GetWorldCorners(c);
                        float rectCenterY = (c[0].y + c[1].y) * 0.5f;
                        float contentCenterY = (minY + maxY) * 0.5f;
                        float dy = rectCenterY - contentCenterY;
                        if (Mathf.Abs(dy) > 0.5f)
                        {
                            var pos = _attachedRect.position;
                            _attachedRect.position = new Vector3(pos.x, pos.y + dy, pos.z);
                        }
                    }
                }

                float scaledHeight = nativeH * ratio;

                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[vanilla-machine] ratio=" + ratio
                                     + " box=" + nativeW + "x" + boxH
                                     + " content=" + measuredH
                                     + " used=" + nativeH
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
                           string purity = null, string container = null)
        {
            ClearSamples();

            _filling = true;
            try
            {
                if (modules != null)
                    for (int i = 0; i < modules.Length; i++) Accept(_moduleGrid, modules[i]);
                if (inputs != null)
                    for (int i = 0; i < inputs.Length; i++) Accept(_inputGrid, inputs[i]);
                if (outputs != null)
                    for (int i = 0; i < outputs.Length; i++) Accept(_outputGrid, outputs[i], purity);

                // 搅拌机：容器槽放一只液体容器（默认 bottled_water），
                // 并把投入的液体成分灌进去——否则预览出来只是一只空瓶。
                if (!string.IsNullOrEmpty(container) && _containerSlot != null)
                {
                    var liquidContainer = CatalogWindow.CreatePreviewItem(container);
                    if (liquidContainer == null)
                    {
                        Accept(_containerSlot, container);
                    }
                    else
                    {
                        if (inputs != null && inputs.Length > 0)
                            BlendIntoPreview(liquidContainer, inputs[0]);

                        Accept(_containerSlot, liquidContainer);
                    }
                }
            }
            finally { _filling = false; }
        }

        /// <summary>
        /// 把投入物品的液体成分灌进容器样本。直接用游戏自己的 BlendInto，
        /// 保证容器图标 / 提示框显示的是搅拌后的内容物。
        /// loss = 0：图鉴只展示成分，不模拟模块损失。
        /// </summary>
        private static void BlendIntoPreview(GameItem container, string inputId)
        {
            if (container == null || string.IsNullOrEmpty(inputId)) return;

            GameItem chemical = null;
            try
            {
                chemical = CatalogWindow.CreatePreviewItem(inputId);
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
        /// 容器槽样本的成分文本；格式和成分检测器一致，由 LiquidManager 统一生成。
        /// mainOnly = true 时只写总体积 + 主要成分名。
        /// </summary>
        internal string ContainerCompositionText(bool mainOnly = false, string mainName = null)
        {
            GameItem container = null;
            if (_containerSlot != null)
            {
                for (int i = 0; i < _samples.Count; i++)
                {
                    var it = _samples[i];
                    if (it != null && it.parentInventory == _containerSlot)
                    {
                        container = it;
                        break;
                    }
                }
            }

            return LiquidManager.FormatComposition(container, mainOnly, mainName);
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

                var inventory = element.parentInventory;
                if (inventory == null) continue;

                try
                {
                    var slot = inventory.GetIncompleteItemInventorySlot(element);
                    var nodes = inventory.HighlightSlot(slot, RenderHandler.BackgroundColor.Green, true);
                    if (nodes != null) _matchHighlights.AddRange(nodes);
                }
                catch (Exception e) { Warn("highlight item: " + e.Message); }
            }
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

                if (ModEntry.Log != null && Time.unscaledTime >= _hoverLogAt)
                {
                    _hoverLogAt = Time.unscaledTime + 0.5f;
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

                // 每帧重建：原版徽章 / 其它模组追加内容靠这个时间戳保持最新
                TooltipFollower.Begin(rich, ItemTooltipHandler.BASE_TOOLTIP_CHAR_WIDTH,
                    delegate
                    {
                        try { return element != null ? element.GetTooltip() : null; }
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

                // 原版入格：算格子坐标 + 形状变换 + SetRenderingParent + tooltip 链
                int moved = GraphUtils.TryAccept(inv, item);
                if (moved > 0)
                {
                    _samples.Add(item);
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
                _containerSlot = null;
                if (_window != null && _window.rectTransform != null)
                    UnityEngine.Object.Destroy(_window.rectTransform.gameObject);
            }
            catch { }
            _window = null;
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
