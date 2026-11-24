#if false
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

using HarmonyLib;
using UnityEngine;
#endif

namespace ItemCatalog.UI
{
    /// <summary>
    /// 拆枪探测静音：**暂时关闭**（2026-11-25），完整实现保留在文件末尾的
    /// <c>#if false</c> 块里，恢复时把条件改成 true 即可。
    ///
    /// 关闭原因（三种实现都有问题）：
    ///   · Harmony patch `UnityEngine.AudioSource`        → 扫描器判 reflection-external（12-10 起拒绝）
    ///   · `AudioListener.pause` / `volume`                → 对探测期间新 Play 不可靠 / 影响全局音量
    ///   · transpiler patch `GunHelper.DisassembleWeapon`  → 日志替换成功，实际仍有声音
    ///
    /// 现在允许探测时响一下。空入口只为让调用点（GunDisassemblyProbe / CatalogTicker）不用改。
    /// </summary>
    internal static class GunSoundPatch
    {
        internal static void Install(string modId)
        {
        }

        internal static void BeginSilent()
        {
        }

        internal static void EndSilent()
        {
        }

        internal static void Tick()
        {
        }
    }

#if false
    /// <summary>
    /// 原实现（transpiler + ratchet mute 双保险）。当前不参与编译。
    /// </summary>
    internal static class GunSoundPatchOriginal
    {
        /// <summary>true = 探测中，拆枪末尾的 ratchet 不播放。</summary>
        internal static bool Silent;

        private static bool _installed;
        private static MethodInfo _playIfNotSilent;

        private const float UnmuteDelay = 2f;

        private static readonly List<AudioSource> _muted = new List<AudioSource>();
        private static float _unmuteAt;

        internal static void Install(string modId)
        {
            if (_installed) return;
            _installed = true;

            try
            {
                var target = AccessTools.Method(typeof(GunHelper), "DisassembleWeapon");
                if (target == null)
                {
                    Warn("GunHelper.DisassembleWeapon not found");
                    return;
                }

                _playIfNotSilent = typeof(GunSoundPatchOriginal).GetMethod(
                    "PlayIfNotSilent",
                    BindingFlags.NonPublic | BindingFlags.Static);

                var transpiler = typeof(GunSoundPatchOriginal).GetMethod(
                    "Transpiler",
                    BindingFlags.NonPublic | BindingFlags.Static);

                var harmony = new Harmony(modId + ".gunsound");
                harmony.Patch(target, transpiler: new HarmonyMethod(transpiler));
            }
            catch (Exception e)
            {
                Warn("install: " + e.Message);
            }
        }

        internal static void BeginSilent()
        {
            Silent = true;

            // 双保险：transpiler 万一没挂上（游戏更新改了 IL / Harmony 版本差异），
            // 直接把拆枪用的两个 source 静音。探测期间播放时它们已经是 mute 状态。
            try
            {
                var audio = EmporiumAudioPlayer.Instance;
                if (audio != null)
                {
                    Mute(audio.ratchet);
                    Mute(audio.ratchet2);
                }

                _unmuteAt = Time.unscaledTime + UnmuteDelay;
            }
            catch (Exception e)
            {
                Warn("begin: " + e.Message);
            }
        }

        internal static void EndSilent()
        {
            Silent = false;
            // mute 留到 Tick 里恢复：已经在播的片段立刻放声会重新听到。
        }

        /// <summary>保留给 CatalogTicker：探测结束 2 秒后恢复被 mute 的 source。</summary>
        internal static void Tick()
        {
            if (_muted.Count == 0) return;
            if (Time.unscaledTime < _unmuteAt) return;

            for (int i = 0; i < _muted.Count; i++)
            {
                try { if (_muted[i] != null) _muted[i].mute = false; } catch { }
            }

            _muted.Clear();
        }

        private static void Mute(AudioSource source)
        {
            if (source == null) return;

            try
            {
                source.mute = true;
                if (!_muted.Contains(source)) _muted.Add(source);
            }
            catch { }
        }

        /// <summary>
        /// 替换 `AudioSource.Play()` 的位置。栈上正好是那个 AudioSource，
        /// 参数签名一致（消费一个 AudioSource、无返回值），IL 保持平衡。
        /// </summary>
        private static void PlayIfNotSilent(AudioSource source)
        {
            if (Silent || source == null) return;

            try { source.Play(); } catch { }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int replaced = 0;

            foreach (var ins in instructions)
            {
                if ((ins.opcode == OpCodes.Callvirt || ins.opcode == OpCodes.Call)
                    && ins.operand is MethodInfo mi
                    && mi.Name == "Play"
                    && mi.GetParameters().Length == 0
                    && mi.DeclaringType != null
                    && mi.DeclaringType.Name == "AudioSource")
                {
                    replaced++;
                    yield return new CodeInstruction(OpCodes.Call, _playIfNotSilent);
                    continue;
                }

                yield return ins;
            }

            if (ModEntry.Log != null)
                ModEntry.Log.Log("[gun-sound] transpiler replaced " + replaced + " AudioSource.Play call(s)");
        }

        private static void Warn(string message)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("[gun-sound] " + message);
        }
    }
#endif
}
