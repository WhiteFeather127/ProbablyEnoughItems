using System;
using System.Collections.Generic;
using System.Text;

namespace ItemCatalog.Catalog
{
    /// <summary>
    /// 来源表配置读取（随包只读）：
    ///   <c>Data/clients.csv</c> — 阵营分节 + 一行一个客人（名字 / 立绘 / 买卖清单都在这一张表）
    ///   <c>Data/goods.csv</c>   — **已废弃**。它的列已并入 clients.csv 第 12~16 列，
    ///                             文件不再随包分发；老安装目录里残留的仍会被读进来叠加，
    ///                             删掉也完全不影响。
    ///
    /// 必须用 <c>ModHelper.ReadModFileText</c> 而不是 <c>System.IO.File</c>：
    /// 模组扫描器把后者判为 <c>files</c> 能力，而本模组 manifest 没有声明能力，
    /// 直接读会导致玩家侧拒绝激活。改表后重启游戏即可生效，不需要重新编译。
    ///
    /// clients.csv 列顺序（前 7 列必填，8~16 可选）：
    /// kind,id,name_key,location_id,fallback_key,profession,sprite,
    /// water,empty,buys_tags,buys_items,sells,base,exclude,random_items,dynamic
    ///   kind=faction 行是**阵营分节**，id 列填官方本地化 key，其后所有客人行都继承它；
    ///   kind=client / location 行是一个来源的元数据，不带 faction 列。
    /// 旧的 goods.csv 列顺序（6 列，仅供残留文件兼容解析）：
    /// id,base,exclude,items,random_items,dynamic
    /// </summary>
    internal static partial class SourceIndex
    {
        private const string ClientsCsvPath = "Data/clients.csv";
        private const string GoodsCsvPath = "Data/goods.csv";
        private const int ClientColumnCount = 7;
        private const int GoodsColumnCount = 6;

        /// <summary>配置里 dynamic=commissary 那一行生成的来源对象，运行时往里补商品。</summary>
        private static SourceInfo _commissarySource;

        /// <summary>
        /// 解析结果缓存。CSV 随包只读，进程内不会变；但配给站换档会触发
        /// <c>Invalidate()</c> 重建，没必要每次重新读文件。
        /// 读失败时**不**缓存，留出下次重试的机会。
        /// </summary>
        private static HardcodedSource[] _cachedDefinitions;

        /// <summary>上一次解析时跳过的 <c>kind=unused</c> 行数（只用于日志）。</summary>
        internal static int UnusedRowCount { get; private set; }

        /// <summary>上一次解析时跳过的 <c>kind=alias</c> 行数（只用于日志）。</summary>
        internal static int AliasRowCount { get; private set; }

        /// <summary>
        /// 读两张表并按 id 拼成定义列表。单行出错只跳过该行并记日志，
        /// 任一文件缺失 / 读取失败时返回空表（图鉴其他功能不受影响）。
        /// </summary>
        private static HardcodedSource[] LoadDefinitions()
        {
            // 每次重建都要换一个新的 SourceInfo（AllSources 已被清空），不能沿用上次的
            _commissarySource = null;
            if (_cachedDefinitions != null) return _cachedDefinitions;

            string clientsText = ReadTable(ClientsCsvPath);
            if (clientsText == null) return new HardcodedSource[0];

            // goods.csv 的列已全部并入 clients.csv 第 12~16 列，文件不再随包分发；
            // 老安装目录里残留的那份仍然会被读进来叠加，删掉也完全不影响。
            string goodsText = ReadTable(GoodsCsvPath);

            var defs = ParseClients(clientsText);
            var goods = goodsText == null
                ? new Dictionary<string, string[]>(StringComparer.Ordinal)
                : ParseGoods(goodsText);
            var byId = new Dictionary<string, HardcodedSource>(StringComparer.Ordinal);
            for (int i = 0; i < defs.Count; i++) byId[defs[i].Id] = defs[i];

            // 把货单挂到对应来源上；goods.csv 里写了但客人表里没有的 id 要报出来
            var used = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < defs.Count; i++)
            {
                var def = defs[i];
                string[] row;

                // 第 12 列 sells 已经能单独成立：没有 goods.csv 行也不算错。
                // 两张表合并后 clients.csv 才是主表，goods.csv 只作为兼容保留。
                var sells = SplitIds(def.Sells);

                if (!goods.TryGetValue(def.Id, out row))
                {
                    if (sells.Length > 0) def.Items = sells;
                    else if (def.Kind == SourceKind.Client)
                        Warn("'" + def.Id + "' 既没有货单行，也没有第 12 列 sells");
                    continue;
                }

                used.Add(def.Id);
                // 两边都可能有值：clients.csv 的第 12~16 列是主，goods.csv 行只做叠加/兼容
                if (string.IsNullOrEmpty(def.Base)) def.Base = NullIfEmpty(Cell(row, 1));
                def.Exclude = Concat(def.Exclude, SplitIds(Cell(row, 2)));
                def.Items = Concat(Concat(SplitIds(Cell(row, 3)), sells), def.Items);
                def.RandomItems = Concat(def.RandomItems, SplitIds(Cell(row, 4)));
                if (string.IsNullOrEmpty(def.Dynamic)) def.Dynamic = NullIfEmpty(Cell(row, 5));
            }

            foreach (var pair in goods)
            {
                if (!used.Contains(pair.Key))
                    Warn(GoodsCsvPath + ": id '" + pair.Key + "' is not in " + ClientsCsvPath + ", ignored");
            }

            // base 继承放在全部行读完之后做，允许引用后面才出现的行（博士上门 → 博士店里）
            for (int i = 0; i < defs.Count; i++) ResolveBase(defs[i], byId, 0);

            var result = defs.ToArray();
            _cachedDefinitions = result;

            if (ModEntry.Log != null)
            {
                ModEntry.Log.Log(string.Format("[sources] loaded {0} definitions from {1} + {2} (unused={3} alias={4} skipped)",
                    result.Length, ClientsCsvPath, GoodsCsvPath, UnusedRowCount, AliasRowCount));
            }

            return result;
        }

        /// <summary>读一张表；失败返回 null（调用方据此返回空表且不写缓存）。</summary>
        private static string ReadTable(string path)
        {
            try
            {
                string text = ModHelper.ReadModFileText(ModEntry.MOD_ID, path);
                if (string.IsNullOrEmpty(text))
                {
                    Warn(path + " is missing or empty; the source list will be empty");
                    return null;
                }
                return text;
            }
            catch (Exception e)
            {
                Warn("cannot read " + path + ": " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// 解析客人表：<c>faction</c> 行切换当前阵营，随后的 client / location 行继承它。
        /// 客人行不再各自写 faction，避免同一阵营里出现漏填的「无阵营」行。
        /// </summary>
        private static List<HardcodedSource> ParseClients(string text)
        {
            var defs = new List<HardcodedSource>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string faction = null;
            UnusedRowCount = 0;
            AliasRowCount = 0;

            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = i == 0 ? lines[i].TrimStart('\uFEFF') : lines[i];
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;

                string[] cols;
                try { cols = ParseCsvRow(trimmed); }
                catch (Exception e)
                {
                    Warn(ClientsCsvPath + " line " + (i + 1) + ": " + e.Message);
                    continue;
                }

                if (cols.Length < ClientColumnCount)
                {
                    Warn(ClientsCsvPath + " line " + (i + 1) + ": expected " + ClientColumnCount
                        + " columns, got " + cols.Length);
                    continue;
                }

                string kindText = cols[0].Trim();
                string id = cols[1].Trim();

                // 阵营分节行：只切换后续行的归属，本身不是一个来源
                if (string.Equals(kindText, "faction", StringComparison.OrdinalIgnoreCase))
                {
                    if (id.Length == 0)
                    {
                        Warn(ClientsCsvPath + " line " + (i + 1) + ": empty faction key");
                        continue;
                    }
                    faction = id;
                    continue;
                }

                // kind=unused：游戏里确实有这条客人，但当前流程遇不到（预告片 / 伪装客 / 无独立身份）。
                // kind=alias ：同一个角色在游戏代码里的另一个 id，数据已并入同名的正式行。
                // 两种都只作资料留档，运行时整行忽略，不参与来源与求购。
                if (string.Equals(kindText, "unused", StringComparison.OrdinalIgnoreCase))
                {
                    UnusedRowCount++;
                    continue;
                }

                if (string.Equals(kindText, "alias", StringComparison.OrdinalIgnoreCase))
                {
                    AliasRowCount++;
                    continue;
                }

                SourceKind kind;
                if (string.Equals(kindText, "client", StringComparison.OrdinalIgnoreCase)) kind = SourceKind.Client;
                else if (string.Equals(kindText, "location", StringComparison.OrdinalIgnoreCase)) kind = SourceKind.Location;
                else
                {
                    Warn(ClientsCsvPath + " line " + (i + 1) + ": unknown kind '" + kindText + "'");
                    continue;
                }

                if (id.Length == 0)
                {
                    Warn(ClientsCsvPath + " line " + (i + 1) + ": empty id");
                    continue;
                }
                if (!seen.Add(id))
                {
                    Warn(ClientsCsvPath + " line " + (i + 1) + ": duplicate id '" + id + "'");
                    continue;
                }
                if (faction == null)
                {
                    Warn("'" + id + "' appears before any faction row; treated as faction_none");
                }

                defs.Add(new HardcodedSource
                {
                    Kind = kind,
                    Id = id,
                    ClientNameKey = NullIfEmpty(cols[2]),
                    LocationId = NullIfEmpty(cols[3]),
                    FallbackKey = NullIfEmpty(cols[4]),
                    Faction = faction ?? "faction_none",
                    // profession 只用于人工分组，代码不解释其内容
                    Sprite = NullIfEmpty(cols[6]),
                    // 第 8 列可选：该客人的容器灌什么水（pure/hq/base/rust/ghost/gutterflow）
                    Water = cols.Length > 7 ? NullIfEmpty(cols[7]) : null,
                    // 第 9 列可选：1/yes = 他也摆空瓶（有水也有原始容器的客人）
                    Empty = cols.Length > 8 && IsTrueColumn(cols[8]),
                    // 第 10 / 11 列可选：他收购的种类标签 / 具体物品 id（都用 | 分隔）
                    BuysTags = cols.Length > 9 ? NullIfEmpty(cols[9]) : null,
                    BuysItems = cols.Length > 10 ? NullIfEmpty(cols[10]) : null,
                    // 第 12 列：他卖的货（原来的 goods.csv items 列合并进来）
                    Sells = cols.Length > 11 ? NullIfEmpty(cols[11]) : null,
                    // 第 13~16 列：原来 goods.csv 的 base / exclude / random_items / dynamic
                    Base = cols.Length > 12 ? NullIfEmpty(cols[12]) : null,
                    Exclude = cols.Length > 13 ? SplitIds(cols[13]) : new string[0],
                    RandomItems = cols.Length > 14 ? SplitIds(cols[14]) : new string[0],
                    Dynamic = cols.Length > 15 ? NullIfEmpty(cols[15]) : null,
                    Items = new string[0]
                });
            }

            return defs;
        }

        /// <summary>解析货单表：id → 原始 6 列（真正拆 items 在挂到来源时做）。</summary>
        private static Dictionary<string, string[]> ParseGoods(string text)
        {
            var goods = new Dictionary<string, string[]>(StringComparer.Ordinal);

            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = i == 0 ? lines[i].TrimStart('\uFEFF') : lines[i];
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;

                string[] cols;
                try { cols = ParseCsvRow(trimmed); }
                catch (Exception e)
                {
                    Warn(GoodsCsvPath + " line " + (i + 1) + ": " + e.Message);
                    continue;
                }

                if (cols.Length < GoodsColumnCount)
                {
                    Warn(GoodsCsvPath + " line " + (i + 1) + ": expected " + GoodsColumnCount
                        + " columns, got " + cols.Length);
                    continue;
                }

                string id = cols[0].Trim();
                if (id.Length == 0)
                {
                    Warn(GoodsCsvPath + " line " + (i + 1) + ": empty id");
                    continue;
                }
                if (goods.ContainsKey(id))
                {
                    Warn(GoodsCsvPath + " line " + (i + 1) + ": duplicate id '" + id + "'");
                    continue;
                }
                goods.Add(id, cols);
            }

            return goods;
        }

        /// <summary>取第 index 列，缺列时当空串（少写尾部空列不算错）。</summary>
        private static string Cell(string[] cols, int index)
        {
            return cols.Length > index ? cols[index] : string.Empty;
        }

        /// <summary>合并两组 id，保序去重（clients.csv 的 sells 与 goods.csv 的 items 叠加）。</summary>
        private static string[] Concat(string[] a, string[] b)
        {
            if (a == null || a.Length == 0) return b ?? new string[0];
            if (b == null || b.Length == 0) return a;

            var list = new List<string>(a.Length + b.Length);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < a.Length; i++)
                if (!string.IsNullOrEmpty(a[i]) && seen.Add(a[i])) list.Add(a[i]);
            for (int i = 0; i < b.Length; i++)
                if (!string.IsNullOrEmpty(b[i]) && seen.Add(b[i])) list.Add(b[i]);
            return list.ToArray();
        }

        /// <summary>
        /// 递归展开 base：继承对方的 items / random_items，叠加自己的，再按 exclude 剔除。
        /// 深度上限防止配置里写环。
        /// </summary>
        private static void ResolveBase(HardcodedSource def, Dictionary<string, HardcodedSource> byId, int depth)
        {
            if (def == null || def.Base == null) return;

            if (depth > 8)
            {
                Warn("base chain too deep at '" + def.Id + "', ignored");
                def.Base = null;
                return;
            }

            HardcodedSource parent;
            if (!byId.TryGetValue(def.Base, out parent) || parent == null)
            {
                Warn("'" + def.Id + "' refers to unknown base '" + def.Base + "'");
                def.Base = null;
                return;
            }

            if (ReferenceEquals(parent, def))
            {
                Warn("'" + def.Id + "' cannot inherit from itself");
                def.Base = null;
                return;
            }

            ResolveBase(parent, byId, depth + 1);

            var excluded = new HashSet<string>(def.Exclude, StringComparer.Ordinal);
            def.Items = MergeIds(parent.Items, def.Items, excluded);
            def.RandomItems = MergeIds(parent.RandomItems, def.RandomItems, excluded);
            def.Base = null;   // 标记已展开，重入时不会重复合并
        }

        /// <summary>合并两组 id、去重、剔除 exclude，保持顺序。</summary>
        private static string[] MergeIds(string[] inherited, string[] own, HashSet<string> excluded)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>();

            for (int pass = 0; pass < 2; pass++)
            {
                string[] part = pass == 0 ? inherited : own;
                if (part == null) continue;
                for (int i = 0; i < part.Length; i++)
                {
                    string id = part[i];
                    if (string.IsNullOrEmpty(id) || excluded.Contains(id)) continue;
                    if (seen.Add(id)) result.Add(id);
                }
            }
            return result.ToArray();
        }

        private static string[] SplitIds(string cell)
        {
            if (string.IsNullOrEmpty(cell)) return new string[0];

            string[] parts = cell.Split('|');
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                string id = parts[i].Trim();
                if (id.Length > 0 && seen.Add(id)) result.Add(id);
            }
            return result.ToArray();
        }

        private static string NullIfEmpty(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            string t = s.Trim();
            return t.Length == 0 ? null : t;
        }

        /// <summary>布尔列：1 / true / yes / y 都算真。</summary>
        private static bool IsTrueColumn(string s)
        {
            string t = (s ?? string.Empty).Trim().ToLowerInvariant();

            return t == "1" || t == "true" || t == "yes" || t == "y";
        }

        /// <summary>
        /// 最小 CSV 行解析：支持双引号包裹字段、字段内逗号、双写引号转义。
        /// 列数固定，不做宽泛的 CSV 方言兼容。
        /// </summary>
        internal static string[] ParseCsvRow(string line)
        {
            var cells = new List<string>(ClientColumnCount);
            var field = new StringBuilder();
            bool quoted = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                        else quoted = false;
                    }
                    else field.Append(c);
                }
                else if (c == '"' && field.Length == 0)
                {
                    quoted = true;
                }
                else if (c == ',')
                {
                    cells.Add(field.ToString());
                    field.Length = 0;
                }
                else
                {
                    field.Append(c);
                }
            }
            cells.Add(field.ToString());
            return cells.ToArray();
        }
    }
}
