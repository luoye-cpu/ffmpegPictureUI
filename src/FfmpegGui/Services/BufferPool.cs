using System;
using System.Buffers;

namespace FfmpegGui.Services
{
    /// <summary>
    /// 统一的 ArrayPool 内存池管理器。
    /// 减少大尺寸 byte[]/float[] 的反复分配（降低 Gen 2 GC 压力）。
    /// </summary>
    public static class BufferPool
    {
        // 最大缓存数组长度（超过此长度直接分配，不进入池）
        private const int MaxPooledLength = 256 * 1024 * 1024; // 256MB

        /// <summary>
        /// 租借 byte 数组（至少指定长度，实际可能更大）。
        /// 超过池上限时直接分配普通数组（绝不允许返回不够长的缓冲）。
        /// 使用完必须调用 Return。
        /// </summary>
        public static byte[] RentByte(int length)
            => length > MaxPooledLength ? new byte[length] : ArrayPool<byte>.Shared.Rent(length);

        /// <summary>
        /// 归还 byte 数组（超大直配数组不入池，直接丢弃）。
        /// </summary>
        public static void ReturnByte(byte[]? buffer)
        {
            if (buffer == null || buffer.Length > MaxPooledLength) return;
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }

        /// <summary>
        /// 租借 float 数组（GainMap 管线等像素处理使用）。
        /// </summary>
        public static float[] RentFloat(int length)
            => length > MaxPooledLength / 4 ? new float[length] : ArrayPool<float>.Shared.Rent(length);

        /// <summary>
        /// 归还 float 数组。
        /// </summary>
        public static void ReturnFloat(float[]? buffer)
        {
            if (buffer == null || buffer.Length > MaxPooledLength / 4) return;
            ArrayPool<float>.Shared.Return(buffer, clearArray: false);
        }

        /// <summary>
        /// 租借 ushort 数组（16-bit 像素路径）。
        /// </summary>
        public static ushort[] RentUshort(int length)
            => length > MaxPooledLength / 2 ? new ushort[length] : ArrayPool<ushort>.Shared.Rent(length);

        /// <summary>
        /// 归还 ushort 数组。
        /// </summary>
        public static void ReturnUshort(ushort[]? buffer)
        {
            if (buffer == null || buffer.Length > MaxPooledLength / 2) return;
            ArrayPool<ushort>.Shared.Return(buffer, clearArray: false);
        }

        /// <summary>
        /// 便利方法：对字节数组执行操作并自动归还。
        /// </summary>
        public static T Using<T>(int length, Func<byte[], T> action)
        {
            var buf = RentByte(length);
            try { return action(buf); }
            finally { ReturnByte(buf); }
        }

        /// <summary>
        /// 便利方法：对浮点数组执行操作并自动归还。
        /// </summary>
        public static T UsingFloat<T>(int length, Func<float[], T> action)
        {
            var buf = RentFloat(length);
            try { return action(buf); }
            finally { ReturnFloat(buf); }
        }
    }
}