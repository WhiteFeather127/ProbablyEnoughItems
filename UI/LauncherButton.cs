using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 「图鉴」启动按钮。
    ///
    /// 每个可贴靠的界面各一枚按钮，谁的界面在就显示谁那枚：
    ///   · `inventory` —— 主场景的物品栏窗口；
    ///   · `scene`     —— 店铺场景底部那排 UI（地面 / 口袋机 / 管口 / 口袋边）。
    ///
    /// 绑定规则（用户要求）：**按钮认同一个场景对象**。目标节点被销毁或失活，
    /// 按钮跟着销毁；目标换了（窗口重建），旧按钮连同新位置一起重来。
    /// 两枚按钮打开同一个图鉴窗口，只是喂给它的锚点不同
    /// （见 <see cref="CatalogWindow.OpenAt"/>），所以面板会贴着被点的那一侧。
    ///
    /// 位置算法和图鉴面板完全一致：把目标的世界矩形归一化到同一个 UI 层（画布根），
    /// 再用锚点贴住 —— 面板左边缘就是这么和物品栏右边缘对齐的。
    ///
    /// 曾经试过、不要再用的两种做法：
    ///   ① 挂进物品栏窗口节点 → 那是物品树的格子层（TreeNodeRender + RectMask2D），
    ///      按钮被窗口背景盖住，拖动还会把窗口一起拖走；
    ///   ② 不用归一化、直接算父节点本地坐标 → 父节点常是 0×0 / pivot 在中心的空壳，
    ///      位置直接跑到左上角。
    /// </summary>
    internal static class LauncherButton
    {
        private const string ButtonName = "ItemCatalog.LaunchButton";

        /// <summary>图标按钮边长（`button.png` 是 32×32 的方图）。</summary>
        private const float IconSize = 32f;

        /// <summary>没有图标时的文字按钮尺寸。</summary>
        private const float TextWidth = 100f;
        private const float TextHeight = 30f;

        /// <summary>实际用的按钮尺寸：有图标就是方图，没有才用文字按钮的宽高。</summary>
        private static float _width = IconSize;
        private static float _height = IconSize;

        private const float Gap = 4f;

        private static Sprite _icon;
        private static bool _iconTried;

        /// <summary>模组目录里的 `button.png` → Sprite；读不到返回 null（退回文字按钮）。</summary>
        private static Sprite LoadIcon()
        {
            if (_iconTried) return _icon;
            _iconTried = true;

            try
            {
                var bytes = ModHelper.ReadModFileBytes(ModEntry.MOD_ID, "button.png");
                if (bytes == null || bytes.Length == 0)
                {
                    Warn("button.png not found");
                    return null;
                }

                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(bytes))
                {
                    Warn("button.png decode failed");
                    return null;
                }

                tex.filterMode = FilterMode.Point;
                tex.wrapMode = TextureWrapMode.Clamp;

                _icon = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f), 1f);
            }
            catch (Exception e) { Warn("load button.png: " + e.Message); }

            return _icon;
        }

        /// <summary>一枚按钮 = 一个贴靠目标。</summary>
        private sealed class Dock
        {
            internal string Key;             // "inventory" / "scene"，仅用于日志
            internal RectTransform Target;   // 贴靠对象（按钮因它而生）
            internal RectTransform Root;     // 按钮容器（挂在画布根上）
            internal RectTransform Parent;   // 画布根
        }

        private static readonly List<Dock> _docks = new List<Dock>();

        /// <summary>
        /// 挂在**目标节点**上的生命周期钩子：目标销毁/隐藏，按钮跟着走。
        ///
        /// 这样就不需要每帧去问「目标还在不在」——Unity 自己会在节点销毁时调 OnDestroy。
        /// 面板移动/改尺寸仍然由 <see cref="PanelWatchPatch"/> 的窗口事件驱动重排。
        /// </summary>
        private sealed class DockWatcher : MonoBehaviour
        {
            internal Dock Dock;

            private void OnDestroy() { OnTargetDestroyed(Dock); }
            private void OnDisable() { OnTargetHidden(Dock, true); }
            private void OnEnable() { OnTargetHidden(Dock, false); }
        }

        internal static void OnGameLoaded()
        {
            HookSceneLoaded();

            CatalogWindow.ResetForScene();

            // 换场景后旧画布连同按钮一起没了，清掉失效登记
            Cleanup();

            try { Refresh(); }
            catch (Exception e) { Warn("inject launch button: " + e.Message); }
        }

        private static bool _hookedScene;

        /// <summary>
        /// 订阅 Unity 的场景加载事件。`ModHook` 只有「游戏加载完成」那几个，没有换场景的，
        /// 而进店铺是直接换场景——不挂这个钩子，店铺那枚按钮就只能等窗口事件。
        /// </summary>
        private static void HookSceneLoaded()
        {
            if (_hookedScene) return;
            _hookedScene = true;

            try
            {
                UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
            }
            catch (Exception e) { Warn("hook sceneLoaded: " + e.Message); }
        }

        /// <summary>
        /// 场景加载完成后**同一帧**挂按钮：先强制结算布局，再量矩形，
        /// 因此不需要任何补挂协程，按钮和界面一起出现在淡入里。
        /// </summary>
        private static void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene,
                                          UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            try
            {
                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[button] scene loaded t=" + Time.unscaledTime.ToString("F2")
                                     + " scene=" + (scene.IsValid() ? scene.name : "?") + " frame=" + Time.frameCount);

                CatalogWindow.ResetForScene();
                Cleanup();
                Refresh();
            }
            catch (Exception e) { Warn("scene loaded: " + e.Message); }
        }

        /// <summary>
        /// 重新解析一遍目标并挂按钮。只在**事件**里调用：场景加载、窗口显隐/几何变化、
        /// 打开/关闭物品栏。不做每帧轮询——存活性由 <see cref="DockWatcher"/> 负责。
        /// </summary>
        internal static void Refresh()
        {
            // 同步量一次：让布局立即结算，否则拿到的可能是上一帧的 0×0 矩形
            try { Canvas.ForceUpdateCanvases(); }
            catch { }

            // 主场景：物品栏窗口
            var inventory = AnchorTarget.ResolveInventoryNode();
            Update("inventory", inventory);

            // 店铺场景：底部那排 UI（地面 / 口袋机 / 管口 / 口袋边）。
            // 物品栏窗口在的时候不去猜底部区块，否则主场景里会多长出一枚按钮。
            Update("scene", inventory != null ? null : AnchorTarget.ResolveScenePanel());

            Cleanup();
        }

        /// <summary>
        /// 窗口对象刚被 new 出来的瞬间（`PixelWindow..ctor` 后置钩子）。
        /// 把它记进候选名单并立刻试挂一次——不需要再在场景里扫。
        /// </summary>
        internal static void OnPanelCreated(PixelWindow window)
        {
            if (window == null) return;

            try
            {
                var node = AnchorTarget.WindowRectOf(window);
                if (node != null) AnchorTarget.RegisterPanel(node);
            }
            catch { }

            // 一次场景加载会构造几十个窗口，同一帧内不必每个都重排
            if (Time.unscaledTime < _nextCreatedRefreshAt) return;
            _nextCreatedRefreshAt = Time.unscaledTime + 0.05f;

            try { Refresh(); }
            catch (Exception e) { Warn("panel created: " + e.Message); }
        }

        private static float _nextCreatedRefreshAt;

        /// <summary>窗口几何变了：立刻重排一次。</summary>
        internal static void OnPanelGeometryChanged(PixelWindow window)
        {
            try { Refresh(); }
            catch (Exception e) { Warn("dock on panel change: " + e.Message); }
        }

        // ---------------------------------------------------------------- 目标生灭

        private static void OnTargetDestroyed(Dock dock)
        {
            if (dock == null) return;
            DestroyDock(dock);
        }

        private static void OnTargetHidden(Dock dock, bool hidden)
        {
            if (dock == null || dock.Root == null) return;

            try { dock.Root.gameObject.SetActive(!hidden); }
            catch { }

            // 重新显示时窗口可能已经挪过位置，顺手对齐一次
            if (!hidden) PlaceCurrent(dock);
        }

        /// <summary>图鉴面板量到目标矩形后回喂一次，保证按钮和面板用同一份测量。</summary>
        internal static void SyncToInventory(Rect bounds)
        {
            var dock = Find("inventory");
            if (dock == null || dock.Root == null || dock.Parent == null) return;

            try { PlaceAt(dock.Root, dock.Parent, bounds); }
            catch (Exception e) { Warn("sync button: " + e.Message); }
        }

        /// <summary>按目标当前的矩形把这一枚按钮重新摆一次。</summary>
        private static void PlaceCurrent(Dock dock)
        {
            if (dock == null || dock.Root == null || dock.Parent == null || dock.Target == null) return;

            Rect bounds;
            try { bounds = DockedLayout.ToWorldRect(dock.Target); }
            catch { return; }

            if (bounds.width < 2f || bounds.height < 2f) return;

            try { PlaceAt(dock.Root, dock.Parent, bounds); }
            catch (Exception e) { Warn("place button: " + e.Message); }
        }

        // ---------------------------------------------------------------- 每枚按钮

        private static void Update(string key, RectTransform target)
        {
            var dock = Find(key);

            if (target == null)
            {
                // 目标对象没了 → 按钮跟着消失
                if (dock != null) DestroyDock(dock);
                return;
            }

            RectTransform canvas;
            try { canvas = AnchorTarget.RootCanvas(target); }
            catch { canvas = null; }

            if (canvas == null) return;

            // 目标换了实例（窗口重建）或换了画布：旧按钮作废
            if (dock != null && (dock.Target != target || dock.Parent != canvas))
                DestroyDock(dock);

            if (dock == null)
            {
                dock = Create(key, target, canvas);
                if (dock == null) return;
            }

            dock.Target = target;

            Rect bounds;
            try { bounds = DockedLayout.ToWorldRect(target); }
            catch { return; }

            if (bounds.width < 2f || bounds.height < 2f) return;

            try { PlaceAt(dock.Root, canvas, bounds); }
            catch (Exception e) { Warn("place button: " + e.Message); }
        }

        private static Dock Create(string key, RectTransform target, RectTransform canvas)
        {
            var manager = CustomUIManager.Instance;
            if (manager == null) return null;

            RectTransform rect;
            try
            {
                var go = new GameObject(ButtonName, typeof(RectTransform));
                rect = (RectTransform)go.transform;
                rect.SetParent(canvas, false);
            }
            catch (Exception e) { Warn("create button: " + e.Message); return null; }

            var dock = new Dock { Key = key, Target = target, Root = rect, Parent = canvas };
            _docks.Add(dock);

            // 把按钮的寿命挂在目标对象上：目标销毁 → 按钮跟着销毁，不用轮询
            try
            {
                var watcher = target.GetComponent<DockWatcher>();
                if (watcher == null) watcher = target.gameObject.AddComponent<DockWatcher>();
                watcher.Dock = dock;
            }
            catch (Exception e) { Warn("watch target: " + e.Message); }

            try
            {
                var builder = manager.BuildInto(rect, false, ModEntry.MOD_ID + ".button." + key);

                var icon = LoadIcon();
                if (icon != null)
                {
                    // 图标按钮：`button.png` 直接当按钮表面，不再显示文字
                    var image = builder.AddImage(icon, IconSize, IconSize, "open").Last;
                    if (image != null)
                    {
                        // 图片元素本身没有点击能力，要挂 Unity Button 才吃得到射线
                        CatalogWindow.MakeClickable(image, delegate { OpenFor(dock); });
                    }
                }
                else
                {
                    // 读不到图就退回文字按钮，至少能点
                    _width = TextWidth;
                    _height = TextHeight;
                    builder.AddButton(ModEntry.T("btn_open"), delegate { OpenFor(dock); }, "open");
                }

                CustomUIBuilder.RebuildLayout(rect);

                var element = builder.Last;
                if (element != null && element.Rect != null &&
                    (element.Rect.rect.width < 1f || element.Rect.rect.height < 1f))
                {
                    element.Rect.sizeDelta = new Vector2(_width, _height);
                }
            }
            catch (Exception e) { Warn("build button: " + e.Message); }

            // 新建的元素 layer 默认回到 0，统一刷成窗口那一层
            try
            {
                int layer = target.gameObject.layer;
                rect.gameObject.layer = layer;

                var parts = rect.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < parts.Length; i++) parts[i].gameObject.layer = layer;
            }
            catch { }

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[button] dock '" + key + "' -> " + DockedLayout.HierarchyPath(target)
                                 + "  t=" + Time.unscaledTime.ToString("F2") + " frame=" + Time.frameCount);

            return dock;
        }

        private static void OpenFor(Dock dock)
        {
            if (dock == null) return;
            CatalogWindow.OpenAt(dock.Target);
        }

        // ---------------------------------------------------------------- 摆放

        private static void PlaceAt(RectTransform rect, RectTransform parent, Rect bounds, bool alignTop = false)
        {
            if (rect == null || parent == null) return;
            if (bounds.width < 2f || bounds.height < 2f) return;

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
            rect.sizeDelta = new Vector2(_width, _height);
            rect.anchoredPosition = inside ? new Vector2(-Gap, offsetY) : new Vector2(Gap, offsetY);
        }

        // ---------------------------------------------------------------- 生灭

        private static Dock Find(string key)
        {
            for (int i = 0; i < _docks.Count; i++)
            {
                if (_docks[i] != null && _docks[i].Key == key) return _docks[i];
            }

            return null;
        }

        private static void DestroyDock(Dock dock)
        {
            if (dock == null) return;

            try { if (dock.Root != null) UnityEngine.Object.Destroy(dock.Root.gameObject); }
            catch { }

            dock.Root = null;
            dock.Target = null;
            _docks.Remove(dock);
        }

        /// <summary>目标被销毁后 Unity 会把引用变 null，这类登记直接清掉。</summary>
        private static void Cleanup()
        {
            for (int i = _docks.Count - 1; i >= 0; i--)
            {
                var dock = _docks[i];
                if (dock == null || dock.Root == null || dock.Target == null || dock.Parent == null)
                {
                    if (dock != null) DestroyDock(dock);
                    else _docks.RemoveAt(i);
                }
            }
        }

        private static void Warn(string message)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("[button] " + message);
        }
    }
}
