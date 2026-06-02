using System;
using System.Reflection;

namespace FfmpegGui.Services
{
    public static class CpuFeatureService
    {
        private static bool _detected = false;

        public static bool HasSse2 { get; private set; }
        public static bool HasSse41 { get; private set; }
        public static bool HasAvx { get; private set; }
        public static bool HasAvx2 { get; private set; }
        public static bool HasAvx512F { get; private set; }
        public static bool HasAdvSimd { get; private set; }

        public static void Detect()
        {
            if (_detected) return;
            _detected = true;
            try
            {
                try { HasSse2 = System.Runtime.Intrinsics.X86.Sse2.IsSupported; } catch { HasSse2 = false; }
                try { HasSse41 = System.Runtime.Intrinsics.X86.Sse41.IsSupported; } catch { HasSse41 = false; }
                try { HasAvx = System.Runtime.Intrinsics.X86.Avx.IsSupported; } catch { HasAvx = false; }
                try { HasAvx2 = System.Runtime.Intrinsics.X86.Avx2.IsSupported; } catch { HasAvx2 = false; }
                // Avx512F may not exist in older runtimes - use reflection to check safely
                try
                {
                    var asm = typeof(System.Runtime.Intrinsics.X86.Avx2).Assembly;
                    var t = asm.GetType("System.Runtime.Intrinsics.X86.Avx512F");
                    if (t != null)
                    {
                        var prop = t.GetProperty("IsSupported", BindingFlags.Public | BindingFlags.Static);
                        if (prop != null)
                        {
                            HasAvx512F = (bool)prop.GetValue(null)!;
                        }
                    }
                }
                catch { HasAvx512F = false; }

                try { HasAdvSimd = System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported; } catch { HasAdvSimd = false; }
            }
            catch { }
        }

        public static string Summary()
        {
            Detect();
            return $"SSE2={HasSse2}, SSE4.1={HasSse41}, AVX={HasAvx}, AVX2={HasAvx2}, AVX512F={HasAvx512F}, AdvSimd={HasAdvSimd}";
        }
    }
}
