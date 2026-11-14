using System;
using System.Collections.Generic;
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

        /// <summary>
        /// 只解析**真正的物品栏窗口节点**（①②），**不含**画布兜底。
        /// 要把按钮挂成窗口子物体时必须用这个：画布是整屏矩形，
        /// 挂上去就会跑到屏幕右下角（历史上的「按钮偏移」就是这么来的）。
        /// </summary>
        internal static RectTransform ResolveWindow()
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

            // ② 店铺场景：物品栏窗口整个不在（进店是换场景），改贴底部那排 UI
            //    （地面 / 口袋机 / 管口 / 口袋边）—— 那里才是能放东西带走的地方。
            window = ResolveScenePanel();
            if (window != null) return window;

            // ③ 再退一步：店铺自己的容器窗口（展示柜 / 前后台库存 …）
            window = ResolveShopWindow();
            if (window != null) return window;

            // ④ 名字像物品栏的可见节点
            return FindInventoryLike();
        }

        /// <summary>
        /// 店铺场景里可贴的窗口，按「玩家看得最久」排序：展示柜优先，
        /// 然后前台/后台库存、巴扎左柜、垃圾桶、文件柜、排液口。
        /// </summary>
        private static readonly WindowProp[] ShopWindowOrder =
        {
            WindowProp.Showcase,
            WindowProp.FrontInv,
            WindowProp.BackInv,
            WindowProp.BazarLeft,
            WindowProp.Trash,
            WindowProp.DocInv,
            WindowProp.DrainInv
        };

        private static string _loggedShopTarget;
        private static float _nextShopProbeLogAt;

        /// <summary>店铺场景里第一个「活着且有尺寸」的容器窗口；都没有返回 null。</summary>
        private static RectTransform ResolveShopWindow()
        {
            EmporiumEntry emporium;
            try { emporium = EmporiumEntry.Instance; }
            catch { return null; }

            if (emporium == null) return null;

            for (int i = 0; i < ShopWindowOrder.Length; i++)
            {
                WindowProp prop = ShopWindowOrder[i];

                PixelWindow win = null;
                try { win = GetWindow(emporium, prop); }
                catch { }

                if (win == null) continue;

                var node = WindowRectOf(win);
                if (node == null || !node.gameObject.activeInHierarchy) continue;

                var sized = PickSizedNode(node);
                if (sized == null) continue;

                Rect rect;
                try { rect = DockedLayout.ToWorldRect(sized); }
                catch { continue; }

                if (rect.width < 2f || rect.height < 2f) continue;

                if (_loggedShopTarget != prop.ToString() && ModEntry.Log != null)
                {
                    _loggedShopTarget = prop.ToString();
                    ModEntry.Log.Log(string.Format(
                        "[anchor] shop window {0} '{1}' -> ({2:F0},{3:F0},{4:F0}x{5:F0})",
                        prop, DockedLayout.HierarchyPath(sized), rect.x, rect.y, rect.width, rect.height));
                }

                return sized;
            }

            // 一个都没有：把候选状态打一次，方便判断店铺场景到底开了哪扇窗
            if (ModEntry.Log != null && Time.unscaledTime > _nextShopProbeLogAt)
            {
                _nextShopProbeLogAt = Time.unscaledTime + 10f;

                for (int i = 0; i < ShopWindowOrder.Length; i++)
                {
                    WindowProp prop = ShopWindowOrder[i];

                    PixelWindow win = null;
                    try { win = GetWindow(emporium, prop); }
                    catch { }

                    string state = "null";
                    if (win != null)
                    {
                        var node = WindowRectOf(win);
                        bool active = node != null && node.gameObject.activeInHierarchy;
                        state = active ? "active" : "inactive";
                    }

                    ModEntry.Log.Log("[anchor] shop probe " + prop + " = " + state);
                }
            }

            return null;
        }

        /// <summary>
        /// 按名字找容器界面里的一个具体区块（`pocket1` / `pocket2` / `backpack` …）。
        ///
        /// 店铺场景里真正能装东西带走的是这些「口袋」，它们是同一扇窗口里的子区块，
        /// 名字来自 `EquipmentDirectory.CreateExpeditionBoxWindow`
        /// （`pocket1` / `pocket2` / `backpack`，标签键 `item_expedition_box_label_*`）。
        /// </summary>
        private static RectTransform _pocketNode;
        private static float _nextPocketScanAt;

        internal static RectTransform ResolvePocket(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            // 上次找到的还活着就复用：每 0.25 秒扫全场景太贵
            var cached = _pocketNode;
            if (cached != null)
            {
                try
                {
                    if (cached.gameObject.activeInHierarchy)
                    {
                        var cachedRect = DockedLayout.ToWorldRect(cached);
                        if (cachedRect.width > 20f && cachedRect.height > 20f) return cached;
                    }
                }
                catch { }

                _pocketNode = null;
            }

            if (Time.unscaledTime < _nextPocketScanAt) return null;
            _nextPocketScanAt = Time.unscaledTime + 1f;

            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<RectTransform>();
                for (int i = 0; i < all.Length; i++)
                {
                    var rt = all[i];
                    if (rt == null || !rt.gameObject.activeInHierarchy) continue;
                    if (rt.name.IndexOf(id, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var rect = DockedLayout.ToWorldRect(rt);
                    if (rect.width < 20f || rect.height < 20f) continue;

                    _pocketNode = rt;

                    if (ModEntry.Log != null)
                    {
                        ModEntry.Log.Log(string.Format(
                            "[anchor] pocket '{0}' -> '{1}' ({2:F0},{3:F0},{4:F0}x{5:F0})",
                            id, DockedLayout.HierarchyPath(rt), rect.x, rect.y, rect.width, rect.height));
                    }

                    return rt;
                }
            }
            catch (Exception e) { Warn("resolve pocket: " + e.Message); }

            return null;
        }

        // ── 店铺场景：底部那排 UI（地面 / 口袋机 / 管口 / 口袋边）───────────────
        // 这几个区块没有挂在 EmporiumEntry 的属性上，只能按结构找：
        // 画布下方、够宽、不是满屏的那一块。找到后按钮贴它右端、图鉴左端贴它右端，
        // 和主场景里「贴物品栏」完全同一套（见 CatalogWindow.ApplyPlacement）。
        private static RectTransform _scenePanel;
        private static float _nextScenePanelScanAt;
        private static float _nextSceneProbeLogAt;

        internal static RectTransform ResolveScenePanel()
        {
            var cached = _scenePanel;
            if (cached != null)
            {
                try
                {
                    if (cached.gameObject.activeInHierarchy)
                    {
                        var cachedRect = DockedLayout.ToWorldRect(cached);
                        if (cachedRect.width > 100f && cachedRect.height > 40f) return cached;
                    }
                }
                catch { }

                _scenePanel = null;
            }

            // 换场景后界面还在构建，底部区块会晚几帧才有尺寸：扫描间隔放短，
            // 但真正跑多久由 LauncherButton 那段有上限的补挂决定。
            if (Time.unscaledTime < _nextScenePanelScanAt) return null;
            _nextScenePanelScanAt = Time.unscaledTime + 0.15f;

            RectTransform best = null;
            float bestWidth = 0f;
            Rect bestRect = new Rect();

            // 失败时把这些候选打出来：能直接看出「界面已经在，只是被哪条规则挡掉了」
            var probes = new List<string>(6);

            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<RectTransform>();
                for (int i = 0; i < all.Length; i++)
                {
                    var rt = all[i];
                    if (rt == null || !rt.gameObject.activeInHierarchy) continue;
                    if (rt.name == "WindowUICanvas") continue;

                    Rect rect;
                    try { rect = DockedLayout.ToWorldRect(rt); }
                    catch { continue; }

                    if (probes.Count < 6 && rect.width > 80f && rect.height > 40f)
                    {
                        probes.Add(string.Format("{0}  {1:F0}x{2:F0} @({3:F0},{4:F0})",
                            rt.name, rect.width, rect.height, rect.center.x, rect.center.y));
                    }

                    if (rect.width < 260f || rect.height < 60f) continue;
                    if (rect.width < bestWidth) continue;

                    // 只要「靠下」的：中心在下半屏
                    var canvasRect = CanvasRectOf(rt);
                    if (canvasRect.height <= 1f) continue;
                    if (rect.center.y > canvasRect.yMin + canvasRect.height * 0.45f) continue;

                    // 满屏的容器不要（那是画布/根节点本身）
                    if (rect.width >= canvasRect.width * 0.95f && rect.height >= canvasRect.height * 0.9f) continue;

                    if (rect.width > bestWidth)
                    {
                        best = rt;
                        bestWidth = rect.width;
                        bestRect = rect;
                    }
                }
            }
            catch (Exception e) { Warn("resolve scene panel: " + e.Message); }

            if (best == null)
            {
                if (ModEntry.Log != null && Time.unscaledTime > _nextSceneProbeLogAt)
                {
                    _nextSceneProbeLogAt = Time.unscaledTime + 0.5f;
                    ModEntry.Log.Log("[anchor] scene panel: none (candidates=" + probes.Count + ")");
                    for (int i = 0; i < probes.Count; i++)
                        ModEntry.Log.Log("[anchor]   " + probes[i]);
                }

                return null;
            }

            _scenePanel = best;

            if (ModEntry.Log != null)
            {
                ModEntry.Log.Log(string.Format(
                    "[anchor] scene panel '{0}' ({1:F0},{2:F0},{3:F0}x{4:F0})",
                    DockedLayout.HierarchyPath(best), bestRect.x, bestRect.y, bestRect.width, bestRect.height));
            }

            return best;
        }

        /// <summary>节点所属画布的屏幕矩形；拿不到返回全屏。</summary>
        private static Rect CanvasRectOf(RectTransform node)
        {
            try
            {
                var canvas = node.GetComponentInParent<Canvas>();
                if (canvas != null)
                {
                    var rt = canvas.transform as RectTransform;
                    if (rt != null) return DockedLayout.ToWorldRect(rt);
                }
            }
            catch { }

            return new Rect(0f, 0f, Screen.width, Screen.height);
        }

        /// <summary>只认物品栏窗口（`EmporiumEntry.invWindow`），不给任何兜底。</summary>
        internal static RectTransform ResolveInventoryNode()
        {
            try { return ResolveInventoryWindow(); }
            catch { return null; }
        }

        internal static RectTransform Resolve()
        {
            var window = ResolveWindow();
            if (window != null) return window;

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
            BazarLeft,
            Showcase,
            Trash,
            DocInv,
            DrainInv
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
                    case WindowProp.BazarLeft: return emporium.bazarLeftInvWindow;
                    case WindowProp.Showcase: return emporium.showcaseWindow;
                    case WindowProp.Trash: return emporium.trashInvWindow;
                    case WindowProp.DocInv: return emporium.docInvWindow;
                    case WindowProp.DrainInv: return emporium.drainInvWindow;
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
