using System;
using System.Collections.Generic;
using System.Text;

namespace ItemCatalog.Catalog
{
    /// <summary>
    /// 拆枪产出的自动探测 + 本地缓存。
    ///
    /// 游戏没有拆解数据表：`GunHelper.DisassembleWeapon` 是一串按枪 id 写死的 if/else。
    /// 这里在**隔离的临时容器**里真跑一次这个方法，把掉出来的物品 id 记下来，
    /// 写进 <c>gun_disassembly.txt</c>（签名 / 格式版本 / 枪 id 集合任一变化就重探）。
    /// 模拟失败（系统没就绪、容器拒收、抛异常）时退回 <see cref="GunDisassembly"/> 的 IL 表。
    ///
    /// 安全约束：
    /// · 枪用 <c>CatalogWindow.CreatePreviewItem</c> 生成，随机流 / uniqueId 自带隔离；
    /// · 拆解产生的物品全部落在一个脱离世界的 20×20 网格里，读完立刻销毁；
    /// · 拆解末尾的 ratchet 音效由 <c>GunSoundPatch.Silent</c> 跳过（transpiler 钩掉了那次 Play）；
    /// · 产物创建会推进 <c>PlayerStore.currentUniqueId</c>，结束后还原。
    ///
    /// 缓存是纯文本（每行 <c>key=value</c>，产物用 <c>|</c> 分隔），不引额外的 JSON 依赖。
    /// </summary>
    internal static class GunDisassemblyProbe
    {
        private const string CacheFile = "gun_disassembly.txt";

        /// <summary>缓存格式版本：探测方式（拆枪流程 / 记录哪些产物）变了就 +1。</summary>
        private const string CacheVersion = "1";

        private static readonly string[] Empty = new string[0];

        private static Dictionary<string, string[]> _outputs;
        private static bool _ready;
        private static int _attempts;

        /// <summary>加载阶段先试一次；失败的话打开详情页时再补一次（最多两次）。</summary>
        internal static void Warmup()
        {
            EnsureReady();
        }

        internal static string[] OutputsFor(string gunId)
        {
            if (string.IsNullOrEmpty(gunId)) return Empty;

            EnsureReady();

            string[] outputs;
            if (_outputs != null && _outputs.TryGetValue(gunId, out outputs)) return outputs;

            // 没探到（模拟失败 / 缓存里没有）→ 退回照抄 IL 的表
            return GunDisassembly.OutputsFor(gunId);
        }

        private static void EnsureReady()
        {
            if (_ready || _attempts >= 2) return;
            _attempts++;

            try
            {
                string signature = GunSignature();

                if (TryLoadCache(signature))
                {
                    _ready = true;
                    return;
                }

                _outputs = ProbeAll();
                if (_outputs != null && _outputs.Count > 0)
                {
                    _ready = true;
                    SaveCache(signature);
                }
            }
            catch (Exception e) { Warn("probe: " + e.Message); }
        }

        /// <summary>目录里可见、带 ADV_GUN_TAG 的枪 id（排序后拼成签名，用于缓存失效判断）。</summary>
        private static string GunSignature()
        {
            var ids = GunIds();
            var sb = new StringBuilder();
            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0) sb.Append('|');
                sb.Append(ids[i]);
            }
            return sb.ToString();
        }

        private static List<string> GunIds()
        {
            var ids = new List<string>();
            var items = CatalogDataBuilder.Visible;
            if (items == null) return ids;

            for (int i = 0; i < items.Count; i++)
            {
                var entry = items[i];
                var item = entry == null ? null : entry.Item;
                if (item == null || string.IsNullOrEmpty(entry.Id)) continue;

                bool gun;
                try { gun = item.IsTag("ADV_GUN_TAG"); }
                catch { gun = false; }
                if (gun) ids.Add(entry.Id);
            }

            ids.Sort(StringComparer.Ordinal);
            return ids;
        }

        private static Dictionary<string, string[]> ProbeAll()
        {
            var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var ids = GunIds();

            int probed = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                var outputs = ProbeOne(ids[i]);
                if (outputs == null) continue;

                map[ids[i]] = outputs;
                probed++;
            }

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[gun] probed " + probed + "/" + ids.Count + " guns");

            return map;
        }

        /// <summary>
        /// 在临时网格里真拆一次。返回 null = 这次没探测成功（调用方回退 IL 表）；
        /// 空数组 = 这把枪拆完确实不掉东西。
        /// </summary>
        private static string[] ProbeOne(string gunId)
        {
            var store = PlayerStore.Instance;
            int savedUniqueId = store == null ? 0 : store.currentUniqueId;

            GameGridInventory box = null;

            try
            {
                var gun = UI.CatalogWindow.CreatePreviewItem(gunId);
                if (gun == null) return null;

                // 沙盒只需装下枪本身：产物在 EmporiumTryAdd 前被 LoadCapture 截获，
                // 不会落进网格，所以不需要大容器。
                box = new GameGridInventory(12, 8);
                GraphUtils.TryAcceptAll(box, gun, 1);

                if (gun.parentInventory != box)
                {
                    Warn(gunId + ": sandbox refused the gun");
                    return null;
                }

                if (!LoadCapture.Begin()) return null;

                string[] ids;
                try
                {
                    // 拆解末尾会播 ratchet：GunSoundPatch 在探测期间跳过 AudioSource 播放
                    UI.GunSoundPatch.BeginSilent();
                    try { GunHelper.DisassembleWeapon(gun); }
                    finally { UI.GunSoundPatch.EndSilent(); }

                    ids = LoadCapture.End().ToArray();
                }
                finally
                {
                    // 异常时也要关捕获窗口，否则后面所有 TryAdd / EmporiumTryAdd 都会被吞掉
                    if (LoadCapture.Active) LoadCapture.End();
                }

                if (ModEntry.Log != null && ids.Length > 0)
                    ModEntry.Log.Log("[gun] " + gunId + " -> " + string.Join("|", ids));

                return ids;
            }
            catch (Exception e)
            {
                Warn(gunId + ": " + e.Message);
                return null;
            }
            finally
            {
                UI.GunSoundPatch.EndSilent();

                // TryUnloadAll 会把枪上配件放进沙盒；异常中断时也可能有残留，一起清掉
                if (box != null)
                {
                    try
                    {
                        var leftovers = GraphUtils.FindAllChildrenType<GameItem>(box, null, null);
                        if (leftovers != null)
                        {
                            for (int i = 0; i < leftovers.Count; i++)
                            {
                                if (leftovers[i] == null) continue;
                                try { leftovers[i].Destroy(); } catch { }
                            }
                        }
                    }
                    catch { }
                }

                if (store != null) store.currentUniqueId = savedUniqueId;
            }
        }

        private static bool TryLoadCache(string signature)
        {
            try
            {
                if (!ModHelper.ModDataFileExists(ModEntry.MOD_ID, CacheFile)) return false;

                string text = ModHelper.ReadModDataFileText(ModEntry.MOD_ID, CacheFile);
                if (string.IsNullOrEmpty(text)) return false;

                string sig = null, probe = null, guns = null;
                var map = new Dictionary<string, string[]>(StringComparer.Ordinal);

                var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0) continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq);
                    string value = line.Substring(eq + 1);

                    if (key == "sig") sig = value;
                    else if (key == "probe") probe = value;
                    else if (key == "guns") guns = value;
                    else map[key] = Split(value);
                }

                // 游戏版本 + 所有启用模组的指纹（与箱子内容 / 枪械槽同一套签名）：
                // 别的模组改了某把枪的拆解产物、但枪 id 集合没变时，旧缓存必须作废。
                if (!string.Equals(probe, CacheVersion, StringComparison.Ordinal)) return false;
                if (!string.Equals(sig, BoxProbe.Signature(), StringComparison.Ordinal)) return false;
                if (!string.Equals(guns, signature, StringComparison.Ordinal)) return false;

                _outputs = map;

                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[gun] cache loaded: " + map.Count + " guns");

                return true;
            }
            catch (Exception e)
            {
                Warn("cache read: " + e.Message);
                return false;
            }
        }

        private static void SaveCache(string signature)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("sig=").Append(BoxProbe.Signature()).Append('\n');
                sb.Append("probe=").Append(CacheVersion).Append('\n');
                sb.Append("guns=").Append(signature).Append('\n');

                foreach (var pair in _outputs)
                {
                    sb.Append(pair.Key).Append('=');
                    var outputs = pair.Value ?? Empty;
                    for (int i = 0; i < outputs.Length; i++)
                    {
                        if (i > 0) sb.Append('|');
                        sb.Append(outputs[i]);
                    }
                    sb.Append('\n');
                }

                ModHelper.WriteModDataFileText(ModEntry.MOD_ID, CacheFile, sb.ToString());

                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[gun] cache saved: " + _outputs.Count + " guns");
            }
            catch (Exception e) { Warn("cache write: " + e.Message); }
        }

        private static string[] Split(string value)
        {
            if (string.IsNullOrEmpty(value)) return Empty;

            var parts = value.Split('|');
            var list = new List<string>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                string id = parts[i].Trim();
                if (id.Length > 0) list.Add(id);
            }
            return list.ToArray();
        }

        private static void Warn(string message)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("[gun] " + message);
        }
    }
}
