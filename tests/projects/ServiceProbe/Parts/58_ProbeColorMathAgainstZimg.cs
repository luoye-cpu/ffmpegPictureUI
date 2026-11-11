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
        /// colormath —— 把本仓 CICP 原色表推导出的线性矩阵，与**外部参照实现 zimg** 逐元素对撞。
        /// 参照值来源：本机 `C:\PLAN\VQBench\PLAN\ffmpeg-full\ffmpeg.exe`（git-2026-08-06，含 zscale/zimg）
        /// 实测于 2026-09-24：`-f rawvideo -pix_fmt gbrpf32le -video_size 1x1`（喂单位基向量）→
        /// `zscale=pin=&lt;T&gt;:tin=linear:min=0:p=bt709:t=linear:m=0,format=gbrpf32le` → 读回 12 字节浮点。
        /// ⚠ 两点口径：① **必须走 float**，16-bit 出口会把 BT.2020/P3→709 的负值与 &gt;1 削平，
        ///    先前用 8-bit `color=` 源 + `rgb48le` 出口测出的"各 token 都是单位阵"就是那个假象；
        /// ② gbrp 平面序是 **G,B,R**，下面每行已按 (R,G,B) 三列重排。
        /// 控制组：pin=1(bt709)→bt709 必须是精确单位阵；pin=9(bt2020) 必须等于 ITU BT.2080 发布值
        /// （也与本仓 `ProbeMatrix` 的期望常量一致）⇒ 参照本身可信。
        /// </summary>
        private static void ProbeColorMathAgainstZimg()
        {
            // ── L-10 F-10 的兑现锁（此前 17 条门禁观测不到源曲线维，全靠这几条）──
            {
                var b601 = FfmpegGui.Services.ColorMapping.TransferCurve.Bt601;
                // TransferCurve 是 readonly struct ⇒ 不能与 null 比；存在性由类型系统本身保证
                {
                    Check(Math.Abs(b601.G - 1.0 / 0.45) < 1e-12, $"L-10 γ 必须是 1/0.45（BT.601-7 定义式；实 {b601.G}）");
                    Check(Math.Abs(b601.C - 1.0 / 4.5) < 1e-12, $"L-10 趾段斜率必须倒数为 4.5（实 1/C={1.0 / b601.C}）");
                    // dng_color_space.h:134-140 的 β 是**线性域**拐点，本仓 D 是**编码域**拐点 ⇒ d/4.5 才可比
                    Check(Math.Abs(b601.D / 4.5 - 0.0180539685108078) < 2e-6,
                          $"L-10 编码域拐点 D={b601.D} ⇒ 线性域 {b601.D / 4.5:0.0000000000} 必须贴 DNG SDK 的 β=0.0180539685108078");
                    Check(b601.CicpToken == "smpte170m", $"L-10 曲线自带的 CICP 名必须是 smpte170m（实 {b601.CicpToken ?? "null"}）⇒ 不许图省事写成 bt709 洗掉源语义");
                    // ExactTransferName 吃的是**友好空间名**（"BT.2100 PQ" 这种），
                    // 拿 trc token "smpte170m" 去叫它必然 null ⇒ 那不是缺陷，是我这条锁问错了对象。
                    // 170M **源**的命名可达性改由曲线侧正查承担，端到端那一格标为未判定（需 wiring 级判据）。
                    var viaToken = FfmpegGui.Services.ColorMapping.TransferCurve.ForCicpTransfer("6");
                    Check(viaToken != null && viaToken.Value.CicpToken == "smpte170m",
                          $"L-10 CICP 码 6 必须解析到 170M 曲线且 CICP 名不回退（实 {viaToken?.CicpToken ?? "null"}）");
                    Console.WriteLine("  L-10 未判定：170M 源的 -color_trc 端到端读回（ExactTransferName 只吃空间名 ⇒ 需 wiring 判据）");
                    Check(ColorSpaceRegistry.ExactTransferName("Adobe RGB") == "bt470m",
                          "L-10 反向哨兵：Adobe RGB 仍 = γ2.2 一族，不许被 170M 的改动带跑");
                    // 往返闭合（拆式自己跟自己可逆）
                    double worst = 0;
                    for (int i = 1; i < 65536; i += 511)
                    {
                        double y = i / 65535.0, lin = b601.ToLinear(y), back = b601.FromLinear(lin);
                        worst = Math.Max(worst, Math.Abs(back - y));
                    }
                    Check(worst < 1e-6, $"L-10 分段式往返闭合最大偏差={worst:0.0000000000}（趾段/幂段衔接处）");
                }
            }
            // token → 9 项行主序（与 LinearMatrixBetweenPrimaries 同形：out[r] = Σ m[r*3+k]·in[k]）
            //   ⚠ 每个数组都过了一道独立校验：**三行之和必须各为 1**（白→白，与白点无关）——
            //     第一版这里有 4 个 token 因为平面序重排写错，正是这条校验逐个抓出来的。
            var zimg = new (string Tok, double[] M)[]
            {
                ("bt709",     new[] { 1.0, 0.0, 0.0,  0.0, 1.0, 0.0,  0.0, 0.0, 1.0 }),
                ("bt470m",    new[] { 1.48616, -0.40355, -0.08260,  -0.02510, 0.95402, 0.07108,  -0.02722, -0.04410, 1.07132 }),
                ("bt470bg",   new[] { 1.04404, -0.04404, 0.0,  0.0, 1.0, 0.0,  0.0, 0.01179, 0.98821 }),
                ("smpte170m", new[] { 0.93954, 0.05018, 0.01028,  0.01777, 0.96579, 0.01643,  -0.00162, -0.00437, 1.00599 }),
                ("smpte240m", new[] { 0.93954, 0.05018, 0.01028,  0.01777, 0.96579, 0.01643,  -0.00162, -0.00437, 1.00599 }),
                ("film",      new[] { 1.34618, -0.33920, -0.00698,  -0.04735, 1.06605, -0.01870,  -0.02166, -0.06131, 1.08298 }),
                ("bt2020",    new[] { 1.66049, -0.58764, -0.07285,  -0.12455, 1.13290, -0.00835,  -0.01815, -0.10058, 1.11873 }),
                ("smpte428",  new[] { 3.14666, -1.66646, -0.48019,  -0.99552, 1.95576, 0.03976,  0.06359, -0.21456, 1.15097 }),
                ("smpte431",  new[] { 1.15752, -0.15496, -0.00255,  -0.04150, 1.04557, -0.00407,  -0.01805, -0.07858, 1.09663 }),
                ("smpte432",  new[] { 1.22494, -0.22494, 0.0,  -0.04206, 1.04206, 0.0,  -0.01964, -0.07864, 1.09827 }),
                ("jedec-p22", new[] { 1.02525, -0.02655, 0.00130,  0.01939, 0.94803, 0.03258,  -0.00177, -0.00144, 1.00321 }),
            };
            // ── 容差**分档**（P4 收口：原先一个 1e-3 把两种性质完全不同的上限混在一起）──
            //   实测残差（2026-09-26，本 mode 自己打出来的 maxErr，不是估的）：
            //     bt2020 1.14e-6 · bt470bg 3.38e-6 · smpte432 3.95e-6 · smpte170m 4.91e-6
            //     · smpte240m 4.91e-6 · jedec-p22 4.92e-6  ⇒ **A 档**
            //     bt470m 1.73e-4                                    ⇒ **B 档**（唯一残留）
            //   A 档 = 1e-5：锚 A（zimg）的读数只有 5 位小数 ⇒ 单元素舍入地板 5e-6，
            //     实测最大 4.92e-6 正贴着它 ⇒ 再严就是在抄 zimg 的舍入，而不是在验本仓的数学。
            //   B 档 = 5e-4：**只留给 `bt470m`**。它的残差确实来自"白点只给到有限位数"
            //     （Illuminant C 0.3101,0.3162 ⇒ 色适应对白点位数远比原色敏感，残差落在对角元 1,1）。
            //   ⚠ 2026-10-11 更正：本档原先还收着 `smpte431`（2.36e-4），当时的理由写的也是"白点位数"——
            //     **那句是假的**。归因实验：白点常数一个不动、只把本仓锥矩阵末项从转写错的 1.0633
            //     改回 lcms `cmswtpnt.c` 的 1.0296，该格残差 2.36e-4 → 4.03e-6 ⇒ 它是被常数错撑起来的，
            //     不是精度上限。⇒ `smpte431` 已收到 A 档（下表），B 档不许再拿"白点位数"吸收别的格子。
            //   ⚠ 没登记进本表的 token ⇒ 判红并点名"不知道该用哪档"：默认走宽松档就是这条锁漏齿的方式。
            var tolTier = new System.Collections.Generic.Dictionary<string, (double Tol, string Why)>
            {
                ["bt2020"] = (1e-5, "A 档=锚读数舍入地板 5e-6"),
                ["bt470bg"] = (1e-5, "A 档=锚读数舍入地板 5e-6"),
                ["smpte432"] = (1e-5, "A 档=锚读数舍入地板 5e-6"),
                ["smpte170m"] = (1e-5, "A 档=锚读数舍入地板 5e-6"),
                ["smpte240m"] = (1e-5, "A 档=锚读数舍入地板 5e-6"),
                ["jedec-p22"] = (1e-5, "A 档=锚读数舍入地板 5e-6"),
                ["bt470m"] = (5e-4, "B 档=白点位数上限（Illuminant C）"),
                ["smpte431"] = (1e-5, "A 档=锚读数舍入地板 5e-6（旧 B 档 5e-4 的归因是错的：该格残差来自本仓锥矩阵末项被转写成 1.0633，2026-10-11 改回 lcms 的 1.0296 后实测 4.03e-6）"),
            };

            foreach (var (tok, expect) in zimg)
            {
                var got = ColorSpaceRegistry.LinearMatrixBetweenPrimaries(tok, "bt709");
                if (tok == "bt709")
                {
                    // 同 token 的契约就是 null（=不映射）；把它当"缺矩阵"判红是我第一版的错向断言。
                    Check(got == null, "bt709→bt709 必须 null（同原色 = 不映射，绝不生成单位阵去乘一遍）");
                    continue;
                }
                if (got == null)
                {
                    // null 只有在「本仓**故意**不收录」时才正确，且必须逐个点名理由：
                    //   film     = 未收录（lcms 有原色但残差 2.1e-4，精度不足以填表）
                    //   smpte428 = **xy 参数化表达不了**（H.273 蓝原色 y=0 ⇒ 归一化退化）⇒ 只能不收录
                    // 其余 token 拿到 null 一律红：那说明表被删漏了，而不是"设计如此"。
                    var whyNull = new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["film"] = "未收录（精度不足）",
                        ["smpte428"] = "xy 不可表达 ⇒ 故意不收录，不许挑一组「看着像」的补回来",
                    };
                    Check(whyNull.ContainsKey(tok),
                        $"{tok}→bt709 返回 null ⇒ 只允许出现在「故意不收录」名单里（名单与理由：film=精度不足、smpte428=xy 表达不了）");
                    continue;
                }
                double err = 0; int worst = -1;
                for (int i = 0; i < 9; i++)
                {
                    double d = Math.Abs(got[i] - expect[i]);
                    if (d > err) { err = d; worst = i; }
                }
                Console.WriteLine($"  {tok,-11} maxErr={err:E2} (位置 {worst / 3},{worst % 3}) " +
                    $"本仓=[{string.Join(",", got.Select(v => v.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)))}] " +
                    $"zimg=[{string.Join(",", expect.Select(v => v.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)))}]");
                if (!tolTier.TryGetValue(tok, out var tier))
                {
                    // 新加 token 时**必须**同时登记档位与依据 ⇒ 否则"这条到底该多严"会变成隐含默认值。
                    Check(false, $"{tok}→bt709 未登记容差档位 ⇒ 不判（实测 maxErr={err:E2}；请补 tolTier 与依据）");
                    continue;
                }
                Check(err <= tier.Tol,
                    $"{tok}→bt709 与 zimg 参照逐元素一致（≤{tier.Tol:0.######}，{tier.Why}）实差 {err:E2}");
            }

            // 表内重复项的直接后果：名义上不同的标准被判"同原色"⇒ 跳映射（用户明令禁止的那类静默）
            var dup = new System.Collections.Generic.List<string>();
            for (int i = 0; i < zimg.Length; i++)
                for (int j = i + 1; j < zimg.Length; j++)
                {
                    var a = ColorSpaceRegistry.LinearMatrixBetweenPrimaries(zimg[i].Tok, "bt709");
                    var b = ColorSpaceRegistry.LinearMatrixBetweenPrimaries(zimg[j].Tok, "bt709");
                    if (a == null || b == null) continue;
                    bool same = true;
                    for (int k = 0; k < 9; k++) if (Math.Abs(a[k] - b[k]) > 1e-9) { same = false; break; }
                    if (same) dup.Add($"{zimg[i].Tok}≡{zimg[j].Tok}");
                }
            Console.WriteLine("  本仓把不同标准算成**同一矩阵**的 token 对：" + (dup.Count == 0 ? "无" : string.Join(" ", dup)));
            // 重复**不是自动违法**：zimg 自己也让 smpte170m 与 smpte240m 同原色（差异只在传递函数），
            // 所以这里要的是「白名单之外不得重复」——白名单每一对都必须有外部锚的理由，
            // 加对进白名单等同于改判据，必须写清楚是谁、凭什么加进来的（别用"看着一样"当理由）。
            var allowedDup = new System.Collections.Generic.Dictionary<string, string>
            {
                ["smpte170m≡smpte240m"] = "两锚同判：lcms chrm 与 zimg 矩阵都给出同一组原色，差异只在 trc/matrix",
            };
            var badDup = dup.Where(x => !allowedDup.ContainsKey(x)).ToList();
            Check(dup.Count > 0 && badDup.Count == 0,
                $"同矩阵对全部落在白名单内（实得 {dup.Count} 对，白名单外 {badDup.Count} 对：{string.Join(" ", badDup)}）");
        }

    }
}
