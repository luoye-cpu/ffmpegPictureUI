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
        /// AU2-X1（2026-11-10）：AppleMakerNote **纯托管解析器 vs exiftool 对拍**。
        /// 用法：<c>ServiceProbe gainmap-mn-parity &lt;heic&gt;</c>
        /// <para>
        /// 链路：探针切出 Exif item ⇒ <see cref="FfmpegGui.Services.AppleMakerNoteParser"/> 纯托管
        /// 读 tag 33/48（零子进程）⇒ exiftool `-j -a -G1 -MakerNotes:all` 读同两 tag（对照侧）⇒
        /// 逐值对拍（差 &lt; 1e-4）+ 合成 A 件身份值 1.5/0.5。
        /// 存在理由：GainMapDecoder 的 MN 元数据已改为「纯托管优先 + exiftool fallback」，行为契约
        /// 要求**两路读数一致**（SPEC §7.2 对拍验收硬判据）——公式段虽然已提取成共用方法
        /// （BuildAppleMetadataFromTags），但「纯托管读出的 tag 值 == exiftool 读出的 tag 值」必须
        /// 有活体对拍，不能靠结构推断。调用者 = 在册门禁 verify-gainmap-isobmff §1i（合成器 A 件）。
        /// </para>
        /// </summary>
        private static void ProbeGainMapMnParity(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: ServiceProbe gainmap-mn-parity <heic>"); _fail++; return; }
            var path = args[1];
            var bytes = File.ReadAllBytes(path);
            var r = IsoBmffGainMapProbe.Probe(bytes);
            Console.WriteLine($"file={path} size={bytes.Length} form={r.Form ?? "-"} determinate={r.Determinate}");
            Check(r.HasGainMap && r.Form == "aux-apple-hdrgainmap",
                $"对拍载体为 Apple aux 形态（实 form={r.Form ?? "-"} hasGainMap={r.HasGainMap}）");
            Check(r.Determinate && r.ExifItemData is { Length: > 0 },
                $"Exif item 已切出（实 item {r.ExifItemId}，{r.ExifItemData?.Length ?? -1} B）");

            // ── 纯托管臂（被测侧）──
            var managed = FfmpegGui.Services.AppleMakerNoteParser.TryParse(r.ExifItemData, out var mnWhy);
            Check(managed != null, $"纯托管解析成功（实 failReason={mnWhy}）");
            if (managed == null) return;
            Check(MathF.Abs(managed.Tag33HdrHeadroom - 1.5f) < 1e-4f,
                $"纯托管 tag33(HDRHeadroom) = 1.5（实 {managed.Tag33HdrHeadroom}）—— A 件身份值");
            Check(MathF.Abs(managed.Tag48HdrGain - 0.5f) < 1e-4f,
                $"纯托管 tag48(HDRGain) = 0.5（实 {managed.Tag48HdrGain}）—— A 件身份值");

            // ── exiftool 臂（对照侧；键名纪律与 GainMapDecoder.TryReadAppleMakerNoteMetadataAsync 同源）──
            if (!ExifToolService.IsAvailable)
            { Check(false, "exiftool 不可用 ⇒ 对拍无对照侧（需 publish/PLAN 工具链）"); return; }
            var exif33 = ReadMakerTagViaExiftool(path, "HDRHeadroom");
            var exif48 = ReadMakerTagViaExiftool(path, "HDRGain");
            Check(exif33.HasValue && exif48.HasValue,
                $"exiftool 读到两 tag（实 33={exif33?.ToString() ?? "缺失"} 48={exif48?.ToString() ?? "缺失"}）");
            if (!exif33.HasValue || !exif48.HasValue) return;
            Check(MathF.Abs(exif33.Value - managed.Tag33HdrHeadroom) < 1e-4f,
                $"对拍 tag33 一致（managed={managed.Tag33HdrHeadroom} exif={exif33.Value}）—— 纯托管接管后读数不得漂移");
            Check(MathF.Abs(exif48.Value - managed.Tag48HdrGain) < 1e-4f,
                $"对拍 tag48 一致（managed={managed.Tag48HdrGain} exif={exif48.Value}）—— 纯托管接管后读数不得漂移");
            Console.WriteLine($"mn-parity managed33={managed.Tag33HdrHeadroom} managed48={managed.Tag48HdrGain} " +
                $"exif33={exif33.Value} exif48={exif48.Value} parity=ok");
        }

        /// <summary>
        /// exiftool 读单 tag（`-j -a -G1 -MakerNotes:all` 的 JSON，Apple 组 / MakerNotes 组 / 裸名三形态；
        /// 值容忍 JSON number 与 "n/d" rational 字符串——exiftool 对 rational64s 的两种输出形态都实测过）。
        /// 只取值不断言（断言在对拍臂）；异常/超时 ⇒ null（不抛）。
        /// </summary>
        private static float? ReadMakerTagViaExiftool(string path, string tagName)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ExifToolService.DetectedPath!,
                    Arguments = $"-j -a -G1 -MakerNotes:all \"{path}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return null;
                p.BeginErrorReadLine();     // stderr 不排空会堵管道（同 GainMapDecoder 的 P2-3 实测）
                var readTask = p.StandardOutput.ReadToEndAsync();
                if (!readTask.Wait(10000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return null;
                }
                p.WaitForExit();
                var json = readTask.Result;
                if (string.IsNullOrWhiteSpace(json)) return null;
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Array || root.GetArrayLength() == 0) return null;
                float? fallback = null;
                foreach (var prop in root[0].EnumerateObject())
                {
                    int colon = prop.Name.IndexOf(':');
                    bool hit;
                    if (colon > 0)
                    {
                        var ns = prop.Name.Substring(0, colon);
                        hit = (ns.Equals("Apple", StringComparison.OrdinalIgnoreCase)
                               || ns.Equals("MakerNotes", StringComparison.OrdinalIgnoreCase))
                              && prop.Name.Substring(colon + 1).Equals(tagName, StringComparison.OrdinalIgnoreCase);
                    }
                    else hit = prop.Name.Equals(tagName, StringComparison.OrdinalIgnoreCase);
                    if (!hit) continue;
                    var v = ReadRationalJsonValue(prop.Value);
                    if (v.HasValue)
                    {
                        if (colon > 0) return v;      // 带组名命中 ⇒ 权威
                        fallback ??= v;               // 裸名 ⇒ 仅兜底
                    }
                }
                return fallback;
            }
            catch { return null; }
        }

        /// <summary>exiftool JSON 值 → float：number 直取；字符串容忍 "3/2"（rational）与 "1.5"（十进制）。</summary>
        private static float? ReadRationalJsonValue(System.Text.Json.JsonElement e)
        {
            if (e.ValueKind == System.Text.Json.JsonValueKind.Number)
                return e.TryGetSingle(out var f) ? f : null;
            if (e.ValueKind != System.Text.Json.JsonValueKind.String) return null;
            var s = e.GetString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            int slash = s!.IndexOf('/');
            if (slash > 0
                && float.TryParse(s.AsSpan(0, slash), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var num)
                && float.TryParse(s.AsSpan(slash + 1), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var den)
                && den != 0)
                return num / den;
            return float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        }

    }
}
