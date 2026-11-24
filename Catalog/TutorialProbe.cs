using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

using HarmonyLib;

namespace ItemCatalog.Catalog
{
    /// <summary>
    /// 「教程物品」来源：跳过开场时 <c>PlayerStore.HandleSkipIntro</c> 直接塞进背包的那一批。
    ///
    /// 这份清单只以 <c>ldstr</c> 形式写在那个方法里，游戏没有对应的数据表或字段，
    /// 也不能拿它当数据接口调用（它会改一堆存档字段、并把物品真的塞进背包）。
    /// 所以走和 <see cref="BoxProbe"/> / 枪械槽缓存同一套办法：
    ///
    ///   1. 平时完全不介入，<c>_capturing</c> 为 false 时前缀只做一次布尔判断；
    ///   2. 玩家真的跳过开场时（<c>HandleSkipIntro</c> 执行期间）截获
    ///      <c>DirectoryMaster.Item(string, bool)</c> 的 id，写进
    ///      <c>ModData/probably_enough_items/tutorial_items.txt</c>；
    ///   3. 之后每次建来源索引先读缓存；没有缓存就用 <see cref="Fallback"/>
    ///      （从 IL 抄录的同一批 id），保证图鉴不会因为没人跳过开场就缺这一类。
    ///
    /// 私有成员一律「字面类型 + 字面成员名」，按扫描规则不需要声明任何能力。
    /// </summary>
    internal static class TutorialProbe
    {
        private const string CacheFile = "tutorial_items.txt";

        /// <summary>
        /// IL 抄录的回退表（<c>PlayerStore.HandleSkipIntro</c> 里的 <c>ldstr</c> 顺序）。
        /// 只在「还没捕获过」时使用；捕获结果一旦写下就以缓存为准。
        /// </summary>
        private static readonly string[] Fallback =
        {
            "tutorial_book",
            "joe_card",
            "wanted_paper",
            "magnifier",
            "labeler",
            "topical_bandage_item",
            "fanny_pack",
            "cigarette_guide",
            "cigarette_color",
            "injector_guide",
            "stamp_guide",
            "logo_checker",
            "intel_sec"
        };

        private static bool _installed;
        private static bool _loaded;
        private static string[] _items;

        private static bool _capturing;
        private static List<string> _captured;
        private static HashSet<string> _capturedSeen;

        /// <summary>已捕获（或从缓存读到）的清单；没有时返回 IL 回退表。</summary>
        internal static string[] Items
        {
            get
            {
                EnsureLoaded();
                return (_items != null && _items.Length > 0) ? _items : Fallback;
            }
        }

        internal static void Install(string modId)
        {
            if (_installed) return;
            _installed = true;

            try
            {
                var harmony = new Harmony(modId + ".tutorialprobe");

                var skipIntro = AccessTools.DeclaredMethod(
                    typeof(PlayerStore), "HandleSkipIntro", Type.EmptyTypes);
                var addItem = AccessTools.DeclaredMethod(
                    typeof(DirectoryMaster), "Item",
                    new[] { typeof(string), typeof(bool) });

                if (skipIntro != null)
                {
                    harmony.Patch(skipIntro,
                        prefix: new HarmonyMethod(typeof(TutorialProbe)
                            .GetMethod(nameof(BeforeSkipIntro), BindingFlags.NonPublic | BindingFlags.Static)),
                        postfix: new HarmonyMethod(typeof(TutorialProbe)
                            .GetMethod(nameof(AfterSkipIntro), BindingFlags.NonPublic | BindingFlags.Static)));
                }

                if (addItem != null)
                {
                    harmony.Patch(addItem, prefix: new HarmonyMethod(typeof(TutorialProbe)
                        .GetMethod(nameof(BeforeItem), BindingFlags.NonPublic | BindingFlags.Static)));
                }

                if (ModEntry.Log != null)
                {
                    ModEntry.Log.Log("[tutorial] patched HandleSkipIntro=" + (skipIntro != null)
                                     + " Item=" + (addItem != null));
                }
            }
            catch (Exception e) { Warn("install: " + e.Message); }
        }

        // ---------------------------------------------------------------- 捕获

        private static void BeforeSkipIntro()
        {
            _capturing = true;
            _captured = new List<string>();
            _capturedSeen = new HashSet<string>(StringComparer.Ordinal);
        }

        private static void BeforeItem(string __0)
        {
            if (!_capturing) return;
            if (_captured == null || string.IsNullOrEmpty(__0)) return;
            if (_capturedSeen != null && !_capturedSeen.Add(__0)) return;

            _captured.Add(__0);
        }

        private static void AfterSkipIntro()
        {
            _capturing = false;

            var captured = _captured;
            _captured = null;
            _capturedSeen = null;

            if (captured == null || captured.Count == 0) return;

            _items = captured.ToArray();
            _loaded = true;

            SaveCache(_items);

            // 索引已经建过（读档时）也不会错过这次新捕获：下次查询前整表重建。
            SourceIndex.Invalidate();

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[tutorial] captured " + _items.Length + " item(s)");
        }

        // ---------------------------------------------------------------- 缓存

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                if (!ModHelper.ModDataFileExists(ModEntry.MOD_ID, CacheFile)) return;

                string text = ModHelper.ReadModDataFileText(ModEntry.MOD_ID, CacheFile);
                if (string.IsNullOrEmpty(text)) return;

                string sig = null;
                var ids = new List<string>();

                var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0) continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq);
                    string value = line.Substring(eq + 1);

                    if (key == "sig") { sig = value; continue; }
                    if (key == "probe") continue;   // 旧缓存遗留：格式版本已废弃，只认环境签名

                    if (key == "id" && value.Length > 0) ids.Add(value);
                }

                if (!string.Equals(sig, CacheSignature.Value, StringComparison.Ordinal)) return;
                if (ids.Count == 0) return;

                _items = ids.ToArray();

                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[tutorial] cache loaded: " + _items.Length + " item(s)");
            }
            catch (Exception e) { Warn("cache read: " + e.Message); }
        }

        private static void SaveCache(string[] ids)
        {
            if (ids == null || ids.Length == 0) return;

            try
            {
                var sb = new StringBuilder();
                sb.Append("sig=").Append(CacheSignature.Value).Append('\n');

                for (int i = 0; i < ids.Length; i++)
                {
                    if (string.IsNullOrEmpty(ids[i])) continue;
                    sb.Append("id=").Append(ids[i]).Append('\n');
                }

                ModHelper.WriteModDataFileText(ModEntry.MOD_ID, CacheFile, sb.ToString());
            }
            catch (Exception e) { Warn("cache write: " + e.Message); }
        }

        private static void Warn(string msg)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("[tutorial] " + msg);
        }
    }
}
