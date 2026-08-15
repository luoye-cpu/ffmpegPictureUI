using System.Buffers.Binary;
using System.Text;

namespace FfmpegGui.Services
{
    /// <summary>
    /// PNG 3.0 chunk 写入服务（sBIT / cICP / cLLI / mDCV）。
    /// ffmpeg PNG 编码器只能输出 8/16-bit 容器，无法表达 10/12-bit 有效位语义
    /// （gbrp10le 输入会被自动提升为 16-bit 且不写 sBIT chunk）。
    /// 本服务在编码完成后以二进制方式写入 sBIT chunk，使 16-bit 容器内
    /// 的 10/12-bit 有效位信息符合 PNG 3.0 规范（2025-06-24 W3C Recommendation）。
    /// 算法与 tools/src/pngcicp 工具一致（CRC32 反射表 0xEDB88320，已验证）。
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
