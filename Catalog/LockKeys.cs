using System;
using System.Collections.Generic;

namespace ItemCatalog.Catalog
{
    /// <summary>
    /// 「这个箱子要用哪张卡开」——照 <c>LockHelper</c> 的规则实现：
    ///
    /// <code>
    /// GetLock(item):        item.IsTag("LOCKED_CONTAINER_TAG")
    ///                         ? item.GetTagReadonly("LOCK_ID").GetString()
    ///                         : ""
    /// CanOpenLock(box, key): key == "LOCK_ID_COMMAND"  → true（命令卡万能）
    ///                        else GetLock(box) == key
    /// </code>
    ///
    /// 所以卡物品自己带着一个 `LOCK_ID` 标签值，和箱子的锁 id 相等就能开。
    /// 这里把「锁 id → 卡物品 id」做成一张表，供来源页显示「开这个箱子要哪张卡」。
    /// </summary>
    internal static class LockKeys
    {
        private const string LockedTag = "LOCKED_CONTAINER_TAG";
        private const string LockIdTag = "LOCK_ID";
        private const string CommandLockId = "LOCK_ID_COMMAND";

        private static Dictionary<string, string> _byLockId;

        /// <summary>箱子需要哪张卡；没锁 / 找不到卡返回 null。</summary>
        internal static string KeyForBox(GameItem box)
        {
            string lockId = LockIdOf(box);
            if (string.IsNullOrEmpty(lockId)) return null;

            EnsureBuilt();

            if (_byLockId == null) return null;

            string keyId;
            return _byLockId.TryGetValue(lockId, out keyId) ? keyId : null;
        }

        /// <summary>读箱子的锁 id（箱子没锁返回 null）。</summary>
        internal static string LockIdOf(GameItem box)
        {
            if (box == null) return null;

            try
            {
                if (!box.IsTag(LockedTag)) return null;
                return TagLockId(box);
            }
            catch { return null; }
        }

        /// <summary>
        /// 直接读 `LOCK_ID` 标签的值，不判断是不是箱子 ——
        /// 卡片自己也带这个标签，值就是它能开的锁 id。
        /// </summary>
        internal static string TagLockId(GameItem item)
        {
            if (item == null) return null;

            try
            {
                var tag = item.GetTagReadonly(LockIdTag);
                if (tag == null) return null;

                string value = tag.GetString();
                return string.IsNullOrEmpty(value) ? null : value;
            }
            catch { return null; }
        }

        /// <summary>这张卡能不能开那个锁（命令卡万能）。</summary>
        internal static bool CanOpen(string keyLockId, string boxLockId)
        {
            if (string.IsNullOrEmpty(keyLockId) || string.IsNullOrEmpty(boxLockId)) return false;
            if (IsMasterKey(keyLockId)) return true;

            return string.Equals(keyLockId, boxLockId, StringComparison.Ordinal);
        }

        /// <summary>命令卡能开所有锁，来源页额外提示它。</summary>
        internal static bool IsMasterKey(string keyId)
        {
            return string.Equals(keyId, CommandLockId, StringComparison.Ordinal);
        }

        /// <summary>扫一遍目录，把「带 LOCK_ID 标签的物品」按锁 id 建表。</summary>
        private static void EnsureBuilt()
        {
            if (_byLockId != null) return;

            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            try
            {
                foreach (var entry in RecipeIndex.AllEntries)
                {
                    if (entry == null) continue;

                    var item = entry.Item;
                    if (item == null) continue;

                    try
                    {
                        // 箱子自己也有 LOCK_ID，别把它当卡
                        if (item.IsTag(LockedTag)) continue;

                        var tag = item.GetTagReadonly(LockIdTag);
                        if (tag == null) continue;

                        string value = tag.GetString();
                        if (string.IsNullOrEmpty(value)) continue;

                        if (!map.ContainsKey(value)) map[value] = entry.Id;
                    }
                    catch { }
                }
            }
            catch (Exception e)
            {
                if (ModEntry.Log != null) ModEntry.Log.Log("[lock] scan failed: " + e.Message);
                return;
            }

            // 目录可能还没建完（第一次调用常发生在 Build 之前），
            // 这时拿到的是空表 —— **不要**缓存，否则后面永远是 0。
            if (map.Count == 0)
            {
                if (ModEntry.Log != null) ModEntry.Log.Log("[lock] keycards=0 (retry later)");
                return;
            }

            _byLockId = map;

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[lock] keycards=" + map.Count);
        }
    }
}
