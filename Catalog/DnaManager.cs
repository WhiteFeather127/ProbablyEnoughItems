using System;
using System.Collections.Generic;

using ItemCatalog.UI;

namespace ItemCatalog.Catalog
{
    /// <summary>
    /// 植物 DNA 盘管理器（对应 <see cref="LiquidManager"/> 的角色）。
    ///
    /// 目录里只有一份**空白盘**（<c>plant_dna_disk</c>）；写入特质后的盘仍共用这个本体 id，
    /// 名字 / 颜色 / 剩余次数由游戏自己的 <c>PlantGeneticsHelper.UpdateDiskDisplay</c> 改写
    /// （化学=紫、特殊=蓝、负面=红、其余=绿、空白=黑）。因为 id 相同，
    /// **已写入的盘不进注册表条目，只由本管理器统一生成**（<see cref="DnaCatalog"/>），
    /// 每种盘与空白盘分开、各显示一次。
    /// </summary>
    internal static class DnaManager
    {
        internal const string DiskId = "plant_dna_disk";
        internal const string EntryPrefix = "dna:";

        /// <summary>全部未锁定的植物特质（不含空白盘），按 <c>PlantTraitRegistry.All</c> 的注册顺序。</summary>
        private static List<PlantTrait> _traits;

        /// <summary>特质 id → 能提供它的作物种子（机器页挑代表种子用）。</summary>
        private static readonly Dictionary<string, List<string>> _sources =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        /// <summary>来源缓存对应的目录代号；目录重建后要重算（可能有新模组作物）。</summary>
        private static int _sourcesGeneration = -1;

        internal static bool IsDnaEntry(string id)
        {
            return !string.IsNullOrEmpty(id) && id.StartsWith(EntryPrefix, StringComparison.Ordinal);
        }

        private static void EnsureTraits()
        {
            if (_traits != null) return;

            var list = new List<PlantTrait>();

            try
            {
                var all = PlantTraitRegistry.All;
                if (all != null)
                {
                    foreach (PlantTrait trait in all)
                    {
                        if (trait == null || string.IsNullOrEmpty(trait.id)) continue;

                        // 锁定的特质抽不出来、也写不进盘，不属于「存在的盘」
                        if (trait.locked) continue;

                        list.Add(trait);
                    }
                }
            }
            catch (Exception e)
            {
                if (ModEntry.Log != null)
                    ModEntry.Log.Warning("[dna] enumerate traits: " + e.Message);
            }

            _traits = list;

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[dna] written kinds=" + list.Count);
        }

        /// <summary>已写入盘的种类数（每种特质一只）。</summary>
        internal static int Count
        {
            get { EnsureTraits(); return _traits.Count; }
        }

        internal static PlantTrait TraitAt(int index)
        {
            EnsureTraits();
            if (_traits.Count == 0) return null;

            int i = ((index % _traits.Count) + _traits.Count) % _traits.Count;
            return _traits[i];
        }

        internal static string Label(PlantTrait trait)
        {
            return trait == null ? "空白" : trait.displayName;
        }

        internal static string EntryIdFor(PlantTrait trait)
        {
            return trait == null ? EntryPrefix + "blank" : EntryPrefix + trait.id;
        }

        /// <summary>管理器条目（dna:&lt;特质id&gt;）对应的特质；空白 / 未知返回 null。</summary>
        internal static PlantTrait TraitForEntry(string entryId)
        {
            if (!IsDnaEntry(entryId)) return null;

            string traitId = entryId.Substring(EntryPrefix.Length);
            if (string.IsNullOrEmpty(traitId) ||
                string.Equals(traitId, "blank", StringComparison.Ordinal)) return null;

            EnsureTraits();
            for (int i = 0; i < _traits.Count; i++)
            {
                var trait = _traits[i];
                if (trait != null && string.Equals(trait.id, traitId, StringComparison.Ordinal)) return trait;
            }

            return null;
        }

        /// <summary>特质在管理器里的序号（= 机器页的页码）；没有返回 -1。</summary>
        internal static int IndexOf(PlantTrait trait)
        {
            if (trait == null) return -1;

            EnsureTraits();
            for (int i = 0; i < _traits.Count; i++)
            {
                var t = _traits[i];
                if (t != null && string.Equals(t.id, trait.id, StringComparison.Ordinal)) return i;
            }

            return -1;
        }

        /// <summary>
        /// 能提供该特质的作物种子（先天带这个特质，或游戏判定可兼容）。
        /// 只用来给机器页挑一个代表性种子，不再单独出「来源」页。
        /// </summary>
        internal static List<string> Sources(PlantTrait trait)
        {
            var empty = new List<string>();
            if (trait == null) return empty;

            if (_sourcesGeneration != CatalogDataBuilder.Generation)
            {
                _sources.Clear();
                _sourcesGeneration = CatalogDataBuilder.Generation;
            }

            List<string> cached;
            if (_sources.TryGetValue(trait.id, out cached)) return cached;

            var list = new List<string>();
            var items = CatalogDataBuilder.Visible;

            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var entry = items[i];
                    var item = entry == null ? null : entry.Item;
                    if (item == null || string.IsNullOrEmpty(entry.Id)) continue;

                    bool seed = false;
                    try { seed = item.IsTag("SEED_ITEM_TAG"); }
                    catch { }
                    if (!seed) continue;

                    PlantDefinition plant = null;
                    try { plant = HydroponicHelper.GetPlant(item); }
                    catch { }
                    if (plant == null) continue;

                    bool compatible = false;
                    try { compatible = plant.GetInnateRank(trait.id) > 0; }
                    catch { }

                    if (!compatible)
                    {
                        try
                        {
                            string reason;
                            compatible = PlantGeneticsHelper.IsTraitCompatible(plant, trait, out reason);
                        }
                        catch { }
                    }

                    if (compatible) list.Add(entry.Id);
                }
            }

            _sources[trait.id] = list;
            return list;
        }

        /// <summary>空白盘（目录条目 plant_dna_disk 本体）。</summary>
        internal static GameItem CreateBlankDisk()
        {
            return CreateDisk((PlantTrait)null);
        }

        /// <summary>造一只写入指定特质的盘：1 级、可用 1 次；trait 为 null 时是空白盘。</summary>
        internal static GameItem CreateDisk(PlantTrait trait)
        {
            return CatalogWindow.CreatePreviewItem(DiskId, delegate
            {
                var item = DirectoryMaster.Item(DiskId, true);
                if (item == null) return null;

                if (trait != null)
                {
                    try { PlantGeneticsHelper.WriteDisk(item, trait, 1, 1); }
                    catch (Exception e)
                    {
                        if (ModEntry.Log != null)
                            ModEntry.Log.Warning("[dna] write '" + trait.id + "': " + e.Message);
                    }
                }

                return item;
            });
        }
    }
}
