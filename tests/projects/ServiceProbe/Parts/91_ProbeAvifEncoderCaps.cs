using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FfmpegGui.Models;

namespace ServiceProbe
{
    internal static partial class Program
    {
        /// <summary>
        /// avifcaps —— 2026-11-23 修复计划 R1/R6：显式 AVIF 位深请求超过编码器上限时必须「显式拒绝」，
        /// 不得被 Math.Min 静默降位。
        /// 红绿口径：修复前 libsvtav1/qsv/amf/vaapi/未知编码器 + 12bit 不抛（RED）；
        /// 修复后抛 ColorOutputContractException（GREEN）。
        /// 只调纯函数，不起 ffmpeg、不编码、不涉及全量基线。
        /// 类型与方法均用反射定位（ValidateAvifRequestedBitDepth 为 internal，不依赖 InternalsVisibleTo，
        /// 也不依赖承载类的名字）。
        /// </summary>
        private static Task ProbeAvifEncoderCaps(string[] args)
        {
            var asm = typeof(FfmpegOptions).Assembly;
            const BindingFlags F = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

            Type? host = asm.GetTypes().FirstOrDefault(t => t.GetMethod("ValidateAvifRequestedBitDepth", F) != null);
            Check(host != null, "找到 ValidateAvifRequestedBitDepth 的承载类型（反射）");
            if (host == null) return Task.CompletedTask;

            MethodInfo validate = host.GetMethod("ValidateAvifRequestedBitDepth", F)!;
            MethodInfo? maxFor = host.GetMethod("AvifMaxBitDepthForEncoder", F);
            Check(maxFor != null, "找到 AvifMaxBitDepthForEncoder（反射）");

            // 返回: null=未拒绝；否则=异常类型名
            string? Run(string? encoder, int? requested)
            {
                var o = new FfmpegOptions { Format = "avif", Encoder = encoder, BitDepthRequested = requested };
                try { validate.Invoke(null, new object[] { o }); return null; }
                catch (TargetInvocationException e) { return e.InnerException?.GetType().Name ?? "unknown"; }
            }

            int? Max(string? encoder) => maxFor == null ? null : (int?)maxFor.Invoke(null, new object?[] { encoder });

            var capped = new[] { "av1_nvenc", "libsvtav1", "av1_qsv", "av1_amf", "av1_vaapi", "some-unknown-av1" };

            // —— 应拒绝：显式 12bit 超过上限 10 的编码器（R1）——
            foreach (var enc in capped)
            {
                var r = Run(enc, 12);
                Check(r == "ColorOutputContractException",
                    $"R1 {enc} + 显式 12bit ⇒ 显式拒绝（实际: {r ?? "未拒绝=静默降位"}）");
            }

            // —— 不应拒绝（防过拒）——
            Check(Run("libaom-av1", 12) == null, "R1 libaom-av1 + 12bit ⇒ 放行");
            Check(Run(null, 12) == null, "R1 默认编码器(libaom) + 12bit ⇒ 放行");
            foreach (var enc in capped.Where(e => e != "some-unknown-av1"))
            {
                Check(Run(enc, 8) == null, $"R1 {enc} + 8bit ⇒ 放行");
                Check(Run(enc, 10) == null, $"R1 {enc} + 10bit ⇒ 放行");
                Check(Run(enc, null) == null, $"R1 {enc} + 未显式指定 ⇒ 放行");
            }
            // 16bit 走独立的 Sample Transform 资格判定，不归本函数
            Check(Run("libaom-av1", 16) == null, "R1 libaom-av1 + 16bit ⇒ 本函数不拦（独立契约）");

            // —— R6 雏形：上限表自洽 ——
            Check(Max("libaom-av1") == 12, "R6 libaom 上限 12");
            foreach (var enc in capped)
                Check(Max(enc) == 10, $"R6 {enc} 上限 10");
            return Task.CompletedTask;
        }
    }
}
