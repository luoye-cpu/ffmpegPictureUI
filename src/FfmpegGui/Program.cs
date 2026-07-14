using Avalonia;
using System;
using System.IO;
using System.Text.Json;

namespace FfmpegGui
{
    internal class Program
    {
        /// <summary>GPU 加速是否启用（由 BuildAvaloniaApp 读取 settings.json + --no-gpu 参数决定）</summary>
        public static bool IsGpuAccelerated { get; private set; } = false;

        [STAThread]
        public static void Main(string[] args) => BuildAvaloniaApp(args)
            .StartWithClassicDesktopLifetime(args);

        /// <summary>
        /// 读取用户 GPU 偏好设置（settings.json）并结合 --no-gpu 参数决定是否启用 GPU 渲染。
        /// 优先级：--no-gpu 参数 > settings.json 中的 GpuAcceleration > 默认启用。
        /// </summary>
        private static bool ShouldEnableGpu(string[] args)
        {
            // 命令行参数 --no-gpu 强制禁用（最高优先级）
            foreach (var arg in args)
            {
                if (arg.Equals("--no-gpu", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // 读取 settings.json 中的用户偏好
            try
            {
                var settingsPath = Path.Combine(
                    Path.GetDirectoryName(Environment.ProcessPath!) ?? ".", "settings.json");
                if (File.Exists(settingsPath))
                {
                    var json = File.ReadAllText(settingsPath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("GpuAcceleration", out var prop))
                        return prop.GetBoolean();
                }
            }
            catch { /* 文件损坏或不存在，使用默认值 */ }

            return true; // 默认启用 GPU 加速
        }

        public static AppBuilder BuildAvaloniaApp(string[] args)
        {
            IsGpuAccelerated = ShouldEnableGpu(args);
            var builder = AppBuilder.Configure<App>()
                .UsePlatformDetect();

            if (IsGpuAccelerated)
            {
                if (OperatingSystem.IsWindows())
                {
                    // ── Windows: ANGLE EGL (D3D11 加速) 优先 → Vulkan 备选 → 软件兜底 ──
                    // AngleEgl = ANGLE 将 OpenGL ES 转换为 Direct3D 11，实现 GPU 加速
                    builder.With(new Win32PlatformOptions
                    {
                        RenderingMode = new[]
                        {
                            Win32RenderingMode.AngleEgl,
                            Win32RenderingMode.Vulkan,
                            Win32RenderingMode.Software
                        }
                    });
                }
                else if (OperatingSystem.IsLinux())
                {
                    // ── Linux: Vulkan 优先 → EGL 备选 → GLX 备选 → 软件兜底 ──
                    // 为将来 Linux/ARM 迁移预留
                    builder.With(new X11PlatformOptions
                    {
                        RenderingMode = new[]
                        {
                            X11RenderingMode.Vulkan,
                            X11RenderingMode.Egl,
                            X11RenderingMode.Glx,
                            X11RenderingMode.Software
                        }
                    });
                }
            }
            // else: 使用默认 CPU 软件渲染（IsGpuAccelerated == false）

            return builder.LogToTrace();
        }
    }
}
