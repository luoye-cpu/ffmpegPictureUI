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
        /// 厂商 HDR 尾注（OnePlus/OPPO 私有承载 `local.hdr.linear.mask`）的执行面断言（T12/R6）。
        /// <para>
        /// 夹具是**结构层**合成件：段结构合法、但里面的"增益图"只有 22 B 假 JPEG（不可解码）⇒ 本段只钉
        /// 「抽取 + 声明自洽 + 与标准承载互斥 + 参数映射 + 覆盖口径」，
        /// **像素级重建不在这里冒充判据**（真机件的人工对拍见 docs/FIX_PLAN_2026-10-14.md T12）。
        /// </para>
        /// <para>
        /// 为什么落在 ServiceProbe 而不是 `tests/gainmap`：后者当时是 #75/#76 登记在册的**源文本镜像**，
        /// 没有任何门禁执行它的 exe ⇒ 断言写在那里＝"只在死代码里有的断言"（#76 的 ⑤-c 计的正是这类假账）。
        /// ⚠ 2026-10-16 价值普查 A-1 把 `tests/{encode,io,metadata,gainmap}` 这四份无人执行的镜像**整体删除**
        ///   ⇒ 那句"为什么落 ServiceProbe"从此是历史理由；#76 的 ⑤-c 也换成了「只此一份锁」（⑤）。
        /// </para>
        /// </summary>
        private static void ProbeVendorHdr(string[] args)
        {
            byte[] FakeJpeg()
            {
                var j = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
                j.AddRange(System.Text.Encoding.ASCII.GetBytes("JFIF\0"));
                j.AddRange(new byte[] { 1, 1, 0, 0, 1, 0, 1, 0, 0 });
                j.AddRange(new byte[] { 0xFF, 0xD9 });
                return j.ToArray();                       // 22 B，段结构合法
            }
            static byte[] Be(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

            byte[] BuildTrailer(long declaredMaskLen, long declaredMetaLen = VendorHdrTrailer.ParamBlockBytes,
                int layoutMutateOffset = -1, float layoutMutateValue = 0f)
            {
                var jpeg = FakeJpeg();
                var b = new List<byte>();
                var hdr = new List<byte>();
                hdr.AddRange(Be((uint)(8 + 84 + jpeg.Length + 16 + 144 + 64)));   // 记录大小（仅示意）
                hdr.AddRange(BitConverter.GetBytes(1.25f));                        // f32 = 声明比率
                hdr.Add(0); hdr.AddRange(System.Text.Encoding.ASCII.GetBytes("TESTDEV-9"));
                while (hdr.Count < 84) hdr.Add(0);
                b.AddRange(hdr);                                                   // 84 B 记录头
                b.AddRange(jpeg);
                b.AddRange(new byte[16]);
                var meta = new byte[144];
                // 布局稳定位（2026-11-09 双真机样本互证，与 VendorHdrTrailer.LayoutStable 同批维护）：
                // 旧夹具除已锁字段外全 0 ⇒ 布局校验上线后必须补齐真机形态，否则 V1–V19 全死于新校验。
                BitConverter.GetBytes(-1f).CopyTo(meta, 20);
                BitConverter.GetBytes(1f).CopyTo(meta, 28);
                BitConverter.GetBytes(25f).CopyTo(meta, 48);
                BitConverter.GetBytes(10f).CopyTo(meta, 72);
                BitConverter.GetBytes(6f).CopyTo(meta, 76);
                BitConverter.GetBytes(144f).CopyTo(meta, 8);     // 块自带长度 = 定位锚
                BitConverter.GetBytes(-42f).CopyTo(meta, 32);    // min = -42/256 log2
                BitConverter.GetBytes(2.45991f).CopyTo(meta, 36);
                BitConverter.GetBytes(73f).CopyTo(meta, 44);     // max = 73/256 log2
                BitConverter.GetBytes(64).CopyTo(meta, 80);
                BitConverter.GetBytes(48).CopyTo(meta, 84);
                BitConverter.GetBytes(8).CopyTo(meta, 88);
                if (layoutMutateOffset >= 0) BitConverter.GetBytes(layoutMutateValue).CopyTo(meta, layoutMutateOffset);
                b.AddRange(meta);
                b.AddRange(System.Text.Encoding.ASCII.GetBytes(
                    "[{\"length\":" + declaredMaskLen + ",\"name\":\"local.hdr.linear.mask\",\"offset\":1,\"version\":1},"
                    + "{\"length\":" + declaredMetaLen + ",\"name\":\"local.hdr.meta.data\",\"offset\":2,\"version\":1}]"));
                return b.ToArray();
            }

            var ok = VendorHdrTrailer.Probe(BuildTrailer(FakeJpeg().Length));
            Console.WriteLine($"V1 Found={ok.Found} Det={ok.Determinate} reason={ok.FailReason ?? "-"} " +
                $"slack={ok.DeclaredSlack} mask={ok.MaskWidth}x{ok.MaskHeight}x{ok.MaskDepth} ratio={ok.DeclaredRatio} dev='{ok.Device}'");
            Check(ok.Found && ok.Determinate, "V1 合成尾注必须被认出（含声明长度自洽）");

            var mm = VendorHdrTrailer.BuildMetadata(ok);
            Console.WriteLine($"V2 min={mm?.GainMapMin.ToString("F7")} max={mm?.GainMapMax.ToString("F7")} " +
                $"γ={mm?.Gamma.ToString("F5")} ch={mm?.Channels}");
            Check(mm != null && Math.Abs(mm.GainMapMin - (-42f / 256f)) < 1e-6f
                  && Math.Abs(mm.GainMapMax - (73f / 256f)) < 1e-6f
                  && Math.Abs(mm.Gamma - 2.45991f) < 1e-4f && mm.Channels == 1,
                  "V2 参数按 ISO 单位读出（/256 log2 + 单通道）");
            Check(ok.MaskWidth == 64 && ok.MaskHeight == 48 && ok.MaskDepth == 8,
                  "V3 mask 几何取自参数块而非 JPEG 头（夹具的假 JPEG 没有 SOF）");
            Check(Math.Abs(ok.DeclaredRatio - 1.25f) < 1e-6f, "V4 记录头比率读回");
            Check(ok.Device.Contains("TESTDEV"), "V5 设备名取最长可打印串");

            var bad = VendorHdrTrailer.Probe(BuildTrailer(FakeJpeg().Length + 4_000_000));
            Console.WriteLine($"V6 bad: Found={bad.Found} Det={bad.Determinate} reason={bad.FailReason ?? "-"}");
            Check(!bad.Found, "V6 声明长度与实际不符 ⇒ 必须拒（不许靠认得字段名放行）");
            var oddMeta = VendorHdrTrailer.Probe(BuildTrailer(FakeJpeg().Length, 145));
            Console.WriteLine($"V7 oddMeta: Found={oddMeta.Found} reason={oddMeta.FailReason ?? "-"}");
            Check(!oddMeta.Found, "V7 参数块长度不是 144 ⇒ 必须拒");

            // ── 与标准承载互斥 + 实现唯一性：需要仓库内真 tmap 合成件 ──
            var root = FindRepoRoot();
            var tmapPath = root == null ? "" : Path.Combine(root, "tests", "data", "synth_heic_gainmap.heic");
            if (File.Exists(tmapPath))
            {
                var bytes = File.ReadAllBytes(tmapPath);
                var vp = VendorHdrTrailer.Probe(bytes);
                var iso = IsoBmffGainMapProbe.Probe(bytes);
                Console.WriteLine($"V8/V9 vendorFound={vp.Found} isoHasGainMap={iso.HasGainMap} isoForm={iso.Form ?? "-"}");
                Check(!vp.Found, "V8 标准 tmap 件不得被厂商支认出（两支互斥）");
                Check(iso.HasGainMap && iso.Form == "tmap", "V9 ISO 支仍是标准承载的唯一判据");
            }
            else Check(false, $"V8/V9 夹具在位（缺 {tmapPath} ⇒ 互斥判据未跑）");

            // 单一实现锁：只数**带引号的字面量**（实现标记），不数注释里的反引号提及
            //   —— 第一版写成 Contains("local.hdr.linear.mask") 得到 5 个文件，那 4 个是我自己的注释文本
            //   ⇒ 「grep 命中 ≠ 有实现」的反面教材：锁会把散文算成代码。
            int files = -1;
            if (root != null && Directory.Exists(Path.Combine(root, "src")))
            {
                files = 0;
                var marker = "\"local.hdr.linear.mask\"";
                foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
                {
                    var sep = Path.DirectorySeparatorChar;
                    if (f.Contains($"{sep}obj{sep}") || f.Contains($"{sep}bin{sep}")) continue;
                    if (File.ReadAllText(f).Contains(marker)) files++;
                }
            }
            Console.WriteLine($"V10 含引号字面量的 src/*.cs 文件数={files}");
            Check(files == 1, "V10 尾注识别只有一份实现（双拷贝=本仓禁例）");

            // ── 覆盖口径（R1：`--vendor-gm-*`）：非法值必须**点名并沿用实测**，不许静默吞、也不许停转换 ──
            var lo = new FfmpegGui.Models.FfmpegOptions { VendorGmGamma = -1f };
            var lg = new List<string>();
            var mo = VendorHdrTrailer.BuildMetadata(ok, lo, s => lg.Add(s));
            Console.WriteLine($"V11 γ 覆盖=-1 => γ={(mo?.Gamma.ToString("F5"))} logs={lg.Count} [{string.Join(" | ", lg)}]");
            Check(mo != null && Math.Abs(mo.Gamma - 2.45991f) < 1e-4f && lg.Count == 1
                  && lg[0].Contains("vendor-gm-gamma") && lg[0].Contains("ignored"),
                  "V11 非法 γ 覆盖 ⇒ 警告 + 沿用实测值（不停整条转换）");

            var hi = new FfmpegGui.Models.FfmpegOptions { VendorGmMax = 40f };
            var lh = new List<string>();
            var mh = VendorHdrTrailer.BuildMetadata(ok, hi, s => lh.Add(s));
            Console.WriteLine($"V12 max 覆盖=40 => max={(mh?.GainMapMax.ToString("F5"))} logs={lh.Count}");
            Check(mh != null && Math.Abs(mh.GainMapMax - (73f / 256f)) < 1e-6f && lh.Count == 1,
                  "V12 越界（|log2|>10）max 覆盖 ⇒ 警告 + 沿用实测值");

            var hv = new FfmpegGui.Models.FfmpegOptions { VendorGmMin = -0.5f, VendorGmMax = 0.5f, VendorGmGamma = 2.2f };
            var lv = new List<string>();
            var mv = VendorHdrTrailer.BuildMetadata(ok, hv, s => lv.Add(s));
            Console.WriteLine($"V13 合法覆盖 => min={(mv?.GainMapMin.ToString("F3"))} max={(mv?.GainMapMax.ToString("F3"))} γ={(mv?.Gamma.ToString("F3"))} logs={lv.Count}");
            Check(mv != null && mv.GainMapMin == -0.5f && mv.GainMapMax == 0.5f && mv.Gamma == 2.2f && lv.Count == 0,
                  "V13 三个合法覆盖全部生效且零警告");

            var rev = new FfmpegGui.Models.FfmpegOptions { VendorGmMin = 1f, VendorGmMax = -1f };
            var mr = VendorHdrTrailer.BuildMetadata(ok, rev);
            Console.WriteLine($"V14 min>max => null?={(mr == null)} backward={(mr?.BaseRenditionIsHdr.ToString())}");
            Check(mr != null && mr.BaseRenditionIsHdr,
                  "V14 min>max（反向）⇒ 打方向标记交调用方点名，不静默丢弃");

            var zero = new FfmpegGui.Models.FfmpegOptions { VendorGmMin = 0.25f, VendorGmMax = 0.25f };
            var mz = VendorHdrTrailer.BuildMetadata(ok, zero);
            Console.WriteLine($"V15 min==max => meta={(mz == null ? "null" : "non-null")}");
            Check(mz == null, "V15 区间宽度 0 ⇒ 判为无增益可承载（返回 null 由上层降级）");

            // ── V16/V17：非有限值必须**不采纳**（2026-10-15 审查抓到的漏判：`Math.Abs(NaN)>10` 为 false
            //   ⇒ 旧守卫会让 NaN 被静默采纳；而 FfmpegOptions 会随设置落盘，STJ 默认拒写 NaN ⇒ 保存当场抛）──
            foreach (var (vName, vBad) in new[] { ("V16 NaN", float.NaN), ("V17 Infinity", float.PositiveInfinity) })
            {
                var o16 = new FfmpegGui.Models.FfmpegOptions { VendorGmMin = vBad, VendorGmMax = vBad, VendorGmGamma = vBad };
                var l16 = new List<string>();
                var m16 = VendorHdrTrailer.BuildMetadata(ok, o16, s => l16.Add(s));
                bool kept = m16 != null
                    && Math.Abs(m16.GainMapMin - (-42f / 256f)) < 1e-6f
                    && Math.Abs(m16.GainMapMax - (73f / 256f)) < 1e-6f
                    && Math.Abs(m16.Gamma - 2.45991f) < 1e-4f;
                Console.WriteLine($"{vName} => min={(m16?.GainMapMin.ToString("F5"))} max={(m16?.GainMapMax.ToString("F5"))} " +
                    $"γ={(m16?.Gamma.ToString("F5"))} 警告 {l16.Count} 条");
                Check(kept && l16.Count == 3,
                    $"{vName} 三个覆盖位全部被拒并各自点名警告（实测值原样保留 ⇒ 绝不落进设置/像素）");
            }

            // ── V18：清单点名了厂商层、但结构核对不过 ⇒ 必须**响亮点名**，不许静默丢掉 HDR 层 ──
            //   （审查抓到的诚实性缺陷：旧写法只在 `!Determinate` 时打日志，而"长度不符/参数块没找到/
            //    几何不可信"这几条 Determinate=true ⇒ 一声不响地按普通 SDR 出件。）
            if (root != null)
            {
                var v18Path = Path.Combine(root, "tests", "output", "_vendorhdr_v18.heic");
                try
                {
                    var v18Log = new List<string>();
                    // 声明长度与真实 JPEG 段不符 ⇒ Probe 判 Found=false 且 FailReason 非空
                    File.WriteAllBytes(v18Path, BuildTrailer(FakeJpeg().Length + 4_000_000));
                    bool v18 = GainMapDecoder.IsVendorHdrTrailerAsync(v18Path, s => v18Log.Add(s)).GetAwaiter().GetResult();
                    string v18All = string.Join(" | ", v18Log);
                    Console.WriteLine($"V18 返回={v18} 日志 {v18Log.Count} 条 [{v18All}]");
                    Check(!v18 && v18Log.Count >= 1 && v18All.Contains("no JPEG matches the declared mask length")
                          && v18All.Contains("not counted as a vendor HDR layer"),
                          "V18 检出厂商清单但结构核对不过 ⇒ 必须点名拒因（不许静默当「没有 HDR 层」）");
                }
                finally { try { if (File.Exists(v18Path)) File.Delete(v18Path); } catch { } }
            }
            else Check(false, "V18 需要仓库根（找不到 src/ + tests/ 的那一层 ⇒ 判红，不静默跳过）");

            // ── V19：参数块布局指纹校验（2026-11-09 批次 C / P2.1）──
            //   稳定位置被改（off 48: 25.0 → 24.0；该位置不在任何已锁字段/定位锚上 ⇒ 只可能死于新校验）
            //   ⇒ 必须 Found=false + Determinate=false + FailReason 点名 offset 48。
            //   没有这条负控，将来删掉 Probe 里的布局校验块，§3d 照样全绿（无牙判据）。
            {
                var lay = VendorHdrTrailer.Probe(BuildTrailer(FakeJpeg().Length, layoutMutateOffset: 48, layoutMutateValue: 24f));
                Console.WriteLine($"V19 layout: Found={lay.Found} Det={lay.Determinate} reason={lay.FailReason ?? "-"}");
                Check(!lay.Found && !lay.Determinate && (lay.FailReason ?? "").Contains("offset 48"),
                    "V19 参数块布局指纹被改 ⇒ 拒认 + Determinate=false + 点名 offset（2026-11-09 双真机样本互证的布局校验有牙）");
            }

            // ── W 组：真机件臂（可选位置参数 `vendor-hdr <path>`；2026-11-09 接入）──
            //   语料 = tests/output/corpus/gainmap-vendor-heic/IMG20261101124423.heic
            //   （OnePlus 15T 直出 HEIC，含 GPS 隐私 ⇒ 不入仓，门禁侧 verify-gainmap-isobmff §3v
            //   用 sha256[:16] 锁定字节）。V1–V19 是结构层合成件自测；三处真机形态差异合成件造不出：
            //     ① mask 恰为主图 1/2 分辨率（1536x2048 vs 3072x4096）；
            //     ② max = 50/256 log2（合成件是 73/256）；
            //     ③ 记录头比率 = f32 @ SOI 前 80B、紧贴设备名（本件 1.2，与 2026-10-14 历史样本同值；
            //       ⚠ 2026-11-09 勘误：此前任务口径记「16B 前导 0 + 设备名、无 f32 比率 ⇒ DeclaredRatio=0」
            //       是把 SOI 前 16B 的零填充误读成「无比率字段」——当轮探针实测 ratio=1.2，且是
            //       SOI 前 128B 窗口内唯一 (1.0,64] 的 f32 命中；「头部比率不参与 Found 判定」由
            //       门禁 §3v 臂③b 的字节级变异负控钉住（抹掉 1.2f ⇒ ratio=0 且 Found 仍 True）。
            //   计数契约：W 组走**独立计数器**（尾行 real=N fail=M），不并入 V 组的 _pass ⇒
            //   verify-gainmap-isobmff §3d 的「PROBE RESULT: pass=19 fail=0」逐字匹配形状不变
            //   （2026-11-09 批次 C：V19 布局负控上线，V 组 18→19，门禁字面量同批）。
            //   real 臂失败 ⇒ _fail++（exit code 诚实反映），但 _pass 恒 19（条数锁不漂）。
            //   权威读数（2026-11-09 headless 端到端实测）：EXIT=0、HDR 线性像素峰值 232 nits、
            //   识别日志 vendor HDR layer: device=OnePlus 15T, mask 1536x2048x8,
            //   headroom log2 [-0.1067..0.1953] = 0.929x..1.145x、mask JPEG 820,165 B
            //   （manifest 声明 820,181，差 16 ≤ 64 容差）、P1 自洽 slack=209。
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1])) return;   // 无参：合成臂到此为止（现状形状不变）
            string realPath = args[1];
            int rp = 0, rf = 0;
            void WCk(bool ok, string msg)
            {
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {msg}");
                if (ok) rp++; else rf++;
            }
            if (!File.Exists(realPath))
            {
                Console.WriteLine($"FAIL W0 文件不存在: {realPath}");
                rf++;
            }
            else
            {
                var realBytes = File.ReadAllBytes(realPath);
                var wp = VendorHdrTrailer.Probe(realBytes);
                Console.WriteLine($"W Found={wp.Found} Det={wp.Determinate} reason={wp.FailReason ?? "-"} " +
                    $"dev='{wp.Device}' mask={wp.MaskWidth}x{wp.MaskHeight}x{wp.MaskDepth} " +
                    $"ratio={wp.DeclaredRatio} slack={wp.DeclaredSlack} jpegLen={(wp.MaskJpeg?.Length ?? -1)}");
                WCk(wp.Found && wp.Determinate,
                    $"W1 真机件被识别（Found=True 且 Determinate=True｜实 Found={wp.Found} Det={wp.Determinate} reason={wp.FailReason ?? "-"}）");
                WCk(wp.Device == "OnePlus 15T", $"W2 设备名 OnePlus 15T（实 '{wp.Device}'）");
                WCk(wp.MaskWidth == 1536 && wp.MaskHeight == 2048 && wp.MaskDepth == 8,
                    $"W3 mask 几何 1536x2048x8（实 {wp.MaskWidth}x{wp.MaskHeight}x{wp.MaskDepth}，恰为主图 3072x4096 的 1/2）");
                var wm = VendorHdrTrailer.BuildMetadata(wp);
                Console.WriteLine(wm != null
                    ? $"W-meta min={wm.GainMapMin.ToString("G9")} max={wm.GainMapMax.ToString("G9")} g={wm.Gamma.ToString("G9")} ch={wm.Channels}"
                    : "W-meta null");
                WCk(wm != null && Math.Abs(wm.GainMapMin - (-0.1067f)) <= 0.001f,
                    $"W4 min log2 = -27.3185/256 ≈ -0.1067±0.001（实 {wm?.GainMapMin.ToString("G9")}）");
                WCk(wm != null && Math.Abs(wm.GainMapMax - 0.1953f) <= 0.001f,
                    $"W5 max log2 = 50/256 ≈ 0.1953±0.001（实 {wm?.GainMapMax.ToString("G9")}）");
                WCk(wm != null && Math.Abs(wm.Gamma - 2.9126f) <= 0.01f,
                    $"W6 gamma ≈ 2.9126±0.01（实 {wm?.Gamma.ToString("G9")}）");
                WCk((wp.MaskJpeg?.Length ?? -1) == 820165,
                    $"W7 mask JPEG 长度 820165（manifest 声明 820181，差 16 ≤ 64 容差｜实 {(wp.MaskJpeg?.Length ?? -1)}）");
                WCk(Math.Abs(wp.DeclaredRatio - 1.2f) <= 0.001f,
                    $"W8 DeclaredRatio=1.2（记录头 f32 @ SOI 前 80B、紧贴设备名；2026-10-14 历史样本同值｜实 {wp.DeclaredRatio}）");
                WCk(wp.DeclaredSlack == 209, $"W9 声明自洽 slack=209（实 {wp.DeclaredSlack}）");
            }
            Console.WriteLine($"real={rp} fail={rf}");
            if (rf > 0) _fail++;   // 真文件臂失败 ⇒ 进程级失败；pass（V 组条数锁 18）不受影响
        }

    }
}
