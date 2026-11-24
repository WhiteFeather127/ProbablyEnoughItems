using System;

using UnityEngine;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 探测拆枪时不要出声。
    ///
    /// 以前用四个 `[HarmonyPatch(typeof(AudioSource), …)]` 钩引擎的播放函数，
    /// 扫描器判成 `reflection-external`（对 UnityEngine 引擎类型打补丁），
    /// 2026-12-10 起直接拒绝加载（`mod-scan` 会逐条列出这四处）。
    ///
    /// 现在不做任何补丁：探测期间直接把全局 `AudioListener.pause` 打开，
    /// 所有 AudioSource 的播放（包括 `GunHelper.DisassembleWeapon` 末尾直接
    /// `ratchet.Play()`）都不出声；探测结束后延迟 2 秒恢复，避免已经在播的片段
    /// 立刻接着响。直接读写公开属性，扫描器不记录任何能力。
    /// </summary>
    internal static class GunSoundPatch
    {
        private const float RestoreDelay = 2f;

        private static bool _paused;
        private static float _restoreAt;

        internal static void Install(string modId)
        {
            // 不再安装 Harmony 补丁；保留入口给 ModEntry 调用。
        }

        internal static void BeginSilent()
        {
            try
            {
                if (!_paused)
                {
                    AudioListener.pause = true;
                    _paused = true;
                }

                _restoreAt = Time.unscaledTime + RestoreDelay;
            }
            catch (Exception e)
            {
                Warn("begin: " + e.Message);
            }
        }

        internal static void EndSilent()
        {
            // 恢复留到 Tick 里，见类的说明。
        }

        /// <summary>由 CatalogTicker 每帧调用：探测结束 2 秒后恢复全局音频。</summary>
        internal static void Tick()
        {
            if (!_paused) return;
            if (Time.unscaledTime < _restoreAt) return;

            try { AudioListener.pause = false; } catch { }
            _paused = false;
        }

        private static void Warn(string message)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("[gun-sound] " + message);
        }
    }
}
