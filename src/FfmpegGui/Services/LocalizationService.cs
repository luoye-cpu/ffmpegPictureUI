using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FfmpegGui.Models;

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
                // 外部文件缺失（典型场景：单文件发布时 Content 未随包输出）→
                // 回退到程序集内嵌资源，确保语言可用且可正常切换
                if (!TryLoadEmbeddedResource(language))
                    // 内嵌资源也缺失（极端情况）→ 内嵌回退字典
                    LoadEmbeddedFallback();
            }
            else
            {
                try
                {
                    var json = File.ReadAllText(filePath);
                    var dict = JsonSerializer.Deserialize(json, AppJsonContext.Default.DictionaryStringString);
                    if (dict != null)
                    {
                        foreach (var kv in dict)
                            _strings[kv.Key] = kv.Value;
                    }
                    else
                    {
                        // 文件存在但内容无法解析 → 尝试内嵌资源
                        if (!TryLoadEmbeddedResource(language))
                            LoadEmbeddedFallback();
                    }
                }
                catch
                {
                    // 文件损坏（编码/读取异常）→ 尝试内嵌资源
                    if (!TryLoadEmbeddedResource(language))
                        LoadEmbeddedFallback();
                }
            }

            // 统一收尾：标题版本号与程序集版本同步（2026-08-16 修复：
            // 此前版本号散落在 JSON 中需手工维护，发版遗漏导致标题版本滞后）
            NormalizeAppTitleVersion();

            CurrentLanguage = language;
            RefreshVersion++;
        }

        /// <summary>
        /// 将 app.title 中的版本号统一替换为程序集版本（如 "1.5.0" → "1.5.4"）。
        /// JSON/内嵌资源/回退字典任何来源均生效，避免手工维护版本号遗漏。
        /// </summary>
        private void NormalizeAppTitleVersion()
        {
            if (!_strings.TryGetValue("app.title", out var title) || string.IsNullOrEmpty(title))
                return;
            var ver = typeof(LocalizationService).Assembly.GetName().Version;
            if (ver == null) return;
            var verStr = $"{ver.Major}.{ver.Minor}.{ver.Build}";
            _strings["app.title"] = System.Text.RegularExpressions.Regex.Replace(
                title, @"\d+\.\d+(\.\d+)?", verStr);
        }

        /// <summary>
        /// 从程序集内嵌资源加载语言文件（Resources/Locales/*.json 编译进程序集）。
        /// 用于单文件发布等外部资源文件缺失的场景，保证语言可用且可双向切换。
        /// </summary>
        private bool TryLoadEmbeddedResource(string language)
        {
            try
            {
                var asm = typeof(LocalizationService).Assembly;
                // NativeAOT 友好：优先直接构造资源名（EmbeddedResource 编译期固定），
                // 仅作为最后手段才枚举资源清单（GetManifestResourceNames 在 AOT 下可用但属反射）
                var directName = $"FfmpegGui.Resources.Locales.{language}.json";
                var resName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.Equals(directName, StringComparison.OrdinalIgnoreCase));
                if (resName == null)
                {
                    resName = asm.GetManifestResourceNames()
                        .FirstOrDefault(n => n.Contains(".Resources.Locales.", StringComparison.OrdinalIgnoreCase)
                                             && n.EndsWith($".{language}.json", StringComparison.OrdinalIgnoreCase));
                }
                if (resName == null) return false;

                using var stream = asm.GetManifestResourceStream(resName);
                if (stream == null) return false;
                using var reader = new StreamReader(stream);
                var dict = JsonSerializer.Deserialize(reader.ReadToEnd(), AppJsonContext.Default.DictionaryStringString);
                if (dict == null) return false;

                _strings.Clear();
                foreach (var kv in dict)
                    _strings[kv.Key] = kv.Value;
                CurrentLanguage = language;
                RefreshVersion++;
                return true;
            }
            catch
            {
                return false;
            }
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

        /// <summary>
        /// 内嵌回退（极端情况：外部文件与程序集内嵌资源全部缺失）。
        /// 回退文本为英文，但语言标记保持 zh-CN（软件默认语言）——
        /// 修复：旧代码硬编码 CurrentLanguage="en-US" 会导致 ToggleLanguage
        /// 永远停留在英文状态，用户点击「中文」按钮也无法切回中文（v1.5.1 缺陷）。
        /// </summary>
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
            _strings["color.range"] = "Color Range (TV/PC)";
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
            CurrentLanguage = "zh-CN";
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
