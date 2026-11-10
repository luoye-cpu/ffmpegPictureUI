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
/// ⚠ **`tmap` 载荷与 `grid` 载荷常驻 `meta/idat`**（iloc `construction_method=1`，2026-11-02 实测 iPhone HDR HEIC）：
/// 这类件里瓦片在 `mdat`（method 0），而 `tmap` / `grid` 的可达数据在 `idat`
/// ⇒ **两种 construction_method 都要支持**（旧实现只支持 0，会把这类件判成「tmap payload 不可切」而 fail-closed）。
/// </para>
///
/// <para>
/// ⚠ **增益图也可以是 `grid` 分块**（`dimg tmap → [底图, grid]`、`dimg grid → [瓦片…]`）：
/// 本类按「逐瓦片切出 + 各瓦片 `hvcC`→Annex-B」产出 <see cref="Result.GainMapTiles"/>，
/// 由解码端（<see cref="GainMapDecoder"/>）逐瓦片解码后拼接成整幅增益图。
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
        /// <summary>承载形式：`tmap`（可解） / `aux-21496`（ISO 21496-1 辅助图，AU2-X1 起按**前置元数据
        /// 结构尝试**——结构成立 ⇒ 可解，不成立 ⇒ fail-closed 点名「结构未证实」） /
        /// `aux-apple-hdrgainmap`（Apple 早期辅助图，AU-W2 起可解——Determinate=true 时
        /// <see cref="GainMapItemData"/> 与 <see cref="ExifItemData"/> 已就位）。</summary>
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

        /// <summary>
        /// 增益图是否以 **`grid` 派生项**（分块）承载（2026-11-02 新增；iPhone HDR HEIC 实测形态）。
        /// true ⇒ <see cref="GainMapTiles"/> 持有各瓦片裸流（Annex-B / OBU，raster 顺序），
        /// 解码端逐瓦片解码后按 `GridRows/GridCols` 拼接成 `GridWidth×GridHeight` 的增益图。
        /// 此形态下 <see cref="GainMapItemData"/> 恒为 null（单 item 字节不存在）。
        /// </summary>
        public bool GainMapIsGrid;
        /// <summary>`grid` 瓦片裸流（raster 顺序 = 左上→右下，行优先）；非 grid 时为 null。</summary>
        public List<byte[]>? GainMapTiles;
        /// <summary>`grid` 的行数 / 列数（非 grid 时 0）。</summary>
        public int GridRows;
        public int GridCols;
        /// <summary>`grid` 声明的**整幅显示**尺寸（可小于 rows×tileH / cols×tileW 的铺满尺寸 ⇒ 右/下裁切）；非 grid 时 0。</summary>
        public int GridWidth;
        public int GridHeight;
        /// <summary>单个瓦片的宽高（取自瓦片 item 的 `ispe` 属性；非 grid 时 0）。</summary>
        public int TileWidth;
        public int TileHeight;

        /// <summary>`ftyp` 的兼容品牌（诊断用）。</summary>
        public string Brands = "";

        // ── aux-apple 解码形态（2026-11-10，AU-W2）────────────────────────────────
        /// <summary>主图关联的 Exif item 的 id（aux-apple 形态：AppleMakerNote 的载体）；-1 = 未定位到。
        /// 定位判据 = infe 类型 "Exif" **且** `iref cdsc: from Exif item → to primary`。</summary>
        public int ExifItemId = -1;
        /// <summary>
        /// 主图 Exif item 的**原始字节**（`u32 tiff_header_offset` + 头部 + TIFF；aux-apple 形态专用）。
        /// 探针只切数据不解析 —— AppleMakerNote 的字段读取（HDRHeadroom/HDRGain）在
        /// <see cref="GainMapDecoder"/> 层走 exiftool 整件通道（SPEC §3 决策 2：零新解析器）；
        /// 本字段的存在性 = 「容器里确有元数据载体」的结构级证据。
        /// </summary>
        public byte[]? ExifItemData;

        // ── 2026-11-09 修复批（D1/D2/D5/D8）新增读数 ──────────────────────────────
        /// <summary>pitm 指向的 item 类型（"grid" / "hvc1" / "av01" / "iden"…）；未知为 null。
        /// D1 的判据真值：<see cref="PrimaryIsGridDerived"/> 才能回答「主图是不是那个组」，
        /// `ffprobe 有没有 ≥2 块的组`回答不了（增益图自己也是组）。</summary>
        public string? PrimaryItemType;
        /// <summary>**主图**是否为 `grid` 派生项。只有它为 true 才允许走 `[0:g:0]` 组解码；
        /// false 时组（若存在）属于增益图/缩略图，组解码会拿到错误的图（2026-11-08 实测：
        /// 底图单 item + 增益图 grid 的仓库夹具产出增益图马赛克）。</summary>
        public bool PrimaryIsGridDerived;
        /// <summary>**主图**的 `irot` 角度码（0..3，×90°）；未声明为 -1。展示语义 = 先镜像后旋转
        /// （ISO 23008-12 的属性施加顺序），ffmpeg 解码底图时已施加 ⇒ 底图缓冲是**展示**取向。</summary>
        public int PrimaryIrot = -1;
        /// <summary>**主图**的 `imir` 轴码（0=左右镜像 / 1=上下镜像）；未声明为 -1。</summary>
        public int PrimaryImir = -1;
        /// <summary>存在 `auxC` URN = alpha 辅助图（`*auxid:1`，HEVC/AV1 同判）。
        /// alpha 是**独立 item/流**，主流 pix_fmt 不带 alpha 平面 ⇒ 只看 HasAlphaPlane 会把
        /// 透明静默拍平（2026-11-08 实测 4/4 带 alpha 的 HEIC 全部丢 alpha）。</summary>
        public bool HasAuxAlpha;
        /// <summary>任一 item 的数据 extent 越过 EOF（截断件）。真因必须先于
        /// 「承载形式不支持」报告——2026-11-08 实测截断件被误报成 auxC 形式不支持。</summary>
        public bool IsTruncated;
        public string? TruncationDetail;
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
    /// D5（2026-11-09）：文件截断预检——任一 item 的 iloc extent 越过 EOF ⇒ 文件不完整。
    /// <para>返回 <c>null</c> = **不知道**（不是 ISO-BMFF / 过大 / 读失败）⇒ 调用方必须照旧走原流程，
    /// 不得当成「未截断」。截断件必须在进 ffmpeg **之前**拦截：随包 ffmpeg 对「截断分块 + 复合图滤镜」
    /// 会以 0xC0000005 崩溃（2026-11-08 实测），且崩溃码会被当任务退出码展示。</para>
    /// </summary>
    public static async Task<(bool IsTruncated, string? Detail)?> CheckTruncationAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > MaxManagedParseBytes) return null;
            var bytes = await File.ReadAllBytesAsync(path, ct);
            if (!LooksLikeIsoBmff(bytes)) return null;
            var r = Probe(bytes);
            return (r.IsTruncated, r.TruncationDetail);
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
            // auxC 属性 → URN 类别（1=ISO 21496 / 2=Apple hdrgainmap / 3=alpha）。
            // AU-W2（2026-11-10）：Apple 形态要按「auxC 属性归属哪个 item」反查 aux item ⇒
            // 光有 sawAuxApple 布尔不够，必须记下每个 auxC 的 **1-based ipco 索引**。
            var auxCProps = new List<(int PropIndex, int Kind)>();
            int primary = -1;
            bool sawAux21496 = false;
            bool sawAuxApple = false;
            bool sawAuxAlpha = false;
            bool itemTypeUnknown = false;

            // ── 顶层 box：找 ftyp（品牌）与 meta ──
            int metaOff = -1, metaSize = 0;
            // meta 内 `idat` 载荷区间（construction_method=1 的 item 数据住这里；2026-11-02 新增）。
            // ⚠ `idat` 是**纯 box**（非 FullBox）：载荷起点即 item 数据起点，extent 偏移相对它。
            int idatStart = -1, idatEnd = -1;
            // ⚠ 截断/非法 box 头 ⇒ 「不知道」，调用方不得当成「没有」（见 Result.Determinate 的契约）。
            bool boxScanIncomplete = false;
            for (int p = 0; p + 8 <= d.Length;)
            {
                if (!ReadBox(d, p, d.Length, out var type, out var size, out var hdr))
                { boxScanIncomplete = true; break; }
                if (type == "ftyp") r.Brands = ReadBrands(d, p, size, hdr);
                else if (type == "meta") { metaOff = p; metaSize = size; }
                p += size;
            }
            if (metaOff < 0)
            {
                if (boxScanIncomplete)
                {
                    r.Determinate = false;
                    r.FailReason = "[IsoBmff] 顶层 box 链解析中断（截断或 box 头非法）⇒ 无法断定有无增益图";
                }
                // 没有 meta：可能是纯 track 型 ISO-BMFF（如 .heic 的 mov 包装）⇒ 无 item 结构 ⇒ 无增益图。
                return r;
            }

            // meta 是 FullBox：跳过 4 字节 version/flags 后才是子 box。
            int bodyStart = metaOff + 8 + 4, bodyEnd = metaOff + metaSize;
            for (int p = bodyStart; p + 8 <= bodyEnd;)
            {
                if (!ReadBox(d, p, bodyEnd, out var type, out var size, out var hdr))
                { boxScanIncomplete = true; break; }
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
                        ParseIprp(d, cstart, cend, ipcoProps, ipmaMap, auxCProps,
                            ref sawAux21496, ref sawAuxApple, ref sawAuxAlpha);
                        break;
                    case "idat":
                        // 2026-11-02：真实 iPhone HDR HEIC 把 tmap / grid 载荷放在 meta/idat
                        // （iloc construction_method=1）⇒ 记下载荷区间供 SliceItem 使用。
                        idatStart = cstart; idatEnd = cend;
                        break;
                }
                p += size;
            }

            r.PrimaryItemId = primary;
            // ── D1/D8 地基（2026-11-09）：主图身份 + 展示变换 ──────────────────────────
            // 主图类型是「走组解码还是走 0:0」的唯一权威（ffprobe 只能回答"存在组"，回答不了
            // "主图是不是那个组"——增益图自己也组成组）。irot/imir 是底图缓冲取向的声明源。
            if (primary >= 0 && items.TryGetValue(primary, out var pty))
            {
                r.PrimaryItemType = pty;
                r.PrimaryIsGridDerived = pty == "grid";
                if (ipmaMap.TryGetValue(primary, out var pidx))
                {
                    foreach (var ix in pidx)
                    {
                        if (ix < 1 || ix > ipcoProps.Count) continue;
                        var (pt, poff, psz) = ipcoProps[ix - 1];
                        // irot/imir 有**两种真实形态**（2026-11-09 实测 iPhone 原生件定案）：
                        //   · 规范 FullBox：content = 4B version/flags + 1B（角度/轴）—— content 5 B；
                        //   · **Apple 非规范短形**：content = 1B（角度/轴），无 version/flags —— 实测
                        //     `00 00 00 09 'irot' 03`（IMG_1159 原生件，ffmpeg 也接受）。
                        // 旧判据 `psz >= 5` 对 Apple 形态恒漏检 ⇒ 主图旋转整条丢失（D8 的根因之一）。
                        int angleOff = psz >= 5 ? 4 : 0;
                        if (pt == "irot" && psz >= 1 && poff + angleOff < d.Length && r.PrimaryIrot < 0)
                            r.PrimaryIrot = d[poff + angleOff] & 0x3;
                        else if (pt == "imir" && psz >= 1 && poff + angleOff < d.Length && r.PrimaryImir < 0)
                            r.PrimaryImir = d[poff + angleOff] & 0x1;
                    }
                }
            }
            // ── D5 地基（2026-11-09）：文件截断预检 ─────────────────────────────────
            // 任一 item 的数据 extent 越过 EOF ⇒ 文件不完整。必须在「承载形式」判定**之前**得出，
            // 否则截断件会被误报成「形式不支持」（2026-11-08 实测：DJI 截断件被报 auxC 不支持，
            // 真因=mdat 声称 2.98 MB 实际 142 KB，随后 ffmpeg 崩 0xC0000005）。
            // 数学与 SliceItem 同源：method 0 ⇒ 绝对区间 [Base+off, +len) 对 [0, d.Length)；
            // method 1 ⇒ 对 [idatStart, idatEnd)。
            foreach (var kv in locs)
            {
                int ds, de;
                if (kv.Value.Method == 0) { ds = 0; de = d.Length; }
                else if (kv.Value.Method == 1) { ds = idatStart; de = idatEnd; }
                else continue;                                  // method 2（item 内偏移）不在本判定范围
                if (ds < 0) continue;
                foreach (var (off, len) in kv.Value.Extents)
                {
                    long end = (long)ds + kv.Value.Base + off + len;
                    if (end > de)
                    {
                        r.IsTruncated = true;
                        r.TruncationDetail = $"item {kv.Key} extent 端点 {end} 越过数据区终点 {de}"
                            + $"（off={off} len={len} base={kv.Value.Base}）";
                        break;
                    }
                }
                if (r.IsTruncated) break;
            }

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

                // 增益图 item 候选 = tmap 的 dimg 目标（排除主图），分两类：
                //   · 编解码 item（av01/hvc1/hev1）—— 单 item 直载；
                //   · `grid` 派生项 —— 分块承载（2026-11-02：iPhone HDR HEIC 实测
                //     `dimg tmap -> [底图 grid, 增益图 grid]` 与 `dimg 增益图 grid -> 瓦片…`）。
                // ⚠ 不依赖 iref 是否带 reference_count（见 ParseIref 的两种读法都收敛到同一候选集）。
                var codecCands = new List<int>();
                var gridCands = new List<int>();
                foreach (var rf in refs)
                {
                    if (rf.Type != "dimg" || rf.From != tmapId) continue;
                    foreach (var to in rf.To)
                    {
                        if (to == primary || !items.TryGetValue(to, out var ty)) continue;
                        if (IsCodecItem(ty)) { if (!codecCands.Contains(to)) codecCands.Add(to); }
                        else if (ty == "grid") { if (!gridCands.Contains(to)) gridCands.Add(to); }
                    }
                }

                if (codecCands.Count == 1 && gridCands.Count == 0)
                {
                    r.GainMapItemId = codecCands[0];
                    if (items.TryGetValue(r.GainMapItemId, out var gty))
                    {
                        r.GainMapCodec = gty;
                        if (gty == "av01")
                        {
                            // AV1：`av01` item 的 OBU 流自带结构，ffmpeg `-f obu` **可**直接解（实测 400x300 ✓）。
                            r.GainMapFfmpegFormat = "obu";
                            r.GainMapItemData = SliceItem(d, locs, r.GainMapItemId, idatStart, idatEnd, ref r, "gainmap item");
                        }
                        else if (gty is "hvc1" or "hev1")
                        {
                            // ⚠⚠ **HEVC 的增益图 item 必须先转 Annex-B** ────────────────────────────────
                            //   ISO-BMFF 里的 HEVC item 是**长度前缀 NAL**（参数集另存于 `hvcC`），而 ffmpeg 的
                            //   `-f hevc` 只吃 **Annex-B**（起始码）⇒ 直接喂 item 字节会 `No start code is found`
                            //   （2026-09-20 实测：从真实 HEIC 抽出 `hvc1` item 喂 `-f hevc` 即此错）。
                            //   ⇒ 这里做「`hvcC` 驱动的长度前缀 → Annex-B」转换；**任何一步失败都 fail-closed 点名**，
                            //     绝不退回「给出 `-f hevc` 这个错命令」——那只会让下游收到一条看不懂的 ffmpeg 错误。
                            var raw = SliceItem(d, locs, r.GainMapItemId, idatStart, idatEnd, ref r, "gainmap item");
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
                }
                else if (codecCands.Count == 0 && gridCands.Count == 1)
                {
                    // 分块承载（2026-11-02）：网格项 + 瓦片清单，见 ParseGridGainMap。
                    r.GainMapItemId = gridCands[0];
                    r.GainMapIsGrid = true;
                    ParseGridGainMap(d, items, locs, refs, ipcoProps, ipmaMap, idatStart, idatEnd, r);
                }
                else
                {
                    r.Determinate = false;
                    r.FailReason = $"[IsoBmff] tmap 的 dimg 增益图候选：codec={codecCands.Count} grid={gridCands.Count}（应恰为一类且各 1）";
                }

                r.TmapPayload = SliceItem(d, locs, tmapId, idatStart, idatEnd, ref r, "tmap payload");
                return r;
            }

            // ── 判定 2（次级）：辅助图形式（auxC 的 URN 带 21496）—— AU2-X1（2026-11-10）**结构尝试**。
            //   ⚠ 调研饱和度结论（SPEC §7.0，五来源交叉，2026-11-10 定稿）：**该形态在已知生态无写入器**——
            //     libheif #1121 正文明确弃用 aux 形态选 tmap；libultrahdr heifultrahdr.cpp 仅 tmap；
            //     libavif PR#2960 写侧统一 tmap item；SharpAstro #70 自标 "to be confirmed"。
            //     ⇒ 本分支是**防御性前瞻设计**：按最合理结构（auxC URN=21496 的 aux item，其 item 数据
            //       前置 `version(1B,=0) + ISO 21496-1 GainMapMetadata`——ISO 21496-1 的元数据语法唯一，
            //       载体可插在 tmap item 数据或 aux item 数据，语法不变）做结构尝试，ParseIso21496 复用。
            //     结构成立 ⇒ Determinate=true（与 tmap 同等放行）；version/元数据不匹配 ⇒ fail-closed
            //     点名「aux-21496 元数据结构未证实」（不许当成「无增益图」静默丢弃）。
            //     若未来真机样本出现且实际结构与此不符，**按样本修正本方法**（预期在此登记）。
            if (sawAux21496)
            {
                r.HasGainMap = true;
                r.Form = "aux-21496";
                TryParseIsoAux(d, items, locs, refs, ipcoProps, ipmaMap, auxCProps,
                    idatStart, idatEnd, primary, r);
            }
            // ── 判定 2b：Apple 早期辅助图形式（auxC URN 含 hdrgainmap，**不是** ISO 21496-1）──
            //   AU-W2（2026-11-10）：从「认出点名」升级为**完整解码形态**——按 auxC 属性归属反查
            //   aux item → `iref auxl` 验证 aux→primary → 主图 Exif item（`iref cdsc`）→ 切出
            //   aux item 裸流（hvcC→Annex-B）与 Exif item 字节 ⇒ Determinate=true。
            //   元数据字段（HDRHeadroom/HDRGain）的**读取**在 GainMapDecoder 层走 exiftool
            //   整件通道（SPEC §3 决策 2）；本探针只做容器级提取与结构自证。
            //   ⚠ 提取链上任何一步缺失 ⇒ 仍 fail-closed 点名（FailReason 保留 hdrgainmap 字样，
            //     供调用方区分「形式认得但结构不全」与「完全陌生的形态」），绝不退化成「无增益图」。
            if (sawAuxApple)
            {
                r.HasGainMap = true;
                r.Form = "aux-apple-hdrgainmap";
                TryParseAppleAux(d, items, locs, refs, ipcoProps, ipmaMap, auxCProps,
                    idatStart, idatEnd, primary, r);
            }
            // D2（2026-11-09）：alpha 辅助图与增益图判定**正交**——带 alpha 的件也可能同时带增益图。
            if (sawAuxAlpha) r.HasAuxAlpha = true;
            if (boxScanIncomplete && !r.HasGainMap)
            {
                r.Determinate = false;
                r.FailReason = "[IsoBmff] meta 子 box 链解析中断（截断或 box 头非法）⇒ 无法断定有无增益图";
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
    /// <para>
    /// AU-W2（2026-11-10）：<paramref name="auxCProps"/> 记下每个 `auxC` 的 **1-based ipco 索引 →
    /// URN 类别**（1=ISO 21496 / 2=Apple hdrgainmap / 3=alpha）——Apple 形态的 aux item 定位
    /// 靠「ipma 反查哪个 item 关联了该属性索引」，布尔标志给不出这个关联。
    /// </para>
    /// </summary>
    private static void ParseIprp(byte[] d, int start, int end,
        List<(string Type, int Off, int Size)> props, Dictionary<int, List<int>> ipma,
        List<(int PropIndex, int Kind)> auxCProps,
        ref bool sawAux21496, ref bool sawAuxApple, ref bool sawAuxAlpha)
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
                        if (z > s)
                        {
                            var urn = Encoding.ASCII.GetString(d, s, z - s);
                            int kind = 0;
                            if (urn.Contains("21496")) { sawAux21496 = true; kind = 1; }
                            // Apple 早期 HEIF 辅助图（iPhone 12 起的 HDR Gain Map）：URN 形如
                            // `urn:com:apple:photo:2020:aux:hdrgainmap`，**不含 21496** ⇒ 旧判据对它完全
                            // 无感，会被报成「无增益图」而静默按 SDR 处理（= 可用性不诚实）。
                            // AU-W2：已升级为完整解码形态（TryParseAppleAux），布尔只作判定入口。
                            else if (urn.Contains("hdrgainmap", StringComparison.OrdinalIgnoreCase)) { sawAuxApple = true; kind = 2; }
                            // D2（2026-11-09）：alpha 辅助图（HEVC `urn:mpeg:hevc:2015:auxid:1` /
                            // AV1 `urn:mpeg:av1:2021:auxid:1`）。alpha 是独立 item，主流 pix_fmt 不带
                            // alpha 平面 ⇒ 只看 HasAlphaPlane 会把透明静默拍平。URN 权威，不猜流格式。
                            else if (urn.Contains("auxid:1", StringComparison.OrdinalIgnoreCase)) { sawAuxAlpha = true; kind = 3; }
                            if (kind != 0) auxCProps.Add((props.Count, kind));   // 1-based：刚 Add 的这条
                        }
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
    /// Apple aux 形态的**完整提取**（AU-W2，2026-11-10）。链路（ISO 23008-12 + Apple 官方文档，SPEC §2）：
    /// <list type="number">
    /// <item>按 `ipma` 反查「哪个 item 关联了 URN 含 hdrgainmap 的 auxC 属性」⇒ aux item（增益图 item）；</item>
    /// <item>`iref auxl: from aux item → to primary` 验证归属方向（aux 指向主图；反向/缺失 fail-closed）；</item>
    /// <item>主图 Exif item：infe 类型 "Exif" **且** `iref cdsc: from Exif → to primary`（AppleMakerNote 载体）；</item>
    /// <item>`SliceItem(aux)`（hvc1/hev1 走 hvcC→Annex-B，与 tmap 形态同一套转换器）+ `SliceItem(Exif)`。</item>
    /// </list>
    /// 全部成功 ⇒ <see cref="Result.Determinate"/>=true 并填充 GainMapItemData/ExifItemData；
    /// 任何一步缺失 ⇒ Determinate=false + FailReason（**保留 hdrgainmap 字样**——调用方与门禁靠它
    /// 区分「形式认得但结构不全」与「完全陌生」，也保住旧 §1c 判据的稳定性）。
    /// <para>⚠ 元数据字段（HDRHeadroom/HDRGain）的读取在 GainMapDecoder 层走 exiftool 整件通道，
    /// 本方法不做 EXIF/TIFF 解析（SPEC §3 决策 2：零新解析器）。</para>
    /// </summary>
    private static void TryParseAppleAux(byte[] d, Dictionary<int, string> items,
        Dictionary<int, (int Method, int Base, List<(int Off, int Len)> Extents)> locs,
        List<(string Type, int From, List<int> To)> refs,
        List<(string Type, int Off, int Size)> ipcoProps, Dictionary<int, List<int>> ipmaMap,
        List<(int PropIndex, int Kind)> auxCProps,
        int idatStart, int idatEnd, int primary, Result r)
    {
        void Fail(string why)
        {
            r.Determinate = false;
            r.FailReason = $"[IsoBmff] Apple 辅助图（auxC URN 含 hdrgainmap）不可解：{why}";
        }

        // ① aux item 定位：ipma 反查关联了 hdrgainmap auxC 的 item（恰 1 个；多个 = 歧义不猜）。
        int auxId = -1, owners = 0;
        foreach (var kv in ipmaMap)
        {
            bool hit = false;
            foreach (var ix in kv.Value)
                foreach (var ap in auxCProps)
                    if (ap.Kind == 2 && ix == ap.PropIndex) { hit = true; break; }
            if (!hit) continue;
            owners++;
            if (auxId < 0) auxId = kv.Key;
        }
        if (auxId < 0) { Fail("ipma 未把 hdrgainmap auxC 关联到任何 item（aux item 无法定位）"); return; }
        if (owners > 1) { Fail($"ipma 有 {owners} 个 item 关联 hdrgainmap auxC（歧义，不猜）"); return; }
        if (!items.TryGetValue(auxId, out var auxType) || !IsCodecItem(auxType))
        {
            Fail($"aux item {auxId} 类型 '{(items.TryGetValue(auxId, out var t) ? t : "?")}' 不是编解码 item");
            return;
        }

        // ② 归属方向：iref auxl: from aux → to 必须含主图（ISO 23008-12 方向）。
        bool auxlOk = false;
        foreach (var rf in refs)
        {
            if (rf.Type != "auxl" || rf.From != auxId) continue;
            foreach (var to in rf.To) if (to == primary) { auxlOk = true; break; }
        }
        if (!auxlOk) { Fail($"iref 无 auxl: aux item {auxId} → primary（归属方向不成立）"); return; }

        // ③ Exif item：类型 "Exif" 且 cdsc 指向主图（AppleMakerNote 的载体）。
        int exifId = -1;
        foreach (var kv in items)
        {
            if (!string.Equals(kv.Value, "Exif", StringComparison.Ordinal)) continue;
            foreach (var rf in refs)
            {
                if (rf.Type != "cdsc" || rf.From != kv.Key) continue;
                foreach (var to in rf.To) if (to == primary) { exifId = kv.Key; break; }
                if (exifId >= 0) break;
            }
            if (exifId >= 0) break;
        }
        if (exifId < 0) { Fail("未找到 cdsc 关联主图的 Exif item（AppleMakerNote 元数据不可得）"); return; }

        // ④ Exif item 字节（纯切片，无编解码转换）。
        var exifData = SliceItem(d, locs, exifId, idatStart, idatEnd, ref r, "Exif item");
        if (exifData == null) { Fail($"Exif item {exifId} 切片失败（见上一条 FailReason）"); return; }

        // ⑤ aux item 裸流：hvc1/hev1 走 hvcC→Annex-B（与 tmap 形态同一套转换器）；av01 原样。
        r.GainMapItemId = auxId;
        r.GainMapCodec = auxType;
        var raw = SliceItem(d, locs, auxId, idatStart, idatEnd, ref r, "aux item");
        if (raw == null) { Fail($"aux item {auxId} 切片失败（见上一条 FailReason）"); return; }
        byte[]? stream;
        if (auxType is "hvc1" or "hev1")
        {
            if (!TryFindHvcC(ipcoProps, ipmaMap, auxId, out int hOff, out int hLen, out var hErr))
            { Fail($"aux item {auxId} 需要 hvcC：{hErr}"); return; }
            if (!TryParseHvcC(d, hOff, hLen, out int lengthSize, out var paramSets, out var pErr))
            { Fail($"aux item {auxId} hvcC 解析失败：{pErr}"); return; }
            stream = ToAnnexB(raw, lengthSize, paramSets, out var cErr);
            if (stream == null) { Fail($"aux item {auxId} 长度前缀转换失败：{cErr}"); return; }
        }
        else stream = raw;                              // av01 ⇒ 原始 OBU
        r.GainMapFfmpegFormat = auxType == "av01" ? "obu" : "hevc";
        r.GainMapItemData = stream;
        r.ExifItemId = exifId;
        r.ExifItemData = exifData;
        r.Determinate = true;                            // 容器级提取全部成立；元数据语义由解码层验收
    }

    /// <summary>
    /// aux-21496 形态的**结构尝试**（AU2-X1，2026-11-10）。防御性前瞻设计——该形态在已知生态
    /// **无写入器**（libheif #1121 正文明确弃用 aux 形态选 tmap；libultrahdr / libavif 写侧均走
    /// tmap item；SharpAstro #70 自标 "to be confirmed"），本方法按最合理结构尝试提取：
    /// <list type="number">
    /// <item>按 `ipma` 反查「哪个 item 关联了 URN 含 21496 的 auxC 属性」⇒ aux item（恰 1 个）；</item>
    /// <item>`iref auxl: from aux item → to primary` 验证归属方向（ISO 23008-12 同纪律）；</item>
    /// <item>`SliceItem(aux)` 切出 item 数据，前置结构 = `version(1B,=0)` +
    ///       ISO 21496-1 `GainMapMetadata`（**与 tmap 载荷同构**——ISO 21496-1 的元数据语法唯一，
    ///       载体可插在 tmap item 数据或 aux item 数据，语法不变；<see cref="GainMapDecoder.JpegContainer.ParseIso21496"/>
    ///       原样复用，`out consumed` 给出元数据字节数）；</item>
    /// <item>元数据之后的剩余字节 = 增益图编码流：`hvc1`/`hev1` 走 hvcC→Annex-B（与 tmap /
    ///       Apple 形态同一套转换器），`av01` 原样 OBU。</item>
    /// </list>
    /// 全部成立 ⇒ <see cref="Result.Determinate"/>=true，<see cref="Result.TmapPayload"/> 填
    /// `version+元数据`段（与 tmap 载荷同构 ⇒ 解码层 `ParseIso21496(payload, 1, len-1)` 既有分支
    /// 直接消费，零特判）、<see cref="Result.GainMapItemData"/> 填裸流；
    /// 任何一步不成立 ⇒ Determinate=false + 点名「**aux-21496 元数据结构未证实**」（fail-closed，
    /// 绝不退化成「无增益图」——防御性识别的价值就在把「认识但解不了」与「完全陌生」分开）。
    /// <para>⚠ `ipma` 反查与 <see cref="TryParseAppleAux"/> ① 同构（kind=1 vs kind=2），独立成段
    /// 不共用 helper：两形态提取链的演进互不牵连（真机样本到位后大概率要各自修正）。</para>
    /// </summary>
    private static void TryParseIsoAux(byte[] d, Dictionary<int, string> items,
        Dictionary<int, (int Method, int Base, List<(int Off, int Len)> Extents)> locs,
        List<(string Type, int From, List<int> To)> refs,
        List<(string Type, int Off, int Size)> ipcoProps, Dictionary<int, List<int>> ipmaMap,
        List<(int PropIndex, int Kind)> auxCProps,
        int idatStart, int idatEnd, int primary, Result r)
    {
        void Fail(string why)
        {
            r.Determinate = false;
            r.FailReason = $"[IsoBmff] aux-21496 元数据结构未证实：{why}";
        }

        // ① aux item 定位：ipma 反查关联了 21496 auxC 的 item（恰 1 个；多个 = 歧义不猜）。
        int auxId = -1, owners = 0;
        foreach (var kv in ipmaMap)
        {
            bool hit = false;
            foreach (var ix in kv.Value)
                foreach (var ap in auxCProps)
                    if (ap.Kind == 1 && ix == ap.PropIndex) { hit = true; break; }
            if (!hit) continue;
            owners++;
            if (auxId < 0) auxId = kv.Key;
        }
        if (auxId < 0) { Fail("ipma 未把 21496 auxC 关联到任何 item（aux item 无法定位）"); return; }
        if (owners > 1) { Fail($"ipma 有 {owners} 个 item 关联 21496 auxC（歧义，不猜）"); return; }
        if (!items.TryGetValue(auxId, out var auxType) || !IsCodecItem(auxType))
        {
            Fail($"aux item {auxId} 类型 '{(items.TryGetValue(auxId, out var t) ? t : "?")}' 不是编解码 item");
            return;
        }

        // ② 归属方向：iref auxl: from aux → to 必须含主图（ISO 23008-12 方向）。
        bool auxlOk = false;
        foreach (var rf in refs)
        {
            if (rf.Type != "auxl" || rf.From != auxId) continue;
            foreach (var to in rf.To) if (to == primary) { auxlOk = true; break; }
        }
        if (!auxlOk) { Fail($"iref 无 auxl: aux item {auxId} → primary（归属方向不成立）"); return; }

        // ③ 切 aux item 数据（多 extent 拼接，construction_method 0/1 同 tmap 形态口径）。
        var raw = SliceItem(d, locs, auxId, idatStart, idatEnd, ref r, "aux item");
        if (raw == null) { Fail($"aux item {auxId} 切片失败（见上一条 FailReason）"); return; }

        // ④ 前置结构：version(1B,=0) + GainMapMetadata。version ≠ 0 或元数据不闭合 ⇒ 结构未证实。
        if (raw.Length < 2 || raw[0] != 0)
        {
            Fail($"前置 version 字节 = {(raw.Length > 0 ? raw[0].ToString() : "无数据")}（要求 0）"
                + "——该形态当前无已知写入器（libheif#1121 / libultrahdr / libavif 三生态均走 tmap），"
                + "字节层证据不足 ⇒ fail-closed");
            return;
        }
        var metaPayloadLen = GainMapDecoder.JpegContainer.ParseIso21496(raw, 1, raw.Length - 1, out int consumed);
        if (metaPayloadLen == null)
        {
            Fail("前置 GainMapMetadata 解析失败（非预期 min_version / 数据截断）");
            return;
        }
        if (raw.Length <= 1 + consumed)
        {
            Fail($"前置元数据外无编码流（元数据 {consumed} B 吃满 item 数据）");
            return;
        }

        // ⑤ 元数据之后的剩余字节 = 增益图编码流。hvc1/hev1：hvcC→Annex-B（同一套转换器）；av01：原样。
        var streamPart = new byte[raw.Length - 1 - consumed];
        Array.Copy(raw, 1 + consumed, streamPart, 0, streamPart.Length);
        byte[]? stream;
        if (auxType is "hvc1" or "hev1")
        {
            if (!TryFindHvcC(ipcoProps, ipmaMap, auxId, out int hOff, out int hLen, out var hErr))
            { Fail($"aux item {auxId} 需要 hvcC：{hErr}"); return; }
            if (!TryParseHvcC(d, hOff, hLen, out int lengthSize, out var paramSets, out var pErr))
            { Fail($"aux item {auxId} hvcC 解析失败：{pErr}"); return; }
            stream = ToAnnexB(streamPart, lengthSize, paramSets, out var cErr);
            if (stream == null) { Fail($"aux item {auxId} 长度前缀转换失败：{cErr}"); return; }
        }
        else stream = streamPart;                        // av01 ⇒ 原始 OBU

        // ⑥ `version+元数据` 段与 tmap 载荷**同构** ⇒ 填进 TmapPayload，解码层
        //    `ParseIso21496(TmapPayload, 1, len-1)` 既有分支零特判消费（AU2-X1 复用决策）。
        var payload = new byte[1 + consumed];
        Array.Copy(raw, 0, payload, 0, payload.Length);
        r.GainMapItemId = auxId;
        r.GainMapCodec = auxType;
        r.GainMapFfmpegFormat = auxType == "av01" ? "obu" : "hevc";
        r.GainMapItemData = stream;
        r.TmapPayload = payload;
        r.Determinate = true;
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

    /// <summary>
    /// 按 `iloc` 切出某个 item 的字节（多 extent 拼接）。
    /// <para>
    /// **construction_method**（2026-11-02 增）：0 = 数据在文件里（extent 偏移相对文件起点，base_offset 亦相对文件）；
    /// 1 = 数据在 `meta/idat` 里（extent 偏移相对 `idat` 载荷起点）——真实 iPhone HDR HEIC 的
    /// `tmap` / `grid` 载荷全都走 method=1（实测 2026-11-02），旧实现只支持 0 会让这类件报
    /// 「tmap payload 使用 construction_method=1（仅支持 0）」而整条增益图解码 fail-closed。
    /// 2（item 内部）仍不支持 ⇒ fail-closed 点名。
    /// </para>
    /// </summary>
    private static byte[]? SliceItem(byte[] d, Dictionary<int, (int Method, int Base, List<(int Off, int Len)> Extents)> locs,
        int itemId, int idatStart, int idatEnd, ref Result r, string what)
    {
        if (!locs.TryGetValue(itemId, out var loc))
        {
            r.Determinate = false; r.FailReason = $"[IsoBmff] {what} item {itemId} 无 iloc 记录";
            return null;
        }
        int dataStart, dataEnd;
        switch (loc.Method)
        {
            case 0:
                dataStart = 0; dataEnd = d.Length;
                break;
            case 1:
                if (idatStart < 0 || idatEnd <= idatStart)
                {
                    r.Determinate = false;
                    r.FailReason = $"[IsoBmff] {what} 使用 construction_method=1 但 meta 内无 idat 盒";
                    return null;
                }
                dataStart = idatStart; dataEnd = idatEnd;
                break;
            default:
                r.Determinate = false;
                r.FailReason = $"[IsoBmff] {what} 使用 construction_method={loc.Method}（仅支持 0/1）";
                return null;
        }
        int total = 0;
        foreach (var (_, len) in loc.Extents) total += len;
        if (total <= 0 || loc.Base < 0 || (long)dataStart + loc.Base + total > dataEnd)
        {
            r.Determinate = false;
            r.FailReason = $"[IsoBmff] {what} extent 越界（base={loc.Base} 合计={total} 数据区=[{dataStart},{dataEnd})）";
            return null;
        }
        var buf = new byte[total];
        int w = 0;
        foreach (var (off, len) in loc.Extents)
        {
            if (off < 0 || len < 0 || (long)dataStart + loc.Base + off + len > dataEnd)
            {
                r.Determinate = false; r.FailReason = $"[IsoBmff] {what} extent 越界（off={off} len={len}）";
                return null;
            }
            Array.Copy(d, dataStart + loc.Base + off, buf, w, len);
            w += len;
        }
        return buf;
    }

    /// <summary>
    /// 解析 `grid` 承载的增益图（2026-11-02，iPhone HDR HEIC 实测形态）：
    /// `dimg: tmap → [底图, grid]`、`dimg: grid → [瓦片…]`，瓦片是独立编解码 item（hvc1/av01），
    /// 每个瓦片有自己的 `hvcC` / `ispe`；`grid` 与 `tmap` 载荷常驻 `meta/idat`（construction_method=1）。
    /// <para>
    /// 失败一律 **fail-closed + 点名**（沿用本类既有契约）：grid 载荷不可解 / 瓦片数不匹配 /
    /// 瓦片类型混装 / 某瓦片无 `hvcC` / 长度前缀断链 —— 每个分支都有具体 `FailReason`。
    /// </para>
    /// </summary>
    private static void ParseGridGainMap(byte[] d, Dictionary<int, string> items,
        Dictionary<int, (int Method, int Base, List<(int Off, int Len)> Extents)> locs,
        List<(string Type, int From, List<int> To)> refs,
        List<(string Type, int Off, int Size)> ipcoProps, Dictionary<int, List<int>> ipmaMap,
        int idatStart, int idatEnd, Result r)
    {
        int gridId = r.GainMapItemId;
        var payload = SliceItem(d, locs, gridId, idatStart, idatEnd, ref r, "gainmap grid");
        if (payload == null) return;
        if (!TryParseGridPayload(payload, out int rows, out int cols, out int gw, out int gh, out var gErr))
        {
            r.Determinate = false;
            r.FailReason = $"[IsoBmff] grid 载荷解析失败：{gErr}";
            return;
        }

        // 瓦片 = grid 的 dimg 目标（顺序 = raster 顺序：左→右、上→下）
        var tileIds = new List<int>();
        foreach (var rf in refs)
        {
            if (rf.Type != "dimg" || rf.From != gridId) continue;
            foreach (var to in rf.To) if (!tileIds.Contains(to)) tileIds.Add(to);
        }
        int expectTiles = rows * cols;
        if (tileIds.Count != expectTiles)
        {
            r.Determinate = false;
            r.FailReason = $"[IsoBmff] grid 瓦片数 = {tileIds.Count}，与 {rows}x{cols} = {expectTiles} 不符";
            return;
        }

        var tiles = new List<byte[]>();
        string? codec = null;
        foreach (var tid in tileIds)
        {
            if (!items.TryGetValue(tid, out var ty) || !IsCodecItem(ty))
            {
                r.Determinate = false;
                r.FailReason = $"[IsoBmff] grid 瓦片 item {tid} 类型 '{ty ?? "?"}' 不是编解码 item";
                return;
            }
            if (codec == null) codec = ty;
            else if (codec != ty)
            {
                r.Determinate = false;
                r.FailReason = $"[IsoBmff] grid 瓦片类型混装（{codec} vs {ty}）";
                return;
            }
            var raw = SliceItem(d, locs, tid, idatStart, idatEnd, ref r, $"gainmap grid tile {tid}");
            if (raw == null) return;
            byte[]? stream;
            if (ty is "hvc1" or "hev1")
            {
                if (!TryFindHvcC(ipcoProps, ipmaMap, tid, out int hOff, out int hLen, out var hErr))
                {
                    r.Determinate = false;
                    r.FailReason = $"[IsoBmff] grid 瓦片 item {tid} 需要 hvcC：{hErr}";
                    return;
                }
                if (!TryParseHvcC(d, hOff, hLen, out int lengthSize, out var paramSets, out var pErr))
                {
                    r.Determinate = false;
                    r.FailReason = $"[IsoBmff] grid 瓦片 hvcC 解析失败：{pErr}";
                    return;
                }
                stream = ToAnnexB(raw, lengthSize, paramSets, out var cErr);
                if (stream == null)
                {
                    r.Determinate = false;
                    r.FailReason = $"[IsoBmff] grid 瓦片 item {tid} 长度前缀转换失败：{cErr}";
                    return;
                }
            }
            else stream = raw;                          // av01 ⇒ 原始 OBU
            tiles.Add(stream);
        }

        // 瓦片尺寸（ispe 挂在每个瓦片上）；缺失 ⇒ 无法得知单块尺寸（拼接与裁切都依赖它）⇒ fail-closed。
        if (!TryFindIspe(d, ipcoProps, ipmaMap, tileIds[0], out int tw, out int th) || tw <= 0 || th <= 0)
        {
            r.Determinate = false;
            r.FailReason = "[IsoBmff] grid 瓦片无有效 ispe（无法得知单块尺寸）";
            return;
        }
        // 声明缺省（flags bit0=0）⇒ 全铺不裁：整幅 = 列×瓦片宽 / 行×瓦片高。
        if (gw <= 0 || gh <= 0) { gw = cols * tw; gh = rows * th; }

        r.GainMapCodec = codec;
        r.GainMapFfmpegFormat = codec == "av01" ? "obu" : "hevc";
        r.GainMapTiles = tiles;
        r.GridRows = rows;
        r.GridCols = cols;
        r.GridWidth = gw;
        r.GridHeight = gh;
        r.TileWidth = tw;
        r.TileHeight = th;
    }

    /// <summary>
    /// `grid` 派生项载荷（ISO/IEC 23008-12，仅 version 0 定义）：
    /// <c>u8 version</c> <c>u8 flags</c> <c>u8 rowsMinusOne</c> <c>u8 colsMinusOne</c>，
    /// flags bit0=1 时再跟 <c>u32 outputWidth</c> <c>u32 outputHeight</c>
    /// （整幅显示尺寸，可小于瓦片铺满的尺寸 ⇒ 右/下裁切；实测 iPhone 件即如此）。
    /// </summary>
    private static bool TryParseGridPayload(byte[] p, out int rows, out int cols,
        out int width, out int height, out string err)
    {
        rows = 0; cols = 0; width = 0; height = 0; err = "";
        if (p.Length < 4) { err = $"grid 载荷过短（{p.Length} B）"; return false; }
        if (p[0] != 0) { err = $"grid version={p[0]}（仅 v0 定义）"; return false; }
        byte flags = p[1];
        rows = p[2] + 1;
        cols = p[3] + 1;
        int q = 4;
        if ((flags & 1) != 0)
        {
            if (p.Length < q + 8) { err = $"grid 载荷声明了宽高但缺字节（{p.Length} B）"; return false; }
            width = (int)Be32(p, q);
            height = (int)Be32(p, q + 4);
            q += 8;
        }
        return true;
    }

    /// <summary>
    /// 找该 item 的 `ispe` 属性并读出宽高。`ispe` 内容 = 4 字节 version/flags 后紧跟
    /// <c>u32 image_width</c> / <c>u32 image_height</c>；只走 `ipma` 关联（与 `TryFindHvcC` 同纪律，不猜）。
    /// </summary>
    private static bool TryFindIspe(byte[] d, List<(string Type, int Off, int Size)> props,
        Dictionary<int, List<int>> ipma, int itemId, out int w, out int h)
    {
        w = 0; h = 0;
        if (!ipma.TryGetValue(itemId, out var idxs)) return false;
        foreach (var ix in idxs)
        {
            if (ix < 1 || ix > props.Count || props[ix - 1].Type != "ispe") continue;
            var (type, off, size) = props[ix - 1];
            if (size < 12) continue;
            w = (int)Be32(d, off + 4);
            h = (int)Be32(d, off + 8);
            return w > 0 && h > 0;
        }
        return false;
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
