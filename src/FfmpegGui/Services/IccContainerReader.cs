using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FfmpegGui.Services;

/// <summary>Bounded, byte-preserving ICC reads for containers whose metadata tools are optional.</summary>
internal static class IccContainerReader
{
    private const int MaxProfileBytes = 32 * 1024 * 1024;
    private const int MaxEntries = 65536;

    // true distinguishes a readable container with no ICC from an unavailable/failed reader.
    internal static bool TryRead(string path, out byte[]? profile)
    {
        profile = null;
        try
        {
            using var file = File.OpenRead(path);
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".jpg": case ".jpeg": case ".jpegli":
                    return ReadJpeg(file, out profile);
                case ".webp":
                    return ReadWebp(file, out profile);
                case ".tif": case ".tiff":
                    return ReadTiff(file, out profile);
                default: return false;
            }
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (OverflowException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static bool ReadJpeg(Stream file, out byte[]? profile)
    {
        profile = null;
        if (file.ReadByte() != 0xff || file.ReadByte() != 0xd8) return false;
        var chunks = new SortedDictionary<int, byte[]>();
        int chunkCount = 0, total = 0;
        var sizeBytes = new byte[2];
        for (int entries = 0; entries < MaxEntries; entries++)
        {
            if (file.ReadByte() != 0xff) return false;
            int marker;
            do { marker = file.ReadByte(); } while (marker == 0xff);
            if (marker < 0 || marker == 0) return false;
            if (marker is 0xd9 or 0xda)
            {
                if (chunks.Count == 0) return true;
                if (chunkCount != chunks.Count) return false;
                profile = new byte[total];
                int cursor = 0;
                for (int i = 1; i <= chunkCount; i++)
                {
                    if (!chunks.TryGetValue(i, out var bytes)) return false;
                    bytes.CopyTo(profile, cursor); cursor += bytes.Length;
                }
                return true;
            }
            if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
            file.ReadExactly(sizeBytes);
            int length = BinaryPrimitives.ReadUInt16BigEndian(sizeBytes) - 2;
            if (length < 0 || length > file.Length - file.Position) return false;
            if (marker != 0xe2 || length < 14) { file.Seek(length, SeekOrigin.Current); continue; }
            var bytesInSegment = new byte[length];
            file.ReadExactly(bytesInSegment);
            if (!bytesInSegment.AsSpan(0, 12).SequenceEqual("ICC_PROFILE\0"u8)) continue;
            int index = bytesInSegment[12], count = bytesInSegment[13];
            if (index == 0 || count == 0 || index > count || (chunkCount != 0 && chunkCount != count)
                || chunks.ContainsKey(index)) return false;
            chunkCount = count;
            total = checked(total + length - 14);
            if (total > MaxProfileBytes) return false;
            chunks.Add(index, bytesInSegment.AsSpan(14).ToArray());
        }
        return false;
    }

    private static bool ReadWebp(Stream file, out byte[]? profile)
    {
        profile = null;
        var header = new byte[12]; file.ReadExactly(header);
        if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return false;
        long end = (long)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4)) + 8;
        if (end < 12 || end > file.Length) return false;
        var chunk = new byte[8]; bool foundImage = false;
        for (int entries = 0; file.Position < end && entries < MaxEntries; entries++)
        {
            if (end - file.Position < 8) return false;
            file.ReadExactly(chunk);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4, 4));
            long padded = (long)size + (size & 1);
            if (padded > end - file.Position) return false;
            string type = Encoding.ASCII.GetString(chunk, 0, 4);
            foundImage |= type is "VP8 " or "VP8L" or "ANIM";
            if (type == "ICCP")
            {
                if (profile != null || size < 128 || size > MaxProfileBytes) return false;
                profile = new byte[(int)size]; file.ReadExactly(profile);
                if ((size & 1) != 0) file.ReadByte();
            }
            else file.Seek(padded, SeekOrigin.Current);
        }
        return file.Position == end && foundImage;
    }

    // This path-level reader describes the default decoded image (IFD0), not the TIFF
    // document as a whole. ICC tags belong to individual IFDs; borrowing another page's
    // profile would falsely describe the first page. A readable IFD0 without ICC must
    // therefore return true/null and must not trigger a document-wide ExifTool fallback.
    private static bool ReadTiff(Stream file, out byte[]? profile)
    {
        profile = null;
        var header = new byte[16];
        if (file.Length < 8) return false;
        file.ReadExactly(header.AsSpan(0, 8));
        bool little;
        if (header[0] == 'I' && header[1] == 'I') little = true;
        else if (header[0] == 'M' && header[1] == 'M') little = false;
        else return false;
        ushort U16(ReadOnlySpan<byte> b) => little ? BinaryPrimitives.ReadUInt16LittleEndian(b) : BinaryPrimitives.ReadUInt16BigEndian(b);
        uint U32(ReadOnlySpan<byte> b) => little ? BinaryPrimitives.ReadUInt32LittleEndian(b) : BinaryPrimitives.ReadUInt32BigEndian(b);
        ulong U64(ReadOnlySpan<byte> b) => little ? BinaryPrimitives.ReadUInt64LittleEndian(b) : BinaryPrimitives.ReadUInt64BigEndian(b);
        int version = U16(header.AsSpan(2, 2));
        bool big = version == 43;
        ulong offset;
        if (version == 42) offset = U32(header.AsSpan(4, 4));
        else if (big && U16(header.AsSpan(4, 2)) == 8 && U16(header.AsSpan(6, 2)) == 0)
        {
            file.ReadExactly(header.AsSpan(8, 8)); offset = U64(header.AsSpan(8, 8));
        }
        else return false;
        int countSize = big ? 8 : 2, entrySize = big ? 20 : 12, pointerSize = big ? 8 : 4;
        int headerSize = big ? 16 : 8;
        if (offset < (uint)headerSize || offset > (ulong)file.Length
            || (ulong)file.Length - offset < (uint)countSize) return false;
        file.Position = (long)offset;
        var buffer = new byte[20]; file.ReadExactly(buffer.AsSpan(0, countSize));
        ulong count = big ? U64(buffer.AsSpan(0, 8)) : U16(buffer.AsSpan(0, 2));
        // The next-IFD pointer is part of this directory even when it is zero. Missing
        // bytes mean unreadable, not proof that the first page has no profile.
        if (count > MaxEntries || count * (uint)entrySize + (uint)pointerSize > (ulong)(file.Length - file.Position)) return false;
        for (ulong i = 0; i < count; i++)
        {
            file.ReadExactly(buffer.AsSpan(0, entrySize));
            if (U16(buffer.AsSpan(0, 2)) != 34675) continue;
            if (profile != null || U16(buffer.AsSpan(2, 2)) is not (1 or 7)) return false;
            ulong size = big ? U64(buffer.AsSpan(4, 8)) : U32(buffer.AsSpan(4, 4));
            ulong valueOffset = big ? U64(buffer.AsSpan(12, 8)) : U32(buffer.AsSpan(8, 4));
            if (size < 128 || size > MaxProfileBytes || valueOffset > (ulong)file.Length || size > (ulong)file.Length - valueOffset) return false;
            long next = file.Position; file.Position = (long)valueOffset;
            profile = new byte[(int)size]; file.ReadExactly(profile); file.Position = next;
        }
        file.ReadExactly(buffer.AsSpan(0, pointerSize));
        ulong nextIfd = big ? U64(buffer.AsSpan(0, 8)) : U32(buffer.AsSpan(0, 4));
        // Validate the link's minimum bounds without selecting/scanning another page.
        return nextIfd == 0 || (nextIfd >= (uint)headerSize && nextIfd != offset
            && nextIfd <= (ulong)file.Length && (ulong)file.Length - nextIfd >= (uint)countSize);
    }
}
