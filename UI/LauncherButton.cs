using System;
using UnityEngine;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 把「图鉴」按钮**直接挂成物品栏窗口节点的子物体**。
    ///
    /// 以前是把按钮创建在画布上、每 0.25 秒按「解析到的矩形」重新摆一次。
    /// 那套做法有个致命退化：<see cref="AnchorTarget.Resolve"/> 找不到物品栏窗口时会
    /// 退回 <c>StoreUIManager.inventoryCanvas</c>（整屏 1920x1080@0,0），按钮就被摆到
    /// 屏幕右下角 —— 日志里的 <c>[button] sync bounds=1920x1080@(0,0) anchor=(1.000,0.000)</c>。
    ///
    /// 现在改成父子关系：窗口移动/缩放/隐藏由 Unity 的布局自动带着按钮走，
    /// 既不需要每帧量矩形，也不会再出现「解析失败→跑到屏幕角落」。
    /// 唯一要处理的是窗口节点被销毁重建（Unity 的 null 比较会返回 true），
    /// 这时重新挂一次即可（见 <see cref="ApplyDock"/>）。
    /// </summary>
    internal static class LauncherButton
    {
        private const string ButtonName = "ItemCatalog.LaunchButton";
        private const float ButtonWidth = 100f;
        private const float ButtonHeight = 30f;
        private const float Gap = 4f;

        private static CustomUIElement _buttonElement;
        private static string _buttonLabel;

        private static RectTransform _buttonRoot;   // 按钮容器（挂在窗口节点下）
        private static string _dockedPath;          // 当前绑定的窗口路径（仅用于日志）
        private static float _nextDockCheckAt;

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

        /// <summary>
        /// 解析物品栏窗口节点并把按钮挂上去。窗口还没生成 / 还没激活时直接返回，
        /// 等 <see cref="PanelWatchPatch"/> 的下一次面板事件再试。
        /// </summary>
        private static void Inject()
        {
            var manager = CustomUIManager.Instance;
            if (manager == null) { Warn("CustomUIManager is not ready"); return; }

            // 只认真正的物品栏窗口节点，不接受画布兜底（那正是偏移的来源）
            var target = AnchorTarget.ResolveWindow();
            if (target == null)
            {
                Warn("no inventory window resolved, will retry on next panel event");
                return;
            }

            _dockedPath = DockedLayout.HierarchyPath(target);

            // 同一个窗口里已经有按钮就复用，避免重复创建
            var existing = target.Find(ButtonName) as RectTransform;
            if (existing != null && existing != _buttonRoot)
            {
                UnityEngine.Object.Destroy(existing.gameObject);
                existing = null;
            }

            // 换画布/换窗口时清掉残留在别处的旧按钮，否则会多出一份且永远可点
            try
            {
                var allStale = UnityEngine.Object.FindObjectsOfType<GameObject>();
                for (int i = 0; i < allStale.Length; i++)
                {
                    var stale = allStale[i];
                    if (stale == null) continue;
                    if (stale.name != ButtonName && stale.name != "ItemCatalog.LoadBar") continue;
                    if (stale.transform.IsChildOf(target)) continue;
                    UnityEngine.Object.Destroy(stale);
                }
            }
            catch (Exception e) { Warn("cleanup leftovers: " + e.Message); }

            var rect = existing;
            if (rect == null)
            {
                var go = new GameObject(ButtonName, typeof(RectTransform));
                rect = (RectTransform)go.transform;
                // worldPositionStays=false：只用锚点定位，不继承窗口的缩放累积
                rect.SetParent(target, false);
            }

            // 贴窗口内部右下角：锚点与轴心都定在父节点右下角；
            // 之后窗口怎么移动/缩放都由 Unity 自己带着走，不再计算世界坐标。
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 0f);
            rect.sizeDelta = new Vector2(ButtonWidth, ButtonHeight);
            rect.anchoredPosition = new Vector2(-Gap, Gap);

            _buttonRoot = rect;

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

            if (ModEntry.Log != null)
            {
                var size = target.rect.size;
                ModEntry.Log.Log(string.Format(
                    "[button] bound to '{0}' window={1:F0}x{2:F0} anchor=(1,0) pivot=(1,0) offset=(-{3:F0},{3:F0})",
                    _dockedPath, size.x, size.y, Gap));
            }
        }

        /// <summary>
        /// 保持绑定有效。按钮是窗口的子物体，位置由布局自动跟随，
        /// 所以这里只做两件事：窗口节点没了 → 重新挂；绑定对象换人了 → 重新挂。
        /// </summary>
        internal static void ApplyDock()
        {
            if (Time.unscaledTime < _nextDockCheckAt) return;
            _nextDockCheckAt = Time.unscaledTime + 0.5f;

            // Unity 的「已销毁」对象 == null 为真：窗口重建后这里会命中
            if (_buttonRoot == null || _buttonRoot.parent == null)
            {
                Inject();
                return;
            }

            var target = AnchorTarget.ResolveWindow();
            if (target != null && _buttonRoot.parent != target) Inject();
        }

        /// <summary>
        /// 游戏窗口（PixelWindow）发生任何可能改变布局的事件后的回调
        /// （由 <see cref="PanelWatchPatch"/> 钩 Show/Hide/Validate/FixBounds/ResizePixels/
        /// ReclampDockedWindows 触发）。按钮绑在窗口上，位置不用管，只补一次绑定检查。
        /// </summary>
        internal static void OnPanelGeometryChanged(PixelWindow window)
        {
            ApplyDock();
        }

        /// <summary>
        /// 面板布局时把物品栏矩形喂过来的旧通道。按钮改成窗口子物体后，
        /// 位置已由布局自动跟随，这里只当成一次「绑定还活着吗」的检查机会。
        /// </summary>
        internal static void SyncToInventory(Rect bounds)
        {
            ApplyDock();
        }

        private static void OpenCatalog()
        {
            CatalogWindow.Toggle();
        }

        private static void Warn(string message)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("[button] " + message);
        }
    }
}
