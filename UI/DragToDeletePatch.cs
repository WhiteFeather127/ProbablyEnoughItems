using System;
using System.Collections.Generic;
using System.Reflection;

using HarmonyLib;
using UnityEngine;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 把物品拖到「图鉴」上 → 直接删除（不进垃圾桶）。
    ///
    /// 删除走游戏自己的 <c>GraphUtils.TryDestroyAll(list, -1)</c>：数量传负数表示「全部销毁」，
    /// 这是 `PlayerStore.RemoveItemFromStoreInventory` 内部用的同一句，
    /// 会先把物品从所属库存摘掉再销毁，不留半条记录。
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

                // 框选拖拽走另一个处理器：一次拖一整个列表，单独钩它的松手
                var multiRelease = AccessTools.Method(typeof(ItemMultiSelectHandler), "OnEventRelease");
                if (multiRelease != null)
                {
                    harmony.Patch(multiRelease, prefix: new HarmonyMethod(typeof(DragToDeletePatch)
                        .GetMethod(nameof(BeforeMultiRelease), BindingFlags.NonPublic | BindingFlags.Static)));
                }

                if (ModEntry.Log != null)
                {
                    ModEntry.Log.Log("[dragdelete] patched ItemMouseDragHandler.OnEventRelease"
                        + (multiRelease != null ? " + ItemMultiSelectHandler.OnEventRelease" : ""));
                }
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
                // 只认作弊模式：那个开关同时管「点击拿取」和「拖过来删除」，
                // 普通模式误拖到图鉴上不该把东西弄没
                if (!CatalogWindow.TakeMode) return;

                if (!IsPointerOverCatalog(Input.mousePosition)) return;

                // 直接销毁：数量 -1 = 全部涂掉，和游戏自己清店铺库存用的是同一句
                var doomed = new List<GameItem>(1);
                doomed.Add(__state);

                GraphUtils.TryDestroyAll(doomed, -1);

                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[dragdelete] destroyed " + __state.identifier);
            }
            catch (Exception e)
            {
                if (ModEntry.Log != null)
                    ModEntry.Log.Warning("[dragdelete] trash failed: " + e.Message);
            }
        }

        private static FieldInfo _multiItems;

        /// <summary>
        /// 多选处理器当前拖着的那些元素（私有字段 `selectedItems`）。
        /// 注意它的真实类型是 <c>List&lt;GameItemElement&gt;</c>，不是 `List&lt;GameItem&gt;`
        /// （泛型没有协变，直接 cast 会得到 null），所以要按 IEnumerable 逐个转。
        /// </summary>
        private static System.Collections.IEnumerable RawSelected(ItemMultiSelectHandler handler)
        {
            if (handler == null) return null;

            try
            {
                if (_multiItems == null)
                {
                    _multiItems = typeof(ItemMultiSelectHandler).GetField(
                        "selectedItems", BindingFlags.NonPublic | BindingFlags.Instance);
                }

                if (_multiItems == null) return null;

                return _multiItems.GetValue(handler) as System.Collections.IEnumerable;
            }
            catch { return null; }
        }

        private static List<GameItem> SelectedItems(ItemMultiSelectHandler handler)
        {
            var raw = RawSelected(handler);
            if (raw == null) return null;

            var list = new List<GameItem>();
            foreach (var item in raw)
            {
                var gameItem = item as GameItem;
                if (gameItem != null) list.Add(gameItem);
            }

            return list;
        }

        /// <summary>多选里第一个元素，用来给红底量尺寸（群组拖拽时没有单只的“当前物品”）。</summary>
        internal static GameItemElement FirstSelectedElement()
        {
            var all = SelectedElements();
            return all != null && all.Count > 0 ? all[0] : null;
        }

        /// <summary>当前框选拖拽中的全部元素（每只都要各自垫一块红底）。</summary>
        internal static List<GameItemElement> SelectedElements()
        {
            var raw = RawSelected(ItemMultiSelectHandler.current);
            if (raw == null) return null;

            var list = new List<GameItemElement>();
            foreach (var item in raw)
            {
                var element = item as GameItemElement;
                if (element != null) list.Add(element);
            }

            return list;
        }

        private static PropertyInfo _fakeMiddle;

        /// <summary>
        /// 框选拖拽时每只物品的**幽灵视觉节点**：`GameItemElement.fakeMiddle`。
        /// <c>ToggleFakeBackground(true, …)</c> 会把它挂到 <c>RenderHandler.overlayRoot</c> 下，
        /// <c>MoveGhosts</c> 每帧带平滑插值移动它。红底挂到它下面就和物品完全同步。
        /// </summary>
        internal static RectTransform GhostNode(GameItemElement element)
        {
            if (element == null) return null;

            try
            {
                if (_fakeMiddle == null)
                {
                    _fakeMiddle = typeof(GameItemElement).GetProperty(
                        "fakeMiddle", BindingFlags.Public | BindingFlags.Instance);
                }

                if (_fakeMiddle == null) return null;

                var node = _fakeMiddle.GetValue(element, null) as TreeNodeRender;
                return node == null ? null : node.rectTransform;
            }
            catch { return null; }
        }

        internal static bool IsGroupDragging()
        {
            try
            {
                var multi = ItemMultiSelectHandler.current;
                return multi != null && multi.IsGroupDragging;
            }
            catch { return false; }
        }

        /// <summary>
        /// 框选一批物品拖到图鉴上松手：整批删掉，并且不让原逻辑把它们摆进图鉴。
        /// 返回 false 表示跳过游戏自己的落点处理。
        /// </summary>
        private static bool BeforeMultiRelease(ItemMultiSelectHandler __instance)
        {
            try
            {
                if (!CatalogWindow.TakeMode) return true;
                if (__instance == null || !__instance.IsGroupDragging) return true;
                if (!IsPointerOverCatalog(Input.mousePosition)) return true;

                var items = SelectedItems(__instance);
                if (items == null || items.Count == 0) return true;

                var doomed = new List<GameItem>(items);

                // 先收掉拖拽状态（幽灵图 / 选中框 / 副本位置），再销毁，
                // 否则 Cancel 会把已经不存在的物品往回搬
                try { __instance.Cancel(); } catch { }

                GraphUtils.TryDestroyAll(doomed, -1);

                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[dragdelete] destroyed " + doomed.Count + " (multi)");

                return false;
            }
            catch (Exception e)
            {
                if (ModEntry.Log != null)
                    ModEntry.Log.Warning("[dragdelete] multi failed: " + e.Message);

                return true;
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
