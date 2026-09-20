using System.Buffers.Binary;
using System.Text;

namespace FfmpegGui.Services
{
    /// <summary>
    /// PNG 3.0 chunk 写入服务（已实现：sBIT / cICP；**尚未实现：cLLI / mDCV**）。
    /// ffmpeg PNG 编码器只能输出 8/16-bit 容器，无法表达 10/12-bit 有效位语义
    /// （gbrp10le 输入会被自动提升为 16-bit 且不写 sBIT chunk），也**不写 cICP**（实测）。
    /// 本服务在编码完成后以二进制方式补写，使输出符合 PNG 3.0 规范（2025-06-24 W3C Recommendation）。
    /// 算法与 tools/src/pngcicp 工具一致（CRC32 反射表 0xEDB88320，已经 ffmpeg 解码零告警交叉验证）。
    /// ⚠ 已知缺口：生产路径（QueueProcessor）只调 <see cref="TryInsertSbit"/>，未调 <see cref="TryInsertCicp"/>
    ///   ⇒ HDR(PQ/HLG) PNG 目前**没有任何色彩描述**（iccgen 又不支持 HDR ICC）；
    ///   接前必须用与 BuildArguments 同源的输出语义判定，且避开与 iCCP 的冲突（规范：iCCP 优先）。
    /// </summary>
    public static class PngCicpService
    {
        /// <summary>向 16-bit RGB PNG 写入 sBIT chunk（3 字节：R/G/B 有效位数）。
        /// 仅当目标为 RGB 或灰度 PNG 时有效；失败时返回 false 且不修改文件。</summary>
        public static bool TryInsertSbit(string pngPath, int bits)
        {
            if (bits is < 1 or > 16) return false;
            try
            {
                var data = File.ReadAllBytes(pngPath);
                if (!TryParseHeader(data, out var colorType, out var bitDepth, out var ihdrEnd))
                    return false;

                // sBIT 语义按 color type：
                //   RGB (2):    3 字节 R,G,B
                //   RGB+Alpha (6): 4 字节 R,G,B,A
                //   Gray (0):   1 字节
                //   Gray+Alpha (4): 2 字节
                int payloadLen = colorType switch
                {
                    2 => 3,
                    6 => 4,
                    0 => 1,
                    4 => 2,
                    _ => 0 // 调色板类型不支持 sBIT（软件不输出 pal8 以外调色板）
                };
                if (payloadLen == 0) return false;

                var payload = new byte[payloadLen];
                Array.Fill(payload, (byte)Math.Clamp(bits, 1, 16));
                var chunk = BuildChunk("sBIT", payload);

                // 已存在 sBIT？先移除旧 chunk（避免重复）
                using var ms = new MemoryStream(data.Length + chunk.Length + 16);
                ms.Write(data, 0, ihdrEnd);
                int pos = ihdrEnd;
                while (pos + 12 <= data.Length)
                {
                    int len = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
                    var type = Encoding.ASCII.GetString(data, pos + 4, 4);
                    if (type == "sBIT") { pos += 12 + len; continue; }
                    ms.Write(data, pos, 12 + len);
                    pos += 12 + len;
                }
                // 在 IHDR 后插入 sBIT（PNG 规范要求 sBIT 紧随 IHDR）
                var buf = ms.ToArray();
                using var outMs = new MemoryStream(buf.Length + chunk.Length);
                outMs.Write(buf, 0, ihdrEnd);
                outMs.Write(chunk);
                outMs.Write(buf, ihdrEnd, buf.Length - ihdrEnd);
                File.WriteAllBytes(pngPath, outMs.ToArray());
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>向 PNG 写入 cICP chunk（4 字节：primaries/transfer/matrix/full-range）。
        /// 备用能力：ffmpeg 编码器在帧携带色彩标签时会自动写 cICP，此方法用于
        /// 补救帧标签缺失的场景（如 tonemap 后）。matrix 对 PNG 恒为 0（RGB），full 恒为 1。</summary>
        public static bool TryInsertCicp(string pngPath, byte primaries, byte transfer)
        {
            try
            {
                var data = File.ReadAllBytes(pngPath);
                if (!TryParseHeader(data, out _, out _, out var ihdrEnd))
                    return false;

                var chunk = BuildChunk("cICP", new byte[] { primaries, transfer, 0, 1 });

                // 移除旧 cICP
                using var ms = new MemoryStream(data.Length + chunk.Length + 16);
                ms.Write(data, 0, ihdrEnd);
                int pos = ihdrEnd;
                while (pos + 12 <= data.Length)
                {
                    int len = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
                    var type = Encoding.ASCII.GetString(data, pos + 4, 4);
                    if (type == "cICP") { pos += 12 + len; continue; }
                    ms.Write(data, pos, 12 + len);
                    pos += 12 + len;
                }
                var buf = ms.ToArray();
                using var outMs = new MemoryStream(buf.Length + chunk.Length);
                outMs.Write(buf, 0, ihdrEnd);
                outMs.Write(chunk);
                outMs.Write(buf, ihdrEnd, buf.Length - ihdrEnd);
                File.WriteAllBytes(pngPath, outMs.ToArray());
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// PNG 色彩标注动作（由**四大策略**决定，不靠“文件里碰巧有没有 iCCP”临时推断）。
        /// </summary>
        public enum PngLabelAction
        {
            /// <summary>写 cICP。</summary>
            WriteCicp,
            /// <summary>不写：S2 下源只有 ICC，且该 ICC **不是标准色彩空间 ICC**（色度未命中任何已知空间）
            /// ⇒ CICP 只能猜，而猜出的 cICP 优先级高于 iCCP，会降级源 ICC 的精确描述。</summary>
            SkipSourceIcc,
            /// <summary>不写：输出语义无法用 H.273 命名（如 ProPhoto/Adobe）——宁缺勿错。</summary>
            SkipUnnameable,
            /// <summary>不写：已有 cICP（不重复、不覆盖）。</summary>
            SkipAlreadyLabeled,
        }

        /// <summary>
        /// 标注决策（纯函数，可断言）。规范前提（PNG Third Edition）：优先级 **cICP=1 &gt; iCCP=2 &gt; sRGB=3 &gt; cHRM/gAMA=4**，
        /// 且“无 cICP 时至多一个嵌入 profile”⇒ cICP 与 iCCP **允许并存**。
        ///
        ///  • **源容器本身就带 CICP 标签**（nclx/cICP/等效）⇒ 无论哪个策略都应当**携带**（丢了就是丢信息）；
        ///  • S2 CarryIcc 且源**只有 ICC**：
        ///      – ICC 是**标准色彩空间 ICC**（色度精确命中某已知空间，与输出语义一致）⇒ **写 cICP**（两者同义，不丢精度）；
        ///      – ICC 非标准（相机定制/色度未命中）⇒ **不写**：猜出的 cICP 会降级源 ICC；
        ///  • S1 Recommended / S4 Manual ⇒ ICC 由 iccgen/我们生成，与 CICP 同义 ⇒ 并写安全且更稳健；
        ///  • S3 BakeCicpOnly ⇒ 本意就是“只留 CICP”⇒ 必写 cICP（若有 ICC 属异常，由调用方告警）。
        /// </summary>
        /// <param name="sourceHasCicp">源容器是否自带 CICP 标签（true=携带）。</param>
        /// <param name="iccIsStandardSpace">源 ICC 是否经**色度度量**确认等于某个标准空间（由
        /// IccProfileService.DetectIccPrimaries 得出，容差 2e-3；非按描述文本猜测）。</param>
        public static PngLabelAction DecideLabel(FfmpegGui.Models.ColorStrategy strategy, bool hasIccp,
                                                 bool hasCicp, bool sourceHasCicp, bool iccIsStandardSpace,
                                                 bool cicpNameable)
        {
            if (hasCicp) return PngLabelAction.SkipAlreadyLabeled;
            if (!cicpNameable) return PngLabelAction.SkipUnnameable;
            // S2：能携带就携带，能精确命名才补写；只对“非标准源 ICC”不推断。
            if (strategy == FfmpegGui.Models.ColorStrategy.CarryIcc && hasIccp && !sourceHasCicp && !iccIsStandardSpace)
                return PngLabelAction.SkipSourceIcc;
            return PngLabelAction.WriteCicp;
        }

        /// <summary>
        /// PNG 是否已带 iCCP（ICC）或已有 cICP。
        /// 规范依据（PNG Third Edition，已核原文）：色彩来信息**优先级 cICP=1 &gt; iCCP=2 &gt; sRGB=3 &gt; cHRM/gAMA=4**，
        /// 且“除非存在 cICP，否则一个数据流应至多含一个嵌入 profile”——即 **cICP 与 iCCP 允许并存**。
        /// 因此不能盲写 cICP：只有当 ICC 是**我们自生成的标准 ICC**（与 CICP 同义）时两者才必然一致；
        /// 携带的**源 ICC**（相机定制/不可 CICP 命名）被高优先级 cICP 覆盖会变差 ⇒ 那种情形不写。
        /// </summary>
        public static bool HasColorChunk(string pngPath, out bool hasIccp, out bool hasCicp)
        {
            hasIccp = false; hasCicp = false;
            try
            {
                var data = File.ReadAllBytes(pngPath);
                if (!TryParseHeader(data, out _, out _, out var pos)) return false;
                while (pos + 12 <= data.Length)
                {
                    int len = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
                    if (len < 0 || pos + 12 + len > data.Length) break;
                    var type = Encoding.ASCII.GetString(data, pos + 4, 4);
                    if (type == "iCCP") hasIccp = true;
                    else if (type == "cICP") hasCicp = true;
                    else if (type == "IDAT" || type == "IEND") break;   // 色彩 chunk 必在 IDAT 之前，后面的不用再看
                    pos += 12 + len;
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>解析 PNG 签名与 IHDR，返回 colorType/bitDepth 与 IHDR chunk 末尾偏移。</summary>
        private static bool TryParseHeader(byte[] data, out byte colorType, out byte bitDepth, out int ihdrEnd)
        {
            colorType = 0;
            bitDepth = 0;
            ihdrEnd = 0;
            if (data.Length < 33 || data[0] != 0x89 || data[1] != 0x50) return false;
            var type = Encoding.ASCII.GetString(data, 12, 4);
            if (type != "IHDR") return false;
            bitDepth = data[24];
            colorType = data[25];
            int len = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(8));
            ihdrEnd = 8 + 12 + len;
            return true;
        }

        private static byte[] BuildChunk(string typeName, byte[] payload)
        {
            var type = Encoding.ASCII.GetBytes(typeName);
            var crcInput = new byte[type.Length + payload.Length];
            type.CopyTo(crcInput, 0);
            payload.CopyTo(crcInput, type.Length);
            uint crc = Crc32(crcInput);
            var chunk = new byte[12 + payload.Length];
            BinaryPrimitives.WriteInt32BigEndian(chunk, payload.Length);
            type.CopyTo(chunk, 4);
            payload.CopyTo(chunk, 8);
            // 注意: 必须写 span 末尾（sBIT 3 字节 payload 时 AsSpan(12) 会越界）
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(chunk.Length - 4), crc);
            return chunk;
        }

        private static uint Crc32(byte[] data)
        {
            // 反射 CRC-32 (IEEE 802.3), 多项式 0xEDB88320
            Span<uint> table = stackalloc uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[(int)i] = c;
            }
            uint crc = 0xFFFFFFFF;
            foreach (var b in data)
                crc = table[(int)((crc ^ b) & 0xFF)] ^ (crc >> 8);
            return ~crc;
        }
    }
}
