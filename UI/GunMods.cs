using System;
using System.Collections.Generic;

using ItemCatalog.Catalog;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 枪械配件：把「这把枪有哪些改装槽、每个槽收什么」交给枪自己的原版窗口决定，
    /// 不再维护固定 8 槽的 tag 表（表只作为借不到窗口时的兜底）。
    ///
    /// 槽的来源：<c>gun.contentWindow</c> 的面板行，凡是有 <see cref="GameSlotInventory"/>
    /// 的面板就是一个槽，顺序按面板顺序；模组新加的槽自动出现。
    ///
    /// 每个槽「收什么」：把该槽的 <c>mayInventoryAddItemFunc</c> 拿空白探测物品调一次，
    /// 用 <see cref="IsTagCapture"/> 记下期间被问到的 tag。预览场景下
    /// 「拥有状态」「互斥检查」都是常量，所以这等价于槽自己的 tag 要求，
    /// 不需要为每个配件各跑一遍委托。
    ///
    /// 配件侧的限制（<c>GunModHelper.AllowOnlyWeapon</c> 写进闭包 <c>weaponIds</c>）
    /// 仍按原来的方式读（<c>ModReflect</c>）。
    /// </summary>
    internal static class GunMods
    {
        internal const string StockTag = "MOD_STOCK_TAG";
        internal const string RailTag = "MOD_RAIL_TAG";
        internal const string MagazineTag = "MOD_MAGAZINE_TAG";
        internal const string UnderbarrelTag = "MOD_UNDER_RAIL_TAG";
        internal const string BarrelTag = "MOD_BARREL_TAG";
        internal const string SpecialTag = "MOD_SPECIAL_TAG";

        /// <summary>任何配件都会带的统一 tag（<c>GunModHelper.InitGunMod</c> 打上）。</summary>
        internal const string ModTag = "WEAPON_MOD_TAG";

        /// <summary>原版 <c>CreateGunModWindow</c> 的面板顺序（8 槽，5 列）——兜底用。</summary>
        internal static readonly string[] SlotKeys =
        {
            "mech_gun_tag_stock",
            "mech_gun_tag_rail",
            "mech_gun_tag_magazine",
            "mech_gun_tag_underbarrel",
            "mech_gun_tag_barrel",
            "mech_gun_tag_special1",
            "mech_gun_tag_special2",
            "mech_gun_tag_special3"
        };

        private static readonly string[] SlotTags =
        {
            StockTag, RailTag, MagazineTag, UnderbarrelTag, BarrelTag,
            SpecialTag, SpecialTag, SpecialTag
        };

        /// <summary>拿不到原版显示名时的兜底（与其它 Loc 兜底一样只用中文）。</summary>
        private static readonly string[] FallbackTitles =
        {
            "枪托", "导轨", "弹匣", "下挂", "枪管", "特殊槽位1", "特殊槽位2", "特殊槽位3"
        };

        /// <summary>原版固定槽数（手写兜底壳 / 借不到窗口时用）。</summary>
        internal const int LegacySlotCount = 8;

        /// <summary>一个改装槽：面板角色、显示名、探测到的 tag 要求、可装配件（懒加载）。</summary>
        internal sealed class GunSlot
        {
            internal string Role;
            internal string Title;
            internal string[] Tags;
            internal List<string> Candidates;

            /// <summary>真的从窗口探测出来的（手写兜底的 8 槽不算，不写缓存）。</summary>
            internal bool Probed;

            /// <summary>
            /// 槽的判定对任何探测物品都返回 false（既没问 tag 又不收）。
            /// 这种槽是一格都装不了，候选必须为空，而不是「不限」。
            /// </summary>
            internal bool RejectsAll;
        }

        /// <summary>这把枪实际的改装槽列表（探测失败时退回原版 8 槽）。</summary>
        internal static List<GunSlot> SlotsFor(string gunId)
        {
            if (string.IsNullOrEmpty(gunId)) return LegacySlots();

            ResetForGeneration();

            List<GunSlot> cached;
            if (_slots.TryGetValue(gunId, out cached) && cached != null) return cached;

            // 目录还没建好（配件列表为空）时探测出的 tag 不可信：先退回原版槽，不写缓存
            if (ModIds().Count == 0) return LegacySlots();

            List<GunSlot> probed;
            try { probed = Probe(gunId); }
            catch (Exception e) { Warn("probe '" + gunId + "': " + e.Message); probed = null; }

            // 借不到窗口（不是模组化枪 / 面板行解析失败）→ 用原版 8 槽兜底（不写进缓存）
            if (probed == null || probed.Count == 0)
            {
                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[gun-slot] " + gunId + " probe empty, legacy 8 slots");

                return LegacySlots();
            }

            _slots[gunId] = probed;
            IndexManager.Invalidate(GunsByModIndex);   // 新枪的真实槽位会影响反查
            CatalogDataBuilder.InvalidateRelations();  // 让关系表下次重建时带上这把枪的槽位
            SaveCache();

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[gun-slot] " + gunId + " slots=" + probed.Count
                                 + " tags=" + DescribeTags(probed));

            return probed;
        }

        internal static int SlotCountFor(string gunId)
        {
            return SlotsFor(gunId).Count;
        }

        /// <summary>
        /// 加载阶段调用：把目录里所有可改装枪的槽位一次探完并写缓存，
        /// 打开配件页时不再现建枪实例（与拆枪 / 箱子等探测同一时机）。
        /// 探测失败不写缓存，留给 <see cref="SlotsFor"/> 在真正用到时重试。
        /// </summary>
        internal static void Warmup()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();

            ResetForGeneration();

            var guns = ModdableGunIds();
            if (guns.Count == 0) return;

            // 目录还没建好（配件列表为空）时探测出的 tag 不可信，和 SlotsFor 同一判定
            if (ModIds().Count == 0) return;

            int cached = 0, probed = 0, failed = 0;

            for (int i = 0; i < guns.Count; i++)
            {
                string gunId = guns[i];
                if (string.IsNullOrEmpty(gunId)) continue;

                List<GunSlot> known;
                if (_slots.TryGetValue(gunId, out known) && known != null)
                {
                    cached++;
                    continue;
                }

                List<GunSlot> slots = null;
                try { slots = Probe(gunId); }
                catch (Exception e) { Warn("warmup '" + gunId + "': " + e.Message); }

                // 借不到窗口的枪保持未探测状态，翻到配件页时再试
                if (slots == null || slots.Count == 0)
                {
                    failed++;
                    continue;
                }

                _slots[gunId] = slots;
                probed++;
            }

            // 新探测到的槽位写探测缓存，并让关系表在下次重建时带上
            if (probed > 0)
            {
                SaveCache();
                CatalogDataBuilder.InvalidateRelations();
            }

            // 反查索引也在这里建好：打开配件的「用途」页时不再现算
            try { EnsureReverseIndex(); }
            catch (Exception e) { Warn("warmup index: " + e.Message); }

            if (ModEntry.Log != null)
            {
                ModEntry.Log.Log("[gun-slot] warmup guns=" + guns.Count
                                 + " cached=" + cached
                                 + " probed=" + probed
                                 + " failed=" + failed
                                 + " (" + watch.ElapsedMilliseconds + " ms)");
            }
        }

        /// <summary>能改装的枪（原版非左轮那几把），按 id 排序。</summary>
        internal static List<string> ModdableGunIds()
        {
            if (_moddableGunIds != null && _moddableGunIdsGeneration == CatalogDataBuilder.Generation)
                return _moddableGunIds;

            var ids = new List<string>();

            foreach (var entry in RecipeIndex.AllEntries)
            {
                if (entry == null || entry.Unused || string.IsNullOrEmpty(entry.Id)) continue;
                if (!IsModdable(entry.Item)) continue;
                ids.Add(entry.Id);
            }

            ids.Sort(StringComparer.OrdinalIgnoreCase);
            _moddableGunIds = ids;
            _moddableGunIdsGeneration = CatalogDataBuilder.Generation;
            return ids;
        }

        private static List<string> _moddableGunIds;
        private static int _moddableGunIdsGeneration = -1;

        internal static string SlotTitleFor(string gunId, int slot)
        {
            var slots = SlotsFor(gunId);
            if (slot < 0 || slot >= slots.Count) return string.Empty;
            return slots[slot].Title;
        }

        /// <summary>某个槽能装的配件 id（按目录顺序，已按枪的限制过滤）。</summary>
        internal static List<string> Compatible(string gunId, int slot)
        {
            var slots = SlotsFor(gunId);
            if (slot < 0 || slot >= slots.Count) return new List<string>();

            var spec = slots[slot];
            if (spec.Candidates == null) spec.Candidates = BuildCandidates(gunId, spec);
            return spec.Candidates;
        }

        /// <summary>原版 8 槽的显示名（手写兜底壳用）。</summary>
        internal static string LegacySlotTitle(int slot)
        {
            if (slot < 0 || slot >= FallbackTitles.Length) return string.Empty;
            return VanillaMachinePanel.MechLabel(SlotKeys[slot], FallbackTitles[slot]);
        }

        /// <summary>这把枪有没有改装槽（原版 <c>InitGun(..., moddable)</c> 给非左轮枪加的类型）。</summary>
        internal static bool IsModdable(GameItem gun)
        {
            if (gun == null) return false;

            try { return gun.IsGameItemType("MODDABLE"); }
            catch { return false; }
        }

        /// <summary>这件物品是不是枪械配件（统一 tag，模组配件也走同一个）。</summary>
        internal static bool IsMod(GameItem item)
        {
            if (item == null) return false;

            try { if (item.IsTag(ModTag)) return true; }
            catch { }

            // 兜底：老版本 / 手工拼的配件可能只带槽位 tag
            for (int i = 0; i < SlotTags.Length; i++)
            {
                try { if (item.IsTag(SlotTags[i])) return true; }
                catch { }
            }

            return false;
        }

        /// <summary>
        /// 这件配件能不能装到这把枪上（任一槽接受即算）。
        /// 反查走一次建好的「配件 → 枪」索引，不再每次把每把枪的每个槽重新扫一遍。
        /// </summary>
        internal static bool FitsAnySlot(string gunId, string modId)
        {
            if (string.IsNullOrEmpty(gunId) || string.IsNullOrEmpty(modId)) return false;

            ResetForGeneration();

            var index = EnsureReverseIndex();
            if (index == null) return false;

            HashSet<string> guns;
            return index.TryGetValue(modId, out guns) && guns.Contains(gunId);
        }

        /// <summary>
        /// 反查索引：配件 id → 能装它的枪。
        /// 只在内存里，由 <see cref="IndexManager"/> 按 generation 缓存；
        /// 落盘的「物品 → 关系」索引统一走 CatalogDataBuilder 的关系总表，不再存物物关联。
        /// </summary>
        private static Dictionary<string, HashSet<string>> EnsureReverseIndex()
        {
            return IndexManager.Get(GunsByModIndex, BuildReverseIndex);
        }

        private static Dictionary<string, HashSet<string>> BuildReverseIndex()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var index = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            var guns = ModdableGunIds();
            for (int i = 0; i < guns.Count; i++)
            {
                string gunId = guns[i];
                if (string.IsNullOrEmpty(gunId)) continue;

                List<GunSlot> slots;
                if (_slots.TryGetValue(gunId, out slots) && slots != null)
                {
                    for (int slot = 0; slot < slots.Count; slot++)
                    {
                        var candidates = Compatible(gunId, slot);
                        for (int c = 0; c < candidates.Count; c++)
                            AddGunForMod(index, candidates[c], gunId);
                    }
                }
                else
                {
                    // 探测失败的枪：按原版 8 槽的 tag 兜底
                    var mods = ModItems();
                    for (int m = 0; m < mods.Count; m++)
                        if (FitsLegacy(mods[m].Key, gunId))
                            AddGunForMod(index, mods[m].Key, gunId);
                }
            }

            if (ModEntry.Log != null)
            {
                ModEntry.Log.Log("[gun-slot] index mods=" + ModItems().Count
                                 + " guns=" + guns.Count
                                 + " (" + watch.ElapsedMilliseconds + " ms)");
            }

            return index;
        }

        private static void AddGunForMod(Dictionary<string, HashSet<string>> index, string modId, string gunId)
        {
            if (string.IsNullOrEmpty(modId)) return;

            HashSet<string> guns;
            if (!index.TryGetValue(modId, out guns))
            {
                guns = new HashSet<string>(StringComparer.Ordinal);
                index[modId] = guns;
            }
            guns.Add(gunId);
        }

        private static bool FitsLegacy(string modId, string gunId)
        {
            var entry = RecipeIndex.FindEntry(modId);
            var item = entry == null ? null : entry.Item;
            if (item == null) return false;

            if (!FitsCached(modId, item, gunId)) return false;

            for (int i = 0; i < SlotTags.Length; i++)
            {
                try { if (item.IsTag(SlotTags[i])) return true; }
                catch { }
            }

            return false;
        }

        // ---------------------------------------------------------------- 探测

        private static readonly Dictionary<string, List<GunSlot>> _slots =
            new Dictionary<string, List<GunSlot>>(StringComparer.Ordinal);

        private static int _generation = -1;

        /// <summary>「配件 × 枪」的装填判定缓存：同一对只跑一次 mayInventoryAddItemFunc。</summary>
        private static Dictionary<string, bool> _fitsCache;

        /// <summary>反查索引 / 配件清单在 <see cref="IndexManager"/> 里的名字。</summary>
        private const string GunsByModIndex = "gun-mod-guns";
        private const string ModListIndex = "gun-mod-items";

        /// <summary>配件清单：id 列表 + 实例，由 <see cref="IndexManager"/> 按 generation 缓存。</summary>
        private sealed class ModList
        {
            internal List<string> Ids;
            internal List<KeyValuePair<string, GameItem>> Items;
        }

        // ---------------------------------------------------------------- 缓存
        //
        // 枪槽探测结果单独存 gun_slots.txt：这是探测原始数据（槽位 tag / 拒绝标记），
        // 不是物品索引。索引里只保留「枪 ↔ 槽位 ↔ 候选配件」的关系记录（item-relations）。
        // 文件带 sig 头，游戏 / 模组变化后整体作废重探。

        private const string CacheFile = "gun_slots.txt";

        private static bool _cacheLoaded;

        /// <summary>目录 generation 变化时清空内存结果，并重新读一次缓存。</summary>
        private static void ResetForGeneration()
        {
            if (_generation == CatalogDataBuilder.Generation) return;

            _generation = CatalogDataBuilder.Generation;
            _slots.Clear();
            _fitsCache = null;
            _cacheLoaded = false;
            LoadCache();
        }

        private static void LoadCache()
        {
            if (_cacheLoaded) return;
            _cacheLoaded = true;

            try
            {
                if (!ModHelper.ModDataFileExists(ModEntry.MOD_ID, CacheFile)) return;

                string text = ModHelper.ReadModDataFileText(ModEntry.MOD_ID, CacheFile);
                if (string.IsNullOrEmpty(text)) return;

                var map = ParseSlotCache(text);
                if (map == null || map.Count == 0) return;

                foreach (var pair in map) _slots[pair.Key] = pair.Value;

                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[gun-slot] cache loaded: " + map.Count + " guns");
            }
            catch (Exception e) { Warn("cache read: " + e.Message); }
        }

        private static Dictionary<string, List<GunSlot>> ParseSlotCache(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            var map = new Dictionary<string, List<GunSlot>>(StringComparer.Ordinal);
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool sigChecked = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;

                if (!sigChecked)
                {
                    sigChecked = true;

                    if (!line.StartsWith("sig=", StringComparison.Ordinal)) return null;
                    if (!string.Equals(line.Substring(4), CacheSignature.Value, StringComparison.Ordinal))
                        return null;
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq);
                string value = line.Substring(eq + 1);

                if (!key.StartsWith("gun:", StringComparison.Ordinal)) continue;

                string gunId = key.Substring(4);
                if (gunId.Length == 0) continue;

                var slots = ParseSlots(value);
                if (slots.Count > 0) map[gunId] = slots;
            }

            return map.Count > 0 ? map : null;
        }

        private static List<GunSlot> ParseSlots(string value)
        {
            var slots = new List<GunSlot>();
            if (string.IsNullOrEmpty(value)) return slots;

            var parts = value.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (string.IsNullOrEmpty(part)) continue;

                var fields = part.Split('~');
                if (fields.Length < 2 || string.IsNullOrEmpty(fields[0])) continue;

                var tags = string.IsNullOrEmpty(fields[1])
                    ? new string[0]
                    : fields[1].Split('|');

                slots.Add(new GunSlot
                {
                    Role = fields[0],
                    Title = VanillaMachinePanel.MechLabel(fields[0], fields[0]),
                    Tags = tags,
                    RejectsAll = fields.Length > 2 && fields[2] == "1",
                    Probed = true
                });
            }

            return slots;
        }

        private static void SaveCache()
        {
            try
            {
                int guns = 0;
                var text = new System.Text.StringBuilder();
                text.Append("sig=").Append(CacheSignature.Value).Append('\n');

                foreach (var pair in _slots)
                {
                    var slots = pair.Value;
                    if (slots == null || slots.Count == 0) continue;
                    if (!slots[0].Probed) continue;   // 手写兜底的 8 槽不写

                    guns++;
                    text.Append("gun:").Append(pair.Key).Append('=');

                    for (int i = 0; i < slots.Count; i++)
                    {
                        if (i > 0) text.Append(';');

                        var slot = slots[i];
                        text.Append(slot.Role).Append('~');
                        if (slot.Tags != null) text.Append(string.Join("|", slot.Tags));
                        text.Append('~').Append(slot.RejectsAll ? "1" : "0");
                    }

                    text.Append('\n');
                }

                if (guns == 0) return;

                ModHelper.WriteModDataFileText(ModEntry.MOD_ID, CacheFile, text.ToString());

                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[gun-slot] cache saved: " + guns + " guns");
            }
            catch (Exception e) { Warn("cache write: " + e.Message); }
        }

        private static List<GunSlot> Probe(string gunId)
        {
            var result = new List<GunSlot>();

            GameItem gun = null;
            try { gun = CatalogWindow.CreatePreviewItem(gunId); }
            catch (Exception e) { Warn("create gun '" + gunId + "': " + e.Message); }
            if (gun == null) return result;

            GameItem probe = null;
            try
            {
                var row = WindowRow(gun);
                if (row == null) return result;

                probe = PickProbeItem() ?? gun;

                var panels = row.Panels;
                for (int i = 0; i < panels.Count; i++)
                {
                    var panel = panels[i];
                    var slot = VanillaMachinePanel.SlotOf(panel);
                    if (slot == null) continue;

                    bool rejectsAll;
                    var tags = ProbeTags(slot, probe, out rejectsAll);

                    result.Add(new GunSlot
                    {
                        Role = panel.role,
                        Title = TitleOf(panel),
                        Tags = tags,
                        RejectsAll = rejectsAll,
                        Probed = true
                    });
                }
            }
            catch (Exception e) { Warn("probe '" + gunId + "': " + e.Message); }
            finally
            {
                if (probe != null && !ReferenceEquals(probe, gun))
                {
                    try { probe.Destroy(); } catch { }
                }
                try { gun.Destroy(); } catch { }
            }

            return result;
        }

        private static MachineryPanelRow WindowRow(GameItem gun)
        {
            var window = gun == null ? null : gun.contentWindow;
            return window == null ? null : window.childElement as MachineryPanelRow;
        }

        private static string TitleOf(MachineryPanel panel)
        {
            string title = null;
            try { title = panel.label == null ? null : panel.label.GetText(); }
            catch { }

            if (!string.IsNullOrEmpty(title)) return title;

            return VanillaMachinePanel.MechLabel(panel.role, panel.role);
        }

        /// <summary>
        /// 拿空白探测物品调一次槽的入格判定，返回它问过的 tag（只保留真正有区分度的）。
        /// 预览场景下「拥有状态」「互斥」都是常量，所以这就是槽自己的 tag 要求。
        /// <paramref name="rejectsAll"/>：既没问 tag、又直接返回 false——这个槽一格都装不了。
        /// </summary>
        private static string[] ProbeTags(GameSlotInventory slot, GameItem probe, out bool rejectsAll)
        {
            rejectsAll = false;

            var func = slot == null ? null : slot.mayInventoryAddItemFunc;
            if (func == null) return new string[0];

            bool accepted = false;
            var raw = IsTagCapture.Capture(probe, delegate
            {
                try { accepted = func(probe, slot); } catch { }
            });

            if (raw.Count == 0 && !accepted) rejectsAll = true;

            var mods = ModItems();
            var keep = new List<string>();

            for (int i = 0; i < raw.Count; i++)
            {
                string tag = raw[i];
                if (string.IsNullOrEmpty(tag)) continue;

                int matched = 0;
                for (int m = 0; m < mods.Count; m++)
                {
                    var item = mods[m].Value;
                    if (item == null) continue;

                    try { if (item.IsTag(tag)) matched++; }
                    catch { }
                }

                // 一件配件都不带（空类别）或所有配件都带（没有区分度）的 tag 不保留
                if (matched > 0 && matched < mods.Count) keep.Add(tag);
            }

            return keep.ToArray();
        }

        /// <summary>目录里所有配件的 id（按 generation 缓存）。</summary>
        private static List<string> ModIds()
        {
            return Mods().Ids;
        }

        /// <summary>目录里所有配件（id + 实例），按 generation 缓存；候选 / 反查只扫这一份。</summary>
        private static List<KeyValuePair<string, GameItem>> ModItems()
        {
            return Mods().Items;
        }

        /// <summary>配件清单统一放进 <see cref="IndexManager"/>，跟着目录 generation 失效。</summary>
        private static ModList Mods()
        {
            return IndexManager.Get(ModListIndex, BuildMods);
        }

        private static ModList BuildMods()
        {
            var items = new List<KeyValuePair<string, GameItem>>();

            foreach (var entry in RecipeIndex.AllEntries)
            {
                if (entry == null || entry.Unused || string.IsNullOrEmpty(entry.Id)) continue;

                var item = entry.Item;
                if (item == null || !IsMod(item)) continue;

                items.Add(new KeyValuePair<string, GameItem>(entry.Id, item));
            }

            var ids = new List<string>(items.Count);
            for (int i = 0; i < items.Count; i++) ids.Add(items[i].Key);

            return new ModList { Ids = ids, Items = items };
        }

        private static GameItem PickProbeItem()
        {
            // 只需要一件「没有特殊 tag」的物品：探测器只记录被问到的 tag，
            // 物品本身带什么不影响结果。
            foreach (var entry in RecipeIndex.AllEntries)
            {
                if (entry == null || entry.Unused || entry.Item == null) continue;
                if (IsMod(entry.Item)) continue;

                try { return CatalogWindow.CreatePreviewItem(entry.Id); }
                catch { }
            }

            return null;
        }

        private static List<string> BuildCandidates(string gunId, GunSlot slot)
        {
            var ids = new List<string>();
            if (slot == null || slot.RejectsAll) return ids;

            var mods = ModItems();
            for (int i = 0; i < mods.Count; i++)
            {
                var item = mods[i].Value;
                if (item == null) continue;
                if (!FitsCached(mods[i].Key, item, gunId)) continue;
                if (!HasAnyTag(item, slot.Tags)) continue;

                ids.Add(mods[i].Key);
            }

            ids.Sort(StringComparer.OrdinalIgnoreCase);
            return ids;
        }

        private static bool HasAnyTag(GameItem item, string[] tags)
        {
            if (tags == null || tags.Length == 0) return true;   // 没探测到要求 = 不限制

            for (int i = 0; i < tags.Length; i++)
            {
                string tag = tags[i];
                if (string.IsNullOrEmpty(tag)) continue;

                try { if (item.IsTag(tag)) return true; }
                catch { }
            }

            return false;
        }

        private static List<GunSlot> LegacySlots()
        {
            var list = new List<GunSlot>(LegacySlotCount);
            for (int i = 0; i < LegacySlotCount; i++)
            {
                list.Add(new GunSlot
                {
                    Role = SlotKeys[i],
                    Title = LegacySlotTitle(i),
                    Tags = new[] { SlotTags[i] }
                });
            }

            return list;
        }

        private static string DescribeTags(List<GunSlot> slots)
        {
            if (slots == null) return "-";

            var text = new System.Text.StringBuilder();
            for (int i = 0; i < slots.Count; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append(slots[i].Role).Append('=');
                if (slots[i].RejectsAll) { text.Append('x'); continue; }

                var tags = slots[i].Tags;
                if (tags == null || tags.Length == 0) text.Append("-");
                else text.Append(string.Join("|", tags));
            }

            return text.ToString();
        }

        /// <summary>
        /// <see cref="Fits"/> 的结果缓存：配对键是「枪 id + 配件 id」。
        /// 候选列表按槽位分别算，没有缓存时同一对会按槽数重复调代理。
        /// </summary>
        private static bool FitsCached(string modId, GameItem item, string gunId)
        {
            string key = gunId + "\n" + modId;

            bool value;
            if (_fitsCache != null && _fitsCache.TryGetValue(key, out value)) return value;

            value = Fits(item, gunId);
            if (_fitsCache == null) _fitsCache = new Dictionary<string, bool>(StringComparer.Ordinal);
            _fitsCache[key] = value;
            return value;
        }

        /// <summary>
        /// 配件自己的「仅限这些枪」限制。`GunModHelper.AllowOnlyWeapon` 把枪 id 数组
        /// 塞进委托闭包字段 `weaponIds`；没有这个字段就表示不限枪。
        /// 读不到时按「允许」处理，宁可多列也不漏列。
        ///
        /// 闭包类型是编译器生成的，编译期拿不到字面类型，所以这里走 `ModReflect`
        /// （游戏提供的替身：由游戏检查成员归属，不需要任何能力）。
        /// 以前是 `target.GetType().GetField(...)`，会被扫描器判成 reflection-external，
        /// 重新授权时模组会被拒绝加载。
        /// </summary>
        private static bool Fits(GameItem mod, string gunId)
        {
            try
            {
                var func = mod.mayInventoryAddItemFunc;
                if (func == null) return true;

                var list = func.GetInvocationList();
                for (int i = 0; i < list.Length; i++)
                {
                    var target = list[i] == null ? null : list[i].Target;
                    if (target == null) continue;

                    if (!ModReflect.Has(target, "weaponIds")) continue;

                    var allowed = ModReflect.Get<string[]>(target, "weaponIds");
                    if (allowed == null) continue;

                    for (int k = 0; k < allowed.Length; k++)
                        if (string.Equals(allowed[k], gunId, StringComparison.Ordinal)) return true;

                    return false;
                }
            }
            catch { }

            return true;
        }

        private static void Warn(string msg)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("[gun-slot] " + msg);
        }
    }
}
