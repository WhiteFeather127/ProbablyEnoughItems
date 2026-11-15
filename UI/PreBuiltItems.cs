using System;
using System.Collections.Generic;

namespace ItemCatalog.UI
{
    /// <summary>
    /// PreBuiltItemHelper.preBuiltItems 里的「预构建键」：它们不是目录里的物品 id
    /// （图鉴里查不到条目），而是游戏生成函数的别名——二手净水器、各种箱子、
    /// 纸条、注水滤芯…生成时会随机耐久 / 内容物。
    ///
    /// · 拿取（拖出）必须走 <see cref="ItemSpawner.Spawn"/>，耐久才是游戏内的那套随机；
    /// · 提示框里的耐久范围用静态表（从工厂 IL 抄的常量），绝不在此采样——
    ///   采样会白白消耗游戏全局 RNG 的状态。
    /// </summary>
    internal static class PreBuiltItems
    {
        private static Dictionary<string, bool> _ids;

        /// <summary>
        /// 带随机耐久的预构建键（全集，抄自 PreBuiltItemHelper 各工厂的 IL）。
        /// 全程序集扫过一遍：除了二手净水器，其余预构建键的随机
        /// 都不是耐久——random_*_module 的 0~100 挑的是外观形状，
        /// RandomCigarette 的 0~100 是品质，loot_crate_* 的 RNG 是内容物数量。
        /// </summary>
        private static readonly Dictionary<string, string> StaticRanges =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "portable_water_purifier_used", "15 ~ 50" }
            };

        internal static bool IsPreBuilt(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;

            EnsureIds();

            return _ids.ContainsKey(id);
        }

        private static void EnsureIds()
        {
            if (_ids != null) return;

            _ids = new Dictionary<string, bool>(StringComparer.Ordinal);

            var field = typeof(PreBuiltItemHelper).GetField("preBuiltItems",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.Public);

            var dict = field != null ? field.GetValue(null) as System.Collections.IDictionary : null;
            if (dict == null) return;

            foreach (var key in dict.Keys)
            {
                var text = key as string;
                if (!string.IsNullOrEmpty(text)) _ids[text] = true;
            }
        }

        /// <summary>
        /// 耐久范围文本（“15 ~ 50”），查静态表。不在表里（或没有耐久）返回 null，
        /// 提示框就不写这一行。注意：这里不做任何生成采样，避免消耗游戏 RNG。
        /// </summary>
        internal static string DurabilityRange(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            string range;
            return StaticRanges.TryGetValue(id, out range) ? range : null;
        }
    }
}
