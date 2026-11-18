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
        // 所有页面共用同一个窗口高度上限（包括机器页）：
        // 内容超出就交给自动分页，不再为某一类页面单独放宽/压缩上限。
        // 900 太小：机器页内容会被砍到 900，布局组只好把标题栏压到 0
        //（日志 [win-children] Header h=0）；这里再留出质量需求行的余量。
        private const float WinMaxHeight = 1040f;

        private const float WindowChrome = 64f;   // 标题栏 + 内容区上下留白
        private const float IconSide = 40f;

        private const float CellWidth = 56f;
        private const float CellHeight = 56f;
        private const float CellSpacing = 6f;

        // 每页行数不再写死上限：和机器页共用同一个 MaxContentHeight()，
        // 能装几行就装几行，装不下自动翻页，矮屏则自动少放几行。
        // （旧行为是硬封顶 6 行，导致普通页永远达不到机器页能用到的最大高度。）

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
                return Mathf.Max(1, rows);
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
        private const int CatMachineRecipe = 12;   // 双向：这件东西是某台机器的投入 / 模块 / 产出
        private const int CatReaction = 13;        // 双向：液体条目的成分反应（一条反应一页）

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

            /// <summary>
            /// 静态机器配方页（当前物品是投入 / 模块 / 产出时反查到的机器）。
            /// MachineGroups 只包含和当前物品相关的那些配方组，每组一页。
            /// </summary>
            internal string MachineId;
            internal List<MachinePanels.RecipeGroup> MachineGroups;

            /// <summary>成分反应页：一条反应一页。</summary>
            internal List<LiquidReaction> Reactions;
        }

        /// <summary>货单里的一个格子。有水也有空瓶的客人，容器会多出「空瓶」那一格。</summary>
        private sealed class GoodsCell
        {
            internal string Id;
            internal bool Empty;   // 只画空瓶：不显示成分、不参与轮播
        }

        /// <summary>
        /// 一个来源的**完整**货单块（不再预先切片）。
        /// 切片统一由 <see cref="BuildPagePlan"/> 按每页格数做。
        /// </summary>
        private sealed class SourceBlock
        {
            internal SourceInfo Source;
            internal List<GoodsCell> Cells;

            internal int Count { get { return Cells == null ? 0 : Cells.Count; } }
        }

        /// <summary>
        /// 一页 = 某个内容块（Block）里的一段。
        /// Start/Count 是块内条目范围；Count == 0 表示这一块不做切片、整块一页
        /// （配方 / 使用 / 机器纯度档这类「一条关系一页」的内容）。
        /// </summary>
        private struct PageSlice
        {
            internal int Block;
            internal int Start;
            internal int Count;

            internal bool IsWholeBlock { get { return Count <= 0; } }
        }

        /// <summary>
        /// 一种页面的分页描述。新增页面类型只需：
        ///   ① 给 <see cref="Category"/> 加一份数据字段；
        ///   ② 在 <see cref="EnsurePageTypes"/> 里注册一项。
        /// 中央的分页计划、页码钳制、翻页条都不用改。
        /// </summary>
        private sealed class PageType
        {
            /// <summary>内容块数量（客人数量 / 配方数量 / 机器纯度档数量 …）。</summary>
            internal Func<Category, int> BlockCount;

            /// <summary>第 block 块可切片的条目数；返回 0 表示整块一页。</summary>
            internal Func<Category, int, int> BlockItems;

            /// <summary>
            /// 第 block 块每页装得下几条；null = 用全局 ItemsPerPage。
            /// 来源页 / 掉落页按「这一块实际画不画立绘、箱子」自己算行数，
            /// 超出的行由分页计划自动放到下一页。
            /// </summary>
            internal Func<Category, int, int> PageItems;

            /// <summary>渲染某个块里的某一段。</summary>
            internal Action<CustomUIBuilder, Category, PageSlice> Render;
        }

        private static CustomUIWindow _window;
        private static RectTransform _bodyHost;

        // 窗口正文拆成三段，翻页时只重建中间那段：
        //   _chromeHost  固定：大类导航 + 物品类型
        //   _contentHost 可变：只有它随翻页 / 切类重建
        //   _pagerHost   固定：翻页条（页码文字与按钮状态原地更新）
        private static RectTransform _chromeHost;
        private static RectTransform _contentHost;
        private static RectTransform _pagerHost;

        private static CustomUIElement _catLabel;
        private static CustomUIElement _catPrev;
        private static CustomUIElement _catNext;
        private static CustomUIElement _pagerLabel;
        private static CustomUIElement _pagerPrev;
        private static CustomUIElement _pagerNext;

        /// <summary>当前大类的完整分页计划：翻页 / 页码上限的唯一依据。</summary>
        private static readonly List<PageSlice> _pagePlan = new List<PageSlice>();

        /// <summary>
        /// 来源块实测出来的每页容量（block → 条目数）。
        /// 公式只是初值；第一次真正排版后按 LayoutUtility 量到的剩余高度校正，
        /// 保证有立绘的页也能排满到和机器页相同的最大高度。
        /// </summary>
        private static readonly Dictionary<int, int> _sourceCapacity = new Dictionary<int, int>();

        private static bool _calibratingCapacity;

        /// <summary>当前正在渲染的那一页（_page 对应的切片）。</summary>
        private static PageSlice _currentSlice;

        /// <summary>页面类型注册表：kind → 分页与渲染规则。</summary>
        private static Dictionary<int, PageType> _pageTypes;

        private static ItemEntry _entry;

        /// <summary>false = 来源方向（左键）；true = 用途方向（右键）。
        /// 只影响标题栏的方向标注，页面内容是同一份。</summary>
        private static bool _asUse;

        private static int _category;
        private static int _page;

        private static List<Category> _categories = new List<Category>();
        private static readonly List<SourceBlock> _sourceBlocks = new List<SourceBlock>();

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

        /// <summary>机器页当前嵌入的原版机器窗口（真 GameGridInventory + 样本物品）。</summary>
        private static VanillaMachinePanel _vanillaMachine;
        private static int _vanillaVariant;   // 当前档内轮播到第几条配方

        // 机器页自适应高度用的实测值（布局跑完才有，只增不减）
        private static RectTransform _machineTopRow;    // 机器页顶部行（配方名 + 品质需求 + 机器图标）
        private static RectTransform _machinePagerRow;  // 机器页底部翻页条

        /// <summary>顶部那行「需品质加成 +X%」：随配方的每秒轮播一起换文字。</summary>
        private static CustomUIElement _machineQualityLabel;

        /// <summary>品质需求所在的行：单独一行，居中放在机器图标下方。</summary>
        private static RectTransform _machineQualityRow;

        /// <summary>
        /// 窗口层级遮挡：拖拽落点用 RenderHandler.RaycastElement 从前往后找
        /// TreeNodeRender。详情窗口的正文是自定义 UI（没有 TreeNodeRender），
        /// 射线会穿过去命中窗口后面的游戏容器。这里在窗口底层垫一张透明、
        /// 可射线、带空 TreeNodeRender 的整窗挡板，碰到它就停止，后面的东西收不到操作。
        /// </summary>
        private static GameObject _inputBlocker;

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
            _sourceBlocks.Clear();
            _pagePlan.Clear();
            _sourceCapacity.Clear();
            _currentSlice = new PageSlice();
            _inputBlocker = null;
            _chromeHost = null;
            _contentHost = null;
            _pagerHost = null;
            _catLabel = null;
            _catPrev = null;
            _catNext = null;
            _pagerLabel = null;
            _pagerPrev = null;
            _pagerNext = null;
            _dirLabel = null;
            StopPortrait();
            _cellAnims.Clear();
            _tagRollIndex.Clear();   // 换物品后标签轮播从头开始
            _tagStrips.Clear();
            _machineStrips.Clear();
            if (_vanillaMachine != null) { _vanillaMachine.Destroy(); _vanillaMachine = null; }
            _vanillaVariant = 0;
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

            // 机器页的物品悬停每帧命中：配方每秒轮播，旧物品销毁后新物品不会再发 PointerEnter
            if (_vanillaMachine != null)
            {
                try { _vanillaMachine.TickHover(); } catch { }
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

            // 左右键按方向取记录：来源 = 当前物品是产物的一侧，用途 = 它是投入的一侧。
            // 同一条记录的内容仍按原样画（配方同时有材料与产物、使用同时有来源与产物），
            // 所以从来源查能看到用途侧，从用途查也能看到来源侧。
            if (_asUse)
            {
                // ===== 用途方向 =====
                AddClientCategory(null, SourceIndex.WantedBy(_entry.Id, _entry.Item));

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

                AddMachineRecipeCategories();
                AddCraftCategory();
                AddUseCategory(UseIndex.Using(_entry.Id, _entry.Item));
            }
            else
            {
                // ===== 来源方向 =====
                // 卖这件的客人：页面里同时会画他的售出与求购（见 BuildSourceBlocks）
                AddClientCategory(SourceIndex.For(_entry.Id),
                                  SourceIndex.WantedBy(_entry.Id, _entry.Item));
                AddSourceCategory(CatLocation, SourceIndex.For(_entry.Id));

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

                AddMachineRecipeCategories();
                AddCraftCategory();
                AddUseCategory(UseIndex.Producing(_entry.Id));

                // 掉落池（同一地点合并成一页）：地点池并集。
                var loot = LootSource.For(_entry.Id);
                if (loot.Count > 0)
                {
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

            AddReactionCategory();

            if (_category < 0) _category = 0;
            if (_category >= _categories.Count) _category = Math.Max(0, _categories.Count - 1);
        }

        /// <summary>
        /// 液体条目的成分反应：来源方向看「它是产物的反应」，
        /// 用途方向看「它作为反应物 / 溶剂参与的反应」，一条反应一页。
        /// </summary>
        private static void AddReactionCategory()
        {
            if (_entry == null) return;

            // 加热板：和熔炉的配方页一样，把所有需要温度的反应挂到它名下
            if (string.Equals(_entry.Id, "heating_plate", StringComparison.Ordinal))
            {
                var heated = LiquidReactions.Heated();
                if (heated.Count > 0)
                    _categories.Add(new Category { Kind = CatReaction, Reactions = heated });
                return;
            }

            string liquidId = QueryLiquidId();
            if (string.IsNullOrEmpty(liquidId)) return;

            var reactions = LiquidReactions.For(liquidId, _asUse);
            if (reactions.Count == 0) return;

            _categories.Add(new Category { Kind = CatReaction, Reactions = reactions });
        }

        /// <summary>当前查询的是液体条目（liquid:xxx）时返回它的液体 id，否则 null。</summary>
        private static string QueryLiquidId()
        {
            if (_entry == null || !LiquidCatalog.IsLiquidEntry(_entry.Id)) return null;
            return _entry.Id.Substring(LiquidCatalog.EntryPrefix.Length);
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
        /// 「他在收」清单页（见 <c>BuildSourceBlocks</c> 的方向分支）。
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

            // 收购方：同一客人既卖又收时只登记收购，不额外加人物；
            // 用途方向（sources == null）才把只收不卖的客人补进来。
            var buyers = new List<SourceInfo>();
            if (wanted != null)
            {
                for (int i = 0; i < wanted.Count; i++)
                {
                    var one = wanted[i];
                    if (one == null || one.Kind != SourceKind.Client) continue;

                    if (clients.Contains(one))
                    {
                        if (!buyers.Contains(one)) buyers.Add(one);
                        continue;
                    }

                    if (sources != null) continue;   // 来源方向只列卖家

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
            _inputBlocker = null;

            // 窗口是新的：旧的固定区 / 可变区宿主都已随旧窗口销毁
            _chromeHost = null;
            _contentHost = null;
            _pagerHost = null;
            _catLabel = null;
            _catPrev = null;
            _catNext = null;
            _pagerLabel = null;
            _pagerPrev = null;
            _pagerNext = null;
            _dirLabel = null;

            if (_window != null)
            {
                try { CustomUIBuilder.RebuildLayout(_window.ContentRoot); }
                catch { }
            }

            Render();
        }



        /// <summary>
        /// 固定标题栏高度：只设 LayoutElement 的 min/preferred/flexible 三项，
        /// 不改 anchor / pivot / 位置 / padding。
        ///
        /// 高度取「关闭按钮实测高度 + 上下留白」，保证按钮完整且不会过高。
        /// </summary>

        /// <summary>窗口被游戏关掉（Esc / 点 X）时，别留下悬空提示框和拖拽登记。</summary>
        private static void OnWindowClosed()
        {
            _highlights.Clear();
            if (_vanillaMachine != null) { _vanillaMachine.Destroy(); _vanillaMachine = null; }
            _window = null;
            _bodyHost = null;
            _chromeHost = null;
            _contentHost = null;
            _pagerHost = null;
            _catLabel = null;
            _catPrev = null;
            _catNext = null;
            _pagerLabel = null;
            _pagerPrev = null;
            _pagerNext = null;
            _dirLabel = null;
            _pagePlan.Clear();
            _currentSlice = new PageSlice();
            _inputBlocker = null;
            CatalogWindow.ResetExternalCells();
        }

        /// <summary>
        /// 整窗重画（开窗 / 换物品 / 数据重建时）。版式固定为三块：
        ///   chrome（标题、方向标注、大类导航、物品类型）
        ///   content（这一页的内容，只有它会随翻页重建）
        ///   pager（翻页条，页码与按钮状态原地更新）
        /// </summary>
        private static void Render()
        {
            var manager = CustomUIManager.Instance;
            if (manager == null || _window == null || !_window.IsAlive || _bodyHost == null) return;

            BuildCategories();

            var category = CurrentCategory();
            BuildSourceBlocks(category);
            BuildPagePlan(category);
            ClampPage();

            try
            {
                EnsureHosts(manager);
                EnsureInputBlocker();

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

                RenderChrome(manager);
                RenderContent(manager, category);
                RenderPager(manager, category);

                CustomUIBuilder.RebuildLayout(_bodyHost);
                ApplyContentHeight();
                SyncHighlights();   // 窗口定稿后再对齐，避免布局重排把格子挪走
            }
            catch (Exception e)
            {
                Warn("render: " + e.Message);
            }
        }

        /// <summary>
        /// 窗口层级遮挡：拖拽落点用 RenderHandler.RaycastElement 从前往后找
        /// TreeNodeRender。详情窗口的正文是自定义 UI（没有 TreeNodeRender），
        /// 射线会穿过去命中窗口后面的游戏容器。这里在窗口底层垫一张透明、
        /// 可射线、带空 TreeNodeRender 的整窗挡板，碰到它就停止，后面的东西收不到操作。
        /// </summary>
        private static void EnsureInputBlocker()
        {
            if (_window == null || !_window.IsAlive) return;
            if (_inputBlocker != null) return;

            var root = _window.Rect;
            if (root == null) return;

            try
            {
                var go = new GameObject("recipe:inputBlocker",
                    typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(TreeNodeRender));

                var rect = (RectTransform)go.transform;
                rect.SetParent(root, false);
                rect.SetAsFirstSibling();          // 画在所有窗口内容之后方（层级最低）
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                rect.localScale = Vector3.one;

                var image = go.GetComponent<UnityEngine.UI.Image>();
                image.color = new Color(0f, 0f, 0f, 0f);   // 全透明，但 raycastTarget=true
                image.raycastTarget = true;

                var ignore = go.AddComponent<UnityEngine.UI.LayoutElement>();
                ignore.ignoreLayout = true;

                _inputBlocker = go;
            }
            catch (Exception e) { Warn("input blocker: " + e.Message); }
        }

        /// <summary>正文三个宿主只在开窗时建一次；之后翻页不再碰它们。</summary>
        private static void EnsureHosts(CustomUIManager manager)
        {
            if (_bodyHost == null) return;

            if (_chromeHost == null || _contentHost == null || _pagerHost == null)
            {
                CustomUIBuilder.ClearContainer(_bodyHost);

                var builder = new CustomUIBuilder(manager, _window, _bodyHost);

                builder.BeginColumn(6f);
                _chromeHost = builder.CurrentContainer;
                builder.End();

                builder.BeginColumn(4f);
                _contentHost = builder.CurrentContainer;
                builder.End();

                builder.BeginColumn(2f);
                _pagerHost = builder.CurrentContainer;
                builder.End();
            }

            // 嵌套列的默认 childAlignment 是左上，宽度不会自己撑满；
            // 显式给 1 的 flexibleWidth，机器面板等居中内容才有完整的横向空间。
            var bodyLe = EnsureLayoutElement(_bodyHost);
            bodyLe.flexibleWidth = 1f;
            if (_chromeHost != null) EnsureLayoutElement(_chromeHost).flexibleWidth = 1f;
            if (_contentHost != null) EnsureLayoutElement(_contentHost).flexibleWidth = 1f;
            if (_pagerHost != null) EnsureLayoutElement(_pagerHost).flexibleWidth = 1f;
        }

        /// <summary>
        /// 固定区：大类导航 + 物品类型。翻页、切页都只改文字与按钮状态，
        /// 不重建这一块（否则整窗闪一下、滚动位置也丢了）。
        /// </summary>
        private static void RenderChrome(CustomUIManager manager)
        {
            if (_chromeHost == null) return;

            CustomUIBuilder.ClearContainer(_chromeHost);
            var builder = new CustomUIBuilder(manager, _window, _chromeHost);

            // 大类翻页：按钮贴两端，名称居中；到头就禁用，不做循环
            builder.BeginRow(6f);
            builder.AddButton("<", delegate { TurnCategory(-1); }, "cat_prev");
            _catPrev = builder.Last;
            builder.AddFlexibleSpace();
            builder.AddLabel(CategoryLabel(), "cat_title");
            _catLabel = builder.Last;
            if (_catLabel != null && _catLabel.text != null)
                _catLabel.text.fontSize = 18f;   // 大类名放大：这是页面唯一的导航信息
            builder.AddFlexibleSpace();
            builder.AddButton(">", delegate { TurnCategory(1); }, "cat_next");
            _catNext = builder.Last;
            builder.End();

            // 这件物品自己的类型标签：和游戏 tooltip 上那一串一致
            // （枪械/武器、食物、水、日用品、奢侈品、文档 …）
            string itemTypes = DescribeItemTypes(_entry);
            if (!string.IsNullOrEmpty(itemTypes))
                builder.AddLabel(itemTypes, "item_types");

            UpdateChromeState();
        }

        /// <summary>只改导航行文字 / 按钮可用性，不重建控件。</summary>
        private static void UpdateChromeState()
        {
            if (_catLabel != null) _catLabel.SetText(CategoryLabel());
            if (_catPrev != null) _catPrev.SetInteractable(_category > 0);
            if (_catNext != null) _catNext.SetInteractable(_category < _categories.Count - 1);
        }

        /// <summary>
        /// 可变区：当前页内容。翻页时**只**清这个容器。
        /// 旧格子的提示框 / 拖拽登记 / 绿底 / 图标轮播也在这里统一失效。
        /// </summary>
        private static void RenderContent(CustomUIManager manager, Category category)
        {
            if (_contentHost == null) return;

            // 离开机器页时把原版面板一起销毁，别留一个挂在已销毁容器上的对象
            if (_vanillaMachine != null && (category == null || category.Kind != CatMachine))
            {
                try { _vanillaMachine.Destroy(); } catch { }
                _vanillaMachine = null;
            }

            CustomUIBuilder.ClearContainer(_contentHost);
            _highlights.Clear();          // 旧绿底随容器一起销毁
            _cellAnims.Clear();           // 旧格子的 Image 已销毁，不能留着继续改
            _tagStrips.Clear();           // 标签格这一轮重新收集
            _tagStripSeq = 0;
            _machineTopRow = null;
            _machineQualityLabel = null;
            _machineQualityRow = null;
            CatalogWindow.ResetExternalCells();   // 旧格子的提示框 / 拖拽登记一起失效

            // 只有来源页才有「哪个客人」这回事；配方页清掉种子，
            // 成分行回落到实例自身的液体。
            SourceSeed = null;
            SourceWater = null;

            var builder = new CustomUIBuilder(manager, _window, _contentHost);

            if (category == null)
            {
                builder.AddWarning(Loc("cat_none", "暂未收录"), "cat_empty");
            }
            else
            {
                var type = PageTypeFor(category);
                if (type != null) type.Render(builder, category, _currentSlice);
            }

            // 内容区嵌在正文列里：高度要先经 _contentHost 传到 _bodyHost，
            // ApplyContentHeight 才能量到本页真实的首选高度（否则窗口停在上一页的尺寸）。
            try
            {
                CustomUIBuilder.RebuildLayout(_contentHost);
                CustomUIBuilder.RebuildLayout(_bodyHost);
            }
            catch { }
        }

        /// <summary>翻页条：只在换大类 / 列数变化时重建；单纯翻页只原地改文字与按钮。</summary>
        private static void RenderPager(CustomUIManager manager, Category category)
        {
            if (_pagerHost == null) return;

            if (category == null)
            {
                CustomUIBuilder.ClearContainer(_pagerHost);
                _pagerLabel = null;
                _pagerPrev = null;
                _pagerNext = null;
                return;
            }

            CustomUIBuilder.ClearContainer(_pagerHost);
            _pagerLabel = null;
            _pagerPrev = null;
            _pagerNext = null;

            var builder = new CustomUIBuilder(manager, _window, _pagerHost);

            // 与图鉴翻页条同款：两端弹性空位，固定 34px 高。
            builder.BeginRow(6f);
            var pagerRow = builder.CurrentContainer;
            _machinePagerRow = pagerRow;   // 实测高度用
            builder.AddFlexibleSpace();
            builder.AddButton("<", delegate { TurnPage(-1); }, "src_pp");
            _pagerPrev = builder.Last;
            builder.AddLabel(string.Format(ModEntry.T("page"), _page + 1, Mathf.Max(1, _pagePlan.Count)), "src_page");
            _pagerLabel = builder.Last;
            builder.AddButton(">", delegate { TurnPage(1); }, "src_pn");
            _pagerNext = builder.Last;
            builder.AddFlexibleSpace();
            builder.End();

            if (pagerRow != null)
            {
                var pagerLayout = EnsureLayoutElement(pagerRow);
                pagerLayout.flexibleHeight = 0f;
                pagerLayout.minHeight = 34f;
            }

            UpdatePagerState();
        }

        /// <summary>翻页条文字 / 按钮状态的原位更新（不重建控件）。</summary>
        private static void UpdatePagerState()
        {
            int pages = Mathf.Max(1, _pagePlan.Count);

            if (_pagerLabel != null)
                _pagerLabel.SetText(string.Format(ModEntry.T("page"), _page + 1, pages));
            if (_pagerPrev != null) _pagerPrev.SetInteractable(_page > 0);
            if (_pagerNext != null) _pagerNext.SetInteractable(_page < pages - 1);
        }

        // ---------------------------------------------------------------- 分页计划

        private static Category CurrentCategory()
        {
            if (_categories.Count == 0) return null;

            int index = Mathf.Clamp(_category, 0, _categories.Count - 1);
            return _categories[index];
        }

        private static void EnsurePageTypes()
        {
            if (_pageTypes != null) return;

            // 客人 / 地点：每个来源一块，块内货单按每页格数切片。
            // 块数取自实际建好的 _sourceBlocks，保证与渲染数据一一对应。
            var sources = new PageType
            {
                BlockCount = c => _sourceBlocks.Count,
                BlockItems = (c, b) => SourceBlockItemCount(b),
                PageItems = SourcePageItemsPerPage,
                Render = RenderSourceBlock
            };

            _pageTypes = new Dictionary<int, PageType>
            {
                { CatClient, sources },
                { CatLocation, sources },

                // 合成 / 使用：一条关系一页（整块不切片）
                { CatCraft, new PageType
                    {
                        BlockCount = c => c.Recipes == null ? 0 : c.Recipes.Count,
                        BlockItems = (c, b) => 0,
                        Render = RenderRecipeBlock
                    } },
                { CatUse, new PageType
                    {
                        BlockCount = c => c.Uses == null ? 0 : c.Uses.Count,
                        BlockItems = (c, b) => 0,
                        Render = RenderUseBlock
                    } },

                // 掉落：一张地点池一块，物品多时按每页格数切片
                { CatLoot, new PageType
                    {
                        BlockCount = c => c.Loot == null ? 0 : c.Loot.Count,
                        BlockItems = LootBlockItemCount,
                        PageItems = LootPageItemsPerPage,
                        Render = RenderLootBlock
                    } },

                // 机器：一个纯度档一块，整块一页（档内配方自己轮播）
                { CatMachine, new PageType
                    {
                        BlockCount = c => MachineGroupCount(),
                        BlockItems = (c, b) => 0,
                        Render = RenderMachineBlock
                    } },

                // 反查：当前物品是某台机器的投入 / 模块 / 产出
                { CatMachineRecipe, new PageType
                    {
                        BlockCount = c => c.MachineGroups == null ? 0 : c.MachineGroups.Count,
                        BlockItems = (c, b) => 0,
                        Render = RenderMachineRecipeBlock
                    } },

                // 成分反应：一条反应一页（左侧反应物 / 中间条件 / 右侧产物）
                { CatReaction, new PageType
                    {
                        BlockCount = c => c.Reactions == null ? 0 : c.Reactions.Count,
                        BlockItems = (c, b) => 0,
                        Render = RenderReactionBlock
                    } }
            };
        }

        private static PageType PageTypeFor(Category category)
        {
            if (category == null) return null;

            EnsurePageTypes();

            PageType type;
            return _pageTypes.TryGetValue(category.Kind, out type) ? type : null;
        }

        private static int SourceBlockItemCount(int block)
        {
            if (block < 0 || block >= _sourceBlocks.Count) return 0;

            var page = _sourceBlocks[block];
            return page == null ? 0 : page.Count;
        }

        private static int LootBlockItemCount(Category category, int block)
        {
            if (category == null || category.Loot == null ||
                block < 0 || block >= category.Loot.Count) return 0;

            var table = category.Loot[block];
            return table == null || table.Items == null ? 0 : table.Items.Count;
        }

        /// <summary>页面正文之外必定占用的高度：窗口标题栏、大类行、翻页条、层间距。</summary>
        private static float PageBaseOverhead()
        {
            return WindowChrome + 34f + 34f + 32f;
        }

        /// <summary>按这一页实际要画的额外内容反算每页容量（列 × 行）。</summary>
        private static int PageCapacity(float overhead)
        {
            int rows = Mathf.FloorToInt((MaxContentHeight() - overhead)
                                        / (CellHeight + CellSpacing));
            return Mathf.Max(1, GridColumns() * Mathf.Max(1, rows));
        }

        /// <summary>
        /// 来源页：这一块画不画阵营标题 / 客人名 / 立绘，自己决定能排几行。
        /// 有立绘的页行数少、没立绘的页行数多，内容都顶到同一个最大高度，
        /// 装不下的行由 <see cref="BuildPagePlan"/> 自动放到下一页。
        /// </summary>
        private static int SourcePageItemsPerPage(Category category, int block)
        {
            if (block < 0 || block >= _sourceBlocks.Count) return ItemsPerPage;

            var page = _sourceBlocks[block];
            var source = page == null ? null : page.Source;

            // 已经按实测校正过：直接用校正后的容量
            int calibrated;
            if (_sourceCapacity.TryGetValue(block, out calibrated) && calibrated > 0)
                return calibrated;

            float overhead = PageBaseOverhead();

            if (source != null && source.Kind != SourceKind.Location) overhead += 30f; // 阵营标题
            if (category != null && category.Kind == CatClient) overhead += 28f;       // 客人名

            if (source != null && ClientSprite.AllFor(source.Sprite) != null)
                overhead += PortraitMaxSide + 8f;

            return PageCapacity(overhead);
        }

        /// <summary>掉落页：有箱子/卡那一行时少排一行货，其余空间全给内容物。</summary>
        private static int LootPageItemsPerPage(Category category, int block)
        {
            if (category == null || category.Loot == null ||
                block < 0 || block >= category.Loot.Count) return ItemsPerPage;

            var table = category.Loot[block];
            float overhead = PageBaseOverhead();

            string boxId = null;
            if (table != null)
            {
                string site = category.Site;
                boxId = !string.IsNullOrEmpty(site) &&
                        site.StartsWith("item:", StringComparison.OrdinalIgnoreCase)
                    ? site.Substring(5).Trim()
                    : (LootSource.BoxForPool(table.TableId) ?? table.TableId);
            }

            if (!string.IsNullOrEmpty(boxId) && RecipeIndex.FindEntry(boxId) != null)
                overhead += CellHeight + CellSpacing + 30f;   // 箱子/卡一行 +「可能开出」

            return PageCapacity(overhead);
        }

        private static int MachineGroupCount()
        {
            if (_entry == null) return 0;

            var groups = MachinePanels.RecipesFor(_entry.Id);
            return groups == null ? 0 : groups.Length;
        }

        /// <summary>
        /// 唯一的切页算法：遍历内容块，按当前每页格数把每块切成若干页。
        /// 页面类型只声明「几块 / 每块多少条 / 怎么画」，这里不关心内容是什么。
        /// </summary>
        private static void BuildPagePlan(Category category)
        {
            _pagePlan.Clear();

            var type = PageTypeFor(category);
            if (type == null)
            {
                _pagePlan.Add(new PageSlice());
                return;
            }

            int blocks = type.BlockCount == null ? 1 : Mathf.Max(1, type.BlockCount(category));

            for (int block = 0; block < blocks; block++)
            {
                int items = type.BlockItems == null ? 0 : type.BlockItems(category, block);

                // 整块一页（配方 / 使用 / 机器档位）
                if (items <= 0)
                {
                    _pagePlan.Add(new PageSlice { Block = block, Start = 0, Count = 0 });
                    continue;
                }

                // 每页容量由页面类型自己算（来源页会扣掉立绘/标题，掉落页扣掉箱子），
                // 装不下的行自然落到下一页。
                int per = type.PageItems != null
                    ? Mathf.Max(1, type.PageItems(category, block))
                    : Mathf.Max(1, ItemsPerPage);

                // 按容量切片（客人货单 / 地点池）
                for (int start = 0; start < items; start += per)
                {
                    _pagePlan.Add(new PageSlice
                    {
                        Block = block,
                        Start = start,
                        Count = Mathf.Min(per, items - start)
                    });
                }
            }

            if (_pagePlan.Count == 0) _pagePlan.Add(new PageSlice());
        }

        private static void ClampPage()
        {
            if (_pagePlan.Count == 0) _pagePlan.Add(new PageSlice());

            if (_page < 0) _page = 0;
            if (_page >= _pagePlan.Count) _page = _pagePlan.Count - 1;

            _currentSlice = _pagePlan[_page];
        }

        /// <summary>列数变化会改变每页格数，必须重算分页计划后再重画当前页。</summary>
        private static void RebuildPlanAndPage()
        {
            var manager = CustomUIManager.Instance;
            if (manager == null || _window == null || !_window.IsAlive) return;

            var category = CurrentCategory();
            _sourceCapacity.Clear();   // 列数变了，按旧列数实测的容量作废
            BuildPagePlan(category);
            ClampPage();

            RenderContent(manager, category);
            UpdatePagerState();
            ApplyContentHeight();
        }

        private static int _tagStripSeq;
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

                var frame = _window.frame;
                var title = frame != null ? frame.titleText : null;
                if (title == null) return;

                // 标题栏必须是整条 header：优先用关闭按钮的父节点，
                // 不能只拿 title.transform.parent（模板里可能只是一个包住标题的窄容器，
                // 那样「来源/用途」会贴在窄容器左边，看起来像居中）。
                RectTransform bar = null;
                if (frame != null && frame.closeButton != null)
                    bar = frame.closeButton.transform.parent as RectTransform;
                if (bar == null) bar = title.transform.parent as RectTransform;
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
        /// 把标题栏的物品名居中。直接取 <see cref="CustomUIWindowFrame.titleText"/>，
        /// 不再靠文字内容搜索（搜索会漏、也可能找到克隆出来的方向标签）。
        /// 同时把标题的布局宽度设为弹性，确保 TMP 的居中是在整条标题栏里居中。
        /// </summary>
        private static void CenterTitle()
        {
            if (_window == null || _entry == null) return;

            try
            {
                var frame = _window.frame;
                var title = frame != null ? frame.titleText : null;
                if (title == null) return;

                title.alignment = TMPro.TextAlignmentOptions.Center;

                var le = title.GetComponent<UnityEngine.UI.LayoutElement>();
                if (le == null) le = title.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
                le.flexibleWidth = 1f;
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
                // 掉落池只按地点成大类（上面的 SiteName 分支已返回地点名）。
                // 这里不再回退到「掉落池」——那是后端概念，玩家看到的应该是
                // 「垃圾倾倒场 / 小偷 / 装着它的箱子」这类具体地点。
                case CatLoot: return Loc("cat_site", "地点");
                case CatMachine: return Loc("cat_machine", "机器");
                case CatReaction: return Loc("cat_reaction", "反应");
                case CatMachineRecipe:
                {
                    var machineEntry = RecipeIndex.FindEntry(category.MachineId);
                    if (machineEntry != null && !string.IsNullOrEmpty(machineEntry.DisplayName))
                        return machineEntry.DisplayName;
                    return Loc("cat_machine", "机器");
                }
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

        /// <summary>
        /// 大类切换：到头就停，不循环。
        /// 固定区只更新导航文字 / 按钮状态；只有内容区与翻页条重画。
        /// </summary>
        private static void TurnCategory(int delta)
        {
            if (_categories.Count == 0) return;

            int next = Mathf.Clamp(_category + delta, 0, _categories.Count - 1);
            if (next == _category) return;

            _category = next;
            _page = 0;

            var category = CurrentCategory();
            BuildSourceBlocks(category);   // 换类后来源块要按新类重建
            BuildPagePlan(category);
            ClampPage();

            var manager = CustomUIManager.Instance;
            if (manager == null || _window == null || !_window.IsAlive) return;

            try
            {
                UpdateChromeState();
                RenderContent(manager, category);
                RenderPager(manager, category);
                CustomUIBuilder.RebuildLayout(_bodyHost);
                ApplyContentHeight();
                SyncHighlights();
            }
            catch (Exception e) { Warn("category turn: " + e.Message); }
        }

        /// <summary>页码翻页：到头就停，不循环。只重建内容区，翻页条原地更新。</summary>
        private static void TurnPage(int delta)
        {
            int pages = Mathf.Max(1, _pagePlan.Count);
            int next = Mathf.Clamp(_page + delta, 0, pages - 1);
            if (next == _page) return;

            _page = next;
            _currentSlice = _pagePlan[_page];

            var manager = CustomUIManager.Instance;
            if (manager == null || _window == null || !_window.IsAlive) return;

            try
            {
                RenderContent(manager, CurrentCategory());
                UpdatePagerState();
                ApplyContentHeight();
                SyncHighlights();
            }
            catch (Exception e) { Warn("page turn: " + e.Message); }
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
                // 三层嵌套布局：先内容、后正文，量到的 preferred 才是本页的真实高度
                if (_contentHost != null) CustomUIBuilder.RebuildLayout(_contentHost);
                CustomUIBuilder.RebuildLayout(_bodyHost);

                float preferred = UnityEngine.UI.LayoutUtility.GetPreferredHeight(_bodyHost);
                if (preferred < 1f) preferred = EstimateHeight();

                var pageCategory = CurrentCategory();
                bool machinePage = pageCategory != null && pageCategory.Kind == CatMachine;

                // 机器页与普通页完全共用这个公式，不再有第二套高度逻辑。
                // preferred 取自 _bodyHost，翻页条就在它内部，所以已包含翻页条高度。
                float height = Mathf.Clamp(preferred + WindowChrome, WinMinHeight, MaxContentHeight());

                // 顶部行（配方名 + 右上角机器图标）固定高度：
                // 不给它声明高度的话，布局组会按剩余空间压缩/拉伸它，
                // 机器图标的显示尺寸就随内容多少而变。
                if (machinePage && _machineTopRow != null)
                {
                    float topWant = 60f;
                    try
                    {
                        float p = UnityEngine.UI.LayoutUtility.GetPreferredHeight(_machineTopRow);
                        if (p >= 40f && p <= 160f) topWant = p;
                    }
                    catch { }

                    var topLe = EnsureLayoutElement(_machineTopRow);
                    topLe.minHeight = topWant;
                    topLe.preferredHeight = topWant;
                    topLe.flexibleHeight = 0f;
                }

                if (_window.ContentRoot != null)
                {
                    // ContentRoot 不做任何页面差异化的高度声明：
                    // 机器页和普通页走同一套通用逻辑，靠 _bodyHost 的 preferred 自然撑开。
                    // 之前给机器页加 preferredHeight 补偿，既造成跨页残留（开一次机器
                    // 所有页面都变窄），又因为补偿量估不准反复压扁标题栏。
                    var crLayout = EnsureLayoutElement(_window.ContentRoot);
                    crLayout.minHeight = -1f;
                    crLayout.preferredHeight = -1f;
                    crLayout.flexibleHeight = -1f;

                    // 分配子级高度的是窗口 root 上的布局组，必须重排 root。
                    CustomUIBuilder.RebuildLayout(_window.Rect);

                    // 实测「窗口顶边 → bodyHost 顶边」= 标题栏 + 各层 padding 的真实总量。
                    // 必须在**首轮给足空间**（gap 用 220）时测量，这样标题栏是自然高度，
                    // （已移除机器页的高度实测/采纳：机器页现与普通页共用同一公式）
                }

                if (Mathf.Abs(height - _lastHeight) > 2f)
                {
                    // 锚点就是窗口自身中心：改高度时上下对称伸缩，
                    // 不再锁顶边。窗口拖到哪，中心就留在哪。
                    _window.SetSize(WinWidth, height);
                    _lastHeight = height;

                    // 窗口变了尺寸，让内容根再排一次，避免内容按旧尺寸算
                    CustomUIBuilder.RebuildLayout(_window.ContentRoot);
                }

                // 实测校正：公式算少了一行时，按真实剩余高度补容量并重画一次
                if (TryCalibrateSourceCapacity(height)) return;

                // 布局完成后量一次容器真宽：比首帧的假定值准。
                // 列数受它影响时只多渲染一轮（这里不会再置位，不会循环）。
                float w = _bodyHost.rect.width;
                if (w > 320f && w < 900f && Math.Abs(w - 12f - _gridContentWidth) > 8f)
                {
                    _gridContentWidth = w - 12f;
                    if (GridColumns() != _lastColumnsUsed)
                    {
                        _lastColumnsUsed = GridColumns();
                        // 列数变了 → 每页格数变了，分页计划必须重算；只重画内容区
                        RebuildPlanAndPage();
                        return;
                    }
                }



            }
            catch (Exception e)
            {
                Warn("auto height: " + e.Message);
            }
        }

        /// <summary>
        /// 来源页容量的实测校正：公式只是初值，真正排版后用 LayoutUtility 量到的
        /// 剩余高度补行。只校正每块的**第一页**，结果缓存进 _sourceCapacity，
        /// 后续翻页直接用，不会每次翻页都重排。
        /// </summary>
        private static bool TryCalibrateSourceCapacity(float usedHeight)
        {
            if (_calibratingCapacity) return false;

            var category = CurrentCategory();
            if (category == null || category.Sources == null) return false;

            var slice = _currentSlice;
            if (slice.IsWholeBlock || slice.Count <= 0 || slice.Start != 0) return false;

            int block = slice.Block;
            if (_sourceCapacity.ContainsKey(block)) return false;

            int total = SourceBlockItemCount(block);
            if (total <= slice.Count) return false;   // 这一块已经全装下，没有行可加

            float room = MaxContentHeight() - usedHeight;
            int rowH = Mathf.RoundToInt(CellHeight + CellSpacing);
            int extraRows = Mathf.FloorToInt(room / rowH);
            if (extraRows <= 0) return false;

            int per = slice.Count + extraRows * GridColumns();
            if (per <= slice.Count) return false;

            var manager = CustomUIManager.Instance;
            if (manager == null || _window == null || !_window.IsAlive) return false;

            _sourceCapacity[block] = per;

            BuildPagePlan(category);
            ClampPage();

            _calibratingCapacity = true;
            try
            {
                RenderContent(manager, category);
                UpdatePagerState();
                ApplyContentHeight();
            }
            catch (Exception e) { Warn("calibrate capacity: " + e.Message); }
            finally { _calibratingCapacity = false; }

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[recipe] capacity block=" + block
                                 + " " + slice.Count + " -> " + per
                                 + " (room=" + room.ToString("F0") + ")");

            return true;
        }

        /// <summary>窗口最长到 WinMaxHeight，但别长出屏幕（留 40px 给标题栏与任务栏）。</summary>
        private static float MaxContentHeight()
        {
            float cap = Screen.height > 0 ? Mathf.Min(WinMaxHeight, Screen.height - 40f) : WinMaxHeight;
            return Mathf.Max(cap, WinMinHeight);
        }

        /// <summary>
        /// 机器面板的最大高度：与普通页共用 <see cref="MaxContentHeight"/>，
        /// 只扣掉面板之外固定占用的部分（窗口标题栏、顶部行、翻页条、间距）。
        /// 质量需求行的高度已经通过提高 WinMaxHeight 预留，不再单独从面板里扣。
        /// </summary>
        private static float MaxMachinePanelHeight()
        {
            const float topRowH = 60f;
            const float pagerH = 34f;
            const float spacing = 20f;

            return Mathf.Max(200f, MaxContentHeight() - WindowChrome - topRowH - pagerH - spacing);
        }

        /// <summary>
        /// 布局还没算出首选高度时（首次打开 / 字体未就绪）的兜底：按这一页实际行数估。
        /// 立绘是页面里最大的一块（<see cref="PortraitMaxSide"/>），必须算进去，否则会把货单挤出窗口。
        /// </summary>
        private static float EstimateHeight()
        {
            const float fixedPart = 34f + 30f + 28f + 34f + 32f;  // 大类行 / 阵营标题 / 客人名 / 翻页条 / 间距
            float h = fixedPart;

            var category = CurrentCategory();
            if (category != null && category.Sources != null &&
                _currentSlice.Block >= 0 && _currentSlice.Block < _sourceBlocks.Count)
            {
                var page = _sourceBlocks[_currentSlice.Block];
                if (page.Source != null && !string.IsNullOrEmpty(page.Source.Sprite))
                    h += PortraitMaxSide + 8f;

                // 本页只画切片里的那一段（整块一页时 Count=0，按 0 行算）
                int cells = _currentSlice.IsWholeBlock ? 0 : _currentSlice.Count;
                int rows = Math.Max(1, (cells + GridColumns() - 1) / GridColumns());
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
        internal static bool ApplyVanillaHighlight(UnityEngine.UI.Image image)
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
                // 原版绿贴图是九宫格（带 border），放大到 64px 时若用 Simple 会把
                // 中间拉伸、绿边被精灵图盖住；用 Sliced 才能保持边框厚度、绿边露出。
                if (green.border.x > 0f || green.border.y > 0f)
                    image.type = UnityEngine.UI.Image.Type.Sliced;
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

        private static void BuildSourceBlocks(Category category)
        {
            _sourceBlocks.Clear();
            _sourceCapacity.Clear();   // 块重建后实测容量作废
            if (category == null || category.Sources == null) return;

            for (int i = 0; i < category.Sources.Count; i++)
            {
                var source = category.Sources[i];
                if (source == null) continue;

                // 一个客人一页：售出与求购合在同一页，方向只决定进入这一页的客人。
                var items = source.Items;
                var cells = new List<GoodsCell>(items == null ? 0 : items.Length);

                if (items != null)
                {
                    // 卖水的客人：容器一格（满瓶）。
                    // 既卖水又卖瓶的客人（`empty` 列 = 1）：给灌过水的那件容器再拆一个
                    // 原始空瓶格，不轮播、不显示成分。`water` 带 `@物品id` 限定时，
                    // 没被灌的容器本来就是空瓶，不拆，免得出现两格一模一样的空瓶。
                    for (int k = 0; k < items.Length; k++)
                    {
                        string id = items[k];
                        if (string.IsNullOrEmpty(id)) continue;

                        if (source.SellsEmpty && CatalogWindow.IsLiquidContainer(id)
                            && CatalogWindow.ClientHasFilledBottle(source.Water, id))
                            cells.Add(new GoodsCell { Id = id, Empty = true });

                        cells.Add(new GoodsCell { Id = id });
                    }
                }

                _sourceBlocks.Add(new SourceBlock
                {
                    Source = source,
                    Cells = cells
                });
            }
        }

        /// <summary>来源块渲染器：只画切片 [start, start+count) 里的货单格子。</summary>
        private static void RenderSourceBlock(CustomUIBuilder builder, Category category, PageSlice slice)
        {
            RenderSourceBlock(builder, slice.Block, slice.Start, slice.Count);
        }

        private static void RenderSourceBlock(CustomUIBuilder builder, int block, int start, int count)
        {
            if (block < 0 || block >= _sourceBlocks.Count) return;

            var page = _sourceBlocks[block];
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
            bool showClientName = false;
            var currentCategory = CurrentCategory();
            if (currentCategory != null) showClientName = currentCategory.Kind == CatClient;

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
                int portraitStart = portraits.Count > 1
                    ? (source.SourceId == null ? 0 : (source.SourceId.GetHashCode() & 0x7FFFFFFF) % portraits.Count)
                    : 0;
                _portraitIndex = portraitStart;

                float w, h;
                CatalogWindow.FitIcon(portraits[portraitStart], PortraitMaxSide, out w, out h);
                builder.BeginRow(4f);
                builder.AddFlexibleSpace();
                builder.AddImage(portraits[portraitStart], w, h, "src_portrait");

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
            bool hasCells = cells != null && cells.Count > 0;

            // 有内容才留标题：售出
            if (hasCells)
            {
                builder.AddLabel(ModEntry.T("src_sold"), "src_sold");
                // 只画本页切片那一段；格子编号按页内序号，避免同一物品多页时
                // 元素 id 重复（提示框 / 绿底 / 拖拽都按 id 登记）。
                int from = Mathf.Clamp(start, 0, cells.Count);
                int to = count > 0 ? Mathf.Min(from + count, cells.Count) : cells.Count;

                builder.BeginColumn(4f);
                var gridHost = builder.CurrentContainer;
                builder.BeginGrid(GridColumns(), GridCellWidth(), CellHeight, CellSpacing);

                for (int i = from; i < to; i++)
                {
                    var cell = cells[i];
                    bool match = !_asUse && _entry != null &&
                                 string.Equals(cell.Id, _entry.Id, StringComparison.Ordinal);
                    RenderGoods(builder, cell.Id, match, i - from, cell.Empty);
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

            // 客人（双向）：售出、求购固定两块；求购只在第一页画一次，避免货单分页时重复
            if (showClientName && start == 0)
            {
                RenderBuys(builder, source, true, _asUse);
            }
        }

        /// <summary>
        /// 「他在收」区块。数据来自 `clients.csv` 第 10/11 列，
        /// 是从各客人 `willAcceptItem` 谓词的 IL 里抄出来的
        /// （`IsTag("WATER")` / `IsID("bottled_water_premium")`）：
        ///   · 种类：只显示标签名（WATER、FOOD…），不展开成具体物品；
        ///   · 特殊物品：和货单格子一样，有图标、能点击、能悬停。
        /// </summary>
        private static void RenderBuys(CustomUIBuilder builder, SourceInfo source,
                                       bool withHeader = true, bool matchCurrent = false)
        {
            if (source == null) return;

            var tags = source.BuysTags;
            var items = source.BuysItems;

            bool hasTags = tags != null && tags.Length > 0;
            bool hasItems = items != null && items.Length > 0;

            if (!hasTags && !hasItems) return;

            if (withHeader) builder.AddLabel(ModEntry.T("src_wanted"), "src_wanted");

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
                    CurrentId = matchCurrent && _entry != null ? _entry.Id : null,
                    Exclude = exclude
                };
                _tagStripSeq++;
                _tagStrips.Add(buyStrip);
                RenderTagStrip(buyStrip);
                return;
            }

            if (!hasItems) return;

            RenderItemGrid(builder, items, 500, matchCurrent);
        }


        /// <summary>
        /// 一排图鉴格子。用 `BeginGrid` 而不是 `BeginRow`：
        /// 网格格子自带和「客人的货」一样的绿底、同样的 56×56 尺寸，
        /// 行里手搓的图没有底、还会被压小。
        /// </summary>
        private static void RenderItemGrid(CustomUIBuilder builder, string[] ids, int idBase,
                                           bool matchEnabled = true)
        {
            if (ids == null || ids.Length == 0) return;

            var host = builder.CurrentContainer;
            builder.BeginGrid(GridColumns(), GridCellWidth(), CellHeight, CellSpacing);

            for (int i = 0; i < ids.Length; i++)
            {
                if (string.IsNullOrEmpty(ids[i])) continue;

                bool match = matchEnabled && _entry != null &&
                             string.Equals(ids[i], _entry.Id, StringComparison.Ordinal);
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
        private static void RenderGoods(CustomUIBuilder builder, string id, bool match, int index, bool emptyOnly, List<Sprite> rotate = null, float sideOverride = 0f)
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
                CatalogWindow.FitIcon(sprite, sideOverride > 0f ? sideOverride : IconSide, out w, out h);
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
        /// 使用记录的方向选择：来源方向（左键）= 当前物品是产物；
        /// 用途方向（右键）= 当前物品是投入。页面内容仍是完整记录，两侧都会画。
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
        /// 静态机器配方的正反查：当前物品是投入 / 模块（用途方向）或产出（来源方向）时，
        /// 把对应的机器配方组加进来。只收录有原版面板外壳的机器
        /// （熔炉、搅拌机）；新增机器时把 id 加进 <see cref="KnownMachines"/>。
        /// </summary>
        private static readonly string[] KnownMachines = { "furnace", "blender", "chem_finisher", "evaporator" };

        private static void AddMachineRecipeCategories()
        {
            if (_entry == null) return;

            for (int m = 0; m < KnownMachines.Length; m++)
            {
                string machineId = KnownMachines[m];
                if (string.Equals(_entry.Id, machineId, StringComparison.Ordinal)) continue;

                var groups = MachinePanels.RecipesFor(machineId);
                if (groups == null || groups.Length == 0) continue;

                var matched = new List<MachinePanels.RecipeGroup>();
                for (int g = 0; g < groups.Length; g++)
                {
                    var group = groups[g];
                    if (group == null || group.Variants == null) continue;

                    bool hit = false;
                    for (int v = 0; v < group.Variants.Length && !hit; v++)
                    {
                        var recipe = group.Variants[v];
                        if (recipe == null) continue;

                        hit = _asUse
                            ? ContainsId(recipe.Inputs, _entry.Id) || ContainsId(recipe.Modules, _entry.Id)
                            : ContainsId(recipe.Outputs, _entry.Id) ||
                              InputProducesLiquid(recipe, QueryLiquidId());
                    }

                    if (hit) matched.Add(group);
                }

                if (matched.Count == 0) continue;

                _categories.Add(new Category
                {
                    Kind = CatMachineRecipe,
                    MachineId = machineId,
                    MachineGroups = matched
                });
            }
        }

        /// <summary>
        /// 反查页只轮播包含当前物品的变体：
        /// 用途方向看 Inputs/Modules，来源方向看 Outputs；机器自己的页不筛。
        /// </summary>
        private static MachinePanels.Recipe[] VariantsFor(MachinePanels.RecipeGroup group, bool filterToEntry)
        {
            if (group == null || group.Variants == null) return null;
            if (!filterToEntry || _entry == null) return group.Variants;

            var list = new List<MachinePanels.Recipe>();
            for (int i = 0; i < group.Variants.Length; i++)
            {
                var recipe = group.Variants[i];
                if (recipe == null) continue;

                bool hit = _asUse
                    ? ContainsId(recipe.Inputs, _entry.Id) || ContainsId(recipe.Modules, _entry.Id)
                    : ContainsId(recipe.Outputs, _entry.Id) ||
                      InputProducesLiquid(recipe, QueryLiquidId());

                if (hit) list.Add(recipe);
            }

            return list.Count > 0 ? list.ToArray() : group.Variants;
        }

        private static bool ContainsId(string[] ids, string id)
        {
            if (ids == null || string.IsNullOrEmpty(id)) return false;

            for (int i = 0; i < ids.Length; i++)
                if (string.Equals(ids[i], id, StringComparison.Ordinal)) return true;

            return false;
        }

        /// <summary>
        /// 配方的投入物品里有没有主成分是这种液体的。
        /// 搅拌机把物品里的成分搅进容器，产物是液体而不是物品，
        /// 所以液体条目反查来源时只能按投入物品的 LiquidTag 匹配。
        /// </summary>
        private static bool InputProducesLiquid(MachinePanels.Recipe recipe, string liquidId)
        {
            if (recipe == null || string.IsNullOrEmpty(liquidId) || recipe.Inputs == null) return false;

            for (int i = 0; i < recipe.Inputs.Length; i++)
            {
                var entry = RecipeIndex.FindEntry(recipe.Inputs[i]);
                if (entry != null && string.Equals(entry.LiquidTag, liquidId, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 合成大类，**按方向取配方**：左键（来源）= 产出它的配方；
        /// 右键（用途）= 把它当材料用掉的配方。类名都叫「合成」，
        /// 配方页材料与产物自明方向（当前物品在哪列就高亮在哪列）。
        /// </summary>
        private static void AddCraftCategory()
        {
            var merged = new List<RecipeInfo>();

            // 方向选择记录：来源 = 产出它的配方；用途 = 把它当材料用掉的配方。
            // 传 Item 是为了让 `tag:XXX` 那类「整类目标」也能命中
            // （拆解只对带 ADV_GUN_TAG 的枪、屠宰只对带 BLADE_ITEM_TAG 的刀）。
            if (_asUse)
            {
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
        /// 来源侧 / 去处侧共用的一条「箱子 ↔ 内容物」关系：
        /// 一个地点池（或箱子）一块，块内产出按每页格数切片。
        /// </summary>
        private static void RenderLootBlock(CustomUIBuilder builder, Category category, PageSlice slice)
        {
            RenderLootBody(builder, category.Loot, category.Site,
                           slice.Block, slice.Start, slice.Count);
        }

        /// <summary>
        /// 掉落关系**共用渲染器**（来源侧反查到的箱子 / 地点池，与去处侧箱子 / 卡自己展开，
        /// 走的是这同一段代码）：是箱子就画「箱子 + 需要的卡 →『可能开出』→ 内容物格子」；
        /// 是地点池就只有内容物格子（地点名在分类标题上，不重复）。
        /// 分页由 PageSlice 给出：第 tableIdx 张表、表内 [segStart, segStart+segCount) 段。
        /// </summary>
        private static void RenderLootBody(CustomUIBuilder builder, List<LootTableInfo> loot, string site,
                                           int tableIdx, int segStart, int segCount)
        {
            if (loot == null || loot.Count == 0) return;

            int idx = Mathf.Clamp(tableIdx, 0, loot.Count - 1);
            var table = loot[idx];
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

            RenderLootItems(builder, table, segStart, segCount);
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

        /// <summary>把一张战利品表里本页那一段产出铺成图鉴格子。</summary>
        private static void RenderLootItems(CustomUIBuilder builder, LootTableInfo table,
                                            int segStart = 0, int take = 0)
        {
            var items = table == null ? null : table.Items;
            if (items == null || items.Count == 0) return;

            // 只画本页那一段：装满一页的格数就翻到下一页，不再把整表铺出窗口
            int start = Mathf.Clamp(segStart, 0, items.Count);
            int end = take > 0 ? Mathf.Min(start + take, items.Count) : items.Count;

            var gridHost = builder.CurrentContainer;
            builder.BeginGrid(GridColumns(), GridCellWidth(), CellHeight, CellSpacing);

            int slot = 0;
            for (int i = start; i < end; i++)
            {
                var item = items[i];
                if (item == null || string.IsNullOrEmpty(item.Id)) continue;

                bool match = _entry != null && string.Equals(item.Id, _entry.Id, StringComparison.Ordinal);
                RenderGoods(builder, item.Id, match, slot++, false);
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
        private static void RenderUseBlock(CustomUIBuilder builder, Category category, PageSlice slice)
        {
            RenderUsePage(builder, category.Uses, slice.Block);
        }

        private static void RenderUsePage(CustomUIBuilder builder, List<UseInfo> uses, int useIndex)
        {
            if (uses == null || useIndex < 0 || useIndex >= uses.Count) return;

            var use = uses[useIndex];
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

            bool hasProduces = use.Produces != null && use.Produces.Length > 0;

            if (hasProduces)
            {
                builder.AddLabel(ModEntry.T("rec_result"), "rec_result");
                RenderItemGrid(builder, use.Produces, 700);
            }

            bool dissolve = string.Equals(use.Kind, "dissolve", StringComparison.Ordinal);
            bool contain = string.Equals(use.Kind, "contain", StringComparison.Ordinal);

            if (dissolve || contain)
            {
                // 两个方向共用同一份内容：产物液体的成分说明。
                // 容器条目（contain）展示容器自身的内容物，化学品（dissolve）展示产物液体。
                List<LiquidManager.Component> components;
                if (contain)
                {
                    var sourceEntry = sources.Count > 0 ? RecipeIndex.FindEntry(sources[0]) : null;
                    components = LiquidManager.Components(sourceEntry);
                }
                else
                {
                    string product = use.Produces != null && use.Produces.Length > 0
                        ? use.Produces[0] : null;
                    components = LiquidManager.Components(LiquidCatalog.Find(product));
                }

                RenderLiquidComposition(builder, components);
            }
            else if (!string.IsNullOrEmpty(use.Note))
            {
                builder.AddLabel(ModEntry.T(use.Note), "use_note");
            }
        }

        /// <summary>
        /// 逐行渲染液体成分：总体积一行，之后每种液体一行。
        /// 液体名带下划线且可点击，左右键分别追溯它的来源 / 用途。
        /// 使用页与搅拌机页共用这一份渲染。
        /// </summary>
        private static void RenderLiquidComposition(CustomUIBuilder builder,
                                                    List<LiquidManager.Component> components,
                                                    string idPrefix = "use_")
        {
            if (components == null || components.Count == 0) return;

            builder.AddLabel(LiquidManager.TotalText(components[0].Total), idPrefix + "dissolve_total");
            SetCompositionFont(builder.Last);

            for (int i = 0; i < components.Count; i++)
            {
                var component = components[i];

                builder.BeginRow(2f, TextAnchor.MiddleLeft);

                builder.AddLabel("<u>" + component.Name + "</u>", idPrefix + "comp_name" + i);
                var nameLabel = builder.Last;
                SetCompositionFont(nameLabel);
                AttachLiquidLookup(nameLabel, component.Id);

                // Summary 行只写名字（化学产品的详细成分不适合逐种列出）
                if (!component.Summary)
                {
                    double pct = component.Total > 0
                        ? component.Amount * 100.0 / component.Total : 0.0;
                    builder.AddLabel(
                        ": " + WaterScannerUIManager.FormatAmount(component.Amount)
                        + " (" + pct.ToString("0.##") + "%)",
                        idPrefix + "comp_value" + i);
                    SetCompositionFont(builder.Last);
                }

                builder.End();
            }
        }

        private static void SetCompositionFont(CustomUIElement element)
        {
            if (element == null || element.text == null) return;
            element.text.fontSize = 15f;
            element.text.alignment = TextAlignmentOptions.Left;
        }

        /// <summary>给成分名挂左/右键查询，行为与物品格一致。</summary>
        private static void AttachLiquidLookup(CustomUIElement element, string liquidId)
        {
            if (element == null || element.text == null || string.IsNullOrEmpty(liquidId)) return;

            var entry = LiquidCatalog.Find(LiquidCatalog.EntryPrefix + liquidId);
            if (entry == null) return;

            try
            {
                element.text.raycastTarget = true;
                var go = element.text.gameObject;
                var trigger = go.GetComponent<UnityEngine.EventSystems.EventTrigger>();
                if (trigger == null) trigger = go.AddComponent<UnityEngine.EventSystems.EventTrigger>();

                var click = new UnityEngine.EventSystems.EventTrigger.Entry
                {
                    eventID = UnityEngine.EventSystems.EventTriggerType.PointerClick
                };
                click.callback.AddListener(delegate (UnityEngine.EventSystems.BaseEventData data)
                {
                    var pointer = data as UnityEngine.EventSystems.PointerEventData;
                    OpenFor(entry, pointer != null
                        && pointer.button == UnityEngine.EventSystems.PointerEventData.InputButton.Right);
                });
                trigger.triggers.Add(click);
            }
            catch { }
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

        private static void RenderRecipeBlock(CustomUIBuilder builder, Category category, PageSlice slice)
        {
            RenderRecipePage(builder, category.Recipes, slice.Block);
        }

        private static void RenderRecipePage(CustomUIBuilder builder, List<RecipeInfo> recipes, int recipeIndex)
        {
            if (recipes == null || recipeIndex < 0 || recipeIndex >= recipes.Count) return;

            var recipe = recipes[recipeIndex];

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
        /// <summary>
        /// 机器页：机器原生格子就是配方的载体——一条配方 = 画一遍面板，
        /// 物品图标放进对应格子。每个纯度组一个容器，容器内每秒轮播一条配方。
        /// </summary>
        private static void RenderMachineBlock(CustomUIBuilder builder, Category category, PageSlice slice)
        {
            if (_entry == null) return;

            string machineId = _entry.Id;
            var groups = MachinePanels.RecipesFor(machineId);
            if (groups == null || groups.Length == 0) return;

            int gi = Mathf.Clamp(slice.Block, 0, groups.Length - 1);
            RenderMachineGroup(builder, machineId, groups[gi], gi, true, false);
        }

        private static void RenderMachineRecipeBlock(CustomUIBuilder builder, Category category, PageSlice slice)
        {
            if (category == null || category.MachineGroups == null ||
                category.MachineGroups.Count == 0) return;

            int gi = Mathf.Clamp(slice.Block, 0, category.MachineGroups.Count - 1);
            RenderMachineGroup(builder, category.MachineId, category.MachineGroups[gi], gi, false, true);
        }

        private static void RenderMachineGroup(CustomUIBuilder builder, string machineId,
                                               MachinePanels.RecipeGroup group, int groupIndex,
                                               bool highlightMachine, bool filterVariants)
        {
            if (_entry == null) return;
            if (string.IsNullOrEmpty(machineId)) return;
            if (group == null) return;

            var variants = VariantsFor(group, filterVariants);
            if (variants == null || variants.Length == 0) return;

            if (_vanillaVariant >= variants.Length) _vanillaVariant = 0;
            var recipe = variants[_vanillaVariant];
            if (recipe == null) return;

            // 顶部第一行：配方名 + 机器图标。
            builder.BeginRow(6f, UnityEngine.TextAnchor.MiddleCenter);
            _machineTopRow = builder.CurrentContainer;   // 实测高度用
            builder.AddLabel(MachineRecipeTitle(group, recipe), "mach_group");
            builder.AddFlexibleSpace();
            RenderGoods(builder, machineId, highlightMachine, 3180, false, null, 52f);
            builder.End();

            // 顶部第二行：质量需求，居中放在机器图标下面。
            // 不需要质量时文字为空，不显示这一行内容（行本身保留，避免每秒轮播时窗口跳动）。
            _machineQualityLabel = null;
            _machineQualityRow = null;
            if (!string.IsNullOrEmpty(group.Purity))
            {
                builder.BeginRow(6f, UnityEngine.TextAnchor.MiddleCenter);
                _machineQualityRow = builder.CurrentContainer;
                builder.AddFlexibleSpace();
                builder.AddLabel(QualityRequirementText(recipe), "mach_quality");
                _machineQualityLabel = builder.Last;
                if (_machineQualityLabel != null && _machineQualityLabel.text != null)
                {
                    _machineQualityLabel.text.fontSize = 14f;
                    _machineQualityLabel.text.alignment = TextAlignmentOptions.Center;
                }
                builder.AddFlexibleSpace();
                builder.End();
            }

            // 蒸发器没有固定配方：每个可蒸出的液体一页，页面画「容器 + 馏出」两个原版槽。
            // 第一步：造原版窗口外壳（还没挂、没放物品），先拿到它缩放后的真实尺寸
            if (_vanillaMachine != null) { _vanillaMachine.Destroy(); _vanillaMachine = null; }
            _vanillaMachine = new VanillaMachinePanel();

            bool built = false;
            if (string.Equals(machineId, "furnace", StringComparison.Ordinal))
                built = _vanillaMachine.BuildFurnaceShell();
            else if (string.Equals(machineId, "blender", StringComparison.Ordinal))
                built = _vanillaMachine.BuildBlenderShell();
            else if (string.Equals(machineId, "chem_finisher", StringComparison.Ordinal))
                built = _vanillaMachine.BuildChemFinisherShell();
            else if (string.Equals(machineId, "evaporator", StringComparison.Ordinal))
                built = _vanillaMachine.BuildEvaporatorShell();

            if (!built)
            {
                if (ModEntry.Log != null) ModEntry.Log.Warning("[vanilla-machine] build failed");
                _vanillaMachine.Destroy();
                _vanillaMachine = null;
                return;
            }

            // 占位容器：里面放一个 AddSpace(面板高度)，让 bodyHost 的 VerticalLayoutGroup
            // 把「标题 + 面板高度 + 翻页条」都算进总高——这样窗口高度正好包住面板，
            // 标题和翻页条不会被挤出窗口（之前面板 ignoreLayout，高度算不进去才被挤）。
            //
            // 面板上限与普通页共用 MaxContentHeight()：窗口最大高度扣掉
            // WindowChrome、顶部行、翻页条与间距，剩下的全给面板。
            float panelMaxH = MaxMachinePanelHeight();

            // 搅拌机 / 化学制剂成型机 / 蒸发器在面板下面还要写成分，先把这部分高度留出来
            if (HasMachineComposition(machineId))
                panelMaxH -= MachineCompositionHeight(recipe);

            builder.BeginColumn(2f, UnityEngine.TextAnchor.MiddleCenter);
            var host = builder.CurrentContainer;
            float panelH = Mathf.Max(1f, _vanillaMachine.ScaledSize(_bodyHost, panelMaxH, WinWidth - 40f).y);
            builder.End();

            // 关键：host 在 bodyHost 的 VerticalLayoutGroup 下，childControlWidth/Height=true，
            // 宽高都取自己的 LayoutElement。不设宽度只有默认 100px（面板中线会贴左边缘）；
            // 高度也要显式设成面板高度，否则 host 可能比面板矮、面板下半截盖住下方翻页条。
            if (host != null)
            {
                var hostLayout = EnsureLayoutElement(host);
                hostLayout.preferredWidth = WinWidth - 40f;
                hostLayout.flexibleWidth = 1f;   // 撑满内容列，面板按 host 中心对齐
                hostLayout.minHeight = panelH;
                hostLayout.preferredHeight = panelH;
                hostLayout.flexibleHeight = 0f;
            }

            // 第二步：把面板挂进占位容器、相对 host 正中心绝对定位，再缩放补偿 Canvas 差异，
            // 布局跑一次后塞物品（否则 inventory inner rect 还是 0，物品会浮到格子外）。
            var rect = _vanillaMachine.AttachContentTo(host);
            if (rect != null) CustomUIBuilder.RebuildLayout(rect);

            // 用**实测内容高度**（不是 rect 高度）修正占位与 host：
            // rect 含底部留白，按它给高度会让内容下方空一段、翻页条被推远。
            float visualH = _vanillaMachine.MatchGameCanvasScale(host, panelMaxH, WinWidth - 40f);
            if (visualH > 10f && host != null)
            {
                // host 高度严格等于面板**实测视觉高度**：之前用 rect 高度(782)，
                // 面板实际只有 700 左右，中间 80 多就成了「面板与翻页条之间的空白」。
                var hostLe = EnsureLayoutElement(host);
                hostLe.minHeight = visualH;
                hostLe.preferredHeight = visualH;
                hostLe.flexibleHeight = 0f;
            }

            FillVanillaMachine(recipe, group.Purity);
            _vanillaMachine.HighlightItem(_entry == null ? null : _entry.Id);
            // 查询的是液体条目时，装着它的容器槽标绿（成分由液体管理器判定）
            _vanillaMachine.HighlightLiquid(QueryLiquidId());

            // 搅拌机 / 化学制剂成型机 / 蒸发器：面板下面按成分检测器的格式列出液体成分（一行一种 + 总体积）
            if (HasMachineComposition(machineId))
            {
                // 成分随品质变化的化学产品（药）只写总量 + 主要成分名
                bool summaryOnly = IsVariableCompositionDrug(recipe);
                var components = LiquidManager.Components(_vanillaMachine.ContainerSample,
                    summaryOnly, summaryOnly ? MainIngredientName(recipe) : null);
                if (components.Count > 0)
                {
                    builder.BeginColumn(2f, UnityEngine.TextAnchor.UpperLeft);
                    RenderLiquidComposition(builder, components);
                    builder.End();
                }
            }

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[vanilla-machine] " + machineId + " v" + _vanillaVariant
                                 + " panelH=" + panelH + " visualH=" + visualH
                                 + " rect=" + (rect == null ? "null" : rect.rect.size.ToString()));
        }


        /// <summary>哪些机器的面板下面还要列容器成分（容器里真有液体可读）。</summary>
        private static bool HasMachineComposition(string machineId)
        {
            return string.Equals(machineId, "blender", StringComparison.Ordinal)
                   || string.Equals(machineId, "chem_finisher", StringComparison.Ordinal)
                   || string.Equals(machineId, "evaporator", StringComparison.Ordinal);
        }

        /// <summary>
        /// 机器成分区高度：第一行总体积 + 每种液体一行（字号 15）。
        /// 化学产品只写总量 + 主要成分名，固定两行；
        /// 化学制剂成型机 / 蒸发器按配方只灌一种液体，也是两行。
        /// </summary>
        private static float MachineCompositionHeight(MachinePanels.Recipe recipe)
        {
            int lines;

            if (recipe != null && !string.IsNullOrEmpty(recipe.ContainerLiquid))
            {
                lines = 2;
            }
            else if (IsVariableCompositionDrug(recipe))
            {
                lines = 2;
            }
            else
            {
                int components = 0;
                var item = RecipeItem(recipe);
                if (item != null)
                {
                    try
                    {
                        var liquids = Liquid.Liquids;
                        for (int i = 0; i < liquids.Count; i++)
                        {
                            if (item.GetTagReadonly(liquids[i].currentPart).GetInt() > 0)
                                components++;
                        }
                    }
                    catch { }
                }

                if (components <= 0) components = 4;
                lines = components + 1;   // 总体积 + 各成分
            }

            return lines * 20f + 6f;
        }

        /// <summary>配方里的投入物品实例（取第一个输入）。</summary>
        private static GameItem RecipeItem(MachinePanels.Recipe recipe)
        {
            if (recipe == null || recipe.Inputs == null || recipe.Inputs.Length == 0) return null;

            var entry = RecipeIndex.FindEntry(recipe.Inputs[0]);
            return entry == null ? null : entry.Item;
        }

        /// <summary>
        /// 成分会随品质变化的化学产品（药）：游戏用 CHEMICAL_PRODUCT_TAG 标记，
        /// 详细成分不适合逐种列出，只展示总量和主要成分名。
        /// </summary>
        private static bool IsVariableCompositionDrug(MachinePanels.Recipe recipe)
        {
            var item = RecipeItem(recipe);
            if (item == null) return false;

            try { return item.IsTag("CHEMICAL_PRODUCT_TAG"); }
            catch { return false; }
        }

        /// <summary>化学产品的主要成分名：游戏把有效成分写在 ACTIVATE_INGREDIENT_TAG。</summary>
        private static string MainIngredientName(MachinePanels.Recipe recipe)
        {
            var item = RecipeItem(recipe);
            if (item == null) return null;

            try
            {
                string liquidId = item.GetTagReadonly("ACTIVATE_INGREDIENT_TAG").GetString();
                var liquid = Liquid.GetLiquidFromId(liquidId);
                if (liquid == null) return null;

                return string.IsNullOrEmpty(liquid.displayName)
                    ? liquid.identifier : liquid.displayName;
            }
            catch { return null; }
        }

        private static string WorldRect(RectTransform rect)
        {
            if (rect == null) return "null";
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return string.Format("(x{0:F0}..{1:F0} y{2:F0}..{3:F0})",
                corners[0].x, corners[2].x, corners[0].y, corners[2].y);
        }

        /// <summary>LayoutElement 的关键值，用来确认高度声明有没有落到位。</summary>
        private static string LayoutInfo(RectTransform rect)
        {
            if (rect == null) return "null";
            var le = rect.GetComponent<UnityEngine.UI.LayoutElement>();
            if (le == null) return "noLE";
            return string.Format("(min{0:F0} pref{1:F0} flex{2:F1})",
                le.minHeight, le.preferredHeight, le.flexibleHeight);
        }

        /// <summary>父级容器上挂了哪些布局组、是否控制子级高度。</summary>
        private static string GroupInfo(RectTransform rect)
        {
            if (rect == null) return "null";
            var v = rect.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
            var h = rect.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>();
            var g = rect.GetComponent<UnityEngine.UI.GridLayoutGroup>();
            var sb = new System.Text.StringBuilder();
            if (v != null) sb.Append("VLG(ctrlH=").Append(v.childControlHeight).Append(",ctrlW=").Append(v.childControlWidth).Append(")");
            if (h != null) sb.Append(" HLG");
            if (g != null) sb.Append(" Grid");
            if (sb.Length == 0) sb.Append("none");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- 成分反应页

        private static void RenderReactionBlock(CustomUIBuilder builder, Category category, PageSlice slice)
        {
            if (category == null || category.Reactions == null || category.Reactions.Count == 0) return;

            int i = Mathf.Clamp(slice.Block, 0, category.Reactions.Count - 1);
            RenderReactionPage(builder, category.Reactions[i]);
        }

        /// <summary>
        /// 一条成分反应：原版面板三列——左 = 反应物容器，中 = 温度 / 溶剂 / 条件，右 = 产物容器。
        /// 反应物容器里按 10 ml 基准连同最佳量溶剂一起灌入；
        /// 面板下面左右两列分别用液体管理器列出两侧容器的成分。
        /// </summary>
        private static void RenderReactionPage(CustomUIBuilder builder, LiquidReaction reaction)
        {
            if (reaction == null) return;

            // 有温度限制的反应：右上角放一台加热板（点它可查加热板自身）
            if (reaction.TempMin > 0f)
            {
                builder.BeginRow(6f, UnityEngine.TextAnchor.MiddleCenter);
                builder.AddFlexibleSpace();
                RenderGoods(builder, "heating_plate", false, 3190, false, null, 52f);
                builder.End();
            }

            if (_vanillaMachine != null) { _vanillaMachine.Destroy(); _vanillaMachine = null; }
            _vanillaMachine = new VanillaMachinePanel();

            if (!_vanillaMachine.BuildReactionShell(ReactionConditionLines(reaction)))
            {
                if (ModEntry.Log != null) ModEntry.Log.Warning("[reaction] build failed");
                _vanillaMachine.Destroy();
                _vanillaMachine = null;
                return;
            }

            string[] reactantIds;
            float[] reactantMl;
            BuildReactionInputs(reaction, out reactantIds, out reactantMl);

            // 面板下面还要写两侧成分，先留出这部分高度
            float panelMaxH = MaxMachinePanelHeight() - ReactionCompositionHeight(reaction);

            builder.BeginColumn(2f, UnityEngine.TextAnchor.MiddleCenter);
            var host = builder.CurrentContainer;
            float panelH = Mathf.Max(1f, _vanillaMachine.ScaledSize(_bodyHost, panelMaxH, WinWidth - 40f).y);
            builder.End();

            if (host != null)
            {
                var hostLayout = EnsureLayoutElement(host);
                hostLayout.preferredWidth = WinWidth - 40f;
                hostLayout.flexibleWidth = 1f;
                hostLayout.minHeight = panelH;
                hostLayout.preferredHeight = panelH;
                hostLayout.flexibleHeight = 0f;
            }

            var rect = _vanillaMachine.AttachContentTo(host);
            if (rect != null) CustomUIBuilder.RebuildLayout(rect);

            float visualH = _vanillaMachine.MatchGameCanvasScale(host, panelMaxH, WinWidth - 40f);
            if (visualH > 10f && host != null)
            {
                var hostLe = EnsureLayoutElement(host);
                hostLe.minHeight = visualH;
                hostLe.preferredHeight = visualH;
                hostLe.flexibleHeight = 0f;
            }

            _vanillaMachine.FillReaction(reactantIds, reactantMl,
                                         reaction.Products, reaction.ProductMl);
            // 装着所查询液体的那一侧容器标绿（反应物或产物，可能两侧都有）
            _vanillaMachine.HighlightLiquid(QueryLiquidId());

            // 两个容器下面分别列成分：左 = 反应物，右 = 产物
            builder.BeginRow(12f, UnityEngine.TextAnchor.UpperLeft);

            builder.BeginColumn(2f, UnityEngine.TextAnchor.UpperLeft);
            RenderLiquidComposition(builder, LiquidManager.Components(_vanillaMachine.ContainerSample),
                                    "react_in_");
            builder.End();

            builder.AddFlexibleSpace();

            builder.BeginColumn(2f, UnityEngine.TextAnchor.UpperLeft);
            if (reaction.Products == null || reaction.Products.Length == 0)
            {
                // 无实物产物：容器保持空，底下写残留的溶剂；没有溶剂就写「无」
                string label;
                string liquidId = null;

                if (!string.IsNullOrEmpty(reaction.Solvent))
                {
                    label = "<u>" + LiquidManager.NameOf(reaction.Solvent) + "</u>";
                    liquidId = reaction.Solvent;
                }
                else
                {
                    label = Loc("react_none", "无");
                }

                builder.AddLabel(label, "react_out_none");
                SetCompositionFont(builder.Last);
                if (liquidId != null) AttachLiquidLookup(builder.Last, liquidId);
            }
            else
            {
                RenderLiquidComposition(builder, LiquidManager.Components(_vanillaMachine.ProductSample),
                                        "react_out_");
            }
            builder.End();

            builder.End();
        }

        /// <summary>反应物 + 最佳量溶剂 / 催化剂，一起装进左侧容器。</summary>
        private static void BuildReactionInputs(LiquidReaction reaction,
                                                out string[] ids, out float[] ml)
        {
            var idList = new List<string>();
            var mlList = new List<float>();

            if (reaction.Reactants != null)
                for (int i = 0; i < reaction.Reactants.Length; i++)
                {
                    idList.Add(reaction.Reactants[i]);
                    mlList.Add(reaction.ReactantMl != null && i < reaction.ReactantMl.Length
                        ? reaction.ReactantMl[i] : 0f);
                }

            if (!string.IsNullOrEmpty(reaction.Solvent) && reaction.SolventMl > 0f)
            {
                idList.Add(reaction.Solvent);
                mlList.Add(reaction.SolventMl);
            }

            ids = idList.ToArray();
            ml = mlList.ToArray();
        }

        /// <summary>面板下面两侧成分区的高度（左右并排，取行数多的一侧）。</summary>
        private static float ReactionCompositionHeight(LiquidReaction reaction)
        {
            int left = (reaction.Reactants == null ? 0 : reaction.Reactants.Length)
                       + (string.IsNullOrEmpty(reaction.Solvent) || reaction.SolventMl <= 0f ? 0 : 1)
                       + 1;   // 总体积
            int right = reaction.Products == null || reaction.Products.Length == 0
                ? 0
                : reaction.Products.Length + 1;

            return Mathf.Max(left, right) * 20f + 6f;
        }

        /// <summary>中间那列的条件：温度、溶剂 / 催化剂、额外条件。</summary>
        private static string[] ReactionConditionLines(LiquidReaction reaction)
        {
            var lines = new List<string>();

            lines.Add(Loc("react_temp", "温度") + " " + ReactionTempText(reaction));

            if (!string.IsNullOrEmpty(reaction.Solvent))
            {
                string label = reaction.SolventIsCatalyst
                    ? Loc("react_catalyst", "催化")
                    : Loc("react_solvent", "溶剂");
                lines.Add(label + " " + LiquidManager.NameOf(reaction.Solvent));
            }

            if (!string.IsNullOrEmpty(reaction.ConditionKey) ||
                !string.IsNullOrEmpty(reaction.ConditionFallback))
            {
                string condition = string.IsNullOrEmpty(reaction.ConditionKey)
                    ? reaction.ConditionFallback
                    : Loc(reaction.ConditionKey, reaction.ConditionFallback);
                if (!string.IsNullOrEmpty(condition)) lines.Add(condition);
            }

            return lines.ToArray();
        }

        private static string ReactionTempText(LiquidReaction reaction)
        {
            if (reaction.TempMin <= 0f && reaction.TempMax <= 0f)
                return Loc("react_ambient", "常温");

            if (reaction.TempMax > reaction.TempMin)
                return Mathf.RoundToInt(reaction.TempMin) + "–"
                       + Mathf.RoundToInt(reaction.TempMax) + " °C";

            return "≥" + Mathf.RoundToInt(reaction.TempMin) + " °C";
        }

        /// <summary>把当前配方的物品塞进原版窗口的真 inventory（轮播换配方时只换物品，不重建窗口）。</summary>
        private static void FillVanillaMachine(MachinePanels.Recipe recipe, string purity = null)
        {
            if (_vanillaMachine == null || recipe == null) return;
            _vanillaMachine.Fill(recipe.Inputs, recipe.Modules, recipe.Outputs, purity,
                                 recipe.Container, recipe.ContainerLiquid, recipe.ContainerVolume);
        }

        private static void RenderMachineRecipeStrip(MachineRecipeStrip strip)
        {
            if (strip == null || strip.Host == null || strip.Group == null) return;

            var manager = CustomUIManager.Instance;
            if (manager == null || _window == null || !_window.IsAlive) return;

            for (int i = _highlights.Count - 1; i >= 0; i--)
                if (_highlights[i].Key == null || _highlights[i].Value == null) _highlights.RemoveAt(i);

            CustomUIBuilder.ClearContainer(strip.Host);
            var builder = new CustomUIBuilder(manager, _window, strip.Host);

            var variants = strip.Group.Variants;
            int vi = ((strip.Variant % variants.Length) + variants.Length) % variants.Length;
            var recipe = variants[vi];
            if (recipe == null) return;

            int mi = recipe.Modules.Length == 0 ? -1
                : ((strip.Module % recipe.Modules.Length) + recipe.Modules.Length) % recipe.Modules.Length;
            string moduleId = mi >= 0 ? recipe.Modules[mi] : null;

            DrawMachinePanel(builder, MachinePanels.For(_entry.Id), recipe.Inputs,
                             moduleId, recipe.Outputs, strip.Base);

            string note = Loc(recipe.NoteKey, recipe.NoteFallback);
            if (!string.IsNullOrEmpty(note)) builder.AddLabel(note, "mach_note");
        }

        /// <summary>
        /// 机器原生面板：逐格画出界面结构，配方物品按角色放进对应格子。
        /// Grid 的 7x5 形状格依次填投料；单槽按角色填模块 / 产物；电池 / 纸条留空框。
        /// 格子里只画图标（不画名字标签），否则一个槽里多个元素会在行布局里叠成一团。
        /// </summary>
        private static void DrawMachinePanel(CustomUIBuilder builder, MachinePanels.Panel[] panels,
                                             string[] inputs, string moduleId, string[] outputs, int idBase)
        {
            if (panels == null || panels.Length == 0) return;

            int outputAt = 0;
            for (int p = 0; p < panels.Length; p++)
            {
                var panel = panels[p];
                if (panel == null) continue;

                string label = MachinePanelLabel(panel);

                if (panel.Kind == MachinePanels.Grid)
                {
                    builder.AddLabel(label, "mach_panel");
                    DrawGridShape(builder, slotIndex =>
                    {
                        if (panel.Role == MachinePanels.RoleInput && inputs != null && slotIndex < inputs.Length)
                            return inputs[slotIndex];
                        return null;
                    }, idBase + p * 16);
                    continue;
                }

                builder.BeginRow(6f, UnityEngine.TextAnchor.MiddleLeft);
                builder.AddLabel(label, "mach_panel");
                builder.AddFlexibleSpace();

                string itemId = null;
                if (panel.Role == MachinePanels.RoleModule) itemId = moduleId;
                else if (panel.Role == MachinePanels.RoleOutput && outputs != null && outputAt < outputs.Length)
                    itemId = outputs[outputAt++];

                DrawItemInSlot(builder, itemId, idBase + p);
                builder.End();
            }
        }

        /// <summary>格子里放一个物品：有图标就画图标，没图标画空框。只画图标，不画名字。</summary>
        private static void DrawItemInSlot(CustomUIBuilder builder, string id, int elementId)
        {
            if (string.IsNullOrEmpty(id))
            {
                DrawSlot(builder);
                return;
            }

            var sprite = CatalogWindow.IconFor(id) ?? CatalogWindow.CompositeIconFor(id);
            if (sprite == null)
            {
                DrawSlot(builder);
                return;
            }

            float side = MachineCellSide(24f);
            float w, h;
            CatalogWindow.FitIcon(sprite, side, out w, out h);
            builder.AddImage(sprite, w, h, "mach_item" + elementId);

            // 物品图标下面垫一层原版机器格子底框（和空槽同一张 cell 精灵）
            PutCellFrame(builder, side);
        }

        /// <summary>
        /// 在刚画的物品格下面垫一层原版格子底框：挂到同一个父级、排在图标之前绘制，
        /// ignoreLayout 不参与布局，靠 _highlights 跟随图标格对齐。
        /// </summary>
        private static void PutCellFrame(CustomUIBuilder builder, float side)
        {
            var frame = MachineSlotSprite();
            var cell = builder.Last != null ? builder.Last.Rect : null;
            if (frame == null || cell == null || cell.parent == null) return;

            try
            {
                var go = new GameObject("machFrame", typeof(RectTransform), typeof(UnityEngine.UI.Image));
                go.transform.SetParent(cell.parent, false);
                go.transform.SetSiblingIndex(cell.GetSiblingIndex());

                var image = go.GetComponent<UnityEngine.UI.Image>();
                image.sprite = frame;
                image.raycastTarget = false;
                var rect = (RectTransform)go.transform;
                rect.sizeDelta = new Vector2(side, side);

                var ignore = go.AddComponent<UnityEngine.UI.LayoutElement>();
                ignore.ignoreLayout = true;

                _highlights.Add(new KeyValuePair<RectTransform, RectTransform>(rect, cell));
            }
            catch (Exception e) { Warn("machine cell frame: " + e.Message); }
        }

        /// <summary>每秒推进机器配方的轮播索引：配方组间独立，模块数多个时也单独轮。</summary>
        private static void TickMachineRecipes()
        {
            if (!IsOpen || _vanillaMachine == null || _entry == null) return;

            var category = CurrentCategory();
            if (category == null) return;

            MachinePanels.RecipeGroup[] groups;
            if (category.Kind == CatMachine)
            {
                groups = MachinePanels.RecipesFor(_entry.Id);
            }
            else if (category.Kind == CatMachineRecipe && category.MachineGroups != null)
            {
                groups = category.MachineGroups.ToArray();
            }
            else return;

            if (groups == null || groups.Length == 0) return;

            // 当前页对应的配方组（分页计划里的 block 就是组序号）
            int gi = Mathf.Clamp(_currentSlice.Block, 0, groups.Length - 1);
            var group = groups[gi];
            if (group == null || group.Variants == null || group.Variants.Length <= 1) return;

            // 同档内轮播：切到下一条配方，只换原版 inventory 里的物品，不重建窗口。
            // 反查页只轮播「包含当前物品」的变体。
            bool filterVariants = category.Kind == CatMachineRecipe;
            var variants = VariantsFor(group, filterVariants);
            if (variants == null || variants.Length <= 1) return;

            _vanillaVariant = (_vanillaVariant + 1) % variants.Length;
            var recipe = variants[_vanillaVariant];

            // 顶部第二行「需要：质量 +X%」跟材料一起换，保持同一条配方的信息同步
            if (_machineQualityLabel != null)
            {
                _machineQualityLabel.SetText(QualityRequirementText(recipe));
                if (_machineQualityLabel.text != null)
                    _machineQualityLabel.text.alignment = TextAlignmentOptions.Center;

                if (_machineQualityRow != null)
                {
                    try { CustomUIBuilder.RebuildLayout(_machineQualityRow); } catch { }
                }
            }

            try
            {
                FillVanillaMachine(recipe, group.Purity);
                _vanillaMachine.HighlightItem(_entry == null ? null : _entry.Id);
                _vanillaMachine.HighlightLiquid(QueryLiquidId());
            }
            catch (Exception e) { Warn("machine roll: " + e.Message); }
        }

        /// <summary>
        /// 机器页左上角配方名：熔炉用「物品名（本地化纯度）」，
        /// 例如「金属锭（完美纯度）」；其他机器仍用分组标题。
        /// </summary>
        private static string MachineRecipeTitle(MachinePanels.RecipeGroup group, MachinePanels.Recipe recipe)
        {
            if (group == null) return "?";

            // 搅拌机：一物品一页，标题直接用该物品的显示名
            if (!string.IsNullOrEmpty(group.TitleItemId))
            {
                var entry = RecipeIndex.FindEntry(group.TitleItemId);
                if (entry != null && !string.IsNullOrEmpty(entry.DisplayName)) return entry.DisplayName;

                string name = ItemName(group.TitleItemId);
                if (!string.IsNullOrEmpty(name)) return name;
            }

            if (!string.IsNullOrEmpty(group.Purity) &&
                recipe != null && recipe.Outputs != null && recipe.Outputs.Length > 0)
            {
                string id = recipe.Outputs[0];
                var entry = RecipeIndex.FindEntry(id);
                string itemName = entry != null && !string.IsNullOrEmpty(entry.DisplayName)
                    ? entry.DisplayName
                    : ItemName(id);

                string purity = VanillaMachinePanel.PurityDisplayName(group.Purity);
                if (!string.IsNullOrEmpty(purity))
                {
                    string suffix = Loc("purity_suffix", " ({0})");
                    return itemName + string.Format(suffix, purity);
                }
            }

            return Loc(group.TitleKey, group.TitleFallback);
        }

        /// <summary>
        /// 熔炉顶部那行：这一条配方需要的模块质量加成（代码里的百分比 / 100）。
        /// 只写数值，不区分助熔剂；助熔剂 / 材料在下面随配方一起轮播。
        /// </summary>
        private static string QualityRequirementText(MachinePanels.Recipe recipe)
        {
            if (recipe == null || recipe.RequiredQualityPercent <= 0) return "";

            // 原版就有的两条词条：
            //   mech_machine_required   = 需要 / Requires
            //   mech_module_stat_quality = 质量 / Quality
            // 数值沿用原版模块词条的格式：+X%
            string prefix = VanillaMachinePanel.MechLabel("mech_machine_required", "Requires");
            string quality = VanillaMachinePanel.MechLabel("mech_module_stat_quality", "Quality");

            return prefix + " " + quality + " +" + recipe.RequiredQualityPercent + "%";
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
        private static void DrawGridShape(CustomUIBuilder builder,
                                          System.Func<int, string> itemFor = null, int idBase = 0)
        {
            string shape = MachinePanels.ContainerShape;
            var frame = MachineSlotSprite();
            float side = MachineCellSide(18f);

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
                    {
                        string itemId = itemFor == null ? null : itemFor(idx);
                        var sprite = string.IsNullOrEmpty(itemId) ? null
                            : (CatalogWindow.IconFor(itemId) ?? CatalogWindow.CompositeIconFor(itemId));
                        if (sprite != null)
                        {
                            float w, h;
                            CatalogWindow.FitIcon(sprite, side, out w, out h);
                            builder.AddImage(sprite, w, h, "mach_cellitem_" + idBase + "_" + idx);
                            PutCellFrame(builder, side);   // 物品图标下面垫原版格子底框
                        }
                        else builder.AddImage(frame, side, side, "mach_cell_" + r + "_" + c);
                    }
                    else
                        builder.AddSpace(side);
                }

                builder.End();
            }
        }

        /// <summary>单格功能槽：一个格框。</summary>
        private static void DrawSlot(CustomUIBuilder builder)
        {
            var frame = MachineSlotSprite();
            if (frame != null)
            {
                float side = MachineCellSide(24f);
                builder.AddImage(frame, side, side, "mach_slot");
            }
            else builder.AddLabel("▢", "mach_slot_text");
        }

        /// <summary>
        /// 机器格子精灵：直接用游戏原版的那一张——
        /// <c>GameGridInventory.SetMachineryStyle</c> 里写死了
        /// <c>SetBackgroundSprite("Window/machinery", "cell")</c>。
        /// 之前用血条描边凑合，和原版窗口里的格子完全不是一个样子。
        /// 拿不到才退回描边，再不行走字符画兜底。
        /// </summary>
        private static Sprite _machineSlotSprite;
        private static bool _machineSlotTried;

        private static Sprite MachineSlotSprite()
        {
            if (_machineSlotTried) return _machineSlotSprite;
            _machineSlotTried = true;

            // 原版机器格子（SetMachineryStyle 的实参）
            try { _machineSlotSprite = RenderHandler.TryLoadFromAtlas("Window/machinery", "cell"); }
            catch { }
            if (_machineSlotSprite != null) return _machineSlotSprite;

            var handler = RenderHandler.current;
            if (handler != null) _machineSlotSprite = handler.healthBarOutline;
            return _machineSlotSprite;
        }

        /// <summary>格子的边长：优先用原版精灵的像素大小（SetNativeSize），拿不到退回旧值。</summary>
        private static float MachineCellSide(float fallback)
        {
            var sprite = MachineSlotSprite();
            if (sprite == null) return fallback;

            float w = sprite.rect.width / Mathf.Max(1f, sprite.pixelsPerUnit);
            float h = sprite.rect.height / Mathf.Max(1f, sprite.pixelsPerUnit);
            return Mathf.Max(8f, Mathf.Max(w, h));
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
