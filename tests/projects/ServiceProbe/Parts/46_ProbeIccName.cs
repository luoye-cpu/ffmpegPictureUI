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
        private static void ProbeIccName()
        {
            // ── L-3 ICC 结构校验器：10 收 + 3 拒，**逐样本点名原因**（不许只校"拒了 N 个"）──
            //   正样本=本机 lcms 自带测试件（含 spac 类 test3/test4，其 PCS 实测为 'Lab '）；
            //   负样本=lcms 自己的 bad/bad_mpe/toosmall，三个各死于**不同**判据 ⇒ 原因必须逐条对得上。
            {
                var rootL3 = FindRepoRoot();
                if (rootL3 == null) Console.WriteLine("  L-3 SKIP（找不到仓根，不给绿灯）");
                else
                {
                    var dirL3 = System.IO.Path.Combine(rootL3, "tools", "src", "jpegli", "third_party", "lcms");
                    // ⚠ 2026-10-13 修 L-3b 的**路径错**（不是降级）：本清单原先写
                    //   `plugins/test_profiles/test4.icc`，而 vendored lcms 的 `plugins/test_profiles/` 里
                    //   只有 test0/1/2/3/5 —— **根本没有 test4**（实测 `ls` 与下面这条 SKIP 行）。
                    //   仓内实存的那一件是 `testbed/test4.icc`（实测头：class=`spac` data=`RGB` PCS=`Lab`，
                    //   61,020 B），与注释本意「含 spac 类 test3/test4」正是同一族 ⇒ 取它。
                    //   其余 9 件路径逐字未动。
                    string[] acceptL3 = {
                        "plugins/test_profiles/test0.icc", "plugins/test_profiles/test1.icc",
                        "plugins/test_profiles/test2.icc", "plugins/test_profiles/test3.icc",
                        "testbed/test4.icc", "plugins/test_profiles/test5.icc",
                        "testbed/crayons.icc", "testbed/ibm-t61.icc", "testbed/new.icc",
                        "fuzzers/corpus/alltags.icc",
                    };
                    int accOk = 0; int accMissing = 0;
                    foreach (var rel in acceptL3)
                    {
                        var fp = System.IO.Path.Combine(dirL3, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                        if (!System.IO.File.Exists(fp)) { accMissing++; Console.WriteLine($"  L-3 缺正样本 {rel} SKIP"); continue; }
                        var ok = IccProfileService.ValidateIccProfile(fp, out var why);
                        if (ok) accOk++;
                        else Console.WriteLine($"  L-3 正样本被拒 {rel}：{why}");
                    }
                    // 分母是**清单条数**，不是"找得到的条数"：缺件必须显式报出来（原写法 `>=9/10`
                    // 在缺一件时恰好等于 9 ⇒ 缺件被吸收进余量，看板照绿 —— 正是本仓忌的静默降档）。
                    Check(accMissing == 0 && accOk == acceptL3.Length,
                        $"L-3 合法件必须**逐件**收下（清单 {acceptL3.Length}、在位 {acceptL3.Length - accMissing}、"
                      + $"收下 {accOk}）⇒ 收紧校验不许把真 ICC 判成无 ICC，也不许用『缺素材』把分母缩小");

                    // 三条合成负样本 + lcms 自带三条，各点名它应当死在哪一判据上
                    var tmpL3 = System.IO.Path.Combine(rootL3, "tests", "output", "l3neg");
                    System.IO.Directory.CreateDirectory(tmpL3);
                    string W(string name, byte[] b)
                    {
                        var fp = System.IO.Path.Combine(tmpL3, name);
                        System.IO.File.WriteAllBytes(fp, b);
                        return fp;
                    }
                    byte[] GoodBytes()
                    {
                        var fp = System.IO.Path.Combine(dirL3, "plugins", "test_profiles", "test0.icc");
                        return System.IO.File.Exists(fp) ? System.IO.File.ReadAllBytes(fp) : System.Array.Empty<byte>();
                    }
                    void Neg(string label, string fp, string mustContain)
                    {
                        var ok = IccProfileService.ValidateIccProfile(fp, out var why);
                        Check(!ok && (why ?? "").Contains(mustContain, StringComparison.OrdinalIgnoreCase),
                              $"L-3 负样本「{label}」必须被拒且原因含「{mustContain}」（实 ok={ok} reason={why ?? "-"}）");
                    }
                    Neg("128B 全零+acsp", W("zeros128.icc",
                        new byte[128]), "size");
                    var g = GoodBytes();
                    if (g.Length >= 300)
                    {
                        var trunc = new byte[300];
                        System.Array.Copy(g, trunc, 300);
                        Neg("截断 300B", W("trunc300.icc", trunc), "size");
                        var bad = (byte[])g.Clone();
                        bad[0] = 0x7F;   // 头 size 字段改大 ⇒ 声明 > 文件长
                        Neg("头 size 被改坏", W("badsize.icc", bad), "size");
                    }
                    else Console.WriteLine("  L-3 SKIP 两条变形负样本（拿不到 test0.icc 原件）");
                    // lcms 自带坏件：只要求"被拒 + 原因非空"。
                    // ⚠ 曾硬性期望 bad.icc 死在「类别」上，实测它先死在 size（声明 1397245260 > 实际 331）
                    //   ⇒ 判据的先后顺序是我的实现决定的，不该反过来要求样本符合某一格；
                    //   "各死于不同判据"这一事实只由上面三支**合成**样本负责（那里我能控制它先撞哪条）。
                    foreach (var rel in new[] { "testbed/bad.icc", "testbed/bad_mpe.icc", "testbed/toosmall.icc" })
                    {
                        var fp = System.IO.Path.Combine(dirL3, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                        if (!System.IO.File.Exists(fp)) { Console.WriteLine($"  L-3 缺负样本 {rel} SKIP"); continue; }
                        var ok = IccProfileService.ValidateIccProfile(fp, out var why2);
                        Check(!ok && !string.IsNullOrWhiteSpace(why2),
                              $"L-3 lcms 坏件 {rel} 必须被拒且给出原因（实 ok={ok} reason={why2 ?? "-"}）");
                    }
                }
            }
            var expect = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sRGB"] = "sRGB", ["Display P3"] = "Display P3",
                ["ProPhoto RGB"] = "ProPhoto RGB", ["BT.2020"] = "Rec.2020",
                // ❗ Adobe RGB / ColorMatch RGB 的**自身原色在 CICP 里没有 token**（iccgen 只能按 token 生成），
                //   而 registry 对它们的 Out* 就是“归一到 BT.2020”⇒ 这条路径生成出的**本来就是 BT.2020 ICC**。
                //   所以这里必须期望 Rec.2020（期望“Adobe RGB”才是错的，不要为了绿而改断言）。
                //   真正携带 Adobe RGB 原色的 ICC 只能由具名条目/纯托管生成器产出（见下面的专项断言）。
                ["Adobe RGB"] = "Rec.2020", ["ColorMatch RGB"] = "Rec.2020",
            };
            int generated = 0;
            foreach (var (spec, want) in expect)
            {
                var s = ColorSpaceRegistry.Resolve(spec);
                if (s == null) { Check(false, $"{spec}：registry 应能解析（现在解不出）"); continue; }
                var icc = IccProfileService.ResolveNamedIcc(spec, null,
                    s.OutPrimaries ?? "bt709", s.OutTrc ?? "iec61966-2-1", s.OutMatrix ?? "bt709");
                if (string.IsNullOrWhiteSpace(icc) || !File.Exists(icc))
                { Console.WriteLine($"  {spec,-14} SKIP（无法生成命名 ICC，不给绿灯）"); continue; }
                generated++;
                var got = FfmpegGui.Services.ColorMapping.ColorSpaceIdentify.IdentifyIccSpaceName(icc, "RGB built-in");
                Console.WriteLine($"  {spec,-14} 生成 ICC tokens={s.OutPrimaries}/{s.OutTrc} → 识别={got ?? "(null)"}");
                Check(string.Equals(got, want, StringComparison.OrdinalIgnoreCase),
                      $"{spec}：描述被误导时仍按度量认出 {want}（实得 {got ?? "null"}）");
                IccProfileService.TryDeleteIcc(icc);
            }
            Check(generated >= 4, $"至少 4 个标准空间可被生成并度量识别（实 {generated}）");
            // 兜底与“绝不猜”：无 ICC 时描述才当线索；两者都无 ⇒ null
            Check(FfmpegGui.Services.ColorMapping.ColorSpaceIdentify.IdentifyIccSpaceName(null, "Display P3") == "Display P3",
                "无 ICC 时仍按描述兜底（保住相机 JPEG 这类只给描述的场景）");
            Check(FfmpegGui.Services.ColorMapping.ColorSpaceIdentify.IdentifyIccSpaceName(null, null) == null,
                "既无 ICC 又无可读描述 ⇒ null（绝不猜成 sRGB）");

            // 专项：具名空间自己的 ICC（纯托管生成，如 ProPhoto 保真 ICC）必须能按色度认回本名，
            // 而不被“内置描述/无关描述”带偏——这才是“保真携带”路径赖以成立的根据。
            {
                var pIcc = FfmpegGui.Services.IccProfileService.GetFaithfulIcc("ProPhoto RGB");
                if (string.IsNullOrWhiteSpace(pIcc) || !File.Exists(pIcc))
                { Console.WriteLine("  ProPhoto 保真 ICC  SKIP（生成不可用，不给绿灯）"); }
                else
                {
                    var g = FfmpegGui.Services.ColorMapping.ColorSpaceIdentify.IdentifyIccSpaceName(pIcc, "Whatever weird name");
                    Console.WriteLine($"  ProPhoto 保真 ICC → 识别={g ?? "(null)"}（描述被故意写成一个无意义名字）");
                    Check(string.Equals(g, "ProPhoto RGB", StringComparison.OrdinalIgnoreCase),
                          $"保真 ICC 能被色度认回 ProPhoto RGB（实得 {g ?? "null"}）");
                }
            }
        }

    }
}
