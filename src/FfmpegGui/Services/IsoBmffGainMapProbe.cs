using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FfmpegGui.Services;

/// <summary>
/// ISO-BMFF (HEIF/AVIF) **增益图容器探测器** —— 纯托管、零外部依赖、只读。
///
/// <para>
/// 存在的理由：<see cref="GainMapDecoder"/> 原先只认 **JPEG 容器**（SOI / APP2 ISO 21496-1 / MPF），
/// 而 AVIF/HEIC 的增益图既没有 SOI 也没有 MPF ⇒ 不解析 ISO-BMFF 就**完全看不见**增益图，
/// 表现为「静默丢弃、无点名」（2026-09-20 实测确认）。
/// </para>
///
/// <para>
/// 承载形式（ISO/IEC 23008-12:2024 修正案 "Support for tone map derived image items"）：
/// 一个 **`tmap` 派生项**（tone map derived image item）+ 一个独立的**增益图 item**，
/// 由 `iref` 的 `dimg` 从 `tmap` 指向「底图 + 增益图」。实测 libavif 产物：
/// </para>
/// <code>
/// iinf: 1=av01(底图) 2=tmap 3=av01(增益图) 4=Exif 5=mime
/// iref: dimg  from 2(tmap) -> [1(底图), 3(增益图)]
/// iloc: item2 的 extent = tmap 载荷（142 B）
/// ftyp: compatible_brands 含 'tmap'
/// </code>
///
/// <para>
/// ⚠ **`tmap` 载荷布局**（libavif `avifWriteToneMappedImagePayload` + ISO 21496-1 C.2.2）：
/// <c>unsigned int(8) version</c> 紧跟 <c>GainMapMetadata</c>。而该 <c>GainMapMetadata</c> 用的是
/// **独立分母**形式 —— 与 JPEG APP2 载荷**同构**，故
/// <see cref="GainMapDecoder.JpegContainer.ParseIso21496"/> 可**原样复用**（只需跳过首个 version 字节）。
/// 实测长度 = 1 + 2 + 2 + 1 + 16 + 通道数×40（3 通道 ⇒ 142 B），**逐字节吻合、剩余 0 字节**。
/// </para>
///
/// <para>
/// ⚠ **两条解码路径，编码格式决定走哪条**（2026-09-20 实测）：
/// <list type="bullet">
/// <item><c>av01</c>（AV1）：item 字节本身就是 OBU 流，ffmpeg 用 <c>-f obu</c> **直接可解**。</item>
/// <item><c>hvc1</c>/<c>hev1</c>（HEVC）：item 字节是**长度前缀 NAL**，参数集另存于 <c>hvcC</c>；
/// 而 ffmpeg 的 <c>-f hevc</c> 只吃 **Annex-B**（起始码）⇒ 必须先做
/// 「`hvcC` 驱动的长度前缀 → Annex-B」转换（本类实现）。</item>
/// </list>
/// 转换失败（无 <c>hvcC</c> / 长度前缀断链）一律 **fail-closed + 点名**，绝不给出错命令。
/// </para>
/// </summary>
public static class IsoBmffGainMapProbe
{
    /// <summary>纯托管解析的文件上限（与 <see cref="GainMapDecoder.MaxManagedParseBytes"/> 同口径）。</summary>
    internal const long MaxManagedParseBytes = 256L * 1024 * 1024;

    /// <summary>探测结果。<see cref="Determinate"/>=false ⇒ 「不知道」，调用方**不得**当成「没有」。</summary>
    public sealed class Result
    {
        /// <summary>是否为本探测器认识的 ISO-BMFF（`ftyp` 存在）。</summary>
        public bool IsIsoBmff;
        /// <summary>解析是否确定。false ⇒ <see cref="FailReason"/> 说明原因。</summary>
        public bool Determinate = true;
        public string? FailReason;

        /// <summary>是否存在增益图。</summary>
        public bool HasGainMap;
        /// <summary>承载形式：`tmap`（可解） / `aux-21496`（辅助图形式，本工具尚未解）。</summary>
        public string? Form;

        public int PrimaryItemId = -1;
        public int TmapItemId = -1;
        /// <summary>增益图 item 的 id；-1 = 未定位到。</summary>
        public int GainMapItemId = -1;
        /// <summary>增益图 item 的编解码类型（`av01` / `hvc1` / `hev1`）。</summary>
        public string? GainMapCodec;
        /// <summary>交给 ffmpeg 解复用增益图裸流所需的 `-f` 值（`obu` / `hevc`）。</summary>
        public string? GainMapFfmpegFormat;

        /// <summary>`tmap` item 的载荷（含首个 version 字节）；供 ISO 21496-1 解析。</summary>
        public byte[]? TmapPayload;
        /// <summary>
        /// 可直接喂给 <see cref="GainMapFfmpegFormat"/> 对应解复用器的**裸流字节**：
        /// <c>av01</c> ⇒ 原始 OBU 流；<c>hvc1</c>/<c>hev1</c> ⇒ **已转成 Annex-B**（前置 `hvcC` 参数集）。
        /// </summary>
        public byte[]? GainMapItemData;

        /// <summary>`ftyp` 的兼容品牌（诊断用）。</summary>
        public string Brands = "";
    }

    /// <summary>是否为本探测器认识的 ISO-BMFF（只做 8 字节头部判断，不解析）。</summary>
    public static bool LooksLikeIsoBmff(byte[] data)
        => data.Length >= 12 && data[4] == (byte)'f' && data[5] == (byte)'t'
           && data[6] == (byte)'y' && data[7] == (byte)'p';

    /// <summary>读文件并探测；文件不存在 / 过大 / 读失败返回 null（调用方按「未知」处理）。</summary>
    public static async Task<Result?> ProbeFileAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > MaxManagedParseBytes) return null;
            var bytes = await File.ReadAllBytesAsync(path, ct);
            return Probe(bytes);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// 轻量判定：该 ISO-BMFF 是否带 `tmap` 派生项（= 增益图容器）。
    /// <para>
    /// 专供动画探测使用 —— **静态 AVIF + alpha**（libavif 产物）与**增益图 AVIF** 的 ffprobe 形态
    /// **完全相同**（都是 2 条视频流、`nb_frames` 都是 1，实测 `Color`/`Alpha` vs `Color`/`GMap`）
    /// ⇒ 只有容器里的 `tmap` 项能把两者分开。
    /// </para>
    /// <para>返回 <c>null</c> = **不知道**（文件过大 / 读失败）⇒ 调用方必须退回旧行为，不得当成 false。</para>
    /// </summary>
    internal static bool? HasTmapItemFast(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > TmapFastMaxBytes) return null;
            return Probe(File.ReadAllBytes(path)) is { HasGainMap: true, Form: "tmap" };
        }
        catch { return null; }
    }

    /// <summary>
    /// 动画探测路径上允许读取的文件上限。**故意远小于 <see cref="MaxManagedParseBytes"/>**：
    /// 该判定会被每个 AVIF 项调用若干次，读大文件只为取一个布尔不划算；超限即「不知道」⇒ 退回旧行为。
    /// </summary>
    private const long TmapFastMaxBytes = 32L * 1024 * 1024;

    /// <summary>解析内存中的 ISO-BMFF。任何解析异常 ⇒ <see cref="Result.Determinate"/>=false（不抛）。</summary>
    public static Result Probe(byte[] d)
    {
        var r = new Result();
        try
        {
            if (!LooksLikeIsoBmff(d)) return r;          // IsIsoBmff=false ⇒ 「不是本探测器的对象」
            r.IsIsoBmff = true;

            var items = new Dictionary<int, string>();
            var locs = new Dictionary<int, (int Method, int Base, List<(int Off, int Len)> Extents)>();
            var refs = new List<(string Type, int From, List<int> To)>();
            // `ipco` 的属性列表（**1-based** 索引，按盒内出现顺序）与 `ipma` 的 item→属性索引映射。
            var ipcoProps = new List<(string Type, int Off, int Size)>();
            var ipmaMap = new Dictionary<int, List<int>>();
            int primary = -1;
            bool sawAux21496 = false;
            bool itemTypeUnknown = false;

            // ── 顶层 box：找 ftyp（品牌）与 meta ──
            int metaOff = -1, metaSize = 0;
            for (int p = 0; p + 8 <= d.Length;)
            {
                if (!ReadBox(d, p, d.Length, out var type, out var size, out var hdr)) break;
                if (type == "ftyp") r.Brands = ReadBrands(d, p, size, hdr);
                else if (type == "meta") { metaOff = p; metaSize = size; }
                p += size;
            }
            if (metaOff < 0)
            {
                // 没有 meta：可能是纯 track 型 ISO-BMFF（如 .heic 的 mov 包装）⇒ 无 item 结构 ⇒ 无增益图。
                return r;
            }

            // meta 是 FullBox：跳过 4 字节 version/flags 后才是子 box。
            int bodyStart = metaOff + 8 + 4, bodyEnd = metaOff + metaSize;
            for (int p = bodyStart; p + 8 <= bodyEnd;)
            {
                if (!ReadBox(d, p, bodyEnd, out var type, out var size, out var hdr)) break;
                int cstart = p + hdr, cend = p + size;
                switch (type)
                {
                    case "pitm":
                        {
                            int ver = d[cstart];
                            int q = cstart + 4;
                            if (ver == 0) { if (q + 2 <= cend) primary = Be16(d, q); }
                            else { if (q + 4 <= cend) primary = (int)Be32(d, q); }
                            break;
                        }
                    case "iinf":
                        ParseIinf(d, cstart, cend, items, ref itemTypeUnknown);
                        break;
                    case "iloc":
                        ParseIloc(d, cstart, cend, locs);
                        break;
                    case "iref":
                        ParseIref(d, cstart, cend, refs);
                        break;
                    case "iprp":
                        ParseIprp(d, cstart, cend, ipcoProps, ipmaMap, ref sawAux21496);
                        break;
                }
                p += size;
            }

            r.PrimaryItemId = primary;
            // ⚠ 失败原因一律以 `[IsoBmff] ` 开头：本仓的诊断串约定（`[tag]` 前缀）—— 见
            //   `tests/scripts/_probe-cjk-hardcode-scan.ps1` 的口径②：`[` 开头的字面量归**诊断桶**（ℹ 不判），
            //   否则会被算进「UI 面」判据（该判据余量为 0，任何新增中文 UI 串都会转红）。
            if (itemTypeUnknown) { r.Determinate = false; r.FailReason = "[IsoBmff] infe 版本 < 2（无 item_type）"; return r; }

            // ── 判定 1（权威）：存在 `tmap` 派生项 ──
            int tmapId = -1;
            foreach (var kv in items) if (kv.Value == "tmap") { tmapId = kv.Key; break; }

            if (tmapId >= 0)
            {
                r.HasGainMap = true;
                r.Form = "tmap";
                r.TmapItemId = tmapId;

                // 增益图 item = tmap 的 dimg 目标里「是编解码 item 且不是主图」的那一个。
                // ⚠ 不依赖 iref 是否带 reference_count（见 ParseIref 的两种读法都收敛到同一候选集）。
                var cands = new List<int>();
                foreach (var rf in refs)
                {
                    if (rf.Type != "dimg" || rf.From != tmapId) continue;
                    foreach (var to in rf.To)
                        if (to != primary && items.TryGetValue(to, out var ty) && IsCodecItem(ty) && !cands.Contains(to))
                            cands.Add(to);
                }
                if (cands.Count == 1) r.GainMapItemId = cands[0];
                else { r.Determinate = false; r.FailReason = $"[IsoBmff] tmap 的 dimg 增益图候选数 = {cands.Count}（应为 1）"; }

                if (r.GainMapItemId >= 0 && items.TryGetValue(r.GainMapItemId, out var gty))
                {
                    r.GainMapCodec = gty;
                    if (gty == "av01")
                    {
                        // AV1：`av01` item 的 OBU 流自带结构，ffmpeg `-f obu` **可**直接解（实测 400x300 ✓）。
                        r.GainMapFfmpegFormat = "obu";
                        r.GainMapItemData = SliceItem(d, locs, r.GainMapItemId, ref r, "gainmap item");
                    }
                    else if (gty is "hvc1" or "hev1")
                    {
                        // ⚠⚠ **HEVC 的增益图 item 必须先转 Annex-B** ────────────────────────────────
                        //   ISO-BMFF 里的 HEVC item 是**长度前缀 NAL**（参数集另存于 `hvcC`），而 ffmpeg 的
                        //   `-f hevc` 只吃 **Annex-B**（起始码）⇒ 直接喂 item 字节会 `No start code is found`
                        //   （2026-09-20 实测：从真实 HEIC 抽出 `hvc1` item 喂 `-f hevc` 即此错）。
                        //   ⇒ 这里做「`hvcC` 驱动的长度前缀 → Annex-B」转换；**任何一步失败都 fail-closed 点名**，
                        //     绝不退回「给出 `-f hevc` 这个错命令」——那只会让下游收到一条看不懂的 ffmpeg 错误。
                        var raw = SliceItem(d, locs, r.GainMapItemId, ref r, "gainmap item");
                        if (raw != null)
                        {
                            if (!TryFindHvcC(ipcoProps, ipmaMap, r.GainMapItemId, out int hOff, out int hLen, out var hErr))
                            {
                                r.Determinate = false;
                                r.FailReason = $"[IsoBmff] 增益图 item 类型 '{gty}' 需要 hvcC：{hErr}";
                            }
                            else if (!TryParseHvcC(d, hOff, hLen, out int lengthSize, out var paramSets, out var pErr))
                            {
                                r.Determinate = false;
                                r.FailReason = $"[IsoBmff] hvcC 解析失败：{pErr}";
                            }
                            else
                            {
                                var annexB = ToAnnexB(raw, lengthSize, paramSets, out var cErr);
                                if (annexB == null)
                                {
                                    r.Determinate = false;
                                    r.FailReason = $"[IsoBmff] 增益图 item '{gty}' 长度前缀转换失败：{cErr}";
                                }
                                else
                                {
                                    r.GainMapFfmpegFormat = "hevc";
                                    r.GainMapItemData = annexB;
                                }
                            }
                        }
                    }
                    else
                    {
                        r.Determinate = false;
                        r.FailReason = $"[IsoBmff] 增益图 item 类型 '{gty}' 无已知裸流解复用器";
                    }
                }
                r.TmapPayload = SliceItem(d, locs, tmapId, ref r, "tmap payload");
                return r;
            }

            // ── 判定 2（次级）：辅助图形式（auxC 的 URN 带 21496）── 本工具**尚未**支持解码。
            if (sawAux21496)
            {
                r.HasGainMap = true;
                r.Form = "aux-21496";
                r.Determinate = false;
                r.FailReason = "[IsoBmff] 辅助图（auxC URN 含 21496）形式，尚未支持解码";
            }
            return r;
        }
        catch (Exception ex)
        {
            r.Determinate = false;
            r.FailReason = $"[IsoBmff] 解析异常 {ex.GetType().Name}";
            return r;
        }
    }

    // ═══════════════════════════════════════════
    //  子 box 解析
    // ═══════════════════════════════════════════

    private static bool IsCodecItem(string t) => t is "av01" or "hvc1" or "hev1";

    private static string ReadBrands(byte[] d, int off, int size, int hdr)
    {
        try
        {
            int q = off + hdr, end = off + size;
            var sb = new StringBuilder();
            if (q + 4 <= end) { sb.Append(Encoding.ASCII.GetString(d, q, 4)); q += 8; }   // major + minor_version
            for (; q + 4 <= end; q += 4)
            {
                var b = Encoding.ASCII.GetString(d, q, 4);
                sb.Append(',').Append(b);
            }
            return sb.ToString();
        }
        catch { return ""; }
    }

    /// <summary>解析 `iinf` → item_id → item_type。</summary>
    private static void ParseIinf(byte[] d, int start, int end, Dictionary<int, string> items, ref bool itemTypeUnknown)
    {
        if (start + 4 > end) return;
        int ver = d[start];
        int p = start + 4;
        int count;
        if (ver == 0) { if (p + 2 > end) return; count = Be16(d, p); p += 2; }
        else { if (p + 4 > end) return; count = (int)Be32(d, p); p += 4; }

        for (int i = 0; i < count && p + 8 <= end; i++)
        {
            if (!ReadBox(d, p, end, out var type, out var size, out var hdr) || type != "infe") break;
            int e = p + hdr, ee = p + size;
            int iver = d[e];
            int q = e + 4;
            int itemId;
            if (iver >= 3) { if (q + 4 > ee) break; itemId = (int)Be32(d, q); q += 4; }
            else { if (q + 2 > ee) break; itemId = Be16(d, q); q += 2; }
            q += 2;                                        // item_protection_index
            if (iver < 2)
            {
                itemTypeUnknown = true;                    // 无 item_type ⇒ 判据不可用（fail-closed）
                p += size; continue;
            }
            if (q + 4 > ee) break;
            items[itemId] = Encoding.ASCII.GetString(d, q, 4);
            p += size;
        }
    }

    /// <summary>
    /// 解析 `iloc` → item_id → (construction_method, base_offset, extents)。
    /// ⚠ ISO/IEC 14496-12：`offset_size`/`length_size` 是**同一个字节的高低半字节**，
    /// `base_offset_size`/`index_size` 同理 —— 不是两个独立字节（首版按两字节写 ⇒ 偏移全乱）。
    /// </summary>
    private static void ParseIloc(byte[] d, int start, int end,
        Dictionary<int, (int Method, int Base, List<(int Off, int Len)> Extents)> locs)
    {
        if (start + 8 > end) return;
        int ver = d[start];
        int p = start + 4;
        int b0 = d[p], b1 = d[p + 1]; p += 2;
        int offSize = b0 >> 4, lenSize = b0 & 0xF;
        int baseSize = b1 >> 4, idxSize = b1 & 0xF;
        int count, idSize;
        if (ver < 2) { count = Be16(d, p); p += 2; idSize = 2; }
        else { count = (int)Be32(d, p); p += 4; idSize = 4; }

        for (int i = 0; i < count; i++)
        {
            if (p + idSize + 2 > end) break;
            int itemId = idSize == 4 ? (int)Be32(d, p) : Be16(d, p);
            p += idSize;
            int method = 0;
            if (ver == 1 || ver == 2) { method = Be16(d, p) & 0xF; p += 2; }
            p += 2;                                        // data_reference_index
            int baseOff = baseSize > 0 ? ReadBig(d, p, baseSize) : 0;
            p += baseSize;
            if (p + 2 > end) break;
            int ec = Be16(d, p); p += 2;
            var extents = new List<(int, int)>();
            for (int k = 0; k < ec; k++)
            {
                p += idxSize;                              // extent_index（method 1 才有意义）
                int o = offSize > 0 ? ReadBig(d, p, offSize) : 0; p += offSize;
                int l = lenSize > 0 ? ReadBig(d, p, lenSize) : 0; p += lenSize;
                extents.Add((o, l));
            }
            locs[itemId] = (method, baseOff, extents);
        }
    }

    /// <summary>
    /// 解析 `iref` → [(type, from, targets)]。
    /// <para>
    /// ⚠ **两种读法必须都容忍**：ISO/IEC 14496-12 的 <c>SingleItemTypeReferenceBox</c> **没有**
    /// <c>reference_count</c>（目标个数由盒长推出），但 libavif 的写出代码（`write.c:3437-3443`）
    /// **写了**一个 u16 count ⇒ 同一个盒在两种读法下**盒长完全相同**（实测 dimg 盒 size=16：
    /// `from + count(2) + to[2]` 与 `from + to[3]` 都等于 16，**无法靠长度区分**）。
    /// 判别法：若首值恰等于「其后元素个数」，按「带 count」读；否则按「无 count」读。
    /// 实测三个盒（dimg / cdsc / thmb）在该判别法下**全部得到正确目标**。
    /// </para>
    /// </summary>
    private static void ParseIref(byte[] d, int start, int end, List<(string, int, List<int>)> refs)
    {
        if (start + 4 > end) return;
        int ver = d[start];
        int p = start + 4;
        int idSize = ver >= 1 ? 4 : 2;
        while (p + 8 <= end)
        {
            if (!ReadBox(d, p, end, out var type, out var size, out var hdr)) break;
            int q = p + hdr, ee = p + size;
            if (q + idSize > ee) break;
            int from = idSize == 4 ? (int)Be32(d, q) : Be16(d, q);
            q += idSize;
            var vals = new List<int>();
            while (q + idSize <= ee) { vals.Add(idSize == 4 ? (int)Be32(d, q) : Be16(d, q)); q += idSize; }
            List<int> to;
            if (vals.Count >= 1 && vals[0] == vals.Count - 1) to = vals.GetRange(1, vals.Count - 1);
            else to = vals;
            refs.Add((type, from, to));
            p += size;
        }
    }

    /// <summary>
    /// 解析 `iprp`：收集 `ipco` 的属性盒（**1-based** 索引，按出现顺序）与 `ipma` 的 item→属性索引映射；
    /// 同时保留旧判据「`ipco` 里存在 URN 含 `21496` 的 `auxC`」。
    /// </summary>
    private static void ParseIprp(byte[] d, int start, int end,
        List<(string Type, int Off, int Size)> props, Dictionary<int, List<int>> ipma, ref bool sawAux21496)
    {
        for (int p = start; p + 8 <= end;)
        {
            if (!ReadBox(d, p, end, out var type, out var size, out var hdr)) return;
            if (type == "ipco")
            {
                int q = p + hdr, ee = p + size;
                while (q + 8 <= ee)
                {
                    if (!ReadBox(d, q, ee, out var t2, out var s2, out var h2)) break;
                    props.Add((t2, q + h2, s2 - h2));       // 记**内容**偏移与长度
                    if (t2 == "auxC")
                    {
                        int s = q + h2 + 4, e2 = q + s2;    // auxC 是 FullBox
                        int z = s;
                        while (z < e2 && d[z] != 0) z++;
                        if (z > s && Encoding.ASCII.GetString(d, s, z - s).Contains("21496")) sawAux21496 = true;
                    }
                    q += s2;
                }
            }
            else if (type == "ipma")
            {
                ParseIpma(d, p + hdr, p + size, ipma);
            }
            p += size;
        }
    }

    /// <summary>
    /// 解析 `ipma`（ItemPropertyAssociationBox）→ item_id → 属性索引列表。
    /// ⚠ `flags` 的 bit0 决定 association 是 1 字节（index 7 位）还是 2 字节（index 15 位）；
    /// `version` 决定 item_ID 是 16 位还是 32 位 —— 三者**不能互相推断**。
    /// </summary>
    private static void ParseIpma(byte[] d, int start, int end, Dictionary<int, List<int>> map)
    {
        if (start + 8 > end) return;
        int ver = d[start];
        int flags = (d[start + 1] << 16) | (d[start + 2] << 8) | d[start + 3];
        int p = start + 4;
        int count = (int)Be32(d, p); p += 4;
        bool wide = (flags & 1) != 0;
        int idSize = ver >= 1 ? 4 : 2;
        for (int i = 0; i < count; i++)
        {
            if (p + idSize + 1 > end) return;
            int itemId = idSize == 4 ? (int)Be32(d, p) : Be16(d, p);
            p += idSize;
            int ac = d[p++];
            var list = new List<int>();
            for (int k = 0; k < ac; k++)
            {
                if (p + (wide ? 2 : 1) > end) return;
                int v = wide ? Be16(d, p) : d[p];
                p += wide ? 2 : 1;
                list.Add(v & (wide ? 0x7FFF : 0x7F));
            }
            map[itemId] = list;
        }
    }

    /// <summary>
    /// 找该 item 的 `hvcC` 属性。优先走 `ipma` 关联（权威）；`ipma` 缺失时才退化为
    /// 「`ipco` 里恰有一个 `hvcC`」。**不猜**：多个 `hvcC` 且无关联 ⇒ 失败并点名。
    /// </summary>
    private static bool TryFindHvcC(List<(string Type, int Off, int Size)> props,
        Dictionary<int, List<int>> ipma, int itemId, out int off, out int len, out string err)
    {
        off = -1; len = 0; err = "";
        if (ipma.TryGetValue(itemId, out var idxs))
        {
            foreach (var ix in idxs)
                if (ix >= 1 && ix <= props.Count && props[ix - 1].Type == "hvcC")
                {
                    off = props[ix - 1].Off; len = props[ix - 1].Size;
                    return true;
                }
            err = "ipma does not associate any hvcC with this item";
            return false;
        }
        int cnt = 0, found = -1;
        for (int i = 0; i < props.Count; i++) if (props[i].Type == "hvcC") { cnt++; found = i; }
        if (cnt == 1) { off = props[found].Off; len = props[found].Size; return true; }
        err = cnt == 0
            ? "no hvcC property in ipco"
            : $"{cnt} hvcC properties in ipco but no ipma association (ambiguous)";
        return false;
    }

    /// <summary>
    /// 解析 `hvcC`（ISO/IEC 14496-15 HEVCDecoderConfigurationRecord）：
    /// 取 `lengthSizeMinusOne`（决定 item 里长度字段的字节数）与参数集（NAL 类型 32/33/34 = VPS/SPS/PPS）。
    /// ⚠ 偏移按**内容**起点计（调用方已跳过 8 字节盒头）；`lengthSize` 位于第 22 字节的低 2 位。
    /// </summary>
    private static bool TryParseHvcC(byte[] d, int off, int len, out int lengthSize,
        out List<byte[]> paramSets, out string err)
    {
        lengthSize = 0; paramSets = new List<byte[]>(); err = "";
        if (len < 23) { err = $"hvcC too short ({len} B)"; return false; }
        if (d[off] != 1) { err = $"hvcC configurationVersion={d[off]} (expect 1)"; return false; }
        lengthSize = (d[off + 21] & 0x03) + 1;
        int numArrays = d[off + 22];
        int p = off + 23, end = off + len;
        for (int a = 0; a < numArrays; a++)
        {
            if (p + 3 > end) { err = "hvcC array header out of range"; return false; }
            int nalType = d[p] & 0x3F;
            int n = Be16(d, p + 1); p += 3;
            for (int k = 0; k < n; k++)
            {
                if (p + 2 > end) { err = "hvcC NAL length out of range"; return false; }
                int ln = Be16(d, p); p += 2;
                if (ln <= 0 || p + ln > end) { err = "hvcC NAL out of range"; return false; }
                if (nalType is 32 or 33 or 34)               // VPS / SPS / PPS
                {
                    var u = new byte[ln];
                    Array.Copy(d, p, u, 0, ln);
                    paramSets.Add(u);
                }
                p += ln;
            }
        }
        return true;
    }

    /// <summary>
    /// 「长度前缀 NAL → Annex-B」：把每个 NAL 的 <paramref name="lengthSize"/> 字节长度字段换成
    /// 4 字节起始码 <c>00 00 00 01</c>，并在最前面**前置参数集**（来自 `hvcC`，Annex-B 要求参数集在流内）。
    /// <para>
    /// ⚠ **严格闭合**：任何一个长度越界、或末尾有残留字节，都判失败（返回 <c>null</c> + 点名）。
    /// 这正是「假 HEVC」（把 AV1 字节的 item 类型改成 `hvc1`）能被识破的地方 —— 长度前缀必然断链。
    /// </para>
    /// </summary>
    private static byte[]? ToAnnexB(byte[] item, int lengthSize, List<byte[]> paramSets, out string err)
    {
        err = "";
        if (lengthSize != 1 && lengthSize != 2 && lengthSize != 4)
        {
            err = $"lengthSize={lengthSize} invalid";
            return null;
        }
        var nals = new List<(int Off, int Len)>();
        int total = 0;
        foreach (var ps in paramSets) total += 4 + ps.Length;

        int p = 0;
        while (p + lengthSize <= item.Length)
        {
            int head = p;
            int ln = ReadBig(item, p, lengthSize);
            p += lengthSize;
            if (ln <= 0 || p + ln > item.Length)
            {
                err = $"length-prefix chain broken at offset {head} (len={ln}, remain={item.Length - p})";
                return null;
            }
            nals.Add((p, ln));
            total += 4 + ln;
            p += ln;
        }
        if (p != item.Length) { err = $"trailing {item.Length - p} bytes after last NAL"; return null; }
        if (nals.Count == 0) { err = "no NAL unit"; return null; }

        var buf = new byte[total];
        int w = 0;
        foreach (var ps in paramSets)
        {
            buf[w] = 0; buf[w + 1] = 0; buf[w + 2] = 0; buf[w + 3] = 1; w += 4;
            Array.Copy(ps, 0, buf, w, ps.Length); w += ps.Length;
        }
        foreach (var (off, ln) in nals)
        {
            buf[w] = 0; buf[w + 1] = 0; buf[w + 2] = 0; buf[w + 3] = 1; w += 4;
            Array.Copy(item, off, buf, w, ln); w += ln;
        }
        return buf;
    }

    /// <summary>按 `iloc` 切出某个 item 的字节（多 extent 拼接）；失败/不支持 ⇒ 置不确定并返回 null。</summary>
    private static byte[]? SliceItem(byte[] d, Dictionary<int, (int Method, int Base, List<(int Off, int Len)> Extents)> locs,
        int itemId, ref Result r, string what)
    {
        if (!locs.TryGetValue(itemId, out var loc))
        {
            r.Determinate = false; r.FailReason = $"[IsoBmff] {what} item {itemId} 无 iloc 记录";
            return null;
        }
        if (loc.Method != 0)
        {
            // construction_method=1 ⇒ 数据在 meta/idat 里，本实现只支持文件偏移（method 0）。
            r.Determinate = false; r.FailReason = $"[IsoBmff] {what} 使用 construction_method={loc.Method}（仅支持 0）";
            return null;
        }
        int total = 0;
        foreach (var (_, len) in loc.Extents) total += len;
        if (total <= 0 || loc.Base < 0 || (long)loc.Base + total > d.Length)
        {
            r.Determinate = false;
            r.FailReason = $"[IsoBmff] {what} extent 越界（base={loc.Base} 合计={total} 文件={d.Length}）";
            return null;
        }
        var buf = new byte[total];
        int w = 0;
        foreach (var (off, len) in loc.Extents)
        {
            if (off < 0 || len < 0 || (long)loc.Base + off + len > d.Length)
            {
                r.Determinate = false; r.FailReason = $"[IsoBmff] {what} extent 越界（off={off} len={len}）";
                return null;
            }
            Array.Copy(d, loc.Base + off, buf, w, len);
            w += len;
        }
        return buf;
    }

    // ═══════════════════════════════════════════
    //  字节工具
    // ═══════════════════════════════════════════

    private static bool ReadBox(byte[] d, int pos, int end, out string type, out int size, out int hdr)
    {
        type = ""; size = 0; hdr = 8;
        if (pos < 0 || pos + 8 > end) return false;
        long sz = ((long)d[pos] << 24) | ((long)d[pos + 1] << 16) | ((long)d[pos + 2] << 8) | d[pos + 3];
        type = Encoding.ASCII.GetString(d, pos + 4, 4);
        if (sz == 1)
        {
            if (pos + 16 > end) return false;
            sz = 0;
            for (int i = 0; i < 8; i++) sz = (sz << 8) | d[pos + 8 + i];
            hdr = 16;
        }
        else if (sz == 0) sz = end - pos;
        if (sz < hdr || pos + sz > end) return false;
        size = (int)sz;
        return true;
    }

    private static int Be16(byte[] d, int o) => (d[o] << 8) | d[o + 1];

    private static uint Be32(byte[] d, int o)
        => ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];

    private static int ReadBig(byte[] d, int o, int n)
    {
        int v = 0;
        for (int i = 0; i < n && i < 4; i++) v = (v << 8) | d[o + i];
        return v;
    }
}
