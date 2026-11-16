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

        private const float CellWidth = 56f;
        private const float CellHeight = 56f;
        private const float CellSpacing = 6f;

        // 一页最多几行货，放满就翻页；最后一页不足时窗口会自己缩到内容高度。
        // 窗口加高后一页最多 6 行（48 件），屏幕矮时自动少放几行、靠翻页补。
        private const int MaxGridRows = 6;

        /// <summary>
        /// 网格可用宽：用布局完成后量到的容器真宽（缓存于 <see cref="_gridContentWidth"/>）；
        /// 首帧还没量到时先按「窗口宽 - 留白」假定，渲染完会校正并最多重排一轮。
        /// </summary>
        private static float GridContentWidth()
        {
            return _gridContentWidth > 60f ? _gridContentWidth : WinWidth - 36f;
        }

        /// <summary>每行格数随容器宽自适应：可用宽除以「格宽 + 间距」。</summary>
        private static int GridColumns()
        {
            int cols = Mathf.FloorToInt((GridContentWidth() + CellSpacing) / (CellWidth + CellSpacing));
            return Mathf.Clamp(cols, 4, 12);
        }

        /// <summary>
        /// 格宽拉到把可用宽占满（GridLayoutGroup 的格子不会自己拉伸，
        /// 余量会全堆在右边缘）。
        /// </summary>
        private static float GridCellWidth()
        {
            int cols = GridColumns();
            float cell = (GridContentWidth() - (cols - 1) * CellSpacing) / cols;
            return Mathf.Clamp(cell, 44f, 80f);
        }

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

        private static int ItemsPerPage { get { return GridColumns() * RowsPerPage; } }

        /// <summary>客人立绘最长边。客人精灵本身一般是 256 见方，再放大会开始发虚。</summary>
        private const float PortraitMaxSide = 300f;

        /// <summary>同一客人的多种立绘轮播间隔（秒）。</summary>
        private const float PortraitSwitchSeconds = 1f;

        private static float _lastHeight = -1f;

        // 网格可用宽：布局完成后量到的 _bodyHost 真宽（-1 = 还没量到，用假定值）
        private static float _gridContentWidth = -1f;
        private static int _lastColumnsUsed = -1;

        // ---- 客人立绘轮播：同一来源的多张精灵按顺序每秒换一张 ----
        private static List<Sprite> _portraitSprites;
        private static int _portraitIndex;
        private static float _portraitNextAt;

        /// <summary>下一次把窗口提到最前的时间点（0 = 不需要）。</summary>
        private static float _raiseAt;
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
        /// 合成与使用都是**双向**大类：来源窗口和用途窗口看到同一份内容
        /// （配方页当前物品在哪列高亮自明方向；使用页投入在上、产物在下）。
        /// </summary>
        private const int CatClient = 0;
        private const int CatLocation = 1;
        private const int CatCraft = 2;      // 双向：产出它的配方 + 把它当材料用掉的配方
        private const int CatLoot = 6;       // 双向：能开出它的地点池（同一地点合并成一页）
        private const int CatMachine = 10;   // 双向：这件东西自己是机器（界面 + 产出配方）
        private const int CatUse = 11;       // 双向：使用互查 + 箱子/卡的开启关系

        private sealed class Category
        {
            internal int Kind;
            internal List<SourceInfo> Sources;

            /// <summary>
            /// 客人（双向）里的收购方：谁**收**这件物品。与 <see cref="Sources"/>
            /// （卖给他 / 能摆上桌）分开存，页面上每个客人的收购清单单独成页。
            /// </summary>
            internal List<SourceInfo> Wanted;

            internal List<RecipeInfo> Recipes;
            internal List<UseInfo> Uses;
            internal List<LootTableInfo> Loot;

            /// <summary>
            /// 掉落池对应的「地方」：游戏本地化键（`location_dumping_grounds`
            /// 这种地点/人名）。有它就按它给分类起名，不再统一叫「掉落池」。
            /// 箱子的开启关系走「使用」大类，不再有 item:箱子 掉落页。
            /// </summary>
            internal string Site;
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

            /// <summary>true = 这一页是这位客人的收购清单（「他在收」），不画货单。</summary>
            internal bool Buys;

            internal int Count { get { return Cells == null ? 0 : Cells.Count; } }
        }

        private static CustomUIWindow _window;
        private static RectTransform _bodyHost;

        private static ItemEntry _entry;

        /// <summary>false = 来源方向（左键）；true = 用途方向（右键）。
        /// 只影响标题栏的方向标注，页面内容是同一份。</summary>
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
        private static readonly List<MachineRecipeStrip> _machineStrips = new List<MachineRecipeStrip>();

        private static int _tagStripSeq;

        /// <summary>查不到出处的掉落池：别的来源都没有时才兜底列成「掉落池」大类。</summary>

        private sealed class TagStrip
        {
            internal UnityEngine.RectTransform Host;   // 这一条的容器（重建时清它）
            internal string[] Tags;                 // 这一条里要画的标签
            internal string[] Items;                // 紧跟在标签后面、画在同一行的具体物品
            internal int Base;                      // 格子 id 起点
            internal string CurrentId;              // 当前详情物品，用于「它自己就在标签里」判定
            internal HashSet<string> Exclude;       // 已经单独列出的具体物品，别在标签格里重复画
        }

        /// <summary>机器静态配方的一条轮播：一个纯度组一个，逐秒切到下一条配方。</summary>
        private sealed class MachineRecipeStrip
        {
            internal UnityEngine.RectTransform Host;
            internal MachinePanels.RecipeGroup Group;
            internal int Variant;
            internal int Module;
            internal int Base;
        }

        /// <summary>左键（来源方向）打开。页面同一份，方向标注不同。</summary>
        internal static void OpenFor(ItemEntry entry)
        {
            OpenFor(entry, false);
        }

        /// <summary>
        /// 打开（或复用）窗口。来源与去处已合并成同一页：
        /// 客人 / 地点 / 机器 / 合成(双向) / 使用(双向，含开启) / 掉落池。
        /// 左键（asUse=false）标题栏标「来源」，右键（true）标「用途」——
        /// 页面同一份，方向靠标注与打开入口区分。
        /// </summary>
        internal static void OpenFor(ItemEntry entry, bool asUse)
        {
            if (entry == null) return;

            _entry = entry;
            _asUse = asUse;
            _category = 0;
            _page = 0;

            if (_window == null || !_window.IsAlive) Create();
            else
            {
                // 复用时窗口可能只是被隐藏了：先显示再重画
                try { _window.Show(); } catch { }
                Render();
            }

            if (_window != null && _window.IsAlive) Raise();

            // 这一帧往后一点点再提一次：点击事件里图鉴自己也会 BringToFront，
            // 谁后执行谁在上面，晚一帧补一次才稳。
            _raiseAt = Time.unscaledTime + 0.05f;

            if (ModEntry.Log != null)
            {
                try
                {
                    ModEntry.Log.Log("[recipe] open id=" + entry.Id + " asUse=" + asUse
                                     + " cats=" + _categories.Count + " " + CategoryDigest()
                                     + " " + State());
                }
                catch { }
            }
        }

        /// <summary>
        /// 把窗口提到最前并确保处于显示状态。
        ///
        /// `BringToFront` 只处理窗口管理器自己的顺序，可能仍被同层的其他节点压住，
        /// 所以再补一次 `SetAsLastSibling`。
        /// </summary>
        private static void Raise()
        {
            if (_window == null || !_window.IsAlive) return;

            try { _window.Show(); } catch { }
            try { _window.BringToFront(); } catch { }

            try
            {
                var root = _window.root;
                if (root != null) root.transform.SetAsLastSibling();
            }
            catch { }
        }

        /// <summary>一行窗口状态，用来判断「打开了但看不见」是尺寸还是层级问题。</summary>
        private static string State()
        {
            try
            {
                var rect = _window.Rect;
                if (rect == null) return "state rect=null";

                var size = rect.rect;
                var pos = rect.position;

                return string.Format(
                    "visible={0} size={1:F0}x{2:F0} pos=({3:F0},{4:F0}) sibling={5} parent={6}",
                    _window.IsVisible, size.width, size.height, pos.x, pos.y,
                    rect.GetSiblingIndex(), rect.parent == null ? "?" : rect.parent.name);
            }
            catch (Exception e) { return "state error: " + e.Message; }
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
            _raiseAt = 0f;
            _categories = new List<Category>();
            _sourcePages.Clear();
            StopPortrait();
            _cellAnims.Clear();
            _tagRollIndex.Clear();   // 换物品后标签轮播从头开始
            _tagStrips.Clear();
            _machineStrips.Clear();
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

            // 打开后补提一次到最前（点击那一帧图鉴可能又把自己提到前面）
            if (_raiseAt > 0f && Time.unscaledTime >= _raiseAt)
            {
                _raiseAt = 0f;
                Raise();
            }

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

            // 机器静态配方：同一纯度组内轮播不同投料方案（模块也轮播）
            TickMachineRecipes();

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
            builder.BeginGrid(GridColumns(), GridCellWidth(), CellHeight, CellSpacing);

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

            // 具体物品紧接在标签格后面，同一张网格里往下排——列数够时就在同一行，
            // 不会再被拆成上下两块。
            if (strip.Items != null && strip.Items.Length > 0)
            {
                for (int i = 0; i < strip.Items.Length; i++)
                {
                    string id = strip.Items[i];
                    if (string.IsNullOrEmpty(id)) continue;

                    bool match = _entry != null && string.Equals(id, _entry.Id, StringComparison.Ordinal);
                    RenderGoods(builder, id, match, strip.Base + 200 + i, false);
                }
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

            // 同一套大类，**页面内容按打开方向取**：左键=来源方向（谁卖它 / 从哪得到它），
            // 右键=用途方向（谁收它 / 它参与什么）。方向标注（ApplyDirectionLabel）
            // 与之一致。两个方向查到的是同一个页面结构，内容各是各的。

            if (_asUse)
            {
                // ===== 用途方向 =====

                // 收它的客人：页里只有「他在收」清单页
                AddClientCategory(null, SourceIndex.WantedBy(_entry.Id, _entry.Item));

                // 他自己是机器 → 机器界面 + 产出配方（机器是双向内容，两个方向都给）
                if (MachinePanels.For(_entry.Id) != null ||
                    MachinePanels.RecipesFor(_entry.Id) != null ||
                    RecipeIndex.ByMachine(_entry.Id).Count > 0)
                {
                    _categories.Add(new Category
                    {
                        Kind = CatMachine,
                        Recipes = RecipeIndex.ByMachine(_entry.Id)
                    });
                }

                // 合成：把它当材料用掉的配方
                AddCraftCategory();

                // 使用：能拿它做什么（含箱子/卡的开启——他是箱/卡时自己就是来源）
                AddUseCategory(UseIndex.Using(_entry.Id, _entry.Item));
            }
            else
            {
                // ===== 来源方向 =====

                // 卖给他（能摆上桌）的客人：页里是他的货单
                AddClientCategory(SourceIndex.For(_entry.Id), null);

                // 地点各自成类——配给站、杰克逊博士
                AddSourceCategory(CatLocation, SourceIndex.For(_entry.Id));

                // 他自己是机器 → 机器界面（电池 / 模块 / 功能槽 / 容器网格）+ 产出配方。
                // 判定不再依赖运行时配方索引（游戏机器的 recipeManager 静态为空）：
                // 静态面板表 / 静态配方表里有它就算机器。
                if (MachinePanels.For(_entry.Id) != null ||
                    MachinePanels.RecipesFor(_entry.Id) != null ||
                    RecipeIndex.ByMachine(_entry.Id).Count > 0)
                {
                    _categories.Add(new Category
                    {
                        Kind = CatMachine,
                        Recipes = RecipeIndex.ByMachine(_entry.Id)
                    });
                }

                // 合成：产出它的配方
                AddCraftCategory();

                // 使用：怎么得到它（含箱内容物反查——装着它的箱子开启关系）
                AddUseCategory(UseIndex.Producing(_entry.Id));

                // 掉落池（同一地点合并成一页）：地点池并集。
                // 箱子的开启关系已并入「使用」，这里不挂 item:箱子 掉落页。
                var loot = LootSource.For(_entry.Id);
                if (loot.Count > 0)
                {
                    // 掉落池本身不该是一个大类：玩家遇到的是「垃圾倾倒场能翻出什么」。
                    // 按池所属的地方分组，每个地方一个大类，池的并集在里面展开。
                    var order = new List<string>();
                    var buckets = new Dictionary<string, List<LootTableInfo>>(StringComparer.Ordinal);

                    for (int i = 0; i < loot.Count; i++)
                    {
                        var table = loot[i];
                        if (table == null) continue;

                        string site = LootSource.SiteFor(table.TableId);
                        if (string.IsNullOrEmpty(site) ||
                            site.StartsWith("item:", StringComparison.OrdinalIgnoreCase))
                        {
                            // 查不到出处的池只是内部数据，不单独成篇；
                            // item: 站点（箱子）的开启关系已并入「使用」。
                            continue;
                        }

                        List<LootTableInfo> bucket;
                        if (!buckets.TryGetValue(site, out bucket))
                        {
                            bucket = new List<LootTableInfo>();
                            buckets[site] = bucket;
                            order.Add(site);
                        }

                        bucket.Add(table);
                    }

                    // 地点页 = 这个地方**全部池子**的并集（不是只含当前物品的那几张）：
                    // 玩家问的是「垃圾倾倒场能给什么」，垃圾堆 / 残骸 / 拾荒 /
                    // 背包发现合在一页里；当前物品命中了其中至少一张才进这个站点。
                    for (int i = 0; i < order.Count; i++)
                    {
                        string site = order[i];

                        var siteTables = new List<LootTableInfo>();
                        var allTables = LootSource.Tables;
                        for (int t = 0; t < allTables.Length; t++)
                        {
                            var tt = allTables[t];
                            if (tt == null) continue;

                            if (string.Equals(LootSource.SiteFor(tt.TableId), site, StringComparison.Ordinal))
                                siteTables.Add(tt);
                        }

                        if (siteTables.Count == 0) continue;

                        _categories.Add(new Category
                        {
                            Kind = CatLoot,
                            Loot = new List<LootTableInfo> { MergeLootTables(siteTables) },
                            Site = site
                        });
                    }
                }
            }

            if (_category < 0) _category = 0;
            if (_category >= _categories.Count) _category = Math.Max(0, _categories.Count - 1);
        }

        /// <summary>
        /// 客人合并、地点拆开。
        ///
        /// 客人动辄几十个，逐个成类会把分类条撑爆，所以全部收进一个「客人」大类，
        /// 页内翻页切换，一页一个客人（名字写在页内那行）。
        /// 地点只有几个，各自成类更直观：标题直接是地点名（杰克逊博士的店）。
        /// </summary>
        /// <summary>
        /// 客人大类，**按方向取内容**：左键（来源）传 <paramref name="sources"/>——
        /// 卖给他（货单里有这件，能摆上桌）的客人，页里画货单；
        /// 右键（用途）传 <paramref name="wanted"/>——收这件的客人，页里只有
        /// 「他在收」清单页（见 <c>BuildSourcePages</c> 的方向分支）。
        /// </summary>
        private static void AddClientCategory(List<SourceInfo> sources, List<SourceInfo> wanted)
        {
            var clients = new List<SourceInfo>();

            // sources / wanted 都可能为 null——右键只传 wanted（用途方向没有卖家货单），
            // 左键只传 sources（来源方向不做收购查询）
            if (sources != null)
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    var one = sources[i];
                    if (one == null || one.Kind != SourceKind.Client) continue;
                    clients.Add(one);
                }
            }

            // 收购方：卖家里没有的补在后面（同一客人既卖又收时，一页清单里两面都有）
            var buyers = new List<SourceInfo>();
            if (wanted != null)
            {
                for (int i = 0; i < wanted.Count; i++)
                {
                    var one = wanted[i];
                    if (one == null || one.Kind != SourceKind.Client) continue;
                    if (clients.Contains(one)) continue;
                    clients.Add(one);
                    buyers.Add(one);
                }
            }

            if (clients.Count == 0) return;

            var category = new Category { Kind = CatClient, Sources = clients, Wanted = buyers };
            _categories.Add(category);
        }

        private static void AddSourceCategory(int kind, List<SourceInfo> sources)
        {
            SourceKind target = kind == CatClient ? SourceKind.Client : SourceKind.Location;

            if (kind == CatLocation)
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    var one = sources[i];
                    if (one == null || one.Kind != target) continue;

                    _categories.Add(new Category
                    {
                        Kind = kind,
                        Sources = new List<SourceInfo> { one }
                    });
                }

                return;
            }

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

                // 标题栏直接用物品名（不再在正文里重复一个 header）。
                // SetTitle 每次都可能重建文字对象（对齐回默认左对齐），
                // 所以居中每轮渲染都补一次，不能只在首次开窗时设。
                try
                {
                    _window.SetTitle(_entry == null ? "?" : _entry.DisplayName);
                    CenterTitle();
                }
                catch (Exception e) { Warn("set title: " + e.Message); }

                // 标题栏左侧的「来源 / 用途」：左右键打开同一页，靠它区分方向
                ApplyDirectionLabel();

                // 大类翻页：按钮贴两端，名称居中；到头就禁用，不做循环
                builder.BeginRow(6f);
                builder.AddButton("<", delegate { TurnCategory(-1); }, "cat_prev");
                if (builder.Last != null) builder.Last.SetInteractable(_category > 0);
                builder.AddFlexibleSpace();
                builder.AddLabel(CategoryLabel(), "cat_title");
                if (builder.Last != null && builder.Last.text != null)
                    builder.Last.text.fontSize = 18f;   // 大类名放大：这是页面唯一的导航信息
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
                    builder.AddWarning(Loc("cat_none", "暂未收录"), "cat_empty");
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
                else if (category.Kind == CatMachine) RenderMachinePage(builder, category.Recipes);
                else if (category.Loot != null) RenderLootPage(builder, category.Loot, category.Site);
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

        private static TMPro.TMP_Text _dirLabel;

        /// <summary>
        /// 在标题栏左侧写「来源 / 用途」——左右键打开同一页，靠它区分方向。
        ///
        /// 窗口标题由 `CustomUIWindow.SetTitle` 居中绘制，没有「左侧留白」这种接口，
        /// 所以这里复制标题那个文字对象（字体、颜色、描边全都跟着），改成靠左摆放。
        /// 找不到标题节点就什么都不做——宁可没有这个标签，也不去猜坐标。
        /// </summary>
        private static void ApplyDirectionLabel()
        {
            if (_window == null || _entry == null) return;

            string dir = ModEntry.T(_asUse ? "dir_use" : "dir_source");
            if (string.IsNullOrEmpty(dir)) return;

            try
            {
                if (_dirLabel != null)
                {
                    _dirLabel.text = dir;
                    return;
                }

                var root = _window.Rect;
                if (root == null) return;

                TMPro.TMP_Text title = null;
                var texts = root.GetComponentsInChildren<TMPro.TMP_Text>(true);
                for (int i = 0; i < texts.Length; i++)
                {
                    if (texts[i] != null && texts[i].text == _entry.DisplayName)
                    {
                        title = texts[i];
                        break;
                    }
                }

                if (title == null || title.transform.parent == null) return;

                // 物品名居中：默认是左对齐，和左边的「来源/用途」挤在一起看着像两段标题
                try { title.alignment = TMPro.TextAlignmentOptions.Center; } catch { }

                // 就放在标题栏里，但让它的布局组忽略这份克隆：
                // 否则会被当成第三个子物体排在关闭按钮后面（跑到右边去）；
                // 挂到窗口根上又会被竖排布局组排到最底下。
                var bar = title.transform.parent as RectTransform;
                if (bar == null) return;

                var clone = UnityEngine.Object.Instantiate(title.gameObject, bar);
                clone.name = "directionLabel";

                try
                {
                    var ignore = clone.GetComponent<UnityEngine.UI.LayoutElement>();
                    if (ignore == null) ignore = clone.AddComponent<UnityEngine.UI.LayoutElement>();
                    ignore.ignoreLayout = true;
                }
                catch { }

                var rect = clone.transform as RectTransform;
                if (rect != null)
                {
                    float side = bar.rect.height > 4f ? bar.rect.height : 30f;

                    rect.anchorMin = new Vector2(0f, 0.5f);
                    rect.anchorMax = new Vector2(0f, 0.5f);
                    rect.pivot = new Vector2(0f, 0.5f);
                    rect.anchoredPosition = new Vector2(10f, 0f);
                    rect.sizeDelta = new Vector2(140f, side);
                }

                // 画在最上层，否则会被窗口背景/内容盖住
                try { clone.transform.SetAsLastSibling(); } catch { }

                _dirLabel = clone.GetComponent<TMPro.TMP_Text>();
                if (_dirLabel != null)
                {
                    _dirLabel.text = dir;
                    _dirLabel.raycastTarget = false;
                    try { _dirLabel.alignment = TMPro.TextAlignmentOptions.Left; } catch { }
                }
            }
            catch (Exception e) { Warn("direction label: " + e.Message); }
        }

        /// <summary>
        /// 把标题栏的物品名居中。游戏的 <c>SetTitle</c> 建出来的文字对象默认左对齐，
        /// 且每次重设标题都可能换新对象 —— 所以每轮渲染都调一次，
        /// 按文字内容找回标题再设对齐。
        /// </summary>
        private static void CenterTitle()
        {
            if (_window == null || _entry == null) return;

            try
            {
                var root = _window.Rect;
                if (root == null) return;

                string title = _entry.DisplayName;
                var texts = root.GetComponentsInChildren<TMPro.TMP_Text>(true);
                for (int i = 0; i < texts.Length; i++)
                {
                    if (texts[i] != null && texts[i].text == title)
                    {
                        texts[i].alignment = TMPro.TextAlignmentOptions.Center;
                        break;
                    }
                }
            }
            catch { }
        }

        private static string CategoryTitle(Category category)
        {
            if (category == null) return "?";

            // 掉落池按「地方」分大类：垃圾倾倒场 / 小偷 / 装着它的箱子
            if (category.Kind == CatLoot && !string.IsNullOrEmpty(category.Site))
                return SiteName(category.Site);

            switch (category.Kind)
            {
                // 客人合并在一个类里，标题写「客人」，是谁看页内那行；
                // 地点拆开成多个类，标题直接写地点名（配给站 / 杰克逊博士）
                case CatClient: return ModEntry.T("src_clients");
                case CatLocation: return SourceTitle(category);
                case CatCraft: return Loc("cat_craft", "合成");
                case CatUse: return Loc("cat_use", "使用");
                case CatLoot: return ModEntry.T("src_loot");
                case CatMachine: return Loc("cat_machine", "机器");
                default: return "?";
            }
        }

        /// <summary>把所有大类标题拼成一行，日志里能直接看出每页是什么。</summary>
        private static string CategoryDigest()
        {
            if (_categories.Count == 0) return "none";

            var text = new System.Text.StringBuilder();
            for (int i = 0; i < _categories.Count; i++)
            {
                if (i > 0) text.Append(" | ");
                text.Append(i + 1).Append(':').Append(CategoryTitle(_categories[i]));
            }

            return text.ToString();
        }

        /// <summary>来源类大类（客人 / 地点 / 求购）的标题：用来源自己的名字。</summary>
        private static string SourceTitle(Category category)
        {
            if (category == null || category.Sources == null || category.Sources.Count == 0) return "?";

            try
            {
                string name = SourceIndex.DisplayName(category.Sources[0]);
                if (!string.IsNullOrEmpty(name) && name != "?") return name;
            }
            catch { }

            return ModEntry.T("src_clients");
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

                // 布局完成后量一次容器真宽：比首帧的假定值准。
                // 列数受它影响时只多渲染一轮（这里不会再置位，不会循环）。
                float w = _bodyHost.rect.width;
                if (w > 320f && w < 900f && Math.Abs(w - 12f - _gridContentWidth) > 8f)
                {
                    _gridContentWidth = w - 12f;
                    if (GridColumns() != _lastColumnsUsed)
                    {
                        _lastColumnsUsed = GridColumns();
                        Render();
                        return;
                    }
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

                int rows = Math.Max(1, (page.Count + GridColumns() - 1) / GridColumns());
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

            for (int i = 0; i < category.Sources.Count; i++)
            {
                var source = category.Sources[i];
                if (source == null) continue;

                // 方向：右键（用途）客人页只有「他在收」清单页——他的货单
                // 是来源方向的内容，不混进用途页；左键（来源）画货单页。
                if (_asUse)
                {
                    if (category.Wanted != null && category.Wanted.Contains(source))
                    {
                        _sourcePages.Add(new SourcePage
                        {
                            Source = source,
                            Cells = new List<GoodsCell>(),
                            Buys = true
                        });
                    }
                    continue;
                }

                var items = source.Items;
                if (items == null || items.Length == 0)
                {
                    // 没有货单：不挂空页
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

            // 大标题：阵营，靠左。地点（杰克逊博士 / 配给站）没有阵营，
            // 「地点商店」标签也冗余——名字已经在分类标题上，不写。
            if (source == null || source.Kind != SourceKind.Location)
            {
                builder.AddHeader(FactionTitle(source), "src_faction");
                var faction = builder.Last;
                if (faction != null && faction.text != null)
                    faction.text.alignment = TextAlignmentOptions.Left;
            }

            // 小标题：客人名，居中。
            // 客人是「多个来源合并成一个类」，必须靠这行区分是谁；
            // 地点那种一个类一个来源，名字已经在分类标题上了，正文再写一遍是重复。
            bool showClientName = _categories != null
                                  && _category >= 0 && _category < _categories.Count
                                  && _categories[_category].Kind == CatClient;

            if (showClientName)
            {
                builder.AddLabel(SourceIndex.DisplayName(source), "src_client");
                var name = builder.Last;
                if (name != null && name.text != null)
                {
                    name.text.alignment = TextAlignmentOptions.Center;
                    name.text.fontSize = 20f;
                }
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
                builder.BeginGrid(GridColumns(), GridCellWidth(), CellHeight, CellSpacing);

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

            // 客人（双向）的收购页：这一页只画收购清单——
            // 收购标签（WATER/FOOD 这类整类目标）+ 专收的具体物品
            if (page.Buys)
            {
                RenderBuys(builder, source, false);
            }
        }

        /// <summary>
        /// 「他在收」区块。数据来自 `clients.csv` 第 10/11 列，
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

            if (withHeader) builder.AddHeader(ModEntry.T("src_clients"), "src_buys");

            if (hasTags)
            {
                // 标签格要排除已经单独列出的具体物品，否则同一个瓶子会画两遍：
                // 囤水者既收任意 WATER、又专收 bottled_water_premium，
                // 不排除的话「他在收」下面会出现两格一模一样的水。
                HashSet<string> exclude = null;
                if (hasItems)
                {
                    exclude = new HashSet<string>(StringComparer.Ordinal);
                    for (int k = 0; k < items.Length; k++)
                        if (!string.IsNullOrEmpty(items[k])) exclude.Add(items[k]);
                }

                // 每个收购标签给一格「这一类」的物品：当前物品属于该类就固定显示它，
                // 否则轮播该类全部物品。
                //
                // 标签格和「具体物品」共用同一张网格：以前两者各建一个 grid，
                // 结果被排成上下两行。现在物品跟着标签一起交给 RenderTagStrip，
                // 由它在同一行里接着画。
                builder.BeginColumn(2f);
                var buyStripHost = builder.CurrentContainer;
                builder.End();

                var buyStrip = new TagStrip
                {
                    Host = buyStripHost,
                    Tags = tags,
                    Items = items,
                    Base = 800 + _tagStripSeq * 8,
                    CurrentId = _entry == null ? null : _entry.Id,
                    Exclude = exclude
                };
                _tagStripSeq++;
                _tagStrips.Add(buyStrip);
                RenderTagStrip(buyStrip);
                return;
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
            builder.BeginGrid(GridColumns(), GridCellWidth(), CellHeight, CellSpacing);

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
                default: break;
            }

            // 兜底：数据里误把物品 id 写进「标签」列（`black_injector` 这种），
            // 不要把它原样印在界面上——能查到物品就用物品名，查不到才退回原字符串。
            try
            {
                var entry = RecipeIndex.FindEntry(key);
                if (entry != null && !string.IsNullOrEmpty(entry.DisplayName))
                    return entry.DisplayName;
            }
            catch { }

            return key;
        }

        /// <summary>
        /// 货单里的一个物品预览：与图鉴格子同一套交互（悬停原生提示框、
        /// 左键看来源 / 右键看去处、开启拿取后可拖出）。
        /// 没有精灵图时用 AddButton 兜底，保证文字格子也能点、也能悬停。
        /// </summary>
        private static void RenderGoods(CustomUIBuilder builder, string id, bool match, int index, bool emptyOnly, List<Sprite> rotate = null)
        {
            // 占位 id（random_performance_module 这类）直接换成同族的**默认模块**：
            // 池子里塞的是占位，游戏生成时才掷骰；图鉴展示不做随机实例，
            // 统一显示默认模块的目录条目（图标 / 名字 / 详情都是本体）。
            id = LootSource.ExpandPlaceholder(id);

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

            // 变体状态物品（portable_water_purifier_used 之类）借本体图标
            if (sprite == null)
            {
                string baseId = IconFallbackId(id);
                if (baseId != null)
                {
                    sprite = CatalogWindow.CompositeIconFor(baseId);
                    if (sprite == null) sprite = CatalogWindow.IconFor(baseId);
                }
            }

            // 标签源（`tag:XXX`）要轮播一整类物品，图标序列由调用方给
            if (rotate != null && rotate.Count > 0) sprite = rotate[0];

            var entry = RecipeIndex.FindEntry(id);

            // 预构建键（portable_water_purifier_used 这类）不在目录里：照抄容器的写法，
            // 给它一条带实例的条目（样本在隔离随机流里生成），后面的 BindCell /
            // 原生提示框链 / 拖拽拿取就全和普通容器格子一样了。
            if (entry == null && PreBuiltItems.IsPreBuilt(id))
                entry = CatalogWindow.PreBuiltEntry(id);

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
                builder.AddButton(Shorten(entry == null ? ItemName(id) : entry.DisplayName),
                    CellSelect(entry), elementId);
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

            // 占位 id 换成同族默认模块（与 RenderGoods 同一规则），不做随机实例
            id = LootSource.ExpandPlaceholder(id);

            var entry = RecipeIndex.FindEntry(id);
            if (entry == null && PreBuiltItems.IsPreBuilt(id)) entry = CatalogWindow.PreBuiltEntry(id);

            CatalogWindow.BindCell(builder.Last, entry, hasButton);
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
        /// 使用大类，**按方向取记录**：左键（来源）= 怎么得到它（当前物品是产物，
        /// 含装着它的箱子的开启关系）；右键（用途）= 能拿它做什么（当前物品是投入，
        /// 含箱子/卡自己的开启关系）。类名都叫「使用」，页内「产物」分界线上投入、
        /// 下产出。
        /// </summary>
        private static void AddUseCategory(List<UseInfo> seed)
        {
            var merged = new List<UseInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // 箱子/卡的「开启」关系（合成 open 记录，并进「使用」大类）：
            // 三条路（箱自身 / 卡自身 / 内容物反查）合出来，再按方向筛——
            // 来源方向只留「当前物品是产物」的，用途方向只留「当前物品是投入」的。
            var opens = SynthesizeOpenRelations();
            for (int i = 0; i < opens.Count; i++)
            {
                var u = opens[i];
                if (u == null || !IsUseInDirection(u)) continue;
                if (!seen.Add(UseSignature(u))) continue;
                merged.Add(u);
            }

            // csv 使用记录（调用方已按方向查好：Producing / Using）
            AppendUseSeed(merged, seen, seed);

            if (merged.Count > 0)
                _categories.Add(new Category { Kind = CatUse, Uses = merged });
        }

        /// <summary>
        /// 使用记录的方向判定：来源方向（左键）= 当前物品是**产物**；
        /// 用途方向（右键）= 当前物品是**投入**（在来源里；标签源由
        /// <c>UseIndex.Using</c> 查询天然按方向命中，合成记录没有标签源）。
        /// </summary>
        private static bool IsUseInDirection(UseInfo u)
        {
            if (u == null || _entry == null) return false;

            if (_asUse)
            {
                var s = u.Sources;
                if (s != null)
                {
                    for (int i = 0; i < s.Length; i++)
                        if (string.Equals(s[i], _entry.Id, StringComparison.Ordinal)) return true;
                }
                return false;
            }

            var p = u.Produces;
            if (p != null)
            {
                for (int i = 0; i < p.Length; i++)
                    if (string.Equals(p[i], _entry.Id, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>使用记录的签名（方式 + 来源 + 产物）：同一条关系只留一份。</summary>
        private static string UseSignature(UseInfo u)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(u.Kind ?? "");
            sb.Append('|');
            var s = u.Sources ?? new string[0];
            for (int i = 0; i < s.Length; i++) { sb.Append(s[i]); sb.Append(','); }
            sb.Append('>');
            var p = u.Produces ?? new string[0];
            for (int i = 0; i < p.Length; i++) { sb.Append(p[i]); sb.Append(','); }
            return sb.ToString();
        }

        private static void AppendUseSeed(List<UseInfo> merged, HashSet<string> seen, List<UseInfo> seed)
        {
            if (seed == null) return;

            for (int i = 0; i < seed.Count; i++)
            {
                var u = seed[i];
                if (u == null || !seen.Add(UseSignature(u))) continue;
                merged.Add(u);
            }
        }

        /// <summary>
        /// 合成箱子/卡的「开启」使用记录（并进「使用」大类，不再单独开「开启」类）：
        ///   他自己是箱子：Sources=[箱, 需要的卡]，Produces=[内容物并集]；
        ///   他自己是卡：能开的每个箱一条，Sources=[卡, 箱]，Produces=[内容物]；
        ///   他是箱内容物：装着它的每个箱一条（BoxesForItem）——来源方向可查。
        /// 「箱子 + 需要的卡 → 可能开出 → 内容物」的渲染就是使用页的样子
        /// （来源格子在上、产物分界线、产物格子在下），两个方向同一页。
        /// </summary>
        private static List<UseInfo> SynthesizeOpenRelations()
        {
            var result = new List<UseInfo>();
            if (_entry == null) return result;

            string self = _entry.Id;

            // 他自己是箱子：每个内容物表一条（来源 = 箱 + 需要的卡）
            var asBox = LootSource.AsBox(self);
            for (int i = 0; i < asBox.Count; i++)
            {
                var t = asBox[i];
                if (t == null || t.Items == null || t.Items.Count == 0) continue;

                var src = new List<string>(2) { self };
                string keyId = LootKeyFor(self, _entry);
                if (!string.IsNullOrEmpty(keyId)) src.Add(keyId);

                result.Add(new UseInfo { Sources = src.ToArray(), Kind = "open", Produces = LootIds(t) });
            }

            // 他自己是卡：能开哪些箱（每个箱一条，来源 = 卡 + 箱）
            var asKey = LootSource.ForKey(_entry.Item);
            if (asKey.Count == 0) asKey = LootSource.BoxesForKeyId(self);
            for (int i = 0; i < asKey.Count; i++)
            {
                var t = asKey[i];
                if (t == null || t.Items == null || t.Items.Count == 0) continue;

                string boxId = LootSource.BoxForPool(t.TableId) ?? t.TableId;
                result.Add(new UseInfo { Sources = new[] { self, boxId }, Kind = "open", Produces = LootIds(t) });
            }

            // 箱内容物反查：装着它的箱子（open 记录的来源方向）
            var boxes = LootSource.BoxesForItem(self);
            for (int b = 0; b < boxes.Count; b++)
            {
                string boxId = boxes[b];
                var boxEntry = RecipeIndex.FindEntry(boxId);
                var boxTables = LootSource.AsBox(boxId);
                if (boxTables == null || boxTables.Count == 0) continue;

                var products = new List<string>();
                for (int i = 0; i < boxTables.Count; i++)
                {
                    var ids = LootIds(boxTables[i]);
                    for (int k = 0; k < ids.Length; k++)
                        if (!products.Contains(ids[k])) products.Add(ids[k]);
                }

                var src = new List<string>(2) { boxId };
                string keyId = LootKeyFor(boxId, boxEntry);
                if (!string.IsNullOrEmpty(keyId)) src.Add(keyId);

                result.Add(new UseInfo { Sources = src.ToArray(), Kind = "open", Produces = products.ToArray() });
            }

            return result;
        }

        /// <summary>一张掉落表的全部物品 id（去重，按池内记录的原样）。</summary>
        private static string[] LootIds(LootTableInfo t)
        {
            if (t == null || t.Items == null) return new string[0];

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var ids = new List<string>();
            for (int i = 0; i < t.Items.Count; i++)
            {
                var it = t.Items[i];
                if (it == null || string.IsNullOrEmpty(it.Id)) continue;
                if (!seen.Add(it.Id)) continue;
                ids.Add(it.Id);
            }
            return ids.ToArray();
        }

        /// <summary>
        /// 合成大类，**按方向取配方**：左键（来源）= 产出它的配方；
        /// 右键（用途）= 把它当材料用掉的配方。类名都叫「合成」，
        /// 配方页材料与产物自明方向（当前物品在哪列就高亮在哪列）。
        /// </summary>
        private static void AddCraftCategory()
        {
            var merged = new List<RecipeInfo>();

            if (_asUse)
            {
                // 传 Item 是为了让 `tag:XXX` 那类「整类目标」也能命中
                // （拆解只对带 ADV_GUN_TAG 的枪、屠宰只对带 BLADE_ITEM_TAG 的刀）。
                var consumed = RecipeIndex.Consuming(_entry.Id, _entry.Item);
                for (int i = 0; i < consumed.Count; i++)
                    if (consumed[i] != null && !merged.Contains(consumed[i])) merged.Add(consumed[i]);
            }
            else
            {
                var produced = RecipeIndex.Producing(_entry.Id);
                for (int i = 0; i < produced.Count; i++)
                    if (produced[i] != null && !merged.Contains(produced[i])) merged.Add(produced[i]);
            }

            if (merged.Count > 0)
                _categories.Add(new Category { Kind = CatCraft, Recipes = merged });
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
        /// <summary>
        /// 来源侧：一件物品的来源 = 「使用某个箱子，可能开出它」。
        /// 渲染与去处侧共用 <see cref="RenderLootBody"/> —— 同一条
        /// 「箱子 ↔ 内容物」关系从两个方向打开看到的是同一页。
        /// </summary>
        private static void RenderLootPage(CustomUIBuilder builder, List<LootTableInfo> loot, string site)
        {
            RenderLootBody(builder, loot, site);
        }

        /// <summary>
        /// 掉落关系**共用渲染器**（来源侧反查到的箱子 / 地点池，与去处侧箱子 / 卡自己展开，
        /// 走的是这同一段代码）：是箱子就画「箱子 + 需要的卡 →『可能开出』→ 内容物格子」；
        /// 是地点池就只有内容物格子（地点名在分类标题上，不重复）。
        /// </summary>
        private static void RenderLootBody(CustomUIBuilder builder, List<LootTableInfo> loot, string site)
        {
            var table = loot[_page];
            if (table == null) return;

            // 这张表对应的箱子：站点给了 item:<箱子> 就用它（来源反查）；
            // 没给就从池反查（箱子经 box_pools 映射 / 卡开箱）；再不行退回表 id
            // （箱子自己的内容物表 TableId 就是箱子 id）。地点池没有箱子条目，跳过箱子区。
            string boxId = !string.IsNullOrEmpty(site) &&
                           site.StartsWith("item:", StringComparison.OrdinalIgnoreCase)
                ? site.Substring(5).Trim()
                : (LootSource.BoxForPool(table.TableId) ?? table.TableId);

            var boxEntry = string.IsNullOrEmpty(boxId) ? null : RecipeIndex.FindEntry(boxId);

            if (boxEntry != null)
            {
                // 箱子旁边并排画那张卡（`box_pools.csv` 第 3 列，或锁标签推出来）
                string keyId = LootKeyFor(boxId, boxEntry);

                var pair = new List<string>(2);
                pair.Add(boxId);
                if (!string.IsNullOrEmpty(keyId)) pair.Add(keyId);

                RenderItemGrid(builder, pair.ToArray(), 800);

                builder.AddLabel(Loc("loot_opens", "可能开出"), "loot_opens");
            }

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
        /// 掉落池 id → 可读名。认不出来的原样显示，方便发现新池子。
        /// 已知的 12 个来自日志：`[loot] tables=13 ids=…`
        /// </summary>
        /// <summary>
        /// 直接查游戏自己的本地化表（不经过我们那份 csv）。
        ///
        /// 依次试 ClientName / UI / 机制 / 通用四张表；取不到（回显 key 或
        /// `Translation Error"…"`）都算失败，返回 null。
        /// </summary>
        private static string GameLocText(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;

            string text = null;

            try { text = LocHelper.GetLocalizedClientName(key); } catch { }
            if (IsMissingTranslation(text, key))
            {
                try { text = LocHelper.GetLocalizedUIOrDefault(key, null); } catch { }
            }
            if (IsMissingTranslation(text, key))
            {
                try { text = LocHelper.GetLocalizedMechanicOrDefault(key, null); } catch { }
            }
            if (IsMissingTranslation(text, key))
            {
                try { text = LocHelper.GetLocalized(key, null); } catch { }
            }

            return IsMissingTranslation(text, key) ? null : text;
        }

        /// <summary>空串、回显 key、或游戏那句 `Translation Error"…"` 都算没翻出来。</summary>
        private static bool IsMissingTranslation(string text, string key)
        {
            if (string.IsNullOrEmpty(text)) return true;
            if (string.Equals(text, key, StringComparison.OrdinalIgnoreCase)) return true;
            return text.IndexOf("Translation Error", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 一个「地方」的显示名：`item:&lt;箱子 id&gt;` 取物品名，
        /// 其余当成游戏本地化键（`location_dumping_grounds` → 垃圾倾倒场）。
        /// </summary>
        private static string SiteName(string site)
        {
            if (string.IsNullOrEmpty(site)) return null;

            if (site.StartsWith("item:", StringComparison.OrdinalIgnoreCase))
            {
                string id = site.Substring(5).Trim();
                if (id.Length == 0) return null;

                var entry = RecipeIndex.FindEntry(id);
                return entry == null ? id : entry.DisplayName;
            }

            return GameLocText(site) ?? site;
        }

        /// <summary>
        /// 同一地方的几张池子并成一张表：物品 id 并集去重（第一次出现的权重保留）。
        /// 池 id 取第一张的（只在调试日志里有意义）。
        /// </summary>
        private static LootTableInfo MergeLootTables(List<LootTableInfo> tables)
        {
            var merged = new LootTableInfo
            {
                TableId = tables != null && tables.Count > 0 ? tables[0].TableId : null
            };
            if (tables == null) return merged;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < tables.Count; i++)
            {
                var t = tables[i];
                if (t == null || t.Items == null) continue;

                for (int k = 0; k < t.Items.Count; k++)
                {
                    var it = t.Items[k];
                    if (it == null || string.IsNullOrEmpty(it.Id)) continue;
                    if (!seen.Add(it.Id)) continue;

                    // 不是物品的 id（POI 节点 junk_pile / fresh_scrap_pile 这类：
                    // 组表的成员既不是表也不是物品时会被当物品收进来）——
                    // 目录 / 预构建 / 本体映射全都解析不了，格子只会变成一排文字，过滤掉
                    if (RecipeIndex.FindEntry(it.Id) == null &&
                        !PreBuiltItems.IsPreBuilt(it.Id) &&
                        BaseItemId(it.Id) == null) continue;

                    merged.Items.Add(new LootItemRef { Id = it.Id, Weight = it.Weight });
                }
            }

            return merged;
        }

        /// <summary>把一张战利品表的全部产出铺成图鉴格子。</summary>
        private static void RenderLootItems(CustomUIBuilder builder, LootTableInfo table)
        {
            var items = table == null ? null : table.Items;
            if (items == null || items.Count == 0) return;

            var gridHost = builder.CurrentContainer;
            builder.BeginGrid(GridColumns(), GridCellWidth(), CellHeight, CellSpacing);

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
        /// 布局：来源格子在上，「产物」一行当分界线，产物格子在下——
        /// 方向 / 方式标签都不写（大类标题已经说了这是「使用」）。
        /// </summary>
        private static void RenderUsePage(CustomUIBuilder builder, List<UseInfo> uses)
        {
            var use = uses[_page];
            if (use == null) return;

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

        /// <summary>
        /// 机器页：上半部分按游戏窗口的面板顺序还原机器界面
        /// （电池 / 模块 / 功能槽 / 容器网格），下半部分是它自己的产出配方
        /// （工具 = 必需的模块，材料 = 原料，产物 = 做出来的东西）。
        /// 面板结构与标签来自 <see cref="MachinePanels"/>（抄自各机器类的 IL）。
        /// </summary>
        private static void RenderMachinePage(CustomUIBuilder builder, List<RecipeInfo> recipes)
        {
            var panels = MachinePanels.For(_entry.Id);
            if (panels != null && panels.Length > 0)
            {
                builder.AddLabel(Loc("mach_title", "机器界面"), "mach_title");

                for (int i = 0; i < panels.Length; i++)
                {
                    var panel = panels[i];
                    if (panel == null) continue;

                    string label = MachinePanelLabel(panel);

                    if (panel.Kind == MachinePanels.Grid)
                    {
                        builder.AddLabel(label, "mach_panel");
                        DrawGridShape(builder);
                        continue;
                    }

                    builder.BeginRow(6f, UnityEngine.TextAnchor.MiddleLeft);
                    builder.AddLabel(label, "mach_panel");
                    builder.AddFlexibleSpace();
                    DrawSlot(builder);
                    builder.End();
                }
            }

            // 静态产出配方：按纯度分档，每档一个轮播容器（同一档的不同投料方案
            // 每秒切一条）；游戏机器没有 CraftingRecipe，只能像面板一样静态收录。
            var groups = MachinePanels.RecipesFor(_entry.Id);
            if (groups != null && groups.Length > 0)
            {
                builder.AddLabel(Loc("mach_recipes", "产出配方"), "mach_recipes");

                for (int i = 0; i < groups.Length; i++)
                {
                    var group = groups[i];
                    if (group == null || group.Variants == null || group.Variants.Length == 0) continue;

                    if (i > 0) builder.AddSeparator("mach_sep", 4);

                    builder.BeginColumn(2f);
                    var host = builder.CurrentContainer;
                    builder.End();

                    var strip = new MachineRecipeStrip
                    {
                        Host = host,
                        Group = group,
                        Variant = 0,
                        Module = 0,
                        Base = 3200 + _machineStrips.Count * 24
                    };
                    _machineStrips.Add(strip);
                    RenderMachineRecipeStrip(strip);
                }
            }
            else if (recipes != null && recipes.Count > 0)
            {
                // 没有静态表的机器：退回运行时配方（目前游戏机器几乎都没有）
                builder.AddLabel(Loc("mach_recipes", "产出配方"), "mach_recipes");
                for (int i = 0; i < recipes.Count; i++)
                {
                    if (recipes[i] == null) continue;
                    if (i > 0) builder.AddSeparator("mach_sep", 4);
                    RenderRecipe(builder, recipes[i]);
                }
            }
        }

        /// <summary>
        /// 把一条机器配方重画到自己的容器里（清空 → 新建 builder → 标题 / 投入 / 模块 / 说明 / 产物）。
        /// <see cref="TickMachineRecipes"/> 每秒只重建这一小块，页面其余部分不动。
        /// 物品顺序按表里的「最优排列」（高纯度投料在前），不还原机器的槽位坐标。
        /// </summary>
        private static void RenderMachineRecipeStrip(MachineRecipeStrip strip)
        {
            if (strip == null || strip.Host == null || strip.Group == null) return;

            var manager = CustomUIManager.Instance;
            if (manager == null || _window == null || !_window.IsAlive) return;

            for (int i = _highlights.Count - 1; i >= 0; i--)
                if (_highlights[i].Key == null || _highlights[i].Value == null) _highlights.RemoveAt(i);

            CustomUIBuilder.ClearContainer(strip.Host);
            var builder = new CustomUIBuilder(manager, _window, strip.Host);

            var group = strip.Group;
            var variants = group.Variants;
            int vi = ((strip.Variant % variants.Length) + variants.Length) % variants.Length;
            var recipe = variants[vi];
            if (recipe == null) return;

            builder.AddLabel(Loc(group.TitleKey, group.TitleFallback), "mach_group");

            if (recipe.Inputs.Length > 0)
            {
                builder.AddLabel(Loc("mach_inputs", "投入"), "mach_sub");
                RenderItemGrid(builder, recipe.Inputs, strip.Base);
            }

            if (recipe.Modules.Length > 0)
            {
                // 模块也是轮播：一条配方里多个模块时每秒换一个，
                // 避免把「互斥的模块选项」画成一次装三个
                builder.AddLabel(Loc("mach_modules", "模块"), "mach_sub");
                int mi = recipe.Modules.Length == 1 ? 0
                    : ((strip.Module % recipe.Modules.Length) + recipe.Modules.Length) % recipe.Modules.Length;
                RenderItemGrid(builder, new[] { recipe.Modules[mi] }, strip.Base + 8);
            }

            string note = Loc(recipe.NoteKey, recipe.NoteFallback);
            if (!string.IsNullOrEmpty(note)) builder.AddLabel(note, "mach_note");

            if (recipe.Outputs.Length > 0)
            {
                builder.AddLabel(Loc("mach_outputs", "产物"), "mach_sub");
                RenderItemGrid(builder, recipe.Outputs, strip.Base + 16);
            }
        }

        /// <summary>每秒推进机器配方的轮播索引：配方组间独立，模块数多个时也单独轮。</summary>
        private static void TickMachineRecipes()
        {
            if (!IsOpen || _machineStrips.Count == 0) return;

            for (int i = 0; i < _machineStrips.Count; i++)
            {
                var strip = _machineStrips[i];
                if (strip == null || strip.Host == null || strip.Group == null) continue;

                var variants = strip.Group.Variants;
                if (variants == null || variants.Length == 0) continue;

                strip.Variant = (strip.Variant + 1) % variants.Length;

                var recipe = variants[strip.Variant];
                if (recipe != null && recipe.Modules.Length > 1)
                    strip.Module = (strip.Module + 1) % recipe.Modules.Length;

                try { RenderMachineRecipeStrip(strip); }
                catch (Exception e) { Warn("machine recipe: " + e.Message); }
            }
        }

        /// <summary>面板标签：优先游戏自己的 mech_*_label_* 键（跟随游戏语言），取不到用兜底。</summary>
        private static string MachinePanelLabel(MachinePanels.Panel panel)
        {
            if (panel == null) return "-";

            if (!string.IsNullOrEmpty(panel.GameLabelKey))
            {
                string text = null;
                try { text = LocHelper.GetLocalizedMechanicOrDefault(panel.GameLabelKey, null); } catch { }

                if (!string.IsNullOrEmpty(text) &&
                    !string.Equals(text, panel.GameLabelKey, StringComparison.OrdinalIgnoreCase) &&
                    text.IndexOf("Translation Error", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return text;
                }
            }

            switch (panel.Kind)
            {
                case MachinePanels.Battery: return Loc("mach_battery", "电池");
                case MachinePanels.Module: return Loc("mach_module", "模块");
                case MachinePanels.Note: return Loc("mach_note", "纸条");
                default: return panel.Fallback ?? "-";
            }
        }

        /// <summary>画容器网格：形状码 7 列一行，'1' 画格框，'0' 留空。</summary>
        private static void DrawGridShape(CustomUIBuilder builder)
        {
            string shape = MachinePanels.ContainerShape;
            var frame = MachineSlotSprite();

            // 布局还没就绪拿不到描边图时，退回字符画（等宽方块 + 全角空格对齐）
            if (frame == null)
            {
                for (int r = 0; r * MachinePanels.ShapeCols < shape.Length; r++)
                {
                    var row = new System.Text.StringBuilder();
                    for (int c = 0; c < MachinePanels.ShapeCols; c++)
                    {
                        int idx = r * MachinePanels.ShapeCols + c;
                        row.Append(idx < shape.Length && shape[idx] == '1' ? "▢" : "　");
                    }

                    builder.AddLabel(row.ToString(), "mach_grid_r" + r);
                }

                return;
            }

            for (int r = 0; r * MachinePanels.ShapeCols < shape.Length; r++)
            {
                builder.BeginRow(2f, UnityEngine.TextAnchor.MiddleCenter);

                for (int c = 0; c < MachinePanels.ShapeCols; c++)
                {
                    int idx = r * MachinePanels.ShapeCols + c;
                    if (idx < shape.Length && shape[idx] == '1')
                        builder.AddImage(frame, 18f, 18f, "mach_cell_" + r + "_" + c);
                    else
                        builder.AddSpace(18f);
                }

                builder.End();
            }
        }

        /// <summary>单格功能槽：一个格框。</summary>
        private static void DrawSlot(CustomUIBuilder builder)
        {
            var frame = MachineSlotSprite();
            if (frame != null) builder.AddImage(frame, 24f, 24f, "mach_slot");
            else builder.AddLabel("▢", "mach_slot_text");
        }

        /// <summary>
        /// 格框图：直接用游戏 RenderHandler 的血条描边（本来就是给 UI 框用的）。
        /// 拿不到返回 null，调用方走字符画兜底。
        /// </summary>
        private static Sprite _machineSlotSprite;

        private static Sprite MachineSlotSprite()
        {
            if (_machineSlotSprite != null) return _machineSlotSprite;

            var handler = RenderHandler.current;
            if (handler == null) return null;

            _machineSlotSprite = handler.healthBarOutline;
            return _machineSlotSprite;
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

                // 液体需求（水 / 酒精 / 酸碱…）：游戏里是容器的内容物不是物品，
                // 由液体管理器统一换成一只具体容器（bottled_water）展示，
                // 格子可点、可查、不再是没有图标的文字。
                string id = LiquidManager.Represent(ing) ?? FirstId(ing);
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

        private static string IconFallbackId(string id)
        {
            string baseId = BaseItemId(id);
            if (baseId == null) return null;

            return CatalogWindow.IconFor(baseId) != null ? baseId : null;
        }

        /// <summary>
        /// 特殊物品 id → 本体物品 id。实现在 <see cref="LootSource.BaseItemId"/>
        /// （数据层的真实 id 解析唯一入口），这里只是 UI 层的转发——
        /// 图标 / 名字 / 正反查索引必须共用同一个解析器，不能各写一份。
        /// </summary>
        internal static string BaseItemId(string id)
        {
            return LootSource.BaseItemId(id);
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
