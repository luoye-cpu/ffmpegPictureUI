using System;
using System.IO;
using System.Text;
using FfmpegGui.Services;
using TC = FfmpegGui.Services.ColorMapping.TransferCurve;   // 色彩映射引擎的曲线表（P1）
using CK = FfmpegGui.Services.ColorMapping.ColorKernels;     // 执行内核（P3）
using CM = FfmpegGui.Services.ColorMapping.ColorMetrics;     // PSNR/ΔE 度量
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
using CP = FfmpegGui.Services.ColorMapping.ColorTransformPlan;   // S5′ 裁决探针
using GF = FfmpegGui.Services.ColorMapping.RawGamutFit;         // `--raw-color-target auto` 判据内核（2026-10-04）
using VK = FfmpegGui.Services.ColorMapping.VerdictKind;          // S5′ 裁决探针
using DG = FfmpegGui.Services.ColorMapping.DegradationCodes;     // S5′ 裁决探针
using F709 = FfmpegGui.Services.ColorMapping.Transfer709Curve;
using GMJ = FfmpegGui.Services.ColorMapping.GainMapJpegCodec;   // GainMap 解码口径/编码参数单一真值（gmgamut）
using AAS = FfmpegGui.Services.AppSettingsService;

namespace ServiceProbe
{
    internal static partial class Program
    {
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

        /// <summary>合成测试帧（rgb48le）：灰阶斜坡 + 饱和原色 + 斜向渐变（灰阶图测不出色域错）。</summary>
        private static byte[] SyntheticFrame16(int w, int h)
        {
            var b = new byte[w * h * 6];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 6;
                    ushort r, g, bl;
                    int k = x + y;
                    if (y < h / 4) { r = g = bl = (ushort)((k % 4096) * 16); }                     // 灰阶斜坡
                    else if (y < h / 2) { int t = (k % 512) * 128; r = (ushort)Math.Min(t, 65535); g = bl = 0; }  // 红梯
                    else if (y < 3 * h / 4)                                                  // 饱和原色块
                    {
                        int c = (x / Math.Max(1, w / 6)) % 6;
                        r = (ushort)((c is 0 or 3 or 4) ? 65535 : 0);
                        g = (ushort)((c is 1 or 3 or 5) ? 65535 : 0);
                        bl = (ushort)((c is 2 or 4 or 5) ? 65535 : 0);
                    }
                    else { r = (ushort)(x * 65535 / Math.Max(1, w - 1)); g = (ushort)(y * 65535 / Math.Max(1, h - 1)); bl = 32768; }
                    b[o] = (byte)r; b[o + 1] = (byte)(r >> 8);
                    b[o + 2] = (byte)g; b[o + 3] = (byte)(g >> 8);
                    b[o + 4] = (byte)bl; b[o + 5] = (byte)(bl >> 8);
                }
            return b;
        }

    }
}
