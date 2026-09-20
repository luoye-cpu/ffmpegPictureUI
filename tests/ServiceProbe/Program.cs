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
using VK = FfmpegGui.Services.ColorMapping.VerdictKind;          // S5′ 裁决探针
using DG = FfmpegGui.Services.ColorMapping.DegradationCodes;     // S5′ 裁决探针
using F709 = FfmpegGui.Services.ColorMapping.Transfer709Curve;
using AAS = FfmpegGui.Services.AppSettingsService;

namespace ServiceProbe
{
    /// <summary>
    /// 服务级探针 —— 直接调用真实服务实现做单元级验证，无需跑完整编码队列。
    ///
    /// 用法:
    ///   ServiceProbe gainmap &lt;jpeg&gt; [写出副图路径]   纯托管容器解析 (ISO 21496-1 / MPF / 副图切割)
    ///   ServiceProbe icc &lt;色彩空间名&gt;                 解析/生成命名 ICC 并打印头 + tag 表
    ///   ServiceProbe cicp &lt;文件&gt;                     ffprobe 风格的色彩元数据探测结果 (Registry 解释)
    ///   ServiceProbe matrix                           线性域 primaries 矩阵：对公开发布值 + 互逆自洽
    ///   ServiceProbe curve                            传递函数表：锚点 / 往返互逆 / 单调 / 与 SimdPixelOps 一致
    ///   ServiceProbe gamut &lt;jpeg&gt; [token]            读底图 ICC 判原色
    ///
    /// 退出码: 0 = 全部断言通过 (若给了断言), 1 = 失败; 输出为 key=value 行, 便于脚本 grep。
    /// </summary>
    internal static class Program
    {
        private static int _pass, _fail;

        /// <summary>
        /// 从探针输出目录向上定位仓库内工具链（publish/PLAN），写入 AppSettings。
        /// 没这一步，依赖 ffmpeg(iccgen) / exiftool 的断言会“SKIP 开绿”，看板上看不出来。
        /// </summary>
        private static void InitExternalTools()
        {
            try
            {
                var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
                for (int up = 0; dir != null && up < 8; dir = dir.Parent, up++)
                {
                    var ff = System.IO.Path.Combine(dir.FullName, "publish", "PLAN", "ffmpeg-full", "ffmpeg.exe");
                    if (!System.IO.File.Exists(ff)) continue;
                    var st = FfmpegGui.Services.AppSettingsService.Current;
                    st.FfmpegDirectory = System.IO.Path.GetDirectoryName(ff);
                    var et = System.IO.Path.Combine(dir.FullName, "publish", "PLAN", "exiftool", "exiftool.exe");
                    if (System.IO.File.Exists(et)) st.ExifToolPath = et;
                    Console.WriteLine($"tools-root={dir.FullName}");
                    return;
                }
                Console.WriteLine("tools-root=(未找到 publish/PLAN，需外部工具的断言将 SKIP)");
            }
            catch (Exception ex) { Console.WriteLine($"tools-init 异常: {ex.GetType().Name}: {ex.Message}"); }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            // ── P2-5 行为探针的子进程端：把 UTF-8 字节原样写到两条流上 ──
            // 直接写 Console.OpenStandard*()，绕开 Console 编码设置，保证「产出的字节 = UTF-8」可被父进程逐字节验证。
            // 放在 InitExternalTools 之前：子进程要快，且不依赖外部工具。
            if (args.Length > 0 && args[0].Equals("emit-utf8", StringComparison.OrdinalIgnoreCase))
            {
                var payload = "OUT:" + (args.Length > 1 ? args[1] : "");
                Console.OpenStandardOutput().Write(Encoding.UTF8.GetBytes(payload));
                Console.OpenStandardOutput().Flush();
                var errPayload = "ERR:" + (args.Length > 1 ? args[1] : "");
                Console.OpenStandardError().Write(Encoding.UTF8.GetBytes(errPayload));
                Console.OpenStandardError().Flush();
                return 0;
            }
            InitExternalTools();          // 否则 iccgen/exiftool 类互测会静默 SKIP（假绿）
            if (args.Length == 0)
            {
                Console.WriteLine("usage: ServiceProbe <gainmap|icc|icc-dump|cicp|color|faithful|pq|matrix|curve|plan|geometry|bench|selftest|i18n> ...");
                return 2;
            }
            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "gainmap": ProbeGainMap(args); break;
                    case "gainmap-isobmff": ProbeGainMapIsoBmff(args); break;
                    case "icc": ProbeIcc(args); break;
                    case "icc-dump": ProbeIccDump(args); break;
                    case "faithful": ProbeFaithful(args); break;
                    case "color": ProbeColor(args); break;
                    case "pq": ProbePq(); break;
                    case "gamut": ProbeGamut(args); break;
                    case "matrix": ProbeMatrix(); break;
                    case "curve": ProbeCurve(); break;
                    case "plan": ProbePlan(args); break;
                    case "contract": ProbeContract(); break;
                    // verdict —— S5′：纯函数级裁决断言（每个 Degradation.Code 一条正向 + 一条负控）
                    case "verdict": ProbeVerdict(); break;
                    case "bitdepth": ProbeBitDepth(args).GetAwaiter().GetResult(); break;
                    case "tonemap": ProbeToneMap(args).GetAwaiter().GetResult(); break;
                    case "tonemapcurve": DumpToneMapCurve(args); break;
                    case "ootf": ProbeOotf(); break;
                    // hlgwire —— **接线级**门禁：HLG 是否在**真实执行路径**（RGB48 内核）上应用了 OOTF
                    case "hlgwire": ProbeHlgWire(); break;
                    case "bt2446": ProbeBt2446(); break;
                    // png3 <file.png> [sbitBits] [cicpPrimaries cicpTransfer] —— 用**生产代码**写 chunk，
                    // 供外部脚本用 ffmpeg/ffprobe/exiftool 做互操作验收（现有测试只验自写自读）。
                    case "png3": ProbePng3(args); break;
                    case "wire": ProbeWire(); break;
                    // peak <文件> [期望元数据峰值] [用户覆盖峰值] —— 端到端验“源峰值读取”（P4c）
                    case "peak": ProbePeak(args); break;
                    // peakmeasure &lt;PQ图&gt; &lt;最高码值&gt; [用户峰值] —— 整帧实测内容峰值（峰值链路最后一档）
                    case "peakmeasure": ProbePeakMeasure(args).GetAwaiter().GetResult(); break;
                    case "iccname": ProbeIccName(); break;
                    // psnr &lt;a&gt; &lt;b&gt; [阈dB] —— 任意两张图（同尺寸）的像素级比对，接线路径的门禁需要它
                    case "psnr": ProbePsnr(args).GetAwaiter().GetResult(); break;
                    case "runner": ProbeRunnerGuard(args).GetAwaiter().GetResult(); break;
                    case "settings": ProbeSettingsSave(); break;
                    case "procstreams": ProbeProcStreams(); break;
                    case "encoding": ProbeProcEncoding(); break;
                    case "ipc": ProbeIpc(); break;
                    // ipc-server [秒] —— 跨进程往返的服务端（驻留）；ipc-client —— 跨进程往返的客户端
                    case "ipc-server":
                        ProbeIpcServer(args.Length > 1 && int.TryParse(args[1], out var sec) ? sec : 30);
                        break;
                    case "ipc-client": ProbeIpcClient(); break;
                    case "ipc-raw-client": ProbeIpcRawClient(); break;
                    // ipc-submit <输入文件> <输出目录> —— 向**正在运行的实例**（真实 GUI）提交任务并等产物
                    case "ipc-submit":
                        if (args.Length > 2) ProbeIpcSubmitToRunning(args[1], args[2]);
                        else { Console.WriteLine("usage: ServiceProbe ipc-submit <inFile> <outDir>"); return 2; }
                        break;
                    case "piperaw-server":
                        ProbePipeRawServer(args.Length > 1 && int.TryParse(args[1], out var psec) ? psec : 20);
                        break;
                    case "piperaw-client": ProbePipeRawClient(); break;
                    case "decision": ProbeDecision(); break;
                    case "engine": ProbeEngineAb(); break;
                    // geometry [输出根目录] —— 引擎「最长边几何缩放」路径覆盖（直调引擎，绕过 QueueProcessor 预缩放）
                    case "geometry": ProbeGeometry(args).GetAwaiter().GetResult(); break;
                    // firstframe <input> [输出根目录] —— 引擎解码步「多帧输入只取首帧」覆盖（直调引擎，绕过路由层）
                    case "firstframe": ProbeFirstframe(args).GetAwaiter().GetResult(); break;
                    case "selftest": ProbeSelfTest(); break;
                    // i18n —— 双语本地化端到端：真实现 LocalizationService 能加载两套字典且取值命中
                    case "i18n": ProbeI18n(); break;
                    case "bench": ProbeBench(args); break;
                    case "xcheck": ProbeXcheck(args).GetAwaiter().GetResult(); break;
                    default:
                        Console.WriteLine($"unknown command: {args[0]}");
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"exception: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine(ex.StackTrace ?? "");
                _fail++;
            }
            Console.WriteLine($"PROBE RESULT: pass={_pass} fail={_fail}");
            return _fail == 0 ? 0 : 1;
        }

        private static void Check(bool ok, string msg)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {msg}");
            if (ok) _pass++; else _fail++;
        }

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

        // ────────────────────────────── gainmap ──────────────────────────────

        private static void ProbeGainMap(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: ServiceProbe gainmap <jpeg> [outSecondary]"); _fail++; return; }
            var path = args[1];
            var bytes = File.ReadAllBytes(path);
            Console.WriteLine($"file={path} size={bytes.Length}");

            var c = GainMapDecoder.JpegContainer.Parse(bytes);
            Check(c != null, "容器解析非 null");
            if (c == null) return;
            Console.WriteLine($"images={c.Images.Count}");
            for (int i = 0; i < c.Images.Count; i++)
            {
                var im = c.Images[i];
                Console.WriteLine($"image[{i}] start={im.Start} len={im.Length} {im.W}x{im.H}");
            }
            Console.WriteLine($"mpfEntries={c.MpfEntries.Count} mpfBase={c.MpfBaseOffset}");
            for (int i = 0; i < c.MpfEntries.Count; i++)
            {
                var e = c.MpfEntries[i];
                int abs = c.MpfBaseOffset + (int)e.Offset;
                bool soi = abs > 0 && abs + 1 < bytes.Length && bytes[abs] == 0xFF && bytes[abs + 1] == 0xD8;
                Console.WriteLine($"mpf[{i}] attr=0x{e.Attr:X8} size={e.Size} offset={e.Offset} abs={abs} soi={soi}");
            }
            var iso = c.IsoMetadata;
            if (iso == null) Console.WriteLine("iso=null");
            else
            {
                Console.WriteLine($"iso min={iso.GainMapMin:0.####} max={iso.GainMapMax:0.####} gamma={iso.Gamma:0.####} " +
                    $"offsetSDR={iso.OffsetSdr:0.####} offsetHDR={iso.OffsetHdr:0.####} channels={iso.Channels} " +
                    $"hdrBase={iso.BaseRenditionIsHdr} capMin={iso.HdrCapacityMin:0.####} capMax={iso.HdrCapacityMax:0.####}");
            }
            Check(c.IsGainMapContainer(), "IsGainMapContainer()=true");
            Check(c.Images.Count >= 2, $"顶层图像数 {c.Images.Count} >= 2");
            Check(iso != null, "ISO 21496-1 元数据存在");
            // 增益区间非零：SDR 底 max>0（上映射）或 HDR 底 min<0（下映射）均合法
            Check(iso != null && (iso.GainMapMax > 0 || iso.GainMapMin < 0), "ISO 增益区间非零");
            if (iso != null && iso.BaseRenditionIsHdr)
                Console.WriteLine("note=HDR 底（backwardDirection）：增益应为 min<0, max=0");

            var sec = c.GetSecondaryImage();
            Check(sec != null, "副图定位成功");
            if (sec != null && args.Length >= 3)
            {
                File.WriteAllBytes(args[2], c.Slice(sec.Value));
                Console.WriteLine($"secondary_written={args[2]} bytes={c.Slice(sec.Value).Length}");
            }
        }

        /// <summary>
        /// ISO-BMFF（AVIF/HEIC）增益图探测。用法：
        /// <c>ServiceProbe gainmap-isobmff &lt;file&gt; [outGainMapRaw] [gainmap|none]</c>
        /// <para>
        /// 第 4 个位置参数声明**期望**：<c>gainmap</c>（默认）或 <c>none</c>
        /// （负控：普通 AVIF/HEIC 必须判「无增益图」，用来证明判据**有牙**而不是恒真）。
        /// </para>
        /// </summary>
        private static void ProbeGainMapIsoBmff(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: ServiceProbe gainmap-isobmff <file> [outRaw] [gainmap|none]"); _fail++; return; }
            var path = args[1];
            // ⚠ 位置参数**与开关混排**：`none` / `gainmap` 是开关（可出现在任意位置），
            //   其余第一个非开关参数才是 outRaw —— 旧写法写死 args[3] ⇒ `gainmap-isobmff <f> none`
            //   会被当成 outRaw 并静默退回默认期望，负控**整段失效**（实测已踩）。
            var expect = "gainmap";
            string? outRaw = null;
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i].Equals("none", StringComparison.OrdinalIgnoreCase)) expect = "none";
                else if (args[i].Equals("gainmap", StringComparison.OrdinalIgnoreCase)) expect = "gainmap";
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

            Check(r.IsIsoBmff, "识别为 ISO-BMFF（ftyp 存在）");
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

        /// <summary>faithful &lt;色彩空间名&gt; [期望 yes|no] —— 保真 ICC 可用性 + 确定性(重复生成同字节)。</summary>
        private static void ProbeFaithful(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: ServiceProbe faithful <spec> [yes|no]"); _fail++; return; }
            var spec = args[1];
            var expectYes = args.Length >= 3 ? args[2].StartsWith("y", StringComparison.OrdinalIgnoreCase) : (bool?)null;
            var p1 = IccProfileService.GetFaithfulIcc(spec);
            var p2 = IccProfileService.GetFaithfulIcc(spec);
            Console.WriteLine($"spec={spec} faithfulOnly={IccProfileBuilder.IsFaithfulOnly(spec)} icc={p1 ?? "(null)"}");
            if (p1 == null)
            {
                if (expectYes == false) Check(true, $"{spec} 不走保真路径（预期）");
                else Check(false, $"{spec} 应有保真 ICC 但为 null");
                return;
            }
            Check(expectYes != false, $"{spec} 保真 ICC 解析成功");
            var b1 = File.ReadAllBytes(p1);
            Check(p2 == p1 && File.ReadAllBytes(p2!).AsSpan().SequenceEqual(b1), "重复解析同一缓存且字节一致（确定性）");
            Check(IccProfileService.IsValidIccProfile(p1), "ICC 魔数有效");
            var info = IccProfileService.ParseInfo(p1);
            Console.WriteLine($"size={b1.Length} version={info?.Version} class={info?.DeviceClass} cs={info?.ColorSpace} desc={info?.Description}");
            Check(!string.IsNullOrWhiteSpace(info?.Description), "mluc desc 可回读");
            Check((info?.ColorSpace ?? "").StartsWith("RGB"), "色彩空间签名 RGB");
            // 关键 tag 齐备且 wtpt = D50
            foreach (var t in new[] { "wtpt", "bkpt", "rXYZ", "gXYZ", "bXYZ", "rTRC", "gTRC", "bTRC", "chad", "desc" })
            {
                var (off, len) = IccProfileService.FindIccTag(b1, t);
                Check(off > 0 && len > 0, $"tag {t} 存在 (off={off} len={len})");
            }
            var (wo, _) = IccProfileService.FindIccTag(b1, "wtpt");
            Check(Math.Abs(ReadS15(b1, (int)wo + 12) - 1.0) < 0.001, "wtpt.Y = 1.0（D50 PCS 归一）");
            // tag 目录必须升序（ICC.1 强制 + 本仓库二分查找依赖）
            uint n = (uint)((b1[128] << 24) | (b1[129] << 16) | (b1[130] << 8) | b1[131]);
            bool sorted = true;
            for (int i = 1; i < n; i++)
                if (string.CompareOrdinal(ReadAscii(b1, 132 + (i - 1) * 12, 4), ReadAscii(b1, 132 + i * 12, 4)) > 0) sorted = false;
            Check(sorted, "tag 目录升序");
        }

        /// <summary>color &lt;文件&gt; [格式] [exiftool.exe] —— 源色彩探测结果 + 保真判定。</summary>
        private static void ProbeColor(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: ServiceProbe color <file> [fmt] [exiftool]"); _fail++; return; }
            // ICC/EXIF 语义探测依赖 exiftool：探针进程无 PLAN 自动检测，需显式传入路径
            if (args.Length >= 4) FfmpegGui.Services.AppSettingsService.Current.ExifToolPath = args[3];
            Console.WriteLine($"exiftool available={ExifToolService.IsAvailable} path={ExifToolService.DetectedPath ?? "(null)"}");
            var (ip, id) = IccProfileService.ExtractIccToTempFile(args[1]);
            Console.WriteLine($"extractedIcc={(ip == null ? "(null)" : Path.GetFileName(ip))} desc={id ?? "(null)"} " +
                $"guessed={IccProfileService.GuessColorSpace(id) ?? "(null)"}");
            if (ip != null) IccProfileService.TryDeleteIcc(ip);
            var m = FfmpegGui.Services.FfmpegCommandBuilder.ProbeInputColorMetadata(args[1]);
            Console.WriteLine($"gamut={m.sourceGamutName ?? "(null)"} primaries={m.colorPrimaries ?? "-"} trc={m.colorTrc ?? "-"} " +
                $"matrix={m.colorSpace ?? "-"} hasIccSemantics={m.hasIccSemantics} isRgb={m.isRgb} bitDepth={m.bitDepth} " +
                $"peakNits={m.peakNits} {m.width}x{m.height}");
            // 同时给出“度量优先”的识别结果（与 sourceGamutName 同一入口），便于脚本断言而不看描述文字
            {
                var (ip2, id2) = IccProfileService.ExtractIccToTempFile(args[1]);
                Console.WriteLine($"spaceName=" + (FfmpegGui.Services.ColorMapping.ColorSpaceIdentify
                    .IdentifyIccSpaceName(ip2, id2) ?? "(null)"));
                IccProfileService.TryDeleteIcc(ip2);
            }
            var opt = new FfmpegGui.Models.FfmpegOptions { Format = args.Length >= 3 ? args[2] : "png" };
            var f = FfmpegGui.Services.FfmpegCommandBuilder.ResolveFaithfulUltraWideIcc(opt, m.sourceGamutName, m.colorTrc, opt.Format);
            Console.WriteLine($"faithfulIcc={(f ?? "(null)")}");
        }

        /// <summary>
        /// curve —— 传递函数表的锚点 + 互逆自洽 + 与现有 SIMD 内核一致性。
        /// 规则：锚点值来自标准定义或可直接算出的值；互逆与单调是“不变量”，即使抄错也能拦住。
        /// </summary>
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
            Anch("PQ EOTF(0.5) nits", TC.Pq.ToLinear(0.5) * 10000.0, 92.7, 1.0);
            Anch("PQ EOTF(0.5799) nits", TC.Pq.ToLinear(0.5799) * 10000.0, 203.0, 1.0);
            Anch("PQ EOTF(0.7516) nits", TC.Pq.ToLinear(0.7516) * 10000.0, 1000.0, 3.0);
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
        }

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
            Case("sRGB→P3 两侧可命名→H2", FfmpegGui.Services.ColorMapping.ColorAction.Map,
                FfmpegGui.Services.ColorMapping.ColorBackend.FfmpegFilter, true, srgb, p3);
            Case("PQ2020→sRGB 可命名→H2", FfmpegGui.Services.ColorMapping.ColorAction.Map,
                FfmpegGui.Services.ColorMapping.ColorBackend.FfmpegFilter, true, pq2020, srgb);
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
            return new FfmpegGui.Services.ColorMapping.TransformSpec
            {
                SrcCurve = src.Curve,
                DstCurve = dst.Curve,
                Matrix = matrix ? src.MatrixTo(dst) : null,
                ClampLinearBeforeEncode = clamp,
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

        /// <summary>
        /// contract —— 四策略契约自检（规划 §6 C1–C6）：枚举 策略×容器×源 全格，
        /// 断言以 <b>EffectiveStrategy</b> 为准；带 OverrideReason 的覆盖格豁免结局断言（但必须有说明）。
        /// </summary>
        private static void ProbeContract()
        {
            CS Mk(string prim, string trc, string name, string? icc, DC conf)
            {
                var d = CS.FromCicp(prim, trc, name)!;
                if (icc != null) { d.SourceIccPath = icc; d.IsIccDerived = true; }
                d.Confidence = conf;
                return d;
            }
            var srgbTag = Mk("bt709", "iec61966-2-1", "sRGB(仅标签)", null, DC.CicpTag);
            var srgbIcc = Mk("bt709", "iec61966-2-1", "sRGB(带ICC)", "t.srgb.icc", DC.IccMetric);
            var p3Icc = Mk("smpte432", "iec61966-2-1", "Display P3(带ICC)", "t.p3.icc", DC.IccMetric);
            var pq2020 = CS.FromCicp("bt2020", "smpte2084", "BT.2100 PQ", nitsOfLinearOne: TC.PqPeakNits)!;
            pq2020.Confidence = DC.CicpTag;
            var adobe = CS.FromNamedSpace("Adobe RGB")!; adobe.SourceIccPath = "t.adobe.icc"; adobe.Confidence = DC.IccMetric;
            var prophoto = CS.FromNamedSpace("ProPhoto RGB")!; prophoto.SourceIccPath = "t.prophoto.icc"; prophoto.Confidence = DC.IccMetric;
            var untagged = FfmpegGui.Services.ColorMapping.ColorSpaceIdentify.AssumeByConvention()!;
            // HLG 必须单独成行：它是 scene-referred，与 PQ（display-referred 绝对 nits）的处理路径不同，
            // 共用一行会掩盖“缺 OOTF”这类系统性错误。
            var hlg2020 = CS.FromCicp("bt2020", "arib-std-b67", "BT.2100 HLG", nitsOfLinearOne: 1000.0)!;
            hlg2020.Confidence = DC.CicpTag;

            var sources = new (string Name, CS D)[]
                { ("sRGB仅标签", srgbTag), ("sRGB带ICC", srgbIcc), ("P3带ICC", p3Icc), ("HDR-PQ", pq2020), ("HDR-HLG", hlg2020), ("Adobe带ICC", adobe), ("无标签", untagged) };
            var formats = new[] { "png", "jpg", "tiff", "webp", "avif", "jxl", "gif", "heic" };
            var strategies = new[] { ST.Recommended, ST.CarryIcc, ST.BakeCicpOnly, ST.Manual };

            var viol = new System.Collections.Generic.List<string>();
            var rejectReasons = new System.Collections.Generic.Dictionary<string, int>();
            var rejectByStrategy = new System.Collections.Generic.Dictionary<ST, int>();
            int cells = 0, exempted = 0, rejects = 0;

            CI MakeIntent(ST st, CS src, string fmt, CSC scope)
            {
                var it = new CI
                {
                    Strategy = st, Source = src, Format = CE.CapsFor(fmt), CarryScope = scope,
                    TargetBitDepth = 8, AllowApproximateCurve = false,
                    Manual = st == ST.Manual ? MI.Create(true, "Display P3", "smpte432", "iec61966-2-1", null)
                                             : MI.Create(false, null, null, null, null),
                };
                if (st == ST.Manual) { it.TargetExplicit = true; it.Target = p3Icc; }
                return it;
            }

            foreach (var st in strategies)
                foreach (var fmt in formats)
                    foreach (var (sname, src) in sources)
                    {
                        var it = MakeIntent(st, src, fmt, CSC.All);
                        var p = CE.Plan(it);
                        cells++;
                        var caps = CE.CapsFor(fmt);
                        bool ex = p.Override != OR.None;
                        if (ex) exempted++;
                        if (p.Action == CA.Reject)
                        {
                            rejects++;
                            rejectByStrategy[st] = rejectByStrategy.GetValueOrDefault(st) + 1;
                            var rk = p.Reason.Length > 58 ? p.Reason[..58] : p.Reason;
                            rejectReasons[rk] = rejectReasons.GetValueOrDefault(rk) + 1;
                        }
                        void V(bool ok, string msg) { if (!ok) viol.Add($"[{st}/{fmt}/{sname}] {msg}"); }

                        // 通则：拒绝必带建议；ColorLoss=None 不得伴随像素变更
                        V(p.Action != CA.Reject || p.Advice.Length > 0, "Reject 无 Advice");
                        V(p.ColorLoss != CL.None || (p.Matrix == null && !p.NeedsCurveChange), "ColorLoss=None 却有矩阵/曲线变更");
                        V(p.Annot != AC.IccAndCicp || caps.CanHaveBothIccAndCicp, "标为 ICC+CICP 双描述但容器不支持共存");
                        if (p.Action != CA.Reject)
                            V(p.AttachIcc || p.OutPrimaries != null || p.UnlabeledAssumedSrgb, "产出行无任何标注也未记 UnlabeledAssumedSrgb");
                        if (ex) V(p.Reason.Length > 0, "覆盖格缺覆盖说明（禁止偷偷覆盖）");
                        // 不变量：**同时声明了** CICP 与自生成 ICC 时，两者必须用同一条 TRC。
                        // ⚠ 已用变异测试证明：在 224 格里它是**空转的**（把 IccTrc 改回写死 sRGB 仍 95/0），
                        //   因为所有生成 ICC 的格子要么被“ICC 优先”清了 Out*、要么 IccTrc 为 null。
                        //   它只当未来变更的绊线；本 bug 的真正探测器是 verify-color-wiring (6)（已同样用变异验证过）。
                        // 只管“两条都声明”的行：故意只声明 ICC、不写 CICP 的行（容器非规范性，
                        // ColorStrategyPlanning 在 ICC 定好后清空 Out*）属合法，不构成矛盾。
                        if (p.AttachIcc && p.IccPath == null && p.IccFromGenerator
                            && p.IccTrc != null && p.OutTransfer != null)
                            V(string.Equals(p.IccTrc, p.OutTransfer, StringComparison.OrdinalIgnoreCase),
                              $"ICC 的 TRC({p.IccTrc}) 与输出 TRC({p.OutTransfer}) 不一致");

                        // C1/C2：S2 生效格（含覆盖格）永不改像素、永不生成 ICC
                        if (p.EffectiveStrategy == ST.CarryIcc)
                        {
                            V(p.Backend == CB.None && p.Matrix == null && !p.NeedsCurveChange && !p.IccFromGenerator,
                              "S2 生效格出现像素变更或生成 ICC");
                            V(p.Action is CA.CarryIcc or CA.None or CA.Reject, "S2 出现非法 Action");
                            V(!p.AttachIcc || p.IccPath != null, "S2 附着了非携带的 ICC");
                            // S2 的 Action=None 必须属于两种零改动形态之一（继承 CICP / 隐含 sRGB 无标注）
                            if (p.Action == CA.None)
                                V(p.UnlabeledAssumedSrgb || p.Annot == AC.CicpOnly,
                                  "S2 的 None 必须是「继承源 CICP」或「隐含 sRGB 无标注」");
                            if (p.UnlabeledAssumedSrgb)
                                V(!caps.HasColorPath || caps.UnlabeledMeansSrgb || it.AllowUnlabeled,
                                  "S2 无标注直通，但容器有色彩通路却不允许未标注");
                        }
                        // C2/C3：S3 绝不输出 ICC；无 CICP 容器上只能 None/Reject；可 CICP 容器上必须真写了 CICP
                        if (p.EffectiveStrategy == ST.BakeCicpOnly)
                        {
                            V(!p.AttachIcc && !p.IccFromGenerator && !p.UnlabeledAssumedSrgb || p.UnlabeledAssumedSrgb && !p.AttachIcc,
                              "S3 输出了 ICC");
                            if (!caps.CanCicp && !ex)
                            {
                                V(p.Action is CA.None or CA.Reject, "S3 在无 CICP 容器上映射了像素");
                                if (p.Action == CA.None)
                                    V(caps.UnlabeledMeansSrgb || it.AllowUnlabeled || !caps.HasColorPath,
                                      "S3 在无 CICP 容器上无标注直通，但容器既有色调通路又不在白名单（TIFF 类）");
                            }
                            if (caps.CanCicp && p.Action != CA.Reject && !ex)
                                V(p.OutPrimaries != null, "S3 在可 CICP 容器上却没写 CICP");
                        }
                        // S1：携带与否必须与 CarryScope 定义一致（此处 scope=All）
                        if (p.EffectiveStrategy == ST.Recommended && p.Action == CA.CarryIcc)
                            V(caps.CanCarryArbitraryIcc && src.SourceIccPath != null, "S1 携带了不存在的 ICC/容器不支持");
                    }

            Check(viol.Count == 0, $"contract C1–C4: {cells} 格（覆盖豁免 {exempted} 格、拒绝 {rejects} 格）零违规"
                  + (viol.Count > 0 ? " 例如: " + string.Join(" | ", viol.ConvertAll(s => s).GetRange(0, Math.Min(12, viol.Count))) : ""));

            // HEIF 专项：本工具链**无写入通路**（实测 Unknown format 'heif'），能力表必须全保守；
            // 色彩层对其与 gif/jxr 同类：无色彩通路 ⇒ 只允许“直通+无标注”或 Reject，绝不得声称描述了广色域。
            foreach (var f in new[] { "heic", "heif" })
            {
                var capsH = CE.CapsFor(f);
                Check(!capsH.CanCarryArbitraryIcc && !capsH.CanCicp && !capsH.CanGenerateIccFromCicp,
                    $"{f} 能力表为保守（无写入通路，实测：ffmpeg Unknown format 'heif'）");
                Check(!capsH.HasColorPath, $"{f} 被认定无任何色彩管理通路（HasColorPath=false）");
                // 广色域源：不得产出“看起来标了 P3/2020”的结果
                var pWide = CE.Plan(new CI { Strategy = ST.Recommended, Source = p3Icc, Format = capsH });
                Check(pWide.Action == CA.Reject || pWide.OutPrimaries == null,
                    $"{f} 广色域源不得静默产出厂色域标注（实为 {pWide.Action}/p={pWide.OutPrimaries}）");
                // S2/S3 在无任何通路的容器上只能 Reject 或无标注直通（不能映射）
                var pS2 = CE.Plan(new CI { Strategy = ST.CarryIcc, Source = p3Icc, Format = capsH });
                Check(pS2.Action == CA.Reject || pS2.Matrix == null,
                    $"{f} + S2：要么 Reject 要么像素零改动（实为 {pS2.Action}）");
            }

            // C4c HLG 专项：**无 HDR 通路**的容器上不得“默默按 PQ 方式压暗”，必须 Reject；
            // 确实请求 tonemap 时，必须把“OOTF 未标定”写进建议（已知缺口可见，而不是隐性错）。
            // ⚠ **2026-09-16 契约更新**：`png`/`apng` **有** HDR 通路（经 **cICP**）——
            //   两条管线的 HDR→PNG 产物实测都写着 `smpte2084,bt2020`，且**逐字节相同**。
            //   故它们不再属于"无 HDR 通路"组，改列正向断言（否则本断言锁的是过时的能力表）。
            foreach (var f in new[] { "tiff", "webp", "gif" })
            {
                var hp = CE.Plan(new CI { Strategy = ST.Recommended, Source = hlg2020, Format = CE.CapsFor(f) });
                Check(hp.Action == CA.Reject, $"HLG × {f}：无 HDR 通路 ⇒ Reject（实为 {hp.Action}）");
            }
            foreach (var f in new[] { "png", "apng" })
            {
                var hp = CE.Plan(new CI { Strategy = ST.Recommended, Source = hlg2020, Format = CE.CapsFor(f) });
                Check(hp.Action != CA.Reject, $"HLG × {f}：有 cICP 通路 ⇒ 不得 Reject（实为 {hp.Action}）");
                Check(CE.CapsFor(f).CanHdr, $"{f} 的能力表须标 CanHdr=true（HDR 经 cICP 承载）");
            }
            foreach (var f in new[] { "avif", "jxl" })
            {
                var hh = CE.Plan(new CI { Strategy = ST.Recommended, Source = hlg2020, Format = CE.CapsFor(f) });
                Check(hh.Matrix == null && hh.ColorLoss == CL.None,
                    $"HLG × {f}：默认保持 HDR、不改像素（实为 {hh.Action}/{hh.ColorLoss}）");
                var ht = CE.Plan(new CI { Strategy = ST.Recommended, Source = hlg2020, Format = CE.CapsFor(f),
                                           HdrOutput = false, ToneMapRequested = true, ToneMap = FfmpegGui.Services.ColorMapping.ToneMapMode.Mobius });
                Check(ht.Tone is { Active: true } && ht.Advice.Contains("OOTF"),
                    $"HLG × {f} + 请求 tonemap：必须声明 OOTF 未标定（Advice={ht.Advice}）");
            }

            // C4b GainMap 专项：它是“输出结构”能力（仅 JPEG 族），不是色调曲线；
            // UI 的 IsVisible 不算门禁——契约层必须自拦，否则变成“以为有 HDR、实则被压暗”的静默降级。
            foreach (var f in new[] { "png", "tiff", "webp", "avif", "jxl", "gif", "heic" })
            {
                var gm = CE.Plan(new CI { Strategy = ST.Recommended, Source = pq2020, Format = CE.CapsFor(f), GainMapRequested = true });
                Check(gm.Action == CA.Reject && gm.Advice.Length > 0,
                    $"GainMap × {f}：必须 Reject+建议（实为 {gm.Action}）");
            }
            foreach (var f in new[] { "jpg", "jpeg", "jpegli" })
            {
                var capsG = CE.CapsFor(f);
                Check(capsG.SupportsGainMap, $"{f} 能力表标记 SupportsGainMap");
                var s2g = CE.Plan(new CI { Strategy = ST.CarryIcc, Source = pq2020, Format = capsG, GainMapRequested = true });
                Check(s2g.Override == OR.GainMapRequiresBaseMapping && s2g.EffectiveStrategy != ST.CarryIcc,
                    $"GainMap × {f}：S2 被显式覆盖为 {s2g.EffectiveStrategy}（原因 {s2g.Override}）");
            }
            // 正向（**2026-09-16 契约更新**）：GainMap 已接入引擎 ⇒ JPEG 族的**有效**任务不得被规划层拒。
            // 用「HDR 源 + 显式 tonemap」是刻意的：这正是 `ColorIntentFactory` 对 HDR→JPEG 的真实装配
            //（`!caps.CanHdr` ⇒ 自动 hable），而 GainMap 的主用途正是「HDR 底图 → SDR 底图 + 增益图」。
            // 第二条把覆盖原因 `GainMapRequiresBaseMapping` 的**承诺**钉住：底图必须真的被映射（Action=Map），
            // 而不是"记了个原因却直通"。
            {
                var gmOk = CE.Plan(new CI { Strategy = ST.Recommended, Source = pq2020, Format = CE.CapsFor("jpg"),
                                            GainMapRequested = true, ToneMapRequested = true,
                                            ToneMap = FfmpegGui.Services.ColorMapping.ToneMapMode.Hable, HdrPeakNits = 1000 });
                Check(gmOk.Action != CA.Reject, $"GainMap × jpg + 显式 tonemap ⇒ 不得 Reject（实为 {gmOk.Action}）");
                Check(gmOk.Action == CA.Map, $"GainMap × jpg：底图确实被映射（Action={gmOk.Action}，兑现 GainMapRequiresBaseMapping）");
            }

            // C4d 色域压缩（GMO）专项（**2026-09-17 新增**）：`--color-gamut-map on` 的语义此前**零覆盖**。
            // 本组把「开关 → 决策 → 后端」三段钉在同一处（纯函数，无 IO）。
            // 刻意选 **BT.2020(SDR) → Display P3** 这一对：两侧都能按 CICP 命名（`h2Possible=true`）
            // ⇒ **越界本身**并不强制进程内后端；只有开了开关（`needsGmo`）才强制。
            // 这样才能用一个开关把「决策」与「后端」两条断言同时钉住 —— 若用 Adobe RGB 这类不可命名的源，
            // 后端恒为 InProcess，负控就变成空转。
            {
                var b2020 = CS.FromCicp("bt2020", "bt709", "BT.2020(SDR)")!;
                b2020.Confidence = DC.CicpTag;

                var gmo = MakeIntent(ST.Manual, b2020, "png", CSC.All);
                gmo.AllowGamutCompression = true;
                var pg = CE.Plan(gmo);
                Check(pg.Action != CA.Reject && pg.GamutOutsideDestination && pg.GamutMap,
                    $"GMO 正向前置：BT.2020 → Display P3 越界 + 开关开 ⇒ GamutMap 必须置位（实为 {pg.Action}/{pg.GamutMap}）");
                Check(pg.ColorLoss == CL.GamutCompression,
                    $"GMO 正向：开关开 + 源越界 ⇒ ColorLoss 必须为 {CL.GamutCompression}（实为 {pg.ColorLoss}）");
                Check(pg.Backend == CB.InProcess,
                    $"GMO 正向：色域压缩只能由进程内内核执行 ⇒ 后端必须为 {CB.InProcess}（实为 {pg.Backend}）");

                // 负控：只把**开关关掉**（其余一字不动）⇒ 必须回到 Clipping + zscale 后端。
                var clip = MakeIntent(ST.Manual, b2020, "png", CSC.All);
                clip.AllowGamutCompression = false;
                var pc = CE.Plan(clip);
                Check(pc.GamutOutsideDestination && !pc.GamutMap && pc.ColorLoss == CL.Clipping,
                    $"GMO 负控：开关关 ⇒ 必须回到 {CL.Clipping} 且 GamutMap=false（实为 {pc.ColorLoss}/{pc.GamutMap}）");
                Check(pc.Backend == CB.FfmpegFilter,
                    $"GMO 负控：未开压缩时越界本身不强制进程内后端（实为 {pc.Backend}）");
            }

            // C4c 策略覆盖留痕（**2026-09-16 新增**）：GainMap 接入引擎后，S3/S4 两格若不被覆盖就会
            // 被规划层 Reject（S3：JPEG 无 CICP 承载通路；S4：无显式目标），而基线（旧专用管线）**有产出**
            // ⇒ 那是功能回退。故 §2 覆盖① 从「仅 S2」扩展到**三条非 S1 策略**（S2 已在上面覆盖）。
            // 这组断言把「覆盖必须发生 **且** 必须留痕 **且** 不得改写声明值」钉住 —— 只靠
            // verify-color-strategy 的产出断言是间接的：覆盖被悄悄去掉时它不会立刻转红。
            {
                var capsJpg = CE.CapsFor("jpg");
                // ⚠ 必须带上 tonemap 请求：HDR 源 + SDR 容器若**不显式请求** tonemap，规划层会因另一条
                //   独立且正确的规则（"HDR 源 → SDR 结局需显式请求 tonemap"）Reject —— 那会**掩盖**本组
                //   要测的策略覆盖（首版断言就因此假红）。真实装配路径 `ColorIntentFactory` 对 HDR→JPEG
                //   本来就会自动置 tonemap（`!caps.CanHdr`），所以带上它才是忠实的构造。
                const double kPeak = 1000;
                // S3（仅 CICP）：JPEG 结构上不可能兑现 —— 无 CICP 承载通路，底图色域只能由底图 ICC 表达。
                var s3g = CE.Plan(new CI { Strategy = ST.BakeCicpOnly, Source = pq2020, Format = capsJpg,
                                           GainMapRequested = true, ToneMapRequested = true,
                                           ToneMap = FfmpegGui.Services.ColorMapping.ToneMapMode.Hable, HdrPeakNits = kPeak });
                Check(s3g.Action != CA.Reject, $"GainMap × jpg × S3：不得 Reject（实为 {s3g.Action}）");
                Check(s3g.Override == OR.GainMapRequiresBaseMapping,
                    $"GainMap × jpg × S3：覆盖原因必须是 {OR.GainMapRequiresBaseMapping}（实为 {s3g.Override}）");
                Check(s3g.EffectiveStrategy == ST.Recommended,
                    $"GainMap × jpg × S3：生效策略应为 {ST.Recommended}（实为 {s3g.EffectiveStrategy}）");
                Check(s3g.DeclaredStrategy == ST.BakeCicpOnly,
                    $"GainMap × jpg × S3：**声明值必须原样保留**（实为 {s3g.DeclaredStrategy}）");
                // S4（手动但未给任何字段）：目标由 JpegGainMapBaseGamut 决定 ⇒「没有显式目标」不是错误。
                var s4g = CE.Plan(new CI { Strategy = ST.Manual, Source = pq2020, Format = capsJpg,
                                           GainMapRequested = true, ToneMapRequested = true,
                                           ToneMap = FfmpegGui.Services.ColorMapping.ToneMapMode.Hable, HdrPeakNits = kPeak,
                                           Manual = FfmpegGui.Services.ColorMapping.ManualIntent.Create(false, null, null, null, null) });
                Check(s4g.Action != CA.Reject, $"GainMap × jpg × S4：不得 Reject（实为 {s4g.Action}）");
                Check(s4g.Override == OR.GainMapRequiresBaseMapping,
                    $"GainMap × jpg × S4：覆盖原因必须是 {OR.GainMapRequiresBaseMapping}（实为 {s4g.Override}）");
                // 负控（**关键**）：同样的 S3 + tonemap 但**不请求 GainMap** ⇒ 不得出现该覆盖。
                // 否则「非 S1 一律被覆盖」这种退化实现也能让上面 5 条全绿 ⇒ 它们就成了空转。
                var s3n = CE.Plan(new CI { Strategy = ST.BakeCicpOnly, Source = pq2020, Format = capsJpg,
                                           GainMapRequested = false, ToneMapRequested = true,
                                           ToneMap = FfmpegGui.Services.ColorMapping.ToneMapMode.Hable, HdrPeakNits = kPeak });
                Check(s3n.Override != OR.GainMapRequiresBaseMapping,
                    $"负控：S3 × jpg **不请求 GainMap** ⇒ 不得出现 {OR.GainMapRequiresBaseMapping}（实为 {s3n.Override}）");
            }

            // C12 PNG 标注策略：ICC/CICP 取舍由**四大策略**决定（纯函数，无 IO）。
            // 规范前提（PNG Third Edition）：优先级 cICP=1 > iCCP=2；且无 cICP 时至多一个 profile
            // ⇒ cICP 与 iCCP 允许并存，关键在“ICC 是谁的”。
            {
                // ⚠ 不得把枚举装成 object 再 ==（装箱后是引用相等，永远 false 且不报错）；
                //   也不能 `var X = SomeEnumType;`（类型不是值）⇒ 取实际成员作为局部常量。
                var W = FfmpegGui.Services.PngCicpService.PngLabelAction.WriteCicp;
                var SkipSrc = FfmpegGui.Services.PngCicpService.PngLabelAction.SkipSourceIcc;
                var SkipUn = FfmpegGui.Services.PngCicpService.PngLabelAction.SkipUnnameable;
                var SkipLabeled = FfmpegGui.Services.PngCicpService.PngLabelAction.SkipAlreadyLabeled;
                FfmpegGui.Services.PngCicpService.PngLabelAction DL(ST s, bool iccp, bool cicp, bool src, bool stdIcc, bool nameable)
                    => FfmpegGui.Services.PngCicpService.DecideLabel(s, iccp, cicp, src, stdIcc, nameable);
                Check(DL(ST.Recommended, false, false, false, false, true) == W,
                    "S1 无 ICC 且可命名 ⇒ 写 cICP");
                Check(DL(ST.Recommended, true, false, false, false, true) == W,
                    "S1 带自生成标准 ICC ⇒ 仍写 cICP（与 ICC 同义，并写安全）");
                Check(DL(ST.CarryIcc, true, false, false, true, true) == W,
                    "S2 源只有 ICC、但该 ICC 是标准色彩空间⇒ 写 cICP（两者同义，不丢精度）");
                Check(DL(ST.CarryIcc, true, false, false, false, true) == SkipSrc,
                    "S2 源只有 ICC、且 ICC 非标准（色度未命中）⇒ 不写（猜出的 cICP 会降级源 ICC）");
                Check(DL(ST.CarryIcc, true, false, true, false, true) == W,
                    "S2 且源自带 CICP ⇒ 必须携带（ICC+CICP 一起带，丢掉就是丢信息）");
                Check(DL(ST.CarryIcc, false, false, false, false, true) == W,
                    "S2 但源无 ICC ⇒ 写 cICP（契约里的“零改动直通/继承标签”）");
                Check(DL(ST.BakeCicpOnly, true, false, false, false, true) == W,
                    "S3 仅CICP ⇒ 必写 cICP（残留 iCCP 由调用方另行告警）");
                Check(DL(ST.Manual, true, false, false, false, true) == W,
                    "S4 手动 ⇒ 可命名即写（用户担保）");
                // 反向护栏：证明参数真在起作用，而不是恒真断言
                Check(DL(ST.Recommended, true, false, false, false, false) == SkipUn,
                    "不可命名（ProPhoto/Adobe）⇒ 宁缺勿错，ICC 作唯一描述");
                Check(DL(ST.Manual, false, true, false, false, true) == SkipLabeled,
                    "已有 cICP ⇒ 不重复写、不覆盖");
                Check(DL(ST.BakeCicpOnly, true, false, false, false, true) == W,
                    "S3 即使源无 CICP 也写（本意就是只留 CICP）");
                // ICC 曲线 → CICP 令牌：仅当与标准曲线等价才命名（不猜）。本文件已有 TC = TransferCurve 别名。
                Check(FfmpegGui.Services.FfmpegCommandBuilder.CicpTokenOfCurve(TC.Srgb) == "iec61966-2-1",
                    "曲线 sRGB ⇒ iec61966-2-1");
                Check(FfmpegGui.Services.FfmpegCommandBuilder.CicpTokenOfCurve(TC.Pq) == "smpte2084",
                    "曲线 PQ ⇒ smpte2084");
                Check(FfmpegGui.Services.FfmpegCommandBuilder.CicpTokenOfCurve(TC.Hlg) == "arib-std-b67",
                    "曲线 HLG ⇒ arib-std-b67");
                Check(FfmpegGui.Services.FfmpegCommandBuilder.CicpTokenOfCurve(TC.Gamma28) == null,
                    "γ2.8 无标准对应 ⇒ 不命名（不猜）");
                Check(FfmpegGui.Services.FfmpegCommandBuilder.CicpTokenOfCurve(null) == null,
                    "null 曲线（非中性/无法解析 profile）⇒ 不命名");
            }

            // C5 开关鲁棒性：三档 carry-scope 下关键格行为与定义一致，且契约仍成立
            static bool ScopeAllows(CSC sc, CS src) => sc switch
            {
                CSC.None => false,
                CSC.Unnameable => !src.IsCicpNameable,
                _ => true,
            };
            foreach (var scope in new[] { CSC.None, CSC.Unnameable, CSC.All })
                foreach (var (src, fmt) in new[] { (adobe, "png"), (prophoto, "avif"), (p3Icc, "tiff"), (srgbIcc, "png") })
                {
                    var it = MakeIntent(ST.Recommended, src, fmt, scope);
                    var p = CE.Plan(it);
                    bool carries = p.Action == CA.CarryIcc;
                    bool expect = CE.CapsFor(fmt).CanCarryArbitraryIcc && src.SourceIccPath != null && ScopeAllows(scope, src);
                    Check(carries == expect, $"C5 scope={scope} {src.Name}→{fmt}: carry={carries} 预期 {expect}（{p.Reason}）");
                    Check(p.Matrix == null || p.Action == CA.Map, $"C5 scope={scope} {src.Name}→{fmt}: 非映射格不得带矩阵");
                }

            // C6 确定性（同意图两次规划结果必须一致）；完整“直接路径 vs cjxl 管道”同构待 P7 接线后补
            for (int i = 0; i < 2; i++)
            {
                var a = CE.Plan(MakeIntent(ST.Recommended, p3Icc, "avif", CSC.All));
                var b = CE.Plan(MakeIntent(ST.Recommended, p3Icc, "avif", CSC.All));
                Check(a.Action == b.Action && a.Annot == b.Annot && a.ColorLoss == b.ColorLoss
                      && ((a.Matrix == null) == (b.Matrix == null)) && a.OutPrimaries == b.OutPrimaries,
                    "C6 同一 ColorIntent 两次规划结果一致（确定性）");
            }
            // C7 H2 口径黑名单：目标曲线 bt709/bt470bg/smpte240m 在 Std 口径下不得交给 H2；Zimg 口径下允许（对齐历史）
            var b709sdr = CS.FromCicp("bt709", "bt709", "BT.709 SDR");
            var p3b = CS.FromCicp("smpte432", "iec61966-2-1", "Display P3");
            if (b709sdr != null && p3b != null)
            {
                var polForce = new FfmpegGui.Services.ColorMapping.PlanPolicy
                { AllowApproximateCurve = false, ForceMapping = true, TargetExplicit = true };
                var pStd = CE.Plan(p3b, b709sdr, polForce);
                Check(pStd.Backend != CB.FfmpegFilter,
                    $"C7 Std 口径：目标 bt709 不得走 H2（实为 {pStd.Backend}）{pStd.Reason}");
                var polZ = new FfmpegGui.Services.ColorMapping.PlanPolicy
                { AllowApproximateCurve = false, ForceMapping = true, TargetExplicit = true, Transfer709 = F709.Zimg };
                var pZ = CE.Plan(p3b, b709sdr, polZ);
                Check(pZ.Backend == CB.FfmpegFilter, $"C7 Zimg 口径：允许回退到 H2 对齐历史产物（实为 {pZ.Backend}）");
                // 对照：非黑名单目标（sRGB 曲线）仍应优先 H2
                var pOk = CE.Plan(p3b, srgbTag, polForce);
                Check(pOk.Backend == CB.FfmpegFilter, $"C7 对照：目标 iec61966-2-1 仍可走 H2（实为 {pOk.Backend}）");
            }

            // C8 单入口检测器：必须抓到真入口，且**不得误杀**标注工具（iccgen/format/纯缩放/CLI 标注选项）
            var pH1 = new FfmpegGui.Services.ColorMapping.ColorTransformPlan { Action = CA.Map, Backend = CB.InProcess };
            var pCarry = new FfmpegGui.Services.ColorMapping.ColorTransformPlan { Action = CA.CarryIcc, Backend = CB.None };
            var pNoop = new FfmpegGui.Services.ColorMapping.ColorTransformPlan { Action = CA.None, Backend = CB.None };
            var pH2 = new FfmpegGui.Services.ColorMapping.ColorTransformPlan { Action = CA.Map, Backend = CB.FfmpegFilter };
            string zs = "-vf format=gbrpf32le,zscale=pin=bt709:tin=iec61966-2-1:min=gbr:p=smpte432:t=iec61966-2-1:m=bt709,format=rgb48le";
            Check(CE.FindPixelColorEntry(zs, pH1) != null, "C8 抓到双映射入口（H1 计划 + zscale 色彩链）");
            Check(CE.FindPixelColorEntry(zs, pCarry) != null, "C8 抓到违反：携带计划却带色彩链");
            Check(CE.FindPixelColorEntry(zs + "," + zs.Replace("zscale=", "colormatrix="), pH2) != null, "C8 抓到违反：H2 计划但出现两条色彩入口");
            // 不误杀：iccgen（标注生成器）/ format= / 纯缩放 / -colorspace 等 CLI 标注选项 / 无参 zscale
            Check(CE.FindPixelColorEntry("-vf format=rgb48le,iccgen=color_primaries=smpte432:color_trc=iec61966-2-1:force=1 -colorspace bt709 -color_trc bt709", pNoop) == null,
                "C8 无误杀：iccgen + CLI 标注选项不算像素入口");
            Check(CE.FindPixelColorEntry("-vf scale=800:-1,format=rgb48le,zscale=3840:2160,iccgen", pH2) == null,
                "C8 无误杀：纯缩放（无色彩参数）不算入口（H2 单链合法）");
            Check(CE.FindPixelColorEntry("-i in.png -c:v libjxl out.jxl", pCarry) == null, "C8 无误杀：无色彩滤镜的命令行");

            // C9 几何凸包包含判据（取代旧的“矩阵元素全非负”近似）：已知关系必须全部正确
            var gSrgb = CS.FromCicp("bt709", "iec61966-2-1", "sRGB");
            var gP3 = CS.FromCicp("smpte432", "iec61966-2-1", "Display P3");
            var g2020 = CS.FromCicp("bt2020", "bt709", "BT.2020");
            var gPro = CS.FromNamedSpace("ProPhoto RGB");
            if (gSrgb != null && gP3 != null && g2020 != null && gPro != null)
            {
                (bool? got, bool want, string what)[] cases = {
                    (CS.ContainsGamut(gSrgb, gP3), true,  "sRGB ⊆ Display P3"),
                    (CS.ContainsGamut(gSrgb, g2020), true, "sRGB ⊆ BT.2020"),
                    // ⚠ 不是笔误："BT.2020 包住 P3" 是**近似说法**。实测/手算均表明：
                    //   2020 的 G→R 边在 x=0.680 处 y≈0.3183，而 Display P3 红原色 y=0.320
                    //   ⇒ P3 红原色超出 2020 三角形约 0.0017（近共线），严格几何上 P3 ⊄ BT.2020。
                    //   几何判据能抓到这个差正是它优于“矩阵元素非负”近似的地方。
                    (CS.ContainsGamut(gP3, g2020), false,   "Display P3 ⊄ BT.2020（红原色超出≈0.0017）"),
                    (CS.ContainsGamut(gP3, gSrgb), false,  "Display P3 ⊄ sRGB"),
                    (CS.ContainsGamut(g2020, gP3), false,  "BT.2020 ⊄ Display P3"),
                    (CS.ContainsGamut(g2020, gSrgb), false,"BT.2020 ⊄ sRGB"),
                    (CS.ContainsGamut(gPro, g2020), false, "ProPhoto ⊄ BT.2020"),
                    (CS.ContainsGamut(gPro, gSrgb), false, "ProPhoto ⊄ sRGB"),
                    (CS.ContainsGamut(gSrgb, gSrgb), true, "sRGB ⊆ sRGB（自身，边界松弛）"),
                };
                foreach (var (got, want, what) in cases)
                {
                    Check(got == want, $"C9 {what}（实={got}）");
                    if (got != want)
                    {
                        // 失败时输出实际参与判定的 xy（D50 列归一）与目标三角形，便于区分"几何真值"与"实现错"
                        static string Fmt(double[]? v) => v == null ? "null"
                            : $"R({v[0]:F5},{v[1]:F5}) G({v[2]:F5},{v[3]:F5}) B({v[4]:F5},{v[5]:F5})";
                        var srcD = what.StartsWith("sRGB ⊆") ? gSrgb : what.StartsWith("Display P3 ⊆") ? gP3
                               : what.StartsWith("BT.2020 ⊄") ? g2020 : gPro;
                        var dstD = what.EndsWith("Display P3") && what.Contains("⊆") ? gP3
                                 : what.Contains("BT.2020") && !what.StartsWith("BT.2020") ? g2020
                                 : what.Contains("sRGB") && what.StartsWith("ProPhoto") ? gSrgb
                                 : what.Contains("sRGB（自身") ? gSrgb : g2020;
                        Console.WriteLine($"      源={Fmt(srcD?.PrimariesXyD50())}");
                        Console.WriteLine($"      标={Fmt(dstD?.PrimariesXyD50())}");
                    }
                }
            }

            // C10 抖动确定性：相位取绝对坐标 ⇒ 整幅与逐条 tile 必须逐字节一致
            {
                int dw = 32, dh = 32;
                var srcA = new byte[dw * dh * 4];
                for (int i = 0; i < dw * dh; i++)
                {
                    byte g = (byte)(i % 251);
                    srcA[i * 4] = g; srcA[i * 4 + 1] = (byte)(255 - g); srcA[i * 4 + 2] = (byte)((i * 7) % 256); srcA[i * 4 + 3] = 255;
                }
                var specD = Spec("bt709", "iec61966-2-1", "smpte432", "iec61966-2-1");
                specD.Dither = FfmpegGui.Services.ColorMapping.DitherMode.Ordered4x4;
                var eotf8 = TC.Srgb.BuildEotfTable8();
                var whole = new byte[srcA.Length];
                CK.TransformRgba8(srcA, whole, specD, eotf8, dw, dh);
                var tiled = new byte[srcA.Length];
                for (int y0 = 0; y0 < dh; y0 += 7)
                {
                    int hh = Math.Min(7, dh - y0);
                    CK.TransformRgba8(srcA.AsSpan(y0 * dw * 4, hh * dw * 4), tiled.AsSpan(y0 * dw * 4, hh * dw * 4),
                                      specD, eotf8, dw, hh, null, 0, y0);
                }
                bool same = true; int first = -1;
                for (int i = 0; i < whole.Length; i++) if (whole[i] != tiled[i]) { same = false; first = i; break; }
                Check(same, $"C10 抖动：整幅 vs 逐条 tile（originY）逐字节一致{(same ? "" : $"，首个差异在字节 {first}")}");
                // 确定性：两次运行结果相同（无 RNG）
                var again = new byte[srcA.Length];
                CK.TransformRgba8(srcA, again, specD, eotf8, dw, dh);
                Check(whole.AsSpan().SequenceEqual(again), "C10 抖动：同输入两次运行逐字节一致（无 RNG）");
            }

            // C11 rgb48→rgb8 降位内核：绝对相位 ⇒ tile 切分逐字节一致；拑动后渐变仍单调
            {
                int cw = 64, ch = 8;
                var src16 = new byte[cw * ch * 6];
                for (int i = 0; i < cw * ch; i++)
                {
                    int v = (int)Math.Round(i / (double)(cw * ch - 1) * 65535.0);
                    for (int c = 0; c < 3; c++)
                    {
                        src16[i * 6 + c * 2] = (byte)(v & 0xFF); src16[i * 6 + c * 2 + 1] = (byte)(v >> 8);
                    }
                }
                var spec8 = Spec("bt709", "iec61966-2-1", "smpte432", "iec61966-2-1");
                spec8.Dither = FfmpegGui.Services.ColorMapping.DitherMode.Ordered4x4;
                var tS = CK.BuildTables(spec8.SrcCurve); var tD = CK.BuildTables(spec8.DstCurve);
                var whole8 = new byte[cw * ch * 3];
                CK.TransformRgb48leToRgb8(src16, whole8, spec8, cw * ch, tS, tD, cw, 0);
                var tiled8 = new byte[cw * ch * 3];
                for (int y0 = 0; y0 < ch; y0 += 3)
                {
                    int hh = Math.Min(3, ch - y0);
                    CK.TransformRgb48leToRgb8(src16.AsSpan(y0 * cw * 6, hh * cw * 6), tiled8.AsSpan(y0 * cw * 3, hh * cw * 3),
                                              spec8, hh * cw, tS, tD, cw, y0 * cw);
                }
                bool same8 = true; int diffAt = -1;
                for (int i = 0; i < whole8.Length; i++) if (whole8[i] != tiled8[i]) { same8 = false; diffAt = i; break; }
                Check(same8, $"C11 8bit 降位：整幅 vs 逐条 tile（originIndex）逐字节一致{(same8 ? "" : $"，首差 {diffAt}")}");
                // 单调不降：拑动后沿渐变 R 不应前进后退（允许相邻等值，不允许下降超过 1）
                int worstDrop = 0;
                for (int i = 1; i < cw * ch; i++)
                {
                    int d = whole8[i * 3] - whole8[(i - 1) * 3];
                    if (d < worstDrop) worstDrop = d;
                }
                Check(worstDrop >= -1, $"C11 拑动后渐变仍单调（最大回落 {worstDrop} LSB，仅允许拖动带来的 ±1）");
                // 精度：与 16bit 路径的 8bit 化结果相比，误差 ≤1 LSB（拑动只应搬动 1 个台阶）
                var specN = Spec("bt709", "iec61966-2-1", "smpte432", "iec61966-2-1");
                var mid16 = new byte[src16.Length];
                CK.TransformRgb48le(src16, mid16, specN, cw * ch, tS, tD);
                int maxDev = 0;
                for (int i = 0; i < cw * ch; i++)
                {
                    int plain = (int)Math.Round((mid16[i * 6] + mid16[i * 6 + 1] * 256) / 65535.0 * 255.0);
                    int dev = Math.Abs(plain - whole8[i * 3]);
                    if (dev > maxDev) maxDev = dev;
                }
                Check(maxDev <= 1, $"C11 拑动幅度受控：与无拑动降位最大偏差 {maxDev} LSB（应 ≤1）");
            }

            Console.WriteLine($"  contract: 共 {cells} 格，覆盖豁免 {exempted}，拒绝 {rejects}");
            // 拒绝必须可审计：按策略与原因分组列出（防止“过度拒绝”被一个总数掩盖）
            foreach (var (s, n) in rejectByStrategy)
                Console.WriteLine($"    拒绝 {s,-12} {n,3} 格");
            foreach (var (r, n) in rejectReasons)
                Console.WriteLine($"    · {n,3}× {r}");
        }

        /// <summary>
        /// bitdepth —— 端到端验证 Plan.OutBitDepth 真的驱动了出口位深（否则新接的 8bit 内核就是死代码）。
        /// 用 Adobe→sRGB（不可命名⇒必走 H1）强制发生变换，再读 PNG IHDR 的位深字节。
        /// </summary>
        private static async Task ProbeBitDepth(string[] args)
        {
            string inPng = args.Length > 1 ? args[1] : "tests/output/validate/bit_in.png";
            var ff = FfmpegGui.Services.AppSettingsService.Current.FfmpegPath;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(inPng))!);
            // 造一个 16bit 输入图（内容无关，只验位宽）
            if (!File.Exists(inPng))
            {
                var psi = new System.Diagnostics.ProcessStartInfo(ff!,
                    $"-y -hide_banner -loglevel error -f lavfi -i \"smptehdbars=s=64x48\" -pix_fmt rgb48le -frames:v 1 \"{inPng}\"")
                { RedirectStandardError = true, UseShellExecute = false };
                using var pr = System.Diagnostics.Process.Start(psi)!; pr.StandardError.ReadToEnd(); pr.WaitForExit();
            }
            Check(File.Exists(inPng) && new FileInfo(inPng).Length > 100, $"16bit 输入就绪 {inPng}");

            var adobe = CS.FromNamedSpace("Adobe RGB");
            var srgb = CS.FromCicp("bt709", "iec61966-2-1", "sRGB");
            if (adobe == null || srgb == null) { Check(false, "描述符构造失败"); return; }
            var spec = Spec("bt470bg", "bt470bg", "bt709", "iec61966-2-1");

            foreach (var (depth, expect) in new[] { (8, 8), (16, 16) })
            {
                var it = new CI { Strategy = ST.Recommended, Source = adobe, Target = srgb, Format = CE.CapsFor("png"),
                                  TargetBitDepth = depth, TargetExplicit = true, AllowApproximateCurve = false };
                var plan = CE.Plan(it);
                Check(plan.Action == CA.Map && plan.Backend == CB.InProcess,
                    $"depth={depth}: 用例需走 H1 映射（实为 {plan.Action}/{plan.Backend}）");
                Check(plan.OutBitDepth == expect, $"depth={depth}: Plan.OutBitDepth 应为 {expect}（实为 {plan.OutBitDepth}）");
                Check(plan.NeedsDither == (depth == 8), $"depth={depth}: NeedsDither 应与位深一致（实={plan.NeedsDither}）");

                string outPng = $"tests/output/validate/bit_{depth}.png";
                if (File.Exists(outPng)) File.Delete(outPng);
                var st = await FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(inPng, outPng, spec, plan, null, default, 2);
                Check(File.Exists(outPng) && new FileInfo(outPng).Length > 100, $"depth={depth}: 端到端产出非空 PNG（{st.ProcessMs}ms）");
                int bd = PngBitDepth(outPng);
                Check(bd == expect, $"depth={depth}: 输出 PNG IHDR 位深应为 {expect}（实为 {bd}）");
            }
        }

        /// <summary>直接读 PNG IHDR 的 bit depth 字节（宽 4 + 高 4 后的那一个）。</summary>
        private static int PngBitDepth(string path)
        {
            var b = File.ReadAllBytes(path);
            return b.Length > 25 ? b[24] : -1;
        }

        // ─────────────────────────── verdict（S5′）───────────────────────────

        /// <summary>
        /// verdict —— **纯函数级**裁决断言（S5′）：每个已填的 <c>Degradation.Code</c> 一条**正向**
        /// （构造对应 <c>ColorIntent</c>，断言 <c>Kind</c> 且 <c>Degradations</c> 含该 <c>Code</c>）
        /// 与一条**负控**（只改一个变量让该损失消失，断言该 <c>Code</c> 不再出现 ⇒ 证明断言不是空转）；
        /// 外加 <c>Kind</c>/<c>Degradations</c> 的自洽性（<c>Kind==Proceed ⇔ Degradations.Count==0</c>）。
        /// <para>⚠ 门禁判据一律打 <c>Degradation.Code</c>，**禁止**匹配 <c>Text</c> 自由文本。</para>
        /// <para>⚠ 只读 <c>ColorMappingEngine.Plan</c> 的纯函数输出，不做任何 IO、不启动子进程。</para>
        /// </summary>
        private static void ProbeVerdict()
        {
            CS Mk(string prim, string trc, string name, string? icc, DC conf)
            {
                var d = CS.FromCicp(prim, trc, name)!;
                if (icc != null) { d.SourceIccPath = icc; d.IsIccDerived = true; }
                d.Confidence = conf;
                return d;
            }
            var srgbTag = Mk("bt709", "iec61966-2-1", "sRGB(仅标签)", null, DC.CicpTag);
            var srgbIcc = Mk("bt709", "iec61966-2-1", "sRGB(带ICC)", "t.srgb.icc", DC.IccMetric);
            var p3Icc = Mk("smpte432", "iec61966-2-1", "Display P3(带ICC)", "t.p3.icc", DC.IccMetric);
            var adobe = CS.FromNamedSpace("Adobe RGB")!;
            adobe.SourceIccPath = "t.adobe.icc"; adobe.Confidence = DC.IccMetric;
            var bt2020Sdr = CS.FromCicp("bt2020", "bt709", "BT.2020(SDR)")!;
            bt2020Sdr.Confidence = DC.CicpTag;
            // 与 `contract` 网格同源的其余三类源：HDR-PQ / HDR-HLG / 无标签（按惯例假定）。
            var pq2020 = CS.FromCicp("bt2020", "smpte2084", "BT.2100 PQ", nitsOfLinearOne: TC.PqPeakNits)!;
            pq2020.Confidence = DC.CicpTag;
            var hlg2020 = CS.FromCicp("bt2020", "arib-std-b67", "BT.2100 HLG", nitsOfLinearOne: 1000.0)!;
            hlg2020.Confidence = DC.CicpTag;
            var untagged = FfmpegGui.Services.ColorMapping.ColorSpaceIdentify.AssumeByConvention()!;

            string[] CodesOf(CP pl)
            {
                var v = pl.Verdict;
                if (v == null) return Array.Empty<string>();
                var a = new string[v.Degradations.Count];
                for (int i = 0; i < a.Length; i++) a[i] = v.Degradations[i].Code;
                return a;
            }
            bool Has(CP pl, string code) => Array.IndexOf(CodesOf(pl), code) >= 0;
            string Show(CP pl) => $"{pl.Action}/kind={pl.Verdict?.Kind}/bit={pl.OutBitDepth}/codes=[{string.Join(",", CodesOf(pl))}]";

            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            void Note(CP pl) { foreach (var c in CodesOf(pl)) seen.Add(c); }

            // ══════════ ① BitDepthReduced：出口位深 16 → 8（发生变换 + 8bit 容器上限）══════════
            // 判据来源：`ColorStrategyPlanning.PlanWithPolicy` 的 OutBitDepth 赋值
            //   （Action==Map && ColorLoss!=None && TargetBitDepth<=8 && !Requires16BitIntermediate && RgbNative）。
            // 用例：S4 手动 Adobe RGB → Display P3、输出 PNG（RgbNative、上限 16bit 但目标位深 8）。
            CI ManualToP3(string fmt, int depth, CS? target = null) => new()
            {
                Strategy = ST.Manual, Source = adobe, Target = target ?? p3Icc, TargetExplicit = true,
                Format = CE.CapsFor(fmt), TargetBitDepth = depth, AllowApproximateCurve = false,
                Manual = MI.Create(true, "Display P3", "smpte432", "iec61966-2-1", null),
            };
            {
                var pos = CE.Plan(ManualToP3("png", 8));
                Note(pos);
                Check(pos.Action == CA.Map && pos.OutBitDepth == 8,
                    $"BitDepthReduced 正向前置：需 Map + OutBitDepth=8（实为 {Show(pos)}）");
                Check(pos.Verdict?.Kind == VK.ProceedWithDegradation && Has(pos, DG.BitDepthReduced),
                    $"BitDepthReduced 正向：{DG.BitDepthReduced} 必须被登记且 Kind={VK.ProceedWithDegradation}（实为 {Show(pos)}）");

                // 负控：只把**目标位深 8 → 16**（其余一字不动）⇒ 该损失必须消失，且仍然产出。
                var neg = CE.Plan(ManualToP3("png", 16));
                Note(neg);
                Check(neg.Action == CA.Map && neg.OutBitDepth == 16,
                    $"BitDepthReduced 负控前置：16bit 目标仍须产出且 OutBitDepth=16（实为 {Show(neg)}）");
                Check(!Has(neg, DG.BitDepthReduced),
                    $"BitDepthReduced 负控：目标位深 8→16 后 {DG.BitDepthReduced} 必须消失（实为 {Show(neg)}）");
            }

            // ══════════ ② GamutClipped：源色域超出目标（裁切/GMO）══════════
            // 判据来源：`ColorTransformPlan.GamutOutsideDestination`（PlanCore 里由 GamutContainedIn 置位）。
            {
                var pos = CE.Plan(ManualToP3("png", 8));
                Check(pos.GamutOutsideDestination && Has(pos, DG.GamutClipped),
                    $"GamutClipped 正向：Adobe RGB → Display P3 越界必须登记 {DG.GamutClipped}（实为 {Show(pos)}）");

                // 负控：只把**目标改成超集 BT.2020(SDR)**（其余一字不动）⇒ 越界消失，该 Code 必须消失。
                var neg = CE.Plan(ManualToP3("png", 8, bt2020Sdr));
                Note(neg);
                Check(neg.Action != CA.Reject,
                    $"GamutClipped 负控前置：换超集目标仍须产出（实为 {Show(neg)}）");
                Check(!neg.GamutOutsideDestination && !Has(neg, DG.GamutClipped),
                    $"GamutClipped 负控：目标改为超集 BT.2020(SDR) 后 {DG.GamutClipped} 必须消失（实为 {Show(neg)}）");
            }

            // ══════════ ②′ GamutCompressed：开关开 + 源越界 ⇒ GMO 压缩（与 GamutClipped 互斥）══════════
            // 判据来源：`ColorTransformPlan.GamutMap`（PlanCore 的**单点置位** =
            //   `GamutOutsideDestination && policy.AllowGamutCompression && 目标亮度系数可取`）。
            // ⚠ 本组是本轮（2026-09-17）补的**零覆盖**：`GamutCompressed` 与 `ColorLoss=GamutCompression`
            //   此前没有任何门禁断言（`--color-gamut-map` 接进引擎后只靠 H1 后端日志间接可见）。
            {
                CI GmoOn(string fmt, int depth)
                {
                    var it = ManualToP3(fmt, depth);
                    it.AllowGamutCompression = true;
                    return it;
                }
                var pos = CE.Plan(GmoOn("png", 8));
                Note(pos);
                Check(pos.GamutOutsideDestination && pos.GamutMap,
                    $"GamutCompressed 正向前置：源越界 + 开关开 ⇒ GamutMap 必须置位（实为 {Show(pos)}）");
                Check(pos.Verdict?.Kind == VK.ProceedWithDegradation && Has(pos, DG.GamutCompressed),
                    $"GamutCompressed 正向：{DG.GamutCompressed} 必须被登记且 Kind={VK.ProceedWithDegradation}（实为 {Show(pos)}）");
                Check(pos.ColorLoss == CL.GamutCompression,
                    $"GamutCompressed 正向：ColorLoss 必须是 {CL.GamutCompression}（实为 {pos.ColorLoss}）");
                Check(!Has(pos, DG.GamutClipped),
                    $"GamutCompressed 正向：与 {DG.GamutClipped} 互斥，不得同时登记（实为 {Show(pos)}）");

                // 负控：只把**开关关掉**（其余一字不动）⇒ GamutCompressed 消失、GamutClipped 回来。
                // 这是「判据真的跟着开关走」的直接证据：若 GamutMap 被写成常量，本组必然转红。
                var neg = CE.Plan(ManualToP3("png", 8));
                Note(neg);
                Check(!neg.GamutMap && neg.GamutOutsideDestination,
                    $"GamutCompressed 负控前置：开关关 ⇒ GamutMap=false 且仍越界（实为 {Show(neg)}）");
                Check(!Has(neg, DG.GamutCompressed) && Has(neg, DG.GamutClipped),
                    $"GamutCompressed 负控：开关关后 {DG.GamutCompressed} 必须消失、{DG.GamutClipped} 必须回来（实为 {Show(neg)}）");

                // 越界判据必须**独立于**开关（本条是变异逼出来的）：把 `GamutOutsideDestination` 从
                // GamutMap 的判据里删掉（只留 AllowGamutCompression）时，上面 6 条**全绿**
                // —— 因为源越界那格照样压、开关关那格照样裁。只有"开关开 + **未越界**"这一格能分辨：
                // 它必须 `GamutMap=false`（否则"允许压缩"被误当成"必须压缩"，未越界内容也会被推上 H1 后端）。
                var inGamut = new CI
                {
                    Strategy = ST.Manual, Source = srgbTag, Target = p3Icc, TargetExplicit = true,
                    Format = CE.CapsFor("png"), TargetBitDepth = 8, AllowApproximateCurve = false,
                    AllowGamutCompression = true,
                    Manual = MI.Create(true, "Display P3", "smpte432", "iec61966-2-1", null),
                };
                var ig = CE.Plan(inGamut);
                Note(ig);
                Check(!ig.GamutOutsideDestination && !ig.GamutMap && ig.ColorLoss != CL.GamutCompression,
                    $"GamutCompressed 越界判据：开关开但源**未越界**（sRGB ⊆ Display P3）⇒ 不得置位 GamutMap（实为 {Show(ig)}）");
            }

            // ══════════ ③ UnlabeledAssumedSrgb：产物无标注、按隐含 sRGB 处理 ══════════
            // 判据来源：`ColorTransformPlan.UnlabeledAssumedSrgb`。
            // 用例：S2 携带 + sRGB 源（仅 CICP 标签、无 ICC）+ JPEG（无 CICP 通路、未标注即 sRGB 成立）
            //   ⇒ PlanCarry 结局 2b（无标注直通）。
            CI CarryJpg(CS src, string fmt) => new()
            {
                Strategy = ST.CarryIcc, Source = src, Format = CE.CapsFor(fmt),
                TargetBitDepth = 8, AllowApproximateCurve = false,
            };
            {
                var pos = CE.Plan(CarryJpg(srgbTag, "jpg"));
                Note(pos);
                Check(pos.Action == CA.None && pos.UnlabeledAssumedSrgb && Has(pos, DG.UnlabeledAssumedSrgb),
                    $"UnlabeledAssumedSrgb 正向：S2 × sRGB标签源 × jpg 必须登记该 Code（实为 {Show(pos)}）");

                // 负控：只把**容器 jpg → png**（可写 CICP）⇒ 走"继承 CICP 标签"形态，该 Code 必须消失。
                var neg = CE.Plan(CarryJpg(srgbTag, "png"));
                Note(neg);
                Check(neg.Action != CA.Reject && !neg.UnlabeledAssumedSrgb && !Has(neg, DG.UnlabeledAssumedSrgb),
                    $"UnlabeledAssumedSrgb 负控：容器 jpg→png（可写 CICP）后该 Code 必须消失（实为 {Show(neg)}）");
            }

            // ══════════ ④ NoIccCarrier：源有 ICC 但产物不附 ICC ══════════
            // 判据来源：`!AttachIcc && Src.SourceIccPath != null`（已产出的格）。
            // 用例：S3 仅 CICP + Display P3（带 ICC）+ AVIF（可写 CICP；源已可 CICP 精确表达 ⇒ 不改像素）
            //   —— 刻意选 **AVIF**（CanCarryArbitraryIcc=true）以**隔离** NoIccCarrier，
            //      避免与 ContainerCannotCarryIcc 同时命中（后者另有用例）。
            {
                var pos = CE.Plan(new CI
                {
                    Strategy = ST.BakeCicpOnly, Source = p3Icc, Format = CE.CapsFor("avif"),
                    TargetBitDepth = 8, AllowApproximateCurve = false,
                });
                Note(pos);
                // ⚠ 前置条件里的"源带 ICC"必须看**意图的源描述符**：`pos.Src` 在 PlanCicpOnly 的
                //   等价分支返回的是 Fresh() 计划（Src==null）⇒ 用 pos.Src 会把前置写成假红。
                Check(pos.Action != CA.Reject && !pos.AttachIcc && p3Icc.SourceIccPath != null,
                    $"NoIccCarrier 正向前置：须产出、不附 ICC、且源带 ICC（实为 {Show(pos)}）");
                Check(pos.Verdict?.Kind == VK.ProceedWithDegradation && Has(pos, DG.NoIccCarrier),
                    $"NoIccCarrier 正向：{DG.NoIccCarrier} 必须被登记（实为 {Show(pos)}）");
                Check(!Has(pos, DG.ContainerCannotCarryIcc),
                    $"NoIccCarrier 用例须隔离：AVIF 可携带任意 ICC ⇒ 不得同时命中 {DG.ContainerCannotCarryIcc}（实为 {Show(pos)}）");

                // 负控：只把**源从"带 ICC"换成"仅 CICP 标签"**（其余一字不动）⇒ 该 Code 必须消失。
                var neg = CE.Plan(new CI
                {
                    Strategy = ST.BakeCicpOnly, Source = srgbTag, Format = CE.CapsFor("avif"),
                    TargetBitDepth = 8, AllowApproximateCurve = false,
                });
                Note(neg);
                Check(neg.Action != CA.Reject && !Has(neg, DG.NoIccCarrier),
                    $"NoIccCarrier 负控：源改为无 ICC（仅 CICP 标签）后该 Code 必须消失（实为 {Show(neg)}）");
            }

            // ══════════ ⑤ ContainerCannotCarryIcc：容器能力表判 `!CanCarryArbitraryIcc` ══════════
            // 判据来源：`PlanPolicy.Format.CanCarryArbitraryIcc == false`（能力表事实，非从计划字段反猜）。
            // 用例：S1 推荐 + sRGB（带 ICC）+ GIF（无任何色彩管理通路）⇒ 映射到 sRGB 工作空间、无标注输出。
            CI RecommendedGif(CS src, string fmt) => new()
            {
                Strategy = ST.Recommended, Source = src, Format = CE.CapsFor(fmt),
                TargetBitDepth = 8, AllowApproximateCurve = false, CarryScope = CSC.All,
            };
            {
                var pos = CE.Plan(RecommendedGif(srgbIcc, "gif"));
                Note(pos);
                Check(pos.Action != CA.Reject && !CE.CapsFor("gif").CanCarryArbitraryIcc,
                    $"ContainerCannotCarryIcc 正向前置：须产出且能力表判不可携带（实为 {Show(pos)}）");
                Check(Has(pos, DG.ContainerCannotCarryIcc) && Has(pos, DG.NoIccCarrier),
                    $"ContainerCannotCarryIcc 正向：必须同时登记 {DG.ContainerCannotCarryIcc}（结构性归因）"
                    + $"与 {DG.NoIccCarrier}（已发生的损失）（实为 {Show(pos)}）");

                // 负控：只把**容器 gif → png**（可携带任意 ICC）⇒ 改走 L1 携带，两个 Code 都必须消失。
                var neg = CE.Plan(RecommendedGif(srgbIcc, "png"));
                Note(neg);
                Check(neg.AttachIcc && neg.IccPath != null,
                    $"ContainerCannotCarryIcc 负控前置：png 应改走 L1 携带（实为 {Show(neg)}）");
                Check(!Has(neg, DG.ContainerCannotCarryIcc) && !Has(neg, DG.NoIccCarrier),
                    $"ContainerCannotCarryIcc 负控：容器 gif→png 后该 Code 与 {DG.NoIccCarrier} 都必须消失（实为 {Show(neg)}）");
            }

            // ══════════ ⑥ Reject 格**不得**登记任何损失（Degradations 的语义是"已发生"）══════════
            // 用例：S3 仅 CICP + Display P3（带 ICC）+ JPEG —— 契约不可兑现（JPEG 无 CICP 通路）⇒ Reject。
            {
                var rej = CE.Plan(new CI
                {
                    Strategy = ST.BakeCicpOnly, Source = p3Icc, Format = CE.CapsFor("jpg"),
                    TargetBitDepth = 8, AllowApproximateCurve = false,
                });
                Note(rej);
                var rv = rej.Verdict!;
                Check(rej.Action == CA.Reject, $"Reject 用例前置：S3 × P3带ICC × jpg 应 Reject（实为 {Show(rej)}）");
                Check(rv.Kind == VK.Reject && rv.Degradations.Count == 0,
                    $"Reject 格不得登记 Degradations（实为 {Show(rej)}）");
                Check(rv.Alternatives.Count > 0,
                    $"Reject 格应给出结构化 Alternatives（实为 {rv.Alternatives.Count} 条）");
            }

            // ══════════ ⑥′ ToneMapNotAppliedToGainMap：GainMap 出口**不消费**计划层 tonemap ══════════
            // 判据来源：`CollectDegradations` 末条（`p.GainMap && it.ToneMapRequested && !it.ToneMapAuto`）。
            // 用例（= 实测的「误伤」格）：**SDR 源** + 用户**显式** tonemap + GainMap。
            //   改动前该组合在 `ColorIntentFactory` 的 tonemap 装配门被 Reject（连 GainMap 出口都没进）；
            //   现在放行并由本 Code 诚实登记「该选项未生效」（产出不变，只有 Kind 翻成降级）。
            CI GainMapWithToneMap(CS src, bool gm, bool auto) => new()
            {
                Strategy = ST.Recommended, Source = src, Format = CE.CapsFor("jpg"),
                TargetBitDepth = 8, AllowApproximateCurve = false, CarryScope = CSC.All,
                GainMapRequested = gm, ToneMapRequested = true,
                ToneMap = FfmpegGui.Services.ColorMapping.ToneMapMode.Hable, ToneMapAuto = auto,
            };
            {
                var pos = CE.Plan(GainMapWithToneMap(srgbTag, true, false));
                Note(pos);
                Check(pos.GainMap && pos.Action != CA.Reject,
                    $"ToneMapNotAppliedToGainMap 正向前置：须为 GainMap 出口且产出（实为 {Show(pos)}）");
                Check(pos.Verdict?.Kind == VK.ProceedWithDegradation && Has(pos, DG.ToneMapNotAppliedToGainMap),
                    $"ToneMapNotAppliedToGainMap 正向：{DG.ToneMapNotAppliedToGainMap} 必须被登记且 "
                    + $"Kind={VK.ProceedWithDegradation}（实为 {Show(pos)}）");

                // 负控①：只把 **GainMap 关掉**（其余一字不动）⇒ 该 Code 必须消失。
                //   这是「判据真的跟着 GainMap 位走」的直接证据：若判据被写成只看 ToneMapRequested，本组必红。
                var neg1 = CE.Plan(GainMapWithToneMap(srgbTag, false, false));
                Note(neg1);
                Check(!neg1.GainMap && !Has(neg1, DG.ToneMapNotAppliedToGainMap),
                    $"ToneMapNotAppliedToGainMap 负控①：非 GainMap 格不得登记该 Code（实为 {Show(neg1)}）");

                // 负控②：只把 **ToneMapAuto 打开**（GainMap 仍开）⇒ 该 Code 必须消失。
                //   理由：自动兜底（HDR 源 + 容器不支持 HDR）的目的就是「把 HDR 压成可看的 SDR 底图」，
                //   而 GainMap 出口的分段 Reinhard 正是干这件事 ⇒ 目的已达成，不构成损失。
                //   少了这条，「每一次 HDR→JPEG GainMap 都被误报成降级」不会被任何断言发现。
                var neg2 = CE.Plan(GainMapWithToneMap(srgbTag, true, true));
                Note(neg2);
                Check(neg2.GainMap && !Has(neg2, DG.ToneMapNotAppliedToGainMap),
                    $"ToneMapNotAppliedToGainMap 负控②：ToneMapAuto 格不得登记该 Code（实为 {Show(neg2)}）");
            }

            // ══════════ ⑦ 自洽性 + 覆盖面（对 contract 同源网格全格断言）══════════
            var formats = new[] { "png", "jpg", "tiff", "webp", "avif", "jxl", "gif", "heic" };
            var sources = new (string Name, CS D)[]
                { ("sRGB仅标签", srgbTag), ("sRGB带ICC", srgbIcc), ("P3带ICC", p3Icc), ("HDR-PQ", pq2020),
                  ("HDR-HLG", hlg2020), ("Adobe带ICC", adobe), ("无标签", untagged) };
            var strategies = new[] { ST.Recommended, ST.CarryIcc, ST.BakeCicpOnly, ST.Manual };
            var viol = new System.Collections.Generic.List<string>();
            int cells = 0, degradated = 0, produced = 0, rejected = 0, producedEmpty = 0;
            foreach (var st in strategies)
                foreach (var fmt in formats)
                    foreach (var (sname, src) in sources)
                    {
                        var it = new CI
                        {
                            Strategy = st, Source = src, Format = CE.CapsFor(fmt),
                            TargetBitDepth = 8, AllowApproximateCurve = false, CarryScope = CSC.All,
                            Manual = st == ST.Manual ? MI.Create(true, "Display P3", "smpte432", "iec61966-2-1", null)
                                                     : MI.Create(false, null, null, null, null),
                        };
                        if (st == ST.Manual) { it.TargetExplicit = true; it.Target = p3Icc; }
                        var p = CE.Plan(it);
                        cells++;
                        Note(p);
                        var v = p.Verdict;
                        if (v == null) { viol.Add($"[{st}/{fmt}/{sname}] Verdict 为 null"); continue; }
                        if (v.Kind == VK.ProceedWithDegradation) degradated++;
                        if (p.Action == CA.Reject) rejected++; else produced++;
                        void V(bool ok, string msg) { if (!ok) viol.Add($"[{st}/{fmt}/{sname}] {msg}"); }
                        // 自洽（三条互相钉住；**不**写成 `Kind==Proceed ⇔ Count==0`，
                        // 因为 Reject 格同样是 Count==0 却不是 Proceed）：
                        //   ① ProceedWithDegradation ⇔ 非空；
                        //   ② Proceed ⇒ 空（即"Proceed + 非空"这一不自洽状态被禁掉）；
                        //   ③ Reject ⇔ Action==Reject，且 Reject 格必须为空。
                        V((v.Kind == VK.ProceedWithDegradation) == (v.Degradations.Count > 0),
                          $"Kind={v.Kind} 与 Degradations.Count={v.Degradations.Count} 不自洽");
                        V(v.Kind != VK.Proceed || v.Degradations.Count == 0,
                          $"Kind=Proceed 却带 {v.Degradations.Count} 条 Degradations（不自洽状态）");
                        V((v.Kind == VK.Reject) == (p.Action == CA.Reject),
                          $"Kind={v.Kind} 与 Action={p.Action} 不自洽");
                        V(p.Action != CA.Reject || v.Degradations.Count == 0,
                          $"Reject 格却登记了 {v.Degradations.Count} 条 Degradations");
                        if (p.Action != CA.Reject && v.Degradations.Count == 0) producedEmpty++;
                        // 投影一致性：Action/Reason/Advice 必须是计划的兼容投影（一字不改）
                        V(v.Action == p.Action && v.Reason == p.Reason && v.Advice == p.Advice,
                          "Verdict 的 Action/Reason/Advice 与计划不一致（兼容投影被破坏）");
                        // Code 去重：同一 Code 不得重复登记（GUI/门禁按 Code 计数）
                        for (int i = 0; i < v.Degradations.Count; i++)
                            for (int j = i + 1; j < v.Degradations.Count; j++)
                                V(v.Degradations[i].Code != v.Degradations[j].Code,
                                  $"重复登记 Code={v.Degradations[i].Code}");
                    }
            Check(viol.Count == 0,
                $"verdict 自洽性：{cells} 格（产出 {produced} 含降级 {degradated}、拒绝 {rejected}）零违规"
                + (viol.Count > 0 ? " 例如: " + string.Join(" | ", viol.GetRange(0, Math.Min(8, viol.Count))) : ""));
            // 任务书要求的等价表述（**限定在已产出的格上**）：`Kind==Proceed ⇔ Degradations 为空`。
            Check(producedEmpty + degradated == produced,
                $"verdict 自洽性：已产出 {produced} 格必须恰好二分（Proceed {producedEmpty} + "
                + $"{VK.ProceedWithDegradation} {degradated}）（实为 {producedEmpty + degradated}）");

            // 覆盖面双向钉住：**每个已填 Code 至少被命中一次**（否则正向断言缺失）；
            // 且**不得**出现未登记在案（未被本模式断言）的 Code（否则新 Code 会静默溜过门禁）。
            var filled = new[] { DG.BitDepthReduced, DG.GamutClipped, DG.GamutCompressed, DG.UnlabeledAssumedSrgb,
                                 DG.NoIccCarrier, DG.ContainerCannotCarryIcc, DG.ToneMapNotAppliedToGainMap };
            foreach (var c in filled)
                Check(seen.Contains(c), $"覆盖：{c} 至少被一条正向断言命中");
            var unexpected = new System.Collections.Generic.List<string>();
            foreach (var c in seen) if (Array.IndexOf(filled, c) < 0) unexpected.Add(c);
            Check(unexpected.Count == 0,
                $"覆盖：不得出现未被断言登记的 Code（实际多出: {string.Join(",", unexpected)}）");
        }

        /// <summary>
        /// tonemapcurve &lt;out.csv&gt; &lt;mode&gt; &lt;peakNits&gt; &lt;samples&gt; [sdrWhite]
        /// 导出“x(线性, 1.0=SDR白) → y”供外部参照（ffmpeg tonemap）对拍用。
        /// </summary>
        private static void DumpToneMapCurve(string[] args)
        {
            string path = args.Length > 1 ? args[1] : "tests/output/validate/tm_ours.csv";
            var mode = Enum.TryParse<FfmpegGui.Services.ColorMapping.ToneMapMode>(args.Length > 2 ? args[2] : "Hable", true, out var mm)
                ? mm : FfmpegGui.Services.ColorMapping.ToneMapMode.Hable;
            double peak = args.Length > 3 && double.TryParse(args[3], out var pk) ? pk : 1000;
            int n = args.Length > 4 && int.TryParse(args[4], out var nn) ? nn : 512;
            double white = args.Length > 5 && double.TryParse(args[5], out var ww) ? ww : 203;
            var p = new FfmpegGui.Services.ColorMapping.ToneMapParams
            { Mode = mode, PeakNits = peak, SdrWhiteNits = white };
            double hr = p.Headroom;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++)
            {
                double x = n == 1 ? 0 : i / (n - 1.0) * hr;
                sb.Append(x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                  .Append(FfmpegGui.Services.ColorMapping.ToneMap.Apply(x, p).ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                  .Append('\n');
            }
            File.WriteAllText(path, sb.ToString());
            Console.WriteLine($"tonemapcurve: {path} mode={mode} headroom={hr:F6} n={n}");
        }

        /// <summary>
        /// tonemap —— P4a 色调映射门禁。
        /// 只验“数学性质 + 决策挂载 + 值域换算 + 端到端产出”，
        /// 不去比“跟 ffmpeg hable 一致”（Hable/BT.2390 常数未经标定，故意不实现、不假装通过）。
        /// </summary>
        private static async Task ProbeToneMap(string[] args)
        {
            var off = new FfmpegGui.Services.ColorMapping.ToneMapParams { Mode = FfmpegGui.Services.ColorMapping.ToneMapMode.None, PeakNits = 1000 };
            Check(!off.Active, $"未选算子 ⇒ 不激活（{off}）");
            var flat = new FfmpegGui.Services.ColorMapping.ToneMapParams { Mode = FfmpegGui.Services.ColorMapping.ToneMapMode.ReinhardSegmented, PeakNits = 203 };
            Check(!flat.Active && Math.Abs(flat.Headroom - 1.0) < 1e-12, "headroom=1 ⇒ 不激活（保证不会把 SDR 内容“静默压暗”）");
            Check(Math.Abs(FfmpegGui.Services.ColorMapping.ToneMap.Apply(0.42, flat) - 0.42) < 1e-12, "未激活时严格恒等");

            var p = new FfmpegGui.Services.ColorMapping.ToneMapParams
            { Mode = FfmpegGui.Services.ColorMapping.ToneMapMode.ReinhardSegmented, PeakNits = 1000, SdrWhiteNits = 203 };
            double hr = p.Headroom;
            Check(hr > 4 && hr < 5.5, $"headroom = 1000/203 ≈ {hr:F3}");
            Check(Math.Abs(FfmpegGui.Services.ColorMapping.ToneMap.Apply(0.0, p)) < 1e-6, "端点：Apply(0)=0");
            Check(Math.Abs(FfmpegGui.Services.ColorMapping.ToneMap.Apply(0.5, p) - 0.5) < 1e-6, "knee 以下直通（SDR 内容零损失）");
            Check(Math.Abs(FfmpegGui.Services.ColorMapping.ToneMap.Apply(hr, p) - 1.0) < 1e-3, "端点：峰值→1.0");
            var curve = FfmpegGui.Services.ColorMapping.ToneMap.BuildCurve(p, 512, 0, hr);
            bool mono = true; int bad = -1;
            for (int i = 1; i < curve.Length; i++) if (curve[i] < curve[i - 1] - 1e-7) { mono = false; bad = i; break; }
            Check(mono, $"曲线单调不降（512 点，首个下降点 idx={bad}）");
            bool inRange = true;
            for (int i = 0; i < curve.Length; i++) if (curve[i] < -1e-6 || curve[i] > 1 + 1e-6) { inRange = false; break; }
            Check(inRange, "输出全部落在 [0,1]");

            // 保色相：三通道按同一因子缩放（以 max 为压缩对象）
            float r0 = 3.1f, g0 = 1.55f, b0 = 0.62f;
            float r = r0, g = g0, b = b0;
            FfmpegGui.Services.ColorMapping.ToneMap.ApplyRgb(ref r, ref g, ref b, p, TC.Srgb);
            double gr0 = g0 / r0, gb0 = b0 / r0, gr1 = (double)g / r, gb1 = (double)b / r;
            Check(Math.Abs(gr1 - gr0) < 1e-4 && Math.Abs(gb1 - gb0) < 1e-4,
                $"保色相：G/R、B/R 不变（{gr0:F5}→{gr1:F5}, {gb0:F5}→{gb1:F5}）");
            Check(r > r0 / p.Headroom && r < r0, $"峰值通道被压缩但未截断到 0（{r0:F2}→{r:F3}）");

            // 决策挂载：未请求 ⇒ Tone==null；请求 ⇒ ColorLoss=ToneMapped
            var pq = CS.FromCicp("bt2020", "smpte2084", "BT.2100 PQ", nitsOfLinearOne: TC.PqPeakNits);
            var srgb = CS.FromCicp("bt709", "iec61966-2-1", "sRGB");
            if (pq == null || srgb == null) { Check(false, "描述符构造失败"); return; }
            var itNo = new CI { Strategy = ST.Manual, Source = pq, Target = srgb, Format = CE.CapsFor("png"),
                                 TargetExplicit = true, AllowApproximateCurve = false, HdrPeakNits = 1000 };
            var pNo = CE.Plan(itNo);
            Check(pNo.Tone == null && pNo.ColorLoss != CL.ToneMapped,
                $"未请求 tonemap ⇒ 不挂载（Tone={pNo.Tone} loss={pNo.ColorLoss}）");
            // ⚠ CI 是引用类型：必须另建一个意图，不能 `var itYes = itNo` 后改字段（会污染 pNo 的用例）
            var itYes = new CI { Strategy = ST.Manual, Source = pq, Target = srgb, Format = CE.CapsFor("png"),
                                 TargetExplicit = true, AllowApproximateCurve = false, HdrPeakNits = 1000,
                                 ToneMapRequested = true, ToneMap = FfmpegGui.Services.ColorMapping.ToneMapMode.ReinhardSegmented };
            var pYes = CE.Plan(itYes);
            Check(pYes.Tone is { Active: true }, $"请求后已挂载（{pYes.Tone}）");
            Check(pYes.ColorLoss == CL.ToneMapped, $"ColorLoss 归类为 ToneMapped（实为 {pYes.ColorLoss}）");

            // 值域换算：PQ 的 1.0=10000nit → 需 LinearScale=10000/203；无 tonemap 时必须为 1
            double ls = pYes.ToSpec().LinearScale;
            Check(Math.Abs(ls - 10000.0 / 203.0) < 1e-6, $"ToSpec().LinearScale = 10000/203（实为 {ls:F4}）");
            Check(Math.Abs(pNo.ToSpec().LinearScale - 1.0) < 1e-12, "无 tonemap ⇒ LinearScale=1（不改旧行为）");

            // 端到端：只验“接线可用”（产出存在且可解）；亮度类断言不在此做——
            // ❌ 旧版本曾断言“tonemap 必提升平均亮度”，但本测图经 PQ 解码+缩放后根本无超白成分，
            //    而 knee 以下本就是直通 ⇒ 开/关必然一致 ⇒ 该断言是个错假设（测试设计问题，非实现问题）。
            string src = "tests/output/validate/tm_in.png";
            var ff = FfmpegGui.Services.AppSettingsService.Current.FfmpegPath;
            var psi = new System.Diagnostics.ProcessStartInfo(ff!,
                $"-y -hide_banner -loglevel error -f lavfi -i \"testsrc2=s=64x48:duration=1:rate=1\" -pix_fmt rgb48le -frames:v 1 " +
                $"-color_primaries bt2020 -color_trc smpte2084 \"{src}\"")
            { RedirectStandardError = true, UseShellExecute = false };
            using (var pr = System.Diagnostics.Process.Start(psi)!) { pr.StandardError.ReadToEnd(); pr.WaitForExit(); }
            if (File.Exists(src))
            {
                string o2 = "tests/output/validate/tm_on.png";
                if (File.Exists(o2)) File.Delete(o2);
                await FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(src, o2, pYes.ToSpec(), pYes, null, default, 2);
                Check(File.Exists(o2) && new FileInfo(o2).Length > 100, "端到端：tonemap 计划能完成 解码→变换→编码 全链路");
            }
            else Check(false, $"端到端测试图生成失败 {src}");

            // 确定性内核级判据：造一条 PQ 编码域骟梯（含超白段），直接比较开/关两路输出
            {
                int n = 512;
                var inb = new byte[n * 6]; var outA = new byte[n * 6]; var outB = new byte[n * 6];
                for (int i = 0; i < n; i++)
                {
                    int v = (int)Math.Round(i / (n - 1.0) * 65535.0);
                    for (int c = 0; c < 3; c++) { inb[i * 6 + c * 2] = (byte)(v & 0xFF); inb[i * 6 + c * 2 + 1] = (byte)(v >> 8); }
                }
                // ⚠ 必须把两路统一到同一 nits 域（否则比的不是“是否 tonemap”，而是 LinearScale 差异）。
                const double nitScale = 10000.0 / 203.0;
                var specA = pNo.ToSpec();  specA.LinearScale = nitScale;   // 同域、无 tonemap（纯钳位）
                var specB = pYes.ToSpec();                                 // 同域 + tonemap
                CK.TransformRgb48le(inb, outA, specA, n, null, null);
                CK.TransformRgb48le(inb, outB, specB, n, null, null);
                Func<byte[], int, int> code = (buf, i) => buf[i * 6] + (buf[i * 6 + 1] << 8);
                // ① 存在“超白但未饱和”的压缩带：开 tonemap 后该段严格变暗且仍 >0。
                // ❌ 不可拿最高码做比较：那里关路被钳位到 1.0、开路也被映到端点 1.0，两者相同（无信息）。
                int cmpIdx = -1, cA = 0, cB = 0;
                for (int i = n - 1; i >= 0; i--)
                {
                    int a = code(outA, i), b2 = code(outB, i);
                    if (a != b2) { cmpIdx = i; cA = a; cB = b2; break; }
                }
                Check(cmpIdx > 0 && cB < cA && cB > 0,
                    $"存在超白压缩带（首个差异 idx={cmpIdx}：关={cA} 开={cB}，开路更暗但未丢黑）");
                // ② 单调性：开 tonemap 后仍逐码单调不降
                bool mono2 = true; int dIdx = -1;
                for (int i = 1; i < n; i++) if (code(outB, i) < code(outB, i - 1)) { mono2 = false; dIdx = i; break; }
                Check(mono2, $"tonemap 后码域仍单调（首个下降 idx={dIdx}）");
                // ③ 亚 knee 段逐值不变（SDR 内容零损失，不“静默压暗”）
                int sameCnt = 0, diffBelowKnee = 0;
                for (int i = 0; i < n; i++)
                {
                    double lin = TC.Pq.ToLinear(i / (n - 1.0)) * nitScale;   // → 1.0 = SDR 白（与两路一致）
                    if (lin <= 0.75)
                    {
                        if (code(outA, i) == code(outB, i)) sameCnt++; else diffBelowKnee++;
                    }
                }
                Check(diffBelowKnee == 0 && sameCnt > 20,
                    $"亚 knee 段逐值不变（相同={sameCnt}，异常={diffBelowKnee}）→ SDR 内容零损失");
            }
        }

        /// <summary>用 ffmpeg signalstats 取 YAVG（归一化到 0..1）。</summary>
        private static double MeanLuma(string path)
        {
            var ff = FfmpegGui.Services.AppSettingsService.Current.FfmpegPath;
            var psi = new System.Diagnostics.ProcessStartInfo(ff!,
            $"-hide_banner -i \"{path}\" -vf format=gray,signalstats,metadata=print -f null -")
            { RedirectStandardError = true, UseShellExecute = false };
            using var pr = System.Diagnostics.Process.Start(psi)!;
            string err = pr.StandardError.ReadToEnd(); pr.WaitForExit();
            // signalstats 只写帧 metadata，必须经 metadata=print 输出（loglevel error 下\u539f\u672c\u4ec0\u4e48\u90fd\u770b\u4e0d\u5230）
            var mm = System.Text.RegularExpressions.Regex.Matches(err, "YAVG=(-?\\d+(?:\\.\\d+)?)");
            if (mm.Count == 0) return -1;
            double acc = 0;
            foreach (System.Text.RegularExpressions.Match x in mm)
                acc += double.Parse(x.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            return acc / mm.Count / 255.0;
        }

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

        /// <summary>
        /// hlgwire —— **接线级**门禁：HLG 是否在**真实执行路径**上应用了 BT.2100 OOTF。
        ///
        /// 为什么需要它（2026-09-16 接手核查）：`ootf` 只测 <see cref="Bt2100Ootf"/> **类本身**（数学），
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
            double expectNorm = nits / Peak;
            Console.WriteLine($"    HLG {Code} → 场景线性 {scene:F5} → OOTF 显示 {nits:F1} nits → 归一 {expectNorm:F5}");

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
            };

            // ① 峰值测量路径（tonemap 的 headroom 依据）——漏 OOTF 会让 headroom 算错
            double peak = FfmpegGui.Services.ColorMapping.ColorKernels.MeasurePeakLinearRgb48(buf, 1, spec, null);
            Check(Math.Abs(peak - expectNorm) / expectNorm < 0.05,
                $"① 峰值测量路径应用了 OOTF（期望 {expectNorm:F4}，实得 {peak:F4}）");

            // ② 主转换路径：HLG → sRGB，输出线性域应落在显示光而非场景光
            var dst = new byte[6];
            FfmpegGui.Services.ColorMapping.ColorKernels.TransformRgb48le(buf, dst, spec, 1, null, null);
            int o0 = dst[0] | (dst[1] << 8);
            double outLin = srgb.Curve.ToLinear(o0 / 65535.0);
            Check(Math.Abs(outLin - expectNorm) / expectNorm < 0.05,
                $"② 主转换路径应用了 OOTF（期望线性 {expectNorm:F4}，实得 {outLin:F4}）");

            // ③ float RGBA 路径：与 ①② 同一判据，防止两条实现各自漂移（本项目反复出现过"三处不一致"）
            var f = new float[4] { (float)Code, (float)Code, (float)Code, 1f };
            FfmpegGui.Services.ColorMapping.ColorKernels.TransformRgba(f, spec);
            double fLin = srgb.Curve.ToLinear(f[0]);
            Check(Math.Abs(fLin - expectNorm) / expectNorm < 0.05,
                $"③ float RGBA 路径应用了 OOTF（期望线性 {expectNorm:F4}，实得 {fLin:F4}）");
        }

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

        /// <summary>
        /// wire —— 验证 A1 装配点：选项 → ColorIntent → Plan。
        /// 只验“装配与决策正确”，不改生产路径（A2 负现）。任何不确定均应返回 null 而非半吊子意图。
        /// </summary>
        private static void ProbeWire()
        {
            var sdr = "tests/output/sources/src_8bit.png";
            var pq = "tests/output/validate/src_hdr.png";   // 带真 cICP(9,16) 的 HDR 素材（由 ensure-fixtures 产出）
            if (!File.Exists(sdr) || !File.Exists(pq)) { Check(false, "素材缺失（先跑 generate-sources.ps1 + ensure-fixtures.ps1）"); return; }
            // 防“假 HDR 素材”：源本身必须被探测为 PQ，否则后面的 tonemap 用例没有意义
            var mHdr = FfmpegGui.Services.FfmpegCommandBuilder.ProbeInputColorMetadata(pq);
            Check(mHdr.colorTrc == "smpte2084",
                $"HDR 素材确实被探测为 PQ（实际 trc={mHdr.colorTrc ?? "null"}）——否则素材未带 cICP");
            FfmpegGui.Models.FfmpegOptions O(string fmt, string? cs = null, string? tm = null, string? inPath = null,
                                            double? sdrPeak = null, bool adv = false, string? trc = null)
                => new() { Format = fmt, ColorSpace = cs ?? "auto", ColorStrategy = FfmpegGui.Models.ColorStrategy.Recommended,
                           ColorToneMap = tm ?? "none", BitDepth = 8, UseAdvancedColorParameters = adv, ColorTrc = trc,
                           // 路由门要求“引擎已承接所设参数”；默认质量才算不干涉编码出口
                           Quality = FfmpegGui.Models.FfmpegOptions.GetDefaultQuality(fmt),
                           ColorSdrPeakNits = sdrPeak ?? 100 };

            // 1) SDR 源 + auto 目标：可装配，且不应请求映射
            var i1 = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(O("png"), sdr, out var r1);
            Check(i1 != null && !i1!.ToneMapRequested && i1.Target == null, $"png/auto 装配 OK（reason={r1 ?? "无"}）");
            var p1 = CE.Plan(i1!);
            Check(p1.IsOk && p1.ColorLoss == CL.None, $"png/auto 计划不改像素（{p1.Action}/{p1.ColorLoss}）");

            // 2) 显式目标 P3：可装配且应做矩阵映射
            var i2 = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(O("png", "Display P3"), sdr, out var r2);
            var p2 = i2 == null ? null : CE.Plan(i2);
            Check(i2 != null && i2.TargetExplicit && i2.Target?.PrimariesToken == "smpte432",
                $"目标 P3 解析正确（{(i2 == null ? r2 : i2.Target?.Name)}）");
            Check(p2 != null && p2.IsOk && p2.Matrix != null, $"P3 目标发生矩阵映射（act={p2?.Action}）");

            // （此处曾加过一条“BT.2020 目标的 ICC TRC ≡ 输出 TRC”正例，但它挑错了格子：
            //   PNG 的 CanHaveBothIccAndCicp=false ⇒“ICC 优先”会把 Out* 清空（合法），
            //   没有“两条都声明”可比。面它倒出了一个更结构的问题：
            //   QueueProcessor 的 png3 后置步骤会往 PNG 写 cICP，不看能力表的“不可共存”裁决
            //   ⇒ “PNG 能否 ICC+CICP 并存”在两处答案不同，这是 P7 步骤 3 要收敛的范围。）

            // 3) HDR 源 + bt2446a + SDR 目标：挂载 Method A 并归类 ToneMapped
            var i3 = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(O("png", "sRGB", "bt2446a", pq, 300), pq, out var r3);
            var p3 = i3 == null ? null : CE.Plan(i3);
            var TM2 = FfmpegGui.Services.ColorMapping.ToneMapMode.Bt2446A;
            Check(i3 != null && i3.ToneMapRequested && i3.ToneMap == TM2, $"bt2446a 装配（{(i3 == null ? r3 : i3.ToneMap.ToString())}）");
            // ⚠ 加严：必须确认 Tone 真的“激活”（headroom>1），而不是靠另一条分支给出 ToneMapped
            Check(p3?.Tone?.Mode == TM2 && p3.Tone.Active && p3.Tone.Headroom > 1.5
                  && p3.ColorLoss == CL.ToneMapped,
                $"bt2446a 真的生效（active={p3?.Tone?.Active} headroom={p3?.Tone?.Headroom:F2} loss={p3?.ColorLoss}）");
            Check(System.Math.Abs((p3?.Tone?.SdrPeakNits ?? 0) - 300) < 1e-9, $"L_SDR 透传（{(p3?.Tone?.SdrPeakNits).ToString() ?? "-"}）");

            // 4) 拒绝/回退路径：必须返回 null 并给原因
            Check(FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(O("png", "sRGB", "bt2390", pq), pq, out var rr1) == null
                  && (rr1 ?? "").Contains("出处"), $"bt2390 不装配（{rr1}）");
            Check(FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(O("png", "sRGB", "bogus"), sdr, out var rr2) == null,
                  $"未知 tone-map 值不装配（{rr2}）");
            // **2026-09-17 契约更新**：GIF 已接入引擎 ⇒ 意图**必须装配**（原断言 `== null` 锁的是
            // "GIF 尚未接入"这一历史状态）。「无色彩通路」这个**能力事实**不再由装配层表达，
            // 改由规划层给结局（不改像素 / 映射到 sRGB 工作空间，两种都**不写标注**）。
            // ⚠ 无通路容器里**放行 gif 与 jxr**（两者都已接入引擎；jxr 的执行出口 = BMP/TIFF 中转 +
            //   JxrEncApp，2026-09-17 接入）。DNG 需传感器 Bayer 数据，PPM/BMP/HEIC 无接入计划
            //   ⇒ "其余不放行"仍由下面的 heic 守着。
            var ig = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(O("gif"), sdr, out var rr3);
            Check(ig != null, $"GIF 已接入 ⇒ 意图必须装配（reason={rr3 ?? "无"}）");
            if (ig != null)
            {
                var pg = CE.Plan(ig);
                Check(pg.IsOk && pg.UnlabeledAssumedSrgb,
                    $"GIF 计划：不写任何色彩标注（Action={pg.Action} UnlabeledAssumedSrgb={pg.UnlabeledAssumedSrgb}）");
            }
            // JXR 与 GIF **同款结局**（容器无色彩通路 ⇒ 映射到 sRGB 工作空间且不写标注），
            // 且必须**置 ExternalEncoderExit** —— 否则计划说"像素改了"、执行层却走 ffmpeg 编码器，
            // 而本工具链的 ffmpeg **没有 jxr 编码器** ⇒ 必然产不出产物（不是静默降级，是直接失败）。
            var ij = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(O("jxr"), sdr, out var rr3b);
            Check(ij != null, $"JXR 已接入 ⇒ 意图必须装配（reason={rr3b ?? "无"}）");
            if (ij != null)
            {
                Check(ij.ExternalEncoderExit, "JXR 意图必须置 ExternalEncoderExit（ffmpeg 无 jxr 编码器）");
                var pj = CE.Plan(ij);
                Check(pj.IsOk && pj.ExternalEncoderExit && pj.UnlabeledAssumedSrgb,
                    $"JXR 计划：ExternalEncoderExit + 不写标注（Action={pj.Action} exit={pj.ExternalEncoderExit} "
                    + $"unlabeled={pj.UnlabeledAssumedSrgb}）");
            }
            Check(FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(O("heic"), sdr, out var rr4) == null,
                  $"HEIC（无写入通路）不装配（{rr4}）");
            // HDR 源 + 显式 HDR 目标 + 请求映射 ⇒ 矛盾，不装配
            Check(FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(O("png", "BT.2020 PQ", "hable", pq), pq, out var rr5) == null,
                  $"tone-map 与 HDR 目标矛盾时不装配（{rr5}）");
            // SDR 源却请求 tonemap ⇒ 不装配（避免无意义压暗）
            Check(FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(O("png", "sRGB", "mobius"), sdr, out var rr6) == null,
                  $"非 HDR 源请求 tonemap 时不装配（{rr6}）");

            // 5) A2 前置件：路由判据（纯函数，与 QueueProcessor 插入点无关）
            // **2026-09-16 契约更新**：默认改为**走引擎**（用户指令「清理传统管线」），
            // `legacy` 降级为显式逃生门。故这里同时锁两条：
            //   ① 显式 legacy ⇒ 不路由（逃生门有效，可用于 A/B 与排查）；
            //   ② **默认（不传参）⇒ 路由**（默认值本身就是契约，必须有断言守着，
            //      否则"哪天被改回 legacy"无人发现）。
            var R = typeof(FfmpegGui.Services.ColorMapping.ColorEngineRouter);
            var eng = O("png"); eng.ColorEngine = "engine";
            var leg = O("png"); leg.ColorEngine = "legacy";
            var def = O("png");
            Check(!FfmpegGui.Services.ColorMapping.ColorEngineRouter.ShouldRoute(leg, "o.png", false),
                "显式 legacy ⇒ 不路由（逃生门有效）");
            Check(FfmpegGui.Services.ColorMapping.ColorEngineRouter.ShouldRoute(def, "o.png", false),
                "**默认（不传 --color-engine）⇒ 路由到引擎**（默认值即契约）");
            Check(FfmpegGui.Services.ColorMapping.ColorEngineRouter.ShouldRoute(eng, "o.png", false), "engine + png ⇒ 路由");
            Check(FfmpegGui.Services.ColorMapping.ColorEngineRouter.ShouldRoute(eng, "o.webp", false), "engine + webp ⇒ 路由");
            Check(FfmpegGui.Services.ColorMapping.ColorEngineRouter.ShouldRoute(eng, "o.tif", false), "engine + tif ⇒ 路由");
            // **2026-09-17**：gif 接入引擎出口（调色板链 + 第二输入补 alpha）⇒ 必须路由。
            Check(FfmpegGui.Services.ColorMapping.ColorEngineRouter.ShouldRoute(eng, "o.gif", false),
                "engine + gif ⇒ 路由（2026-09-17 接入）");
            // 负控：仍在白名单外的格式不得被"连带放行"（ppm 无色彩通路且无接入计划）。
            // ⚠ 2026-09-17（S2）：`BlockReason` 返回 `EngineBlock?` ⇒ 判据一律打 **Code**（禁止匹配 Text）。
            var bppm = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(eng, "o.ppm", false);
            Check(bppm?.Code == "FormatNotSupported",
                $"engine + ppm ⇒ 不路由且 Code=FormatNotSupported（{bppm?.Text}）");
            // **2026-09-17：jxl 接入引擎出口（ffmpeg 的 libjxl 子情形）**。两条断言成对锁住分级，
            // 二者**只差一个变量**（EncoderBackend）⇒ "把该变量改回去"就是一次干净的变异：
            //   ① backend=Ffmpeg ⇒ **路由**（与 png/avif/gif 同型）；
            //   ② backend=Cjxl（本机装了 cjxl 时的**默认后端**，EncoderDetectionService:55）
            //      ⇒ **同日第二轮起也路由**（cjxl 执行出口接入，见下方契约变更说明）。
            // 分级结论因此从「按后端拦」变成「按**出口是否存在**判」—— 这正是本轮的实质：
            // 原来挡住 Cjxl 的**不是**"它是外部编码器"，而是"引擎没有能兑现它的出口"。
            var jxlFf = O("jxl"); jxlFf.ColorEngine = "engine";
            jxlFf.EncoderBackend = FfmpegGui.Services.EncoderBackend.Ffmpeg;
            Check(FfmpegGui.Services.ColorMapping.ColorEngineRouter.ShouldRoute(jxlFf, "o.jxl", false),
                "engine + jxl(backend=Ffmpeg) ⇒ 路由（2026-09-17 接入 ffmpeg-libjxl 子情形）");
            // **2026-09-17（cjxl 出口轮）：契约变更 —— jxl+Cjxl 由"被挡"改为"路由"**。
            // 变更理由：引擎新增了 cjxl 执行出口（`RawColorPipeline.EncodeJxlViaCjxlAsync`），
            // 它**兑现了**原来挡它的那条理由（"cjxl 不吃 rawvideo、色彩语义 ffmpeg 参数表达不了"）：
            // 出口把变换后的 raw 经 PPM/PAM 管道喂 cjxl，色彩语义由 `-x color_space=` /
            // `-x icc_pathname=` / `--intensity_target=` 表达（后两者复用**同一份**
            // `CjxlService.BuildCjxlArguments`，不另写一套）。
            // ⚠ 这是**有意**的契约变更：旧断言（⇒ 不路由）会转红，**不要**为了让旧断言变绿而放弃放宽。
            var jxlCjxl = O("jxl"); jxlCjxl.ColorEngine = "engine";
            jxlCjxl.EncoderBackend = FfmpegGui.Services.EncoderBackend.Cjxl;
            var br = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(jxlCjxl, "o.jxl", false);
            Check(br == null, $"engine + jxl(backend=Cjxl) ⇒ **路由**（2026-09-17 cjxl 出口接入；block={br?.Text ?? "无"}）");
            // 计划位（`ExternalEncoderExit`）：**只有 jxl+Cjxl 置位**。ffmpeg 子情形绝不能也走外部管道
            //（多一个进程、还会丢掉 ffmpeg 侧参数）⇒ 两条成对断言，只差 EncoderBackend 一个变量。
            var itCjxl = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(jxlCjxl, sdr, out var whyCjxl);
            Check(itCjxl is { ExternalEncoderExit: true },
                $"意图装配：jxl + Cjxl ⇒ ExternalEncoderExit=true（{whyCjxl ?? "ok"}）");
            var itFf = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(jxlFf, sdr, out var whyFf);
            Check(itFf is { ExternalEncoderExit: false },
                $"意图装配：jxl + Ffmpeg ⇒ ExternalEncoderExit=false（走普通 ffmpeg 出口；{whyFf ?? "ok"}）");
            // 负控：**其他格式** + 外部后端仍必须被挡 —— 放宽不得外溢（cjpgli/cjpegli 尚无出口）。
            var jpgCjpegli = O("jpg"); jpgCjpegli.ColorEngine = "engine";
            jpgCjpegli.EncoderBackend = FfmpegGui.Services.EncoderBackend.Cjpegli;
            var brJpg = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(jpgCjpegli, "o.jpg", false);
            Check(brJpg?.Code == "ExternalBackend",
                $"engine + jpg(backend=Cjpegli) ⇒ 仍被挡（负控：放宽只对 jxl+Cjxl 生效；Code={brJpg?.Code}）");
            // ── cjxl 出口的命令行形态（纯函数层；CLI 端到端形态另见 verify-color-wiring 第 (10) 段）──
            // 要钉住的不变量：**色彩语义来自计划**（override），既不能凭空生成、也不能回头读 options。
            var cjxlOpts = O("jxl"); cjxlOpts.Quality = 75; cjxlOpts.JxlEffort = 7;
            var argsCs = FfmpegGui.Services.CjxlService.BuildCjxlArguments(
                "-", "o.jxl", cjxlOpts, default, null, "Rec2100PQ", 1000);
            Check(argsCs.Contains("-x color_space=Rec2100PQ") && argsCs.Contains("--intensity_target=1000"),
                $"cjxl 出口命令行：色彩语义与显示峰值均来自计划（{argsCs}）");
            var argsNone = FfmpegGui.Services.CjxlService.BuildCjxlArguments(
                "-", "o.jxl", cjxlOpts, default, null, null, 0);
            Check(!argsNone.Contains("color_space") && !argsNone.Contains("intensity_target"),
                $"cjxl 出口命令行：计划未给色彩语义时**不得凭空生成**标注（{argsNone}）");
            // **2026-09-17 单一真值锁**：白名单此前在 `ColorEngineRouter` 与 `ImageEncoderArgs` 各写一份
            // 字面量（后者**全仓无调用者**，只在 Router 文档里被引用）⇒ 已合并为
            // `ImageEncoderArgs.IsEngineEncodable` **一份**，Router 改为调用它。
            // 本断言把合并后的那一份**逐字钉死**：今后新增/移除出口格式必须同步改探针（防再次漂移）。
            string[] encWhitelist = { "png", "apng", "tiff", "tif", "webp", "jpg", "jpeg", "avif", "gif", "jxl", "jxr" };
            string[] encOutside = { "dng", "ppm", "bmp", "heic", "heif", "qoi" };
            var encMissing = new StringBuilder(); var encExtra = new StringBuilder();
            foreach (var f in encWhitelist)
                if (!FfmpegGui.Services.ColorMapping.ImageEncoderArgs.IsEngineEncodable(f)) encMissing.Append(f).Append(' ');
            foreach (var f in encOutside)
                if (FfmpegGui.Services.ColorMapping.ImageEncoderArgs.IsEngineEncodable(f)) encExtra.Append(f).Append(' ');
            Check(encMissing.Length == 0 && encExtra.Length == 0,
                $"引擎出口白名单必须恰为 {string.Join("/", encWhitelist)}（缺=[{encMissing}] 多=[{encExtra}]）");
            // ── JXL 的 ICC 通路：**引擎出口只能写 CICP**（2026-09-17 实测，本次接入的设计约束）──
            // 实测事实链（两条独立路径都不通 ⇒ 不是"命令写错"）：
            //   ① **编码器丢弃**：`-vf iccgen` 生成的 ICC 不落盘 —— legacy 参照 ref.jxl 与引擎出口产物，
            //      exiftool 都读不出 ICC（方法自检：读 cjxl 产的 cjxl_lossless.jxl 同样读不出）；
            //   ② **后置被拒**：`exiftool "-icc_profile<=p3.icc" -overwrite_original w.jxl`
            //      ⇒ 退出码 **1**、`Error: [minor] Will wrap JXL codestream in ISO BMFF container for writing`、
            //      `0 image files updated`（**同一命令写 PNG 成功 584B**）⇒ 是 JXL 容器限制。
            // ⇒ `RawColorPipeline` 对「jxl + 计划要求附 ICC」**显式拒绝**（宁可硬失败，
            //    也不产出一个无任何色彩描述的 JXL —— 那正是历史偏色根因）。
            // 下面两条把这道门**为何不空转**钉住；第 3 条界定它是不是主路径（结论：不是）。
            var jxlCaps = CE.CapsFor("jxl");
            var proP = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor.FromNamedSpace("ProPhoto RGB");
            var p3d = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor.FromCicp("smpte432", "iec61966-2-1", "Display P3");
            Check(proP != null && p3d != null, "构造 ProPhoto / Display P3 描述符（JXL ICC 通路用例前置）");
            if (proP != null && p3d != null)
            {
                Check(!CE.CapsAcceptsCicp(jxlCaps, proP),
                    "jxl 的 CICP 枚举（sRGB/DisplayP3/Rec2100PQ/Rec2100HLG）表达不了 ProPhoto ⇒ 该目标只能走 ICC");
                var polJxl = new FfmpegGui.Services.ColorMapping.PlanPolicy
                { Format = jxlCaps, AllowApproximateCurve = false, ForceMapping = true, TargetExplicit = true };
                var pJxlIcc = CE.Plan(p3d, proP, polJxl);
                Check(pJxlIcc.AttachIcc && pJxlIcc.Action == CA.Map,
                    $"规划层对「jxl + 不可命名目标」确实要求附 ICC（act={pJxlIcc.Action} attachIcc={pJxlIcc.AttachIcc}）"
                    + " ⇒ 出口那道拒绝门不是恒 false 的空断言");
                // 第 3 条（**界定**）：CLI **到不了**上面那个组合 —— 目标描述符由
                // `EffectiveOutputColorTokens` 的 CICP 三元组构造（`ColorSpaceDescriptor.FromCicp`），
                // 装配层还会按容器能力钳制，故 CLI 得到的目标**恒为可 CICP 命名**的
                // ⇒ 规划层的「择一」规则（`EnforceContainerAnnotationRules`，jxl 的
                // `CanHaveBothIccAndCicp=false`）必然剥掉 AttachIcc。下面这条钉的就是这个不变量：
                // 「CLI 装配出的 jxl 计划**永不**同时要求『附 ICC + 改像素』」。
                // 门若变成主路径（该不变量被破坏）⇒ 本条转红，正是我们要的告警。
                var badJxl = O("jxl"); badJxl.ColorEngine = "engine";
                badJxl.UseAdvancedColorParameters = true; badJxl.ColorPrimaries = "smpte431"; badJxl.ColorTrc = "gamma22";
                var badJxlIt = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(badJxl, sdr, out var whyBadJxl);
                var badJxlPlan = badJxlIt == null ? null : CE.Plan(badJxlIt);
                Check(badJxlIt == null || !(badJxlPlan!.AttachIcc && badJxlPlan.Action == CA.Map),
                    "界定：CLI 装配出的 jxl 计划**永不**同时要求「附 ICC + 改像素」"
                    + $"（target={badJxlIt?.Target?.Name ?? ("装配失败: " + whyBadJxl)} "
                    + $"act={badJxlPlan?.Action} attachIcc={badJxlPlan?.AttachIcc}）"
                    + " ⇒ 出口那道门是兜底而非主路径");
                // 第 4 条：**执行体那道门本身**也要有牙（删掉它 ⇒ 本条转红）。
                // 刻意把门放在 `ProbeSizeAsync` **之前** ⇒ 这里可以用不存在的输入路径 + 任意存在的
                // ffmpeg 占位（`ffmpegOverride`），**不需要真素材、不启动任何子进程**（纯拒绝路径）。
                bool jxlIccThrew = false; string? jxlIccMsg = null;
                try
                {
                    FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                        "tests/output/_no_such_input.png", "tests/output/_no_such_output.jxl",
                        pJxlIcc.ToSpec(), pJxlIcc, null, default, 1, Environment.ProcessPath)
                        .GetAwaiter().GetResult();
                }
                catch (InvalidOperationException ex) { jxlIccThrew = true; jxlIccMsg = ex.Message; }
                Check(jxlIccThrew && (jxlIccMsg ?? "").Contains("JXL 出口无法附 ICC"),
                    $"执行体的 JXL ICC 拒绝门确实生效（threw={jxlIccThrew} msg={jxlIccMsg}）");
                // 第 5 条（**分叉**，2026-09-17 cjxl 出口轮）：同一份「jxl + AttachIcc」计划，置上
                // `ExternalEncoderExit` 之后**不得**再被那道门拦住 —— 因为 cjxl 出口**真的能写 ICC**
                //（`-x icc_pathname=`，实测产物 jxlinfo 读回 P3 primaries）。
                // 判据：抛出的异常**不再是** ICC 门那一条（此处会因输入不存在而在更后面失败）
                // ⇒ 说明它**越过了**门。⚠ 把分叉改回"按扩展名判"（删掉 `&& !plan.ExternalEncoderExit`）⇒ 本条转红。
                var pForkPlan = CE.Plan(p3d, proP, polJxl);
                pForkPlan.ExternalEncoderExit = true;
                bool forkThrew = false; string? forkMsg = null;
                try
                {
                    FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                        "tests/output/_no_such_input.png", "tests/output/_no_such_output.jxl",
                        pForkPlan.ToSpec(), pForkPlan, null, default, 1, Environment.ProcessPath)
                        .GetAwaiter().GetResult();
                }
                catch (InvalidOperationException ex) { forkThrew = true; forkMsg = ex.Message; }
                Check(!(forkThrew && (forkMsg ?? "").Contains("JXL 出口无法附 ICC")),
                    "ICC 拒绝门**按出口分叉**：置 ExternalEncoderExit 后不再按扩展名拦"
                    + $"（threw={forkThrew} msg={forkMsg}）");
            }
            // 第 6 条（2026-09-17 cjxl 出口轮**暴露并修复的真实缺陷**的回归锁）：
            // `CarryIcc` / `None` 两个 Action 的语义是"像素零改动"，但这类计划**不填**
            // `SrcCurve`/`DstCurve`/`Matrix`（`PlanCarry` 是提前 return 的分支）⇒ 会落进
            // `ToSpec()` 里 `?? TransferCurve.Linear` / `?? TransferCurve.Srgb` 两个**猜测式默认**
            // ⇒ 得到"按线性解码、按 sRGB 编码"的 spec ⇒ **整幅提亮**。
            // 实测（512x384 P3 源，jxl+Cjxl）：变换输出 == lin2srgb(解码输出)，平均绝对差 0.0000，
            // 均值 0.600 → 0.713。修复 = `ToSpec()` 按 Action 短路成恒等。
            // ⚠ 用**参考算子**直接钉"恒等"，不依赖任何素材/子进程；把短路删掉 ⇒ 0.6 会变成 0.798 ⇒ 转红。
            foreach (var noChange in new[] { CA.CarryIcc, CA.None })
            {
                var pNo = new FfmpegGui.Services.ColorMapping.ColorTransformPlan { Action = noChange };
                var spNo = pNo.ToSpec();
                var rt = FfmpegGui.Services.ColorMapping.ColorKernels.TransformPixelReference(0.6f, 0.6f, 0.6f, spNo);
                Check(spNo.Matrix == null && spNo.SrcCurve.Name == spNo.DstCurve.Name
                      && Math.Abs(rt.R - 0.6) < 1e-6 && Math.Abs(rt.G - 0.6) < 1e-6 && Math.Abs(rt.B - 0.6) < 1e-6,
                    $"{noChange} 计划的 ToSpec 必须是**恒等**（曲线同名={spNo.SrcCurve.Name}、矩阵空、0.6→{rt.R:F4}）"
                    + " —— 否则 cjxl 出口会把「像素零改动」的产物整幅提亮");
            }
            var bAnim = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(eng, "o.png", true);
            Check(bAnim?.Code == "AnimatedInput",
                $"动画输入 ⇒ 不路由（引擎是单帧）且 Code=AnimatedInput（{bAnim?.Text}）");
            // 诚实门（**2026-09-16 契约更新**）：引擎编码出口**已承接**质量/结构参数
            //（见 ImageEncoderArgs），故不再拒绝；但"承接了"必须真的落到编码器参数上，
            // 否则就是一句空话 —— 下面第二条断言即为此处的"牙"。
            var qOk = O("png"); qOk.ColorEngine = "engine"; qOk.Quality = 60;
            Check(FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(qOk, "o.png", false) == null,
                "质量参数已由引擎编码出口承接 ⇒ 不再拒绝");
            var qArgs = FfmpegGui.Services.ColorMapping.ImageEncoderArgs.BuildEncoderOptions("png", qOk);
            int expectCl = FfmpegGui.Services.ColorMapping.ImageEncoderArgs.MapPngCompression(60);
            Check(qArgs.Contains($"-compression_level {expectCl}"),
                $"质量确实落到编码器参数（q=60 ⇒ -compression_level {expectCl}，实得「{qArgs.Trim()}」）");
            // **2026-09-16 契约更新**：最长边几何缩放**已接入引擎**（解码步复用同一个
            // `BuildMaxDimensionScaleFilter`）⇒ 不再拒绝。此处改为"不再拒绝"的正向断言；
            // "真的缩到限值"由 `verify-color-wiring.ps1` 第 (7) 节实测宽高守护（探针是纯函数层，看不到像素）。
            var geo = O("png"); geo.ColorEngine = "engine"; geo.EnableMaxDimension = true;
            Check(FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(geo, "o.png", false) == null,
                "最长边几何缩放已承接 ⇒ 不再拒绝");
            var old = O("png"); old.ColorEngine = "engine"; old.TonemapCurve = "reinhard";
            var bo = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(old, "o.png", false);
            Check(bo?.Code == "LegacyTonemapCurve",
                $"旧 --tonemap-curve 未接入 ⇒ 拒绝并提示改用 --color-tone-map（Code={bo?.Code}）");
            var chroma = O("jpg"); chroma.ColorEngine = "engine"; chroma.Chroma = "4:2:0";
            Check(FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(chroma, "o.jpg", false) == null,
                "jpg 的色度子采样已承接 ⇒ 不再拒绝");
            Check(FfmpegGui.Services.ColorMapping.ImageEncoderArgs.MapEnginePixFmt("jpg", chroma)?.Contains("yuvj420p") == true,
                "色度确实落到 -pix_fmt（jpg 4:2:0 ⇒ yuvj420p）");
            // 负控（**不得为了绿而放宽**）：libwebp 只接受 yuv420p/yuva420p/bgra ⇒ 4:4:4 必须仍然拒绝
            var chBad = O("webp"); chBad.ColorEngine = "engine"; chBad.Chroma = "4:4:4";
            var bchBad = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(chBad, "o.webp", false);
            Check(bchBad?.Code == "EncoderUnsupportedSetting",
                $"负控：libwebp 无 4:4:4 ⇒ 仍拒绝且 Code=EncoderUnsupportedSetting（{bchBad?.Text}）");
            // ── GainMap 接入色彩引擎（**2026-09-16 契约更新**）──────────────────────────
            // 旧契约：`--jpeg-gain-map true` 被路由层整条拦下（此处断言 BlockReason 含 "GainMap"）。
            // 新契约：**JPEG 输出 + GainMap 由色彩引擎决策并执行** ⇒ 路由层不再拦截；
            // 非 JPEG 的拒绝点下移到**规划层**（`ColorStrategyPlanning` 的
            // `GainMapRequested && !Format.SupportsGainMap ⇒ Reject`）⇒ 负控必须改看 Plan。
            var gm = O("jpg"); gm.ColorEngine = "engine"; gm.JpegGainMap = true;
            Check(FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(gm, "o.jpg", false) == null,
                "jpg + JpegGainMap ⇒ 路由层不再拦截（GainMap 已接入引擎）");
            Check(FfmpegGui.Services.ColorMapping.ColorEngineRouter.ShouldRoute(gm, "o.jpg", false),
                "jpg + JpegGainMap ⇒ ShouldRoute 为真（确实交给引擎，而非仅“不报错”）");
            // 正向（走**真实装配**，与 CLI 同源）：HDR 源 + jpg + GainMap ⇒ 规划层不得 Reject。
            // 用 HDR 源是刻意的：GainMap 的主用途正是「HDR 底图 → SDR 底图 + 增益图」。
            var gmIt = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(gm, pq, out var whyGm);
            Check(gmIt != null && gmIt.GainMapRequested,
                $"jpg + JpegGainMap 装配意图并携带 GainMapRequested（reason={whyGm ?? "无"}）");
            var gmPlan = gmIt == null ? null : CE.Plan(gmIt);
            Check(gmPlan != null && gmPlan.Action != CA.Reject,
                $"jpg + JpegGainMap ⇒ 规划层不得 Reject（实为 {gmPlan?.Action}）");
            // 负控（不得为了绿而放宽）：非 JPEG 格式 + GainMap 仍必须被拒 + 给建议，
            // 且拒绝点是**规划层**而不是路由层（路由层现在放行一切 GainMap）。
            var gmPng = O("png"); gmPng.ColorEngine = "engine"; gmPng.JpegGainMap = true;
            var gmPngIt = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(gmPng, pq, out var whyGmP);
            Check(gmPngIt != null, $"png + JpegGainMap 仍可装配意图（reason={whyGmP ?? "无"}）");
            var gmPngPlan = gmPngIt == null ? null : CE.Plan(gmPngIt);
            Check(gmPngPlan != null && gmPngPlan.Action == CA.Reject && (gmPngPlan.Advice ?? "").Contains("AVIF"),
                $"负控：png + JpegGainMap ⇒ 规划层 Reject 并建议改 AVIF/JXL 或目标改 JPEG（{gmPngPlan?.Reason}）");
            // ── GainMap × 最长边几何缩放：**2026-09-18 起不再拦**（Part C 删除了那段死拦截）─────────
            // 【旧契约（已废）】`BlockReason.Code == "GeometryWithGainMap"`，负控「关掉最长边缩放 ⇒ 不拦」。
            // 【为什么废】那段拦截的前置条件在产品里**恒不成立**：`QueueProcessor.cs:421-470` 的预缩放中继
            //   （**所有后端共用**、在格式分发之前）已把超限输入换成缩过的临时件 ⇒ 引擎侧
            //   `BuildMaxDimensionScaleFilter` 恒返回 null ⇒ 判据恒假。实测删前/删后读数**完全相同**
            //   （exit 0、4621B、`images=2`/`iso min=0 max=2.3`/`mpf[1] soi=True`/探针 6/0）。
            // 【新契约】几何**不再**构成阻塞 ⇒ `BlockReason == null` **且** `ShouldRoute == true`。
            //   两条都断言：只断言"不报错"会让「路由说能走、执行体却不接」的形态溜过去。
            var gmGeo = O("jpg"); gmGeo.ColorEngine = "engine"; gmGeo.JpegGainMap = true;
            gmGeo.EnableMaxDimension = true; gmGeo.MaxDimension = 1280;
            var bgmGeo = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(gmGeo, "o.jpg", false);
            Check(bgmGeo == null,
                $"GainMap × 最长边缩放 ⇒ 路由层**不再**拦截（实得 Code={bgmGeo?.Code ?? "null"}；2026-09-18 Part C 删死拦截）");
            Check(FfmpegGui.Services.ColorMapping.ColorEngineRouter.ShouldRoute(gmGeo, "o.jpg", false),
                "GainMap × 最长边缩放 ⇒ ShouldRoute 为真（几何不再把该组合踢出引擎）");
            // 负控必须**仍有牙**：证明"几何不再拦"是**收窄**，不是"GainMap 一律放行"。
            // 只改**一个变量**（inputIsAnimated: false → true），其余与正例逐字相同 ——
            // 这样「把它改回 false」就必然转红，变异验证有一个干净的对照。
            // 为什么选动画这道门：删掉几何拦截后，`AnimatedInput` 是**唯一**还能把 GainMap 踢出引擎的
            //   路由级判据（实测：`anim_gif.gif` + `--jpeg-gain-map true` ⇒ 走专用管线）。
            //   ⇒ 这条负控正好钉住"剩下的那道门还在"。
            var gmGeoAnim = O("jpg"); gmGeoAnim.ColorEngine = "engine"; gmGeoAnim.JpegGainMap = true;
            gmGeoAnim.EnableMaxDimension = true; gmGeoAnim.MaxDimension = 1280;
            var bgmGeoAnim = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(gmGeoAnim, "o.jpg", true);
            Check(bgmGeoAnim?.Code == "AnimatedInput",
                $"负控：同一输入换成动画 ⇒ 仍须拦（Code={bgmGeoAnim?.Code}；证明放行的只是几何这一道门）");
            var ext = O("png"); ext.ColorEngine = "engine"; ext.EncoderBackend = FfmpegGui.Services.EncoderBackend.Cjxl;
            var bExt = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(ext, "o.png", false);
            Check(bExt?.Code == "ExternalBackend",
                $"用户选了外部编码器 ⇒ 引擎不抢决断权（Code={bExt?.Code}）");

            // ══════════════════════════════════════════════════════════════════════════════════
            // 6) **S2：路由拦截的结构化**（规划 §5-D；`BlockReason` 由 `string?` 改为 `EngineBlock?`）
            // ══════════════════════════════════════════════════════════════════════════════════
            // 要钉住的契约：
            //   ① **每个 Code 至少一条正向断言**（构造对应场景 ⇒ 断言 `Block.Code`，**绝不匹配 Text**）；
            //   ② 引擎可走时 `Block` 必须为 **null**（负控，代表格式）；
            //   ③ 语义**不得**回退成"任务做不了"：`BlockReason` 返回的是 `EngineBlock`，**不是** `ColorVerdict`
            //      —— 这条由**类型系统**保证（编译期），此处只需守住 Code 词表。
            //   ④ Code 词表**逐条对应** `BlockReason`/`UnconsumedOutputSettings` 的现有判据（不多不少）。

            // 6a) `OptionsNull`：防御性分支（实际不可达，但既然存在就必须有 Code，否则"逐条对应"有缺口）。
            var bNull = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(null, "o.png", false);
            Check(bNull?.Code == "OptionsNull", $"options 为空 ⇒ Code=OptionsNull（{bNull?.Text}）");

            // 6b) `AnimationScaleUnsupported`：动画缩放（原分支：UnconsumedOutputSettings 的 bad.Add("动画缩放")）。
            var animScale = O("png"); animScale.ColorEngine = "engine"; animScale.AnimationScaleW = 1920;
            var bAnimScale = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(animScale, "o.png", false);
            Check(bAnimScale?.Code == "AnimationScaleUnsupported",
                $"动画缩放未承接 ⇒ Code=AnimationScaleUnsupported（{bAnimScale?.Text}）");

            // 6c) `ProgressiveJpeg`：ffmpeg 的 mjpeg 无渐进能力（原分支：JpegProgressiveId != 0）。
            var prog = O("jpg"); prog.ColorEngine = "engine"; prog.JpegProgressiveId = 1;
            var bProg = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(prog, "o.jpg", false);
            Check(bProg?.Code == "ProgressiveJpeg",
                $"JPEG 渐进未承接 ⇒ Code=ProgressiveJpeg（{bProg?.Text}）");

            // 6d) 多项同时命中 ⇒ Code 取**列表首项**（= 文案里第一个原因），顺序即判据的固定顺序。
            //     ⚠ 这是**确定性契约**（GUI 要按 Code 分派解释），不是"随便取一个"。
            var multi = O("jpg"); multi.ColorEngine = "engine";
            multi.AnimationScaleW = 1920; multi.TonemapCurve = "reinhard"; multi.JpegProgressiveId = 1;
            var bMulti = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(multi, "o.jpg", false);
            Check(bMulti?.Code == "AnimationScaleUnsupported",
                $"多因同时命中 ⇒ Code 取首项 AnimationScaleUnsupported（Code={bMulti?.Code}）");
            // 文案锁（**不是**行为判据）：多因场景的 Text 必须与改动前的拼串**逐字相同**
            // （「、」分隔、顺序一致）。这里用**严格相等**而不是 Contains —— 后者才是"匹配自由文本"。
            Check(bMulti?.Text == "引擎编码出口尚未承接这些输出参数：动画缩放、"
                  + "旧 --tonemap-curve=reinhard（请改用 --color-tone-map）、"
                  + "JPEG 渐进（ffmpeg 的 mjpeg 编码器无此能力；请改用 cjpegli 后端，或把该项设回自动）",
                $"多因场景的 Text 与改动前逐字相同（实得「{bMulti?.Text}」）");
            // 6d-2) ⚠ **`Alternatives` 必须跨所有命中的判据聚合**（不是只给首项那条的替代）。
            //   这是"`Code` 保持单值"的**前提**：team-lead 裁定 ① 的理由正是"GUI 一键修正要的是
            //   **全部可行动替代**，而 `Alternatives` 是列表"。只给首项 ⇒ 那个理由就不成立。
            //   本用例同时命中 3 条判据 ⇒ 必须有 3 条替代，且顺序与判据顺序一致。
            var mAlts = bMulti?.Alternatives
                        ?? System.Array.Empty<FfmpegGui.Services.ColorMapping.ColorAlternative>();
            var mVals = new StringBuilder();
            for (int i = 0; i < mAlts.Count; i++) mVals.Append(mAlts[i].Value).Append(' ');
            Check(mVals.ToString() == "animation-scale-w color-tone-map jpeg-progressive ",
                $"多因命中的 Alternatives 必须**跨全部判据聚合**（实得「{mVals}」）");

            // 6e) `Alternatives`：**只在代码本来就知道替代时填**。填了的必须有，没填的必须为空
            //     —— 两个方向都断言，否则"哪天有人随手补一个编造的替代"无人发现。
            // ⚠ 取值用**空安全**投影（`Block` 为 null 时给 `(0, default, "")`）：变异让某条判据恒假时
            //   本段必须**转红**而不是抛 NullReference（抛异常会把"哪条断言失败"掩盖掉）。
            (int n, FfmpegGui.Services.ColorMapping.AlternativeKind kind, string value) Alt1(
                FfmpegGui.Services.ColorMapping.EngineBlock? b)
                => b != null && b.Alternatives.Count > 0
                   ? (b.Alternatives.Count, b.Alternatives[0].Kind, b.Alternatives[0].Value)
                   : (0, default, "");
            var ALT = FfmpegGui.Services.ColorMapping.AlternativeKind.Option;
            Check(Alt1(bExt) == (1, ALT, "encoder"),
                $"ExternalBackend 给出替代「改 -e Ffmpeg」（实得 {Alt1(bExt)}）");
            // ⚠ 2026-09-18：`GeometryWithGainMap` **已不再被生产**（Part C 删掉那段死拦截）
            //   ⇒ 该组合不再有 `Block`，也就无从谈"替代项"。这里保留一条**反向**断言：
            //   若哪天有人把拦截加回去，`Alt1(bgmGeo)` 会变成 `(1, Option, max-dimension-enabled)`
            //   ⇒ 本条转红（等于给 Part C 的删除上了一把锁）。
            Check(Alt1(bgmGeo).n == 0,
                $"GeometryWithGainMap 已不再生产 ⇒ 该组合无替代项（实得 {Alt1(bgmGeo)}）");
            Check(Alt1(bo) == (1, ALT, "color-tone-map"),
                $"LegacyTonemapCurve 给出替代「改用 --color-tone-map」（实得 {Alt1(bo)}）");
            Check(Alt1(bProg) == (1, ALT, "jpeg-progressive"),
                $"ProgressiveJpeg 给出替代「把 --jpeg-progressive 设回自动」（实得 {Alt1(bProg)}）");
            Check(Alt1(bAnimScale) == (1, ALT, "animation-scale-w"),
                $"AnimationScaleUnsupported 给出替代「把 --animation-scale-w 设回 0」（实得 {Alt1(bAnimScale)}）");
            // 反向：**不该编替代**的三处必须留空（各自的理由见 Router 内的注释）。
            Check(Alt1(bppm).n == 0 && Alt1(bAnim).n == 0 && Alt1(bchBad).n == 0 && Alt1(bNull).n == 0,
                "FormatNotSupported / AnimatedInput / EncoderUnsupportedSetting / OptionsNull "
                + $"⇒ Alternatives 必须为空（实得 {Alt1(bppm).n}/{Alt1(bAnim).n}/{Alt1(bchBad).n}/{Alt1(bNull).n}）");

            // 6f) **负控**：引擎可走的代表格式 ⇒ `Block` 必须为 **null**（漏一个格式就是"假路由"）。
            var negBad = new StringBuilder();
            foreach (var nf in new[] { "png", "webp", "tif", "gif", "jpg", "jxl", "avif" })
            {
                var okF = O(nf); okF.ColorEngine = "engine";
                var bF = FfmpegGui.Services.ColorMapping.ColorEngineRouter.BlockReason(okF, "o." + nf, false);
                if (bF != null) negBad.Append($"{nf}={bF.Code} ");
            }
            Check(negBad.Length == 0,
                $"负控：引擎可走的代表格式 Block 必须为 null（非空=[{negBad}]）");

            _ = R;
        }

        /// <summary>
        /// peak —— 端到端验“源峰值读取”：ffprobe 静态元数据 → ColorIntentFactory → Plan.Tone。
        ///
        /// 素材由 verify-color-peak.ps1 现造，故意让 **MaxCLL=600 与 mastering=4000 不同**，
        /// 于是一次数值就能分辨四种结果：600=对（键名 max_content、MaxCLL 优先）；
        /// 4000=又取了两者较大值（过度压暗）；0.4=有理数被重复换算；0=探测失效（旧代码正是这个）。
        /// 另跑无元数据的 HDR 图：必须回退到源曲线名义峰值，且**日志要写明是回退**。
        /// </summary>
        private static void ProbePeak(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: ServiceProbe peak <file> [expectMetaPeak] [userPeakNits]");
                _fail++;
                return;
            }
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string file = args[1];
            double expect = args.Length >= 3 && double.TryParse(args[2], System.Globalization.NumberStyles.Any, ci, out var e) ? e : -1;
            double userPeak = args.Length >= 4 && double.TryParse(args[3], System.Globalization.NumberStyles.Any, ci, out var u) ? u : 0;
            if (!File.Exists(file)) { Check(false, $"素材存在：{file}（由 verify-color-peak.ps1 生成）"); return; }

            var m = FfmpegGui.Services.FfmpegCommandBuilder.ProbeInputColorMetadata(file);
            Console.WriteLine($"trc={m.colorTrc ?? "-"} metaPeak={m.peakNits:F2} metaSource={m.peakSource ?? "-"}");
            if (expect >= 0)
                Check(System.Math.Abs(m.peakNits - expect) < System.Math.Max(0.01, expect * 1e-3)
                      && (expect == 0 || !string.IsNullOrEmpty(m.peakSource)),
                    $"元数据峰值 = {expect}（实读 {m.peakNits:F2}，来源 {m.peakSource ?? "-"}）");

            var o = new FfmpegGui.Models.FfmpegOptions
            {
                Format = "png", ColorSpace = "sRGB", ColorStrategy = ST.Recommended,
                ColorToneMap = "hable", BitDepth = 8, ColorHdrPeakNits = userPeak,
            };
            var it = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(o, file, out var why);
            Check(it != null, $"选项→意图可装配（{(it == null ? why : "OK")}）");
            if (it == null) return;
            Console.WriteLine($"intentPeak={it.HdrPeakNits:F2} intentSource={it.HdrPeakSource ?? "(无→策略层回退)"}");
            double wantIntent = userPeak > 0 ? userPeak : m.peakNits;
            Check(System.Math.Abs(it.HdrPeakNits - wantIntent) < 1e-6,
                  $"优先级 用户({userPeak}) > 元数据({m.peakNits:F2}) ⇒ 意图取 {wantIntent:F2}");

            var p = CE.Plan(it);
            if (p.Tone == null) { Check(false, $"色调映射未挂载（action={p.Action} reason={p.Reason}）"); return; }
            double wantPeak = wantIntent > 0 ? wantIntent : (it.Source?.NitsOfLinearOne ?? 0);
            Console.WriteLine($"tone={p.Tone} colorLoss={p.ColorLoss}");
            Console.WriteLine($"reason={p.Reason}");
            Check(System.Math.Abs(p.Tone.PeakNits - wantPeak) < System.Math.Max(0.01, wantPeak * 1e-3)
                  && p.Tone.Active && p.Tone.Headroom > 1.0 + 1e-6,
                  $"计划峰值={wantPeak:F2} 且真的激活（peak={p.Tone.PeakNits:F2} headroom={p.Tone.Headroom:F2}）");
            Check(p.Reason.Contains("峰值来源="), "日志写明峰值来源（不静默）");
            Check(userPeak > 0 ? p.Reason.Contains("用户 --color-hdr-peak")
                               : (m.peakNits > 0 ? p.Reason.Contains(m.peakSource ?? "§") : p.Reason.Contains("名义峰值")),
                  $"来源文案与实际一致（{(userPeak > 0 ? "用户" : m.peakNits > 0 ? m.peakSource : "名义回退")}）");
        }

        /// <summary>
        /// psnr &lt;图A&gt; &lt;图B&gt; [阈dB] —— 两条执行路径产物是否一致的通用度量。
        /// 统一解到 rgb48 再比（消除 pix_fmt/位深差异带来的假差异），峰值按**实际满量程** 65535 算。
        /// </summary>
        private static async Task ProbePsnr(string[] args)
        {
            if (args.Length < 3) { Console.WriteLine("usage: ServiceProbe psnr <a> <b> [thresholdDb]"); _fail++; return; }
            double thr = args.Length >= 4 && double.TryParse(args[3], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 55;
            var (wa, ha) = await FfmpegGui.Services.ColorMapping.RawColorPipeline.ProbeSizeAsync(args[1]).ConfigureAwait(false);
            var (wb, hb) = await FfmpegGui.Services.ColorMapping.RawColorPipeline.ProbeSizeAsync(args[2]).ConfigureAwait(false);
            Check(wa > 0 && wa == wb && ha == hb, $"两图尺寸相同且可探测（{wa}x{ha} vs {wb}x{hb}）");
            if (wa <= 0 || wa != wb || ha != hb) return;
            var a = await FfmpegGui.Services.ColorMapping.RawColorPipeline.DecodeToRgb48Async(args[1], wa, ha).ConfigureAwait(false);
            var b = await FfmpegGui.Services.ColorMapping.RawColorPipeline.DecodeToRgb48Async(args[2], wb, hb).ConfigureAwait(false);
            double psnr = CM.Psnr16(a, b);
            int maxDiff = 0;
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i + 1 < n; i += 2)
                maxDiff = Math.Max(maxDiff, Math.Abs((a[i] | (a[i + 1] << 8)) - (b[i] | (b[i + 1] << 8))));
            Console.WriteLine($"psnr={psnr:F2}dB maxDiff={maxDiff}/65535 尺寸={wa}x{ha}");
            Check(psnr >= thr, $"两路产物 PSNR ≥ {thr}dB（实 {psnr:F2}）");
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
        private static async System.Threading.Tasks.Task ProbePeakMeasure(string[] args)
        {
            if (args.Length < 3) { Console.WriteLine("usage: ServiceProbe peakmeasure <png> <maxCode 0..65535> [userPeakNits]"); _fail++; return; }
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string src = args[1];
            if (!int.TryParse(args[2], out int maxCode) || maxCode < 0 || maxCode > 65535)
            { Check(false, $"最高码值参数非法：{args[2]}"); return; }
            double userPeak = args.Length >= 4 && double.TryParse(args[3], System.Globalization.NumberStyles.Any, ci, out var u) ? u : 0;
            if (!File.Exists(src)) { Check(false, $"素材存在 {src}（由 verify-color-peak.ps1 生成）"); return; }

            // ① 内核级：表路径与解析式路径必须同值
            var spec0 = new FfmpegGui.Services.ColorMapping.TransformSpec
            {
                SrcCurve = TC.Pq, DstCurve = TC.Srgb, LinearScale = TC.PqPeakNits / TC.SdrWhiteNits,
                Tone = new FfmpegGui.Services.ColorMapping.ToneMapParams
                { Mode = FfmpegGui.Services.ColorMapping.ToneMapMode.Hable, PeakNits = TC.PqPeakNits },
            };
            byte[] synth = SyntheticFrame16(64, 48);
            double vTab = CK.MeasurePeakLinearRgb48(synth, 64 * 48, spec0, CK.BuildTables(TC.Pq));
            double vAna = CK.MeasurePeakLinearRgb48(synth, 64 * 48, spec0, null);
            Check(System.Math.Abs(vTab - vAna) <= System.Math.Max(1e-4, vAna * 1e-4),
                $"实测内核：表路径与解析式同值（{vTab:F6} vs {vAna:F6}）");

            // ② 端到端：走生产管道（解码→实测→变换→编码）
            var o = new FfmpegGui.Models.FfmpegOptions
            {
                Format = "png", ColorSpace = "sRGB", ColorStrategy = ST.Recommended,
                ColorToneMap = "hable", BitDepth = 16, ColorHdrPeakNits = userPeak,
            };
            var it = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(o, src, out var why);
            Check(it != null, $"选项→意图（{(it == null ? why : "OK")}）");
            if (it == null) return;
            var plan = CE.Plan(it);
            if (plan.Tone == null) { Check(false, $"tonemap 未挂载（{plan.Reason}）"); return; }
            Console.WriteLine($"plan.Tone={plan.Tone} MeasurePeak={plan.Tone.MeasurePeak}");
            Check(userPeak > 0 ? !plan.Tone.MeasurePeak : plan.Tone.MeasurePeak,
                  userPeak > 0 ? "用户已指定峰值 ⇒ 不标记实测" : "无用户值/无元数据 ⇒ 标记待实测");

            var spec = plan.ToSpec();
            string outPng = Path.Combine(Path.GetTempPath(), $"pmout_{Guid.NewGuid():N}.png");
            var logSb = new StringBuilder();
            var st = await FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                src, outPng, spec, plan, s => logSb.Append(s), default, 1).ConfigureAwait(false);
            double expect = maxCode > 0 ? TC.Pq.ToLinear(maxCode / 65535.0) * TC.PqPeakNits : 0;
            Console.WriteLine($"maxCode={maxCode} 期望={expect:F2}nit 实测={st.MeasuredPeakNits:F2}nit");
            Console.WriteLine($"spec.Tone={spec.Tone} log={logSb.ToString().Trim().Replace('\n', ' ')}");
            try { File.Delete(outPng); } catch { }

            Check(System.Math.Abs(plan.Tone.PeakNits - (userPeak > 0 ? userPeak : TC.PqPeakNits)) < 1e-6,
                  $"执行层不污染规划层 Plan.Tone（仍为 {(userPeak > 0 ? userPeak : TC.PqPeakNits):F0}nit）");
            Check(!spec.Tone!.MeasurePeak, "实测标记已清除（不会重复扫描）");
            if (userPeak > 0)
            {
                Check(st.MeasuredPeakNits == 0, $"用户指定峰值时确实未实测（得 {st.MeasuredPeakNits}）");
                Check(System.Math.Abs(spec.Tone.PeakNits - userPeak) < 1e-6, "spec 峰值仍等于用户值");
                return;
            }
            // 两条口径分别验：**实测到了什么**（原始值，哪怕落在钳制区间外）与**策略采了什么**（钳制后）。
            // 否则“很暗的图”会因钳制到 203nit 而看不出实测本身错没错。
            Check(System.Math.Abs(st.MeasuredPeakNits - expect) <= System.Math.Max(1.0, expect * 0.02),
                  $"实测原始值 = 期望 {expect:F2}nit（±2%，实 {st.MeasuredPeakNits:F2}）");
            double wantApplied = System.Math.Clamp(expect, TC.SdrWhiteNits, TC.PqPeakNits);
            Check(System.Math.Abs(spec.Tone.PeakNits - wantApplied) <= System.Math.Max(1.0, wantApplied * 0.02),
                  $"采用值 = clamp(期望,{TC.SdrWhiteNits:F0}…{TC.PqPeakNits:F0}) = {wantApplied:F2}nit（实 {spec.Tone.PeakNits:F2}）");
            Check(spec.Tone.Active == (wantApplied > TC.SdrWhiteNits * (1 + 1e-6)),
                  $"Active 与采用值一致（active={spec.Tone.Active} headroom={spec.Tone.Headroom:F3}）"
                  + (wantApplied <= TC.SdrWhiteNits ? "——图很暗 ⇒ 恒等，**绝不因图暗而增亮**" : ""));
            Check(logSb.ToString().Contains("峰值来源=整帧实测"), "用户可见日志写明实测来源");
        }

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
                                                   || (passthrough && string.Equals(claimP, inP, StringComparison.OrdinalIgnoreCase))
                                                   || implicitSrgb;
                        bool tLanded = claimT == null || string.Equals(claimT, gotT, StringComparison.OrdinalIgnoreCase)
                                                   || string.Equals(claimT, zsT, StringComparison.OrdinalIgnoreCase)
                                                   || (passthrough && string.Equals(claimT, inT, StringComparison.OrdinalIgnoreCase))
                                                   || implicitSrgb;
                        if (!pLanded || !tLanded)
                        {
                            mismatch++;
                            // 自带命令行摘录：不一致的行必须能直接看出哪一段与宣称对不上，
                            // 不留给下轮去猜（本仓库多次因“只看汇总行”错过真错）。
                            string excerpt = outSide.Length > 170 ? outSide[..170] + "…" : outSide;
                            Console.WriteLine($"  ≠ {sn}/{fmt}/{tg} 宣称={(claimP ?? "—")}/{(claimT ?? "—")}  "
                                + $"输出侧={Has(gotP)}/{Has(gotT)}  zscale={Has(zsP)}/{Has(zsT)}  输入侧={Has(inP)}/{Has(inT)} 直通={passthrough} 源sRGB={srcSrgb}");
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

        /// <summary>
        /// engine —— 自研引擎端到端跑通 + 与 legacy（ffmpeg 色彩链）的 A/B 像素对比。
        /// 目的：给“是否把默认值从 legacy 切到 engine”提供数据，而不是凭感觉改。
        /// 不经过 QueueProcessor（那里有 4 个调用点与后置顺序要读），只验管线本身。
        /// </summary>
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
        private static async System.Threading.Tasks.Task ProbeGeometry(string[] args)
        {
            string ff = AppSettingsService.Current.FfmpegPath;
            if (string.IsNullOrWhiteSpace(ff) || !File.Exists(ff))
            { Check(false, "geometry：未找到 ffmpeg（AppSettings 未初始化 / publish/PLAN 缺失）"); return; }

            const string ScaleMarker = "几何缩放（最长边限制）";     // 源码 RawColorPipeline.cs:128 的原样前缀
            string root = args.Length >= 2 ? args[1] : "tests/output/validate";
            // ⚠ 运行级隔离（GUID）：固定目录会被**并发同名实例**互删 ⇒ 假红（TESTING §6 第 66 条）
            string dir = Path.Combine(root, "geometry_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            Console.WriteLine($"geometry-out={dir}");
            var created = new List<string>();      // 结尾按计数自清（不整目录删 ⇒ 避开宿主批量删除守卫）

            try
            {
                // ── 源素材自备：本探针自己生成，绝不依赖别处遗留产物（干净目录上必须能跑）──
                async System.Threading.Tasks.Task<string> MakeSource(int sw, int sh)
                {
                    var p = Path.Combine(dir, $"src_{sw}x{sh}.png");
                    int ec = await FfmpegGui.Services.FfmpegRunner.RunAsync(
                        $"-y -hide_banner -loglevel error -f lavfi -i \"testsrc2=size={sw}x{sh}:duration=1\" "
                      + $"-frames:v 1 \"{p}\"", null, ff).ConfigureAwait(false);
                    if (ec != 0 || !File.Exists(p))
                        throw new InvalidOperationException($"源生成失败 (exit {ec})");
                    if (!created.Contains(p)) created.Add(p);      // 去重：同一尺寸的源在多个用例间复用
                    return p;
                }

                // 用例表：源尺寸 / 最长边限制 / 期望产出。
                // 期望值全部来自 2026-09-17 的**实测**（本文件下方的 boundary 段把「自己算会漂开」也钉住）。
                //   A 512x384 限 128 → 滤镜 scale=128:-2  → 128x96（96 = 384*128/512，恰为整数）
                //   B 512x384 限  32 → 滤镜 scale=32:-2   →  32x24
                //   C 384x512 限  32 → 滤镜 scale=-2:32   →  24x32（**纵向**分支，覆盖 -2 锁高度那条）
                //   D 512x300 限 100 → 滤镜 scale=100:-2  → 100x58（58.59375 ⇒ **非整数**那条边，
                //        自己算 round(58.59)=59 / 取偶到 60 都会与 ffmpeg 的 58 漂开）
                //   E 512x384 限 1024 → 滤镜 null（不超限）→ 512x384（**负控**：尺寸不变 + 无缩放日志）
                var cases = new (string id, int sw, int sh, int maxDim, int expW, int expH)[]
                {
                    ("A-landscape-128", 512, 384, 128, 128, 96),
                    ("B-landscape-32",  512, 384,  32,  32,  24),
                    ("C-portrait-32",   384, 512,  32,  24,  32),
                    ("D-fractional-100",512, 300, 100, 100,  58),
                    ("E-negative",      512, 384, 1024, 512, 384),
                };

                foreach (var c in cases)
                {
                    string src = await MakeSource(c.sw, c.sh).ConfigureAwait(false);
                    var (pw, ph) = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                        .ProbeSizeAsync(src, ff).ConfigureAwait(false);
                    Check(pw == c.sw && ph == c.sh, $"{c.id} 源尺寸自备 = {c.sw}x{c.sh}（ffprobe 读回 {pw}x{ph}）");

                    var o = new FfmpegGui.Models.FfmpegOptions
                    {
                        Format = "png", ColorStrategy = ST.Recommended, BitDepth = 8,
                        EnableMaxDimension = true, MaxDimension = c.maxDim,
                    };
                    // 单一真值：预缩放路径与引擎路径用的是**同一个**函数，这里把它取出来做对照
                    string? filter = FfmpegCommandBuilder.BuildMaxDimensionScaleFilter(o, src);

                    var it = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(o, src, out var why);
                    if (it == null) { Check(false, $"{c.id} 选项→意图失败：{why}"); continue; }
                    var plan = CE.Plan(it);
                    if (!plan.IsOk) { Check(false, $"{c.id} 计划被拒：{plan.Reason}"); continue; }

                    string engOut = Path.Combine(dir, $"eng_{c.id}.png");
                    created.Add(engOut);
                    var sb = new StringBuilder();
                    var gate = new object();          // 引擎日志可能来自行带并行线程
                    FfmpegGui.Services.ColorMapping.RawColorPipeline.Stats st;
                    try
                    {
                        st = await FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                            src, engOut, plan.ToSpec(), plan,
                            s => { lock (gate) sb.Append(s); }, default, 1, ff, o).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Check(false, $"{c.id} 引擎异常：{ex.GetType().Name}: {ex.Message}");
                        continue;
                    }

                    string log = sb.ToString();
                    bool scaledLog = log.Contains(ScaleMarker);
                    var (ew, eh) = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                        .ProbeSizeAsync(engOut, ff).ConfigureAwait(false);
                    bool expectScaled = !string.IsNullOrEmpty(filter);
                    Console.WriteLine($"  · {c.id} 滤镜={filter ?? "(null)"} 引擎产出={ew}x{eh} "
                        + $"Stats={st.Width}x{st.Height} 缩放日志={scaledLog}");
                    Console.WriteLine($"geometry-case id={c.id} src={c.sw}x{c.sh} maxdim={c.maxDim} "
                        + $"expect={c.expW}x{c.expH} engine={ew}x{eh} stats={st.Width}x{st.Height} "
                        + $"log={(scaledLog ? "scaled" : "none")} prescale={(filter == null ? "n/a" : "see-next")}");

                    // ① 产出尺寸（长边锁定 + -2 自动偶数）
                    Check(ew == c.expW && eh == c.expH,
                        $"{c.id} 引擎产出尺寸 = {c.expW}x{c.expH}（ffprobe 读回 {ew}x{eh}）");
                    // ② Stats 反推尺寸 == 实际产物（RawColorPipeline.cs:120-129 那段零覆盖的逻辑）
                    Check(st.Width == ew && st.Height == eh,
                        $"{c.id} Stats 反推尺寸 == 实际产物（{st.Width}x{st.Height} vs {ew}x{eh}）");
                    // ③ 日志证据：这段代码真的跑了 / 负控真的没跑
                    Check(scaledLog == expectScaled, expectScaled
                        ? $"{c.id} 日志出现「{ScaleMarker}」⇒ 引擎几何缩放段确实执行"
                        : $"{c.id} 负控：不超限 ⇒ 日志**不得**出现「{ScaleMarker}」（实际 {(scaledLog ? "出现" : "未出现")}）");

                    // ④ 引擎缩放 vs QueueProcessor 预缩放：同滤镜同尺寸实测对照
                    if (filter == null)
                    {
                        Console.WriteLine($"  · {c.id} 预缩放路径：滤镜为 null ⇒ QueueProcessor 不会预缩放（N/A）");
                    }
                    else
                    {
                        string pre = Path.Combine(dir, $"pre_{c.id}.png");
                        created.Add(pre);
                        int pec = await FfmpegGui.Services.FfmpegRunner.RunAsync(
                            $"-y -hide_banner -loglevel error -i \"{src}\" -vf \"{filter}\" "
                          + $"-compression_level 0 \"{pre}\"", null, ff).ConfigureAwait(false);
                        var (qw, qh) = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                            .ProbeSizeAsync(pre, ff).ConfigureAwait(false);
                        Console.WriteLine($"geometry-prescale id={c.id} engine={ew}x{eh} prescale={qw}x{qh} "
                            + $"filter=\"{filter}\"");
                        Check(pec == 0 && qw > 0, $"{c.id} 预缩放路径产出可用（exit {pec}）");
                        Check(qw == ew && qh == eh,
                            $"{c.id} 引擎缩放 == 预缩放（{ew}x{eh} vs {qw}x{qh}）⇒ 删预缩放不会改变几何口径");
                    }
                }

                // ── 边界坑：把源码注释里那次实测（42x32）钉住 ──
                // ⚠ 这条滤镜**不是** BuildMaxDimensionScaleFilter 为 512x384 选出的（横图锁宽度 ⇒ scale=32:-2），
                //   它是源码注释（RawColorPipeline.cs:99-100）里那次直接实测所用的滤镜。保留它是为了把
                //   「自己算会漂开」这个**事实**钉住：512*32/384 = 42.67 ⇒ 自己算 round=43 / 取偶=44，
                //   而 ffmpeg 的 `-2` 实测给 42。正因如此 :120-129 只能用**解码产物长度**反推另一条边。
                {
                    string src = await MakeSource(512, 384).ConfigureAwait(false);
                    string b = Path.Combine(dir, "boundary_scaleV32.png");
                    created.Add(b);
                    int ec = await FfmpegGui.Services.FfmpegRunner.RunAsync(
                        $"-y -hide_banner -loglevel error -i \"{src}\" "
                      + $"-vf \"scale=-2:32:flags=lanczos\" -compression_level 0 \"{b}\"", null, ff)
                        .ConfigureAwait(false);
                    var (bw, bh) = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                        .ProbeSizeAsync(b, ff).ConfigureAwait(false);
                    int naiveRound = (int)Math.Round(512.0 * 32 / 384);   // 42.67 → 43
                    int naiveEvenUp = (naiveRound + 1) / 2 * 2;           // 43 → 44
                    Console.WriteLine($"geometry-boundary filter=\"scale=-2:32\" actual={bw}x{bh} "
                        + $"selfcompute={512.0 * 32 / 384:F2} naiveRound={naiveRound} naiveEvenUp={naiveEvenUp}");
                    Check(ec == 0 && bw == 42 && bh == 32,
                        $"边界：512x384 用 scale=-2:32 实测 42x32（实 {bw}x{bh}）；"
                      + $"自己算 42.67 会得 {naiveRound}/{naiveEvenUp} ⇒ 必须按解码产物长度反推");
                }

                // ── 删预缩放的**像素级**安全验证（非恒等色彩计划 + 缩放）──
                // 上面 A–E 只比了**尺寸**，而且用的是直通计划（Action=None）⇒ 测不到「先缩放后映射」与
                // 「先映射后缩放」的差异。本段补上**非恒等**计划下的像素对照，回答唯一那个还没答的问题：
                // **把 QueueProcessor 的预缩放删掉、改由引擎在解码里缩放，像素会不会变？**
                //
                // 两条路径（对照今天 vs 删后）：
                //   ① 今天：ffmpeg 预缩放（只缩放、不改色彩）→ 引擎解码该产物 + 色彩映射  ⇒ **缩放 → 映射**
                //   ② 删后：引擎在同一次解码里 `-vf format=rgb48le,scale=…` 缩放 + 色彩映射 ⇒ **缩放 → 映射**
                // ⚠ 关键结论（读码可得，本段用像素实测背书）：**两条路径的顺序相同**，差别只在 ① 多一次
                //   「缩放产物落盘成 PNG → 再解码」的量化往返 ⇒ 预期 ① 略差、差异应很小。
                //   真正的**反序**只出现在 legacy 路径（`--color-engine legacy`）：色彩链在
                //   `FfmpegCommandBuilder.cs:121`、scale 在 `:535` ⇒ legacy = 映射 → 缩放。那是既有的
                //   引擎-vs-legacy 差异，与「删不删预缩放」无关，不在本段射程内。
                {
                    string src = await MakeSource(512, 384).ConfigureAwait(false);
                    const int md = 128, ew = 128, eh = 96;
                    // 非恒等计划：源无任何色彩元数据 ⇒ 引擎按惯例假定 sRGB；目标显式给 Display P3
                    // ⇒ sRGB→P3 真映射（与 ProbeEngineAb 同一手法）。恒等计划下本段无意义，故先断言非恒等。
                    var o = new FfmpegGui.Models.FfmpegOptions
                    {
                        Format = "png", ColorSpace = "Display P3", ColorStrategy = ST.Recommended,
                        BitDepth = 16, EnableMaxDimension = true, MaxDimension = md,
                    };
                    var it = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(o, src, out var why);
                    if (it == null) { Check(false, $"像素对照：选项→意图失败：{why}"); }
                    else
                    {
                        var plan = CE.Plan(it);
                        Check(plan.IsOk && plan.Action != CA.None,
                            $"像素对照：计划为非恒等（Action={plan.Action}，目标 Display P3）—— 恒等计划测不到顺序差异");

                        string pre = Path.Combine(dir, "px_pre_128.png");
                        created.Add(pre);
                        string preFilter = FfmpegCommandBuilder.BuildMaxDimensionScaleFilter(o, src) ?? "";
                        int pec = await FfmpegGui.Services.FfmpegRunner.RunAsync(
                            $"-y -hide_banner -loglevel error -i \"{src}\" -vf \"{preFilter}\" "
                          + $"-compression_level 0 \"{pre}\"", null, ff).ConfigureAwait(false);

                        var sb1 = new StringBuilder(); var g1 = new object();
                        var sb2 = new StringBuilder(); var g2 = new object();
                        string out1 = Path.Combine(dir, "px_today_prescale.png");
                        string out2 = Path.Combine(dir, "px_after_engine.png");
                        created.Add(out1); created.Add(out2);
                        var st1 = await FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                            pre, out1, plan.ToSpec(), plan, s => { lock (g1) sb1.Append(s); },
                            default, 1, ff, o).ConfigureAwait(false);
                        var st2 = await FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                            src, out2, plan.ToSpec(), plan, s => { lock (g2) sb2.Append(s); },
                            default, 1, ff, o).ConfigureAwait(false);

                        Check(pec == 0 && st1.Width == ew && st1.Height == eh
                              && st2.Width == ew && st2.Height == eh,
                            $"像素对照：两条路径尺寸均 {ew}x{eh}（今天 {st1.Width}x{st1.Height} / 删后 {st2.Width}x{st2.Height}）");
                        // 日志证据：今天那路不该缩放（输入已缩过），删后那路必须缩放
                        Check(!sb1.ToString().Contains(ScaleMarker) && sb2.ToString().Contains(ScaleMarker),
                            $"像素对照：缩放归属正确（今天=预缩放、引擎不缩放；删后=引擎缩放）");

                        var pa = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                            .DecodeToRgb48Async(out1, ew, eh).ConfigureAwait(false);
                        var pb = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                            .DecodeToRgb48Async(out2, ew, eh).ConfigureAwait(false);
                        double mse = 0, meanA = 0, meanB = 0; long n = 0; int maxDiff = 0;
                        for (int i = 0; i + 1 < pa.Length && i + 1 < pb.Length; i += 2)
                        {
                            int x = pa[i] | (pa[i + 1] << 8), y = pb[i] | (pb[i + 1] << 8);
                            double d = (x - y) / 65535.0;
                            mse += d * d; meanA += x; meanB += y; n++;
                            maxDiff = Math.Max(maxDiff, Math.Abs(x - y));
                        }
                        mse /= Math.Max(1, n); meanA /= Math.Max(1, n); meanB /= Math.Max(1, n);
                        double psnr = mse > 0 ? 10 * Math.Log10(1.0 / mse) : 99;
                        Console.WriteLine($"geometry-pixels prescale-vs-engine psnr={psnr:F2}dB maxDiff={maxDiff}/65535 "
                            + $"meanToday={meanA / 65535.0:F4} meanAfter={meanB / 65535.0:F4} samples={n}");
                        Check(psnr >= 40.0,
                            $"像素对照：今天(预缩放→映射) vs 删后(引擎内缩放→映射) PSNR ≥ 40dB（实 {psnr:F2}，{n} 样本）");
                        Check(maxDiff <= 3000,
                            $"像素对照：最大单通道差 ≤ 3000/65535（实 {maxDiff}）—— 差异只应来自一次 PNG 量化往返");
                        Check(Math.Abs(meanA - meanB) / 65535.0 < 0.005,
                            $"像素对照：两路平均亮度一致（差 {Math.Abs(meanA - meanB) / 65535.0:F5}）⇒ 删预缩放不偏色");
                    }
                }

                // ═══ 顺序统一（R1）：scale 必须插在「第一条色彩变换之前」（2026-09-18）═══
                // 判据只有一份：`FfmpegCommandBuilder.InsertScaleFilterAtChainHead` —— legacy 的 `:535`
                // 与 `QueueProcessor.BuildPipeColorArgs` 的管道路径同源。
                // 本段**读命令行、不读日志**；token 按 `,` 切分；判别式
                //   `^scale=\d+:-2:flags=lanczos$` / `^scale=-2:\d+:flags=lanczos$`
                // **`-2` 是分水岭**：`AnimationScaleW` 的 `scale={W}:-1:` 与 preScale 的
                // `scale=ceil(iw/2)*2:` 都不匹配 ⇒ 不会把「另一个合法 token」当成本项。
                // ⚠ 载体必须按管线**显式分派**（§6 第 74 条：载体选错 ⇒ 命中另一个合法 token ⇒ 断言**恒真**，
                //   比恒红更危险）。本段把「形态自检」放在每条位置断言**之前**（§6 第 77 条）。
                {
                    static string[] Tok(string chain) => chain.Split(',');

                    // 返回判别式命中的下标；无命中 -1；命中**多于一处** -2（计数异常，单独暴露）
                    static int IdxOfScale(string[] t)
                    {
                        int hit = -1, n = 0;
                        foreach (var (s, i) in t.Select((s, i) => (s, i)))
                            if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^scale=(\d+:-2|-2:\d+):flags=lanczos$"))
                            { n++; if (hit < 0) hit = i; }
                        return n == 1 ? hit : (n == 0 ? -1 : -2);
                    }

                    static int IdxOfColor(string[] t)
                    {
                        for (int i = 0; i < t.Length; i++)
                            if (t[i].StartsWith("zscale=") || t[i].StartsWith("tonemap=")
                                || t[i].StartsWith("colorchannelmixer=")) return i;
                        return -1;
                    }

                    // 决策层的「偶数守卫」token（`FfmpegCommandBuilder.Decision.cs:316` 的 preScale）——
                    // ⚠ 它**不是**色彩变换，但它是**另一个合法** `scale=…:flags=lanczos` token：
                    //   `scale=ceil(iw/2)*2:ceil(ih/2)*2:flags=lanczos`。
                    //   它恰好落在 R1 锚点的**前驱**位置上（链形如 `format=…,{preScale}zscale=…`）
                    //   ⇒ 「前驱必须是 format=」这条**冻结判据在带 preScale 的形态上有假红**（实测踩到）。
                    static bool IsEvenGuard(string t) =>
                        System.Text.RegularExpressions.Regex.IsMatch(
                            t, @"^scale=ceil\(iw/2\)\*2:ceil\(ih/2\)\*2:flags=lanczos$");

                    // 取 `<opt> <值>`：值可能带字面引号（GIF complex / 管道路径），也可能不带
                    // （legacy `-vf` 走 `string.Join(" ", args)`，链是裸 token）——两种都要能吃。
                    static string? TakeOpt(string cmd, string opt)
                    {
                        int i = cmd.IndexOf(opt + " ", StringComparison.Ordinal);
                        if (i < 0) return null;
                        int s = i + opt.Length + 1;
                        if (s < cmd.Length && cmd[s] == '"')
                        {
                            int e = cmd.IndexOf('"', s + 1);
                            return e > s ? cmd.Substring(s + 1, e - s - 1) : null;
                        }
                        int sp = cmd.IndexOf(' ', s);
                        return sp > s ? cmd.Substring(s, sp - s) : cmd.Substring(s);
                    }

                    string r1src = await MakeSource(512, 384).ConfigureAwait(false);

                    // HDR（PQ/BT.2020）源：用来逼出 **S2 形态**（`format=yuv444p,tonemap=…,format=rgb48le,zscale=…`
                    // ⇒ 链里有**两个**色彩变换）—— M-h 必须在这个形态上才测得出来（单一色彩变换时 M-g ≡ M-h）。
                    async System.Threading.Tasks.Task<string> MakeHdrSource(int sw, int sh)
                    {
                        var p = Path.Combine(dir, $"hdrsrc_{sw}x{sh}.png");
                        int ec = await FfmpegGui.Services.FfmpegRunner.RunAsync(
                            $"-y -hide_banner -loglevel error -f lavfi -i \"testsrc2=size={sw}x{sh}:duration=1:rate=1\" "
                          + $"-pix_fmt rgb48le -frames:v 1 -color_primaries bt2020 -color_trc smpte2084 \"{p}\"",
                            null, ff).ConfigureAwait(false);
                        if (ec != 0 || !File.Exists(p))
                            throw new InvalidOperationException($"HDR 源生成失败 (exit {ec})");
                        if (!created.Contains(p)) created.Add(p);
                        return p;
                    }

                    // ① legacy `-vf` + **有**色彩变换（源无标签⇒按惯例 sRGB；目标 Display P3 ⇒ 真映射）
                    string r1out = Path.Combine(dir, "r1_l1.png");
                    created.Add(r1out);
                    var o1 = new FfmpegGui.Models.FfmpegOptions
                    {
                        Format = "png", ColorSpace = "Display P3", ColorStrategy = ST.Recommended,
                        BitDepth = 16, EnableMaxDimension = true, MaxDimension = 128,
                    };
                    string c1 = FfmpegCommandBuilder.BuildArguments(o1, r1src, r1out);
                    string? ch1 = TakeOpt(c1, "-vf");
                    Console.WriteLine($"r1-legacy-vf {ch1}");
                    if (ch1 == null) Check(false, "R1①：legacy 带色彩变换时应产出 -vf");
                    else
                    {
                        var t = Tok(ch1); int si = IdxOfScale(t), mi = IdxOfColor(t);
                        Check(si >= 0, $"R1①存在性：判别式命中且**唯一**（si={si}，-2 表示命中多处）");
                        Check(mi >= 0, $"R1①形态自检：该格确有色彩变换（mi={mi}）—— 形态不符即红（§6 第 77 条）");
                        Check(si >= 0 && mi >= 0 && si == mi - 1,
                            $"R1①位置：scaleIdx == mapIdx-1（si={si}, mi={mi}）⇒ 缩放→映射");
                        // ⚠ **先判下标有效再取下标**：变异下 `si` 会是 -1（未注入）或 -2（命中多处），
                        //   写成 `si == 0 || t[si - 1]...` 会**抛 IndexOutOfRangeException** ⇒
                        //   本组后续断言**整段不执行**（实测 pass 52→36），且异常行**不带 `FAIL` 前缀**
                        //   ⇒ 只按 `FAIL` 过滤的复读会漏掉它。故一律 `si >= 0 && …`。
                        Check(si >= 0 && (si == 0 || t[si - 1].StartsWith("format=") || IsEvenGuard(t[si - 1])),
                            $"R1①前驱形状：前驱是 format= 或决策层的偶数守卫（实 {(si > 0 ? t[si - 1] : "<链首/未命中>")}）");
                        Check(si >= 0 && (si == 0 || IdxOfColor(t[..si]) < 0),
                            $"R1①不越过色彩变换：`si` 之前**没有任何**色彩变换（实 {(si > 0 ? IdxOfColor(t[..si]) : -1)}）");
                    }

                    // ①′ **S2 形态**（HDR→SDR，链里有**两个**色彩变换：`tonemap=` 与 `zscale=`）
                    //    ⇒ R1 的锚点是**第一条**色彩变换（`tonemap=`），不是 `zscale=`。
                    //    本格是 M-h 的落点：插到 `tonemap` 之后 ⇒ 位置/不越过断言必须红。
                    string hdrSrc = await MakeHdrSource(512, 384).ConfigureAwait(false);
                    var o1b = new FfmpegGui.Models.FfmpegOptions
                    {
                        Format = "png", ColorSpace = "sRGB", ColorStrategy = ST.Recommended,
                        BitDepth = 8, EnableMaxDimension = true, MaxDimension = 128,
                    };
                    string c1b = FfmpegCommandBuilder.BuildArguments(o1b, hdrSrc, Path.Combine(dir, "r1_l1b.png"));
                    string? ch1b = TakeOpt(c1b, "-vf");
                    Console.WriteLine($"r1-legacy-vf-s2 {ch1b}");
                    if (ch1b == null || !Tok(ch1b).Any(x => x.StartsWith("tonemap=")))
                    {
                        // ⚠ **可数的 SKIP**（不是通过、也不是失败）：S2 形态需要**带 CICP 的 HDR 素材**，
                        //   而 `lavfi testsrc2 → PNG` **不携带** `-color_primaries/-color_trc`
                        //   （实测 ffprobe 报 `color_primaries=unknown` / `color_transfer=unknown`）
                        //   ⇒ HDR 探测不到 ⇒ 决策层不挂 tonemap 链 ⇒ **M-h 在本探针里无落点**。
                        //   影响评估：M-h 与 M-g 命中**同一条**断言（「不越过色彩变换」），差别只在
                        //   「链里有几个色彩变换」；M-g 已在单变换链上把它证红 ⇒ 本 SKIP **不降低**该断言的
                        //   已证强度，只缺「双变换链」这一个形态。补一份带 CICP 的素材后本格自动生效。
                        Console.WriteLine("r1-s2-shape SKIP：S2（tonemap）形态不可达（lavfi→PNG 不带 CICP）⇒ "
                            + "M-h 无落点，按「与 M-g 同断言、缺双变换形态」登记（SKIP=1）");
                    }
                    else
                    {
                        var t = Tok(ch1b); int si = IdxOfScale(t), mi = IdxOfColor(t);
                        Check(t.Any(x => x.StartsWith("tonemap=")),
                            $"R1①′形态自检：该格必须是 **S2**（含 `tonemap=`）—— 形态不符即红（§6 第 77 条）");
                        Check(si >= 0 && mi >= 0 && si == mi - 1,
                            $"R1①′位置：scaleIdx == **第一条**色彩变换 - 1（si={si}, mi={mi}）");
                        Check(si >= 0 && (si == 0 || IdxOfColor(t[..si]) < 0),
                            $"R1①′不越过色彩变换：`si` 之前无任何色彩变换（实 {(si > 0 ? IdxOfColor(t[..si]) : -1)}）");
                    }

                    // ② legacy `-vf` + **无**色彩变换（sRGB→sRGB 恒等）⇒ 链首即锚点
                    var o2 = new FfmpegGui.Models.FfmpegOptions
                    {
                        Format = "png", ColorStrategy = ST.Recommended,
                        BitDepth = 8, EnableMaxDimension = true, MaxDimension = 128,
                    };
                    string c2 = FfmpegCommandBuilder.BuildArguments(o2, r1src, Path.Combine(dir, "r1_l2.png"));
                    string? ch2 = TakeOpt(c2, "-vf");
                    Console.WriteLine($"r1-legacy-vf-nomap {ch2}");
                    if (ch2 == null) Check(false, "R1②：legacy 无色彩变换时仍应注入 scale（新建 -vf）");
                    else
                    {
                        var t = Tok(ch2); int si = IdxOfScale(t), mi = IdxOfColor(t);
                        Check(mi == -1, $"R1②形态自检：该格**无**色彩变换（mi={mi}）");
                        Check(si == 0, $"R1②位置：无色彩变换时 scaleIdx == 0（si={si}）");
                    }

                    // ③ legacy `-filter_complex`（GIF 调色板优化）⇒ 载体是 **complex 首段**（先剥字面引号）
                    var o3 = new FfmpegGui.Models.FfmpegOptions
                    {
                        Format = "gif", ColorStrategy = ST.Recommended, GifPaletteOptimize = true,
                        EnableMaxDimension = true, MaxDimension = 128,
                    };
                    string c3 = FfmpegCommandBuilder.BuildArguments(o3, r1src, Path.Combine(dir, "r1_l3.gif"));
                    string? ch3 = TakeOpt(c3, "-filter_complex");
                    Console.WriteLine($"r1-legacy-complex {ch3}");
                    if (ch3 == null) Check(false, "R1③：GIF 调色板优化应产出 -filter_complex");
                    else
                    {
                        var t = Tok(ch3.Split(';')[0]); int si = IdxOfScale(t), mi = IdxOfColor(t);
                        Check(si >= 0, $"R1③存在性：complex **首段**里判别式命中且唯一（si={si}）");
                        // ⚠ 该格（SDR 源 ⇒ 链里无色彩变换）**必须**要求 `si == 0`：
                        //   写成 `mi < 0 ||` 会让判据**恒真**（mi 恒为 -1）⇒ 正是 §6 第 74 条的坑。
                        Check(mi < 0 ? si == 0 : si == mi - 1,
                            $"R1③位置：首段无色彩变换时 scaleIdx == 0（si={si}, mi={mi}）");
                        Check(si >= 0 && (si == 0 || t[si - 1].StartsWith("format=") || IsEvenGuard(t[si - 1])),
                            $"R1③前驱形状：前驱是 format= 或偶数守卫（实 {(si > 0 ? t[si - 1] : "<段首/未命中>")}）");
                        Check(si >= 0 && (si == 0 || IdxOfColor(t[..si]) < 0),
                            $"R1③不越过色彩变换：`si` 之前**没有任何**色彩变换（实 {(si > 0 ? IdxOfColor(t[..si]) : -1)}）");
                    }

                    // ④ **反向断言 + M-e 负控**：引擎 gif 出口的 complex 串里那个 `-2` scale 属于
                    //    `[1:v]` **alpha 对齐分支**，**不属于**色彩链（色彩映射在本进程完成，该串无色彩变换）。
                    //    ⇒ 门禁**不得**给该格套 `scaleIdx == mapIdx - 1`；若把该 `-2` 当色彩链载体，
                    //      位置断言会**恒真**（§6 第 74 条的第三个陷阱）。⚠ 本条**预期绿**，不是变异。
                    var o4 = new FfmpegGui.Models.FfmpegOptions
                    {
                        Format = "gif", ColorStrategy = ST.Recommended, GifPaletteOptimize = true,
                        EnableMaxDimension = true, MaxDimension = 128,
                    };
                    var it4 = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(o4, r1src, out _);
                    if (it4 == null) Check(false, "R1④：引擎 gif 意图构建失败（无法做反向断言）");
                    else
                    {
                        var plan4 = CE.Plan(it4);
                        var sb4 = new StringBuilder();
                        string engGif = Path.Combine(dir, "r1_e1.gif");
                        created.Add(engGif);
                        await FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                            r1src, engGif, plan4.ToSpec(), plan4, s => { lock (sb4) sb4.Append(s); },
                            default, 1, ff, o4).ConfigureAwait(false);
                        string? ec4 = null;
                        foreach (var ln in sb4.ToString().Split('\n'))
                            if (ln.Contains("-filter_complex"))
                            { ec4 = TakeOpt(ln, "-filter_complex"); if (ec4 != null) break; }
                        Console.WriteLine($"r1-engine-complex {ec4}");
                        if (ec4 == null) Check(false, "R1④：引擎 gif 命令行里应有 -filter_complex（反向断言的载体）");
                        else
                        {
                            var segs = ec4.Split(';');
                            Check(segs[0].StartsWith("[1:v]"),
                                $"R1④反向：complex **首段以 [1:v] 开头**（实 {segs[0][..Math.Min(12, segs[0].Length)]}）"
                              + " ⇒ 该段的 scale 属 alpha 分支，载体**不是**色彩链");
                            var alphaTok = Tok(segs[0]);
                            Check(IdxOfColor(alphaTok) < 0,
                                $"R1④反向：alpha 段内**无**色彩变换（mi={IdxOfColor(alphaTok)}）"
                              + " ⇒ 位置断言若套在该格上会恒真");
                            bool colorSegHasScale = false;
                            foreach (var sg in segs)
                                if (sg.StartsWith("[0:v]") && IdxOfScale(Tok(sg)) >= 0) colorSegHasScale = true;
                            Check(!colorSegHasScale,
                                "R1④反向：色彩分支段 `[0:v]…` **不含** scale（缩放属解码 -vf）");
                            Check(ec4.Contains("scale=128:-2:flags=lanczos")
                                  && alphaTok[0] != "scale=128:-2:flags=lanczos",
                                "R1④反向：该 `-2` **子串确实存在**、但 token 层面挂在 `[1:v]` 段上（不是独立 token）"
                              + " ⇒ 这正是「子串法载体」会踩的坑（§6 第 74 条）");
                        }
                    }

                    // ⑤ **成对**尺寸断言：只查「命令行里有 scale」是**必要不充分**（§6 第 70 条）——
                    //    必须再用**外部可观测量**（产物尺寸）背书。
                    int ec1 = await FfmpegGui.Services.FfmpegRunner.RunAsync(c1, null, ff).ConfigureAwait(false);
                    var (rw, rh) = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                        .ProbeSizeAsync(r1out).ConfigureAwait(false);
                    Check(ec1 == 0 && rw == 128 && rh == 96,
                        $"R1⑤成对尺寸：命令行含 scale **且**产物确为 128x96（实 exit={ec1} {rw}x{rh}）");

                    // ⑥ 负控：不超限 ⇒ **不得**注入（证明判别式与存在性断言不是恒真）
                    var o6 = new FfmpegGui.Models.FfmpegOptions
                    {
                        Format = "png", ColorStrategy = ST.Recommended, BitDepth = 8,
                        EnableMaxDimension = true, MaxDimension = 1024,
                    };
                    string c6 = FfmpegCommandBuilder.BuildArguments(o6, r1src, Path.Combine(dir, "r1_neg.png"));
                    Check(FfmpegCommandBuilder.FindInjectedScaleFilter(c6) == null,
                        "R1⑥负控：512x384 限 1024（不超限）⇒ 命令行**不含**最长边缩放");
                }
            }
            finally
            {
                // 结尾自清（按计数）：文件逐个删，目录空才删（宿主批量删除守卫会拦整目录删）
                // ⚠ 分母只算**清理时真实存在**的文件：中途异常时某些产物可能根本没被创建，
                //   若拿 created.Count 当分母会报出「11/12」这种**假残留**（实测踩到）。
                int total = 0;
                foreach (var p in created) { try { if (File.Exists(p)) total++; } catch { } }
                int removed = 0;
                foreach (var p in created) { try { if (File.Exists(p)) { File.Delete(p); removed++; } } catch { } }
                bool dirGone = false;
                try
                {
                    if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                    { Directory.Delete(dir); dirGone = true; }
                }
                catch { }
                Console.WriteLine($"geometry-cleanup: 删除文件 {removed}/{total}，目录{(dirGone ? "已" : "未")}删除");
            }
        }

        // ═══════════════════ firstframe：引擎「多帧输入只取首帧」的直调载体 ═══════════════════

        /// <summary>
        /// firstframe &lt;input&gt; [输出根目录] —— **直调引擎**，验证 <c>RawColorPipeline</c> 的解码步
        /// 对多帧输入**只取首帧**（`-frames:v 1`），而不是把 N 帧顺序写进同一条 rawvideo
        /// （后者会让 `h = px/w` 反推出 `h = N×单帧高`，而完整性检查恰好成立 ⇒ **静默产出竖叠错图**）。
        ///
        /// <para>
        /// <b>为什么需要这个 mode</b>（2026-09-18，`#136` 切片 C）：<c>verify-engine-firstframe.ps1</c>
        /// 原先拿**完整 CLI**（`--color-engine engine`）喂动图 webp，靠"引擎被路由到"才走得到那条解码命令。
        /// 而 `#136` 拆轴后「动画输入 + 显式 engine」会变成 <c>exit 90</c> 硬失败
        /// （<c>ColorEngineRouter.BlockReason</c> 的 <c>AnimatedInput</c> 分支 + <c>ExplicitlyRequested</c>）
        /// ⇒ CLI 载体**整体失效**。本 mode 绕过路由层直调 <c>TransformFileAsync</c>，
        /// 让这条守卫在**行为改变之前**就先站住（先换载体、再改行为）。
        /// </para>
        ///
        /// <para>
        /// <b>为什么必须传 <c>options</c></b>：<c>RawColorPipeline.cs:94-95</c> 是
        /// <c>scaleFilter = options != null ? BuildMaxDimensionScaleFilter(options, inputPath) : null</c>
        /// ⇒ 不传 options 就**跑不到**几何缩放段，也就复现不了「N 帧竖叠」这个缺陷形态
        /// （同文件的 <c>bitdepth</c> 就是 <c>options=null</c>，故它当不了本项载体）。
        /// </para>
        ///
        /// <para>
        /// <b>载体分派（§6 第 74 条：载体选错 ⇒ 断言恒真，比恒红更危险）</b>：引擎日志里有**两条**
        /// <c>-f rawvideo</c> —— 解码步（<c>RawColorPipeline.cs:126-128</c>，<c>-i</c> 在 <c>-f rawvideo</c> **之前**）
        /// 与编码步（<c>BuildEncodeArgs:793</c> / <c>WriteRgb48ToPngAsync:1054</c>，<c>-f rawvideo</c> 在前且带
        /// <c>-video_size</c>）。本 mode 按「<c>-f rawvideo</c> 的下标 <b>&gt;</b> <c>-i</c> 的下标」显式分派，
        /// 并把**编码特征**（<c>-video_size</c> / <c>image2</c>）作为**拒绝条件**；被拒的候选会打
        /// <c>firstframe-reject</c> 供人核对。
        /// </para>
        ///
        /// <para>
        /// 打印的结构化行：<c>firstframe-out=</c> / <c>firstframe-filter</c> / <c>firstframe-cmd</c>（解码步）/
        /// <c>firstframe-enc</c>（编码步，供门禁复读 <c>-video_size</c> 做"引擎自报尺寸 == 产物尺寸"）/
        /// <c>firstframe-ref</c>（**单帧参照**：同一滤镜只取首帧缩放，给出"不竖叠时另一条边应是多少"的
        /// **独立观测量** —— 不靠门禁自己算，因为 <c>-2</c> 的取偶会与手算漂开）/
        /// <c>firstframe-case</c> / <c>firstframe-cleanup:</c>。输出走 GUID 运行级隔离，结尾按计数自清。
        /// </para>
        /// </summary>
        private static async System.Threading.Tasks.Task ProbeFirstframe(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: ServiceProbe firstframe <input> [outDir]");
                _fail++; return;
            }
            string input = args[1];
            if (!File.Exists(input)) { Check(false, $"firstframe：输入不存在 {input}"); return; }
            string ff = AppSettingsService.Current.FfmpegPath;
            if (string.IsNullOrWhiteSpace(ff) || !File.Exists(ff))
            { Check(false, "firstframe：未找到 ffmpeg（AppSettings 未初始化 / publish/PLAN 缺失）"); return; }

            const string ScaleMarker = "几何缩放（最长边限制）";     // RawColorPipeline.cs:145 的原样前缀
            string root = args.Length >= 3 ? args[2] : "tests/output/validate";
            string dir = Path.Combine(root, "firstframe_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            Console.WriteLine($"firstframe-out={dir}");
            var created = new List<string>();

            try
            {
                var (w0, h0) = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                    .ProbeSizeAsync(input, ff).ConfigureAwait(false);
                Check(w0 > 0 && h0 > 0, $"firstframe：能探测输入尺寸（{w0}x{h0}）");
                if (w0 <= 0 || h0 <= 0) return;

                // 最长边限制 = **较长边的一半**（保证超限 ⇒ 缩放段真的跑；至少 2，`-2` 取偶的下限）。
                // ⚠ 期望的另一条边**不在这里算**（`-2` 的取偶会与手算漂开，见 geometry 的 42x32 坑）——
                //   改由下面那条**单帧参照**给出独立观测量。
                int maxDim = Math.Max(2, Math.Max(w0, h0) / 2);
                var o = new FfmpegGui.Models.FfmpegOptions
                {
                    Format = "png", ColorStrategy = ST.Recommended, BitDepth = 8,
                    EnableMaxDimension = true, MaxDimension = maxDim,
                };

                // ⚠ 风险②：必须断言 filter 非空 —— 否则输入没超限 ⇒ 缩放段不跑 ⇒ 尺寸断言**真空绿**。
                string? filter = FfmpegCommandBuilder.BuildMaxDimensionScaleFilter(o, input);
                Console.WriteLine($"firstframe-filter \"{filter ?? "(null)"}\"");
                Check(!string.IsNullOrEmpty(filter),
                    $"firstframe：输入确实超限 ⇒ 缩放滤镜非空（filter={filter ?? "(null)"}；为空则本 mode 无判别力）");
                if (string.IsNullOrEmpty(filter)) return;

                var it = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(o, input, out var why);
                if (it == null) { Check(false, $"firstframe：选项→意图失败：{why}"); return; }
                var plan = CE.Plan(it);
                if (!plan.IsOk) { Check(false, $"firstframe：计划被拒：{plan.Reason}"); return; }

                // ── 单帧参照：**同一滤镜**、只取首帧 ⇒ "不竖叠时另一条边应是多少"的独立观测量 ──
                // ⚠ 这条命令**自己带 `-frames:v 1`**：不带就会踩同一个 image2 多帧缺陷
                //   （`QueueProcessor` 的预缩放块正是缺它才失败，与本 mode 要锁的东西互为镜像）。
                string refPng = Path.Combine(dir, "firstframe_ref.png");
                created.Add(refPng);
                string refArgs = $"-y -hide_banner -loglevel error -i \"{input}\" -frames:v 1 -vf \"{filter}\" "
                               + $"-compression_level 0 \"{refPng}\"";
                Console.WriteLine($"firstframe-ref {refArgs}");
                int refEc = await FfmpegGui.Services.FfmpegRunner.RunAsync(refArgs, null, ff).ConfigureAwait(false);
                int rw = 0, rh = 0;
                if (refEc == 0 && File.Exists(refPng))
                {
                    var rr = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                        .ProbeSizeAsync(refPng, ff).ConfigureAwait(false);
                    rw = rr.w; rh = rr.h;
                }
                Check(refEc == 0 && rw > 0 && rh > 0,
                    $"firstframe：单帧参照产出可用（exit {refEc}，{rw}x{rh}）—— 期望尺寸的**独立**来源");

                // ── 被测：引擎主解码（多帧输入 + 超限 ⇒ 走 `RawColorPipeline.cs:126-128`）──
                string outPng = Path.Combine(dir, "firstframe_out.png");
                created.Add(outPng);
                var sb = new StringBuilder();
                var gate = new object();
                FfmpegGui.Services.ColorMapping.RawColorPipeline.Stats st;
                try
                {
                    st = await FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                        input, outPng, plan.ToSpec(), plan,
                        s => { lock (gate) sb.Append(s); }, default, 1, ff, o).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Check(false, $"firstframe：引擎异常：{ex.GetType().Name}: {ex.Message}");
                    return;
                }
                string log = sb.ToString();

                // ── 载体分派：解码步 vs 编码步（见方法头）──
                string? dec = null, enc = null;
                var rejects = new List<string>();
                foreach (var raw in log.Split('\n'))
                {
                    if (!raw.Contains("[色彩] ffmpeg ")) continue;
                    if (!raw.Contains("-f rawvideo")) continue;
                    string body = raw.Substring(raw.IndexOf("[色彩] ffmpeg ", StringComparison.Ordinal)
                                              + "[色彩] ffmpeg ".Length);
                    int iRaw = body.IndexOf("-f rawvideo", StringComparison.Ordinal);
                    int iIn = body.IndexOf("-i ", StringComparison.Ordinal);
                    bool encShape = iIn < 0 || iRaw < iIn;
                    if (encShape)
                    {
                        if (enc == null) enc = body;
                        continue;
                    }
                    // ⚠ 自检：解码步候选里**不得**出现编码特征（选错载体 ⇒ 断言恒真）
                    if (body.Contains("-video_size") || body.Contains("image2"))
                    { rejects.Add("含编码特征(-video_size/image2)"); continue; }
                    if (dec == null) dec = body;
                }
                Console.WriteLine($"firstframe-cmd {dec ?? "(未取到)"}");
                Console.WriteLine($"firstframe-enc {enc ?? "(未取到)"}");
                foreach (var rj in rejects) Console.WriteLine($"firstframe-reject {rj}");
                Check(dec != null, "firstframe：从引擎日志里取到**解码步**命令行（载体已显式分派，拒绝项见 firstframe-reject）");
                Check(enc != null, "firstframe：同时取到**编码步**命令行（供门禁复读 -video_size 做自洽断言）");

                int iV1 = -1, iInDec = -1;
                if (dec != null)
                {
                    iV1 = dec.IndexOf("-frames:v 1", StringComparison.Ordinal);
                    iInDec = dec.IndexOf("-i ", StringComparison.Ordinal);
                }
                bool v1AfterI = iV1 >= 0 && iInDec >= 0 && iV1 > iInDec;
                Check(v1AfterI,
                    $"firstframe：解码步的 `-frames:v 1` 落在 `-i` **之后**（下标 {iV1} > {iInDec}）"
                  + " —— 它是**输出侧**选项，放 `-i` 之前会被当输入选项而无效");

                var (ew, eh) = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                    .ProbeSizeAsync(outPng, ff).ConfigureAwait(false);
                bool lockedOk = (w0 >= h0 ? ew : eh) == maxDim;
                Console.WriteLine($"firstframe-case input={Path.GetFileName(input)} src={w0}x{h0} maxdim={maxDim} "
                    + $"engine={ew}x{eh} stats={st.Width}x{st.Height} ref={rw}x{rh} "
                    + $"log={(log.Contains(ScaleMarker) ? "scaled" : "none")} "
                    + $"framesv1={(iV1 >= 0 ? "yes" : "no")} v1afteri={(v1AfterI ? "yes" : "no")}");

                Check(lockedOk, $"firstframe：产物**锁定的那条边** == MaxDimension（{ew}x{eh}，maxdim={maxDim}）");
                // ⭐ 本 mode 的**主判据**：产物 == 单帧参照 ⇒ 解码步只取了一帧。
                //   若缺 `-frames:v 1`，N 帧竖叠 ⇒ 另一条边变成 N 倍 ⇒ 与参照**不等**。
                Check(ew == rw && eh == rh,
                    $"firstframe：产物 == 单帧参照（{ew}x{eh} vs {rw}x{rh}）⇒ 解码步**只取首帧**"
                  + "（缺 `-frames:v 1` 时另一条边会变成 N 倍，如 128x96 → 128x960）");
                Check(st.Width == ew && st.Height == eh,
                    $"firstframe：Stats 反推尺寸 == 实际产物（{st.Width}x{st.Height} vs {ew}x{eh}）");
                Check(log.Contains(ScaleMarker),
                    $"firstframe：日志出现「{ScaleMarker}」⇒ 引擎几何缩放段确实执行（否则尺寸断言无判别力）");
            }
            finally
            {
                // 结尾自清（按计数）：文件逐个删，目录空才删（宿主批量删除守卫会拦整目录删）。
                // ⚠ 分母只算**清理时真实存在**的文件（照 geometry 的实测修正：中途异常时某些产物
                //   根本没被创建，拿 created.Count 当分母会报出「N/M」这种**假残留**）。
                int total = 0;
                foreach (var p in created) { try { if (File.Exists(p)) total++; } catch { } }
                int removed = 0;
                foreach (var p in created) { try { if (File.Exists(p)) { File.Delete(p); removed++; } } catch { } }
                bool dirGone = false;
                try
                {
                    if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                    { Directory.Delete(dir); dirGone = true; }
                }
                catch { }
                Console.WriteLine($"firstframe-cleanup: 删除文件 {removed}/{total}，目录{(dirGone ? "已" : "未")}删除");
            }
        }

        /// <summary>
        /// P2-1 / P2-3 的直接证据。
        /// ① 8 线程 × 400 次对同一 <see cref="FfmpegGui.Models.QueueItem.Log"/> 做 <c>+=</c>：
        ///    要求一条不丢、一条不重（旧实现是「读快照→拼接→写回」，并发下必然互相覆盖）。
        /// ② 让子进程往 stderr 灌约 170KB：<c>只读 stdout</c> 的写法会死锁（管道缓冲约 4KB 写满后子进程阻塞在写上），
        ///    两条流并发排空则正常返回 —— 用来证明 P2-3 是可复现故障，不是理论风险。
        /// </summary>
        private static void ProbeProcStreams()
        {
            var it = new FfmpegGui.Models.QueueItem();
            const int T = 8, N = 400;
            System.Threading.Tasks.Parallel.For(0, T, t =>
            {
                for (int i = 0; i < N; i++) it.Log += $"t{t}-{i};";
            });
            int seen = 0;
            for (int t = 0; t < T; t++) for (int i = 0; i < N; i++) if (it.Log.Contains($"t{t}-{i};")) seen++;
            int separators = it.Log.Split(';').Length - 1;
            // P2-1 的回归锁：属性内修好后，并发追加必须一条不丢、一条不重
            Check(seen == T * N && separators == T * N,
                $"并发追加日志一条不丢不重（期望 {T * N}，实际在位 {seen}，写入计数 {separators}）");

            const string Flood = "/c for /L %i in (1,1,8000) do @echo stderr-flood-line-%i 1>&2";
            System.Diagnostics.Process Start() => System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = @"C:\Windows\System32\cmd.exe",
                    Arguments = Flood,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                })!;

            var p1 = Start();
            bool naiveDone = System.Threading.Tasks.Task.Run(() => p1.StandardOutput.ReadToEnd()).Wait(TimeSpan.FromSeconds(10));
            Check(!naiveDone, $"只读 stdout 在 stderr 洪泛下卡死（10 秒内未返回＝预期；实际返回={naiveDone}）");
            try { p1.Kill(entireProcessTree: true); } catch { }
            try { p1.WaitForExit(3000); } catch { }

            var p2 = Start();
            var bothTask = System.Threading.Tasks.Task.Run(async () =>
            {
                var o = p2.StandardOutput.ReadToEndAsync();
                var e = p2.StandardError.ReadToEndAsync();
                await System.Threading.Tasks.Task.WhenAll(o, e);
                await p2.WaitForExitAsync();
                return e.Result.Length;
            });
            bool drained = bothTask.Wait(TimeSpan.FromSeconds(120));
            Check(drained && bothTask.IsCompletedSuccessfully && bothTask.Result > 100_000,
                $"两条流并发排空即可正常读完（stderr 实得 {(bothTask.IsCompletedSuccessfully ? bothTask.Result : 0)} 字符）");
        }

        /// <summary>
        /// P2-5 行为锁：子进程输出必须按 UTF-8 解码，且该结论不依赖宿主环境。
        /// ① 原始流逐字节比对 ⇒ 证明子进程（同 ffmpeg/cjxl/exiftool 一族）产出确为 UTF-8 字节，
        ///    即「声明 UTF-8」是正确的解码选择，而不是随手挑的；
        /// ② 声明 <c>StandardOutputEncoding/StandardErrorEncoding = UTF8</c> ⇒ 两条流逐字还原；
        /// ③ 负控：同一批字节按非 UTF-8 单字节代码页解必然不还原 ⇒ ② 的断言能识别解码错误，不是空跑；
        /// ④ 现场复现：宿主输出编码为非 UTF-8（zh-CN 控制台的 936 等）且**不声明**解码编码 ⇒ 乱码。
        ///    这正是 P2-5 的现场；修好后每处重定向都显式声明，行为与环境解耦。
        /// </summary>
        private static void ProbeProcEncoding()
        {
            const string Text = "中文路径·测试_图片-αβγ.png";
            const string ExpectedOut = "OUT:" + Text;
            const string ExpectedErr = "ERR:" + Text;

            // 宿主可能是 ServiceProbe.exe 自身，也可能经 dotnet 启动（此时 ProcessPath=dotnet.exe，需补 dll）
            var host = Environment.ProcessPath!;
            var prefix = System.IO.Path.GetFileNameWithoutExtension(host)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? "\"" + System.Reflection.Assembly.GetEntryAssembly()!.Location + "\" "
                : "";
            var arguments = prefix + "emit-utf8 \"" + Text + "\"";

            System.Diagnostics.ProcessStartInfo Psi(Encoding? enc) => new()
            {
                FileName = host,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = enc,
                StandardErrorEncoding = enc
            };

            // ① 原始字节：子进程产出的是 UTF-8（读 BaseStream，绕开任何解码）
            using (var p = System.Diagnostics.Process.Start(Psi(null))!)
            {
                using var ms = new System.IO.MemoryStream();
                p.StandardOutput.BaseStream.CopyTo(ms);
                p.WaitForExit();
                var raw = ms.ToArray();
                var want = Encoding.UTF8.GetBytes(ExpectedOut);
                var same = raw.Length == want.Length && raw.AsSpan().SequenceEqual(want);
                Check(same, $"子进程产出确为 UTF-8 字节（原始流 {raw.Length} 字节 vs 期望 {want.Length} 字节，逐字节{(same ? "一致" : "不一致")}）");
            }

            // ② 声明 UTF-8 解码 ⇒ 两条流逐字还原
            using (var p = System.Diagnostics.Process.Start(Psi(Encoding.UTF8))!)
            {
                var o = p.StandardOutput.ReadToEnd();
                var e = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Check(o == ExpectedOut, $"声明 UTF-8 后 stdout 逐字还原（实得「{o}」）");
                Check(e == ExpectedErr, $"声明 UTF-8 后 stderr 逐字还原（实得「{e}」）");
            }

            // ③ 负控：证明上述断言能识别「解码器选错」
            var wrong = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(ExpectedOut));
            Check(wrong != ExpectedOut,
                "负控：同批字节按非 UTF-8 单字节代码页解码必不还原 ⇒ 上述断言能识别解码错误（非空跑）");

            // ④ 现场复现：宿主输出编码非 UTF-8 + 不声明解码编码（.NET 默认就用宿主 Console.OutputEncoding）
            var saved = Console.OutputEncoding;
            string observed;
            bool simulated;
            try
            {
                Console.OutputEncoding = Encoding.Latin1;   // 代表 zh-CN 控制台的 936 一类非 UTF-8 代码页
                simulated = true;
            }
            catch (Exception ex)
            {
                simulated = false;
                observed = "";
                Console.WriteLine($"INFO 无法把宿主输出编码切到非 UTF-8（{ex.GetType().Name}）⇒ ④ 现场复现 SKIP");
            }
            if (simulated)
            {
                try
                {
                    using var p = System.Diagnostics.Process.Start(Psi(null))!;   // 不声明解码编码 = 旧写法
                    observed = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                }
                finally { Console.OutputEncoding = saved; }
                Check(observed != ExpectedOut,
                    $"现场复现：宿主输出编码非 UTF-8 且未声明解码编码 ⇒ 乱码（实得「{observed}」）");
            }
            // 用 CodePage 比较：Console.OutputEncoding 返回的 UTF8Encoding 实例带控制台默认回退字符，
            // 与 Encoding.UTF8 静态实例的 EncoderFallback 不同 ⇒ Encoding.Equals 会假红。
            Check(Console.OutputEncoding.CodePage == Encoding.UTF8.CodePage,
                $"探针自身仍以 UTF-8 输出（否则门禁日志自身会乱码；实为 {Console.OutputEncoding.WebName}/{Console.OutputEncoding.CodePage}）");
        }

        /// <summary>
        /// S-P1-3/4/5/6 的行为锁：用**生产代码**的 IpcService 跑一轮真实命名管道往返。
        /// ① 服务器能连续服务多个客户端 —— 旧写法在循环外先 await 一次 WaitForConnectionAsync
        ///    把第 1 个连接建立起来，循环里又调**阻塞的** WaitForConnection()（此时管道已连接）
        ///    ⇒ 抛 InvalidOperationException，异常从 fire-and-forget 任务里逃出
        ///    ⇒ **第一个客户端到来时服务器就死了**，之后全部静默失联；
        /// ② 客户端发坏数据/提前断开只影响它自己，后续客户端仍可正常 Ping；
        /// ③ SendRequestAsync 的超时**真的生效**（旧写法 ReadLineAsync 没传 token ⇒ 对端不回包就无限挂起）；
        /// ④ 能力缓存字段是 ConcurrentDictionary 且并发读 + 初始化写不抛异常（S-P1-5）。
        /// ⚠ 用真实管道名（按用户 SID 派生）⇒ **不能**与正在运行的 GUI 实例同时跑。
        /// </summary>
        /// <summary>
        /// 跨进程往返的**服务端**端：起 IPC 服务器并驻留指定秒数。
        /// 为什么需要它：上一轮把服务端与客户端放在**同一个进程**里测往返时，Ping 拿不到响应（根因未定位）；
        /// 而真实的 IPC 用法本来就是跨进程（第二个实例 / CLI 连到正在跑的 GUI）⇒ 用两个进程重测，
        /// 把「同进程」这个干扰因素排除掉。
        /// ⚠ 管道名按用户 SID 派生 ⇒ 测试期间**不能**有正在运行的 GUI 实例。
        /// </summary>
        private static void ProbeIpcServer(int seconds)
        {
            var cts = new System.Threading.CancellationTokenSource();
            var task = FfmpegGui.Services.IpcService.StartServerAsync(null, null, cts.Token);
            Console.WriteLine($"[ipc-server] 已启动，驻留 {seconds}s");
            System.Threading.Thread.Sleep(TimeSpan.FromSeconds(seconds));
            try { cts.Cancel(); } catch { }
            try { task.Wait(TimeSpan.FromSeconds(3)); } catch { }
            Console.WriteLine("[ipc-server] 退出");
        }

        /// <summary>
        /// 跨进程往返的**客户端**端：连到另一个进程里的 `ipc-server`，发 Ping 并断言拿到 pong。
        /// </summary>
        private static void ProbeIpcClient()
        {
            var c = FfmpegGui.Services.IpcService.TryConnectAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
            if (c == null)
            {
                Check(false, "跨进程：8 秒内未连上 IPC 服务器（对端起了吗？管道名按用户 SID 派生）");
                return;
            }
            using (c)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = FfmpegGui.Services.IpcService.SendRequestAsync(c,
                    new FfmpegGui.Services.IpcService.IpcRequest
                    { Id = 1, Type = FfmpegGui.Services.IpcService.RequestType.Ping },
                    TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                sw.Stop();
                Check(r?.Success == true && r.Message == "pong",
                    $"跨进程 Ping 往返拿到 pong（实耗 {sw.Elapsed.TotalSeconds:F2}s，实得「{r?.Message}」）");
            }
        }

        /// <summary>
        /// 跨进程往返的**裸字节客户端**：不经过 `StreamReader`/`StreamWriter`，直接往已连接的管道
        /// 写原始请求字节、再直接读原始响应字节。
        /// 目的：把上一轮那个未定位的问题**一分为二**——
        ///   · 若裸字节能收到响应 ⇒ 服务端处理是好的，问题在客户端的 Stream 层用法；
        ///   · 若裸字节也收不到 ⇒ 服务端确实没读到请求，问题在管道/服务端侧。
        /// ⚠ 需要先起 `ipc-server`（管道名按用户 SID 派生，测试期间不能有正在运行的 GUI 实例）。
        /// </summary>
        private static void ProbeIpcRawClient()
        {
            var c = FfmpegGui.Services.IpcService.TryConnectAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
            if (c == null) { Check(false, "裸字节：8 秒内未连上 IPC 服务器"); return; }
            using (c)
            {
                var req = "{\"Id\":7,\"Type\":6,\"Payload\":null}\n";   // Type=6 → Ping
                var bytes = Encoding.UTF8.GetBytes(req);
                try
                {
                    c.Write(bytes, 0, bytes.Length);
                    c.Flush();
                    Console.WriteLine($"[raw] 已写 {bytes.Length} 字节，等待原始响应…");
                }
                catch (Exception ex)
                {
                    Check(false, $"裸字节写入失败: {ex.GetType().Name}: {ex.Message}");
                    return;
                }

                var buf = new byte[512];
                var readTask = c.ReadAsync(buf, 0, buf.Length);
                var done = System.Threading.Tasks.Task.WhenAny(readTask,
                    System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult();
                if (!ReferenceEquals(done, readTask))
                {
                    Check(false, "裸字节：5 秒内没读到任何响应字节 ⇒ 服务端侧没处理该请求（不是 StreamReader 的问题）");
                    return;
                }
                var n = readTask.GetAwaiter().GetResult();
                var text = Encoding.UTF8.GetString(buf, 0, n);
                Check(n > 0 && text.Contains("pong"),
                    $"裸字节：收到 {n} 字节响应「{text.Trim()}」（含 pong ⇒ 服务端处理正常，问题在客户端 Stream 层）");
            }
        }

        /// <summary>
        /// **环境对照**用的裸管道服务端（完全不涉及生产代码）：
        /// 只为回答一个问题——「这个环境里命名管道到底能不能正常收发」。
        /// 若这对自包含的裸管道都跑不通，则之前关于 `IpcService` 往返失败的一切结论都要重新标注为
        /// 「受环境限制，不可断言」，否则才能把责任归到生产代码上。
        /// </summary>
        private static void ProbePipeRawServer(int seconds)
        {
            const string name = "ffmpegpictureui-piperaw-control";
            using var srv = new System.IO.Pipes.NamedPipeServerStream(name,
                System.IO.Pipes.PipeDirection.InOut,
                4,   // 刻意与生产一致（生产是 4；控制组原为 1）——验证实例数是否是差异来源
                System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
            Console.WriteLine("[piperaw-server] 等待连接…");
            var accept = srv.WaitForConnectionAsync();
            if (!accept.Wait(TimeSpan.FromSeconds(10))) { Console.WriteLine("[piperaw-server] 10s 内无人连接"); return; }
            Console.WriteLine("[piperaw-server] 已连接，等待请求…");
            var buf = new byte[256];
            var readTask = srv.ReadAsync(buf, 0, buf.Length);
            if (!readTask.Wait(TimeSpan.FromSeconds(10)))
            {
                Console.WriteLine("[piperaw-server] 10s 内没读到请求字节 ⇒ 本环境管道读不通");
                return;
            }
            var n = readTask.Result;
            Console.WriteLine($"[piperaw-server] 读到 {n} 字节: {Encoding.UTF8.GetString(buf, 0, n).Trim()}");
            var resp = Encoding.UTF8.GetBytes("pong\n");
            srv.Write(resp, 0, resp.Length);
            srv.Flush();
            Console.WriteLine("[piperaw-server] 已回 pong");
            System.Threading.Thread.Sleep(Math.Max(1, seconds) * 1000);
        }

        /// <summary>**环境对照**用的裸管道客户端（见 <see cref="ProbePipeRawServer"/>）。</summary>
        private static void ProbePipeRawClient()
        {
            const string name = "ffmpegpictureui-piperaw-control";
            using var c = new System.IO.Pipes.NamedPipeClientStream(".",
                name, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            try
            {
                // 关键对照点：生产代码的 TryConnectAsync 用的是 **ConnectAsync(超时)**，
                // 而能跑通的控制组用的是**同步** Connect(超时)。这里刻意改用 ConnectAsync 复现差异。
                var ca = c.ConnectAsync(8000);
                if (!ca.Wait(TimeSpan.FromSeconds(12)))
                {
                    Check(false, "裸管道对照(ConnectAsync)：连接未在 12 秒内完成");
                    return;
                }
            }
            catch (Exception ex) { Check(false, $"裸管道对照(ConnectAsync)：连接失败 {ex.GetType().Name}: {ex.Message}"); return; }
            try
            {
                var req = Encoding.UTF8.GetBytes("ping\n");
                c.Write(req, 0, req.Length);
                c.Flush();
            }
            catch (Exception ex)
            {
                Check(false, $"裸管道对照：**写入**失败 {ex.GetType().Name}: {ex.Message} ⇒ 本环境的管道写入受限，"
                    + "之前关于 IpcService 往返失败的结论不可断言");
                return;
            }
            var buf = new byte[256];
            var readTask = c.ReadAsync(buf, 0, buf.Length);
            if (!readTask.Wait(TimeSpan.FromSeconds(5)))
            {
                Check(false, "裸管道对照：5 秒内没读到响应 ⇒ 本环境管道收发不通");
                return;
            }
            var n = readTask.Result;
            Check(n > 0 && Encoding.UTF8.GetString(buf, 0, n).Contains("pong"),
                $"裸管道对照：自包含的裸管道往返**正常**（收到「{Encoding.UTF8.GetString(buf, 0, n).Trim()}」）"
                + " ⇒ 管道本身可用，IpcService 的往返失败可归到生产代码");
        }

        /// <summary>
        /// 向**正在运行的实例**（真实 GUI）提交一个任务并等产物：`ipc-submit &lt;输入文件&gt; &lt;输出目录&gt;`。
        /// 用途：验证 GUI 侧两件事 ——
        ///   ① `App.xaml.cs` 起的 IPC 服务端确实可用（而不是只在探针自建的服务端里可用）；
        ///   ② `HandleSubmitJob` 走 `mainWindow != null` 分支时，**S-P1-3 的 UI 线程编组真的生效**：
        ///      编组坏了就不会有产物（要么在非 UI 线程操作控件时抛异常、要么永远排不进队列）。
        ///      ⇒ 这条断言是 S-P1-3 唯一的**运行期**证据（此前只有代码审查）。
        /// ⚠ 需要目标实例已启动且 `EnableIpcServer = true`。
        /// </summary>
        private static void ProbeIpcSubmitToRunning(string inFile, string outDir)
        {
            var c = FfmpegGui.Services.IpcService.TryConnectAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
            if (c == null)
            {
                Check(false, "未连上正在运行的实例（它起了吗？EnableIpcServer 是否为 true？）");
                return;
            }
            using (c)
            {
                var payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    InputPaths = new[] { inFile },
                    OutputDirectory = outDir,
                    Format = "jpg",
                    EncoderBackend = 0,   // EncoderBackend.Ffmpeg
                    Quality = 90,
                    Concurrency = 1,
                    PreserveStructure = false
                });
                var r = FfmpegGui.Services.IpcService.SendRequestAsync(c,
                    new FfmpegGui.Services.IpcService.IpcRequest
                    { Id = 21, Type = FfmpegGui.Services.IpcService.RequestType.SubmitJob, Payload = payload },
                    TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                Check(r?.Id == 21 && r.Success && (r.Message?.Contains("Submitted") ?? false),
                    $"向运行中实例 SubmitJob 受理（Id={r?.Id} Success={r?.Success}「{r?.Message}」）");

                var expected = System.IO.Path.Combine(outDir,
                    System.IO.Path.GetFileNameWithoutExtension(inFile) + ".jpg");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(120) && !System.IO.File.Exists(expected))
                    System.Threading.Thread.Sleep(250);
                Check(System.IO.File.Exists(expected) && new System.IO.FileInfo(expected).Length > 0,
                    $"GUI 队列真的处理并产出（{expected}）⇒ UI 线程编组生效（否则排不进队列或必抛）");
            }
        }

        private static void ProbeIpc()
        {
            // ⚠ 本探针只做**自包含**的两项（不依赖与自身进程内的 IPC 服务器做往返）：
            //   ① S-P1-6：对端接受连接但永不回包时，客户端必须按超时返回，绝不能无限挂起；
            //   ② S-P1-5：能力缓存字段类型 + 并发读冒烟。
            // 为什么不做「应用 IPC 服务器往返」：实测在**同进程内**同时跑服务端与客户端时，
            //   Ping 得不到响应（客户端 3 秒超时返回「请求超时」），根因未定位；
            //   而往返用例一旦挂住会把整条门禁拖死 ⇒ 在查清之前**不要**把它加进门禁清单。
            //   服务端循环本身的修复（S-P1-4）见源码注释，其缺陷有直接证据：
            //   修复前**第一个**客户端就会让服务器抛异常死掉（客户端侧表现为 IOException: Pipe is broken）。

            // ① 静默对端：接受连接但永不回包
            try
            {
                var sn = "ffmpegpictureui-probe-silent-" + Environment.ProcessId;
                using var silent = new System.IO.Pipes.NamedPipeServerStream(sn,
                    System.IO.Pipes.PipeDirection.InOut, 1,
                    System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    await silent.WaitForConnectionAsync();
                    await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(20));
                });
                using var sc = new System.IO.Pipes.NamedPipeClientStream(".", sn,
                    System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
                sc.Connect(3000);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var rTo = FfmpegGui.Services.IpcService.SendRequestAsync(sc,
                    new FfmpegGui.Services.IpcService.IpcRequest
                    { Id = 99, Type = FfmpegGui.Services.IpcService.RequestType.Ping },
                    TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                sw.Stop();
                Check(rTo != null && !rTo.Success && (rTo.Message?.Contains("超时") ?? false)
                        && sw.Elapsed.TotalSeconds < 10,
                    $"静默对端下客户端按超时返回（实耗 {sw.Elapsed.TotalSeconds:F1}s，消息「{rTo?.Message}」）"
                    + " —— 旧写法读无 token、写/刷无界，这里会无限挂起");
            }
            catch (Exception ex)
            {
                Check(false, $"静默对端超时用例异常: {ex.GetType().Name}: {ex.Message}");
            }

            // ② S-P1-5：缓存字段类型（确定性断言）+ 并发读冒烟
            var fld = typeof(FfmpegGui.Services.FormatCapabilitiesService)
                .GetField("_cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Check(fld != null && fld.FieldType.Name.Contains("ConcurrentDictionary"),
                $"能力缓存字段是并发字典（实为 {fld?.FieldType.Name}）");

            var errsBox = new long[1];
            var init = System.Threading.Tasks.Task.Run(
                () => FfmpegGui.Services.FormatCapabilitiesService.InitializeAsync());
            var readers = new System.Threading.Tasks.Task[8];
            for (int i = 0; i < readers.Length; i++)
            {
                int seed = i;
                readers[i] = System.Threading.Tasks.Task.Run(() =>
                {
                    for (int k = 0; k < 400; k++)
                    {
                        try
                        {
                            _ = FfmpegGui.Services.FormatCapabilitiesService
                                .GetCapabilities(((seed + k) % 2 == 0) ? "PNG" : "jxl");
                        }
                        catch { System.Threading.Interlocked.Increment(ref errsBox[0]); }
                    }
                });
            }
            System.Threading.Tasks.Task.WaitAll(readers);
            try { init.Wait(TimeSpan.FromSeconds(60)); } catch { }
            Check(errsBox[0] == 0, $"并发读能力表 + 初始化写不抛异常（异常 {errsBox[0]} 次）");
            Check(FfmpegGui.Services.FormatCapabilitiesService.GetCapabilities("PNG") != null,
                "大写「PNG」也能命中缓存（ToLowerInvariant 与缓存键一致）");

            // ③ IPC 往返（同进程）：起服务端 → 连它 → Ping → 期望 pong。
            //    这一条在 2026-09-15 之前**不可能**通过：服务端用 StreamReader/StreamWriter 会抛异常
            //    被 `catch (Exception) { }` 静默吞掉（对端表现为刚连上就断管），且消息被
            //    `AppJsonContext.WriteIndented = true` 序列化成**多行** JSON（接收端按行只拿到碎片）
            //    ⇒ 服务端永远不回包、客户端只能超时。两处都修好后实测 0.07 秒拿到 pong。
            //    ⚠ 管道名按用户 SID 派生 ⇒ 跑本探针时**不能**有正在运行的 GUI 实例。
            {
                var cts = new System.Threading.CancellationTokenSource();
                var srv = System.Threading.Tasks.Task.Run(
                    () => FfmpegGui.Services.IpcService.StartServerAsync(null, null, cts.Token));
                System.Threading.Thread.Sleep(300);
                var c = FfmpegGui.Services.IpcService.TryConnectAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                if (c == null)
                {
                    Check(false, "IPC 往返：5 秒内未连上本进程内的服务端（管道名被占用？先关掉运行中的实例）");
                }
                else
                {
                    using (c)
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var r = FfmpegGui.Services.IpcService.SendRequestAsync(c,
                            new FfmpegGui.Services.IpcService.IpcRequest
                            { Id = 42, Type = FfmpegGui.Services.IpcService.RequestType.Ping },
                            TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                        sw.Stop();
                        Check(r?.Success == true && r.Message == "pong",
                            $"IPC 往返（同进程）拿到 pong（实耗 {sw.Elapsed.TotalSeconds:F2}s，实得「{r?.Message}」）");
                    }
                }
                // ④ 请求处理器：响应必须**回显 req.Id**，且「什么都没做」时**不得谎报成功**。
                //    旧写法把 Id 写死成 0（客户端按 resp.Id == req.Id 匹配 ⇒ GetStatus/Pause/Resume/Stop
                //    这些请求永远匹配不上、只能超时，尽管服务端其实已经执行了）；并且在既无 MainWindow
                //    也无 orchestrator 时仍报 Success=true（"Paused"/"Stopped"）——谎报成功比报错更糟。
                FfmpegGui.Services.IpcService.IpcResponse? Ask(
                    FfmpegGui.Services.IpcService.RequestType t, long id)
                {
                    var cc = FfmpegGui.Services.IpcService
                        .TryConnectAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    if (cc == null) return null;
                    using (cc)
                    {
                        return FfmpegGui.Services.IpcService.SendRequestAsync(cc,
                            new FfmpegGui.Services.IpcService.IpcRequest { Id = id, Type = t },
                            TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    }
                }

                // ⚠ 断言必须同时看 Message：`SendRequestAsync` 超时返回的**合成响应**也带 req.Id
                //    ⇒ 只断言 `Id == req.Id` 是**空断言**（Id 写死 0 时客户端匹配不上、走超时兜底，
                //    而兜底响应的 Id 恰好就是 req.Id）。这一点是**变异测试抓出来的**：
                //    把 GetStatus 的 `Id = req.Id` 改回 0，只断言 Id 的版本依然全绿。
                var st = Ask(FfmpegGui.Services.IpcService.RequestType.GetStatus, 7);
                Check(st != null && st.Id == 7 && st.Success && (st.Message?.Contains("队列计数不可用") ?? false),
                    $"GetStatus 回显 req.Id 且拿到**真实**响应（发 7，实得 Id={st?.Id} Success={st?.Success}「{st?.Message}」）");
                var ps = Ask(FfmpegGui.Services.IpcService.RequestType.Pause, 8);
                Check(ps != null && ps.Id == 8 && !ps.Success && (ps.Message?.Contains("未执行") ?? false),
                    $"无 MainWindow/orchestrator 时 Pause 如实报失败且回显 Id（发 8，实得 Id={ps?.Id} Success={ps?.Success}「{ps?.Message}」）");
                var sp = Ask(FfmpegGui.Services.IpcService.RequestType.Stop, 9);
                Check(sp != null && sp.Id == 9 && !sp.Success && (sp.Message?.Contains("未执行") ?? false),
                    $"无 MainWindow/orchestrator 时 Stop 如实报失败且回显 Id（发 9，实得 Id={sp?.Id} Success={sp?.Success}「{sp?.Message}」）");

                try { cts.Cancel(); } catch { }
                try { srv.Wait(TimeSpan.FromSeconds(3)); } catch { }
            }

            // ⑤ 端到端 SubmitJob：IPC → DefaultQueueOrchestrator → QueueProcessor → 真实转换 → 产物。
            //    这是 IPC 的**核心功能**（把任务提交给正在运行的实例），而此前 IPC 从未跑通
            //    ⇒ 这条路径**从未被验证过**。无 MainWindow 时处理器走 orchestrator 分支，
            //    而 DefaultQueueOrchestrator 有无参构造 ⇒ 可以 headless 驱动。
            //    ⚠ 这里必须**另起一个服务端**（带真实 orchestrator）：上面的块用的是 (null, null)，
            //      若共用会把 Pause/Stop 的断言语义改掉（有 orchestrator 时它们会返回 Success=true）。
            {
                var work = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "ipcsubmit_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                var inDir = System.IO.Path.Combine(work, "in");
                var outDir = System.IO.Path.Combine(work, "out");
                System.IO.Directory.CreateDirectory(inDir);
                System.IO.Directory.CreateDirectory(outDir);
                var srcImg = System.IO.Path.Combine(inDir, "a.png");

                var ff = System.IO.Path.Combine(
                    Environment.GetEnvironmentVariable("FFMPEGGUI_FFMPEG_DIR") ?? "", "ffmpeg.exe");
                if (!System.IO.File.Exists(ff))
                {
                    Console.WriteLine("INFO 未找到 ffmpeg（FFMPEGGUI_FFMPEG_DIR 未设？）⇒ ⑤ SubmitJob 用例 SKIP");
                }
                else
                {
                    var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = ff,
                        Arguments = $"-y -v error -f lavfi -i \"testsrc=size=64x64:rate=1\" -frames:v 1 \"{srcImg}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    })!;
                    mk.WaitForExit();

                    // DefaultQueueOrchestrator 是 internal ⇒ 用**反射**构造并转成公开的 IQueueOrchestrator。
                    // 之所以不用 InternalsVisibleTo：改 csproj 会触发 NuGet 还原，而本环境还原不可用
                    // （见 HANDOVER §C.4 的 ProgramData 坑）⇒ 反射是这里最省事且不动生产配置的办法。
                    var orchType = typeof(FfmpegGui.Services.IpcService).Assembly
                        .GetType("FfmpegGui.Services.DefaultQueueOrchestrator", throwOnError: true)!;
                    var orch = (FfmpegGui.Services.IQueueOrchestrator)Activator.CreateInstance(orchType)!;
                    var cts2 = new System.Threading.CancellationTokenSource();
                    var srv2 = System.Threading.Tasks.Task.Run(
                        () => FfmpegGui.Services.IpcService.StartServerAsync(orch, null, cts2.Token));
                    System.Threading.Thread.Sleep(300);

                    var cc = FfmpegGui.Services.IpcService
                        .TryConnectAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    if (cc == null)
                    {
                        Check(false, "SubmitJob：5 秒内未连上服务端");
                    }
                    else
                    {
                        using (cc)
                        {
                            // 手搓 payload JSON（IpcJsonContext 是 internal，探针用不了）；
                            // 属性名必须与 SubmitJobPayload 完全一致（源生成默认大小写敏感）。
                            var payload = System.Text.Json.JsonSerializer.Serialize(new
                            {
                                InputPaths = new[] { srcImg },
                                OutputDirectory = outDir,
                                Format = "jpg",
                                EncoderBackend = 0,   // EncoderBackend.Ffmpeg
                                Quality = 90,
                                Concurrency = 1,
                                PreserveStructure = false
                            });
                            var r = FfmpegGui.Services.IpcService.SendRequestAsync(cc,
                                new FfmpegGui.Services.IpcService.IpcRequest
                                {
                                    Id = 11,
                                    Type = FfmpegGui.Services.IpcService.RequestType.SubmitJob,
                                    Payload = payload
                                },
                                TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                            Check(r?.Id == 11 && r.Success && (r.Message?.Contains("Submitted") ?? false),
                                $"SubmitJob 受理（实得 Id={r?.Id} Success={r?.Success}「{r?.Message}」）");

                            // 等产物：这一步才算端到端（IPC → 编排 → 队列 → ffmpeg → 文件）
                            var outFile = System.IO.Path.Combine(outDir, "a.jpg");
                            var sw2 = System.Diagnostics.Stopwatch.StartNew();
                            while (sw2.Elapsed < TimeSpan.FromSeconds(90) && !System.IO.File.Exists(outFile))
                                System.Threading.Thread.Sleep(200);
                            Check(System.IO.File.Exists(outFile) && new System.IO.FileInfo(outFile).Length > 0,
                                $"SubmitJob 端到端产出文件（期望 {outFile}）");
                        }
                    }
                    try { cts2.Cancel(); } catch { }
                    try { srv2.Wait(TimeSpan.FromSeconds(3)); } catch { }
                }
            }
        }

        /// <summary>
        /// S-P1-1 / S-P0-2 的三条不变式（用 --settings 独立文件驱动真实的 AppSettingsService）：
        /// ① 写不进去时 Save() 必须返回 false 并说清原因（旧写法 <c>catch {}</c> 吞掉 ⇒ 用户以为已保存）；
        /// ② 原子写不留 <c>.tmp</c>，且第二次保存要把上一版留在 <c>.bak</c>（旧写法直写，打断即毁唯一一份）；
        /// ③ 设置文件损坏时 Load 必须留下 <c>LastLoadIssue</c>（旧写法静默重置为默认值）。
        /// </summary>
        private static void ProbeSettingsSave()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cfgprobe_{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                var f = System.IO.Path.Combine(dir, "settings.json");
                System.IO.File.WriteAllText(f, "{}");
                AAS.Load(f);

                AAS.Current.Language = "en-US";
                AAS.Current.MaxQueueSize = 7;
                bool ok1 = AAS.Save();
                Check(ok1 && AAS.LastSaveIssue == null,
                    $"第一次保存成功且无告警（ok={ok1}, issue={AAS.LastSaveIssue}）");
                Check(!System.IO.File.Exists(f + ".tmp"), "原子写不留 .tmp 残片");
                var t1 = System.IO.File.ReadAllText(f);
                Check(t1.Contains("en-US") && t1.Contains("7"), "落盘内容确实是本次内存值（语言 + 队列并发）");

                AAS.Current.Language = "zh-CN";
                bool ok2 = AAS.Save();
                Check(ok2 && System.IO.File.Exists(f + ".bak")
                        && System.IO.File.ReadAllText(f + ".bak").Contains("en-US"),
                    "第二次保存把上一版留在 .bak（写坏/崩溃时可回滚）");

                var beforeLocked = System.IO.File.ReadAllText(f);
                using (new System.IO.FileStream(f, System.IO.FileMode.Open,
                           System.IO.FileAccess.ReadWrite, System.IO.FileShare.None))
                {
                    AAS.Current.Language = "en-US";
                    bool ok3 = AAS.Save();
                    Check(!ok3 && !string.IsNullOrEmpty(AAS.LastSaveIssue),
                        $"目标不可写时 Save 返回 false 并给出原因（ok={ok3}, issue={AAS.LastSaveIssue}）");
                    // ⚠ 不能在独占句柄还开着时 ReadAllText —— 那连打开都不允许（实测 IOException），是探针自己的错
                }
                Check(System.IO.File.ReadAllText(f) == beforeLocked,
                    "失败时目标文件内容一字未动（原子写真正要买的东西）");

                var bad = System.IO.Path.Combine(dir, "corrupt.json");
                System.IO.File.WriteAllText(bad, "{ this is not json");
                AAS.Load(bad);
                Check(AAS.LastLoadIssue != null,
                    $"损坏的设置文件被记录而非静默重置（issue={AAS.LastLoadIssue}）");
            }
            finally
            {
                try { System.IO.Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>
        /// P1-2 的守卫探针：用 <c>cmd.exe</c> 当子进程替身（FfmpegRunner 的 ffmpegPath 可注入），
        /// 这样不必真等 30 分钟也能测“静默 + 不耗 CPU”这一类挂死。
        /// ⚠ 2026-09-19（#15-b 收尾轮）**载体大改**，起因是一条真实回归：心跳改为**默认开启**后，
        /// 注入自检原先**只看参数、不看目标** ⇒ 给 <c>cmd.exe</c> 追加 ` -progress pipe:1` 会**改坏它的
        /// 命令行**（ping 收到未知选项、**0.0 s 退 1**）⇒ case A / case C **两条同时转红**
        /// （E⑫ 整轮实测：`runner` 2/0 → 0/2）。修法（`CanInjectProgressPipe` 加 `IsFfmpegTarget` 一道）后
        /// **cmd.exe 不再被注入** ⇒ 它的活性判据回到**旧双信号路径**。
        /// ⇒ 于是 C 段载体**必须换**：cmd 上「心跳判死」分支已不可达 ⇒ 原载体上「取消优先于心跳判死」
        /// 会**恒绿**（空断言，看着像在测其实没测）。新载体 = **真 ffmpeg + 1 字节畸形 `.jxl`**
        /// （与 <c>verify-ffmpeg-heartbeat.ps1</c> ② 同一 fixture）：名字像 ffmpeg ⇒ **会被注入**，
        /// 且**零进度块**（本探针前置实测：4 s 仍存活、进度 0 字节）⇒ 同时满足「会被注入」∧「零进度块」
        /// 两个必要条件，才能真的二分“谁先到谁赢”。
        /// ⚠ B 段（原 <c>runner --hang</c>）同理：原载体是 cmd、靠**降低阈值**判死；cmd 不再被注入后
        /// 它只能走旧 30 min 路径 ⇒ 换成真 ffmpeg 忙等，并**默认跑**（不再 gate 在 flag 后 —— 否则
        /// 整轮跑的是无参数 `runner`，心跳判死路径会**零覆盖**）。成本 ~2 s（hbSec=1 即判死）。
        /// </summary>
        private static async Task ProbeRunnerGuard(string[] args)
        {
            const string Cmd = @"C:\Windows\System32\cmd.exe";
            // ❗ 替身一律用**绝对路径**，不要写裸命令名 ping：
            //   本宿主下 Start-Process 传给子进程的环境块**不含 System32**（实测 where.exe ping ⇒ exit=1、
            //   cmd 报 "'ping' is not recognized..."），而宿主里 Get-Command ping.exe 却能找到。
            //   用裸名会让本探针在“经门禁脚本跑”时假红（退出码 1/0.2s），而直接跑却绿 —— 曾据此误判为产物陈旧。
            //   生产代码不受影响：FfmpegRunner 用的是 AppSettingsService.Current.FfmpegPath（绝对路径）。
            const string Ping = @"C:\Windows\System32\PING.EXE";
            // 忙等载体：真 ffmpeg + 1 字节 `0x00` 的 .jxl（与 verify-ffmpeg-heartbeat.ps1 ② 同一 fixture）。
            // ⚠ 实测（本探针落地前）：`-i <1字节.jxl> -frames:v 1 <out.png>` 在 4.0 s 时**仍存活且进度 0 字节**
            //   ⇒ 满足「会被注入」（名字含 ffmpeg）∧「零进度块」两个必要条件。
            var ff = FfmpegGui.Services.AppSettingsService.Current.FfmpegPath;
            var work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "runnerprobe_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(work);
            var badJxl = System.IO.Path.Combine(work, "bad.jxl");
            System.IO.File.WriteAllBytes(badJxl, new byte[] { 0x00 });
            var logA = new System.Text.StringBuilder();
            var swA = System.Diagnostics.Stopwatch.StartNew();
            var rcA = await FfmpegRunner.RunAsync($"/c {Ping} -n 3 127.0.0.1 >nul", s => logA.Append(s), Cmd);
            swA.Stop();
            // ⚠ 失败时必须把子进程输出带出来：只报“退出码 1”无法区分“cmd 自己报错”与“守卫误杀”。
            //   实测（2026-09-15）：经 Start-Process 启动本探针时 cmd 退 1/0.2s，直接跑却退 0/2.4s；
            //   没有这段输出就只能靠猜。
            Check(rcA == 0 && logA.ToString().IndexOf("[超时]") < 0,
                $"静默但正常退出的子进程不被误杀（退出码 {rcA}，{swA.Elapsed.TotalSeconds:F1}s）"
                + (rcA == 0 ? "" : $"；子进程输出=<{Dump(logA)}>"));

            // ── C-legacy：取消语义（cmd 替身 ⇒ (A) 后走**旧双信号路径**）──────────────────────
            //   ⚠ 它**不再**经过心跳判死分支 ⇒ 只能验“旧路径的取消语义”，别当成“取消优先于心跳判死”。
            //   ⚠ 两条都用 `threwC` 前置判据 ⇒ **绿/红都算 2 条**，断言总数不随成败漂移（基线才可对账）。
            var logC = new System.Text.StringBuilder();
            var swC = System.Diagnostics.Stopwatch.StartNew();
            var threwC = false;
            var rcC = 0;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                try
                {
                    rcC = await FfmpegRunner.RunAsync($"/c {Ping} -n 600 127.0.0.1 >nul", s => logC.Append(s), Cmd, cts.Token);
                }
                catch (OperationCanceledException) { threwC = true; }
            }
            swC.Stop();
            Check(threwC, "C-legacy 取消应抛 OperationCanceledException"
                + (threwC ? "" : $"（实际返回码 {rcC}；子进程输出=<{Dump(logC)}>）"));
            Check(threwC && swC.Elapsed.TotalSeconds < 30,
                $"C-legacy 取消在 {swC.Elapsed.TotalSeconds:F1}s 内传播（子进程被终止，不是等到自然退出）");

            // ── C1：真 ffmpeg 忙等（零进度块）下，**取消必须优先于心跳判死** ──────────────────────
            //   hbSec=8 ⇒ 心跳判死最早落在 ~8 s；取消在 3 s ⇒ 若实现把取消**推迟到心跳 tick**（或让心跳
            //   抢先判死），本用例会拿到 HangExitCode（≈8 s）而不是 OCE ⇒ 转红。
            //   ⚠ 两条自检专防“空断言”：注入必须真的发生、且心跳判死行必须**不出现**。
            {
                var prev = Environment.GetEnvironmentVariable("FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC");
                Environment.SetEnvironmentVariable("FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC", "8");
                try
                {
                    var log1 = new System.Text.StringBuilder();
                    var sw1 = System.Diagnostics.Stopwatch.StartNew();
                    var threw1 = false;
                    var rc1 = 0;
                    using (var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                    {
                        try
                        {
                            rc1 = await FfmpegRunner.RunAsync(
                                $"-y -hide_banner -loglevel error -i \"{badJxl}\" -frames:v 1 \"{System.IO.Path.Combine(work, "out_c1.png")}\"",
                                s => log1.Append(s), ff, cts1.Token);
                        }
                        catch (OperationCanceledException) { threw1 = true; }
                    }
                    sw1.Stop();
                    var t1 = log1.ToString();
                    Check(threw1, "C1 真 ffmpeg 忙等（零进度块）下，**取消**必须抛 OperationCanceledException"
                        + (threw1 ? "" : $"；实际返回码 {rc1}（{FfmpegRunner.HangExitCode} = 心跳判死抢先），"
                            + $"耗时 {sw1.Elapsed.TotalSeconds:F1}s；输出=<{Dump(log1)}>"));
                    Check(threw1 && sw1.Elapsed.TotalSeconds >= 2.5 && sw1.Elapsed.TotalSeconds <= 6,
                        $"C1 取消在 ~3 s 生效（实测 {sw1.Elapsed.TotalSeconds:F1}s ∈ [2.5, 6]）⇒ **远早于** 8 s 心跳阈值"
                        + " ⇒ 取消没有被推迟到心跳 tick");
                    Check(t1.IndexOf("[心跳] 已追加") >= 0,
                        "C1 自检：本用例确实**注入了** `-progress pipe:1`（否则「取消优先」是空断言）");
                    Check(t1.IndexOf("无进度心跳") < 0,
                        "C1 自检：**没有**出现心跳判死行 ⇒ 判死没有抢先于取消");
                }
                finally { Environment.SetEnvironmentVariable("FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC", prev); }
            }

            // ── B：忙等（零进度块）必须被心跳**限时判死**（原 `--hang` 的真 ffmpeg 版）────────────
            {
                var prevB = Environment.GetEnvironmentVariable("FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC");
                Environment.SetEnvironmentVariable("FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC", "1");
                try
                {
                    var logB = new System.Text.StringBuilder();
                    var swB = System.Diagnostics.Stopwatch.StartNew();
                    var rcB = await FfmpegRunner.RunAsync(
                        $"-y -hide_banner -loglevel error -i \"{badJxl}\" -frames:v 1 \"{System.IO.Path.Combine(work, "out_stall.png")}\"",
                        s => logB.Append(s), ff);
                    swB.Stop();
                    var txt = logB.ToString();
                    Check(rcB == FfmpegRunner.HangExitCode && txt.IndexOf("无进度心跳") >= 0,
                        $"B 忙等（零进度块）被心跳限时判死（退出码 {rcB}，期望 {FfmpegRunner.HangExitCode}，"
                        + $"耗时 {swB.Elapsed.TotalSeconds:F0}s）—— 忙等型挂死不再永不返回");
                }
                finally { Environment.SetEnvironmentVariable("FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC", prevB); }
            }

            try { System.IO.Directory.Delete(work, true); } catch { }
        }

        /// <summary>
        /// 把子进程输出压成一行并截断，供断言失败时携带。
        /// 只报退出码的诊断价值极低（“退出码 1”既可能是守卫误杀，也可能是 cmd 自己报错）。
        /// </summary>
        private static string Dump(System.Text.StringBuilder sb)
        {
            var s = sb.ToString().Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length == 0) return "(无输出)";
            return s.Length > 300 ? s.Substring(0, 300) + "…" : s;
        }

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

        /// <summary>png3 &lt;file.png&gt; [sbitBits（0=不写）] [primaries transfer]：调生产代码 PngCicpService 写 chunk。</summary>
        private static void ProbePng3(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("用法: png3 <file.png> [sbitBits] [primaries transfer]"); return; }
            string path = args[1];
            // ⚠ 两项要分开报：旧写法 bits=0（意为“不写 sBIT”）也会去调 TryInsertSbit，
            //    它失败后把“cICP 已写成”一起抹平成“写入结果=False”⇒ 看板上误判为失败（实测）。
            bool sbitOk = true, cicpOk = true;
            bool didSbit = false, didCicp = false;
            int sbitBits = 0;
            if (args.Length >= 3 && int.TryParse(args[2], out int bits))
            {
                if (bits > 0) { didSbit = true; sbitBits = bits; sbitOk = FfmpegGui.Services.PngCicpService.TryInsertSbit(path, bits); }
            }
            if (args.Length >= 5 && byte.TryParse(args[3], out byte pr) && byte.TryParse(args[4], out byte tr))
            {
                didCicp = true; cicpOk = FfmpegGui.Services.PngCicpService.TryInsertCicp(path, pr, tr);
            }
            Console.WriteLine($"png3: {path} "
                + (didSbit ? $"sBIT(写 {sbitBits}bit)={sbitOk} " : "sBIT=跳过 ")
                + (didCicp ? $"cICP={cicpOk} " : "cICP=跳过 ")
                + $"大小={(new FileInfo(path).Exists ? new FileInfo(path).Length : -1)}");
            Check((!didSbit || sbitOk) && (!didCicp || cicpOk), "png3 chunk 写入成功");
        }

        private static void ProbeSelfTest()
        {
            int w = 128, h = 128;
            var src = SyntheticFrame16(w, h);
            var cases = new (string name, string sp, string st, string dp, string dt)[]
            {
                ("sRGB→P3", "bt709", "iec61966-2-1", "smpte432", "iec61966-2-1"),
                ("P3→sRGB", "smpte432", "iec61966-2-1", "bt709", "iec61966-2-1"),
                ("sRGB→BT.2020", "bt709", "iec61966-2-1", "bt2020", "bt709"),
                ("BT.2020→sRGB", "bt2020", "bt709", "bt709", "iec61966-2-1"),
                ("sRGB 自身曲线往返", "bt709", "iec61966-2-1", "bt709", "iec61966-2-1"),
            };
            foreach (var (name, sp, st, dp, dt) in cases)
            {
                var spec = Spec(sp, st, dp, dt);
                var dst = new byte[src.Length];
                var dstFast = new byte[src.Length];
                var tt = (CK.BuildTables(spec.SrcCurve), CK.BuildTables(spec.DstCurve));
                CK.TransformRgb48le(src, dst, spec, w * h);
                CK.TransformRgb48le(src, dstFast, spec, w * h, tt.Item1, tt.Item2);   // 表驱动快路径
                
                // 0) 快路径 == 解析式参考路径（生产用表，验证必须覆盖表）
                //    ⚠️ 必须按 16-bit 样本比对：逐字节比会把 0x0100 vs 0x00FF（仅差 1 LSB）算成“差 255”。
                int lsb = 0, worstOff = -1;
                for (int i = 0; i + 2 <= dst.Length; i += 2)
                {
                    int dv = Math.Abs((dst[i] | (dst[i + 1] << 8)) - (dstFast[i] | (dstFast[i + 1] << 8)));
                    if (dv > lsb) { lsb = dv; worstOff = i; }
                }
                if (lsb > 1 && worstOff >= 0)
                {
                    int px = worstOff / 6 * 6;   // 同像素起点
                    Console.WriteLine($"    最差样本 byteOff={worstOff} 解析={(dst[worstOff] | (dst[worstOff + 1] << 8))} " +
                        $"表={(dstFast[worstOff] | (dstFast[worstOff + 1] << 8))} " +
                        $"输入像素=({src[px] | (src[px + 1] << 8)},{src[px + 2] | (src[px + 3] << 8)},{src[px + 4] | (src[px + 5] << 8)}) " +
                        $"输出像素 解析=({dst[px] | (dst[px + 1] << 8)},{dst[px + 2] | (dst[px + 3] << 8)},{dst[px + 4] | (dst[px + 5] << 8)}) " +
                        $"表=({dstFast[px] | (dstFast[px + 1] << 8)},{dstFast[px + 2] | (dstFast[px + 3] << 8)},{dstFast[px + 4] | (dstFast[px + 5] << 8)}) " +
                        $"src={spec.SrcCurve.Name} dst={spec.DstCurve.Name}");
                }
                // 容差按目标曲线自身性质定：BT.709/240M 的 toe 是幂段切线（ITU 系数取整后留有
                // ~21 LSB 台阶，lcms2/zimg 同样存在），拐点附近选分支不同可差至 24 LSB；其余曲线应 ≤1。
                int tolLsb = spec.DstCurve.HasItuRoundingKink ? 24 : 1;
                Check(lsb <= tolLsb, $"{name}: 表驱动与解析式一致（max {lsb}/65535，容差 {tolLsb}"
                    + (spec.DstCurve.HasItuRoundingKink ? "，ITU 取整拐点台阶" : "") + "）");

                // 0b) **8-bit 出口**也要表/解析双路径互验 —— 实际教训：EncodeTo8 的解析式分支
                //     漏乘 65535（把 0..1 当 16-bit 码值），整幅输出变近黑，而此缺口只在无表路径出现
                //     （小帧 <256k 像素就是不建表），16-bit 出口的上面那条断言根本看不到。
                {
                    var d8a = new byte[w * h * 3];
                    var d8t = new byte[w * h * 3];
                    CK.TransformRgb48leToRgb8(src, d8a, spec, w * h, null, null, w, 0);
                    CK.TransformRgb48leToRgb8(src, d8t, spec, w * h, tt.Item1, tt.Item2, w, 0);
                    int mx = 0, worst8 = -1;
                    for (int i = 0; i < d8a.Length; i++)
                    { int dd = Math.Abs(d8a[i] - d8t[i]); if (dd > mx) { mx = dd; worst8 = i; } }
                    // 均值也要报：只查“两路一致”会漏掉“两路一起错”（黑图两边都黑照样通过）
                    double meanA = 0, meanT = 0;
                    foreach (byte by in d8a) meanA += by;
                    foreach (byte by in d8t) meanT += by;
                    meanA /= Math.Max(1, d8a.Length); meanT /= Math.Max(1, d8t.Length);
                    Console.WriteLine($"    rgb8 出口：最大差异 {mx}/255（idx={worst8}）解析均值={meanA:F1} 表均值={meanT:F1}");
                    Check(mx <= (spec.DstCurve.HasItuRoundingKink ? 2 : 1),
                        $"{name}: rgb8 出口 表驱动与解析式一致（max {mx}/255）");
                    Check(meanA > 8 && meanT > 8, $"{name}: rgb8 出口亮度合理（均值 {meanA:F1}/{meanT:F1}，非全黑）");
                }

                // 1) 快内核 == 参考实现（同一数学的两个入口不能分叉）
                double worst = 0; int checkedCount = 0;
                for (int i = 0; i < w * h; i += 7)
                {
                    int o = i * 6;
                    var (rr, gg, bb) = CK.TransformPixelReference(
                        (src[o] | (src[o + 1] << 8)) / 65535f,
                        (src[o + 2] | (src[o + 3] << 8)) / 65535f,
                        (src[o + 4] | (src[o + 5] << 8)) / 65535f, spec);
                    worst = Math.Max(worst, Math.Abs(rr * 65535 - (dst[o] | (dst[o + 1] << 8))));
                    worst = Math.Max(worst, Math.Abs(gg * 65535 - (dst[o + 2] | (dst[o + 3] << 8))));
                    worst = Math.Max(worst, Math.Abs(bb * 65535 - (dst[o + 4] | (dst[o + 5] << 8))));
                    checkedCount++;
                }
                Check(worst <= 1.5, $"{name}: 融合内核与参考实现一致（max {worst:F2}/65535，样本 {checkedCount}）");

                // 2) 中性保持：灰阶斜坡映射后 R=G=B（白点守恒的直接体现）
                int off = (h / 8) * w * 6;
                int bad = 0;
                for (int x = 0; x < w; x++)
                {
                    int o = off + x * 6;
                    int r = dst[o] | (dst[o + 1] << 8), g = dst[o + 2] | (dst[o + 3] << 8), b = dst[o + 4] | (dst[o + 5] << 8);
                    if (Math.Max(Math.Abs(r - g), Math.Max(Math.Abs(g - b), Math.Abs(r - b))) > 2) bad++;
                }
                Check(bad == 0, $"{name}: 灰阶保持中性（异常 {bad}/{w}）");

                // 3) 单调性：斜坡不得出现反向跳变
                int rev = 0;
                for (int x = 1; x < w; x++)
                {
                    int a = off + (x - 1) * 6, bq = off + x * 6;
                    if ((dst[bq] | (dst[bq + 1] << 8)) < (dst[a] | (dst[a + 1] << 8))) rev++;
                }
                Check(rev == 0, $"{name}: 斜坡单调（反向 {rev}）");
            }

            // 4) 往返：sRGB→P3→sRGB 应基本恒等
            var a1 = new byte[src.Length]; var a2 = new byte[src.Length];
            CK.TransformRgb48le(src, a1, Spec("bt709", "iec61966-2-1", "smpte432", "iec61966-2-1"), w * h);
            CK.TransformRgb48le(a1, a2, Spec("smpte432", "iec61966-2-1", "bt709", "iec61966-2-1"), w * h);
            double psnr = CM.Psnr16(src, a2);
            Console.WriteLine($"  sRGB→P3→sRGB 往返 PSNR(16bit) = {psnr:F1} dB");
            Check(psnr >= 80, $"往返恒等（PSNR ≥ 80dB）");

            // 5) 恒等跳过：IsIdentity 必须为 true（避免无谓全帧重编码）
            Check(Spec("bt709", "iec61966-2-1", "bt709", "iec61966-2-1", matrix: false).IsIdentity, "同空间 Spec.IsIdentity=true");

            // 6) 8-bit 表路径：同空间往返必须字节不变
            var lut = TC.Srgb.BuildEotfTable8();
            var s8 = new byte[] { 0, 32, 64, 255, 128, 12, 200, 255, 1, 2, 3, 255 };
            var d8 = new byte[12];
            var enc = CK.BuildEncodeLut16(TC.Srgb, true);
            CK.TransformRgba8(s8, d8, Spec("bt709", "iec61966-2-1", "bt709", "iec61966-2-1", matrix: false), lut, w, h, enc);
            bool same = true;
            for (int i = 0; i < 12; i++) same &= s8[i] == d8[i];
            Check(same, "8bit 表路径：同空间往返字节不变");

            // 7) 调度器规模分档：小图单线程、中图整帧并行、大图流式（不新增并发限制，仅选策略）
            var sc1 = FfmpegGui.Services.ColorMapping.ColorScheduler.ForItem(2, 512, 512, 6);
            var sc2 = FfmpegGui.Services.ColorMapping.ColorScheduler.ForItem(2, 8192, 4096, 6);
            var scM = FfmpegGui.Services.ColorMapping.ColorScheduler.ForItem(2, 4096, 2160, 6);
            Check(sc1.Tier == "S" && sc1.Dop == 1, $"小图走 S 档单线程（实为 {sc1.Tier}/dop{sc1.Dop}）");
            Check(scM.Tier == "M" && !scM.Streaming, $"4K 走 M 档整帧并行（实为 {scM.Tier} stream={scM.Streaming}）");
            Check(sc2.Streaming && sc2.TileRows >= 1, $"8K 走 L 档流式（{sc2.ToLogFragment()}）");
        }

        /// <summary>
        /// bench —— 性能基线：内核吐量（含 DOP 扩展性）+ 端到端 H1 vs H2(zscale) 对比。
        /// 数据写入控制台，由 run-color-engine-verification.ps1 归档到 docs。
        /// </summary>
        private static void ProbeBench(string[] args)
        {
            // 绝对门槛只在 Release 下成立（Debug 关掉优化，吞吐会差 5-10 倍，拿它当基线会误判）
            bool isRelease = typeof(Program).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
                is System.Reflection.AssemblyConfigurationAttribute[] { Length: > 0 } cfg
                && string.Equals(cfg[0].Configuration, "Release", StringComparison.OrdinalIgnoreCase);
            if (!isRelease)
                Console.WriteLine("⚠ 当前为 Debug 构建：只输出数值，不做绝对吞吐门槛（请用 -c Release 复跑）");

            bool big = args.Length >= 2 && args[1] == "big";
            var sizes = big ? new[] { (1024, 1024), (4096, 2160), (8192, 4096) } : new[] { (1024, 1024), (4096, 2160) };
            var namedCases = new (string name, string sp, string st, string dp, string dt)[]
            {
                ("sRGB→P3(矩阵同曲线)", "bt709", "iec61966-2-1", "smpte432", "iec61966-2-1"),
                ("BT.2020→sRGB(含裁切)", "bt2020", "bt709", "bt709", "iec61966-2-1"),
                ("PQ→sRGB(变曲线+裁切)", "bt2020", "smpte2084", "bt709", "iec61966-2-1"),
            };
            int cores = Environment.ProcessorCount;
            foreach (var (w, h) in sizes)
            {
                var src = SyntheticFrame16(w, h);
                var dst = new byte[src.Length];
                Console.WriteLine($"\n--- {w}x{h} ({(long)w * h / 1_000_000.0:F1} MPix, rgb48={src.Length / 1048576}MB) cores={cores} ---");
                foreach (var (name, sp, st, dp, dt) in namedCases)
                {
                    var spec = Spec(sp, st, dp, dt);
                    var ttS = CK.BuildTables(spec.SrcCurve); var ttD = CK.BuildTables(spec.DstCurve);
                    double single = 0, best = 0, singleSlow = 0, bestSlow = 0;
                    foreach (int dop in new[] { 1, Math.Min(2, cores), Math.Min(4, cores), Math.Min(8, cores), cores })
                    {
                        // 每档取 N 次最优（min 耗时）：单样本估扩展比噪声大（实测同一用例 2.9×/3.2×/5.2× 飘）
                        double ms = double.MaxValue, slowMs = double.MaxValue;
                        for (int rep = 0; rep < 3; rep++)
                        {
                            Array.Clear(dst);
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            int rows = Math.Max(1, h / dop);
                            var opt = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = dop };
                            System.Threading.Tasks.Parallel.For(0, (h + rows - 1) / rows, opt, b =>
                            {
                                int rs = b * rows, rc = Math.Min(rows, h - rs);
                                int off = rs * w * 6, n = rc * w;
                                CK.TransformRgb48le(new ReadOnlySpan<byte>(src, off, n * 6), new Span<byte>(dst, off, n * 6), spec, n, ttS, ttD);
                            });
                            sw.Stop();
                            ms = Math.Min(ms, sw.Elapsed.TotalMilliseconds);
                        }
                        double mpxs = (long)w * h / 1e6 / Math.Max(1e-6, ms / 1000.0);
                        double mbs = src.Length * 2.0 / 1048576.0 / Math.Max(1e-6, ms / 1000.0);
                        if (dop == 1 || dop == cores)
                        {
                            // 解析式（无表）对照，取 3 次最优
                            for (int rep = 0; rep < 3; rep++)
                            {
                                Array.Clear(dst);
                                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                                CK.TransformRgb48le(src, dst, spec, w * h);
                                sw2.Stop();
                                slowMs = Math.Min(slowMs, sw2.Elapsed.TotalMilliseconds);
                            }
                            double slow = (long)w * h / 1e6 / Math.Max(1e-6, slowMs / 1000.0);
                            if (dop == 1) singleSlow = slow; else bestSlow = slow;
                        }
                        Console.WriteLine($"  {name,-24} dop={dop,-2} {ms,8:F1}ms  {mpxs,6:F1} MPix/s  {mbs,6:F0} MB/s(读+写)");
                        if (dop == 1) single = mpxs;
                        best = Math.Max(best, mpxs);
                    }
                    Console.WriteLine($"    参考（解析式单核）= {singleSlow:F1} MPix/s  快路径增益 = {single / Math.Max(1e-6, singleSlow):F1}×");
                    if (!isRelease) continue;
                    // 门槛（Release、表驱动快路径）：单核 ≥ 40 MPix/s（全尺寸都门禁）；
                    // 扩展比 ≥4× **只在 ≥8MPix（工作集 ≥50MB、带宽受限）的尺寸上作门禁**：
                    //   小帧（6MB）单核已接近内存带宽上限（实测 157.8 MPix/s ≈ 6.3 GB/s），此时“扩展比”衡量的是
                    //   调度开销而非并行能力（实测 3.4×/6.4× 飘手），作门禁无意义 → 只记录。
                    Check(single >= 40, $"{name} 单核 ≥40MPix/s（实 {single:F1}）");
                    bool bandwidthBound = (long)w * h >= 8_000_000;
                    if (bandwidthBound)
                        Check(best / Math.Max(1e-6, single) >= 4.0, $"{name} 多核扩展比 ≥4×（实 {best / Math.Max(1e-6, single):F1}×）");
                    else
                        Console.WriteLine($"    (小帧扩展比 {best / Math.Max(1e-6, single):F1}× 仅记录：单核已近带宽上限，不作门禁)");
                }
            }

            // 端到端：引擎（H1，解→变换→编）vs 传统 zscale（H2，单进程）
            BenchEndToEnd(isRelease);
        }

        /// <summary>端到端吞吐对比：同一张 2K 图，PNG→PNG，各自跑 3 次取最好。</summary>
        private static void BenchEndToEnd(bool isRelease)
        {
            var ff = AppSettingsService.Current.FfmpegPath;
            if (string.IsNullOrWhiteSpace(ff) || !File.Exists(ff)) { Console.WriteLine("  SKIP 端到端（无 ffmpeg）"); return; }
            var dir = Path.Combine(Path.GetTempPath(), "cmsbench");
            Directory.CreateDirectory(dir);
            string srcPng = Path.Combine(dir, "in.png");
            if (!File.Exists(srcPng) || new FileInfo(srcPng).Length == 0)
            {
                RunCli(ff, $"-y -hide_banner -loglevel error -f lavfi -i smptehdbars=s=2048x1080 -frames:v 1 \"{srcPng}\"");
                if (!File.Exists(srcPng)) { Check(false, "端到端基准图生成失败"); return; }
            }
            var src = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor.FromCicp("bt709", "iec61966-2-1", "sRGB");
            var dst = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor.FromCicp("smpte432", "iec61966-2-1", "P3");
            if (src == null || dst == null) return;
            var plan = FfmpegGui.Services.ColorMapping.ColorMappingEngine.Plan(src, dst,
                new FfmpegGui.Services.ColorMapping.PlanPolicy
                {
                    Format = FfmpegGui.Services.ColorMapping.ColorMappingEngine.CapsFor("png"),
                    AllowApproximateCurve = false,
                });
            var spec = Spec("bt709", "iec61966-2-1", "smpte432", "iec61966-2-1");

            double h2 = double.MaxValue, h1 = double.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                RunCli(ff, $"-y -hide_banner -loglevel error -i \"{srcPng}\" -vf \"format=gbrpf32le,zscale=pin=bt709:tin=iec61966-2-1:min=gbr:p=smpte432:t=iec61966-2-1:m=bt709,format=rgb48le\" \"{dir}/h2.png\"");
                sw.Stop(); h2 = Math.Min(h2, sw.Elapsed.TotalMilliseconds);
            }
            for (int i = 0; i < 3; i++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                    srcPng, Path.Combine(dir, "h1.png"), spec, plan, null, default, 1).GetAwaiter().GetResult();
                sw.Stop(); h1 = Math.Min(h1, sw.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"  端到端 2K PNG sRGB→P3： H2(zscale 单进程)={h2:F0}ms  H1(引擎 三进程+内存变换)={h1:F0}ms  比值={h1 / h2:F2}×");
            bool h1ok = File.Exists(Path.Combine(dir, "h1.png")) && new FileInfo(Path.Combine(dir, "h1.png")).Length > 0;
            Check(h1ok, "引擎端到端产出有效 PNG");
            if (isRelease) Check(h1 <= h2 * 2.0, $"端到端 H1 ≤ H2×2.0（实 {h1 / h2:F2}×；超出则调代价模型）");
        }

        private static void RunCli(string exe, string args)
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe, Arguments = args, RedirectStandardError = true, RedirectStandardOutput = true,
                UseShellExecute = false, CreateNoWindow = true,
            });
            p?.WaitForExit();
        }

        /// <summary>
        /// xcheck —— 引擎与外部参照（libzimg/zscale）对同一图做同一转换，逐像素比对。
        /// 这是“自研精度可信”的硬证据：PSNR(16bit) 与峰值 ΔE。
        /// 用法：ServiceProbe xcheck &lt;input&gt; &lt;srcPrim&gt; &lt;srcTrc&gt; &lt;dstPrim&gt; &lt;dstTrc&gt; [outDir]
        /// </summary>
        private static async System.Threading.Tasks.Task ProbeXcheck(string[] args)
        {
            if (args.Length < 6)
            {
                Console.WriteLine("usage: ServiceProbe xcheck <input> <srcPrim> <srcTrc> <dstPrim> <dstTrc> [outDir]");
                _fail++; return;
            }
            string input = args[1];
            var (w, h) = await FfmpegGui.Services.ColorMapping.RawColorPipeline.ProbeSizeAsync(input).ConfigureAwait(false);
            if (w <= 0) { Check(false, "无法探测尺寸"); return; }
            var outDir = args.Length >= 7 ? args[6] : Path.Combine(Path.GetTempPath(), "cmsx");
            Directory.CreateDirectory(outDir);
            var spec = Spec(args[2], args[3], args[4], args[5]);

            // A) 引擎路径
            var raw = await FfmpegGui.Services.ColorMapping.RawColorPipeline
                .DecodeToRgb48Async(input, w, h).ConfigureAwait(false);
            var raw2 = new byte[raw.Length];
            CK.TransformRgb48le(raw, raw2, spec, w * h, CK.BuildTables(spec.SrcCurve), CK.BuildTables(spec.DstCurve));
            string enginePng = Path.Combine(outDir, "engine.png");
            await FfmpegGui.Services.ColorMapping.RawColorPipeline.WriteRgb48ToPngAsync(
                raw2, w, h, enginePng, args[4], args[5], null).ConfigureAwait(false);

            // B) 参照路径（zimg 自己完成同样的 primaries/曲线转换）
            //    ⚠️ 必须像生产代码那样在 **-i 之前**声明输入色彩（-color_primaries/-color_trc）：
            //    源文件无 CICP 时（lavfi 产出的 PNG 为 gbr/unknown/unknown），只写 zscale 的 tin=
            //    会被当 unknown 处理（实测参照几乎不变，差仅 244/65535），导致“参照失效”而误判引擎错。
            string mTok = args[4] == "bt2020" ? "bt2020nc" : "bt709";
            string zimgPng = Path.Combine(outDir, "zimg.png");
            var ff = AppSettingsService.Current.FfmpegPath;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ff,
                Arguments = $"-y -hide_banner -loglevel error -color_primaries {args[2]} -color_trc {args[3]} -i \"{input}\" "
                  + $"-vf \"format=gbrpf32le,zscale=pin={args[2]}:tin={args[3]}:min=gbr:rin=pc:p={args[4]}:t={args[5]}:m={mTok}:r=pc,format=rgb48le\" \"{zimgPng}\"",
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                if (p == null) { Check(false, "无法启动 ffmpeg"); return; }
                string err = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await p.WaitForExitAsync().ConfigureAwait(false);
                if (p.ExitCode != 0) { Check(false, $"zscale 参照路径失败: {err.Trim()}"); return; }
            }

            // C) 比对：两路均写成 16bit PNG → 各自解回 rgb48 再比
            var a = await FfmpegGui.Services.ColorMapping.RawColorPipeline.DecodeToRgb48Async(enginePng, w, h).ConfigureAwait(false);
            var b = await FfmpegGui.Services.ColorMapping.RawColorPipeline.DecodeToRgb48Async(zimgPng, w, h).ConfigureAwait(false);
            double psnr = CM.Psnr16(a, b);
            int maxDiff = 0, deSamples = 0; double worstDeltaE = 0, sumDeltaE = 0;
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i + 6 <= n; i += 6)
            {
                int r1 = a[i] | (a[i + 1] << 8), g1 = a[i + 2] | (a[i + 3] << 8), b1 = a[i + 4] | (a[i + 5] << 8);
                int r2 = b[i] | (b[i + 1] << 8), g2 = b[i + 2] | (b[i + 3] << 8), b2 = b[i + 4] | (b[i + 5] << 8);
                maxDiff = Math.Max(maxDiff, Math.Max(Math.Abs(r1 - r2), Math.Max(Math.Abs(g1 - g2), Math.Abs(b1 - b2))));
                if ((i / 6) % 53 == 0)   // 抽样算 ΔE（8bit 口径，与业界一致）
                {
                    double de = CM.DeltaE76((byte)(r1 >> 8), (byte)(g1 >> 8), (byte)(b1 >> 8),
                                             (byte)(r2 >> 8), (byte)(g2 >> 8), (byte)(b2 >> 8));
                    worstDeltaE = Math.Max(worstDeltaE, de); sumDeltaE += de; deSamples++;
                }
            }
            Console.WriteLine($"xcheck {Path.GetFileName(input)} {args[2]}/{args[3]}→{args[4]}/{args[5]}  {w}x{h}");
            Console.WriteLine($"  PSNR(16bit)={psnr:F2}dB  maxDiff={maxDiff}/65535  ΔE max={worstDeltaE:F3} avg={(deSamples > 0 ? sumDeltaE / deSamples : 0):F4} (样本 {deSamples})");
            Check(psnr >= 55, $"引擎 vs zimg PSNR ≥ 55dB（实 {psnr:F1}）");
            Check(maxDiff <= 1500, $"最大单通道差异 ≤ 1500/65535（实 {maxDiff}）");
            Check(worstDeltaE <= 1.5, $"峰值 ΔE*ab ≤ 1.5（实 {worstDeltaE:F3}）");
        }

        /// <summary>行主序 3x3 乘法（仅探针内部用于互逆自洽校验）。</summary>
        private static double[] Mul9(double[] a, double[] b)
        {
            var o = new double[9];
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    o[r * 3 + c] = a[r * 3] * b[c] + a[r * 3 + 1] * b[3 + c] + a[r * 3 + 2] * b[6 + c];
            return o;
        }

        // ─────────────────────── gamut / matrix ───────────────────────

        /// <summary>gamut &lt;ultrahdr-jpg&gt; [期望token]：读底图 ICC 判定底图实际原色（验证底图色域声明）。</summary>
        private static void ProbeGamut(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: ServiceProbe gamut <jpeg> [expectedToken]"); _fail++; return; }
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

        // ────────────────────────────── icc ──────────────────────────────

        private static void ProbeIcc(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: ServiceProbe icc <色彩空间名>"); _fail++; return; }
            var spec = args[1];
            var s = ColorSpaceRegistry.Resolve(spec);
            Console.WriteLine($"spec={s?.Key ?? "(未识别)"} kind={s?.Kind.ToString() ?? "-"} " +
                $"out={s?.OutPrimaries}/{s?.OutTrc}/{s?.OutMatrix}");
            var icc = IccProfileService.ResolveNamedIcc(spec, null,
                s?.OutPrimaries ?? "bt709", s?.OutTrc ?? "iec61966-2-1", s?.OutMatrix ?? "bt709");
            Console.WriteLine($"icc={icc ?? "(null)"}");
            if (icc == null) { Check(false, "命名 ICC 解析成功"); return; }
            var b = File.ReadAllBytes(icc);
            var info = IccProfileService.ParseInfo(icc);
            Console.WriteLine($"iccSize={b.Length} version={info?.Version} class={info?.DeviceClass} cs={info?.ColorSpace} pcs={info?.Pcs} desc={info?.Description}");
            Check(IccProfileService.IsValidIccProfile(icc), "ICC 魔数有效");
            Check(!string.IsNullOrWhiteSpace(info?.Description), "desc 标签存在");
            foreach (var tag in new[] { "desc", "wtpt", "rXYZ", "gXYZ", "bXYZ", "rTRC", "gTRC", "bTRC", "chad" })
            {
                var (off, len) = IccProfileService.FindIccTag(b, tag);
                Console.WriteLine($"tag {tag}: offset={off} size={len}");
            }
            var (wo, wl) = IccProfileService.FindIccTag(b, "wtpt");
            Check(wo > 0 && wl == 20, "wtpt 为 20 字节 XYZ");
            if (wo > 0 && wl >= 20)
            {
                // XYZ = s15Fixed16 ×3 (大端, 值 = raw/65536)
                double X = ReadS15(b, (int)wo + 8), Y = ReadS15(b, (int)wo + 12), Z = ReadS15(b, (int)wo + 16);
                Console.WriteLine($"wtpt X={X:0.0000} Y={Y:0.0000} Z={Z:0.0000}");
                Check(Math.Abs(Y - 1.0) < 0.01, "wtpt Y ≈ 1.0 (PCS 归一)");
            }
        }

        private static double ReadS15(byte[] b, int o)
            => ((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]) / 65536.0;

        /// <summary>icc-dump &lt;file.icc&gt; [tag ...] —— 打印 ICC 头 + tag 表 + 指定 tag 摘要（校准参考/自检）。</summary>
        private static void ProbeIccDump(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: ServiceProbe icc-dump <file.icc> [tag ...]"); _fail++; return; }
            var path = args[1];
            var b = File.ReadAllBytes(path);
            var info = IccProfileService.ParseInfo(path);
            Console.WriteLine($"file={path} size={b.Length} declaredSize={(uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3])}");
            Console.WriteLine($"version={info?.Version} class={info?.DeviceClass} cs={info?.ColorSpace} pcs={info?.Pcs} desc={info?.Description}");
            Console.WriteLine($"creator={ReadAscii(b, 80, 4)} intent={ReadAscii(b, 64, 4)} magic={ReadAscii(b, 36, 4)}");
            uint tagCount = (uint)((b[128] << 24) | (b[129] << 16) | (b[130] << 8) | b[131]);
            Console.WriteLine($"tags={tagCount}");
            for (int i = 0; i < tagCount && 132 + i * 12 + 11 < b.Length; i++)
            {
                int e = 132 + i * 12;
                var sig = ReadAscii(b, e, 4);
                uint off = (uint)((b[e + 4] << 24) | (b[e + 5] << 16) | (b[e + 6] << 8) | b[e + 7]);
                uint len = (uint)((b[e + 8] << 24) | (b[e + 9] << 16) | (b[e + 10] << 8) | b[e + 11]);
                var type = off + 8 <= b.Length - 4 ? ReadAscii(b, (int)off, 4) : "????";
                Console.WriteLine($"  {sig} type={type} off={off} len={len}");
            }
            foreach (var tag in args.Skip(2))
            {
                var (off, len) = IccProfileService.FindIccTag(b, tag);
                if (off == 0 || len == 0 || off + len > b.Length) { Console.WriteLine($"{tag}: 未命中/越界"); continue; }
                var o = (int)off;
                var t = ReadAscii(b, o, 4);
                if (t == "para")
                {
                    int ft = (int)ReadBe16(b, o + 8);
                    // parametricCurveType: sig(4) + reserved(4) + functionType(2) + reserved(2) → 参数从 +12 起
                    Console.WriteLine($"{tag}: para type={ft} " + string.Join(" ", EnumerateS15(b, o + 12, ((int)len - 12) / 4)));
                }
                else if (t == "curv")
                {
                    uint n = ReadBe32(b, o + 8);
                    Console.WriteLine($"{tag}: curv count={n}" + (n > 0 && n <= 4 ? $" vals=[{string.Join(",", CurvVals(b, o + 12, (int)n))}]" : ""));
                }
                else if (t == "XYZ ")
                    Console.WriteLine($"{tag}: XYZ [{string.Join(", ", EnumerateS15(b, o + 8, 3))}]");
                else if (t == "mntr")
                    Console.WriteLine($"{tag}: XYZType [{string.Join(", ", EnumerateS15(b, o + 8, 9))}]");
                else if (t == "desc")
                {
                    int al = (int)ReadBe32(b, o + 8);
                    Console.WriteLine($"{tag}: desc \"{Encoding.ASCII.GetString(b, o + 12, Math.Max(0, al - 1))}\"");
                }
                else Console.WriteLine($"{tag}: type={t} len={len}");
            }
        }

        private static string ReadAscii(byte[] b, int o, int n)
            => o >= 0 && o + n <= b.Length ? Encoding.ASCII.GetString(b, o, n) : "????";
        private static int ReadBe16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
        private static uint ReadBe32(byte[] b, int o)
            => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        private static IEnumerable<string> EnumerateS15(byte[] b, int o, int count)
        {
            for (int k = 0; k < count && o + k * 4 + 3 < b.Length; k++)
                yield return (ReadBe32(b, o + k * 4) / 65536.0).ToString("0.0000");
        }
        private static IEnumerable<string> CurvVals(byte[] b, int o, int n)
        {
            for (int k = 0; k < n && o + k * 2 + 1 < b.Length; k++)
                yield return (((b[o + k * 2] << 8) | b[o + k * 2 + 1]) / 65535.0).ToString("0.0000");
        }
    }
}

