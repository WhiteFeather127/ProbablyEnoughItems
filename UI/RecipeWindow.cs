﻿﻿﻿﻿﻿﻿﻿using System;
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
            // 内容水平居中：滚动条只占右侧 S，但居中会把内容右半边多推出去 S/2 的两倍，
            // 所以可用宽要扣 2 × 滚动条宽，最右一列才不会被压住。
            return _gridContentWidth > 60f
                ? _gridContentWidth
                : WinWidth - 36f - _scrollbarWidth * 2f;
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

        /// <summary>客人立绘最长边。客人精灵本身一般是 256 见方，再放大会开始发虚。</summary>
        private const float PortraitMaxSide = 300f;

        /// <summary>同一客人的多种立绘轮播间隔（秒）。</summary>
        private const float PortraitSwitchSeconds = 1f;

        private static float _lastHeight = -1f;
        private static float _lastWidth = -1f;

        /// <summary>
        /// 机器页需要更宽时由渲染阶段写入（0 = 用默认 WinWidth）。
        /// 机器面板按「默认宽度」排布：一行放不下就加宽窗口，只有超过
        /// <see cref="MaxMachinePanelWidth"/> 才退回自动换行。
        /// </summary>
        private static float _pageWidthOverride;

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
        private const int CatGunUpgrade = 14;      // 双向：枪械翻新（一把枪 × 一个可翻新品相一页）
        private const int CatGunMods = 15;         // 用途：枪械配件（一把枪一页，8 个改装槽轮播）

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

            /// <summary>枪械翻新页：一把枪的每个可翻新品相一页。</summary>
            internal List<GunUpgradePage> GunUpgrades;

            /// <summary>
            /// 枪械配件页：每页一把枪的改装窗口。
            /// 看枪时只有它自己；看配件时是所有能装它的枪。
            /// </summary>
            internal List<string> GunModGunIds;
        }

        /// <summary>
        /// 枪械翻新的一页：当前品相 <see cref="From"/> → 翻新后品相 <see cref="To"/>。
        /// 品相索引见 <see cref="GunUpgrades"/>（0 全新 / 1 极佳 / 2 良好 / 3 一般 / 4 劣）。
        /// </summary>
        private sealed class GunUpgradePage
        {
            internal string GunId;
            internal int From;
            internal int To;
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
        /// 一页 = 一个内容块（客人 / 配方 / 机器档位 / 掉落池 …）。
        /// 块内不再切片：内容超高由页面滚动条处理，Start/Count 恒为 0。
        /// </summary>
        private struct PageSlice
        {
            internal int Block;
            internal int Start;
            internal int Count;
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

            /// <summary>渲染某个块。块内不再切片，超高由页面滚动条处理。</summary>
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

            /// <summary>
            /// 收购 WATER 时客人要的水质（pure / hq / base…，来自 willAcceptItem 谓词）。
            /// 标签格下面的说明写这几个档位，而不是空样本的「未知的水」。
            /// </summary>
            internal string[] WaterQualities;
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

            var openPerf = System.Diagnostics.Stopwatch.StartNew();

            _entry = entry;
            QueryTarget.Set(entry);   // 查询态（绿底 / 停轮播）的唯一来源
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

            long tRender = openPerf.ElapsedMilliseconds;

            if (_window != null && _window.IsAlive) Raise();

            // 这一帧往后一点点再提一次：点击事件里图鉴自己也会 BringToFront，
            // 谁后执行谁在上面，晚一帧补一次才稳。
            _raiseAt = Time.unscaledTime + 0.05f;

            if (ModEntry.Log != null)
            {
                try
                {
                    if (openPerf.ElapsedMilliseconds >= 20)
                    {
                        ModEntry.Log.Log("[perf] detail-open id=" + entry.Id
                                         + " ms=" + openPerf.ElapsedMilliseconds
                                         + " (render=" + tRender
                                         + " raise=" + (openPerf.ElapsedMilliseconds - tRender) + ")");
                    }

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
            if (_vanillaMachine != null) { _vanillaMachine.Destroy(); _vanillaMachine = null; }
            _machinePageHighlight = null;
            _vanillaVariant = 0;
            _gunModSlots = null;
            _gunModPinned = null;
            _gunModRoll = 0;
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
            var tickPerf = System.Diagnostics.Stopwatch.StartNew();
            long t0 = tickPerf.ElapsedMilliseconds;
            TickTagRolls();

            // 机器静态配方：同一纯度组内轮播不同投料方案（模块也轮播）
            long t1 = tickPerf.ElapsedMilliseconds;
            TickMachineRecipes();

            // 枪械配件：每个改装槽轮播这一槽能装的全部配件
            long t2 = tickPerf.ElapsedMilliseconds;
            TickGunMods();

            // 货单里的容器图标：成分不同 → 瓶身精灵不同，逐张轮播
            long t3 = tickPerf.ElapsedMilliseconds;
            if (_cellAnims.Count == 0)
            {
                LogTickPerf(t0, t1, t2, t3, t3);
                return;
            }

            foreach (var pair in _cellAnims)
            {
                var anim = pair.Value;
                if (anim == null || anim.Image == null) continue;
                if (anim.Sprites == null || anim.Sprites.Count <= 1) continue;

                anim.Index = (anim.Index + 1) % anim.Sprites.Count;
                try { anim.Image.sprite = anim.Sprites[anim.Index]; }
                catch { }
            }

            LogTickPerf(t0, t1, t2, t3, tickPerf.ElapsedMilliseconds);
        }

        /// <summary>每秒轮播的总耗时超过 5ms 就记一行，按环节拆开（偶发卡排查用）。</summary>
        private static void LogTickPerf(long t0, long t1, long t2, long t3, long t4)
        {
            if (t4 < 5 || ModEntry.Log == null) return;

            ModEntry.Log.Log("[perf] detail tick tag=" + (t1 - t0)
                             + " machine=" + (t2 - t1)
                             + " gun=" + (t3 - t2)
                             + " anim=" + (t4 - t3)
                             + " total=" + t4 + "ms");
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

                    // 收购 WATER：格子按水质档位分好，轮播的是格子里装的容器
                    if (string.Equals(tag, "WATER", StringComparison.OrdinalIgnoreCase))
                    {
                        int count = WaterContainerIds().Count;
                        if (count <= 1) continue;

                        int waterIndex;
                        if (!_tagRollIndex.TryGetValue(tag, out waterIndex)) waterIndex = 0;
                        _tagRollIndex[tag] = (waterIndex + 1) % count;
                        stripMoved = true;
                        continue;
                    }

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
        /// 水是特例：客人收「水」看的是**水质**——`willAcceptItem` 谓词限定了能收的档位
        /// （水囤积者只要纯水 / 优质水、拾荒者只要基础水 / 幽灵水……），这里直接写这些档位。
        /// 不能读轮播样本的实例：样本是空瓶，`GetWaterDisplayName` 只会回「未知的水」。
        /// </summary>
        private static string CaptionFor(string tag, string currentId, string[] waterQualities)
        {
            // 内部 `_TAG` 已经展开成物品了，不需要再写一行标签
            if (IsInternalTag(tag)) return null;

            if (!string.Equals(tag, "WATER", StringComparison.OrdinalIgnoreCase))
                return DescribeTags(new[] { tag });

            if (waterQualities != null && waterQualities.Length > 0)
            {
                var names = new List<string>();
                for (int i = 0; i < waterQualities.Length; i++)
                {
                    string name = LiquidManager.WaterQualityName(waterQualities[i]);
                    if (!string.IsNullOrEmpty(name) && !names.Contains(name)) names.Add(name);
                }

                if (names.Count > 0) return string.Join("/", names.ToArray());
            }

            return ModEntry.T("use_tag_water");
        }

        /// <summary>查的是液体条目时，收购标签（WATER 等）就是这种液体 → 整格标绿。</summary>
        private static bool TagMatchesQueriedLiquid(string tag)
        {
            string liquidId = QueryLiquidId();
            return !string.IsNullOrEmpty(liquidId) &&
                   string.Equals(tag, liquidId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 收购 WATER 的格子：按客人承认的水质**分格**（纯水 / 优质水 / 基础水…），
        /// 一格一个水质档位；格子里轮播的是**能装水的容器**（瓶子 / 罐子 / 水壶…），
        /// 下面写该档位。查水时整格标绿，轮播的是容器不是绿底。
        /// </summary>
        private static void RenderWaterQualityCells(CustomUIBuilder builder, TagStrip strip,
                                                    string tag, int tagIndex)
        {
            var qualities = strip.WaterQualities;
            int count = qualities == null || qualities.Length == 0 ? 1 : qualities.Length;

            var containers = WaterContainerIds();

            int roll;
            if (!_tagRollIndex.TryGetValue(tag, out roll)) roll = 0;

            bool queryingWater = TagMatchesQueriedLiquid(tag);

            for (int k = 0; k < count; k++)
            {
                string quality = qualities != null && qualities.Length > 0 ? qualities[k] : null;

                string caption = string.IsNullOrEmpty(quality)
                    ? ModEntry.T("use_tag_water")
                    : LiquidManager.WaterQualityName(quality);

                int cellId = strip.Base + 100 + tagIndex * 8 + k;

                string showId;
                if (containers.Count == 0)
                {
                    showId = LiquidManager.ContainerId;
                }
                else
                {
                    // 每格自己的相位：同一秒各格停在不同的容器上，各自独立轮播
                    int index = ((roll + WaterCellPhase(cellId)) % containers.Count + containers.Count) % containers.Count;
                    showId = containers[index];
                }

                RenderGoods(builder, showId, queryingWater, cellId, false);
                AddOverlayCaption(builder, builder.Last, caption, "tagcapw" + cellId);
            }
        }

        /// <summary>每格自己的轮播相位（由格子 id 稳定算出），各格互不对齐。</summary>
        private static int WaterCellPhase(int cellId)
        {
            unchecked
            {
                uint hash = (uint)cellId * 2654435761u;
                return (int)(hash % 100003u);
            }
        }

        private static List<string> _waterContainerIds;
        private static int _waterContainerGeneration = -1;

        /// <summary>
        /// 客人会按「水」收的容器 id。不能按 WATER 标签找：普通水瓶是**灌水后**
        /// 才被 <c>WaterHelper.UpdateContainerGoodType</c> 标成 WATER，目录里的空样本没有。
        /// 过滤规则照游戏：
        ///   · 必须是液体容器；
        ///   · 排除 <c>WINE_CONTAINER_TAG</c> —— 酒瓶走酒自己的那套，
        ///     <c>WaterFeatureHelper</c> 对它们直接 return，水质从不 exposed，
        ///     客人不会把它当水收；
        ///   · 认水靠 <c>LIQUID_CONTAINER_BASE_PRICE_TAG</c>（UpdateContainerGoodType
        ///     只给带这个标签的容器切换 WATER 类型）。
        /// </summary>
        private static List<string> WaterContainerIds()
        {
            if (_waterContainerIds != null && _waterContainerGeneration == CatalogDataBuilder.Generation)
                return _waterContainerIds;

            _waterContainerGeneration = CatalogDataBuilder.Generation;

            var list = new List<string>();
            var fallback = new List<string>();

            var all = CatalogDataBuilder.All;
            for (int i = 0; i < all.Count; i++)
            {
                var entry = all[i];
                if (entry == null || string.IsNullOrEmpty(entry.Id) || entry.Item == null) continue;
                if (!LiquidManager.IsContainer(entry.Item)) continue;

                fallback.Add(entry.Id);

                bool wine;
                try { wine = entry.Item.IsTag("WINE_CONTAINER_TAG"); }
                catch { wine = false; }
                if (wine) continue;

                bool basePriced;
                try { basePriced = entry.Item.IsTag("LIQUID_CONTAINER_BASE_PRICE_TAG"); }
                catch { basePriced = false; }
                if (!basePriced) continue;

                list.Add(entry.Id);
            }

            return _waterContainerIds = list.Count > 0 ? list : fallback;
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

                // 收购 WATER：按客人承认的水质分格（一格一档）。
                // 不能走 ItemsWithTag —— 普通水容器是**灌水之后**才打 WATER 标签，
                // 目录里的空样本查不到，最后只剩静态带 WATER 类型的瓶装泉水。
                if (string.Equals(tag, "WATER", StringComparison.OrdinalIgnoreCase))
                {
                    RenderWaterQualityCells(builder, strip, tag, i);
                    continue;
                }

                var ids = ItemsWithTag(tag);
                if (strip.Exclude != null && strip.Exclude.Count > 0) ids.RemoveAll(strip.Exclude.Contains);
                if (ids.Count == 0) continue;

                // 内部标记（ADV_GUN_TAG 这类）不写名字，直接展开成具体物品。
                // 展开的每个 id 都要自己做命中判定，否则查一把枪时整条标签都不会变绿。
                if (IsInternalTag(tag))
                {
                    for (int k = 0; k < ids.Count; k++)
                        RenderGoods(builder, ids[k], MatchesCurrent(ids[k]),
                            strip.Base + i * 8 + k, false);
                    continue;
                }

                // 当前物品自己就在这一类里 → 固定显示它；否则按轮播索引取一件。
                // 查液体时同样算命中：查水 → WATER 这一类标绿（液体不是具体物品 id）。
                bool selfId = !string.IsNullOrEmpty(strip.CurrentId) && ids.Contains(strip.CurrentId);
                bool self = selfId || TagMatchesQueriedLiquid(tag);

                string showId;
                if (selfId)
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
                AddOverlayCaption(builder, builder.Last,
                    CaptionFor(tag, strip.CurrentId, strip.WaterQualities), "tagcap" + (strip.Base + i));
            }

            // 具体物品紧接在标签格后面，同一张网格里往下排——列数够时就在同一行，
            // 不会再被拆成上下两块。
            if (strip.Items != null && strip.Items.Length > 0)
            {
                for (int i = 0; i < strip.Items.Length; i++)
                {
                    string id = strip.Items[i];
                    if (string.IsNullOrEmpty(id)) continue;

                    bool match = MatchesCurrent(id);
                    RenderGoods(builder, id, match, strip.Base + 200 + i, false);
                }
            }

            builder.End();

            CustomUIBuilder.RebuildLayout(strip.Host);

            // 局部重建后绿底是新对象，必须立刻对齐；先强制把布局算完，
            // 否则延后到帧末的布局会让绿底停在默认位置（那块大绿方块）。
            FinalizeLayoutAndSyncHighlights(strip.Host);
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

                // 只有真有配方组可画时才建机器页：contentWindow 是 MachineryPanelRow
                // 的物品远不止机器（枪、储物舱、动物笼、远征箱…），没有配方组时整页空白。
                if (MachineGroupCount() > 0)
                {
                    _categories.Add(new Category { Kind = CatMachine });
                }

                AddMachineRecipeCategories();
                AddCraftCategory();
                AddUseCategory(UseIndex.Using(_entry.Id, _entry.Item));
                AddGunUpgradeCategory();
                AddGunModsCategory();
            }
            else
            {
                // ===== 来源方向 =====
                // 卖这件的客人：页面里同时会画他的售出与求购（见 BuildSourceBlocks）
                AddClientCategory(SourceIndex.For(_entry.Id),
                                  SourceIndex.WantedBy(_entry.Id, _entry.Item));
                AddSourceCategory(CatLocation, SourceIndex.For(_entry.Id));

                // 只有真有配方组可画时才建机器页：contentWindow 是 MachineryPanelRow
                // 的物品远不止机器（枪、储物舱、动物笼、远征箱…），没有配方组时整页空白。
                if (MachineGroupCount() > 0)
                {
                    _categories.Add(new Category { Kind = CatMachine });
                }

                AddMachineRecipeCategories();
                AddCraftCategory();
                AddUseCategory(UseIndex.Producing(_entry.Id));
                AddGunUpgradeCategory();

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
                    _entry == null ? "?" : _entry.DisplayName, "paper")
                    .SetDraggable(true)
                    .SetCloseOnEscape(true)   // ESC = Close()：销毁窗口（含演示机器实例），不是只隐藏
                    .SetSize(WinWidth, WindowFixedHeight())
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

            // 整窗拖动：正文容器 + 正文外层的框架 ScrollRect + 窗口根节点各挂一层。
            // 事件从被点中的物体往上找最近的 IDragHandler，正文里有东西挡着时
            // 必须比祖先 ScrollRect 更近才能收到（滚动区里的挂点见 AttachScrollDragHandler）。
            AttachWindowDragHandlers(_window == null ? null : _window.Rect);

            _lastHeight = -1f;   // 新窗口要重新按内容量一次高
            _lastWidth = -1f;
            _pageWidthOverride = 0f;
            _inputBlocker = null;
            _scrollHost = null;  // 窗口重建：旧滚动区已随旧窗口销毁
            _scrollRoot = null;
            _scrollbarWidth = 0f;

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

            var renderPerf = System.Diagnostics.Stopwatch.StartNew();

            BuildCategories();

            long tCats = renderPerf.ElapsedMilliseconds;

            var category = CurrentCategory();
            BuildSourceBlocks(category);
            BuildPagePlan(category);
            ClampPage();

            long tBlocks = renderPerf.ElapsedMilliseconds;

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

                long tChrome = renderPerf.ElapsedMilliseconds;

                // 页面宽度由 RenderContent 内部按需写入（进入时先复位成默认宽）
                RenderContent(manager, category);

                long tContent = renderPerf.ElapsedMilliseconds;

                RenderPager(manager, category);

                long tPager = renderPerf.ElapsedMilliseconds;

                CustomUIBuilder.RebuildLayout(_bodyHost);
                ApplyContentHeight();

                // 布局重建默认延后到帧末；先强制定稿，绿底才能按最终 rect 一次贴准。
                FinalizeLayoutAndSyncHighlights(_bodyHost);

                long tLayout = renderPerf.ElapsedMilliseconds;

                if (ModEntry.Log != null && tLayout >= 20)
                {
                    ModEntry.Log.Log("[perf] detail-render cats=" + tCats
                                     + " blocks=" + (tBlocks - tCats)
                                     + " chrome=" + (tChrome - tBlocks)
                                     + " content=" + (tContent - tChrome)
                                     + " pager=" + (tPager - tContent)
                                     + " layout=" + (tLayout - tPager)
                                     + " total=" + tLayout);
                }
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
            // 高度上让正文吃掉窗口剩余空间：翻页条因此固定在窗口最下面，
            // 不会随内容多少上下浮动。
            var bodyLe = EnsureLayoutElement(_bodyHost);
            bodyLe.flexibleWidth = 1f;
            bodyLe.flexibleHeight = 1f;

            if (_chromeHost != null)
            {
                var le = EnsureLayoutElement(_chromeHost);
                le.flexibleWidth = 1f;
                le.flexibleHeight = 0f;
            }

            if (_contentHost != null)
            {
                var le = EnsureLayoutElement(_contentHost);
                le.flexibleWidth = 1f;
                le.flexibleHeight = 1f;
            }

            if (_pagerHost != null)
            {
                var le = EnsureLayoutElement(_pagerHost);
                le.flexibleWidth = 1f;
                le.flexibleHeight = 0f;
            }
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

            // 机器页放不下时会加宽窗口；先复位成默认宽度，只有本页真的需要才重新写入。
            // 翻页 / 切大类不会走整窗 Render，所以复位必须放在这里——
            // 否则宽机器页的宽度会残留到后面所有页面上。
            _pageWidthOverride = 0f;

            var perf = System.Diagnostics.Stopwatch.StartNew();

            // 离开机器页时把原版面板一起销毁，别留一个挂在已销毁容器上的对象
            if (_vanillaMachine != null && (category == null || category.Kind != CatMachine))
            {
                try { _vanillaMachine.Destroy(); } catch { }
                _vanillaMachine = null;
            }

            // 机器页的面板是借用的工厂窗口：必须先还回窗口，再清容器，
            // 否则 ClearContainer 会把窗口自己的 UI 一起销毁。
            if (_vanillaMachine != null)
            {
                try { _vanillaMachine.RestoreContent(); } catch { }
            }

            // 正文统一放进可滚动区域：内容超过可用高度时出现原版滚动条。
            // ScrollView 只在第一次（或窗口重建后）实例化一次；翻页只清它的 content，
            // 不再每页重建整个滚动条 / Viewport / Mask 子树——这是换页卡顿的大头。
            bool reuseScroll = _scrollHost != null && _scrollHost.IsChildOf(_contentHost);

            // 复用：只清页面内容；否则连旧滚动区一起清掉再重建
            CustomUIBuilder.ClearContainer(reuseScroll ? _scrollHost : _contentHost);

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

            if (reuseScroll)
            {
                _scrollbarWidth = MeasureScrollbarWidth(_scrollHost);
            }
            else
            {
                _scrollHost = null;
                _scrollRoot = null;
                _scrollbarWidth = 0f;

                var scrollBuilder = new CustomUIBuilder(manager, _window, _contentHost);
                scrollBuilder.BeginScrollFlexible(0f);
                _scrollHost = scrollBuilder.CurrentContainer;
                scrollBuilder.End();

                _scrollbarWidth = MeasureScrollbarWidth(_scrollHost);
            }

            // 页面内容直接建在滚动区 content 上；滚动区本身复用
            var builder = new CustomUIBuilder(manager, _window, _scrollHost);

            long tSetup = perf.ElapsedMilliseconds;

            if (category == null)
            {
                builder.AddWarning(Loc("cat_none", "暂未收录"), "cat_empty");
            }
            else
            {
                var type = PageTypeFor(category);
                if (type != null) type.Render(builder, category, _currentSlice);
            }

            long tRender = perf.ElapsedMilliseconds;

            // 滚动区没重建，拖拽挂点仍然在；重建时补挂
            if (!reuseScroll) AttachScrollDragHandler();

            // 内容区嵌在正文列里：高度要先经 _contentHost 传到 _bodyHost，
            // ApplyContentHeight 才能量到本页真实的首选高度（否则窗口停在上一页的尺寸）。
            try
            {
                CustomUIBuilder.RebuildLayout(_contentHost);
                CustomUIBuilder.RebuildLayout(_bodyHost);
                LimitScrollHeight();

                // 复用滚动区时位置会留着上一页的：翻页后回到顶部
                if (reuseScroll && _scrollHost != null)
                {
                    var scroll = _scrollHost.GetComponentInParent<UnityEngine.UI.ScrollRect>(true);
                    if (scroll != null) scroll.verticalNormalizedPosition = 1f;
                }
            }
            catch { }

            perf.Stop();

            if (ModEntry.Log != null)
            {
                ModEntry.Log.Log("[perf] detail kind=" + (category == null ? "none" : CategoryName(category.Kind))
                                 + " page=" + (_page + 1) + "/" + _pagePlan.Count
                                 + " uses=" + (category == null || category.Uses == null ? 0 : category.Uses.Count)
                                 + " ms=" + perf.ElapsedMilliseconds
                                 + " [setup=" + tSetup
                                 + " render=" + (tRender - tSetup)
                                 + " layout=" + (perf.ElapsedMilliseconds - tRender) + "]");
            }
        }

        /// <summary>日志里的页面类型名（排查性能用）。</summary>
        private static string CategoryName(int kind)
        {
            switch (kind)
            {
                case CatClient: return "client";
                case CatLocation: return "location";
                case CatMachine: return "machine";
                case CatCraft: return "craft";
                case CatUse: return "use";
                case CatLoot: return "loot";
                case CatMachineRecipe: return "machine-recipe";
                case CatReaction: return "reaction";
                case CatGunUpgrade: return "gun-upgrade";
                case CatGunMods: return "gun-mods";
                default: return kind.ToString();
            }
        }

        private static RectTransform _scrollHost;   // ScrollRect.content
        private static RectTransform _scrollRoot;   // ScrollView 元素本体（带 LayoutElement）
        private static float _scrollbarWidth;       // 竖向滚动条占掉的宽度（内容排版要扣掉）

        /// <summary>
        /// 把整窗拖动的手柄挂到本页 ScrollRect 的 viewport 上。
        /// 不能挂 ScrollRect 自己那个节点：同一个 GameObject 上有两个 IDragHandler 时
        /// 两个都会收到拖拽（窗口和内容一起动）。viewport 比它更靠近被点中的内容，
        /// 拖拽在 viewport 就停住——滚轮（IScrollHandler）仍然归 ScrollRect。
        /// </summary>
        private static void AttachScrollDragHandler()
        {
            try
            {
                var root = _window == null ? null : _window.Rect;
                var content = _scrollHost;
                if (root == null || content == null) return;

                var scroll = content.GetComponentInParent<UnityEngine.UI.ScrollRect>(true);
                if (scroll == null) return;

                var viewport = scroll.viewport;
                GameObject host = viewport != null && viewport.gameObject != scroll.gameObject
                    ? viewport.gameObject
                    : content.gameObject;

                if (host == null || host.GetComponent<WindowDragHandler>() != null) return;

                var handler = host.AddComponent<WindowDragHandler>();
                handler.Target = root;
            }
            catch (Exception e) { Warn("scroll drag: " + e.Message); }
        }

        /// <summary>
        /// 整窗拖动的手柄布置：正文容器、正文外层的框架 ScrollRect、窗口根节点。
        /// 都是“比会抢拖拽的 ScrollRect 更靠近被点物体”的位置，互不重叠（同一个节点只挂一个）。
        /// </summary>
        private static void AttachWindowDragHandlers(RectTransform root)
        {
            if (root == null) return;

            try
            {
                // 1) 正文容器（翻页条 / 大类行 / 边距都在它里面）
                ExplicitDragHandler(_bodyHost, root);

                // 1.5) 框架内容根：正文以外的窗口留白
                var contentRoot = _window == null ? null : _window.ContentRoot;
                if (contentRoot != null &&
                    contentRoot.GetComponent<UnityEngine.UI.ScrollRect>() == null)
                {
                    ExplicitDragHandler(contentRoot, root);
                }

                // 2) 正文外层若还有框架自带的 ScrollRect，它的 viewport 也要挂
                Transform t = _bodyHost != null ? _bodyHost.parent : root;
                while (t != null)
                {
                    var scroll = t.GetComponent<UnityEngine.UI.ScrollRect>();
                    if (scroll != null)
                    {
                        var viewport = scroll.viewport;
                        Transform host = viewport != null && viewport.gameObject != scroll.gameObject
                            ? viewport
                            : (scroll.content != null ? (Transform)scroll.content : null);
                        ExplicitDragHandler(host, root);
                    }

                    if (t == root) break;
                    t = t.parent;
                }

                // 3) 根节点：原生 UIDragHandler 已经覆盖时不再叠第二个（会双倍位移）
                if (root.GetComponent<UIDragHandler>() == null)
                    ExplicitDragHandler(root, root);
            }
            catch (Exception e) { Warn("window drag: " + e.Message); }
        }

        private static void ExplicitDragHandler(Transform target, RectTransform root)
        {
            if (target == null) return;

            var go = target.gameObject;
            if (go == null || go.GetComponent<WindowDragHandler>() != null) return;

            var handler = go.AddComponent<WindowDragHandler>();
            handler.Target = root;
        }

        /// <summary>竖向滚动条宽度：内容区可用宽必须扣掉它，否则格子会被滚动条压住。</summary>
        private static float MeasureScrollbarWidth(RectTransform content)
        {
            try
            {
                var scroll = content == null ? null : content.GetComponentInParent<UnityEngine.UI.ScrollRect>(true);
                if (scroll == null) return 0f;

                var bar = scroll.verticalScrollbar != null
                    ? scroll.verticalScrollbar
                    : scroll.GetComponentInChildren<UnityEngine.UI.Scrollbar>(true);
                if (bar == null) return 0f;

                var rt = bar.transform as RectTransform;
                if (rt == null) return 0f;

                float w = rt.rect.width;
                if (w <= 0.5f) w = Mathf.Abs(rt.sizeDelta.x);
                if (w <= 0.5f) w = UnityEngine.UI.LayoutUtility.GetPreferredWidth(rt);
                return Mathf.Clamp(w, 0f, 48f);
            }
            catch { return 0f; }
        }

        /// <summary>
        /// 滚动区域高度 = 内容高度，但不超过正文可用高度。
        /// 内容没超高时窗口照常按内容收缩；超高时窗口停在上限、内容内部滚动。
        /// </summary>
        private static void LimitScrollHeight()
        {
            if (_scrollHost == null) return;

            if (_scrollRoot == null)
            {
                // ScrollView 元素本体 = 从 content 的父节点往上第一个带 LayoutElement 的节点
                // （content 自己也可能带 LayoutElement，必须跳过，否则会把内容本身钉死高度）
                var rt = _scrollHost.parent as RectTransform;
                while (rt != null && rt != _contentHost)
                {
                    if (rt.GetComponent<UnityEngine.UI.LayoutElement>() != null) { _scrollRoot = rt; break; }
                    rt = rt.parent as RectTransform;
                }
            }

            if (_scrollRoot == null) return;

            // 滚动区交给布局组吃掉正文剩余高度：内容短时铺满（不留下方空白），
            // 内容超高由 ScrollRect 内部滚动。不能再按像素写死高度——
            // 首帧量到的 _contentHost 高度偏大时会把窗口标题栏挤掉。
            var le = EnsureLayoutElement(_scrollRoot);
            le.minHeight = 0f;
            le.preferredHeight = -1f;
            le.flexibleHeight = 1f;
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

            // 客人 / 地点：每个来源一块，整块一页（货单太长时页面内滚动，不再切到下一页）。
            // 块数取自实际建好的 _sourceBlocks，保证与渲染数据一一对应。
            var sources = OnePerBlock(c => _sourceBlocks.Count, RenderSourceBlock);

            _pageTypes = new Dictionary<int, PageType>
            {
                { CatClient, sources },
                { CatLocation, sources },
                { CatCraft, OnePerBlock(c => c.Recipes == null ? 0 : c.Recipes.Count, RenderRecipeBlock) },
                { CatUse, new PageType { BlockCount = c => UsePageTotal(c), Render = RenderUseBlock } },
                // 掉落：一张地点池一块，整块一页（内容超高时页面内滚动）
                { CatLoot, OnePerBlock(c => c.Loot == null ? 0 : c.Loot.Count, RenderLootBlock) },
                // 机器：一个纯度档一块，整块一页（档内配方自己轮播）
                { CatMachine, OnePerBlock(c => MachineGroupCount(), RenderMachineBlock) },
                // 反查：当前物品是某台机器的投入 / 模块 / 产出
                { CatMachineRecipe, OnePerBlock(c => c.MachineGroups == null ? 0 : c.MachineGroups.Count, RenderMachineRecipeBlock) },
                // 成分反应：一条反应一页（左侧反应物 / 中间条件 / 右侧产物）
                { CatReaction, OnePerBlock(c => c.Reactions == null ? 0 : c.Reactions.Count, RenderReactionBlock) },
                // 枪械翻新：一把枪的一个品相一页（只列可翻新的品相）
                { CatGunUpgrade, OnePerBlock(c => c.GunUpgrades == null ? 0 : c.GunUpgrades.Count, RenderGunUpgradeBlock) },
                // 枪械配件：一把枪一页（原版改装窗口，8 个槽位轮播可装配件）
                { CatGunMods, OnePerBlock(c => c.GunModGunIds == null ? 0 : c.GunModGunIds.Count, RenderGunModsBlock) }
            };
        }

        /// <summary>「一块一页」：块数由数据给出，块内不切片（超高由页面滚动条处理）。</summary>
        private static PageType OnePerBlock(Func<Category, int> blockCount,
                                            Action<CustomUIBuilder, Category, PageSlice> render)
        {
            return new PageType { BlockCount = blockCount, Render = render };
        }

        private static PageType PageTypeFor(Category category)
        {
            if (category == null) return null;

            EnsurePageTypes();

            PageType type;
            return _pageTypes.TryGetValue(category.Kind, out type) ? type : null;
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

            // 一块一页：块内不再切片，内容超高由页面滚动条处理。
            for (int block = 0; block < blocks; block++)
                _pagePlan.Add(new PageSlice { Block = block, Start = 0, Count = 0 });

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
                // 机器类直接写这台机器的名字（和从材料反查时的机器页标题一致），
                // 不再统一写「机器」——一台机器一个分类，写名字更好认。
                case CatMachine: return MachineCategoryTitle(_entry == null ? null : _entry.Id);
                case CatReaction: return Loc("cat_reaction", "反应");
                case CatGunUpgrade: return Loc("cat_gun_upgrade", "枪械翻修");
                case CatGunMods: return GunModCategoryTitle();
                case CatMachineRecipe: return MachineCategoryTitle(category.MachineId);
                default: return "?";
            }
        }

        /// <summary>机器分类标题：机器的显示名；查不到时退回「机器」。</summary>
        private static string MachineCategoryTitle(string machineId)
        {
            var machineEntry = string.IsNullOrEmpty(machineId) ? null : RecipeIndex.FindEntry(machineId);
            if (machineEntry != null && !string.IsNullOrEmpty(machineEntry.DisplayName))
                return machineEntry.DisplayName;

            return Loc("cat_machine", "机器");
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
                FinalizeLayoutAndSyncHighlights(_bodyHost);
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
                FinalizeLayoutAndSyncHighlights(_bodyHost);
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

                // 固定高度：屏幕高度的 2/3（上限仍是 WinMaxHeight），内容超高交给内部滚动
                float height = WindowFixedHeight();

                var pageCategory = CurrentCategory();
                bool machinePage = pageCategory != null && pageCategory.Kind == CatMachine;

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

                float winW = _pageWidthOverride > 0f ? _pageWidthOverride : WinWidth;
                if (Mathf.Abs(height - _lastHeight) > 2f || Mathf.Abs(winW - _lastWidth) > 2f)
                {
                    // 锚点就是窗口自身中心：改高度时上下对称伸缩，
                    // 不再锁顶边。窗口拖到哪，中心就留在哪。
                    _window.SetSize(winW, height);
                    _lastHeight = height;
                    _lastWidth = winW;

                    // 窗口变了尺寸，让内容根再排一次，避免内容按旧尺寸算
                    CustomUIBuilder.RebuildLayout(_window.ContentRoot);
                }

                // 布局完成后量一次容器真宽：比首帧的假定值准。
                // 扣掉 2 × 滚动条宽（内容居中，右侧要留出对称的余量）。
                float w = _bodyHost.rect.width;
                float usable = w - 12f - _scrollbarWidth * 2f;
                if (_pageWidthOverride <= 0f && w > 320f && w < 900f &&
                    Math.Abs(usable - _gridContentWidth) > 8f)
                {
                    _gridContentWidth = usable;
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

        /// <summary>详情窗口的固定高度：屏幕高度的 2/3（上限 WinMaxHeight，下限 WinMinHeight）。</summary>
        private static float WindowFixedHeight()
        {
            float h = Screen.height > 0 ? Screen.height * (2f / 3f) : WinMaxHeight;
            return Mathf.Clamp(h, WinMinHeight, WinMaxHeight);
        }

        private static float MaxContentHeight()
        {
            // 内容区上限跟着固定窗口高度走：窗口外框之外的高度不能再用
            return Mathf.Max(WinMinHeight, WindowFixedHeight() - WindowChrome);
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

                // 一块一页：按整块货单的行数估（超高由滚动条处理，这里只用来兜底）
                int cells = page.Count;
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

        /// <summary>
        /// 强制把布局算完后再对齐绿底。
        /// Unity 的布局重建默认延后到帧末，只调 SyncHighlights 的话拿到的还是回流前的
        /// 旧 rect（绿底只盖住格子一半）；这里在同一个调用里先强制定稿，一次贴准。
        /// </summary>
        private static void FinalizeLayoutAndSyncHighlights(RectTransform host)
        {
            try { UnityEngine.Canvas.ForceUpdateCanvases(); } catch { }
            try { UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(host); } catch { }
            SyncHighlights();
        }

        // ---------------------------------------------------------------- 来源页

        private static void BuildSourceBlocks(Category category)
        {
            _sourceBlocks.Clear();
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
                    bool match = !_asUse && MatchesCurrent(cell.Id, cell.Empty);
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
                    Exclude = exclude,
                    WaterQualities = source.BuysWater
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

                bool match = matchEnabled && MatchesCurrent(ids[i]);
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

            // 原版确实没有对应类型键的极少数标签才需要模组文案；
            // 当前只有 ALL_TYPE 会走到这里（游戏 `type_all_type` 已覆盖），
            // 所以不再保留容器 / 口径的自译分支。

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
        private static void RenderGoods(CustomUIBuilder builder, string id, bool match, int index, bool emptyOnly, List<Sprite> rotate = null, float sideOverride = 0f, ItemEntry overrideEntry = null)
        {
            // 占位 id（random_performance_module 这类）直接换成同族的**默认模块**：
            // 池子里塞的是占位，游戏生成时才掷骰；图鉴展示不做随机实例，
            // 统一显示默认模块的目录条目（图标 / 名字 / 详情都是本体）。
            id = LootSource.ExpandPlaceholder(id);

            // 指定了实例条目（枪械品相格）：图标和可查对象都直接用它，
            // 容器 / 预构建 / 占位那套解析全部跳过——保证和使用页的格子走同一条渲染路径。
            Sprite sprite = overrideEntry == null ? null : CatalogWindow.IconFor(overrideEntry);
            List<Sprite> fulls = null;

            if (sprite == null)
            {
                // 容器：按当前客人算出他卖的这瓶水的图标（见 CatalogWindow.BuildClientVariants）。
                string ignoredComposition;

                if (!emptyOnly && !string.IsNullOrEmpty(SourceSeed))
                    CatalogWindow.BuildClientVariants(id, SourceWater, out ignoredComposition, out fulls);

                // 空瓶格：图集里不带液体段的那张 `<size>`（`normal` / `xl` / `pitcher`…）。
                // 不能用 CompositeIconFor —— 图鉴那份实例是灌过水的，合成出来是满瓶。
                sprite = emptyOnly
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
            }

            var entry = overrideEntry ?? RecipeIndex.FindEntry(id);

            if (overrideEntry == null)
            {
                // 预构建键（portable_water_purifier_used 这类）不在目录里：照抄容器的写法，
                // 给它一条带实例的条目（样本在隔离随机流里生成），后面的 BindCell /
                // 原生提示框链 / 拖拽拿取就全和普通容器格子一样了。
                if (entry == null && PreBuiltItems.IsPreBuilt(id))
                    entry = CatalogWindow.PreBuiltEntry(id);

                // 容器格：可查对象换成它灌装的液体（图鉴的液体条目代表该容器），
                // 判定统一交给液体管理器。
                string contentLiquid = ContainerContentLiquid(entry, id, emptyOnly);
                if (contentLiquid != null)
                {
                    var liquidEntry = LiquidCatalog.Find(LiquidCatalog.EntryPrefix + contentLiquid);
                    if (liquidEntry != null) entry = liquidEntry;
                }
            }

            string elementId = "g" + index;

            bool hasButton = sprite == null;
            if (!hasButton)
            {
                // 物品占用的空间统一是正方形（和图鉴 / 使用页的网格格一致）：
                // 网格会把单元格撑成方形，行内布局则按我们给的尺寸占位——这里直接给方形，
                // 图本身用 preserveAspect 等比缩放居中，两边占位和视觉尺寸就完全一样。
                float side = sideOverride > 0f ? sideOverride : IconSide;
                builder.AddImage(sprite, side, side, elementId);

                var iconImage = builder.Last == null ? null : builder.Last.image;
                if (iconImage != null) iconImage.preserveAspect = true;

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

        /// <summary>
        /// 这个格子的容器里灌着什么液体；空瓶 / 非容器返回 null。
        /// 来源页的货单按客人水种推断，其它地方读实例自己的内容物。
        /// </summary>
        private static string ContainerContentLiquid(ItemEntry entry, string id, bool emptyOnly)
        {
            if (emptyOnly || entry == null) return null;
            if (!LiquidManager.IsContainer(entry.Item)) return null;

            if (CatalogWindow.ClientComposition && !string.IsNullOrEmpty(SourceSeed))
            {
                string water = LiquidManager.WaterFor(SourceWater, id);
                return LiquidManager.IsEmptyWater(water) ? null : LiquidManager.WaterId;
            }

            return LiquidManager.ContentLiquid(entry.Item);
        }

        /// <summary>
        /// 格子的查询对象是不是当前条目（绿底判定）。
        /// - id 精确相等；
        /// - 别名 / 本体等价：满装盒（full_*）、占位（random_*）、状态变体这类
        ///   走 <see cref="LootSource.BaseItemId"/> 解析到同一个本体的，也算同一件；
        /// - 查的是液体条目时，**容器里含有这种液体**就整格命中——判定与机器页
        ///   <c>VanillaMachinePanel.HighlightLiquid</c> 同一条（液体管理器 Parts，
        ///   不要求它是主成分）。垃圾倾倒场里装着水 / 酒精的格子都会变绿。
        /// </summary>
        private static bool MatchesCurrent(string id, bool emptyOnly = false)
        {
            if (string.IsNullOrEmpty(id) || _entry == null || string.IsNullOrEmpty(_entry.Id))
                return false;

            // 同 id / 同一本体的绿底判定统一交给查询管理器
            if (QueryTarget.IsQueried(id)) return true;

            if (!LiquidCatalog.IsLiquidEntry(_entry.Id)) return false;

            var entry = RecipeIndex.FindEntry(id);
            if (entry == null && PreBuiltItems.IsPreBuilt(id)) entry = CatalogWindow.PreBuiltEntry(id);

            var item = entry == null ? null : entry.Item;
            if (item == null) return false;

            string liquidId = _entry.Id.Substring(LiquidCatalog.EntryPrefix.Length);

            // 客人货单格：容器灌的是这个客人的水种（与 BuildClientVariants 同一条推算）——
            // 查水或水里混的杂质（重金属 / 微生物…）时整格变绿。
            // 货单格的实例是空瓶（水只是显示用），所以不能只靠 Parts。
            if (!emptyOnly && !string.IsNullOrEmpty(SourceSeed) && LiquidManager.IsContainer(item))
            {
                string water = LiquidManager.WaterFor(SourceWater, id);
                if (LiquidManager.ContainsLiquid(water, liquidId)) return true;
            }

            // 显示层判据：容器格被换成灌装液体时的那一种（来源页按客人水种推断）
            string content = ContainerContentLiquid(entry, id, emptyOnly);
            if (content != null && string.Equals(content, liquidId, StringComparison.Ordinal)) return true;

            // 成分判据：只要容器里有这种液体就算（不要求它是主成分）
            try { return LiquidManager.Parts(item).ContainsKey(liquidId); }
            catch { return false; }
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

            // 容器格：可查对象换成它灌装的液体（与 RenderGoods 同一规则）
            string contentLiquid = ContainerContentLiquid(entry, id, false);
            if (contentLiquid != null)
            {
                var liquidEntry = LiquidCatalog.Find(LiquidCatalog.EntryPrefix + contentLiquid);
                if (liquidEntry != null) entry = liquidEntry;
            }

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
        /// 枪械翻新：当前物品是枪 → 只列它自己的可翻新品相；
        /// 当前物品是枪械零件 → 列出目录里所有枪（每把 × 每个可翻新品相各一页）。
        /// 全新 / 极佳不可翻新，不作为输入页出现；极佳只作为「良好翻新后」的产物出现。
        /// </summary>
        private static void AddGunUpgradeCategory()
        {
            if (_entry == null) return;

            // 翻修是「拿这件东西去做什么」，只属于用途方向；
            // 来源方向（左键）不该出现，否则枪械零件的来源里会冒出一条翻修关系。
            if (!_asUse) return;

            var pages = new List<GunUpgradePage>();

            if (GunUpgrades.IsGun(_entry.Item))
            {
                AddGunUpgradePages(pages, _entry.Id);
            }
            else if (string.Equals(_entry.Id, "gun_part", StringComparison.Ordinal))
            {
                var guns = GunIds();
                for (int i = 0; i < guns.Count; i++) AddGunUpgradePages(pages, guns[i]);
            }

            if (pages.Count > 0)
                _categories.Add(new Category { Kind = CatGunUpgrade, GunUpgrades = pages });
        }

        private static void AddGunUpgradePages(List<GunUpgradePage> pages, string gunId)
        {
            if (pages == null || string.IsNullOrEmpty(gunId)) return;

            for (int i = 0; i < GunUpgrades.Refurbishable.Length; i++)
            {
                int from = GunUpgrades.Refurbishable[i];
                pages.Add(new GunUpgradePage { GunId = gunId, From = from, To = from - 1 });
            }
        }

        /// <summary>目录里的枪（不含 Unused）：按 id 排序，供枪械零件的翻新清单用。</summary>
        private static List<string> GunIds()
        {
            if (_gunIds != null && _gunIdsGeneration == CatalogDataBuilder.Generation) return _gunIds;

            var ids = new List<string>();

            foreach (var entry in RecipeIndex.AllEntries)
            {
                if (entry == null || entry.Unused || string.IsNullOrEmpty(entry.Id)) continue;
                if (!GunUpgrades.IsGun(entry.Item)) continue;
                ids.Add(entry.Id);
            }

            ids.Sort(StringComparer.OrdinalIgnoreCase);
            _gunIds = ids;
            _gunIdsGeneration = CatalogDataBuilder.Generation;
            return ids;
        }

        private static List<string> _gunIds;
        private static int _gunIdsGeneration = -1;

        /// <summary>
        /// 枪械配件：看枪时列它自己的改装窗口；看配件时列所有能装它的枪。
        /// 和其它「用途」页一样，只在用途方向出现。
        /// </summary>
        private static void AddGunModsCategory()
        {
            if (_entry == null || !_asUse) return;

            var gunIds = new List<string>();

            if (GunMods.IsModdable(QueryTarget.Item))
            {
                gunIds.Add(QueryTarget.Id);
            }
            else if (GunMods.IsMod(QueryTarget.Item))
            {
                var guns = ModdableGunIds();
                for (int i = 0; i < guns.Count; i++)
                    if (GunMods.FitsAnySlot(guns[i], QueryTarget.Id)) gunIds.Add(guns[i]);
            }

            if (gunIds.Count > 0)
                _categories.Add(new Category { Kind = CatGunMods, GunModGunIds = gunIds });
        }

        /// <summary>
        /// 「枪械模组」这个名字直接用原版本地化：配件物品的类型就是 `GUN_MOD`，
        /// 对应类型键 `type_gun_mod`（枪械模组 / Gun Mod / …），不再自己维护一份文案。
        /// </summary>
        private static string GunModCategoryTitle()
        {
            string vanilla = TagDisplayName("GUN_MOD");
            if (!string.IsNullOrEmpty(vanilla) &&
                !string.Equals(vanilla, "GUN_MOD", StringComparison.OrdinalIgnoreCase))
            {
                return vanilla;
            }

            return Loc("cat_gun_mods", "枪械模组");
        }

        /// <summary>能改装的枪（原版非左轮那几把），按 id 排序。</summary>
        private static List<string> ModdableGunIds()
        {
            if (_moddableGunIds != null && _moddableGunIdsGeneration == CatalogDataBuilder.Generation)
                return _moddableGunIds;

            var ids = new List<string>();

            foreach (var entry in RecipeIndex.AllEntries)
            {
                if (entry == null || entry.Unused || string.IsNullOrEmpty(entry.Id)) continue;
                if (!GunMods.IsModdable(entry.Item)) continue;
                ids.Add(entry.Id);
            }

            ids.Sort(StringComparer.OrdinalIgnoreCase);
            _moddableGunIds = ids;
            _moddableGunIdsGeneration = CatalogDataBuilder.Generation;
            return ids;
        }

        private static List<string> _moddableGunIds;
        private static int _moddableGunIdsGeneration = -1;

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
        private static readonly string[] KnownMachines = { "furnace", "blender", "chem_finisher", "evaporator", "hydroponic", "plant_dna_manipulator", "desequencer", "printer" };

        private static void AddMachineRecipeCategories()
        {
            if (_entry == null) return;

            for (int m = 0; m < KnownMachines.Length; m++)
            {
                string machineId = KnownMachines[m];
                if (string.Equals(_entry.Id, machineId, StringComparison.Ordinal)) continue;

                // DNA 编辑器：页序与 DnaManager 的特质顺序一致。
                //   来源方向：已写入的盘（dna:*）直接指向它自己那一页；
                //   用途方向：种子 / 空白盘都是提取的投入，按页匹配（空白盘每一页都要）。
                // 空白盘不能当「产出」匹配（所有写入盘共用本体 id），所以来源方向仍然跳过它。
                if (string.Equals(machineId, "plant_dna_manipulator", StringComparison.Ordinal))
                {
                    var dnaGroups = MachinePanels.RecipesFor(machineId);
                    if (dnaGroups == null || dnaGroups.Length == 0) continue;

                    if (_asUse)
                    {
                        bool blankDisk = string.Equals(_entry.Id, DnaManager.DiskId, StringComparison.Ordinal);
                        var matchedUse = new List<MachinePanels.RecipeGroup>();

                        for (int g = 0; g < dnaGroups.Length; g++)
                        {
                            var group = dnaGroups[g];
                            var recipe = group != null && group.Variants != null && group.Variants.Length > 0
                                ? group.Variants[0] : null;
                            if (recipe == null) continue;

                            if (blankDisk || ContainsId(recipe.Inputs, _entry.Id))
                                matchedUse.Add(group);
                        }

                        if (matchedUse.Count > 0)
                        {
                            _categories.Add(new Category
                            {
                                Kind = CatMachineRecipe,
                                MachineId = machineId,
                                MachineGroups = matchedUse
                            });
                        }

                        continue;
                    }

                    var dnaTrait = DnaManager.TraitForEntry(_entry.Id);
                    if (dnaTrait == null) continue;

                    int page = DnaManager.IndexOf(dnaTrait);
                    if (page >= 0 && page < dnaGroups.Length)
                    {
                        _categories.Add(new Category
                        {
                            Kind = CatMachineRecipe,
                            MachineId = machineId,
                            MachineGroups = new List<MachinePanels.RecipeGroup> { dnaGroups[page] }
                        });
                    }

                    continue;
                }

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
                              || string.Equals(recipe.Chip, _entry.Id, StringComparison.Ordinal)
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
                      || string.Equals(recipe.Chip, _entry.Id, StringComparison.Ordinal)
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
        /// 配方的投入物品里有没有这种液体。
        /// 搅拌机把物品里的成分搅进容器（<c>ChemicalHelper.BlendInto</c> 逐个
        /// <c>CURRENT_PART_*</c> 倒进容器），产物是液体而不是物品，
        /// 所以液体条目反查来源时按**投入物品的成分表**匹配（不只看主成分）。
        /// </summary>
        private static bool InputProducesLiquid(MachinePanels.Recipe recipe, string liquidId)
        {
            if (recipe == null || string.IsNullOrEmpty(liquidId) || recipe.Inputs == null) return false;

            // 成分标签可能还没算过（本页是液体条目时先确保一次）
            LiquidManager.TagItems();

            for (int i = 0; i < recipe.Inputs.Length; i++)
            {
                string id = recipe.Inputs[i];
                if (string.IsNullOrEmpty(id)) continue;

                // 投入本身就是这种液体（蒸发器 / 化学成型机把液体条目写在 Inputs 里）
                if (string.Equals(id, LiquidCatalog.EntryPrefix + liquidId, StringComparison.Ordinal))
                    return true;

                var entry = RecipeIndex.FindEntry(id);
                var item = entry == null ? null : entry.Item;
                if (item == null) continue;

                bool hit = false;
                try { hit = LiquidManager.Parts(item).ContainsKey(liquidId); }
                catch { }
                if (hit) return true;

                // 没有 CURRENT_PART 的物品退回主成分标签（化学产品的有效成分）
                if (string.Equals(entry.LiquidTag, liquidId, StringComparison.Ordinal)) return true;
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
            {
                ExpandCropVariants(merged);
                _categories.Add(new Category { Kind = CatCraft, Recipes = merged });
            }
        }

        /// <summary>
        /// 含品质档作物（小 / 普通 / 大…）的配方按档复制：一档一份，
        /// 材料/产物格子显示对应档的实例。游戏配方只按本体 id 匹配，任何一档都能投料。
        /// 一条配方里出现多种作物时只展开找到的第一种，避免组合爆炸。
        /// </summary>
        private static void ExpandCropVariants(List<RecipeInfo> recipes)
        {
            if (recipes == null) return;

            for (int i = 0; i < recipes.Count; i++)
            {
                var recipe = recipes[i];
                if (recipe == null) continue;

                string cropId = FirstCropId(recipe);
                int tiers = string.IsNullOrEmpty(cropId) ? 0 : CropVariants.Count(cropId);
                if (tiers <= 1) continue;

                recipes.RemoveAt(i);
                for (int v = 0; v < tiers; v++) recipes.Insert(i + v, recipe.CopyForCrop(cropId, v));
                i += tiers - 1;
            }
        }

        /// <summary>配方里出现的第一种带品质档的作物 id（材料 → 工具 → 产物）。</summary>
        private static string FirstCropId(RecipeInfo recipe)
        {
            if (recipe == null) return null;

            string id = FirstCropIn(recipe.Materials);
            if (id == null) id = FirstCropIn(recipe.Tools);
            if (id != null) return id;

            if (recipe.Outputs != null)
            {
                for (int i = 0; i < recipe.Outputs.Count; i++)
                {
                    var output = recipe.Outputs[i];
                    if (output != null && CropVariants.Count(output.Id) > 1) return output.Id;
                }
            }

            return null;
        }

        private static string FirstCropIn(List<RecipeIngredient> ingredients)
        {
            if (ingredients == null) return null;

            for (int i = 0; i < ingredients.Count; i++)
            {
                var ing = ingredients[i];
                if (ing == null || ing.Ids == null) continue;

                for (int k = 0; k < ing.Ids.Count; k++)
                {
                    if (CropVariants.Count(ing.Ids[k]) > 1) return ing.Ids[k];
                }
            }

            return null;
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

                bool match = MatchesCurrent(item.Id);
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
        /// 一条使用记录占几页：内部 `_TAG`（ADV_GUN_TAG / STANDARD_MACHINE_TAG 这类）
        /// 会展开成一整类物品，一格一页；其它记录恒为 1 页。
        /// 当前查询的物品本身就在这一类里时只占 1 页（查哪台机器就只显示哪台，
        /// 不能把整类的机器都摊出来）。
        /// </summary>
        private static int UsePageCount(UseInfo use)
        {
            int pages = 1;
            if (use == null || use.SourceTags == null) return pages;

            string self = QueryTarget.Id;

            for (int i = 0; i < use.SourceTags.Length; i++)
            {
                string tag = use.SourceTags[i];
                if (!IsInternalTag(tag)) continue;

                var ids = ItemsWithTag(tag);
                int count = !string.IsNullOrEmpty(self) && ids.Contains(self) ? 1 : ids.Count;
                pages = Mathf.Max(pages, count);
            }

            return pages;
        }

        private static int UsePageTotal(Category category)
        {
            if (category == null || category.Uses == null) return 0;

            int total = 0;
            for (int i = 0; i < category.Uses.Count; i++)
                total += UsePageCount(category.Uses[i]);

            return total;
        }

        /// <summary>
        /// 使用页里一个标签源在这一页要画的那一件：
        /// 当前查询的物品自身就在这一类里 → 固定显示它（查哪台机器只显示哪台）；
        /// 否则内部 `_TAG` 按页码取第 N 件，其它标签取第一件。
        /// </summary>
        private static string TagSourceItem(string tag, int page)
        {
            var ids = ItemsWithTag(tag);
            if (ids.Count == 0) return null;

            if (!string.IsNullOrEmpty(QueryTarget.Id) && ids.Contains(QueryTarget.Id))
                return QueryTarget.Id;

            if (IsInternalTag(tag))
                return ids[Mathf.Clamp(page, 0, ids.Count - 1)];

            return ids[0];
        }

        private static void RenderUseBlock(CustomUIBuilder builder, Category category, PageSlice slice)
        {
            if (category == null || category.Uses == null) return;

            // Block 是「使用大类里的第几页」，要跨过前面每条记录各自的页数才能落到具体记录
            int page = slice.Block;
            for (int i = 0; i < category.Uses.Count; i++)
            {
                int pages = UsePageCount(category.Uses[i]);
                if (page < pages)
                {
                    RenderUsePage(builder, category.Uses, i, page);
                    return;
                }
                page -= pages;
            }
        }

        /// <summary>
        /// 使用页：一件事一行 —— 哪件东西被怎么用掉、换回了什么。
        /// 例：开箱子 → 内容物；喝掉液体好运 → 空瓶；绷带缠玻璃片 → 玻璃刀。
        /// 布局：来源格子在上，「产物」一行当分界线，产物格子在下——
        /// 方向 / 方式标签都不写（大类标题已经说了这是「使用」）。
        /// 标签源（ADV_GUN_TAG / STANDARD_MACHINE_TAG…）由 <paramref name="tagPage"/>
        /// 指定这一页画这一类里的第几件。
        /// </summary>
        private static void RenderUsePage(CustomUIBuilder builder, List<UseInfo> uses, int useIndex,
                                          int tagPage = 0)
        {
            if (uses == null || useIndex < 0 || useIndex >= uses.Count) return;

            var use = uses[useIndex];
            if (use == null) return;

            var sources = new List<string>(use.Sources ?? new string[0]);

            bool hasProduces = use.Produces != null && use.Produces.Length > 0;
            bool hasTags = use.SourceTags != null && use.SourceTags.Length > 0;

            // 统一走配方行：`来源 + 标签目标 → 产物`。
            // 标签源（ADV_GUN_TAG / STANDARD_MACHINE_TAG…）在 UsePageCount 里已经一格一页，
            // 这一页只取第 tagPage 件，和具体来源用「+」连在同一行里。
            var inputs = new List<FormulaCell>(sources.Count + (hasTags ? use.SourceTags.Length : 0));
            for (int i = 0; i < sources.Count; i++) inputs.Add(Formula(sources[i]));

            if (hasTags)
            {
                for (int i = 0; i < use.SourceTags.Length; i++)
                {
                    string id = TagSourceItem(use.SourceTags[i], tagPage);
                    if (string.IsNullOrEmpty(id)) continue;
                    inputs.Add(Formula(id));
                }
            }

            var outputs = new List<FormulaCell>();
            if (hasProduces)
            {
                for (int i = 0; i < use.Produces.Length; i++)
                    outputs.Add(Formula(use.Produces[i]));
            }

            RenderFormulaRow(builder, inputs, outputs, 600);

            // 非内部标签（`WATER` 这类种类词）仍写一行说明；内部 `_TAG` 会展开成物品，不写名字
            if (hasTags)
            {
                string tagLine = DescribeTags(use.SourceTags);
                if (!string.IsNullOrEmpty(tagLine))
                    builder.AddLabel(tagLine, "use_source_tags");
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

        // ---------------------------------------------------------------- 枪械升级页

        private static void RenderGunUpgradeBlock(CustomUIBuilder builder, Category category, PageSlice slice)
        {
            if (category == null || category.GunUpgrades == null || category.GunUpgrades.Count == 0) return;

            int i = Mathf.Clamp(slice.Block, 0, category.GunUpgrades.Count - 1);
            RenderGunUpgradePage(builder, category.GunUpgrades[i]);
        }

        /// <summary>
        /// 枪械翻修的一页：枪（当前品相）+ 枪械零件 → 枪（上一档品相），
        /// 下面写翻修成功率。只读展示，不提供操作。
        /// </summary>
        private static void RenderGunUpgradePage(CustomUIBuilder builder, GunUpgradePage page)
        {
            if (page == null) return;

            var weapon = RecipeIndex.FindEntry(page.GunId);
            string name = weapon == null ? ItemName(page.GunId) : weapon.DisplayName;
            if (string.IsNullOrEmpty(name)) name = page.GunId;

            builder.AddLabel(name, "gun_up_name");

            // 配方行模版：枪（当前品相）+ 枪械零件 → 枪（上一档品相）。
            var inputs = new List<FormulaCell>
            {
                new FormulaCell
                {
                    Id = page.GunId,
                    Entry = CatalogWindow.GunConditionEntry(page.GunId, page.From),
                    Caption = GunUpgrades.ConditionName(page.From)
                },
                Formula("gun_part")
            };

            var outputs = new List<FormulaCell>
            {
                new FormulaCell
                {
                    Id = page.GunId,
                    Entry = CatalogWindow.GunConditionEntry(page.GunId, page.To),
                    Caption = GunUpgrades.ConditionName(page.To)
                }
            };

            RenderFormulaRow(builder, inputs, outputs, 4190);

            builder.AddLabel(ModEntry.T("gun_up_chance", GunUpgrades.Chance(page.GunId)), "gun_up_chance");
        }

        // ---------------------------------------------------------------- 配方行模版

        /// <summary>
        /// 配方行里的一格：普通物品给 <see cref="Id"/>；需要指定实例（枪械品相格）时给
        /// <see cref="Entry"/>。Caption 是格子右边的小字（品相名），没有就留空。
        /// </summary>
        private sealed class FormulaCell
        {
            internal string Id;
            internal ItemEntry Entry;
            internal string Caption;
        }

        private static FormulaCell Formula(string id, string caption = null)
        {
            return new FormulaCell { Id = id, Caption = caption };
        }

        /// <summary>
        /// 配方行模版：`[投入] + [投入] → [产物]`。
        /// 使用页、枪械翻修页（以及以后同类页面）都走这一份：格宽 / 间距 / 绿底 /
        /// 格子交互完全一致，页面里不要再自己拼 Row。
        /// </summary>
        private static void RenderFormulaRow(CustomUIBuilder builder,
                                             List<FormulaCell> inputs,
                                             List<FormulaCell> outputs,
                                             int elementBase)
        {
            bool hasInputs = inputs != null && inputs.Count > 0;
            bool hasOutputs = outputs != null && outputs.Count > 0;
            if (!hasInputs && !hasOutputs) return;

            // 拆解这类产物按个数重复（shotgun 一次 7 个零件）：一行塞不下时，
            // 输入留着「+」公式行，产物换成可换行的网格，不再让格子挤出窗口。
            int total = (inputs == null ? 0 : inputs.Count) + (outputs == null ? 0 : outputs.Count);
            bool stacked = hasOutputs && total > 6;

            builder.BeginRow(8f, UnityEngine.TextAnchor.MiddleCenter);

            if (hasInputs)
            {
                for (int i = 0; i < inputs.Count; i++)
                {
                    if (i > 0) builder.AddLabel("+", "formula_plus" + (elementBase + i));
                    RenderFormulaCell(builder, inputs[i], elementBase + i);
                }
            }

            if (hasOutputs && !stacked)
            {
                builder.AddLabel("→", "formula_arrow" + elementBase);

                for (int i = 0; i < outputs.Count; i++)
                    RenderFormulaCell(builder, outputs[i], elementBase + 100 + i);
            }

            builder.End();

            if (!stacked) return;

            builder.BeginRow(4f, UnityEngine.TextAnchor.MiddleCenter);
            builder.AddLabel("→", "formula_arrow" + elementBase);
            builder.AddFlexibleSpace();
            builder.End();

            builder.BeginGrid(GridColumns(), GridCellWidth(), CellHeight, CellSpacing);
            for (int i = 0; i < outputs.Count; i++)
                RenderFormulaCell(builder, outputs[i], elementBase + 100 + i);
            builder.End();
        }

        private static void RenderFormulaCell(CustomUIBuilder builder, FormulaCell cell, int elementBase)
        {
            if (cell == null || string.IsNullOrEmpty(cell.Id)) return;

            // 自定义实例（枪械品相格）也走 RenderGoods：
            // 尺寸、绿底、悬停 / 点击交互和使用页的格子完全同一条路径。
            RenderGoods(builder, cell.Id, MatchesCurrent(cell.Id), elementBase, false, null, CellWidth, cell.Entry);

            if (!string.IsNullOrEmpty(cell.Caption))
                builder.AddLabel(cell.Caption, "formula_cap" + elementBase);
        }

        // ---------------------------------------------------------------- 枪械配件页

        private static List<string>[] _gunModSlots;
        private static string[] _gunModPinned;
        private static int _gunModRoll;

        private static void RenderGunModsBlock(CustomUIBuilder builder, Category category, PageSlice slice)
        {
            if (category == null || category.GunModGunIds == null || category.GunModGunIds.Count == 0) return;

            int i = Mathf.Clamp(slice.Block, 0, category.GunModGunIds.Count - 1);
            RenderGunModsPage(builder, category.GunModGunIds[i]);
        }

        private static void RenderGunModsPage(CustomUIBuilder builder, string gunId)
        {
            if (_entry == null || string.IsNullOrEmpty(gunId)) return;

            _gunModSlots = BuildGunModSlots(gunId);
            _gunModPinned = BuildGunModPinned(gunId);

            // 直接用机器页模板：左边保留分类名（原版「枪械模组」），右上角只放枪图标
            RenderMachinePageTemplate(
                builder,
                GunModCategoryTitle(),
                gunId,
                null,
                null,
                delegate (VanillaMachinePanel panel) { return panel.BuildGunModShell(); },
                // 量尺寸前先用每个槽位里最大的配件占位：轮播小件时面板不缩
                delegate (VanillaMachinePanel panel) { FillGunModLargest(); },
                0f,
                delegate (VanillaMachinePanel panel)
                {
                    _gunModRoll = 0;
                    FillGunModSlots();
                },
                delegate (VanillaMachinePanel panel)
                {
                    // 从配件反查进来时高亮配件自己；看枪时高亮枪
                    bool fromMod = GunMods.IsMod(QueryTarget.Item);
                    panel.HighlightItem(fromMod ? QueryTarget.Id : gunId);
                });
        }

        private static List<string>[] BuildGunModSlots(string gunId)
        {
            var slots = new List<string>[GunMods.SlotCount];
            for (int i = 0; i < slots.Length; i++) slots[i] = GunMods.Compatible(gunId, i);
            return slots;
        }

        /// <summary>
        /// 从配件反查时，把它固定在自己能装的槽位里（不参与轮播），
        /// 否则轮播到别的配件就看不到这件东西了。
        /// </summary>
        private static string[] BuildGunModPinned(string gunId)
        {
            var pinned = new string[GunMods.SlotCount];
            if (!QueryTarget.Active || !GunMods.IsMod(QueryTarget.Item)) return pinned;

            for (int i = 0; i < pinned.Length; i++)
            {
                var list = _gunModSlots == null ? null : _gunModSlots[i];
                if (list != null && list.Contains(QueryTarget.Id)) pinned[i] = QueryTarget.Id;
            }

            return pinned;
        }

        /// <summary>每个槽位先用体积最大的那件配件占位（量尺寸用）。</summary>
        private static void FillGunModLargest()
        {
            if (_vanillaMachine == null || _gunModSlots == null) return;

            var ids = new string[_gunModSlots.Length];
            for (int i = 0; i < ids.Length; i++) ids[i] = LargestMod(_gunModSlots[i]);

            _vanillaMachine.FillGunMods(ids);
        }

        /// <summary>按精灵面积挑最大的一件：面板尺寸以它为基准，轮播小件时不会再变。</summary>
        private static string LargestMod(List<string> ids)
        {
            if (ids == null || ids.Count == 0) return null;

            string best = ids[0];
            float bestArea = -1f;

            for (int i = 0; i < ids.Count; i++)
            {
                var entry = RecipeIndex.FindEntry(ids[i]);
                var sprite = CatalogWindow.IconFor(entry);
                float area = sprite == null ? 0f : sprite.rect.width * sprite.rect.height;

                if (area > bestArea)
                {
                    bestArea = area;
                    best = ids[i];
                }
            }

            return best;
        }

        /// <summary>按当前轮播位置把 8 个槽填上配件；每个槽各自偏移一格，画面不会整排同步。</summary>
        private static void FillGunModSlots()
        {
            if (_vanillaMachine == null || _gunModSlots == null) return;

            var ids = new string[_gunModSlots.Length];

            for (int i = 0; i < ids.Length; i++)
            {
                // 从配件反查进来：这一槽固定显示正在查的配件，不参与轮播
                if (_gunModPinned != null && !string.IsNullOrEmpty(_gunModPinned[i]))
                {
                    ids[i] = _gunModPinned[i];
                    continue;
                }

                var list = _gunModSlots[i];
                if (list == null || list.Count == 0) continue;

                int index = ((_gunModRoll + i) % list.Count + list.Count) % list.Count;
                ids[i] = list[index];
            }

            _vanillaMachine.FillGunMods(ids);
        }

        /// <summary>每秒推进一次枪械配件槽的轮播。</summary>
        private static void TickGunMods()
        {
            if (!IsOpen || _vanillaMachine == null || _gunModSlots == null) return;

            var category = CurrentCategory();
            if (category == null || category.Kind != CatGunMods) return;

            _gunModRoll++;
            FillGunModSlots();
            RefreshMachinePageHighlights();   // 重填会清掉旧绿底，必须补回来
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

                builder.AddLabel("<u>" + LiquidManager.NameOf(component.Id) + "</u>",
                                 idPrefix + "comp_name" + i);
                var nameLabel = builder.Last;
                SetCompositionFont(nameLabel);
                AttachLiquidLookup(nameLabel, component.Id);

                // 和物品一致：只有当前正在检索的那种液体才垫绿底
                if (string.Equals(component.Id, QueryLiquidId(), StringComparison.Ordinal))
                    AddLiquidNameBackground(builder, nameLabel);

                // Summary 行只写名字（化学产品的详细成分不适合逐种列出）
                if (!component.Summary)
                {
                    int lo = component.Ranged ? component.Min : component.Amount;
                    int hi = component.Ranged ? component.Max : component.Amount;

                    double loPct = component.Total > 0 ? lo * 100.0 / component.Total : 0.0;
                    double hiPct = component.Total > 0 ? hi * 100.0 / component.Total : 0.0;

                    string amount = lo == hi
                        ? WaterScannerUIManager.FormatAmount(lo)
                        : WaterScannerUIManager.FormatAmount(lo) + "~" + WaterScannerUIManager.FormatAmount(hi);

                    string percent = lo == hi
                        ? loPct.ToString("0.##")
                        : loPct.ToString("0.##") + "~" + hiPct.ToString("0.##");

                    builder.AddLabel(": " + amount + " (" + percent + "%)", idPrefix + "comp_value" + i);
                    SetCompositionFont(builder.Last);
                }

                builder.End();
            }
        }

        /// <summary>
        /// 水培机「一个周期后」：在液体管理器读到的实际内容物上按配方消耗量扣。
        /// 水按 ml（<see cref="MachinePanels.Recipe.WaterUse"/>，机器调 <c>WaterHelper.RemoveMl</c>），
        /// 矿物按绝对量 UL（机器调 <c>RemoveContaminantFromContainer(…, mineralUseRate, …)</c>）。
        /// 两者都是 1/1000 ml，乘 1000 换成水用的 ml。
        /// </summary>
        private static List<LiquidManager.Component> HydroAfterCycle(
            List<LiquidManager.Component> before, MachinePanels.Recipe recipe)
        {
            var after = new List<LiquidManager.Component>();
            if (before == null || before.Count == 0 || recipe == null) return after;

            int water = 0, mineral = 0, other = 0;
            for (int i = 0; i < before.Count; i++)
            {
                var component = before[i];
                if (string.Equals(component.Id, LiquidManager.WaterId, StringComparison.Ordinal))
                    water += component.Amount;
                else if (string.Equals(component.Id, "mineral", StringComparison.Ordinal))
                    mineral += component.Amount;
                else
                    other += component.Amount;
            }

            int waterAfter = Mathf.Max(0, water - recipe.WaterUse * 1000);   // WaterUse 是 ml
            int mineralAfter = Mathf.Max(0, mineral - recipe.MineralUse);     // MineralUse 是 UL

            int total = waterAfter + mineralAfter + other;

            if (waterAfter > 0)
            {
                after.Add(new LiquidManager.Component
                {
                    Id = LiquidManager.WaterId,
                    Name = LiquidManager.NameOf(LiquidManager.WaterId),
                    Amount = waterAfter,
                    Total = total
                });
            }

            if (mineralAfter > 0)
            {
                after.Add(new LiquidManager.Component
                {
                    Id = "mineral",
                    Name = LiquidManager.NameOf("mineral"),
                    Amount = mineralAfter,
                    Total = total
                });
            }

            // 其它成分（水培正常只有水和矿物）原样保留，只更新总量
            for (int i = 0; i < before.Count; i++)
            {
                var component = before[i];
                if (string.Equals(component.Id, LiquidManager.WaterId, StringComparison.Ordinal)) continue;
                if (string.Equals(component.Id, "mineral", StringComparison.Ordinal)) continue;

                component.Total = total;
                after.Add(component);
            }

            return after;
        }

        /// <summary>水培页面板下方要预留的高度：成分变化（前/后）+ 净水块 + 生长条件。</summary>
        private static float HydroExtraHeight(MachinePanels.Recipe recipe)
        {
            float height = 96f;

            int lines = PurifyLineCount(recipe);
            if (lines > 0) height += (lines + 1) * 20f + 6f;   // 标题 + 每行一种污染物

            return height;
        }

        /// <summary>净水块会画几行（不含标题）；植物没有 purifying 特性时返回 0。</summary>
        private static int PurifyLineCount(MachinePanels.Recipe recipe)
        {
            if (recipe == null || recipe.Inputs == null || recipe.Inputs.Length == 0) return 0;

            var seedEntry = RecipeIndex.FindEntry(recipe.Inputs[0]);
            var seed = seedEntry == null ? null : seedEntry.Item;
            if (seed == null) return 0;

            int rank = 0;
            try { rank = PlantGeneticsHelper.GetTraitRank(seed, "purifying"); } catch { }
            if (rank <= 0) return 0;

            int count = 0;
            for (int i = 0; i < PurifyPollutants.Length; i++)
            {
                try
                {
                    var dict = Liquid.LiquidDict;
                    if (dict != null && dict.ContainsKey(PurifyPollutants[i])) count++;
                }
                catch { }
            }

            return count;
        }

        /// <summary>
        /// 游戏水质系统实际使用的污染物（<c>WaterPremadeHelper</c> 的各张分布表里的键）。
        /// 净水特性只清这几种里可被过滤器移除的；<c>mineral</c> 不在其中（矿物质会被保留）。
        /// </summary>
        private static readonly string[] PurifyPollutants =
        {
            "heavy_metal", "organic_waste", "microplastic", "microbe",
            "chemical_contaminant", "physical_contaminant"
        };

        /// <summary>
        /// 水培「净水」：<c>purifying</c> 特性每周期清掉每种污染物的一定比例，
        /// 但会停在各自的下限（<see cref="PlantWaterQuality.PurifyingFloor"/>）。
        /// 这里把下限按**百分比**写出来，每行一种污染物。
        /// </summary>
        private static void RenderPurifyEffect(CustomUIBuilder builder, MachinePanels.Recipe recipe)
        {
            if (builder == null || recipe == null) return;
            if (recipe.Inputs == null || recipe.Inputs.Length == 0) return;

            var seedEntry = RecipeIndex.FindEntry(recipe.Inputs[0]);
            var seed = seedEntry == null ? null : seedEntry.Item;
            if (seed == null) return;

            int rank = 0;
            try { rank = PlantGeneticsHelper.GetTraitRank(seed, "purifying"); } catch { }
            if (rank <= 0) return;

            var lines = new List<string>();
            for (int i = 0; i < PurifyPollutants.Length; i++)
            {
                string id = PurifyPollutants[i];

                Liquid liquid = null;
                try
                {
                    var dict = Liquid.LiquidDict;
                    if (dict != null) dict.TryGetValue(id, out liquid);
                }
                catch { }
                if (liquid == null) continue;

                int floorBp = 0;   // 保留底线，万分比（10000 = 100%）
                try { floorBp = PlantWaterQuality.PurifyingFloor(liquid, rank); } catch { }
                if (floorBp < 0) floorBp = 0;

                string percent = (floorBp / 100.0).ToString("0.##") + "%";
                lines.Add(LiquidManager.NameOf(id) + "：" + percent);
            }

            if (lines.Count == 0) return;

            builder.BeginColumn(2f, UnityEngine.TextAnchor.UpperLeft);
            builder.AddLabel(Loc("mach_hydro_purify", "净水"), "hydro_purify");
            SetCompositionFont(builder.Last);

            for (int i = 0; i < lines.Count; i++)
            {
                builder.AddLabel(lines[i], "hydro_purify_" + i);
                SetCompositionFont(builder.Last);
            }
            builder.End();
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
                var entry = all[i];
                var item = entry != null ? entry.Item : null;
                if (item == null || entry.Unused) continue;   // 游戏里标成 unused 的不参与标签展开

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

                // 一律先问游戏自己的物品类型表（`type_weapon` / `type_food` /
                // `type_household_good` / `type_document` …），取不到再原样显示。
                // 尸体标签（SPECIES_HUMAN / SPECIES_XENO）在游戏里不可达：
                // 战斗未实装、不会有尸体，uses.csv 里也没有对应记录。
                names.Add(TagDisplayName(tag));
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
        /// 规则是 `type_` + 小写。取不到（`ADV_GUN_TAG` 这种）就原样返回。
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
            RenderMachineGroup(builder, machineId, groups[gi], gi, false);
        }

        private static void RenderMachineRecipeBlock(CustomUIBuilder builder, Category category, PageSlice slice)
        {
            if (category == null || category.MachineGroups == null ||
                category.MachineGroups.Count == 0) return;

            int gi = Mathf.Clamp(slice.Block, 0, category.MachineGroups.Count - 1);
            RenderMachineGroup(builder, category.MachineId, category.MachineGroups[gi], gi, true);
        }

        /// <summary>标题行右上角主体图标的边长：机器页放机器、枪械配件页放枪，共用这一个值。</summary>
        private const float HeaderSubjectSide = 52f;

        /// <summary>
        /// 标题行右上角的主体图标（机器 / 枪）。尺寸与绿底判定统一在这里：
        /// 绿底只看「它是不是当前被查的物品」，不再由各页面自己传 highlight 标记。
        /// </summary>
        private static void AppendHeaderSubject(CustomUIBuilder builder, string id, string name, int elementBase)
        {
            if (builder == null || string.IsNullOrEmpty(id)) return;

            builder.AddFlexibleSpace();
            if (!string.IsNullOrEmpty(name)) builder.AddLabel(name, "header_subject_name");
            RenderGoods(builder, id, MatchesCurrent(id), elementBase, false, null, HeaderSubjectSide);
        }

        /// <summary>
        /// 机器页模版：标题行（左标题 + 右上角主体图标）→ 可选附加行（质量需求）→
        /// 原版机器面板（按可用高度缩放、居中、挂进占位容器）→ 填面板。
        /// 机器页和枪械配件页都走这一份；面板尺寸 / 定位 / 挂载只在这里维护。
        /// </summary>
        /// <returns>面板是否建好并填完。</returns>
        private static bool RenderMachinePageTemplate(
            CustomUIBuilder builder,
            string title,
            string subjectId,
            string subjectName,
            Action extraHeader,
            Func<VanillaMachinePanel, bool> buildShell,
            Action<VanillaMachinePanel> beforeMeasure,
            float belowHeight,
            Action<VanillaMachinePanel> fill,
            Action<VanillaMachinePanel> highlight)
        {
            if (builder == null || string.IsNullOrEmpty(subjectId)) return false;

            var mtPerf = System.Diagnostics.Stopwatch.StartNew();

            // 标题行：左标题（可空），右上角主体图标（机器 / 枪），统一入口
            builder.BeginRow(6f, UnityEngine.TextAnchor.MiddleCenter);
            _machineTopRow = builder.CurrentContainer;   // 实测高度用
            if (!string.IsNullOrEmpty(title)) builder.AddLabel(title, "mach_group");
            AppendHeaderSubject(builder, subjectId, subjectName, 3180);
            builder.End();

            if (extraHeader != null) extraHeader();

            long tHeader = mtPerf.ElapsedMilliseconds;

            if (_vanillaMachine != null) { _vanillaMachine.Destroy(); _vanillaMachine = null; }
            _vanillaMachine = new VanillaMachinePanel();

            if (buildShell == null || !buildShell(_vanillaMachine))
            {
                if (ModEntry.Log != null) ModEntry.Log.Warning("[machine-page] build failed: " + subjectId);
                _vanillaMachine.Destroy();
                _vanillaMachine = null;
                return false;
            }

            long tShell = mtPerf.ElapsedMilliseconds;

            // 量尺寸前先预填（枪械配件页用「每槽最大件」占位，保证轮播时面板不缩）
            if (beforeMeasure != null) beforeMeasure(_vanillaMachine);

            // 面板上限与普通页共用 MaxContentHeight()；面板下方还有内容时先扣掉那部分高度
            float panelMaxH = MaxMachinePanelHeight() - Mathf.Max(0f, belowHeight);

            // 宽度按「内容里最宽的那一块」定，其余面板在同等宽度内自动换行。
            // 内容居中，扣 2 × 滚动条宽才不会被右侧滚动条压住。
            float defaultW = WinWidth - 40f - _scrollbarWidth * 2f;
            float panelW = _vanillaMachine.FitWidth(_bodyHost, defaultW);
            _pageWidthOverride = panelW > defaultW + 1f ? panelW + 40f + _scrollbarWidth * 2f : 0f;

            builder.BeginColumn(2f, UnityEngine.TextAnchor.MiddleCenter);
            var host = builder.CurrentContainer;
            float panelH = Mathf.Max(1f, _vanillaMachine.ScaledSize(_bodyHost).y);
            builder.End();

            // host 在 bodyHost 的 VerticalLayoutGroup 下，childControlWidth/Height=true，
            // 宽高都取自己的 LayoutElement；不设宽度只有默认 100px（面板中线会贴左边缘）。
            if (host != null)
            {
                var hostLayout = EnsureLayoutElement(host);
                hostLayout.preferredWidth = panelW;
                hostLayout.flexibleWidth = 1f;
                hostLayout.minHeight = panelH;
                hostLayout.preferredHeight = panelH;
                hostLayout.flexibleHeight = 0f;
            }

            // 把面板挂进占位容器、相对 host 正中心绝对定位，再缩放补偿 Canvas 差异；
            // 布局跑一次后塞物品（否则 inventory inner rect 还是 0，物品会浮到格子外）。
            long tFit = mtPerf.ElapsedMilliseconds;

            var rect = _vanillaMachine.AttachContentTo(host);
            if (rect != null) CustomUIBuilder.RebuildLayout(rect);

            long tAttach = mtPerf.ElapsedMilliseconds;

            // 用**实测内容高度**（不是 rect 高度）修正占位与 host：
            // rect 含底部留白，按它给高度会让内容下方空一段、翻页条被推远。
            float visualH = _vanillaMachine.MatchGameCanvasScale(host);
            if (visualH > 10f && host != null)
            {
                var hostLe = EnsureLayoutElement(host);
                hostLe.minHeight = visualH;
                hostLe.preferredHeight = visualH;
                hostLe.flexibleHeight = 0f;
            }

            long tScale = mtPerf.ElapsedMilliseconds;

            if (fill != null) fill(_vanillaMachine);

            long tFill = mtPerf.ElapsedMilliseconds;

            // 放入样本后再量一次宽度：空槽和装满的槽宽度不同（水培的水 / 种子槽、
            // 模块网格等都会随内容变宽），预填前算出的列数会偏多或偏少——
            // 换行与页面宽度必须以**放进物品后的实际宽度**为准。
            float filledW = _vanillaMachine.FitWidth(_bodyHost, defaultW);
            if (Mathf.Abs(filledW - panelW) > 0.5f)
            {
                panelW = filledW;
                _pageWidthOverride = panelW > defaultW + 1f ? panelW + 40f + _scrollbarWidth * 2f : 0f;

                if (host != null)
                {
                    var widthLe = EnsureLayoutElement(host);
                    widthLe.preferredWidth = panelW;
                    widthLe.flexibleWidth = 1f;
                }

                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[machine-page] refit after fill " + subjectId
                                     + " panelW=" + panelW.ToString("F1")
                                     + " override=" + _pageWidthOverride.ToString("F1"));
            }

            // 填完后再按实际内容校正一次高度与居中（物品会改变面板行尺寸）
            float filledH = _vanillaMachine.MatchGameCanvasScale(host);
            if (filledH > 10f && host != null)
            {
                var heightLe = EnsureLayoutElement(host);
                heightLe.minHeight = filledH;
                heightLe.preferredHeight = filledH;
                heightLe.flexibleHeight = 0f;
            }

            long tRefit = mtPerf.ElapsedMilliseconds;

            if (ModEntry.Log != null)
            {
                ModEntry.Log.Log("[machine-page] " + subjectId
                                 + " panelH=" + panelH + " visualH=" + visualH
                                 + " filledH=" + filledH
                                 + " panelW=" + panelW.ToString("F1")
                                 + " rect=" + (rect == null ? "null" : rect.rect.size.ToString()));

                if (mtPerf.ElapsedMilliseconds >= 30)
                {
                    ModEntry.Log.Log("[perf] machine-template " + subjectId
                                     + " ms=" + mtPerf.ElapsedMilliseconds
                                     + " [header=" + tHeader
                                     + " shell=" + (tShell - tHeader)
                                     + " fit=" + (tFit - tShell)
                                     + " attach=" + (tAttach - tFit)
                                     + " scale=" + (tScale - tAttach)
                                     + " fill=" + (tFill - tScale)
                                     + " refit=" + (tRefit - tFill)
                                     + " highlight=" + (mtPerf.ElapsedMilliseconds - tRefit) + "]");
                }
            }

            // 每次填完都会清掉旧绿底（ClearSamples → ClearMatchHighlights），
            // 所以高亮回调要在 fill 之后跑；轮播时由 RefreshMachinePageHighlights 再跑一次。
            _machinePageHighlight = highlight;
            RefreshMachinePageHighlights();

            return true;
        }

        /// <summary>当前机器页 / 配件页的高亮回调（填完面板后必须重跑，否则绿底被清掉）。</summary>
        private static Action<VanillaMachinePanel> _machinePageHighlight;

        /// <summary>重新给面板上的物品垫绿底。轮播换物品后调用。</summary>
        private static void RefreshMachinePageHighlights()
        {
            if (_vanillaMachine == null || _machinePageHighlight == null) return;

            try { _machinePageHighlight(_vanillaMachine); }
            catch (Exception e) { Warn("machine highlight: " + e.Message); }
        }

        /// <summary>
        /// 各机器只显示哪些面板（沿用原来手写壳的取舍，不把无关框加进来）。
        /// 返回 null = 这台机器没有既定取舍，保留所有有角色的面板。
        /// </summary>
        private static string[] MachineRoles(string machineId)
        {
            switch (machineId)
            {
                case "furnace": return new[] { "module", "input", "output" };
                case "blender": return new[] { "input", "container" };
                case "chem_finisher": return new[] { "container", "output" };
                case "evaporator": return new[] { "container", "distillate" };
                case "hydroponic": return new[] { "module", "seed", "water", "output" };
                case "desequencer": return new[] { "chip", "input", "output" };
                case "printer": return new[] { "module", "input", "chip", "output" };
                case "purifier": return new[] { "module", "container" };
                case "projector": return new[] { "module" };
                default: return null;
            }
        }

        /// <summary>
        /// 机器页外壳：统一借用工厂造好的原版窗口（含模组对面板的修改）。
        /// 借用不到时才退回手写壳——手写壳只作为老版本 / 非标准窗口的兜底。
        /// </summary>
        private static bool BuildMachineShell(VanillaMachinePanel panel, string machineId)
        {
            if (panel == null || string.IsNullOrEmpty(machineId)) return false;

            // DNA 编辑器带特性盘 / 数据盘等与「提取」无关的面板，
            // 按用户要求只显示种子 / 空白盘 / 产出，继续用手写壳。
            if (machineId == "plant_dna_manipulator") return panel.BuildDnaManipulatorShell();

            string[] roles = MachineRoles(machineId);

            // 演示用机器必须是**独立实例**：不能借用图鉴条目里的 Item，
            // 否则 Adopt/隐藏面板/放宽过滤器会改到图鉴自己的那份实例，
            // 关窗或翻页时还会留下挂载状态。每次现建一个，随 VanillaMachinePanel 一起销毁。
            var shellPerf = System.Diagnostics.Stopwatch.StartNew();
            var demo = UI.CatalogWindow.CreatePreviewItem(machineId);
            long tCreate = shellPerf.ElapsedMilliseconds;

            bool adopted = demo != null && panel.AdoptOwned(demo, roles);

            if (ModEntry.Log != null && shellPerf.ElapsedMilliseconds >= 20)
            {
                ModEntry.Log.Log("[perf] machine-shell " + machineId
                                 + " create=" + tCreate
                                 + " adopt=" + (shellPerf.ElapsedMilliseconds - tCreate)
                                 + " total=" + shellPerf.ElapsedMilliseconds);
            }

            if (adopted) return true;
            if (demo != null)
            {
                try { demo.Destroy(); } catch { }
            }

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[machine-page] " + machineId + ": adopt unavailable, using hand-written shell");

            if (machineId == "furnace") return panel.BuildFurnaceShell();
            if (machineId == "blender") return panel.BuildBlenderShell();
            if (machineId == "chem_finisher") return panel.BuildChemFinisherShell();
            if (machineId == "evaporator") return panel.BuildEvaporatorShell();
            if (machineId == "hydroponic") return panel.BuildHydroponicShell();
            if (machineId == "desequencer") return panel.BuildDesequencerShell();
            if (machineId == "printer") return panel.BuildPrinterShell();

            return false;
        }

        private static void RenderMachineGroup(CustomUIBuilder builder, string machineId,
                                               MachinePanels.RecipeGroup group, int groupIndex,
                                               bool filterVariants)
        {
            if (_entry == null) return;
            if (string.IsNullOrEmpty(machineId)) return;
            if (group == null) return;

            var variants = VariantsFor(group, filterVariants);
            if (variants == null || variants.Length == 0) return;

            if (_vanillaVariant >= variants.Length) _vanillaVariant = 0;
            var recipe = variants[_vanillaVariant];
            if (recipe == null) return;

            // 面板下方还要留的高度：搅拌机 / 化学制剂成型机 / 蒸发器的成分，水培机的变化说明
            float below = 0f;
            if (HasMachineComposition(machineId))
                below = MachineCompositionHeight(recipe);
            else if (string.Equals(machineId, "hydroponic", StringComparison.Ordinal))
                below = HydroExtraHeight(recipe);

            _machineQualityLabel = null;
            _machineQualityRow = null;

            bool built = RenderMachinePageTemplate(
                builder,
                MachineRecipeTitle(group, recipe),
                machineId,
                null,
                // 质量需求行：居中放在主体图标下面；不需要质量时整行不画
                delegate
                {
                    if (string.IsNullOrEmpty(group.Purity)) return;

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
                },
                delegate (VanillaMachinePanel panel) { return BuildMachineShell(panel, machineId); },
                null,
                below,
                delegate (VanillaMachinePanel panel)
                {
                    FillVanillaMachine(recipe, group.Purity);
                    if (string.Equals(machineId, "plant_dna_manipulator", StringComparison.Ordinal))
                        panel.FillDnaBlankDisk();
                },
                delegate (VanillaMachinePanel panel)
                {
                    panel.HighlightItem(_entry == null ? null : _entry.Id);
                    // 查询的是液体条目时，装着它的容器槽标绿（成分由液体管理器判定）
                    panel.HighlightLiquid(QueryLiquidId());
                });

            if (!built) return;

            // 搅拌机 / 化学制剂成型机 / 蒸发器：面板下面按成分检测器的格式列出液体成分（一行一种 + 总体积）
            if (HasMachineComposition(machineId))
            {
                // 成分随品质变化的化学产品（药）只写总量 + 主要成分名
                bool summaryOnly = IsVariableCompositionDrug(recipe);
                var components = LiquidManager.Components(_vanillaMachine, false,
                    summaryOnly, summaryOnly ? MainIngredientName(recipe) : null);
                if (components.Count > 0)
                {
                    builder.BeginColumn(2f, UnityEngine.TextAnchor.UpperLeft);
                    RenderLiquidComposition(builder, components);
                    builder.End();
                }
            }

            // 水培机：概率产物标黄底 + 提示框写概率；面板下面写一个周期的成分变化
            if (string.Equals(machineId, "hydroponic", StringComparison.Ordinal))
            {
                // 水槽按最佳养分浓度重灌（养分等级是万分比：矿物占水量的 1.5%）
                _vanillaMachine.FillHydroponicWater(recipe.NutrientPercent);

                // 变化前：直接读水槽实际灌进去的液体（液体管理器，单位 UL，
                // 与容器提示框的总体积同一口径，不再自己按容量换算）
                var before = LiquidManager.Components(_vanillaMachine, false);

                // 标黄放在灌水之后：灌水会更新容器样本，别让高亮被后续重建盖掉
                _vanillaMachine.MarkUncertain(recipe.Outputs, recipe.Uncertain, recipe.UncertainNote);

                builder.BeginRow(12f, UnityEngine.TextAnchor.UpperLeft);

                builder.BeginColumn(2f, UnityEngine.TextAnchor.UpperLeft);
                builder.AddLabel(Loc("mach_hydro_before", "变化前"), "hydro_before");
                SetCompositionFont(builder.Last);
                RenderLiquidComposition(builder, before, "hydro_before_");
                builder.End();

                builder.AddFlexibleSpace();

                builder.BeginColumn(2f, UnityEngine.TextAnchor.UpperLeft);
                builder.AddLabel(Loc("mach_hydro_after", "一个周期后"), "hydro_after");
                SetCompositionFont(builder.Last);
                RenderLiquidComposition(builder, HydroAfterCycle(before, recipe), "hydro_after_");
                builder.End();

                builder.End();

                // 倒数第二块：净水（purifying）——每种污染物一行，写它最终能降到的百分比
                RenderPurifyEffect(builder, recipe);

                // 最后一行：矿物质最佳浓度 + 污染物影响
                string growNote = Loc(recipe.NoteKey, recipe.NoteFallback);
                if (!string.IsNullOrEmpty(growNote))
                {
                    builder.BeginColumn(2f, UnityEngine.TextAnchor.UpperLeft);
                    builder.AddLabel(growNote, "mach_note");
                    if (builder.Last != null && builder.Last.text != null)
                    {
                        builder.Last.text.fontSize = 13f;
                        builder.Last.text.alignment = TextAlignmentOptions.Left;
                    }
                    builder.End();
                }
            }

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
                bool isHeatingPlate = _entry != null &&
                                      string.Equals(_entry.Id, "heating_plate", StringComparison.Ordinal);

                builder.BeginRow(6f, UnityEngine.TextAnchor.MiddleCenter);
                builder.AddFlexibleSpace();
                RenderGoods(builder, "heating_plate", isHeatingPlate, 3190, false, null, 52f);
                builder.End();
            }

            if (_vanillaMachine != null) { _vanillaMachine.Destroy(); _vanillaMachine = null; }
            _vanillaMachine = new VanillaMachinePanel();

            if (!_vanillaMachine.BuildReactionShell(ReactionConditionLines(reaction), QueryLiquidId()))
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
            float panelH = Mathf.Max(1f, _vanillaMachine.ScaledSize(_bodyHost).y);
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

            float visualH = _vanillaMachine.MatchGameCanvasScale(host);
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
            RenderLiquidComposition(builder, LiquidManager.Components(_vanillaMachine),
                                    "react_in_");
            builder.End();

            builder.AddFlexibleSpace();

            builder.BeginColumn(2f, UnityEngine.TextAnchor.UpperLeft);
            if (reaction.Products == null || reaction.Products.Length == 0)
            {
                // 无实物产物：容器保持空，底下写残留的溶剂；没有溶剂 / 只是催化剂就写「无」
                // （催化剂不参与产物，量也不进产物容器）
                string label;
                string liquidId = null;

                if (!string.IsNullOrEmpty(reaction.Solvent) && !reaction.SolventIsCatalyst)
                {
                    label = ConditionLiquidName(reaction.Solvent);
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
                RenderLiquidComposition(builder, LiquidManager.Components(_vanillaMachine, true),
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
                lines.Add(label + " " + ConditionLiquidName(reaction.Solvent));
            }

            if (!string.IsNullOrEmpty(reaction.ConditionKey) ||
                !string.IsNullOrEmpty(reaction.ConditionFallback))
            {
                string condition = string.IsNullOrEmpty(reaction.ConditionKey)
                    ? reaction.ConditionFallback
                    : Loc(reaction.ConditionKey, reaction.ConditionFallback);

                // 条件里的液体名与绝对数字都由代码提供：
                //   液体名走液体管理器（占位符 {0}/{1}），
                //   阈值取 LiquidReactions 的规则常量，本地化文件里不再写死 5:1 / 10% / 40% / 100 °C。
                var args = new List<object>();
                var liquidArgs = ConditionLiquidArgs(reaction);
                if (liquidArgs != null)
                {
                    for (int i = 0; i < liquidArgs.Length; i++)
                        args.Add(ConditionLiquidName(liquidArgs[i]));
                }

                switch (reaction.ConditionKey)
                {
                    case "react_cond_solvent_ratio":
                        args.Add(LiquidReactions.SolventRatioParts);
                        args.Add(LiquidReactions.SolventRatioBase);
                        break;
                    case "react_cond_nomore_alcohol":
                        args.Add(LiquidReactions.NoMoreAlcoholPercent);
                        break;
                    case "react_cond_alcohol_ratio":
                        args.Add(LiquidReactions.AlcoholKillPercent);
                        break;
                    case "react_cond_waste_full":
                    case "react_cond_microbe_heat":
                        args.Add(LiquidReactions.FullReactionTemp);
                        break;
                }

                if (args.Count > 0)
                {
                    try { condition = string.Format(condition, args.ToArray()); } catch { }
                }

                if (!string.IsNullOrEmpty(condition)) lines.Add(condition);
            }

            return lines.ToArray();
        }

        /// <summary>条件文本里提到的液体（对应 {0}/{1} 占位符），名字由液体管理器提供。</summary>
        private static string[] ConditionLiquidArgs(LiquidReaction reaction)
        {
            return reaction == null ? null : reaction.ConditionLiquidIds;
        }

        /// <summary>
        /// 条件里的液体名：link（可点击）+ 下划线，名字由液体管理器提供。
        /// 绿底由 ConditionLinkHandler 单独画，避免富文本底纹改掉字体颜色。
        /// </summary>
        private static string ConditionLiquidName(string liquidId)
        {
            return "<link=\"" + LiquidCatalog.EntryPrefix + liquidId + "\">"
                   + "<u>" + LiquidManager.NameOf(liquidId) + "</u>"
                   + "</link>";
        }

        /// <summary>给成分行里当前检索的液体名垫一块绿底（和物品高亮同一个通道）。</summary>
        private static void AddLiquidNameBackground(CustomUIBuilder builder, CustomUIElement label)
        {
            var cell = label == null ? null : label.Rect;
            if (cell == null || cell.parent == null) return;

            try
            {
                var go = new GameObject("liquidNameMark",
                    typeof(RectTransform), typeof(UnityEngine.UI.Image));
                go.transform.SetParent(cell.parent, false);
                go.transform.SetSiblingIndex(cell.GetSiblingIndex());

                var image = go.GetComponent<UnityEngine.UI.Image>();
                image.sprite = null;   // 无 sprite 的 Image 直接画纯色块，最稳
                image.color = GameColors.HighlightGreen;
                image.raycastTarget = false;

                var rect = go.GetComponent<RectTransform>();
                rect.sizeDelta = Vector2.zero;

                var ignore = go.AddComponent<UnityEngine.UI.LayoutElement>();
                ignore.ignoreLayout = true;

                // 布局组会反复改标签的锚点/尺寸，用每帧跟随保证底纹贴着文字
                var follow = go.AddComponent<RectFollower>();
                follow.Target = cell;
            }
            catch { }
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
                                 recipe.Container, recipe.ContainerLiquid, recipe.ContainerVolume,
                                 recipe.OutputItem, recipe.InputItem, recipe.Chip);
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
                // 高亮回调由机器页模板统一保存：重填会清掉旧绿底，统一补回来
                RefreshMachinePageHighlights();
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

            // 品质档作物：同一物品的每档各占一页，标题带「（小 / 普通 / 大）」
            string cropSuffix = group.TitleSuffix ?? string.Empty;

            // 搅拌机：一物品一页，标题直接用该物品的显示名
            if (!string.IsNullOrEmpty(group.TitleItemId))
            {
                var entry = RecipeIndex.FindEntry(group.TitleItemId);
                if (entry != null && !string.IsNullOrEmpty(entry.DisplayName)) return entry.DisplayName + cropSuffix;

                string name = ItemName(group.TitleItemId);
                if (!string.IsNullOrEmpty(name)) return name + cropSuffix;
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

            return Loc(group.TitleKey, group.TitleFallback) + cropSuffix;
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

        private static void RenderRecipe(CustomUIBuilder builder, RecipeInfo recipe)
        {
            builder.AddLabel(ModEntry.T("rec_mats"), "rec_mats");
            builder.BeginRow(8f);
            RenderIngredients(builder, recipe.Materials, "m", recipe);
            builder.AddFlexibleSpace();
            builder.End();

            if (recipe.Tools.Count > 0)
            {
                builder.AddLabel(ModEntry.T("rec_tools"), "rec_tools");
                builder.BeginRow(8f);
                RenderIngredients(builder, recipe.Tools, "t", recipe);
                builder.AddFlexibleSpace();
                builder.End();
            }

            builder.AddLabel(ModEntry.T("rec_result"), "rec_result");
            builder.BeginRow(8f);
            RenderOutputs(builder, recipe.Outputs, recipe);
            builder.AddFlexibleSpace();
            builder.End();
        }

        /// <summary>
        /// 这一格是不是「按品质档展示的作物」：是就返回对应档的实例条目，否则 null。
        /// 副本只指定一种作物，其余格子仍走普通解析。
        /// </summary>
        private static ItemEntry CropCellEntry(RecipeInfo recipe, string id)
        {
            if (recipe == null || recipe.DisplayCropVariant < 0) return null;
            if (string.IsNullOrEmpty(id)) return null;

            if (!string.Equals(id, recipe.DisplayCropId, StringComparison.Ordinal)) return null;

            return CatalogWindow.CropVariantEntry(id, recipe.DisplayCropVariant);
        }

        private static void RenderIngredients(CustomUIBuilder builder, List<RecipeIngredient> ingredients,
                                              string prefix, RecipeInfo recipe = null)
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
                // 由液体管理器统一换成一只装了该液体的普通容器展示（图鉴的液体条目代表它），
                // 格子可点、可查、不再是没有图标的文字。
                string liquidId = LiquidManager.LiquidFor(ing);
                string id = LiquidManager.Represent(ing) ?? FirstId(ing);

                var liquidEntry = liquidId == null
                    ? null
                    : LiquidCatalog.Find(LiquidCatalog.EntryPrefix + liquidId);

                // 品质档作物：这一份副本专门展示该档，格子用对应档的实例（图标 + 悬停名）
                var cropEntry = liquidEntry == null ? CropCellEntry(recipe, id) : null;

                var sprite = cropEntry != null
                    ? CatalogWindow.IconFor(cropEntry)
                    : (liquidEntry != null
                        ? CatalogWindow.IconFor(liquidEntry)
                        : (id == null ? null : CatalogWindow.IconFor(id)));

                var entry = cropEntry ?? (liquidEntry ?? RecipeIndex.FindEntry(id));
                if (entry == null && id != null && PreBuiltItems.IsPreBuilt(id))
                    entry = CatalogWindow.PreBuiltEntry(id);

                if (sprite != null)
                {
                    float w, h;
                    CatalogWindow.FitIcon(sprite, IconSide, out w, out h);
                    builder.AddImage(sprite, w, h, prefix + i);
                }
                else
                {
                    builder.AddButton(Shorten(ing.DisplayName), CellSelect(entry), prefix + i);
                }

                CatalogWindow.BindCell(builder.Last, entry, sprite == null);
                builder.AddLabel("×" + ing.Amount, prefix + i + "_n");
            }
        }

        private static void RenderOutputs(CustomUIBuilder builder, List<RecipeOutput> outputs, RecipeInfo recipe = null)
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

                // 品质档作物的副本：产物格也按该档展示
                var cropEntry = CropCellEntry(recipe, output.Id);

                var entry = cropEntry ?? RecipeIndex.FindEntry(output.Id);
                var sprite = cropEntry != null
                    ? CatalogWindow.IconFor(cropEntry)
                    : CatalogWindow.IconFor(output.Id);

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

                if (cropEntry != null) CatalogWindow.BindCell(builder.Last, cropEntry, sprite == null);
                else BindGoods(builder, output.Id, sprite == null);

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
