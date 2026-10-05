using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using Tests.Shared;
using FfmpegGui.Services;
// ⚠ 与拆分前 `ServiceProbe/Program.cs` 顶部的 using-alias **逐字一致**：
//   `GF` = `RawGamutFit`（原 Program.cs:21）。拆出后必须自带，
//   否则 `GF.M2020To709` 等引用全部 CS0103（拆分时实测踩过）。
using GF = FfmpegGui.Services.ColorMapping.RawGamutFit;
// ⚠ 2026-10-05 审查补：`engine`(`ProbeEngineAb`) / `simdswitch`(`ProbeSimdSwitch`) 两个 mode
//   当初**漏搬**（组表声明了却没人实现 ⇒ 独立跑 0 条断言；旧宿主是 35/0 与 28/0）
//   ⇒ 现补搬。它们用到原宿主顶部的这些别名，**逐字照抄**（漏写即 CS0103；本次实测踩过）。
using CB = FfmpegGui.Services.ColorMapping.ColorBackend;
using CE = FfmpegGui.Services.ColorMapping.ColorMappingEngine;
using CS = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor;
using ST = FfmpegGui.Models.ColorStrategy;
using OR = FfmpegGui.Services.ColorMapping.OverrideReason;
using AAS = FfmpegGui.Services.AppSettingsService;

namespace Tests.Engine
{
    /// <summary>
    /// 子系统测试组：**engine**（自研引擎与像素内核）—— 2026-10-05 从 ServiceProbe 的单个
    /// 577 KB `Program.cs` 中物理拆出，使"改引擎这一块"只需跑本工程，不再跑整个宿主。
    ///
    /// <para><b>覆盖的 mode</b>：`engine` `gamut` `gamutfit` `simdswitch`（见 `TestGroups`）。
    /// 本文件承载 `gamutfit` —— `RawGamutFit`（`--raw-color-target auto` 的降级判据内核）的纯函数判据。</para>
    ///
    /// <para><b>独立运行</b>：<c>dotnet run --project tests/engine</c>（或直接跑产物 exe）。
    /// 也可由聚合入口 `ServiceProbe gamutfit` 调用（保既有门禁契约零改动）。</para>
    /// </summary>
    internal static class EngineGroup
    {
        /// <summary>
        /// <c>Check</c> 转发到共享 `Harness`。
        /// ⚠ **不是复制实现**：计数与输出格式只有 `Harness` 一份（否则各组会漂移出各自的
        /// PASS/FAIL 口径，本仓已有"两处独立实现漂移"的教训）。保留本地同名包装，
        /// 只为让**拆出的原代码一字不改**（降低拆分引入差异的风险）。
        /// </summary>
        private static void Check(bool ok, string msg) => Harness.Check(ok, msg);
        // ⚠ `ProbeSimdSwitch` 用到（补搬时按 CS0103 逐个补齐；实现仍在 Harness，不复制）。
        private static void Skip(string msg) => Harness.Skip(msg);

        /// <summary>本组成员实现。</summary>
        internal static void RunGamutFit() => ProbeGamutFit();
        // ── 2026-10-05 审查补搬（原本漏搬 ⇒ 组表声明了却 0 条断言）──
        internal static void RunEngineAb() => ProbeEngineAb();
        internal static void RunSimdSwitch() => ProbeSimdSwitch();
        internal static void RunGamut(string[] a) => ProbeGamut(a);
        private static void ProbeGamutFit()
        {
            // ── 1. 常量交叉锁：本内核的 M2020To709 == 产品注册表里同一对矩阵 ──
            //     两处独立维护同一对矩阵 ⇒ 抄错一处端到端测不出来。
            var reg = ColorSpaceRegistry.LinearMatrixBetweenPrimaries("bt2020", "bt709");
            Check(reg != null, "gamutfit: 注册表提供 bt2020→bt709 矩阵");
            if (reg != null)
            {
                double err = 0;
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        err = Math.Max(err, Math.Abs(GF.M2020To709[i, j] - reg[i * 3 + j]));
                Console.WriteLine($"GF.M2020To709 vs ColorSpaceRegistry maxErr={err:E2}");
                Check(err <= 1e-15, "gamutfit: M2020To709 派生自注册表（≤1e-15，非后备常数）");
            }
            // 派生值仍须落在 BT.2080 发布值附近（拦住"注册表整条错"这种情形）
            {
                var pub = new[]
                {
                    1.660491, -0.587641, -0.072850,
                   -0.124550,  1.132901, -0.008350,
                   -0.018151, -0.100581,  1.118731,
                };
                double e = 0;
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        e = Math.Max(e, Math.Abs(GF.M2020To709[i, j] - pub[i * 3 + j]));
                Console.WriteLine($"GF.M2020To709 vs BT.2080 发布值 maxErr={e:E2}");
                Check(e <= 2e-3, "gamutfit: M2020To709 与 BT.2080 发布值一致（≤2e-3）");
            }

            // ── 2. 正反矩阵互逆 ──
            double idErr = 0;
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    double s = 0;
                    for (int k = 0; k < 3; k++) s += GF.M2020To709[i, k] * GF.M709To2020[k, j];
                    idErr = Math.Max(idErr, Math.Abs(s - (i == j ? 1.0 : 0.0)));
                }
            Console.WriteLine($"M2020To709 x M709To2020 与单位阵偏差 max={idErr:E2}");
            Check(idErr <= 1e-12, "gamutfit: 正反矩阵互逆（≤1e-12）");

            // ── 3. 阈值形态（方向必须 fail-closed）──
            Check(GF.FracThreshold > 0 && GF.FracThreshold <= 0.01,
                  $"gamutfit: FracThreshold in (0,1%]（实 {GF.FracThreshold}）");
            Check(GF.NegThreshold < 0, $"gamutfit: NegThreshold < 0（实 {GF.NegThreshold}）");
            Check(GF.FloorFactor > 0 && GF.FloorFactor <= 1,
                  $"gamutfit: FloorFactor in (0,1]（实 {GF.FloorFactor}）");
            // ⚠ 容差必须比真实越界量级小至少 2 个数量级（否则会掩盖真越界）
            Check(GF.EpsNeg > 0 && GF.EpsNeg <= 1e-3,
                  $"gamutfit: EpsNeg in (0,1e-3]（实 {GF.EpsNeg}）");

            // ── 4. 行为：合成数据 ──
            static byte[] Rgb48(int n, Func<int, (int r, int g, int b)> f)
            {
                var a = new byte[n * 6];
                for (int i = 0; i < n; i++)
                {
                    var (r, g, b) = f(i);
                    a[i * 6 + 0] = (byte)(r & 0xFF); a[i * 6 + 1] = (byte)((r >> 8) & 0xFF);
                    a[i * 6 + 2] = (byte)(g & 0xFF); a[i * 6 + 3] = (byte)((g >> 8) & 0xFF);
                    a[i * 6 + 4] = (byte)(b & 0xFF); a[i * 6 + 5] = (byte)((b >> 8) & 0xFF);
                }
                return a;
            }
            const int N = 4096;

            // (a) 中性灰 ⇒ bt709 里仍是灰（无负值）⇒ 判"装得下"
            var gray = GF.MeasureRgb48(Rgb48(N, _ => (30000, 30000, 30000)), N, 1);
            Console.WriteLine($"(a) 中性灰: measurable={gray.Measurable} frac={gray.FracOutOf709:E2} maxNeg={gray.MaxNegExcursion:E2} fits={gray.FitsIn709}");
            Check(gray.Measurable && gray.FitsIn709, "gamutfit(a): 中性灰 ⇒ 装在 bt709 内");

            // (b) 纯 bt2020 红 ⇒ bt709 的 G/B 变负（≈ −0.1246）⇒ 判"超出"
            var red = GF.MeasureRgb48(Rgb48(N, _ => (65535, 0, 0)), N, 1);
            Console.WriteLine($"(b) bt2020 纯红: measurable={red.Measurable} frac={red.FracOutOf709:E2} maxNeg={red.MaxNegExcursion:F4} fits={red.FitsIn709}");
            Check(red.Measurable && !red.FitsIn709, "gamutfit(b): bt2020 纯红 ⇒ 超出 bt709");

            // (c) 全黑 ⇒ 地板以上像素为 0 ⇒ 必须"测不出"（**不得**返回"装得下"）
            var black = GF.MeasureRgb48(Rgb48(N, _ => (0, 0, 0)), N, 1);
            Console.WriteLine($"(c) 全黑: measurable={black.Measurable} reason={black.Reason}");
            Check(!black.Measurable, "gamutfit(c): 全黑 ⇒ 三态「测不出」（不得当成装得下）");
            Check(!black.FitsIn709, "gamutfit(c): 测不出时 FitsIn709 必须为 false（保守）");

            // (d) bt709 纯红（换算成 bt2020 表示）⇒ 恰在边界上 ⇒ 判"装得下"
            //     ⚠ 这条钉的是 EpsNeg：去掉容差 ⇒ 边界色被整片计成越界 ⇒ 本条转红。
            var inRed = GF.MeasureRgb48(Rgb48(N, _ => (41123, 4528, 1075)), N, 1);
            Console.WriteLine($"(d) bt709 纯红(bt2020 表示): measurable={inRed.Measurable} frac={inRed.FracOutOf709:E2} maxNeg={inRed.MaxNegExcursion:F6} fits={inRed.FitsIn709}");
            Check(inRed.Measurable && inRed.FitsIn709, "gamutfit(d): bt709 边界内的红 ⇒ 装在 bt709 内（EpsNeg 生效）");

            // (e) **地板负控**：99% 中性灰 + 1% 极暗的越界红 ⇒ 越界像素全在地板下 ⇒ 仍判"装得下"
            //     ⚠ 变异验证：把 FloorFactor 置 0 ⇒ 该暗红像素被计入 ⇒ 本条必须转红。
            var floorCase = GF.MeasureRgb48(
                Rgb48(N, i => (i % 100 == 0) ? (200, 0, 0) : (30000, 30000, 30000)), N, 1);
            Console.WriteLine($"(e) 灰+暗越界红: measurable={floorCase.Measurable} floor={floorCase.LuminanceFloor:F5} frac={floorCase.FracOutOf709:E2} fits={floorCase.FitsIn709}");
            Check(floorCase.Measurable && floorCase.FitsIn709,
                  "gamutfit(e): 越界像素全在亮度地板以下 ⇒ 判「装得下」（地板负控）");

            // (f) 输入长度不符 ⇒ 必须"测不出"（不得把截断数据当有效样本）
            var bad = GF.MeasureRgb48(new byte[N * 6 - 6], N, 1);
            Check(!bad.Measurable, "gamutfit(f): 长度不符 ⇒ 三态「测不出」");
        }
        private static void ProbeEngineAb()
        {
            var RCP = typeof(FfmpegGui.Services.ColorMapping.RawColorPipeline);
            var ff = FfmpegGui.Services.AppSettingsService.Current.FfmpegPath;
            string dir = "tests/output/validate/engineab";
            Directory.CreateDirectory(dir);

            // A/B 只在“两侧意图相同”时才有意义：legacy 不读 --color-tone-map（它用 --tonemap-curve），
            // 所以 HDR→SDR 的 tonemap 用例**不进本表**（否则比的是“一边映射一边不映射”）。
            var cases = new (string name, string src, string cs)[]
            {
                ("sRGB→P3",  "tests/output/sources/src_8bit.png", "Display P3"),
                ("P3→sRGB",  "tests/output/results/color/a2_srgb_p3.png", "sRGB"),
            };
            foreach (var cse in cases)
            {
                string src = cse.src;
                if (!File.Exists(src)) { Check(false, $"{cse.name} 缺素材 {src}（先跑 generate-sources.ps1 + ensure-fixtures.ps1）"); continue; }
                foreach (var fmt in new[] { "png", "jpg", "webp", "tiff" })
                {
                    var o = new FfmpegGui.Models.FfmpegOptions
                    {
                        Format = fmt, ColorSpace = cse.cs,
                        ColorStrategy = FfmpegGui.Models.ColorStrategy.Recommended, BitDepth = 8,
                    };
                    var intent = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(o, src, out var why);
                    if (intent == null) { Check(false, $"{cse.name}/{fmt} 装配失败：{why}"); continue; }
                    var plan = CE.Plan(intent);
                    if (!plan.IsOk) { Check(false, $"{cse.name}/{fmt} 计划拒绝：{plan.Reason}"); continue; }

                    string ext = fmt == "tiff" ? "tif" : fmt;
                    // 诊断行：把“执行入参”原样打出来（像素错时第一需要知道的是 spec 而不是产物）
                    {
                        var sp0 = plan.ToSpec();
                        string mts = sp0.Matrix == null ? "恒等"
                            : $"[{sp0.Matrix[0]:F4},{sp0.Matrix[1]:F4},{sp0.Matrix[2]:F4}]";
                        Console.WriteLine($"  · {cse.name}/{fmt} spec: src={sp0.SrcCurve.Name} dst={sp0.DstCurve.Name} "
                            + $"scale={sp0.LinearScale:F4} 矩阵={mts} tone={(sp0.Tone?.ToString() ?? "无")} "
                            + $"outBit={plan.OutBitDepth} action={plan.Action} loss={plan.ColorLoss}");
                    }
                    string engOut = $"{dir}/eng_{cse.name}_{fmt}.{ext}";
                    if (File.Exists(engOut)) File.Delete(engOut);
                    try
                    {
                        var st = FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                            src, engOut, plan.ToSpec(), plan, null, default, 2).GetAwaiter().GetResult();
                        Check(File.Exists(engOut) && new FileInfo(engOut).Length > 200,
                            $"{cse.name}/{fmt} 引擎产出 {new FileInfo(engOut).Length}B（{st.Width}x{st.Height}, 变换 {st.ProcessMs}ms）");
                    }
                    catch (Exception ex) { Check(false, $"{cse.name}/{fmt} 引擎异常：{ex.Message}"); continue; }

                    // 与 legacy 对比：同一目标走 ffmpeg 色彩链，逐像素算 PSNR（peak=1.0，两侧同域）
                    if (string.IsNullOrWhiteSpace(ff) || !File.Exists(ff)) { Check(false, $"{cse.name}/{fmt} 无 ffmpeg，A/B 不可运行"); continue; }
                    string legOut = $"{dir}/leg_{cse.name}_{fmt}.{ext}";
                    if (File.Exists(legOut)) File.Delete(legOut);
                    var args = FfmpegGui.Services.FfmpegCommandBuilder.BuildArguments(o, src, legOut);
                    using (var pr = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        ff, args) { RedirectStandardError = true, UseShellExecute = false })!)
                    { pr.StandardError.ReadToEnd(); pr.WaitForExit(120_000); }
                    if (!File.Exists(legOut)) { Check(false, $"{cse.name}/{fmt} legacy 未产出（A/B 无法对比）"); continue; }

                    try
                    {
                        var (w1, h1) = FfmpegGui.Services.ColorMapping.RawColorPipeline.ProbeSizeAsync(engOut).GetAwaiter().GetResult();
                        var (w2, h2) = FfmpegGui.Services.ColorMapping.RawColorPipeline.ProbeSizeAsync(legOut).GetAwaiter().GetResult();
                        if (w1 != w2 || h1 != h2) { Check(false, $"{cse.name}/{fmt} A/B 尺寸不一致 {w1}x{h1} vs {w2}x{h2}"); continue; }
                        var a = FfmpegGui.Services.ColorMapping.RawColorPipeline.DecodeToRgb48Async(engOut, w1, h1).GetAwaiter().GetResult();
                        var b = FfmpegGui.Services.ColorMapping.RawColorPipeline.DecodeToRgb48Async(legOut, w1, h1).GetAwaiter().GetResult();
                        double mse = 0, meanA = 0, meanB = 0; long n = 0;
                        for (int i = 0; i + 1 < a.Length && i + 1 < b.Length; i += 2)
                        {
                            double x = a[i] | (a[i + 1] << 8), y = b[i] | (b[i + 1] << 8);
                            double d = (x - y) / 65535.0;
                            mse += d * d; meanA += x; meanB += y; n++;
                        }
                        mse /= Math.Max(1, n); meanA /= Math.Max(1, n); meanB /= Math.Max(1, n);
                        double psnr = mse > 0 ? 10 * Math.Log10(1.0 / mse) : 99;
                        // 阈值按容器性质分开定：**无损容器**（png/tiff）两路应几乎同值；
                        // **损失编码容器**（jpg/webp）两侧各自做了 YUV 量化/DCT，差异来自编码器而非色彩，
                        // 拿同一个 40dB 去卡它们只会测到“编码器噪声”，反而遮住真正的色彩偏差。
                        // 所以对损失容器只要求“亮度不偏”（均值差）+ 一个宽松下限。
                        bool lossyContainer = fmt is "jpg" or "webp";
                        double thr = lossyContainer ? 20.0 : 40.0;
                        Console.WriteLine($"  {cse.name}/{fmt} 源={plan.Src?.Name} 目标={plan.Dst?.Name} "
                            + $"psnr={psnr:F2}dB engMean={meanA / 65535.0:F4} legMean={meanB / 65535.0:F4}"
                            + (lossyContainer ? "（损失编码容器：两侧量化差异不计入色彩门禁）" : ""));
                        Check(psnr >= thr, $"{cse.name}/{fmt} 引擎 vs legacy PSNR≥{thr}dB（实 {psnr:F2}，{n} 样本）");
                        // 色彩不偏的真正判据（与容器是否损失无关）：两路均值必须在 2% 内
                        Check(System.Math.Abs(meanA - meanB) / 65535.0 < 0.02,
                              $"{cse.name}/{fmt} 引擎与 legacy 平均亮度一致（差 {System.Math.Abs(meanA - meanB) / 65535.0:F4}）");
                        // 亮度不变量：纯色域映射不应把图压成全黑或抛成全白
                        Check(meanA / 65535.0 > 0.02 && meanA / 65535.0 < 0.98,
                            $"{cse.name}/{fmt} 引擎产物亮度合理（engMean={meanA / 65535.0:F4}）");
                    }
                    catch (Exception ex) { Check(false, $"{cse.name}/{fmt} A/B 对比异常：{ex.Message}"); }
                }
            }

            // ═════════════════ #29：`--color-709-curve` 的**端到端**行为锁（真产物字节）═════════════════
            // 与 `contract` 的 C7b 分工：C7b 在**内核**上量口径差（无外部依赖、上下界可推演），
            // 本段把它跑成**真产物**（ffmpeg 解码 → H1 变换 → PNG 编码 → 再解码对拍）——
            // 缺陷当初的表象就是"两条 CLI 命令产物逐字节相同"（2026-09-26 实测四格 × {std,zimg} 全同 md5），
            // 只有真产物层能证明"用户这次拿到的确实是另一个东西"。
            // 格子必须结构上落在 H1：Display P3 ⊄ BT.709 ⇒ 开 GMO ⇒ needsGmo ⇒ 后端与口径无关
            // （若落进 H2，比的就是 zscale 而不是我们的出口曲线，本段就测不到要测的东西 ⇒ 下面先断言前提）。
            {
                string p3Src = "tests/output/results/color/a2_srgb_p3.png";
                if (!File.Exists(p3Src) || string.IsNullOrWhiteSpace(ff) || !File.Exists(ff))
                {
                    Check(false, $"#29 端到端缺前提（素材 {p3Src} 或 ffmpeg）⇒ 本段未验证，不是通过");
                }
                else
                {
                    var o709 = new FfmpegGui.Models.FfmpegOptions
                    {
                        Format = "png", ColorSpace = "BT.709", ColorStrategy = FfmpegGui.Models.ColorStrategy.Recommended,
                        BitDepth = 8, ColorGamutMap = "on",
                        Quality = FfmpegGui.Models.FfmpegOptions.GetDefaultQuality("png"),
                    };
                    var byCurve = new System.Collections.Generic.Dictionary<string, string>();
                    bool premOk = true;
                    foreach (var curve in new[] { "std", "zimg" })
                    {
                        o709.Color709Curve = curve;
                        var it709 = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(o709, p3Src, out var why709);
                        if (it709 == null) { Check(false, $"#29 端到端 {curve} 意图装配失败：{why709}"); premOk = false; break; }
                        var pl709 = CE.Plan(it709);
                        if (!pl709.IsOk) { Check(false, $"#29 端到端 {curve} 计划拒绝：{pl709.Reason}"); premOk = false; break; }
                        if (pl709.Backend != CB.InProcess)
                        { Check(false, $"#29 端到端前提：{curve} 必须落在 H1（实为 {pl709.Backend}）{pl709.Reason}"); premOk = false; break; }
                        string op = $"{dir}/_709_{curve}.png";
                        if (File.Exists(op)) File.Delete(op);
                        try
                        {
                            FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                                p3Src, op, pl709.ToSpec(), pl709, null, default, 2).GetAwaiter().GetResult();
                            byCurve[curve] = op;
                        }
                        catch (Exception ex) { Check(false, $"#29 端到端 {curve} 引擎异常：{ex.Message}"); premOk = false; break; }
                    }
                    if (premOk && byCurve.Count == 2)
                    {
                        var bStd = File.ReadAllBytes(byCurve["std"]);
                        var bZim = File.ReadAllBytes(byCurve["zimg"]);
                        Check(!bStd.AsSpan().SequenceEqual(bZim),
                            $"#29 端到端：std 与 zimg 的真产物必须**逐字节不同**（std {bStd.Length}B / zimg {bZim.Length}B）"
                          + "⇒ 缺陷原貌就是两者同 md5（静默无效参数）");

                        // 差多少：解码回 rgb48（8bit 产物 ⇒ 码值 = 8bit×257），换算成 8bit LSB 再比上下界。
                        var (ws, hs) = FfmpegGui.Services.ColorMapping.RawColorPipeline
                            .ProbeSizeAsync(byCurve["std"]).GetAwaiter().GetResult();
                        var pxStd = FfmpegGui.Services.ColorMapping.RawColorPipeline
                            .DecodeToRgb48Async(byCurve["std"], ws, hs).GetAwaiter().GetResult();
                        var pxZim = FfmpegGui.Services.ColorMapping.RawColorPipeline
                            .DecodeToRgb48Async(byCurve["zimg"], ws, hs).GetAwaiter().GetResult();
                        int maxE2e = 0; long sumE2e = 0; long cntE2e = 0;
                        for (int i = 0; i + 1 < pxStd.Length && i + 1 < pxZim.Length; i += 2)
                        {
                            int d = Math.Abs((pxStd[i] | (pxStd[i + 1] << 8)) - (pxZim[i] | (pxZim[i + 1] << 8))) / 257;
                            if (d > maxE2e) maxE2e = d;
                            sumE2e += d; cntE2e++;
                        }
                        double meanE2e = (double)sumE2e / Math.Max(1, cntE2e);
                        Check(maxE2e >= 24 && maxE2e <= 32 && meanE2e >= 2.0,
                            $"#29 端到端差量级：max={maxE2e} LSB@8bit（界 24–32，理论 27.2）mean={meanE2e:F2}"
                          + $"（{ws}x{hs}，{cntE2e} 样本）⇒ 差的是口径量级，不是压缩/元数据噪声");

                        // 反向：同口径重跑必须逐字节相同（把"差"归因到口径，而不是抖动/RNG/时间戳）
                        string op2 = $"{dir}/_709_std_rerun.png";
                        if (File.Exists(op2)) File.Delete(op2);
                        o709.Color709Curve = "std";
                        var it2 = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(o709, p3Src, out var why2);
                        var pl2 = CE.Plan(it2!);
                        FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                            p3Src, op2, pl2.ToSpec(), pl2, null, default, 2).GetAwaiter().GetResult();
                        Check(File.ReadAllBytes(op2).AsSpan().SequenceEqual(bStd),
                            "#29 端到端反向：std 重跑产物必须逐字节相同（确定性；差异只应来自口径本身）");
                    }
                }
            }
        }

        // ═══════════════════ geometry：引擎几何缩放路径的覆盖 ═══════════════════

        /// <summary>
        /// geometry [输出根目录] —— **只覆盖 <c>RawColorPipeline</c> 的「最长边几何缩放」路径**
        /// （2026-09-17 新增；此前**零覆盖**）。
        ///
        /// <para>
        /// <b>本 mode 存在的唯一理由</b>：<c>QueueProcessor</c> 在**路由之前**就用同一份
        /// <c>FfmpegCommandBuilder.BuildMaxDimensionScaleFilter</c> 把超限图预缩放到临时文件
        /// （<c>QueueProcessor.cs:429-496</c>），并把 <c>captured.InputPath</c> 改写成缩放产物（<c>:487</c>）
        /// ⇒ 引擎拿到时**已不超限** ⇒ <c>RawColorPipeline.cs:94-129</c> 那段几何缩放**永不执行**。
        /// 本探针**直接调用引擎**（不经 <c>QueueProcessor</c>）⇒ 那段代码才真的跑到。
        /// 一旦 legacy 的预缩放执行体被删除，这段就变成**承重路径**，故这里逐条锁住。
        /// </para>
        ///
        /// <para>
        /// 断言分六组（全部打**可观测事实**，不打自由文本）：
        /// <list type="number">
        /// <item><b>产出尺寸</b>：长边锁定 <c>MaxDimension</c> + 另一条边 <c>-2</c> 自动取偶，逐例断言；</item>
        /// <item><b>Stats 反推尺寸 == 实际产物尺寸</b>：<c>RawColorPipeline.cs:120-129</c> 用**解码产物长度**
        ///   反推另一条边（<c>rawvideo</c> 无行填充 ⇒ <c>len == w*h*6</c> 必须精确成立）—— 这正是零覆盖的那段；</item>
        /// <item><b>日志证据</b>：出现 <c>几何缩放（最长边限制）</c> ⇒ 这段代码真的跑了（**唯一**凭据）；
        ///   负控（不超限）必须**不出现**该行；</item>
        /// <item><b>引擎缩放 vs QueueProcessor 预缩放</b>：同滤镜同尺寸实测对照（决定能否安全删掉预缩放）；</item>
        /// <item><b>像素级删预缩放安全</b>：**非恒等**计划（sRGB→Display P3）下，「今天(预缩放→映射)」vs
        ///   「删后(引擎内缩放→映射)」的 PSNR / 最大单通道差。读码结论是两条路径**顺序相同**
        ///   （<c>:92-97</c> 把 scaleFilter 塞进解码 <c>-vf</c> ⇒ 都是 缩放→映射），差别只在今天多一次
        ///   「缩放产物落 PNG → 再解码」的量化往返 ⇒ 就**引擎出口**而言，删预缩放几乎不改像素。
        ///   ⚠ 真正的反序写在 legacy 路径（<c>FfmpegCommandBuilder.cs:121</c> 色彩链 / <c>:535</c> scale
        ///   ⇒ 映射→缩放），但**今天它是休眠的**：预缩放已把输入换成缩放产物 ⇒ <c>:535</c> 的
        ///   <c>BuildMaxDimensionScaleFilter</c> 返回 null ⇒ <c>:535</c> 从不被调用。
        ///   ⇒ **删预缩放会唤醒它**，非引擎通路的顺序随即翻转为 映射→缩放（2×2 实测 36.47dB / 14417）。
        ///   结论：删预缩放**不是**纯引擎侧改动（详见 <c>docs/TESTING.md</c> §6 第 71 条）。</item>
        /// <item><b>顺序统一（R1，2026-09-18 新增）</b>：legacy 的 scale 必须插在**第一条色彩变换之前**
        ///   （<c>FfmpegCommandBuilder.InsertScaleFilterAtChainHead</c>）。本组**读命令行、不读日志**，
        ///   按管线**显式分派载体**（legacy 的裸 <c>-vf</c> / legacy 的 complex 首段 / 引擎的 gif complex），
        ///   每格**先做形态自检**再断位置（§6 第 77 条），并对引擎 gif 那条 <c>[1:v]</c> alpha 分支的
        ///   <c>-2</c> 做**反向断言**（§6 第 74 条：载体选错 ⇒ 断言恒真）。
        ///   ⚠ 实测修正：带 <c>preScale</c> 的形态里，锚点的**前驱是决策层的偶数守卫**
        ///   （<c>scale=ceil(iw/2)*2:ceil(ih/2)*2:flags=lanczos</c>），**不是** <c>format=</c>
        ///   ⇒ 「前驱必须是 <c>format=</c>」这条判据在该形态上会**假红**，故判据放宽为「<c>format=</c> 或偶数守卫」
        ///   并补一条更强的「<c>si</c> 之前无任何色彩变换」。</item>
        /// </list>
        /// </para>
        ///
        /// <para>
        /// <b>边界坑</b>（源码注释已写、此前无任何门禁）：<c>512x384</c> 用<b>纵向</b>滤镜 <c>scale=-2:32</c>
        /// 实测得 <b>42x32</b>，而按 <c>512*32/384 = 42.67</c> 自己算会取到 <b>43 / 44</b>
        /// ⇒ 另一条边**只能**由解码产物长度反推，自己算必与 ffmpeg 漂开。
        /// </para>
        ///
        /// <para>源素材自备（<c>lavfi testsrc2</c>，不依赖别处遗留产物）；输出走 GUID 运行级隔离，结尾按计数自清。</para>
        /// </summary>

        private static void ProbeSimdSwitch()
        {
            const double PsnrTolDb = 1e-9;   // 同一 SSE ⇒ 同一 double 算式 ⇒ 实差应为 0，这里只留一点余量

            bool hasAvx512 = System.Runtime.Intrinsics.X86.Avx512BW.IsSupported;
            bool hasAvx2 = System.Runtime.Intrinsics.X86.Avx2.IsSupported;
            bool hasSse2 = System.Runtime.Intrinsics.X86.Sse2.IsSupported;
            Console.WriteLine($"isa-avx512bw={hasAvx512} isa-avx2={hasAvx2} isa-sse2={hasSse2}");

            // 探针自带的第二份「优先级」读数（测试端重述判据是**应该**的，否则实现抄错无人知）
            string WantKernel(bool allowSimd)
                => !allowSimd ? SimdKernelRouting.PathScalar
                 : hasAvx512 ? SimdKernelRouting.PathAvx512
                 : hasAvx2 ? SimdKernelRouting.PathAvx2
                 : hasSse2 ? SimdKernelRouting.PathSse2
                 : SimdKernelRouting.PathScalar;

            // ── S0：配置 → 选路（**改写任何设置之前**的一次性读数）──
            bool startupFlag = AAS.Current.AutoUseSimdBinaries;
            string startupKernel = SimdKernelRouting.ResolvePixelKernelPath();
            Console.WriteLine($"startup-flag={startupFlag}");
            Console.WriteLine($"startup-kernel={startupKernel}");
            Check(startupFlag || startupKernel == SimdKernelRouting.PathScalar,
                $"S0 配置说「关」（startup-flag={startupFlag}）⇒ 启动选路必须是 scalar（实 {startupKernel}）");
            if (startupFlag && hasSse2)
                Check(startupKernel != SimdKernelRouting.PathScalar,
                    $"S0 配置说「开」且本机有 SSE2 ⇒ 启动选路不得是 scalar（实 {startupKernel}）");
            else Skip($"S0 的「开 ⇒ 非 scalar」这一臂本机判不了（startup-flag={startupFlag} isa-sse2={hasSse2}）");

            // ── S1：开关两态的选路读数（进程内直接改写设置，与面板写的是同一个属性）──
            // ⚠ 哨兵初值不是装饰：try 里的赋值在 C# 的确定性赋值规则下**不算已赋值**，
            //   且万一 try 抛异常（Main 会 catch 成 FAIL），这些值会一路带到断言里 ⇒ 必须**判红**而不是编译不过/静默绿。
            string kernelOn = "?", kernelOff = "?";
            var prior = AAS.Current.AutoUseSimdBinaries;
            try
            {
                AAS.Current.AutoUseSimdBinaries = true;
                kernelOn = SimdKernelRouting.ResolvePixelKernelPath();
                AAS.Current.AutoUseSimdBinaries = false;
                kernelOff = SimdKernelRouting.ResolvePixelKernelPath();
            }
            finally { AAS.Current.AutoUseSimdBinaries = prior; }
            Console.WriteLine($"kernel-on={kernelOn} kernel-off={kernelOff}");
            Check(kernelOn == WantKernel(true),
                $"S1a 开关开 ⇒ 选路 = 本机 ISA 优先级（期望 {WantKernel(true)}，实 {kernelOn}）");
            Check(kernelOff == SimdKernelRouting.PathScalar,
                $"S1b 开关关 ⇒ 选路 = scalar，与本机有无 AVX-512 无关（实 {kernelOff}；isa={hasAvx512}/{hasAvx2}/{hasSse2}）");
            Check(kernelOff != kernelOn || !hasSse2,
                $"S1c 开关必须**真的换掉内核**（开={kernelOn} 关={kernelOff}）⇒ 否则它仍是空开关");

            // 确定性合成：LCG 字节对，差异散布在 ~1/7 + ~1/11 的样本上（±128 与大 ±1 两种幅度）
            static (byte[] a, byte[] b) Synth(int len)
            {
                var a = new byte[len];
                var b = new byte[len];
                uint s = 12345u + (uint)len;
                for (int i = 0; i < len; i++)
                {
                    s = unchecked(s * 1664525u + 1013904223u);
                    a[i] = (byte)(s >> 24);
                }
                for (int i = 0; i < len; i++)
                {
                    b[i] = a[i];
                    if (i % 7 == 3) b[i] = (byte)(a[i] ^ 0x80);
                    else if (i % 11 == 5) b[i] = (byte)(a[i] == 0 ? 1 : a[i] - 1);
                }
                return (a, b);
            }
            // 第三份实现：探针自己写的逐像素标量循环（不经任何选路，作为绝对参照）
            static long ScalarOracle(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
            {
                long acc = 0;
                for (int i = 0; i < x.Length; i++) { int d = x[i] - y[i]; acc += (long)d * d; }
                return acc;
            }
            static int DiffCount(byte[] x, byte[] y)
            {
                int n = 0;
                for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) n++;
                return n;
            }

            var cases8 = new (string name, int len, int channels)[]
            {
                ("8bit-64字节对齐", 64 * 64 * 3, 3),
                ("8bit-带标量尾巴", 41 * 37 * 3, 3),     // 4551：不满一个 32/64 字节向量，逼尾循环
                ("8bit-灰度单通道", 1001, 1),
            };
            foreach (var c in cases8)
            {
                var (a, b) = Synth(c.len);
                int diffCount = DiffCount(a, b);
                Check(diffCount >= 32 && c.len % c.channels == 0,
                    $"S5 {c.name} 输入下限：差异样本 {diffCount} ≥ 32 且长度可被通道数整除"
                  + $"⇒ 否则「全等帧 ⇒ 每条内核都返回 0」会把下面所有等式假绿");

                long sseOn = long.MinValue, sseOff = long.MinValue;
                double psnrOn = double.NaN, psnrOff = double.NaN;
                prior = AAS.Current.AutoUseSimdBinaries;
                try
                {
                    AAS.Current.AutoUseSimdBinaries = true;
                    sseOn = PsnrCalculator.SquaredDiffSum(a, b);
                    psnrOn = PsnrCalculator.CalculatePsnr(a, b, 8, c.channels, true);
                    AAS.Current.AutoUseSimdBinaries = false;
                    sseOff = PsnrCalculator.SquaredDiffSum(a, b);
                    psnrOff = PsnrCalculator.CalculatePsnr(a, b, 8, c.channels, true);
                }
                finally { AAS.Current.AutoUseSimdBinaries = prior; }
                long oracle = ScalarOracle(a, b);
                Console.WriteLine($"  {c.name}: len={c.len} diffs={diffCount} sse(on|off|oracle)={sseOn}|{sseOff}|{oracle} "
                    + $"psnr on={psnrOn:F9} off={psnrOff:F9}");
                Check(sseOff == oracle,
                    $"S3 {c.name} 回退分支**真是逐像素标量**：与探针自带的第三份实现相等（关={sseOff} 参照={oracle}）");
                Check(sseOn == sseOff,
                    $"S2 {c.name} 开关开/关两次 SSE 逐位相等（开={sseOn} 关={sseOff}）⇒ 默认路径数值一个字没动");
                Check(double.IsFinite(psnrOn) && double.IsFinite(psnrOff)
                      && Math.Abs(psnrOn - psnrOff) <= PsnrTolDb,
                    $"S2b {c.name} 开关开/关两次的 PSNR 差 ≤ {PsnrTolDb:g}dB（实 {Math.Abs(psnrOn - psnrOff):E3}）");

                // ── S4 有牙自证：把 b 整体错位一格 ⇒ 必须跳出上面那两条的容差 ──
                var bMis = new byte[c.len];
                for (int i = 0; i < c.len; i++) bMis[i] = b[(i + 1) % c.len];
                long sseMis = ScalarOracle(a, bMis);
                double psnrMis = PsnrCalculator.CalculatePsnr(a, bMis, 8, c.channels, true);
                Check(sseMis != sseOff && Math.Abs(psnrMis - psnrOff) > PsnrTolDb,
                    $"S4 {c.name} 负控：错位一格的标量结果（sse={sseMis} psnr={psnrMis:F6}）必须被 S2/S2b 抓到"
                  + $"（实差 SSE={Math.Abs(sseMis - sseOff)} PSNR={Math.Abs(psnrMis - psnrOff):E3}）"
                  + $"⇒ 否则上面那两条只是「两边同一个错值」，容差没有牙");
            }

            // ── S3b 多帧（真实管线 QualityAnalysisService 用的就是这个入口）──
            {
                var (fa, fb) = Synth(64 * 32 * 3);
                int frame = fa.Length;
                var aa = new byte[frame * 2];
                var bb = new byte[frame * 2];
                Array.Copy(fa, 0, aa, 0, frame);          // 帧 1 与参考全等 ⇒ 该帧 PSNR = inf
                Array.Copy(fa, 0, bb, 0, frame);
                Array.Copy(fa, 0, aa, frame, frame);      // 帧 2 才带差异
                Array.Copy(fb, 0, bb, frame, frame);
                (double Average, double Min, double Max) on = (double.NaN, double.NaN, double.NaN);
                (double Average, double Min, double Max) off = (double.NaN, double.NaN, double.NaN);
                prior = AAS.Current.AutoUseSimdBinaries;
                try
                {
                    AAS.Current.AutoUseSimdBinaries = true;
                    on = PsnrCalculator.CalculateMultiFramePsnr(aa, bb, frame, 8, 3, true);
                    AAS.Current.AutoUseSimdBinaries = false;
                    off = PsnrCalculator.CalculateMultiFramePsnr(aa, bb, frame, 8, 3, true);
                }
                finally { AAS.Current.AutoUseSimdBinaries = prior; }
                Console.WriteLine($"  多帧: on={on.Average:F9}/{on.Min:F9}/{on.Max:F9} off={off.Average:F9}/{off.Min:F9}/{off.Max:F9}");
                Check(double.IsPositiveInfinity(on.Max) && double.IsPositiveInfinity(off.Max),
                    $"S3a 多帧 max=inf（帧 1 全等）在两态都成立（on={on.Max} off={off.Max}）⇒ 回退没把 inf 语义吃掉");
                Check(Math.Abs(on.Average - off.Average) <= PsnrTolDb
                      && Math.Abs(on.Min - off.Min) <= PsnrTolDb,
                    $"S3b 多帧 avg/min 两态差 ≤ {PsnrTolDb:g}dB（实 {Math.Abs(on.Average - off.Average):E3} / {Math.Abs(on.Min - off.Min):E3}）");
            }

            // ── S3c 16-bit 路径：本来就是逐像素标量 ⇒ 开关两态必须同样逐位一致 ──
            {
                var (a16, b16) = Synth(60 * 40 * 3 * 2);
                double pOn = double.NaN, pOff = double.NaN;
                prior = AAS.Current.AutoUseSimdBinaries;
                try
                {
                    AAS.Current.AutoUseSimdBinaries = true;
                    pOn = PsnrCalculator.CalculatePsnr(a16, b16, 16, 3, true);
                    AAS.Current.AutoUseSimdBinaries = false;
                    pOff = PsnrCalculator.CalculatePsnr(a16, b16, 16, 3, true);
                }
                finally { AAS.Current.AutoUseSimdBinaries = prior; }
                Console.WriteLine($"  16bit: on={pOn:F9} off={pOff:F9}");
                Check(double.IsFinite(pOn) && Math.Abs(pOn - pOff) <= PsnrTolDb,
                    $"S3c 16-bit 两态 PSNR 逐位一致（{pOn:F6} vs {pOff:F6}）⇒ 开关没被错接进 16-bit 分支");
            }

            // ── S6：SimdPixelOps 那一路消费者（tooltip 说的是「SIMD 加速的像素处理」，不止 PSNR）──
            {
                int nF = 512;
                var fsrc = new float[nF];
                uint fs = 987654321u;
                for (int i = 0; i < nF; i++)
                {
                    fs = unchecked(fs * 1664525u + 1013904223u);
                    fsrc[i] = i switch
                    {
                        0 => 0f,
                        1 => 1f,
                        2 => 0.0031308f,        // sRGB 编码阈值（分支边界）
                        3 => 0.00313081f,
                        _ => (int)((fs >> 8) & 0xFFFFFF) / (float)0xFFFFFF,
                    };
                }
                var dOn = new byte[nF];
                var dOff = new byte[nF];
                prior = AAS.Current.AutoUseSimdBinaries;
                try
                {
                    AAS.Current.AutoUseSimdBinaries = false;
                    Check(!SimdKernelRouting.FloatToSrgb8UsesSimd,
                        $"S6a 开关关 ⇒ FloatToSrgb8UsesSimd 必须为 false（实 {SimdKernelRouting.FloatToSrgb8UsesSimd}，isa-avx2={hasAvx2}）");
                    SimdPixelOps.FloatToSrgb8(fsrc, dOff);
                    AAS.Current.AutoUseSimdBinaries = true;
                    Check(SimdKernelRouting.FloatToSrgb8UsesSimd == hasAvx2,
                        $"S6b 开关开 ⇒ SIMD 判定 == 本机 Avx2.IsSupported（实 {SimdKernelRouting.FloatToSrgb8UsesSimd} vs {hasAvx2}）");
                    SimdPixelOps.FloatToSrgb8(fsrc, dOn);
                }
                finally { AAS.Current.AutoUseSimdBinaries = prior; }
                int offVsRef = 0, onVsOff = 0, shifted = 0;
                for (int i = 0; i < nF; i++)
                {
                    offVsRef = Math.Max(offVsRef, Math.Abs(dOff[i] - SimdPixelOps.FloatToSrgb8Scalar(fsrc[i])));
                    onVsOff = Math.Max(onVsOff, Math.Abs(dOn[i] - dOff[i]));
                    if (i > 0) shifted = Math.Max(shifted, Math.Abs(dOff[i - 1] - SimdPixelOps.FloatToSrgb8Scalar(fsrc[i])));
                }
                Console.WriteLine($"  float-to-srgb8: offVsScalarRef={offVsRef}LSB onVsOff={onVsOff}LSB shiftedMax={shifted}LSB isa-avx2={hasAvx2}");
                Check(offVsRef == 0, $"S6c 回退路径与标量参考 0 LSB 差（实 {offVsRef}）");
                Check(onVsOff <= 1, $"S6d 两态输出差 ≤ 1 LSB ⇒ 回退不改数值语义（实 {onVsOff}）");
                Check(shifted > 0,
                    $"S6e 负控：错位一格必须被 S6c 那种逐字节判据抓到（实错位差 {shifted} LSB）⇒ 否则 0 LSB 那条是空判据");
                if (hasAvx2 && onVsOff == 0)
                    Console.WriteLine("INFO S6 的数值臂本机不可判别（AVX2 的 LUT 近似恰好与标量逐字节相同）"
                        + "⇒ 该臂由 S6a/S6b 的选路读数 + 编排脚本的结构锁承担（不判红、也不假装验过）");
            }
        }

        /// <summary>
        /// peakmeasure —— 峰值链路最后一档：无用户值、无元数据时**整帧实测内容峰值**。
        ///
        /// 为什么不算“自己验自己”：期望 nits 由**素材生成时写入的最高码值**经 PQ 曲线定义换算
        /// （PQ EOTF 本身另有 curve 门禁的锚点），与实测路径无关。另外必须钉住：
        ///  • 用户显式给了峰值 ⇒ 绝不再去实测（不拿猜值覆盖用户值）；
        ///  • 实测低于参考白 ⇒ 钳到参考白，**不因图暗而增亮**；
        ///  • 执行层只改副本，Plan.Tone 不被污染；
        ///  • 表路径与解析式路径同值；
        ///  • 用户可见日志写明“峰值来源=整帧实测”。
        /// </summary>

        private static void ProbeGamut(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: ServiceProbe gamut <jpeg> [expectedToken]"); Harness.FailOnly(); return; }
            var got = GainMapDecoder.DetectBasePrimaries(args[1]);
            Console.WriteLine($"base_primaries={got ?? "(null→sRGB惯例)"}");
            Check(got != null, "底图 ICC 可读出原色（已附 ICC）");
            if (args.Length >= 3)
                Check(string.Equals(got, args[2], StringComparison.OrdinalIgnoreCase), $"底图原色 == {args[2]}（实际 {got ?? "null"}）");
        }

        /// <summary>
        /// 线性域 primaries 矩阵的**系数级**断言：与公开发布值逐元素比对。
        /// 参考：ITU-R BT.2080（BT.709⇄BT.2020）、Display P3→sRGB（AAP/ASC 已发布矩阵）。
        /// 目的：保证“真的做了映射”而不是碰巧相近（漏映射/错转置会相差 ≥0.05）。
        /// </summary>
        /// <summary>
        /// `RawGamutFit` 的**纯函数级**判据（合成数据；不依赖素材、不依赖 ffmpeg）。
        /// <para>
        /// 存在理由：`--raw-color-target auto` 的降级判据若只靠端到端覆盖，则
        /// ① 常量矩阵抄错、② 阈值方向反了、③ **「测不出」被当成「装得下」**（三态失效）、
        /// ④ **亮度地板失效导致暗部噪声参与判定** 这四类缺陷都测不出来。本 mode 逐条钉死。
        /// </para>
        /// </summary>
    }
}
