using System;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace FfmpegGui.Services
{
    /// <summary>
    /// .NET 原生 PSNR 计算器（2026-08-15 替换 ffmpeg psnr filter）。
    ///
    /// 方案 A (归一化) + 方案 C (按编码器原生域):
    ///   - 位深自适应: bitsPerSample=8/10/12/16, MaxValue=(1&lt;&lt;bitsPerSample)-1
    ///   - 色域标注: isRgb=true → "PSNR(RGB)", false → "PSNR(YUV)"
    ///   - 输入: 连续像素字节 (8-bit=1字节/通道, 16-bit=2字节/通道 LE)
    ///   - SIMD 仅加速 8-bit 路径 (16-bit 输入走标量, 精度不差且更简单)
    ///
    /// 性能实测 (9504x6336, 60MP, 8-bit):
    ///   ffmpeg psnr filter : ~359 ms
    ///   .NET AVX2          : ~17.6 ms (20× 加速)
    ///   .NET 标量(JIT向量化): ~113 ms (3× 加速)
    ///
    /// 实现 (2026-08-15 向前看齐 AVX512/AVX10):
    ///   AVX512 (BW) → AVX2 → SSE2 → 标量 (8-bit 像素)
    ///   16-bit 像素: 标量 (ReadOnlySpan&lt;ushort&gt; reinterpret)
    /// </summary>
    public static class PsnrCalculator
    {
        /// <summary>计算位深对应的峰值信号值</summary>
        public static int MaxValueForBits(int bitsPerSample) => (1 << bitsPerSample) - 1;

        /// <summary>
        /// 计算单帧 PSNR (dB)。
        /// </summary>
        /// <param name="a">参考帧像素 (字节序列)</param>
        /// <param name="b">待检帧像素 (字节序列)</param>
        /// <param name="bitsPerSample">位深 (8/10/12/16)</param>
        /// <param name="channels">通道数 (3=RGB/YUV, 4=RGBA, 1=灰度)</param>
        /// <param name="isRgb">true=RGB 域, false=YUV 域 (仅标注不影响数值)</param>
        /// <returns>PSNR 值 (dB), 无损时返回 PositiveInfinity</returns>
        public static double CalculatePsnr(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b,
            int bitsPerSample = 8, int channels = 3, bool isRgb = true)
        {
            if (a.Length != b.Length)
                throw new ArgumentException("两帧长度不一致", nameof(b));
            int bytesPerSample = bitsPerSample > 8 ? 2 : 1;
            int pixelCount = a.Length / (bytesPerSample * channels);

            if (bytesPerSample == 1)
            {
                // 8-bit: SIMD 加速
                var sse = SquaredDiffSum(a, b);
                double mse = (double)sse / (pixelCount * channels);
                double maxVal = MaxValueForBits(bitsPerSample);
                return mse <= 0 ? double.PositiveInfinity : 10 * Math.Log10(maxVal * maxVal / mse);
            }
            else
            {
                // 16-bit: 标量 (ushort 重解释)
                var au = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(a);
                var bu = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(b);
                long sum = 0;
                for (int i = 0; i < au.Length; i++)
                {
                    int d = au[i] - bu[i];
                    sum += (long)d * d;
                }
                double mse = (double)sum / (pixelCount * channels);
                double maxVal = MaxValueForBits(bitsPerSample);
                return mse <= 0 ? double.PositiveInfinity : 10 * Math.Log10(maxVal * maxVal / mse);
            }
        }

        /// <summary>兼容旧接口: 8-bit RGB</summary>
        public static double CalculatePsnr(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
            => CalculatePsnr(a, b, 8, 3, true);

        /// <summary>由平方误差和与像素数计算 PSNR (dB)。</summary>
        public static double PsnrFromSse(long sse, long pixelCount, int bitsPerSample = 8)
        {
            double mse = (double)sse / pixelCount;
            double maxVal = MaxValueForBits(bitsPerSample);
            return mse <= 0 ? double.PositiveInfinity : 10 * Math.Log10(maxVal * maxVal / mse);
        }

        /// <summary>
        /// 对多帧 (动图) 计算 PSNR 汇总。
        /// 语义与 ffmpeg psnr filter 一致:
        ///   average = 全局 MSE 的 PSNR; min/max = 各帧 PSNR 的极值。
        /// </summary>
        /// <param name="frameSizeBytes">单帧字节数 (w*h*channels*bytesPerSample)</param>
        public static (double Average, double Min, double Max) CalculateMultiFramePsnr(
            ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int frameSizeBytes,
            int bitsPerSample = 8, int channels = 3, bool isRgb = true)
        {
            if (frameSizeBytes <= 0)
                throw new ArgumentException("帧大小必须为正", nameof(frameSizeBytes));
            if (a.Length != b.Length)
                throw new ArgumentException("两序列长度不一致", nameof(b));

            int bytesPerSample = bitsPerSample > 8 ? 2 : 1;
            int pixelsPerFrame = frameSizeBytes / bytesPerSample / channels;

            long totalSse = 0;
            long totalPixels = 0;
            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;

            for (int off = 0; off + frameSizeBytes <= a.Length; off += frameSizeBytes)
            {
                var fa = a.Slice(off, frameSizeBytes);
                var fb = b.Slice(off, frameSizeBytes);
                long sse = bytesPerSample == 1
                    ? SquaredDiffSum(fa, fb)
                    : SquaredDiffSum16(fa, fb);
                double psnr = PsnrFromSse(sse, pixelsPerFrame * channels, bitsPerSample);
                totalSse += sse;
                totalPixels += pixelsPerFrame * channels;
                if (psnr < min) min = psnr;
                if (psnr > max) max = psnr;
            }
            // 尾部不足一帧的像素
            int tail = a.Length - (a.Length / frameSizeBytes) * frameSizeBytes;
            if (tail > 0)
            {
                var fa = a[^tail..];
                var fb = b[^tail..];
                long sse = bytesPerSample == 1
                    ? SquaredDiffSum(fa, fb)
                    : SquaredDiffSum16(fa, fb);
                double psnr = PsnrFromSse(sse, tail / bytesPerSample, bitsPerSample);
                totalSse += sse;
                totalPixels += tail / bytesPerSample;
                if (psnr < min) min = psnr;
                if (psnr > max) max = psnr;
            }

            if (totalPixels == 0)
                return (double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
            double avg = PsnrFromSse(totalSse, totalPixels, bitsPerSample);
            if (min == double.PositiveInfinity) min = avg;
            if (max == double.NegativeInfinity) max = avg;
            return (avg, min, max);
        }

        /// <summary>兼容旧接口: 8-bit RGB 多帧</summary>
        public static (double Average, double Min, double Max) CalculateMultiFramePsnr(
            ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int frameSizeBytes)
            => CalculateMultiFramePsnr(a, b, frameSizeBytes, 8, 3, true);

        // ═══════════════════════════════════════════════════
        // 平方误差和计算 (8-bit SIMD 加速)
        // ═══════════════════════════════════════════════════

        /// <summary>16-bit 平方误差和 (标量, ushort 重解释)</summary>
        private static long SquaredDiffSum16(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            var au = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(a);
            var bu = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(b);
            long sum = 0;
            for (int i = 0; i < au.Length; i++)
            {
                int d = au[i] - bu[i];
                sum += (long)d * d;
            }
            return sum;
        }

        /// <summary>
        /// 测试钩子: 强制使用指定指令集路径。
        /// </summary>
        internal static long SquaredDiffSumForced(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, string path)
        {
            if (a.Length != b.Length)
                throw new ArgumentException("两帧长度不一致", nameof(b));
            return path switch
            {
                "avx512" => SquaredDiffSumAvx512(a, b),
                "avx2" => SquaredDiffSumAvx2(a, b),
                "sse2" => SquaredDiffSumSse2(a, b),
                "scalar" => SquaredDiffSumScalar(a, b),
                _ => throw new ArgumentException($"未知路径: {path}", nameof(path))
            };
        }

        /// <summary>
        /// 对两帧等长 8-bit 像素计算平方误差和 (SIMD 加速)。
        /// 指令集优先级: AVX512 (BW) → AVX2 → SSE2 → 标量。
        /// </summary>
        public static long SquaredDiffSum(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            if (a.Length != b.Length)
                throw new ArgumentException("两帧长度不一致", nameof(b));

            if (Avx512BW.IsSupported)
                return SquaredDiffSumAvx512(a, b);
            if (Avx2.IsSupported)
                return SquaredDiffSumAvx2(a, b);
            if (Sse2.IsSupported)
                return SquaredDiffSumSse2(a, b);
            return SquaredDiffSumScalar(a, b);
        }

        // ═══════════════════════════════════════════════════
        // 实现 (8-bit SIMD 路径)
        // ═══════════════════════════════════════════════════

        /// <summary>AVX512 (BW): 64 字节/轮</summary>
        private static unsafe long SquaredDiffSumAvx512(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            long sum = 0;
            int i = 0;
            fixed (byte* pa = a)
            fixed (byte* pb = b)
            {
                var acc = Vector512<long>.Zero;
                int len = a.Length;
                for (; i + 64 <= len; i += 64)
                {
                    // 64 字节 → 两次 vpmovzxbw (32 字节 → 32×int16 零扩展)
                    var a16 = Avx512BW.ConvertToVector512Int16(Avx.LoadVector256(pa + i));
                    var b16 = Avx512BW.ConvertToVector512Int16(Avx.LoadVector256(pb + i));
                    var a16b = Avx512BW.ConvertToVector512Int16(Avx.LoadVector256(pa + i + 32));
                    var b16b = Avx512BW.ConvertToVector512Int16(Avx.LoadVector256(pb + i + 32));
                    var d = Avx512BW.Subtract(a16, b16);               // 差 ∈ [-255,255]
                    var db = Avx512BW.Subtract(a16b, b16b);
                    var sq = Avx512BW.MultiplyAddAdjacent(d, d);       // 16×int32
                    var sqb = Avx512BW.MultiplyAddAdjacent(db, db);
                    var t = Avx512F.Add(sq, sqb);                      // 16×int32
                    acc = Avx512F.Add(acc, Avx512F.ConvertToVector512Int64(t.GetLower().AsInt32())); // 8×int64
                    acc = Avx512F.Add(acc, Avx512F.ConvertToVector512Int64(t.GetUpper().AsInt32()));
                }
                sum = Vector512.Sum(acc);
            }
            for (; i < a.Length; i++)
            {
                int d = a[i] - b[i];
                sum += (long)d * d;
            }
            return sum;
        }

        /// <summary>AVX2: cvtepu8_epi16 零扩展 + madd_epi16 平方归约 (32 字节/轮)</summary>
        private static unsafe long SquaredDiffSumAvx2(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            long sum = 0;
            int i = 0;
            fixed (byte* pa = a)
            fixed (byte* pb = b)
            {
                var acc = Vector256<long>.Zero;
                int len = a.Length;
                for (; i + 32 <= len; i += 32)
                {
                    var va1 = Avx.LoadVector128(pa + i);
                    var vb1 = Avx.LoadVector128(pb + i);
                    var va2 = Avx.LoadVector128(pa + i + 16);
                    var vb2 = Avx.LoadVector128(pb + i + 16);
                    var a16 = Avx2.ConvertToVector256Int16(va1);   // 16×int16 零扩展
                    var b16 = Avx2.ConvertToVector256Int16(vb1);
                    var a16b = Avx2.ConvertToVector256Int16(va2);
                    var b16b = Avx2.ConvertToVector256Int16(vb2);
                    var d = Avx2.Subtract(a16, b16);               // 差 ∈ [-255,255]
                    var db = Avx2.Subtract(a16b, b16b);
                    var sq = Avx2.MultiplyAddAdjacent(d, d);       // 8 对 int16 → 4×int32
                    var sqb = Avx2.MultiplyAddAdjacent(db, db);
                    var t = Avx2.Add(sq, sqb);                     // 4×int32
                    acc = Avx2.Add(acc, Avx2.ConvertToVector256Int64(t.GetLower().AsInt32()));
                    acc = Avx2.Add(acc, Avx2.ConvertToVector256Int64(t.GetUpper().AsInt32()));
                }
                sum = Vector256.Sum(acc);
            }
            for (; i < a.Length; i++)
            {
                int d = a[i] - b[i];
                sum += (long)d * d;
            }
            return sum;
        }

        /// <summary>SSE2: unpack 零扩展 + pmaddwd 平方归约 (16 字节/轮)</summary>
        private static unsafe long SquaredDiffSumSse2(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            long sum = 0;
            int i = 0;
            fixed (byte* pa = a)
            fixed (byte* pb = b)
            {
                var zero = Vector128<byte>.Zero;
                int len = a.Length;
                // int32 向量累加, 每 4096 轮归约到 long (4096×2M≈8G < int32.MaxValue/4, 安全)
                var acc32 = Vector128<int>.Zero;
                const int ChunkRounds = 4096;
                int round = 0;
                for (; i + 32 <= len; i += 32)
                {
                    var va1 = Sse2.LoadVector128(pa + i);
                    var vb1 = Sse2.LoadVector128(pb + i);
                    var va2 = Sse2.LoadVector128(pa + i + 16);
                    var vb2 = Sse2.LoadVector128(pb + i + 16);
                    // 零扩展 byte→ushort: unpack(数据, 0) 即 cvtepu8_epi16
                    var a16 = Sse2.UnpackLow(va1, zero).AsInt16();
                    var b16 = Sse2.UnpackLow(vb1, zero).AsInt16();
                    var a16b = Sse2.UnpackHigh(va1, zero).AsInt16();
                    var b16b = Sse2.UnpackHigh(vb1, zero).AsInt16();
                    var a16c = Sse2.UnpackLow(va2, zero).AsInt16();
                    var b16c = Sse2.UnpackLow(vb2, zero).AsInt16();
                    var a16d = Sse2.UnpackHigh(va2, zero).AsInt16();
                    var b16d = Sse2.UnpackHigh(vb2, zero).AsInt16();
                    var d1 = Sse2.Subtract(a16, b16);
                    var d2 = Sse2.Subtract(a16b, b16b);
                    var d3 = Sse2.Subtract(a16c, b16c);
                    var d4 = Sse2.Subtract(a16d, b16d);
                    var sq1 = Sse2.MultiplyAddAdjacent(d1, d1);    // 4×int32
                    var sq2 = Sse2.MultiplyAddAdjacent(d2, d2);
                    var sq3 = Sse2.MultiplyAddAdjacent(d3, d3);
                    var sq4 = Sse2.MultiplyAddAdjacent(d4, d4);
                    var t1 = Sse2.Add(Sse2.Add(sq1, sq2), Sse2.Add(sq3, sq4));
                    acc32 = Sse2.Add(acc32, t1);
                    if (++round >= ChunkRounds)
                    {
                        sum += acc32.GetElement(0) + (long)acc32.GetElement(1)
                             + acc32.GetElement(2) + (long)acc32.GetElement(3);
                        acc32 = Vector128<int>.Zero;
                        round = 0;
                    }
                }
                sum += acc32.GetElement(0) + (long)acc32.GetElement(1)
                     + acc32.GetElement(2) + (long)acc32.GetElement(3);
            }
            for (; i < a.Length; i++)
            {
                int d = a[i] - b[i];
                sum += (long)d * d;
            }
            return sum;
        }

        /// <summary>标量回退 (无 SIMD 环境)</summary>
        private static long SquaredDiffSumScalar(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            long sum = 0;
            for (int i = 0; i < a.Length; i++)
            {
                int d = a[i] - b[i];
                sum += (long)d * d;
            }
            return sum;
        }
    }
}
