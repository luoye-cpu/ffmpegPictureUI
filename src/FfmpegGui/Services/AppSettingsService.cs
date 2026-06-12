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
                var json = JsonSerializer.Serialize(_current, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch { }
        }
    }
}
