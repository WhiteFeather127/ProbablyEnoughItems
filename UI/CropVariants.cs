﻿﻿﻿﻿﻿using System;
using System.Collections.Generic;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 作物的品质档。游戏在 <see cref="PreBuiltItemHelper"/> 里给每档作物各写了一个
    /// 生成函数（小 / 普通 / 大 / 巨大），产出的是**同一个本体 id**、但名字、单价、
    /// 精灵不同的实例——图鉴把每一档各展开成一格。
    ///
    /// 和 <see cref="PreBuiltItems"/> 的预构建键不同：这些生成函数没有登记进
    /// <c>PreBuiltItemHelper.preBuiltItems</c>，也不能让 <see cref="CatalogWindow.CreatePreviewItem(string)"/>
    /// 直接认 id（那会让水培投入这类普通预览也随机变成某一档），所以单独一张表。
    /// </summary>
    internal static class CropVariants
    {
        internal sealed class Tier
        {
            internal string Label;
            internal Func<GameItem> Create;
        }

        private static Tier T(string label, Func<GameItem> create)
        {
            return new Tier { Label = label, Create = create };
        }

        /// <summary>本体 id → 品质档（从低到高，最后一档即最佳品质）。</summary>
        private static readonly Dictionary<string, Tier[]> Table =
            new Dictionary<string, Tier[]>(StringComparer.Ordinal)
            {
                { "nutrifruit", new[] {
                    T("小", PreBuiltItemHelper.CreateNutrifruitSmall),
                    T("普通", PreBuiltItemHelper.CreateNutrifruitNormal),
                    T("大", PreBuiltItemHelper.CreateNutrifruitLarge),
                    T("巨大", PreBuiltItemHelper.CreateNutrifruitHuge) } },
                { "kotton_fiber", new[] {
                    T("小", PreBuiltItemHelper.CreateSmallKotton),
                    T("普通", PreBuiltItemHelper.CreateKotton),
                    T("大", PreBuiltItemHelper.CreateLargeKotton) } },
                { "dream_cap", new[] {
                    T("小", delegate { return PreBuiltItemHelper.CreateSmallDreamCap(false); }),
                    T("普通", delegate { return PreBuiltItemHelper.CreateDreamCap(false); }),
                    T("大", delegate { return PreBuiltItemHelper.CreateLargeDreamCap(false); }) } },
                { "meal_cap", new[] {
                    T("小", delegate { return PreBuiltItemHelper.CreateSmallDreamCap(true); }),
                    T("普通", delegate { return PreBuiltItemHelper.CreateDreamCap(true); }),
                    T("大", delegate { return PreBuiltItemHelper.CreateLargeDreamCap(true); }) } },
                { "fillerweed", new[] {
                    T("小", PreBuiltItemHelper.CreateFillerweedSmall),
                    T("普通", PreBuiltItemHelper.CreateFillerweed) } }
            };

        private static Tier[] Tiers(string id)
        {
            Tier[] tiers;
            return id != null && Table.TryGetValue(id, out tiers) ? tiers : null;
        }

        /// <summary>是不是有品质档的作物。</summary>
        internal static bool Has(string id)
        {
            return Tiers(id) != null;
        }

        /// <summary>品质档数量；不是作物返回 0。</summary>
        internal static int Count(string id)
        {
            var tiers = Tiers(id);
            return tiers == null ? 0 : tiers.Length;
        }

        /// <summary>最佳品质的档位（大型果）；不是作物返回 -1。</summary>
        internal static int BestIndex(string id)
        {
            int count = Count(id);
            return count <= 0 ? -1 : count - 1;
        }

        internal static string Label(string id, int index)
        {
            var tiers = Tiers(id);
            if (tiers == null || tiers.Length == 0) return null;

            int i = ((index % tiers.Length) + tiers.Length) % tiers.Length;
            return tiers[i].Label;
        }

        /// <summary>
        /// 造一档作物实例。工厂里会掷植物基因（<c>ApplyFactoryGenetics</c>），
        /// 所以统一走 <see cref="CatalogWindow.CreatePreviewItem(string, Func{GameItem})"/>
        /// 的隔离随机流，不污染存档的随机序列。
        /// 食用菇的工厂是从 dream_cap 实例改出来的（identifier 仍是 dream_cap），
        /// 而目录里 meal_cap 是独立 id，这里把 identifier 归一到表键，
        /// 图鉴 ID 行、拿取、按 id 反查才会一致。
        /// </summary>
        internal static GameItem Create(string id, int index)
        {
            var tiers = Tiers(id);
            if (tiers == null || tiers.Length == 0) return null;

            int i = ((index % tiers.Length) + tiers.Length) % tiers.Length;

            var item = CatalogWindow.CreatePreviewItem(id, tiers[i].Create);
            if (item == null) return null;

            try
            {
                if (!string.Equals(item.identifier, id, StringComparison.Ordinal))
                    item.identifier = id;
            }
            catch (Exception e)
            {
                if (ModEntry.Log != null)
                    ModEntry.Log.Warning("[crop] id normalize '" + id + "': " + e.Message);
            }

            return item;
        }
    }
}
