using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FfmpegGui.Services.ColorMapping;

namespace FfmpegGui.Services
{
    /// <summary>
    /// JXR 高位 TIFF 中间件的样本语义，由 <c>BitsPerSample</c> 与 <c>SampleFormat</c> **共同**决定
    /// （单看一个都不够定性：<c>16bit+FP</c> 是半浮点、<c>16bit+unsigned</c> 是编码整数）。
    /// </summary>
    internal enum JxrSampleKind
    {
        /// <summary>SampleFormat=1 ⇒ 样本已是**编码值**：只搬深度，不加传递函数。</summary>
        UInt16,
        /// <summary>SampleFormat=2 且 16bit ⇒ signed fixed point，T.832 BD16S = s2.13。</summary>
        Fixed16,
        /// <summary>SampleFormat=2 且 32bit ⇒ signed fixed point，T.832 BD32S = s7.24。</summary>
        Fixed32,
        /// <summary>SampleFormat=3 且 16bit ⇒ IEEE 半浮点，语义 = **线性 scRGB**（1.0 = SDR 白）。</summary>
        HalfFloat16,
        /// <summary>SampleFormat=3 且 32bit ⇒ IEEE 单精度，语义 = **线性 scRGB**。</summary>
        Float32,
    }

    /// <summary>一张解包后的 JXR 高位帧：chunky、无压缩、逐样本小端。</summary>
    internal sealed class JxrHighBitFrame
    {
        public int Width;
        public int Height;
        public int Channels;
        public JxrSampleKind Kind;
        public byte[] Samples = Array.Empty<byte>();
        public bool Premultiplied;
        public int SourceBitsPerSample;
        public int BytesPerSample => Kind is JxrSampleKind.Float32 or JxrSampleKind.Fixed32 ? 4 : 2;
    }

    /// <summary>
    /// 一次 JXR 高位帧的出口映射计划。所有“1.0 等于多少 nits”的换算只在这里发生：
    /// <see cref="ToneMapParams"/> 的线性域约定恒为 1.0 = 203 nits。
    /// </summary>
    internal sealed class JxrColorPlan
    {
        public TransferCurve SourceCurve = TransferCurve.Srgb;
        public double SourceNitsOfLinearOne = TransferCurve.SdrWhiteNits;
        public bool SourceFullRange = true;
        public bool SourcePremultiplied;
        /// <summary>整数 sRGB 直通：样本已是 sRGB 码值时只搬深度，避免二次 OETF。</summary>
        public bool IntCopy;
        public ToneMapParams Tone = new();
        public TransferCurve TargetCurve = TransferCurve.Srgb;
        public double TargetNitsOfLinearOne = TransferCurve.SdrWhiteNits;
        public bool TargetIsHdr;
        public string TargetPrimariesToken = "bt709";
        public string TargetTrcToken = "iec61966-2-1";

        public string Summary =>
            $"src={SourceCurve.Name} srcNits={SourceNitsOfLinearOne:F0} "
            + $"dst={TargetCurve.Name} dstNits={TargetNitsOfLinearOne:F0} "
            + $"map={(IntCopy ? "int-copy" : Tone.Mode switch { ToneMapMode.None => "encode", ToneMapMode.ReinhardSegmented => "reinhard", ToneMapMode.Hable => "hable", ToneMapMode.Mobius => "mobius", ToneMapMode.Bt2446A => "bt2446a", _ => Tone.Mode.ToString() })} "
            + $"premul={SourcePremultiplied} full={SourceFullRange}";
    }

    /// <summary>一次 JXR 高位直通臂的结果：路径/通道/实测峰值/是否真的做了 tone map。</summary>
    internal sealed class JxrHighPrecisionOutcome
    {
        public string? PamPath;
        public int Channels;
        public bool TryBlindArms;
        public double PeakNits;
        public bool ToneMapped;
        public JxrColorPlan? Plan;
    }

    /// <summary>映射前的实测统计 —— 钳位与峰值必须由这里回传，日志才有牙。</summary>
    internal readonly record struct JxrFrameStats(
        float Peak,              // 映射前 RGB 最大样本值（浮点档=线性域；整数档=编码值/65535）
        long OverWhite,          // >1.0 的 RGB 样本个数
        long RgbSampleCount,
        float AlphaMin,
        float AlphaMax,
        double LinearPeak = 0,   // 统一到 1.0=203nit 的线性峰值
        double PeakNits = 0);    // LinearPeak × 203

    /// <summary>
    /// JXR 高位深输入的取帧 → 映射 → 中间件写出（2026-10-21，修 <c>-106 Unsupported format</c>）。
    ///
    /// <para><b>为什么要有这一层</b>（本机实测，Xbox Game DVR 导出的 2560×1600 JXR）：该文件真身是
    /// 4×16bit IEEE 半浮点（线性 scRGB）+ 独立 alpha 平面，而 jxrlib 转换表里 <c>64bppRGBAHalf</c>
    /// 只有 →32bppRGBA / →128bppRGBAFloat 两条出边 ⇒ 旧链两臂同撞 <c>WMP_errUnsupportedFormat(-106)</c>：
    /// BMP 写入器不认 64bpp（<c>JXRTestBmp.c</c> 的 <c>WriteBMPHeader</c> 尾支），
    /// 回退臂 <c>-c 0</c>（24bppRGB）又缺表条目（<c>JXRGluePFC.c:2291</c>）。</para>
    ///
    /// <para><b>为什么不能整条换成 ffmpeg 的 libjxr</b>：那个解码器把 planar alpha 塌成 0（真值恒 1.0），
    /// 且不施加 scRGB→sRGB；而 swscale **完全不接受 <c>rgbaf32le</c> 作源**（实测四目标全拒）
    /// ⇒ 浮点 TIFF 只能由本类这样的托管读取器消费。</para>
    ///
    /// <para><b>只读标注诚实的件</b>：压缩须为 none、排布须 chunky、<c>StripByteCounts</c> 之和须与
    /// <c>w×h×通道×字节</c> 精确相等、4 通道须声明 <c>ExtraSamples</c>；任一不成立 ⇒ 点名拒绝，不猜解释。
    /// （对照：同一文件的 16-bit FP TIFF label 本身是真的，坏的在消费者一侧 —— ffmpeg 的 TIFF 解码器
    /// 不认 16-bit FP，按整数读，实测整幅暗约 1.8 档。）</para>
    /// </summary>
    internal static class JxrHighPrecisionDecode
    {
        // JxrDecApp 的 -c 索引（本机 usage 实测；与 tests/scripts/verify-jxr-anim-input.ps1 的码表同源）。
        // 一律选「与源同格式的直通档」：直通 = from==to ⇒ 不查转换表 ⇒ 不会撞 -106，
        // 也不会让 jxrlib 顺手做传递函数与钳位 —— 映射全部发生在 EncodeToPam16（可统计、可播报）。
        public const int Arm64RgbAHalf = 25;    // 64bppRGBAHalf 直通（半浮点源）
        public const int Arm128RgbaFloat = 28;  // 128bppRGBAFloat 直通（单精度源）
        public const int Arm64RgbaInt = 23;     // 64bppRGBA 直通（16-bit 整数 RGBA 源）
        public const int Arm48RgbInt = 10;      // 48bppRGB 直通（16-bit 整数 3 通道源）
        public const int Arm48RgbHalf = 12;     // 48bppRGBHalf 直通（3 通道半浮点源）
        public const int Arm48RgbFixed = 11;    // 48bppRGBFixedPoint
        public const int Arm96RgbFixed = 14;    // 96bppRGBFixedPoint
        public const int Arm64RgbaFixed = 24;   // 64bppRGBAFixedPoint
        public const int Arm128RgbaFixed = 27;  // 128bppRGBAFixedPoint

        /// <summary>
        /// 按 ffmpeg/libjxr 报出的 pix_fmt 选直通臂；报不出、或不是高位档 ⇒ null（调用方保持既有行为）。
        /// 探测失败不是错误：本函数只作提示，选错时调用方仍有旧臂兜底。
        /// </summary>
        public static int? ChoosePassthroughArm(string? ffprobePixFmt)
        {
            if (string.IsNullOrWhiteSpace(ffprobePixFmt)) return null;
            var s = ffprobePixFmt.Trim().ToLowerInvariant();
            if (s.Contains("af16", StringComparison.Ordinal)) return Arm64RgbAHalf;   // rgbaf16le / gbrapf16le
            if (s.Contains("af32", StringComparison.Ordinal)) return Arm128RgbaFloat; // rgbaf32le / gbrapf32le
            if (s is "rgba64le" or "rgba64be" or "bgra64le" or "bgra64be") return Arm64RgbaInt;
            if (s is "rgb48le" or "rgb48be" or "bgr48le" or "bgr48be") return Arm48RgbInt;
            return null;
        }

        /// <summary>
        /// 用 JXR 容器里的 PIXEL_FORMAT 选直通臂（T.832 Table A.6 → JxrDecApp -c）。
        /// 只有“同格式直通”才不会撞 jxrlib 的缺失转换表；颜色/曲线由托管路径处理。
        /// </summary>
        public static int? ChoosePassthroughArm(JxrPixelFormatInfo pf)
        {
            if (!pf.IsRgbLike) return null;
            return pf.Mnemonic switch
            {
                "64bppRGBAHalf" => Arm64RgbAHalf,
                "128bppRGBAFloat" => Arm128RgbaFloat,
                "64bppPRGBA" => Arm64RgbaInt,
                "64bppRGBA" => Arm64RgbaInt,
                "48bppRGB" => Arm48RgbInt,
                "48bppRGBHalf" => Arm48RgbHalf,
                "48bppRGBFixedPoint" => Arm48RgbFixed,
                "96bppRGBFixedPoint" => Arm96RgbFixed,
                "64bppRGBAFixedPoint" => Arm64RgbaFixed,
                "128bppRGBAFixedPoint" => Arm128RgbaFixed,
                _ => null,
            };
        }

        /// <summary>
        /// 探针**未给出可用答案**时的盲试顺序（2026-10-29 实机改判的核心）。
        /// <para>为什么需要它：这条通路真正解码用的是 JxrDecApp（jxrlib），跟 ffmpeg 能不能解 JXR 无关；
        /// 而历史上"探针 exit=1 ⇒ 当场判'不是高位深直通档'"会把一次**仪器失败**折算成一条**内容结论**，
        /// 落进对 64bppRGBAHalf 结构上必死的 BMP / <c>-c 0</c> 旧路。本机实测有三种完全不同的病因都能打出
        /// 同一句 <c>exit=1</c>：0 字节的件（<c>Invalid data found</c>）、头 1KB 被别的进程握着的件
        /// （<c>Permission denied</c>）、以及任何不带 libjxr 的 ffprobe 构建。</para>
        /// <para>顺序 = Game DVR / HDR 抓件的实际常见度（半浮点 RGBA → 单精度 RGBA → 16-bit RGBA → 16-bit RGB
        /// → 3 通道半浮点 / fixed point）。每档都由调用方逐档自校验（JxrDecApp 退出码 + TIFF 产出 +
        /// <see cref="TryReadTiff"/> 的 fail-closed 拒读），所以"试错"不会产出坏件；且**只在旧两臂已经失败之后**
        /// 才启用 ⇒ 8-bit 源（旧臂能成）行为不变。</para>
        /// </summary>
        public static IReadOnlyList<int> BlindArmOrder() => new[]
        {
            Arm64RgbAHalf, Arm128RgbaFloat, Arm64RgbaInt, Arm48RgbInt,
            Arm48RgbHalf, Arm48RgbFixed, Arm96RgbFixed, Arm64RgbaFixed, Arm128RgbaFixed,
        };

        /// <summary>
        /// 这次探测算不算"给出了答案"。空串与 <c>unknown</c> 都算**没有**：前者是读不到，
        /// 后者是 demuxer 自己承认分不出类（实测 4B~1KB 的残件报 <c>unknown</c>）——
        /// 两者都不许被下游当成"这不是高位深文件"。
        /// </summary>
        public static bool ProbeGaveAnswer(string? pixFmt)
        {
            var s = (pixFmt ?? "").Trim();
            return s.Length > 0 && !s.Equals("unknown", StringComparison.OrdinalIgnoreCase);
        }

        // ────────────────────────────────────────────────────────────────────────
        // 取帧：读 JxrDecApp 写出的高位 TIFF
        // ────────────────────────────────────────────────────────────────────────

        private enum TiffTag
        {
            ImageWidth = 256, ImageLength = 257, BitsPerSample = 258, Compression = 259,
            PhotometricInterpretation = 262, StripOffsets = 273, SamplesPerPixel = 277,
            RowsPerStrip = 278, StripByteCounts = 279, PlanarConfiguration = 284,
            ExtraSamples = 338, SampleFormat = 339,
        }

        private readonly struct IfdEntry
        {
            public readonly ushort Type;
            public readonly uint Count;
            public readonly uint ValueOrOffset;
            public IfdEntry(ushort type, uint count, uint valueOrOffset)
            { Type = type; Count = count; ValueOrOffset = valueOrOffset; }
            public int InlineSize => TypeSize(Type) * (int)Count;
        }

        /// <summary>读一张 JxrDecApp 写出的高位 TIFF；任何前提不成立都点名拒绝（out error）。</summary>
        public static bool TryReadTiff(string path, out JxrHighBitFrame? frame, out string? error)
        {
            frame = null; error = null;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                var hdr = ReadExact(fs, 0, 8);
                if (hdr[0] != 'I' || hdr[1] != 'I')
                {
                    error = "不是小端 TIFF（JxrDecApp 只写 II）";
                    return false;
                }
                var magic = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(2));
                if (magic != 42) { error = $"TIFF magic={magic}（期望 42）"; return false; }

                var ifdOff = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(4));
                var count = BinaryPrimitives.ReadUInt16LittleEndian(ReadExact(fs, ifdOff, 2));
                var entries = new Dictionary<TiffTag, IfdEntry>();
                for (int i = 0; i < count; i++)
                {
                    var e = ReadExact(fs, ifdOff + 2 + (long)i * 12, 12);
                    var tag = (TiffTag)BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(0));
                    entries[tag] = new IfdEntry(
                        BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(2)),
                        BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(4)),
                        BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(8)));
                }

                if (!entries.TryGetValue(TiffTag.ImageWidth, out var ew) ||
                    !entries.TryGetValue(TiffTag.ImageLength, out var eh))
                { error = "缺 ImageWidth/ImageLength"; return false; }
                int w = (int)ReadSingle(fs, ew), h = (int)ReadSingle(fs, eh);
                if (w <= 0 || h <= 0) { error = $"尺寸非法 {w}x{h}"; return false; }

                // Compression 缺省按 TIFF 语义视为 1=none；只有**显式**非 1 才拒。
                var compression = GetInt(fs, entries, TiffTag.Compression);
                if (compression.HasValue && compression != 1)
                { error = $"Compression={compression}（只接受 none=1，压缩件不在此通路）"; return false; }

                if (GetInt(fs, entries, TiffTag.PlanarConfiguration) is int planar && planar != 1)
                { error = $"PlanarConfiguration={planar}（平面式排布未支持）"; return false; }

                var photo = GetInt(fs, entries, TiffTag.PhotometricInterpretation);
                if (photo != 2) { error = $"PhotometricInterpretation={photo}（只接受 2=RGB）"; return false; }

                var ch = GetInt(fs, entries, TiffTag.SamplesPerPixel);
                if (ch is not (3 or 4)) { error = $"SamplesPerPixel={ch}（只接受 3/4）"; return false; }
                int channels = ch.Value;

                var bps = ReadUShortArray(fs, entries, TiffTag.BitsPerSample);
                if (bps == null || bps.Length != channels)
                { error = "BitsPerSample 缺失或条数不等于 SamplesPerPixel"; return false; }
                for (int i = 1; i < bps.Length; i++)
                    if (bps[i] != bps[0])
                    { error = $"BitsPerSample 各通道不等（{string.Join("/", bps)}），混合深度不在本通路"; return false; }

                var sf = ReadUShortArray(fs, entries, TiffTag.SampleFormat);
                if (sf != null && sf.Length != channels) { error = "SampleFormat 条数不等于 SamplesPerPixel"; return false; }
                if (sf != null)
                    for (int i = 1; i < sf.Length; i++)
                        if (sf[i] != sf[0]) { error = "SampleFormat 各通道不一致"; return false; }
                ushort fmt = sf == null || sf.Length == 0 ? (ushort)1 : sf[0];

                JxrSampleKind kind;
                if (fmt == 1 && bps[0] == 16) kind = JxrSampleKind.UInt16;
                else if (fmt == 2 && bps[0] == 16) kind = JxrSampleKind.Fixed16;
                else if (fmt == 2 && bps[0] == 32) kind = JxrSampleKind.Fixed32;
                else if (fmt == 3 && bps[0] == 16) kind = JxrSampleKind.HalfFloat16;
                else if (fmt == 3 && bps[0] == 32) kind = JxrSampleKind.Float32;
                else { error = $"BitsPerSample={bps[0]} + SampleFormat={fmt} 不在通路内"; return false; }

                if (channels == 4)
                {
                    var extra = GetInt(fs, entries, TiffTag.ExtraSamples);
                    if (extra is not (2 or 3))
                    { error = $"4 通道但 ExtraSamples={extra}（第 4 样本语义未声明 ⇒ 拒绝猜 alpha）"; return false; }
                }

                var offsets = ReadUIntArray(fs, entries, TiffTag.StripOffsets);
                var byteCounts = ReadUIntArray(fs, entries, TiffTag.StripByteCounts);
                if (offsets == null || byteCounts == null ||
                    offsets.Length != byteCounts.Length || offsets.Length == 0)
                { error = "StripOffsets/StripByteCounts 缺失或条数不等"; return false; }

                int bpsBytes = kind is JxrSampleKind.Float32 or JxrSampleKind.Fixed32 ? 4 : 2;
                long need = (long)w * h * channels * bpsBytes;
                long have = 0;
                for (int i = 0; i < byteCounts.Length; i++) have += byteCounts[i];
                if (have != need)
                { error = $"样本字节不符：StripByteCounts 合计 {have}，按 {w}x{h}x{channels}x{bpsBytes} 应为 {need}"; return false; }

                var samples = new byte[need];
                int at = 0;
                for (int i = 0; i < offsets.Length; i++)
                {
                    var seg = ReadExact(fs, offsets[i], (int)byteCounts[i]);
                    Buffer.BlockCopy(seg, 0, samples, at, seg.Length);
                    at += seg.Length;
                }

                frame = new JxrHighBitFrame
                {
                    Width = w, Height = h, Channels = channels, Kind = kind, Samples = samples,
                    SourceBitsPerSample = (int)bps[0],
                };
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        // ────────────────────────────────────────────────────────────────────────
        // 映射 + 16-bit PAM 写出
        // ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 高位帧 → 16-bit PAM（<c>P7 … MAXVAL 65535</c>，大端样本；ffmpeg 原生读回
        /// <c>rgb48le</c>/<c>rgba64le</c>，实测）。
        /// <para>本方法只负责“把源像素搬到目标编码”，不做第二次决策：源曲线、源 nits、目标曲线、
        /// 目标 nits、预乘与色调映射全部由 <see cref="JxrColorPlan"/> 带入。默认目标 = sRGB，
        /// 与旧行为等价；JXR 识别出的 PQ/HLG/scRGB 则按真实曲线先 EOTF 再 OETF。</para>
        /// </summary>
        public static JxrFrameStats EncodeToPam16(JxrHighBitFrame f, string pamPath, JxrColorPlan plan)
        {
            if (plan.SourcePremultiplied) f.Premultiplied = true;
            var scan = Scan(f, plan);
            // 峰值只增不减：用户显式给的峰值可能小于实测；内容峰值必须覆盖最亮样本，否则高光仍会被钳。
            if (scan.PeakNits > plan.Tone.PeakNits)
                plan.Tone.PeakNits = scan.PeakNits;
            bool toneMap = plan.Tone.Active && scan.LinearPeak > 1.0001;

            int bandRows = Math.Max(1, Math.Min(f.Height, 1_000_000 / Math.Max(1, f.Width * f.Channels)));
            var floats = new float[bandRows * f.Width * f.Channels];
            var bytes = new byte[floats.Length * 2];

            using var ms = new FileStream(pamPath, FileMode.Create, FileAccess.Write);
            WritePamHeader(ms, f.Width, f.Height, f.Channels);

            for (int y0 = 0; y0 < f.Height; y0 += bandRows)
            {
                int rows = Math.Min(bandRows, f.Height - y0);
                int n = rows * f.Width * f.Channels;
                LoadBand(f, y0, n, floats);

                for (int i = 0; i < n; i += f.Channels)
                {
                    float a = f.Channels == 4 ? floats[i + 3] : 1f;
                    if (plan.IntCopy)
                    {
                        // 整数 sRGB 直通：源码值已经是出口码值，只搬深度（旧 0.5007→0.7359 二次 OETF 的教训）。
                        for (int c = 0; c < Math.Min(3, f.Channels); c++)
                            WriteU16(bytes, i + c, floats[i + c]);
                        if (f.Channels == 4) WriteU16(bytes, i + 3, a);
                        continue;
                    }

                    // 预乘 alpha：先还原成直通 RGB，映射完仍输出直通 alpha（cjxl/cjpegli 的 PAM 输入按直通解释）。
                    float r = floats[i], g = f.Channels >= 2 ? floats[i + 1] : r, b = f.Channels >= 3 ? floats[i + 2] : r;
                    if (plan.SourcePremultiplied && f.Channels == 4)
                    {
                        if (a > 1e-6f) { r /= a; g /= a; b /= a; }
                        else { r = g = b = 0f; }
                    }

                    double lr = DecodeSampleToLinear(r, plan);
                    double lg = DecodeSampleToLinear(g, plan);
                    double lb = DecodeSampleToLinear(b, plan);
                    if (toneMap)
                    {
                        float fr = (float)lr, fg = (float)lg, fb = (float)lb;
                        ToneMap.ApplyRgb(ref fr, ref fg, ref fb, plan.Tone, plan.TargetCurve);
                        lr = fr; lg = fg; lb = fb;
                    }

                    WriteU16(bytes, i, (float)EncodeLinearToTarget(lr, plan));
                    if (f.Channels >= 2) WriteU16(bytes, i + 1, (float)EncodeLinearToTarget(lg, plan));
                    if (f.Channels >= 3) WriteU16(bytes, i + 2, (float)EncodeLinearToTarget(lb, plan));
                    if (f.Channels == 4) WriteU16(bytes, i + 3, a);
                }
                ms.Write(bytes, 0, n * 2);
            }
            return scan;
        }

        /// <summary>旧签名（只用于历史调用/探针）：整数 sRGB 直通、浮点线性→sRGB、可选 4 通道 Reinhard。</summary>
        public static JxrFrameStats EncodeToPam16(JxrHighBitFrame f, string pamPath, bool useReinhard)
        {
            var plan = new JxrColorPlan
            {
                SourceCurve = f.Kind == JxrSampleKind.UInt16 ? TransferCurve.Srgb : TransferCurve.Linear,
                SourceNitsOfLinearOne = TransferCurve.SdrWhiteNits,
                IntCopy = f.Kind == JxrSampleKind.UInt16,
                SourceFullRange = true,
                TargetCurve = TransferCurve.Srgb,
                TargetNitsOfLinearOne = TransferCurve.SdrWhiteNits,
            };
            if (useReinhard && f.Channels == 4) plan.Tone.Mode = ToneMapMode.ReinhardSegmented;
            return EncodeToPam16(f, pamPath, plan);
        }

        /// <summary>3 通道与 4 通道都可做统一线性域 tone map（旧实现只允许 RGBA 是历史限制）。</summary>
        public static bool CanToneMap(JxrHighBitFrame f) => f.Channels is 3 or 4;

        /// <summary>整帧扫描：原始峰值 / 线性峰值 / 越白样本数 / alpha 区间。峰值源只来自像素。</summary>
        public static JxrFrameStats Scan(JxrHighBitFrame f) => Scan(f, new JxrColorPlan
        {
            SourceCurve = f.Kind == JxrSampleKind.UInt16 ? TransferCurve.Srgb : TransferCurve.Linear,
            IntCopy = f.Kind == JxrSampleKind.UInt16,
        });

        public static JxrFrameStats Scan(JxrHighBitFrame f, JxrColorPlan plan)
        {
            int n = f.Width * f.Height * f.Channels;
            float peak = 0f, aMin = float.MaxValue, aMax = float.MinValue;
            double linearPeak = 0;
            long overWhite = 0, rgbCount = 0;

            for (int i = 0; i < n; i++)
            {
                float v = ReadSampleFloat(f, i);
                if (i % f.Channels < 3)
                {
                    rgbCount++;
                    if (v > peak) peak = v;
                    double lin = DecodeSampleToLinear(v, plan);
                    if (lin > linearPeak) linearPeak = lin;
                    if (lin > 1.0001) overWhite++;
                }
                else
                {
                    if (v < aMin) aMin = v;
                    if (v > aMax) aMax = v;
                }
            }
            if (f.Channels == 3) { aMin = 1f; aMax = 1f; }
            return new JxrFrameStats(peak, overWhite, rgbCount,
                aMin == float.MaxValue ? 1f : aMin, aMax == float.MinValue ? 1f : aMax,
                linearPeak, linearPeak * TransferCurve.SdrWhiteNits);
        }

        /// <summary>源样本 → 引擎线性域（1.0 = SDR 白 203nit）。有限范围在这里展开，曲线在这里取反。</summary>
        private static double DecodeSampleToLinear(float encoded, JxrColorPlan plan)
        {
            double e = encoded;
            if (!plan.SourceFullRange)
                e = (e - 16.0 / 256.0) / (219.0 / 256.0);
            double lin = plan.SourceCurve.ToLinear(e);
            return lin * plan.SourceNitsOfLinearOne / TransferCurve.SdrWhiteNits;
        }

        /// <summary>引擎线性域 → 目标编码。目标曲线与目标 nits 都由计划给定。</summary>
        private static double EncodeLinearToTarget(double linear, JxrColorPlan plan)
        {
            double rel = linear * TransferCurve.SdrWhiteNits / plan.TargetNitsOfLinearOne;
            // sRGB 目标复用唯一一份 16-bit 编码核（gate ⑤ 的存在锁）；其它曲线走 TransferCurve。
            if (plan.TargetCurve.Kind == TransferCurve.CurveKind.Parametric
                && Math.Abs(plan.TargetNitsOfLinearOne - TransferCurve.SdrWhiteNits) < 1e-6)
                return SimdPixelOps.FloatToSrgb16Scalar((float)Math.Clamp(rel, 0.0, 1.0)) / 65535.0;
            return plan.TargetCurve.FromLinear(Math.Clamp(rel, 0.0, 1.0));
        }

        private static void WriteU16(byte[] bytes, int index, float v)
        {
            ushort u = (ushort)Math.Clamp((long)MathF.Round(Math.Clamp(v, 0f, 1f) * 65535f), 0L, 65535L);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(index * 2), u);
        }

        private static void LoadBand(JxrHighBitFrame f, int y0, int n, float[] into)
        {
            int off = y0 * f.Width * f.Channels * f.BytesPerSample;
            var src = f.Samples.AsSpan(off);
            switch (f.Kind)
            {
                case JxrSampleKind.Float32:
                    for (int i = 0; i < n; i++) into[i] = BinaryPrimitives.ReadSingleLittleEndian(src[(i * 4)..]);
                    break;
                case JxrSampleKind.Fixed32:
                    for (int i = 0; i < n; i++)
                        into[i] = BinaryPrimitives.ReadInt32LittleEndian(src[(i * 4)..]) / 16777216f; // s7.24
                    break;
                case JxrSampleKind.Fixed16:
                    for (int i = 0; i < n; i++)
                        into[i] = BinaryPrimitives.ReadInt16LittleEndian(src[(i * 2)..]) / 8192f; // s2.13
                    break;
                case JxrSampleKind.HalfFloat16:
                    for (int i = 0; i < n; i++)
                        into[i] = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(src[(i * 2)..]));
                    break;
                default:
                    for (int i = 0; i < n; i++)
                        into[i] = BinaryPrimitives.ReadUInt16LittleEndian(src[(i * 2)..]) / 65535f;
                    break;
            }
        }

        private static float ReadSampleFloat(JxrHighBitFrame f, int i) => f.Kind switch
        {
            JxrSampleKind.Float32 => BinaryPrimitives.ReadSingleLittleEndian(f.Samples.AsSpan(i * 4)),
            JxrSampleKind.Fixed32 => BinaryPrimitives.ReadInt32LittleEndian(f.Samples.AsSpan(i * 4)) / 16777216f,
            JxrSampleKind.Fixed16 => BinaryPrimitives.ReadInt16LittleEndian(f.Samples.AsSpan(i * 2)) / 8192f,
            JxrSampleKind.HalfFloat16 => (float)BitConverter.UInt16BitsToHalf(
                BinaryPrimitives.ReadUInt16LittleEndian(f.Samples.AsSpan(i * 2))),
            _ => BinaryPrimitives.ReadUInt16LittleEndian(f.Samples.AsSpan(i * 2)) / 65535f,
        };

        private static void WritePamHeader(Stream s, int w, int h, int channels)
        {
            var text = "P7\nWIDTH " + w.ToString(CultureInfo.InvariantCulture)
                + "\nHEIGHT " + h.ToString(CultureInfo.InvariantCulture)
                + "\nDEPTH " + channels.ToString(CultureInfo.InvariantCulture)
                + "\nMAXVAL 65535\nTUPLTYPE " + (channels == 4 ? "RGB_ALPHA" : "RGB") + "\nENDHDR\n";
            var b = Encoding.ASCII.GetBytes(text);
            s.Write(b, 0, b.Length);
        }

        // ────────────────────────────────────────────────────────────────────────
        // IFD 取数
        // ────────────────────────────────────────────────────────────────────────

        private static byte[] ReadExact(Stream s, long pos, int len)
        {
            s.Seek(pos, SeekOrigin.Begin);
            var buf = new byte[len];
            int got = 0;
            while (got < len)
            {
                int r = s.Read(buf, got, len - got);
                if (r <= 0) break;
                got += r;
            }
            if (got != len) throw new InvalidDataException($"读到 {got}/{len} 字节 @0x{pos:X}");
            return buf;
        }

        private static uint ReadSingle(Stream s, in IfdEntry e)
        {
            var raw = e.InlineSize <= 4
                ? BitConverter.GetBytes(e.ValueOrOffset)
                : ReadExact(s, e.ValueOrOffset, e.InlineSize);
            return e.Type == 3 ? BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0))
                               : BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0));
        }

        private static int? GetInt(Stream s, Dictionary<TiffTag, IfdEntry> entries, TiffTag tag)
            => entries.TryGetValue(tag, out var e) ? (int)ReadSingle(s, e) : (int?)null;

        private static ushort[]? ReadUShortArray(Stream s, Dictionary<TiffTag, IfdEntry> entries, TiffTag tag)
        {
            if (!entries.TryGetValue(tag, out var e) || e.Count == 0 || TypeSize(e.Type) != 2) return null;
            int n = (int)e.Count;
            var raw = e.InlineSize <= 4 ? BitConverter.GetBytes(e.ValueOrOffset)
                                        : ReadExact(s, e.ValueOrOffset, e.InlineSize);
            var r = new ushort[n];
            for (int i = 0; i < n; i++) r[i] = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(i * 2));
            return r;
        }

        private static uint[]? ReadUIntArray(Stream s, Dictionary<TiffTag, IfdEntry> entries, TiffTag tag)
        {
            if (!entries.TryGetValue(tag, out var e) || e.Count == 0) return null;
            int tsz = TypeSize(e.Type);
            if (tsz != 2 && tsz != 4) return null;
            int n = (int)e.Count;
            var raw = e.InlineSize <= 4 ? BitConverter.GetBytes(e.ValueOrOffset)
                                        : ReadExact(s, e.ValueOrOffset, e.InlineSize);
            var r = new uint[n];
            for (int i = 0; i < n; i++)
                r[i] = tsz == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(i * 2))
                                : BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i * 4));
            return r;
        }

        private static int TypeSize(ushort type) => type switch
        {
            1 or 2 or 6 or 7 => 1,   // BYTE / ASCII / SBYTE / UNDEFINED
            3 or 8 => 2,             // SHORT / SSHORT
            4 or 9 or 11 => 4,       // LONG / SLONG / FLOAT
            5 or 10 or 12 => 8,      // RATIONAL / SRATIONAL / DOUBLE
            _ => 0,
        };
    }
}
