using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FfmpegGui.Services.ColorMapping
{
    /// <summary>
    /// 「该图实际用色是否装在 BT.709 内」的统计内核 —— <c>--raw-color-target auto</c> 的判据。
    /// <para>
    /// <b>语义（必须与日志/文档口径一致）</b>：本内核测的是
    /// 「<b>本管线自身的 bt2020 → bt709 这一步会不会裁切像素</b>」，
    /// <b>不是</b>绝对色度真值 —— 它跑在「dngtool 的相机原生数据被声明为 bt2020」的中间件上
    /// （见 <c>RawService.cs:174</c> 的 <c>-o 0</c> 与 <c>QueueProcessor.cs</c> 的
    /// <c>ColorSourceDeclPrimaries = "bt2020"</c>），继承该近似。
    /// 就本决策而言这正是要问的问题：管线会不会把颜色切掉。
    /// </para>
    /// <para>
    /// <b>⚠⚠ 判据必须带亮度地板</b>：暗部噪声会把像素色度甩到色域外，但完全不可见。
    /// 实测（2026-10-04，13 个不同厂商 RAW 样本）：A7II 样本的全部"超 bt709"像素都来自最暗两档，
    /// 过地板后为 0.0000%；而 Pentax 黄花样本集中在最亮档（20.98%）。
    /// ⇒ 不加地板：阈值放宽会<b>切掉真实颜色</b>，阈值收紧则因噪声<b>永不判 bt709</b>（功能等于没有）。
    /// </para>
    /// <para>
    /// <b>⚠ 不得下采样</b>：几何缩放是盒式平均，会削掉极值色度 ⇒ 把结论推向"装在 bt709 内"
    /// （即<b>危险方向</b>：会真的裁切）。本内核按<b>原分辨率</b>解码后做<b>跨步抽样</b>
    /// （抽样不平均、保留极值）。
    /// </para>
    /// </summary>
    public static class RawGamutFit
    {
        /// <summary>超出 bt709 的像素比例阈值（仅统计地板以上像素）。0.1%。</summary>
        public const double FracThreshold = 0.001;
        /// <summary>最负超出量阈值（满量程 1.0 的 −0.5%）。必须 <b>大于</b>此值才算"未超出"。</summary>
        public const double NegThreshold = -0.005;
        /// <summary>亮度地板系数：地板 = <see cref="FloorFactor"/> × Y 的 99.9 分位。</summary>
        public const double FloorFactor = 0.25;
        /// <summary>
        /// 判「越界」的浮点容差（满量程 1.0 的 1e-4 = 0.01%）。
        /// <para>
        /// ⚠ 必需：**恰在 bt709 边界上的颜色**（例如把 bt709 纯红换算成 bt2020 再换回来）
        /// 会因矩阵数值误差落到 **−1.7e-5** 量级 ⇒ 若判据取 <c>&lt; 0</c>，这类像素会被整片计成"越界"，
        /// 于是 `frac` 恒为 1.0 ⇒ **永远判 bt2020**（功能失效，且方向正好是危险的那一侧）。
        /// 实测（`ServiceProbe gamutfit` (d) 条钉住）。
        /// </para>
        /// <para>
        /// 本容差**不放宽实质判据**：真正越界的颜色是 **−1e-1** 量级（bt2020 纯红 ≈ −0.1246），
        /// 与容差差 **3 个数量级**；而决策阈值 <see cref="NegThreshold"/> = −0.005 比它大 50 倍
        /// ⇒ 任何可能影响结论的越界量都必然被计入。
        /// </para>
        /// </summary>
        public const double EpsNeg = 1e-4;
        /// <summary>抽样目标像素数（统计量，不需要全像素）。</summary>
        private const int TargetSamples = 1_000_000;
        /// <summary>16-bit 满量程。</summary>
        private const double Full = 65535.0;

        /// <summary>
        /// BT.2020 线性 RGB → BT.709/sRGB 线性 RGB（D65↔D65，无色适应）。
        /// <para>
        /// ⚠⚠ **从 <see cref="ColorSpaceRegistry.LinearMatrixBetweenPrimaries"/> 派生**（单一真值源），
        /// **不是**本文件自带一份常数。理由：本内核的判据必须与**管线实际执行的那次转换**用同一个矩阵，
        /// 否则「判据说装得下、实际却裁切」——正是本仓最忌讳的「宣称≠交付」。
        /// 实测两处独立常数会差 **2.7e-4**（2026-10-04，`ServiceProbe gamutfit` 钉住），
        /// 量级虽小，但会让判据与产物脱钩。
        /// </para>
        /// <para>后备常数 = BT.2080 发布值（仅当注册表缺席时使用；探针会断言注册表在位）。</para>
        /// </summary>
        public static readonly double[,] M2020To709 = LoadPair("bt2020", "bt709", new[,]
        {
            {  1.6602269424, -0.5875473357, -0.0728385790 },
            { -0.1245536957,  1.1329258308, -0.0083491262 },
            { -0.0181550952, -0.1006026875,  1.1189980479 },
        });

        /// <summary>BT.709 线性 RGB → BT.2020 线性 RGB（同源派生；供互逆自检与探针用）。</summary>
        public static readonly double[,] M709To2020 = LoadPair("bt709", "bt2020", new[,]
        {
            { 0.6275037447, 0.3292753256, 0.0433027050 },
            { 0.0691084837, 0.9195193943, 0.0113592220 },
            { 0.0163940316, 0.0880110089, 0.8953804118 },
        });

        private static double[,] LoadPair(string from, string to, double[,] fallback)
        {
            try
            {
                var m = ColorSpaceRegistry.LinearMatrixBetweenPrimaries(from, to);
                if (m != null && m.Length == 9)
                {
                    var r = new double[3, 3];
                    for (int i = 0; i < 3; i++)
                        for (int j = 0; j < 3; j++)
                            r[i, j] = m[i * 3 + j];
                    return r;
                }
            }
            catch { /* 注册表异常 ⇒ 用后备常数，绝不因此让类型初始化失败 */ }
            return fallback;
        }

        /// <summary>BT.2020 亮度系数（用于地板）。</summary>
        private static readonly double[] YCoeff2020 = { 0.2627, 0.6780, 0.0593 };

        /// <summary>
        /// 统计结果。⚠ <see cref="Reason"/> 一律 <b>纯 ASCII</b>：
        /// 它由本类的字符串字面量构造，而该行<b>不是</b>诊断 sink
        /// ⇒ 含中文会被 <c>_probe-cjk-hardcode-scan</c> 计入 <c>CsUi</c>（余量 0）而转红。
        /// 中文说明由调用方（<c>QueueProcessor</c> 的 <c>captured.Log +=</c>，属诊断 sink）承载。
        /// </summary>
        public readonly record struct Result(
            int SampledPixels,
            double LuminanceFloor,
            double FracOutOf709,
            double MaxNegExcursion,
            bool Measurable,
            bool FitsIn709,
            string Reason);

        /// <summary>
        /// 测不出可用结果时的返回（<see cref="Result.Measurable"/> = false）。
        /// ⚠ <b>三态</b>：调用方<b>不得</b>把"测不出"当成"装在 bt709 内"。
        /// </summary>
        private static Result Unmeasurable(string why) =>
            new(0, 0, double.NaN, double.NaN, false, false, why);

        /// <summary>
        /// 对 dngtool 的线性中间件 TIFF 做统计。失败一律返回
        /// <see cref="Result.Measurable"/> = false（绝不猜一个结论）。
        /// </summary>
        /// <param name="rawTiffPath">dngtool 的 <c>-d -T -o 0</c> 产物（线性 16-bit RGB）。</param>
        public static async Task<Result> MeasureAsync(string rawTiffPath, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(rawTiffPath) || !File.Exists(rawTiffPath))
                return Unmeasurable("file-not-found");

            if (!TryReadTiffSize(rawTiffPath, out int w, out int h) || w <= 0 || h <= 0)
                return Unmeasurable("tiff-size-unreadable");

            long pixels = (long)w * h;
            byte[] raw;
            try
            {
                // 复用既有公开入口（内部已带 `-frames:v 1`，避免多帧容器把 N 帧拼进同一条 rawvideo）。
                // ⚠ w/h 只用于下面的长度校验；解码命令本身不消费它们（见其实现）。
                raw = await RawColorPipeline.DecodeToRgb48Async(rawTiffPath, w, h, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Unmeasurable("decode-failed:" + ex.GetType().Name);
            }

            if (raw.LongLength != pixels * 6)
                return Unmeasurable($"size-mismatch:{raw.LongLength}!={pixels * 6}");

            return MeasureRgb48(raw, pixels, 0, ct);
        }

        /// <summary>
        /// <b>纯函数核心</b>（不碰文件/子进程）—— 供 <see cref="MeasureAsync"/> 与探针复用。
        /// ⚠ 拆出来是刻意的：本类的判据必须能在**合成数据**上被独立验证（不依赖素材、不依赖 ffmpeg），
        /// 否则"三态"与阈值只能靠端到端间接覆盖。
        /// </summary>
        /// <param name="rgb48">rgb48le 原始字节（长度必须 = <paramref name="pixels"/> × 6）。</param>
        /// <param name="pixels">像素数。</param>
        /// <param name="stride">跨步；≤0 ⇒ 按 <see cref="TargetSamples"/> 自动取。</param>
        public static Result MeasureRgb48(byte[] rgb48, long pixels, int stride = 0, CancellationToken ct = default)
        {
            if (rgb48 == null || pixels <= 0 || rgb48.LongLength != pixels * 6)
                return Unmeasurable("size-mismatch");
            byte[] raw = rgb48;

            // ⚠ 跨步抽样：flat 索引步长 ⇒ 样本数 = pixels / stride（**不是** /stride²，2026-10-04 修）。
            //   步长与行宽（6024）不成整数倍 ⇒ 每行落在不同列，不会踩到去马赛克的周期性。
            if (stride <= 0) stride = Math.Max(1, (int)(pixels / TargetSamples));

            // ── pass 1：Y 直方图 ⇒ 地板 ──
            var yHist = new int[65536];
            int sampled = 0;
            for (long i = 0; i < pixels; i += stride)
            {
                int o = (int)(i * 6);
                double r = (raw[o] | (raw[o + 1] << 8)) / Full;
                double g = (raw[o + 2] | (raw[o + 3] << 8)) / Full;
                double b = (raw[o + 4] | (raw[o + 5] << 8)) / Full;
                int y = (int)Math.Round((YCoeff2020[0] * r + YCoeff2020[1] * g + YCoeff2020[2] * b) * Full);
                if (y < 0) y = 0; else if (y > 65535) y = 65535;
                yHist[y]++;
                sampled++;
                if ((i & 0xFFFFF) == 0) ct.ThrowIfCancellationRequested();
            }
            if (sampled == 0) return Unmeasurable("no-samples");

            int need = (int)Math.Ceiling(sampled * 0.999);
            int acc = 0, yP999 = 0;
            for (int i = 0; i < 65536; i++)
            {
                acc += yHist[i];
                if (acc >= need) { yP999 = i; break; }
            }
            // 地板下限 1/65535：全黑/纯噪声图不应把地板算成 0（那会把噪声全放进来）
            double floorNorm = Math.Max(1.0 / Full, FloorFactor * (yP999 / Full));

            // ── pass 2：地板以上像素的越界统计 ──
            int above = 0, outCount = 0;
            double worstNeg = 0;
            for (long i = 0; i < pixels; i += stride)
            {
                int o = (int)(i * 6);
                double r = (raw[o] | (raw[o + 1] << 8)) / Full;
                double g = (raw[o + 2] | (raw[o + 3] << 8)) / Full;
                double b = (raw[o + 4] | (raw[o + 5] << 8)) / Full;
                double y = YCoeff2020[0] * r + YCoeff2020[1] * g + YCoeff2020[2] * b;
                if (y < floorNorm) continue;
                above++;
                double n0 = M2020To709[0, 0] * r + M2020To709[0, 1] * g + M2020To709[0, 2] * b;
                double n1 = M2020To709[1, 0] * r + M2020To709[1, 1] * g + M2020To709[1, 2] * b;
                double n2 = M2020To709[2, 0] * r + M2020To709[2, 1] * g + M2020To709[2, 2] * b;
                double mn = Math.Min(n0, Math.Min(n1, n2));
                if (mn < -EpsNeg)
                {
                    outCount++;
                    if (mn < worstNeg) worstNeg = mn;
                }
                if ((i & 0xFFFFF) == 0) ct.ThrowIfCancellationRequested();
            }
            if (above == 0) return Unmeasurable("no-pixels-above-floor");

            double frac = outCount / (double)above;
            bool fits = frac < FracThreshold && worstNeg > NegThreshold;
            string reason = fits ? "fits-bt709" : "exceeds-bt709";
            return new Result(sampled, floorNorm, frac, worstNeg, true, fits, reason);
        }

        /// <summary>
        /// 极简 TIFF 尺寸读取：只取 IFD0 的 <c>ImageWidth(256)</c> / <c>ImageLength(257)</c>。
        /// 目的仅是给 <see cref="RawColorPipeline.DecodeToRgb48Async"/> 一个长度校验基准；
        /// 像素解码完全交给 ffmpeg（多 strip / 压缩格式它都能处理）⇒ 本解析器<b>不需要</b>理解 strip。
        /// </summary>
        private static bool TryReadTiffSize(string path, out int w, out int h)
        {
            w = 0; h = 0;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                var head = new byte[8];
                if (fs.Read(head, 0, 8) != 8) return false;
                bool le = head[0] == 0x49 && head[1] == 0x49;
                bool be = head[0] == 0x4D && head[1] == 0x4D;
                if (!le && !be) return false;

                uint U32(byte[] b, int o) => le
                    ? (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24))
                    : (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
                ushort U16(byte[] b, int o) => le ? (ushort)(b[o] | (b[o + 1] << 8)) : (ushort)((b[o] << 8) | b[o + 1]);

                long ifd0 = U32(head, 4);
                fs.Seek(ifd0, SeekOrigin.Begin);
                var cnt = new byte[2];
                if (fs.Read(cnt, 0, 2) != 2) return false;
                int n = U16(cnt, 0);
                var ent = new byte[12 * n];
                if (fs.Read(ent, 0, ent.Length) != ent.Length) return false;
                for (int i = 0; i < n; i++)
                {
                    int o = i * 12;
                    ushort tag = U16(ent, o);
                    if (tag != 256 && tag != 257) continue;
                    ushort typ = U16(ent, o + 2);
                    if (typ != 3 && typ != 4) continue;          // SHORT / LONG
                    uint v = typ == 3 ? U16(ent, o + 8) : U32(ent, o + 8);
                    if (tag == 256) w = (int)v; else h = (int)v;
                }
                return w > 0 && h > 0;
            }
            catch { return false; }
        }
    }
}
