using System;
using Tests.Shared;
using FfmpegGui.Services;
// ⚠ 与拆分前 `ServiceProbe/Program.cs` 顶部的 using-alias **逐字一致**：
//   `GF` = `RawGamutFit`（原 Program.cs:21）。拆出后必须自带，
//   否则 `GF.M2020To709` 等引用全部 CS0103（拆分时实测踩过）。
using GF = FfmpegGui.Services.ColorMapping.RawGamutFit;

namespace Tests.Engine
{
    /// <summary>
    /// 子系统测试组：**engine**（自研引擎与像素内核）—— 2026-10-05 从 ServiceProbe 的单个
    /// 577 KB `Program.cs` 中物理拆出，使"改引擎这一块"只需跑本工程，不再跑整个宿主。
    ///
    /// <para><b>覆盖的 mode</b>：`engine` `gamut` `gamutfit` `simdswitch`（见 `TestGroups`）。
    /// 本文件承载 `gamutfit` —— `RawGamutFit`（`--raw-color-target auto` 的降级判据内核）的纯函数判据。</para>
    ///
    /// <para><b>独立运行</b>：<c>dotnet run --project tests/engine</c>（或直接跑产物 exe）。
    /// 也可由聚合入口 `ServiceProbe gamutfit` 调用（保既有门禁契约零改动）。</para>
    /// </summary>
    internal static class EngineGroup
    {
        /// <summary>
        /// <c>Check</c> 转发到共享 `Harness`。
        /// ⚠ **不是复制实现**：计数与输出格式只有 `Harness` 一份（否则各组会漂移出各自的
        /// PASS/FAIL 口径，本仓已有"两处独立实现漂移"的教训）。保留本地同名包装，
        /// 只为让**拆出的原代码一字不改**（降低拆分引入差异的风险）。
        /// </summary>
        private static void Check(bool ok, string msg) => Harness.Check(ok, msg);

        /// <summary>本组成员实现。</summary>
        internal static void RunGamutFit() => ProbeGamutFit();
        private static void ProbeGamutFit()
        {
            // ── 1. 常量交叉锁：本内核的 M2020To709 == 产品注册表里同一对矩阵 ──
            //     两处独立维护同一对矩阵 ⇒ 抄错一处端到端测不出来。
            var reg = ColorSpaceRegistry.LinearMatrixBetweenPrimaries("bt2020", "bt709");
            Check(reg != null, "gamutfit: 注册表提供 bt2020→bt709 矩阵");
            if (reg != null)
            {
                double err = 0;
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        err = Math.Max(err, Math.Abs(GF.M2020To709[i, j] - reg[i * 3 + j]));
                Console.WriteLine($"GF.M2020To709 vs ColorSpaceRegistry maxErr={err:E2}");
                Check(err <= 1e-15, "gamutfit: M2020To709 派生自注册表（≤1e-15，非后备常数）");
            }
            // 派生值仍须落在 BT.2080 发布值附近（拦住"注册表整条错"这种情形）
            {
                var pub = new[]
                {
                    1.660491, -0.587641, -0.072850,
                   -0.124550,  1.132901, -0.008350,
                   -0.018151, -0.100581,  1.118731,
                };
                double e = 0;
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        e = Math.Max(e, Math.Abs(GF.M2020To709[i, j] - pub[i * 3 + j]));
                Console.WriteLine($"GF.M2020To709 vs BT.2080 发布值 maxErr={e:E2}");
                Check(e <= 2e-3, "gamutfit: M2020To709 与 BT.2080 发布值一致（≤2e-3）");
            }

            // ── 2. 正反矩阵互逆 ──
            double idErr = 0;
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    double s = 0;
                    for (int k = 0; k < 3; k++) s += GF.M2020To709[i, k] * GF.M709To2020[k, j];
                    idErr = Math.Max(idErr, Math.Abs(s - (i == j ? 1.0 : 0.0)));
                }
            Console.WriteLine($"M2020To709 x M709To2020 与单位阵偏差 max={idErr:E2}");
            Check(idErr <= 1e-12, "gamutfit: 正反矩阵互逆（≤1e-12）");

            // ── 3. 阈值形态（方向必须 fail-closed）──
            Check(GF.FracThreshold > 0 && GF.FracThreshold <= 0.01,
                  $"gamutfit: FracThreshold in (0,1%]（实 {GF.FracThreshold}）");
            Check(GF.NegThreshold < 0, $"gamutfit: NegThreshold < 0（实 {GF.NegThreshold}）");
            Check(GF.FloorFactor > 0 && GF.FloorFactor <= 1,
                  $"gamutfit: FloorFactor in (0,1]（实 {GF.FloorFactor}）");
            // ⚠ 容差必须比真实越界量级小至少 2 个数量级（否则会掩盖真越界）
            Check(GF.EpsNeg > 0 && GF.EpsNeg <= 1e-3,
                  $"gamutfit: EpsNeg in (0,1e-3]（实 {GF.EpsNeg}）");

            // ── 4. 行为：合成数据 ──
            static byte[] Rgb48(int n, Func<int, (int r, int g, int b)> f)
            {
                var a = new byte[n * 6];
                for (int i = 0; i < n; i++)
                {
                    var (r, g, b) = f(i);
                    a[i * 6 + 0] = (byte)(r & 0xFF); a[i * 6 + 1] = (byte)((r >> 8) & 0xFF);
                    a[i * 6 + 2] = (byte)(g & 0xFF); a[i * 6 + 3] = (byte)((g >> 8) & 0xFF);
                    a[i * 6 + 4] = (byte)(b & 0xFF); a[i * 6 + 5] = (byte)((b >> 8) & 0xFF);
                }
                return a;
            }
            const int N = 4096;

            // (a) 中性灰 ⇒ bt709 里仍是灰（无负值）⇒ 判"装得下"
            var gray = GF.MeasureRgb48(Rgb48(N, _ => (30000, 30000, 30000)), N, 1);
            Console.WriteLine($"(a) 中性灰: measurable={gray.Measurable} frac={gray.FracOutOf709:E2} maxNeg={gray.MaxNegExcursion:E2} fits={gray.FitsIn709}");
            Check(gray.Measurable && gray.FitsIn709, "gamutfit(a): 中性灰 ⇒ 装在 bt709 内");

            // (b) 纯 bt2020 红 ⇒ bt709 的 G/B 变负（≈ −0.1246）⇒ 判"超出"
            var red = GF.MeasureRgb48(Rgb48(N, _ => (65535, 0, 0)), N, 1);
            Console.WriteLine($"(b) bt2020 纯红: measurable={red.Measurable} frac={red.FracOutOf709:E2} maxNeg={red.MaxNegExcursion:F4} fits={red.FitsIn709}");
            Check(red.Measurable && !red.FitsIn709, "gamutfit(b): bt2020 纯红 ⇒ 超出 bt709");

            // (c) 全黑 ⇒ 地板以上像素为 0 ⇒ 必须"测不出"（**不得**返回"装得下"）
            var black = GF.MeasureRgb48(Rgb48(N, _ => (0, 0, 0)), N, 1);
            Console.WriteLine($"(c) 全黑: measurable={black.Measurable} reason={black.Reason}");
            Check(!black.Measurable, "gamutfit(c): 全黑 ⇒ 三态「测不出」（不得当成装得下）");
            Check(!black.FitsIn709, "gamutfit(c): 测不出时 FitsIn709 必须为 false（保守）");

            // (d) bt709 纯红（换算成 bt2020 表示）⇒ 恰在边界上 ⇒ 判"装得下"
            //     ⚠ 这条钉的是 EpsNeg：去掉容差 ⇒ 边界色被整片计成越界 ⇒ 本条转红。
            var inRed = GF.MeasureRgb48(Rgb48(N, _ => (41123, 4528, 1075)), N, 1);
            Console.WriteLine($"(d) bt709 纯红(bt2020 表示): measurable={inRed.Measurable} frac={inRed.FracOutOf709:E2} maxNeg={inRed.MaxNegExcursion:F6} fits={inRed.FitsIn709}");
            Check(inRed.Measurable && inRed.FitsIn709, "gamutfit(d): bt709 边界内的红 ⇒ 装在 bt709 内（EpsNeg 生效）");

            // (e) **地板负控**：99% 中性灰 + 1% 极暗的越界红 ⇒ 越界像素全在地板下 ⇒ 仍判"装得下"
            //     ⚠ 变异验证：把 FloorFactor 置 0 ⇒ 该暗红像素被计入 ⇒ 本条必须转红。
            var floorCase = GF.MeasureRgb48(
                Rgb48(N, i => (i % 100 == 0) ? (200, 0, 0) : (30000, 30000, 30000)), N, 1);
            Console.WriteLine($"(e) 灰+暗越界红: measurable={floorCase.Measurable} floor={floorCase.LuminanceFloor:F5} frac={floorCase.FracOutOf709:E2} fits={floorCase.FitsIn709}");
            Check(floorCase.Measurable && floorCase.FitsIn709,
                  "gamutfit(e): 越界像素全在亮度地板以下 ⇒ 判「装得下」（地板负控）");

            // (f) 输入长度不符 ⇒ 必须"测不出"（不得把截断数据当有效样本）
            var bad = GF.MeasureRgb48(new byte[N * 6 - 6], N, 1);
            Check(!bad.Measurable, "gamutfit(f): 长度不符 ⇒ 三态「测不出」");
        }
    }
}
