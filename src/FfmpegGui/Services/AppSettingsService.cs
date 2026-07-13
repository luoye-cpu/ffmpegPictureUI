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

        private static AppSettings? _current;
        public static AppSettings Current => _current ??= Load();

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        // ── v2.0 自动迁移旧字段 ──
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
                        _current = settings;
                        return settings;
                    }
                }
            }
            catch { }

            _current = new AppSettings();
            return _current;
        }

        public static void Save()
        {
            if (_current == null) return;
            try
            {
                // 不序列化已迁移的旧字段
                var clone = new AppSettings
                {
                    FfmpegDirectory = _current.FfmpegDirectory,
                    OutputDirectory = _current.OutputDirectory,
                    ExifToolPath = _current.ExifToolPath,
                    JxlLibDir = _current.JxlLibDir,
                    WindowsArtifactsDir = _current.WindowsArtifactsDir,
                    PreserveInputFolderStructure = _current.PreserveInputFolderStructure,
                    MaxQueueSize = _current.MaxQueueSize,
                    ThemeMode = _current.ThemeMode,
                    FfmpegPriority = _current.FfmpegPriority,
                    AutoUseSimdBinaries = _current.AutoUseSimdBinaries,
                    IgnoredToolPaths = _current.IgnoredToolPaths,
                    EnabledImageFormats = _current.EnabledImageFormats,
                };
                var json = JsonSerializer.Serialize(clone, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
                File.WriteAllText(SettingsPath, json);
            }
            catch { }
        }
    }
}
