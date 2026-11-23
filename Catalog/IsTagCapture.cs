using System;
using System.Collections.Generic;
using System.Reflection;

using HarmonyLib;

namespace ItemCatalog.Catalog
{
    /// <summary>
    /// 「某件探测物品被问了哪些 tag」的捕获钩子。
    ///
    /// 用途：枪械槽的 <c>mayInventoryAddItemFunc</c> 把要求写死在闭包里
    /// （<c>mod.IsTag("MOD_STOCK_TAG") &amp;&amp; …</c>），字段里读不到。
    /// 拿一件空白探测物品去调一次这个委托，顺手把 <c>GameItem.IsTag</c> 的入参记下来，
    /// 就能得到「这个槽要求什么 tag」，不必为每个配件都跑一遍委托。
    ///
    /// 只在自己的捕获窗口（<see cref="Capture"/>）内生效：平时 <c>_probeItem</c> 为 null，
    /// 前缀只做一次引用比较，对游戏没有可见影响。
    /// 按 ModHelper / Harmony 的既有用法接入（与 <see cref="LoadCapture"/> 同一套）。
    /// </summary>
    internal static class IsTagCapture
    {
        private static bool _installed;

        private static GameItem _probeItem;
        private static List<string> _tags;
        private static HashSet<string> _seen;

        internal static bool Installed { get { return _installed; } }

        internal static void Install(string modId)
        {
            if (_installed) return;
            _installed = true;

            try
            {
                var harmony = new Harmony(modId + ".istagcapture");

                var isTag = AccessTools.DeclaredMethod(
                    typeof(GameItem), "IsTag", new[] { typeof(string) });

                if (isTag != null)
                {
                    harmony.Patch(isTag, prefix: new HarmonyMethod(typeof(IsTagCapture)
                        .GetMethod(nameof(BeforeIsTag), BindingFlags.NonPublic | BindingFlags.Static)));
                }

                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[capture] patched GameItem.IsTag=" + (isTag != null));
            }
            catch (Exception e) { Warn("install: " + e.Message); }
        }

        /// <summary>
        /// 在捕获窗口里跑一次 <paramref name="action"/>，返回期间对
        /// <paramref name="probe"/> 问过的 tag（按首次出现顺序去重）。
        /// 窗口不可嵌套——嵌套时内层不会覆盖外层的上下文。
        /// </summary>
        internal static List<string> Capture(GameItem probe, Action action)
        {
            var result = new List<string>();
            if (probe == null || action == null) return result;

            var prevItem = _probeItem;
            var prevTags = _tags;
            var prevSeen = _seen;

            _probeItem = probe;
            _tags = result;
            _seen = new HashSet<string>(StringComparer.Ordinal);

            try { action(); }
            catch (Exception e) { Warn("capture: " + e.Message); }
            finally
            {
                _probeItem = prevItem;
                _tags = prevTags;
                _seen = prevSeen;
            }

            return result;
        }

        private static void BeforeIsTag(GameItem __instance, string tagName)
        {
            if (_probeItem == null || tagName == null) return;
            if (!ReferenceEquals(__instance, _probeItem)) return;
            if (_seen != null && !_seen.Add(tagName)) return;

            if (_tags != null) _tags.Add(tagName);
        }

        private static void Warn(string msg)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("[istag-capture] " + msg);
        }
    }
}
