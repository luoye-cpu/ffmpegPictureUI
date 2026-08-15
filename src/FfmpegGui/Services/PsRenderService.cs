using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
#if WINDOWS
using Microsoft.Win32;
#endif

namespace FfmpegGui.Services
{
    /// <summary>PS 打开 DNG 的验证结果</summary>
    public class PsOpenResult
    {
        public bool Opened { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string? Bits { get; set; }
        public string? Error { get; set; }
        public override string ToString() =>
            Opened ? $"OPEN_OK {Width}x{Height} bits={Bits}" : $"OPEN_FAIL: {Error}";
    }

    /// <summary>
    /// Photoshop 自动化验证服务。
    /// 通过 ExtendScript (jsx) 调用本地 Photoshop (PS 2026 等) 打开 DNG/图片并导出，
    /// 用于验证 dngtool 输出的 ACR 兼容性（PS/ACR 是 RAW 渲染的黄金标准）。
    ///
    /// 依赖: 本地安装 Adobe Photoshop（检测: 注册表 + 常见路径）。
    /// 注意: PS 进程启动/退出较慢 (10-30s)，调用方应使用后台任务。
    /// </summary>
    public static class PsRenderService
    {
        /// <summary>默认查找的 PS 版本目录（新→旧）</summary>
        private static readonly string[] PsVersionNames =
        {
            "Adobe Photoshop 2026", "Adobe Photoshop 2025", "Adobe Photoshop 2024",
            "Adobe Photoshop 2023", "Adobe Photoshop 2022", "Adobe Photoshop 2021",
            "Adobe Photoshop 2020", "Adobe Photoshop CS6 (64 Bit)"
        };

        private static string? _detectedPath;
        private static bool _detected;

        public static bool IsAvailable => Detect();

        public static string? DetectedPath => _detectedPath;

        public static bool Detect()
        {
            if (_detected) return _detectedPath != null;
            _detected = true;

            // ① 用户手动指定
            var manual = AppSettingsService.Current.PhotoshopPath;
            if (!string.IsNullOrWhiteSpace(manual) && File.Exists(manual))
            {
                _detectedPath = manual;
                return true;
            }

            // ② 注册表（Adobe 安装信息，仅 Windows）
#if WINDOWS
            if (OperatingSystem.IsWindows())
            {
                DetectFromRegistry();
                if (_detectedPath != null) return true;
            }
#endif

            // ③ 常见安装路径
            foreach (var version in PsVersionNames)
            {
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                foreach (var baseDir in new[] { pf, x86 })
                {
                    if (string.IsNullOrWhiteSpace(baseDir)) continue;
                    var candidate = Path.Combine(baseDir, "Adobe", version, "Photoshop.exe");
                    if (File.Exists(candidate))
                    {
                        _detectedPath = candidate;
                        return true;
                    }
                }
            }
            return false;
        }

        public static void ClearCache()
        {
            _detected = false;
            _detectedPath = null;
        }

#if WINDOWS
        /// <summary>从注册表检测 Photoshop 安装路径（仅 Windows 编译）</summary>
        [SupportedOSPlatform("windows")]
        private static void DetectFromRegistry()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Adobe\Photoshop");
                if (key != null)
                {
                    foreach (var sub in key.GetSubKeyNames())
                    {
                        using var appKey = key.OpenSubKey(sub);
                        var path = appKey?.GetValue("ApplicationPath") as string;
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            var exe = Path.Combine(path, "Photoshop.exe");
                            if (File.Exists(exe))
                            {
                                _detectedPath = exe;
                                return;
                            }
                        }
                    }
                }
            }
            catch { }
        }
#endif

        /// <summary>
        /// 清理残留的 Photoshop 进程。
        /// ⚠️ PS 的 app.quit() 不终止进程（脚本引擎退出但进程驻留），
        ///    残留实例会导致后续脚本调用连接旧实例而排队超时。
        ///    此服务用于无人值守自动化验证，直接清理所有 Photoshop.exe。
        /// </summary>
        private static void KillExistingPhotoshop()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("Photoshop"))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                }
                // 等待进程完全退出
                for (int i = 0; i < 20; i++)
                {
                    if (Process.GetProcessesByName("Photoshop").Length == 0) break;
                    Thread.Sleep(200);
                }
            }
            catch { }
        }

        /// <summary>
        /// 用 PS 打开 DNG/图片，验证 ACR 兼容性。
        /// 生成临时 jsx 脚本 → 启动 Photoshop.exe 执行 → 读取结果文件。
        /// ⚠️ PS 的 app.quit() 不会终止进程；残留实例会导致新脚本连接旧实例排队超时，
        ///    因此调用前会先清理残留的 Photoshop 进程。
        /// </summary>
        /// <param name="imagePath">DNG/RAW/图片路径</param>
        /// <param name="log">日志回调</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="timeoutSeconds">PS 启动+打开超时（默认 120s）</param>
        /// <returns>打开结果</returns>
        public static async Task<PsOpenResult> OpenInPhotoshopAsync(
            string imagePath, Action<string>? log = null,
            CancellationToken ct = default, int timeoutSeconds = 120)
        {
            var result = new PsOpenResult();
            if (!Detect())
            {
                result.Error = "未检测到 Photoshop";
                log?.Invoke("[ps] ⚠️ 未检测到 Photoshop\n");
                return result;
            }
            if (!File.Exists(imagePath))
            {
                result.Error = "文件不存在";
                return result;
            }

            // ⚠️ 清理残留 PS 进程（app.quit() 不终止进程，残留实例阻塞新脚本）
            KillExistingPhotoshop();

            // 2026-08-16 修复: 统一使用 PlatformServices.GetTempDir()（优先用户缓存目录），
            // 此前硬编码 Path.GetTempPath() 绕过用户 CacheDirectory 设置
            var scriptDir = Path.Combine(PlatformServices.GetTempDir(), "ffmpeg_ps_check");
            Directory.CreateDirectory(scriptDir);
            var scriptPath = Path.Combine(scriptDir, $"ps_open_{Guid.NewGuid():N}.jsx");
            var resultPath = Path.Combine(scriptDir, $"ps_open_{Guid.NewGuid():N}.txt");

            try
            {
                // ── 生成 ExtendScript ──
                var fsPath = imagePath.Replace("\\", "/");
                var fsResult = resultPath.Replace("\\", "/");
                var jsx = $@"#target photoshop
var f = new File(""{fsPath}"");
var log = new File(""{fsResult}"");
log.encoding = ""UTF-8"";
log.open(""w"");
log.writeln(""fsName="" + f.fsName + "" exists="" + f.exists);
if (!f.exists) {{ log.writeln(""OPEN_FAIL: not found""); log.close(); app.quit(); }}
try {{
    var doc = app.open(f);
    if (!doc) {{ log.writeln(""OPEN_FAIL: null""); log.close(); app.quit(); }}
    log.writeln(""OPEN_OK "" + doc.width.value + ""x"" + doc.height.value + "" bits="" + doc.bitsPerChannel);
    doc.close(SaveOptions.DONOTSAVECHANGES);
}} catch (e) {{
    log.writeln(""OPEN_FAIL: "" + e.message);
}}
log.writeln(""DONE"");
log.close();
app.quit();
";
                File.WriteAllText(scriptPath, jsx, new UTF8Encoding(true));

                log?.Invoke($"[ps] 调用 Photoshop 打开: {Path.GetFileName(imagePath)}\n");
                log?.Invoke($"[ps] 命令: {_detectedPath} {scriptPath}\n");

                // ── 启动 PS（阻塞等待脚本完成，后台执行）──
                var psi = new ProcessStartInfo
                {
                    FileName = _detectedPath!,
                    Arguments = $"\"{scriptPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null)
                {
                    result.Error = "PS 启动失败";
                    return result;
                }
                PlatformServices.SetSafePriority(p, AppSettingsService.Current.FfmpegPriority);

                // ── 等待结果文件写入完成（含 DONE 标记；仅 Exists 会读到 0 字节半成品）──
                var sw = Stopwatch.StartNew();
                var resultReady = false;
                while (!resultReady)
                {
                    if (sw.Elapsed.TotalSeconds > timeoutSeconds)
                    {
                        try { p.Kill(entireProcessTree: true); } catch { }
                        result.Error = $"PS 超时 ({timeoutSeconds}s)";
                        log?.Invoke("[ps] ⚠️ PS 超时，已终止\n");
                        return result;
                    }
                    if (File.Exists(resultPath))
                    {
                        try
                        {
                            if (File.ReadAllText(resultPath).Contains("DONE", StringComparison.Ordinal))
                                resultReady = true;
                        }
                        catch { /* 文件被占用，稍后重试 */ }
                    }
                    if (!resultReady)
                        await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                // PS 进程退出（脚本 app.quit()）
                try { await p.WaitForExitAsync(ct).ConfigureAwait(false); } catch { }

                // ── 解析结果 ──
                var lines = File.ReadAllLines(resultPath);
                foreach (var line in lines)
                {
                    if (line.StartsWith("OPEN_OK ", StringComparison.Ordinal))
                    {
                        result.Opened = true;
                        var parts = line.Substring(8).Split(new[] { 'x', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            if (int.TryParse(parts[0], out var w)) result.Width = w;
                            if (int.TryParse(parts[1], out var h)) result.Height = h;
                        }
                        var bitsIdx = line.IndexOf("bits=", StringComparison.Ordinal);
                        if (bitsIdx >= 0)
                            result.Bits = line.Substring(bitsIdx + 5);
                    }
                    else if (line.StartsWith("OPEN_FAIL", StringComparison.Ordinal))
                    {
                        result.Error = line.Substring("OPEN_FAIL".Length).Trim();
                    }
                }
                log?.Invoke($"[ps] 结果: {result}\n");
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Error = "已取消";
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
            finally
            {
                // 清理脚本（保留结果文件便于诊断）
                try { File.Delete(scriptPath); } catch { }
            }
        }

        /// <summary>
        /// 用 PS 渲染 DNG → PNG（ACR 渲染参考），返回 PNG 路径。
        /// 用于与 dngtool 去马赛克输出做对比验证。
        /// </summary>
        /// <param name="dngPath">DNG 路径</param>
        /// <param name="outPngPath">输出 PNG 路径</param>
        /// <param name="log">日志回调</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="timeoutSeconds">超时（默认 180s，含渲染）</param>
        public static async Task<PsOpenResult> RenderDngToPngAsync(
            string dngPath, string outPngPath, Action<string>? log = null,
            CancellationToken ct = default, int timeoutSeconds = 180)
        {
            var result = new PsOpenResult();
            if (!Detect())
            {
                result.Error = "未检测到 Photoshop";
                return result;
            }
            if (!File.Exists(dngPath))
            {
                result.Error = "DNG 不存在";
                return result;
            }

            // ⚠️ 清理残留 PS 进程（app.quit() 不终止进程，残留实例阻塞新脚本）
            KillExistingPhotoshop();

            // 2026-08-16 修复: 统一使用 PlatformServices.GetTempDir()（优先用户缓存目录）
            var scriptDir = Path.Combine(PlatformServices.GetTempDir(), "ffmpeg_ps_check");
            Directory.CreateDirectory(scriptDir);
            var scriptPath = Path.Combine(scriptDir, $"ps_render_{Guid.NewGuid():N}.jsx");
            var resultPath = Path.Combine(scriptDir, $"ps_render_{Guid.NewGuid():N}.txt");

            try
            {
                var fsIn = dngPath.Replace("\\", "/");
                var fsOut = outPngPath.Replace("\\", "/");
                var fsResult = resultPath.Replace("\\", "/");
                var jsx = $@"#target photoshop
var f = new File(""{fsIn}"");
var log = new File(""{fsResult}"");
log.encoding = ""UTF-8"";
log.open(""w"");
log.writeln(""fsName="" + f.fsName + "" exists="" + f.exists);
if (!f.exists) {{ log.writeln(""OPEN_FAIL: not found""); log.close(); app.quit(); }}
try {{
    var doc = app.open(f);
    if (!doc) {{ log.writeln(""OPEN_FAIL: null""); log.close(); app.quit(); }}
    log.writeln(""OPEN_OK "" + doc.width.value + ""x"" + doc.height.value + "" bits="" + doc.bitsPerChannel);
    // 转 8-bit 并导出 PNG
    doc.bitsPerChannel = BitsPerChannelType.EIGHT;
    var pngOpts = new PNGSaveOptions();
    pngOpts.compression = 6;
    var outFile = new File(""{fsOut}"");
    doc.saveAs(outFile, pngOpts, true, Extension.LOWERCASE);
    log.writeln(""SAVED "" + outFile.fsName);
    doc.close(SaveOptions.DONOTSAVECHANGES);
}} catch (e) {{
    log.writeln(""OPEN_FAIL: "" + e.message);
}}
log.writeln(""DONE"");
log.close();
app.quit();
";
                File.WriteAllText(scriptPath, jsx, new UTF8Encoding(true));
                log?.Invoke($"[ps] PS 渲染 DNG→PNG: {Path.GetFileName(dngPath)}\n");

                var psi = new ProcessStartInfo
                {
                    FileName = _detectedPath!,
                    Arguments = $"\"{scriptPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) { result.Error = "PS 启动失败"; return result; }
                PlatformServices.SetSafePriority(p, AppSettingsService.Current.FfmpegPriority);

                // ── 等待结果文件写入完成（含 DONE 标记）──
                var sw = Stopwatch.StartNew();
                var resultReady = false;
                while (!resultReady)
                {
                    if (sw.Elapsed.TotalSeconds > timeoutSeconds)
                    {
                        try { p.Kill(entireProcessTree: true); } catch { }
                        result.Error = $"PS 超时 ({timeoutSeconds}s)";
                        return result;
                    }
                    if (File.Exists(resultPath))
                    {
                        try
                        {
                            if (File.ReadAllText(resultPath).Contains("DONE", StringComparison.Ordinal))
                                resultReady = true;
                        }
                        catch { }
                    }
                    if (!resultReady)
                        await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                try { await p.WaitForExitAsync(ct).ConfigureAwait(false); } catch { }

                var lines = File.ReadAllLines(resultPath);
                foreach (var line in lines)
                {
                    if (line.StartsWith("OPEN_OK ", StringComparison.Ordinal))
                    {
                        result.Opened = true;
                        var parts = line.Substring(8).Split(new[] { 'x', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            if (int.TryParse(parts[0], out var w)) result.Width = w;
                            if (int.TryParse(parts[1], out var h)) result.Height = h;
                        }
                    }
                    else if (line.StartsWith("OPEN_FAIL", StringComparison.Ordinal))
                    {
                        result.Error = line.Substring("OPEN_FAIL".Length).Trim();
                    }
                }
                result.Error ??= File.Exists(outPngPath) ? null : "PNG 未生成";
                log?.Invoke($"[ps] 渲染结果: {result}\n");
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
            finally
            {
                try { File.Delete(scriptPath); } catch { }
            }
        }
    }
}
