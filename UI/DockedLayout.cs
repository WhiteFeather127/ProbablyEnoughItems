using UnityEngine;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 停靠布局计算：找出「物品栏实际占据的屏幕范围」，并把它换算成任意父节点下的归一化坐标，
    /// 让图鉴面板与按钮都能贴着物品栏右边缘、贴满屏幕其余空间。
    /// </summary>
    internal static class DockedLayout
    {
        /// <summary>物品栏内容的世界空间矩形；量不到时退回整块 Canvas 的矩形。</summary>
        internal static Rect InventoryContentRect(RectTransform root)
        {
            var rootRect = WorldRect(root);
            if (rootRect.width <= 1f || rootRect.height <= 1f) return rootRect;

            float rootArea = rootRect.width * rootRect.height;
            Rect union = new Rect();
            bool has = false;

            Accumulate(root, rootRect, rootArea, ref union, ref has, 0);

            if (!has || union.width < 32f || union.height < 8f) return rootRect;
            if (union.width * union.height > rootArea * 0.98f) return rootRect;
            if (union.width > rootRect.width) union.width = rootRect.width;

            return union;
        }

        /// <summary>节点在场景里的层级路径（最多 8 层），用于日志与配置填写。</summary>
        internal static string HierarchyPath(Transform node)
        {
            if (node == null) return "<null>";

            var stack = new System.Collections.Generic.List<string>(8);
            var current = node;
            while (current != null && stack.Count < 8)
            {
                stack.Add(current.name);
                current = current.parent;
            }

            var builder = new System.Text.StringBuilder(64);
            for (int i = stack.Count - 1; i >= 0; i--)
            {
                builder.Append(stack[i]);
                if (i > 0) builder.Append('/');
            }

            return builder.ToString();
        }

        /// <summary>节点自身在世界空间的矩形。</summary>
        internal static Rect ToWorldRect(RectTransform rectTransform)
        {
            return WorldRect(rectTransform);
        }

        /// <summary>世界坐标 → 相对 space 的归一化坐标（0..1）。</summary>
        internal static Vector2 ToNormalized(RectTransform space, Vector3 world)
        {
            var local = space.InverseTransformPoint(world);
            var rect = space.rect;
            return new Vector2((local.x - rect.xMin) / rect.width, (local.y - rect.yMin) / rect.height);
        }

        internal static Vector3 RightCenter(Rect worldRect)
        {
            return new Vector3(worldRect.xMax, worldRect.center.y, 0f);
        }

        internal static Vector3 BottomRight(Rect worldRect)
        {
            return new Vector3(worldRect.xMax, worldRect.yMin, 0f);
        }

        internal static Vector3 TopRight(Rect worldRect)
        {
            return new Vector3(worldRect.xMax, worldRect.yMax, 0f);
        }

        private static void Accumulate(RectTransform node, Rect rootRect, float rootArea,
            ref Rect union, ref bool has, int depth)
        {
            if (depth > 2) return;

            for (int i = 0; i < node.childCount; i++)
            {
                var child = node.GetChild(i) as RectTransform;
                if (child == null || !child.gameObject.activeInHierarchy) continue;

                var rect = WorldRect(child);
                if (rect.width > 4f && rect.height > 4f && rect.width * rect.height < rootArea * 0.98f)
                {
                    union = has ? Union(union, rect) : rect;
                    has = true;
                }

                Accumulate(child, rootRect, rootArea, ref union, ref has, depth + 1);
            }
        }

        private static Rect WorldRect(RectTransform rectTransform)
        {
            var corners = new Vector3[4];
            rectTransform.GetWorldCorners(corners);

            float xMin = corners[0].x, xMax = corners[0].x;
            float yMin = corners[0].y, yMax = corners[0].y;

            for (int i = 1; i < 4; i++)
            {
                if (corners[i].x < xMin) xMin = corners[i].x;
                if (corners[i].x > xMax) xMax = corners[i].x;
                if (corners[i].y < yMin) yMin = corners[i].y;
                if (corners[i].y > yMax) yMax = corners[i].y;
            }

            return new Rect(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        private static Rect Union(Rect a, Rect b)
        {
            float xMin = Mathf.Min(a.xMin, b.xMin);
            float yMin = Mathf.Min(a.yMin, b.yMin);
            float xMax = Mathf.Max(a.xMax, b.xMax);
            float yMax = Mathf.Max(a.yMax, b.yMax);
            return new Rect(xMin, yMin, xMax - xMin, yMax - yMin);
        }
    }
}
