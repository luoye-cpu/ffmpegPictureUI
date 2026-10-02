using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FfmpegGui.Services
{
    /// <summary>
    /// 产物侧 EXIF IFD1 缩略图：从**最终产物**缩一张小 JPEG，再用 exiftool 后置嵌进去。
    ///
    /// <para><b>为什么从产物缩而不是从源缩</b>：源可能是 RAW/DNG（解码贵），也可能是 16-bit 或
    /// 已被色彩管线换过色域的像素。缩略图的语义是「这张图的缩小预览」，必须与出口像素一致。
    /// 从产物取 ⇒ 几何、位深、色彩都已定样，不必再猜一遍。</para>
    ///
    /// <para><b>调用时刻</b>：元数据阶段（<c>RestoreMetadataAsync</c> 之后、色彩后置之前）。
    /// 本仓的不变量是「元数据先写、色彩标签最后定」，倒过来会让后置步骤的判据看错现场。</para>
    ///
    /// <para><b>兑现性由实测决定</b>（见 <see cref="FormatCapabilities.SupportsThumbnail"/>）：
    /// jpg / png / apng / webp / avif / jxl 已实测「写入后能回读到与声明等长的图像数据」；
    /// tiff / gif / jxr / dng 实测均被 exiftool 拒（「0 image files updated」**而退出码仍是 0**
    /// ⇒ 判据只能看回读字节，不能看 rc）。GIF 结构上没有 EXIF 槽位；JXR 收得下 ICC 却收不下 IFD1。</para>
    ///
    /// <para><b>已知局限（写在这里是为了不被当成已解决）</b>：缩略图是 ffmpeg 现编的 8-bit JPEG，
    /// EXIF IFD1 没有 ICC 槽位 ⇒ 广色域产物的小预览会按 sRGB 解读而略欠饱和。它<b>不参与</b>出口
    /// 色彩裁决（<c>DecideOutputColor</c> 只对主图负责），成功日志里会带上这条口径。</para>
    /// </summary>
    public static class ThumbnailService
    {
        /// <summary>EXIF 基线缩略图的长边（160x128）。超过它仍会写（实测 exiftool 收 320x240），但会出声。</summary>
        public const int BaselineLongEdge = 160;
        public const int DefaultLongEdge = BaselineLongEdge;
        public const int DefaultQuality = 75;
        public const int MinLongEdge = 16;
        public const int MaxLongEdge = 512;

        /// <summary>长边取值钳制（UI/CLI 两条入口共用同一份，避免两处口径漂移）。</summary>
        public static int ClampLongEdge(int requested)
            => Math.Min(MaxLongEdge, Math.Max(MinLongEdge, requested <= 0 ? DefaultLongEdge : requested));

        /// <summary>
        /// 质量 1..100 → ffmpeg 的 <c>-qscale:v</c>（2=最好 / 31=最差，反向线性）。
        /// 做成纯函数是为了让探针能直接对账，而不是从命令行倒推。
        /// </summary>
        public static int MapQualityToQscale(int quality)
        {
            var q = Math.Min(100, Math.Max(1, quality));
            var qscale = (int)Math.Round(31.0 - (q - 1) * 29.0 / 99.0, MidpointRounding.AwayFromZero);
            return Math.Min(31, Math.Max(2, qscale));
        }

        /// <summary>
        /// 只缩不放、保长宽、边长取偶数：长边维度取 <c>min(源, S)</c>，另一维 <c>-2</c> 由 ffmpeg 按
        /// 比例算并凑偶。写成纯函数是因为这条 filter 是本功能唯一的几何真值。
        /// </summary>
        public static string BuildScaleFilter(int longEdge)
        {
            var s = longEdge.ToString(CultureInfo.InvariantCulture);
            return $"scale='if(gt(iw,ih),min(iw,{s}),-2)':'if(gt(iw,ih),-2,min(ih,{s}))'";
        }

        /// <summary>缩略图编码命令行（从产物 → 临时 JPEG）。</summary>
        public static string BuildEncodeArgs(string fromPath, string thumbPath, int longEdge, int quality)
            => $"-y -hide_banner -loglevel error -i \"{fromPath}\" "
             + $"-vf \"{BuildScaleFilter(longEdge)}\" "
             + $"-frames:v 1 -qscale:v {MapQualityToQscale(quality).ToString(CultureInfo.InvariantCulture)} "
             + $"-update 1 \"{thumbPath}\"";

        /// <summary>exiftool 嵌入命令行。</summary>
        public static string BuildEmbedArgs(string thumbPath, string targetPath)
            => $"-overwrite_original -m -ThumbnailImage<=\"{thumbPath}\" \"{targetPath}\"";

        /// <summary>
        /// 是否该跳过缩略图并出声。隐私优先：<c>StripAll</c> 与 <c>StripExifAll</c> 都是用户
        /// 「不要把描述性元数据留在产物里」的明确表达，缩略图属于描述性元数据。
        /// </summary>
        public static string? PrivacySkipReason(Models.FfmpegOptions opts)
            => opts.MetadataMode == Models.MetadataMode.StripAll ? "metadata mode is StripAll"
             : opts.StripExifAll ? "EXIF is being stripped"
             : null;

        /// <summary>把格式串归一到能力表的键（<c>.JPG</c> / <c>jpeg</c> / <c>tif</c> 都落到同一个条目）。</summary>
        public static string CapabilityKey(string? format)
        {
            var f = (format ?? "").Trim().TrimStart('.').ToLowerInvariant();
            return f switch
            {
                "jpeg" or "jpe" => "jpg",
                "tif" => "tiff",
                _ => f
            };
        }

        /// <summary>
        /// 生成并嵌入缩略图。任何一步不成立都**出声**，绝不静默产出一张没有缩略图的图。
        /// </summary>
        /// <returns>0=已嵌入；非 0=未嵌入（原因已在日志里点名）</returns>
        public static async Task<int> ApplyAsync(
            string outputPath, Models.FfmpegOptions opts,
            Action<string>? log = null, CancellationToken ct = default)
        {
            var emit = log ?? (_ => { });
            if (!opts.EnableThumbnail) return 0;

            var key = CapabilityKey(opts.Format);
            var caps = FormatCapabilitiesService.GetCapabilities(key);
            if (caps is not { SupportsThumbnail: true })
            {
                emit($"[thumbnail] ignored: thumbnail is not expressible on container '{key}' "
                   + "— this build has no verified EXIF IFD1 write path for it, so no thumbnail is emitted.\n");
                return -1;
            }

            if (PrivacySkipReason(opts) is string why)
            {
                emit($"[thumbnail] skipped: {why} (a thumbnail is descriptive metadata; privacy wins)\n");
                return -2;
            }

            if (!ExifToolService.IsAvailable)
            {
                emit("[thumbnail] skipped: exiftool not available (the embed step needs it)\n");
                return -3;
            }

            var longEdge = ClampLongEdge(opts.ThumbnailLongEdge);
            if (longEdge > BaselineLongEdge)
                emit($"[thumbnail] note: long edge {longEdge}px exceeds the EXIF baseline {BaselineLongEdge}x128; "
                   + "some viewers only read baseline-sized thumbnails\n");

            if (!File.Exists(outputPath))
            {
                emit("[thumbnail] skipped: output file not found (nothing to shrink from)\n");
                return -4;
            }

            var thumbPath = Path.Combine(PlatformServices.GetTempDir(), $"thumb_{Guid.NewGuid():N}.jpg");
            try
            {
                var ffmpeg = AppSettingsService.Current.FfmpegPath;
                var encExit = await FfmpegRunner.RunAsync(
                    BuildEncodeArgs(outputPath, thumbPath, longEdge, opts.ThumbnailQuality),
                    emit, ffmpeg, ct);
                if (encExit != 0 || !File.Exists(thumbPath) || new FileInfo(thumbPath).Length == 0)
                {
                    emit($"[thumbnail] failed: thumbnail encode rc={encExit} (no embed attempted)\n");
                    return -5;
                }

                var embedExit = await ExifToolService.EmbedThumbnailAsync(thumbPath, outputPath, emit, ct);
                if (embedExit != 0)
                {
                    emit($"[thumbnail] failed: exiftool embed rc={embedExit} — output has NO thumbnail\n");
                    return embedExit;
                }

                emit($"[thumbnail] embedded: {new FileInfo(thumbPath).Length} B JPEG thumbnail, "
                   + $"long edge {longEdge}, quality {opts.ThumbnailQuality}\n");
                return 0;
            }
            finally
            {
                try { if (File.Exists(thumbPath)) File.Delete(thumbPath); } catch { }
            }
        }
    }
}
