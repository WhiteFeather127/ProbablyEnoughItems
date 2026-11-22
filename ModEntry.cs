using System;
using UnityEngine;

namespace ItemCatalog
{
    /// <summary>
    /// 模组入口：日志、配置、钩子订阅与调试按钮；界面与数据分别在 UI/ 与 Catalog/ 下。
    /// </summary>
    public class ModEntry : IMod
    {
        public const string MOD_ID = "probably_enough_items";

        internal static ModLog Log;

        /// <summary>取本地化文案；找不到时返回 key 本身，绝不抛异常。</summary>
        internal static string T(string key, params object[] args)
        {
            try
            {
                return ModHelper.GetLocalized(MOD_ID, key, args);
            }
            catch (Exception e)
            {
                if (Log != null) Log.Warning("localization '" + key + "' failed: " + e.Message);
                return key;
            }
        }

        public void Init(ModManifest manifest)
        {
            Log = new ModLog(manifest);
            Log.Log("Probably Enough Items initializing...");

            // 不再提供布局配置：贴靠目标运行时自动解析（见 AnchorTarget），
            // 面板层级固定在 paper（与原版纸条同层）。
        }

        public void OnEnable()
        {
            ModHook.OnGameLoadedLate += OnGameLoaded;
            ModHook.OnCreateTooltipLate += AppendItemId;
            ModHook.OnCreateTooltipLate += AppendLiquidInfo;

            // 模组真正把物品塞进目录的时刻（比 OnGameLoadedLate 更贴切）：
            // 一有目录初始化就增量补入图鉴，别等到玩家点开图鉴才发现「多了东西」。
            ModHook.OnModItemDirectoryInit += OnModDirectoryInit;

            // 常驻节拍器从游戏一开始就运行：载入后的后台探测靠它驱动
            UI.CatalogTicker.Ensure();

            // 监听游戏窗口显隐（PixelWindow.Show/Hide）：物品栏面板一激活就重锚按钮
            UI.PanelWatchPatch.Install(MOD_ID);

            // 把物品拖到图鉴上 → 丢进店里的垃圾桶
            UI.DragToDeletePatch.Install(MOD_ID);

            // 拆枪探测要跳过 DisassembleWeapon 末尾的 ratchet 音效（Silent 标志）
            UI.GunSoundPatch.Install(MOD_ID);

            try
            {
                ModHelper.AddDebugFunction(MOD_ID, ModHelper.DEBUG_CATEGORY_SPECIAL,
                    "Catalog: Dump Data", () => Catalog.CatalogDataBuilder.Dump());

                ModHelper.AddDebugFunction(MOD_ID, ModHelper.DEBUG_CATEGORY_SPECIAL,
                    "Catalog: Reopen", () => UI.CatalogWindow.Reopen(), tall: true);

                ModHelper.AddDebugFunction(MOD_ID, ModHelper.DEBUG_CATEGORY_SPECIAL,
                    "Catalog: Dump Recipes", () => Catalog.RecipeIndex.Dump());

                ModHelper.AddDebugFunction(MOD_ID, ModHelper.DEBUG_CATEGORY_SPECIAL,
                    "Catalog: Dump Sources", () => Catalog.SourceIndex.Dump());

                ModHelper.AddDebugFunction(MOD_ID, ModHelper.DEBUG_CATEGORY_SPECIAL,
                    "Catalog: Probe Sources", () => Catalog.SourceProbe.Log("F9"));
            }
            catch (Exception e)
            {
                Log.Warning("F9 debug buttons unavailable: " + e.Message);
            }

            Log.Log("Probably Enough Items enabled.");
        }

        public void OnDisable()
        {
            ModHook.OnGameLoadedLate -= OnGameLoaded;
            ModHook.OnCreateTooltipLate -= AppendItemId;
            ModHook.OnCreateTooltipLate -= AppendLiquidInfo;
            ModHook.OnModItemDirectoryInit -= OnModDirectoryInit;

            try { ModHelper.RemoveDebugFunctions(MOD_ID); }
            catch { /* 受限构建中不存在 F9 菜单 */ }

            Log.Log("Probably Enough Items disabled.");
        }

        /// <summary>
        /// 给所有物品提示框（背包、柜台、机器，以及我们自己的图鉴）追加一行物品 ID。
        /// 挂的是官方钩子，所以只要游戏正常构建 tooltip 就会生效。
        /// </summary>
        private static void AppendItemId(RichTextBuilder builder, GameItem item)
        {
            if (builder == null || item == null) return;

            string id = null;
            try { id = item.identifier; }
            catch { }

            if (string.IsNullOrEmpty(id)) return;

            try
            {
                builder.AddLine(T("tt_id") + ": " + id);
            }
            catch (Exception e)
            {
                if (Log != null) Log.Warning("append item id failed: " + e.Message);
            }
        }

        /// <summary>
        /// 容器类物品追加一行成分：主要成分只写名字（占比大、无信息量），
        /// 杂质写可能出现的百分比区间（同一 id 每次生成的含量不同）。
        /// 非容器 / 空瓶时 ContainerRangeText 返回 null，这里什么也不加。
        /// </summary>
        private static void AppendLiquidInfo(RichTextBuilder builder, GameItem item)
        {
            if (builder == null || item == null) return;

            // 空瓶格（来源窗口里「也摆空瓶」的那一格）不显示成分
            if (UI.CatalogWindow.SuppressLiquidInfo) return;

            // 非液体容器直接跳过（水和酒都算容器）
            try
            {
                if (!item.IsTag("LIQUID_CONTAINER_TAG") && !item.IsTag("WINE_CONTAINER_TAG")) return;
            }
            catch { return; }

            string text = null;
            try
            {
                string id = item.identifier;
                if (!string.IsNullOrEmpty(id)) text = UI.CatalogWindow.ContainerRangeText(id);
            }
            catch { }

            if (string.IsNullOrEmpty(text)) return;

            try
            {
                // 「杂质：」和每种杂质各占一行（文本里用 \n 分隔）。
                // RichTextBuilder 没有多行接口，只能逐段 AddLine。
                string[] lines = text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Length == 0) continue;
                    builder.AddLine(lines[i]);
                }
            }
            catch (Exception e)
            {
                if (Log != null) Log.Warning("append liquid info failed: " + e.Message);
            }
        }

        /// <summary>
        /// 某个模组的物品目录被初始化——模组正是借这个回调往目录里塞物品。
        /// 这是「新物品出现」最准的信号，比在打开图鉴时比对 id 集合更早、也更省。
        /// </summary>
        private static void OnModDirectoryInit(ModItemDirectory directory)
        {
            try
            {
                // 不写 `directory.GetType()`：那属于「运行时才知道的类型」，
                // 扫描器会算成 reflection-external（2026-12-10 起直接拒绝加载）。
                // 这里只是打个日志，用固定别名就够。
                string name = directory == null ? "<null>" : "modDirectory";

                // 只是记标记，真正的增量补入由常驻节拍器在下一个空闲帧做：
                // 此刻正处在模组的目录初始化流程里，不适合同步造物品。
                ProbablyEnoughItems.Api.ItemCatalogApi.NotifyItemsChanged("mod directory: " + name);

                if (Log != null) Log.Log("[moddir] " + name + " initialized -> queued for catalog sync");
            }
            catch (Exception e)
            {
                if (Log != null) Log.Warning("[moddir] notify failed: " + e.Message);
            }
        }

        private static void OnGameLoaded()
        {
            try
            {
                UI.LauncherButton.OnGameLoaded();
            }
            catch (Exception e)
            {
                if (Log != null) Log.Error("game load handler failed: " + e);
            }

            // 建表 + 预解析图标 + 展开变体格子全部在加载阶段一次做完，
            // 之后没有「加载中」状态：按钮始终可点，打开图鉴时不需要再做任何准备。
            try
            {
                UI.CatalogWindow.Warmup();
            }
            catch (Exception e)
            {
                if (Log != null) Log.Warning("catalog warmup failed: " + e.Message);
            }
        }
    }
}
