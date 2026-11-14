using System;
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
        private const float ButtonWidth = 100f;
        private const float ButtonHeight = 30f;
        private const float Gap = 4f;
        private const float TickSeconds = 0.2f;

        /// <summary>一枚按钮 = 一个贴靠目标。</summary>
        private sealed class Dock
        {
            internal string Key;             // "inventory" / "scene"，仅用于日志
            internal RectTransform Target;   // 贴靠对象（按钮因它而生）
            internal RectTransform Root;     // 按钮容器（挂在画布根上）
            internal RectTransform Parent;   // 画布根
        }

        private static readonly List<Dock> _docks = new List<Dock>();
        private static float _nextTickAt;

        internal static void OnGameLoaded()
        {
            CatalogWindow.ResetForScene();

            // 换场景后旧画布连同按钮一起没了，清掉失效登记
            Cleanup();
            _nextTickAt = 0f;

            try { Tick(); }
            catch (Exception e) { Warn("inject launch button: " + e.Message); }
        }

        /// <summary>常驻节拍（由 CatalogTicker 每帧调用），内部 0.2 秒门限。</summary>
        internal static void Tick()
        {
            if (Time.unscaledTime < _nextTickAt) return;
            _nextTickAt = Time.unscaledTime + TickSeconds;

            // 主场景：物品栏窗口
            var inventory = AnchorTarget.ResolveInventoryNode();
            Update("inventory", inventory);

            // 店铺场景：底部那排 UI（地面 / 口袋机 / 管口 / 口袋边）。
            // 物品栏窗口在的时候不去猜底部区块，否则主场景里会多长出一枚按钮。
            Update("scene", inventory != null ? null : AnchorTarget.ResolveScenePanel());

            Cleanup();
        }

        /// <summary>窗口几何变了：立刻重排一次（不等节拍）。</summary>
        internal static void OnPanelGeometryChanged(PixelWindow window)
        {
            _nextTickAt = 0f;
            try { Tick(); }
            catch (Exception e) { Warn("dock on panel change: " + e.Message); }
        }

        /// <summary>图鉴面板量到目标矩形后回喂一次，保证按钮和面板用同一份测量。</summary>
        internal static void SyncToInventory(Rect bounds)
        {
            var dock = Find("inventory");
            if (dock == null || dock.Root == null || dock.Parent == null) return;

            try { PlaceAt(dock.Root, dock.Parent, bounds); }
            catch (Exception e) { Warn("sync button: " + e.Message); }
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

            try
            {
                var builder = manager.BuildInto(rect, false, ModEntry.MOD_ID + ".button." + key);
                builder.AddButton(ModEntry.T("btn_open"), delegate { OpenFor(dock); }, "open");
                CustomUIBuilder.RebuildLayout(rect);

                var element = builder.Last;
                if (element != null && element.Rect != null &&
                    (element.Rect.rect.width < 1f || element.Rect.rect.height < 1f))
                {
                    element.Rect.sizeDelta = new Vector2(ButtonWidth, ButtonHeight);
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
                ModEntry.Log.Log("[button] dock '" + key + "' -> " + DockedLayout.HierarchyPath(target));

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
            rect.sizeDelta = new Vector2(ButtonWidth, ButtonHeight);
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
