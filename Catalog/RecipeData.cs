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

    /// <summary>
    /// 一条配方里某个参与物品的角色。显示位置由角色决定：
    ///   Tool     → 右上角（机器 / 工作台）
    ///   Material → 材料区（手持工具也归这一类）
    ///   Output   → 产物区
    ///   Module   → 模块，按槽位分组（机器模块迁移时再加 SlotId）
    /// </summary>
    internal enum PartRole
    {
        Tool = 0,
        Material = 1,
        Output = 2,
        Module = 3
    }

    /// <summary>配方里的一个参与物品（拍平后的「配方 × 物品」一行）。</summary>
    internal sealed class ItemPart
    {
        internal PartRole Role;

        /// <summary>单个 id（机器 / 产物）。材料可能有多件候选，见 <see cref="Ids"/>。</summary>
        internal string Id;
        internal List<string> Ids;
        internal List<string> Tags;

        internal string DisplayName;
        internal int Amount = 1;

        /// <summary>手持工具（不消耗）；角色仍是 <see cref="PartRole.Material"/>。</summary>
        internal bool IsTool;

        /// <summary>槽位 id（<see cref="PartRole.Module"/> 用）：module / chip / container / dispenser / butcher…</summary>
        internal string SlotId;
    }

    /// <summary>一条配方：属于某台机器 / 某张蓝图。</summary>
    internal sealed class RecipeInfo
    {
        internal string MachineId;        // 配方来源物品（合成台 / 蓝图 / 贩卖机）
        internal string MachineName;
        internal string Name;             // 配方自带的 customName，可能为空

        /// <summary>手持工具。现在也同时出现在 <see cref="Materials"/> 里（按角色算作 Material）。</summary>
        internal List<RecipeIngredient> Tools = new List<RecipeIngredient>();
        internal List<RecipeIngredient> Materials = new List<RecipeIngredient>();
        internal List<RecipeOutput> Outputs = new List<RecipeOutput>();

        /// <summary>
        /// 拍平后的角色视图：机器（Tool）+ 材料（含手持工具）+ 产物。
        /// 与上面三个列表共享同一批对象，只多一层角色标注，供统一查询 / 缓存使用。
        /// </summary>
        internal List<ItemPart> Parts = new List<ItemPart>();

        /// <summary>产物 id 的缩写，用于索引与日志。</summary>
        internal string FirstOutputId
        {
            get { return Outputs.Count == 0 ? null : Outputs[0].Id; }
        }

        /// <summary>
        /// 这一份副本按哪个作物、哪一档品质展示（-1 = 不指定）。
        /// 作物有品质档后，一条配方会按档复制几份，材料/产物格子分别显示对应档的实例。
        /// </summary>
        internal string DisplayCropId;
        internal int DisplayCropVariant = -1;

        /// <summary>复制一份，只为指定作物的某一品质档标记展示档位（材料/产物列表共享）。</summary>
        internal RecipeInfo CopyForCrop(string cropId, int variant)
        {
            return new RecipeInfo
            {
                MachineId = MachineId,
                MachineName = MachineName,
                Name = Name,
                Tools = Tools,
                Materials = Materials,
                Outputs = Outputs,
                Parts = Parts,
                DisplayCropId = cropId,
                DisplayCropVariant = variant
            };
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

        /// <summary>机器 id → 它自己的产出配方（机器详情页用）。</summary>
        private static readonly Dictionary<string, List<RecipeInfo>> _byMachine =
            new Dictionary<string, List<RecipeInfo>>(StringComparer.Ordinal);

        internal static int MachineCount { get; private set; }
        internal static int UnresolvedOutputs { get; private set; }
        internal static bool Built { get; private set; }

        /// <summary>本索引在 <see cref="IndexManager"/> 里的名字 / 建好后的占位值。</summary>
        private const string IndexName = "recipes";
        private static readonly object IndexMarker = new object();

        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>空扫描后的重试时间点（unscaled）。</summary>
        private static float _retryAt;

        /// <summary>重试退避间隔：10 → 20 → 40 → 60 封顶（卡顿保护）。</summary>
        private static float _retryDelay = 10f;

        /// <summary>本次扫描中访问物品字段抛异常的次数（实例被销毁的信号）。</summary>
        private static int _scanThrows;

        /// <summary>建索引；缓存与失效交给 <see cref="IndexManager"/>（目录 generation / 环境签名）。</summary>
        internal static void Build()
        {
            IndexManager.Get(IndexName, BuildCore);
        }

        private static object BuildCore()
        {
            if (UnityEngine.Time.unscaledTime < _retryAt) return null;

            // 目录还没就绪（All 为空）时绝不缓存结果：任何来源层都可能通过
            // BaseItemId → FindEntry 提前触发本次构建，此时缓存 0 条配方
            // 会把合成/机器页全部锁死。不设 Built，等目录就绪后重扫。
            if (CatalogDataBuilder.All.Count == 0)
            {
                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[recipes] catalog not ready, deferred");
                _retryAt = UnityEngine.Time.unscaledTime + _retryDelay;
                _retryDelay = UnityEngine.Mathf.Min(_retryDelay * 2f, 60f);
                return null;
            }

            All.Clear();
            _byOutput.Clear();
            _byMaterial.Clear();
            _byMaterialTag.Clear();
            _entryById.Clear();
            _byMachine.Clear();
            _scanThrows = 0;

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

                // 读档会重建 DirectoryMaster 的物品实例：条目里缓存的 GameItem
                // 可能已被游戏销毁（Unity 对象销毁后访问字段抛 MissingReference）。
                // 只有实例真的被销毁时才按 id 重新解析——DirectoryMaster.Item
                // 带创建副作用（所有权注册 / uniqueId），对 516 个条目盲调会卡死游戏。
                RecipeManager manager = null;
                bool destroyed = false;
                try { manager = entry.Item.recipeManager; }
                catch { destroyed = true; _scanThrows++; }

                if (destroyed)
                {
                    try
                    {
                        var fresh = DirectoryMaster.Item(entry.Id);
                        if (fresh != null) manager = fresh.recipeManager;
                    }
                    catch { }
                }

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

            // 游戏的 recipeManager 只由 CraftingHelper.InitCraftingTable /
            // InitCraftingBlueprint / InitVendingTable 挂上——**进存档之后**才存在。
            // 主菜单暖机时扫到 0 台机器是正常状态，绝不能缓存：
            // 不设 Built，10 秒冷却后由下一次 FindEntry/查询自动重扫，
            // 进存档后第一次查询就会拿到完整配方。
            if (MachineCount == 0)
            {
                _retryAt = UnityEngine.Time.unscaledTime + _retryDelay;
                _retryDelay = UnityEngine.Mathf.Min(_retryDelay * 2f, 60f);

                if (ModEntry.Log != null)
                    ModEntry.Log.Log(string.Format(
                        "[recipes] no machines yet (recipeManager attaches after save load), deferred"
                        + " [all={0} withItem={1} throws={2}] retry in {3:0}s",
                        items.Count, CountWithItem(items), _scanThrows, _retryDelay));
                return null;
            }

            Built = true;

            if (ModEntry.Log != null)
            {
                // 诊断三件套：目录条目数 / 带 GameItem 的 / 带 recipeManager 的——
                // machines=0 时一眼看出断在哪一层
                int withItem = 0, withManager = 0;
                for (int i = 0; i < items.Count; i++)
                {
                    var e = items[i];
                    if (e == null || e.Item == null) continue;
                    withItem++;
                    RecipeManager m = null;
                    try { m = e.Item.recipeManager; } catch { }
                    if (m != null) withManager++;
                }

                ModEntry.Log.Log(string.Format(
                    "[recipes] machines={0} recipes={1} outputs={2} unresolved={3} ({4} ms)"
                    + " [all={5} withItem={6} withManager={7} scanThrows={8}]",
                    MachineCount, All.Count, _byOutput.Count, UnresolvedOutputs, watch.ElapsedMilliseconds,
                    items.Count, withItem, withManager, _scanThrows));
            }

            return IndexMarker;
        }

        private static int CountWithItem(System.Collections.Generic.List<ItemEntry> items)
        {
            int n = 0;
            for (int i = 0; i < items.Count; i++)
                if (items[i] != null && items[i].Item != null) n++;
            return n;
        }

        private static bool entryByIdMissing(ItemEntry entry)
        {
            return !string.IsNullOrEmpty(entry.Id) && !_entryById.ContainsKey(entry.Id);
        }

        /// <summary>把一条配方登记进两张反查表。</summary>
        private static void IndexRecipe(RecipeInfo info)
        {
            // 机器自己的产出页：MachineId 就是那台机器的物品 id
            if (!string.IsNullOrEmpty(info.MachineId)) AddTo(_byMachine, info.MachineId, info);

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

        /// <summary>这台机器（或蓝图 / 贩卖机）自己的全部配方，按登记顺序。</summary>
        internal static List<RecipeInfo> ByMachine(string machineId)
        {
            Build();

            List<RecipeInfo> list;
            return machineId != null && _byMachine.TryGetValue(machineId, out list)
                ? list
                : new List<RecipeInfo>();
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
            if (id != null && _entryById.TryGetValue(id, out entry)) return entry;

            // 液体条目（liquid:xxx）/ DNA 盘条目（dna:xxx）是搜索时动态生成的合成条目
            return LiquidCatalog.Find(id) ?? DnaCatalog.Find(id);
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
            IndexManager.Invalidate(IndexName);
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

            BuildParts(info);
            return info;
        }

        /// <summary>
        /// 生成拍平后的角色视图：机器（Tool）+ 材料（含手持工具）+ 产物。
        /// 与 Materials / Outputs 共享同一批对象，只多一层角色标注。
        /// </summary>
        private static void BuildParts(RecipeInfo info)
        {
            info.Parts.Clear();

            if (!string.IsNullOrEmpty(info.MachineId))
            {
                info.Parts.Add(new ItemPart
                {
                    Role = PartRole.Tool,
                    Id = info.MachineId,
                    DisplayName = info.MachineName,
                    Amount = 1
                });
            }

            for (int i = 0; i < info.Materials.Count; i++)
            {
                var ing = info.Materials[i];
                if (ing == null) continue;

                info.Parts.Add(new ItemPart
                {
                    Role = PartRole.Material,
                    Ids = ing.Ids,
                    Tags = ing.Tags,
                    DisplayName = ing.DisplayName,
                    Amount = ing.Amount,
                    IsTool = ing.IsTool
                });
            }

            // 手持工具：配方要求，不是消耗材料；单独标 IsTool，不进 Materials / 材料索引
            for (int i = 0; i < info.Tools.Count; i++)
            {
                var ing = info.Tools[i];
                if (ing == null) continue;

                info.Parts.Add(new ItemPart
                {
                    Role = PartRole.Material,
                    Ids = ing.Ids,
                    Tags = ing.Tags,
                    DisplayName = ing.DisplayName,
                    Amount = ing.Amount,
                    IsTool = true
                });
            }

            for (int i = 0; i < info.Outputs.Count; i++)
            {
                var output = info.Outputs[i];
                if (output == null) continue;

                info.Parts.Add(new ItemPart
                {
                    Role = PartRole.Output,
                    Id = output.Id,
                    Amount = Math.Max(1, output.Amount)
                });
            }
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
