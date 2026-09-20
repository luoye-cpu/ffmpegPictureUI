using Avalonia;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FfmpegGui.Models;
using FfmpegGui.Services;

namespace FfmpegGui
{
    internal class Program
    {
        /// <summary>GPU 加速是否启用（由 BuildAvaloniaApp 读取 settings.json + --no-gpu 参数决定）</summary>
        public static bool IsGpuAccelerated { get; private set; } = false;

        [STAThread]
        public static void Main(string[] args)
        {
            // 启动时清理上次崩溃遗留的僵尸临时目录（超过 24 小时的旧缓存）
            try { Services.PlatformServices.CleanupZombieTempDirs(); } catch { }

            // 检查是否为 headless 模式
            var cliResult = CliParser.Parse(args);
            if (cliResult.IsHeadless || cliResult.ShowHelp || cliResult.ShowVersion)
            {
                ExitCode = RunHeadless(cliResult).GetAwaiter().GetResult();
                // 必须同时写进程退出码：Main 是 void（WinExe），只赋值静态属性时
                // 脚/CI 看到的永远是 0，无法察觉“有任务失败”（实测踩过）。
                System.Environment.ExitCode = ExitCode;
                return;
            }

            // ── 全局异常兜底（2026-09-16 新增）────────────────────────────
            // 背景：全仓 28 处 async void 事件处理器里只有约 5 个有 try，而此前
            // **没有任何全局兜底** ⇒ 任一 await 抛异常都会成为未处理异常，
            // 用户表现为「程序突然关闭」，无提示、无日志（最难排查的一类故障）。
            // 订阅两处：① Avalonia Dispatcher（UI 线程，可 Handled 阻止崩溃）；
            //          ② AppDomain / TaskScheduler（后台线程，无法恢复，仅记录）。
            // ⚠ 原则：**记录 + 出声**，不静默吞掉（可用性诚实原则）。
            InstallGlobalExceptionHandlers();

            BuildAvaloniaApp(args).StartWithClassicDesktopLifetime(args);
        }

        /// <summary>进程退出码（headless 模式使用）</summary>
        public static int ExitCode { get; private set; }

        private static bool _handlersInstalled;

        /// <summary>
        /// 安装全局异常兜底（幂等）。把「进程无声消失」降级为「日志留痕 + 控制台出声」。
        /// </summary>
        private static void InstallGlobalExceptionHandlers()
        {
            if (_handlersInstalled) return;
            _handlersInstalled = true;

            // ① UI 线程：Avalonia Dispatcher 的未处理异常。这是 async void 事件处理器
            //    抛异常时的落点，**可以阻止进程退出**。
            try
            {
                Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
                {
                    e.Handled = true;
                    ReportFatal("UI 线程", e.Exception);
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"⚠ 无法订阅 Dispatcher 异常（{ex.GetType().Name}）");
            }

            // ② 后台线程：无法恢复，只能记录（不阻止退出）
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                ReportFatal("后台线程", e.ExceptionObject as Exception);

            // ③ 未观察的 Task：记录并标记已观察
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                ReportFatal("未观察的 Task", e.Exception);
                e.SetObserved();
            };
        }

        /// <summary>
        /// 记录未处理异常：控制台 + 缓存目录下的 crash.log。
        /// GUI 下用户看不到控制台 ⇒ **必须落盘**，否则等于什么都没记。
        /// </summary>
        private static void ReportFatal(string where, Exception? ex)
        {
            try
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 未处理异常（{where}）: {ex}";
                Console.Error.WriteLine(line);
                var dir = Services.PlatformServices.GetTempDir();
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, "crash.log"), line + Environment.NewLine);
                }
            }
            catch { /* 兜底路径本身不得再抛 */ }
        }

        /// <summary>
        /// 读取用户 GPU 偏好（复用 AppSettingsService 单例，避免重复 I/O）。
        /// 优先级：--no-gpu 参数 > settings.json 中的 GpuAcceleration > 默认启用。
        /// </summary>
        private static bool ShouldEnableGpu(string[] args)
        {
            if (args.Any(a => a.Equals("--no-gpu", StringComparison.OrdinalIgnoreCase)))
                return false;

            try
            {
                // 复用 AppSettingsService 加载，避免和 App.OnFrameworkInitializationCompleted 重复读文件
                return Services.AppSettingsService.Current.GpuAcceleration;
            }
            catch { return true; }
        }

        /// <summary>读取用户选择的渲染后端（无效值一律回退 "auto"，绝不因配置写坏而拒绝启动）。</summary>
        private static string ReadRenderingMode()
        {
            try
            {
                var m = (Services.AppSettingsService.Current.RenderingMode ?? "auto").Trim().ToLowerInvariant();
                return m is "angle" or "vulkan" or "software" ? m : "auto";
            }
            catch { return "auto"; }
        }

        public static AppBuilder BuildAvaloniaApp(string[] args)
        {
            IsGpuAccelerated = ShouldEnableGpu(args);
            var builder = AppBuilder.Configure<App>()
                .UsePlatformDetect();

            // 渲染后端由「设置 → 渲染后端」决定（"手动选择 GPU"）：
            //   auto（默认，平台默认回退链）/ angle（Windows ANGLE→D3D11）/ vulkan / software
            // GpuAcceleration=false 与 RenderingMode="software" 等价，都走纯 CPU 渲染。
            var renderMode = ReadRenderingMode();
            if (IsGpuAccelerated && renderMode != "software")
            {
                if (OperatingSystem.IsWindows())
                {
                    // AngleEgl = ANGLE 将 OpenGL ES 转换为 Direct3D 11，实现 GPU 加速。
                    // 每种模式**都保留 Software 兜底**：GPU 初始化失败时不应直接起不来。
                    var order = renderMode switch
                    {
                        "angle" => new[] { Win32RenderingMode.AngleEgl, Win32RenderingMode.Software },
                        "vulkan" => new[] { Win32RenderingMode.Vulkan, Win32RenderingMode.Software },
                        _ => new[] { Win32RenderingMode.AngleEgl, Win32RenderingMode.Vulkan, Win32RenderingMode.Software }
                    };
                    builder.With(new Win32PlatformOptions { RenderingMode = order });
                }
                else if (OperatingSystem.IsLinux())
                {
                    // 为将来 Linux/ARM 迁移预留
                    var order = renderMode switch
                    {
                        "vulkan" => new[] { X11RenderingMode.Vulkan, X11RenderingMode.Software },
                        "angle" => new[] { X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software },
                        _ => new[] { X11RenderingMode.Vulkan, X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software }
                    };
                    builder.With(new X11PlatformOptions { RenderingMode = order });
                }
            }
            // else: 使用默认 CPU 软件渲染（GpuAcceleration == false 或 RenderingMode == "software"）

            return builder.LogToTrace();
        }

        /// <summary>
        /// 真实检测 ffmpeg 是否可用：配置的 FfmpegPath 存在，或能在系统 PATH 中找到。
        /// （FfmpegPath 是计算属性，永不返回 null，故不能以 != null 判定可用性）
        /// </summary>
        private static bool IsFfmpegAvailable()
        {
            try
            {
                var p = AppSettingsService.Current.FfmpegPath;
                if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                    return true;
                if (PlatformServices.TryFindInPath(PlatformServices.Ffmpeg, out _))
                    return true;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 无界面批处理模式入口
        /// </summary>
        private static async Task<int> RunHeadless(CliParser.Result opts)
        {
            // 显示帮助/版本
            if (opts.ShowHelp)
            {
                CliParser.PrintHelp();
                return 0;
            }
            if (opts.ShowVersion)
            {
                var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
                Console.WriteLine($"FFmpegPictureUI v{version}");
                return 0;
            }

            // HEIC/HEIF：**只解码、不编码**（工具链无 HEIF 写通路；HEVC 图片编码另涉专利授权）。
            // 不拦截时会透传给 ffmpeg 报“Unable to choose an output format”，对用户毫无信息量。
            if (Models.AppSettings.IsDecodeOnlyFormat(opts.Format))
            {
                Console.Error.WriteLine($"错误: 不支持的输出格式 '{opts.Format}' —— HEIC/HEIF 仅支持作为输入解码，不提供编码输出（避免 HEVC 图片编码的专利/授权问题）。");
                Console.Error.WriteLine("可选的等效输出：AVIF（同为 ISO 基媒体，可携 ICC/CICP）或 JPEG XL / WebP / PNG / TIFF。用 --help 查看完整列表。");
                return 2;
            }

            // JPEG + HDR 目标且未开 GainMap 的“显式拒绝”待接入：色彩选项在本行之后才由
            // CliParser.BuildJobSpecs 组装成 FfmpegOptions，在此处拿不到 ColorSpace/ColorTrc/JpegGainMap。
            // 在那之前，命令层仍会把 HDR 传递钳为 SDR（capTrc 行为），不会产出错标的 PQ JPEG。

            // 如果没有输入文件且不是显示帮助/版本，报错
            if (opts.InputPatterns.Length == 0)
            {
                Console.Error.WriteLine("错误: 未指定输入文件。使用 -i/--input 或位置参数指定。");
                Console.Error.WriteLine("使用 --help 查看用法。");
                return 2;
            }

            // 加载配置（支持 --settings 指定文件）
            if (!string.IsNullOrWhiteSpace(opts.SettingsFile))
            {
                AppSettingsService.Load(opts.SettingsFile);
            }
            else
            {
                AppSettingsService.Load();
            }
            // 读设置失败过去是 catch{} 静默：用户的全部设置被重置为默认值却毫无痕迹，
            // 于是一整批转换都在“看起来配置过”的默认参数下跑。必须出声。
            if (AppSettingsService.LastLoadIssue != null)
                Console.Error.WriteLine($"⚠ {AppSettingsService.LastLoadIssue}");

            // ── PLAN 便携包 ffmpeg 目录回填（与 GUI 启动路径一致）──
            // 此前 headless 缺少该步骤：当 settings 无 FfmpegDirectory 且 ffmpeg 不在系统 PATH 时，
            // FfmpegPath 退化为裸名 "ffmpeg.exe"，导致所有 ffmpeg 相关操作（含 cjxl/cjpegli 管道回退）启动失败。
            try
            {
                var planPath = PlatformServices.PlanFolderPath;
                if (planPath != null && string.IsNullOrWhiteSpace(AppSettingsService.Current.FfmpegDirectory))
                {
                    var ffmpegInPlan = PlatformServices.FindPlanSubDir(planPath, "ffmpeg-full");
                    if (ffmpegInPlan != null)
                    {
                        AppSettingsService.Current.FfmpegDirectory = ffmpegInPlan;
                        if (!AppSettingsService.Save())
                            Console.Error.WriteLine($"⚠ 设置未能落盘，本次 ffmpeg 目录回填只在当前进程有效：{AppSettingsService.LastSaveIssue}");
                    }
                }
            }
            catch { }

            // 预热外部工具检测（同步等待，避免后续并发竞争）
            Console.WriteLine("正在检测外部工具...");
            // `#26`：headless 侧把 exiftool 检测的取消/超时点名接到控制台（消息自带 "\n" ⇒ 用 Write）。
            ExternalToolsDetector.EnsureAllDetected(m => Console.Write(m));
            Console.WriteLine($"  ffmpeg: {(IsFfmpegAvailable() ? "✅" : "❌")}");
            Console.WriteLine($"  cjxl: {(CjxlService.IsAvailable ? "✅" : "❌")}");
            Console.WriteLine($"  cjpegli: {(CjpegliService.IsAvailable ? "✅" : "❌")}");
            Console.WriteLine($"  exiftool: {(ExifToolService.IsAvailable ? "✅" : "❌")}");
            Console.WriteLine($"  JxrEncApp: {(JxrService.IsAvailable ? "✅" : "❌")}");
            Console.WriteLine($"  dngtool: {(RawService.IsAvailable ? "✅" : "❌")}");

            // 构建队列项
            Console.WriteLine("正在准备任务队列...");
            List<QueueItem> items;
            try
            {
                items = CliParser.BuildJobSpecs(opts);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"错误: {ex.Message}");
                return 2;
            }

            Console.WriteLine($"找到 {items.Count} 个输入文件，开始处理...");

            // ── 可用性诚实（2026-09-16）：矩阵声明若在本管线不可用，必须出声 ──
            // 本管线的矩阵有两个角色：① `-i` 前的输入声明（需 swscale 能按该矩阵转换）；
            // ② `-i` 后的输出标注（需目标容器能承载）。实测 `bt2020c`/`ycgco`/`chroma-derived-*`/`ictcp`
            // 在角色①会令 swscale 报 "Impossible to convert between the formats"（整条命令 -40、零产物），
            // 故代码会**跳过**该声明；若不告知，用户会以为"选了矩阵就生效了"。
            var declaredMatrix = items.Count > 0 ? items[0].Options.ColorMatrix : null;
            if (!string.IsNullOrWhiteSpace(declaredMatrix)
                && ColorSpaceRegistry.ToFfmpegInputMatrixName(declaredMatrix) == null)
            {
                Console.Error.WriteLine(
                    $"⚠ 色彩矩阵「{declaredMatrix}」本管线不支持（swscale 无法按其做像素转换）"
                    + "⇒ 已忽略该输入声明；产物将按源/目标的实际矩阵标注。");
                Console.Error.WriteLine(
                    "  可用值：" + string.Join(" / ", ColorSpaceRegistry.FfmpegInputMatrixNames));
            }

            // ── 干跑模式：仅打印命令，不执行 ──
            if (opts.DryRun)
            {
                Console.WriteLine("\n=== Dry-run 模式：仅打印将执行的命令 ===");
                foreach (var item in items)
                {
                    var cmd = MainWindow.BuildQueueItemCommand(item);
                    Console.WriteLine($"[{Path.GetFileName(item.InputPath)} → {Path.GetFileName(item.OutputPath)}]");
                    Console.WriteLine(cmd);
                    Console.WriteLine();
                }
                return 0;
            }

            // 创建编排器和控制台观察者（观察者内部负责双写文件+控制台，支持 Text/JSONL）
            var observer = new ConsoleQueueObserver(opts.LogLevel, opts.LogFormat, opts.LogFile);
            observer.SetTotalCount(items.Count);

            var orchestrator = new DefaultQueueOrchestrator();
            orchestrator.ItemProgress += observer.OnItemProgress;
            orchestrator.ItemCompleted += observer.OnItemCompleted;
            orchestrator.QueueCompleted += observer.OnQueueCompleted;

            try
            {
                // 启动队列
                await orchestrator.StartAsync(items, opts.Concurrency);

                // 等待完成
                await orchestrator.WaitForCompletionAsync();

                // 检查是否有失败（失败数从 ItemCompleted 事件统计）
                var failed = items.Count(i => i.HasError || i.Status == "失败");
                return failed == 0 ? 0 : 1;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("\n已取消。");
                return 130; // SIGINT 风格
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"错误: {ex.Message}");
                return 1;
            }
            finally
            {
                observer.Dispose();
            }
        }
    }
}
