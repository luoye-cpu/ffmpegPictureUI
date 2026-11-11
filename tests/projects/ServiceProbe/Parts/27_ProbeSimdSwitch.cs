using System;
using System.IO;
using System.Text;
using FfmpegGui.Services;
using TC = FfmpegGui.Services.ColorMapping.TransferCurve;   // 色彩映射引擎的曲线表（P1）
using CK = FfmpegGui.Services.ColorMapping.ColorKernels;     // 执行内核（P3）
using CM = FfmpegGui.Services.ColorMapping.ColorMetrics;     // PSNR/ΔE 度量
using CS = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor;
using CE = FfmpegGui.Services.ColorMapping.ColorMappingEngine;
using CI = FfmpegGui.Services.ColorMapping.ColorIntent;
using CA = FfmpegGui.Services.ColorMapping.ColorAction;
using CB = FfmpegGui.Services.ColorMapping.ColorBackend;
using CL = FfmpegGui.Services.ColorMapping.ColorLoss;
using ST = FfmpegGui.Models.ColorStrategy;
using DC = FfmpegGui.Services.ColorMapping.DescriptorConfidence;
using OR = FfmpegGui.Services.ColorMapping.OverrideReason;
using AC = FfmpegGui.Services.ColorMapping.AnnotChoice;
using CSC = FfmpegGui.Services.ColorMapping.CarryScope;
using MI = FfmpegGui.Services.ColorMapping.ManualIntent;
using CP = FfmpegGui.Services.ColorMapping.ColorTransformPlan;   // S5′ 裁决探针
using GF = FfmpegGui.Services.ColorMapping.RawGamutFit;         // `--raw-color-target auto` 判据内核（2026-10-04）
using VK = FfmpegGui.Services.ColorMapping.VerdictKind;          // S5′ 裁决探针
using DG = FfmpegGui.Services.ColorMapping.DegradationCodes;     // S5′ 裁决探针
using F709 = FfmpegGui.Services.ColorMapping.Transfer709Curve;
using GMJ = FfmpegGui.Services.ColorMapping.GainMapJpegCodec;   // GainMap 解码口径/编码参数单一真值（gmgamut）
using AAS = FfmpegGui.Services.AppSettingsService;

namespace ServiceProbe
{
    internal static partial class Program
    {
        /// <summary>
        /// simdswitch —— 面板「SIMD 优化」(AppSettings.AutoUseSimdBinaries) 的**消费点**行为锁
        /// （2026-09-27 新增；编排与结构锁见 tests/scripts/verify-simd-switch-fallback.ps1）。
        ///
        /// 立锁动机：这个开关此前**只有写入端**（复选框 → Save() → settings.json），全仓
        /// **没有任何代码读它决定走不走 SIMD**，而 zh/en 两条 tooltip 都在承诺「关闭后逐像素回退」
        /// ⇒ 声明与实现冲突。实现落在 SimdKernelRouting（判据只此一份），本 mode 锁它**真的被消费**。
        ///
        /// 判据形状（全程进程内、确定性合成像素，不需要素材 / 外部 exe / 特定硬件）：
        ///   S0 本进程**从配置里**拿到的开关值 → 选路（配置→内核这一跳；编排脚本再用
        ///      FFMPEGGUI_AUTO_SIMD=false/true 各跑一次，交叉判定这两行确实跟着配置走）；
        ///   S1 选路本身：关 ⇒ 恒 scalar（与本机 ISA 无关）；开 ⇒ 探针自带的那份 ISA 优先级读数；
        ///   S2/S3 同一份输入、开关开/关两次：8-bit SSE 逐位相等，且回退那条与**探针自带的第三份
        ///         独立标量实现**逐字节相等（⇒ 「标量分支偷偷仍走 SIMD / 错位」都会红）；
        ///   S4 有牙自证：把 b 整体错位 1 字节 ⇒ SSE 与 PSNR 必须**跳出** S2/S3 的容差，
        ///      否则那两条只是在量「两边都是同一个错值」，容差本身没有牙；
        ///   S5 下限断言：输入必须真的有差异（全等帧 ⇒ 任何内核都返回 0 ⇒ 空断言恒绿通道）；
        ///   S6 SimdPixelOps 那一路消费者（tooltip 说的是「SIMD 加速的像素处理」，不止 PSNR）。
        ///
        /// ⚠ 正确性不依赖硬件：只有 S0a 那一臂需要「本机至少 SSE2」（win-x64 恒成立），
        ///   判不了时走 Skip() 点名，不静默消失。AVX-512/AVX2 只影响**性能读数**，不影响任何判据。
        /// </summary>
        private static void ProbeSimdSwitch()
        {
            const double PsnrTolDb = 1e-9;   // 同一 SSE ⇒ 同一 double 算式 ⇒ 实差应为 0，这里只留一点余量

            bool hasAvx512 = System.Runtime.Intrinsics.X86.Avx512BW.IsSupported;
            bool hasAvx2 = System.Runtime.Intrinsics.X86.Avx2.IsSupported;
            bool hasSse2 = System.Runtime.Intrinsics.X86.Sse2.IsSupported;
            Console.WriteLine($"isa-avx512bw={hasAvx512} isa-avx2={hasAvx2} isa-sse2={hasSse2}");

            // 探针自带的第二份「优先级」读数（测试端重述判据是**应该**的，否则实现抄错无人知）
            string WantKernel(bool allowSimd)
                => !allowSimd ? SimdKernelRouting.PathScalar
                 : hasAvx512 ? SimdKernelRouting.PathAvx512
                 : hasAvx2 ? SimdKernelRouting.PathAvx2
                 : hasSse2 ? SimdKernelRouting.PathSse2
                 : SimdKernelRouting.PathScalar;

            // ── S0：配置 → 选路（**改写任何设置之前**的一次性读数）──
            bool startupFlag = AAS.Current.AutoUseSimdBinaries;
            string startupKernel = SimdKernelRouting.ResolvePixelKernelPath();
            Console.WriteLine($"startup-flag={startupFlag}");
            Console.WriteLine($"startup-kernel={startupKernel}");
            Check(startupFlag || startupKernel == SimdKernelRouting.PathScalar,
                $"S0 配置说「关」（startup-flag={startupFlag}）⇒ 启动选路必须是 scalar（实 {startupKernel}）");
            if (startupFlag && hasSse2)
                Check(startupKernel != SimdKernelRouting.PathScalar,
                    $"S0 配置说「开」且本机有 SSE2 ⇒ 启动选路不得是 scalar（实 {startupKernel}）");
            else Skip($"S0 的「开 ⇒ 非 scalar」这一臂本机判不了（startup-flag={startupFlag} isa-sse2={hasSse2}）");

            // ── S1：开关两态的选路读数（进程内直接改写设置，与面板写的是同一个属性）──
            // ⚠ 哨兵初值不是装饰：try 里的赋值在 C# 的确定性赋值规则下**不算已赋值**，
            //   且万一 try 抛异常（Main 会 catch 成 FAIL），这些值会一路带到断言里 ⇒ 必须**判红**而不是编译不过/静默绿。
            string kernelOn = "?", kernelOff = "?";
            var prior = AAS.Current.AutoUseSimdBinaries;
            try
            {
                AAS.Current.AutoUseSimdBinaries = true;
                kernelOn = SimdKernelRouting.ResolvePixelKernelPath();
                AAS.Current.AutoUseSimdBinaries = false;
                kernelOff = SimdKernelRouting.ResolvePixelKernelPath();
            }
            finally { AAS.Current.AutoUseSimdBinaries = prior; }
            Console.WriteLine($"kernel-on={kernelOn} kernel-off={kernelOff}");
            Check(kernelOn == WantKernel(true),
                $"S1a 开关开 ⇒ 选路 = 本机 ISA 优先级（期望 {WantKernel(true)}，实 {kernelOn}）");
            Check(kernelOff == SimdKernelRouting.PathScalar,
                $"S1b 开关关 ⇒ 选路 = scalar，与本机有无 AVX-512 无关（实 {kernelOff}；isa={hasAvx512}/{hasAvx2}/{hasSse2}）");
            Check(kernelOff != kernelOn || !hasSse2,
                $"S1c 开关必须**真的换掉内核**（开={kernelOn} 关={kernelOff}）⇒ 否则它仍是空开关");

            // 确定性合成：LCG 字节对，差异散布在 ~1/7 + ~1/11 的样本上（±128 与大 ±1 两种幅度）
            static (byte[] a, byte[] b) Synth(int len)
            {
                var a = new byte[len];
                var b = new byte[len];
                uint s = 12345u + (uint)len;
                for (int i = 0; i < len; i++)
                {
                    s = unchecked(s * 1664525u + 1013904223u);
                    a[i] = (byte)(s >> 24);
                }
                for (int i = 0; i < len; i++)
                {
                    b[i] = a[i];
                    if (i % 7 == 3) b[i] = (byte)(a[i] ^ 0x80);
                    else if (i % 11 == 5) b[i] = (byte)(a[i] == 0 ? 1 : a[i] - 1);
                }
                return (a, b);
            }
            // 第三份实现：探针自己写的逐像素标量循环（不经任何选路，作为绝对参照）
            static long ScalarOracle(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
            {
                long acc = 0;
                for (int i = 0; i < x.Length; i++) { int d = x[i] - y[i]; acc += (long)d * d; }
                return acc;
            }
            static int DiffCount(byte[] x, byte[] y)
            {
                int n = 0;
                for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) n++;
                return n;
            }

            var cases8 = new (string name, int len, int channels)[]
            {
                ("8bit-64字节对齐", 64 * 64 * 3, 3),
                ("8bit-带标量尾巴", 41 * 37 * 3, 3),     // 4551：不满一个 32/64 字节向量，逼尾循环
                ("8bit-灰度单通道", 1001, 1),
            };
            foreach (var c in cases8)
            {
                var (a, b) = Synth(c.len);
                int diffCount = DiffCount(a, b);
                Check(diffCount >= 32 && c.len % c.channels == 0,
                    $"S5 {c.name} 输入下限：差异样本 {diffCount} ≥ 32 且长度可被通道数整除"
                  + $"⇒ 否则「全等帧 ⇒ 每条内核都返回 0」会把下面所有等式假绿");

                long sseOn = long.MinValue, sseOff = long.MinValue;
                double psnrOn = double.NaN, psnrOff = double.NaN;
                prior = AAS.Current.AutoUseSimdBinaries;
                try
                {
                    AAS.Current.AutoUseSimdBinaries = true;
                    sseOn = PsnrCalculator.SquaredDiffSum(a, b);
                    psnrOn = PsnrCalculator.CalculatePsnr(a, b, 8, c.channels, true);
                    AAS.Current.AutoUseSimdBinaries = false;
                    sseOff = PsnrCalculator.SquaredDiffSum(a, b);
                    psnrOff = PsnrCalculator.CalculatePsnr(a, b, 8, c.channels, true);
                }
                finally { AAS.Current.AutoUseSimdBinaries = prior; }
                long oracle = ScalarOracle(a, b);
                Console.WriteLine($"  {c.name}: len={c.len} diffs={diffCount} sse(on|off|oracle)={sseOn}|{sseOff}|{oracle} "
                    + $"psnr on={psnrOn:F9} off={psnrOff:F9}");
                Check(sseOff == oracle,
                    $"S3 {c.name} 回退分支**真是逐像素标量**：与探针自带的第三份实现相等（关={sseOff} 参照={oracle}）");
                Check(sseOn == sseOff,
                    $"S2 {c.name} 开关开/关两次 SSE 逐位相等（开={sseOn} 关={sseOff}）⇒ 默认路径数值一个字没动");
                Check(double.IsFinite(psnrOn) && double.IsFinite(psnrOff)
                      && Math.Abs(psnrOn - psnrOff) <= PsnrTolDb,
                    $"S2b {c.name} 开关开/关两次的 PSNR 差 ≤ {PsnrTolDb:g}dB（实 {Math.Abs(psnrOn - psnrOff):E3}）");

                // ── S4 有牙自证：把 b 整体错位一格 ⇒ 必须跳出上面那两条的容差 ──
                var bMis = new byte[c.len];
                for (int i = 0; i < c.len; i++) bMis[i] = b[(i + 1) % c.len];
                long sseMis = ScalarOracle(a, bMis);
                double psnrMis = PsnrCalculator.CalculatePsnr(a, bMis, 8, c.channels, true);
                Check(sseMis != sseOff && Math.Abs(psnrMis - psnrOff) > PsnrTolDb,
                    $"S4 {c.name} 负控：错位一格的标量结果（sse={sseMis} psnr={psnrMis:F6}）必须被 S2/S2b 抓到"
                  + $"（实差 SSE={Math.Abs(sseMis - sseOff)} PSNR={Math.Abs(psnrMis - psnrOff):E3}）"
                  + $"⇒ 否则上面那两条只是「两边同一个错值」，容差没有牙");
            }

            // ── S3b 多帧（真实管线 QualityAnalysisService 用的就是这个入口）──
            {
                var (fa, fb) = Synth(64 * 32 * 3);
                int frame = fa.Length;
                var aa = new byte[frame * 2];
                var bb = new byte[frame * 2];
                Array.Copy(fa, 0, aa, 0, frame);          // 帧 1 与参考全等 ⇒ 该帧 PSNR = inf
                Array.Copy(fa, 0, bb, 0, frame);
                Array.Copy(fa, 0, aa, frame, frame);      // 帧 2 才带差异
                Array.Copy(fb, 0, bb, frame, frame);
                (double Average, double Min, double Max) on = (double.NaN, double.NaN, double.NaN);
                (double Average, double Min, double Max) off = (double.NaN, double.NaN, double.NaN);
                prior = AAS.Current.AutoUseSimdBinaries;
                try
                {
                    AAS.Current.AutoUseSimdBinaries = true;
                    on = PsnrCalculator.CalculateMultiFramePsnr(aa, bb, frame, 8, 3, true);
                    AAS.Current.AutoUseSimdBinaries = false;
                    off = PsnrCalculator.CalculateMultiFramePsnr(aa, bb, frame, 8, 3, true);
                }
                finally { AAS.Current.AutoUseSimdBinaries = prior; }
                Console.WriteLine($"  多帧: on={on.Average:F9}/{on.Min:F9}/{on.Max:F9} off={off.Average:F9}/{off.Min:F9}/{off.Max:F9}");
                Check(double.IsPositiveInfinity(on.Max) && double.IsPositiveInfinity(off.Max),
                    $"S3a 多帧 max=inf（帧 1 全等）在两态都成立（on={on.Max} off={off.Max}）⇒ 回退没把 inf 语义吃掉");
                Check(Math.Abs(on.Average - off.Average) <= PsnrTolDb
                      && Math.Abs(on.Min - off.Min) <= PsnrTolDb,
                    $"S3b 多帧 avg/min 两态差 ≤ {PsnrTolDb:g}dB（实 {Math.Abs(on.Average - off.Average):E3} / {Math.Abs(on.Min - off.Min):E3}）");
            }

            // ── S3c 16-bit 路径：本来就是逐像素标量 ⇒ 开关两态必须同样逐位一致 ──
            {
                var (a16, b16) = Synth(60 * 40 * 3 * 2);
                double pOn = double.NaN, pOff = double.NaN;
                prior = AAS.Current.AutoUseSimdBinaries;
                try
                {
                    AAS.Current.AutoUseSimdBinaries = true;
                    pOn = PsnrCalculator.CalculatePsnr(a16, b16, 16, 3, true);
                    AAS.Current.AutoUseSimdBinaries = false;
                    pOff = PsnrCalculator.CalculatePsnr(a16, b16, 16, 3, true);
                }
                finally { AAS.Current.AutoUseSimdBinaries = prior; }
                Console.WriteLine($"  16bit: on={pOn:F9} off={pOff:F9}");
                Check(double.IsFinite(pOn) && Math.Abs(pOn - pOff) <= PsnrTolDb,
                    $"S3c 16-bit 两态 PSNR 逐位一致（{pOn:F6} vs {pOff:F6}）⇒ 开关没被错接进 16-bit 分支");
            }

            // ── S6：SimdPixelOps 那一路消费者（tooltip 说的是「SIMD 加速的像素处理」，不止 PSNR）──
            {
                int nF = 512;
                var fsrc = new float[nF];
                uint fs = 987654321u;
                for (int i = 0; i < nF; i++)
                {
                    fs = unchecked(fs * 1664525u + 1013904223u);
                    fsrc[i] = i switch
                    {
                        0 => 0f,
                        1 => 1f,
                        2 => 0.0031308f,        // sRGB 编码阈值（分支边界）
                        3 => 0.00313081f,
                        _ => (int)((fs >> 8) & 0xFFFFFF) / (float)0xFFFFFF,
                    };
                }
                var dOn = new byte[nF];
                var dOff = new byte[nF];
                prior = AAS.Current.AutoUseSimdBinaries;
                try
                {
                    AAS.Current.AutoUseSimdBinaries = false;
                    Check(!SimdKernelRouting.FloatToSrgb8UsesSimd,
                        $"S6a 开关关 ⇒ FloatToSrgb8UsesSimd 必须为 false（实 {SimdKernelRouting.FloatToSrgb8UsesSimd}，isa-avx2={hasAvx2}）");
                    SimdPixelOps.FloatToSrgb8(fsrc, dOff);
                    AAS.Current.AutoUseSimdBinaries = true;
                    Check(SimdKernelRouting.FloatToSrgb8UsesSimd == hasAvx2,
                        $"S6b 开关开 ⇒ SIMD 判定 == 本机 Avx2.IsSupported（实 {SimdKernelRouting.FloatToSrgb8UsesSimd} vs {hasAvx2}）");
                    SimdPixelOps.FloatToSrgb8(fsrc, dOn);
                }
                finally { AAS.Current.AutoUseSimdBinaries = prior; }
                int offVsRef = 0, onVsOff = 0, shifted = 0;
                for (int i = 0; i < nF; i++)
                {
                    offVsRef = Math.Max(offVsRef, Math.Abs(dOff[i] - SimdPixelOps.FloatToSrgb8Scalar(fsrc[i])));
                    onVsOff = Math.Max(onVsOff, Math.Abs(dOn[i] - dOff[i]));
                    if (i > 0) shifted = Math.Max(shifted, Math.Abs(dOff[i - 1] - SimdPixelOps.FloatToSrgb8Scalar(fsrc[i])));
                }
                Console.WriteLine($"  float-to-srgb8: offVsScalarRef={offVsRef}LSB onVsOff={onVsOff}LSB shiftedMax={shifted}LSB isa-avx2={hasAvx2}");
                Check(offVsRef == 0, $"S6c 回退路径与标量参考 0 LSB 差（实 {offVsRef}）");
                Check(onVsOff <= 1, $"S6d 两态输出差 ≤ 1 LSB ⇒ 回退不改数值语义（实 {onVsOff}）");
                Check(shifted > 0,
                    $"S6e 负控：错位一格必须被 S6c 那种逐字节判据抓到（实错位差 {shifted} LSB）⇒ 否则 0 LSB 那条是空判据");
                if (hasAvx2 && onVsOff == 0)
                    Console.WriteLine("INFO S6 的数值臂本机不可判别（AVX2 的 LUT 近似恰好与标量逐字节相同）"
                        + "⇒ 该臂由 S6a/S6b 的选路读数 + 编排脚本的结构锁承担（不判红、也不假装验过）");
            }
        }

    }
}
