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

        /// <summary>单帧 mod 总耗时超过这个值就记一行（毫秒）。</summary>
        private const double TickLogThresholdMs = 6;

        private static int _tickSlowLogs;

        private static double Ms(long from, long to)
        {
            return (to - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private void Update()
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

            // 窗口事件登记的重排在这里执行（事件回调里不碰画布，见 LauncherButton.RequestRefresh）
            try { LauncherButton.TickPendingRefresh(); } catch { }
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();

            CatalogWindow.AutoRefreshTick();
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();

            CatalogWindow.DragTick();
            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();

            // 拖拽中也每帧刷一下红底提示（没在拖时这里只读一个静态字段就返回）
            try { CatalogWindow.DragHighlightTick(); } catch { }
            long t4 = System.Diagnostics.Stopwatch.GetTimestamp();

            // 详情窗口的客人立绘：同一客人多种外观时每秒轮播（内部自己掐间隔）
            RecipeWindow.TickPortrait();
            long t5 = System.Diagnostics.Stopwatch.GetTimestamp();

            // 模组通过 ItemCatalogApi.NotifyItemsChanged() 报过变更就在这里补（没有标记时只是一次判断）
            if (Catalog.CatalogDataBuilder.ProcessPendingAppend())
            {
                try { CatalogWindow.WarmupSpritesOnly(); } catch { }
            }
            long t6 = System.Diagnostics.Stopwatch.GetTimestamp();

            // 运行时来源（配方 / 掉落表）探测：只在 F9「Catalog: Probe Sources」手动触发。
            // 自动探会在游戏中途反复遍历全部物品并写一份报告文件，造成可感知的卡顿。

            // 拆枪探测后的 ratchet 静音恢复（2 秒后；没探测时只读一个静态字段）
            try { GunSoundPatch.Tick(); } catch { }
            long t7 = System.Diagnostics.Stopwatch.GetTimestamp();

            // 帧级归因：mod 自己这一帧到底花在哪一段（排查偶发卡用）
            double total = Ms(t0, t7);
            if (total >= TickLogThresholdMs && ModEntry.Log != null && _tickSlowLogs < 300)
            {
                _tickSlowLogs++;
                ModEntry.Log.Log("[perf] tick total=" + total.ToString("F1")
                                 + " launcher=" + Ms(t0, t1).ToString("F1")
                                 + " catalog=" + Ms(t1, t2).ToString("F1")
                                 + " drag=" + Ms(t2, t3).ToString("F1")
                                 + " dragHi=" + Ms(t3, t4).ToString("F1")
                                 + " portrait=" + Ms(t4, t5).ToString("F1")
                                 + " append=" + Ms(t5, t6).ToString("F1")
                                 + " gun=" + Ms(t6, t7).ToString("F1"));
            }
        }
    }
}
