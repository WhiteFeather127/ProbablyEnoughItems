using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

using UnityEngine;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 把来源表里的 sprite 列解析成客人立绘。
    ///
    /// 三种写法：
    ///   · 固定 key（george / wanted1 / officer_lun …）→ 单张
    ///   · "pool:方法名" → 从 SpriteDict 的静态方法里读出它用到的所有池前缀
    ///     （GetRandomScavSpriteName → maleScav + femaleScav），男女一起返回
    ///   · "pool:maleScav|femaleScav" → 直接指定池前缀
    ///
    /// 同一客人往往有多种外观，所以对外给的是**列表**，详情窗口里每秒轮播一张
    /// （见 RecipeWindow.TickPortrait）。不同 key 指向同一张精灵图时只算一张。
    ///
    /// 取图不走 GetPool：它的循环从 i = 1 开始无条件 Add、遇到不存在的编号就停，
    /// **#0 永远不在结果里**，池稍小就只剩一两张（轮播看起来像没生效）。
    /// 改为直接读 SpriteDict 上的 List&lt;SpriteEntry&gt;（scavMaleEntries / lowerFemaleEntries …），
    /// 每个条目自带 key + sprite，顺序就是配置顺序，也不依赖懒加载的 spriteDictionary。
    /// </summary>
    internal static class ClientSprite
    {
        private static readonly Dictionary<string, List<Sprite>> _lists =
            new Dictionary<string, List<Sprite>>(StringComparer.Ordinal);

        // SpriteDict 上所有 List<SpriteEntry> 字段，以及 SpriteEntry 的 key / sprite
        private static FieldInfo[] _entryFields;
        private static FieldInfo _entryKey;
        private static FieldInfo _entrySprite;

        /// <summary>该来源的第一张立绘（没有则 null）。</summary>
        internal static Sprite For(string spec)
        {
            var list = AllFor(spec);
            return list != null && list.Count > 0 ? list[0] : null;
        }

        /// <summary>该来源对应的全部立绘，按配置顺序排好，同一张精灵图只出现一次。</summary>
        internal static List<Sprite> AllFor(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return null;

            List<Sprite> list;
            if (_lists.TryGetValue(spec, out list)) return list;

            try
            {
                list = Collect(spec);
                if (list == null || list.Count == 0) return null;

                _lists[spec] = list;

                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[portrait] " + spec + " -> " + list.Count + " sprites");

                return list;
            }
            catch (Exception e)
            {
                Warn("'" + spec + "': " + e.Message);
                return null;
            }
        }

        /// <summary>清掉缓存（换档 / 精灵字典重建后调用）。</summary>
        internal static void Clear()
        {
            _lists.Clear();
            _entryFields = null;
        }

        // ---------------------------------------------------------------- 解析

        /// <summary>
        /// `pool:方法名` 里的方法名 → `SpriteDict` 上的静态方法。
        /// 每个分支都是**字面方法名**：以前是 `GetMethod(rest, …)`，名字来自配置，
        /// 扫描器按「拼接出的名称」判成 reflection-external（2026-12-10 起直接拒绝加载）。
        /// 新增池方法时在这里补一行即可。
        /// </summary>
        private static MethodInfo PoolMethod(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            const BindingFlags F = BindingFlags.Public | BindingFlags.Static;
            var none = Type.EmptyTypes;

            switch (name)
            {
                case "GetRandomScavSpriteName":
                    return typeof(SpriteDict).GetMethod("GetRandomScavSpriteName", F, null, none, null);
                case "GetRandomScavMaleSpriteName":
                    return typeof(SpriteDict).GetMethod("GetRandomScavMaleSpriteName", F, null, none, null);
                case "GetRandomLowerLevelSpriteName":
                    return typeof(SpriteDict).GetMethod("GetRandomLowerLevelSpriteName", F, null, none, null);
                case "GetRandomUpperSpriteName":
                    return typeof(SpriteDict).GetMethod("GetRandomUpperSpriteName", F, null, none, null);
                case "GetRandomMerchantSpriteName":
                    return typeof(SpriteDict).GetMethod("GetRandomMerchantSpriteName", F, null, none, null);
                case "GetShadyMerchantSpriteName":
                    return typeof(SpriteDict).GetMethod("GetShadyMerchantSpriteName", F, null, none, null);
                case "GetRandomFarmerSpriteName":
                    return typeof(SpriteDict).GetMethod("GetRandomFarmerSpriteName", F, null, none, null);
                case "GetRandomScientistSpriteName":
                    return typeof(SpriteDict).GetMethod("GetRandomScientistSpriteName", F, null, none, null);
                case "GetRandomShadySpriteName":
                    return typeof(SpriteDict).GetMethod("GetRandomShadySpriteName", F, null, none, null);
                case "GetRandomElderSpriteName":
                    return typeof(SpriteDict).GetMethod("GetRandomElderSpriteName", F, null, none, null);
                case "getRandomPilotSpriteName":
                    return typeof(SpriteDict).GetMethod("getRandomPilotSpriteName", F, null, none, null);
                case "GetRandomMercSpriteName":
                    return typeof(SpriteDict).GetMethod("GetRandomMercSpriteName", F, null, none, null);
                case "GetRandomMinerSpriteName":
                    return typeof(SpriteDict).GetMethod("GetRandomMinerSpriteName", F, null, none, null);
                case "GetRandomMedSpriteName":
                    return typeof(SpriteDict).GetMethod("GetRandomMedSpriteName", F, null, none, null);
                case "GetRandomSecSpriteName":
                    return typeof(SpriteDict).GetMethod("GetRandomSecSpriteName", F, null, none, null);
                case "GetRandomRevSpriteName":
                    return typeof(SpriteDict).GetMethod("GetRandomRevSpriteName", F, null, none, null);
                case "GetCourrierSpriteName":
                    return typeof(SpriteDict).GetMethod("GetCourrierSpriteName", F, null, none, null);
                case "GetInventorSpriteName":
                    return typeof(SpriteDict).GetMethod("GetInventorSpriteName", F, null, none, null);
                case "GetClerkSpriteName":
                    return typeof(SpriteDict).GetMethod("GetClerkSpriteName", F, null, none, null);
                case "GetCartelHandlerSpriteName":
                    return typeof(SpriteDict).GetMethod("GetCartelHandlerSpriteName", F, null, none, null);
                case "GetAshishSpriteName":
                    return typeof(SpriteDict).GetMethod("GetAshishSpriteName", F, null, none, null);
                case "GetRetiredAquafarmerSpriteName":
                    return typeof(SpriteDict).GetMethod("GetRetiredAquafarmerSpriteName", F, null, none, null);
                case "GetRetiredChemistSpriteName":
                    return typeof(SpriteDict).GetMethod("GetRetiredChemistSpriteName", F, null, none, null);
                case "GetRetiredFarmerSpriteName":
                    return typeof(SpriteDict).GetMethod("GetRetiredFarmerSpriteName", F, null, none, null);
                default:
                    return null;
            }
        }

        private static List<Sprite> Collect(string spec)
        {
            if (!spec.StartsWith("pool:", StringComparison.Ordinal))
                return FromEntries(new[] { spec }) ?? Single(spec);

            string rest = spec.Substring(5);
            if (rest.Length == 0) return null;

            // 显式写死池名：pool:maleScav|femaleScav
            if (rest.IndexOf('|') >= 0)
                return FromPrefixes(rest.Split('|'));

            // 方法名 → 从 IL 里读出它用到的所有池前缀
            var mi = PoolMethod(rest);

            if (mi == null || mi.ReturnType != typeof(string))
                return FromPrefixes(new[] { rest });      // 其实直接写了池前缀

            var prefixes = PrefixesOf(mi);
            if (prefixes.Count > 0) return FromPrefixes(prefixes);

            // IL 里没读到（或没有）GetPool：调一次拿 key。
            // 含 '#' 的截出前缀再展开整池（至少能拿到这一边全部变体），否则就是固定 key。
            var fallback = mi.Invoke(null, null) as string;
            if (string.IsNullOrEmpty(fallback)) return null;

            int hash = fallback.IndexOf('#');
            return hash > 0
                ? FromPrefixes(new[] { fallback.Substring(0, hash) })
                : (FromEntries(new[] { fallback }) ?? Single(fallback));
        }

        private static List<Sprite> FromPrefixes(IEnumerable<string> prefixes)
        {
            return FromEntries(prefixes) ?? FromPools(prefixes);
        }

        /// <summary>
        /// 首选：直接扫 SpriteDict 的 List&lt;SpriteEntry&gt;。
        /// 每个条目自带 key（maleScav#0）和 sprite，男女池都能命中，且含 #0。
        /// </summary>
        private static List<Sprite> FromEntries(IEnumerable<string> prefixes)
        {
            var inst = SpriteDict.Instance;
            if (inst == null) return null;

            EnsureEntryAccessors();
            if (_entryFields == null) return null;

            var list = new List<Sprite>();
            var seen = new HashSet<Sprite>();
            bool hit = false;

            for (int f = 0; f < _entryFields.Length; f++)
            {
                IEnumerable entries;
                try { entries = _entryFields[f].GetValue(inst) as IEnumerable; }
                catch { continue; }
                if (entries == null) continue;

                foreach (var item in entries)
                {
                    if (item == null) continue;

                    string key;
                    Sprite sprite;
                    try
                    {
                        key = _entryKey.GetValue(item) as string;
                        sprite = _entrySprite.GetValue(item) as Sprite;
                    }
                    catch { continue; }

                    if (sprite == null || string.IsNullOrEmpty(key)) continue;
                    if (!Matches(key, prefixes)) continue;

                    hit = true;
                    if (seen.Add(sprite)) list.Add(sprite);   // 同一张精灵图只算一张
                }
            }

            return hit && list.Count > 0 ? list : null;
        }

        /// <summary>
        /// 兜底：条目表结构对不上时退回 GetPool。它从 #1 开始编号，
        /// 所以手动补一个 #0，再按字典里实际存在的 key 去重。
        /// </summary>
        private static List<Sprite> FromPools(IEnumerable<string> prefixes)
        {
            var inst = SpriteDict.Instance;
            if (inst == null) return null;

            var keys = new List<string>();
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var p in prefixes)
            {
                if (string.IsNullOrEmpty(p)) continue;

                var zero = p + "#0";
                if (seenKeys.Add(zero)) keys.Add(zero);

                var pool = SpriteDict.GetPool(p, int.MaxValue);
                if (pool == null) continue;

                for (int i = 0; i < pool.Count; i++)
                {
                    var k = pool[i];
                    if (!string.IsNullOrEmpty(k) && seenKeys.Add(k)) keys.Add(k);
                }
            }

            var list = new List<Sprite>();
            var seen = new HashSet<Sprite>();
            for (int i = 0; i < keys.Count; i++)
            {
                var sprite = inst.GetSprite(keys[i]);
                if (sprite == null) continue;
                if (seen.Add(sprite)) list.Add(sprite);
            }

            return list.Count > 0 ? list : null;
        }

        private static List<Sprite> Single(string key)
        {
            var inst = SpriteDict.Instance;
            if (inst == null) return null;

            var sprite = inst.GetSprite(key);
            return sprite != null ? new List<Sprite> { sprite } : null;
        }

        /// <summary>key 是否属于某个池：等于前缀本身，或前缀 + '#' + 编号。</summary>
        private static bool Matches(string key, IEnumerable<string> prefixes)
        {
            foreach (var p in prefixes)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (key.Length < p.Length) continue;
                if (string.CompareOrdinal(key, 0, p, 0, p.Length) != 0) continue;
                if (key.Length == p.Length) return true;          // 固定 key
                char next = key[p.Length];
                if (next == '#') return true;                      // prefix#N
                // 也认不带 '#' 的编号变体：scavElder2 / scavElder10。
                // 老拾荒客这类 `GetRandomElderSpriteName` 返回的是固定 key
                // （IL 里 ldstr "scavElder"），若图集里按 scavElder2 这样编号，
                // 原先只认 '#' 就会漏掉，轮播看起来只有一张。
                if (next >= '0' && next <= '9') return true;
            }
            return false;
        }

        /// <summary>
        /// 从 GetRandomXxxSpriteName 的 IL 里读出所有 GetPool 的第一个实参。
        /// 这些方法就是 ldstr "maleScav" / ldc.i4 int.MaxValue / call GetPool 的重复，
        /// 所以能一次拿到男女两个池（调方法本身只会 PickGendered 选一边）。
        /// </summary>
        private static List<string> PrefixesOf(MethodInfo method)
        {
            return PrefixesOf(method, 0);
        }

        /// <summary>
        /// 从 GetRandomXxxSpriteName 的 IL 里读出所有 GetPool 的第一个实参。
        /// 这些方法就是 ldstr "maleScav" / ldc.i4 int.MaxValue / call GetPool 的重复，
        /// 所以能一次拿到男女两个池（调方法本身只会 PickGendered 选一边）。
        /// 有的方法只是转发（`getRandomPilotSpriteName` → `GetRandomLowerLevelSpriteName`），
        /// 自己 IL 里一个池名都没有，所以还要跟着调用走一层。
        /// </summary>
        private static List<string> PrefixesOf(MethodInfo method, int depth)
        {
            var prefixes = new List<string>();
            if (method == null || depth > 3) return prefixes;

            try
            {
                var body = method.GetMethodBody();
                if (body == null) return prefixes;

                var il = body.GetILAsByteArray();
                if (il == null) return prefixes;

                var module = method.Module;

                // ldstr(0x72) token ; ldc.i4(0x20) int.MaxValue ; call(0x28) token
                for (int i = 0; i + 14 < il.Length; i++)
                {
                    if (il[i] != 0x72) continue;                       // ldstr
                    if (il[i + 5] != 0x20) continue;                   // ldc.i4
                    if (BitConverter.ToInt32(il, i + 6) != int.MaxValue) continue;
                    if (il[i + 10] != 0x28 && il[i + 10] != 0x6F) continue;   // call / callvirt

                    try
                    {
                        var target = module.ResolveMethod(BitConverter.ToInt32(il, i + 11)) as MethodInfo;
                        if (target == null || target.Name != "GetPool") continue;

                        var prefix = module.ResolveString(BitConverter.ToInt32(il, i + 1));
                        if (!string.IsNullOrEmpty(prefix) && !prefixes.Contains(prefix))
                            prefixes.Add(prefix);
                    }
                    catch { }
                }

                if (prefixes.Count > 0) return prefixes;

                // 本方法里没有池名 —— 跟着无参 string 转发走一层
                for (int i = 0; i + 5 <= il.Length; i++)
                {
                    byte op = il[i];
                    if (op != 0x28 && op != 0x6F) continue;

                    MethodInfo target;
                    try { target = module.ResolveMethod(BitConverter.ToInt32(il, i + 1)) as MethodInfo; }
                    catch { i += 4; continue; }

                    if (target == null
                        || target.DeclaringType != typeof(SpriteDict)
                        || target.ReturnType != typeof(string)
                        || target.GetParameters().Length != 0
                        || ReferenceEquals(target, method))
                    {
                        i += 4;
                        continue;
                    }

                    foreach (var nested in PrefixesOf(target, depth + 1))
                    {
                        if (!prefixes.Contains(nested)) prefixes.Add(nested);
                    }

                    if (prefixes.Count > 0) return prefixes;
                    i += 4;
                }
            }
            catch { }

            return prefixes;
        }

        private static void EnsureEntryAccessors()
        {
            if (_entryFields != null) return;

            try
            {
                // 全程字面类型：`SpriteDict.SpriteEntry` 是公开的嵌套类型，
                // `List<SpriteDict.SpriteEntry>` 也是字面量。
                // 之前靠 `f.FieldType.GetGenericArguments()[0]` 取类型，属于「运行时才知道的类型」，
                // 会被扫描器判成 reflection-external（2026-12-10 起直接拒绝加载）。
                var entryList = typeof(List<SpriteDict.SpriteEntry>);

                var fields = new List<FieldInfo>();

                foreach (var f in typeof(SpriteDict).GetFields(
                             BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (f.FieldType != entryList) continue;
                    fields.Add(f);
                }

                if (fields.Count == 0) return;

                _entryKey = typeof(SpriteDict.SpriteEntry).GetField("key",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _entrySprite = typeof(SpriteDict.SpriteEntry).GetField("sprite",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (_entryKey == null || _entrySprite == null) return;

                _entryFields = fields.ToArray();
            }
            catch
            {
                _entryFields = null;
            }
        }

        private static void Warn(string message)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("[client-sprite] " + message);
        }
    }
}
