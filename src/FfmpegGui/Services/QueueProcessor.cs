using FfmpegGui.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FfmpegGui.Services
{
    public class QueueProcessor
    {
        private readonly ConcurrentQueue<QueueItem> _queue = new();
        private readonly object _queueLock = new();
        private readonly List<Task> _running = new();
        private CancellationTokenSource? _cts;
        private int _concurrency = 2;
        // 请求在当前队列完成后优雅停止（不立刻 Cancel）
        private volatile bool _stopAfterQueueRequested = false;

        /// <summary>调度器是否正在运行（有活跃的 CTS 且未被取消）</summary>
        public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;
        private readonly Action<QueueItem> _onItemUpdated;
        private readonly Action? _onQueueStopped;
        // GainMapDecoder 产生的临时目录（任务结束时清理）
        private readonly List<string> _pendingRawDirs = new();

        public QueueProcessor(Action<QueueItem> onItemUpdated, Action? onStopped = null)
        {
            _onItemUpdated = onItemUpdated;
            _onQueueStopped = onStopped;
        }

        public void Add(QueueItem item)
        {
            item.Status = "待处理";
            _queue.Enqueue(item);
            _onItemUpdated?.Invoke(item);
        }

        public void SetConcurrency(int c) => _concurrency = Math.Max(1, c);

        public void Start(int? concurrency = null)
        {
            if (concurrency.HasValue)
                _concurrency = Math.Max(1, concurrency.Value);
            // 动态限制：根据可用内存调整最大并发数（每任务预留 200MB）
            _concurrency = ClampConcurrencyByMemory(_concurrency);
            if (_cts != null)
            {
                _cts.Cancel();
                _cts = null;
            }
            _stopAfterQueueRequested = false;
            _cts = new CancellationTokenSource();
            Task.Run(() => ProcessAsync(_cts.Token));
        }

        /// <summary>根据系统可用内存动态限制并发数（每任务预留 200MB，下限 1，上限不变）</summary>
        private static int ClampConcurrencyByMemory(int requested)
        {
            try
            {
                var memInfo = GC.GetGCMemoryInfo();
                var availableMB = memInfo.TotalAvailableMemoryBytes / (1024 * 1024);
                var maxByMemory = (int)Math.Max(1, availableMB / 200);
                return Math.Max(1, Math.Min(requested, maxByMemory));
            }
            catch { return requested; }
        }

        /// <summary>
        /// 将所有"已停止"和"失败"的项重新加入队列（状态复位为"待处理"）。
        /// 调用方负责在调用此方法前停止正在运行的队列。
        /// </summary>
        public void RequeueStoppedAndFailed(List<QueueItem> allItems)
        {
            foreach (var item in allItems)
            {
                if (item.Status == "已停止" || (item.Status.StartsWith("失败") && item.ExitCode != 0))
                {
                    item.Status = "待处理";
                    item.IsCancelled = false;
                    item.ExitCode = null;
                    item.Log += "[重新排队]\n";
                    _queue.Enqueue(item);
                    _onItemUpdated?.Invoke(item);
                }
            }
        }

        public void Stop()
        {
            // 立即取消 CTS 并清除优雅停止请求
            _stopAfterQueueRequested = false;
            _cts?.Cancel();
            _cts = null;

            // 清理 GainMapDecoder 遗留的临时目录
            CleanupPendingDirs();
        }

        /// <summary>清理 GainMapDecoder 产生的临时目录（任务结束后不再需要）</summary>
        private void CleanupPendingDirs()
        {
            lock (_pendingRawDirs)
            {
                foreach (var dir in _pendingRawDirs)
                {
                    try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
                }
                _pendingRawDirs.Clear();
            }
        }

        /// <summary>
        /// 请求在当前队列处理完后优雅停止（不会立刻中断正在运行的任务）
        /// </summary>
        public void StopAfterCurrentQueue()
        {
            _stopAfterQueueRequested = true;
        }

        /// <summary>
        /// 取消已请求的“完成当前队列后停止”请求（如果尚未生效）。
        /// </summary>
        public void CancelStopAfterCurrentQueue()
        {
            _stopAfterQueueRequested = false;
        }

        /// <summary>
        /// 清空待处理（尚未开始执行）的队列项，并返回被清空的项列表。
        /// 注意：调用方负责处理 UI 更新，此处不再逐个回调 _onItemUpdated。
        /// </summary>
        public List<QueueItem> ClearPending()
        {
            var removed = new List<QueueItem>();
            lock (_queueLock)
            {
                while (_queue.TryDequeue(out var item))
                {
                    item.IsCancelled = true;
                    item.Status = "已删除";
                    removed.Add(item);
                }
            }
            return removed;
        }

        private async Task ProcessAsync(CancellationToken ct)
        {
            var sem = new SemaphoreSlim(_concurrency);
            var tasks = new List<Task>();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                // 定期清理已完成任务（每 50 次迭代清理一次）
                if (tasks.Count > 0 && tasks.Count % 50 == 0)
                    tasks.RemoveAll(t => t.IsCompleted);

                // 如果已请求在当前队列完成后停止，且当前无待处理项且所有已启动任务均完成，则退出循环
                if (_stopAfterQueueRequested && _queue.IsEmpty && tasks.All(t => t.IsCompleted))
                {
                    break;
                }

                QueueItem? item = null;
                lock (_queueLock)
                {
                    if (_queue.TryDequeue(out var it)) item = it;
                }
                if (item != null)
                {
                    // 跳过已停止的任务
                    if (item.IsCancelled)
                    {
                        item.Status = "已删除";
                        _onItemUpdated?.Invoke(item);
                        continue;
                    }

                    await sem.WaitAsync(ct).ConfigureAwait(false);
                    var captured = item; // capture for closure
                    var t = Task.Run(async () =>
                    {
                        try
                        {
                            captured.Status = "处理中";
                            captured.StartedAt = DateTimeOffset.UtcNow;
                            _onItemUpdated?.Invoke(captured);

                            // 确保输出目录存在 —— 先将输出路径标准化为绝对路径，避免相对路径在不同进程中导致位置不一致
                            string finalOutputPath;
                            try
                            {
                                finalOutputPath = Path.GetFullPath(item.OutputPath);
                            }
                            catch
                            {
                                finalOutputPath = item.OutputPath;
                            }

                            var outDir = Path.GetDirectoryName(finalOutputPath);
                            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                                Directory.CreateDirectory(outDir);

                            // ── 根据编码器后端调度 ──
                            var backend = captured.Options.EncoderBackend;
                            var inputExt = Path.GetExtension(captured.InputPath).ToLowerInvariant();

                            // Ultra HDR JPEG 输入：GainMapDecoder (纯 C#) 应用增益图恢复 HDR 线性像素
                            // JXR 格式由 ProcessJxrAsync 自行处理（直接 RAW→TIFF 管道，保 HDR 元数据）
                            var isJxrOutput = captured.Options.Format.Equals("jxr", StringComparison.OrdinalIgnoreCase);
                            if (inputExt is ".jpg" or ".jpeg" && !isJxrOutput
                                && await IsUltraHdrJpegAsync(captured.InputPath))
                            {
                                var skipForJxl = captured.Options.Format.Equals("jxl", StringComparison.OrdinalIgnoreCase)
                                    && !captured.Options.JxlPreserveUltrahdr;
                                if (skipForJxl)
                                {
                                    captured.Log += "[UltraHDR] 输入为 Ultra HDR JPEG，但已关闭「保留增益图」→ 按 SDR 基础图处理\n";
                                }
                                else
                                {
                                    captured.Log += "[UltraHDR] 检测到 Ultra HDR JPEG，GainMapDecoder 应用增益图恢复 HDR...\n";
                                    _onItemUpdated?.Invoke(captured);
                                    var decodedPath = await DecodeUltraHdrToLinearAsync(captured, captured.InputPath, ct);
                                    if (decodedPath != null)
                                    {
                                        captured.InputPath = decodedPath;
                                        captured.Options.DecodedUltraHdrColorSpace = "HDRLinear";
                                        captured.Log += "[UltraHDR] ✅ 已解码为线性 HDR 像素，继续编码\n";
                                    }
                                    else
                                    {
                                        captured.Log += "[UltraHDR] ⚠️ 增益图应用失败，按 SDR 基础图处理\n";
                                    }
                                    _onItemUpdated?.Invoke(captured);
                                }
                            }

                            // ── RAW 预处理：Bayer 传感器数据去马赛克 ──
                            // dngtool 将相机 RAW → 线性 16-bit TIFF，解决 ffmpeg 无法处理
                            // Bayer RAW 和色彩映射错误的问题。
                            // 注意: DNG 输出目标跳过此步骤——ProcessDngAsync 需要原始传感器数据
                            // (Bayer 相位/白平衡)，必须由 dngtool 直接读取输入 RAW 文件编码。
                            var isDngOutput = captured.Options.Format.Equals("dng", StringComparison.OrdinalIgnoreCase);
                            if (RawService.IsRawFile(captured.InputPath) && RawService.IsAvailable
                                && !isDngOutput)
                            {
                                captured.Log += "[RAW] 检测到 RAW 文件，正在进行 dngtool 去马赛克预处理...\n";
                                _onItemUpdated?.Invoke(captured);
                                var rawTempDir = Path.Combine(PlatformServices.GetTempDir(), $"raw_{Guid.NewGuid():N}");
                                Directory.CreateDirectory(rawTempDir);
                                var rawTiff = Path.Combine(rawTempDir, $"{Path.GetFileNameWithoutExtension(captured.InputPath)}_raw.tiff");
                                var success = await RawService.PreProcessAsync(captured.InputPath, rawTiff, s =>
                                {
                                    captured.Log += s;
                                    _onItemUpdated?.Invoke(captured);
                                }, ct);
                                if (success)
                                {
                                    captured.Log += "[RAW] ✅ 预处理完成，使用线性 TIFF 继续编码。色彩空间: BT.709 + 线性传输。\n";
                                    captured.InputPath = rawTiff;
                                    // 仅在用户未手动设置色彩参数时覆盖（尊重用户意图）
                                    if (!captured.Options.UseAdvancedColorParameters
                                        && string.IsNullOrWhiteSpace(captured.Options.ColorPrimaries))
                                    {
                                        captured.Options.ColorPrimaries = "bt709";
                                        captured.Options.ColorTrc = "linear";
                                    }
                                    else
                                    {
                                        captured.Log += "[RAW] 用户已设置色彩参数，保留用户配置。\n";
                                    }
                                }
                                else
                                {
                                    captured.Log += "[RAW] ⚠️ 预处理失败，将尝试用 ffmpeg 直接解码（可能失败或色彩错误）。\n";
                                }
                                _onItemUpdated?.Invoke(captured);
                            }

                            // GIF → AVIF：FFmpeg 编码器丢弃 alpha，走 avifenc 两步法保留透明通道
                            if (inputExt == ".gif"
                                && captured.Options.Format.Equals("avif", StringComparison.OrdinalIgnoreCase)
                                && HasAvifencAvailable())
                            {
                                await ProcessGifToAvifViaAvifencAsync(captured, finalOutputPath, ct);
                            }
                            // AVIF → GIF/WebP：动画轨道分离+alphamerge 需两步法
                            else if (inputExt == ".avif"
                                && (captured.Options.Format.Equals("gif", StringComparison.OrdinalIgnoreCase)
                                    || captured.Options.Format.Equals("webp", StringComparison.OrdinalIgnoreCase)))
                            {
                                await ProcessAvifToGifWebpAsync(captured, finalOutputPath, ct);
                            }
                            else if (inputExt == ".jxl")
                            {
                                // JXL 输入：智能检测类型并选择最优路径（独立于编码器选择）
                                await ProcessJxlInputAsync(captured, finalOutputPath, ct);
                            }
                            else if (inputExt == ".jxr")
                            {
                                // JXR 输入：JxrDecApp 解码 → 根据目标格式选择编码器
                                await ProcessJxrInputAsync(captured, finalOutputPath, ct);
                            }
                            // Gain Map (Ultra HDR) JPEG：纯 C# GainMapEncoder + cjpegli
                            else if (captured.Options.JpegGainMap
                                && (captured.Options.Format == "jpg" || captured.Options.Format == "jpeg"))
                            {
                                await ProcessGainMapJpegAsync(captured, finalOutputPath, ct);
                            }
                            else if (backend == EncoderBackend.Cjxl)
                            {
                                await ProcessCjxlAsync(captured, finalOutputPath, ct);
                            }
                            else if (backend == EncoderBackend.Cjpegli)
                            {
                                await ProcessCjpegliAsync(captured, finalOutputPath, ct);
                            }
                            else if (backend == EncoderBackend.Jxr)
                            {
                                await ProcessJxrAsync(captured, finalOutputPath, ct);
                            }
                            else if (backend == EncoderBackend.Dng)
                            {
                                await ProcessDngAsync(captured, finalOutputPath, ct);
                            }
                            else
                            {
                                var args = FfmpegCommandBuilder.BuildArguments(captured.Options, captured.InputPath, finalOutputPath);
                                captured.Command = "ffmpeg " + args;
                                captured.Log += $"[cmd] ffmpeg {args}\n";
                                _onItemUpdated?.Invoke(captured);
                                var exitCode = await FfmpegRunner.RunAsync(args, s =>
                                {
                                    captured.Log += s;
                                    _onItemUpdated?.Invoke(captured);
                                }, AppSettingsService.Current.FfmpegPath, ct);
                                captured.ExitCode = exitCode;
                                captured.Status = exitCode == 0 ? "已完成" : $"失败 (退出码 {exitCode})";

                                // DNG/RAW 输入失败时给出提示
                                if (exitCode != 0 && RawService.IsRawFile(captured.InputPath))
                                {
                                    captured.Log += "[RAW] ⚠️ 文件转换失败。可能原因：\n";
                                    captured.Log += "[RAW]   - Bayer 传感器原始数据需 dngtool 去马赛克（已自动尝试）\n";
                                    captured.Log += "[RAW]   - 文件损坏或格式不受支持\n";
                                    captured.Log += "[RAW]   - 请确保 dngtool 已放入 PLAN/artifacts/ 目录\n";
                                }

                                // ── 统一元数据恢复 ──
                                // FFmpeg 的 -map_metadata 0 在不同 muxer 下行为不一致（尤其是 ICC Profile、
                                // XMP 包、Exif IFD 子标签等），因此对所有输出格式都走 exiftool 恢复，
                                // 确保 ICC 色彩描述、XMP、Exif 等完整保留。AVIF muxer 尤为严重，
                                // 其他格式（TIFF/PNG/WebP/JXL）也存在不同程度的丢失。
                                if (exitCode == 0)
                                    await RestoreMetadataAsync(captured, finalOutputPath);
                            }

                            // ── ExifTool 后处理 ──
                            if (captured.ExitCode == 0
                                && captured.Options.MetadataMode == Models.MetadataMode.PreserveAll
                                && ExifToolService.NeedsProcessing(captured.Options))
                            {
                                if (ExifToolService.IsAvailable)
                                {
                                    try
                                    {
                                        captured.Log += "[exiftool] 开始隐私清理...\n";
                                        _onItemUpdated?.Invoke(captured);
                                        var exifExit = await ExifToolService.RunAsync(
                                            finalOutputPath,
                                            captured.Options,
                                            s =>
                                            {
                                                captured.Log += s;
                                                _onItemUpdated?.Invoke(captured);
                                            });
                                        if (exifExit == 0)
                                            captured.Log += "[exiftool] 隐私清理完成\n";
                                        else
                                            captured.Log += $"[exiftool] 警告: 退出码 {exifExit}\n";
                                    }
                                    catch (Exception ex)
                                    {
                                        captured.Log += $"[exiftool] 错误: {ex.Message}\n";
                                    }
                                }
                                else
                                {
                                    captured.Log += "[exiftool] 未检测到 exiftool，已跳过元数据隐私清理。请安装 exiftool 并在设置中配置路径以启用该功能。\n";
                                    _onItemUpdated?.Invoke(captured);
                                }
                            }
                            // ── 追加 .png 后缀（JXL/AVIF 兼容性）──
                            if (captured.ExitCode == 0 && captured.Options.AppendPngExtension)
                            {
                                var fmt = captured.Options.Format.ToLower();
                                if (fmt is "avif" or "jxl")
                                {
                                    try
                                    {
                                        var newPath = finalOutputPath + ".png";
                                        File.Move(finalOutputPath, newPath);
                                        finalOutputPath = newPath;
                                        captured.OutputPath = newPath;
                                        captured.Log += $"[rename] 已追加 .png 后缀: {Path.GetFileName(newPath)}\n";
                                    }
                                    catch (Exception ex)
                                    {
                                        captured.Log += $"[rename] ⚠️ 追加 .png 后缀失败: {ex.Message}\n";
                                    }
                                }
                            }
                            captured.CompletedAt = DateTimeOffset.UtcNow;
                        }
                        catch (OperationCanceledException)
                        {
                            captured.Status = "已停止";
                            captured.CompletedAt = DateTimeOffset.UtcNow;
                        }
                        catch (Exception ex)
                        {
                            captured.Status = "失败: " + ex.Message;
                            captured.CompletedAt = DateTimeOffset.UtcNow;
                        }
                        finally
                        {
                            _onItemUpdated?.Invoke(captured);
                            sem.Release();
                        }
                    }, ct);

                    tasks.Add(t);
                }
                else
                {
                    // 空队列，短暂等待
                    await Task.Delay(200, ct).ConfigureAwait(false);
                }
            }

                // 等待正在运行的任务完成
                try { await Task.WhenAll(tasks.ToArray()); } catch { }
            }
            finally
            {
                try { _onQueueStopped?.Invoke(); } catch { }
            }
        }

        // ── 编码器后端专用处理方法 ──

        /// <summary>外部工具编码后恢复元数据（优先 exiftool，不可用时回退 ffmpeg）</summary>
        private async Task RestoreMetadataAsync(QueueItem item, string outputPath)
        {
            if (item.Options.MetadataMode != Models.MetadataMode.PreserveAll)
                return;

            var backend = item.Options.EncoderBackend;

            // ── 判断是否需要保护输出文件的色彩元数据 ──
            // 原则：如果用户手动指定了色彩参数，说明用户有明确的色彩意图，
            //       不应被源文件元数据覆盖；如果一切为 auto，则恢复源文件元数据。
            var userSpecifiedColor =
                item.Options.UseAdvancedColorParameters ||
                (!string.IsNullOrWhiteSpace(item.Options.ColorSpace)
                 && !item.Options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase));

            // 编码器后端保护：外部编码器/FFmpeg 已嵌入色彩信息
            // cjpegli 管道模式不嵌入任何色彩标签（无 JFIF/ICC），不应保护
            var encoderProtectsColor =
                backend == EncoderBackend.Cjxl ||
                backend == EncoderBackend.Jxr ||
                backend == EncoderBackend.Ffmpeg;

            // 综合判断：用户指定 或 编码器保护 → 安全模式
            var protectColorMetadata = userSpecifiedColor || encoderProtectsColor;

            // TIFF 特殊处理：TIFF 使用 ICC Profile 色彩管理，不支持视频风格 color_primaries。
            // 即使用户指定了色彩空间，也应保留源文件 ICC Profile（唯一色彩信息载体）。
            var isTiff = item.Options.Format.Equals("tiff", StringComparison.OrdinalIgnoreCase)
                      || item.Options.Format.Equals("tif", StringComparison.OrdinalIgnoreCase);
            if (isTiff && userSpecifiedColor)
            {
                item.Log += "[tiff] TIFF 格式使用 ICC Profile 色彩管理，将保留源文件 ICC 配置（不支持 color_primaries 模式）\n";
                protectColorMetadata = false; // 允许完整元数据复制（含 ICC Profile）
            }

            // ── 优先：exiftool ──
            if (ExifToolService.IsAvailable)
            {
                try
                {
                    if (protectColorMetadata)
                    {
                        var reason = userSpecifiedColor ? "用户指定色彩空间" : "编码器保护";
                        item.Log += $"[exiftool] 从源文件恢复元数据（安全模式：{reason}，不覆盖色彩标签）...\n";
                    }
                    else
                        item.Log += "[exiftool] 从源文件恢复元数据...\n";
                    _onItemUpdated?.Invoke(item);

                    var copyExit = protectColorMetadata
                        ? await ExifToolService.CopyMetadataSafeAsync(
                            item.InputPath, outputPath,
                            s => { item.Log += s; _onItemUpdated?.Invoke(item); })
                        : await ExifToolService.CopyMetadataAsync(
                            item.InputPath, outputPath,
                            s => { item.Log += s; _onItemUpdated?.Invoke(item); });

                    if (copyExit == 0)
                    {
                        // 安全模式下 ICC Profile 可能被排除，显式恢复。
                        // 但若用户手动指定了色彩空间，源 ICC 会与用户选择冲突 → 跳过。
                        if (protectColorMetadata && !userSpecifiedColor)
                        {
                            var iccExit = await ExifToolService.CopyIccProfileAsync(
                                item.InputPath, outputPath,
                                s => { item.Log += s; _onItemUpdated?.Invoke(item); });
                            if (iccExit == 0)
                                item.Log += "[exiftool] ICC Profile 已恢复\n";
                            else
                                item.Log += $"[exiftool] ⚠️ ICC Profile 恢复失败（退出码 {iccExit}），目标格式可能不支持 exiftool ICC 写入（如 JXL）。\n" +
                                    $"[exiftool] 建议：勾选「保留 Ultra HDR 增益图」或使用外部 cjxl 编码，以正确保留色彩配置。\n";
                        }
                        else if (protectColorMetadata && userSpecifiedColor)
                        {
                            item.Log += "[exiftool] 用户已指定色彩空间，跳过源文件 ICC Profile 恢复（避免覆盖用户选择）\n";
                        }
                        // JPEG 输出添加 JFIF 头（提高手机兼容性）
                        if (IsJpegOutput(item))
                            await ExifToolService.EnsureJfifHeaderAsync(outputPath);
                        item.Log += "[exiftool] 元数据恢复完成\n";
                        return;
                    }
                    else if (protectColorMetadata)
                    {
                        item.Log += $"[exiftool] 元数据恢复警告: 退出码 {copyExit}，已跳过 ffmpeg 回退（色彩保护模式）\n";
                        return;
                    }
                    else
                    {
                        item.Log += $"[exiftool] 元数据恢复警告: 退出码 {copyExit}，尝试 ffmpeg 回退...\n";
                    }
                }
                catch (Exception ex)
                {
                    if (protectColorMetadata)
                    {
                        item.Log += $"[exiftool] 元数据恢复异常: {ex.Message}，已跳过 ffmpeg 回退（色彩保护模式）\n";
                        return;
                    }
                    item.Log += $"[exiftool] 元数据恢复异常: {ex.Message}，尝试 ffmpeg 回退...\n";
                }
            }
            else
            {
                item.Log += "[metadata] exiftool 未检测到，使用 ffmpeg 回退方案恢复元数据\n";
            }

            // ── 回退：ffmpeg 重新封装 ──
            // 仅在色彩不需要保护时才执行 ffmpeg 回退。
            // ffmpeg -map_metadata 1 会从源文件映射全局元数据（可能含 ICC/色彩标签），
            // 与用户手动指定的色彩参数或编码器嵌入的色彩信息可能冲突。
            if (protectColorMetadata)
            {
                item.Log += "[ffmpeg-meta] 已跳过回退（色彩保护模式）\n";
                return;
            }
            await RestoreMetadataViaFfmpegAsync(item, outputPath);
        }

        /// <summary>
        /// 使用 ffmpeg 重新封装来恢复元数据：以目标文件的像素流为基础，
        /// 从源文件映射元数据，输出到临时文件后原子替换。
        /// </summary>
        private async Task RestoreMetadataViaFfmpegAsync(QueueItem item, string outputPath)
        {
            // 使用带正确扩展名的临时文件路径，确保 ffmpeg 可以自动识别输出格式
            var tempPath = Path.Combine(PlatformServices.GetTempDir(),
                $"meta_{Guid.NewGuid():N}_{Path.GetFileName(outputPath)}");
            try
            {
                item.Log += "[ffmpeg-meta] 使用 ffmpeg 从源文件恢复元数据...\n";
                _onItemUpdated?.Invoke(item);

                // 双输入重新封装：
                //   -i outputPath  (map 0 = 目标文件的像素流)
                //   -i inputPath   (map_metadata 1 = 源文件的元数据)
                //   -c copy        (不解码，直接复制码流)
                //   -map_metadata 1 (从第二个输入映射全局元数据)
                //   -map_metadata:s:v 1:s:v (从第二个输入映射视频流元数据)
                var ffArgs = $"-y -i \"{outputPath}\" -i \"{item.InputPath}\" " +
                             $"-map 0 -map_metadata 1 -map_metadata:s:v 1:s:v " +
                             $"-c copy \"{tempPath}\"";

                item.Log += $"[ffmpeg-meta] ffmpeg {ffArgs}\n";
                var exitCode = await FfmpegRunner.RunAsync(ffArgs,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                    AppSettingsService.Current.FfmpegPath);

                if (exitCode == 0 && File.Exists(tempPath))
                {
                    // 原子替换：删除原文件，重命名临时文件
                    try
                    {
                        File.Delete(outputPath);
                        File.Move(tempPath, outputPath);
                        item.Log += "[ffmpeg-meta] 元数据恢复完成（ffmpeg 重新封装）\n";
                    }
                    catch (Exception ex)
                    {
                        item.Log += $"[ffmpeg-meta] 文件替换失败: {ex.Message}\n";
                        TryCleanupTemp(tempPath);
                    }
                }
                else
                {
                    item.Log += $"[ffmpeg-meta] ffmpeg 重新封装失败 (退出码 {exitCode})，元数据可能丢失\n";
                    TryCleanupTemp(tempPath);
                }
            }
            catch (Exception ex)
            {
                item.Log += $"[ffmpeg-meta] 异常: {ex.Message}\n";
                TryCleanupTemp(tempPath);
            }
        }

        private static void TryCleanupTemp(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>cjxl 编码（优先直接编码，失败自动转 PNG 再试）</summary>
        private async Task ProcessCjxlAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            var isJpegInput = Path.GetExtension(item.InputPath).ToLowerInvariant() is ".jpg" or ".jpeg";

            // 第一步：直接尝试 cjxl
            item.Command = "cjxl " + CjxlService.BuildCjxlArguments(item.InputPath, outputPath, item.Options);
            var inputExtForCjxl = Path.GetExtension(item.InputPath).ToLowerInvariant();
            item.Log += $"[cjxl] 直接编码 (输入: {inputExtForCjxl}, 目标: jxl, effort={item.Options.JxlEffort ?? 5}, threads={item.Options.Threads})\n";
            _onItemUpdated?.Invoke(item);
            int exitCode;
            if (isJpegInput)
            {
                item.Log += "[cjxl] 检测到 JPEG 输入，启用无损重封装模式（-d 0 --lossless_jpeg=1，不解码 DCT 系数）\n";
                var jpegOpts = new Models.FfmpegOptions
                {
                    Quality = item.Options.Quality,
                    JxlEffort = item.Options.JxlEffort ?? 5,
                    CjxlProgressive = item.Options.CjxlProgressive,
                    CjxlPhotonNoiseIso = item.Options.CjxlPhotonNoiseIso,
                    Threads = item.Options.Threads
                };
                exitCode = await CjxlService.RunWithOptionsAsync(
                    item.InputPath, outputPath, jpegOpts,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); });
            }
            else
            {
                exitCode = await CjxlService.RunWithOptionsAsync(
                    item.InputPath, outputPath, item.Options,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); });
            }

            if (exitCode == 0)
            {
                item.ExitCode = 0;
                item.Status = isJpegInput ? "已完成 (cjxl 无损重封装)" : "已完成 (cjxl)";
                await RestoreMetadataAsync(item, outputPath);
                return;
            }

            // 动图不进行 PNG 中转（会丢失动画帧），回退到 FFmpeg 内置编码器
            if (IsAnimated(item))
            {
                item.Log += $"[cjxl] 动图/不支持格式（退出码 {exitCode}），回退到 FFmpeg libjxl_anim 编码器\n";
                _onItemUpdated?.Invoke(item);

                var ffmpegOpts = CloneOptionsForFfmpeg(item.Options);
                var args = FfmpegCommandBuilder.BuildArguments(ffmpegOpts, item.InputPath, outputPath);
                item.Command = "ffmpeg " + args;
                var ffExit = await FfmpegRunner.RunAsync(args, s =>
                {
                    item.Log += s;
                    _onItemUpdated?.Invoke(item);
                }, AppSettingsService.Current.FfmpegPath);
                item.ExitCode = ffExit;
                item.Status = ffExit == 0 ? "已完成 (cjxl→ffmpeg 回退)" : $"失败 (ffmpeg 退出码 {ffExit})";
                return;
            }

            // 第二步：直接编码失败 → ffmpeg 管道直通 cjxl（无中间文件）
            var inputExtForPipe = Path.GetExtension(item.InputPath).ToLowerInvariant();
            item.Log += $"[cjxl] 直接编码失败 (退出码 {exitCode})，{inputExtForPipe} 不被 cjxl 直接支持，将通过 ffmpeg 管道直通 cjxl（无磁盘中间文件）\n";
            _onItemUpdated?.Invoke(item);

            var pipeResult = await PipeFfmpegToCjxlAsync(item, outputPath, ct);
            item.ExitCode = pipeResult.exitCode;
            item.Status = pipeResult.status;
            if (pipeResult.exitCode == 0)
                await RestoreMetadataAsync(item, outputPath);
        }

        /// <summary>cjpegli 编码（优先直接编码，失败自动转 PNG 再试）</summary>
        private async Task ProcessCjpegliAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            // 第一步：直接尝试 cjpegli
            item.Command = "cjpegli " + CjpegliService.BuildCjpegliArguments(item.InputPath, outputPath, item.Options);
            item.Log += "[cjpegli] 直接编码...\n";
            _onItemUpdated?.Invoke(item);
            var exit = await CjpegliService.RunWithOptionsAsync(
                item.InputPath, outputPath, item.Options,
                s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);

            if (exit == 0)
            {
                item.ExitCode = 0;
                item.Status = "已完成 (cjpegli)";
                await RestoreMetadataAsync(item, outputPath);
                return;
            }

            // 动图不进行 PNG 中转（会丢失动画帧），回退到 FFmpeg 内置编码器
            if (IsAnimated(item))
            {
                item.Log += $"[cjpegli] 动图/不支持格式（退出码 {exit}），回退到 FFmpeg mjpeg 编码器\n";
                _onItemUpdated?.Invoke(item);

                var ffmpegOpts = CloneOptionsForFfmpeg(item.Options);
                var args = FfmpegCommandBuilder.BuildArguments(ffmpegOpts, item.InputPath, outputPath);
                item.Command = "ffmpeg " + args;
                var ffExit = await FfmpegRunner.RunAsync(args, s =>
                {
                    item.Log += s;
                    _onItemUpdated?.Invoke(item);
                }, AppSettingsService.Current.FfmpegPath);
                item.ExitCode = ffExit;
                item.Status = ffExit == 0 ? "已完成 (cjpegli→ffmpeg 回退)" : $"失败 (ffmpeg 退出码 {ffExit})";
                return;
            }

            // 第二步：直接编码失败 → ffmpeg 管道直通 cjpegli（无中间文件）
            var ext = Path.GetExtension(item.InputPath).ToLowerInvariant();
            item.Log += $"[cjpegli] 直接编码失败 (退出码 {exit})，{ext} 不被支持，通过 ffmpeg 管道直通 cjpegli（无磁盘中间文件）\n";
            _onItemUpdated?.Invoke(item);

            var pipeResult = await PipeFfmpegToCjpegliAsync(item, outputPath, ct);
            item.ExitCode = pipeResult.exitCode;
            item.Status = pipeResult.status;
            if (pipeResult.exitCode == 0)
                await RestoreMetadataAsync(item, outputPath);
        }

        /// <summary>
        /// Gain Map (Ultra HDR) JPEG 编码（纯 C# 管线，无 ultrahdr 依赖）：
        /// 1. ffmpeg 解码 → 线性 RGB 浮点像素
        /// 2. GainMapEncoder → 分段 Reinhard 色调映射 → cjpegli 基础图 + 增益图 → 打包
        /// </summary>
        private async Task ProcessGainMapJpegAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            var ffmpegPath = AppSettingsService.Current.FfmpegPath;

            if (!CjpegliService.IsAvailable)
            {
                item.Log += "[GainMap] ⚠️ cjpegli 不可用，无法编码 Gain Map，回退到纯 JPEG 编码（无增益图）\n";
                _onItemUpdated?.Invoke(item);

                var mjpegOpts = CloneOptionsForFfmpeg(item.Options);
                mjpegOpts.Encoder = "mjpeg";
                mjpegOpts.EncoderBackend = EncoderBackend.Ffmpeg;
                var args = FfmpegCommandBuilder.BuildArguments(mjpegOpts, item.InputPath, outputPath);
                item.Command = "ffmpeg " + args;
                var mjExit = await FfmpegRunner.RunAsync(args,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ffmpegPath);
                item.ExitCode = mjExit;
                item.Status = mjExit == 0 ? "已完成 (mjpeg, 无增益图)" : $"失败 (mjpeg 退出码 {mjExit})";
                if (item.ExitCode == 0) await RestoreMetadataAsync(item, outputPath);
                return;
            }

            var tempDir = Path.Combine(PlatformServices.GetTempDir(), $"gainmap_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                // Step 1: 尺寸
                item.Log += "[GainMap] Step 1: 获取图像尺寸...\n";
                _onItemUpdated?.Invoke(item);
                var (width, height) = await ProbeImageSizeAsync(item.InputPath, ffmpegPath);
                if (width <= 0 || height <= 0)
                {
                    item.ExitCode = -1;
                    item.Status = "失败 (无法获取图像尺寸)";
                    return;
                }
                item.Log += $"[GainMap]   尺寸: {width}x{height}\n";

                // Step 2: ffmpeg 解码 → 线性 RGB 浮点 (gbrpf32le planar rawvideo)
                //   HDR 输入 (PQ/HLG) → zscale t=linear → 线性空间; 1.0 = 峰值亮度 (npl)
                //   SDR 输入 → zscale t=linear → 1.0 = SDR 白点 (headroom=1, 无增益)
                var transfer = await ProbeColorTransferAsync(item.InputPath, ffmpegPath);
                var isHdrInput = transfer is "smpte2084" or "arib-std-b67";
                var nits = item.Options.JpegGainMapTargetNits > 0
                    ? item.Options.JpegGainMapTargetNits : 1000;
                var peakNits = isHdrInput ? nits : GainMapEncoder.KSdrWhiteNits;
                var rawPath = Path.Combine(tempDir, "hdr.rgba");
                // 尝试 1: zscale 转线性 (HDR PQ/HLG 输入)
                var decodeArgs = $"-y -i \"{item.InputPath}\" " +
                    $"-vf \"zscale=t=linear:npl={nits}:r=pc\" " +
                    $"-pix_fmt gbrpf32le -f rawvideo \"{rawPath}\"";
                item.Log += $"[GainMap] Step 2: ffmpeg 解码为线性 RGB 浮点 " +
                    $"({(isHdrInput ? $"HDR PQ/HLG npl={nits}" : "SDR")})...\n";
                _onItemUpdated?.Invoke(item);
                var decodeExit = await FfmpegRunner.RunAsync(decodeArgs,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ffmpegPath, ct);
                // 尝试 2: zscale 失败 (无色彩元数据的线性输入如 dngtool TIFF) → 直接解码
                if (decodeExit != 0 || !File.Exists(rawPath))
                {
                    item.Log += "[GainMap] ⚠️ zscale 线性转换失败，尝试直接解码 (输入可能已是线性)...\n";
                    _onItemUpdated?.Invoke(item);
                    var directArgs = $"-y -i \"{item.InputPath}\" " +
                        $"-pix_fmt gbrpf32le -f rawvideo \"{rawPath}\"";
                    decodeExit = await FfmpegRunner.RunAsync(directArgs,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ffmpegPath, ct);
                }
                if (decodeExit != 0 || !File.Exists(rawPath))
                {
                    item.Log += $"[GainMap] ⚠️ ffmpeg 线性解码失败 (exit {decodeExit})\n";
                    _onItemUpdated?.Invoke(item);
                    item.ExitCode = decodeExit;
                    item.Status = $"失败 (线性解码退出码 {decodeExit})";
                    return;
                }
                PlatformServices.MarkAsTemporaryFile(rawPath);

                // Step 3: 读取像素 (gbrpf32le planar: G/B/R 三平面) → RGBA 交错
                int pixelCount = width * height;
                var rawBytes = await File.ReadAllBytesAsync(rawPath, ct);
                if (rawBytes.Length < pixelCount * 3L * 4L)
                {
                    item.Log += "[GainMap] ⚠️ RAW 像素数据不完整\n";
                    _onItemUpdated?.Invoke(item);
                    item.ExitCode = -1;
                    item.Status = "失败 (像素数据不完整)";
                    return;
                }
                // gbrpf32le: 平面顺序 G, B, R (ffmpeg gbr 命名), 各 pixelCount 个 float
                var gPlane = new float[pixelCount];
                var bPlane = new float[pixelCount];
                var rPlane = new float[pixelCount];
                Buffer.BlockCopy(rawBytes, 0, gPlane, 0, pixelCount * 4);
                Buffer.BlockCopy(rawBytes, pixelCount * 4, bPlane, 0, pixelCount * 4);
                Buffer.BlockCopy(rawBytes, pixelCount * 8, rPlane, 0, pixelCount * 4);
                var pixels = new float[pixelCount * 4];
                for (int i = 0; i < pixelCount; i++)
                {
                    int o = i * 4;
                    pixels[o] = rPlane[i];
                    pixels[o + 1] = gPlane[i];
                    pixels[o + 2] = bPlane[i];
                    pixels[o + 3] = 1f;
                }

                // Step 4: GainMapEncoder 纯 C# 编码
                var gmq = item.Options.JpegGainMapQuality >= 0
                    ? item.Options.JpegGainMapQuality : 75;
                // 质量 → butteraugli distance (与 cjpegli 语义一致)
                float baseDist = Math.Clamp((100f - item.Options.Quality) * 0.25f, 0.5f, 6.0f);
                float gmDist = Math.Clamp((100f - gmq) * 0.25f, 0.5f, 6.0f);

                item.Log += $"[GainMap] Step 3: GainMapEncoder 编码 (纯 C#, 峰值 {peakNits:F0}nits, " +
                    $"base d={baseDist:F2}, gm d={gmDist:F2})...\n";
                _onItemUpdated?.Invoke(item);
                item.Command = "[GainMap] 纯托管编码 (GainMapEncoder + cjpegli)";
                var ok = await GainMapEncoder.EncodeAsync(
                    pixels, width, height, outputPath,
                    hdrPeakNits: peakNits, sdrWhiteNits: GainMapEncoder.KSdrWhiteNits,
                    multiChannel: item.Options.JpegGainMapMultiChannel,
                    baseQuality: baseDist, gainMapQuality: gmDist,
                    downsample: Math.Max(item.Options.JpegGainMapDownsample, 1),
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);

                item.ExitCode = ok ? 0 : -1;
                item.Status = ok ? "已完成 (Gain Map 纯托管编码)" : "失败 (GainMapEncoder 编码失败)";
                if (ok)
                    await RestoreMetadataAsync(item, outputPath);
            }
            catch (OperationCanceledException)
            {
                item.Status = "已停止";
            }
            catch (Exception ex)
            {
                item.Log += $"[GainMap] 编码异常: {ex.Message}\n";
                item.ExitCode = -1;
                item.Status = $"失败: {ex.Message}";
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>使用 cjpegli 编码 SDR 基础图（与 ProcessCjpegliAsync 行为一致：先直接尝试，失败走 ffmpeg 管道）</summary>
        private async Task<int> EncodeSdrBaseWithCjpegliAsync(QueueItem item, string sdrBaseJpeg, CancellationToken ct)
        {
            // 直接尝试 cjpegli
            item.Command = "cjpegli " + CjpegliService.BuildCjpegliArguments(item.InputPath, sdrBaseJpeg, item.Options);
            item.Log += $"[cjpegli-sdr] 直接编码...\n";
            _onItemUpdated?.Invoke(item);
            var exit = await CjpegliService.RunWithOptionsAsync(
                item.InputPath, sdrBaseJpeg, item.Options,
                s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
            if (exit == 0) return 0;

            // 直接编码失败 → ffmpeg 管道直通 cjpegli（与 ProcessCjpegliAsync 一致）
            var ext = Path.GetExtension(item.InputPath).ToLowerInvariant();
            item.Log += $"[cjpegli-sdr] 直接编码失败 (退出码 {exit})，{ext} 非原生格式，ffmpeg 管道直通 cjpegli...\n";
            _onItemUpdated?.Invoke(item);
            var result = await PipeFfmpegToCjpegliAsync(item, sdrBaseJpeg, ct);
            return result.exitCode;
        }

        /// <summary>
        /// JxrEncApp 编码：ffmpeg 解码输入 → BMP 临时文件 → JxrEncApp 编码为 JPEG XR。
        /// JxrEncApp 原生支持 BMP/TIFF 输入，BMP 是无损中间格式。
        /// </summary>
        private async Task ProcessJxrAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            if (!JxrService.IsAvailable)
            {
                item.Log += "[jxr] JxrEncApp.exe 未检测到，无法编码\n";
                item.ExitCode = -1;
                item.Status = "失败 (JxrEncApp 未找到)";
                return;
            }

            var ffmpegPath = AppSettingsService.Current.FfmpegPath;
            var tempDir = Path.Combine(PlatformServices.GetTempDir(), $"jxr_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                var inputExt = Path.GetExtension(item.InputPath).ToLowerInvariant();

                string intermediatePath;
                bool isHdr;

                // ── 常规输入：ffmpeg 解码 → BMP/TIFF ──
                {
                    int bitDepth = item.Options.BitDepth ?? await ProbeBitDepthAsync(item.InputPath, ffmpegPath);
                    isHdr = bitDepth > 8;
                    var intermediateExt = isHdr ? ".tiff" : ".bmp";
                    intermediatePath = Path.Combine(tempDir, $"input{intermediateExt}");
                    var pixFmt = isHdr ? "rgb48le" : "bgr24";
                    item.Log += $"[jxr] 检测位深: {bitDepth}-bit, 像素格式: {pixFmt}\n";

                    item.Log += $"[jxr] Step 1: ffmpeg 解码为 {intermediateExt.ToUpper()}（{pixFmt}）...\n";
                    _onItemUpdated?.Invoke(item);

                    var colorArgs = BuildJxrColorArgs(item.Options);
                    var tiffExtra = isHdr ? " -compression_algo raw -pred none" : "";

                    // 16-bit 输入默认转换为 sRGB（JxrEncApp 固定 sRGB 输出）
                    string? zscaleFilter = null;
                    if (isHdr && string.IsNullOrWhiteSpace(colorArgs))
                    {
                        // auto 模式: 从宽色域 (默认 Rec.2020) 转换为 sRGB
                        // format=rgb48le 前缀: 规避 libzimg 对 4:2:0 输入的尺寸整除要求（奇数尺寸 1027）
                        zscaleFilter = "format=rgb48le,zscale=primariesin=bt2020:primaries=bt709:transferin=bt709:transfer=iec61966-2-1,";
                    }
                    var filterArg = zscaleFilter != null ? $"-vf \"{zscaleFilter}format={pixFmt}\" " : "";
                    var decodeArgs = $"-y {colorArgs}-i \"{item.InputPath}\" {filterArg}-pix_fmt {pixFmt}{tiffExtra} \"{intermediatePath}\"";
                    item.Command = $"ffmpeg {decodeArgs}";
                    var decExit = await FfmpegRunner.RunAsync(decodeArgs,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ffmpegPath);
                    if (decExit != 0 || !File.Exists(intermediatePath))
                    {
                        item.ExitCode = decExit;
                        item.Status = $"失败 ({intermediateExt} 解码退出码 {decExit})";
                        return;
                    }
                }

                // 提示 OS 优先内存缓存中间文件，减少 SSD 写入
                PlatformServices.MarkAsTemporaryFile(intermediatePath);

                // Step 2: JxrEncApp 编码
                item.Log += "[jxr] Step 2: JxrEncApp 编码 JPEG XR...\n";
                _onItemUpdated?.Invoke(item);
                var quality = item.Options.Quality / 100.0;
                if (item.Options.Lossless) quality = 1.0;
                var jxrArgs = JxrService.BuildArguments(intermediatePath, outputPath, quality);
                item.Command = "JxrEncApp " + jxrArgs;
                var jxrExit = await JxrService.RunAsync(jxrArgs,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); });

                item.ExitCode = jxrExit;
                item.Status = jxrExit == 0
                    ? (isHdr ? "已完成 (JxrEncApp JPEG XR HDR)" : "已完成 (JxrEncApp JPEG XR)")
                    : $"失败 (JxrEncApp 退出码 {jxrExit})";
                if (jxrExit == 0)
                {
                    // ── HDR 色彩元数据写入（JxrEncApp 不支持嵌入色彩配置）──
                    if (!string.IsNullOrWhiteSpace(item.Options.DecodedUltraHdrColorSpace))
                    {
                        await WriteJxrHdrMetadataAsync(item, outputPath);
                    }
                    await RestoreMetadataAsync(item, outputPath);
                }
            }
            catch (OperationCanceledException)
            {
                item.Status = "已停止";
            }
            catch (Exception ex)
            {
                item.Log += $"[jxr] 异常: {ex.Message}\n";
                item.ExitCode = -1;
                item.Status = $"失败: {ex.Message}";
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>将 HDR 色彩元数据写入 JXR（JxrEncApp 不支持色彩配置，最佳努力写入 EXIF/XMP）</summary>
        private async Task WriteJxrHdrMetadataAsync(QueueItem item, string outputPath)
        {
            if (!ExifToolService.IsAvailable) return;
            try
            {
                item.Log += "[jxr] 写入 HDR 色彩元数据...\n";
                _onItemUpdated?.Invoke(item);

                var args = $"-overwrite_original -m " +
                    $"-EXIF:ColorSpace=Uncalibrated " +
                    $"-EXIF:ImageDescription=\"HDR Rec.2100 PQ\" " +
                    $"\"{outputPath}\"";

                var exit = await ExifToolService.RunRawAsync(args,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); });
                if (exit == 0)
                    item.Log += "[jxr] HDR 元数据已写入 (Uncalibrated + Rec.2100 PQ 描述)\n";
                else
                    item.Log += $"[jxr] ⚠️ HDR 元数据写入警告 (退出码 {exit})\n";
            }
            catch (Exception ex)
            {
                item.Log += $"[jxr] HDR 元数据写入异常: {ex.Message}\n";
            }
        }

        /// <summary>10-bit PQ RAW (x2bgr10le) → 无压缩 Radiance HDR (.hdr, 32bppRGBE)，JxrEncApp 可识别为 HDR</summary>
        private static async Task<string?> ConvertRawToRadianceHdrAsync(
            string rawPath, int width, int height, string tempDir,
            Action<string>? logCallback = null)
        {
            var hdrPath = Path.Combine(tempDir, "input.hdr");
            try
            {
                var rawBytes = await File.ReadAllBytesAsync(rawPath);
                var expectedSize = width * height * 4;
                if (rawBytes.Length < expectedSize)
                {
                    logCallback?.Invoke($"[jxr-hdr] RAW size mismatch: {rawBytes.Length} vs {expectedSize}\n");
                    return null;
                }

                // 写入 Radiance 头（无压缩格式）
                using var fs = new FileStream(hdrPath, FileMode.Create, FileAccess.Write);
                using var sw = new StreamWriter(fs, System.Text.Encoding.ASCII, 4096, true);
                sw.NewLine = "\n";
                sw.Write("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {0} +X {1}\n", height, width);
                sw.Flush();

                // 逐像素转换: 10-bit PQ(BGR packed) → linear float → RGBE
                var rgbeRow = new byte[width * 4];
                for (int y = 0; y < height; y++)
                {
                    int rowOffset = y * width * 4;
                    for (int x = 0; x < width; x++)
                    {
                        int i = rowOffset + x * 4;
                        uint packed = (uint)(rawBytes[i] | (rawBytes[i + 1] << 8)
                            | (rawBytes[i + 2] << 16) | (rawBytes[i + 3] << 24));
                        // x2bgr10le: B[0:9], G[10:19], R[20:29], X[30:31]
                        float b10 = (packed & 0x3FF) / 1023.0f;
                        float g10 = ((packed >> 10) & 0x3FF) / 1023.0f;
                        float r10 = ((packed >> 20) & 0x3FF) / 1023.0f;
                        float r = PqToLinear(r10);
                        float g = PqToLinear(g10);
                        float b = PqToLinear(b10);
                        RgbToRgbe(r, g, b, out byte R, out byte G, out byte B, out byte E);
                        int dst = x * 4;
                        rgbeRow[dst] = R;
                        rgbeRow[dst + 1] = G;
                        rgbeRow[dst + 2] = B;
                        rgbeRow[dst + 3] = E;
                    }
                    await fs.WriteAsync(rgbeRow, 0, rgbeRow.Length);
                }
                logCallback?.Invoke($"[jxr-hdr] Radiance HDR 已生成 ({width}x{height}, RGBE, uncompressed)\n");
                return hdrPath;
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"[jxr-hdr] 转换失败: {ex.Message}\n");
                return null;
            }
        }

        /// <summary>ST.2084 (PQ) EOTF → linear light</summary>
        private static float PqToLinear(float pq)
        {
            // ST.2084 EOTF: L = ((max(V^(1/m2) - c1, 0)) / (c2 - c3 * V^(1/m2)))^(1/m1)
            const float m1 = 2610f / 16384f;   // 0.1593017578125
            const float m2 = 2523f / 32f;       // 78.84375
            const float c1 = 3424f / 4096f;     // 0.8359375
            const float c2 = 2413f / 128f;      // 18.8515625
            const float c3 = 2392f / 128f;      // 18.6875
            float v = MathF.Pow(pq, 1.0f / m2);
            float num = MathF.Max(v - c1, 0);
            float den = c2 - c3 * v;
            return MathF.Pow(num / den, 1.0f / m1);
        }

        /// <summary>Linear float RGB → RGBE (Radiance shared-exponent)</summary>
        private static void RgbToRgbe(float r, float g, float b,
            out byte R, out byte G, out byte B, out byte E)
        {
            float max = MathF.Max(r, MathF.Max(g, b));
            if (max < 1e-32f) { R = G = B = E = 0; return; }
            int e = (int)MathF.Ceiling(MathF.Log2(max));
            e = Math.Clamp(e + 128, 0, 255);
            float scale = MathF.Pow(2, e - 128 - 8);
            R = (byte)Math.Clamp((int)(r / scale + 0.5f), 0, 255);
            G = (byte)Math.Clamp((int)(g / scale + 0.5f), 0, 255);
            B = (byte)Math.Clamp((int)(b / scale + 0.5f), 0, 255);
            E = (byte)e;
        }

        /// <summary>
        /// DNG 编码输出：任意 RAW/DNG 输入 → DNG 文件。
        /// 使用 dngtool (LibRaw + Adobe DNG SDK 1.7.1)。
        /// 压缩: 无损 JPEG (默认) 或 JXL (DNG 1.7)。
        /// </summary>
        private async Task ProcessDngAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            if (!RawService.IsAvailable || !RawService.IsDngTool)
            {
                item.Log += "[dng] dngtool.exe 未检测到，无法编码 DNG\n";
                item.Log += "[dng] 请将 dngtool.exe 放入 PLAN/artifacts/ 目录（含 DNG 1.7 JXL 支持）\n";
                item.ExitCode = -1;
                item.Status = "失败 (dngtool 未找到)";
                return;
            }

            // 输入必须为 RAW/DNG；非 RAW 输入无法生成 DNG（无传感器数据）
            if (!RawService.IsRawFile(item.InputPath))
            {
                item.Log += "[dng] ⚠️ 仅 RAW/DNG 文件可编码为 DNG（普通图片无传感器数据）\n";
                item.ExitCode = -1;
                item.Status = "失败 (非 RAW 输入)";
                return;
            }

            // 压缩方式: 无损 JPEG 或 JXL
            int compression = item.Options.Lossless ? 0 : 0; // 当前 DNG 默认无损
            int jxlQuality = 0;
            if (item.Options.JxlModular == true) // 复用 JXL 相关选项选择 JXL 压缩
            {
                compression = 1;
                jxlQuality = item.Options.Lossless ? 0 : Math.Clamp(item.Options.Quality, 1, 100);
            }

            item.Log += $"[dng] 编码 DNG ({(compression == 1 ? $"JXL q={jxlQuality}" : "无损 JPEG")})...\n";
            _onItemUpdated?.Invoke(item);

            var success = await RawService.EncodeToDngAsync(
                item.InputPath, outputPath, compression, jxlQuality,
                s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);

            item.ExitCode = success ? 0 : -1;
            item.Status = success
                ? $"已完成 ({(compression == 1 ? "JXL 压缩 DNG" : "DNG")})"
                : "失败 (dngtool 编码失败)";

            if (success)
                await RestoreMetadataAsync(item, outputPath);
        }

        /// <summary>构建 JXR ffmpeg 解码的色彩空间参数</summary>
        private static string BuildJxrColorArgs(FfmpegOptions options)
        {
            var sb = new StringBuilder();

            // Ultra HDR 解码输出：显式标记 Rec.2100 PQ（优先级最高）
            if (!string.IsNullOrWhiteSpace(options.DecodedUltraHdrColorSpace))            {
                sb.Append("-color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc ");
                return sb.ToString();
            }

            var colorSpace = options.ColorSpace;
            if (!string.IsNullOrWhiteSpace(colorSpace) && !colorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                // 将色彩空间映射为 primaries/trc/colorspace
                switch (colorSpace.ToUpper())
                {
                    case "BT.2020":
                        sb.Append("-color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc ");
                        break;
                    case "BT.709":
                        sb.Append("-color_primaries bt709 -color_trc bt709 -colorspace bt709 ");
                        break;
                    case "BT.601":
                        sb.Append("-color_primaries smpte170m -color_trc smpte170m -colorspace smpte170m ");
                        break;
                }
            }
            // 高级色彩参数（覆盖上面的默认映射）
            if (options.UseAdvancedColorParameters)
            {
                if (!string.IsNullOrWhiteSpace(options.ColorPrimaries))
                    sb.Append($"-color_primaries {options.ColorPrimaries} ");
                if (!string.IsNullOrWhiteSpace(options.ColorTrc))
                    sb.Append($"-color_trc {options.ColorTrc} ");
                if (!string.IsNullOrWhiteSpace(options.ColorMatrix))
                    sb.Append($"-colorspace {options.ColorMatrix} ");
            }
            return sb.ToString();
        }

        /// <summary>检测 JPEG 是否为 Ultra HDR (含 MPF 增益图)</summary>
        private static async Task<bool> IsUltraHdrJpegAsync(string path)
        {
            if (!ExifToolService.IsAvailable) return false;
            try
            {
                // Ultra HDR JPEG 在 MPF 结构中包含第二张图 (MPImage2)
                var result = await ExifToolService.GetTagAsync(path, "MPImage2");
                return !string.IsNullOrWhiteSpace(result);
            }
            catch { return false; }
        }

        /// <summary>将 Ultra HDR JPEG 解码为线性 HDR 像素 (gbrpf32le raw), 失败返回 null</summary>
        private async Task<string?> DecodeUltraHdrToLinearAsync(QueueItem item, string inputPath, CancellationToken ct)
        {
            var tempDir = Path.Combine(PlatformServices.GetTempDir(), $"uhdr_linear_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var rawPath = Path.Combine(tempDir, "hdr.rgba");
                var result = await GainMapDecoder.DecodeToLinearRawAsync(inputPath, rawPath,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
                if (result == null || !File.Exists(rawPath))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                    return null;
                }
                PlatformServices.MarkAsTemporaryFile(rawPath);
                // 临时目录在任务结束后由 OS 临时目录策略清理; 队列停止时统一尝试删除
                lock (_pendingRawDirs) _pendingRawDirs.Add(tempDir);
                return rawPath;
            }
            catch
            {
                try { Directory.Delete(tempDir, true); } catch { }
                return null;
            }
        }

        /// <summary>判断输出是否为 JPEG 格式</summary>
        private static bool IsJpegOutput(QueueItem item)
        {
            var fmt = (item.Options.Format ?? "").ToLowerInvariant();
            return fmt is "jpg" or "jpeg" or "jpegli";
        }

        /// <summary>用 ffprobe 探测输入文件的位深</summary>
        private static async Task<int> ProbeBitDepthAsync(string inputPath, string ffmpegPath)
        {
            try
            {
                var ffprobePath = PlatformServices.ResolveFfprobePath(ffmpegPath)
                    ?? Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe.exe");
                if (!File.Exists(ffprobePath)) ffprobePath = ffmpegPath.Replace("ffmpeg.exe", "ffprobe.exe");

                var psi = new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    Arguments = $"-v error -select_streams v:0 -show_entries stream=pix_fmt -of csv=p=0 \"{inputPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return 8;
                var output = (await p.StandardOutput.ReadToEndAsync()).Trim();
                await p.WaitForExitAsync();

                // 从像素格式推断位深
                return output switch
                {
                    string s when s.Contains("16") || s.Contains("48") || s.Contains("64") => 16,
                    string s when s.Contains("12") => 12,
                    string s when s.Contains("10") => 10,
                    string s when s.Contains("p010") || s.Contains("p012") => 10,
                    _ => 8
                };
            }
            catch { return 8; }
        }

        /// <summary>使用 ffprobe 获取图像文件的宽高</summary>
        private static async Task<(int width, int height)> ProbeImageSizeAsync(string inputPath, string ffmpegPath)
        {
            try
            {
                var ffprobePath = PlatformServices.ResolveFfprobePath(ffmpegPath)
                    ?? Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe.exe");
                if (!File.Exists(ffprobePath)) ffprobePath = ffmpegPath.Replace("ffmpeg.exe", "ffprobe.exe");

                var psi = new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    Arguments = $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 \"{inputPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return (0, 0);
                var output = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();
                var parts = output.Trim().Split(',');
                if (parts.Length >= 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                    return (w, h);
            }
            catch { }
            return (0, 0);
        }

        /// <summary>检测输入图像的色彩传输函数 (smpte2084=PQ HDR, arib-std-b67=HLG HDR)</summary>
        private static async Task<string?> ProbeColorTransferAsync(string inputPath, string ffmpegPath)
        {
            try
            {
                var ffprobePath = PlatformServices.ResolveFfprobePath(ffmpegPath)
                    ?? Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe.exe");
                if (!File.Exists(ffprobePath)) ffprobePath = ffmpegPath.Replace("ffmpeg.exe", "ffprobe.exe");

                var psi = new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    Arguments = $"-v error -select_streams v:0 -show_entries stream=color_transfer -of csv=p=0 \"{inputPath}\"",
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

        /// <summary>用 ffmpeg 将输入转为临时高质量 PNG，返回路径（失败返回 null 并设置状态）</summary>
        private async Task<string?> PreConvertToPngAsync(QueueItem item, CancellationToken ct)
        {
            var tmp = Path.Combine(PlatformServices.GetTempDir(), $"ffmpeg_preconv_{Guid.NewGuid():N}.png");
            item.Log += "[preconv] 使用 ffmpeg 转为高质量 PNG 中间格式\n";
            // 传递 -map_metadata 0 保留源文件元数据到临时 PNG，确保后续外部工具编码时有元数据可用
            item.Command = $"ffmpeg -y -i \"{item.InputPath}\" -map_metadata 0 -compression_level 0 \"{tmp}\"";
            _onItemUpdated?.Invoke(item);

            var args = $"-y -i \"{item.InputPath}\" -map_metadata 0 -compression_level 0 \"{tmp}\"";
            var exit = await FfmpegRunner.RunAsync(args,
                s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                AppSettingsService.Current.FfmpegPath);

            if (exit != 0 || !File.Exists(tmp))
            {
                item.Log += $"[preconv] ffmpeg 转换失败 (退出码 {exit})\n";
                item.ExitCode = exit;
                item.Status = $"失败 (预转换退出码 {exit})";
                return null;
            }

            PlatformServices.MarkAsTemporaryFile(tmp);
            item.Log += "[preconv] 转换完成\n";
            return tmp;
        }

        /// <summary>
        /// ffmpeg 解码 → 管道 → cjxl 编码（无磁盘中间文件）。
        /// 命令等价于: ffmpeg -i input -compression_level 0 -f image2pipe -vcodec png - | cjxl - output.jxl -d X -e Y
        /// 元数据通过 exiftool 在编码后恢复。
        /// </summary>
        private async Task<(int exitCode, string status)> PipeFfmpegToCjxlAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            var cjxlPath = CjxlService.DetectedPath;
            if (string.IsNullOrEmpty(cjxlPath))
            {
                item.Log += "[cjxl-pipe] cjxl 未找到，无法使用管道\n";
                return (-1, "失败 (cjxl 未找到)");
            }

            var ffmpegPath = AppSettingsService.Current.FfmpegPath;

            // auto 模式：探测输入 HDR 属性，传递给 cjxl 色彩参数
            var hdrMeta = default(FfmpegCommandBuilder.ColorMetadata);
            if (string.IsNullOrWhiteSpace(item.Options.ColorSpace)
                || item.Options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                hdrMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(item.InputPath);
            }

            var cjxlArgs = CjxlService.BuildCjxlArguments("-", outputPath, item.Options, hdrMeta);
            var (pipeInputColor, pipeOutputColor) = BuildPipeColorArgs(item.Options, item.InputPath);

            item.Command = $"ffmpeg -y {pipeInputColor}-i \"{item.InputPath}\" {pipeOutputColor}-compression_level 0 -f image2pipe -vcodec png - | cjxl {cjxlArgs}";
            item.Log += $"[cjxl-pipe] ffmpeg 管道 → cjxl（无中间文件）\n";
            _onItemUpdated?.Invoke(item);

            return await PipeFfmpegToExternalEncoderAsync(
                item, ffmpegPath, cjxlPath, cjxlArgs, outputPath, "cjxl", ct);
        }

        /// <summary>
        /// ffmpeg 解码 → 管道 → cjpegli 编码（无磁盘中间文件）。
        /// </summary>
        private async Task<(int exitCode, string status)> PipeFfmpegToCjpegliAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            var cjpegliPath = CjpegliService.DetectedPath;
            if (string.IsNullOrEmpty(cjpegliPath))
            {
                item.Log += "[cjpegli-pipe] cjpegli 未找到，无法使用管道\n";
                return (-1, "失败 (cjpegli 未找到)");
            }

            var ffmpegPath = AppSettingsService.Current.FfmpegPath;

            // auto 模式：探测输入 HDR 属性
            var hdrMeta = default(FfmpegCommandBuilder.ColorMetadata);
            if (string.IsNullOrWhiteSpace(item.Options.ColorSpace)
                || item.Options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                hdrMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(item.InputPath);
            }

            var cjpegliArgs = CjpegliService.BuildCjpegliArguments("-", outputPath, item.Options, hdrMeta);
            var (pipeInputColor2, pipeOutputColor2) = BuildPipeColorArgs(item.Options, item.InputPath);

            item.Command = $"ffmpeg -y {pipeInputColor2}-i \"{item.InputPath}\" {pipeOutputColor2}-compression_level 0 -f image2pipe -vcodec png - | cjpegli {cjpegliArgs}";
            item.Log += $"[cjpegli-pipe] ffmpeg 管道 → cjpegli（无中间文件）\n";
            _onItemUpdated?.Invoke(item);

            return await PipeFfmpegToExternalEncoderAsync(
                item, ffmpegPath, cjpegliPath, cjpegliArgs, outputPath, "cjpegli", ct);
        }

        /// <summary>
        /// 通用管道方法：ffmpeg 解码 → stdout (PNG 流) → 外部编码器 stdin → 输出文件。
        /// 此方法消除了所有"解码→写临时 PNG 文件→读取再编码"的磁盘中转。
        /// </summary>
        private async Task<(int exitCode, string status)> PipeFfmpegToExternalEncoderAsync(
            QueueItem item,
            string ffmpegPath,
            string encoderPath,
            string encoderArgs,
            string outputPath,
            string encoderTag,
            CancellationToken ct)
        {
            Process? procFf = null;
            Process? procEnc = null;
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
                var linkedToken = linked.Token;

                // ffmpeg: 解码输入为 PPM 流输出到 stdout
                // primaries/trc 在 -i 前作输入覆盖，colorspace 在 -i 后
                var (pipeInColor, pipeOutColor) = BuildPipeColorArgs(item.Options, item.InputPath);
                var ffArgs = $"-y {pipeInColor}-i \"{item.InputPath}\" {pipeOutColor}-compression_level 0 -f image2pipe -vcodec png -";
                var psiFf = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = ffArgs,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                // 外部编码器：从 stdin 读取 PNG 流
                var psiEnc = new ProcessStartInfo
                {
                    FileName = encoderPath,
                    Arguments = encoderArgs,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                procFf = Process.Start(psiFf);
                if (procFf == null)
                {
                    item.Log += $"[{encoderTag}-pipe] 启动 ffmpeg 失败\n";
                    return (-1, $"失败 (ffmpeg 启动失败)");
                }
                PlatformServices.SetSafePriority(procFf, AppSettingsService.Current.FfmpegPriority);

                procEnc = Process.Start(psiEnc);
                if (procEnc == null)
                {
                    item.Log += $"[{encoderTag}-pipe] 启动 {encoderTag} 失败\n";
                    return (-1, $"失败 ({encoderTag} 启动失败)");
                }
                PlatformServices.SetSafePriority(procEnc, AppSettingsService.Current.FfmpegPriority);

                // 非阻塞消费 stderr 日志
                var ffLogTask = ConsumeStreamLinesAsync(procFf.StandardError, s =>
                {
                    item.Log += $"[ffmpeg] {s}\n";
                });
                var encLogTask = ConsumeStreamLinesAsync(procEnc.StandardError, s =>
                {
                    item.Log += $"[{encoderTag}] {s}\n";
                    _onItemUpdated?.Invoke(item);
                });
                var encOutTask = ConsumeStreamLinesAsync(procEnc.StandardOutput, s =>
                {
                    item.Log += $"[{encoderTag}] {s}\n";
                });

                // ── 关键：先完成管道传输 + 关闭 stdin，再等待进程退出 ──
                Exception? transferError = null;
                try
                {
                    await procFf.StandardOutput.BaseStream.CopyToAsync(
                        procEnc.StandardInput.BaseStream, linkedToken);
                }
                catch (OperationCanceledException)
                {
                    item.Log += $"[{encoderTag}-pipe] 传输被取消\n";
                }
                catch (Exception ex)
                {
                    transferError = ex;
                    item.Log += $"[{encoderTag}-pipe] 传输错误: {ex.Message}\n";
                }

                // 关闭编码器 stdin 发送 EOF
                try { procEnc.StandardInput.Close(); } catch { }

                // 等待编码器正常退出
                try { await procEnc.WaitForExitAsync(linkedToken); }
                catch (OperationCanceledException) { }

                // ffmpeg 应已自行退出（stdout 已读完）
                try { await procFf.WaitForExitAsync(CancellationToken.None); } catch { }

                // 等待日志消费完成
                await ffLogTask;
                await encLogTask;
                await encOutTask;

                if (linkedToken.IsCancellationRequested)
                {
                    item.Log += $"[{encoderTag}-pipe] 超时或取消\n";
                    return (-1, "已停止/超时");
                }

                var encExitCode = procEnc.HasExited ? procEnc.ExitCode : -1;
                if (encExitCode == 0 && transferError == null)
                {
                    item.Log += $"[{encoderTag}-pipe] 管道编码完成\n";
                    return (0, $"已完成 ({encoderTag} 管道)");
                }
                else
                {
                    item.Log += $"[{encoderTag}-pipe] 编码失败: {encoderTag} 退出码 {encExitCode}\n";
                    return (encExitCode != 0 ? encExitCode : -1,
                            $"失败 ({encoderTag} 退出码 {encExitCode})");
                }
            }
            catch (OperationCanceledException)
            {
                return (-1, "已停止");
            }
            catch (Exception ex)
            {
                item.Log += $"[{encoderTag}-pipe] 异常: {ex.Message}\n";
                return (-1, $"失败 (管道异常: {ex.Message})");
            }
            finally
            {
                if (procEnc != null && !procEnc.HasExited)
                {
                    try { procEnc.Kill(entireProcessTree: true); } catch { }
                }
                if (procFf != null && !procFf.HasExited)
                {
                    try { procFf.Kill(entireProcessTree: true); } catch { }
                }
                try { procEnc?.Dispose(); } catch { }
                try { procFf?.Dispose(); } catch { }
            }
        }

        /// <summary>AVIF → GIF/WebP：分轨提取颜色+alpha，再合并编码保留透明通道</summary>
        private async Task ProcessAvifToGifWebpAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            var fmt = item.Options.Format.ToLowerInvariant();
            var tempDir = Path.Combine(PlatformServices.GetTempDir(), $"avif2gifwebp_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                // Step 1: 分轨提取颜色(0:2) + alpha(0:3) → 分别解码避免 alphamerge 压缩色彩
                item.Command = $"[avif→{fmt}] 两步法: 分轨提取颜色+alpha → alphamerge 合并编码";
                item.Log += $"[avif→{fmt}] Step1: 分轨提取颜色+alpha...\n";
                _onItemUpdated?.Invoke(item);

                var colorArgs = $"-y -i \"{item.InputPath}\" -map 0:2 -vsync 0 -frame_pts 1 \"{tempDir}\\c_%04d.png\"";
                var alphaArgs = $"-y -i \"{item.InputPath}\" -map 0:3 -vsync 0 -frame_pts 1 \"{tempDir}\\a_%04d.png\"";

                var colorExit = await FfmpegRunner.RunAsync(colorArgs, s => { item.Log += s; }, AppSettingsService.Current.FfmpegPath);
                if (colorExit != 0) { item.ExitCode = colorExit; item.Status = $"失败 (颜色流 {colorExit})"; return; }

                var hasAlpha = Directory.GetFiles(tempDir, "a_*.png").Length > 0;
                if (!hasAlpha) await FfmpegRunner.RunAsync(alphaArgs, s => { }, AppSettingsService.Current.FfmpegPath);
                hasAlpha = Directory.GetFiles(tempDir, "a_*.png").Length > 0;

                var colorFiles = Directory.GetFiles(tempDir, "c_*.png");
                item.Log += $"[avif→{fmt}]   颜色 {colorFiles.Length} 帧, alpha {(hasAlpha ? "有" : "无")}\n";

                var fps = item.Options.AnimationFps ?? 33;
                string encodeArgs;

                if (fmt == "gif")
                {
                    var dither = item.Options.GifDither ? "=dither=bayer:bayer_scale=5:diff_mode=rectangle" : "";
                    if (hasAlpha)
                    {
                        // 单 pass: color(0:v) + alpha(1:v) → alphamerge → palettegen → paletteuse
                        encodeArgs = $"-y -framerate {fps} -i \"{tempDir}\\c_%04d.png\" -framerate {fps} -i \"{tempDir}\\a_%04d.png\" -filter_complex \"[0:v][1:v]alphamerge,split[a][b];[a]palettegen=reserve_transparent=1:stats_mode=full:max_colors=256[p];[b][p]paletteuse{dither}\" -loop 0 \"{outputPath}\"";
                    }
                    else
                    {
                        encodeArgs = $"-y -framerate {fps} -i \"{tempDir}\\c_%04d.png\" -vf \"split[a][b];[a]palettegen=stats_mode=full:max_colors=256[p];[b][p]paletteuse{dither}\" -loop 0 \"{outputPath}\"";
                    }
                }
                else // webp
                {
                    var quality = item.Options.Quality;
                    var loop = item.Options.AnimationLoop;
                    if (loop < 0) loop = 0;
                    if (hasAlpha)
                    {
                        // 颜色 + alpha 分离帧 → alphamerge 合并 → yuva420p → libwebp_anim
                        encodeArgs = $"-y -framerate {fps} -i \"{tempDir}\\c_%04d.png\" -framerate {fps} -i \"{tempDir}\\a_%04d.png\" -filter_complex \"[0:v][1:v]alphamerge,format=yuva420p\" -c:v libwebp_anim -q:v {quality} -loop {loop} \"{outputPath}\"";
                    }
                    else
                    {
                        encodeArgs = $"-y -framerate {fps} -i \"{tempDir}\\c_%04d.png\" -c:v libwebp_anim -q:v {quality} -loop {loop} \"{outputPath}\"";
                    }
                }

                item.Log += $"[avif→{fmt}] Step2: 编码...\n";
                _onItemUpdated?.Invoke(item);

                var encodeExit = await FfmpegRunner.RunAsync(encodeArgs, s =>
                {
                    item.Log += s; _onItemUpdated?.Invoke(item);
                }, AppSettingsService.Current.FfmpegPath);

                item.ExitCode = encodeExit;
                item.Status = encodeExit == 0 ? $"已完成 (avif→{fmt})" : $"失败 (退出码 {encodeExit})";

                if (encodeExit == 0)
                    await RestoreMetadataAsync(item, outputPath);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>GIF → AVIF 两步法：先提取带透明通道的 PNG 帧，再用 avifenc 编码</summary>
        private async Task ProcessGifToAvifViaAvifencAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            // 优先手动路径（WindowsArtifactsDir 或旧 AvifencPath），回退 ffmpeg 同目录
            var avifencPath = AppSettingsService.Current.AvifencPath;
            if (string.IsNullOrWhiteSpace(avifencPath) || !File.Exists(avifencPath))
            {
                var artifactsDir = AppSettingsService.Current.WindowsArtifactsDir;
                if (!string.IsNullOrWhiteSpace(artifactsDir))
                    avifencPath = Path.Combine(artifactsDir, PlatformServices.Avifenc);
            }
            if (string.IsNullOrWhiteSpace(avifencPath) || !File.Exists(avifencPath))
                avifencPath = Path.Combine(AppSettingsService.Current.FfmpegDir ?? "", PlatformServices.Avifenc);
            if (!File.Exists(avifencPath))
            {
                item.Log += "[gif→avif] avifenc.exe 未找到，回退到 FFmpeg 单命令\n";
                _onItemUpdated?.Invoke(item);
                // fall through to normal FFmpeg path
                var fallbackArgs = FfmpegCommandBuilder.BuildArguments(item.Options, item.InputPath, outputPath);
                item.Command = "ffmpeg " + fallbackArgs;
                var fallbackExit = await FfmpegRunner.RunAsync(fallbackArgs, s =>
                {
                    item.Log += s; _onItemUpdated?.Invoke(item);
                }, AppSettingsService.Current.FfmpegPath);
                item.ExitCode = fallbackExit;
                item.Status = fallbackExit == 0 ? "已完成 (ffmpeg 回退)" : $"失败 (退出码 {fallbackExit})";
                return;
            }

            var tempDir = Path.Combine(PlatformServices.GetTempDir(), $"avifenc_frames_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            item.Command = "[gif→avif] 两步法: ffmpeg 提取RGBA帧 → avifenc 编码动图AVIF";

            try
            {
                // Step 1: 提取 GIF 帧为带 alpha 的 PNG
                item.Log += "[gif→avif] Step1: 提取 GIF 帧为 RGBA PNG...\n";
                _onItemUpdated?.Invoke(item);

                var extractArgs = $"-y -i \"{item.InputPath}\" -pix_fmt rgba -vsync 0 \"{tempDir}\\f_%04d.png\"";
                var extractExit = await FfmpegRunner.RunAsync(extractArgs, s =>
                {
                    item.Log += s;
                    _onItemUpdated?.Invoke(item);
                }, AppSettingsService.Current.FfmpegPath);

                if (extractExit != 0)
                {
                    item.ExitCode = extractExit;
                    item.Status = $"失败 (帧提取退出码 {extractExit})";
                    return;
                }

                var pngFiles = Directory.GetFiles(tempDir, "*.png").OrderBy(f => f).ToList();
                if (pngFiles.Count == 0)
                {
                    item.ExitCode = -1;
                    item.Status = "失败 (未提取到任何帧)";
                    return;
                }

                item.Log += $"[gif→avif]   提取 {pngFiles.Count} 帧\n";

                // Step 2: avifenc 编码为动图 AVIF
                item.Log += "[gif→avif] Step2: avifenc 编码...\n";
                _onItemUpdated?.Invoke(item);

                var fps = item.Options.AnimationFps ?? 33; // 默认 33fps（接近原始 GIF）
                var quality = Math.Max(20, Math.Min(100, item.Options.Quality)); // avifenc -q 范围 0-100
                var speed = Math.Clamp(item.Options.AvifCpuUsed ?? 5, 0, 10);
                var threads = Math.Max(1, item.Options.Threads);

                var avifencArgs = new StringBuilder();
                avifencArgs.Append($"-q {quality} ");
                avifencArgs.Append($"-s {speed} ");
                avifencArgs.Append($"-j {threads} ");
                foreach (var f in pngFiles)
                    avifencArgs.Append($"\"{f}\" ");
                avifencArgs.Append($"-o \"{outputPath}\" ");
                avifencArgs.Append($"--fps {fps} ");
                avifencArgs.Append($"--repetition-count infinite");

                item.Log += $"[gif→avif]   avifenc {avifencArgs}\n";

                var psi = new ProcessStartInfo
                {
                    FileName = avifencPath,
                    Arguments = avifencArgs.ToString(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null) { item.Log += e.Data + "\n"; _onItemUpdated?.Invoke(item); }
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) { item.Log += e.Data + "\n"; _onItemUpdated?.Invoke(item); }
                };

                try
                {
                    process.Start();
                    PlatformServices.SetSafePriority(process, AppSettingsService.Current.FfmpegPriority);
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    await process.WaitForExitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    try { if (!process.HasExited) process.Kill(true); } catch { }
                    item.Status = "已停止";
                    return;
                }

                item.ExitCode = process.ExitCode;
                item.Status = process.ExitCode == 0 ? "已完成 (avifenc 两步法)" : $"失败 (avifenc 退出码 {process.ExitCode})";

                if (process.ExitCode == 0)
                    await RestoreMetadataAsync(item, outputPath);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>JXL 输入智能处理（DJXL 重构 / djxl→cjpegli 管道 / ffmpeg 回退）</summary>
        private async Task ProcessJxlInputAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            var targetFmt = (item.Options.Format ?? "").ToLowerInvariant();
            var jxlType = JxlInspector.DetectType(item.InputPath);
            var jxlTypeName = jxlType switch
            {
                JxlImageType.JpegReconstruction => "JPEG 重构（可从 JXL 无损还原原始 JPEG）",
                JxlImageType.NativeCodestream => "原生 JXL 码流（需解码为像素再编码）",
                _ => "未知/其他"
            };
            item.Log += $"[jxl] 输入类型: {jxlTypeName}\n";
            item.Log += $"[jxl] 目标格式: {targetFmt}  |  djxl: {(DjxlService.IsAvailable ? "可用" : "不可用")}  |  cjpegli: {(CjpegliService.IsAvailable ? "可用" : "不可用")}  |  cjxl: {(CjxlService.IsAvailable ? "可用" : "不可用")}\n";

            if (jxlType == JxlImageType.JpegReconstruction)
            {
                if (DjxlService.IsAvailable)
                {
                    item.Log += "[jxl] 使用 djxl 从 JXL 还原原始 JPEG（无损，不解码像素）\n";
                    item.Command = $"djxl \"{item.InputPath}\" \"{outputPath}\"";
                    var exit = await DjxlService.RunAsync(item.InputPath, outputPath, item.Options.Threads,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
                    item.ExitCode = exit;
                    item.Status = exit == 0 ? "已完成 (djxl 重构 JPEG)" : $"失败 (djxl 退出码 {exit})";
                    if (exit == 0) await RestoreMetadataAsync(item, outputPath);
                }
                else
                {
                    item.Log += "[djxl] 未检测到 djxl，回退到 ffmpeg\n";
                    var args = FfmpegCommandBuilder.BuildArguments(item.Options, item.InputPath, outputPath);
                    item.Command = "ffmpeg " + args;
                    var exit = await FfmpegRunner.RunAsync(args,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                        AppSettingsService.Current.FfmpegPath);
                    item.ExitCode = exit;
                    item.Status = exit == 0 ? "已完成 (ffmpeg)" : $"失败 (退出码 {exit})";
                }
            }
            else if (jxlType == JxlImageType.NativeCodestream)
            {
                // 动图 JXL 不解码为 PNG 中间格式（会丢失动画帧），直接用 ffmpeg 处理
                if (IsAnimated(item))
                {
                    item.Log += "[jxl] 动图 JXL 不进行 djxl→PNG 中间解码，回退到 ffmpeg\n";
                    _onItemUpdated?.Invoke(item);
                    var ffmpegOpts = CloneOptionsForFfmpeg(item.Options);
                    var args2 = FfmpegCommandBuilder.BuildArguments(ffmpegOpts, item.InputPath, outputPath);
                    item.Command = "ffmpeg " + args2;
                    var exit2 = await FfmpegRunner.RunAsync(args2,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                        AppSettingsService.Current.FfmpegPath);
                    item.ExitCode = exit2;
                    item.Status = exit2 == 0 ? "已完成 (ffmpeg)" : $"失败 (退出码 {exit2})";
                    return;
                }

                if (DjxlService.IsAvailable)
                {
                    // ── 判断是否需要 PNG 中转 ──
                    // 仅在以下情况使用 PNG 中间文件（外部编码器不支持 stdin）：
                    //   ① 输出 JPEG/JPEGLI 且 cjpegli 可用 → 管道优先，失败回退 PNG 中转
                    //   ② 输出 JXL 且 cjxl 可用 → cjxl 不支持 stdin，需 PNG 中转
                    // 其他所有输出格式 → djxl 直接管道传给 ffmpeg
                    var usePngIntermediary =
                        (CjpegliService.IsAvailable && (targetFmt == "jpg" || targetFmt == "jpeg" || targetFmt == "jpegli"))
                        || (CjxlService.IsAvailable && targetFmt == "jxl");

                    if (usePngIntermediary)
                    {
                        // ── JXL 目标：优先使用 djxl→cjxl 管道（cjxl 支持 stdin）──
                        if (CjxlService.IsAvailable && targetFmt == "jxl")
                        {
                            item.Log += "[pipeline] 尝试 djxl -> cjxl 管道（无中间文件）\n";
                            var pipeResult = await PipeDjxlToCjxlAsync(item, outputPath, ct);
                            if (pipeResult.exitCode == 0)
                            {
                                item.ExitCode = 0;
                                item.Status = pipeResult.status;
                                await RestoreMetadataAsync(item, outputPath);
                                return;
                            }
                            item.Log += "[pipeline] djxl→cjxl 管道失败，回退到 PNG 中转\n";
                        }

                        // ── JPEG/JPEGLI 目标：优先使用 djxl→cjpegli 管道 ──
                        if (CjpegliService.IsAvailable && (targetFmt == "jpg" || targetFmt == "jpeg" || targetFmt == "jpegli"))
                        {
                            item.Log += "[pipeline] 尝试 djxl -> cjpegli 管道\n";
                            var pipeExit = await JxlPipelineService.TryPipeDjxlToCjpegliAsync(
                                item.InputPath, outputPath, item.Options.Quality, item.Options.Threads,
                                s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
                            if (pipeExit == 0)
                            {
                                item.ExitCode = 0;
                                item.Status = "已完成 (cjpegli 管道)";
                                await RestoreMetadataAsync(item, outputPath);
                                return;
                            }
                            else
                            {
                                item.Log += "[pipeline] 管道失败，回退到临时文件\n";
                            }
                        }

                        // ── 回退/直接：PNG 中转 ──
                        var tmp = Path.Combine(PlatformServices.GetTempDir(), Guid.NewGuid().ToString() + ".png");
                        var tmpCreated = false;
                        try
                        {
                            tmpCreated = await FallbackDjxlDecodeToFile(item, tmp, outputPath, ct);
                        }
                        finally
                        {
                            try { if (tmpCreated && File.Exists(tmp)) File.Delete(tmp); } catch { }
                        }
                    }
                    else
                    {
                        // ── 其他格式：djxl 管道 → ffmpeg ──
                        item.Command = $"djxl \"{item.InputPath}\" --output_format=png - | ffmpeg {BuildFfmpegPipeArguments(item.Options, outputPath)}";
                        await PipeDjxlToFfmpegAsync(item, outputPath, ct);
                    }
                }
                else
                {
                    item.Log += "[djxl] 未检测到 djxl，使用 ffmpeg 直接处理 JXL\n";
                    var args = FfmpegCommandBuilder.BuildArguments(item.Options, item.InputPath, outputPath);
                    item.Command = "ffmpeg " + args;
                    var exit = await FfmpegRunner.RunAsync(args,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                        AppSettingsService.Current.FfmpegPath);
                    item.ExitCode = exit;
                    item.Status = exit == 0 ? "已完成 (ffmpeg)" : $"失败 (退出码 {exit})";
                }
            }
            else
            {
                var args = FfmpegCommandBuilder.BuildArguments(item.Options, item.InputPath, outputPath);
                var exit = await FfmpegRunner.RunAsync(args,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                    AppSettingsService.Current.FfmpegPath);
                item.ExitCode = exit;
                item.Status = exit == 0 ? "已完成 (ffmpeg)" : $"失败 (退出码 {exit})";
            }
        }

        /// <summary>
        /// JXR 输入智能处理：JxrDecApp 解码 JXR → BMP → 根据目标格式选择编码器。
        /// JxrDecApp 不支持 stdout 管道，必须经过磁盘 BMP 中间文件。
        /// </summary>
        private async Task ProcessJxrInputAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            // 查找 JxrDecApp（解码器），与 JxrEncApp（编码器）在同一 artifacts 目录
            var jxrDecPath = ResolveJxrDecAppPath();
            if (string.IsNullOrEmpty(jxrDecPath))
            {
                item.Log += "[jxr] JxrDecApp.exe 未检测到，无法解码 JXR 文件\n";
                item.Log += "[jxr] FFmpeg 不支持 JXR 格式，请将 JxrDecApp.exe 放入 PLAN/artifacts/ 目录\n";
                item.ExitCode = -1;
                item.Status = "失败 (JXR 解码器不可用)";
                return;
            }

            var targetFmt = (item.Options.Format ?? "").ToLowerInvariant();
            item.Log += $"[jxr] 输入: JXR  |  目标格式: {targetFmt}  |  JxrDecApp: 可用  |  cjpegli: {(CjpegliService.IsAvailable ? "可用" : "不可用")}  |  cjxl: {(CjxlService.IsAvailable ? "可用" : "不可用")}\n";

            var tempDir = Path.Combine(PlatformServices.GetTempDir(), $"jxr_input_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                // Step 1: JxrDecApp 解码 JXR → BMP/TIF
                // 注意: JxrDecApp 的 BMP 写入器仅支持 ≤32bpp 常规格式 (24bpp/32bppBGRA/8bit灰度等)。
                // NVIDIA 等工具导出的 HDR JXR (128bppRGBAFloat scRGB) 无法输出 BMP → 自动回退
                // "TIF + -c 0" (24bppRGB)，补丁版 JxrDecApp 内置 scRGB→sRGB 转换 (Convert_Float_To_U8)。
                var intermediatePath = Path.Combine(tempDir, "decoded.bmp");
                item.Log += "[jxr] Step 1: JxrDecApp 解码 JXR → BMP...\n";
                _onItemUpdated?.Invoke(item);

                var decArgs = $"-i \"{item.InputPath}\" -o \"{intermediatePath}\"";
                item.Command = $"JxrDecApp {decArgs}";
                var decExit = await RunJxrDecAppAsync(jxrDecPath, decArgs,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);

                if (decExit != 0 || !File.Exists(intermediatePath))
                {
                    // BMP 输出失败（典型：高位深 / float 像素格式，如 NVIDIA HDR 截图 128bppRGBAFloat）
                    // -c 0 输出 8-bit sRGB TIF（BMP 扩展名会把目标映射为 BGR 导致转换表缺失，故用 TIF）
                    item.Log += $"[jxr]   BMP 输出不支持该像素格式 (退出码 {decExit})，回退 TIF + -c 0 (8-bit sRGB)...\n";
                    _onItemUpdated?.Invoke(item);
                    intermediatePath = Path.Combine(tempDir, "decoded.tif");
                    decArgs = $"-i \"{item.InputPath}\" -o \"{intermediatePath}\" -c 0";
                    item.Command = $"JxrDecApp {decArgs}";
                    decExit = await RunJxrDecAppAsync(jxrDecPath, decArgs,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);

                    if (decExit != 0 || !File.Exists(intermediatePath))
                    {
                        item.ExitCode = decExit;
                        item.Status = $"失败 (JxrDecApp 退出码 {decExit})";
                        return;
                    }
                    item.Log += "[jxr]   TIF 解码成功 (scRGB→sRGB 转换完成)\n";
                }
                var intermediateSize = new FileInfo(intermediatePath).Length;
                item.Log += $"[jxr]   解码完成: {intermediateSize / 1024}KB {Path.GetExtension(intermediatePath).TrimStart('.').ToUpperInvariant()}\n";
                PlatformServices.MarkAsTemporaryFile(intermediatePath); // 提示 OS 优先内存缓存

                // Step 2: 根据目标格式选择编码路径
                var useCjxl = CjxlService.IsAvailable && targetFmt == "jxl";
                var useCjpegli = CjpegliService.IsAvailable && (targetFmt == "jpg" || targetFmt == "jpeg" || targetFmt == "jpegli");
                var useJxrEnc = targetFmt == "jxr";

                if (useCjxl || useCjpegli)
                {
                    // ── cjxl / cjpegli: PPM 管道直通（跳过 PNG 磁盘文件，省 10% 时间 + 21MB 磁盘）──
                    item.Log += $"[jxr] Step 2: ffmpeg {Path.GetExtension(intermediatePath).TrimStart('.').ToUpperInvariant()} → PPM pipe → {(useCjxl ? "cjxl" : "cjpegli")}（无磁盘中间文件）...\n";
                    _onItemUpdated?.Invoke(item);

                    // 临时替换 InputPath 指向中间文件（管道方法使用 item.InputPath 作为 ffmpeg 输入）
                    var savedInputPath = item.InputPath;
                    item.InputPath = intermediatePath;
                    try
                    {
                        (int exitCode, string status) pipeResult;
                        if (useCjxl)
                        {
                            pipeResult = await PipeFfmpegToCjxlAsync(item, outputPath, ct);
                            item.Command = $"ffmpeg -i BMP -compression_level 0 -f image2pipe -vcodec png - | cjxl {CjxlService.BuildCjxlArguments("-", outputPath, item.Options)}";
                        }
                        else
                        {
                            pipeResult = await PipeFfmpegToCjpegliAsync(item, outputPath, ct);
                            item.Command = $"ffmpeg -i BMP -compression_level 0 -f image2pipe -vcodec png - | cjpegli {CjpegliService.BuildCjpegliArguments("-", outputPath, item.Options)}";
                        }

                        if (pipeResult.exitCode == 0)
                        {
                            item.ExitCode = 0;
                            item.Status = pipeResult.status;
                        }
                        else
                        {
                            // ── 管道失败 → 回退 PNG 磁盘中转 ──
                            item.Log += $"[jxr] 管道失败（退出码 {pipeResult.exitCode}），回退 BMP→PNG 磁盘中转...\n";
                            _onItemUpdated?.Invoke(item);
                            var pngPath = Path.Combine(tempDir, "decoded.png");
                            var pngArgs = $"-y -i \"{intermediatePath}\" -compression_level 0 \"{pngPath}\"";
                            var pngExit = await FfmpegRunner.RunAsync(pngArgs,
                                s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                                AppSettingsService.Current.FfmpegPath, ct);

                            if (pngExit != 0 || !File.Exists(pngPath))
                            {
                                item.ExitCode = pngExit;
                                item.Status = $"失败 (BMP→PNG 回退退出码 {pngExit})";
                                return;
                            }

                            if (useCjxl)
                            {
                                item.Command = "cjxl " + CjxlService.BuildCjxlArguments(pngPath, outputPath, item.Options);
                                var cjxlExit = await CjxlService.RunWithOptionsAsync(pngPath, outputPath, item.Options,
                                    s => { item.Log += s; _onItemUpdated?.Invoke(item); });
                                item.ExitCode = cjxlExit;
                                item.Status = cjxlExit == 0 ? "已完成 (JXR→PNG→cjxl 回退)" : $"失败 (cjxl 退出码 {cjxlExit})";
                            }
                            else
                            {
                                item.Command = "cjpegli " + CjpegliService.BuildCjpegliArguments(pngPath, outputPath, item.Options);
                                var cjexit = await CjpegliService.RunWithOptionsAsync(pngPath, outputPath, item.Options,
                                    s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
                                item.ExitCode = cjexit;
                                item.Status = cjexit == 0 ? "已完成 (JXR→PNG→cjpegli 回退)" : $"失败 (cjpegli 退出码 {cjexit})";
                            }
                        }
                    }
                    finally
                    {
                        item.InputPath = savedInputPath;
                    }
                }
                else if (useJxrEnc)
                {
                    // ── JXR → JXR：重新编码（可调整质量/无损）──
                    item.Log += "[jxr] Step 2: JxrEncApp 重新编码 → JXR...\n";
                    _onItemUpdated?.Invoke(item);
                    var quality = item.Options.Lossless ? 1.0 : item.Options.Quality / 100.0;
                    var encArgs = JxrService.BuildArguments(intermediatePath, outputPath, quality);
                    item.Command = "JxrEncApp " + encArgs;
                    var encExit = await JxrService.RunAsync(encArgs,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
                    item.ExitCode = encExit;
                    item.Status = encExit == 0 ? "已完成 (JXR→重新编码→JXR)" : $"失败 (JxrEncApp 退出码 {encExit})";
                }
                else
                {
                    // ── FFmpeg 标准编码：中间文件 → 目标格式 ──
                    item.Log += $"[jxr] Step 2: ffmpeg {Path.GetExtension(intermediatePath).TrimStart('.').ToUpperInvariant()} → {targetFmt.ToUpper()}...\n";
                    _onItemUpdated?.Invoke(item);

                    var ffmpegOpts = CloneOptionsForFfmpeg(item.Options);
                    // 确保 FFmpeg 使用正确的编码器
                    ffmpegOpts.EncoderBackend = EncoderBackend.Ffmpeg;
                    var args = FfmpegCommandBuilder.BuildArguments(ffmpegOpts, intermediatePath, outputPath);
                    item.Command = "ffmpeg " + args;
                    item.Log += $"[cmd] ffmpeg {args}\n";
                    var ffExit = await FfmpegRunner.RunAsync(args,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                        AppSettingsService.Current.FfmpegPath, ct);
                    item.ExitCode = ffExit;
                    item.Status = ffExit == 0 ? $"已完成 (JXR→{Path.GetExtension(intermediatePath).TrimStart('.').ToUpperInvariant()}→ffmpeg {targetFmt})" : $"失败 (ffmpeg 退出码 {ffExit})";
                }

                // ── 元数据恢复 ──
                if (item.ExitCode == 0)
                    await RestoreMetadataAsync(item, outputPath);
            }
            catch (OperationCanceledException)
            {
                item.Status = "已停止";
            }
            catch (Exception ex)
            {
                item.Log += $"[jxr] 异常: {ex.Message}\n";
                item.ExitCode = -1;
                item.Status = $"失败: {ex.Message}";
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// djxl 解码 JXL → stdout(PNG流) → cjxl stdin → 输出 JXL（无磁盘中间文件）。
        /// 管道失败时返回非零，调用方回退到 PNG 中转方案。
        /// </summary>
        private async Task<(int exitCode, string status)> PipeDjxlToCjxlAsync(
            QueueItem item, string outputPath, CancellationToken ct)
        {
            var djxlPath = DjxlService.DetectedPath;
            var cjxlPath = CjxlService.DetectedPath;
            if (string.IsNullOrEmpty(djxlPath) || string.IsNullOrEmpty(cjxlPath))
                return (-1, "失败 (djxl 或 cjxl 未找到)");

            Process? procDj = null;
            Process? procCj = null;
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
                var linkedToken = linked.Token;

                // cjxl: 从 stdin 读取，编码为 JXL
                var cjxlArgs = CjxlService.BuildCjxlArguments("-", outputPath, item.Options);
                var psiCj = new ProcessStartInfo
                {
                    FileName = cjxlPath,
                    Arguments = cjxlArgs,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                // djxl: 解码 JXL → PNG 流输出到 stdout
                var djArgs = $"\"{item.InputPath}\" --output_format=png -";
                var psiDj = new ProcessStartInfo
                {
                    FileName = djxlPath,
                    Arguments = djArgs,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                procCj = Process.Start(psiCj);
                if (procCj == null) return (-1, "失败 (cjxl 启动失败)");
                PlatformServices.SetSafePriority(procCj, AppSettingsService.Current.FfmpegPriority);
                procDj = Process.Start(psiDj);
                if (procDj == null) return (-1, "失败 (djxl 启动失败)");
                PlatformServices.SetSafePriority(procDj, AppSettingsService.Current.FfmpegPriority);

                // 非阻塞消费日志
                var djLogTask = ConsumeStreamLinesAsync(procDj.StandardError,
                    s => item.Log += $"[djxl] {s}\n");
                var cjLogTask = ConsumeStreamLinesAsync(procCj.StandardError,
                    s => { item.Log += $"[cjxl] {s}\n"; _onItemUpdated?.Invoke(item); });
                var cjOutTask = ConsumeStreamLinesAsync(procCj.StandardOutput,
                    s => item.Log += $"[cjxl] {s}\n");

                // 管道传输：djxl stdout → cjxl stdin
                try
                {
                    await procDj.StandardOutput.BaseStream.CopyToAsync(
                        procCj.StandardInput.BaseStream, linkedToken);
                }
                catch (OperationCanceledException) { item.Log += "[djxl→cjxl] 传输取消\n"; }
                catch (Exception ex) { item.Log += $"[djxl→cjxl] 传输错误: {ex.Message}\n"; }

                try { procCj.StandardInput.Close(); } catch { }
                try { await procCj.WaitForExitAsync(linkedToken); } catch { }
                try { await procDj.WaitForExitAsync(CancellationToken.None); } catch { }

                await djLogTask; await cjLogTask; await cjOutTask;

                var exitCode = procCj.HasExited ? procCj.ExitCode : -1;
                if (exitCode == 0)
                {
                    item.Log += "[djxl→cjxl] 管道编码完成\n";
                    return (0, "已完成 (djxl→cjxl 管道)");
                }
                return (exitCode, $"失败 (cjxl 退出码 {exitCode})");
            }
            catch (OperationCanceledException) { return (-1, "已停止"); }
            catch (Exception ex)
            {
                item.Log += $"[djxl→cjxl] 异常: {ex.Message}\n";
                return (-1, $"失败: {ex.Message}");
            }
            finally
            {
                if (procCj != null && !procCj.HasExited) try { procCj.Kill(true); } catch { }
                if (procDj != null && !procDj.HasExited) try { procDj.Kill(true); } catch { }
                try { procCj?.Dispose(); } catch { }
                try { procDj?.Dispose(); } catch { }
            }
        }

        private async Task<bool> FallbackDjxlDecodeToFile(QueueItem item, string tmp, string outputPath, CancellationToken ct)
        {
            // 动图不解码为 PNG 中间格式
            if (IsAnimated(item))
            {
                item.Log += "[djxl] 动图不进行 PNG 中间解码，回退失败\n";
                item.ExitCode = -1;
                item.Status = "失败 (动图不支持 PNG 中转)";
                return false;
            }

            item.Log += "[djxl] 解码 JXL 为中间 PNG\n";
            var exit = await DjxlService.RunAsync(item.InputPath, tmp, item.Options.Threads,
                s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
            if (exit == 0 && File.Exists(tmp))
            {
                PlatformServices.MarkAsTemporaryFile(tmp);
                var targetFmt = (item.Options.Format ?? "").ToLowerInvariant();

                // 根据输出格式选择外部编码器
                if (targetFmt == "jxl" && CjxlService.IsAvailable)
                {
                    item.Log += "[cjxl] 对中间 PNG 进行编码\n";
                    var cjxlExit = await CjxlService.RunWithOptionsAsync(tmp, outputPath, item.Options,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); });
                    item.ExitCode = cjxlExit;
                    item.Status = cjxlExit == 0 ? "已完成 (cjxl, PNG 中转)" : $"失败 (cjxl 退出码 {cjxlExit})";
                    if (cjxlExit == 0) await RestoreMetadataAsync(item, outputPath);
                    return true;
                }
                else if ((targetFmt == "jpg" || targetFmt == "jpeg" || targetFmt == "jpegli") && CjpegliService.IsAvailable)
                {
                    item.Log += "[cjpegli] 对中间文件进行编码\n";
                    var cjexit = await CjpegliService.RunWithOptionsAsync(tmp, outputPath, item.Options,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
                    item.ExitCode = cjexit;
                    item.Status = cjexit == 0 ? "已完成 (cjpegli 编码)" : $"失败 (cjpegli 退出码 {cjexit})";
                    if (cjexit == 0) await RestoreMetadataAsync(item, outputPath);
                    return true;
                }
                else
                {
                    item.Log += "[ffmpeg] 对中间文件编码\n";
                    var args = FfmpegCommandBuilder.BuildArguments(item.Options, tmp, outputPath);
                    item.Command = "ffmpeg " + args;
                    var exit2 = await FfmpegRunner.RunAsync(args,
                        s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                        AppSettingsService.Current.FfmpegPath);
                    item.ExitCode = exit2;
                    item.Status = exit2 == 0 ? "已完成 (ffmpeg)" : $"失败 (ffmpeg 退出码 {exit2})";
                    // ffmpeg with -map_metadata 0 preserves metadata from the input file,
                    // but here the input is tmp PNG which has no metadata, so we still need restore.
                    if (exit2 == 0) await RestoreMetadataAsync(item, outputPath);
                    return true;
                }
            }
            else
            {
                item.ExitCode = exit;
                item.Status = $"失败 (djxl 解码退出码 {exit})";
                return false;
            }
        }

        /// <summary>
        /// 将 djxl 解码输出通过管道直接传给 ffmpeg 编码，避免中间 PNG 临时文件。
        /// 适用于 JXL → PNG/WebP/AVIF/TIFF/GIF 等所有非 JPEG/JXL 目标格式。
        /// </summary>
        private async Task PipeDjxlToFfmpegAsync(QueueItem item, string outputPath, CancellationToken ct)
        {
            var djxl = DjxlService.DetectedPath;
            if (string.IsNullOrEmpty(djxl))
            {
                item.Log += "[pipe] djxl 未找到，回退到 ffmpeg 直接处理\n";
                var args = FfmpegCommandBuilder.BuildArguments(item.Options, item.InputPath, outputPath);
                var exit = await FfmpegRunner.RunAsync(args,
                    s => { item.Log += s; _onItemUpdated?.Invoke(item); },
                    AppSettingsService.Current.FfmpegPath);
                item.ExitCode = exit;
                item.Status = exit == 0 ? "已完成 (ffmpeg)" : $"失败 (退出码 {exit})";
                return;
            }

            item.Log += "[pipe] 尝试 djxl → ffmpeg 管道\n";
            _onItemUpdated?.Invoke(item);

            var ffmpegArgs = BuildFfmpegPipeArguments(item.Options, outputPath);

            Process? procDj = null;
            Process? procFf = null;
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
                var linkedToken = linked.Token;

                var ffmpegPath = AppSettingsService.Current.FfmpegPath;

                // djxl 解码 JXL → PNG 流输出到 stdout
                var djArgs = $"\"{item.InputPath}\" --output_format=png -";
                var psiDj = new ProcessStartInfo
                {
                    FileName = djxl,
                    Arguments = djArgs,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                // ffmpeg 从 stdin 读取 PNG 流
                var psiFf = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = ffmpegArgs,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                procDj = Process.Start(psiDj);
                if (procDj == null)
                {
                    item.Log += "[pipe] 启动 djxl 失败\n";
                    item.ExitCode = -1;
                    item.Status = "失败 (djxl 启动失败)";
                    return;
                }
                PlatformServices.SetSafePriority(procDj, AppSettingsService.Current.FfmpegPriority);

                procFf = Process.Start(psiFf);
                if (procFf == null)
                {
                    item.Log += "[pipe] 启动 ffmpeg 失败\n";
                    item.ExitCode = -1;
                    item.Status = "失败 (ffmpeg 启动失败)";
                    return;
                }
                PlatformServices.SetSafePriority(procFf, AppSettingsService.Current.FfmpegPriority);

                // 启动 stderr/stdout 消费者（非阻塞，并行运行）
                var djErrTask = ConsumeStreamLinesAsync(procDj.StandardError, s => item.Log += $"[djxl] {s}\n");
                var ffLogTask = ConsumeStreamLinesAsync(procFf.StandardError, s =>
                {
                    item.Log += s + "\n";
                    _onItemUpdated?.Invoke(item);
                });
                var ffOutTask = ConsumeStreamLinesAsync(procFf.StandardOutput, s => item.Log += s + "\n");

                // ── 关键：先完成传输 + 关闭 stdin，再等待进程退出（避免死锁）──
                try
                {
                    await procDj.StandardOutput.BaseStream.CopyToAsync(procFf.StandardInput.BaseStream, linkedToken);
                }
                catch (OperationCanceledException)
                {
                    item.Log += "[pipe] 传输被取消\n";
                }
                catch (Exception ex)
                {
                    item.Log += $"[pipe] 传输错误: {ex.Message}\n";
                }

                // 关闭 ffmpeg stdin 以发送 EOF，让 ffmpeg 完成编码
                try { procFf.StandardInput.Close(); } catch { }

                // 等待 ffmpeg 正常退出（stdin 已关闭，ffmpeg 会自行结束）
                try { await procFf.WaitForExitAsync(linkedToken); } catch (OperationCanceledException) { }
                // djxl 应该已经退出（CopyToAsync 完成后 stdout 已读完）
                try { await procDj.WaitForExitAsync(CancellationToken.None); } catch { }

                // 等待日志消费者完成
                await djErrTask;
                await ffLogTask;
                await ffOutTask;

                var ffExitCode = procFf.HasExited ? procFf.ExitCode : -1;
                item.ExitCode = ffExitCode;
                item.Status = ffExitCode == 0 ? "已完成 (djxl→ffmpeg 管道)" : $"失败 (ffmpeg 退出码 {ffExitCode})";
                if (ffExitCode == 0) await RestoreMetadataAsync(item, outputPath);
            }
            catch (OperationCanceledException)
            {
                item.Status = "已停止";
            }
            catch (Exception ex)
            {
                item.Log += $"[pipe] 异常: {ex.Message}\n";
                item.ExitCode = -1;
                item.Status = $"失败 (管道异常: {ex.Message})";
            }
            finally
            {
                // 确保进程被清理
                if (procFf != null && !procFf.HasExited)
                {
                    try { procFf.Kill(entireProcessTree: true); } catch { }
                }
                if (procDj != null && !procDj.HasExited)
                {
                    try { procDj.Kill(entireProcessTree: true); } catch { }
                }
                try { procDj?.Dispose(); } catch { }
                try { procFf?.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// 构建管道中 ffmpeg 的色彩参数。
        /// 返回 (inputArgs, outputArgs)：inputArgs 放 -i 前作输入覆盖，outputArgs 放 -i 后。
        /// </summary>
        private static (string inputArgs, string outputArgs) BuildPipeColorArgs(
            Models.FfmpegOptions options, string? inputPath = null)
        {
            string inputArgs = "", outputArgs = "";
            if (options.UseAdvancedColorParameters
                && (!string.IsNullOrWhiteSpace(options.ColorPrimaries)
                 || !string.IsNullOrWhiteSpace(options.ColorTrc)
                 || !string.IsNullOrWhiteSpace(options.ColorMatrix)))
            {
                if (!string.IsNullOrWhiteSpace(options.ColorPrimaries))
                    inputArgs += $"-color_primaries {options.ColorPrimaries} ";
                if (!string.IsNullOrWhiteSpace(options.ColorTrc))
                    inputArgs += $"-color_trc {options.ColorTrc} ";
                if (!string.IsNullOrWhiteSpace(options.ColorMatrix))
                    outputArgs += $"-colorspace {options.ColorMatrix} ";
            }
            else if (!string.IsNullOrWhiteSpace(options.ColorSpace)
                     && !options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var cs = options.ColorSpace switch
                {
                    "BT.601" => ("bt470bg", "bt470bg"),
                    "BT.709" => ("bt709", "bt709"),
                    "BT.2020" => ("bt2020", "bt2020nc"),
                    _ => ("bt709", "bt709")
                };
                // BT.2020 HDR: gamma 输入 → PQ 输出 (Rec.2100)
                var trc = options.ColorSpace == "BT.2020" ? "smpte2084" : "bt709";
                if (options.ColorSpace == "BT.2020")
                {
                    // 源为 gamma (bt709)，转换为 PQ (smpte2084)
                    // format=rgb48le 前缀（RGB 域转换）：规避 libzimg 对 4:2:0 的尺寸整除要求
                    // （奇数尺寸 1027 错误）及 RGB 输入无标签时的 3074 错误
                    inputArgs += $"-color_primaries {cs.Item1} -color_trc bt709 ";
                    outputArgs += "-vf \"format=rgb48le,zscale=transferin=bt709:transfer=smpte2084\" ";
                }
                else
                {
                    inputArgs += $"-color_primaries {cs.Item1} -color_trc {trc} ";
                }
                outputArgs += $"-colorspace {cs.Item2} ";
            }
            // auto 模式：探测输入属性。16-bit 且无标签时默认 Rec.2020→sRGB 转换
            else if (!string.IsNullOrEmpty(inputPath)
                     && (string.IsNullOrWhiteSpace(options.ColorSpace)
                         || options.ColorSpace.Equals("auto", StringComparison.OrdinalIgnoreCase)))
            {
                var hdrMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(inputPath);
                if (hdrMeta.bitDepth > 8)
                {
                    if (!string.IsNullOrEmpty(hdrMeta.colorPrimaries))
                    {
                        inputArgs += $"-color_primaries {hdrMeta.colorPrimaries} ";
                    }
                    else
                    {
                        // 16-bit 无色彩标签（如 TIFF ICC）→ 默认 Rec.2020 转 sRGB
                        // format=rgb48le 前缀（RGB 域转换）：规避 libzimg 对 4:2:0 的尺寸整除要求
                        // （奇数尺寸 1027 错误）及 RGB 输入无标签时的 3074 错误
                        outputArgs += "-vf \"format=rgb48le,zscale=primariesin=bt2020:primaries=bt709:transferin=bt709:transfer=iec61966-2-1\" ";
                    }
                    if (!string.IsNullOrEmpty(hdrMeta.colorTrc))
                        inputArgs += $"-color_trc {hdrMeta.colorTrc} ";
                    if (!string.IsNullOrEmpty(hdrMeta.colorSpace))
                        outputArgs += $"-colorspace {hdrMeta.colorSpace} ";
                }
            }
            return (inputArgs, outputArgs);
        }

        /// <summary>构建 ffmpeg 从 stdin 读取 PPM 流的命令行参数</summary>
        private static string BuildFfmpegPipeArguments(Models.FfmpegOptions options, string outputPath)
        {
            // 以 stdin (-) 为输入，用 image2pipe 格式指定 PPM 流
            var args = FfmpegCommandBuilder.BuildArguments(options, "-", outputPath);
            // 插入 -f image2pipe 到 -i - 之前
            var idx = args.IndexOf("-i \"-\"", StringComparison.Ordinal);
            if (idx >= 0)
            {
                args = args.Substring(0, idx) + "-f image2pipe " + args.Substring(idx);
            }
            else
            {
                // 如果 BuildArguments 生成的格式不同，兼容处理
                idx = args.IndexOf("-i -", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    args = args.Substring(0, idx) + "-f image2pipe " + args.Substring(idx);
                }
            }
            return args;
        }

        private static async Task ConsumeStreamLinesAsync(StreamReader reader, Action<string> onLine)
        {
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    onLine(line);
                }
            }
            catch { }
        }

        /// <summary>动图回退：将外部工具参数克隆为 FFmpeg 内置编码器参数</summary>
        private static Models.FfmpegOptions CloneOptionsForFfmpeg(Models.FfmpegOptions original)
        {
            var fmt = (original.Format ?? "").ToLowerInvariant();
            string encoder = fmt switch
            {
                "avif" => "libsvtav1",  // SVT-AV1 对 yuva420p 支持比 libaom-av1 更可靠
                "jxl" => "libjxl_anim",
                "jpg" or "jpeg" or "jpegli" => "mjpeg",
                "png" or "apng" => "apng",
                "webp" => "libwebp_anim",
                _ => original.Encoder ?? ""
            };

            return new Models.FfmpegOptions
            {
                Format = original.Format ?? "avif",
                Quality = original.Quality,
                Chroma = original.Chroma,
                BitDepth = original.BitDepth,
                ColorSpace = original.ColorSpace,
                UseAdvancedColorParameters = original.UseAdvancedColorParameters,
                ColorPrimaries = original.ColorPrimaries,
                ColorTrc = original.ColorTrc,
                ColorMatrix = original.ColorMatrix,
                Threads = original.Threads,
                MetadataMode = original.MetadataMode,
                Encoder = encoder,
                EncoderBackend = EncoderBackend.Ffmpeg,
                Lossless = original.Lossless,
                // 动图参数
                AnimationFps = original.AnimationFps,
                AnimationLoop = original.AnimationLoop,
                GifPaletteOptimize = original.GifPaletteOptimize,
                GifDither = original.GifDither,
                AnimationScaleW = original.AnimationScaleW,
                // AVIF 动图强制 still-picture=0
                AvifStillPicture = false,
                AvifCpuUsed = original.AvifCpuUsed,
                AvifRowMt = original.AvifRowMt,
                AvifTune = original.AvifTune,
                AvifPreset = original.AvifPreset,
                // JXL
                JxlEffort = original.JxlEffort,
                JxlModular = original.JxlModular,
                // 编码器私有选项
                PngPred = original.PngPred,
                WebpPreset = original.WebpPreset,
                WebpCompressionLevel = original.WebpCompressionLevel,
                JpegHuffman = original.JpegHuffman,
                JpegDct = original.JpegDct,
                JpegGainMap = original.JpegGainMap,
                JpegGainMapQuality = original.JpegGainMapQuality,
                JpegGainMapTargetNits = original.JpegGainMapTargetNits,
                JpegGainMapHdrCf = original.JpegGainMapHdrCf,
                JpegGainMapDownsample = original.JpegGainMapDownsample,
                JpegGainMapMultiChannel = original.JpegGainMapMultiChannel,
                TiffCompressionAlgo = original.TiffCompressionAlgo,
                // ExifTool
                StripExifGps = original.StripExifGps,
                StripExifTime = original.StripExifTime,
                StripExifCamera = original.StripExifCamera,
                StripExifAll = original.StripExifAll,
                StripXmp = original.StripXmp,
                // Cjxl/Cjpegli 选项（回退时忽略，FFmpeg 不使用）
                CjxlProgressive = false,
                CjxlPhotonNoiseIso = 0,
                CjpegliChromaSubsampling = "auto",
                CjpegliProgressiveId = -1,
                CjpegliOptimize = true,
                CjpegliAdaptiveQuant = true,
                CjpegliEncoderBackend = "libjpeg",
                CjpegliPsnrTarget = 0,
                CjpegliMultiThreadAvailable = false,
            };
        }

        /// <summary>判断 avifenc.exe 是否可用（手动路径 > artifacts目录 > PLAN文件夹 > ffmpeg同目录）</summary>
        private static bool HasAvifencAvailable()
        {
            var manualPath = AppSettingsService.Current.AvifencPath;
            if (!string.IsNullOrWhiteSpace(manualPath) && File.Exists(manualPath))
                return true;
            // artifacts 目录
            var artifactsDir = AppSettingsService.Current.WindowsArtifactsDir;
            if (!string.IsNullOrWhiteSpace(artifactsDir) && File.Exists(Path.Combine(artifactsDir, PlatformServices.Avifenc)))
                return true;
            // PLAN 便携文件夹
            if (PlatformServices.TryFindInPlanFolder(PlatformServices.Avifenc) != null)
                return true;
            return File.Exists(Path.Combine(AppSettingsService.Current.FfmpegDir ?? "", PlatformServices.Avifenc));
        }

        // ═══════════════════════════════════════════════
        // JXR 解码器辅助方法（JxrDecApp）
        // ═══════════════════════════════════════════════

        /// <summary>解析 JxrDecApp.exe 路径（解码器，与编码器同目录）</summary>
        private static string? ResolveJxrDecAppPath()
        {
            // ① PLAN 便携包自动检测（与 JxrEncApp 同目录 artifacts/）
            var planFound = PlatformServices.TryFindInPlanFolder(PlatformServices.JxrDec);
            if (planFound != null && File.Exists(planFound)) return planFound;

            // ② 从 JxrEncApp 同目录推断
            var encPath = JxrService.DetectedPath;
            if (!string.IsNullOrEmpty(encPath))
            {
                var dir = Path.GetDirectoryName(encPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    var candidate = Path.Combine(dir, PlatformServices.JxrDec);
                    if (File.Exists(candidate)) return candidate;
                }
            }

            // ③ 程序同目录
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var local = Path.Combine(exeDir, PlatformServices.JxrDec);
            if (File.Exists(local)) return local;

            // ④ 系统 PATH
            if (PlatformServices.TryFindInPath(PlatformServices.JxrDec, out var pathFound))
                return pathFound;

            return null;
        }

        /// <summary>运行 JxrDecApp 解码器</summary>
        private static async Task<int> RunJxrDecAppAsync(
            string jxrDecPath, string arguments,
            Action<string>? logCallback, CancellationToken ct)
        {
            logCallback?.Invoke($"[jxr-dec] {Path.GetFileName(jxrDecPath)} {arguments}\n");

            var psi = new ProcessStartInfo
            {
                FileName = jxrDecPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) =>
            { if (e.Data != null) logCallback?.Invoke(e.Data + Environment.NewLine); };
            process.ErrorDataReceived += (_, e) =>
            { if (e.Data != null) logCallback?.Invoke(e.Data + Environment.NewLine); };

            try
            {
                process.Start();
                PlatformServices.SetSafePriority(process, AppSettingsService.Current.FfmpegPriority);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (ct.CanBeCanceled)
                {
                    using var reg = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
                    try { await process.WaitForExitAsync(ct); }
                    catch (OperationCanceledException) { try { if (!process.HasExited) process.Kill(true); } catch { } throw; }
                }
                else { await process.WaitForExitAsync(); }
                return process.ExitCode;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logCallback?.Invoke($"[jxr-dec] 启动失败: {ex.Message}\n");
                return -1;
            }
        }

        /// <summary>判断队列项是否为动图（输出为GIF/APNG、设有帧率、或输入为动图文件）</summary>
        private static bool IsAnimated(QueueItem item)
        {
            var fmt = (item.Options.Format ?? "").ToLowerInvariant();
            // 输出格式为 GIF / APNG → 始终是动图
            if (fmt is "gif" or "apng") return true;
            // 用户设置了帧率 → 动图模式
            if (item.Options.AnimationFps.HasValue) return true;
            // 输入文件是动图格式（.gif / .apng / .avif 可能含动画）
            var inputExt = Path.GetExtension(item.InputPath).ToLowerInvariant();
            if (inputExt is ".gif" or ".apng") return true;
            return false;
        }
    }
}

