using System;
using System.Collections.Generic;
using System.Reflection;

using TMPro;
using UnityEngine;

using ItemCatalog.Catalog;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 物品详情窗口（合成表 / 来源）：点图鉴格子后弹出，只读。
    ///
    /// 来源页一页一个「来源 + 它的一段货单」，版式：
    ///   大标题（靠左，阵营）→ 小标题（居中，客人名）→ 客人立绘（居中缩小）
    ///   → 该来源可能摆出的全部物品网格（当前检索的物品标绿）→ 底部翻页条。
    /// 顶部一行「&lt; 大类 &gt;」负责在 客人的货 / 地点商店 / 合成 之间切换；
    /// 底部翻页条的布局与图鉴一致（&lt; 页码 &gt;，两端弹性空位，固定在底部）。
    /// </summary>
    internal static class RecipeWindow
    {
        private const string WinId = "recipe:win";
        private const float WinWidth = 600f;
        private const float WinMinHeight = 240f;
        private const float WinMaxHeight = 900f;
        private const float WindowChrome = 64f;   // 标题栏 + 内容区上下留白
        private const float IconSide = 40f;

        private const int GridColumns = 8;
        private const float CellWidth = 56f;
        private const float CellHeight = 56f;
        private const float CellSpacing = 6f;

        // 一页最多几行货，放满就翻页；最后一页不足时窗口会自己缩到内容高度。
        // 窗口加高后一页最多 6 行（48 件），屏幕矮时自动少放几行、靠翻页补。
        private const int MaxGridRows = 6;

        private static int RowsPerPage
        {
            get
            {
                // 有立绘的来源页开销最大：把它算进去，保证满页也装得下
                const float fixedPart = 34f + 30f + 28f + 34f + 32f
                                        + PortraitMaxSide + 8f + WindowChrome;
                int rows = Mathf.FloorToInt((MaxContentHeight() - fixedPart)
                                            / (CellHeight + CellSpacing));
                return Mathf.Clamp(rows, 1, MaxGridRows);
            }
        }

        private static int ItemsPerPage { get { return GridColumns * RowsPerPage; } }

        /// <summary>客人立绘最长边。客人精灵本身一般是 256 见方，再放大会开始发虚。</summary>
        private const float PortraitMaxSide = 300f;

        /// <summary>同一客人的多种立绘轮播间隔（秒）。</summary>
        private const float PortraitSwitchSeconds = 1f;

        private static float _lastHeight = -1f;

        // ---- 客人立绘轮播：同一来源的多张精灵按顺序每秒换一张 ----
        private static List<Sprite> _portraitSprites;
        private static int _portraitIndex;
        private static float _portraitNextAt;
        private static UnityEngine.UI.Image _portraitImage;
        private static int _portraitSwitches;   // 诊断日志用，每页重置


        // 反射缓存：SetHighlightColor(Image, BackgroundColor.Green=1, BackgroundType.Smooth=1)
        // 两个枚举是 internal，只能反射调用，但这样 sprite / 颜色 / enabled 全由原版决定。
        // BackgroundType：Grid(0)=greenSquare(grid_green，网格状带灰底)、Smooth(1)=green(纯绿)、
        // Border(2)=greenBorder。用 Smooth，得到的是一整块纯绿，不是「灰板 + 绿框」。
        private static MethodInfo _setHighlight;
        private static object _colorGreen;
        private static object _typeSmooth;

        /// <summary>本页所有绿底，Render 末尾布局定稿后同步到各自格子的位置。</summary>
        private static readonly List<KeyValuePair<RectTransform, RectTransform>> _highlights =
            new List<KeyValuePair<RectTransform, RectTransform>>();

        /// <summary>
        /// 大类。客人/地点来自 SourceIndex，合成来自 RecipeIndex，
        /// 使用来自 UseIndex（开箱、喝掉、涂抹这类「对物品本身动手」的转化）。
        /// </summary>
        private const int CatClient = 0;
        private const int CatLocation = 1;
        private const int CatCraftOut = 2;
        private const int CatCraftIn = 3;
        private const int CatUseOut = 4;     // 来源侧：用了什么才得到这件
        private const int CatUsedBy = 5;     // 去处侧：这件东西用掉能得到什么
        private const int CatLoot = 6;       // 来源侧：能开出它的战利品表（开箱/搜容器）
        private const int CatLootUse = 7;    // 去处侧：这箱子/这张卡能开出什么
        private const int CatLock = 8;       // 去处侧：箱子要哪张卡 / 卡能开什么
        private const int CatWanted = 9;     // 去处侧：哪个客人收这件（收购清单命中）

        private sealed class Category
        {
            internal int Kind;
            internal List<SourceInfo> Sources;
            internal List<RecipeInfo> Recipes;
            internal List<UseInfo> Uses;
            internal List<LootTableInfo> Loot;
            internal bool LootAsUse;   // true = 去处侧（箱子/卡 → 产出）

            /// <summary>
            /// true = 这一页是「求购」：结构照来源页（阵营 / 客人名 / 立绘 / 翻页），
            /// 但正文不画他卖的货，改画他收什么。
            /// </summary>
            internal bool IsWanted;
        }

        /// <summary>货单里的一个格子。有水也有空瓶的客人，容器会多出「空瓶」那一格。</summary>
        private sealed class GoodsCell
        {
            internal string Id;
            internal bool Empty;   // 只画空瓶：不显示成分、不参与轮播
        }

        /// <summary>一个来源的一页货单切片。</summary>
        private sealed class SourcePage
        {
            internal SourceInfo Source;
            internal List<GoodsCell> Cells;
            internal int Count { get { return Cells == null ? 0 : Cells.Count; } }
        }

        private static CustomUIWindow _window;
        private static RectTransform _bodyHost;

        private static ItemEntry _entry;

        /// <summary>false = 来源（左键）；true = 去处（右键）。</summary>
        private static bool _asUse;

        private static int _category;
        private static int _page;

        private static List<Category> _categories = new List<Category>();
        private static readonly List<SourcePage> _sourcePages = new List<SourcePage>();

        /// <summary>
        /// 当前正在渲染的客人 id（只在来源页有效，配方页为 null）。
        /// 容器的成分与瓶身精灵都按它派生——不同客人灌的水不同。
        /// </summary>
        internal static string SourceSeed;

        /// <summary>当前客人的容器灌什么水（clients.csv 第 8 列，留空=base）。</summary>
        internal static string SourceWater;

        /// <summary>货单里容器格子的多张图标（成分不同 → 瓶身精灵不同），每秒轮播。</summary>
        private sealed class CellAnimation
        {
            internal List<Sprite> Sprites;
            internal int Index;
            internal UnityEngine.UI.Image Image;
        }

        private static readonly Dictionary<string, CellAnimation> _cellAnims =
            new Dictionary<string, CellAnimation>(StringComparer.Ordinal);

        /// <summary>
        /// 用途页每个标签格轮播到第几件（tag → 索引）。
        /// 和 `_cellAnims` 不是一回事：那个只换格子里的一张精灵，
        /// 这里换的是**整个物品**，所以要配合重建正文才能让
        /// 图标 / 悬停提示 / 点击目标 / 绿底一起变。
        /// </summary>
        private static readonly Dictionary<string, int> _tagRollIndex =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// 本次渲染画出来的「标签格条」。每条自带一个容器元素，
        /// 轮播时只把它们自己的容器清掉重画，不动页面其余部分
        /// （旧做法是整页 `Render()`，会闪、会丢滚动位置、会重置立绘）。
        /// </summary>
        private static readonly List<TagStrip> _tagStrips = new List<TagStrip>();

        private static int _tagStripSeq;

        private sealed class TagStrip
        {
            internal UnityEngine.RectTransform Host;   // 这一条的容器（重建时清它）
            internal string[] Tags;                 // 这一条里要画的标签
            internal int Base;                      // 格子 id 起点
            internal string CurrentId;              // 当前详情物品，用于「它自己就在标签里」判定
            internal HashSet<string> Exclude;       // 已经单独列出的具体物品，别在标签格里重复画
        }

        internal static void OpenFor(ItemEntry entry)
        {
            OpenFor(entry, false);
        }

        /// <summary>打开（或复用）窗口。asUse=false 看来源，true 看去处。</summary>
        internal static void OpenFor(ItemEntry entry, bool asUse)
        {
            if (entry == null) return;

            _entry = entry;
            _asUse = asUse;
            _category = 0;
            _page = 0;

            if (_window == null || !_window.IsAlive) Create();
            else Render();
        }

        /// <summary>窗口是否还开着：图鉴的拖拽 / 提示框允许详情窗口单独存在。</summary>
        internal static bool IsOpen
        {
            get { return _window != null && _window.IsAlive; }
        }

        /// <summary>拖拽幽灵图要挂在窗口画布上，图鉴关掉时退回本窗口的画布。</summary>
        internal static Transform CanvasParent
        {
            get
            {
                if (_window == null || !_window.IsAlive || _window.root == null) return null;
                return _window.root.transform.parent;
            }
        }

        internal static void Close()
        {
            try { if (IsOpen) _window.Close(); }
            catch { }

            _window = null;
            _bodyHost = null;
            _categories = new List<Category>();
            _sourcePages.Clear();
            StopPortrait();
            _cellAnims.Clear();
            _tagRollIndex.Clear();   // 换物品后标签轮播从头开始
            _tagStrips.Clear();
            _tagStripSeq = 0;
            SourceSeed = null;
            SourceWater = null;
            CatalogWindow.ResetExternalCells();
        }

        /// <summary>
        /// 每秒推进一张客人立绘。由 CatalogTicker 每帧调用，内部自己掐 1 秒间隔；
        /// 只改 Image.sprite，不重建布局，开销可以忽略。
        /// 立绘的显示尺寸按第一张算好——客人精灵都是同一规格，换图不会变形。
        /// </summary>
        internal static void TickPortrait()
        {
            if (!IsOpen) return;
            if (Time.unscaledTime < _portraitNextAt) return;   // 立绘与货单图标共用同一个 1 秒节拍

            _portraitNextAt = Time.unscaledTime + PortraitSwitchSeconds;

            // 客人立绘
            var sprites = _portraitSprites;
            if (sprites != null && sprites.Count > 1 && _portraitImage != null)
            {
                _portraitIndex = (_portraitIndex + 1) % sprites.Count;
                try
                {
                    _portraitImage.sprite = sprites[_portraitIndex];

                    if (_portraitSwitches < 3 && ModEntry.Log != null)
                    {
                        _portraitSwitches++;
                        ModEntry.Log.Log("[portrait] switch " + _portraitIndex + "/" + sprites.Count);
                    }
                }
                catch { /* 窗口正在销毁 */ }
            }

            // 标签格只重建自己那一小块，其余格子的 _cellAnims 仍然有效，不用提前返回
            TickTagRolls();

            // 货单里的容器图标：成分不同 → 瓶身精灵不同，逐张轮播
            if (_cellAnims.Count == 0) return;

            foreach (var pair in _cellAnims)
            {
                var anim = pair.Value;
                if (anim == null || anim.Image == null) continue;
                if (anim.Sprites == null || anim.Sprites.Count <= 1) continue;

                anim.Index = (anim.Index + 1) % anim.Sprites.Count;
                try { anim.Image.sprite = anim.Sprites[anim.Index]; }
                catch { }
            }
        }

        /// <summary>
        /// 推进标签格的轮播索引，**只重建那一条标签格自己的容器**。
        /// 不整页 `Render()`：页面其余部分（标题、客人立绘、配料、产物）原样保留，
        /// 也不会每次重建都把滚动位置和立绘轮播重置掉。
        /// 返回 true 表示这一轮重建过（旧 `_cellAnims` 已随容器销毁）。
        /// </summary>
        private static bool TickTagRolls()
        {
            if (!IsOpen || _tagStrips.Count == 0) return false;

            bool moved = false;
            for (int i = 0; i < _tagStrips.Count; i++)
            {
                var strip = _tagStrips[i];
                if (strip == null || strip.Tags == null) continue;
                if (strip.Host == null) continue;   // 容器已被别的重建销毁

                bool stripMoved = false;
                for (int k = 0; k < strip.Tags.Length; k++)
                {
                    string tag = strip.Tags[k];
                    if (IsInternalTag(tag)) continue;   // 已经展开成静态物品，不轮播
                    var ids = ItemsWithTag(tag);
                    if (ids.Count <= 1) continue;
                    if (string.Equals(tag, strip.CurrentId, StringComparison.Ordinal)) continue;

                    int index;
                    if (!_tagRollIndex.TryGetValue(tag, out index)) index = 0;
                    _tagRollIndex[tag] = (index + 1) % ids.Count;
                    stripMoved = true;
                }

                if (!stripMoved) continue;

                try { RenderTagStrip(strip); }
                catch (Exception e) { Warn("tag strip: " + e.Message); }
                moved = true;
            }

            return moved;
        }

        /// <summary>
        /// 标签格下面那行说明。
        /// 水是特例：客人收「水」其实管的是**水质**，所以这里写当前那瓶水的档位名
        /// （`WaterFeatureHelper.GetWaterDisplayName` → `mech_condition_pure_water`
        /// / `..._high_quality_water` / `basewater` / `ghostwater` / `rustwater` /
        /// `gutterflow`），成分交给 tooltip，不再写「水」。
        /// </summary>
        private static string CaptionFor(string tag, string currentId)
        {
            // 内部 `_TAG` 已经展开成物品了，不需要再写一行标签
            if (IsInternalTag(tag)) return null;

            if (!string.Equals(tag, "WATER", StringComparison.OrdinalIgnoreCase))
                return DescribeTags(new[] { tag });

            var ids = ItemsWithTag(tag);
            if (ids.Count == 0) return null;

            string showId;
            if (!string.IsNullOrEmpty(currentId) && ids.Contains(currentId))
            {
                showId = currentId;
            }
            else
            {
                int index;
                if (!_tagRollIndex.TryGetValue(tag, out index)) index = 0;
                showId = ids[((index % ids.Count) + ids.Count) % ids.Count];
            }

            var entry = RecipeIndex.FindEntry(showId);
            var item = entry != null ? entry.Item : null;
            if (item != null)
            {
                try
                {
                    string name = WaterFeatureHelper.GetWaterDisplayName(item);
                    if (!string.IsNullOrEmpty(name)) return name;
                }
                catch { }
            }

            return ModEntry.T("use_tag_water");
        }

        /// <summary>把一条标签格重画到它自己的容器里（清空 → 新建 builder → 画格子）。</summary>
        private static void RenderTagStrip(TagStrip strip)
        {
            if (strip == null || strip.Host == null) return;

            var manager = CustomUIManager.Instance;
            if (manager == null || _window == null || !_window.IsAlive) return;

            // 旧的格子和绿底随容器一起销毁，先把悬空登记清掉（否则每秒轮播会一直累积）
            for (int i = _highlights.Count - 1; i >= 0; i--)
            {
                if (_highlights[i].Key == null || _highlights[i].Value == null)
                    _highlights.RemoveAt(i);
            }

            CustomUIBuilder.ClearContainer(strip.Host);

            var builder = new CustomUIBuilder(manager, _window, strip.Host);

            // 所有标签格共用一张网格：以前每格各自 BeginGrid 一次，结果竖着排成一列
            builder.BeginGrid(GridColumns, CellWidth, CellHeight, CellSpacing);

            for (int i = 0; i < strip.Tags.Length; i++)
            {
                string tag = strip.Tags[i];
                if (string.IsNullOrEmpty(tag)) continue;

                // 什么都收的客人：不展开物品（那是全注册表），只写一行「全部物品」
                if (tag.StartsWith("ALL_TYPE", StringComparison.OrdinalIgnoreCase))
                {
                    // 原版有 `type_all_type` / `type_all_type_except_contraband`，直接用它的文案
                    string allName = BuyTagName(tag);
                    if (string.IsNullOrEmpty(allName) ||
                        allName.StartsWith("ALL_TYPE", StringComparison.OrdinalIgnoreCase))
                    {
                        allName = ModEntry.T("all_items");
                    }

                    builder.AddLabel(allName, "tag_all" + (strip.Base + i));
                    continue;
                }

                var ids = ItemsWithTag(tag);
                if (strip.Exclude != null && strip.Exclude.Count > 0) ids.RemoveAll(strip.Exclude.Contains);
                if (ids.Count == 0) continue;

                // 内部标记（ADV_GUN_TAG 这类）不写名字，直接展开成具体物品
                if (IsInternalTag(tag))
                {
                    for (int k = 0; k < ids.Count; k++)
                        RenderGoods(builder, ids[k], false, strip.Base + i * 8 + k, false);
                    continue;
                }

                // 当前物品自己就在这一类里 → 固定显示它；否则按轮播索引取一件
                bool self = !string.IsNullOrEmpty(strip.CurrentId) && ids.Contains(strip.CurrentId);

                string showId;
                if (self)
                {
                    showId = strip.CurrentId;
                }
                else
                {
                    int index;
                    if (!_tagRollIndex.TryGetValue(tag, out index)) index = 0;
                    index = ((index % ids.Count) + ids.Count) % ids.Count;
                    showId = ids[index];
                }

                // 绿底表示「这一类里有当前这件」，与轮播到哪一件无关（MatchMatch 用 self 判定）
                RenderGoods(builder, showId, self, strip.Base + i, false);
                AddOverlayCaption(builder, builder.Last, CaptionFor(tag, strip.CurrentId), "tagcap" + (strip.Base + i));
            }

            builder.End();

            CustomUIBuilder.RebuildLayout(strip.Host);

            // 局部重建后绿底是新对象，必须立刻对齐一次，否则会停在默认位置（画面上那块大绿方块）
            SyncHighlights();
        }

        private const float CaptionHeight = 15f;
        private const float CaptionFontSize = 10f;

        /// <summary>
        /// 把说明文字叠到刚画好的那一格上：半透明黑底 + 白字，贴在格子底部，
        /// 和原版「容器名称」在物品图标上的写法一致。
        ///
        /// 做法：正常 AddLabel 出一个元素（这样沿用窗口的字体/材质），再把它从布局里
        /// 摘出来挂到格子上、拉伸成底栏，并在它下面插一张 55% 黑底的 Image。
        /// </summary>
        private static void AddOverlayCaption(CustomUIBuilder builder, CustomUIElement cell, string text, string id)
        {
            if (builder == null || string.IsNullOrEmpty(text)) return;
            if (cell == null || cell.Rect == null) return;

            builder.AddLabel(text, id);
            var label = builder.Last;
            if (label == null || label.Rect == null) return;

            var rect = label.Rect;
            var host = cell.Rect;

            try
            {
                rect.SetParent(host, false);
                rect.anchorMin = new Vector2(0f, 0f);
                rect.anchorMax = new Vector2(1f, 0f);
                rect.pivot = new Vector2(0.5f, 0f);
                rect.anchoredPosition = Vector2.zero;
                rect.sizeDelta = new Vector2(0f, CaptionHeight);
                rect.localScale = Vector3.one;

                // 格子内部可能带布局组：不排除的话这条底栏会把格子撑高、把图标挤小，
                // 绿底是按格子矩形画的，于是看起来「绿底画歪了」。
                var ignore = rect.GetComponent<UnityEngine.UI.LayoutElement>();
                if (ignore == null) ignore = rect.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
                ignore.ignoreLayout = true;

                // 黑底必须是「格子的子物体、排在文字之前」：
                // 做成文字的子物体就画在文字之上，字会显得发黑发透。
                var bgGo = new GameObject("caption_bg",
                    typeof(RectTransform), typeof(UnityEngine.UI.Image));

                var bgRect = (RectTransform)bgGo.transform;
                bgRect.SetParent(host, false);
                bgRect.anchorMin = new Vector2(0f, 0f);
                bgRect.anchorMax = new Vector2(1f, 0f);
                bgRect.pivot = new Vector2(0.5f, 0f);
                bgRect.anchoredPosition = Vector2.zero;
                bgRect.sizeDelta = new Vector2(0f, CaptionHeight);
                bgRect.localScale = Vector3.one;

                var bg = bgGo.GetComponent<UnityEngine.UI.Image>();
                bg.color = new Color(0f, 0f, 0f, 0.6f);
                bg.raycastTarget = false;
                bgGo.layer = host.gameObject.layer;

                var bgIgnore = bgGo.AddComponent<UnityEngine.UI.LayoutElement>();
                bgIgnore.ignoreLayout = true;

                // 紧挨在文字前面 → 先画底、后画字
                bgRect.SetSiblingIndex(rect.GetSiblingIndex());

                var tmp = rect.GetComponentInChildren<TMPro.TMP_Text>(true);
                if (tmp != null)
                {
                    tmp.fontSize = CaptionFontSize;
                    tmp.alignment = TMPro.TextAlignmentOptions.Center;
                    tmp.color = new Color(1f, 1f, 1f, 1f);   // 不透明，别继承窗口的半透明样式
                    tmp.raycastTarget = false;
                    tmp.enableWordWrapping = false;
                    tmp.overflowMode = TMPro.TextOverflowModes.Truncate;
                }
            }
            catch (Exception e) { Warn("caption overlay: " + e.Message); }
        }

        private static void StopPortrait()
        {
            _portraitSprites = null;
            _portraitIndex = 0;
            _portraitImage = null;
            _portraitNextAt = 0f;
        }

        // ---------------------------------------------------------------- 大类

        private static void BuildCategories()
        {
            _categories = new List<Category>();
            if (_entry == null) return;

            if (!_asUse)
            {
                // 来源只算「谁卖给我」；收购清单属于用途侧（见 CatWantedBy）
                var sources = SourceIndex.For(_entry.Id);

                AddSourceCategory(CatClient, sources);
                AddSourceCategory(CatLocation, sources);

                var produced = RecipeIndex.Producing(_entry.Id);
                if (produced.Count > 0)
                {
                    _categories.Add(new Category { Kind = CatCraftOut, Recipes = produced });
                }

                var uses = UseIndex.Producing(_entry.Id);
                if (uses.Count > 0)
                {
                    _categories.Add(new Category { Kind = CatUseOut, Uses = uses });
                }

                var loot = LootSource.For(_entry.Id);
                if (loot.Count > 0)
                {
                    // 只留「玩家真能遇到」的池：能对到箱子，或写明了使用场合。
                    // 只埋在场景数据里的池直接不列。
                    var used = new List<LootTableInfo>(loot.Count);
                    for (int i = 0; i < loot.Count; i++)
                    {
                        if (LootSource.IsUsed(loot[i])) used.Add(loot[i]);
                    }

                    if (used.Count > 0)
                        _categories.Add(new Category { Kind = CatLoot, Loot = used });
                }
            }
            else
            {
                // 谁收这件：用来源页那套结构（阵营 / 客人名 / 立绘 / 一页一个客人），
                // 正文换成「他收什么」。放在用途侧第一类 —— 「卖给谁」是最常用的去处。
                var wanted = SourceIndex.WantedBy(_entry.Id, _entry.Item);
                if (wanted.Count > 0)
                {
                    _categories.Add(new Category
                    {
                        Kind = CatWanted,
                        Sources = wanted,
                        IsWanted = true
                    });
                }

                var consumed = RecipeIndex.Consuming(_entry.Id, _entry.Item);
                if (consumed.Count > 0)
                {
                    _categories.Add(new Category { Kind = CatCraftIn, Recipes = consumed });
                }

                // 传 Item 是为了让 `tag:XXX` 那类「整类目标」也能命中
                // （拆解只对带 ADV_GUN_TAG 的枪、屠宰只对带 BLADE_ITEM_TAG 的刀）。
                var used = UseIndex.Using(_entry.Id, _entry.Item);
                if (used.Count > 0)
                {
                    _categories.Add(new Category { Kind = CatUsedBy, Uses = used });
                }

                // 他自己就是箱子 → 开启后能开出什么
                var asBox = LootSource.AsBox(_entry.Id);
                if (asBox.Count > 0)
                {
                    _categories.Add(new Category
                    {
                        Kind = CatLootUse,
                        Loot = asBox,
                        LootAsUse = true
                    });
                }

                // 他自己是卡 → 能开哪些箱子（表 id 不是物品 id 时会是 0 条）
                var asKey = LootSource.ForKey(_entry.Item);
                if (asKey.Count == 0) asKey = LootSource.BoxesForKeyId(_entry.Id);
                if (asKey.Count > 0)
                {
                    _categories.Add(new Category
                    {
                        Kind = CatLootUse,
                        Loot = asKey,
                        LootAsUse = true
                    });
                }

                // 锁信息不再单独开一页：开启页已经把「箱子 + 卡」并排画出来了，
                // 两个大类都叫「开启」会变成 1/2、2/2 两页，看着像重复。
            }

            if (_category < 0) _category = 0;
            if (_category >= _categories.Count) _category = Math.Max(0, _categories.Count - 1);
        }

        private static void AddSourceCategory(int kind, List<SourceInfo> sources)
        {
            SourceKind target = kind == CatClient ? SourceKind.Client : SourceKind.Location;

            List<SourceInfo> picked = null;
            for (int i = 0; i < sources.Count; i++)
            {
                var s = sources[i];
                if (s == null || s.Kind != target) continue;

                if (picked == null) picked = new List<SourceInfo>();
                picked.Add(s);
            }

            if (picked != null) _categories.Add(new Category { Kind = kind, Sources = picked });
        }

        private static string CategoryTitle(Category category)
        {
            if (category == null) return "?";

            switch (category.Kind)
            {
                case CatClient: return ModEntry.T("src_clients");
                case CatLocation: return ModEntry.T("src_locations");
                case CatCraftOut: return ModEntry.T("rec_cat_craft_out");
                case CatCraftIn: return ModEntry.T("rec_cat_craft_in");
                case CatUseOut: return ModEntry.T("rec_cat_use_out");
                case CatUsedBy: return ModEntry.T("rec_cat_use_in");
                case CatLoot: return ModEntry.T("src_loot");
                case CatLootUse: return ModEntry.T("loot_use");
                case CatLock: return ModEntry.T("loot_use");
                case CatWanted: return ModEntry.T("rec_cat_wanted");
                default: return "?";
            }
        }

        // ---------------------------------------------------------------- 窗口

        private static void Create()
        {
            CustomUIBuilder builder;

            try
            {
                builder = CustomUIManager.Window(WinId,
                    _entry == null ? ModEntry.T("rec_title") : _entry.DisplayName, "paper")
                    .SetDraggable(true)
                    .SetSize(WinWidth, WinMinHeight)
                    .Center()
                    .UseMachineryStyle(true)
                    .OnClose(OnWindowClosed);
            }
            catch (Exception e)
            {
                Warn("create window: " + e.Message);
                return;
            }

            builder.BeginColumn(6f);
            _bodyHost = builder.CurrentContainer;
            builder.End();

            _window = builder.Show();
            _lastHeight = -1f;   // 新窗口要重新按内容量一次高

            if (_window != null)
            {
                try { CustomUIBuilder.RebuildLayout(_window.ContentRoot); }
                catch { }
            }

            Render();
        }

        /// <summary>窗口被游戏关掉（Esc / 点 X）时，别留下悬空提示框和拖拽登记。</summary>
        private static void OnWindowClosed()
        {
            _highlights.Clear();
            _window = null;
            _bodyHost = null;
            CatalogWindow.ResetExternalCells();
        }

        private static void Render()
        {
            var manager = CustomUIManager.Instance;
            if (manager == null || _window == null || !_window.IsAlive || _bodyHost == null) return;

            BuildCategories();

            var category = _categories.Count == 0 ? null : _categories[_category];
            BuildSourcePages(category);

            int pages = PageCount(category);
            if (_page < 0) _page = 0;
            if (_page >= pages) _page = Math.Max(0, pages - 1);

            try
            {
                CustomUIBuilder.ClearContainer(_bodyHost);
                _highlights.Clear();          // 旧绿底随容器一起销毁
                _cellAnims.Clear();           // 旧格子的 Image 已销毁，不能留着继续改
                _tagStrips.Clear();           // 标签格这一轮重新收集
                _tagStripSeq = 0;
                CatalogWindow.ResetExternalCells();   // 旧格子的提示框 / 拖拽登记一起失效
                var builder = new CustomUIBuilder(manager, _window, _bodyHost);

                // 标题栏直接用物品名（不再在正文里重复一个 header）
                try { _window.SetTitle(_entry == null ? "?" : _entry.DisplayName); }
                catch (Exception e) { Warn("set title: " + e.Message); }

                // 大类翻页：按钮贴两端，名称居中；到头就禁用，不做循环
                builder.BeginRow(6f);
                builder.AddButton("<", delegate { TurnCategory(-1); }, "cat_prev");
                if (builder.Last != null) builder.Last.SetInteractable(_category > 0);
                builder.AddFlexibleSpace();
                builder.AddLabel(CategoryLabel(), "cat_title");
                builder.AddFlexibleSpace();
                builder.AddButton(">", delegate { TurnCategory(1); }, "cat_next");
                if (builder.Last != null)
                    builder.Last.SetInteractable(_category < _categories.Count - 1);
                builder.End();

                // 这件物品自己的类型标签：和游戏 tooltip 上那一串一致
                // （枪械/武器、食物、水、日用品、奢侈品、文档 …）
                string itemTypes = DescribeItemTypes(_entry);
                if (!string.IsNullOrEmpty(itemTypes))
                    builder.AddLabel(itemTypes, "item_types");

                if (category == null)
                {
                    builder.AddWarning(_asUse ? ModEntry.T("use_none") : ModEntry.T("src_none"), "cat_empty");
                    builder.End();
                    CustomUIBuilder.RebuildLayout(_bodyHost);
                    ApplyContentHeight();
                    return;
                }

                // 内容区按自身内容定高，不撑满窗口；翻页条自然紧跟在下面
                builder.BeginColumn(4f);

                // 只有来源页才有「哪个客人」这回事；配方页清掉种子，
                // 成分行回落到实例自身的液体。
                SourceSeed = null;
                SourceWater = null;
                if (category.Sources != null) RenderSourcePage(builder);
                else if (category.Uses != null) RenderUsePage(builder, category.Uses);
                else if (category.Kind == CatLock) RenderLockPage(builder);
                else if (category.Loot != null && category.LootAsUse) RenderLootUsePage(builder, category.Loot);
                else if (category.Loot != null) RenderLootPage(builder, category.Loot);
                else RenderRecipePage(builder, category.Recipes);

                builder.End();

                RenderPager(builder, pages);

                builder.End();
                CustomUIBuilder.RebuildLayout(_bodyHost);
                ApplyContentHeight();
                SyncHighlights();   // 窗口定稿后再对齐，避免布局重排把格子挪走
            }
            catch (Exception e)
            {
                Warn("render: " + e.Message);
            }
        }

        private static string CategoryLabel()
        {
            if (_categories.Count == 0) return "-";
            return (_category + 1) + "/" + _categories.Count + "  " + CategoryTitle(_categories[_category]);
        }

        /// <summary>大类切换：到头就停，不循环。</summary>
        private static void TurnCategory(int delta)
        {
            if (_categories.Count == 0) return;

            int next = Mathf.Clamp(_category + delta, 0, _categories.Count - 1);
            if (next == _category) return;

            _category = next;
            _page = 0;
            Render();
        }

        private static int PageCount(Category category)
        {
            if (category == null) return 1;
            if (category.Sources != null) return Math.Max(1, _sourcePages.Count);
            if (category.Recipes != null) return Math.Max(1, category.Recipes.Count);
            if (category.Uses != null) return Math.Max(1, category.Uses.Count);
            if (category.Loot != null) return Math.Max(1, category.Loot.Count);
            return 1;
        }

        /// <summary>与图鉴翻页条同款：两端弹性空位，固定 34px 高。</summary>
        private static void RenderPager(CustomUIBuilder builder, int pages)
        {
            builder.BeginRow(6f);
            var pagerRow = builder.CurrentContainer;
            builder.AddFlexibleSpace();
            builder.AddButton("<", delegate { TurnPage(-1); Render(); }, "src_pp");
            if (builder.Last != null) builder.Last.SetInteractable(_page > 0);
            builder.AddLabel(string.Format(ModEntry.T("page"), _page + 1, pages), "src_page");
            builder.AddButton(">", delegate { TurnPage(1); Render(); }, "src_pn");
            if (builder.Last != null) builder.Last.SetInteractable(_page < pages - 1);
            builder.AddFlexibleSpace();
            builder.End();

            if (pagerRow != null)
            {
                var pagerLayout = EnsureLayoutElement(pagerRow);
                pagerLayout.flexibleHeight = 0f;
                pagerLayout.minHeight = 34f;
            }
        }

        /// <summary>页码翻页：到头就停，不循环。</summary>
        private static void TurnPage(int delta)
        {
            int pages = PageCount(_categories.Count == 0 ? null : _categories[_category]);
            if (pages <= 0) return;

            _page = Mathf.Clamp(_page + delta, 0, pages - 1);
        }

        /// <summary>
        /// 窗口高度跟着这一页的实际内容走：货少就矮、货多就高，封顶后靠翻页继续放。
        /// 用 LayoutUtility 量 _bodyHost 的首选高度（网格行数、立绘高度都算在内），
        /// 比按行数估常量准，也自动适配配方页。
        /// </summary>
        private static void ApplyContentHeight()
        {
            if (_window == null || !_window.IsAlive || _bodyHost == null) return;

            try
            {
                float preferred = UnityEngine.UI.LayoutUtility.GetPreferredHeight(_bodyHost);
                if (preferred < 1f) preferred = EstimateHeight();

                float height = Mathf.Clamp(preferred + WindowChrome, WinMinHeight, MaxContentHeight());
                if (Mathf.Abs(height - _lastHeight) > 2f)
                {
                    _window.SetSize(WinWidth, height);
                    _lastHeight = height;

                    // 窗口变了尺寸，让内容根再排一次，避免内容按旧尺寸算
                    CustomUIBuilder.RebuildLayout(_window.ContentRoot);
                }
            }
            catch (Exception e)
            {
                Warn("auto height: " + e.Message);
            }
        }

        /// <summary>窗口最长到 WinMaxHeight，但别长出屏幕（留 80px 给标题栏与任务栏）。</summary>
        private static float MaxContentHeight()
        {
            float cap = Screen.height > 0 ? Mathf.Min(WinMaxHeight, Screen.height - 80f) : WinMaxHeight;
            return Mathf.Max(cap, WinMinHeight);
        }

        /// <summary>
        /// 布局还没算出首选高度时（首次打开 / 字体未就绪）的兜底：按这一页实际行数估。
        /// 立绘是页面里最大的一块（<see cref="PortraitMaxSide"/>），必须算进去，否则会把货单挤出窗口。
        /// </summary>
        private static float EstimateHeight()
        {
            const float fixedPart = 34f + 30f + 28f + 34f + 32f;  // 大类行 / 阵营标题 / 客人名 / 翻页条 / 间距
            float h = fixedPart;

            var category = _categories.Count == 0 ? null : _categories[_category];
            if (category != null && category.Sources != null && _page < _sourcePages.Count)
            {
                var page = _sourcePages[_page];
                if (page.Source != null && !string.IsNullOrEmpty(page.Source.Sprite))
                    h += PortraitMaxSide + 8f;

                int rows = Math.Max(1, (page.Count + GridColumns - 1) / GridColumns);
                h += rows * (CellHeight + CellSpacing);
            }
            else
            {
                h += RowsPerPage * (CellHeight + CellSpacing);   // 配方页：材料 / 工具 / 产物等几行
            }

            return h;
        }

        /// <summary>
        /// 反射调原版 SetHighlightColor：BackgroundColor.Green(1) / BackgroundType.Smooth(1)。
        /// 取原版的纯绿贴图 RenderHandler.green（不是 Grid 档的 grid_green 网格灰底），
        /// 颜色编码在 sprite 里，不额外设 Image.color。
        ///
        /// 只改一层 Image 的 sprite，不新建 GameObject、不叠底色：灰板多大绿块就多大。
        /// 必须确认最终 sprite 就是原版绿贴图——灰板自带 sprite，反射失败时
        /// `image.sprite != null` 却仍是灰板，不能再拿「非空」当成功判断。
        /// 原版不设 Image.color / Image.type，这里也不设（自造 tint 和 Sliced 会造成色偏）。
        /// </summary>
        private static bool ApplyVanillaHighlight(UnityEngine.UI.Image image)
        {
            if (image == null) return false;

            try
            {
                var handler = RenderHandler.current;
                if (handler == null) return false;
                var green = handler.green;
                if (green == null) return false;      // 拿不到原版绿贴图就别画，别留灰白方块

                try
                {
                    if (_setHighlight == null)
                    {
                        _setHighlight = typeof(RenderHandler).GetMethod("SetHighlightColor",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (_setHighlight != null)
                        {
                            var args = _setHighlight.GetParameters();
                            _colorGreen = Enum.ToObject(args[1].ParameterType, 1);   // Green
                            _typeSmooth = Enum.ToObject(args[2].ParameterType, 1);   // Smooth
                        }
                    }
                    if (_setHighlight != null)
                        _setHighlight.Invoke(handler, new object[] { image, _colorGreen, _typeSmooth });
                }
                catch (Exception e)
                {
                    Warn("set highlight color: " + e.Message);
                }

                // 无论反射成功与否，都保证这一层落到原版纯绿贴图上
                if (!ReferenceEquals(image.sprite, green)) image.sprite = green;
                image.enabled = true;
                return true;
            }
            catch (Exception e)
            {
                Warn("vanilla highlight: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 绿底是格子的前置兄弟，GridLayoutGroup 不会给它算位置（ignoreLayout），
        /// 所以等布局定稿后复制格子的锚点与尺寸。这样绘制顺序一定在格子之前，
        /// 不会盖到物品图标上。
        /// </summary>
        private static void SyncHighlights()
        {
            for (int i = 0; i < _highlights.Count; i++)
            {
                var highlight = _highlights[i].Key;
                var cell = _highlights[i].Value;
                if (highlight == null || cell == null) continue;

                highlight.anchorMin = cell.anchorMin;
                highlight.anchorMax = cell.anchorMax;
                highlight.pivot = cell.pivot;
                highlight.anchoredPosition = cell.anchoredPosition;
                highlight.sizeDelta = cell.sizeDelta;
                highlight.localScale = cell.localScale;
            }
        }

        // ---------------------------------------------------------------- 来源页

        private static void BuildSourcePages(Category category)
        {
            _sourcePages.Clear();
            if (category == null || category.Sources == null) return;

            // 求购页一页一个客人，正文是他的收购清单，不画货单
            if (category.IsWanted)
            {
                for (int i = 0; i < category.Sources.Count; i++)
                {
                    _sourcePages.Add(new SourcePage
                    {
                        Source = category.Sources[i],
                        Cells = new List<GoodsCell>()
                    });
                }
                return;
            }

            for (int i = 0; i < category.Sources.Count; i++)
            {
                var source = category.Sources[i];
                if (source == null) continue;

                var items = source.Items;
                if (items == null || items.Length == 0)
                {
                    _sourcePages.Add(new SourcePage
                    {
                        Source = source,
                        Cells = new List<GoodsCell>()
                    });
                    continue;
                }

                // 卖水的客人：容器一格（满瓶）。
                // 既卖水又卖瓶的客人（`empty` 列 = 1）：给灌过水的那件容器再拆一个
                // 原始空瓶格，不轮播、不显示成分。`water` 带 `@物品id` 限定时，
                // 没被灌的容器本来就是空瓶，不拆，免得出现两格一模一样的空瓶。
                var cells = new List<GoodsCell>(items.Length);
                for (int k = 0; k < items.Length; k++)
                {
                    string id = items[k];
                    if (string.IsNullOrEmpty(id)) continue;

                    if (source.SellsEmpty && CatalogWindow.IsLiquidContainer(id)
                        && CatalogWindow.ClientHasFilledBottle(source.Water, id))
                        cells.Add(new GoodsCell { Id = id, Empty = true });

                    cells.Add(new GoodsCell { Id = id });
                }

                for (int start = 0; start < cells.Count; start += ItemsPerPage)
                {
                    int count = Math.Min(ItemsPerPage, cells.Count - start);
                    _sourcePages.Add(new SourcePage
                    {
                        Source = source,
                        Cells = cells.GetRange(start, count)
                    });
                }
            }
        }

        private static void RenderSourcePage(CustomUIBuilder builder)
        {
            var page = _sourcePages[_page];
            var source = page.Source;

            // 本页是哪个客人：容器的成分与瓶身精灵都按它派生
            SourceSeed = source == null ? null : source.SourceId;
            SourceWater = source == null ? null : source.Water;

            // 大标题：阵营，靠左
            builder.AddHeader(FactionTitle(source), "src_faction");
            var faction = builder.Last;
            if (faction != null && faction.text != null)
                faction.text.alignment = TextAlignmentOptions.Left;

            // 小标题：客人 / 地点名，居中
            builder.AddLabel(SourceIndex.DisplayName(source), "src_client");
            var name = builder.Last;
            if (name != null && name.text != null)
            {
                name.text.alignment = TextAlignmentOptions.Center;
                name.text.fontSize = 20f;
            }

            // 客人立绘：居中。同一客人有多种外观时按顺序每秒轮播（见 TickPortrait）
            _portraitSprites = ClientSprite.AllFor(source.Sprite);
            _portraitImage = null;
            _portraitSwitches = 0;
            _portraitNextAt = Time.unscaledTime + PortraitSwitchSeconds;

            var portraits = _portraitSprites;
            if (portraits != null && portraits.Count > 0)
            {
                // 同职业的多个客人共用同一个精灵池（都写 pool:GetRandomScavSpriteName），
                // 按客人名算一个起点错开，免得九个人显示同一张脸。轮播顺序仍是从前到后。
                int start = portraits.Count > 1
                    ? (source.SourceId == null ? 0 : (source.SourceId.GetHashCode() & 0x7FFFFFFF) % portraits.Count)
                    : 0;
                _portraitIndex = start;

                float w, h;
                CatalogWindow.FitIcon(portraits[start], PortraitMaxSide, out w, out h);
                builder.BeginRow(4f);
                builder.AddFlexibleSpace();
                builder.AddImage(portraits[start], w, h, "src_portrait");

                // 记住这张 Image，轮播时只换 sprite，不重建布局
                var portraitElement = builder.Last;
                if (portraitElement != null) _portraitImage = portraitElement.image;

                builder.AddFlexibleSpace();
                builder.End();

                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[portrait] render " + source.SourceId
                        + " sprites=" + portraits.Count
                        + " image=" + (_portraitImage != null));
            }
            else if (ModEntry.Log != null)
            {
                ModEntry.Log.Log("[portrait] render " + source.SourceId
                    + " sprites=0 spec=" + source.Sprite);
            }

            // 该来源可能摆出的全部物品。只收购、不卖货的客人（比如按标签收
            // WEAPON 的 Scavenger）货单是空的，不画空网格，让位给下面的求购区。
            var cells = page.Cells;
            if (cells.Count > 0)
            {
                builder.BeginColumn(4f);
                var gridHost = builder.CurrentContainer;
                builder.BeginGrid(GridColumns, CellWidth, CellHeight, CellSpacing);

                for (int i = 0; i < cells.Count; i++)
                {
                    var cell = cells[i];
                    bool match = _entry != null &&
                                 string.Equals(cell.Id, _entry.Id, StringComparison.Ordinal);
                    RenderGoods(builder, cell.Id, match, i, cell.Empty);
                }

                builder.End();
                builder.End();

                // 网格高度由实际行数决定（不再撑满窗口），窗口跟着内容拉伸
                if (gridHost != null)
                {
                    var gridLayout = EnsureLayoutElement(gridHost);
                    gridLayout.flexibleHeight = 0f;
                    gridLayout.minHeight = CellHeight;
                }
            }

            // 「他在收什么」只出现在求购页：普通来源页只讲他卖/给的货。
            var cat = _category >= 0 && _category < _categories.Count ? _categories[_category] : null;
            if (cat != null && cat.IsWanted) RenderBuys(builder, source, false);
        }

        /// <summary>
        /// 「求购」区块。数据来自 `clients.csv` 第 10/11 列，
        /// 是从各客人 `willAcceptItem` 谓词的 IL 里抄出来的
        /// （`IsTag("WATER")` / `IsID("bottled_water_premium")`）：
        ///   · 种类：只显示标签名（WATER、FOOD…），不展开成具体物品；
        ///   · 特殊物品：和货单格子一样，有图标、能点击、能悬停。
        /// </summary>
        private static void RenderBuys(CustomUIBuilder builder, SourceInfo source, bool withHeader = true)
        {
            if (source == null) return;

            var tags = source.BuysTags;
            var items = source.BuysItems;

            bool hasTags = tags != null && tags.Length > 0;
            bool hasItems = items != null && items.Length > 0;

            if (!hasTags && !hasItems) return;

            // 求购页本身就叫「求购」，正文里不再重复一个同名标题
            if (withHeader) builder.AddHeader(ModEntry.T("src_buys"), "src_buys");

            if (hasTags)
            {
                var names = new List<string>(tags.Length);
                for (int i = 0; i < tags.Length; i++)
                    names.Add(BuyTagName(tags[i]));

                builder.AddLabel(string.Join(" / ", names.ToArray()), "src_buys_tags");

                // 标签格要排除已经单独列出的具体物品，否则同一个瓶子会画两遍：
                // 囤水者既收任意 WATER、又专收 bottled_water_premium，
                // 不排除的话「求购」下面会出现两格一模一样的水。
                HashSet<string> exclude = null;
                if (hasItems)
                {
                    exclude = new HashSet<string>(StringComparer.Ordinal);
                    for (int k = 0; k < items.Length; k++)
                        if (!string.IsNullOrEmpty(items[k])) exclude.Add(items[k]);
                }

                // 每个收购标签给一格「这一类」的物品：当前物品属于该类就固定显示它，
                // 否则轮播该类全部物品。标签格圈进自己的容器，轮播时只重建这一条，
                // 不会整页重画、也就不会打断客人立绘的每秒轮播。
                builder.BeginColumn(2f);
                var buyStripHost = builder.CurrentContainer;
                builder.End();

                var buyStrip = new TagStrip
                {
                    Host = buyStripHost,
                    Tags = tags,
                    Base = 800 + _tagStripSeq * 8,
                    CurrentId = _entry == null ? null : _entry.Id,
                    Exclude = exclude
                };
                _tagStripSeq++;
                _tagStrips.Add(buyStrip);
                RenderTagStrip(buyStrip);
            }

            if (!hasItems) return;

            RenderItemGrid(builder, items, 500);
        }

        /// <summary>
        /// 一排图鉴格子。用 `BeginGrid` 而不是 `BeginRow`：
        /// 网格格子自带和「客人的货」一样的绿底、同样的 56×56 尺寸，
        /// 行里手搓的图没有底、还会被压小。
        /// </summary>
        private static void RenderItemGrid(CustomUIBuilder builder, string[] ids, int idBase)
        {
            if (ids == null || ids.Length == 0) return;

            var host = builder.CurrentContainer;
            builder.BeginGrid(GridColumns, CellWidth, CellHeight, CellSpacing);

            for (int i = 0; i < ids.Length; i++)
            {
                if (string.IsNullOrEmpty(ids[i])) continue;

                bool match = _entry != null && string.Equals(ids[i], _entry.Id, StringComparison.Ordinal);
                RenderGoods(builder, ids[i], match, idBase + i, false);
            }

            builder.End();

            if (host != null)
            {
                var layout = EnsureLayoutElement(host);
                layout.flexibleHeight = 0f;
                layout.minHeight = CellHeight;
            }
        }

        /// <summary>
        /// 种类标签 → 可读名字。
        ///
        /// 物品类型一律走游戏自带的 `type_*` 翻译（`TagDisplayName`）：
        /// 我们自己那份 `buy_tag_household` 写的是「家居用品」，游戏写「日用品」，
        /// 同一件东西在汇总行和格子说明里出现两个名字，所以删掉这层自译。
        /// 只有游戏没有类型键的标签（容器、口径、物资）才用模组自己的文案。
        /// </summary>
        private static string BuyTagName(string tag)
        {
            string key = (tag ?? string.Empty).Trim();
            if (key.Length == 0) return key;

            // 一律先用原版：游戏的 `type_*` 表覆盖了全部 63 个类型，
            // 收购标签里的 WATER / FOOD / HOUSEHOLD_GOOD / SUBSTANCE / ALL_TYPE 都在其中。
            string vanilla = TagDisplayName(key);
            if (!string.IsNullOrEmpty(vanilla) &&
                !string.Equals(vanilla, key, StringComparison.OrdinalIgnoreCase))
            {
                return vanilla;
            }

            // 原版确实没有对应类型键的极少数标签，才用模组自己的文案
            switch (key)
            {
                case "LIQUID_CONTAINER_TAG": return ModEntry.T("buy_tag_container");
                case "GUN_CALIBER_TAG": return ModEntry.T("buy_tag_caliber");
                default: return key;
            }
        }

        /// <summary>
        /// 货单里的一个物品预览：与图鉴格子同一套交互（悬停原生提示框、
        /// 左键看来源 / 右键看去处、开启拿取后可拖出）。
        /// 没有精灵图时用 AddButton 兜底，保证文字格子也能点、也能悬停。
        /// </summary>
        private static void RenderGoods(CustomUIBuilder builder, string id, bool match, int index, bool emptyOnly, List<Sprite> rotate = null)
        {
            // 容器：按当前客人算出他卖的这瓶水的图标（见 CatalogWindow.BuildClientVariants）。
            string ignoredComposition;
            List<Sprite> fulls = null;

            if (!emptyOnly && !string.IsNullOrEmpty(SourceSeed))
                CatalogWindow.BuildClientVariants(id, SourceWater, out ignoredComposition, out fulls);

            // 空瓶格：图集里不带液体段的那张 `<size>`（`normal` / `xl` / `pitcher`…）。
            // 不能用 CompositeIconFor —— 图鉴那份实例是灌过水的，合成出来是满瓶。
            var sprite = emptyOnly
                ? (CatalogWindow.EmptyContainerIcon(id) ?? CatalogWindow.CompositeIconFor(id))
                : (fulls != null && fulls.Count > 0 ? fulls[0] : CatalogWindow.CompositeIconFor(id));

            // 容器类（水瓶这类）在池子里可能取不到合成图，退回空容器图 / 本体图，
            // 免得格子上变成一串截断的文字
            if (sprite == null) sprite = CatalogWindow.EmptyContainerIcon(id);
            if (sprite == null) sprite = CatalogWindow.IconFor(id);

            // `random_performance_module` 这类占位 id 本身没有图，
            // 借同族那个具体模块的图标顶上
            if (sprite == null)
            {
                string alias = AliasIconId(id);
                if (alias != null) sprite = CatalogWindow.CompositeIconFor(alias);
                if (sprite == null && alias != null) sprite = CatalogWindow.IconFor(alias);
            }

            // 标签源（`tag:XXX`）要轮播一整类物品，图标序列由调用方给
            if (rotate != null && rotate.Count > 0) sprite = rotate[0];

            var entry = RecipeIndex.FindEntry(id);
            string elementId = "g" + index;

            bool hasButton = sprite == null;
            if (!hasButton)
            {
                float w, h;
                CatalogWindow.FitIcon(sprite, IconSide, out w, out h);
                builder.AddImage(sprite, w, h, elementId);

                // 标签源的轮播优先于容器的水种轮播（同一个格子不会两者都有）
                var cycle = rotate != null && rotate.Count > 1
                    ? rotate
                    : (fulls != null && fulls.Count > 1 ? fulls : null);

                if (cycle != null)
                {
                    var cell = builder.Last;
                    if (cell != null && cell.image != null)
                    {
                        _cellAnims[elementId] = new CellAnimation
                        {
                            Sprites = cycle,
                            Index = 0,
                            Image = cell.image
                        };
                    }
                }
            }
            else
            {
                builder.AddButton(Shorten(ItemName(id)), CellSelect(entry), elementId);
            }

            var element = builder.Last;
            MarkMatch(element, match, hasButton);
            CatalogWindow.BindCell(element, entry, hasButton, emptyOnly, !emptyOnly);
        }

        /// <summary>无图标格子的左键动作；找不到目录条目时给个空动作，别把 null 传给按钮。</summary>
        private static Action CellSelect(ItemEntry entry)
        {
            if (entry == null) return delegate { };
            return CatalogWindow.SelectAction(entry);
        }

        /// <summary>给任意物品格子补上交互（配方页的材料 / 工具 / 产物也用这一套）。</summary>
        private static void BindGoods(CustomUIBuilder builder, string id, bool hasButton)
        {
            if (id == null) return;
            CatalogWindow.BindCell(builder.Last, RecipeIndex.FindEntry(id), hasButton);
        }

        /// <summary>
        /// 检索命中的格子：**只有一层**，和格子底板一样大，但是绿的。
        ///
        /// - 按钮格子（没有图标）自带一层 ButtonNormal 灰板。直接把那层灰板
        ///   换成原版绿色贴图即可，rect 完全不动，天然与灰板等大、且不会叠两层。
        ///   之前把绿底做成前置兄弟画在灰板下面，反而被灰板盖住，只剩一圈绿边。
        /// - 图标格子的 Image 就是物品图标，不能覆盖；这时才补一层等大的绿底，
        ///   作为格子的前置兄弟（父级 Graphic 先于子级绘制，子节点会盖住图标），
        ///   用 ignoreLayout 让网格不给它排位置，末尾同步到格子坐标。
        /// </summary>
        private static void MarkMatch(CustomUIElement element, bool match, bool hasButton)
        {
            if (element == null || !match) return;

            try
            {
                // 灰板就地换绿：一层，尺寸不变
                if (hasButton && element.image != null)
                {
                    if (ApplyVanillaHighlight(element.image))
                    {
                        // SpriteSwap 会在悬停 / 按下时把灰板贴图换回来，禁掉状态切换
                        if (element.button != null)
                            element.button.transition = UnityEngine.UI.Selectable.Transition.None;
                    }
                    else
                    {
                        Warn("vanilla highlight unavailable, skip green cell");
                    }
                    return;
                }

                var cell = element.Rect;
                if (cell == null || cell.parent == null) return;

                var go = new GameObject("match", typeof(RectTransform), typeof(UnityEngine.UI.Image));
                go.transform.SetParent(cell.parent, false);
                go.transform.SetSiblingIndex(cell.GetSiblingIndex());   // 绘制在格子之前

                var image = go.GetComponent<UnityEngine.UI.Image>();
                image.raycastTarget = false;
                if (!ApplyVanillaHighlight(image))
                {
                    Warn("vanilla highlight unavailable, skip green cell");
                    UnityEngine.Object.DestroyImmediate(go);
                    return;
                }

                var ignore = go.AddComponent<UnityEngine.UI.LayoutElement>();
                ignore.ignoreLayout = true;

                _highlights.Add(new KeyValuePair<RectTransform, RectTransform>(
                    (RectTransform)go.transform, cell));
            }
            catch (Exception e)
            {
                Warn("mark match: " + e.Message);
            }
        }

        /// <summary>
        /// 大标题的阵营名用原版本地化：CSV 的 faction 列填的就是 ClientName 表 key，
        /// 跟 StoreReputation.InitReputation 里 factionDisplay 的取法一致
        /// （faction_name_lower_level / _upper_level / _security / _black_market /
        ///  _revolution / _cartel，原版有 zh/en/ru 等全语种翻译）。
        /// 无阵营 / 地点是我们自己的分组标签，游戏里没有对应阵营，走模组文案。
        /// </summary>
        private static string FactionTitle(SourceInfo source)
        {
            string faction = source == null ? null : source.Faction;
            if (string.IsNullOrEmpty(faction))
                return ModEntry.T(source != null && source.Kind == SourceKind.Location
                    ? "src_locations" : "src_clients");

            if (faction.StartsWith("faction_name_", StringComparison.Ordinal))
            {
                string name;
                try
                {
                    name = LocHelper.GetLocalizedClientName(faction);
                }
                catch (Exception e)
                {
                    Warn("faction name '" + faction + "': " + e.Message);
                    return faction;
                }

                // 没翻译时游戏回显 key 或空串：先试模组自己的表（faction_name_church
                // 这类游戏没有的阵营，只有我们才提供名字），最后才退回 key
                if (string.IsNullOrEmpty(name) ||
                    name.IndexOf("faction_name_", StringComparison.Ordinal) >= 0 ||
                    name.IndexOf("Translation Error", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string own = ModEntry.T(faction);
                    return string.IsNullOrEmpty(own) || string.Equals(own, faction, StringComparison.Ordinal)
                        ? faction
                        : own;
                }

                return name;
            }

            string text = ModEntry.T(faction);
            return string.Equals(text, faction, StringComparison.Ordinal) ? faction : text;
        }

        // ---------------------------------------------------------------- 战利品页

        /// <summary>
        /// 战利品页：一件物品的来源 = 「使用某个箱子，可能开出它」。
        /// 一页一张表：上面是箱子本身，下面把这箱子**所有**可能的产出按图鉴格子铺开。
        /// 表 id 多数就是箱子物品 id（`med_box`…），不是的话那格退化成文字。
        /// </summary>
        private static void RenderLootPage(CustomUIBuilder builder, List<LootTableInfo> loot)
        {
            var table = loot[_page];
            if (table == null) return;

            builder.AddHeader(Loc("src_loot", "掉落池"), "src_loot");

            // 表 id 多数是掉落池（junkTable / medicalTable…），不是物品 id。
            // 若 `box_pools.csv` 登记了「箱子 → 这个池」，就画那个箱子；
            // 没登记才退回池名文字。
            string boxId = LootSource.BoxForPool(table.TableId) ?? table.TableId;
            var boxEntry = RecipeIndex.FindEntry(boxId);

            // 箱子旁边并排画那张卡（`box_pools.csv` 第 3 列，或锁标签推出来）
            string keyId = LootKeyFor(boxId, boxEntry);

            if (boxEntry == null)
            {
                // 这个池没有对应的箱子：写清「哪个场合在用它」——
                // 垃圾堆、小偷随身货、拾荒者货单之类，玩家真正遇到池的地方。
                var uses = LootSource.UsesFor(table.TableId);
                var names = new List<string>(uses.Length);
                for (int i = 0; i < uses.Length; i++)
                {
                    string name = PoolUseName(uses[i]);
                    if (!string.IsNullOrEmpty(name)) names.Add(name);
                }

                if (names.Count > 0)
                {
                    builder.AddLabel(Loc("loot_where", "用这个池的地方"), "loot_where");
                    builder.AddLabel(string.Join(" · ", names.ToArray()), "loot_where_list");
                }
                else
                {
                    builder.AddLabel(PoolName(table.TableId), "loot_pool");
                }
            }

            var pair = new List<string>(2);
            if (boxEntry != null) pair.Add(boxId);
            if (!string.IsNullOrEmpty(keyId)) pair.Add(keyId);

            RenderItemGrid(builder, pair.ToArray(), 800);

            builder.AddLabel(Loc("loot_opens", "可能开出"), "loot_opens");

            RenderLootItems(builder, table);
        }

        /// <summary>
        /// 取文案，取不到（返回 key 本身）就用兜底文字 ——
        /// 免得界面上冒出一串 `loot_opens` 这种键名。
        /// </summary>
        private static string Loc(string key, string fallback)
        {
            string text = ModEntry.T(key);
            if (string.IsNullOrEmpty(text) || string.Equals(text, key, StringComparison.Ordinal))
                return fallback;

            return text;
        }

        /// <summary>
        /// 开这个表要哪张卡。优先用 `box_pools.csv` 里写死的，
        /// 没写就退回按锁标签推（`LockHelper.LOCK_ID`），再没有就是不需要卡。
        /// </summary>
        private static string LootKeyFor(string boxId, ItemEntry boxEntry)
        {
            string mapped = LootSource.KeyForBoxId(boxId);
            if (!string.IsNullOrEmpty(mapped)) return mapped;

            if (boxEntry == null || boxEntry.Item == null) return null;

            return LockKeys.KeyForBox(boxEntry.Item);
        }

        /// <summary>
        /// 去处侧：这箱子 / 这张卡能开出什么。
        /// 结构和来源侧一样，只是不显示「需要卡」—— 因为当前物品自己就是箱或卡。
        /// </summary>
        private static void RenderLootUsePage(CustomUIBuilder builder, List<LootTableInfo> loot)
        {
            var table = loot[_page];
            if (table == null) return;

            builder.AddHeader(Loc("loot_use", "开启"), "loot_use");

            // 当前物品自己就是箱子（经 `box_pools.csv` 映射到这张表）时，
            // 箱子格画它自己，而不是池名。
            string boxId = LootSource.BoxForPool(table.TableId) ?? table.TableId;

            // 当前物品自己是箱子（映射到这张表）时，箱子格画它自己
            if (_entry != null && LootSource.AsBox(_entry.Id).Count > 0) boxId = _entry.Id;

            var boxEntry = RecipeIndex.FindEntry(boxId);

            // 箱子旁边并排一张卡：这是「箱子 + 卡」这一对
            string keyId = LootKeyFor(boxId, boxEntry);

            if (boxEntry == null) builder.AddLabel(PoolName(table.TableId), "loot_pool");

            var pair = new List<string>(2);
            if (boxEntry != null) pair.Add(boxId);
            if (!string.IsNullOrEmpty(keyId)) pair.Add(keyId);

            RenderItemGrid(builder, pair.ToArray(), 900);

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[loot] use-page entry=" + (_entry == null ? "?" : _entry.Id)
                                 + " table=" + table.TableId
                                 + " box=" + boxId + "(found=" + (boxEntry != null) + ")"
                                 + " key=" + (keyId ?? "-")
                                 + " items=" + (table.Items == null ? 0 : table.Items.Count));

            builder.AddLabel(Loc("loot_opens", "可能开出"), "loot_opens");

            RenderLootItems(builder, table);
        }

        /// <summary>
        /// 去处侧的「锁信息」页：箱子显示自己的锁 id 和需要的卡，卡显示自己能开的锁。
        /// 掉落表是场景掉落池（`junkTable` / `medicalTable`…），不是箱子物品本身，
        /// 所以箱子开出的内容给不出来 —— 这一页只给「锁」这一层确定的信息。
        /// </summary>
        private static void RenderLockPage(CustomUIBuilder builder)
        {
            var item = _entry == null ? null : _entry.Item;

            builder.AddHeader(Loc("loot_use", "开启"), "lock_title");

            string boxLockId = LockKeys.LockIdOf(item);
            string keyLockId = LockKeys.TagLockId(item);

            if (!string.IsNullOrEmpty(boxLockId))
            {
                builder.AddLabel(Loc("lock_id", "锁编号") + "：" + boxLockId, "lock_id");

                string keyId = LockKeys.KeyForBox(item);

                if (!string.IsNullOrEmpty(keyId))
                {
                    builder.AddLabel(Loc("loot_needs", "需要卡"), "loot_needs");

                    builder.BeginRow(6f);
                    RenderUseItem(builder, keyId, "lockkey");
                    builder.AddFlexibleSpace();
                    builder.End();
                }
            }

            if (!string.IsNullOrEmpty(keyLockId))
            {
                builder.AddLabel(Loc("lock_opens", "可开的锁") + "：" + keyLockId, "lock_opens");

                if (LockKeys.IsMasterKey(keyLockId))
                    builder.AddLabel(Loc("lock_master", "命令卡可开所有锁"), "lock_master");
            }
        }

        /// <summary>
        /// 掉落池 id → 可读名。认不出来的原样显示，方便发现新池子。
        /// 已知的 12 个来自日志：`[loot] tables=13 ids=…`
        /// </summary>
        /// <summary>
        /// 解析 `loot_pools.csv` 里的一个「使用处」记号：
        /// `key:loot_use_junk_pile` 取模组本地化，`item:scav_crate` 取物品显示名，
        /// 其它记号原样当文字。
        /// </summary>
        private static string PoolUseName(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;

            string value = token.Trim();

            if (value.StartsWith("key:", StringComparison.OrdinalIgnoreCase))
            {
                string key = value.Substring(4).Trim();
                if (key.Length == 0) return null;

                string text = ModEntry.T(key);
                return string.IsNullOrEmpty(text) || string.Equals(text, key, StringComparison.Ordinal)
                    ? null
                    : text;
            }

            if (value.StartsWith("item:", StringComparison.OrdinalIgnoreCase))
            {
                string id = value.Substring(5).Trim();
                if (id.Length == 0) return null;

                var entry = RecipeIndex.FindEntry(id);
                return entry == null ? id : entry.DisplayName;
            }

            return value;
        }

        private static string PoolName(string tableId)
        {
            switch ((tableId ?? string.Empty).Trim())
            {
                case "POI_dumpingGround": return ModEntry.T("pool_dumping");
                case "junkTable": return ModEntry.T("pool_junk");
                case "accessCardTable": return ModEntry.T("pool_access_card");
                case "allModuleTable": return ModEntry.T("pool_module_all");
                case "t1moduleTable": return ModEntry.T("pool_module_t1");
                case "t2moduleTable": return ModEntry.T("pool_module_t2");
                case "makeshiftWeaponTable": return ModEntry.T("pool_weapon_makeshift");
                case "materialTable": return ModEntry.T("pool_material");
                case "householdTable": return ModEntry.T("pool_household");
                case "packedFoodTable": return ModEntry.T("pool_food_packed");
                case "toolTable": return ModEntry.T("pool_tool");
                case "medicalTable": return ModEntry.T("pool_medical");
                default: return tableId;
            }
        }

        /// <summary>把一张战利品表的全部产出铺成图鉴格子。</summary>
        private static void RenderLootItems(CustomUIBuilder builder, LootTableInfo table)
        {
            var items = table == null ? null : table.Items;
            if (items == null || items.Count == 0) return;

            var gridHost = builder.CurrentContainer;
            builder.BeginGrid(GridColumns, CellWidth, CellHeight, CellSpacing);

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || string.IsNullOrEmpty(item.Id)) continue;

                bool match = _entry != null && string.Equals(item.Id, _entry.Id, StringComparison.Ordinal);
                RenderGoods(builder, item.Id, match, i, false);
            }

            builder.End();

            if (gridHost != null)
            {
                var gridLayout = EnsureLayoutElement(gridHost);
                gridLayout.flexibleHeight = 0f;
                gridLayout.minHeight = CellHeight;
            }
        }

        // ---------------------------------------------------------------- 使用页

        /// <summary>
        /// 使用页：一件事一行 —— 哪件东西被怎么用掉、换回了什么。
        /// 例：开箱子 → 内容物；喝掉液体好运 → 空瓶；绷带缠玻璃片 → 玻璃刀。
        /// </summary>
        private static void RenderUsePage(CustomUIBuilder builder, List<UseInfo> uses)
        {
            var use = uses[_page];
            if (use == null) return;

            builder.AddLabel(ModEntry.T(UseKindKey(use.Kind)), "use_kind");

            var sources = new List<string>(use.Sources ?? new string[0]);

            // 标签源是一整类，看有没有能画的（比如 SPECIES_HUMAN 只挂在
            // GameCharacterItem 上，注册表里一件都没有）
            bool hasTagCells = false;
            if (use.SourceTags != null)
            {
                for (int i = 0; i < use.SourceTags.Length; i++)
                {
                    if (ItemsWithTag(use.SourceTags[i]).Count > 0) { hasTagCells = true; break; }
                }
            }

            if (sources.Count > 0 || hasTagCells)
            {
                builder.AddLabel(ModEntry.T("use_source"), "use_source");

                if (sources.Count > 0)
                    RenderItemGrid(builder, sources.ToArray(), 600);

                // 每个标签单独一格；当前物品自己在标签里就固定显示自己，否则轮播。
                // 标签格圈进自己的容器，轮播时只重建这一块（不整页重画）。
                if (use.SourceTags != null && use.SourceTags.Length > 0)
                {
                    builder.BeginColumn(2f);
                    var stripHost = builder.CurrentContainer;
                    builder.End();

                    var strip = new TagStrip
                    {
                        Host = stripHost,
                        Tags = use.SourceTags,
                        Base = 640 + _tagStripSeq * 8,
                        CurrentId = _entry == null ? null : _entry.Id
                    };
                    _tagStripSeq++;
                    _tagStrips.Add(strip);

                    RenderTagStrip(strip);
                }
            }

            // 全是内部 `_TAG` 时这一行会是空的，别留空标题
            if (use.SourceTags != null && use.SourceTags.Length > 0)
            {
                string tagLine = DescribeTags(use.SourceTags);
                if (!string.IsNullOrEmpty(tagLine))
                    builder.AddLabel(tagLine, "use_source_tags");
            }

            builder.AddLabel(ModEntry.T("rec_result"), "rec_result");
            RenderItemGrid(builder, use.Produces, 700);

            if (!string.IsNullOrEmpty(use.Note))
                builder.AddLabel(ModEntry.T(use.Note), "use_note");
        }

        /// <summary>
        /// 带某个标签的所有物品 id（按注册表顺序）。
        /// 标签源代表**一整类**目标（任意带 ADV_GUN_TAG 的枪、任意带 BLADE_ITEM_TAG 的刀），
        /// 所以这些图标要合成一个轮播格，而不是只画第一件。
        /// </summary>
        private static List<string> ItemsWithTag(string tag)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(tag)) return list;

            var all = CatalogDataBuilder.All;
            for (int i = 0; i < all.Count; i++)
            {
                var item = all[i] != null ? all[i].Item : null;
                if (item == null) continue;

                // `ADV_GUN_TAG` 这类是真标签；`WEAPON` / `FOOD`（客人收购词）
                // 是 itemTypes，两个都试一遍。
                bool hit;
                try { hit = item.IsTag(tag) || item.IsGameItemType(tag); }
                catch { hit = false; }
                if (hit) list.Add(all[i].Id);
            }

            // 一个标签命中 0 件时记一行：求购区块空着多半是这里没匹配上（每个标签只记一次）
            if (list.Count == 0 && _emptyTagLogged.Add(tag) && ModEntry.Log != null)
                ModEntry.Log.Log("[tag] '" + tag + "' matched 0 items");

            return list;
        }

        private static readonly HashSet<string> _emptyTagLogged = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>把标签翻译成人话；没收录的原样显示。</summary>
        private static string DescribeTags(string[] tags)
        {
            if (tags == null || tags.Length == 0) return string.Empty;

            var names = new List<string>(tags.Length);
            for (int i = 0; i < tags.Length; i++)
            {
                string tag = (tags[i] ?? string.Empty).Trim();

                // 内部 `_TAG` 不写名字：它们会展开成具体物品，标签名是代码细节
                if (IsInternalTag(tag)) continue;

                switch (tag)
                {
                    case "SPECIES_HUMAN": names.Add(ModEntry.T("use_tag_humanoid")); break;
                    case "SPECIES_XENO": names.Add(ModEntry.T("use_tag_xeno")); break;
                    // 其余一律先问游戏自己的物品类型表（`type_weapon` / `type_food` /
                    // `type_household_good` / `type_document` …），取不到再原样显示。
                    default: names.Add(TagDisplayName(tag)); break;
                }
            }

            return string.Join(" / ", names.ToArray());
        }

        /// <summary>
        /// 这件物品自己的类型标签（`GameItem.GetGameItemType()`），
        /// 就是游戏 tooltip 上显示的那一串：枪械 / 武器 / 食物 / 水 / 日用品 / 奢侈品 / 文档…
        /// 用 `type_*` 本地化，取不到的原样显示。
        /// </summary>
        private static string DescribeItemTypes(ItemEntry entry)
        {
            var item = entry != null ? entry.Item : null;
            if (item == null) return null;

            var names = new List<string>();

            try
            {
                var types = item.GetGameItemType();
                if (types != null)
                {
                    for (int i = 0; i < types.Count; i++)
                    {
                        string name = TagDisplayName(types[i]);
                        if (!string.IsNullOrEmpty(name) && !names.Contains(name))
                            names.Add(name);
                    }
                }
            }
            catch { }

            return names.Count > 0 ? string.Join(" / ", names.ToArray()) : null;
        }

        /// <summary>
        /// 游戏内部标记（`ADV_GUN_TAG` / `BLADE_ITEM_TAG` / `BROKEN_MACHINE_TAG`）：
        /// 只给代码判定用，玩家侧一律展开成具体物品，不显示标签名。
        /// </summary>
        private static bool IsInternalTag(string tag)
        {
            return !string.IsNullOrEmpty(tag) &&
                   tag.EndsWith("_TAG", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 把 `HOUSEHOLD_GOOD` / `DOCUMENT` / `WEAPON` 这类词换成游戏自带的本地化。
        ///
        /// 游戏有 63 个 `type_*` 键（`type_household_good`、`type_document`、
        /// `type_weapon`、`type_food`、`type_brevage` …），就是物品类型表；
        /// 规则是 `type_` + 小写。取不到（`ADV_GUN_TAG` 这种）就原样返回，
        /// 由 `DescribeTags` 的 switch 兜住。
        /// </summary>
        private static string TagDisplayName(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return tag;

            string text = LookupTypeKey(tag.Trim());
            if (text != null) return text;

            // `LIQUID_CONTAINER_TAG` / `GUN_CALIBER_TAG` 这类客户端标签：
            // 去掉 `_TAG` 再按类型表查一次（`type_liquid_container` …），
            // 仍然查不到才由调用方用模组文案兜底。
            string trimmed = tag.Trim();
            if (trimmed.EndsWith("_TAG", StringComparison.OrdinalIgnoreCase))
            {
                text = LookupTypeKey(trimmed.Substring(0, trimmed.Length - 4));
                if (text != null) return text;
            }

            return tag;
        }

        /// <summary>
        /// 按游戏自己的物品类型表查名字：`type_` + 小写。
        /// 依次试 mechanic / UI / 通用表；取不到（或接口把 key 原样回显）返回 null。
        /// </summary>
        private static string LookupTypeKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            string key = "type_" + name.ToLowerInvariant();

            string text = null;
            try { text = LocHelper.GetLocalizedMechanicOrDefault(key, null); } catch { }
            if (string.IsNullOrEmpty(text))
            {
                try { text = LocHelper.GetLocalizedUIOrDefault(key, null); } catch { }
            }
            if (string.IsNullOrEmpty(text))
            {
                try { text = LocHelper.GetLocalized(key, null); } catch { }
            }

            // 查不到时接口会回显 key，或者返回 `Translation Error"xxx" in yyy` ——
            // 后者以前被当成有效翻译，界面上就出现「近战武器 / Translation Error" in type_welder」
            if (string.IsNullOrEmpty(text) ||
                string.Equals(text, key, StringComparison.OrdinalIgnoreCase) ||
                text.IndexOf("Translation Error", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return null;
            }

            return text;
        }

        /// <summary>使用页里的一个物品格子：有图就画图，没图退化成按钮，点击打开图鉴。</summary>
        private static void RenderUseItem(CustomUIBuilder builder, string id, string prefix)
        {
            if (string.IsNullOrEmpty(id)) return;

            var entry = RecipeIndex.FindEntry(id);

            // 容器类物品要带液体覆盖层（`liquid_luck` 这类本体是瓶子、颜色靠修饰器层画的），
            // 所以先走 CompositeIconFor，取不到再退回本体图。
            var sprite = CatalogWindow.CompositeIconFor(id);
            if (sprite == null) sprite = CatalogWindow.IconFor(id);

            if (sprite != null)
            {
                float w, h;
                CatalogWindow.FitIcon(sprite, IconSide, out w, out h);
                builder.AddImage(sprite, w, h, prefix);
            }
            else
            {
                builder.AddButton(Shorten(entry == null ? id : entry.DisplayName),
                    CellSelect(entry), prefix);
            }

            BindGoods(builder, id, sprite == null);
        }

        /// <summary>使用方式的标题文案。</summary>
        private static string UseKindKey(string kind)
        {
            switch ((kind ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "open": return "use_kind_open";
                case "drink": return "use_kind_drink";
                case "apply": return "use_kind_apply";
                case "combine": return "use_kind_combine";
                default: return "use_kind_use";
            }
        }

        // ---------------------------------------------------------------- 配方页

        private static void RenderRecipePage(CustomUIBuilder builder, List<RecipeInfo> recipes)
        {
            var recipe = recipes[_page];

            builder.AddLabel(MachineName(recipe), "rec_machine");
            RenderRecipe(builder, recipe);
        }

        private static string MachineName(RecipeInfo recipe)
        {
            if (recipe == null) return "-";

            string name = recipe.MachineId;
            var entry = RecipeIndex.FindEntry(recipe.MachineId);
            if (entry != null && !string.IsNullOrEmpty(entry.DisplayName)) name = entry.DisplayName;

            return name ?? "-";
        }

        private static void RenderRecipe(CustomUIBuilder builder, RecipeInfo recipe)
        {
            builder.AddLabel(ModEntry.T("rec_mats"), "rec_mats");
            builder.BeginRow(8f);
            RenderIngredients(builder, recipe.Materials, "m");
            builder.AddFlexibleSpace();
            builder.End();

            if (recipe.Tools.Count > 0)
            {
                builder.AddLabel(ModEntry.T("rec_tools"), "rec_tools");
                builder.BeginRow(8f);
                RenderIngredients(builder, recipe.Tools, "t");
                builder.AddFlexibleSpace();
                builder.End();
            }

            builder.AddLabel(ModEntry.T("rec_result"), "rec_result");
            builder.BeginRow(8f);
            RenderOutputs(builder, recipe.Outputs);
            builder.AddFlexibleSpace();
            builder.End();
        }

        private static void RenderIngredients(CustomUIBuilder builder, List<RecipeIngredient> ingredients, string prefix)
        {
            if (ingredients == null || ingredients.Count == 0)
            {
                builder.AddLabel("-", prefix + "_none");
                return;
            }

            for (int i = 0; i < ingredients.Count; i++)
            {
                var ing = ingredients[i];
                if (ing == null) continue;

                string id = FirstId(ing);
                var sprite = id == null ? null : CatalogWindow.IconFor(id);

                if (sprite != null)
                {
                    float w, h;
                    CatalogWindow.FitIcon(sprite, IconSide, out w, out h);
                    builder.AddImage(sprite, w, h, prefix + i);
                }
                else
                {
                    builder.AddButton(Shorten(ing.DisplayName), CellSelect(RecipeIndex.FindEntry(id)), prefix + i);
                }

                BindGoods(builder, id, sprite == null);
                builder.AddLabel("×" + ing.Amount, prefix + i + "_n");
            }
        }

        private static void RenderOutputs(CustomUIBuilder builder, List<RecipeOutput> outputs)
        {
            if (outputs == null || outputs.Count == 0)
            {
                builder.AddLabel("-", "o_none");
                return;
            }

            for (int i = 0; i < outputs.Count; i++)
            {
                var output = outputs[i];
                if (output == null) continue;

                var entry = RecipeIndex.FindEntry(output.Id);
                var sprite = CatalogWindow.IconFor(output.Id);

                if (sprite != null)
                {
                    float w, h;
                    CatalogWindow.FitIcon(sprite, IconSide, out w, out h);
                    builder.AddImage(sprite, w, h, "o" + i);
                }
                else
                {
                    builder.AddButton(Shorten(entry == null ? output.Id : entry.DisplayName),
                        CellSelect(entry), "o" + i);
                }

                BindGoods(builder, output.Id, sprite == null);
                if (output.Amount > 1) builder.AddLabel("×" + output.Amount, "o" + i + "_n");
            }
        }

        private static string FirstId(RecipeIngredient ing)
        {
            if (ing == null) return null;
            if (ing.Ids != null && ing.Ids.Count > 0) return ing.Ids[0];
            return null;
        }

        /// <summary>
        /// `ModuleHelper.GetRandomModuleId()` 会产出 `random_performance_module` 这类
        /// **占位 id**：它们不是真物品，开箱时才被换成同族的具体模块，所以没有自己的图。
        /// 这里给它们借同族模块的图标，格子上至少不会是一串截断字母。
        /// </summary>
        private static string AliasIconId(string id)
        {
            switch ((id ?? string.Empty).Trim())
            {
                case "random_performance_module": return "system_module_overclock";
                case "random_efficiency_module": return "system_module_eco";
                case "random_quality_module": return "system_module_fineness";
                case "random_node": return "common_electronic";
                default: return null;
            }
        }

        /// <summary>占位 id 的可读名；不认识的返回 null。</summary>
        private static string PlaceholderName(string id)
        {
            switch ((id ?? string.Empty).Trim())
            {
                case "random_performance_module": return Loc("item_random_perf", "随机性能模块");
                case "random_efficiency_module": return Loc("item_random_eff", "随机效率模块");
                case "random_quality_module": return Loc("item_random_qual", "随机品质模块");
                case "random_node": return Loc("item_random_node", "随机节点");
                default: return null;
            }
        }

        private static readonly Dictionary<string, string> _nameCache =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// 物品 id → 显示名。图鉴里没有的物品（例如掉落池里的
        /// `random_performance_module` 这种占位 id）也要有个能看的名字，
        /// 否则格子上会是一串截断的英文字母。
        /// </summary>
        private static string ItemName(string id)
        {
            if (string.IsNullOrEmpty(id)) return "?";

            var entry = RecipeIndex.FindEntry(id);
            if (entry != null) return entry.DisplayName;

            string placeholder = PlaceholderName(id);
            if (!string.IsNullOrEmpty(placeholder)) return placeholder;

            string cached;
            if (_nameCache.TryGetValue(id, out cached)) return cached;

            string name = null;

            try
            {
                var item = DirectoryMaster.Item(id, false);
                if (item != null) name = item.GetDisplayName(false);
            }
            catch { }

            if (string.IsNullOrEmpty(name) || string.Equals(name, id, StringComparison.Ordinal))
                name = id.Replace('_', ' ');

            _nameCache[id] = name;
            return name;
        }

        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text)) return "?";
            return text.Length <= 10 ? text : text.Substring(0, 9) + "…";
        }

        private static UnityEngine.UI.LayoutElement EnsureLayoutElement(RectTransform rect)
        {
            var element = rect.GetComponent<UnityEngine.UI.LayoutElement>();
            if (element == null) element = rect.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
            return element;
        }

        private static void Warn(string message)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("[recipe-ui] " + message);
        }
    }
}