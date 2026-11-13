using System;
using System.Reflection;
using UnityEngine;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 解析「图鉴要贴在哪块 UI 上」。
    /// 全部自动，按可靠性从高到低：
    ///   ① 游戏自己的物品栏窗口引用（<c>EmporiumEntry.invWindow</c>）；
    ///   ② 名字像物品栏的可见节点（InvWindow / InventoryBar）；
    ///   ③ StoreUIManager.inventoryCanvas。
    /// 不做任何分辨率/坐标硬编码，也不提供配置项。
    /// </summary>
    internal static class AnchorTarget
    {
        /// <summary>上次命中 invWindow 的日志标记，避免每秒重复打印。</summary>
        private static bool _loggedWindowTarget;

        internal static RectTransform Resolve()
        {
            // ① 最可靠：游戏自己维护的物品栏窗口
            var window = ResolveInventoryWindow();
            if (window != null)
            {
                if (!_loggedWindowTarget && ModEntry.Log != null)
                {
                    _loggedWindowTarget = true;
                    var rect = DockedLayout.ToWorldRect(window);
                    ModEntry.Log.Log(string.Format("[anchor] inventory window '{0}' -> ({1:F0},{2:F0},{3:F0}x{4:F0})",
                        DockedLayout.HierarchyPath(window), rect.x, rect.y, rect.width, rect.height));
                }

                return window;
            }

            // ② 名字像物品栏的可见节点
            var auto = FindInventoryLike();
            if (auto != null) return auto;

            // ③ 画布兜底（位置不精确，但至少能用）
            var store = StoreUIManager.Instance;
            if (store != null && store.inventoryCanvas != null)
            {
                Warn("falling back to inventoryCanvas (position may be off)");
                return store.inventoryCanvas.transform as RectTransform;
            }

            Warn("StoreUIManager.inventoryCanvas is null");
            return null;
        }

        /// <summary>取 <c>EmporiumEntry.Instance.invWindow</c> 的渲染节点；拿不到返回 null。</summary>
        private static RectTransform ResolveInventoryWindow()
        {
            try
            {
                var emporium = EmporiumEntry.Instance;
                if (emporium == null) return null;

                var window = WindowPropertyRect(emporium, WindowProp.Inv);
                if (window == null || !window.gameObject.activeInHierarchy) return null;

                // PixelWindow 上拿到的常是 0×0 的布局节点（真正带尺寸的是它的子节点）
                return PickSizedNode(window);
            }
            catch (Exception e)
            {
                Warn("resolve invWindow: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// 找一个**有实际尺寸**的节点：自身有尺寸就用自身，否则向下找面积最大的子节点（最多 3 层）。
        /// 物品栏窗口的父链上有一串 0×0 的布局节点，直接拿会得到 (0,0) 尺寸。
        /// </summary>
        private static RectTransform PickSizedNode(RectTransform node)
        {
            if (node == null) return null;

            var rect = DockedLayout.ToWorldRect(node);
            if (rect.width > 1f && rect.height > 1f) return node;

            RectTransform best = null;
            float bestArea = 0f;

            try { CollectSized(node, ref best, ref bestArea, 0); }
            catch (Exception e) { Warn("pick sized node: " + e.Message); }

            return best;
        }

        private static void CollectSized(RectTransform node, ref RectTransform best, ref float bestArea, int depth)
        {
            if (depth > 3) return;

            for (int i = 0; i < node.childCount; i++)
            {
                var child = node.GetChild(i) as RectTransform;
                if (child == null) continue;

                bool active = false;
                try { active = child.gameObject.activeInHierarchy; } catch { }
                if (!active) continue;

                var rect = DockedLayout.ToWorldRect(child);
                if (rect.width <= 1f || rect.height <= 1f) continue;

                float area = rect.width * rect.height;
                if (area > bestArea)
                {
                    bestArea = area;
                    best = child;
                }
            }

            // 本层没有可用的再往下挖
            if (best == null)
            {
                for (int i = 0; i < node.childCount; i++)
                {
                    var child = node.GetChild(i) as RectTransform;
                    if (child == null) continue;
                    CollectSized(child, ref best, ref bestArea, depth + 1);
                }
            }
        }

        /// <summary>要用到的容器窗口（EmporiumEntry 上的那几个 PixelWindow 属性）。</summary>
        internal enum WindowProp
        {
            Inv,
            FrontInv,
            BackInv,
            Showcase,
            Trash
        }

        /// <summary>
        /// 按枚举直接读属性。以前传属性名字符串再反射，属于「拼接出的名称」，
        /// 会被扫描器判成 reflection-external（2026-12-10 起直接拒绝加载模组）；
        /// 现在每个分支都是字面属性访问，扫描器能直接看清。
        /// </summary>
        internal static PixelWindow GetWindow(EmporiumEntry emporium, WindowProp prop)
        {
            if (emporium == null) return null;

            try
            {
                switch (prop)
                {
                    case WindowProp.Inv: return emporium.invWindow;
                    case WindowProp.FrontInv: return emporium.frontInvWindow;
                    case WindowProp.BackInv: return emporium.backInvWindow;
                    case WindowProp.Showcase: return emporium.showcaseWindow;
                    case WindowProp.Trash: return emporium.trashInvWindow;
                    default: return null;
                }
            }
            catch { return null; }
        }

        /// <summary>EmporiumEntry 上某个容器窗口的渲染节点。</summary>
        internal static RectTransform WindowPropertyRect(EmporiumEntry emporium, WindowProp prop)
        {
            return WindowRectOf(GetWindow(emporium, prop));
        }

        /// <summary>
        /// 容器窗口的渲染节点。`PixelWindow.rectTransform` 是公开属性，
        /// 直接读即可 —— 以前用 `window.GetType()` 遍历字段/属性，
        /// 那属于「运行时才知道的类型」，会被判成 reflection-external。
        /// </summary>
        internal static RectTransform WindowRectOf(object window)
        {
            if (window == null) return null;

            try
            {
                var pixelWindow = window as PixelWindow;
                if (pixelWindow != null)
                {
                    try
                    {
                        var rect = pixelWindow.rectTransform;
                        if (rect != null) return rect;
                    }
                    catch { }
                }

                var component = window as Component;
                if (component != null) return component.transform as RectTransform;

                return window as RectTransform;
            }
            catch { return null; }
        }

        /// <summary>上次解析命中（矩形锚点）的节点与规格，用于 O(1) 复查。</summary>
        private static RectTransform _cachedRectNode;
        private static float _cachedW, _cachedH;

        /// <summary>
        /// 带缓存的解析：上次命中的节点还活着、还激活、尺寸仍在容差内就直接复用。
        /// 全场景 FindObjectsOfType 每 1 秒跑一次是游戏内周期性掉帧的来源之一，复查是 O(1)。
        /// 窗口显隐回调等「必须重新评估」的场景仍应调 <see cref="Resolve"/>。
        /// </summary>
        internal static RectTransform ResolveCached()
        {
            var node = _cachedRectNode;
            if (node != null)
            {
                try
                {
                    if (node.gameObject.activeInHierarchy)
                    {
                        var rect = DockedLayout.ToWorldRect(node);
                        float sizeDiff = Mathf.Abs(rect.width - _cachedW) + Mathf.Abs(rect.height - _cachedH);
                        if (sizeDiff <= Mathf.Max(40f, (_cachedW + _cachedH) * 0.08f))
                            return node;
                    }
                }
                catch { }

                _cachedRectNode = null;   // 失效（销毁/关面板/尺寸变了）→ 下面全量重扫
            }

            var resolved = Resolve();
            _cachedRectNode = resolved;

            // 记下这次解析到的尺寸，下次复查才有比较基准
            if (resolved != null)
            {
                try
                {
                    var rect = DockedLayout.ToWorldRect(resolved);
                    _cachedW = rect.width;
                    _cachedH = rect.height;
                }
                catch { _cachedW = 0f; _cachedH = 0f; }
            }

            return resolved;
        }

        /// <summary>向上找到所属 Canvas 的 RectTransform，用于给按钮/面板找坐标系与父节点。</summary>
        internal static RectTransform RootCanvas(RectTransform target)
        {
            if (target == null) return null;

            Transform current = target;
            while (current != null)
            {
                if (current.GetComponent<Canvas>() != null) return current as RectTransform;
                current = current.parent;
            }

            return target;
        }

        /// <summary>自动模式：找名字含 InvWindow / InventoryBar 之类的可见节点。</summary>
        private static RectTransform FindInventoryLike()
        {
            RectTransform best = null;

            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<RectTransform>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    var candidate = all[i];
                    if (candidate == null || !candidate.gameObject.activeInHierarchy) continue;

                    string name = candidate.name.ToLowerInvariant();
                    if (name.IndexOf("invwindow", StringComparison.Ordinal) < 0 &&
                        name.IndexOf("inventory", StringComparison.Ordinal) < 0 &&
                        name.IndexOf("itembar", StringComparison.Ordinal) < 0) continue;

                    var rect = DockedLayout.ToWorldRect(candidate);
                    if (rect.width < 120f || rect.height < 40f) continue;

                    if (best == null) best = candidate;
                    else
                    {
                        var bestRect = DockedLayout.ToWorldRect(best);
                        if (rect.width * rect.height > bestRect.width * bestRect.height) best = candidate;
                    }
                }
            }
            catch (Exception e)
            {
                Warn("inventory scan failed: " + e.Message);
            }

            if (best != null && ModEntry.Log != null)
                ModEntry.Log.Log("[anchor] auto picked '" + DockedLayout.HierarchyPath(best) + "'");

            return best;
        }

        private static void Warn(string message)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("[anchor] " + message);
        }
    }
}
