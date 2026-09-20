using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FfmpegGui.Services
{
    /// <summary>
    /// 纯托管 ICC v4 matrix/TRC RGB 配置文件生成器。
    ///
    /// 为什么需要它：ffmpeg <c>iccgen</c> 只能为 zscale 可命名的 primaries 生成 ICC（bt709/smpte432/bt2020…），
    /// 对 ProPhoto/ROMM（无 CICP 命名）与 BT.2100 PQ（iccgen 明确 "Not yet implemented"）无解；
    /// 而外部 .icc 资源要么需人工下载、要么有分发许可问题。本生成器仅使用**公开标准里的数值**
    /// （IEC 61966-2-5 / Kodak ROMM 色度与传递函数、ITU-R BT.2100 PQ EOTF），零第三方依赖、无许可负担。
    ///
    /// 结构（与 Google/skcms 产出的最小 matrix/TRC 配置文件同构，约 600 B）：
    ///   头 128 B：size / version 4.0.0 / mntr / RGB  / XYZ  / 日期 / 'acsp' / flags /
    ///             manufacturer / model / attributes / renderingIntent=relative /
    ///             PCS 白点 D50 / creator / profileID / reserved
    ///   tag 目录：按签名升序（ICC.1 强制；本仓库 <see cref="IccProfileService.FindIccTag"/> 依赖二分）
    ///   bTRC bXYZ bkpt chad cprt desc gTRC gXYZ rTRC rXYZ wtpt
    ///   （三通道 TRC 相同 → 三个 tag 指向同一份数据；'desc' 用 v4 的 mluc，'chad' 用 sf32）
    ///
    /// 色彩学：rXYZ/gXYZ/bXYZ/wtpt 必须表达在 PCS（D50）中，故由 primaries+白点解出 RGB→XYZ 后
    /// 再做 Bradford 色适应；'chad' 记录该适应矩阵（v4 必需）。矩阵运算复用
    /// <see cref="ColorSpaceRegistry"/>（全流程唯一色彩语义来源），不重复实现。
    /// </summary>
    public static class IccProfileBuilder
    {
        /// <summary>PCS 固有白点 D50 (X, Y, Z)，ICC.1:2010+ 强制。</summary>
        private static readonly double[] D50Xyz = { 0.9642, 1.0, 0.8249 };

        /// <summary>
        /// 写入器版本号：产物结构/编码方式变更时 +1（缓存文件名包含此号，
        /// 保证旧格式产物不会被当作新结果复用）。v2 = 'mluc' 改为 recordSize 头 + 无 BOM、NUL 结尾文本。
        /// </summary>
        public const int WriterVersion = 2;

        /// <summary>传递函数（device code → linear）表达：parametric type3 或 curv LUT。</summary>
        public sealed class TransferCurve
        {
            /// <summary>true → 'para' functionType 3: Y=(aX+b)^g (X≥d) ; Y=cX (X&lt;d)。参数存 s15Fixed16。</summary>
            public bool Parametric = true;
            public double G = 1, A = 1, B = 0, C = 0, D = 0;
            /// <summary>'curv' LUT 采样数（Parametric=false 时生效）。PQ 这类不可参数化曲线用 1024。</summary>
            public int TableEntries = 1024;
            /// <summary>curv 采样：code(0..1) → linear(0..1)。</summary>
            public Func<double, double>? CodeToLinear;
        }

        /// <summary>待生成配置文件的 RGB 空间描述。</summary>
        public sealed class RgbSpace
        {
            public string Description = "";
            public double[] Rp = Array.Empty<double>();
            public double[] Gp = Array.Empty<double>();
            public double[] Bp = Array.Empty<double>();
            public double[] Wp = Array.Empty<double>();
            public TransferCurve Curve = new();
        }

        /// <summary>
        /// 命名空间的传递函数表（device code → linear）。色度/白点一律取自
        /// <see cref="ColorSpaceRegistry"/>（全流程唯一色彩语义来源），本表只补 Registry 未存的 TRC 参数。
        /// </summary>
        private static TransferCurve? CurveFor(string specKey) => specKey switch
        {
            // ProPhoto/ROMM (IEC 61966-2-5)：X<1/32 → X/16（线性趾）; X≥1/32 → X^1.8（恰为 para type3）
            "ProPhoto RGB" => new TransferCurve
            { Parametric = true, G = 1.8, A = 1.0, B = 0.0, C = 1.0 / 16.0, D = 1.0 / 32.0 },
            // Adobe RGB (1998)：纯 2.2 幂律（type3 退化：d=0 使总走幂函数分支）
            "Adobe RGB" => new TransferCurve
            { Parametric = true, G = 2.2, A = 1.0, B = 0.0, C = 1.0, D = 0.0 },
            // BT.2100 PQ：SMPTE ST 2084 EOTF 不可参数化 → curv LUT 采样（同 libjxl icc_creator 做法）
            "BT.2020 PQ" or "P3 PQ" => new TransferCurve
            {
                Parametric = false, TableEntries = 1024,
                CodeToLinear = v => SimdPixelOps.PqEotfScalar((float)v),
            },
            _ => null,
        };

        private static string DescriptionFor(string specKey) => specKey switch
        {
            "ProPhoto RGB" => "ProPhoto RGB (ROMM, IEC 61966-2-5)",
            "Adobe RGB" => "Adobe RGB (1998)",
            "BT.2020 PQ" or "P3 PQ" => "ITU-R BT.2100 PQ (smpte2084)",
            _ => specKey,
        };

        /// <summary>
        /// 按 ColorSpaceRegistry 键组装可生成的命名空间（色度取自 Registry，曲线取本表）；
        /// 无曲线定义（→ iccgen 能处理）时返回 null。
        /// </summary>
        public static RgbSpace? SpaceFor(string? specKey)
        {
            var s = ColorSpaceRegistry.Resolve(specKey);
            if (s == null || s.Rp == null || s.Gp == null || s.Bp == null || s.Wp == null) return null;
            var curve = CurveFor(s.Key);
            if (curve == null) return null;
            return new RgbSpace
            {
                Description = DescriptionFor(s.Key),
                Rp = s.Rp, Gp = s.Gp, Bp = s.Bp, Wp = s.Wp,
                Curve = curve,
            };
        }

        /// <summary>命名空间是否需要「像素原样 + 仅 ICC 描述」的保真路径（色域超出 BT.2020，归一会削色）。</summary>
        public static bool IsFaithfulOnly(string? specKey)
        {
            var s = ColorSpaceRegistry.Resolve(specKey);
            return s?.Key == "ProPhoto RGB";
        }

        // ═══════════════════════════════════════════════════
        //  生成
        // ═══════════════════════════════════════════════════

        /// <summary>构建 matrix/TRC ICC v4 配置文件字节；色度/曲线不合法返回 null。</summary>
        public static byte[]? Build(RgbSpace s)
        {
            try
            {
                if (s.Rp.Length != 2 || s.Gp.Length != 2 || s.Bp.Length != 2 || s.Wp.Length != 2) return null;

                // ── 色度 → 线性 RGB→XYZ（白点归一），再 Bradford 适应到 D50 PCS ──
                var rgbToXyz = ColorSpaceRegistry.PrimariesToXyz(s.Rp, s.Gp, s.Bp, s.Wp);
                var chad = ColorSpaceRegistry.BradfordAdapt(ColorSpaceRegistry.XyzFromXy(s.Wp), D50Xyz);
                var adapted = ColorSpaceRegistry.Mat3Mul(chad, rgbToXyz);   // 列向量约定: [rXYZ gXYZ bXYZ]

                var tagDesc = BuildMluc(s.Description);
                var tagCprt = BuildText("Generated by FFmpegPictureUI (pure managed ICC writer; "
                    + "colorimetry from public standards: IEC 61966-2-5 / ITU-R BT.2100).");
                var tagWtpt = BuildXyz(D50Xyz[0], D50Xyz[1], D50Xyz[2]);
                var tagBkpt = BuildXyz(0, 0, 0);
                var tagRXyz = BuildXyz(adapted[0], adapted[3], adapted[6]);
                var tagGXyz = BuildXyz(adapted[1], adapted[4], adapted[7]);
                var tagBXyz = BuildXyz(adapted[2], adapted[5], adapted[8]);
                var tagChad = BuildSf32(chad);
                var tagTrc = BuildTrc(s.Curve);

                // 目录（签名 → 数据）；三通道 TRC 共享同一数据块
                var tags = new List<(string Sig, byte[] Data)>
                {
                    ("desc", tagDesc), ("cprt", tagCprt), ("wtpt", tagWtpt), ("bkpt", tagBkpt),
                    ("rXYZ", tagRXyz), ("gXYZ", tagGXyz), ("bXYZ", tagBXyz), ("chad", tagChad),
                    ("rTRC", tagTrc), ("gTRC", tagTrc), ("bTRC", tagTrc),
                };
                // ICC.1 要求 tag 目录按签名升序（本服务二分查找依赖）
                tags.Sort((a, b) => string.CompareOrdinal(a.Sig, b.Sig));

                int dirSize = 4 + tags.Count * 12;
                int pos = 128 + dirSize;
                var offsets = new int[tags.Count];
                var dataSizes = new int[tags.Count];
                var seen = new Dictionary<byte[], int>(ReferenceEqualityComparer.Instance);
                for (int i = 0; i < tags.Count; i++)
                {
                    // 同一数据（三通道 TRC）只落一次盘，后续 tag 复用 offset
                    if (seen.TryGetValue(tags[i].Data, out int first))
                    {
                        offsets[i] = first;
                    }
                    else
                    {
                        seen[tags[i].Data] = pos;
                        offsets[i] = pos;
                        pos += Align4(tags[i].Data.Length);
                    }
                    dataSizes[i] = tags[i].Data.Length;
                }

                var ms = new MemoryStream(pos);
                var w = new BinaryWriter(ms);
                WriteHeader(w, pos, s);
                // tag 目录
                WriteU32(w, (uint)tags.Count);
                for (int i = 0; i < tags.Count; i++)
                {
                    w.Write(Ascii4(tags[i].Sig));
                    WriteU32(w, (uint)offsets[i]);
                    WriteU32(w, (uint)dataSizes[i]);
                }
                // tag 数据（去重写入）
                var written = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
                for (int i = 0; i < tags.Count; i++)
                {
                    if (!written.Add(tags[i].Data)) continue;
                    w.Write(tags[i].Data);
                    int pad = Align4(tags[i].Data.Length) - tags[i].Data.Length;
                    for (int k = 0; k < pad; k++) w.Write((byte)0);
                }
                w.Flush();
                return ms.ToArray();
            }
            catch { return null; }
        }

        private static void WriteHeader(BinaryWriter w, int size, RgbSpace s)
        {
            WriteU32(w, (uint)size);              // 0   profile size
            WriteU32(w, 0);                       // 4   preferred CMM
            w.Write(new byte[] { 4, 0, 0, 0 });   // 8   version 4.0.0 (主.次.修订.无效)
            w.Write(Ascii4("mntr"));              // 12  device class
            w.Write(Ascii4("RGB "));              // 16  data colour space
            w.Write(Ascii4("XYZ "));              // 20  PCS
            // 24  date/time (u16×6: Y M D h m s) —— 固定值保证产物可复现/可缓存
            w.Write(Be16(2026)); w.Write(Be16(1)); w.Write(Be16(1));
            w.Write(Be16(0)); w.Write(Be16(0)); w.Write(Be16(0));
            w.Write(Ascii4("acsp"));              // 36  magic
            WriteU32(w, 0);                       // 40  profile file flags
            WriteU32(w, 0);                       // 44  embedded profile signature
            w.Write(Ascii4("FFGU"));              // 48  device manufacturer
            WriteU32(w, 0);                       // 52  device model
            WriteU32(w, 0); WriteU32(w, 0);       // 56  device attributes (u64)
            WriteU32(w, 1);                       // 64  rendering intent = relative colorimetric
            // 68  PCS 固有白点 (XYZ × s15Fixed16) = D50
            foreach (var v in D50Xyz) WriteS15(w, v);
            w.Write(Ascii4("FFGU"));              // 80  profile creator
            w.Write(new byte[16]);                // 84  profile ID（未计算 → 置零；校验器仅告警）
            w.Write(new byte[28]);                // 100 reserved
        }

        // ── tag 数据构造 ──

        /// <summary>'XYZ ' = sig + reserved + 3 × s15Fixed16 (20 B)</summary>
        private static byte[] BuildXyz(double x, double y, double z)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(Ascii4("XYZ ")); WriteU32(w, 0);
            WriteS15(w, x); WriteS15(w, y); WriteS15(w, z);
            w.Flush(); return ms.ToArray();
        }

        /// <summary>'sf32' (chad) = sig + reserved + 9 × s15Fixed16 (44 B)，行主序。</summary>
        private static byte[] BuildSf32(double[] m)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(Ascii4("sf32")); WriteU32(w, 0);
            for (int i = 0; i < 9; i++) WriteS15(w, m[i]);
            w.Flush(); return ms.ToArray();
        }

        /// <summary>'para' functionType 3：Y=(aX+b)^g (X≥d); Y=cX (X&lt;d)。文件参数序 = g,a,b,c,d。</summary>
        private static byte[] BuildTrc(TransferCurve c)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            if (c.Parametric)
            {
                w.Write(Ascii4("para")); WriteU32(w, 0);
                w.Write(Be16(3)); w.Write(Be16(0));            // functionType 3 + reserved
                WriteS15(w, c.G); WriteS15(w, c.A); WriteS15(w, c.B);
                WriteS15(w, c.C); WriteS15(w, c.D);
            }
            else
            {
                int n = Math.Clamp(c.TableEntries, 3, 4096);
                w.Write(Ascii4("curv")); WriteU32(w, 0);
                WriteU32(w, (uint)n);
                var f = c.CodeToLinear ?? (v => Math.Pow(v, 2.2));
                for (int i = 0; i < n; i++)
                {
                    double v = Math.Clamp(f(i / (double)(n - 1)), 0.0, 1.0);
                    w.Write(Be16((int)Math.Round(v * 65535.0)));
                }
            }
            w.Flush(); return ms.ToArray();
        }

        /// <summary>
        /// 'mluc'（v4 描述类型，对齐 Google/skcms 与 exiftool/lcms 解析）：
        ///   头 16B = sig + reserved + recordCount + recordSize(=12)
        ///   记录 12B = language('en') + country('US') + 字节长 + 偏移（相对 tag 起始 = 28）
        ///   文本 = UTF-16BE，**无 BOM、以 NUL 结尾**（带 BOM 会被 exiftool 报 “Corrupted ProfileDescription”）
        /// </summary>
        private static byte[] BuildMluc(string text)
        {
            var utf16 = Encoding.BigEndianUnicode.GetBytes(text + "\0");   // NUL 结尾
            const int headerSize = 4 + 4 + 4 + 4;                          // sig+res+count+recordSize = 16
            const int recordSize = 2 + 2 + 4 + 4;                           // = 12

            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(Ascii4("mluc")); WriteU32(w, 0);
            WriteU32(w, 1);                                                 // 记录数
            WriteU32(w, recordSize);                                        // 每条记录字节数（必需）
            w.Write(new[] { (byte)'e', (byte)'n' });
            w.Write(new[] { (byte)'U', (byte)'S' });
            WriteU32(w, (uint)utf16.Length);
            WriteU32(w, (uint)(headerSize + recordSize));                    // = 28，相对 tag 起始
            w.Write(utf16);
            w.Flush(); return ms.ToArray();
        }

        /// <summary>'text'：sig + reserved + ASCII（无长度字段）。</summary>
        private static byte[] BuildText(string ascii)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(Ascii4("text")); WriteU32(w, 0);
            w.Write(Encoding.ASCII.GetBytes(ascii));
            w.Flush(); return ms.ToArray();
        }

        // ── 小工具（ICC 全部大端） ──

        private static int Align4(int n) => (n + 3) & ~3;
        private static byte[] Ascii4(string s)
            => new[] { (byte)s[0], (byte)s[1], (byte)s[2], (byte)s[3] };
        private static byte[] Be16(int v) => new[] { (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF) };
        private static void WriteU32(BinaryWriter w, uint v)
        {
            w.Write((byte)(v >> 24)); w.Write((byte)(v >> 16)); w.Write((byte)(v >> 8)); w.Write((byte)v);
        }
        /// <summary>s15Fixed16：值 × 65536 → int32 大端（超范围钳制）。</summary>
        private static void WriteS15(BinaryWriter w, double v)
        {
            long i = (long)Math.Round(Math.Clamp(v, -32768.0, 32767.99999) * 65536.0);
            uint u = unchecked((uint)(int)i);
            WriteU32(w, u);
        }
    }
}
