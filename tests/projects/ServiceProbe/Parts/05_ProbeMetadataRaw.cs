using System;
using System.IO;
using System.Text;
using FfmpegGui.Services;
using TC = FfmpegGui.Services.ColorMapping.TransferCurve;   // 色彩映射引擎的曲线表（P1）
using CK = FfmpegGui.Services.ColorMapping.ColorKernels;     // 执行内核（P3）
using CM = FfmpegGui.Services.ColorMapping.ColorMetrics;     // PSNR/ΔE 度量
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
using CP = FfmpegGui.Services.ColorMapping.ColorTransformPlan;   // S5′ 裁决探针
using GF = FfmpegGui.Services.ColorMapping.RawGamutFit;         // `--raw-color-target auto` 判据内核（2026-10-04）
using VK = FfmpegGui.Services.ColorMapping.VerdictKind;          // S5′ 裁决探针
using DG = FfmpegGui.Services.ColorMapping.DegradationCodes;     // S5′ 裁决探针
using F709 = FfmpegGui.Services.ColorMapping.Transfer709Curve;
using GMJ = FfmpegGui.Services.ColorMapping.GainMapJpegCodec;   // GainMap 解码口径/编码参数单一真值（gmgamut）
using AAS = FfmpegGui.Services.AppSettingsService;

namespace ServiceProbe
{
    internal static partial class Program
    {
        /// <summary>
        /// 锁「RAW 输入的拍摄字段补回」与「队列显示名」两条**非显然**约束（2026-09-21 新增）。
        ///
        /// <para>⚠⚠ **刻意不依赖任何素材**：仓库里**没有任何受版本控制的 RAW/DNG 素材**
        /// （`tests/output/**` 与 `tools/src/dng_sdk/**` 都被 `.gitignore` 排除）⇒
        /// 若把锁建在「真跑一张 RAW」上，干净检出时它会 SKIP ⇒ **静默假绿**。
        /// 故这里断言的是**参数形态**与**纯逻辑取值**，任何检出都能跑。
        /// 端到端行为锁见 `verify-metadata-privacy.ps1` 的 ④⑤。</para>
        ///
        /// <para>被锁的四条约束都是「一旦被顺手优化掉就**静默**出错」的类型 ——
        /// 产物看起来完全正常，只是字段又丢了、或隐私又漏了。</para>
        /// </summary>
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

            // ── D. JFIF 头这一步**必须留证据**（`F-11` 的那一半：不合并 exiftool 调用，但失败不许静默）──
            //   实测（exiftool 13.59 + 本仓 publish/PLAN/ffmpeg-full，2026-10-13）三条，判据按它们写：
            //     ① 这一步 `-JFIFVersion=1.02` 在 ffmpeg mjpeg 产物上**恒失败**
            //        （`Warning: Sorry, JFIFVersion is not writable` + rc=1；分组限定写法同样被拒）；
            //     ② 而产物**本来就带** `[JFIF] JFIFVersion : 1.02`（APP0 由编码器写）⇒ 兼容性目标没掉；
            //     ③ 修复前这两条**在日志里完全看不见**（不传回调 + `catch { }`），下一行照样打「元数据恢复完成」。
            //   ⇒ 所以这里**不**断"这一步 rc=0"（那是把编码器的功劳记到这一步头上，且与实测相反），
            //     改断三件：交付件确实带 JFIF（第二个读者读回）｜ 这一步必须点名（见证行）｜
            //     见证行里的 rc 必须等于方法返回的 rc（防"日志说的和实际做的不一致"）。
            //   ⚠ 本组加在**被执行那份**里；`metaraw` 不在 `verify-probe-mirror-parity.ps1` 的 8-mode 表内
            //     ⇒ 不引入新漂移（若以后并入镜像，必须同批把 mode 加进那张表，否则尺子漏一个 mode=假覆盖）。
            if (!ExifToolService.IsAvailable)
            {
                Skip("D1..D3 JFIF 分步点名：exiftool 不可用（需 publish/PLAN 工具链）⇒ 本组未验证，不计通过");
            }
            else
            {
                var jdir = System.IO.Path.Combine(FfmpegGui.Services.PlatformServices.GetTempDir(),
                                                 "jfifprobe_" + System.Guid.NewGuid().ToString("N"));
                var okJpg = System.IO.Path.Combine(jdir, "ok.jpg");
                bool made = false;
                try
                {
                    System.IO.Directory.CreateDirectory(jdir);
                    var jff = FfmpegGui.Services.AppSettingsService.Current.FfmpegPath;
                    // 夹具用**真 ffmpeg 产物**（与产品同编码器），不手搓字节
                    RunCapture(jff, $"-y -v error -f lavfi -i testsrc=size=32x32:rate=1 -frames:v 1 \"{okJpg}\"");
                    made = System.IO.File.Exists(okJpg);
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine("      [skip-ctx] 造 JFIF 夹具异常：" + ex.GetType().Name + " " + ex.Message);
                }
                if (!made)
                {
                    Skip("D1..D3 JFIF 分步点名：造不出 JPEG 夹具（ffmpeg 未产出或临时目录不可写）⇒ 本组未验证");
                }
                else
                {
                    try
                    {
                        var logJ = new System.Text.StringBuilder();
                        int rcJ = RunTask(ExifToolService.EnsureJfifHeaderAsync(okJpg, m => logJ.Append(m).Append('\n')));
                        var sJ = logJ.ToString();
                        var etPath = ExifToolService.DetectedPath ?? "";
                        string Cut2(string v)   // 日志可能带 exiftool 的整段 warning，截断后再进断言消息
                        {
                            var one = v.Replace((char)13, ' ').Replace((char)10, ' ').Trim();
                            return one.Length > 90 ? one.Substring(0, 90) + "…" : one;
                        }

                        // D1 交付件带 JFIF —— 由**第二个读者**（exiftool 读回）证明，不看这一步的返回值
                        var readBack = RunCapture(etPath, $"-a -G1 -s -jfif:all \"{okJpg}\"");
                        Check(readBack.Contains("JFIFVersion"),
                              $"D1 交付 JPEG 必须真的带 JFIF（exiftool 读回；实得「{Cut2(readBack)}」）");

                        // D2 这一步必须点名：`[exiftool] JFIF 头写入 rc=` 是**只有新代码会打**的见证行
                        Check(sJ.Contains("[exiftool] JFIF 头写入 rc="),
                              $"D2 JFIF 这一步必须留痕（修复前不传回调 + catch 全吞 ⇒ 日志一行不留）。实得「{Cut2(sJ)}」");

                        // D3 日志说的 rc 必须等于方法返回的 rc（两个读者对账，防"日志与行为脱钩"）
                        var mrc = System.Text.RegularExpressions.Regex.Match(sJ, @"JFIF 头写入 rc=(-?\d+)");
                        Check(mrc.Success && int.Parse(mrc.Groups[1].Value) == rcJ,
                              $"D3 见证行里的 rc 必须等于返回值（日志={(mrc.Success ? mrc.Groups[1].Value : "<无>")} 返回={rcJ}）");
                        Console.WriteLine($"      [info] D 组夹具实测 rc={rcJ}（exiftool 对本仓 mjpeg 产物**恒**返回非 0 ⇒ 见 F-13）");
                    }
                    finally
                    {
                        try { System.IO.Directory.Delete(jdir, true); } catch { }
                    }
                }
            }
        }

    }
}
