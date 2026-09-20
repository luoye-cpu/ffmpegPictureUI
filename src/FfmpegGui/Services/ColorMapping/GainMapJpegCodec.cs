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
    /// 线性化解码滤镜（zscale）。三个参数都不是可选的：
    /// <list type="bullet">
    /// <item><c>t=linear</c>：把源像素搬到线性域（PQ/HLG/SDR 统一）。</item>
    /// <item><c>npl={nits}</c>：声明"线性 1.0 = 多少 nits"。HDR 源必须给实际峰值，
    /// 否则 1.0 的物理含义错位 ⇒ headroom 算错 ⇒ 增益图范围错。</item>
    /// <item><c>pin={srcPrim}</c>：**锁定源原色语义**。zscale 不改原色，所以"线性像素处于哪个原色"
    /// 必须显式声明，否则 <see cref="GainMapEncoder"/> 会拿探测到的 ICC 描述去乘一套不属于它的原色矩阵。</item>
    /// <item><c>r=pc</c>：全范围。线性浮点本无 YUV 范围语义，但 zscale 要求显式给出。</item>
    /// </list>
    /// ⚠ 本串是**唯一真值**：传统 GainMap 管线与引擎 GainMap 出口都调它，禁止第二份。
    /// </summary>
    public static string LinearDecodeFilter(double nits, string srcPrim)
        => "zscale=t=linear:npl=" + nits.ToString("0.######", CultureInfo.InvariantCulture)
         + ":r=pc:pin=" + srcPrim;

    /// <summary>首选解码命令行：zscale 线性化 → gbrpf32le 平面浮点 rawvideo。
    /// ⚠ `-frames:v 1` **不可省**（2026-09-18 实测缺陷，与 `RawColorPipeline` 主解码同一形态）：
    /// 下游（<see cref="RawColorPipeline.TransformGainMapAsync"/> / 传统 GainMap 管线）把这条 raw
    /// **按 `w×h` 单帧**解释（`raw.Length < pixelCount*12` 只做**下界**检查 ⇒ 多帧会被静默放行，
    /// 再按单帧切平面 ⇒ 底图错位）。`-frames:v` 是**输出侧**选项，必须放在 `-i` **之后**。</summary>
    public static string LinearDecodeArgs(string inputPath, string rawPath, double nits, string srcPrim)
        => $"-y -i \"{inputPath}\" -frames:v 1 -vf \"{LinearDecodeFilter(nits, srcPrim)}\" -pix_fmt gbrpf32le -f rawvideo \"{rawPath}\"";

    /// <summary>
    /// 回退解码命令行：输入**已是线性**（如 dngtool 产出的线性 TIFF）时 zscale 无法命名其传递特性
    /// 而失败 ⇒ 直接取平面浮点（与首选同样的像素格式，下游无需分支）。
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

    /// <summary>一次 GainMap 编码的全部标量参数（由 <see cref="ResolveEncodeParams"/> 产出）。</summary>
    public sealed class EncodeParams
    {
        /// <summary>源是否为 HDR 传递（PQ/HLG）。SDR 源 ⇒ 无增益（headroom=1）。</summary>
        public bool IsHdrInput;
        /// <summary>zscale <c>npl</c> 用的名义峰值 nits（HDR 才有意义）。</summary>
        public double Nits;
        /// <summary>传给编码器的 HDR 峰值 nits（SDR 源固定 = SDR 白点 ⇒ headroom=1）。</summary>
        public float PeakNits;
        /// <summary>线性像素所处的源原色（CICP token）。</summary>
        public string SrcPrim = "bt709";
        /// <summary>容器 HDR 静态元数据里的峰值（0 = 无）。</summary>
        public float MetaPeak;
        /// <summary>峰值来源文本（日志自证用：用户指定 / 静态元数据 xxx / 名义 1000）。</summary>
        public string PeakSourceText = "";
    }

    /// <summary>
    /// 解析 GainMap 编码参数。**与旧专用管线逐条同源**（2026-09-16 抽出，逻辑未改）：
    /// 峰值优先级 = 用户 <c>JpegGainMapTargetNits</c>（默认 1000，故实际上总是走这支）
    /// &gt; 容器 HDR 静态元数据（MaxCLL 优先、其次 BS.2086 mastering）&gt; 名义 1000。
    /// 原色 = 探测到的 CICP primaries，缺失时取源色域名对应的 ffmpeg primaries，兜底 bt709。
    /// </summary>
    public static EncodeParams ResolveEncodeParams(FfmpegOptions o, string inputPath)
    {
        var p = new EncodeParams();
        try
        {
            var meta = FfmpegCommandBuilder.ProbeInputColorMetadata(inputPath);
            p.MetaPeak = (float)meta.peakNits;
            string metaPeakFrom = string.IsNullOrWhiteSpace(meta.peakSource) ? "元数据" : meta.peakSource!;
            p.IsHdrInput = meta.colorTrc is "smpte2084" or "arib-std-b67";
            p.SrcPrim = !string.IsNullOrWhiteSpace(meta.colorPrimaries)
                        && meta.colorPrimaries != "unspecified" && meta.colorPrimaries != "unknown"
                ? meta.colorPrimaries!
                : (ColorSpaceRegistry.FfmpegTokens(meta.sourceGamutName).primaries ?? "bt709");

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
}
