using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using Tests.Shared;
using FfmpegGui.Services;
using TC = FfmpegGui.Services.ColorMapping.TransferCurve;
using CK = FfmpegGui.Services.ColorMapping.ColorKernels;
using CM = FfmpegGui.Services.ColorMapping.ColorMetrics;
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
using CP = FfmpegGui.Services.ColorMapping.ColorTransformPlan;
using GF = FfmpegGui.Services.ColorMapping.RawGamutFit;
using VK = FfmpegGui.Services.ColorMapping.VerdictKind;
using DG = FfmpegGui.Services.ColorMapping.DegradationCodes;
using F709 = FfmpegGui.Services.ColorMapping.Transfer709Curve;
using AAS = FfmpegGui.Services.AppSettingsService;

namespace Tests.Core
{
    /// <summary>
    /// 子系统测试组：**core**（决策层：契约 / 裁决 / 计划 / 路由 / 落盘）—— 2026-10-05 从
    /// ServiceProbe 的单个 577 KB `Program.cs` 中物理拆出，使"改决策层这一块"只需跑本工程，
    /// 不再跑整个宿主。
    ///
    /// <para><b>覆盖的 mode</b>：`contract` `verdict` `plan` `decision` `wire` `runner` `settings`
    /// （见 <see cref="TestGroups"/>）。</para>
    ///
    /// <para><b>抽取规则</b>：方法体**逐字**从旧宿主搬来，字符串一个字未改（门禁在 grep 那些文本）。
    /// 共享 helper 一律转发 <see cref="Harness"/>，不复制实现。仅 `Dump` 与 `Runner` 探针
    /// 一同搬来（全仓只有它一个调用者）。</para>
    /// </summary>
    internal static class CoreGroup
    {
        /// <summary>
        /// <c>Check</c> 转发到共享 `Harness`。
        /// ⚠ **不是复制实现**：计数与输出格式只有 `Harness` 一份（否则各组会漂移出各自的
        /// PASS/FAIL 口径，本仓已有"两处独立实现漂移"的教训）。保留本地同名包装，
        /// 只为让**拆出的原代码一字不改**（降低拆分引入差异的风险）。
        /// </summary>
        private static void Check(bool ok, string msg) => Harness.Check(ok, msg);

        internal static void RunContract() => ProbeContract();
        internal static void RunVerdict() => ProbeVerdict();
        internal static void RunPlan(string[] args) => ProbePlan(args);
        internal static void RunDecision() => ProbeDecision();
        internal static void RunWire() => ProbeWire();
        internal static void RunRunner(string[] args) => ProbeRunnerGuard(args).GetAwaiter().GetResult();
        internal static void RunSettingsSave() => ProbeSettingsSave();

        // ══════════════════════════════ plan ══════════════════════════════

        /// <summary>
        /// plan —— 引擎决策表审计（P2 可行性门禁）：典型 src/dst/格式组合必须给出预期动作与后端。
        /// 额外：用 iccgen(lcms2) 生成的真实 ICC 验证“读 ICC → 归入已知空间 + 曲线解析”正确。
        /// </summary>
        private static void ProbePlan(string[] args)
        {
            var pol = new FfmpegGui.Services.ColorMapping.PlanPolicy { AllowApproximateCurve = false };

            void Case(string what, FfmpegGui.Services.ColorMapping.ColorAction act,
                      FfmpegGui.Services.ColorMapping.ColorBackend? backend, bool expectMatrix,
                      FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor? s,
                      FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor? d,
                      FfmpegGui.Services.ColorMapping.PlanPolicy? p = null)
            {
                var pl = FfmpegGui.Services.ColorMapping.ColorMappingEngine.Plan(s, d, p ?? pol);
                pl.Src = s; pl.Dst = d;
                Console.WriteLine("  " + pl.ToLogLine());
                Check(pl.Action == act, $"{what}: act={pl.Action} 预期 {act}（{pl.Reason}）");
                if (backend.HasValue) Check(pl.Backend == backend.Value, $"{what}: backend={pl.Backend} 预期 {backend.Value}");
                if (expectMatrix) Check(pl.Matrix != null, $"{what}: 应产出非恒等矩阵");
                else Check(pl.Matrix == null, $"{what}: 不应有矩阵（恒等/直通）");
            }

            var srgb = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor.FromCicp("bt709", "iec61966-2-1", "sRGB");
            var p3 = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor.FromCicp("smpte432", "iec61966-2-1", "Display P3");
            var rec2020 = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor.FromCicp("bt2020", "bt709", "BT.2020 SDR");
            var pq2020 = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor.FromCicp("bt2020", "smpte2084", "BT.2100 PQ",
                nitsOfLinearOne: FfmpegGui.Services.ColorMapping.TransferCurve.PqPeakNits);
            var prophoto = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor.FromNamedSpace("ProPhoto RGB");
            var adobe = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor.FromNamedSpace("Adobe RGB");

            Check(srgb != null && p3 != null && rec2020 != null && pq2020 != null && prophoto != null && adobe != null,
                "全部测试描述符可构造");
            if (srgb == null || p3 == null || rec2020 == null || pq2020 == null || prophoto == null || adobe == null) return;

            Case("sRGB→sRGB(png) 应直通", FfmpegGui.Services.ColorMapping.ColorAction.None, null, false, srgb, srgb);
            // ⚠⚠ 2026-10-05：H2 死后端已**删除**（`ColorTransformPlan` 后端选择处有长注释）⇒
            //   原先「两侧可命名 ⇒ FfmpegFilter(H2)」这两条契约**随执行体一起作废**。
            //   改判**新契约**：两侧可命名也一律 `InProcess`（H1）—— 因为**没有 H2 执行体**。
            //   ⚠ 用例名同步去掉 "→H2"，避免名字与断言再次脱钩（本仓"名字说 A、断言判 B"是踩过的坑）。
            Case("sRGB→P3 两侧可命名→H1(H2 已删除)", FfmpegGui.Services.ColorMapping.ColorAction.Map,
                FfmpegGui.Services.ColorMapping.ColorBackend.InProcess, true, srgb, p3);
            Case("PQ2020→sRGB 可命名→H1(H2 已删除)", FfmpegGui.Services.ColorMapping.ColorAction.Map,
                FfmpegGui.Services.ColorMapping.ColorBackend.InProcess, true, pq2020, srgb);
            Case("ProPhoto→sRGB 不可命名→H1", FfmpegGui.Services.ColorMapping.ColorAction.Map,
                FfmpegGui.Services.ColorMapping.ColorBackend.InProcess, true, prophoto, srgb);
            Case("Adobe(γ2.2)→BT.2020 不可命名→H1", FfmpegGui.Services.ColorMapping.ColorAction.Map,
                FfmpegGui.Services.ColorMapping.ColorBackend.InProcess, true, adobe, rec2020);
            Case("源不可识别→拒绝", FfmpegGui.Services.ColorMapping.ColorAction.Reject, null, false, null, srgb);

            // 无目标：不改像素，沿用源描述
            var pAuto = FfmpegGui.Services.ColorMapping.ColorMappingEngine.Plan(srgb, null, pol);
            Check(pAuto.Action == FfmpegGui.Services.ColorMapping.ColorAction.None, "dst=null → 不映射");

            // 曲线一致性：引擎必须把 Adobe 当 2.2、ProPhoto 当 1.8（而不是 sRGB 凑）
            Check(adobe.Curve.Name == "gamma2.2", $"Adobe 曲线识别为 2.2（实为 {adobe.Curve.Name}）");
            Check(Math.Abs(prophoto.Curve.G - 1.8) < 1e-12 && prophoto.Curve.HasToeSegment,
                "ProPhoto 曲线为 1.8 + 线性趾");
            Check(Math.Abs(adobe.Curve.ToLinear(0.5) - Math.Pow(0.5, 2.2)) < 1e-12,
                "Adobe 中间调按 2.2 解码（旧实现错用 sRGB 曲线会得 0.2140）");
            double srgbMid = FfmpegGui.Services.ColorMapping.TransferCurve.Srgb.ToLinear(0.5);
            Check(Math.Abs(adobe.Curve.ToLinear(0.5) - srgbMid) > 3e-3,
                $"γ2.2 与 sRGB 曲线确实不同（差={Math.Abs(adobe.Curve.ToLinear(0.5) - srgbMid):F5}）→ 证明旧近似有误差");

            // 标注规则：HDR 不附 ICC（iccgen 不支持 HDR）；SDR 附 ICC + CICP 双描述
            var pHdr = FfmpegGui.Services.ColorMapping.ColorMappingEngine.Plan(pq2020,
                FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor.FromCicp("bt2020", "smpte2084", "BT.2100 PQ"),
                new FfmpegGui.Services.ColorMapping.PlanPolicy
                { Format = FfmpegGui.Services.ColorMapping.ColorMappingEngine.CapsFor("avif"), AllowApproximateCurve = false, HdrOutput = true });
            Check(!pHdr.AttachIcc || pHdr.IccTrc == null, $"HDR 目标不附 HDR ICC（iccgen 不支持）: {pHdr.Reason}");
            var pSdr = FfmpegGui.Services.ColorMapping.ColorMappingEngine.Plan(srgb, rec2020,
                new FfmpegGui.Services.ColorMapping.PlanPolicy { Format = FfmpegGui.Services.ColorMapping.ColorMappingEngine.CapsFor("avif"), AllowApproximateCurve = false });
            Check(pSdr.OutPrimaries == "bt2020" && pSdr.OutTransfer != null, "SDR 目标产出 CICP 标注");

            // 真实 ICC（lcms2/iccgen 产物）：读色度 + 曲线，并归入已知空间
            var ff = FfmpegGui.Services.AppSettingsService.Current.FfmpegPath;
            if (!string.IsNullOrWhiteSpace(ff) && System.IO.File.Exists(ff))
            {
                foreach (var (tag, prim, trc, mtx, wantTok) in new[]
                {
                    ("srgb", "bt709", "iec61966-2-1", "bt709", "bt709"),
                    ("p3", "smpte432", "iec61966-2-1", "bt709", "smpte432"),
                    ("b2020", "bt2020", "iec61966-2-1", "bt2020nc", "bt2020"),
                })
                {
                    // ⚠ 2026-09-17 修根因：这里原先嵌的 1×1 PNG base64 是**坏的**（IDAT 解不开，
                    //   ffmpeg 报 `[png] inflate returned error -3`）⇒ 解码阶段就失败、不产出中间
                    //   PNG ⇒ `GetOrGenerateStandardIcc` 恒返回 null。于是本段三条 tag 的断言
                    //   **只能靠 `%TEMP%/stdicc_*.icc` 缓存蒙对**：缓存被清 ⇒ `plan` 立刻 34/1，
                    //   唯一红行 `iccgen 未产出 b2020 ICC`（另有 5 条子断言被 `continue` 跳过）。
                    //   ⇒ 这是"门禁的隐式前置条件"，**不是**代码回归，也**不是**"iccgen 生不出
                    //   bt2020/iec61966-2-1/bt2020nc"：实测换有效载体后 bt709 / smpte432 / bt2020
                    //   三组都能现场生成（`iccname` 模式走 carrier=null 的 lavfi 兜底，故它一直是绿的）。
                    //   故根因修在载体上。下面的 base64 是 ffmpeg 现场产出的**有效** 1×1 PNG（90 B）。
                    var carrier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cms_{Guid.NewGuid():N}.png");
                    System.IO.File.WriteAllBytes(carrier, Convert.FromBase64String(
                        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAACXBIWXMAAAABAAAAAQBPJcTWAAAADElEQVR4nGNsaGgAAAMIAYKGLCvCAAAAAElFTkSuQmCC"));
                    var icc = FfmpegGui.Services.IccProfileService.GetOrGenerateStandardIcc(carrier, prim, trc, mtx);
                    if (icc == null) { Check(false, $"iccgen 未产出 {tag} ICC"); continue; }
                    var d2 = FfmpegGui.Services.ColorMapping.ColorSpaceIdentify.FromIccFile(icc, tag);
                    Check(d2 != null, $"ICC({tag}) 可解析为描述符");
                    if (d2 == null) continue;
                    // 诊断：输出 ICC 实测矩阵与理论矩阵的逐元素差（定位“归不入已知空间”的真因）
                    var wantM = FfmpegGui.Services.ColorSpaceRegistry.ColorantsD50(wantTok);
                    if (d2.RgbToXyzD50 != null && wantM != null)
                    {
                        var ci9 = System.Globalization.CultureInfo.InvariantCulture;
                        Console.WriteLine($"    ICC({tag}) 实测=[{string.Join(",", Array.ConvertAll(d2.RgbToXyzD50, v => v.ToString("0.0000", ci9)))}]");
                        Console.WriteLine($"    理论({wantTok})=[{string.Join(",", Array.ConvertAll(wantM, v => v.ToString("0.0000", ci9)))}] name={d2.Name}");
                    }
                    Check(d2.PrimariesToken == wantTok, $"ICC({tag}) 归入已知空间 token={wantTok}（实为 {d2.PrimariesToken ?? "未识别"}）");
                    Check(d2.Curve.Equivalent(FfmpegGui.Services.ColorMapping.TransferCurve.Srgb)
                          || d2.Curve.Kind == FfmpegGui.Services.ColorMapping.TransferCurve.CurveKind.Table,
                        $"ICC({tag}) 曲线解析为 sRGB 参数式（实为 {d2.Curve.Name} " +
                        $"g={d2.Curve.G:F6} a={d2.Curve.A:F6} b={d2.Curve.B:F6} c={d2.Curve.C:F6} d={d2.Curve.D:F6} e={d2.Curve.E:F6} f={d2.Curve.F:F6}；" +
                        $"本方 Srgb g={FfmpegGui.Services.ColorMapping.TransferCurve.Srgb.G:F6} a={FfmpegGui.Services.ColorMapping.TransferCurve.Srgb.A:F6} b={FfmpegGui.Services.ColorMapping.TransferCurve.Srgb.B:F6} c={FfmpegGui.Services.ColorMapping.TransferCurve.Srgb.C:F6} d={FfmpegGui.Services.ColorMapping.TransferCurve.Srgb.D:F6}）");
                    var back = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor.FromCicp(prim, trc, tag);
                    var m = d2.MatrixTo(back);
                    Check(m != null, $"ICC({tag}) 与同空间 CICP 描述符可互映射（矩阵可逆且白点守恒）");
                    if (m != null)
                    {
                        double err = 0;
                        for (int i = 0; i < 9; i++) err = Math.Max(err, Math.Abs(m[i] - (i % 4 == 0 ? 1.0 : 0.0)));
                        Console.WriteLine($"    ICC({tag}) → CICP({prim}) 矩阵与单位阵偏差 max={err:E2}");
                        Check(err <= 1e-3, $"ICC({tag}) 与 CICP({prim}) 实质同一空间（≤1e-3）");
                    }
                    try { System.IO.File.Delete(carrier); } catch { }
                }
            }
            else Console.WriteLine("  SKIP iccgen 互测（未找到 ffmpeg）");
        }

        // ══════════════════════════════ contract ══════════════════════════════

        /// <summary>
        /// contract —— 四策略契约自检（规划 §6 C1–C6）：枚举 策略×容器×源 全格，
        /// 断言以 <b>EffectiveStrategy</b> 为准；带 OverrideReason 的覆盖格豁免结局断言（但必须有说明）。
        /// </summary>
        private static void ProbeContract()
        {
            CS Mk(string prim, string trc, string name, string? icc, DC conf)
            {
                var d = CS.FromCicp(prim, trc, name)!;
                if (icc != null) { d.SourceIccPath = icc; d.IsIccDerived = true; }
                d.Confidence = conf;
                return d;
            }
            var srgbTag = Mk("bt709", "iec61966-2-1", "sRGB(仅标签)", null, DC.CicpTag);
            var srgbIcc = Mk("bt709", "iec61966-2-1", "sRGB(带ICC)", "t.srgb.icc", DC.IccMetric);
            var p3Icc = Mk("smpte432", "iec61966-2-1", "Display P3(带ICC)", "t.p3.icc", DC.IccMetric);
            var pq2020 = CS.FromCicp("bt2020", "smpte2084", "BT.2100 PQ", nitsOfLinearOne: TC.PqPeakNits)!;
            pq2020.Confidence = DC.CicpTag;
            var adobe = CS.FromNamedSpace("Adobe RGB")!; adobe.SourceIccPath = "t.adobe.icc"; adobe.Confidence = DC.IccMetric;
            var prophoto = CS.FromNamedSpace("ProPhoto RGB")!; prophoto.SourceIccPath = "t.prophoto.icc"; prophoto.Confidence = DC.IccMetric;
            var untagged = FfmpegGui.Services.ColorMapping.ColorSpaceIdentify.AssumeByConvention()!;
            // HLG 必须单独成行：它是 scene-referred，与 PQ（display-referred 绝对 nits）的处理路径不同，
            // 共用一行会掩盖“缺 OOTF”这类系统性错误。
            var hlg2020 = CS.FromCicp("bt2020", "arib-std-b67", "BT.2100 HLG", nitsOfLinearOne: 1000.0)!;
            hlg2020.Confidence = DC.CicpTag;

            var sources = new (string Name, CS D)[]
                { ("sRGB仅标签", srgbTag), ("sRGB带ICC", srgbIcc), ("P3带ICC", p3Icc), ("HDR-PQ", pq2020), ("HDR-HLG", hlg2020), ("Adobe带ICC", adobe), ("无标签", untagged) };
            var formats = new[] { "png", "jpg", "tiff", "webp", "avif", "jxl", "gif", "heic" };
            var strategies = new[] { ST.Recommended, ST.CarryIcc, ST.BakeCicpOnly, ST.Manual };

            var viol = new System.Collections.Generic.List<string>();
            var rejectReasons = new System.Collections.Generic.Dictionary<string, int>();
            var rejectByStrategy = new System.Collections.Generic.Dictionary<ST, int>();
            int cells = 0, exempted = 0, rejects = 0;

            CI MakeIntent(ST st, CS src, string fmt, CSC scope)
            {
                var it = new CI
                {
                    Strategy = st, Source = src, Format = CE.CapsFor(fmt), CarryScope = scope,
                    TargetBitDepth = 8, AllowApproximateCurve = false,
                    Manual = st == ST.Manual ? MI.Create(true, "Display P3", "smpte432", "iec61966-2-1", null)
                                             : MI.Create(false, null, null, null, null),
                };
                if (st == ST.Manual) { it.TargetExplicit = true; it.Target = p3Icc; }
                return it;
            }

            foreach (var st in strategies)
                foreach (var fmt in formats)
                    foreach (var (sname, src) in sources)
                    {
                        var it = MakeIntent(st, src, fmt, CSC.All);
                        var p = CE.Plan(it);
                        cells++;
                        var caps = CE.CapsFor(fmt);
                        bool ex = p.Override != OR.None;
                        if (ex) exempted++;
                        if (p.Action == CA.Reject)
                        {
                            rejects++;
                            rejectByStrategy[st] = rejectByStrategy.GetValueOrDefault(st) + 1;
                            var rk = p.Reason.Length > 58 ? p.Reason[..58] : p.Reason;
                            rejectReasons[rk] = rejectReasons.GetValueOrDefault(rk) + 1;
                        }
                        void V(bool ok, string msg) { if (!ok) viol.Add($"[{st}/{fmt}/{sname}] {msg}"); }

                        // 通则：拒绝必带建议；ColorLoss=None 不得伴随像素变更
                        V(p.Action != CA.Reject || p.Advice.Length > 0, "Reject 无 Advice");
                        V(p.ColorLoss != CL.None || (p.Matrix == null && !p.NeedsCurveChange), "ColorLoss=None 却有矩阵/曲线变更");
                        V(p.Annot != AC.IccAndCicp || caps.CanHaveBothIccAndCicp, "标为 ICC+CICP 双描述但容器不支持共存");
                        if (p.Action != CA.Reject)
                            V(p.AttachIcc || p.OutPrimaries != null || p.UnlabeledAssumedSrgb, "产出行无任何标注也未记 UnlabeledAssumedSrgb");
                        if (ex) V(p.Reason.Length > 0, "覆盖格缺覆盖说明（禁止偷偷覆盖）");
                        // 不变量：**同时声明了** CICP 与自生成 ICC 时，两者必须用同一条 TRC。
                        // ⚠ 已用变异测试证明：在 224 格里它是**空转的**（把 IccTrc 改回写死 sRGB 仍 95/0），
                        //   因为所有生成 ICC 的格子要么被“ICC 优先”清了 Out*、要么 IccTrc 为 null。
                        //   它只当未来变更的绊线；本 bug 的真正探测器是 verify-color-wiring (6)（已同样用变异验证过）。
                        // 只管“两条都声明”的行：故意只声明 ICC、不写 CICP 的行（容器非规范性，
                        // ColorStrategyPlanning 在 ICC 定好后清空 Out*）属合法，不构成矛盾。
                        if (p.AttachIcc && p.IccPath == null && p.IccFromGenerator
                            && p.IccTrc != null && p.OutTransfer != null)
                            V(string.Equals(p.IccTrc, p.OutTransfer, StringComparison.OrdinalIgnoreCase),
                              $"ICC 的 TRC({p.IccTrc}) 与输出 TRC({p.OutTransfer}) 不一致");

                        // C1/C2：S2 生效格（含覆盖格）永不改像素、永不生成 ICC
                        if (p.EffectiveStrategy == ST.CarryIcc)
                        {
                            V(p.Backend == CB.None && p.Matrix == null && !p.NeedsCurveChange && !p.IccFromGenerator,
                              "S2 生效格出现像素变更或生成 ICC");
                            V(p.Action is CA.CarryIcc or CA.None or CA.Reject, "S2 出现非法 Action");
                            V(!p.AttachIcc || p.IccPath != null, "S2 附着了非携带的 ICC");
                            // S2 的 Action=None 必须属于两种零改动形态之一（继承 CICP / 隐含 sRGB 无标注）
                            if (p.Action == CA.None)
                                V(p.UnlabeledAssumedSrgb || p.Annot == AC.CicpOnly,
                                  "S2 的 None 必须是「继承源 CICP」或「隐含 sRGB 无标注」");
                            if (p.UnlabeledAssumedSrgb)
                                V(!caps.HasColorPath || caps.UnlabeledMeansSrgb || it.AllowUnlabeled,
                                  "S2 无标注直通，但容器有色彩通路却不允许未标注");
                        }
                        // C2/C3：S3 绝不输出 ICC；无 CICP 容器上只能 None/Reject；可 CICP 容器上必须真写了 CICP
                        if (p.EffectiveStrategy == ST.BakeCicpOnly)
                        {
                            V(!p.AttachIcc && !p.IccFromGenerator && !p.UnlabeledAssumedSrgb || p.UnlabeledAssumedSrgb && !p.AttachIcc,
                              "S3 输出了 ICC");
                            if (!caps.CanCicp && !ex)
                            {
                                V(p.Action is CA.None or CA.Reject, "S3 在无 CICP 容器上映射了像素");
                                if (p.Action == CA.None)
                                    V(caps.UnlabeledMeansSrgb || it.AllowUnlabeled || !caps.HasColorPath,
                                      "S3 在无 CICP 容器上无标注直通，但容器既有色调通路又不在白名单（TIFF 类）");
                            }
                            if (caps.CanCicp && p.Action != CA.Reject && !ex)
                                V(p.OutPrimaries != null, "S3 在可 CICP 容器上却没写 CICP");
                        }
                        // S1：携带与否必须与 CarryScope 定义一致（此处 scope=All）
                        if (p.EffectiveStrategy == ST.Recommended && p.Action == CA.CarryIcc)
                            V(caps.CanCarryArbitraryIcc && src.SourceIccPath != null, "S1 携带了不存在的 ICC/容器不支持");
                    }

            Check(viol.Count == 0, $"contract C1–C4: {cells} 格（覆盖豁免 {exempted} 格、拒绝 {rejects} 格）零违规"
                  + (viol.Count > 0 ? " 例如: " + string.Join(" | ", viol.ConvertAll(s => s).GetRange(0, Math.Min(12, viol.Count))) : ""));

            // HEIF 专项：本工具链**无写入通路**（实测 Unknown format 'heif'），能力表必须全保守；
            // 色彩层对其与 gif/jxr 同类：无色彩通路 ⇒ 只允许“直通+无标注”或 Reject，绝不得声称描述了广色域。
            foreach (var f in new[] { "heic", "heif" })
            {
                var capsH = CE.CapsFor(f);
                Check(!capsH.CanCarryArbitraryIcc && !capsH.CanCicp && !capsH.CanGenerateIccFromCicp,
                    $"{f} 能力表为保守（无写入通路，实测：ffmpeg Unknown format 'heif'）");
                Check(!capsH.HasColorPath, $"{f} 被认定无任何色彩管理通路（HasColorPath=false）");
                // 广色域源：不得产出“看起来标了 P3/2020”的结果
                var pWide = CE.Plan(new CI { Strategy = ST.Recommended, Source = p3Icc, Format = capsH });
                Check(pWide.Action == CA.Reject || pWide.OutPrimaries == null,
                    $"{f} 广色域源不得静默产出厂色域标注（实为 {pWide.Action}/p={pWide.OutPrimaries}）");
                // S2/S3 在无任何通路的容器上只能 Reject 或无标注直通（不能映射）
                var pS2 = CE.Plan(new CI { Strategy = ST.CarryIcc, Source = p3Icc, Format = capsH });
                Check(pS2.Action == CA.Reject || pS2.Matrix == null,
                    $"{f} + S2：要么 Reject 要么像素零改动（实为 {pS2.Action}）");
            }

            // C4c HLG 专项：**无 HDR 通路**的容器上不得“默默按 PQ 方式压暗”，必须 Reject；
            // 确实请求 tonemap 时，必须把“OOTF 未标定”写进建议（已知缺口可见，而不是隐性错）。
            // ⚠ **2026-09-16 契约更新**：`png`/`apng` **有** HDR 通路（经 **cICP**）——
            //   两条管线的 HDR→PNG 产物实测都写着 `smpte2084,bt2020`，且**逐字节相同**。
            //   故它们不再属于"无 HDR 通路"组，改列正向断言（否则本断言锁的是过时的能力表）。
            foreach (var f in new[] { "tiff", "webp", "gif" })
            {
                var hp = CE.Plan(new CI { Strategy = ST.Recommended, Source = hlg2020, Format = CE.CapsFor(f) });
                Check(hp.Action == CA.Reject, $"HLG × {f}：无 HDR 通路 ⇒ Reject（实为 {hp.Action}）");
            }
            foreach (var f in new[] { "png", "apng" })
            {
                var hp = CE.Plan(new CI { Strategy = ST.Recommended, Source = hlg2020, Format = CE.CapsFor(f) });
                Check(hp.Action != CA.Reject, $"HLG × {f}：有 cICP 通路 ⇒ 不得 Reject（实为 {hp.Action}）");
                Check(CE.CapsFor(f).CanHdr, $"{f} 的能力表须标 CanHdr=true（HDR 经 cICP 承载）");
            }
            foreach (var f in new[] { "avif", "jxl" })
            {
                var hh = CE.Plan(new CI { Strategy = ST.Recommended, Source = hlg2020, Format = CE.CapsFor(f) });
                Check(hh.Matrix == null && hh.ColorLoss == CL.None,
                    $"HLG × {f}：默认保持 HDR、不改像素（实为 {hh.Action}/{hh.ColorLoss}）");
                var ht = CE.Plan(new CI { Strategy = ST.Recommended, Source = hlg2020, Format = CE.CapsFor(f),
                                           HdrOutput = false, ToneMapRequested = true, ToneMap = FfmpegGui.Services.ColorMapping.ToneMapMode.Mobius });
                Check(ht.Tone is { Active: true } && ht.Advice.Contains("OOTF"),
                    $"HLG × {f} + 请求 tonemap：必须声明 OOTF 未标定（Advice={ht.Advice}）");
            }

            // C4b GainMap 专项：它是“输出结构”能力（JPEG 的 APP2/MPF，或 AVIF 的 ISO-BMFF `tmap` 派生项），
            // 不是色调曲线；UI 的 IsVisible 不算门禁——契约层必须自拦，否则变成“以为有 HDR、实则被压暗”的静默降级。
            // ⚠ 2026-10-02：**avif 已移出本负控清单**（tmap 派生项写出已落地，三路验证见
            //   `docs/AVIF_GAINMAP_ENCODER_2026-10-02.md`）⇒ 负控**只剩 6 个格式、不得为空**；
            //   同时新增 avif 的**正向**断言（不得 Reject + 能力表位为真），把新旧口径各钉一颗牙。
            foreach (var f in new[] { "png", "tiff", "webp", "jxl", "gif", "heic" })
            {
                var gm = CE.Plan(new CI { Strategy = ST.Recommended, Source = pq2020, Format = CE.CapsFor(f), GainMapRequested = true });
                Check(gm.Action == CA.Reject && gm.Advice.Length > 0,
                    $"GainMap × {f}：必须 Reject+建议（实为 {gm.Action}）");
            }
            {
                var capsA = CE.CapsFor("avif");
                Check(capsA.SupportsGainMap, "avif 能力表须标记 SupportsGainMap（ISO-BMFF tmap 派生项）");
                var gmAvif = CE.Plan(new CI { Strategy = ST.Recommended, Source = pq2020, Format = capsA, GainMapRequested = true });
                Check(gmAvif.Action != CA.Reject,
                    $"GainMap × avif：已支持 tmap 派生项 ⇒ 不得 Reject（实为 {gmAvif.Action}）");
            }
            foreach (var f in new[] { "jpg", "jpeg", "jpegli" })
            {
                var capsG = CE.CapsFor(f);
                Check(capsG.SupportsGainMap, $"{f} 能力表标记 SupportsGainMap");
                var s2g = CE.Plan(new CI { Strategy = ST.CarryIcc, Source = pq2020, Format = capsG, GainMapRequested = true });
                Check(s2g.Override == OR.GainMapRequiresBaseMapping && s2g.EffectiveStrategy != ST.CarryIcc,
                    $"GainMap × {f}：S2 被显式覆盖为 {s2g.EffectiveStrategy}（原因 {s2g.Override}）");
            }
            // 正向（**2026-09-16 契约更新**）：GainMap 已接入引擎 ⇒ JPEG 族的**有效**任务不得被规划层拒。
            // 用「HDR 源 + 显式 tonemap」是刻意的：这正是 `ColorIntentFactory` 对 HDR→JPEG 的真实装配
            //（`!caps.CanHdr` ⇒ 自动 hable），而 GainMap 的主用途正是「HDR 底图 → SDR 底图 + 增益图」。
            // 第二条把覆盖原因 `GainMapRequiresBaseMapping` 的**承诺**钉住：底图必须真的被映射（Action=Map），
            // 而不是"记了个原因却直通"。
            {
                var gmOk = CE.Plan(new CI { Strategy = ST.Recommended, Source = pq2020, Format = CE.CapsFor("jpg"),
                                            GainMapRequested = true, ToneMapRequested = true,
                                            ToneMap = FfmpegGui.Services.ColorMapping.ToneMapMode.Hable, HdrPeakNits = 1000 });
                Check(gmOk.Action != CA.Reject, $"GainMap × jpg + 显式 tonemap ⇒ 不得 Reject（实为 {gmOk.Action}）");
                Check(gmOk.Action == CA.Map, $"GainMap × jpg：底图确实被映射（Action={gmOk.Action}，兑现 GainMapRequiresBaseMapping）");
            }

            // C4d 色域压缩（GMO）专项（**2026-09-17 新增**）：`--color-gamut-map on` 的语义此前**零覆盖**。
            // 本组把「开关 → 决策 → 后端」三段钉在同一处（纯函数，无 IO）。
            // 刻意选 **BT.2020(SDR) → Display P3** 这一对：两侧都能按 CICP 命名（`h2Possible=true`）
            // ⇒ **越界本身**并不强制进程内后端；只有开了开关（`needsGmo`）才强制。
            // 这样才能用一个开关把「决策」与「后端」两条断言同时钉住 —— 若用 Adobe RGB 这类不可命名的源，
            // 后端恒为 InProcess，负控就变成空转。
            {
                var b2020 = CS.FromCicp("bt2020", "bt709", "BT.2020(SDR)")!;
                b2020.Confidence = DC.CicpTag;

                var gmo = MakeIntent(ST.Manual, b2020, "png", CSC.All);
                gmo.AllowGamutCompression = true;
                var pg = CE.Plan(gmo);
                Check(pg.Action != CA.Reject && pg.GamutOutsideDestination && pg.GamutMap,
                    $"GMO 正向前置：BT.2020 → Display P3 越界 + 开关开 ⇒ GamutMap 必须置位（实为 {pg.Action}/{pg.GamutMap}）");
                Check(pg.ColorLoss == CL.GamutCompression,
                    $"GMO 正向：开关开 + 源越界 ⇒ ColorLoss 必须为 {CL.GamutCompression}（实为 {pg.ColorLoss}）");
                Check(pg.Backend == CB.InProcess,
                    $"GMO 正向：色域压缩只能由进程内内核执行 ⇒ 后端必须为 {CB.InProcess}（实为 {pg.Backend}）");

                // 负控：只把**开关关掉**（其余一字不动）⇒ 必须回到 Clipping + zscale 后端。
                var clip = MakeIntent(ST.Manual, b2020, "png", CSC.All);
                clip.AllowGamutCompression = false;
                var pc = CE.Plan(clip);
                Check(pc.GamutOutsideDestination && !pc.GamutMap && pc.ColorLoss == CL.Clipping,
                    $"GMO 负控：开关关 ⇒ 必须回到 {CL.Clipping} 且 GamutMap=false（实为 {pc.ColorLoss}/{pc.GamutMap}）");
                // ⚠⚠ 2026-10-05：H2 死后端已删除 ⇒ 原先「越界本身不强制进程内后端」这条
                //   （其写法是"H2 应被选中"）作废。改判**新契约**：本档一律 `InProcess`；
                //   而本条真正要守的语义（"越界**本身**不应触发 GMO 压缩"）由上面那条
                //   `GamutMap=false` + `ColorLoss=Clipping` 断言继续承担，语义未丢。
                Check(pc.Backend != CB.FfmpegFilter,
                    $"GMO 负控：H2 已删除（2026-10-05）⇒ 不得再选到 H2（实为 {pc.Backend}）");
            }

            // C4c 策略覆盖留痕（**2026-09-16 新增**）：GainMap 接入引擎后，S3/S4 两格若不被覆盖就会
            // 被规划层 Reject（S3：JPEG 无 CICP 承载通路；S4：无显式目标），而基线（旧专用管线）**有产出**
            // ⇒ 那是功能回退。故 §2 覆盖① 从「仅 S2」扩展到**三条非 S1 策略**（S2 已在上面覆盖）。
            // 这组断言把「覆盖必须发生 **且** 必须留痕 **且** 不得改写声明值」钉住 —— 只靠
            // verify-color-strategy 的产出断言是间接的：覆盖被悄悄去掉时它不会立刻转红。
            {
                var capsJpg = CE.CapsFor("jpg");
                // ⚠ 必须带上 tonemap 请求：HDR 源 + SDR 容器若**不显式请求** tonemap，规划层会因另一条
                //   独立且正确的规则（"HDR 源 → SDR 结局需显式请求 tonemap"）Reject —— 那会**掩盖**本组
                //   要测的策略覆盖（首版断言就因此假红）。真实装配路径 `ColorIntentFactory` 对 HDR→JPEG
                //   本来就会自动置 tonemap（`!caps.CanHdr`），所以带上它才是忠实的构造。
                const double kPeak = 1000;
                // S3（仅 CICP）：JPEG 结构上不可能兑现 —— 无 CICP 承载通路，底图色域只能由底图 ICC 表达。
                var s3g = CE.Plan(new CI { Strategy = ST.BakeCicpOnly, Source = pq2020, Format = capsJpg,
                                           GainMapRequested = true, ToneMapRequested = true,
                                           ToneMap = FfmpegGui.Services.ColorMapping.ToneMapMode.Hable, HdrPeakNits = kPeak });
                Check(s3g.Action != CA.Reject, $"GainMap × jpg × S3：不得 Reject（实为 {s3g.Action}）");
                Check(s3g.Override == OR.GainMapRequiresBaseMapping,
                    $"GainMap × jpg × S3：覆盖原因必须是 {OR.GainMapRequiresBaseMapping}（实为 {s3g.Override}）");
                Check(s3g.EffectiveStrategy == ST.Recommended,
                    $"GainMap × jpg × S3：生效策略应为 {ST.Recommended}（实为 {s3g.EffectiveStrategy}）");
                Check(s3g.DeclaredStrategy == ST.BakeCicpOnly,
                    $"GainMap × jpg × S3：**声明值必须原样保留**（实为 {s3g.DeclaredStrategy}）");
                // S4（手动但未给任何字段）：目标由 JpegGainMapBaseGamut 决定 ⇒「没有显式目标」不是错误。
                var s4g = CE.Plan(new CI { Strategy = ST.Manual, Source = pq2020, Format = capsJpg,
                                           GainMapRequested = true, ToneMapRequested = true,
                                           ToneMap = FfmpegGui.Services.ColorMapping.ToneMapMode.Hable, HdrPeakNits = kPeak,
                                           Manual = FfmpegGui.Services.ColorMapping.ManualIntent.Create(false, null, null, null, null) });
                Check(s4g.Action != CA.Reject, $"GainMap × jpg × S4：不得 Reject（实为 {s4g.Action}）");
                Check(s4g.Override == OR.GainMapRequiresBaseMapping,
                    $"GainMap × jpg × S4：覆盖原因必须是 {OR.GainMapRequiresBaseMapping}（实为 {s4g.Override}）");
                // 负控（**关键**）：同样的 S3 + tonemap 但**不请求 GainMap** ⇒ 不得出现该覆盖。
                // 否则「非 S1 一律被覆盖」这种退化实现也能让上面 5 条全绿 ⇒ 它们就成了空转。
                var s3n = CE.Plan(new CI { Strategy = ST.BakeCicpOnly, Source = pq2020, Format = capsJpg,
                                           GainMapRequested = false, ToneMapRequested = true,
                                           ToneMap = FfmpegGui.Services.ColorMapping.ToneMapMode.Hable, HdrPeakNits = kPeak });
                Check(s3n.Override != OR.GainMapRequiresBaseMapping,
                    $"负控：S3 × jpg **不请求 GainMap** ⇒ 不得出现 {OR.GainMapRequiresBaseMapping}（实为 {s3n.Override}）");
            }

            // C12 PNG 标注策略：ICC/CICP 取舍由**四大策略**决定（纯函数，无 IO）。
            // 规范前提（PNG Third Edition）：优先级 cICP=1 > iCCP=2；且无 cICP 时至多一个 profile
            // ⇒ cICP 与 iCCP 允许并存，关键在“ICC 是谁的”。
            {
                // ⚠ 不得把枚举装成 object 再 ==（装箱后是引用相等，永远 false 且不报错）；
                //   也不能 `var X = SomeEnumType;`（类型不是值）⇒ 取实际成员作为局部常量。
                var W = FfmpegGui.Services.PngCicpService.PngLabelAction.WriteCicp;
                var SkipSrc = FfmpegGui.Services.PngCicpService.PngLabelAction.SkipSourceIcc;
                var SkipUn = FfmpegGui.Services.PngCicpService.PngLabelAction.SkipUnnameable;
                var SkipLabeled = FfmpegGui.Services.PngCicpService.PngLabelAction.SkipAlreadyLabeled;
                FfmpegGui.Services.PngCicpService.PngLabelAction DL(ST s, bool iccp, bool cicp, bool src, bool stdIcc, bool nameable)
                    => FfmpegGui.Services.PngCicpService.DecideLabel(s, iccp, cicp, src, stdIcc, nameable);
                Check(DL(ST.Recommended, false, false, false, false, true) == W,
                    "S1 无 ICC 且可命名 ⇒ 写 cICP");
                Check(DL(ST.Recommended, true, false, false, false, true) == W,
                    "S1 带自生成标准 ICC ⇒ 仍写 cICP（与 ICC 同义，并写安全）");
                Check(DL(ST.CarryIcc, true, false, false, true, true) == W,
                    "S2 源只有 ICC、但该 ICC 是标准色彩空间⇒ 写 cICP（两者同义，不丢精度）");
                Check(DL(ST.CarryIcc, true, false, false, false, true) == SkipSrc,
                    "S2 源只有 ICC、且 ICC 非标准（色度未命中）⇒ 不写（猜出的 cICP 会降级源 ICC）");
                Check(DL(ST.CarryIcc, true, false, true, false, true) == W,
                    "S2 且源自带 CICP ⇒ 必须携带（ICC+CICP 一起带，丢掉就是丢信息）");
                Check(DL(ST.CarryIcc, false, false, false, false, true) == W,
                    "S2 但源无 ICC ⇒ 写 cICP（契约里的“零改动直通/继承标签”）");
                Check(DL(ST.BakeCicpOnly, true, false, false, false, true) == W,
                    "S3 仅CICP ⇒ 必写 cICP（残留 iCCP 由调用方另行告警）");
                Check(DL(ST.Manual, true, false, false, false, true) == W,
                    "S4 手动 ⇒ 可命名即写（用户担保）");
                // 反向护栏：证明参数真在起作用，而不是恒真断言
                Check(DL(ST.Recommended, true, false, false, false, false) == SkipUn,
                    "不可命名（ProPhoto/Adobe）⇒ 宁缺勿错，ICC 作唯一描述");
                Check(DL(ST.Manual, false, true, false, false, true) == SkipLabeled,
                    "已有 cICP ⇒ 不重复写、不覆盖");
                Check(DL(ST.BakeCicpOnly, true, false, false, false, true) == W,
                    "S3 即使源无 CICP 也写（本意就是只留 CICP）");
                // ICC 曲线 → CICP 令牌：仅当与标准曲线等价才命名（不猜）。本文件已有 TC = TransferCurve 别名。
                Check(FfmpegGui.Services.FfmpegCommandBuilder.CicpTokenOfCurve(TC.Srgb) == "iec61966-2-1",
                    "曲线 sRGB ⇒ iec61966-2-1");
                Check(FfmpegGui.Services.FfmpegCommandBuilder.CicpTokenOfCurve(TC.Pq) == "smpte2084",
                    "曲线 PQ ⇒ smpte2084");
                Check(FfmpegGui.Services.FfmpegCommandBuilder.CicpTokenOfCurve(TC.Hlg) == "arib-std-b67",
                    "曲线 HLG ⇒ arib-std-b67");
                Check(FfmpegGui.Services.FfmpegCommandBuilder.CicpTokenOfCurve(TC.Gamma28) == null,
                    "γ2.8 无标准对应 ⇒ 不命名（不猜）");
                Check(FfmpegGui.Services.FfmpegCommandBuilder.CicpTokenOfCurve(null) == null,
                    "null 曲线（非中性/无法解析 profile）⇒ 不命名");
            }

            // C5 开关鲁棒性：三档 carry-scope 下关键格行为与定义一致，且契约仍成立
            static bool ScopeAllows(CSC sc, CS src) => sc switch
            {
                CSC.None => false,
                CSC.Unnameable => !src.IsCicpNameable,
                _ => true,
            };
            foreach (var scope in new[] { CSC.None, CSC.Unnameable, CSC.All })
                foreach (var (src, fmt) in new[] { (adobe, "png"), (prophoto, "avif"), (p3Icc, "tiff"), (srgbIcc, "png") })
                {
                    var it = MakeIntent(ST.Recommended, src, fmt, scope);
                    var p = CE.Plan(it);
                    bool carries = p.Action == CA.CarryIcc;
                    bool expect = CE.CapsFor(fmt).CanCarryArbitraryIcc && src.SourceIccPath != null && ScopeAllows(scope, src);
                    Check(carries == expect, $"C5 scope={scope} {src.Name}→{fmt}: carry={carries} 预期 {expect}（{p.Reason}）");
                    Check(p.Matrix == null || p.Action == CA.Map, $"C5 scope={scope} {src.Name}→{fmt}: 非映射格不得带矩阵");
                }

            // C6 确定性（同意图两次规划结果必须一致）；完整“直接路径 vs cjxl 管道”同构待 P7 接线后补
            for (int i = 0; i < 2; i++)
            {
                var a = CE.Plan(MakeIntent(ST.Recommended, p3Icc, "avif", CSC.All));
                var b = CE.Plan(MakeIntent(ST.Recommended, p3Icc, "avif", CSC.All));
                Check(a.Action == b.Action && a.Annot == b.Annot && a.ColorLoss == b.ColorLoss
                      && ((a.Matrix == null) == (b.Matrix == null)) && a.OutPrimaries == b.OutPrimaries,
                    "C6 同一 ColorIntent 两次规划结果一致（确定性）");
            }
            // C7 H2 口径黑名单：目标曲线 bt709/bt470bg/smpte240m 在 Std 口径下不得交给 H2；Zimg 口径下允许（对齐历史）
            var b709sdr = CS.FromCicp("bt709", "bt709", "BT.709 SDR");
            var p3b = CS.FromCicp("smpte432", "iec61966-2-1", "Display P3");
            if (b709sdr != null && p3b != null)
            {
                var polForce = new FfmpegGui.Services.ColorMapping.PlanPolicy
                { AllowApproximateCurve = false, ForceMapping = true, TargetExplicit = true };
                var pStd = CE.Plan(p3b, b709sdr, polForce);
                Check(pStd.Backend != CB.FfmpegFilter,
                    $"C7 Std 口径：目标 bt709 不得走 H2（实为 {pStd.Backend}）{pStd.Reason}");
                var polZ = new FfmpegGui.Services.ColorMapping.PlanPolicy
                { AllowApproximateCurve = false, ForceMapping = true, TargetExplicit = true, Transfer709 = F709.Zimg };
                var pZ = CE.Plan(p3b, b709sdr, polZ);
                // ⚠⚠ 2026-10-05：H2 死后端已**删除**（见 `ColorTransformPlan` 后端选择处的长注释）⇒
                //   原先「Zimg 口径允许回退到 H2 对齐历史产物」与「对照：非黑名单目标仍可走 H2」
                //   两条契约**随之作废**（H2 不再可达）。此处改判**新契约**：
                //   无论口径是否对齐 zimg，后端一律 `InProcess`（H1）—— 即"本档不存在 H2 执行体"。
                //   ⚠ 这不是"为绿而放宽"：断言的方向从"H2 应当被选中"改成"**H2 不得再出现**"，
                //     仍然是有牙的（若有人恢复 H2 置位，本条立刻转红）。
                Check(pZ.Backend != CB.FfmpegFilter,
                    $"C7 Zimg 口径：H2 已删除（2026-10-05）⇒ 不得再选到 H2（实为 {pZ.Backend}）");
                // 对照：非黑名单目标同样不得走 H2
                var pOk = CE.Plan(p3b, srgbTag, polForce);
                Check(pOk.Backend != CB.FfmpegFilter,
                    $"C7 对照：H2 已删除 ⇒ 任何目标都不得走 H2（实为 {pOk.Backend}）");
            }

            // C7b —— 缺陷 #29 的**行为锁**（此前只有 C7 那条结构锁，而像素从头到尾没被这个开关碰过）：
            //   `--color-709-curve` 在**结构上必须走 H1** 的格子里，std 与 zimg 曾拿到**逐字节相同**的产物
            //   （2026-09-26 实测四格 × {std,zimg} 全数同 md5）⇒ 用户显式选了口径却是静默无效参数。
            //   本段量的是**真像素**（Plan → ToSpec → ColorKernels，与 QueueProcessor.cs:1090 同一条链），
            //   差量级按实测的曲线分歧给**上下界**，不写"不同即可"那种没牙的断言：
            //     · zimg 的 bt709 = 纯 γ(1/2.4)，与 ITU-R BT.709-6 定义式的编码差在 linear≈0.0169 处
            //       取最大 = **6986 LSB@16bit ≈ 27.2 LSB@8bit**（本轮按两条定义式自算；
            //       zscale 实测值 7231 LSB@16bit 与之差 245 ≈ 1 LSB@8bit 的量化地板，同源同向）。
            //   ⚠ 前提条件也断言：两格都**必须**落在 H1（BT.2020 ⊄ BT.709 ⇒ 开 GMO ⇒ needsGmo ⇒ 后端与口径
            //     无关），否则比较的对象变成"我们的曲线 vs zscale 的曲线"，本断言就测不到我们要测的东西。
            {
                var lin2020 = CS.FromCicp("bt2020", "linear", "BT.2020(线性源)");
                var bt709Sdr = CS.FromCicp("bt709", "bt709", "BT.709 SDR");
                var srgbDst2 = CS.FromCicp("bt709", "iec61966-2-1", "sRGB 目标");
                if (lin2020 != null && bt709Sdr != null && srgbDst2 != null)
                {
                    FfmpegGui.Services.ColorMapping.PlanPolicy Pol709(bool zimg, bool gmo = true)
                        => new()
                        {
                            Format = CE.CapsFor("png"),
                            AllowApproximateCurve = false, ForceMapping = true, TargetExplicit = true,
                            AllowGamutCompression = gmo, TargetBitDepth = 8,
                            Transfer709 = zimg ? F709.Zimg : F709.Std,
                        };
                    var c7bStd = CE.Plan(lin2020, bt709Sdr, Pol709(false));
                    var c7bZim = CE.Plan(lin2020, bt709Sdr, Pol709(true));
                    Check(c7bStd.Backend == CB.InProcess && c7bZim.Backend == CB.InProcess,
                        $"C7b 前提：两格都必须落在 H1（实 std={c7bStd.Backend} / zimg={c7bZim.Backend}）");

                    var spStd = c7bStd.ToSpec(); var spZim = c7bZim.ToSpec();
                    Check(spStd.DstCurve.Equivalent(TC.Bt709) && !spZim.DstCurve.Equivalent(TC.Bt709)
                          && Math.Abs(spZim.DstCurve.G - 2.4) < 1e-12 && spZim.DstCurve.C == 0.0 && spZim.DstCurve.D == 0.0,
                          $"C7b 结构：zimg 的出口曲线必须是纯 γ(1/2.4)（实={spZim.DstCurve.Name}），std 仍是定义式（实={spStd.DstCurve.Name}）");
                    // 口径只改**像素**，不改**标注**：这是"对齐历史产物"的字面语义（zscale 的产物也一直
                    // 是 t=bt709 标签 + γ2.4 像素）。⚠ 反过来也必须成立：这条曲线自己**不**声称可 CICP 命名。
                    Check(spZim.DstCurve.CicpToken == null && spZim.DstCurve.TableLength == 0
                          && c7bZim.OutTransfer == "bt709" && c7bStd.OutTransfer == "bt709"
                          && c7bZim.IccTrc == "bt709" && c7bStd.IccTrc == "bt709"
                          && c7bZim.AttachIcc == c7bStd.AttachIcc,
                          $"C7b 标注不随口径走：zimg 曲线 CicpToken=null（不发明命名），CICP 与 ICC 仍 bt709"
                        + $"（实 t={c7bZim.OutTransfer}/{c7bStd.OutTransfer} icc={c7bZim.IccTrc ?? "-"}/{c7bStd.IccTrc ?? "-"}）");

                    // 65536 级**线性**灰阶：源曲线=linear ⇒ 输入码值就是线性值，两条出口曲线的取差点
                    // （linear≈0.0169）必被采到；R=G=B ⇒ GMO 对中性色严格 no-op（见 GamutMapScalar 的性质），
                    // 于是 16bit 差值里只剩"曲线口径"这一项，没有别的算子混进来。
                    const int n709 = 65536;
                    var in709 = new byte[n709 * 6];
                    for (int i = 0; i < n709; i++)
                    {
                        ushort v = (ushort)i;
                        for (int c = 0; c < 3; c++) { in709[i * 6 + c * 2] = (byte)(v & 0xFF); in709[i * 6 + c * 2 + 1] = (byte)(v >> 8); }
                    }
                    var oStd = new byte[n709 * 6]; var oZim = new byte[n709 * 6];
                    var oStd2 = new byte[n709 * 6]; var oZim2 = new byte[n709 * 6];
                    CK.TransformRgb48le(in709, oStd, spStd, n709);
                    CK.TransformRgb48le(in709, oZim, spZim, n709);
                    CK.TransformRgb48le(in709, oStd2, spStd, n709);
                    CK.TransformRgb48le(in709, oZim2, spZim, n709);
                    Check(oStd.AsSpan().SequenceEqual(oStd2) && oZim.AsSpan().SequenceEqual(oZim2),
                        "C7b 反向（确定性）：同口径两跑必须逐字节相同（std/zimg 各两跑；防 RNG/抖动混进口径差里）");
                    bool same16 = oStd.AsSpan().SequenceEqual(oZim);
                    int max16 = 0; long sum16 = 0;
                    for (int i = 0; i + 1 < oStd.Length; i += 2)
                    {
                        int d = Math.Abs((oStd[i] | (oStd[i + 1] << 8)) - (oZim[i] | (oZim[i + 1] << 8)));
                        if (d > max16) max16 = d;
                        sum16 += d;
                    }
                    Check(!same16 && max16 >= 6900 && max16 <= 7100,
                        $"C7b 行为（16bit）：std vs zimg 必须**产物不同**且最大差落在 6986±100 LSB@16bit"
                      + $"（实 相同={same16} max={max16} mean={(double)sum16 / (n709 * 3):F1}）—— 静默无效或把曲线换错都拦");

                    // 同一条差换算到 8bit 出口（本工具最常见的产物位深）。⚠ 这里显式关掉有序抖动：
                    // 本断言量的是**口径差本身**，±1 LSB 的抖动不该进上下界（抖动的一致性由 C10/C11 守）。
                    var sp8Std = c7bStd.ToSpec(); sp8Std.Dither = FfmpegGui.Services.ColorMapping.DitherMode.None;
                    var sp8Zim = c7bZim.ToSpec(); sp8Zim.Dither = FfmpegGui.Services.ColorMapping.DitherMode.None;
                    var b8Std = new byte[n709 * 3]; var b8Zim = new byte[n709 * 3];
                    CK.TransformRgb48leToRgb8(in709, b8Std, sp8Std, n709, null, null, n709, 0);
                    CK.TransformRgb48leToRgb8(in709, b8Zim, sp8Zim, n709, null, null, n709, 0);
                    int max8 = 0; long sum8 = 0;
                    for (int i = 0; i < b8Std.Length; i++)
                    {
                        int d = Math.Abs(b8Std[i] - b8Zim[i]);
                        if (d > max8) max8 = d;
                        sum8 += d;
                    }
                    double mean8 = (double)sum8 / b8Std.Length;
                    Check(max8 >= 25 && max8 <= 30 && mean8 >= 9.0 && mean8 <= 15.0,
                        $"C7b 行为（8bit）：最大差应落在 27±3 LSB、均值 11.8±3 LSB（实 max={max8} mean={mean8:F2}）"
                      + "⇒ 量级对得上实测分歧，不是末位抖动");

                    // ── "不许静默"那一半：兑现要点名兑现，兑现不了要点名原因（可用性诚实）──
                    Check(c7bZim.Reason.Contains("已兑现(H1)"),
                        $"C7b 点名：zimg 在 H1 格必须声明已兑现（实 Reason={c7bZim.Reason}）");
                    Check(!c7bStd.Reason.Contains("口径=zimg"),
                        "C7b 反向：std（=默认值）不逐格播报，避免把日志变成噪声");
                    // H2 格（放行 zscale 的那条）也必须点名，且说清"由 zscale 兑现"而不是"由我们换曲线"
                    var p3lin2 = CS.FromCicp("smpte432", "iec61966-2-1", "Display P3(标签)");
                    if (p3lin2 != null)
                    {
                        var c7bH2 = CE.Plan(p3lin2, bt709Sdr, Pol709(true, gmo: false));
                        // ⚠⚠ 2026-10-05：H2 死后端已删除 ⇒ 原「H2 格要说清口径由 zscale 兑现」作废。
                        //   改判**新契约**：该格一律走 H1，且必须**点名"已兑现(H1)"**
                        //   （而不是留着一条 H2 文案挂在一个不存在的执行体上）。
                        Check(c7bH2.Backend != CB.FfmpegFilter && c7bH2.Reason.Contains("已兑现(H1)"),
                            $"C7b 点名：H2 已删除 ⇒ 本格走 H1 且须点名「已兑现(H1)」（实 backend={c7bH2.Backend} Reason={c7bH2.Reason}）");
                    }
                    var c7bNa = CE.Plan(lin2020, srgbDst2, Pol709(true));
                    Check(c7bNa.Action == CA.Map && c7bNa.Reason.Contains("与 BT.709 编码口径无关"),
                        $"C7b 点名不适用：目标是 sRGB 曲线 ⇒ 必须说明为何本开关不适用（act={c7bNa.Action} 实 Reason={c7bNa.Reason}）");
                    var polGm709 = Pol709(true); polGm709.GainMapRequested = true;
                    var c7bGm = CE.Plan(lin2020, bt709Sdr, polGm709);
                    Check(c7bGm.Reason.Contains("GainMap 出口"),
                        $"C7b 点名不适用：GainMap 出口不消费计划层曲线 ⇒ 必须点名（实 Reason={c7bGm.Reason}）");
                    var c7bNone = CE.Plan(lin2020, null, Pol709(true));
                    // ⚠ 只查"不改像素"没有牙：那四个字在**基础理由**里就有（"无目标色彩空间 → 不改像素，沿用源描述"），
                    //   把 Transfer709Note 的不适用点名整段删掉它也照样绿（实测：见下方注释的短语才是点名独有）。
                    //   故额外锁一条**只可能来自点名文本**的短语 —— 缺陷 #29 的"不许静默"就是这一条在守。
                    Check(c7bNone.Action == CA.None && c7bNone.Reason.Contains("不改像素")
                          && c7bNone.Reason.Contains("没有出口编码可换曲线"),
                        $"C7b 点名不适用：无目标（不改像素）⇒ 必须点名（act={c7bNone.Action} 实 Reason={c7bNone.Reason}）");
                    // 未验证的 zimg 形态不得被"顺手发明"成曲线：bt470bg/smpte240m 只放行 H2，不换出口曲线
                    var bt470bgDst = CS.FromCicp("bt709", "bt470bg", "BT.709 原色 + γ2.8 传递");
                    var smpte240Dst = CS.FromCicp("bt709", "smpte240m", "BT.709 原色 + SMPTE 240M 传递");
                    var p3ForToken = CS.FromCicp("smpte432", "linear", "Display P3(线性源)");
                    foreach (var dUnverified in new[] { bt470bgDst, smpte240Dst })
                    {
                        if (dUnverified == null || p3ForToken == null) { Check(false, "C7b 前提：bt470bg/smpte240m 或 P3(线性) 描述符不可用"); continue; }
                        var c7bUv = CE.Plan(p3ForToken, dUnverified, Pol709(true, gmo: false));
                        Check(c7bUv.DstCurve != null && !c7bUv.DstCurve.Value.Equivalent(TC.Bt709Zimg)
                              && c7bUv.ToSpec().DstCurve.Equivalent(dUnverified.Curve)
                              && c7bUv.Reason.Contains("未实测"),
                            $"C7b 不发明曲线：{dUnverified.TransferToken} 的 zimg 形态未验证 ⇒ 出口曲线仍是本仓那条"
                          + $"（实 dst={c7bUv.ToSpec().DstCurve.Name}）{c7bUv.Reason}");
                    }
                    // ── 上面那条量的是"计划有没有换曲线"（行为）；这条量"可用表本身长什么样"（字面）──
                    // ⚠ 正向对照（bt709 必须给出曲线）与两条 null 一起写：只写 null 那两条的话，
                    //   `ZimgExitCurve` 退化成"永远返回 null"（开关整个失效）也会让它俩假绿。
                    // ServiceProbe 不在 FfmpegGui 的 InternalsVisibleTo 名单里（改 csproj 会炸还原），
                    // 故与取私有字段的既有做法一致：NonPublic|Static 反射，取不到方法就判红。
                    var zecMi = typeof(CE).GetMethod("ZimgExitCurve",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    if (zecMi == null)
                        Check(false, "C7b 可用表字面锁：反射取不到 ColorMappingEngine.ZimgExitCurve（改名/改可见性 ⇒ 锁静默失效）");
                    else
                    {
                        Check(zecMi.Invoke(null, new object?[] { "bt709" }) is TC zec709 && zec709.Equivalent(TC.Bt709Zimg),
                            "C7b 可用表字面锁：ZimgExitCurve(\"bt709\") 必须给出 zimg 形态（正向对照，拦\"永远 null\"式退化）");
                        Check(zecMi.Invoke(null, new object?[] { "bt470bg" }) == null,
                            "C7b 可用表字面锁：ZimgExitCurve(\"bt470bg\") 必须为 null（未实测 ⇒ 不换曲线，也不为凑绿发明曲线）");
                        Check(zecMi.Invoke(null, new object?[] { "smpte240m" }) == null,
                            "C7b 可用表字面锁：ZimgExitCurve(\"smpte240m\") 必须为 null（未实测 ⇒ 不换曲线，也不为凑绿发明曲线）");
                    }
                }
                else Check(false, "C7b 前提描述符不可用（bt2020/linear、bt709/bt709、sRGB 之一为 null）");
            }

            // C8 单入口检测器：必须抓到真入口，且**不得误杀**标注工具（iccgen/format/纯缩放/CLI 标注选项）
            var pH1 = new FfmpegGui.Services.ColorMapping.ColorTransformPlan { Action = CA.Map, Backend = CB.InProcess };
            var pCarry = new FfmpegGui.Services.ColorMapping.ColorTransformPlan { Action = CA.CarryIcc, Backend = CB.None };
            var pNoop = new FfmpegGui.Services.ColorMapping.ColorTransformPlan { Action = CA.None, Backend = CB.None };
            // ⚠⚠ 2026-10-05：原有一条合成用例 `pH2 = { Action=Map, Backend=FfmpegFilter }` 用来测
            //   「H2 计划下出现两条色彩入口要被抓到」。H2 死后端删除后，`FindPixelColorEntry`
            //   新增了**反向自检**（置位 H2 ⇒ 直接返回"置位与执行体不匹配"）⇒ 该合成用例
            //   必然命中那条自检、不再是"H2 单链"场景 ⇒ 用例本身失去意义，**删除**。
            //   其覆盖不丢：真正的"双入口"检测由上面 `pH1` 那条承担（H1 计划 + zscale 链）。
            string zs = "-vf format=gbrpf32le,zscale=pin=bt709:tin=iec61966-2-1:min=gbr:p=smpte432:t=iec61966-2-1:m=bt709,format=rgb48le";
            Check(CE.FindPixelColorEntry(zs, pH1) != null, "C8 抓到双映射入口（H1 计划 + zscale 色彩链）");
            Check(CE.FindPixelColorEntry(zs, pCarry) != null, "C8 抓到违反：携带计划却带色彩链");
            // 反向自检（新，2026-10-05）：H2 置位必须**响亮报错**（而不是静默当"单链合法"）
            var pH2Probe = new FfmpegGui.Services.ColorMapping.ColorTransformPlan { Action = CA.Map, Backend = CB.FfmpegFilter };
            var h2Verdict = CE.FindPixelColorEntry("-vf scale=800:-1,format=rgb48le", pH2Probe);
            Check(h2Verdict != null && h2Verdict.Contains("无 H2 执行体"),
                $"C8 反向自检：H2 置位必须报「无 H2 执行体」（实 {h2Verdict ?? "null"}）");
            // 不误杀：iccgen（标注生成器）/ format= / 纯缩放 / -colorspace 等 CLI 标注选项 / 无参 zscale
            Check(CE.FindPixelColorEntry("-vf format=rgb48le,iccgen=color_primaries=smpte432:color_trc=iec61966-2-1:force=1 -colorspace bt709 -color_trc bt709", pNoop) == null,
                "C8 无误杀：iccgen + CLI 标注选项不算像素入口");
            Check(CE.FindPixelColorEntry("-vf scale=800:-1,format=rgb48le,zscale=3840:2160,iccgen", pH1) == null,
                "C8 无误杀：纯缩放（无色彩参数）不算入口（H1 单链合法）");
            Check(CE.FindPixelColorEntry("-i in.png -c:v libjxl out.jxl", pCarry) == null, "C8 无误杀：无色彩滤镜的命令行");

            // C9 几何凸包包含判据（取代旧的“矩阵元素全非负”近似）：已知关系必须全部正确
            var gSrgb = CS.FromCicp("bt709", "iec61966-2-1", "sRGB");
            var gP3 = CS.FromCicp("smpte432", "iec61966-2-1", "Display P3");
            var g2020 = CS.FromCicp("bt2020", "bt709", "BT.2020");
            var gPro = CS.FromNamedSpace("ProPhoto RGB");
            if (gSrgb != null && gP3 != null && g2020 != null && gPro != null)
            {
                (bool? got, bool want, string what)[] cases = {
                    (CS.ContainsGamut(gSrgb, gP3), true,  "sRGB ⊆ Display P3"),
                    (CS.ContainsGamut(gSrgb, g2020), true, "sRGB ⊆ BT.2020"),
                    // ⚠ 不是笔误："BT.2020 包住 P3" 是**近似说法**。实测/手算均表明：
                    //   2020 的 G→R 边在 x=0.680 处 y≈0.3183，而 Display P3 红原色 y=0.320
                    //   ⇒ P3 红原色超出 2020 三角形约 0.0017（近共线），严格几何上 P3 ⊄ BT.2020。
                    //   几何判据能抓到这个差正是它优于“矩阵元素非负”近似的地方。
                    (CS.ContainsGamut(gP3, g2020), false,   "Display P3 ⊄ BT.2020（红原色超出≈0.0017）"),
                    (CS.ContainsGamut(gP3, gSrgb), false,  "Display P3 ⊄ sRGB"),
                    (CS.ContainsGamut(g2020, gP3), false,  "BT.2020 ⊄ Display P3"),
                    (CS.ContainsGamut(g2020, gSrgb), false,"BT.2020 ⊄ sRGB"),
                    (CS.ContainsGamut(gPro, g2020), false, "ProPhoto ⊄ BT.2020"),
                    (CS.ContainsGamut(gPro, gSrgb), false, "ProPhoto ⊄ sRGB"),
                    (CS.ContainsGamut(gSrgb, gSrgb), true, "sRGB ⊆ sRGB（自身，边界松弛）"),
                };
                foreach (var (got, want, what) in cases)
                {
                    Check(got == want, $"C9 {what}（实={got}）");
                    if (got != want)
                    {
                        // 失败时输出实际参与判定的 xy（D50 列归一）与目标三角形，便于区分"几何真值"与"实现错"
                        static string Fmt(double[]? v) => v == null ? "null"
                            : $"R({v[0]:F5},{v[1]:F5}) G({v[2]:F5},{v[3]:F5}) B({v[4]:F5},{v[5]:F5})";
                        var srcD = what.StartsWith("sRGB ⊆") ? gSrgb : what.StartsWith("Display P3 ⊆") ? gP3
                               : what.StartsWith("BT.2020 ⊄") ? g2020 : gPro;
                        var dstD = what.EndsWith("Display P3") && what.Contains("⊆") ? gP3
                                 : what.Contains("BT.2020") && !what.StartsWith("BT.2020") ? g2020
                                 : what.Contains("sRGB") && what.StartsWith("ProPhoto") ? gSrgb
                                 : what.Contains("sRGB（自身") ? gSrgb : g2020;
                        Console.WriteLine($"      源={Fmt(srcD?.PrimariesXyD50())}");
                        Console.WriteLine($"      标={Fmt(dstD?.PrimariesXyD50())}");
                    }
                }
            }

            // C10 抖动确定性：相位取绝对坐标 ⇒ 整幅与逐条 tile 必须逐字节一致
            {
                int dw = 32, dh = 32;
                var srcA = new byte[dw * dh * 4];
                for (int i = 0; i < dw * dh; i++)
                {
                    byte g = (byte)(i % 251);
                    srcA[i * 4] = g; srcA[i * 4 + 1] = (byte)(255 - g); srcA[i * 4 + 2] = (byte)((i * 7) % 256); srcA[i * 4 + 3] = 255;
                }
                var specD = Spec("bt709", "iec61966-2-1", "smpte432", "iec61966-2-1");
                specD.Dither = FfmpegGui.Services.ColorMapping.DitherMode.Ordered4x4;
                var eotf8 = TC.Srgb.BuildEotfTable8();
                var whole = new byte[srcA.Length];
                CK.TransformRgba8(srcA, whole, specD, eotf8, dw, dh);
                var tiled = new byte[srcA.Length];
                for (int y0 = 0; y0 < dh; y0 += 7)
                {
                    int hh = Math.Min(7, dh - y0);
                    CK.TransformRgba8(srcA.AsSpan(y0 * dw * 4, hh * dw * 4), tiled.AsSpan(y0 * dw * 4, hh * dw * 4),
                                      specD, eotf8, dw, hh, null, 0, y0);
                }
                bool same = true; int first = -1;
                for (int i = 0; i < whole.Length; i++) if (whole[i] != tiled[i]) { same = false; first = i; break; }
                Check(same, $"C10 抖动：整幅 vs 逐条 tile（originY）逐字节一致{(same ? "" : $"，首个差异在字节 {first}")}");
                // 确定性：两次运行结果相同（无 RNG）
                var again = new byte[srcA.Length];
                CK.TransformRgba8(srcA, again, specD, eotf8, dw, dh);
                Check(whole.AsSpan().SequenceEqual(again), "C10 抖动：同输入两次运行逐字节一致（无 RNG）");
            }

            // C11 rgb48→rgb8 降位内核：绝对相位 ⇒ tile 切分逐字节一致；拑动后渐变仍单调
            {
                int cw = 64, ch = 8;
                var src16 = new byte[cw * ch * 6];
                for (int i = 0; i < cw * ch; i++)
                {
                    int v = (int)Math.Round(i / (double)(cw * ch - 1) * 65535.0);
                    for (int c = 0; c < 3; c++)
                    {
                        src16[i * 6 + c * 2] = (byte)(v & 0xFF); src16[i * 6 + c * 2 + 1] = (byte)(v >> 8);
                    }
                }
                var spec8 = Spec("bt709", "iec61966-2-1", "smpte432", "iec61966-2-1");
                spec8.Dither = FfmpegGui.Services.ColorMapping.DitherMode.Ordered4x4;
                var tS = CK.BuildTables(spec8.SrcCurve); var tD = CK.BuildTables(spec8.DstCurve);
                var whole8 = new byte[cw * ch * 3];
                CK.TransformRgb48leToRgb8(src16, whole8, spec8, cw * ch, tS, tD, cw, 0);
                var tiled8 = new byte[cw * ch * 3];
                for (int y0 = 0; y0 < ch; y0 += 3)
                {
                    int hh = Math.Min(3, ch - y0);
                    CK.TransformRgb48leToRgb8(src16.AsSpan(y0 * cw * 6, hh * cw * 6), tiled8.AsSpan(y0 * cw * 3, hh * cw * 3),
                                              spec8, hh * cw, tS, tD, cw, y0 * cw);
                }
                bool same8 = true; int diffAt = -1;
                for (int i = 0; i < whole8.Length; i++) if (whole8[i] != tiled8[i]) { same8 = false; diffAt = i; break; }
                Check(same8, $"C11 8bit 降位：整幅 vs 逐条 tile（originIndex）逐字节一致{(same8 ? "" : $"，首差 {diffAt}")}");
                // 单调不降：拑动后沿渐变 R 不应前进后退（允许相邻等值，不允许下降超过 1）
                int worstDrop = 0;
                for (int i = 1; i < cw * ch; i++)
                {
                    int d = whole8[i * 3] - whole8[(i - 1) * 3];
                    if (d < worstDrop) worstDrop = d;
                }
                Check(worstDrop >= -1, $"C11 拑动后渐变仍单调（最大回落 {worstDrop} LSB，仅允许拖动带来的 ±1）");
                // 精度：与 16bit 路径的 8bit 化结果相比，误差 ≤1 LSB（拑动只应搬动 1 个台阶）
                var specN = Spec("bt709", "iec61966-2-1", "smpte432", "iec61966-2-1");
                var mid16 = new byte[src16.Length];
                CK.TransformRgb48le(src16, mid16, specN, cw * ch, tS, tD);
                int maxDev = 0;
                for (int i = 0; i < cw * ch; i++)
                {
                    int plain = (int)Math.Round((mid16[i * 6] + mid16[i * 6 + 1] * 256) / 65535.0 * 255.0);
                    int dev = Math.Abs(plain - whole8[i * 3]);
                    if (dev > maxDev) maxDev = dev;
                }
                Check(maxDev <= 1, $"C11 拑动幅度受控：与无拑动降位最大偏差 {maxDev} LSB（应 ≤1）");
            }

            // C12 **精确可表示的码值必须原样出来**（有序抖动的居中判据；2026-09-26 补）
            //   动机：C10/C11 只测「tile 一致」与「单调」，两者对**中心偏移**都不敏感 ——
            //   旧写法 t = Bayer4[p] − 0.5f/4f ⇒ t ∈ [−0.125, +0.8125]，16 个相位里 **6 个** t > 0.5，
            //   于是"本来 8bit 能 1:1 精确表示"的码值被系统性抬 +1（8bit 源经 rgb48le 后码值恒为 257 的整数倍）。
            //   这就是维护者点名的**无意义精度损失**：没有任何视觉收益，只是把可精确表达的值推高一档。
            //   ⇒ 判据必须按相位穷举（只跑一种相位会随机落在"恰好没事"的那 10/16 上）。
            {
                int w12 = 16, h12 = 16;                       // 16×16 ⇒ 4×4 相位各出现 16 次
                var specN12 = Spec("bt709", "iec61966-2-1", "bt709", "iec61966-2-1");   // 同原色 ⇒ 无矩阵
                specN12.Dither = FfmpegGui.Services.ColorMapping.DitherMode.Ordered4x4;
                var tbl12 = CK.BuildTables(specN12.SrcCurve);
                var src12 = new byte[w12 * h12 * 6];
                var want12 = new byte[w12 * h12 * 3];
                for (int i = 0; i < w12 * h12; i++)
                {
                    // 扫过整条 8bit 码轴（含 0/255 两端），16bit 值 = 257×码值 ⇒ 8bit 能 1:1 精确表示
                    int code = (int)Math.Round(i / (double)(w12 * h12 - 1) * 255.0);
                    int v16 = code * 257;
                    for (int c = 0; c < 3; c++)
                    {
                        src12[i * 6 + c * 2] = (byte)(v16 & 0xFF); src12[i * 6 + c * 2 + 1] = (byte)(v16 >> 8);
                        want12[i * 3 + c] = (byte)code;
                    }
                }
                var got12 = new byte[w12 * h12 * 3];
                CK.TransformRgb48leToRgb8(src12, got12, specN12, w12 * h12, tbl12, tbl12, w12, 0);
                int lifted = 0, dropped = 0, worstPhase = 0;
                var phaseBad = new int[16];
                for (int i = 0; i < w12 * h12; i++)
                {
                    int ph = (i % w12 & 3) + ((i / w12 & 3) << 2);
                    for (int c = 0; c < 3; c++)
                    {
                        int d = got12[i * 3 + c] - want12[i * 3 + c];
                        if (d > 0) lifted++; else if (d < 0) dropped++;
                        if (d != 0) phaseBad[ph]++;
                    }
                }
                for (int p = 1; p < 16; p++) if (phaseBad[p] > phaseBad[worstPhase]) worstPhase = p;
                Check(lifted == 0 && dropped == 0,
                    $"C12 精确可表示的码值经拑动降位必须逐字节 no-op（抬升 {lifted} / 下降 {dropped}，最差相位 {worstPhase} 命中 {phaseBad[worstPhase]} 次）");
                // 这条判据自己也要有对照：同一内核关掉抖动时必须仍逐字节相等（否则上面那条可能只是"抖动根本没跑"）
                specN12.Dither = FfmpegGui.Services.ColorMapping.DitherMode.None;
                var gotOff = new byte[got12.Length];
                CK.TransformRgb48leToRgb8(src12, gotOff, specN12, w12 * h12, tbl12, tbl12, w12, 0);
                Check(gotOff.AsSpan().SequenceEqual(want12), "C12 对照：关掉抖动同样逐字节 no-op ⇒ 上面那条不是「抖动没生效」造出来的假绿");
                // 反向哨兵：**半档**输入（257 的整数倍 +128，8bit 不可精确表示）在 16 个相位上必须展开成
                //   ≥2 个不同输出。没有这条，"C12 全等"也可能只是这条路把抖动整体吞掉了。
                var srcOdd = new byte[src12.Length]; var gotOdd = new byte[got12.Length];
                for (int i = 0; i < w12 * h12; i++)
                {
                    int vOdd = ((i % 254) + 1) * 257 + 128;      // ≤ 254×257+128 = 65346，不越 16bit 界
                    for (int c = 0; c < 3; c++)
                    { srcOdd[i * 6 + c * 2] = (byte)(vOdd & 0xFF); srcOdd[i * 6 + c * 2 + 1] = (byte)(vOdd >> 8); }
                }
                var specD2 = Spec("bt709", "iec61966-2-1", "bt709", "iec61966-2-1");
                specD2.Dither = FfmpegGui.Services.ColorMapping.DitherMode.Ordered4x4;
                CK.TransformRgb48leToRgb8(srcOdd, gotOdd, specD2, w12 * h12, tbl12, tbl12, w12, 0);
                int distinct = new System.Collections.Generic.HashSet<byte>(gotOdd).Count;
                Check(distinct >= 2, $"C12 哨兵：半档输入经抖动必须展开成 ≥2 个不同输出（实得 {distinct} 种）⇒ 抖动确实在这条路上生效");
            }

            // C13 RGBA 出口的抖动必须加在**码域**（`ColorKernels.cs:351-362` 现在加在线性域）
            //   依据：本出口的输入是 8-bit ⇒ 每一个源码值在 8-bit 出口上都**精确可表示**，
            //   所以"抖动的正确行为"在这里就是**整体 no-op**；任何偏离都是纯损伤。
            //   而线性域加法会被 sRGB OETF 趾段斜率（12.92）放大：暗部 ±0.469/255 的线性位移
            //   折算成码值可达 ~10 档 ⇒ 比不抖更差（取证见 docs/COLOR_MATRIX_AUDIT_2026-09-24.md §4.3）。
            //   ⚠ 本出口的**产品调用数为 0**（同 §4.3："只被门禁养着"）⇒ 这条锁的是"两副面孔"不再出现，
            //     以及 P3 把自研出口定样时不许把它复制成活路。
            {
                int w13 = 16, h13 = 16;                       // 16×16 ⇒ 16 个 Bayer 相位全覆盖
                var spec13 = Spec("bt709", "iec61966-2-1", "bt709", "iec61966-2-1");   // 同原色 ⇒ 无矩阵
                var eotf813 = TC.Srgb.BuildEotfTable8();
                var src13 = new byte[w13 * h13 * 4];
                for (int i = 0; i < w13 * h13; i++)
                {
                    byte c13 = (byte)(i % 256);               // 扫遍整条 8bit 码轴（含 0 与 255 端点）
                    src13[i * 4 + 0] = c13; src13[i * 4 + 1] = c13; src13[i * 4 + 2] = c13; src13[i * 4 + 3] = 200;
                }
                var off13 = new byte[src13.Length];
                CK.TransformRgba8(src13, off13, spec13, eotf813, w13, h13);            // Dither 默认 None
                int offBad = 0, firstOff = -1;
                for (int i = 0; i < w13 * h13; i++)
                    for (int c = 0; c < 3; c++)
                        if (off13[i * 4 + c] != src13[i * 4 + c]) { offBad++; if (firstOff < 0) firstOff = src13[i * 4 + c]; }
                Check(offBad == 0,
                    $"C13 对照：不抖时 identity 直通必须逐字节还原（破坏 {offBad} 处，首个源码值 {firstOff}）⇒ 否则下面两条是编码表的锅，不是抖动的锅");

                var specD13 = Spec("bt709", "iec61966-2-1", "bt709", "iec61966-2-1");
                specD13.Dither = FfmpegGui.Services.ColorMapping.DitherMode.Ordered4x4;
                var on13 = new byte[src13.Length];
                CK.TransformRgba8(src13, on13, specD13, eotf813, w13, h13);
                int changed = 0, maxDev = 0, darkestHit = 255;
                for (int i = 0; i < w13 * h13; i++)
                    for (int c = 0; c < 3; c++)
                    {
                        int d = Math.Abs(on13[i * 4 + c] - src13[i * 4 + c]);
                        if (d != 0) { changed++; if (src13[i * 4 + c] < darkestHit) darkestHit = src13[i * 4 + c]; }
                        if (d > maxDev) maxDev = d;
                    }
                Check(changed == 0,
                    $"C13 8bit→8bit 精确可表示 ⇒ 开抖动后必须逐字节还原（被改变 {changed} 处，最低的受损源码值 {darkestHit}）");
                Check(maxDev <= 1,
                    $"C13 抖动幅度上限：与源码值最大偏离 {maxDev} 必须 ≤1 档（码域抖动的定义；线性域加会被趾段斜率放大到 ~10）");
                Console.WriteLine($"  C13 读数：改动 {changed}/768 处、最大偏离 {maxDev} 档、最低受损码值 {darkestHit}");

                // 反向哨兵：换成**跨色域矩阵 + 非中性像素**（会产生真正的分数码值）⇒ 抖动必须仍然起作用，
                //   但偏离仍受"码域加 ±0.469 档"的定义约束（`Round(x+t)` vs `Round(x)` 最多差 1）。
                //   ⚠ 输入必须**带彩色**：三条通道相等时，行和为 1 的原色矩阵保中性 ⇒ 输出仍落在整数码上，
                //     抖动"无事可做"是正确行为而不是失效（实测用灰阶跑这条 ⇒ 改动 0/768，误判成"抖动被关掉"）。
                //   没有这条哨兵，"把抖动整段删掉"也能让上面三条全绿 ⇒ 那才是真的降级。
                var srcC = new byte[src13.Length];
                for (int i = 0; i < w13 * h13; i++)
                {
                    srcC[i * 4 + 0] = (byte)(i % 256);
                    srcC[i * 4 + 1] = (byte)((i * 7 + 3) % 256);
                    srcC[i * 4 + 2] = (byte)((i * 13 + 5) % 256);
                    srcC[i * 4 + 3] = 255;
                }
                var specOn13 = Spec("bt709", "iec61966-2-1", "smpte432", "iec61966-2-1");
                var specMx13 = Spec("bt709", "iec61966-2-1", "smpte432", "iec61966-2-1");
                specMx13.Dither = FfmpegGui.Services.ColorMapping.DitherMode.Ordered4x4;
                var mOff = new byte[srcC.Length]; var mOn = new byte[srcC.Length];
                CK.TransformRgba8(srcC, mOff, specOn13, eotf813, w13, h13);
                CK.TransformRgba8(srcC, mOn, specMx13, eotf813, w13, h13);
                int diffM = 0, devM = 0;
                for (int i = 0; i + 4 <= srcC.Length; i += 4)
                    for (int c = 0; c < 3; c++)
                    {
                        int d = Math.Abs(mOn[i + c] - mOff[i + c]);
                        if (d != 0) diffM++;
                        if (d > devM) devM = d;
                    }
                Check(diffM > 0 && devM <= 1,
                    $"C13 哨兵：有矩阵且非中性输入时抖动仍要生效且只在码域展开（实际改动 {diffM}/768 处、最大偏离 {devM} 档；要求 >0 且 ≤1）");
            }

            Console.WriteLine($"  contract: 共 {cells} 格，覆盖豁免 {exempted}，拒绝 {rejects}");
            // 拒绝必须可审计：按策略与原因分组列出（防止“过度拒绝”被一个总数掩盖）
            foreach (var (s, n) in rejectByStrategy)
                Console.WriteLine($"    拒绝 {s,-12} {n,3} 格");
            foreach (var (r, n) in rejectReasons)
                Console.WriteLine($"    · {n,3}× {r}");
        }

        // ───────────────────── P3：内核自洽 / 性能 / 与外部参照互测 ─────────────────

        private static FfmpegGui.Services.ColorMapping.TransformSpec Spec(
            string sp, string st, string dp, string dt, bool matrix = true, bool clamp = true)
        {
            var src = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor.FromCicp(sp, st, sp) ?? throw new Exception($"src {sp}/{st}");
            var dst = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor.FromCicp(dp, dt, dp) ?? throw new Exception($"dst {dp}/{dt}");

            // ⚠️ 亮度域换算必须与生产 `ColorTransformPlan.ToSpec()` **同口径**，否则本探针测的根本
            //    不是真实引擎路径：PQ 的 1.0 = 10000nits、HLG = 1000nits、其余 = 203nits（SDR 参考白）。
            //    旧实现两项都留默认 1 ⇒ PQ→sRGB 不做 49× 换算，与 zimg 参照差出 PSNR 7.3dB，
            //    而那笔账长期被当成「zimg 参照口径分歧（PQ 归一约定不同）」豁免 —— 实为**探针自身的
            //    构造缺陷**（它绕过 ToSpec 手搓 spec），不是引擎的口径分歧。
            double Nits(FfmpegGui.Services.ColorMapping.TransferCurve c)
                => c.Kind switch
                {
                    FfmpegGui.Services.ColorMapping.TransferCurve.CurveKind.Pq
                        => FfmpegGui.Services.ColorMapping.TransferCurve.PqPeakNits,
                    FfmpegGui.Services.ColorMapping.TransferCurve.CurveKind.Hlg
                        => FfmpegGui.Services.ColorMapping.Bt2100Ootf.NominalPeakNits,
                    _ => FfmpegGui.Services.ColorMapping.TransferCurve.SdrWhiteNits,
                };
            double sdr = FfmpegGui.Services.ColorMapping.TransferCurve.SdrWhiteNits;

            // ⚠️ 白名单同步：因本缺陷而被判为「口径分歧」的格子，修复后应从 $knownNonDefects 移除，
            //    否则豁免会把真正的回归也一起免掉（门禁就失去牙齿）。见 verify-color-engine-xcheck.ps1。
            return new FfmpegGui.Services.ColorMapping.TransformSpec
            {
                SrcCurve = src.Curve,
                DstCurve = dst.Curve,
                Matrix = matrix ? src.MatrixTo(dst) : null,
                ClampLinearBeforeEncode = clamp,
                LinearScale = Nits(src.Curve) / sdr,
                EncodeScale = sdr / Nits(dst.Curve),
            };
        }

        // ══════════════════════════════ verdict ══════════════════════════════

        private static void ProbeVerdict()
        {
            CS Mk(string prim, string trc, string name, string? icc, DC conf)
            {
                var d = CS.FromCicp(prim, trc, name)!;
                if (icc != null) { d.SourceIccPath = icc; d.IsIccDerived = true; }
                d.Confidence = conf;
                return d;
            }
            var srgbTag = Mk("bt709", "iec61966-2-1", "sRGB(仅标签)", null, DC.CicpTag);
            var srgbIcc = Mk("bt709", "iec61966-2-1", "sRGB(带ICC)", "t.srgb.icc", DC.IccMetric);
            var p3Icc = Mk("smpte432", "iec61966-2-1", "Display P3(带ICC)", "t.p3.icc", DC.IccMetric);
            var adobe = CS.FromNamedSpace("Adobe RGB")!;
            adobe.SourceIccPath = "t.adobe.icc"; adobe.Confidence = DC.IccMetric;
            var bt2020Sdr = CS.FromCicp("bt2020", "bt709", "BT.2020(SDR)")!;
            bt2020Sdr.Confidence = DC.CicpTag;
            // 与 `contract` 网格同源的其余三类源：HDR-PQ / HDR-HLG / 无标签（按惯例假定）。
            var pq2020 = CS.FromCicp("bt2020", "smpte2084", "BT.2100 PQ", nitsOfLinearOne: TC.PqPeakNits)!;
            pq2020.Confidence = DC.CicpTag;
            var hlg2020 = CS.FromCicp("bt2020", "arib-std-b67", "BT.2100 HLG", nitsOfLinearOne: 1000.0)!;
            hlg2020.Confidence = DC.CicpTag;
            var untagged = FfmpegGui.Services.ColorMapping.ColorSpaceIdentify.AssumeByConvention()!;

            string[] CodesOf(CP pl)
            {
                var v = pl.Verdict;
                if (v == null) return Array.Empty<string>();
                var a = new string[v.Degradations.Count];
                for (int i = 0; i < a.Length; i++) a[i] = v.Degradations[i].Code;
                return a;
            }
            bool Has(CP pl, string code) => Array.IndexOf(CodesOf(pl), code) >= 0;
            string Show(CP pl) => $"{pl.Action}/kind={pl.Verdict?.Kind}/bit={pl.OutBitDepth}/codes=[{string.Join(",", CodesOf(pl))}]";

            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            void Note(CP pl) { foreach (var c in CodesOf(pl)) seen.Add(c); }

            // ══════════ ① BitDepthReduced：出口位深 16 → 8（发生变换 + 8bit 容器上限）══════════
            // 判据来源：`ColorStrategyPlanning.PlanWithPolicy` 的 OutBitDepth 赋值
            //   （Action==Map && ColorLoss!=None && TargetBitDepth<=8 && !Requires16BitIntermediate && RgbNative）。
            // 用例：S4 手动 Adobe RGB → Display P3、输出 PNG（RgbNative、上限 16bit 但目标位深 8）。
            CI ManualToP3(string fmt, int depth, CS? target = null) => new()
            {
                Strategy = ST.Manual, Source = adobe, Target = target ?? p3Icc, TargetExplicit = true,
                Format = CE.CapsFor(fmt), TargetBitDepth = depth, AllowApproximateCurve = false,
                Manual = MI.Create(true, "Display P3", "smpte432", "iec61966-2-1", null),
            };
            {
                var pos = CE.Plan(ManualToP3("png", 8));
                Note(pos);
                Check(pos.Action == CA.Map && pos.OutBitDepth == 8,
                    $"BitDepthReduced 正向前置：需 Map + OutBitDepth=8（实为 {Show(pos)}）");
                Check(pos.Verdict?.Kind == VK.ProceedWithDegradation && Has(pos, DG.BitDepthReduced),
                    $"BitDepthReduced 正向：{DG.BitDepthReduced} 必须被登记且 Kind={VK.ProceedWithDegradation}（实为 {Show(pos)}）");

                // 负控：只把**目标位深 8 → 16**（其余一字不动）⇒ 该损失必须消失，且仍然产出。
                var neg = CE.Plan(ManualToP3("png", 16));
                Note(neg);
                Check(neg.Action == CA.Map && neg.OutBitDepth == 16,
                    $"BitDepthReduced 负控前置：16bit 目标仍须产出且 OutBitDepth=16（实为 {Show(neg)}）");
                Check(!Has(neg, DG.BitDepthReduced),
                    $"BitDepthReduced 负控：目标位深 8→16 后 {DG.BitDepthReduced} 必须消失（实为 {Show(neg)}）");
            }

            // ══════════ ② GamutClipped：源色域超出目标（裁切/GMO）══════════
            // 判据来源：`ColorTransformPlan.GamutOutsideDestination`（PlanCore 里由 GamutContainedIn 置位）。
            {
                var pos = CE.Plan(ManualToP3("png", 8));
                Check(pos.GamutOutsideDestination && Has(pos, DG.GamutClipped),
                    $"GamutClipped 正向：Adobe RGB → Display P3 越界必须登记 {DG.GamutClipped}（实为 {Show(pos)}）");

                // 负控：只把**目标改成超集 BT.2020(SDR)**（其余一字不动）⇒ 越界消失，该 Code 必须消失。
                var neg = CE.Plan(ManualToP3("png", 8, bt2020Sdr));
                Note(neg);
                Check(neg.Action != CA.Reject,
                    $"GamutClipped 负控前置：换超集目标仍须产出（实为 {Show(neg)}）");
                Check(!neg.GamutOutsideDestination && !Has(neg, DG.GamutClipped),
                    $"GamutClipped 负控：目标改为超集 BT.2020(SDR) 后 {DG.GamutClipped} 必须消失（实为 {Show(neg)}）");
            }

            // ══════════ ②′ GamutCompressed：开关开 + 源越界 ⇒ GMO 压缩（与 GamutClipped 互斥）══════════
            // 判据来源：`ColorTransformPlan.GamutMap`（PlanCore 的**单点置位** =
            //   `GamutOutsideDestination && policy.AllowGamutCompression && 目标亮度系数可取`）。
            // ⚠ 本组是本轮（2026-09-17）补的**零覆盖**：`GamutCompressed` 与 `ColorLoss=GamutCompression`
            //   此前没有任何门禁断言（`--color-gamut-map` 接进引擎后只靠 H1 后端日志间接可见）。
            {
                CI GmoOn(string fmt, int depth)
                {
                    var it = ManualToP3(fmt, depth);
                    it.AllowGamutCompression = true;
                    return it;
                }
                var pos = CE.Plan(GmoOn("png", 8));
                Note(pos);
                Check(pos.GamutOutsideDestination && pos.GamutMap,
                    $"GamutCompressed 正向前置：源越界 + 开关开 ⇒ GamutMap 必须置位（实为 {Show(pos)}）");
                Check(pos.Verdict?.Kind == VK.ProceedWithDegradation && Has(pos, DG.GamutCompressed),
                    $"GamutCompressed 正向：{DG.GamutCompressed} 必须被登记且 Kind={VK.ProceedWithDegradation}（实为 {Show(pos)}）");
                Check(pos.ColorLoss == CL.GamutCompression,
                    $"GamutCompressed 正向：ColorLoss 必须是 {CL.GamutCompression}（实为 {pos.ColorLoss}）");
                Check(!Has(pos, DG.GamutClipped),
                    $"GamutCompressed 正向：与 {DG.GamutClipped} 互斥，不得同时登记（实为 {Show(pos)}）");

                // 负控：只把**开关关掉**（其余一字不动）⇒ GamutCompressed 消失、GamutClipped 回来。
                // 这是「判据真的跟着开关走」的直接证据：若 GamutMap 被写成常量，本组必然转红。
                var neg = CE.Plan(ManualToP3("png", 8));
                Note(neg);
                Check(!neg.GamutMap && neg.GamutOutsideDestination,
                    $"GamutCompressed 负控前置：开关关 ⇒ GamutMap=false 且仍越界（实为 {Show(neg)}）");
                Check(!Has(neg, DG.GamutCompressed) && Has(neg, DG.GamutClipped),
                    $"GamutCompressed 负控：开关关后 {DG.GamutCompressed} 必须消失、{DG.GamutClipped} 必须回来（实为 {Show(neg)}）");

                // 越界判据必须**独立于**开关（本条是变异逼出来的）：把 `GamutOutsideDestination` 从
                // GamutMap 的判据里删掉（只留 AllowGamutCompression）时，上面 6 条**全绿**
                // —— 因为源越界那格照样压、开关关那格照样裁。只有"开关开 + **未越界**"这一格能分辨：
                // 它必须 `GamutMap=false`（否则"允许压缩"被误当成"必须压缩"，未越界内容也会被推上 H1 后端）。
                var inGamut = new CI
                {
                    Strategy = ST.Manual, Source = srgbTag, Target = p3Icc, TargetExplicit = true,
                    Format = CE.CapsFor("png"), TargetBitDepth = 8, AllowApproximateCurve = false,
                    AllowGamutCompression = true,
                    Manual = MI.Create(true, "Display P3", "smpte432", "iec61966-2-1", null),
                };
                var ig = CE.Plan(inGamut);
                Note(ig);
                Check(!ig.GamutOutsideDestination && !ig.GamutMap && ig.ColorLoss != CL.GamutCompression,
                    $"GamutCompressed 越界判据：开关开但源**未越界**（sRGB ⊆ Display P3）⇒ 不得置位 GamutMap（实为 {Show(ig)}）");
            }

            // ══════════ ③ UnlabeledAssumedSrgb：产物无标注、按隐含 sRGB 处理 ══════════
            // 判据来源：`ColorTransformPlan.UnlabeledAssumedSrgb`。
            // 用例：S2 携带 + sRGB 源（仅 CICP 标签、无 ICC）+ JPEG（无 CICP 通路、未标注即 sRGB 成立）
            //   ⇒ PlanCarry 结局 2b（无标注直通）。
            CI CarryJpg(CS src, string fmt) => new()
            {
                Strategy = ST.CarryIcc, Source = src, Format = CE.CapsFor(fmt),
                TargetBitDepth = 8, AllowApproximateCurve = false,
            };
            {
                var pos = CE.Plan(CarryJpg(srgbTag, "jpg"));
                Note(pos);
                Check(pos.Action == CA.None && pos.UnlabeledAssumedSrgb && Has(pos, DG.UnlabeledAssumedSrgb),
                    $"UnlabeledAssumedSrgb 正向：S2 × sRGB标签源 × jpg 必须登记该 Code（实为 {Show(pos)}）");

                // 负控：只把**容器 jpg → png**（可写 CICP）⇒ 走"继承 CICP 标签"形态，该 Code 必须消失。
                var neg = CE.Plan(CarryJpg(srgbTag, "png"));
                Note(neg);
                Check(neg.Action != CA.Reject && !neg.UnlabeledAssumedSrgb && !Has(neg, DG.UnlabeledAssumedSrgb),
                    $"UnlabeledAssumedSrgb 负控：容器 jpg→png（可写 CICP）后该 Code 必须消失（实为 {Show(neg)}）");
            }

            // ══════════ ④ NoIccCarrier：源有 ICC 但产物不附 ICC ══════════
            // 判据来源：`!AttachIcc && Src.SourceIccPath != null`（已产出的格）。
            // 用例：S3 仅 CICP + Display P3（带 ICC）+ AVIF（可写 CICP；源已可 CICP 精确表达 ⇒ 不改像素）
            //   —— 刻意选 **AVIF**（CanCarryArbitraryIcc=true）以**隔离** NoIccCarrier，
            //      避免与 ContainerCannotCarryIcc 同时命中（后者另有用例）。
            {
                var pos = CE.Plan(new CI
                {
                    Strategy = ST.BakeCicpOnly, Source = p3Icc, Format = CE.CapsFor("avif"),
                    TargetBitDepth = 8, AllowApproximateCurve = false,
                });
                Note(pos);
                // ⚠ 前置条件里的"源带 ICC"必须看**意图的源描述符**：`pos.Src` 在 PlanCicpOnly 的
                //   等价分支返回的是 Fresh() 计划（Src==null）⇒ 用 pos.Src 会把前置写成假红。
                Check(pos.Action != CA.Reject && !pos.AttachIcc && p3Icc.SourceIccPath != null,
                    $"NoIccCarrier 正向前置：须产出、不附 ICC、且源带 ICC（实为 {Show(pos)}）");
                Check(pos.Verdict?.Kind == VK.ProceedWithDegradation && Has(pos, DG.NoIccCarrier),
                    $"NoIccCarrier 正向：{DG.NoIccCarrier} 必须被登记（实为 {Show(pos)}）");
                Check(!Has(pos, DG.ContainerCannotCarryIcc),
                    $"NoIccCarrier 用例须隔离：AVIF 可携带任意 ICC ⇒ 不得同时命中 {DG.ContainerCannotCarryIcc}（实为 {Show(pos)}）");

                // 负控：只把**源从"带 ICC"换成"仅 CICP 标签"**（其余一字不动）⇒ 该 Code 必须消失。
                var neg = CE.Plan(new CI
                {
                    Strategy = ST.BakeCicpOnly, Source = srgbTag, Format = CE.CapsFor("avif"),
                    TargetBitDepth = 8, AllowApproximateCurve = false,
                });
                Note(neg);
                Check(neg.Action != CA.Reject && !Has(neg, DG.NoIccCarrier),
                    $"NoIccCarrier 负控：源改为无 ICC（仅 CICP 标签）后该 Code 必须消失（实为 {Show(neg)}）");
            }

            // ══════════ ⑤ ContainerCannotCarryIcc：容器能力表判 `!CanCarryArbitraryIcc` ══════════
            // 判据来源：`PlanPolicy.Format.CanCarryArbitraryIcc == false`（能力表事实，非从计划字段反猜）。
            // 用例：S1 推荐 + sRGB（带 ICC）+ GIF（无任何色彩管理通路）⇒ 映射到 sRGB 工作空间、无标注输出。
            CI RecommendedGif(CS src, string fmt) => new()
            {
                Strategy = ST.Recommended, Source = src, Format = CE.CapsFor(fmt),
                TargetBitDepth = 8, AllowApproximateCurve = false, CarryScope = CSC.All,
            };
            {
                var pos = CE.Plan(RecommendedGif(srgbIcc, "gif"));
                Note(pos);
                Check(pos.Action != CA.Reject && !CE.CapsFor("gif").CanCarryArbitraryIcc,
                    $"ContainerCannotCarryIcc 正向前置：须产出且能力表判不可携带（实为 {Show(pos)}）");
                Check(Has(pos, DG.ContainerCannotCarryIcc) && Has(pos, DG.NoIccCarrier),
                    $"ContainerCannotCarryIcc 正向：必须同时登记 {DG.ContainerCannotCarryIcc}（结构性归因）"
                    + $"与 {DG.NoIccCarrier}（已发生的损失）（实为 {Show(pos)}）");

                // 负控：只把**容器 gif → png**（可携带任意 ICC）⇒ 改走 L1 携带，两个 Code 都必须消失。
                var neg = CE.Plan(RecommendedGif(srgbIcc, "png"));
                Note(neg);
                Check(neg.AttachIcc && neg.IccPath != null,
                    $"ContainerCannotCarryIcc 负控前置：png 应改走 L1 携带（实为 {Show(neg)}）");
                Check(!Has(neg, DG.ContainerCannotCarryIcc) && !Has(neg, DG.NoIccCarrier),
                    $"ContainerCannotCarryIcc 负控：容器 gif→png 后该 Code 与 {DG.NoIccCarrier} 都必须消失（实为 {Show(neg)}）");
            }

            // ══════════ ⑥ Reject 格**不得**登记任何损失（Degradations 的语义是"已发生"）══════════
            // 用例：S3 仅 CICP + Display P3（带 ICC）+ JPEG —— 契约不可兑现（JPEG 无 CICP 通路）⇒ Reject。
            {
                var rej = CE.Plan(new CI
                {
                    Strategy = ST.BakeCicpOnly, Source = p3Icc, Format = CE.CapsFor("jpg"),
                    TargetBitDepth = 8, AllowApproximateCurve = false,
                });
                Note(rej);
                var rv = rej.Verdict!;
                Check(rej.Action == CA.Reject, $"Reject 用例前置：S3 × P3带ICC × jpg 应 Reject（实为 {Show(rej)}）");
                Check(rv.Kind == VK.Reject && rv.Degradations.Count == 0,
                    $"Reject 格不得登记 Degradations（实为 {Show(rej)}）");
                Check(rv.Alternatives.Count > 0,
                    $"Reject 格应给出结构化 Alternatives（实为 {rv.Alternatives.Count} 条）");
            }

            // ══════════ ⑥′ ToneMapNotAppliedToGainMap：GainMap 出口**不消费**计划层 tonemap ══════════
            // 判据来源：`CollectDegradations` 末条（`p.GainMap && it.ToneMapRequested && !it.ToneMapAuto`）。
            // 用例（= 实测的「误伤」格）：**SDR 源** + 用户**显式** tonemap + GainMap。
            //   改动前该组合在 `ColorIntentFactory` 的 tonemap 装配门被 Reject（连 GainMap 出口都没进）；
            //   现在放行并由本 Code 诚实登记「该选项未生效」（产出不变，只有 Kind 翻成降级）。
            CI GainMapWithToneMap(CS src, bool gm, bool auto) => new()
            {
                Strategy = ST.Recommended, Source = src, Format = CE.CapsFor("jpg"),
                TargetBitDepth = 8, AllowApproximateCurve = false, CarryScope = CSC.All,
                GainMapRequested = gm, ToneMapRequested = true,
                ToneMap = FfmpegGui.Services.ColorMapping.ToneMapMode.Hable, ToneMapAuto = auto,
            };
            {
                var pos = CE.Plan(GainMapWithToneMap(srgbTag, true, false));
                Note(pos);
                Check(pos.GainMap && pos.Action != CA.Reject,
                    $"ToneMapNotAppliedToGainMap 正向前置：须为 GainMap 出口且产出（实为 {Show(pos)}）");
                Check(pos.Verdict?.Kind == VK.ProceedWithDegradation && Has(pos, DG.ToneMapNotAppliedToGainMap),
                    $"ToneMapNotAppliedToGainMap 正向：{DG.ToneMapNotAppliedToGainMap} 必须被登记且 "
                    + $"Kind={VK.ProceedWithDegradation}（实为 {Show(pos)}）");

                // 负控①：只把 **GainMap 关掉**（其余一字不动）⇒ 该 Code 必须消失。
                //   这是「判据真的跟着 GainMap 位走」的直接证据：若判据被写成只看 ToneMapRequested，本组必红。
                var neg1 = CE.Plan(GainMapWithToneMap(srgbTag, false, false));
                Note(neg1);
                Check(!neg1.GainMap && !Has(neg1, DG.ToneMapNotAppliedToGainMap),
                    $"ToneMapNotAppliedToGainMap 负控①：非 GainMap 格不得登记该 Code（实为 {Show(neg1)}）");

                // 负控②：只把 **ToneMapAuto 打开**（GainMap 仍开）⇒ 该 Code 必须消失。
                //   理由：自动兜底（HDR 源 + 容器不支持 HDR）的目的就是「把 HDR 压成可看的 SDR 底图」，
                //   而 GainMap 出口的分段 Reinhard 正是干这件事 ⇒ 目的已达成，不构成损失。
                //   少了这条，「每一次 HDR→JPEG GainMap 都被误报成降级」不会被任何断言发现。
                var neg2 = CE.Plan(GainMapWithToneMap(srgbTag, true, true));
                Note(neg2);
                Check(neg2.GainMap && !Has(neg2, DG.ToneMapNotAppliedToGainMap),
                    $"ToneMapNotAppliedToGainMap 负控②：ToneMapAuto 格不得登记该 Code（实为 {Show(neg2)}）");
            }

            // ══════════ ⑦ 自洽性 + 覆盖面（对 contract 同源网格全格断言）══════════
            var formats = new[] { "png", "jpg", "tiff", "webp", "avif", "jxl", "gif", "heic" };
            var sources = new (string Name, CS D)[]
                { ("sRGB仅标签", srgbTag), ("sRGB带ICC", srgbIcc), ("P3带ICC", p3Icc), ("HDR-PQ", pq2020),
                  ("HDR-HLG", hlg2020), ("Adobe带ICC", adobe), ("无标签", untagged) };
            var strategies = new[] { ST.Recommended, ST.CarryIcc, ST.BakeCicpOnly, ST.Manual };
            var viol = new System.Collections.Generic.List<string>();
            int cells = 0, degradated = 0, produced = 0, rejected = 0, producedEmpty = 0;
            foreach (var st in strategies)
                foreach (var fmt in formats)
                    foreach (var (sname, src) in sources)
                    {
                        var it = new CI
                        {
                            Strategy = st, Source = src, Format = CE.CapsFor(fmt),
                            TargetBitDepth = 8, AllowApproximateCurve = false, CarryScope = CSC.All,
                            Manual = st == ST.Manual ? MI.Create(true, "Display P3", "smpte432", "iec61966-2-1", null)
                                                     : MI.Create(false, null, null, null, null),
                        };
                        if (st == ST.Manual) { it.TargetExplicit = true; it.Target = p3Icc; }
                        var p = CE.Plan(it);
                        cells++;
                        Note(p);
                        var v = p.Verdict;
                        if (v == null) { viol.Add($"[{st}/{fmt}/{sname}] Verdict 为 null"); continue; }
                        if (v.Kind == VK.ProceedWithDegradation) degradated++;
                        if (p.Action == CA.Reject) rejected++; else produced++;
                        void V(bool ok, string msg) { if (!ok) viol.Add($"[{st}/{fmt}/{sname}] {msg}"); }
                        // 自洽（三条互相钉住；**不**写成 `Kind==Proceed ⇔ Count==0`，
                        // 因为 Reject 格同样是 Count==0 却不是 Proceed）：
                        //   ① ProceedWithDegradation ⇔ 非空；
                        //   ② Proceed ⇒ 空（即"Proceed + 非空"这一不自洽状态被禁掉）；
                        //   ③ Reject ⇔ Action==Reject，且 Reject 格必须为空。
                        V((v.Kind == VK.ProceedWithDegradation) == (v.Degradations.Count > 0),
                          $"Kind={v.Kind} 与 Degradations.Count={v.Degradations.Count} 不自洽");
                        V(v.Kind != VK.Proceed || v.Degradations.Count == 0,
                          $"Kind=Proceed 却带 {v.Degradations.Count} 条 Degradations（不自洽状态）");
                        V((v.Kind == VK.Reject) == (p.Action == CA.Reject),
                          $"Kind={v.Kind} 与 Action={p.Action} 不自洽");
                        V(p.Action != CA.Reject || v.Degradations.Count == 0,
                          $"Reject 格却登记了 {v.Degradations.Count} 条 Degradations");
                        if (p.Action != CA.Reject && v.Degradations.Count == 0) producedEmpty++;
                        // 投影一致性：Action/Reason/Advice 必须是计划的兼容投影（一字不改）
                        V(v.Action == p.Action && v.Reason == p.Reason && v.Advice == p.Advice,
                          "Verdict 的 Action/Reason/Advice 与计划不一致（兼容投影被破坏）");
                        // Code 去重：同一 Code 不得重复登记（GUI/门禁按 Code 计数）
                        for (int i = 0; i < v.Degradations.Count; i++)
                            for (int j = i + 1; j < v.Degradations.Count; j++)
                                V(v.Degradations[i].Code != v.Degradations[j].Code,
                                  $"重复登记 Code={v.Degradations[i].Code}");
                    }
            Check(viol.Count == 0,
                $"verdict 自洽性：{cells} 格（产出 {produced} 含降级 {degradated}、拒绝 {rejected}）零违规"
                + (viol.Count > 0 ? " 例如: " + string.Join(" | ", viol.GetRange(0, Math.Min(8, viol.Count))) : ""));
            // 任务书要求的等价表述（**限定在已产出的格上**）：`Kind==Proceed ⇔ Degradations 为空`。
            Check(producedEmpty + degradated == produced,
                $"verdict 自洽性：已产出 {produced} 格必须恰好二分（Proceed {producedEmpty} + "
                + $"{VK.ProceedWithDegradation} {degradated}）（实为 {producedEmpty + degradated}）");

            // 覆盖面双向钉住：**每个已填 Code 至少被命中一次**（否则正向断言缺失）；
            // 且**不得**出现未登记在案（未被本模式断言）的 Code（否则新 Code 会静默溜过门禁）。
            var filled = new[] { DG.BitDepthReduced, DG.GamutClipped, DG.GamutCompressed, DG.UnlabeledAssumedSrgb,
                                 DG.NoIccCarrier, DG.ContainerCannotCarryIcc, DG.ToneMapNotAppliedToGainMap };
            foreach (var c in filled)
                Check(seen.Contains(c), $"覆盖：{c} 至少被一条正向断言命中");
            var unexpected = new System.Collections.Generic.List<string>();
            foreach (var c in seen) if (Array.IndexOf(filled, c) < 0) unexpected.Add(c);
            Check(unexpected.Count == 0,
                $"覆盖：不得出现未被断言登记的 Code（实际多出: {string.Join(",", unexpected)}）");
        }

        // ══════════════════════════════ wire ══════════════════════════════

        /// <summary>
        /// wire —— 验证 A1 装配点：选项 → ColorIntent → Plan。
        /// 只验“装配与决策正确”，不改生产路径（A2 负现）。任何不确定均应返回 null 而非半吊子意图。
        /// </summary>
        private static void ProbeWire()
        {
            var sdr = "tests/output/sources/src_8bit.png";
            var pq = "tests/output/validate/src_hdr.png";   // 带真 cICP(9,16) 的 HDR 素材（由 ensure-fixtures 产出）
            if (!File.Exists(sdr) || !File.Exists(pq)) { Check(false, "素材缺失（先跑 generate-sources.ps1 + ensure-fixtures.ps1）"); return; }
            // 防“假 HDR 素材”：源本身必须被探测为 PQ，否则后面的 tonemap 用例没有意义
            var mHdr = FfmpegGui.Services.FfmpegCommandBuilder.ProbeInputColorMetadata(pq);
            Check(mHdr.colorTrc == "smpte2084",
                $"HDR 素材确实被探测为 PQ（实际 trc={mHdr.colorTrc ?? "null"}）——否则素材未带 cICP");
            FfmpegGui.Models.FfmpegOptions O(string fmt, string? cs = null, string? tm = null, string? inPath = null,
                                            double? sdrPeak = null, bool adv = false, string? trc = null)
                => new() { Format = fmt, ColorSpace = cs ?? "auto", ColorStrategy = FfmpegGui.Models.ColorStrategy.Recommended,
                           ColorToneMap = tm ?? "none", BitDepth = 8, UseAdvancedColorParameters = adv, ColorTrc = trc,
                           // 路由门要求“引擎已承接所设参数”；默认质量才算不干涉编码出口
                           Quality = FfmpegGui.Models.FfmpegOptions.GetDefaultQuality(fmt),
                           ColorSdrPeakNits = sdrPeak ?? 100 };

            // 1) SDR 源 + auto 目标：可装配，且不应请求映射
            var i1 = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(O("png"), sdr, out var r1);
            Check(i1 != null && !i1!.ToneMapRequested && i1.Target == null, $"png/auto 装配 OK（reason={r1 ?? "无"}）");
            var p1 = CE.Plan(i1!);
            Check(p1.IsOk && p1.ColorLoss == CL.None, $"png/auto 计划不改像素（{p1.Action}/{p1.ColorLoss}）");

            // 2) 显式目标 P3：可装配且应做矩阵映射
            var i2 = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(O("png", "Display P3"), sdr, out var r2);
            var p2 = i2 == null ? null : CE.Plan(i2);
            Check(i2 != null && i2.TargetExplicit && i2.Target?.PrimariesToken == "smpte432",
                $"目标 P3 解析正确（{(i2 == null ? r2 : i2.Target?.Name)}）");
            Check(p2 != null && p2.IsOk && p2.Matrix != null, $"P3 目标发生矩阵映射（act={p2?.Action}）");

            // （此处曾加过一条“BT.2020 目标的 ICC TRC ≡ 输出 TRC”正例，但它挑错了格子：
            //   PNG 的 CanHaveBothIccAndCicp=false ⇒“ICC 优先”会把 Out* 清空（合法），
            //   没有“两条都声明”可比。面它倒出了一个更结构的问题：
            //   QueueProcessor 的 png3 后置步骤会往 PNG 写 cICP，不看能力表的“不可共存”裁决
            //   ⇒ “PNG 能否 ICC+CICP 并存”在两处答案不同，这是 P7 步骤 3 要收敛的范围。）

            // 3) HDR 源 + bt2446a + SDR 目标：挂载 Method A 并归类 ToneMapped
            var i3 = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(O("png", "sRGB", "bt2446a", pq, 300), pq, out var r3);
            var p3 = i3 == null ? null : CE.Plan(i3);
            var TM2 = FfmpegGui.Services.ColorMapping.ToneMapMode.Bt2446A;
            Check(i3 != null && i3.ToneMapRequested && i3.ToneMap == TM2, $"bt2446a 装配（{(i3 == null ? r3 : i3.ToneMap.ToString())}）");
            // ⚠ 加严：必须确认 Tone 真的“激活”（headroom>1），而不是靠另一条分支给出 ToneMapped
            Check(p3?.Tone?.Mode == TM2 && p3.Tone.Active && p3.Tone.Headroom > 1.5
                  && p3.ColorLoss == CL.ToneMapped,
                $"bt2446a 真的生效（active={p3?.Tone?.Active} headroom={p3?.Tone?.Headroom:F2} loss={p3?.ColorLoss}）");
            Check(System.Math.Abs((p3?.Tone?.SdrPeakNits ?? 0) - 300) < 1e-9, $"L_SDR 透传（{(p3?.Tone?.SdrPeakNits).ToString() ?? "-"}）");

            // 4) 拒绝/回退路径：必须返回 null 并给原因
            Check(FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(O("png", "sRGB", "bt2390", pq), pq, out var rr1) == null
                  && (rr1 ?? "").Contains("出处"), $"bt2390 不装配（{rr1}）");
            Check(FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(O("png", "sRGB", "bogus"), sdr, out var rr2) == null,
                  $"未知 tone-map 值不装配（{rr2}）");
            // **2026-09-17 契约更新**：GIF 已接入引擎 ⇒ 意图**必须装配**（原断言 `== null` 锁的是
            // "GIF 尚未接入"这一历史状态）。「无色彩通路」这个**能力事实**不再由装配层表达，
            // 改由规划层给结局（不改像素 / 映射到 sRGB 工作空间，两种都**不写标注**）。
            // ⚠ 无通路容器里**放行 gif 与 jxr**（两者都已接入引擎；jxr 的执行出口 = BMP/TIFF 中转 +
            //   JxrEncApp，2026-09-17 接入）。DNG 需传感器 Bayer 数据，PPM/BMP/HEIC 无接入计划
            //   ⇒ "其余不放行"仍由下面的 heic 守着。
            var ig = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(O("gif"), sdr, out var rr3);
            Check(ig != null, $"GIF 已接入 ⇒ 意图必须装配（reason={rr3 ?? "无"}）");
            if (ig != null)
            {
                var pg = CE.Plan(ig);
                Check(pg.IsOk && pg.UnlabeledAssumedSrgb,
                    $"GIF 计划：不写任何色彩标注（Action={pg.Action} UnlabeledAssumedSrgb={pg.UnlabeledAssumedSrgb}）");
            }
            // JXR 与 GIF **同款结局**（容器无色彩通路 ⇒ 映射到 sRGB 工作空间且不写标注），
            // 且必须**置 ExternalEncoderExit** —— 否则计划说"像素改了"、执行层却走 ffmpeg 编码器，
            // 而本工具链的 ffmpeg **没有 jxr 编码器** ⇒ 必然产不出产物（不是静默降级，是直接失败）。
            var ij = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(O("jxr"), sdr, out var rr3b);
            Check(ij != null, $"JXR 已接入 ⇒ 意图必须装配（reason={rr3b ?? "无"}）");
            if (ij != null)
            {
                Check(ij.ExternalEncoderExit, "JXR 意图必须置 ExternalEncoderExit（ffmpeg 无 jxr 编码器）");
                var pj = CE.Plan(ij);
                Check(pj.IsOk && pj.ExternalEncoderExit && pj.UnlabeledAssumedSrgb,
                    $"JXR 计划：ExternalEncoderExit + 不写标注（Action={pj.Action} exit={pj.ExternalEncoderExit} "
                    + $"unlabeled={pj.UnlabeledAssumedSrgb}）");
            }
            Check(FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(O("heic"), sdr, out var rr4) == null,
                  $"HEIC（无写入通路）不装配（{rr4}）");
            // HDR 源 + 显式 HDR 目标 + 请求映射 ⇒ 矛盾，不装配
            Check(FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(O("png", "BT.2020 PQ", "hable", pq), pq, out var rr5) == null,
                  $"tone-map 与 HDR 目标矛盾时不装配（{rr5}）");
            // SDR 源却请求 tonemap ⇒ 不装配（避免无意义压暗）
            Check(FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(O("png", "sRGB", "mobius"), sdr, out var rr6) == null,
                  $"非 HDR 源请求 tonemap 时不装配（{rr6}）");

            // 5) A2 前置件：路由判据（纯函数，与 QueueProcessor 插入点无关）
            // **2026-09-16 契约更新**：默认改为**走引擎**（用户指令「清理传统管线」），
            // `legacy` 降级为显式逃生门。故这里同时锁两条：
            //   ① 显式 legacy ⇒ 不路由（逃生门有效，可用于 A/B 与排查）；
            //   ② **默认（不传参）⇒ 路由**（默认值本身就是契约，必须有断言守着，
            //      否则"哪天被改回 legacy"无人发现）。
            var R = typeof(FfmpegGui.Services.ColorMapping.ColorEngineRouter);
            var eng = O("png"); eng.ColorEngine = "engine";
            var leg = O("png"); leg.ColorEngine = "legacy";
            var def = O("png");
            Check(!FfmpegGui.Services.ColorMapping.ColorEngineRouter.ShouldRoute(leg, "o.png", false),
                "显式 legacy ⇒ 不路由（逃生门有效）");
            Check(FfmpegGui.Services.ColorMapping.ColorEngineRouter.ShouldRoute(def, "o.png", false),
                "**默认（不传 --color-engine）⇒ 路由到引擎**（默认值即契约）");
            Check(FfmpegGui.Services.ColorMapping.ColorEngineRouter.ShouldRoute(eng, "o.png", false), "engine + png ⇒ 路由");
            Check(FfmpegGui.Services.ColorMapping.ColorEngineRouter.ShouldRoute(eng, "o.webp", false), "engine + webp ⇒ 路由");
            Check(FfmpegGui.Services.ColorMapping.ColorEngineRouter.ShouldRoute(eng, "o.tif", false), "engine + tif ⇒ 路由");
            // **2026-09-17**：gif 接入引擎出口（调色板链 + 第二输入补 alpha）⇒ 必须路由。
            Check(FfmpegGui.Services.ColorMapping.ColorEngineRouter.ShouldRoute(eng, "o.gif", false),
                "engine + gif ⇒ 路由（2026-09-17 接入）");
            // 负控：仍在白名单外的格式不得被"连带放行"（ppm 无色彩通路且无接入计划）。
            // ⚠ 2026-09-17（S2）：`BlockReason` 返回 `EngineBlock?` ⇒ 判据一律打 **Code**（禁止匹配 Text）。
            var bppm = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(eng, "o.ppm", false);
            Check(bppm?.Code == "FormatNotSupported",
                $"engine + ppm ⇒ 不路由且 Code=FormatNotSupported（{bppm?.Text}）");
            // **2026-09-17：jxl 接入引擎出口（ffmpeg 的 libjxl 子情形）**。两条断言成对锁住分级，
            // 二者**只差一个变量**（EncoderBackend）⇒ "把该变量改回去"就是一次干净的变异：
            //   ① backend=Ffmpeg ⇒ **路由**（与 png/avif/gif 同型）；
            //   ② backend=Cjxl（本机装了 cjxl 时的**默认后端**，EncoderDetectionService:55）
            //      ⇒ **同日第二轮起也路由**（cjxl 执行出口接入，见下方契约变更说明）。
            // 分级结论因此从「按后端拦」变成「按**出口是否存在**判」—— 这正是本轮的实质：
            // 原来挡住 Cjxl 的**不是**"它是外部编码器"，而是"引擎没有能兑现它的出口"。
            var jxlFf = O("jxl"); jxlFf.ColorEngine = "engine";
            jxlFf.EncoderBackend = FfmpegGui.Services.EncoderBackend.Ffmpeg;
            Check(FfmpegGui.Services.ColorMapping.ColorEngineRouter.ShouldRoute(jxlFf, "o.jxl", false),
                "engine + jxl(backend=Ffmpeg) ⇒ 路由（2026-09-17 接入 ffmpeg-libjxl 子情形）");
            // **2026-09-17（cjxl 出口轮）：契约变更 —— jxl+Cjxl 由"被挡"改为"路由"**。
            // 变更理由：引擎新增了 cjxl 执行出口（`RawColorPipeline.EncodeJxlViaCjxlAsync`），
            // 它**兑现了**原来挡它的那条理由（"cjxl 不吃 rawvideo、色彩语义 ffmpeg 参数表达不了"）：
            // 出口把变换后的 raw 经 PPM/PAM 管道喂 cjxl，色彩语义由 `-x color_space=` /
            // `-x icc_pathname=` / `--intensity_target=` 表达（后两者复用**同一份**
            // `CjxlService.BuildCjxlArguments`，不另写一套）。
            // ⚠ 这是**有意**的契约变更：旧断言（⇒ 不路由）会转红，**不要**为了让旧断言变绿而放弃放宽。
            var jxlCjxl = O("jxl"); jxlCjxl.ColorEngine = "engine";
            jxlCjxl.EncoderBackend = FfmpegGui.Services.EncoderBackend.Cjxl;
            var br = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(jxlCjxl, "o.jxl", false);
            Check(br == null, $"engine + jxl(backend=Cjxl) ⇒ **路由**（2026-09-17 cjxl 出口接入；block={br?.Text ?? "无"}）");
            // 计划位（`ExternalEncoderExit`）：**只有 jxl+Cjxl 置位**。ffmpeg 子情形绝不能也走外部管道
            //（多一个进程、还会丢掉 ffmpeg 侧参数）⇒ 两条成对断言，只差 EncoderBackend 一个变量。
            var itCjxl = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(jxlCjxl, sdr, out var whyCjxl);
            Check(itCjxl is { ExternalEncoderExit: true },
                $"意图装配：jxl + Cjxl ⇒ ExternalEncoderExit=true（{whyCjxl ?? "ok"}）");
            var itFf = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(jxlFf, sdr, out var whyFf);
            Check(itFf is { ExternalEncoderExit: false },
                $"意图装配：jxl + Ffmpeg ⇒ ExternalEncoderExit=false（走普通 ffmpeg 出口；{whyFf ?? "ok"}）");
            // 负控：**其他格式** + 外部后端仍必须被挡 —— 放宽不得外溢（cjpgli/cjpegli 尚无出口）。
            var jpgCjpegli = O("jpg"); jpgCjpegli.ColorEngine = "engine";
            jpgCjpegli.EncoderBackend = FfmpegGui.Services.EncoderBackend.Cjpegli;
            var brJpg = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(jpgCjpegli, "o.jpg", false);
            Check(brJpg?.Code == "ExternalBackend",
                $"engine + jpg(backend=Cjpegli) ⇒ 仍被挡（负控：放宽只对 jxl+Cjxl 生效；Code={brJpg?.Code}）");
            // ── cjxl 出口的命令行形态（纯函数层；CLI 端到端形态另见 verify-color-wiring 第 (10) 段）──
            // 要钉住的不变量：**色彩语义来自计划**（override），既不能凭空生成、也不能回头读 options。
            var cjxlOpts = O("jxl"); cjxlOpts.Quality = 75; cjxlOpts.JxlEffort = 7;
            var argsCs = FfmpegGui.Services.CjxlService.BuildCjxlArguments(
                "-", "o.jxl", cjxlOpts, default, null, "Rec2100PQ", 1000);
            Check(argsCs.Contains("-x color_space=Rec2100PQ") && argsCs.Contains("--intensity_target=1000"),
                $"cjxl 出口命令行：色彩语义与显示峰值均来自计划（{argsCs}）");
            var argsNone = FfmpegGui.Services.CjxlService.BuildCjxlArguments(
                "-", "o.jxl", cjxlOpts, default, null, null, 0);
            Check(!argsNone.Contains("color_space") && !argsNone.Contains("intensity_target"),
                $"cjxl 出口命令行：计划未给色彩语义时**不得凭空生成**标注（{argsNone}）");
            // ── cjxl 的 --photon_noise_iso：三条规则（2026-09-30 实机复现后定，取证 tests/output/t62/）──
            // ① **真执行永不输出 `auto`**：cjxl 的解析器是 `ParseFloat && >= 0`（libjxl
            //    `tools/cjxl_main.cc:ParsePhotonNoiseParameter`），收到 `auto` 会在读 stdin 之前
            //    退出 1、零产物，写方表现为 broken pipe（"管道正在被关闭"）。
            // ② 预览**保留** `auto`：那是"待从 EXIF 解析"的显示意图，不发给编码器。
            // ③ **distance=0 时不发噪声**：实测 `-d 0 --photon_noise_iso=400` 让 99.46% 的 16-bit
            //    样本偏离源（平均偏差 30875/65535），而 jxlinfo 对两者都报 "(possibly) lossless"
            //    —— 噪声由解码端按帧头参数合成，体积只多 10 B ⇒ 从文件大小查不出来。
            //    判据是**实际发出的 distance**，不是 Lossless 复选框（Quality=100 也算数学无损）。
            string pnLog = "";
            Action<string> pnSink = s => pnLog += s;
            var pnLossy = O("jxl"); pnLossy.Quality = 75; pnLossy.JxlEffort = 7;   // distance 2.4（2026-10-02 起按 libjxl 官方曲线；旧公式算出来是 3.8）
            var pnAutoUnresolved = O("jxl"); pnAutoUnresolved.Quality = 75; pnAutoUnresolved.JxlEffort = 7;
            pnAutoUnresolved.CjxlAutoPhotonNoise = true;   // CjxlPhotonNoiseIso 保持 0 = 未解析
            var a1 = FfmpegGui.Services.CjxlService.BuildCjxlArguments(
                "-", "o.jxl", pnAutoUnresolved, default, null, null, 0, false, pnSink);
            Check(!a1.Contains("photon_noise_iso"),
                $"① 真执行 + 未解析的 auto ⇒ **省略该 flag**，绝不发占位串（{a1}）");
            Check(pnLog.Contains("dropped"),
                $"① 省略时**点名**（不静默吞掉用户设置）：[{pnLog.Trim()}]");
            var a2 = FfmpegGui.Services.CjxlService.BuildCjxlArguments(
                "-", "o.jxl", pnAutoUnresolved, default, null, null, 0, true, null);
            Check(a2.Contains("--photon_noise_iso=auto"),
                $"② 预览仍显示 auto 占位（有损距离下）：[{a2}]");
            var pnLossless = O("jxl"); pnLossless.Lossless = true; pnLossless.JxlEffort = 7;
            pnLossless.CjxlPhotonNoiseIso = 400;
            pnLog = "";
            var a3 = FfmpegGui.Services.CjxlService.BuildCjxlArguments(
                "-", "o.jxl", pnLossless, default, null, null, 0, false, pnSink);
            Check(a3.Contains("-d 0") && !a3.Contains("photon_noise_iso"),
                $"③ 无损 ⇒ 只有 -d 0、不带噪声（叠加会静默破坏逐比特无损）（{a3}）");
            Check(pnLog.Contains("photon noise skipped"), $"③ 无损丢弃时点名：[{pnLog.Trim()}]");
            pnLossy.CjxlPhotonNoiseIso = 400;
            var a4 = FfmpegGui.Services.CjxlService.BuildCjxlArguments(
                "-", "o.jxl", pnLossy, default, null, null, 0, false, null);
            Check(a4.Contains("--photon_noise_iso=400"),
                $"④ **正控**：有损 + 数值 ISO ⇒ flag 照发（否则 ①③ 只是「功能没接上」的假绿）（{a4}）");
            var pnQ100 = O("jxl"); pnQ100.Quality = 100; pnQ100.JxlEffort = 7; pnQ100.CjxlPhotonNoiseIso = 400;
            var a5 = FfmpegGui.Services.CjxlService.BuildCjxlArguments(
                "-", "o.jxl", pnQ100, default, null, null, 0, false, null);
            Check(a5.Contains("-d 0.0") && !a5.Contains("photon_noise_iso"),
                $"⑤ 判据是**发出的 distance**而非复选框：Quality=100 ⇒ -d 0.0 ⇒ 同样不发噪声（{a5}）");
            // ── jbrd 无损重封装（2026-09-30：开关默认启用，且此前被三道独立失效永久掐死）──
            // 实测失效链（取证 tests/output/t64/）：模型默认 false + 采集式「面板不可见⇒false」
            //   + 与一只**默认勾着**的「保留 Ultra HDR 增益图」无条件相与 ⇒ 三条里任一条都足以让
            //   它恒假 ⇒ 默认配置下 JPEG→JXL 从来走的是 `-d 3.8` 像素重编码（产物 908 kB，非无损）。
            // ⚠ 语义分工（本组要钉的就是这个）：`DefaultJxlLosslessJpeg` = **开关**默认（true）；
            //   `FfmpegOptions.JxlLosslessJpeg` = **本次有效值**，必须与「输入真是 JPEG」相与 ——
            //   因为 `ImageEncoderArgs` 只看这个布尔就拦掉整个 jxl 引擎路线，而它看不到输入格式。
            Check(FfmpegGui.Models.FfmpegOptions.DefaultJxlLosslessJpeg
                    && !new FfmpegGui.Models.FfmpegOptions().JxlLosslessJpeg,
                "jbrd-① 开关默认开=true 而模型字段(有效值)默认=false；两者必须分家，否则 PNG→JXL 会被误拦出引擎");
            var jbOn = O("jxl"); jbOn.JxlEffort = 7; jbOn.Threads = 0; jbOn.JxlLosslessJpeg = true;
            var jbFile = FfmpegGui.Services.CjxlService.BuildCjxlArguments(
                "in.jpg", "o.jxl", jbOn, default, null, null, 0, false, null);
            Check(jbFile.Contains("-d 0") && jbFile.Contains("--lossless_jpeg=1"),
                $"jbrd-② 有效值+真 JPEG 输入 ⇒ 发出无损重封装（{jbFile}）");
            pnLog = "";
            var jbPipe = FfmpegGui.Services.CjxlService.BuildCjxlArguments(
                "-", "o.jxl", jbOn, default, null, null, 0, false, pnSink);
            Check(!jbPipe.Contains("--lossless_jpeg=1") && pnLog.Contains("input is not a JPEG file"),
                $"jbrd-③ 有效值开着但输入是管道 ⇒ **不冒充**重封装且点名（日志=[{pnLog.Trim()}] 命令=[{jbPipe}]）");
            var jbOff = O("jxl"); jbOff.JxlLosslessJpeg = false; jbOff.Quality = 75;
            var jbOffArgs = FfmpegGui.Services.CjxlService.BuildCjxlArguments(
                "in.jpg", "o.jxl", jbOff, default, null, null, 0, false, null);
            Check(jbOffArgs.Contains("--lossless_jpeg=0"),
                $"jbrd-④ **正控**：手动关掉 ⇒ 显式发 --lossless_jpeg=0（不是省略，cjxl 对 JPEG 默认是 1）（{jbOffArgs}）");
            var jbRoute = O("jxl"); jbRoute.ColorEngine = "engine";
            jbRoute.EncoderBackend = FfmpegGui.Services.EncoderBackend.Cjxl;
            jbRoute.JxlLosslessJpeg = true;
            var jbBr = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(jbRoute, "o.jxl", false);
            Check(jbBr?.Code == "EncoderUnsupportedSetting",
                $"jbrd-⑤ 有效值为真时引擎**必须拦**（引擎要先解码，结构上无法复制 DCT 系数）⇒ 实 Code={jbBr?.Code ?? "null"}");
            // 负控（与 ⑤ 只差一个布尔）：有效值为假 ⇒ jxl+Cjxl 照旧路由 —— 放宽/拦截都不得外溢。
            var jbRouteOff = O("jxl"); jbRouteOff.ColorEngine = "engine";
            jbRouteOff.EncoderBackend = FfmpegGui.Services.EncoderBackend.Cjxl;
            var jbBr2 = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(jbRouteOff, "o.jxl", false);
            Check(jbBr2 == null, $"jbrd-⑥ 负控：有效值 false ⇒ 仍走引擎出口（block={jbBr2?.Code ?? "无"}）");
            // ── jbrd 开关**不得改写在 ffmpeg-libjxl 后端上的质量轴**（2026-10-01 实测后补）──
            // 该开关 09-30 起默认启用，而 `FfmpegCommandBuilder` 里曾有一条
            // `if (JxlLosslessJpeg) ⇒ -distance 0` 的特例：ffmpeg 的 libjxl 做不到 jbrd（09-19 实测），
            // 这条特例只剩副作用 —— 实测 `-e Ffmpeg` 下 q=30/50/75/90 **产物全是 58.3 kB、命令行恒为
            // -distance 0**（关掉后恢复质量轴起效）。默认值一翻，这格从罕见变成常态。
            // ⚠ 2026-10-02：下方 qa-① 的期望值随 JXL distance 公式换成 libjxl 官方曲线而更新
            //   （旧 10.5 / 1.5 ⇒ 新 6.4 / 1.0；公式见 ImageEncoderArgs.MapJxlDistance）。
            // ⚠ 这里显式给 BitDepth，避免 BuildArguments 去探测不存在的输入而把读数搅浑。
            string JxlCmd(int q, bool lossless, bool? modular)
            {
                var o = O("jxl"); o.Quality = q; o.Lossless = lossless; o.BitDepth = 8;
                o.JxlLosslessJpeg = true; o.JxlModular = modular;
                return FfmpegGui.Services.FfmpegCommandBuilder.BuildArguments(o, "in.jpg", "out.jxl");
            }
            var qa30 = JxlCmd(30, false, null); var qa90 = JxlCmd(90, false, null);
            Check(qa30.Contains("-distance 6.4") && qa90.Contains("-distance 1.0")
                    && !qa30.Contains("-distance 0") && !qa90.Contains("-distance 0"),
                $"qa-① 开关开着也不吞质量轴：q30⇒6.4 / q90⇒1.0（实 [{qa30}] / [{qa90}]）");
            // ⚠ P1-3（2026-10-03 复查）：`MapJxlDistanceInverse` 只是**近似**互逆（正函数 1 位小数取整），
            //   旧注释自称"严格互逆"（自相矛盾），且旧锚点 q30/q90 恰落在互逆点上 ⇒ 结构性抓不到差异。
            //   此处**显式钉死往返读数**并把锚点选在**非互逆点 q75**（→2.4→74，差 1）以恢复判别力：
            //   q75 的 74 是量化的**诚实结果**（不是缺陷）—— 若日后有人"修"成严格互逆，这条会红，逼一次有意识决策。
            int rt75 = FfmpegGui.Models.FfmpegOptions.MapJxlDistanceInverse(
                FfmpegGui.Models.FfmpegOptions.MapJxlDistanceForward(75));
            int rt30 = FfmpegGui.Models.FfmpegOptions.MapJxlDistanceInverse(
                FfmpegGui.Models.FfmpegOptions.MapJxlDistanceForward(30));
            Check(rt75 == 74 && rt30 == 30,
                $"jxl-rt 反函数往返（近似互逆）：非互逆点 q75 ⇒ {rt75}（期望 74，差 1 属量化）；互逆点 q30 ⇒ {rt30}（期望 30）");
            var qaL = JxlCmd(75, true, null);
            Check(qaL.Contains("-distance 0"), $"qa-② 正控：真要无损由 Lossless 决定（{qaL}）");
            var qaM = JxlCmd(75, false, true);
            Check(qaM.Contains("-modular 1"), $"qa-③ 特例曾漏发的 -modular 现在跟着单一真值走（{qaM}）");
            // **2026-09-17 单一真值锁**：白名单此前在 `ColorEngineRouter` 与 `ImageEncoderArgs` 各写一份
            // 字面量（后者**全仓无调用者**，只在 Router 文档里被引用）⇒ 已合并为
            // `ImageEncoderArgs.IsEngineEncodable` **一份**，Router 改为调用它。
            // 本断言把合并后的那一份**逐字钉死**：今后新增/移除出口格式必须同步改探针（防再次漂移）。
            string[] encWhitelist = { "png", "apng", "tiff", "tif", "webp", "jpg", "jpeg", "avif", "gif", "jxl", "jxr" };
            string[] encOutside = { "dng", "ppm", "bmp", "heic", "heif", "qoi" };
            var encMissing = new StringBuilder(); var encExtra = new StringBuilder();
            foreach (var f in encWhitelist)
                if (!FfmpegGui.Services.ColorMapping.ImageEncoderArgs.IsEngineEncodable(f)) encMissing.Append(f).Append(' ');
            foreach (var f in encOutside)
                if (FfmpegGui.Services.ColorMapping.ImageEncoderArgs.IsEngineEncodable(f)) encExtra.Append(f).Append(' ');
            Check(encMissing.Length == 0 && encExtra.Length == 0,
                $"引擎出口白名单必须恰为 {string.Join("/", encWhitelist)}（缺=[{encMissing}] 多=[{encExtra}]）");
            // ── JXL 的 ICC 通路：**引擎出口只能写 CICP**（2026-09-17 实测，本次接入的设计约束）──
            // 实测事实链（两条独立路径都不通 ⇒ 不是"命令写错"）：
            //   ① **编码器丢弃**：`-vf iccgen` 生成的 ICC 不落盘 —— legacy 参照 ref.jxl 与引擎出口产物，
            //      exiftool 都读不出 ICC（方法自检：读 cjxl 产的 cjxl_lossless.jxl 同样读不出）；
            //   ② **后置被拒**：`exiftool "-icc_profile<=p3.icc" -overwrite_original w.jxl`
            //      ⇒ 退出码 **1**、`Error: [minor] Will wrap JXL codestream in ISO BMFF container for writing`、
            //      `0 image files updated`（**同一命令写 PNG 成功 584B**）⇒ 是 JXL 容器限制。
            // ⇒ `RawColorPipeline` 对「jxl + 计划要求附 ICC」**显式拒绝**（宁可硬失败，
            //    也不产出一个无任何色彩描述的 JXL —— 那正是历史偏色根因）。
            // 下面两条把这道门**为何不空转**钉住；第 3 条界定它是不是主路径（结论：不是）。
            var jxlCaps = CE.CapsFor("jxl");
            var proP = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor.FromNamedSpace("ProPhoto RGB");
            var p3d = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor.FromCicp("smpte432", "iec61966-2-1", "Display P3");
            Check(proP != null && p3d != null, "构造 ProPhoto / Display P3 描述符（JXL ICC 通路用例前置）");
            if (proP != null && p3d != null)
            {
                // 第三个参数 = **外部编码器出口**（#38）：这条断言讲的正是 cjxl 的 `-x color_space=`
                // 枚举宽度（ProPhoto 无处可标 ⇒ 只能走 ICC），故必须显式给 true；
                // 同一目标走 ffmpeg 的 libjxl 出口时该枚举**不适用**（`CapsAcceptsCicp(…, false)` 为真）。
                Check(!CE.CapsAcceptsCicp(jxlCaps, proP, true),
                    "jxl 的 CICP 枚举（sRGB/DisplayP3/Rec2100PQ/Rec2100HLG）表达不了 ProPhoto ⇒ 该目标只能走 ICC");
                var polJxl = new FfmpegGui.Services.ColorMapping.PlanPolicy
                { Format = jxlCaps, AllowApproximateCurve = false, ForceMapping = true, TargetExplicit = true };
                var pJxlIcc = CE.Plan(p3d, proP, polJxl);
                Check(pJxlIcc.AttachIcc && pJxlIcc.Action == CA.Map,
                    $"规划层对「jxl + 不可命名目标」确实要求附 ICC（act={pJxlIcc.Action} attachIcc={pJxlIcc.AttachIcc}）"
                    + " ⇒ 出口那道拒绝门不是恒 false 的空断言");
                // 第 3 条（**界定**）：CLI **到不了**上面那个组合 —— 目标描述符由
                // `EffectiveOutputColorTokens` 的 CICP 三元组构造（`ColorSpaceDescriptor.FromCicp`），
                // 装配层还会按容器能力钳制，故 CLI 得到的目标**恒为可 CICP 命名**的
                // ⇒ 规划层的「择一」规则（`EnforceContainerAnnotationRules`，jxl 的
                // `CanHaveBothIccAndCicp=false`）必然剥掉 AttachIcc。下面这条钉的就是这个不变量：
                // 「CLI 装配出的 jxl 计划**永不**同时要求『附 ICC + 改像素』」。
                // 门若变成主路径（该不变量被破坏）⇒ 本条转红，正是我们要的告警。
                var badJxl = O("jxl"); badJxl.ColorEngine = "engine";
                badJxl.UseAdvancedColorParameters = true; badJxl.ColorPrimaries = "smpte431"; badJxl.ColorTrc = "gamma22";
                var badJxlIt = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(badJxl, sdr, out var whyBadJxl);
                var badJxlPlan = badJxlIt == null ? null : CE.Plan(badJxlIt);
                Check(badJxlIt == null || !(badJxlPlan!.AttachIcc && badJxlPlan.Action == CA.Map),
                    "界定：CLI 装配出的 jxl 计划**永不**同时要求「附 ICC + 改像素」"
                    + $"（target={badJxlIt?.Target?.Name ?? ("装配失败: " + whyBadJxl)} "
                    + $"act={badJxlPlan?.Action} attachIcc={badJxlPlan?.AttachIcc}）"
                    + " ⇒ 出口那道门是兜底而非主路径");
                // 第 4 条：**执行体那道门本身**也要有牙（删掉它 ⇒ 本条转红）。
                // 刻意把门放在 `ProbeSizeAsync` **之前** ⇒ 这里可以用不存在的输入路径 + 任意存在的
                // ffmpeg 占位（`ffmpegOverride`），**不需要真素材、不启动任何子进程**（纯拒绝路径）。
                bool jxlIccThrew = false; string? jxlIccMsg = null;
                try
                {
                    FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                        "tests/output/_no_such_input.png", "tests/output/_no_such_output.jxl",
                        pJxlIcc.ToSpec(), pJxlIcc, null, default, 1, Environment.ProcessPath)
                        .GetAwaiter().GetResult();
                }
                catch (InvalidOperationException ex) { jxlIccThrew = true; jxlIccMsg = ex.Message; }
                Check(jxlIccThrew && (jxlIccMsg ?? "").Contains("JXL 出口无法附 ICC"),
                    $"执行体的 JXL ICC 拒绝门确实生效（threw={jxlIccThrew} msg={jxlIccMsg}）");
                // 第 5 条（**分叉**，2026-09-17 cjxl 出口轮）：同一份「jxl + AttachIcc」计划，置上
                // `ExternalEncoderExit` 之后**不得**再被那道门拦住 —— 因为 cjxl 出口**真的能写 ICC**
                //（`-x icc_pathname=`，实测产物 jxlinfo 读回 P3 primaries）。
                // 判据：抛出的异常**不再是** ICC 门那一条（此处会因输入不存在而在更后面失败）
                // ⇒ 说明它**越过了**门。⚠ 把分叉改回"按扩展名判"（删掉 `&& !plan.ExternalEncoderExit`）⇒ 本条转红。
                var pForkPlan = CE.Plan(p3d, proP, polJxl);
                pForkPlan.ExternalEncoderExit = true;
                bool forkThrew = false; string? forkMsg = null;
                try
                {
                    FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                        "tests/output/_no_such_input.png", "tests/output/_no_such_output.jxl",
                        pForkPlan.ToSpec(), pForkPlan, null, default, 1, Environment.ProcessPath)
                        .GetAwaiter().GetResult();
                }
                catch (InvalidOperationException ex) { forkThrew = true; forkMsg = ex.Message; }
                Check(!(forkThrew && (forkMsg ?? "").Contains("JXL 出口无法附 ICC")),
                    "ICC 拒绝门**按出口分叉**：置 ExternalEncoderExit 后不再按扩展名拦"
                    + $"（threw={forkThrew} msg={forkMsg}）");
            }
            // 第 6 条（2026-09-17 cjxl 出口轮**暴露并修复的真实缺陷**的回归锁）：
            // `CarryIcc` / `None` 两个 Action 的语义是"像素零改动"，但这类计划**不填**
            // `SrcCurve`/`DstCurve`/`Matrix`（`PlanCarry` 是提前 return 的分支）⇒ 会落进
            // `ToSpec()` 里 `?? TransferCurve.Linear` / `?? TransferCurve.Srgb` 两个**猜测式默认**
            // ⇒ 得到"按线性解码、按 sRGB 编码"的 spec ⇒ **整幅提亮**。
            // 实测（512x384 P3 源，jxl+Cjxl）：变换输出 == lin2srgb(解码输出)，平均绝对差 0.0000，
            // 均值 0.600 → 0.713。修复 = `ToSpec()` 按 Action 短路成恒等。
            // ⚠ 用**参考算子**直接钉"恒等"，不依赖任何素材/子进程；把短路删掉 ⇒ 0.6 会变成 0.798 ⇒ 转红。
            foreach (var noChange in new[] { CA.CarryIcc, CA.None })
            {
                var pNo = new FfmpegGui.Services.ColorMapping.ColorTransformPlan { Action = noChange };
                var spNo = pNo.ToSpec();
                var rt = FfmpegGui.Services.ColorMapping.ColorKernels.TransformPixelReference(0.6f, 0.6f, 0.6f, spNo);
                Check(spNo.Matrix == null && spNo.SrcCurve.Name == spNo.DstCurve.Name
                      && Math.Abs(rt.R - 0.6) < 1e-6 && Math.Abs(rt.G - 0.6) < 1e-6 && Math.Abs(rt.B - 0.6) < 1e-6,
                    $"{noChange} 计划的 ToSpec 必须是**恒等**（曲线同名={spNo.SrcCurve.Name}、矩阵空、0.6→{rt.R:F4}）"
                    + " —— 否则 cjxl 出口会把「像素零改动」的产物整幅提亮");
            }
            var bAnim = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(eng, "o.png", true);
            Check(bAnim?.Code == "AnimatedInput",
                $"动画输入 ⇒ 不路由（引擎是单帧）且 Code=AnimatedInput（{bAnim?.Text}）");
            // 诚实门（**2026-09-16 契约更新**）：引擎编码出口**已承接**质量/结构参数
            //（见 ImageEncoderArgs），故不再拒绝；但"承接了"必须真的落到编码器参数上，
            // 否则就是一句空话 —— 下面第二条断言即为此处的"牙"。
            var qOk = O("png"); qOk.ColorEngine = "engine"; qOk.Quality = 60;
            Check(FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(qOk, "o.png", false) == null,
                "质量参数已由引擎编码出口承接 ⇒ 不再拒绝");
            var qArgs = FfmpegGui.Services.ColorMapping.ImageEncoderArgs.BuildEncoderOptions("png", qOk);
            int expectCl = FfmpegGui.Services.ColorMapping.ImageEncoderArgs.MapPngCompression(60);
            Check(qArgs.Contains($"-compression_level {expectCl}"),
                $"质量确实落到编码器参数（q=60 ⇒ -compression_level {expectCl}，实得「{qArgs.Trim()}」）");
            // **2026-09-16 契约更新**：最长边几何缩放**已接入引擎**（解码步复用同一个
            // `BuildMaxDimensionScaleFilter`）⇒ 不再拒绝。此处改为"不再拒绝"的正向断言；
            // "真的缩到限值"由 `verify-color-wiring.ps1` 第 (7) 节实测宽高守护（探针是纯函数层，看不到像素）。
            var geo = O("png"); geo.ColorEngine = "engine"; geo.EnableMaxDimension = true;
            Check(FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(geo, "o.png", false) == null,
                "最长边几何缩放已承接 ⇒ 不再拒绝");
            var old = O("png"); old.ColorEngine = "engine"; old.TonemapCurve = "reinhard";
            var bo = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(old, "o.png", false);
            Check(bo?.Code == "LegacyTonemapCurve",
                $"旧 --tonemap-curve 未接入 ⇒ 拒绝并提示改用 --color-tone-map（Code={bo?.Code}）");
            var chroma = O("jpg"); chroma.ColorEngine = "engine"; chroma.Chroma = "4:2:0";
            Check(FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(chroma, "o.jpg", false) == null,
                "jpg 的色度子采样已承接 ⇒ 不再拒绝");
            Check(FfmpegGui.Services.ColorMapping.ImageEncoderArgs.MapEnginePixFmt("jpg", chroma)?.Contains("yuvj420p") == true,
                "色度确实落到 -pix_fmt（jpg 4:2:0 ⇒ yuvj420p）");
            // 负控（**不得为了绿而放宽**）：libwebp 只接受 yuv420p/yuva420p/bgra ⇒ 4:4:4 必须仍然拒绝
            var chBad = O("webp"); chBad.ColorEngine = "engine"; chBad.Chroma = "4:4:4";
            var bchBad = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(chBad, "o.webp", false);
            Check(bchBad?.Code == "EncoderUnsupportedSetting",
                $"负控：libwebp 无 4:4:4 ⇒ 仍拒绝且 Code=EncoderUnsupportedSetting（{bchBad?.Text}）");
            // ── GainMap 接入色彩引擎（**2026-09-16 契约更新**）──────────────────────────
            // 旧契约：`--jpeg-gain-map true` 被路由层整条拦下（此处断言 BlockReason 含 "GainMap"）。
            // 新契约：**JPEG 输出 + GainMap 由色彩引擎决策并执行** ⇒ 路由层不再拦截；
            // 非 JPEG 的拒绝点下移到**规划层**（`ColorStrategyPlanning` 的
            // `GainMapRequested && !Format.SupportsGainMap ⇒ Reject`）⇒ 负控必须改看 Plan。
            var gm = O("jpg"); gm.ColorEngine = "engine"; gm.JpegGainMap = true;
            Check(FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(gm, "o.jpg", false) == null,
                "jpg + JpegGainMap ⇒ 路由层不再拦截（GainMap 已接入引擎）");
            Check(FfmpegGui.Services.ColorMapping.ColorEngineRouter.ShouldRoute(gm, "o.jpg", false),
                "jpg + JpegGainMap ⇒ ShouldRoute 为真（确实交给引擎，而非仅“不报错”）");
            // 正向（走**真实装配**，与 CLI 同源）：HDR 源 + jpg + GainMap ⇒ 规划层不得 Reject。
            // 用 HDR 源是刻意的：GainMap 的主用途正是「HDR 底图 → SDR 底图 + 增益图」。
            var gmIt = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(gm, pq, out var whyGm);
            Check(gmIt != null && gmIt.GainMapRequested,
                $"jpg + JpegGainMap 装配意图并携带 GainMapRequested（reason={whyGm ?? "无"}）");
            var gmPlan = gmIt == null ? null : CE.Plan(gmIt);
            Check(gmPlan != null && gmPlan.Action != CA.Reject,
                $"jpg + JpegGainMap ⇒ 规划层不得 Reject（实为 {gmPlan?.Action}）");
            // 负控（不得为了绿而放宽）：非 JPEG 格式 + GainMap 仍必须被拒 + 给建议，
            // 且拒绝点是**规划层**而不是路由层（路由层现在放行一切 GainMap）。
            var gmPng = O("png"); gmPng.ColorEngine = "engine"; gmPng.JpegGainMap = true;
            var gmPngIt = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(gmPng, pq, out var whyGmP);
            Check(gmPngIt != null, $"png + JpegGainMap 仍可装配意图（reason={whyGmP ?? "无"}）");
            var gmPngPlan = gmPngIt == null ? null : CE.Plan(gmPngIt);
            Check(gmPngPlan != null && gmPngPlan.Action == CA.Reject && (gmPngPlan.Advice ?? "").Contains("AVIF"),
                $"负控：png + JpegGainMap ⇒ 规划层 Reject 并建议改 AVIF/JXL 或目标改 JPEG（{gmPngPlan?.Reason}）");
            // ── GainMap × 最长边几何缩放：**2026-09-18 起不再拦**（Part C 删除了那段死拦截）─────────
            // 【旧契约（已废）】`BlockReason.Code == "GeometryWithGainMap"`，负控「关掉最长边缩放 ⇒ 不拦」。
            // 【为什么废】那段拦截的前置条件在产品里**恒不成立**：`QueueProcessor.cs:421-470` 的预缩放中继
            //   （**所有后端共用**、在格式分发之前）已把超限输入换成缩过的临时件 ⇒ 引擎侧
            //   `BuildMaxDimensionScaleFilter` 恒返回 null ⇒ 判据恒假。实测删前/删后读数**完全相同**
            //   （exit 0、4621B、`images=2`/`iso min=0 max=2.3`/`mpf[1] soi=True`/探针 6/0）。
            // 【新契约】几何**不再**构成阻塞 ⇒ `BlockReason == null` **且** `ShouldRoute == true`。
            //   两条都断言：只断言"不报错"会让「路由说能走、执行体却不接」的形态溜过去。
            var gmGeo = O("jpg"); gmGeo.ColorEngine = "engine"; gmGeo.JpegGainMap = true;
            gmGeo.EnableMaxDimension = true; gmGeo.MaxDimension = 1280;
            var bgmGeo = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(gmGeo, "o.jpg", false);
            Check(bgmGeo == null,
                $"GainMap × 最长边缩放 ⇒ 路由层**不再**拦截（实得 Code={bgmGeo?.Code ?? "null"}；2026-09-18 Part C 删死拦截）");
            Check(FfmpegGui.Services.ColorMapping.ColorEngineRouter.ShouldRoute(gmGeo, "o.jpg", false),
                "GainMap × 最长边缩放 ⇒ ShouldRoute 为真（几何不再把该组合踢出引擎）");
            // 负控必须**仍有牙**：证明"几何不再拦"是**收窄**，不是"GainMap 一律放行"。
            // 只改**一个变量**（inputIsAnimated: false → true），其余与正例逐字相同 ——
            // 这样「把它改回 false」就必然转红，变异验证有一个干净的对照。
            // 为什么选动画这道门：删掉几何拦截后，`AnimatedInput` 是**唯一**还能把 GainMap 踢出引擎的
            //   路由级判据（实测：`anim_gif.gif` + `--jpeg-gain-map true` ⇒ 走专用管线）。
            //   ⇒ 这条负控正好钉住"剩下的那道门还在"。
            var gmGeoAnim = O("jpg"); gmGeoAnim.ColorEngine = "engine"; gmGeoAnim.JpegGainMap = true;
            gmGeoAnim.EnableMaxDimension = true; gmGeoAnim.MaxDimension = 1280;
            var bgmGeoAnim = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(gmGeoAnim, "o.jpg", true);
            Check(bgmGeoAnim?.Code == "AnimatedInput",
                $"负控：同一输入换成动画 ⇒ 仍须拦（Code={bgmGeoAnim?.Code}；证明放行的只是几何这一道门）");
            var ext = O("png"); ext.ColorEngine = "engine"; ext.EncoderBackend = FfmpegGui.Services.EncoderBackend.Cjxl;
            var bExt = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(ext, "o.png", false);
            Check(bExt?.Code == "ExternalBackend",
                $"用户选了外部编码器 ⇒ 引擎不抢决断权（Code={bExt?.Code}）");

            // ══════════════════════════════════════════════════════════════════════════════════
            // 6) **S2：路由拦截的结构化**（规划 §5-D；`BlockReason` 由 `string?` 改为 `EngineBlock?`）
            // ══════════════════════════════════════════════════════════════════════════════════
            // 要钉住的契约：
            //   ① **每个 Code 至少一条正向断言**（构造对应场景 ⇒ 断言 `Block.Code`，**绝不匹配 Text**）；
            //   ② 引擎可走时 `Block` 必须为 **null**（负控，代表格式）；
            //   ③ 语义**不得**回退成"任务做不了"：`BlockReason` 返回的是 `EngineBlock`，**不是** `ColorVerdict`
            //      —— 这条由**类型系统**保证（编译期），此处只需守住 Code 词表。
            //   ④ Code 词表**逐条对应** `BlockReason`/`UnconsumedOutputSettings` 的现有判据（不多不少）。

            // 6a) `OptionsNull`：防御性分支（实际不可达，但既然存在就必须有 Code，否则"逐条对应"有缺口）。
            var bNull = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(null, "o.png", false);
            Check(bNull?.Code == "OptionsNull", $"options 为空 ⇒ Code=OptionsNull（{bNull?.Text}）");

            // 6b) `AnimationScaleUnsupported`：动画缩放（原分支：UnconsumedOutputSettings 的 bad.Add("动画缩放")）。
            var animScale = O("png"); animScale.ColorEngine = "engine"; animScale.AnimationScaleW = 1920;
            var bAnimScale = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(animScale, "o.png", false);
            Check(bAnimScale?.Code == "AnimationScaleUnsupported",
                $"动画缩放未承接 ⇒ Code=AnimationScaleUnsupported（{bAnimScale?.Text}）");

            // 6c) `ProgressiveJpeg`：ffmpeg 的 mjpeg 无渐进能力（原分支：JpegProgressiveId != 0）。
            var prog = O("jpg"); prog.ColorEngine = "engine"; prog.JpegProgressiveId = 1;
            var bProg = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(prog, "o.jpg", false);
            Check(bProg?.Code == "ProgressiveJpeg",
                $"JPEG 渐进未承接 ⇒ Code=ProgressiveJpeg（{bProg?.Text}）");

            // 6d) 多项同时命中 ⇒ Code 取**列表首项**（= 文案里第一个原因），顺序即判据的固定顺序。
            //     ⚠ 这是**确定性契约**（GUI 要按 Code 分派解释），不是"随便取一个"。
            var multi = O("jpg"); multi.ColorEngine = "engine";
            multi.AnimationScaleW = 1920; multi.TonemapCurve = "reinhard"; multi.JpegProgressiveId = 1;
            var bMulti = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(multi, "o.jpg", false);
            Check(bMulti?.Code == "AnimationScaleUnsupported",
                $"多因同时命中 ⇒ Code 取首项 AnimationScaleUnsupported（Code={bMulti?.Code}）");
            // 文案锁（**不是**行为判据）：多因场景的 Text 必须与改动前的拼串**逐字相同**
            // （「、」分隔、顺序一致）。这里用**严格相等**而不是 Contains —— 后者才是"匹配自由文本"。
            Check(bMulti?.Text == "引擎编码出口尚未承接这些输出参数：动画缩放、"
                  + "旧 --tonemap-curve=reinhard（请改用 --color-tone-map）、"
                  + "JPEG 渐进（ffmpeg 的 mjpeg 编码器无此能力；请改用 cjpegli 后端，或把该项设回自动）",
                $"多因场景的 Text 与改动前逐字相同（实得「{bMulti?.Text}」）");
            // 6d-2) ⚠ **`Alternatives` 必须跨所有命中的判据聚合**（不是只给首项那条的替代）。
            //   这是"`Code` 保持单值"的**前提**：team-lead 裁定 ① 的理由正是"GUI 一键修正要的是
            //   **全部可行动替代**，而 `Alternatives` 是列表"。只给首项 ⇒ 那个理由就不成立。
            //   本用例同时命中 3 条判据 ⇒ 必须有 3 条替代，且顺序与判据顺序一致。
            var mAlts = bMulti?.Alternatives
                        ?? System.Array.Empty<FfmpegGui.Services.ColorMapping.ColorAlternative>();
            var mVals = new StringBuilder();
            for (int i = 0; i < mAlts.Count; i++) mVals.Append(mAlts[i].Value).Append(' ');
            Check(mVals.ToString() == "animation-scale-w color-tone-map jpeg-progressive ",
                $"多因命中的 Alternatives 必须**跨全部判据聚合**（实得「{mVals}」）");

            // 6e) `Alternatives`：**只在代码本来就知道替代时填**。填了的必须有，没填的必须为空
            //     —— 两个方向都断言，否则"哪天有人随手补一个编造的替代"无人发现。
            // ⚠ 取值用**空安全**投影（`Block` 为 null 时给 `(0, default, "")`）：变异让某条判据恒假时
            //   本段必须**转红**而不是抛 NullReference（抛异常会把"哪条断言失败"掩盖掉）。
            (int n, FfmpegGui.Services.ColorMapping.AlternativeKind kind, string value) Alt1(
                FfmpegGui.Services.ColorMapping.EngineBlock? b)
                => b != null && b.Alternatives.Count > 0
                   ? (b.Alternatives.Count, b.Alternatives[0].Kind, b.Alternatives[0].Value)
                   : (0, default, "");
            var ALT = FfmpegGui.Services.ColorMapping.AlternativeKind.Option;
            Check(Alt1(bExt) == (1, ALT, "encoder"),
                $"ExternalBackend 给出替代「改 -e Ffmpeg」（实得 {Alt1(bExt)}）");
            // ⚠ 2026-09-18：`GeometryWithGainMap` **已不再被生产**（Part C 删掉那段死拦截）
            //   ⇒ 该组合不再有 `Block`，也就无从谈"替代项"。这里保留一条**反向**断言：
            //   若哪天有人把拦截加回去，`Alt1(bgmGeo)` 会变成 `(1, Option, max-dimension-enabled)`
            //   ⇒ 本条转红（等于给 Part C 的删除上了一把锁）。
            Check(Alt1(bgmGeo).n == 0,
                $"GeometryWithGainMap 已不再生产 ⇒ 该组合无替代项（实得 {Alt1(bgmGeo)}）");
            Check(Alt1(bo) == (1, ALT, "color-tone-map"),
                $"LegacyTonemapCurve 给出替代「改用 --color-tone-map」（实得 {Alt1(bo)}）");
            Check(Alt1(bProg) == (1, ALT, "jpeg-progressive"),
                $"ProgressiveJpeg 给出替代「把 --jpeg-progressive 设回自动」（实得 {Alt1(bProg)}）");
            Check(Alt1(bAnimScale) == (1, ALT, "animation-scale-w"),
                $"AnimationScaleUnsupported 给出替代「把 --animation-scale-w 设回 0」（实得 {Alt1(bAnimScale)}）");
            // 反向：**不该编替代**的三处必须留空（各自的理由见 Router 内的注释）。
            Check(Alt1(bppm).n == 0 && Alt1(bAnim).n == 0 && Alt1(bchBad).n == 0 && Alt1(bNull).n == 0,
                "FormatNotSupported / AnimatedInput / EncoderUnsupportedSetting / OptionsNull "
                + $"⇒ Alternatives 必须为空（实得 {Alt1(bppm).n}/{Alt1(bAnim).n}/{Alt1(bchBad).n}/{Alt1(bNull).n}）");

            // 6f) **负控**：引擎可走的代表格式 ⇒ `Block` 必须为 **null**（漏一个格式就是"假路由"）。
            var negBad = new StringBuilder();
            foreach (var nf in new[] { "png", "webp", "tif", "gif", "jpg", "jxl", "avif" })
            {
                var okF = O(nf); okF.ColorEngine = "engine";
                var bF = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(okF, "o." + nf, false);
                if (bF != null) negBad.Append($"{nf}={bF.Code} ");
            }
            Check(negBad.Length == 0,
                $"负控：引擎可走的代表格式 Block 必须为 null（非空=[{negBad}]）");

            // 7) **P1-3 枚举序锁**（2026-10-04 补）────────────────────────────────────────
            // 病因：RAW 源描述符的置信度曾**恰好等于**闸门阈值（`CicpTag == MinConfidenceForMapping`），
            // 靠 `>=` 才放行 ⇒ 只要有人往枚举里插一个成员、或把闸门抬到 `NamedIcc`，这条刚修好的
            // RAW 路径就**零告警地退回「直通」**（像素一个都不动，却仍标目标域）。
            // 现修法：RAW 分支**显式**把置信度抬到 `NamedIcc`（见 `ColorIntentFactory` 的 `ColorSourceDecl*` 分支）。
            // 下面三条把「枚举序不可随意插成员」钉死，并锁住「显式抬高」这件事本身（不再依赖枚举巧合）。
            var minConf = FfmpegGui.Services.ColorMapping.EquivalenceTolerances.MinConfidenceForMapping;
            Check(minConf == FfmpegGui.Services.ColorMapping.DescriptorConfidence.CicpTag,
                $"P1-3 闸门阈值仍是 CicpTag（实 {minConf}）—— 若有意抬高，须同步确认 RAW 源描述符仍 >= 阈值");
            Check((int)FfmpegGui.Services.ColorMapping.DescriptorConfidence.CicpTag
                  < (int)FfmpegGui.Services.ColorMapping.DescriptorConfidence.NamedIcc,
                "P1-3 枚举序不变：CicpTag("
                + $"{(int)FfmpegGui.Services.ColorMapping.DescriptorConfidence.CicpTag}) < NamedIcc("
                + $"{(int)FfmpegGui.Services.ColorMapping.DescriptorConfidence.NamedIcc})");
            var rawDeclO = O("tiff");
            rawDeclO.ColorSourceDeclPrimaries = "bt2020";
            rawDeclO.ColorSourceDeclTrc = "linear";
            // ⚠⚠ 输入路径必须是 **RAW**（用真素材，避免探测抛错）：`ColorIntentFactory` 的「自产中间件」
            //   分支自 2026-10-04 审查起**只认 RAW 与 Ultra HDR 解码**两条路 —— 用 png 路径会让该分支
            //   不触发 ⇒ 下面这条 P1-3 锁**假红**（实测：收紧后 `probe:wire` exit=1）。
            //   ⇒ 这里改用 `tests/output/raw_samples/` 下的真 ARW（`RawService.IsRawFile` 是扩展名判据）。
            const string rawDeclPath = "tests/output/raw_samples/Sony_ILCE7S.ARW";
            var iRawDecl = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(rawDeclO, rawDeclPath, out var rRawDecl);
            Check(iRawDecl?.Source != null
                  && iRawDecl.Source!.Confidence == FfmpegGui.Services.ColorMapping.DescriptorConfidence.NamedIcc
                  && iRawDecl.Source.Confidence >= minConf,
                $"P1-3 RAW 源描述符置信度**显式** NamedIcc（实 {iRawDecl?.Source?.Confidence}，闸门 {minConf}）"
                + $"⇒ 不依赖「恰好等于」（reason={rRawDecl ?? "无"}）");

            // 8) **回归锁（2026-10-04）**：`ColorMatrix` 是**输入声明**、不是目标轴 ——
            //    设了它**不得**吃掉 `ColorSpace` 目标。
            //    曾踩：把「用户是否选了目标」的值派生判据写成含 `ColorMatrix` 的 `HasExplicitColorParams`，
            //    而下游判据是**互斥**形状（`!advanced && ColorSpace…`）⇒ `--color-matrix bt709
            //    --color-space sRGB` 会把 ColorSpace 目标**整条丢掉**。
            //    实测后果：HDR(PQ) 源 → png 变 `决策：None`、产物仍 `smpte2084/bt2020`（用户要的 sRGB 被静默丢弃）。
            //    ⇒ 锁住「矩阵在位 + 指定色空间 ⇒ 目标仍成立」。
            var matO = O("png", "sRGB");
            matO.ColorMatrix = "bt709";
            var iMat = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(matO, sdr, out var rMat);
            Check(iMat?.Target != null && iMat.TargetExplicit,
                $"ColorMatrix 不得吃掉 ColorSpace 目标（Target={(iMat?.Target == null ? "null" : iMat.Target.Name)}"
                + $"，TargetExplicit={iMat?.TargetExplicit}，reason={rMat ?? "无"}）");

            _ = R;
        }

        // ══════════════════════════════ decision ══════════════════════════════

        /// <summary>
        /// decision —— “同源宣称值”与 <c>BuildArguments</c> **实际发出的命令行**逐项对（取 -i 之后的输出侧标签）。
        /// 不一致就是“同一问题两处答案不同”的现行证据，不拿推理交差。
        /// 网格：{png,jpg,webp,tiff} × {auto,sRGB,Display P3,BT.2020,BT.2020 PQ} × {SDR,P3,HDR} 源。
        /// </summary>
        private static void ProbeDecision()
        {
            var srcs = new (string name, string path)[]
            {
                ("sdr", "tests/output/validate/sdr_plain.png"),
                ("p3",  "tests/output/results/color/a2_srgb_p3.png"),
                ("hdr", "tests/output/validate/src_hdr.png"),
            };
            string[] targets = { "auto", "sRGB", "Display P3", "BT.2020", "BT.2020 PQ" };
            int compared = 0, mismatch = 0, iccOnly = 0, implicitCount = 0, hdrClaim = 0, embedMismatch = 0;
            foreach (var (sn, sp) in srcs)
            {
                if (!File.Exists(sp)) { Console.WriteLine($"  SKIP 源缺失 {sp}"); continue; }
                foreach (var fmt in new[] { "png", "jpg", "webp", "tiff" })
                    foreach (var tg in targets)
                    {
                        var o = new FfmpegGui.Models.FfmpegOptions
                        {
                            Format = fmt, ColorSpace = tg, BitDepth = 8,
                            Quality = FfmpegGui.Models.FfmpegOptions.GetDefaultQuality(fmt),
                            ColorStrategy = ST.Recommended,
                        };
                        string outPath = $"out.{(fmt == "jpg" ? "jpg" : fmt == "tiff" ? "tif" : fmt)}";
                        string cmd;
                        try { cmd = FfmpegGui.Services.FfmpegCommandBuilder.BuildArguments(o, sp, outPath); }
                        catch (Exception ex) { Check(false, $"{sn}/{fmt}/{tg} BuildArguments 抛异常：{ex.Message}"); continue; }

                        // 只看**输出侧**：-i 之前的 -color_* 是“源声明”，不是输出决定
                        int iAt = cmd.IndexOf(" -i ", StringComparison.Ordinal);
                        string outSide = iAt >= 0 ? cmd[(iAt + 4)..] : cmd;
                        // ⚠ 不能用 `[regex]::Match` 写在表达式里：会被当成别名引用（CS7000），本文件没 using 该命名空间。
                        string Got(string key) =>
                            Last($"-{key} (\\S+)", outSide);
                        string gotP = Got("color_primaries"), gotT = Got("color_trc");
                        // legacy 的“输出决定”不只用 -color_* 表达，更多时候是 zscale 的 p=/t= + iccgen
                        // （首轮审计实测：几乎所有格子输出侧根本没有 -color_primaries/-color_trc）。
                        // 所以判定改成：“宣称值必须以后面三种形式之一在命令行落实”，否则才是真不一致：
                        //   ① 输出侧 -color_primaries/-color_trc   ② zscale 的 p=/t=   ③ 宣称值就是 ICC 推的（无目标声明）
                        // ⚠ 逐字字符串里的引号必须双写（""）；写 \" 会破坏字面量（CS1026）。
                        // ⚠ 必须取**最后一个** zscale：-vf 链常有“转线性→tonemap→回编码”多段，
                        //   取第一个会把中间量（如 t=bt709 的线性化步骤）当成输出决定 ⇒ 假差异。
                        string Last(string pattern, string where)
                        {
                            var ms = System.Text.RegularExpressions.Regex.Matches(where, pattern);
                            return ms.Count == 0 ? "" : ms[ms.Count - 1].Groups[1].Value;
                        }
                        var zs = Last(@"zscale=([^""]+)", outSide);
                        string zsP = System.Text.RegularExpressions.Regex.Match(zs, @"(?:^|:)p=([^:""]+)").Groups[1].Value;
                        string zsT = System.Text.RegularExpressions.Regex.Match(zs, @"(?:^|:)t=([^:""]+)").Groups[1].Value;
                        string Has(string v) => string.IsNullOrWhiteSpace(v) ? "—" : (v.Length > 13 ? v[..13] : v);
                        // 第四种合法形式：**直通**。源不变时命令行既无 zscale 也无输出侧 -color_*，
                        // 但“源声明”（-i 前）已把同一对 token 带进编码器 ⇒ 不算未落实。
                        string inSide = iAt >= 0 ? cmd[..iAt] : "";
                        string InGot(string key) =>
                            System.Text.RegularExpressions.Regex.Match(inSide, $"-{key} (\\S+)").Groups[1].Value;
                        bool passthrough = !outSide.Contains("zscale") && !outSide.Contains("tonemap");
                        string inP = InGot("color_primaries"), inT = InGot("color_trc");

                        var (claimP, claimT, _) = FfmpegGui.Services.FfmpegCommandBuilder
                            .EffectiveOutputColorTokens(o, sp);
                        // 拿到完整的裁决点（不只那三个令牌）：tonemap 与 ICC 落盘两项也要对账
                        var dec = FfmpegGui.Services.FfmpegCommandBuilder.DecideOutputColor(o, sp);
                        compared++;
                        // 第五种合法形式：**隐含 sRGB**。PNG/WebP/JPEG 无标签即按 sRGB 解读。
                        // ⚠ 必须同时要求**源本身也是 sRGB/无标签**！否则会把“像素是 P3、却声称 bt709”
                        //   这种真缺陷一并赦免（上一版就犯了：p3/webp/auto 被当成隐式合法）。
                        bool srcSrgb = (inP.Length == 0 || inP == "bt709") && (inT.Length == 0 || inT == "iec61966-2-1");
                        bool implicitSrgb = gotP.Length == 0 && gotT.Length == 0 && passthrough && srcSrgb
                            && (claimP == null || claimP == "bt709")
                            && (claimT == null || claimT == "iec61966-2-1");
                        if (implicitSrgb) implicitCount++;
                        bool pLanded = claimP == null || string.Equals(claimP, gotP, StringComparison.OrdinalIgnoreCase)
                                                   || string.Equals(claimP, zsP, StringComparison.OrdinalIgnoreCase)
                                                   || (passthrough && string.Equals(claimP, inP, StringComparison.OrdinalIgnoreCase))
                                                   || implicitSrgb;
                        bool tLanded = claimT == null || string.Equals(claimT, gotT, StringComparison.OrdinalIgnoreCase)
                                                   || string.Equals(claimT, zsT, StringComparison.OrdinalIgnoreCase)
                                                   || (passthrough && string.Equals(claimT, inT, StringComparison.OrdinalIgnoreCase))
                                                   || implicitSrgb;
                        if (!pLanded || !tLanded)
                        {
                            mismatch++;
                            // 自带命令行摘录：不一致的行必须能直接看出哪一段与宣称对不上，
                            // 不留给下轮去猜（本仓库多次因“只看汇总行”错过真错）。
                            string excerpt = outSide.Length > 170 ? outSide[..170] + "…" : outSide;
                            Console.WriteLine($"  ≠ {sn}/{fmt}/{tg} 宣称={(claimP ?? "—")}/{(claimT ?? "—")}  "
                                + $"输出侧={Has(gotP)}/{Has(gotT)}  zscale={Has(zsP)}/{Has(zsT)}  输入侧={Has(inP)}/{Has(inT)} 直通={passthrough} 源sRGB={srcSrgb}");
                            Console.WriteLine($"      输出侧命令行：{excerpt}");
                        }
                        // ── 第二项对账：跑了 tonemap 就不得再宣称 HDR 传递（本探针要钉住的原缺陷）──
                        // 判据取自**命令行**而非 dec，否则成了“拿自己验自己”（假绿）。
                        // ⚠ 只看 trc：BT.2020 原色 + SDR 曲线（用户显式要 SDR-2020）是合法组合，不能当违反。
                        bool cmdTonemapped = outSide.Contains("tonemap");
                        if (cmdTonemapped && (claimT == "smpte2084" || claimT == "arib-std-b67"))
                        {
                            hdrClaim++;
                            Console.WriteLine($"  ≠ HDR已映射 {sn}/{fmt}/{tg}：命令行跑了 tonemap，宣称却仍是 {(claimP ?? "—")}/{(claimT ?? "—")}");
                        }
                        // ── 第三项对账：iccgen 的 ICC 能不能真的落盘（探针自带实测表，不读生产代码的表）──
                        // 实测（ffmpeg-full 2026.9.2 + exiftool 13.59）：同一 P3 源同一 iccgen 链，
                        //   png/jpg/tiff 产物内都能读到 P3 ICC；**webp 产物 RIFF 内根本没有 ICCP 块**。
                        // ⇒ 出口语义不是 sRGB 而编码器又会丢 ICC 时，dec 必须说出“要后置嵌入”；其余容器不得多嵌。
                        bool probeSaysEncoderDropsIcc = fmt == "webp";
                        bool outIsNotSrgb = (claimP != null && claimP != "bt709") || (claimT != null && claimT != "iec61966-2-1");
                        bool wantEmbed = probeSaysEncoderDropsIcc && outIsNotSrgb;
                        if (dec.NeedsPostEmbedIcc != wantEmbed)
                        {
                            embedMismatch++;
                            Console.WriteLine($"  ≠ ICC后置 {sn}/{fmt}/{tg}：出口语义 {(claimP ?? "—")}/{(claimT ?? "—")} "
                                + $"编码器落盘={!probeSaysEncoderDropsIcc} ⇒ 应后置嵌入={wantEmbed}，裁决点给 {dec.NeedsPostEmbedIcc}｜{dec.Short()}");
                        }
                        if (!outSide.Contains("iccgen")) iccOnly++;
                    }
            }
            Console.WriteLine($"  合计对比 {compared} 格：宣称未落实 {mismatch} 格；跑了 tonemap 仍宣称 HDR {hdrClaim} 格；"
                + $"ICC 后置判据不符 {embedMismatch} 格；命令行未跑 iccgen {iccOnly} 格；按隐含 sRGB 合法放过 {implicitCount} 格");
            Check(compared >= 40, $"网格覆盖足够（{compared} 格）");
            Check(mismatch == 0, $"同源宣称值全部在命令行得到落实（未落实 {mismatch}）");
            Check(hdrClaim == 0, $"像素已被 tonemap 成 SDR 时，宣称不得再是 HDR 语义（违反 {hdrClaim}）");
            Check(embedMismatch == 0, $"ICC 落盘能力与“要不要后置嵌入”一致（违反 {embedMismatch}）");
        }

        // ══════════════════════════════ settings ══════════════════════════════

        /// <summary>
        /// S-P1-1 / S-P0-2 的三条不变式（用 --settings 独立文件驱动真实的 AppSettingsService）：
        /// ① 写不进去时 Save() 必须返回 false 并说清原因（旧写法 <c>catch {}</c> 吞掉 ⇒ 用户以为已保存）；
        /// ② 原子写不留 <c>.tmp</c>，且第二次保存要把上一版留在 <c>.bak</c>（旧写法直写，打断即毁唯一一份）；
        /// ③ 设置文件损坏时 Load 必须留下 <c>LastLoadIssue</c>（旧写法静默重置为默认值）。
        /// </summary>
        private static void ProbeSettingsSave()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cfgprobe_{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                var f = System.IO.Path.Combine(dir, "settings.json");
                System.IO.File.WriteAllText(f, "{}");
                AAS.Load(f);

                AAS.Current.Language = "en-US";
                AAS.Current.MaxQueueSize = 7;
                bool ok1 = AAS.Save();
                Check(ok1 && AAS.LastSaveIssue == null,
                    $"第一次保存成功且无告警（ok={ok1}, issue={AAS.LastSaveIssue}）");
                Check(!System.IO.File.Exists(f + ".tmp"), "原子写不留 .tmp 残片");
                var t1 = System.IO.File.ReadAllText(f);
                Check(t1.Contains("en-US") && t1.Contains("7"), "落盘内容确实是本次内存值（语言 + 队列并发）");

                AAS.Current.Language = "zh-CN";
                bool ok2 = AAS.Save();
                Check(ok2 && System.IO.File.Exists(f + ".bak")
                        && System.IO.File.ReadAllText(f + ".bak").Contains("en-US"),
                    "第二次保存把上一版留在 .bak（写坏/崩溃时可回滚）");

                var beforeLocked = System.IO.File.ReadAllText(f);
                using (new System.IO.FileStream(f, System.IO.FileMode.Open,
                           System.IO.FileAccess.ReadWrite, System.IO.FileShare.None))
                {
                    AAS.Current.Language = "en-US";
                    bool ok3 = AAS.Save();
                    Check(!ok3 && !string.IsNullOrEmpty(AAS.LastSaveIssue),
                        $"目标不可写时 Save 返回 false 并给出原因（ok={ok3}, issue={AAS.LastSaveIssue}）");
                    // ⚠ 不能在独占句柄还开着时 ReadAllText —— 那连打开都不允许（实测 IOException），是探针自己的错
                }
                Check(System.IO.File.ReadAllText(f) == beforeLocked,
                    "失败时目标文件内容一字未动（原子写真正要买的东西）");

                var bad = System.IO.Path.Combine(dir, "corrupt.json");
                System.IO.File.WriteAllText(bad, "{ this is not json");
                AAS.Load(bad);
                Check(AAS.LastLoadIssue != null,
                    $"损坏的设置文件被记录而非静默重置（issue={AAS.LastLoadIssue}）");

                // ── 全字段落盘往返锁（2026-09-24 补）──
                //   上面几条只测 Language / MaxQueueSize ⇒ **没进 `Save()` 手写克隆表**的属性能永远不被发现
                //   （实测就是这样漏着 `RenderingMode` 而本探针一直绿）。⚠ 光看"文件里有没有这个键"也不够：
                //   `Save()` 序列化的是 `new AppSettings{…}` 那个 clone ⇒ 漏掉的字段会带着**模型默认值**
                //   一起落盘，键在、值却是错的。⇒ 判据只能是「设非默认值 → Save → 读回 → 逐个相等」。
                AAS.Load(f);   // 目标重新指回 f（上一步的损坏文件会把 Save 引到 corrupt.json）
                var fresh = new FfmpegGui.Models.AppSettings();
                var props = typeof(FfmpegGui.Models.AppSettings)
                    .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                var sentinels = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<System.Reflection.PropertyInfo, object?>>();
                var unsupported = new System.Collections.Generic.List<string>();
                int eligible = 0;
                foreach (var p in props)
                {
                    if (!p.CanRead || !p.CanWrite) continue;
                    if (p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false).Length > 0) continue;
                    if (p.GetCustomAttributes(typeof(ObsoleteAttribute), false).Length > 0) continue;
                    eligible++;
                    var sent = SettingsSentinel(p, p.GetValue(fresh));
                    if (sent == null) { unsupported.Add(p.Name + ":" + p.PropertyType.Name); continue; }
                    p.SetValue(AAS.Current, sent);
                    sentinels.Add(new System.Collections.Generic.KeyValuePair<System.Reflection.PropertyInfo, object?>(p, sent));
                }
                Check(unsupported.Count == 0,
                    "每个候选持久化属性都能造出非默认哨兵（不支持的类型 = " + (unsupported.Count == 0 ? "无" : string.Join(", ", unsupported)) + "）");
                Check(eligible >= 20,
                    $"反射口径自检：候选持久化属性 >= 20（实得 {eligible}）⇒ 否则下面的往返比对会空转");
                Check(AAS.Save(), "带哨兵值的那次 Save 成功");
                //   ⚠ 读回**不用** `AppJsonContext`（它是 internal，探针看不见），也不用 `AAS.Load`
                //     （它会用 `FFMPEGGUI_*` 环境变量覆盖路径/队列/主题等字段 ⇒ 哨兵会被环境值洗掉，测的就不是落盘了）。
                //     这里用默认命名 + 大小写不敏感的普通反序列化：文件里的键就是 PascalCase 的属性名，
                //     读回口径与写出口径一致 ⇒ 本条只回答"我设的值有没有真的出去再回来"。
                var back = System.Text.Json.JsonSerializer.Deserialize<FfmpegGui.Models.AppSettings>(
                    System.IO.File.ReadAllText(f),
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                var diffs = new System.Collections.Generic.List<string>();
                if (back == null) diffs.Add("反序列化读回 null");
                else
                {
                    foreach (var kv in sentinels)
                    {
                        var got = kv.Key.GetValue(back);
                        if (!SettingsSame(kv.Key.PropertyType, kv.Value, got))
                            diffs.Add(kv.Key.Name + ": 写入 " + SettingsShow(kv.Value) + " / 读回 " + SettingsShow(got));
                    }
                }
                Check(diffs.Count == 0,
                    "全部 " + sentinels.Count + " 个持久化属性都真的落盘并能读回（不同源 = "
                    + (diffs.Count == 0 ? "无" : string.Join(" | ", diffs)) + "）");
            }
            finally
            {
                try { System.IO.Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>
        /// 为「落盘往返」造一个**必然不等于模型默认值**的哨兵；返回 null = 该类型不认识
        /// ⇒ 由调用方点名判红（新增属性用了没见过的类型时，逼这里同步，而不是静默少测一个字段）。
        /// </summary>
        private static object? SettingsSentinel(System.Reflection.PropertyInfo p, object? freshDefault)
        {
            var t = p.PropertyType;
            if (t == typeof(string))
            {
                var s = "zz-" + p.Name + "-7";
                return (freshDefault as string) == s ? s + "x" : s;
            }
            if (t == typeof(bool)) return !(freshDefault as bool? ?? false);
            if (t == typeof(int))
            {
                var v = 4242;
                return (freshDefault as int?) == v ? 4243 : v;
            }
            if (t == typeof(System.Collections.Generic.List<string>))
                return new System.Collections.Generic.List<string> { "zz-a", "zz-b" };
            return null;
        }

        private static bool SettingsSame(Type t, object? a, object? b)
        {
            if (t == typeof(System.Collections.Generic.List<string>))
            {
                var la = a as System.Collections.Generic.List<string>;
                var lb = b as System.Collections.Generic.List<string>;
                if (la == null || lb == null || la.Count != lb.Count) return false;
                for (int i = 0; i < la.Count; i++) if (la[i] != lb[i]) return false;
                return true;
            }
            return Equals(a, b);
        }

        private static string SettingsShow(object? v)
        {
            if (v is System.Collections.Generic.List<string> l) return "[" + string.Join(",", l) + "]";
            return v?.ToString() ?? "(null)";
        }

        // ══════════════════════════════ runner ══════════════════════════════

        /// <summary>
        /// P1-2 的守卫探针：用 <c>cmd.exe</c> 当子进程替身（FfmpegRunner 的 ffmpegPath 可注入），
        /// 这样不必真等 30 分钟也能测“静默 + 不耗 CPU”这一类挂死。
        /// ⚠ 2026-09-19（#15-b 收尾轮）**载体大改**，起因是一条真实回归：心跳改为**默认开启**后，
        /// 注入自检原先**只看参数、不看目标** ⇒ 给 <c>cmd.exe</c> 追加 ` -progress pipe:1` 会**改坏它的
        /// 命令行**（ping 收到未知选项、**0.0 s 退 1**）⇒ case A / case C **两条同时转红**
        /// （E⑫ 整轮实测：`runner` 2/0 → 0/2）。修法（`CanInjectProgressPipe` 加 `IsFfmpegTarget` 一道）后
        /// **cmd.exe 不再被注入** ⇒ 它的活性判据回到**旧双信号路径**。
        /// ⇒ 于是 C 段载体**必须换**：cmd 上「心跳判死」分支已不可达 ⇒ 原载体上「取消优先于心跳判死」
        /// 会**恒绿**（空断言，看着像在测其实没测）。新载体 = **真 ffmpeg + 1 字节畸形 `.jxl`**
        /// （与 <c>verify-ffmpeg-heartbeat.ps1</c> ② 同一 fixture）：名字像 ffmpeg ⇒ **会被注入**，
        /// 且**零进度块**（本探针前置实测：4 s 仍存活、进度 0 字节）⇒ 同时满足「会被注入」∧「零进度块」
        /// 两个必要条件，才能真的二分“谁先到谁赢”。
        /// ⚠ B 段（原 <c>runner --hang</c>）同理：原载体是 cmd、靠**降低阈值**判死；cmd 不再被注入后
        /// 它只能走旧 30 min 路径 ⇒ 换成真 ffmpeg 忙等，并**默认跑**（不再 gate 在 flag 后 —— 否则
        /// 整轮跑的是无参数 `runner`，心跳判死路径会**零覆盖**）。成本 ~2 s（hbSec=1 即判死）。
        /// </summary>
        private static async Task ProbeRunnerGuard(string[] args)
        {
            const string Cmd = @"C:\Windows\System32\cmd.exe";
            // ❗ 替身一律用**绝对路径**，不要写裸命令名 ping：
            //   本宿主下 Start-Process 传给子进程的环境块**不含 System32**（实测 where.exe ping ⇒ exit=1、
            //   cmd 报 "'ping' is not recognized..."），而宿主里 Get-Command ping.exe 却能找到。
            //   用裸名会让本探针在“经门禁脚本跑”时假红（退出码 1/0.2s），而直接跑却绿 —— 曾据此误判为产物陈旧。
            //   生产代码不受影响：FfmpegRunner 用的是 AppSettingsService.Current.FfmpegPath（绝对路径）。
            const string Ping = @"C:\Windows\System32\PING.EXE";
            // 忙等载体：真 ffmpeg + 1 字节 `0x00` 的 .jxl（与 verify-ffmpeg-heartbeat.ps1 ② 同一 fixture）。
            // ⚠ 实测（本探针落地前）：`-i <1字节.jxl> -frames:v 1 <out.png>` 在 4.0 s 时**仍存活且进度 0 字节**
            //   ⇒ 满足「会被注入」（名字含 ffmpeg）∧「零进度块」两个必要条件。
            var ff = FfmpegGui.Services.AppSettingsService.Current.FfmpegPath;
            var work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "runnerprobe_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(work);
            var badJxl = System.IO.Path.Combine(work, "bad.jxl");
            System.IO.File.WriteAllBytes(badJxl, new byte[] { 0x00 });
            var logA = new System.Text.StringBuilder();
            var swA = System.Diagnostics.Stopwatch.StartNew();
            var rcA = await FfmpegRunner.RunAsync($"/c {Ping} -n 3 127.0.0.1 >nul", s => logA.Append(s), Cmd);
            swA.Stop();
            // ⚠ 失败时必须把子进程输出带出来：只报“退出码 1”无法区分“cmd 自己报错”与“守卫误杀”。
            //   实测（2026-09-15）：经 Start-Process 启动本探针时 cmd 退 1/0.2s，直接跑却退 0/2.4s；
            //   没有这段输出就只能靠猜。
            Check(rcA == 0 && logA.ToString().IndexOf("[超时]") < 0,
                $"静默但正常退出的子进程不被误杀（退出码 {rcA}，{swA.Elapsed.TotalSeconds:F1}s）"
                + (rcA == 0 ? "" : $"；子进程输出=<{Dump(logA)}>"));

            // ── C-legacy：取消语义（cmd 替身 ⇒ (A) 后走**旧双信号路径**）──────────────────────
            //   ⚠ 它**不再**经过心跳判死分支 ⇒ 只能验“旧路径的取消语义”，别当成“取消优先于心跳判死”。
            //   ⚠ 两条都用 `threwC` 前置判据 ⇒ **绿/红都算 2 条**，断言总数不随成败漂移（基线才可对账）。
            var logC = new System.Text.StringBuilder();
            var swC = System.Diagnostics.Stopwatch.StartNew();
            var threwC = false;
            var rcC = 0;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                try
                {
                    rcC = await FfmpegRunner.RunAsync($"/c {Ping} -n 600 127.0.0.1 >nul", s => logC.Append(s), Cmd, cts.Token);
                }
                catch (OperationCanceledException) { threwC = true; }
            }
            swC.Stop();
            Check(threwC, "C-legacy 取消应抛 OperationCanceledException"
                + (threwC ? "" : $"（实际返回码 {rcC}；子进程输出=<{Dump(logC)}>）"));
            Check(threwC && swC.Elapsed.TotalSeconds < 30,
                $"C-legacy 取消在 {swC.Elapsed.TotalSeconds:F1}s 内传播（子进程被终止，不是等到自然退出）");

            // ── C1：真 ffmpeg 忙等（零进度块）下，**取消必须优先于心跳判死** ──────────────────────
            //   hbSec=8 ⇒ 心跳判死最早落在 ~8 s；取消在 3 s ⇒ 若实现把取消**推迟到心跳 tick**（或让心跳
            //   抢先判死），本用例会拿到 HangExitCode（≈8 s）而不是 OCE ⇒ 转红。
            //   ⚠ 两条自检专防“空断言”：注入必须真的发生、且心跳判死行必须**不出现**。
            {
                var prev = Environment.GetEnvironmentVariable("FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC");
                Environment.SetEnvironmentVariable("FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC", "8");
                try
                {
                    var log1 = new System.Text.StringBuilder();
                    var sw1 = System.Diagnostics.Stopwatch.StartNew();
                    var threw1 = false;
                    var rc1 = 0;
                    using (var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                    {
                        try
                        {
                            rc1 = await FfmpegRunner.RunAsync(
                                $"-y -hide_banner -loglevel error -i \"{badJxl}\" -frames:v 1 \"{System.IO.Path.Combine(work, "out_c1.png")}\"",
                                s => log1.Append(s), ff, cts1.Token);
                        }
                        catch (OperationCanceledException) { threw1 = true; }
                    }
                    sw1.Stop();
                    var t1 = log1.ToString();
                    Check(threw1, "C1 真 ffmpeg 忙等（零进度块）下，**取消**必须抛 OperationCanceledException"
                        + (threw1 ? "" : $"；实际返回码 {rc1}（{FfmpegRunner.HangExitCode} = 心跳判死抢先），"
                            + $"耗时 {sw1.Elapsed.TotalSeconds:F1}s；输出=<{Dump(log1)}>"));
                    Check(threw1 && sw1.Elapsed.TotalSeconds >= 2.5 && sw1.Elapsed.TotalSeconds <= 6,
                        $"C1 取消在 ~3 s 生效（实测 {sw1.Elapsed.TotalSeconds:F1}s ∈ [2.5, 6]）⇒ **远早于** 8 s 心跳阈值"
                        + " ⇒ 取消没有被推迟到心跳 tick");
                    Check(t1.IndexOf("[心跳] 已追加") >= 0,
                        "C1 自检：本用例确实**注入了** `-progress pipe:1`（否则「取消优先」是空断言）");
                    Check(t1.IndexOf("无进度心跳") < 0,
                        "C1 自检：**没有**出现心跳判死行 ⇒ 判死没有抢先于取消");
                }
                finally { Environment.SetEnvironmentVariable("FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC", prev); }
            }

            // ── B：忙等（零进度块）必须被心跳**限时判死**（原 `--hang` 的真 ffmpeg 版）────────────
            {
                var prevB = Environment.GetEnvironmentVariable("FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC");
                Environment.SetEnvironmentVariable("FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC", "1");
                try
                {
                    var logB = new System.Text.StringBuilder();
                    var swB = System.Diagnostics.Stopwatch.StartNew();
                    var rcB = await FfmpegRunner.RunAsync(
                        $"-y -hide_banner -loglevel error -i \"{badJxl}\" -frames:v 1 \"{System.IO.Path.Combine(work, "out_stall.png")}\"",
                        s => logB.Append(s), ff);
                    swB.Stop();
                    var txt = logB.ToString();
                    Check(rcB == FfmpegRunner.HangExitCode && txt.IndexOf("无进度心跳") >= 0,
                        $"B 忙等（零进度块）被心跳限时判死（退出码 {rcB}，期望 {FfmpegRunner.HangExitCode}，"
                        + $"耗时 {swB.Elapsed.TotalSeconds:F0}s）—— 忙等型挂死不再永不返回");
                }
                finally { Environment.SetEnvironmentVariable("FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC", prevB); }
            }

            try { System.IO.Directory.Delete(work, true); } catch { }
        }

        /// <summary>
        /// 把子进程输出压成一行并截断，供断言失败时携带。
        /// 只报退出码的诊断价值极低（“退出码 1”既可能是守卫误杀，也可能是 cmd 自己报错）。
        /// </summary>
        ///
        /// <para><b>为什么搬到这里</b>：全仓只有 `ProbeRunnerGuard` 一个调用者（旧宿主 Program.cs:6443/6462/6493）
        /// ⇒ 按模板第 4 条第 3 款「只在**一个** probe 里用的 helper 连同该 probe 一起搬走」。</para>
        private static string Dump(System.Text.StringBuilder sb)
        {
            var s = sb.ToString().Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length == 0) return "(无输出)";
            return s.Length > 300 ? s.Substring(0, 300) + "…" : s;
        }
    }
}
