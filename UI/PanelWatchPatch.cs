using System;
using System.Reflection;

using HarmonyLib;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 监听游戏窗口（PixelWindow）的**显示/隐藏和布局变化**。
    ///
    /// 图鉴按钮要贴在物品栏右边缘，物品栏位置一变按钮就得跟。之前是每秒轮询
    /// FindObjectsOfType 自己量，两个问题：最多 1 秒延迟；别的模组挪动窗口时
    /// 我们只能靠轮询撞上。
    ///
    /// 这里改为钩游戏自己的窗口生命周期方法——任何模组（包括原版）挪窗口、
    /// 改尺寸、重新贴靠，都必然经过这些函数，于是按钮跟着一起动，
    /// 不需要知道是谁改了它。
    ///
    /// 钩点全部是「可能影响窗口矩形」的方法：
    ///   Show / Hide                 显示、隐藏
    ///   Validate                    布局校验（改内容、改尺寸后都会走）
    ///   FixBounds                   边界修正（拖动、贴边吸附）
    ///   ResizePixels                尺寸变化
    ///   ReclampDockedWindows        全局重新贴靠（分辨率、黑边变化）
    /// </summary>
    internal static class PanelWatchPatch
    {
        private static bool _installed;

        internal static void Install(string modId)
        {
            if (_installed) return;
            _installed = true;

            try
            {
                var harmony = new Harmony(modId + ".panelwatch");
                var pixelWindow = typeof(PixelWindow);
                int patched = 0;

                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                var staticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;

                var instancePostfix = new HarmonyMethod(typeof(PanelWatchPatch)
                    .GetMethod(nameof(OnWindowChanged), BindingFlags.NonPublic | BindingFlags.Static));
                var staticPostfix = new HarmonyMethod(typeof(PanelWatchPatch)
                    .GetMethod(nameof(OnWindowLayoutGlobal), BindingFlags.NonPublic | BindingFlags.Static));

                foreach (var name in new[] { "Show", "Hide", "Validate", "FixBounds", "ResizePixels" })
                {
                    foreach (var method in pixelWindow.GetMethods(flags))
                    {
                        if (method.Name != name || method.IsAbstract) continue;
                        if (method.ContainsGenericParameters) continue;

                        bool hasBody = false;
                        try { hasBody = method.GetMethodBody() != null; } catch { }
                        if (!hasBody) continue;

                        harmony.Patch(method, postfix: instancePostfix);
                        patched++;
                    }
                }

                foreach (var method in pixelWindow.GetMethods(staticFlags))
                {
                    if (method.Name != "ReclampDockedWindows") continue;
                    if (method.ContainsGenericParameters) continue;

                    bool hasBody = false;
                    try { hasBody = method.GetMethodBody() != null; } catch { }
                    if (!hasBody) continue;

                    harmony.Patch(method, postfix: staticPostfix);
                    patched++;
                }

                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[panelwatch] patched " + patched + " PixelWindow lifecycle method(s)");
            }
            catch (Exception e)
            {
                if (ModEntry.Log != null)
                    ModEntry.Log.Warning("[panelwatch] install failed (launcher button will not follow panel geometry): " + e.Message);
            }
        }

        /// <summary>实例方法钩子：任何窗口的显示/隐藏/布局变化。</summary>
        private static void OnWindowChanged(PixelWindow __instance)
        {
            try { LauncherButton.OnPanelGeometryChanged(__instance); }
            catch { }

            // 图鉴面板跟着物品栏一起变尺寸（窗口被拖动缩放的场景都走这里）
            try { CatalogWindow.OnInventoryGeometryChanged(); }
            catch { }
        }

        /// <summary>静态方法钩子：全局重新贴靠（分辨率/黑边变化）。</summary>
        private static void OnWindowLayoutGlobal()
        {
            try { LauncherButton.OnPanelGeometryChanged(null); }
            catch { }

            try { CatalogWindow.OnInventoryGeometryChanged(); }
            catch { }
        }
    }
}
