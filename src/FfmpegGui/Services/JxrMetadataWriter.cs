using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace FfmpegGui.Services;

/// <summary>
/// JXR/WDP 出口色彩标签写入器（T.832 IFD）。
///
/// JxrEncApp 只写 PIXEL_FORMAT/尺寸/图像偏移，**不会写 ICC / PTM_COLOR_INFO / COLOR_SPACE**；
/// 这会让 HDR 出口被读成 sRGB，视觉上就是过曝或反相。这里按 T.832 A.7.17–A.7.21 在文件尾部
/// 重建一份 IFD（原图数据偏移不动），再改文件头的 FIRST_IFD_OFFSET 指过去，避免搬动压缩数据。
/// </summary>
public static class JxrMetadataWriter
{
    private const ushort TagColorSpace = 0xA001;
    private const ushort TagIcc = 0x8773;
    private const ushort TagPtmColorInfo = 0xBC05;

    private const ushort TypeByte = 1;
    private const ushort TypeUndefined = 7;
    private const ushort TypeUshort = 3;

    public sealed class OutputMetadata
    {
        public byte[]? IccBytes;
        public ushort? ColorSpace;
        public byte? Primaries;
        public byte? Transfer;
        public byte? Matrix;
        public bool? FullRange;
        public string Source = "JXR 出口";
    }

    private sealed class Entry
    {
        public ushort Tag;
        public ushort Type;
        public uint Count;
        public byte[] Value = Array.Empty<byte>();
    }

    public static bool TryInject(string path, OutputMetadata meta, out string error)
    {
        error = "";
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 8 || bytes[0] != (byte)'I' || bytes[1] != (byte)'I'
                || bytes[2] != 0xBC || bytes[3] != 1)
            {
                error = "不是 II + 0xBC + version=1 的 JXR";
                return false;
            }

            int firstIfd = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
            if (firstIfd <= 0 || firstIfd + 2 > bytes.Length)
            {
                error = $"FIRST_IFD_OFFSET 越界: {firstIfd}";
                return false;
            }

            var entries = ReadIfd(bytes, firstIfd, out var readError);
            if (entries == null)
            {
                error = readError;
                return false;
            }

            // 替换/追加三类标签。ICC 先写，保证 PTM/COLOR_SPACE 的排序仍由下面统一排序处理。
            if (meta.IccBytes is { Length: > 0 })
            {
                if (meta.IccBytes.Length > 64 * 1024 * 1024)
                {
                    error = $"ICC 过大（{meta.IccBytes.Length} B > 64 MiB）";
                    return false;
                }
                Upsert(entries, new Entry { Tag = TagIcc, Type = TypeUndefined, Count = (uint)meta.IccBytes.Length, Value = meta.IccBytes });
            }
            else
                entries.RemoveAll(e => e.Tag == TagIcc);

            if (meta.Primaries.HasValue || meta.Transfer.HasValue || meta.Matrix.HasValue)
            {
                byte pr = meta.Primaries ?? 2;      // 2 = unspecified (H.273)
                byte t = meta.Transfer ?? 2;
                byte m = meta.Matrix ?? 0;         // RGB 恒等
                byte range = (byte)(meta.FullRange == true ? 1 : 0);
                var ptm = new byte[] { pr, t, m, range };
                Upsert(entries, new Entry { Tag = TagPtmColorInfo, Type = TypeByte, Count = 4, Value = ptm });
            }
            else
            {
                entries.RemoveAll(e => e.Tag == TagPtmColorInfo);
            }

            if (meta.ColorSpace.HasValue)
                Upsert(entries, new Entry { Tag = TagColorSpace, Type = TypeUshort, Count = 1,
                    Value = new byte[] { (byte)(meta.ColorSpace.Value & 0xFF), (byte)(meta.ColorSpace.Value >> 8) } });
            else
                entries.RemoveAll(e => e.Tag == TagColorSpace);

            entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));

            // 新 IFD 放文件尾部；原 IFD/图像数据均原地保留。所有偏移偶数。
            int ifdOffset = Align2(bytes.Length);
            int entryBytes = checked(2 + entries.Count * 12 + 4);
            int valueBase = Align2(ifdOffset + entryBytes);
            int valueCursor = valueBase;
            int total = valueBase;
            foreach (var e in entries)
            {
                if (e.Value.Length <= 4) continue;
                total = Align2(total + e.Value.Length);
            }
            var outBytes = new byte[total];
            Buffer.BlockCopy(bytes, 0, outBytes, 0, bytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(4), (uint)ifdOffset);

            int p = ifdOffset;
            BinaryPrimitives.WriteUInt16LittleEndian(outBytes.AsSpan(p), (ushort)entries.Count);
            p += 2;
            foreach (var e in entries)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(outBytes.AsSpan(p), e.Tag);
                BinaryPrimitives.WriteUInt16LittleEndian(outBytes.AsSpan(p + 2), e.Type);
                BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(p + 4), e.Count);
                if (e.Value.Length <= 4)
                {
                    outBytes.AsSpan(p + 8, 4).Clear();
                    Buffer.BlockCopy(e.Value, 0, outBytes, p + 8, e.Value.Length);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(p + 8), (uint)valueCursor);
                    Buffer.BlockCopy(e.Value, 0, outBytes, valueCursor, e.Value.Length);
                    valueCursor = Align2(valueCursor + e.Value.Length);
                }
                p += 12;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(p), 0);

            var tmp = path + ".meta.tmp";
            File.WriteAllBytes(tmp, outBytes);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static List<Entry>? ReadIfd(byte[] bytes, int ifdOffset, out string error)
    {
        error = "";
        if (ifdOffset + 2 > bytes.Length) { error = "IFD 头越界"; return null; }
        int n = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(ifdOffset));
        if (n <= 0 || n > 4096) { error = $"IFD 条目数异常: {n}"; return null; }
        int end = ifdOffset + 2 + n * 12;
        if (end + 4 > bytes.Length) { error = "IFD 条目区越界"; return null; }

        var list = new List<Entry>(n);
        for (int i = 0; i < n; i++)
        {
            int q = ifdOffset + 2 + i * 12;
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(q));
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(q + 2));
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(q + 4));
            int size = TypeSize(type);
            if (size <= 0) { error = $"未知 TIFF 类型 {type} (tag 0x{tag:X4})"; return null; }
            long bytesNeeded = (long)size * count;
            if (bytesNeeded < 0 || bytesNeeded > int.MaxValue) { error = $"标签 0x{tag:X4} 长度溢出"; return null; }
            int valueLen = (int)bytesNeeded;
            var value = new byte[valueLen];
            if (valueLen <= 4)
            {
                Buffer.BlockCopy(bytes, q + 8, value, 0, valueLen);
            }
            else
            {
                uint off = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(q + 8));
                if (off > bytes.Length || (long)off + valueLen > bytes.Length)
                {
                    error = $"标签 0x{tag:X4} 值偏移越界: off={off} len={valueLen}";
                    return null;
                }
                Buffer.BlockCopy(bytes, (int)off, value, 0, valueLen);
            }
            list.Add(new Entry { Tag = tag, Type = type, Count = count, Value = value });
        }
        return list;
    }

    private static void Upsert(List<Entry> list, Entry e)
    {
        int idx = list.FindIndex(x => x.Tag == e.Tag);
        if (idx >= 0) list[idx] = e;
        else list.Add(e);
    }

    private static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,   // BYTE/ASCII/SBYTE/UNDEFINED
        3 or 8 => 2,            // SHORT/SSHORT
        4 or 9 or 11 => 4,      // LONG/SLONG/FLOAT
        5 or 10 or 12 => 8,     // RATIONAL/SRATIONAL/DOUBLE
        _ => 0,
    };

    private static int Align2(int v) => (v + 1) & ~1;
}
