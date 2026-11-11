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
        /// jxlgm —— 同程序集 internal <c>JxlGainMapWriter</c>（JXL jhgm 容器组装，2026-11-08）的
        /// 单元级锁：反射调 internal 类型（对齐 avifgm 先例），不依赖任何外部 exe。
        /// 调用者 = 在册门禁 verify-gainmap-jxl.ps1 §1/§2。
        /// </summary>
        private static void ProbeJxlGainMap()
        {
            var asm = typeof(GainMapEncoder).Assembly;
            var t = asm.GetType("FfmpegGui.Services.JxlGainMapWriter", throwOnError: false);
            Check(t != null, "jxlgm：找得到 internal JxlGainMapWriter（否则本 mode 的锁全部真空绿）");
            if (t == null) return;
            var build = t.GetMethod("Build",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                new[] { typeof(byte[]), typeof(byte[]), typeof(byte[]) });
            Check(build != null, "jxlgm：找得到 public static Build(byte[], byte[], byte[])（底图/增益图裸码流 + ISO 单一真值）");
            if (build == null) return;
            var br = build.ReturnType;
            string[] members = { "Data", "FailReason", "BaseCodestreamBytes", "GainMapCodestreamBytes", "JhgmPayloadBytes", "Ok" };
            bool membersOk = members.All(m => br.GetProperty(m) != null || br.GetField(m) != null);
            Check(membersOk, "jxlgm：BuildResult 六成员齐（Data/FailReason/BaseCodestreamBytes/GainMapCodestreamBytes/JhgmPayloadBytes/Ok）");
            var isoM = typeof(GainMapEncoder).GetMethod("BuildIso21496Metadata",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                new[] { typeof(int), typeof(float), typeof(float), typeof(float?) });
            Check(isoM != null, "jxlgm：找得到 internal BuildIso21496Metadata(int,float,float,float?)（ISO blob 单一真值，必须逐字节同源）");
            if (isoM == null) return;
            var iso = (byte[])isoM.Invoke(null, new object?[] { 1, 1.0f, 0f, null })!;
            Check(iso.Length == 61, $"jxlgm：单通道 ISO 21496-1 blob = 61 B（实 {iso.Length}）");

            // 正控：最小裸码流（写出器只认 FF 0A 前缀；像素可解性归门禁 §1 的 djxl 臂）
            byte[] sig = { 0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87, 0x0A };
            byte[] baseCs = { 0xFF, 0x0A, 0x01, 0x02 };
            byte[] gmCs = { 0xFF, 0x0A, 0x03, 0x04 };
            var r = build.Invoke(null, new object?[] { baseCs, gmCs, iso });
            bool ok = BRo(r, "Ok", false);
            var data = BRo(r, "Data", Array.Empty<byte>());
            Check(ok, "jxlgm：正控 Build 成功（Ok=true）");
            int jhgmPayloadLen = 1 + 2 + iso.Length + 1 + 4 + gmCs.Length;
            int wantLen = 12 + 20 + (8 + jhgmPayloadLen) + (8 + baseCs.Length);
            Check(data.Length == wantLen,
                  $"jxlgm：容器总长 = sig 12 + ftyp 20 + jhgm 8+{jhgmPayloadLen} + jxlc 8+{baseCs.Length} = {wantLen}（实 {data.Length}）");
            Check(data.Length >= 12 && data.AsSpan(0, 12).SequenceEqual(sig),
                  "jxlgm：容器以 JXL 签名开头（00 00 00 0C JXL_ 0D 0A 87 0A）");
            bool ftypOk = data.Length >= 32 && BE32(data, 12) == 20
                          && data[16] == (byte)'f' && data[17] == (byte)'t' && data[18] == (byte)'y' && data[19] == (byte)'p';
            Check(ftypOk, "jxlgm：ftyp box 在位（20 B，size + 类型字面量）");
            int off = 32;
            uint jhgmSize = data.Length >= off + 8 ? BE32(data, off) : 0;
            bool jhgmType = data.Length >= off + 8 && data[off + 4] == (byte)'j' && data[off + 5] == (byte)'h'
                            && data[off + 6] == (byte)'g' && data[off + 7] == (byte)'m';
            Check(jhgmType && jhgmSize == 8 + jhgmPayloadLen,
                  $"jxlgm：jhgm box 紧随 ftyp 且 size = 8+{jhgmPayloadLen}（实 size={jhgmSize}）");
            int off2 = off + (int)jhgmSize;
            uint jxlcSize = data.Length >= off2 + 8 ? BE32(data, off2) : 0;
            bool jxlcType = data.Length >= off2 + 8 && data[off2 + 4] == (byte)'j' && data[off2 + 5] == (byte)'x'
                            && data[off2 + 6] == (byte)'l' && data[off2 + 7] == (byte)'c';
            Check(jxlcType && off2 > off && jxlcSize == 8 + baseCs.Length,
                  $"jxlgm：jhgm 在 jxlc 之前（偏移 {off} < {off2}）且 jxlc size = 8+{baseCs.Length}（实 size={jxlcSize}）");
            int p = off + 8;
            Check(p < data.Length && data[p] == 0, "jxlgm：载荷首字节 version = 0");
            int metaLen = p + 2 < data.Length ? (data[p + 1] << 8) | data[p + 2] : -1;
            Check(metaLen == iso.Length, $"jxlgm：BE16 元数据长 = {iso.Length}（实 {metaLen}）");
            bool blobEq = metaLen == iso.Length && p + 3 + iso.Length <= data.Length
                          && data.AsSpan(p + 3, iso.Length).SequenceEqual(iso);
            Check(blobEq, "jxlgm：ISO blob 与 BuildIso21496Metadata 输出逐字节相等（jxlinfo 看不见 blob 内容 ⇒ 这是解析级判据的核心一针）");
            int q = p + 3 + iso.Length;
            Check(q < data.Length && data[q] == 0,
                  "jxlgm：颜色编码长 = 0（rawmakase #912 同形：map 施加于底图色彩空间）");
            int q2 = q + 1;
            uint altIcc = q2 + 4 <= data.Length ? BE32(data, q2) : 0xFFFFFFFF;
            Check(altIcc == 0, $"jxlgm：alt ICC 长 = 0（实 {altIcc}）");
            int gOff = q2 + 4;
            bool gmEq = gOff + gmCs.Length <= data.Length && data.AsSpan(gOff, gmCs.Length).SequenceEqual(gmCs);
            Check(gmEq, "jxlgm：增益图载荷 = 输入裸码流（FF 0A 开头）逐字节相等");
            int bOff = off2 + 8;
            bool baseEq = bOff + baseCs.Length <= data.Length && data.AsSpan(bOff, baseCs.Length).SequenceEqual(baseCs);
            Check(baseEq, "jxlgm：jxlc 载荷 = 底图裸码流逐字节相等");

            // 负控三臂
            var rBad1 = build.Invoke(null, new object?[] { sig.Concat(new byte[] { 1 }).ToArray(), gmCs, iso });
            bool okBad1 = BRo(rBad1, "Ok", true);
            string fr1 = BRo(rBad1, "FailReason", "");
            Check(!okBad1 && (fr1.Contains("容器") || fr1.Contains("码流")),
                  $"jxlgm：负控① 传入容器件 ⇒ 拒绝且 FailReason 点名（实 ok={okBad1} reason={fr1}）");
            var rBad2 = build.Invoke(null, new object?[] { new byte[] { 0x12, 0x34, 0x56, 0x78 }, gmCs, iso });
            bool okBad2 = BRo(rBad2, "Ok", true);
            string fr2 = BRo(rBad2, "FailReason", "");
            Check(!okBad2 && !string.IsNullOrWhiteSpace(fr2),
                  $"jxlgm：负控② 随机字节（非 FF 0A）⇒ 拒绝且 FailReason 非空（实 ok={okBad2} reason={fr2}）");
            var rBad3 = build.Invoke(null, new object?[] { baseCs, Array.Empty<byte>(), iso });
            bool okBad3 = BRo(rBad3, "Ok", true);
            Check(!okBad3, $"jxlgm：负控③ 空增益图 ⇒ 拒绝（实 ok={okBad3}）");
        }

        /// <summary>大端读 u32（jxlgm 用）。</summary>
        private static uint BE32(byte[] b, int off)
            => ((uint)b[off] << 24) | ((uint)b[off + 1] << 16) | ((uint)b[off + 2] << 8) | b[off + 3];

        /// <summary>按成员名反射读 BuildResult 的属性或字段（jxlgm 用；属性优先）。</summary>
        private static object? BR(object? res, string member)
        {
            if (res == null) return null;
            var ty = res.GetType();
            var pi = ty.GetProperty(member);
            if (pi != null) return pi.GetValue(res);
            return ty.GetField(member)?.GetValue(res);
        }

        /// <summary>BR 的带默认值版（jxlgm 用）。</summary>
        private static T BRo<T>(object? res, string member, T def)
        {
            var v = BR(res, member);
            return v is T t ? t : def;
        }

        /// <summary>造一个 nclx 型 `colr` 盒：size(4)+'colr'+'nclx'+prim(2)+trc(2)+mtx(2)+full_range(1) = 19 B。</summary>
        private static byte[] NclxColrBox(byte fullRange)
        {
            var b = new List<byte>(19);
            b.AddRange(new byte[] { 0, 0, 0, 19 });
            b.AddRange(Encoding.ASCII.GetBytes("colr"));
            b.AddRange(Encoding.ASCII.GetBytes("nclx"));
            b.AddRange(new byte[] { 0, 1, 0, 13, 0, 1, fullRange });
            return b.ToArray();
        }

        /// <summary>扫出字节流里所有 `nclx` 盒的 (偏移, prim, trc, mtx, full_range)。</summary>
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

        private static void RunCap(string exe, string args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.StandardError.ReadToEnd(); p.StandardOutput.ReadToEnd(); p.WaitForExit();
        }



        // ───────────────────── 缩略图探针的共用小工具 ─────────────────────

        /// <summary>造一个指定容器的最小目标件。avif 走 avifenc —— 本构建 ffmpeg 无 libaom-avif 编码器（实测 rc=8）。</summary>
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

        /// <summary>
        /// 跑外部工具并回收 stdout。<paramref name="redirectOut"/> 非空时把 stdout 当**二进制**落盘
        /// （exiftool 的 <c>-b</c> 输出是字节流，走字符串读会毁掉 0x00）。
        /// </summary>
        private static string RunCapture(string exe, string args, string? redirectOut = null)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            if (redirectOut != null)
            {
                using (var fs = new FileStream(redirectOut, FileMode.Create, FileAccess.Write))
                    p.StandardOutput.BaseStream.CopyTo(fs);
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                return "";
            }
            var text = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return text;
        }

        private static int RunTask(System.Threading.Tasks.Task<int> t)
            => t.GetAwaiter().GetResult();

        private static string Sha(string path)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            return Convert.ToHexString(sha.ComputeHash(fs));
        }


    }
}
