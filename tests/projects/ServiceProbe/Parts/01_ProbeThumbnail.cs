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
        // ───────────────────── 缩略图（EXIF IFD1）─────────────────────

        /// <summary>
        /// 缩略图功能的产物级判据。**核心判据不是「有没有写进去」，而是「声明与数据是否闭合」**：
        /// `ThumbnailLength` 说 N 字节、`-b -ThumbnailImage` 就要能取到 N 字节。
        /// 这条尺子同时抓住本项目已实测到的悬空指针缺陷（ffmpeg 的元数据 muxer 会保留 IFD1 的
        /// Offset/Length 标签却丢掉图像数据 ⇒ 产物声称有缩略图、实际一个字节都没有）。
        /// 因此夹具里必须有一例**真悬空**的负样本，否则这条判据是空的。
        /// </summary>
        private static System.Threading.Tasks.Task ProbeThumbnail(string[] args)
        {
            const string dir = "tests/output/validate";
            var st = FfmpegGui.Services.AppSettingsService.Current;
            var ff = st.FfmpegPath;
            var et = st.ExifToolPath;
            string avifenc = "";
            try
            {
                var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
                for (int up = 0; d != null && up < 8; d = d.Parent, up++)
                {
                    var c = System.IO.Path.Combine(d.FullName, "publish", "PLAN", "artifacts", "avifenc.exe");
                    if (System.IO.File.Exists(c)) { avifenc = c; break; }
                }
            }
            catch { }

            // T0 前置：能力表是**懒播种**的（`SeedLocalRules` 由 InitializeAsync 触发）。
            // 表空时 `GetCapabilities(...) is { SupportsThumbnail: true }` 恒 false ⇒
            // 正向断言全红的同时**负向断言集体假绿**（什么都没在比对）。先证明表非空，再谈逐格式。
            FfmpegGui.Services.FormatCapabilitiesService.ClearCache();
            FfmpegGui.Services.FormatCapabilitiesService.InitializeAsync(ff).GetAwaiter().GetResult();
            var seeded = FfmpegGui.Services.FormatCapabilitiesService.GetCapabilities("jpg") != null;
            Check(seeded, "T0 前置：格式能力表已播种（表空 ⇒ 逐格式断言全是空判据）");
            if (!seeded) return System.Threading.Tasks.Task.CompletedTask;

            // ── T1..T10 纯函数与能力表：不依赖外部编码工具 ──
            var q100 = FfmpegGui.Services.ThumbnailService.MapQualityToQscale(100);
            var q1 = FfmpegGui.Services.ThumbnailService.MapQualityToQscale(1);
            Check(q100 == 2 && q1 == 31, $"T1 质量轴 1..100 → qscale 反向线性且收口 (100→{q100}, 1→{q1})");
            var qMid = FfmpegGui.Services.ThumbnailService.MapQualityToQscale(50);
            Check(qMid > q100 && qMid < q1 && qMid is >= 2 and <= 31, $"T2 质量轴单调 (50→{qMid})");
            Check(FfmpegGui.Services.ThumbnailService.MapQualityToQscale(0) == q1
                  && FfmpegGui.Services.ThumbnailService.MapQualityToQscale(999) == q100,
                  "T3 越界质量被钳到两端（不抛、不产出非法 qscale）");

            var sf = FfmpegGui.Services.ThumbnailService.BuildScaleFilter(160);
            Check(sf.Contains("min(iw,160)") && sf.Contains("min(ih,160)") && sf.Contains("-2"),
                  $"T4 缩放串两维都钳长边、另一维保比例取偶 [{sf}]");
            Check(FfmpegGui.Services.ThumbnailService.ClampLongEdge(0) == 160
                  && FfmpegGui.Services.ThumbnailService.ClampLongEdge(4000) == 512
                  && FfmpegGui.Services.ThumbnailService.ClampLongEdge(1) == 16,
                  "T5 长边钳制 [16,512]，0 回落到默认 160");

            Check(FfmpegGui.Services.ThumbnailService.CapabilityKey("jpeg") == "jpg"
                  && FfmpegGui.Services.ThumbnailService.CapabilityKey(".TIF") == "tiff"
                  && FfmpegGui.Services.ThumbnailService.CapabilityKey("PNG") == "png",
                  "T6 能力表键归一（jpeg/.TIF/PNG 都落到同一条目，避免两处口径）");

            // 能力表：兑现性按 2026-10-02 逐容器实测。判据是「回读字节 == ThumbnailLength 声明」。
            // ⚠ `no` 那四个不是"没测"，是**测了不通**：exiftool 报「0 image files updated」而**退出码仍是 0**
            //   ⇒ 这条负断言防的是"顺手把 rc 当成功"的假绿，也防有人给 GIF/JXR/DNG 开一条走不通的路。
            string[] yes = { "jpg", "png", "apng", "webp", "avif", "jxl" };
            string[] no = { "tiff", "gif", "jxr", "dng" };
            foreach (var f in yes)
                Check(FfmpegGui.Services.FormatCapabilitiesService.GetCapabilities(f) is { SupportsThumbnail: true },
                      $"T7 能力表 {f}=可写缩略图（实测依据见 FormatCapabilitiesService 注释）");
            foreach (var f in no)
                Check(FfmpegGui.Services.FormatCapabilitiesService.GetCapabilities(f) is not { SupportsThumbnail: true },
                      $"T8 能力表 {f}=不可写（实测不通：exiftool 0 updated 但 rc=0）");

            var o = new FfmpegGui.Models.FfmpegOptions { Format = "jpg" };
            Check(FfmpegGui.Services.ThumbnailService.PrivacySkipReason(o) == null, "T9 默认态不跳过（隐私开关全关时不该拦）");
            var oStripAll = new FfmpegGui.Models.FfmpegOptions { Format = "jpg", MetadataMode = FfmpegGui.Models.MetadataMode.StripAll };
            var oStripExif = new FfmpegGui.Models.FfmpegOptions { Format = "jpg", StripExifAll = true };
            Check(FfmpegGui.Services.ThumbnailService.PrivacySkipReason(oStripAll) != null
                  && FfmpegGui.Services.ThumbnailService.PrivacySkipReason(oStripExif) != null,
                  "T10 StripAll / StripExifAll 都能让缩略图让位（隐私不能让新功能破口）");

            if (string.IsNullOrEmpty(ff) || string.IsNullOrEmpty(et))
            {
                Skip("T11..T20 行为断言：ffmpeg/exiftool 未定位（需 publish/PLAN 工具链）");
                return System.Threading.Tasks.Task.CompletedTask;
            }

            Directory.CreateDirectory(dir);

            // 声明-数据闭合判据：返回 (声明长度, 实取字节)
            (int declared, int got) ReadThumb(string path)
            {
                // exiftool 一行一个标签：多行时**逐行**取数（整块 Substring 会把两行拼成一个
                // 解析不出的串，`1980\r\n…` → 声明读成 0 ⇒ 判据假红）。
                int decl = 0;
                foreach (var line in RunCapture(et, $"-n -s -ThumbnailLength \"{path}\"").Split('\n'))
                {
                    var l = line.Trim();
                    var colon = l.LastIndexOf(':');
                    if (colon < 0) continue;
                    if (int.TryParse(l.Substring(colon + 1).Trim(), out var v) && v > 0) { decl = v; break; }
                }
                var bin = path + ".thumb.bin";
                RunCapture(et, $"-b -ThumbnailImage \"{path}\"", redirectOut: bin);
                var got = File.Exists(bin) ? (int)new FileInfo(bin).Length : 0;
                try { if (File.Exists(bin)) File.Delete(bin); } catch { }
                return (decl, got);
            }
            bool Closed(string path) { var (d, g) = ReadThumb(path); return d > 0 && g == d; }

            // ── T11..T15 五个容器逐条真跑 ──
            foreach (var fmt in yes)
            {
                var outPath = $"{dir}/th_{fmt}.{fmt}";
                if (!MakeTarget(ff, avifenc, dir, outPath, fmt)) { Skip($"T11-{fmt} 造不出 {fmt} 目标件"); continue; }
                var opt = new FfmpegGui.Models.FfmpegOptions { Format = fmt, EnableThumbnail = true };
                var log = new System.Text.StringBuilder();
                var rc = RunTask(FfmpegGui.Services.ThumbnailService.ApplyAsync(outPath, opt, s => log.Append(s)));
                var (d, g) = ReadThumb(outPath);
                Check(rc == 0 && Closed(outPath),
                      $"T11 [{fmt}] 缩略图声明-数据闭合 (declared={d}, extracted={g}, rc={rc})");
            }

            // ── T12 悬空负样本：ffmpeg 造的残缺 EXIF 必须被同一把尺子抓住，否则上面全是空判据 ──
            // 两种中转件都量：png 与 apng（同一 eXIf 通路，apng 也照样漏）。
            {
                var src = $"{dir}/th_dangling_src.jpg";
                var donor = $"{dir}/th_donor.jpg";
                var withThumb = $"{dir}/th_dangling_withthumb.jpg";
                MakeTarget(ff, avifenc, dir, src, "jpg");
                MakeTarget(ff, avifenc, dir, donor, "jpg");
                File.Copy(src, withThumb, true);
                RunCapture(et, $"-overwrite_original \"-ThumbnailImage<={donor}\" \"{withThumb}\"");
                foreach (var carrier in new[] { "png", "apng" })
                {
                    var dangling = $"{dir}/th_dangling.{carrier}";
                    RunCapture(ff, $"-y -hide_banner -loglevel error -i \"{withThumb}\" -map_metadata 0 \"{dangling}\"");
                    var (d, g) = ReadThumb(dangling);
                    Check(d > 0 && g == 0,
                          $"T12 [{carrier}] 悬空负样本被抓住：声明 {d} B、实取 {g} B（这条不红，T11 的判据就是假的）");
                }
            }

            // ── T13 诚实性：能力位为 false 的容器必须**出声**拒绝，而不是静默交一张没缩略图的图 ──
            // 逐容器真跑到分发层（不只是查表）：gif/jxr 在这里同时验证「造得出件、但这条路走不通」。
            // dng 只在 T8 查表 ⇒ 它的产物依赖 gitignored 的 RAW 素材，不进默认清单的行为断言。
            foreach (var (badFmt, badExt) in new[] { ("tiff", "tif"), ("gif", "gif"), ("jxr", "jxr") })
            {
                var badPath = $"{dir}/th_neg_{badFmt}.{badExt}";
                if (!MakeTarget(ff, avifenc, dir, badPath, badFmt)) { Skip($"T13-{badFmt} 造不出目标件"); continue; }
                var opt = new FfmpegGui.Models.FfmpegOptions { Format = badFmt, EnableThumbnail = true };
                var log = new System.Text.StringBuilder();
                var rc = RunTask(FfmpegGui.Services.ThumbnailService.ApplyAsync(badPath, opt, s => log.Append(s)));
                var (d, _) = ReadThumb(badPath);
                Check(rc != 0 && log.ToString().Contains("not expressible") && d == 0,
                      $"T13 [{badFmt}] 出声拒绝而非静默丢弃 (rc={rc}, 点名={log.ToString().Contains("not expressible")}, declared={d})");
            }

            // ── T14 反真空对照：开关关掉 ⇒ 一个字不说、文件一个字节都不动 ──
            {
                var png = $"{dir}/th_off_ctrl.png";
                MakeTarget(ff, avifenc, dir, png, "png");
                var before = Sha(png);
                var opt = new FfmpegGui.Models.FfmpegOptions { Format = "png", EnableThumbnail = false };
                var log = new System.Text.StringBuilder();
                var rc = RunTask(FfmpegGui.Services.ThumbnailService.ApplyAsync(png, opt, s => log.Append(s)));
                Check(rc == 0 && log.Length == 0 && Sha(png) == before,
                      $"T14 开关关闭时零播报且零改写 (rc={rc}, logLen={log.Length})");
            }

            // ── T15 不伤主图：嵌入前后解码像素逐字节相同 ──
            {
                var jpg = $"{dir}/th_pixels.jpg";
                MakeTarget(ff, avifenc, dir, jpg, "jpg");
                var a = $"{dir}/th_px_a.ppm"; var b = $"{dir}/th_px_b.ppm";
                RunCapture(ff, $"-y -hide_banner -loglevel error -i \"{jpg}\" -frames:v 1 \"{a}\"");
                var opt = new FfmpegGui.Models.FfmpegOptions { Format = "jpg", EnableThumbnail = true };
                RunTask(FfmpegGui.Services.ThumbnailService.ApplyAsync(jpg, opt!, _ => { }));
                RunCapture(ff, $"-y -hide_banner -loglevel error -i \"{jpg}\" -frames:v 1 \"{b}\"");
                bool same = File.Exists(a) && File.Exists(b)
                    && new FileInfo(a).Length == new FileInfo(b).Length
                    && Sha(a) == Sha(b);
                Check(same, "T15 嵌入缩略图不改动主图像素（前后解码逐字节相同）");
            }

            // ── T16 隐私优先：StripAll 开着也不能把缩略图塞回去 ──
            {
                var png = $"{dir}/th_privacy.png";
                MakeTarget(ff, avifenc, dir, png, "png");
                var opt = new FfmpegGui.Models.FfmpegOptions
                {
                    Format = "png", EnableThumbnail = true, MetadataMode = FfmpegGui.Models.MetadataMode.StripAll
                };
                var log = new System.Text.StringBuilder();
                var rc = RunTask(FfmpegGui.Services.ThumbnailService.ApplyAsync(png, opt, s => log.Append(s)));
                var (d, g) = ReadThumb(png);
                Check(rc != 0 && log.ToString().Contains("skipped") && d == 0 && g == 0,
                      $"T16 StripAll 时缩略图让位 (rc={rc}, declared={d}, extracted={g})");
            }

            // ── T17 越界尺寸只钳不炸，且仍闭合 ──
            {
                var png = $"{dir}/th_clamp.png";
                MakeTarget(ff, avifenc, dir, png, "png");
                var opt = new FfmpegGui.Models.FfmpegOptions
                {
                    Format = "png", EnableThumbnail = true, ThumbnailLongEdge = 9999, ThumbnailQuality = 99
                };
                var rc = RunTask(FfmpegGui.Services.ThumbnailService.ApplyAsync(png, opt, _ => { }));
                Check(rc == 0 && Closed(png), $"T17 长边越界被钳到 512 后仍写出闭合缩略图 (rc={rc})");
            }

            // ── T18 命令行形状锁：探测/embed 两条串不能被人顺手改坏 ──
            {
                var enc = FfmpegGui.Services.ThumbnailService.BuildEncodeArgs("IN.png", "OUT.jpg", 160, 75);
                var emb = FfmpegGui.Services.ThumbnailService.BuildEmbedArgs("T.jpg", "O.png");
                Check(enc.Contains("-vf \"scale='if(gt(iw,ih)") && enc.Contains("-frames:v 1") && enc.Contains("-qscale:v")
                      && emb.Contains("-ThumbnailImage<=\"T.jpg\"") && emb.Contains("-overwrite_original"),
                      $"T18 命令行形状锁 (embed=[{emb}])");
            }

            // ── T19 色彩标签不被带入：写缩略图不得引入 ColorSpace/ICC（色彩后置保留裁决权） ──
            {
                var jpg = $"{dir}/th_color.jpg";
                MakeTarget(ff, avifenc, dir, jpg, "jpg");
                RunTask(FfmpegGui.Services.ThumbnailService.ApplyAsync(jpg,
                    new FfmpegGui.Models.FfmpegOptions { Format = "jpg", EnableThumbnail = true }, _ => { }));
                var cs = RunCapture(et, $"-s -ColorSpace \"{jpg}\"");
                Check(!cs.Contains(":"),
                      $"T19 缩略图写入不附带 ColorSpace 宣称 (读出=[{cs.Trim()}])");
            }

            // ── T20 CLI 可达性：`--thumbnail*` 必须被解析器收下（未知键会在这里暴露） ──
            {
                var res = FfmpegGui.CliParser.Parse(new[] {
                    "--headless", "--input", "in.jpg", "--output", "out", "--format", "jpg",
                    "--thumbnail=true", "--thumbnail-size=240", "--thumbnail-quality=90" });
                res.ExtraOptions.TryGetValue("thumbnail", out var t);
                res.ExtraOptions.TryGetValue("thumbnail-size", out var sz);
                res.ExtraOptions.TryGetValue("thumbnail-quality", out var qq);
                Check(t == "true" && sz == "240" && qq == "90",
                      $"T20 CLI 收下三个键 (thumbnail={t}, size={sz}, quality={qq})");
            }

            // ── T21 结构锁：键 → 模型字段的映射只有 CliParser 里那一行负责，而它在 private 方法链里
            //     （`BuildBaseOptions`/`ApplyExtraOption` 都不对探针可见）⇒ 只能按字面文本钉住。
            T21_StructuralLockOnCliMapping();

            return System.Threading.Tasks.Task.CompletedTask;
        }

        /// <summary>
        /// T21：把「CLI 键 → 模型字段」的三行映射钉成结构锁。
        /// 需要它是因为 T20 只证明解析器**收下**了键，不证明有人把值搬到 `FfmpegOptions` 上 ——
        /// 本仓已有前例：`--jxl-modular` 曾被 UI 采集、进模型、再不被任何消费者读取（空枪）。
        /// </summary>
        private static void T21_StructuralLockOnCliMapping()
        {
            string? root = null;
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            for (int up = 0; d != null && up < 8; d = d.Parent, up++)
            {
                var c = System.IO.Path.Combine(d.FullName, "src", "FfmpegGui", "CliParser.cs");
                if (System.IO.File.Exists(c)) { root = c; break; }
            }
            if (root == null) { Skip("T21 结构锁：找不到 CliParser.cs 源文件"); return; }

            var src = System.IO.File.ReadAllText(root);
            foreach (var line in new[] {
                "case \"thumbnail\": options.EnableThumbnail = ParseBoolStrict(key, value); break;",
                "case \"thumbnail-size\": options.ThumbnailLongEdge = ParseIntStrict(key, value); break;",
                "case \"thumbnail-quality\": options.ThumbnailQuality = ParseIntStrict(key, value); break;" })
                Check(src.Contains(line), $"T21 映射行在位 [{line}]");
        }

    }
}
