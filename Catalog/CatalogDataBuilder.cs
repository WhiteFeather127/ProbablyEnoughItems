using System;
using System.Collections.Generic;
using System.Diagnostics;
using ProbablyEnoughItems.Api;

namespace ItemCatalog.Catalog
{
    /// <summary>
    /// 一次性把游戏里所有已注册物品（含模组物品）抓成轻量快照，供界面使用。
    /// 只读取游戏公开成员，不做反射，不需要声明任何能力。
    /// </summary>
    internal static class CatalogDataBuilder
    {
        internal static readonly List<ItemEntry> All = new List<ItemEntry>();
        internal static readonly List<ItemEntry> Visible = new List<ItemEntry>();

        internal static int FailedCount;
        internal static int DirectoryCount;
        internal static int ExtraCount;      // 通过 ItemCatalogApi 注册进来的额外条目数
        internal static long BuildMilliseconds;

        /// <summary>上次构建时的「额外条目提供者」数量，用于检测之后新注册的提供者。</summary>
        private static int _knownProviderCount = -1;

        /// <summary>上次构建时的「变体提供者」数量，同上。</summary>
        private static int _knownVariantProviderCount = -1;

        /// <summary>构建代号：每次 Build 自增，供 UI 判断是否需要重新展开变体。</summary>
        internal static int Generation;

        private static bool _built;

        /// <summary>上一次构建时抓到的注册表 id，用于检测新增物品。</summary>
        private static readonly HashSet<string> _knownIds = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>上一次构建时收录的「额外条目」id（来自 ItemCatalogApi 提供者）。</summary>
        private static readonly HashSet<string> _knownExtraIds = new HashSet<string>(StringComparer.Ordinal);

        internal static void EnsureBuilt()
        {
            if (_built) return;
            Build(false);
        }

        /// <summary>
        /// 目录里是否出现了上次构建时没有的 id（例如后加载的模组注册了新物品）。
        /// 只做标识枚举，不创建物品，开销约等于一次目录遍历。
        /// </summary>
        internal static bool HasNewItems()
        {
            if (!_built) return true;

            // 之后才注册的「额外条目提供者」也要触发重建
            if (ItemCatalogApi.ItemProviderCount != _knownProviderCount) return true;

            // 变体提供者决定每件物品展开几格，后注册同样要重算
            if (ItemCatalogApi.VariantProviderCount != _knownVariantProviderCount) return true;

            try
            {
                // 提供者给出的条目集合发生变化（增/删/换 id）同样需要重建
                var extras = ItemCatalogApi.CollectExtraItems();
                if (extras != null)
                {
                    int seen = 0;
                    for (int i = 0; i < extras.Count; i++)
                    {
                        string id = extras[i].Key;
                        if (string.IsNullOrEmpty(id)) continue;
                        if (!_knownExtraIds.Contains(id)) return true;
                        seen++;
                    }
                    if (seen != _knownExtraIds.Count) return true;
                }

                var ids = CollectAllIds();
                if (ids.Count != _knownIds.Count) return true;

                foreach (var pair in ids)
                {
                    if (!_knownIds.Contains(pair.Key)) return true;
                }

                return false;
            }
            catch (Exception e)
            {
                LogError("detect new items", e);
                return false;
            }
        }

        /// <summary>
        /// 增量补入「加载之后才注册进来」的条目。
        ///
        /// 背景：`OnGameLoadedLate` 里建表时，某些模组的物品还没注册完
        /// （IMod.OnEnable 晚于该钩子）。等到玩家点开图鉴，`HasNewItems()` 发现 id 变多，
        /// 原来会整表重建——那是把 518 件物品重新造一遍（1.5~1.9 秒），
        /// 全部压在一次点击里，表现就是首次打开卡死。
        ///
        /// 这里只造新增的那几条，然后补排一次序、推进 Generation。
        /// </summary>
        /// <returns>true = 已经处理完（含"其实没变化"）；false = 有删除或提供者变化，必须整表重建。</returns>
        internal static bool TryAppendNewItems()
        {
            string reason;
            return TryAppendNewItems(out reason);
        }

        /// <summary>最近一次增量补入实际新增的条目数（0 表示没有新增）。</summary>
        internal static int LastAppendCount { get; private set; }

        /// <summary>同上，另外返回判定原因，便于日志定位。</summary>
        internal static bool TryAppendNewItems(out string reason)
        {
            reason = null;
            LastAppendCount = 0;

            if (!_built)
            {
                reason = "catalog not built yet";
                return false;
            }

            // 后注册的提供者不必整表重建：
            // 额外条目走下面的 newExtras 增量；变体提供者只影响「一格展开成几格」，
            // 把 Generation 推进一次让 FlatEntries 重算即可。
            bool variantProvidersChanged = ItemCatalogApi.VariantProviderCount != _knownVariantProviderCount;

            try
            {
                var ids = CollectAllIds();

                var extras = ItemCatalogApi.CollectExtraItems();
                var newExtras = new List<KeyValuePair<string, GameItem>>();
                var seenExtraIds = new HashSet<string>(StringComparer.Ordinal);

                if (extras != null)
                {
                    for (int i = 0; i < extras.Count; i++)
                    {
                        string id = extras[i].Key;
                        if (string.IsNullOrEmpty(id)) continue;
                        if (ids.ContainsKey(id)) continue;    // 注册表里有的不算额外条目（与 Build 一致）

                        seenExtraIds.Add(id);
                        if (!_knownExtraIds.Contains(id)) newExtras.Add(extras[i]);
                    }
                }

                // 有额外条目消失 → 只能整表重建
                foreach (var known in _knownExtraIds)
                {
                    if (seenExtraIds.Contains(known)) continue;

                    reason = "extra item disappeared: " + known;
                    return false;
                }

                // 有 id 消失（模组卸载、目录变化）→ 整表重建
                if (ids.Count < _knownIds.Count)
                {
                    reason = "ids removed " + _knownIds.Count + " -> " + ids.Count;
                    return false;
                }

                foreach (var known in _knownIds)
                {
                    if (!ids.ContainsKey(known))
                    {
                        reason = "id disappeared: " + known;
                        return false;
                    }
                }

                int added = 0;
                foreach (var pair in ids)
                {
                    if (_knownIds.Contains(pair.Key)) continue;

                    GameItem item;
                    try { item = DirectoryMaster.Item(pair.Key); }
                    catch (Exception e)
                    {
                        FailedCount++;
                        if (FailedCount <= 3) LogError("create item '" + pair.Key + "'", e);
                        continue;
                    }

                    if (item == null) { FailedCount++; continue; }

                    AddEntry(pair.Key, item, pair.Value);
                    _knownIds.Add(pair.Key);
                    added++;
                }

                // 运行中新注册的额外条目：同样只补这几件
                for (int i = 0; i < newExtras.Count; i++)
                {
                    string id = newExtras[i].Key;
                    if (!_knownExtraIds.Add(id)) continue;

                    AddEntry(id, newExtras[i].Value, false, true);
                    ExtraCount++;
                    added++;
                }

                _knownProviderCount = ItemCatalogApi.ItemProviderCount;
                _knownVariantProviderCount = ItemCatalogApi.VariantProviderCount;

                if (added == 0 && !variantProvidersChanged)
                {
                    reason = "no change";
                    return true;   // 没有新增，什么都不用做
                }

                if (added > 0)
                {
                    Visible.Sort(delegate (ItemEntry a, ItemEntry b)
                    {
                        return string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
                    });
                }

                // 变体提供者变化不增条目，但会让格子数变化，同样要推进 Generation
                Generation++;
                LastAppendCount = added;
                reason = added > 0 ? ("appended " + added) : "variant providers changed";

                if (ModEntry.Log != null)
                {
                    ModEntry.Log.Log(string.Format(
                        "[build] appended {0} late item(s), total visible={1} (no full rebuild)", added, Visible.Count));
                }

                return true;
            }
            catch (Exception e)
            {
                LogError("append new items", e);
                reason = "exception: " + e.Message;
                return false;
            }
        }

        private static Dictionary<string, bool> CollectAllIds()
        {
            // id -> 是否来自 UnusedDirectory（同一 id 若也出现在正常目录里，以正常目录为准）
            var ids = new Dictionary<string, bool>(StringComparer.Ordinal);

            DirectoryCount = 0;

            try
            {
                // DirectoryMaster.directories 是私有成员，改用公开的分类名 + 标识列表接口
                var classNames = DirectoryMaster.GetDictionaryClassNameList<GameItem>();
                if (classNames != null && classNames.Count > 0)
                {
                    for (int i = 0; i < classNames.Count; i++)
                    {
                        string className = classNames[i];
                        if (string.IsNullOrEmpty(className)) continue;

                        DirectoryCount++;
                        bool unusedDirectory =
                            className.IndexOf("Unused", StringComparison.OrdinalIgnoreCase) >= 0;

                        List<string> idList;
                        try { idList = DirectoryMaster.GetIdentifierList<GameItem>(className); }
                        catch (Exception e)
                        {
                            LogError("list ids of " + className, e);
                            continue;
                        }

                        Collect(ids, idList, unusedDirectory);
                    }
                }
                else
                {
                    // 兜底：一次性列出全部标识
                    DirectoryCount++;
                    Collect(ids, DirectoryMaster.GetIdentifierList<GameItem>(null), false);
                }

                if (ids.Count == 0)
                {
                    // 分类名接口没给出结果时再退一步，直接请求全量标识
                    Collect(ids, DirectoryMaster.GetIdentifierList<GameItem>(null), false);
                    if (ids.Count == 0)
                        Collect(ids, DirectoryMaster.GetIdentifierList<GameItem>(string.Empty), false);

                    if (ModEntry.Log != null)
                        ModEntry.Log.Warning("item directory names yielded no entries, fallback collected " + ids.Count);
                }
            }
            catch (Exception e)
            {
                LogError("enumerate item directories", e);
            }

            return ids;
        }

        /// <summary>把一件物品收进快照（注册表条目与模组额外条目共用）。</summary>
        private static void AddEntry(string id, GameItem item, bool unused, bool fromProvider = false)
        {
            if (string.IsNullOrEmpty(id) || item == null) return;

            var entry = new ItemEntry { Id = id, Unused = unused, Item = item, FromProvider = fromProvider };

            try { entry.Name = Trimmed(item.name); } catch { }

            // 先取短描述，空的话退回风味文本（手工构造的物品往往只填了后者）
            try
            {
                entry.Desc = Trimmed(item.shortDescription);
                if (string.IsNullOrEmpty(entry.Desc)) entry.Desc = Trimmed(item.flavorText);
            }
            catch { }
            try { entry.Value = item.unitValue; } catch { }
            try { entry.Types = item.itemTypes; } catch { }
            try { if (item.isDebugMenu) entry.Unused = true; } catch { }

            try
            {
                entry.ModId = TagString(item, ModHelper.MOD_ID_TAG);
                entry.ModName = TagString(item, ModHelper.MOD_NAME_TAG);
                entry.SpriteAtlas = Trimmed(item.spriteAtlasPath);
                entry.SpriteName = Trimmed(item.spritePath);
                entry.SpriteKey = BuildSpriteKey(item, entry.ModId);
            }
            catch (Exception e)
            {
                if (FailedCount <= 3) LogError("read metadata of '" + id + "'", e);
            }

            All.Add(entry);
            if (!entry.Unused) Visible.Add(entry);
        }

        internal static void Build(bool force)
        {
            if (_built && !force) return;

            var watch = Stopwatch.StartNew();
            All.Clear();
            Visible.Clear();
            FailedCount = 0;

            var ids = CollectAllIds();

            foreach (var pair in ids)
            {
                GameItem item;
                try
                {
                    item = DirectoryMaster.Item(pair.Key);
                }
                catch (Exception e)
                {
                    FailedCount++;
                    if (FailedCount <= 3) LogError("create item '" + pair.Key + "'", e);
                    continue;
                }

                if (item == null)
                {
                    FailedCount++;
                    continue;
                }

                AddEntry(pair.Key, item, pair.Value);
            }

            // 模组通过 ItemCatalogApi 注册的额外条目：注册表里没有的才补进来
            List<KeyValuePair<string, GameItem>> extras;
            try { extras = ItemCatalogApi.CollectExtraItems(); }
            catch (Exception e) { extras = null; LogError("collect extra items", e); }

            ExtraCount = 0;
            _knownExtraIds.Clear();
            if (extras != null)
            {
                for (int i = 0; i < extras.Count; i++)
                {
                    string id = extras[i].Key;
                    if (string.IsNullOrEmpty(id) || ids.ContainsKey(id)) continue;
                    if (!_knownExtraIds.Add(id)) continue;   // 提供者之间重复

                    AddEntry(id, extras[i].Value, false, true);
                    ExtraCount++;
                }
            }

            _knownProviderCount = ItemCatalogApi.ItemProviderCount;
            _knownVariantProviderCount = ItemCatalogApi.VariantProviderCount;

            // 按物品 ID 排序：同一前缀（同类物品）会自然聚在一起
            Visible.Sort(delegate (ItemEntry a, ItemEntry b)
            {
                return string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
            });

            // 记录本次快照，供 HasNewItems 比对（注册表 id 与额外条目分开存）
            _knownIds.Clear();
            foreach (var pair in ids) _knownIds.Add(pair.Key);

            watch.Stop();
            BuildMilliseconds = watch.ElapsedMilliseconds;
            _built = true;
            Generation++;   // 通知 UI：变体展开需要重算

            RecipeIndex.Invalidate();   // 物品表变了，配方索引下次查询时重建
            SourceIndex.Invalidate();   // 来源索引同理

            if (ModEntry.Log != null)
            {
                ModEntry.Log.Log(string.Format(ModEntry.T("build_log"), Visible.Count)
                    + string.Format(" (directories={0}, total={1}, hidden={2}, failed={3}, extra={4}, {5} ms)",
                        DirectoryCount, All.Count, All.Count - Visible.Count, FailedCount, ExtraCount, BuildMilliseconds));
            }
        }

        /// <summary>
        /// 由常驻节拍器每帧调用：有模组通过 <see cref="ItemCatalogApi.NotifyItemsChanged()"/> 报过变更
        /// 就处理一次。没有待处理标记时只是一次布尔判断。
        /// </summary>
        /// <returns>true = 这次真的补入/重建了内容（调用方可以顺手预热图标）。</returns>
        internal static bool ProcessPendingAppend()
        {
            string source;
            if (!ItemCatalogApi.ConsumeItemsChanged(out source)) return false;

            string reason = null;
            bool appended = false;

            try { appended = TryAppendNewItems(out reason); }
            catch (Exception e) { reason = "exception: " + e.Message; }

            if (!appended)
            {
                // 有条目被删掉 / 提供者撤销：这种低频情况才整表重建
                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[build] full rebuild (" + (source ?? "notify") + "): " + (reason ?? "unknown"));

                try
                {
                    Build(true);
                }
                catch (Exception e)
                {
                    LogError("rebuild after notify", e);
                    return false;
                }

                ItemCatalogApi.RaiseRefreshed(Visible.Count);
                return true;
            }

            if (ModEntry.Log != null)
            {
                ModEntry.Log.Log("[build] notify '" + (source ?? "?") + "' -> " + (reason ?? "ok"));
            }

            if (LastAppendCount > 0) ItemCatalogApi.RaiseRefreshed(Visible.Count);

            return LastAppendCount > 0;
        }

        internal static void Dump()
        {
            if (ModEntry.Log == null) return;

            EnsureBuilt();
            ModEntry.Log.Log(string.Format(
                "[dump] visible={0} total={1} hidden={2} failed={3} directories={4} build={5}ms",
                Visible.Count, All.Count, All.Count - Visible.Count, FailedCount, DirectoryCount, BuildMilliseconds));

            int shown = Math.Min(20, Visible.Count);
            for (int i = 0; i < shown; i++)
            {
                var it = Visible[i];
                ModEntry.Log.Log(string.Format("[dump] {0} | {1} | value={2} | atlas={3} | sprite={4} | mod={5}",
                    it.Id, it.DisplayName, it.Value, it.SpriteAtlas ?? "-", it.SpriteName ?? "-", it.ModName ?? "vanilla"));
            }
        }

        private static void Collect(Dictionary<string, bool> ids, List<string> idList, bool unusedDirectory)
        {
            if (idList == null) return;

            for (int i = 0; i < idList.Count; i++)
            {
                string id = idList[i];
                if (string.IsNullOrEmpty(id)) continue;

                bool unused;
                if (ids.TryGetValue(id, out unused))
                {
                    if (!unusedDirectory) ids[id] = false;
                }
                else
                {
                    ids[id] = unusedDirectory;
                }
            }
        }

        private static string BuildSpriteKey(GameItem item, string modId)
        {
            string path = item.spritePath;
            if (string.IsNullOrEmpty(path)) return null;

            if (!string.IsNullOrEmpty(modId)) return modId + ":" + path;

            string atlas = item.spriteAtlasPath;
            if (item.useSpriteFromMod) return path;      // 没有来源标签时退回单键
            if (!string.IsNullOrEmpty(atlas)) return atlas + "/" + path;
            return path;
        }

        private static string TagString(GameItem item, string tag)
        {
            var state = item.GetTagReadonly(tag);
            return state == null ? null : state.GetString();
        }

        private static string Trimmed(string value)
        {
            return string.IsNullOrEmpty(value) ? null : value.Trim();
        }

        private static void LogError(string what, Exception e)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("failed to " + what + ": " + e.Message);
        }
    }
}
