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
            // ── 内核断言：全部归**被执行那一份** ServiceProbe（2026-10-14 删掉五个镜像工程后）──────
            // 原先这里有 5 个组（core / color / engine / geometry / diagnostics），每组指向一个
            // `tests/<组名>` 工程 —— 那些工程是 ServiceProbe 同名 mode 的**第二份拷贝**：
            //   · `<OutputType>Exe</OutputType>` ⇒ `dotnet test` 只还原不跑还 exit 0；
            //   · 全仓没有任何门禁/运行器/`.sln` 引用它们（2026-10-13 逐名 grep 为证）；
            //   · 门禁实测"只镜像那份独有的断言 = 0 条"⇒ 按断言口径删掉不丢任何东西。
            // ⇒ 双拷贝连同"改了执行面、镜像没人管"这类缺陷的土壤一起删除；组表把它们的 mode
            //   原样交给 `ServiceProbe`（目录 `tests/ServiceProbe` 存在、`Program.cs` 里有
            //   `case "<mode>":` 分派 ⇒ 覆盖账的 ②③⑤ 三条取证都仍然成立）。
            // ⚠ 曾经的中间形态（`Modes/<mode>.cs` 由 `_gen-mode-split.ps1` 从组体切片）也一并删除；
            //   "不许再长出第二份"由第 75 条门禁的存在锁看着（它现在量的不再是指纹对拍，而是"只此一份"）。
            ["ServiceProbe"] = new[] {
                "contract", "verdict", "plan", "decision", "wire", "runner", "settings",
                "matrix", "curve", "ootf", "hlgwire", "bt2446", "pq", "colormath", "iccname",
                "engine", "gamut", "gamutfit", "simdswitch",
                "geometry", "firstframe", "orientation", "orientmeta",
                "bench", "peak", "peakmeasure", "psnr", "xcheck", "selftest", "tooldetect", "i18n",
                // 2026-10-15 T12/R6：厂商 HDR 尾注断言只在 ServiceProbe 里有实现（**没有镜像**，
                // 与上面那 31 个「镜像已整体删除」的 mode 同一形态）；调用者 = 在册第 49 条
                // `verify-gainmap-isobmff.ps1` 的 §3d ⇒ 覆盖账的 ⑥-c 不会把它算成零调用点。
                // ⚠ 这个块里**注释也不许出现 ASCII 双引号**：#69 的组表取数扫的是数组块内所有
                //   双引号字面量（不分注释/代码）⇒ 注释里写「双引号包中文」会凭空多出一个 mode，
                //   当场把 ⑥-c 的零调用点抬一级、账目从 67 变 69（2026-10-15 我自己连踩两次，
                //   第二次就踩在这句警告自己的引号上 ⇒ 只能用「」）。
                "vendor-hdr",
                // 2026-10-16（价值普查 A-1）：下面这四组原本各指一个**镜像工程**目录
                // （encode / metadata / gainmap / io），共 1,773 行、134 个 Check 调用，但全仓
                // 没有任何门禁执行它们的 exe —— 唯一提到它们的地方是第 75/76 条的登记表，
                // 而该锁自己的判据写死「镜像那份独有的断言 = 0 条」⇒ 按断言口径并入不丢任何东西。
                // 这 15 个 mode **全都活着**，只是执行体一直是 ServiceProbe：其中 7 个在运行器默认
                // 探针清单里由运行器直接起（encoding / ipc / procstreams / metaraw / thumbnail /
                // oog / avifgm），另外 8 个由在册门禁按参数位置调用（bitdepth / png3 / icc /
                // icc-dump / faithful / gainmap / gainmap-isobmff / ultrahdr）。
                // ⚠ 本块注释里**仍然不许出现 ASCII 双引号**（#69 取数扫数组块内全部双引号字面量，
                //   见上面那句 2026-10-15 的自身反例）⇒ 需要引号处一律用「」。
                "encoding", "bitdepth", "png3", "thumbnail",
                "metaraw", "icc", "icc-dump", "faithful",
                "gainmap", "ultrahdr", "gainmap-isobmff", "oog", "avifgm",
                "ipc", "procstreams",
                // 2026-10-16 可见性普查 ⑨：这三个 mode **每轮真的在被执行**（`verify-gainmap-memory.ps1:234,341`
                // 跑 memadmit、`verify-color-tonemap.ps1:36` 跑 tonemapcurve、
                // `verify-gainmap-isobmff.ps1:484,485` 跑 color），但此前既不在 `$Probes` 也不在本表
                // ⇒ 四档账把它们当不存在（⑦-a 的 67==67 只是「两份手抄互相对表」，不是对代码）。
                // ⚠ 本仓第一版 ⑨ 落地时把 `color` 登记成了「零调用点的门外手工工具」，当场被 ⑨-a 判红：
                //   它的调用者是**在册**门禁（起 `$sp` = ServiceProbe.exe，stdout 喂给 §3b 两条 CK）
                //   ⇒ 按「有调用点就必须进账」的规矩并进本组，而不是留在 #69 的豁免表里。
                "memadmit", "tonemapcurve", "color",
                // 2026-10-21 ⑨ 再次开口：`framelen` 由 `verify-gainmap-memory.ps1` 的 H-6 **每轮执行**
                // （进程内调 `GainMapEncoder.EncodeAsync` 的帧长守卫）⇒ 按「有调用点就必须进账」并进本组，
                // 不进任何豁免表。它不读外部工具、不落盘（守卫在第一步返回），所以没有夹具依赖。
                "framelen",
            },

            // ── 编码参数与产物 / 元数据与 ICC / 增益图与 Ultra HDR / 进程与 IPC ──
            // 这四组原先各指向一份镜像工程（tests/encode、tests/metadata、tests/gainmap、tests/io），
            // 2026-10-16 价值普查 A-1 判定为**无人执行的拷贝**并删除 ⇒ 它们的 mode 全部并入上面的
            // `ServiceProbe` 组（见那一段的来历注释）。**执行体只有 ServiceProbe 这一份。**

            // ── 增益图宿主（GainMapTestHost）三段物理拆分（2026-10-05）──
            // ⚠ 旧宿主是三段**串行**单文件（A 编码 → B 解码闭环 / C 结构；B、C 消费 A 的产物）。
            //   拆分后每组**自带夹具**（见各组文件头：解码组/结构组各自调一次 GainMapEncoder
            //   产出自己目录下的件）⇒ 三个 mode 可独立跑、可任意顺序跑，不依赖别人先跑过什么。
            ["gainmap-encode"] = new[] { "gainmap-encode" },   // A 编码验证 + D23–D38 位深/域 PSNR
            ["gainmap-decode"] = new[] { "gainmap-decode" },   // B 解码闭环 + E 方向标签 + D15–D32
            ["gainmap-struct"] = new[] { "gainmap-struct" },   // C 输出结构（exiftool）+ D PNG 3.0 sBIT/cICP

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
