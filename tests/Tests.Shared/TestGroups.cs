using System;
using System.Collections.Generic;
using System.Linq;

namespace Tests.Shared
{
    /// <summary>
    /// **测试组表**（单一真值）：子系统工程 ↔ 其覆盖的 mode 名。
    ///
    /// <para><b>为什么必须是单一真值</b>：拆分后若"哪些 mode 归哪个组"散落在各工程里，
    /// 就会漂移出「某个 mode 不属于任何组」⇒ 该 mode **永远不被任何组跑到** = 假覆盖。
    /// 本仓有第 54 条的前车之鉴（`_probe-settings-clone-coverage.ps1` **存在但没上清单** ⇒
    /// 谁也不跑）。故：**组表只此一份**，并由门禁断言"组表 mode 并集 == 运行器 `$Probes` 默认清单"。</para>
    ///
    /// <para><b>粒度</b>：按用户口径取"更细"—— 组名尽量贴近 mode 名，定位精确。</para>
    /// </summary>
    public static class TestGroups
    {
        /// <summary>子系统工程名 → 该组覆盖的 mode（顺序即默认执行顺序）。</summary>
        public static readonly IReadOnlyDictionary<string, string[]> Map =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // ── 决策层：契约 / 裁决 / 计划 / 路由 / 落盘 ──
            ["core"] = new[] { "contract", "verdict", "plan", "decision", "wire", "runner", "settings" },

            // ── 色彩基元与数学 ──
            ["color"] = new[] { "matrix", "curve", "ootf", "hlgwire", "bt2446", "pq", "colormath", "iccname" },

            // ── 自研引擎与像素内核 ──
            ["engine"] = new[] { "engine", "gamut", "gamutfit", "simdswitch" },

            // ── 编码参数与产物 ──
            ["encode"] = new[] { "encoding", "bitdepth", "png3", "thumbnail" },

            // ── 元数据与 ICC ──
            ["metadata"] = new[] { "metaraw", "icc", "icc-dump", "faithful" },

            // ── 增益图 / Ultra HDR ──
            ["gainmap"] = new[] { "gainmap", "ultrahdr", "gainmap-isobmff", "oog", "avifgm" },

            // ── 增益图宿主（GainMapTestHost）三段物理拆分（2026-10-05）──
            // ⚠ 旧宿主是三段**串行**单文件（A 编码 → B 解码闭环 / C 结构；B、C 消费 A 的产物）。
            //   拆分后每组**自带夹具**（见各组文件头：解码组/结构组各自调一次 GainMapEncoder
            //   产出自己目录下的件）⇒ 三个 mode 可独立跑、可任意顺序跑，不依赖别人先跑过什么。
            ["gainmap-encode"] = new[] { "gainmap-encode" },   // A 编码验证 + D23–D38 位深/域 PSNR
            ["gainmap-decode"] = new[] { "gainmap-decode" },   // B 解码闭环 + E 方向标签 + D15–D32
            ["gainmap-struct"] = new[] { "gainmap-struct" },   // C 输出结构（exiftool）+ D PNG 3.0 sBIT/cICP

            // ── 几何：缩放 / 首帧 / 取向 ──
            ["geometry"] = new[] { "geometry", "firstframe", "orientation", "orientmeta" },

            // ── 进程与 IPC ──
            ["io"] = new[] { "ipc", "procstreams" },

            // ── 诊断与性能 ──
            ["diagnostics"] = new[] { "bench", "peak", "peakmeasure", "psnr", "xcheck", "selftest", "tooldetect", "i18n" },

            // ── UI 行为（Avalonia.Headless 加载真实 MainWindow；2026-10-05 从 UiTestHost 拆分）──
            // ⚠ 这 5 个工程与上面各组的**关键差别**：它们共用一个**活窗口** `W`
            //   （点控件 / 读 CommandText / 反射调 private 处理器）。
            //   ⇒ 共享 GUI 底座单独抽成 tests/UiHostShared（窗口 W / TempDir / Avalonia 引导 /
            //     Pump / Find<T> / SetInput / Regen / Cmd / SetFormat / Click / Check / Safe /
            //     SweepReset / SetStrategyRadio / PickEncoder / ZscaleParam / UiState /
            //     ClassifyDetectLines + token 助手族），**只此一份**，5 个工程共同引用。
            // ⚠ 与 ServiceProbe 各组不同：UI 断言**不做纯函数委派** —— 必须在真实控件树上取值，
            //   换个环境（无头平台/控件模板）读数就变，故组体逐字保留。
            // ⚠ GroupP_StartupDetectLog 必须**紧跟启动**观察启动期日志（见 tests/ui-cli/Program.cs
            //   的顺序不变式），故它单独占一个 mode 且排在 ui-cli 的第一位。

            // 核心：输出格式→命令 / 参数传递 / 面板显隐联动 / 编码器下拉联动 / 预设往返
            ["ui-core"] = new[] { "ui-format-command", "ui-parameter-passing", "ui-panel-visibility", "ui-encoder-linkage", "ui-preset-roundtrip" },

            // 模式轴：简洁模式 / 后台可见性 / GUI 端到端 / GainMap 开关保目标
            ["ui-mode"] = new[] { "ui-simple-mode", "ui-background-visibility", "ui-end-to-end", "ui-gainmap-toggle" },

            // 色彩面：色彩管线 / ICC 选项 / 策略映射 / 编码参数分域
            ["ui-color"] = new[] { "ui-color", "ui-icc", "ui-strategy-map", "ui-encoder-args" },

            // 控件穷举与缺陷锁
            ["ui-sweep"] = new[] { "ui-sweep", "ui-param-defects" },

            // CLI 口径 + 启动期检测日志（⚠ P 必须紧跟启动，见该工程 Program.cs 的顺序不变式）
            ["ui-cli"] = new[] { "ui-cli-strict", "ui-startup-detect" },
        };

        /// <summary>取某组覆盖的 mode；组名未知 ⇒ 抛（fail-closed，不返回空集合蒙混）。</summary>
        public static string[] ModesOf(string group)
            => Map.TryGetValue(group, out var m)
               ? m
               : throw new ArgumentException($"未知测试组 '{group}'。可用: {string.Join(", ", Map.Keys)}");

        /// <summary>全部组名（稳定顺序）。</summary>
        public static IEnumerable<string> GroupNames => Map.Keys;

        /// <summary>
        /// 组表覆盖的 mode **并集**（去重）。门禁用它与本仓运行器的 `$Probes` 默认清单对拍。
        /// </summary>
        public static string[] AllModes()
            => Map.Values.SelectMany(v => v).Distinct(StringComparer.OrdinalIgnoreCase)
                  .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();

        /// <summary>某个 mode 属于哪些组（可能多个 —— 组是运行入口分组，不是独占归属）。</summary>
        public static string[] GroupsOf(string mode)
            => Map.Where(kv => kv.Value.Contains(mode, StringComparer.OrdinalIgnoreCase))
                  .Select(kv => kv.Key).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
