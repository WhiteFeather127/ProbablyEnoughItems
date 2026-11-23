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

        /// <summary>这件配件能不能装到这把枪上（任一槽接受即算）。</summary>
        internal static bool FitsAnySlot(string gunId, string modId)
        {
            if (string.IsNullOrEmpty(gunId) || string.IsNullOrEmpty(modId)) return false;

            ResetForGeneration();

            // 已探测过（或缓存里读到）的枪：按真实槽判定
            List<GunSlot> known;
            if (_slots.TryGetValue(gunId, out known) && known != null)
            {
                for (int i = 0; i < known.Count; i++)
                    if (Compatible(gunId, i).Contains(modId)) return true;

                return false;
            }

            // 还没探测（懒加载）：先用原版 8 槽的 tag 快速判，不现建枪实例，
            // 免得打开配件页时一次性把几十把枪都造一遍。
            return FitsLegacy(modId, gunId);
        }

        private static bool FitsLegacy(string modId, string gunId)
        {
            var entry = RecipeIndex.FindEntry(modId);
            var item = entry == null ? null : entry.Item;
            if (item == null) return false;

            if (!Fits(item, gunId)) return false;

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

        /// <summary>目录里所有配件 id（探测 tag 是否需要时用来算「有多少配件带它」）。</summary>
        private static List<string> _modIds;

        // ---------------------------------------------------------------- 缓存
        //
        // 探测结果落在模组数据目录的 gun_slots.txt：
        //   sig=<游戏版本 + 所有启用模组指纹，与箱子探测同一套失效规则>
        //   probe=<格式版本>
        //   gun:<枪id>=<role>~<tag1|tag2>~<rejects 0/1>;<role>~…
        // 缓存命中时，反查（某配件能装哪些枪）不需要再把每把枪都造一遍。

        private const string CacheFile = "gun_slots.txt";
        private const string CacheVersion = "1";

        private static bool _cacheLoaded;

        /// <summary>目录 generation 变化时清空内存结果，并重新读一次缓存。</summary>
        private static void ResetForGeneration()
        {
            if (_generation == CatalogDataBuilder.Generation) return;

            _generation = CatalogDataBuilder.Generation;
            _slots.Clear();
            _modIds = null;
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

                string sig = null, probe = null;
                var map = new Dictionary<string, List<GunSlot>>(StringComparer.Ordinal);

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
                    if (!key.StartsWith("gun:", StringComparison.Ordinal)) continue;

                    string gunId = key.Substring(4);
                    if (gunId.Length == 0) continue;

                    var slots = ParseSlots(value);
                    if (slots.Count > 0) map[gunId] = slots;
                }

                // 游戏 / 模组变了就整份作废（和箱子内容、拆枪产物同一套签名）
                if (!string.Equals(probe, CacheVersion, StringComparison.Ordinal)) return;
                if (!string.Equals(sig, BoxProbe.Signature(), StringComparison.Ordinal)) return;

                foreach (var pair in map) _slots[pair.Key] = pair.Value;

                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[gun-slot] cache loaded: " + map.Count + " guns");
            }
            catch (Exception e) { Warn("cache read: " + e.Message); }
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
                var sb = new System.Text.StringBuilder();
                sb.Append("sig=").Append(BoxProbe.Signature()).Append('\n');
                sb.Append("probe=").Append(CacheVersion).Append('\n');

                int guns = 0;

                foreach (var pair in _slots)
                {
                    var slots = pair.Value;
                    if (slots == null || slots.Count == 0) continue;
                    if (!slots[0].Probed) continue;   // 手写兜底的 8 槽不写

                    sb.Append("gun:").Append(pair.Key).Append('=');

                    for (int i = 0; i < slots.Count; i++)
                    {
                        if (i > 0) sb.Append(';');

                        var slot = slots[i];
                        sb.Append(slot.Role).Append('~');
                        if (slot.Tags != null) sb.Append(string.Join("|", slot.Tags));
                        sb.Append('~').Append(slot.RejectsAll ? "1" : "0");
                    }

                    sb.Append('\n');
                    guns++;
                }

                if (guns == 0) return;

                ModHelper.WriteModDataFileText(ModEntry.MOD_ID, CacheFile, sb.ToString());

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

            var mods = ModIds();
            var keep = new List<string>();

            for (int i = 0; i < raw.Count; i++)
            {
                string tag = raw[i];
                if (string.IsNullOrEmpty(tag)) continue;

                int matched = 0;
                for (int m = 0; m < mods.Count; m++)
                {
                    var entry = RecipeIndex.FindEntry(mods[m]);
                    var item = entry == null ? null : entry.Item;
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
            if (_modIds != null) return _modIds;

            var ids = new List<string>();
            foreach (var entry in RecipeIndex.AllEntries)
            {
                if (entry == null || entry.Unused || string.IsNullOrEmpty(entry.Id)) continue;
                if (!IsMod(entry.Item)) continue;

                ids.Add(entry.Id);
            }

            return _modIds = ids;
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

            foreach (var entry in RecipeIndex.AllEntries)
            {
                if (entry == null || entry.Unused || string.IsNullOrEmpty(entry.Id)) continue;

                var item = entry.Item;
                if (item == null || !IsMod(item)) continue;
                if (!Fits(item, gunId)) continue;
                if (!HasAnyTag(item, slot.Tags)) continue;
                if (!ids.Contains(entry.Id)) ids.Add(entry.Id);
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
