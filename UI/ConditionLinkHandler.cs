using System;

using TMPro;

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 让 TMP 文本里的 &lt;link=liquid:xxx&gt; 片段可点击：
    /// 左键打开该液体的来源、右键打开用途（和成分名一致）。
    /// 同时只给正在检索的那个 link 画绿底（用独立 Image，避免富文本底纹改掉字体颜色）。
    /// </summary>
    internal sealed class ConditionLinkHandler : MonoBehaviour, IPointerClickHandler
    {
        internal TMP_Text Text;
        internal Action<string, bool> OnLink;

        /// <summary>需要垫绿底的 link id（liquid:xxx）；null = 不画。</summary>
        internal string HighlightLinkId;

        private Image _mark;
        private RectTransform _markRect;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (Text == null || OnLink == null) return;

            int index;
            try
            {
                index = TMP_TextUtilities.FindIntersectingLink(Text, eventData.position,
                                                               eventData.pressEventCamera);
            }
            catch { return; }

            if (index < 0) return;

            string id = null;
            try { id = Text.textInfo.linkInfo[index].GetLinkID(); } catch { }
            if (string.IsNullOrEmpty(id)) return;

            OnLink(id, eventData.button == PointerEventData.InputButton.Right);
        }

        private void LateUpdate()
        {
            try
            {
                EnsureMark();
                SyncMark();
            }
            catch { }
        }

        private void SyncMark()
        {
            if (_mark == null || Text == null || string.IsNullOrEmpty(HighlightLinkId))
            {
                if (_mark != null) _mark.enabled = false;
                return;
            }

            var info = Text.textInfo;
            if (info == null || info.linkCount == 0) { _mark.enabled = false; return; }

            int linkIndex = -1;
            for (int i = 0; i < info.linkCount; i++)
            {
                if (string.Equals(info.linkInfo[i].GetLinkID(), HighlightLinkId, StringComparison.Ordinal))
                {
                    linkIndex = i;
                    break;
                }
            }

            if (linkIndex < 0) { _mark.enabled = false; return; }

            var link = info.linkInfo[linkIndex];
            int first = link.linkTextfirstCharacterIndex;
            int last = Mathf.Min(first + link.linkTextLength - 1, info.characterCount - 1);

            bool has = false;
            Vector3 min = Vector3.zero;
            Vector3 max = Vector3.zero;

            for (int i = first; i <= last; i++)
            {
                var infoChar = info.characterInfo[i];
                if (!infoChar.isVisible) continue;

                if (!has)
                {
                    min = infoChar.bottomLeft;
                    max = infoChar.topRight;
                    has = true;
                }
                else
                {
                    min = Vector3.Min(min, infoChar.bottomLeft);
                    max = Vector3.Max(max, infoChar.topRight);
                }
            }

            if (!has) { _mark.enabled = false; return; }

            var parent = Text.rectTransform.parent as RectTransform;
            if (parent == null) { _mark.enabled = false; return; }

            if (_markRect.parent != parent) _markRect.SetParent(parent, false);

            Vector3 worldMin = Text.transform.TransformPoint(min);
            Vector3 worldMax = Text.transform.TransformPoint(max);
            Vector3 localMin = parent.InverseTransformPoint(worldMin);
            Vector3 localMax = parent.InverseTransformPoint(worldMax);

            _markRect.anchorMin = Vector2.zero;
            _markRect.anchorMax = Vector2.zero;
            _markRect.pivot = Vector2.zero;
            _markRect.anchoredPosition = new Vector2(localMin.x - 2f, localMin.y);
            _markRect.sizeDelta = new Vector2(Mathf.Max(0f, localMax.x - localMin.x) + 4f,
                                              Mathf.Max(0f, localMax.y - localMin.y));

            _mark.enabled = true;

            // 画在文字后面：保持在 TMP 文本对象之前一层
            if (_markRect.GetSiblingIndex() >= Text.transform.GetSiblingIndex())
                _markRect.SetSiblingIndex(Mathf.Max(0, Text.transform.GetSiblingIndex() - 1));
        }

        private void EnsureMark()
        {
            if (_mark != null) return;

            var parent = Text == null ? null : Text.rectTransform.parent as RectTransform;
            if (parent == null) return;

            var go = new GameObject("conditionLiquidMark",
                typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);

            _markRect = go.GetComponent<RectTransform>();
            _mark = go.GetComponent<Image>();
            _mark.sprite = null;   // 无 sprite 的 Image 直接画纯色块
            _mark.color = GameColors.HighlightGreen;
            _mark.raycastTarget = false;

            var ignore = go.AddComponent<LayoutElement>();
            ignore.ignoreLayout = true;
        }

        private void OnEnable()
        {
            EnsureMark();
        }
    }
}
