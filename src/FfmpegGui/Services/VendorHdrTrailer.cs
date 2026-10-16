using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FfmpegGui.Services;

/// <summary>
/// 厂商（OnePlus/OPPO ColorOS）私有 HDR 尾注的探测结果。
/// <para><see cref="Determinate"/>=false 或 <see cref="FailReason"/> 非空 ⇒ 不许当作「没有增益图」。</para>
/// </summary>
public sealed class VendorHdrTrailerResult
{
    public bool Found;
    public bool Determinate = true;
    public string? FailReason;
    public byte[]? MaskJpeg;
    public byte[]? ParamBlock;
    /// <summary>尾注头部声明的 f32（实测 1.2）；0 = 没读到。</summary>
    public float DeclaredRatio;
    public string Device = "";
    public int MaskWidth, MaskHeight, MaskDepth;
    public long MaskDeclaredLength;
    /// <summary>清单四段声明之和 与 从 mask 起点到文件尾 的差（实测 213；>4 KiB 判不可断定）。</summary>
    public long DeclaredSlack;
    public float[] RawFloats = Array.Empty<float>();
}

/// <summary>
/// 厂商私有 HDR 尾注探测器 —— 纯托管、只读、零外部依赖、全程有界。
///
/// <para>
/// 存在的理由（2026-10-14 实测）：OnePlus 15T 的分块 HEIC 在 ISO-BMFF 的 item 表**之外**、
/// <c>mdat</c> 之后追加了一段厂商 HDR 层，清单原文命名 <c>local.hdr.linear.mask</c>
/// （半显示尺寸的**单通道 gray JPEG**）+ <c>local.hdr.meta.data</c>（144 B 参数块）。
/// 它既不是 ISO 21496-1 的 <c>tmap</c> 派生项，也不是 <c>auxC</c> 辅助图 ⇒
/// <see cref="IsoBmffGainMapProbe"/>、ffprobe、exiftool 一律看不见，表现为
/// 「手机显示 GainMap、转换后 HDR 静默消失」。本探测器补的正是这一段**看不见**的输入。
/// </para>
///
/// <para>
/// ⚠ **参数语义是推断的**：厂商未公开 144 B 块的字段定义。本文件只按「两条独立读数互证」
/// 采用其中三个字段（见 <see cref="BuildMetadata"/>），其余字段**原样进日志、不参与计算**。
/// 字节偏移由门禁 <c>verify-gainmap-isobmff.ps1</c> 的 §3d（跑 <c>ServiceProbe vendor-hdr</c>，V2 逐字段钉死）锁定，改动必须同步该闸。
/// </para>
///
/// <para>
/// 定位一律不靠解析整棵 box 树（手搓 ISOBMFF 走查会在私有区失步）：
/// 用 ASCII 清单名 + JPEG 段遍历 + 「参数块自带 144.0 长度」三重锚点互证。
/// </para>
/// </summary>
public static class VendorHdrTrailer
{
    public const int ParamBlockBytes = 144;
    // 载体清单里的两个段名 —— **只有这一种写法在本机被验证过**（一加 15T 原片，2026-10-14）。
    // ⚠ 撤回记录：本轮一度在这里写过"本机还找到第二种写法（`vendor.videoenhance.*`，offset 都是 1）"
    //   并据此把识别放宽成"按长度分配角色"。那句"发现"是**我把一段截断/乱码的工具输出读成了事实**：
    //   事后按字面量精确复查（`grep -rn "imagelicmask"` 全盘 + 仓库内），全机唯一命中就是我自己
    //   刚写下的那几行 ⇒ 没有第二个样本，放宽判据等于用无法验证的"兼容性"换误认风险。已回退。
    //   ⇒ 判据保持名称驱动：清单里没有这两个名字 = 明确判「无此承载形式」，不去猜同构的不同命名。
    const string MaskName = "local.hdr.linear.mask";
    const string MetaName = "local.hdr.meta.data";

    // 144 B 参数块内的字节偏移（**推断**；语义无公开规范，改动须同步门禁）
    const int P_SELF_SIZE = 8;    // f32 == 144.0 ⇒ 块自带长度，兼作定位锚
    const int P_MIN_X256  = 32;   // f32 == -42.0  ⇒ log2(最小提升) = 值/256
    const int P_GAMMA     = 36;   // f32 == 2.45991
    const int P_MAX_X256  = 44;   // f32 == 73.0   ⇒ log2(最大提升) = 值/256
    const int P_W         = 80;   // i32 == 2944（实测等于 mask 宽）
    const int P_H         = 84;   // i32 == 2208
    const int P_DEPTH     = 88;   // i32 == 8

    const int MaxJpegSpanCandidates = 4096;   // 有界：扫描 SOI 的次数上限
    const int ManifestBackScan = 2048;
    const int ManifestFwdScan = 8192;
    const int ParamScanWindow = 1024;
    const int HeaderWindow = 128;

    /// <summary>在字节流里找 ASCII 串（区分大小写）。</summary>
    static int IndexOfAscii(byte[] d, int from, string s)
    {
        var pat = Encoding.ASCII.GetBytes(s);
        int end = d.Length - pat.Length;
        for (int i = from; i <= end; i++)
        {
            int j = 0;
            while (j < pat.Length && d[i + j] == pat[j]) j++;
            if (j == pat.Length) return i;
        }
        return -1;
    }

    // 参数块是**小端**，与宿主字节序无关 ⇒ 逐字节拼，不用 BitConverter.ToSingle(d, o)
    static float F32(byte[] d, int o)
    {
        if (o + 4 > d.Length) return 0f;
        uint bits = (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
        return BitConverter.UInt32BitsToSingle(bits);
    }
    static int I32(byte[] d, int o) =>
        o + 4 > d.Length ? 0 : d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24);

    /// <summary>
    /// 从 SOI 起按 JPEG 段结构走到 EOI，返回整段长度（含 SOI/EOI）；结构非法返回 -1。
    /// 只走段头，不进熵码（SOS 后按「找下一个非填充标记」跳过）。
    /// </summary>
    /// <summary>从 SOI 走到 EOI，返回整段长度。`limit` 是**扫描上界**（不含）：
    /// 没有它时 4096 个候选每个都可能走到文件尾 ⇒ 最坏 4096×文件大小 的逐字节走查。
    /// 调用方给的是「声明长度 + 容差」，于是总功恒有界（A3 审查项）。</summary>
    static long JpegSpan(byte[] d, int soi, int limit)
    {
        if (soi + 4 > d.Length) return -1;
        if (d[soi] != 0xFF || d[soi + 1] != 0xD8) return -1;
        int p = soi + 2;
        while (p + 4 <= limit && p + 4 <= d.Length)
        {
            if (d[p] != 0xFF) { p++; continue; }
            byte m = d[p + 1];
            if (m == 0xD9) return (p + 2) - soi;                    // EOI
            if (m == 0x01 || (m >= 0xD0 && m <= 0xD7)) { p += 2; continue; }
            int len = (d[p + 2] << 8) | d[p + 3];
            if (len < 2) return -1;
            if (m == 0xDA)                                          // SOS：跳过熵码数据
            {
                p += 2 + len;
                while (p + 1 < limit && p + 1 < d.Length)
                {
                    if (d[p] == 0xFF && d[p + 1] != 0x00 && !(d[p + 1] >= 0xD0 && d[p + 1] <= 0xD7))
                        break;
                    p++;
                }
                continue;
            }
            p += 2 + len;
        }
        return -1;
    }

    /// <summary>清单条目：length/name（清单里另有 `offset`/`version` 字段；**offset 实测语义是段尾位置**，
    /// 但本探测器不依赖它 —— 定位一律用「声明长度 + JPEG 段遍历 + 参数块自尺寸锚」）。</summary>
    readonly struct Entry
    {
        public readonly long Length; public readonly string Name;
        public Entry(long l, string n) { Length = l; Name = n; }
    }

    static List<Entry> ParseManifest(byte[] d, int anchor)
    {
        var list = new List<Entry>();
        int s = Math.Max(0, anchor - ManifestBackScan);
        int open = -1;
        for (int i = anchor; i >= s; i--) if (d[i] == (byte)'[') { open = i; break; }
        if (open < 0) return list;
        int e = Math.Min(d.Length - 1, anchor + ManifestFwdScan);
        int close = -1;
        for (int i = anchor; i <= e; i++) if (d[i] == (byte)']') { close = i; break; }
        if (close < 0) return list;

        int p = open;
        while (p <= close)
        {
            int ob = IndexOfAscii(d, p, "{");
            if (ob < 0 || ob > close) break;
            int cb = IndexOfAscii(d, ob, "}");
            if (cb < 0 || cb > close) break;
            string obj = Encoding.ASCII.GetString(d, ob, cb - ob + 1);
            long len = -1; string? name = null;
            int li = obj.IndexOf("\"length\":", StringComparison.Ordinal);
            if (li >= 0)
            {
                int st = li + 9, k = st;
                while (k < obj.Length && (char.IsDigit(obj[k]) || obj[k] == '-')) k++;
                if (long.TryParse(obj.AsSpan(st, k - st), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var v)) len = v;
            }
            int ni = obj.IndexOf("\"name\":\"", StringComparison.Ordinal);
            if (ni >= 0)
            {
                int st = ni + 8;
                int en = obj.IndexOf('"', st);
                if (en > st) name = obj[st..en];
            }
            if (name != null && len > 0) list.Add(new Entry(len, name));
            p = cb + 1;
        }
        return list;
    }

    public static VendorHdrTrailerResult Probe(byte[] data)
    {
        var r = new VendorHdrTrailerResult();
        try
        {
            int anchor = IndexOfAscii(data, 0, MaskName);
            if (anchor < 0) { r.Found = false; r.Determinate = true; return r; }   // 明确「无此承载形式」

            var entries = ParseManifest(data, anchor);
            long maskLen = -1, metaLen = -1;
            foreach (var en in entries)
            {
                if (en.Name == MaskName) maskLen = en.Length;
                else if (en.Name == MetaName) metaLen = en.Length;
            }
            if (maskLen <= 0) { r.FailReason = "[vendor-hdr] manifest has no local.hdr.linear.mask length"; return r; }
            if (metaLen != ParamBlockBytes)
            {
                r.FailReason = $"[vendor-hdr] local.hdr.meta.data length = {metaLen}, expected {ParamBlockBytes}";
                return r;
            }
            r.MaskDeclaredLength = maskLen;
            // ── 长度自洽见下方 P1 检查（声明四段之和 vs 尾注区实长）──

            // ── mask = 与声明长度相符的唯一 JPEG 段 ──
            int found = 0, soi = -1; long span = -1;
            for (int i = 0; i + 3 <= data.Length; i++)
            {
                if (data[i] != 0xFF || data[i + 1] != 0xD8 || data[i + 2] != 0xFF) continue;
                if (++found > MaxJpegSpanCandidates)
                {
                    r.FailReason = $"[vendor-hdr] SOI candidates exceeded bound ({MaxJpegSpanCandidates})";
                    return r;
                }
                // 上界 = 声明长度 + 64 B 容差（再往后不可能匹配，无须走查）
                long sp = JpegSpan(data, i, i + (int)System.Math.Min((long)maskLen + 64, data.Length - i));
                if (sp <= 0) continue;
                if (Math.Abs(sp - maskLen) > 64) continue;
                if (soi >= 0) { r.FailReason = "[vendor-hdr] more than one JPEG matches the declared mask length"; return r; }
                soi = i; span = sp;
            }
            if (soi < 0) { r.FailReason = "[vendor-hdr] no JPEG matches the declared mask length"; return r; }

            // ── 参数块：靠「块自带 144.0 长度」定位，不靠硬编码距离 ──
            int pb = -1;
            int lim = Math.Min(data.Length - (P_SELF_SIZE + 4), soi + (int)span + ParamScanWindow);
            for (int i = soi + (int)span; i <= lim; i++)
            {
                if (Math.Abs(F32(data, i + P_SELF_SIZE) - ParamBlockBytes) < 0.001f
                    && i + ParamBlockBytes <= data.Length) { pb = i; break; }
            }
            if (pb < 0) { r.FailReason = "[vendor-hdr] param block anchor (self-size 144.0) not found after the mask"; return r; }

            r.MaskJpeg = new byte[span];
            Buffer.BlockCopy(data, soi, r.MaskJpeg, 0, (int)span);
            r.ParamBlock = new byte[ParamBlockBytes];
            Buffer.BlockCopy(data, pb, r.ParamBlock, 0, ParamBlockBytes);

            var fl = new float[ParamBlockBytes / 4];
            for (int i = 0; i < fl.Length; i++) fl[i] = F32(r.ParamBlock, i * 4);
            r.RawFloats = fl;

            r.MaskWidth = I32(r.ParamBlock, P_W);
            r.MaskHeight = I32(r.ParamBlock, P_H);
            r.MaskDepth = I32(r.ParamBlock, P_DEPTH);
            if (r.MaskWidth < 16 || r.MaskHeight < 16 || r.MaskWidth > 65535 || r.MaskHeight > 65535
                || (r.MaskDepth != 8 && r.MaskDepth != 10 && r.MaskDepth != 12))
            {
                r.FailReason = $"[vendor-hdr] implausible mask geometry {r.MaskWidth}x{r.MaskHeight}x{r.MaskDepth}";
                return r;
            }

            // ── 尾注头部：f32 比率 + 设备名（都在 SOI 之前的小窗口里） ──
            int hs = Math.Max(0, soi - HeaderWindow);
            for (int i = hs; i + 4 <= soi; i++)
            {
                float v = F32(data, i);
                if (v > 1.0f && v <= 64.0f) { r.DeclaredRatio = v; break; }
            }
            // 设备名 = 窗口里**最长的**可打印串（逐字节拼会把它左右的结构体字节也收进来）
            int runStart = -1, bestStart = -1, bestLen = 0;
            for (int i = hs; i <= soi; i++)
            {
                bool printable = i < soi && data[i] >= 32 && data[i] < 127;
                if (printable) { if (runStart < 0) runStart = i; }
                else if (runStart >= 0)
                {
                    int len = i - runStart;
                    if (len > bestLen) { bestStart = runStart; bestLen = len; }
                    runStart = -1;
                }
            }
            if (bestLen >= 4) r.Device = Encoding.ASCII.GetString(data, bestStart, bestLen);

            // ── P1 长度自洽 ──
            //   实测本件：144 + 1,408,096 + 80 + 4 = 1,408,324，而从 mask 起点到文件尾 = 1,408,537，
            //   差 213（记录头 + 补齐 + 清单脚，厂商未逐字节对齐）⇒ 只做 4 KiB 容差的**弱**自洽：
            //   对不上就判「不可断定」，绝不因为认得两个字段名就放行结构不符的件。
            long declaredSum = metaLen + maskLen + 80 + 4;
            long region = data.Length - soi;
            if (Math.Abs(region - declaredSum) > 4096)
            {
                r.Determinate = false;
                r.FailReason = $"[vendor-hdr] manifest lengths don't reconcile: declared sum={declaredSum} "
                             + $"vs region from mask start={region} (tolerance 4096)";
                return r;
            }
            r.DeclaredSlack = region - declaredSum;

            r.Found = true;
            return r;
        }
        catch (Exception ex)
        {
            r.Found = false; r.Determinate = false;
            r.FailReason = $"[vendor-hdr] probe exception {ex.GetType().Name}";
            return r;
        }
    }

    /// <summary>
    /// 144 B 块 → ISO 21496-1 语义的 <see cref="GainMapDecoder.GainMapMetadata"/>。
    ///
    /// <para>采用的三个字段与其依据（**互证**，其余字段不参与计算）：</para>
    /// <list type="bullet">
    /// <item>max = f32[44]/256 = 73/256 = 0.28516 log2 ⇒ 线性 1.2166；
    /// 尾注头部独立声明的 f32 = 1.2 ⇒ 两条读数差 1.4%，故最大提升 ≈1.2× 可信。</item>
    /// <item>min = f32[32]/256 = −42/256 = −0.16406 log2 ⇒ 0.891×（暗部下压，与「恢复对比」一致）。</item>
    /// <item>gamma = f32[36] = 2.45991（施加为 g^(1/γ)，见 ApplyGainChannel）。</item>
    /// </list>
    /// <para>offsetSdr/offsetHdr 取 0：块内没有可信的对应字段，猜一个非零值只会把误差藏进乘式。</para>
    /// </summary>
    public static GainMapDecoder.GainMapMetadata? BuildMetadata(VendorHdrTrailerResult r,
        FfmpegGui.Models.FfmpegOptions? o = null, Action<string>? log = null)
    {
        if (!r.Found || r.ParamBlock == null) return null;
        float minLog2 = F32(r.ParamBlock, P_MIN_X256) / 256f;
        float maxLog2 = F32(r.ParamBlock, P_MAX_X256) / 256f;
        float gamma = F32(r.ParamBlock, P_GAMMA);
        if (gamma <= 0f) gamma = 1f;

        // 覆盖位（`--vendor-gm-*`）：非法 ⇒ 警告 + 沿用实测值，绝不停整条转换。
        // ⚠ 审查（2026-10-15）：合法性判据必须显式挡 NaN/Infinity —— `Math.Abs(NaN)>10` 与 `NaN<=0`
        //   都是 false ⇒ 旧写法会让 `--vendor-gm-min=NaN` **被静默采纳**（与"非法只警告"相反），
        //   且这个值会随 FfmpegOptions 落盘，System.Text.Json 默认拒写 NaN ⇒ 设置保存当场抛异常。
        static bool LegalOverride(float v, bool positiveRequired) =>
            float.IsFinite(v) && (positiveRequired ? v > 0f : System.Math.Abs(v) <= 10f);
        if (o != null)
        {
            if (o.VendorGmMin.HasValue)
            {
                float v = o.VendorGmMin.Value;
                if (!LegalOverride(v, false))
                    log?.Invoke($"[vendor-hdr] WARN: --vendor-gm-min={v:0.####} is out of range (finite, |log2|<=10) => ignored, using measured {minLog2:0.#####}\n");
                else minLog2 = v;
            }
            if (o.VendorGmMax.HasValue)
            {
                float v = o.VendorGmMax.Value;
                if (!LegalOverride(v, false))
                    log?.Invoke($"[vendor-hdr] WARN: --vendor-gm-max={v:0.####} is out of range (finite, |log2|<=10) => ignored, using measured {maxLog2:0.#####}\n");
                else maxLog2 = v;
            }
            if (o.VendorGmGamma.HasValue)
            {
                float v = o.VendorGmGamma.Value;
                if (!LegalOverride(v, true))
                    log?.Invoke($"[vendor-hdr] WARN: --vendor-gm-gamma={v:0.####} must be finite and > 0 => ignored, using measured {gamma:0.#####}\n");
                else gamma = v;
            }
        }
        if (minLog2 == maxLog2) return null;         // 区间宽度 0 ⇒ 没有可承载的增益

        var m = new GainMapDecoder.GainMapMetadata
        {
            Channels = 1,                            // 实测 mask = 单通道 gray JPEG
            GainMapMin = minLog2,
            GainMapMax = maxLog2,
            Gamma = gamma,
            OffsetSdr = 0f,
            OffsetHdr = 0f,
            HdrCapacityMin = minLog2,
            HdrCapacityMax = maxLog2,
            // 反向（底图已是 HDR、增益用于下映射）不在这里猜，也不静默丢弃：
            // 打上方向由调用方的既有 backward 分支点名（与 ISO-BMFF 同一条路）。
            BaseRenditionIsHdr = minLog2 > maxLog2,
            UseBaseColorSpace = true,
            GainMapWidth = r.MaskWidth,
            GainMapHeight = r.MaskHeight,
            MapSecondaryWidth = r.MaskWidth,
            MapSecondaryHeight = r.MaskHeight,
        };
        m.FillRemainingChannelsFromZero();
        return m;
    }

    /// <summary>把 144 B 块原样打成 ASCII 串（供日志对拍；不进任何计算）。</summary>
    public static string DumpBlock(VendorHdrTrailerResult r)
    {
        if (r.RawFloats.Length == 0 || r.ParamBlock == null) return "(no param block)";
        var s = new StringBuilder();
        for (int i = 0; i < r.RawFloats.Length; i += 6)
        {
            s.Append("f");
            s.Append(i.ToString(CultureInfo.InvariantCulture));
            s.Append('=');
            for (int j = i; j < Math.Min(i + 6, r.RawFloats.Length); j++)
                s.Append(r.RawFloats[j].ToString("0.####", CultureInfo.InvariantCulture)).Append(' ');
            s.Append("| ");
        }
        return s.ToString().TrimEnd();
    }
}
