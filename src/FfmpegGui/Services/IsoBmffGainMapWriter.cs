using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FfmpegGui.Services;

/// <summary>
/// ISO-BMFF（AVIF）增益图**容器写出器** —— 纯托管、零外部依赖。
///
/// <para>
/// 存在的理由：<see cref="IsoBmffGainMapProbe"/> 已能**读** `tmap` 派生项，但写出侧此前只有
/// **JPEG**（APP2 + XMP + MPF）。而 ffmpeg 的 `avif` muxer **不提供**增益图授权
/// （实测 `ffmpeg -h muxer=avif` 只有 `movie_timescale` / `loop`），libavif CLI 的增益图选项也被
/// 编译期开关门控 ⇒ **只能自己组装容器**。本类即该组装器。
/// </para>
///
/// <para>
/// 输入 = 「底图 AVIF 字节 + 增益图 AVIF 字节 + ISO 21496-1 tmap 载荷」，
/// 三者都是**单图** AVIF（由 ffmpeg 各编一张，属性盒可原样搬运）。
/// 输出 = 一个带 <c>tmap</c> 派生项的三 item 容器，结构与
/// <see cref="IsoBmffGainMapProbe"/> 解析的 libavif 产物同构：
/// </para>
/// <code>
/// ftyp: major=avif, compat 含 tmap
/// iinf: 1=av01(底图) 2=tmap 3=av01(增益图)
/// iref: dimg  from 2(tmap) -> [1(底图), 3(增益图)]
/// iloc: item2 的 extent = tmap 载荷（1 + 21 + 通道数×40 字节）
/// </code>
///
/// <para>
/// ⚠ **偏移必须两遍算**：<c>meta</c> 的长度依赖 <c>iloc</c>（其内部偏移是定长 u32），而 <c>iloc</c>
/// 里要填的偏移又依赖 <c>meta</c> 结束位置 ⇒ 先用零偏移建一遍量长度，再重建。这是本格式最容易写错的地方。
/// </para>
///
/// <para>
/// 诊断串一律以 <c>[GainMap-Avif] </c> 开头 —— 本仓 <c>_probe-cjk-hardcode-scan.ps1</c> 的口径②：
/// <c>[</c> 开头的字面量归**诊断桶**，否则会被算进「UI 面」判据（该判据余量为 0）。
/// </para>
/// </summary>
internal static class IsoBmffGainMapWriter
{
    /// <summary>组装结果。<see cref="Data"/>=null ⇒ 失败，<see cref="FailReason"/> 点名原因。</summary>
    internal sealed class BuildResult
    {
        public byte[]? Data;
        public string? FailReason;
        public int BaseItemBytes;
        public int GainMapItemBytes;
        public int TmapPayloadBytes;
        public bool Ok => Data != null;
    }

    /// <summary>单个 item 的 id 约定（与 libavif 产物一致）。</summary>
    private const int ItemBase = 1;
    private const int ItemTmap = 2;
    private const int ItemGainMap = 3;

    /// <summary>
    /// 组装带 `tmap` 派生项的 AVIF。
    /// </summary>
    /// <param name="baseAvif">底图（SDR）单图 AVIF 字节。</param>
    /// <param name="gainmapAvif">增益图单图 AVIF 字节（通常单色）。</param>
    /// <param name="tmapPayload">ISO 21496-1 tmap 载荷，**含首个 version 字节**（见 <see cref="BuildTmapPayload"/>）。</param>
    /// <param name="basePrimaries">
    /// 底图 CICP primaries；&lt; 0 ⇒ 原样保留 ffmpeg 写出的 `colr`。
    /// ⚠ 必须可覆盖：实测 ffmpeg 的 `avif` muxer **不采纳** `-color_primaries/-color_trc`
    /// （写出的是 `primaries=2/transfer=2` 即 unspecified）⇒ 底图色度只能由本写出器掌控。
    /// </param>
    /// <param name="baseTransfer">底图 CICP transfer；仅当 <paramref name="basePrimaries"/> ≥ 0 时生效。</param>
    /// <param name="baseMatrix">底图 CICP matrix；仅当 <paramref name="basePrimaries"/> ≥ 0 时生效。</param>
    /// <param name="baseFullRangeFallback">
    /// 底图 nclx 的 full_range 位。**兜底值**：仅当底图**不带** nclx 型 colr 盒（或那个 colr 是 ICC 型）
    /// 时才用它；底图自带 nclx 时**一律以原 colr 的该位为准**（<see cref="PatchColr"/> 只读继承）。
    /// ⚠ 默认 <c>false</c>（limited/tv）：yuv420p 的约定，也与 ffmpeg 实产一致（2026-10-03 实测）。
    /// </param>
    /// <param name="alternateTransfer">
    /// 替代图（HDR 侧）的 CICP transfer，默认 16 = PQ —— 与 libavif 自家增益图样本一致
    /// （其 `Alternate image` 报 `Transfer Char. : 16`）。
    /// </param>
    public static BuildResult Build(byte[] baseAvif, byte[] gainmapAvif, byte[] tmapPayload,
        int basePrimaries = -1, int baseTransfer = -1, int baseMatrix = -1, bool baseFullRangeFallback = false,
        int alternateTransfer = 16)
    {
        var r = new BuildResult();
        try
        {
            if (baseAvif == null || baseAvif.Length == 0) { r.FailReason = "[GainMap-Avif] 底图 AVIF 为空"; return r; }
            if (gainmapAvif == null || gainmapAvif.Length == 0) { r.FailReason = "[GainMap-Avif] 增益图 AVIF 为空"; return r; }
            if (tmapPayload == null || tmapPayload.Length == 0) { r.FailReason = "[GainMap-Avif] tmap 载荷为空"; return r; }

            if (!TryExtractSingle(baseAvif, "base", out var baseImg, out var bErr)) { r.FailReason = bErr; return r; }
            if (!TryExtractSingle(gainmapAvif, "gainmap", out var gmImg, out var gErr)) { r.FailReason = gErr; return r; }

            // 底图色度：ffmpeg 不采纳 CLI 标志 ⇒ 由本写出器重写 colr（单一真值在调用方）。
            if (basePrimaries >= 0)
                // ⚠ 用写回值覆盖入参：PatchColr 会只读继承底图原 colr 的 full_range 位，
                //   下面给 tmap 挂的 colr 必须沿用同一个值（同一份像素，range 不可能两说）。
                baseFullRangeFallback = PatchColr(baseImg.Props, basePrimaries, baseTransfer, baseMatrix, baseFullRangeFallback);

            // 增益图不得大于底图（ISO 21496-1 要求增益图是底图的降采样；反向会解码错位）
            if (baseImg.Width > 0 && gmImg.Width > 0
                && (gmImg.Width > baseImg.Width || gmImg.Height > baseImg.Height))
            {
                r.FailReason = $"[GainMap-Avif] 增益图 {gmImg.Width}x{gmImg.Height} 大于底图 {baseImg.Width}x{baseImg.Height}（应为其降采样）";
                return r;
            }

            r.BaseItemBytes = baseImg.ItemData.Length;
            r.GainMapItemBytes = gmImg.ItemData.Length;
            r.TmapPayloadBytes = tmapPayload.Length;

            // ── 属性盒（ipco）与 item→属性关联（ipma）────────────────────────────
            // 底图属性先排（1..N），增益图属性接在其后（N+1..M）。索引 **1-based**。
            var ipcoChildren = new List<byte[]>();
            var assocBase = new List<int>();
            foreach (var p in baseImg.Props) { ipcoChildren.Add(p); assocBase.Add(ipcoChildren.Count); }
            var assocGain = new List<int>();
            foreach (var p in gmImg.Props) { ipcoChildren.Add(p); assocGain.Add(ipcoChildren.Count); }
            // tmap 是派生项，本身无码流；给它挂底图的 ispe 以声明尺寸（与 libavif 同形）。
            var assocTmap = new List<int>();
            int ispeIdx = baseImg.Props.FindIndex(p => IsBox(p, "ispe"));
            if (ispeIdx >= 0) assocTmap.Add(ispeIdx + 1);
            // tmap 项还要带一个 `colr` 描述**替代图（HDR 侧）**的色度 —— 与 libultrahdr v2 的
            // `avifultrahdr.cpp:506-520`（为 tmap 单独建 nclx）同口径；libavif 据此报告 "Alternate image"。
            // 缺它时容器仍可解，但替代图的色度只能从底图 colr 猜 ⇒ 标注与语义不符。
            if (basePrimaries >= 0)
            {
                ipcoChildren.Add(Box("colr", Concat(Ascii("nclx"), U16(basePrimaries), U16(alternateTransfer),
                    U16(baseMatrix), new[] { (byte)(baseFullRangeFallback ? 0x80 : 0x00) })));
                assocTmap.Add(ipcoChildren.Count);
            }

            var ftyp = BuildFtyp();
            var baseBytes = baseImg.ItemData;
            var gmBytes = gmImg.ItemData;

            // !!! The gain map item MUST carry a Temporal Delimiter (measured 2026-10-02).
            // ffmpeg's `avif` muxer writes the `av01` item WITHOUT a TD (first byte measured `0a` =
            // OBU_SEQUENCE_HEADER), while ffmpeg's own `-f obu` demuxer REQUIRES one: without it ffmpeg
            // reports `Missing Temporal Delimiter` / `No sequence header available`, the downstream
            // GainMapDecoder cannot read the gain map dimensions, and the WHOLE gain map decode fails.
            // The base image is unaffected (ffmpeg reads it from the container, not via `-f obu`).
            // libavif's own output starts with `12` (= TD OBU); we prepend the same for parity.
            if (gmBytes.Length >= 1 && ((gmBytes[0] >> 3) & 0x0F) != 2)
                gmBytes = Concat(new byte[] { 0x12, 0x00 }, gmBytes);

            // ── 两遍法：先零偏移量长度，再填真偏移 ──────────────────────────────
            var metaProbe = BuildMeta(ipcoChildren, assocBase, assocTmap, assocGain, tmapPayload.Length, baseBytes.Length, gmBytes.Length, 0, 0, 0);
            int ftypLen = ftyp.Length;
            int metaLen = metaProbe.Length;
            const int mdatHeaderLen = 8;
            long o1 = ftypLen + metaLen + mdatHeaderLen;
            long o2 = o1 + baseBytes.Length;
            long o3 = o2 + tmapPayload.Length;
            if (o3 + gmBytes.Length > int.MaxValue)
            {
                r.FailReason = "[GainMap-Avif] 产物超过 2 GiB（32 位 box 长度上限）";
                return r;
            }

            var meta = BuildMeta(ipcoChildren, assocBase, assocTmap, assocGain, tmapPayload.Length, baseBytes.Length, gmBytes.Length, (uint)o1, (uint)o2, (uint)o3);
            if (meta.Length != metaLen)
            {
                // 偏移是定长 u32 ⇒ 两遍长度必须一致；不一致说明实现被改坏了（不静默继续）
                r.FailReason = $"[GainMap-Avif] 内部错误：meta 两遍长度不一致（{metaLen} vs {meta.Length}）";
                return r;
            }

            var mdat = BuildMdat(baseBytes, tmapPayload, gmBytes);
            var outBuf = new byte[ftypLen + metaLen + mdat.Length];
            int w = 0;
            Array.Copy(ftyp, 0, outBuf, w, ftyp.Length); w += ftyp.Length;
            Array.Copy(meta, 0, outBuf, w, meta.Length); w += meta.Length;
            Array.Copy(mdat, 0, outBuf, w, mdat.Length);

            r.Data = outBuf;
            return r;
        }
        catch (Exception ex)
        {
            r.FailReason = $"[GainMap-Avif] 组装异常 {ex.GetType().Name}: {ex.Message}";
            return r;
        }
    }

    /// <summary>
    /// 构建 tmap 载荷 = <c>u8 version(0)</c> + ISO 21496-1 GainMapMetadata。
    /// 后半段与 JPEG APP2 载荷**逐字节同构**（libavif `avifWriteToneMappedImagePayload`），故可复用
    /// <see cref="GainMapEncoder"/> 的既有生成器，只多一个前导 version 字节。
    /// </summary>
    public static byte[] BuildTmapPayload(byte[] iso21496Metadata)
    {
        var buf = new byte[1 + iso21496Metadata.Length];
        buf[0] = 0;                                     // version
        Array.Copy(iso21496Metadata, 0, buf, 1, iso21496Metadata.Length);
        return buf;
    }

    // ═══════════════════════════════════════════════════════════════
    //  容器构建
    // ═══════════════════════════════════════════════════════════════

    private static byte[] BuildFtyp()
    {
        // 与 libavif 产物同形的兼容品牌集：tmap 必须在内（读侧与第三方据此识别派生项容器）。
        var payload = Concat(
            Ascii("avif"), U32(0),
            Ascii("avif"), Ascii("mif1"), Ascii("miaf"), Ascii("MA1A"), Ascii("tmap"));
        return Box("ftyp", payload);
    }

    private static byte[] BuildMdat(byte[] baseBytes, byte[] tmapPayload, byte[] gmBytes)
        => Box("mdat", Concat(baseBytes, tmapPayload, gmBytes));

    private static byte[] BuildMeta(List<byte[]> ipcoChildren, List<int> assocBase, List<int> assocTmap,
        List<int> assocGain, int tmapLen, int baseLen, int gmLen, uint o1, uint o2, uint o3)
    {
        var hdlr = FullBox("hdlr", 0, 0, Concat(U32(0), Ascii("pict"), new byte[12], new byte[] { 0 }));
        var pitm = FullBox("pitm", 0, 0, U16(ItemBase));

        var iinfPayload = new MemoryStream();
        var iw = new BinaryWriter(iinfPayload);
        WriteBe16(iw, 3);
        iw.Write(BuildInfe(ItemBase, "av01"));
        iw.Write(BuildInfe(ItemTmap, "tmap"));
        iw.Write(BuildInfe(ItemGainMap, "av01"));
        iw.Flush();
        var iinf = FullBox("iinf", 0, 0, iinfPayload.ToArray());

        // ⚠ **必须写 `reference_count`**：ISO/IEC 14496-12 的 `SingleItemTypeReferenceBox` 含该字段，
        //   且 libavif 强校验 —— 缺它时 `avifdec` 直接报
        //   "box[dimg] for 'tmap' item 2 must have exactly 2 entries with distinct ids"（实测）。
        //   读侧 `IsoBmffGainMapProbe.ParseIref` 的两种读法对本形态都收敛到正确候选集（实测不受影响）。
        var dimg = Box("dimg", Concat(U16(ItemTmap), U16(2), U16(ItemBase), U16(ItemGainMap)));
        var iref = FullBox("iref", 0, 0, dimg);

        var ipco = Box("ipco", Concat(ipcoChildren.ToArray()));
        var ipma = BuildIpma(assocBase, assocTmap, assocGain);
        var iprp = Box("iprp", Concat(ipco, ipma));

        // ⚠ `altr`（alternative entity group）**不可省**：libavif 靠它把 `tmap` 识别为「替代图」。
        //   缺它时容器仍可被解析、主图也能解出，但 `avifdec --info` 报 `Gain map: Absent`（实测）。
        //   结构（ISO/IEC 23008-12 EntityToGroupBox）：group_id(u32) + num_entities(u32) + entity_id(u32)[N]；
        //   entity_id 顺序照 libavif 产物 —— **tmap 在前、底图在后**。
        var altr = FullBox("altr", 0, 0, Concat(U32(6), U32(2), U32(ItemTmap), U32(ItemBase)));
        var grpl = Box("grpl", altr);

        var iloc = BuildIloc(tmapLen, baseLen, gmLen, o1, o2, o3);

        return FullBox("meta", 0, 0, Concat(hdlr, pitm, iinf, iref, iprp, grpl, iloc));
    }

    /// <summary>
    /// 重写底图 `colr` 盒的 nclx 色度。**必须做**：实测 ffmpeg 的 `avif` muxer 不采纳
    /// `-color_primaries/-color_trc`（写出 `primaries=2/transfer=2` 即 unspecified），
    /// 若不自控 ⇒ 底图色度标注与像素不符（本仓最忌讳的「宣称 ≠ 交付」）。
    ///
    /// ⚠⚠ **2026-10-03 修正：本函数只负责修 CICP 三字段，`full_range` 位改为只读继承底图原 colr。**
    /// 修正前该位由调用方参数决定且默认 `true`（full），而 ffmpeg 实产是 **tv** —— 实测 libaom-av1
    /// 静态图 `ffprobe color_range=tv`、容器内 colr 盒末字节 `0x00`（`00 00 00 13 … 6e 63 6c 78 00 02 00 02 00 02 00`）。
    /// ⇒ 每条 AVIF 增益图产物的主图都被贴成 full range，而像素明明是 limited：Y∈[16,235] 被按 [0,255] 解读，
    /// 黑阶抬起、对比度压缩。这正是本函数注释开头宣称要消掉的形状。
    /// 权威口径：libultrahdr `avifultrahdr.cpp:435/520` 同样是「取**实际图像**的 range」而非硬编码。
    /// </summary>
    /// <param name="fullRangeFallback">底图不带 nclx 型 colr 时才启用的兜底值。</param>
    /// <returns>最终落到 colr 里的 full_range（供 tmap 的 colr 对齐同一份像素）。</returns>
    private static bool PatchColr(List<byte[]> props, int primaries, int transfer, int matrix, bool fullRangeFallback)
    {
        bool fr = fullRangeFallback;
        for (int i = 0; i < props.Count; i++)
        {
            if (!IsBox(props[i], "colr")) continue;
            // ⚠ 先读原值再整盒重建：range 是 ffmpeg 如实写下的，我们无权替它改口。
            var own = ReadNclxFullRange(props[i]);
            if (own.HasValue) fr = own.Value;
            props[i] = Box("colr", Concat(Ascii("nclx"), U16(primaries), U16(transfer), U16(matrix),
                new[] { (byte)(fr ? 0x80 : 0x00) }));
            return fr;
        }
        // 原文件没有 colr ⇒ 补一个（无色彩标注比标错更安全，但既然调用方给了值就写上）
        props.Add(Box("colr", Concat(Ascii("nclx"), U16(primaries), U16(transfer), U16(matrix),
            new[] { (byte)(fr ? 0x80 : 0x00) })));
        return fr;
    }

    /// <summary>
    /// 从已有 `colr` 盒读出 nclx 的 full_range 位；不是 nclx 型（ICC/‘rICC’/‘prof’）或结构过短 ⇒ 返回 null。
    /// <para>colr 不是 FullBox，content = colour_type(4) + primaries(2) + transfer(2) + matrix(2) + full_range(1)
    /// ⇒ 最小合法盒总长 = 8(header) + 11 = 19 字节（与 ffmpeg 实测产物长度相符）。</para>
    /// </summary>
    private static bool? ReadNclxFullRange(byte[] colrBox)
    {
        if (colrBox == null || colrBox.Length < 8 + 11) return null;
        if (Encoding.ASCII.GetString(colrBox, 8, 4) != "nclx") return null;
        // ⚠ 用**固定偏移**读 full_range 位，**不用「末字节」**（2026-10-04 修 P2-4）：
        //   盒布局 = size(4)+type(4) | 'nclx'(4) | primaries(2) | transfer(2) | matrix(2) | full_range(1)
        //   ⇒ full_range 恒在偏移 8+4+2+2+2 = 18。原写法 `colrBox[Length-1]` 只在「盒恰好 19 字节」时
        //   才等价；若上游多写了填充/未知尾字节，末字节读到的就不是 range 位（**静默读错位**比读不到更糟）。
        //   上面的 `Length < 19` 前置已保证 [18] 在界内。
        const int FullRangeOffset = 8 + 4 + 2 + 2 + 2;   // = 18
        return (colrBox[FullRangeOffset] & 0x80) != 0;
    }

    private static byte[] BuildInfe(int itemId, string itemType)
        => FullBox("infe", 2, 0, Concat(U16(itemId), U16(0), Ascii(itemType), new byte[] { 0 }));

    private static byte[] BuildIpma(List<int> assocBase, List<int> assocTmap, List<int> assocGain)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        WriteBe32(w, 3);                                    // entry_count
        WriteIpmaEntry(w, ItemBase, assocBase);
        WriteIpmaEntry(w, ItemTmap, assocTmap);
        WriteIpmaEntry(w, ItemGainMap, assocGain);
        w.Flush();
        return FullBox("ipma", 0, 0, ms.ToArray());
    }

    private static void WriteIpmaEntry(BinaryWriter w, int itemId, List<int> assoc)
    {
        WriteBe16(w, itemId);
        w.Write((byte)assoc.Count);
        foreach (var a in assoc)
        {
            // 索引 1 字节（index 7 位）；av1C/ispe 等编解码属性按规范标 essential（bit7）。
            // 读侧 ParseIpma 用 0x7F 掩码取值 ⇒ essential 位不影响自读，第三方需要它。
            w.Write((byte)(a > 0x7F ? 0x7F : (a | 0x80)));
        }
    }

    private static byte[] BuildIloc(int tmapLen, int baseLen, int gmLen, uint o1, uint o2, uint o3)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        // offset_size=4, length_size=4, base_offset_size=0, index_size=0 ⇒ 半字节打包 0x44, 0x00
        w.Write((byte)0x44);
        w.Write((byte)0x00);
        WriteBe16(w, 3);                                    // item_count
        WriteIlocEntry(w, ItemBase, o1, baseLen);
        WriteIlocEntry(w, ItemTmap, o2, tmapLen);
        WriteIlocEntry(w, ItemGainMap, o3, gmLen);
        w.Flush();
        return FullBox("iloc", 0, 0, ms.ToArray());
    }

    private static void WriteIlocEntry(BinaryWriter w, int itemId, uint offset, int length)
    {
        WriteBe16(w, itemId);
        WriteBe16(w, 0);                                    // data_reference_index
        WriteBe16(w, 1);                                    // extent_count
        WriteBe32(w, offset);
        WriteBe32(w, (uint)length);
    }

    // ═══════════════════════════════════════════════════════════════
    //  单图 AVIF 解析（取 item 字节与属性盒）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>从一张单图 AVIF 里取出主 item 的码流字节与其属性盒（原样保留）。</summary>
    private sealed class SingleImage
    {
        public byte[] ItemData = Array.Empty<byte>();
        public List<byte[]> Props = new();
        public int Width;
        public int Height;
    }

    private static bool TryExtractSingle(byte[] d, string what, out SingleImage img, out string err)
    {
        img = new SingleImage();
        err = "";
        if (!IsoBmffGainMapProbe.LooksLikeIsoBmff(d))
        {
            err = $"[GainMap-Avif] {what} 不是 ISO-BMFF（缺 ftyp）";
            return false;
        }

        BoxInfo? meta = null;
        foreach (var b in Boxes(d, 0, d.Length)) if (b.Type == "meta") { meta = b; break; }
        if (meta == null) { err = $"[GainMap-Avif] {what} 缺 meta 盒"; return false; }

        int bodyStart = meta.Value.ContentOffset + 4;        // meta 是 FullBox
        int bodyEnd = meta.Value.Offset + meta.Value.Size;

        int primary = -1;
        BoxInfo? iinf = null, iloc = null, iprp = null;
        foreach (var b in Boxes(d, bodyStart, bodyEnd))
        {
            switch (b.Type)
            {
                case "pitm":
                    {
                        int q = b.ContentOffset;
                        if (b.ContentLength >= 6 && d[q] == 0) primary = Be16(d, q + 4);
                        else if (b.ContentLength >= 8) primary = (int)Be32(d, q + 4);
                        break;
                    }
                case "iinf": iinf = b; break;
                case "iloc": iloc = b; break;
                case "iprp": iprp = b; break;
            }
        }
        if (primary < 0) { err = $"[GainMap-Avif] {what} 缺 pitm（无法定位主图）"; return false; }
        if (iloc == null) { err = $"[GainMap-Avif] {what} 缺 iloc"; return false; }

        // item 类型（诊断用；本写出器只接受 av01）
        var items = new Dictionary<int, string>();
        if (iinf != null) ParseIinfTypes(d, iinf.Value, items);
        if (items.TryGetValue(primary, out var ptype) && ptype != "av01")
        {
            err = $"[GainMap-Avif] {what} 主 item 类型 '{ptype}'（本写出器只支持 av01）";
            return false;
        }

        if (!TrySliceItem(d, iloc.Value, primary, out var itemBytes, out var sErr))
        {
            err = $"[GainMap-Avif] {what} {sErr}";
            return false;
        }
        img.ItemData = itemBytes;

        // ⚠ 必须先声明再 out：`if (cond) M(out var x);` 里 `out var` 的作用域**只到该内嵌语句**
        //   （无花括号时不会延伸到外层块）—— 写成 `out var` 会得到 CS0103。
        int w = 0, h = 0;
        if (iprp != null) ParseProps(d, iprp.Value, primary, img, out w, out h);
        img.Width = w;
        img.Height = h;
        return true;
    }

    private static void ParseIinfTypes(byte[] d, BoxInfo iinf, Dictionary<int, string> items)
    {
        int p = iinf.ContentOffset;
        int end = iinf.Offset + iinf.Size;
        if (p + 4 > end) return;
        int ver = d[p];
        p += 4;
        int count;
        if (ver == 0) { if (p + 2 > end) return; count = Be16(d, p); p += 2; }
        else { if (p + 4 > end) return; count = (int)Be32(d, p); p += 4; }
        for (int i = 0; i < count && p + 8 <= end; i++)
        {
            if (!TryReadBox(d, p, end, out var b) || b.Type != "infe") return;
            int q = b.ContentOffset;
            int iver = d[q];
            q += 4;
            int itemId;
            if (iver >= 3) { if (q + 4 > b.Offset + b.Size) return; itemId = (int)Be32(d, q); q += 4; }
            else { if (q + 2 > b.Offset + b.Size) return; itemId = Be16(d, q); q += 2; }
            q += 2;                                          // item_protection_index
            if (iver >= 2 && q + 4 <= b.Offset + b.Size)
                items[itemId] = Encoding.ASCII.GetString(d, q, 4);
            p = b.Offset + b.Size;
        }
    }

    /// <summary>取 ipco 属性盒（原样字节）与 ipma 关联，筛出该 item 实际关联的那些。</summary>
    private static void ParseProps(byte[] d, BoxInfo iprp, int primary, SingleImage img, out int width, out int height)
    {
        width = 0; height = 0;
        var props = new List<BoxInfo>();
        Dictionary<int, List<int>>? ipma = null;
        int end = iprp.Offset + iprp.Size;
        foreach (var b in Boxes(d, iprp.ContentOffset, end))
        {
            if (b.Type == "ipco")
            {
                foreach (var pb in Boxes(d, b.ContentOffset, b.Offset + b.Size)) props.Add(pb);
            }
            else if (b.Type == "ipma")
            {
                ipma = ParseIpmaMap(d, b);
            }
        }

        var wanted = new List<int>();
        if (ipma != null && ipma.TryGetValue(primary, out var list)) wanted.AddRange(list);
        else for (int i = 1; i <= props.Count; i++) wanted.Add(i);   // 无 ipma ⇒ 全给（保守）

        foreach (var ix in wanted)
        {
            if (ix < 1 || ix > props.Count) continue;
            var pb = props[ix - 1];
            var raw = new byte[pb.Size];
            Array.Copy(d, pb.Offset, raw, 0, pb.Size);
            img.Props.Add(raw);
            if (pb.Type == "ispe" && pb.ContentLength >= 12)
            {
                width = (int)Be32(d, pb.ContentOffset + 4);
                height = (int)Be32(d, pb.ContentOffset + 8);
            }
        }
    }

    private static Dictionary<int, List<int>> ParseIpmaMap(byte[] d, BoxInfo ipma)
    {
        var map = new Dictionary<int, List<int>>();
        int p = ipma.ContentOffset;
        int end = ipma.Offset + ipma.Size;
        if (p + 8 > end) return map;
        int ver = d[p];
        int flags = (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];
        p += 4;
        int count = (int)Be32(d, p); p += 4;
        bool wide = (flags & 1) != 0;
        int idSize = ver >= 1 ? 4 : 2;
        for (int i = 0; i < count; i++)
        {
            if (p + idSize + 1 > end) return map;
            int itemId = idSize == 4 ? (int)Be32(d, p) : Be16(d, p);
            p += idSize;
            int ac = d[p++];
            var list = new List<int>();
            for (int k = 0; k < ac; k++)
            {
                if (p + (wide ? 2 : 1) > end) return map;
                int v = wide ? Be16(d, p) : d[p];
                p += wide ? 2 : 1;
                list.Add(v & (wide ? 0x7FFF : 0x7F));
            }
            map[itemId] = list;
        }
        return map;
    }

    /// <summary>按 iloc 切出 item 字节（多 extent 拼接；仅支持 construction_method=0）。</summary>
    private static bool TrySliceItem(byte[] d, BoxInfo iloc, int itemId, out byte[] bytes, out string err)
    {
        bytes = Array.Empty<byte>();
        err = "";
        int p = iloc.ContentOffset;
        int end = iloc.Offset + iloc.Size;
        if (p + 8 > end) { err = "iloc too short"; return false; }
        int ver = d[p];
        p += 4;
        int b0 = d[p], b1 = d[p + 1]; p += 2;
        int offSize = b0 >> 4, lenSize = b0 & 0xF;
        int baseSize = b1 >> 4, idxSize = b1 & 0xF;
        int count, idSize;
        if (ver < 2) { count = Be16(d, p); p += 2; idSize = 2; }
        else { count = (int)Be32(d, p); p += 4; idSize = 4; }

        for (int i = 0; i < count; i++)
        {
            if (p + idSize + 2 > end) break;
            int id = idSize == 4 ? (int)Be32(d, p) : Be16(d, p);
            p += idSize;
            int method = 0;
            if (ver == 1 || ver == 2) { method = Be16(d, p) & 0xF; p += 2; }
            p += 2;                                          // data_reference_index
            long baseOff = baseSize > 0 ? ReadBig(d, p, baseSize) : 0;
            p += baseSize;
            if (p + 2 > end) break;
            int ec = Be16(d, p); p += 2;
            var extents = new List<(long Off, long Len)>();
            for (int k = 0; k < ec; k++)
            {
                p += idxSize;
                long o = offSize > 0 ? ReadBig(d, p, offSize) : 0; p += offSize;
                long l = lenSize > 0 ? ReadBig(d, p, lenSize) : 0; p += lenSize;
                extents.Add((o, l));
            }
            if (id != itemId) continue;
            if (method != 0) { err = $"item {itemId} uses construction_method={method} (only 0 supported)"; return false; }
            long total = 0;
            foreach (var (_, l) in extents) total += l;
            if (total <= 0 || total > int.MaxValue) { err = $"item {itemId} bad length ({total})"; return false; }
            var buf = new byte[total];
            int w = 0;
            foreach (var (o, l) in extents)
            {
                long abs = baseOff + o;
                if (abs < 0 || abs + l > d.Length) { err = $"item {itemId} extent out of range"; return false; }
                Array.Copy(d, abs, buf, w, l);
                w += (int)l;
            }
            bytes = buf;
            return true;
        }
        err = $"item {itemId} not in iloc";
        return false;
    }

    // ═══════════════════════════════════════════════════════════════
    //  字节工具
    // ═══════════════════════════════════════════════════════════════

    private readonly struct BoxInfo
    {
        public readonly string Type;
        public readonly int Offset;
        public readonly int Size;
        public readonly int HeaderSize;
        public BoxInfo(string type, int offset, int size, int headerSize)
        { Type = type; Offset = offset; Size = size; HeaderSize = headerSize; }
        public int ContentOffset => Offset + HeaderSize;
        public int ContentLength => Size - HeaderSize;
    }

    private static IEnumerable<BoxInfo> Boxes(byte[] d, int start, int end)
    {
        int p = start;
        while (p + 8 <= end)
        {
            if (!TryReadBox(d, p, end, out var b)) yield break;
            yield return b;
            p = b.Offset + b.Size;
            if (b.Size <= 0) yield break;
        }
    }

    private static bool TryReadBox(byte[] d, int pos, int end, out BoxInfo box)
    {
        box = default;
        if (pos < 0 || pos + 8 > end) return false;
        long sz = ((long)d[pos] << 24) | ((long)d[pos + 1] << 16) | ((long)d[pos + 2] << 8) | d[pos + 3];
        var type = Encoding.ASCII.GetString(d, pos + 4, 4);
        int hdr = 8;
        if (sz == 1)
        {
            if (pos + 16 > end) return false;
            sz = 0;
            for (int i = 0; i < 8; i++) sz = (sz << 8) | d[pos + 8 + i];
            hdr = 16;
        }
        else if (sz == 0) sz = end - pos;
        if (sz < hdr || pos + sz > end) return false;
        box = new BoxInfo(type, pos, (int)sz, hdr);
        return true;
    }

    private static bool IsBox(byte[] raw, string type)
        => raw.Length >= 8 && Encoding.ASCII.GetString(raw, 4, 4) == type;

    private static byte[] Box(string type, byte[] payload)
    {
        var buf = new byte[8 + payload.Length];
        WriteBe32To(buf, 0, (uint)buf.Length);
        Encoding.ASCII.GetBytes(type, 0, 4, buf, 4);
        Array.Copy(payload, 0, buf, 8, payload.Length);
        return buf;
    }

    private static byte[] FullBox(string type, byte version, uint flags, byte[] payload)
    {
        var body = new byte[4 + payload.Length];
        body[0] = version;
        body[1] = (byte)((flags >> 16) & 0xFF);
        body[2] = (byte)((flags >> 8) & 0xFF);
        body[3] = (byte)(flags & 0xFF);
        Array.Copy(payload, 0, body, 4, payload.Length);
        return Box(type, body);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p.Length;
        var buf = new byte[total];
        int w = 0;
        foreach (var p in parts) { Array.Copy(p, 0, buf, w, p.Length); w += p.Length; }
        return buf;
    }

    private static byte[] Ascii(string s)
    {
        var b = new byte[s.Length];
        for (int i = 0; i < s.Length; i++) b[i] = (byte)s[i];
        return b;
    }

    private static byte[] U16(int v) => new[] { (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF) };
    private static byte[] U32(uint v) => new[] { (byte)((v >> 24) & 0xFF), (byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF) };

    private static void WriteBe16(BinaryWriter w, int v) { w.Write((byte)((v >> 8) & 0xFF)); w.Write((byte)(v & 0xFF)); }
    private static void WriteBe32(BinaryWriter w, uint v)
    {
        w.Write((byte)((v >> 24) & 0xFF)); w.Write((byte)((v >> 16) & 0xFF));
        w.Write((byte)((v >> 8) & 0xFF)); w.Write((byte)(v & 0xFF));
    }
    private static void WriteBe32To(byte[] b, int o, uint v)
    {
        b[o] = (byte)((v >> 24) & 0xFF); b[o + 1] = (byte)((v >> 16) & 0xFF);
        b[o + 2] = (byte)((v >> 8) & 0xFF); b[o + 3] = (byte)(v & 0xFF);
    }

    private static int Be16(byte[] d, int o) => (d[o] << 8) | d[o + 1];
    private static uint Be32(byte[] d, int o)
        => ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];
    private static long ReadBig(byte[] d, int o, int n)
    {
        long v = 0;
        for (int i = 0; i < n && i < 8; i++) v = (v << 8) | d[o + i];
        return v;
    }
}
