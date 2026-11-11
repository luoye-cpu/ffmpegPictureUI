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
        /// 解码 Ultra HDR JPEG → 线性 HDR 浮点（gbrpf32le，1.0 = SDR 白点），把**像素实测**读数打出来。
        /// 用法：<c>ServiceProbe gainmap-stats &lt;file&gt; [none]</c>，或 <c>gainmap-stats --size-recon</c>
        /// （纯函数臂：<see cref="GainMapDecoder.ReconcileBaseSize"/> 的五种结局，无文件、不落盘）。
        /// <para>
        /// 存在理由：P0-2 要证明 Apple 两代样本「施加后峰值 &gt; 底图峰值」，而产品日志里的
        /// 「峰值 N nits」是**由元数据算的**（`203 × 2^HDRCapacityMax`，见 `GainMapDecoder` 的 peakNits），
        /// 不是像素读数 —— 拿它当施加证据就是让声明给自己作证。这里读回解码器**真正写出**的线性像素，
        /// 给出 max / mean / 超过 SDR 白点的分量数；`capLinear` 只是同一条元数据上限，用作**上界**判据。
        /// </para>
        /// <para>
        /// 负控（`none`）：普通 JPEG / 剥掉增益图的同一底图 ⇒ 解码器必须返回 `null`（不得凭空造增益）。
        /// </para>
        /// </summary>
        private static void ProbeGainMapStats(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: ServiceProbe gainmap-stats <file> [none] | --size-recon"); _fail++; return; }
            var path = args[1];
            // ── 纯函数臂（`gainmap-stats --size-recon`）：底图尺寸对账的裁决，无文件 / 无外部工具 / 不落盘 ──
            //   为什么必须落在这里（2026-11-02 第二批）：`GainMapDecoder.ReconcileBaseSize` 覆盖的那个状态
            //   ——「ffprobe 报的尺寸 ≠ 解码帧实际像素**且**容器未声明 clap」—— **造不出文件样本**：
            //   实测改 ISOBMFF 的 ispe 不影响 ffprobe 报的 HEVC 流尺寸（SPS 说了算，三件变异件仍报 64x64），
            //   把 clap 的类型名改掉（`clap_none.heic`）则 ffprobe 不再给 Frame Cropping、解码器也不裁
            //   ⇒ 两侧同时退回 64x64。结论：fail-closed 分支与 TryUniformScale 的放大臂**只能直接钉裁决**，
            //   否则这两个出口永远只有手跑（改前零判据臂）。臂 A1–A8 覆盖五种结局与两条臂边界。
            if (path.Equals("--size-recon", StringComparison.OrdinalIgnoreCase))
            {
                // ⚠ 走**反射**而不是加 `InternalsVisibleTo("ServiceProbe")`：改 `FfmpegGui.csproj` 会触发
                //   整树重还原，而本机 `dotnet restore` 目前对所有工程报
                //   `NETSDK1060 ... Value cannot be null. (Parameter 'path1')`（与本次改动无关，
                //   见 HANDOVER 的环境注记）⇒ 改 csproj 当场让 13 个工程全编不过。反射与既有 `avifgm`
                //   mode 直呼 `private static PatchColr` 是同一形态，不改出货件可见性。
                var mtRec = typeof(GainMapDecoder).GetMethod("ReconcileBaseSize",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                // ⚠ 枚举是**嵌套**在 GainMapDecoder 里的（元数据全名 `…GainMapDecoder+BaseSizeVerdict`），
                //   用 GetNestedType 找；按 "FfmpegGui.Services.BaseSizeVerdict" 找永远为 null（2026-11-02 实测踩过）。
                var etVerdict = typeof(GainMapDecoder).GetNestedType("BaseSizeVerdict",
                    System.Reflection.BindingFlags.NonPublic);
                Check(mtRec != null && etVerdict != null,
                    "A0 反射找得到 internal ReconcileBaseSize / BaseSizeVerdict（找不到则本节余下 7 条全是真空绿）");
                if (mtRec == null || etVerdict == null)
                {
                    Console.WriteLine($"A0 诊断：method={(mtRec == null ? "null" : "ok")} enum={(etVerdict == null ? "null" : "ok")}");
                    return;
                }

                // 一次调用 ⇒ (裁决名, aw, ah)。`out` 形参经反射是**装箱数组元素**，Invoke 后被回写。
                (string V, int W, int H) Recon(int w, int h, long px, bool crop)
                {
                    var a = new object[] { w, h, px, crop, 0, 0 };
                    var r = mtRec.Invoke(null, a);
                    return (r == null ? "null" : r.ToString()!, (int)a[4], (int)a[5]);
                }

                var r1 = Recon(64, 64, 4096, false);
                Console.WriteLine($"A1 verdict={r1.V} out={r1.W}x{r1.H}（探测 64x64 / 实测 4096 = 一致）");
                Check(r1.V == "Match" && r1.W == 64 && r1.H == 64,
                    $"A1 一致：探测尺寸与实测像素相符 ⇒ Match 且不改写（64x64 / 4096）｜实 {r1.V} {r1.W}x{r1.H}");

                // 这一条是 fail-closed 的**牙齿**：256 像素恰好是 64x64 的 1/4 等比（k=4），若先试等比
                // 就会静默出半幅 HDR。容器没声明裁切 ⇒ 行跳步不可知 ⇒ 必须拒绝（连等比也不认）。
                var r2 = Recon(64, 64, 256, false);
                Console.WriteLine($"A2 verdict={r2.V}（探测 64x64 / 实测 256 / 未声明裁切）");
                Check(r2.V == "RejectUndeclared",
                    $"A2 fail-closed：未声明 clap 时即便恰为 1/4 等比也拒绝（64x64 探测 / 256 实测）｜实 {r2.V}");

                // A2b 是 A2 的**另一个方向**：A2 只证明「恰为 1/4 缩小比例」时被拒，挡不住
                // 「闸只挡了缩小臂」的坏实现（例如把 cropDeclared 判断塞进缩小循环里）。
                // 取 4x4 探测 / 256 实测：只能是放大 k=4（(4*4)²=256；缩小臂需 4%k==0 且 (4/k)²==256，永不成立）
                // ⇒ 这条完全落在放大臂上，与 A2 的缩小型互补。
                var r2b = Recon(4, 4, 256, false);
                Console.WriteLine($"A2b verdict={r2b.V}（探测 4x4 / 实测 256 / 未声明裁切 / 放大型）");
                Check(r2b.V == "RejectUndeclared",
                    $"A2b 未声明 clap 的**放大**型错配（4x4 探测 / 256 实测）同样必须拒绝 ⇒ 闸在两条臂之前｜实 {r2b.V}");

                var r3 = Recon(64, 64, 256, true);
                Console.WriteLine($"A3 verdict={r3.V} out={r3.W}x{r3.H}（探测 64x64 / 实测 256 / 已声明裁切）");
                Check(r3.V == "Scaled" && r3.W == 16 && r3.H == 16,
                    $"A3 缩小臂：声明 clap 且 k=4 ⇒ Scaled 16x16｜实 {r3.V} {r3.W}x{r3.H}");

                // 放大臂（探测**偏小**）：解复用器报了 Frame Cropping 但解码器未施加时会出现。
                var r4 = Recon(8, 8, 256, true);
                Console.WriteLine($"A4 verdict={r4.V} out={r4.W}x{r4.H}（探测 8x8 / 实测 256 / 已声明裁切）");
                Check(r4.V == "Scaled" && r4.W == 16 && r4.H == 16,
                    $"A4 放大臂：探测偏小 k=2（8x8 探测 / 256 实测）⇒ Scaled 16x16｜实 {r4.V} {r4.W}x{r4.H}");

                // TryUniformScale 的 k 上限 16（等比，面积最多 ×256）：两端各钉一次，免得将来为
                // 「更大倍率」放宽时把放大臂静默扩到任意比例。
                var r5 = Recon(64, 64, 16, true);
                Console.WriteLine($"A5 verdict={r5.V} out={r5.W}x{r5.H}（探测 64x64 / 实测 16 / k=16 缩小端）");
                Check(r5.V == "Scaled" && r5.W == 4 && r5.H == 4,
                    $"A5 缩小臂边界：k=16 仍接受（64x64 探测 / 16 像素 ⇒ 4x4）｜实 {r5.V} {r5.W}x{r5.H}");

                var r6 = Recon(4, 4, 4096, true);
                Console.WriteLine($"A6 verdict={r6.V} out={r6.W}x{r6.H}（探测 4x4 / 实测 4096 / k=16 放大端）");
                Check(r6.V == "Scaled" && r6.W == 64 && r6.H == 64,
                    $"A6 放大臂边界：k=16 仍接受（4x4 探测 / 4096 像素 ⇒ 64x64）｜实 {r6.V} {r6.W}x{r6.H}");

                var r7 = Recon(64, 64, 1000, true);
                Console.WriteLine($"A7 verdict={r7.V}（探测 64x64 / 实测 1000 / 已声明裁切 / 非等比）");
                Check(r7.V == "RejectNonUniform",
                    $"A7 非等比拒绝：声明 clap 但 4096 → 1000 像素 ⇒ RejectNonUniform（不猜非整数倍）｜实 {r7.V}");

                var r8a = Recon(0, 0, 4096, false);
                var r8b = Recon(64, 64, 0, false);
                Console.WriteLine($"A8 verdict={r8a.V}/{r8b.V} out={r8a.W}x{r8a.H}（退化输入：0 宽 / 0 像素）");
                Check(r8a.V == "Match" && r8b.V == "Match" && r8a.W == 0 && r8a.H == 0,
                    $"A8 退化输入不崩：0 宽 / 0 像素 ⇒ Match 且原样返回（0x0）｜实 {r8a.V} {r8a.W}x{r8a.H} / {r8b.V}");

                return;   // 汇总行与退出码由 Main 统一处理（重复打印会破坏按行匹配）
            }
            var expect = "gainmap";
            for (int i = 2; i < args.Length; i++)
                if (args[i].Equals("none", StringComparison.OrdinalIgnoreCase)) expect = "none";
            // ⚠ 落进**子目录**而不是 %TEMP% 根：本机沙箱对部分进程拒写 %TEMP% 根（`Access denied`，
            //   实测 2026-10-20），而同一进程写 `%TEMP%\gmdecode_…\` 子目录是允许的。
            var rawDir = Path.Combine(Path.GetTempPath(), $"probe_gmstats_{Guid.NewGuid():N}");
            Directory.CreateDirectory(rawDir);
            var raw = Path.Combine(rawDir, "hdr.rgba");
            try
            {
                var res = GainMapDecoder.DecodeToLinearRawAsync(path, raw, log: s => Console.Write(s)).GetAwaiter().GetResult();
                Console.WriteLine($"file={path} expect={expect}");
                if (res == null || !File.Exists(raw))
                {
                    Console.WriteLine("stats decoded=null");
                    Check(expect == "none", "负控：判为非增益图（解码器返回 null）");
                    return;
                }
                var (w, h, peakNits, _, _) = res.Value;
                var b = File.ReadAllBytes(raw);
                int n = w * h;
                double max = double.NegativeInfinity, sum = 0;
                long over = 0;
                for (int i = 0; i < n * 3; i++)
                {
                    float v = BitConverter.ToSingle(b, i * 4);
                    if (v > max) max = v;
                    sum += v;
                    if (v > 1.0f) over++;
                }
                double mean = n > 0 ? sum / (n * 3) : 0;
                double cap = peakNits / GainMapEncoder.KSdrWhiteNits;
                Console.WriteLine($"stats w={w} h={h} peakNits={peakNits:F0} capLinear={cap:F4} " +
                    $"maxLinear={max:F4} meanLinear={mean:F4} over1={over} total={n * 3}");
                Check(expect != "none", $"正向：解码成功且写出线性像素（{w}x{h}）");
                if (expect == "none") return;
                Check(max > 1.0, $"施加后像素峰值超过 SDR 白点（maxLinear={max:F4} > 1.0）");
                Check(over > 0, $"确有像素高于 SDR 白点（over1={over}/{n * 3}）");
                Check(max <= cap * 1.001, $"像素峰值不得超过元数据上限（maxLinear={max:F4} ≤ capLinear={cap:F4}）");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"stats 异常: {ex.GetType().Name}: {ex.Message}");
                Check(false, "gainmap-stats 执行异常");
            }
            finally { try { if (Directory.Exists(rawDir)) Directory.Delete(rawDir, true); } catch { } }
        }

    }
}
