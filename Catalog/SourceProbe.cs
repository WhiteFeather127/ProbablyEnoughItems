using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ItemCatalog.Catalog
{
    /// <summary>
    /// 诊断：把「五类来源页到底有没有数据、各覆盖多少物品」打进日志，
    /// 并写一份 <c>source-probe.txt</c> 到 ModData 目录，方便离线复查。
    ///
    /// 配方与掉落表都是运行时数据（<c>GameItem.recipeManager</c> / <c>LootRegistry</c>），
    /// 离线扫 DLL 得不到，所以只能进游戏后靠这里实测。
    /// 触发方式：F9 → 「Catalog: Probe Sources」，或由 CatalogTicker 自动探几次。
    /// </summary>
    internal static class SourceProbe
    {
        private const string FileName = "source-probe.txt";

        private static int _runs;
        private static int _lastLoot = -2;
        private static int _lastRecipe = -2;
        private static float _nextCheck;

        /// <summary>出一份完整报告：摘要进日志，全量写文件。</summary>
        internal static void Log(string tag)
        {
            var report = new StringBuilder(1 << 16);
            var brief = new List<string>();

            Action<string> say = s => { report.AppendLine(s); brief.Add(s); };
            Action<string> only = s => report.AppendLine(s);

            say("# ItemCatalog 来源探测  tag=" + tag
                + "  gameVersion=" + SafeGameVersion()
                + "  t=" + Time.realtimeSinceStartup.ToString("F1"));

            int all = CatalogDataBuilder.All.Count;
            int visible = CatalogDataBuilder.Visible.Count;
            say("[图鉴] all=" + all + " visible=" + visible
                + " hidden=" + (all - visible)
                + " extra=" + CatalogDataBuilder.ExtraCount);

            say("[来源索引] built=" + SourceIndex.Built
                + " clients=" + SourceIndex.ClientTypeCount
                + " locations=" + SourceIndex.LocationCount
                + " entries=" + SourceIndex.AllSources.Count);

            // 合成：只读状态，不主动 Build（早建会把 Built 置 true，之后配方加载了也不再重建）
            int recipeCount = RecipeIndex.Built ? RecipeIndex.All.Count : -1;
            int outputs = 0;
            if (RecipeIndex.Built)
                for (int i = 0; i < RecipeIndex.All.Count; i++)
                    outputs += RecipeIndex.All[i].Outputs.Count;

            say("[合成] " + (RecipeIndex.Built
                    ? "built=true machines=" + RecipeIndex.MachineCount
                      + " recipes=" + recipeCount + " outputs=" + outputs
                      + " unresolved=" + RecipeIndex.UnresolvedOutputs
                    : "built=false（还没构建，进存档后按 F9 再看）")
                + "  → " + (recipeCount > 0 ? "有" : "没有"));

            // 掉落表：Tables 内部有「没加载就不记 built、下次再试」的保护，可以反复调
            var tables = LootSource.Tables;
            int lootEntries = 0;
            for (int i = 0; i < tables.Length; i++) lootEntries += tables[i].Items.Count;
            say("[掉落表] tables=" + tables.Length + " entries=" + lootEntries
                + "  → " + (tables.Length > 0 ? "有" : "没有"));

            // 逐物品统计五类来源页
            int cClient = 0, cLocation = 0, cCraft = 0, cUse = 0, cLoot = 0, cAny = 0;
            var noSource = new List<string>();

            foreach (var entry in CatalogDataBuilder.Visible)
            {
                string id = entry == null ? null : entry.Id;
                if (string.IsNullOrEmpty(id)) continue;

                bool hasClient = false, hasLocation = false, hasCraft = false,
                     hasUse = false, hasLoot = false;

                var sources = SourceIndex.For(id);
                for (int i = 0; i < sources.Count; i++)
                {
                    var s = sources[i];
                    if (s == null) continue;
                    if (s.Kind == SourceKind.Client) hasClient = true;
                    else if (s.Kind == SourceKind.Location) hasLocation = true;
                }

                hasCraft = RecipeIndex.Built && RecipeIndex.Producing(id).Count > 0;
                hasUse = UseIndex.Producing(id).Count > 0;
                hasLoot = LootSource.For(id).Count > 0;

                if (hasClient) cClient++;
                if (hasLocation) cLocation++;
                if (hasCraft) cCraft++;
                if (hasUse) cUse++;
                if (hasLoot) cLoot++;

                if (hasClient || hasLocation || hasCraft || hasUse || hasLoot) cAny++;
                else noSource.Add(id);
            }

            say("[五类来源页·覆盖物品数] 客人=" + cClient
                + " 地点=" + cLocation
                + " 合成=" + cCraft
                + " 使用来源=" + cUse
                + " 战利品表=" + cLoot);
            say("[覆盖] 至少一条来源=" + cAny + " / visible=" + visible
                + "   完全没有来源=" + noSource.Count);

            // ---- 以下只写文件，日志不刷屏 ----
            only("");
            only("=== 前 20 条配方（材料 -> 产物）===");
            int shown = Math.Min(20, RecipeIndex.Built ? RecipeIndex.All.Count : 0);
            for (int i = 0; i < shown; i++)
            {
                var r = RecipeIndex.All[i];
                var mats = new List<string>();
                for (int k = 0; k < r.Materials.Count; k++)
                {
                    var ing = r.Materials[k];
                    string n = ing.DisplayName;
                    if (string.IsNullOrEmpty(n) && ing.Ids != null && ing.Ids.Count > 0) n = ing.Ids[0];
                    if (string.IsNullOrEmpty(n) && ing.Tags != null && ing.Tags.Count > 0) n = "tag:" + ing.Tags[0];
                    mats.Add((n ?? "?") + "x" + ing.Amount);
                }

                var outs = new List<string>();
                for (int k = 0; k < r.Outputs.Count; k++)
                    outs.Add(r.Outputs[k].Id + "x" + r.Outputs[k].Amount);

                only("  " + r.MachineId + " : [" + string.Join(" + ", mats.ToArray())
                     + "] -> [" + string.Join(" + ", outs.ToArray()) + "]");
            }
            if (shown == 0) only("  (空)");

            only("");
            only("=== 全部掉落表 ===");
            for (int i = 0; i < tables.Length; i++)
                only("  " + tables[i].TableId + "  (" + tables[i].Items.Count + " 条)");
            if (tables.Length == 0) only("  (空)");

            only("");
            only("=== 完全没有来源的物品 (" + noSource.Count + ") ===");
            for (int i = 0; i < noSource.Count; i++) only("  " + noSource[i]);

            string path = "(write failed)";
            try
            {
                ModHelper.WriteModDataFileText(ModEntry.MOD_ID, FileName, report.ToString());
                path = ModHelper.GetModDataFolder(ModEntry.MOD_ID) + "\\" + FileName;
            }
            catch (Exception e)
            {
                if (ModEntry.Log != null) ModEntry.Log.Warning("[probe] write " + FileName + " failed: " + e.Message);
            }

            if (ModEntry.Log == null) return;

            foreach (string line in brief) ModEntry.Log.Log("[probe] " + line);
            ModEntry.Log.Log("[probe] file: " + path);

            int head = Math.Min(20, noSource.Count);
            for (int i = 0; i < head; i++) ModEntry.Log.Log("[probe] no-source: " + noSource[i]);
            if (noSource.Count > head)
                ModEntry.Log.Log("[probe] ... 其余 " + (noSource.Count - head) + " 个见 " + FileName);
        }

        private static string SafeGameVersion()
        {
            try { return Application.version ?? "?"; }
            catch { return "?"; }
        }

        /// <summary>
        /// 由 CatalogTicker 每帧调用，内部 2 秒节流。
        /// 第一次在来源索引建好后出报告；之后配方数 / 掉落表数发生变化再出，最多 3 次。
        /// 这样能抓到「读档后运行时数据才注册」的那一刻，又不会刷屏。
        /// </summary>
        internal static void Tick()
        {
            if (_runs >= 3) return;
            if (!SourceIndex.Built) return;

            float now = Time.realtimeSinceStartup;
            if (_nextCheck <= 0f) { _nextCheck = now + 3f; return; }
            if (now < _nextCheck) return;
            _nextCheck = now + 2f;

            int loot = LootSource.Tables.Length;
            int recipe = RecipeIndex.Built ? RecipeIndex.All.Count : -1;

            if (_runs > 0 && loot == _lastLoot && recipe == _lastRecipe) return;

            _lastLoot = loot;
            _lastRecipe = recipe;
            _runs++;

            try { Log(_runs == 1 ? "auto" : "auto-changed"); }
            catch (Exception e)
            {
                if (ModEntry.Log != null) ModEntry.Log.Warning("[probe] auto failed: " + e.Message);
                _runs = 3;
            }
        }
    }
}
