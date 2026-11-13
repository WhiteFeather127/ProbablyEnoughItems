
using System;
using UnityEngine;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 把「图鉴」按钮注入到贴靠目标的右边缘、垂直居中。
    /// 目标由 <see cref="AnchorTarget"/> 自动解析（物品栏节点 → StoreUIManager.inventoryCanvas）。
    /// </summary>
    internal static class LauncherButton
    {
        private const string ButtonName = "ItemCatalog.LaunchButton";
        private const float ButtonWidth = 100f;
        private const float ButtonHeight = 30f;
        private const float Gap = 4f;

        private static CustomUIElement _buttonElement;
        private static string _buttonLabel;

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

            var target = AnchorTarget.Resolve();
            if (target == null) { Warn("no dock target resolved"); return; }

            _dockedPath = DockedLayout.HierarchyPath(target);

            var parent = AnchorTarget.RootCanvas(target);
            if (parent == null) { Warn("dock target has no canvas root"); return; }

            var existing = parent.Find(ButtonName);
            if (existing != null) UnityEngine.Object.Destroy(existing.gameObject);

            // 重贴可能换了画布（上次贴错到 CanvasPaperUI 之类）：
            // 残留在其它画布里的旧按钮/旧进度条也要清掉，否则会多出一份且永远可点。
            try
            {
                var allStale = UnityEngine.Object.FindObjectsOfType<GameObject>();
                for (int i = 0; i < allStale.Length; i++)
                {
                    var stale = allStale[i];
                    if (stale == null) continue;
                    if (stale.name != ButtonName && stale.name != "ItemCatalog.LoadBar") continue;
                    if (stale.transform.IsChildOf(parent)) continue;   // 当前画布的由上面那行处理

                    UnityEngine.Object.Destroy(stale);
                }
            }
            catch (Exception e) { Warn("cleanup leftovers: " + e.Message); }

            // 锚点解析到的就是物品栏窗口本身，直接用它自己的矩形（与旧版命中 @572,22,776x328 一致）
            var bounds = DockedLayout.ToWorldRect(target);

            // 兜底：万一拿到的还是 0 尺寸/退化节点，退回整块画布，至少按钮能被看到
            if (bounds.width < 2f || bounds.height < 2f)
            {
                Warn("dock target has no size, falling back to canvas");
                bounds = DockedLayout.ToWorldRect(parent);
            }

            _dockedBounds = bounds;   // 记录一次，便于日志对照

            var go = new GameObject(ButtonName, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.sizeDelta = new Vector2(ButtonWidth, ButtonHeight);

            _buttonRoot = rect;
            _dockParent = parent;
            PlaceAt(rect, parent, bounds);

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

            if (ModEntry.Log != null)
            {
                ModEntry.Log.Log(string.Format(
                    "[button] target='{0}' parent='{1}' bounds={2}x{3}@({4:F0},{5:F0}) anchor=({6:F3},{7:F3}) corner={8}",
                    DockedLayout.HierarchyPath(target), DockedLayout.HierarchyPath(parent),
                    Mathf.RoundToInt(bounds.width), Mathf.RoundToInt(bounds.height),
                    bounds.x, bounds.y, _lastAnchor.x, _lastAnchor.y,
                    _lastInside ? "bottom-right(inset)" : "bottom-right"));
            }
        }

        private static RectTransform _buttonRoot;   // 按钮容器（每 tick 只改它的锚点，不重建）
        private static RectTransform _dockParent;   // 容器所在的画布
        private static Vector2 _lastAnchor;
        private static bool _lastInside;

        /// <summary>
        /// 把按钮容器摆到 bounds 的右下角。
        /// 只改 RectTransform 的锚点，不销毁重建，所以每 tick 重算也不会打断交互。
        /// </summary>
        private static void PlaceAt(RectTransform rect, RectTransform parent, Rect bounds)
        {
            var anchor = DockedLayout.ToNormalized(parent, DockedLayout.BottomRight(bounds));
            anchor.x = Mathf.Clamp01(anchor.x);
            anchor.y = Mathf.Clamp01(anchor.y);

            // 目标已经贴到画布右边缘时，按钮改为向内摆放，避免跑到屏幕外
            bool inside = anchor.x > 0.965f;

            if (rect.parent != parent) rect.SetParent(parent, false);

            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.sizeDelta = new Vector2(ButtonWidth, ButtonHeight);
            rect.pivot = inside ? new Vector2(1f, 0f) : new Vector2(0f, 0f);
            rect.anchoredPosition = inside ? new Vector2(-Gap, Gap) : new Vector2(Gap, Gap);

            _lastAnchor = anchor;
            _lastInside = inside;
        }

        /// <summary>
        /// 按「当前这一帧的布局」重新摆一次按钮。
        /// 面板每次绘制都是现量目标矩形，按钮也用同一份现量结果，
        /// 因此布局一稳定就会自动对齐，无需检测变化再重建。
        /// </summary>
        internal static void ApplyDock()
        {
            var rect = _buttonRoot;
            var parent = _dockParent;
            if (rect == null || parent == null) return;

            if (Time.unscaledTime < _nextDockCheckAt) return;
            _nextDockCheckAt = Time.unscaledTime + 0.25f;

            RectTransform target = null;
            Rect bounds;

            try
            {
                target = AnchorTarget.ResolveCached();
                if (target == null) return;
                bounds = DockedLayout.ToWorldRect(target);
            }
            catch (Exception e) { Warn("apply dock: " + e.Message); return; }

            // 布局还没跑完（0 尺寸）时先保持现状，等下一次布局事件
            if (bounds.width < 2f || bounds.height < 2f) return;

            // 矩形没变就不动（Validate 这类钩子可能很频繁）
            if (RectNear(bounds, _dockedBounds)) return;

            try { PlaceAt(rect, parent, bounds); }
            catch (Exception e) { Warn("place button: " + e.Message); }

            _dockedBounds = bounds;

            if (ModEntry.Log != null)
            {
                _dockLogCount++;
                // 前几次布局变化各打一条，之后每分钟最多一条，避免刷屏
                if (_dockLogCount <= 6 || Time.unscaledTime > _nextDockLogAt)
                {
                    _nextDockLogAt = Time.unscaledTime + 60f;
                    ModEntry.Log.Log(string.Format(
                        "[button] sync bounds={0}x{1}@({2:F0},{3:F0}) anchor=({4:F3},{5:F3})",
                        Mathf.RoundToInt(bounds.width), Mathf.RoundToInt(bounds.height),
                        bounds.x, bounds.y, _lastAnchor.x, _lastAnchor.y));
                }
            }
        }

        private static int _dockLogCount;
        private static float _nextDockLogAt;

        private static void OpenCatalog()
        {
            CatalogWindow.Toggle();
        }

        private static string _dockedPath;         // 最近一次解析到的目标路径（仅用于日志）
        private static Rect _dockedBounds;         // 最近一次测量到的目标矩形
        private static float _nextDockCheckAt;
        private static float _nextVisibilityCheckAt;

        /// <summary>
        /// 游戏窗口（PixelWindow）发生任何可能改变矩形的事件后的回调
        /// （由 <see cref="PanelWatchPatch"/> 钩 Show/Hide/Validate/FixBounds/ResizePixels/
        /// ReclampDockedWindows 触发）。不区分是本体还是别的模组挪的窗口——
        /// 只要走游戏自己的窗口函数，按钮就会跟着重新对齐。
        /// </summary>
        internal static void OnPanelGeometryChanged(PixelWindow window)
        {
            if (_buttonRoot == null) return;
            if (Time.unscaledTime < _nextVisibilityCheckAt) return;
            _nextVisibilityCheckAt = Time.unscaledTime + 0.2f;

            _nextDockCheckAt = 0f;   // 立刻允许 ApplyDock 跑一次
            ApplyDock();
        }

        /// <summary>
        /// 面板布局时把**它自己刚量到的**物品栏矩形直接喂过来。
        /// 这是按钮的主同步通道：面板怎么摆，按钮就按同一个矩形摆，不存在两边各量一次的可能。
        /// </summary>
        internal static void SyncToInventory(Rect bounds)
        {
            var rect = _buttonRoot;
            var parent = _dockParent;
            if (rect == null || parent == null) return;

            // 布局还没跑完（0 尺寸）时不动，等面板下一次布局
            if (bounds.width < 2f || bounds.height < 2f) return;

            try { PlaceAt(rect, parent, bounds); }
            catch (Exception e) { Warn("sync button: " + e.Message); return; }

            _dockedBounds = bounds;   // 记下来，避免随后的钩子回调再摆一次
        }

        /// <summary>两个矩形是否基本相同（1px 容差）。</summary>
        private static bool RectNear(Rect a, Rect b)
        {
            return Mathf.Abs(a.x - b.x) <= 1f
                && Mathf.Abs(a.y - b.y) <= 1f
                && Mathf.Abs(a.width - b.width) <= 1f
                && Mathf.Abs(a.height - b.height) <= 1f;
        }

        private static void Warn(string message)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("[button] " + message);
        }
    }
}
