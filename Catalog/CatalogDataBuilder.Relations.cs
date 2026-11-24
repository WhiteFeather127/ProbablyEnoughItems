using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ItemCatalog.Catalog
{
    /// <summary>统一关系索引里一条关系的来源类型。</summary>
    internal enum ItemRelationKind
    {
        Client = 0,
        Location = 1,
        Loot = 2,
        Recipe = 3,
        Use = 4,
        Upgrade = 5,
        Machine = 6,
        Gun = 7
    }

    /// <summary>
    /// 统一关系索引里的一条命中：某个物品在某条关系里扮演一个角色。
    /// <see cref="Part"/> 的角色决定它是来源（Output）还是用途（Material / Tool / Module）。
    /// 这是查询时临时生成的视图；缓存里存的是关系本体（<see cref="RelationRecord"/>）。
    /// </summary>
    internal sealed class ItemRelationHit
    {
        internal ItemRelationKind Kind;
        internal string ProviderId;   // 客人 / 地点 / 掉落表 / 机器 / 升级 id
        internal string Label;        // 显示名（日志 / 调试）
        internal ItemPart Part;
        internal object Payload;      // SourceInfo / LootTableInfo / RecipeInfo / UseInfo / UpgradeInfo / MachinePanels.Recipe

        internal bool IsUse
        {
            get { return Part != null && Part.Role != PartRole.Output; }
        }
    }

    /// <summary>
    /// 总表构建的一部分：把所有来源 / 用途数据统一登记成关系记录，
    /// 再建一张「物品 → 关系 id」的索引。
    ///
    ///   RelationRecord { Kind, ProviderId, Parts[] }
    ///   ByItem: 物品 id → 关系 id 列表
    ///
    /// 关系本体只存一份；同一件物品出现在多条关系里时，索引里只有关系 id。
    /// 物品是 <see cref="PartRole.Output"/> → 来源方向命中；其余角色 → 用途方向命中。
    /// 缓存与失效由 <see cref="IndexManager"/> 负责（总缓存里的 <c>item-relations</c> 分段）。
    /// </summary>
    internal static partial class CatalogDataBuilder
    {
        private const string RelationIndexName = "item-relations";

        /// <summary>一条关系本体（客人的一份货单 / 一张掉落表 / 一条配方 / 一条枪槽…）。</summary>
        internal sealed class RelationRecord
        {
            internal string Id;           // 内容哈希：所有列表 + 角色 / 槽位 / 数量算出来的稳定 id
            internal ItemRelationKind Kind;
            internal string ProviderId;
            internal string Label;
            internal List<ItemPart> Parts = new List<ItemPart>();
            internal object Payload;
        }

        internal sealed class RelationIndexData
        {
            /// <summary>关系 id（内容哈希）→ 关系本体。</summary>
            internal readonly Dictionary<string, RelationRecord> Records =
                new Dictionary<string, RelationRecord>(StringComparer.Ordinal);

            /// <summary>物品 id → 关系 id（内容哈希）列表。</summary>
            internal readonly Dictionary<string, List<string>> ByItem =
                new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }

        private static bool _deferredCatalogLogged;
        private static bool _deferredLootLogged;

        /// <summary>
        /// 建一次完整的关系总表并写进总缓存（<c>item-relations</c> 分段）。
        /// 加载完成、掉落表就绪、打开图鉴三处都会调，由 <see cref="IndexManager"/> 去重，
        /// 同一 generation 只建一遍；数据没齐时返回 null，不写半份表。
        /// </summary>
        internal static void BuildRelations()
        {
            // 机器配方构建会读物品实例上的液体成分标签（动态机器按 generation 缓存），
            // 必须先补标签，否则机器关系里的液体产物 / 成分就是空的。
            try { LiquidManager.TagItems(); }
            catch (Exception e) { WarnRelation("liquid tags: " + e.Message); }

            var data = IndexManager.Get(RelationIndexName,
                                        BuildRelationCore,
                                        SerializeRelations,
                                        ParseRelations);

            BindPayloads(data);
        }

        /// <summary>
        /// 关系表从总缓存加载时 Payload 是空的（不落盘）：按 Kind 把运行时对象重新绑回去。
        /// 详情窗只认 Payload，所以每次取到表之后都要补一次；已经绑过就跳过。
        /// </summary>
        private static void BindPayloads(RelationIndexData data)
        {
            if (data == null || data.Records == null || data.Records.Count == 0) return;

            bool missing = false;
            foreach (var record in data.Records.Values)
            {
                if (record != null && record.Payload == null) { missing = true; break; }
            }
            if (!missing) return;

            try
            {
                var sources = SourceIndex.AllSources;
                for (int i = 0; i < sources.Count; i++)
                {
                    var source = sources[i];
                    if (source == null) continue;

                    var kind = source.Kind == SourceKind.Client
                        ? ItemRelationKind.Client
                        : ItemRelationKind.Location;

                    Bind(data, ComputeRelationId(kind, source.SourceId, source.Parts), source);
                }
            }
            catch (Exception e) { WarnRelation("bind sources: " + e.Message); }

            try
            {
                var tables = LootSource.Tables;
                if (tables != null)
                {
                    for (int i = 0; i < tables.Length; i++)
                    {
                        var table = tables[i];
                        if (table == null) continue;

                        Bind(data, ComputeRelationId(ItemRelationKind.Loot, table.TableId, table.Parts), table);
                    }
                }
            }
            catch (Exception e) { WarnRelation("bind loot: " + e.Message); }

            try
            {
                var recipes = RecipeIndex.All;
                for (int i = 0; i < recipes.Count; i++)
                {
                    var recipe = recipes[i];
                    if (recipe == null) continue;

                    Bind(data, ComputeRelationId(ItemRelationKind.Recipe, recipe.MachineId, recipe.Parts), recipe);
                }
            }
            catch (Exception e) { WarnRelation("bind recipes: " + e.Message); }

            try
            {
                var machineRecipes = ItemCatalog.UI.MachinePanels.AllRecipes();
                for (int i = 0; i < machineRecipes.Count; i++)
                {
                    var pair = machineRecipes[i];
                    var parts = MachineRelationParts(pair.Key, pair.Value);

                    Bind(data, ComputeRelationId(ItemRelationKind.Machine, pair.Key, parts), pair.Value);
                }
            }
            catch (Exception e) { WarnRelation("bind machines: " + e.Message); }

            try
            {
                var uses = UseIndex.AllRecords();
                for (int i = 0; i < uses.Count; i++)
                {
                    var use = uses[i];
                    if (use == null) continue;

                    Bind(data, ComputeRelationId(ItemRelationKind.Use, use.Kind, UseRelationParts(use)), use);
                }
            }
            catch (Exception e) { WarnRelation("bind uses: " + e.Message); }

            try
            {
                var upgrades = UpgradeCatalog.AllRecords();
                for (int i = 0; i < upgrades.Count; i++)
                {
                    var upgrade = upgrades[i];
                    if (upgrade == null) continue;

                    Bind(data, ComputeRelationId(ItemRelationKind.Upgrade, upgrade.Id, upgrade.Parts), upgrade);
                }
            }
            catch (Exception e) { WarnRelation("bind upgrades: " + e.Message); }

            try
            {
                foreach (var record in data.Records.Values)
                {
                    if (record == null || record.Payload != null) continue;
                    if (record.Kind != ItemRelationKind.Gun || record.Parts == null) continue;

                    string role = null;
                    for (int p = 0; p < record.Parts.Count; p++)
                    {
                        var part = record.Parts[p];
                        if (part != null && part.Role == PartRole.Tool) role = part.SlotId;
                    }
                    if (string.IsNullOrEmpty(role)) continue;

                    var slots = ItemCatalog.UI.GunMods.SlotsFor(record.ProviderId);
                    for (int s = 0; s < slots.Count; s++)
                    {
                        if (slots[s] != null && string.Equals(slots[s].Role, role, StringComparison.Ordinal))
                        {
                            record.Payload = slots[s];
                            break;
                        }
                    }
                }
            }
            catch (Exception e) { WarnRelation("bind guns: " + e.Message); }
        }

        private static void Bind(RelationIndexData data, string id, object payload)
        {
            if (string.IsNullOrEmpty(id) || payload == null) return;

            RelationRecord record;
            if (data.Records.TryGetValue(id, out record) && record != null && record.Payload == null)
                record.Payload = payload;
        }

        /// <summary>
        /// 使用关系在关系表里的 parts：dissolve / contain 只挂在液体条目（产物侧）名下，
        /// 与 UseIndex 原来的登记口径一致；否则容器物品的用途页会多出「装着某种液体」。
        /// </summary>
        private static List<ItemPart> UseRelationParts(UseInfo use)
        {
            if (use == null) return null;

            var parts = use.Parts;
            if (parts == null) return null;

            if (!string.Equals(use.Kind, "dissolve", StringComparison.Ordinal) &&
                !string.Equals(use.Kind, "contain", StringComparison.Ordinal))
            {
                return parts;
            }

            var filtered = new List<ItemPart>();
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part != null && part.Role == PartRole.Output) filtered.Add(part);
            }

            return filtered;
        }

        /// <summary>枪槽探测补上了新的枪：让关系表在下次触发时重建。</summary>
        internal static void InvalidateRelations()
        {
            IndexManager.Invalidate(RelationIndexName);
        }

        /// <summary>能产出这件物品的关系（来源方向）。</summary>
        internal static List<ItemRelationHit> RelationSources(string itemId)
        {
            return LookupRelation(itemId, false);
        }

        /// <summary>把这件物品当投入 / 工具 / 模块使用的关系（用途方向）。</summary>
        internal static List<ItemRelationHit> RelationUses(string itemId)
        {
            return LookupRelation(itemId, true);
        }

        private static List<ItemRelationHit> LookupRelation(string itemId, bool wantUse)
        {
            var data = IndexManager.Get(RelationIndexName, BuildRelationCore);
            if (data == null || string.IsNullOrEmpty(itemId)) return new List<ItemRelationHit>();

            List<string> recordIds;
            if (!data.ByItem.TryGetValue(itemId, out recordIds) || recordIds == null)
                return new List<ItemRelationHit>();

            var result = new List<ItemRelationHit>();

            for (int i = 0; i < recordIds.Count; i++)
            {
                RelationRecord record;
                if (!data.Records.TryGetValue(recordIds[i], out record) || record == null)
                    continue;
                if (record.Parts == null) continue;

                for (int p = 0; p < record.Parts.Count; p++)
                {
                    var part = record.Parts[p];
                    if (part == null) continue;

                    bool isOutput = part.Role == PartRole.Output;
                    if (isOutput == wantUse) continue;              // 方向不符
                    if (!PartMatches(part, itemId)) continue;       // 关系里还有别的物品

                    result.Add(new ItemRelationHit
                    {
                        Kind = record.Kind,
                        ProviderId = record.ProviderId,
                        Label = record.Label,
                        Part = part,
                        Payload = record.Payload
                    });
                }
            }

            return result;
        }

        /// <summary>这条 part 是否就是查询的物品（含占位 id 展开）。</summary>
        private static bool PartMatches(ItemPart part, string itemId)
        {
            if (part == null || string.IsNullOrEmpty(itemId)) return false;

            if (!string.IsNullOrEmpty(part.Id))
            {
                if (string.Equals(part.Id, itemId, StringComparison.Ordinal)) return true;

                string expanded = LootSource.ExpandPlaceholder(part.Id);
                if (!string.IsNullOrEmpty(expanded) &&
                    string.Equals(expanded, itemId, StringComparison.Ordinal)) return true;
            }

            if (part.Ids != null)
            {
                for (int i = 0; i < part.Ids.Count; i++)
                {
                    string id = part.Ids[i];
                    if (string.IsNullOrEmpty(id)) continue;
                    if (string.Equals(id, itemId, StringComparison.Ordinal)) return true;

                    string expanded = LootSource.ExpandPlaceholder(id);
                    if (!string.IsNullOrEmpty(expanded) &&
                        string.Equals(expanded, itemId, StringComparison.Ordinal)) return true;
                }
            }

            // 按标签匹配的投入 / 收购（HOUSEHOLD_GOOD 这类）：
            // 登记时已把成员物品写进 ByItem，这里要同样认标签，
            // 否则「右键物品 → 用途 / 求购」取到关系后又在这一步被丢掉。
            if (part.Tags != null)
            {
                for (int t = 0; t < part.Tags.Count; t++)
                {
                    var members = TagMembers(part.Tags[t]);
                    for (int k = 0; k < members.Count; k++)
                        if (string.Equals(members[k], itemId, StringComparison.Ordinal)) return true;
                }
            }

            return false;
        }

        private static RelationIndexData BuildRelationCore()
        {
            var watch = Stopwatch.StartNew();
            var data = new RelationIndexData();

            // 图鉴还没建好（预热没跑完）就先不建，避免把空表写进总缓存
            if (Visible.Count == 0)
            {
                if (!_deferredCatalogLogged && ModEntry.Log != null)
                {
                    _deferredCatalogLogged = true;
                    ModEntry.Log.Log("[relations] catalog not ready, deferred");
                }
                return null;
            }

            // 配方是存档数据（recipeManager 进存档后才挂上）：主动扫一次。
            // 扫不到（这个存档没有工作台）也照常建表——否则整份关系总表永远写不进缓存。
            try { RecipeIndex.Build(); }
            catch (Exception e) { WarnRelation("recipe build: " + e.Message); }

            if (!RecipeIndex.Built)
                WarnRelation("no crafting tables in this save, building without recipes");

            // 掉落表是静态数据、本次会话一定会加载完整；没就绪就先不建（不落盘半份表）
            try
            {
                var lootTables = LootSource.Tables;
                if (lootTables == null || lootTables.Length == 0)
                {
                    if (!_deferredLootLogged && ModEntry.Log != null)
                    {
                        _deferredLootLogged = true;
                        ModEntry.Log.Log("[relations] loot tables not ready, deferred");
                    }
                    return null;
                }
            }
            catch (Exception e)
            {
                WarnRelation("loot ready check: " + e.Message);
                return null;
            }

            // 客人 / 地点货单：卖出的物品 → Output；收购清单 → Material
            try
            {
                SourceIndex.Build();
                var sources = SourceIndex.AllSources;
                for (int i = 0; i < sources.Count; i++)
                {
                    var source = sources[i];
                    if (source == null) continue;

                    var kind = source.Kind == SourceKind.Client
                        ? ItemRelationKind.Client
                        : ItemRelationKind.Location;

                    AddRelation(data, kind, source.SourceId, source.Name, source, source.Parts);
                }
            }
            catch (Exception e) { WarnRelation("sources: " + e.Message); }

            // 掉落表：表里能开出的物品 → Output
            try
            {
                var tables = LootSource.Tables;
                if (tables != null)
                {
                    for (int i = 0; i < tables.Length; i++)
                    {
                        var table = tables[i];
                        if (table == null) continue;

                        AddRelation(data, ItemRelationKind.Loot, table.TableId, table.TableId, table, table.Parts);
                    }
                }
            }
            catch (Exception e) { WarnRelation("loot: " + e.Message); }

            // 配方：机器（Tool）、材料 / 工具（Material）、产物（Output）
            try
            {
                var recipes = RecipeIndex.All;
                for (int i = 0; i < recipes.Count; i++)
                {
                    var recipe = recipes[i];
                    if (recipe == null) continue;

                    AddRelation(data, ItemRelationKind.Recipe, recipe.MachineId, recipe.MachineName, recipe, recipe.Parts);
                }
            }
            catch (Exception e) { WarnRelation("recipes: " + e.Message); }

            // 机器静态配方：每个槽位自动变成一条 Module / Material / Output
            try
            {
                var machineRecipes = ItemCatalog.UI.MachinePanels.AllRecipes();
                for (int i = 0; i < machineRecipes.Count; i++)
                {
                    var pair = machineRecipes[i];
                    var parts = MachineRelationParts(pair.Key, pair.Value);
                    AddRelation(data, ItemRelationKind.Machine, pair.Key, pair.Key, pair.Value, parts);
                }
            }
            catch (Exception e) { WarnRelation("machines: " + e.Message); }

            // 枪械改装槽：一条槽一条关系（枪 Tool、槽接受的配件作为 Module 候选 Ids）
            try
            {
                var guns = ItemCatalog.UI.GunMods.ModdableGunIds();
                for (int i = 0; i < guns.Count; i++)
                {
                    string gunId = guns[i];
                    if (string.IsNullOrEmpty(gunId)) continue;

                    var slots = ItemCatalog.UI.GunMods.SlotsFor(gunId);
                    if (slots == null || slots.Count == 0) continue;

                    for (int s = 0; s < slots.Count; s++)
                    {
                        var slot = slots[s];
                        if (slot == null) continue;

                        var parts = new List<ItemPart>
                        {
                            // Tool 的 SlotId 记下槽位角色：枪槽表从关系记录还原时靠它
                            new ItemPart { Role = PartRole.Tool, Id = gunId, Amount = 1, SlotId = slot.Role }
                        };

                        var mods = ItemCatalog.UI.GunMods.Compatible(gunId, s);
                        var candidates = new List<string>();
                        if (mods != null)
                        {
                            for (int m = 0; m < mods.Count; m++)
                            {
                                if (string.IsNullOrEmpty(mods[m])) continue;
                                if (!candidates.Contains(mods[m])) candidates.Add(mods[m]);
                            }
                        }

                        if (candidates.Count > 0)
                        {
                            parts.Add(new ItemPart
                            {
                                Role = PartRole.Module,
                                Ids = candidates,
                                Amount = 1,
                                SlotId = slot.Role
                            });
                        }

                        AddRelation(data, ItemRelationKind.Gun, gunId, slot.Title, slot, parts);
                    }
                }
            }
            catch (Exception e) { WarnRelation("guns: " + e.Message); }

            // 使用：uses.csv + 自动补充 + 开箱
            try
            {
                var uses = UseIndex.AllRecords();
                for (int i = 0; i < uses.Count; i++)
                {
                    var use = uses[i];
                    if (use == null) continue;

                    AddRelation(data, ItemRelationKind.Use, use.Kind, use.Kind, use, UseRelationParts(use));
                }
            }
            catch (Exception e) { WarnRelation("uses: " + e.Message); }

            // 网络升级：授予的物品 → Output
            try
            {
                var upgrades = UpgradeCatalog.AllRecords();
                for (int i = 0; i < upgrades.Count; i++)
                {
                    var upgrade = upgrades[i];
                    if (upgrade == null) continue;

                    AddRelation(data, ItemRelationKind.Upgrade, upgrade.Id, upgrade.Title, upgrade, upgrade.Parts);
                }
            }
            catch (Exception e) { WarnRelation("upgrades: " + e.Message); }

            // 图鉴里出现的每个物品都要在索引里有一行（没有关系就是空列表），
            // 这样总缓存覆盖全部可见物品，而不是只覆盖参与过关系的那些。
            try
            {
                var visible = Visible;
                for (int i = 0; i < visible.Count; i++)
                {
                    var entry = visible[i];
                    if (entry == null || string.IsNullOrEmpty(entry.Id)) continue;
                    if (!data.ByItem.ContainsKey(entry.Id)) data.ByItem[entry.Id] = new List<string>();
                }
            }
            catch (Exception e) { WarnRelation("catalog coverage: " + e.Message); }

            if (ModEntry.Log != null)
            {
                int indexed = 0;
                foreach (var pair in data.ByItem) indexed += pair.Value == null ? 0 : pair.Value.Count;

                ModEntry.Log.Log("[relations] relations=" + data.Records.Count
                                 + " entries=" + indexed
                                 + " items=" + data.ByItem.Count
                                 + " (" + watch.ElapsedMilliseconds + " ms)");
            }

            return data;
        }

        /// <summary>把一条机器配方的每个槽位映射成角色：机器 Tool、投入 Material、模块按槽 Module、产物 Output。</summary>
        private static List<ItemPart> MachineRelationParts(string machineId, ItemCatalog.UI.MachinePanels.Recipe recipe)
        {
            var parts = new List<ItemPart>();
            if (recipe == null) return parts;

            if (!string.IsNullOrEmpty(machineId))
            {
                parts.Add(new ItemPart
                {
                    Role = PartRole.Tool,
                    Id = machineId,
                    Amount = 1
                });
            }

            AddMachineIds(parts, recipe.Inputs, PartRole.Material, null);
            AddMachineIds(parts, recipe.Modules, PartRole.Module, "module");
            AddMachineIds(parts, new[] { recipe.Chip }, PartRole.Module, "chip");
            AddMachineIds(parts, new[] { recipe.Container }, PartRole.Module, "container");
            AddMachineIds(parts, new[] { recipe.CageDispenser }, PartRole.Module, "dispenser");
            AddMachineIds(parts, new[] { recipe.CageButcher }, PartRole.Module, "butcher");
            AddMachineIds(parts, recipe.Outputs, PartRole.Output, null);

            if (!string.IsNullOrEmpty(recipe.CageAnimal))
            {
                parts.Add(new ItemPart
                {
                    Role = PartRole.Output,
                    Id = recipe.CageAnimal,
                    Amount = 1,
                    SlotId = "animal"
                });
            }

            // 水培：水和矿物质是自动灌进去的（配方的 NutrientPercent / 液体容器），
            // 不在 Inputs 里。挂成隐性 Material，查水 / 矿物质的用途才能看到「种植」。
            if (string.Equals(machineId, "hydroponic", StringComparison.Ordinal))
            {
                parts.Add(new ItemPart
                {
                    Role = PartRole.Material,
                    Id = ItemCatalog.Catalog.LiquidCatalog.EntryPrefix + ItemCatalog.Catalog.LiquidManager.WaterId,
                    SlotId = "water",
                    Amount = 1
                });

                parts.Add(new ItemPart
                {
                    Role = PartRole.Material,
                    Id = ItemCatalog.Catalog.LiquidCatalog.EntryPrefix + "mineral",
                    SlotId = "nutrient",
                    Amount = 1
                });
            }

            // 只有搅拌机是「把投入物品里的成分倒进容器」：那些液体才是产出。
            // 其它机器投入里带的液体不是产物，不能记成 Output（否则查水会在「来源」里冒出种植）。
            if (string.Equals(machineId, "blender", StringComparison.Ordinal))
                AddMachineInputLiquids(parts, recipe.Inputs);

            return parts;
        }

        private static void AddMachineInputLiquids(List<ItemPart> parts, string[] inputs)
        {
            if (parts == null || inputs == null) return;

            for (int i = 0; i < inputs.Length; i++)
            {
                string id = inputs[i];
                if (string.IsNullOrEmpty(id)) continue;

                if (ItemCatalog.Catalog.LiquidCatalog.IsLiquidEntry(id))
                {
                    parts.Add(new ItemPart { Role = PartRole.Output, Id = id, Amount = 1 });
                    continue;
                }

                ItemEntry entry = null;
                try { entry = RecipeIndex.FindEntry(id); } catch { }
                var item = entry == null ? null : entry.Item;
                if (item == null) continue;

                try
                {
                    var liquidParts = LiquidManager.Parts(item);
                    foreach (var pair in liquidParts)
                    {
                        if (string.IsNullOrEmpty(pair.Key)) continue;

                        string liquidId = ItemCatalog.Catalog.LiquidCatalog.EntryPrefix + pair.Key;
                        parts.Add(new ItemPart { Role = PartRole.Output, Id = liquidId, Amount = 1 });
                    }
                }
                catch { }

                if (!string.IsNullOrEmpty(entry.LiquidTag))
                {
                    parts.Add(new ItemPart
                    {
                        Role = PartRole.Output,
                        Id = ItemCatalog.Catalog.LiquidCatalog.EntryPrefix + entry.LiquidTag,
                        Amount = 1
                    });
                }
            }
        }

        private static void AddMachineIds(List<ItemPart> parts, string[] ids, PartRole role, string slotId)
        {
            if (parts == null || ids == null) return;

            for (int i = 0; i < ids.Length; i++)
            {
                string id = ids[i];
                if (string.IsNullOrEmpty(id)) continue;

                parts.Add(new ItemPart
                {
                    Role = role,
                    Id = id,
                    Amount = 1,
                    SlotId = slotId
                });
            }
        }

        /// <summary>
        /// 登记一条关系：用「所有列表 + 角色 / 槽位 / 数量」算内容哈希作为关系 id，
        /// 再把参与物品登记进「物品 → 关系 id」索引。相同内容的关系只存一份。
        /// </summary>
        private static void AddRelation(RelationIndexData data,
                                        ItemRelationKind kind,
                                        string providerId,
                                        string label,
                                        object payload,
                                        List<ItemPart> parts)
        {
            if (data == null || parts == null || parts.Count == 0) return;

            string id = ComputeRelationId(kind, providerId, parts);

            RelationRecord record;
            if (!data.Records.TryGetValue(id, out record) || record == null)
            {
                record = new RelationRecord
                {
                    Id = id,
                    Kind = kind,
                    ProviderId = providerId,
                    Label = label,
                    Parts = parts,
                    Payload = payload
                };
                data.Records[id] = record;
            }

            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part == null) continue;

                if (!string.IsNullOrEmpty(part.Id))
                    AddRelationItem(data, part.Id, id);

                if (part.Ids != null)
                {
                    for (int k = 0; k < part.Ids.Count; k++)
                    {
                        if (string.IsNullOrEmpty(part.Ids[k])) continue;
                        AddRelationItem(data, part.Ids[k], id);
                    }
                }

                // 按标签匹配的投入（拆解枪 / 屠宰刀 / 整类作物…）：把满足该标签的物品
                // 逐个登记，查询端才能从具体物品反查到这条关系。
                if (part.Tags != null)
                {
                    for (int t = 0; t < part.Tags.Count; t++)
                    {
                        var members = TagMembers(part.Tags[t]);
                        for (int k = 0; k < members.Count; k++)
                            AddRelationItemOne(data, members[k], id);
                    }
                }
            }
        }

        /// <summary>tag → 目录里带这个标签的物品 id（按 generation 缓存）。</summary>
        private static List<string> TagMembers(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return EmptyIds;

            if (_tagMembersGeneration != CatalogDataBuilder.Generation)
            {
                _tagMembers.Clear();
                _tagMembersGeneration = CatalogDataBuilder.Generation;
            }

            List<string> list;
            if (_tagMembers.TryGetValue(tag, out list)) return list;

            list = new List<string>();

            var items = Visible;
            for (int i = 0; i < items.Count; i++)
            {
                var entry = items[i];
                if (entry == null || entry.Item == null || string.IsNullOrEmpty(entry.Id)) continue;

                bool hit = false;
                try { hit = entry.Item.IsTag(tag); }
                catch { }
                if (!hit)
                {
                    // 收购词分两种：`WATER` 走 IsTag，`WEAPON` / `FOOD` 这类走类型
                    // （SetGameItemType），和 SourceIndex.WantedBy 同一套判定。
                    try { hit = entry.Item.IsGameItemType(tag); }
                    catch { }
                }

                if (hit) list.Add(entry.Id);
            }

            _tagMembers[tag] = list;
            return list;
        }

        private static readonly Dictionary<string, List<string>> _tagMembers =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private static readonly List<string> EmptyIds = new List<string>();
        private static int _tagMembersGeneration = -1;

        /// <summary>配方 / 关系的内容哈希：把机器、角色、槽位、候选 id 集合、数量全部拼起来再取 SHA1。</summary>
        private static string ComputeRelationId(ItemRelationKind kind, string providerId, List<ItemPart> parts)
        {
            var text = new StringBuilder();
            text.Append((int)kind).Append('|').Append(providerId ?? string.Empty).Append('\n');

            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part == null) continue;

                text.Append((int)part.Role).Append('|')
                    .Append(part.SlotId ?? string.Empty).Append('|')
                    .Append(part.Id ?? string.Empty).Append('|')
                    .Append(part.Amount).Append('|');

                if (part.Ids != null && part.Ids.Count > 0)
                {
                    var ids = new List<string>(part.Ids);
                    ids.Sort(StringComparer.Ordinal);
                    text.Append(string.Join(",", ids.ToArray()));
                }

                text.Append('\n');
            }

            return Sha1(text.ToString());
        }

        private static string Sha1(string text)
        {
            try
            {
                using (var sha = SHA1.Create())
                {
                    byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                    var sb = new StringBuilder(hash.Length * 2);
                    for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch
            {
                return text == null ? "0" : text.GetHashCode().ToString("x8");
            }
        }

        private static void AddRelationItem(RelationIndexData data, string itemId, string recordId)
        {
            if (string.IsNullOrEmpty(itemId)) return;

            AddRelationItemOne(data, itemId, recordId);

            // 占位 id（random_*）同时按展开后的本体登记，和现有正反查口径一致
            string expanded = LootSource.ExpandPlaceholder(itemId);
            if (!string.IsNullOrEmpty(expanded) &&
                !string.Equals(expanded, itemId, StringComparison.Ordinal))
            {
                AddRelationItemOne(data, expanded, recordId);
            }

            // 变体 / 预构建键（_used、full_* …）也按本体登记，
            // 与 SourceData.AddAliasSource / LootSource.ResolveAlias 同一口径。
            string baseId = LootSource.BaseItemId(itemId);
            if (!string.IsNullOrEmpty(baseId) &&
                !string.Equals(baseId, itemId, StringComparison.Ordinal))
            {
                AddRelationItemOne(data, baseId, recordId);
            }
        }

        private static void AddRelationItemOne(RelationIndexData data, string itemId, string recordId)
        {
            List<string> list;
            if (!data.ByItem.TryGetValue(itemId, out list))
            {
                list = new List<string>();
                data.ByItem[itemId] = list;
            }

            if (!list.Contains(recordId)) list.Add(recordId);
        }

        // ---------------------------------------------------------------- 总缓存格式
        //
        // r <id> <kind> <provider> <label>          ← 关系本体
        // p <id> <role> <slot> <partId> <ids> <amt> ← 关系的参与物品
        // i <itemId> <recordId>|<recordId>…         ← 物品 → 关系 id

        private static string SerializeRelations(RelationIndexData data)
        {
            if (data == null || data.Records.Count == 0) return string.Empty;

            var text = new StringBuilder();

            var ids = new List<string>(data.Records.Keys);
            ids.Sort(StringComparer.Ordinal);

            for (int i = 0; i < ids.Count; i++)
            {
                var record = data.Records[ids[i]];
                if (record == null) continue;

                text.Append("r\t").Append(record.Id).Append('\t')
                    .Append((int)record.Kind).Append('\t')
                    .Append(EscapeField(record.ProviderId)).Append('\t')
                    .Append(EscapeField(record.Label)).Append('\n');

                if (record.Parts == null) continue;

                for (int p = 0; p < record.Parts.Count; p++)
                {
                    var part = record.Parts[p];
                    if (part == null) continue;

                    text.Append("p\t").Append(record.Id).Append('\t')
                        .Append((int)part.Role).Append('\t')
                        .Append(EscapeField(part.SlotId)).Append('\t')
                        .Append(EscapeField(part.Id)).Append('\t')
                        .Append(EscapeIds(part.Ids)).Append('\t')
                        .Append(part.Amount).Append('\t')
                        .Append(EscapeIds(part.Tags)).Append('\n');
                }
            }

            foreach (var pair in data.ByItem)
            {
                var list = pair.Value;

                text.Append("i\t").Append(pair.Key).Append('\t');
                if (list != null)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i > 0) text.Append('|');
                        text.Append(list[i]);
                    }
                }
                text.Append('\n');
            }

            return text.ToString();
        }

        private static RelationIndexData ParseRelations(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return null;

            var data = new RelationIndexData();

            // 旧格式的 p 行没有 Tags 列（第 8 列）：整份作废，重建一次写出带 Tags 的新格式。
            bool legacy = false;

            var lines = payload.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrEmpty(line)) continue;

                var cols = line.Split('\t');
                if (cols.Length < 2) continue;

                if (cols[0] == "r" && cols.Length >= 5)
                {
                    string id = cols[1];
                    if (string.IsNullOrEmpty(id)) continue;

                    int kind;
                    if (!int.TryParse(cols[2], out kind)) continue;

                    data.Records[id] = new RelationRecord
                    {
                        Id = id,
                        Kind = (ItemRelationKind)kind,
                        ProviderId = cols[3],
                        Label = cols[4],
                        Payload = null
                    };
                }
                else if (cols[0] == "p" && cols.Length >= 7)
                {
                    int role, amount;
                    if (!int.TryParse(cols[2], out role)) continue;
                    if (!int.TryParse(cols[6], out amount) || amount < 1) amount = 1;

                    if (cols.Length < 8) legacy = true;   // 旧格式：没有 Tags 列

                    RelationRecord record;
                    if (!data.Records.TryGetValue(cols[1], out record) || record == null) continue;

                    record.Parts.Add(new ItemPart
                    {
                        Role = (PartRole)role,
                        SlotId = string.IsNullOrEmpty(cols[3]) ? null : cols[3],
                        Id = string.IsNullOrEmpty(cols[4]) ? null : cols[4],
                        Ids = SplitIds(cols[5]),
                        Amount = amount,
                        Tags = cols.Length >= 8 ? SplitIds(cols[7]) : new List<string>()
                    });
                }
                else if (cols[0] == "i" && cols.Length >= 2)
                {
                    string itemId = cols[1];
                    if (string.IsNullOrEmpty(itemId)) continue;

                    var ids = new List<string>();
                    if (cols.Length >= 3 && !string.IsNullOrEmpty(cols[2]))
                    {
                        var parts = cols[2].Split('|');
                        for (int k = 0; k < parts.Length; k++)
                        {
                            if (!string.IsNullOrEmpty(parts[k])) ids.Add(parts[k]);
                        }
                    }

                    data.ByItem[itemId] = ids;
                }
            }

            // 物品索引里引用了不存在的关系时剔除；本来就没有关系的空行要保留
            var dead = new List<string>();
            foreach (var pair in data.ByItem)
            {
                var list = pair.Value;
                bool had = list != null && list.Count > 0;

                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (data.Records.ContainsKey(list[i])) continue;
                    list.RemoveAt(i);
                }

                if (had && list.Count == 0) dead.Add(pair.Key);
            }

            for (int i = 0; i < dead.Count; i++) data.ByItem.Remove(dead[i]);

            if (legacy)
            {
                WarnRelation("cache has no tag column, rebuilding");
                return null;
            }

            return data.Records.Count > 0 ? data : null;
        }

        private static List<string> SplitIds(string text)
        {
            var ids = new List<string>();
            if (string.IsNullOrEmpty(text)) return ids;

            var parts = text.Split('|');
            for (int i = 0; i < parts.Length; i++)
            {
                if (!string.IsNullOrEmpty(parts[i])) ids.Add(parts[i]);
            }

            return ids;
        }

        private static string EscapeIds(List<string> ids)
        {
            if (ids == null || ids.Count == 0) return string.Empty;

            var text = new StringBuilder();
            for (int i = 0; i < ids.Count; i++)
            {
                if (string.IsNullOrEmpty(ids[i])) continue;
                if (text.Length > 0) text.Append('|');
                text.Append(EscapeField(ids[i]));
            }

            return text.ToString();
        }

        private static string EscapeField(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        }

        private static void WarnRelation(string message)
        {
            try { if (ModEntry.Log != null) ModEntry.Log.Warning("[relations] " + message); } catch { }
        }
    }
}
