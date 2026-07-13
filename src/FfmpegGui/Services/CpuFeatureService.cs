using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace FfmpegGui.Services
{
    public static class CpuFeatureService
    {
        private static bool _detected = false;

        // ── 架构属性 ──
        public static bool IsArm64 { get; private set; }
        public static bool IsX64 { get; private set; }

        // ── X86 特性 ──
        public static bool HasSse2 { get; private set; }
        public static bool HasSse41 { get; private set; }
        public static bool HasAvx { get; private set; }
        public static bool HasAvx2 { get; private set; }
        public static bool HasAvx512F { get; private set; }

        // ── ARM 特性 ──
        public static bool HasAdvSimd { get; private set; }
        public static bool HasNeon => HasAdvSimd;

        // ── 综合 ──
        public static bool HasAnySimd { get; private set; }

        public static void Detect()
        {
            if (_detected) return;
            _detected = true;

            var arch = RuntimeInformation.ProcessArchitecture;
            IsArm64 = arch == Architecture.Arm64;
            IsX64  = arch == Architecture.X64;
            bool isX86 = arch is Architecture.X86 or Architecture.X64;

            if (isX86)
            {
                // 🔒 仅在 x86/x64 进程中访问 X86 intrinsics（ARM64 上这些类型不存在）
                try { HasSse2  = System.Runtime.Intrinsics.X86.Sse2.IsSupported;  } catch { }
                try { HasSse41 = System.Runtime.Intrinsics.X86.Sse41.IsSupported; } catch { }
                try { HasAvx   = System.Runtime.Intrinsics.X86.Avx.IsSupported;   } catch { }
                try { HasAvx2  = System.Runtime.Intrinsics.X86.Avx2.IsSupported;  } catch { }
                // Avx512F 通过反射安全检测（某些旧运行时可能不存在）
                try
                {
                    var asm = typeof(System.Runtime.Intrinsics.X86.Avx2).Assembly;
                    var t = asm.GetType("System.Runtime.Intrinsics.X86.Avx512F");
                    if (t != null)
                    {
                        var prop = t.GetProperty("IsSupported", BindingFlags.Public | BindingFlags.Static);
                        if (prop != null)
                            HasAvx512F = (bool)prop.GetValue(null)!;
                    }
                }
                catch { }
            }

            if (IsArm64)
            {
                // 🔒 仅在 ARM64 进程中访问 ARM intrinsics
                try { HasAdvSimd = System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported; } catch { }
            }

            HasAnySimd = HasAvx2 || HasAvx || HasSse41 || HasSse2 || HasAvx512F || HasAdvSimd;
        }

        public static string Summary()
        {
            Detect();
            return $"SSE2={HasSse2}, SSE4.1={HasSse41}, AVX={HasAvx}, AVX2={HasAvx2}, AVX512F={HasAvx512F}, AdvSimd={HasAdvSimd}";
        }
    }
}
