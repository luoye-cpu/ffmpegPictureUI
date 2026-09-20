using System;
using System.Threading.Tasks;

namespace FfmpegGui.Services.ColorMapping;

/// <summary>
/// 色彩变换的并行/分块调度（性能调度层）。
///
/// 设计约束（按用户决策）：
///  • **不新增任何并发限制** —— 队列并发（QueueProcessor._concurrency + 其内存钳制）与
///    FfmpegOptions.ComputeAdaptiveThreads 已经决定了"每个任务能用多少核"，
///    本类只是把那份份额用掉，不做二次限流，也不引入新的用户设置。
///  • 规模策略只影响"怎么用这些核"：小图直算（省线程启动）、中图行带并行、大图流式分块（省内存、可流水）。
/// </summary>
public sealed class ColorScheduler
{
    /// <summary>行带并行度（来自队列既有份额）。</summary>
    public int Dop { get; private init; }
    /// <summary>每次处理的行数（0 = 整帧一次处理）。</summary>
    public int TileRows { get; private init; }
    /// <summary>是否走流式（true 时全帧不驻留，配合 RawColorPipeline 的有界队列背压）。</summary>
    public bool Streaming { get; private init; }
    /// <summary>整帧字节数（诊断用）。</summary>
    public long FrameBytes { get; private init; }
    /// <summary>规模档位（S/M/L），日志用。</summary>
    public string Tier { get; private init; } = "M";

    /// <summary>单 tile 目标工作集：L2 友好（256KB）—— 实测踩过：跨 tile 边界的 pow 计算使 L1 命中率骤降。</summary>
    private const int TileWorkingSetBytes = 256 * 1024;

    /// <summary>
    /// 整帧超过该值才启用流式（不是并发限制，而是避免整帧两份缓冲驻留）。
    /// 取 160MB：两缓冲共 320MB，再大就会明显挤压队列并发的内存预算（8192x4096x6=201MB 属此情况）。
    /// </summary>
    private const long StreamingThresholdBytes = 160L * 1024 * 1024;

    /// <summary>小图阈值（低于此值单线程直算：线程/Task 启动成本高于计算本身）。</summary>
    private const long SingleThreadMaxPixels = 512_000;

    public static ColorScheduler ForItem(int concurrency, int width, int height, int bytesPerPixel)
    {
        long pixels = Math.Max(1, (long)width * height);
        long frame = pixels * Math.Max(1, bytesPerPixel);
        int dop = Math.Max(1, FfmpegGui.Models.FfmpegOptions.ComputeAdaptiveThreads(concurrency));
        if (pixels <= SingleThreadMaxPixels)
            return new ColorScheduler { Dop = 1, TileRows = 0, Streaming = false, FrameBytes = frame, Tier = "S" };

        int tileRows = TileRowsFor(bytesPerPixel * Math.Max(1, width));
        bool stream = frame > StreamingThresholdBytes;
        return new ColorScheduler
        {
            Dop = dop,
            TileRows = stream ? tileRows : 0,          // 非流式时整帧一次处理（少一层队列开销）
            Streaming = stream,
            FrameBytes = frame,
            Tier = stream ? "L" : "M",
        };
    }

    /// <summary>按每行字节数求 tile 行数（保证工作集 ≤ L2 友好上限，且至少 1 行、至多 4096 行）。</summary>
    public static int TileRowsFor(int bytesPerRow)
    {
        if (bytesPerRow <= 0) return 64;
        int rows = TileWorkingSetBytes / bytesPerRow;
        if (rows < 1) rows = 1;
        if (rows > 4096) rows = 4096;
        return rows;
    }

    /// <summary>
    /// 并行处理 [0,rowTotal) 的行带。<paramref name="body"/> 参数为 (起始行, 行数)。
    /// Dop=1 时直接串行（无 Task 开销）。
    /// </summary>
    public void RunBands(int rowTotal, Action<int, int> body)
    {
        if (rowTotal <= 0) return;
        int rows = TileRows > 0 ? TileRows : Math.Max(1, (rowTotal + Dop - 1) / Dop);
        int bands = (rowTotal + rows - 1) / rows;
        if (Dop <= 1 || bands <= 1)
        {
            for (int b = 0; b < bands; b++)
            {
                int start = b * rows;
                body(start, Math.Min(rows, rowTotal - start));
            }
            return;
        }
        var opt = new ParallelOptions { MaxDegreeOfParallelism = Dop };
        System.Threading.Tasks.Parallel.For(0, bands, opt, b =>
        {
            int start = b * rows;
            body(start, Math.Min(rows, rowTotal - start));
        });
    }

    /// <summary>调度决策的日志片段（每项一条，便于性能回归 diff）。</summary>
    public string ToLogFragment()
        => $"tier={Tier} dop={Dop} tileRows={(TileRows > 0 ? TileRows.ToString() : "整帧")} stream={(Streaming ? "on" : "off")} frame={FrameBytes / (1024 * 1024)}MB";
}

/// <summary>
/// 色彩度量（可行性/性能验证用；也服务 C7「质量分析前先同空间」）。
/// 全部为纯托管、无分配热路径。
/// </summary>
public static class ColorMetrics
{
    /// <summary>两幅同尺寸 8-bit RGB(A) 图的 PSNR（dB）。stride 由调用方给。</summary>
    public static double Psnr(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int bytesPerPixel = 3)
    {
        int n = Math.Min(a.Length, b.Length);
        double se = 0; long count = 0;
        for (int i = 0; i + bytesPerPixel <= n; i += bytesPerPixel)
        {
            for (int c = 0; c < bytesPerPixel; c++)
            {
                double d = a[i + c] - b[i + c];
                se += d * d; count++;
            }
        }
        if (count == 0) return double.PositiveInfinity;
        double mse = se / count;
        if (mse <= 0) return double.PositiveInfinity;
        return 10.0 * Math.Log10(255.0 * 255.0 / mse);
    }

    /// <summary>两幅 16-bit（LE 打包）图的 PSNR，按 65535 满量程。</summary>
    public static double Psnr16(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int bytesPerPixel = 6)
    {
        int n = Math.Min(a.Length, b.Length);
        double se = 0; long count = 0;
        for (int i = 0; i + bytesPerPixel <= n; i += bytesPerPixel)
        {
            for (int c = 0; c + 1 < bytesPerPixel; c += 2)
            {
                double d = ((a[i + c] | (a[i + c + 1] << 8)) - (b[i + c] | (b[i + c + 1] << 8))) / 65535.0;
                se += d * d; count++;
            }
        }
        if (count == 0) return double.PositiveInfinity;
        double mse = se / count;
        return mse <= 0 ? double.PositiveInfinity : 10.0 * Math.Log10(1.0 / mse);
    }

    /// <summary>
    /// 两个 sRGB 8-bit 色度的 CIE ΔE*ab（D50；用于"引擎 vs 参考实现"的正确性判据）。
    /// 说明：这里用 D50 sRGB 矩阵（与 ICC PCS 一致），保证与 lcms2 的比对口径相同。
    /// </summary>
    public static double DeltaE76(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
    {
        LabOf(r1, g1, b1, out double l1, out double a1, out double bb1);
        LabOf(r2, g2, b2, out double l2, out double a2, out double bb2);
        return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (bb1 - bb2) * (bb1 - bb2));
    }

    private static void LabOf(byte r, byte g, byte b, out double L, out double A, out double B)
    {
        var cv = TransferCurve.Srgb;
        double lr = cv.ToLinear(r / 255.0), lg = cv.ToLinear(g / 255.0), lb = cv.ToLinear(b / 255.0);
        var m = ColorSpaceRegistry.ColorantsD50("bt709");
        double X = 0, Y = 0, Z = 0;
        if (m != null)
        {
            X = m[0] * lr + m[1] * lg + m[2] * lb;
            Y = m[3] * lr + m[4] * lg + m[5] * lb;
            Z = m[6] * lr + m[7] * lg + m[8] * lb;
        }
        // PCS 白点 D50：Xn=0.9642 Yn=1 Zn=0.8249
        X = LabF(X / 0.9642); Y = LabF(Y / 1.0); Z = LabF(Z / 0.8249);
        L = 116 * Y - 16; A = 500 * (X - Y); B = 200 * (Y - Z);
    }

    private static double LabF(double t)
        => t > 0.008856 ? Math.Cbrt(t) : (7.787 * t + 16.0 / 116.0);
}
