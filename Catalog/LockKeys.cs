using System;
using System.Collections.Generic;
using System.Reflection;

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
    /// 注意：**卡物品不带 `LOCK_ID` 标签**——游戏里「锁 id → 卡」的配对是
    /// `StoreClientList.lockToKeycard` / `lockToCryptoModule` 两张静态字典
    /// （cctor 里 Add(LOCK_ID_SEC, sec_keycard) 这一批）。这里把字典读过来，
    /// 读不到就用 IL 里抄的表兜底，供来源页显示「开这个箱子要哪张卡」。
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

        /// <summary>
        /// 建表：先拿游戏的「锁 id → 卡」静态字典（卡不带 LOCK_ID 标签，
        /// 只扫目录一张卡都扫不到），拿不到退回 IL 抄的表；然后目录扫描
        /// 只用来**补充**游戏字典之外的条目（例如带 LOCK_ID 的非卡钥匙）。
        /// </summary>
        private static void EnsureBuilt()
        {
            if (_byLockId != null) return;

            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            SeedGameLockMap(map);

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

        /// <summary>
        /// 把游戏的「锁 id → 卡」配对放进表：优先反射读
        /// <c>StoreClientList.lockToKeycard</c> 静态字典（跟着游戏版本走），
        /// 反射失败用 2026-11-15 从 <c>StoreClientList..cctor</c> IL 抄的表兜底。
        /// </summary>
        private static void SeedGameLockMap(Dictionary<string, string> map)
        {
            try
            {
                var field = typeof(StoreClientList).GetField(
                    "lockToKeycard", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);

                if (field != null && field.GetValue(null) is System.Collections.IDictionary dict)
                {
                    foreach (System.Collections.DictionaryEntry e in dict)
                    {
                        var k = e.Key as string;
                        var v = e.Value as string;
                        if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v))
                            map[k] = v;
                    }

                    return;
                }
            }
            catch { /* 反射失败走兜底表 */ }

            // 兜底：StoreClientList..cctor 的原始配对（LOCK_ID_COMMAND 万能，也配了命令卡）
            var seed = new[]
            {
                new[] { "LOCK_ID_SEC", "sec_keycard" },
                new[] { "LOCK_ID_COMMAND", "cmd_keycard" },
                new[] { "LOCK_ID_MED", "med_keycard" },
                new[] { "LOCK_ID_ENG", "eng_keycard" },
                new[] { "LOCK_ID_SUP", "sup_keycard" },
                new[] { "LOCK_ID_SER", "ser_keycard" }
            };

            for (int i = 0; i < seed.Length; i++)
                if (!map.ContainsKey(seed[i][0])) map[seed[i][0]] = seed[i][1];
        }
    }
}
