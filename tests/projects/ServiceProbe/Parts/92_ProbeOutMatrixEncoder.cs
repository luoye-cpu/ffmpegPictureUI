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
        /// outmatrixenc —— 2026-11-23 修复计划 R2（G1）：AVIF 输出矩阵能力判定须感知具体编码器。
        /// 口径与出口层一致：libsvtav1 无 gbrp（ImageEncoderArgs 已 block），故 avif+rgb+libsvtav1 ⇒ Rejected；
        /// libaom 与 encoder=null（旧调用）保持 Carried，旧 238 格判定不变。
        /// nvenc/qsv/amf 暂无实机证据，本用例不对其下结论（避免把未验证的能力写成事实）。
        /// 只调纯函数，不起 ffmpeg，不涉及全量基线。方法与枚举均用反射定位。
        /// </summary>
        private static Task ProbeOutMatrixEncoder(string[] args)
        {
            var asm = typeof(FfmpegOptions).Assembly;
            const BindingFlags F = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

            Type? host = asm.GetTypes().FirstOrDefault(t => t.GetMethod("ResolveOutMatrixCapability", F) != null);
            Check(host != null, "找到 ResolveOutMatrixCapability 的承载类型（反射）");
            if (host == null) return Task.CompletedTask;

            MethodInfo m = host.GetMethod("ResolveOutMatrixCapability", F)!;
            var ps = m.GetParameters();
            Check(ps.Length == 4 && ps[3].ParameterType == typeof(string),
                $"R2 ResolveOutMatrixCapability 带第 4 个 string? encoder 参数（实际参数数: {ps.Length}）");
            if (ps.Length != 4) return Task.CompletedTask;

            object ffmpegBackend = Enum.Parse(ps[2].ParameterType, "Ffmpeg");

            string Cap(string fmt, string token, string? encoder)
                => m.Invoke(null, new object?[] { fmt, token, ffmpegBackend, encoder })!.ToString()!;

            // —— G1：libsvtav1 + rgb ⇒ Rejected ——
            Check(Cap("avif", "rgb", "libsvtav1") == "Rejected", $"R2 avif+rgb+libsvtav1 ⇒ Rejected（实际: {Cap("avif", "rgb", "libsvtav1")}）");

            // —— 不得过拒 ——
            Check(Cap("avif", "rgb", "libaom-av1") == "Carried", "R2 avif+rgb+libaom-av1 ⇒ Carried");
            Check(Cap("avif", "rgb", null) == "Carried", "R2 avif+rgb+encoder=null ⇒ Carried（旧行为不变）");
            foreach (var tok in new[] { "709", "601", "240m", "2020nc" })
                Check(Cap("avif", tok, "libsvtav1") == Cap("avif", tok, null), $"R2 avif+{tok}+libsvtav1 与 encoder=null 判定一致（YUV 矩阵不受影响）");
            foreach (var tok in new[] { "ycgco", "fcc", "xyb" })
                Check(Cap("avif", tok, "libsvtav1") == "Rejected", $"R2 avif+{tok}+libsvtav1 仍 Rejected");

            // 非 AVIF 格式不受编码器参数影响
            foreach (var fmt in new[] { "png", "webp", "jxl" })
                Check(Cap(fmt, "rgb", "libsvtav1") == Cap(fmt, "rgb", null), $"R2 {fmt}+rgb 不受 encoder 参数影响");
            return Task.CompletedTask;
        }
    }
}
