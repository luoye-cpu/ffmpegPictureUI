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

namespace Tests.Color
{
    /// <summary>
    /// 子系统测试组：**color**（色彩基元与数学）—— 2026-10-05 从 ServiceProbe 的单个
    /// 577 KB `Program.cs` 中物理拆出，使"改色彩基元/曲线这一块"只需跑本工程。
    ///
    /// <para><b>覆盖的 mode</b>：`matrix` `curve` `ootf` `hlgwire` `bt2446` `pq` `colormath`
    /// `iccname`（见 <see cref="TestGroups"/>）。</para>
    ///
    /// <para><b>抽取规则</b>：方法体**逐字**从旧宿主搬来，字符串一个字未改（门禁在 grep 那些文本）。
    /// 共享 helper 一律转发 <see cref="Harness"/>，不复制实现。仅 `Mul9` 与 `Matrix` 探针
    /// 一同搬来（全仓只有它一个调用者）。</para>
    /// </summary>
    internal static class ColorGroup
    {
        /// <summary>
        /// <c>Check</c> 转发到共享 `Harness`。
        /// ⚠ **不是复制实现**：计数与输出格式只有 `Harness` 一份（否则各组会漂移出各自的
        /// PASS/FAIL 口径，本仓已有"两处独立实现漂移"的教训）。保留本地同名包装，
        /// 只为让**拆出的原代码一字不改**（降低拆分引入差异的风险）。
        /// </summary>
        private static void Check(bool ok, string msg) => Harness.Check(ok, msg);

        internal static void RunCurve() => ProbeCurve();
        internal static void RunMatrix() => ProbeMatrix();
        internal static void RunOotf() => ProbeOotf();
        internal static void RunHlgWire() => ProbeHlgWire();
        internal static void RunBt2446() => ProbeBt2446();
        internal static void RunPq() => ProbePq();
        internal static void RunColorMath() => ProbeColorMathAgainstZimg();
        internal static void RunIccName() => ProbeIccName();

        // ══════════════════════════════ curve ══════════════════════════════

        private static void ProbeCurve()
        {
            void Anch(string what, double got, double want, double tol)
            {
                Console.WriteLine($"  {what,-34} got={got:F6} want={want:F6} err={Math.Abs(got - want):E1}");
                Check(Math.Abs(got - want) <= tol, $"{what} 锚点（≤{tol:g2}）");
            }
            // 锚点值写成**标准定义式**自算，而不是抄录小数（本探针曾因抄错小数把正确实现判为 FAIL）
            Anch("sRGB ToLinear(0.5)", TC.Srgb.ToLinear(0.5), Math.Pow((0.5 + 0.055) / 1.055, 2.4), 1e-9);
            Anch("BT.709 ToLinear(0.5)", TC.Bt709.ToLinear(0.5), Math.Pow((0.5 + 0.099) / 1.099, 1 / 0.45), 1e-9);
            // 趾段：sRGB EOTF 为 L = V/12.92（不是 ×12.92）；BT.709 为 L = V/4.5。c = 1/斜率。
            Anch("sRGB 趾段 ToLinear(0.03)", TC.Srgb.ToLinear(0.03), 0.03 / 12.92, 1e-9);
            Anch("BT.709 趾段 ToLinear(0.05)", TC.Bt709.ToLinear(0.05), 0.05 / 4.5, 1e-9);
            Anch("gamma2.2 ToLinear(0.5)", TC.Gamma22.ToLinear(0.5), Math.Pow(0.5, 2.2), 1e-9);
            Anch("gamma2.6 ToLinear(0.5)", TC.Gamma26.ToLinear(0.5), Math.Pow(0.5, 2.6), 1e-9);
            Anch("ProPhoto ToLinear(0.5)", TC.ProPhoto.ToLinear(0.5), Math.Pow(0.5, 1.8), 1e-9);
            Anch("ProPhoto 趾段 ToLinear(0.03)", TC.ProPhoto.ToLinear(0.03), 0.03 / 16.0, 1e-12);
            Anch("ProPhoto 趾段连续(拐点 d)", TC.ProPhoto.ToLinear(TC.ProPhoto.D), TC.ProPhoto.D / 16.0, 1e-12);
            // ⚠️ PQ 绝对锚点：码值必须是**精确解**，容差必须**紧到能抓住常数错误**。
            //    旧锚点用 0.5799 / 0.7516（4 位小数）+ 容差 1 / 3 —— 这两个数都是**不精确的码值**：
            //    203 nits 处 4 位小数的量化误差就有 ±1.4 nits，而 m1 常数写错（2610→2615，差 0.19%）
            //    在 203 nits 处只造成 ~1.1 nits 偏差，**正好藏在容差里** —— 这是该错误长期存活的原因之一。
            //    现码值经 **zimg 实测核对**（ffmpeg -vf "zscale=tin=smpte2084:t=linear:npl=10000"，
            //    16-bit 量化地板 ±0.04 nits）：0.580694 → 203.25 nits、0.751812 → 999.77 nits、
            //    0.5 → 92.32 nits ⇒ 203 nits 的 PQ 码值是 **0.5807** 而不是 0.5799（后者仅 201.5 nits）。
            Anch("PQ EOTF(0.5) nits", TC.Pq.ToLinear(0.5) * 10000.0, 92.28, 0.5);
            Anch("PQ EOTF(0.580694) nits(=203)", TC.Pq.ToLinear(0.580694) * 10000.0, 203.0, 0.5);
            Anch("PQ EOTF(0.751812) nits(=1000)", TC.Pq.ToLinear(0.751812) * 10000.0, 1000.0, 1.0);
            Anch("HLG EOTF(0.5)=拐点 1/12", TC.Hlg.ToLinear(0.5), 1.0 / 12.0, 1e-6);
            Anch("HLG EOTF(0.2)=V^2/3", TC.Hlg.ToLinear(0.2), 0.04 / 3.0, 1e-9);
            Anch("Linear ToLinear(0.37)", TC.Linear.ToLinear(0.37), 0.37, 0.0);

            // 互逆自洽：17 个采样点往返
            var curves = new[] { TC.Srgb, TC.Bt709, TC.Gamma22, TC.Gamma28, TC.ProPhoto, TC.Gamma18,
                                 TC.Gamma26, TC.Smpte240M, TC.Pq, TC.Hlg };
            foreach (var cv in curves)
            {
                double worst = 0;
                for (int i = 0; i <= 16; i++)
                {
                    double x = i / 16.0;
                    worst = Math.Max(worst, Math.Abs(cv.FromLinear(cv.ToLinear(x)) - x));
                }
                Check(worst <= 1e-6, $"{cv.Name} 往返互逆（max err={worst:E2}）");
            }

            // 单调性 + 表生成
            foreach (var cv in new[] { TC.Srgb, TC.Bt709, TC.ProPhoto, TC.Pq, TC.Hlg, TC.Gamma26 })
            {
                var t = cv.BuildEotfTable8();
                bool mono = t[0] <= t[1];
                for (int i = 2; i < 256; i++) mono &= t[i] > t[i - 1];
                Check(mono && Math.Abs(t[255] - 1.0f) < 2e-6f, $"{cv.Name} 8bit 表单调且白点=1.0");
            }

            // 与现有生产内核一致（统一性：引擎必须与 SimdPixelOps 的 sRGB 一致）
            double worstSrgb = 0;
            for (int i = 0; i <= 255; i += 17)
                worstSrgb = Math.Max(worstSrgb, Math.Abs(TC.Srgb.ToLinear(i / 255.0)
                    - FfmpegGui.Services.SimdPixelOps.SrgbToLinearScalar((float)(i / 255.0))));
            Check(worstSrgb <= 1e-5, $"引擎 sRGB 曲线与 SimdPixelOps 一致（max={worstSrgb:E2}）");

            // 与生产 SIMD 内核的 PQ 一致（统一性护栏：同一曲线只允许一份真值，否则两处会分叉）
            double worstPq = 0;
            foreach (double v in new[] { 0.0, 0.01, 0.1, 0.25, 0.5, 0.5799, 0.7516, 0.9, 1.0 })
            {
                worstPq = Math.Max(worstPq, Math.Abs(TC.Pq.ToLinear(v) - FfmpegGui.Services.SimdPixelOps.PqEotfScalar((float)v)));
                worstPq = Math.Max(worstPq, Math.Abs(TC.Pq.FromLinear(v) - FfmpegGui.Services.SimdPixelOps.PqOetfScalar((float)v)));
            }
            Check(worstPq <= 2e-5, $"引擎 PQ 与 SimdPixelOps 双向一致（max={worstPq:E2}，float 参考实现含 MathF 误差）");

            // 查询接口
            Check(TC.ForCicpTransfer("bt470m")!.Value.G == 2.2, "CICP bt470m → gamma2.2");
            Check(TC.ForCicpTransfer("iec61966-2-1")!.Value.Equivalent(TC.Srgb), "CICP iec61966-2-1 → sRGB");
            Check(TC.ForCicpTransfer("prophoto") == null, "未知 CICP token → null（不猜）");
            Check(TC.ForSourceTransfer("gamma1.8")!.Value.G == 1.8, "源名 gamma1.8 → 1.8");
            Check(TC.Srgb.CicpToken == "iec61966-2-1" && TC.ProPhoto.CicpToken == null,
                "CICP 可命名性标记正确（ProPhoto 不可命名）");
            Check(TC.Pq.CicpToken == "smpte2084" && TC.Hlg.CicpToken == "arib-std-b67", "HDR 曲线 CICP 标记");

            // ── #29：BT.709 的 **zimg 实现形态**（`--color-709-curve zimg` 的出口曲线）锚点 ──
            // 这条曲线存在的唯一理由 = 复刻历史产物；它**不是** BT.709 的定义式，两者差到肉眼可见
            // （见下方 16bit 最大差锚点）。所以它必须同时满足：
            //   ① 数学上是纯 γ(1/2.4)（无 α、无线性趾）；② 不被 CICP token 查询命中（默认口径永远是定义式）；
            //   ③ 与定义式的差**量级可断言**（不是"差不多"也不是"等价写法"）。
            Anch("Bt709Zimg ToLinear(0.5)", TC.Bt709Zimg.ToLinear(0.5), Math.Pow(0.5, 2.4), 1e-12);
            Anch("Bt709Zimg FromLinear(0.5)", TC.Bt709Zimg.FromLinear(0.5), Math.Pow(0.5, 1.0 / 2.4), 1e-12);
            Check(TC.Bt709Zimg.CicpToken == null && !TC.Bt709Zimg.HasToeSegment
                  && !TC.Bt709Zimg.HasItuRoundingKink && !TC.Bt709Zimg.Equivalent(TC.Bt709),
                  $"Bt709Zimg 不可 CICP 命名且无趾段（实 token={TC.Bt709Zimg.CicpToken ?? "null"} 趾={TC.Bt709Zimg.HasToeSegment}）"
                + "⇒ 它只描述像素，不冒充标准里的 bt709");
            Check(TC.ForCicpTransfer("bt709")!.Value.Equivalent(TC.Bt709),
                "CICP bt709 仍解析为定义式（zimg 形态不得变成 token 的默认解读，否则 std 口径被静默替换）");
            {
                double maxD = 0, sumD = 0;
                const int samples = 65536;
                for (int i = 0; i < samples; i++)
                {
                    double l = i / (samples - 1.0);
                    double d = Math.Abs(TC.Bt709.FromLinear(l) - TC.Bt709Zimg.FromLinear(l));
                    if (d > maxD) maxD = d;
                    sumD += d;
                }
                // 实测锚点：定义式与纯 γ(1/2.4) 的编码差最大 6986/65535（linear≈0.017），
                // 均值 3059/65535；zscale=t=bt709 的实测值 7231 与之差 245 ≈ 1 LSB@8bit 的量化地板。
                Check(maxD * 65535 >= 6900 && maxD * 65535 <= 7100 && sumD / samples * 65535 >= 2900
                      && sumD / samples * 65535 <= 3200,
                    $"两条 BT.709 口径的差可看见：max={maxD * 65535:F0} LSB@16bit（={maxD * 255:F1} LSB@8bit）"
                  + $" mean={sumD / samples * 65535:F0}（界 6900–7100 / 2900–3200）");
            }
        }

        // ══════════════════════════════ ootf ══════════════════════════════

        /// <summary>
        /// ootf —— BT.2100-2 Table 5 HLG OOTF 门禁（常数逐字取自建议书正文）。
        /// 重点验：γ 公式、α=L_W 的峰值约定、代数反解的往返恒等、不钳位（Note 5h）、
        /// 以及一个**外部可解释锚点**：HLG 0.75 码值应落在 SDR 参考白附近。
        /// </summary>
        private static void ProbeOotf()
        {
            double G(double lw) => FfmpegGui.Services.ColorMapping.Bt2100Ootf.SystemGamma(lw);
            Check(Math.Abs(G(1000) - 1.2) < 1e-12, $"γ(1000nit) = 1.2（建议书明示）");
            Check(Math.Abs(G(4000) - 1.4527) < 1e-3, $"γ(4000nit) = 1.2+0.42·log10(4) = {G(4000):F4}（3 位有效 1.45）");
            Check(Math.Abs(G(300) - 0.9804) < 1e-3, $"γ(300nit) = {G(300):F4}（≈0.98）");
            Check(Math.Abs(G(10000) - 1.62) < 1e-12, $"γ(10000nit) = {G(10000):F4}（=1.62）");
            double sum = FfmpegGui.Services.ColorMapping.Bt2100Ootf.YsR
                       + FfmpegGui.Services.ColorMapping.Bt2100Ootf.YsG
                       + FfmpegGui.Services.ColorMapping.Bt2100Ootf.YsB;
            Check(Math.Abs(sum - 1.0) < 1e-12, $"Y_S 系数和 = 1（{sum}）→ 消色差时 Y_S≡E，不引入额外增益");

            double r = 1, g = 1, b = 1;
            FfmpegGui.Services.ColorMapping.Bt2100Ootf.Apply(ref r, ref g, ref b, 1000);
            Check(Math.Abs(r - 1000) < 1e-9 && Math.Abs(g - 1000) < 1e-9,
                $"α=L_W：消色差名义峰值 (1,1,1) → {r:F1} nits（应等于 L_W=1000）");

            bool ach = true;
            for (int i = 1; i <= 20; i++)
            {
                double e = i / 10.0;   // 含超白 1.1..2.0
                double rr = e, gg = e, bb = e;
                FfmpegGui.Services.ColorMapping.Bt2100Ootf.Apply(ref rr, ref gg, ref bb, 1000);
                double want = FfmpegGui.Services.ColorMapping.Bt2100Ootf.ApplyAchromatic(e, 1000);
                if (Math.Abs(rr - want) > 1e-9 * Math.Max(1, want)) ach = false;
            }
            Check(ach, "一般式与消色差特例一致：F = α·E^γ（含超白 E>1）");

            bool mono = true;
            for (int i = 1; i < 512; i++)
                if (FfmpegGui.Services.ColorMapping.Bt2100Ootf.ApplyAchromatic(i / 511.0 * 2, 1000)
                    < FfmpegGui.Services.ColorMapping.Bt2100Ootf.ApplyAchromatic((i - 1) / 511.0 * 2, 1000) - 1e-12)
                    mono = false;
            Check(mono, "消色差 OOTF 在 [0,2] 上单调不降");

            // 往返恒等（含超白与负值：Note 5h 要求不得钳位，反解必须能原路返回）
            double worst = 0; string worstAt = "";
            foreach (var t in new[] { (0.3, 0.2, 0.1), (1.5, 1.5, 1.5), (2.0, 0.5, 0.05), (0.9, -0.2, 0.05) })
            {
                double x = t.Item1, y = t.Item2, z = t.Item3;
                var key = $"({x},{y},{z})";
                FfmpegGui.Services.ColorMapping.Bt2100Ootf.Apply(ref x, ref y, ref z, 4000);
                FfmpegGui.Services.ColorMapping.Bt2100Ootf.ApplyInverse(ref x, ref y, ref z, 4000);
                double e = Math.Max(Math.Abs(x - t.Item1), Math.Max(Math.Abs(y - t.Item2), Math.Abs(z - t.Item3)));
                if (e > worst) { worst = e; worstAt = key; }
            }
            Check(worst < 1e-12, $"OOTF ∘ OOTF⁻¹ 恒等（最大误差 {worst:E2} @ {worstAt}，含超白/负值）");

            double sw = 1.5, sg = 1.5, sb = 1.5;
            FfmpegGui.Services.ColorMapping.Bt2100Ootf.Apply(ref sw, ref sg, ref sb, 1000);
            Check(sw > 1000, $"Note 5h：超白不被钳位（1.5 → {sw:F1} nits > L_W）");

            // 外部锚点：HLG 系统级 EOTF 的 0.75 码值经 OOTF(1000nit) 后应落在 SDR 参考白附近
            double e75 = TC.Hlg.ToLinear(0.75);
            double rr75 = e75, gg75 = e75, bb75 = e75;
            FfmpegGui.Services.ColorMapping.Bt2100Ootf.Apply(ref rr75, ref gg75, ref bb75, 1000);
            Console.WriteLine($"    锚点：HLG 0.75 → 场景线性 {e75:F5} → 显示 {rr75:F1} nits（SDR 参考白 {TC.SdrWhiteNits:F0}）");
            Check(Math.Abs(rr75 - TC.SdrWhiteNits) / TC.SdrWhiteNits < 0.05,
                $"HLG 0.75 码值≈SDR 参考白（{rr75:F1} vs {TC.SdrWhiteNits:F0} nits，偏差 {100 * Math.Abs(rr75 - TC.SdrWhiteNits) / TC.SdrWhiteNits:F1}%）");
        }

        // ══════════════════════════════ hlgwire ══════════════════════════════

        /// <summary>
        /// hlgwire —— **接线级**门禁：HLG 是否在**真实执行路径**上应用了 BT.2100 OOTF。
        ///
        /// 为什么需要它（2026-09-16 接手核查）：`ootf` 只测 `Bt2100Ootf` **类本身**（数学），
        /// `engine` 只测 P3→sRGB（SDR）—— 两者都覆盖不到「HLG 接线在真实内核路径上生效」。
        /// 实测：HLG 字段只加在 float RGBA 路径（`TransformRgba/8`），而该路径在 `src/` 中**零调用点**；
        /// 生产路径 `TransformRgb48le*` 与 `MeasurePeakLinearRgb48` **都没有 HLG 分支**。
        ///
        /// 判据不依赖任何实现细节：HLG 0.75 码值是 BT.2100 的漫反射白约定，
        /// 场景线性 0.26496 经 OOTF(1000nit) 应得 ≈202 nits ⇒ 归一化到「1.0 = 源峰值」即 ≈0.202。
        /// **某条路径若给出 0.265（场景光原值），就说明它漏了 OOTF。**
        /// </summary>
        private static void ProbeHlgWire()
        {
            const double Code = 0.75;
            const double Peak = 1000.0;   // HLG 名义峰值（与 ColorIntentFactory.FixHdrNits 同源）
            var hlg = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor
                .FromCicp("bt2020", "arib-std-b67", "BT.2100 HLG", nitsOfLinearOne: Peak)!;
            var srgb = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor
                .FromCicp("bt709", "iec61966-2-1", "sRGB")!;

            double scene = hlg.Curve.ToLinear(Code);
            double nits = FfmpegGui.Services.ColorMapping.Bt2100Ootf.ApplyAchromatic(scene, Peak);
            double expectNorm = nits / Peak;   // 归一到**源峰值**域（1.0 = 1000nits），仅作对照打印
            // ⚠️ 生产口径（与 `ColorTransformPlan.ToSpec()` 同源）：内核中间域是「1.0 = SDR 参考白」，
            //    源 HLG 的 1.0 = 1000nits 必须经 LinearScale = 1000/203 换过去 ⇒ 结果落在 1.0=203nits 域。
            //    旧探针**不设** LinearScale ⇒ 实际测的是「把 1000nits 归一直接当成 sRGB 白」的
            //    **非生产路径**（与 xcheck 同一类缺陷，见 2026-09-28 修复）。
            //    本用例 Code=0.75 ⇒ nits≈203 ⇒ expectSdr≈1.002（几乎正好是 SDR 白，便于观察）。
            double sdr = FfmpegGui.Services.ColorMapping.TransferCurve.SdrWhiteNits;
            double expectSdr = nits / sdr;
            Console.WriteLine($"    HLG {Code} → 场景线性 {scene:F5} → OOTF 显示 {nits:F1} nits "
                + $"→ 归一(源峰值) {expectNorm:F5} → SDR 白域 {expectSdr:F5}");

            // 单像素 rgb48**le**（内核按 little-endian 解释字节对）
            ushort v = (ushort)Math.Round(Code * 65535.0);
            var buf = new byte[6];
            for (int k = 0; k < 3; k++) { buf[k * 2] = (byte)(v & 0xFF); buf[k * 2 + 1] = (byte)(v >> 8); }

            var spec = new FfmpegGui.Services.ColorMapping.TransformSpec
            {
                SrcCurve = hlg.Curve,
                DstCurve = srgb.Curve,
                Matrix = null,
                ClampLinearBeforeEncode = false,   // 不钳位，才能看到真实线性值
                SrcIsHlgScene = true,
                SrcHlgSystemGamma = FfmpegGui.Services.ColorMapping.Bt2100Ootf.SystemGamma(Peak),
                LinearScale = Peak / sdr,   // 与生产 ToSpec 同口径：HLG 1.0=1000nits → 1.0=203nits
            };

            // ① 峰值测量路径（tonemap 的 headroom 依据）——漏 OOTF 会让 headroom 算错。
            //    MeasurePeak 的口径是「已乘 LinearScale」⇒ 结果落在 1.0 = SDR 参考白域。
            double peak = FfmpegGui.Services.ColorMapping.ColorKernels.MeasurePeakLinearRgb48(buf, 1, spec, null);
            Check(Math.Abs(peak - expectSdr) / expectSdr < 0.05,
                $"① 峰值测量路径应用了 OOTF + 亮度域换算（期望 {expectSdr:F4}，实得 {peak:F4}）");

            // ② 主转换路径：HLG → sRGB，输出应落在**显示光**且经 SDR 白域换算。
            //    ⚠ Code=0.75 时 nits≈203 ⇒ expectSdr≈1.002 略超 1，48-bit 出口会钳到 65535
            //      （误差 ~0.2%，远小于 5% 容差）⇒ 不影响本判据的判别力。
            var dst = new byte[6];
            FfmpegGui.Services.ColorMapping.ColorKernels.TransformRgb48le(buf, dst, spec, 1, null, null);
            int o0 = dst[0] | (dst[1] << 8);
            double outLin = srgb.Curve.ToLinear(o0 / 65535.0);
            Check(Math.Abs(outLin - expectSdr) / expectSdr < 0.05,
                $"② 主转换路径应用了 OOTF + 亮度域换算（期望线性 {expectSdr:F4}，实得 {outLin:F4}）");

            // ③ float RGBA 路径：与 ①② 同一判据，防止两条实现各自漂移（本项目反复出现过"三处不一致"）
            var f = new float[4] { (float)Code, (float)Code, (float)Code, 1f };
            FfmpegGui.Services.ColorMapping.ColorKernels.TransformRgba(f, spec);
            double fLin = srgb.Curve.ToLinear(f[0]);
            Check(Math.Abs(fLin - expectSdr) / expectSdr < 0.05,
                $"③ float RGBA 路径应用了 OOTF + 亮度域换算（期望线性 {expectSdr:F4}，实得 {fLin:F4}）");
        }

        // ══════════════════════════════ bt2446 ══════════════════════════════

        /// <summary>
        /// bt2446 —— ITU-R Report BT.2446-1 §4 Method A 门禁（常数逐字取自建议书 Table 2/3/4）。
        /// 拿不到外部参照（ffmpeg 无此算子），所以用“标准自洽”作铁门：
        ///   • 分段 knee 在两个边界必须连续 ⇒ 能反推二次段常数是否读错；
        ///   • §4.2 的 255 端点必须精确落到 Lmax=1000 nits ⇒ 能反推指数多项式是否读错。
        /// ⚠ 不拿“往返恒等”当门禁：§4.1 的 Y' 是 1/12.4 域、§4.2 的 Y' 是 BT.2020 SDR 域，不同编码。
        /// </summary>
        private static void ProbeBt2446()
        {
            double Rho(double l) => FfmpegGui.Services.ColorMapping.Bt2446MethodA.Rho(l);
            double Knee(double y) => FfmpegGui.Services.ColorMapping.Bt2446MethodA.Step2Knee(y);
            double TM(double y) => FfmpegGui.Services.ColorMapping.Bt2446MethodA.ToneMapLuma(y);
            double Inv(double y) => FfmpegGui.Services.ColorMapping.Bt2446MethodA.InverseLumaNits(y);

            Console.WriteLine($"    ρ_HDR(1000)={Rho(1000):F4}  ρ_SDR(100)={Rho(100):F4}  E(255)={FfmpegGui.Services.ColorMapping.Bt2446MethodA.Exponent(255):F6}");
            double s = 0.2627 + 0.6780 + 0.0593;
            Check(Math.Abs(s - 1.0) < 1e-12, $"亮度系数和 = 1（{s}）");
            Check(Math.Abs(TM(0.0)) < 1e-12, $"端点：Y'=0 → 0（{TM(0.0):E2}）");
            Check(Math.Abs(TM(1.0) - 1.0) < 1e-9, $"端点：Y'=1 → 1（{TM(1.0):F8}）");

            // 铁门 1：三段 knee 在边界必须连续（常数读错会直接断裂）
            double c1 = Math.Abs(Knee(0.7399) - Knee(0.7399 + 1e-9));
            double c2 = Math.Abs(Knee(0.9909) - Knee(0.9909 + 1e-9));
            Check(c1 < 2e-3, $"knee 第一段/二次段在 0.7399 连续（跳变 {c1:E2}）");
            Check(c2 < 2e-3, $"knee 二次段/第三段在 0.9909 连续（跳变 {c2:E2}）");
            Check(Math.Abs(Knee(1.0) - 1.0) < 1e-12, $"knee 上端点 = 1（{Knee(1.0):F6}）");

            bool mono = true, inr = true; int bad = -1;
            for (int i = 1; i < 1024; i++)
            {
                double a = TM((i - 1) / 1023.0), b = TM(i / 1023.0);
                if (b < a - 1e-12) { mono = false; bad = i; break; }
                if (b < -1e-12 || b > 1 + 1e-12) { inr = false; break; }
            }
            Check(mono, $"§4.1 亮度曲线单调不降（首个下降 idx={bad}）");
            Check(inr, "§4.1 输出落在 [0,1]");

            // 铁门 2：§4.2 的 255 端点必须等于 Lmax
            double peak = Inv(1.0);
            Check(Math.Abs(peak - FfmpegGui.Services.ColorMapping.Bt2446MethodA.LmaxNits) < 0.5,
                $"§4.2 锚点：Y'=1 → {peak:F2} nits，应等于 Lmax=1000（验证指数多项式常数读法）");
            bool mono2 = true;
            for (int i = 1; i < 256; i++) if (Inv(i / 255.0) < Inv((i - 1) / 255.0) - 1e-12) mono2 = false;
            Check(mono2 && Math.Abs(Inv(0.0)) < 1e-12, "§4.2 反变换单调且 f(0)=0");
            Console.WriteLine($"    §4.2 取样：Y'=0.5 → {Inv(0.5):F1} nits；0.75 → {Inv(0.75):F1} nits；1.0 → {peak:F1} nits");

            // 中性灰不得引入色度偏移（Table 3 结构件）
            FfmpegGui.Services.ColorMapping.Bt2446MethodA.ColourCorrect(0.9, 0.9, 0.9, TM(FfmpegGui.Services.ColorMapping.Bt2446MethodA.LumaPrime(0.9, 0.9, 0.9)),
                out double cbg, out double crg, out double ytm);
            Check(Math.Abs(cbg) < 1e-12 && Math.Abs(crg) < 1e-12, $"中性灰：Cb'=Cr'=0（{cbg:E1}/{crg:E1}）");
            // 超白（Y'=1）不得被钳到 <1
            Check(TM(1.0) >= 1.0 - 1e-9, "峰值不被压到 1 以下（保留全部高光层次到端点）");

            // ── 像素级接入（ApplyRgb 的 Bt2446A 分支）──
            var pa = new FfmpegGui.Services.ColorMapping.ToneMapParams
            { Mode = FfmpegGui.Services.ColorMapping.ToneMapMode.Bt2446A, PeakNits = 1000, SdrWhiteNits = 203, SdrPeakNits = 100 };
            Check(pa.Active, $"bt2446a 参数已激活（headroom={pa.Headroom:F3}）");

            // ① 中性灰必须保持中性（不得引入伪色度）
            float x0 = 0.5f * (float)(1000.0 / 203.0), xr = x0, xg = x0, xb = x0;
            FfmpegGui.Services.ColorMapping.ToneMap.ApplyRgb(ref xr, ref xg, ref xb, pa, TC.Srgb);
            Check(Math.Abs(xr - xg) < 1e-6f && Math.Abs(xg - xb) < 1e-6f,
                $"中性灰输入→中性灰输出（{xr:F6}/{xg:F6}/{xb:F6}）");

            // ② 与独立单元实现逐点一致：输出码值应等于 ToneMapLuma(Gamma124(v_m))
            bool agree = true; double worstA = 0; int wIdx = -1;
            for (int i = 1; i <= 32; i++)
            {
                double vm = i / 32.0;                            // “1.0 = L_HDR” 域的线性值
                double vo = vm * 1000.0 / 203.0;                 // → 引擎线性域（1.0 = SDR 白）
                float rr = (float)vo, gg = (float)vo, bb = (float)vo;
                FfmpegGui.Services.ColorMapping.ToneMap.ApplyRgb(ref rr, ref gg, ref bb, pa, TC.Srgb);
                double code = TC.Srgb.FromLinear(rr);            // 重新编码回码值
                double want = TM(FfmpegGui.Services.ColorMapping.Bt2446MethodA.Gamma124(vm));
                double d = Math.Abs(code - want);
                if (d > worstA) { worstA = d; wIdx = i; }
                if (d > 2e-4) agree = false;
            }
            Check(agree, $"像素路径与单元实现一致（最大偏差 {worstA:E2} @i={wIdx}）");

            // ③ 峰值：v_m=1 → 码值 1.0（L_HDR 映到目标满量程）
            float pr = (float)(1000.0 / 203.0), pg = pr, pb = pr;
            FfmpegGui.Services.ColorMapping.ToneMap.ApplyRgb(ref pr, ref pg, ref pb, pa, TC.Srgb);
            double pcode = TC.Srgb.FromLinear(pr);
            Check(Math.Abs(pcode - 1.0) < 1e-4, $"L_HDR 峰值→目标满量程（码值 {pcode:F6}）");

            // ④ 单调（消色差，按码值）
            bool monoA = true; float prev = -1f;
            for (int i = 0; i <= 64; i++)
            {
                double vo = i / 64.0 * 1000.0 / 203.0;
                float rr = (float)vo, gg = (float)vo, bb = (float)vo;
                FfmpegGui.Services.ColorMapping.ToneMap.ApplyRgb(ref rr, ref gg, ref bb, pa, TC.Srgb);
                float c = (float)TC.Srgb.FromLinear(rr);
                if (c < prev - 1e-5f) { monoA = false; break; }
                prev = c;
            }
            Check(monoA, "像素路径消色差单调不降");

            // ⑤ 契约：请求 bt2446a 时确实挂载并归为 ToneMapped（不再被当作未实现拒绝）
            var pqS = CS.FromCicp("bt2020", "smpte2084", "BT.2100 PQ", nitsOfLinearOne: TC.PqPeakNits)!;
            var srgbS = CS.FromCicp("bt709", "iec61966-2-1", "sRGB")!;
            var pA = CE.Plan(new CI { Strategy = ST.Manual, Source = pqS, Target = srgbS, Format = CE.CapsFor("png"),
                                       TargetExplicit = true, HdrPeakNits = 1000,
                                       ToneMapRequested = true, ToneMap = FfmpegGui.Services.ColorMapping.ToneMapMode.Bt2446A });
            Check(pA.Tone is { Active: true } && pA.Tone.Mode == FfmpegGui.Services.ColorMapping.ToneMapMode.Bt2446A
                  && pA.ColorLoss == CL.ToneMapped,
                $"契约挂载 bt2446a（Tone={pA.Tone} loss={pA.ColorLoss}）");
        }

        // ══════════════════════════════ pq ══════════════════════════════

        /// <summary>pq —— SMPTE ST 2084 EOTF/OETF 锚点值与互逆自检（防符号/常数写错致画面变黑）。</summary>
        private static void ProbePq()
        {
            // ST 2084 公开锚点：编码值 → 亮度(nits)
            (float code, double nits)[] anchors = new[]
            {
                (0.5f, 92.7), (0.5799f, 203.0), (0.7516f, 1000.0), (1.0f, 10000.0),
            };
            foreach (var (code, nits) in anchors)
            {
                double got = SimdPixelOps.PqEotfScalar(code) * SimdPixelOps.PqPeakNits;
                Check(Math.Abs(got - nits) / nits < 0.035, $"EOTF({code}) = {got:F1} nits (期望 {nits})");
            }
            // 互逆：OETF(EOTF(E)) ≈ E，且 EOTF(OETF(L)) ≈ L
            foreach (float l in new[] { 0.0002f, 0.0031f, 0.0203f, 0.1f, 0.5f, 1.0f })
            {
                float e = SimdPixelOps.PqOetfScalar(l);
                float back = SimdPixelOps.PqEotfScalar(e);
                Check(Math.Abs(back - l) < 1e-4f + l * 1e-3f, $"往返 L={l} → E={e:F5} → L'={back:F6}");
            }
            // 黑位基准区：E < 0.18 应给接近 0（不能反向变亮）
            Check(SimdPixelOps.PqEotfScalar(0.0f) == 0f, "EOTF(0) = 0");
            Check(SimdPixelOps.PqEotfScalar(0.1f) < SimdPixelOps.PqEotfScalar(0.5f), "EOTF 单调递增");
            Check(SimdPixelOps.PqEotfScalar(0.5f) > 0.001f, "EOTF(0.5) 非零（旧符号错误会返回 0）");
        }

        // ══════════════════════════════ matrix ══════════════════════════════

        private static void ProbeMatrix()
        {
            const double Tol = 2e-3;   // 色度输入仅 3 位小数 → 与发布值差 ~1e-3

            void Cmp(string name, string from, string to, double[] expected)
            {
                var m = ColorSpaceRegistry.LinearMatrixBetweenPrimaries(from, to);
                if (m == null) { Check(false, $"{name}: 矩阵为 null（未实现该对）"); return; }
                double err = 0;
                for (int i = 0; i < 9; i++) err = Math.Max(err, Math.Abs(m[i] - expected[i]));
                Console.WriteLine($"{name} maxErr={err:E2} m=[{string.Join(",", m.Select(v => v.ToString("0.00000", System.Globalization.CultureInfo.InvariantCulture)))}]");
                Check(err <= Tol, $"{name} 与发布值一致（≤{Tol}）");
            }

            // BT.2020 → BT.709（ITU-R BT.2080 / 业界通用值：1.6605,-0.5877,-0.0728 | -0.1246,1.1330,-0.0084 | -0.0182,-0.1006,1.1187）
            // ⚠️ 曾误用第二个常见写法（第二行 1.422456）作为期望值 → 该错已由“两矩阵互不为逆”自身拦下：
            //    期望常量必须过互逆校验才能入库。
            Cmp("bt2020→bt709", "bt2020", "bt709", new[] {
                1.660491, -0.587641, -0.072850,
               -0.124550,  1.132901, -0.008350,
               -0.018151, -0.100581,  1.118731 });
            // BT.709 → BT.2020（BT.2080 Table：0.627404,0.329282,0.043314 | 0.069097,0.919544,0.011360 | 0.016392,0.088014,0.895600）
            Cmp("bt709→bt2020", "bt709", "bt2020", new[] {
                0.627404, 0.329282, 0.043314,
                0.069097, 0.919544, 0.011360,
                0.016392, 0.088014, 0.895600 });
            // 互逆自洽：正向与反向矩阵必须互为逆（仅抄录外部数值不够，这个检查能拦住“抄错行”）
            var a = ColorSpaceRegistry.LinearMatrixBetweenPrimaries("bt2020", "bt709");
            var b = ColorSpaceRegistry.LinearMatrixBetweenPrimaries("bt709", "bt2020");
            if (a != null && b != null)
            {
                var prod = Mul9(a, b);
                double idErr = 0;
                for (int i = 0; i < 9; i++) idErr = Math.Max(idErr, Math.Abs(prod[i] - (i % 4 == 0 ? 1.0 : 0.0)));
                Console.WriteLine($"bt2020→709→bt2020 与单位阵偏差 max={idErr:E2}");
                Check(idErr <= 1e-4, "双向矩阵互逆（≤1e-4）");
            }
            else Check(false, "双向矩阵互逆：存在 null");
            // Display P3 → sRGB (D65)
            Cmp("smpte432→bt709", "smpte432", "bt709", new[] {
                1.2249401, -0.2249404, 0.0,
               -0.0420569,  1.0420571, 0.0,
               -0.0196376, -0.0786361, 1.0982735 });

            // 同 token / 未知 token → null（调用不得“只改标签”）
            Check(ColorSpaceRegistry.LinearMatrixBetweenPrimaries("bt709", "bt709") == null, "同原色返回 null（无需映射）");
            Check(ColorSpaceRegistry.LinearMatrixBetweenPrimaries("nonsense", "bt709") == null, "未知原色返回 null");
            // 行和为 1（中性守恒）：白→白，保证灰阶不偏色
            var m = ColorSpaceRegistry.LinearMatrixBetweenPrimaries("bt2020", "bt709");
            if (m != null)
            {
                for (int r = 0; r < 3; r++)
                {
                    double s = m[r * 3] + m[r * 3 + 1] + m[r * 3 + 2];
                    Check(Math.Abs(s - 1.0) <= 1e-3, $"bt2020→bt709 第{r}行和≈1（白点守恒）实值={s:0.000000}");
                }
            }
            // 传递函数命名能力（不可精确表达者必须为 null，绝不凑近似）
            Check(ColorSpaceRegistry.ExactTransferName("Adobe RGB") == "bt470m", "Adobe RGB → bt470m(gamma2.2) 可精确表达");
            Check(ColorSpaceRegistry.ExactTransferName("sRGB") == "iec61966-2-1", "sRGB → iec61966-2-1");
            Check(ColorSpaceRegistry.ExactTransferName("Display P3") == "iec61966-2-1", "Display P3 → sRGB 曲线");
            Check(ColorSpaceRegistry.ExactTransferName("BT.2100 PQ") == "smpte2084", "BT.2100 PQ → smpte2084");
            Check(ColorSpaceRegistry.ExactTransferName("ProPhoto RGB") == null, "ProPhoto(1.8) 不可精确表达 → null");
            Check(ColorSpaceRegistry.ExactTransferName("DCI-P3") == null, "DCI-P3(2.6) 不可精确表达 → null");
        }

        /// <summary>行主序 3x3 乘法（仅探针内部用于互逆自洽校验）。</summary>
        ///
        /// <para><b>为什么搬到这里</b>：全仓只有 `ProbeMatrix` 一个调用者（旧宿主 Program.cs:7694）
        /// ⇒ 按模板第 4 条第 3 款「只在**一个** probe 里用的 helper 连同该 probe 一起搬走」，
        /// 不留在 ServiceProbe（那会让本组依赖宿主），也不复制到 Harness（那是给多组共用准备的）。</para>
        private static double[] Mul9(double[] a, double[] b)
        {
            var o = new double[9];
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    o[r * 3 + c] = a[r * 3] * b[c] + a[r * 3 + 1] * b[3 + c] + a[r * 3 + 2] * b[6 + c];
            return o;
        }

        // ══════════════════════════════ colormath ══════════════════════════════

        /// <summary>
        /// colormath —— 把本仓 CICP 原色表推导出的线性矩阵，与**外部参照实现 zimg** 逐元素对撞。
        /// 参照值来源：本机 `C:\PLAN\VQBench\PLAN\ffmpeg-full\ffmpeg.exe`（git-2026-08-06，含 zscale/zimg）
        /// 实测于 2026-09-24：`-f rawvideo -pix_fmt gbrpf32le -video_size 1x1`（喂单位基向量）→
        /// `zscale=pin=&lt;T&gt;:tin=linear:min=0:p=bt709:t=linear:m=0,format=gbrpf32le` → 读回 12 字节浮点。
        /// ⚠ 两点口径：① **必须走 float**，16-bit 出口会把 BT.2020/P3→709 的负值与 &gt;1 削平，
        ///    先前用 8-bit `color=` 源 + `rgb48le` 出口测出的"各 token 都是单位阵"就是那个假象；
        /// ② gbrp 平面序是 **G,B,R**，下面每行已按 (R,G,B) 三列重排。
        /// 控制组：pin=1(bt709)→bt709 必须是精确单位阵；pin=9(bt2020) 必须等于 ITU BT.2080 发布值
        /// （也与本仓 `ProbeMatrix` 的期望常量一致）⇒ 参照本身可信。
        /// </summary>
        private static void ProbeColorMathAgainstZimg()
        {
            // token → 9 项行主序（与 LinearMatrixBetweenPrimaries 同形：out[r] = Σ m[r*3+k]·in[k]）
            //   ⚠ 每个数组都过了一道独立校验：**三行之和必须各为 1**（白→白，与白点无关）——
            //     第一版这里有 4 个 token 因为平面序重排写错，正是这条校验逐个抓出来的。
            var zimg = new (string Tok, double[] M)[]
            {
                ("bt709",     new[] { 1.0, 0.0, 0.0,  0.0, 1.0, 0.0,  0.0, 0.0, 1.0 }),
                ("bt470m",    new[] { 1.48616, -0.40355, -0.08260,  -0.02510, 0.95402, 0.07108,  -0.02722, -0.04410, 1.07132 }),
                ("bt470bg",   new[] { 1.04404, -0.04404, 0.0,  0.0, 1.0, 0.0,  0.0, 0.01179, 0.98821 }),
                ("smpte170m", new[] { 0.93954, 0.05018, 0.01028,  0.01777, 0.96579, 0.01643,  -0.00162, -0.00437, 1.00599 }),
                ("smpte240m", new[] { 0.93954, 0.05018, 0.01028,  0.01777, 0.96579, 0.01643,  -0.00162, -0.00437, 1.00599 }),
                ("film",      new[] { 1.34618, -0.33920, -0.00698,  -0.04735, 1.06605, -0.01870,  -0.02166, -0.06131, 1.08298 }),
                ("bt2020",    new[] { 1.66049, -0.58764, -0.07285,  -0.12455, 1.13290, -0.00835,  -0.01815, -0.10058, 1.11873 }),
                ("smpte428",  new[] { 3.14666, -1.66646, -0.48019,  -0.99552, 1.95576, 0.03976,  0.06359, -0.21456, 1.15097 }),
                ("smpte431",  new[] { 1.15752, -0.15496, -0.00255,  -0.04150, 1.04557, -0.00407,  -0.01805, -0.07858, 1.09663 }),
                ("smpte432",  new[] { 1.22494, -0.22494, 0.0,  -0.04206, 1.04206, 0.0,  -0.01964, -0.07864, 1.09827 }),
                ("jedec-p22", new[] { 1.02525, -0.02655, 0.00130,  0.01939, 0.94803, 0.03258,  -0.00177, -0.00144, 1.00321 }),
            };
            // ── 容差**分档**（P4 收口：原先一个 1e-3 把两种性质完全不同的上限混在一起）──
            //   实测残差（2026-09-26，本 mode 自己打出来的 maxErr，不是估的）：
            //     bt2020 1.14e-6 · bt470bg 3.38e-6 · smpte432 3.95e-6 · smpte170m 4.91e-6
            //     · smpte240m 4.91e-6 · jedec-p22 4.92e-6  ⇒ **A 档**
            //     bt470m 1.76e-4 · smpte431 2.36e-4        ⇒ **B 档**
            //   A 档 = 1e-5：锚 A（zimg）的读数只有 5 位小数 ⇒ 单元素舍入地板 5e-6，
            //     实测最大 4.92e-6 正贴着它 ⇒ 再严就是在抄 zimg 的舍入，而不是在验本仓的数学。
            //   B 档 = 5e-4：这两格的残差**不来自原色抄错**，而来自"白点只给到有限位数"这一事实——
            //     `bt470m` 用 Illuminant C (0.3101,0.3162)、`smpte431` 用影院白 (0.314,0.351)，
            //     而色适应矩阵对白点位数远比原色敏感（残差集中在对角元 1,1 / 反对角 0,2 正是适应项）。
            //     ⇒ 要收紧 B 档，先按权威源取更高精度的白点常数、再用同一锚复测；
            //       **不许**把 5e-4 调到 1e-3 去"吸收"，也不许拿 A 档去判它（那会立刻假红）。
            //   ⚠ 没登记进本表的 token ⇒ 判红并点名"不知道该用哪档"：默认走宽松档就是这条锁漏齿的方式。
            var tolTier = new System.Collections.Generic.Dictionary<string, (double Tol, string Why)>
            {
                ["bt2020"] = (1e-5, "A 档=锚读数舍入地板 5e-6"),
                ["bt470bg"] = (1e-5, "A 档=锚读数舍入地板 5e-6"),
                ["smpte432"] = (1e-5, "A 档=锚读数舍入地板 5e-6"),
                ["smpte170m"] = (1e-5, "A 档=锚读数舍入地板 5e-6"),
                ["smpte240m"] = (1e-5, "A 档=锚读数舍入地板 5e-6"),
                ["jedec-p22"] = (1e-5, "A 档=锚读数舍入地板 5e-6"),
                ["bt470m"] = (5e-4, "B 档=白点位数上限（Illuminant C）"),
                ["smpte431"] = (5e-4, "B 档=白点位数上限（影院白 0.314,0.351）"),
            };

            foreach (var (tok, expect) in zimg)
            {
                var got = ColorSpaceRegistry.LinearMatrixBetweenPrimaries(tok, "bt709");
                if (tok == "bt709")
                {
                    // 同 token 的契约就是 null（=不映射）；把它当"缺矩阵"判红是我第一版的错向断言。
                    Check(got == null, "bt709→bt709 必须 null（同原色 = 不映射，绝不生成单位阵去乘一遍）");
                    continue;
                }
                if (got == null)
                {
                    // null 只有在「本仓**故意**不收录」时才正确，且必须逐个点名理由：
                    //   film     = 未收录（lcms 有原色但残差 2.1e-4，精度不足以填表）
                    //   smpte428 = **xy 参数化表达不了**（H.273 蓝原色 y=0 ⇒ 归一化退化）⇒ 只能不收录
                    // 其余 token 拿到 null 一律红：那说明表被删漏了，而不是"设计如此"。
                    var whyNull = new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["film"] = "未收录（精度不足）",
                        ["smpte428"] = "xy 不可表达 ⇒ 故意不收录，不许挑一组「看着像」的补回来",
                    };
                    Check(whyNull.ContainsKey(tok),
                        $"{tok}→bt709 返回 null ⇒ 只允许出现在「故意不收录」名单里（名单与理由：film=精度不足、smpte428=xy 表达不了）");
                    continue;
                }
                double err = 0; int worst = -1;
                for (int i = 0; i < 9; i++)
                {
                    double d = Math.Abs(got[i] - expect[i]);
                    if (d > err) { err = d; worst = i; }
                }
                Console.WriteLine($"  {tok,-11} maxErr={err:E2} (位置 {worst / 3},{worst % 3}) " +
                    $"本仓=[{string.Join(",", got.Select(v => v.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)))}] " +
                    $"zimg=[{string.Join(",", expect.Select(v => v.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)))}]");
                if (!tolTier.TryGetValue(tok, out var tier))
                {
                    // 新加 token 时**必须**同时登记档位与依据 ⇒ 否则"这条到底该多严"会变成隐含默认值。
                    Check(false, $"{tok}→bt709 未登记容差档位 ⇒ 不判（实测 maxErr={err:E2}；请补 tolTier 与依据）");
                    continue;
                }
                Check(err <= tier.Tol,
                    $"{tok}→bt709 与 zimg 参照逐元素一致（≤{tier.Tol:0.######}，{tier.Why}）实差 {err:E2}");
            }

            // 表内重复项的直接后果：名义上不同的标准被判"同原色"⇒ 跳映射（用户明令禁止的那类静默）
            var dup = new System.Collections.Generic.List<string>();
            for (int i = 0; i < zimg.Length; i++)
                for (int j = i + 1; j < zimg.Length; j++)
                {
                    var a = ColorSpaceRegistry.LinearMatrixBetweenPrimaries(zimg[i].Tok, "bt709");
                    var b = ColorSpaceRegistry.LinearMatrixBetweenPrimaries(zimg[j].Tok, "bt709");
                    if (a == null || b == null) continue;
                    bool same = true;
                    for (int k = 0; k < 9; k++) if (Math.Abs(a[k] - b[k]) > 1e-9) { same = false; break; }
                    if (same) dup.Add($"{zimg[i].Tok}≡{zimg[j].Tok}");
                }
            Console.WriteLine("  本仓把不同标准算成**同一矩阵**的 token 对：" + (dup.Count == 0 ? "无" : string.Join(" ", dup)));
            // 重复**不是自动违法**：zimg 自己也让 smpte170m 与 smpte240m 同原色（差异只在传递函数），
            // 所以这里要的是「白名单之外不得重复」——白名单每一对都必须有外部锚的理由，
            // 加对进白名单等同于改判据，必须写清楚是谁、凭什么加进来的（别用"看着一样"当理由）。
            var allowedDup = new System.Collections.Generic.Dictionary<string, string>
            {
                ["smpte170m≡smpte240m"] = "两锚同判：lcms chrm 与 zimg 矩阵都给出同一组原色，差异只在 trc/matrix",
            };
            var badDup = dup.Where(x => !allowedDup.ContainsKey(x)).ToList();
            Check(dup.Count > 0 && badDup.Count == 0,
                $"同矩阵对全部落在白名单内（实得 {dup.Count} 对，白名单外 {badDup.Count} 对：{string.Join(" ", badDup)}）");
        }

        // ══════════════════════════════ iccname ══════════════════════════════

        /// <summary>
        /// iccname —— ICC 空间识别必须**靠度量**（色度矩阵+曲线），不能被描述文字左右。
        ///
        /// 现场用生产代码生成各标准 ICC，并把描述**强制当成误导值**（“RGB built-in”——那正是
        /// 我们生成的 ICC 的真实描述）再问识别结果：认对 ⇒ 保真/归一/底图原色判断有据可依；
        /// 认错或认不出 ⇒ 这些判断会**静默失效**（上一轮实测就是这个情形）。
        /// </summary>
        private static void ProbeIccName()
        {
            var expect = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sRGB"] = "sRGB", ["Display P3"] = "Display P3",
                ["ProPhoto RGB"] = "ProPhoto RGB", ["BT.2020"] = "Rec.2020",
                // ❗ Adobe RGB / ColorMatch RGB 的**自身原色在 CICP 里没有 token**（iccgen 只能按 token 生成），
                //   而 registry 对它们的 Out* 就是“归一到 BT.2020”⇒ 这条路径生成出的**本来就是 BT.2020 ICC**。
                //   所以这里必须期望 Rec.2020（期望“Adobe RGB”才是错的，不要为了绿而改断言）。
                //   真正携带 Adobe RGB 原色的 ICC 只能由具名条目/纯托管生成器产出（见下面的专项断言）。
                ["Adobe RGB"] = "Rec.2020", ["ColorMatch RGB"] = "Rec.2020",
            };
            int generated = 0;
            foreach (var (spec, want) in expect)
            {
                var s = ColorSpaceRegistry.Resolve(spec);
                if (s == null) { Check(false, $"{spec}：registry 应能解析（现在解不出）"); continue; }
                var icc = IccProfileService.ResolveNamedIcc(spec, null,
                    s.OutPrimaries ?? "bt709", s.OutTrc ?? "iec61966-2-1", s.OutMatrix ?? "bt709");
                if (string.IsNullOrWhiteSpace(icc) || !File.Exists(icc))
                { Console.WriteLine($"  {spec,-14} SKIP（无法生成命名 ICC，不给绿灯）"); continue; }
                generated++;
                var got = FfmpegGui.Services.ColorMapping.ColorSpaceIdentify.IdentifyIccSpaceName(icc, "RGB built-in");
                Console.WriteLine($"  {spec,-14} 生成 ICC tokens={s.OutPrimaries}/{s.OutTrc} → 识别={got ?? "(null)"}");
                Check(string.Equals(got, want, StringComparison.OrdinalIgnoreCase),
                      $"{spec}：描述被误导时仍按度量认出 {want}（实得 {got ?? "null"}）");
                IccProfileService.TryDeleteIcc(icc);
            }
            Check(generated >= 4, $"至少 4 个标准空间可被生成并度量识别（实 {generated}）");
            // 兜底与“绝不猜”：无 ICC 时描述才当线索；两者都无 ⇒ null
            Check(FfmpegGui.Services.ColorMapping.ColorSpaceIdentify.IdentifyIccSpaceName(null, "Display P3") == "Display P3",
                "无 ICC 时仍按描述兜底（保住相机 JPEG 这类只给描述的场景）");
            Check(FfmpegGui.Services.ColorMapping.ColorSpaceIdentify.IdentifyIccSpaceName(null, null) == null,
                "既无 ICC 又无可读描述 ⇒ null（绝不猜成 sRGB）");

            // 专项：具名空间自己的 ICC（纯托管生成，如 ProPhoto 保真 ICC）必须能按色度认回本名，
            // 而不被“内置描述/无关描述”带偏——这才是“保真携带”路径赖以成立的根据。
            {
                var pIcc = FfmpegGui.Services.IccProfileService.GetFaithfulIcc("ProPhoto RGB");
                if (string.IsNullOrWhiteSpace(pIcc) || !File.Exists(pIcc))
                { Console.WriteLine("  ProPhoto 保真 ICC  SKIP（生成不可用，不给绿灯）"); }
                else
                {
                    var g = FfmpegGui.Services.ColorMapping.ColorSpaceIdentify.IdentifyIccSpaceName(pIcc, "Whatever weird name");
                    Console.WriteLine($"  ProPhoto 保真 ICC → 识别={g ?? "(null)"}（描述被故意写成一个无意义名字）");
                    Check(string.Equals(g, "ProPhoto RGB", StringComparison.OrdinalIgnoreCase),
                          $"保真 ICC 能被色度认回 ProPhoto RGB（实得 {g ?? "null"}）");
                }
            }
        }
    }
}
