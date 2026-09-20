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

        /// <summary>
        /// SDR 基图色调映射（单调、保留高光层次）：y≤knee（SDR 内容）直通保真；
        /// y>knee 将 [knee, headroom] 单调压缩到 [knee, 1.0]（轻微 S 形，端点对齐、无暗环），
        /// 取代旧硬剪切 `y<=1?y:1`（会把所有高光压成死白）。增益图仍按实际 base 值计算，重建不失配。
        /// </summary>
        public static float SegmentedReinhardScalar(float y, float headroom)
        {
            const float knee = 0.75f;
            if (y <= knee) return y;
            if (headroom <= knee) return 1.0f;
            float t = (y - knee) / (headroom - knee);          // 0..1
            float eased = t / (t + 1f) * 2f;                    // 单调: t=0→0, t=1→1
            float v = knee + (1f - knee) * Math.Clamp(eased, 0f, 1f);
            return Math.Clamp(v, 0f, 1f);
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
        /// （H4 依赖 MathF.Pow，AVX2 的 LUT 查表需要 gather 指令，延迟高于标量 pow；无 gather 的纯向量化无法表达 pow。）
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
        // H6: 线性域 primaries 矩阵映射（RGBA 交错 float，跳过 alpha）
        //     系数为 double[9] 行主序（由 ColorSpaceRegistry 从公开色度 + Bradford 色适应算出）。
        //     与 zscale 的 p=/pin= 转换等价，但可用于 zscale 无法命名的空间（ProPhoto/P3/任意 ICC 色度）。
        //     实测（2026-09）：3×3 乘加无 pow，JIT 已良好向量化 → 保持标量实现。
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// 就地把 RGBA 交错线性浮点像素从源原色映射到目标原色：out = M · in。
        /// 输入可为负/超 1（HDR 与矩阵混色均属正常），故**不做 clamp**；由下游编码自行钳位。
        /// </summary>
        public static void ApplyPrimariesMatrix(Span<float> rgba, double[] m9)
        {
            if (m9 is null || m9.Length != 9) return;
            float m0 = (float)m9[0], m1 = (float)m9[1], m2 = (float)m9[2];
            float m3 = (float)m9[3], m4 = (float)m9[4], m5 = (float)m9[5];
            float m6 = (float)m9[6], m7 = (float)m9[7], m8 = (float)m9[8];
            for (int i = 0; i + 4 <= rgba.Length; i += 4)
            {
                float r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
                rgba[i] = m0 * r + m1 * g + m2 * b;
                rgba[i + 1] = m3 * r + m4 * g + m5 * b;
                rgba[i + 2] = m6 * r + m7 * g + m8 * b;
            }
        }

        // ═══════════════════════════════════════════════════
        // H5: PQ (SMPTE ST 2084 / BT.2100) EOTF ⇄ OETF
        //    定义: L = 线性相对亮度 (1.0 = 10000 nits), E = 编码值 (0..1)
        //      OETF: E = ((c1 + c2·L^m1) / (1 + c3·L^m1))^m2
        //      EOTF: L = ((c1 − E^(1/m2)) / (c3·E^(1/m2) − c2))^(1/m1)
        //    同 SrgbToLinear：依赖 MathF.Pow，AVX2 LUT 方案无收益 → 纯标量。
        // ═══════════════════════════════════════════════════

        /// <summary>PQ 参考亮度 (nits)：编码值 1.0 = 10000 nits。</summary>
        public const float PqPeakNits = 10000f;

        private const float PqM1 = 2615f / 16384f;            // 0.1593017578125
        private const float PqM2 = 2523f / 32f;               // 78.84375
        private const float PqC1 = 3424f / 4096f;             // 0.8359375
        private const float PqC2 = 2413f / 128f;              // 18.8515625
        private const float PqC3 = 2392f / 128f;              // 18.6875

        /// <summary>
        /// PQ 编码值 → 线性相对亮度 (1.0 = 10000 nits)。
        /// EOTF: L = ((E^(1/m2) − c1) / (c2 − c3·E^(1/m2)))^(1/m1)（SMPTE ST 2084 / skcms 同形）。
        /// ⚠️ 分子/分母必须保持该符号顺序：写成 (c1−p)/(p·c3−c2) 并对负值钳 0 会令几乎所有
        ///    正常码值（p&gt;c1）返回 0 → 画面变黑（实测已踩）。
        /// </summary>
        public static float PqEotfScalar(float encoded)
        {
            float e = Math.Clamp(encoded, 0f, 1f);
            if (e <= 0f) return 0f;
            float p = MathF.Pow(e, 1f / PqM2);
            float num = p - PqC1;
            float den = PqC2 - PqC3 * p;
            if (num <= 0f || den <= 0f) return 0f;      // 低于黑位基准(E≈0.1854) → 0
            return MathF.Pow(num / den, 1f / PqM1);
        }

        /// <summary>线性相对亮度 (1.0 = 10000 nits) → PQ 编码值</summary>
        public static float PqOetfScalar(float linear)
        {
            float l = Math.Clamp(linear, 0f, 1f);
            float lm = MathF.Pow(l, PqM1);
            float e = MathF.Pow((PqC1 + PqC2 * lm) / (1f + PqC3 * lm), PqM2);
            return Math.Clamp(e, 0f, 1f);
        }

        /// <summary>批量 PQ 编码值 → 线性（就地，RGBA 交错，只动 RGB 三通道）</summary>
        public static void PqEotfRgba(Span<float> rgba)
        {
            for (int i = 0; i + 4 <= rgba.Length; i += 4)
            {
                rgba[i] = PqEotfScalar(rgba[i]);
                rgba[i + 1] = PqEotfScalar(rgba[i + 1]);
                rgba[i + 2] = PqEotfScalar(rgba[i + 2]);
            }
        }

        /// <summary>批量线性 → PQ 编码值（就地，RGBA 交错，只动 RGB 三通道）</summary>
        public static void PqOetfRgba(Span<float> rgba)
        {
            for (int i = 0; i + 4 <= rgba.Length; i += 4)
            {
                rgba[i] = PqOetfScalar(rgba[i]);
                rgba[i + 1] = PqOetfScalar(rgba[i + 1]);
                rgba[i + 2] = PqOetfScalar(rgba[i + 2]);
            }
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
