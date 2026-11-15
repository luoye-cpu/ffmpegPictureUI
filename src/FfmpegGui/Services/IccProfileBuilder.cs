using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FfmpegGui.Services.ColorMapping;

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
    ///   bTRC bXYZ chad cprt desc gTRC gXYZ rTRC rXYZ wtpt（标准空间另加 chrm 色度）
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
        /// v3 = 自生成 ICC 全量升 v4.4（cprt 由 v2 textType 改 v4 mluc，与版本位原子同批落地，
        /// 杜绝"4.4 头 + textType cprt"的混合非法 profile）；另增 BuildV2（真 v2.0：desc=descriptionType、
        /// TRC=curv 采样）。
        /// </summary>
        public const int WriterVersion = 3;

        /// <summary>
        /// 自生成 ICC 的规范版本位。<c>V44</c> = 全量默认（v4.4.0，头版本字节 04 40 00 00）；
        /// <c>V2</c> = 仅 JPEG 出口可选的最大兼容档（v2.0.0，头版本字节 02 00 00 00，
        /// desc 用 v2 descriptionType、TRC 一律 curv 采样——v2 规范不认 para）。
        /// </summary>
        public enum IccProfileVersion { V44, V2 }

        /// <summary>UI/CLI 取值归一：v2/v2.0/2 → V2；其余一切（含 null/拼错）→ V44 默认档。</summary>
        public static IccProfileVersion ParseVersionToken(string? token) =>
            token?.Trim().TrimStart('v') switch
            {
                "2" or "2.0" => IccProfileVersion.V2,
                _ => IccProfileVersion.V44,
            };

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
            /// <summary>
            /// 是否写 ICC `chrm` 色度标签。标准空间 ICC（由 CICP/描述符生成）必须写：
            /// 色度是读者（exiftool/lcms/校色工具）区分 sRGB/P3/Rec.2020 的独立依据，只有 XYZ
            /// 矩阵列时部分读者读不出原色；命名 profile（ProPhoto 等）保持既有结构，避免缓存/尺寸漂移。
            /// </summary>
            public bool IncludeChromaticity;
            /// <summary>
            /// ICC.1:2022 的 <c>cicp</c> tag（H.273 编码点）对应值；四个字段必须同时可命名才写。
            /// 这是 ICC 内部的额外 CICP 标注，与容器 cICP 独立，供只读 profile 的读者取得同一语义。
            /// </summary>
            public byte? CicpPrimaries;
            public byte? CicpTransfer;
            public byte? CicpMatrix;
            public byte? CicpFullRange;
            public bool HasCicp => CicpPrimaries.HasValue && CicpTransfer.HasValue
                && CicpMatrix.HasValue && CicpFullRange.HasValue;
            /// <summary>
            /// 源 ICC 重写 v4.4 时使用：直接给出已适应到 D50 的 RGB→XYZ 矩阵（行主序 9 项，
            /// 列 = r/g/b 的 XYZ）。非空时不再由 Rp/Gp/Bp 反推矩阵，保证逐点保留源 profile 的色度。
            /// </summary>
            public double[]? D50Colorants;
            /// <summary>直接矩阵重写时的媒体白点 XYZ（缺省 = D50）。</summary>
            public double[]? D50WhitePoint;
            /// <summary>直接矩阵重写时的 Bradford `chad`（行主序 9 项；缺省按白点算）。</summary>
            public double[]? Chad;
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
            // desc 只写标准色域名（合规线口径：省 mluc 长文案的 +48 B；括号注记不写进 tag）
            "ProPhoto RGB" => "ProPhoto RGB",
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

        /// <summary>
        /// 由色彩引擎的 CICP 描述符组装标准 ICC（sRGB/Rec.709/Display P3/Rec.2020 等）。
        /// 色度直接取描述符的物理 xy，曲线转成 ICC 的 para/curv 表达；不依赖 ffmpeg iccgen。
        /// 这样生成的标准 ICC 与源空间同色度，且能被 exiftool/lcms 接受。
        /// </summary>
        public static RgbSpace? SpaceFromDescriptor(ColorSpaceDescriptor? d)
        {
            if (d?.Rp is not { Length: 2 } || d.Gp is not { Length: 2 }
                || d.Bp is not { Length: 2 } || d.Wp is not { Length: 2 })
                return null;
            var curve = CurveFrom(d.Curve);
            if (curve == null) return null;
            var cicp = AvifCicp.FromSourceTokens(d.PrimariesToken, d.TransferToken);
            var space = new RgbSpace
            {
                Description = d.Name,
                Rp = d.Rp, Gp = d.Gp, Bp = d.Bp, Wp = d.Wp,
                Curve = curve,
                IncludeChromaticity = true,
            };
            if (cicp != null)
            {
                // ICC 是 RGB 数据空间，cicpType 的 MatrixCoefficients 必须为 0；
                // 图像是 full-range RGB，VideoFullRangeFlag 写 1。
                space.CicpPrimaries = cicp.Value.P;
                space.CicpTransfer = cicp.Value.T;
                space.CicpMatrix = 0;
                space.CicpFullRange = 1;
            }
            return space;
        }

        public static RgbSpace? SpaceFromCicp(string? primaries, string? trc)
            => SpaceFromDescriptor(ColorSpaceDescriptor.FromCicp(primaries, trc));

        /// <summary>
        /// 把源 ICC 的实测色度/白点/TRC 组装成 v4.4 重写配置。矩阵已经是
        /// D50-PCS 的 RGB→XYZ（源 profile 的 rXYZ/gXYZ/bXYZ，行主序），不再由原色 xy
        /// 反推，避免二次 Bradford 适应改变数值。仅适用于三通道共享同一 TRC 的
        /// matrix/TRC RGB profile；LUT/设备链接类 profile 由调用方 fail-closed。
        /// </summary>
        public static RgbSpace? SpaceFromIccMatrix(string? description, double[] d50Colorants,
            FfmpegGui.Services.ColorMapping.TransferCurve sourceCurve,
            double[]? whitePointXyz = null, double[]? chad = null)
        {
            if (d50Colorants is not { Length: 9 }) return null;
            var curve = CurveFrom(sourceCurve);
            if (curve == null) return null;
            return new RgbSpace
            {
                Description = string.IsNullOrWhiteSpace(description) ? "ICC profile" : description,
                Curve = curve,
                D50Colorants = d50Colorants,
                D50WhitePoint = whitePointXyz,
                Chad = chad,
                IncludeChromaticity = false,
            };
        }

        private static TransferCurve? CurveFrom(FfmpegGui.Services.ColorMapping.TransferCurve c)
        {
            if (c.Kind == FfmpegGui.Services.ColorMapping.TransferCurve.CurveKind.Parametric)
                return new TransferCurve
                {
                    Parametric = true,
                    G = c.G, A = c.A, B = c.B, C = c.C, D = c.D,
                };
            // PQ/HLG/Table/Linear：ICC curv 采样（PQ 1024 点与 libjxl icc_creator 同做法）。
            if (c.Kind is FfmpegGui.Services.ColorMapping.TransferCurve.CurveKind.Pq
                or FfmpegGui.Services.ColorMapping.TransferCurve.CurveKind.Hlg
                or FfmpegGui.Services.ColorMapping.TransferCurve.CurveKind.Table
                or FfmpegGui.Services.ColorMapping.TransferCurve.CurveKind.Linear)
                return new TransferCurve
                {
                    Parametric = false,
                    TableEntries = 1024,
                    CodeToLinear = v => c.ToLinear(v),
                };
            return null;
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

        /// <summary>构建 matrix/TRC ICC 配置文件字节（默认 v4.4.0）；色度/曲线不合法返回 null。</summary>
        public static byte[]? Build(RgbSpace s) => BuildCore(s, IccProfileVersion.V44);

        /// <summary>构建 v2.0.0 兼容档（desc=descriptionType、cprt=textType、TRC 一律 curv 采样；无 chad/bkpt）。</summary>
        public static byte[]? BuildV2(RgbSpace s) => BuildCore(s, IccProfileVersion.V2);

        private static byte[]? BuildCore(RgbSpace s, IccProfileVersion version)
        {
            try
            {
                bool directMatrix = s.D50Colorants is { Length: 9 };
                if (!directMatrix && (s.Rp.Length != 2 || s.Gp.Length != 2
                    || s.Bp.Length != 2 || s.Wp.Length != 2)) return null;

                // ── 色度 → 线性 RGB→XYZ（白点归一），再 Bradford 适应到 D50 PCS ──
                double[] adapted;
                double[] chad;
                if (directMatrix)
                {
                    adapted = s.D50Colorants!;
                    chad = s.Chad is { Length: 9 }
                        ? s.Chad!
                        : ColorSpaceRegistry.BradfordAdapt(s.D50WhitePoint ?? D50Xyz, D50Xyz);
                }
                else
                {
                    var rgbToXyz = ColorSpaceRegistry.PrimariesToXyz(s.Rp, s.Gp, s.Bp, s.Wp);
                    chad = ColorSpaceRegistry.BradfordAdapt(ColorSpaceRegistry.XyzFromXy(s.Wp), D50Xyz);
                    adapted = ColorSpaceRegistry.Mat3Mul(chad, rgbToXyz);   // 列向量约定: [rXYZ gXYZ bXYZ]
                }

                bool v2 = version == IccProfileVersion.V2;
                // cprt 短文案（与 lcms/iccgen 同口径）：v4 必须 mluc（textType 是 v2 类型，
                //   只升版本字节不改它 = "4.4 头 + v2 cprt" 混合非法 profile）；v2 才用 textType。
                const string cprtText = "No copyright, use freely";
                var tagDesc = v2 ? BuildDescType(s.Description) : BuildMluc(s.Description);
                var tagCprt = v2 ? BuildText(cprtText) : BuildMluc(cprtText);
                var wtpt = s.D50WhitePoint ?? D50Xyz;
                var tagWtpt = BuildXyz(wtpt[0], wtpt[1], wtpt[2]);
                var tagRXyz = BuildXyz(adapted[0], adapted[3], adapted[6]);
                var tagGXyz = BuildXyz(adapted[1], adapted[4], adapted[7]);
                var tagBXyz = BuildXyz(adapted[2], adapted[5], adapted[8]);
                var tagChad = BuildSf32(chad);
                var tagTrc = v2 ? BuildTrcV2(s.Curve) : BuildTrc(s.Curve);
                var tagChrm = s.IncludeChromaticity && s.Rp.Length == 2 && s.Gp.Length == 2 && s.Bp.Length == 2
                    ? BuildChrm(s.Rp, s.Gp, s.Bp) : null;
                var tagCicp = !v2 && s.HasCicp
                    ? BuildCicp(s.CicpPrimaries!.Value, s.CicpTransfer!.Value, s.CicpMatrix!.Value, s.CicpFullRange!.Value)
                    : null;

                // 目录（签名 → 数据）；三通道 TRC 共享同一数据块。
                // bkpt 两分支都不写（v4.3 起废弃，省略即默认黑点）；chad v4 必填、v2 规范无此 tag 故省。
                var tags = new List<(string Sig, byte[] Data)>
                {
                    ("desc", tagDesc), ("cprt", tagCprt), ("wtpt", tagWtpt),
                    ("rXYZ", tagRXyz), ("gXYZ", tagGXyz), ("bXYZ", tagBXyz),
                };
                if (tagChrm != null) tags.Add(("chrm", tagChrm));
                if (tagCicp != null) tags.Add(("cicp", tagCicp));
                if (!v2) tags.Add(("chad", tagChad));
                tags.Add(("rTRC", tagTrc));
                tags.Add(("gTRC", tagTrc));
                tags.Add(("bTRC", tagTrc));
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
                WriteHeader(w, pos, s, version);
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
                return StampProfileId(ms.ToArray());
            }
            catch { return null; }
        }

        /// <summary>
        /// ICC.1 Profile ID：对“Profile ID 字段（offset 84..99）清零后的整份 profile”做 MD5，
        /// 把 16 字节摘要写回同一位置。只影响 header 的 ID，不改变 tag 数据、体积或对齐；
        /// ProfileID 在 ICC 里是可选项，但补算后第三方校验器不会再把它当“未填”。
        /// </summary>
        private static byte[] StampProfileId(byte[] profile)
        {
            if (profile.Length < 128) return profile;
            var copy = (byte[])profile.Clone();
            Array.Clear(copy, 84, 16);
            var id = MD5.HashData(copy);
            Buffer.BlockCopy(id, 0, profile, 84, 16);
            return profile;
        }

        private static void WriteHeader(BinaryWriter w, int size, RgbSpace s, IccProfileVersion version)
        {
            WriteU32(w, (uint)size);              // 0   profile size
            WriteU32(w, 0);                       // 4   preferred CMM
            // 8   版本号（BCD：byte0=主版本；byte1=次版本高 nibble / 修订低 nibble）。
            //     ★全仓唯一版本字节位★：v44 = 04 40 00 00（v4.4.0）；v2 = 02 00 00 00（v2.0.0）。
            w.Write(version == IccProfileVersion.V44
                ? new byte[] { 4, 0x40, 0, 0 }
                : new byte[] { 2, 0, 0, 0 });
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

        /// <summary>
        /// 'cicp' tag type（ICC.1:2022 §10.3）：type signature + reserved(4) +
        /// ColourPrimaries / TransferCharacteristics / MatrixCoefficients / VideoFullRangeFlag 各 1 B。
        /// 本生成器的数据空间是 RGB，调用方必须把 MatrixCoefficients 设为 0。
        /// </summary>
        private static byte[] BuildCicp(byte primaries, byte transfer, byte matrix, byte fullRange)
        {
            var ms = new MemoryStream(12); var w = new BinaryWriter(ms);
            w.Write(Ascii4("cicp")); WriteU32(w, 0);
            w.Write(primaries); w.Write(transfer); w.Write(matrix); w.Write(fullRange);
            w.Flush(); return ms.ToArray();
        }

        /// <summary>
        /// 'chrm' (chromaticityType)：sig(4)+reserved(4)+channelCount(2)+reserved(2)
        /// + count × (x,y) s15Fixed16。RGB 设备按 red/green/blue 顺序；它是独立于
        /// XYZ 矩阵的色度声明，exiftool 的 <c>ChromaticityChannel1</c> 直接读第一对。
        /// </summary>
        private static byte[] BuildChrm(double[] r, double[] g, double[] b)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(Ascii4("chrm")); WriteU32(w, 0);
            w.Write(Be16(3)); w.Write(Be16(0));
            foreach (var xy in new[] { r, g, b })
            {
                WriteS15(w, xy[0]);
                WriteS15(w, xy[1]);
            }
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
                w.Flush(); return ms.ToArray();
            }
            else
            {
                int n = Math.Clamp(c.TableEntries, 3, 4096);
                return BuildCurv(SampleTransfer(c, n));
            }
        }

        /// <summary>
        /// v2 出口的 TRC（v2 不认 'para'）。两档形态，按曲线形状自动选：
        /// <list type="bullet">
        /// <item><b>单值幂曲线</b>（ProPhoto γ1.8 带线性趾、AdobeRGB γ2.2）：写
        ///   count=1 的 'curv'——ICC 规定 count=1 即 "Y = X^gamma"，gamma 存为 u8Fixed8。
        ///   体积 14 B（vs 1024 点表 2060 B）。触发条件：u8Fixed8 粒度（1/256）扫一遍 gamma，
        ///   与目标曲线逐点 max|差| ≤ 0.5/255（8-bit 消费端半个码阶）。</item>
        /// <item><b>非幂曲线</b>（sRGB 折点、PQ/HLG）：落 256 点 'curv' LUT（514 B）——
        ///   8-bit 消费端 256 级已足够，1024 点对老 JPEG 消费端无收益。</item>
        /// </list>
        /// 数据源与 v44 分支共用 <see cref="SampleTransfer"/>——常数只许有一份。
        /// </summary>
        private static byte[] BuildTrcV2(TransferCurve c)
        {
            double[] target = SampleTransfer(c, 256);
            // u8Fixed8 gamma 网格 1.000..2.500（1/256 步进），选 max|差| 最小的一个量化 gamma
            int bestG = 256; double bestMax = double.MaxValue;
            for (int g = 256; g <= 640; g++)
            {
                double gamma = g / 256.0; double maxD = 0;
                for (int i = 0; i < target.Length; i++)
                {
                    double d = Math.Abs(target[i] - Math.Pow(i / (double)(target.Length - 1), gamma));
                    if (d > maxD) maxD = d;
                }
                if (maxD < bestMax) { bestMax = maxD; bestG = g; }
            }
            if (bestMax <= 0.5 / 255.0)
                return BuildCurvSingle((uint)bestG);
            return BuildCurv(SampleTransfer(c, 256));
        }

        /// <summary>count=1 的 'curv'：ICC 规定 count=1 时表值即 u8Fixed8 gamma，传递函数 = Y=X^gamma。
        /// 共 14 B（sig4+reserved4+count4+value2）。</summary>
        private static byte[] BuildCurvSingle(uint gammaFixed8)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(Ascii4("curv")); WriteU32(w, 0);
            WriteU32(w, 1);
            w.Write(Be16((int)gammaFixed8));
            w.Flush(); return ms.ToArray();
        }

        /// <summary>把传递函数（参数式或 LUT 函数）统一采样成 0..1 归一数组。
        /// v44 的 curv 分支与 v2 全曲线采样共用本函数，避免两份曲线常数漂移。</summary>
        private static double[] SampleTransfer(TransferCurve c, int n)
        {
            var f = c.Parametric
                ? new Func<double, double>(v => v < c.D ? c.C * v : Math.Pow(c.A * v + c.B, c.G))
                : (c.CodeToLinear ?? (v => Math.Pow(v, 2.2)));
            double[] t = new double[n];
            for (int i = 0; i < n; i++) t[i] = Math.Clamp(f(i / (double)(n - 1)), 0.0, 1.0);
            return t;
        }

        /// <summary>'curv'：sig + reserved + count(u32) + count × u16 BE（0..65535）。</summary>
        private static byte[] BuildCurv(double[] samples)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(Ascii4("curv")); WriteU32(w, 0);
            WriteU32(w, (uint)samples.Length);
            foreach (var v in samples) w.Write(Be16((int)Math.Round(v * 65535.0)));
            w.Flush(); return ms.ToArray();
        }

        /// <summary>
        /// v2 descriptionType（类型签名 'desc'）：sig(4)+reserved(4)+asciiCount(4, 含结尾 NUL)+ASCII
        /// + UnicodeLangCode('en')+CountryCode('US')+unicodeCount(4, 含 NUL)+UTF-16BE
        /// + ScriptCode(2)=0 + ScriptCodeCount(2)=0。
        /// </summary>
        private static byte[] BuildDescType(string text)
        {
            var ascii = Encoding.ASCII.GetBytes(text + "\0");
            var utf16 = Encoding.BigEndianUnicode.GetBytes(text + "\0");
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(Ascii4("desc")); WriteU32(w, 0);
            WriteU32(w, (uint)ascii.Length);
            w.Write(ascii);
            w.Write(new[] { (byte)'e', (byte)'n' });
            w.Write(new[] { (byte)'U', (byte)'S' });
            WriteU32(w, (uint)utf16.Length);
            w.Write(utf16);
            w.Write(Be16(0)); w.Write(Be16(0));   // scriptCode=0 + scriptCount=0
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
