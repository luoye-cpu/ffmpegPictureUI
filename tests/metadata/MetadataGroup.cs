using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using Tests.Shared;
using FfmpegGui.Services;
using TC = FfmpegGui.Services.ColorMapping.TransferCurve;
using CK = FfmpegGui.Services.ColorMapping.ColorKernels;
using CM = FfmpegGui.Services.ColorMapping.ColorMetrics;
using CS = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor;
using CE = FfmpegGui.Services.ColorMapping.ColorMappingEngine;
using CI = FfmpegGui.Services.ColorMapping.ColorIntent;
using CA = FfmpegGui.Services.ColorMapping.ColorAction;
using CB = FfmpegGui.Services.ColorMapping.ColorBackend;
using CL = FfmpegGui.Services.ColorMapping.ColorLoss;
using ST = FfmpegGui.Models.ColorStrategy;
using DC = FfmpegGui.Services.ColorMapping.DescriptorConfidence;
using OR = FfmpegGui.Services.ColorMapping.OverrideReason;
using AC = FfmpegGui.Services.ColorMapping.AnnotChoice;
using CSC = FfmpegGui.Services.ColorMapping.CarryScope;
using MI = FfmpegGui.Services.ColorMapping.ManualIntent;
using CP = FfmpegGui.Services.ColorMapping.ColorTransformPlan;
using GF = FfmpegGui.Services.ColorMapping.RawGamutFit;
using VK = FfmpegGui.Services.ColorMapping.VerdictKind;
using DG = FfmpegGui.Services.ColorMapping.DegradationCodes;
using F709 = FfmpegGui.Services.ColorMapping.Transfer709Curve;
using AAS = FfmpegGui.Services.AppSettingsService;

namespace Tests.Metadata
{
    /// <summary>
    /// 子系统测试组：**metadata**（元数据与 ICC）—— 2026-10-05 从 ServiceProbe 的单个
    /// 577 KB <c>Program.cs</c> 中物理拆出，使"改元数据这一块"只需跑本工程，不再跑整个宿主。
    ///
    /// <para><b>覆盖的 mode</b>：<c>metaraw</c> <c>icc</c> <c>icc-dump</c> <c>faithful</c>
    /// （见 <c>TestGroups</c>）。</para>
    ///
    /// <para><b>抽取原则</b>：<c>Probe*</c> 方法体**逐字复制**自
    /// <c>tests/ServiceProbe/Program.cs</c>（不改逻辑、不改字符串 —— 门禁在 grep 那些文本），
    /// 共享 helper 一律**转发**到 <c>Harness</c>。只被本组用的 ICC 字节读取器
    /// （<c>ReadS15</c> / <c>ReadAscii</c> / <c>ReadBe16</c> / <c>ReadBe32</c> /
    /// <c>EnumerateS15</c> / <c>CurvVals</c>）随各自 probe 一起搬来，**不复制**实现。</para>
    /// </summary>
    internal static class MetadataGroup
    {
        // ⚠ 共享 helper 一律**转发** Harness（实现只此一份，避免各组读数分叉）。
        private static void Check(bool ok, string msg) => Harness.Check(ok, msg);
        private static void Skip(string msg) => Harness.Skip(msg);

        /// <summary>
        /// 旧宿主里的 `_fail++`（"给了用法/前置不满足"时**只增计数、不打印**）。
        /// <para>⚠ 转发到 <c>Harness.Fail()</c>，**不是**本组自建计数器：自建一份会让
        /// <c>Harness.SummaryLine()</c>/<c>ExitCode()</c> 读到 0/0 而汇总与计数脱钩。
        /// 也**不能**改写成 <c>Check(false, …)</c> —— 那会多打一行 <c>FAIL …</c>，与旧读数分叉。</para>
        /// </summary>
        private static void Fail() => Harness.FailOnly();

        // ── 入口（供 Program 分派；目标方法体即抽出的 Probe* 原文）──
        internal static void RunMetadataRaw() => ProbeMetadataRaw();
        internal static void RunIcc(string[] a) => ProbeIcc(a);
        internal static void RunIccDump(string[] a) => ProbeIccDump(a);
        internal static void RunFaithful(string[] a) => ProbeFaithful(a);

        private static double ReadS15(byte[] b, int o)
            => ((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]) / 65536.0;
        private static string ReadAscii(byte[] b, int o, int n)
            => o >= 0 && o + n <= b.Length ? Encoding.ASCII.GetString(b, o, n) : "????";
        private static int ReadBe16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
        private static uint ReadBe32(byte[] b, int o)
            => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        private static IEnumerable<string> EnumerateS15(byte[] b, int o, int count)
        {
            for (int k = 0; k < count && o + k * 4 + 3 < b.Length; k++)
                yield return (ReadBe32(b, o + k * 4) / 65536.0).ToString("0.0000");
        }
        private static IEnumerable<string> CurvVals(byte[] b, int o, int n)
        {
            for (int k = 0; k < n && o + k * 2 + 1 < b.Length; k++)
                yield return (((b[o + k * 2] << 8) | b[o + k * 2 + 1]) / 65535.0).ToString("0.0000");
        }
        private static void ProbeMetadataRaw()
        {
            // ── A. 队列显示名：SourceInputPath 优先 ──
            //    RAW 预处理会把 InputPath 改写成内部临时 TIFF（`xxx_raw.tiff`），
            //    若不优先取源路径，队列 / 进度窗 / CLI 会显示**用户从未提供的文件名**。
            var it = new FfmpegGui.Models.QueueItem
            {
                InputPath = @"C:\Temp\raw_abc123\Canon_EOS40D_raw.tiff",
                SourceInputPath = @"D:\photos\Canon_EOS40D.CR2",
            };
            Check(it.DisplayName == "Canon_EOS40D.CR2",
                $"A1 DisplayName 取源路径（实「{it.DisplayName}」）");
            Check(!it.DisplayName.Contains("_raw.tiff"),
                "A2 DisplayName 不得是内部临时文件名");
            it.Status = "已完成";
            Check(it.DisplayText.StartsWith("Canon_EOS40D.CR2", StringComparison.Ordinal),
                $"A3 DisplayText 用显示名（实「{it.DisplayText}」）");

            var it2 = new FfmpegGui.Models.QueueItem { InputPath = @"D:\photos\x.jpg" };
            Check(it2.DisplayName == "x.jpg",
                $"A4 无源路径时回退 InputPath（实「{it2.DisplayName}」）");

            // ── B. 补回拍摄字段的参数形态 ──
            var opt = new FfmpegGui.Models.FfmpegOptions();
            var png = ExifToolService.BuildRawCaptureFieldsArgs(@"D:\photos\a.CR2", @"D:\out\a.png", opt);
            var jpg = ExifToolService.BuildRawCaptureFieldsArgs(@"D:\photos\a.CR2", @"D:\out\a.jpg", opt);

            Check(png.Contains("-DateTimeOriginal") && png.Contains("-CreateDate"),
                "B1 白名单含拍摄时间主字段（DateTimeOriginal / CreateDate）");
            Check(png.Contains("-LensModel") && png.Contains("-MeteringMode") && png.Contains("-ExposureProgram"),
                "B2 白名单含镜头 / 测光 / 曝光程序");
            Check(png.Contains("-EXIF:Flash") && !png.Contains(" -Flash"),
                "B3 Flash 必须带 -EXIF: 限定（裸写会顺带创建 XMP 块，连 --XMP:all 都拦不住）");
            Check(!png.Contains("-all:all"),
                "B4 不得用 -all:all（源 RAW 的 ColorSpace/尺寸/Orientation 会与产物冲突）");
            Check(!png.Contains("-Orientation") && !png.Contains("-ColorSpace") && !png.Contains("-PixelXDimension"),
                "B5 不得回带 Orientation / ColorSpace / PixelXDimension（Orientation 会导致二次旋转）");
            Check(png.Contains("-EXIF:all="),
                "B6 PNG 目标必须先复位损坏的 eXIf 块（-EXIF:all=）");
            Check(!jpg.Contains("-EXIF:all="),
                "B7 负控：非 PNG 目标不得插 -EXIF:all=（会多一次无谓的整文件重写）");
            Check(png.Contains("-TagsFromFile \"D:\\photos\\a.CR2\""),
                "B8 取源必须是**源 RAW**（不是中间 TIFF）");

            // ── C. 隐私开关必须在补漏里同样生效 ──
            //    `CanAbsorbStrip` 为真时那次独立隐私清理会被**跳过** ⇒
            //    补漏若不排除 GPS/XMP，就等于**把刚剥掉的隐私又装回去**。
            var gpsOn = ExifToolService.BuildRawCaptureFieldsArgs(@"D:\a.CR2", @"D:\o.jpg",
                new FfmpegGui.Models.FfmpegOptions { StripExifGps = true });
            Check(!gpsOn.Contains("-GPS:all"),
                "C1 StripExifGps 时不得带 -GPS:all（否则等于把剥掉的隐私装回去）");
            var gpsOff = ExifToolService.BuildRawCaptureFieldsArgs(@"D:\a.CR2", @"D:\o.jpg",
                new FfmpegGui.Models.FfmpegOptions { StripExifGps = false });
            Check(gpsOff.Contains("-GPS:all"), "C2 负控：不剥 GPS 时必须带 -GPS:all");
            var xmpOn = ExifToolService.BuildRawCaptureFieldsArgs(@"D:\a.CR2", @"D:\o.jpg",
                new FfmpegGui.Models.FfmpegOptions { StripXmp = true });
            Check(!xmpOn.Contains("-XMP:all"), "C3 StripXmp 时不得带 -XMP:all");
            var timeOn = ExifToolService.BuildRawCaptureFieldsArgs(@"D:\a.CR2", @"D:\o.jpg",
                new FfmpegGui.Models.FfmpegOptions { StripExifTime = true });
            Check(!timeOn.Contains("-DateTimeOriginal"), "C4 StripExifTime 时不得带 -DateTimeOriginal");
            var camOn = ExifToolService.BuildRawCaptureFieldsArgs(@"D:\a.CR2", @"D:\o.jpg",
                new FfmpegGui.Models.FfmpegOptions { StripExifCamera = true });
            Check(!camOn.Contains("-LensModel"), "C5 StripExifCamera 时不得带 -LensModel");
        }
        private static void ProbeIcc(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: ServiceProbe icc <色彩空间名>"); Fail(); return; }
            var spec = args[1];
            var s = ColorSpaceRegistry.Resolve(spec);
            Console.WriteLine($"spec={s?.Key ?? "(未识别)"} kind={s?.Kind.ToString() ?? "-"} " +
                $"out={s?.OutPrimaries}/{s?.OutTrc}/{s?.OutMatrix}");
            var icc = IccProfileService.ResolveNamedIcc(spec, null,
                s?.OutPrimaries ?? "bt709", s?.OutTrc ?? "iec61966-2-1", s?.OutMatrix ?? "bt709");
            Console.WriteLine($"icc={icc ?? "(null)"}");
            if (icc == null) { Check(false, "命名 ICC 解析成功"); return; }
            var b = File.ReadAllBytes(icc);
            var info = IccProfileService.ParseInfo(icc);
            Console.WriteLine($"iccSize={b.Length} version={info?.Version} class={info?.DeviceClass} cs={info?.ColorSpace} pcs={info?.Pcs} desc={info?.Description}");
            Check(IccProfileService.IsValidIccProfile(icc), "ICC 魔数有效");
            Check(!string.IsNullOrWhiteSpace(info?.Description), "desc 标签存在");
            foreach (var tag in new[] { "desc", "wtpt", "rXYZ", "gXYZ", "bXYZ", "rTRC", "gTRC", "bTRC", "chad" })
            {
                var (off, len) = IccProfileService.FindIccTag(b, tag);
                Console.WriteLine($"tag {tag}: offset={off} size={len}");
            }
            var (wo, wl) = IccProfileService.FindIccTag(b, "wtpt");
            Check(wo > 0 && wl == 20, "wtpt 为 20 字节 XYZ");
            if (wo > 0 && wl >= 20)
            {
                // XYZ = s15Fixed16 ×3 (大端, 值 = raw/65536)
                double X = ReadS15(b, (int)wo + 8), Y = ReadS15(b, (int)wo + 12), Z = ReadS15(b, (int)wo + 16);
                Console.WriteLine($"wtpt X={X:0.0000} Y={Y:0.0000} Z={Z:0.0000}");
                Check(Math.Abs(Y - 1.0) < 0.01, "wtpt Y ≈ 1.0 (PCS 归一)");
            }
        }
        private static void ProbeIccDump(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: ServiceProbe icc-dump <file.icc> [tag ...]"); Fail(); return; }
            var path = args[1];
            var b = File.ReadAllBytes(path);
            var info = IccProfileService.ParseInfo(path);
            Console.WriteLine($"file={path} size={b.Length} declaredSize={(uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3])}");
            Console.WriteLine($"version={info?.Version} class={info?.DeviceClass} cs={info?.ColorSpace} pcs={info?.Pcs} desc={info?.Description}");
            Console.WriteLine($"creator={ReadAscii(b, 80, 4)} intent={ReadAscii(b, 64, 4)} magic={ReadAscii(b, 36, 4)}");
            uint tagCount = (uint)((b[128] << 24) | (b[129] << 16) | (b[130] << 8) | b[131]);
            Console.WriteLine($"tags={tagCount}");
            for (int i = 0; i < tagCount && 132 + i * 12 + 11 < b.Length; i++)
            {
                int e = 132 + i * 12;
                var sig = ReadAscii(b, e, 4);
                uint off = (uint)((b[e + 4] << 24) | (b[e + 5] << 16) | (b[e + 6] << 8) | b[e + 7]);
                uint len = (uint)((b[e + 8] << 24) | (b[e + 9] << 16) | (b[e + 10] << 8) | b[e + 11]);
                var type = off + 8 <= b.Length - 4 ? ReadAscii(b, (int)off, 4) : "????";
                Console.WriteLine($"  {sig} type={type} off={off} len={len}");
            }
            foreach (var tag in args.Skip(2))
            {
                var (off, len) = IccProfileService.FindIccTag(b, tag);
                if (off == 0 || len == 0 || off + len > b.Length) { Console.WriteLine($"{tag}: 未命中/越界"); continue; }
                var o = (int)off;
                var t = ReadAscii(b, o, 4);
                if (t == "para")
                {
                    int ft = (int)ReadBe16(b, o + 8);
                    // parametricCurveType: sig(4) + reserved(4) + functionType(2) + reserved(2) → 参数从 +12 起
                    Console.WriteLine($"{tag}: para type={ft} " + string.Join(" ", EnumerateS15(b, o + 12, ((int)len - 12) / 4)));
                }
                else if (t == "curv")
                {
                    uint n = ReadBe32(b, o + 8);
                    Console.WriteLine($"{tag}: curv count={n}" + (n > 0 && n <= 4 ? $" vals=[{string.Join(",", CurvVals(b, o + 12, (int)n))}]" : ""));
                }
                else if (t == "XYZ ")
                    Console.WriteLine($"{tag}: XYZ [{string.Join(", ", EnumerateS15(b, o + 8, 3))}]");
                else if (t == "mntr")
                    Console.WriteLine($"{tag}: XYZType [{string.Join(", ", EnumerateS15(b, o + 8, 9))}]");
                else if (t == "desc")
                {
                    int al = (int)ReadBe32(b, o + 8);
                    Console.WriteLine($"{tag}: desc \"{Encoding.ASCII.GetString(b, o + 12, Math.Max(0, al - 1))}\"");
                }
                else Console.WriteLine($"{tag}: type={t} len={len}");
            }
        }
        private static void ProbeFaithful(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: ServiceProbe faithful <spec> [yes|no]"); Fail(); return; }
            var spec = args[1];
            var expectYes = args.Length >= 3 ? args[2].StartsWith("y", StringComparison.OrdinalIgnoreCase) : (bool?)null;
            var p1 = IccProfileService.GetFaithfulIcc(spec);
            var p2 = IccProfileService.GetFaithfulIcc(spec);
            Console.WriteLine($"spec={spec} faithfulOnly={IccProfileBuilder.IsFaithfulOnly(spec)} icc={p1 ?? "(null)"}");
            if (p1 == null)
            {
                if (expectYes == false) Check(true, $"{spec} 不走保真路径（预期）");
                else Check(false, $"{spec} 应有保真 ICC 但为 null");
                return;
            }
            Check(expectYes != false, $"{spec} 保真 ICC 解析成功");
            var b1 = File.ReadAllBytes(p1);
            Check(p2 == p1 && File.ReadAllBytes(p2!).AsSpan().SequenceEqual(b1), "重复解析同一缓存且字节一致（确定性）");
            Check(IccProfileService.IsValidIccProfile(p1), "ICC 魔数有效");
            var info = IccProfileService.ParseInfo(p1);
            Console.WriteLine($"size={b1.Length} version={info?.Version} class={info?.DeviceClass} cs={info?.ColorSpace} desc={info?.Description}");
            Check(!string.IsNullOrWhiteSpace(info?.Description), "mluc desc 可回读");
            Check((info?.ColorSpace ?? "").StartsWith("RGB"), "色彩空间签名 RGB");
            // 关键 tag 齐备且 wtpt = D50
            foreach (var t in new[] { "wtpt", "bkpt", "rXYZ", "gXYZ", "bXYZ", "rTRC", "gTRC", "bTRC", "chad", "desc" })
            {
                var (off, len) = IccProfileService.FindIccTag(b1, t);
                Check(off > 0 && len > 0, $"tag {t} 存在 (off={off} len={len})");
            }
            var (wo, _) = IccProfileService.FindIccTag(b1, "wtpt");
            Check(Math.Abs(ReadS15(b1, (int)wo + 12) - 1.0) < 0.001, "wtpt.Y = 1.0（D50 PCS 归一）");
            // tag 目录必须升序（ICC.1 强制 + 本仓库二分查找依赖）
            uint n = (uint)((b1[128] << 24) | (b1[129] << 16) | (b1[130] << 8) | b1[131]);
            bool sorted = true;
            for (int i = 1; i < n; i++)
                if (string.CompareOrdinal(ReadAscii(b1, 132 + (i - 1) * 12, 4), ReadAscii(b1, 132 + i * 12, 4)) > 0) sorted = false;
            Check(sorted, "tag 目录升序");
        }
    }
}
