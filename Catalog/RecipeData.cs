using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace ItemCatalog.Catalog
{
    /// <summary>配方里的一个成分（材料或工具）。</summary>
    internal sealed class RecipeIngredient
    {
        internal string DisplayName;      // 游戏给的显示名（可能是物品名，也可能是分类名）
        internal int Amount;

        /// <summary>精确 id 集合（CraftingIdentifierFilter）。为空表示不按 id 匹配。</summary>
        internal List<string> Ids;

        /// <summary>标签集合（CraftingTagsFilter）。为空表示不按标签匹配。</summary>
        internal List<string> Tags;

        /// <summary>true = 工具（不消耗）；false = 材料（消耗）。</summary>
        internal bool IsTool;

        /// <summary>既没有 id 也没有标签（纯委托过滤器），只能显示名字。</summary>
        internal bool Opaque
        {
            get
            {
                return (Ids == null || Ids.Count == 0) && (Tags == null || Tags.Count == 0);
            }
        }

        /// <summary>这一条是否接受给定物品（按 id 或标签）。</summary>
        internal bool Accepts(GameItem item, string id)
        {
            if (Ids != null && Ids.Count > 0)
            {
                for (int i = 0; i < Ids.Count; i++)
                {
                    if (string.Equals(Ids[i], id, StringComparison.Ordinal)) return true;
                }
            }

            if (Tags != null && Tags.Count > 0 && item != null)
            {
                for (int i = 0; i < Tags.Count; i++)
                {
                    try { if (item.IsTag(Tags[i])) return true; }
                    catch { }
                }
            }

            return false;
        }
    }

    /// <summary>配方产物。</summary>
    internal sealed class RecipeOutput
    {
        internal string Id;
        internal int Amount;
    }

    /// <summary>一条配方：属于某台机器 / 某张蓝图。</summary>
    internal sealed class RecipeInfo
    {
        internal string MachineId;        // 配方来源物品（合成台 / 蓝图 / 贩卖机）
        internal string MachineName;
        internal string Name;             // 配方自带的 customName，可能为空

        internal List<RecipeIngredient> Tools = new List<RecipeIngredient>();
        internal List<RecipeIngredient> Materials = new List<RecipeIngredient>();
        internal List<RecipeOutput> Outputs = new List<RecipeOutput>();

        /// <summary>产物 id 的缩写，用于索引与日志。</summary>
        internal string FirstOutputId
        {
            get { return Outputs.Count == 0 ? null : Outputs[0].Id; }
        }
    }

    /// <summary>
    /// 全量配方索引：从「带 recipeManager 的物品」出发，读出它（及其子物品）的全部配方。
    ///
    /// 游戏侧的结构（反编译确认）：
    ///   GameItem.recipeManager             → RecipeManager
    ///   RecipeManager.availableRecipes     → List&lt;CraftingRecipe&gt;
    ///   CraftingRecipe.requiredTools       → List&lt;CraftingFilter&gt;
    ///   CraftingRecipe.requiredMaterials   → List&lt;CraftingFilter&gt;
    ///   CraftingRecipe.craftFunc           → 大多数是 SetCraftingConstantFunc 生成的闭包，
    ///                                        Target 上有 itemIdentifier / amount 两个字段
    ///   GameItem.FindAllRecipes()          → 自身 + 子物品的配方合并（游戏自己也是这么枚举的）
    ///
    /// 产物 id 优先读闭包字段（无副作用）；读不到才退回 CraftWithoutMaterials()。
    /// </summary>
    internal static class RecipeIndex
    {
        internal static readonly List<RecipeInfo> All = new List<RecipeInfo>();

        /// <summary>产物 id → 能产出它的配方。</summary>
        private static readonly Dictionary<string, List<RecipeInfo>> _byOutput =
            new Dictionary<string, List<RecipeInfo>>(StringComparer.Ordinal);

        /// <summary>精确材料 id → 用到它的配方。</summary>
        private static readonly Dictionary<string, List<RecipeInfo>> _byMaterial =
            new Dictionary<string, List<RecipeInfo>>(StringComparer.Ordinal);

        /// <summary>标签材料 → 用它的配方（查询时再按物品标签匹配）。</summary>
        private static readonly List<KeyValuePair<string, RecipeInfo>> _byMaterialTag =
            new List<KeyValuePair<string, RecipeInfo>>();

        /// <summary>条目 id → 条目（查询时用来取图标 / 显示名）。</summary>
        private static readonly Dictionary<string, ItemEntry> _entryById =
            new Dictionary<string, ItemEntry>(StringComparer.Ordinal);

        internal static int MachineCount { get; private set; }
        internal static int UnresolvedOutputs { get; private set; }
        internal static bool Built { get; private set; }

        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>建索引。重复调用只会重建一次（Generation 变化时由调用方清空 Built）。</summary>
        internal static void Build()
        {
            if (Built) return;

            All.Clear();
            _byOutput.Clear();
            _byMaterial.Clear();
            _byMaterialTag.Clear();
            _entryById.Clear();

            MachineCount = 0;
            UnresolvedOutputs = 0;

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var seenRecipes = new HashSet<CraftingRecipe>();

            var items = CatalogDataBuilder.All;
            for (int i = 0; i < items.Count; i++)
            {
                var entry = items[i];
                if (entry == null || entry.Item == null) continue;
                if (entryByIdMissing(entry)) _entryById[entry.Id] = entry;

                RecipeManager manager;
                try { manager = entry.Item.recipeManager; }
                catch { manager = null; }
                if (manager == null) continue;

                List<CraftingRecipe> recipes;
                try { recipes = entry.Item.FindAllRecipes(); }
                catch (Exception e) { Warn("find recipes of '" + entry.Id + "': " + e.Message); continue; }
                if (recipes == null || recipes.Count == 0) continue;

                MachineCount++;

                for (int r = 0; r < recipes.Count; r++)
                {
                    var recipe = recipes[r];
                    if (recipe == null) continue;
                    if (!seenRecipes.Add(recipe)) continue;   // 子物品可能被多个父物品枚举到

                    var info = ReadRecipe(recipe, entry);
                    if (info == null) continue;

                    All.Add(info);
                    IndexRecipe(info);
                }
            }

            watch.Stop();
            Built = true;

            if (ModEntry.Log != null)
            {
                ModEntry.Log.Log(string.Format(
                    "[recipes] machines={0} recipes={1} outputs={2} unresolved={3} ({4} ms)",
                    MachineCount, All.Count, _byOutput.Count, UnresolvedOutputs, watch.ElapsedMilliseconds));
            }
        }

        private static bool entryByIdMissing(ItemEntry entry)
        {
            return !string.IsNullOrEmpty(entry.Id) && !_entryById.ContainsKey(entry.Id);
        }

        /// <summary>把一条配方登记进两张反查表。</summary>
        private static void IndexRecipe(RecipeInfo info)
        {
            for (int i = 0; i < info.Outputs.Count; i++)
            {
                AddTo(_byOutput, info.Outputs[i].Id, info);
            }

            for (int i = 0; i < info.Materials.Count; i++)
            {
                var ing = info.Materials[i];
                if (ing == null) continue;

                if (ing.Ids != null)
                {
                    for (int k = 0; k < ing.Ids.Count; k++) AddTo(_byMaterial, ing.Ids[k], info);
                }

                if (ing.Tags != null)
                {
                    for (int k = 0; k < ing.Tags.Count; k++)
                        _byMaterialTag.Add(new KeyValuePair<string, RecipeInfo>(ing.Tags[k], info));
                }
            }
        }

        private static void AddTo(Dictionary<string, List<RecipeInfo>> map, string key, RecipeInfo info)
        {
            if (string.IsNullOrEmpty(key)) return;

            List<RecipeInfo> list;
            if (!map.TryGetValue(key, out list))
            {
                list = new List<RecipeInfo>();
                map[key] = list;
            }

            if (!list.Contains(info)) list.Add(info);
        }

        // ---------------------------------------------------------------- 查询

        /// <summary>能产出这件物品的配方。</summary>
        internal static List<RecipeInfo> Producing(string id)
        {
            Build();

            List<RecipeInfo> list;
            if (string.IsNullOrEmpty(id) || !_byOutput.TryGetValue(id, out list)) return new List<RecipeInfo>();
            return list;
        }

        /// <summary>把这件物品当材料用的配方（精确 id + 标签匹配）。</summary>
        internal static List<RecipeInfo> Consuming(string id, GameItem item)
        {
            Build();

            var result = new List<RecipeInfo>();

            List<RecipeInfo> byId;
            if (!string.IsNullOrEmpty(id) && _byMaterial.TryGetValue(id, out byId)) result.AddRange(byId);

            for (int i = 0; i < _byMaterialTag.Count; i++)
            {
                var pair = _byMaterialTag[i];
                if (result.Contains(pair.Value)) continue;

                bool match = false;
                if (item != null)
                {
                    try { match = item.IsTag(pair.Key); } catch { }
                }

                if (match) result.Add(pair.Value);
            }

            return result;
        }

        internal static ItemEntry FindEntry(string id)
        {
            Build();

            ItemEntry entry;
            return id != null && _entryById.TryGetValue(id, out entry) ? entry : null;
        }

        /// <summary>全量条目（找钥匙卡这类「按标签扫一遍」的用途）。</summary>
        internal static IEnumerable<ItemEntry> AllEntries
        {
            get
            {
                Build();
                return _entryById.Values;
            }
        }

        /// <summary>数据表重建后调用，下次查询前重新建索引。</summary>
        internal static void Invalidate()
        {
            Built = false;
        }

        // ---------------------------------------------------------------- 读取

        private static RecipeInfo ReadRecipe(CraftingRecipe recipe, ItemEntry machine)
        {
            var info = new RecipeInfo
            {
                MachineId = machine.Id,
                MachineName = machine.DisplayName
            };

            try { info.Name = recipe.customName; }
            catch { }

            try
            {
                var tools = recipe.requiredTools;
                if (tools != null)
                {
                    for (int i = 0; i < tools.Count; i++) AddIngredient(info.Tools, tools[i], true);
                }
            }
            catch (Exception e) { Warn("read tools of '" + machine.Id + "': " + e.Message); }

            try
            {
                var materials = recipe.requiredMaterials;
                if (materials != null)
                {
                    for (int i = 0; i < materials.Count; i++) AddIngredient(info.Materials, materials[i], false);
                }
            }
            catch (Exception e) { Warn("read materials of '" + machine.Id + "': " + e.Message); }

            ReadOutputs(recipe, info);

            // 既没材料也没产物 → 不是可展示的配方
            if (info.Materials.Count == 0 && info.Outputs.Count == 0) return null;

            return info;
        }

        private static void AddIngredient(List<RecipeIngredient> into, CraftingFilter filter, bool isTool)
        {
            if (filter == null || into == null) return;

            var ing = new RecipeIngredient { IsTool = isTool };

            try { ing.DisplayName = filter.displayName; } catch { }

            try { ing.Amount = filter.amount; } catch { ing.Amount = 1; }
            if (ing.Amount <= 0) ing.Amount = 1;

            var tagged = filter as CraftingTagsFilter;
            if (tagged != null)
            {
                ing.Tags = new List<string>();
                try
                {
                    var set = tagged.tagFilter;
                    if (set != null)
                        foreach (var tag in set)
                            if (!string.IsNullOrEmpty(tag)) ing.Tags.Add(tag);
                }
                catch { }
            }

            var identified = filter as CraftingIdentifierFilter;
            if (identified != null)
            {
                ing.Ids = new List<string>();
                try
                {
                    var set = identified.identifierFilter;
                    if (set != null)
                        foreach (var id in set)
                            if (!string.IsNullOrEmpty(id)) ing.Ids.Add(id);
                }
                catch { }
            }

            into.Add(ing);
        }

        /// <summary>
        /// 读产物：优先反射 craftFunc 闭包上的 itemIdentifier / amount（无副作用），
        /// 读不到再退回 CraftWithoutMaterials()（会真的造一次物品，但只用于兜底）。
        /// </summary>
        private static void ReadOutputs(CraftingRecipe recipe, RecipeInfo info)
        {
            var func = recipe.craftFunc;
            if (func != null)
            {
                var target = func.Target;
                if (target != null)
                {
                    string id = null;
                    int amount = 1;

                    // craftFunc 的闭包类型是编译器生成的，写不出字面类型，
                    // 所以交给 ModReflect（由游戏替你查找并检查目标）。
                    // 以前是 `target.GetType().GetField(...)`，属于「运行时才知道的类型」，
                    // 扫描器判成 reflection-external，2026-12-10 起会拒绝加载模组。
                    try { id = ModReflect.Get<string>(target, "itemIdentifier"); }
                    catch { }

                    try { amount = ModReflect.Get<int>(target, "amount"); }
                    catch { }

                    if (!string.IsNullOrEmpty(id))
                    {
                        info.Outputs.Add(new RecipeOutput { Id = id, Amount = Math.Max(1, amount) });
                        return;
                    }
                }
            }

            // 兜底：真的跑一次「无材料合成」，只取 identifier / 数量
            try
            {
                var produced = recipe.CraftWithoutMaterials();
                if (produced != null)
                {
                    for (int i = 0; i < produced.Count && i < 4; i++)
                    {
                        var item = produced[i];
                        if (item == null || string.IsNullOrEmpty(item.identifier)) continue;

                        info.Outputs.Add(new RecipeOutput
                        {
                            Id = item.identifier,
                            Amount = Math.Max(1, item.unitCount)
                        });
                    }
                }
            }
            catch (Exception e)
            {
                Warn("craft without materials: " + e.Message);
            }

            if (info.Outputs.Count == 0) UnresolvedOutputs++;
        }

        /// <summary>把索引摘要打进日志（F9 调试用）。</summary>
        internal static void Dump()
        {
            Build();

            if (ModEntry.Log == null) return;

            ModEntry.Log.Log(string.Format("[recipes] machines={0} recipes={1} outputs={2} unresolved={3}",
                MachineCount, All.Count, _byOutput.Count, UnresolvedOutputs));

            int shown = Math.Min(20, All.Count);
            for (int i = 0; i < shown; i++)
            {
                var r = All[i];

                string mats = "";
                for (int k = 0; k < r.Materials.Count; k++)
                {
                    var ing = r.Materials[k];
                    if (ing == null) continue;
                    if (k > 0) mats += " + ";
                    mats += (ing.DisplayName ?? (ing.Ids != null && ing.Ids.Count > 0 ? ing.Ids[0] : "?")) + "×" + ing.Amount;
                }

                string outs = "";
                for (int k = 0; k < r.Outputs.Count; k++)
                {
                    if (k > 0) outs += " + ";
                    outs += r.Outputs[k].Id + "×" + r.Outputs[k].Amount;
                }

                ModEntry.Log.Log("[recipes] " + r.MachineId + " : [" + mats + "] -> [" + outs + "]");
            }
        }

        private static void Warn(string message)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("[recipes] " + message);
        }
    }
}
