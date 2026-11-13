using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace ProbablyEnoughItems.Api
{
    /// <summary>模块的一格：效果 id（null = 无特殊效果）+ 学习进度标记。</summary>
    public sealed class ModuleEffectCellInfo
    {
        /// <summary>模块效果标签 id；null 表示「无特殊效果」那一格。</summary>
        public string Effect;

        /// <summary>-1 = 非自学习；0 = 自学习但属性为 0；-2 = 自学习满属性（取系统上限）。</summary>
        public int Bonus;
    }

    /// <summary>浆果形态：品相 + 视觉变体 + 展示用标签。</summary>
    public sealed class BerryFormInfo
    {
        /// <summary>CONDITION 标签值（-2..2）。</summary>
        public int Condition;

        /// <summary>VISUAL 标签值（同品相下再分外观）。</summary>
        public int Visual;

        /// <summary>展示用短标签（图鉴内部用，语言可能固定为中文）。</summary>
        public string Label;
    }

    /// <summary>
    /// 图鉴内置展开逻辑用到的枚举表与隔离工具，开放给第三方 <see cref="ICatalogVariantProvider"/> 复用。
    ///
    /// 这些表与图鉴自己的硬编码分支**共用同一份数据**（图鉴内部直接引用这里的字段），
    /// 因此不会出现「模组抄了一份、游戏更新后失同步」的问题。
    ///
    /// 工具方法解决的都是 provider 容易踩的坑：
    /// <list type="bullet">
    /// <item><see cref="SnapshotModulePool"/> / <see cref="RestoreModulePool"/>：批量造物品时游戏会从
    /// <c>ModuleEffectHelper.moduleEffects</c> 里「抽走」词条，不还原会污染玩家后续抽到的模块。</item>
    /// <item><see cref="IsolateTagState"/>：复用同一个模板实例时各变体共享标签字典，改一个全变。</item>
    /// <item><see cref="NormalizeIssues"/>：把生成器随机掷出的瑕疵固定成「只留第 k 处」。</item>
    /// <item><see cref="StripAuthenticityLabels"/>：去掉标签打印机贴上去的真品/赝品那条 ItemFeature。</item>
    /// </list>
    /// </summary>
    public static class CatalogVariantKit
    {
        // ---------------------------------------------------------------- 枚举表

        /// <summary>自学习算法效果 id（特殊处理：额外多出「0 属性 / 满属性」两格）。</summary>
        public const string LearningEffectId = "MODULE_EFFECT_LEARNING_ALGORITHM";

        /// <summary>模块效果的完整清单，按游戏静态构造的注册顺序。</summary>
        public static readonly string[] ModuleEffectIds =
        {
            "MODULE_EFFECT_PREMIUM",
            "MODULE_EFFECT_INFERIOR",
            "MODULE_EFFECT_RECEIVER",
            "MODULE_EFFECT_TOP_MOUNT",
            "MODULE_EFFECT_SIDE_MOUNT",
            "MODULE_EFFECT_OVERVOLTED",
            "MODULE_EFFECT_DEGRADING",
            "MODULE_EFFECT_LEARNING_ALGORITHM",
            "MODULE_EFFECT_NEGATIVE_FEEDBACK"
        };

        /// <summary>模块占格精灵的后缀枚举（实际存在的才会被采用）。</summary>
        public static readonly string[] ModuleShapeSuffixes =
        {
            "",
            "_c",
            "_l",
            "_t",
            "_2",
            "_2_h",
            "_2_s",
            "_3",
            "_4",
            "_5"
        };

        /// <summary>
        /// 一个占格形状内的格子顺序：无特殊效果 → 各效果（自学习算法出「0 属性 / 满属性」两格）。
        /// </summary>
        public static readonly ModuleEffectCellInfo[] ModuleEffectCells = BuildModuleEffectCells();

        /// <summary>
        /// 浆果形态。品相取值：采收走 RollBloomberryCondition（-2 / -1 / 1 / 2 四档，不含 0），
        /// 客户货物等走工厂的 InitBerry（-2..1 均匀随机，含 0）。
        /// </summary>
        public static readonly BerryFormInfo[] BerryForms =
        {
            new BerryFormInfo { Condition =  2, Visual = 0, Label = "较小" },
            new BerryFormInfo { Condition =  1, Visual = 0, Label = "正常" },
            new BerryFormInfo { Condition =  0, Visual = 0, Label = "正常（+0）" },
            new BerryFormInfo { Condition = -1, Visual = 2, Label = "过熟" },
            new BerryFormInfo { Condition = -1, Visual = 0, Label = "干瘪" },
            new BerryFormInfo { Condition = -2, Visual = 0, Label = "发霉" }
        };

        /// <summary>香烟的四处瑕疵标签（印章 / logo / 字体 / 盒色）。</summary>
        public static readonly string[] CigaretteIssueTags =
        {
            "CIGARETTE_SEAL_ISSUE",
            "CIGARETTE_LOGO_ISSUE",
            "CIGARETTE_LETTERING_ISSUE",
            "CIGARETTE_COLOR_ISSUE"
        };

        /// <summary>免疫宁注射器的五处瑕疵标签（指示器 / 标签 / 颜色 / 序列号 / 瓶身）。</summary>
        public static readonly string[] InjectorIssueTags =
        {
            "ISSUE_INDICATOR",
            "ISSUE_LABEL",
            "ISSUE_COLOR",
            "ISSUE_SERIAL",
            "ISSUE_BODY"
        };

        /// <summary>食品券的五处瑕疵标签（印章 / logo / 芯片 / 字体 / 面额）。</summary>
        public static readonly string[] StampIssueTags =
        {
            "STAMP_SEAL_ISSUE",
            "STAMP_LOGO_ISSUE",
            "STAMP_CHIP_ISSUE",
            "STAMP_LETTERING_ISSUE",
            "STAMP_AMOUNT_ISSUE"
        };

        /// <summary>食品券的全部面额（CreateFakeStamp 的掷点即这五个值）。</summary>
        public static readonly int[] StampAmounts = { 40, 80, 120, 160, 200 };

        /// <summary>每种面额的状态数：1 张真券 + 每种瑕疵各 1 张假券。</summary>
        public static int StampStateCount
        {
            get { return 1 + StampIssueTags.Length; }
        }

        private static ModuleEffectCellInfo[] BuildModuleEffectCells()
        {
            var list = new List<ModuleEffectCellInfo>(ModuleEffectIds.Length + 2);

            list.Add(new ModuleEffectCellInfo { Effect = null, Bonus = -1 });

            for (int i = 0; i < ModuleEffectIds.Length; i++)
            {
                string effect = ModuleEffectIds[i];

                if (string.Equals(effect, LearningEffectId, StringComparison.Ordinal))
                {
                    list.Add(new ModuleEffectCellInfo { Effect = effect, Bonus = 0 });
                    list.Add(new ModuleEffectCellInfo { Effect = effect, Bonus = -2 });
                }
                else
                {
                    list.Add(new ModuleEffectCellInfo { Effect = effect, Bonus = -1 });
                }
            }

            return list.ToArray();
        }

        // ---------------------------------------------------------------- 隔离工具

        /// <summary>
        /// 快照 <c>ModuleEffectHelper.moduleEffects</c>。造物品前后包一层，
        /// 就能避免把游戏共享的随机词条池抽干。
        /// 用法：<c>var h = SnapshotModulePool(); try { ... } finally { RestoreModulePool(h); }</c>
        /// </summary>
        public static object SnapshotModulePool()
        {
            var backup = new Dictionary<string, IList>(StringComparer.Ordinal);

            try
            {
                var pool = ModuleEffectHelper.moduleEffects;
                if (pool == null) return backup;

                foreach (var pair in pool)
                {
                    // `moduleEffects` 是 Dictionary<string, ModuleEffect>，值不是 IList，
                    // 所以这里没有可快照的集合。以前为造同类型副本用了
                    // `Activator.CreateInstance(pair.Value.GetType())` —— 运行时类型反射，
                    // 会被判成 reflection-external（2026-12-10 起拒绝加载），现在直接跳过。
                    var source = pair.Value as IList;
                    if (source == null) continue;

                    backup[pair.Key] = source;
                }
            }
            catch (Exception e) { Warn("snapshot module pool: " + e.Message); }

            return backup;
        }

        /// <summary>还原文 <see cref="SnapshotModulePool"/> 取得的快照。</summary>
        public static void RestoreModulePool(object handle)
        {
            var backup = handle as Dictionary<string, IList>;
            if (backup == null || backup.Count == 0) return;

            try
            {
                var pool = ModuleEffectHelper.moduleEffects;
                if (pool == null) return;

                foreach (var pair in pool)
                {
                    IList copy;
                    if (!backup.TryGetValue(pair.Key, out copy)) continue;

                    var target = pair.Value as IList;
                    if (target == null) continue;

                    target.Clear();
                    for (int i = 0; i < copy.Count; i++) target.Add(copy[i]);
                }
            }
            catch (Exception e) { Warn("restore module pool: " + e.Message); }
        }

        /// <summary>
        /// 把物品的标签存储换成独立副本。<c>TagSystem.Clone()</c> 是浅拷贝（内部字典共用），
        /// 复用同一模板实例造出的多个变体会互相覆盖标签——造完每个变体都调一次。
        /// </summary>
        public static void IsolateTagState(GameItem item)
        {
            if (item == null) return;

            try { CopyTagDict(item.state); } catch (Exception e) { Warn("isolate state: " + e.Message); }
            try { CopyTagDict(item.modifiedState); } catch (Exception e) { Warn("isolate modifiedState: " + e.Message); }
        }

        private static void CopyTagDict(TagSystem system)
        {
            if (system == null) return;

            var field = typeof(TagSystem).GetField("<dict>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (field == null) return;

            // `TagSystem.<dict>` 的声明类型就是 Dictionary<string, TagState>，
            // 用字面类型新建副本即可，不再 `Activator.CreateInstance(dict.GetType())`
            // （运行时类型反射 = reflection-external，2026-12-10 起拒绝加载）。
            var dict = field.GetValue(system) as Dictionary<string, TagState>;
            if (dict == null) return;

            var copy = new Dictionary<string, TagState>(dict.Count, StringComparer.Ordinal);

            foreach (var entry in dict)
            {
                var state = entry.Value;
                copy[entry.Key] = state == null ? null : state.Clone() as TagState;
            }

            field.SetValue(system, copy);
        }

        /// <summary>
        /// 让「实际品质」与格子表示的状态一致：
        /// variant 为 0 时无瑕疵，为 k 时只保留第 k 处瑕疵（1-based，对应 issueTags[k-1]）。
        /// </summary>
        public static void NormalizeIssues(GameItem item, int variant, string[] issueTags)
        {
            if (item == null || issueTags == null || issueTags.Length == 0) return;

            int keep = variant - 1;

            for (int i = 0; i < issueTags.Length; i++)
            {
                bool flawed = i == keep;

                try
                {
                    if (item.IsTag(issueTags[i]) != flawed) item.SetEnableDisableTag(issueTags[i], flawed);
                }
                catch { }
            }
        }

        /// <summary>把食品券的面额强制改成指定值（名称和售价一起改，避免显示不一致）。</summary>
        public static void ForceStampAmount(GameItem item, int amount)
        {
            if (item == null) return;

            try { SetIntTag(item, "STAMP_AMOUNT", amount); } catch { }
            try { item.name = LocHelper.GetLocalizedMechanic("ins_stamp_name", new object[] { amount }); } catch { }
            try { item.unitValue = amount; } catch { }
        }

        private static void SetIntTag(GameItem item, string tag, int value)
        {
            if (item == null || string.IsNullOrEmpty(tag)) return;

            var state = item.GetTagReadonly(tag);
            if (state != null) { state.SetInt(value); return; }

            item.EnableTag(tag);
            state = item.GetTagReadonly(tag);
            if (state != null) state.SetInt(value);
        }

        /// <summary>
        /// 去掉「标签打印机」贴上去的真品/赝品那条 ItemFeature。
        /// 只清这一条特性，物品自身的分类标签（CATEGORY_*）保持不动。
        /// </summary>
        public static void StripAuthenticityLabels(GameItem item)
        {
            if (item == null) return;

            RemoveFeature(item, ItemFeatureList.CigaretteAuthenticity);
            RemoveFeature(item, ItemFeatureList.CigaretteFakeAuthenticity);
            RemoveFeature(item, ItemFeatureList.CigaretteCounterfeit);
            RemoveFeature(item, ItemFeatureList.CigaretteFakeCounterfeit);
            RemoveFeature(item, ItemFeatureList.StampAuthenticity);
            RemoveFeature(item, ItemFeatureList.StampFakeAuthenticity);
            RemoveFeature(item, ItemFeatureList.StampCounterfeit);
            RemoveFeature(item, ItemFeatureList.StampFakeCounterfeit);
        }

        private static void RemoveFeature(GameItem item, Func<ItemFeature> factory)
        {
            if (item == null || factory == null) return;

            try
            {
                var feature = factory();
                if (feature == null) return;

                string id = null;
                try { id = feature.identifier; } catch { }

                if (!string.IsNullOrEmpty(id)) item.RemoveItemFeatureByID(id);
                else item.RemoveItemFeature(feature);
            }
            catch { }
        }

        private static void Warn(string message)
        {
            if (ItemCatalog.ModEntry.Log != null) ItemCatalog.ModEntry.Log.Warning("[kit] " + message);
        }
    }
}
