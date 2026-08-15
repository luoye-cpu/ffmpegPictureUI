// pngcicp — PNG 3.0 cICP/sBIT chunk 插入/查看工具
// 用法:
//   pngcicp cicp <in.png> <out.png> <primaries> <transfer> [matrix] [full]
//   pngcicp sbit <in.png> <out.png> <r> <g> <b>
//   pngcicp info <in.png>
// 色彩参数:
//   primaries: 9=BT.2020, 12=Display P3 (SMPTE 432), 1=sRGB/BT.709
//   transfer : 16=PQ (SMPTE 2084), 18=HLG (ARIB STD-B67), 13=sRGB, 1=BT.709
//   matrix   : 0=RGB (PNG 必须为 0)
//   full-range: 1=full (PNG 必须为 1)
using System.Buffers.Binary;
using System.Text;

if (args.Length < 2) { PrintUsage(); return 1; }

var cmd = args[0].ToLowerInvariant();
var path = args[1];

try
{
    if (cmd == "info")
    {
        var data = File.ReadAllBytes(path);
        Console.WriteLine($"File: {path} ({data.Length} bytes)");
        if (data.Length < 8 || data[0] != 0x89 || data[1] != 0x50) { Console.WriteLine("Not a PNG file"); return 1; }
        DumpChunks(data);
        return 0;
    }
    if (cmd == "cicp")
    {
        if (args.Length < 5) { PrintUsage(); return 1; }
        var dst = args[2];
        byte primaries = byte.Parse(args[3]);
        byte transfer = byte.Parse(args[4]);
        byte matrix = args.Length > 5 ? byte.Parse(args[5]) : (byte)0;
        byte fullRange = args.Length > 6 ? byte.Parse(args[6]) : (byte)1;

        var data = File.ReadAllBytes(path);
        var chunk = BuildChunk("cICP", new byte[] { primaries, transfer, matrix, fullRange });
        var merged = InsertChunk(data, "cICP", chunk);
        File.WriteAllBytes(dst, merged);
        Console.WriteLine($"Inserted cICP: primaries={primaries} transfer={transfer} matrix={matrix} full={fullRange}");
        Console.WriteLine($"Written: {dst} ({merged.Length} bytes)");
        return 0;
    }
    if (cmd == "sbit")
    {
        if (args.Length < 6) { PrintUsage(); return 1; }
        var dst = args[2];
        byte r = byte.Parse(args[3]);
        byte g = byte.Parse(args[4]);
        byte b = byte.Parse(args[5]);
        var data = File.ReadAllBytes(path);
        var chunk = BuildChunk("sBIT", new byte[] { r, g, b });
        var merged = InsertChunk(data, "sBIT", chunk);
        File.WriteAllBytes(dst, merged);
        Console.WriteLine($"Inserted sBIT: R={r} G={g} B={b} (16-bit container, {r}-bit significant)");
        Console.WriteLine($"Written: {dst} ({merged.Length} bytes)");
        return 0;
    }
    PrintUsage();
    return 1;
}
catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); Console.Error.WriteLine(ex.StackTrace); return 1; }

static void PrintUsage()
{
    Console.WriteLine("pngcicp — PNG 3.0 cICP/sBIT chunk tool");
    Console.WriteLine("  cicp <in.png> <out.png> <primaries> <transfer> [matrix] [full]");
    Console.WriteLine("  sbit <in.png> <out.png> <r> <g> <b>");
    Console.WriteLine("  info <in.png>");
}

static void DumpChunks(byte[] data)
{
    int pos = 8;
    while (pos + 12 <= data.Length)
    {
        int len = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
        var type = Encoding.ASCII.GetString(data, pos + 4, 4);
        if (type == "cICP" && len >= 4)
            Console.WriteLine($"  cICP: primaries={data[pos+8]} transfer={data[pos+9]} matrix={data[pos+10]} full={data[pos+11]}");
        else if (type == "sBIT")
            Console.WriteLine($"  sBIT: R={data[pos+8]} G={data[pos+9]} B={data[pos+10]}");
        else if (type == "mDCV")
            Console.WriteLine($"  mDCV: {len} bytes (SMPTE ST 2086 mastering display)");
        else if (type == "cLLI")
            Console.WriteLine($"  cLLI: MaxCLL={BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos+8))} MaxFALL={BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos+10))}");
        else if (type is "iCCP" or "sRGB" or "gAMA" or "cHRM" or "IHDR" or "IDAT" or "IEND" or "PLTE" or "tEXt" or "pHYs")
            Console.WriteLine($"  {type}: {len} bytes");
        else
            Console.WriteLine($"  {type}: {len} bytes");
        if (type == "IEND") break;
        pos += 12 + len;
    }
}

/// <summary>在 IHDR 后插入 chunk；若已存在同名 chunk 先移除（幂等）。</summary>
static byte[] InsertChunk(byte[] data, string typeName, byte[] chunk)
{
    // 解析 IHDR 结束位置
    int sigEnd = 8;
    int len = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(sigEnd));
    int ihdrEnd = sigEnd + 12 + len;

    using var ms = new MemoryStream(data.Length + chunk.Length + 16);
    ms.Write(data, 0, ihdrEnd);
    // 移除同名旧 chunk，其余按原序复制
    int pos = ihdrEnd;
    while (pos + 12 <= data.Length)
    {
        int clen = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
        var ctype = Encoding.ASCII.GetString(data, pos + 4, 4);
        if (ctype == typeName) { pos += 12 + clen; continue; }
        ms.Write(data, pos, 12 + clen);
        pos += 12 + clen;
    }
    var buf = ms.ToArray();
    using var outMs = new MemoryStream(buf.Length + chunk.Length);
    outMs.Write(buf, 0, ihdrEnd);
    outMs.Write(chunk);
    outMs.Write(buf, ihdrEnd, buf.Length - ihdrEnd);
    return outMs.ToArray();
}

static (byte[] head, byte[] tail, int sigEnd) SplitAfterIhdr(byte[] data)
{
    int pos = 8;
    int len = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
    int ihdrEnd = pos + 12 + len;
    return (data[0..ihdrEnd], data[ihdrEnd..], ihdrEnd);
}

static byte[] BuildChunk(string typeName, byte[] payload)
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
    BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(chunk.Length - 4), crc);
    return chunk;
}

static uint Crc32(byte[] data)
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
