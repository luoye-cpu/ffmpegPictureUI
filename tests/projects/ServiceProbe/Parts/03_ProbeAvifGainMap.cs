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
        /// `avifgm` —— AVIF 增益图**写出侧** `colr.full_range` 的继承锁（2026-10-03 补）。
        /// 动机：`IsoBmffGainMapWriter.PatchColr` 曾把底图 colr 的 `full_range` 位**硬写成 full(0x80)**，
        /// 而 ffmpeg(libaom-av1) 对 yuv420p 实产的是 **tv(0x00)** ⇒ 每条 AVIF 增益图产物的主图都被贴错
        /// range（Y∈[16,235] 被按 [0,255] 解读 ⇒ 黑阶抬起、对比度压缩）。仓库内**此前没有任何判据覆盖
        /// 这条写出路径**（`gainmap-isobmff` 只测**解码**侧、`gainmap` 测 **JPEG** Ultra HDR 容器）
        /// ⇒ 该缺陷长期静默。权威口径：libultrahdr `avifultrahdr.cpp:435` 取**实际图像**的 range（非硬编码）。
        /// 三段：① 单元级（反射直调 `PatchColr` ⇒ 只读继承、双向）
        /// ② `Build` 的 `baseFullRangeFallback` 默认值必须 `false`（limited/tv）
        /// ③ 端到端（真 ffmpeg 编 tv-range 底图 + 产品 `EncodeAsync(container:"avif")` ⇒ 产物底图 colr 必须 0x00）
        /// </summary>
        private static async Task ProbeAvifGainMap()
        {
            // ── L-4 F-4 的兑现锁：三态不许塌成两态（"不知道"≠"没有"）──
            {
                byte[] BoxL4(string type, byte[] payload)
                {
                    var b = new byte[8 + payload.Length];
                    int sz = b.Length;
                    b[0] = (byte)(sz >> 24); b[1] = (byte)(sz >> 16); b[2] = (byte)(sz >> 8); b[3] = (byte)sz;
                    System.Text.Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
                    payload.CopyTo(b, 8);
                    return b;
                }
                var ftypL4 = BoxL4("ftyp", new byte[] { (byte)'a', (byte)'v', (byte)'i', (byte)'f', 0, 0, 0, 0,
                                                        (byte)'a', (byte)'v', (byte)'i', (byte)'f' });
                byte[] LieChild(string type, int lieSize)
                {
                    var b = new byte[12];
                    b[0] = (byte)(lieSize >> 24); b[1] = (byte)(lieSize >> 16); b[2] = (byte)(lieSize >> 8); b[3] = (byte)lieSize;
                    System.Text.Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
                    return b;
                }

                // ① 干净件：真没有 meta ⇒ 允许"确定·无增益图"
                var rL4a = FfmpegGui.Services.IsoBmffGainMapProbe.Probe(ftypL4);
                Check(rL4a.Determinate && !rL4a.HasGainMap,
                      $"L-4 只有 ftyp 的干净件必须判「确定·无」（实 det={rL4a.Determinate} has={rL4a.HasGainMap} reason={rL4a.FailReason ?? "-"}）");

                // ② 顶层 box 链截断（下一盒谎报 4000B）⇒ 必须"不知道 + 具名原因"
                var truncTop = new byte[ftypL4.Length + 12];
                ftypL4.CopyTo(truncTop, 0);
                LieChild("meta", 4000).CopyTo(truncTop, ftypL4.Length);
                var rL4b = FfmpegGui.Services.IsoBmffGainMapProbe.Probe(truncTop);
                Check(!rL4b.Determinate && !string.IsNullOrWhiteSpace(rL4b.FailReason),
                      $"L-4 顶层截断必须报「不知道+原因」，绝不返回「确定无」（实 det={rL4b.Determinate} has={rL4b.HasGainMap} reason={rL4b.FailReason ?? "-"}）");

                // ③ meta 在、但其子 box 链断 ⇒ 同样不许冒充"确定无"
                var metaBody = new byte[4 + 12];
                LieChild("iloc", 3000).CopyTo(metaBody, 4);
                var withMeta = new byte[ftypL4.Length + BoxL4("meta", metaBody).Length];
                ftypL4.CopyTo(withMeta, 0);
                BoxL4("meta", metaBody).CopyTo(withMeta, ftypL4.Length);
                var rL4c = FfmpegGui.Services.IsoBmffGainMapProbe.Probe(withMeta);
                Check(!rL4c.Determinate && (rL4c.FailReason ?? "").Contains("meta"),
                      $"L-4 meta 子链断裂必须「不知道」且原因点名 meta（实 det={rL4c.Determinate} reason={rL4c.FailReason ?? "-"}）");
            }
            // ── ①② 单元级：确定性，不需要 ffmpeg ──
            var t = typeof(GainMapEncoder).Assembly
                .GetType("FfmpegGui.Services.IsoBmffGainMapWriter", throwOnError: false);
            Check(t != null, "avifgm：找得到 internal IsoBmffGainMapWriter（否则本 mode 的锁全部真空绿）");
            if (t == null) return;
            var pm = t.GetMethod("PatchColr",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Check(pm != null, "avifgm：找得到 private static PatchColr");
            if (pm == null) return;
            bool Invoke(List<byte[]> props, bool fallback)
                => (bool)pm.Invoke(null, new object[] { props, 1, 13, 1, fallback })!;

            var p1 = new List<byte[]> { NclxColrBox(0x00) };
            var r1 = Invoke(p1, true);
            Check(!r1 && (p1[0][^1] & 0x80) == 0,
                  $"avifgm ①底图 colr=tv(0x00) + 兜底 true ⇒ **必须只读继承 0x00**（旧代码在此写 0x80 = 本缺陷）｜实 0x{p1[0][^1]:X2}");

            var p2 = new List<byte[]> { NclxColrBox(0x80) };
            var r2 = Invoke(p2, false);
            Check(r2 && (p2[0][^1] & 0x80) != 0,
                  $"avifgm ①继承是**双向**的：底图 colr=full(0x80) + 兜底 false ⇒ 仍 0x80（不是一律写 tv）｜实 0x{p2[0][^1]:X2}");

            var p3 = new List<byte[]>();
            var r3 = Invoke(p3, false);
            Check(!r3 && p3.Count == 1 && (p3[0][^1] & 0x80) == 0,
                  $"avifgm ①底图**无** colr ⇒ 用兜底值；兜底 false ⇒ 补 0x00｜实 0x{(p3.Count == 1 ? p3[0][^1] : 0xFF):X2}");

            var bp = t.GetMethod("Build",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?
                .GetParameters().FirstOrDefault(x => x.Name == "baseFullRangeFallback");
            Check(bp != null && bp.HasDefaultValue && bp.DefaultValue is bool dv && !dv,
                  "avifgm ②`Build` 的 baseFullRangeFallback **默认值必须是 false**（limited/tv；旧代码 true ⇒ 每次组装都把 ffmpeg 的 tv 改口成 full）"
                  + $"｜实 {bp?.DefaultValue?.ToString() ?? "(无此参数)"}");

            // ── ③ 端到端：真 ffmpeg + 产品写出器 ──
            var ff = AAS.Current.FfmpegPath;
            if (string.IsNullOrEmpty(ff) || !File.Exists(ff)) { Skip("avifgm ③：ffmpeg 未定位（① ② 已跑，本条不产出读数）"); return; }
            var work = Path.Combine(FfmpegGui.Services.PlatformServices.GetTempDir(), "avifgm_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(work);
            try
            {
                string png = Path.Combine(work, "base.png"), rawAvif = Path.Combine(work, "raw_base.avif");
                RunCap(ff, $"-y -hide_banner -loglevel error -f lavfi -i testsrc2=s=64x48 -frames:v 1 \"{png}\"");
                // 与产品 `EncodeAvifFileAsync` 同口径：yuv420p + 三个 CICP 标志、**不传** -color_range（默认 tv）
                RunCap(ff, $"-y -hide_banner -loglevel error -i \"{png}\" -frames:v 1 -c:v libaom-av1 -crf 18 -b:v 0 "
                         + $"-pix_fmt yuv420p -color_primaries bt709 -color_trc iec61966-2-1 -colorspace bt709 -f avif \"{rawAvif}\"");
                var rawN = File.Exists(rawAvif) ? FindNclx(File.ReadAllBytes(rawAvif)) : new List<(int Off, int Prim, int Trc, int Mtx, byte Fr)>();
                Check(rawN.Count >= 1 && rawN.All(x => (x.Fr & 0x80) == 0),
                      $"avifgm ③前提：ffmpeg 裸底图 colr.full_range 全为 tv(0x00)（实 {rawN.Count} 个 nclx）");

                int w = 64, h = 48;
                var px = new float[w * h * 4];
                for (int i = 0; i < w * h; i++)
                {
                    float u = (i % w) / (float)(w - 1);
                    px[i * 4 + 0] = 0.2f + 3.0f * u;   // 线性峰值 > 1 ⇒ HDR 高光 ⇒ 走真增益图分支
                    px[i * 4 + 1] = 0.5f + 1.0f * u;
                    px[i * 4 + 2] = 0.1f + 2.0f * u;
                    px[i * 4 + 3] = 1f;
                }
                string outAvif = Path.Combine(work, "gm_out.avif");
                var ok = await GainMapEncoder.EncodeAsync(px, w, h, outAvif,
                    hdrPeakNits: 1000f, sdrWhiteNits: 203f, multiChannel: false,
                    baseQuality: 1.5f, gainMapQuality: 1.5f, downsample: 4,
                    log: null, container: "avif");
                Check(ok && File.Exists(outAvif) && new FileInfo(outAvif).Length > 500,
                      $"avifgm ③产品 AVIF 增益图写出成功（实 {(File.Exists(outAvif) ? new FileInfo(outAvif).Length : 0)} B）");
                if (!File.Exists(outAvif)) return;
                var n = FindNclx(File.ReadAllBytes(outAvif));
                Check(n.Count >= 3, $"avifgm ③产物含 3 个 nclx colr（底图/增益图/tmap）｜实 {n.Count}");
                var bc = n.Where(x => x.Prim == 1 && x.Trc == 13 && x.Mtx == 1).ToList();
                var tc = n.Where(x => x.Trc == 16).ToList();
                Check(bc.Count == 1 && (bc[0].Fr & 0x80) == 0,
                      "avifgm ③**核心**：产物底图 colr.full_range = 0x00（继承 ffmpeg 实产；旧代码写 0x80）"
                      + $"｜实 0x{(bc.Count == 1 ? bc[0].Fr : 0xFF):X2}");
                Check(tc.Count == 1 && (tc[0].Fr & 0x80) == 0,
                      $"avifgm ③tmap 替代图 colr 与底图同 range（同一份像素不可能两说）｜实 0x{(tc.Count == 1 ? tc[0].Fr : 0xFF):X2}");
            }
            finally { try { Directory.Delete(work, true); } catch { } }
        }

    }
}
