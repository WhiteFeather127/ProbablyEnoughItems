using System;
using System.Collections.Generic;
using System.Reflection;

using HarmonyLib;

namespace ItemCatalog.Catalog
{
    /// <summary>
    /// 加载期「捕获式」探测的统一钩子。
    ///
    /// 只在自己的探测窗口（<see cref="Begin"/> ~ <see cref="End"/>）内生效，
    /// 桌面线程同步执行，所以游戏自身在加载期生成箱子 / 拆枪完全不受影响。
    ///
    /// 三个前缀：
    /// <list type="bullet">
    /// <item><c>PreBuiltItemHelper.LuckRoll(int, float)</c> → 恒 true，
    /// 箱子内容的所有概率闸门都通过（已确认全部调用第二参都是 luckMult）。</item>
    /// <item><c>PreBuiltItemHelper.TryAdd(GameItem, GameItem)</c> → 记录产物 id 并销毁，
    /// 不真正放进箱子，因此不受 7×5 容量限制。</item>
    /// <item><c>GraphUtils.EmporiumTryAdd(GraphNodeStorage, GameItem)</c> → 拆枪产物走这条出口，
    /// 记录 id 并销毁，不再依赖 20×20 沙盒回读。</item>
    /// </list>
    ///
    /// 私有成员一律「字面类型 + 字面成员名」，不拼字符串、不用 <c>obj.GetType()</c>，
    /// 按扫描规则不需要声明任何能力。
    /// </summary>
    internal static class LoadCapture
    {
        private static bool _installed;

        private static bool _active;
        private static HashSet<string> _ids;

        private static int _captured;
        private static int _destroyed;

        internal static bool Active { get { return _active; } }

        internal static void Install(string modId)
        {
            if (_installed) return;
            _installed = true;

            try
            {
                var harmony = new Harmony(modId + ".loadcapture");

                var luckRoll = AccessTools.DeclaredMethod(
                    typeof(PreBuiltItemHelper), "LuckRoll",
                    new[] { typeof(int), typeof(float) });
                var tryAdd = AccessTools.DeclaredMethod(
                    typeof(PreBuiltItemHelper), "TryAdd",
                    new[] { typeof(GameItem), typeof(GameItem) });
                var emporiumTryAdd = AccessTools.DeclaredMethod(
                    typeof(GraphUtils), "EmporiumTryAdd",
                    new[] { typeof(GraphNodeStorage), typeof(GameItem) });

                // 箱子内容里的存档门控（FillEvidence 的 WineMaker 判断）：
                // 只改谓词返回值，不真解锁——Unlock() 会写 state=2 并触发回调，
                // 存档就真被改了，事后也回不去。
                var upgradeUnlocked = AccessTools.DeclaredMethod(
                    typeof(NetworkUpgrade), "IsUnlocked", Type.EmptyTypes);

                if (luckRoll != null)
                {
                    harmony.Patch(luckRoll, prefix: new HarmonyMethod(typeof(LoadCapture)
                        .GetMethod(nameof(BeforeLuckRoll), BindingFlags.NonPublic | BindingFlags.Static)));
                }

                if (tryAdd != null)
                {
                    harmony.Patch(tryAdd, prefix: new HarmonyMethod(typeof(LoadCapture)
                        .GetMethod(nameof(BeforeTryAdd), BindingFlags.NonPublic | BindingFlags.Static)));
                }

                if (emporiumTryAdd != null)
                {
                    harmony.Patch(emporiumTryAdd, prefix: new HarmonyMethod(typeof(LoadCapture)
                        .GetMethod(nameof(BeforeEmporiumTryAdd), BindingFlags.NonPublic | BindingFlags.Static)));
                }

                if (upgradeUnlocked != null)
                {
                    harmony.Patch(upgradeUnlocked, prefix: new HarmonyMethod(typeof(LoadCapture)
                        .GetMethod(nameof(BeforeUpgradeUnlocked), BindingFlags.NonPublic | BindingFlags.Static)));
                }

                if (ModEntry.Log != null)
                {
                    ModEntry.Log.Log("[capture] patched LuckRoll=" + (luckRoll != null)
                                     + " TryAdd=" + (tryAdd != null)
                                     + " EmporiumTryAdd=" + (emporiumTryAdd != null)
                                     + " NetworkUpgrade.IsUnlocked=" + (upgradeUnlocked != null));
                }
            }
            catch (Exception e) { Warn("install: " + e.Message); }
        }

        /// <summary>开一个捕获窗口。已经在捕获中时返回 false（不允许嵌套）。</summary>
        internal static bool Begin()
        {
            if (_active) return false;

            _ids = new HashSet<string>(StringComparer.Ordinal);
            _captured = 0;
            _destroyed = 0;
            _active = true;
            return true;
        }

        /// <summary>关窗口并返回捕获到的 id（按首次出现顺序去重）。</summary>
        internal static List<string> End()
        {
            _active = false;

            var list = _ids == null ? new List<string>() : new List<string>(_ids);
            _ids = null;

            if (_captured != _destroyed)
                Warn("captured " + _captured + " but destroyed " + _destroyed);

            return list;
        }

        private static bool BeforeLuckRoll(ref bool __result)
        {
            if (!_active) return true;

            __result = true;
            return false;
        }

        private static bool BeforeTryAdd(GameItem __1)
        {
            return Capture(__1, "tryadd");
        }

        private static bool BeforeEmporiumTryAdd(GameItem __1)
        {
            return Capture(__1, "emporium");
        }

        /// <summary>探测窗口内把所有网络升级视为已解锁（只影响返回值，不动存档）。</summary>
        private static bool BeforeUpgradeUnlocked(ref bool __result)
        {
            if (!_active) return true;

            __result = true;
            return false;
        }

        /// <summary>记录 id 并销毁实例；返回 false = 跳过原方法。</summary>
        private static bool Capture(GameItem item, string source)
        {
            if (!_active) return true;

            try
            {
                if (item == null) return false;

                _captured++;

                string id = item.identifier;
                if (!string.IsNullOrEmpty(id) && _ids != null) _ids.Add(id);

                try
                {
                    item.Destroy();
                    _destroyed++;
                }
                catch (Exception e) { Warn(source + " destroy: " + e.Message); }
            }
            catch (Exception e) { Warn(source + ": " + e.Message); }

            return false;
        }

        private static void Warn(string message)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("[capture] " + message);
        }
    }
}
