using System;

namespace FfmpegGui.Services;

/// <summary>
/// JXL（JPEG XL）增益图**容器写出器** —— 纯托管、零外部依赖（2026-11-08 写侧批次，用户裁决
/// 按「解析级读者」标准开闸）。
///
/// <para>
/// 存在的理由：与 AVIF 出口的 <see cref="IsoBmffGainMapWriter"/> 同构 —— cjxl CLI **没有**
/// 注入增益图的开关（vendored <c>tools/cjxl_main.cc</c> 对 `jhgm` 只在 JXL→JXL 重编码时透传、
/// JPEG→JXL 不解析 ISO 21496-1），而 libjxl 的 C API 在随包产物里是静态 exe（无 libjxl.dll 可
/// P/Invoke）⇒ 唯一不动工具链的写出路径就是**自己组装容器**。
/// </para>
///
/// <para>
/// 载荷字段序（vendored <c>tools/src/libjxl/lib/extras/gain_map.cc:118-145</c>，2026-11-08
/// 正控实测逐字节复刻——随包 jxlinfo 0.12.0 解析通过）：
/// <code>
/// version(1B)=0 + BE16 元数据长 + ISO 21496-1 blob
///   + 颜色编码长(1B)=0 + BE32 alt ICC 长(4B)=0 + 增益图(剩余全部)
/// </code>
/// ⚠ **增益图尺寸不落盘**：读侧按盒剩余字节推出（gain_map.cc:222-226）⇒ jhgm 载荷里
/// 没有「增益图尺寸」字段，也就无法在写出器内复核「增益图 ≤ 底图」这类形状约束
///（AVIF 的 tmap 有 ispe/pixi 可核，这里没有）——调用方纪律兜底，读侧探测落地后事后核对。
/// </para>
///
/// <para>
/// 容器 = sig(12B) + ftyp(20B) + **jhgm** + jxlc。⚠ **jhgm 必须在 jxlc 之前**：
/// vendored <c>lib/extras/enc/jxl.cc:331-350</c> 的顺序就是先 box 后帧；zenjxl issue #22 实测
/// 码流 &gt;4096B 时**尾随** box 会被部分读者漏读。
/// </para>
///
/// <para>
/// 形状对齐 rawmakase PR #912 先例（2026-11-06 合入，与本写出器同形并被 jxlinfo + jxl-oxide
/// 双读者互验）：version 0、无 alt color encoding、无 alt ICC（ISO flags bit6 useBaseColorSpace
/// ⇒ 施加空间 = 底图色彩空间 = 线性 RGB，GAINMAP_ROUTES §1:40），底图 sRGB SDR、增益图灰度
///（或 multiChannel 时 RGB）裸码流。
/// </para>
///
/// <para>
/// 诊断串一律以 <c>[GainMap-Jxl] </c> 开头 —— 本仓 <c>_probe-cjk-hardcode-scan.ps1</c> 的口径②：
/// <c>[</c> 开头的字面量归**诊断桶**，否则会被算进「UI 面」判据（该判据余量为 0）。
/// </para>
/// </summary>
internal static class JxlGainMapWriter
{
    /// <summary>组装结果。<see cref="Data"/>=null ⇒ 失败，<see cref="FailReason"/> 点名原因。</summary>
    internal sealed class BuildResult
    {
        public byte[]? Data;
        public string? FailReason;
        /// <summary>底图裸码流字节数（进 jxlc 的载荷）。</summary>
        public int BaseCodestreamBytes;
        /// <summary>增益图裸码流字节数（jhgm 载荷尾部）。</summary>
        public int GainMapCodestreamBytes;
        /// <summary>jhgm 盒载荷总字节数（含 version/尺寸位与 ISO blob）。</summary>
        public int JhgmPayloadBytes;
        public bool Ok => Data != null;
    }

    /// <summary>JXL 容器签名（ISO/IEC 18181-2）：00 00 00 0C 'JXL ' 0D 0A 87 0A，共 12 字节。</summary>
    private static readonly byte[] ContainerSignature =
        { 0x00, 0x00, 0x00, 0x0C, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 0x0D, 0x0A, 0x87, 0x0A };

    /// <summary>
    /// ftyp 盒：size(4)=20 + 'ftyp' + major='jxl ' + minor_version(4)=0 + compatible='jxl '。
    /// 单兼容品牌，与 libjxl extras 产物的形状一致（2026-11-08 probe.jxl 正控同款）。
    /// </summary>
    private static readonly byte[] FtypBox =
        { 0x00, 0x00, 0x00, 0x14, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
          (byte)'j', (byte)'x', (byte)'l', (byte)' ', 0x00, 0x00, 0x00, 0x00,
          (byte)'j', (byte)'x', (byte)'l', (byte)' ' };

    /// <summary>组装总量上限：盒尺寸位是 BE32，2 GiB 之外本就不可能成立；显式上限让错误发生在
    /// 组装前而不是 OOM 时。⚠ 用 <see cref="int.MaxValue"/> 收口——数组分配的长度参数最终按 int 走，
    /// 上限必须落在 int 正数域内（2^31-1），超限先拒绝再分配。</summary>
    private const long MaxTotalBytes = int.MaxValue;

    /// <summary>
    /// 组装带 `jhgm` 盒的 JXL 增益图容器。
    /// </summary>
    /// <param name="baseCodestream">底图（SDR sRGB）**裸码流**字节（FF 0A 开头；cjxl 零 hint 产物）。</param>
    /// <param name="gainMapCodestream">增益图**裸码流**字节（灰度或 RGB；FF 0A 开头）。</param>
    /// <param name="isoMetadata">ISO 21496-1 元数据**裸 blob**（GainMapEncoder.BuildIso21496Metadata
    /// 的产物，**不含** tmap 的 version 前缀——jhgm 载荷首字节是自己的 version 位）。</param>
    public static BuildResult Build(byte[] baseCodestream, byte[] gainMapCodestream, byte[] isoMetadata)
    {
        var r = new BuildResult();
        try
        {
            if (baseCodestream == null || baseCodestream.Length == 0)
            { r.FailReason = "[GainMap-Jxl] 底图码流为空"; return r; }
            if (gainMapCodestream == null || gainMapCodestream.Length == 0)
            { r.FailReason = "[GainMap-Jxl] 增益图码流为空"; return r; }
            if (isoMetadata == null || isoMetadata.Length == 0)
            { r.FailReason = "[GainMap-Jxl] ISO 21496-1 元数据为空"; return r; }
            // jhgm 的元数据长度位是 BE16（gain_map.cc:122 StoreBE16）；超出即结构上写不下。
            if (isoMetadata.Length > 0xFFFF)
            {
                r.FailReason = $"[GainMap-Jxl] ISO 21496-1 元数据 {isoMetadata.Length} B 超出 jhgm BE16 尺寸位上限 65535 B";
                return r;
            }

            string? baseErr = ValidateNakedCodestream(baseCodestream, "底图");
            if (baseErr != null) { r.FailReason = baseErr; return r; }
            string? gmErr = ValidateNakedCodestream(gainMapCodestream, "增益图");
            if (gmErr != null) { r.FailReason = gmErr; return r; }

            // jhgm 载荷 = version(1) + BE16 元数据长 + ISO + 颜色编码长(1) + BE32 alt ICC 长 + 增益图
            int payloadLen = 1 + 2 + isoMetadata.Length + 1 + 4 + gainMapCodestream.Length;
            int jhgmBoxLen = 8 + payloadLen;
            int jxlcBoxLen = 8 + baseCodestream.Length;
            long total = ContainerSignature.Length + FtypBox.Length + (long)jhgmBoxLen + jxlcBoxLen;
            if (total > MaxTotalBytes)
            {
                r.FailReason = $"[GainMap-Jxl] 组装总量 {total} B 超出 {MaxTotalBytes} B 上限";
                return r;
            }

            var data = new byte[(int)total];
            int pos = 0;

            Array.Copy(ContainerSignature, 0, data, pos, ContainerSignature.Length);
            pos += ContainerSignature.Length;
            Array.Copy(FtypBox, 0, data, pos, FtypBox.Length);
            pos += FtypBox.Length;

            // ── jhgm 盒（必须在 jxlc 之前，见类注释）──
            // 载荷序（gain_map.cc:118-145）：version(1) + BE16 元数据长 + ISO blob
            //   + 颜色编码长(1)=0 + BE32 alt ICC 长=0 + 增益图（剩余全部，尺寸不落盘）。
            WriteBe32(data, ref pos, (uint)jhgmBoxLen);
            data[pos++] = (byte)'j'; data[pos++] = (byte)'h'; data[pos++] = (byte)'g'; data[pos++] = (byte)'m';
            data[pos++] = 0x00;                                  // jhgm_version = 0（rawmakase #912 / vendored 测试同值）
            WriteBe16(data, ref pos, isoMetadata.Length);        // gain_map_metadata_size (BE16)
            Array.Copy(isoMetadata, 0, data, pos, isoMetadata.Length);
            pos += isoMetadata.Length;
            data[pos++] = 0x00;                                  // 颜色编码长 = 0（无 alt color encoding）
            WriteBe32(data, ref pos, 0u);                        // alt ICC 长 = 0（无 alt ICC）
            Array.Copy(gainMapCodestream, 0, data, pos, gainMapCodestream.Length);
            pos += gainMapCodestream.Length;

            // ── jxlc 盒：底图裸码流整体作为单一 jxlc 载荷 ──
            WriteBe32(data, ref pos, (uint)jxlcBoxLen);
            data[pos++] = (byte)'j'; data[pos++] = (byte)'x'; data[pos++] = (byte)'l'; data[pos++] = (byte)'c';
            Array.Copy(baseCodestream, 0, data, pos, baseCodestream.Length);
            pos += baseCodestream.Length;

            r.Data = data;
            r.BaseCodestreamBytes = baseCodestream.Length;
            r.GainMapCodestreamBytes = gainMapCodestream.Length;
            r.JhgmPayloadBytes = payloadLen;
            return r;
        }
        catch (Exception ex)
        {
            r.Data = null;
            r.FailReason = $"[GainMap-Jxl] 组装异常: {ex.Message}";
            return r;
        }
    }

    /// <summary>
    /// 裸码流守卫：JXL 裸码流以 FF 0A 开头（ISO/IEC 18181-1）；容器件以 00 00 00 0C 'JXL ' 0D 0A 87 0A 开头。
    /// 返回 null = 合法；否则返回点名原因（容器件时明说「传入了容器件而非裸码流」）。
    /// </summary>
    private static string? ValidateNakedCodestream(byte[] codestream, string which)
    {
        if (codestream.Length >= 2 && codestream[0] == 0xFF && codestream[1] == 0x0A)
            return null;
        bool looksContainer = codestream.Length >= 12;
        if (looksContainer)
        {
            for (int i = 0; i < ContainerSignature.Length; i++)
            {
                if (codestream[i] != ContainerSignature[i]) { looksContainer = false; break; }
            }
        }
        return looksContainer
            ? $"[GainMap-Jxl] {which}码流传入了容器件而非裸码流（JXL 容器签名开头）：" +
              "jhgm 载荷要求 FF 0A 裸码流（cjxl 零 hint 产物）——检查子图编码是否混入了 -x/--intensity_target 等 hint"
            : $"[GainMap-Jxl] {which}码流不是 JXL 裸码流（应为 FF 0A 开头，实得 " +
              $"{(codestream.Length >= 2 ? $"{codestream[0]:X2} {codestream[1]:X2}" : "不足 2 字节")}）";
    }

    private static void WriteBe16(byte[] buf, ref int pos, int v)
    {
        buf[pos++] = (byte)((v >> 8) & 0xFF);
        buf[pos++] = (byte)(v & 0xFF);
    }

    private static void WriteBe32(byte[] buf, ref int pos, uint v)
    {
        buf[pos++] = (byte)((v >> 24) & 0xFF);
        buf[pos++] = (byte)((v >> 16) & 0xFF);
        buf[pos++] = (byte)((v >> 8) & 0xFF);
        buf[pos++] = (byte)(v & 0xFF);
    }
}
