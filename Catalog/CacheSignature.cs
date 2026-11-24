using System;
using System.Collections.Generic;
using System.Text;

using UnityEngine;

namespace ItemCatalog.Catalog
{
    /// <summary>
    /// 所有磁盘探测缓存共用的「环境签名」。
    ///
    /// 签名 = 游戏版本 + <c>Assembly-CSharp.dll</c> 哈希 + 所有启用模组的指纹；
    /// 单独落在 <c>ModData/probably_enough_items/environment.txt</c>，
    /// 一次启动只计算 / 校验一次，之后各缓存（箱子 / 拆枪 / 教程物品 / 网络升级 …）
    /// 都读同一份 <see cref="Value"/>，失效规则从此只有一处。
    ///
    /// 注意：签名文件只是「本次启动的校验结果」，不是缓存本体；各缓存仍把自己的
    /// <c>sig=</c> 写进各自的文件，避免某次探测失败后旧文件在下一轮被误用。
    /// </summary>
    internal static class CacheSignature
    {
        /// <summary>签名单独存放的文件名（与其它缓存同在 ModData/probably_enough_items）。</summary>
        internal const string FileName = "environment.txt";

        private static bool _ready;
        private static bool _changed;
        private static bool _firstRun;
        private static string _value;
        private static string _gameValue;

        /// <summary>本次启动的环境签名（内存缓存，只算一次）。</summary>
        internal static string Value
        {
            get
            {
                Ensure();
                return _value ?? "unknown";
            }
        }

        /// <summary>
        /// 只含游戏本体的签名（版本 + 程序集哈希）。
        /// 给「探测缓存」用：枪槽 / 拆枪产物的探测结果只取决于游戏本体，
        /// 换模组 dll 不该让它们重探一次（每次 20s 起步）。
        /// </summary>
        internal static string GameOnly
        {
            get
            {
                Ensure();
                return _gameValue ?? "unknown";
            }
        }

        /// <summary>相对上次启动，游戏或模组文件是否变过（第一次运行也算变过）。</summary>
        internal static bool Changed
        {
            get
            {
                Ensure();
                return _changed;
            }
        }

        /// <summary>
        /// 启动阶段主动校验一次：算哈希 → 和 environment.txt 比对 → 变了就写回。
        /// 预热或任一探测调用都行，重复调用无开销。
        /// </summary>
        internal static void Ensure()
        {
            if (_ready) return;
            _ready = true;

            try
            {
                _value = Compute();

                string previous = ReadStored();
                _firstRun = string.IsNullOrEmpty(previous);
                _changed = _firstRun || !string.Equals(previous, _value, StringComparison.Ordinal);

                if (_changed) WriteStored(_value);

                if (ModEntry.Log != null)
                {
                    ModEntry.Log.Log("[cache] environment "
                                     + (_firstRun ? "first" : _changed ? "changed" : "same")
                                     + " game=" + Application.version);
                }
            }
            catch (Exception e)
            {
                _value = "unknown";
                _changed = false;
                Warn("verify: " + e.Message);
            }
        }

        private static string Compute()
        {
            var text = new StringBuilder();

            string gamePart = "game=" + Application.version + ":" + GameHash();
            _gameValue = gamePart;
            text.Append(gamePart);

            var mods = new List<string>();

            try
            {
                var list = ModLoader.Mods;
                if (list != null)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        var mod = list[i];
                        if (mod == null || !mod.IsEnabled) continue;

                        string id = mod.Manifest != null && !string.IsNullOrEmpty(mod.Manifest.ID)
                            ? mod.Manifest.ID
                            : "?";

                        string fingerprint = null;
                        try { if (mod.Fingerprint != null) fingerprint = mod.Fingerprint.Combined; }
                        catch { }

                        if (string.IsNullOrEmpty(fingerprint))
                        {
                            try
                            {
                                if (!string.IsNullOrEmpty(mod.DllPath))
                                    fingerprint = ModFingerprint.HashFile(mod.DllPath);
                            }
                            catch { }
                        }

                        mods.Add(id + ":" + (fingerprint ?? "?"));
                    }
                }
            }
            catch (Exception e) { Warn("mods: " + e.Message); }

            mods.Sort(StringComparer.Ordinal);
            text.Append("|mods=").Append(string.Join(";", mods.ToArray()));
            return text.ToString();
        }

        /// <summary>
        /// 游戏本体的哈希：优先哈希 Assembly-CSharp.dll；拿不到 Location
        /// （或被裁剪）时退回模块 MVID——重建过的程序集一定不同。
        /// </summary>
        private static string GameHash()
        {
            try
            {
                var assembly = typeof(GameItem).Assembly;

                string location = assembly.Location;
                if (!string.IsNullOrEmpty(location))
                {
                    string hash = ModFingerprint.HashFile(location);
                    if (!string.IsNullOrEmpty(hash)) return hash;
                }

                return assembly.ManifestModule.ModuleVersionId.ToString();
            }
            catch (Exception e)
            {
                Warn("game: " + e.Message);
                return "?";
            }
        }

        private static string ReadStored()
        {
            try
            {
                if (!ModHelper.ModDataFileExists(ModEntry.MOD_ID, FileName)) return null;

                string text = ModHelper.ReadModDataFileText(ModEntry.MOD_ID, FileName);
                if (string.IsNullOrEmpty(text)) return null;

                var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0) continue;

                    const string prefix = "sig=";
                    if (line.StartsWith(prefix, StringComparison.Ordinal))
                        return line.Substring(prefix.Length).Trim();

                    return line;   // 兼容只写一行裸签名
                }

                return null;
            }
            catch (Exception e)
            {
                Warn("read: " + e.Message);
                return null;
            }
        }

        private static void WriteStored(string value)
        {
            try
            {
                ModHelper.WriteModDataFileText(ModEntry.MOD_ID, FileName, "sig=" + value + "\n");
            }
            catch (Exception e) { Warn("write: " + e.Message); }
        }

        private static void Warn(string message)
        {
            try { if (ModEntry.Log != null) ModEntry.Log.Warning("[cache] " + message); } catch { }
        }
    }
}
