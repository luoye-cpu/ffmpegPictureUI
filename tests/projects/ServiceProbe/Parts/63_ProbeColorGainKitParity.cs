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
        /// 跨仓同进程对撞（2026-10-22，P3）：宿主实现 vs 独立库 `ColorGainKit`。
        /// 为什么必须存在：两仓各自报"全绿"（宿主侧各闸、库侧 `ColorMatrix.SelfTest`）**互不证明同一件事**。
        ///   2026-10-22 首次对撞即抓到 <c>SimdPixelOps.ComputeGainMapGray</c> 在 140 格里 11 格不一致、
        ///   最大差 <b>133/255</b>（库侧停在 F-5 之前的 `eps = 0.001f` 分母地板 ⇒ 暗底图把增益顶满），
        ///   而这条分歧**从来不在任何一本账里**（库自检此前对该函数 0 条用例）。
        /// ⚠ 只做**标量 API** 的反射对撞：`Span&lt;T&gt;` 参数无法经 <c>MethodInfo.Invoke</c> 传递。
        ///   覆盖面 = <c>Log2Gain</c> / <c>LogGainToByteScalar</c> / <c>SegmentedReinhardScalar</c> /
        ///   <c>FloatToSrgb8Scalar</c> / <c>TransferCurve.ForCicpTransfer</c> + 曲线取值 /
        ///   <c>IsoBmffGainMapProbe.Probe(byte[])</c>。整幅灰度函数由前两者复合而成，其网格判据在库侧自检里。
        /// ⚠ 库缺席必须 <c>Skip</c> 点名（不给绿灯）—— 本仓"无料可量打 SKIP"的既有口径。
        /// 判据口径：两实现都是同一 float 同式 ⇒ **逐码全等、容差 0**；另有一条反控证明比较器有牙。
        /// </summary>
        private static void ProbeColorGainKitParity()
        {
            var rootCgk = Environment.GetEnvironmentVariable("COLORGAINKIT_ROOT");
            if (string.IsNullOrWhiteSpace(rootCgk)) rootCgk = @"C:\PLAN\ColorGainKit";
            var cmDll = System.IO.Path.Combine(rootCgk, @"src\ColorMatrix\bin\Release\net11.0\ColorMatrix.dll");
            var gmDll = System.IO.Path.Combine(rootCgk, @"src\GainMapKit\bin\Release\net11.0\GainMapKit.dll");
            Console.WriteLine($"[cgkparity] ColorGainKit={rootCgk} ColorMatrix在盘={System.IO.File.Exists(cmDll)} GainMapKit在盘={System.IO.File.Exists(gmDll)}");
            if (!System.IO.File.Exists(cmDll) || !System.IO.File.Exists(gmDll))
            {
                Skip($"cgkparity 前置：本机没有 ColorGainKit 的出货 DLL（{cmDll} / {gmDll}）⇒ 跨仓等价本轮**未判定**（不记绿）");
                return;
            }
            var asmCM = System.Reflection.Assembly.LoadFrom(cmDll);
            var asmGM = System.Reflection.Assembly.LoadFrom(gmDll);
            var libSimd = asmCM.GetType("ColorMatrix.SimdPixelOps", true);
            var libTc = asmCM.GetType("ColorMatrix.TransferCurve", true);
            var libProbe = asmGM.GetType("GainMapKit.IsoBmffGainMapProbe", true);
            var hostSimd = typeof(FfmpegGui.Services.SimdPixelOps);
            var hostTc = typeof(FfmpegGui.Services.ColorMapping.TransferCurve);
            var hostProbe = typeof(FfmpegGui.Services.IsoBmffGainMapProbe);

            static System.Reflection.MethodInfo Mx(Type? t, string name, int argc)
            {
                if (t is null) throw new TypeLoadException($"类型缺席 ⇒ 无法反射 {name}/{argc}");
                foreach (var mi in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                    if (mi.Name == name && mi.GetParameters().Length == argc && !mi.IsGenericMethod) return mi;
                throw new MissingMethodException($"{t.FullName}.{name}/{argc}");
            }
            static float FF(System.Reflection.MethodInfo mi, params object[] args) => (float)mi.Invoke(null, args)!;
            static byte BB(System.Reflection.MethodInfo mi, params object[] args) => (byte)mi.Invoke(null, args)!;

            // ── 1/2 Log2Gain：F-5 的前向唯一定义式，单侧地板与允许衰减各一格 ──
            var hLog2 = Mx(hostSimd, "Log2Gain", 5);
            var lLog2 = Mx(libSimd, "Log2Gain", 5);
            float[] hsG = { 0f, 0.001f, 0.01f, 0.03f, 0.2f, 1f, 4f, 100f };
            float[] ssG = { -0.5f, -0.02f, 0f, 1e-6f, 0.001f, 0.01f, 0.05f, 0.2f, 1f };
            const float Off = 1f / 64f;   // ISO 21496-1 baseOffset/alternateOffset（两侧同值，与 GainMapEncoder.KOffset* 同源）
            foreach (var floorAtOne in new[] { true, false })
            {
                int bad = 0; string first = "";
                foreach (var h in hsG) foreach (var s in ssG)
                {
                    float a = FF(hLog2, h, s, Off, Off, floorAtOne);
                    float b = FF(lLog2, h, s, Off, Off, floorAtOne);
                    if (a != b) { bad++; if (first == "") first = $" (h={h},s={s}) host={a:0.####} lib={b:0.####}"; }
                }
                Check(bad == 0, $"cgkparity Log2Gain(floorAtOne={floorAtOne})：{hsG.Length * ssG.Length} 格与库**逐码全等**（{bad} 格不等{first}）");
            }

            // ── 3 量化：log2(gain) → 8-bit，含双侧量程 ──
            var hQ = Mx(hostSimd, "LogGainToByteScalar", 3);
            var lQ = Mx(libSimd, "LogGainToByteScalar", 3);
            int qBad = 0; string qFirst = "";
            foreach (float mx in new[] { 0.56f, 1.3f, 2.0f })
                foreach (float mn in new[] { 0f, -4.111f })
                    for (double lg = -8; lg <= 8.0001; lg += 0.05)
                    {
                        byte a = BB(hQ, (float)lg, mx, mn); byte b = BB(lQ, (float)lg, mx, mn);
                        if (a != b) { qBad++; if (qFirst == "") qFirst = $" (lg={lg:F2},max={mx},min={mn}) host={a} lib={b}"; }
                    }
            Check(qBad == 0, $"cgkparity LogGainToByteScalar：3×2×321 点逐码全等（{qBad} 点不等{qFirst}）");

            // ── 4 Reinhard 分段 / 5 sRGB 编码标量 ──
            var hR = Mx(hostSimd, "SegmentedReinhardScalar", 2);
            var lR = Mx(libSimd, "SegmentedReinhardScalar", 2);
            int rBad = 0;
            foreach (float hr in new[] { 1.5f, 4f, 1000f / 203f })
                for (double y = 0; y <= 1000.0; y += 0.5)
                    if (FF(hR, (float)y, hr) != FF(lR, (float)y, hr)) rBad++;
            Check(rBad == 0, $"cgkparity SegmentedReinhardScalar：3×2001 点逐码全等（{rBad} 点不等）");

            var hS8 = Mx(hostSimd, "FloatToSrgb8Scalar", 1);
            var lS8 = Mx(libSimd, "FloatToSrgb8Scalar", 1);
            int sBad = 0; string sFirst = "";
            for (int i = -200; i <= 1200; i++)
            {
                float v = i / 1000f;
                byte a = BB(hS8, v); byte b = BB(lS8, v);
                if (a != b) { sBad++; if (sFirst == "") sFirst = $" (v={v}) host={a} lib={b}"; }
            }
            Check(sBad == 0, $"cgkparity FloatToSrgb8Scalar：1401 点（含负值与 >1.0）逐码全等（{sBad} 点不等{sFirst}）");

            // ── 6 CICP token → 曲线：映射集合 + 取值网格 ──
            var hTcM = Mx(hostTc, "ForCicpTransfer", 1);
            var lTcM = Mx(libTc, "ForCicpTransfer", 1);
            string?[] tokens = { "bt709", "smpte170m", "bt470bg", "iec61966-2-1", "smpte2084", "arib-std-b67",
                "linear", "gamma2.2", "gamma2.8", "bt1361", "smpte240m", "gbr", "unknown", "", null };
            int nullBad = 0; double curveWorst = 0; string cFirst = "";
            foreach (var tk in tokens)
            {
                object? A = hTcM.Invoke(null, new object?[] { tk });
                object? B = lTcM.Invoke(null, new object?[] { tk });
                if ((A == null) != (B == null)) { nullBad++; cFirst += $"[{tk ?? "null"}: host={(A == null ? "null" : "有")} lib={(B == null ? "null" : "有")}] "; continue; }
                if (A == null || B == null) continue;
                var toA = A.GetType().GetMethod("ToLinear")!; var toB = B.GetType().GetMethod("ToLinear")!;
                var frA = A.GetType().GetMethod("FromLinear")!; var frB = B.GetType().GetMethod("FromLinear")!;
                for (int i = 0; i <= 32; i++)
                {
                    double x = i / 32.0;
                    double d1 = Math.Abs((double)toA.Invoke(A, new object[] { x })! - (double)toB.Invoke(B, new object[] { x })!);
                    double d2 = Math.Abs((double)frA.Invoke(A, new object[] { x })! - (double)frB.Invoke(B, new object[] { x })!);
                    if (Math.Max(d1, d2) > curveWorst) { curveWorst = Math.Max(d1, d2); if (d1 > 0 || d2 > 0) cFirst += $"[{tk}@{x:F2} 差={Math.Max(d1, d2):E2}] "; }
                }
            }
            Check(nullBad == 0, $"cgkparity ForCicpTransfer 映射集合：{tokens.Length} 个 token 的空/非空一一对应（{nullBad} 个不一致 {cFirst}）");
            Check(curveWorst <= 1e-12, $"cgkparity 曲线取值网格：33 点 × 命中 token，最大差 {curveWorst:E3}（容差 1e-12）");

            // ── 7 容器读手：真实 libavif 增益图件 + 两种截断，逐字段含载荷字节 ──
            var hPrM = Mx(hostProbe, "Probe", 1);
            var lPrM = Mx(libProbe, "Probe", 1);
            var fxDir = System.IO.Path.Combine(rootCgk, "fixtures");
            var fieldNames = new[] { "IsIsoBmff", "Determinate", "FailReason", "HasGainMap", "Form", "PrimaryItemId",
                "TmapItemId", "GainMapItemId", "GainMapCodec", "GainMapFfmpegFormat", "Brands", "TmapPayload", "GainMapItemData",
                // ── grid 承载（2026-11-13 增）────────────────────────────────────────────────
                // ⚠ 为什么必须比：库侧此前**只认 codec item**，`grid` 派生项一律落到「候选数=0」的 fail-closed
                //   分支，而 `GainMapItemId` / `GainMapCodec` 在那条分支里是 -1 / null ⇒ 光比上面 13 个字段
                //   也能「全等」（两侧同为失败态），**抓不到移植是否真的产出了瓦片**。
                //   ⇒ 这 8 项是 grid 移植的**唯一**证据面，缺一项就等于该维度未判定。
                "GainMapIsGrid", "GridRows", "GridCols", "GridWidth", "GridHeight", "TileWidth", "TileHeight",
                "GainMapTiles" };
            // ⚠ 两侧字段面必须**先核对再比**（2026-11-13）：库侧是**外仓 DLL**，可能是旧版（无 grid 字段）。
            //   旧实现直接 `GetField(fn)!` + `GetValue` ⇒ 旧 DLL 触发 NullReferenceException，
            //   报出来是一句无名字的 "exception: NullReferenceException"，且**后续全部判据不再执行**
            //   （实测：变异态 pass=7 fail=1，曲线/容器段的读数整段丢失）。⇒ fail-closed 但**点名**。
            // ⚠ 字段在**嵌套的 `Result` 类**上，不在探测器类上 ⇒ 取 `Probe` 的返回类型（不能拿 *Probe 类型去 GetField）。
            var hostResultType = hPrM.ReturnType;
            var libResultType = lPrM.ReturnType;
            var missingFields = new System.Collections.Generic.List<string>();
            foreach (var fn in fieldNames)
            {
                if (hostResultType.GetField(fn) is null) missingFields.Add($"宿主缺 {fn}");
                if (libResultType.GetField(fn) is null) missingFields.Add($"库缺 {fn}");
            }
            Check(missingFields.Count == 0,
                $"cgkparity 字段面齐备（{fieldNames.Length} 项）：宿主与库同名同型可反射" +
                $"（缺失 {(missingFields.Count == 0 ? "无" : string.Join("; ", missingFields))}）" +
                "；库侧缺项 ⇒ 该库版本落后于宿主（先重编 ColorGainKit 再判，勿按「等价」读）");
            int cmp = 0, fBad = 0; string fFirst = "";
            if (System.IO.Directory.Exists(fxDir) && missingFields.Count == 0)
            {
                foreach (var fp in System.IO.Directory.GetFiles(fxDir).Where(f => f.EndsWith(".avif") || f.EndsWith(".jpg")).OrderBy(f => f, StringComparer.Ordinal))
                {
                    var bytes = System.IO.File.ReadAllBytes(fp);
                    foreach (var cut in new[] { bytes.Length, Math.Max(1, bytes.Length / 2), Math.Min(4096, bytes.Length) })
                    {
                        var sub = new byte[cut]; Array.Copy(bytes, sub, cut);
                        var ra = hPrM.Invoke(null, new object[] { sub })!;
                        var rb = lPrM.Invoke(null, new object[] { sub })!;
                        cmp++;
                        foreach (var fn in fieldNames)
                        {
                            var fa = ra.GetType().GetField(fn);
                            var fb = rb.GetType().GetField(fn);
                            if (fa is null || fb is null) continue;      // 已在上面点名，不在此处炸空引用
                            var va = fa.GetValue(ra);
                            var vb = fb.GetValue(rb);
                            // `GainMapTiles` 是 `List<byte[]>`：`Equals` 走引用比较 ⇒ 恒不等，必须逐块比字节。
                            bool eq = (va, vb) switch
                            {
                                (byte[] pa, byte[] pb) => pa.AsSpan().SequenceEqual(pb),
                                (null, null) => true,
                                (System.Collections.Generic.List<byte[]> la, System.Collections.Generic.List<byte[]> lb) =>
                                    la.Count == lb.Count && la.Zip(lb).All(t => t.First.AsSpan().SequenceEqual(t.Second)),
                                _ => Equals(va, vb)
                            };
                            if (!eq)
                            {
                                fBad++;
                                if (fFirst.Length < 260)
                                {
                                    string sa = va is byte[] ? pa2(va) : (va?.ToString() ?? "null");
                                    string sb2 = vb is byte[] ? pa2(vb) : (vb?.ToString() ?? "null");
                                    fFirst += $"{System.IO.Path.GetFileName(fp)}/{cut}:{fn} host={sa} lib={sb2} ";
                                }
                            }
                        }
                    }
                }
            }
            // 字段面缺 ⇒ 只由上面那条负责报红（否则 cmp 因为跳过比较而恒 0，会在「扫描面非空」上再报一次假红）。
            if (missingFields.Count == 0)
            {
                Check(cmp >= 12, $"cgkparity 容器对撞的**扫描面非空**（正控：实比对 {cmp} 个「件 × 截断变体」，须 ≥12）");
                Check(fBad == 0, $"cgkparity IsoBmffGainMapProbe 逐字段（{fieldNames.Length} 项：含 tmap 载荷 / 增益图 item 字节 / grid 瓦片与网格尺寸）：{cmp} 变体全等（{fBad} 处不一致 {fFirst}）");
            }

            // ── 7b grid 移植的**正控**（2026-11-13）────────────────────────────────────────
            // 上面那条「全等」在两个实现**都失败**时同样成立（同为 GainMapItemId=-1 / Tiles=null）⇒
            // 必须有独立一条证明「grid 承载体确实被解开了」，否则库侧退回只认 codec item 也照样全绿。
            // 判据落在**宿主**读数上（宿主是权威副本，其 grid 能力由 ISO-BMFF 结构决定，不随库变化）。
            int gridSeen = 0, gridTiles = 0, gridDims = 0;
            if (System.IO.Directory.Exists(fxDir) && missingFields.Count == 0)
            {
                foreach (var fp in System.IO.Directory.GetFiles(fxDir).Where(f => f.EndsWith(".avif") || f.EndsWith(".jpg")).OrderBy(f => f, StringComparer.Ordinal))
                {
                    var bytes = System.IO.File.ReadAllBytes(fp);
                    var ra = hPrM.Invoke(null, new object[] { bytes })!;
                    if ((bool)ra.GetType().GetField("GainMapIsGrid")!.GetValue(ra)!)
                    {
                        gridSeen++;
                        var tl = (System.Collections.Generic.List<byte[]>?)ra.GetType().GetField("GainMapTiles")!.GetValue(ra);
                        int rows = (int)ra.GetType().GetField("GridRows")!.GetValue(ra)!;
                        int cols = (int)ra.GetType().GetField("GridCols")!.GetValue(ra)!;
                        int gw = (int)ra.GetType().GetField("GridWidth")!.GetValue(ra)!;
                        int gh = (int)ra.GetType().GetField("GridHeight")!.GetValue(ra)!;
                        if (tl != null && tl.Count == rows * cols) gridTiles++;
                        if (gw > 0 && gh > 0) gridDims++;
                    }
                }
            }
            // 字段面已缺（库版本落后）⇒ 本段无从判定，由上面那条「字段面齐备」负责报红，此处不叠加噪声。
            if (missingFields.Count == 0)
            {
                Check(gridSeen >= 1, $"cgkparity grid 正控：夹具里至少 1 个件的增益图走 grid 承载（实测 {gridSeen} 个）—— 0 个 ⇒ 上面那条「全等」对 grid 移植**无判定力**");
                Check(gridSeen == 0 || gridTiles == gridSeen,
                    $"cgkparity grid 正控：每个 grid 件的瓦片数 == rows×cols（{gridTiles}/{gridSeen} 件满足）");
                Check(gridSeen == 0 || gridDims == gridSeen,
                    $"cgkparity grid 正控：每个 grid 件的整幅尺寸 > 0（{gridDims}/{gridSeen} 件满足）");
            }

            // ── 8 反控：比较器必须有牙——把 F-5 之前的 eps 式另算一遍，必须与宿主**不等** ──
            double epsOld = Math.Log2(Math.Max(0.01f / Math.Max(0f, 0.001f), 1.0));
            float hostNew = FF(hLog2, 0.01f, 0f, Off, Off, true);
            Check(Math.Abs(epsOld - hostNew) > 1.0,
                $"cgkparity 反控：旧 eps 式在 (h=0.01,s=0) 给 {epsOld:F3}、宿主给 {hostNew:F3} ⇒ 二者必须**不等**（相等就说明本对撞是空转）");

            static string pa2(object v) => v switch
            {
                byte[] bb2 => $"[{bb2.Length}B]",
                System.Collections.Generic.List<byte[]> ls => $"[{ls.Count}块:{string.Join(",", ls.Select(x => x.Length + "B"))}]",
                _ => v?.ToString() ?? "null"
            };
        }

    }
}
