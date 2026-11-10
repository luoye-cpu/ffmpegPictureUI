using System;
using System.Text;

namespace FfmpegGui.Services;

/// <summary>AppleMakerNote 两个增益图 tag 的读数（tag 0x0021 / 0x0030 的解码值）。</summary>
public sealed class AppleMakerNoteTags
{
    /// <summary>tag 0x0021 = HDRHeadroom（exiftool Apple.pm 的 MeteorHeadroom；A 件实测 1.5）。</summary>
    public float Tag33HdrHeadroom;
    /// <summary>tag 0x0030 = HDRGain（exiftool Apple.pm 的 MeteorPlusGainMap；A 件实测 0.5）。</summary>
    public float Tag48HdrGain;
}

/// <summary>
/// **纯托管 AppleMakerNote 解析器**（AU2-X1，2026-11-10）—— 从 HEIF Exif item 字节直接解出
/// Apple HDR 增益图的 maker tag 33/48，**零子进程**（替代此前整件喂 exiftool 的唯一通路；
/// exiftool 通道保留为 fallback，见 <see cref="GainMapDecoder.TryReadAppleMakerNoteMetadataAsync"/>）。
///
/// <para>
/// 解析规格 = AU-W2 合成器 <c>tests/scripts/_make-synth-aux-gainmap.py</c> 写入逻辑的**逆向**
/// （<c>build_apple_exif</c>，:104-126 实测调通的结构 —— exiftool 13.61 能读出 1.5/0.5）：
/// </para>
/// <code>
///   Exif item data:
///     u32 tiff_header_offset（BE，A 件 = 6）        ⇒ TIFF 头在 item + 4 + offset
///     [对齐头 "Exif\0\0"（6 B，被上面 4 B 计）]
///     TIFF header @ +4+offset:
///       'MM'（big-endian）或 'II'（little-endian）  ⇒ **双编码容忍**（本解析器两种都解）
///       u16 magic = 42
///       u32 IFD0 offset（相对 TIFF 头）
///     IFD0: tag 0x8769（ExifIFD pointer，LONG，值 = offset 相对 TIFF 头）
///     ExifIFD: tag 0x927C（MakerNote，UNDEFINED，count = 长度，值 = offset 相对 TIFF 头）
///     MakerNote:
///       前 10 字节 = "Apple iOS\0"                  ⇒ 签名验证（不匹配 ⇒ fail-closed）
///       IFD 从 MakerNote 起点后 14 字节             ⇒ exiftool MakerNotes.pm `Start => $valuePtr + 14`
///       **entry 的值 offset 相对 MakerNote 起点**   ⇒ exiftool `Base => $start - 14`（$start = IFD 绝对
///                                                     地址 ⇒ Base = IFD绝对 − 14 = MN 起点；实测校准：
///                                                     A 件 entry offset 56 ⇒ 值在 MN起点+56）
///       tag 0x0021 / 0x0030：**双编码容忍** ——
///         TIFF type 5（RATIONAL64，count 1 ⇒ 8 B @ MN+offset，u32 num + u32 den）与
///         TIFF type 11（FLOAT32，count 1 ⇒ 4 B IEEE754，inline 于 entry 的 value 字段）。
///         官方文档 float32 / exiftool Writable=rational64s / AU-W2 合成件 rational64s —— 不同
///         iOS 版本与工具链两态都真实存在，解析端必须都认。
/// </code>
///
/// <para>
/// ⚠ **错误处理契约**：签名不匹配 / IFD 越界 / tag 缺失 / 字节序标识缺失 ⇒ 返回 <c>null</c> +
/// <paramref name="failReason"/> 点名，**绝不抛异常** —— 调用方（GainMapDecoder）据此降级
/// exiftool 整件通道；「解析不出来」必须可见而不是崩任务。
/// </para>
/// </summary>
public static class AppleMakerNoteParser
{
    /// <summary>AppleMakerNote 签名（exiftool MakerNotes.pm 的判定条件，前 10 字节）。</summary>
    private static readonly byte[] AppleSignature = Encoding.ASCII.GetBytes("Apple iOS\0");

    /// <summary>
    /// 解析 HEIF Exif item 字节，提取 AppleMakerNote tag 33/48。
    /// 成功 ⇒ 返回读数；任何一步不成立 ⇒ <c>null</c> + <paramref name="failReason"/> 点名（不抛）。
    /// </summary>
    public static AppleMakerNoteTags? TryParse(byte[]? exifItemData, out string failReason)
    {
        failReason = "";
        try
        {
            if (exifItemData == null || exifItemData.Length < 12)
            {
                failReason = $"Exif item 过短（{exifItemData?.Length ?? 0} B < 12）";
                return null;
            }

            // ① u32 tiff_header_offset（恒 big-endian，HEIF 规范）⇒ TIFF 头在 item + 4 + offset。
            //   （A 件：offset=6，前缀 u32(6) + "Exif\0\0" ⇒ TIFF 头在 +10 —— exiftool QuickTime.pm
            //   `start = 4 + n` 的同款读法，2026-11-10 实测调通的布局。）
            int tiffHeaderOffset = (exifItemData[0] << 24) | (exifItemData[1] << 16)
                                   | (exifItemData[2] << 8) | exifItemData[3];
            int tiff = 4 + tiffHeaderOffset;
            if (tiff + 8 > exifItemData.Length)
            {
                failReason = $"tiff_header_offset={tiffHeaderOffset} 越界（item {exifItemData.Length} B）";
                return null;
            }

            // ② TIFF 头：'MM' big-endian / 'II' little-endian（双编码容忍），magic=42。
            bool bigEndian;
            if (exifItemData[tiff] == (byte)'M' && exifItemData[tiff + 1] == (byte)'M') bigEndian = true;
            else if (exifItemData[tiff] == (byte)'I' && exifItemData[tiff + 1] == (byte)'I') bigEndian = false;
            else
            {
                failReason = $"TIFF 字节序标识缺失（0x{exifItemData[tiff]:X2}{exifItemData[tiff + 1]:X2}，非 MM/II）";
                return null;
            }
            if (U16(exifItemData, tiff + 2, bigEndian) != 42)
            {
                failReason = $"TIFF magic ≠ 42（实 {U16(exifItemData, tiff + 2, bigEndian)}）";
                return null;
            }
            int ifd0Off = U32(exifItemData, tiff + 4, bigEndian);

            // ③ IFD0 → 0x8769 ExifIFD pointer（LONG，值 offset 相对 TIFF 头）。
            int exifIfdOff = FindLongEntry(exifItemData, tiff, ifd0Off, 0x8769, bigEndian, out string? e1);
            if (exifIfdOff < 0) { failReason = $"IFD0 无 ExifIFD pointer（0x8769）：{e1}"; return null; }

            // ④ ExifIFD → 0x927C MakerNote（UNDEFINED，值 offset 相对 TIFF 头，count = 长度）。
            int mnOff = FindUndefinedEntry(exifItemData, tiff, exifIfdOff, 0x927C, bigEndian,
                out int mnLen, out string? e2);
            if (mnOff < 0) { failReason = $"ExifIFD 无 MakerNote（0x927C）：{e2}"; return null; }
            if (mnLen < 16)
            {
                failReason = $"MakerNote 过短（{mnLen} B < 16，装不下签名+IFD 头）";
                return null;
            }

            // ⑤ Apple 签名验证（前 10 字节 "Apple iOS\0"）。
            for (int i = 0; i < AppleSignature.Length; i++)
            {
                if (exifItemData[mnOff + i] != AppleSignature[i])
                {
                    failReason = $"MakerNote 签名不匹配（byte {i}: 0x{exifItemData[mnOff + i]:X2} ≠ 0x{AppleSignature[i]:X2}）";
                    return null;
                }
            }

            // ⑥ MakerNote 内部 IFD：从 MN 起点 +14（Apple 形态），entry 值 offset 相对 **MN 起点**。
            //   （exiftool MakerNotes.pm：`Start => $valuePtr + 14` + `Base => $start - 14` —— $start 是
            //   IFD 的绝对地址 ⇒ Base = MN 起点；A 件实测 entry offset 56 ⇒ 值在 MN起点+56。）
            int mnIfd = mnOff + 14;
            // tag 0x0021：type 5 RATIONAL（值区 8 B @ MN+off）或 type 11 FLOAT32（inline）。
            if (!ReadTagFloat(exifItemData, mnIfd, mnOff, 0x0021, bigEndian, out float m33, out string? e33))
            { failReason = $"tag 0x0021（HDRHeadroom）读取失败：{e33}"; return null; }
            if (!ReadTagFloat(exifItemData, mnIfd, mnOff, 0x0030, bigEndian, out float m48, out string? e48))
            { failReason = $"tag 0x0030（HDRGain）读取失败：{e48}"; return null; }

            return new AppleMakerNoteTags { Tag33HdrHeadroom = m33, Tag48HdrGain = m48 };
        }
        catch (Exception ex)
        {
            failReason = $"解析异常 {ex.GetType().Name}（防御性兜底，不外抛）";
            return null;
        }
    }

    // ═══════════════════════════════════════════
    //  TIFF 结构读数（全部带越界保护，越界 ⇒ 负值/失败）
    // ═══════════════════════════════════════════

    private static int U16(byte[] d, int o, bool be) => be
        ? (d[o] << 8) | d[o + 1]
        : d[o] | (d[o + 1] << 8);

    private static int U32(byte[] d, int o, bool be) => be
        ? (int)((uint)d[o] << 24 | (uint)d[o + 1] << 16 | (uint)d[o + 2] << 8 | d[o + 3])
        : (int)((uint)d[o] | (uint)d[o + 1] << 8 | (uint)d[o + 2] << 16 | (uint)d[o + 3] << 24);

    /// <summary>
    /// 在 IFD 里找指定 tag，返回 LONG（type 4）count=1 的 inline 值（offset 语义由调用方解释）。
    /// 找不到 / 类型不符 ⇒ 负值 + 点名。
    /// </summary>
    private static int FindLongEntry(byte[] d, int tiffBase, int ifdRel, int tag, bool be, out string? err)
    {
        err = null;
        int ifd = tiffBase + ifdRel;
        if (ifd + 2 > d.Length) { err = $"IFD@+{ifdRel} 越界"; return -1; }
        int count = U16(d, ifd, be);
        int p = ifd + 2;
        for (int i = 0; i < count; i++, p += 12)
        {
            if (p + 12 > d.Length) { err = $"IFD@+{ifdRel} entry {i} 越界"; return -1; }
            if (U16(d, p, be) != tag) continue;
            int type = U16(d, p + 2, be);
            int cnt = U32(d, p + 4, be);
            if (type != 4 || cnt < 1) { err = $"tag 0x{tag:X4} type={type} count={cnt}（需 LONG×1）"; return -1; }
            return U32(d, p + 8, be);                       // LONG count=1 ⇒ inline
        }
        err = $"IFD@+{ifdRel} 共 {count} entry 无 tag 0x{tag:X4}";
        return -1;
    }

    /// <summary>
    /// 在 IFD 里找指定 tag 的 UNDEFINED（type 7）值：返回值区绝对 offset 与长度（count）。
    /// count 大于 4 ⇒ value 字段是 offset（相对 TIFF 头）；≤ 4 ⇒ inline（MakerNote 本体在 value 字段里，
    /// 极罕见形态，此处按 offset 语义统一处理——合成件与真机件 count 都远大于 4）。
    /// </summary>
    private static int FindUndefinedEntry(byte[] d, int tiffBase, int ifdRel, int tag, bool be,
        out int length, out string? err)
    {
        length = 0; err = null;
        int ifd = tiffBase + ifdRel;
        if (ifd + 2 > d.Length) { err = $"IFD@+{ifdRel} 越界"; return -1; }
        int count = U16(d, ifd, be);
        int p = ifd + 2;
        for (int i = 0; i < count; i++, p += 12)
        {
            if (p + 12 > d.Length) { err = $"IFD@+{ifdRel} entry {i} 越界"; return -1; }
            if (U16(d, p, be) != tag) continue;
            int type = U16(d, p + 2, be);
            int cnt = U32(d, p + 4, be);
            if (cnt <= 0) { err = $"tag 0x{tag:X4} count={cnt}"; return -1; }
            int off = U32(d, p + 8, be);
            if (tiffBase + off < 0 || tiffBase + off + cnt > d.Length)
            { err = $"tag 0x{tag:X4} 值区越界（off={off} len={cnt}）"; return -1; }
            length = cnt;
            return tiffBase + off;                          // 绝对 offset
        }
        err = $"IFD@+{ifdRel} 共 {count} entry 无 tag 0x{tag:X4}";
        return -1;
    }

    /// <summary>
    /// 读 MakerNote IFD 里的一个 float tag（0x0021 / 0x0030）。
    /// **双编码容忍**：type 5（RATIONAL64，num/den）与 type 11（FLOAT32 IEEE754）都解析。
    /// 值区基准：<paramref name="mnBase"/>（= MakerNote 绝对起点）+ entry 的 offset 字段。
    /// </summary>
    private static bool ReadTagFloat(byte[] d, int mnIfd, int mnBase, int tag, bool be,
        out float value, out string? err)
    {
        value = 0; err = null;
        if (mnIfd + 2 > d.Length) { err = "MakerNote IFD 越界"; return false; }
        int count = U16(d, mnIfd, be);
        int p = mnIfd + 2;
        for (int i = 0; i < count; i++, p += 12)
        {
            if (p + 12 > d.Length) { err = $"MakerNote IFD entry {i} 越界"; return false; }
            if (U16(d, p, be) != tag) continue;
            int type = U16(d, p + 2, be);
            int cnt = U32(d, p + 4, be);
            if (cnt < 1) { err = $"tag 0x{tag:X4} count={cnt}"; return false; }

            if (type == 5)
            {
                // RATIONAL64：8 B（u32 num + u32 den）。count=1 ⇒ 8 B > 4 B value 字段 ⇒ 值区在 offset 处；
                // count>1 只取第一组（本仓消费语义 = 单值）。
                int off = U32(d, p + 8, be);
                int v = mnBase + off;
                if (v < 0 || v + 8 > d.Length) { err = $"tag 0x{tag:X4} 值区越界（MN+{off}）"; return false; }
                int num = U32(d, v, be), den = U32(d, v + 4, be);
                if (den == 0) { err = $"tag 0x{tag:X4} RATIONAL 分母为 0"; return false; }
                value = (float)((double)num / den);
                return true;
            }
            if (type == 11)
            {
                // FLOAT32：4 B IEEE754。count=1 ⇒ 4 B ≤ 4 B value 字段 ⇒ **inline**（endian 跟 TIFF 头）。
                // count>1（值区在 offset 处）同样容忍——真机 float32 件的潜在形态。
                float fv;
                if (cnt == 1)
                {
                    fv = GetFloat(d, p + 8, be);
                }
                else
                {
                    int off = U32(d, p + 8, be);
                    int v = mnBase + off;
                    if (v < 0 || v + 4 > d.Length) { err = $"tag 0x{tag:X4} 值区越界（MN+{off}）"; return false; }
                    fv = GetFloat(d, v, be);
                }
                value = fv;
                return true;
            }
            err = $"tag 0x{tag:X4} 编码 type={type}（容忍 RATIONAL(5)/FLOAT(11)，实 {type}）";
            return false;
        }
        err = $"MakerNote IFD 共 {count} entry 无 tag 0x{tag:X4}";
        return false;
    }

    private static float GetFloat(byte[] d, int o, bool be)
    {
        int bits = be
            ? (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]
            : d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24);
        return BitConverter.Int32BitsToSingle(bits);
    }
}