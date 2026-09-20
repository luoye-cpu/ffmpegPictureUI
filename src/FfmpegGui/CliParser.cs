using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FfmpegGui.Models;
using FfmpegGui.Services;

namespace FfmpegGui
{
    /// <summary>
    /// 命令行参数解析器 - 支持 headless 批处理模式
    /// </summary>
    public sealed class CliParser
    {
        /// <summary>
        /// 解析结果
        /// </summary>
        public sealed class Result
        {
            public bool IsHeadless { get; set; }
            public bool ShowHelp { get; set; }
            public bool ShowVersion { get; set; }

            // 输入输出
            public string[] InputPatterns { get; set; } = Array.Empty<string>();
            public string OutputDirectory { get; set; } = "";
            public bool PreserveInputStructure { get; set; } = false;

            // 编码参数
            public string Format { get; set; } = "jxl";
            public EncoderBackend EncoderBackend { get; set; } = EncoderBackend.Ffmpeg;
            /// <summary>用户是否通过 -e/--encoder 显式指定了后端。未指定时按目标格式推断，避免默认 Cjxl 导致非 JXL 格式被错编成 JXL。</summary>
            public bool EncoderBackendExplicit { get; set; } = false;
            public int Quality { get; set; } = 75;
            public int Concurrency { get; set; } = Environment.ProcessorCount;
            public bool AutoThreads { get; set; } = false;

            // 高级选项
            public string? SettingsFile { get; set; }
            public string? LogFile { get; set; }
            public LogLevel LogLevel { get; set; } = LogLevel.Info;
            public LogFormat LogFormat { get; set; } = LogFormat.Text;
            public bool DryRun { get; set; } = false;  // 仅打印命令，不执行
            public Dictionary<string, string> ExtraOptions { get; } = new();

            // 元数据
            public MetadataMode MetadataMode { get; set; } = MetadataMode.PreserveAll;
            // 包2-2b：GPS 删除改三态——null=用户未显式指定（不覆盖模型默认值）。
            // 此前 CLI 侧独立默认 true 与 FfmpegOptions.StripExifGps 形成两个真值，
            // 只改模型默认值不生效（变异实测 verify-metadata-privacy 仍全绿）。
            public bool? StripGps { get; set; }
            public bool StripTime { get; set; } = false;
            public bool StripCamera { get; set; } = false;
            public bool StripAllExif { get; set; } = false;
            public bool StripXmp { get; set; } = false;

            // 预设
            public string? PresetName { get; set; }
        }

        /// <summary>
        /// 解析命令行参数
        /// </summary>
        public static Result Parse(string[] args)
        {
            var result = new Result();

            if (args.Length == 0) return result;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];

                // 内联取值：`--key=value` 与 `--key value` **完全等效**。
                // ⚠ 只按**第一个** '=' 切分：取值里可能含 '='（Windows 路径 / base64 / query 串）。
                //   仅在长选项（`--`）上启用；短选项（-i/-o/...）保持原样。
                string? inlineValue = null;
                if (arg.StartsWith("--"))
                {
                    int eq = arg.IndexOf('=');
                    if (eq > 2)
                    {
                        inlineValue = arg.Substring(eq + 1);
                        arg = arg.Substring(0, eq);
                    }
                }

                // 取选项值：优先内联值；否则消费下一个参数（未内联时与旧行为逐字节一致）。
                string? TakeValue()
                {
                    if (inlineValue != null) return inlineValue;
                    if (i + 1 < args.Length) return args[++i];
                    return null;
                }

                switch (arg.ToLowerInvariant())
                {
                    // ── 帮助/版本 ──
                    case "--help":
                    case "-h":
                    case "/?":
                        result.ShowHelp = true;
                        break;
                    case "--version":
                    case "-v":
                        result.ShowVersion = true;
                        break;

                    // ── 运行模式 ──
                    case "--headless":
                        result.IsHeadless = true;
                        break;
                    case "--gui":
                        result.IsHeadless = false;
                        break;

                    // ── 输入输出 ──
                    case "-i":
                    case "--input":
                    {
                        var v = TakeValue();
                        if (v != null)
                            result.InputPatterns = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        break;
                    }
                    case "-o":
                    case "--output":
                    {
                        var v = TakeValue();
                        if (v != null)
                            result.OutputDirectory = v;
                        break;
                    }
                    case "--preserve-structure":
                        result.PreserveInputStructure = true;
                        break;

                    // ── 格式/编码器 ──
                    case "-f":
                    case "--format":
                    {
                        var v = TakeValue();
                        if (v != null)
                        {
                            var fmt = v.ToLowerInvariant();
                            // 包2-2a：jpegli 是编码器后端而非容器格式——此前原样保留会拼出
                            // ".jpegli" 非法扩展名，ffmpeg 报 -22 且无任何产物（实测）。
                            // 规范化为 jpg：用户意图即"用 jpegli 编 JPEG"，jpg 目标在
                            // cjpegli 可用时自动走 jpegli 编码（QueueProcessor 路由）。
                            if (fmt == "jpegli") fmt = "jpg";
                            result.Format = fmt;
                        }
                        break;
                    }
                    case "-e":
                    case "--encoder":
                    {
                        var v = TakeValue();
                        if (v != null)
                        {
                            if (!Enum.TryParse<EncoderBackend>(v, true, out var eb))
                                throw new ArgumentException($"未知编码器: {v}");
                            result.EncoderBackend = eb;
                            result.EncoderBackendExplicit = true;
                        }
                        break;
                    }
                    case "-q":
                    case "--quality":
                    {
                        // 取值解析失败**必须报错**（P1-E 统一口径，见 BadValue）：此前 int.TryParse
                        // 无 else ⇒ `--quality abc` 静默按默认 75 跑，用户以为设过了。
                        // 范围仍按旧行为钳制（1..100），不做拒绝 —— 钳制是**已文档化**的口径
                        // （--help 写明 1-100），与「解析失败」是两回事。
                        var v = TakeValue();
                        if (v != null)
                            result.Quality = Math.Clamp(ParseIntStrict("quality", v), 1, 100);
                        break;
                    }
                    case "-j":
                    case "--jobs":
                    case "--concurrency":
                    {
                        // 同上：`--jobs abc` 此前静默回落到 CPU 核数。
                        var v = TakeValue();
                        if (v != null)
                            result.Concurrency = Math.Max(1, ParseIntStrict("jobs", v));
                        break;
                    }
                    case "--auto-threads":
                        result.AutoThreads = true;
                        break;

                    // ── 元数据选项 ──
                    case "--strip-gps":
                        result.StripGps = true;
                        break;
                    case "--no-strip-gps":
                        result.StripGps = false;
                        break;
                    case "--strip-time":
                        result.StripTime = true;
                        break;
                    case "--strip-camera":
                        result.StripCamera = true;
                        break;
                    case "--strip-all-exif":
                        result.StripAllExif = true;
                        break;
                    case "--strip-xmp":
                        result.StripXmp = true;
                        break;
                    case "--strip-metadata":
                        result.MetadataMode = MetadataMode.StripAll;
                        break;
                    case "--preserve-metadata":
                        result.MetadataMode = MetadataMode.PreserveAll;
                        // “保留所有元数据”应真正保留全部：显式传入时清除各隐私删除标志（含默认开启的 GPS 删除）。
                        // 未显式传入时仍保留隐私默认（StripGps=true）。若其后又传 --strip-gps 等，则“最后一个标志生效”。
                        result.StripGps = false;
                        result.StripTime = false;
                        result.StripCamera = false;
                        result.StripAllExif = false;
                        result.StripXmp = false;
                        break;

                    // ── 日志/配置 ──
                    case "--settings":
                    {
                        var v = TakeValue();
                        if (v != null)
                            result.SettingsFile = v;
                        break;
                    }
                    case "--log-file":
                    {
                        var v = TakeValue();
                        if (v != null)
                            result.LogFile = v;
                        break;
                    }
                    case "--log-level":
                    {
                        // 同上：此前 Enum.TryParse 无 else ⇒ `--log-level Verbos` 静默保持 Info。
                        var v = TakeValue();
                        if (v != null)
                            result.LogLevel = ParseEnumStrict<LogLevel>("log-level", v, "Error | Warn | Info | Debug | Trace");
                        break;
                    }
                    case "--log-format":
                    {
                        var v = TakeValue();
                        if (v != null)
                            result.LogFormat = ParseEnumStrict<LogFormat>("log-format", v, "Text | JsonLines");
                        break;
                    }

                    // ── 干跑模式 ──
                    case "--dry-run":
                        result.DryRun = true;
                        break;

                    // ── 预设 ──
                    case "--preset":
                    {
                        var v = TakeValue();
                        if (v != null)
                            result.PresetName = v;
                        break;
                    }

                    // ── 扩展选项 (key=value 形式，透传给 FfmpegOptions) ──
                    case var a when a.StartsWith("--"):
                        var key = a[2..];
                        if (inlineValue != null)
                        {
                            // `--key=value`：取值已按**第一个** '=' 切出（可含 '='），与空格形式等效
                            result.ExtraOptions[key] = inlineValue;
                        }
                        else if (i + 1 < args.Length && !LooksLikeOption(args[i + 1]))
                        {
                            // ⚠ D4（2026-09-19 修）：判据此前是 `!args[i + 1].StartsWith("-")`，
                            //   于是 `--animation-loop -1` / `--threads -1` 的 `-1`（**合法取值**，
                            //   -1 在 AnimationLoop 里是"不循环"哨兵、在 JpegGainMapQuality 里是
                            //   "用默认"哨兵）**不被消费**，本键反被置为字面量 "true"（下面的 else），
                            //   随后 `int.Parse("true")` 抛 FormatException 被兜底 catch 吞掉
                            //   ⇒「设了 -1」与「什么都没设」在外部不可区分（实测）。
                            //   LooksLikeOption 把「负数/小数字面量」与「选项」区分开。
                            result.ExtraOptions[key] = args[++i];
                        }
                        else
                        {
                            result.ExtraOptions[key] = "true";
                        }
                        break;

                    // ── 位置参数（作为输入） ──
                    default:
                        if (!arg.StartsWith("-"))
                        {
                            result.InputPatterns = result.InputPatterns.Concat(new[] { arg }).ToArray();
                        }
                        break;
                }
            }

            return result;
        }

        /// <summary>
        /// 打印帮助信息
        /// </summary>
        public static void PrintHelp()
        {
            Console.WriteLine(@"
FFmpegPictureUI - 批量图片/动图/视频转换工具
==========================================

用法: FfmpegGui.exe [选项] [输入文件/目录...]

运行模式:
  --headless              无界面批处理模式（不显示 GUI）
  --gui                   启动图形界面（默认）

输入输出:
  -i, --input <paths>     输入文件/目录/通配符 (逗号分隔多个)
  -o, --output <dir>      输出目录 (默认: 同输入目录)
  --preserve-structure    保留输入目录结构到输出目录

编码参数:
  -f, --format <fmt>      输出格式 (jpg, png, webp, avif, jxl, jxr, tiff, gif, dng...)
  -e, --encoder <type>    编码器后端 (Ffmpeg, Cjxl, Cjpegli, Jxr, Dng)
  -q, --quality <1-100>   质量参数（**默认按格式**：png/apng=100, webp=95, jpg/jpegli=92, avif/jxl/gif=90；
                          引擎路径下若与格式默认值不同会被视为「用户显式指定」并如实承接）
  -j, --jobs, --concurrency <n>  并发任务数 (默认: CPU核心数)
  --auto-threads          自动分配每任务线程数 (避免超线程)

高级编码选项 (通过 --key=value 透传):
  --chroma <4:2:0|4:2:2|4:4:4|auto>
  --bit-depth <8|10|12|16>
  --color-space <sRGB|Display P3|BT.2020 PQ|...>
  --color-primaries <bt709|bt2020|...>
  --color-trc <linear|srgb|pq|hlg|...>
  --threads <n>           每任务 ffmpeg 线程数

色彩处理（策略与执行路径）:
  --color-strategy <recommended|carry|cicp-only|manual>
                          色彩策略（默认 recommended）；别名 auto/携带/仅cicp/手动
  --color-engine <auto|engine|legacy>
                          色彩执行路径（默认 auto）。auto=优先自研引擎、不可走时回退传统管线；
                          engine=显式要求引擎（不可走则失败并点名）；legacy=强制传统管线（A/B 与排查用）
  --color-tone-map <reinhard|hable|mobius|bt2446a>
                          HDR→SDR 色调映射算子（默认 none 即不映射；HDR 源转 SDR 容器时必需）
  --color-hdr-peak <nits> 源峰值亮度（未给则读容器静态元数据，仍无则整帧实测）
  --color-sdr-white <nits> SDR 参考白（默认 203）
  --color-sdr-peak <nits>  SDR 峰值
  --color-matrix <bt709|bt2020nc|bt470bg|smpte170m|gbr>
                          输入矩阵声明（影响 YUV→RGB 解读）
  --color-range <auto|tv|pc>   输出色彩范围（仅 YUV 输出格式有意义）
  --color-carry-scope <none|unnameable|all>
                          「推荐」策略的携带范围（默认 all：无损优先）
  --color-709-curve <std|zimg>  BT.709 编码口径（std=ITU-R 定义式；zimg=对齐历史产物）
  --color-allow-unlabeled <true|false>
                          允许在未标注语义不统一的容器（TIFF 类）产出无标注结果
  --color-gamut-map <off|on>
                          源色域超出目标时：off=钳位（硬裁切，默认）；on=GMO 色域压缩（去饱和，非裁切）
  --icc-file <path>       用外部 ICC 文件判定**源**色彩空间
  --icc-mode <mode>       旧参数，映射为 --color-strategy（兼容用）

元数据处理:
  --strip-gps             删除 GPS 信息 (默认: 开启)
  --no-strip-gps          保留 GPS 信息
  --strip-time            删除时间戳
  --strip-camera          删除相机信息
  --strip-all-exif        删除所有 EXIF
  --strip-xmp             删除 XMP 数据
  --strip-metadata        删除所有元数据
  --preserve-metadata     保留所有元数据（含 GPS；显式传入时覆盖默认 GPS 删除）

  注: 默认 MetadataMode=PreserveAll 但为隐私默认删除 GPS；如需保留 GPS 请用 --preserve-metadata 或 --no-strip-gps。

预设与配置:
  --preset <name>         使用内置/用户预设
  --settings <file>       指定配置文件路径
  --log-file <file>       日志输出文件
  --log-level <level>     日志级别 (Error|Warn|Info|Debug|Trace)
  --log-format <fmt>      日志格式 (Text|JsonLines)
  --dry-run               仅打印将执行的命令，不实际执行

其他:
  -h, --help              显示此帮助
  -v, --version           显示版本号

示例:
  # 批量转换目录下所有 RAW 为 JXL (无损)
  FfmpegGui.exe --headless -i D:\Photos -o D:\Converted -f jxl -e Cjxl --preserve-structure

  # 指定质量并发转为 AVIF
  FfmpegGui.exe --headless -i *.jpg -o out -f avif -q 85 -j 4

  # 使用预设
  FfmpegGui.exe --headless -i input.mov -o out -f jxl --preset ""JXL Lossless""

  # 启动 GUI
  FfmpegGui.exe
");
        }

        /// <summary>
        /// 根据 CLI 参数构建队列项列表
        /// </summary>
        public static List<QueueItem> BuildJobSpecs(Result opts)
        {
            var items = new List<QueueItem>();

            // 展开输入模式
            var inputFiles = new List<string>();
            foreach (var pattern in opts.InputPatterns)
            {
                ExpandInputPattern(pattern, inputFiles);
            }

            if (inputFiles.Count == 0)
            {
                throw new InvalidOperationException("未找到匹配的输入文件。请使用 -i/--input 指定。");
            }

            // 可用性诚实原则：目标格式依赖的外部编码器缺失时直接报错，
            // 不静默用 ffmpeg 兜底（ffmpeg 无 JXR 编码器、无法编码 DNG）。
            ValidateFormatToolAvailability(opts.Format);

            var baseOptions = BuildBaseOptions(opts);

            foreach (var file in inputFiles)
            {
                var outputPath = BuildOutputPath(file, opts);
                var options = baseOptions.DeepClone();
                options.Format = opts.Format;
                // 未显式指定 -e 时按目标格式推断合理后端，避免默认后端与格式不符导致错标文件
                options.EncoderBackend = opts.EncoderBackendExplicit
                    ? opts.EncoderBackend
                    : EncoderBackendCompat.InferDefaultBackend(opts.Format);

                var item = new QueueItem
                {
                    InputPath = file,
                    OutputPath = outputPath,
                    Options = options,
                    InputBaseDir = opts.PreserveInputStructure ? Path.GetDirectoryName(file) : null
                };

                items.Add(item);
            }

            return items;
        }

        /// <summary>
        /// 可用性诚实：校验目标格式所依赖的外部编码器是否可用；缺失则报错，
        /// 而非静默回退 ffmpeg（ffmpeg 无法真正编码 jxr/dng，回退只会产出 opaque 失败或错标文件）。
        /// 注：jxl 缺 cjxl 时 ffmpeg 内置 libjxl 仍可编码，故不在此强制（色彩场景另在 UI 告警）。
        /// </summary>
        private static void ValidateFormatToolAvailability(string format)
        {
            switch ((format ?? "").ToLowerInvariant())
            {
                case "jxr":
                    if (!JxrService.IsAvailable)
                        throw new InvalidOperationException(
                            "JXR 输出需要 JxrEncApp，但未检测到。请将 JxrEncApp 放入 PLAN/artifacts/（不回退 ffmpeg，ffmpeg 无 JXR 编码器）。");
                    break;
                case "dng":
                    if (!RawService.IsDngTool)
                        throw new InvalidOperationException(
                            "DNG 输出需要 dngtool，但未检测到。请将 dngtool 放入 PLAN/artifacts/（不回退 ffmpeg，ffmpeg 无法编码 DNG）。");
                    break;
            }
        }

        private static void ExpandInputPattern(string pattern, List<string> results)
        {
            // 处理目录、通配符、单文件
            if (Directory.Exists(pattern))
            {
                // 目录：递归查找支持的图片/视频文件
                var exts = AppSettings.AllImageFormats.Values.SelectMany(x => x).ToHashSet();
                exts.UnionWith(AppSettings.AllVideoFormats.Values.SelectMany(x => x));
                foreach (var ext in exts)
                {
                    results.AddRange(Directory.GetFiles(pattern, "*" + ext, SearchOption.AllDirectories));
                }
            }
            else if (File.Exists(pattern))
            {
                results.Add(pattern);
            }
            else
            {
                // 通配符
                var dir = Path.GetDirectoryName(pattern);
                var searchPattern = Path.GetFileName(pattern);
                if (string.IsNullOrEmpty(dir)) dir = ".";
                if (Directory.Exists(dir))
                {
                    results.AddRange(Directory.GetFiles(dir, searchPattern));
                }
            }
        }

        private static FfmpegOptions BuildBaseOptions(Result opts)
        {
            var options = new FfmpegOptions
            {
                Quality = opts.Quality,
                Threads = 0, // 0=auto
                AutoThreads = opts.AutoThreads,
                MetadataMode = opts.MetadataMode,
                StripExifGps = opts.StripGps ?? true,
                StripExifTime = opts.StripTime,
                StripExifCamera = opts.StripCamera,
                StripExifAll = opts.StripAllExif,
                StripXmp = opts.StripXmp
            };

            // 应用预设（如果指定）
            if (!string.IsNullOrWhiteSpace(opts.PresetName))
            {
                var preset = PresetManagerService.GetPresetByName(opts.PresetName);
                if (preset != null)
                {
                    options.ApplyPresetData(preset.Data);
                }
                else
                {
                    Console.WriteLine($"⚠️ 未找到预设: {opts.PresetName}");
                }
            }

            // 应用扩展选项
            foreach (var kv in opts.ExtraOptions)
            {
                ApplyExtraOption(options, kv.Key, kv.Value);
            }

            return options;
        }

        /// <summary>
        /// 把一条 <c>--key value</c> 形式的透传选项写进 <see cref="FfmpegOptions"/>。
        /// 可见性从 private 提到 internal（2026-09-19，P1-C）：IPC 侧
        /// <c>IpcService.HandleSubmitJob</c> 也要用这份映射，否则 IPC 与 CLI 会各有一套 key 语义。
        /// </summary>
        internal static void ApplyExtraOption(FfmpegOptions options, string key, string value)
        {
            // 简单映射常用字段，忽略不匹配的
            try
            {
                switch (key.ToLowerInvariant())
                {
                    case "chroma":
                        // ── 2026-09-19（裁决第 2 项：严格化）──
                        // 取值域 = GUI 候选（`MainWindow.xaml:355-360` 逐字 4 项）与组合矩阵轴登记
                        // （`_lib-axes.ps1:199`）**完全一致** ⇒ 封闭且本仓自持。
                        // 收口前实测两处静默：① `--chroma 4:4:0`（拼错）与 `4:2:0` 在下游
                        //   `ImageEncoderArgs.MapPixFmt` 里**同归 420**（只有 444/422 被识别）；
                        // ② 大小写：下游 `is "4:4:4"` 是**大小写敏感**的原串比较 ⇒ `--chroma AUTO`
                        //   此前静默等于 4:2:0（而不是 auto）。本函数统一 Trim+小写后归一，两处一并消掉。
                        // ⚠ `4:4:4` **必须放行**：`_lib-reject.ps1:624-626`/`:694-697` 要求
                        //   「CLI 接受 4:4:4、由**引擎**拒绝（webp 不支持）」⇒ 不得在 CLI 层提前拦。
                        options.Chroma = ParseTokenStrict(key, value,
                            "auto | 4:4:4 | 4:2:2 | 4:2:0", "auto", "4:4:4", "4:2:2", "4:2:0");
                        break;
                    case "bit-depth":
                        // ── D6（2026-09-19 定性 + 修）──
                        // **有意**的部分：「`auto` ≡ 未设置」是设计，不是缺陷。模型侧
                        // `FfmpegOptions.BitDepth == null` 的语义就是 auto（FfmpegOptions.cs:146-149
                        // 「null = auto（不指定，由编码器自行判断）」），UI 也把下拉的 auto 映射成
                        // null（MainWindow.xaml.cs:1351-1353 `!Equals("auto") && TryParse` ⇒ 否则 null）。
                        // ⇒ CLI 接受 auto 并落到 null 与 UI 逐字同源。
                        // **缺陷**的部分：此前是靠 `int.Parse("auto")` 抛 FormatException、被兜底
                        // catch 吞掉**碰巧**得到 null ⇒ 用户显式写的 `auto` 与拼错的值（如 banana）
                        // 在外部完全不可区分。现在 auto 走显式分支，其余解析失败报错。
                        options.BitDepth = string.Equals(value.Trim(), "auto", StringComparison.OrdinalIgnoreCase)
                            ? (int?)null
                            : ParseIntStrict(key, value);
                        break;
                    case "color-space": options.ColorSpace = value; break;
                    case "color-primaries": options.ColorPrimaries = value; break;
                    case "color-trc": options.ColorTrc = value; break;
                    case "color-matrix": options.ColorMatrix = value; break;
                    case "color-range":
                        // ── 2026-09-19（裁决第 2 项：严格化）──
                        // 取值域 = `FfmpegCommandBuilder.ResolveOutputColorRange`（`:938-945`）：只有 `tv` / `pc`
                        // 被识别，**其余一切**（含 `auto` 与拼错值）都落「跟随输入」同一分支 ⇒ 收口前
                        // `--color-range tvv`（拼错）与 `--color-range auto` 在外部**不可区分**。
                        // 轴登记（`_lib-axes.ps1:238-241`）与 `_lib-matrix.ps1:58` 都要求 `auto` 被接受
                        // （语义 = 「不下发 / 跟随输入」，等价物是**不设该轴**而非同轴另一取值）⇒ 白名单含 `auto`。
                        options.ColorRange = ParseTokenStrict(key, value, "auto | tv | pc", "auto", "tv", "pc");
                        break;
                    // 自研色彩引擎开关。**D5 同族**（2026-09-19 修）：它与 --color-strategy 同属
                    // `--help` 的「色彩处理（策略与执行路径）」一节，取值域在 help 里就是
                    // auto | engine | legacy 三项。此前是**自由字符串**：任何拼错的值（`--color-engine engin`）
                    // 都会落进 ColorEngineRouter 的 `!Equals("legacy")` 判据（ColorEngineRouter.cs:101/116）
                    // ⇒ 与 auto 等价 —— 用户以为指定了执行路径，实际按默认跑（同一类"看起来配置过"）。
                    // 注：三处比较全是 OrdinalIgnoreCase，故这里规范化为小写不改变语义。
                    case "color-engine":
                        options.ColorEngine = value.Trim().ToLowerInvariant() switch
                        {
                            "auto" => "auto",
                            "engine" => "engine",
                            "legacy" => "legacy",
                            _ => throw BadValue(key, value, "支持 auto | engine | legacy"),
                        };
                        break;
                    case "color-709-curve":
                        // ── 2026-09-19（裁决第 2 项：严格化）──
                        // 取值域 = `ColorIntentFactory.cs:180` 的**唯一**判据 `Equals("zimg", OrdinalIgnoreCase)`
                        // ⇒ 只有 `zimg` 与 `std` 两个语义；**其余一切**（含拼错值）静默落 `std`。
                        // 危害等级本表最高：该曲线决定 BT.709 的编码口径（`ColorIntent.cs:85` 记载 zscale 把
                        // `t=bt709` 实现为纯 γ1/2.4、与 `std` 的拟合 RMS 差 7e-5）⇒ 拼错即**像素值悄悄变了**，
                        // 且全程零告警。默认 `std`（`FfmpegOptions.cs:361`）与轴登记（std|zimg）一致。
                        options.Color709Curve = ParseTokenStrict(key, value, "std | zimg", "std", "zimg");
                        break;
                    case "color-carry-scope":
                        // ── 2026-09-19（裁决第 2 项：严格化）──
                        // 取值域 = `Enum.TryParse<CarryScope>`（`ColorIntentFactory.cs:179`）；枚举
                        // `ColorIntent.cs:74-82` = None(0) | Unnameable(1) | All(2)，默认 `all`
                        // （`FfmpegOptions.cs:363`）。`TryParse` **失败时静默保留默认值** ⇒ 拼错值
                        // （`--color-carry-scope alll`）与"什么都没设"在外部不可区分。
                        // ⚠ 必须**保留数字串 0|1|2**：`Enum.TryParse` 本来就接受数值形式，收窄时把既有
                        //   合法写法挡在门外就是引入回归。轴登记（none|unnameable|all）已被覆盖。
                        options.ColorCarryScope = ParseTokenStrict(key, value,
                            "none | unnameable | all (or 0 | 1 | 2)", "none", "unnameable", "all", "0", "1", "2");
                        break;
                    case "color-allow-unlabeled": options.ColorAllowUnlabeled = ParseBoolStrict(key, value); break;
                    case "color-gamut-map":
                        // 源色域超出目标：off=钳位硬裁切（默认，与历史行为逐字节相同）| on=GMO 压缩。
                        // ⚠ 取值非法**必须报错**：静默忽略会让用户以为开关生效了、实际按默认跑
                        //   （"看起来配置过"的假象）。这是本表的**既有正确口径**，2026-09-19 P1-E 起
                        //   已推广到全表（D5）：一切「可解析取值」的参数一律走 BadValue / *Strict。
                        options.ColorGamutMap = value.Trim().ToLowerInvariant() switch
                        {
                            "off" => "off",
                            "on" => "on",
                            _ => throw BadValue(key, value, "支持 off | on"),
                        };
                        break;
                    case "color-tone-map":
                        // ── 2026-09-19（裁决第 2 项：严格化）──
                        // 取值域 = `ColorIntentFactory.cs:184-204`（Trim+小写后 switch）：`""` / `none` /
                        // `reinhard` / `hable` / `mobius` / `bt2446a`；其 `default:` 分支对未知值**返 null**
                        // ⇒ 引擎不接手 ⇒ **静默回退传统管线**（策略悄悄换了执行路径，用户无从察觉）。
                        // ⚠ `bt2390` 必须**放行**：`:202` 把它判为「可识别但本管线不支持」并显式拒绝
                        //   ⇒ 属「**能识别**」而非「无法识别」，按本仓分层语义不得在 CLI 层提前拦。
                        // ⚠ 空串也放行：消费方写的是 `(o.ColorToneMap ?? "none")`，空串与 none 同义，
                        //   是既有合法写法（轴登记 none|reinhard|hable|mobius|bt2446a 已被覆盖）。
                        options.ColorToneMap = ParseTokenStrict(key, value,
                            "none | reinhard | hable | mobius | bt2446a (bt2390 recognized, unsupported)",
                            "", "none", "reinhard", "hable", "mobius", "bt2446a", "bt2390");
                        break;
                    // legacy 色彩链的 tonemap 曲线（BuildArguments 会读它，此前 CLI 无法设置）。
                    // 与 --color-tone-map（引擎路径）是两个不同入口，故引擎路由会明确不接受共存的歧义写法。
                    case "tonemap-curve": options.TonemapCurve = value; break;
                    case "color-hdr-peak": options.ColorHdrPeakNits = ParseDoubleStrict(key, value); break;
                    case "color-sdr-white": options.ColorSdrWhiteNits = ParseDoubleStrict(key, value); break;
                    case "color-sdr-peak": options.ColorSdrPeakNits = ParseDoubleStrict(key, value); break;
                    case "threads": options.Threads = ParseIntStrict(key, value); break;
                    case "lossless": options.Lossless = ParseBoolStrict(key, value); break;
                    case "jxl-effort": options.JxlEffort = ParseIntStrict(key, value); break;
                    case "jxl-modular": options.JxlModular = ParseBoolStrict(key, value); break;
                    case "jxl-lossless-jpeg": options.JxlLosslessJpeg = ParseBoolStrict(key, value); break;
                    case "cjxl-progressive": options.CjxlProgressive = ParseBoolStrict(key, value); break;
                    case "cjxl-photon-noise-iso": options.CjxlPhotonNoiseIso = ParseIntStrict(key, value); break;
                    case "cjxl-auto-photon-noise": options.CjxlAutoPhotonNoise = ParseBoolStrict(key, value); break;
                    case "jxl-preserve-ultrahdr": options.JxlPreserveUltrahdr = ParseBoolStrict(key, value); break;
                    case "webp-preset": options.WebpPreset = value; break;
                    case "webp-compression": options.WebpCompressionLevel = ParseIntStrict(key, value); break;
                    case "avif-cpu-used": options.AvifCpuUsed = ParseIntStrict(key, value); break;
                    case "avif-still-picture": options.AvifStillPicture = ParseBoolStrict(key, value); break;
                    case "avif-row-mt": options.AvifRowMt = ParseBoolStrict(key, value); break;
                    case "avif-tune": options.AvifTune = value; break;
                    case "png-pred": options.PngPred = value; break;
                    case "png-dpi": options.PngDpi = ParseIntStrict(key, value); break;
                    case "tiff-compression": options.TiffCompressionAlgo = value; break;
                    case "tiff-dpi": options.TiffDpi = ParseIntStrict(key, value); break;
                    case "jpeg-huffman":
                        // ── 2026-09-19（裁决第 2 项：严格化）──
                        // 取值域 = GUI 的两项（`MainWindow.xaml:683-685` = `default` | `optimal`），下游判据是
                        // `=="optimal" ? "1" : "0"`（`FfmpegCommandBuilder.cs:173-174`、`ImageEncoderArgs.cs:84-85`）
                        // ⇒ 实质是 **boolean**。
                        // ⚠ 顺带修一处**静默取反**：此前写 ffmpeg 原生的 `--jpeg-huffman 1`（= 开）会落进
                        //   `else` ⇒ 下发 `-huffman 0`（= 关），与用户意图**相反**且零告警。现把 1/0 纳入白名单
                        //   并统一归一成 `optimal`/`default` 再下发（`optimal`→1、`default`→0，与旧行为逐字相同）。
                        options.JpegHuffman = ParseTokenStrict(key, value,
                            "default | optimal (or 1 | 0)", "default", "optimal", "1", "0") switch
                        {
                            "optimal" or "1" => "optimal",
                            _ => "default",
                        };
                        break;
                    case "jpeg-dct": options.JpegDct = value; break;
                    case "jpeg-progressive": options.JpegProgressiveId = ParseIntStrict(key, value); break;
                    case "jpeg-gain-map": options.JpegGainMap = ParseBoolStrict(key, value); break;
                    // ⚠ -1 是**合法哨兵**（= 用默认 75，QueueProcessor.cs:1877-1878 `>= 0 ? v : 75`），
                    //   故这里只做「能不能解析成整数」，不做范围校验（把哨兵误判成非法会破坏合法用法）。
                    case "jpeg-gain-map-quality": options.JpegGainMapQuality = ParseIntStrict(key, value); break;
                    case "jpeg-gain-map-target-nits": options.JpegGainMapTargetNits = ParseIntStrict(key, value); break;
                    case "jpeg-gain-map-downsample": options.JpegGainMapDownsample = ParseIntStrict(key, value); break;
                    case "jpeg-gain-map-multi-channel": options.JpegGainMapMultiChannel = ParseBoolStrict(key, value); break;
                    case "jpeg-gain-map-base-gamut":
                        // 底图色域（两者均为 SDR 底，HDR 由增益图承载）：srgb(默认) | bt2020
                        options.JpegGainMapBaseGamut = value.Trim().ToLowerInvariant() switch
                        {
                            "srgb" or "s-r-g-b" or "bt709" => "srgb",
                            "bt2020" or "rec2020" or "bt2100" or "2020" => "bt2020",
                            _ => throw BadValue(key, value, "支持 srgb | bt2020"),
                        };
                        break;
                    case "cjpegli-chroma": options.CjpegliChromaSubsampling = value; break;
                    case "cjpegli-progressive": options.CjpegliProgressiveId = ParseIntStrict(key, value); break;
                    case "cjpegli-optimize": options.CjpegliOptimize = ParseBoolStrict(key, value); break;
                    case "cjpegli-adaptive-quant": options.CjpegliAdaptiveQuant = ParseBoolStrict(key, value); break;
                    case "cjpegli-backend": options.CjpegliEncoderBackend = value; break;
                    case "cjpegli-psnr": options.CjpegliPsnrTarget = ParseFloatStrict(key, value); break;
                    case "dng-compression": options.DngCompression = ParseIntStrict(key, value); break;
                    case "dng-jxl-quality": options.DngJxlQuality = ParseIntStrict(key, value); break;
                    case "dng-jxl-effort": options.DngJxlEffort = ParseIntStrict(key, value); break;
                    case "dng-linear": options.DngLinear = ParseBoolStrict(key, value); break;
                    case "dng-bit-depth": options.DngBitDepth = ParseIntStrict(key, value); break;
                    case "dng-highlight-mode": options.DngHighlightMode = ParseIntStrict(key, value); break;
                    case "animation-fps": options.AnimationFps = ParseIntStrict(key, value); break;
                    // ⚠ -1 是**合法哨兵**（不循环），0=无限，>0=指定次数（FfmpegOptions.cs:403）。
                    //   空格形式 `--animation-loop -1` 现在也能正确消费（D4 修复，见 Parse 里的 LooksLikeOption）。
                    case "animation-loop": options.AnimationLoop = ParseIntStrict(key, value); break;
                    case "animation-scale-w": options.AnimationScaleW = ParseIntStrict(key, value); break;
                    case "animation-duration": options.AnimationDuration = ParseDoubleStrict(key, value); break;
                    case "max-dimension-enabled": options.EnableMaxDimension = ParseBoolStrict(key, value); break;
                    case "max-dimension": options.MaxDimension = ParseIntStrict(key, value); break;
                    case "gif-palette": options.GifPaletteOptimize = ParseBoolStrict(key, value); break;
                    case "gif-dither": options.GifDither = ParseBoolStrict(key, value); break;
                    case "icc-mode":   // 兼容旧标志：映射为色彩策略
                        // ── D5（2026-09-19 修）──
                        // 此前是 `if (Enum.TryParse(...)) ...` **无 else** ⇒ 非法取值被静默忽略
                        // （不报错、不改退出码），而同一张表里的 --color-gamut-map 对非法取值**抛错**
                        // ⇒ 同族参数两套口径，用户/自动化脚本无法预知。现统一为抛错。
                        // ⚠ 4 个取值只产出 3 种策略（None 与 BakeToStandard 同归 Recommended，
                        //   ColorStrategy.cs:47-53），这是**有意**的等价类，不是缺陷（K5 已锁）。
                        if (!Enum.TryParse<IccMode>(value, true, out var icc))
                            throw BadValue(key, value, "支持 None | CarryIcc | BakeToStandard | BakeOnly");
                        options.ColorStrategy = ColorStrategyMapper.FromIccMode(icc);
                        break;
                    case "color-strategy":
                        // ── D5（2026-09-19 修）── 与 --color-gamut-map / --jpeg-gain-map-base-gamut
                        // **同一口径**：取值非法必须报错，不得静默忽略（否则用户以为策略生效了，
                        // 实际按默认 Recommended 跑）。
                        if (!ColorStrategyMapper.TryParse(value, out var cs))
                            throw BadValue(key, value,
                                "支持 recommended | carry | cicp-only | manual（别名 auto / 推荐 / 携带 / 仅cicp / 手动）");
                        options.ColorStrategy = cs;
                        break;
                    case "icc-file": options.IccFilePath = value; break;

                    // ── 未知选项不得静默忽略 ──
                    // 走到这里 ⇒ 该 `--key` 既不在显式 switch、也不在本映射表：用户拼错，或用了
                    // 不存在的选项。历史行为是**完全静默**（实测 `--totally-unknown-option=xyz`
                    // 退出码 0、零告警、产物照出），用户无从察觉"配置压根没生效"。
                    // ⚠ 只**告警**、不改退出码：无法证明没有调用方依赖静默忽略（2026-09-17 评审结论）。
                    default:
                        Console.WriteLine($"⚠️ 未知选项 --{key}（已忽略）");
                        break;
                }
            }
            catch (ArgumentException)
            {
                // **取值非法**必须上报，不得静默忽略：本分支放行 switch 里显式抛的取值校验
                // （--color-gamut-map / --jpeg-gain-map-base-gamut / color-engine / color-strategy /
                //   icc-mode / BadValue + 全部 *Strict 帮助函数）。静默吞掉会让用户以为选项生效了、
                // 实际按默认值跑 —— 正是本项目最忌讳的"看起来配置过"的假象。
                // 出口：IPC 侧捕获它转成 `Invalid payload: …`（IpcService.cs:363-371）；
                //       CLI 侧由 Program.RunHeadless 打印 `错误: …` 并返回退出码 2（Program.cs:282-286）。
                throw;
            }
            catch
            {
                // 兜底：仍不放过任何非 ArgumentException 的意外（例如未来新增分支里漏改的 Parse）。
                // ⚠ 2026-09-19 起 typed 取值解析已全部走 *Strict 帮助函数（抛 ArgumentException），
                //   本兜底在产品路径上**不再**承担「静默忽略取值错误」的职责。
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  取值解析失败的**统一口径**（2026-09-19，P1-E：D4 / D5 / D6）
        // ══════════════════════════════════════════════════════════════════════
        // 规则：**取值域有限、且解析器就在本文件手里**的参数，解析失败一律抛
        // `ArgumentException`，消息点名 `--key` 与收到的原值。为什么是这个异常类型：
        //   · 它是本仓**既有约定**：`-e/--encoder` 早已这么抛（Parse 里「未知编码器」）；
        //   · IPC 侧 `IpcService.HandleSubmitJob` 捕获它并转成 `Invalid payload: …`
        //     （IpcService.cs:363-371）—— 协议原生的报错通道；
        //   · CLI 侧 `Program.RunHeadless` 捕获后打印 `错误: …` 并返回退出码 2
        //     （Program.cs:282-286）。
        // ⇒ CLI / IPC 两个入口拿到的是**同一种**失败语义，不再「有的喊、有的吞」（D5）。
        //
        // 刻意**不**做的两件事（避免把合法用法误判成非法）：
        //   ① 不做**范围**校验。本仓大量使用哨兵值：`AnimationLoop = -1`（不循环）、
        //      `JpegGainMapQuality = -1`（用默认 75，QueueProcessor.cs:1877-1878）、
        //      `BitDepth = null`（= auto）、`AnimationScaleW = 0`（保持原始）。
        //      范围/哨兵语义属各下游组件，不在此收口 ⇒ `--bit-depth 0` 这类「能解析但语义可疑」
        //      的取值仍按旧行为下发，由下游/ffmpeg 响亮失败。
        //   ② 自由字符串型取值的收口是**逐项判定**的，不是一刀切（2026-09-19 维护者裁决第 2 项）。
        //      ✅ **已收口**（取值域封闭且由本仓自持 ⇒ 走 `ParseTokenStrict`，非法值报错）：
        //        chroma（auto|4:4:4|4:2:2|4:2:0）· color-range（auto|tv|pc）·
        //        color-709-curve（std|zimg）· color-carry-scope（none|unnameable|all|0|1|2）·
        //        color-tone-map（""|none|reinhard|hable|mobius|bt2446a|bt2390）·
        //        jpeg-huffman（default|optimal|1|0）
        //      ⛔ **刻意不收口**（收口就会把合法值挡在门外，分四类）：
        //        · 域在**下游工具**手里（本文件没有可信枚举，硬校验等于**编造**一张表）：
        //          webp-preset · png-pred · tiff-compression · jpeg-dct · cjpegli-chroma · tonemap-curve
        //        · **透传式**（本仓只有别名归一表，其余原样给 ffmpeg ⇒ 无封闭域）：
        //          color-primaries · color-trc
        //        · **关键词式**（`ColorSpaceRegistry.Resolve` 是关键词匹配，`bt2100` / `romm` /
        //          `iec61966` 等都能命中 ⇒ 任何固定白名单都会误伤；且 `auto`/`default` 与
        //          "无法识别"**同归 null**，当前无法区分）：color-space
        //        · **路径型**（无"无法识别"概念；加存在性校验会把非法路径从"静默回退"变成硬失败，
        //          与 `verify-color-wiring.ps1:254-261` 的既有断言**正面冲突** ⇒ 属**行为变更**，另议）：
        //          icc-file
        //      ⚠ 另记：`cjpegli-backend` 是**死选项**（全仓无消费者，仅 `FfmpegOptions.cs:309` 的声明
        //        + GUI 的读写）⇒ 用户设了不会有任何效果。属"看起来配置过"的假象，待定处置。
        //      ⚠ **分层语义（收口时最容易踩的坑）**：`bt2390`（tone-map）· `ycgco`/`bt2020c`（matrix）·
        //        `4:4:4` + webp（chroma）都是「**能识别但本管线不支持**」⇒ 必须**放行**、由引擎层
        //        响亮拒绝，**不得**在 CLI 层提前拦（`_lib-reject.ps1:624-626` / `:694-697` 与
        //        `verify-color-token-normalize.ps1:203-207` 就是钉住这条的既有断言）。

        /// <summary>构造「取值非法」的异常（统一文案，点名 key 与原值）。</summary>
        /// <remarks>
        /// WARNING: <paramref name="value"/> is declared <c>string?</c> (not <c>string</c>) on purpose:
        /// this helper only formats the message (null renders as empty), and every *Strict caller already
        /// treats it as possibly-null (see <see cref="ParseTokenStrict"/>'s <c>value ?? ""</c>), so a
        /// non-nullable annotation would contradict the callers' semantics.
        /// WARNING: declaring it <c>string</c> triggers CS8604 -- after <c>x ?? y</c> on a NON-nullable
        /// reference type, Roslyn marks <c>x</c> as maybe-null, so passing it to a non-nullable parameter
        /// warns. Verified by an isolated minimal repro: removing only <c>??</c> clears the warning.
        /// See COLOR_TRAPS section A.
        /// </remarks>
        private static ArgumentException BadValue(string key, string? value, string expected)
            => new ArgumentException($"--{key} 取值无效: '{value}'（{expected}）");

        private static int ParseIntStrict(string key, string value)
        {
            if (!int.TryParse(value, System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out var v))
                throw BadValue(key, value, "需要整数");
            return v;
        }

        private static double ParseDoubleStrict(string key, string value)
        {
            if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out var v))
                throw BadValue(key, value, "需要数值");
            return v;
        }

        private static float ParseFloatStrict(string key, string value)
        {
            if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var v))
                throw BadValue(key, value, "需要数值");
            return v;
        }

        private static bool ParseBoolStrict(string key, string value)
        {
            if (!bool.TryParse(value, out var v))
                throw BadValue(key, value, "需要 true | false");
            return v;
        }

        private static T ParseEnumStrict<T>(string key, string value, string expected) where T : struct, Enum
        {
            if (!Enum.TryParse<T>(value, true, out var v))
                throw BadValue(key, value, expected);
            return v;
        }

        /// <summary>
        /// 校验「**取值域封闭、且该域由本仓自持**」的字符串型选项（2026-09-19，维护者裁决第 2 项：严格化）。
        ///
        /// <para>
        /// 为什么需要它：本表里还有一批字符串选项直接 `options.X = value;` —— 拼错的值与合法值在外部
        /// **不可区分**，用户以为选项生效了、实际按默认跑（本项目最忌讳的"看起来配置过"的假象）。
        /// 例如 `--color-709-curve zimg`（本意：用 zimg 口径）拼成 `zimg2` 会被静默当成 `std` ⇒
        /// **像素值悄悄变了**；`--jpeg-huffman 1`（ffmpeg 原生"开"）被下游 `=="optimal" ? "1" : "0"`
        /// 静默映射成 `-huffman 0`（"关"）⇒ 与用户意图**相反**。
        /// </para>
        /// <para>
        /// ⚠ **只收口「域在本仓手里」的那一批**。刻意**不**收口的三类（收口会把合法值挡在门外）：
        /// <list type="number">
        ///   <item>域在**下游 ffmpeg / libwebp / cjpegli** 手里的（`--webp-preset` / `--png-pred` /
        ///     `--tiff-compression` / `--jpeg-dct` / `--cjpegli-chroma` / `--tonemap-curve`）——
        ///     本文件没有可信的完整枚举，硬校验就是**编造**一个表。</item>
        ///   <item>**透传式**（`--color-primaries` / `--color-trc`）：本仓只有一张**别名归一表**，
        ///     其余值原样透传给 ffmpeg ⇒ 无封闭域。</item>
        ///   <item>**关键词式**（`--color-space`）：`ColorSpaceRegistry.Resolve` 是**关键词匹配**
        ///     （`bt2100` / `romm` / `iec61966` 等都能命中）⇒ 任何固定白名单都会误伤，
        ///     且 `auto`/`default` 与"无法识别"**同归 null**，当前无法区分。</item>
        /// </list>
        /// </para>
        /// <para>
        /// ⚠ **分层语义**：本函数只拒「**无法识别**」的 token。「**能识别但本管线不支持**」的 token
        /// （如 `--color-tone-map bt2390`、`--color-matrix ycgco`、`--chroma 4:4:4` + webp）必须
        /// **放行**，由引擎层响亮拒绝 —— 这是本仓既有的分层约定，别在 CLI 层提前拦。
        /// </para>
        /// <para>
        /// ⚠ `expected` 一律传**纯 ASCII**（`"auto | 4:4:4 | 4:2:2 | 4:2:0"`）。原因：`_probe-cjk-hardcode-scan`
        /// 的判据项 `CsUi` 是 **fail-closed 且余量 0**，而本函数的调用行**不是**诊断 sink ⇒ 加中文会立刻
        /// 让该门禁转红。中文语义已由 <see cref="BadValue"/> 的模板承载，信息量不变。**请勿"顺手改成中文"。**
        /// </para>
        /// </summary>
        private static string ParseTokenStrict(string key, string value, string expected, params string[] allowed)
        {
            var v = (value ?? "").Trim().ToLowerInvariant();
            foreach (var a in allowed)
                if (string.Equals(v, a, StringComparison.Ordinal)) return a;
            throw BadValue(key, value, expected);
        }

        /// <summary>
        /// 下一个命令行 token 是否「像选项」——决定 <c>--key value</c> 的空格形式要不要消费它。
        /// ⚠ 判据**不能**是简单的 <c>StartsWith("-")</c>（旧实现，D4）：`--animation-loop -1`
        ///   与 `--threads -1` 的 `-1` 是**合法取值**（-1 在 AnimationLoop 里是"不循环"哨兵、
        ///   在 JpegGainMapQuality 里是"用默认"哨兵），旧判据不消费它，本键反被置为字面量
        ///   "true"，随后 <c>int.Parse("true")</c> 抛 FormatException 被兜底 catch 吞掉
        ///   ⇒「设了 -1」与「什么都没设」在外部不可区分（实测）。
        /// 规则：以 '-' 开头**且不是**负数/小数字面量（-1 / -1.5 / -.5）⇒ 当选项；
        ///   否则（含 `-1`、`-.5`、以及一切不以 '-' 开头的串）⇒ 当取值。
        /// </summary>
        private static bool LooksLikeOption(string token)
        {
            if (string.IsNullOrEmpty(token) || token[0] != '-') return false;
            if (token.Length == 1) return true;              // 单独一个 "-"
            var c = token[1];
            if (c == '-') return true;                       // "--xxx"
            return !(char.IsDigit(c) || c == '.');           // "-1"/"-.5" ⇒ 取值；"-abc" ⇒ 选项
        }

        private static string BuildOutputPath(string inputFile, Result opts)
        {
            var ext = "." + opts.Format.ToLowerInvariant();
            var fileName = Path.GetFileNameWithoutExtension(inputFile) + ext;

            if (string.IsNullOrWhiteSpace(opts.OutputDirectory))
            {
                return Path.Combine(Path.GetDirectoryName(inputFile) ?? ".", fileName);
            }

            if (opts.PreserveInputStructure)
            {
                // 简单实现：仅用文件名，目录结构由 QueueProcessor.Start 时处理
                return Path.Combine(opts.OutputDirectory, fileName);
            }

            return Path.Combine(opts.OutputDirectory, fileName);
        }
    }
}