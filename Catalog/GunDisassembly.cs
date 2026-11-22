using System;
using System.Collections.Generic;

namespace ItemCatalog.Catalog
{
    /// <summary>
    /// 螺丝刀拆枪的**回退**产出表。
    ///
    /// 正常路径是 <see cref="GunDisassemblyProbe"/>：在隔离容器里真跑一次
    /// <c>GunHelper.DisassembleWeapon</c>，结果缓存到本地。这里这张表只在
    /// 模拟失败（系统没就绪 / 容器拒收 / 抛异常）时兜底，内容照抄游戏
    /// 2026-11-22 的 IL：<c>DisassembleWeapon</c> 是一串按枪 id 写死的 if/else，
    /// 没有数据表可读。重复的 id 表示个数，与 <c>GraphUtils.EmporiumTryAdd</c>
    /// 的调用次数一致；表里没有的枪拆完不掉东西。
    /// </summary>
    internal static class GunDisassembly
    {
        private static readonly Dictionary<string, string[]> Outputs =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                { "printed_gun", new[] { "printer_plastic", "printer_plastic" } },
                { "handmade_pistol", new[] {
                    "gun_part", "gun_part", "printer_plastic", "scrap_metal", "scrap_metal" } },
                { "heavy_handmade_pistol", new[] {
                    "gun_part", "gun_part", "printer_plastic",
                    "scrap_metal", "scrap_metal", "scrap_metal", "scrap_metal" } },
                { "heavy_handmade_pistol_s", new[] {
                    "gun_part", "gun_part", "gun_part", "printer_plastic",
                    "scrap_metal", "scrap_metal", "scrap_metal", "scrap_metal" } },
                { "glock_receiver", new[] {
                    "gun_part", "gun_part", "gun_part", "gun_part" } },
                { "revolver_short_tac", new[] {
                    "gun_part", "gun_part", "gun_part", "gun_part", "gun_part" } },
                { "revolver", new[] {
                    "gun_part", "gun_part", "gun_part", "gun_part", "gun_part" } },
                { "smg", new[] {
                    "gun_part", "gun_part", "gun_part", "gun_part", "gun_part", "gun_part" } },
                { "shotgun", new[] {
                    "gun_part", "gun_part", "gun_part", "gun_part",
                    "gun_part", "gun_part", "gun_part" } }
            };

        private static readonly string[] Empty = new string[0];

        internal static string[] OutputsFor(string gunId)
        {
            if (string.IsNullOrEmpty(gunId)) return Empty;

            string[] outputs;
            return Outputs.TryGetValue(gunId, out outputs) ? outputs : Empty;
        }
    }
}
