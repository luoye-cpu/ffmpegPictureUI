using System;
using System.IO;
using System.Text;

namespace FfmpegGui.Services
{
    public enum JxlImageType
    {
        Unknown = 0,
        JpegReconstruction = 1,
        NativeCodestream = 2
    }

    /// <summary>
    /// 快速检测 JXL 文件类型：区分是否包含 JPEG 重构数据（jbrd）或为原生 codestream。
    /// 容器文件按 ISOBMFF box 长度链完整遍历顶层 box（而非固定窗口字节扫描）——
    /// cjxl 常把 jbrd 写在 jxlp/jxlc 之后，窗口扫描会因大元数据 box 漏检 jbrd，
    /// 导致本应无损还原的文件被误判为原生码流而遭到有损重编码。
    /// 说明：这是轻量级检测，遇到不确定情况应使用 libjxl API 做准确判断。
    /// </summary>
    public static class JxlInspector
    {
        public static JxlImageType DetectType(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);

                // 裸码流 magic: 0xFF 0x0A
                // ⚠ **必须放在长度检查之前**（2026-09-18 修）：真实 JXL 裸码流最短也 ≥12 B
                //   （1×1 实测 60 B）⇒ 若写在 `!ReadExact(fs, head, 12)` 那一支里就是**死代码**：
                //   裸码流会落到下面的容器检查 ⇒ 字节 4..8 不是 "JXL " ⇒ 判 `Unknown`。
                //   实测后果（修前）：用户显式 `--format jxl --encoder cjxl` / `--format jpg --encoder cjpegli`
                //   被**静默忽略**，实际跑 ffmpeg 的 libjxl / mjpeg；且 app 自己产出的 .jxl（cjxl 默认
                //   `--container=0` ⇒ 裸码流）回灌时也拿不到 app 自己的 cjxl 出口。
                //   ⚠ `FF 0A` 与 ISOBMFF 容器不冲突：容器首 12 B 是 `00 00 00 0C 'JXL ' 0D 0A 87 0A`
                //   （字节 0..1 = `00 00`），故先判 FF 0A 不会把容器误判成裸码流。
                //   ⚠ 对 <12 B 的短文件行为**逐字不变**：`ReadExact` 会把已读到的字节留在 head 里
                //   （其余保持零初始化），所以「2 字节 FF 0A」仍判 NativeCodestream、「1 字节 FF」仍判 Unknown。
                var head = new byte[12];
                bool full12 = ReadExact(fs, head, 12);
                if (head[0] == 0xFF && head[1] == 0x0A)
                    return JxlImageType.NativeCodestream;
                if (!full12)
                    return JxlImageType.Unknown;

                // ISOBMFF 容器：bytes 4..8 == "JXL "（ftyp 的 brand）
                if (!(head[4] == (byte)'J' && head[5] == (byte)'X' && head[6] == (byte)'L' && head[7] == (byte)' '))
                    return JxlImageType.Unknown;

                var (hasJbrd, hasCodestream) = WalkTopLevelBoxes(fs);
                if (hasJbrd) return JxlImageType.JpegReconstruction;
                if (hasCodestream) return JxlImageType.NativeCodestream;

                // box 链异常（畸形长度字段等）→ 退回有界窗口扫描兜底
                return ScanWindowForType(fs);
            }
            catch { }
            return JxlImageType.Unknown;
        }

        /// <summary>遍历顶层 box：jbrd → JPEG 重构数据；jxlc/jxlp → 码流。两者可共存，jbrd 优先。</summary>
        private static (bool hasJbrd, bool hasCodestream) WalkTopLevelBoxes(FileStream fs)
        {
            bool hasJbrd = false, hasCodestream = false;
            long pos = 0;
            var header = new byte[8];
            var large = new byte[8];

            while (true)
            {
                fs.Seek(pos, SeekOrigin.Begin);
                if (!ReadExact(fs, header, 8)) break;

                uint size32 = (uint)((header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3]);
                string type = Encoding.ASCII.GetString(header, 4, 4);

                long headerSize = 8;
                long size;
                if (size32 == 1)
                {
                    // largesize：8 字节 64 位长度
                    if (!ReadExact(fs, large, 8)) break;
                    size = 0;
                    for (int i = 0; i < 8; i++) size = (size << 8) | large[i];
                    headerSize = 16;
                }
                else if (size32 == 0)
                {
                    size = -1;   // 到文件尾
                }
                else
                {
                    size = size32;
                }

                if (type == "jbrd") hasJbrd = true;
                else if (type == "jxlc" || type == "jxlp") hasCodestream = true;

                if (size < 0 || size < headerSize) break;   // 到尾 box / 畸形长度
                pos += size;
                if (pos <= 0) break;                        // 溢出保护
            }

            return (hasJbrd, hasCodestream);
        }

        /// <summary>兜底：窗口内字节扫描（box 遍历失败时使用，窗口扩到 1MB）。</summary>
        private static JxlImageType ScanWindowForType(FileStream fs)
        {
            try
            {
                fs.Seek(0, SeekOrigin.Begin);
                var buf = new byte[1024 * 1024];
                var r = fs.Read(buf, 0, buf.Length);
                if (r < 4) return JxlImageType.Unknown;

                if (IndexOfSequence(buf, r, new byte[] { (byte)'j', (byte)'b', (byte)'r', (byte)'d' }) >= 0)
                    return JxlImageType.JpegReconstruction;
                if (IndexOfSequence(buf, r, new byte[] { (byte)'j', (byte)'x', (byte)'l', (byte)'c' }) >= 0
                    || IndexOfSequence(buf, r, new byte[] { (byte)'j', (byte)'x', (byte)'l', (byte)'p' }) >= 0)
                    return JxlImageType.NativeCodestream;
            }
            catch { }
            return JxlImageType.Unknown;
        }

        private static bool ReadExact(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = s.Read(buf, off, count - off);
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        private static int IndexOfSequence(byte[] buffer, int length, byte[] seq)
        {
            if (seq == null || seq.Length == 0) return -1;
            for (int i = 0; i <= length - seq.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < seq.Length; j++)
                {
                    if (buffer[i + j] != seq[j]) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }
    }
}
