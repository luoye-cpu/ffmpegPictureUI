using System.Runtime.Intrinsics.X86;

namespace FfmpegGui.Services
{
    /// <summary>
    /// 逐像素 SIMD 内核的**唯一选路站点**，也是面板「SIMD 优化」复选框
    /// （AppSettings.AutoUseSimdBinaries）的**唯一消费点**。
    ///
    /// 为什么单独一个类（2026-09-27）：
    ///   这个开关此前只有写入端（复选框 → AppSettingsService.Save()），
    ///   全仓**没有任何代码读它决定走不走 SIMD**：
    ///     · PsnrCalculator.SquaredDiffSum 无条件按 Avx512BW → Avx2 → Sse2 的优先级选路；
    ///     · SimdPixelOps.FloatToSrgb8 无条件看 Avx2.IsSupported。
    ///   而两条本地化文案（tip.simd）都在**承诺**一个回退：
    ///   中文「关闭后逐像素回退，仅用于排查」/ 英文「falls back to scalar code」。
    ///   ⇒ 声明与实现冲突。本仓铁律「降级不是修复」⇒ 把实现提到声明的水位，
    ///     而不是把 tooltip 改弱。
    ///
    /// ⚠ 判据只此一份：新增任何「按指令集挑内核」的站点一律调本类，
    ///   **不得**在调用点各写一遍「读设置 + 排 ISA 优先级」
    ///   （本仓因探测逻辑四处拷贝、口径不一吃过多次亏）。
    ///   对应的结构锁见 tests/scripts/verify-simd-switch-fallback.ps1 的 ③。
    /// </summary>
    public static class SimdKernelRouting
    {
        // 路径名字面量与 PsnrCalculator 的内核分派表**同源**（同一组常量，不各处手写小写串）。
        public const string PathAvx512 = "avx512";
        public const string PathAvx2 = "avx2";
        public const string PathSse2 = "sse2";
        public const string PathScalar = "scalar";

        /// <summary>
        /// 面板复选框当前是否允许走 SIMD 内核。
        /// 全仓唯一一处读 <c>AutoUseSimdBinaries</c> 的非 UI / 非持久化代码。
        /// 每次调用现读、不缓存 ⇒ 面板勾选**立即**生效，无需重启（与 tooltip 的「排查」用途一致）。
        /// </summary>
        public static bool UserAllowsSimd => AppSettingsService.Current.AutoUseSimdBinaries;

        /// <summary>
        /// 8-bit 逐像素内核选路：
        ///   开关关 ⇒ **恒 scalar**，与本机有没有 AVX-512 无关（这就是 tooltip 承诺的那条回退）；
        ///   开关开 ⇒ 按本机 ISA 取最优：AVX512(BW) → AVX2 → SSE2 → scalar。
        ///
        /// ⚠ 默认（开）路径与加这个开关前**逐位一致**：同样的三级 IsSupported 优先级、
        ///   同样的四个内核、同样的调用点，只是判据搬到了本函数里。
        ///   数值等价的实证锁在 tests/ServiceProbe 的 simdswitch mode（S2：两条路 SSE 必须相等，
        ///   并与探针自带的第三份独立标量实现逐字节对照）。
        /// </summary>
        public static string ResolvePixelKernelPath()
        {
            if (!UserAllowsSimd) return PathScalar;
            if (Avx512BW.IsSupported) return PathAvx512;
            if (Avx2.IsSupported) return PathAvx2;
            if (Sse2.IsSupported) return PathSse2;
            return PathScalar;   // 非 x86 / 无 SSE2 的运行时：本来就只有标量
        }

        /// <summary>
        /// SimdPixelOps.FloatToSrgb8 走不走 AVX2 —— 同一个开关、同一处判定。
        /// 该核只需要 AVX2（无 AVX-512 变体），故不套用上面的优先级链。
        /// </summary>
        public static bool FloatToSrgb8UsesSimd => UserAllowsSimd && Avx2.IsSupported;
    }
}
