using System;
using System.Reflection;

using HarmonyLib;
using UnityEngine;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 把物品拖到「图鉴」上 → 丢进店里的垃圾桶。
    ///
    /// 不去自己删物品：游戏给了 <c>ModHelper.MoveItemToTrash(GameItem)</c>，
    /// 它做的事就是把物品塞进 `EmporiumEntry.trashInvElement`（店里那口垃圾桶），
    /// 和玩家手动往垃圾桶里拖完全同一条路径，库存、堆叠、存档都由游戏自己收尾。
    ///
    /// 判定时机：拖拽统一走 <c>ItemMouseDragHandler</c>（静态 `current`），
    /// 松手在私有方法 `OnEventRelease` 里决定落到哪个格子。
    /// 这里在它执行**之后**再看一眼鼠标位置：
    ///   · 拖动物品先按游戏逻辑归位（落在空白处就回原格）；
    ///   · 如果松手时指针压在图鉴按钮/面板上，就把它丢进垃圾桶。
    /// 这样不用拦截原逻辑，也不会把游戏的落点判定搞乱。
    /// </summary>
    internal static class DragToDeletePatch
    {
        private static bool _installed;

        internal static void Install(string modId)
        {
            if (_installed) return;
            _installed = true;

            try
            {
                var harmony = new Harmony(modId + ".dragdelete");

                var release = AccessTools.Method(typeof(ItemMouseDragHandler), "OnEventRelease");
                if (release == null)
                {
                    if (ModEntry.Log != null)
                        ModEntry.Log.Warning("[dragdelete] ItemMouseDragHandler.OnEventRelease not found");
                    return;
                }

                harmony.Patch(
                    release,
                    prefix: new HarmonyMethod(typeof(DragToDeletePatch)
                        .GetMethod(nameof(BeforeRelease), BindingFlags.NonPublic | BindingFlags.Static)),
                    postfix: new HarmonyMethod(typeof(DragToDeletePatch)
                        .GetMethod(nameof(AfterRelease), BindingFlags.NonPublic | BindingFlags.Static)));

                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[dragdelete] patched ItemMouseDragHandler.OnEventRelease");
            }
            catch (Exception e)
            {
                if (ModEntry.Log != null)
                    ModEntry.Log.Warning("[dragdelete] install failed: " + e.Message);
            }
        }

        /// <summary>松手前把这只物品记下来（松手后 `currentItem` 可能已经被清空）。</summary>
        private static void BeforeRelease(ItemMouseDragHandler __instance, out GameItem __state)
        {
            __state = null;

            try
            {
                if (__instance == null) return;
                if (!__instance.IsDraggingItem) return;

                __state = __instance.GetCurrentItem();
            }
            catch { }
        }

        /// <summary>松手后：指针压在图鉴上就把物品丢进垃圾桶。</summary>
        private static void AfterRelease(GameItem __state)
        {
            if (__state == null) return;

            try
            {
                if (!IsPointerOverCatalog(Input.mousePosition)) return;

                // 垃圾桶属于店铺场景（EmporiumEntry.trashInvElement），别处没有可丢的地方
                if (EmporiumEntry.Instance == null) return;

                ModHelper.MoveItemToTrash(__state);

                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[dragdelete] trashed " + __state.identifier);
            }
            catch (Exception e)
            {
                if (ModEntry.Log != null)
                    ModEntry.Log.Warning("[dragdelete] trash failed: " + e.Message);
            }
        }

        /// <summary>指针是否压在图鉴按钮或图鉴面板上。</summary>
        private static bool IsPointerOverCatalog(Vector3 screenPosition)
        {
            var point = new Vector2(screenPosition.x, screenPosition.y);

            try
            {
                if (LauncherButton.IsPointerOver(point)) return true;
            }
            catch { }

            try
            {
                if (CatalogWindow.IsPointerOverPanel(point)) return true;
            }
            catch { }

            return false;
        }
    }
}
