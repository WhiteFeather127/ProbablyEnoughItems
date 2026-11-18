using System;

using TMPro;

using UnityEngine;
using UnityEngine.EventSystems;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 让 TMP 文本里的 &lt;link=liquid:xxx&gt; 片段可点击：
    /// 左键打开该液体的来源、右键打开用途（和成分名一致）。
    /// 只挂在我们自己造的 MachineryText 上，不改原版文本。
    /// </summary>
    internal sealed class ConditionLinkHandler : MonoBehaviour, IPointerClickHandler
    {
        internal TMP_Text Text;
        internal Action<string, bool> OnLink;

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
    }
}
