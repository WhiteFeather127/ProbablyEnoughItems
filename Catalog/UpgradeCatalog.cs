using System;
using System.Collections.Generic;
using System.Text;

using UnityEngine;

namespace ItemCatalog.Catalog
{
    /// <summary>
    /// 「王尔德的人脉网络」里的一个升级（原版 <c>NetworkUpgrade</c> 定义）。
    /// 文案在 <see cref="UpgradeCatalog"/> 建索引时取一次并缓存，
    /// 失效规则与其它索引一致（由 <c>IndexManager</c> 统一管理）。
    /// </summary>
    internal sealed class UpgradeInfo
    {
        internal string Id;
        internal string Title;
        internal string Description;

        /// <summary>等级名：原版 <c>GetTabLabel</c> 的结果（默认档 → network_ui_tab_basic）。</summary>
        internal string Level;

        internal string IconKey;

        /// <summary>基价（不套难度 / 天赋倍率），图鉴要稳定数字。</summary>
        internal int Cost;

        /// <summary>这次升级会给的物品（真实物品 id，已去重）。</summary>
        internal string[] Items;

        private List<ItemPart> _parts;

        /// <summary>和其它关系同一套角色视图：升级授予的物品 → Output。</summary>
        internal List<ItemPart> Parts
        {
            get
            {
                if (_parts != null) return _parts;

                _parts = new List<ItemPart>();
                if (Items == null) return _parts;

                for (int i = 0; i < Items.Length; i++)
                {
                    string id = Items[i];
                    if (string.IsNullOrEmpty(id)) continue;

                    _parts.Add(new ItemPart
                    {
                        Role = PartRole.Output,
                        Id = id,
                        Amount = 1
                    });
                }

                return _parts;
            }
        }

        /// <summary>图标解析成功后缓存；失败不缓存，下次渲染再试（prefab 可能还没就绪）。</summary>
        internal Sprite IconSprite;
    }

    /// <summary>
    /// 网络升级索引：物品 id → 哪些升级会给出它。
    ///
    /// 数据全部来自游戏：升级清单用 <c>NetworkUpgrade.GetDefinitionsInOrder()</c>，
    /// 等级顺序用原版 <c>NetworkUpgradeLayout.Build</c> 现算，等级名用
    /// <c>NetworkUpgrade.GetTabLabel</c>（任意新 tab 都能解析），标题 / 介绍用定义自己的本地化方法。
    /// 结果由 <see cref="IndexManager"/> 统一缓存（generation + 环境签名），一次启动只建一次。
    ///
    /// 唯一写死的是「升级 → 授予物品」表——原版把奖励写在 <c>NetworkUpgradeList</c> 的
    /// 解锁 lambda 里，运行时要取只能真的执行一次（会改存档），所以照
    /// <c>tools/audit/out/sources_all.csv</c>（kind=upgrade）与 lambda IL 抄成静态表。
    /// 新增升级若无奖励，不登记即可。
    /// </summary>
    internal static class UpgradeCatalog
    {
        private static readonly Dictionary<string, string[]> ItemGrants =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                { "BREWER", new[] { "card_brewer" } },
                { "CHEMIST", new[] { "card_chem" } },
                { "PHARMA", new[] { "card_pharma" } },
                { "MINER", new[] { "card_mining" } },

                // 退休农夫：种子 / 手册 / 营养片 / 小份口粮
                { "RETIRED_FARMER", new[] {
                    "fillerweed_seed", "hydroponic_guide", "nutrient_tablet",
                    "nutrifruit_seed", "water_ration_small" } },
                // 退休牧场主：屠宰 / 标记 / 加工肉（另有牲畜，不是物品）
                { "RETIRED_RANCHER", new[] {
                    "kitchen_cleaver", "labeler_blue", "marking_pen", "processed_meat" } },
                // 退休枪匠：整箱枪械零件与配件
                { "RETIRED_GUNSMITH", new[] {
                    "gun_part", "internal_springs_improved", "laser", "laser_toy",
                    "mag_22", "mag_22_high", "printer_chip_pistol", "printer_plastic",
                    "rds", "screwdriver", "silencer", "silencer_makeshift" } },
                // 退休化学家：一整套化学器械与耗材
                { "RETIRED_CHEMIST", new[] {
                    "100ml_cylinder", "1ml_dropper", "25ml_cylinder", "5ml_dropper",
                    "blender", "bottled_water", "chem_finisher", "chem_scanner",
                    "chemistry_guide", "coffee_bean", "common_chemical", "heating_plate",
                    "kotton_fabric", "water_pitcher" } },

                // 枪支许可：招牌
                { "GUN_PERMIT_I", new[] { "sign_firearm" } },
                { "GUN_PERMIT_III", new[] { "sign_firearm_heavy" } },

                // 水商：净水器 + 招牌 + 滤芯 + 手册（water_jug 是预构建键，走 PreBuiltEntry）
                { "WATER_TRADER", new[] {
                    "portable_water_purifier", "sign_water", "water_filter",
                    "water_guide", "water_pitcher" } },

                // 水产养殖：养殖箱 / 水箱 / 饲料 / 刀具；II 级给两条鳗鱼
                { "AQUACULTURE", new[] {
                    "industrial_fish_feed", "kitchen_knife", "marking_pen",
                    "ras", "ras_water_tank", "water_jug" } },
                { "AQUACULTURE_II", new[] { "eel" } },

                // 酿酒：酵母 / 酒瓶 / 酒果 / 口粮 / 放大镜 / 酿酒手册
                { "WINEMAKER", new[] {
                    "magnifier", "water_ration", "wine_berry",
                    "wine_book", "wine_bottle", "wine_yeast" } },

                // 植物 DNA：基因扫描仪 + DNA 盘
                { "PLANT_DNA", new[] { "gene_scanner", "plant_dna_disk" } },
            };

        private const string IndexName = "network-upgrades";

        /// <summary>升级索引本体：全量定义 + 物品反查；由 <see cref="IndexManager"/> 统一缓存。</summary>
        private sealed class UpgradeIndex
        {
            internal List<UpgradeInfo> All;
            internal Dictionary<string, List<UpgradeInfo>> ByItem;
        }

        private static string _networkTitle;

        private static Sprite[] _prefabIcons;
        private static Sprite _priceTag;
        private static Sprite _slotFrame;
        private static Sprite _slotInset;
        private static Sprite _slotBorder;
        private static bool _prefabReady;
        private static bool _prefabWarned;

        /// <summary>预热时建一次；实际的内存缓存与失效交给 <see cref="IndexManager"/>。</summary>
        internal static void Build()
        {
            IndexManager.Get(IndexName, BuildIndex);
        }

        private static UpgradeIndex BuildIndex()
        {
            var all = new List<UpgradeInfo>();
            var byItem = new Dictionary<string, List<UpgradeInfo>>(StringComparer.Ordinal);

            List<NetworkUpgrade> definitions;
            try { definitions = NetworkUpgrade.GetDefinitionsInOrder(); }
            catch (Exception e)
            {
                Warn("definitions: " + e.Message);
                return new UpgradeIndex { All = all, ByItem = byItem };
            }

            // 等级清单：照原版网络 UI 的做法由 NetworkUpgradeLayout 分组，等级名现场解析。
            // 别的模组新增 tab（基础 / 高级之外的第三档）不用改这里。
            var tabs = DiscoverTabs(definitions);

            for (int i = 0; i < definitions.Count; i++)
            {
                var definition = definitions[i];
                if (definition == null || string.IsNullOrEmpty(definition.id)) continue;

                string[] grants;
                var info = new UpgradeInfo
                {
                    Id = definition.id,
                    IconKey = definition.iconKey,
                    Cost = definition.cost,
                    Items = ItemGrants.TryGetValue(definition.id, out grants) && grants != null
                        ? grants
                        : new string[0]
                };

                // 文案 / 等级只在建索引时取一次；索引跟着 generation / 环境签名失效
                try { info.Title = definition.GetLocalizedTitle(); } catch { }
                try { info.Description = definition.GetLocalizedDescription(); } catch { }
                info.Level = LevelLabel(definition.tab);

                if (string.IsNullOrEmpty(info.Title)) info.Title = definition.id;
                if (info.Description == null) info.Description = string.Empty;

                all.Add(info);

                for (int k = 0; k < info.Items.Length; k++)
                {
                    string itemId = info.Items[k];
                    if (string.IsNullOrEmpty(itemId)) continue;

                    List<UpgradeInfo> list;
                    if (!byItem.TryGetValue(itemId, out list))
                    {
                        list = new List<UpgradeInfo>();
                        byItem[itemId] = list;
                    }
                    list.Add(info);
                }
            }

            if (ModEntry.Log != null)
            {
                int withItems = 0;
                for (int i = 0; i < all.Count; i++)
                    if (all[i].Items != null && all[i].Items.Length > 0) withItems++;

                var levels = new StringBuilder();
                for (int i = 0; i < tabs.Count; i++)
                {
                    if (i > 0) levels.Append(',');
                    levels.Append(LevelLabel(tabs[i].tab));
                }

                ModEntry.Log.Log("[upgrade] defs=" + all.Count
                                 + " withItems=" + withItems
                                 + " items=" + byItem.Count
                                 + " levels=" + levels);
            }

            return new UpgradeIndex { All = all, ByItem = byItem };
        }

        /// <summary>给出这件物品的全部升级（按定义顺序）。没有返回空表。</summary>
        internal static List<UpgradeInfo> For(string itemId)
        {
            var index = IndexManager.Get(IndexName, BuildIndex);
            if (index == null || string.IsNullOrEmpty(itemId)) return new List<UpgradeInfo>();

            List<UpgradeInfo> list;
            return index.ByItem.TryGetValue(itemId, out list) ? list : new List<UpgradeInfo>();
        }

        /// <summary>全部升级定义（给统一关系索引聚合用）。</summary>
        internal static List<UpgradeInfo> AllRecords()
        {
            var index = IndexManager.Get(IndexName, BuildIndex);
            return index == null ? new List<UpgradeInfo>() : index.All;
        }

        /// <summary>等级名：直接问原版（默认档走 network_ui_tab_basic，其余走 network_ui_tab_&lt;tab&gt;）。</summary>
        internal static string LevelLabel(string tab)
        {
            string label = null;
            try { label = NetworkUpgrade.GetTabLabel(tab); } catch { }

            if (string.IsNullOrEmpty(label)) label = tab ?? string.Empty;
            return label;
        }

        /// <summary>原版的升级分档顺序；解析失败就退回按定义出现顺序去重。</summary>
        private static List<NetworkUpgradeLayout.Tab> DiscoverTabs(List<NetworkUpgrade> definitions)
        {
            try
            {
                var tabs = NetworkUpgradeLayout.Build(definitions);
                if (tabs != null && tabs.Count > 0) return tabs;
            }
            catch (Exception e) { Warn("layout: " + e.Message); }

            var fallback = new List<NetworkUpgradeLayout.Tab>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < definitions.Count; i++)
            {
                var definition = definitions[i];
                if (definition == null || string.IsNullOrEmpty(definition.id)) continue;

                string tab = definition.tab ?? string.Empty;
                if (!seen.Add(tab)) continue;
                fallback.Add(new NetworkUpgradeLayout.Tab { tab = tab });
            }
            return fallback;
        }

        /// <summary>大类标题：原版网络窗口的标题键 <c>inte_wild_network</c>。</summary>
        internal static string NetworkTitle
        {
            get
            {
                if (_networkTitle != null) return _networkTitle;

                string text = null;
                try { text = LocHelper.GetLocalizedUI("inte_wild_network", new object[0]); } catch { }

                // 取不到时给个英文兜底；标题一律用原版键，不在模组里另造文案
                if (IsMissing(text, "inte_wild_network")) text = "Wild's Network";

                _networkTitle = text;
                return _networkTitle;
            }
        }

        /// <summary>升级图标：照原版 <c>WildNetworkUI.ResolveIcon</c> 的顺序，只走公开 API。</summary>
        internal static Sprite Icon(UpgradeInfo info)
        {
            if (info == null) return null;
            if (info.IconSprite != null) return info.IconSprite;

            EnsurePrefab();

            Sprite sprite = null;

            if (!string.IsNullOrEmpty(info.IconKey))
            {
                var manager = CustomUIManager.Instance;
                if (manager != null)
                {
                    try { sprite = manager.GetSprite(info.IconKey); } catch { }
                }
                if (sprite == null) sprite = FindIcon(info.IconKey);
            }

            if (sprite == null) sprite = FindIcon(info.Id);
            if (sprite == null) sprite = FindIcon("BUY_REFERAL");

            if (sprite != null) info.IconSprite = sprite;
            return sprite;
        }

        /// <summary>原版价格标签底图（prefab 上的 <c>priceTagNormal</c>）。</summary>
        internal static Sprite PriceTag
        {
            get
            {
                EnsurePrefab();
                return _priceTag;
            }
        }

        /// <summary>原版升级槽的底图 / 内衬 / 外框（照 <c>BuildSlot</c> 逐层叠，84×84）。</summary>
        internal static Sprite SlotFrame
        {
            get
            {
                EnsurePrefab();
                return _slotFrame;
            }
        }

        internal static Sprite SlotInset
        {
            get
            {
                EnsurePrefab();
                return _slotInset;
            }
        }

        internal static Sprite SlotBorder
        {
            get
            {
                EnsurePrefab();
                return _slotBorder;
            }
        }

        private static Sprite FindIcon(string key)
        {
            if (string.IsNullOrEmpty(key) || _prefabIcons == null) return null;

            for (int i = 0; i < _prefabIcons.Length; i++)
            {
                var sprite = _prefabIcons[i];
                if (sprite != null && string.Equals(sprite.name, key, StringComparison.Ordinal))
                    return sprite;
            }

            return null;
        }

        /// <summary>
        /// 网络窗口 prefab 上的图标 / 价格标签。prefab 没就绪时不要记失败：
        /// 图鉴可能在 CustomUIManager 起来之前就渲染了一轮，下次再试即可。
        /// </summary>
        private static void EnsurePrefab()
        {
            if (_prefabReady) return;

            try
            {
                var manager = CustomUIManager.Instance;
                var prefab = manager == null ? null : manager.networkUIPrefab;
                if (prefab == null) return;

                var frame = prefab.GetComponent<WildNetworkFrame>();
                if (frame == null) return;

                _prefabIcons = frame.icons;
                _priceTag = frame.priceTagNormal;
                _slotFrame = frame.slotFrame;
                _slotInset = frame.slotInset;
                _slotBorder = frame.slotBorderNormal;
                _prefabReady = true;
            }
            catch (Exception e)
            {
                if (!_prefabWarned)
                {
                    _prefabWarned = true;
                    Warn("prefab: " + e.Message);
                }
            }
        }

        private static bool IsMissing(string text, string key)
        {
            if (string.IsNullOrEmpty(text)) return true;
            if (string.Equals(text, key, StringComparison.OrdinalIgnoreCase)) return true;
            return text.IndexOf("Translation Error", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void Warn(string message)
        {
            try { if (ModEntry.Log != null) ModEntry.Log.Warning("[upgrade] " + message); } catch { }
        }
    }
}
