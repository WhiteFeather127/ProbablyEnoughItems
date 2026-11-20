using System;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 枪械翻新：枪械零件（`gun_part`）用在枪上，按品相逐档提升。
    ///
    /// 品相不是标签，而是游戏的 `CATEGORY_EQUIPMENT_CONDITION` 特性，
    /// 索引固定 `<see cref="SetEquipmentCondition"/>` 的入参：
    /// 0 全新 / 1 极佳 / 2 良好 / 3 一般 / 4 劣。
    /// 翻新只作用于「一般 / 良好 / 劣」，成功后上升一档；全新与极佳不可翻新。
    ///
    /// 显示名直接读原版 <c>ItemCondition.display</c>（游戏按当前语言翻译好了），
    /// 不在模组里另维护一份品相文案。
    /// </summary>
    internal static class GunUpgrades
    {
        internal const int ConditionNew = 0;
        internal const int ConditionExcellent = 1;
        internal const int ConditionGood = 2;
        internal const int ConditionFair = 3;
        internal const int ConditionPoor = 4;

        /// <summary>可翻新的输入品相，从低到高；每一项翻新后上升一档。</summary>
        internal static readonly int[] Refurbishable = { ConditionPoor, ConditionFair, ConditionGood };

        /// <summary>
        /// 拿不到原版显示名时的兜底（原版 `mech_condition_equip_*` 的中文）：
        /// 全新 / 极佳 / 良好 / 一般 / 较差。
        /// </summary>
        private static readonly string[] FallbackNames = { "全新", "极佳", "良好", "一般", "较差" };

        private static string[] _names;

        internal static bool IsGun(GameItem item)
        {
            if (item == null) return false;

            try { return item.IsTag("ADV_GUN_TAG"); }
            catch { return false; }
        }

        /// <summary>品相的本地化显示名（原版 `ItemCondition.display`）。</summary>
        internal static string ConditionName(int condition)
        {
            var names = Names();
            int i = condition < 0 ? 0 : (condition >= names.Length ? names.Length - 1 : condition);
            return names[i];
        }

        private static string[] Names()
        {
            if (_names != null) return _names;

            var names = new string[FallbackNames.Length];

            for (int i = 0; i < names.Length; i++)
            {
                string display = null;

                try
                {
                    var feature = ItemFeatureList.EquipementConditionFeature(
                        (ItemFeatureList.EquipmentCondition)i);
                    var condition = feature == null ? null : feature.realCondition;
                    display = condition == null ? null : condition.display;
                }
                catch { }

                names[i] = string.IsNullOrEmpty(display) ? FallbackNames[i] : display;
            }

            _names = names;
            return names;
        }

        /// <summary>
        /// 初始翻新概率（%）。游戏里是 `基础可修复性 + 失败累计值`，
        /// 预览实例的累计值为 0，所以这里就是基础值；失败后的累加不展示。
        /// </summary>
        internal static int Chance(string id)
        {
            var entry = CatalogWindow.GunConditionEntry(id, ConditionGood);
            var item = entry == null ? null : entry.Item;
            if (item == null) return 0;

            try { return GunHelper.GetRefurbishChance(item); }
            catch { return 0; }
        }

        /// <summary>
        /// 造一个指定品相的预览实例。实例化统一走
        /// <see cref="CatalogWindow.CreatePreviewItem(string)"/> 的隔离随机流，
        /// 之后写品相特性（这一步只是改标签与数值，不掷随机数）。
        /// </summary>
        internal static GameItem Create(string id, int condition)
        {
            var item = CatalogWindow.CreatePreviewItem(id);
            if (item == null) return null;

            try
            {
                GunHelper.SetEquipmentCondition(item,
                    (ItemFeatureList.EquipmentCondition)condition);
            }
            catch (Exception e)
            {
                if (ModEntry.Log != null)
                    ModEntry.Log.Warning("[gun] set condition '" + id + "' #" + condition + ": " + e.Message);
            }

            return item;
        }
    }
}
