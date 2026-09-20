using System;

namespace FfmpegGui.Services.ColorMapping;

/// <summary>
/// ITU-R Report BT.2446-1 §4「Conversion Method A」（HDR↔SDR）。
///
/// 常数逐字取自建议书正文 Table 2 / Table 3 / Table 4（本地存档：tools/std/bt2446-1.txt）。
/// ⚠ 无需 ICtCp、无需 3D LUT —— 它是**亮度域 1D 曲线 + 色差校正**，H1 表驱动内核可直接承载。
///
/// §4.1 输入域：归一化 full-range **线性显示光**（BT.2100 Table 2 色度），1.0 = L_HDR（默认 1000 cd/m²）。
/// 其 Y' 是线性光的 **1/12.4 次幂**（不是 SDR gamma！），故 Y'∈[0,1] 被强烈压向 1。
///
/// ⚠ 重要事实（实测推导所得，别误解为可往返恒等）：
///   §4.1 的 Y'（1/12.4 域）与 §4.2 的 Y'（BT.2020 SDR 非线性域）**不是同一编码**，
///   报告只称两者"互补、质量损失最小"并以 Annex 1 指标评估 ⇒ **不能拿 HDR→SDR→HDR 恒等当门禁**。
///   可用的硬锚点见 <see cref="InverseLumaNits"/>（255 段端点精确落到 Lmax=1000 nits）。
/// </summary>
public static class Bt2446MethodA
{
    /// <summary>建议书默认：HDR 制作峰值 1000 cd/m²、SDR 目标峰值 100 cd/m²。</summary>
    public const double LHdrDefault = 1000.0;
    public const double LSdrDefault = 100.0;
    /// <summary>§4.2 的最大显示亮度（Table 4）。</summary>
    public const double LmaxNits = 1000.0;

    /// <summary>Table 2：ρ = 1 + 32·(L/10000)^(1/12.4)。</summary>
    public static double Rho(double lNits) => 1.0 + 32.0 * Math.Pow(lNits / 10000.0, 1.0 / 12.4);

    /// <summary>Table 2：非线性化 R' = R^(1/12.4)（输入线性显示光，1.0 = L_HDR）。</summary>
    public static double Gamma124(double linear) => linear <= 0 ? 0 : Math.Pow(linear, 1.0 / 12.4);

    /// <summary>Table 2：亮度 Y' = 0.2627R' + 0.6780G' + 0.0593B'（BT.2100 系数，和为 1）。</summary>
    public static double LumaPrime(double rp, double gp, double bp) => 0.2627 * rp + 0.6780 * gp + 0.0593 * bp;

    /// <summary>Table 2 Step 1：进感知线性域 Yp' = log(1+(ρ_HDR−1)Y')/log(ρ_HDR)。</summary>
    public static double Step1Perceptual(double yp, double lHdr)
        => Math.Log(1.0 + (Rho(lHdr) - 1.0) * yp) / Math.Log(Rho(lHdr));

    /// <summary>Table 2 Step 2：感知域三段 knee（线性 → 二次 → 线性）。</summary>
    public static double Step2Knee(double ypp)
        => ypp <= 0.7399 ? 1.0770 * ypp
         : ypp < 0.9909 ? -1.1510 * ypp * ypp + 2.7811 * ypp - 0.6302
         : 0.5 * ypp + 0.5;

    /// <summary>Table 2 Step 3：回 gamma 域 Y_SDR' = (ρ_SDR^Yc' − 1)/(ρ_SDR − 1)。</summary>
    public static double Step3Gamma(double yc, double lSdr)
        => (Math.Pow(Rho(lSdr), yc) - 1.0) / (Rho(lSdr) - 1.0);

    /// <summary>§4.1 亮度映射合成：Y'（1/12.4 域）→ Y_SDR'（SDR 非线性域）。</summary>
    public static double ToneMapLuma(double yPrime, double lHdr = LHdrDefault, double lSdr = LSdrDefault)
        => Step3Gamma(Step2Knee(Step1Perceptual(yPrime, lHdr)), lSdr);

    /// <summary>
    /// Table 3 色差校正：f = (Y_SDR'/Y')^1.1，Cb' = f(B'−Y')/1.8814，Cr' = f(R'−Y')/1.4746，
    /// 且 Y_TMO' = Y_SDR' − max(0.1·Cr', 0)。
    /// </summary>
    public static void ColourCorrect(double rp, double gp, double bp, double ySdrPrime,
                                    out double cb, out double cr, out double yTmo)
    {
        double yp = LumaPrime(rp, gp, bp);
        double f = yp <= 0 ? 1.0 : Math.Pow(ySdrPrime / yp, 1.1);
        cb = f * (bp - yp) / 1.8814;
        cr = f * (rp - yp) / 1.4746;
        yTmo = ySdrPrime - Math.Max(0.1 * cr, 0.0);
    }

    /// <summary>Table 4：指数 E（分段，T=70，Y''=255·Y'）。</summary>
    public static double Exponent(double yDouble)
        => yDouble <= 70.0
            ? 1.8712e-5 * yDouble * yDouble - 2.7334e-3 * yDouble + 1.3141
            : 2.8305e-6 * yDouble * yDouble - 7.4622e-4 * yDouble + 1.2528;

    /// <summary>
    /// §4.2 SDR→HDR 亮度映射，输出**绝对 nits**（0..Lmax）。
    /// 硬锚点：Y'=1 ⇒ Y''=255 ⇒ E=1.2466 ⇒ 255^E = 1000.0 nits = Lmax（建议书常数自洽，可校验读法）。
    /// </summary>
    public static double InverseLumaNits(double ySdrPrime)
    {
        double yy = 255.0 * ySdrPrime;
        return yy <= 0 ? 0.0 : Math.Pow(yy, Exponent(yy));
    }
}
