using System;

namespace FfmpegGui.Services.ColorMapping;

/// <summary>
/// 色调映射算子。
/// ⚠ 只列"数学形式有权威出处或项目内已验证"的算子；
///   Hable / BT.2390 的常数**不凭记忆写**（本项目已因"期望常量凭记忆写错"吃过三次亏），
///   必须先由标准文本或 ffmpeg 实测输出标定后再加入，并以 PSNR 门禁验收。
/// </summary>
public enum ToneMapMode
{
    /// <summary>不做色调映射（HDR 保持 HDR，或 SDR 内容）。</summary>
    None = 0,
    /// <summary>分段 Reinhard：y≤knee 直通、y&gt;knee 单调压缩到 [knee,1]，保色相。
    /// 与 GainMap 底图 **同一个实现**（<see cref="SimdPixelOps.SegmentedReinhardScalar"/>），杜绝两套色调映射。</summary>
    ReinhardSegmented = 1,
    /// <summary>Hable filmic 曲线（Uncharted 2，John Hable 提出）。
    /// 常数与归一化按 ffmpeg <c>libavfilter/vf_tonemap.c</c> 源码实现（已核）：
    /// <c>a=.15 b=.50 c=.10 d=.20 e=.02 f=.30</c>，输出 <c>hable(sig)/hable(peak)</c>。
    /// ⚠ 这是“好看”的电影曲线，会明显改变平均亮度（ffmpeg 文档自己标注了这一缺点）。</summary>
    Hable = 2,
    /// <summary>Mobius 变换（Reinhard 的推广，带近黑线性段）。
    /// 公式逐字对齐 ffmpeg <c>vf_tonemap.c::mobius()</c>；拐点参数默认 0.3（同 ffmpeg）。
    /// 特性：低于拐点的值 1:1 不映射（保色精度优于 hable/reinhard）。</summary>
    Mobius = 4,
    /// <summary>ITU-R Report BT.2446-1 §4「Method A」（HDR→SDR）。
    /// 常数逐字取自建议书 Table 2/3/4（单测（bt2446）已用“标准自洽锚点”验证 12/12）。
    /// 与 hable/mobius 的区别：它是**亮度域曲线 + 色差校正**（不是单通道标量），且在**编码域**定义。</summary>
    Bt2446A = 5,
    /// <summary>ITU-R Report BT.2390 EETF（带线性段的 Hermite 样条滚降，拐点偏移 0.5）。
    /// 未实现：本工具链无参考实现可用、且尚未拿到标准公式原文，**绝不凭记忆填常数**；
    /// 选用时按“未就绪”拒绝而非静默降级。</summary>
    Bt2390 = 3,
}

/// <summary>
/// 色调映射参数。值域约定（与 <see cref="TransformSpec.LinearScale"/> 配套）：
///  • 线性域中 **1.0 = SDR 参考白**（默认 203 nits，BT.2100/libultrahdr 同值）；
///  • HDR 内容峰值 = <see cref="PeakNits"/>，因此 headroom = PeakNits / SdrWhiteNits ≥ 1；
///  • 因此源 PQ 编码值（1.0 = 10000 nits）需先经 LinearScale = 10000/203 换算到本约定域。
/// </summary>
public sealed class ToneMapParams
{
    public ToneMapMode Mode = ToneMapMode.None;
    public double SdrWhiteNits = TransferCurve.SdrWhiteNits;
    /// <summary>内容峰值（nits）。未知时按保守取 SDR 白（等效不映射），**不得猜 1000**。</summary>
    public double PeakNits = TransferCurve.SdrWhiteNits;

    /// <summary>Mobius 拐点参数 j（ffmpeg 默认 0.3）：x ≤ j 时 1:1 直通。</summary>
    public double Knee = 0.3;
    /// <summary>Method A 的 SDR 目标峰值 L_SDR（建议书默认 100 cd/m²；与 SdrWhiteNits=参考白 203 是两个不同量）。</summary>
    public double SdrPeakNits = 100.0;

    /// <summary>
    /// 本峰值是“既无用户指定、也无容器静态元数据”时按**源曲线名义值**兜底出来的 ⇒ 执行层可整帧实测内容峰值替换它。
    /// 静像几乎都不带 MaxCLL/BS.2086（实测），拿名义 PQ 10000nit 当内容峰值会把画面压暗到不可接受（headroom=49）；
    /// 但**只有执行层看得到像素**，所以下一步由 <c>RawColorPipeline</c> 消费本标记。
    /// </summary>
    public bool MeasurePeak;

    /// <summary>峰值相对 SDR 白的档数（≥1）。1 = 无压缩。</summary>
    public double Headroom => PeakNits <= SdrWhiteNits ? 1.0 : PeakNits / Math.Max(1.0, SdrWhiteNits);

    /// <summary>是否真的需要压缩（headroom≈1 时映射应完全直通，保证 ColorLoss=None）。</summary>
    public bool Active => Mode != ToneMapMode.None && Headroom > 1.0 + 1e-6;

    public override string ToString() => Active
        ? $"tonemap={Mode} peak={PeakNits:F0}nit{(MeasurePeak ? "(待实测)" : "")} white={SdrWhiteNits:F0}nit headroom={Headroom:F2}"
        : "tonemap=off";

    /// <summary>浅拷贝（字段全是值类型）：执行层改写峰值时不得污染规划层的 Plan.Tone。</summary>
    public ToneMapParams Clone() => new()
    {
        Mode = Mode, SdrWhiteNits = SdrWhiteNits, PeakNits = PeakNits, Knee = Knee,
        SdrPeakNits = SdrPeakNits, MeasurePeak = MeasurePeak,
    };
}

/// <summary>色调映射执行（线性域）。全部为标量实现，热路径与内核共享同一函数。</summary>
public static class ToneMap
{
    // Hable filmic 常数——逐字对齐 ffmpeg vf_tonemap.c::hable()（a,b,c,d,e,f）
    private const float HbleA = 0.15f, HbleB = 0.50f, HbleC = 0.10f, HbleD = 0.20f, HbleE = 0.02f, HbleF = 0.30f;

    /// <summary>Hable 原始响应曲线（未归一化）。ffmpeg: <c>(in*(in*a+b*c)+d*e)/(in*(in*a+b)+d*f) - e/f</c></summary>
    private static double HableRaw(double x)
        => (x * (x * HbleA + HbleB * HbleC) + HbleD * HbleE)
         / (x * (x * HbleA + HbleB) + HbleD * HbleF) - HbleE / HbleF;

    /// <summary>Mobius：逐字对齐 ffmpeg vf_tonemap.c::mobius(in, j, peak)。</summary>
    private static double Mobius(double x, double j, double peak)
    {
        if (x <= j) return x;
        double denom = j * j - 2.0 * j + peak;
        double a = -j * j * (peak - 1.0) / (Math.Abs(denom) < 1e-12 ? 1e-12 : denom);
        double b = (j * j - 2.0 * j * peak + peak) / Math.Max(peak - 1.0, 1e-6);
        return (b * b + 2.0 * b * j + j * j) / (b - a) * (x + a) / (x + b);
    }

    /// <summary>对**单个亮度/通道值**做映射：输入线性（1.0 = SDR 白），输出 ∈ [0,1]。</summary>
    public static double Apply(double y, in ToneMapParams p)
    {
        if (!p.Active) return y < 0 ? 0 : y;
        return p.Mode switch
        {
            ToneMapMode.ReinhardSegmented => SimdPixelOps.SegmentedReinhardScalar((float)y, (float)p.Headroom),
            // ffmpeg 语义：hable(sig)/hable(peak)，peak 即 headroom（本域 1.0 = SDR 白）
            ToneMapMode.Hable => y <= 0 ? 0 : HableRaw(y) / HableRaw(p.Headroom),
            ToneMapMode.Mobius => Mobius(y, p.Knee, p.Headroom),
            _ => y,
        };
    }

    /// <summary>
    /// 对线性 RGB 做**保色相**色调映射：以 max(R,G,B) 为压缩对象（与 GainMap 完全一致的选择，
    /// 不用 BT.709 亮度做分母，避免高饱和色被过压缩后出现暗环），再按同一比例缩放三通道。
    /// </summary>
    public static void ApplyRgb(ref float r, ref float g, ref float b, in ToneMapParams p, TransferCurve dstCurve)
    {
        if (!p.Active) return;
        if (p.Mode == ToneMapMode.Bt2446A) { ApplyBt2446A(ref r, ref g, ref b, p, dstCurve); return; }
        float maxY = Math.Max(r, Math.Max(g, b));
        if (maxY <= 1e-6f) return;
        float mapped = (float)Apply(maxY, p);
        float scale = mapped / maxY;
        r *= scale; g *= scale; b *= scale;
    }

    /// <summary>
    /// BT.2446-1 §4.1 Method A 的像素级实现。
    /// 值域换算：引擎线性域是“1.0 = SDR 参考白”，建议书要“1.0 = L_HDR”⇒ v_m = v · SdrWhiteNits / PeakNits。
    /// 输出：建议书给的是**最终 SDR 码值信号** R'G'B'，所以下方用 dstCurve.ToLinear 反推回线性，
    /// 让内核第 5 步原样编码回 R' —— 避免自造 EOTF 约定与双重转换误差。
    /// ⚠ 负值（矩阵混色产生）在 1/12.4 与幂运算前钳到 0：建议书假定输入为非负线性显示光。
    /// </summary>
    private static void ApplyBt2446A(ref float r, ref float g, ref float b, in ToneMapParams p, TransferCurve dstCurve)
    {
        double k = p.SdrWhiteNits / Math.Max(1.0, p.PeakNits);   // → “1.0 = L_HDR”
        double nr = Math.Max(0.0, r * k), ng = Math.Max(0.0, g * k), nb = Math.Max(0.0, b * k);
        double rp = Bt2446MethodA.Gamma124(nr), gp = Bt2446MethodA.Gamma124(ng), bp = Bt2446MethodA.Gamma124(nb);
        double yp = Bt2446MethodA.LumaPrime(rp, gp, bp);
        double ysdr = Bt2446MethodA.ToneMapLuma(yp, p.PeakNits, p.SdrPeakNits);
        Bt2446MethodA.ColourCorrect(rp, gp, bp, ysdr, out double cb, out double cr, out double ytmo);
        // 反 BT.2020 Table 4（非线性域）：R'=Y'+1.4746Cr'  B'=Y'+1.8814Cb'  G'=(Y'−0.2627R'−0.0593B')/0.6780
        double ro = ytmo + 1.4746 * cr, bo = ytmo + 1.8814 * cb;
        double go = (ytmo - 0.2627 * ro - 0.0593 * bo) / 0.6780;
        TransferCurve dst = dstCurve;   // struct（不可空）：调用方必须传真实目标曲线
        r = (float)dst.ToLinear(Math.Clamp(ro, 0.0, 1.0));
        g = (float)dst.ToLinear(Math.Clamp(go, 0.0, 1.0));
        b = (float)dst.ToLinear(Math.Clamp(bo, 0.0, 1.0));
    }

    /// <summary>诊断用：把 headroom 域整条曲线取点（供探针验单调/端点，不参与渲染）。</summary>
    public static double[] BuildCurve(in ToneMapParams p, int samples, double from, double to)
    {
        var c = new double[samples];
        for (int i = 0; i < samples; i++)
        {
            double x = from + (to - from) * (samples == 1 ? 0 : i / (double)(samples - 1));
            c[i] = Apply(x, p);
        }
        return c;
    }
}
