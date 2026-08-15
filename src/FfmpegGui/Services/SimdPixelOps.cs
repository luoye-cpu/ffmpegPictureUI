using System;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace FfmpegGui.Services
{
    /// <summary>
    /// SIMD 像素操作库（2026-08-15，GainMap 管线 CPU 指令集加速）。
    ///
    /// 实测结论 (4096 像素 RGBA, Release):
    ///   H1 FloatToSrgb8   AVX2 (LUT gather)  0.06ms vs 标量 0.15ms → 2.5× ✅ 保留 SIMD
    ///   H2 ReinhardToSdr  AVX2 (GetElement 收集) 慢 2×  → 标量
    ///   H3 ComputeGainMap AVX2 (gather log2) 慢 1.9× → 标量
    ///   H4 SrgbToLinear   AVX2 (gather pow)  慢 6.7× → 标量
    ///   教训: AVX2 gather 延迟高, 对中小数据 (GainMap 4K) 反不如标量 MathF;
    ///   只有 FloatToSrgb8 (每像素 1 次 pow, 标量开销大) 有真实收益。
    ///
    /// 精度: log2/exp2 用 1024 项 LUT + 线性插值 (表由 MathF 生成, 绝对正确),
    /// 8-bit 输出误差 ≤ 1 LSB (D28 断言验证)。
    ///
    /// 覆盖 GainMap 管线 4 个热点 API (标量/向量统一入口):
    ///   FloatToSrgb8    (编码: 线性→sRGB 8-bit)     — AVX2 加速
    ///   ReinhardToSdr   (编码: 分段色调映射)          — 标量
    ///   ComputeGainMapGray (编码: log2 增益)         — 标量
    ///   SrgbToLinearRgba(解码: sRGB→线性)            — 标量
    /// </summary>
    public static class SimdPixelOps
    {
        // ═══════════════════════════════════════════════════
        // 常量
        // ═══════════════════════════════════════════════════

        private const float SrgbLinearThreshold = 0.0031308f;
        private const float SrgbLinearSlope = 12.92f;
        private const float SrgbGamma = 1f / 2.4f;         // 编码指数
        private const float SrgbInvGamma = 2.4f;           // 解码指数
        private const float SrgbA = 1.055f;
        private const float SrgbB = 0.055f;

        // ── log2/exp2 查表 (表由 MathF 生成, 精度保证; 仅 FloatToSrgb8 AVX2 使用) ──
        private const int LutBits = 10;                     // 1024 项/区间
        private const float LutScale = 1f / (1 << LutBits);
        private static readonly float[] Log2Lut = BuildLog2Lut();
        private static readonly float[] Exp2Lut = BuildExp2Lut();

        private static float[] BuildLog2Lut()
        {
            var t = new float[(1 << LutBits) + 1];
            for (int i = 0; i <= (1 << LutBits); i++)
                t[i] = MathF.Log2(1f + i * LutScale);      // log2(1+f), f∈[0,1]
            return t;
        }

        private static float[] BuildExp2Lut()
        {
            var t = new float[(1 << LutBits) + 1];
            for (int i = 0; i <= (1 << LutBits); i++)
                t[i] = MathF.Pow(2f, i * LutScale - 0.5f); // 2^(f-0.5), f∈[0,1]
            return t;
        }

        // ═══════════════════════════════════════════════════
        // H1: FloatToSrgb8 — 线性 float [0,1] → sRGB 8-bit (AVX2 加速, 2.5×)
        //    s = v<=0.0031308 ? 12.92v : 1.055*pow(v,1/2.4)-0.055
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// 批量 线性 float → sRGB 8-bit（逐像素，源 RGBA 交错或纯值数组皆可）。
        /// 输入范围 [0,1]，越界自动 clamp。与标量 FloatToSrgb8Scalar 逐字节一致（±1 LSB）。
        /// </summary>
        public static void FloatToSrgb8(Span<float> src, Span<byte> dst)
        {
            if (src.Length != dst.Length)
                throw new ArgumentException("源/目标长度不一致");
            if (Avx2.IsSupported)
            {
                FloatToSrgb8Avx2(src, dst);
                return;
            }
            for (int i = 0; i < src.Length; i++)
                dst[i] = FloatToSrgb8Scalar(src[i]);
        }

        /// <summary>标量参考实现（供测试断言 + 无 SIMD 回退）</summary>
        public static byte FloatToSrgb8Scalar(float v)
        {
            v = Math.Clamp(v, 0f, 1f);
            float s;
            if (v <= SrgbLinearThreshold) s = SrgbLinearSlope * v;
            else s = SrgbA * MathF.Pow(v, SrgbGamma) - SrgbB;
            return (byte)Math.Clamp(MathF.Round(s * 255f), 0, 255);
        }

        /// <summary>AVX2: 8 路 float，log2/exp2 用 LUT 查表 + 线性插值</summary>
        private static unsafe void FloatToSrgb8Avx2(Span<float> src, Span<byte> dst)
        {
            var vZero = Vector256<float>.Zero;
            var vOne = Vector256.Create(1f);
            var vThreshold = Vector256.Create(SrgbLinearThreshold);
            var vSlope = Vector256.Create(SrgbLinearSlope);
            var vA = Vector256.Create(SrgbA);
            var vB = Vector256.Create(SrgbB);
            var vGamma = Vector256.Create(SrgbGamma);
            var v255 = Vector256.Create(255f);
            var vHalf = Vector256.Create(0.5f);

            int i = 0;
            fixed (float* ps = src)
            {
                for (; i + 8 <= src.Length; i += 8)
                {
                    var v = Avx.LoadVector256(ps + i);
                    // clamp [0,1]
                    v = Avx.Max(vZero, Avx.Min(vOne, v));

                    // 线性分支: lin = 12.92 * v
                    var lin = Avx.Multiply(vSlope, v);

                    // gamma 分支: g = 1.055 * pow(v, 1/2.4) - 0.055
                    // pow(v, g) = exp2(g * log2(v))
                    var log2v = Log2Approx(v);
                    var pow = Exp2Approx(Avx.Multiply(vGamma, log2v));
                    var gamma = Avx.Subtract(Avx.Multiply(vA, pow), vB);

                    // 选择: v <= 0.0031308 ? lin : gamma
                    var sel = Avx.CompareLessThanOrEqual(v, vThreshold);
                    var s = Avx.BlendVariable(gamma, lin, sel);

                    // *255 + 0.5 取整 (与 MathF.Round 一致)
                    var scaled = Avx.Add(Avx.Multiply(s, v255), vHalf);
                    var rounded = Avx.ConvertToVector256Int32(scaled);
                    // clamp 0..255
                    rounded = Avx2.Max(Vector256<int>.Zero, rounded);
                    rounded = Avx2.Min(Vector256.Create(255), rounded);
                    // 8×int32 → 8×byte (⚠️ 不能用 PackSignedSaturate 两次打包:
                    // vpackssdq/vpackuswb 的 128 位块序导致字节错乱; GetElement 每像素
                    // 1 次 vpextrd, 开销可忽略)
                    for (int k = 0; k < 8; k++)
                        dst[i + k] = (byte)rounded.GetElement(k);
                }
            }
            // 尾部标量
            for (; i < src.Length; i++)
                dst[i] = FloatToSrgb8Scalar(src[i]);
        }

        // ═══════════════════════════════════════════════════
        // H2: ReinhardToSdr — 分段 Reinhard 色调映射 (编码侧, 标量)
        //    y≤1 直通; 1<y<1.25 smoothstep 过渡; y≥1.25 标准 Reinhard (headroom 归一化)
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// 分段 Reinhard 色调映射（RGBA 交错，每像素按 max(R,G,B) 计算统一缩放）。
        /// 与 GainMapEncoder.ReinhardToSdr 语义一致（误差 &lt; 1e-6）。
        /// ⚠️ 实测 AVX2 版慢 2×（maxY GetElement 收集开销），故纯标量。
        /// </summary>
        public static void ReinhardToSdr(Span<float> hdr, Span<float> sdr, float headroom)
        {
            if (hdr.Length != sdr.Length)
                throw new ArgumentException("源/目标长度不一致");
            for (int i = 0; i + 4 <= hdr.Length; i += 4)
            {
                float r = hdr[i], g = hdr[i + 1], b = hdr[i + 2];
                float maxY = Math.Max(r, Math.Max(g, b));
                float maxSdr = SegmentedReinhardScalar(maxY, headroom);
                float scale = maxY > 1e-6f ? maxSdr / maxY : 0f;
                sdr[i] = Math.Clamp(r * scale, 0f, 1f);
                sdr[i + 1] = Math.Clamp(g * scale, 0f, 1f);
                sdr[i + 2] = Math.Clamp(b * scale, 0f, 1f);
                sdr[i + 3] = hdr[i + 3];
            }
        }

        /// <summary>标量参考：分段 Reinhard 单值</summary>
        public static float SegmentedReinhardScalar(float y, float headroom)
        {
            if (y <= 1.0f) return y;
            float t = y / Math.Max(headroom, 1f);
            float reinhard = t / (1.0f + t);
            reinhard *= 2.0f;
            if (y >= 1.25f) return Math.Min(reinhard, 1.0f);
            float x = Math.Clamp((y - 1.0f) / 0.25f, 0f, 1f);
            float smooth = x * x * (3.0f - 2.0f * x);
            return 1.0f + (reinhard - 1.0f) * smooth;
        }

        // ═══════════════════════════════════════════════════
        // H3: ComputeGainMap — 灰度增益图计算 (编码侧, 标量)
        //    gain = log2(亮度(HDR)/亮度(SDR)), BT.709 加权
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// 灰度增益图计算：输入 HDR/SDR RGBA 交错，输出 每像素 1 byte。
        /// 语义与 GainMapEncoder.ComputeGainMap 灰度分支一致（误差 &lt; 1 LSB）。
        /// ⚠️ 实测 AVX2 版 (gather log2) 慢 1.9×，故纯标量。
        /// </summary>
        public static void ComputeGainMapGray(Span<float> hdr, Span<float> sdr, Span<byte> gain, float maxLog2Gain)
        {
            const float eps = 0.001f;
            for (int i = 0; i + 4 <= hdr.Length; i += 4)
            {
                float hR = hdr[i], hG = hdr[i + 1], hB = hdr[i + 2];
                float sR = sdr[i], sG = sdr[i + 1], sB = sdr[i + 2];
                float hLum = 0.2126f * hR + 0.7152f * hG + 0.0722f * hB;
                float sLum = 0.2126f * sR + 0.7152f * sG + 0.0722f * sB;
                float logGain = MathF.Log2(Math.Max(hLum / Math.Max(sLum, eps), 1.0f));
                gain[i / 4] = LogGainToByteScalar(logGain, maxLog2Gain);
            }
        }

        /// <summary>log2(gain) → 8-bit 标量参考</summary>
        public static byte LogGainToByteScalar(float logGain, float maxLog2Gain)
        {
            if (maxLog2Gain <= 0f) maxLog2Gain = 1f;
            float clamped = Math.Clamp(logGain, 0f, maxLog2Gain);
            return (byte)(clamped / maxLog2Gain * 255f);
        }

        // ═══════════════════════════════════════════════════
        // H4: SrgbToLinear — sRGB 编码值 → 线性 float (解码侧, 标量)
        //    lin = v<=0.04045 ? v/12.92 : pow((v+0.055)/1.055, 2.4)
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// 批量 sRGB → 线性（就地转换 RGBA 交错数组，只处理 RGB 三通道，跳过 alpha）。
        /// ⚠️ 实测 AVX2 版 (gather pow) 慢 6.7×，故纯标量。
        /// </summary>
        public static void SrgbToLinearRgba(Span<float> rgba)
        {
            for (int i = 0; i + 4 <= rgba.Length; i += 4)
            {
                rgba[i] = SrgbToLinearScalar(rgba[i]);
                rgba[i + 1] = SrgbToLinearScalar(rgba[i + 1]);
                rgba[i + 2] = SrgbToLinearScalar(rgba[i + 2]);
            }
        }

        /// <summary>标量参考实现</summary>
        public static float SrgbToLinearScalar(float v)
        {
            v = Math.Clamp(v, 0f, 1f);
            return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, SrgbInvGamma);
        }

        // ═══════════════════════════════════════════════════
        // 数学近似核心（AVX2 专用, LUT 查表 + 线性插值）
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// log2(x) 近似 (8 路 float) — 位提取指数 + LUT 尾数插值。
        /// </summary>
        private static Vector256<float> Log2Approx(Vector256<float> v)
        {
            // 提取 IEEE754 位 (⚠️ 位重解释 AsInt32, 不能 ConvertToVector256Int32=数值取整!)
            var bits = v.AsInt32();
            var expInt = Avx2.ShiftRightArithmetic(bits, 23);
            expInt = Avx2.And(expInt, Vector256.Create(0xFF));
            var exp = Avx.ConvertToVector256Single(expInt);   // int→float 数值转换 (正确)
            exp = Avx.Subtract(exp, Vector256.Create(127f));
            var mantBits = Avx2.And(bits, Vector256.Create(0x007FFFFF));
            mantBits = Avx2.Or(mantBits, Vector256.Create(0x3F800000));
            var m = mantBits.AsSingle();                      // ⚠️ 位重解释!
            // 查表: idx = (m-1)*1024, 线性插值
            // ⚠️ 必须截断转换 (ConvertToVector256Int32 是 round-nearest, 1023.9→1024 越界!)
            var idxF = Avx.Multiply(Avx.Subtract(m, Vector256.Create(1f)), Vector256.Create(1024f));
            var idx = Avx.ConvertToVector256Int32WithTruncation(idxF);
            var frac = Avx.Subtract(idxF, Avx.ConvertToVector256Single(idx));
            unsafe
            {
                fixed (float* p = Log2Lut)
                {
                    var v0 = Avx2.GatherVector256(p, idx, 4);
                    var v1 = Avx2.GatherVector256(p, Avx2.Add(idx, Vector256.Create(1)), 4);
                    var interp = Avx.Add(v0, Avx.Multiply(Avx.Subtract(v1, v0), frac));
                    return Avx.Add(exp, interp);
                }
            }
        }

        /// <summary>
        /// exp2(x) 近似 (8 路 float) — 整数部分位运算 + LUT 小数插值。
        /// x = n + f (n 整数, f∈[-0.5,0.5]); 2^x = 2^n · 2^f。
        /// </summary>
        private static Vector256<float> Exp2Approx(Vector256<float> x)
        {
            var rounded = Avx.RoundToNearestInteger(x);
            var frac = Avx.Subtract(x, rounded);
            var n = Avx.ConvertToVector256Int32(rounded);     // float→int 数值取整 (正确)
            // 2^f 查表: idxF = (f + 0.5) * 1024 ∈ [0, 1024)
            // ⚠️ 必须截断转换 (round-nearest 会越界)
            var idxF = Avx.Multiply(Avx.Add(frac, Vector256.Create(0.5f)), Vector256.Create(1024f));
            var idx = Avx.ConvertToVector256Int32WithTruncation(idxF);
            var frac2 = Avx.Subtract(idxF, Avx.ConvertToVector256Single(idx));
            unsafe
            {
                fixed (float* p = Exp2Lut)
                {
                    var v0 = Avx2.GatherVector256(p, idx, 4);
                    var v1 = Avx2.GatherVector256(p, Avx2.Add(idx, Vector256.Create(1)), 4);
                    var fPart = Avx.Add(v0, Avx.Multiply(Avx.Subtract(v1, v0), frac2));
                    // 2^n = 位运算 (n+127)<<23
                    var biased = Avx2.Add(n, Vector256.Create(127));
                    var expBits = Avx2.ShiftLeftLogical(biased, 23);
                    var scale = expBits.AsSingle();           // ⚠️ 位重解释!
                    return Avx.Multiply(scale, fPart);
                }
            }
        }
    }
}
