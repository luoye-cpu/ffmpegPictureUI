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
    /// 快速检测 JXL 文件类型（启发式）：区分是否包含 JPEG 重构数据（jbrd）或为原生 codestream。
    /// 说明：这是轻量级检测，遇到不确定情况应使用 libjxl API 做准确判断。
    /// </summary>
    public static class JxlInspector
    {
        public static JxlImageType DetectType(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                var buf = new byte[65536];
                var r = fs.Read(buf, 0, buf.Length);
                if (r < 4) return JxlImageType.Unknown;

                // ISOBMFF-based container: bytes 4..8 == "JXL "
                if (r >= 12 && buf[4] == (byte)'J' && buf[5] == (byte)'X' && buf[6] == (byte)'L' && buf[7] == (byte)' ')
                {
                    // 在字节缓冲区中查找关键 box 名称，避免将整个缓冲区转换为字符串
                    if (IndexOfSequence(buf, r, new byte[] { (byte)'j', (byte)'b', (byte)'r', (byte)'d' }) >= 0)
                        return JxlImageType.JpegReconstruction;
                    if (IndexOfSequence(buf, r, new byte[] { (byte)'j', (byte)'x', (byte)'l', (byte)'c' }) >= 0)
                        return JxlImageType.NativeCodestream;
                    return JxlImageType.NativeCodestream;
                }

                // Naked codestream magic: 0xFF 0x0A
                if (buf[0] == 0xFF && buf[1] == 0x0A) return JxlImageType.NativeCodestream;
            }
            catch { }
            return JxlImageType.Unknown;
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
