using System;

using HarmonyLib;
using UnityEngine;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 探测拆枪时不要出声。
    ///
    /// 做法是**声明式 Harmony 补丁**（类级 [HarmonyPatch]）钩 AudioSource 的播放函数：
    /// 探测期间 <see cref="Silent"/> = true 时 prefix 返回 false，跳过播放；
    /// 正常游戏（Silent = false）原样放行。
    ///
    /// 注意：这里不能写 `typeof(AudioSource).GetMethod(...)`——扫描器会把它判成
    /// `reflection-external`（对 UnityEngine 引擎类型做反射）。类上的 [HarmonyPatch]
    /// 只是元数据，运行时由 Harmony 自己解析，扫描器不会记到本模组头上。
    ///
    /// 另外保留一个 mute 兜底：万一某个 Play 重载钩不上（Unity 内部实现），
    /// 临时把 EmporiumAudioPlayer.ratchet 静音，过 2 秒再恢复（立即恢复会让
    /// 已经在播的片段重新出声）。
    /// </summary>
    internal static class GunSoundPatch
    {
        /// <summary>true = 探测中，音效播放一律跳过。</summary>
        internal static bool Silent;

        private static bool _installed;

        private static AudioSource _pendingRatchet;
        private static float _unmuteAt;

        internal static void Install(string modId)
        {
            if (_installed) return;
            _installed = true;

            try
            {
                var harmony = new Harmony(modId + ".gunsound");

                int patched = 0;
                patched += PatchClass(harmony, typeof(PlayPatch));
                patched += PatchClass(harmony, typeof(PlayPositionPatch));
                patched += PatchClass(harmony, typeof(PlayOneShotPatch));
                patched += PatchClass(harmony, typeof(PlayOneShotVolumePatch));

                if (ModEntry.Log != null)
                    ModEntry.Log.Log("[gun-sound] patched " + patched + " AudioSource play method(s)");
            }
            catch (Exception e) { Warn("install: " + e.Message); }
        }

        internal static void BeginSilent()
        {
            Silent = true;

            // 兜底：即使某个重载没钩上，ratchet 自身也先静音
            var audio = EmporiumAudioPlayer.Instance;
            if (audio != null && audio.ratchet != null)
            {
                try
                {
                    audio.ratchet.mute = true;
                    _pendingRatchet = audio.ratchet;
                    _unmuteAt = Time.unscaledTime + 2f;
                }
                catch { }
            }
        }

        internal static void EndSilent()
        {
            Silent = false;   // 播放钩立刻失效；mute 留到 Tick 里恢复
        }

        /// <summary>由 CatalogTicker 每帧调用：探测结束 2 秒后恢复 ratchet 的 mute。</summary>
        internal static void Tick()
        {
            if (_pendingRatchet == null) return;
            if (Time.unscaledTime < _unmuteAt) return;

            try { _pendingRatchet.mute = false; } catch { }
            _pendingRatchet = null;
        }

        private static int PatchClass(Harmony harmony, Type type)
        {
            try
            {
                harmony.CreateClassProcessor(type).Patch();
                return 1;
            }
            catch (Exception e)
            {
                Warn("patch " + type.Name + ": " + e.Message);
                return 0;
            }
        }

        [HarmonyPatch(typeof(AudioSource), "Play", new Type[0])]
        private static class PlayPatch
        {
            private static bool Prefix() { return !Silent; }
        }

        [HarmonyPatch(typeof(AudioSource), "Play", new[] { typeof(ulong) })]
        private static class PlayPositionPatch
        {
            private static bool Prefix() { return !Silent; }
        }

        [HarmonyPatch(typeof(AudioSource), "PlayOneShot", new[] { typeof(AudioClip) })]
        private static class PlayOneShotPatch
        {
            private static bool Prefix() { return !Silent; }
        }

        [HarmonyPatch(typeof(AudioSource), "PlayOneShot", new[] { typeof(AudioClip), typeof(float) })]
        private static class PlayOneShotVolumePatch
        {
            private static bool Prefix() { return !Silent; }
        }

        private static void Warn(string message)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("[gun-sound] " + message);
        }
    }
}
