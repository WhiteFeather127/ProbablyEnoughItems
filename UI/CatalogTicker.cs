using UnityEngine;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 图鉴打开期间的常驻节拍器：不依赖鼠标悬停，按固定间隔刷新当前页的物品数据与图标。
    /// 真正的节奏控制放在 CatalogWindow.AutoRefreshTick 里（内部有时间门限与分批）。
    /// </summary>
    public sealed class CatalogTicker : MonoBehaviour
    {
        private static CatalogTicker _instance;

        internal static void Ensure()
        {
            if (_instance != null) return;

            try
            {
                // 复用已存在的节拍器（域重载后 _instance 会丢，但物体还在）
                var existing = GameObject.Find("ItemCatalogTicker");
                if (existing != null)
                {
                    _instance = existing.GetComponent<CatalogTicker>();
                    if (_instance != null) return;
                }

                var go = new GameObject("ItemCatalogTicker");
                UnityEngine.Object.DontDestroyOnLoad(go);   // 跨场景存活：探测/重锚靠它驱动
                _instance = go.AddComponent<CatalogTicker>();
            }
            catch { /* 场景切换中，下次打开窗口再建 */ }
        }

        private void Update()
        {
            // 窗口事件登记的重排在这里执行（事件回调里不碰画布，见 LauncherButton.RequestRefresh）
            try { LauncherButton.TickPendingRefresh(); } catch { }

            CatalogWindow.AutoRefreshTick();
            CatalogWindow.DragTick();

            // 拖拽中也每帧刷一下红底提示（没在拖时这里只读一个静态字段就返回）
            try { CatalogWindow.DragHighlightTick(); } catch { }

            // 详情窗口的客人立绘：同一客人多种外观时每秒轮播（内部自己掐间隔）
            RecipeWindow.TickPortrait();

            // 模组通过 ItemCatalogApi.NotifyItemsChanged() 报过变更就在这里补（没有标记时只是一次判断）
            if (Catalog.CatalogDataBuilder.ProcessPendingAppend())
            {
                try { CatalogWindow.WarmupSpritesOnly(); } catch { }
            }

            // 运行时来源（配方 / 掉落表）探测：内部节流，数据 ready 后自动打一次日志
            try { Catalog.SourceProbe.Tick(); } catch { }
        }
    }
}
