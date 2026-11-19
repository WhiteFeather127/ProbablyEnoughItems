using UnityEngine;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 原版底色（与游戏内物品格子高亮一致）。全局共用，不要在别处再写字面量。
    /// </summary>
    internal static class GameColors
    {
        /// <summary>原版绿底（物品命中检索时的高亮色），#5D6B3F。</summary>
        internal static readonly Color HighlightGreen = new Color(93f / 255f, 107f / 255f, 63f / 255f, 1f);

        /// <summary>原版红底（删除 / 危险操作），#813832。</summary>
        internal static readonly Color HighlightRed = new Color(129f / 255f, 56f / 255f, 50f / 255f, 1f);

        /// <summary>取同色但指定透明度（拖拽底纹要半透明，别挡住图标）。</summary>
        internal static Color WithAlpha(Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }
    }
}
