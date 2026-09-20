using System;
using System.Collections.Generic;
using FfmpegGui.Models;

namespace FfmpegGui.Services.ColorMapping;

/// <summary>
/// 策略层（规划 §1/§2）：把用户声明的四大策略 + 手动字段翻译成唯一计划。
///
/// 三条硬规矩（由 <c>ServiceProbe contract</c> 机器校验，不得靠注释遵守）：
/// 1. <b>S2 永不改像素、永不生成 ICC</b> —— 只有"携带 / 无标注直通 / Reject"三种结局；
/// 2. <b>S3 只写 CICP</b> —— 容器不能写 CICP 时只允许"隐含 sRGB 的无标注直通"或 Reject；
/// 3. 功能要求可覆盖策略（GainMap 要求底图映射、HDR 无 ICC 携带通路），但必须写
///    <see cref="ColorTransformPlan.Override"/> 与日志，禁止"偷偷覆盖"。
/// </summary>
public static partial class ColorMappingEngine
{
    /// <summary>sRGB 参照描述符（判定"隐含 sRGB 等价"用）。</summary>
    private static readonly ColorSpaceDescriptor? SrgbRef =
        ColorSpaceDescriptor.FromCicp("bt709", "iec61966-2-1", "sRGB");

    /// <summary>按策略产出计划（管线唯一入口）。</summary>
    public static ColorTransformPlan Plan(ColorIntent it)
    {
        if (it == null) throw new ArgumentNullException(nameof(it));
        var pol = it.ToPolicy();
        var p = PlanWithPolicy(it, pol);
        // ── GainMap 请求位：**唯一装配点** ──
        // 放在这里而不是各个 PlanXxx 里，是因为本方法的返回分支很多（PlanFaithful / PlanCarry /
        // PlanCicpOnly / PlanManual / PlanRecommended，且各自还能 Reject 提前返回）——
        // 漏掉任何一支都会让「用户要了 GainMap、执行层却不知道」再次变成静默降级。
        // 计划层只负责**传递**这个位；「本次能否产出 GainMap」由 PlanWithPolicy 开头按
        // FormatColorCaps.SupportsGainMap 判定（非 JPEG 族直接 Reject）。
        p.GainMap = pol.GainMapRequested;
        // ── 外部编码器出口位（cjxl）：**唯一装配点**（2026-09-17 接入）──
        // 与 GainMap 同款理由：本方法返回分支很多，漏掉任何一支都会让「计划说走 cjxl、
        // 执行层却不知道」再次变成**静默换编码器**（产物由 ffmpeg-libjxl 出，用户选的 cjxl 参数全丢）。
        // 计划层只负责**传递**这个位；「本次能否走 cjxl 出口」由路由层（`ColorEngineRouter.BlockReason`）
        // 与执行层（`RawColorPipeline` 的出口）各自显式判定，任一不满足都**显式失败**，不静默回退。
        p.ExternalEncoderExit = pol.ExternalEncoderExit;
        // ── 统一裁决（S3 统一形状 + S5′ 补结构化损失）：**唯一投影点** ──
        // 放在本方法的 return 之前，是因为这里能覆盖 PlanWithPolicy 的**所有**返回分支
        // （PlanFaithful / PlanCarry / PlanCicpOnly / PlanManual / PlanRecommended，
        //  以及它们各自的 Reject 提前返回、EnsureAnnotationNotSilentlyMissing 的兜底 Reject）。
        // ⚠ S5′ 仍然**不改语义**：只在「本来就会产出」的格上登记**已发生**的损失，
        //   不翻任何 Action ⇒ 不改变"产出 / 不产出"，也不改变任何产物字节。
        p.Verdict = ProjectVerdict(p, pol, it);
        return p;
    }

    /// <summary>
    /// 把计划的最终状态投影成统一裁决（S1/S3 的**唯一投影点**；S5′ 在此补结构化损失）。
    /// <para>
    /// 投影规则：
    /// <list type="bullet">
    ///   <item><c>Action == Reject</c> ⇒ <see cref="VerdictKind.Reject"/>（**不登记**任何损失 ——
    ///         <c>Degradations</c> 的语义是「**已发生的**损失」，不是「将会拒绝的原因」）；</item>
    ///   <item>已产出（None / Map / CarryIcc）且 <c>Degradations</c> 非空 ⇒ <see cref="VerdictKind.ProceedWithDegradation"/>；</item>
    ///   <item>已产出且 <c>Degradations</c> 为空 ⇒ <see cref="VerdictKind.Proceed"/>。</item>
    /// </list>
    /// 即 <c>Kind == Proceed</c> ⇔ <c>Degradations.Count == 0</c>（自洽性由 <c>ServiceProbe verdict</c> 断言）。
    /// </para>
    /// <para>
    /// <c>Action</c> / <c>Reason</c> / <c>Advice</c> 原样带过去（兼容投影，一个字符都不改）。
    /// </para>
    /// <para>
    /// <c>Alternatives</c> **只搬运** <see cref="Reject"/> 助手已经放进
    /// <see cref="ColorTransformPlan.Verdict"/> 的结构化内容（调用点确实知道替代时才会放）；
    /// 本方法**不做任何推断**——尤其禁止从 <c>Advice</c> 自由文本反推替代（那是把文案当判据）。
    /// </para>
    /// </summary>
    /// <param name="pol">
    /// 规划策略（**只读** <see cref="PlanPolicy.Format"/> 的能力表）：<c>ContainerCannotCarryIcc</c>
    /// 的归因必须来自能力表事实，而不是从计划字段反猜容器。
    /// </param>
    /// <param name="it">
    /// 本次意图（**只读** <see cref="ColorIntent.Source"/>）：⚠ 必须从这里读源描述符，**不能**用
    /// <see cref="ColorTransformPlan.Src"/> —— 后者只有走 <c>PlanCore</c> 的分支才会被赋值，
    /// 而 <c>PlanCarry</c> / <c>PlanCicpOnly</c> 的多个分支返回的是 <c>Fresh()</c> 出来的计划
    /// （<c>Src == null</c>）⇒ 从计划读会把"源带 ICC"这一事实漏成假阴性。
    /// </param>
    private static ColorVerdict ProjectVerdict(ColorTransformPlan p, PlanPolicy pol, ColorIntent it)
    {
        var staged = p.Verdict;
        var degs = CollectDegradations(p, pol, it);
        if (staged?.Degradations is { Count: > 0 })
        {
            // 调用点（Reject 助手）确实登记了损失时**搬运**；当前恒空，保留以免将来被静默丢弃。
            for (int i = 0; i < staged.Degradations.Count; i++) degs.Add(staged.Degradations[i]);
        }
        return new ColorVerdict
        {
            Kind = p.Action == ColorAction.Reject
                ? VerdictKind.Reject
                : degs.Count > 0 ? VerdictKind.ProceedWithDegradation : VerdictKind.Proceed,
            Action = p.Action,
            Reason = p.Reason,
            Advice = p.Advice,
            Degradations = degs,
            Alternatives = staged?.Alternatives ?? Array.Empty<ColorAlternative>(),
        };
    }

    /// <summary>
    /// S5′：给「引擎本来就会产出、但确实丢了东西」的格登记**已发生**的结构化损失。
    /// <para>
    /// **判据一律取自现成字段**（<see cref="ColorTransformPlan"/> 的 Action/OutBitDepth/
    /// GamutOutsideDestination/UnlabeledAssumedSrgb/AttachIcc、<see cref="PlanPolicy.Format"/> 的能力表、
    /// <see cref="ColorIntent.Source"/> 的 <c>SourceIccPath</c>），**不新算任何东西**；
    /// 且只在 <c>Action != Reject</c> 时登记 —— <c>Reject</c> 格的"原因"属于 <c>Reason</c>/<c>Alternatives</c>，
    /// 不属于 <c>Degradations</c>（后者是「已发生」而非「将拒绝」）。
    /// </para>
    /// <para>顺序固定，保证裁决可逐项对拍。</para>
    /// </summary>
    private static List<ColorDegradation> CollectDegradations(ColorTransformPlan p, PlanPolicy pol, ColorIntent it)
    {
        var degs = new List<ColorDegradation>(4);
        if (p.Action == ColorAction.Reject) return degs;

        // ① 出口位深 16 → 8：只有**确实发生变换**（Map）才谈得上"被降位丢精度"；
        //    直通/携带（ColorLoss=None）规则上恒为 16bit 承载（见 PlanWithPolicy 的 OutBitDepth 赋值）。
        if (p.Action == ColorAction.Map && p.OutBitDepth == 8)
            degs.Add(new ColorDegradation(DegradationCodes.BitDepthReduced,
                $"出口位深降为 {p.OutBitDepth}bit（容器上限）⇒ 中间精度损失，已按目标位深做有序抖动"));

        // ② 源色域超出目标：**硬裁切**（GamutClipped）与 **GMO 压缩**（GamutCompressed）是两种不同的
        //    「已发生损失」，必须分开登记：
        //    · 映射生效时再报 GamutClipped（"被裁切"）与"已压缩"自相矛盾 —— 裁切根本没发生；
        //    · 两者都不登记则更糟：一次真实的去饱和会被裁决报成 Proceed（无任何损失）。
        if (p.GamutOutsideDestination)
        {
            degs.Add(p.GamutMap
                ? new ColorDegradation(DegradationCodes.GamutCompressed,
                    $"源色域超出目标 {p.Dst?.Name ?? "-"} ⇒ 已按 GMO 压缩（向同亮度灰混合去饱和，非硬裁切；"
                    + $"ColorLoss={p.ColorLoss}）")
                : new ColorDegradation(DegradationCodes.GamutClipped,
                    $"源色域超出目标 {p.Dst?.Name ?? "-"} ⇒ 越界颜色被裁切或压缩（ColorLoss={p.ColorLoss}）"));
        }

        // ③ 产物无任何标注，但按容器惯例把像素当隐含 sRGB 处理（语义正确，但描述是隐含的）。
        if (p.UnlabeledAssumedSrgb)
            degs.Add(new ColorDegradation(DegradationCodes.UnlabeledAssumedSrgb,
                "产物无色彩标注，像素按隐含 sRGB 处理（依赖容器/查看器惯例，非显式描述）"));

        // ④/⑤ 源有 ICC 但产物不附 ICC ⇒ 源描述精度丢失；容器能力表判 `!CanCarryArbitraryIcc` 时
        //      额外给出**结构性**归因（不是"没写"，是"写不了"）。
        // ⚠ 源描述符取自 `it.Source` 而非 `p.Src`：`p.Src` 只在 PlanCore 分支被赋值，
        //   而 PlanCarry/PlanCicpOnly 的多个分支返回 Fresh() 计划（Src==null）⇒ 从计划读会漏判。
        if (!p.AttachIcc && it.Source?.SourceIccPath != null)
        {
            degs.Add(new ColorDegradation(DegradationCodes.NoIccCarrier,
                "源带 ICC 但产物不附 ICC ⇒ 丢失源 ICC 的描述精度"));
            if (!pol.Format.CanCarryArbitraryIcc)
                degs.Add(new ColorDegradation(DegradationCodes.ContainerCannotCarryIcc,
                    $"容器 {pol.Format.Format} 不能携带任意 ICC（能力表 CanCarryArbitraryIcc=false）⇒ 被迫放弃源 ICC"));
        }

        // ⑥ GainMap 出口**不消费**计划层的 tonemap ⇒ 用户显式请求的曲线未生效（2026-09-18）。
        // 判据：计划确为 GainMap 出口（`p.GainMap`）+ 用户**显式**请求了曲线（`ToneMapRequested`）
        //   且**不是**系统自动兜底（`!ToneMapAuto`）。
        // ⚠ 为什么排除 `ToneMapAuto`：自动兜底（HDR 源 + 容器不支持 HDR ⇒ 内部默认 hable）的**目的**
        //   是"把 HDR 压成可看的 SDR 底图"，而 GainMap 出口的分段 Reinhard 正是干这件事
        //   ⇒ 目的已达成，不构成损失（否则每一次 HDR→JPEG GainMap 都会被误报成降级）。
        // ⚠ 为什么必须登记（而不是像 legacy 那样静默忽略）：用户点了 hable，实际生效的是编码器内部的
        //   分段 Reinhard —— 曲线不同、压暗不同。不登记就是"看起来配置过"，正是本项目最忌讳的。
        //   ⚠ 登记它**不改变**产出与否（`CollectDegradations` 只在已产出的格上跑，见 `ProjectVerdict`），
        //   只把 `Kind` 从 `Proceed` 翻成 `ProceedWithDegradation`。
        if (p.GainMap && it.ToneMapRequested && !it.ToneMapAuto)
            degs.Add(new ColorDegradation(DegradationCodes.ToneMapNotAppliedToGainMap,
                $"请求的 tonemap={it.ToneMap} 在 GainMap 出口**不被消费**：底图压暗由 GainMapEncoder 内部的"
                + "分段 Reinhard 自持 ⇒ 该选项未生效（产物已产出）"));
        return degs;
    }

    /// <summary>策略判定主体（<see cref="Plan(ColorIntent)"/> 的包体；返回值会被补上 GainMap 位）。</summary>
    private static ColorTransformPlan PlanWithPolicy(ColorIntent it, PlanPolicy pol)
    {
        // GainMap 是“输出结构”能力（ISO 21496-1 / XMP+MPF），只能由 JPEG 族承载。
        // 必须在契约层拦住：UI 的 IsVisible 不是门禁，CLI/预设能绕过它；
        // 否则会变成“用户以为有 HDR 增益图，实际只得到被压暗的 SDR”——静默降级。
        if (it.GainMapRequested && !pol.Format.SupportsGainMap)
        {
            var pg = Fresh(it.Strategy);
            return Reject(pg, $"增益图（GainMap）不适用于 {pol.Format.Format}：它依赖 JPEG 的 APP2/XMP+MPF 结构",
                "要保留 HDR：输出改用 AVIF/JXL（原生存 PQ/HLG）；或把目标改为 JPEG 并开启增益图",
                new[]
                {
                    new ColorAlternative(AlternativeKind.Format, "jpg",
                        "JPEG 族是唯一能承载 GainMap（APP2/XMP+MPF）的容器（能力表 SupportsGainMap）"),
                    new ColorAlternative(AlternativeKind.Format, "avif", "原生 PQ/HLG ⇒ 不经 GainMap 也能保 HDR"),
                    new ColorAlternative(AlternativeKind.Format, "jxl", "原生 PQ/HLG ⇒ 不经 GainMap 也能保 HDR"),
                });
        }
        // S1 auto 下的 HDR 保持：无显式目标且容器支持 HDR ⇒ 默认保持 HDR
        // （“HDR 不被悄悄 SDR 化”这条不能依赖调用方传参，否则 avif/jxl 会被误拒）
        // ⚠ **位深前提**（2026-09-17）：`CanHdr` 对 PNG/APNG 的能力前提是**位深 >8**
        //   （PQ/HLG 只能由 16bit 承载，见能力表 `HdrRequiresHighBitDepth`）。8bit 的 PNG 装不下 HDR
        //   ⇒ 这里不能置 HdrOutput，否则规划层会宣称"保持 HDR"而交付其实是 tonemap 后的 bt709/bt709
        //   —— 正是本项目最忌讳的「宣称≠交付」（实测 hdr_png8：计划 bt2020/smpte2084、交付 bt709/bt709）。
        if (pol.Strategy == ColorStrategy.Recommended && it.Target == null && !pol.HdrOutput
            && CanCarryHdrAt(pol.Format, pol.TargetBitDepth) && !pol.ToneMapRequested
            && it.Source?.Curve.Kind is TransferCurve.CurveKind.Pq or TransferCurve.CurveKind.Hlg)
            pol.HdrOutput = true;
        var (declared, eff, ov) = ResolveStrategy(it, pol);

        // ── 超广色域保真**优先于一切策略**（与传统管线 `DecideOutputColor` 的顺序一致：先判保真）──
        ColorTransformPlan p = !string.IsNullOrWhiteSpace(pol.FaithfulIccPath)
            ? PlanFaithful(it, pol, declared, ov)
            : eff switch
            {
                ColorStrategy.CarryIcc     => PlanCarry(it, pol, declared, ov),
                ColorStrategy.BakeCicpOnly => PlanCicpOnly(it, pol, declared, ov),
                ColorStrategy.Manual       => PlanManual(it, pol, declared, ov),
                _                          => PlanRecommended(it, pol, declared, ov),
            };

        p.DeclaredStrategy = declared;
        p.EffectiveStrategy = eff;
        if (p.Override == OverrideReason.None) p.Override = ov;
        // 色调映射挂载（P4）：仅当用户显式请求 + 选了算子 + 确实发生映射 + 源是 HDR 传递。
        // 峰值优先级：用户给定 / 容器静态元数据（装配点已读，见 ColorIntentFactory）> 源曲线峰值（PQ=10000/HLG=1000）；
        // 绝不默认 1000nits。
        if (it.ToneMapRequested && it.ToneMap != ToneMapMode.None && p.Action == ColorAction.Map
            && p.Src != null && p.Src.Curve.Kind is TransferCurve.CurveKind.Pq or TransferCurve.CurveKind.Hlg)
        {
            // 未实现的算子必须**显式拒绝**，绝不能“接受请求但什么也不做”（那等于静默钳位）。
            if (it.ToneMap == ToneMapMode.Bt2390)
                return Reject(p, "bt2390 无可靠出处：已核对现行 Report ITU-R BT.2390-12 正文，全文不含 EETF 定义"
                        + "（libplacebo/ffmpeg 的“bt.2390”命名无法回追到标准条款）",
                    "标准曲线请用 bt2446a（ITU-R BT.2446-1 §4 Method A，已按正文常数实现并通过自洽锚点验证）；"
                  + "或先用 hable / mobius");
            double peak = it.HdrPeakNits > 0 ? it.HdrPeakNits : p.Src.NitsOfLinearOne;
            p.Tone = new ToneMapParams
            {
                Mode = it.ToneMap, PeakNits = peak, SdrWhiteNits = it.SdrWhiteNits, SdrPeakNits = it.SdrPeakNits,
                // 没有用户值也没有元数据 ⇒ 这里的 peak 只是名义兜底，标记给执行层去整帧实测（P4c）。
                MeasurePeak = it.HdrPeakNits <= 0,
            };
            // 峰值来源必须入日志：headroom 直接决定压暗程度，而静像几乎不带 MaxCLL/BS.2086（实测），
            // 看不到“峰值从哪来”就等同于又一个“静默未激活”类缺陷。
            string peakFrom = it.HdrPeakNits > 0 ? (it.HdrPeakSource ?? "外部传入")
                                                 : $"待整帧实测（暂按源曲线名义峰值 {peak:F0}nit 兜底）";
            p.Reason += $" [tonemap: {p.Tone} 峰值来源={peakFrom}"
                      + (it.ToneMapAuto
                            ? "｜算子=自动（容器不支持 HDR ⇒ 内部默认曲线；用 --color-tone-map 可指定）"
                            : "")
                      + "]";
            // HLG 是 scene-referred：编码值→显示亮度需经 BT.2100-2 Table 5 的 OOTF（已按建议书原文实现，
            // 见 Bt2100Ootf：α=L_W、γ=1.2+0.42·Log10(L_W/1000)）。尚未接入像素养分路径，故仍写明来源与默认 L_W。
            if (p.Src.Curve.Kind == TransferCurve.CurveKind.Hlg)
                p.Advice = $"HLG 按 BT.2100-2 Table 5 OOTF 处理（α=L_W={p.Src.NitsOfLinearOne:F0}nit, "
                         + $"γ={FfmpegGui.Services.ColorMapping.Bt2100Ootf.SystemGamma(p.Src.NitsOfLinearOne):F4}）；"
                         + "如需按实际显示峰值，用 --color-hdr-peak 指定 L_W。";
        }

        p.CodecLoss = CodecLossOf(pol.Format.Format, it.TargetLossless);
        EnforceContainerAnnotationRules(p, pol.Format);
        p.ColorLoss = ClassifyColorLoss(p, pol, it);
        p.Annot = ClassifyAnnot(p);
        // 出口位深真驱动（§5′.6）：直通/携带绝不降位；只有确实变换且目标容器上限 8bit 才走 8bit+抖动。
        // ⚠ 2026-09-16 追加 `pol.Format.RgbNative` 条件：**RGB 原生容器**（png/tiff/apng）的像素
        //   本身就是交付物 ⇒ 8bit 容器就该出 8bit。而 **YUV 容器**（jpg/webp）里 8bit 只是编码结果，
        //   中间件降成 8bit 会在 RGB→YUV 之前白丢精度（legacy 是保 16bit 到编码器）。
        //   A/B 实测：引擎出 `rgb24` 中间件时与 legacy 的 jpg 在 RGB 域仅 30.3dB（max|Δ|=122）；
        //   改为 16bit 中间件后见 TESTING §6 第 49 条复测。
        p.OutBitDepth = p.Action != ColorAction.Map || p.ColorLoss == ColorLoss.None ? 16
            : (pol.TargetBitDepth <= 8 && !p.Requires16BitIntermediate && pol.Format.RgbNative ? 8 : 16);
        EnsureAnnotationNotSilentlyMissing(p, pol, it);
        return p;
    }

    // ══════════════════════════ 策略覆盖（§2）══════════════════════════

    private static (ColorStrategy declared, ColorStrategy eff, OverrideReason ov) ResolveStrategy(ColorIntent it, PlanPolicy pol)
    {
        var declared = it.Strategy;
        bool srcHdr = it.Source?.Curve.Kind is TransferCurve.CurveKind.Pq or TransferCurve.CurveKind.Hlg;

        // ① 生成 GainMap：功能定义要求底图 tone-map + 色域映射 ⇒ **声明策略不适用**（规划 §2 覆盖①）。
        //    对三条非 S1 策略都要覆盖，各自都是"在该出口结构上不可兑现"，不是为了让断言变绿：
        //      · S2「携带」：底图必须被重映射，携带源 ICC 会与重映射后的像素失配；
        //      · S3「仅 CICP」：GainMap 只能由 JPEG 承载，而 **JPEG 没有 CICP 通路**
        //        （底图色域由底图 ICC 表达，见 GainMapEncoder）⇒ 该策略在此出口不可能成立；
        //      · S4「手动」未给目标：GainMap 的目标底图色域由 `JpegGainMapBaseGamut` 决定，
        //        "没有显式目标"不是错误 —— 否则 GainMap 在手动策略下只剩 Reject，功能无路可走。
        //    ⚠ 覆盖必须留痕（Override + 日志），禁止"偷偷覆盖"：本函数只返回原因，
        //      由 Plan 写进计划；声明值（declared）原样保留，两条管线都能看出"用户要的是哪个策略"。
        if (it.GainMapRequested && declared != ColorStrategy.Recommended)
        {
            var eff = (it.Manual?.HasAny ?? false) ? ColorStrategy.Manual : ColorStrategy.Recommended;
            return (declared, eff, eff == declared ? OverrideReason.None : OverrideReason.GainMapRequiresBaseMapping);
        }

        if (declared != ColorStrategy.CarryIcc) return (declared, declared, OverrideReason.None);

        // ② HDR 通路无"携带 HDR ICC"能力 ⇒ 仍是 S2（不改像素），但结局只能是标注继承
        if (srcHdr) return (declared, declared, OverrideReason.HdrNoIccCarryPath);
        // ③ 容器不能携带 ICC ⇒ 仍是 S2，结局为 Reject（E1 未通过前的 AVIF 即此）
        if (!pol.Format.CanCarryArbitraryIcc) return (declared, declared, OverrideReason.ContainerCannotCarryIcc);
        return (declared, declared, OverrideReason.None);
    }

    // ══════════════════════════ L0 超广色域保真（2026-09-16 接入引擎）══════════════════════════

    /// <summary>
    /// 超广色域保真：**像素零改动 + 仅靠源 ICC 描述**。语义与传统管线的 Faithful 分支一致。
    /// <para>
    /// **为什么必须先于策略判定**：ProPhoto/ROMM 等色域**超出 BT.2020** ⇒ 任何"归一化"都会削色；
    /// 而 zscale 又无法命名其 primaries 与 1.8 传递（旧行为按 sRGB 曲线线性化 + 矩阵转 BT.2020，
    /// 会**像素与标签双错**）。唯一正确的做法是像素一帧不动 + 附着该 ICC（Photoshop/darktable 惯例）。
    /// </para>
    /// <para>
    /// **标注取舍**：**不写任何 CICP** —— 写 bt709 会与 ProPhoto ICC 冲突，写 bt2020 则是削色后的假标签。
    /// </para>
    /// </summary>
    private static ColorTransformPlan PlanFaithful(ColorIntent it, PlanPolicy pol,
        ColorStrategy declared, OverrideReason ov)
    {
        var p = Fresh(declared);
        p.Action = ColorAction.CarryIcc;
        p.Backend = ColorBackend.None;
        p.AttachIcc = true;
        p.IccPath = pol.FaithfulIccPath;
        p.ColorLoss = ColorLoss.None;
        p.OutPrimaries = p.OutTransfer = p.OutMatrix = null;      // 不写 CICP（与 ICC 冲突）
        p.Reason = "L0 超广色域保真：像素零改动 + 仅靠源 ICC 描述"
                 + $"（{System.IO.Path.GetFileName(pol.FaithfulIccPath)}）；"
                 + "不写 CICP（写 bt709 会与 ICC 冲突、bt2020 是削色后的假标签）";
        p.Advice = "如需映射到某个标准空间，请显式指定目标色域（那将放弃保真）。";
        if (ov != OverrideReason.None) p.Override = ov;
        return p;
    }

    // ══════════════════════════ S2 携带（三种结局）══════════════════════

    private static ColorTransformPlan PlanCarry(ColorIntent it, PlanPolicy pol, ColorStrategy declared, OverrideReason ov)
    {
        var p = Fresh(declared);
        var src = it.Source;
        var caps = pol.Format;
        if (src == null)
            return Reject(p, "S2 携带：源色彩无法确定（既无 ICC 也无 CICP）",
                "为源文件补色彩描述，或改用 S1 推荐");

        bool hdr = src.Curve.Kind is TransferCurve.CurveKind.Pq or TransferCurve.CurveKind.Hlg;
        if (hdr)
        {
            // §2 覆盖②：不改像素，仅沿用源的 CICP 描述（能命名且容器支持时）
            p.Action = ColorAction.None;
            p.Override = OverrideReason.HdrNoIccCarryPath;
            p.ColorLoss = ColorLoss.None;
            p.Reason = "HDR 无 ICC 携带通路（iccgen 不支持 PQ/HLG）→ 不改像素，仅标注继承";
            p.Advice = "如需精确描述请用支持 rICC 的容器；S2 不生成 ICC、不映射";
            CarryCicpLabels(p, src, caps);
            // 既不能携带 HDR ICC、又不能写 CICP ⇒ 该容器无法表达 HDR，必须拒绝而不是静默产出无标注行
            if (p.OutPrimaries == null && !p.AttachIcc)
                return Reject(p, $"S2 携带：{caps.Format} 既不能携带 HDR ICC 又不能写 CICP → HDR 无法表达",
                    "改输出到 AVIF/JXL（CICP 枚举或 rICC），或显式请求 tonemap 降 SDR",
                    // ⚠ 不填 heif：能力表实测本工具链**无 heif muxer/无 heifenc**（"根无写入通路"），
                    //   把它列成替代等于把用户送进必然失败的任务。
                    //   2026-09-16：Advice 文案**同步去掉了 HEIF**（原先写 `AVIF/HEIF/JXL`）——
                    //   推荐一个无写入通路的格式属**误导**，与「可用性诚实」冲突。
                    //   已确认门禁不匹配该文案（`verify-color-caps.ps1` 只断言 heic/heif 的**能力表**保守），故改动安全。
                    FormatAlts("原生 CICP（PQ/HLG）或 rICC ⇒ 可表达 HDR", "avif", "jxl"));
            return p;
        }

        // 结局 1：源有 ICC 且容器可携带 ⇒ 像素零改动 + 附着源 ICC
        if (src.SourceIccPath != null)
        {
            if (!caps.CanCarryArbitraryIcc)
                return Reject(p, $"S2 携带：源有 ICC 但 {caps.Format} 不能携带任意 ICC"
                                 + (caps.HasColorPath ? "" : "（该容器无任何色彩管理通路）"),
                    "改用 PNG/TIFF/WebP/AVIF/JXL 携带，或改 S1 用可命名空间 + 标准 ICC",
                    FormatAlts("能力表 CanCarryArbitraryIcc ⇒ 可原样携带源 ICC", "png", "tiff", "webp", "avif", "jxl"));
            p.Action = ColorAction.CarryIcc;
            p.Backend = ColorBackend.None;
            p.AttachIcc = true;
            p.IccPath = src.SourceIccPath;
            p.ColorLoss = ColorLoss.None;
            p.Reason = "L1 无损：像素零改动，携带源 ICC（" + (System.IO.Path.GetFileName(src.SourceIccPath)) + "）";
            CarryCicpLabels(p, src, caps);
            return p;
        }

        // 结局 2（两种形态，都是像素零改动）：
        //   2a 容器可写 CICP 且源可命名 ⇒ **继承源 CICP 标签**（描述随容器元数据一起带走，不生成 ICC）
        //   2b 容器未标注语义明确（PNG/JPEG/WebP）⇒ 无标注直通 + UnlabeledAssumedSrgb
        if (src.Confidence >= EquivalenceTolerances.MinConfidenceForMapping && CapsAcceptsCicp(caps, src))
        {
            p.Action = ColorAction.None;
            p.Backend = ColorBackend.None;
            p.ColorLoss = ColorLoss.None;
            p.Reason = "源无 ICC 但带 CICP 标签且容器可写 CICP → 不改像素，仅继承标签（携带的第二形态）";
            p.Override = ov == OverrideReason.None ? OverrideReason.SourceHasNoIcc : ov;
            CarryCicpLabels(p, src, caps);
            return p;
        }
        if (src.Confidence >= EquivalenceTolerances.MinConfidenceForMapping && IsSrgbEquivalent(src)
            && (caps.UnlabeledMeansSrgb || pol.AllowUnlabeled || !caps.HasColorPath))
        {
            p.Action = ColorAction.None;
            p.Backend = ColorBackend.None;
            p.UnlabeledAssumedSrgb = true;
            p.Override = ov == OverrideReason.None ? OverrideReason.SourceHasNoIcc : ov;
            p.ColorLoss = ColorLoss.None;
            p.Reason = "源无 ICC 且判定为隐含 sRGB/等价 + 容器未标注语义明确 → L0 无标注直通（不生成 ICC、不映射）";
            p.Advice = "若源实为广色域，请改用 S1（可附标准 ICC）或为源补 ICC";
            return p;
        }

        return Reject(p, "S2 携带：源无 ICC 可携带，且不满足\"隐含 sRGB + 容器未标注语义明确\"" +
                         (caps.UnlabeledMeansSrgb ? "" : $"（{caps.Format} 的未标注语义不统一）"),
            "为源补 ICC；或改 S1（可命名空间用标准 ICC 描述）；S2 不生成 ICC、不改像素");
    }

    // ══════════════════════════ S3 仅烘焙 CICP ═════════════════════════

    private static ColorTransformPlan PlanCicpOnly(ColorIntent it, PlanPolicy pol, ColorStrategy declared, OverrideReason ov)
    {
        var p = Fresh(declared);
        var src = it.Source;
        var caps = pol.Format;

        if (!caps.CanCicp)
        {
            // 容器无 CICP 通路：只有"未标注即隐含 sRGB 且该容器惯例明确"才允许无操作直通
            if (src != null && IsSrgbEquivalent(src)
                && src.Confidence >= EquivalenceTolerances.MinConfidenceForMapping
                && (caps.UnlabeledMeansSrgb || pol.AllowUnlabeled || !caps.HasColorPath))
            {
                p.Action = ColorAction.None;
                p.Backend = ColorBackend.None;
                p.UnlabeledAssumedSrgb = true;
                p.CicpUnavailable = caps.NoCicpReason;
                p.ColorLoss = ColorLoss.None;
                p.Reason = $"{caps.Format} 无 CICP 通路且 sRGB 未标注语义明确 → 不写任何标注、不改像素";
                return p;
            }
            return Reject(p, $"S3 仅 CICP：{caps.Format} 不能写 CICP" +
                             (caps.UnlabeledMeansSrgb ? "" : "，且未标注语义不统一（TIFF 类需显式 --color-allow-unlabeled）"),
                "改用支持 CICP 的容器（AVIF/JXL），或选 S2 携带 ICC / S1 允许 ICC 描述",
                // ⚠ 同前：heif 无写入通路，不列为替代；Advice 文案同步去掉 HEIF（2026-09-16）。
                FormatAlts("能力表 CanCicp ⇒ 可写 CICP", "avif", "jxl"));
        }

        if (src == null)
            return Reject(p, "S3 仅 CICP：源色彩无法确定 → 无法写出正确标签", "补源色彩描述或改用 S4 手动指定");

        // 目标：用户指定优先，否则取"该容器可 CICP 表达的最窄超集"
        var target = it.Target ?? NameableSupersetFor(src, caps);
        if (target == null || !CapsAcceptsCicp(caps, target))
            return Reject(p, $"S3 仅 CICP：源 {src.Name} 无法用 {caps.Format} 的 CICP 表达" +
                             (caps.CicpSpaceEnums != null ? $"（枚举集仅 {string.Join('/', caps.CicpSpaceEnums)}）" : ""),
                "改 S1/S2 并携带 ICC，或换容器（PNG/TIFF/WebP 可携带任意 ICC）",
                FormatAlts("能力表 CanCarryArbitraryIcc ⇒ 可携带任意 ICC", "png", "tiff", "webp"));

        if (src.Equivalent(target))
        {
            p.Action = ColorAction.None;
            p.Backend = ColorBackend.None;
            p.Reason = "源已可用 CICP 精确表达 → 不改像素，仅写 CICP";
        }
        else
        {
            // 本策略下的映射是"为达成可 CICP 表达"，不是用户目标空间；先置位再规划以跳过"等价即直通"以外的分支
            pol.TargetExplicit = true;
            var core = PlanCore(src, target, pol);
            core.DeclaredStrategy = declared;
            core.EffectiveStrategy = ColorStrategy.BakeCicpOnly;
            core.Override = ov;
            StripIcc(core);
            return core;
        }
        p.OutPrimaries = target.PrimariesToken;
        p.OutTransfer = target.TransferToken;
        p.OutMatrix = MatrixTokenFor(target.PrimariesToken);
        p.CicpUnavailable = CicpUnavailableReason.None;
        StripIcc(p);
        return p;
    }

    // ══════════════════════════ S4 手动 ═══════════════════════════════

    private static ColorTransformPlan PlanManual(ColorIntent it, PlanPolicy pol, ColorStrategy declared, OverrideReason ov)
    {
        var p = Fresh(declared);
        var caps = pol.Format;
        var m = it.Manual;
        if (it.Target == null && !(m?.HasAny ?? false))
            return Reject(p, "S4 手动：未提供任何目标色彩描述（或高级色彩未启用）",
                "选择目标色彩空间，或勾选高级色彩并给出 primaries/transfer 三元组");

        var target = it.Target ?? BuildManualTarget(m);
        if (target == null)
            return Reject(p, $"S4 手动：无法解析目标（space={m?.Space ?? "-"} p={m?.Primaries ?? "-"} t={m?.Transfer ?? "-"}）",
                "使用受支持的空间名或 CICP token");

        bool canName = CapsAcceptsCicp(caps, target);
        if (!canName && !caps.CanCarryArbitraryIcc)
            return Reject(p, $"S4 手动：{caps.Format} 既不能写该空间的 CICP 又不能携带任意 ICC"
                             + (caps.HasColorPath ? "" : "（该容器完全无色彩管理通路）"),
                $"改用 PNG/TIFF/WebP/JXL 以携带 ICC；若坚持 {caps.Format} 则目标只能取 sRGB（无标注）；或改用 S1 自动处理",
                FormatAlts("能力表 CanCarryArbitraryIcc ⇒ 可携带任意 ICC", "png", "tiff", "webp", "jxl"));

        // 用户担保：S4 允许在低置信度源上映射（历史缺陷：静默假定已被 Reject 消解，仍要求至少一个描述）
        if (it.Source == null)
            return Reject(p, "S4 手动：源色彩完全未知 → 无法确定映射起点", "为源指定色彩空间（ManualIntent.Space 之外的源侧设置）");

        pol.TargetExplicit = true;
        pol.ForceMapping = true;
        var core = PlanCore(it.Source, target, pol);
        core.DeclaredStrategy = declared;
        core.EffectiveStrategy = ColorStrategy.Manual;
        if (ov != OverrideReason.None) core.Override = ov;
        // 择一容器（如 JXL）：用户显式给了三元组 ⇒ CICP 优先；否则 ICC 优先。
        // ⚠️ 前提：容器真的能写 CICP——否则“CICP 优先”会先剥 ICC、再被能力规则剥掉 CICP，最经落成零标注（实测踩过）。
        if (!caps.CanHaveBothIccAndCicp)
        {
            bool cicpFirst = caps.CanCicp && ((m?.Primaries != null && m?.Transfer != null) || !core.AttachIcc);
            if (cicpFirst) { core.AttachIcc = false; core.IccPath = null; core.IccPrimaries = core.IccTrc = core.IccMatrix = null; }
            else { core.OutPrimaries = core.OutTransfer = core.OutMatrix = null; core.CicpUnavailable = CicpUnavailableReason.ContainerNonNormative; }
        }
        return core;
    }

    // ══════════════════════════ S1 推荐（无损阶梯）══════════════════════

    private static ColorTransformPlan PlanRecommended(ColorIntent it, PlanPolicy pol, ColorStrategy declared, OverrideReason ov)
    {
        var p = Fresh(declared);
        var src = it.Source;
        var caps = pol.Format;
        bool srcHdr = src?.Curve.Kind is TransferCurve.CurveKind.Pq or TransferCurve.CurveKind.Hlg;

        // 用户请求了 tonemap 但未给目标 ⇒ 目标取“源原色的 SDR 对应空间”。
        // 否则请求会被静默忽略（无目标 → Action=None → 不改像素），违反“不得静默降级”。
        if (pol.ToneMapRequested && srcHdr && it.Target == null)
        {
            var twin = SdrTwinOf(src!, caps);
            if (twin != null) it.Target = twin;
        }

        // ── HDR 源 + auto 目标 + 容器「能承载 HDR 但**当前位深**不够」⇒ 显式交回传统管线（2026-09-17）──
        // 为什么不能走下面的 HDR 守卫（Reject）：8bit PNG 是本工具的**合法**出口（GUI 默认位深就是 8），
        //   Reject 会让「HDR → PNG(8bit)」无路可走。
        // 为什么不让引擎接管 tonemap：引擎的 SDR 标定是 bt709/iec61966-2-1（sRGB 曲线），而 PNG 的
        //   SDR 出口语义（传统 tonemap 链末级 `zscale=p=bt709:t=bt709` ⇒ 编码器写 cICP bt709/bt709）
        //   是 **bt709/bt709** —— 两者不同，接管就会**改变交付**，而 :126-128 锁定的是 bt709/bt709。
        // 所以本格的正确结局是「显式交回 + 如实标注出口语义」：旧实现宣称"不改像素、沿用源描述"
        //   （OutPrimaries=bt2020/OutTransfer=smpte2084），交付却是 tonemap 后的 bt709/bt709。
        if (srcHdr == true && it.Target == null && !pol.HdrOutput && !pol.ToneMapRequested && caps.CanHdr)
            return PlanDelegatedHdrSdr(p, it, pol, declared, ov);

        // HDR 不被悄悄 SDR 化（规划 §2）
        if (srcHdr == true && !pol.HdrOutput && !pol.ToneMapRequested)
            return Reject(p, "HDR 源 → SDR 目标需显式请求色调映射（否则高光整体压暗）",
                "开启 tonemap（--color-tone-map / HDR→SDR 选项），或输出到支持 HDR 的容器（AVIF/JXL）",
                // ⚠ 同前：heif 无写入通路，不列为替代；Advice 文案同步去掉 HEIF（2026-09-16）。
                FormatAlts("能力表 CanHdr ⇒ 可原样表达 HDR，无需降 SDR", "avif", "jxl"));

        // ── 容器**根本没有**色彩管理通路（HasColorPath=false，如 GIF）：目标只能钉成 sRGB（2026-09-17）──
        // 产物注定无标注 ⇒ 查看器只能按**设备一致性惯例**把它当 sRGB 解释 ⇒ 唯一诚实的映射终点就是 sRGB。
        // 不钉的后果（实测）：`HDR 源 + gif + auto 目标` 会带着 `it.Target == null` 走到
        // `PlanCore`，再由 `EnsureAnnotationNotSilentlyMissing` 拒绝（"不产出无标注结果"）
        // —— 而**同一个**转换在不传 `--color-space` 时反而做得了（下方 `!caps.HasColorPath` 分支），
        // 自相矛盾；且 legacy 对同一输入正常产出 GIF（实测 src_hdr8 → 17662B / src_hdr → 21694B），
        // 引擎侧却硬失败（退出码 91）⇒ 这是"接入即回归"，不是策略收紧。
        // ⚠ 必须放在 HDR 守卫**之后**：**未**请求 tonemap 的 HDR → 无通路容器仍须 Reject
        //   （`ServiceProbe contract` 的 `HLG × gif ⇒ Reject` 锁着这条）。
        if (!caps.HasColorPath && it.Target == null && SrgbRef != null)
            it.Target = SrgbRef;

        // L1 携带优先（无损；范围由 CarryScope 单一开关决定，规划 §9）
        bool carryInScope = pol.CarryScope switch
        {
            CarryScope.None => false,
            CarryScope.Unnameable => src != null && !src.IsCicpNameable,
            _ => true,
        };
        if (!pol.TargetExplicit && carryInScope && src?.SourceIccPath != null && caps.CanCarryArbitraryIcc && srcHdr == false)
        {
            p.Action = ColorAction.CarryIcc;
            p.Backend = ColorBackend.None;
            p.AttachIcc = true;
            p.IccPath = src.SourceIccPath;
            p.Reason = $"L1 无损优先（carry-scope={pol.CarryScope}）：像素零改动，携带源 ICC";
            CarryCicpLabels(p, src!, caps);
            return p;
        }

        // 置信度闸门：低置信度（纯惯例假定）不允许改像素映射
        if (!pol.TargetExplicit && src != null && src.Confidence < EquivalenceTolerances.MinConfidenceForMapping)
        {
            p.Action = ColorAction.None;
            p.Backend = ColorBackend.None;
            p.UnlabeledAssumedSrgb = !caps.CanCicp;
            p.Reason = $"源描述置信度低（{src.Confidence}，按惯例假定）→ 不改像素；仅保证标注不缺位";
            p.Advice = "如需映射，请为源补 ICC 或改用 S4 手动指定（用户担保）";
            CarryCicpLabels(p, src, caps);
            return p;
        }

        // L2 超集归一（无损阶梯）：auto 目标、无法携带源 ICC、且该容器**无论何种方式**都无法表达源空间
        //   → 取“容器可表达的最窄超集”（包含源⇒零削色）；找不到则 Reject。
        // ⚠️ “能表达”不等于“能写 CICP”：PNG/JPEG/WebP/TIFF 无 CICP 通路但可携带/生成 ICC（实测踩过：混为一谈会误拒 sRGB→png）。
        bool canCarrySourceIcc = caps.CanCarryArbitraryIcc && src?.SourceIccPath != null;
        bool expressible = src != null && (CapsAcceptsCicp(caps, src)
            || ((caps.CanCarryArbitraryIcc || caps.CanGenerateIccFromCicp) && src.IsCicpNameable));
        if (it.Target == null && src != null && srcHdr == false && !canCarrySourceIcc && !expressible)
        {
            // 容器根本没有任何色彩管理通路（GIF/JXR/PPM）：唯一诚实的做法是映射到 sRGB 工作空间并无标注输出
            if (!caps.HasColorPath)
            {
                if (SrgbRef == null) return Reject(p, "内部错误：sRGB 参照不可用", "");
                var cg = PlanCore(src, SrgbRef, pol);
                cg.DeclaredStrategy = declared;
                cg.EffectiveStrategy = ColorStrategy.Recommended;
                cg.UnlabeledAssumedSrgb = true;
                cg.Reason = $"{caps.Format} 无任何色彩管理通路 → 映射到 sRGB 工作空间并无标注输出（设备一致性惯例）：" + cg.Reason;
                cg.Advice = $"若需保留 {src.Name} 的完整色彩，请改用 PNG/TIFF/WebP/JXL 携带 ICC";
                return cg;
            }
            var sup = NameableSupersetFor(src, caps);
            if (sup == null)
                return Reject(p, $"S1 推荐：{caps.Format} 既不能携带 {src.Name} 的 ICC，又不能表达该空间",
                    "改用 PNG/TIFF/WebP/JXL 携带 ICC；或接受归入可命名超集（需容器支持）",
                    FormatAlts("能力表 CanCarryArbitraryIcc ⇒ 可携带任意 ICC", "png", "tiff", "webp", "jxl"));
            var c2 = PlanCore(src, sup, pol);
            c2.DeclaredStrategy = declared;
            c2.EffectiveStrategy = ColorStrategy.Recommended;
            if (ov != OverrideReason.None) c2.Override = ov;
            c2.Reason = $"L2 超集归一（{src.Name} → {sup.Name}，包含源色域⇒零削色）：" + c2.Reason;
            return c2;
        }

        var core = PlanCore(src, it.Target, pol);
        core.DeclaredStrategy = declared;
        core.EffectiveStrategy = ColorStrategy.Recommended;
        if (ov != OverrideReason.None) core.Override = ov;
        if (pol.Strategy == ColorStrategy.BakeCicpOnly && !it.GainMapRequested) StripIcc(core);
        return core;
    }

    /// <summary>
    /// 兜底：任何**非 Reject** 行若最终既无 ICC 又无 CICP，只允许两种结局：
    /// ① 实际像素确实等价 sRGB，且（像素未被改动 **或** 容器**根本没有**色彩管理通路
    ///    —— 后者的"无标注"是物理上不可避免的）⇒ 显式记 <see cref="ColorTransformPlan.UnlabeledAssumedSrgb"/>；
    /// ② 否则 ⇒ Reject + 建议（绝不允许“静默产出无标注行”——这正是历史偏色根因）。
    /// </summary>
    private static void EnsureAnnotationNotSilentlyMissing(ColorTransformPlan p, PlanPolicy pol, ColorIntent it)
    {
        if (p.Action == ColorAction.Reject) return;
        if (p.AttachIcc || p.OutPrimaries != null || p.UnlabeledAssumedSrgb) return;

        var effective = p.Dst ?? p.Src;
        bool pixelsUntouched = p.Matrix == null && !p.NeedsCurveChange;
        // ⚠ 2026-09-17：容器**根本没有**色彩管理通路（`HasColorPath=false`，如 GIF）时，
        //   "无标注"是**物理上不可避免**的（没有 ICC 通路也没有 CICP 通路）⇒ 只要**目标确实等价 sRGB**
        //   （`S1` 已把这类容器的 auto 目标钉成 sRGB，见 `PlanRecommended` 的对应注释），
        //   允许**像素已改**的行记 `UnlabeledAssumedSrgb` —— 与下方 `!caps.HasColorPath` 分支的结局一致。
        //   不放松这一条的后果：`HDR/广色域 源 + gif + 显式 sRGB 目标` 被硬拒（退出码 91），
        //   而同一个转换不传 `--color-space` 时反而做得了 ⇒ 自相矛盾。
        //   对**有**色彩通路的容器本条件恒不成立（`!HasColorPath` 为 false）⇒ 既有行为零变化。
        if ((pixelsUntouched || !pol.Format.HasColorPath) && IsSrgbEquivalent(effective) && !pol.HdrOutput)
        {
            p.UnlabeledAssumedSrgb = true;
            p.Annot = AnnotChoice.UnlabeledAssumedSrgb;
            p.Reason += $" 无标注可用（{pol.Format.Format} 无色彩管理通路且像素等价 sRGB）→ 记 UnlabeledAssumedSrgb";
            return;
        }
        p.Action = ColorAction.Reject;
        p.Backend = ColorBackend.None;
        p.Matrix = null;
        p.NeedsCurveChange = false;
        p.Annot = AnnotChoice.None;
        p.Reason = $"{pol.Format.Format} 既不能携带 ICC 又不能表达该空间的 CICP → 不产出无标注结果";
        p.Advice = "改用 PNG/TIFF/WebP/JXL（可携带任意 ICC），或接受归入可命名超集（sRGB/Display P3/BT.2020）";
    }

    // ══════════════════════════ 共用小工具 ════════════════════════════

    private static ColorTransformPlan Fresh(ColorStrategy declared) => new()
    {
        DeclaredStrategy = declared,
        EffectiveStrategy = declared,
    };

    /// <summary>
    /// 拒绝助手：把计划置为 <see cref="ColorAction.Reject"/> 并写 <c>Reason</c>/<c>Advice</c>
    /// （文案**逐字不改**，既有门禁/日志在匹配它们）。
    /// <para>
    /// <paramref name="alternatives"/> 是**可选**的结构化替代路径：**只在调用点本来就知道替代时**传入，
    /// 拿不准就传 null（投影点不做推断）。这里只把它**暂存**进 <see cref="ColorTransformPlan.Verdict"/>，
    /// 由 <see cref="ProjectVerdict"/> 统一成最终裁决（Kind/Action/Reason/Advice 一律以计划最终状态为准）。
    /// </para>
    /// </summary>
    private static ColorTransformPlan Reject(ColorTransformPlan p, string reason, string advice,
        IReadOnlyList<ColorAlternative>? alternatives = null)
    {
        p.Action = ColorAction.Reject;
        p.Backend = ColorBackend.None;
        p.Reason = reason;
        p.Advice = advice;
        p.Matrix = null;
        p.NeedsCurveChange = false;
        p.AttachIcc = false;
        p.IccPath = null;
        if (alternatives is { Count: > 0 })
            p.Verdict = new ColorVerdict { Alternatives = alternatives };
        return p;
    }

    /// <summary>
    /// 把一组容器名转成 <see cref="AlternativeKind.Format"/> 替代（共享同一条 <paramref name="why"/>）。
    /// <para>⚠ 容器名必须是**本项目能力表已登记**的格式；理由由调用点给出，禁止在此编造。</para>
    /// </summary>
    private static ColorAlternative[] FormatAlts(string why, params string[] formats)
    {
        var alts = new ColorAlternative[formats.Length];
        for (int i = 0; i < formats.Length; i++)
            alts[i] = new ColorAlternative(AlternativeKind.Format, formats[i], why);
        return alts;
    }

    private static ColorSpaceDescriptor? BuildManualTarget(ManualIntent? m)
    {
        if (m == null || !m.FieldsFromUi) return null;
        if (m.Primaries != null || m.Transfer != null)
        {
            var d = ColorSpaceDescriptor.FromCicp(m.Primaries ?? "bt709", m.Transfer ?? "iec61966-2-1", m.Space);
            if (d != null) { d.Confidence = DescriptorConfidence.NamedIcc; return d; }
        }
        return ColorSpaceDescriptor.FromNamedSpace(m.Space);
    }

    /// <summary>源已可 CICP 表达时沿用其标签（携带策略下的"标注继承"，同样不改像素）。</summary>
    private static void CarryCicpLabels(ColorTransformPlan p, ColorSpaceDescriptor src, FormatColorCaps caps)
    {
        if (!CapsAcceptsCicp(caps, src)) { p.CicpUnavailable = caps.CanCicp ? CicpUnavailableReason.SpaceNotNameable : caps.NoCicpReason; return; }
        p.OutPrimaries = src.PrimariesToken;
        p.OutTransfer = src.TransferToken;
        p.OutMatrix = MatrixTokenFor(src.PrimariesToken);
    }

    /// <summary>剥离一切 ICC 标注（S3 契约）。</summary>
    private static void StripIcc(ColorTransformPlan p)
    {
        p.AttachIcc = false;
        p.IccPath = null;
        p.IccPrimaries = p.IccTrc = p.IccMatrix = null;
        p.IccFromGenerator = false;
    }

    /// <summary>是否为隐含 sRGB（等价于 bt709 + iec61966-2-1）。</summary>
    public static bool IsSrgbEquivalent(ColorSpaceDescriptor? src)
        => src != null && SrgbRef != null && src.Equivalent(SrgbRef);

    /// <summary>
    /// 取"该容器可用 CICP 表达的最窄超集"（S3 用）：sRGB → Display P3 → BT.2020，HDR 保曲线。
    /// 找不到（如 JXL 无 SDR-BT.2020 枚举而源是 Adobe RGB）⇒ null，调用方 Reject。
    /// </summary>
    public static ColorSpaceDescriptor? NameableSupersetFor(ColorSpaceDescriptor? src, FormatColorCaps caps)
    {
        if (src == null || !src.IsMappable) return null;
        bool hdr = src.Curve.Kind is TransferCurve.CurveKind.Pq or TransferCurve.CurveKind.Hlg;
        var cands = hdr
            ? new[] { ("bt2020", src.Curve.CicpToken) }
            : new (string, string?)[] { ("bt709", "iec61966-2-1"), ("smpte432", "iec61966-2-1"),
                                        ("bt709", "bt709"), ("smpte432", "bt709"), ("bt2020", "bt709") };
        foreach (var (prim, trc) in cands)
        {
            var d = ColorSpaceDescriptor.FromCicp(prim, trc, $"CICP:{prim}/{trc}");
            if (d == null || !CapsAcceptsCicp(caps, d)) continue;
            if (ContainsGamut(src, d)) return d;
        }
        return null;
    }

    /// <summary>
    /// 源色域是否被目标包含：几何凸包判据（规划 §5′.3）；无法判定时保守当作"不包含"（宁可不选该超集）。
    /// </summary>
    private static bool ContainsGamut(ColorSpaceDescriptor src, ColorSpaceDescriptor dst)
        => ColorSpaceDescriptor.ContainsGamut(src, dst) ?? false;

    /// <summary>
    /// 源原色的 SDR 对应空间（**原色统一降到 bt709**，传递换成 SDR 标定）。
    /// 用于“用户请求了 tonemap 但未指定目标”的情形，避免该请求被静默忽略。
    /// <para>
    /// **原色为什么是 bt709**（2026-09-17 用户决定）：HDR 源 → SDR 容器的出口原色语义一律取 bt709，
    /// 三个容器（png/jpg/webp）必须一致。bt709 与 sRGB 的原色色度相同 ⇒ 这一改动等于**恢复 09-16
    /// 之前的契约**（当时 jpg 的 ICC 量出来就是 sRGB 红 x=0.640）；旧实现保留源原色（BT.2020）
    /// 会让同一 HDR 源在三容器上得到两套原色语义（png8 走传统管线得 bt709，jpg/webp 得 bt2020）。
    /// </para>
    /// <para>
    /// **传递曲线为什么不跟着原色一起改成 bt709**（仍优先 <c>iec61966-2-1</c>，三条理由，逐条有出处）：
    /// <list type="number">
    ///   <item><b>仓库惯例</b>：<see cref="NameableSupersetFor"/> 的 SDR 候选表把
    ///         <c>("bt709","iec61966-2-1")</c> 排在第一（其次才是 <c>("bt709","bt709")</c>）；</item>
    ///   <item><b>容器惯例</b>：<c>FfmpegCommandBuilder.GetFormatColorCapabilities</c> 对 jpg/jpeg/webp/tiff
    ///         的 <c>capTrc</c> 恒为 <c>iec61966-2-1</c>，而传统 tonemap 链的出口
    ///         <c>tmDstT = capT ?? "bt709"</c> 用的正是它（png 无钳制才落到 bt709）；</item>
    ///   <item><b>语义必要</b>：只有 iec61966-2-1 让出口**等价 sRGB**，从而兑现
    ///         “未标注即 sRGB”容器的无 ICC 直通契约（<c>verify-decision-delivery</c> :159 锁定
    ///         hdr/webp **不带** ICC）。若改用 bt709 曲线，出口就不再等价 sRGB ⇒ webp 会被迫
    ///         后置嵌入一张重复的 ICC，与 09-16 契约冲突。</item>
    /// </list>
    /// ⚠ 另有独立佐证：<c>ColorSpaceRegistry</c> 记录 zimg 把 <c>t=bt709</c> 实现为纯 γ2.4、
    /// 与定义式分歧（见 <c>PlanCore</c> 的 <c>zimgDivergent</c> 黑名单）⇒ 引擎侧更不该拿它当 SDR 标定。
    /// </para>
    /// </summary>
    private static ColorSpaceDescriptor? SdrTwinOf(ColorSpaceDescriptor src, FormatColorCaps caps)
    {
        if (src.PrimariesToken == null) return null;
        foreach (var trc in new[] { "iec61966-2-1", "bt709" })
        {
            var d = ColorSpaceDescriptor.FromCicp("bt709", trc, $"{src.Name} -> SDR(bt709/{trc})");
            if (d != null && (CapsAcceptsCicp(caps, d) || caps.CanCarryArbitraryIcc || caps.CanGenerateIccFromCicp))
                return d;
        }
        return null;
    }

    /// <summary>
    /// 容器在**本次目标位深**下能否承载 HDR。判据 = 能力表 <see cref="FormatColorCaps.CanHdr"/>
    /// 叠加它的位深前提（PNG/APNG 的 PQ/HLG 只能由 &gt;8bit 承载；AVIF/JXL 原生 HDR 不受此限）。
    /// <para>
    /// ⚠⚠ **本判据的语义是「容器能否承载 HDR *标注*」（PQ/HLG 的 CICP 或 ICC）**，
    /// 因此**不能**把 GainMap 算进来 —— 2026-09-20 实测：把 JPEG+GainMap 判为 true 会让
    /// 「HDR 源 → JPEG + `--jpeg-gain-map`」**保留 bt2020/smpte2084 标注**，而 JPEG **既无 CICP 通路、
    /// ICC 也表达不了 PQ** ⇒ 引擎 `Reject`（「不产出无标注结果」）⇒
    /// 三格 `*-hdr-jpggm` 从 **63/0 掉到 58/5**。
    /// </para>
    /// <para>
    /// GainMap 出口的 HDR 是**另一条机制**：底图 tonemap 成 SDR + **增益层**承载 HDR
    /// （见 <c>ColorStrategyPlanning</c> 的 S2 覆盖①「GainMap 要求底图映射」）
    /// ⇒ 它**不需要**、也**不应该**让本判据翻真。
    /// 「按 GainMap 开关判断目标能否表达 HDR」是**另一个问题**（RAW 源色域的默认取值）
    /// ⇒ 那个场景用 <see cref="FormatColorCaps.CanHdrWith"/>，**不要**混进这里。
    /// </para>
    /// </summary>
    private static bool CanCarryHdrAt(FormatColorCaps caps, int bitDepth)
        => caps.CanHdr && (!caps.HdrRequiresHighBitDepth || bitDepth > 8);

    /// <summary>
    /// HDR 源 + auto 目标 + 容器在本位深装不下 HDR ⇒ **显式交回传统管线**执行 tonemap 与标注。
    /// <para>
    /// 计划只做两件事：
    /// <list type="number">
    ///   <item><see cref="ColorAction.None"/> —— 明确"引擎本次不做像素变换"（<c>QueueProcessor</c>
    ///         据此交回传统管线重编码）；</item>
    ///   <item>声明**出口语义**（bt709/bt709），使其与交付**一致**。该语义来自容器的 SDR 标定：
    ///         PNG/APNG 无 SDR 钳制（<c>GetFormatColorCapabilities</c> 的 capTrc=null），
    ///         传统 tonemap 链的末级即 <c>zscale=p=bt709:t=bt709</c>，编码器据此写 cICP；
    ///         <c>verify-decision-delivery</c> :126-128 正是按 bt709/bt709 锁定的。</item>
    /// </list>
    /// </para>
    /// <para>
    /// ⚠ **绝不再写源 HDR 标签**（旧实现写 bt2020/smpte2084，而交付是 bt709/bt709）。
    /// 结构化替代路径（<see cref="AlternativeKind.Strategy"/> → legacy）随裁决一起给出，
    /// 让"谁执行"可审计，而不是靠日志文案。
    /// </para>
    /// </summary>
    private static ColorTransformPlan PlanDelegatedHdrSdr(ColorTransformPlan p, ColorIntent it, PlanPolicy pol,
        ColorStrategy declared, OverrideReason ov)
    {
        var caps = pol.Format;
        p.Src = it.Source;
        p.Action = ColorAction.None;
        p.Backend = ColorBackend.None;
        p.DeclaredStrategy = declared;
        p.EffectiveStrategy = declared;
        if (ov != OverrideReason.None) p.Override = ov;
        p.OutPrimaries = "bt709";
        p.OutTransfer = "bt709";
        p.OutMatrix = "bt709";
        p.AttachIcc = false;
        p.Reason = $"HDR 源（{it.Source!.Curve.Name}）+ auto 目标：{caps.Format} 的 HDR 承载能力要求位深 >8"
                 + $"（能力表 HdrRequiresHighBitDepth），而本次目标位深 {pol.TargetBitDepth} 装不下"
                 + " ⇒ 引擎不做像素变换；tonemap 与标注由传统管线裁决并执行，"
                 + "出口语义 = 该容器的 SDR 标定 bt709/bt709（本计划**不**宣称保持源 HDR 标签）";
        p.Advice = "如需由引擎执行 HDR→SDR：显式指定 SDR 目标（--color-space sRGB），或把输出位深提到 >8";
        p.Verdict = new ColorVerdict
        {
            Alternatives = new[]
            {
                new ColorAlternative(AlternativeKind.Strategy, "legacy",
                    $"位深 ≤8 的 {caps.Format} 装不下 HDR ⇒ 本出口的 tonemap 归传统管线"
                    + "（引擎的 SDR 标定为 bt709/iec61966-2-1，与 PNG 的 SDR 出口语义 bt709/bt709 不同，"
                    + "接管会改变交付）"),
            },
        };
        return p;
    }

    /// <summary>按容器能力裁剪标注（ICC/CICP 是否可共存、无 CICP 时记原因）。</summary>
    private static void EnforceContainerAnnotationRules(ColorTransformPlan p, FormatColorCaps caps)
    {
        if (p.Action == ColorAction.Reject) return;
        bool icc = p.AttachIcc, cicp = p.OutPrimaries != null;

        if (cicp && !caps.CanCicp)
        {
            p.OutPrimaries = p.OutTransfer = p.OutMatrix = null;
            p.CicpUnavailable = caps.NoCicpReason;
            cicp = false;
        }
        if (icc && cicp && !caps.CanHaveBothIccAndCicp)
        {
            // 择一：携带型 ICC 优先（描述更完备）；纯生成 ICC 且源已命名则保留 CICP
            if (p.IccPath != null) { p.OutPrimaries = p.OutTransfer = p.OutMatrix = null; p.CicpUnavailable = CicpUnavailableReason.ContainerNonNormative; cicp = false; }
            else { StripIcc(p); icc = false; }
            p.Reason += " 标注=择一（容器不能同时携带 ICC 与 CICP）";
        }
        if (!icc && !cicp && !p.UnlabeledAssumedSrgb && p.Action != ColorAction.None)
            p.CicpUnavailable = p.CicpUnavailable == CicpUnavailableReason.None
                ? CicpUnavailableReason.SpaceNotNameable : p.CicpUnavailable;
        if (p.IccPath == null && p.IccPrimaries != null) p.IccFromGenerator = true;    // 由 iccgen/命名生成
    }

    private static AnnotChoice ClassifyAnnot(ColorTransformPlan p)
    {
        if (p.Action == ColorAction.Reject) return AnnotChoice.None;
        if (p.UnlabeledAssumedSrgb) return AnnotChoice.UnlabeledAssumedSrgb;
        bool icc = p.AttachIcc, cicp = p.OutPrimaries != null;
        return (icc, cicp) switch
        {
            (true, true) => AnnotChoice.IccAndCicp,
            (true, false) => AnnotChoice.IccOnly,
            (false, true) => AnnotChoice.CicpOnly,
            _ => AnnotChoice.None,
        };
    }

    private static ColorLoss ClassifyColorLoss(ColorTransformPlan p, PlanPolicy pol, ColorIntent it)
    {
        if (p.Action is ColorAction.None or ColorAction.CarryIcc or ColorAction.Reject) return ColorLoss.None;
        if (p.Matrix == null && !p.NeedsCurveChange && p.Tone == null) return ColorLoss.None;   // 仅改标注
        if (p.Tone is { Active: true }) return ColorLoss.ToneMapped;                            // 有意的亮度重映射
        if (p.Src?.Curve.Kind is TransferCurve.CurveKind.Pq or TransferCurve.CurveKind.Hlg && !pol.HdrOutput)
            return it.ToneMapRequested ? ColorLoss.ToneMapped : ColorLoss.Clipping;
        if (p.GamutOutsideDestination)
            // ⚠ 判据取 **p.GamutMap**（PlanCore 的单点决策）而不是 pol.AllowGamutCompression：
            //   后者只是"用户允许"，前者才是"本次真的会压"。二者在「目标亮度系数取不到」时会分叉
            //   （PlanCore 已明确退回硬裁切并把原因写进 Reason），此时若仍报 GamutCompression，
            //   就会与 CollectDegradations 依 p.GamutMap 登记的 GamutClipped **自相矛盾**
            //   （同一份裁决里既说"已压缩"又说"被裁切"）。
            return p.GamutMap ? ColorLoss.GamutCompression : ColorLoss.Clipping;
        return ColorLoss.RoundTripQuantization;
    }
}
