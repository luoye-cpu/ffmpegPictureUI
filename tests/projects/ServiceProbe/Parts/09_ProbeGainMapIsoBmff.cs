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
        /// ISO-BMFF（AVIF/HEIC）增益图探测。用法：
        /// <c>ServiceProbe gainmap-isobmff &lt;file&gt; [outGainMapRaw] [gainmap|none|auxapple|auxiso|auxisofull|grid|auxdec]</c>
        /// <para>
        /// 第 4 个位置参数声明**期望**：<c>gainmap</c>（默认）/ <c>none</c>
        /// （负控：普通 AVIF/HEIC 必须判「无增益图」，用来证明判据**有牙**而不是恒真）/
        /// <c>auxapple</c>（Apple 早期 `auxC` 辅助图的**极简/残缺件**：认出但结构不全 ⇒ fail-closed 点名）/
        /// <c>auxiso</c>（ISO 21496-1 辅助图**未证实态**：fail-closed——极简夹具（无 ipma/iloc 定位不到
        /// aux item）与 AU2-X1 的 D-bad 负控件（version 段破坏）共用此契约）/
        /// <c>auxisofull</c>（AU2-X1 2026-11-10：aux-21496 防御性形态**结构尝试成立**的完整件——
        /// 前置 `version+GainMapMetadata` 就位 + 编码流可切 ⇒ Determinate=true）/
        /// <c>grid</c>（grid 分块 + idat 承载）/ <c>auxdec</c>（AU-W2 2026-11-10：Apple aux
        /// **完整件**——探针必须完成容器级提取：aux item 裸流 + Exif item 字节 ⇒ Determinate=true）。
        /// </para>
        /// </summary>
        private static void ProbeGainMapIsoBmff(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: ServiceProbe gainmap-isobmff <file> [outRaw] [gainmap|none|auxapple|auxiso|auxisofull|grid|auxdec]"); _fail++; return; }
            var path = args[1];
            // ⚠ 位置参数**与开关混排**：`none` / `gainmap` / `auxapple` 是开关（可出现在任意位置），
            //   其余第一个非开关参数才是 outRaw —— 旧写法写死 args[3] ⇒ `gainmap-isobmff <f> none`
            //   会被当成 outRaw 并静默退回默认期望，负控**整段失效**（实测已踩）。
            var expect = "gainmap";
            string? outRaw = null;
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i].Equals("none", StringComparison.OrdinalIgnoreCase)) expect = "none";
                else if (args[i].Equals("gainmap", StringComparison.OrdinalIgnoreCase)) expect = "gainmap";
                else if (args[i].Equals("auxapple", StringComparison.OrdinalIgnoreCase)) expect = "auxapple";
                else if (args[i].Equals("auxiso", StringComparison.OrdinalIgnoreCase)) expect = "auxiso";
                else if (args[i].Equals("auxisofull", StringComparison.OrdinalIgnoreCase)) expect = "auxisofull";
                else if (args[i].Equals("grid", StringComparison.OrdinalIgnoreCase)) expect = "grid";
                else if (args[i].Equals("auxdec", StringComparison.OrdinalIgnoreCase)) expect = "auxdec";
                else outRaw = args[i];
            }
            var bytes = File.ReadAllBytes(path);
            Console.WriteLine($"file={path} size={bytes.Length} expect={expect}");

            var r = IsoBmffGainMapProbe.Probe(bytes);
            Console.WriteLine($"isobmff={r.IsIsoBmff} brands={r.Brands}");
            Console.WriteLine($"determinate={r.Determinate} failReason={r.FailReason ?? "-"}");
            Console.WriteLine($"hasGainMap={r.HasGainMap} form={r.Form ?? "-"} primary={r.PrimaryItemId} " +
                $"tmap={r.TmapItemId} gainmapItem={r.GainMapItemId}");
            Console.WriteLine($"codec={r.GainMapCodec ?? "-"} ffmpegFmt={r.GainMapFfmpegFormat ?? "-"} " +
                $"tmapPayload={r.TmapPayload?.Length ?? -1} gmData={r.GainMapItemData?.Length ?? -1}");
            Console.WriteLine($"grid={r.GainMapIsGrid} rows={r.GridRows} cols={r.GridCols} " +
                $"gridW={r.GridWidth} gridH={r.GridHeight} tileW={r.TileWidth} tileH={r.TileHeight} tiles={r.GainMapTiles?.Count ?? 0}");
            // D1/D2/D5/D8（2026-11-09）：主图身份 / 展示变换 / alpha 辅助图 / 截断 —— 修复判据的读数面。
            Console.WriteLine($"primaryType={r.PrimaryItemType ?? "-"} primaryIsGrid={r.PrimaryIsGridDerived} " +
                $"irot={r.PrimaryIrot} imir={r.PrimaryImir} auxAlpha={r.HasAuxAlpha} truncated={r.IsTruncated}" +
                (r.TruncationDetail == null ? "" : $" detail={r.TruncationDetail}"));

            Check(r.IsIsoBmff, "识别为 ISO-BMFF（ftyp 存在）");
            if (expect == "auxapple")
            {
                // 契约：认出来并点名（不得退化成「无增益图」），但**尚未支持解码** ⇒ 不是确定态、没有字节。
                Check(!r.Determinate, $"未支持的形式必须 determinate=false（实 {r.Determinate}）");
                Check(r.HasGainMap, "检测到 Apple 早期辅助图增益图（hasGainMap=true）");
                Check(r.Form == "aux-apple-hdrgainmap", $"承载形式为 aux-apple-hdrgainmap（实 {r.Form ?? "-"}）");
                Check((r.FailReason ?? "").Contains("hdrgainmap"), $"failReason 点名 hdrgainmap（实 {r.FailReason ?? "-"}）");
                Check(r.GainMapItemData == null, "未支持解码 ⇒ 不得给出可解字节（gmData 必须为空）");
                return;
            }
            if (expect == "auxiso")
            {
                Check(!r.Determinate, $"未支持的形式必须 determinate=false（实 {r.Determinate}）");
                Check(r.HasGainMap, "检测到 ISO 21496-1 辅助图增益图（hasGainMap=true）");
                Check(r.Form == "aux-21496", $"承载形式为 aux-21496（实 {r.Form ?? "-"}）");
                Check(r.GainMapItemData == null, "未支持解码 ⇒ 不得给出可解字节（gmData 必须为空）");
                return;
            }
            if (expect == "auxisofull")
            {
                // AU2-X1（2026-11-10）：aux-21496 防御性形态**结构尝试成立**的完整件断言
                // （合成件 D = tests/scripts/_make-synth-aux-gainmap.py：auxC URN=21496 +
                // item 数据前置 version(0) + GainMapMetadata(62 B) + HEVC 流）。
                // 负控（version 段破坏 ⇒ fail-closed）走 expect=auxiso（同款四条契约，
                // 门禁 §1i 另以 FailReason 文本点名「结构未证实」锁牙）。
                Check(r.Determinate, $"aux-21496 结构尝试成立 ⇒ determinate=true（failReason={r.FailReason ?? "-"}）");
                Check(r.HasGainMap, "检测到增益图");
                Check(r.Form == "aux-21496", $"承载形式为 aux-21496（实 {r.Form ?? "-"}）");
                Check(r.GainMapItemId > 0, $"aux item（增益图 item）已定位（实 {r.GainMapItemId}）");
                Check(r.TmapPayload is { Length: >= 62 } && r.TmapPayload[0] == 0,
                    $"前置 `version+元数据` 段已就位且 version=0（实 {r.TmapPayload?.Length ?? -1} B，与 tmap 载荷同构）");
                Check(r.GainMapFfmpegFormat != null, $"aux 裸流解复用器已知（实 {r.GainMapFfmpegFormat ?? "-"}）");
                Check(r.GainMapItemData is { Length: > 100 }, $"元数据之后的增益图编码流已切出（实 {r.GainMapItemData?.Length ?? -1} B）");
                if (r.GainMapItemData != null && outRaw != null)
                {
                    File.WriteAllBytes(outRaw, r.GainMapItemData);
                    Console.WriteLine($"gainmap_raw_written={outRaw} bytes={r.GainMapItemData.Length}");
                }
                return;
            }
            if (expect == "auxdec")
            {
                // AU-W2（2026-11-10）：Apple aux **完整件**的容器级提取断言（合成件由
                // tests/scripts/_make-synth-aux-gainmap.py 生成：primary(hvc1) + aux(hvc1 4:0:0 灰度)
                // + Exif(AppleMakerNote 33/48) + iref auxl/cdsc）。
                // 元数据字段值（HDRHeadroom/HDRGain）不在本 mode 断言 —— 它由 exiftool 通道在
                // GainMapDecoder 层读取，门禁 §1h 另设 exiftool 读数臂。
                Check(r.Determinate, $"Apple aux 完整提取 ⇒ determinate=true（failReason={r.FailReason ?? "-"}）");
                Check(r.HasGainMap, "检测到增益图");
                Check(r.Form == "aux-apple-hdrgainmap", $"承载形式为 aux-apple-hdrgainmap（实 {r.Form ?? "-"}）");
                Check(r.GainMapItemId > 0, $"aux item（增益图 item）已定位（实 {r.GainMapItemId}）");
                Check(r.GainMapFfmpegFormat != null, $"aux 裸流解复用器已知（实 {r.GainMapFfmpegFormat ?? "-"}）");
                Check(r.GainMapItemData is { Length: > 100 }, $"aux item 裸流已切出（实 {r.GainMapItemData?.Length ?? -1} B）");
                Check(r.ExifItemId > 0 && r.ExifItemData is { Length: > 0 },
                    $"主图 Exif item 已切出（AppleMakerNote 载体；实 item {r.ExifItemId}，{r.ExifItemData?.Length ?? -1} B）");
                if (r.GainMapItemData != null && outRaw != null)
                {
                    File.WriteAllBytes(outRaw, r.GainMapItemData);
                    Console.WriteLine($"gainmap_raw_written={outRaw} bytes={r.GainMapItemData.Length}");
                }
                return;
            }
            if (expect == "grid")
            {
                // 2026-11-02：grid 分块承载（`dimg tmap -> [底图, grid]`、`dimg grid -> 瓦片…`），
                // 且 tmap / grid 载荷在 idat（construction_method=1）——两个结构盲区的联合判据。
                // ⚠ 这里只钉**结构形态**（通用）；具体数值（128x96 / 64x48 / 4 块）由门禁对
                //   合成件 `tests/data/synth_heic_grid_gainmap.heic` 的打印行断言 —— 真实 iPhone 件
                //   是 1536x2048 / 1024x1024 / 4 块，数值不同但形态相同。
                //   （IsIsoBmff 已由本方法开头统一断言，此处不重复。）
                Check(r.Determinate, $"解析确定（failReason={r.FailReason ?? "-"}）");
                Check(r.HasGainMap, "检测到增益图");
                Check(r.Form == "tmap", $"承载形式为 tmap（实 {r.Form ?? "-"}）");
                Check(r.GainMapIsGrid, "增益图承载为 grid 分块");
                Check((r.GainMapTiles?.Count ?? 0) > 0, $"grid 瓦片数 > 0（实 {r.GainMapTiles?.Count ?? 0}）");
                Check(r.TmapPayload != null && r.TmapPayload.Length > 0,
                    $"tmap 载荷已切出（construction_method=1 / idat；实 {r.TmapPayload?.Length ?? -1} B）");
                Check(r.GridWidth > 0 && r.GridHeight > 0 && r.TileWidth > 0 && r.TileHeight > 0,
                    $"grid 声明与瓦片尺寸 > 0（实 {r.GridWidth}x{r.GridHeight} / {r.TileWidth}x{r.TileHeight}）");
                Check(r.GainMapFfmpegFormat != null, $"瓦片裸流解复用器已知（实 {r.GainMapFfmpegFormat ?? "-"}）");
                return;
            }
            Check(r.Determinate, $"解析确定（failReason={r.FailReason ?? "-"}）");
            if (expect == "none")
            {
                Check(!r.HasGainMap, "负控：判为「无增益图」（hasGainMap=false）");
                return;
            }
            Check(r.HasGainMap, "检测到增益图");
            Check(r.Form == "tmap", $"承载形式为 tmap（实 {r.Form ?? "-"}）");
            Check(r.GainMapItemId > 0, $"增益图 item 已定位（实 {r.GainMapItemId}）");
            Check(r.GainMapFfmpegFormat != null, $"增益图有已知裸流解复用器（实 {r.GainMapFfmpegFormat ?? "-"}）");

            Check(r.TmapPayload != null, "tmap 载荷已切出");
            if (r.TmapPayload == null) return;
            var pl = r.TmapPayload;
            Check(pl.Length >= 22 && pl[0] == 0, $"tmap 载荷 version=0 且长度 ≥22（实 {pl.Length}）");

            var meta = GainMapDecoder.JpegContainer.ParseIso21496(pl, 1, pl.Length - 1);
            Check(meta != null, "ParseIso21496（跳过 version 字节）解析成功");
            if (meta == null) return;
            Console.WriteLine($"iso min={meta.GainMapMin:0.####} max={meta.GainMapMax:0.####} gamma={meta.Gamma:0.####} " +
                $"offsetSDR={meta.OffsetSdr:0.####} offsetHDR={meta.OffsetHdr:0.####} channels={meta.Channels} " +
                $"capMin={meta.HdrCapacityMin:0.####} capMax={meta.HdrCapacityMax:0.####}");
            Check(meta.GainMapMax > 0 || meta.GainMapMin < 0, "ISO 增益区间非零");
            // 载荷长度闭合：1(version) + 21(min/writer/flags+16B headroom) + ch×40
            Check(pl.Length == 1 + 21 + meta.Channels * 40,
                $"载荷长度闭合 1+21+ch×40（实 {pl.Length}，ch={meta.Channels}）");
            // 方向（**不依赖** ParseIso21496 的 flags 位）：base headroom > alternate headroom ⇒ 底图已是 HDR
            bool backward = meta.HdrCapacityMin > meta.HdrCapacityMax;
            Console.WriteLine($"direction={(backward ? "backward(底图=HDR)" : "forward(底图=SDR)")}");

            if (r.GainMapItemData != null && outRaw != null)
            {
                File.WriteAllBytes(outRaw, r.GainMapItemData);
                Console.WriteLine($"gainmap_raw_written={outRaw} bytes={r.GainMapItemData.Length}");
            }
        }

    }
}
