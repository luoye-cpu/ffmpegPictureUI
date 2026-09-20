using System;
using System.Collections.Generic;

namespace FfmpegGui.Services.ColorMapping;

/// <summary>
/// 识别层（引擎 L1）：把"容器标签 + ICC 度量值"统一解析成 <see cref="ColorSpaceDescriptor"/>，
/// 并把 ICC 描述的空间**归入已知空间**（获得 CICP token，从而可用 H2/ffmpeg 后端与正确标注）。
///
/// 优先级（与项目既有决策一致）：**ICC 度量值 &gt; CICP 标签 &gt; 无任何描述**。
/// 标签与 ICC 矛盾时以 ICC 为准（历史上"按标签猜色域"是偏色主因之一）。
/// </summary>
public static class ColorSpaceIdentify
{
    /// <summary>可比对的候选空间（token 或具名），用于"读 ICC 反推这是哪个已知空间"。</summary>
    private static readonly (string Label, bool IsToken, string Key)[] KnownSpaceCandidates =
    {
        ("sRGB/bt709",     true,  "bt709"),
        ("BT.2020",        true,  "bt2020"),
        ("Display P3",     true,  "smpte432"),
        ("DCI-P3",         true,  "smpte431"),
        ("BT.601-625",     true,  "bt470bg"),
        ("sRGB",           false, "sRGB"),
        ("Display P3",     false, "Display P3"),
        ("Adobe RGB",      false, "Adobe RGB"),
        ("ProPhoto RGB",   false, "ProPhoto RGB"),
        ("ColorMatch RGB", false, "ColorMatch RGB"),
        ("BT.2020",        false, "BT.2020"),
    };

    /// <summary>色度矩阵匹配容差（ICC 为 16.16 定点，各家实现取整差异 &lt;1e-3）。</summary>
    private const double KnownSpaceTol = 2e-3;

    /// <summary>
    /// 由 ICC 文件构造描述符。取不到色度矩阵或曲线不可解析（含非中性 TRC）→ 返回 null，
    /// 上层必须走"携带 ICC"或告警，不得改用近似曲线映射。
    /// </summary>
    public static ColorSpaceDescriptor? FromIccFile(string? iccPath, string? descriptionHint = null)
    {
        if (string.IsNullOrWhiteSpace(iccPath)) return null;
        var m = IccProfileService.ReadIccColorants(iccPath);
        var curve = IccProfileService.ReadIccCurve(iccPath);
        if (m == null || curve == null) return null;

        var d = ColorSpaceDescriptor.FromIcc(m, curve.Value,
            $"ICC:{(string.IsNullOrWhiteSpace(descriptionHint) ? "profile" : descriptionHint)}", iccPath);
        RefineKnownSpace(d, curve.Value);
        d.Confidence = DescriptorConfidence.IccMetric;      // 即使归入已知空间，仍保持最高置信度
        return d;
    }

    /// <summary>
    /// 把 ICC 得到的矩阵与已知空间理论值比对；命中则补 PrimariesToken / 色度坐标 / 更友好的名字，
    /// 使该空间可被 zscale（H2）表达并可写成标准 ICC/CICP 标注。
    /// 曲线保持 ICC 原样（可能是表曲线），因此命中不代表曲线可命名。
    /// </summary>
    public static void RefineKnownSpace(ColorSpaceDescriptor d, TransferCurve? curveOverride = null)
    {
        if (d.RgbToXyzD50 == null) return;
        double[]? best = null; string? bestLabel = null; string? bestToken = null; double bestErr = double.MaxValue;

        foreach (var (label, isToken, key) in KnownSpaceCandidates)
        {
            var m = isToken ? ColorSpaceRegistry.ColorantsD50(key) : ColorSpaceRegistry.NamedSpaceColorantsD50(key);
            if (m == null) continue;
            double err = MaxAbsDiff(m, d.RgbToXyzD50);
            if (err < bestErr) { bestErr = err; best = m; bestLabel = label; bestToken = isToken ? key : null; }
        }
        if (bestErr > KnownSpaceTol || bestLabel == null) return;      // 不匹配任何已知空间：保留纯 ICC 描述

        var curve = curveOverride ?? d.Curve;
        d.Name = $"{bestLabel} (ICC 识别, {curve.Name}, err={bestErr:E1})";
        d.RgbToXyzD50 = best;                                          // 采用理论值，消除定点取整噪声
        d.PrimariesToken = bestToken;
        // 注册名：具名条目的 label 就是注册名；token 条目走**显式**反查表（不能扫 registry 取第一个同 token 者）
        d.KnownSpaceName = CanonicalSpaceName(bestToken ?? bestLabel);
        if (bestToken != null)
        {
            var def = ColorSpaceRegistry.GetPrimaries(bestToken);
            if (def != null) { d.Rp = def.R; d.Gp = def.G; d.Bp = def.B; d.Wp = def.W; }
        }
        else
        {
            var chr = ColorSpaceRegistry.NamedSpaceChromaticities(bestLabel);
            if (chr != null) { d.Rp = chr.Value.R; d.Gp = chr.Value.G; d.Bp = chr.Value.B; d.Wp = chr.Value.W; }
        }
    }

    private static double MaxAbsDiff(double[] a, double[] b)
    {
        double e = 0;
        for (int i = 0; i < 9; i++) e = Math.Max(e, Math.Abs(a[i] - b[i]));
        return e;
    }

    /// <summary>
    /// CICP primaries token → <see cref="ColorSpaceRegistry"/> 认识的规范空间名。
    /// ⚠ 必须显式列表：从 registry 反查 token 会踩“多名共享同组原色”的歧义（本仓库吃过这类亏）。
    /// 选名时兼顾两个消费方：《ApplyIccSemantics》的 switch 与 registry 解析（“Rec.2020”两边都认）。
    /// </summary>
    private static readonly Dictionary<string, string> TokenToSpaceName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bt709"]    = "sRGB",
        ["smpte432"] = "Display P3",
        ["smpte431"] = "DCI-P3",
        ["bt2020"]   = "Rec.2020",
    };

    /// <summary>把 token 或具名 label 统一成注册名（未知 token 返回原值，由调用方判定能否解析）。</summary>
    private static string? CanonicalSpaceName(string? tokenOrLabel)
    {
        if (string.IsNullOrWhiteSpace(tokenOrLabel)) return null;
        if (TokenToSpaceName.TryGetValue(tokenOrLabel, out var name)) return name;
        return tokenOrLabel switch                                   // 具名条目已是注册名；纠两个写法差异
        {
            "BT.2020" => "Rec.2020",
            _ => tokenOrLabel,
        };
    }

    /// <summary>
    /// 给 ICC 定一个 registry 认识的规范空间名 —— 判据顺序固定为：
    /// **色度矩阵 + 曲线度量命中**（<see cref="FromIccFile"/> 内部已做，置信度最高）→ 否则才退回
    /// 描述文字猜测（<see cref="IccProfileService.GuessColorSpace"/>）→ 再不行 null（绝不猜 sRGB）。
    ///
    /// 为何必须有这条：ICC 的**描述文字不可靠** —— 本仓库自己生成的标准 ICC 描述就是
    /// “RGB built-in”（实测），而相机/软件写的描述又常是任意字符串；只按描述猜会让
    /// “超广色域保真 / 归一 BT.2020 / GainMap 底图原色” 这些依赖 sourceGamutName 的判断**静默失效**。
    /// </summary>
    public static string? IdentifyIccSpaceName(string? iccPath, string? descriptionHint = null)
    {
        var d = FromIccFile(iccPath, descriptionHint);
        if (!string.IsNullOrWhiteSpace(d?.KnownSpaceName)) return d!.KnownSpaceName;
        return IccProfileService.GuessColorSpace(descriptionHint);   // 度量认不出时才拿描述当线索
    }

    /// <summary>
    /// 由 CICP tokens 构造（无 ICC 时的主路径）。primaries 未标注时按业界惯例 **假定 sRGB**
    /// （项目决策 P9：只有 RAW 输入才允许假定 BT.2020）。
    ///
    /// 置信度规则（规划 §5′.1）：**两项标签均真实存在** ⇒ <see cref="DescriptorConfidence.CicpTag"/>；
    /// 只要有一项是我们“按惯例假定”的 ⇒ 降为 <see cref="DescriptorConfidence.CodecDefault"/>（不允许引擎改像素映射）。
    /// </summary>
    public static ColorSpaceDescriptor? FromCicpTokens(string? primaries, string? transfer,
        string? name = null, double nitsOfLinearOne = TransferCurve.SdrWhiteNits)
    {
        bool tagged = IsRealTag(primaries) && IsRealTag(transfer);
        var d = ColorSpaceDescriptor.FromCicp(NormalizePrimaries(primaries), NormalizeTransfer(transfer), name, nitsOfLinearOne);
        if (d != null)
        {
            d.Confidence = tagged ? DescriptorConfidence.CicpTag : DescriptorConfidence.CodecDefault;
            if (!tagged) d.Name += " [假定]";
            return d;
        }
        // 具名空间优先（如 "Display P3"、"BT.2020 PQ" 这类内部名）
        var n = ColorSpaceDescriptor.FromNamedSpace(name);
        if (n != null) n.Confidence = DescriptorConfidence.NamedIcc;
        return n;
    }

    /// <summary>完全无描述时的显式假定入口（置信度 CodecDefault，仅可用于标注继承，不可用于映射）。</summary>
    public static ColorSpaceDescriptor? AssumeByConvention(bool sourceIsRaw = false)
    {
        var d = ColorSpaceDescriptor.FromCicp(sourceIsRaw ? "bt2020" : "bt709",
            sourceIsRaw ? "bt709" : "iec61966-2-1",
            sourceIsRaw ? "BT.2020 (RAW 假定)" : "sRGB (惯例假定)");
        if (d != null) d.Confidence = DescriptorConfidence.CodecDefault;
        return d;
    }

    private static bool IsRealTag(string? t)
    {
        var s = (t ?? "").Trim().ToLowerInvariant();
        return s.Length > 0 && s is not ("unspecified" or "unknown" or "reserved" or "unknown(bt709)" or "tv");
    }

    private static string? NormalizePrimaries(string? p)
    {
        var t = (p ?? "").Trim().ToLowerInvariant();
        return t.Length == 0 || t is "unspecified" or "unknown" or "reserved" ? "bt709" : t;
    }

    private static string? NormalizeTransfer(string? t)
    {
        var s = (t ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0 || s is "unspecified" or "unknown" or "reserved") return "iec61966-2-1";   // 无标签 → sRGB 惯例
        return s;
    }

    /// <summary>诊断串（供日志与 shadow 对比）。</summary>
    public static string Describe(ColorSpaceDescriptor? d)
        => d == null ? "(未识别)" : d.ToDiagnosticString();
}
