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
        // ────────────────────────────── i18n ──────────────────────────────

        /// <summary>
        /// i18n —— 双语本地化的**端到端**断言：直接调用真实现 <see cref="LocalizationService"/>，
        /// 而不是只校验 json 数据层（`_probe-i18n-scan.ps1` 做的是后者）。
        ///
        /// 为什么必须两条都有：`_probe-i18n-scan.ps1` 只能证明「json 里定义了这些 key」，
        /// 证明不了「程序真的把它们加载进来了」。中间隔着三道坎，任一道断了都会**静默**
        /// 回退成把 key 名本身显示给用户（`LocalizationService.cs:56-58` 的 getter 回退）：
        ///   ① `Resources/Locales/*.json` 是否随构建拷进输出目录（改了 json 却不重建 ⇒ 产物陈旧）；
        ///   ② 进程能否定位到该目录（`Environment.ProcessPath` → 回退逐级向上找 `Resources/Locales`）；
        ///   ③ `AppJsonContext` 能否把该 json 反序列化成字典。
        /// 本模式对**每个 key 前缀至少抽 1 条**断言「取到非空值且 != key 本身」，把三道坎一起锁住。
        ///
        /// ⚠ 抽样刻意**偏向本轮才补齐的 key**：它们此前 100% 缺失 ⇒ 若输出目录里的 json 陈旧，
        ///   这里必红，不会假绿。（无法直接读私有字典 `_strings` 的 size，故用「抽样命中率」代替。）
        /// </summary>
        private static void ProbeI18n()
        {
            var loc = LocalizationService.Instance;

            // (0) 报告实际加载目录与文件时间戳 —— 产物陈旧时这是第一手线索
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
            var probeDir = Path.Combine(exeDir, "Resources", "Locales");
            Console.WriteLine($"process-path={Environment.ProcessPath}");
            Console.WriteLine($"locale-dir={probeDir} exists={Directory.Exists(probeDir)}");
            foreach (var lang in new[] { "zh-CN", "en-US" })
            {
                var f = Path.Combine(probeDir, lang + ".json");
                if (File.Exists(f))
                    Console.WriteLine($"  {lang}.json bytes={new FileInfo(f).Length} mtime={File.GetLastWriteTime(f):yyyy-MM-dd HH:mm:ss}");
                else
                    Console.WriteLine($"  {lang}.json 缺失（将走程序集内嵌资源回退）");
            }

            // 代表 key：每个前缀至少 1 条（新增前缀 + 本轮之前就有的前缀都覆盖）
            var reps = new[]
            {
                // ── 本轮才补齐的 key（此前 100% 缺失 ⇒ 天然是「产物陈旧」的探针）──
                "gainmap.title", "priority.normal", "detail.category.camera", "meta.status.restored",
                "preset.dlg.delete", "tip.ps.verify", "dng.comp.jxl", "avif.aq.mode",
                "color.strategy.recommended", "settings.render.vulkan", "jpeg.huffman",
                "nvenc.p1", "qsv.veryslow", "svt.tune.vmaf", "vaapi.4", "amf.balanced",
                "progress.col.status", "filter.btn.ok", "png.pred.paeth", "webp.preset",
                "jxl.effort", "jxr.encoder.title", "jpegli.backend.sjpeg", "tiff.compression",
                "gif.dither", "max.dimension", "threads.auto.hint", "ph.cache.auto",
                "queue.running", "media.loading", "icc.invalid.file", "gpu.advice.switch.cpu",
                "label.ps.verify", "enable.max.dimension",
                // ── 本轮之前就存在的 key：一并锁住，防「只测新增、旧 key 回归无人管」──
                "app.title", "format.jpeg", "icc.mode.carry", "gain.map.quality", "cache.dir.tooltip",
                "preset.manager.title", "detail.technical.info", "progress.status.encoding",
                "format.filter.title", "queue.progress", "rgba.unavailable",
            };
            Console.WriteLine($"reps={reps.Length}");

            // (1)+(2)+(3) 两语言逐条：能加载 / 取到值 / 值 != key / 非空
            var perLang = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var lang in new[] { "zh-CN", "en-US" })
            {
                loc.LoadLocale(lang);
                Check(string.Equals(loc.CurrentLanguage, lang, StringComparison.Ordinal),
                      $"{lang}：LoadLocale 后 CurrentLanguage={loc.CurrentLanguage}");

                var vals = new Dictionary<string, string>(StringComparer.Ordinal);
                var bad = new List<string>();
                foreach (var k in reps)
                {
                    var v = loc[k];
                    vals[k] = v;
                    if (string.IsNullOrWhiteSpace(v) || string.Equals(v, k, StringComparison.Ordinal)) bad.Add(k);
                }
                perLang[lang] = vals;
                Check(bad.Count == 0,
                      $"{lang}：{reps.Length} 条代表 key 全部取到非空值且 != key 本身（异常 {bad.Count} 条{(bad.Count > 0 ? "：" + string.Join(", ", bad) : "")}）");
            }

            // 两套字典**确实不同**：抽 3 条 zh/en 应当不同的 key 逐对比较
            var mustDiffer = new[] { "gainmap.title", "priority.normal", "detail.category.camera" };
            var diff = 0;
            foreach (var k in mustDiffer)
                if (!string.Equals(perLang["zh-CN"][k], perLang["en-US"][k], StringComparison.Ordinal)) diff++;
            Check(diff == mustDiffer.Length,
                  $"zh-CN / en-US 确实是两套不同字典（{mustDiffer.Length} 条抽样中 {diff} 条取值不同）");

            // (4) 未定义 key 的回退契约：必须**返回 key 本身**（LocalizationService.cs:56-58）
            const string undef = "no.such.key.definitely.undefined";
            loc.LoadLocale("zh-CN");
            Check(string.Equals(loc[undef], undef, StringComparison.Ordinal),
                  "未定义 key 在 zh-CN 下回退为 key 本身（锁住索引器契约）");
            loc.LoadLocale("en-US");
            Check(string.Equals(loc[undef], undef, StringComparison.Ordinal),
                  "未定义 key 在 en-US 下同样回退为 key 本身");

            // 复位到默认语言，避免影响同进程内的后续调用
            loc.LoadLocale("zh-CN");
        }

    }
}
