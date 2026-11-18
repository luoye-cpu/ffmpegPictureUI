using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FfmpegGui.Services.ColorMapping;

namespace FfmpegGui.Services;

/// <summary>JXR 容器里 PIXEL_FORMAT 的数值语义（T.832 Table A.6）。</summary>
internal sealed class JxrPixelFormatInfo
{
    public string Guid = "";
    public string Mnemonic = "Unknown";
    public int Channels;
    public bool HasAlpha;
    public bool Premultiplied;
    public int BitsPerSample;
    public string Numeric = "UINT";     // UINT / SINT / Float / RGBE
    public string Family = "RGB";       // RGB / YONLY / CMYK / CMYKDIRECT / NCOMPONENT / YUV420 / YUV422 / YUV444
    public string Bpc = "";
    public bool IsFloatOrFixed => Numeric is "Float" or "SINT" || Bpc is "BD16F" or "BD32F" or "BD16S" or "BD32S";
    public bool IsRgbLike => Family is "RGB" or "YONLY";
}

/// <summary>PTM_COLOR_INFO（T.832 Table A.7）。</summary>
internal sealed class JxrPtmInfo
{
    public byte Primaries;
    public byte Transfer;
    public byte Matrix;
    public bool FullRange;
    public int ReservedK;
    public bool HasMatrix => Matrix != 0;
}

/// <summary>JXR 文件头/IFD 的可复核快照。所有字段都来自容器，不从像素猜。</summary>
internal sealed class JxrContentInfo
{
    public bool Ok;
    public string Error = "";
    public string Path = "";
    public int FileVersionId;
    public int FirstIfdOffset;
    public int PrimaryIfdOffset;
    public int IfdCount;
    public int Width;
    public int Height;
    public int ImageType;
    public int? SpatialXformPrimary;
    public bool AlphaOffsetPresent;
    public JxrPixelFormatInfo PixelFormat = new();
    public int? ColorSpaceTag;          // null = 缺省；0xFFFF = 不首选 sRGB
    public JxrPtmInfo? Ptm;
    public byte[]? IccBytes;
    public SourceIccStatus IccStatus = SourceIccStatus.Absent;
    public string IccDescriptorName = "";
    public ColorSpaceDescriptor? Descriptor;
    public string ColorSource = "unknown";  // icc / ptm / color-space-srgb / scrgb-default / srgb-default / unknown
    public double NitsOfLinearOne = TransferCurve.SdrWhiteNits;
    public bool IsHdrNominal;
    public bool FloatOrFixed;
    public List<string> Warnings = new();

    public bool HasAlpha => PixelFormat.HasAlpha || AlphaOffsetPresent;

    public string PixelFormatText => PixelFormat.Mnemonic
        + (PixelFormat.Channels > 0 ? $" (NC={PixelFormat.Channels}, BPC={PixelFormat.Bpc}, Num={PixelFormat.Numeric})" : "");

    public string Warn(string code)
    {
        if (!Warnings.Contains(code)) Warnings.Add(code);
        return code;
    }

    public string ToJson()
    {
        var sb = new StringBuilder(512);
        sb.Append('{');
        bool first = true;
        void Add(string k, string? v, bool raw = false)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(JsonEscape(k)).Append("\":");
            if (raw) sb.Append(v ?? "null");
            else sb.Append('"').Append(JsonEscape(v ?? "")).Append('"');
        }
        Add("path", Path);
        Add("ok", Ok ? "true" : "false", raw: true);
        Add("error", Error);
        Add("container", "jxr");
        Add("file_version_id", FileVersionId.ToString(), raw: true);
        Add("first_ifd_offset", FirstIfdOffset.ToString(), raw: true);
        Add("primary_ifd_offset", PrimaryIfdOffset.ToString(), raw: true);
        Add("ifd_count", IfdCount.ToString(), raw: true);
        Add("width", Width.ToString(), raw: true);
        Add("height", Height.ToString(), raw: true);
        Add("image_type", ImageType.ToString(), raw: true);
        Add("spatial_xform_primary", SpatialXformPrimary?.ToString(), raw: true);
        Add("alpha_offset_present", AlphaOffsetPresent ? "true" : "false", raw: true);
        Add("pixel_format", PixelFormat.Mnemonic);
        Add("pixel_format_guid", PixelFormat.Guid);
        Add("channels", PixelFormat.Channels.ToString(), raw: true);
        Add("has_alpha", HasAlpha ? "true" : "false", raw: true);
        Add("premultiplied", PixelFormat.Premultiplied ? "true" : "false", raw: true);
        Add("bpc", PixelFormat.Bpc);
        Add("bits_per_sample", PixelFormat.BitsPerSample.ToString(), raw: true);
        Add("numeric", PixelFormat.Numeric);
        Add("family", PixelFormat.Family);
        Add("color_space", ColorSpaceTag?.ToString() ?? "absent", raw: true);
        Add("ptm_present", Ptm != null ? "true" : "false", raw: true);
        Add("ptm_primaries", Ptm?.Primaries.ToString(), raw: true);
        Add("ptm_transfer", Ptm?.Transfer.ToString(), raw: true);
        Add("ptm_matrix", Ptm?.Matrix.ToString(), raw: true);
        Add("ptm_full_range", Ptm?.FullRange.ToString(), raw: true);
        Add("icc_status", IccStatus.ToString());
        Add("icc_bytes", (IccBytes?.Length ?? 0).ToString(), raw: true);
        Add("icc_descriptor", IccDescriptorName);
        Add("color_source", ColorSource);
        Add("primaries", Descriptor?.PrimariesToken);
        Add("transfer", Descriptor?.TransferToken);
        Add("nits_of_linear_one", NitsOfLinearOne.ToString("F1", System.Globalization.CultureInfo.InvariantCulture), raw: true);
        Add("hdr_nominal", IsHdrNominal ? "true" : "false", raw: true);
        Add("descriptor", Descriptor?.Name);
        sb.Append(",\"warnings\":[");
        for (int i = 0; i < Warnings.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(JsonEscape(Warnings[i])).Append('"');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static string JsonEscape(string s)
    {
        var b = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': b.Append("\\\\"); break;
                case '"': b.Append("\\\""); break;
                case '\n': b.Append("\\n"); break;
                case '\r': b.Append("\\r"); break;
                case '\t': b.Append("\\t"); break;
                default:
                    if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("X4"));
                    else b.Append(c);
                    break;
            }
        }
        return b.ToString();
    }
}

/// <summary>
/// 纯托管 JXR 容器识别器：读文件头与 IFD 链，解释 PIXEL_FORMAT / COLOR_SPACE / PTM_COLOR_INFO / ICC。
/// 不依赖 exiftool，不读像素；任何“不知道”都保留为警告，不猜。
/// </summary>
internal static class JxrContentProbe
{
    private const int TagColorSpace = 0xA001;
    private const int TagPixelFormat = 0xBC01;
    private const int TagSpatialXform = 0xBC02;
    private const int TagImageType = 0xBC04;
    private const int TagPtm = 0xBC05;
    private const int TagIcc = 0x8773;
    private const int TagWidth = 0xBC80;
    private const int TagHeight = 0xBC81;
    private const int TagAlphaOffset = 0xBCC2;
    private const int TagAlphaCount = 0xBCC3;
    private const int TagImageOffset = 0xBCC0;
    private const int TagImageCount = 0xBCC1;

    private sealed class Entry
    {
        public ushort Tag;
        public ushort Type;
        public uint Count;
        public uint ValueOrOffset;
        public long EntryOffset;
    }

    private static readonly Dictionary<string, JxrPixelFormatInfo> PixelFormats = BuildPixelFormats();

    public static bool IsJxrPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = System.IO.Path.GetExtension(path);
        return ext.Equals(".jxr", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".wdp", StringComparison.OrdinalIgnoreCase);
    }

    public static JxrContentInfo Probe(string path)
    {
        var info = new JxrContentInfo { Path = path };
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 8)
            {
                info.Error = $"文件过短（{fs.Length} 字节）";
                return info;
            }
            var head = ReadAt(fs, 0, 8);
            if (head[0] != 0x49 || head[1] != 0x49 || head[2] != 0xBC)
            {
                info.Error = "不是小端 JXR（期望 II BC）";
                return info;
            }
            info.FileVersionId = head[3];
            info.FirstIfdOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
            if (info.FirstIfdOffset < 8 || info.FirstIfdOffset >= fs.Length)
            {
                info.Error = $"FIRST_IFD_OFFSET={info.FirstIfdOffset} 越界（文件 {fs.Length} 字节）";
                return info;
            }

            // 沿 IFD 链扫描：优先取“非 preview”的第一张；preview 只记录，不作为主图解释。
            var candidates = new List<(int offset, int imageType, bool preview, Dictionary<int, Entry> entries)>();
            int off = info.FirstIfdOffset;
            int guard = 0;
            while (off > 0 && off + 2 <= fs.Length && guard++ < 64)
            {
                if (!TryReadIfd(fs, off, out var entries, out var next, out var err))
                {
                    info.Warn("JXR-IFD-01");
                    if (info.IfdCount == 0) info.Error = err;
                    break;
                }
                int imageType = entries.TryGetValue(TagImageType, out var eit) ? (int)ReadScalar(fs, eit) : 0;
                bool preview = (imageType & 0x01) != 0;
                candidates.Add((off, imageType, preview, entries));
                info.IfdCount++;
                off = next;
            }
            if (candidates.Count == 0)
            {
                if (info.Error.Length == 0) info.Error = "没有可读 IFD";
                return info;
            }

            var pick = candidates.FirstOrDefault(c => !c.preview && c.entries.ContainsKey(TagPixelFormat));
            if (pick.entries == null)
                pick = candidates.FirstOrDefault(c => c.entries.ContainsKey(TagPixelFormat));
            if (pick.entries == null)
                pick = candidates[0];
            info.PrimaryIfdOffset = pick.offset;
            info.ImageType = pick.imageType;
            if (pick.preview) info.Warn("JXR-PREVIEW-01");

            var entries2 = pick.entries;
            info.Width = entries2.TryGetValue(TagWidth, out var ew) ? (int)ReadScalar(fs, ew) : 0;
            info.Height = entries2.TryGetValue(TagHeight, out var eh) ? (int)ReadScalar(fs, eh) : 0;
            if (info.Width <= 0 || info.Height <= 0) info.Warn("JXR-SIZE-01");

            if (entries2.TryGetValue(TagImageType, out var eit2)) info.ImageType = (int)ReadScalar(fs, eit2);
            if (entries2.TryGetValue(TagSpatialXform, out var esx)) info.SpatialXformPrimary = (int)ReadScalar(fs, esx);
            info.AlphaOffsetPresent = entries2.ContainsKey(TagAlphaOffset)
                || (entries2.TryGetValue(TagAlphaCount, out var eac) && ReadScalar(fs, eac) > 0);

            if (entries2.TryGetValue(TagColorSpace, out var ecs))
            {
                var v = (int)ReadScalar(fs, ecs);
                // COLOR_SPACE 规定 only 1 / 0xFFFF；其它值按 0xFFFF 处理（T.832 A.7.17）。
                info.ColorSpaceTag = v == 1 ? 1 : 0xFFFF;
                if (v != 1 && v != 0xFFFF) info.Warn("JXR-COLORSPACE-01");
            }

            if (entries2.TryGetValue(TagPtm, out var ep))
            {
                var bytes = ReadBytes(fs, ep);
                if (bytes.Length >= 4)
                {
                    info.Ptm = new JxrPtmInfo
                    {
                        Primaries = bytes[0],
                        Transfer = bytes[1],
                        Matrix = bytes[2],
                        FullRange = (bytes[3] & 0x01) != 0,
                        ReservedK = bytes[3] >> 1,
                    };
                    if ((bytes[3] >> 1) != 0) info.Warn("JXR-PTM-02");
                }
                else info.Warn("JXR-PTM-01");
            }

            if (entries2.TryGetValue(TagPixelFormat, out var epf))
            {
                var bytes = ReadBytes(fs, epf);
                if (bytes.Length >= 16)
                {
                    var guid = Convert.ToHexString(bytes.AsSpan(0, 16));
                    if (PixelFormats.TryGetValue(guid, out var pf))
                        info.PixelFormat = pf;
                    else
                    {
                        info.PixelFormat = new JxrPixelFormatInfo { Guid = guid, Mnemonic = "Unknown(" + guid[^4..] + ")" };
                        info.Warn("JXR-PIXFMT-01");
                    }
                }
                else info.Warn("JXR-PIXFMT-02");
            }
            else info.Warn("JXR-PIXFMT-03");

            info.FloatOrFixed = info.PixelFormat.IsFloatOrFixed;
            if (entries2.TryGetValue(TagIcc, out var eicc))
            {
                var bytes = ReadBytes(fs, eicc);
                if (bytes.Length > 0)
                {
                    info.IccBytes = bytes;
                    info.IccStatus = SourceIccStatus.Present;
                    info.IccDescriptorName = TryIccDescriptor(bytes, out var desc, out var w)
                        ? (desc ?? "")
                        : "";
                    if (info.IccDescriptorName.Length == 0)
                    {
                        info.IccStatus = SourceIccStatus.Unreadable;
                        info.Warn(w ?? "JXR-ICC-01");
                    }
                }
            }

            // PTM 与 float/fixed 的规范矛盾：PTM 只允许 BD8/10/16/16S；float/fixed 上出现 PTM 属非 conforming，
            // 不拒绝，ICC 优先；无 ICC 时按 scRGB 解释并说明“PTM 被忽略”。
            if (info.Ptm != null && info.FloatOrFixed)
            {
                info.Warn("JXR-PTM-03");
                if (info.IccBytes == null) info.Warn("JXR-PTM-04");
            }
            if (info.Ptm != null && info.PixelFormat.Family is "CMYK" or "CMYKDIRECT" or "NCOMPONENT" or "RGBE")
                info.Warn("JXR-PTM-05");

            BuildDescriptor(fs, info);
            info.Ok = info.Error.Length == 0;
            return info;
        }
        catch (Exception ex)
        {
            info.Error = ex.GetType().Name + ": " + ex.Message;
            return info;
        }
    }

    public static string ProbeJson(string path) => Probe(path).ToJson();

    private static void BuildDescriptor(FileStream fs, JxrContentInfo info)
    {
        // 1) ICC 优先（T.832：ICC 存在时优先于 COLOR_SPACE / 默认语义）
        if (info.IccBytes is { Length: > 0 })
        {
            var desc = TryLoadIccDescriptor(info.IccBytes, out var log, out var path);
            info.Descriptor = desc;
            if (desc != null)
            {
                info.ColorSource = "icc";
                info.NitsOfLinearOne = desc.NitsOfLinearOne;
                info.IsHdrNominal = desc.Curve.Kind is TransferCurve.CurveKind.Pq or TransferCurve.CurveKind.Hlg;
                info.IccDescriptorName = desc.Name;
            }
            else
            {
                info.IccDescriptorName = log ?? "ICC 不可解析";
                info.IccStatus = SourceIccStatus.Unreadable;
                info.Warn("JXR-ICC-02");
                // 携带而不映射：保留 ICC 字节，描述符按惯例兜底，但绝不声称已解析。
            }
            try { if (path != null && File.Exists(path)) File.Delete(path); } catch { }
            if (info.Descriptor != null) return;
        }

        // 2) PTM 优先于 COLOR_SPACE；float/fixed 上 PTM 不适用（非 conforming），忽略并警告。
        if (info.Ptm is { } ptm && !info.FloatOrFixed)
        {
            var d = DescriptorFromPtm(ptm, info);
            if (d != null)
            {
                info.Descriptor = d;
                info.ColorSource = "ptm";
                info.NitsOfLinearOne = d.NitsOfLinearOne;
                info.IsHdrNominal = d.Curve.Kind is TransferCurve.CurveKind.Pq or TransferCurve.CurveKind.Hlg;
                return;
            }
        }

        // 3) COLOR_SPACE=1 + UINT ⇒ sRGB；0xFFFF / 缺省无 ICC/PTM ⇒ 按数值惯例。
        bool uintLike = info.PixelFormat.Numeric == "UINT";
        bool sRGBTag = info.ColorSpaceTag == 1 && uintLike;
        if (sRGBTag)
        {
            var d = ColorSpaceIdentify.FromCicpTokens("bt709", "iec61966-2-1", "JXR COLOR_SPACE=1 sRGB", 203);
            if (d != null)
            {
                d.Confidence = DescriptorConfidence.CicpTag;
                info.Descriptor = d;
                info.ColorSource = "color-space-srgb";
                info.NitsOfLinearOne = 203;
                return;
            }
        }
        if (info.ColorSpaceTag == 1 && info.FloatOrFixed)
        {
            info.Warn("JXR-COLORSPACE-02");   // 规范禁止：float/fixed 不得 COLOR_SPACE=1
        }

        // 4) 数值惯例：UINT RGB/Gray = sRGB；Float/Fixed = scRGB（BT.709 原色 + 线性，1.0=203）。
        if (uintLike)
        {
            var d = ColorSpaceIdentify.FromCicpTokens("bt709", "iec61966-2-1",
                "JXR unsigned RGB (sRGB 惯例)", 203);
            if (d != null)
            {
                d.Confidence = DescriptorConfidence.CodecDefault;
                info.Descriptor = d;
                info.ColorSource = "srgb-default";
                info.NitsOfLinearOne = 203;
                return;
            }
        }
        if (info.FloatOrFixed)
        {
            var d = ColorSpaceIdentify.FromCicpTokens("bt709", "linear", "scRGB (JXR float/fixed)", 203);
            if (d != null)
            {
                d.Confidence = DescriptorConfidence.CodecDefault;
                info.Descriptor = d;
                info.ColorSource = "scrgb-default";
                info.NitsOfLinearOne = 203;
                // scRGB 的 1.0 = SDR 白，但取样可 >1；按 T.832 A.7.21 它是 HDR 承载格式。
                // 标称 HDR 不等于“峰值已知” —— 峰值仍由整帧扫描回填。
                info.IsHdrNominal = true;
                return;
            }
        }

        // 5) 未知组合：保留“不知道”，不要伪造一个可映射的描述符。
        info.ColorSource = "unknown";
        info.Warn("JXR-COLOR-01");
    }

    private static ColorSpaceDescriptor? DescriptorFromPtm(JxrPtmInfo p, JxrContentInfo info)
    {
        // 表 A.8/A.9 的值与 CICP/H.273 同号；项目识别器已覆盖 1/5/6/9/11/12 等等价 token。
        var prim = p.Primaries switch
        {
            1 => "bt709",
            5 => "bt470bg",
            6 => "smpte170m",
            9 => "bt2020",
            11 => "smpte431",   // DCI-P3
            12 => "smpte432",   // Display P3
            _ => null,
        };
        var trc = p.Transfer switch
        {
            1 or 6 or 14 or 15 => "bt709",
            2 => "unspecified",
            5 => "bt470bg",
            8 => "linear",
            13 => "iec61966-2-1",
            16 => "smpte2084",
            18 => "arib-std-b67",
            _ => null,
        };
        if (prim == null || trc == null)
        {
            info.Warn("JXR-PTM-06");
            return null;
        }
        double nits = p.Transfer == 16 ? 10000 : p.Transfer == 18 ? 1000 : 203;
        var d = ColorSpaceIdentify.FromCicpTokens(prim, trc, $"JXR PTM ({prim}/{trc})", nits);
        if (d != null) d.Confidence = DescriptorConfidence.CicpTag;
        return d;
    }

    private static bool TryIccDescriptor(byte[] bytes, out string? desc, out string? warning)
    {
        desc = null; warning = null;
        try
        {
            var path = WriteTempIcc(bytes);
            var d = ColorSpaceIdentify.FromIccFile(path);
            if (d != null)
            {
                desc = d.Name;
                return true;
            }
            warning = "ICC 解析返回 null（LUT-only 或参数非法）";
            return false;
        }
        catch (Exception ex)
        {
            warning = "ICC 解析异常: " + ex.Message;
            return false;
        }
    }

    private static ColorSpaceDescriptor? TryLoadIccDescriptor(byte[] bytes, out string? log, out string? tempPath)
    {
        log = null; tempPath = null;
        try
        {
            tempPath = WriteTempIcc(bytes);
            var d = ColorSpaceIdentify.FromIccFile(tempPath);
            if (d != null)
            {
                d.SourceIccPath = tempPath;
                d.SourceIccStatus = SourceIccStatus.Present;
                return d;
            }
            log = "ICC 存在但不可解析（LUT-only 或无有效参数）";
            return null;
        }
        catch (Exception ex)
        {
            log = "ICC 解析异常: " + ex.Message;
            return null;
        }
    }

    private static string WriteTempIcc(byte[] bytes)
    {
        var dir = PlatformServices.GetTempDir();
        Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, $"jxr_icc_{Guid.NewGuid():N}.icc");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static bool TryReadIfd(FileStream fs, int offset, out Dictionary<int, Entry> entries,
        out int next, out string error)
    {
        entries = new Dictionary<int, Entry>();
        next = 0;
        error = "";
        try
        {
            var cntBuf = ReadAt(fs, offset, 2);
            int count = BinaryPrimitives.ReadUInt16LittleEndian(cntBuf);
            if (count <= 0 || count > 4096)
            {
                error = $"IFD @0x{offset:X} 条目数 {count} 非法";
                return false;
            }
            long need = 2L + 12L * count + 4L;
            if (offset + need > fs.Length)
            {
                error = $"IFD @0x{offset:X} 越界（{need} 字节）";
                return false;
            }
            for (int i = 0; i < count; i++)
            {
                var raw = ReadAt(fs, offset + 2 + i * 12, 12);
                var e = new Entry
                {
                    Tag = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0)),
                    Type = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(2)),
                    Count = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4)),
                    ValueOrOffset = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8)),
                    EntryOffset = offset + 2 + i * 12,
                };
                entries[e.Tag] = e;
            }
            var tail = ReadAt(fs, offset + 2 + count * 12, 4);
            next = (int)BinaryPrimitives.ReadUInt32LittleEndian(tail);
            if (next < 0 || next >= fs.Length) next = 0;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 => 4,
        5 or 10 or 12 => 8,
        _ => 0,
    };

    private static byte[] ReadBytes(FileStream fs, Entry e)
    {
        long size = (long)TypeSize(e.Type) * e.Count;
        if (size <= 0 || size > 64L * 1024 * 1024) throw new InvalidDataException($"标签 0x{e.Tag:X4} 大小非法 {size}");
        if (size <= 4)
        {
            var raw = BitConverter.GetBytes(e.ValueOrOffset);
            return raw.AsSpan(0, (int)size).ToArray();
        }
        if (e.ValueOrOffset < 0 || e.ValueOrOffset + size > fs.Length || e.ValueOrOffset > int.MaxValue)
            throw new InvalidDataException($"标签 0x{e.Tag:X4} 偏移越界");
        return ReadAt(fs, (int)e.ValueOrOffset, (int)size);
    }

    private static long ReadScalar(FileStream fs, Entry e)
    {
        var b = ReadBytes(fs, e);
        return e.Type switch
        {
            1 => b.Length > 0 ? b[0] : 0,
            3 => b.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(b) : 0,
            4 => b.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(b) : 0,
            _ => 0,
        };
    }

    private static byte[] ReadAt(FileStream fs, int pos, int len)
    {
        var buf = new byte[len];
        fs.Seek(pos, SeekOrigin.Begin);
        int got = 0;
        while (got < len)
        {
            int r = fs.Read(buf, got, len - got);
            if (r <= 0) throw new EndOfStreamException($"读到 {got}/{len} 字节 @0x{pos:X}");
            got += r;
        }
        return buf;
    }

    private static Dictionary<string, JxrPixelFormatInfo> BuildPixelFormats()
    {
        const string P = "24C3DD6F034EFE4BB1853D77768DC9";
        var d = new Dictionary<string, JxrPixelFormatInfo>(StringComparer.OrdinalIgnoreCase);
        void Add(string suffix, string name, int nc, bool alpha, bool premul, string bpc, string num, string family, int bits)
            => d[P + suffix] = new JxrPixelFormatInfo
            {
                Guid = P + suffix, Mnemonic = name, Channels = nc, HasAlpha = alpha,
                Premultiplied = premul, Bpc = bpc, Numeric = num, Family = family, BitsPerSample = bits,
            };
        Add("0D", "24bppRGB", 3, false, false, "BD8", "UINT", "RGB", 8);
        Add("0C", "24bppBGR", 3, false, false, "BD8", "UINT", "RGB", 8);
        Add("0E", "32bppBGR", 3, false, false, "BD8", "UINT", "RGB", 8);
        Add("15", "48bppRGB", 3, false, false, "BD16", "UINT", "RGB", 16);
        Add("12", "48bppRGBFixedPoint", 3, false, false, "BD16S", "SINT", "RGB", 16);
        Add("3B", "48bppRGBHalf", 3, false, false, "BD16F", "Float", "RGB", 16);
        Add("18", "96bppRGBFixedPoint", 3, false, false, "BD32S", "SINT", "RGB", 32);
        Add("40", "64bppRGBFixedPoint", 3, false, false, "BD16S", "SINT", "RGB", 16);
        Add("42", "64bppRGBHalf", 3, false, false, "BD16F", "Float", "RGB", 16);
        Add("41", "128bppRGBFixedPoint", 3, false, false, "BD32S", "SINT", "RGB", 32);
        Add("1B", "128bppRGBFloat", 3, false, false, "BD32F", "Float", "RGB", 32);
        Add("0F", "32bppBGRA", 4, true, false, "BD8", "UINT", "RGB", 8);
        Add("16", "64bppRGBA", 4, true, false, "BD16", "UINT", "RGB", 16);
        Add("1D", "64bppRGBAFixedPoint", 4, true, false, "BD16S", "SINT", "RGB", 16);
        Add("3A", "64bppRGBAHalf", 4, true, false, "BD16F", "Float", "RGB", 16);
        Add("1E", "128bppRGBAFixedPoint", 4, true, false, "BD32S", "SINT", "RGB", 32);
        Add("19", "128bppRGBAFloat", 4, true, false, "BD32F", "Float", "RGB", 32);
        Add("10", "32bppPBGRA", 4, true, true, "BD8", "UINT", "RGB", 8);
        Add("17", "64bppPRGBA", 4, true, true, "BD16", "UINT", "RGB", 16);
        Add("1A", "128bppPRGBAFloat", 4, true, true, "BD32F", "Float", "RGB", 32);
        Add("1C", "32bppCMYK", 4, false, false, "BD8", "UINT", "CMYK", 8);
        Add("2C", "40bppCMYKAlpha", 5, true, false, "BD8", "UINT", "CMYK", 8);
        Add("1F", "64bppCMYK", 4, false, false, "BD16", "UINT", "CMYK", 16);
        Add("2D", "80bppCMYKAlpha", 5, true, false, "BD16", "UINT", "CMYK", 16);
        Add("20", "24bpp3Channels", 3, false, false, "BD8", "UINT", "NCOMPONENT", 8);
        Add("21", "32bpp4Channels", 4, false, false, "BD8", "UINT", "NCOMPONENT", 8);
        Add("26", "48bpp3Channels", 3, false, false, "BD16", "UINT", "NCOMPONENT", 16);
        Add("27", "64bpp4Channels", 4, false, false, "BD16", "UINT", "NCOMPONENT", 16);
        Add("08", "8bppGray", 1, false, false, "BD8", "UINT", "YONLY", 8);
        Add("0B", "16bppGray", 1, false, false, "BD16", "UINT", "YONLY", 16);
        Add("13", "16bppGrayFixedPoint", 1, false, false, "BD16S", "SINT", "YONLY", 16);
        Add("3E", "16bppGrayHalf", 1, false, false, "BD16F", "Float", "YONLY", 16);
        Add("3F", "32bppGrayFixedPoint", 1, false, false, "BD32S", "SINT", "YONLY", 32);
        Add("11", "32bppGrayFloat", 1, false, false, "BD32F", "Float", "YONLY", 32);
        Add("05", "BlackWhite", 1, false, false, "BD1", "UINT", "YONLY", 1);
        Add("09", "16bppBGR555", 3, false, false, "BD5", "UINT", "RGB", 5);
        Add("0A", "16bppBGR565", 3, false, false, "BD565", "UINT", "RGB", 5);
        Add("14", "32bppBGR101010", 3, false, false, "BD10", "UINT", "RGB", 10);
        Add("3D", "32bppRGBE", 3, false, false, "BD8", "RGBE", "RGB", 8);
        Add("54", "32bppCMYKDIRECT", 4, false, false, "BD8", "UINT", "CMYKDIRECT", 8);
        Add("55", "64bppCMYKDIRECT", 4, false, false, "BD16", "UINT", "CMYKDIRECT", 16);
        Add("56", "40bppCMYKDIRECTAlpha", 5, true, false, "BD8", "UINT", "CMYKDIRECT", 8);
        Add("43", "80bppCMYKDIRECTAlpha", 5, true, false, "BD16", "UINT", "CMYKDIRECT", 16);
        Add("44", "12bppYCC420", 3, false, false, "BD8", "UINT", "YUV420", 8);
        Add("45", "16bppYCC422", 3, false, false, "BD8", "UINT", "YUV422", 8);
        Add("46", "20bppYCC422", 3, false, false, "BD10", "UINT", "YUV422", 10);
        Add("47", "32bppYCC422", 3, false, false, "BD16", "UINT", "YUV422", 16);
        Add("48", "24bppYCC444", 3, false, false, "BD8", "UINT", "YUV444", 8);
        Add("49", "30bppYCC444", 3, false, false, "BD10", "UINT", "YUV444", 10);
        Add("4A", "48bppYCC444", 3, false, false, "BD16", "UINT", "YUV444", 16);
        Add("4B", "48bppYCC444FixedPoint", 3, false, false, "BD16S", "SINT", "YUV444", 16);
        Add("4C", "20bppYCC420Alpha", 4, true, false, "BD8", "UINT", "YUV420", 8);
        Add("4D", "24bppYCC422Alpha", 4, true, false, "BD8", "UINT", "YUV422", 8);
        Add("4E", "30bppYCC422Alpha", 4, true, false, "BD10", "UINT", "YUV422", 10);
        Add("4F", "48bppYCC422Alpha", 4, true, false, "BD16", "UINT", "YUV422", 16);
        Add("50", "32bppYCC444Alpha", 4, true, false, "BD8", "UINT", "YUV444", 8);
        Add("51", "40bppYCC444Alpha", 4, true, false, "BD10", "UINT", "YUV444", 10);
        Add("52", "64bppYCC444Alpha", 4, true, false, "BD16", "UINT", "YUV444", 16);
        Add("53", "64bppYCC444AlphaFixedPoint", 4, true, false, "BD16S", "SINT", "YUV444", 16);
        // NCOMPONENT 5–8 通道：只识别，不进入 RGB 引擎（按前四通道 CMYK 解释会静默改色）。
        Add("22", "40bpp5Channels", 5, false, false, "BD8", "UINT", "NCOMPONENT", 8);
        Add("23", "48bpp6Channels", 6, false, false, "BD8", "UINT", "NCOMPONENT", 8);
        Add("24", "56bpp7Channels", 7, false, false, "BD8", "UINT", "NCOMPONENT", 8);
        Add("25", "64bpp8Channels", 8, false, false, "BD8", "UINT", "NCOMPONENT", 8);
        Add("2E", "32bpp3ChannelsAlpha", 4, true, false, "BD8", "UINT", "NCOMPONENT", 8);
        Add("2F", "40bpp4ChannelsAlpha", 5, true, false, "BD8", "UINT", "NCOMPONENT", 8);
        Add("30", "48bpp5ChannelsAlpha", 6, true, false, "BD8", "UINT", "NCOMPONENT", 8);
        Add("31", "56bpp6ChannelsAlpha", 7, true, false, "BD8", "UINT", "NCOMPONENT", 8);
        Add("32", "64bpp7ChannelsAlpha", 8, true, false, "BD8", "UINT", "NCOMPONENT", 8);
        Add("33", "72bpp8ChannelsAlpha", 9, true, false, "BD8", "UINT", "NCOMPONENT", 8);
        Add("28", "80bpp5Channels", 5, false, false, "BD16", "UINT", "NCOMPONENT", 16);
        Add("29", "96bpp6Channels", 6, false, false, "BD16", "UINT", "NCOMPONENT", 16);
        Add("2A", "112bpp7Channels", 7, false, false, "BD16", "UINT", "NCOMPONENT", 16);
        Add("2B", "128bpp8Channels", 8, false, false, "BD16", "UINT", "NCOMPONENT", 16);
        Add("34", "64bpp3ChannelsAlpha", 4, true, false, "BD16", "UINT", "NCOMPONENT", 16);
        Add("35", "80bpp4ChannelsAlpha", 5, true, false, "BD16", "UINT", "NCOMPONENT", 16);
        Add("36", "96bpp5ChannelsAlpha", 6, true, false, "BD16", "UINT", "NCOMPONENT", 16);
        Add("37", "112bpp6ChannelsAlpha", 7, true, false, "BD16", "UINT", "NCOMPONENT", 16);
        Add("38", "128bpp7ChannelsAlpha", 8, true, false, "BD16", "UINT", "NCOMPONENT", 16);
        Add("39", "144bpp8ChannelsAlpha", 9, true, false, "BD16", "UINT", "NCOMPONENT", 16);
        return d;
    }
}
