using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace FfmpegGui.Services
{
    /// <summary>
    /// PNG 出口优化器（oxipng 10.2.1 三档后置 IDAT 重压）——png-optimizer-integration T3，契约 <b>C-EXEC@1</b>。
    /// <para>
    /// 定位：PNG 出口的<b>可选</b>后置重压步骤（不替换 ffmpeg 编码器、不在 ffmpeg 命令行加参数）。
    /// 插入点由 QueueProcessor 两处接线保证：主分支 <c>RestoreMetadataAsync</c> 之后、
    /// <c>ApplyColorPostStepsAsync</c> 之前（顺序 A：先压缩后嵌 chunk —— oxipng 只重压 IDAT，
    /// iCCP/cICP/sBIT/eXIf 全部在其之后写入，既有色彩后置五步零感知）。
    /// </para>
    /// <para>
    /// 回落语义（六分支，任何分支都<b>不修改原始像素数据、不判任务失败</b>）：
    /// ① exit 0 且 out 存在且更小 → 覆盖原产物并播报成功；
    /// ② exit 0 且 out 不存在（oxipng 已知行为：仅在产物更小时写出 <c>--out</c>）→ 保留原样；
    /// ③ exit 0 但 out ≥ 原（防御分支）→ 保留原样；④ exit ≠ 0 → 保留原样；
    /// ⑤ 600s 进程级超时（R4.1：oxipng 自带 <c>--timeout</c> 对大图高压缩档有效性有限，
    /// 进程级等待是唯一权威保护）→ kill → 保留原样；⑥ 任一异常 → 保留原样。
    /// 临时件（<c>&lt;name&gt;.pngopt-tmp/out-&lt;guid&gt;.png</c>）与产物同目录（AGENTS §3：不落 %TEMP%）、
    /// <c>finally</c> 删除。所有播报前缀 <c>[pngopt]</c>，文案逐字锁定于 C-EXEC@1。
    /// </para>
    /// </summary>
    public static class PngOptimizerService
    {
        /// <summary>进程级单文件超时（秒）。R4.1：与 benchmark R3.4 口径一致的唯一权威保护。</summary>
        public const int TimeoutSeconds = 600;

        /// <summary>
        /// 对静态 PNG 产物执行 oxipng 优化（档位由 <paramref name="mode"/> 决定）。
        /// </summary>
        /// <param name="outputPath">PNG 产物绝对路径（编码完成、元数据恢复之后的产物）。</param>
        /// <param name="mode">
        /// 档位：null/空/"off" ⇒ 直接返回（R1.1：零播报零触碰，产物与禁用该功能前逐字节一致）；
        /// "default" ⇒ 按 C-DEFARGV@1 level-2 保守参数集执行；
        /// "max"/"zopfli15"/"zopfli30" ⇒ 按 C-PARAMS@1 保守参数集执行。
        /// 其他值（合法流转不可能出现：CLI 走 ParseTokenStrict、GUI 值数组封闭）⇒ 防御性播报后跳过。
        /// </param>
        /// <param name="log">播报回调（每条一行；接线方负责换行与 UI 刷新）。</param>
        /// <param name="ct">取消令牌：外部取消时 kill 子进程并原样传播（取消不播 [pngopt] 行，交上层取消路径）。</param>
        public static async Task ApplyPngOptimizerAsync(string outputPath, string? mode, Action<string> log, CancellationToken ct)
        {
            // ── R1.1 off 短路：零播报零触碰（判据：日志零 [pngopt] 行 + 产物 sha256 逐字节不变）──
            var m = mode?.Trim();
            if (string.IsNullOrEmpty(m) || m.Equals("off", StringComparison.OrdinalIgnoreCase)) return;

            ct.ThrowIfCancellationRequested();

            // ── 格式门（C-EXEC@1：仅静态 PNG 执行）──
            // 调用方（QueueProcessor 两处接线）已按 options.Format is "png" or "apng" 保证只对 PNG 家族调用；
            // 此处按产物扩展名再防御一道：非 .png 直接退（不播报——非 PNG 出口不该走到这里）。
            if (!string.Equals(Path.GetExtension(outputPath), ".png", StringComparison.OrdinalIgnoreCase)) return;

            // APNG 出口：产物扩展名也是 .png（ffmpeg -f apng 强制写 .png 容器，见 FfmpegCommandBuilder case "apng"）
            // ⇒ 扩展名判不出动图，按 PNG chunk 结构找 acTL（APNG 标志 chunk，规范要求其位于 IDAT 之前）。
            if (ContainsActlBeforeIdat(outputPath))
            {
                log("[pngopt] APNG 动图出口不优化，已跳过");
                return;
            }

            // ── 未知档位防御（最后一道闸，不静默）：值域封闭 = off|default|max|zopfli15|zopfli30
            //    （C-PARAMS@1 + C-DEFARGV@1；off 已在上游短路，此处合法入参为其余四档）──
            var mv = m.ToLowerInvariant();
            if (mv is not ("default" or "max" or "zopfli15" or "zopfli30"))
            {
                log($"[pngopt] ⚠️ 未知的优化档位（{m}），已跳过（合法值：default / max / zopfli15 / zopfli30）");
                return;
            }

            // ── C-RESOLVE@1 解析：AppSettings 显式键 → PLAN/artifacts 惯例位 → 皆无 ⇒ null ──
            var oxipng = ResolveOxipngPath();
            if (oxipng == null)
            {
                // R5.5：工具缺失不判任务失败，但跳过 ≠ 成功——必须出声（照 AGENTS §7 exiftool 同族纪律）。
                log("[pngopt] 未检测到 oxipng，已跳过优化（期望位置 PLAN/artifacts/oxipng.exe，或在设置中指定路径）");
                return;
            }

            // 临时件：与产物同目录（同盘 ⇒ File.Move 覆盖是同卷操作；AGENTS §3「用完即删」纪律，
            // GUID 防并发/重跑撞名；oxipng 无已知 TEMP 写入，进程继承宿主已钉的 TMP 根）。
            var name = Path.GetFileNameWithoutExtension(outputPath);
            var dir = Path.GetDirectoryName(outputPath) ?? ".";
            var guid = Guid.NewGuid().ToString("N");
            var tmpIn = Path.Combine(dir, $"{name}.pngopt-tmp-{guid}.png");
            var tmpOut = Path.Combine(dir, $"{name}.pngopt-out-{guid}.png");

            try
            {
                var origSize = new FileInfo(outputPath).Length;
                File.Copy(outputPath, tmpIn, overwrite: true);

                var args = BuildArgs(m, tmpIn, tmpOut);
                var psi = new ProcessStartInfo
                {
                    FileName = oxipng,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    // oxipng（Rust）输出为 UTF-8；P2-5 教训：默认按控制台代码页解码，非 ASCII 输出会乱码。
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                foreach (var a in args) psi.ArgumentList.Add(a);

                var sw = Stopwatch.StartNew();
                using var proc = Process.Start(psi);
                if (proc == null) throw new InvalidOperationException("oxipng 进程启动失败（Process.Start 返回 null）");

                // 本仓范式（ProbeCjpegliVersion / FfmpegCommandBuilder.ProbeWithFfprobe）：
                // 先挂读取任务、再等退出 —— 同步读排在 WaitForExit 之后 ⇒ 管道缓冲满即死锁、超时分支成死代码。
                var outTask = proc.StandardOutput.ReadToEndAsync();
                var errTask = proc.StandardError.ReadToEndAsync();

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
                try
                {
                    await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                    proc.WaitForExit();   // 混合模式收尾：确保退出事件 flush、ExitCode 可靠（进程已退出 ⇒ 立即返回）
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    TryKill(proc);
                    throw;   // 外部取消：临时件清理交 finally，取消语义交上层 ProcessAsync 的取消路径
                }
                catch (OperationCanceledException)
                {
                    // 分支⑤：600s 超时 → kill → 保留原样（R4.3：点名超时 + 换档提示；zopfli 档 × 大图预期内正常路径）
                    TryKill(proc);
                    DrainOutput(outTask, errTask);
                    log($"[pngopt] ⚠️ 优化超时（{TimeoutSeconds}s 上限），保留原样（档位 {m}；大图建议改用 max 档）");
                    return;
                }

                var code = proc.ExitCode;
                if (code == 0)
                {
                    var outFi = new FileInfo(tmpOut);
                    if (!outFi.Exists)
                    {
                        // 分支②：oxipng 仅在产物更小时写出 --out，无法更小 ⇒ out 不存在（tools.json known_pitfalls 实证）——
                        // 正常路径，不判失败（R4.4）。
                        log($"[pngopt] 无法产生更小产物，保留原样（档位 {m}）");
                        return;
                    }
                    var newSize = outFi.Length;
                    if (newSize >= origSize)
                    {
                        // 分支③：防御分支（oxipng「仅更小才写出」语义下不该发生）。
                        log($"[pngopt] 优化产物未小于原产物，保留原样（档位 {m}）");
                        return;
                    }

                    // 分支①：File.Move(overwrite) —— 失败时原产物原地不动
                    //（File.Copy 半途失败会损毁原产物，违背 R4.2「不产出损坏文件」，故不用 Copy）。
                    File.Move(tmpOut, outputPath, overwrite: true);
                    var pct = origSize > 0 ? (origSize - newSize) * 100.0 / origSize : 0.0;
                    log($"[pngopt] 已压缩（档位 {m}）：{origSize} → {newSize} 字节（-{pct:F1}%，耗时 {sw.Elapsed.TotalSeconds:F1}s）");
                    return;
                }

                // 分支④：exit ≠ 0 → 保留原样。oxipng 的 stdout/stderr 不进用户日志（契约文案无内容位），
                // 落诊断 Trace 供事后取证。
                DrainOutput(outTask, errTask);
                log($"[pngopt] ⚠️ 优化失败（exit {code}），保留原样（档位 {m}）");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // 外部取消不被 ⑥ 吞：原样传播（finally 照常清理临时件）
            }
            catch (Exception ex)
            {
                // 分支⑥：任一异常 → 保留原样，不判任务失败。
                log($"[pngopt] ⚠️ 优化异常（{ex.GetType().Name}: {ex.Message}），保留原样（档位 {m}）");
            }
            finally
            {
                TryDelete(tmpIn);
                TryDelete(tmpOut);
            }
        }

        /// <summary>
        /// 按 C-PARAMS@1 / C-DEFARGV@1 终值表构造四执行档 argv（公开给判据面作 argv 快照断言的单一出处）。
        /// 保守参数集：<c>--interlace keep</c>（保持隔行模式，<c>-i</c> 默认 off 会移除隔行）+
        /// <c>--nb</c>（位深不变）+ <c>--nc</c>（色型不变）+ <c>--np</c>（不转 indexed）+ <c>--ng</c>（不改灰度）；
        /// 显式不传：<c>--strip</c>/<c>-a</c>（结构/alpha 变换）、<c>--fix/--force</c>（保持「仅更小才写出」语义）、
        /// <c>--timeout</c>（进程级等待取代，见 <see cref="TimeoutSeconds"/>）、<c>--nx/--nz</c>（与优化目标矛盾）。
        /// </summary>
        /// <param name="mode">default | max | zopfli15 | zopfli30（其他值抛 ArgumentException——值域封闭；
        /// off 不到达此处——ApplyPngOptimizerAsync 上游已短路）。</param>
        /// <param name="inputPath">oxipng 输入（临时输入副本）。</param>
        /// <param name="outputPath">oxipng 输出（--out 目标，临时件）。</param>
        public static IReadOnlyList<string> BuildArgs(string mode, string inputPath, string outputPath)
        {
            var m = (mode ?? "").Trim().ToLowerInvariant();
            // default 档（C-DEFARGV@1）：`<in> -o 2 --interlace keep --nb --nc --np --ng --out <out>`
            // —— oxipng `--opt` 默认即 2，此处显式化；max/zopfli 沿 C-PARAMS@1。
            var args = new List<string> { inputPath, "-o", m == "default" ? "2" : "max" };
            switch (m)
            {
                case "max":
                case "default":
                    break;
                case "zopfli15":
                    args.AddRange(new[] { "-z", "--zi", "15" });   // --zi 必须与 -z 同传（--help：requires --zopfli）
                    break;
                case "zopfli30":
                    args.AddRange(new[] { "-z", "--zi", "30" });
                    break;
                default:
                    throw new ArgumentException($"未知档位：{mode}（合法值：default / max / zopfli15 / zopfli30）", nameof(mode));
            }
            args.AddRange(new[] { "--interlace", "keep", "--nb", "--nc", "--np", "--ng", "--out", outputPath });
            return args;
        }

        /// <summary>
        /// C-RESOLVE@1 解析链：① AppSettings 显式键（非空且文件存在）；② PLAN/artifacts 惯例位。
        /// ②与 <c>ExternalToolsDetector.FindInArtifactsOrPlan</c>（:908，private）同语义三步：
        /// 用户 artifacts 目录 → <see cref="PlatformServices.TryFindInPlanFolder"/>（完整名 + 去扩展名兼底）→
        /// ffmpeg 同目录。该类是 private 方法且 ExternalToolsDetector.cs 不属 T3 write_set ⇒ 此处按同语义复刻；
        /// 展示面（ProbeAllTools oxipng 段）与执行面解析顺序一致（P7f：面板说可用 ⇔ 队列真能用），
        /// 漂移由 verify-tool-detection 门禁兜底。
        /// </summary>
        /// <returns>可执行的 oxipng 路径；皆无 ⇒ null（调用方播报跳过，不是失败）。</returns>
        private static string? ResolveOxipngPath()
        {
            // ① AppSettings 显式键（C-SETTINGS@1；失效时只回退解析，不覆写显式值——B1 口径）
            var explicitPath = AppSettingsService.Current.PngOptimizerPath;
            if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return explicitPath;

            // ② artifacts 目录 / PLAN 文件夹 / ffmpeg 同目录（FindInArtifactsOrPlan 同语义）
            var artifactsDir = AppSettingsService.Current.WindowsArtifactsDir;
            if (!string.IsNullOrWhiteSpace(artifactsDir))
            {
                var p = Path.Combine(artifactsDir, PlatformServices.OxipngName);
                if (File.Exists(p)) return p;
            }
            var planFound = PlatformServices.TryFindInPlanFolder(PlatformServices.OxipngName);
            if (planFound == null)
            {
                var nameNoExt = Path.GetFileNameWithoutExtension(PlatformServices.OxipngName);
                if (nameNoExt != PlatformServices.OxipngName)
                    planFound = PlatformServices.TryFindInPlanFolder(nameNoExt);
            }
            if (planFound != null) return planFound;
            var ffDir = AppSettingsService.Current.FfmpegDir;
            if (!string.IsNullOrWhiteSpace(ffDir))
            {
                var p = Path.Combine(ffDir, PlatformServices.OxipngName);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        /// <summary>
        /// PNG chunk 结构级遍历判断是否 APNG（acTL 标志 chunk）。
        /// 规范要求 acTL 位于 IDAT 之前 ⇒ 见 acTL 即 APNG（true）、先见 IDAT 即静态（false）。
        /// 结构级遍历而非裸字节搜索：iCCP/eXIf 的数据块理论上可含 "acTL" 四字节序列，裸搜会误判
        /// （误判方向虽保守——跳过优化并播报——结构遍历零误判且成本相同）。解析失败/非 PNG 容器 ⇒ false
        ///（按静态处理，后续优化路径会自己报错）。
        /// </summary>
        private static bool ContainsActlBeforeIdat(string pngPath)
        {
            try
            {
                using var fs = File.OpenRead(pngPath);
                var hdr = new byte[8];
                if (!ReadFull(fs, hdr) || !hdr.AsSpan().SequenceEqual(PngSignature)) return false;
                while (ReadFull(fs, hdr))
                {
                    uint len = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0, 4));
                    bool isActl = hdr[4] == (byte)'a' && hdr[5] == (byte)'c' && hdr[6] == (byte)'T' && hdr[7] == (byte)'L';
                    if (isActl) return true;
                    bool isIdat = hdr[4] == (byte)'I' && hdr[5] == (byte)'D' && hdr[6] == (byte)'A' && hdr[7] == (byte)'T';
                    if (isIdat) return false;
                    // 跳过数据 + CRC（(long) 强转防 uint+4 回绕）
                    if (fs.Seek((long)len + 4, SeekOrigin.Current) < 0) return false;
                }
                return false;
            }
            catch { return false; }
        }

        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        private static bool ReadFull(FileStream fs, byte[] buf)
        {
            int off = 0;
            while (off < buf.Length)
            {
                int n = fs.Read(buf, off, buf.Length - off);
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        /// <summary>kill 进程树（超时/取消路径）。收尸等待避免僵尸句柄延迟释放。</summary>
        private static void TryKill(Process proc)
        {
            try
            {
                if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                proc.WaitForExit();
            }
            catch { /* 已退出/句柄失效 ⇒ 目标已达 */ }
        }

        /// <summary>吞掉读取任务的异常（kill 后流关闭，读取任务可能以异常完成；此处只作诊断落 Trace）。</summary>
        private static void DrainOutput(Task<string> outTask, Task<string> errTask)
        {
            try
            {
                Task.WaitAll(new[] { outTask, errTask }, TimeSpan.FromSeconds(2));
                var stdout = outTask.Status == TaskStatus.RanToCompletion ? outTask.Result : "";
                var stderr = errTask.Status == TaskStatus.RanToCompletion ? errTask.Result : "";
                if (!string.IsNullOrWhiteSpace(stdout) || !string.IsNullOrWhiteSpace(stderr))
                    Trace.WriteLine($"[pngopt] oxipng stdout={stdout.Trim()} stderr={stderr.Trim()}");
            }
            catch { /* 诊断用途，失败不追 */ }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* 尽力删除；残留件名带 GUID，不与后续轮次冲突（AGENTS §3 临时件纪律） */ }
        }
    }
}