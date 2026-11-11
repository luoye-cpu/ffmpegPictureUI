using System;
using System.IO;
using System.Threading.Tasks;
using FfmpegGui.Services;

namespace ServiceProbe
{
    internal static partial class Program
    {
        // ══════════════════════════════════════════════════════════════
        // mediasvc —— 三服务进程内判据（批 4·5，docs/TEST_MODULE_PLAN_2026-11-12.md 盲区#10 收口）
        //   MediaInfoService / PlanFolderDetector / PsRenderService 此前 tests 面 0 引用。
        //   载体 = verify-tool-detection.ps1（主题同族：检测/PLAN）经 `ServiceProbe mediasvc` 调用。
        //   ⚠ PlanFolderDetector.Detect() 是 **exe 相邻目录**查找（BaseDirectory+PLAN）——ServiceProbe
        //     的 bin 旁无 PLAN ⇒ 本 mode 锁的是**否定路径契约**（no-throw + IsValid=false + 字段空）
        //     与条件不变式（IsValid ⇒ 四目录非空且 ffmpeg.exe 在盘）；正控路径由第 95 条
        //     verify-tool-detection 的 B4（LooksLikePlan/PlanFolderSource）承载，不在此重复。
        //   ⚠ PsRenderService 依赖本机 Photoshop 安装（注册表/常见路径），判据全部写成**条件不变式**
        //     （IsAvailable ⇒ DetectedPath 非空；ClearCache 后答案不变）⇒ 两类机器都判绿、装了 PS 的
        //     机器额外锁 DetectedPath 存在性。
        //   ⚠ 临时目录走 Path.GetTempPath()（runner 已钉到 tests/output/_tmp/round-tmp）+ finally 删除。
        private static void ProbeMediaSvc()
        {
            Console.WriteLine("=== mediasvc: MediaInfoService / PlanFolderDetector / PsRenderService ===");

            var planDir = Environment.GetEnvironmentVariable("FFMPEGGUI_PLAN_DIR");
            var ffmpegDir = string.IsNullOrEmpty(planDir)
                ? null
                : Directory.GetDirectories(planDir, "ffmpeg-full*", SearchOption.TopDirectoryOnly) is { Length: > 0 } ds ? ds[0] : null;
            var ffprobe = ffmpegDir != null ? Path.Combine(ffmpegDir, "ffprobe.exe") : null;

            // ── ① MediaInfoService：真件 ffprobe JSON ──
            var tmp = Path.Combine(Path.GetTempPath(), "mediasvc_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            try
            {
                string png = Path.Combine(tmp, "probe.png");
                if (ffmpegDir != null && File.Exists(ffprobe))
                {
                    var mk = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = Path.Combine(ffmpegDir, "ffmpeg.exe"),
                        Arguments = $"-y -f lavfi -i testsrc2=size=256x256:rate=1 -frames:v 1 \"{png}\"",
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var mkp = System.Diagnostics.Process.Start(mk);
                    mkp?.WaitForExit();
                }

                if (File.Exists(png) && ffprobe != null)
                {
                    var info = MediaInfoService.GetMediaInfoAsync(png, ffprobe, Path.Combine(ffmpegDir!, "ffmpeg.exe"))
                        .GetAwaiter().GetResult();
                    Check(!string.IsNullOrWhiteSpace(info), "media: 真件 ffprobe 输出非空（len=" + (info?.Length ?? 0) + "）");
                    Check(info != null && info.Contains("png", StringComparison.OrdinalIgnoreCase),
                          "media: JSON 点名 png 格式");
                    Check(info != null && info.Contains("256"), "media: JSON 含宽 256");
                }
                else
                {
                    Check(false, "media: 前提（ffmpeg/ffprobe 可用）不满足 —— 本行应只在坏环境出现");
                }

                // ② 缺文件：不得抛未处理异常（空串/错误串都算诚实回话）
                var missing = MediaInfoService.GetMediaInfoAsync(Path.Combine(tmp, "no_such_file.png"), ffprobe,
                    ffmpegDir != null ? Path.Combine(ffmpegDir, "ffmpeg.exe") : null).GetAwaiter().GetResult();
                Check(missing != null, "media: 缺文件回话不崩（返回 " + (missing == null ? "<null>" : $"len={missing.Length}") + "）");
            }
            catch (Exception ex)
            {
                Check(false, "media: 未处理异常 = " + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                try { Directory.Delete(tmp, recursive: true); } catch { }
            }

            // ── ③ PlanFolderDetector：否定路径契约（ServiceProbe 旁无 PLAN）──
            try
            {
                var r = PlanFolderDetector.Detect();
                Check(r != null, "plan: Detect 返回非 null");
                if (r!.IsValid)
                {
                    // 条件不变式：声明有效 ⇒ 四目录都在且 ffmpeg.exe 在盘（装在带 PLAN 目录旁的宿主上才走）
                    Check(r.FfmpegDir != null && File.Exists(Path.Combine(r.FfmpegDir, PlatformServices.Ffmpeg)),
                          "plan: IsValid ⇒ FfmpegDir 有 ffmpeg.exe");
                    Check(r.JxlBinDir != null && Directory.Exists(r.JxlBinDir), "plan: IsValid ⇒ JxlBinDir 在盘");
                    Check(r.ExifToolDir != null && Directory.Exists(r.ExifToolDir), "plan: IsValid ⇒ ExifToolDir 在盘");
                    Check(r.ArtifactsDir != null && Directory.Exists(r.ArtifactsDir), "plan: IsValid ⇒ ArtifactsDir 在盘");
                }
                else
                {
                    Check(r.PlanPath == null && r.FfmpegDir == null && r.JxlBinDir == null,
                          "plan: 无 PLAN ⇒ fail-closed 空结果（PlanPath/FfmpegDir/JxlBinDir 全 null）");
                }
            }
            catch (Exception ex)
            {
                Check(false, "plan: Detect 未处理异常 = " + ex.GetType().Name + " " + ex.Message);
            }

            // ── ④ PsRenderService：条件不变式 + 缓存幂等 ──
            try
            {
                bool a1 = PsRenderService.Detect();
                string? p1 = PsRenderService.DetectedPath;
                Check(a1 == (p1 != null), "ps: IsAvailable ⇔ DetectedPath 非空（缓存自洽，a1=" + a1 + "）");
                if (a1) Check(File.Exists(p1!), "ps: 声称可用 ⇒ 路径在盘（" + p1 + "）");

                PsRenderService.ClearCache();
                bool a2 = PsRenderService.Detect();
                Check(a2 == a1, "ps: ClearCache 后 Detect 答案不变（幂等，a1=" + a1 + " a2=" + a2 + "）");
            }
            catch (Exception ex)
            {
                Check(false, "ps: Detect/ClearCache 未处理异常 = " + ex.GetType().Name + " " + ex.Message);
            }
        }
    }
}
