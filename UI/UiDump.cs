using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 诊断用：把所有 Canvas 及其子节点的层级、名字、屏幕矩形、可见性与组件类型导出成文本，
    /// 便于确定「哪一块才是真正的物品栏」。写入 ModData 目录，同时在 F8 控制台打印摘要。
    /// </summary>
    internal static class UiDump
    {
        private const string FileName = "ui-dump.txt";
        private const int MaxDepth = 5;
        private const int MaxLines = 6000;

        private static readonly string[] Keywords =
        {
            "invent", "bar", "tray", "hotbar", "pocket", "bag", "slot",
            "panel", "counter", "hud", "item", "grid", "store", "ui"
        };

        internal static void Dump()
        {
            var text = new StringBuilder(1 << 16);
            var candidates = new List<string>();

            text.AppendLine("# ItemCatalog UI dump");
            text.AppendLine("# canvas | depth | hierarchy path | active | screen rect (x,y,w,h) | components");
            text.AppendLine();

            Canvas[] canvases;
            try
            {
                canvases = UnityEngine.Object.FindObjectsOfType<Canvas>(true);
            }
            catch (Exception e)
            {
                Warn("FindObjectsOfType<Canvas> failed: " + e.Message);
                return;
            }

            int total = 0;
            for (int i = 0; i < canvases.Length; i++)
            {
                var canvas = canvases[i];
                if (canvas == null) continue;

                var rect = canvas.transform as RectTransform;
                string header = string.Format("canvas[{0}] \"{1}\" renderMode={2} sortingOrder={3} active={4} rect={5}",
                    i, canvas.name, canvas.renderMode, canvas.sortingOrder,
                    canvas.gameObject.activeInHierarchy, RectText(rect));
                text.AppendLine(header);

                Walk(canvas.transform as RectTransform, 1, text, candidates, ref total);
                text.AppendLine();
            }

            text.AppendLine("# candidate nodes (name matched a keyword)");
            for (int i = 0; i < candidates.Count; i++) text.AppendLine(candidates[i]);

            string body = text.ToString();
            string path = "(write failed)";
            try
            {
                ModHelper.WriteModDataFileText(ModEntry.MOD_ID, FileName, body);
                path = ModHelper.GetModDataFolder(ModEntry.MOD_ID) + "\\" + FileName;
            }
            catch (Exception e)
            {
                Warn("write " + FileName + " failed: " + e.Message);
            }

            if (ModEntry.Log != null)
            {
                ModEntry.Log.Log(string.Format("[dump] canvases={0} nodes={1} candidates={2}",
                    canvases.Length, total, candidates.Count));
                ModEntry.Log.Log("[dump] file: " + path);

                int shown = Math.Min(25, candidates.Count);
                for (int i = 0; i < shown; i++) ModEntry.Log.Log("[dump] " + candidates[i]);
            }
        }

        private static void Walk(RectTransform node, int depth, StringBuilder text, List<string> candidates, ref int total)
        {
            if (node == null || depth > MaxDepth || total >= MaxLines) return;

            for (int i = 0; i < node.childCount; i++)
            {
                var child = node.GetChild(i) as RectTransform;
                if (child == null) continue;

                total++;
                string line = string.Format("{0}{1} | {2} | active={3} | {4} | {5}{6}",
                    new string(' ', depth * 2),
                    HierarchyPath(child),
                    depth,
                    child.gameObject.activeInHierarchy,
                    RectText(child),
                    Components(child),
                    TextOf(child));

                text.AppendLine(line);

                if (depth <= 3 && MatchesKeyword(child.name))
                {
                    candidates.Add(string.Format("{0} (depth={1}, active={2}) {3}{4}",
                        HierarchyPath(child), depth, child.gameObject.activeInHierarchy, RectText(child),
                        LabelOf(child)));
                }

                Walk(child, depth + 1, text, candidates, ref total);
            }
        }

        private static bool MatchesKeyword(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            string lower = name.ToLowerInvariant();
            for (int i = 0; i < Keywords.Length; i++)
            {
                if (lower.Contains(Keywords[i])) return true;
            }

            return false;
        }

        private static string HierarchyPath(Transform node)
        {
            var stack = new List<string>(8);
            var current = node;
            while (current != null && stack.Count < 8)
            {
                stack.Add(current.name);
                current = current.parent;
            }

            var builder = new StringBuilder(64);
            for (int i = stack.Count - 1; i >= 0; i--)
            {
                builder.Append(stack[i]);
                if (i > 0) builder.Append('/');
            }

            return builder.ToString();
        }

        private static string RectText(RectTransform rect)
        {
            if (rect == null) return "no-rect";

            try
            {
                var corners = new Vector3[4];
                rect.GetWorldCorners(corners);
                float xMin = corners[0].x, yMin = corners[0].y, xMax = corners[0].x, yMax = corners[0].y;
                for (int i = 1; i < 4; i++)
                {
                    if (corners[i].x < xMin) xMin = corners[i].x;
                    if (corners[i].y < yMin) yMin = corners[i].y;
                    if (corners[i].x > xMax) xMax = corners[i].x;
                    if (corners[i].y > yMax) yMax = corners[i].y;
                }

                return string.Format("({0:F0},{1:F0},{2:F0}x{3:F0})",
                    xMin, yMin, xMax - xMin, yMax - yMin);
            }
            catch
            {
                return "err";
            }
        }

        /// <summary>节点自身文本（若没有则返回空串），用于识别窗口标题。</summary>
        private static string TextOf(RectTransform rect)
        {
            try
            {
                var text = rect.GetComponent<TMPro.TMP_Text>();
                if (text == null || string.IsNullOrEmpty(text.text)) return string.Empty;

                string value = text.text.Replace("\n", " ").Trim();
                if (value.Length > 28) value = value.Substring(0, 28) + "…";
                return " | \"" + value + "\"";
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>节点下第一个非空文本，方便认出这块 UI 是什么。</summary>
        private static string LabelOf(RectTransform node)
        {
            var text = DescendantText(node, 0);
            return string.IsNullOrEmpty(text) ? string.Empty : " label=" + text;
        }

        private static string DescendantText(RectTransform node, int depth)
        {
            if (node == null || depth > 3) return null;

            for (int i = 0; i < node.childCount; i++)
            {
                var child = node.GetChild(i) as RectTransform;
                if (child == null || !child.gameObject.activeInHierarchy) continue;

                var text = TextOf(child);
                if (!string.IsNullOrEmpty(text)) return text.TrimStart(' ', '|').Trim('"');

                var deeper = DescendantText(child, depth + 1);
                if (!string.IsNullOrEmpty(deeper)) return deeper;
            }

            return null;
        }

        private static string Components(RectTransform rect)
        {
            try
            {
                var parts = new List<string>(4);
                var components = rect.GetComponents<Component>();
                for (int i = 0; i < components.Length; i++)
                {
                    var component = components[i];
                    if (component == null) continue;

                    // 不写 `component.GetType()`：那是「运行时才知道的类型」，
                    // 扫描器算成 reflection-external（2026-12-10 起拒绝加载）。
                    // 这里只是调试转储，用字面类型判断要跳过的两个即可。
                    if (component is RectTransform || component is CanvasRenderer) continue;

                    parts.Add(component.name);
                    if (parts.Count >= 5) break;
                }

                return parts.Count == 0 ? "-" : string.Join(",", parts.ToArray());
            }
            catch
            {
                return "err";
            }
        }

        private static void Warn(string message)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("[dump] " + message);
        }
    }
}
