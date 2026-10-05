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

namespace Tests.GainMap
{
    /// <summary>
    /// 子系统测试组：**gainmap**（增益图 / Ultra HDR）—— 2026-10-05 从 ServiceProbe 的单个
    /// 577 KB <c>Program.cs</c> 中物理拆出，使"改增益图这一块"只需跑本工程，不再跑整个宿主。
    ///
    /// <para><b>覆盖的 mode</b>：<c>gainmap</c> <c>ultrahdr</c> <c>gainmap-isobmff</c>
    /// <c>oog</c> <c>avifgm</c>（见 <c>TestGroups</c>）。</para>
    ///
    /// <para><b>抽取原则</b>：<c>Probe*</c> 方法体**逐字复制**自
    /// <c>tests/ServiceProbe/Program.cs</c>（不改逻辑、不改字符串 —— 门禁在 grep 那些文本），
    /// 共享 helper 一律**转发**到 <c>Harness</c>。只被本组用的 <c>NclxColrBox</c> /
    /// <c>FindNclx</c> / <c>ReadU16Le</c> 随各自 probe 一起搬来，**不复制**实现。</para>
    /// </summary>
    internal static class GainMapGroup
    {
        // ⚠ 共享 helper 一律**转发** Harness（实现只此一份，避免各组读数分叉）。
        private static void Check(bool ok, string msg) => Harness.Check(ok, msg);
        private static void Skip(string msg) => Harness.Skip(msg);
        private static int RunCap(string exe, string args) => Harness.RunCap(exe, args);

        /// <summary>
        /// 旧宿主里的 `_fail++`（"给了用法/前置不满足"时**只增计数、不打印**）。
        /// <para>⚠ 转发到 <c>Harness.Fail()</c>，**不是**本组自建计数器：自建一份会让
        /// <c>Harness.SummaryLine()</c>/<c>ExitCode()</c> 读到 0/0 而汇总与计数脱钩。
        /// 也**不能**改写成 <c>Check(false, …)</c> —— 那会多打一行 <c>FAIL …</c>，与旧读数分叉。</para>
        /// </summary>
        private static void Fail() => Harness.FailOnly();

        // ── 入口（供 Program 分派；目标方法体即抽出的 Probe* 原文）──
        internal static void RunGainMap(string[] a) => ProbeGainMap(a);
        internal static void RunUltraHdr(string[] a) => ProbeUltraHdr(a).GetAwaiter().GetResult();
        internal static void RunGainMapIsoBmff(string[] a) => ProbeGainMapIsoBmff(a);
        internal static void RunOog() => ProbeOog().GetAwaiter().GetResult();
        internal static void RunAvifGainMap() => ProbeAvifGainMap().GetAwaiter().GetResult();

        private static byte[] NclxColrBox(byte fullRange)
        {
            var b = new List<byte>(19);
            b.AddRange(new byte[] { 0, 0, 0, 19 });
            b.AddRange(Encoding.ASCII.GetBytes("colr"));
            b.AddRange(Encoding.ASCII.GetBytes("nclx"));
            b.AddRange(new byte[] { 0, 1, 0, 13, 0, 1, fullRange });
            return b.ToArray();
        }
        private static List<(int Off, int Prim, int Trc, int Mtx, byte Fr)> FindNclx(byte[] b)
        {
            var res = new List<(int Off, int Prim, int Trc, int Mtx, byte Fr)>();
            for (int i = 0; i + 11 <= b.Length; i++)
                if (b[i] == (byte)'n' && b[i + 1] == (byte)'c' && b[i + 2] == (byte)'l' && b[i + 3] == (byte)'x')
                    res.Add((i, (b[i + 4] << 8) | b[i + 5], (b[i + 6] << 8) | b[i + 7],
                             (b[i + 8] << 8) | b[i + 9], b[i + 10]));
            return res;
        }
        private static int[] ReadU16Le(string path)
        {
            var d = File.ReadAllBytes(path);
            var v = new int[d.Length / 2];
            for (int i = 0; i < v.Length; i++) v[i] = d[i * 2] | (d[i * 2 + 1] << 8);
            return v;
        }
        private static void ProbeGainMap(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: ServiceProbe gainmap <jpeg> [outSecondary]"); Fail(); return; }
            var path = args[1];
            var bytes = File.ReadAllBytes(path);
            Console.WriteLine($"file={path} size={bytes.Length}");

            var c = GainMapDecoder.JpegContainer.Parse(bytes);
            Check(c != null, "容器解析非 null");
            if (c == null) return;
            Console.WriteLine($"images={c.Images.Count}");
            for (int i = 0; i < c.Images.Count; i++)
            {
                var im = c.Images[i];
                Console.WriteLine($"image[{i}] start={im.Start} len={im.Length} {im.W}x{im.H}");
            }
            Console.WriteLine($"mpfEntries={c.MpfEntries.Count} mpfBase={c.MpfBaseOffset}");
            for (int i = 0; i < c.MpfEntries.Count; i++)
            {
                var e = c.MpfEntries[i];
                int abs = c.MpfBaseOffset + (int)e.Offset;
                bool soi = abs > 0 && abs + 1 < bytes.Length && bytes[abs] == 0xFF && bytes[abs + 1] == 0xD8;
                Console.WriteLine($"mpf[{i}] attr=0x{e.Attr:X8} size={e.Size} offset={e.Offset} abs={abs} soi={soi}");
            }
            var iso = c.IsoMetadata;
            if (iso == null) Console.WriteLine("iso=null");
            else
            {
                Console.WriteLine($"iso min={iso.GainMapMin:0.####} max={iso.GainMapMax:0.####} gamma={iso.Gamma:0.####} " +
                    $"offsetSDR={iso.OffsetSdr:0.####} offsetHDR={iso.OffsetHdr:0.####} channels={iso.Channels} " +
                    $"hdrBase={iso.BaseRenditionIsHdr} capMin={iso.HdrCapacityMin:0.####} capMax={iso.HdrCapacityMax:0.####}");
            }
            Check(c.IsGainMapContainer(), "IsGainMapContainer()=true");
            Check(c.Images.Count >= 2, $"顶层图像数 {c.Images.Count} >= 2");
            Check(iso != null, "ISO 21496-1 元数据存在");
            // 增益区间非零：SDR 底 max>0（上映射）或 HDR 底 min<0（下映射）均合法
            Check(iso != null && (iso.GainMapMax > 0 || iso.GainMapMin < 0), "ISO 增益区间非零");
            if (iso != null && iso.BaseRenditionIsHdr)
                Console.WriteLine("note=HDR 底（backwardDirection）：增益应为 min<0, max=0");

            var sec = c.GetSecondaryImage();
            Check(sec != null, "副图定位成功");
            if (sec != null && args.Length >= 3)
            {
                File.WriteAllBytes(args[2], c.Slice(sec.Value));
                Console.WriteLine($"secondary_written={args[2]} bytes={c.Slice(sec.Value).Length}");
            }
        }
        private static async Task ProbeUltraHdr(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: ServiceProbe ultrahdr <file>"); Fail(); return; }
            var path = args[1];
            if (!File.Exists(path)) { Console.WriteLine($"FAIL 文件不存在: {path}"); Fail(); return; }

            var ext = Path.GetExtension(path).ToLowerInvariant();
            bool isBmff = ext is ".avif" or ".heic" or ".heif";
            var logs = new List<string>();
            void L(string t) => logs.Add(t.TrimEnd('\n'));

            bool detected = isBmff
                ? await GainMapDecoder.IsIsoBmffGainMapAsync(path, L)
                : await GainMapDecoder.IsUltraHdrManagedAsync(path);
            Console.WriteLine($"[ultrahdr] {Path.GetFileName(path)}  isBmff={isBmff}  detected={detected}");
            foreach (var l in logs) Console.WriteLine($"    log: {l}");
            Check(detected, $"① 容器检测：应判为增益图输入（实 {detected}）");

            logs.Clear();
            var meta = await GainMapDecoder.ReadMetadataAsync(path, L);
            Console.WriteLine(meta == null
                ? "[ultrahdr] metadata = null"
                : $"[ultrahdr] metadata min={meta.GainMapMin} max={meta.GainMapMax} gamma={meta.Gamma} "
                  + $"cap=[{meta.HdrCapacityMin},{meta.HdrCapacityMax}] ch={meta.Channels} useBase={meta.UseBaseColorSpace}");
            foreach (var l in logs) Console.WriteLine($"    log: {l}");
            Check(meta != null, "② 元数据读取：应读出增益元数据（ISO 21496-1 或 XMP）");

            logs.Clear();
            var outRaw = Path.Combine(Path.GetTempPath(), $"uhdr_probe_{Guid.NewGuid():N}.pfm");
            var dec = await GainMapDecoder.DecodeToLinearRawAsync(path, outRaw, L);
            Console.WriteLine(dec == null
                ? "[ultrahdr] decoded = null"
                : $"[ultrahdr] decoded w={dec.Value.w} h={dec.Value.h} peakNits={dec.Value.peakNits:F1} "
                  + $"jxlCs={dec.Value.jxlColorSpace ?? "-"} fileExists={File.Exists(outRaw)}");
            foreach (var l in logs) Console.WriteLine($"    log: {l}");
            Check(dec != null, "③ 线性 HDR 解码：DecodeToLinearRawAsync 应返回非 null");
            if (dec != null)
                Check(dec.Value.peakNits > 203f, $"④ 解码峰值应 > SDR 白点 203（实 {dec.Value.peakNits:F1}）");
            try { if (File.Exists(outRaw)) File.Delete(outRaw); } catch { }
        }
        private static void ProbeGainMapIsoBmff(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: ServiceProbe gainmap-isobmff <file> [outRaw] [gainmap|none]"); Fail(); return; }
            var path = args[1];
            // ⚠ 位置参数**与开关混排**：`none` / `gainmap` 是开关（可出现在任意位置），
            //   其余第一个非开关参数才是 outRaw —— 旧写法写死 args[3] ⇒ `gainmap-isobmff <f> none`
            //   会被当成 outRaw 并静默退回默认期望，负控**整段失效**（实测已踩）。
            var expect = "gainmap";
            string? outRaw = null;
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i].Equals("none", StringComparison.OrdinalIgnoreCase)) expect = "none";
                else if (args[i].Equals("gainmap", StringComparison.OrdinalIgnoreCase)) expect = "gainmap";
                else outRaw = args[i];
            }
            var bytes = File.ReadAllBytes(path);
            Console.WriteLine($"file={path} size={bytes.Length} expect={expect}");

            var r = IsoBmffGainMapProbe.Probe(bytes);
            Console.WriteLine($"isobmff={r.IsIsoBmff} brands={r.Brands}");
            Console.WriteLine($"determinate={r.Determinate} failReason={r.FailReason ?? "-"}");
            Console.WriteLine($"hasGainMap={r.HasGainMap} form={r.Form ?? "-"} primary={r.PrimaryItemId} " +
                $"tmap={r.TmapItemId} gainmapItem={r.GainMapItemId}");
            Console.WriteLine($"codec={r.GainMapCodec ?? "-"} ffmpegFmt={r.GainMapFfmpegFormat ?? "-"} " +
                $"tmapPayload={r.TmapPayload?.Length ?? -1} gmData={r.GainMapItemData?.Length ?? -1}");

            Check(r.IsIsoBmff, "识别为 ISO-BMFF（ftyp 存在）");
            Check(r.Determinate, $"解析确定（failReason={r.FailReason ?? "-"}）");
            if (expect == "none")
            {
                Check(!r.HasGainMap, "负控：判为「无增益图」（hasGainMap=false）");
                return;
            }
            Check(r.HasGainMap, "检测到增益图");
            Check(r.Form == "tmap", $"承载形式为 tmap（实 {r.Form ?? "-"}）");
            Check(r.GainMapItemId > 0, $"增益图 item 已定位（实 {r.GainMapItemId}）");
            Check(r.GainMapFfmpegFormat != null, $"增益图有已知裸流解复用器（实 {r.GainMapFfmpegFormat ?? "-"}）");

            Check(r.TmapPayload != null, "tmap 载荷已切出");
            if (r.TmapPayload == null) return;
            var pl = r.TmapPayload;
            Check(pl.Length >= 22 && pl[0] == 0, $"tmap 载荷 version=0 且长度 ≥22（实 {pl.Length}）");

            var meta = GainMapDecoder.JpegContainer.ParseIso21496(pl, 1, pl.Length - 1);
            Check(meta != null, "ParseIso21496（跳过 version 字节）解析成功");
            if (meta == null) return;
            Console.WriteLine($"iso min={meta.GainMapMin:0.####} max={meta.GainMapMax:0.####} gamma={meta.Gamma:0.####} " +
                $"offsetSDR={meta.OffsetSdr:0.####} offsetHDR={meta.OffsetHdr:0.####} channels={meta.Channels} " +
                $"capMin={meta.HdrCapacityMin:0.####} capMax={meta.HdrCapacityMax:0.####}");
            Check(meta.GainMapMax > 0 || meta.GainMapMin < 0, "ISO 增益区间非零");
            // 载荷长度闭合：1(version) + 21(min/writer/flags+16B headroom) + ch×40
            Check(pl.Length == 1 + 21 + meta.Channels * 40,
                $"载荷长度闭合 1+21+ch×40（实 {pl.Length}，ch={meta.Channels}）");
            // 方向（**不依赖** ParseIso21496 的 flags 位）：base headroom > alternate headroom ⇒ 底图已是 HDR
            bool backward = meta.HdrCapacityMin > meta.HdrCapacityMax;
            Console.WriteLine($"direction={(backward ? "backward(底图=HDR)" : "forward(底图=SDR)")}");

            if (r.GainMapItemData != null && outRaw != null)
            {
                File.WriteAllBytes(outRaw, r.GainMapItemData);
                Console.WriteLine($"gainmap_raw_written={outRaw} bytes={r.GainMapItemData.Length}");
            }
        }
        private static async Task ProbeOog()
        {
            const string dir = "tests/output/validate";
            Directory.CreateDirectory(dir);
            var ff = AAS.Current.FfmpegPath;
            if (string.IsNullOrEmpty(ff) || !File.Exists(ff)) { Skip("oog：ffmpeg 未定位"); return; }

            // 8 个纯色块，每块 8x8 像素；值取在 **P3 编码域**的 16-bit 整数上（含大量 sRGB 越界色）
            int[][] blocks = new[]
            {
                new[] { 65535, 0, 0 }, new[] { 0, 65535, 0 }, new[] { 0, 0, 65535 },
                new[] { 65535, 0, 65535 }, new[] { 0, 65535, 65535 }, new[] { 65535, 65535, 0 },
                new[] { 30000, 5000, 60000 }, new[] { 5000, 15000, 58000 },
            };
            const int W = 8 * 8, H = 8;
            var raw = new byte[W * H * 6];
            for (int b = 0; b < blocks.Length; b++)
                for (int y = 0; y < H; y++)
                    for (int x = b * 8; x < b * 8 + 8; x++)
                    {
                        int o = (y * W + x) * 6;
                        for (int c = 0; c < 3; c++)
                        {
                            ushort v = (ushort)blocks[b][c];
                            raw[o + c * 2] = (byte)(v & 0xFF);            // rgb48le = 小端
                            raw[o + c * 2 + 1] = (byte)(v >> 8);
                        }
                    }
            string gridRaw = $"{dir}/oog_grid.raw", gridPng = $"{dir}/oog_grid.png";
            File.WriteAllBytes(gridRaw, raw);
            RunCap(ff, $"-y -hide_banner -loglevel error -f rawvideo -pix_fmt rgb48le -video_size {W}x{H} "
                     + $"-i \"{gridRaw}\" -update 1 \"{gridPng}\"");
            Check(File.Exists(gridPng) && new FileInfo(gridPng).Length > 100, $"越界网格输入就绪（{W}x{H}, 8 块）");

            // ① 参照 = zimg：与 legacy 路**逐字相同**的那条链（产物级已证 legacy 逐比特等于它）
            string refRaw = $"{dir}/oog_zimg.raw";
            RunCap(ff, $"-y -hide_banner -loglevel error -i \"{gridPng}\" "
                     + $"-vf \"format=rgb48le,zscale=pin=smpte432:tin=iec61966-2-1:min=gbr:p=bt709:t=iec61966-2-1:m=bt709\" "
                     + $"-f rawvideo -pix_fmt rgb48le -update 1 \"{refRaw}\"");

            // ② 被测 = 引擎内核：显式源=Display P3、目标=sRGB、16-bit 出口、GMO 关闭（默认契约）
            var srcD = CS.FromNamedSpace("Display P3");
            var dstD = CS.FromCicp("bt709", "iec61966-2-1", "sRGB");
            if (srcD == null || dstD == null) { Check(false, "oog：描述符构造失败（P3 或 sRGB 取不到）"); return; }
            var it = new CI { Strategy = ST.Recommended, Source = srcD, Target = dstD,
                              Format = CE.CapsFor("png"), TargetBitDepth = 16, TargetExplicit = true,
                              AllowApproximateCurve = false };
            var plan = CE.Plan(it);
            Check(plan.Action == CA.Map && plan.Backend == CB.InProcess,
                  $"oog：该用例应走 H1 进程内映射（实为 {plan.Action}/{plan.Backend}｜{plan.Reason}）");
            var spec = plan.ToSpec();
            Check(!spec.GamutMap, $"oog：GMO 必须关（默认契约=硬裁切），实为 GamutMap={spec.GamutMap}");

            string kernPng = $"{dir}/oog_h1.png", kernRaw = $"{dir}/oog_h1.raw";
            if (File.Exists(kernPng)) File.Delete(kernPng);
            await FfmpegGui.Services.ColorMapping.RawColorPipeline.TransformFileAsync(
                gridPng, kernPng, spec, plan, null, default, 2);
            Check(File.Exists(kernPng) && new FileInfo(kernPng).Length > 100, "oog：内核产出非空 PNG");
            RunCap(ff, $"-y -hide_banner -loglevel error -i \"{kernPng}\" "
                     + $"-f rawvideo -pix_fmt rgb48le -update 1 \"{kernRaw}\"");

            // ③ 逐块对表
            var a = ReadU16Le(refRaw); var k = ReadU16Le(kernRaw);
            Check(a.Length == k.Length && a.Length == W * H * 3, $"两侧样本数一致（zimg={a.Length} h1={k.Length}）");
            if (a.Length != k.Length || a.Length == 0) return;
            int worst = 0, worstBlock = -1, worstCh = -1;
            var perBlock = new int[blocks.Length];
            for (int i = 0; i < a.Length; i += 3)
            {
                int px = i / 3; int blk = px % blocks.Length;
                for (int c = 0; c < 3; c++)
                {
                    int d = Math.Abs(a[i + c] - k[i + c]);
                    if (d > worst) { worst = d; worstBlock = blk; worstCh = c; }
                    perBlock[blk] = Math.Max(perBlock[blk], d);
                }
            }
            var sb = new System.Text.StringBuilder("逐块最大|Δ|（16-bit 单位）：");
            for (int b2 = 0; b2 < perBlock.Length; b2++)
                sb.Append($" 块{b2}={perBlock[b2]}");
            Console.WriteLine("    " + sb);
            Check(worst <= 128,
                  $"越界带：H1 内核与 zimg 参照逐块一致（容差 128/65535 ≈ 0.5/255，实差最大 {worst}"
                  + $" = {Math.Round(worst / 257.0, 1)}/255，出现在块 {worstBlock} 的通道 {new[] { 'R', 'G', 'B' }[Math.Max(worstCh, 0)]}，"
                  + $"源值={blocks[Math.Max(worstBlock, 0)][Math.Max(worstCh, 0)]}）｜{sb}");
        }
        private static async Task ProbeAvifGainMap()
        {
            // ── ①② 单元级：确定性，不需要 ffmpeg ──
            var t = typeof(GainMapEncoder).Assembly
                .GetType("FfmpegGui.Services.IsoBmffGainMapWriter", throwOnError: false);
            Check(t != null, "avifgm：找得到 internal IsoBmffGainMapWriter（否则本 mode 的锁全部真空绿）");
            if (t == null) return;
            var pm = t.GetMethod("PatchColr",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Check(pm != null, "avifgm：找得到 private static PatchColr");
            if (pm == null) return;
            bool Invoke(List<byte[]> props, bool fallback)
                => (bool)pm.Invoke(null, new object[] { props, 1, 13, 1, fallback })!;

            var p1 = new List<byte[]> { NclxColrBox(0x00) };
            var r1 = Invoke(p1, true);
            Check(!r1 && (p1[0][^1] & 0x80) == 0,
                  $"avifgm ①底图 colr=tv(0x00) + 兜底 true ⇒ **必须只读继承 0x00**（旧代码在此写 0x80 = 本缺陷）｜实 0x{p1[0][^1]:X2}");

            var p2 = new List<byte[]> { NclxColrBox(0x80) };
            var r2 = Invoke(p2, false);
            Check(r2 && (p2[0][^1] & 0x80) != 0,
                  $"avifgm ①继承是**双向**的：底图 colr=full(0x80) + 兜底 false ⇒ 仍 0x80（不是一律写 tv）｜实 0x{p2[0][^1]:X2}");

            var p3 = new List<byte[]>();
            var r3 = Invoke(p3, false);
            Check(!r3 && p3.Count == 1 && (p3[0][^1] & 0x80) == 0,
                  $"avifgm ①底图**无** colr ⇒ 用兜底值；兜底 false ⇒ 补 0x00｜实 0x{(p3.Count == 1 ? p3[0][^1] : 0xFF):X2}");

            var bp = t.GetMethod("Build",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?
                .GetParameters().FirstOrDefault(x => x.Name == "baseFullRangeFallback");
            Check(bp != null && bp.HasDefaultValue && bp.DefaultValue is bool dv && !dv,
                  "avifgm ②`Build` 的 baseFullRangeFallback **默认值必须是 false**（limited/tv；旧代码 true ⇒ 每次组装都把 ffmpeg 的 tv 改口成 full）"
                  + $"｜实 {bp?.DefaultValue?.ToString() ?? "(无此参数)"}");

            // ── ③ 端到端：真 ffmpeg + 产品写出器 ──
            var ff = AAS.Current.FfmpegPath;
            if (string.IsNullOrEmpty(ff) || !File.Exists(ff)) { Skip("avifgm ③：ffmpeg 未定位（① ② 已跑，本条不产出读数）"); return; }
            var work = Path.Combine(FfmpegGui.Services.PlatformServices.GetTempDir(), "avifgm_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(work);
            try
            {
                string png = Path.Combine(work, "base.png"), rawAvif = Path.Combine(work, "raw_base.avif");
                RunCap(ff, $"-y -hide_banner -loglevel error -f lavfi -i testsrc2=s=64x48 -frames:v 1 \"{png}\"");
                // 与产品 `EncodeAvifFileAsync` 同口径：yuv420p + 三个 CICP 标志、**不传** -color_range（默认 tv）
                RunCap(ff, $"-y -hide_banner -loglevel error -i \"{png}\" -frames:v 1 -c:v libaom-av1 -crf 18 -b:v 0 "
                         + $"-pix_fmt yuv420p -color_primaries bt709 -color_trc iec61966-2-1 -colorspace bt709 -f avif \"{rawAvif}\"");
                var rawN = File.Exists(rawAvif) ? FindNclx(File.ReadAllBytes(rawAvif)) : new List<(int Off, int Prim, int Trc, int Mtx, byte Fr)>();
                Check(rawN.Count >= 1 && rawN.All(x => (x.Fr & 0x80) == 0),
                      $"avifgm ③前提：ffmpeg 裸底图 colr.full_range 全为 tv(0x00)（实 {rawN.Count} 个 nclx）");

                int w = 64, h = 48;
                var px = new float[w * h * 4];
                for (int i = 0; i < w * h; i++)
                {
                    float u = (i % w) / (float)(w - 1);
                    px[i * 4 + 0] = 0.2f + 3.0f * u;   // 线性峰值 > 1 ⇒ HDR 高光 ⇒ 走真增益图分支
                    px[i * 4 + 1] = 0.5f + 1.0f * u;
                    px[i * 4 + 2] = 0.1f + 2.0f * u;
                    px[i * 4 + 3] = 1f;
                }
                string outAvif = Path.Combine(work, "gm_out.avif");
                var ok = await GainMapEncoder.EncodeAsync(px, w, h, outAvif,
                    hdrPeakNits: 1000f, sdrWhiteNits: 203f, multiChannel: false,
                    baseQuality: 1.5f, gainMapQuality: 1.5f, downsample: 4,
                    log: null, container: "avif");
                Check(ok && File.Exists(outAvif) && new FileInfo(outAvif).Length > 500,
                      $"avifgm ③产品 AVIF 增益图写出成功（实 {(File.Exists(outAvif) ? new FileInfo(outAvif).Length : 0)} B）");
                if (!File.Exists(outAvif)) return;
                var n = FindNclx(File.ReadAllBytes(outAvif));
                Check(n.Count >= 3, $"avifgm ③产物含 3 个 nclx colr（底图/增益图/tmap）｜实 {n.Count}");
                var bc = n.Where(x => x.Prim == 1 && x.Trc == 13 && x.Mtx == 1).ToList();
                var tc = n.Where(x => x.Trc == 16).ToList();
                Check(bc.Count == 1 && (bc[0].Fr & 0x80) == 0,
                      "avifgm ③**核心**：产物底图 colr.full_range = 0x00（继承 ffmpeg 实产；旧代码写 0x80）"
                      + $"｜实 0x{(bc.Count == 1 ? bc[0].Fr : 0xFF):X2}");
                Check(tc.Count == 1 && (tc[0].Fr & 0x80) == 0,
                      $"avifgm ③tmap 替代图 colr 与底图同 range（同一份像素不可能两说）｜实 0x{(tc.Count == 1 ? tc[0].Fr : 0xFF):X2}");
            }
            finally { try { Directory.Delete(work, true); } catch { } }
        }
    }
}
