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
        // ── 显式输出矩阵模式（C-PLAN-EXT@1 / C-CAPS@1 / C-REPORT@1，color-matrix-options 计划）：
        //    **唯一判定与覆盖点**。auto（null）直接早退 ⇒ 全链路与改造前逐字节同路径（CM-REQ-08）；
        //    显式值在 MatrixTokenFor 三处推导（ColorTransformPlan 的 SetLabelsFrom / 本文件的
        //    PlanCicpOnly / CarryCicpLabels）完成之后覆盖 p.OutMatrix 并置 OutMatrixVerdict。
        //    放在 Verdict 投影之前：Rejected 决议（exit 91、零产物、出口不被调用）能如实反映进最终裁决。
        ApplyExplicitOutMatrix(p, it, pol);
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
    /// GamutOutsideDestination/UnlabeledAssumedSrgb/AttachIcc、<see cref="PlanPolicy.Format"/> 的能力表
    /// 与 `PlanPolicy.TargetBitDepth`、<see cref="ColorIntent.Source"/> 的 <c>SourceIccPath</c>），
    /// **不新算任何东西**；
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

        // ①b **交付档 ≠ 目标档**（升位 / 降位两个方向）：`PlanWithPolicy` 末尾的 OutBitDepth 式子
        //    值域只有 {8,16}，而 RGB 原生出口交付的像素格式同样是两档（rgb24 / rgb48le）
        //    ⇒ 目标 10/12 必然被取到上档 16（升）、目标 32 必然被取到下档 16（降）。
        //    两种都不是用户要的那个数，都不许静默（不登记就等于"看起来按 12bit 出了"）。
        //    ⚠ **方向必须分开登记**：升到 16 不丢精度（只是产物变大），用 `BitDepthReduced` 播它
        //      就是拿"降级"这个词骗日志；反之同理 ⇒ `>` 走 `BitDepthRaised`、`<` 走 `BitDepthReduced`。
        //    ⚠ 与上面 ① **不重复触发**：① 要求 `OutBitDepth == 8`，而 RGB 原生出口给 8 的前提就是
        //      `TargetBitDepth <= 8` ⇒ 那一格永远不满足此处的 `!=`。
        //    ⚠ 三条前置，缺一条就假报：
        //      · `pol.Format.RgbNative` —— 只有 RGB 原生容器的交付档由本层说得出（`RawColorPipeline`
        //        直接吐 rgb24/rgb48le）。YUV 容器（jpg/webp/avif）的 16 只是**中间件**精度，真实出口由
        //        `ImageEncoderArgs`（AVIF 见 `AvifEffectiveBitDepth`）决定 ⇒ 拿它比对会把
        //        "10bit 的 AVIF"说成"升到 12bit"（假播报）；
        //      · `outlet <= MaxBitDepth` —— 出口必须**真的承载得了**这一档：gif 是 RgbNative 但上限 8，
        //        给它报"升到 16"是假的（调色板量化后仍是 8bit）；
        //      · 交付档**只在能断定的格上**才与目标比对（断法见下面 `outlet` 的取法）：断不出来的
        //        格子按"不播报"处理 —— 猜出来的交付档就是新的假播报源。
        //
        // ⚠⚠⚠ **2026-09-26（任务 #35 实测轮）本段的第 4 条前置 —— 也是"YUV 容器零播报"的真正根因**：
        //    把上面这条扩到非 RgbNative 容器**不足以**兑现播报，因为传进本层的
        //    `pol.TargetBitDepth` **已经不是用户请求的那个数**了。实测（本机 Release exe，
        //    `--headless --log-level Debug`，产物与命令行都记在 tests/output/t35/）：
        //      · `--bit-depth 10 -f webp` → 本层看到 `TargetBitDepth=8`，出口 `-pix_fmt yuv420p`（8bit）；
        //      · `--bit-depth 16 -f avif` → 本层看到 `TargetBitDepth=10`，出口 `-pix_fmt yuv420p10le`；
        //        （⚠ 那两个数是 **#35 当时**的读数；#36 修 `AvifMaxBitDepthForEncoder` 后同一格的
        //         钳制档变成 12 —— 见 `ImageEncoderArgs` 里那条"空编码器名 ⇒ 实跑 libaom-av1 ⇒ 12"。）
        //      · `--bit-depth 16 -f avif --color-space "Display P3"` → 本层看到 `TargetBitDepth=8`
        //        （`GetFormatColorCapabilities` 当时那条"avif + P3 + 高位深 ⇒ 回退 8bit"保守支）。
        //        ⚠ 该特例已于**同日任务 #39 实测否证并删除** ⇒ 这一格现在的正确结局是
        //        `TargetBitDepth=12`（libaom 上限）并照常播报 16→12；两条都由
        //        `verify-decision-delivery` ⑯/⑰ 钉住，别拿本行旧读数当现状。）
        //    请求档在**进入本层之前**被两处钳掉，修复前两处都不留痕：
        //      ① `FfmpegCommandBuilder.Decision.cs`（现 :147-156，引文为**修复前**原文）
        //         `if (options.BitDepth.HasValue && options.BitDepth.Value > capBd) options.BitDepth = capBd;`
        //         —— 直接**改写调用方的 options**（capBd 来自 `GetFormatColorCapabilities`：
        //         webp/jpg=8、avif=`AvifMaxBitDepth`⇒**#35 当时**未显式选编码器是 10（#36 已改为 12）、其余 16）；
        //      ② `ColorIntentFactory.cs:145`
        //         `TargetBitDepth = Math.Min(o.BitDepth ?? (meta.bitDepth > 8 ? meta.bitDepth : 8), caps.MaxBitDepth)`
        //         —— 再按能力表 `MaxBitDepth` 钳一次（webp=8 / avif=12 / jpg=8）。
        //    ⇒ 到本层手上时 `TargetBitDepth <= caps.MaxBitDepth` **恒成立**，于是"按能力表算出口档"
        //      无论怎么写都只能是 `outlet = min(TargetBitDepth, 上限) == TargetBitDepth` ⇒ 本段
        //      在非 RgbNative 分支上**永不触发**。这不是判据太松，而是**证据已经被上面两处吃掉了**。
        //    ⇒ **修法**（2026-09-26 实施轮）：不放开本段的判据（放开了也仍是假绿，因为事实不在本层），
        //      而是把"用户请求档"这一事实**穿过那两处钳制带到本层**：
        //        · `Decision.cs` 钳制前先把被钳掉的那一档记进 `FfmpegOptions.BitDepthRequested`
        //          （只记**更深**的一次 —— 同一 options 重入时第二次读到的已是出口档）；
        //        · `ColorIntentFactory` 装配成 <see cref="ColorIntent.RequestedBitDepth"/>（= 请求档，
        //          与 `TargetBitDepth` = 交付档 刻意分成两个字段）；
        //        · 本段下方新增的 **非 RgbNative 分支**据此播报"请求 10 ⇒ 交付 8"。
        //      ⇒ 已实测：`verify-color-strategy.ps1` ③f 的 F3（webp 10）**由故意留红转为绿**，
        //        整段 71/0（读数出处：`tests/output/_gate_script_verify-color-strategy.ps1.txt`
        //        2026-09-26 16:05:39 那轮，被测 DLL 16:00:17 构建）。
        //      ⚠ **尚未复跑**：网格 §2a 那 71 格（webp 54 / avif 17）"真静默"是否随之清零 ——
        //        预期清零，但截至本次改动**没有**实测数；复跑产物落在 `tests/output/t35/grid-t35fix*.csv`，
        //        跑完把格数填回这里（不许拿"应该"当读数）。
        //    ⚠ 既有 RgbNative 分支的行为与文案**一字未动**（png/tiff/jxl 的 10/12 ⇒ `BitDepthRaised`
        //      仍照常播报，实测见 ③f 的 jxl 格）。
        if (pol.Format.RgbNative)
        {
            // 交付位深从哪儿读，取决于本次产物由谁编码（`QueueProcessor` 在 Action=None/CarryIcc 时
            // 把重编码交给 ffmpeg，那一带的 `OutBitDepth` **不代表交付**）：
            //   · Map 支 —— 引擎出口真驱动，直接读 `p.OutBitDepth`（这是本层唯一的交付事实源）；
            //   · 直通/携带支 —— 由 ffmpeg 侧按 `EffectiveBitDepth → MapPixFmt` 取档，只有
            //     `MapPixFmt` 明写着「bd<=8 ? rgb24 : rgb48le」的这四个容器能断定是两档；
            //     其余（tif/jxr/gif/ppm 走的是别的分支）**保守返回目标本身 ⇒ 不播报**：
            //     本层的契约是"只播能证实的"，猜出来的交付档就是新的假播报源。
            int outlet = p.Action == ColorAction.Map ? p.OutBitDepth
                : pol.Format.Format is "png" or "apng" or "tiff" or "jxl"
                    ? (pol.TargetBitDepth <= 8 ? 8 : 16)
                    : pol.TargetBitDepth;
            // `TargetBitDepth >= 8` 不是防呆而是**去重**：低于 8 的目标（只有预设能塞进来，CLI 已拒）
            // 必然被抬到 8 ⇒ 那一格上面 ① 已经按"容器上限降为 8bit"播过，再播一条"升为 8bit"就是自相矛盾。
            if (outlet != pol.TargetBitDepth && pol.TargetBitDepth >= 8 && outlet <= pol.Format.MaxBitDepth)
            {
                bool up = outlet > pol.TargetBitDepth;
                degs.Add(new ColorDegradation(
                    up ? DegradationCodes.BitDepthRaised : DegradationCodes.BitDepthReduced,
                    (up
                        ? $"出口位深升为 {outlet}bit（本次目标 {pol.TargetBitDepth}bit）⇒ **升位、非降级**：像素精度不损失，代价是产物更大。原因="
                        : $"出口位深降为 {outlet}bit（本次目标 {pol.TargetBitDepth}bit）⇒ 高于出口档的精度不保留。原因=")
                    + (up && pol.TargetBitDepth <= 8 && p.Requires16BitIntermediate
                        ? "目标虽是 8bit，但本次变换需 16bit 中间精度（HDR 曲线/高位深目标）⇒ 出口按 16bit 交付"
                        : $"{pol.Format.Format} 出口的 RGB 原生像素格式只有 8/16 两档（rgb24 / rgb48le）⇒ 目标 {pol.TargetBitDepth}bit 无对应出口档，被取到 {outlet}bit")));
            }
        }
        else if (it.RequestedBitDepth is int req && req > pol.TargetBitDepth
                 && !(p.Action == ColorAction.Map && p.OutBitDepth == 8))
        {
            // **非 RGB 原生出口**（webp/jpg/jpegli/avif/heic…）：交付档不是本层算出来的，而是
            // `Decision` 的 capBd 与 `ColorIntentFactory` 的能力表各钳一次之后的 `pol.TargetBitDepth` ——
            // 而那条链的出口像素格式正是由它经 `EffectiveBitDepth → MapPixFmt` 取档 ⇒ **它等于实际交付档**
            //（不是猜；反证由网格 §2a 拿产物 pix_fmt 实测对账，③f 的 F3 拿 webp 真格子对账）。
            // ⇒ 只要"用户请求 > 交付"就是**真实降位**，不播就是静默降级（任务 #35 的 71 格）。
            // ⚠ 与上面 ① **去重**：`Map` 且出口 8bit 时 ① 已按"容器上限降为 8bit"播过，再播一条就自相矛盾。
            // ⚠ 此分支**不做升位**：钳制只向下，"交付 > 请求"在这里不可能成立，写出了就是假播报。
            degs.Add(new ColorDegradation(DegradationCodes.BitDepthReduced,
                $"出口位深降为 {pol.TargetBitDepth}bit（本次目标 {req}bit）⇒ 高于出口档的精度不保留。原因="
                + $"{pol.Format.Format} 出口由编码器取档，本工具对该容器实测可交付的上限 = {pol.TargetBitDepth}bit"));
        }

        // ② 源色域超出目标：**硬裁切**（GamutClipped）与 **GMO 压缩**（GamutCompressed）是两种不同的
        //    「已发生损失」，必须分开登记：
        //    · 映射生效时再报 GamutClipped（"被裁切"）与"已压缩"自相矛盾 —— 裁切根本没发生；
        //    · 两者都不登记则更糟：一次真实的去饱和会被裁决报成 Proceed（无任何损失）。
        if (p.GamutOutsideDestination)
        {
            // ⚠ 2026-11-10（R4 占位双写修复）：手动目标描述符的 Name 是 ColorIntentFactory 的
            //   DstLabel 占位「目标」（ColorIntentFactory.cs:211 `FromCicp(tp, tt, DstLabel)`），
            //   直拼模板得到「源色域超出目标 目标」双写（24 格 A1/A3/C10a/C10b/F17 实测）；
            //   对照 D13/E15 的自动推导目标带实名（"SDR(bt709/iec61966-2-1)"）。同模板两种填充
            //   状态就是占位泄漏。修复：占位/空名回退 CICP token 对（如 "bt2020/smpte2084"），
            //   连 token 都没有时**降级为不带名**（单次出现），绝不再输出占位字面量或 "-"。
            var dstLabel = DstGamutLabelOf(p);
            var dstSeg = string.IsNullOrEmpty(dstLabel) ? "" : $" {dstLabel}";
            degs.Add(p.GamutMap
                ? new ColorDegradation(DegradationCodes.GamutCompressed,
                    $"源色域超出目标{dstSeg} ⇒ 已按 GMO 压缩（向同亮度灰混合去饱和，非硬裁切；"
                    + $"ColorLoss={p.ColorLoss}）")
                : new ColorDegradation(DegradationCodes.GamutClipped,
                    $"源色域超出目标{dstSeg} ⇒ 越界颜色被裁切或压缩（ColorLoss={p.ColorLoss}）"));
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

    /// <summary>
    /// 色域降级播报里的目标描述名（R4 占位双写修复的落点函数）。
    /// 正常实名（自动推导目标，如 "SDR(bt709/iec61966-2-1)"）原样返回；
    /// 手动目标的占位名（ColorIntentFactory DstLabel「目标」）或空名回退 CICP token 对
    /// （如 "bt2020/smpte2084"）；连 token 都没有时返回空串（调用方降级为不带名）。
    /// </summary>
    private static string DstGamutLabelOf(ColorTransformPlan p)
    {
        var name = p.Dst?.Name;
        if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, "目标", StringComparison.Ordinal))
            return name;
        var pt = p.Dst?.PrimariesToken;
        var tt = p.Dst?.TransferToken;
        return !string.IsNullOrWhiteSpace(pt) && !string.IsNullOrWhiteSpace(tt) ? $"{pt}/{tt}" : "";
    }

    /// <summary>策略判定主体（<see cref="Plan(ColorIntent)"/> 的包体；返回值会被补上 GainMap 位）。</summary>
    private static ColorTransformPlan PlanWithPolicy(ColorIntent it, PlanPolicy pol)
    {
        // GainMap 是“输出结构”能力（ISO 21496-1 / XMP+MPF），由 JPEG 族（APP2/XMP+MPF）、AVIF（tmap）与 JXL（jhgm）承载。
        // 必须在契约层拦住：UI 的 IsVisible 不是门禁，CLI/预设能绕过它；
        // 否则会变成“用户以为有 HDR 增益图，实际只得到被压暗的 SDR”——静默降级。
        if (it.GainMapRequested && !pol.Format.SupportsGainMap)
        {
            var pg = Fresh(it.Strategy);
            return Reject(pg, $"增益图（GainMap）不适用于 {pol.Format.Format}：它依赖 JPEG 的 APP2/XMP+MPF、AVIF 的 ISO-BMFF tmap 派生项或 JXL 的 jhgm box",
                "要保留 HDR：输出改用 AVIF/JXL（原生存 PQ/HLG，或其增益图承载 tmap/jhgm）；或把目标改为 JPEG 并开启增益图",
                new[]
                {
                    new ColorAlternative(AlternativeKind.Format, "jpg",
                        "JPEG 族、AVIF 与 JXL 可承载 GainMap（APP2/XMP+MPF、tmap 或 jhgm；能力表 SupportsGainMap）"),
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
        // ⚠ 2026-11-09（D4 配套第四处）：线性 HDR 也是 HDR 传递 —— 判据不认 linear 会让装配层
        //   已经接受的 ToneMapRequested（自动 hable 兜底 / 显式 --color-tone-map）**静默无效**：
        //   exit 90 消了、Map 照跑，但内核收不到 ToneMapParams ⇒ 交付=纯 OETF 编码，日志无任何
        //   "[tonemap" 行。2026-11-09 实测（OnePlus 样本）：修复前 jpg 三臂（默认/强制 sRGB/显式
        //   hable）读数逐值一致 + 全程 0 条 tonemap 行 = 挂载点拦截的指纹；放行后三臂仍同值
        //   （headroom 1.14 下 hable ≈ OETF+clip），但日志出现 [tonemap: … 峰值来源=Ultra HDR
        //   增益图解码实测] ⇒ 选项兑现且播报在。峰值证据与三处 srcHdr 同形（it.HdrPeakNits）。
        if (it.ToneMapRequested && it.ToneMap != ToneMapMode.None && p.Action == ColorAction.Map
            && p.Src != null && (p.Src.Curve.Kind is TransferCurve.CurveKind.Pq or TransferCurve.CurveKind.Hlg
                || (p.Src.Curve.Kind is TransferCurve.CurveKind.Linear
                    && it.HdrPeakNits > TransferCurve.SdrWhiteNits)))
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
            // ⚠ 2026-11-09（R1 HLG 文案修正，一行级）：旧尾句"如需按实际显示峰值，用 --color-hdr-peak
            //   指定 L_W"指向一根**没接线**的旋钮——OOTF 的 α 读的是描述符名义峰值
            //   （p.Src.NitsOfLinearOne，BT.2100 名义 1000nit），--color-hdr-peak 装配进
            //   it.HdrPeakNits 后只被 ToneMapParams（:342）消费、**改不了 OOTF 的 α**。
            //   按"承诺必须兑现"口径改为如实说明现状（不给虚假指引）。
            if (p.Src.Curve.Kind == TransferCurve.CurveKind.Hlg)
                p.Advice = $"HLG 按 BT.2100-2 Table 5 OOTF 处理（α=L_W={p.Src.NitsOfLinearOne:F0}nit, "
                         + $"γ={FfmpegGui.Services.ColorMapping.Bt2100Ootf.SystemGamma(p.Src.NitsOfLinearOne):F4}）；"
                         + "L_W 取源名义峰值（BT.2100），--color-hdr-peak 当前不改 OOTF 的 α（该值仅被色调映射消费；"
                         + "要改亮度结局请用 --color-tone-map 选算子）。";
        }

        // ── 2026-11-09（R1 P2-1）：用户显式 --color-hdr-peak 的「无承载」点名 ──
        // 缺陷（OnePlus HEIC 基线实测）：`--color-hdr-peak 1000` × png 直通 → 与 def 产物逐字节相同、
        // 无任何承接/未承接播报——峰值装配进了 it.HdrPeakNits（ColorIntentFactory 用户值优先），
        // 但本计划没有挂 Tone（直通不做色调映射）⇒ 该值无任何消费点 ⇒ 静默忽略。
        // UnconsumedOutputSettingItems（ColorEngineRouter）管的是"引擎编码出口吃不下编码参数"，
        // 峰值不在其清单、且 legacy 直通根本不经过那道闸 ⇒ 断线点在规划层，点名也落在规划层。
        // 口径：只在**直通/携带**（像素零改动、无 ToneMapParams）时点名——Map 且挂了 Tone 的格
        // 该值已被消费（ToneMapParams.PeakNits）；Map 而未挂 Tone 的场景（如 PQ 目标 OETF 直编）
        // 属另一族问题，不在本批扩面。措辞对齐"可用性诚实原则"：说清它没生效、生效条件是什么。
        if (p.Tone == null
            && p.Action is ColorAction.None or ColorAction.CarryIcc
            && it.HdrPeakSource == "用户 --color-hdr-peak"
            && it.HdrPeakNits > 0)
        {
            p.Reason += $"｜用户 --color-hdr-peak={it.HdrPeakNits:F0} 在本次交付中无承载"
                      + "（直通/携带不做色调映射，PNG 的 cICP 也不携带峰值元数据；"
                      + "该开关仅在与 --color-tone-map 同用时被消费）";
        }

        // ── 2026-11-10（R4）：用户显式 --color-matrix 的「无承载」点名 ──
        // matrix 的文档语义是**输入矩阵声明**（FfmpegOptions.cs:204，help 原话「YUV→RGB 解读」用）。
        // 引擎路径的输入恒为**已解码 RGB 像素**（Ultra HDR/RAW 中间件的 gbrpf32le PFM、或容器解码的
        // rgb48）——整个链路无 YUV 中转 ⇒ 矩阵声明没有任何作用对象；输出侧矩阵（OutMatrix）由
        // 目标 primaries 推导（MatrixTokenFor），从不读用户值。Ultra HDR 中间件路径的输入声明还在
        // FfmpegCommandBuilder.cs:1017 写死 bt709，用户值被整体绕过。
        // 24 格 D 组实测（D12/D13/D14，2026-11-09）：`--color-matrix bt2020nc/bt709` 在引擎格零消费、
        // 决策文案零提及（D12 产物逐字节 = avif-def）——静默无效正是本仓反复禁止的「看起来配置过」。
        // 判据通道：用户值经 it.Manual.Matrix（ManualIntent.Create 的 enabled=UseAdvancedColorParameters）
        // 进入规划层。引擎路径上 matrix 恒无承载（含 Map 格——输出矩阵同样由 primaries 推导），
        // 故只要值在位即点名，句式对齐上方 hdr-peak 点名。
        // ⚠ 已知缺口：CLI 裸 `--color-matrix`（未开高级色彩 ⇒ UseAdvancedColorParameters=false ⇒
        //   Manual=null）拿不到该值，本处点不到名——根因在装配层防呆契约与 CLI 无高级开关，
        //   ColorIntentFactory/CliParser 均不在 R4 授权面，如实上报不改。
        if (it.Manual?.FieldsFromUi == true && !string.IsNullOrWhiteSpace(it.Manual.Matrix))
            p.Reason += $"｜用户 --color-matrix={it.Manual.Matrix} 在本次交付中无承载"
                      + "（引擎路径输入为已解码 RGB 像素，无 YUV 矩阵语义；输出矩阵由目标 primaries 推导）";

        p.CodecLoss = CodecLossOf(pol.Format.Format, it.TargetLossless);
        EnforceContainerAnnotationRules(p, pol.Format, pol.ExternalEncoderExit);
        p.ColorLoss = ClassifyColorLoss(p, pol, it);
        p.Annot = ClassifyAnnot(p);
        // 出口位深真驱动（§5′.6）：直通/携带绝不降位；只有确实变换且目标容器上限 8bit 才走 8bit+抖动。
        // ⚠ 2026-09-16 追加 `pol.Format.RgbNative` 条件：**RGB 原生容器**（png/tiff/apng）的像素
        //   本身就是交付物 ⇒ 8bit 容器就该出 8bit。而 **YUV 容器**（jpg/webp）里 8bit 只是编码结果，
        //   中间件降成 8bit 会在 RGB→YUV 之前白丢精度（legacy 是保 16bit 到编码器）。
        //   A/B 实测：引擎出 `rgb24` 中间件时与 legacy 的 jpg 在 RGB 域仅 30.3dB（max|Δ|=122）；
        //   改为 16bit 中间件后见 TESTING §6 第 49 条复测。
        // ⚠ **2026-09-30 记一笔实测过的弯路**：曾把直通支也改成 `TargetBitDepth<=8 ⇒ 8`，想让 8bit 源
        //   直接按 8bit 交付。位深确实降下来了（同一份 8bit 源 20,233.6 kB → 11,485.5 kB），
        //   但**无损被改成 ±1 有损**：`OutBitDepth=8` 会让引擎走 `ColorKernels.EncodeTo8`，
        //   其 `code16/65535*255` 在 float 下对「×257 的码值」不是恒等映射
        //   （实测 0..91 恒等、92 起约半数偏低 1、252..255 恒偏低 1；`tests/output/t65/`）。
        //   ⚠ **2026-10-31 更正（结论不变）**：上面那句"×257 的码值"**与实物不符** —— 随包 ffmpeg 把
        //   8-bit 扩成 `rgb48le` 的顶码实测为 **65283**（≈×256 定点 + ≤3 码抖动），不是 257 的整数倍。
        //   "`EncodeTo8` 不是恒等映射"这条**实测结论**照旧成立（它量的是真实码值），
        //   错的只是把它归因给"257 倍数"；该错误归因正是 S8（白点被压低 1 LSB）的源头。
        //   而 ffmpeg 的 `format=rgb24` 与 cjxl 本身都是精确的（`tests/output/t66/` E1/E2/E3）。
        //   ⇒ 本字段的语义仍是「**中间件精度**」；直通时的**交付档**由各出口自己按
        //   `TargetBitDepth` + `ImageEncoderArgs.MapPixFmt` 决定（cjxl 出口已照此修，见该处注释）。
        // ⚠ **本式的值域是 {8,16}**（下游 `RawColorPipeline`/`ImageEncoderArgs.MapPixFmt` 只区分这两档：
        //   `OutBitDepth == 8` 走 rgb24、其余走 rgb48le）⇒ 目标 10/12 必然被取到上档 16，
        //   这一「请求 ≠ 交付」不静默：由 `CollectDegradations` 的 ①b 登记 `BitDepthRaised` 播报。
        p.OutBitDepth = p.Action != ColorAction.Map || p.ColorLoss == ColorLoss.None ? 16
            : (pol.TargetBitDepth <= 8 && !p.Requires16BitIntermediate && pol.Format.RgbNative ? 8 : 16);
        // 交付档要能被下游出口读到（此前 `ColorTransformPlan.TargetBitDepth` 声明了却无人赋值，
        // 于是「出口该出几 bit」只能被各出口重新推导一遍 —— cjxl 出口就是这么推错成中间件档的）。
        p.TargetBitDepth = pol.TargetBitDepth;
        EnsureAnnotationNotSilentlyMissing(p, pol, it);
        return p;
    }

    // ══════════════════════════ 策略覆盖（§2）══════════════════════════

    private static (ColorStrategy declared, ColorStrategy eff, OverrideReason ov) ResolveStrategy(ColorIntent it, PlanPolicy pol)
    {
        var declared = it.Strategy;
        // ⚠ 2026-11-09（D4 配套）：线性 HDR 也算 HDR 源 —— 与 ColorIntentFactory 的装配层判据同形
        //   （峰值证据走 it.HdrPeakNits，装配层已把 o.DecodedUltraHdrPeakNits 接进该位）。
        //   S2「携带」对线性 HDR 同样无 HDR ICC 携带通路（中间件无 SourceIccPath）⇒ 同样降级标注继承。
        bool srcHdr = it.Source?.Curve.Kind is TransferCurve.CurveKind.Pq or TransferCurve.CurveKind.Hlg
            || (it.Source?.Curve.Kind is TransferCurve.CurveKind.Linear
                && it.HdrPeakNits > TransferCurve.SdrWhiteNits);

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
            // 容器的 ICC 通路只对 avifenc 成立，而**本出口**是 ffmpeg（写不了 ICC）：
            // 源空间不可命名时 ICC 就是唯一描述 ⇒ 这一格无法诚实交付，必须拒绝（2026-10-26 实测缺陷）。
            if (!pol.ExitWritesIcc && !src.IsCicpNameable)
                return Reject(p, $"S2 携带：{caps.Format} 出口（ffmpeg）不写 ICC，而 {src.Name} 无 CICP 名"
                                 + " ⇒ 交付出去只会是一张「像素 A、标签 B」的图",
                    "改用 PNG/TIFF/WebP/JXL 出口携带 ICC；或显式指定可命名的目标空间"
                    + "（sRGB / Display P3 / BT.2020 / Rec.2100）让引擎映射；也可改 S1 推荐自动取可命名超集",
                    FormatAlts("能携带任意 ICC 的出口", "png", "tiff", "webp", "jxl"));
            p.Action = ColorAction.CarryIcc;
            p.Backend = ColorBackend.None;
            p.AttachIcc = pol.ExitWritesIcc;
            p.IccPath = pol.ExitWritesIcc ? src.SourceIccPath : null;
            p.ColorLoss = ColorLoss.None;
            p.Reason = "L1 无损：像素零改动，携带源 ICC（" + (System.IO.Path.GetFileName(src.SourceIccPath)) + "）";
            if (!pol.ExitWritesIcc)
                p.Reason = "L1 无损：像素零改动；该出口不写 ICC，源空间可 CICP 命名 ⇒ 以 nclx 标签描述";
            CarryCicpLabels(p, src, caps);
            return p;
        }

        // 结局 2（两种形态，都是像素零改动）：
        //   2a 容器可写 CICP 且源可命名 ⇒ **继承源 CICP 标签**（描述随容器元数据一起带走，不生成 ICC）
        //   2b 容器未标注语义明确（PNG/JPEG/WebP）⇒ 无标注直通 + UnlabeledAssumedSrgb
        if (src.Confidence >= EquivalenceTolerances.MinConfidenceForMapping && CapsAcceptsCicp(caps, src, pol.ExternalEncoderExit))
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
        var target = it.Target ?? NameableSupersetFor(src, caps, pol.ExternalEncoderExit);
        if (target == null || !CapsAcceptsCicp(caps, target, pol.ExternalEncoderExit))
            return Reject(p, $"S3 仅 CICP：源 {src.Name} 无法用 {caps.Format} 的 CICP 表达" +
                             // ⚠ "枚举集仅 …"这句只在**外部出口**下才成立（那位钉的是 cjxl 的
                             //   `-x color_space=` 宽度）；ffmpeg 出口没有这个限制 ⇒ 不该把它说成容器的锅。
                             (pol.ExternalEncoderExit && caps.ExternalOutletCicpSpaceEnums != null
                                 ? $"（本次出口为外部编码器，枚举集仅 {string.Join('/', caps.ExternalOutletCicpSpaceEnums)}）" : ""),
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

        bool canName = CapsAcceptsCicp(caps, target, pol.ExternalEncoderExit);
        // 「能携带」还要「本出口真写得进去」（AVIF/ffmpeg 写不了 ICC，见 PlanPolicy.ExitWritesIcc）
        if (!canName && (!caps.CanCarryArbitraryIcc || !pol.ExitWritesIcc))
            return Reject(p, $"S4 手动：{caps.Format} 既不能写该空间的 CICP 又不能携带任意 ICC"
                             + (!pol.ExitWritesIcc ? "（本出口不写 ICC）" : "")
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
        // ⚠ 2026-11-09（D4 配套）：线性 HDR 也算 HDR 源（判据与 ColorIntentFactory/ResolveStrategy 同形，
        //   峰值证据 = it.HdrPeakNits）。srcLinearHdr/linearHdrCarried 供下方 HDR 守卫分流 linear 用。
        bool srcHdr = src?.Curve.Kind is TransferCurve.CurveKind.Pq or TransferCurve.CurveKind.Hlg
            || (src?.Curve.Kind is TransferCurve.CurveKind.Linear
                && it.HdrPeakNits > TransferCurve.SdrWhiteNits);
        bool srcLinearHdr = src?.Curve.Kind is TransferCurve.CurveKind.Linear;
        bool linearHdrCarried = false;

        // 用户请求了 tonemap 但未给目标 ⇒ 目标取“源原色的 SDR 对应空间”。
        // 否则请求会被静默忽略（无目标 → Action=None → 不改像素），违反“不得静默降级”。
        if (pol.ToneMapRequested && srcHdr && it.Target == null)
        {
            var twin = SdrTwinOf(src!, caps, pol.ExternalEncoderExit);
            if (twin != null) it.Target = twin;
        }

        // ── HDR 源 + auto 目标 + 容器「能承载 HDR 但**当前位深**不够」⇒ 显式交回传统管线（2026-09-17）──
        // 为什么不能走下面的 HDR 守卫（Reject）：8bit PNG 是本工具的**合法**出口（GUI 默认位深就是 8），
        //   Reject 会让「HDR → PNG(8bit)」无路可走。
        // 为什么当初不让引擎接管 tonemap：引擎的 SDR 标定是 bt709/iec61966-2-1（sRGB 分段），而传统
        //   tonemap 链末级当时写 `zscale=…t=bt709`（zimg 把 `t=bt709` 实现成**纯 γ2.4**）⇒ 两条管线的
        //   交付确实不同，接管就等于**改变交付**，而当时的锁钉的就是 bt709/bt709。
        // ⚠ **该前提已随 #26②（2026-09-26）消失**：链末默认改成 `t=iec61966-2-1`（=sRGB 分段，
        //   CICP transfer=13；改前旧标签声称 BT.709 定义式却没实现它，且 H.273 里没有纯 γ2.4 的编号
        //   ⇒ 只能把像素提到标签水位，不许改标签收口）。现在两条管线的 SDR 出口**同为** bt709/iec61966-2-1
        //   ⇒ 这条 `交回` 从此**只是路径选择**（谁来做这次 tonemap），不再是正确性要求；
        //   P3「像素路径清退 zimg、全部走自研内核」落地时**连这条交回一起撤掉**。
        //   锁已同步：`verify-decision-delivery.ps1` ⑮（链末用的曲线 == 产物标签兑现的曲线 + 必须等于
        //   iec61966-2-1，另配"用生产写块器把标签改成 cICP=1 ⇒ 判据必须失配"的负控）。
        // 旧实现的另一处错也要记住：它曾宣称"不改像素、沿用源描述"（OutPrimaries=bt2020/
        //   OutTransfer=smpte2084），而交付其实是 tonemap 后的 SDR ⇒ 那才是「宣称≠交付」。
        // ⚠ 2026-11-09（D3）：linear HDR 的分流**先于** PQ/HLG 委派，且**不设** caps.CanHdr 前提 ——
        //   jpg/webp/tiff 这类 CanHdr=false 的 SDR-only 容器正是 D3 的主场景（暗图指纹所在）。
        //   linear HDR **不**交回传统管线：传统管线的 `NeedsHdrToSdrTonemap` 只认 PQ/HLG
        //   （FfmpegCommandBuilder.cs:1414），linear PFM 会被它当 SDR 直通：计划若声称
        //   "tonemap 与标注由传统管线执行"，交付却是 linear 原样码值 ⇒ 宣称≠交付
        //   （正是 PlanDelegatedHdrSdr 注释里批判过的旧错形态）。两种诚实结局：
        //  ① 容器在本位深能承载 HDR 语义（CanCarryHdrAt）⇒ 目标保持 null，落到函数尾
        //    PlanCore(src, null) 直通携带：像素=linear 原值、标注=源 linear 语义
        //    （AVIF/JXL 的 nclx transfer=linear、PNG 16bit 的 cICP[1,8,0,1]）⇒ 声明与像素自洽。
        //  ② 装不下（jpg/png 8bit/webp/tiff）⇒ 目标补成源原色的 SDR twin，走引擎**纯 OETF
        //    编码**（不挂 tonemap —— linear 已在线性域，OETF 是无损曲线映射；与 PQ/HLG
        //    「必须先 EOTF、不做 tonemap 就是高光裁剪灾难」不同）。linear 码值直接写 8-bit
        //    gamma 容器 = 暗图（2026-11-08 D3 实测指纹：默认档到 linear×255 仅 0.65/255 ⇒
        //    完全没做传递曲线；默认 vs 强制 sRGB 差 34.96/255）。twin 为 null ⇒ 落到下方
        //    :697 Reject（fail-closed，好过暗图）。
        if (srcHdr == true && it.Target == null && !pol.HdrOutput && !pol.ToneMapRequested && srcLinearHdr)
        {
            if (CanCarryHdrAt(caps, pol.TargetBitDepth))
            {
                linearHdrCarried = true;
            }
            else
            {
                // ⚠ `src!`：srcHdr 为真 ⇒ src 必非空（曲线 Kind 命中要求 src 存在），但编译器的
                //   可空流分析不追 bool 判定的传递 ⇒ 与 :151 的 `srcNn` 同款用 null-forgiving 收口。
                var twin = SdrTwinOf(src!, caps, pol.ExternalEncoderExit);
                if (twin != null) it.Target = twin;
            }
        }
        else if (srcHdr == true && it.Target == null && !pol.HdrOutput && !pol.ToneMapRequested && caps.CanHdr)
            return PlanDelegatedHdrSdr(p, it, pol, declared, ov);   // PQ/HLG 既有委派（linear 已被上一支截走）

        // HDR 不被悄悄 SDR 化（规划 §2）
        // ⚠ 2026-11-09（D3）：linear HDR 在这里**一律放行**（srcLinearHdr）——它的 SDR 结局
        //   （直通携带 / SDR twin 编码 / 用户显式 SDR 目标）走的都是**纯 OETF 曲线编码**
        //   （>1.0 段按容器钳制 = 编码语义），不是色调映射压暗；:697 防的「HDR→SDR 需显式
        //   请求 tonemap，否则高光整体压暗」针对的是 PQ/HLG 码值域必须先 EOTF 展开的情形。
        //   ⚠ 显式 `--color-tone-map none` 的 linear HDR 也到此放行：none = 不挂 tonemap 算子
        //     ≠ 不编码（8-bit 容器总得从线性 float 转到 gamma 码域），交付=OETF 编码，诚实。
        //   ⚠ PQ/HLG 侧行为一个字未动（其不触发 :697 的既有前提 = 装配层 :389 的自动兜底）。
        if (srcHdr == true && !pol.HdrOutput && !pol.ToneMapRequested && !linearHdrCarried && !srcLinearHdr)
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
        // ⚠ 出口写不了 ICC 时（`ExitWritesIcc=false`，AVIF 静态出口）：
        //   源空间**可 CICP 命名** ⇒ 照常零改动携带，描述靠标签（`AttachIcc=false`）；
        //   不可命名（ProPhoto/Adobe…）⇒ 不得走这里：ICC 是唯一描述，写了也落不了盘
        //   ⇒ 落到下面的 L2（取可命名超集映射），由容器把像素与标签对齐。
        bool carryInScope = pol.CarryScope switch
        {
            CarryScope.None => false,
            CarryScope.Unnameable => src != null && !src.IsCicpNameable,
            _ => true,
        };
        if (!pol.TargetExplicit && carryInScope && src?.SourceIccPath != null && caps.CanCarryArbitraryIcc && srcHdr == false
            && (pol.ExitWritesIcc || src.IsCicpNameable))
        {
            p.Action = ColorAction.CarryIcc;
            p.Backend = ColorBackend.None;
            // 出口写不了 ICC ⇒ 只保留可命名标签（`CarryCicpLabels` 已按源空间写入）
            p.AttachIcc = pol.ExitWritesIcc;
            p.IccPath = pol.ExitWritesIcc ? src.SourceIccPath : null;
            p.Reason = $"L1 无损优先（carry-scope={pol.CarryScope}）：像素零改动，携带源 ICC";
            if (!pol.ExitWritesIcc)
                p.Reason += "；⚠ 该出口不写 ICC ⇒ 仅以可命名的 CICP 标签描述（源空间可命名才允许走到这里）";
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
        bool canCarrySourceIcc = caps.CanCarryArbitraryIcc && pol.ExitWritesIcc && src?.SourceIccPath != null;
        bool expressible = src != null && (CapsAcceptsCicp(caps, src, pol.ExternalEncoderExit)
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
            var sup = NameableSupersetFor(src, caps, pol.ExternalEncoderExit);
            // 源色域比**所有**可命名目标都宽（ProPhoto/ROMM，BT.2020 也装不下）时的唯一出路：
            // 取**最宽的可命名目标** BT.2020/SDR，越界色裁切 —— 这正是传统管线对 avif 一直做的
            // （`-color_primaries bt2020 -color_trc bt709` + `GamutClipped` 播报），现在引擎侧
            // 与它同口径，而不是把 AVIF 整格拒掉（那会让 ProPhoto 源失去 avif 出口）。
            // ⚠ 只在容器真的能写该空间的 CICP 时才走；否则仍是 Reject，绝不产出无标注行。
            bool clippedToWidest = false;
            if (sup == null)
            {
                var widest = ColorSpaceDescriptor.FromCicp("bt2020", "bt709", "CICP:bt2020/bt709");
                if (widest != null && CapsAcceptsCicp(caps, widest, pol.ExternalEncoderExit))
                {
                    sup = widest;
                    clippedToWidest = true;
                }
            }
            if (sup == null)
                return Reject(p, $"S1 推荐：{caps.Format} 既不能携带 {src.Name} 的 ICC，又不能表达该空间",
                    "改用 PNG/TIFF/WebP/JXL 携带 ICC；或接受归入可命名超集（需容器支持）",
                    FormatAlts("能力表 CanCarryArbitraryIcc ⇒ 可携带任意 ICC", "png", "tiff", "webp", "jxl"));
            var c2 = PlanCore(src, sup, pol);
            c2.DeclaredStrategy = declared;
            c2.EffectiveStrategy = ColorStrategy.Recommended;
            if (ov != OverrideReason.None) c2.Override = ov;
            c2.Reason = (clippedToWidest
                    ? $"L2 归入最宽可命名目标（{src.Name} → {sup.Name}；源比任何可命名空间都宽 ⇒ 越界色裁切，"
                      + "已按 GamutClipped 播报）"
                    : $"L2 超集归一（{src.Name} → {sup.Name}，包含源色域⇒零削色）")
                + "：" + c2.Reason;
            return c2;
        }

        var core = PlanCore(src, it.Target, pol);
        core.DeclaredStrategy = declared;
        core.EffectiveStrategy = ColorStrategy.Recommended;
        if (ov != OverrideReason.None) core.Override = ov;
        // ⚠ 2026-11-09（D3）：linear HDR 直通携带必须点名 —— PlanCore(src, null) 的默认理由是
        //   "无目标色彩空间 → 不改像素"，不解释"为什么这次不改像素是诚实的"（峰值 >SDR 白点的
        //   线性像素原样进容器）。放行 ≠ 静默：理由里写清容器可承载、位深足够、标注自洽三件事。
        if (linearHdrCarried)
            // ⚠ 2026-11-10（R4 措辞修正）：旧句「像素=linear 原值」只对 RGB 容器（PNG 16bit）成立；
            //   AVIF 这类 YUV 出口实际产物是 yuv420p12le（24 格 D12 实测：RGB→BT.709 矩阵转换 +
            //   4:2:0 色度下采样）。改措辞区分两件事：**色彩语义不变**（标注=源 linear 语义、不做
            //   SDR 化——这是本播报担保的）与**像素可能被容器色度降采样**（编码事实，见 --chroma）。
            core.Reason = "线性 HDR 直通携带（容器可承载 HDR 且位深足够 ⇒ 色彩语义不变：标注=源 linear 语义、"
                        + "不做 SDR 化。像素按出口编码交付：RGB 容器=linear 原值；YUV 容器（如 AVIF）经矩阵转换"
                        + "与色度下采样（默认 4:2:0，见 --chroma），不是逐值原样）：" + core.Reason;

        // ── 2026-11-10（R4）：SDR-only 出口的 linear HDR 高光裁剪感知提示 ──
        // 机理：linear HDR 源码值 1.0 = SDR 白点 203nits（TransferCurve.SdrWhiteNits），峰值像素
        //   线性值 = HdrPeakNits/203 > 1.0。SDR-only 出口经 OETF 编码时 OETF(>1.0) 仍 >1.0 ⇒
        //   码域钳到 1.0（255）⇒ 高光被**裁剪**（tonemap 是压缩保留层次，裁剪直接削平）。
        //   D3 的「纯 OETF 编码」修的是暗图（没做曲线映射），不改变 >1.0 段的钳制事实。
        // 判据（缺一不可，均防误伤）：
        //   · srcLinearHdr：源是 linear 曲线（PQ/HLG 源的 SDR 化走 :767 委派/自动兜底，另有播报）；
        //   · it.HdrPeakNits > SdrWhiteNits：确有 HDR 峰值证据（来源已入日志 :349）；
        //   · !pol.ToneMapRequested：tonemap 已承接高光（压缩而非裁剪），不重复提示；
        //   · !linearHdrCarried：直通携带像素保全（HDR 语义在容器里活着），无裁剪；
        //   · 目标曲线非 PQ/HLG 且 !pol.HdrOutput：出口本身是 HDR 时无裁剪。
        // 真机件数学校准：华为 IMG_20261102_190339 峰值 900nits ⇒ headroom=900/203=4.43× ⇒ 命中；
        //   OnePlus IMG20261101124423 峰值 232nits ⇒ headroom=232/203=1.14× < 2.0 ⇒ 不命中。
        //   阈值 2.0× = 高光溢出超 1 档（+1 stop）才会被明显削平；1.14× 的溢出段在 8bit 量化噪声量级。
        double sdrHeadroom = it.HdrPeakNits / TransferCurve.SdrWhiteNits;
        bool sdrExitForLinear = srcLinearHdr
            && it.HdrPeakNits > TransferCurve.SdrWhiteNits
            && !pol.ToneMapRequested
            && !linearHdrCarried
            && !pol.HdrOutput
            && it.Target?.Curve.Kind is not (TransferCurve.CurveKind.Pq or TransferCurve.CurveKind.Hlg);
        if (sdrExitForLinear && sdrHeadroom >= 2.0)
            core.Reason += $"｜HDR headroom {sdrHeadroom:F2}×，纯线性编码将裁剪高光，建议 --color-tone-map hable";

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
        if (!CapsAcceptsCicp(caps, src, p.ExternalEncoderExit)) { p.CicpUnavailable = caps.CanCicp ? CicpUnavailableReason.SpaceNotNameable : caps.NoCicpReason; return; }
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
    public static ColorSpaceDescriptor? NameableSupersetFor(ColorSpaceDescriptor? src, FormatColorCaps caps, bool externalEncoderOutlet)
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
            if (d == null || !CapsAcceptsCicp(caps, d, externalEncoderOutlet)) continue;
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
    private static ColorSpaceDescriptor? SdrTwinOf(ColorSpaceDescriptor src, FormatColorCaps caps, bool externalEncoderOutlet)
    {
        if (src.PrimariesToken == null) return null;
        foreach (var trc in new[] { "iec61966-2-1", "bt709" })
        {
            var d = ColorSpaceDescriptor.FromCicp("bt709", trc, $"{src.Name} -> SDR(bt709/{trc})");
            if (d != null && (CapsAcceptsCicp(caps, d, externalEncoderOutlet) || caps.CanCarryArbitraryIcc || caps.CanGenerateIccFromCicp))
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
    /// <param name="externalEncoderExit">
    /// 本次产物由**外部编码器出口**（cjxl）编码。<see cref="FormatColorCaps.CicpSpaceEnums"/> 钉的
    /// 就是这个枚举的宽度 —— ffmpeg 的 libjxl 出口能写任意 CICP（实测见下方择一分支的注释），
    /// 所以"这条 CICP 落不落得进"**必须按出口判**。⚠ 不能读 <c>p.ExternalEncoderExit</c>：那个位是
    /// <see cref="Plan"/> 在 <c>PlanWithPolicy</c> **返回之后**才装配的（:40），在本函数里恒为 false
    /// —— 第一版就是这么把已经修好的 cjxl 分支又改回去的（实测：wiring (10) 三条同时转红）。
    /// </param>
    private static void EnforceContainerAnnotationRules(ColorTransformPlan p, FormatColorCaps caps, bool externalEncoderExit)
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
            // 择一：携带型 ICC 优先（描述更完备）；纯生成 ICC 且源已命名则保留 CICP。
            // ⚠⚠ 但"保留 CICP"必须以**这个容器真的写得下这条 CICP**为前提（2026-09-26 任务 #37 实测）：
            //   JXL 的 `color_space` 只有 sRGB / Display P3 / Rec.2100 PQ / HLG 四个枚举
            //   （`CapsAcceptsCicp` + `CicpSpaceEnums`），拿 `bt2020/bt709` 走这条分支会把**唯一能兑现的
            //   那个标注（生成 ICC）剥掉**，留一个执行体写不出来的 CICP ⇒ 实测结局是 cjxl 出口抛
            //   「计划未提供 ICC ⇒ 拒绝」（网格 full 层 24 格 `jxl × BT.2020` 全数被拒）。
            //   判据仍只有**一个真值源**：`CapsAcceptsCicp` —— 与路由/执行体用的是同一个函数，
            //   不在这里另写一份枚举表。
            // ⚠⚠ 但这条前置必须看**实际出口**，不能只看格式能力表（2026-09-26 实测打出来的一格）：
            //   `CicpSpaceEnums` 钉的是 **cjxl 的 `-x color_space=` 枚举**，而同一目标走 ffmpeg 的
            //   libjxl 出口**能写任意 CICP**（实测：`-e Ffmpeg` + DCI-P3 ⇒ jxlinfo 读回
            //   「Rec.2100 primaries, 709 transfer」，`verify-color-wiring` (10) 的交叉证明格）。
            //   无条件按枚举剥 CICP 会把**那条本来能交付的路**改成"附一张 ffmpeg 出口写不进的 ICC"
            //   ⇒ 实测产物 0 字节（`RawColorPipeline` 的 JXL 附 ICC 诚实门抛）。
            //   能力表本来就没有"后端"这一维（这是 #37 暴露出的真缺口），这里先用出口标志位收口：
            //   **只有 cjxl 出口**才受枚举约束。
            var cicpProbe = ColorSpaceDescriptor.FromCicp(p.OutPrimaries, p.OutTransfer, "择一判定");
            if (p.IccPath != null)
            { p.OutPrimaries = p.OutTransfer = p.OutMatrix = null; p.CicpUnavailable = CicpUnavailableReason.ContainerNonNormative; cicp = false; }
            else if (!CapsAcceptsCicp(caps, cicpProbe, externalEncoderExit))
            { p.OutPrimaries = p.OutTransfer = p.OutMatrix = null; p.CicpUnavailable = CicpUnavailableReason.SpaceNotNameable; cicp = false; }
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

    // ════════ 输出矩阵模式（C-CAPS@1 / C-REPORT@1 / C-PLAN-EXT@1，color-matrix-options 计划）════════
    //
    //  显式输出矩阵模式（--color-out-matrix / GUI「色彩矩阵模式」）的三件事都只在这一段做：
    //  ① C-CAPS@1 唯一真值判定（ResolveOutMatrixCapability——按 T1 扫描报告校准版判定表编码）；
    //  ② C-PLAN-EXT@1 覆盖（ApplyExplicitOutMatrix——在 MatrixTokenFor 三处推导完成之后
    //    覆盖 p.OutMatrix 并置 OutMatrixVerdict；Plan() 在 Verdict 投影之前调用）；
    //  ③ C-REPORT@1 规划层播报（Reason 中文同义句；ASCII 模板常量供 T6 出口层 EmitWarn 消费）。
    //  动图回退路径与预设入口汇聚到 Plan(ColorIntent) 后自动走同一判定（不得出现第二份表）。
    //  auto（null）整段早退 ⇒ 全链路与改造前逐字节同路径（CM-REQ-08 回归红线）。

    /// <summary>
    /// C-REPORT@1 播报模板（本任务 = Provider；T6 出口层 EmitWarn 消费、T7 播报锁断言）。
    /// <para>
    /// ⚠ 全 ASCII：<c>ImageEncoderArgs.EmitWarn</c> 的硬约束（<c>_probe-cjk-hardcode-scan</c>
    /// 的 CsUi 余量 0）；契约文档模板 1 里的「→」在 ASCII 约束面下落地为 <c>-></c>。
    /// 中文同义句由规划层写进 <see cref="ColorTransformPlan.Reason"/>（规划层通道中文可用）。
    /// 占位符按 <c>string.Format</c> 顺序：
    /// <list type="bullet">
    /// <item>模板 1（落地）<see cref="OutMatrixReportLanded"/>：{0}=format {1}=token
    ///       {2}=落地描述（**由 T6 出口填**：nclx matrix_coefficients=n / -colorspace name
    ///       （输出侧）/ cjxl VarDCT (XYB) / libjxl -xyb 1 / cjpegli --xyb）；</item>
    /// <item>模板 2（冗余）<see cref="OutMatrixReportRedundant"/>：{0}=token {1}=format
    ///       {2}=RGB|XYB-default（当前 T1 校准版判定表**无** XYB-default 命中格——jxl 两后端的
    ///       xyb 均 Carried，占位按契约保留）；</item>
/// <item>模板 3（拒绝）<see cref="OutMatrixReportRejected"/>：{0}=token {1}=format
///       {2}=原因短语（规划层在 Rejected 时**直接发**——出口不被调用 ⇒ T6 无机会发）。</item>
/// <item>模板 4（跨维冲突拒绝）<see cref="OutMatrixReportConflict"/>：{0}=显式 token
///       {1}=推导矩阵 {2}=format（CMX 批新增：交叉一致性校验闸——显式 H.273 YUV 族值与
///       primaries 推导链留下的矩阵标注跨族 ⇒ 由规划层直接发，同模板 3）。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 播报唯一性（计划 §2.3 第 5 条）：Carried/Redundant 的 ASCII 模板由 T6 出口层发
    /// （出口被调用的格子才有出口侧播报）；Rejected 由规划层发——三档各自只有一个发点，不双播。
    /// </para>
    /// </summary>
    public const string OutMatrixReportLanded = "[color-out-matrix] {0}: {1} -> {2}";
    public const string OutMatrixReportRedundant = "[color-out-matrix] {0} on {1}: container is already {2}; recorded, no pixel effect";
    public const string OutMatrixReportRejected = "[color-out-matrix] {0} is not carried by {1} ({2}) => contract rejection, exit 91, no output";
    // 模板 4（CMX 批）：跨维冲突拒绝——显式 H.273 YUV 族值与 primaries 推导链留下的矩阵标注
    // 跨族（如 BT.2020 源 + 显式 bt709 ⇒ primaries=9 + matrix=1，消费端从 matrix 反推 primaries
    // ⇒ 广色域错读）。占位 {0}=显式 token、{1}=推导矩阵（覆盖前 p.OutMatrix）、{2}=format；
    // 全 ASCII（CsUi 纪律），由规划层在交叉一致性校验闸拦截时直接发。
    public const string OutMatrixReportConflict = "[color-out-matrix] {0} conflicts with derived matrix {1} for {2} output => contract rejection, exit 91, no output";

    /// <summary>
    /// C-CAPS@1 唯一真值判定函数：格式 × 编码后端 × 归一 token ⇒ 承载判定。
    /// <para>
    /// 判定表按 <b>T1 扫描报告校准版</b>编码
    /// （tests/output/_tmp/color-matrix-scan-20261110/report.md §7 校准清单 C1-C13，2026-11-10
    /// 读数；与计划书 C-CAPS@1 初版的出入以下列修订注记为准——「校准属兼容性追加/收窄」）：
    /// </para>
    /// <list type="bullet">
    /// <item><b>C1/C2</b>：webp/jpg × bt470bg 由计划初版 Carried 校准为 <b>Redundant</b>
    ///       （auto 恒 bt470bg，显式值与推导值相同、产物逐字节一致 ⇒ 「冗余但一致」档）；</item>
    /// <item><b>C3</b>：webp/jpg × smpte170m（bt601 归一后）校准为 <b>Redundant</b>
    ///       （swscale 实测 bt470bg=smpte170m 同组转换结果——ColorSpaceRegistry.cs:621；
    ///       601 族整体收窄为 Redundant，其余 YUV 族维持 Rejected）；</item>
    /// <item><b>C4</b>：avif × ycgco 校准为 <b>Rejected</b>（swscale 输入侧实测不可转换
    ///       ——ColorSpaceRegistry.cs:622；输出侧同引擎未证，保守收窄）；</item>
    /// <item><b>C5</b>：avif × fcc 校准为 <b>Rejected</b>（AV1 bitstream matrix_coefficients
    ///       枚举 3/4=Reserved，nclx 写不进——容器机制推断，同 AGENTS §7 AVIF 口径标注方式）；</item>
    /// <item><b>C6</b>：avif × avifenc 两步法（gif→avif / jxr→avif）= <b>Not-wired</b>：
    ///       两步法参数拼装无矩阵项（QueueProcessor.cs:4612/:5352），且不经过本规划层——
    ///       显式矩阵经 CLI/GUI 提交的 avif 任务恒走 ffmpeg 编码器出口（libaom/libsvtav1），
    ///       故本函数对 avif 的非 Ffmpeg 后端保守判 Rejected；两步法与显式矩阵并存的行为属 T6 出口面；</item>
    /// <item><b>C7/C13</b>：avif × YUV 族 = <b>Carried + Requires-PixFmt-Switch</b>：
    ///       nclx 可写（枚举 1/5/6/7/9）且输出侧 -colorspace 驱动 RGB→YUV 转换，但引擎通路
    ///       pix_fmt 判据（RawColorPipeline.cs:1053）未命中时 libaom 自选 gbrp ⇒ 标签孤立。
    ///       「切 pix_fmt」由 T6 出口按本 Carried 决议并入落地（计划 §2.3 第 3 条同向）——
    ///       本函数不为此新增枚举成员（OutMatrixCapability 四值契约不变），pixfmt-switch
    ///       经规划层 Reason 播报与 T6 落地点兑现；</item>
    /// <item><b>C8</b>：jxl × cjxl × xyb = <b>Carried</b>（cjxl v0.12.0 无显式 xyb 开关——
    ///       VarDCT 隐式承载；「显式确认」= 规划层播报确认，与 --jxl-modular 的冲突点名归
    ///       CjxlService 既有 modular 优先播报（T6 面））；</item>
    /// <item><b>C9</b>：jpg × cjpegli × xyb = <b>Carried</b>（--xyb 旗标存在；
    ///       BuildCjpegliArguments 当前零引用 = 未接线，T6 新增传递——Carried 的落地前提是
    ///       T6 接线，判定照常产出；cjpegli 出口不经本规划层，由 T6 以 backend=Cjpegli 直调本函数）；</item>
    /// <item><b>C10</b>：jxl × ffmpeg libjxl × xyb = <b>Carried</b>（-xyb AVOption 实测 default 1
    ///       已开，显式即确认）；</item>
    /// <item><b>C11</b>：png/apng/tiff/tif/bmp/ppm/gif：rgb=<b>Redundant</b>（RGB 原生/调色板
    ///       容器本就是 RGB 语义）、YUV 族与 xyb=<b>Rejected</b>（无矩阵概念）——计划初版确认项；</item>
    /// <item><b>C12</b>：dng/jxr：「矩阵」是契约外概念（DNG ColorMatrix1/2=相机校准矩阵、
    ///       JXR 内部 YCoCg 不暴露）⇒ YUV 族与 xyb=<b>Rejected</b>；rgb 按计划初版
    ///       <b>Redundant</b>（渲染空间/载体均为 RGB 线性域；T1 无冲突项 ⇒ 保留计划值）。
    ///       dng 不经本规划层（ColorIntentFactory 对它返回 null——传感器 Bayer 数据不接入引擎），
    ///       判定格为死格、编码只为表完整性；jxr 经引擎 JxrEncApp 出口可达；</item>
    /// </list>
    /// <para>
    /// 已知边界（三参签名的维度上限）：avif × rgb 按 <b>libaom-av1</b>（默认编码器）口径判
    /// Carried（nclx matrix=0 + gbrp 直喂语义自洽，T1 §4 表「可写」）；用户显式选 libsvtav1 时
    /// 它无 gbrp 支持位（T1 §1 表：rgb 拒绝）——该收窄需要编码器名维度（backend 枚举装不下），
    /// 由出口层既有像素格式边界族（ImageEncoderArgs.cs:1019 libsvtav1 边界）与 T6 落地点承接；
    /// 本函数不做二次判定（单一真值）。同理，Requires-PixFmt-Switch 的「引擎通路 vs legacy 通路」
    /// 区分（legacy 的 MapPixFmt 恒显式 YUV pix_fmt、无孤立标签问题）也由 T6 落地点按出口维度处理。
    /// </para>
    /// <para>
    /// token 契约：调用方传入的值已在 <c>FfmpegOptions.ColorOutMatrix</c> 的 setter 归一
    /// （bt601→smpte170m、auto/空白→null、小写——T2 落地；leader 2026-11-10 批复确认
    /// 「判定函数无需再归一」）⇒ 本函数按归一后 token 字面匹配，不重复归一；
    /// 空白/null 返回 NotApplicable（auto 无判定意义）。
    /// </para>
    /// </summary>
    /// <param name="fmt">目标格式（如 "jpg" / "webp" / "avif" / "jxl"；大小写不敏感）。</param>
    /// <param name="token">输出矩阵 token（C-TOKEN@1 域的**归一后**成员；null/空白 = auto）。</param>
    /// <param name="backend">编码后端（决定 jpg 的 mjpeg/cjpegli 与 avif 的 ffmpeg 编码器口径）。</param>
    public static OutMatrixCapability ResolveOutMatrixCapability(string fmt, string? token, EncoderBackend backend)
    {
        if (string.IsNullOrWhiteSpace(token)) return OutMatrixCapability.NotApplicable;   // auto：无判定意义
        var t = token.Trim();
        var f = (fmt ?? "").Trim().TrimStart('.').ToLowerInvariant();
        // 601 族（bt470bg/smpte170m）：swscale 实测同组转换结果（C3 校准）。
        bool yuv601 = t is "bt470bg" or "smpte170m";

        switch (f)
        {
            case "jpg" or "jpeg" or "jpegli":
                if (backend == EncoderBackend.Cjpegli)
                {
                    // T1 §1 表第 3 行（cjpegli 出口）：像素全程 RGB ⇒ rgb 冗余；
                    // 无 YUV 转换发生点 ⇒ 全 YUV 族（含 601）Rejected；--xyb 内部特性 ⇒ xyb Carried（C9）。
                    if (t == "rgb") return OutMatrixCapability.Redundant;
                    if (t == "xyb") return OutMatrixCapability.Carried;
                    return OutMatrixCapability.Rejected;
                }
                // T1 §1 表第 1/2 行（ffmpeg mjpeg legacy + 引擎 raw 通路；两行判定一致）：
                // C1/C2/C3 校准——601 族 = Redundant（auto 恒 bt470bg，显式值与推导值相同、产物
                // 逐字节一致）；rgb = Rejected（mjpeg 仅收 YUV pix_fmt）；bt709/smpte240m/fcc/
                // bt2020nc/ycgco = Rejected（转换偏离解码端固定 BT.601 假设 = 饱和色偏，
                // 2026-10-25 实测纯红 255,0,0→232,0,1；JFIF 无矩阵标注位——对齐 2026-11-03
                // colorRange 出口侧口径）；xyb = Rejected（mjpeg 无 XYB 概念）。
                if (yuv601) return OutMatrixCapability.Redundant;
                return OutMatrixCapability.Rejected;
            case "png" or "apng" or "tiff" or "tif" or "bmp" or "ppm" or "gif":
                // T1 §1 表第 4-6/15-16/18 行 + C11 确认：RGB 原生/调色板/无头容器——
                // rgb = Redundant（本就是 RGB 语义；gif 的依据 = 引擎链像素 rgb24/rgb48le →
                // pal8，矩阵无作用点）；YUV 族与 xyb = Rejected（无矩阵概念）。
                if (t == "rgb") return OutMatrixCapability.Redundant;
                return OutMatrixCapability.Rejected;
            case "webp":
                // T1 §1 表第 7/8 行（ffmpeg libwebp legacy + 引擎；两行判定一致）：C1/C3 校准——
                // 601 族 = Redundant（swscale 与 bt470bg 同系数，解码端假设同为 BT.601 族）；
                // rgb = Rejected（有损路径仅 yuv420p；无损路径 bgra 是编码器内部表示非矩阵语义）；
                // bt709/smpte240m/fcc/bt2020nc/ycgco = Rejected（WebP 容器无矩阵标注位 + 转换偏离
                // 解码端固定假设 = 人为色偏，对齐 2026-11-03 colorRange 出口侧口径）；xyb = Rejected。
                if (yuv601) return OutMatrixCapability.Redundant;
                return OutMatrixCapability.Rejected;
            case "avif":
                if (backend != EncoderBackend.Ffmpeg)
                    return OutMatrixCapability.Rejected;   // C6：avifenc 两步法不经本规划层；防御 fail-closed
                // T1 §1 表第 11 行（libaom-av1，legacy 默认编码器 + 引擎出口）：
                // rgb = Carried（nclx matrix=0 + gbrp 直喂语义自洽——按 libaom 口径，libsvtav1
                // 边界见方法注释）；ycgco = Rejected（C4）；fcc = Rejected（C5）；xyb = Rejected
                //（AV1/AVIF 无 XYB 概念）；bt709/bt470bg/smpte170m/smpte240m/bt2020nc = Carried
                //（C7：nclx 枚举 1/5/6/7/9 可写 + 输出侧 -colorspace 驱动转换；
                // Requires-PixFmt-Switch 由 T6 出口并入落地）。
                if (t == "rgb") return OutMatrixCapability.Carried;
                if (t is "ycgco" or "fcc" or "xyb") return OutMatrixCapability.Rejected;
                return OutMatrixCapability.Carried;        // 601/709 族
            case "jxl":
                // T1 §1 表第 13/14 行（cjxl 与 ffmpeg libjxl；rgb/YUV 判定两行一致）：
                // JXL RgbNative ⇒ rgb = Redundant（JXL 本就是 RGB 容器）、YUV 族 = Rejected
                //（RgbNative，无 YUV 承载位）；xyb = Carried（C8 cjxl 隐式确认 / C10 libjxl
                // -xyb default 1——两后端判定相同，落地描述由 T6 按出口区分）。
                if (t == "rgb") return OutMatrixCapability.Redundant;
                if (t == "xyb") return OutMatrixCapability.Carried;
                return OutMatrixCapability.Rejected;
            case "dng" or "jxr":
                // C12 确认：「矩阵」是契约外概念 ⇒ YUV 族与 xyb = Rejected；rgb 按计划初版
                // Redundant（渲染空间/载体均为 RGB 线性域；dng 为死格，编码只为表完整性）。
                if (t == "rgb") return OutMatrixCapability.Redundant;
                return OutMatrixCapability.Rejected;
            default:
                // 未知格式 fail-closed：矛盾选择按 Rejected 收场（CM-REQ-05），不静默放行。
                return OutMatrixCapability.Rejected;
        }
    }

    /// <summary>
    /// 显式输出矩阵模式的**唯一覆盖点**（C-PLAN-EXT@1）：在既有 <c>MatrixTokenFor</c> 三处推导
    /// （ColorTransformPlan.SetLabelsFrom / 本文件 PlanCicpOnly / CarryCicpLabels）**全部完成之后**
    /// 由 <see cref="Plan"/>（Verdict 投影之前）调用——
    /// auto（null）直接早退 ⇒ 整条链路与改造前逐字节同路径（CM-REQ-08）；
    /// 显式值按 C-CAPS@1 判定并覆盖 <c>p.OutMatrix</c> / 置 <c>p.OutMatrixVerdict</c> / 播报。
    /// <para>
    /// 播报通道（C-REPORT@1）：Carried/Redundant 只写 <see cref="ColorTransformPlan.Reason"/>
    /// （规划层 ⇒ 任务详情，中文），ASCII 模板（OutMatrixReportLanded/Redundant）由 T6 出口层
    /// EmitWarn 消费（「落地描述」由 T6 出口填——本层不写死落地点）；**Rejected 由本层直接发
    /// EmitWarn ASCII**（出口不被调用 ⇒ T6 无机会发）+ Reason 中文同义句；exit 91 由既有
    /// 「Action=Reject ⇒ QueueProcessor 不出产物 return 91」机制兑现
    /// （ProPhoto S2 携带契约拒绝同款形态，queue :1702-1710）。
    /// </para>
    /// <para>
    /// 裁定注记：xyb 是 libjxl 内部感知色彩表示（非 H.273），Carried 时**不覆盖**
    /// <c>p.OutMatrix</c>（那是 L5 CICP 标注域——ToFfmpegColorSpaceName 不认 xyb，写入会被
    /// iccgen 的 IccMatrix / 输出侧 -colorspace 消费点踩到）；T6 按
    /// <see cref="ColorTransformPlan.OutMatrixMode"/> 分派编码器开关（cjxl VarDCT 隐式 /
    /// libjxl -xyb / cjpegli --xyb）。H.273 族 Carried ⇒ 覆盖 p.OutMatrix = token。
    /// </para>
    /// <para>
    /// 主体已 Reject 时（PlanWithPolicy 各提前返回分支）显式矩阵判定**照跑**（fail-closed：
    /// 显式选择必须被点名，不许静默），Rejected 分支只**追加** Reason（不覆盖主体拒绝原因）；
    /// Carried/Redundant 分支不翻转 Action（主体拒绝的格子出口本就不被调用，exit 91 已然）。
    /// </para>
    /// <para>
    /// backend 推导：与 ColorIntentFactory 的 ExternalEncoderExit 判据同源（(jxl &amp;&amp; Cjxl) || jxr）。
    /// ⚠ jpg×Cjpegli 不经本规划层（QueueProcessor 的 cjpegli 分派带引擎豁免）⇒ 本层 jpg 恒按
    /// Ffmpeg（mjpeg）口径判；cjpegli 出口的判定由 T6 出口层以 backend=Cjpegli 直接调
    /// <see cref="ResolveOutMatrixCapability"/>（唯一真值函数，不构成第二份表）。
    /// </para>
    /// </summary>
    private static void ApplyExplicitOutMatrix(ColorTransformPlan p, ColorIntent it, PlanPolicy pol)
    {
        var token = it.OutMatrixMode;
        if (string.IsNullOrWhiteSpace(token)) return;   // auto（null）：零播报、零覆盖（CM-REQ-08 回归红线）

        var fmt = pol.Format.Format;
        var backend = fmt == "jxr" ? EncoderBackend.Jxr
            : pol.ExternalEncoderExit && fmt == "jxl" ? EncoderBackend.Cjxl
            : EncoderBackend.Ffmpeg;
        var cap = ResolveOutMatrixCapability(fmt, token, backend);
        p.OutMatrixMode = token;
        p.OutMatrixVerdict = cap;

        // ── 交叉一致性校验的输入（CMX 批第二道闸，A1 严口径）──
        // 覆盖前读：此刻 p.OutMatrix 仍是 MatrixTokenFor 三处推导（SetLabelsFrom / PlanCicpOnly /
        // CarryCicpLabels）完成后留下的**推导值**（bt2020nc / bt709 / smpte170m / null——值域闭集、
        // 全小写字面量；显式值对 p.OutMatrix 的覆盖只发生在下方 Carried 分支内），不是显式值。
        var derivedMatrix = p.OutMatrix;
        var tNorm = token.Trim();   // 与 ResolveOutMatrixCapability 内部 Trim 同构（T2 归一后本无空白，防御）
        // 参与面：仅 H.273 YUV 族显式值（非 rgb 非 xyb）——xyb 是 libjxl 内部感知表示、Carried 时
        // 不覆盖 p.OutMatrix（既有裁定）；rgb 是 identity 矩阵、无 primaries 约束。两者不参与本闸，
        // 承载判定照旧（判定表第 2/3 行）。本闸只做「显式 vs 推导」的交叉比较，不构成第二份承载表
        // （唯一真值仍是 ResolveOutMatrixCapability）。
        bool cmxParticipates = tNorm != "xyb" && tNorm != "rgb";
        // 601 族互通（判定表第 6 行）：显式 smpte170m ↔ 推导 bt470bg（或反向）——T1 实测 swscale
        // 同系数（ColorSpaceRegistry.cs:621）；bt601 已在入口归一为 smpte170m ⇒ 天然进族。
        bool cmxSame601 = cmxParticipates
            && tNorm is "bt470bg" or "smpte170m"
            && derivedMatrix is "bt470bg" or "smpte170m";
        bool cmxSameValue = cmxParticipates && string.Equals(tNorm, derivedMatrix, StringComparison.OrdinalIgnoreCase);
        // 跨族（判定表第 7 行）：推导已标注（非 null/空——MatrixTokenFor 只产出 null 不产空串，
        // IsNullOrEmpty 是对「某路径写空串」的防御，空串语义上同为未标注）且既非同值又非 601
        // 互通 ⇒ 契约拒绝。推导 null/空（primaries 未标注/CodecDefault）⇒ 无交叉基准，放行
        // Carried（判定表第 5 行），Reason 在 Carried 既有文本之后追加点名。
        bool cmxCrossFamily = cmxParticipates && !string.IsNullOrEmpty(derivedMatrix) && !cmxSameValue && !cmxSame601;

        switch (cap)
        {
            case OutMatrixCapability.Carried:
                // ── 交叉一致性校验闸（CMX 批第二道闸，A1 严口径）──
                // 承载判定（ResolveOutMatrixCapability）只回答「该出口能否承载此矩阵」；本闸回答
                // 「显式值与 primaries 推导链留下的标注是否自洽」：Carried 档只覆盖 p.OutMatrix、
                // 不动 primaries 推导，跨族显式值会产出 primaries=X + matrix=Y 的跨维不一致标注
                // （如 BT.2020 源 + 显式 bt709 ⇒ primaries=9 + matrix=1），消费端从 matrix 反推
                // primaries ⇒ 广色域被错读为窄色域（2026-11-11 用户裁决「禁止乱塞入」）。
                if (cmxCrossFamily)
                {
                    // 裁决字段改 Rejected 与 Action 自洽（本格 cap 原是 Carried，由本闸翻成 Rejected）；
                    // 不覆盖 p.OutMatrix（显式值未获采纳，推导值保留——Reject 计划本就不出产物）。
                    p.OutMatrixVerdict = OutMatrixCapability.Rejected;
                    // 模板 4 ASCII 由规划层发（同既有 Rejected 分支——出口不被调用 ⇒ T6 无机会发）；
                    // 中文同义句进 Reason；exit 91 由 Action=Reject 的既有执行机制兑现。
                    ImageEncoderArgs.EmitWarn(string.Format(OutMatrixReportConflict, token, derivedMatrix, fmt));
                    var cmxZh = $"显式值与目标 primaries 推导矩阵 {derivedMatrix} 不一致 ⇒ 契约拒绝（exit 91、不产出）"
                              + "——输出矩阵随 primaries 推导，显式值仅可在推导值同族（601 族互通）内选择";
                    if (p.Action == ColorAction.Reject)
                    {
                        // 主体已 Reject（PlanWithPolicy 提前返回）：只追加，不覆盖主体拒绝原因。
                        p.Reason += $"｜用户 --color-out-matrix={token} 与推导矩阵 {derivedMatrix} 不一致（{cmxZh}）";
                    }
                    else
                    {
                        Reject(p,
                            $"输出矩阵 {token} 与目标 primaries 推导矩阵 {derivedMatrix} 不一致：{cmxZh}",
                            "显式 --color-out-matrix 仅可选推导矩阵同值或 601 族（bt470bg/smpte170m）成员；不指定（auto）则沿用推导值");
                        // Reject 助手不清这两位（其既有调用点都在 Classify* 重算之前）；
                        // 本调用点在重算之后 ⇒ 手动补齐（与既有 Rejected 分支同款）。
                        p.Annot = AnnotChoice.None;
                        p.ColorLoss = ColorLoss.None;
                    }
                    break;
                }
                // H.273 族 ⇒ 覆盖推导值（C-PLAN-EXT@1：显式值在推导完成后覆盖 p.OutMatrix）；
                // xyb ⇒ 不覆盖（裁定见方法注释），T6 按 OutMatrixMode 落地开关。
                if (token != "xyb") p.OutMatrix = token;
                p.Reason += $"｜用户 --color-out-matrix={token} 已由 {fmt} 出口承载"
                          + $"（{OutMatrixCarriedLandingNote(fmt, token, backend)}；出口侧落地与播报见执行层）";
                // 推导 null/空（primaries 未标注/CodecDefault）⇒ 放行，追加点名（判定表第 5 行）：
                // 显式矩阵以用户值为准（中文进 Reason 既有通道；IsNullOrEmpty 与 cmxCrossFamily 同口径）。
                if (cmxParticipates && string.IsNullOrEmpty(derivedMatrix))
                    p.Reason += "｜源 primaries 未标注，显式矩阵以用户值为准";
                break;
            case OutMatrixCapability.Redundant:
                p.Reason += $"｜用户 --color-out-matrix={token} on {fmt}：容器/出口已按同值处理"
                          + "（冗余，仅登记不生效，产物与 auto 档逐字节一致）";
                break;
            case OutMatrixCapability.Rejected:
            {
                var (whyZh, whyAscii) = OutMatrixRejectionWhy(fmt, token, backend);
                // 模板 3 ASCII 由规划层发（出口不被调用 ⇒ T6 无机会发）；中文同义句进 Reason；
                // exit 91 由 Action=Reject 的既有执行机制兑现，本层不直接设退出码。
                ImageEncoderArgs.EmitWarn(string.Format(OutMatrixReportRejected, token, fmt, whyAscii));
                if (p.Action == ColorAction.Reject)
                {
                    // 主体已 Reject（PlanWithPolicy 提前返回）：只追加，不覆盖主体拒绝原因。
                    p.Reason += $"｜用户 --color-out-matrix={token} 不被 {fmt} 出口承载（{whyZh}）"
                              + "⇒ 契约拒绝，exit 91，不产出";
                }
                else
                {
                    Reject(p,
                        $"输出矩阵 {token} 不被 {fmt} 出口承载（{whyZh}）⇒ 契约拒绝（exit 91、不产出）",
                        "该格式的承载矩阵见 --help 的 --color-out-matrix 说明；不指定（auto）则沿用现有推导");
                    // Reject 助手不清这两位（其既有调用点都在 Classify* 重算之前）；
                    // 本调用点在重算之后 ⇒ 手动补齐，保证裁决字段与 Action 自洽。
                    p.Annot = AnnotChoice.None;
                    p.ColorLoss = ColorLoss.None;
                }
                break;
            }
        }
    }

    /// <summary>
    /// Carried 档规划层 Reason 的落地方向注记（T1 §1 表逐格背书；「落地描述」的**精确文本**
    /// 由 T6 出口填——本层只给方向，不抢出口的播报职责）。
    /// </summary>
    private static string OutMatrixCarriedLandingNote(string fmt, string token, EncoderBackend backend) => (fmt, token) switch
    {
        ("avif", "rgb") =>
            "nclx matrix=0（libaom gbrp 直喂语义自洽；libsvtav1 无 gbrp，该收窄归出口侧编码器维度）",
        ("avif", _) =>
            "nclx matrix_coefficients 可写 + 输出侧 -colorspace 驱动 RGB→YUV 转换；引擎通路需配合 YUV pix_fmt（pixfmt-switch 由出口落地）",
        ("jxl", "xyb") => backend == EncoderBackend.Cjxl
            ? "cjxl VarDCT 隐式承载（v0.12.0 无显式开关；--jxl-modular 开启时 modular 优先，冲突点名见 cjxl 出口播报）"
            : "libjxl -xyb（默认已开，显式即确认）",
        _ => "转换矩阵/标注/编码器开关由执行层按出口落地点写入",
    };

    /// <summary>
    /// Rejected 档的原因短语（中文进 Reason、ASCII 进 C-REPORT@1 模板 3 的 EmitWarn——
    /// 逐格对应 <see cref="ResolveOutMatrixCapability"/> 的判定注释与 T1 扫描报告 evidence）。
    /// </summary>
    private static (string Zh, string Ascii) OutMatrixRejectionWhy(string fmt, string token, EncoderBackend backend)
    {
        var t = token.Trim();
        var f = (fmt ?? "").Trim().ToLowerInvariant();
        bool yuv = t is "bt470bg" or "smpte170m" or "bt709" or "smpte240m" or "bt2020nc" or "fcc" or "ycgco";

        if (f is "jpg" or "jpeg" or "jpegli")
        {
            if (backend == EncoderBackend.Cjpegli)
                return ("cjpegli 从 RGB 直编，无 YUV 转换发生点（矩阵选择不暴露为 CLI 参数）",
                        "cjpegli encodes from RGB directly; no YUV conversion point");
            return t switch
            {
                "rgb" => ("mjpeg 编码器仅收 YUV pix_fmt（yuvj420p/422p/444p 族）",
                          "mjpeg encoder accepts YUV pixel formats only (yuvj420p/422p/444p)"),
                "xyb" => ("mjpeg 出口无 XYB 概念", "mjpeg exit has no XYB concept"),
                _ => ("转换系数偏离解码端固定 BT.601 假设 ⇒ 饱和色偏（2026-10-25 实测纯红 255,0,0→232,0,1）；JFIF 无矩阵标注位",
                      "conversion coefficients would deviate from the decoder's fixed BT.601 assumption (2026-10-25 measured 255,0,0 -> 232,0,1); JFIF has no matrix annotation"),
            };
        }
        if (f == "webp")
        {
            return t switch
            {
                "rgb" => ("libwebp 有损路径仅 yuv420p（无损路径 bgra 是编码器内部表示而非矩阵语义）",
                          "libwebp lossy path is yuv420p only; bgra (lossless) is an internal representation, not a matrix semantic"),
                "xyb" => ("libwebp 出口无 XYB 概念", "libwebp exit has no XYB concept"),
                _ => ("WebP 容器无矩阵标注位，转换偏离解码端固定 BT.601 假设 = 人为色偏（对齐 2026-11-03 colorRange 出口侧口径）",
                      "WebP container has no matrix annotation; conversion would mismatch the decoder's fixed BT.601 assumption (per 2026-11-03 colorRange policy)"),
            };
        }
        if (f == "avif")
        {
            if (backend != EncoderBackend.Ffmpeg)
                return ("avif 出口由 ffmpeg 编码器承载；avifenc 两步法现状无矩阵通路",
                        "avif exit is carried by ffmpeg encoders; avifenc two-step has no matrix path wired");
            return t switch
            {
                "ycgco" => ("swscale 对 ycgco 的转换能力被输入侧实测否定（Impossible to convert），输出侧未证 ⇒ 保守拒绝",
                            "swscale conversion for ycgco proven impossible on the input side; output side unverified => rejected conservatively"),
                "fcc" => ("AV1 bitstream matrix_coefficients 枚举 3/4 为 Reserved，nclx 写不进",
                          "AV1 matrix_coefficients enum 3/4 are Reserved; nclx cannot carry it"),
                "xyb" => ("AV1/AVIF 无 XYB 概念", "AV1/AVIF has no XYB concept"),
                _ => ("avif 出口不承载该矩阵", "avif exit does not carry this matrix"),
            };
        }
        if (f == "jxl")
        {
            return ("JXL 是 RGB 原生容器（RgbNative），无 YUV 承载位",
                    "JXL is an RGB-native container (RgbNative); no YUV carrier");
        }
        if (f is "dng" or "jxr")
        {
            return yuv
                ? ("该出口的矩阵是契约外概念（DNG ColorMatrix1/2=相机校准矩阵 / JXR 内部 YCoCg 不暴露），无 H.273 承载",
                   "matrix here is outside this contract (DNG ColorMatrix1/2 = camera calibration; JXR internal YCoCg not exposed)")
                : ("该出口无 XYB 概念", "this exit has no XYB concept");
        }
        if (yuv)
        {
            return ("RGB 原生/调色板容器无 YUV 矩阵概念",
                    "RGB-native/palette container has no YUV matrix concept");
        }
        return ("未知格式/出口组合，fail-closed", "unknown format/exit combination; fail-closed");
    }
}
