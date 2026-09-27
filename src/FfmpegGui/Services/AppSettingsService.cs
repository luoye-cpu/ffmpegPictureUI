using System;
using System.IO;
using System.Text.Json;
using FfmpegGui.Models;

namespace FfmpegGui.Services
{
    public static class AppSettingsService
    {
        /// <summary>
        /// 设置文件存储在 exe 同目录下的 settings.json（便携模式，单文件发布也正确）
        /// </summary>
        private static readonly string SettingsPath = Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath!) ?? ".", "settings.json");

        /// <summary>
        /// 用户级配置目录（跨实例共享）。用于存放多实例共享的全局设置。
        /// 优先级低于便携 settings.json（便携优先），仅作为备用读取源。
        /// </summary>
        private static readonly string UserConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FFmpegPictureUI");

        /// <summary>环境变量前缀，用于覆盖配置（如 FFMPEGGUI_FFMPEG_DIR）</summary>
        private const string EnvPrefix = "FFMPEGGUI_";

        private static AppSettings? _current;
        public static AppSettings Current => _current ??= Load();

        /// <summary>本次真正读到的设置文件（决定 Save 写回哪里，修“读得到、写不回”的不对称 S-P0-2）</summary>
        private static string? _loadedFrom;
        /// <summary>_loadedFrom 是否来自 --settings 显式指定（是 ⇒ Save 不得偷偷写到别处）</summary>
        private static bool _loadedFromExplicit;

        /// <summary>最近一次读设置文件的问题（损坏/权限）；null = 无。静默重置用户设置属缺陷，必须可见。</summary>
        public static string? LastLoadIssue { get; private set; }
        /// <summary>最近一次保存的问题（失败，或回退写到了别的位置）；null = 一切正常。</summary>
        public static string? LastSaveIssue { get; private set; }
        /// <summary>最近一次成功落盘的设置文件路径（诊断用）。</summary>
        public static string? LastSavedTo { get; private set; }


        /// <summary>用户级配置目录路径</summary>
        public static string GetUserConfigDir() => UserConfigDir;

        /// <summary>
        /// 使用指定路径加载设置（headless 模式 --settings 支持）。
        /// 优先级：环境变量覆盖 > 指定文件 > 便携 settings.json > 用户配置目录 > 默认。
        /// </summary>
        public static AppSettings Load(string? explicitPath)
        {
            var settings = new AppSettings();

            // 选择基础文件层：用户配置目录 < 便携 < 指定文件（后者整体覆盖）
            var userPath = Path.Combine(UserConfigDir, "settings.json");
            string fileToLoad = string.Empty;
            if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
                fileToLoad = explicitPath;
            else if (File.Exists(SettingsPath))
                fileToLoad = SettingsPath;
            else if (File.Exists(userPath))
                fileToLoad = userPath;

            if (!string.IsNullOrEmpty(fileToLoad))
            {
                try
                {
                    var json = File.ReadAllText(fileToLoad);
                    var loaded = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppSettings);
                    if (loaded != null) settings = loaded;
                }
                catch (Exception ex)
                {
                    // 旧写法是 catch { } —— 用户全部设置被静默重置为默认值且毫无痕迹。
                    LastLoadIssue = $"设置文件无法解析，已按默认值继续（{Path.GetFileName(fileToLoad)}）：{ex.GetType().Name}: {ex.Message}";
                }
                _loadedFrom = fileToLoad;
                _loadedFromExplicit = !string.IsNullOrWhiteSpace(explicitPath)
                    && string.Equals(fileToLoad, Path.GetFullPath(explicitPath!), StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                _loadedFrom = null;
                _loadedFromExplicit = false;
            }

            // 环境变量字段级覆盖（最高优先级）
            settings = ApplyEnvironmentOverrides(settings);
            MigrateLegacy(settings);

            _current = settings;
            return settings;
        }

        /// <summary>加载默认便携配置（供既有调用点使用）</summary>
        public static AppSettings Load() => Load(null);

        /// <summary>应用环境变量覆盖（字段级，FFMPEGGUI_ 前缀）</summary>
        private static AppSettings ApplyEnvironmentOverrides(AppSettings settings)
        {
            static string? Get(string key) =>
                Environment.GetEnvironmentVariable(EnvPrefix + key);

            settings.FfmpegDirectory = Get("FFMPEG_DIR") ?? settings.FfmpegDirectory;
            settings.OutputDirectory = Get("OUTPUT_DIR") ?? settings.OutputDirectory;
            settings.ExifToolPath = Get("EXIFTOOL_PATH") ?? settings.ExifToolPath;
            settings.JxlLibDir = Get("JXL_LIB_DIR") ?? settings.JxlLibDir;
            settings.WindowsArtifactsDir = Get("ARTIFACTS_DIR") ?? settings.WindowsArtifactsDir;
            settings.DngToolPath = Get("DNGTOOL_PATH") ?? settings.DngToolPath;
            settings.CacheDirectory = Get("CACHE_DIR") ?? settings.CacheDirectory;

            if (bool.TryParse(Get("GPU"), out var gpu)) settings.GpuAcceleration = gpu;
            if (bool.TryParse(Get("AUTO_SIMD"), out var simd)) settings.AutoUseSimdBinaries = simd;
            if (bool.TryParse(Get("PRESERVE_STRUCTURE"), out var preserve)) settings.PreserveInputFolderStructure = preserve;
            if (int.TryParse(Get("MAX_QUEUE"), out var maxQueue)) settings.MaxQueueSize = maxQueue;
            if (int.TryParse(Get("PRIORITY"), out var priority)) settings.FfmpegPriority = Math.Clamp(priority, 0, 5);
            if (int.TryParse(Get("THEME"), out var theme)) settings.ThemeMode = theme;
            if (int.TryParse(Get("AUTO_ENCODE"), out var autoEncode)) settings.SimpleModeAutoEncode = autoEncode != 0;
            if (!string.IsNullOrWhiteSpace(Get("LANG"))) settings.Language = Get("LANG")!;

            return settings;
        }

        /// <summary>v2.0 旧字段迁移</summary>
        private static void MigrateLegacy(AppSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.JxlLibDir))
            {
                var legacy = settings.CjxlPath ?? settings.CjpegliPath;
                if (!string.IsNullOrWhiteSpace(legacy))
                {
                    settings.JxlLibDir = legacy;
                    settings.CjxlPath = null;
                    settings.CjpegliPath = null;
                }
            }
            if (string.IsNullOrWhiteSpace(settings.WindowsArtifactsDir))
            {
                var legacy = settings.AvifencPath ?? settings.UltrahdrPath ?? settings.JxrPath;
                if (!string.IsNullOrWhiteSpace(legacy))
                {
                    settings.WindowsArtifactsDir = legacy;
                    settings.AvifencPath = null;
                    settings.UltrahdrPath = null;
                    settings.JxrPath = null;
                }
            }
        }

        /// <summary>
        /// 落盘设置。<b>S-P1-1</b>：临时件 + 原子改名 + 保留上一版 <c>.bak</c>
        /// （旧写法一句 <c>File.WriteAllText</c> 直写，中途崩溃/磁盘满会把用户唯一一份设置打断）。
        /// <b>S-P0-2</b>：写回**本次的读取来源**（从 %APPDATA% 读到的就写回 %APPDATA%，旧写法只写 exe 同目录 ⇒
        /// 读得到、写不回）；便携目录只读时回退到用户配置目录，并把这件事写进 <see cref="LastSaveIssue"/>
        /// ——旧写法是 <c>catch { }</c> 整个吞掉，用户以为已经保存了。
        /// 显式 <c>--settings</c> 指定的文件写不进去时**不回退**（那是用户点名的位置，偷偷改地方更糟），直接报失败。
        /// </summary>
        public static bool Save()
        {
            LastSaveIssue = null;
            if (_current == null)
            {
                LastSaveIssue = "设置尚未加载，本次 Save 被忽略";
                return false;
            }

            // 不序列化已迁移的旧字段
            var clone = new AppSettings
            {
                FfmpegDirectory = _current.FfmpegDirectory,
                OutputDirectory = _current.OutputDirectory,
                ExifToolPath = _current.ExifToolPath,
                JxlLibDir = _current.JxlLibDir,
                WindowsArtifactsDir = _current.WindowsArtifactsDir,
                IccDirectory = _current.IccDirectory,
                DngToolPath = _current.DngToolPath,
                PhotoshopPath = _current.PhotoshopPath,
                PreserveInputFolderStructure = _current.PreserveInputFolderStructure,
                MaxQueueSize = _current.MaxQueueSize,
                ThemeMode = _current.ThemeMode,
                Language = _current.Language,
                FfmpegPriority = _current.FfmpegPriority,
                GpuAcceleration = _current.GpuAcceleration,
                // ⚠ 这张表是**手写**的，漏一个字段就等于把它**重置成模型默认值**（不是"不落盘"，
                //   因为 clone 是 new AppSettings ⇒ 未赋值的属性带的是默认值，随后被整体序列化落盘）。
                //   RenderingMode 就因此漏了很久：菜单改它后立刻 Save()，值当场被打回「auto」。
                //   ⇒ 新增任何持久化属性都必须同时加到这里；判据见 tests/scripts/_probe-settings-clone-coverage.ps1。
                RenderingMode = _current.RenderingMode,
                SimpleModeAutoEncode = _current.SimpleModeAutoEncode,
                EnableIpcServer = _current.EnableIpcServer,
                AutoUseSimdBinaries = _current.AutoUseSimdBinaries,
                IgnoredToolPaths = _current.IgnoredToolPaths,
                EnabledImageFormats = _current.EnabledImageFormats,
                CacheDirectory = _current.CacheDirectory,
                EnableMaxDimension = _current.EnableMaxDimension,
                MaxLength = _current.MaxLength,
            };
            // 序列化选项 (WriteIndented + 忽略 null) 已由 AppJsonContext 的
            // JsonSourceGenerationOptions 声明，无需再传 JsonSerializerOptions
            var json = JsonSerializer.Serialize(clone, AppJsonContext.Default.AppSettings);

            var userPath = Path.Combine(UserConfigDir, "settings.json");
            var primary = _loadedFrom ?? SettingsPath;
            var candidates = new List<string> { primary };
            if (!_loadedFromExplicit)
            {
                foreach (var alt in new[] { SettingsPath, userPath })
                    if (!candidates.Any(c => string.Equals(c, alt, StringComparison.OrdinalIgnoreCase)))
                        candidates.Add(alt);
            }

            Exception? last = null;
            foreach (var target in candidates)
            {
                try
                {
                    WriteAtomicWithBackup(target, json);
                    LastSavedTo = target;
                    if (!string.Equals(target, primary, StringComparison.OrdinalIgnoreCase))
                        LastSaveIssue = $"设置没能写入 {primary}，已改写到 {target}（下次启动会从这里读取）";
                    return true;
                }
                catch (Exception ex) { last = ex; }
            }

            LastSaveIssue = $"设置保存失败（试过 {candidates.Count} 个位置）：" +
                $"{last?.GetType().Name}: {last?.Message}";
            return false;
        }

        /// <summary>临时件 → 备份旧版 → 原子改名。任何一步失败，目标文件本身都还是原来那份完整内容。</summary>
        private static void WriteAtomicWithBackup(string target, string json)
        {
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var tmp = target + ".tmp";
            try
            {
                File.WriteAllText(tmp, json);
                if (File.Exists(target)) File.Copy(target, target + ".bak", overwrite: true);
                File.Move(tmp, target, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
    }
}
