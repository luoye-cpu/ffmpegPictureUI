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
        private static int _pass, _fail, _skip;

        /// <summary>
        /// 从探针输出目录向上定位仓库内工具链（publish/PLAN），写入 AppSettings。
        /// 没这一步，依赖 ffmpeg(iccgen) / exiftool 的断言会“SKIP 开绿”，看板上看不出来。
        /// </summary>
        private static void InitExternalTools()
        {
            // 临时件根钉进产物区（同 Harness.PinScratchRoot；本宿主不共用 Harness）
            try
            {
                var scratch = Path.Combine(Directory.GetCurrentDirectory(), "tests", "output", "_tmp");
                Directory.CreateDirectory(scratch);
                foreach (var k in new[] { "TMP", "TEMP", "TMPDIR" }) Environment.SetEnvironmentVariable(k, scratch);
            }
            catch { }
            try
            {
                var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
                for (int up = 0; dir != null && up < 8; dir = dir.Parent, up++)
                {
                    var ff = System.IO.Path.Combine(dir.FullName, "publish", "PLAN", "ffmpeg-full", "ffmpeg.exe");
                    if (!System.IO.File.Exists(ff)) continue;
                    var st = FfmpegGui.Services.AppSettingsService.Current;
                    st.FfmpegDirectory = System.IO.Path.GetDirectoryName(ff);
                    var et = System.IO.Path.Combine(dir.FullName, "publish", "PLAN", "exiftool", "exiftool.exe");
                    if (System.IO.File.Exists(et)) st.ExifToolPath = et;
                    Console.WriteLine($"tools-root={dir.FullName}");
                    return;
                }
                Console.WriteLine("tools-root=(未找到 publish/PLAN，需外部工具的断言将 SKIP)");
            }
            catch (Exception ex) { Console.WriteLine($"tools-init 异常: {ex.GetType().Name}: {ex.Message}"); }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            // ── P2-5 行为探针的子进程端：把 UTF-8 字节原样写到两条流上 ──
            // 直接写 Console.OpenStandard*()，绕开 Console 编码设置，保证「产出的字节 = UTF-8」可被父进程逐字节验证。
            // 放在 InitExternalTools 之前：子进程要快，且不依赖外部工具。
            if (args.Length > 0 && args[0].Equals("emit-utf8", StringComparison.OrdinalIgnoreCase))
            {
                var payload = "OUT:" + (args.Length > 1 ? args[1] : "");
                Console.OpenStandardOutput().Write(Encoding.UTF8.GetBytes(payload));
                Console.OpenStandardOutput().Flush();
                var errPayload = "ERR:" + (args.Length > 1 ? args[1] : "");
                Console.OpenStandardError().Write(Encoding.UTF8.GetBytes(errPayload));
                Console.OpenStandardError().Flush();
                return 0;
            }
            InitExternalTools();          // 否则 iccgen/exiftool 类互测会静默 SKIP（假绿）
            if (args.Length == 0)
            {
                Console.WriteLine("usage: ServiceProbe <gainmap|icc|icc-dump|cicp|color|faithful|pq|matrix|curve|plan|geometry|bench|selftest|i18n|psnr|simdswitch|thumbnail|oog|avifgm|jxlgm> ...");
                return 2;
            }
            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "gainmap": ProbeGainMap(args); break;
                    case "ultrahdr": ProbeUltraHdr(args).GetAwaiter().GetResult(); break;
                    case "gainmap-isobmff": ProbeGainMapIsoBmff(args); break;
                    // gainmap-mn-parity —— AppleMakerNote **纯托管解析器 vs exiftool 对拍**（AU2-X1，2026-11-10）；
                    //   调用者 = 在册门禁 verify-gainmap-isobmff §1i（合成器 A 件）
                    case "gainmap-mn-parity": ProbeGainMapMnParity(args); break;
                    case "gainmap-stats": ProbeGainMapStats(args); break;
                    case "vendor-hdr": ProbeVendorHdr(args); break;
                    case "icc": ProbeIcc(args); break;
                    case "icc-dump": ProbeIccDump(args); break;
                    case "faithful": ProbeFaithful(args); break;
                    case "thumbnail": ProbeThumbnail(args).GetAwaiter().GetResult(); break;
                    case "oog": ProbeOog().GetAwaiter().GetResult(); break;
                    case "avifgm": ProbeAvifGainMap().GetAwaiter().GetResult(); break;
                    // jxlgm —— JxlGainMapWriter（JXL jhgm 容器组装，2026-11-08）的单元级锁；
                    //   调用者 = 在册门禁 verify-gainmap-jxl.ps1 §1/§2
                    case "jxlgm": ProbeJxlGainMap(); break;
                    case "color": ProbeColor(args); break;
                    case "pq": ProbePq(); break;
                    // DNG 出口的 exiftool 后置串（纯函数）：XMP(700) 必须默认随源走，--strip-xmp 时才不抄
                    case "dng-xmp-args": ProbeDngXmpArgs(); break;
                    case "gamut": ProbeGamut(args); break;
                    // gmgamut —— GainMap 出口的「源传递特性 / 底图原色」接线锁（两条臂各带对照）
                    case "gmgamut": ProbeGmGamut(args).GetAwaiter().GetResult(); break;
                    case "matrix": ProbeMatrix(); break;
                    case "gamutfit": ProbeGamutFit(); break;
                    case "curve": ProbeCurve(); break;
                    case "plan": ProbePlan(args); break;
                    case "contract": ProbeContract(); break;
                    // verdict —— S5′：纯函数级裁决断言（每个 Degradation.Code 一条正向 + 一条负控）
                    case "verdict": ProbeVerdict(); break;
                    case "bitdepth": ProbeBitDepth(args).GetAwaiter().GetResult(); break;
                    case "tonemap": ProbeToneMap(args).GetAwaiter().GetResult(); break;
                    case "tonemapcurve": DumpToneMapCurve(args); break;
                    case "ootf": ProbeOotf(); break;
                    // hlgwire —— **接线级**门禁：HLG 是否在**真实执行路径**（RGB48 内核）上应用了 OOTF
                    case "hlgwire": ProbeHlgWire(); break;
                    case "bt2446": ProbeBt2446(); break;
                    // png3 <file.png> [sbitBits] [cicpPrimaries cicpTransfer] —— 用**生产代码**写 chunk，
                    // 供外部脚本用 ffmpeg/ffprobe/exiftool 做互操作验收（现有测试只验自写自读）。
                    case "png3": ProbePng3(args); break;
                    // pngopt —— PNG 出口优化器判据宿主（png-optimizer-integration T6，契约 C-PROBE@1）；
                    //   六 case 子命令：pixel/ihdr/chunk/argv/off/timeout（每断言一行 PASS|FAIL pngopt-<case>-NN <读数>）。
                    //   调用者 = 批次 2 门禁 verify-png-optimizer.ps1（T7 接线）⇒ 与 bitdepth/png3 同形态。
                    case "pngopt": ProbePngOpt(args).GetAwaiter().GetResult(); break;
                    case "wire": ProbeWire(); break;
                    // peak <文件> [期望元数据峰值] [用户覆盖峰值] —— 端到端验“源峰值读取”（P4c）
                    case "peak": ProbePeak(args); break;
                    // peakmeasure &lt;PQ图&gt; &lt;最高码值&gt; [用户峰值] —— 整帧实测内容峰值（峰值链路最后一档）
                    case "peakmeasure": ProbePeakMeasure(args).GetAwaiter().GetResult(); break;
                    case "iccname": ProbeIccName(); break;
                    // psnr &lt;a&gt; &lt;b&gt; [阈dB] —— 任意两张图（同尺寸）的像素级比对，接线路径的门禁需要它
                    case "psnr": ProbePsnr(args).GetAwaiter().GetResult(); break;
                    // simdswitch（无参）—— 面板「SIMD 优化」开关的消费点行为锁（判据单点在 SimdKernelRouting）
                    case "simdswitch": ProbeSimdSwitch(); break;
                    case "runner": ProbeRunnerGuard(args).GetAwaiter().GetResult(); break;
                    case "settings": ProbeSettingsSave(); break;
                    // metaraw —— RAW 输入的**元数据保留**锁（参数形态 + 纯逻辑，**不需要任何素材**）
                    case "metaraw": ProbeMetadataRaw(); break;
                    case "procstreams": ProbeProcStreams(); break;
                    case "encoding": ProbeProcEncoding(); break;
                    case "ipc": ProbeIpc(); break;
                    // ipc-server [秒] —— 跨进程往返的服务端（驻留）；ipc-client —— 跨进程往返的客户端
                    case "ipc-server":
                        ProbeIpcServer(args.Length > 1 && int.TryParse(args[1], out var sec) ? sec : 30);
                        break;
                    case "ipc-client": ProbeIpcClient(); break;
                    case "ipc-raw-client": ProbeIpcRawClient(); break;
                    // ipc-submit <输入文件> <输出目录> —— 向**正在运行的实例**（真实 GUI）提交任务并等产物
                    case "ipc-submit":
                        if (args.Length > 2) ProbeIpcSubmitToRunning(args[1], args[2]);
                        else { Console.WriteLine("usage: ServiceProbe ipc-submit <inFile> <outDir>"); return 2; }
                        break;
                    case "piperaw-server":
                        ProbePipeRawServer(args.Length > 1 && int.TryParse(args[1], out var psec) ? psec : 20);
                        break;
                    case "piperaw-client": ProbePipeRawClient(); break;
                    case "decision": ProbeDecision(); break;
                    case "engine": ProbeEngineAb(); break;
                    // geometry [输出根目录] —— 引擎「最长边几何缩放」路径覆盖（直调引擎，绕过 QueueProcessor 预缩放）
                    case "geometry": ProbeGeometry(args).GetAwaiter().GetResult(); break;
                    // firstframe <input> [输出根目录] —— 引擎解码步「多帧输入只取首帧」覆盖（直调引擎，绕过路由层）
                    case "firstframe": ProbeFirstframe(args).GetAwaiter().GetResult(); break;
                    // orientation [输出根目录] —— 取向标签（TIFF Orientation）下的**显示尺寸**口径（直调引擎）
                    case "orientation": ProbeOrientation(args).GetAwaiter().GetResult(); break;
                    // orientmeta [输出根目录] —— 元数据恢复不得把 Orientation 抄回已旋正的产物（二次旋转）
                    case "orientmeta": ProbeOrientMeta(args).GetAwaiter().GetResult(); break;
                    case "selftest": ProbeSelfTest(); break;
                    // tooldetect [预算秒] —— 外部工具探测的开销/诚实性/可注入性（见方法头注释）
                    case "tooldetect": ProbeToolDetect(args); break;
                    case "colormath": ProbeColorMathAgainstZimg(); break;
                    // i18n —— 双语本地化端到端：真实现 LocalizationService 能加载两套字典且取值命中
                    case "i18n": ProbeI18n(); break;
                    case "bench": ProbeBench(args); break;
                    // memadmit [pressure] —— 大缓冲准入门的自证（见方法注释；由 verify-gainmap-memory.ps1 调用）
                    case "memadmit": ProbeMemAdmit(args); break;
                    // framelen —— GainMapEncoder 入口**帧长守卫**的动态自证（由 verify-gainmap-memory.ps1 调用）
                    case "framelen": ProbeFrameLengthGuard().GetAwaiter().GetResult(); break;
                    case "cgkparity": ProbeColorGainKitParity(); break;
                    case "xcheck": ProbeXcheck(args).GetAwaiter().GetResult(); break;
                    default:
                        Console.WriteLine($"unknown command: {args[0]}");
                        return 2;
                }
            }
            catch (Exception ex)
            {
                // ⚠ 2026-09-21：加 **`FAIL ` 前缀**（`TESTING.md` 第 85 条处置建议 ①）。
                //   原样只打 `exception: …` ⇒ **不带 `FAIL` 前缀** ⇒ 凡按 `^FAIL` 过滤的读者**看不见它**；
                //   而汇总行照常打印、`fail` 只 +1 ⇒ 实测出现过「`pass` 52→36（16 条整段没跑）而 `fail` 只 +1」
                //   ⇒ 只看汇总行的人会以为"只坏了 1 条"。加前缀后它与 `Check` 的失败行**同形**，不会再被过滤掉。
                Console.WriteLine($"FAIL exception: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine(ex.StackTrace ?? "");
                _fail++;
            }
            // ⚠ `skip=` **只在 >0 时**追加：既有门禁里有按 `PROBE RESULT: pass=N fail=0` 逐字匹配的断言，
            //   无条件改这行的格式会让它们集体转红（无关的假红）。
            Console.WriteLine(_skip > 0
                ? $"PROBE RESULT: pass={_pass} fail={_fail} skip={_skip}"
                : $"PROBE RESULT: pass={_pass} fail={_fail}");
            return _fail == 0 ? 0 : 1;
        }

        /// <summary>
        /// 「本机条件下这条**判不了**」必须**计数并点名**（`SKIP`），既不许静默消失、也不许记成 PASS。
        /// 本仓既有教训：静默 SKIP = 断言无声消失 ⇒ 假绿通道（见 `docs/TESTING.md §6` 与
        /// `docs/HANDOVER_2026-09-22.md §2.2` 第 1 项的 5 脚本普查）。
        /// </summary>
        private static void Skip(string msg)
        {
            Console.WriteLine($"SKIP {msg}");
            _skip++;
        }

        private static void Check(bool ok, string msg)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {msg}");
            if (ok) _pass++; else _fail++;
        }

    }
}
