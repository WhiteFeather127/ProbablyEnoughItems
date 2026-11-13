using System;
using UnityEngine;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 完全按原版 <see cref="ItemTooltipHandler.OnEventUpdate"/> 的方式驱动提示框：
    /// 每帧用 Input.mousePosition + ItemTooltipHandler.tooltipTranslation 作为位置，
    /// 用 ItemTooltipHandler.BASE_TOOLTIP_CHAR_WIDTH（机器类会乘倍数）作为宽度，
    /// 并且**每帧重新构建内容**——Item Details 的类型徽章依赖这个时间戳才不会过期。
    /// 拖拽 UI、电影模式时暂停显示。
    /// </summary>
    public sealed class TooltipFollower : MonoBehaviour
    {
        private static TooltipFollower _instance;

        private RichText _rich;
        private Func<RichText> _refresh;
        private int _width = ItemTooltipHandler.BASE_TOOLTIP_CHAR_WIDTH;
        private bool _active;
        private Vector2 _lastPosition = new Vector2(-99999f, -99999f);

        /// <summary>开始跟随鼠标显示这份提示框；refresh 每帧调用一次以重建内容。</summary>
        internal static void Begin(RichText rich, int width, Func<RichText> refresh)
        {
            if (rich == null) return;

            Ensure();
            if (_instance == null) return;

            _instance._rich = rich;
            _instance._refresh = refresh;
            _instance._width = width;
            _instance._active = true;
            _instance._lastPosition = new Vector2(-99999f, -99999f);
            _instance.Redraw(true);
        }

        /// <summary>停止跟随（离开图标、翻页、关窗）。</summary>
        internal static void End()
        {
            if (_instance == null) return;

            _instance._active = false;
            _instance._rich = null;
            _instance._refresh = null;
        }

        /// <summary>跟随是否正在进行（自愈检查用）。</summary>
        internal static bool IsActive
        {
            get { return _instance != null && _instance._active; }
        }

        private static void Ensure()
        {
            if (_instance != null) return;

            try
            {
                var go = new GameObject("ItemCatalogTooltipFollower");
                _instance = go.AddComponent<TooltipFollower>();
            }
            catch { /* 场景切换中，下一帧再建 */ }
        }

        private void Update()
        {
            Redraw(false);
        }

        private void Redraw(bool force)
        {
            if (!_active || _rich == null) return;

            var overlay = OverlayHandler.current;
            if (overlay == null) return;

            // 原版在电影模式和拖拽 UI 时收起提示框
            var store = PlayerStore.Instance;
            if (store != null && store.cinematicMode) return;
            if (UIDragHandler.isDraggingAnyUI) return;

            // 每帧重建，和原版一致：Item Details 的徽章靠这一步刷新时间戳
            if (_refresh != null)
            {
                try
                {
                    var next = _refresh();
                    if (next != null)
                    {
                        _rich = next;
                        force = true;
                    }
                }
                catch { /* 重建失败就继续用旧内容 */ }
            }

            Vector2 position = Input.mousePosition;
            if (!force && (position - _lastPosition).sqrMagnitude < 0.25f) return;

            _lastPosition = position;

            var handler = ItemTooltipHandler.current;
            Vector2 translation = handler == null ? Vector2.zero : handler.tooltipTranslation;

            try
            {
                overlay.ShowTooltip(position, translation, _rich, _width);
            }
            catch
            {
                _active = false;
            }
        }
    }
}
