using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;

using UnityEngine;

namespace ItemCatalog.Catalog
{
    /// <summary>
    /// 箱子内容物的加载期自动探测。
    ///
    /// 游戏没有箱子内容表：<c>PreBuiltItemHelper.crateFillers</c> 里每个箱子对应一个
    /// <c>Fill*</c> 方法，内容是「先 LuckRoll 再 TryAdd」的硬编码序列。
    /// 这里在加载阶段真跑一遍这些 filler：
    /// <list type="bullet">
    /// <item><see cref="LoadCapture"/> 把 <c>LuckRoll</c> 恒置 true，所有概率闸门通过；</item>
    /// <item>同一个钩子截获 <c>TryAdd</c> 的物品 id 并销毁，不受 7×5 容量限制；</item>
    /// <item>不吃幸运的两处单独处理：<c>packedFoodTable</c> 用掉落表展开整表，
    /// 破旧磁带照抄方法内的 14 个候选 id。</item>
    /// </list>
    ///
    /// 结果直接作为唯一数据源交给 <see cref="LootSource.ApplyBoxProbe"/>，
    /// 不再维护手工 CSV（探测会跟着游戏版本自动更新）。
    ///
    /// 失败（补丁没装上 / filler 读不到 / 抛异常）时只打日志；截获集合为空会退回
    /// 直接读沙盒里的产物（受容量限制）。
    /// </summary>
    internal static class BoxProbe
    {
        private static readonly string[] CrateIds =
        {
            "loot_crate_medical",
            "loot_crate_security",
            "loot_crate_service",
            "loot_crate_engineering",
            "loot_crate_evidence"
        };

        private static readonly string[] BoxIds =
        {
            "med_box",
            "sec_box",
            "service_box",
            "eng_box",
            "evidence_box"
        };

        private const int NormalRuns = 2;

        /// <summary>
        /// `StartingPerkContent.CreateUnownedDegradedCassette()` 的候选是方法内的局部数组，
        /// 没有公开入口可读；这里照抄那 14 个 id（2026-11-22 反汇编）。
        /// 已拥有的磁带会被游戏从候选里剔除（取决于存档），图鉴按「可能出现的全部」列出。
        /// 不补这份清单就只能靠采样，14 抽 14 需要 coupon collector 级别次数的重复。
        /// </summary>
        private static readonly string[] DegradedCassettes =
        {
            "cassette1d", "cassette2d", "cassette3d", "cassette4d", "cassette5d",
            "cassette6d", "cassette7d", "cassette8d", "cassette9d", "cassette10d",
            "cassette11d", "cassette12d", "cassette13d", "cassette14d"
        };

        /// <summary>探测结果的本地缓存（与 dll 同目录的 ModData 文件夹）。</summary>
        private const string CacheFile = "box_probe.txt";

        /// <summary>缓存格式版本：探测规则变化时 +1，旧缓存自动失效。</summary>
        private const string CacheVersion = "5";

        private static string _signature;

        private static bool _done;
        private static bool _running;

        /// <summary>加载阶段调用：幂等；系统没就绪时直接返回且不置 <c>_done</c>，等下一次信号。</summary>
        internal static void ScanIfNeeded()
        {
            if (_done || _running) return;

            if (!Ready()) return;

            try
            {
                // 先读缓存：游戏版本 / 模组版本没变就直接用，跳过 5 秒左右的探测
                Dictionary<string, List<string>> cached;
                Dictionary<string, string> cachedKeys;
                if (TryLoadCache(out cached, out cachedKeys))
                {
                    LootSource.ApplyBoxProbe(cached);
                    LootSource.ApplyBoxKeycards(cachedKeys);
                    _done = true;
                    return;
                }

                Scan();
            }
            catch (Exception e)
            {
                _running = false;
                Warn("scan: " + e.Message);
            }
        }

        /// <summary>物品目录与掉落表都就绪才能跑：filler 需要真实目录物品，表展开需要 LootRegistry。</summary>
        private static bool Ready()
        {
            if (CatalogDataBuilder.All.Count == 0) return false;

            try
            {
                foreach (var id in LootRegistry.TableIds)
                {
                    if (!string.IsNullOrEmpty(id)) return true;
                }
            }
            catch { }

            return false;
        }

        private static void Scan()
        {
            _running = true;

            var watch = Stopwatch.StartNew();

            var fillers = ReadFillers();
            if (fillers == null)
            {
                _running = false;
                return;
            }

            var store = PlayerStore.Instance;
            int savedUniqueId = store == null ? 0 : store.currentUniqueId;
            var savedRng = RNG.random;
            var savedUnityState = UnityEngine.Random.state;

            // 模块生成器会从 ModuleEffectHelper.moduleEffects 里抽走词条，前后快照/还原
            var pool = ProbablyEnoughItems.Api.CatalogVariantKit.SnapshotModulePool();

            var results = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            try
            {
                for (int c = 0; c < CrateIds.Length; c++)
                {
                    string crateId = CrateIds[c];
                    string boxId = BoxIds[c];

                    Action<GameItem, float> filler;
                    if (!fillers.TryGetValue(crateId, out filler) || filler == null) continue;

                    var set = new HashSet<string>(StringComparer.Ordinal);
                    int unknown = 0;

                    ProbeOne(crateId, boxId, filler, set, ref unknown);

                    int tableAdded = 0;
                    if (string.Equals(crateId, "loot_crate_service", StringComparison.Ordinal))
                        tableAdded = ExpandTable("packedFoodTable", set, ref unknown);

                    // 两个箱子的 filler 都会调 CreateUnownedDegradedCassette()：
                    // 候选清单在方法内部，采样拿不全，这里直接补齐全部 14 个。
                    if (string.Equals(crateId, "loot_crate_evidence", StringComparison.Ordinal) ||
                        string.Equals(crateId, "loot_crate_service", StringComparison.Ordinal))
                    {
                        for (int i = 0; i < DegradedCassettes.Length; i++)
                            Add(DegradedCassettes[i], set, ref unknown);
                    }

                    var list = new List<string>(set);
                    results[boxId] = list;

                    if (ModEntry.Log != null)
                    {
                        ModEntry.Log.Log("[boxprobe] " + boxId + ": captured=" + list.Count
                                         + " table=+" + tableAdded + " unknown=" + unknown);
                    }
                }

                LootSource.ApplyBoxProbe(results);

                // 箱子 → 卡：扫目录里所有 LOCKED_CONTAINER_TAG 物品，按 LOCK_ID 查游戏的卡字典
                var keycards = CollectKeycards();
                LootSource.ApplyBoxKeycards(keycards);

                _done = true;

                SaveCache(results, keycards);
            }
            finally
            {
                ProbablyEnoughItems.Api.CatalogVariantKit.RestoreModulePool(pool);
                UnityEngine.Random.state = savedUnityState;
                RNG.random = savedRng;
                if (store != null) store.currentUniqueId = savedUniqueId;

                _running = false;

                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[boxprobe] done in " + watch.ElapsedMilliseconds + "ms");
            }
        }

        private static void ProbeOne(string crateId, string boxId, Action<GameItem, float> filler,
                                     HashSet<string> set, ref int unknown)
        {
            GameItem crate = null;

            try
            {
                crate = DirectoryMaster.Item(boxId, true);
                if (crate == null)
                {
                    Warn(boxId + ": crate item not found");
                    return;
                }

                int maxRuns = NormalRuns;
                int plateau = 1;

                int runs = 0;
                int emptyRuns = 0;

                while (runs < maxRuns)
                {
                    if (!LoadCapture.Begin()) break;

                    int before = set.Count;

                    try { filler(crate, 1f); }
                    catch (Exception e) { Warn(crateId + " run " + runs + ": " + e.Message); }

                    var ids = LoadCapture.End();
                    runs++;

                    for (int i = 0; i < ids.Count; i++) Add(ids[i], set, ref unknown);

                    if (set.Count == before)
                    {
                        if (++emptyRuns >= plateau) break;
                    }
                    else emptyRuns = 0;
                }

                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[boxprobe] " + boxId + " runs=" + runs);

                // 钩子没装上时截获集合是空的：退回直接读沙盒里的内容。
                // 受 7×5 容量限制，不是完整清单，但至少不会整箱为空。
                if (set.Count == 0)
                {
                    var children = GraphUtils.FindAllChildrenType<GameItem>(crate, null, null);
                    if (children != null)
                    {
                        for (int i = 0; i < children.Count; i++)
                        {
                            var product = children[i];
                            if (product == null) continue;
                            Add(product.identifier, set, ref unknown);
                            try { product.Destroy(); } catch { }
                        }
                    }
                }
            }
            catch (Exception e) { Warn(crateId + ": " + e.Message); }
            finally
            {
                if (crate != null)
                {
                    try { crate.Destroy(); } catch { }
                }
            }
        }

        /// <summary>把一张掉落表的全部条目并进结果集；返回新增数量。</summary>
        private static int ExpandTable(string tableId, HashSet<string> set, ref int unknown)
        {
            int added = 0;

            try
            {
                var entries = LootRegistry.GetTableEntries(tableId);
                if (entries == null) return 0;

                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    if (entry == null || string.IsNullOrEmpty(entry.id)) continue;

                    int before = set.Count;
                    Add(entry.id, set, ref unknown);
                    if (set.Count > before) added++;
                }
            }
            catch (Exception e) { Warn("table '" + tableId + "': " + e.Message); }

            return added;
        }

        /// <summary>归一化：展开 random_* 占位，只保留能在目录里解析的真实物品 id。</summary>
        private static void Add(string raw, HashSet<string> set, ref int unknown)
        {
            if (string.IsNullOrEmpty(raw)) return;

            string id = LootSource.ExpandPlaceholder(raw.Trim());
            if (string.IsNullOrEmpty(id)) return;

            string baseId;
            try { baseId = LootSource.BaseItemId(id); }
            catch { baseId = null; }

            if (string.IsNullOrEmpty(baseId))
            {
                unknown++;
                return;
            }

            set.Add(id);
        }

        /// <summary>读私有静态 <c>crateFillers</c>（字面类型 + 字面字段名，扫描器免费）。</summary>
        private static Dictionary<string, Action<GameItem, float>> ReadFillers()
        {
            try
            {
                var field = typeof(PreBuiltItemHelper).GetField("crateFillers",
                    BindingFlags.NonPublic | BindingFlags.Static);

                var dict = field == null ? null : field.GetValue(null) as IDictionary;
                if (dict == null)
                {
                    Warn("crateFillers not found");
                    return null;
                }

                var map = new Dictionary<string, Action<GameItem, float>>(StringComparer.Ordinal);

                foreach (DictionaryEntry pair in dict)
                {
                    string key = pair.Key as string;
                    var filler = pair.Value as Action<GameItem, float>;
                    if (!string.IsNullOrEmpty(key) && filler != null) map[key] = filler;
                }

                return map;
            }
            catch (Exception e)
            {
                Warn("crateFillers: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// 箱子 → 卡：扫目录里所有带 <c>LOCKED_CONTAINER_TAG</c> 的物品，
        /// 读它的 `LOCK_ID`，再用游戏的「锁 id → 卡」字典（`StoreClientList.lockToKeycard`，
        /// <see cref="LockKeys"/> 负责反射读取与兜底）换成卡 id。
        /// 因此新增锁容器 / 卡不需要改任何表。
        /// </summary>
        private static Dictionary<string, string> CollectKeycards()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            var items = CatalogDataBuilder.Visible;
            if (items == null) return map;

            for (int i = 0; i < items.Count; i++)
            {
                var entry = items[i];
                var item = entry == null ? null : entry.Item;
                if (item == null || string.IsNullOrEmpty(entry.Id)) continue;

                bool locked;
                try { locked = item.IsTag("LOCKED_CONTAINER_TAG"); }
                catch { locked = false; }
                if (!locked) continue;

                string key = null;
                try { key = LockKeys.KeyForBox(item); }
                catch { }

                if (!string.IsNullOrEmpty(key)) map[entry.Id] = key;
            }

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[boxprobe] keycards=" + map.Count);

            return map;
        }

        /// <summary>
        /// 读缓存：游戏与所有启用模组的哈希都一致才认。五个箱子少任何一个都重探
        /// （缓存是纯文本 `<c>key=value</c>`，产物用 <c>|</c> 分隔，和拆枪缓存同一套路）。
        /// </summary>
        private static bool TryLoadCache(out Dictionary<string, List<string>> results,
                                        out Dictionary<string, string> keycards)
        {
            results = null;
            keycards = new Dictionary<string, string>(StringComparer.Ordinal);

            try
            {
                if (!ModHelper.ModDataFileExists(ModEntry.MOD_ID, CacheFile)) return false;

                string text = ModHelper.ReadModDataFileText(ModEntry.MOD_ID, CacheFile);
                if (string.IsNullOrEmpty(text)) return false;

                string sig = null, probe = null;
                var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);

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
                    if (key == "probe") { probe = value; continue; }

                    if (key.StartsWith("boxkey:", StringComparison.Ordinal))
                    {
                        string box = key.Substring(7);
                        if (box.Length > 0 && value.Length > 0) keycards[box] = value;
                        continue;
                    }

                    map[key] = Split(value);
                }

                if (!string.Equals(probe, CacheVersion, StringComparison.Ordinal)) return false;
                if (!string.Equals(sig, Signature(), StringComparison.Ordinal)) return false;

                for (int i = 0; i < BoxIds.Length; i++)
                    if (!map.ContainsKey(BoxIds[i])) return false;

                results = map;

                if (ModEntry.Log != null)
                {
                    ModEntry.Log.Log("[boxprobe] cache loaded: " + map.Count + " boxes, "
                                     + keycards.Count + " keycards");
                }

                return true;
            }
            catch (Exception e)
            {
                Warn("cache read: " + e.Message);
                return false;
            }
        }

        private static void SaveCache(Dictionary<string, List<string>> results,
                                      Dictionary<string, string> keycards)
        {
            if (results == null || results.Count == 0) return;

            try
            {
                var sb = new StringBuilder();
                sb.Append("sig=").Append(Signature()).Append('\n');
                sb.Append("probe=").Append(CacheVersion).Append('\n');

                if (keycards != null)
                {
                    foreach (var pair in keycards)
                    {
                        if (string.IsNullOrEmpty(pair.Key) || string.IsNullOrEmpty(pair.Value)) continue;
                        sb.Append("boxkey:").Append(pair.Key).Append('=').Append(pair.Value).Append('\n');
                    }
                }

                foreach (var pair in results)
                {
                    sb.Append(pair.Key).Append('=');

                    var ids = pair.Value;
                    if (ids != null)
                    {
                        for (int i = 0; i < ids.Count; i++)
                        {
                            if (i > 0) sb.Append('|');
                            sb.Append(ids[i]);
                        }
                    }

                    sb.Append('\n');
                }

                ModHelper.WriteModDataFileText(ModEntry.MOD_ID, CacheFile, sb.ToString());

                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[boxprobe] cache saved: " + results.Count + " boxes");
            }
            catch (Exception e) { Warn("cache write: " + e.Message); }
        }

        private static List<string> Split(string value)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(value)) return list;

            var parts = value.Split('|');
            for (int i = 0; i < parts.Length; i++)
            {
                string id = parts[i].Trim();
                if (id.Length > 0) list.Add(id);
            }

            return list;
        }

        /// <summary>
        /// 游戏版本 + 所有启用模组指纹：缓存文件在同一套环境下才有效。
        /// 内部共享给其它探测缓存（枪械槽），保证失效规则一致。
        /// </summary>
        internal static string Signature()
        {
            if (_signature != null) return _signature;

            var sb = new StringBuilder();
            sb.Append("game=").Append(Application.version).Append(':').Append(GameHash());

            var mods = new List<string>();

            try
            {
                var list = ModLoader.Mods;
                if (list != null)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        var mod = list[i];
                        if (mod == null || !mod.IsEnabled) continue;

                        string id = mod.Manifest != null && !string.IsNullOrEmpty(mod.Manifest.ID)
                            ? mod.Manifest.ID
                            : "?";

                        string fp = null;
                        try { if (mod.Fingerprint != null) fp = mod.Fingerprint.Combined; }
                        catch { }

                        if (string.IsNullOrEmpty(fp))
                        {
                            try
                            {
                                if (!string.IsNullOrEmpty(mod.DllPath))
                                    fp = ModFingerprint.HashFile(mod.DllPath);
                            }
                            catch { }
                        }

                        mods.Add(id + ":" + (fp ?? "?"));
                    }
                }
            }
            catch (Exception e) { Warn("signature mods: " + e.Message); }

            mods.Sort(StringComparer.Ordinal);
            sb.Append("|mods=").Append(string.Join(";", mods.ToArray()));

            _signature = sb.ToString();
            return _signature;
        }

        /// <summary>
        /// 游戏本体的哈希：优先哈希 Assembly-CSharp.dll；拿不到 Location
        /// （或被裁剪）时退回模块 MVID——重建过的程序集一定不同。
        /// </summary>
        private static string GameHash()
        {
            try
            {
                var assembly = typeof(GameItem).Assembly;

                string location = assembly.Location;
                if (!string.IsNullOrEmpty(location))
                {
                    string hash = ModFingerprint.HashFile(location);
                    if (!string.IsNullOrEmpty(hash)) return hash;
                }

                return assembly.ManifestModule.ModuleVersionId.ToString();
            }
            catch (Exception e)
            {
                Warn("signature game: " + e.Message);
                return "?";
            }
        }

        private static void Warn(string message)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("[boxprobe] " + message);
        }
    }
}
