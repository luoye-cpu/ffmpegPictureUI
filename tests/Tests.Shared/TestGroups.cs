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

            // ── 几何：缩放 / 首帧 / 取向 ──
            ["geometry"] = new[] { "geometry", "firstframe", "orientation", "orientmeta" },

            // ── 进程与 IPC ──
            ["io"] = new[] { "ipc", "procstreams" },

            // ── 诊断与性能 ──
            ["diagnostics"] = new[] { "bench", "peak", "peakmeasure", "psnr", "xcheck", "selftest", "tooldetect", "i18n" },
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
