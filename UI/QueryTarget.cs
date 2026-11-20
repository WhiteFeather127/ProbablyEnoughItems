using System;

using ItemCatalog.Catalog;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 「详情窗口正在查的那件东西」的唯一入口。
    ///
    /// 所有会轮播的格子（标签条、枪械配件槽、机器面板）都要问它两件事：
    ///   ① <see cref="IsQueried"/>：这一格是不是当前物品 → 是就垫绿底；
    ///   ② 同一个判定也用来决定「不要转走」：被查的东西所在的轮播面必须固定停在它上面。
    /// 以前的写法是各页自己拿 <c>_entry</c> 判断、各自钉住，行为容易不一致；
    /// 统一收在这里之后，绿底和停轮播走的是同一份数据。
    /// </summary>
    internal static class QueryTarget
    {
        private static ItemEntry _entry;

        internal static ItemEntry Entry { get { return _entry; } }
        internal static string Id { get { return _entry == null ? null : _entry.Id; } }
        internal static GameItem Item { get { return _entry == null ? null : _entry.Item; } }
        internal static bool Active { get { return _entry != null; } }

        internal static void Set(ItemEntry entry)
        {
            _entry = entry;
        }

        /// <summary>
        /// 这件物品是不是当前被查的那件：同 id，或同一本体
        /// （`portable_water_purifier_used` 这类变体后缀 / 占位 id 都按本体算）。
        /// </summary>
        internal static bool IsQueried(string id)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(Id)) return false;
            if (string.Equals(id, Id, StringComparison.Ordinal)) return true;

            string baseId = RecipeWindow.BaseItemId(id);
            if (!string.IsNullOrEmpty(baseId) &&
                string.Equals(baseId, Id, StringComparison.Ordinal)) return true;

            string entryBase = RecipeWindow.BaseItemId(Id);
            return !string.IsNullOrEmpty(baseId) && !string.IsNullOrEmpty(entryBase) &&
                   string.Equals(baseId, entryBase, StringComparison.Ordinal);
        }
    }
}
