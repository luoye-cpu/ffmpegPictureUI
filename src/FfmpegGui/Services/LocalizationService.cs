using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace FfmpegGui.Services
{
    /// <summary>
    /// 多语言本地化服务 — 支持中/英文动态切换。
    /// 默认中文 (zh-CN)，通过右上角按钮可切换为英文 (en-US)。
    /// 资源文件位于 Resources/Locales/ 目录下。
    /// </summary>
    public sealed class LocalizationService : INotifyPropertyChanged
    {
        public static LocalizationService Instance { get; } = new();

        private readonly Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);
        private string _currentLanguage = "zh-CN";

        /// <summary>当前语言代码 (zh-CN / en-US)</summary>
        public string CurrentLanguage
        {
            get => _currentLanguage;
            private set
            {
                if (_currentLanguage != value)
                {
                    _currentLanguage = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsZh));
                    OnPropertyChanged(nameof(IsEn));
                }
            }
        }

        public bool IsZh => _currentLanguage == "zh-CN";
        public bool IsEn => _currentLanguage == "en-US";

        /// <summary>刷新版本号：语言切换时递增，用于触发 UI 绑定刷新</summary>
        private int _refreshVersion;
        public int RefreshVersion
        {
            get => _refreshVersion;
            private set { _refreshVersion = value; OnPropertyChanged(); }
        }

        /// <summary>索引器：通过字符串键获取本地化文本，键不存在时返回键本身</summary>
        public string this[string key]
        {
            get
            {
                if (_strings.TryGetValue(key, out var value))
                    return value;
                return key; // 回退：显示键名便于发现遗漏
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void LoadLocale(string language)
        {
            _strings.Clear();
            var localeDir = Path.Combine(
                Path.GetDirectoryName(Environment.ProcessPath!) ?? ".",
                "Resources", "Locales");

            // 也尝试在源码目录查找（开发模式）
            if (!Directory.Exists(localeDir))
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                // 尝试往上查找 src 目录（开发模式 dotnet run）
                var probe = baseDir;
                for (int i = 0; i < 6; i++)
                {
                    var candidate = Path.Combine(probe, "Resources", "Locales");
                    if (Directory.Exists(candidate))
                    {
                        localeDir = candidate;
                        break;
                    }
                    probe = Path.GetDirectoryName(probe);
                    if (probe == null) break;
                }
            }

            var filePath = Path.Combine(localeDir, $"{language}.json");
            if (!File.Exists(filePath))
            {
                // 回退：找不到资源文件时尝试使用默认中文
                if (language != "zh-CN")
                {
                    LoadLocale("zh-CN");
                    return;
                }
                // 连中文都找不到，使用内嵌的英文回退
                LoadEmbeddedFallback();
                return;
            }

            try
            {
                var json = File.ReadAllText(filePath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (dict != null)
                {
                    foreach (var kv in dict)
                        _strings[kv.Key] = kv.Value;
                }
            }
            catch
            {
                if (language != "zh-CN")
                {
                    LoadLocale("zh-CN");
                    return;
                }
                LoadEmbeddedFallback();
                return;
            }

            CurrentLanguage = language;
            RefreshVersion++;
        }

        /// <summary>切换语言：zh-CN ↔ en-US</summary>
        public void ToggleLanguage()
        {
            var next = _currentLanguage == "zh-CN" ? "en-US" : "zh-CN";
            LoadLocale(next);
        }

        /// <summary>设置为指定语言</summary>
        public void SetLanguage(string lang)
        {
            LoadLocale(lang);
        }

        /// <summary>内嵌英文回退（极端情况：资源文件全部丢失）</summary>
        private void LoadEmbeddedFallback()
        {
            _strings.Clear();
            _strings["app.title"] = "FFmpeg Picture Converter";
            _strings["format.jpeg"] = "JPEG";
            _strings["format.png"] = "PNG";
            _strings["quality"] = "Quality";
            _strings["encoder"] = "Encoder";
            _strings["output.format"] = "Output Format";
            _strings["start.queue"] = "▶ Start Queue";
            _strings["stop.queue"] = "⏹ Stop Queue";
            _strings["add.to.queue"] = "Add to Queue";
            _strings["clear.queue"] = "Clear Queue";
            _strings["select.file"] = "Select Files...";
            _strings["select.folder"] = "Select Folder";
            _strings["delete.selected"] = "Delete Selected";
            _strings["conversion.queue"] = "Conversion Queue";
            _strings["selected.files"] = "Selected Files";
            _strings["generated.command"] = "Generated Command";
            _strings["execution.log"] = "Execution Log";
            _strings["concurrent.tasks"] = "Concurrent Tasks";
            _strings["process.priority"] = "Process Priority";
            _strings["preset"] = "Preset";
            _strings["simple.mode"] = "Simple Mode";
            _strings["return.full"] = "← Return to Full Mode";
            _strings["auto.encode"] = "Auto Encode";
            _strings["add.files"] = "Add Files";
            _strings["clear"] = "Clear";
            _strings["show.errors.only"] = "Show Errors Only";
            _strings["ready"] = "Ready";
            _strings["queue"] = "Queue";
            _strings["items"] = "items";
            _strings["drag.drop.hint"] = "Drag & Drop to Start";
            _strings["files"] = "files";
            _strings["ffmpeg.dir"] = "FFmpeg Dir";
            _strings["output.dir"] = "Output Dir";
            _strings["browse"] = "Browse...";
            _strings["keep.dir.structure"] = "Keep Dir Structure";
            _strings["cache.dir"] = "Cache Dir";
            _strings["theme"] = "Theme";
            _strings["gpu"] = "GPU";
            _strings["external.tools"] = "External Tools";
            _strings["simd.optimization"] = "SIMD Optimization";
            _strings["jxl.reference.lib"] = "JXL Reference Lib";
            _strings["conversion.mode"] = "Conversion Mode";
            _strings["still.image"] = "Still Image";
            _strings["animation"] = "Animation";
            _strings["advanced.color"] = "Advanced Color";
            _strings["advanced.codec"] = "Advanced Codec Options";
            _strings["cpu.threads"] = "CPU Threads";
            _strings["auto"] = "Auto";
            _strings["single.thread"] = "Single Thread";
            _strings["metadata.mode"] = "Metadata Mode";
            _strings["preserve.all"] = "Preserve All Metadata";
            _strings["strip.all"] = "Strip All Metadata";
            _strings["lossless"] = "Lossless";
            _strings["generate.command"] = "Generate Command";
            _strings["parameter.panel"] = "Parameter Panel";
            _strings["chroma.subsampling"] = "Chroma Subsampling";
            _strings["bit.depth"] = "Bit Depth";
            _strings["color.space.quick"] = "Color Space (Quick)";
            _strings["color.primaries"] = "Color Primaries";
            _strings["color.transfer"] = "Color Transfer (trc)";
            _strings["color.matrix"] = "Color Matrix";
            _strings["icc.color.management"] = "ICC Color Management";
            _strings["exiftool.privacy"] = "ExifTool Privacy Cleanup";
            _strings["animation.params"] = "Animation Parameters";
            _strings["fps"] = "FPS";
            _strings["loop.count"] = "Loop Count";
            _strings["scale.width"] = "Scale Width";
            _strings["max.duration"] = "Max Duration (s)";
            _strings["format.filter"] = "Format Filter...";
            _strings["double.click.hint"] = "Double click to view media info";
            _strings["stop.after.current"] = "Stop After Queue";
            _strings["total.files"] = "Total Files";
            _strings["language"] = "EN";
            _strings["icc.mode.none"] = "① Default — No ICC (CICP only)";
            _strings["icc.mode.carry"] = "② Carry ICC — Keep source or add standard ICC";
            _strings["icc.mode.bake"] = "③ Bake + Embed — Convert pixels & embed standard ICC";
            _strings["icc.mode.bakeonly"] = "④ Bake Only — Convert pixels, no ICC output";
            _strings["strip.gps"] = "Strip GPS Location";
            _strings["strip.time"] = "Strip Timestamps";
            _strings["strip.camera"] = "Strip Camera/Lens Info";
            _strings["strip.all.exif"] = "Strip All EXIF";
            _strings["strip.xmp"] = "Strip XMP Metadata";
            _strings["append.png.extension"] = "Append .png extension (JXL/AVIF compat)";
            CurrentLanguage = "en-US";
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
