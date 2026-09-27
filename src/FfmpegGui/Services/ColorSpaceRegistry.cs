using System;
using System.Collections.Generic;

namespace FfmpegGui.Services
{
    /// <summary>
    /// 色彩空间注册中心 —— 全流程唯一的色彩语义来源。
    /// 统一 UI 简化下拉、CLI、预设、ffmpeg 直接路径、cjxl/cjpegli 管道、ICC 烘焙各处对色彩空间的解释，
    /// 消除历史上 ffmpeg/管道/烘焙三套映射不一致的问题。
    ///
    /// 策略（对齐审查决策）：
    /// - 标准色域（sRGB / BT.709）：原样。
    /// - 广色域（Display P3 / BT.2020 PQ/HLG，CICP 可精确表达）：用精确 CICP + 标准 ICC（iccgen）双描述。
    /// - 不可原样表达的广色域（DCI-P3 / P3-PQ，见各 Spec 注释）：统一归一化到 BT.2020 超集容器（P3⊂BT.2020，零削色）
    ///   ——DCI-P3→BT.2020 SDR(bt709+生成ICC)；P3-PQ→BT.2020 PQ(Rec2100PQ+intensity)，均做源→BT.2020 像素 gamut 转换。
    /// - 超广色域（Adobe RGB / ProPhoto / ColorMatch，zscale/iccgen 均不支持其 primaries）：归一化到 BT.2020 描述
    ///   （像素经线性域 primaries 矩阵转换到 BT.2020，输出标 bt2020 + BT.2020 ICC）。
    /// - 软件最低 BT.709：不再提供任何低于 BT.709 的输出标记（BT.601/bt470m/bt470bg/smpte170m 一律归入 BT.709）。
    /// </summary>
    public static class ColorSpaceRegistry
    {
        public enum GamutKind { Standard, Wide, UltraWide }

        /// <summary>一个色彩空间的完整规格。</summary>
        public sealed class Spec
        {
            public string Key = "";
            public GamutKind Kind;
            // ── 归一化后的输出描述（CICP tokens，供 ffmpeg -color_* 与 zscale）──
            public string OutPrimaries = "bt709";
            public string OutTrc = "iec61966-2-1";
            public string OutMatrix = "bt709";
            // ── iccgen 参数（CICP 枚举名；生成标准 ICC）──
            public string? IccgenPrimaries;
            public string? IccgenTrc;
            // ── cjxl -x color_space 缩写（null=不指定，由容器/ICC 决定）──
            public string? CjxlColorSpace;
            // ── 源物理色度（超广色域像素转换矩阵用；null=无需矩阵转换）──
            public double[]? Rp, Gp, Bp, Wp;   // xy 色度坐标 + 白点
            public string? SourceTransfer;      // 源编码传递函数名（Adobe/ProPhoto 等）
            /// <summary>是否超广色域（需转换并归入 BT.2020 描述）。</summary>
            public bool NeedsBt2020Normalize => Kind == GamutKind.UltraWide;
        }

        // 目标白点 D65（转换基准；BT.2020 原色已写入下方各 Spec）
        private static readonly double[] D65 = { 0.3127, 0.3290 };

        // Illuminant C（BT.470-6 M 制与 film 原色的白点；数值取自
        // docs/COLOR_MATRIX_FIX_PLAN_2026-09-24.md「P1 · 第二套独立推导贴 zimg」表的"最佳白点"列）。
        // ⚠ 不许凭记忆改动：改一次就要重跑一次附录 A 的对拍。
        private static readonly double[] IlluminantC = { 0.3101, 0.3162 };

        private static readonly Dictionary<string, Spec> ByKey = Build();

        private static Dictionary<string, Spec> Build()
        {
            var d = new Dictionary<string, Spec>(StringComparer.OrdinalIgnoreCase);
            void Add(Spec s) => d[s.Key] = s;

            // ── 标准 ──
            Add(new Spec { Key = "sRGB", Kind = GamutKind.Standard,
                OutPrimaries = "bt709", OutTrc = "iec61966-2-1", OutMatrix = "bt709",
                IccgenPrimaries = "bt709", IccgenTrc = "iec61966-2-1", CjxlColorSpace = "sRGB",
                Rp = new[]{0.640,0.330}, Gp = new[]{0.300,0.600}, Bp = new[]{0.150,0.060}, Wp = D65 });
            Add(new Spec { Key = "BT.709", Kind = GamutKind.Standard,
                OutPrimaries = "bt709", OutTrc = "bt709", OutMatrix = "bt709",
                IccgenPrimaries = "bt709", IccgenTrc = "bt709", CjxlColorSpace = "sRGB",
                Rp = new[]{0.640,0.330}, Gp = new[]{0.300,0.600}, Bp = new[]{0.150,0.060}, Wp = D65 });

            // ── 广色域（CICP 精确可表达）──
            // Display P3：primaries=smpte432，trc=sRGB(iec61966-2-1)（P5 统一），matrix=bt709
            Add(new Spec { Key = "Display P3", Kind = GamutKind.Wide,
                OutPrimaries = "smpte432", OutTrc = "iec61966-2-1", OutMatrix = "bt709",
                IccgenPrimaries = "smpte432", IccgenTrc = "iec61966-2-1", CjxlColorSpace = "DisplayP3",
                Rp = new[]{0.680,0.320}, Gp = new[]{0.265,0.690}, Bp = new[]{0.150,0.060}, Wp = D65 });
            // DCI-P3（影院 P3：smpte431 基色 + smpte428 gamma2.6 传递 + 影院白点）无法原样表达（实测）：
            //  ① zscale 不识别 smpte428（"Undefined constant in 'smpte428'"）→ 无法烘焙传递/无法 iccgen；
            //  ② cjxl 无 DCI-P3 枚举、描述串 primaries token 只认 sRGB/2020；③ 故无 ICC 可携带 → 旧 CjxlColorSpace=null 回落 sRGB。
            // 归一化到 BT.2020 SDR（用户要求：映射到 BT.2020 超集保证零削色）：P3 ⊂ BT.2020，色域无损；
            // SDR 传递用 bt709（与 Adobe RGB 等超广归一一致）；cjxl 无 SDR-BT.2020 枚举 → 走 Kind=UltraWide
            // 归一化分支：源→BT.2020 像素 gamut 转换 + 生成标准 BT.2020 SDR ICC 携带（icc_pathname）。
            // Rp/Gp/Bp 保留 P3 物理色度（源探测/gamut 矩阵用）；SourceTransfer 记录原始 smpte428 供参考。
            Add(new Spec { Key = "DCI-P3", Kind = GamutKind.UltraWide,
                OutPrimaries = "bt2020", OutTrc = "bt709", OutMatrix = "bt2020nc",
                // IccgenPrimaries 不设值：ICC 若用 bt2020 声明色域会与 P3 原色几何矛盾，用 smpte432 又与
                // 归一化输出像素（bt2020）不符；DCI 影院白 (0.314,0.351) 无法用标准 primaries 键表达，
                // 白点差异由 Wp 字段承载，故置 null（PreserveAll 生成路径不依赖此字段）。
                IccgenPrimaries = null, IccgenTrc = "bt709", CjxlColorSpace = null,
                // 白点必须用 DCI 影院白 (0.314,0.351)：Wp 参与源→BT.2020 gamut 矩阵计算，
                // 误用 D65 会产生整体色偏（Display P3 才是 D65 白，勿混淆）。
                Rp = new[]{0.680,0.320}, Gp = new[]{0.265,0.690}, Bp = new[]{0.150,0.060},
                Wp = new[]{0.314, 0.351},
                SourceTransfer = "smpte428" });
            // P3 PQ（Display P3 基色 + PQ 传递，HDR）无法原样表达（实测）：cjxl 无 P3+PQ 枚举、描述串不认 P3 基色；
            // iccgen 不支持 smpte2084（"Not yet implemented"）→ 无 ICC。旧值 CjxlColorSpace=null → 显式目标回落 sRGB（丢 HDR+广色域）。
            // 归一化到 BT.2020 PQ（Rec2100PQ）：P3 ⊂ BT.2020（超集，无削色），完整保留 HDR/PQ 语义，cjxl 有枚举 + intensity_target 精确表达。
            // Kind=UltraWide：走归一化分支做源→BT.2020 像素 gamut 转换（而非仅 relabel）；stdT 恒为 smpte2084（OutTrc）故总烘焙 PQ。
            // Rp/Gp/Bp 保留 P3 物理色度（源探测/像素 gamut 转换用）；OutPrimaries=bt2020 为归一化输出描述。
            Add(new Spec { Key = "P3 PQ", Kind = GamutKind.UltraWide,
                OutPrimaries = "bt2020", OutTrc = "smpte2084", OutMatrix = "bt2020nc",
                IccgenPrimaries = "bt2020", IccgenTrc = "smpte2084", CjxlColorSpace = "Rec2100PQ",
                Rp = new[]{0.680,0.320}, Gp = new[]{0.265,0.690}, Bp = new[]{0.150,0.060}, Wp = D65,
                SourceTransfer = "smpte2084" });
            // P3 HLG（Display P3 基色 + HLG 传递，HDR）：与 P3 PQ 同理，cjxl 无 P3+HLG 枚举、描述串不认 P3 基色，
            // 归一化到 BT.2020 HLG（Rec2100HLG）：P3 ⊂ BT.2020 超集零削色，保留 HDR/HLG 语义。
            Add(new Spec { Key = "P3 HLG", Kind = GamutKind.UltraWide,
                OutPrimaries = "bt2020", OutTrc = "arib-std-b67", OutMatrix = "bt2020nc",
                IccgenPrimaries = "bt2020", IccgenTrc = "arib-std-b67", CjxlColorSpace = "Rec2100HLG",
                Rp = new[]{0.680,0.320}, Gp = new[]{0.265,0.690}, Bp = new[]{0.150,0.060}, Wp = D65,
                SourceTransfer = "arib-std-b67" });
            Add(new Spec { Key = "BT.2020 PQ", Kind = GamutKind.Wide,
                OutPrimaries = "bt2020", OutTrc = "smpte2084", OutMatrix = "bt2020nc",
                IccgenPrimaries = "bt2020", IccgenTrc = "smpte2084", CjxlColorSpace = "Rec2100PQ",
                Rp = new[]{0.708,0.292}, Gp = new[]{0.170,0.797}, Bp = new[]{0.131,0.046}, Wp = D65 });
            Add(new Spec { Key = "BT.2020 HLG", Kind = GamutKind.Wide,
                OutPrimaries = "bt2020", OutTrc = "arib-std-b67", OutMatrix = "bt2020nc",
                IccgenPrimaries = "bt2020", IccgenTrc = "arib-std-b67", CjxlColorSpace = "Rec2100HLG",
                Rp = new[]{0.708,0.292}, Gp = new[]{0.170,0.797}, Bp = new[]{0.131,0.046}, Wp = D65 });
            // 裸 "BT.2020"（未指明传递）解析为 SDR BT.2020（bt2020 primaries + bt709 传递），与源侧
            // MapIccToZscale/ApplyIccSemantics 的 "Rec.2020"→(bt2020,bt709) 一致（修复 D-2：旧值 smpte2084 令 SDR
            // BT.2020 源被当 PQ 解码→变暗，三处矛盾）。HDR 用明确的 "BT.2020 PQ"/"BT.2020 HLG"。
            // SDR BT.2020 在 cjxl 无枚举 → Kind=UltraWide 走归一化分支携带标准 BT.2020 SDR ICC（与 DCI-P3/Adobe RGB 一致）。
            Add(new Spec { Key = "BT.2020", Kind = GamutKind.UltraWide,
                OutPrimaries = "bt2020", OutTrc = "bt709", OutMatrix = "bt2020nc",
                IccgenPrimaries = "bt2020", IccgenTrc = "bt709", CjxlColorSpace = null,
                Rp = new[]{0.708,0.292}, Gp = new[]{0.170,0.797}, Bp = new[]{0.131,0.046}, Wp = D65 });

            // ── 超广色域（CICP 无法命名）：归入 BT.2020 描述，像素做矩阵转换 ──
            // 输出统一 bt2020 primaries；SDR 内容 trc 用 bt709（BT.2020 SDR 惯用）；ICC 用 iccgen bt2020。
            // CjxlColorSpace=null：cjxl 无 SDR-BT.2020/超广 color_space 枚举，必须由标准 BT.2020 ICC 描述；
            // 旧值 "Rec2100PQ" 会把 SDR 内容误标为 HDR PQ → 画面变暗（已修正，与 MapPrimariesTransferToCjxl 的 null 策略一致）。
            Add(new Spec { Key = "Adobe RGB", Kind = GamutKind.UltraWide,
                OutPrimaries = "bt2020", OutTrc = "bt709", OutMatrix = "bt2020nc",
                IccgenPrimaries = "bt2020", IccgenTrc = "bt709", CjxlColorSpace = null,
                Rp = new[]{0.640,0.330}, Gp = new[]{0.210,0.710}, Bp = new[]{0.150,0.060}, Wp = D65,
                SourceTransfer = "gamma2.2" });
            Add(new Spec { Key = "ProPhoto RGB", Kind = GamutKind.UltraWide,
                OutPrimaries = "bt2020", OutTrc = "bt709", OutMatrix = "bt2020nc",
                IccgenPrimaries = "bt2020", IccgenTrc = "bt709", CjxlColorSpace = null,
                Rp = new[]{0.7347,0.2653}, Gp = new[]{0.1596,0.8404}, Bp = new[]{0.0366,0.0001},
                Wp = new[]{0.3457,0.3585} /* D50 */, SourceTransfer = "gamma1.8" });
            Add(new Spec { Key = "ColorMatch RGB", Kind = GamutKind.UltraWide,
                OutPrimaries = "bt2020", OutTrc = "bt709", OutMatrix = "bt2020nc",
                IccgenPrimaries = "bt2020", IccgenTrc = "bt709", CjxlColorSpace = null });

            return d;
        }

        /// <summary>按显示名/探测名解析（大小写/分隔符不敏感）。未知或 auto → null。</summary>
        public static Spec? Resolve(string? displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return null;
            var n = displayName.Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');
            if (n == "auto" || n == "default") return null;
            // 直接键匹配
            foreach (var k in ByKey.Keys)
                if (n == k.ToLowerInvariant() || n == k.ToLowerInvariant().Replace(" ", "")) return ByKey[k];
            // 关键词匹配（顺序敏感：先具体后宽泛）
            if (n.Contains("prophoto") || n.Contains("romm")) return ByKey["ProPhoto RGB"];
            if (n.Contains("colormatch")) return ByKey["ColorMatch RGB"];
            if (n.Contains("adobe") || n.Contains("adobergb") || n.Contains("adobe rgb")) return ByKey["Adobe RGB"];
            if (n.Contains("dci") && n.Contains("p3")) return ByKey["DCI-P3"];
            if (n.Contains("displayp3") || n.Contains("display p3") || n.Contains("p3"))
            {
                if (n.Contains("pq") || n.Contains("2084")) return ByKey["P3 PQ"];
                if (n.Contains("hlg") || n.Contains("b67") || n.Contains("arib")) return ByKey["P3 HLG"];
                return ByKey["Display P3"];
            }
            if (n.Contains("2020") || n.Contains("2100"))
            {
                if (n.Contains("hlg") || n.Contains("arib") || n.Contains("b67")) return ByKey["BT.2020 HLG"];
                if (n.Contains("pq") || n.Contains("2084") || n.Contains("hdr")) return ByKey["BT.2020 PQ"];
                // Rec.2100/BT.2100 按定义即 HDR（PQ/HLG），未指明时默认 PQ；
                // 裸 "2020"（如 SDR BT.2020 ICC 推断名 "Rec.2020"）→ SDR BT.2020（修复 D-2：旧行为落入
                // 裸 BT.2020(PQ) 把 SDR 源当 HDR 解码→变暗，与 MapIccToZscale/ApplyIccSemantics 的 bt709 矛盾）。
                if (n.Contains("2100")) return ByKey["BT.2020 PQ"];
                return ByKey["BT.2020"];
            }
            if (n.Contains("709") || n.Contains("srgb") || n.Contains("iec61966"))
            {
                if (n.Contains("srgb") || n.Contains("iec61966")) return ByKey["sRGB"];
                return ByKey["BT.709"];
            }
            return null;
        }

        // ══════════════ 适配器（供现有调用点机械迁移）══════════════

        /// <summary>显示名 → ffmpeg (primaries, trc, matrix)。未知 → (null,null,null)。</summary>
        public static (string? primaries, string? trc, string? matrix) FfmpegTokens(string? displayName)
        {
            var s = Resolve(displayName);
            return s == null ? (null, null, null) : (s.OutPrimaries, s.OutTrc, s.OutMatrix);
        }

        /// <summary>显示名 → cjxl -x color_space 缩写。</summary>
        public static string? CjxlColorSpace(string? displayName) => Resolve(displayName)?.CjxlColorSpace;

        /// <summary>显示名是否超广色域（需转换到 BT.2020）。</summary>
        public static bool IsUltraWide(string? displayName) => Resolve(displayName)?.NeedsBt2020Normalize == true;

        /// <summary>
        /// 生成超广色域（Adobe RGB / ProPhoto 等）→ BT.2020 的线性域 primaries 矩阵滤镜链：
        /// zscale 线性化 → colorchannelmixer(源→BT.2020 线性矩阵) → zscale 回编码。
        /// 仅改 primaries，传递函数按 sRGB 曲线保持（Adobe~2.2≈sRGB；ProPhoto 1.8 近似）。
        /// 输出容器另由 -color_primaries bt2020 + iccgen 声明为 BT.2020。非超广色域返回 null。
        /// </summary>
        public static string? BuildUltraWideToBt2020Filter(string? displayName)
            => BuildUltraWideToBt2020Filter(displayName, false, false);

        /// <summary>
        /// 超广色域 → 标准 BT.2020 转换滤镜，**传递函数感知 HDR**：
        /// hdr=true → PQ(smpte2084) 或 HLG(arib-std-b67)；hdr=false → 保留源 SDR 传递(bt709)。
        /// 不能无条件用裸 "BT.2020" Spec 的 smpte2084，否则 SDR 会被编为 PQ 变暗。
        /// ⚠️ 源传递函数若 zscale 无法精确表达（ProPhoto 1.8、DCI-P3 2.6）则返回 null：
        /// 绝不拿 sRGB/bt709 曲线凑近似（那是中间调错），由上层走保真携带 ICC 或告警。
        /// 保留旧行为）；新色彩引擎传 allowApproximateCurve=false，曲线不可表达时直接拒绝转换。
        /// </summary>
        public static string? BuildUltraWideToBt2020Filter(string? displayName, bool hdr, bool hlg, bool allowApproximateCurve = true)
        {
            var spec = Resolve(displayName);
            if (spec == null || !spec.NeedsBt2020Normalize) return null;
            var encIn = ResolveInputCurve(displayName, allowApproximateCurve);
            if (encIn == null) return null;
            var m = LinearMatrix(displayName, "BT.2020");   // 线性域 primaries 矩阵(与传递无关)
            if (m == null) return null;
            var outT = hdr ? (hlg ? "arib-std-b67" : "smpte2084") : spec.OutTrc;
            return BuildFilter(spec.OutPrimaries, outT, spec.OutMatrix, m, encIn);
        }

        /// <summary>超广色域归一化后的输出描述 tokens (primaries,trc,matrix)。默认 SDR(bt709)。</summary>
        public static (string primaries, string trc, string matrix) UltraWideNormalizeTokens(string? displayName)
            => UltraWideNormalizeTokens(displayName, false, false);

        /// <summary>超广色域归一化输出描述 tokens，hdr=true 时传递为 PQ/HLG。</summary>
        public static (string primaries, string trc, string matrix) UltraWideNormalizeTokens(string? displayName, bool hdr, bool hlg)
        {
            var s = Resolve(displayName);
            var (p, t, m) = s == null ? ("bt2020", "bt709", "bt2020nc") : (s.OutPrimaries, s.OutTrc, s.OutMatrix);
            return hdr ? (p, hlg ? "arib-std-b67" : "smpte2084", m) : (p, t, m);
        }

        /// <summary>
        /// 通用线性域 primaries 转换滤镜（仅改 primaries，传递函数按源 sRGB 曲线解码/目标曲线编码）。
        /// 需 from/to 均有完整色度定义；否则返回 null。
        /// </summary>
        public static string? BuildGamutTransformFilter(string? fromName, string? toName, bool allowApproximateCurve = true)
        {
            var t = Resolve(toName);
            var m = LinearMatrix(fromName, toName);
            if (m == null || t == null) return null;
            var encIn = ResolveInputCurve(fromName, allowApproximateCurve);
            if (encIn == null) return null;
            return BuildFilter(t.OutPrimaries, t.OutTrc, t.OutMatrix, m, encIn);
        }

        /// <summary>
        /// 源→BT.2020 归一化滤镜（统一入口）：线性域 primaries 矩阵转换到 BT.2020，
        /// 输出传递函数由 outTrc 显式指定（必须与容器 CICP/ICC 声明一致，避免像素与标签失配）。
        /// SDR 目标传 bt709、HDR 目标传 smpte2084/arib-std-b67。取代旧 BuildGamutTransformFilter(src,"BT.2020")
        /// 对 SDR 源盲用裸 "BT.2020" Spec 的 smpte2084 而把 SDR bake 成 PQ（与 bt709 ICC 失配→变暗）的行为。
        /// srcGamut 需有色度定义（LinearMatrix 可算），否则返回 null。
        /// </summary>
        public static string? BuildToBt2020Filter(string? fromName, string outTrc, bool allowApproximateCurve = true)
        {
            var m = LinearMatrix(fromName, "BT.2020");
            if (m == null) return null;
            var encIn = ResolveInputCurve(fromName, allowApproximateCurve);
            if (encIn == null) return null;
            return BuildFilter("bt2020", outTrc, "bt2020nc", m, encIn);
        }

        // 统一生成滤镜链: 线性化 → colorchannelmixer(线性矩阵) → 回编码+声明目标帧元数据。
        // encIn = **源传递函数的 CICP 精确名**（由调用方经 ExactTransferName 得出）：
        // 旧实现固定 iec61966-2-1，使 Adobe RGB(gamma2.2)、DCI-P3(gamma2.6) 等源被按 sRGB 曲线线性化
        // → 中间调整体偏差。现在曲线不可精确表达时调用方直接不做转换。
        private static string BuildFilter(string outP, string outT, string outM, double[] m, string encIn)
        {
            string F(double v) => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
            var mix = "colorchannelmixer=" +
                $"rr={F(m[0])}:rg={F(m[1])}:rb={F(m[2])}:" +
                $"gr={F(m[3])}:gg={F(m[4])}:gb={F(m[5])}:" +
                $"br={F(m[6])}:bg={F(m[7])}:bb={F(m[8])}";
            // 末级 zscale 以 pin=p=目标primaries(恒等) 仅声明输出帧元数据，供容器 CICP + iccgen 正确读取。
            return $"format=gbrpf32le,zscale=tin={encIn}:t=linear,{mix}," +
                   $"zscale=pin={outP}:tin=linear:p={outP}:t={outT}:m={outM}";
        }

        /// <summary>
        /// 通用源→目标线性 RGB 3x3 矩阵（含白点色适应）。任一侧缺色度定义返回 null。
        /// </summary>
        public static double[]? LinearMatrix(string? fromName, string? toName)
        {
            var f = Resolve(fromName);
            var t = Resolve(toName);
            if (f == null || t == null) return null;
            if (f.Rp == null || f.Gp == null || f.Bp == null || f.Wp == null) return null;
            if (t.Rp == null || t.Gp == null || t.Bp == null || t.Wp == null) return null;
            var fXyz = PrimariesToXyz(f.Rp, f.Gp, f.Bp, f.Wp);
            var adapt = BradfordAdapt(XyzFromXy(f.Wp), XyzFromXy(t.Wp));
            var fToXyzD = Mat3Mul(adapt, fXyz);                 // srcLinear → 目标白点 XYZ
            var xyzToTRgb = Invert3x3(PrimariesToXyz(t.Rp, t.Gp, t.Bp, t.Wp));
            return Mat3Mul(xyzToTRgb, fToXyzD);                 // → 目标线性 RGB
        }

        /// <summary>
        /// 由已探测的 CICP (primaries,trc) tokens 映射到 cjxl color_space 缩写（管道/jxl 用）。
        /// 覆盖 Display P3、BT.2100 PQ/HLG、sRGB、linear 等；无法命名的返回 null。
        /// </summary>
        public static string? MapPrimariesTransferToCjxl(string? primaries, string? transfer)
        {
            var p = (primaries ?? "").ToLowerInvariant();
            var t = (transfer ?? "").ToLowerInvariant();
            // 超广色域探测名（若上层已归一为 bt2020 会走下面分支）
            if (p == "bt2020")
                return t switch
                {
                    "smpte2084" => "Rec2100PQ",
                    "arib-std-b67" => "Rec2100HLG",
                    "linear" => "RGB_D65_202_Rel_Lin",
                    // SDR BT.2020 在 cjxl 无 color_space 枚举 → 返回 null，由调用方以标准 ICC 描述。
                    // （旧代码默认 Rec2100PQ 会把 SDR 误标为 HDR PQ → 变暗，已修正）
                    _ => null,
                };
            if (p == "smpte432")        // Display P3 基色
                // P3+PQ 无 cjxl 枚举、iccgen 不支持 PQ（实测）→ 归一到 BT.2020 PQ（Rec2100PQ，P3⊂BT.2020 超集，保 HDR）。
                // 旧值 null 会令 P3-PQ 源经管道回落 sRGB（丢 HDR+广色域）；与 Spec(P3 PQ→BT.2020 PQ) 归一化保持一致。
                return t == "smpte2084" ? "Rec2100PQ" : "DisplayP3";
            if (p == "smpte431")        // DCI-P3 源：保留 null → 由调用方携带源 ICC 精确描述（QueueProcessor 广色域源分支）；
                return null;            // 目标归一化已在 Spec(DCI-P3→BT.2020 SDR) 处理，二者上下文不同、各自正确。
            if (p == "bt709" || p == "bt470bg" || p == "smpte170m" || p == "bt470m")
                return t == "linear" ? "RGB_D65_SRG_Rel_Lin" : "sRGB";
            if (string.IsNullOrWhiteSpace(p) && !string.IsNullOrWhiteSpace(t))
                return t switch { "smpte2084" => "Rec2100PQ", "arib-std-b67" => "Rec2100HLG", _ => null };
            return null;
        }

        /// <summary>
        /// 线性化用的源传递函数名：优先取 zscale 可精确表达的名；
        /// <paramref name="allowApproximateCurve"/>=true（**legacy 默认**）时回退到 iec61966-2-1 近似，
        /// 以保持旧版产物字节一致；新色彩引擎传 false → 返回 null，由上层走携带 ICC 或告警。
        /// </summary>
        private static string? ResolveInputCurve(string? displayName, bool allowApproximateCurve)
            => ExactTransferName(displayName) ?? (allowApproximateCurve ? "iec61966-2-1" : null);

        /// <summary>
        /// 源传递函数的 **zscale 可精确表达名**（CICP token）。返回 null 表示 zscale 无法表达该曲线
        /// （ProPhoto/ColorMatch 的 1.8+线性趾、DCI-P3 的 smpte428 gamma2.6）——
        /// 此时绝不允许拿 sRGB/bt709 近似代替（会造成中间调错），上层必须改走
        /// 「像素原样 + 携带源/命名 ICC」保真路径，或明确告警近似风险。
        /// </summary>
        public static string? ExactTransferName(string? displayName)
        {
            var s = Resolve(displayName);
            if (s == null) return null;
            return (s.SourceTransfer ?? s.OutTrc).ToLowerInvariant() switch
            {
                "iec61966-2-1" or "srgb" => "iec61966-2-1",
                "bt709" or "709" or "bt2020-10" or "bt2020-12" => "bt709",
                "gamma2.2" or "bt470m" or "adobe" => "bt470m",      // Adobe RGB (1998) = BT.470-6 M 制 gamma2.2
                "smpte2084" or "pq" => "smpte2084",
                "arib-std-b67" or "hlg" => "arib-std-b67",
                "linear" => "linear",
                _ => null,                                          // gamma1.8 / smpte428 等：不可精确表达
            };
        }

        // ══════════════════════════════════════════════════════════════════════════════
        //  CLI / GUI token → ffmpeg 原生常量名（**单一真值**，2026-09-16 新增）
        //
        //  为什么必须集中：ffmpeg 的 `-color_trc` / `-color_primaries` / `-colorspace` 只认**自己的**
        //  常量名，而面向用户的词表用的是友好名（`--help` 写的是 `<linear|srgb|pq|hlg>`）与
        //  zimg 风格名（bt2020ncl / ycgcocn）。若不规范化就原样拼进命令行 ⇒ ffmpeg 报
        //  `Unable to parse "<opt>" option value "<token>"` ⇒ **退出码 -22、零产物**，
        //  且报错文本只提解码器，用户完全看不出是自己选的 token 不被支持。
        //
        //  实测（ffmpeg git-2026-09-01-c27482a18d，本仓库 publish/PLAN/ffmpeg-full）：
        //    `-color_trc hlg | pq | srgb`                                  ⇒ Unable to parse "color_trc"
        //    `-colorspace bt2020ncl | bt2020cl | ycgcocn | ycgcocs | bt2020 |
        //                 smpte432 | smpte431`                            ⇒ Unable to parse "colorspace"
        //    `-colorspace gbr`                                             ⇒ 同上（ffmpeg 只认 rgb）
        //  可用的 `-colorspace` 名（逐项实测）：bt709 / bt2020nc / bt2020c / ycgco / rgb / fcc /
        //    bt470bg / smpte170m / smpte240m / smpte2085 / chroma-derived-nc / chroma-derived-c /
        //    ictcp（另 unknown|unspecified）。
        //  ⚠ 注意 `gbr`：**zscale 认 gbr、ffmpeg 的 -colorspace 认 rgb** —— 两个界面名不同，
        //    故规范化分「模型内规范名」与「ffmpeg 名」两层，勿混用。
        // ══════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// ffmpeg `-colorspace` 实际接受的常量名（逐项实测枚举）。
        /// **不要**用 zimg/ITU 的名字推断：`bt2020ncl`/`2020_ncl`/`ycgcocn` 均不在其中。
        /// </summary>
        public static readonly string[] FfmpegColorSpaceNames =
        {
            "bt709", "bt2020nc", "bt2020c", "ycgco", "rgb", "fcc", "bt470bg",
            "smpte170m", "smpte240m", "smpte2085", "chroma-derived-nc",
            "chroma-derived-c", "ictcp", "unknown", "unspecified",
        };

        /// <summary>传递函数 token → ffmpeg `-color_trc` 常量名。未知 token 原样返回（由校验层出声）。</summary>
        public static string? NormalizeTrcToken(string? token)
        {
            var t = (token ?? "").Trim().ToLowerInvariant();
            if (t.Length == 0) return null;
            return t switch
            {
                "srgb" or "iec61966-2-1" or "13" => "iec61966-2-1",
                "pq" or "smpte2084" or "16" => "smpte2084",
                "hlg" or "arib-std-b67" or "18" => "arib-std-b67",
                "709" or "bt709" or "1" => "bt709",
                "gamma22" or "bt470m" or "4" => "bt470m",
                "gamma28" or "bt470bg" or "5" => "bt470bg",
                "smpte170m" or "6" => "smpte170m",
                "smpte240m" or "7" => "smpte240m",
                "linear" or "8" => "linear",
                "log100" or "9" => "log100",
                "log316" or "10" => "log316",
                _ => t,                                  // 已是 ffmpeg 名（bt2020-10/12、smpte428…）或未知
            };
        }

        /// <summary>基准 token → ffmpeg `-color_primaries` 常量名。未知 token 原样返回。</summary>
        public static string? NormalizePrimariesToken(string? token)
        {
            var t = (token ?? "").Trim().ToLowerInvariant();
            if (t.Length == 0) return null;
            return t switch
            {
                "srgb" or "rec709" => "bt709",
                "rec2020" or "bt2100" => "bt2020",
                "display-p3" or "displayp3" or "p3" => "smpte432",
                "dci-p3" or "dcip3" => "smpte431",
                _ => t,                                  // bt709/bt2020/smpte432/bt470m/bt470bg/jedec-p22…
            };
        }

        /// <summary>
        /// 矩阵 token → ffmpeg `-colorspace` 常量名。
        /// 归一三类历史脏值：zimg 风格后缀（bt2020ncl/bt2020cl/ycgcocn/ycgcocs）、
        /// 裸族名（bt2020 指矩阵时应为 bt2020nc）、zimg 分隔写法（2020_ncl/2020_cl）。
        /// ⚠ `gbr` 是 zscale 名，**不是** ffmpeg 名 ⇒ 返回原值由调用方按界面转换（见 <see cref="ToFfmpegColorSpaceName"/>）。
        /// </summary>
        public static string? NormalizeMatrixToken(string? token)
        {
            var t = (token ?? "").Trim().ToLowerInvariant();
            if (t.Length == 0) return null;
            return t switch
            {
                "bt2020ncl" or "2020_ncl" or "bt2020" => "bt2020nc",
                "bt2020cl" or "2020_cl" => "bt2020c",
                "ycgcocn" or "ycgcocs" => "ycgco",
                "709" or "1" => "bt709",
                "170m" or "6" => "smpte170m",
                "470bg" or "5" => "bt470bg",
                "2020" => "bt2020nc",
                _ => t,
            };
        }

        /// <summary>
        /// 模型内矩阵 token → ffmpeg `-colorspace` 名（**界面差异的唯一转换点**）。
        /// 仅一处差异：zscale 认 `gbr`、ffmpeg 认 `rgb`（实测）。返回 null 表示该 token
        /// 在 ffmpeg 侧不存在 ⇒ 调用方**不得**把它拼进 `-colorspace`（否则整条命令 -22 失败）。
        /// </summary>
        public static string? ToFfmpegColorSpaceName(string? token)
        {
            var t = NormalizeMatrixToken(token);
            if (string.IsNullOrWhiteSpace(t)) return null;
            if (t.Equals("gbr", StringComparison.OrdinalIgnoreCase)) return "rgb";
            return Array.Exists(FfmpegColorSpaceNames, n => string.Equals(n, t, StringComparison.OrdinalIgnoreCase))
                ? t : null;
        }

        /// <summary>
        /// ffmpeg `-colorspace` 作为 **输入声明**（放在 `-i` 之前）时，**swscale 真正能完成转换**的矩阵名
        /// （逐项实测枚举，2026-09-16）。⚠ 与 <see cref="FfmpegColorSpaceNames"/> **不是同一个集合**：
        /// 后者只要求"ffmpeg 能解析该名字"，前者要求"swscale 能按该矩阵做像素转换"。
        ///
        /// 实测（ffmpeg git-2026-09-01，`-colorspace X -i y4m -frames:v 1 out.png`）：
        /// | 结果 | 矩阵 |
        /// |---|---|
        /// | ✅ 且**像素确实不同**（5 组互异 md5） | `bt709` `bt2020nc` `bt470bg`(=`smpte170m`=`rgb`) `smpte240m` `fcc` |
        /// | ❌ `Impossible to convert between the formats` | `bt2020c` `ycgco` `chroma-derived-nc` `chroma-derived-c` `ictcp` |
        /// | ❌ ffmpeg 不认该名 | `gbr`（ffmpeg 侧叫 `rgb`） |
        /// ⇒ 本管线**绝不可**把上表 ❌ 的名字放进 `-i` 前，否则整条命令 `-40` 失败、零产物。
        /// </summary>
        public static readonly string[] FfmpegInputMatrixNames =
        {
            "bt709", "bt2020nc", "bt470bg", "smpte170m", "smpte240m", "fcc", "rgb",
        };

        /// <summary>
        /// 模型内矩阵 token → 可安全放在 `-i` 前的 ffmpeg 输入矩阵名；**不可用时返回 null**（调用方必须跳过，不得原样拼）。
        /// 唯一界面差异：模型内 RGB 矩阵叫 `gbr`，ffmpeg 认 `rgb`。
        /// </summary>
        public static string? ToFfmpegInputMatrixName(string? token)
        {
            var t = NormalizeMatrixToken(token);
            if (string.IsNullOrWhiteSpace(t)) return null;
            if (t.Equals("gbr", StringComparison.OrdinalIgnoreCase)) return "rgb";
            return Array.Exists(FfmpegInputMatrixNames, n => string.Equals(n, t, StringComparison.OrdinalIgnoreCase))
                ? t : null;
        }

        /// <summary>
        /// 模型内矩阵 token → 可直接拼接的 `-colorspace &lt;name&gt; `（**所有 `-colorspace` 发出点的唯一出口**）。
        /// 返回空串表示「不写该标签」，两种情况：
        ///  ① token 在 ffmpeg 侧不存在（如历史脏值 smpte432 被当矩阵用）；
        ///  ② RGB 矩阵族（gbr→rgb）—— libwebp_anim/mjpeg/libaom/libsvtav1 实测拒绝写 RGB 矩阵
        ///     （"Undefined constant … in 'gbr'" ⇒ 0 字节），故与主路径保持同一口径：不写。
        /// ⚠ 绝不返回未经验证的名字：拼进命令就是 `Unable to parse` ⇒ 整条任务 -22、零产物。
        /// </summary>
        public static string FfmpegColorSpaceArg(string? token)
        {
            var n = ToFfmpegColorSpaceName(token);
            if (n == null || n.Equals("rgb", StringComparison.OrdinalIgnoreCase)) return "";
            return $"-colorspace {n} ";
        }

        /// <summary>CICP primaries token → 本注册表内的色度坐标（线性域矩阵用）；未知返回 null。</summary>
        public static Spec? ResolvePrimariesToken(string? token)
        {
            var t = (token ?? "").Trim().ToLowerInvariant();
            if (t.Length == 0) return null;
            foreach (var s in ByKey.Values)
                if (string.Equals(s.OutPrimaries, t, StringComparison.OrdinalIgnoreCase)
                    && s.Rp != null && s.Gp != null && s.Bp != null && s.Wp != null)
                    return s;
            return t switch
            {
                "gbr" or "rgb" or "bt709" => ByKey["sRGB"],     // RGB 矩阵族 = BT.709 原色
                _ => null,
            };
        }

        /// <summary>
        /// CICP primaries token → **规范色度坐标**（ITU-T H.273 / 各 BT 建议书的原色表）。
        /// ⚠️ 绝不从 Spec.OutPrimaries 反查：超广类 Spec（DCI-P3/Adobe/P3 PQ/ProPhoto…）的
        /// OutPrimaries 是“归一化后的输出标签”（=bt2020），而 Rp/Gp/Bp 是“源自身色度”，
        /// 两者语义不同。实测踩过：用 OutPrimaries 反查时 “bt2020” 命中了 DCI-P3 Spec，
        /// 导致 bt2020→bt709 算出与 P3→sRGB 完全相同的矩阵（ServiceProbe matrix 负值锚点抓出）。
        /// </summary>
        public sealed class PrimariesDef
        {
            public required string Token;
            public required double[] R, G, B, W;   // xy 色度 + 白点
        }

        /// <summary>
        /// CICP primaries token → 规范原色 xy + 白点。
        ///
        /// 数据来源（三处，每一格都要能指回其中之一；缺依据就**不填**，让它 fail-closed）：
        ///  ① 原色 xy：`docs/COLOR_MATRIX_FIX_PLAN_2026-09-24.md` **附录 A**「锚 B｜lcms2 `chrm` 实测」表；
        ///  ② 白点　：同文件 **P1**「第二套独立推导贴 zimg」表的「最佳白点」列；
        ///  ③ 贴 zimg 残差 max|Δ|：同上表，对照参照矩阵见 `COLOR_MATRIX_AUDIT_2026-09-24.md` **§2**。
        ///
        /// 两档精度（口径来自 FIX_PLAN P1 结论③，断言侧必须按档分别设容差、并把依据写进文案）：
        ///  - **A 档 ≤1e-5**：xy 与白点都能精确表达，贴 zimg 到 1e-5 以内。
        ///  - **B 档 ≤5e-4（精度受限，非"已验证通过"）**：lcms 的 `chrm` 只给 4 位小数 ⇒ 残差 ~2e-4。
        ///    要再严必须回 ITU-T H.273 表 1 或 zimg 常数取更高精度的 xy，**不得**把 1e-3 当"验过了"。
        /// </summary>
        private static readonly Dictionary<string, PrimariesDef> PrimariesTable = new(StringComparer.OrdinalIgnoreCase)
        {
            // ── A 档：公布值/两锚一致 ──────────────────────────────────────────────
            // ⚠ 本族拆开的缘由：旧版本这里写着「470M/470BG/170M/240M/FCC/P22 同原色族，差异在传递/矩阵」，
            //   并给五个 token 填了**同一组**常数 —— 实测那组恰好只是 JEDEC-P22 自己的 ⇒ 其余四格在拿 P22 的
            //   矩阵乘像素（与 zimg 逐元素实差 0.052–0.461，审计 §2）。事实是 470M / 470BG / 170M(=240M) / P22
            //   **四套原色互不相同**，差异同时存在于原色与传递函数。顺带：`fcc` 是 **矩阵**名（CICP matrix 4），
            //   根本不是原色 token（见本类 FfmpegColorSpaceNames），旧注释把它列进"原色族"是第二处错。
            // BT.709 / sRGB 原色（D65）— 附录 A 的自检基准（还原精确）
            ["bt709"] = new() { Token = "bt709", R = new[] { 0.640, 0.330 }, G = new[] { 0.300, 0.600 }, B = new[] { 0.150, 0.060 }, W = D65 },
            // BT.2020 / BT.2100 原色（D65）— BT.2080 公布值，两锚一致（贴 zimg 1.1e-6）
            ["bt2020"] = new() { Token = "bt2020", R = new[] { 0.708, 0.292 }, G = new[] { 0.170, 0.797 }, B = new[] { 0.131, 0.046 }, W = D65 },
            // Display P3（BT.709 白点 D65 + DCI 原色）— EG 432-1（贴 zimg 3.95e-6）
            ["smpte432"] = new() { Token = "smpte432", R = new[] { 0.680, 0.320 }, G = new[] { 0.265, 0.690 }, B = new[] { 0.150, 0.060 }, W = D65 },
            // JEDEC-P22（D65）— 贴 zimg 4.92e-6。⚠ 反证：这一组就是旧表里被五个 token 共用的那组常数，
            // 也就是说旧表实际是"只有 P22 对、其余四格全在拿 P22 算"。
            ["jedec-p22"] = new() { Token = "jedec-p22", R = new[] { 0.630, 0.340 }, G = new[] { 0.295, 0.605 }, B = new[] { 0.155, 0.077 }, W = D65 },
            // BT.470-6 System B / BT.601-625（EBU）原色（D65）— 附录 A 实测 (0.640,0.330)(0.290,0.600)(0.150,0.060)，
            // 与 bt709 只差绿原色 x（0.290 vs 0.300）⇒ 贴 zimg 后矩阵呈"≈单位阵 + 1.044 白点归一"的形状（审计 §2 同判）。
            ["bt470bg"] = new() { Token = "bt470bg", R = new[] { 0.640, 0.330 }, G = new[] { 0.290, 0.600 }, B = new[] { 0.150, 0.060 }, W = D65 },
            // SMPTE 170M（BT.601-525 / SMPTE-C 原色，D65）— 附录 A 实测 (0.630,0.340)(0.310,0.595)(0.155,0.070)
            ["smpte170m"] = new() { Token = "smpte170m", R = new[] { 0.630, 0.340 }, G = new[] { 0.310, 0.595 }, B = new[] { 0.155, 0.070 }, W = D65 },
            // SMPTE 240M：**与 smpte170m 同原色（SMPTE-C），差异只在传递函数** — 附录 A 与 zimg 两锚同判这一点。
            // ⚠ 这是本表**唯一一对合法的重复**（FIX_PLAN P1 的"同矩阵白名单"预期只含这一对）；
            //   白名单外出现重复即意味着有新的两格被压成同一组常数，必须报错。
            ["smpte240m"] = new() { Token = "smpte240m", R = new[] { 0.630, 0.340 }, G = new[] { 0.310, 0.595 }, B = new[] { 0.155, 0.070 }, W = D65 },

            // ── B 档：原色 xy 受 lcms `chrm` 4 位小数限制，残差 ~2e-4（精度受限，别再当 A 档用）──
            // DCI-P3 / 影院（RP 431-2 白点 0.314,0.351）— 贴 zimg 2.3e-4
            ["smpte431"] = new() { Token = "smpte431", R = new[] { 0.680, 0.320 }, G = new[] { 0.265, 0.690 }, B = new[] { 0.150, 0.060 }, W = new[] { 0.314, 0.351 } },
            // BT.470-6 System M（NTSC 1953）原色 + **Illuminant C** 白点 — 贴 zimg 1.6e-4
            // （白点已由"第二套独立推导"定为 C 而非 D65；残差来自 xy 只有 4 位小数，见上方 B 档说明）
            ["bt470m"] = new() { Token = "bt470m", R = new[] { 0.670, 0.330 }, G = new[] { 0.210, 0.710 }, B = new[] { 0.140, 0.080 }, W = IlluminantC },

            // ⚠ **smpte428 故意不收录**（不是漏了，也不许"挑一组看着像的"补回来）：
            //   CICP 10 = SMPTE ST 428-1，其原色是 CIE-1931 XYZ 三角（红 (1,0)、绿 (0,1)、**蓝 (0,0)**、白 1/3,1/3）。
            //   蓝原色 y=0 ⇒ PrimariesToXyz() 的归一化退化，**xy 参数化根本无法表达它**——
            //   实测贴不上 zimg（max|Δ| 1.9，即 ~199% 满量程），第二套独立推导同样失败，
            //   故 FIX_PLAN P1 定案：这一格不存在"该填什么值"，只存在"不填"。
            //   旧写法把 smpte428 抄成 smpte431 的字节级副本 ⇒ 拿 DCI-P3 的矩阵去乘像素，属最坏的一类静默错。
            //   现在 GetPrimaries("smpte428") → null ⇒ LinearMatrixBetweenPrimaries() / ColorantsD50() /
            //   ColorSpaceDescriptor.FromCicp() 一律 fail-closed（明说不支持）。
            //   （附录 A 曾记"锚 A(zimg) 与锚 B(lcms) 对 smpte428 给出不同原色"——两锚的"冲突"已被上面的
            //    可表达性判据消解：无论哪一组 xy 都表达不了 XYZ 三角，因此不需要再裁决谁对。）

            // ⚠ **film 同样不收录**：附录 A 有它的 lcms 原色 (0.681,0.319)(0.243,0.692)(0.145,0.049)+C 白，
            //   但贴 zimg 仍只有 2.1e-4（同 B 档精度问题），而审计把 `film→null` 定性为**能力缺口 = fail-closed，安全**，
            //   FIX_PLAN P1 明确要求保留该断言 ⇒ 本轮不新增，登记为能力边界；要收它得先解决 xy 精度（回 H.273 表 1）。
        };

        /// <summary>取 CICP primaries token 的规范色度；未知返回 null（调用方不得猜测）。</summary>
        public static PrimariesDef? GetPrimaries(string? token)
        {
            var t = (token ?? "").Trim().ToLowerInvariant();
            if (t.Length == 0) return null;
            if (PrimariesTable.TryGetValue(t, out var def)) return def;
            // ffmpeg 别名（709/2020/unspecified 等）与 RGB 矩阵族
            return t switch
            {
                "709" or "gbr" or "rgb" or "srgb" => PrimariesTable["bt709"],
                "2020" or "rec2020" or "bt2100" => PrimariesTable["bt2020"],
                "p3" or "display-p3" or "displayp3" => PrimariesTable["smpte432"],
                "dci" or "dci-p3" => PrimariesTable["smpte431"],
                "601" or "bt601" => PrimariesTable["smpte170m"],
                "p22" or "ebu3213" => PrimariesTable["jedec-p22"],
                _ => null,
            };
        }

        /// <summary>
        /// 具名空间（ProPhoto/Adobe RGB/DCI-P3/ColorMatch…）**自身**色度的 RGB→XYZ(D50)。
        /// ⚠️ 必须用 Spec.Rp/Gp/Bp/Wp（源自身色度），绝对不可用 <see cref="ColorantsD50"/>(token)：
        /// 超广类 Spec 的 OutPrimaries 是归一化输出标签（=bt2020），与自身色度不同义。
        /// </summary>
        public static double[]? NamedSpaceColorantsD50(string? displayName)
        {
            var s = Resolve(displayName);
            if (s == null || s.Rp == null || s.Gp == null || s.Bp == null || s.Wp == null) return null;
            return Mat3Mul(BradfordAdapt(XyzFromXy(s.Wp), XyzFromXy(IccD50Xy)), PrimariesToXyz(s.Rp, s.Gp, s.Bp, s.Wp));
        }

        /// <summary>具名空间自身的色度坐标（r/g/b/白点 xy）；无定义返回 null。</summary>
        public static (double[] R, double[] G, double[] B, double[] W)? NamedSpaceChromaticities(string? displayName)
        {
            var s = Resolve(displayName);
            if (s?.Rp == null || s.Gp == null || s.Bp == null || s.Wp == null) return null;
            return (s.Rp, s.Gp, s.Bp, s.Wp);
        }

        /// <summary>具名空间**源编码**传递函数名（Adobe=gamma2.2 / ProPhoto=gamma1.8 …）；未记录则 null。</summary>
        public static string? NamedSpaceSourceTransfer(string? displayName) => Resolve(displayName)?.SourceTransfer;

        /// <summary>ICC PCS D50 白点 xy（供引擎计算色度矩阵时复用，避免多处写字面值）。</summary>
        public static double[] IccPcsWhiteXy => IccD50Xy;

        /// <summary>3x3 求逆（行主序）；奇异矩阵返回 null。供引擎组合 ICC 矩阵用。</summary>
        public static double[]? InvertMatrix(double[] m9) => m9 != null && m9.Length == 9 ? Invert3x3(m9) : null;

        /// <summary>3x3 乘法（行主序）。</summary>
        public static double[] MultiplyMatrices(double[] a, double[] b) => Mat3Mul(a, b);

        /// <summary>
        /// 某 primaries token 的理论 RGB→XYZ(**D50 PCS**) 矩阵（9 项行主序，含 Bradford 白点适应）。
        /// 用途：**读 ICC 判色域**——把 ICC 的 rXYZ/gXYZ/bXYZ 列向量与各候选空间的理论值比对，
        /// 从而确定底图/图像实际处于哪个原色空间（对齐 libultrahdr IccHelper::readIccColorGamut，
        /// 其 kSRGB/kDisplayP3/kRec2020 即同一表达）。注意与 <see cref="LinearMatrix"/> 不同：
        /// 后者适应到**目标白点**（D65），本函数适应到 ICC 的 D50 PCS。
        /// </summary>
        public static double[]? ColorantsD50(string? primariesToken)
        {
            var d = GetPrimaries(primariesToken);
            if (d == null) return null;
            var adapt = BradfordAdapt(XyzFromXy(d.W), XyzFromXy(IccD50Xy));
            return Mat3Mul(adapt, PrimariesToXyz(d.R, d.G, d.B, d.W));
        }

        // ICC PCS 白点 D50（xy）
        private static readonly double[] IccD50Xy = { 0.34570, 0.35850 };

        /// <summary>
        /// 两个 CICP primaries token 之间的线性域 3x3 矩阵（含白点色适应）；色度取自 H.273 规范原色表。
        /// 供无法交给 zscale 的管线（如 GainMap 纯 C# 底图构建）做**真实像素映射**。
        /// 同一 token、或任一未知 → null（调用方不得只改标签）。
        /// </summary>
        public static double[]? LinearMatrixBetweenPrimaries(string? fromToken, string? toToken)
        {
            if (string.Equals(fromToken, toToken, StringComparison.OrdinalIgnoreCase)) return null;
            var f = GetPrimaries(fromToken);
            var t = GetPrimaries(toToken);
            if (f == null || t == null) return null;
            if (f == t) return null;                                    // 同一原色（别名）→ 无需映射
            var fToXyzD = Mat3Mul(BradfordAdapt(XyzFromXy(f.W), XyzFromXy(t.W)), PrimariesToXyz(f.R, f.G, f.B, f.W));
            var xyzToTRgb = Invert3x3(PrimariesToXyz(t.R, t.G, t.B, t.W));
            return Mat3Mul(xyzToTRgb, fToXyzD);
        }

        // ══════════════ 超广色域 → BT.2020 线性域转换矩阵（供 t4 像素转换）══════════════

        /// <summary>
        /// <summary>
        /// 校验手动指定的 CICP 三元组（primaries/trc/matrix），返回面向用户的「非阻拦」告警文本；
        /// 组合有效且常见则返回 null。仅作提示，不阻止执行（尊重用户选择）。
        /// </summary>
        public static string? ValidateCicpTriple(string? primaries, string? trc, string? matrix)
        {
            var knownPrim = new[] { "bt709", "smpte432", "bt2020", "smpte431" };
            var knownTrc = new[] { "iec61966-2-1", "bt709", "smpte2084", "arib-std-b67", "linear", "bt2020-10", "bt2020-12" };
            var p = (primaries ?? "").Trim().ToLowerInvariant();
            var t = (trc ?? "").Trim().ToLowerInvariant();
            var m = (matrix ?? "").Trim().ToLowerInvariant();
            var warns = new System.Collections.Generic.List<string>();

            if (p.Length > 0 && !System.Array.Exists(knownPrim, x => x == p))
                warns.Add($"基准 \"{p}\" 非工具链常用值（zscale/编码器可能不支持）");
            if (t == "smpte428")
                warns.Add("传递 smpte428(DCI-P3 gamma) zscale 不支持，将被归一化");
            else if (t.Length > 0 && !System.Array.Exists(knownTrc, x => x == t))
                warns.Add($"传递函数 \"{t}\" 非工具链常用值");

            bool hdrTrc = t is "smpte2084" or "arib-std-b67";
            if (hdrTrc && p == "bt709")
                warns.Add("HDR 传递(PQ/HLG)通常应配 BT.2020/P3 基准，配 bt709 可能不被识别");
            // 矩阵可用性（2026-09-16）：本管线的矩阵有**两个角色**，任一角色不可用都必须出声 ——
            // 否则用户以为声明生效了，实际被静默忽略（实测 `bt2020c`/`ycgco` 作为输入声明会令 swscale
            // 报 "Impossible to convert between the formats" ⇒ 整条命令 -40、零产物，故代码里会跳过它）。
            if (m.Length > 0 && ToFfmpegInputMatrixName(m) == null)
                warns.Add($"矩阵 \"{m}\" 本管线不支持（swscale 无法按其做像素转换）⇒ 该声明会被忽略，"
                        + "产物按源/目标的实际矩阵标注");
            if (p.Length > 0 && m.Length > 0)
            {
                bool mismatch = (p == "bt2020" && m is not ("bt2020nc" or "bt2020c" or "gbr" or "rgb" or "ictcp"))
                             || ((p == "bt709" || p == "smpte432") && m == "bt2020nc");
                if (mismatch)
                    warns.Add($"矩阵 \"{m}\" 与基准 \"{p}\" 不匹配");
            }
            return warns.Count > 0 ? string.Join("；", warns) : null;
        }

        /// <summary>
        /// 计算源线性 RGB → BT.2020 线性 RGB 的 3x3 矩阵（含白点 von Kries/Bradford 色适应）。
        /// 仅对超广色域（有 Rp/Gp/Bp/Wp）有效；否则返回 null。矩阵作用于线性光值。
        /// </summary>
        public static double[]? LinearMatrixToBt2020(string? displayName)
            => LinearMatrix(displayName, "BT.2020");

        // ── 色彩测量小工具（Lindbloom primaries→XYZ + 矩阵运算；矩阵一律 double[9] 行主序）──
        //    internal：供 <see cref="IccProfileBuilder"/> 复用（保持 primaries/色适应矩阵单一来源）。
        internal static double[] XyzFromXy(double[] xy)
            => new[] { xy[0] / xy[1], 1.0, (1.0 - xy[0] - xy[1]) / xy[1] };

        internal static double[] PrimariesToXyz(double[] r, double[] g, double[] b, double[] w)
        {
            // Lindbloom: N 行为 RGB→XYZ 的色度矩阵（列=原色, zi=1-xi-yi）; 解 N*S = W 得每通道缩放 S。
            double zr = 1 - r[0] - r[1], zg = 1 - g[0] - g[1], zb = 1 - b[0] - b[1];
            var N = new[] { r[0], g[0], b[0], r[1], g[1], b[1], zr, zg, zb };
            var W = XyzFromXy(w);   // 归一化白点 (X/Y, 1, Z/Y)
            var invN = Invert3x3(N);
            double Sr = invN[0] * W[0] + invN[1] * W[1] + invN[2] * W[2];
            double Sg = invN[3] * W[0] + invN[4] * W[1] + invN[5] * W[2];
            double Sb = invN[6] * W[0] + invN[7] * W[1] + invN[8] * W[2];
            // RGB→XYZ = N · diag(S)
            return new[]
            {
                r[0] * Sr, g[0] * Sg, b[0] * Sb,
                r[1] * Sr, g[1] * Sg, b[1] * Sb,
                zr * Sr, zg * Sg, zb * Sb,
            };
        }

        /// <summary>
        /// Bradford 色适应矩阵（src 白 → dst 白，作用于 XYZ）。
        /// ⚠️ 正确复合是 **M⁻¹·L·M**（XYZ→锥空间→缩放→回 XYZ）。
        /// 实测踩过严重隐患：旧写法 `Mat3Mul(Mat3Mul(L, M), Minv)` = L·M·M⁻¹ = **L**（只剩对角矩阵），
        /// 因为同白点时 L 恰好是单位阵而从未暴露；跨白点（ProPhoto D50 ↔ sRGB D65）时
        /// 会把未适应的 D65 矩阵乘个对角数（row0 从 0.4124/0.3576/0.1805 变成 0.4365/0.3785/0.1910，
        /// 行和由 0.9642 变成 1.006），由“读 lcms2 生成的 ICC 与理论值比对”抓出。
        /// </summary>
        internal static double[] BradfordAdapt(double[] srcW, double[] dstW)
        {
            var M = new[] { 0.8951, 0.2664, -0.1614, -0.7502, 1.7135, 0.0367, 0.0389, -0.0685, 1.0633 };
            var coneS = MatVec(M, srcW);
            var coneD = MatVec(M, dstW);
            var L = new[] { coneD[0] / coneS[0], 0, 0, 0, coneD[1] / coneS[1], 0, 0, 0, coneD[2] / coneS[2] };
            var LaM = Mat3Mul(L, M);
            var Minv = Invert3x3(M);
            return Mat3Mul(Minv, LaM);          // M⁻¹ · L · M
        }

        internal static double[] Mat3Mul(double[] a, double[] b)
        {
            var c = new double[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    double sum = 0;
                    for (int k = 0; k < 3; k++) sum += a[i * 3 + k] * b[k * 3 + j];
                    c[i * 3 + j] = sum;
                }
            return c;
        }

        private static double[] MatVec(double[] m, double[] v) => new[]
        {
            m[0] * v[0] + m[1] * v[1] + m[2] * v[2],
            m[3] * v[0] + m[4] * v[1] + m[5] * v[2],
            m[6] * v[0] + m[7] * v[1] + m[8] * v[2],
        };

        private static double[] Invert3x3(double[] m)
        {
            double a = m[0], b = m[1], c = m[2];
            double d = m[3], e = m[4], f = m[5];
            double g = m[6], h = m[7], i = m[8];
            double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
            double det = a * A + b * B + c * C;
            double inv = Math.Abs(det) < 1e-12 ? 0 : 1.0 / det;
            return new[]
            {
                A * inv, (c * h - b * i) * inv, (b * f - c * e) * inv,
                B * inv, (a * i - c * g) * inv, (c * d - a * f) * inv,
                C * inv, (b * g - a * h) * inv, (a * e - b * d) * inv,
            };
        }
    }
}
