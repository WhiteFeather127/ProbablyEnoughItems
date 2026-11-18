using System;
using System.Collections.Generic;
using System.Reflection;
using ProbablyEnoughItems.Api;
using ItemCatalog.Catalog;
using UnityEngine;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 图鉴面板：停靠在物品栏右侧，上下贴屏幕、右端贴屏幕，内部按可用尺寸自适应每页物品数量。
    /// 阶段一只做全物品展示（分页图标网格），搜索与详情在后续版本加入。
    /// </summary>
    internal static class CatalogWindow
    {
        private const string WinId = ModEntry.MOD_ID + ".catalog";
        /// <summary>面板所在画布层，固定为 paper（与原版纸条同层），不再开放配置。</summary>
        private const string Layer = "paper";   // CustomUIManager.LAYER_PAPER

        // 网格单元与页面留白（像素）
        private const float CellWidth = 66f;
        private const float CellHeight = 66f;
        private const float CellSpacing = 6f;
        private const float PaddingX = 26f;      // 左右内边距合计
        private const float ReservedY = 208f;    // 顶部复选框/搜索框/计数/提示 + 分隔线 + 底部翻页行
        private const float IconMaxSide = 48f;   // 图标最长边（等比缩放后）

        private const float MinPanelWidth = 260f;

        private static CustomUIWindow _window;
        private static RectTransform _gridHost;
        private static RectTransform _inventoryRect;
        private static int _page;

        /// <summary>整窗输入挡板（透明 Image + 空 TreeNodeRender），见 EnsureInputBlocker。</summary>
        private static GameObject _inputBlocker;

        private static int _columns = 6;
        private static int _rows = 7;
        private static int _perPage = 42;

        /// <summary>「拿取物品」复选框：开启后点击格子会把物品放进背包，而不是选中查看。</summary>
        private static bool _takeMode;

        // ---- 拖拽取用 ----
        private const float DragStartPixels = 14f;    // 超过这个位移才算拖拽，避免误触

        private static ItemEntry _pressEntry;
        private static Vector2 _pressPos;
        private static bool _pressArmed;
        private static float _suppressClickUntil;     // 拖拽结束后短暂屏蔽点击，避免又取一件

        // 预构建键（PreBuiltItemHelper）的合成条目缓存：Id → 带实例的 ItemEntry
        private static readonly Dictionary<string, ItemEntry> _preBuiltEntries =
            new Dictionary<string, ItemEntry>(StringComparer.Ordinal);

        private static bool _dragging;
        private static ItemEntry _dragEntry;
        private static GameItem _dragItem;       // 拖拽中的实体物品（先放进 dummy 容器）
        private static GameObject _ghost;
        private static RectTransform _ghostRect;
        private static int _dragLogs;
        private static bool _vanillaDragStarted;   // 原版 StartDrag() 是否已成功交出去
        private static int _vanillaDragAttempts;   // 为了等渲染节点，重试了几帧
        private static bool _zonesLogged;

        private static readonly Dictionary<string, Sprite> SpriteCache = new Dictionary<string, Sprite>();

        private static int _spriteHits;
        private static int _spriteMisses;
        private static int _spriteLogged;
        private static bool _spriteSummaryLogged;
        private static int _clickFailures;
        private static TooltipManager _tooltipManager;
        /// <summary>原版物品提示框的字符宽度（游戏常量，60）。</summary>
        private static int TooltipCharWidth
        {
            get { return ItemTooltipHandler.BASE_TOOLTIP_CHAR_WIDTH; }
        }

        /// <summary>
        /// 与 ItemTooltipHandler 一致的宽度：机器类物品会乘上 MACHINE_TOOLTIP_WIDTH_MULT。
        /// 判定条件照抄原版（FEED_DISPENSER_TAG / MACHINE 类型 / MACHINE_MANUAL_TAG）。
        /// </summary>
        private static int TooltipWidthFor(GameItem item)
        {
            int width = ItemTooltipHandler.BASE_TOOLTIP_CHAR_WIDTH;
            if (item == null) return width;

            bool machine = false;
            try
            {
                machine = item.IsGameItemType("MACHINE")
                          || item.IsTag("MACHINE_MANUAL_TAG")
                          || item.IsTag("FEED_DISPENSER_TAG");
            }
            catch { }

            if (!machine) return width;

            return Mathf.RoundToInt(width * ItemTooltipHandler.MACHINE_TOOLTIP_WIDTH_MULT);
        }

        private static string _search = string.Empty;

        private static ItemEntry _lastRichEntry;
        private static RichText _lastRich;
        private static int _tooltipLogs;          // 成功日志计数
        private static int _tooltipFailLogs;      // 失败日志计数，与成功分开
        private static readonly HashSet<string> _tooltipFailKeys =
            new HashSet<string>(StringComparer.Ordinal);
        private static GameItemElement _probeElement;
        private static string _probeId;
        private static int _sampleLogged;
        /// <summary>
        /// 原生 tooltip 链依赖运行时机器窗口、图鉴预览实例给不了的条目。
        /// 命中后用「不含运行时标签」的探针重跑原版 GetTooltip()。
        /// </summary>
        private static readonly HashSet<string> _tooltipDegraded =
            new HashSet<string>(StringComparer.Ordinal);
        /// <summary>GameItem.contentWindow 的私有 backing field（setter 非 public）。</summary>
        private static FieldInfo _contentWindowField;

        // ---- 页面级自动刷新（不依赖悬停） ----
        private const float PageRefreshSeconds = 1f;   // 每节拍间隔
        private static readonly List<ItemEntry> _pageEntries = new List<ItemEntry>();
        private static readonly Dictionary<string, UnityEngine.UI.Image> _pageImages =
            new Dictionary<string, UnityEngine.UI.Image>();
        private static float _pageRefreshNextAt;
        private static string _hoveredEntryId;      // 存的是 entry.Key（变体格子带 #index）
        private static int _refreshLogs;

        /// <summary>
        /// 详情窗口（以及其它外部窗口）里的物品格子。拖拽起手靠 _hoveredEntryId 反查 entry，
        /// 只查 _pageEntries 会漏掉这些格子，所以单独登记一份。
        /// </summary>
        private static readonly Dictionary<string, ItemEntry> _externalCells =
            new Dictionary<string, ItemEntry>(StringComparer.Ordinal);

        // ---- 硬编码变体表（自动探测已删除，展开只认下表 + 接口提供者） ----

        /// <summary>模块效果的完整清单（唯一来源：<see cref="CatalogVariantKit"/>，同时开放给第三方）。</summary>
        private static readonly string[] ModuleEffectIds = CatalogVariantKit.ModuleEffectIds;

        private const string LearningEffectId = CatalogVariantKit.LearningEffectId;

        /// <summary>学习进度上限 = 基础属性 × 该倍率（对应 ModuleEffectHelper.LEARNING_ALGO_MAX_MULT）。</summary>
        private const int LearningMaxMult = 2;

        /// <summary>模块精灵所在图集。</summary>
        private const string ModuleAtlas = "Items/items_module";

        /// <summary>
        /// 浆果形态。取值来自 InsBerryUI.OnInit 的分支（condition -2..2 共 5 档，
        /// 其中 -1 档再按 visual 分成 未熟/干瘪(0,1) 与 过熟(2)）。
        /// </summary>
        /// <summary>浆果形态表（唯一来源：<see cref="CatalogVariantKit"/>）。</summary>
        private static readonly BerryFormInfo[] BerryForms = CatalogVariantKit.BerryForms;

        /// <summary>占格形状内的格子顺序（唯一来源：<see cref="CatalogVariantKit"/>）。</summary>
        private static readonly ModuleEffectCellInfo[] ModuleEffectCells =
            CatalogVariantKit.ModuleEffectCells;

        /// <summary>
        /// 占格方式 = 精灵（形状由精灵图像算出：BuildShapeFromSprite）。
        /// 后缀按图集里模块精灵的命名规律枚举，实际存在的才用。
        /// </summary>
        private static readonly string[] ModuleShapeSuffixes =
        {
            "",
            "_c",
            "_l",
            "_t",
            "_2",
            "_2_h",
            "_2_s",
            "_3",
            "_4",
            "_5"
        };

        /// <summary>id → 该模块实际存在的占格精灵（按上表顺序）。</summary>
        private static readonly Dictionary<string, List<string>> _moduleShapes =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private static readonly List<ItemEntry> _flat = new List<ItemEntry>();
        private static int _flatGeneration = -1;

        /// <summary>id → 未展开的原始条目（变体格子要借它取基础物品与元数据）。</summary>
        private static readonly Dictionary<string, ItemEntry> _baseById =
            new Dictionary<string, ItemEntry>(StringComparer.Ordinal);
        private static int _cellFallbackWarns;




        /// <summary>模组注册的变体提供者的播放游标。</summary>
        private static readonly Dictionary<string, int> _providerCursor =
            new Dictionary<string, int>(StringComparer.Ordinal);

        private static int _liquidFillWarns;



        /// <summary>预览专用的独立随机源，避免重掷推进存档的随机流。</summary>
        // 非 readonly：每次生成后要把 RNG.random 的新位置存回来，否则序列不推进
        private static System.Random _previewRng = new System.Random(20261105);

        // 游戏的 RNG.* 走 UnityEngine.Random，所以预览流也用它，只是单独保存一份 State 反复推进
        private static UnityEngine.Random.State _previewState;
        private static bool _previewStateReady;
        private static bool _overlayShown;
        private static int _overlayWarned;

        /// <summary>
        /// 面板贴靠的对象。谁按的按钮就贴谁：物品栏窗口那枚贴物品栏，
        /// 店铺底部那排那枚贴底部那排。为 null 时走原来的自动解析。
        /// </summary>
        internal static RectTransform AnchorOverride;

        internal static void Toggle()
        {
            var manager = CustomUIManager.Instance;
            if (manager == null)
            {
                Warn("CustomUIManager is not ready yet");
                return;
            }

            _inventoryRect = AnchorOverride != null ? AnchorOverride : AnchorTarget.Resolve();

            if (_window != null && _window.IsAlive)
            {
                if (_window.IsVisible)
                {
                    HideNativeTooltip();
                    _window.Hide();
                    return;
                }

                _window.Show();
                _window.BringToFront();

                // 打开面板属于「界面变了」的事件点：顺手让按钮重新认一次目标
                try { LauncherButton.Refresh(); } catch { }
                return;
            }

            Create();

            try { LauncherButton.Refresh(); } catch { }
        }

        /// <summary>数据重建后按当前布局重新打开。</summary>
        internal static void Reopen()
        {
            if (_window != null && _window.IsAlive) _window.Close();
            _window = null;
            _inputBlocker = null;
            _gridHost = null;
            SpriteCache.Clear();

            if (CustomUIManager.Instance != null) Create();
        }

        /// <summary>
        /// 游戏加载完成后一次性做完所有准备：建表、预解析图标、展开变体格子。
        /// 之后没有「加载中」状态——按钮始终可点，打开图鉴不需要再做任何准备。
        /// 必须在 ResetForScene 之后调用（后者会清空各种缓存）。
        /// </summary>
        internal static void Warmup()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                if (CatalogDataBuilder.HasNewItems()) CatalogDataBuilder.Build(true);
                else CatalogDataBuilder.EnsureBuilt();
            }
            catch (Exception e)
            {
                Warn("warmup: build data failed: " + e.Message);
                return;
            }

            long buildMs = watch.ElapsedMilliseconds;

            // 搅拌机可搅拌清单：手写表优先，BLENDABLE_TAG 自动补齐（后加载的模组物品也能收录）
            try { MachinePanels.DiscoverBlenderInputs(); }
            catch (Exception e) { Warn("warmup: blender discovery failed: " + e.Message); }

            // 熔炉：探测当前版本/补丁实际接受的输入与模块，校验档位覆盖
            try { MachinePanels.DiscoverFurnaceRecipes(); }
            catch (Exception e) { Warn("warmup: furnace discovery failed: " + e.Message); }

            // 变体来源只有接口提供者与硬编码生成器表，预热阶段不做任何探测。
            var items = CatalogDataBuilder.Visible;
            bool uiReady = CustomUIManager.Instance != null;

            // 图标：只在 UI 管理器就绪时预热，避免把 null 写进缓存
            int sprites = 0;
            if (uiReady)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var entry = items[i];
                    if (entry != null && GetSprite(entry) != null) sprites++;
                }
            }

            // 变体格子：一次性展开、建实例、预解析图标，
            // 否则第一次翻到那些页时才现场工厂调用 + 解析贴图，会卡一下。
            int cells = 0;
            int missingItem = 0;
            int missingSprite = 0;

            try
            {
                var flat = FlatEntries();
                cells = flat.Count;

                for (int i = 0; i < flat.Count; i++)
                {
                    var cell = flat[i];
                    if (cell == null) continue;

                    if (cell.VariantIndex >= 0 && uiReady) EnsureCellItem(cell);

                    if (cell.Item == null) missingItem++;

                    if (uiReady)
                    {
                        var sprite = GetSprite(cell);
                        if (sprite == null) missingSprite++;
                        else if (cell.VariantIndex >= 0) sprites++;
                    }
                }
            }
            catch (Exception e)
            {
                Warn("warmup: expand variants failed: " + e.Message);
            }

            watch.Stop();

            if (ModEntry.Log == null) return;

            ModEntry.Log.Log(string.Format(
                "[warmup] items={0} cells={1} sprites={2} build={3}ms total={4}ms",
                items.Count, cells, sprites, buildMs, watch.ElapsedMilliseconds));

            // 完整性自检：目录里的条目能不能都造出实例、能不能都取到图标
            bool complete = missingItem == 0 && missingSprite == 0 && CatalogDataBuilder.FailedCount == 0;

            // 配方索引跟物品表一起在加载阶段建好，打开合成表时不用现扫
            try { RecipeIndex.Build(); }
            catch (Exception e) { Warn("warmup: recipe index failed: " + e.Message); }

            // 获取途径索引（配给站 / 博士 / 客人）同样一次建好
            try { SourceIndex.Build(); }
            catch (Exception e) { Warn("warmup: source index failed: " + e.Message); }

            ModEntry.Log.Log(string.Format(
                "[verify] dirs={0} total={1} visible={2} hidden={3} failed={4} extra={5} | cells={6} missingItem={7} missingSprite={8} -> {9}",
                CatalogDataBuilder.DirectoryCount,
                CatalogDataBuilder.All.Count,
                items.Count,
                CatalogDataBuilder.All.Count - items.Count,
                CatalogDataBuilder.FailedCount,
                CatalogDataBuilder.ExtraCount,
                cells, missingItem, missingSprite,
                complete ? "OK" : "INCOMPLETE"));
        }

        /// <summary>
        /// 只补解析图标（不做建表、不展开变体格子）。
        /// 运行中有模组新注册了物品时调一次，避免玩家第一次翻到那一页时现加载贴图。
        /// 已解析过的条目在 SpriteCache 里命中，开销只是一次字典查询。
        /// </summary>
        internal static void WarmupSpritesOnly()
        {
            if (CustomUIManager.Instance == null) return;

            try
            {
                var items = CatalogDataBuilder.Visible;
                for (int i = 0; i < items.Count; i++)
                {
                    var entry = items[i];
                    if (entry != null) GetSprite(entry);
                }
            }
            catch (Exception e)
            {
                Warn("warmup sprites: " + e.Message);
            }
        }

        /// <summary>场景切换后窗口会随场景销毁，句柄必须作废。</summary>
        /// <summary>指针是否压在图鉴面板上（拖拽删除要用）。</summary>
        internal static bool IsPointerOverPanel(Vector2 screenPoint)
        {
            if (_window == null || !_window.IsAlive || !_window.IsVisible) return false;

            var rect = _window.Rect;
            if (rect == null) return false;

            try
            {
                return UnityEngine.RectTransformUtility.RectangleContainsScreenPoint(rect, screenPoint, null);
            }
            catch { return false; }
        }

        /// <summary>
        /// 从某个界面上的图鉴按钮打开：面板贴到那枚按钮绑定的对象上
        /// （物品栏窗口 / 店铺底部那排 UI）。两枚按钮共用同一个窗口，只是锚点不同。
        /// </summary>
        internal static void OpenAt(RectTransform anchor)
        {
            AnchorOverride = anchor;
            Toggle();
        }

        internal static void ResetForScene()
        {
            HideNativeTooltip();   // 切场景/重建前先把悬停提示框收掉

            // 换场景后物品实例全换了：重建目录，顺带让配方 / 来源索引一起失效重算
            try { Catalog.CatalogDataBuilder.Build(true); } catch { }

            AnchorOverride = null;   // 旧场景的对象已销毁，别拿它算锚点
            _dropMark = null;        // 红底随窗口一起没了
            _groupMarks.Clear();     // 多选红底挂在物品节点上，同样随场景销毁
            _groupMarkKeys.Clear();

            // 合成贴图与舞台随场景销毁（物品实例都没了，缓存全部失效）
            _compositeSprites.Clear();
            _compositeByCell.Clear();
            DestroyCompositeStage();
            LiquidCatalog.Reset();

            _window = null;
            _inputBlocker = null;
            _gridHost = null;
            SpriteCache.Clear();
            _tooltipManager = null;
            _lastRichEntry = null;
            _lastRich = null;
            // 变体来源（接口提供者 / 硬编码生成器表）与场景无关，这里无需清理。
            _pageEntries.Clear();
            _pageImages.Clear();

            // 分帧建格/刷新是跨帧状态，切场景必须断掉（旧 builder 的容器已随场景销毁）
            _building = false;
            _buildBuilder = null;
            _buildItems = null;
            _refreshing = false;
            _refreshCursor = 0;
            _cellOverlays.Clear();
            _pageRefreshNextAt = 0f;

            // 「这一格会不会变」的判定结果按 id 缓存，但变体提供者可能在任意时刻注册或撤销，
            // 缓存的前提（会话内不变）不成立。场景切换后一律重判，
            // 否则先前判成「非动态」的 id 即使后来注册了提供者也不会轮播。
            _dynamicCellCache.Clear();

            DestroyProbe();
        }

        private static void Create()
        {
            // 目录里出现了上次没有的 id（后加载的模组、动态注册）。
            // 绝大多数情况只是多出几件物品，增量补进去即可；
            // 整表重建要重造全部物品（1.5~1.9 秒），那正是首次打开卡顿的来源。
            if (CatalogDataBuilder.HasNewItems())
            {
                bool appended = false;
                string reason = null;

                try { appended = CatalogDataBuilder.TryAppendNewItems(out reason); }
                catch (Exception e) { reason = "exception: " + e.Message; }

                if (!appended)
                {
                    if (ModEntry.Log != null)
                    {
                        ModEntry.Log.Log("[build] full rebuild: " + (reason ?? "unknown"));
                    }

                    CatalogDataBuilder.Build(true);
                    SpriteCache.Clear();
                }

                // 订阅了 CatalogRefreshed 的模组应该知道清单变了
                if (appended && CatalogDataBuilder.LastAppendCount > 0)
                {
                    ItemCatalogApi.RaiseRefreshed(CatalogDataBuilder.Visible.Count);
                }
            }
            else
            {
                CatalogDataBuilder.EnsureBuilt();
            }

            // 目录可能有增量（后加载模组）：按需重扫可搅拌清单；generation 没变时直接返回
            try { MachinePanels.DiscoverBlenderInputs(); }
            catch (Exception e) { Warn("blender discovery: " + e.Message); }

            // 熔炉输入/模块白名单同理，generation 没变时直接返回
            try { MachinePanels.DiscoverFurnaceRecipes(); }
            catch (Exception e) { Warn("furnace discovery: " + e.Message); }

            _page = 0;
            _inventoryRect = AnchorOverride != null ? AnchorOverride : AnchorTarget.Resolve();

            CustomUIBuilder builder;
            try
            {
                // Window(...) 是静态方法，内部使用 CustomUIManager.Instance
                builder = CustomUIManager.Window(WinId, ModEntry.T("win_title"), Layer)
                    .SetDraggable(false)
                    .SetCloseOnEscape(false)
                    .OnClose(HideNativeTooltip);
            }
            catch (Exception e)
            {
                Error("create catalog window", e);
                return;
            }

            // 右上角：「拿取物品」复选框（勾选后点格子=取一件进背包）
            try
            {
                builder.BeginRow(6f);
                builder.AddFlexibleSpace();
                builder.AddToggle(ModEntry.T("take_toggle"), _takeMode, OnTakeModeChanged, "take");
                builder.End();
            }
            catch (Exception e)
            {
                Warn("add take toggle: " + e.Message);
            }

            // 顶部搜索框：边输入边过滤，只重建网格容器，输入框焦点不丢
            try
            {
                builder.AddInput(ModEntry.T("search_placeholder"), _search,
                    OnSearchChanged, "search", OnSearchTyping);
            }
            catch (Exception e)
            {
                Warn("add search input: " + e.Message);
            }

            builder.AddLabel(string.Format(ModEntry.T("count"), CatalogDataBuilder.Visible.Count), "count");
            builder.AddSeparator();

            builder.BeginColumn(6f);
            _gridHost = builder.CurrentContainer;
            builder.End();

            // 网格吃掉剩余高度，翻页行才会固定在底部而不是跟着物品末尾走
            if (_gridHost != null)
            {
                var gridLayout = EnsureLayoutElement(_gridHost);
                gridLayout.flexibleHeight = 1f;
                gridLayout.minHeight = CellHeight;
            }

            builder.BeginRow(6f);
            var pagerRow = builder.CurrentContainer;
            builder.AddFlexibleSpace();
            builder.AddButton("<", PreviousPage, "prev");
            builder.AddLabel("", "page");
            builder.AddButton(">", NextPage, "next");
            builder.AddFlexibleSpace();
            builder.End();

            if (pagerRow != null)
            {
                var pagerLayout = EnsureLayoutElement(pagerRow);
                pagerLayout.flexibleHeight = 0f;
                pagerLayout.minHeight = 34f;
            }

            _window = builder.Show();
            ApplyPlacement(_window, _inventoryRect);
            EnsureInputBlocker();
            Render();

            // 常驻节拍器：页面不悬停也持续刷新
            CatalogTicker.Ensure();
            if (_window != null)
            {
                // 首页渲染完成后强制刷新一次布局，确保贴边尺寸立即生效
                CustomUIBuilder.RebuildLayout(_window.ContentRoot);
            }
        }

        // ── 分帧建格（懒加载）──────────────────────────────────────────────
        // 一页 84 格一次性建完要 300~400ms，打开/翻页会明显卡一下。
        // 现在每帧只建 CellsPerFrame 格，剩下的交给常驻节拍器继续，
        // 首帧先建两批保证立刻有内容，不会出现整页空白。
        private const int CellsPerFrame = 8;

        // 每秒一轮的图标刷新同样分摊到多帧：机器容器单格就要 40~95ms，
        // 一帧刷完 84 格是几百毫秒的硬卡。
        private const int RefreshCellsPerFrame = 12;

        private static bool _refreshing;
        private static int _refreshCursor;

        private static CustomUIBuilder _buildBuilder;
        private static List<ItemEntry> _buildItems;
        private static int _buildCursor;
        private static int _buildEnd;
        private static bool _building;

        /// <summary>继续把当前页的格子建完（每帧调用，`budget` 为本次最多建几格）。</summary>
        private static void PumpBuild(int budget)
        {
            if (!_building || _buildBuilder == null) return;

            try
            {
                int built = 0;
                while (_buildCursor < _buildEnd && built < budget)
                {
                    BuildCell(_buildBuilder, _buildItems[_buildCursor]);
                    _buildCursor++;
                    built++;
                }

                if (_buildCursor >= _buildEnd)
                {
                    _buildBuilder.End();
                    CustomUIBuilder.RebuildLayout(_gridHost);
                    _building = false;
                    _buildBuilder = null;
                    _buildItems = null;

                    if (!_spriteSummaryLogged && ModEntry.Log != null)
                    {
                        _spriteSummaryLogged = true;
                        ModEntry.Log.Log(string.Format(
                            "[sprite] page 1 render: ok={0} miss={1}", _spriteHits, _spriteMisses));
                    }
                }
                else
                {
                    // 已建好的格子先排好版，用户看到的是逐步填充而不是空白
                    CustomUIBuilder.RebuildLayout(_gridHost);
                }
            }
            catch (Exception e)
            {
                _building = false;
                _buildBuilder = null;
                _buildItems = null;
                Error("render item grid", e);
            }
        }

        /// <summary>建一格（分帧建格的单元，内容与原整页循环里的单格逻辑一致）。</summary>
        private static void BuildCell(CustomUIBuilder builder, ItemEntry entry)
        {
            if (builder == null || entry == null) return;

            // 变体格子：按需创建第 index 个实例（内部完成覆盖层采集）
            EnsureCellItem(entry);

            var sprite = GetSprite(entry);
            string elementId = "i:" + entry.Key;

            if (sprite != null)
            {
                // 液体容器：先合成后显示——有缓存用缓存，没有当场构建，
                // 保证格子第一次出现就是灌装后的图（而不是先空瓶、1 秒后才换水）。
                List<ContainerOverlay> overlays;
                if (_cellOverlays.TryGetValue(entry.Key, out overlays) && overlays != null && overlays.Count > 0)
                {
                    var composite = BuildCompositeSprite(entry.Key, sprite, overlays);
                    if (composite != null) sprite = composite;
                }

                // 按精灵图真实比例缩放，避免非正方形图标被拉扁
                float iconW, iconH;
                FitSprite(sprite, IconMaxSide, out iconW, out iconH);

                // 不再挂 CustomUI 自带的文字 tooltip，悬停统一走原生物品提示框
                builder.AddImage(sprite, iconW, iconH, elementId);

                var element = builder.Last;
                if (element != null) element.OnClick(MakeSelect(entry));

                // MakeClickable 返回真正被射线命中的子物体（Image 挂在子节点上）
                var hit = MakeClickable(element, MakeSelect(entry));
                AttachHover(hit, entry);
                AttachRightClick(hit, entry);

                // 记下格子，供自动刷新原地图标
                _pageEntries.Add(entry);
                if (hit != null)
                {
                    var icon = hit.GetComponent<UnityEngine.UI.Image>();
                    if (icon != null) _pageImages[entry.Key] = icon;
                }
            }
            else
            {
                builder.AddButton(Shorten(entry.DisplayName), MakeSelect(entry), elementId);

                // 和 BindCell(hasButton: true) 一致：事件挂在真正接收射线的子物体上。
                var buttonElement = builder.Last;
                if (buttonElement != null)
                {
                    var buttonHost = HitGraphic(buttonElement);
                    AttachHover(buttonHost, entry);
                    AttachRightClick(buttonHost, entry);
                }
            }
        }

        /// <summary>
        /// 窗口层级遮挡：拖拽落点用 RenderHandler.RaycastElement 从前往后找
        /// TreeNodeRender。图鉴正文是自定义 UI（没有 TreeNodeRender），射线会穿过去
        /// 命中窗口后面的游戏容器（拖拽释放会掉进背后的箱子/货架）。
        /// 这里在窗口底层垫一张透明、可射线、带空 TreeNodeRender 的整窗挡板，
        /// 碰到它就停止，后面的东西收不到操作。与详情窗口用同一套做法。
        /// </summary>
        private static void EnsureInputBlocker()
        {
            if (_window == null || !_window.IsAlive) return;
            if (_inputBlocker != null) return;

            var root = _window.Rect;
            if (root == null) return;

            try
            {
                var go = new GameObject("catalog:inputBlocker",
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

        private static void Render()
        {
            var manager = CustomUIManager.Instance;
            if (manager == null || _window == null || !_window.IsAlive || _gridHost == null) return;

            EnsureInputBlocker();


            var items = FilterItems();
            int perPage = Math.Max(1, _perPage);
            int pages = Math.Max(1, (items.Count + perPage - 1) / perPage);
            if (_page < 0) _page = 0;
            if (_page >= pages) _page = pages - 1;

            _spriteHits = 0;
            _spriteMisses = 0;

            _pageEntries.Clear();
            _pageImages.Clear();

            // 翻页/搜索会重建列表：正在进行的刷新游标要作废，否则会指向旧条目
            _refreshing = false;
            _refreshCursor = 0;
            // 注意：翻页重建不清理 _cellOverlays——key 是格子键、物品实例跨翻页存活，
            // 覆盖层仍然有效；清空会让「采集在重建前、应用在重建后」的时序全部丢失。

            try
            {
                CustomUIBuilder.ClearContainer(_gridHost);

                var builder = new CustomUIBuilder(manager, _window, _gridHost);
                builder.BeginGrid(_columns, CellWidth, CellHeight, CellSpacing);

                int start = _page * perPage;
                int end = Math.Min(items.Count, start + perPage);

                // 分帧建格：这一帧只建两批，剩下的由 AutoRefreshTick → PumpBuild 继续。
                // 首帧就有内容，不会整页空白；打开/翻页也不会再整页卡一下。
                _buildBuilder = builder;
                _buildItems = items;
                _buildCursor = start;
                _buildEnd = end;
                _building = true;
                PumpBuild(CellsPerFrame * 2);
            }
            catch (Exception e)
            {
                Error("render item grid", e);
            }

            SetText("count", string.Format(ModEntry.T("count"), items.Count));
            SetText("page", string.Format(ModEntry.T("page"), _page + 1, pages));
            SetInteractable("prev", _page > 0);
            SetInteractable("next", _page < pages - 1);
        }

        /// <summary>翻页到头时禁用按钮，明确告诉用户不能循环。</summary>
        private static void SetInteractable(string id, bool value)
        {
            if (_window == null || !_window.IsAlive) return;
            var element = _window.Get(id);
            if (element != null) element.SetInteractable(value);
        }

        // 禁止循环翻页：到头就停，也不让 _page 越过边界累积（-1、-2… 会导致点很多次才回得来）
        private static void PreviousPage()
        {
            if (_page <= 0) return;
            _page--;
            Render();
        }

        private static void NextPage()
        {
            int pages = Math.Max(1, (FilterItems().Count + Math.Max(1, _perPage) - 1) / Math.Max(1, _perPage));
            if (_page >= pages - 1) return;
            _page++;
            Render();
        }

        private static void OnSearchTyping(string text)
        {
            ApplySearch(text);
        }

        private static void OnSearchChanged(string text)
        {
            ApplySearch(text);
        }

        private static void ApplySearch(string text)
        {
            string value = text ?? string.Empty;
            if (string.Equals(value, _search, StringComparison.Ordinal)) return;

            _search = value;
            _page = 0;
            Render();
        }

        /// <summary>
        /// 把快照展开成「格子列表」：有限变体集合的物品按 N 格展开（JEI 的 matching stacks），
        /// 其余保持 1 格。只在数据重建（Generation 变化）后重算一次。
        /// </summary>
        private static List<ItemEntry> FlatEntries()
        {
            if (_flatGeneration == CatalogDataBuilder.Generation) return _flat;

            _flat.Clear();
            _baseById.Clear();

            // 重建往往就是"变体提供者刚注册/撤销"引起的，
            // 「这一格会不会变」的缓存必须跟着失效，否则会沿用旧判定。
            _dynamicCellCache.Clear();

            // 模块的「形状 → 效果」排列在格子展开内部完成（见 ModuleCells），
            // 条目本身保持目录顺序。
            var source = CatalogDataBuilder.Visible;

            for (int i = 0; i < source.Count; i++)
            {
                var entry = source[i];
                if (entry == null) continue;

                entry.VariantIndex = -1;
                _baseById[entry.Id] = entry;

                int cells = VariantCellCount(entry);
                if (cells <= 1)
                {
                    _flat.Add(entry);
                    continue;
                }

                for (int v = 0; v < cells; v++) _flat.Add(entry.CloneVariant(v));
            }

            _flatGeneration = CatalogDataBuilder.Generation;

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[cells] items=" + source.Count + " cells=" + _flat.Count);

            return _flat;
        }







        /// <summary>格子展开结果变了，下次刷新节拍重绘一次。</summary>
        private static bool _pendingRender;





        /// <summary>对比当前标签与快照：统计「新增的键」与「已存在但被改写的键」。</summary>
        private static void CompareTags(GameItem item, Dictionary<string, string> before,
            out int added, out int changed)
        {
            added = 0;
            changed = 0;

            var dict = SafeTagDict(item);
            if (dict == null || before == null) return;

            foreach (var pair in dict)
            {
                string now = TagText(pair.Value) ?? "~";

                string old;
                if (!before.TryGetValue(pair.Key, out old)) { added++; continue; }
                if (!string.Equals(old, now, StringComparison.Ordinal)) changed++;
            }
        }




        /// <summary>这件物品应该展开成几格（1 = 不展开，走单格轮播）。</summary>
        private static int VariantCellCount(ItemEntry entry)
        {
            if (entry == null) return 1;

            // ① 模组提供者：数量最权威（不设上限）
            var provider = ItemCatalogApi.FindVariantProvider(entry.Id);
            if (provider != null)
            {
                int count = 0;
                try { count = provider.VariantCount; } catch { }
                if (count > 1) return count;
            }

            // 刮刮乐：奖品随机，只保留一张（不展开成多格）
            if (IsScratcherEntry(entry)) return 1;

            // 香烟：1 格正品 + 4 格各带一处瑕疵的赝品
            if (IsCigaretteEntry(entry))
                return 1 + CigaretteIssueTags.Length;

            // 免疫宁注射器：与香烟同样的处理（1 格正品 + 每处瑕疵一格）
            if (IsInjectorEntry(entry)) return 1 + InjectorIssueTags.Length;

            // 食品券：每个面额都要覆盖全部真赝状态（真券 + 每种瑕疵一张假券）
            if (IsStampEntry(entry)) return StampAmounts.Length * StampStateCount;

            // ② 模块：占格形状数 × 每形状内的效果格
            if (IsModuleEntry(entry))
                return ModuleShapes(entry.Id).Count * ModuleEffectCells.Length;

            // ③ 浆果：五个形态（较小 / 正常 / 过熟 / 干瘪 / 发霉）
            if (IsBerryEntry(entry))
                return BerryForms.Length;

            // ④ 硬编码生成器表（香烟正品/赝品、真/假食品券…）
            Func<GameItem>[] pre;
            if (PrebuiltVariants.TryGetValue(entry.Id, out pre) && pre != null && pre.Length > 1)
                return pre.Length;

            return 1;
        }


        /// <summary>
        /// 容器里到底有没有东西。注意 <c>WaterHelper.AddWater</c> 写的不是
        /// <c>LIQUID_CONTAINER_CURRENT</c>，而是逐液体的 <c>CURRENT_PART_*</c> 标签，
        /// 所以必须两者都看，否则永远判成空瓶。
        /// </summary>
        private static bool HasLiquidContent(GameItem item)
        {
            if (item == null) return false;

            if (ReadIntTag(item, "LIQUID_CONTAINER_CURRENT") > 0) return true;

            var dict = SafeTagDict(item);
            if (dict == null) return false;

            foreach (var pair in dict)
            {
                if (pair.Key == null) continue;
                if (pair.Key.IndexOf("CURRENT_PART_", StringComparison.Ordinal) != 0) continue;

                var state = pair.Value;
                if (state == null) continue;

                try { if (state.valueEnabled && state.valueInt > 0) return true; }
                catch { }
            }

            return false;
        }

        /// <summary>渲染前确保这一格已经有了对应的变体实例（按需创建，启动时不批量造物品）。</summary>
        private static void EnsureCellItem(ItemEntry entry)
        {
            if (entry == null) return;

            // 无变体的物品直接用构建期那份实例（VariantIndex = -1），
            // 所以实例级后处理不能只挂在变体创建路径上，这里也要走一遍。
            if (entry.Item != null)
            {
                // 只有第一次加工成功时才需要重选精灵（容器灌装后外观会变）
                if (PostProcessCellItem(entry, entry.Item)) FinalizeItemSprites(entry.Item, entry);
                return;
            }

            if (entry.VariantIndex < 0) return;

            var item = CreateCellItem(entry, entry.VariantIndex);
            if (item != null)
            {
                PostProcessCellItem(entry, item);
                FinalizeItemSprites(item, entry);   // 所有处理（灌装/状态/成分）完成后再选精灵
                entry.Item = item;
            }
        }

        /// <summary>实例级后处理只做一次（键 = entry.Key，同一格可能被反复渲染）。</summary>
        private static readonly HashSet<string> _cellPostProcessed = new HashSet<string>(StringComparer.Ordinal);

        private static int _lotteryLogs;

        /// <summary>每格的实例级加工：彩票去号码、药品灌满纯度、医疗瓶灌满主成分。</summary>
        private static bool PostProcessCellItem(ItemEntry entry, GameItem item)
        {
            if (entry == null || item == null) return false;
            if (!_cellPostProcessed.Add(entry.Key ?? entry.Id)) return false;

            ApplyItemProcessing(item, entry);

            return true;
        }

        /// <summary>
        /// 实例级加工本体（可重复调用）：
        /// 彩票去号码、药品灌满纯度、医疗瓶灌满主成分。
        /// 「拿取物品」也要用同一套，保证取到的和看到的完全一致。
        /// </summary>
        private static void ApplyItemProcessing(GameItem item, ItemEntry entry)
        {
            if (item == null || entry == null) return;

            StripLotteryData(item, entry.Id);

            if (IsFullPurityItem(entry.Id)) ApplyFullPurity(item, entry.Id);

            FillMainIngredient(item, entry.Id);
        }

        /// <summary>
        /// med_bottle_* 出厂该装的液体。映射来自 PreBuildChemHelper 里的制瓶函数：
        ///   red → acid / blue → base / orange → reducing_agent
        ///   violet → methylol + stabilizing_agent（各一半，芬太尼前体）
        ///   small_pink → lipobond_agent
        /// 多个液体时平均分配整瓶容量。
        /// </summary>
        private static readonly Dictionary<string, string[]> MedBottleFill =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                { "med_bottle_red",        new[] { "acid" } },
                { "med_bottle_blue",       new[] { "base" } },
                { "med_bottle_orange",     new[] { "reducing_agent" } },
                { "med_bottle_violet",     new[] { "methylol", "stabilizing_agent" } },
                { "med_bottle_small_pink", new[] { "lipobond_agent" } }
            };

        /// <summary>
        /// 把医疗瓶灌满「纯的」成分：只加表里的液体、总量到容量为止，
        /// 不加任何杂质液体（原版制瓶会 SetImpurity 掺 200..800）。
        /// </summary>
        private static void FillMainIngredient(GameItem item, string id)
        {
            string[] liquids;
            if (string.IsNullOrEmpty(id) || !MedBottleFill.TryGetValue(id, out liquids)) return;
            if (liquids == null || liquids.Length == 0) return;

            if (HasLiquidContent(item)) return;   // 已经有内容就不动

            int capacity = ReadIntTag(item, "LIQUID_CONTAINER_CAPACITY");
            if (capacity <= 0) return;

            int perLiquid = capacity / liquids.Length;

            try
            {
                for (int i = 0; i < liquids.Length; i++)
                {
                    // 最后一份吃掉余数，保证正好满瓶
                    int amount = i == liquids.Length - 1 ? capacity - perLiquid * (liquids.Length - 1) : perLiquid;
                    WaterHelper.AddLiquid(item, liquids[i], amount);
                }

                if (ModEntry.Log != null && _medFillLogs < 8)
                {
                    _medFillLogs++;
                    ModEntry.Log.Log("[med-fill] " + id + " " + string.Join("+", liquids)
                        + " cap=" + capacity + " now=" + (HasLiquidContent(item) ? "yes" : "NO"));
                }
            }
            catch (Exception e) { Warn("med fill '" + id + "': " + e.Message); }
        }

        private static int _medFillLogs;

        /// <summary>彩票上的所有「数值类」标签。</summary>
        private static readonly string[] LotteryDataTags =
        {
            "LOTTERY_TICKET_TAG",
            "LOTTERY_NUMBER_TAG",
            "LOTTERY_WINNING_NUMBER_TAG",
            "LOTTERY_MATCH_COUNT_TAG",
            "LOTTERY_IS_WINNING_TAG",
            "LOTTERY_EDITION_TAG",
            "LOTTERY_SPAWN_LUCK_INT"
        };

        /// <summary>
        /// 图鉴里的彩票仍然用 lottery_ticket 本身（名字、精灵、价格都不变），
        /// 但不带任何号码/期号/中奖数据：把这些标签全部关掉。
        /// 于是提示框里没有号码段，CheckTicket 也会因为缺 LOTTERY_TICKET_TAG 直接返回——
        /// 就是一张「还没印号码、不能用」的票。
        /// </summary>
        private static void StripLotteryData(GameItem item, string id)
        {
            if (!string.Equals(id, "lottery_ticket", StringComparison.Ordinal)) return;

            for (int i = 0; i < LotteryDataTags.Length; i++)
            {
                try
                {
                    if (item.IsTag(LotteryDataTags[i])) item.SetEnableDisableTag(LotteryDataTags[i], false);
                }
                catch { }
            }

            if (ModEntry.Log != null && _lotteryLogs < 6)
            {
                _lotteryLogs++;
                ModEntry.Log.Log("[lottery] " + id
                    + " ticket=" + item.IsTag("LOTTERY_TICKET_TAG")
                    + " edition=" + ReadIntTag(item, "LOTTERY_EDITION_TAG")
                    + " number='" + ReadStringTag(item, "LOTTERY_NUMBER_TAG") + "'");
            }
        }

        /// <summary>
        /// 精灵选择的统一收口：在变体实例的所有加工都完成之后调用。
        /// 容器按容量档位与水质重算瓶身精灵；所有物品都跑元素管线并采集覆盖层
        /// （外观随内容/状态变化的类型统一在这里收口，不要散落到各灌装点）。
        /// </summary>
        private static void FinalizeItemSprites(GameItem item, ItemEntry entry)
        {
            if (item == null) return;

            string key = entry == null ? null : entry.Key;
            int cap = ReadIntTag(item, "LIQUID_CONTAINER_CAPACITY");

            try
            {
                // 容器（带容量标签，>0）先按原版规则重算瓶身精灵与「水」类型。
                // 判定走数据驱动，不依赖特定 TAG——新容器类型自动被覆盖。
                if (cap > 0)
                {
                    ItemEntry baseEntry;
                    _baseById.TryGetValue(entry == null ? null : entry.Id, out baseEntry);
                    var baseItem = baseEntry == null ? null : baseEntry.Item;

                    UpdateContainerSprite(item, baseItem);

                    // 有液体 → 物品种类变水（原版 UpdateContainerGoodType 的效果，
                    // 覆盖层修饰器按这个类型工作）
                    if (HasLiquidContent(item) && !item.IsGameItemType("WATER"))
                        item.SetGameItemType("WATER");
                    else if (!HasLiquidContent(item) && item.IsGameItemType("WATER"))
                        item.RemoveGameItemType("WATER");
                }

                // 所有物品统一走元素管线并采集覆盖层：非容器同样可能有内置覆盖层
                // （液体好运瓶没有容量标签，精灵却由液体层 + 高光层 + 标签层叠出来）。
                // key 用格子键：多格容器的每一格液体不同，覆盖层互不共享。
                ApplyElementOverlayPipeline(item);
                CaptureContainerOverlays(item, key);

                // 诊断：采集结果（每 id 一条）——容量/液体/覆盖层层数
                if (entry != null && ModEntry.Log != null && _compositeDiag.Add("fin:" + entry.Id))
                {
                    List<ContainerOverlay> ov;
                    _cellOverlays.TryGetValue(key, out ov);

                    ModEntry.Log.Log("[container-diag] " + entry.Id + " cap=" + cap
                                     + " liquid=" + HasLiquidContent(item)
                                     + " overlays=" + (ov == null ? 0 : ov.Count));
                }
            }
            catch { }
        }

        /// <summary>该 id 在游戏自己的预置生成器注册表里有没有生成函数（化学品等出厂带内容物的物品）。</summary>
        private static bool HasPrebuiltGenerator(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;

            try
            {
                var dict = PreBuiltItemHelper.preBuiltItems;
                return dict != null && dict.ContainsKey(id);
            }
            catch { return false; }
        }

        /// <summary>Whether a liquid with the same id exists in the game liquid registry.</summary>
        private static bool LiquidRegistryContains(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;

            try
            {
                var dict = Liquid.LiquidDict;
                return dict != null && dict.ContainsKey(id);
            }
            catch { return false; }
        }

        /// <summary>
        /// Fill an empty container with the liquid registered under the SAME id
        /// (acid / base / zyanide ... chemicals whose item id == liquid id).
        /// Uses the vanilla WaterHelper.AddLiquid, amount = full capacity.
        /// </summary>
        private static void FillFromLiquidRegistry(GameItem item, string liquidId)
        {
            try
            {
                int capacity = ReadIntTag(item, "LIQUID_CONTAINER_CAPACITY");
                if (capacity <= 0) return;

                WaterHelper.AddLiquid(item, liquidId, capacity);

                if (ModEntry.Log != null && _prebuiltDiag.Add(liquidId))
                    ModEntry.Log.Log("[liquid-fill] " + liquidId + " -> cap=" + capacity
                                     + " now=" + (HasLiquidContent(item) ? "yes" : "NO"));
            }
            catch (Exception e)
            {
                if (_liquidFillWarns++ < 8) Warn("liquid fill '" + liquidId + "': " + e.Message);
            }
        }

        /// <summary>
        /// 用游戏自己的预置生成器造一份真实实例（化学品出厂即带正确的酸/碱/纯度）。
        /// 前后快照随机流（与 CreatePreviewItem 同样的保护），不干扰游戏随机性。
        /// </summary>
        private static GameItem TrySpawnPrebuilt(string id)
        {
            var savedRng = RNG.random;
            var savedUnityState = UnityEngine.Random.state;

            try
            {
                var dict = PreBuiltItemHelper.preBuiltItems;
                if (dict == null) return null;

                System.Func<GameItem> func;
                if (!dict.TryGetValue(id, out func) || func == null) return null;

                return func();
            }
            catch (Exception e)
            {
                if (_liquidFillWarns++ < 8) Warn("prebuilt spawn '" + id + "': " + e.Message);
                return null;
            }
            finally
            {
                UnityEngine.Random.state = savedUnityState;
                RNG.random = savedRng;
            }
        }

        /// <summary>按来源造出「第 index 个变体」的实例。</summary>
        private static GameItem CreateCellItem(ItemEntry entry, int index)
        {
            if (entry == null) return null;

            ItemEntry baseEntry;
            _baseById.TryGetValue(entry.Id, out baseEntry);
            var baseItem = baseEntry == null ? entry.Item : baseEntry.Item;

            // ① 模组提供者
            var provider = ItemCatalogApi.FindVariantProvider(entry.Id);
            if (provider != null)
            {
                try
                {
                    var variant = provider.CreateVariant(index);
                    if (variant != null) return variant;
                }
                catch (Exception e)
                {
                    Warn("variant provider create '" + entry.Id + "' #" + index + ": " + e.Message);
                }
            }

            // ② 模块：先按占格形状（精灵）排列，同一形状内再按效果排列
            if (IsModuleEntry(entry))
            {
                var module = CreateBaseModule(entry.Id);
                if (module != null)
                {
                    var shapes = ModuleShapes(entry.Id);
                    int perShape = ModuleEffectCells.Length;
                    int total = shapes.Count * perShape;

                    int flat = index % total;
                    string shape = shapes[flat / perShape];
                    var cell = ModuleEffectCells[flat % perShape];

                    ApplyModuleShape(module, shape);

                    if (!string.IsNullOrEmpty(cell.Effect))
                        ApplyModuleEffect(module, entry.Id, cell.Effect, cell.Bonus);

                    LogModuleCell(entry, index, module, shape, cell);

                    return module;
                }
            }

            // ③ 浆果：五个形态（较小 / 正常 / 过熟 / 干瘪 / 发霉）
            if (IsBerryEntry(entry))
            {
                // 必须拿到「标签状态独立」的实例：克隆出来的 items 会共享 TagSystem，
                // 五格互相覆盖（最后写的发霉赢）。先走原版生成器，它每次都会新建状态。
                GameItem berry = null;
                string source = "none";

                try
                {
                    berry = ItemSpawner.Spawn(entry.Id);
                    if (berry != null) source = "spawn";
                }
                catch (Exception e) { Warn("berry spawn '" + entry.Id + "': " + e.Message); }

                if (berry == null && baseItem != null)
                {
                    try
                    {
                        berry = baseItem.CloneLinked();
                        if (berry != null) source = "clone";

                        if (berry != null)
                        {
                            try { berry.state = baseItem.state == null ? null : baseItem.state.Clone(); }
                            catch (Exception e) { Warn("berry state clone: " + e.Message); }

                            try { berry.modifiedState = baseItem.modifiedState == null ? null : baseItem.modifiedState.Clone(); }
                            catch (Exception e) { Warn("berry modifiedState clone: " + e.Message); }
                        }
                    }
                    catch (Exception e) { Warn("berry clone '" + entry.Id + "': " + e.Message); }
                }

                if (berry == null)
                {
                    try
                    {
                        berry = DirectoryMaster.Item(entry.Id, true);
                        if (berry != null) source = "template";
                    }
                    catch (Exception e) { Warn("berry template '" + entry.Id + "': " + e.Message); }
                }

                if (berry == null)
                {
                    berry = CreatePreviewItem(entry.Id);
                    if (berry != null) source = "preview";
                }

                if (berry == null && _berryCellLogs < 12 && ModEntry.Log != null)
                    ModEntry.Log.Warning("[berry-cell] " + entry.Id + " #" + index + " 实例创建失败");

                if (berry != null)
                {
                    var form = BerryForms[index % BerryForms.Length];

                    // 品相标签可能还没建出来，先确保存在再写值
                    try { berry.EnableTag("BERRY_TAG", true); } catch { }
                    try { berry.EnableTag("BERRY_CONDITION_INT", true); } catch { }
                    try { berry.EnableTag("BERRY_VISUAL_VARIANT_INT", true); } catch { }

                    CatalogVariantKit.IsolateTagState(berry);

                    try { WineHelper.SetBerryCondition(berry, form.Condition, form.Visual); }
                    catch (Exception e) { Warn("berry condition '" + entry.Id + "' #" + index + ": " + e.Message); }

                    LogBerryCell(entry, index, berry, form, source);

                    return berry;
                }
            }

            // ② 香烟：1 格正品 + 4 格各一处瑕疵的赝品（标签也是赝品）
            if (IsCigaretteEntry(entry))
            {
                int variant = index % (1 + CigaretteIssueTags.Length);

                var cigarettePool = CatalogVariantKit.SnapshotModulePool();
                GameItem cigarette = null;
                try
                {
                    cigarette = variant == 0
                        ? PreBuiltItemHelper.GenuineCigarette()
                        : PreBuiltItemHelper.CounterfeitCigarette();
                }
                catch (Exception e) { Warn("cigarette '" + entry.Id + "' #" + index + ": " + e.Message); }
                finally { CatalogVariantKit.RestoreModulePool(cigarettePool); }

                if (cigarette != null)
                {
                    NormalizeCigarette(cigarette, variant);
                    CatalogVariantKit.StripAuthenticityLabels(cigarette);
                    return cigarette;
                }
            }

            // ②b 免疫宁注射器：1 格正品 + 5 格各一处问题的赝品
            if (IsInjectorEntry(entry))
            {
                int variant = index % (1 + InjectorIssueTags.Length);

                var injectorPool = CatalogVariantKit.SnapshotModulePool();
                GameItem injector = null;
                try
                {
                    injector = variant == 0
                        ? InsInjectorHelper.CreateRealInjector()
                        : InsInjectorHelper.CreateCounterfeit();
                }
                catch (Exception e) { Warn("injector '" + entry.Id + "' #" + index + ": " + e.Message); }
                finally { CatalogVariantKit.RestoreModulePool(injectorPool); }

                if (injector != null)
                {
                    CatalogVariantKit.NormalizeIssues(injector, variant, InjectorIssueTags);
                    CatalogVariantKit.StripAuthenticityLabels(injector);
                    return injector;
                }
            }

            // ②c 食品券：面额 × 状态。每个面额都有 1 张真券 + 每种瑕疵 1 张假券
            if (IsStampEntry(entry))
            {
                int denomIndex = (index / StampStateCount) % StampAmounts.Length;
                int state = index % StampStateCount;
                int amount = StampAmounts[denomIndex];

                var stampPool = CatalogVariantKit.SnapshotModulePool();
                GameItem stamp = null;
                try
                {
                    stamp = state == 0
                        ? PreBuiltItemHelper.CreateStamp()
                        : PreBuiltItemHelper.CreateFakeStamp();

                    if (stamp != null)
                    {
                        // 生成器的面额是随机的，这里按格子的面额固定下来
                        if (state == 0) InsStampHelper.SetGenuine(stamp, amount);
                        else InsStampHelper.SetCounterfeit(stamp, amount);
                    }
                }
                catch (Exception e) { Warn("stamp '" + entry.Id + "' #" + index + ": " + e.Message); }
                finally { CatalogVariantKit.RestoreModulePool(stampPool); }

                if (stamp != null)
                {
                    CatalogVariantKit.NormalizeIssues(stamp, state, StampIssueTags);

                    // 「面额瑕疵」那一格保持被改小的面额；其余状态把面额强制纠正回来
                    if (state != StampIssueTags.Length) CatalogVariantKit.ForceStampAmount(stamp, amount);

                    // 真赝品标签是标签打印机贴上去的那一条，统一去掉
                    CatalogVariantKit.StripAuthenticityLabels(stamp);

                    return stamp;
                }
            }

            // ③ 预置生成器（生成流程的真实产物）
            Func<GameItem>[] pre;
            if (PrebuiltVariants.TryGetValue(entry.Id, out pre) && pre != null && pre.Length > 0)
            {
                var pool = CatalogVariantKit.SnapshotModulePool();
                GameItem prebuilt = null;
                try { prebuilt = pre[index % pre.Length](); }
                catch (Exception e) { Warn("prebuilt '" + entry.Id + "' #" + index + ": " + e.Message); }
                finally { CatalogVariantKit.RestoreModulePool(pool); }

                if (prebuilt != null) return prebuilt;
            }


            // 全部来源都失败：退回基础实例，至少保证图标与提示框（名称/类型/价格）可用，
            // 不能返回 null —— 那会让这一格的提示框退化成一堆空字段。
            if (_cellFallbackWarns++ < 5 && ModEntry.Log != null)
            {
                ModEntry.Log.Warning("[cells] no variant instance for '" + entry.Id + "' #" + index
                                     + ", falling back to base item");
            }

            return baseItem;
        }

        /// <summary>按搜索词过滤（id 或显示名，忽略大小写）；空串返回全部格子。</summary>
        private static List<ItemEntry> FilterItems()
        {
            var all = FlatEntries();
            if (string.IsNullOrEmpty(_search) || _search.Trim().Length == 0) return all;

            string query = _search.Trim();

            // 先把查询词反查成类型键：本地化名「奢侈品」→ `LUXURY_ITEM`。
            // 不这么做的话，只有输入英文键名才能搜到，中文名一条都搜不出来。
            var matchedTypes = MatchTypes(query);

            var result = new List<ItemEntry>(64);

            for (int i = 0; i < all.Count; i++)
            {
                var entry = all[i];
                if (entry == null) continue;

                bool hit = false;

                if (!string.IsNullOrEmpty(entry.Id) &&
                    entry.Id.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hit = true;
                }
                else if (!string.IsNullOrEmpty(entry.DisplayName) &&
                         entry.DisplayName.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)
                {
                    hit = true;
                }
                else
                {
                    // 标签 / 物品类型也能搜：输入 `ADV_GUN_TAG` 就列出整类枪，
                    // 输入 `WEAPON` / `FOOD`（客人收购用的那套 itemTypes）同理。
                    var gi = entry.Item;
                    if (gi != null)
                    {
                        try { hit = gi.IsTag(query) || gi.IsGameItemType(query); }
                        catch { hit = false; }

                        // 再按「本地化类型名」命中：日用品 / 文档 / 奢侈品 / 水 …
                        if (!hit && matchedTypes != null)
                            hit = HasAnyType(gi, matchedTypes);
                    }
                }

                if (hit) result.Add(entry);
            }

            // 液体条目只在搜索时加入，不占普通物品网格
            var liquids = LiquidCatalog.Search(query);
            for (int i = 0; i < liquids.Count; i++)
            {
                if (liquids[i] != null) result.Add(liquids[i]);
            }

            return result;
        }

        /// <summary>类型键 → 本地化名（`LUXURY_ITEM` → 奢侈品），首次搜索时建一次。</summary>
        private static Dictionary<string, string> _typeNames;

        private static Dictionary<string, string> TypeNames()
        {
            if (_typeNames != null) return _typeNames;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var all = FlatEntries();

            for (int i = 0; i < all.Count; i++)
            {
                var gi = all[i] != null ? all[i].Item : null;
                if (gi == null) continue;

                List<string> types = null;
                try { types = gi.GetGameItemType(); }
                catch { }
                if (types == null) continue;

                for (int k = 0; k < types.Count; k++)
                {
                    string key = types[k];
                    if (string.IsNullOrEmpty(key) || map.ContainsKey(key)) continue;

                    // 类型名在 `type_<小写>` 键上，先试 mechanic 段，再退 UI / 通用表
                    string locKey = "type_" + key.ToLowerInvariant();
                    string name = null;
                    try { name = LocHelper.GetLocalizedMechanicOrDefault(locKey, null); } catch { }
                    if (string.IsNullOrEmpty(name))
                    {
                        try { name = LocHelper.GetLocalizedUIOrDefault(locKey, null); } catch { }
                    }
                    if (string.IsNullOrEmpty(name))
                    {
                        try { name = LocHelper.GetLocalized(locKey, null); } catch { }
                    }

                    map[key] = string.IsNullOrEmpty(name) ? key : name;
                }
            }

            _typeNames = map;
            return map;
        }

        /// <summary>查询词命中的类型键集合（键名或本地化名任一包含即可）。</summary>
        private static HashSet<string> MatchTypes(string query)
        {
            var names = TypeNames();
            if (names.Count == 0) return null;

            var hits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in names)
            {
                if (pair.Key.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hits.Add(pair.Key);
                    continue;
                }

                if (!string.IsNullOrEmpty(pair.Value) &&
                    pair.Value.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)
                {
                    hits.Add(pair.Key);
                }
            }

            return hits.Count > 0 ? hits : null;
        }

        private static bool HasAnyType(GameItem item, HashSet<string> types)
        {
            try
            {
                var own = item.GetGameItemType();
                if (own == null) return false;

                for (int i = 0; i < own.Count; i++)
                    if (!string.IsNullOrEmpty(own[i]) && types.Contains(own[i])) return true;
            }
            catch { }

            return false;
        }

        private static void OnTakeModeChanged(bool value)
        {
            _takeMode = value;

            if (!value) ClearDropMark();
        }

        /// <summary>作弊模式：点击拿取、拖到面板上删除，都只看这一个开关。</summary>
        internal static bool TakeMode { get { return _takeMode; } }

        private static GameObject _dropMark;

        /// <summary>
        /// 拖动物品且指针压在图鉴上时，在**那只物品底下**垫一块红底，
        /// 和容器放不下物品时游戏给出的红底是同一个意思：这里不能放，松手就没了。
        ///
        /// 红底挂在被拖动的 `GameItemElement` 下面并 `SetAsFirstSibling`，
        /// 所以它永远在图标后面，跟着物品一起移动，不需要自己算坐标。
        /// </summary>
        internal static void DragHighlightTick()
        {
            bool over = false;
            try
            {
                var point = Input.mousePosition;
                over = IsPointerOverPanel(point) || LauncherButton.IsPointerOver(point);
            }
            catch { }

            // 只有作弊模式下松手才会删，普通模式不该给这种暗示
            bool active = over && _takeMode;

            // 单选拖拽：一只物品，红底跟着指针
            GameItemElement dragged = null;
            try
            {
                var handler = ItemMouseDragHandler.current;
                if (handler != null && handler.IsDraggingItem)
                    dragged = handler.GetCurrentItem() as GameItemElement;
            }
            catch { dragged = null; }

            if (dragged != null)
            {
                ClearGroupMarks();

                if (!active) { ClearDropMark(); return; }

                ShowDropMark(dragged);
                return;
            }

            // 框选拖拽：每只被拖的物品底下各垫一块，跟着它们一起走
            ClearDropMark();

            if (!active || !DragToDeletePatch.IsGroupDragging())
            {
                ClearGroupMarks();
                return;
            }

            SyncGroupMarks();
        }

        // ---------------------------------------------------------------- 多选红底

        private static readonly Dictionary<GameItemElement, GameObject> _groupMarks =
            new Dictionary<GameItemElement, GameObject>();

        private static readonly List<GameItemElement> _groupMarkKeys = new List<GameItemElement>();

        /// <summary>
        /// 每一只被框选拖动的物品，各挂一块红底。
        /// 拖拽时元素的视觉节点是 <c>fakeMiddle</c>（见 <see cref="DragToDeletePatch.GhostNode"/>），
        /// 红底挂到它下面垫底，尺寸、位置、平滑插值全跟着幽灵走，不需要自己算坐标。
        /// </summary>
        private static void SyncGroupMarks()
        {
            List<GameItemElement> selected = null;
            try { selected = DragToDeletePatch.SelectedElements(); }
            catch { }

            if (selected == null || selected.Count == 0)
            {
                ClearGroupMarks();
                return;
            }

            // 已经不在选中列表里的，收掉
            for (int i = _groupMarkKeys.Count - 1; i >= 0; i--)
            {
                var key = _groupMarkKeys[i];
                bool keep = false;

                if (key != null)
                {
                    for (int k = 0; k < selected.Count; k++)
                    {
                        if (selected[k] == key) { keep = true; break; }
                    }
                }

                if (!keep) DropGroupMark(key);
            }

            for (int i = 0; i < selected.Count; i++)
                PlaceGroupMark(selected[i]);
        }

        private static void PlaceGroupMark(GameItemElement element)
        {
            if (element == null) return;

            var ghost = DragToDeletePatch.GhostNode(element);
            if (ghost == null) { DropGroupMark(element); return; }

            GameObject go;
            if (!_groupMarks.TryGetValue(element, out go) || go == null)
            {
                go = new GameObject("catalogDropRed",
                    typeof(RectTransform), typeof(UnityEngine.UI.Image));

                var image = go.GetComponent<UnityEngine.UI.Image>();
                image.color = new Color(0.85f, 0.1f, 0.1f, 0.6f);
                image.raycastTarget = false;

                _groupMarks[element] = go;
                _groupMarkKeys.Add(element);
            }

            var rect = (RectTransform)go.transform;
            if (rect.parent != ghost)
            {
                // 挂到幽灵节点下并垫底（SetAsFirstSibling）：永远在图标后面，
                // 和单选红底同一套路；物品怎么平滑移动，红底就怎么跟着
                rect.SetParent(ghost, false);
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                go.transform.SetAsFirstSibling();
            }
        }

        private static void DropGroupMark(GameItemElement key)
        {
            GameObject mark;
            if (key != null && _groupMarks.TryGetValue(key, out mark))
            {
                if (mark != null)
                {
                    try { UnityEngine.Object.Destroy(mark); } catch { }
                }

                _groupMarks.Remove(key);
            }

            _groupMarkKeys.Remove(key);
        }

        private static void ClearGroupMarks()
        {
            // 倒着走：DropGroupMark 会从列表里移除元素
            for (int i = _groupMarkKeys.Count - 1; i >= 0; i--) DropGroupMark(_groupMarkKeys[i]);

            _groupMarks.Clear();
            _groupMarkKeys.Clear();
        }

        private static void ShowDropMark(GameItemElement dragged)
        {
            if (_window == null || !_window.IsAlive || !_window.IsVisible) return;

            var root = _window.Rect;
            if (root == null) return;

            var canvasRoot = root.parent as RectTransform;
            if (canvasRoot == null) return;

            try
            {
                if (_dropMark == null || _dropMark.transform.parent != canvasRoot)
                {
                    var go = new GameObject("catalogDropRed",
                        typeof(RectTransform), typeof(UnityEngine.UI.Image));

                    var rect = (RectTransform)go.transform;
                    rect.SetParent(canvasRoot, false);
                    rect.anchorMin = new Vector2(0.5f, 0.5f);
                    rect.anchorMax = new Vector2(0.5f, 0.5f);
                    rect.pivot = new Vector2(0.5f, 0.5f);

                    var image = go.GetComponent<UnityEngine.UI.Image>();
                    image.color = new Color(0.85f, 0.1f, 0.1f, 0.6f);
                    image.raycastTarget = false;

                    go.layer = canvasRoot.gameObject.layer;

                    _dropMark = go;
                }

                var mark = (RectTransform)_dropMark.transform;

                // 大小取被拖那只物品的**实际显示尺寸**：rect 是本地尺寸，
                // 游戏在拖拽时会给节点缩放，所以要再乘 lossyScale 才是屏幕上看到的大小。
                float width = CellWidth, height = CellHeight;
                try
                {
                    var src = dragged.rectTransform;
                    if (src != null && src.rect.width > 2f && src.rect.height > 2f)
                    {
                        var size = src.rect.size;
                        var scale = src.lossyScale;

                        width = size.x * Mathf.Abs(scale.x);
                        height = size.y * Mathf.Abs(scale.y);
                    }

                    // 红底画在画布上，尺寸要用画布单位：除以画布的 lossyScale 换算一次
                    var canvasScale = canvasRoot.lossyScale;
                    if (Mathf.Abs(canvasScale.x) > 0.001f) width /= Mathf.Abs(canvasScale.x);
                    if (Mathf.Abs(canvasScale.y) > 0.001f) height /= Mathf.Abs(canvasScale.y);
                }
                catch { }

                mark.sizeDelta = new Vector2(width, height);

                // 跟着鼠标走：被拖动的物品就在指针下面，红底于是贴在它身上，
                // 而不是留在物品原来所在的格子里
                Vector2 local;
                if (UnityEngine.RectTransformUtility.ScreenPointToLocalPointInRectangle(
                        canvasRoot, Input.mousePosition, null, out local))
                {
                    var parentRect = canvasRoot.rect;
                    local += new Vector2(
                        (0.5f - canvasRoot.pivot.x) * parentRect.width,
                        (0.5f - canvasRoot.pivot.y) * parentRect.height);

                    mark.anchoredPosition = local;
                }

                if (!_dropMark.activeSelf) _dropMark.SetActive(true);
                _dropMark.transform.SetAsLastSibling();
            }
            catch (Exception e) { Warn("drop mark: " + e.Message); }
        }

        private static void ClearDropMark()
        {
            if (_dropMark != null)
            {
                try { UnityEngine.Object.Destroy(_dropMark); } catch { }
            }

            _dropMark = null;
        }

        /// <summary>外部窗口用 AddButton 建格子时，左键回调直接复用图鉴的选中动作。</summary>
        internal static Action SelectAction(ItemEntry entry) { return MakeSelect(entry); }

        /// <summary>
        /// 给外部窗口（详情窗口）的物品格子挂上与图鉴完全一致的一套交互：
        /// 左键看来源、右键看去处、悬停走原生物品提示框，并登记到拖拽查询表。
        /// 顺序不能变：AttachHover 会清 trigger，右键必须挂在它之后。
        /// AddButton 生成的格子自带左键，这里只补 hover / 右键，避免重复触发。
        /// </summary>
        /// <param name="hasButton">
        /// true = 元素由 AddButton 创建（左键已绑好），这里只补悬停与右键；
        /// false = AddImage 创建，需要 MakeClickable 补一个 Unity Button 左键。
        /// 不要靠 element.button 自动判断：预置体可能自带 Button 组件导致误判。
        /// </param>
        internal static void BindCell(CustomUIElement element, ItemEntry entry, bool hasButton,
            bool emptyBottle = false, bool clientComposition = false)
        {
            if (element == null || entry == null) return;

            try
            {
                if (!string.IsNullOrEmpty(entry.Key)) _externalCells[entry.Key] = entry;

                GameObject host;
                if (hasButton)
                {
                    // 和 MakeClickable 一样，事件要挂在真正被射线命中的那个子物体上；
                    // 只挂在 element.Rect 上会被子节点 Graphic 挡住，hover / 右键收不到
                    host = HitGraphic(element);
                }
                else
                {
                    // MakeClickable 返回真正被射线命中的子物体（Image 挂在子节点上）
                    var hit = MakeClickable(element, MakeSelect(entry));
                    host = hit != null ? hit : HitGraphic(element);
                }

                AttachHover(host, entry, emptyBottle, clientComposition);
                AttachRightClick(host, entry);
            }
            catch (Exception e)
            {
                Warn("bind cell: " + e.Message);
            }
        }

        /// <summary>
        /// 给预构建键的格子（没有目录条目）挂提示框与拖拽登记。
        /// 悬停显示：物品名 + 生成时的耐久范围（采样预构建工厂得出）。
        /// </summary>
        /// <summary>
        /// 给预构建键造一条带实例的目录条目（样本在隔离随机流里生成）。
        /// 有了实例，BindCell 挂的原生提示框链、拖拽拿取就与普通容器格子完全一致——
        /// 这就是「照抄容器的写法」。实例按 id 缓存，换场景时清掉。
        /// </summary>
        internal static ItemEntry PreBuiltEntry(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            ItemEntry cached;
            if (_preBuiltEntries.TryGetValue(id, out cached) && cached != null) return cached;

            var item = CreatePreviewItem(id);
            if (item == null) return null;

            // 实例上掷出来的那一次耐久不进提示框（每次悬停都不同、也没有信息量）：
            // 摘掉耐久特性，原版提示框链就不再画「耐久度: x/max」那行——
            // 那行走特性管线渲染，删文本删不掉。范围由 AppendPrebuiltRange 单独补。
            if (PreBuiltItems.DurabilityRange(id) != null)
            {
                var durabilityFeature = DurabilityHelper.FeatureDurability();
                if (durabilityFeature != null) item.RemoveItemFeatureByID(durabilityFeature.identifier);
            }

            // 特殊物品按普通 id 对待：条目 Id 直接用本体（剥掉 _used 等后缀），
            // 来源反查 / 求购 / 用途 / 合成全部按本体 id 走，不再有特殊 id。
            // 拿取时按登记的生成别名调 ItemSpawner.Spawn（见 PreBuiltItems.SpawnId）。
            string baseId = RecipeWindow.BaseItemId(id) ?? id;

            var entry = new ItemEntry
            {
                Id = baseId,
                Name = BaseEntryName(baseId),
                Item = item
            };

            PreBuiltItems.RegisterSpawnId(id);

            _preBuiltEntries[id] = entry;
            return entry;
        }

        /// <summary>本体 id 的显示名：直接取本体条目的名字。</summary>
        private static string BaseEntryName(string baseId)
        {
            var baseEntry = RecipeIndex.FindEntry(baseId);
            return baseEntry != null ? baseEntry.DisplayName : baseId;
        }

        /// <summary>取格子里真正接收射线的那个子物体（Image 通常挂在子节点上）。</summary>
        private static GameObject HitGraphic(CustomUIElement element)
        {
            if (element == null || element.Rect == null) return null;

            var images = element.Rect.GetComponentsInChildren<UnityEngine.UI.Image>(true);
            for (int i = 0; i < images.Length; i++)
            {
                if (images[i] == null) continue;
                images[i].raycastTarget = true;
                return images[i].gameObject;
            }

            return element.Rect.gameObject;
        }

        /// <summary>外部窗口翻页 / 重绘前调用：清掉已销毁格子的登记与提示框。</summary>
        internal static void ResetExternalCells()
        {
            _externalCells.Clear();
            _preBuiltEntries.Clear();
            HideNativeTooltip();
        }

        /// <summary>
        /// 给格子挂右键：右键看「去处」（它作为材料的配方 / 谁收购它）。
        /// 左键仍走 CustomUI 自己的 OnClick（看来源）。
        /// </summary>
        private static void AttachRightClick(GameObject go, ItemEntry entry)
        {
            if (go == null) return;

            try
            {
                var trigger = go.GetComponent<UnityEngine.EventSystems.EventTrigger>();
                if (trigger == null) trigger = go.AddComponent<UnityEngine.EventSystems.EventTrigger>();

                var click = new UnityEngine.EventSystems.EventTrigger.Entry
                {
                    eventID = UnityEngine.EventSystems.EventTriggerType.PointerClick
                };

                click.callback.AddListener(delegate (UnityEngine.EventSystems.BaseEventData data)
                {
                    var pointer = data as UnityEngine.EventSystems.PointerEventData;
                    if (pointer == null) return;
                    if (pointer.button != UnityEngine.EventSystems.PointerEventData.InputButton.Right) return;

                    if (Time.unscaledTime < _suppressClickUntil) return;
                    RecipeWindow.OpenFor(entry, true);
                });

                trigger.triggers.Add(click);
            }
            catch (Exception e)
            {
                Warn("right click: " + e.Message);
            }
        }

        private static Action MakeSelect(ItemEntry entry)
        {
            return delegate
            {
                // 刚拖拽过：这次松手不再当成点击
                if (Time.unscaledTime < _suppressClickUntil) return;

                // 点击只用来查看：打开这件物品的合成表（只读）。
                // 作弊模式不再管点击——它只管「能不能从图鉴里拖出物品」。
                RecipeWindow.OpenFor(entry);
            };
        }

        /// <summary>
        /// 按图鉴这一格的规则造一份全新实例（变体 / 纯度 / 灌装 / 去号码都一致）。
        /// 拿不到时返回 null（已经打过日志）。
        /// </summary>
        private static GameItem BuildItemForTake(ItemEntry entry)
        {
            if (entry == null) return null;

            // 特殊格子（使用页的溶解瓶）：按登记的工厂现造一只同成分的实例
            if (entry.TakeFactory != null)
            {
                GameItem made = null;
                try { made = entry.TakeFactory(); }
                catch (Exception e) { Warn("take factory '" + entry.Id + "': " + e.Message); }
                return made;
            }

            GameItem item = null;

            try { item = CreateCellItem(entry, Math.Max(0, entry.VariantIndex)); }
            catch (Exception e) { Warn("take item '" + entry.Id + "': " + e.Message); }

            // 回退分支可能把图鉴自己那份预览实例递出来——拿走它会让格子失去图标，
            // 所以同引用一律换成工厂新造的实例。
            bool shared = item != null && (ReferenceEquals(item, entry.Item) || IsBaseInstance(item, entry.Id));

            if (item == null || shared)
            {
                // 本体条目（合成条目）按登记的生成别名 Spawn——耐久随机在工厂里，
                // 直接 Spawn 本体 id 会拿到满耐久的新货
                try { item = ItemSpawner.Spawn(PreBuiltItems.SpawnId(entry.Id)); }
                catch (Exception e) { Warn("take spawn '" + entry.Id + "': " + e.Message); }
            }

            if (item == null) return null;

            ApplyItemProcessing(item, entry);

            // 来源窗口拿取：按当前客人卖的水种真灌一瓶出来
            // （`clients.csv` 的 water 列；空瓶客人不灌，主窗口没有客人也不灌）
            try { ApplyClientWater(item, entry.Id); } catch { }

            FinalizeItemSprites(item, entry);

            return item;
        }

        /// <summary>节拍器每帧调用：拖拽起手的检测。</summary>
        internal static void DragTick()
        {
            try { TickDrag(); }
            catch (Exception e) { Warn("drag tick: " + e.Message); }
        }

        private static void TickDrag()
        {
            // 松手后延迟两帧清场：先让原版 EndDrag() 落位，再销毁 dummy 里的残留
            if (_cleanupPendingFrames > 0 && --_cleanupPendingFrames == 0)
                CleanupDummyStorage();

            if (_dragging)
            {
                TickDragging();
                return;
            }

            // 图鉴窗口或详情窗口任一活着就允许起拖；详情窗口的格子也走同一套拖拽
            if ((_window == null || !_window.IsAlive) && !RecipeWindow.IsOpen)
            {
                _pressArmed = false;
                _pressEntry = null;
                return;
            }

            if (Input.GetMouseButtonDown(0))
            {
                _pressEntry = FindHoveredEntry();
                _pressPos = Input.mousePosition;
                // 只有勾选「拿取物品」才允许起拖；没勾选时按普通点击（选中查看）处理
                _pressArmed = _pressEntry != null && _takeMode;
                return;
            }

            if (!Input.GetMouseButton(0))
            {
                _pressArmed = false;
                return;
            }

            if (!_pressArmed || _pressEntry == null) return;

            var now = (Vector2)Input.mousePosition;
            if ((now - _pressPos).sqrMagnitude < DragStartPixels * DragStartPixels) return;

            var entry = _pressEntry;
            _pressArmed = false;
            _pressEntry = null;
            _suppressClickUntil = Time.unscaledTime + 0.35f;

            BeginDrag(entry);
        }

        /// <summary>拖起：物品先寄存在图鉴自己的窗口里，再交给原版拖拽。</summary>
        /// <summary>
        /// 详情窗口的机器面板用：作弊模式下按 id 走图鉴的拿取流程，
        /// 复制一份物品交给原版拖拽；预览里的原样本不动，继续轮播。
        /// </summary>
        internal static void BeginTakeDrag(GameItem sample)
        {
            if (!TakeMode || _dragging || sample == null) return;

            GameItem copy = null;
            try { copy = DirectoryMaster.Item(sample.identifier, false); }
            catch (Exception e) { Warn("take copy '" + sample.identifier + "': " + e.Message); }
            if (copy == null) return;

            try { GeneralHelper.SetItemOwned(copy, true); } catch { }

            // 药品这类成分随机的物品：拿取统一用图鉴展示的实验室纯级（100% 活性物质）
            try
            {
                if (IsFullPurityItem(copy.identifier))
                    ApplyFullPurity(copy, copy.identifier);
            }
            catch (Exception e) { Warn("full purity copy: " + e.Message); }

            // 容器样本（例如搅拌机里灌好成分的瓶子）要把内容物一起复制，
            // 否则拖出来只是工厂新造的空瓶。
            try
            {
                if (sample.IsTag("LIQUID_CONTAINER_TAG"))
                    CopyLiquidContents(sample, copy);
            }
            catch (Exception e) { Warn("copy container: " + e.Message); }

            try
            {
                if (sample.IsTag("INGOT_PURITY_TAG"))
                {
                    string purity = IngotPurityHelper.GetPurity(sample);
                    if (!string.IsNullOrEmpty(purity))
                    {
                        IngotPurityHelper.SetPurity(copy, purity);
                    }
                }
            }
            catch (Exception e) { Warn("copy purity: " + e.Message); }

            BeginDragItem(copy, null);
        }

        /// <summary>
        /// 把样本容器的液体内容复制到拿取副本上。
        /// 逐液体 CURRENT_PART_* 是绝对量；温度与水源特性一并复制，
        /// 否则拖出来的瓶子会变成空瓶（工厂默认就是空的）。
        /// </summary>
        private static void CopyLiquidContents(GameItem sample, GameItem copy)
        {
            if (sample == null || copy == null) return;

            try
            {
                var liquids = Liquid.Liquids;
                if (liquids == null) return;

                for (int i = 0; i < liquids.Count; i++)
                {
                    var liquid = liquids[i];
                    if (liquid == null) continue;

                    int value = sample.GetTagReadonly(liquid.currentPart).GetInt();
                    copy.ModifyTag(liquid.currentPart, delegate(TagState state)
                    {
                        state.Enable().SetInt(value);
                    }, false);
                }

                // 温度由原版容器初始化时写入；没有该标签就不复制
                if (sample.IsTag("CONTAINER_TEMP_TAG"))
                {
                    double temp = sample.GetTagReadonly("CONTAINER_TEMP_TAG").GetDouble();
                    copy.ModifyTag("CONTAINER_TEMP_TAG", delegate(TagState state)
                    {
                        state.Enable().SetDouble(temp);
                    }, false);
                }

                try { WaterFeatureHelper.UpdateWaterFeature(copy, false); } catch { }
                try { SpriteHelper.UpdateWaterContainerSprite(copy); } catch { }
            }
            catch (Exception e) { Warn("copy liquids: " + e.Message); }
        }

        private static void BeginDrag(ItemEntry entry)
        {
            if (entry == null) return;

            var item = BuildItemForTake(entry);
            if (item == null) return;

            BeginDragItem(item, entry);
        }

        /// <summary>把已经造好的物品放进 dummy 容器并交给原版拖拽。</summary>
        private static void BeginDragItem(GameItem item, ItemEntry entry)
        {
            if (item == null) return;

            string id = entry != null ? entry.Id : item.identifier;

            // 放进图鉴自己的窗口：物品因此有了真实的 parentInventory 和渲染节点，
            // 原版 StartDrag() 才拎得动
            if (!PlaceInDummyStorage(item, id))
            {
                try { item.Destroy(); } catch { }
                return;
            }

            _dragEntry = entry;
            _dragItem = item;
            _dragging = true;

            // 物品刚进窗口时渲染节点可能还没建好：先试一次，拖拽中还会重试
            _dragItemNode = BindDragItemNode(item);
            _dragItemNodeAttempts = 0;

            // 绑上了就先按「物品居中」摆一次，免得 StartDrag 的第一帧偏一下
            MoveDragWindowToMouse((Vector3)Input.mousePosition);

            _vanillaDragStarted = false;
            _vanillaDragAttempts = 0;
            TryStartVanillaDrag();

            // 原版接管了就不画幽灵图（否则会和原版的拖拽视觉重影）；
            // 没接管才用幽灵图兜底；机器面板的样本没有 ItemEntry，跳过幽灵图。
            if (!_vanillaDragStarted && entry != null) ShowGhost(entry);
        }

        /// <summary>
        /// 启动原版拖拽。物品在图鉴窗口里有渲染节点，正常情况下原版会直接接管；
        /// 万一没接管，TickDragging 里的手动落点逻辑仍然可用。
        /// </summary>
        private static void TryStartVanillaDrag()
        {
            if (_vanillaDragStarted) return;
            if (_dragItem == null) return;
            if (_vanillaDragAttempts > 0) return;   // 只尝试一次，避免每帧刷日志

            _vanillaDragAttempts++;
            _vanillaDragStarted = StartVanillaDrag(_dragItem, true);
        }

        /// <summary>我们给临时物品打的标记，松手后按它清理残留。</summary>
        private const string DragDummyTag = "CATALOG_DRAG_DUMMY";

        /// <summary>
        /// 图鉴自己的临时容器窗口：用原版工厂 DirectoryUtils.CreateInventoryWindow 现造一个
        /// （窗口 + 网格容器），和玩家的任何容器都无关。
        /// 有窗口才有渲染节点，原版拖拽才拎得动。
        /// </summary>
        private static PixelWindow _dragWindow;
        private static GameInventory _dragInventory;
        private static RectTransform _dragWindowRect;
        private static float _dragWindowNextSetup;
        private static bool _dragWindowLogged;

        /// <summary>创建这个窗口时所在的场景与商店实例；回主菜单读档后它们会变，必须重建。</summary>
        private static int _dragWindowScene = int.MinValue;
        private static EmporiumEntry _dragWindowEmporium;

        /// <summary>本次拖拽中物品自己的渲染节点（用来把物品摆在鼠标正中）。</summary>
        private static RectTransform _dragItemNode;
        private static int _dragItemNodeAttempts;

        /// <summary>我们创建并塞进 dummy 的实例；松手时统一清理，避免依赖容器枚举。</summary>
        private static readonly List<GameItem> _dummyItems = new List<GameItem>();

        /// <summary>松手后延迟几帧再清场：让原版 EndDrag() 先完成落位，避免抢跑。</summary>
        private static int _cleanupPendingFrames;


        /// <summary>
        /// 惰性创建图鉴自己的拖拽容器（原版窗口 + 网格），创建后一直可见地「藏」在屏幕外。
        /// 不能 Hide()：隐藏后渲染节点会消失，原版拖拽每帧找不到节点就会中止。
        /// </summary>
        private static GameInventory DragInventory
        {
            get
            {
                // 回主菜单再读档时，旧窗口随场景销毁了，缓存的引用全是死的 —— 必须先丢弃重建
                if (_dragInventory != null && !DragWindowUsable()) ResetDragWindow();

                if (_dragInventory != null) return _dragInventory;

                // 不在商店场景里（主菜单等）就别造窗口，等进店再说
                if (EmporiumEntry.Instance == null) return null;

                if (Time.unscaledTime < _dragWindowNextSetup) return null;   // 失败后隔一秒再试，别每帧刷屏
                _dragWindowNextSetup = Time.unscaledTime + 1f;

                try
                {
                    var made = DirectoryUtils.CreateInventoryWindow(DragGridWidth, DragGridHeight, false);
                    _dragWindow = made.Item1;
                    _dragInventory = made.Item2;
                    _dragWindowEmporium = EmporiumEntry.Instance;
                    _dragWindowScene = ActiveSceneHandle();

                    if (_dragWindow != null)
                    {
                        _dragWindow.Show();
                        _dragWindowRect = AnchorTarget.WindowRectOf(_dragWindow);
                        ParkDragWindow();
                    }

                    if (ModEntry.Log != null && !_dragWindowLogged)
                    {
                        _dragWindowLogged = true;
                        ModEntry.Log.Log("[drag] own window: window=" + (_dragWindow != null)
                            + " inv=" + (_dragInventory != null)
                            + " scene=" + _dragWindowScene
                            + " rect=" + (_dragWindowRect == null ? "none" : _dragWindowRect.rect.size.ToString()));
                    }
                }
                catch (Exception e)
                {
                    Warn("drag window create: " + e.Message);
                    _dragWindow = null;
                    _dragInventory = null;
                }

                return _dragInventory;
            }
        }

        private static int ActiveSceneHandle()
        {
            try { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle; }
            catch { return int.MinValue; }
        }

        /// <summary>缓存的窗口是否还活着：场景换过、商店实例换过、RectTransform 被销毁，都算失效。</summary>
        private static bool DragWindowUsable()
        {
            if (_dragWindow == null) return false;

            try
            {
                if (ActiveSceneHandle() != _dragWindowScene) return false;
            }
            catch { return false; }

            try
            {
                if (!ReferenceEquals(EmporiumEntry.Instance, _dragWindowEmporium)) return false;
            }
            catch { return false; }

            if (_dragWindowRect == null)
            {
                try { _dragWindowRect = AnchorTarget.WindowRectOf(_dragWindow); } catch { }
                if (_dragWindowRect == null) return false;
            }

            return true;
        }

        /// <summary>丢弃缓存（场景切换 / 读档后重建）。</summary>
        private static void ResetDragWindow()
        {
            _dragWindow = null;
            _dragInventory = null;
            _dragWindowRect = null;
            _dragWindowEmporium = null;
            _dragWindowScene = int.MinValue;
            _dragWindowNextSetup = 0f;
            _dragItemNode = null;
            _dragItemNodeAttempts = 0;
            _dragWindowLogged = false;
            _dummyItems.Clear();

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[drag] own window reset (scene=" + ActiveSceneHandle() + ")");
        }

        private const int DragGridWidth = 8;      // 够大即可：物品只是临时借住
        private const int DragGridHeight = 8;

        /// <summary>把我们的窗口挪到屏幕外并透明化：不挡视线、不挡点击。</summary>
        private static void ParkDragWindow()
        {
            try
            {
                if (_dragWindowRect == null && _dragWindow != null) _dragWindowRect = AnchorTarget.WindowRectOf(_dragWindow);
                if (_dragWindowRect == null) return;

                _dragWindowRect.position = new Vector3(-20000f, -20000f, 0f);

                var group = _dragWindowRect.GetComponent<CanvasGroup>();
                if (group == null) group = _dragWindowRect.gameObject.AddComponent<CanvasGroup>();
                group.alpha = 0f;
                group.blocksRaycasts = true;    // 原版射线需要能命中
                group.interactable = false;
            }
            catch (Exception e) { Warn("drag window park: " + e.Message); }
        }

        /// <summary>
        /// 把窗口挪到鼠标处：优先按「物品自己的渲染节点居中」来对齐，
        /// 这样物品正好压在光标上；拿不到节点才退化成窗口中心对齐。
        /// </summary>
        private static void MoveDragWindowToMouse(Vector3 mouse)
        {
            if (_dragWindowRect == null && _dragWindow != null) _dragWindowRect = AnchorTarget.WindowRectOf(_dragWindow);
            if (_dragWindowRect == null) return;

            try
            {
                // 物品节点在窗口里，它的矩形中心才是「物品中心」
                var node = _dragItemNode;
                if (node != null)
                {
                    var itemCenter = node.TransformPoint(node.rect.center);
                    _dragWindowRect.position += (mouse - itemCenter);
                    return;
                }

                // 退化：窗口中心对齐（窗口 RectTransform 可能 pivot 在左上、尺寸为 0，
                // 所以这里只能保证不跑飞，真正的居中要靠上面的节点对齐）
                var center = _dragWindowRect.TransformPoint(_dragWindowRect.rect.center);
                _dragWindowRect.position += (mouse - center);
            }
            catch (Exception e) { Warn("drag window move: " + e.Message); }
        }

        /// <summary>
        /// 找到这件物品自己的渲染节点（TreeNodeRender.element == item）。
        /// 渲染元素可能晚一帧生成，所以拖拽开头会重试几次。
        /// </summary>
        private static RectTransform BindDragItemNode(GameItem item)
        {
            if (item == null) return null;

            try
            {
                var type = typeof(TreeNodeRender);
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                var fEl = type.GetField("<element>k__BackingField", F) ?? type.GetField("element", F);
                var pRt = type.GetProperty("rectTransform", F);
                if (fEl == null || pRt == null) return null;

                var all = UnityEngine.Resources.FindObjectsOfTypeAll<TreeNodeRender>();
                for (int i = 0; i < all.Length; i++)
                {
                    var node = all[i];
                    if (node == null) continue;

                    object el;
                    try { el = fEl.GetValue(node); } catch { continue; }
                    if (!ReferenceEquals(el, item)) continue;

                    var rt = pRt.GetValue(node, null) as RectTransform;
                    if (rt != null) return rt;
                }
            }
            catch (Exception e) { Warn("drag item node: " + e.Message); }

            return null;
        }

        /// <summary>清理 / 放置范围就是图鉴自己的临时容器，玩家容器一概不碰。</summary>
        private static List<GraphNodeStorage> CollectDummyStorages()
        {
            var list = new List<GraphNodeStorage>();
            var inv = DragInventory;
            if (inv != null) list.Add(inv);
            return list;
        }

        /// <summary>
        /// 把临时物品放进图鉴自己的窗口（原版工厂造的 PixelWindow + 网格容器）：
        /// 用 UncheckedAccept 按件存、不看形状，任何尺寸都能进；
        /// 玩家的隐藏储藏 / 交换缓冲 / 背包一概不碰。
        /// 物品有了合法的 parentInventory 和渲染节点，原版 StartDrag() 才拎得动。
        /// </summary>
        private static bool PlaceInDummyStorage(GameItem item, string id)
        {
            if (item == null) return false;

            // 取消上一次松手排队的延迟清场（否则它会在拖拽途中误删本次物品）
            _cleanupPendingFrames = 0;

            // 起手先清掉上次拖拽可能残留的临时物品
            CleanupDummyStorage();

            try { item.SetEnableDisableTag(DragDummyTag, true); } catch { }

            var inv = DragInventory;
            if (inv == null) { Warn("dummy: drag inventory unavailable"); return false; }

            bool accepted = false;
            try { accepted = inv.UncheckedAccept(item); }
            catch (Exception e) { Warn("dummy accept '" + id + "': " + e.Message); }

            bool parented = false;
            try { parented = item.parentInventory != null; } catch { }

            if (!accepted || !parented)
            {
                Warn("dummy: refused '" + id + "' accepted=" + accepted + " parent=" + parented);
                return false;
            }

            if (!_dummyItems.Contains(item)) _dummyItems.Add(item);

            // 先把窗口摆到鼠标下，物品才会就地被拎起（否则会从屏幕外飞过来）
            MoveDragWindowToMouse((Vector3)Input.mousePosition);

            if (ModEntry.Log != null)
            {
                int size = 0;
                try { size = item.GetItemSize(); } catch { }
                ModEntry.Log.Log("[drag] staged " + id + " size=" + size + " held=" + inv.childItems.Count
                    + " parent=" + parented);
            }

            return true;
        }

        /// <summary>松手时物品是否还留在图鉴的临时容器里（= 原版没搬走）。</summary>
        private static bool StillInDummy(GameItem item)
        {
            var inv = _dragInventory;
            if (item == null || inv == null) return false;

            try { return ReferenceEquals(item.parentInventory, inv); }
            catch { return false; }
        }

        /// <summary>
        /// 物品是否还留在图鉴自己的临时容器里 —— 只有这种才算「需要销毁的残留」。
        /// 落到玩家背包 / 柜台 / 展示柜 / 垃圾桶的一律保留。
        /// </summary>
        private static bool IsInAnyDummy(GameItem item)
        {
            if (item == null) return false;

            try
            {
                var parent = item.parentInventory;
                if (parent == null) return false;

                var storages = CollectDummyStorages();
                for (int i = 0; i < storages.Count; i++)
                    if (ReferenceEquals(parent, storages[i])) return true;
            }
            catch { }

            return false;
        }

        /// <summary>
        /// 松手清理：清空图鉴自己的临时容器。
        /// 已经被搬到正式容器（背包 / 柜台 / 展示柜 / 垃圾桶）的那份不动，只摘掉临时标签。
        /// 玩家的隐藏储藏 / 背包一概不参与清理。
        /// </summary>
        private static void CleanupDummyStorage()
        {
            int destroyed = 0;
            var inv = _dragInventory;

            // ① 我们跟踪的实例
            for (int i = _dummyItems.Count - 1; i >= 0; i--)
            {
                var it = _dummyItems[i];
                _dummyItems.RemoveAt(i);
                if (it == null) continue;

                if (IsInAnyDummy(it))
                {
                    try { if (inv != null) inv.Expel(it); } catch { }
                    try { it.Destroy(); destroyed++; }
                    catch (Exception e) { Warn("dummy cleanup: " + e.Message); }
                }
                else
                {
                    // 已落地到正式容器：保留物品，只去掉临时标签，别污染玩家存档
                    try { if (inv != null) inv.Expel(it); } catch { }
                    try { it.SetEnableDisableTag(DragDummyTag, false); } catch { }
                }
            }

            // ② 兜底：容器里没被跟踪到的带标签残留
            //    先快照再销毁：Destroy() 会改动 childItems，不能在遍历时直接删
            if (inv != null)
            {
                var leftovers = new List<GameItem>();
                var children = inv.childItems;
                if (children != null)
                {
                    for (int i = 0; i < children.Count; i++)
                    {
                        var it = children[i];
                        if (it == null) continue;

                        try { if (it.IsTag(DragDummyTag)) leftovers.Add(it); }
                        catch { }
                    }
                }

                for (int i = 0; i < leftovers.Count; i++)
                {
                    try { inv.Expel(leftovers[i]); } catch { }
                    try { leftovers[i].Destroy(); destroyed++; }
                    catch { }
                }
            }

            // 拖拽结束后把窗口收回屏幕外，别让它一直压在光标下挡住图鉴的点击
            ParkDragWindow();

            if (destroyed > 0 && ModEntry.Log != null)
                ModEntry.Log.Log("[dummy] cleanup destroyed=" + destroyed);
        }

        /// <summary>
        /// 把物品交给原版拖拽：原版按 lastMouseClickedKey 推进状态机，所以要先伪装「刚按了左键」，
        /// 再调用它自己的 StartDrag()。
        /// </summary>
        private static bool StartVanillaDrag(GameItem item, bool hasRenderNode)
        {
            try
            {
                var handler = ItemMouseDragHandler.current;
                if (handler == null) { Warn("drag: ItemMouseDragHandler.current null"); return false; }

                var type = typeof(ItemMouseDragHandler);
                const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

                var fItem = type.GetField("currentItem", Flags);
                var fDragging = type.GetField("isDragging", Flags);
                if (fItem == null || fDragging == null) { Warn("drag: fields missing"); return false; }

                // 字段名全部字面、类型也是字面 typeof —— 扫描器能直接看清，
                // 不会像「把 Type 当参数传给辅助函数」那样被判成 reflection-external。
                type.GetField("lastMouseClickedKey", Flags).SetValue(handler, KeyCode.Mouse0);
                type.GetField("lastMouseClickedPosition", Flags).SetValue(handler, (Vector3)Input.mousePosition);
                type.GetField("lastItem", Flags).SetValue(handler, item);
                type.GetField("lastInventory", Flags).SetValue(handler, item.parentInventory);
                type.GetField("lastSlot", Flags).SetValue(handler, item.GetIncompleteSlotInInventory());

                fItem.SetValue(handler, item);

                string note = "";
                try
                {
                    var start = type.GetMethod("StartDrag",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    if (start == null) note = "StartDrag not found";
                    else
                    {
                        start.Invoke(handler, null);
                        note = "StartDrag ok";
                    }
                }
                catch (Exception e) { note = "StartDrag threw: " + e.Message; }

                bool dragging = false;
                try { dragging = (bool)fDragging.GetValue(handler); } catch { }

                // 原版没接管：只要物品已有渲染节点，就按它自己的状态机手动置位
                // （OnEventUpdate 后续会把 currentItem.parent 的矩形挪到鼠标下）
                if (!dragging)
                {
                    if (hasRenderNode)
                    {
                        fItem.SetValue(handler, item);
                        fDragging.SetValue(handler, true);
                        dragging = true;
                        note += " (forced)";
                    }
                    else
                    {
                        note += " (no render node)";
                    }
                }

                if (ModEntry.Log != null && _dragLogs++ < 10)
                {
                    ModEntry.Log.Log("[drag] " + item.identifier + " :: " + note
                        + " isDragging=" + dragging
                        + " parent=" + (item.parentInventory != null));
                }

                return dragging;
            }
            catch (Exception e)
            {
                Warn("drag start: " + e.Message);
                return false;
            }
        }

        private static void TickDragging()
        {
            var mouse = (Vector3)Input.mousePosition;

            if (_ghostRect != null) _ghostRect.position = mouse;

            // 渲染节点可能晚一帧生成，前几帧重试绑定，绑上后物品才能精确居中在光标
            if (_dragItemNode == null && _dragItem != null && _dragItemNodeAttempts < 12)
            {
                _dragItemNodeAttempts++;
                _dragItemNode = BindDragItemNode(_dragItem);
            }

            // 窗口跟着鼠标走，物品才会贴着手，而不是从屏幕外飞过来
            MoveDragWindowToMouse(mouse);

            // 原版接管一次即可；接管失败也不影响，下面有自己的落点逻辑兜底
            TryStartVanillaDrag();

            if (Input.GetMouseButton(0)) return;   // 还按着，继续拖

            var item = _dragItem;
            var entry = _dragEntry;
            DropZone zone;
            bool hasZone = TryFindDropZone(mouse, out zone);

            EndGhost();
            _dragging = false;
            _dragEntry = null;
            _dragItem = null;
            _dragItemNode = null;
            _dragItemNodeAttempts = 0;
            _vanillaDragStarted = false;
            _vanillaDragAttempts = 0;
            _suppressClickUntil = Time.unscaledTime + 0.35f;

            // 原版拖拽是否已经把物品搬走（搬走了就不用我们再落地，也不该销毁它）
            bool vanillaMoved = item != null && !StillInDummy(item);

            if (item != null && !vanillaMoved)
            {
                // 落在容器上就搬进去；没落在任何容器上 = 取消，物品由下面的清理销毁。
                // 机器面板的样本没有 ItemEntry，手动落点依赖条目数据，跳过（交给原版拖拽）。
                if (hasZone && entry != null) MoveExistingItem(item, entry, zone);
            }

            // 松手统一清场：dummy 网格里任何临时残留都销毁；
            // 已经落到正式容器的那份会被保留，只摘掉临时标签。
            // 延迟两帧执行，避免抢在原版 EndDrag() 落位之前把物品毁掉。
            _cleanupPendingFrames = 2;
        }

        /// <summary>落点：EmporiumEntry 的哪个容器窗口 + 对应的原版搬运函数。</summary>
        private enum DropKind { Inventory, FrontCounter, BackCounter, DisplayCase, Trash }

        private struct DropZone
        {
            public AnchorTarget.WindowProp WindowProp;   // EmporiumEntry 上的哪个容器窗口
            public DropKind Kind;
        }

        private static readonly DropZone[] DropZones =
        {
            new DropZone { WindowProp = AnchorTarget.WindowProp.Inv,      Kind = DropKind.Inventory },
            new DropZone { WindowProp = AnchorTarget.WindowProp.FrontInv, Kind = DropKind.FrontCounter },
            new DropZone { WindowProp = AnchorTarget.WindowProp.BackInv,  Kind = DropKind.BackCounter },
            new DropZone { WindowProp = AnchorTarget.WindowProp.Showcase, Kind = DropKind.DisplayCase },
            new DropZone { WindowProp = AnchorTarget.WindowProp.Trash,    Kind = DropKind.Trash }
        };

        /// <summary>鼠标下的容器窗口（只认已经显示出来的窗口）。</summary>
        private static bool TryFindDropZone(Vector3 screenPoint, out DropZone zone)
        {
            zone = default(DropZone);

            var emporium = EmporiumEntry.Instance;
            if (emporium == null) return false;

            // 一次性诊断：容器窗口是 PixelWindow，看它们的矩形能不能取到
            if (!_zonesLogged && ModEntry.Log != null)
            {
                _zonesLogged = true;
                var found = new List<string>();
                for (int k = 0; k < DropZones.Length; k++)
                    if (AnchorTarget.WindowPropertyRect(emporium, DropZones[k].WindowProp) != null) found.Add(DropZones[k].WindowProp.ToString());
                ModEntry.Log.Log("[zones] rects: " + (found.Count == 0 ? "none" : string.Join(",", found.ToArray())));
            }

            for (int i = 0; i < DropZones.Length; i++)
            {
                var rect = AnchorTarget.WindowPropertyRect(emporium, DropZones[i].WindowProp);
                if (rect == null) continue;

                try
                {
                    if (!rect.gameObject.activeInHierarchy) continue;
                }
                catch { continue; }

                if (!ContainsScreenPoint(rect, screenPoint)) continue;

                zone = DropZones[i];
                return true;
            }

            return false;
        }

        private static bool ContainsScreenPoint(RectTransform rect, Vector3 screenPoint)
        {
            try
            {
                var canvas = rect.GetComponentInParent<Canvas>();
                var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                    ? canvas.worldCamera
                    : null;

                return RectTransformUtility.RectangleContainsScreenPoint(rect, screenPoint, cam);
            }
            catch { return false; }
        }

        /// <summary>
        /// 落点对应的那个库存对象（用来判断「是不是已经在目标里」）。
        /// 以前返回属性名再 `GetProperty(name)` 反射，名字是拼出来的，
        /// 会被扫描器判成 reflection-external（2026-12-10 起直接拒绝加载模组）；
        /// 现在每个分支都是字面属性访问。
        /// </summary>
        private static GameInventory ElementInventoryFor(EmporiumEntry emporium, DropKind kind)
        {
            if (emporium == null) return null;

            try
            {
                switch (kind)
                {
                    case DropKind.FrontCounter: return emporium.frontInvinvElement;
                    case DropKind.BackCounter: return emporium.backInvinvElement;
                    case DropKind.DisplayCase: return emporium.showcaseElement;
                    case DropKind.Trash: return emporium.trashInvElement;
                    default: return emporium.invElement;
                }
            }
            catch { return null; }
        }

        /// <summary>松手落地：按落点交给对应的原版搬运函数。</summary>
        private static void MoveExistingItem(GameItem item, ItemEntry entry, DropZone zone)
        {
            if (item == null) return;

            // 预构建键没有目录条目：日志里的 id 用物品自己的显示名顶上
            string logId = entry != null ? entry.Id : (item.identifier ?? "<prebuilt>");

            // 已经在这个容器里了就别再搬一次（否则可能被二次接收，凭空多出一件）
            try
            {
                var emporiumNow = EmporiumEntry.Instance;
                var target = ElementInventoryFor(emporiumNow, zone.Kind);

                if (target != null && ReferenceEquals(item.parentInventory, target))
                {
                    if (ModEntry.Log != null)
                        ModEntry.Log.Log("[drop] " + logId + " already in " + zone.Kind);

                    try { item.SetEnableDisableTag(DragDummyTag, false); } catch { }
                    return;
                }
            }
            catch { }

            try
            {
                switch (zone.Kind)
                {
                    case DropKind.FrontCounter: ModHelper.MoveItemToFrontCounter(item); break;
                    case DropKind.BackCounter: ModHelper.MoveItemToBackCounter(item); break;
                    case DropKind.DisplayCase: ModHelper.MoveItemToDisplayCase(item); break;
                    case DropKind.Trash: ModHelper.MoveItemToTrash(item); break;
                    default: ModHelper.MoveItemToInventory(item); break;
                }
            }
            catch (Exception e)
            {
                Warn("drop '" + logId + "' -> " + zone.Kind + ": " + e.Message);
                return;
            }

            if (ModEntry.Log != null)
            {
                ModEntry.Log.Log("[drop] " + logId + (entry != null ? " #" + entry.VariantIndex : "")
                    + " -> " + zone.Kind);
            }
        }

        private static void ShowGhost(ItemEntry entry)
        {
            EndGhost();

            UnityEngine.Sprite sprite = null;

            try
            {
                if (!string.IsNullOrEmpty(entry.Key)) _compositeByCell.TryGetValue(entry.Key, out sprite);
            }
            catch { }

            if (sprite == null) return;   // 没有现成图标就不画幽灵，功能不受影响

            try
            {
                // 挂在窗口根节点的父级（也就是窗口画布）上，保证幽灵图盖在窗口之上
                var parent = _window != null && _window.root != null
                    ? _window.root.transform.parent
                    : RecipeWindow.CanvasParent;

                if (parent == null) return;

                var go = new GameObject("ItemCatalogDragGhost", typeof(RectTransform), typeof(UnityEngine.UI.Image));

                var rt = go.GetComponent<RectTransform>();
                rt.SetParent(parent, false);
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(64f, 64f);
                rt.position = (Vector3)Input.mousePosition;

                var image = go.GetComponent<UnityEngine.UI.Image>();
                image.sprite = sprite;
                image.raycastTarget = false;
                image.preserveAspect = true;

                go.transform.SetAsLastSibling();

                _ghost = go;
                _ghostRect = rt;
            }
            catch (Exception e) { Warn("drag ghost: " + e.Message); }
        }

        private static void EndGhost()
        {
            try
            {
                if (_ghost != null) UnityEngine.Object.Destroy(_ghost);
            }
            catch { }

            _ghost = null;
            _ghostRect = null;
        }

        private static void CancelDrag()
        {
            EndGhost();
            _dragging = false;
            _dragEntry = null;
            _dragItem = null;
            _dragItemNode = null;
            _dragItemNodeAttempts = 0;
            _vanillaDragStarted = false;
            _vanillaDragAttempts = 0;
            _cleanupPendingFrames = 0;
            CleanupDummyStorage();   // 无论怎么中断，临时容器都不留临时物品
        }

        /// <summary>当前鼠标悬停的那一格（_hoveredEntryId 存的是 entry.Key）。</summary>
        private static ItemEntry FindHoveredEntry()
        {
            string key = _hoveredEntryId;
            if (string.IsNullOrEmpty(key)) return null;

            ItemEntry external;
            if (_externalCells.TryGetValue(key, out external) && external != null) return external;

            for (int i = 0; i < _pageEntries.Count; i++)
            {
                var e = _pageEntries[i];
                if (e != null && string.Equals(e.Key, key, StringComparison.Ordinal)) return e;
            }

            return null;
        }


        /// <summary>这个实例是不是「目录里的那份模板」（共享对象，不能直接塞进背包）。</summary>
        private static bool IsBaseInstance(GameItem item, string id)
        {
            if (item == null || string.IsNullOrEmpty(id)) return false;

            ItemEntry baseEntry;
            if (!_baseById.TryGetValue(id, out baseEntry) || baseEntry == null) return false;

            return ReferenceEquals(item, baseEntry.Item);
        }

        private static void SetText(string id, string text)
        {
            if (_window == null || !_window.IsAlive) return;

            var element = _window.Get(id);
            if (element != null) element.SetText(text);
        }

        /// <summary>供合成表窗口复用：按物品 id 取图标（命中图鉴同一份缓存）。</summary>
        internal static Sprite IconFor(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return null;

            var entry = RecipeIndex.FindEntry(itemId);
            return entry == null ? null : GetSprite(entry);
        }

        /// <summary>
        /// 详情窗口的货单格子用：容器类物品把液体覆盖层叠到图标上（和图鉴主窗口同一套管线）。
        /// 非容器 / 空瓶 / 合成失败时退回物品自己的图标，不会更差。
        /// </summary>
        internal static Sprite CompositeIconFor(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return null;

            var entry = RecipeIndex.FindEntry(itemId);
            if (entry == null) return null;

            try { EnsureCellItem(entry); }   // 灌装 + 采集覆盖层，内部幂等
            catch { }

            var baseSprite = GetSprite(entry);
            if (baseSprite == null) return null;

            try
            {
                List<ContainerOverlay> overlays;
                if (_cellOverlays.TryGetValue(entry.Key, out overlays)
                    && overlays != null && overlays.Count > 0)
                {
                    var composite = BuildCompositeSprite(entry.Key, baseSprite, overlays);
                    if (composite != null) return composite;
                }
            }
            catch { }

            return baseSprite;
        }

        // ------------------------------------------------------- 容器成分

        // 成分读取、主要成分、总体积文本统一在 Catalog/LiquidManager.cs。

        /// <summary>
        /// 读一个容器当前的液体成分：液体 identifier → 千分比（0..10000）。
        /// 原版把每种液体的占比存在 CURRENT_PART_&lt;id&gt; 标签里（9990 = 99.90%）。
        /// </summary>
        internal static Dictionary<string, int> LiquidParts(GameItem item)
        {
            return LiquidManager.Parts(item);
        }

        /// <summary>
        /// 容器的成分行。
        /// 在**来源窗口**里按当前客人算（不同客人灌的水不同，见 <see cref="BuildClientVariants"/>）；
        /// 其它地方只读实例自己的液体标签，实例里没有液体就返回 null。
        /// </summary>
        internal static string ContainerRangeText(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return null;

            // 只有来源窗口的货单格才按客人推算；拿出来的物品 / 图鉴格子 / 背包物品
            // 一律读实例自己的液体，否则纯显示用的区间会跟着物品跑。
            if (ClientComposition && !string.IsNullOrEmpty(RecipeWindow.SourceSeed))
            {
                string text;
                List<Sprite> ignored;
                BuildClientVariants(itemId, RecipeWindow.SourceWater, out text, out ignored);
                return text;
            }

            string cached;
            if (_containerRangeCache.TryGetValue(itemId, out cached)) return cached;

            string plain = BuildCompositionText(itemId);
            _containerRangeCache[itemId] = plain ?? string.Empty;
            return plain;
        }

        private static string BuildCompositionText(string itemId)
        {
            var entry = RecipeIndex.FindEntry(itemId);
            if (entry == null) return null;

            try { EnsureCellItem(entry); } catch { }
            var item = entry.Item;
            if (item == null) return null;

            var parts = LiquidParts(item);
            if (parts.Count == 0) return null;   // 实例里没有液体就不显示

            int total = 0;
            foreach (var pair in parts) total += pair.Value;
            if (total <= 0) return null;

            string majorId = null;
            int majorValue = 0;
            foreach (var pair in parts)
            {
                if (pair.Value > majorValue) { majorValue = pair.Value; majorId = pair.Key; }
            }
            if (majorId == null) return null;

            var impurities = new List<string>();
            foreach (var pair in parts)
            {
                if (pair.Key == majorId) continue;
                impurities.Add(LiquidName(pair.Key) + " " + Amount(pair.Value));
            }

            float pct = majorValue * 100f / total;
            string head = ModEntry.T("tt_ingredient") + ": " + LiquidName(majorId)
                          + " " + PercentRange(pct, pct);

            return AppendImpurityLines(head, impurities);
        }

        /// <summary>
        /// 「杂质：」单独一行，每种杂质在它下面各占一行。
        /// 返回的字符串用 '\n' 分隔，由 `AppendLiquidInfo` 逐行 AddLine。
        /// </summary>
        private static string AppendImpurityLines(string head, List<string> impurities)
        {
            if (impurities == null || impurities.Count == 0) return head;

            var sb = new System.Text.StringBuilder(head);
            sb.Append('\n').Append(ModEntry.T("tt_impurity")).Append(':');

            for (int i = 0; i < impurities.Count; i++)
            {
                sb.Append('\n').Append(impurities[i]);
            }

            return sb.ToString();
        }

        /// <summary>
        /// `CURRENT_PART_*` 是**绝对量**（各液体加起来等于容器容量，单位 1/1000 ml），
        /// 不是千分比 —— 以前直接 /100 才会出现 306% 这种数。杂质按绝对量展示。
        /// </summary>
        private static string Amount(int thousandths)
        {
            if (thousandths <= 0) return "0ml";

            float ml = thousandths / 1000f;

            // 小于 1ml 的多留位；再小就换微升（1 千分位 = 1µl），
            // 否则滴管这类小容器会全被舍成 0ml。
            if (ml >= 1f) return ml.ToString("0.#") + "ml";
            if (ml >= 0.01f) return ml.ToString("0.##") + "ml";
            return thousandths + "µl";
        }

        /// <summary>主成分的百分比区间；单值就只写一个。</summary>
        private static string PercentRange(float lo, float hi)
        {
            if (hi <= 0f) return string.Empty;
            if (lo <= 0f || lo > hi) lo = hi;

            if (hi - lo < 0.005f) return hi.ToString("0.#") + "%";
            return lo.ToString("0.#") + "%~" + hi.ToString("0.#") + "%";
        }

        // ------------------------------------------- 来源窗口：按客人的成分与精灵

        /// <summary>
        /// 一个客人的容器在该页展示用的三样东西：
        /// 成分文字、空瓶图标（单独占一格、不轮播）、满瓶图标组（去重后轮播）。
        /// </summary>
        private sealed class ClientVariants
        {
            internal string Composition;
            internal List<Sprite> Sprites;
        }

        private static readonly Dictionary<string, ClientVariants> _clientVariants =
            new Dictionary<string, ClientVariants>(StringComparer.Ordinal);

        /// <summary>
        /// **只给来源窗口用**：同一件容器，不同客人卖的成分不同，所以成分与瓶身精灵按客人算。
        /// 两者都**由代码直接算出**，不采样、不造实例、不往物品里灌水：
        ///   · 瓶身 = 按 `SpriteHelper.UpdateWaterContainerSprite` 的命名规则
        ///            `<size>_<Liquid.color>_100` 从精灵字典取（灌满就是 100 档）；
        ///   · 成分 = 水占比查 `AddWater` 的 grade 档位表，杂质查对应水种的分布表。
        /// 卖空瓶还是装水，由 `water` 那一列决定：留空 = 空瓶（原版拾荒者那套把原始
        /// 容器直接上架），写了水种 = 装水（供应商那套先灌满再上架）。
        /// </summary>
        internal static void BuildClientVariants(string itemId, string water,
            out string composition, out List<Sprite> sprites)
        {
            composition = null;
            sprites = null;

            if (string.IsNullOrEmpty(itemId)) return;

            // 同一客人可能只给某几件容器灌水（供应商就是只灌 `large_bottled_water`，
            // 水壶/小瓶/量筒都是空的），所以先按物品过滤一遍水种。
            // water 列留空 = 这个客人没灌水，卖的是空瓶（LiquidManager.IsEmptyWater 走空瓶分支）。
            water = LiquidManager.WaterFor(water, itemId);

            string cacheKey = itemId + "|" + (water ?? string.Empty);

            ClientVariants cached;
            if (_clientVariants.TryGetValue(cacheKey, out cached))
            {
                composition = cached.Composition;
                sprites = cached.Sprites;
                return;
            }

            var entry = RecipeIndex.FindEntry(itemId);
            if (entry == null) return;

            try { EnsureCellItem(entry); } catch { }

            // 只有按容量灌装的容器才有「成分 + 瓶身精灵」这回事
            if (entry.Item == null || ReadIntTag(entry.Item, "LIQUID_CONTAINER_CAPACITY") <= 0) return;

            int capacity = ReadIntTag(entry.Item, "LIQUID_CONTAINER_CAPACITY");

            var fullList = new List<Sprite>();

            if (LiquidManager.IsEmptyWater(water))
            {
                // 只卖空瓶的客人（`water` 留空）：上架的是原始容器，全程不灌水，
                // 所以既没有成分，图标也得是图集里不带液体段的那张 `<size>`，
                // 不能用图鉴实例（它按客人灌过水，取出来是满瓶）。
                Sprite own = EmptyContainerIcon(entry.Id);
                if (own == null) { try { own = GetSprite(entry); } catch { } }
                if (own != null) fullList.Add(own);

                composition = null;

                var emptyPacked = new ClientVariants
                {
                    Composition = null,
                    Sprites = fullList.Count == 0 ? null : fullList
                };
                _clientVariants[cacheKey] = emptyPacked;
                sprites = emptyPacked.Sprites;

                if (ModEntry.Log != null && _compositeDiag.Add("cv:" + cacheKey))
                    ModEntry.Log.Log("[client] " + cacheKey + " empty-bottle seller, no composition");

                return;
            }

            // 装水的瓶子：精灵名 = `<size>_<colorBase>_<档位>`，其中 colorBase 由
            // **液体混色**决定 —— 混色公式照 `WaterHelper.GetLiquidContainerColor` 抄：
            //   rgb 按「份量 × max(1, colorMultiplier)」加权平均；
            // 再按 `Liquid.GetColorBase` 的四条阈值映射成 blue/dark/red。
            // 所以杂质含量的阈值就是混色落到这些 HSV 边界的点。沿该客人的水占比
            // 区间扫一遍，能变出几种颜色就出几张图。
            int pctLo, pctHi;
            LiquidManager.PurityBand(water, out pctLo, out pctHi);

            foreach (var sprite in ContainerColorSprites(entry, water, pctLo, pctHi))
                fullList.Add(sprite);

            // 取不到就退回实例本体，至少不比原来差
            if (fullList.Count == 0)
            {
                Sprite own = null;
                try { own = GetSprite(entry); } catch { }
                if (own != null) fullList.Add(own);
            }

            // 成分：水占比还是那张档位表（pctLo/pctHi 上面已经算过）
            composition = LiquidManager.WaterComposition(capacity, water);

            var packed = new ClientVariants
            {
                Composition = composition,
                Sprites = fullList.Count == 0 ? null : fullList
            };
            _clientVariants[cacheKey] = packed;

            sprites = packed.Sprites;

            if (ModEntry.Log != null && _compositeDiag.Add("cv:" + cacheKey))
            {
                string names = "-";
                for (int i = 0; i < fullList.Count; i++)
                {
                    if (fullList[i] == null) continue;
                    names = i == 0 ? fullList[i].name : names + "," + fullList[i].name;
                }

                ModEntry.Log.Log("[client] " + cacheKey
                                 + " sprite=" + names
                                 + " comp=" + (composition ?? "-"));
            }
        }

        /// <summary>
        /// 「既卖水又卖瓶」的客人，只对**确实被灌过水**的那件容器额外拆一个原始空瓶格。
        /// `water` 带 `@物品id` 限定时，没匹配上的容器本来就是空瓶，再拆一格会重复。
        /// </summary>
        internal static bool ClientHasFilledBottle(string water, string itemId)
        {
            return !LiquidManager.IsEmptyWater(LiquidManager.WaterFor(water, itemId));
        }

        /// <summary>该物品是不是按容量灌装的液体容器（决定来源页要不要拆出空瓶那一格）。</summary>
        internal static bool IsLiquidContainer(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return false;

            var entry = RecipeIndex.FindEntry(itemId);
            if (entry == null) return false;

            try
            {
                EnsureCellItem(entry);
                return entry.Item != null && ReadIntTag(entry.Item, "LIQUID_CONTAINER_CAPACITY") > 0;
            }
            catch { return false; }
        }

        private const string WaterContainerAtlas3 = "Items/water_container3";
        private const string WaterContainerAtlas4 = "Items/water_container4";

        /// <summary>
        /// 沿该客人的水占比区间扫混色，按颜色名分组，每种颜色出一张瓶身图。
        /// 一个区间内颜色名变几次就有几张 —— 这正是「杂质含量阈值」的体现：
        /// 只有混色落到 HSV 边界之外，精灵才会换。
        /// </summary>
        private static List<Sprite> ContainerColorSprites(ItemEntry entry, string water, int pctLo, int pctHi)
        {
            var list = new List<Sprite>();
            if (entry == null) return list;

            string size, atlas;
            if (!ContainerSizeAndAtlas(entry.Id, out size, out atlas)) return list;

            var dist = LiquidManager.Distribution(water);
            float lo = pctLo / 10000f;
            float hi = pctHi / 10000f;

            Sprite body = null;
            try { body = GetSprite(entry); } catch { }

            Sprite highlight = LoadContainerLayer(atlas, size + "_hl_100");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            const int Steps = 40;

            for (int i = 0; i <= Steps; i++)
            {
                float purity = lo + (hi - lo) * i / Steps;

                string hex = BlendLiquidHex(purity, dist);
                string colorBase = ColorBaseOf(hex);
                if (!seen.Add(colorBase)) continue;

                Sprite layer = LoadContainerLayer(atlas, size + "_" + colorBase + "_100");
                if (layer == null) continue;

                UnityEngine.Color tint;
                if (!UnityEngine.ColorUtility.TryParseHtmlString("#" + hex, out tint))
                    tint = UnityEngine.Color.white;
                tint.a = 1f;

                var overlays = new List<ContainerOverlay>
                {
                    new ContainerOverlay { Sprite = layer, Color = tint }
                };

                if (highlight != null)
                    overlays.Add(new ContainerOverlay { Sprite = highlight, Color = UnityEngine.Color.white });

                Sprite composed = null;

                if (body != null)
                {
                    try
                    {
                        composed = BuildCompositeSprite(entry.Key + "#lq" + colorBase, body, overlays);
                    }
                    catch { }
                }

                list.Add(composed ?? layer);

                if (ModEntry.Log != null && _compositeDiag.Add("cbase:" + entry.Id + "|" + water + "|" + colorBase))
                    ModEntry.Log.Log("[container-color] " + entry.Id + " water=" + water
                                     + " purity=" + purity.ToString("0.###")
                                     + " hex=" + hex + " base=" + colorBase
                                     + " sprite=" + layer.name);
            }

            return list;
        }

        /// <summary>
        /// 空瓶图标。不能用 `CompositeIconFor`：图鉴那份实例是按客人灌过水的，
        /// 合成出来就是满瓶。空瓶就是图集里不带液体段的 `<size>`（`normal`、`xl`、`pitcher`…）。
        /// </summary>
        internal static Sprite EmptyContainerIcon(string itemId)
        {
            var entry = RecipeIndex.FindEntry(itemId);
            if (entry == null) return null;

            string size, atlas;
            if (!ContainerSizeAndAtlas(entry.Id, out size, out atlas)) return null;

            return LoadContainerLayer(atlas, size);
        }

        /// <summary>按名字从容器图集取一层精灵，两张图集都试。</summary>
        private static Sprite LoadContainerLayer(string atlas, string name)
        {
            Sprite sprite = null;

            try { sprite = RenderHandler.TryLoadFromAtlas(atlas, name); } catch { }
            if (sprite != null) return sprite;

            try { sprite = RenderHandler.TryLoadFromAtlas(WaterContainerAtlas3, name); } catch { }
            if (sprite != null) return sprite;

            try { sprite = RenderHandler.TryLoadFromAtlas(WaterContainerAtlas4, name); } catch { }
            return sprite;
        }

        /// <summary>
        /// 照 `WaterHelper.GetLiquidContainerColor` 抄的混色：每种液体取
        /// `CURRENT_PART_*` 的量，权重 = 量 × max(1, colorMultiplier)，
        /// 对 RGB 求加权平均再转回十六进制。
        /// </summary>
        private static string BlendLiquidHex(float waterPurity, Dictionary<string, float> dist)
        {
            float total = 0f, r = 0f, g = 0f, b = 0f;

            AddLiquidColor("CURRENT_PART_WATER", waterPurity, ref total, ref r, ref g, ref b);

            if (dist != null)
            {
                float nonWater = UnityEngine.Mathf.Max(0f, 1f - waterPurity);

                foreach (var pair in dist)
                {
                    if (pair.Value <= 0f) continue;

                    AddLiquidColor(pair.Key, nonWater * pair.Value, ref total, ref r, ref g, ref b);
                }
            }

            if (total <= 0f) return "000000";

            var color = new UnityEngine.Color(r / total, g / total, b / total);
            return UnityEngine.ColorUtility.ToHtmlStringRGB(color);
        }

        private static void AddLiquidColor(string partTag, float amount,
            ref float total, ref float r, ref float g, ref float b)
        {
            if (amount <= 0f) return;

            Liquid liquid = LiquidByPart(partTag);
            if (liquid == null || string.IsNullOrEmpty(liquid.color)) return;

            UnityEngine.Color c;
            if (!UnityEngine.ColorUtility.TryParseHtmlString("#" + liquid.color, out c)) return;

            float weight = amount * UnityEngine.Mathf.Max(1f, liquid.colorMultiplier);

            r += c.r * weight;
            g += c.g * weight;
            b += c.b * weight;
            total += weight;
        }

        private static Liquid LiquidByPart(string partTag)
        {
            if (string.IsNullOrEmpty(partTag)) return null;

            try
            {
                var list = Liquid.Liquids;
                if (list == null) return null;

                for (int i = 0; i < list.Count; i++)
                {
                    var liquid = list[i];
                    if (liquid != null && liquid.currentPart == partTag) return liquid;
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// 照 `Liquid.GetColorBase` 抄的颜色名判定，阈值就这四条：
        /// v&lt;0.25 或 s&lt;0.25 → dark；hue∈[170,260] → blue；
        /// hue≤60 或 ≥330 → red；其余 → dark。
        /// </summary>
        private static string ColorBaseOf(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return "dark";

            UnityEngine.Color c;
            if (!UnityEngine.ColorUtility.TryParseHtmlString("#" + hex, out c)) return "dark";
            if (hex == "ffffff" || hex == "FFFFFF") return "blue";

            float h, s, v;
            UnityEngine.Color.RGBToHSV(c, out h, out s, out v);

            if (v < 0.25f) return "dark";
            if (s < 0.25f) return "dark";

            float hue = h * 360f;

            if (hue >= 170f && hue <= 260f) return "blue";
            if (hue <= 60f || hue >= 330f) return "red";

            return "dark";
        }

        /// <summary>
        /// identifier → 精灵名尺寸段 + 图集。照 `UpdateWaterContainerSprite`
        /// 的分支抄（那里 size 与图集编号成对出现，xl / pitcher 在 4 号图集）。
        /// </summary>
        private static bool ContainerSizeAndAtlas(string identifier, out string size, out string atlas)
        {
            switch (identifier)
            {
                case "1ml_dropper": size = "1ml"; atlas = WaterContainerAtlas3; return true;
                case "5ml_dropper": size = "5ml"; atlas = WaterContainerAtlas3; return true;
                case "25ml_cylinder": size = "25ml"; atlas = WaterContainerAtlas3; return true;
                case "50ml_cylinder": size = "50ml"; atlas = WaterContainerAtlas3; return true;
                case "100ml_cylinder": size = "100ml"; atlas = WaterContainerAtlas3; return true;
                case "mini_bottle": size = "mini"; atlas = WaterContainerAtlas3; return true;
                case "small_bottled_water": size = "small"; atlas = WaterContainerAtlas3; return true;
                case "bottled_water": size = "normal"; atlas = WaterContainerAtlas3; return true;
                case "large_bottled_water": size = "large"; atlas = WaterContainerAtlas3; return true;
                case "water_jug": size = "xl"; atlas = WaterContainerAtlas4; return true;
                case "water_pitcher": size = "pitcher"; atlas = WaterContainerAtlas4; return true;
                default: size = null; atlas = null; return false;
            }
        }

        /// <summary>
        /// 从**来源窗口**拿走物品时，按当前客人卖的水种灌一瓶。
        /// 只有来源页有客人（`RecipeWindow.SourceWater`），主窗口不灌；
        /// 空瓶客人也不灌。
        /// </summary>
        private static void ApplyClientWater(GameItem item, string itemId)
        {
            if (item == null) return;

            // 只有来源页有「哪个客人」；主窗口 / 配方页不灌（SourceSeed 被清空）。
            if (string.IsNullOrEmpty(RecipeWindow.SourceSeed)) return;

            // 与 BuildClientVariants 用同一套规则：留空 = 空瓶客人，不灌；
            // `@物品id` 没匹配上也不灌。
            string water = LiquidManager.WaterFor(RecipeWindow.SourceWater, itemId);
            if (LiquidManager.IsEmptyWater(water)) return;

            try
            {
                if (ReadIntTag(item, "LIQUID_CONTAINER_CAPACITY") <= 0) return;
            }
            catch { return; }

            FillWater(item, water);
        }

        /// <summary>
        /// 按客人卖的水种灌装。用的都是原版自己的 API ——
        /// `StoreClientList.PlaceSupplierInventory` 也是这么给供应商的货灌水的。
        /// </summary>
        private static void FillWater(GameItem item, string water)
        {
            if (item == null) return;

            switch ((water ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "pure": WaterHelper.FillWithPureWater(item); return;
                case "hq": WaterHelper.FillWithHighQualityWater(item); return;
                case "rust": WaterHelper.FillWithRustWater(item); return;
                case "ghost": WaterHelper.FillWithGhostwater(item); return;
                case "gutterflow":
                case "gutter": WaterHelper.FillWithGutterflow(item); return;
                default: WaterHelper.FillWithBaseWater(item); return;
            }
        }

        private static string LiquidName(string liquidId)
        {
            return LiquidManager.NameOf(liquidId);
        }

        /// <summary>供合成表窗口复用：按精灵真实比例算显示尺寸。</summary>
        internal static void FitIcon(Sprite sprite, float maxSide, out float width, out float height)
        {
            FitSprite(sprite, maxSide, out width, out height);
        }

        private static Sprite GetSprite(ItemEntry entry)
        {
            return GetSprite(entry, false);
        }

        /// <summary>force=true 时跳过缓存重新解析（自动刷新用来感知精灵变化）。</summary>
        private static Sprite GetSprite(ItemEntry entry, bool force)
        {
            if (entry == null) return null;

            string atlas, name;
            CurrentSpritePaths(entry, out atlas, out name);

            string cacheKey = (entry.ModId ?? "") + "|" + atlas + "|" + name + "|" + (entry.SpriteKey ?? "");

            Sprite sprite;
            if (!force && SpriteCache.TryGetValue(cacheKey, out sprite)) return sprite;

            sprite = ResolveSprite(entry);

            // 只缓存成功结果：预热阶段管理器可能还没就绪，
            // 缓存 null 会让图标永远缺图。
            if (sprite != null) SpriteCache[cacheKey] = sprite;

            if (sprite != null) _spriteHits++; else _spriteMisses++;

            if (_spriteLogged < 120 && ModEntry.Log != null)
            {
                _spriteLogged++;
                ModEntry.Log.Log(string.Format("[sprite] {0} atlas='{1}' name='{2}' key='{3}' mod='{4}' -> {5}",
                    entry.Id, atlas, name, entry.SpriteKey ?? "-",
                    entry.ModId ?? "-", sprite != null ? "ok" : "MISS"));
            }

            return sprite;
        }

        /// <summary>优先用物品实例当前的图集/精灵名（变体重掷后可能变化）。</summary>
        private static void CurrentSpritePaths(ItemEntry entry, out string atlas, out string name)
        {
            atlas = entry.SpriteAtlas ?? string.Empty;
            name = entry.SpriteName ?? string.Empty;

            var item = entry.Item;
            if (item == null) return;

            try
            {
                if (!string.IsNullOrEmpty(item.spriteAtlasPath)) atlas = item.spriteAtlasPath;
            }
            catch { }

            try
            {
                if (!string.IsNullOrEmpty(item.spritePath)) name = item.spritePath;
            }
            catch { }
        }

        /// <summary>依次尝试：mod 图集 → 原版图集(atlas,name) → 引擎统一键 → 文件加载。</summary>
        private static Sprite ResolveSprite(ItemEntry entry)
        {
            var manager = CustomUIManager.Instance;

            string atlas, name;
            CurrentSpritePaths(entry, out atlas, out name);

            try
            {
                if (!string.IsNullOrEmpty(entry.ModId) && !string.IsNullOrEmpty(name) && manager != null)
                {
                    var fromMod = manager.GetModSprite(entry.ModId, name);
                    if (fromMod != null) return fromMod;
                }

                if (!string.IsNullOrEmpty(atlas) && !string.IsNullOrEmpty(name))
                {
                    var fromAtlas = RenderHandler.TryLoadFromAtlas(atlas, name);
                    if (fromAtlas != null) return fromAtlas;
                }

                if (manager != null && !string.IsNullOrEmpty(entry.SpriteKey))
                {
                    var byKey = manager.GetSprite(entry.SpriteKey);
                    if (byKey != null) return byKey;
                }

                if (!string.IsNullOrEmpty(name))
                {
                    var single = manager == null ? null : manager.GetSprite(name);
                    if (single != null) return single;

                    var fromFile = RenderHandler.LoadFromFile(name);
                    if (fromFile != null) return fromFile;
                }
            }
            catch (Exception e)
            {
                Warn("resolve sprite '" + entry.Id + "': " + e.Message);
            }

            return null;
        }

        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text)) return "?";
            return text.Length <= 10 ? text : text.Substring(0, 9) + "…";
        }

        /// <summary>取或补上 LayoutElement，用于让某个节点吃掉剩余高度。</summary>
        private static UnityEngine.UI.LayoutElement EnsureLayoutElement(RectTransform rect)
        {
            var element = rect.GetComponent<UnityEngine.UI.LayoutElement>();
            if (element == null) element = rect.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
            return element;
        }

        /// <summary>
        /// 图片元素默认不接收射线。AddImage 把真正的 Image 放在子节点上，
        /// 所以要在子节点里找出那个 Graphic 挂 Button，并返回它作为悬停事件的宿主。
        /// </summary>
        internal static GameObject MakeClickable(CustomUIElement element, Action onClick)
        {
            if (element == null || onClick == null) return null;

            try
            {
                var rect = element.Rect;
                if (rect == null) return null;

                var root = rect.gameObject;

                UnityEngine.UI.Image image = null;
                var images = root.GetComponentsInChildren<UnityEngine.UI.Image>(true);
                for (int i = 0; i < images.Length; i++)
                {
                    if (images[i] != null) { image = images[i]; break; }
                }

                var hit = image != null ? image.gameObject : root;
                if (image != null) image.raycastTarget = true;

                var button = hit.GetComponent<UnityEngine.UI.Button>();
                if (button == null) button = hit.AddComponent<UnityEngine.UI.Button>();

                button.transition = UnityEngine.UI.Selectable.Transition.None;
                button.targetGraphic = image;
                button.onClick.AddListener(new UnityEngine.Events.UnityAction(onClick));

                return hit;
            }
            catch (Exception e)
            {
                if (_clickFailures++ < 3) Warn("make icon clickable: " + e.Message);
                return null;
            }
        }

        /// <summary>在被射线命中的物体上挂悬停事件，交给游戏的 TooltipManager 显示。</summary>
        private static void AttachHover(GameObject host, ItemEntry entry,
            bool suppressLiquid = false, bool clientComposition = false)
        {
            if (host == null || entry == null) return;

            try
            {
                var trigger = host.GetComponent<UnityEngine.EventSystems.EventTrigger>();
                if (trigger == null) trigger = host.AddComponent<UnityEngine.EventSystems.EventTrigger>();

                trigger.triggers.Clear();

                var enter = new UnityEngine.EventSystems.EventTrigger.Entry
                {
                    eventID = UnityEngine.EventSystems.EventTriggerType.PointerEnter
                };
                enter.callback.AddListener(delegate { ShowNativeTooltip(entry, host, suppressLiquid, clientComposition); });
                trigger.triggers.Add(enter);

                var exit = new UnityEngine.EventSystems.EventTrigger.Entry
                {
                    eventID = UnityEngine.EventSystems.EventTriggerType.PointerExit
                };
                exit.callback.AddListener(delegate { HideNativeTooltip(); });
                trigger.triggers.Add(exit);
            }
            catch (Exception e)
            {
                Warn("attach hover: " + e.Message);
            }
        }

        /// <summary>空瓶格悬停时不要显示成分行（它本来就没装水）。</summary>
        internal static bool SuppressLiquidInfo;

        /// <summary>
        /// 只有**来源窗口的货单格**才按客人推算成分。
        /// 从那里拿出来的物品、图鉴自己的格子、背包里的物品都走实例真实液体，
        /// 否则一个纯显示用的区间会跟着物品跑到别处去。
        /// </summary>
        internal static bool ClientComposition;

        private static void ShowNativeTooltip(ItemEntry entry, GameObject owner,
            bool suppressLiquid = false, bool clientComposition = false)
        {
            _hoveredEntryId = entry == null ? null : entry.Key;

            // 悬停时只把目录里当前的物品数据同步到探针（页面刷新可能已经换过变体），不重掷
            SyncProbeData(entry);

            // 这两个标记**不能**在本次调用结束时还原：原版提示框是下一帧才构建的，
            // 还原早了就会回落到「实例有没有液体」那条路（预览实例是空的 → 什么都不显示）。
            // 统一在 PointerExit（HideNativeTooltip）里清掉。
            SuppressLiquidInfo = suppressLiquid;
            ClientComposition = clientComposition;

            RichText rich = RebuildTooltip(entry);

            if (rich == null) return;

            // 优先走游戏的 OverlayHandler：物品类型徽章（Item Details 的补丁）挂在这条路径上
            if (ShowViaOverlay(rich, entry)) return;

            var manager = GetTooltipManager();
            if (manager == null)
            {
                Warn("[tooltip] neither OverlayHandler nor TooltipManager found");
                return;
            }

            string text = rich.richTextForeground;
            if (string.IsNullOrEmpty(text)) text = rich.nakedText;

            try
            {
                var ownerComponent = owner == null ? null : owner.GetComponent<UnityEngine.Component>();
                if (ownerComponent != null) manager.SetAndShowTooltip(text, ownerComponent);
                else manager.SetAndShowTooltip(text);
            }
            catch (Exception e)
            {
                Warn("[tooltip] show failed: " + e.Message);
            }
        }

        private static bool ShowViaOverlay(RichText rich, ItemEntry entry)
        {
            if (rich == null) return false;

            var overlay = OverlayHandler.current;
            if (overlay == null)
            {
                if (_overlayWarned++ < 1) Warn("[tooltip] OverlayHandler.current is null");
                return false;
            }

            try
            {
                // 交给跟随组件每帧按鼠标位置重画；内容每 0.25 秒重建，保证类型徽章不过期
                TooltipFollower.Begin(rich, TooltipWidthFor(entry == null ? null : entry.Item),
                    delegate { return RebuildTooltip(entry); });
                _overlayShown = true;
                return true;
            }
            catch (Exception e)
            {
                Warn("[tooltip] overlay show failed: " + e.Message);
                return false;
            }
        }

        private static void HideNativeTooltip()
        {
            _hoveredEntryId = null;

            // 悬停结束才清掉「来源格」的两个标记（提示框是下一帧构建的，不能提前还原）
            SuppressLiquidInfo = false;
            ClientComposition = false;

            TooltipFollower.End();

            // 收起 OverlayHandler 的提示框（会连带移除 Item Details 的类型徽章）
            if (_overlayShown)
            {
                _overlayShown = false;
                var overlay = OverlayHandler.current;
                if (overlay != null)
                {
                    try { overlay.HideTooltip(); }
                    catch { /* 场景切换时可能已销毁 */ }
                }
            }

            var manager = _tooltipManager;
            if (manager == null) return;

            try { manager.HideTooltip(); }
            catch { /* 场景切换时可能已销毁 */ }
        }

        /// <summary>
        /// 直接取“鼠标放在物品上”时游戏生成的那份提示框：
        /// GameItemElement.GetTooltip() → GetTooltipBasic()，内部依次调用
        /// GeneralHelper / CommonTooltipHelper / 各功能 helper，并在正确位置触发
        /// ModInternal.CreateTooltipEarly/Late，所以其它模组的追加也会一起出现。
        /// </summary>
        private static RichText TryHoverTooltip(ItemEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Id)) return null;

            try
            {
                if (_probeElement == null || !string.Equals(_probeId, entry.Key, StringComparison.Ordinal))
                {
                    DestroyProbe();

                    // 元素名用 #probe 后缀：否则会和网格里的 i:<id> 撞名，
                    // CustomUI 会把网格元素覆盖掉（日志 "already has an element ... overwriting"），
                    // 之后点击/悬停全部失效。identifier 由 CopyItemData 还原成真实 id。
                    _probeElement = new GameItemElement(entry.Id + "#probe", entry.SpriteAtlas, entry.SpriteName, null);
                    _probeId = entry.Key;

                    // 探针是空壳，名字/描述/价值/类型都来自目录里那份真实物品
                    CopyItemData(_probeElement, entry.Item);

                    // 空壳默认是“未拥有”，会让原生提示框带上未拥有标记
                    try { GeneralHelper.SetItemOwned(_probeElement, true); }
                    catch (Exception e) { LogTooltip(entry, "SetItemOwned failed: " + e.Message); }

                    // 之前判过「原生链跑不通」的条目，重建探针后仍要沿用降级配置
                    if (_tooltipDegraded.Contains(entry.Key)) StripProbeRuntimeTags(_probeElement);
                }

                var rich = ReadVanillaTooltip(entry);
                if (rich == null) { LogTooltip(entry, "hover: null RichText"); return null; }

                string text = rich.richTextForeground;
                if (string.IsNullOrEmpty(text)) text = rich.nakedText;
                if (string.IsNullOrEmpty(text)) { LogTooltip(entry, "hover: empty text"); return null; }

                LogTooltip(entry, "hover ok len=" + text.Length);

                // 采样前两条完整文本，便于核对“未拥有”这类行的来源
                if (_sampleLogged < 2 && ModEntry.Log != null)
                {
                    _sampleLogged++;
                    string flat = (rich.nakedText ?? string.Empty).Replace("\r", string.Empty).Replace("\n", " ⏎ ");
                    if (flat.Length > 700) flat = flat.Substring(0, 700) + "…";
                    ModEntry.Log.Log("[tooltip-sample] " + entry.Id + " :: " + flat);
                }

                return rich;
            }
            catch (Exception e)
            {
                LogTooltip(entry, "hover failed: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// 跑一遍原版生成链。机器类物品的专属 helper 需要真实运行时窗口/槽位，
        /// 而图鉴里只是目录给出的预览实例；GetTooltipBasic 没有 try/catch，
        /// 一旦抛异常整块提示框就没了（DNA 编辑器就是这个症状）。
        /// 首次失败时把探针的运行时标签清空再跑同一条原版链：
        /// 按 tag 分派的机器 helper 会跳过，名字 / 描述 / 价值 / 类型 /
        /// 其它模组追加的行照常生成，仍然不是模组自己拼文本。
        /// </summary>
        private static RichText ReadVanillaTooltip(ItemEntry entry)
        {
            var probe = _probeElement;
            if (probe == null) return null;

            // 已判定跑不通的条目直接用降级态跑一次。TooltipFollower 每帧重建，
            // 不能每帧都先撞一次异常再降。
            if (_tooltipDegraded.Contains(entry.Key))
            {
                try { return ReadVanillaTooltipOnce(probe); }
                catch (Exception e)
                {
                    LogTooltip(entry, "degraded chain failed: " + e.Message);
                    return null;
                }
            }

            try
            {
                return ReadVanillaTooltipOnce(probe);
            }
            catch (Exception e)
            {
                LogTooltip(entry, "vanilla chain failed: " + e.Message);
                _tooltipDegraded.Add(entry.Key);
                try
                {
                    StripProbeRuntimeTags(probe);
                    return ReadVanillaTooltipOnce(probe);
                }
                catch (Exception e2)
                {
                    LogTooltip(entry, "degraded retry failed: " + e2.Message);
                    return null;
                }
            }
        }

        private static RichText ReadVanillaTooltipOnce(GameItemElement probe)
        {
            var rich = probe.GetTooltip();
            if (rich == null || rich.isTruncated)
            {
                var advanced = probe.GetTooltipAdvanced();
                if (advanced != null) rich = advanced;
            }
            return rich;
        }

        /// <summary>
        /// 只清运行时标签，保留 identifier / 名字 / 描述 / 价值 / 类型等字段。
        /// 用 TagSystem.Clear 而不是 new，避免破坏原版 helper 对同一实例的引用。
        /// 清完补回“已拥有”，否则提示框会多一行未拥有标记。
        /// </summary>
        private static void StripProbeRuntimeTags(GameItemElement probe)
        {
            if (probe == null) return;

            try { if (probe.state != null) probe.state.Clear(); } catch { }
            try { if (probe.modifiedState != null) probe.modifiedState.Clear(); } catch { }

            // 预览实例的 contentWindow 可能只有窗口、没有初始化好的槽位 UI，
            // 降级时一并摘掉，别让别的 helper 又摸到半成品。
            try
            {
                if (_contentWindowField == null)
                    _contentWindowField = typeof(GameItem).GetField("<contentWindow>k__BackingField",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                if (_contentWindowField != null) _contentWindowField.SetValue(probe, null);
            }
            catch { }

            try { GeneralHelper.SetItemOwned(probe, true); } catch { }
        }

        /// <summary>把目录里那份真实物品的数据搬到探针元素上，否则名字会是 identifier。</summary>
        private static void CopyItemData(GameItemElement probe, GameItem source)
        {
            if (probe == null || source == null) return;

            try { probe.identifier = source.identifier; } catch { }
            try { probe.name = source.name; } catch { }
            try { probe.shortDescription = source.shortDescription; } catch { }
            try { probe.longDescription = source.longDescription; } catch { }
            try { probe.flavorText = source.flavorText; } catch { }
            try { probe.unitValue = source.unitValue; } catch { }
            try { probe.unitBaseValue = source.unitBaseValue; } catch { }
            try { probe.spriteAtlasPath = source.spriteAtlasPath; } catch { }
            try { probe.spritePath = source.spritePath; } catch { }
            try { probe.useSpriteFromMod = source.useSpriteFromMod; } catch { }

            try
            {
                probe.itemTypes = source.itemTypes == null
                    ? new List<string>()
                    : new List<string>(source.itemTypes);
            }
            catch { }

            try
            {
                probe.itemFeatures = source.itemFeatures == null
                    ? new List<ItemFeature>()
                    : new List<ItemFeature>(source.itemFeatures);
            }
            catch { }

            // 标签状态整体复制（原版标签 + 其它模组给这件物品挂的标签都在这里面）。
            // 用 TagSystem.Clone() 而不是直接共享引用，避免提示框里的改写污染图鉴数据。
            try { probe.state = source.state == null ? null : source.state.Clone(); }
            catch (Exception e) { Warn("copy state: " + e.Message); }

            try { probe.modifiedState = source.modifiedState == null ? null : source.modifiedState.Clone(); }
            catch (Exception e) { Warn("copy modifiedState: " + e.Message); }

            try { probe.effects = source.effects; }
            catch (Exception e) { Warn("copy effects: " + e.Message); }

            // 已判定「原生链跑不通」的条目：SyncProbeData 每次悬停都会重跑这里，
            // 别把运行时标签/窗口又复制回来，否则每帧重试都得先抛一次异常。
            if (_tooltipDegraded.Contains(_probeId ?? string.Empty))
            {
                StripProbeRuntimeTags(probe);
                return;
            }

            // 机器类物品（如 DNA 编辑器 plant_dna_manipulator）的原版提示框会读内嵌槽位：
            // MachineDnaManipulator.CreateMachineTooltip → PlanOperation →
            // GetOutputDiskSlot → MachineHelper.GetBodyStorage → GameItem.contentWindow.childElement。
            // 空壳探针这里为 null，GetTooltipBasic 又没有 try/catch，异常会让整块提示框消失。
            // contentWindow 的 setter 非 public，且 SetContentWindow() 会调 PixelWindow.AddParent
            // 把探针登记成窗口父物品（污染真实实例），所以只把引用写进私有 backing field。
            try
            {
                var window = source.contentWindow;
                if (window != null)
                {
                    if (_contentWindowField == null)
                        _contentWindowField = typeof(GameItem).GetField("<contentWindow>k__BackingField",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                    if (_contentWindowField != null) _contentWindowField.SetValue(probe, window);
                }
            }
            catch (Exception e) { Warn("copy contentWindow: " + e.Message); }
        }

        /// <summary>探针元素会实例化一个 UI 节点，换物品或切场景时要销毁。</summary>
        private static void DestroyProbe()
        {
            var handler = _probeElement == null ? null : _probeElement.handler;
            _probeElement = null;
            _probeId = null;

            if (handler == null) return;

            try { UnityEngine.Object.Destroy(handler.gameObject); }
            catch { /* 已经随场景销毁 */ }
        }

        /// <summary>构造该物品的原生富文本提示（供游戏的 TooltipListener 取用）。</summary>
        internal static RichText BuildRichTooltip(ItemEntry entry)
        {
            return BuildRichTooltip(entry, false);
        }

        /// <summary>
        /// 页面级刷新：窗口可见时每 1 秒刷整页图标，
        /// 随机物品顺便重掷数据，然后重新解析精灵图并就地替换，不重建整个网格。
        /// 当前悬停的那件每轮都刷，提示框会跟着变。
        /// </summary>
        internal static void AutoRefreshTick()
        {
            // 自愈：窗口已经不可见（任何关闭路径漏掉 Hide 都算）但提示框还挂着 → 收掉
            if (_overlayShown || TooltipFollower.IsActive)
            {
                bool windowVisible = (_window != null && _window.IsAlive && _window.IsVisible)
                                     || RecipeWindow.IsOpen;
                if (!windowVisible) HideNativeTooltip();
            }

            if (_window == null || !_window.IsAlive || !_window.IsVisible) return;

            // 后台探测的展开结果变了 → 重绘一次（此时已经排空，不会反复重排）
            if (_pendingRender)
            {
                _pendingRender = false;
                Render();
                return;
            }


            // 分帧建格优先：还有格子没建完就接着建，别和整页刷新抢同一帧
            if (_building)
            {
                PumpBuild(CellsPerFrame);
                return;
            }

            // 刷新一轮（每秒一次）也是跨帧走的：每帧只刷 RefreshCellsPerFrame 格
            if (_refreshing)
            {
                int total = _pageEntries.Count;
                int done = 0;
                while (_refreshCursor < total && done < RefreshCellsPerFrame)
                {
                    RefreshCellIcon(_pageEntries[_refreshCursor]);
                    _refreshCursor++;
                    done++;
                }

                if (_refreshCursor >= total) _refreshing = false;
                return;
            }

            if (Time.unscaledTime < _pageRefreshNextAt) return;

            _pageRefreshNextAt = Time.unscaledTime + PageRefreshSeconds;

            int count = _pageEntries.Count;
            if (count == 0) return;

            // 一整页在一帧里刷完要 240~430ms（机器类单格就 40~95ms），
            // 现在改成跨帧分摊：每秒发起一轮，每帧只刷 RefreshCellsPerFrame 格。
            _refreshing = true;
            _refreshCursor = 0;

            if (_refreshLogs < 3 && ModEntry.Log != null)
            {
                _refreshLogs++;
                ModEntry.Log.Log(string.Format("[refresh] page={0} icons={1} hovered='{2}'",
                    count, _pageImages.Count, _hoveredEntryId ?? "-"));
            }
        }

        private static void RefreshCellIcon(ItemEntry entry)
        {
            if (entry == null) return;

            // 变体格子（VariantIndex >= 0）是固定的一格，数据不再变，只按需刷新图标；
            // 单格条目才走下面的轮播/重掷链 —— 且只对「真的会变」的条目走（结果按 id 缓存），
            // 静态物品每秒重扫标签表是整页刷新 71ms 的主要来源。
            if (entry.VariantIndex < 0 && IsDynamicCell(entry))
            {
                // 单格轮播来源只有两类：接口注册的变体提供者、硬编码生成器表。
                if (TryCycleRegisteredVariant(entry)) { }
                else if (PrebuiltVariants.ContainsKey(entry.Id)) RerollEntryItem(entry);
            }

            // 容器液体视觉管线：默认停用，容器一律显示空容器。
            // 想要液体显示时用 ItemCatalogApi 的变体提供者接口注册，
            // 或把下面的 #if false 改为 true 恢复自动检测
            // （预灌容器 → 收口管线；id 在游戏预置生成器注册表 → 用生成器；
            //   id 与液体注册表同名（酸/碱…）→ 按同名液体灌满）。
#if false
            // 容器初始就带液体（工厂预灌的单格容器）：走与多格相同的收口管线，
            // 显示原版液体覆盖层（类型变水 → 元素管线 → 采集 → 合成）。
            // 判定全数据驱动：带容量标签 + 实际有液体 → 自动进入本管线。
            // 化学品这类「出厂即带内容物」的物品，工厂给的是空容器，但它们在
            // 游戏自己的预置生成器注册表（PreBuiltItemHelper.preBuiltItems）里
            // 有生成函数——用它生成真实实例（带正确的酸/碱/纯度），同样收口。
            // 都没有的（水瓶等真·空容器）→ 只显示空瓶；覆盖层不变时合成走缓存。
            if (entry.VariantIndex < 0 && entry.Item != null)
            {
                try
                {
                    if (ReadIntTag(entry.Item, "LIQUID_CONTAINER_CAPACITY") > 0)
                    {
                        if (!HasLiquidContent(entry.Item) && HasPrebuiltGenerator(entry.Id))
                        {
                            var generated = TrySpawnPrebuilt(entry.Id);

                            // 诊断：生成器产物有没有液体、容量/成分标签是什么
                            if (ModEntry.Log != null && _prebuiltDiag.Add(entry.Id))
                            {
                                int cap = ReadIntTag(generated, "LIQUID_CONTAINER_CAPACITY");
                                int cur = ReadIntTag(generated, "LIQUID_CONTAINER_CURRENT");
                                ModEntry.Log.Log("[prebuilt] " + entry.Id
                                                 + " -> gen=" + (generated == null ? "null" : "ok")
                                                 + " cap=" + cap + " cur=" + cur
                                                 + " liquid=" + (generated != null && HasLiquidContent(generated)));
                            }

                            if (generated != null && HasLiquidContent(generated))
                                entry.Item = generated;
                        }
                        else if (!HasLiquidContent(entry.Item) && LiquidRegistryContains(entry.Id))
                        {
                            // Item id matches a registered liquid (acid/base etc.):
                            // this item IS that chemical in bottle form - fill it.
                            FillFromLiquidRegistry(entry.Item, entry.Id);
                        }
                        else if (!HasLiquidContent(entry.Item) && ModEntry.Log != null && _prebuiltDiag.Add(entry.Id))
                        {
                            ModEntry.Log.Log("[prebuilt] " + entry.Id + " -> 无预置生成器，保持空瓶");
                        }

                        if (HasLiquidContent(entry.Item))
                            FinalizeItemSprites(entry.Item, entry);
                    }
                }
                catch { }
            }
#endif

            // 覆盖层首次采集可能因图集/生成器尚未就绪而拿到空列表（liquid_luck 这类
            // 没有容量标签、只靠修饰器函数的物品尤其明显）。给它有限次重试，
            // 拿到之后下面的合成分支就会自动生效。非容器重试几次后自然放弃。
            if (entry.Item != null && !string.IsNullOrEmpty(entry.Key)
                && !_cellOverlays.ContainsKey(entry.Key))
            {
                int tries;
                _overlayRetries.TryGetValue(entry.Key, out tries);
                if (tries < 2)
                {
                    _overlayRetries[entry.Key] = tries + 1;
                    try { CaptureContainerOverlays(entry.Item, entry.Key); } catch { }
                }
            }

            // 悬停中的那一格：先把数据同步到探针，提示框下一帧就能读到新数值
            if (string.Equals(entry.Key, _hoveredEntryId, StringComparison.Ordinal))
            {
                SyncProbeData(entry);
            }

            UnityEngine.UI.Image icon;
            if (!_pageImages.TryGetValue(entry.Key, out icon) || icon == null) return;

            // 走缓存：key 里含当前物品的图集/精灵名，变体换了才会重新解析，
            // 否则每秒重复解析（尤其是解析失败时的文件回退）会白耗性能。
            var sprite = GetSprite(entry);
            if (sprite == null) return;

            if (icon.sprite != sprite) icon.sprite = sprite;

            float w, h;
            FitSprite(sprite, IconMaxSide, out w, out h);

            var rect = icon.rectTransform;
            if (rect != null &&
                (Mathf.Abs(rect.sizeDelta.x - w) > 0.5f || Mathf.Abs(rect.sizeDelta.y - h) > 0.5f))
            {
                rect.sizeDelta = new Vector2(w, h);
            }

            // 液体容器：按原版渲染方式把主精灵与覆盖层组合成一张图，整体替换图标。
            // 单格轮播与多格通用：覆盖层每秒随轮播水源变化，合成缓存自动失效重建。
            List<ContainerOverlay> pendingOverlays = null;
            if (!string.IsNullOrEmpty(entry.Key))
                _cellOverlays.TryGetValue(entry.Key, out pendingOverlays);
            if (pendingOverlays != null && pendingOverlays.Count > 0)
            {
                var composite = BuildCompositeSprite(entry.Key, sprite, pendingOverlays);
                if (composite != null)
                {
                    if (icon.sprite != composite) icon.sprite = composite;

                    float cw, ch;
                    FitSprite(composite, IconMaxSide, out cw, out ch);
                    if (rect != null &&
                        (Mathf.Abs(rect.sizeDelta.x - cw) > 0.5f || Mathf.Abs(rect.sizeDelta.y - ch) > 0.5f))
                    {
                        rect.sizeDelta = new Vector2(cw, ch);
                    }
                }
            }
        }

        /// <summary>
        /// 这一格每秒需不需要走轮播/重掷链？结果按 id 缓存——
        /// 判定本身要遍历标签表（液体内容判定等），84 格 × 每秒一次就是整页刷新 71ms 的主要来源。
        /// 判定输入全部来自探测结果与物品模板（会话内不变），缓存是安全的。
        /// </summary>
        private static readonly Dictionary<string, bool> _dynamicCellCache =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        private static bool IsDynamicCell(ItemEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Id)) return false;

            bool cached;
            if (_dynamicCellCache.TryGetValue(entry.Id, out cached)) return cached;

            // 只有两类单格会变：接口注册的变体提供者、硬编码生成器表。
            // 不再做标签层面的探测。
            bool dynamic = ItemCatalogApi.FindVariantProvider(entry.Id) != null
                           || PrebuiltVariants.ContainsKey(entry.Id);

            _dynamicCellCache[entry.Id] = dynamic;
            return dynamic;
        }



        /// <summary>
        /// 容器精灵随液体变化：直接调原版 <see cref="SpriteHelper.UpdateWaterContainerSprite"/>。
        /// 它按容量档位（0/10/25/50/75/100）与容器尺寸（mini/small/normal/large/xl/pitcher…）
        /// 重算精灵路径并 SetSpriteAndShape（连图集一起换），水质好环也由它处理——
        /// 与游戏内背包的显示完全一致。GetSprite 按精灵路径做缓存键，水位不同自动取不同贴图。
        /// </summary>
        private static void UpdateContainerSprite(GameItem item, GameItem baseItem)
        {
            if (item == null) return;

            // 原版 GetCurrentCapacity = current*100/CAPACITY（堆栈实证的除零点）。
            // 预览物品的容量标签可能没被正确初始化——用游戏自己的
            // WaterHelper.InitLiquidContainerItem（写 CAPACITY/BASE_PRICE/DROPPER/
            // GRADUATED 全套的正版初始化，不碰 CURRENT_PART_*，不影响已灌液体）
            // 按模板参数补一遍。之前手写 ModifyTag 写标签被证明无效。
            try
            {
                if (ReadIntTag(item, "LIQUID_CONTAINER_CAPACITY") <= 0)
                {
                    int capacity = ReadIntTag(baseItem, "LIQUID_CONTAINER_CAPACITY");
                    if (capacity <= 0) capacity = 100;

                    int basePrice = ReadIntTag(baseItem, "LIQUID_CONTAINER_BASE_PRICE_TAG");
                    if (basePrice <= 0) basePrice = 1;

                    WaterHelper.InitLiquidContainerItem(item, capacity, basePrice,
                        IsTagOnEither(item, baseItem, "FILTER_CONTAINER_TAG"),
                        IsTagOnEither(item, baseItem, "GRADUATED_CONTAINER_TAG"),
                        IsTagOnEither(item, baseItem, "LIQUID_CONTAINER_DROPPER_TAG"),
                        IsTagOnEither(item, baseItem, "TRANSPARENT_CONTAINER_TAG"),
                        0);

                    if (_ensureTagLogs++ < 6)
                        Warn("re-init container: capacity=" + capacity + " basePrice=" + basePrice
                             + " (now " + ReadIntTag(item, "LIQUID_CONTAINER_CAPACITY") + ")");
                }
            }
            catch (Exception e)
            {
                if (_liquidFillWarns++ < 8) Warn("re-init container: " + e.Message);
            }

            try { SpriteHelper.UpdateWaterContainerSprite(item); }
            catch (Exception e)
            {
                // 带堆栈打出来：只有消息定位不到是哪一环除的零
                string stack = e.StackTrace;
                if (_liquidFillWarns++ < 8)
                    Warn("update container sprite: " + e.Message + "\n" + stack);
            }

            // 覆盖层管线与采集在调用方收口（FinalizeItemSprites / TryCycleLiquidFill），
            // 因为覆盖层按格子 key 区分——多格容器每一格液体不同，不能共享。
        }

        /// <summary>调用元素自己的 HandleInnateModSprite（原版换图函数结尾就是这么做的）。</summary>
        private static void ApplyElementOverlayPipeline(GameItem item)
        {
            var element = item as GameItemElement;
            if (element == null) return;

            try
            {
                var method = typeof(GameItemElement).GetMethod("HandleInnateModSprite",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly,
                    null, System.Type.EmptyTypes, null);
                if (method != null) method.Invoke(element, null);
            }
            catch { }
        }

        /// <summary>一个覆盖层：液体层/高亮层的精灵、位置、颜色与方向（对应原版 ItemSpriteModifier）。</summary>
        private sealed class ContainerOverlay
        {
            public UnityEngine.Sprite Sprite;
            public UnityEngine.Vector2 Position;
            public int Orientation;
            public bool Flipped;
            public UnityEngine.Color Color;
        }

        /// <summary>格子 key → 覆盖层列表（Finalize 时采集，Refresh 时叠加）。</summary>
        private static readonly Dictionary<string, List<ContainerOverlay>> _cellOverlays =
            new Dictionary<string, List<ContainerOverlay>>(StringComparer.Ordinal);

        /// <summary>物品 id → 成分区间文字（按原版档位规则算一次就缓存，tooltip 每帧重建也只是一次查表）。</summary>
        private static readonly Dictionary<string, string> _containerRangeCache =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// 格子 key → 覆盖层空采集的重试次数。修饰器函数依赖图集/生成器，
        /// 图鉴构建得太早时首次会拿到空列表（liquid_luck 这类没有容量标签、
        /// 只靠修饰器函数的物品尤其明显），给它几次重试机会。
        /// </summary>
        private static readonly Dictionary<string, int> _overlayRetries =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// 调用物品自己注册的修饰器函数（GameItem : GameItemFunc，Init 注册），
        /// 拿到原版为真瓶子计算的覆盖层（如 normal_blue_100 / normal_hl_100）。
        /// 空瓶时函数返回空 → 格子上不留覆盖层。
        /// </summary>
        private static void CaptureContainerOverlays(GameItem item, string key)
        {
            var list = new List<ContainerOverlay>();
            int total = 0;

            try
            {
                // 直接调用，不反射：`getItemSpriteModifierForItemFunc` 是公开属性
                // （`Func<GameItem, List<ItemSpriteModifier>>`），
                // `ItemSpriteModifier` 是公开结构体、字段全公开。
                // 之前用 `func.GetType()` / `mod.GetType()` 取成员，属于「运行时才知道的类型」，
                // 扫描器会判成 reflection-external（2026-12-10 起直接拒绝加载模组）。
                var func = item.getItemSpriteModifierForItemFunc;

                if (func == null)
                {
                    // 目录里的实例可能是空壳（没注册修饰器），换个新实例再试一次。
                    // liquid_luck 这类由工厂注册 func 的物品，空壳永远没有覆盖层。
                    try
                    {
                        var fresh = DirectoryMaster.Item(item == null ? null : item.identifier);
                        if (fresh != null && !ReferenceEquals(fresh, item))
                        {
                            func = fresh.getItemSpriteModifierForItemFunc;
                            if (func != null) item = fresh;
                        }
                    }
                    catch { }

                    if (ModEntry.Log != null && _compositeDiag.Add("ov-null:" + key))
                        ModEntry.Log.Log("[overlay] " + key + " -> no modifier func (retry="
                                         + (func != null) + ")");
                }

                if (func != null)
                {
                    var mods = func(item);

                    if (mods != null)
                    {
                        for (int i = 0; i < mods.Count; i++)
                        {
                            var mod = mods[i];
                            total++;

                            if (mod.sprite == null) continue;

                            list.Add(new ContainerOverlay
                            {
                                Sprite = mod.sprite,
                                Position = mod.position,
                                Orientation = mod.orientation,
                                Flipped = mod.isFlipped,
                                Color = mod.color
                            });
                        }
                    }
                }

                if (ModEntry.Log != null && _compositeDiag.Add("ov:" + key))
                    ModEntry.Log.Log("[overlay] " + key + " n=" + total + " kept=" + list.Count);
            }
            catch (Exception e)
            {
                // 修饰器函数是反射调用的：真正的错误裹在 TargetInvocationException 里，
                // 只看 e.Message 会得到没用的“Exception has been thrown by the target of an invocation”。
                var inner = e is TargetInvocationException && e.InnerException != null
                    ? e.InnerException
                    : e;

                if (ModEntry.Log != null && _compositeDiag.Add("ov-err:" + key))
                    ModEntry.Log.Log("[overlay] " + key + " failed: "
                                     + ": " + inner.Message);

                if (_liquidFillWarns++ < 8) Warn("capture overlays: " + inner.Message);
            }

            // 覆盖层只来自物品自己注册的修饰器函数（官方接口 set_getItemSpriteModifierForItemFunc）。
            // 没产出就保持空——图标用物品自己的精灵（无液体视觉）。
            // 想要液体视觉的 mod 自己注册修饰器函数（见测试 mod）。

            // 缓存优先：覆盖层与上次完全相同就不作废合成图——
            // 单格轮播每秒重灌一次但液位/水源不变时，避免每秒重新渲染。
            // 必须在存储之前取 existing（否则拿到的是刚存的当前列表，永远"未变化"）。
            bool unchanged = false;
            List<ContainerOverlay> existing;
            if (_cellOverlays.TryGetValue(key, out existing) && existing != null
                && existing.Count == list.Count)
            {
                unchanged = true;
                for (int i = 0; i < list.Count; i++)
                {
                    var a = existing[i];
                    var b = list[i];
                    if (a.Sprite != b.Sprite || a.Position != b.Position
                        || a.Orientation != b.Orientation || a.Flipped != b.Flipped
                        || a.Color != b.Color)
                    {
                        unchanged = false;
                        break;
                    }
                }
            }

            if (list.Count > 0) _cellOverlays[key] = list;
            else _cellOverlays.Remove(key);

            // 覆盖层真的变了才作废旧合成图（下次刷新重建）
            if (!unchanged) InvalidateComposite(key);
        }


        // prebuilt-generator diagnostic dedup
        private static readonly HashSet<string> _prebuiltDiag = new HashSet<string>(StringComparer.Ordinal);

        // composite pixel-diagnostic dedup
        private static readonly HashSet<string> _compositeDiag = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>合成贴图缓存：格子 key → 已组合的完整图标（主精灵 + 液体层 + 高光层）。</summary>
        private static readonly Dictionary<string, UnityEngine.Sprite> _compositeSprites =
            new Dictionary<string, UnityEngine.Sprite>(StringComparer.Ordinal);

        // 主缓存按「key|主精灵实例 id」分开存（主精灵换了就得重做），
        // 这个索引只服务「取当前这一格现在显示什么」，O(1) 拿到、不受主精灵变化影响。
        private static readonly Dictionary<string, UnityEngine.Sprite> _compositeByCell =
            new Dictionary<string, UnityEngine.Sprite>(StringComparer.Ordinal);

        /// <summary>覆盖层变了：把这一格所有主精灵变体的合成图一起作废。</summary>
        private static void InvalidateComposite(string key)
        {
            if (string.IsNullOrEmpty(key)) return;

            _compositeByCell.Remove(key);

            string prefix = key + "|";
            List<string> dead = null;
            foreach (var pair in _compositeSprites)
            {
                if (pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    if (dead == null) dead = new List<string>();
                    dead.Add(pair.Key);
                }
            }

            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++) _compositeSprites.Remove(dead[i]);
        }

        private static UnityEngine.Canvas _compositeCanvas;
        private static UnityEngine.Camera _compositeCamera;
        private static RectTransform _compositeRoot;
        private static GameObject _compositeStage;

        /// <summary>合成舞台专用图层（TransparentFX，游戏 UI 不用它，相机只拍这层）。</summary>
        private const int CompositeLayer = 1;

        /// <summary>合成舞台（隐藏相机 + WorldSpace 画布），1 世界单位 = 1 精灵像素。</summary>
        private static void EnsureCompositeStage()
        {
            if (_compositeCanvas != null) return;

            try
            {
                _compositeStage = new GameObject("ItemCatalog.CompositeStage");
                UnityEngine.Object.DontDestroyOnLoad(_compositeStage);
                // 舞台放到远离游戏内容的位置，相机只拍这个图层——
                // 否则 cullingMask=Everything 会把游戏场景背景一起拍进合成图
                _compositeStage.transform.position = new UnityEngine.Vector3(0f, 50000f, 0f);

                var camGo = new GameObject("cam");
                camGo.transform.SetParent(_compositeStage.transform, false);
                camGo.layer = CompositeLayer;
                _compositeCamera = camGo.AddComponent<UnityEngine.Camera>();
                _compositeCamera.orthographic = true;
                _compositeCamera.clearFlags = UnityEngine.CameraClearFlags.SolidColor;
                _compositeCamera.backgroundColor = new UnityEngine.Color(0f, 0f, 0f, 0f);
                _compositeCamera.cullingMask = 1 << CompositeLayer;
                _compositeCamera.transform.localPosition = new UnityEngine.Vector3(0f, 0f, -10f);
                // 关键：禁用相机——enabled 的相机每帧把自己的视图渲染到游戏画面上，
                // 会整屏挡住游戏背景；禁用后仍可手动调用 Render() 到 RenderTexture
                _compositeCamera.enabled = false;

                var canvasGo = new GameObject("canvas");
                canvasGo.transform.SetParent(_compositeStage.transform, false);
                canvasGo.layer = CompositeLayer;
                _compositeCanvas = canvasGo.AddComponent<UnityEngine.Canvas>();
                _compositeCanvas.renderMode = UnityEngine.RenderMode.WorldSpace;
                _compositeRoot = canvasGo.GetComponent<RectTransform>();
                _compositeRoot.localPosition = UnityEngine.Vector3.zero;

                var rootImg = canvasGo.AddComponent<UnityEngine.UI.Image>();
                rootImg.raycastTarget = false;
            }
            catch (Exception e)
            {
                if (_liquidFillWarns++ < 8) Warn("composite stage: " + e.Message);
            }
        }

        /// <summary>销毁合成舞台（切场景时释放）。</summary>
        private static void DestroyCompositeStage()
        {
            if (_compositeStage != null) { UnityEngine.Object.Destroy(_compositeStage); }
            _compositeCanvas = null;
            _compositeCamera = null;
            _compositeRoot = null;
            _compositeStage = null;
        }

        /// <summary>
        /// 按原版渲染方式组合完整图标：主精灵垫底，覆盖层按修饰器的
        /// 位置/颜色/方向/翻转叠加（全部 SetNativeSize，1:1 像素空间，无映射猜测），
        /// 相机渲染到 RenderTexture 后读回为 Sprite 缓存。
        /// </summary>
        private static UnityEngine.Sprite BuildCompositeSprite(
            string key, UnityEngine.Sprite main, List<ContainerOverlay> overlays)
        {
            if (main == null || overlays == null || overlays.Count == 0) return null;

            // 缓存键带上主精灵实例 id：主图换了一张（变体/轮播换物品）必须重合成，
            // 否则会一直返回旧图；覆盖层变化由 InvalidateComposite 负责作废。
            string cacheKey = key + "|" + main.GetInstanceID();

            UnityEngine.Sprite cached;
            if (_compositeSprites.TryGetValue(cacheKey, out cached) && cached != null)
            {
                _compositeByCell[key] = cached;
                return cached;
            }

            EnsureCompositeStage();
            if (_compositeRoot == null) return null;

            try
            {
                var rootImg = _compositeRoot.GetComponent<UnityEngine.UI.Image>();
                rootImg.sprite = main;
                rootImg.color = UnityEngine.Color.white;
                _compositeRoot.sizeDelta = new UnityEngine.Vector2(main.rect.width, main.rect.height);
                _compositeRoot.localPosition = UnityEngine.Vector3.zero;
                _compositeRoot.localRotation = UnityEngine.Quaternion.identity;
                _compositeRoot.localScale = UnityEngine.Vector3.one;

                for (int i = _compositeRoot.childCount - 1; i >= 0; i--)
                    UnityEngine.Object.Destroy(_compositeRoot.GetChild(i).gameObject);

                foreach (var ov in overlays)
                {
                    var go = new UnityEngine.GameObject("ov", typeof(UnityEngine.RectTransform));
                    go.transform.SetParent(_compositeRoot, false);
                    go.transform.localPosition = UnityEngine.Vector3.zero;
                    go.layer = CompositeLayer;

                    var img = go.AddComponent<UnityEngine.UI.Image>();
                    img.sprite = ov.Sprite;
                    img.color = ov.Color;
                    img.raycastTarget = false;
                    img.SetNativeSize();

                    var rt = (UnityEngine.RectTransform)go.transform;
                    rt.anchoredPosition = ov.Position;
                    rt.localEulerAngles = new UnityEngine.Vector3(0f, 0f, ov.Orientation * 90f);
                    rt.localScale = new UnityEngine.Vector3(ov.Flipped ? -1f : 1f, 1f, 1f);
                }

                // 取景=主精灵矩形，1:1 像素渲染（无超采样，采样过滤已对齐原版图集）
                float w = main.rect.width;
                float h = main.rect.height;
                _compositeCamera.aspect = w / h;
                _compositeCamera.orthographicSize = h / 2f;
                _compositeCamera.transform.localPosition = new UnityEngine.Vector3(0f, 0f, -10f);

                int texW = UnityEngine.Mathf.Max(4, UnityEngine.Mathf.CeilToInt(w));
                int texH = UnityEngine.Mathf.Max(4, UnityEngine.Mathf.CeilToInt(h));

                var rt2 = UnityEngine.RenderTexture.GetTemporary(texW, texH, 0,
                    UnityEngine.RenderTextureFormat.ARGB32);
                _compositeCamera.targetTexture = rt2;
                var prevActive = UnityEngine.RenderTexture.active;
                UnityEngine.RenderTexture.active = rt2;
                UnityEngine.GL.Clear(false, true, new UnityEngine.Color(0f, 0f, 0f, 0f));
                // 关键：Canvas 网格是帧末延迟重建的——同帧内新建的子 Image
                // 在相机渲染时还没有网格，拍出来是空的（黑图）。
                // ForceUpdateCanvases 立即重建网格后再 Render。
                UnityEngine.Canvas.ForceUpdateCanvases();
                _compositeCamera.Render();

                var tex = new UnityEngine.Texture2D(texW, texH,
                    UnityEngine.TextureFormat.RGBA32, false);
                tex.ReadPixels(new UnityEngine.Rect(0f, 0f, texW, texH), 0, 0);
                tex.Apply();

                // 采样方式抄原版：像素画图集用 Point 过滤（合成纹理默认 Bilinear 是发糊根源）
                tex.filterMode = main.texture != null
                    ? main.texture.filterMode
                    : UnityEngine.FilterMode.Point;

                // 诊断：采样中心与角落像素，确认合成内容非空（每 key 一条）
                if (ModEntry.Log != null && _compositeDiag.Add(key))
                {
                    var c = tex.GetPixel(texW / 2, texH / 2);
                    var e0 = tex.GetPixel(0, 0);
                    ModEntry.Log.Log("[composite] key='" + key + "' main='" + main.name
                                     + "' " + texW + "x" + texH
                                     + " center=(" + c.r.ToString("F2") + "," + c.g.ToString("F2")
                                     + "," + c.b.ToString("F2") + "," + c.a.ToString("F2") + ")"
                                     + " corner a=" + e0.a.ToString("F2"));
                }

                UnityEngine.RenderTexture.active = prevActive;
                _compositeCamera.targetTexture = null;
                UnityEngine.RenderTexture.ReleaseTemporary(rt2);

                // pixelsPerUnit 保证精灵的「世界尺寸」= 主精灵逻辑尺寸（texW/SS）
                float ppu = texW / main.rect.width;
                var sprite = UnityEngine.Sprite.Create(tex,
                    new UnityEngine.Rect(0f, 0f, texW, texH),
                    new UnityEngine.Vector2(0.5f, 0.5f), ppu);
                sprite.name = "composite_" + key;

                _compositeSprites[cacheKey] = sprite;
                _compositeByCell[key] = sprite;
                return sprite;
            }
            catch (Exception e)
            {
                if (_liquidFillWarns++ < 8) Warn("build composite: " + e.Message);
                return null;
            }
        }

        /// <summary>物品或模板任一带该标签即为真（初始化时保留容器种类标记）。</summary>

        /// <summary>物品或模板任一带该标签即为真（初始化时保留容器种类标记）。</summary>
        private static bool IsTagOnEither(GameItem item, GameItem baseItem, string tag)
        {
            try { if (item != null && item.IsTag(tag)) return true; } catch { }
            try { if (baseItem != null && baseItem.IsTag(tag)) return true; } catch { }
            return false;
        }

        /// <summary>确认日志独立配额，别和告警抢预算。</summary>
        private static int _ensureTagLogs;


        /// <summary>读一个 int 标签，读不到返回 0。</summary>
        private static int ReadIntTag(GameItem item, string tag)
        {
            if (item == null || string.IsNullOrEmpty(tag)) return 0;

            try
            {
                var state = item.GetTagReadonly(tag);
                return state == null ? 0 : state.GetInt();
            }
            catch { return 0; }
        }

        /// <summary>
        /// 优先使用模组注册的变体提供者（ItemCatalogApi.ICatalogVariantProvider）：
        /// 它明确知道「一共几种、第几种长什么样」，所以按 0..N-1 顺序播放即可。
        /// </summary>
        private static bool TryCycleRegisteredVariant(ItemEntry entry)
        {
            if (entry == null) return false;

            var provider = ItemCatalogApi.FindVariantProvider(entry.Id);
            if (provider == null) return false;

            int count;
            try { count = provider.VariantCount; }
            catch (Exception e) { Warn("variant provider count '" + entry.Id + "': " + e.Message); return false; }

            if (count <= 0) return false;   // 0 表示未知，退回自动探测

            int cursor;
            _providerCursor.TryGetValue(entry.Id, out cursor);
            _providerCursor[entry.Id] = cursor + 1;

            GameItem variant;
            try { variant = provider.CreateVariant(cursor % count); }
            catch (Exception e) { Warn("variant provider create '" + entry.Id + "': " + e.Message); return false; }

            if (variant == null) return false;

            entry.Item = variant;
            return true;
        }




        /// <summary>
        /// 该标签值能否当作「有限取值轴」：
        /// - 滤掉序列号/时间戳这类大整数（每次生成都不同，本质是连续值）；
        /// - 滤掉纯美术变体（`..._VISUAL_VARIANT_INT`），图鉴用图集贴图，多一格看不出区别。
        /// </summary>
        private static bool IsFiniteAxisCandidate(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return false;

            if (key.IndexOf("VISUAL_VARIANT", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            int number;
            if (int.TryParse(value, out number))
            {
                if (number > 9999 || number < -9999) return false;    // 序列号 / 时间戳
            }

            return true;
        }

        /// <summary>取物品的完整标签表（公开属性，可直接枚举）。</summary>
        private static Dictionary<string, TagState> SafeTagDict(GameItem item)
        {
            if (item == null) return null;

            try { return item.state == null ? null : item.state.dict; }
            catch { return null; }
        }

        /// <summary>标签的「显示值」：未启用返回 null，否则优先字符串、退回整数。</summary>
        private static string TagText(TagState state)
        {
            if (state == null) return null;

            try
            {
                if (!state.valueEnabled) return null;

                string text = state.valueString;
                if (!string.IsNullOrEmpty(text)) return text;

                return state.valueInt.ToString();
            }
            catch { return null; }
        }

        private static void RerollEntryItem(ItemEntry entry)
        {
            var fresh = CreatePreviewItem(entry.Id);
            if (fresh != null) entry.Item = fresh;
        }

        /// <summary>
        /// 在隔离的随机流里重跑一次工厂，得到一份预览实例。
        /// 注意：游戏的 RNG.* 实际调用的是 UnityEngine.Random.Range（不是 RNG.random 字段），
        /// 所以只保存/还原 Random.state 会让每次重掷都从同一状态开始、结果完全相同。
        /// 这里改成“预览流”方案：用一份独立的 Random.State 反复推进，结束时把游戏状态原样还原。
        /// </summary>
        /// <summary>
        /// 某些物品的随机性不在目录工厂里，而在 PreBuiltItemHelper 的预置生成器里：
        /// 香烟的正品/赝品由 SetGenuine / SetConterfeit 在生成时决定，模块的随机词条由
        /// InitRandomEffect 写入。工厂只能给出中性版本，所以这里按 id 映射到正确的生成器。
        /// 有多个状态的（香烟 4 种、食品券 2 种）做成列表轮换，遍历即覆盖全部可能。
        /// </summary>
        private static readonly Dictionary<string, Func<GameItem>[]> PrebuiltVariants =
            new Dictionary<string, Func<GameItem>[]>(StringComparer.Ordinal)
            {
                { "ribwich", new Func<GameItem>[] { PreBuiltItemHelper.CreateRibwichRandom } },
                { "system_module_performance", new Func<GameItem>[] { PreBuiltItemHelper.RandomPerformanceModule } },
                { "system_module_efficiancy", new Func<GameItem>[] { PreBuiltItemHelper.RandomEfficiencyModule } },
                { "system_module_quality", new Func<GameItem>[] { PreBuiltItemHelper.RandomQualityModule } },
                { "system_module_overclock", new Func<GameItem>[] { PreBuiltItemHelper.RandomOverclockModule } },
                { "system_module_eco", new Func<GameItem>[] { PreBuiltItemHelper.RandomEcoModule } },
                { "system_module_fineness", new Func<GameItem>[] { PreBuiltItemHelper.RandomFinenessModule } },
                { "node_medium", new Func<GameItem>[] { PreBuiltItemHelper.RandomNode } }
            };


        private static readonly Dictionary<string, int> _prebuiltCursor =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>刮刮乐：只保留一张（奖品随机，不是有限可枚举的状态集）。</summary>
        private static bool IsScratcherEntry(ItemEntry entry)
        {
            if (entry == null || entry.Item == null) return false;

            try { return entry.Item.IsTag("SCRATCHER_TAG"); }
            catch { return false; }
        }

        /// <summary>
        /// 模块的效果池：ModuleEffectHelper.moduleEffects 是游戏公开的有限集合
        /// （效果 id → 效果定义）。物品是不是模块，由「游戏自己的生成器产物带
        /// MODULE_EFFECT 标签」判定，不写死 id 清单。不是模块返回 null。
        /// </summary>
        /// <summary>模块格子诊断日志的条数上限（避免刷屏）。</summary>
        private static int _moduleCellLogs;

        /// <summary>
        /// 模块格子诊断：形状 / 效果 / 精灵路径是否加载得到 / 效果标签 / 学习进度标签。
        /// 一次会话最多 80 条。
        /// </summary>
        private static void LogModuleCell(ItemEntry entry, int index, GameItem module, string shape, ModuleEffectCellInfo cell)
        {
            if (ModEntry.Log == null || _moduleCellLogs >= 80) return;
            _moduleCellLogs++;

            string atlas = null, sprite = null;
            try { atlas = module.spriteAtlasPath; } catch { }
            try { sprite = module.spritePath; } catch { }

            bool loads = false;
            try { loads = RenderHandler.TryLoadFromAtlas(atlas, sprite) != null; } catch { }

            ModEntry.Log.Log("[module-cell] " + entry.Id + " #" + index
                + " shape=" + (shape ?? "-")
                + " fx=" + (cell.Effect ?? "<none>")
                + " bonus=" + cell.Bonus
                + " sprite=" + (atlas ?? "-") + "/" + (sprite ?? "-")
                + " loads=" + loads
                + " tag1=" + ReadStringTag(module, "MODULE_EFFECT1_TAG")
                + " tag2=" + ReadStringTag(module, "MODULE_EFFECT2_TAG")
                + " base=" + ReadIntTag(module, "BONUS_PERCENTAGE_PERFORMANCE_INT")
                + "/" + ReadIntTag(module, "BONUS_PERCENTAGE_EFFICIENCY_INT")
                + "/" + ReadIntTag(module, "BONUS_PERCENTAGE_QUALITY_INT")
                + " temp=" + ReadIntTag(module, "TEMP_PERCENTAGE_PERFORMANCE_INT")
                + "/" + ReadIntTag(module, "TEMP_PERCENTAGE_EFFICIENCY_INT")
                + "/" + ReadIntTag(module, "TEMP_PERCENTAGE_QUALITY_INT")
                + " max=" + ReadIntTag(module, "LEARNING_ALGO_MAX_PERFORMANCE_INT")
                + "/" + ReadIntTag(module, "LEARNING_ALGO_MAX_EFFICIENCY_INT")
                + "/" + ReadIntTag(module, "LEARNING_ALGO_MAX_QUALITY_INT"));
        }

        /// <summary>读一个 string 标签，读不到返回 "-"。</summary>
        private static string ReadStringTag(GameItem item, string tag)
        {
            if (item == null || string.IsNullOrEmpty(tag)) return "-";

            try
            {
                var state = item.GetTagReadonly(tag);
                string value = state == null ? null : state.GetString();
                return string.IsNullOrEmpty(value) ? "-" : value;
            }
            catch { return "-"; }
        }

        /// <summary>瑕疵标签表与食品券面额（唯一来源：<see cref="CatalogVariantKit"/>）。</summary>
        private static readonly string[] CigaretteIssueTags = CatalogVariantKit.CigaretteIssueTags;

        private static readonly string[] InjectorIssueTags = CatalogVariantKit.InjectorIssueTags;

        private static readonly string[] StampIssueTags = CatalogVariantKit.StampIssueTags;

        private static readonly int[] StampAmounts = CatalogVariantKit.StampAmounts;

        /// <summary>每种面额要展开的状态数：1 张真券 + 每种瑕疵各 1 张假券。</summary>
        private static readonly int StampStateCount = CatalogVariantKit.StampStateCount;

        /// <summary>香烟条目（只有这一种包装进硬编码表）。</summary>
        private static bool IsCigaretteEntry(ItemEntry entry)
        {
            return entry != null && string.Equals(entry.Id, "packet_red_cigarette", StringComparison.Ordinal);
        }

        /// <summary>免疫宁注射器（Carmicheal 的 Immunivax）。</summary>
        private static bool IsInjectorEntry(ItemEntry entry)
        {
            return entry != null && string.Equals(entry.Id, "large_purple_injector", StringComparison.Ordinal);
        }

        /// <summary>食品券。</summary>
        private static bool IsStampEntry(ItemEntry entry)
        {
            return entry != null && string.Equals(entry.Id, "food_stamp", StringComparison.Ordinal);
        }

        /// <summary>
        /// 让「贴的标签」和「实际品质」一致：
        ///   0 = 正品（无瑕疵）
        ///   1..4 = 赝品，各自只带一处瑕疵（印章 / logo / 字体 / 盒色）
        /// 生成器的瑕疵是随机掷的，这里按变体固定下来。
        /// </summary>
        private static void NormalizeCigarette(GameItem item, int variant)
        {
            if (item == null) return;

            int mask = variant <= 0 ? 0 : 1 << (variant - 1);

            for (int i = 0; i < CigaretteIssueTags.Length; i++)
            {
                bool flawed = (mask & (1 << i)) != 0;

                try
                {
                    if (item.IsTag(CigaretteIssueTags[i]) != flawed)
                        item.SetEnableDisableTag(CigaretteIssueTags[i], flawed);
                }
                catch { }
            }

            // 瑕疵要落到对应的标签值上，才算「标签和品质一致」
            if ((mask & 0b0001) != 0) SetIntTag(item, "CIGARETTE_SEAL", 1);
            if ((mask & 0b0010) != 0) SetIntTag(item, "CIGARETTE_LOGO", 1);
            if ((mask & 0b0100) != 0) SetIntTag(item, "CIGARETTE_LETTERING", 1);
            if ((mask & 0b1000) != 0) SetIntTag(item, "CIGARETTE_BOX", 1);
        }

        /// <summary>图鉴里统一按「100% 活性物质」展示的药品。</summary>
        private static readonly string[] FullPurityItemIds =
        {
            "birth_control_pill",
            "caffeine_pill",
            "dream_dust",
            "nightmare_dust",
            "fanta_pill",
            "pink_alt_injector",
            "zyanide_pill"
        };

        private static int _purityLogs;

        internal static bool IsFullPurityItem(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;

            for (int i = 0; i < FullPurityItemIds.Length; i++)
            {
                if (string.Equals(FullPurityItemIds[i], id, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>
        /// 把药品的成分改成「全部为活性物质」：纯度 100%，品级随之变成 lab_pure。
        ///
        /// 配方表（ChemFinisherRecipe.chemFinisherRecipes）是按「产品 id」索引的
        /// （identifier / requiredVolume / activeIngredientId），不是按活性物质 id。
        /// 所以这里传物品自己的 id，配方自己知道该填哪种活性物质、灌多少体积。
        /// 片剂和注射剂走同一条路径。
        /// </summary>
        internal static void ApplyFullPurity(GameItem item, string id)
        {
            if (item == null || string.IsNullOrEmpty(id)) return;

            try
            {
                // 成分按纯度重排：min=max=100 → 只剩活性物质，没有填充物
                ChemFinisherRecipe.SetCompositionByPurity(item, 100, 100, id);

                // 重算 CHEMICAL_PRODUCT_PURITY_TAG 与品级（lab_pure）
                ChemicalProductHelper.GradeProduct(item);

                if (ModEntry.Log != null && _purityLogs < 12)
                {
                    _purityLogs++;
                    ModEntry.Log.Log("[purity] " + id
                        + " active=" + ReadStringTag(item, "ACTIVATE_INGREDIENT_TAG")
                        + " purity=" + ReadIntTag(item, "CHEMICAL_PRODUCT_PURITY_TAG"));
                }
            }
            catch (Exception e) { Warn("full purity '" + id + "': " + e.Message); }
        }

        /// <summary>模块物品：id 前缀固定（系统模块六种），且硬编码表里认这个 id。</summary>
        private static bool IsModuleEntry(ItemEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Id)) return false;
            if (!entry.Id.StartsWith("system_module_", StringComparison.Ordinal)) return false;

            return PrebuiltVariants.ContainsKey(entry.Id);
        }

        /// <summary>
        /// 浆果：带 BERRY_TAG 的物品。
        /// 格子条目（变体）自身 Item 是 null，要回退到基础条目去判断，
        /// 否则 CreateCellItem 里会判成「不是浆果」直接掉到回退分支。
        /// </summary>
        private static bool IsBerryEntry(ItemEntry entry)
        {
            if (entry == null) return false;

            var item = entry.Item;

            if (item == null && !string.IsNullOrEmpty(entry.Id))
            {
                ItemEntry baseEntry;
                if (_baseById.TryGetValue(entry.Id, out baseEntry) && baseEntry != null)
                    item = baseEntry.Item;
            }

            if (item == null) return false;

            try
            {
                return item.IsTag("BERRY_TAG") || item.IsTag("BERRY_CONDITION_INT");
            }
            catch { return false; }
        }

        private static int _berryCellLogs;

        /// <summary>浆果格子诊断：写入的品相 / 视觉变体、回读值、名称，一次会话最多 12 条。</summary>
        private static void LogBerryCell(ItemEntry entry, int index, GameItem berry, BerryFormInfo form, string source)
        {
            if (ModEntry.Log == null || _berryCellLogs >= 12 || form == null) return;
            _berryCellLogs++;

            bool isBerry = false;
            try { isBerry = berry.IsTag("BERRY_TAG"); }
            catch { }

            int stateId = 0, modStateId = 0;
            try { stateId = berry.state == null ? 0 : berry.state.GetHashCode(); } catch { }
            try { modStateId = berry.modifiedState == null ? 0 : berry.modifiedState.GetHashCode(); } catch { }

            ModEntry.Log.Log("[berry-cell] " + entry.Id + " #" + index
                + " src=" + (source ?? "-")
                + " form=" + form.Label
                + " set=" + form.Condition + "/" + form.Visual
                + " cond=" + ReadIntTag(berry, "BERRY_CONDITION_INT")
                + " visual=" + ReadIntTag(berry, "BERRY_VISUAL_VARIANT_INT")
                + " tag=" + isBerry
                + " st=" + stateId + "/" + modStateId
                + " name=" + (berry.name ?? "-"));
        }

        /// <summary>
        /// 造一个干净的模块：直接取目录模板（全新实例），不带形状后缀、不带随机效果。
        /// 形状由调用方自己套一次——生成器产物已经套过形状，再套会往精灵名上叠后缀
        /// （module_green_2_h_c 这种加载不到的路径就是这么来的）。
        /// </summary>
        private static GameItem CreateBaseModule(string id)
        {
            GameItem module = null;

            try { module = DirectoryMaster.Item(id, true); }
            catch (Exception e) { Warn("module template '" + id + "': " + e.Message); }

            if (module == null)
            {
                // 目录里没有（部分损坏/变种模块）：退回目录工厂
                try { module = CreatePreviewItem(id); }
                catch { }
            }

            if (module != null) ClearModuleEffects(module);
            return module;
        }

        /// <summary>
        /// 形状函数的副作用：每次调用都会往精灵名后面追加形状后缀
        /// （module_green_2 → module_green_2_h → module_green_2_h_c），
        /// 叠出来的名字在图集里不存在 → 图标丢失。
        /// 这里逐层剥掉尾部后缀，直到精灵能加载为止。
        /// </summary>
        private static void NormalizeShapeSprite(GameItem module)
        {
            if (module == null) return;

            for (int guard = 0; guard < 4; guard++)
            {
                string atlas, sprite;
                try
                {
                    atlas = module.spriteAtlasPath;
                    sprite = module.spritePath;
                }
                catch { return; }

                if (string.IsNullOrEmpty(sprite)) return;
                if (RenderHandler.TryLoadFromAtlas(atlas, sprite) != null) return;

                int cut = sprite.LastIndexOf('_');
                if (cut <= 0) return;

                try { module.SetSpriteAndShape(atlas, sprite.Substring(0, cut)); }
                catch { return; }
            }
        }

        /// <summary>从「性能 / 效率 / 品质」三元组里取指定属性。</summary>
        private static int StatOf(int[] stats, string stat)
        {
            if (stats == null || stats.Length < 3) return 0;

            if (string.Equals(stat, "PERFORMANCE", StringComparison.Ordinal)) return stats[0];
            if (string.Equals(stat, "EFFICIENCY", StringComparison.Ordinal)) return stats[1];

            return stats[2];
        }

        /// <summary>id → 模块定义里的三项基础属性（InitModuleItem 写入的那份，未受效果影响）。</summary>
        private static readonly Dictionary<string, int[]> _moduleBaseStats =
            new Dictionary<string, int[]>(StringComparer.Ordinal);

        /// <summary>
        /// 读模块的「基础数值」：现造一份未被清理过的目录模板，取 BONUS_PERCENTAGE_* 后即弃。
        /// 不能读当前实例——清效果 / 挂效果都会改这些标签（自学习算法自己就把当前加成清 0）。
        /// </summary>
        private static int ModuleBaseStat(string moduleId, string stat)
        {
            if (string.IsNullOrEmpty(moduleId) || string.IsNullOrEmpty(stat)) return 0;

            int[] values;

            if (!_moduleBaseStats.TryGetValue(moduleId, out values))
            {
                values = new int[3];

                try
                {
                    var template = DirectoryMaster.Item(moduleId, true);
                    if (template != null)
                    {
                        values[0] = ReadIntTag(template, "BONUS_PERCENTAGE_PERFORMANCE_INT");
                        values[1] = ReadIntTag(template, "BONUS_PERCENTAGE_EFFICIENCY_INT");
                        values[2] = ReadIntTag(template, "BONUS_PERCENTAGE_QUALITY_INT");
                    }
                }
                catch (Exception e) { Warn("module base stats '" + moduleId + "': " + e.Message); }

                _moduleBaseStats[moduleId] = values;
            }

            int own = string.Equals(stat, "PERFORMANCE", StringComparison.Ordinal) ? values[0]
                    : string.Equals(stat, "EFFICIENCY", StringComparison.Ordinal) ? values[1]
                    : values[2];

            if (own != 0) return own;

            // 自身属性为 0 的模块：退回三项里绝对值最大的那个，避免上限与进度都是 0
            int best = 0;
            for (int i = 0; i < values.Length; i++)
            {
                if (Math.Abs(values[i]) > Math.Abs(best)) best = values[i];
            }

            return best;
        }

        /// <summary>
        /// 给模块挂一个指定效果。<paramref name="bonus"/> 决定自学习进度：
        /// 0 = 零属性；-2 = 满属性（取系统上限）；-1 = 非自学习。
        /// 只写该模块自身属性对应的进度标签（别的属性清零）。
        /// </summary>
        private static void ApplyModuleEffect(GameItem module, string moduleId, string effectId, int bonus)
        {
            if (module == null || string.IsNullOrEmpty(effectId)) return;

            string stat = ModuleStatOf(moduleId);

            // 挂效果之前的三个基础值（形状函数已经按占格方式算好）。
            // 效果初始化会改写 BONUS_PERCENTAGE_*，所以先拍快照，自学习格再原样写回。
            int[] baseStats =
            {
                ReadIntTag(module, "BONUS_PERCENTAGE_PERFORMANCE_INT"),
                ReadIntTag(module, "BONUS_PERCENTAGE_EFFICIENCY_INT"),
                ReadIntTag(module, "BONUS_PERCENTAGE_QUALITY_INT")
            };

            int baseBonus = StatOf(baseStats, stat);

            if (baseBonus == 0)
            {
                // 模板被清过效果时基础值可能是 0。这里先补回模块定义值，
                // 游戏的 InitLearningAlgo 才能算出正确上限（基础 × LEARNING_ALGO_MAX_MULT）。
                int defined = ModuleBaseStat(moduleId, stat);

                if (defined != 0)
                {
                    SetIntTag(module, "BONUS_PERCENTAGE_" + stat + "_INT", defined);
                    baseBonus = defined;
                }
            }

            try { ModuleEffectHelper.AddEffectToModule(module, effectId); }
            catch (Exception e) { Warn("module effect '" + effectId + "': " + e.Message); }

            if (bonus == -1) return;
            if (!string.Equals(effectId, LearningEffectId, StringComparison.Ordinal)) return;

            // 0 属性格：就停在游戏刚装上的原始状态（加成 0、上限已由 InitLearningAlgo 写好）
            if (bonus != -2) return;

            // 满属性格：按原版行为「每次使用 +LEARNING_ALGO_GAIN_PER_USE，直到上限」一直加到满
            SimulateLearningToMax(module, stat);
        }

        /// <summary>
        /// 模拟原版的使用过程：反复调用游戏的 OnUsedLearningAlgo，
        /// 直到该属性不再增长（即到达 LEARNING_ALGO_MAX_* 上限）。
        /// </summary>
        private static void SimulateLearningToMax(GameItem module, string stat)
        {
            if (module == null) return;

            const int guard = 500;

            int last = ReadIntTag(module, "BONUS_PERCENTAGE_" + stat + "_INT");

            for (int i = 0; i < guard; i++)
            {
                try { ModuleEffectHelper.OnUsedLearningAlgo(module); }
                catch (Exception e) { Warn("learning algo use: " + e.Message); break; }

                int now = ReadIntTag(module, "BONUS_PERCENTAGE_" + stat + "_INT");
                if (now == last) break;   // 到上限（或上限为 0），停

                last = now;
            }

            // 提示框里的「当前%」读的是 TEMP_PERCENTAGE_*，
            // 而学习累加只改 BONUS_PERCENTAGE_*。用游戏自己的同步函数把 TEMP 刷成 BONUS，
            // 否则加成涨满了、提示框还是显示 0%。
            try { ModuleEffectHelper.ResetAllTempStat(module); }
            catch (Exception e) { Warn("reset temp stat: " + e.Message); }

            Warn("learning algo did not settle after " + guard + " uses ('" + stat + "')");
        }

        /// <summary>按精灵设置占格形状（SetSpriteAndShape 会按图像算出占格）。</summary>
        private static void ApplyModuleShape(GameItem module, string sprite)
        {
            if (module == null || string.IsNullOrEmpty(sprite)) return;

            try { module.SetSpriteAndShape(ModuleAtlas, sprite); }
            catch (Exception e) { Warn("module shape sprite '" + sprite + "': " + e.Message); }
        }

        /// <summary>
        /// 该模块在图集里实际存在的占格精灵（按 ModuleShapeSuffixes 顺序）。
        /// 形状来自精灵图像，所以这里只枚举「有图」的组合。
        /// </summary>
        internal static List<string> ModuleShapes(string id)
        {
            if (string.IsNullOrEmpty(id)) return new List<string>(1);

            List<string> cached;
            if (_moduleShapes.TryGetValue(id, out cached)) return cached;

            var list = new List<string>(ModuleShapeSuffixes.Length);

            string root = null;
            string fallback = null;

            try
            {
                var template = DirectoryMaster.Item(id, true);
                if (template != null)
                {
                    fallback = template.spritePath;
                    root = ModuleColorRoot(fallback);
                }
            }
            catch { }

            if (!string.IsNullOrEmpty(root))
            {
                for (int i = 0; i < ModuleShapeSuffixes.Length; i++)
                {
                    string name = root + ModuleShapeSuffixes[i];

                    bool exists = false;
                    try { exists = RenderHandler.TryLoadFromAtlas(ModuleAtlas, name) != null; }
                    catch { }

                    if (exists) list.Add(name);
                }
            }

            if (list.Count == 0 && !string.IsNullOrEmpty(fallback)) list.Add(fallback);

            _moduleShapes[id] = list;
            return list;
        }

        /// <summary>从精灵名取颜色根：module_red_2_h → module_red。</summary>
        private static string ModuleColorRoot(string sprite)
        {
            if (string.IsNullOrEmpty(sprite)) return null;
            if (!sprite.StartsWith("module_", StringComparison.Ordinal)) return null;

            int i = "module_".Length;
            while (i < sprite.Length && char.IsLetter(sprite[i])) i++;

            return i > "module_".Length ? sprite.Substring(0, i) : null;
        }

        /// <summary>模块影响的属性：基础形状 性能/效率/品质；高级形状 超频/环保/精细 也归到这三类。</summary>
        private static string ModuleStatOf(string moduleId)
        {
            switch (moduleId)
            {
                case "system_module_efficiancy":
                case "system_module_eco":
                    return "EFFICIENCY";

                case "system_module_quality":
                case "system_module_fineness":
                    return "QUALITY";

                default:
                    return "PERFORMANCE";
            }
        }

        /// <summary>写一个 int 标签（标签不存在时先启用）。</summary>
        private static void SetIntTag(GameItem item, string tag, int value)
        {
            if (item == null || string.IsNullOrEmpty(tag)) return;

            try
            {
                item.ModifyTag(tag, delegate (TagState state)
                {
                    if (state != null) state.SetInt(value);
                });
            }
            catch { }
        }

        /// <summary>清掉模块实例上的随机效果（只清这个实例，效果池不动）。</summary>
        private static void ClearModuleEffects(GameItem item)
        {
            if (item == null) return;

            string[] tags = { ModuleEffectHelper.MODULE_EFFECT1_TAG, ModuleEffectHelper.MODULE_EFFECT2_TAG };
            for (int i = 0; i < tags.Length; i++)
            {
                try
                {
                    var state = item.GetTagReadonly(tags[i]);
                    string effect = state == null ? null : state.GetString();
                    if (!string.IsNullOrEmpty(effect))
                        ModuleEffectHelper.RemoveEffectFromModule(item, effect);
                }
                catch { }
            }
        }



        private static bool TryNextPrebuilt(string id, out Func<GameItem> factory)
        {
            factory = null;

            Func<GameItem>[] variants;
            if (!PrebuiltVariants.TryGetValue(id, out variants) || variants == null || variants.Length == 0)
                return false;

            int cursor;
            _prebuiltCursor.TryGetValue(id, out cursor);
            _prebuiltCursor[id] = cursor + 1;

            factory = variants[cursor % variants.Length];
            return factory != null;
        }

        internal static GameItem CreatePreviewItem(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            var savedRng = RNG.random;
            var savedUnityState = UnityEngine.Random.state;
            var store = PlayerStore.Instance;
            int savedUniqueId = store == null ? 0 : store.currentUniqueId;

            // 第一次使用时用当前游戏状态派生自己的预览流起点
            if (!_previewStateReady)
            {
                _previewState = savedUnityState;
                _previewStateReady = true;
            }

            try
            {
                RNG.random = _previewRng;
                UnityEngine.Random.state = _previewState;   // 从预览流上次的位置继续

                // 正品/赝品、随机模块这类“随机性在生成器里而不在工厂里”的物品，
                // 走 PreBuiltItemHelper 的对应生成器，工厂只能给出中性版本。
                Func<GameItem> prebuilt;
                if (TryNextPrebuilt(id, out prebuilt))
                {
                    // 模块生成器会从 ModuleEffectHelper.moduleEffects 里「取走」词条，
                    // 直接反复调用会掏空游戏的效果池，所以前后快照/还原一次。
                    var pool = CatalogVariantKit.SnapshotModulePool();
                    try { return prebuilt(); }
                    finally { CatalogVariantKit.RestoreModulePool(pool); }
                }

                // 预构建键（portable_water_purifier_used 这类）：工厂注册在
                // ItemSpawner.Spawn 里，目录里没有这个 id，只能走 spawner
                if (PreBuiltItems.IsPreBuilt(id)) return ItemSpawner.Spawn(id);

                return DirectoryMaster.Item(id);
            }
            catch (Exception e)
            {
                Warn("preview item '" + id + "': " + e.Message);
                return null;
            }
            finally
            {
                _previewState = UnityEngine.Random.state;    // 记下预览流推进后的位置
                _previewRng = RNG.random;                    // RNG 流同样要推进，否则每次都掷出同一个值
                UnityEngine.Random.state = savedUnityState;  // 游戏随机流原样还原
                RNG.random = savedRng;
                if (store != null) store.currentUniqueId = savedUniqueId;
            }
        }

        /// <summary>
        /// 强制重建一次提示框。Item Details 的类型徽章带 0.5 秒过期判定，
        /// 必须像原版那样周期性重跑生成链，徽章才不会消失。
        /// 内容本身不重掷——数据刷新交给页面级的 AutoRefreshTick。
        /// </summary>
        internal static RichText RebuildTooltip(ItemEntry entry)
        {
            if (entry == null) return null;

            return AppendPrebuiltRange(entry, BuildRichTooltip(entry, true));
        }

        /// <summary>
        /// 预构建键的原生提示框：把实例上掷出来的那一次耐久（27/300 这种，每次悬停
        /// 都不一样、也没有信息量）整行删掉，换成「生成时的耐久范围」。
        /// 那行的原文可以直接用游戏自己的 GetRealDisplayString 算出来，删得准。
        /// </summary>
        private static RichText AppendPrebuiltRange(ItemEntry entry, RichText rich)
        {
            if (rich == null || entry == null) return rich;

            string range = PreBuiltItems.DurabilityRangeForEntry(entry.Id);
            if (range == null) return rich;

            string naked = rich.nakedText;
            string foreground = rich.richTextForeground;

            if (entry.Item != null)
            {
                string durLine = DurabilityHelper.GetRealDisplayString(entry.Item);
                if (!string.IsNullOrEmpty(durLine))
                {
                    naked = RemoveLine(naked, durLine);
                    foreground = RemoveLine(foreground, durLine);
                }
            }

            string line = "\n" + string.Format(ModEntry.T("prebuilt_durability"), range);

            return new RichText(
                naked + line,
                rich.richTextStrikethrough,
                rich.richTextUnderline,
                foreground + line,
                rich.richTextBackground,
                rich.recommendedWidth,
                rich.recommendedHeight,
                rich.isTruncated);
        }

        /// <summary>删掉文本里的某一行（连同行首 / 行尾的换行）。</summary>
        private static string RemoveLine(string text, string line)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(line)) return text;

            int i = text.IndexOf(line, StringComparison.Ordinal);
            if (i < 0) return text;

            int start = i;
            while (start > 0 && text[start - 1] == '\n') start--;

            int end = i + line.Length;
            if (end < text.Length && text[end] == '\n') end++;

            return text.Remove(start, end - start);
        }

        /// <summary>
        /// 把目录里当前的物品数据搬到探针上（悬停用），只搬字段、不重掷。
        /// 页面级刷新可能已经换过变体，这样提示框看到的就是当前那份。
        /// </summary>
        private static void SyncProbeData(ItemEntry entry)
        {
            if (entry == null || entry.Item == null) return;
            if (_probeElement == null) return;
            if (!string.Equals(_probeId, entry.Key, StringComparison.Ordinal)) return;

            CopyItemData(_probeElement, entry.Item);

            try { GeneralHelper.SetItemOwned(_probeElement, true); }
            catch { }
        }

        private static RichText BuildRichTooltip(ItemEntry entry, bool force)
        {
            if (entry == null) return null;
            if (!force && _lastRichEntry == entry && _lastRich != null) return _lastRich;

            // 统一走原版物品提示框：探针转发游戏自己的生成链路
            // （含原版内容与其它 mod 通过 OnCreateTooltip 钩子追加的内容）。
            // 图鉴不再自己拼任何行——原版/模组提供什么就显示什么。
            var hover = TryHoverTooltip(entry);
            if (hover != null)
            {
                _lastRichEntry = entry;
                _lastRich = hover;
                return hover;
            }

            return null;
        }

        private static void LogTooltip(ItemEntry entry, string result)
        {
            if (ModEntry.Log == null) return;

            string id = entry == null ? "<null>" : entry.Id;

            // 提示框每帧重建，成功日志很快就会刷满；以前成功和失败共用 12 条额度，
            // 前 12 次 hover ok 就把额度吃光，之后某件物品生成失败在日志里完全看不到。
            // 成功限量、失败独立计数并按「条目+原因」去重，保证异常始终留得下痕迹。
            bool ok = result.StartsWith("hover ok", StringComparison.Ordinal)
                      || result.StartsWith("degraded retry ok", StringComparison.Ordinal);

            if (ok)
            {
                if (_tooltipLogs >= 12) return;
                _tooltipLogs++;
            }
            else
            {
                if (!_tooltipFailKeys.Add(id + "|" + result)) return;
                if (_tooltipFailLogs >= 40) return;
                _tooltipFailLogs++;
            }

            ModEntry.Log.Log("[tooltip] " + id + " -> " + result);
        }

        private static TooltipManager GetTooltipManager()
        {
            if (_tooltipManager != null) return _tooltipManager;

            try { _tooltipManager = UnityEngine.Object.FindObjectOfType<TooltipManager>(); }
            catch (Exception e) { Warn("find TooltipManager: " + e.Message); }

            return _tooltipManager;
        }

        /// <summary>保持宽高比，把精灵塞进 maxSide 的正方形里。</summary>
        private static void FitSprite(Sprite sprite, float maxSide, out float width, out float height)
        {
            width = maxSide;
            height = maxSide;

            if (sprite == null) return;

            var rect = sprite.rect;
            if (rect.width <= 0f || rect.height <= 0f) return;

            float aspect = rect.height / rect.width;
            if (aspect >= 1f)
            {
                height = maxSide;
                width = maxSide / aspect;
            }
            else
            {
                width = maxSide;
                height = maxSide * aspect;
            }
        }

        /// <summary>
        /// 面板左端贴物品栏右边缘，右端/上下端贴屏幕；随后按面板像素尺寸算出每页能放多少格。
        /// </summary>
        private static void ApplyPlacement(CustomUIWindow window, RectTransform inventoryRect)
        {
            if (window == null) return;

            var rect = window.Rect;
            if (rect == null) return;

            var parent = rect.parent as RectTransform;
            if (parent == null)
            {
                Warn("catalog window has no RectTransform parent, keep default placement");
                return;
            }

            float parentWidth = parent.rect.width;
            float parentHeight = parent.rect.height;
            if (parentWidth <= 1f || parentHeight <= 1f)
            {
                Warn("layer rect is not measured yet, keep default placement");
                return;
            }

            float leftNorm = 0f;
            float inventoryRight = 0f;
            if (inventoryRect != null)
            {
                try
                {
                    // 锚点解析到的就是物品栏窗口本身，用它的矩形（与旧版一致）
                    var inventory = DockedLayout.ToWorldRect(inventoryRect);
                    inventoryRight = inventory.xMax;
                    leftNorm = DockedLayout.ToNormalized(parent, DockedLayout.RightCenter(inventory)).x;

                    // 面板刚量到的矩形直接喂给按钮：两者用同一份测量，位置天然一致
                    LauncherButton.SyncToInventory(inventory);
                }
                catch (Exception e)
                {
                    Warn("measure inventory rect: " + e.Message);
                }
            }

            leftNorm = Mathf.Clamp01(leftNorm);
            float availableWidth = parentWidth * (1f - leftNorm);
            if (availableWidth < MinPanelWidth)
            {
                // 量不到物品栏真实边界时的兜底：占屏幕右侧 55%
                leftNorm = 0.45f;
                availableWidth = parentWidth * 0.55f;
                Warn("inventory bounds unusable, fall back to right 55% of the screen");
            }

            rect.anchorMin = new Vector2(leftNorm, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            ComputePageSize(availableWidth, parentHeight);

            if (ModEntry.Log != null)
            {
                ModEntry.Log.Log(string.Format(
                    "[panel] screen={0}x{1} inventoryRightX={2:F1} leftNorm={3:F3} panel={4}x{5} grid={6}x{7}={8}/page",
                    Mathf.RoundToInt(parentWidth), Mathf.RoundToInt(parentHeight), inventoryRight,
                    leftNorm, Mathf.RoundToInt(availableWidth), Mathf.RoundToInt(parentHeight),
                    _columns, _rows, _perPage));
            }
        }

        private static float _nextPlacementCheckAt;

        /// <summary>
        /// 物品栏窗口几何变了（由 <see cref="PanelWatchPatch"/> 在 Show/Hide/ResizePixels/FixBounds/
        /// ReclampDockedWindows 之后调用）：重新量物品栏矩形并重新贴靠。
        /// 面板左右锚点是「物品栏右边缘 → 屏幕右边缘」，所以物品栏一变宽/一变高，
        /// 面板宽度、每页格子数都会跟着重算。
        /// </summary>
        internal static void OnInventoryGeometryChanged()
        {
            if (_window == null || !_window.IsAlive || !_window.IsVisible) return;

            float now = Time.unscaledTime;
            if (now < _nextPlacementCheckAt) return;
            _nextPlacementCheckAt = now + 0.2f;

            int oldPerPage = _perPage;
            int oldColumns = _columns;

            try
            {
                // 按钮指定的贴靠对象优先；没有才走自动解析。
                // 只认真窗口：解析退化到整块画布时会让面板按「右侧 55%」兜底，反而跳一下
                var resolved = AnchorOverride != null ? AnchorOverride : AnchorTarget.ResolveWindow();
                if (resolved != null) _inventoryRect = resolved;

                ApplyPlacement(_window, _inventoryRect);
            }
            catch (Exception e)
            {
                Warn("re-place on inventory resize: " + e.Message);
                return;
            }

            // 每页格数变了才需要重排网格；只是挪了位置的话布局自己会跟上
            if (_perPage != oldPerPage || _columns != oldColumns)
            {
                _page = 0;
                _pendingRender = true;   // 下一帧由 AutoRefreshTick 处理，避免在窗口布局中途重画
            }
        }

        private static void ComputePageSize(float panelWidth, float panelHeight)
        {
            float usableWidth = Mathf.Max(64f, panelWidth - PaddingX);
            float usableHeight = Mathf.Max(64f, panelHeight - ReservedY);

            _columns = Mathf.Max(1, Mathf.FloorToInt((usableWidth + CellSpacing) / (CellWidth + CellSpacing)));
            _rows = Mathf.Max(1, Mathf.FloorToInt((usableHeight + CellSpacing) / (CellHeight + CellSpacing)));
            _perPage = _columns * _rows;
        }

        private static void Warn(string message)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning(message);
        }

        private static void Error(string what, Exception e)
        {
            if (ModEntry.Log != null) ModEntry.Log.Error("failed to " + what + ": " + e);
        }
    }
}
