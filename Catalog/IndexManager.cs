using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace ItemCatalog.Catalog
{
    /// <summary>
    /// 索引管理器：所有「算一次、后面反复查」的索引都从这里取。
    ///
    /// · 内存缓存：按名字存一份，带 <see cref="CatalogDataBuilder.Generation"/> 与
    ///   <see cref="CacheSignature"/> 两个时间戳；目录重建或游戏 / 模组变化后自动失效。
    /// · 磁盘缓存：所有可落盘的索引共用一个 <c>index_all.txt</c>，按
    ///   <c>[section:名字]</c> 分段，文件头是 <c>sig=环境签名</c> + <c>gen=generation</c>；
    ///   签名不符就整份作废、重建后写回。
    ///
    /// 各索引只负责「怎么建 / 怎么序列化」，缓存位置、失效规则、文件头都在这一处。
    /// </summary>
    internal static class IndexManager
    {
        private sealed class Entry
        {
            internal object Value;
            internal int Generation;
            internal string Signature;
        }

        private const string CacheFile = "index_all.txt";
        private const string SectionPrefix = "[section:";

        /// <summary>
        /// 已废弃的分段：枪槽探测结果现在并入 <c>item-relations</c>（kind = Gun），
        /// 旧文件里的这段在加载时直接丢弃，下次写回不会再出现。
        /// </summary>
        private static readonly string[] ObsoleteSections = { "gun-slots" };

        private static readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        /// <summary>磁盘里各索引的原始 payload（名字 → 文本），内存里常驻，保存时整份重写。</summary>
        private static readonly Dictionary<string, string> _sections =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private static bool _fileLoaded;
        private static bool _fileValid;

        /// <summary>
        /// 取内存索引；没有（或已失效）就调用 <paramref name="build"/> 建一次。
        /// 只用于能随时重建的索引；要落盘的用四参重载。
        /// </summary>
        internal static T Get<T>(string name, Func<T> build) where T : class
        {
            return Get(name, build, null, null);
        }

        /// <summary>
        /// 唯一取索引入口：内存 → 总缓存里的分段 → 重建 →（可落盘时）写回。
        /// <paramref name="serialize"/> / <paramref name="parse"/> 传 null 表示这个索引不落盘。
        /// </summary>
        internal static T Get<T>(string name, Func<T> build,
                                 Func<T, string> serialize,
                                 Func<string, T> parse) where T : class
        {
            if (string.IsNullOrEmpty(name) || build == null) return null;

            T cached = Get<T>(name);
            if (cached != null) return cached;

            if (parse != null)
            {
                T loaded;
                if (Peek(name, parse, out loaded))
                {
                    Store(name, loaded);
                    Log(name, "cache loaded", 0);
                    return loaded;
                }
            }

            var watch = Stopwatch.StartNew();
            T value = build();
            if (value == null) return null;   // 数据还没就绪（延迟重试），不缓存也不打日志

            Store(name, value);
            if (serialize != null) SaveSection(name, value, serialize);

            Log(name, "built", watch.ElapsedMilliseconds);
            return value;
        }

        /// <summary>只取内存里仍然有效的索引（不重建）。</summary>
        internal static T Get<T>(string name) where T : class
        {
            if (string.IsNullOrEmpty(name)) return null;

            Entry entry;
            if (!_entries.TryGetValue(name, out entry) || !Fresh(entry)) return null;

            return entry.Value as T;
        }

        /// <summary>作废一个索引（数据源变了、或增量补进了新条目时调用）。</summary>
        internal static void Invalidate(string name)
        {
            if (!string.IsNullOrEmpty(name)) _entries.Remove(name);
        }

        internal static void InvalidateAll()
        {
            _entries.Clear();
        }

        // ---------------------------------------------------------------- 总缓存（分段）

        /// <summary>
        /// 只读总缓存里的某个分段（内存里已常驻的原始文本），不触发重建。
        /// 典型用途：建 A 索引需要 B 索引的缓存结果，又不能递归触发 B 的构建。
        /// </summary>
        internal static bool Peek<T>(string name, Func<string, T> parse, out T value) where T : class
        {
            value = null;

            try
            {
                EnsureCacheLoaded();
                if (!_fileValid) return false;

                string payload;
                if (!_sections.TryGetValue(name, out payload) || string.IsNullOrEmpty(payload)) return false;

                value = parse(payload);
                return value != null;
            }
            catch (Exception e)
            {
                Warn("read " + name + ": " + e.Message);
                return false;
            }
        }

        private static void SaveSection<T>(string name, T value, Func<T, string> serialize) where T : class
        {
            if (string.IsNullOrEmpty(name) || value == null || serialize == null) return;

            try
            {
                EnsureCacheLoaded();

                string payload = serialize(value);
                if (string.IsNullOrEmpty(payload)) return;

                _sections[name] = payload;
                WriteCache();

                Log(name, "saved to " + CacheFile + " (" + payload.Length + " chars)", 0);
            }
            catch (Exception e)
            {
                Warn("write " + name + ": " + e.Message);
            }
        }

        private static void EnsureCacheLoaded()
        {
            if (_fileLoaded) return;
            _fileLoaded = true;
            _fileValid = false;

            try
            {
                if (!ModHelper.ModDataFileExists(ModEntry.MOD_ID, CacheFile)) return;

                string text = ModHelper.ReadModDataFileText(ModEntry.MOD_ID, CacheFile);
                if (string.IsNullOrEmpty(text)) return;

                string signature = null;
                string generation = null;
                string current = null;
                var sections = new Dictionary<string, string>(StringComparer.Ordinal);
                var body = new StringBuilder();

                var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];

                    if (line.StartsWith("sig=", StringComparison.Ordinal))
                    {
                        signature = line.Substring(4).Trim();
                        continue;
                    }
                    if (line.StartsWith("gen=", StringComparison.Ordinal))
                    {
                        generation = line.Substring(4).Trim();
                        continue;
                    }
                    if (line.StartsWith(SectionPrefix, StringComparison.Ordinal) &&
                        line[line.Length - 1] == ']')
                    {
                        if (current != null) sections[current] = body.ToString();

                        current = line.Substring(SectionPrefix.Length,
                                                 line.Length - SectionPrefix.Length - 1).Trim();
                        body.Length = 0;
                        continue;
                    }

                    if (current != null) body.Append(line).Append('\n');
                }

                if (current != null) sections[current] = body.ToString();

                if (!string.Equals(signature, CacheSignature.Value, StringComparison.Ordinal)) return;
                if (!string.Equals(generation, CatalogDataBuilder.Generation.ToString(), StringComparison.Ordinal))
                    return;

                _sections.Clear();
                foreach (var pair in sections) _sections[pair.Key] = pair.Value;

                for (int i = 0; i < ObsoleteSections.Length; i++) _sections.Remove(ObsoleteSections[i]);

                _fileValid = true;
            }
            catch (Exception e)
            {
                Warn("load cache: " + e.Message);
            }
        }

        private static void WriteCache()
        {
            var text = new StringBuilder();
            text.Append("sig=").Append(CacheSignature.Value).Append('\n');
            text.Append("gen=").Append(CatalogDataBuilder.Generation).Append('\n');

            foreach (var pair in _sections)
            {
                if (string.IsNullOrEmpty(pair.Key)) continue;

                text.Append(SectionPrefix).Append(pair.Key).Append("]\n");

                string payload = pair.Value ?? string.Empty;
                text.Append(payload);
                if (payload.Length == 0 || payload[payload.Length - 1] != '\n') text.Append('\n');
            }

            ModHelper.WriteModDataFileText(ModEntry.MOD_ID, CacheFile, text.ToString());
        }

        // ---------------------------------------------------------------- 内部

        private static bool Fresh(Entry entry)
        {
            if (entry == null) return false;
            if (entry.Generation != CatalogDataBuilder.Generation) return false;

            string current = CacheSignature.Value;
            return ReferenceEquals(entry.Signature, current)
                   || string.Equals(entry.Signature, current, StringComparison.Ordinal);
        }

        private static void Store(string name, object value)
        {
            _entries[name] = new Entry
            {
                Value = value,
                Generation = CatalogDataBuilder.Generation,
                Signature = CacheSignature.Value
            };
        }

        private static void Log(string name, string what, long ms)
        {
            try
            {
                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[index] " + name + " " + what + (ms > 0 ? " (" + ms + " ms)" : ""));
            }
            catch { }
        }

        private static void Warn(string message)
        {
            try { if (ModEntry.Log != null) ModEntry.Log.Warning("[index] " + message); } catch { }
        }
    }
}
