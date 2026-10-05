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

namespace Tests.Encode
{
    /// <summary>
    /// 子系统测试组：**encode**（编码参数与产物）—— 2026-10-05 从 ServiceProbe 的单个
    /// 577 KB <c>Program.cs</c> 中物理拆出，使"改编码这一块"只需跑本工程，不再跑整个宿主。
    ///
    /// <para><b>覆盖的 mode</b>：<c>encoding</c> <c>bitdepth</c> <c>png3</c> <c>thumbnail</c>
    /// （见 <c>TestGroups</c>）。</para>
    ///
    /// <para><b>抽取原则</b>：<c>Probe*</c> 方法体**逐字复制**自
    /// <c>tests/ServiceProbe/Program.cs</c>（不改逻辑、不改字符串 —— 门禁在 grep 那些文本），
    /// 共享 helper 一律**转发**到 <c>Harness</c>。只被本组用的 helper
    /// （<c>MakeTarget</c> / <c>PngBitDepth</c> / <c>T21_StructuralLockOnCliMapping</c>）
    /// 随各自 probe 一起搬来，**不复制**实现。</para>
    /// </summary>
    internal static class EncodeGroup
    {
        // ⚠ 共享 helper 一律**转发** Harness（实现只此一份，避免各组读数分叉）。
        private static void Check(bool ok, string msg) => Harness.Check(ok, msg);
        private static void Skip(string msg) => Harness.Skip(msg);
        /// <summary>
        /// <c>RunCapture</c> —— 按 <paramref name="redirectOut"/> **分流**到两个 Harness 入口。
        /// <para><b>为什么必须分流</b>：旧宿主只有一个 <c>RunCapture(exe, args, redirectOut)</c>，
        /// 它有两种语义 ——</para>
        /// <list type="bullet">
        /// <item><c>redirectOut == null</c>：**返回 stdout 文本**（调用方要 grep 它，如
        /// <c>exiftool -n -s -ThumbnailLength</c> 的声明长度、<c>-s -ColorSpace</c>）。</item>
        /// <item><c>redirectOut != null</c>：把 stdout **逐字节**落盘（<c>exiftool -b -ThumbnailImage</c>
        /// 吐的是**二进制图像数据**，含 <c>0x00</c>），**返回空串**。</item>
        /// </list>
        /// <para>⚠ 若把两者都接到 <c>Harness.RunCaptureBinary</c>，则 <c>redirectOut == null</c> 时
        /// 它把 stdout 倒进 <c>Stream.Null</c> 并**返回空串** ⇒ <c>ThumbnailLength</c> 恒解析为 0
        /// ⇒ T11/T12/T17 全红（**实测踩过**：declared 从 1361 掉到 0）。</para>
        /// <para>⚠ 若把两者都接到 <c>Harness.RunCapture</c>，则二进制那路被 <c>File.WriteAllText</c>
        /// 破坏 <c>0x00</c> ⇒ "声明长度 == 实取字节数"判据失真。故**只能按 redirectOut 分流**。</para>
        /// </summary>
        private static string RunCapture(string exe, string args, string? redirectOut = null)
            => redirectOut != null ? Harness.RunCaptureBinary(exe, args, redirectOut)
                                   : Harness.RunCapture(exe, args);
        private static int RunTask(System.Threading.Tasks.Task<int> t) => Harness.RunTask(t);

        /// <summary>
        /// <c>Sha(path)</c> —— 全文 SHA-256（大写 hex，64 字符），转发 <c>Harness.Sha</c>。
        /// <para>⚠ **不要**转发 <c>Harness.Sha16</c>：那只取前 8 字节。旧宿主用的是**全文**，
        /// 判据是"嵌入缩略图前后主图解码**逐字节相同**"，换成 16 字符会削弱判据强度。</para>
        /// </summary>
        private static string Sha(string path) => Harness.Sha(path);

        // ── 入口（供 Program 分派；目标方法体即抽出的 Probe* 原文）──
        internal static void RunProcEncoding() => ProbeProcEncoding();
        internal static void RunBitDepth(string[] a) => ProbeBitDepth(a).GetAwaiter().GetResult();
        internal static void RunPng3(string[] a) => ProbePng3(a);
        internal static void RunThumbnail(string[] a) => ProbeThumbnail(a).GetAwaiter().GetResult();

        private static int PngBitDepth(string path)
        {
            var b = File.ReadAllBytes(path);
            return b.Length > 25 ? b[24] : -1;
        }
        private static FfmpegGui.Services.ColorMapping.TransformSpec Spec(
            string sp, string st, string dp, string dt, bool matrix = true, bool clamp = true)
        {
            var src = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor.FromCicp(sp, st, sp) ?? throw new Exception($"src {sp}/{st}");
            var dst = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor.FromCicp(dp, dt, dp) ?? throw new Exception($"dst {dp}/{dt}");

            // ⚠️ 亮度域换算必须与生产 `ColorTransformPlan.ToSpec()` **同口径**，否则本探针测的根本
            //    不是真实引擎路径：PQ 的 1.0 = 10000nits、HLG = 1000nits、其余 = 203nits（SDR 参考白）。
            //    旧实现两项都留默认 1 ⇒ PQ→sRGB 不做 49× 换算，与 zimg 参照差出 PSNR 7.3dB，
            //    而那笔账长期被当成「zimg 参照口径分歧（PQ 归一约定不同）」豁免 —— 实为**探针自身的
            //    构造缺陷**（它绕过 ToSpec 手搓 spec），不是引擎的口径分歧。
            double Nits(FfmpegGui.Services.ColorMapping.TransferCurve c)
                => c.Kind switch
                {
                    FfmpegGui.Services.ColorMapping.TransferCurve.CurveKind.Pq
                        => FfmpegGui.Services.ColorMapping.TransferCurve.PqPeakNits,
                    FfmpegGui.Services.ColorMapping.TransferCurve.CurveKind.Hlg
                        => FfmpegGui.Services.ColorMapping.Bt2100Ootf.NominalPeakNits,
                    _ => FfmpegGui.Services.ColorMapping.TransferCurve.SdrWhiteNits,
                };
            double sdr = FfmpegGui.Services.ColorMapping.TransferCurve.SdrWhiteNits;

            // ⚠️ 白名单同步：因本缺陷而被判为「口径分歧」的格子，修复后应从 $knownNonDefects 移除，
            //    否则豁免会把真正的回归也一起免掉（门禁就失去牙齿）。见 verify-color-engine-xcheck.ps1。
            return new FfmpegGui.Services.ColorMapping.TransformSpec
            {
                SrcCurve = src.Curve,
                DstCurve = dst.Curve,
                Matrix = matrix ? src.MatrixTo(dst) : null,
                ClampLinearBeforeEncode = clamp,
                LinearScale = Nits(src.Curve) / sdr,
                EncodeScale = sdr / Nits(dst.Curve),
            };
        }
        private static bool MakeTarget(string ff, string avifenc, string dir, string outPath, string fmt)
        {
            try
            {
                if (File.Exists(outPath)) File.Delete(outPath);
                if (fmt == "avif")
                {
                    if (string.IsNullOrEmpty(avifenc)) return false;
                    var png = Path.Combine(dir, "th_avif_in.png");
                    RunCapture(ff, "-y -hide_banner -loglevel error -f lavfi -i \"testsrc=s=64x48:rate=1\" -frames:v 1 "
                                 + $"-update 1 \"{png}\"");
                    RunCapture(avifenc, $"-q 60 \"{png}\" \"{outPath}\"");
                }
                else
                {
                    var codec = fmt switch
                    {
                        "jxl" => "-c:v libjxl ",
                        "jxr" => "-c:v libjxr ",   // 本构建带 jxrlib（实测 rc=0，FileType=HDP）
                        _ => ""
                    };
                    var loop = fmt == "gif" ? "-loop 0 " : "";   // gif 无调色板路径时 testsrc 直出即可
                    RunCapture(ff, "-y -hide_banner -loglevel error -f lavfi -i \"testsrc=s=64x48:rate=1\" -frames:v 1 "
                                 + $"{loop}{codec}-update 1 \"{outPath}\"");
                }
                return File.Exists(outPath) && new FileInfo(outPath).Length > 0;
            }
            catch { return false; }
        }
        private static void T21_StructuralLockOnCliMapping()
        {
            string? root = null;
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            for (int up = 0; d != null && up < 8; d = d.Parent, up++)
            {
                var c = System.IO.Path.Combine(d.FullName, "src", "FfmpegGui", "CliParser.cs");
                if (System.IO.File.Exists(c)) { root = c; break; }
            }
            if (root == null) { Skip("T21 结构锁：找不到 CliParser.cs 源文件"); return; }

            var src = System.IO.File.ReadAllText(root);
            foreach (var line in new[] {
                "case \"thumbnail\": options.EnableThumbnail = ParseBoolStrict(key, value); break;",
                "case \"thumbnail-size\": options.ThumbnailLongEdge = ParseIntStrict(key, value); break;",
                "case \"thumbnail-quality\": options.ThumbnailQuality = ParseIntStrict(key, value); break;" })
                Check(src.Contains(line), $"T21 映射行在位 [{line}]");
        }
        private static void ProbeProcEncoding()
        {
            const string Text = "中文路径·测试_图片-αβγ.png";
            const string ExpectedOut = "OUT:" + Text;
            const string ExpectedErr = "ERR:" + Text;

            // 宿主可能是 ServiceProbe.exe 自身，也可能经 dotnet 启动（此时 ProcessPath=dotnet.exe，需补 dll）
            var host = Environment.ProcessPath!;
            var prefix = System.IO.Path.GetFileNameWithoutExtension(host)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? "\"" + System.Reflection.Assembly.GetEntryAssembly()!.Location + "\" "
                : "";
            var arguments = prefix + "emit-utf8 \"" + Text + "\"";

            System.Diagnostics.ProcessStartInfo Psi(Encoding? enc) => new()
            {
                FileName = host,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = enc,
                StandardErrorEncoding = enc
            };

            // ① 原始字节：子进程产出的是 UTF-8（读 BaseStream，绕开任何解码）
            using (var p = System.Diagnostics.Process.Start(Psi(null))!)
            {
                using var ms = new System.IO.MemoryStream();
                p.StandardOutput.BaseStream.CopyTo(ms);
                p.WaitForExit();
                var raw = ms.ToArray();
                var want = Encoding.UTF8.GetBytes(ExpectedOut);
                var same = raw.Length == want.Length && raw.AsSpan().SequenceEqual(want);
                Check(same, $"子进程产出确为 UTF-8 字节（原始流 {raw.Length} 字节 vs 期望 {want.Length} 字节，逐字节{(same ? "一致" : "不一致")}）");
            }

            // ② 声明 UTF-8 解码 ⇒ 两条流逐字还原
            using (var p = System.Diagnostics.Process.Start(Psi(Encoding.UTF8))!)
            {
                var o = p.StandardOutput.ReadToEnd();
                var e = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Check(o == ExpectedOut, $"声明 UTF-8 后 stdout 逐字还原（实得「{o}」）");
                Check(e == ExpectedErr, $"声明 UTF-8 后 stderr 逐字还原（实得「{e}」）");
            }

            // ③ 负控：证明上述断言能识别「解码器选错」
            var wrong = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(ExpectedOut));
            Check(wrong != ExpectedOut,
                "负控：同批字节按非 UTF-8 单字节代码页解码必不还原 ⇒ 上述断言能识别解码错误（非空跑）");

            // ④ 现场复现：宿主输出编码非 UTF-8 + 不声明解码编码（.NET 默认就用宿主 Console.OutputEncoding）
            var saved = Console.OutputEncoding;
            string observed;
            bool simulated;
            try
            {
                Console.OutputEncoding = Encoding.Latin1;   // 代表 zh-CN 控制台的 936 一类非 UTF-8 代码页
                simulated = true;
            }
            catch (Exception ex)
            {
                simulated = false;
                observed = "";
                Console.WriteLine($"INFO 无法把宿主输出编码切到非 UTF-8（{ex.GetType().Name}）⇒ ④ 现场复现 SKIP");
            }
            if (simulated)
            {
                try
                {
                    using var p = System.Diagnostics.Process.Start(Psi(null))!;   // 不声明解码编码 = 旧写法
                    observed = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                }
                finally { Console.OutputEncoding = saved; }
                Check(observed != ExpectedOut,
                    $"现场复现：宿主输出编码非 UTF-8 且未声明解码编码 ⇒ 乱码（实得「{observed}」）");
            }
            // 用 CodePage 比较：Console.OutputEncoding 返回的 UTF8Encoding 实例带控制台默认回退字符，
            // 与 Encoding.UTF8 静态实例的 EncoderFallback 不同 ⇒ Encoding.Equals 会假红。
            Check(Console.OutputEncoding.CodePage == Encoding.UTF8.CodePage,
                $"探针自身仍以 UTF-8 输出（否则门禁日志自身会乱码；实为 {Console.OutputEncoding.WebName}/{Console.OutputEncoding.CodePage}）");
        }
        private static async Task ProbeBitDepth(string[] args)
        {
            string inPng = args.Length > 1 ? args[1] : "tests/output/validate/bit_in.png";
            var ff = FfmpegGui.Services.AppSettingsService.Current.FfmpegPath;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(inPng))!);
            // 造一个 16bit 输入图（内容无关，只验位宽）
            if (!File.Exists(inPng))
            {
                var psi = new System.Diagnostics.ProcessStartInfo(ff!,
                    $"-y -hide_banner -loglevel error -f lavfi -i \"smptehdbars=s=64x48\" -pix_fmt rgb48le -frames:v 1 \"{inPng}\"")
                { RedirectStandardError = true, UseShellExecute = false };
                using var pr = System.Diagnostics.Process.Start(psi)!; pr.StandardError.ReadToEnd(); pr.WaitForExit();
            }
            Check(File.Exists(inPng) && new FileInfo(inPng).Length > 100, $"16bit 输入就绪 {inPng}");

            var adobe = CS.FromNamedSpace("Adobe RGB");
            var srgb = CS.FromCicp("bt709", "iec61966-2-1", "sRGB");
            if (adobe == null || srgb == null) { Check(false, "描述符构造失败"); return; }
            var spec = Spec("bt470bg", "bt470bg", "bt709", "iec61966-2-1");

            foreach (var (depth, expect) in new[] { (8, 8), (16, 16) })
            {
                var it = new CI { Strategy = ST.Recommended, Source = adobe, Target = srgb, Format = CE.CapsFor("png"),
                                  TargetBitDepth = depth, TargetExplicit = true, AllowApproximateCurve = false };
                var plan = CE.Plan(it);
                Check(plan.Action == CA.Map && plan.Backend == CB.InProcess,
                    $"depth={depth}: 用例需走 H1 映射（实为 {plan.Action}/{plan.Backend}）");
                Check(plan.OutBitDepth == expect, $"depth={depth}: Plan.OutBitDepth 应为 {expect}（实为 {plan.OutBitDepth}）");
                Check(plan.NeedsDither == (depth == 8), $"depth={depth}: NeedsDither 应与位深一致（实={plan.NeedsDither}）");

                string outPng = $"tests/output/validate/bit_{depth}.png";
                if (File.Exists(outPng)) File.Delete(outPng);
                var st = await FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(inPng, outPng, spec, plan, null, default, 2);
                Check(File.Exists(outPng) && new FileInfo(outPng).Length > 100, $"depth={depth}: 端到端产出非空 PNG（{st.ProcessMs}ms）");
                int bd = PngBitDepth(outPng);
                Check(bd == expect, $"depth={depth}: 输出 PNG IHDR 位深应为 {expect}（实为 {bd}）");
            }
        }
        private static void ProbePng3(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("用法: png3 <file.png> [sbitBits] [primaries transfer]"); return; }
            string path = args[1];
            // ⚠ 两项要分开报：旧写法 bits=0（意为“不写 sBIT”）也会去调 TryInsertSbit，
            //    它失败后把“cICP 已写成”一起抹平成“写入结果=False”⇒ 看板上误判为失败（实测）。
            bool sbitOk = true, cicpOk = true;
            bool didSbit = false, didCicp = false;
            int sbitBits = 0;
            if (args.Length >= 3 && int.TryParse(args[2], out int bits))
            {
                if (bits > 0) { didSbit = true; sbitBits = bits; sbitOk = FfmpegGui.Services.PngCicpService.TryInsertSbit(path, bits); }
            }
            if (args.Length >= 5 && byte.TryParse(args[3], out byte pr) && byte.TryParse(args[4], out byte tr))
            {
                didCicp = true; cicpOk = FfmpegGui.Services.PngCicpService.TryInsertCicp(path, pr, tr);
            }
            Console.WriteLine($"png3: {path} "
                + (didSbit ? $"sBIT(写 {sbitBits}bit)={sbitOk} " : "sBIT=跳过 ")
                + (didCicp ? $"cICP={cicpOk} " : "cICP=跳过 ")
                + $"大小={(new FileInfo(path).Exists ? new FileInfo(path).Length : -1)}");
            Check((!didSbit || sbitOk) && (!didCicp || cicpOk), "png3 chunk 写入成功");
        }
        private static System.Threading.Tasks.Task ProbeThumbnail(string[] args)
        {
            const string dir = "tests/output/validate";
            var st = FfmpegGui.Services.AppSettingsService.Current;
            var ff = st.FfmpegPath;
            var et = st.ExifToolPath;
            string avifenc = "";
            try
            {
                var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
                for (int up = 0; d != null && up < 8; d = d.Parent, up++)
                {
                    var c = System.IO.Path.Combine(d.FullName, "publish", "PLAN", "artifacts", "avifenc.exe");
                    if (System.IO.File.Exists(c)) { avifenc = c; break; }
                }
            }
            catch { }

            // T0 前置：能力表是**懒播种**的（`SeedLocalRules` 由 InitializeAsync 触发）。
            // 表空时 `GetCapabilities(...) is { SupportsThumbnail: true }` 恒 false ⇒
            // 正向断言全红的同时**负向断言集体假绿**（什么都没在比对）。先证明表非空，再谈逐格式。
            FfmpegGui.Services.FormatCapabilitiesService.ClearCache();
            FfmpegGui.Services.FormatCapabilitiesService.InitializeAsync(ff).GetAwaiter().GetResult();
            var seeded = FfmpegGui.Services.FormatCapabilitiesService.GetCapabilities("jpg") != null;
            Check(seeded, "T0 前置：格式能力表已播种（表空 ⇒ 逐格式断言全是空判据）");
            if (!seeded) return System.Threading.Tasks.Task.CompletedTask;

            // ── T1..T10 纯函数与能力表：不依赖外部编码工具 ──
            var q100 = FfmpegGui.Services.ThumbnailService.MapQualityToQscale(100);
            var q1 = FfmpegGui.Services.ThumbnailService.MapQualityToQscale(1);
            Check(q100 == 2 && q1 == 31, $"T1 质量轴 1..100 → qscale 反向线性且收口 (100→{q100}, 1→{q1})");
            var qMid = FfmpegGui.Services.ThumbnailService.MapQualityToQscale(50);
            Check(qMid > q100 && qMid < q1 && qMid is >= 2 and <= 31, $"T2 质量轴单调 (50→{qMid})");
            Check(FfmpegGui.Services.ThumbnailService.MapQualityToQscale(0) == q1
                  && FfmpegGui.Services.ThumbnailService.MapQualityToQscale(999) == q100,
                  "T3 越界质量被钳到两端（不抛、不产出非法 qscale）");

            var sf = FfmpegGui.Services.ThumbnailService.BuildScaleFilter(160);
            Check(sf.Contains("min(iw,160)") && sf.Contains("min(ih,160)") && sf.Contains("-2"),
                  $"T4 缩放串两维都钳长边、另一维保比例取偶 [{sf}]");
            Check(FfmpegGui.Services.ThumbnailService.ClampLongEdge(0) == 160
                  && FfmpegGui.Services.ThumbnailService.ClampLongEdge(4000) == 512
                  && FfmpegGui.Services.ThumbnailService.ClampLongEdge(1) == 16,
                  "T5 长边钳制 [16,512]，0 回落到默认 160");

            Check(FfmpegGui.Services.ThumbnailService.CapabilityKey("jpeg") == "jpg"
                  && FfmpegGui.Services.ThumbnailService.CapabilityKey(".TIF") == "tiff"
                  && FfmpegGui.Services.ThumbnailService.CapabilityKey("PNG") == "png",
                  "T6 能力表键归一（jpeg/.TIF/PNG 都落到同一条目，避免两处口径）");

            // 能力表：兑现性按 2026-10-02 逐容器实测。判据是「回读字节 == ThumbnailLength 声明」。
            // ⚠ `no` 那四个不是"没测"，是**测了不通**：exiftool 报「0 image files updated」而**退出码仍是 0**
            //   ⇒ 这条负断言防的是"顺手把 rc 当成功"的假绿，也防有人给 GIF/JXR/DNG 开一条走不通的路。
            string[] yes = { "jpg", "png", "apng", "webp", "avif", "jxl" };
            string[] no = { "tiff", "gif", "jxr", "dng" };
            foreach (var f in yes)
                Check(FfmpegGui.Services.FormatCapabilitiesService.GetCapabilities(f) is { SupportsThumbnail: true },
                      $"T7 能力表 {f}=可写缩略图（实测依据见 FormatCapabilitiesService 注释）");
            foreach (var f in no)
                Check(FfmpegGui.Services.FormatCapabilitiesService.GetCapabilities(f) is not { SupportsThumbnail: true },
                      $"T8 能力表 {f}=不可写（实测不通：exiftool 0 updated 但 rc=0）");

            var o = new FfmpegGui.Models.FfmpegOptions { Format = "jpg" };
            Check(FfmpegGui.Services.ThumbnailService.PrivacySkipReason(o) == null, "T9 默认态不跳过（隐私开关全关时不该拦）");
            var oStripAll = new FfmpegGui.Models.FfmpegOptions { Format = "jpg", MetadataMode = FfmpegGui.Models.MetadataMode.StripAll };
            var oStripExif = new FfmpegGui.Models.FfmpegOptions { Format = "jpg", StripExifAll = true };
            Check(FfmpegGui.Services.ThumbnailService.PrivacySkipReason(oStripAll) != null
                  && FfmpegGui.Services.ThumbnailService.PrivacySkipReason(oStripExif) != null,
                  "T10 StripAll / StripExifAll 都能让缩略图让位（隐私不能让新功能破口）");

            if (string.IsNullOrEmpty(ff) || string.IsNullOrEmpty(et))
            {
                Skip("T11..T20 行为断言：ffmpeg/exiftool 未定位（需 publish/PLAN 工具链）");
                return System.Threading.Tasks.Task.CompletedTask;
            }

            Directory.CreateDirectory(dir);

            // 声明-数据闭合判据：返回 (声明长度, 实取字节)
            (int declared, int got) ReadThumb(string path)
            {
                // exiftool 一行一个标签：多行时**逐行**取数（整块 Substring 会把两行拼成一个
                // 解析不出的串，`1980\r\n…` → 声明读成 0 ⇒ 判据假红）。
                int decl = 0;
                foreach (var line in RunCapture(et, $"-n -s -ThumbnailLength \"{path}\"").Split('\n'))
                {
                    var l = line.Trim();
                    var colon = l.LastIndexOf(':');
                    if (colon < 0) continue;
                    if (int.TryParse(l.Substring(colon + 1).Trim(), out var v) && v > 0) { decl = v; break; }
                }
                var bin = path + ".thumb.bin";
                RunCapture(et, $"-b -ThumbnailImage \"{path}\"", redirectOut: bin);
                var got = File.Exists(bin) ? (int)new FileInfo(bin).Length : 0;
                try { if (File.Exists(bin)) File.Delete(bin); } catch { }
                return (decl, got);
            }
            bool Closed(string path) { var (d, g) = ReadThumb(path); return d > 0 && g == d; }

            // ── T11..T15 五个容器逐条真跑 ──
            foreach (var fmt in yes)
            {
                var outPath = $"{dir}/th_{fmt}.{fmt}";
                if (!MakeTarget(ff, avifenc, dir, outPath, fmt)) { Skip($"T11-{fmt} 造不出 {fmt} 目标件"); continue; }
                var opt = new FfmpegGui.Models.FfmpegOptions { Format = fmt, EnableThumbnail = true };
                var log = new System.Text.StringBuilder();
                var rc = RunTask(FfmpegGui.Services.ThumbnailService.ApplyAsync(outPath, opt, s => log.Append(s)));
                var (d, g) = ReadThumb(outPath);
                Check(rc == 0 && Closed(outPath),
                      $"T11 [{fmt}] 缩略图声明-数据闭合 (declared={d}, extracted={g}, rc={rc})");
            }

            // ── T12 悬空负样本：ffmpeg 造的残缺 EXIF 必须被同一把尺子抓住，否则上面全是空判据 ──
            // 两种中转件都量：png 与 apng（同一 eXIf 通路，apng 也照样漏）。
            {
                var src = $"{dir}/th_dangling_src.jpg";
                var donor = $"{dir}/th_donor.jpg";
                var withThumb = $"{dir}/th_dangling_withthumb.jpg";
                MakeTarget(ff, avifenc, dir, src, "jpg");
                MakeTarget(ff, avifenc, dir, donor, "jpg");
                File.Copy(src, withThumb, true);
                RunCapture(et, $"-overwrite_original \"-ThumbnailImage<={donor}\" \"{withThumb}\"");
                foreach (var carrier in new[] { "png", "apng" })
                {
                    var dangling = $"{dir}/th_dangling.{carrier}";
                    RunCapture(ff, $"-y -hide_banner -loglevel error -i \"{withThumb}\" -map_metadata 0 \"{dangling}\"");
                    var (d, g) = ReadThumb(dangling);
                    Check(d > 0 && g == 0,
                          $"T12 [{carrier}] 悬空负样本被抓住：声明 {d} B、实取 {g} B（这条不红，T11 的判据就是假的）");
                }
            }

            // ── T13 诚实性：能力位为 false 的容器必须**出声**拒绝，而不是静默交一张没缩略图的图 ──
            // 逐容器真跑到分发层（不只是查表）：gif/jxr 在这里同时验证「造得出件、但这条路走不通」。
            // dng 只在 T8 查表 ⇒ 它的产物依赖 gitignored 的 RAW 素材，不进默认清单的行为断言。
            foreach (var (badFmt, badExt) in new[] { ("tiff", "tif"), ("gif", "gif"), ("jxr", "jxr") })
            {
                var badPath = $"{dir}/th_neg_{badFmt}.{badExt}";
                if (!MakeTarget(ff, avifenc, dir, badPath, badFmt)) { Skip($"T13-{badFmt} 造不出目标件"); continue; }
                var opt = new FfmpegGui.Models.FfmpegOptions { Format = badFmt, EnableThumbnail = true };
                var log = new System.Text.StringBuilder();
                var rc = RunTask(FfmpegGui.Services.ThumbnailService.ApplyAsync(badPath, opt, s => log.Append(s)));
                var (d, _) = ReadThumb(badPath);
                Check(rc != 0 && log.ToString().Contains("not expressible") && d == 0,
                      $"T13 [{badFmt}] 出声拒绝而非静默丢弃 (rc={rc}, 点名={log.ToString().Contains("not expressible")}, declared={d})");
            }

            // ── T14 反真空对照：开关关掉 ⇒ 一个字不说、文件一个字节都不动 ──
            {
                var png = $"{dir}/th_off_ctrl.png";
                MakeTarget(ff, avifenc, dir, png, "png");
                var before = Sha(png);
                var opt = new FfmpegGui.Models.FfmpegOptions { Format = "png", EnableThumbnail = false };
                var log = new System.Text.StringBuilder();
                var rc = RunTask(FfmpegGui.Services.ThumbnailService.ApplyAsync(png, opt, s => log.Append(s)));
                Check(rc == 0 && log.Length == 0 && Sha(png) == before,
                      $"T14 开关关闭时零播报且零改写 (rc={rc}, logLen={log.Length})");
            }

            // ── T15 不伤主图：嵌入前后解码像素逐字节相同 ──
            {
                var jpg = $"{dir}/th_pixels.jpg";
                MakeTarget(ff, avifenc, dir, jpg, "jpg");
                var a = $"{dir}/th_px_a.ppm"; var b = $"{dir}/th_px_b.ppm";
                RunCapture(ff, $"-y -hide_banner -loglevel error -i \"{jpg}\" -frames:v 1 \"{a}\"");
                var opt = new FfmpegGui.Models.FfmpegOptions { Format = "jpg", EnableThumbnail = true };
                RunTask(FfmpegGui.Services.ThumbnailService.ApplyAsync(jpg, opt!, _ => { }));
                RunCapture(ff, $"-y -hide_banner -loglevel error -i \"{jpg}\" -frames:v 1 \"{b}\"");
                bool same = File.Exists(a) && File.Exists(b)
                    && new FileInfo(a).Length == new FileInfo(b).Length
                    && Sha(a) == Sha(b);
                Check(same, "T15 嵌入缩略图不改动主图像素（前后解码逐字节相同）");
            }

            // ── T16 隐私优先：StripAll 开着也不能把缩略图塞回去 ──
            {
                var png = $"{dir}/th_privacy.png";
                MakeTarget(ff, avifenc, dir, png, "png");
                var opt = new FfmpegGui.Models.FfmpegOptions
                {
                    Format = "png", EnableThumbnail = true, MetadataMode = FfmpegGui.Models.MetadataMode.StripAll
                };
                var log = new System.Text.StringBuilder();
                var rc = RunTask(FfmpegGui.Services.ThumbnailService.ApplyAsync(png, opt, s => log.Append(s)));
                var (d, g) = ReadThumb(png);
                Check(rc != 0 && log.ToString().Contains("skipped") && d == 0 && g == 0,
                      $"T16 StripAll 时缩略图让位 (rc={rc}, declared={d}, extracted={g})");
            }

            // ── T17 越界尺寸只钳不炸，且仍闭合 ──
            {
                var png = $"{dir}/th_clamp.png";
                MakeTarget(ff, avifenc, dir, png, "png");
                var opt = new FfmpegGui.Models.FfmpegOptions
                {
                    Format = "png", EnableThumbnail = true, ThumbnailLongEdge = 9999, ThumbnailQuality = 99
                };
                var rc = RunTask(FfmpegGui.Services.ThumbnailService.ApplyAsync(png, opt, _ => { }));
                Check(rc == 0 && Closed(png), $"T17 长边越界被钳到 512 后仍写出闭合缩略图 (rc={rc})");
            }

            // ── T18 命令行形状锁：探测/embed 两条串不能被人顺手改坏 ──
            {
                var enc = FfmpegGui.Services.ThumbnailService.BuildEncodeArgs("IN.png", "OUT.jpg", 160, 75);
                var emb = FfmpegGui.Services.ThumbnailService.BuildEmbedArgs("T.jpg", "O.png");
                Check(enc.Contains("-vf \"scale='if(gt(iw,ih)") && enc.Contains("-frames:v 1") && enc.Contains("-qscale:v")
                      && emb.Contains("-ThumbnailImage<=\"T.jpg\"") && emb.Contains("-overwrite_original"),
                      $"T18 命令行形状锁 (embed=[{emb}])");
            }

            // ── T19 色彩标签不被带入：写缩略图不得引入 ColorSpace/ICC（色彩后置保留裁决权） ──
            {
                var jpg = $"{dir}/th_color.jpg";
                MakeTarget(ff, avifenc, dir, jpg, "jpg");
                RunTask(FfmpegGui.Services.ThumbnailService.ApplyAsync(jpg,
                    new FfmpegGui.Models.FfmpegOptions { Format = "jpg", EnableThumbnail = true }, _ => { }));
                var cs = RunCapture(et, $"-s -ColorSpace \"{jpg}\"");
                Check(!cs.Contains(":"),
                      $"T19 缩略图写入不附带 ColorSpace 宣称 (读出=[{cs.Trim()}])");
            }

            // ── T20 CLI 可达性：`--thumbnail*` 必须被解析器收下（未知键会在这里暴露） ──
            {
                var res = FfmpegGui.CliParser.Parse(new[] {
                    "--headless", "--input", "in.jpg", "--output", "out", "--format", "jpg",
                    "--thumbnail=true", "--thumbnail-size=240", "--thumbnail-quality=90" });
                res.ExtraOptions.TryGetValue("thumbnail", out var t);
                res.ExtraOptions.TryGetValue("thumbnail-size", out var sz);
                res.ExtraOptions.TryGetValue("thumbnail-quality", out var qq);
                Check(t == "true" && sz == "240" && qq == "90",
                      $"T20 CLI 收下三个键 (thumbnail={t}, size={sz}, quality={qq})");
            }

            // ── T21 结构锁：键 → 模型字段的映射只有 CliParser 里那一行负责，而它在 private 方法链里
            //     （`BuildBaseOptions`/`ApplyExtraOption` 都不对探针可见）⇒ 只能按字面文本钉住。
            T21_StructuralLockOnCliMapping();

            return System.Threading.Tasks.Task.CompletedTask;
        }
    }
}
