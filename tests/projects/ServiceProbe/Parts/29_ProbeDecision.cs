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
        /// decision —— 步骤 3（单一裁决点）的验收探针：把 <c>EffectiveOutputColorTokens</c> 当作“宣称值”，
        /// 与 <c>BuildArguments</c> **实际发出的命令行**逐项对（取 -i 之后的输出侧标签）。
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
                        // 第六种合法形式（F-9 拆式，2026-10-11）：出口 trc 由链末 `setparams` 落帧标签，
                        //   而 zscale 刻意只留 `t=linear`（zimg 的 `t=bt709` 实为纯幂 γ(1/2.4)，不能当定义式）。
                        //   ⇒ 对账必须读"真正决定帧标签的那一级"，否则拆式会被误读成"宣称未落实"。
                        //   ⚠ 这不是放宽：宣称值仍须逐格在命令行中现身，只是换了取数阶段。
                        var setBlk = Last(@"setparams=([^""\s]+)", outSide);
                        string setP = System.Text.RegularExpressions.Regex.Match(setBlk, @"(?:^|:)color_primaries=([^:""]+)").Groups[1].Value;
                        string setT = System.Text.RegularExpressions.Regex.Match(setBlk, @"(?:^|:)color_trc=([^:""]+)").Groups[1].Value;
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
                                                   || (setP.Length > 0 && string.Equals(claimP, setP, StringComparison.OrdinalIgnoreCase))
                                                   || (passthrough && string.Equals(claimP, inP, StringComparison.OrdinalIgnoreCase))
                                                   || implicitSrgb;
                        bool tLanded = claimT == null || string.Equals(claimT, gotT, StringComparison.OrdinalIgnoreCase)
                                                   || string.Equals(claimT, zsT, StringComparison.OrdinalIgnoreCase)
                                                   || (setT.Length > 0 && string.Equals(claimT, setT, StringComparison.OrdinalIgnoreCase))
                                                   || (passthrough && string.Equals(claimT, inT, StringComparison.OrdinalIgnoreCase))
                                                   || implicitSrgb;
                        if (!pLanded || !tLanded)
                        {
                            mismatch++;
                            // 自带命令行摘录：不一致的行必须能直接看出哪一段与宣称对不上，
                            // 不留给下轮去猜（本仓库多次因“只看汇总行”错过真错）。
                            string excerpt = outSide.Length > 170 ? outSide[..170] + "…" : outSide;
                            Console.WriteLine($"  ≠ {sn}/{fmt}/{tg} 宣称={(claimP ?? "—")}/{(claimT ?? "—")}  "
                                + $"输出侧={Has(gotP)}/{Has(gotT)}  zscale={Has(zsP)}/{Has(zsT)}  setparams={Has(setP)}/{Has(setT)}  输入侧={Has(inP)}/{Has(inT)} 直通={passthrough} 源sRGB={srcSrgb}");
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

    }
}
