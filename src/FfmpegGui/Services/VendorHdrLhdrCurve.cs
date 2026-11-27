using System;
using System.Buffers.Binary;

namespace FfmpegGui.Services
{
    /// <summary>
    /// OPPO/OnePlus/realme ProXDR LHDR（local.hdr.linear.mask + 144 B local.hdr.meta.data）的
    /// 参数解释与 mask → ISO 21496 增益图重建。
    ///
    /// 2026-11-26 更正：旧实现把 byte 32/36/44 当作 min/gamma/max（/256），但独立实现
    /// XDRemux（commit 8893b308e9557339110b4ec7cb18ea227c740071）对同一 36-float 块使用
    /// float[0] = EDR version、float[32] = raw gain、float[33] = precomputed EDR scale。
    /// 本文件逐项移植其 edr.py / gainmap.py；这些是逆向经验模型，不宣称是厂商公开规范。
    /// 当前 OnePlus 15T 真机：f[0]=5.0686、f[32]=10.5181、f[33]=4.0224218，
    /// 因而走 precomputed 直达路径，标准元数据为 min=0 / max=log2(4.0224218)=2.0080644 / gamma=1。
    /// </summary>
    internal static class VendorHdrLhdrCurve
    {
        private static float F(double v) => (float)v;
        private static float F32(VendorHdrTrailerResult p, int index)
        {
            if (p.ParamBlock == null || p.ParamBlock.Length < VendorHdrTrailer.ParamBlockBytes) return float.NaN;
            int bits = BinaryPrimitives.ReadInt32LittleEndian(p.ParamBlock.AsSpan(index * 4, 4));
            return BitConverter.Int32BitsToSingle(bits);
        }

        private static bool ValidScale(float scale)
            => float.IsFinite(scale) && scale >= 1.0f && scale <= 64.0f;

        internal static float EdrVersion(VendorHdrTrailerResult p) => F32(p, 0);

        /// <summary>
        /// 是否落在**没有真机样本**的曲线分支上：只有 version≥3 且 f32[33]≥1 的直达路径
        /// 有一加 15T 原片证据；EarlyScale / sigmoid / main 三支只有上游实现的公式对拍。
        /// 调用方据此点名，避免把“公式算出来了”冒充“这台设备的像素已验证”。
        /// </summary>
        internal static bool IsEmpiricallyUnverified(VendorHdrTrailerResult p)
        {
            float version = F32(p, 0);
            float precomputed = F32(p, 33);
            return !(version >= 3.0f && precomputed >= 1.0f);
        }

        internal static string Describe(VendorHdrTrailerResult p)
            => $"LHDR version={EdrVersion(p):F6}, rawGain=f32[32]={F32(p, 32):F6}, " +
               $"precomputed=f32[33]={F32(p, 33):F6}, EDR scale={EdrScale(p):F6}";

        /// <summary>与 XDRemux edr.edr_scale_calculator 同构。</summary>
        internal static float EdrScale(VendorHdrTrailerResult p)
        {
            float version = F32(p, 0);
            if (version < 3.0f) return EarlyScale(p);
            if (version < 2.0f) return 1.0f; // 与上游一致：保留其防御分支，尽管前一行已覆盖。
            float precomputed = F32(p, 33);
            if (precomputed >= 1.0f) return precomputed;
            float rawGain = F32(p, 32);
            if (rawGain <= 0.0f) return 1.0f;

            float f23 = F32(p, 23);
            float face = F32(p, 24);
            float highlight = MathF.Max(F32(p, 29), 1.0f);
            bool cfg = (int)F32(p, 34) == 1;
            double edr;

            if (f23 <= 0.99f || version < 3.0f)
            {
                double expArg = rawGain * -0.1175 - 6.829;
                edr = 780.3 / (Math.Pow(2.0, expArg) + 1.0) - 772.3;
                if (face > 0.0f)
                {
                    double factor = face < 1.0f ? face : 1.0 / face;
                    edr = (edr - 1.0) * factor + 1.0;
                }
                double s4 = Math.Abs(Math.Sqrt(Math.Abs(edr))) - 1.0;
                if (highlight >= 200.0f)
                {
                    if (highlight >= 320.0f) edr = s4 * 1.34 + 1.0;
                    else edr = s4 * (highlight * -0.0205 + 7.9) + 1.0;
                }
                else edr = s4 * 3.8 + 1.0;

                if (cfg) edr = (Math.Abs(Math.Sqrt(Math.Abs(edr))) - 1.0) * 1.3 + 1.0;
                else if (face > 0.0f)
                {
                    double adj = (Math.Abs(Math.Sqrt(Math.Abs(edr))) - 1.0) * 1.85 + 1.0;
                    edr = highlight <= 320.0f ? adj : (adj - 1.0) * 0.8 + 1.0;
                }
                else if (highlight > 320.0f) edr = (edr - 1.0) * 0.8 + 1.0;
                return F(Math.Clamp(edr, 1.0, 7.9));
            }

            double normGain = rawGain * 1023.0 / 65535.0;
            double scaled = Math.Log2(normGain * 63.0 + 1.0) / highlight * 100.0;
            if (highlight <= 210.0f) edr = scaled * 0.3456 + 1.824;
            else if (highlight > 340.0f) edr = scaled * 0.1046 + 1.878;
            else edr = scaled * 0.5883 + 1.401;
            return F(Math.Clamp(edr, 1.0, 7.9));
        }

        private static float FmaF(float a, float b, float c) => F((double)a * b + c);

        /// <summary>与 XDRemux edr._float32_early_lhdr_scale 同构；每一步显式收窄到 float。</summary>
        private static float EarlyScale(VendorHdrTrailerResult p)
        {
            float version = F32(p, 0);
            if (version < 2.0f) return 1.0f;
            float precomputed = F32(p, 33);
            if (precomputed >= 1.0f) return precomputed;
            float rawGain = F32(p, 32);
            if (rawGain <= 0.0f) return 1.0f;

            float face = F32(p, 24);
            float highlight = F32(p, 29);
            float edr = F(Math.Pow(2.0, FmaF(rawGain, -0.11749999970197678f, -6.828999996185303f)));
            edr = F(780.2999877929688 / F(edr + 1.0f));
            edr = F(edr + -772.2999877929688);
            float faceAdjusted = edr;
            if (face > 0.0f)
            {
                float factor = face < 1.0f ? face : F(1.0f / face);
                faceAdjusted = FmaF(F(edr - 1.0f), factor, 1.0f);
            }
            float sqrtTerm = F(Math.Abs(F(Math.Sqrt(faceAdjusted))) - 1.0f);
            float highlightAdjusted;
            if (highlight >= 200.0f)
            {
                float high = FmaF(sqrtTerm, 1.340000033378601f, 1.0f);
                float midFactor = FmaF(highlight, -0.020500000566244125f, 7.900000095367432f);
                float mid = FmaF(sqrtTerm, midFactor, 1.0f);
                highlightAdjusted = highlight >= 320.0f ? high : mid;
            }
            else highlightAdjusted = FmaF(sqrtTerm, 3.799999952316284f, 1.0f);

            if (F32(p, 34) == float.Epsilon)
            {
                float cfgTerm = F(Math.Abs(F(Math.Sqrt(highlightAdjusted))) - 1.0f);
                return FmaF(cfgTerm, 1.2999999523162842f, 1.0f);
            }
            if (face > 0.0f)
            {
                float faceTerm = F(Math.Abs(F(Math.Sqrt(highlightAdjusted))) - 1.0f);
                float adjusted = FmaF(faceTerm, 1.850000023841858f, 1.0f);
                if (highlight <= 320.0f) return adjusted;
                return FmaF(F(adjusted - 1.0f), 0.800000011920929f, 1.0f);
            }
            if (highlight <= 320.0f) return highlightAdjusted;
            return FmaF(F(highlightAdjusted - 1.0f), 0.800000011920929f, 1.0f);
        }

        internal static GainMapDecoder.GainMapMetadata? CreateMetadata(VendorHdrTrailerResult p)
        {
            float scale = EdrScale(p);
            if (!ValidScale(scale)) return null;
            return new GainMapDecoder.GainMapMetadata
            {
                GainMapMin = 0.0f,
                GainMapMax = MathF.Log2(scale),
                Gamma = 1.0f,
                OffsetSdr = 0.0f,
                OffsetHdr = 0.0f,
                HdrCapacityMin = 0.0f,
                HdrCapacityMax = MathF.Log2(scale),
                BaseRenditionIsHdr = false,
                Channels = 1
            };
        }

        private static float KneePoint(float scale)
        {
            float invGamma = F(0.45454543828964233);
            float t = F(1.0f / F(scale * 100.0f));
            float k = F(1.0f - t);
            float p1 = F(Math.Pow(scale, invGamma));
            float div1 = F(1.0f / p1);
            float xNorm = F(F(0.9800000190734863f - t) / k);
            float p2 = F(Math.Pow(xNorm, invGamma));
            float y = F(F(F(p2 * 1.003937005996704f) - div1) / F(1.0f - div1));
            if (!float.IsFinite(y) || y <= 0.0f) return float.NaN;
            float p3 = F(Math.Pow(y, invGamma));
            if (!float.IsFinite(p3) || p3 == 1.0f) return float.NaN;
            float kneeRaw = F(F(p3 * 255.0f) + -254.0f);
            float kneeAdj = F(kneeRaw / F(p3 - 1.0f));
            float result = MathF.Round(kneeAdj); // Python round / numpy 均为 ties-to-even。
            if (result <= 0.0f) result = kneeRaw;
            return F(result / 255.0f);
        }

        private static float[] Lut(int count, Func<double, double> fn)
        {
            var lut = new float[count];
            for (int i = 0; i < count; i++) lut[i] = F(fn(i / 1000.0));
            return lut;
        }

        private static int ClampIndex(int value, int max) => value < 0 ? 0 : value > max ? max : value;

        /// <summary>
        /// 与 XDRemux gainmap.reconstruct 同构：保留其 LUT 取样和 8-bit round，便于逐字节对拍。
        /// 返回值是与输入 mask 等长、可直接走现有 ISO gamma=1 公式的灰度增益图。
        /// </summary>
        internal static byte[]? ReconstructGainMap8(VendorHdrTrailerResult p, byte[] mask)
        {
            if (mask == null || mask.Length == 0) return null;
            float scale = EdrScale(p);
            if (!ValidScale(scale)) return null;

            var lut0 = Lut(1001, x => Math.Pow(x, 0.625));
            var lut1 = Lut(1001, x => Math.Pow(x, 2.2));
            double gammaFactor = Math.Pow(1.0 / scale, 1.0 / 2.2);
            double headroomScale = (1.0 - gammaFactor) / Math.Max(gammaFactor, 0.001);
            double maxBoost = scale > 1.0f ? Math.Max(scale, 2.0) : 2.0;
            double log2Scale = 255.0 / Math.Log2(Math.Max(scale, 1.01));
            var lut2 = Lut(1001, x => Math.Pow(x * headroomScale + 1.0, 2.2));
            var lut3 = Lut(8001, x => x <= 0.0 ? 0.0 : log2Scale * Math.Log2(Math.Min(Math.Max(x, 1.0), maxBoost)));

            float knee = EdrVersion(p) >= 3.0f ? 0.0f : KneePoint(scale);
            if (!float.IsFinite(knee)) return null; // 早期变体若膝盖点退化，宁可 SDR 降级也不制造假 HDR。
            float kneeRange = knee < 1.0f ? 1.0f - knee : 0.001f;
            var output = new byte[mask.Length];
            for (int i = 0; i < mask.Length; i++)
            {
                float maskF = mask[i] / 255.0f;
                float linGray = lut0[ClampIndex((int)(maskF * 1000.0f), 1000)];
                bool belowKnee = linGray < knee;
                float t = belowKnee ? 0.0f : (linGray - knee) / kneeRange;
                float linear = lut1[ClampIndex((int)(t * 1000.0f), 1000)];
                int l2Index = ClampIndex((int)(linear * 1000.0f), 1000);
                float boosted = belowKnee ? 1.0f : lut2[l2Index];
                int l3Index = ClampIndex((int)(MathF.Min(boosted, 8.0f) * 1000.0f), 8000);
                output[i] = (byte)Math.Clamp((int)MathF.Round(lut3[l3Index]), 0, 255);
            }
            return output;
        }
    }
}
