using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

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
        public int BytesPerSample => Kind == JxrSampleKind.Float32 ? 4 : 2;
    }

    /// <summary>映射前的实测统计 —— 钳位与峰值必须由这里回传，日志才有牙。</summary>
    internal readonly record struct JxrFrameStats(
        float Peak,              // 映射前 RGB 最大样本值（浮点档=线性域；整数档=编码值/65535）
        long OverWhite,          // >1.0 的 RGB 样本个数
        long RgbSampleCount,
        float AlphaMin,
        float AlphaMax);

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
        /// 探针**未给出可用答案**时的盲试顺序（2026-10-29 实机改判的核心）。
        /// <para>为什么需要它：这条通路真正解码用的是 JxrDecApp（jxrlib），跟 ffmpeg 能不能解 JXR 无关；
        /// 而历史上"探针 exit=1 ⇒ 当场判'不是高位深直通档'"会把一次**仪器失败**折算成一条**内容结论**，
        /// 落进对 64bppRGBAHalf 结构上必死的 BMP / <c>-c 0</c> 旧路。本机实测有三种完全不同的病因都能打出
        /// 同一句 <c>exit=1</c>：0 字节的件（<c>Invalid data found</c>）、头 1KB 被别的进程握着的件
        /// （<c>Permission denied</c>）、以及任何不带 libjxr 的 ffprobe 构建。</para>
        /// <para>顺序 = Game DVR / HDR 抓件的实际常见度（半浮点 RGBA → 单精度 RGBA → 16-bit RGBA → 16-bit RGB）。
        /// 每档都由调用方逐档自校验（JxrDecApp 退出码 + TIFF 产出 + <see cref="TryReadTiff"/> 的 fail-closed
        /// 拒读），所以"试错"不会产出坏件；且**只在旧两臂已经失败之后**才启用 ⇒ 8-bit 源（旧臂能成）行为不变，
        /// 也不会被无谓地升采样成浮点。</para>
        /// </summary>
        public static IReadOnlyList<int> BlindArmOrder() => new[]
        {
            Arm64RgbAHalf, Arm128RgbaFloat, Arm64RgbaInt, Arm48RgbInt
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

                int bpsBytes = kind == JxrSampleKind.Float32 ? 4 : 2;
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
        /// <para>浮点档语义 = 线性 scRGB ⇒ 过一次 sRGB 编码（与 jxrlib/WIC 的 8-bit 出口同一条曲线，
        /// 常数唯一来源 <see cref="SimdPixelOps"/>）；整数档语义 = 编码值 ⇒ 只搬深度。</para>
        /// <para><paramref name="useReinhard"/> 且实测峰值 &gt;1.0 时先过
        /// <see cref="SimdPixelOps.ReinhardToSdr"/>（headroom = **整帧**峰值）⇒ 高光被压缩而不是被钳死。
        /// 峰值是**第一遍全帧扫描**得到的，绝不用"到目前为止的峰值"，否则逐行带映射不一致。
        /// 该算子按 RGBA 交错 4 步长实现，故 3 通道浮点档不走它（调用方据 <see cref="CanToneMap"/> 决定并播报）。</para>
        /// </summary>
        public static JxrFrameStats EncodeToPam16(JxrHighBitFrame f, string pamPath, bool useReinhard)
        {
            var scan = Scan(f);
            // ⚠ 整数档是**已编码值**（`-c 23/10` 出来的就是显示参考样本），只搬深度、**不再过 sRGB 曲线**。
            //   2026-10-21 复核抓到：旧写法对所有档一视同仁地调 FloatToSrgb16Scalar ⇒ UInt16 臂被二次编码
            //   （实测 0.5007 → 0.7359，maxdiff 18817）。浮点档才是线性的，才需要编码。
            bool encodedInt = f.Kind == JxrSampleKind.UInt16;
            bool toneMap = !encodedInt && useReinhard && CanToneMap(f) && scan.Peak > 1.0001f;

            int bandRows = Math.Max(1, Math.Min(f.Height, 1_000_000 / Math.Max(1, f.Width * f.Channels)));
            var floats = new float[bandRows * f.Width * f.Channels];
            var sdr = toneMap ? new float[floats.Length] : null;
            var bytes = new byte[floats.Length * 2];

            using var ms = new FileStream(pamPath, FileMode.Create, FileAccess.Write);
            WritePamHeader(ms, f.Width, f.Height, f.Channels);

            for (int y0 = 0; y0 < f.Height; y0 += bandRows)
            {
                int rows = Math.Min(bandRows, f.Height - y0);
                int n = rows * f.Width * f.Channels;
                LoadBand(f, y0, n, floats);

                ReadOnlySpan<float> src = floats;
                if (toneMap)
                {
                    SimdPixelOps.ReinhardToSdr(floats.AsSpan(0, n), sdr!.AsSpan(0, n), scan.Peak);
                    src = sdr.AsSpan(0, n);
                }

                for (int i = 0; i < n; i++)
                {
                    int c = i % f.Channels;
                    ushort v = (c < 3 && !encodedInt)
                        ? SimdPixelOps.FloatToSrgb16Scalar(src[i])
                        : (ushort)Math.Clamp((long)MathF.Round(Math.Clamp(src[i], 0f, 1f) * 65535f), 0L, 65535L);
                    BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2), v);
                }
                ms.Write(bytes, 0, n * 2);
            }
            return scan;
        }

        /// <summary>Reinhard 算子只吃 RGBA 交错 4 步长；3 通道浮点档不具备条件（调用方须播报）。</summary>
        public static bool CanToneMap(JxrHighBitFrame f) => f.Channels == 4;

        /// <summary>整帧扫描：峰值 / 越白样本数 / alpha 区间。整数档的 Peak 是编码值/65535、OverWhite 恒 0。</summary>
        public static JxrFrameStats Scan(JxrHighBitFrame f)
        {
            int n = f.Width * f.Height * f.Channels;
            float peak = 0f, aMin = float.MaxValue, aMax = float.MinValue;
            long overWhite = 0, rgbCount = 0;

            if (f.Kind == JxrSampleKind.UInt16)
            {
                for (int i = 0; i < n; i++)
                {
                    float v = BinaryPrimitives.ReadUInt16LittleEndian(f.Samples.AsSpan(i * 2)) / 65535f;
                    if (i % f.Channels < 3) { rgbCount++; if (v > peak) peak = v; }
                    else { if (v < aMin) aMin = v; if (v > aMax) aMax = v; }
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    float v = ReadSampleFloat(f, i);
                    if (i % f.Channels < 3)
                    {
                        rgbCount++;
                        if (v > peak) peak = v;
                        if (v > 1.0001f) overWhite++;
                    }
                    else { if (v < aMin) aMin = v; if (v > aMax) aMax = v; }
                }
            }
            if (f.Channels == 3) { aMin = 1f; aMax = 1f; }
            return new JxrFrameStats(peak, overWhite, rgbCount,
                aMin == float.MaxValue ? 1f : aMin, aMax == float.MinValue ? 1f : aMax);
        }

        private static void LoadBand(JxrHighBitFrame f, int y0, int n, float[] into)
        {
            // Samples 本身就是 byte[] ⇒ 其长度与任何带内偏移必然落在 int 范围，这里不做 long 往返。
            int off = y0 * f.Width * f.Channels * f.BytesPerSample;
            var src = f.Samples.AsSpan(off);
            // ⚠ 三档**必须三分支**（2026-10-21 复核抓到的真缺陷）：旧写法 `if (Float32) … else 当半浮点`
            //   把 **UInt16 臂（`-c 23`/`-c 10` 出的 64bpp/48bpp 整数 TIFF）也按 IEEE 半浮点解码** ⇒
            //   32816/55/65315 变成 0/3/0（实测 maxdiff=65535）。`Scan`/`ReadSampleFloat` 本来就是三档，
            //   只有这里漏了 ⇒ 读数行（kind=UInt16、peak 正确）反而会掩盖像素是坏的。
            if (f.Kind == JxrSampleKind.Float32)
                for (int i = 0; i < n; i++) into[i] = BinaryPrimitives.ReadSingleLittleEndian(src[(i * 4)..]);
            else if (f.Kind == JxrSampleKind.HalfFloat16)
                for (int i = 0; i < n; i++)
                    into[i] = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(src[(i * 2)..]));
            else
                for (int i = 0; i < n; i++)
                    into[i] = BinaryPrimitives.ReadUInt16LittleEndian(src[(i * 2)..]) / 65535f;
        }

        private static float ReadSampleFloat(JxrHighBitFrame f, int i) => f.Kind switch
        {
            JxrSampleKind.Float32 => BinaryPrimitives.ReadSingleLittleEndian(f.Samples.AsSpan(i * 4)),
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
