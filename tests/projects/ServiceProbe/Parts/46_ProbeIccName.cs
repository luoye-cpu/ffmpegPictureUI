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
using Sato = FfmpegGui.Services.ColorMapping.AvifSampleTransform;
using OM = FfmpegGui.Services.ColorMapping.OutMatrixCapability;
using BP = System.Buffers.Binary.BinaryPrimitives;
using IccIdentity = FfmpegGui.Services.ColorMapping.IccProfileIdentity;

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
            // A display name alone cannot establish primaries or transfer when there are no profile bytes.
            Check(FfmpegGui.Services.ColorMapping.ColorSpaceIdentify.IdentifyIccSpaceName(null, "Display P3") == null,
                "没有 ICC 字节时，不拿描述文字冒充 P3 度量结论");
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

            // AVIF sato v0 signed 32-bit arithmetic. These are fixed RPN conformance vectors,
            // not a second evaluator; call the product parser/evaluator for each vector.
            {
                var sample2 = Sato.Parse([0x02, 0x01, 0x02], out var identityError);
                Check(sample2 is { ArithmeticBitDepth: 32, RequiredInputCount: 2, TokenCount: 1 }
                      && identityError == null && sample2.Evaluate(1023, 4095) == 4095,
                    "sato code 2 is signed arithmetic width, not decoded sample width; sample 2 selects second input");

                (string Name, byte[] Payload, long A, long B, long Expected)[] valid =
                [
                    ("BSR non-power-of-two", [0x02, 0x02, 0x01, 0x43], 61, 0, 5),
                    ("BSR zero", [0x02, 0x02, 0x01, 0x43], 0, 0, 0),
                    ("BSR signed negative intermediate", [0x02, 0x04, 0x01, 0x02, 0x81, 0x43], 1, 2, 0),
                    ("BSR INT32_MAX", [0x02, 0x02, 0x00, 0x7f, 0xff, 0xff, 0xff, 0x43], 0, 0, 30),
                    ("signed constant + 1 remains negative", [0x02, 0x03, 0x00, 0xff, 0xff, 0xff, 0xfd, 0x01, 0x80], 1, 0, -2),
                    ("NOT is not boolean", [0x02, 0x02, 0x01, 0x42], 3, 0, -4),
                    ("INT32_MIN negation saturates", [0x02, 0x02, 0x00, 0x80, 0x00, 0x00, 0x00, 0x40], 0, 0, int.MaxValue),
                    ("INT32_MIN abs saturates", [0x02, 0x02, 0x00, 0x80, 0x00, 0x00, 0x00, 0x41], 0, 0, int.MaxValue),
                    ("INT32_MIN / -1 saturates", [0x02, 0x03, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0xff, 0xff, 0xff, 0xff, 0x83], 0, 0, int.MaxValue),
                    ("divide by zero preserves numerator", [0x02, 0x03, 0x01, 0x02, 0x83], 117, 0, 117),
                    ("product clamps before subsequent subtraction", [0x02, 0x05, 0x00, 0x7f, 0xff, 0xff, 0xff, 0x01, 0x82, 0x00, 0x7f, 0xff, 0xff, 0xff, 0x81], 2, 0, 0),
                    ("fast multiply clamps before a positive add", [0x02, 0x05, 0x00, 0x80, 0x00, 0x00, 0x00, 0x01, 0x82, 0x02, 0x80], 2, 4095, -2147479553L),
                    ("fast quotient by zero preserves input 2", [0x02, 0x07, 0x00, 0x00, 0x00, 0x00, 0x10, 0x01, 0x82, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x83, 0x80], 7, 13, 125),
                    ("positive power saturates", [0x02, 0x03, 0x01, 0x02, 0x87], 2, 31, int.MaxValue),
                    ("negative odd power saturates negatively", [0x02, 0x04, 0x01, 0x40, 0x02, 0x87], 3, 21, int.MinValue),
                    ("negative exponent truncates toward zero", [0x02, 0x04, 0x01, 0x02, 0x40, 0x87], 2, 3, 0),
                ];
                foreach (var (name, payload, a, b, expected) in valid)
                {
                    var expression = Sato.Parse(payload, out var error);
                    var actual = expression?.Evaluate(a, b);
                    Check(error == null && actual == expected,
                        $"sato conformance: {name} (want={expected}, got={actual}, parse={error ?? "ok"})");
                }

                (string Name, byte[] Payload)[] invalid =
                [
                    ("version 1", [0x42, 0x01, 0x01]),
                    ("arithmetic width 8", [0x00, 0x01, 0x01]),
                    ("arithmetic width 16", [0x01, 0x01, 0x01]),
                    ("arithmetic width 64", [0x03, 0x01, 0x01]),
                    ("empty stack", [0x02, 0x00]),
                    ("unary stack underflow", [0x02, 0x01, 0x43]),
                    ("binary stack underflow", [0x02, 0x02, 0x01, 0x80]),
                    ("extra stack value", [0x02, 0x02, 0x01, 0x02]),
                    ("missing token", [0x02, 0x02, 0x01]),
                    ("truncated signed constant", [0x02, 0x01, 0x00, 0xff, 0xff, 0xff]),
                    ("zero trailing byte is not padding", [0x02, 0x01, 0x01, 0x00]),
                    ("third input item unsupported by this route", [0x02, 0x01, 0x03]),
                    ("reserved operator", [0x02, 0x02, 0x01, 0xff]),
                ];
                foreach (var (name, payload) in invalid)
                {
                    var expression = Sato.Parse(payload, out var error);
                    Check(expression == null && !string.IsNullOrWhiteSpace(error),
                        $"sato invalid expression fails at parse with diagnostic: {name}");
                }
            }

            // ICC tag types and TIFF page ownership. The fixture writer only emits small
            // uncompressed RGB pages with one real pixel each and optional ICC bytes.
            {
                var fixtureRoot = Path.Combine(FindRepoRoot() ?? throw new InvalidOperationException("repo root missing"),
                    "tests", "output", "_tmp", "icc-container-probe", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(fixtureRoot);
                string Save(string name, byte[] bytes)
                {
                    string path = Path.Combine(fixtureRoot, name);
                    File.WriteAllBytes(path, bytes);
                    return path;
                }

                var srgb = IccProfileService.BuildStandardIccBytesFromCicp("bt709", "iec61966-2-1")
                    ?? throw new InvalidOperationException("sRGB fixture synthesis failed");
                var p3 = IccProfileService.BuildStandardIccBytesFromCicp("smpte432", "iec61966-2-1")
                    ?? throw new InvalidOperationException("P3 fixture synthesis failed");
                var validPath = Save("srgb.icc", srgb);
                Check(IccProfileService.ValidateIccProfile(validPath, out _)
                      && IccProfileService.DetectIccPrimaries(validPath) == "bt709"
                      && !srgb.AsSpan().SequenceEqual(p3),
                    "ICC control has typed sRGB colorants and differs from the P3 fixture");

                int TagEntry(string signature)
                {
                    int entries = checked((int)BP.ReadUInt32BigEndian(srgb.AsSpan(128)));
                    for (int i = 0; i < entries; ++i)
                    {
                        int entry = 132 + 12 * i;
                        if (Encoding.ASCII.GetString(srgb, entry, 4) == signature) return entry;
                    }
                    throw new InvalidOperationException($"ICC fixture missing {signature}");
                }
                int redXyz = TagEntry("rXYZ");
                int greenXyz = TagEntry("gXYZ");
                int redTrc = TagEntry("rTRC");
                int greenTrc = TagEntry("gTRC");
                var falseType = (byte[])srgb.Clone();
                int colorantPayload = checked((int)BP.ReadUInt32BigEndian(srgb.AsSpan(redXyz + 4)));
                "sf32"u8.CopyTo(falseType.AsSpan(colorantPayload));
                var falseTypePath = Save("xyz-wrong-type.icc", falseType);
                Check(IccProfileService.DetectIccPrimaries(falseTypePath) == null,
                    "rXYZ declared as sf32 cannot be recognized from otherwise unchanged sRGB colorant values");

                var duplicateTag = (byte[])srgb.Clone();
                srgb.AsSpan(redXyz, 4).CopyTo(duplicateTag.AsSpan(greenXyz));
                var unalignedTag = new byte[srgb.Length + 24];
                srgb.CopyTo(unalignedTag, 0);
                srgb.AsSpan(colorantPayload, 20).CopyTo(unalignedTag.AsSpan(srgb.Length + 1));
                BP.WriteUInt32BigEndian(unalignedTag, (uint)unalignedTag.Length);
                BP.WriteUInt32BigEndian(unalignedTag.AsSpan(redXyz + 4), (uint)(srgb.Length + 1));
                var partialAlias = (byte[])srgb.Clone();
                BP.WriteUInt32BigEndian(partialAlias.AsSpan(greenXyz + 4), (uint)colorantPayload);
                BP.WriteUInt32BigEndian(partialAlias.AsSpan(greenXyz + 8), 21);
                var tagPastDeclaredSize = (byte[])srgb.Clone();
                BP.WriteUInt32BigEndian(tagPastDeclaredSize, (uint)(srgb.Length - 16));
                var falseCurveType = (byte[])srgb.Clone();
                "sf32"u8.CopyTo(falseCurveType.AsSpan(checked((int)BP.ReadUInt32BigEndian(srgb.AsSpan(redTrc + 4)))));
                foreach (var (name, bytes) in new (string, byte[])[]
                {
                    ("xyz-type", falseType), ("curve-type", falseCurveType),
                    ("duplicate-tag", duplicateTag), ("unaligned-tag", unalignedTag),
                    ("partial-alias", partialAlias), ("tag-past-declared-size", tagPastDeclaredSize),
                })
                {
                    var path = Save(name + "-invalid.icc", bytes);
                    var valid = IccProfileService.ValidateIccProfile(path, out var whyInvalid);
                    var (extracted, _) = IccProfileService.ExtractIccFromBase64(Convert.ToBase64String(bytes), preserveSourceBytes: true);
                    try
                    {
                        Check(!valid && !string.IsNullOrWhiteSpace(whyInvalid) && extracted == null,
                            $"ICC {name} must fail structural validation and exact carry ({whyInvalid ?? "no diagnostic"})");
                    }
                    finally { IccProfileService.TryDeleteIcc(extracted); }
                }

                // Shared complete TRC payloads are legal. Extra bytes past declared size remain opaque padding.
                var paddedAlias = new byte[srgb.Length + 16];
                srgb.CopyTo(paddedAlias, 0);
                srgb.AsSpan(redTrc + 4, 8).CopyTo(paddedAlias.AsSpan(greenTrc + 4));
                paddedAlias[^1] = 0x5a;
                var paddedPath = Save("padded-trc-alias.icc", paddedAlias);
                var (carriedAlias, _) = IccProfileService.ExtractIccFromBase64(Convert.ToBase64String(paddedAlias), preserveSourceBytes: true);
                try
                {
                    Check(IccProfileService.ValidateIccProfile(paddedPath, out _)
                          && carriedAlias != null && File.ReadAllBytes(carriedAlias).AsSpan().SequenceEqual(paddedAlias),
                        "ICC shared complete TRC payload and terminal padding are accepted and carried byte-for-byte");
                }
                finally { IccProfileService.TryDeleteIcc(carriedAlias); }

                // Keep the real lcms alltags payload as a compatibility control, including its
                // optional mBA previews and three-output gamut metadata. These are not source transforms.
                var allTagsPath = Path.Combine(FindRepoRoot()!, "tools", "src", "jpegli", "third_party", "lcms", "fuzzers", "corpus", "alltags.icc");
                var allTags = File.ReadAllBytes(allTagsPath);
                int CorpusEntry(string signature)
                {
                    int count = checked((int)BP.ReadUInt32BigEndian(allTags.AsSpan(128)));
                    for (int i = 0; i < count; i++)
                    {
                        int entry = 132 + i * 12;
                        if (Encoding.ASCII.GetString(allTags, entry, 4) == signature) return entry;
                    }
                    throw new InvalidOperationException($"lcms alltags fixture missing {signature}");
                }
                byte[] MutateCorpus(string signature, Action<byte[], int> mutate)
                {
                    var bytes = (byte[])allTags.Clone();
                    int entry = CorpusEntry(signature);
                    mutate(bytes, checked((int)BP.ReadUInt32BigEndian(bytes.AsSpan(entry + 4))));
                    return bytes;
                }
                var (carriedCorpus, _) = IccProfileService.ExtractIccFromBase64(Convert.ToBase64String(allTags), preserveSourceBytes: true);
                try
                {
                    Check(IccProfileService.ValidateIccProfile(allTagsPath, out _)
                          && carriedCorpus != null && File.ReadAllBytes(carriedCorpus).AsSpan().SequenceEqual(allTags),
                        "lcms alltags optional preview/gamut payloads retain structural acceptance and exact original bytes");
                }
                finally { IccProfileService.TryDeleteIcc(carriedCorpus); }
                foreach (var (name, bytes) in new (string, byte[])[]
                {
                    ("pre0-mab", MutateCorpus("pre0", (b, p) => "mAB "u8.CopyTo(b.AsSpan(p)))),
                    ("gamut-one-output", MutateCorpus("gamt", (b, p) => b[p + 9] = 1)),
                })
                {
                    Check(IccProfileService.ValidateIccProfile(Save(name + ".icc", bytes), out _),
                        $"ICC optional layout control {name} retains the documented tag type/channel form");
                }
                foreach (var (name, signature, bytes) in new (string, string, byte[])[]
                {
                    ("optional-wrong-type", "pre0", MutateCorpus("pre0", (b, p) => "sf32"u8.CopyTo(b.AsSpan(p)))),
                    ("pre1-wrong-direction", "pre1", MutateCorpus("pre1", (b, p) => "mAB "u8.CopyTo(b.AsSpan(p)))),
                    ("optional-unaligned-clut", "pre0", MutateCorpus("pre0", (b, p) => BP.WriteUInt32BigEndian(b.AsSpan(p + 24), 81))),
                    ("optional-clut-outside", "pre0", MutateCorpus("pre0", (b, p) => BP.WriteUInt32BigEndian(b.AsSpan(p + 24), 196))),
                    ("optional-invalid-curve", "pre0", MutateCorpus("pre0", (b, p) => BP.WriteUInt16BigEndian(b.AsSpan(p + 148 + 8), 5))),
                    ("gamut-two-outputs", "gamt", MutateCorpus("gamt", (b, p) => b[p + 9] = 2)),
                    ("gamut-wrong-input", "gamt", MutateCorpus("gamt", (b, p) => b[p + 8] = 4)),
                    ("gamut-wrong-direction", "gamt", MutateCorpus("gamt", (b, p) => "mAB "u8.CopyTo(b.AsSpan(p)))),
                })
                {
                    bool valid = IccProfileService.ValidateIccProfile(Save(name + ".icc", bytes), out var why);
                    var (carried, _) = IccProfileService.ExtractIccFromBase64(Convert.ToBase64String(bytes), preserveSourceBytes: true);
                    try
                    {
                        Check(!valid && (why ?? "").Contains(signature, StringComparison.Ordinal) && carried == null,
                            $"ICC malformed optional metadata {name} fails typed layout and exact carry ({why ?? "no diagnostic"})");
                    }
                    finally { IccProfileService.TryDeleteIcc(carried); }
                }
                foreach (var (signature, bytes) in new (string, byte[])[]
                {
                    ("A2B0", MutateCorpus("A2B0", (b, p) => "mBA "u8.CopyTo(b.AsSpan(p)))),
                    ("B2A0", MutateCorpus("B2A0", (b, p) => b[p + 9] = 2)),
                    ("D2B0", MutateCorpus("D2B0", (b, p) => BP.WriteUInt16BigEndian(b.AsSpan(p + 10), 2))),
                })
                {
                    Check(!IccProfileService.ValidateIccProfile(Save(signature + "-strict.icc", bytes), out var why)
                          && (why ?? "").Contains(signature, StringComparison.Ordinal),
                        $"ICC optional metadata compatibility does not relax executable {signature} direction/channels ({why ?? "no diagnostic"})");
                }
                int optionalEntry = CorpusEntry("pre0");
                int optionalSize = checked((int)BP.ReadUInt32BigEndian(allTags.AsSpan(optionalEntry + 8)));
                var optionalOnly = new byte[144 + optionalSize];
                allTags.AsSpan(0, 128).CopyTo(optionalOnly);
                BP.WriteUInt32BigEndian(optionalOnly, (uint)optionalOnly.Length);
                BP.WriteUInt32BigEndian(optionalOnly.AsSpan(128), 1);
                "pre0"u8.CopyTo(optionalOnly.AsSpan(132));
                BP.WriteUInt32BigEndian(optionalOnly.AsSpan(136), 144);
                BP.WriteUInt32BigEndian(optionalOnly.AsSpan(140), (uint)optionalSize);
                allTags.AsSpan(checked((int)BP.ReadUInt32BigEndian(allTags.AsSpan(optionalEntry + 4))), optionalSize).CopyTo(optionalOnly.AsSpan(144));
                var optionalIdentity = FfmpegGui.Services.ColorMapping.ColorSpaceIdentify.IdentifyIccBytes(optionalOnly);
                Check(IccProfileService.ValidateIccProfile(Save("optional-only.icc", optionalOnly), out _)
                      && !optionalIdentity.IsValid && !optionalIdentity.CanDescribeRgbSource,
                    "ICC optional preview metadata alone cannot establish an executable source transform");

                byte[] TwoPageTiff(byte[]? firstIcc, byte[]? secondIcc)
                {
                    using var data = new MemoryStream();
                    using var writer = new BinaryWriter(data);
                    int FirstDirectorySize(byte[]? icc) => 2 + 12 * (9 + (icc == null ? 0 : 1)) + 4;
                    int secondIfd = 8 + FirstDirectorySize(firstIcc);
                    int bitsPerSample = secondIfd + FirstDirectorySize(secondIcc);
                    int firstPixel = bitsPerSample + 6;
                    int firstProfile = firstPixel + 6;
                    int secondProfile = firstProfile + (firstIcc?.Length ?? 0);
                    writer.Write((ushort)0x4949);
                    writer.Write((ushort)42);
                    writer.Write(8u);
                    void Entry(ushort tag, ushort type, uint count, uint value)
                    {
                        writer.Write(tag);
                        writer.Write(type);
                        writer.Write(count);
                        writer.Write(value);
                    }
                    void Page(int pixel, byte[]? icc, int profileOffset, uint following)
                    {
                        writer.Write((ushort)(9 + (icc == null ? 0 : 1)));
                        Entry(256, 4, 1, 1);             // Width
                        Entry(257, 4, 1, 1);             // Height
                        Entry(258, 3, 3, (uint)bitsPerSample);
                        Entry(259, 3, 1, 1);             // Compression: none
                        Entry(262, 3, 1, 2);             // PhotometricInterpretation: RGB
                        Entry(273, 4, 1, (uint)pixel);   // StripOffsets
                        Entry(277, 3, 1, 3);             // SamplesPerPixel
                        Entry(278, 4, 1, 1);             // RowsPerStrip
                        Entry(279, 4, 1, 3);             // StripByteCounts
                        if (icc != null) Entry(34675, 7, (uint)icc.Length, (uint)profileOffset);
                        writer.Write(following);
                    }
                    Page(firstPixel, firstIcc, firstProfile, (uint)secondIfd);
                    Page(firstPixel + 3, secondIcc, secondProfile, 0);
                    for (int c = 0; c < 3; ++c) writer.Write((ushort)8);
                    writer.Write(new byte[] { 37, 91, 123, 173, 12, 42 });
                    if (firstIcc != null) writer.Write(firstIcc);
                    if (secondIcc != null) writer.Write(secondIcc);
                    writer.Flush();
                    return data.ToArray();
                }

                var reader = typeof(IccProfileService).Assembly.GetType("FfmpegGui.Services.IccContainerReader")
                    ?.GetMethod("TryRead", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                Check(reader != null, "TIFF tests invoke the native product ICC reader directly");
                if (reader != null)
                {
                    (bool Readable, byte[]? Profile) ReadTiffFixture(string name, byte[] bytes)
                    {
                        object?[] args = [Save(name + ".tiff", bytes), null];
                        bool readable = reader.Invoke(null, args) is true;
                        return (readable, args[1] as byte[]);
                    }

                    var laterOnly = TwoPageTiff(null, p3);
                    var noFirst = ReadTiffFixture("second-page-only", laterOnly);
                    var both = ReadTiffFixture("different-page-profiles", TwoPageTiff(srgb, p3));
                    Check(noFirst is { Readable: true, Profile: null },
                        "TIFF native reader does not borrow a later page ICC for the untagged default page");
                    Check(both.Readable && both.Profile != null && both.Profile.AsSpan().SequenceEqual(srgb),
                        "TIFF native reader returns IFD0 ICC bytes even when IFD1 has a different profile");

                    // Exercise the public source probe, not only the native reader: unqualified
                    // ExifTool ICC output for laterOnly is the P3 profile from IFD1.
                    string laterPath = Path.Combine(fixtureRoot, "second-page-only.tiff");
                    string firstPath = Path.Combine(fixtureRoot, "different-page-profiles.tiff");
                    var laterMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(laterPath);
                    Check(laterMeta.iccStatus == FfmpegGui.Services.ColorMapping.SourceIccStatus.Absent
                          && laterMeta.iccIdentity?.Kind == FfmpegGui.Services.ColorMapping.IccProfileKind.Absent
                          && laterMeta.iccDescriptor == null && !laterMeta.hasIccSemantics
                          && laterMeta.width == 1 && laterMeta.height == 1 && laterMeta.bitDepth == 8,
                        "TIFF normal source entry confirms IFD0 absence despite an IFD1 P3 ICC (real 1x1 RGB8 image)");
                    var firstMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(firstPath);
                    Check(firstMeta.iccStatus == FfmpegGui.Services.ColorMapping.SourceIccStatus.Present
                          && firstMeta.iccIdentity is { IsValid: true } firstIdentity
                          && firstIdentity.ProfileBytes.SequenceEqual(srgb)
                          && firstMeta.iccDescriptor != null && firstMeta.sourceGamutName == "sRGB",
                        "TIFF normal source entry identifies the exact IFD0 sRGB ICC instead of the different IFD1 P3 ICC");

                    string paddedTiff = Save("padded-first-page.tif", TwoPageTiff(paddedAlias, p3));
                    var paddedMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(paddedTiff);
                    var (extractedLater, _) = IccProfileService.ExtractIccToTempFile(laterPath, preserveSourceBytes: true);
                    var (extractedFirst, _) = IccProfileService.ExtractIccToTempFile(firstPath, preserveSourceBytes: true);
                    var (extractedPadded, _) = IccProfileService.ExtractIccToTempFile(paddedTiff, preserveSourceBytes: true);
                    try
                    {
                        Check(extractedLater == null,
                            "TIFF normal extraction does not borrow the IFD1 ICC for an untagged IFD0");
                        Check(extractedFirst != null && File.ReadAllBytes(extractedFirst).AsSpan().SequenceEqual(srgb),
                            "TIFF normal extraction returns the exact IFD0 ICC when page profiles differ");
                        Check(paddedMeta.iccStatus == FfmpegGui.Services.ColorMapping.SourceIccStatus.Present
                              && paddedMeta.iccIdentity is { IsValid: true } paddedIdentity
                              && paddedIdentity.ProfileBytes.SequenceEqual(paddedAlias)
                              && extractedPadded != null && File.ReadAllBytes(extractedPadded).AsSpan().SequenceEqual(paddedAlias),
                            "TIFF .tif source identity and exact extraction retain original IFD0 payload aliases and terminal padding");
                    }
                    finally
                    {
                        IccProfileService.TryDeleteIcc(extractedLater);
                        IccProfileService.TryDeleteIcc(extractedFirst);
                        IccProfileService.TryDeleteIcc(extractedPadded);
                    }

                    int nextPointer = checked((int)BP.ReadUInt32LittleEndian(laterOnly.AsSpan(4))) + 2 + 9 * 12;
                    var cycle = (byte[])laterOnly.Clone();
                    BP.WriteUInt32LittleEndian(cycle.AsSpan(nextPointer), 8);
                    var outOfBounds = (byte[])laterOnly.Clone();
                    BP.WriteUInt32LittleEndian(outOfBounds.AsSpan(nextPointer), (uint)outOfBounds.Length);
                    var invalidFirst = (byte[])laterOnly.Clone();
                    BP.WriteUInt32LittleEndian(invalidFirst.AsSpan(4), 6); // Old reader could see zero tags inside the header.
                    foreach (var (name, bytes) in new (string, byte[])[]
                    {
                        ("next-ifd-truncated", laterOnly[..(nextPointer + 3)]), ("next-ifd-cycle", cycle),
                        ("next-ifd-out-of-bounds", outOfBounds), ("first-ifd-inside-header", invalidFirst),
                    })
                    {
                        Check(!ReadTiffFixture(name, bytes).Readable,
                            $"TIFF {name} is unreadable and cannot establish ICC absence");
                        string unreadablePath = Path.Combine(fixtureRoot, name + ".tiff");
                        var unreadableMeta = FfmpegCommandBuilder.ProbeInputColorMetadata(unreadablePath);
                        Check(unreadableMeta.iccStatus == FfmpegGui.Services.ColorMapping.SourceIccStatus.Unreadable
                              && unreadableMeta.iccIdentity?.Kind == FfmpegGui.Services.ColorMapping.IccProfileKind.MalformedOrUnreadable
                              && unreadableMeta.iccDescriptor == null,
                            $"TIFF {name} normal source entry preserves unknown IFD0 ICC as unreadable, not absent or a later-page profile");
                        var extractionLog = new List<string>();
                        var (unreadableExtract, _) = IccProfileService.ExtractIccToTempFile(unreadablePath,
                            extractionLog.Add, preserveSourceBytes: true);
                        try
                        {
                            Check(unreadableExtract == null && extractionLog.Exists(message =>
                                    message.Contains("document-wide ICC fallback disabled", StringComparison.Ordinal)),
                                $"TIFF {name} normal extraction blocks the unqualified fallback when native page identity is unknown");
                        }
                        finally { IccProfileService.TryDeleteIcc(unreadableExtract); }
                    }
                }
            }

            // Source priority and safety exits must agree across planner and legacy decision.
            {
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
                var cache = (Dictionary<string, (DateTime Time, FfmpegCommandBuilder.ColorMetadata Meta)>)
                    typeof(FfmpegCommandBuilder).GetField("ProbeCache", flags)!.GetValue(null)!;
                var cacheLock = typeof(FfmpegCommandBuilder).GetField("ProbeCacheLock", flags)!.GetValue(null)!;
                var runDir = Path.Combine(FindRepoRoot() ?? throw new InvalidOperationException("repo root missing"),
                    "tests", "output", "_tmp", "icc-source-priority", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(runDir);
                var input = Path.Combine(runDir, "embedded-lut.png");
                var disputedInput = Path.Combine(runDir, "disputed.png");
                var external = Path.Combine(runDir, "source-p3.icc");
                var p3Bytes = IccProfileService.BuildStandardIccBytesFromCicp("smpte432", "iec61966-2-1")
                    ?? throw new InvalidOperationException("P3 ICC generation failed");
                File.WriteAllBytes(external, p3Bytes);
                // Minimal valid A2B0/mAB RGB profile with three identity B curves.
                var lutBytes = new byte[212];
                void U32(int offset, uint value)
                    => System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(lutBytes.AsSpan(offset, 4), value);
                void Sig(int offset, string value) => Encoding.ASCII.GetBytes(value).CopyTo(lutBytes, offset);
                U32(0, (uint)lutBytes.Length); lutBytes[8] = 4;
                Sig(12, "mntr"); Sig(16, "RGB "); Sig(20, "XYZ "); Sig(36, "acsp");
                U32(128, 1); Sig(132, "A2B0"); U32(136, 144); U32(140, 68);
                Sig(144, "mAB "); lutBytes[152] = lutBytes[153] = 3; U32(156, 32);
                Sig(176, "curv"); Sig(188, "curv"); Sig(200, "curv");
                var lut = FfmpegGui.Services.ColorMapping.ColorSpaceIdentify.IdentifyIccBytes(lutBytes);
                Check(lut.Kind == FfmpegGui.Services.ColorMapping.IccProfileKind.LutOrUnsupported && lut.CanDescribeRgbSource,
                    "priority fixture is a valid forward RGB LUT ICC");
                // The ICC safety fallback may omit untrusted P/T, but it cannot waive an
                // explicit matrix request. Exercise the four verdict classes on the same LUT.
                CP OpaqueMatrixPlan(string format, string? outputMatrix, bool explicitProfile = false, IccIdentity? profile = null) => CE.Plan(new CI
                {
                    Format = CE.CapsFor(format), Strategy = ST.Recommended, SourceIccIdentity = profile ?? lut,
                    SourceIccIsExplicit = explicitProfile, OutMatrixMode = outputMatrix,
                });
                var fallback = OpaqueMatrixPlan("jpg", null);
                var redundant = OpaqueMatrixPlan("jpg", "bt470bg");
                var carried = OpaqueMatrixPlan("avif", "bt709");
                var rejected = OpaqueMatrixPlan("jpg", "bt709");
                Check(fallback is { Action: CA.None, OutMatrixMode: null, OutMatrix: null, OutMatrixVerdict: OM.NotApplicable }
                      && fallback.Verdict?.Kind == VK.ProceedWithDegradation,
                    "opaque ICC auto keeps the source fallback without a matrix override");
                Check(redundant is { Action: CA.None, OutMatrixMode: "bt470bg", OutMatrix: null, OutMatrixVerdict: OM.Redundant }
                      && redundant.Verdict?.Kind == VK.ProceedWithDegradation,
                    "opaque ICC JPG/601 records a redundant choice without claiming a conversion");
                Check(carried is { Action: CA.None, OutMatrixMode: "bt709", OutMatrix: "bt709", OutMatrixVerdict: OM.Carried }
                      && carried.Verdict?.Kind == VK.ProceedWithDegradation,
                    "opaque ICC AVIF/709 retains its independent output-matrix request");
                foreach (var plan in new[] { fallback, redundant, carried })
                    Check(plan is { IccSafetyCode: "ICC_SOURCE_UNDESCRIBED", Src: null, OutPrimaries: null, OutTransfer: null,
                              AttachIcc: false, UnlabeledAssumedSrgb: false }
                          && plan.Verdict?.Degradations.Any(d => d.Code == DG.SourceIccUnverified) == true,
                        $"opaque ICC {plan.OutMatrixVerdict} retains uncertainty and invents no color-space claim");
                Check(rejected is { Action: CA.Reject, IccSafetyCode: null, OutMatrixMode: "bt709", OutMatrix: null, OutMatrixVerdict: OM.Rejected }
                      && rejected.Verdict is { Kind: VK.Reject, Action: CA.Reject, Degradations.Count: 0 },
                    "opaque ICC JPG/709 rejects as a matrix conflict, with no staged success or completed degradation");

                foreach (var (profile, code) in new[] { (lut, "ICC_CMS_REQUIRED"), (IccIdentity.Absent, "ICC_EXPLICIT_INVALID") })
                foreach (var (format, token, capability) in new[]
                {
                    ("avif", "bt709", OM.Carried), ("jpg", "bt470bg", OM.Redundant), ("jpg", "bt709", OM.Rejected),
                })
                {
                    var initial = OpaqueMatrixPlan(format, null, explicitProfile: true, profile: profile);
                    var withMatrix = OpaqueMatrixPlan(format, token, explicitProfile: true, profile: profile);
                    Check(initial.Action == CA.Reject && initial.IccSafetyCode == code
                          && withMatrix.Action == CA.Reject && withMatrix.IccSafetyCode == code
                          && withMatrix.OutMatrixMode == token && withMatrix.OutMatrixVerdict == capability
                          && withMatrix.Verdict is { Kind: VK.Reject, Action: CA.Reject, Degradations.Count: 0 }
                          && !string.IsNullOrWhiteSpace(initial.Reason)
                          && withMatrix.Reason.StartsWith(initial.Reason, StringComparison.Ordinal),
                        $"{code} remains the primary rejection under {format}/{token} ({capability})");
                }

                var lutMeta = new FfmpegCommandBuilder.ColorMetadata
                {
                    bitDepth = 16, isRgb = true, pixFmt = "rgb48le", hasIccSemantics = true,
                    iccIdentity = lut, iccStatus = FfmpegGui.Services.ColorMapping.SourceIccStatus.Present,
                };
                void Seed(string path, FfmpegCommandBuilder.ColorMetadata meta)
                {
                    lock (cacheLock) cache[path] = (DateTime.UtcNow, meta);
                }
                try
                {
                    Seed(input, lutMeta);
                    var options = new FfmpegGui.Models.FfmpegOptions
                    { Format = "png", ColorStrategy = ST.Recommended, ColorSpace = "sRGB", BitDepth = 16, IccFilePath = external };
                    var intent = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(options, input, out var why);
                    Check(intent?.Source is { PrimariesToken: "smpte432", TransferToken: "iec61966-2-1" }
                          && intent.Target is { PrimariesToken: "bt709", TransferToken: "iec61966-2-1" },
                        $"valid external P3 overrides embedded LUT and keeps explicit sRGB target ({why ?? "ok"})");
                    var plan = intent == null ? null : CE.Plan(intent);
                    Check(plan is { Action: CA.Map, IccSafetyCode: null }
                          && plan.Src?.PrimariesToken == "smpte432" && plan.Dst?.PrimariesToken == "bt709",
                        "planner maps external P3 to the requested sRGB instead of an opaque-ICC early exit");
                    var decision = FfmpegCommandBuilder.DecideOutputColor(options, input);
                    Check(decision.DeclPrimaries == "smpte432" && decision.DeclTrc == "iec61966-2-1"
                          && decision.OutPrimaries == "bt709" && decision.OutTrc == "iec61966-2-1"
                          && !decision.PixelsUnchanged && decision.PixelChains.Exists(s => s.Contains("pin=smpte432") && s.Contains(":p=bt709")),
                        "decision executes external P3 to requested sRGB with matching source and target labels");
                    options.ColorSpace = "auto";
                    var autoDecision = FfmpegCommandBuilder.DecideOutputColor(options, input);
                    Check(autoDecision.OutPrimaries == "smpte432" && autoDecision.OutTrc == "iec61966-2-1"
                          && autoDecision.PixelsUnchanged && !autoDecision.SourceHasCicp,
                        "external P3 auto stays P3 without claiming the source container carried CICP");
                    options.IccFilePath = null;
                    var opaqueDecision = FfmpegCommandBuilder.DecideOutputColor(options, input);
                    Check(opaqueDecision.OutPrimaries == null && opaqueDecision.OutTrc == null
                          && opaqueDecision.PixelChains.Count == 0,
                        "without the external override the embedded LUT still suppresses unverified P/T");

                    var proPhotoIcc = IccProfileService.GetFaithfulIcc("ProPhoto RGB")
                        ?? throw new InvalidOperationException("ProPhoto ICC generation failed");
                    var proPhotoIdentity = FfmpegGui.Services.ColorMapping.ColorSpaceIdentify.IdentifyIccProfile(proPhotoIcc);
                    var proPhotoDescriptor = proPhotoIdentity.CreateMatrixTrcDescriptor()
                        ?? throw new InvalidOperationException("ProPhoto descriptor missing");
                    proPhotoDescriptor.SourceIccPath = "embedded.icc";
                    var srgbBytes = IccProfileService.BuildStandardIccBytesFromCicp("bt709", "iec61966-2-1")
                        ?? throw new InvalidOperationException("sRGB ICC generation failed");
                    var srgbExternal = Path.Combine(runDir, "source-srgb.icc");
                    File.WriteAllBytes(srgbExternal, srgbBytes);
                    try
                    {
                        var embeddedProPhotoMeta = new FfmpegCommandBuilder.ColorMetadata
                        {
                            bitDepth = 16, isRgb = true, pixFmt = "rgb48be", hasIccSemantics = true,
                            sourceGamutName = "ProPhoto RGB", colorPrimaries = "bt709", colorTrc = "bt709",
                            iccIdentity = proPhotoIdentity, iccDescriptor = proPhotoDescriptor,
                            iccStatus = FfmpegGui.Services.ColorMapping.SourceIccStatus.Present,
                        };
                        Seed(input, embeddedProPhotoMeta);
                        var faithfulOptions = new FfmpegGui.Models.FfmpegOptions
                        {
                            Format = "jxl", EncoderBackend = EncoderBackend.Cjxl, BitDepth = 16,
                            ColorStrategy = ST.Recommended, ColorSpace = "auto", IccFilePath = srgbExternal,
                        };
                        var externalSrgb = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(faithfulOptions, input, out _);
                        var srgbPlan = externalSrgb == null ? null : CE.Plan(externalSrgb);
                        Check(externalSrgb?.Source?.KnownSpaceName == "sRGB" && externalSrgb.FaithfulIccPath == null
                              && srgbPlan != null && srgbPlan.IccPath != proPhotoIcc,
                            "external sRGB overrides embedded ProPhoto without reviving the embedded faithful ICC on the cjxl route");

                        var srgbIdentity = FfmpegGui.Services.ColorMapping.ColorSpaceIdentify.IdentifyIccBytes(srgbBytes);
                        Seed(input, new FfmpegCommandBuilder.ColorMetadata
                        {
                            bitDepth = 16, isRgb = true, pixFmt = "rgb48be", hasIccSemantics = true,
                            sourceGamutName = "sRGB", colorPrimaries = "bt709", colorTrc = "iec61966-2-1",
                            iccIdentity = srgbIdentity, iccDescriptor = srgbIdentity.CreateMatrixTrcDescriptor(),
                            iccStatus = FfmpegGui.Services.ColorMapping.SourceIccStatus.Present,
                        });
                        faithfulOptions.IccFilePath = proPhotoIcc;
                        var externalProPhoto = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(faithfulOptions, input, out _);
                        var proPhotoPlan = externalProPhoto == null ? null : CE.Plan(externalProPhoto);
                        Check(externalProPhoto?.Source?.KnownSpaceName == "ProPhoto RGB"
                              && externalProPhoto.FaithfulIccPath == proPhotoIcc
                              && proPhotoPlan is { Action: CA.CarryIcc, ExternalEncoderExit: true }
                              && proPhotoPlan.IccPath == proPhotoIcc,
                            "external ProPhoto overrides embedded sRGB and selects its own faithful ICC on the cjxl route");
                        Seed(input, embeddedProPhotoMeta);
                        faithfulOptions.IccFilePath = null;
                        foreach (bool primariesOnly in new[] { true, false })
                        {
                            faithfulOptions.ColorPrimaries = primariesOnly ? "bt709" : null;
                            faithfulOptions.ColorTrc = primariesOnly ? null : "iec61966-2-1";
                            var explicitAxis = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(faithfulOptions, input, out _);
                            var axisPlan = explicitAxis == null ? null : CE.Plan(explicitAxis);
                            var axisDecision = FfmpegCommandBuilder.DecideOutputColor(faithfulOptions, input);
                            Check(explicitAxis is { TargetExplicit: true, FaithfulIccPath: null }
                                  && !axisDecision.Faithful
                                  && FfmpegCommandBuilder.ResolveFaithfulUltraWideIcc(faithfulOptions, "ProPhoto RGB", "bt709", "jxl") == null
                                  && axisPlan is { Action: CA.Map } && axisPlan.Dst?.PrimariesToken == "bt709",
                                $"explicit {(primariesOnly ? "primaries" : "transfer")} target converts ProPhoto instead of taking the faithful carry shortcut");
                        }
                    }
                    finally { File.Delete(srgbExternal); }

                    object disputedMeta = new FfmpegCommandBuilder.ColorMetadata
                    { bitDepth = 16, isRgb = true, pixFmt = "rgb48le" };
                    typeof(FfmpegCommandBuilder.ColorMetadata).GetField("InvalidPngCicp",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(disputedMeta, true);
                    foreach (var fmt in new[] { "jxl", "jxr" })
                    {
                        Seed(disputedInput, (FfmpegCommandBuilder.ColorMetadata)disputedMeta);
                        var externalOptions = new FfmpegGui.Models.FfmpegOptions
                        { Format = fmt, BitDepth = 16, ColorStrategy = ST.Recommended, EncoderBackend = EncoderBackend.Cjxl };
                        var disputed = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(externalOptions, disputedInput, out var disputedWhy);
                        var exitPlan = disputed == null ? null : CE.Plan(disputed);
                        Check(disputed is { ExternalEncoderExit: true, TargetBitDepth: 16 }
                              && exitPlan is { Action: CA.None, ExternalEncoderExit: true, TargetBitDepth: 16, OutBitDepth: 16 },
                            $"disputed 16bit {fmt} preserves the selected external route and delivery precision ({disputedWhy ?? "ok"})");
                        Check(exitPlan != null && exitPlan.OutPrimaries == null && exitPlan.OutTransfer == null
                              && exitPlan.OutMatrix == null && !exitPlan.AttachIcc && !exitPlan.UnlabeledAssumedSrgb
                              && exitPlan.Verdict?.Kind == VK.ProceedWithDegradation,
                            $"disputed {fmt} continues with degradation and no invented color-space claim");
                        Seed(input, lutMeta);
                        externalOptions.ColorStrategy = ST.BakeCicpOnly;
                        var opaqueIntent = FfmpegGui.Services.ColorMapping.ColorIntentFactory.FromOptions(externalOptions, input, out var opaqueWhy);
                        var safety = opaqueIntent == null ? null : CE.Plan(opaqueIntent);
                        Check(opaqueIntent is { ExternalEncoderExit: true, TargetBitDepth: 16 }
                              && safety is { Action: CA.None, IccSafetyCode: "ICC_SOURCE_UNDESCRIBED", ExternalEncoderExit: true,
                                TargetBitDepth: 16, OutBitDepth: 16, DeclaredStrategy: ST.BakeCicpOnly, EffectiveStrategy: ST.BakeCicpOnly }
                              && safety.OutPrimaries == null && safety.OutTransfer == null && safety.OutMatrix == null,
                            $"opaque {fmt} safety early exit keeps encoder, 16bit precision and strategy without invented P/T ({opaqueWhy ?? "ok"})");
                    }
                }
                finally
                {
                    lock (cacheLock) { cache.Remove(input); cache.Remove(disputedInput); }
                    File.Delete(external);
                }
            }
        }

    }
}
