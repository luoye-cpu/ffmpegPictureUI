// dngjxlprobe — 从 DNG 提取 JXL tile 数据并输出为 .jxl 文件
// 用法: dngjxlprobe <input.dng> <output.jxl> [tileIndex]
// 通过解析 TIFF IFD 标签 (273=TileOffsets, 279=TileByteCounts) 提取
using System.Buffers.Binary;
using System.Text;

if (args.Length < 2) { Console.WriteLine("usage: dngjxlprobe <in.dng> <out.jxl> [tileIndex]"); return 1; }

var data = File.ReadAllBytes(args[0]);
int tileIdx = args.Length > 2 ? int.Parse(args[2]) : 0;

// TIFF header
if (data.Length < 8 || (data[0] != 'I' && data[0] != 'M')) { Console.WriteLine("not TIFF"); return 1; }
bool isLE = data[0] == 'I';
int ifdOffset = isLE ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4))
                     : BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4));

// 遍历 IFD 条目找 TileOffsets(273)/TileByteCounts(279)/SubIFDs(330)
uint[]? tileOffsets = null, tileCounts = null;
var subIfds = new List<uint>();

void ParseIfd(int ifdOff)
{
    if (ifdOff + 2 > data.Length) return;
    int entryCount = isLE ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ifdOff))
                          : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(ifdOff));
    int p = ifdOff + 2;
    for (int i = 0; i < entryCount && p + 12 <= data.Length; i++, p += 12)
    {
        ushort tag = isLE ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p))
                          : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p));
        ushort type = isLE ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 2))
                           : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p + 2));
        uint count = isLE ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 4))
                          : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p + 4));
        uint valueOff = isLE ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 8))
                             : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p + 8));
        int typeSize = type switch { 1 => 1, 3 => 2, 4 => 4, 5 => 8, 16 => 8, _ => 1 };
        long valueBytes = (long)count * typeSize;

        byte[] GetValueBytes()
        {
            if (valueBytes <= 4)
            {
                var b = new byte[4];
                for (int k = 0; k < valueBytes; k++) b[k] = data[p + 8 + k];
                return b;
            }
            return data[(int)valueOff..(int)(valueOff + valueBytes)];
        }

        if (tag == 273)
        {
            var raw = GetValueBytes();
            tileOffsets = new uint[count];
            for (int k = 0; k < count; k++)
                tileOffsets[k] = isLE ? BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(k * 4))
                                      : BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(k * 4));
        }
        else if (tag == 279)
        {
            var raw = GetValueBytes();
            tileCounts = new uint[count];
            for (int k = 0; k < count; k++)
                tileCounts[k] = isLE ? BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(k * 4))
                                     : BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(k * 4));
        }
        else if (tag == 330 && type == 4)
        {
            var raw = GetValueBytes();
            for (int k = 0; k < count; k++)
                subIfds.Add(isLE ? BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(k * 4))
                                 : BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(k * 4)));
        }
    }
}

ParseIfd(ifdOffset);
// 递归解析 SubIFD (最多 3 层)
for (int depth = 0; depth < 3 && subIfds.Count > 0; depth++)
{
    var next = new List<uint>();
    foreach (var off in subIfds)
    {
        ParseIfd((int)off);
        if (tileOffsets != null) break;
    }
    if (tileOffsets != null) break;
    // 下一层 SubIFD 收集
    subIfds = next;
    _ = subIfds;
}

if (tileOffsets == null || tileCounts == null || tileIdx >= tileOffsets.Length)
{
    Console.WriteLine($"tiles not found (offsets={tileOffsets?.Length} counts={tileCounts?.Length})");
    return 1;
}

var tile = data[(int)tileOffsets[tileIdx]..(int)(tileOffsets[tileIdx] + tileCounts[tileIdx])];
File.WriteAllBytes(args[1], tile);
Console.WriteLine($"tile[{tileIdx}] {tileCounts[tileIdx]} bytes -> {args[1]}");
int show = Math.Min(32, tile.Length);
Console.WriteLine($"header: {Convert.ToHexString(tile[..show])}");
// 检查 JXL codestream magic (FF 0A / FF 0D) 或 container (0000000C 6A584C20)
bool isCode = tile.Length > 1 && (tile[0] == 0xFF && (tile[1] == 0x0A || tile[1] == 0x0D));
bool isBox = tile.Length > 12 && tile[4] == 0x6A && tile[5] == 0x58 && tile[6] == 0x4C && tile[7] == 0x20;
Console.WriteLine($"JXL codestream: {isCode}, JXL container: {isBox}");
return 0;
