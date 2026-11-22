using UnityEngine;
using UnityEngine.EventSystems;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 详情窗口整窗拖动。挂在滚动区 viewport 上（比 ScrollRect 更靠近被点中的内容，
    /// 拖拽事件不会先被 ScrollRect 吃掉），以及没有原生 UIDragHandler 时的窗口根节点上。
    /// 物品格子自带 <see cref="ItemCellDragBlocker"/>，滚动条由 Scrollbar 自己消费拖拽，
    /// 这两处不会把窗口带走。
    /// </summary>
    internal sealed class WindowDragHandler : MonoBehaviour, IBeginDragHandler, IDragHandler
    {
        /// <summary>要移动的窗口根节点（挂在 viewport 上时它和 transform 不是同一个）。</summary>
        internal RectTransform Target;

        private Canvas _canvas;

        private RectTransform Rect
        {
            get { return Target != null ? Target : transform as RectTransform; }
        }

        private void Awake()
        {
            _canvas = GetComponentInParent<Canvas>();
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            if (_canvas == null) _canvas = GetComponentInParent<Canvas>();
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;

            var rect = Rect;
            if (rect == null) return;

            float scale = _canvas != null && _canvas.scaleFactor > 0f ? _canvas.scaleFactor : 1f;
            rect.anchoredPosition += eventData.delta / scale;
        }
    }

    /// <summary>
    /// 物品格子上的拖拽拦截：空实现，只让拖拽事件停在格子上（含格子里的文字说明），
    /// 不冒泡到 <see cref="WindowDragHandler"/> 把窗口一起拖走。
    /// </summary>
    internal sealed class ItemCellDragBlocker : MonoBehaviour,
        IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public void OnBeginDrag(PointerEventData eventData) { }
        public void OnDrag(PointerEventData eventData) { }
        public void OnEndDrag(PointerEventData eventData) { }
    }
}
