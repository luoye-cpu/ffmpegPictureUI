using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FfmpegGui.Services
{
    /// <summary>
    /// ExifTool 集成服务：用于选择性剥离 EXIF 元数据（GPS、时间、相机信息等）
    /// 检测逻辑与 CjxlService 一致——在 ffmpeg 同目录、程序同目录、PATH 中查找
    /// </summary>
    public static class ExifToolService
    {
        private static string? _detectedPath;
        private static bool _detected;

        public static bool IsAvailable
        {
            get
            {
                if (!_detected)
                    Detect();
                return _detectedPath != null;
            }
        }

        /// <summary>
        /// 获取已检测的 exiftool 路径（可能为 null）
        /// </summary>
        public static string? DetectedPath
        {
            get
            {
                if (!_detected) Detect();
                return _detectedPath;
            }
        }

        /// <summary>
        /// 检测 exiftool 位置（三优先级）：
        /// ① 手动指定路径（AppSettings.ExifToolPath）
        /// ② ffmpeg 同目录 / 程序同目录
        /// ③ 系统 PATH
        /// 注意：自动跳过 exiftool(-k).exe（带按键等待的交互版本），
        ///       若仅找到 (-k) 版本则自动复制为 exiftool.exe 使用。
        /// </summary>
        public static void Detect()
        {
            _detected = true;
            _detectedPath = null;

            var names = OperatingSystem.IsWindows()
                ? new[] { PlatformServices.Exiftool, "exiftool(-k).exe" }
                : new[] { PlatformServices.Exiftool };

            // ── ① 手动指定路径 ──
            var manual = AppSettingsService.Current.ExifToolPath;
            if (!string.IsNullOrWhiteSpace(manual) && File.Exists(manual))
            {
                _detectedPath = ResolveSafeExifToolPath(manual);
                if (_detectedPath != null) return;
            }

            // ── ② PLAN 便携包自动检测 ──
            try
            {
                var planFound = PlatformServices.TryFindInPlanFolder(PlatformServices.Exiftool);
                if (planFound != null) { _detectedPath = ResolveSafeExifToolPath(planFound); if (_detectedPath != null) return; }
            }
            catch { }

            // ── ③ 同目录（ffmpeg 目录 → 程序目录）──
            var dirs = new[]
            {
                AppSettingsService.Current.FfmpegDir ?? "",
                AppDomain.CurrentDomain.BaseDirectory,
            };
            foreach (var dir in dirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                // 优先查找标准版 exiftool.exe，其次 (-k) 版
                foreach (var name in names)
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate))
                    {
                        _detectedPath = ResolveSafeExifToolPath(candidate);
                        if (_detectedPath != null) return;
                    }
                }
            }

            // ── ④ 扩展搜索路径（Windows: LocalAppData\Programs 等）──
            try
            {
                var extended = ExternalToolsDetector.FindToolInExtendedPaths(
                    PlatformServices.Exiftool, $"*{PlatformServices.Exiftool}*");
                if (extended != null) { _detectedPath = ResolveSafeExifToolPath(extended); if (_detectedPath != null) return; }
            }
            catch { }

            // ── ⑤ 系统 PATH ──
            foreach (var name in names)
            {
                if (TryFindInPath(name, out var pathFound) && pathFound != null)
                {
                    _detectedPath = ResolveSafeExifToolPath(pathFound);
                    if (_detectedPath != null) return;
                }
            }
        }

        /// <summary>
        /// 将 exiftool(-k).exe 转换为标准 exiftool.exe（复制文件避免按键等待挂起）
        /// </summary>
        private static string? ResolveSafeExifToolPath(string candidatePath)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || !File.Exists(candidatePath))
                return null;

            var fileName = Path.GetFileName(candidatePath);
            // 标准版直接使用
            if (!fileName.Contains("(-k)"))
                return candidatePath;

            // (-k) 版本：查找或创建同目录下的标准 exiftool.exe
            var dir = Path.GetDirectoryName(candidatePath);
            if (string.IsNullOrEmpty(dir)) return null;
            var safePath = Path.Combine(dir, PlatformServices.Exiftool);

            if (File.Exists(safePath))
                return safePath;

            // 将 (-k) 版本复制为标准版（内容相同，文件名中的 (-k) 触发等待行为）
            try
            {
                File.Copy(candidatePath, safePath, overwrite: false);
                return safePath;
            }
            catch
            {
                // 复制失败则回退到 (-k) 版本（可能会挂起，但至少尝试了）
                return candidatePath;
            }
        }

        /// <summary>在系统 PATH 中查找可执行文件（通过 -ver 验证可用性）</summary>
        private static bool TryFindInPath(string exeName, out string? fullPath)
        {
            fullPath = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exeName,
                    Arguments = "-ver",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    p.WaitForExit(5000);
                    if (p.ExitCode == 0)
                    {
                        // 用 where/which 解析完整路径
                        var whichPsi = new ProcessStartInfo
                        {
                            FileName = OperatingSystem.IsWindows() ? "where" : "which",
                            Arguments = exeName,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var wp = Process.Start(whichPsi);
                        if (wp != null)
                        {
                            var output = wp.StandardOutput.ReadToEnd().Trim();
                            wp.WaitForExit(5000);
                            if (!string.IsNullOrWhiteSpace(output))
                            {
                                var firstLine = output.Split(new[] { '\r', '\n' },
                                    StringSplitOptions.RemoveEmptyEntries)[0];
                                if (File.Exists(firstLine))
                                {
                                    fullPath = firstLine;
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 根据选项构建 exiftool 参数
        /// </summary>
        public static string BuildArguments(string filePath, Models.FfmpegOptions options)
        {
            var sb = new StringBuilder();
            sb.Append("-overwrite_original ");

            if (options.StripExifGps)
                sb.Append("-gps:all= ");

            if (options.StripExifTime)
                sb.Append("-time:all= ");

            if (options.StripExifCamera)
            {
                // 清除相机/镜头相关标签（不影响其他 EXIF 如色彩空间、方向等）
                sb.Append("-Make= -Model= ");
                sb.Append("-LensMake= -LensModel= -LensSerialNumber= ");
                sb.Append("-FocalLength= -FocalLengthIn35mmFormat= ");
                sb.Append("-FNumber= -ApertureValue= -MaxApertureValue= ");
                sb.Append("-ExposureTime= -ShutterSpeedValue= ");
                sb.Append("-ISO= -ISOSpeedRatings= ");
                sb.Append("-Flash= -WhiteBalance= ");
                sb.Append("-ExposureProgram= -ExposureMode= -ExposureBiasValue= ");
                sb.Append("-MeteringMode= -SceneCaptureType= ");
                sb.Append("-LightSource= -SensingMethod= ");
                sb.Append("-CameraOwnerName= -BodySerialNumber= ");
            }

            if (options.StripExifAll)
                sb.Append("-exif:all= ");

            if (options.StripXmp)
                sb.Append("-xmp:all= ");

            sb.Append($"\"{filePath}\"");
            return sb.ToString();
        }

        /// <summary>
        /// 对文件执行 exiftool 清理
        /// </summary>
        public static async Task<int> RunAsync(
            string filePath,
            Models.FfmpegOptions options,
            Action<string>? logCallback = null)
        {
            if (_detectedPath == null)
            {
                Detect();
                if (_detectedPath == null)
                    throw new InvalidOperationException("exiftool 未检测到，请确保 exiftool.exe 位于 ffmpeg 同目录或系统 PATH 中");
            }

            var args = BuildArguments(filePath, options);
            logCallback?.Invoke($"[exiftool] {args}\n");

            var psi = new ProcessStartInfo
            {
                FileName = _detectedPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var p = Process.Start(psi);
            if (p == null)
                throw new InvalidOperationException("无法启动 exiftool 进程");

            // 事件驱动读取，避免缓冲区死锁
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();

            var stdoutStr = stdout.ToString().Trim();
            var stderrStr = stderr.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(stdoutStr))
                logCallback?.Invoke(stdoutStr);
            if (!string.IsNullOrWhiteSpace(stderrStr))
                logCallback?.Invoke(stderrStr);

            return p.ExitCode;
        }

        /// <summary>
        /// 判断当前选项是否需要调用 exiftool
        /// </summary>
        public static bool NeedsProcessing(Models.FfmpegOptions options)
        {
            return options.StripExifGps ||
                   options.StripExifTime ||
                   options.StripExifCamera ||
                   options.StripExifAll ||
                   options.StripXmp;
        }

        /// <summary>
        /// 将源文件的所有元数据复制到目标文件（保留目标文件的像素数据不变）。
        /// 用于外部工具（cjxl/cjpegli）编码后恢复丢失的元数据。
        /// 命令：exiftool -overwrite_original -TagsFromFile source -all:all target
        /// </summary>
        public static async Task<int> CopyMetadataAsync(
            string sourcePath, string targetPath,
            Action<string>? logCallback = null)
        {
            if (_detectedPath == null)
            {
                Detect();
                if (_detectedPath == null) return -1;
            }

            // -all:all 复制所有可复制标签，但 ICC_Profile 等二进制块可能被跳过，
            // 因此显式追加 -ICC_Profile 确保色彩配置文件也被复制
            var args = $"-overwrite_original -m -TagsFromFile \"{sourcePath}\" " +
                       $"-all:all -ICC_Profile \"{targetPath}\"";
            logCallback?.Invoke($"[exiftool] 复制元数据: {Path.GetFileName(sourcePath)} → {Path.GetFileName(targetPath)}\n");

            return await RunRawAsync(args, logCallback);
        }

        /// <summary>
        /// 安全复制元数据：仅复制 EXIF/IPTC/XMP 等描述性元数据，
        /// 跳过 ICC_Profile、ColorSpace、色彩相关标签，保护编码器写入的色彩元数据。
        /// 命令：exiftool -overwrite_original -TagsFromFile source
        ///       -EXIF:all -IPTC:all -XMP:all -MakerNotes:all -GPS:all
        ///       --ColorSpace --ICC_Profile --ColorSpaceData
        ///       --ColorPrimaries --TransferFunction --ColorMatrix
        ///       target
        /// </summary>
        public static async Task<int> CopyMetadataSafeAsync(
            string sourcePath, string targetPath,
            Action<string>? logCallback = null)
        {
            if (_detectedPath == null)
            {
                Detect();
                if (_detectedPath == null) return -1;
            }

            // 仅复制描述性元数据组，排除色彩相关标签和 TIFF 结构标签
            // 注意：每个 --TAG 表示"从复制列表中排除该标签"
            var args = $"-overwrite_original -m -TagsFromFile \"{sourcePath}\" " +
                       $"-EXIF:all -IPTC:all -XMP:all -MakerNotes:all -GPS:all " +
                       $"--ColorSpace --ICC_Profile --ColorSpaceData " +
                       $"--ColorPrimaries --TransferFunction --ColorMatrix " +
                       $"--ProfileDescription --ProfileCopyright " +
                       $"--StripOffsets --StripByteCounts --RowsPerStrip " +
                       $"--TileOffsets --TileByteCounts --TileWidth --TileLength " +
                       $"--Compression --Predictor --PhotometricInterpretation " +
                       $"--SamplesPerPixel --BitsPerSample --PlanarConfiguration " +
                       $"\"{targetPath}\"";
            logCallback?.Invoke($"[exiftool] 安全复制元数据（已排除色彩标签，保护编码器输出）: {Path.GetFileName(sourcePath)} → {Path.GetFileName(targetPath)}\n");

            return await RunRawAsync(args, logCallback);
        }

        /// <summary>仅复制 ICC Profile（安全模式下补偿色彩配置文件）</summary>
        public static async Task<int> CopyIccProfileAsync(
            string sourcePath, string targetPath,
            Action<string>? logCallback = null)
        {
            if (_detectedPath == null)
            {
                Detect();
                if (_detectedPath == null) return -1;
            }

            var args = $"-overwrite_original -m -TagsFromFile \"{sourcePath}\" " +
                       $"-ICC_Profile \"{targetPath}\"";
            logCallback?.Invoke($"[exiftool] 恢复 ICC Profile: {Path.GetFileName(sourcePath)} → {Path.GetFileName(targetPath)}\n");

            return await RunRawAsync(args, logCallback);
        }

        /// <summary>将外部 ICC 文件嵌入到输出图片中（用户指定的 ICC 配置文件）</summary>
        /// <param name="iccFilePath">ICC 配置文件路径 (.icc / .icm)</param>
        /// <param name="targetPath">要嵌入 ICC 的目标图片路径</param>
        /// <param name="logCallback">日志回调</param>
        /// <returns>exiftool 退出码，0=成功</returns>
        public static async Task<int> EmbedIccProfileFromFileAsync(
            string iccFilePath, string targetPath,
            Action<string>? logCallback = null)
        {
            if (_detectedPath == null)
            {
                Detect();
                if (_detectedPath == null) return -1;
            }

            // exiftool 从文件读取 ICC 二进制并写入目标: -icc_profile<=file.icc
            var args = $"-overwrite_original -m " +
                       $"-icc_profile<=\"{iccFilePath}\" " +
                       $"\"{targetPath}\"";
            logCallback?.Invoke($"[exiftool] 嵌入 ICC Profile: {Path.GetFileName(iccFilePath)} → {Path.GetFileName(targetPath)}\n");

            return await RunRawAsync(args, logCallback);
        }

        /// <summary>为 JPEG 添加 JFIF 头（提高手机兼容性）</summary>
        public static async Task EnsureJfifHeaderAsync(string targetPath)
        {
            if (_detectedPath == null)
            {
                Detect();
                if (_detectedPath == null) return;
            }
            try
            {
                var args = $"-overwrite_original -JFIFVersion=1.02 \"{targetPath}\"";
                await RunRawAsync(args, null);
            }
            catch { }
        }

        /// <summary>读取单个标签值</summary>
        public static async Task<string?> GetTagAsync(string path, string tag)
        {
            if (_detectedPath == null)
            {
                Detect();
                if (_detectedPath == null) return null;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _detectedPath,
                    Arguments = $"-{tag} -s -s -s \"{path}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                var output = (await p.StandardOutput.ReadToEndAsync()).Trim();
                await p.WaitForExitAsync();
                return string.IsNullOrWhiteSpace(output) ? null : output;
            }
            catch { return null; }
        }

        /// <summary>执行原始 exiftool 命令（内部用）</summary>
        internal static async Task<int> RunRawAsync(string args, Action<string>? logCallback = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _detectedPath!,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var p = Process.Start(psi);
            if (p == null) return -1;

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();

            var stdoutStr = stdout.ToString().Trim();
            var stderrStr = stderr.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(stdoutStr))
                logCallback?.Invoke(stdoutStr + "\n");
            if (!string.IsNullOrWhiteSpace(stderrStr))
                logCallback?.Invoke(stderrStr + "\n");

            return p.ExitCode;
        }

        // ──────── 元数据编辑功能 ────────

        /// <summary>元数据分类定义</summary>
        public enum MetadataCategory
        {
            基本信息, 日期时间, 相机信息, 拍摄参数, GPS位置,
            图片属性, IPTC信息, XMP信息, 色彩配置
        }

        /// <summary>元数据字段定义：显示名 → (exiftool标签名, 分类)</summary>
        public static readonly Dictionary<string, (string TagName, MetadataCategory Category)> MetadataFields = new()
        {
            // ── 基本信息 (17) ──
            ["标题"]           = ("Title",            MetadataCategory.基本信息),
            ["描述"]           = ("Description",      MetadataCategory.基本信息),
            ["作者"]           = ("Author",           MetadataCategory.基本信息),
            ["艺术家"]         = ("Artist",           MetadataCategory.基本信息),
            ["版权"]           = ("Copyright",        MetadataCategory.基本信息),
            ["注释"]           = ("Comment",          MetadataCategory.基本信息),
            ["关键词"]         = ("Keywords",         MetadataCategory.基本信息),
            ["评级"]           = ("Rating",           MetadataCategory.基本信息),
            ["文档名称"]       = ("DocumentName",     MetadataCategory.基本信息),
            ["软件"]           = ("Software",         MetadataCategory.基本信息),
            ["主题"]           = ("Subject",          MetadataCategory.基本信息),
            ["说明"]           = ("Instructions",     MetadataCategory.基本信息),
            ["类别"]           = ("Category",         MetadataCategory.基本信息),
            ["子类别"]         = ("SupplementalCategories", MetadataCategory.基本信息),
            ["来源"]           = ("Source",           MetadataCategory.基本信息),
            ["署名"]           = ("Credit",           MetadataCategory.基本信息),
            ["原始文件名"]     = ("OriginalFileName", MetadataCategory.基本信息),

            // ── 日期时间 (6) ──
            ["拍摄日期"]       = ("DateTimeOriginal",  MetadataCategory.日期时间),
            ["创建日期"]       = ("CreateDate",        MetadataCategory.日期时间),
            ["修改日期"]       = ("ModifyDate",        MetadataCategory.日期时间),
            ["数字化日期"]     = ("DigitizationDate",  MetadataCategory.日期时间),
            ["获取日期"]       = ("DateAcquired",      MetadataCategory.日期时间),
            ["GPS 日期"]       = ("GPSDateTime",       MetadataCategory.日期时间),

            // ── 相机信息 (10) ──
            ["相机制造商"]     = ("Make",              MetadataCategory.相机信息),
            ["相机型号"]       = ("Model",             MetadataCategory.相机信息),
            ["相机序列号"]     = ("BodySerialNumber",  MetadataCategory.相机信息),
            ["相机所有者"]     = ("CameraOwnerName",   MetadataCategory.相机信息),
            ["镜头制造商"]     = ("LensMake",          MetadataCategory.相机信息),
            ["镜头型号"]       = ("LensModel",         MetadataCategory.相机信息),
            ["镜头序列号"]     = ("LensSerialNumber",  MetadataCategory.相机信息),
            ["镜头规格"]       = ("LensSpecification", MetadataCategory.相机信息),
            ["镜头ID"]         = ("LensID",            MetadataCategory.相机信息),
            ["镜头信息"]       = ("LensInfo",          MetadataCategory.相机信息),

            // ── 拍摄参数 (12) ──
            ["焦距"]           = ("FocalLength",             MetadataCategory.拍摄参数),
            ["35mm 等效焦距"]  = ("FocalLengthIn35mmFormat", MetadataCategory.拍摄参数),
            ["光圈"]           = ("FNumber",                 MetadataCategory.拍摄参数),
            ["最大光圈"]       = ("MaxApertureValue",        MetadataCategory.拍摄参数),
            ["ISO"]            = ("ISO",                     MetadataCategory.拍摄参数),
            ["曝光时间"]       = ("ExposureTime",            MetadataCategory.拍摄参数),
            ["快门速度"]       = ("ShutterSpeedValue",       MetadataCategory.拍摄参数),
            ["曝光补偿"]       = ("ExposureBiasValue",       MetadataCategory.拍摄参数),
            ["曝光程序"]       = ("ExposureProgram",         MetadataCategory.拍摄参数),
            ["曝光模式"]       = ("ExposureMode",            MetadataCategory.拍摄参数),
            ["测光模式"]       = ("MeteringMode",            MetadataCategory.拍摄参数),
            ["场景类型"]       = ("SceneCaptureType",        MetadataCategory.拍摄参数),
            ["感光方式"]       = ("SensingMethod",           MetadataCategory.拍摄参数),
            ["闪光灯"]         = ("Flash",                   MetadataCategory.拍摄参数),
            ["白平衡"]         = ("WhiteBalance",            MetadataCategory.拍摄参数),
            ["光源"]           = ("LightSource",             MetadataCategory.拍摄参数),
            ["对比度"]         = ("Contrast",                MetadataCategory.拍摄参数),
            ["饱和度"]         = ("Saturation",              MetadataCategory.拍摄参数),
            ["锐度"]           = ("Sharpness",               MetadataCategory.拍摄参数),

            // ── GPS 位置 (8) ──
            ["GPS 纬度"]       = ("GPSLatitude",       MetadataCategory.GPS位置),
            ["GPS 经度"]       = ("GPSLongitude",      MetadataCategory.GPS位置),
            ["GPS 海拔"]       = ("GPSAltitude",       MetadataCategory.GPS位置),
            ["GPS 纬度参考"]   = ("GPSLatitudeRef",    MetadataCategory.GPS位置),
            ["GPS 经度参考"]   = ("GPSLongitudeRef",   MetadataCategory.GPS位置),
            ["GPS 海拔参考"]   = ("GPSAltitudeRef",    MetadataCategory.GPS位置),
            ["GPS 地图基准"]   = ("GPSMapDatum",       MetadataCategory.GPS位置),
            ["GPS 处理方法"]   = ("GPSProcessingMethod",MetadataCategory.GPS位置),

            // ── 图片属性 (9) ──
            ["方向"]           = ("Orientation",       MetadataCategory.图片属性),
            ["图像描述"]       = ("ImageDescription",  MetadataCategory.图片属性),
            ["用户注释"]       = ("UserComment",       MetadataCategory.图片属性),
            ["图像宽度"]       = ("ImageWidth",        MetadataCategory.图片属性),
            ["图像高度"]       = ("ImageHeight",       MetadataCategory.图片属性),
            ["位深"]           = ("BitsPerSample",     MetadataCategory.图片属性),
            ["压缩"]           = ("Compression",       MetadataCategory.图片属性),
            ["分辨率单位"]     = ("ResolutionUnit",    MetadataCategory.图片属性),
            ["X 分辨率"]       = ("XResolution",       MetadataCategory.图片属性),
            ["Y 分辨率"]       = ("YResolution",       MetadataCategory.图片属性),

            // ── IPTC 信息 (10) ──
            ["IPTC 署名"]      = ("Byline",            MetadataCategory.IPTC信息),
            ["IPTC 署名头衔"]  = ("BylineTitle",       MetadataCategory.IPTC信息),
            ["IPTC 标题"]      = ("Headline",          MetadataCategory.IPTC信息),
            ["IPTC 说明"]      = ("Caption",           MetadataCategory.IPTC信息),
            ["IPTC 说明作者"]  = ("CaptionWriter",     MetadataCategory.IPTC信息),
            ["IPTC 特殊说明"]  = ("SpecialInstructions",MetadataCategory.IPTC信息),
            ["IPTC 国家"]      = ("Country",           MetadataCategory.IPTC信息),
            ["IPTC 省/州"]     = ("ProvinceState",     MetadataCategory.IPTC信息),
            ["IPTC 城市"]      = ("City",              MetadataCategory.IPTC信息),
            ["IPTC 地点"]      = ("Location",          MetadataCategory.IPTC信息),
            ["IPTC 版权声明"]  = ("CopyrightNotice",   MetadataCategory.IPTC信息),

            // ── XMP 信息 (8) ──
            ["XMP 创建者"]     = ("Creator",           MetadataCategory.XMP信息),
            ["XMP 创建工具"]   = ("CreatorTool",       MetadataCategory.XMP信息),
            ["XMP 权利"]       = ("Rights",            MetadataCategory.XMP信息),
            ["XMP 网页声明"]   = ("WebStatement",      MetadataCategory.XMP信息),
            ["XMP 标记"]       = ("Label",             MetadataCategory.XMP信息),
            ["XMP 评级"]       = ("Rating",            MetadataCategory.XMP信息),
            ["XMP 标识符"]     = ("Identifier",        MetadataCategory.XMP信息),
            ["XMP 使用条款"]   = ("UsageTerms",        MetadataCategory.XMP信息),

            // ── 色彩配置 (6) ──
            ["色彩空间"]       = ("ColorSpace",        MetadataCategory.色彩配置),
            ["Gamma"]          = ("Gamma",             MetadataCategory.色彩配置),
            ["白点"]           = ("WhitePoint",        MetadataCategory.色彩配置),
            ["原色"]           = ("PrimaryChromaticities", MetadataCategory.色彩配置),
            ["ICC 配置文件"]   = ("ICCProfile",        MetadataCategory.色彩配置),
            ["色彩模式"]       = ("ColorMode",         MetadataCategory.色彩配置),
        };

        /// <summary>安全地将 JsonElement 转为字符串（处理数字/布尔/null 类型）</summary>
        private static string JsonElementToString(System.Text.Json.JsonElement element) => element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => element.GetString() ?? "",
            System.Text.Json.JsonValueKind.Null => "",
            System.Text.Json.JsonValueKind.True => "true",
            System.Text.Json.JsonValueKind.False => "false",
            _ => element.GetRawText()
        };

        /// <summary>
        /// 读取文件现有元数据（JSON 格式解析），返回 显示名→值 的字典
        /// </summary>
        public static async Task<Dictionary<string, string>> ReadMetadataAsync(string filePath)
        {
            var result = new Dictionary<string, string>();
            foreach (var key in MetadataFields.Keys)
                result[key] = "";

            // 确保已检测 exiftool
            if (_detectedPath == null)
            {
                Detect();
                if (_detectedPath == null)
                    throw new InvalidOperationException("exiftool 未检测到，请确保 exiftool.exe 位于 ffmpeg 同目录或系统 PATH 中");
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _detectedPath,
                    Arguments = $"-json -G \"{filePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var p = Process.Start(psi);
                if (p == null)
                    throw new InvalidOperationException("无法启动 exiftool 进程");

                // 事件驱动读取，避免 stdout/stderr 缓冲区死锁
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                p.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                await p.WaitForExitAsync();

                var stdoutStr = stdout.ToString().Trim();
                var stderrStr = stderr.ToString().Trim();

                if (p.ExitCode != 0)
                    throw new InvalidOperationException($"exiftool 退出码 {p.ExitCode}: {stderrStr}");

                if (string.IsNullOrWhiteSpace(stdoutStr)) return result;

                using var doc = JsonDocument.Parse(stdoutStr);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                    return result;

                var first = root[0];

                foreach (var field in MetadataFields)
                {
                    var tagName = field.Value.TagName;
                    foreach (var prop in first.EnumerateObject())
                    {
                        if (prop.Name.EndsWith($":{tagName}", StringComparison.OrdinalIgnoreCase) ||
                            prop.Name.Equals(tagName, StringComparison.OrdinalIgnoreCase))
                        {
                            result[field.Key] = JsonElementToString(prop.Value);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"读取元数据失败: {ex.Message}", ex);
            }

            return result;
        }

        /// <summary>
        /// 将元数据写入文件，返回 (退出码, 输出信息)
        /// </summary>
        public static async Task<(int ExitCode, string Output)> WriteMetadataAsync(
            string filePath, Dictionary<string, string> tags, bool keepBackup = true)
        {
            // 确保已检测 exiftool
            if (_detectedPath == null)
            {
                Detect();
                if (_detectedPath == null)
                    return (-1, "exiftool 未检测到，请确保 exiftool.exe 位于 ffmpeg 同目录或系统 PATH 中");
            }

            var argList = new List<string>();
            if (!keepBackup)
                argList.Add("-overwrite_original");

            foreach (var (displayName, value) in tags)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                if (!MetadataFields.TryGetValue(displayName, out var fieldDef))
                    continue;
                argList.Add($"-{fieldDef.TagName}={value}");
            }

            argList.Add($"\"{filePath}\"");

            var args = string.Join(" ", argList);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _detectedPath,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var p = Process.Start(psi);
                if (p == null)
                    throw new InvalidOperationException("无法启动 exiftool 进程");

                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                p.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                await p.WaitForExitAsync();

                var output = (stdout.ToString() + "\n" + stderr.ToString()).Trim();
                return (p.ExitCode, output);
            }
            catch (Exception ex)
            {
                return (-1, $"写入元数据异常: {ex.Message}");
            }
        }
    }
}
