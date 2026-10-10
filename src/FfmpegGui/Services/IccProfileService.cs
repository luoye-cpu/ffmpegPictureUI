using System;
using System.IO;
using System.Threading;

namespace FfmpegGui.Services
{
    /// <summary>
    /// ICC 配置文件解析与验证服务。
    /// 支持读取 ICC 文件头、设备类别、色彩空间、描述标签，
    /// 并推断常见标准色彩空间名称。
    /// </summary>
    public static class IccProfileService
    {
        /// <summary>
    /// 用 exiftool 从图片提取内嵌 ICC 配置文件到临时文件。
    /// 返回 (临时文件路径, ICC 描述)；无 ICC / exiftool 不可用 / 失败时返回 (null, null)。
    /// 调用方负责删除返回的临时文件。
    /// 带 5 秒超时保护：exiftool 挂起时杀死进程，防止阻塞编码队列。
    /// </summary>
    /// <param name="log">日志回调（取消/超时分支点名用；null ⇒ 退回 Trace.WriteLine）</param>
    /// <param name="ct">取消令牌（`#26`：上游 7 条路径**全部**持有 `ct` 却一条都没传下来，本次接通）</param>
    public static (string? path, string? description) ExtractIccToTempFile(string imagePath,
        Action<string>? log = null, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
                return (null, null);
            if (!ExifToolService.IsAvailable)
                return (null, null);

            var tmp = Path.Combine(PlatformServices.GetTempDir(), $"icc_extract_{Guid.NewGuid():N}.icc");
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = ExifToolService.DetectedPath!,
                Arguments = $"-b -icc_profile \"{imagePath}\"",
                RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                RedirectStandardError = true,   // P2-3：排空见下方 p?.BeginErrorReadLine()
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            p?.BeginErrorReadLine();   // P2-3：stderr 不排空会在管道缓冲(~4KB)写满时把子进程堵死，我们等 stdout/WaitForExit 就永等（实测可复现）
            if (p == null) return (null, null);

            // RedirectStandardOutput 下 StandardOutput.BaseStream 是原始二进制流，可安全 CopyTo。
            // 注意：读取放后台任务，主线程 WaitForExit(5s) 超时 → Kill（避免进程挂起时 CopyTo 无限阻塞）。
            using var ms = new MemoryStream();
            // `#26`：同步 `WaitForExit(5000)` 不接受 ct ⇒ 用 `ct.Register` 杀树接线（不改签名、不改调用方）；
            // 原有 5s 超时**不变**（外部取消与超时先到者生效）。
            using var reg = ct.Register(() => { try { p?.Kill(entireProcessTree: true); } catch { } });
            var readTask = Task.Run(() => p.StandardOutput.BaseStream.CopyTo(ms));
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }   // 顺带升级：旧 `p.Kill()` 不杀进程树
                TryDelete(tmp);
                var tmsg = $"[icc] ICC 提取超时（5s）⇒ 已终止：{Path.GetFileName(imagePath)}（按无 ICC 处理）";
                if (log != null) log(tmsg + "\n"); else System.Diagnostics.Trace.WriteLine(tmsg);
                return (null, null);
            }
            if (ct.IsCancellationRequested)
            {
                TryDelete(tmp);
                var cmsg = $"[icc] ICC 提取被取消 ⇒ 已终止：{Path.GetFileName(imagePath)}（按无 ICC 处理）";
                if (log != null) log(cmsg + "\n"); else System.Diagnostics.Trace.WriteLine(cmsg);
                return (null, null);
            }
            try { readTask.Wait(1000); } catch { }  // 进程已退出，读取应立即完成

            if (ms.Length < 128) { TryDelete(tmp); return (null, null); }
            File.WriteAllBytes(tmp, ms.ToArray());
            if (!IsValidIccProfile(tmp)) { TryDelete(tmp); return (null, null); }

            var info = ParseInfo(tmp);
            return (tmp, info?.Description);
        }
        catch { return (null, null); }
    }

    /// <summary>
    /// 从**合并探测**（exiftool 一次调用同时返回标签与 `base64:` ICC）还原出临时 ICC 文件。
    /// <para>
    /// 2026-10-09 性能改动：原先探测为了拿 ICC 要**再起一次 exiftool**（`-b -icc_profile`，
    /// 单次实测 0.170 s），而 <see cref="ExifToolService.ReadColorTagsAsync"/> 加上
    /// `-b -icc_profile` 后在同一条 JSON 里就给出 `ICC_Profile:ICC_Profile`。
    /// </para>
    /// <para>
    /// 判据口径与 <see cref="ExtractIccToTempFile"/> 逐条一致（&lt;128 字节 ⇒ 视为无 ICC；
    /// 非合法 ICC ⇒ 视为无 ICC 并删文件）——"这算不算一张真 ICC"只能有一份标准，
    /// 合并路径不许悄悄放宽。
    /// </para>
    /// </summary>
    public static (string? path, string? description) ExtractIccFromBase64(string? base64Value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(base64Value)) return (null, null);
            var payload = base64Value.StartsWith("base64:", StringComparison.OrdinalIgnoreCase)
                ? base64Value.Substring("base64:".Length)
                : base64Value;
            // exiftool 的 base64 可能带换行/空白 ⇒ 先剥掉，别让解析器直接抛
            payload = payload.Replace("\r", "").Replace("\n", "").Replace(" ", "").Replace("\t", "");
            var bytes = Convert.FromBase64String(payload);
            if (bytes.Length < 128) return (null, null);
            var tmp = Path.Combine(PlatformServices.GetTempDir(), $"icc_extract_{Guid.NewGuid():N}.icc");
            File.WriteAllBytes(tmp, bytes);
            if (!IsValidIccProfile(tmp)) { TryDelete(tmp); return (null, null); }
            var info = ParseInfo(tmp);
            return (tmp, info?.Description);
        }
        catch { return (null, null); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>
    /// 公开删除临时 ICC 文件（供探测等外部调用方清理）。
    /// ⚠️ 受保护：用户提供的 PLAN/iccs/*.icc 与纯托管生成缓存都是**共享命名资源**（多处引用、
    /// 可再生成但不应被单个任务销毁），绝不删除；仅清理本服务产生的临时提取件。
    /// </summary>
    public static void TryDeleteIcc(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (IsSharedNamedIcc(path)) return;
        TryDelete(path);
    }

    // ── 读 ICC 判原色（对齐 libultrahdr IccHelper::readIccColorGamut）──

    private static readonly string[] IccGamutCandidates = { "bt709", "smpte432", "bt2020" };

    /// <summary>
    /// 读 ICC 的 rXYZ/gXYZ/bXYZ（即 RGB→XYZ(D50) 矩阵的列向量），与各候选原色的**理论色度矩阵**比对，
    /// 得出图像实际所处的 primaries（CICP token：bt709 / smpte432 / bt2020）。
    /// 容差 2e-3（ICC 用 16.16 定点存储 + 各家实现取整差异）；超差或无 tag 返回 null。
    /// 用途：GainMap 底图色域识别 —— JPEG 无 CICP，底图色域只能由 ICC 表达。
    /// </summary>
    public static string? DetectIccPrimaries(string? iccPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(iccPath) || !File.Exists(iccPath)) return null;
            var icc = File.ReadAllBytes(iccPath);
            if (icc.Length < 132) return null;
            var got = new double[9];
            for (int c = 0; c < 3; c++)
            {
                string sig = c == 0 ? "rXYZ" : c == 1 ? "gXYZ" : "bXYZ";
                var (off, size) = FindIccTag(icc, sig);
                if (size < 20 || (long)off + 20 > icc.Length) return null;
                for (int k = 0; k < 3; k++) got[c * 3 + k] = ReadS15Fixed16(icc, (int)off + 8 + k * 4);
            }
            string? best = null;
            double bestErr = double.MaxValue;
            foreach (var tok in IccGamutCandidates)
            {
                var m = ColorSpaceRegistry.ColorantsD50(tok);
                if (m == null) continue;
                double err = 0;
                for (int c = 0; c < 3; c++)
                    for (int k = 0; k < 3; k++)
                        err = Math.Max(err, Math.Abs(got[c * 3 + k] - m[k * 3 + c]));   // got[通道][XYZ] vs m[XYZ][通道]
                if (err < bestErr) { bestErr = err; best = tok; }
            }
            return bestErr <= 2e-3 ? best : null;
        }
        catch { return null; }
    }

    /// <summary>s15Fixed16（有符号 16.16 定点，大端）→ double</summary>
    private static double ReadS15Fixed16(byte[] d, int p)
        => ((d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]) / 65536.0;

    // ── 供色彩映射引擎使用的 ICC 度量值读取（P2）──

    /// <summary>
    /// 读 rXYZ/gXYZ/bXYZ 组装为 **RGB→XYZ(D50)** 行主序 9 项（列向量 = 各原色）。
    /// 缺 tag / 非 XYZ 类型 / 长度不足 → null。
    /// </summary>
    public static double[]? ReadIccColorants(string? iccPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(iccPath) || !File.Exists(iccPath)) return null;
            var icc = File.ReadAllBytes(iccPath);
            if (icc.Length < 132) return null;
            var m = new double[9];
            for (int c = 0; c < 3; c++)
            {
                string sig = c == 0 ? "rXYZ" : c == 1 ? "gXYZ" : "bXYZ";
                var (off, size) = FindIccTag(icc, sig);
                if (size < 20 || (long)off + 20 > icc.Length) return null;
                if (ReadTagType(icc, off) != "XYZ ") return null;
                // ICC 列向量 (X,Y,Z) → 行主序矩阵的第 c 列：m[row*3+c]
                for (int k = 0; k < 3; k++) m[k * 3 + c] = ReadS15Fixed16(icc, (int)off + 8 + k * 4);
            }
            return m;
        }
        catch { return null; }
    }

    /// <summary>取 tag 数据类型签名（前 4 字节）；越界返回空串。</summary>
    private static string ReadTagType(byte[] d, uint off)
        => off + 4 <= d.Length ? System.Text.Encoding.ASCII.GetString(d, (int)off, 4) : "";

    /// <summary>
    /// 读 rTRC 为传递函数：
    ///  • curveType/para functionType 0–4 → 直接映射到本项目的 7 参数规范形（sRGB 等价 type3/4）；
    ///  • curv 单值 = gamma；curv 多值 = LUT（交 <see cref="FfmpegGui.Services.ColorMapping.TransferCurve.FromTable"/>）；
    ///  • curv 计 0 项 = 线性。
    /// ⚠️  fail-closed：若 r/g/b 三个 TRC 不一致（非中性 profile）或无法解析 → 返回 null，
    /// 由上层走“携带 ICC”，绝不拿单通道曲线凑近似。
    /// </summary>
    public static FfmpegGui.Services.ColorMapping.TransferCurve? ReadIccCurve(string? iccPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(iccPath) || !File.Exists(iccPath)) return null;
            var icc = File.ReadAllBytes(iccPath);
            if (icc.Length < 132) return null;

            byte[]? r = ReadTagBytes(icc, "rTRC");
            byte[]? g = ReadTagBytes(icc, "gTRC");
            byte[]? b = ReadTagBytes(icc, "bTRC");
            if (r == null) return null;
            // 非中性 profile（三通道曲线不同）→ 单曲线模型不适用
            if (!SameBytes(r, g) || !SameBytes(r, b)) return null;

            string sig = System.Text.Encoding.ASCII.GetString(r, 0, 4);
            if (sig == "para")
            {
                int fn = (r[8] << 8) | r[9];
                // curveType/paraType 布局：type(4) + reserved(4) + functionType(2) + **reserved(2)** → 参数从 12 起。
                // 实测踩过：把参数起点当成 10 会整体偏移 2 字节，2.4 被读成 0.000031（由与 lcms2 对比的探针抓出）。
                double P(int i) => ReadS15Fixed16(r, 12 + i * 4);
                switch (fn)
                {
                    case 0: return FfmpegGui.Services.ColorMapping.TransferCurve.FromParametric(P(0), 1, 0, 0, 0, 0, 0, "ICC para0");
                    case 1: { double g2 = P(0), a = P(1), bb = P(2); return FfmpegGui.Services.ColorMapping.TransferCurve.FromParametric(g2, a, bb, 0, -bb / a, 0, 0, "ICC para1"); }
                    // ICC para type2: Y = (aX+b)^g for X ≥ −b/a; cX for X < −b/a。
                    // 规范形 (g,a,b,c,d,e,f)：c=线性 toe 系数, e=幂段偏移 —— c/e 曾错位
                    // （c=0, e=cc）导致 toe 段丢失、幂段被错误加偏移（P1-N8）。
                    case 2: { double g2 = P(0), a = P(1), bb = P(2), cc = P(3); return FfmpegGui.Services.ColorMapping.TransferCurve.FromParametric(g2, a, bb, cc, -bb / a, 0, 0, "ICC para2"); }
                    case 3: return FfmpegGui.Services.ColorMapping.TransferCurve.FromParametric(P(0), P(1), P(2), P(3), P(4), 0, 0, "ICC para3");
                    case 4: return FfmpegGui.Services.ColorMapping.TransferCurve.FromParametric(P(0), P(1), P(2), P(3), P(4), P(5), P(6), "ICC para4");
                    default: return null;
                }
            }
            if (sig == "curv")
            {
                uint count = Be32(r, 8);
                if (count == 0) return FfmpegGui.Services.ColorMapping.TransferCurve.Linear;
                if (count == 1)
                {
                    // 计 1 项时该值是 **u8Fixed8** 表示的 gamma 指数（如 2.2 = 563/256），不是归一化采样
                    double gamma = Be16(r, 12) / 256.0;
                    if (gamma <= 0) return null;
                    return FfmpegGui.Services.ColorMapping.TransferCurve.FromParametric(
                        gamma, 1, 0, 0, 0, 0, 0, $"ICC curv-γ{gamma:0.###}");
                }
                int n = (int)count;
                if (n < 2 || r.Length < 12 + n) return null;
                bool u16 = r.Length >= 12 + n * 2;            // 采样宽度由剩余字节数判定
                var lut = new float[n];
                for (int i = 0; i < n; i++)
                    lut[i] = u16 ? Be16(r, 12 + i * 2) / 65535f : r[12 + i] / 255f;
                return FfmpegGui.Services.ColorMapping.TransferCurve.FromTable(lut, $"ICC curv[{n}]");
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>读出指定 tag 的完整字节（含类型头）；不存在返回 null。</summary>
    private static byte[]? ReadTagBytes(byte[] icc, string tag)
    {
        var (off, size) = FindIccTag(icc, tag);
        if (size < 8 || (long)off + size > icc.Length) return null;
        var b = new byte[size];
        Array.Copy(icc, (int)off, b, 0, (int)size);
        return b;
    }

    private static bool SameBytes(byte[]? a, byte[]? b)
    {
        if (a == null || b == null) return a == b;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static uint Be32(byte[] d, int p)
        => ((uint)d[p] << 24) | ((uint)d[p + 1] << 16) | ((uint)d[p + 2] << 8) | d[p + 3];

    private static int Be16(byte[] d, int p) => (d[p] << 8) | d[p + 1];

    /// <summary>该路径是否为共享命名 ICC（PLAN/iccs 用户文件或本服务生成的命名缓存）。</summary>
    private static bool IsSharedNamedIcc(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var dir = AppSettingsService.Current.IccDirectory;
            if (!string.IsNullOrWhiteSpace(dir))
            {
                var fullDir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (full.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase)) return true;
            }
            var name = Path.GetFileName(full);
            return name.StartsWith("named_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("stdicc_", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// 生成（并缓存）一个标准色域的 SDR ICC 文件（供 cjxl -x icc_pathname 嵌入）。
    /// cjxl/libjxl 无 SDR-BT.2020 等 color_space 枚举，SDR 广色域只能以标准 ICC 描述。
    /// 方法：用 ffmpeg 将载体帧 zscale 到目标 primaries/trc + iccgen 内嵌标准 ICC → 提取为 .icc。
    /// 同一色域只生成一次（缓存）。失败返回 null（调用方回退携带源 ICC）。
    /// </summary>
    /// <param name="log">日志回调（取消/超时分支点名用；null ⇒ 退回 Trace.WriteLine）</param>
    /// <param name="ct">取消令牌（`#26`：上游 7 条路径**全部**持有 `ct` 却一条都没传下来，本次接通）</param>
    public static string? GetOrGenerateStandardIcc(string? carrier, string primaries, string? trc, string? matrix,
        Action<string>? log = null, CancellationToken ct = default)
    {
        // iccgen 不支持 HDR 传递(smpte2084/arib-std-b67): 实测报 "Not yet implemented"。
        // HDR 必须走 CICP 枚举(cjxl color_space=Rec2100PQ/HLG + intensity_target)，不能生成 ICC。
        if (trc is "smpte2084" or "arib-std-b67")
            return null;
        // trc/matrix 缺失时无法构造 zscale 滤镜（引擎计划可能不要求 ICC），回退调用方分支。
        if (string.IsNullOrEmpty(trc) || string.IsNullOrEmpty(matrix))
            return null;
        var tmpDir = PlatformServices.GetTempDir();
        var outIcc = Path.Combine(tmpDir, $"stdicc_{primaries}_{trc}_{matrix}.icc");
        if (File.Exists(outIcc) && IsValidIccProfile(outIcc)) return outIcc;
        try
        {
            var ffmpegDir = Path.GetDirectoryName(AppSettingsService.Current.FfmpegPath) ?? "";
            var ffmpeg = Path.Combine(ffmpegDir, "ffmpeg.exe");
            if (!File.Exists(ffmpeg)) return null;
            // carrier 优先用真实输入图(保证 zscale 有有效帧)，否则退到 lavfi 纯色源。
            var inArg = (!string.IsNullOrEmpty(carrier) && File.Exists(carrier))
                ? $"-i \"{carrier}\""
                : "-f lavfi -i color=c=gray:s=2x2";
            var png = Path.Combine(tmpDir, $"stdicc_{primaries}_{trc}_{matrix}_{Guid.NewGuid():N}.png");
            var vf = "format=rgb48le,"
                + $"zscale=pin=bt709:tin=iec61966-2-1:min=bt709:p={primaries}:t={trc}:m={matrix},iccgen";
            using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-y -hide_banner -loglevel error {inArg} -vf \"{vf}\" -frames:v 1 \"{png}\"",
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                UseShellExecute = false, CreateNoWindow = true
            }))
            {
                if (p == null) return null;
                // ⚠ 先挂读取任务、再等退出（本仓范式，见 `FfmpegCommandBuilder.ProbeWithFfprobe`）：
                //   旧写法把同步读写在 `WaitForExit(15000)` **之前** ⇒ 子进程不关管道就永不返回，
                //   **超时（与超时后的 Kill）是死代码**；且旧 `p.Kill()` 未杀进程树。
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                // `#26`：同步 `WaitForExit(15000)` 不接受 ct ⇒ 用 `ct.Register` 杀树接线（不改签名、不改调用方）；
                // 原有 15s 超时**不变**（外部取消与超时先到者生效）。`#24` 的四步范式原样保留。
                using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } });
                if (!p.WaitForExit(15000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    var tmsg = $"[icc] ffmpeg iccgen 超时（15s）⇒ 已终止：primaries={primaries} trc={trc} matrix={matrix}";
                    if (log != null) log(tmsg + "\n"); else System.Diagnostics.Trace.WriteLine(tmsg);
                    return null;
                }
                if (ct.IsCancellationRequested)
                {
                    var cmsg = $"[icc] ffmpeg iccgen 被取消 ⇒ 已终止：primaries={primaries} trc={trc} matrix={matrix}（ICC 不可用）";
                    if (log != null) log(cmsg + "\n"); else System.Diagnostics.Trace.WriteLine(cmsg);
                    return null;
                }
                outTask.GetAwaiter().GetResult();
                errTask.GetAwaiter().GetResult();
            }
            if (!File.Exists(png)) return null;
            var (icc, _) = ExtractIccToTempFile(png, log, ct);
            TryDelete(png);
            if (icc == null) return null;
            try { if (File.Exists(outIcc)) File.Delete(outIcc); File.Move(icc, outIcc); }
            catch { TryDelete(icc); return null; }
            return IsValidIccProfile(outIcc) ? outIcc : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 为「iccgen 无法生成」的命名色彩空间解析标准 ICC：优先取 PLAN/iccs/<slug>.icc
    /// （经 ICC 魔数 + tag 表校验 desc 存在），其次由纯托管 <see cref="IccProfileBuilder"/>
    /// 按公开标准数值生成（无外部资源/许可依赖），最后回退 GetOrGenerateStandardIcc
    /// （仅适用于 bt709/smpte432/bt2020 等可 iccgen 的 SDR）。
    /// </summary>
    public static string? ResolveNamedIcc(string? specKey, string? carrier, string primaries, string trc, string matrix)
    {
        var slug = NamedIccSlug(specKey);
        if (slug != null)
        {
            var user = FindUserNamedIcc(slug);
            if (user != null) return user;
            var built = BuildNamedIcc(slug, specKey);
            if (built != null) return built;
        }
        return GetOrGenerateStandardIcc(carrier, primaries, trc, matrix);
    }

    /// <summary>
    /// 「像素原样 + 仅靠 ICC 描述」的保真 ICC：适用于色域超出 BT.2020、且 zscale/iccgen/CICP
    /// 均无法命名的空间（ProPhoto/ROMM——归一到 BT.2020 会削色，矩阵转换还会错用传递函数）。
    /// PLAN/iccs/<slug>.icc（用户提供的权威配置文件）优先；缺失时由纯托管生成器按公开标准数值产出。
    /// 返回 ICC 绝对路径；不适用或无法生成 → null（调用方回退原有 BT.2020 归一化）。
    /// </summary>
    public static string? GetFaithfulIcc(string? specKey)
    {
        if (!IccProfileBuilder.IsFaithfulOnly(specKey)) return null;
        var slug = NamedIccSlug(specKey);
        if (slug == null) return null;
        return FindUserNamedIcc(slug) ?? BuildNamedIcc(slug, specKey);
    }

    /// <summary>用户提供的标准 ICC（PLAN/iccs/<slug>.icc）：魔数 + tag 表 desc 校验；无效返回 null。</summary>
    private static string? FindUserNamedIcc(string slug)
    {
        var dir = AppSettingsService.Current.IccDirectory;
        if (string.IsNullOrWhiteSpace(dir)) return null;
        var cand = System.IO.Path.Combine(dir, slug + ".icc");
        if (!File.Exists(cand)) return null;
        try
        {
            var bytes = File.ReadAllBytes(cand);
            return IsValidIccProfile(cand) && FindIccTag(bytes, "desc").size > 0 ? cand : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 纯托管生成命名 ICC（缓存于临时目录，同 slug 只生成一次）。
    /// 缓存文件名带 <see cref="IccProfileBuilder.WriterVersion"/>：写入器格式升级后旧产物自动作废并被清理。
    /// </summary>
    private static string? BuildNamedIcc(string slug, string? specKey)
    {
        var tmpDir = PlatformServices.GetTempDir();
        var outIcc = System.IO.Path.Combine(tmpDir, $"named_{slug}.v{IccProfileBuilder.WriterVersion}.icc");
        if (File.Exists(outIcc) && IsValidIccProfile(outIcc)) return outIcc;
        try
        {
            var space = IccProfileBuilder.SpaceFor(specKey);
            var bytes = space == null ? null : IccProfileBuilder.Build(space);
            if (bytes == null || bytes.Length < 132) return null;
            // 先写唯一名再原子改名，避免并发任务读到半个文件
            var tmp = outIcc + $".{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(tmp, bytes);
            try
            {
                if (File.Exists(outIcc)) File.Delete(outIcc);
                File.Move(tmp, outIcc);
            }
            catch
            {
                TryDelete(tmp);
                return File.Exists(outIcc) && IsValidIccProfile(outIcc) ? outIcc : null;
            }
            CleanupOldNamedIccCaches(tmpDir, slug, outIcc);
            return IsValidIccProfile(outIcc) ? outIcc : null;
        }
        catch { return null; }
    }

    /// <summary>删除同 slug 的旧版本/无版本号缓存（写入器升级后遗留）。</summary>
    private static void CleanupOldNamedIccCaches(string tmpDir, string slug, string keep)
    {
        try
        {
            var keepFull = Path.GetFullPath(keep);
            foreach (var f in Directory.EnumerateFiles(tmpDir, $"named_{slug}*.icc"))
            {
                try { if (!string.Equals(Path.GetFullPath(f), keepFull, StringComparison.OrdinalIgnoreCase)) File.Delete(f); }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>命名空间→PLAN/iccs 文件名（仅收录 iccgen 无法产出者：ProPhoto / BT.2100 PQ）。</summary>
    private static string? NamedIccSlug(string? specKey)
    {
        if (string.IsNullOrWhiteSpace(specKey)) return null;
        var n = specKey.Trim().ToLowerInvariant();
        if (n.Contains("prophoto") || n.Contains("romm")) return "prophoto-rgb";
        if ((n.Contains("2100") || n.Contains("2020")) && (n.Contains("pq") || n.Contains("2084"))) return "bt2100-pq";
        return null;
    }

    /// <summary>
    /// ICC tag 查找：先按「tag 目录升序」做二分（ICC.1 强制要求）；未命中时回退线性扫描。
    /// 回退原因：实测部分工具（skcms / Google 生成的 Display P3、Rec2020 PQ 等）产出的 tag 表**并未排序**，
    /// 仅靠二分会漏掉 wtpt/rXYZ/rTRC 等关键 tag，导致校验误判为无效配置文件。
    /// </summary>
    public static (uint offset, uint size) FindIccTag(byte[] icc, string tag)
    {
        if (icc.Length < 132 || tag.Length != 4) return (0, 0);
        uint tagCount = ReadBigEndianU32(icc, 128);
        if (tagCount == 0 || 132L + tagCount * 12 > icc.Length) return (0, 0);

        // 二分（升序目录，规范情形 O(log n)）
        int lo = 0, hi = (int)tagCount - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int entry = 132 + mid * 12;
            int cmp = string.CompareOrdinal(System.Text.Encoding.ASCII.GetString(icc, entry, 4), tag);
            if (cmp == 0) return (ReadBigEndianU32(icc, entry + 4), ReadBigEndianU32(icc, entry + 8));
            if (cmp < 0) lo = mid + 1; else hi = mid - 1;
        }

        // 兜底：非升序目录 → 线性扫描（条目通常在 10~30 个，开销忽略）
        for (int i = 0; i < (int)tagCount; i++)
        {
            int entry = 132 + i * 12;
            if (string.CompareOrdinal(System.Text.Encoding.ASCII.GetString(icc, entry, 4), tag) == 0)
                return (ReadBigEndianU32(icc, entry + 4), ReadBigEndianU32(icc, entry + 8));
        }
        return (0, 0);
    }

    /// <summary>验证文件是否为合法 ICC v2/v4 配置文件</summary>
        public static bool IsValidIccProfile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return false;
                var bytes = File.ReadAllBytes(filePath);
                if (bytes.Length < 128) return false;

                // ICC 文件魔数: bytes 36-39 = "acsp"
                var magic = System.Text.Encoding.ASCII.GetString(bytes, 36, 4);
                return magic == "acsp";
            }
            catch { return false; }
        }

        /// <summary>解析 ICC 文件的基本头信息和描述标签</summary>
        public static IccProfileInfo? ParseInfo(string filePath)
        {
            try
            {
                var bytes = File.ReadAllBytes(filePath);
                if (bytes.Length < 132) return null;

                var info = new IccProfileInfo
                {
                    FileSize = bytes.Length,
                    DeviceClass = ReadAscii(bytes, 12, 4).Trim('\0').Trim(),
                    ColorSpace = ReadAscii(bytes, 16, 4).Trim('\0').Trim(),
                    Pcs = ReadAscii(bytes, 20, 4).Trim('\0').Trim(),
                };

                // 版本号 (bytes 8-11): BCD 编码，如 0x04 0x20 0x00 0x00 → "4.2.0"
                info.Version = $"{bytes[8]}.{bytes[9]}.{bytes[10]}";

                // 解析标签表: offset 128-131 = tag count (uint32 big-endian)
                var tagCount = ReadBigEndianU32(bytes, 128);
                for (int i = 0; i < tagCount && (132 + i * 12 + 11) < bytes.Length; i++)
                {
                    var tagBase = 132 + i * 12;
                    var sig = ReadAscii(bytes, tagBase, 4);
                    if (sig == "desc")
                    {
                        var tagOff = (int)ReadBigEndianU32(bytes, tagBase + 4);
                        var tagSize = (int)ReadBigEndianU32(bytes, tagBase + 8);
                        if (tagOff > 0 && tagOff + 20 <= bytes.Length && tagSize > 12)
                        {
                            var typeSig = ReadAscii(bytes, tagOff, 4).Trim();
                            if (typeSig == "mluc")
                            {
                                // ICC v4 'mluc'：头 16B = sig + reserved + recordCount + recordSize(通常 12)
                                // 记录 12B = language(2) + country(2) + dataLen(4) + dataOffset(4, 相对 tag 起始)
                                // 文本 = UTF-16（无 BOM、以 NUL 结尾）
                                int recs = (int)ReadBigEndianU32(bytes, tagOff + 8);
                                int recSize = (int)ReadBigEndianU32(bytes, tagOff + 12);
                                if (recSize < 12) recSize = 12;
                                for (int r = 0; r < recs && tagOff + 16 + r * recSize + 12 <= bytes.Length; r++)
                                {
                                    int rec = tagOff + 16 + r * recSize;
                                    int dlen = (int)ReadBigEndianU32(bytes, rec + 4);
                                    int doff = (int)ReadBigEndianU32(bytes, rec + 8);
                                    int start = tagOff + doff;
                                    if (dlen <= 0 || start + dlen > bytes.Length) continue;
                                    var txt = System.Text.Encoding.BigEndianUnicode.GetString(bytes, start, dlen);
                                    info.Description = txt.TrimStart('\uFEFF').Trim('\0').Trim();
                                    break;                      // 取首条记录
                                }
                                if (info.Description != null) break;   // desc 标签解析完毕
                            }
                            else if (typeSig == "desc" || typeSig == "text")
                            {
                                // textDescriptionType: type(4) + reserved(4) + asciiLen(4) + ASCII
                                var asciiLen = (int)ReadBigEndianU32(bytes, tagOff + 8);
                                if (asciiLen > 0 && tagOff + 12 + asciiLen <= bytes.Length)
                                {
                                    info.Description = System.Text.Encoding.ASCII
                                        .GetString(bytes, tagOff + 12, asciiLen - 1)
                                        .Trim('\0').Trim();
                                }
                                break;                                 // 找到 desc 标签即停止
                            }
                        }
                    }
                }

                return info;
            }
            catch { return null; }
        }

        /// <summary>从 ICC 描述字符串推断常见色彩空间名称（用于 UI 自动填充源色彩空间）</summary>
        public static string? GuessColorSpace(string? description)
        {
            if (string.IsNullOrWhiteSpace(description)) return null;
            var d = description.ToLowerInvariant();

            if (d.Contains("srgb") || d.Contains("iec61966")) return "sRGB";
            if (d.Contains("adobergb") || d.Contains("adobe rgb")) return "Adobe RGB";
            if (d.Contains("display p3") || d.Contains("displayp3")) return "Display P3";
            if (d.Contains("dci-p3") || d.Contains("dci p3")) return "DCI-P3";
            if (d.Contains("prophoto") || d.Contains("romm")) return "ProPhoto RGB";
            if (d.Contains("rec.2020") || d.Contains("bt.2020") || d.Contains("rec2020")) return "Rec.2020";
            if (d.Contains("rec.2100") || d.Contains("bt.2100")) return "Rec.2100";
            if (d.Contains("colormatch")) return "ColorMatch RGB";

            return null;
        }

        // ── 辅助方法 ──

        private static string ReadAscii(byte[] bytes, int offset, int length)
        {
            return System.Text.Encoding.ASCII.GetString(bytes, offset, length);
        }

        private static uint ReadBigEndianU32(byte[] bytes, int offset)
        {
            return ((uint)bytes[offset] << 24)
                 | ((uint)bytes[offset + 1] << 16)
                 | ((uint)bytes[offset + 2] << 8)
                 | bytes[offset + 3];
        }
    }

    /// <summary>ICC 配置文件解析结果</summary>
    public class IccProfileInfo
    {
        /// <summary>文件字节数</summary>
        public int FileSize { get; set; }
        /// <summary>设备类别: mntr(显示器)/scnr(扫描仪)/prtr(打印机)/spac(色彩空间)</summary>
        public string DeviceClass { get; set; } = "";
        /// <summary>数据色彩空间: RGB/CMYK/GRAY/Lab/XYZ</summary>
        public string ColorSpace { get; set; } = "";
        /// <summary>Profile Connection Space: XYZ 或 Lab</summary>
        public string Pcs { get; set; } = "";
        /// <summary>ICC 规范版本号</summary>
        public string Version { get; set; } = "";
        /// <summary>ASCII 描述文本</summary>
        public string? Description { get; set; }

        /// <summary>是否为显示器 ICC（用于软校样）</summary>
        public bool IsMonitorProfile => DeviceClass == "mntr";

        public override string ToString()
        {
            var desc = Description != null ? $" \"{Description}\"" : "";
            return $"ICC {Version} | {DeviceClass}/{ColorSpace}/{Pcs}{desc} | {FileSize} bytes";
        }
    }
}
