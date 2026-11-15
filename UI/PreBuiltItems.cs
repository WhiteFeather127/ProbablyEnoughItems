using System;
using System.Collections.Generic;

namespace ItemCatalog.UI
{
    /// <summary>
    /// PreBuiltItemHelper.preBuiltItems 里的「预构建键」：它们不是目录里的物品 id
    /// （图鉴里查不到条目），而是游戏生成函数的别名——二手净水器、各种箱子、
    /// 纸条、注水滤芯…生成时会随机耐久 / 内容物。
    ///
    /// 因此两件事必须走游戏自己的函数：
    /// · 拿取（拖出）用 <see cref="ItemSpawner.Spawn"/>，耐久才是游戏内的那套随机；
    /// · 提示框里的耐久范围靠采样得出（工厂里写死的区间静态读不到）。
    /// </summary>
    internal static class PreBuiltItems
    {
        private static Dictionary<string, bool> _ids;
        private static readonly Dictionary<string, string> RangeCache =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>采样次数：够小（快），也够大（能覆盖到区间两端）。</summary>
        private const int Samples = 16;

        internal static bool IsPreBuilt(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;

            EnsureIds();

            bool yes;
            return _ids.TryGetValue(id, out yes) && yes;
        }

        private static void EnsureIds()
        {
            if (_ids != null) return;

            _ids = new Dictionary<string, bool>(StringComparer.Ordinal);

            try
            {
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
            catch (Exception e)
            {
                Warn("prebuilt ids: " + e.Message);
            }
        }

        /// <summary>按游戏函数生成。ItemSpawner.Spawn 先查预构建表，再回退目录工厂。</summary>
        internal static GameItem Spawn(string id)
        {
            return ItemSpawner.Spawn(id);
        }

        /// <summary>
        /// 耐久范围文本（“15 ~ 50”）。工厂内部会随机一档（二手净水器是 15~50），
        /// 静态读不到区间，所以采样若干次取最小 / 最大，结果按 id 缓存。
        /// 不是耐久物品（采样值恒为 0）返回 null，提示框就不写这一行。
        /// </summary>
        internal static string DurabilityRange(string id)
        {
            if (!IsPreBuilt(id)) return null;

            string cached;
            if (RangeCache.TryGetValue(id, out cached)) return cached;

            int min = int.MaxValue;
            int max = int.MinValue;

            for (int i = 0; i < Samples; i++)
            {
                GameItem item = null;
                try { item = ItemSpawner.Spawn(id); }
                catch { break; }

                if (item == null) break;

                int value;
                try { value = DurabilityHelper.GetRealDurability(item); }
                catch { value = 0; }

                if (value > 0)
                {
                    if (value < min) min = value;
                    if (value > max) max = value;
                }
            }

            string result = null;
            if (max > 0) result = min == max ? min.ToString() : min + " ~ " + max;

            RangeCache[id] = result;
            return result;
        }

        private static void Warn(string message)
        {
            if (ModEntry.Log == null) return;
            ModEntry.Log.Warning("[prebuilt] " + message);
        }
    }
}
