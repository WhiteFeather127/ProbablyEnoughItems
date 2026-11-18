using UnityEngine;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 让一个装饰用 RectTransform（例如文字底纹）每帧跟随目标控件的位置和尺寸。
    /// 目标在布局组里时，sizeDelta / 锚点由布局决定，必须每帧同步才能贴住。
    /// </summary>
    internal sealed class RectFollower : MonoBehaviour
    {
        internal RectTransform Target;

        private void LateUpdate()
        {
            if (Target == null) return;

            var rect = transform as RectTransform;
            if (rect == null || rect.parent != Target.parent) return;

            rect.anchorMin = Target.anchorMin;
            rect.anchorMax = Target.anchorMax;
            rect.pivot = Target.pivot;
            rect.anchoredPosition = Target.anchoredPosition;
            rect.sizeDelta = Target.sizeDelta;
            rect.localScale = Target.localScale;

            // 画在目标之前（文字下面）
            if (rect.GetSiblingIndex() >= Target.GetSiblingIndex())
                rect.SetSiblingIndex(Mathf.Max(0, Target.GetSiblingIndex() - 1));
        }
    }
}
