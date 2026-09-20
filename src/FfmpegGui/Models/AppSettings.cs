using System.IO;
using System.Text.Json.Serialization;

namespace FfmpegGui.Models
{
    public class AppSettings
    {
        public string? FfmpegDirectory { get; set; }
        public string? OutputDirectory { get; set; }

        // ═══════════════════════════════════════════
        // v2.0 简化后的外部工具设置（3 项）
        // ═══════════════════════════════════════════

        /// <summary>exiftool 可执行文件路径（留空则自动检测）</summary>
        public string? ExifToolPath { get; set; }

        /// <summary>JPEG XL 参考库目录（含 cjxl/djxl/cjpegli，留空则自动检测）</summary>
        public string? JxlLibDir { get; set; }

        /// <summary>Windows 构建产物目录（含 JxrEncApp/avifenc/dngtool，留空则自动检测）</summary>
        public string? WindowsArtifactsDir { get; set; }

        /// <summary>标准 ICC 目录（PLAN/iccs，存放 iccgen 无法生成的 prophoto-rgb / bt2100-pq 等；留空则自动检测）</summary>
        public string? IccDirectory { get; set; }

        /// <summary>dngtool 可执行文件路径（LibRaw + DNG SDK，支持 DNG 1.7 JXL，留空则自动检测）</summary>
        public string? DngToolPath { get; set; }

        /// <summary>Photoshop 可执行文件路径（留空则自动检测注册表/常见路径）</summary>
        public string? PhotoshopPath { get; set; }

        // ═══════════════════════════════════════════
        // 旧字段（保留以兼容旧配置文件自动迁移）
        // ═══════════════════════════════════════════

        [Obsolete("v2.0 起使用 JxlLibDir 替代")]
        public string? CjxlPath { get; set; }

        [Obsolete("v2.0 起使用 JxlLibDir 替代")]
        public string? CjpegliPath { get; set; }

        [Obsolete("v2.0 起使用 WindowsArtifactsDir 替代")]
        public string? AvifencPath { get; set; }

        [Obsolete("v2.0 起使用 WindowsArtifactsDir 替代")]
        public string? UltrahdrPath { get; set; }

        [Obsolete("v2.0 起使用 WindowsArtifactsDir 替代")]
        public string? JxrPath { get; set; }

        public bool PreserveInputFolderStructure { get; set; } = false;

        public int MaxQueueSize { get; set; } = 16;

        public int ThemeMode { get; set; } = 2;

        /// <summary>界面语言："zh-CN"=中文（默认）, "en-US"=英文</summary>
        public string Language { get; set; } = "zh-CN";

        /// <summary>GPU 硬件加速：true=启用（Windows: DX11→Vulkan→CPU, Linux: Vulkan→OpenGL→CPU），false=纯软件渲染。需重启生效。</summary>
        public bool GpuAcceleration { get; set; } = true;

        /// <summary>
        /// 渲染后端（「手动选择 GPU」）。取值：<c>"auto"</c>（按平台默认回退链，默认值）/
        /// <c>"angle"</c>（Windows ANGLE→D3D11）/ <c>"vulkan"</c> / <c>"software"</c>（纯 CPU）。
        /// 与 <see cref="GpuAcceleration"/> 的关系：<c>GpuAcceleration=false</c> 时恒为 software；
        /// 否则以本字段为准。**需重启生效**。
        /// </summary>
        public string RenderingMode { get; set; } = "auto";

        /// <summary>简洁模式自动编码：true=队列有任务时自动开始，false=手动控制</summary>
        public bool SimpleModeAutoEncode { get; set; } = false;

        /// <summary>
        /// IPC 服务器（Named Pipe 单实例/跨进程任务提交）：true=启用，false=禁用（默认）。
        /// 默认关闭——后台化核心是"窗口最小化到托盘待命"，进程保留队列继续跑，
        /// 而非挂载常驻后台服务。仅需跨进程提交任务时开启。
        /// </summary>
        public bool EnableIpcServer { get; set; } = false;

        /// <summary>ffmpeg 进程优先级: 0=实时, 1=高, 2=高于正常, 3=正常, 4=低于正常, 5=低</summary>
        public int FfmpegPriority { get; set; } = 3;

        /// <summary>
        /// 图片最长边限制：true=启用（处理时读取原图宽高，若最长边超过 MaxLength 则按下采样比例缩放），false=禁用。
        /// </summary>
        public bool EnableMaxDimension { get; set; } = false;

        /// <summary>图片最长边参数 (MaxLength)。最长边 ≤ 该值时直接使用原图（跳过缩放）；超过时才缩放。</summary>
        public int MaxLength { get; set; } = 1920;

        /// <summary>
        /// 启用后：在检测到与 CPU 指令集匹配的优化二进制时，自动优先使用并保存工具路径（仅在用户未手动指定时生效）。
        /// </summary>
        public bool AutoUseSimdBinaries { get; set; } = true;

        /// <summary>
        /// 用户手动忽略的外部工具路径（持久化）。检测/选择时将跳过这些路径。
        /// </summary>
        public System.Collections.Generic.List<string> IgnoredToolPaths { get; set; } = new System.Collections.Generic.List<string>();

        /// <summary>
        /// 用户自定义缓存/临时文件目录。留空则自动选择（环境变量 → Linux /dev/shm → 系统 TEMP）。
        /// 所有管线中间文件（BMP/RAW/TIFF）将写入此目录，处理完成后自动清理。
        /// </summary>
        public string? CacheDirectory { get; set; }

        /// <summary>ffmpeg 完整路径（计算属性，不持久化）</summary>
        [JsonIgnore]
        public string FfmpegPath =>
            string.IsNullOrWhiteSpace(FfmpegDirectory)
                ? Services.PlatformServices.Ffmpeg
                : Path.Combine(FfmpegDirectory, Services.PlatformServices.Ffmpeg);

        /// <summary>ffprobe 完整路径（计算属性，不持久化）</summary>
        [JsonIgnore]
        public string FfprobePath =>
            string.IsNullOrWhiteSpace(FfmpegDirectory)
                ? Services.PlatformServices.Ffprobe
                : Path.Combine(FfmpegDirectory, Services.PlatformServices.Ffprobe);

        /// <summary>ffmpeg 所在目录（计算属性，不持久化）</summary>
        [JsonIgnore]
        public string? FfmpegDir =>
            string.IsNullOrWhiteSpace(FfmpegDirectory) ? null : FfmpegDirectory;

        // ── 图片文件格式筛选 ──

        /// <summary>
        /// 所有可选的图片格式定义（名称 → 扩展名列表）
        /// </summary>
        [JsonIgnore]
        public static readonly Dictionary<string, string[]> AllImageFormats = new()
        {
            ["PNG"]  = new[] { ".png" },
            ["JPEG"] = new[] { ".jpg", ".jpeg", ".jpe", ".jfif" },
            ["JPEG XL"] = new[] { ".jxl" },
            ["JPEG XR"] = new[] { ".jxr", ".wdp", ".hdp" },
            ["WebP"] = new[] { ".webp" },
            ["AVIF"] = new[] { ".avif" },
            ["TIFF"] = new[] { ".tiff", ".tif" },
            ["HEIC"] = new[] { ".heic", ".heif" },
            ["DNG"]  = new[] { ".dng" },
            ["BMP"]  = new[] { ".bmp" },
            ["GIF"]  = new[] { ".gif" },
            // ── 相机 RAW 格式 (输入专属，需 dngtool 预处理) ──
            ["📷 RAW-Canon"]    = new[] { ".cr2", ".cr3", ".crw" },
            ["📷 RAW-Nikon"]    = new[] { ".nef", ".nrw" },
            ["📷 RAW-Sony"]     = new[] { ".arw", ".srf", ".sr2" },
            ["📷 RAW-Fujifilm"] = new[] { ".raf" },
            ["📷 RAW-Olympus"]  = new[] { ".orf" },
            ["📷 RAW-Panasonic"]= new[] { ".rw2", ".rwl" },
            ["📷 RAW-Pentax"]   = new[] { ".pef" },
            ["📷 RAW-Others"]   = new[] { ".3fr", ".srw", ".mrw", ".x3f", ".erf", ".kdc", ".dcr", ".mef", ".mos", ".iiq", ".bay", ".raw" },
        };

        /// <summary>
        /// 支持的视频格式定义（名称 → 扩展名列表）。视频文件可在"动图模式"下作为输入。
        /// </summary>
        [JsonIgnore]
        public static readonly Dictionary<string, string[]> AllVideoFormats = new()
        {
            ["MP4"]  = new[] { ".mp4", ".m4v" },
            ["MOV"]  = new[] { ".mov" },
            ["MKV"]  = new[] { ".mkv" },
            ["AVI"]  = new[] { ".avi" },
            ["WebM"] = new[] { ".webm" },
            ["WMV"]  = new[] { ".wmv" },
            ["FLV"]  = new[] { ".flv" },
        };

        /// <summary>
        /// **仅支持解码输入、不提供编码输出**的格式名。
        /// HEIC/HEIF：本工具链无 HEIF 写通路（ffmpeg 无 heif muxer、无 heifenc），且 HEVC 图片编码另涉专利授权，
        /// 因此产品定位定为“可读不可写”；解码由 mov demuxer + hevc 解码器承担（已实测 .heic 会路由到 mov）。
        /// </summary>
        [JsonIgnore]
        public static readonly string[] DecodeOnlyFormats = { "HEIC" };

        /// <summary>判断某格式名是否只能解码（大小写/前导点无关）。</summary>
        public static bool IsDecodeOnlyFormat(string? name)
            => name != null && DecodeOnlyFormats.Contains(name.Trim().TrimStart('.'), StringComparer.OrdinalIgnoreCase);

        /// <summary>用户启用的图片格式名称列表（持久化到 settings.json）。默认全选所有格式。</summary>
        public List<string> EnabledImageFormats { get; set; } = new()
        {
            "PNG", "JPEG", "JPEG XL", "JPEG XR", "WebP", "AVIF", "TIFF", "HEIC",
            "DNG", "BMP", "GIF",
            "📷 RAW-Canon", "📷 RAW-Nikon", "📷 RAW-Sony", "📷 RAW-Fujifilm",
            "📷 RAW-Olympus", "📷 RAW-Panasonic", "📷 RAW-Pentax", "📷 RAW-Others",
            "MP4", "MOV", "MKV", "AVI", "WebM", "WMV", "FLV"
        };

        /// <summary>获取所有启用的图片扩展名（小写）</summary>
        public string[] GetEnabledExtensions()
        {
            var exts = new List<string>();
            foreach (var name in EnabledImageFormats)
            {
                if (AllImageFormats.TryGetValue(name, out var arr))
                    exts.AddRange(arr);
            }
            return exts.Select(e => e.ToLowerInvariant()).ToArray();
        }

        /// <summary>获取所有视频扩展名（小写），用于动图模式文件过滤</summary>
        [JsonIgnore]
        public string[] VideoExtensions => AllVideoFormats.Values
            .SelectMany(e => e)
            .Select(e => e.ToLowerInvariant())
            .ToArray();

        /// <summary>判断扩展名是否为视频文件</summary>
        public static bool IsVideoExtension(string ext) =>
            AllVideoFormats.Values.Any(list => list.Contains(ext, StringComparer.OrdinalIgnoreCase));

        /// <summary>获取当前启用的图片 + 视频扩展名（小写），用于动图模式拖放验证</summary>
        public string[] GetEnabledExtensionsIncludingVideo()
        {
            var exts = GetEnabledExtensions().ToList();
            // 仅添加用户在格式筛选窗口中启用的视频格式
            var enabledVideoSet = new HashSet<string>(EnabledImageFormats);
            foreach (var kv in AllVideoFormats)
            {
                if (enabledVideoSet.Contains(kv.Key))
                    exts.AddRange(kv.Value.Select(e => e.ToLowerInvariant()));
            }
            return exts.ToArray();
        }

        /// <summary>根据 EnabledImageFormats 生成 FilePicker 的 FileTypeFilter</summary>
        public Avalonia.Platform.Storage.FilePickerFileType[] GetImageFilePickerFilter()
        {
            var enabledExts = GetEnabledExtensions();
            var patterns = enabledExts.Select(e => "*" + e).ToArray();
            return new[]
            {
                new Avalonia.Platform.Storage.FilePickerFileType("图片文件") { Patterns = patterns },
                new Avalonia.Platform.Storage.FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
            };
        }

        /// <summary>动图模式下的 FileTypeFilter：图片 + 视频文件类型</summary>
        public Avalonia.Platform.Storage.FilePickerFileType[] GetAnimationFilePickerFilter()
        {
            var imgExts = GetEnabledExtensions();
            var vidExts = VideoExtensions;
            var imgPatterns = imgExts.Select(e => "*" + e).ToArray();
            var vidPatterns = vidExts.Select(e => "*" + e).ToArray();
            var allPatterns = imgPatterns.Concat(vidPatterns).ToArray();
            return new[]
            {
                new Avalonia.Platform.Storage.FilePickerFileType("图片与视频文件") { Patterns = allPatterns },
                new Avalonia.Platform.Storage.FilePickerFileType("图片文件") { Patterns = imgPatterns },
                new Avalonia.Platform.Storage.FilePickerFileType("视频文件") { Patterns = vidPatterns },
                new Avalonia.Platform.Storage.FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
            };
        }
    }
}
