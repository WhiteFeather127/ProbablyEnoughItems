using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 「图鉴」启动按钮。
    ///
    /// 定位方式和 <see cref="CatalogWindow.ApplyPlacement"/> 完全一致：
    /// 把**物品栏窗口的世界矩形**归一化到**同一个 UI 层（画布根）**上用锚点贴住 ——
    /// 图鉴面板的左边缘就是这么和物品栏右边缘对齐的，按钮也走同一条路。
    ///
    /// 曾经试过的两种做法都不要再用：
    ///   ① 挂在物品栏窗口节点里 → 该节点是物品树的格子层（TreeNodeRender + RectMask2D），
    ///      窗口背景是它之后的兄弟节点，按钮被盖住看不见；拖动按钮还会把窗口一起拖走。
    ///   ② 不用归一化、直接算父节点本地坐标 → 父节点常是 0×0 或 pivot 在中心的空壳，
    ///      算出来的位置直接跑到左上角。
    /// </summary>
    internal static class LauncherButton
    {
        private const string ButtonName = "ItemCatalog.LaunchButton";
        private const float ButtonWidth = 100f;
        private const float ButtonHeight = 30f;
        private const float Gap = 4f;

        private static CustomUIElement _buttonElement;
        private static string _buttonLabel;

        private static RectTransform _buttonRoot;   // 按钮容器
        private static RectTransform _dockParent;   // 所在的 UI 层（画布根）
        private static string _dockedPath;          // 当前贴靠的窗口路径（仅用于日志）

        private static Vector2 _lastAnchor;
        private static bool _lastInside;
        private static float _nextDockCheckAt;
        private static float _nextCanvasSkipLogAt;

        internal static void OnGameLoaded()
        {
            CatalogWindow.ResetForScene();

            try
            {
                Inject();
            }
            catch (Exception e)
            {
                if (ModEntry.Log != null) ModEntry.Log.Error("failed to inject launch button: " + e);
            }
        }

        private static void Inject()
        {
            var manager = CustomUIManager.Instance;
            if (manager == null) { Warn("CustomUIManager is not ready"); return; }

            // 只认真正的物品栏窗口节点，不接受画布兜底（画布兜底会把按钮甩到屏幕角）
            bool dockPocket;
            var target = ResolveDockTarget(out dockPocket);
            if (target == null)
            {
                Warn("no inventory window resolved, will retry on next panel event");
                return;
            }

            // 和图鉴面板同一个层：面板自己的 RectTransform 父节点就是画布根
            var parent = AnchorTarget.RootCanvas(target);
            if (parent == null) { Warn("dock target has no canvas root"); return; }

            _dockedPath = DockedLayout.HierarchyPath(target);

            var existing = parent.Find(ButtonName) as RectTransform;
            if (existing != null && existing != _buttonRoot)
            {
                UnityEngine.Object.Destroy(existing.gameObject);
                existing = null;
            }

            // 残留（换画布、上次贴错位置）一律清掉，否则会多出一份且永远可点
            try
            {
                var allStale = UnityEngine.Object.FindObjectsOfType<GameObject>();
                for (int i = 0; i < allStale.Length; i++)
                {
                    var stale = allStale[i];
                    if (stale == null) continue;
                    if (stale.name != ButtonName && stale.name != "ItemCatalog.LoadBar") continue;
                    if (stale.transform.IsChildOf(parent)) continue;
                    UnityEngine.Object.Destroy(stale);
                }
            }
            catch (Exception e) { Warn("cleanup leftovers: " + e.Message); }

            var bounds = DockedLayout.ToWorldRect(target);
            if (bounds.width < 2f || bounds.height < 2f)
            {
                Warn("inventory window has no size yet, retry on next panel event");
                return;
            }

            var rect = existing;
            if (rect == null)
            {
                var go = new GameObject(ButtonName, typeof(RectTransform));
                rect = (RectTransform)go.transform;
                rect.SetParent(parent, false);
            }

            _buttonRoot = rect;
            _dockParent = parent;
            PlaceAt(rect, parent, bounds, dockPocket);

            // 吞掉按下/拖拽：按钮和图鉴面板同层，不拦的话拖按钮会牵动下面的窗口
            try
            {
                if (rect.GetComponent<DragBlocker>() == null)
                    rect.gameObject.AddComponent<DragBlocker>();
            }
            catch (Exception e) { Warn("drag blocker: " + e.Message); }

            if (_buttonElement == null || _buttonElement.Rect == null || _buttonElement.Rect.parent != rect)
            {
                var builder = manager.BuildInto(rect, false, ModEntry.MOD_ID + ".button");
                builder.AddButton(ModEntry.T("btn_open"), OpenCatalog, "open");
                CustomUIBuilder.RebuildLayout(rect);

                var element = builder.Last;
                if (element != null && element.Rect != null)
                {
                    // 容器没有布局组时按钮可能是 0 尺寸，这里兜底撑开
                    if (element.Rect.rect.width < 1f || element.Rect.rect.height < 1f)
                        element.Rect.sizeDelta = new Vector2(ButtonWidth, ButtonHeight);

                    _buttonElement = element;

                    try
                    {
                        var text = element.Rect.GetComponentInChildren<TMPro.TMP_Text>(true);
                        _buttonLabel = text == null ? null : text.text;
                    }
                    catch { _buttonLabel = null; }
                }
            }

            // 新建的元素 layer 默认回到 0，统一刷成窗口那一层
            try
            {
                var parts = rect.GetComponentsInChildren<Transform>(true);
                int layer = target.gameObject.layer;
                rect.gameObject.layer = layer;
                for (int i = 0; i < parts.Length; i++) parts[i].gameObject.layer = layer;
            }
            catch { }

            if (ModEntry.Log != null)
            {
                ModEntry.Log.Log(string.Format(
                    "[button] place target='{0}' parent='{1}' bounds={2:F0}x{3:F0}@({4:F0},{5:F0})"
                    + " anchor=({6:F3},{7:F3}) pivot={8} corner={9}",
                    _dockedPath, DockedLayout.HierarchyPath(parent),
                    bounds.width, bounds.height, bounds.x, bounds.y,
                    _lastAnchor.x, _lastAnchor.y,
                    _lastInside ? "(1,0)" : "(0,0)",
                    _lastInside ? "inside" : "outside-right"));
            }
        }

        /// <summary>
        /// 把按钮摆到 bounds（物品栏窗口的世界矩形）右下角：默认贴右边缘**外侧**，
        /// 右边放不下时（窗口已顶到画布右缘）翻到内侧。
        /// 锚点用 `DockedLayout.ToNormalized` 归一化到父层，和图鉴面板同一套算法。
        /// </summary>
        private static void PlaceAt(RectTransform rect, RectTransform parent, Rect bounds, bool alignTop = false)
        {
            if (rect == null || parent == null) return;
            if (bounds.width < 2f || bounds.height < 2f) return;

            // 口袋那种「一格一格」的界面：按钮对齐上沿、贴在右侧，
            // 和标签同一行，看起来像挂在这一块旁边的按钮。
            var corner = alignTop ? DockedLayout.TopRight(bounds) : DockedLayout.BottomRight(bounds);
            var anchor = DockedLayout.ToNormalized(parent, corner);
            anchor.x = Mathf.Clamp01(anchor.x);
            anchor.y = Mathf.Clamp01(anchor.y);

            bool inside = anchor.x > 0.965f;

            if (rect.parent != parent) rect.SetParent(parent, false);

            float pivotY = alignTop ? 1f : 0f;
            float offsetY = alignTop ? -Gap : Gap;

            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = inside ? new Vector2(1f, pivotY) : new Vector2(0f, pivotY);
            rect.sizeDelta = new Vector2(ButtonWidth, ButtonHeight);
            rect.anchoredPosition = inside ? new Vector2(-Gap, offsetY) : new Vector2(Gap, offsetY);

            _lastAnchor = anchor;
            _lastInside = inside;
        }

        /// <summary>
        /// 保持按钮贴着窗口：窗口被拖动/缩放后跟上，窗口节点重建就重挂。
        /// </summary>
        internal static void ApplyDock()
        {
            if (Time.unscaledTime < _nextDockCheckAt) return;
            _nextDockCheckAt = Time.unscaledTime + 0.25f;

            var rect = _buttonRoot;
            var parent = _dockParent;
            if (rect == null || rect.parent == null || parent == null)
            {
                Inject();
                return;
            }

            RectTransform target;
            Rect bounds;
            bool dockPocket;
            try
            {
                target = ResolveDockTarget(out dockPocket);
                if (target == null) return;               // 窗口还没出来，保持现状
                bounds = DockedLayout.ToWorldRect(target);
            }
            catch (Exception e) { Warn("apply dock: " + e.Message); return; }

            if (bounds.width < 2f || bounds.height < 2f) return;

            // 解析退化到整块画布时不要跟着摆（那会把按钮甩到屏幕角）
            var parentRect = parent.rect;
            if (bounds.width >= parentRect.width - 1f && bounds.height >= parentRect.height - 1f)
            {
                if (ModEntry.Log != null && Time.unscaledTime > _nextCanvasSkipLogAt)
                {
                    _nextCanvasSkipLogAt = Time.unscaledTime + 60f;
                    ModEntry.Log.Log("[button] 目标退化成整块画布，保持上次位置："
                        + Mathf.RoundToInt(bounds.width) + "x" + Mathf.RoundToInt(bounds.height));
                }
                return;
            }

            try { PlaceAt(rect, parent, bounds, dockPocket); }
            catch (Exception e) { Warn("place button: " + e.Message); }
        }

        /// <summary>
        /// 贴靠目标：主场景是物品栏窗口，店铺场景是底部那排 UI（地面 / 口袋机 / 管口 / 口袋边）。
        /// 两者都由 <see cref="AnchorTarget.ResolveWindow"/> 决定，这里只做转发。
        /// </summary>
        private static RectTransform ResolveDockTarget(out bool pocket)
        {
            pocket = false;
            return AnchorTarget.ResolveWindow();
        }

        /// <summary>面板布局时把量到的物品栏矩形喂过来（与图鉴面板共用同一次测量）。</summary>
        internal static void SyncToInventory(Rect bounds)
        {
            var rect = _buttonRoot;
            var parent = _dockParent;
            if (rect == null || parent == null) return;
            PlaceAt(rect, parent, bounds);
        }

        /// <summary>
        /// 游戏窗口（PixelWindow）发生任何可能改变矩形的动作后的回调
        /// （由 <see cref="PanelWatchPatch"/> 钩 Show/Hide/Validate/FixBounds/ResizePixels/
        /// ReclampDockedWindows 触发）：立刻重量一次，不排队。
        /// </summary>
        internal static void OnPanelGeometryChanged(PixelWindow window)
        {
            _nextDockCheckAt = 0f;
            ApplyDock();
        }

        private static void OpenCatalog()
        {
            CatalogWindow.Toggle();
        }

        /// <summary>
        /// 吞掉指针按下与拖拽事件。
        ///
        /// uGUI 的事件沿层级向上冒泡：按钮悬在物品栏上方，不拦的话拖动按钮等于拖动下面的窗口。
        /// </summary>
        private sealed class DragBlocker : MonoBehaviour,
            IPointerDownHandler, IPointerUpHandler,
            IBeginDragHandler, IDragHandler, IEndDragHandler, IScrollHandler
        {
            public void OnPointerDown(PointerEventData e) { }
            public void OnPointerUp(PointerEventData e) { }
            public void OnBeginDrag(PointerEventData e) { e.Use(); }
            public void OnDrag(PointerEventData e) { e.Use(); }
            public void OnEndDrag(PointerEventData e) { e.Use(); }
            public void OnScroll(PointerEventData e) { e.Use(); }
        }

        private static void Warn(string message)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("[button] " + message);
        }
    }
}
