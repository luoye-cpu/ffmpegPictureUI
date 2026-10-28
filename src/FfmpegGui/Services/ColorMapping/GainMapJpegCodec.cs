using System;
using System.Globalization;
using FfmpegGui.Models;

namespace FfmpegGui.Services.ColorMapping;

/// <summary>
/// GainMap（Ultra HDR JPEG）**解码口径与编码参数的单一真值**。
///
/// 存在的理由：GainMap 原先只有一条专用管线（<c>QueueProcessor.ProcessGainMapJpegAsync</c>）。
/// 2026-09-16 起引擎也要能产出 GainMap（<see cref="RawColorPipeline"/> 的 GainMap 出口），
/// 若两边各写一份 zscale 滤镜串 / 峰值公式，就会出现本项目反复修过的「两处口径不一致」——
/// 同一素材走引擎与走传统管线得到不同 headroom（偏暗或增益图范围错），且极难定位。
/// ⇒ 解码串、回退解码串、平面→RGBA 交错、编码参数解析**全部集中在此**，两条路径都只调这里。
///
/// 像素约定（<see cref="GainMapEncoder.EncodeAsync"/> 的输入契约）：
/// 交错 RGBA、**线性**、alpha=1、**1.0 = hdrPeakNits**（zscale 的 <c>npl</c> 语义）。
/// 归一化 → 分段 Reinhard → 底图原色矩阵 → gamma → cjpegli → XMP/MPF 打包都在
/// <see cref="GainMapEncoder"/> **内部**完成 ⇒ 调用方**不得**再做色域映射（会双重施加）。
/// </summary>
public static class GainMapJpegCodec
{
    /// <summary>
    /// 线性化解码滤镜（zscale）。这些参数**没有一个是可选的**：
    /// <list type="bullet">
    /// <item><c>t=linear</c>：把源像素搬到线性域（PQ/HLG/SDR 统一）。</item>
    /// <item><c>npl={nits}</c>：声明"线性 1.0 = 多少 nits"。HDR 源必须给实际峰值，
    /// 否则 1.0 的物理含义错位 ⇒ headroom 算错 ⇒ 增益图范围错。</item>
    /// <item><c>pin={srcPrim}</c> / <c>p={srcPrim}</c>：**锁定源原色语义**并显式给出输出原色。
    /// zscale 不改原色，所以"线性像素处于哪个原色"必须显式声明，否则 <see cref="GainMapEncoder"/>
    /// 会拿探测到的 ICC 描述去乘一套不属于它的原色矩阵。</item>
    /// <item><c>tin={srcTrc}</c>：**显式给出源传递特性**。2026-10-28 实测：容器不带 CICP trc 时
    /// （未标注 PNG、只带 ICC 的素材）zscale 从帧标签读不到 trc ⇒ 直接报
    /// <c>code 3074: no path between colorspaces</c>，调用方落到"直解"兜底，把**已 gamma 化的值当线性**用
    /// ⇒ 底图被二次编码提亮（同一素材纯色块实测 13→64、128→189，而走普通出口逐位不变）。
    /// ⚠ 只补 tin 不够：不显式给输出 <c>p=</c> 时实测仍报 3074。</item>
    /// <item><c>r=pc</c>：全范围。线性浮点本无 YUV 范围语义，但 zscale 要求显式给出。</item>
    /// </list>
    /// ⚠ 本串是**唯一真值**：传统 GainMap 管线与引擎 GainMap 出口都调它，禁止第二份。
    /// </summary>
    public static string LinearDecodeFilter(double nits, string srcPrim, string srcTrc)
        => "zscale=t=linear:p=" + srcPrim + ":tin=" + srcTrc
         + ":npl=" + nits.ToString("0.######", CultureInfo.InvariantCulture)
         + ":r=pc:pin=" + srcPrim;

    /// <summary>首选解码命令行：zscale 线性化 → gbrpf32le 平面浮点 rawvideo。
    /// ⚠ `-frames:v 1` **不可省**（2026-09-18 实测缺陷，与 `RawColorPipeline` 主解码同一形态）：
    /// 下游（<see cref="RawColorPipeline.TransformGainMapAsync"/> / 传统 GainMap 管线）把这条 raw
    /// **按 `w×h` 单帧**解释（`raw.Length < pixelCount*12` 只做**下界**检查 ⇒ 多帧会被静默放行，
    /// 再按单帧切平面 ⇒ 底图错位）。`-frames:v` 是**输出侧**选项，必须放在 `-i` **之后**。</summary>
    public static string LinearDecodeArgs(string inputPath, string rawPath, double nits,
        string srcPrim, string srcTrc)
        // 形态由输入的图形态决定（分块流组 ⇒ -filter_complex，见 FfmpegCommandBuilder.ChainArgs）；
        // withLabelReset=true 保留：拼图会把输出帧标签洗成 unknown（实测报 pc, smpte170m/unknown/unknown），
        // 而本串的 pin/tin/p 已不依赖帧标签 ⇒ 洗掉的是**残留**，不是 zscale 的取数来源。
        => $"-y -i \"{inputPath}\" -frames:v 1 {FfmpegCommandBuilder.ChainArgs(inputPath, LinearDecodeFilter(nits, srcPrim, srcTrc), withLabelReset: true)} -pix_fmt gbrpf32le -f rawvideo \"{rawPath}\"";

    /// <summary>
    /// 直解命令行：输入**已是线性**（dngtool 产出的线性 TIFF、解码出的 HDR PFM）⇒ 直接取平面浮点
    /// （与首选同样的像素格式，下游无需分支）。
    /// ⚠ 2026-10-28 口径收紧：本支只允许两种输入——"探测确认已是线性"，或"源曲线无法交给 zscale
    ///   命名 ⇒ 直解后由托管 EOTF 线性化"。把它当 zscale 失败的**通用兜底**会产出错图：gamma 值被当
    ///   线性 ⇒ 底图二次编码提亮（实测 13→64、128→189）。其余情形 zscale 失败必须响亮失败。
    /// ⚠ 同样必须有 `-frames:v 1`（理由见 <see cref="LinearDecodeArgs"/>；两条是同一契约的两种输入）。
    /// </summary>
    public static string DirectDecodeArgs(string inputPath, string rawPath)
        => $"-y -i \"{inputPath}\" -frames:v 1 -pix_fmt gbrpf32le -f rawvideo \"{rawPath}\"";

    /// <summary>
    /// gbrpf32le 平面序 → RGBA 交错。
    /// 平面顺序是 <b>G, B, R</b>（ffmpeg 的 gbr 命名，不是 RGB），每平面 <paramref name="pixelCount"/> 个 float；
    /// alpha 恒 1（GainMap 无透明通道语义）。长度不足由调用方先行拒绝（本函数按给定像素数盲读）。
    /// </summary>
    public static float[] GbrPlanarToRgba(byte[] raw, int pixelCount)
    {
        var gPlane = new float[pixelCount];
        var bPlane = new float[pixelCount];
        var rPlane = new float[pixelCount];
        Buffer.BlockCopy(raw, 0, gPlane, 0, pixelCount * 4);
        Buffer.BlockCopy(raw, pixelCount * 4, bPlane, 0, pixelCount * 4);
        Buffer.BlockCopy(raw, pixelCount * 8, rPlane, 0, pixelCount * 4);
        var rgba = new float[pixelCount * 4];
        for (int i = 0; i < pixelCount; i++)
        {
            int o = i * 4;
            rgba[o] = rPlane[i];
            rgba[o + 1] = gPlane[i];
            rgba[o + 2] = bPlane[i];
            rgba[o + 3] = 1f;
        }
        return rgba;
    }

    /// <summary>
    /// 按 <see cref="EncodeParams"/> 分派解码串 —— 三条臂的**唯一**分派点（传统 GainMap 管线与引擎
    /// GainMap 出口都调它，两处不得再各自写分支）：
    /// <list type="bullet">
    /// <item><see cref="EncodeParams.AlreadyLinear"/>：直解。对线性值再套任何 EOTF 都会压暗整图。</item>
    /// <item><see cref="EncodeParams.ManagedEotf"/>：直解拿到的是**编码值**，随后必须由
    ///   <see cref="DecodeToRgba"/> 施加托管 EOTF —— zscale 命名不了这条源曲线，**绕开 zscale 不等于绕开 gamma**。</item>
    /// <item>其余：zscale 线性化（tin/pin/p 显式给全，不依赖帧标签）。</item>
    /// </list>
    /// ⚠ 这里**没有**"zscale 失败再直解"这一支：兜底直解把 gamma 值当线性用（2026-10-28 实测底图
    /// 13→64、128→189 的成因），zscale 真失败时调用方必须响亮失败。
    /// </summary>
    public static string DecodeArgsFor(EncodeParams p, string inputPath, string rawPath)
        => p.AlreadyLinear || p.ManagedEotf
            ? DirectDecodeArgs(inputPath, rawPath)
            : LinearDecodeArgs(inputPath, rawPath, p.Nits, p.SrcPrim, p.SrcTrc);

    /// <summary>
    /// 平面浮点 → RGBA 交错，并在 <see cref="EncodeParams.ManagedEotf"/> 臂上**就地补完线性化**。
    /// 交错与 EOTF 绑成一个调用：分成两步就会有人漏掉第二步（漏掉的产物是提亮的错图，不是报错）。
    /// 调用方仍须自行校验 <c>raw.Length ≥ pixelCount*12</c>（本函数按给定像素数盲读）。
    /// </summary>
    public static float[] DecodeToRgba(EncodeParams p, byte[] raw, int pixelCount)
    {
        var rgba = GbrPlanarToRgba(raw, pixelCount);
        if (p.ManagedEotf && p.SrcCurve is { } srcCurve)
            // 线性化只借用引擎的同一份内核与同一份曲线（取自源 ICC 描述符）；此处不做任何色彩决策。
            ColorKernels.DecodeToLinearInPlace(rgba, new TransformSpec
            {
                SrcCurve = srcCurve,
                DstCurve = TransferCurve.Linear,
            });
        return rgba;
    }

    /// <summary>
    /// zscale 能否按这个传递特性名做像素转换。**取值表来自本机工具**（<c>ffmpeg -h filter=zscale</c>
    /// 的 transferin 允许值）。它与 <c>ColorSpaceRegistry.ValidateCicpTriple</c> 那张"非阻拦常用值"
    /// 表问的不是同一件事（那张判"该不该提醒用户"，这张判"能不能做像素转换"）⇒ 不合并、不互为副本。
    /// 不在表内 ⇒ 调用方改走托管 EOTF，不得直解。
    /// </summary>
    public static bool IsZscaleNameableTransfer(string? trc)
        => (trc ?? "").Trim().ToLowerInvariant() switch
        {
            "bt709" or "iec61966-2-1" or "iec61966-2-4" or "linear" or "bt470m" or "bt470bg"
            or "smpte170m" or "smpte240m" or "log100" or "log316" or "bt2020-10" or "bt2020-12"
            or "smpte2084" or "arib-std-b67" => true,
            _ => false,
        };

    /// <summary>一次 GainMap 编码的全部标量参数（由 <see cref="ResolveEncodeParams"/> 产出）。</summary>
    public sealed class EncodeParams
    {
        /// <summary>源是否为 HDR 传递（PQ/HLG）。SDR 源 ⇒ 无增益（headroom=1）。</summary>
        public bool IsHdrInput;
        /// <summary>
        /// 源**已经是线性光**（Ultra HDR / tmap / 厂商尾注经 GainMapDecoder 解码出的 PFM）。
        /// 这类输入必须跳过 zscale 的 PQ/HLG EOTF —— 再套一次会把整张图压暗，且 PFM 没有容器标签，
        /// 按 ffprobe 判只会得到「SDR」，于是刚恢复出来的 HDR 层在出口又被丢掉。
        /// </summary>
        public bool AlreadyLinear;
        /// <summary>zscale <c>npl</c> 用的名义峰值 nits（HDR 才有意义）。</summary>
        public double Nits;
        /// <summary>传给编码器的 HDR 峰值 nits（SDR 源固定 = SDR 白点 ⇒ headroom=1）。</summary>
        public float PeakNits;
        /// <summary>线性像素所处的源原色（CICP token）。</summary>
        public string SrcPrim = "bt709";
        /// <summary>源传递特性（喂给 zscale 的 <c>tin</c>）。它从哪来见 <see cref="SrcTrcSource"/>。</summary>
        public string SrcTrc = "iec61966-2-1";
        /// <summary>SrcTrc 的来源文本（日志自证，与 <see cref="PeakSourceText"/> 同形态）。</summary>
        public string SrcTrcSource = "";
        /// <summary>ICC 度量识别出的源空间名（ProPhoto RGB / Display P3 …）；null = 只有 CICP 标签。
        /// CICP 表达不了的原色（超广/具名 ICC 空间）由它去 <c>ColorSpaceRegistry.LinearMatrix</c> 求矩阵。</summary>
        public string? SourceSpaceName;
        /// <summary>源曲线已知但 zscale 无法命名（LUT 型/自定义曲线）⇒ 直解 + **托管 EOTF**，
        /// 绝不把编码值当线性用。</summary>
        public bool ManagedEotf;
        /// <summary><see cref="ManagedEotf"/> 时施加的源曲线（取自 ICC 描述符，不另写公式）。</summary>
        public TransferCurve? SrcCurve;
        /// <summary>探测出的 <see cref="SrcPrim"/> 只是**近似值**（源色域无 CICP 命名：ProPhoto /
        /// Adobe RGB / ColorMatch ⇒ 探测侧按注释归成 bt709 并登记此位）。true 时编码器改按
        /// <see cref="SourceSpaceName"/> 这个具名空间求底图矩阵，不把近似 token 当源的真实原色。</summary>
        public bool PrimariesApproximated;
        /// <summary>容器 HDR 静态元数据里的峰值（0 = 无）。</summary>
        public float MetaPeak;
        /// <summary>峰值来源文本（日志自证用：用户指定 / 静态元数据 xxx / 名义 1000）。</summary>
        public string PeakSourceText = "";
        /// <summary>源是否带 alpha（AVIF 增益图出口当前不携带 ⇒ 必须点名，不得静默拍平）。</summary>
        public bool HasAlpha;
    }

    /// <summary>
    /// 解析 GainMap 编码参数。**与旧专用管线逐条同源**（2026-09-16 抽出，逻辑未改）：
    /// 峰值优先级 = 用户 <c>JpegGainMapTargetNits</c>（默认 1000，故实际上总是走这支）
    /// &gt; 容器 HDR 静态元数据（MaxCLL 优先、其次 BS.2086 mastering）&gt; 名义 1000。
    /// 原色 = 探测到的 CICP primaries，缺失时取源色域名对应的 ffmpeg primaries，兜底 bt709；
    /// 源色域**没有 CICP 命名**时（ProPhoto/Adobe/ColorMatch）另交 <c>SourceSpaceName</c> +
    /// <c>PrimariesApproximated</c>，让编码器改按具名空间求矩阵。
    /// 传递特性 = 上游输入声明 &gt; 容器 CICP &gt; 源 ICC 吻合 CICP &gt; ICC 托管 EOTF &gt; sRGB 惯例假定
    /// （见下面的五级注释；线性输入单独登记 <c>AlreadyLinear</c>）。
    /// </summary>
    public static EncodeParams ResolveEncodeParams(FfmpegOptions o, string inputPath)
    {
        var p = new EncodeParams();
        try
        {
            // ── 已解码的线性 HDR 输入（Ultra HDR JPEG / ISO-BMFF tmap / 厂商 local.hdr 尾注）──────
            //   GainMapDecoder 把这类输入换成 **PFM**：线性、1.0 = SDR 白点，峰值与底图原色在解码期
            //   就登记到 Options 上了。PFM 没有容器色彩标签 ⇒ 只看 ffprobe 必然得出
            //   「SDR ⇒ headroom 1.0 ⇒ 不写增益图」，于是刚恢复的 HDR 层在出口又被丢掉
            //   （2026-10-14 实测：OnePlus 分块 HEIC → AVIF，产物 `tmap` 计数 0、峰值报 203）。
            //   ⇒ 以 DecodedUltraHdr* 为权威，并让调用方据此跳过 zscale。
            if (o.DecodedUltraHdrPeakNits > 0)
            {
                p.AlreadyLinear = true;
                p.PeakNits = o.DecodedUltraHdrPeakNits;
                p.Nits = p.PeakNits;
                p.MetaPeak = p.PeakNits;
                p.IsHdrInput = p.PeakNits > GainMapEncoder.KSdrWhiteNits * 1.001f;
                p.SrcPrim = o.DecodedUltraHdrColorSpace switch
                {
                    var cs when cs != null && cs.Contains("_P3_", StringComparison.Ordinal) => "smpte432",
                    var cs when cs != null && cs.Contains("_202_", StringComparison.Ordinal) => "bt2020",
                    _ => "bt709",
                };
                p.PeakSourceText = "解码后的线性 HDR（DecodedUltraHdrPeakNits）";
                // 本支**不进 zscale**（AlreadyLinear ⇒ 直解）：SrcTrc 只是把"为什么不需要 tin"写清楚，
                // 否则日志会打出一条默认值冒充取数来源。
                p.SrcTrc = "linear";
                p.SrcTrcSource = "解码产物已是线性（不套任何 EOTF）";
                return p;
            }
            var meta = FfmpegCommandBuilder.ProbeInputColorMetadata(inputPath);
            p.HasAlpha = meta.hasAlpha;
            p.MetaPeak = (float)meta.peakNits;
            string metaPeakFrom = string.IsNullOrWhiteSpace(meta.peakSource) ? "元数据" : meta.peakSource!;
            p.IsHdrInput = meta.colorTrc is "smpte2084" or "arib-std-b67";
            p.SrcPrim = !string.IsNullOrWhiteSpace(meta.colorPrimaries)
                        && meta.colorPrimaries != "unspecified" && meta.colorPrimaries != "unknown"
                ? meta.colorPrimaries!
                : (ColorSpaceRegistry.FfmpegTokens(meta.sourceGamutName).primaries ?? "bt709");

            // 源空间名 / 近似登记：与上面的 SrcPrim 同源，**与 trc 走哪一支无关**。
            // （ProPhoto 类素材的 SrcPrim 被归成 bt709 且 colorTrc 也被归成 bt709 ⇒ 下面落在"CICP 可命名"
            //   一支；若把这两行放进 else 分支，编码器就拿不到具名空间，仍然把近似 token 当真原色用。）
            if (!string.IsNullOrWhiteSpace(meta.sourceGamutName)) p.SourceSpaceName = meta.sourceGamutName!;
            p.PrimariesApproximated = meta.primariesApproximated;

            // ── 源传递特性（tin / 是否直解）：五级优先，**不许靠"zscale 失败再直解"的兜底** ──────────
            //   ① 上游**输入声明** <c>ColorSourceDeclTrc</c>：RAW 中间件是 dngtool/LibRaw 的**线性** TIFF，
            //      容器里没有任何色彩标签 ⇒ 探测只会得出"无标签"，按 sRGB 惯例假定就是给线性值再套一次
            //      EOTF（压暗）；声明是上游唯一一手事实，必须优先于探测。
            //   ② 容器 CICP/EXIF 传递。
            //   ③ 源 ICC 度量曲线恰好吻合一个 CICP token ⇒ 交给 zscale 命名。
            //   ④ 源 ICC 有曲线但 zscale 命名不了 ⇒ 直解 + **托管 EOTF**（曲线取自同一份描述符，不另写公式）。
            //   ⑤ 什么标签都没有 ⇒ 按未标注 8-bit RGB 的 sRGB 惯例假定（日志必须点名这是"假定"）。
            // 缺 ③④⑤ 的历史代价（2026-10-28 实测）：zscale 报 code 3074 ⇒ 调用方落到"直解"兜底 ⇒
            // gamma 值被当线性 ⇒ 底图二次编码提亮（同一素材纯色块实测 13→64、128→189）。
            // ⚠ ICC 优先的两种情形：容器命名不了（②落空），或这一对 primaries/trc 本身就是
            //   `ApplyIccSemantics` 从 ICC **合成**的近似（Adobe/ColorMatch/ProPhoto 统一写了 bt709）——
            //   后者若不走 ICC，就等于拿一条与源无关的 BT.709 曲线去解 ProPhoto 的 1.8+趾。
            string declTrc = (o.ColorSourceDeclTrc ?? "").Trim().ToLowerInvariant();
            bool trcFromDecl = declTrc.Length > 0;
            string trc = trcFromDecl ? declTrc : (meta.colorTrc ?? "").Trim().ToLowerInvariant();
            bool iccNeeded = trc != "linear" && (!IsZscaleNameableTransfer(trc) || p.PrimariesApproximated);
            var iccDesc = iccNeeded ? FfmpegCommandBuilder.ProbeInputIccDescriptor(inputPath) : null;

            if (trc == "linear")
            {
                // 输入已是线性 ⇒ 不进 zscale（套任何 EOTF 都是二次解码），直接取平面浮点。
                p.AlreadyLinear = true;
                p.SrcTrc = "linear";
                p.SrcTrcSource = trcFromDecl ? "上游输入声明 ColorSourceDeclTrc=linear" : "容器 CICP 传递=linear";
            }
            else if (IsZscaleNameableTransfer(trc) && !p.PrimariesApproximated)
            {
                p.SrcTrc = trc;
                p.SrcTrcSource = trcFromDecl ? "上游输入声明 ColorSourceDeclTrc" : "容器 CICP/EXIF 传递";
            }
            else
            {
                string iccTok = iccDesc?.Curve.CicpToken ?? "";
                if (IsZscaleNameableTransfer(iccTok))
                {
                    p.SrcTrc = iccTok;
                    p.SrcTrcSource = "源 ICC 曲线吻合 CICP";
                }
                else if (iccDesc != null)
                {
                    p.SrcTrc = "iec61966-2-1";
                    p.SrcTrcSource = "源 ICC 曲线无法被 zscale 命名 ⇒ 直解后由托管 EOTF 线性化";
                    p.ManagedEotf = true;
                    p.SrcCurve = iccDesc.Curve;
                }
                else if (IsZscaleNameableTransfer(trc))
                {
                    // 近似登记在案却读不到 ICC 描述符（缓存里没有可度量的曲线）⇒ 仍用容器/声明的 token，
                    // 并把"这一侧只有 token 可依"写进来源文本 —— 不许伪装成 ICC 实测。
                    p.SrcTrc = trc;
                    p.SrcTrcSource = trcFromDecl
                        ? "上游输入声明 ColorSourceDeclTrc（ICC 描述符不可用，未复核曲线）"
                        : "容器 CICP/EXIF 传递（ICC 描述符不可用，未复核曲线）";
                }
                else
                {
                    p.SrcTrc = "iec61966-2-1";
                    p.SrcTrcSource = "无 CICP 也无可用 ICC ⇒ 按未标注 8-bit RGB 的 sRGB 惯例假定";
                }
            }

            // 峰值来源：>0 = 用户显式指定；0 = auto → 读 HDR 静态元数据，无则名义 1000。
            // 绝不无条件取 1000：4000nit 素材被按 1000nit 处理会得到大 headroom 往返偏暗。
            p.Nits = o.JpegGainMapTargetNits > 0
                ? o.JpegGainMapTargetNits
                : p.MetaPeak > 0 ? p.MetaPeak : 1000f;
            p.PeakNits = p.IsHdrInput ? (float)p.Nits : GainMapEncoder.KSdrWhiteNits;
            p.PeakSourceText = o.JpegGainMapTargetNits > 0 ? "用户指定"
                : p.MetaPeak > 0 ? $"HDR 静态元数据 {metaPeakFrom}" : "名义 1000，无元数据";
        }
        catch
        {
            // 探测异常（ffprobe/exiftool 不可用等）：按业界惯例假定 sRGB + 名义峰值，不抛。
            p.SrcPrim = "bt709";
            p.SrcTrc = "iec61966-2-1";
            p.SrcTrcSource = "探测失败 ⇒ 按 sRGB 惯例假定（未采信任何标签）";
            p.IsHdrInput = false;
            p.Nits = o.JpegGainMapTargetNits > 0 ? o.JpegGainMapTargetNits : 1000f;
            p.PeakNits = GainMapEncoder.KSdrWhiteNits;
            p.PeakSourceText = "探测失败 → 名义 1000，无元数据";
        }
        if (string.IsNullOrWhiteSpace(p.SrcPrim) || p.SrcPrim is "unspecified" or "unknown")
            p.SrcPrim = "bt709";
        return p;
    }

    /// <summary>
    /// GainMap 底图/增益图 JPEG 的 cjpegli 渐进级别（`-p N`）**单一真值**。
    ///
    /// <para>
    /// 为什么需要它：`--jpeg-progressive`（<see cref="FfmpegOptions.JpegProgressiveId"/>，取值
    /// `-1=自动 / 0=基线 / 1=渐进`）与 cjpegli 的 `-p N`（`0..2`）**取值域不同**，映射写两份必然漂移。
    /// 2026-09-19 之前该映射只存在于 GUI（`MainWindow.xaml.cs`），且它写进的
    /// `CjpegliProgressiveId` **GainMap 路径从不读** ⇒ 是死代码：用户设的渐进被静默丢弃
    /// （实测：`--jpeg-progressive 0` 与 `1` 的 Ultra HDR 产物 MD5 **完全相同**，恒为渐进）。
    /// </para>
    /// <para>
    /// 返回值：`1 → 2`（渐进，扫描更多）、`0 → 0`（基线）、其余（含 `-1` 自动）`→ -1`
    /// （= **不传** `-p`，交 cjpegli 自身默认）。
    /// ⚠ 模型默认值是 `0`（基线，与 GUI 的 `JpegProgressiveCombo` 默认档一致）⇒ 不传该选项时
    /// Ultra HDR 底图为**基线** JPEG。
    /// </para>
    /// <para>
    /// ⚠ 覆盖范围（2026-09-19 收口后）：**两条 GainMap 路径都消费本映射** ——
    ///   ① 引擎 GainMap 出口（<c>RawColorPipeline.cs:324-338</c>）；
    ///   ② <c>QueueProcessor</c> 的 GainMap **专用管线**（`ProcessGainMapJpegAsync`）。
    ///   收口前 ① 已接、② 未接（吃默认 `-1` ⇒ 无 `-p` ⇒ cjpegli 默认 2 = 渐进），
    ///   且 `--jpeg-progressive 1` 会被 `ColorEngineRouter` 的 `ProgressiveJpeg` 拦到 ②
    ///   ⇒ 那一路**碰巧**正确但不是由本单一真值保证的。现已把该拦截对 GainMap 放行
    ///   （依据：GainMap 底图/增益图恒由 **cjpegli** 编码，cjpegli **支持** `-p 0..2`，
    ///   故「ffmpeg 的 mjpeg 无渐进能力」对 GainMap 不成立）⇒ ① 成为 GainMap 的常态出口，
    ///   ② 只在引擎不可走时接手（当前唯一活跃触发点 = 动画输入 + GainMap），且同样下发本映射。
    /// </para>
    /// <para>
    /// ⚠ 变异验证（2026-09-19，用来区分「接线」与「碰巧」）：把本函数改为恒 `-1` ⇒
    ///   `--jpeg-progressive 0` 与 `1` 的 Ultra HDR 产物 MD5 在**两条路径上都变得相同**
    ///   （引擎出口 `59e1644482dd4c1fa93b35516a308b4a`、专用管线 `c8a07d432ab713a50d8366f6e2cc1fd3`）
    ///   ⇒ 反证绿态下二者的差异**来自本映射的接线**，不是 cjpegli 默认值碰巧。
    /// </para>
    /// </summary>
    public static int ProgressiveLevelOf(FfmpegOptions o)
        => o.JpegProgressiveId switch { 1 => 2, 0 => 0, _ => -1 };

    /// <summary>
    /// 底图（base rendition）的 cjpegli 色度采样：**4:2:0**。
    /// 底图是一张普通 SDR 照片，色度降采样是行业默认（也与我们出货的 Ultra HDR 底图一致）⇒ 不动。
    /// </summary>
    public const string BaseJpegChromaSubsampling = "420";

    /// <summary>
    /// 增益图 JPEG 的 cjpegli 色度采样（<b>与声明的通道数成套</b>，F-4，2026-10-11）。
    /// <para>
    /// <c>multiChannel=true ⇒ "444"</c>：参照的三通道增益图就是 **24bppRGB888 + 采样因子全 1×1**
    /// （libultrahdr <c>jpegencoderhelper.cpp:41-42</c> 的因子表，应用点 <c>:189-192</c> 逐分量
    /// <c>h_samp_factor/v_samp_factor</c>，<c>:197</c> RGB 非 raw YCbCr ⇒ 色度**零降采样**），
    /// 图像格式的选择见 <c>jpegr.cpp:714-716</c>。逐通道增益写进 R/G/B 三个分量后再过 4:2:0，
    /// G/B 的增益就被色度下采样摧毁 ⇒ 与 tmap/ISO 的 bit7 声明直接矛盾。
    /// </para>
    /// <para>
    /// ⚠ 参照的读侧「<b>信流不信 flag</b>」——<c>jpegr.cpp:2843-2844</c> 按 JPEG SOF 的
    /// <c>numComponents</c> 定输出布局——<b>救不了写侧</b>：声明与交付一致是<b>生产者</b>的义务。
    /// </para>
    /// <para>
    /// <c>multiChannel=false ⇒ "420"</c>：本仓灰度增益图目前是「灰度复制成 RGB 三分量」
    /// （见 <c>GainMapEncoder.WriteGrayOrRgbPngAsync</c>，F12 议题），三分量同值 ⇒ Y = 该值
    /// （BT.709 加权和恰为原值），解侧按亮度取增益（<c>GainMapDecoder.BilinearGray</c>）不受色度
    /// 降采样影响 ⇒ 4:2:0 无害、也不与「1 参数集」的声明矛盾。
    /// 参照的更彻底形态是单分量灰度 JPEG（<c>jpegencoderhelper.cpp:27-28</c> 的 400 = 1 分量），
    /// 那属 F12（灰度件别绕道 RGB），不在本条范围。
    /// </para>
    /// </summary>
    public static string GainMapChromaSubsampling(bool multiChannel) => multiChannel ? "444" : "420";

    /// <summary>
    /// 组装 cjpegli 命令行（<b>单一真值</b>：增益图与底图共用此式，色度采样由调用方按上面两条给定）。
    /// <para>
    /// 为什么把它从 <c>GainMapEncoder.EncodeJpegFileAsync</c> 里搬出来：批次 C 的判据要能**直接量到**
    /// 产品真正下发给 cjpegli 的那串参数（含 <c>--chroma_subsampling</c>），而不是在测试里再拼一遍
    /// —— 本仓刚因「contract 两份拷贝」踩过「全绿但零执行」，同一份字符串只准有一份。
    /// </para>
    /// </summary>
    /// <param name="progressiveLevel">cjpegli 的 <c>-p N</c>；<c>-1</c> = 不传，沿用 cjpegli 默认（=2 渐进）。</param>
    public static string CjpegliCommandArguments(string pngPath, string jpgPath, float distance,
        int progressiveLevel, string chromaSubsampling)
    {
        string progArg = progressiveLevel >= 0 ? $" -p {progressiveLevel}" : "";
        return $"\"{pngPath}\" \"{jpgPath}\" --distance {distance:F1} --chroma_subsampling {chromaSubsampling}{progArg}";
    }
}
