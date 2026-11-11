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
        // ────────────────────────────── ultrahdr ──────────────────────────────

        /// <summary>
        /// ultrahdr —— 隔离「增益图**解码**是否真的生效」，把三段分开观测：
        /// ① 容器检测 ② 元数据读取 ③ 线性 HDR 解码。
        ///
        /// <para>
        /// 存在的理由：`QueueProcessor` 的 Ultra HDR 输入解码路径（`:481-510`）在 headless 下
        /// **不回显** `item.Log` ⇒ 无法靠日志判定它有没有执行。2026-10-02 的产物对拍发现
        /// 「带增益图 vs 剥掉增益图」产物**逐字节相同**（见
        /// `docs/GAINMAP_VENDOR_ANNOTATION_AUDIT_2026-10-02.md` §4）⇒ 必须有直接观测点把成因定位到某一段。
        /// </para>
        /// </summary>
        private static async Task ProbeUltraHdr(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: ServiceProbe ultrahdr <file>"); _fail++; return; }
            var path = args[1];
            if (!File.Exists(path)) { Console.WriteLine($"FAIL 文件不存在: {path}"); _fail++; return; }

            var ext = Path.GetExtension(path).ToLowerInvariant();
            bool isBmff = ext is ".avif" or ".heic" or ".heif";
            var logs = new List<string>();
            void L(string t) => logs.Add(t.TrimEnd('\n'));

            bool detected = isBmff
                ? await GainMapDecoder.IsIsoBmffGainMapAsync(path, L)
                : await GainMapDecoder.IsUltraHdrManagedAsync(path);
            Console.WriteLine($"[ultrahdr] {Path.GetFileName(path)}  isBmff={isBmff}  detected={detected}");
            foreach (var l in logs) Console.WriteLine($"    log: {l}");
            Check(detected, $"① 容器检测：应判为增益图输入（实 {detected}）");

            logs.Clear();
            // ② 的标签写的是「ISO 21496-1 **或** XMP」⇒ 必须两条承载都问，而不是只问 XMP 那一条：
            //   `ReadMetadataAsync` 只走 exiftool 的 hdrgm XMP；JPEG 的 ISO 21496-1 载荷在
            //   `ParseContainerAsync().IsoMetadata`。2026-10-27 与 origin/main 合流后，单输入出口的
            //   默认承载由兜底 "both" 改成 `GainMapMetadataDefault`（iso）⇒ 文件里**没有 XMP**，
            //   旧写法在这一条必红，而它红的成因是**判据与标签不符**（真解码路径 ③④ 一直是绿的，
            //   产品内部 `GainMapDecoder` 走的是 ISO 优先、XMP 补全的同序）。取数顺序与产品同序，
            //   并点名实际来源，免得"读到了"再说不清是从哪一份承载读到的。
            var xmpMeta = await GainMapDecoder.ReadMetadataAsync(path, L);
            var isoMeta = (await GainMapDecoder.ParseContainerAsync(path))?.IsoMetadata;
            var meta = isoMeta ?? xmpMeta;
            Console.WriteLine(meta == null
                ? "[ultrahdr] metadata = null"
                : $"[ultrahdr] metadata source={(meta == isoMeta ? "ISO-APP2" : "XMP-hdrgm")} "
                  + $"min={meta.GainMapMin} max={meta.GainMapMax} gamma={meta.Gamma} "
                  + $"cap=[{meta.HdrCapacityMin},{meta.HdrCapacityMax}] ch={meta.Channels} useBase={meta.UseBaseColorSpace}");
            foreach (var l in logs) Console.WriteLine($"    log: {l}");
            Check(meta != null, "② 元数据读取：应读出增益元数据（ISO 21496-1 或 XMP）");

            logs.Clear();
            var outRaw = Path.Combine(Path.GetTempPath(), $"uhdr_probe_{Guid.NewGuid():N}.pfm");
            var dec = await GainMapDecoder.DecodeToLinearRawAsync(path, outRaw, L);
            Console.WriteLine(dec == null
                ? "[ultrahdr] decoded = null"
                : $"[ultrahdr] decoded w={dec.Value.w} h={dec.Value.h} peakNits={dec.Value.peakNits:F1} "
                  + $"jxlCs={dec.Value.jxlColorSpace ?? "-"} fileExists={File.Exists(outRaw)}");
            foreach (var l in logs) Console.WriteLine($"    log: {l}");
            Check(dec != null, "③ 线性 HDR 解码：DecodeToLinearRawAsync 应返回非 null");
            if (dec != null)
                Check(dec.Value.peakNits > 203f, $"④ 解码峰值应 > SDR 白点 203（实 {dec.Value.peakNits:F1}）");
            try { if (File.Exists(outRaw)) File.Delete(outRaw); } catch { }
        }

    }
}
