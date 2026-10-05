# ═══════════════════════════════════════════════════════════════════════════════
# 【定位】**工具，不是门禁**（名字以 _ 开头 = 手工按需跑；门禁是 verify-* 前缀）。
#   正式门禁见 verify-ui-split-verbatim.ps1（第 70 条）与 verify-ui-split-union.ps1（第 71 条）。
#   本脚本保留的价值：让物理拆分这件事**可复现、可复核**，而不是一次性的手工搬运。
# _gen-ui-split.ps1 —— 生成 UI 测试组工程（一次性脚手架，2026-10-05 拆分）
#
# 为什么用脚本生成而不是手抄：
#   UiTestHost/Program.cs 有 3529 行、17 个组，断言字符串**必须逐字保留**
#   （5 条 UI 门禁 grep 它们，verify-ui-param-defects.ps1:198 还逐锚点要求在册）。
#   手抄 3000+ 行必然引入字符漂移 ⇒ 用**按行区间切片**的方式搬运，保证方法体逐字一致。
#
# 本脚本只做搬运 + 拼装（加 using / 命名空间 / 转发包装），不改任何断言文本。
# ═══════════════════════════════════════════════════════════════════════════════

param(
    [string]$Src = 'tests/UiTestHost/Program.cs',
    [string]$RepoRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
)

$ErrorActionPreference = 'Stop'
Set-Location $RepoRoot

$lines = Get-Content $Src -Encoding utf8

# ── 定位每个 GroupX_ 方法的行区间 ────────────────────────────────────────────
$starts = [ordered]@{}
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^\s*static\s+void\s+(Group[A-Z]_[A-Za-z0-9_]+)\s*\(') {
        $starts[$Matches[1]] = $i + 1
    }
}
$names = @($starts.Keys)

# ⚠ 方法体的**真实结束行** = 与之配对的右花括号所在行（按花括号配平扫描）。
#   为什么不能简单取"下一个组起始行 - 1"：**最后一个组**（N）没有下一个组，
#   取到 EOF 会把其后的 `RunNegativeControl` / `Snip` / 类与命名空间的右花括号
#   一并吞进来 ⇒ 生成文件花括号少 2 个 ⇒ CS1022（实测踩过，5 个工程全挂）。
function Get-MethodEnd([int]$startLine) {
    $depth = 0; $seen = $false
    for ($i = $startLine - 1; $i -lt $lines.Count; $i++) {
        $code = ($lines[$i] -replace '//.*$', '')          # 去行尾注释，避免注释里的花括号干扰
        foreach ($ch in $code.ToCharArray()) {
            if ($ch -eq '{') { $depth++; $seen = $true }
            elseif ($ch -eq '}') {
                $depth--
                if ($seen -and $depth -eq 0) { return $i + 1 }   # 1-based 行号
            }
        }
    }
    return $lines.Count
}

$bounds = @{}
for ($k = 0; $k -lt $names.Count; $k++) {
    $s = $starts[$names[$k]]
    $e = Get-MethodEnd $s
    # 上限保护：不得越过下一个组的起始行
    if ($k + 1 -lt $names.Count -and $e -ge $starts[$names[$k + 1]]) { $e = $starts[$names[$k + 1]] - 1 }
    $g = ($names[$k] -replace 'Group(\w)_.*', '$1')
    $bounds[$g] = @{ S = $s; E = $e; Meth = $names[$k] }
}

# 只保留方法体本身（不含下一组的前导注释块）
function Get-Body([string]$g) {
    $b = $bounds[$g]
    return @($lines[($b.S - 1)..($b.E - 1)])
}

# ── 5 个工程的分组映射（与 docs/TEST_SPLIT_TEMPLATE.md 同口径）──────────────
$projects = [ordered]@{
    'ui-core'  = @{ asm = 'Tests.UiCore';  ns = 'Tests.UiCore';  groups = @('A','B','C','D','N') }
    'ui-mode'  = @{ asm = 'Tests.UiMode';  ns = 'Tests.UiMode';  groups = @('E','F','G','O') }
    'ui-color' = @{ asm = 'Tests.UiColor'; ns = 'Tests.UiColor'; groups = @('H','I','K','R') }
    'ui-sweep' = @{ asm = 'Tests.UiSweep'; ns = 'Tests.UiSweep'; groups = @('J','L') }
    'ui-cli'   = @{ asm = 'Tests.UiCli';   ns = 'Tests.UiCli';   groups = @('M','P') }
}

# ── 需要**显式前置**的入口（默认是单行转发）────────────────────────────────
# ⚠ 为什么 N 需要前置（2026-10-05 拆分时实测，非拆分引入）：
#   `GroupN` 的 N5b 把「载入预设后的命令」与「同场景直接选中的基线命令」逐字节比较。
#   两条路径对 **线程开关** 的写入不同步：
#     · 基线路径：`UpdateOptionAvailability()` → `UpdateThreadAvailabilityForFormat()`
#       （MainWindow.xaml.cs:2272）在 **后端 == Cjpegli** 时**强制** `SingleThreadCheck = true`
#       ⇒ 命令里出现 `-threads 1`；
#     · 往返路径：`ApplyPresetData()` 把预设里的 `p.SingleThread` **原样恢复**（:5944）。
#   而「后端是不是 Cjpegli」取决于 `EncoderCombo` 的**可用编码器列表**，后者由
#   `encoders.Where(e => e.IsAvailable)`（:2063）过滤 —— `IsAvailable` 来自
#   `CjpegliService` / `CjxlService` 的**探测态**，而探测是启动期 fire-and-forget 起的。
#   ⇒ 探测尚未落地时列表不同 ⇒ 选中项不同 ⇒ 两条路径的 `-threads` 分叉。
#   实测（独立进程跑 ui-preset-roundtrip 6 次）：**3 次 39 PASS/0 FAIL、3 次 38 PASS/1 FAIL**，
#   红的恒是 `N5b IccModeNone`（`-threads 16` vs `-threads 1`）。
#   旧宿主之所以从不红：它把 N 排在 A..R 之后，前面的组（尤其 GroupG 显式调
#   `ExternalToolsDetector.EnsureAllDetected()`，见其 G1）已把探测态落实 —— 也就是说
#   **旧宿主的绿是靠"先跑过别的组"换来的**，N 单跑本来就是不稳的。
#   ⇒ 处理方式（不伪造、不改断言）：在本 mode 入口显式做**同一个**确定性前置
#     （同步探测一次外部工具），把旧宿主里"碰巧已落实"的状态变成**显式且可复现**的。
#     这是**补齐前置**，不是放宽判据：N5b 仍然逐字节比较命令串。
# ⚠ `$explicitEntry` 在本文件**所有**前置数组（$convergePre / $colorPre）定义**之后**才构造
#   —— PowerShell 按顺序求值，提前引用会拿到 $null（实测：H 的 $colorPre 被静默丢弃，
#   生成出来的 RunH 只有收敛前置、H10 照旧转红）。

# ── 入口前置：**等启动期工具探测收敛**（除 P 之外的每个 mode 都做）────────────
# ⚠⚠ 为什么每个 mode 都需要它（2026-10-05 拆分时实测，**非拆分引入**的既有依赖）：
#   `InitControls()` 里的工具探测是 **fire-and-forget**（`Task.Run` ×7 并发，
#   MainWindow.xaml.cs:1227-1245）。旧宿主把 `GroupP_StartupDetectLog` 排在**启动后第一个**，
#   而 P 会轮询「7 个槽位都打出结论行」—— 于是**后续所有组都在"探测已落地"的稳态下跑**。
#   拆成"一个 mode 一个进程"后，若单跑某个 mode，就丢掉了这个隐式前置，实测两类竞态：
#     · `C3d`（ui-panel-visibility）：清空 `ExifToolService._detectedPath` 后断言下拉禁用，
#       而后台那次 `Detect()` 可能恰好在此刻把路径**回填** ⇒ `enabled=True` 转红。
#       实测单跑 3 次红 1 次（本次验收里也复现了一次）。
#     · `N5b`（ui-preset-roundtrip）：`EncoderCombo` 的可用项列表
#       （`encoders.Where(e => e.IsAvailable)`，:2063）尚未定型 ⇒ 基线路径与往返路径的
#       `-threads` 分叉（`-threads 1` vs `-threads 16`）。实测单跑 6 次红 3 次。
#   ⇒ 处理方式（**不伪造、不改断言、不放宽判据**）：把 P 当初等的那个条件抽成
#     `UiHost.AwaitStartupDetection()`（共享一份，见 UiHostShared/UiHost.cs），
#     在每个 mode 入口显式等一次。它**不做任何断言、不动计数**，只是等待既有后台工作收敛；
#     P 自己**不调用**它（P 必须原样观察启动窗口，它的轮询就是被测语义本身）。
$convergePre = @(
    '            // ⚠ 共享前置（见生成器 $convergePre 长注释）：等启动期 fire-and-forget 的',
    '            //   工具探测（7 个槽位）收敛，复现旧宿主"GroupP 先跑"建立的稳态。',
    '            //   不做任何断言、不动 PASS/FAIL 计数 —— 只等既有后台工作落地。',
    '            UiHost.AwaitStartupDetection();',
    '            // ⚠ 再落实一次外部工具路径/探测（旧宿主只在 GroupG 里做过，见 :781-784）：',
    '            //   不做则 `EncoderCombo` 的硬件项/外部工具后端不可解析 ⇒ `L1e-0` 间歇红、',
    '            //   连带 `L1f`..`L1k` 六条**整段不执行**（实测单跑 3 次红 1 次）。',
    '            UiHost.ApplyToolPaths();',
    '            // ⚠ 再把「ffmpeg 能力 + 编码器列表」两个缓存**同步**落实：',
    '            //   Step 2 是 Task.Run + Task.WhenAny(8s)，等日志文本仍有残余窗口',
    '            //   ⇒ 实测 L1e-0 6 次红 1 次、N5b 6 次红 4 次。本调用直接等缓存建好',
    '            //   （两服务都幂等带缓存，重复调用不改变最终值）。',
    '            UiHost.SettleCapabilities();',
    '            // ⚠⚠ 最后把**格式 ⇒ 编码器下拉**这条联动走完：`SetFormat` 只 fire-and-forget 起',
    '            //   `RefreshEncoderListAsync`（:2051 async）⇒ 紧随其后的读取可能仍看到**上一个格式**的',
    '            //   残留项（启动是 JPEG ⇒ cjpegli）⇒ `GetCurrentEncoderBackend()`=Cjpegli',
    '            //   ⇒ `SingleThreadCheck` 被置真且**再不复位**（:2367-2372 的 else 只恢复 IsEnabled）',
    '            //   ⇒ 命令恒 `-threads 1`（:1425）。N5b 逐字节比两条路径 ⇒ 分叉。',
    '            //   实测 ui-preset-roundtrip 单跑 10 次红 8 次；本行把联动走到稳态。',
    '            UiHost.SettleFormatLinkage();'
)

# ── 组 H 的**额外**前置：把输出格式预置成 PNG ─────────────────────────────────
# ⚠⚠ 为什么 H 需要它（2026-10-05 拆分时实测定位，**非拆分引入**的既有耦合）：
#   `H10` 的写法是「先把 `ColorSpaceCombo` 选成 `Display P3`，再 `SetFormat("PNG")`，再读命令」。
#   而 `SetFormat` 只要**真的触发了** `FormatCombo_SelectionChanged`，就会走到
#   `UpdateFormatCapabilities()`：那里 `ColorSpaceCombo.Items.Clear()`（:2224）后重建、
#   并 `ColorSpaceCombo.SelectedIndex = 0`（:2245）⇒ **刚选的 `Display P3` 被抹掉**
#   ⇒ 命令里既无 `smpte432` 也无 `zscale/iccgen` ⇒ H10 转红。
#   H10 能过，靠的是「`SetFormat("PNG")` 恰好是**空操作**」——即 `FormatCombo` 当时**已经是 PNG**，
#   赋同值不触发事件（Avalonia 语义）⇒ 不重建 ⇒ `Display P3` 保住。
#   旧宿主里这个前提是 `GroupG` 顺手建立的（`Program.cs:791` 的 `SetFormat("PNG")`，
#   而 H 排在 G 之后）。拆成 `ui-color` 后 H 变成**第一个**跑的组，此时
#   `FormatCombo` 还是启动值 `StillFormats[0] == "JPEG"`（`:536` 置 `SelectedIndex = 0`）
#   ⇒ `SetFormat("PNG")` 首次真的生效 ⇒ 重建 ⇒ 抹掉选择 ⇒ 实测 `ui-color` 稳定 14/1。
#   ⇒ 处理方式（**不改断言、不放宽判据**）：在本 mode 入口显式把格式预置为 PNG，
#     复现旧宿主进入 H 时的同一状态。H10 仍然照原样读命令串并断言 `smpte432` 且非 `bt2020`。
$colorPre = @(
    '            // ⚠ 额外前置（见生成器 $colorPre 长注释）：把输出格式预置为 PNG，',
    '            //   复现旧宿主里 GroupG 在 H 之前做过的 SetFormat("PNG")。',
    '            //   不做这一步时 H10 会因 SetFormat("PNG") **首次真正生效**而重建 ColorSpaceCombo',
    '            //   （:2224 Clear + :2245 SelectedIndex=0），把刚选的 "Display P3" 抹掉 ⇒ 稳定转红。',
    '            SetFormat("PNG");'
)

# ── 需要"收敛前置 + 组专属前置"的入口（其余组只用 $convergePre）──────────────
# ⚠ 在这里构造（而不是在上面）：PowerShell 顺序求值，`$colorPre` 必须先存在。
$explicitEntry = @{
    'H' = @($convergePre) + @('') + $colorPre + @('            GroupH_Color();')
    # ⚠ L 的额外前置：**让编码器列表为 AVIF 定型**。
    #   为什么（实测）：`L1e` 组内在读到 `⚡` 硬件项之前先做 `SetFormat("AVIF")`，而
    #   `RefreshEncoderListAsync` 是 async（:2051）⇒ 列表可能还是 PNG 的
    #   ⇒ `PickEncoder(s => s.StartsWith("⚡"))` 返回 null ⇒ `L1e-0` 转红，
    #   并且因为它**随即 `return`**，同族的 `L1f`..`L1k` **六条断言整段不执行**
    #   （并集从 409 掉到 403 —— 这是"断言静默变少"而非"变红"，最危险的一种）。
    #   实测 `ui-param-defects` 单跑 12 次红 2 次。
    #   ⇒ 入口先把格式走到 AVIF 并等列表出现 avif 编码器族；L 自己随后 `SweepReset()`
    #     回 PNG，故该前置**不影响** L 的任何断言取值。
    'L' = @($convergePre) + @(
        '            // ⚠ 额外前置（见生成器 $explicitEntry[''L''] 长注释）：把 AVIF 的编码器列表定型，',
        '            //   否则 L1e-0 会因列表未换过来而红，并连带 L1f..L1k 六条**整段不执行**。',
        '            SetFormat("AVIF");',
        '            UiHost.AwaitEncoderListSettled("avif");'
    ) + @('            GroupL_ParamDefects();')
}

# ── 组字母 → 旧宿主里方法名的后缀（`GroupX_<后缀>`，逐字取自旧宿主）──────────
# 用于生成 `internal static void RunX() => GroupX_<后缀>();` 入口转发。
$methodSuffix = @{
    'A' = 'FormatToCommand';        'B' = 'ParameterPassing'
    'C' = 'PanelVisibility';        'D' = 'EncoderComboLinkage'
    'E' = 'SimpleMode';             'F' = 'BackgroundVisibility'
    'G' = 'EndToEnd';               'H' = 'Color'
    'I' = 'IccUi';                  'J' = 'UiSweep'
    'K' = 'StrategyMap';            'L' = 'ParamDefects'
    'M' = 'CliStrict';              'N' = 'PresetRoundTrip'
    'O' = 'GainMapToggleKeepsTargets'
    'P' = 'StartupDetectLog';       'R' = 'EncoderArgDomains'
}

# ── 组名 → mode 名（TestGroups 的键 = 组名 = mode 名规格，由 Lead 指定）──────
$modeOf = @{
    'A' = 'ui-format-command'; 'B' = 'ui-parameter-passing'; 'C' = 'ui-panel-visibility'
    'D' = 'ui-encoder-linkage'; 'N' = 'ui-preset-roundtrip'
    'E' = 'ui-simple-mode'; 'F' = 'ui-background-visibility'; 'G' = 'ui-end-to-end'
    'O' = 'ui-gainmap-toggle'
    'H' = 'ui-color'; 'I' = 'ui-icc'; 'K' = 'ui-strategy-map'; 'R' = 'ui-encoder-args'
    'J' = 'ui-sweep'; 'L' = 'ui-param-defects'
    'M' = 'ui-cli-strict'; 'P' = 'ui-startup-detect'
}

$usingBlock = @'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Tests.Shared;
using UiHostShared;
'@

# ═══════════════════════════════════════════════════════════════════════════════
# 转发包装：让**逐字搬来的组体**继续用裸名调用（Pump/Find/Check/...）。
# ⚠ 这些包装**不复制实现** —— 每一个都直接转发到 UiHostShared.UiHost，
#   保证 5 个工程读的是**同一份** GUI 助手（否则就是 5 份实现漂移）。
# ═══════════════════════════════════════════════════════════════════════════════
$forwarders = @'
        // ══════════════ 转发包装（**不复制实现**，一律转 UiHostShared.UiHost）══════════════
        // 为什么保留裸名包装：组体是**逐字**从旧宿主搬来的（断言文本一个字未改）。
        // 若把组体里的 `Pump(6)` 全改成 `UiHost.Pump(6)`，就等于"顺手改了 3000 行"，
        // 反而把拆分风险从"结构"扩散到"每一行"。保留同名包装 = 迁移零文本改动。
        // ⚠ 计数/输出格式**只有** Tests.Shared.Harness 一份真值（Check 转发的就是它）。
        // ⚠ BF/BFS 用 `const` 而不是 `static readonly`：UiHost.BF 本身就是 const，
        //   而某个工程可能一个反射都不用它 ⇒ `static readonly` 会触发 CS0414
        //   （"字段已赋值但从未使用"）。const 不占字段、不产生该警告，
        //   语义也更强（编译期内联，不存在"两个实现漂移"的余地）。
        private const BindingFlags BF = UiHost.BF;
        private const BindingFlags BFS = UiHost.BFS;
        private static FfmpegGui.MainWindow W { get => UiHost.W; set => UiHost.W = value; }
        private static string RepoRoot => UiHost.RepoRoot;
        private static string SrcDir => UiHost.SrcDir;
        private static string TempDir => UiHost.TempDir;

        private static void Pump(int n = 6) => UiHost.Pump(n);
        private static T? Find<T>(string name) where T : Control => UiHost.Find<T>(name);
        private static void SetInput(string path) => UiHost.SetInput(path);
        private static void Regen() => UiHost.Regen();
        private static string Cmd() => UiHost.Cmd();
        private static void SetFormat(string s) => UiHost.SetFormat(s);
        private static void Click(string name) => UiHost.Click(name);
        private static void Check(string name, bool cond, string detail = "") => UiHost.Check(name, cond, detail);
        private static void Safe(string name, Action a) => UiHost.Safe(name, a);
        private static int Run(string exe, string arguments) => UiHost.Run(exe, arguments);
        private static string RunOut(string exe, string arguments) => UiHost.RunOut(exe, arguments);
        private static List<string> Tok(string s) => UiHost.Tok(s);
        private static int IdxFrom(List<string> t, string key, int from) => UiHost.IdxFrom(t, key, from);
        private static int Idx(List<string> t, string key) => UiHost.Idx(t, key);
        private static string? Val(List<string> t, string key) => UiHost.Val(t, key);
        private static string? ValAfter(List<string> t, string key, int from) => UiHost.ValAfter(t, key, from);
        private static bool HasTok(List<string> t, string exact) => UiHost.HasTok(t, exact);
        private static List<string> VfSegs(List<string> t) => UiHost.VfSegs(t);
        private static bool VfHas(List<string> t, string seg) => UiHost.VfHas(t, seg);
        private static bool VfHasPrefix(List<string> t, string prefix) => UiHost.VfHasPrefix(t, prefix);
        private static bool TokEq(List<string> t, string a, string b) => UiHost.TokEq(t, a, b);
        private static string TokStr(List<string> t) => UiHost.TokStr(t);
        private static void DumpCmd(string tag, string cmd) => UiHost.DumpCmd(tag, cmd);
        private static void SweepReset() => UiHost.SweepReset();
        private static void SetStrategyRadio(string name) => UiHost.SetStrategyRadio(name);
        private static string? PickEncoder(Func<string, bool> pred) => UiHost.PickEncoder(pred);
        private static string? PickFfmpegEncoder() => UiHost.PickFfmpegEncoder();
        private static string? ZscaleParam(List<string> t, string key) => UiHost.ZscaleParam(t, key);
        private static string UiState() => UiHost.UiState();
        private static int CountTok(List<string> t, string key) => UiHost.CountTok(t, key);
        private static string Snip(string s) => UiHost.Snip(s);
        private static void ClassifyDetectLines(string? text, string[] names,
            System.Collections.Generic.List<string> verdicts,
            System.Collections.Generic.List<string> others,
            System.Collections.Generic.List<string> suspects)
            => UiHost.ClassifyDetectLines(text, names, verdicts, others, suspects);
'@

foreach ($proj in $projects.Keys) {
    $info = $projects[$proj]
    $dir  = Join-Path 'tests' $proj
    New-Item -ItemType Directory -Force -Path $dir | Out-Null

    # ── 组体文件 ──
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine($usingBlock)
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("namespace $($info.ns)")
    [void]$sb.AppendLine("{")
    [void]$sb.AppendLine("    /// <summary>")
    [void]$sb.AppendLine("    /// 子系统测试组：**$proj** —— 2026-10-05 从 UiTestHost 的单个 3500 行")
    [void]$sb.AppendLine("    /// <c>Program.cs</c> 中物理拆出。组体**逐字**搬来（断言文本一字未改，门禁在 grep 它们）。")
    [void]$sb.AppendLine("    /// <para><b>共享 GUI 底座</b>：窗口 <c>W</c>、<c>TempDir</c>、以及全部 GUI/token 助手")
    [void]$sb.AppendLine('    /// 都在 UiHost（UiHostShared 工程）—— 本类只保留同名**转发包装**，')
    [void]$sb.AppendLine("    /// 不复制实现。</para>")
    [void]$sb.AppendLine("    /// </summary>")
    [void]$sb.AppendLine("    internal static class $($proj -replace '-','')Group")
    [void]$sb.AppendLine("    {")
    [void]$sb.AppendLine($forwarders)

    foreach ($g in $info.groups) {
        [void]$sb.AppendLine()
        [void]$sb.AppendLine("        // ══════════════════════════ 组 $g ══════════════════════════")
        foreach ($l in (Get-Body $g)) { [void]$sb.AppendLine($l) }
    }

    # ── 入口转发（供 Program.Main 逐 mode 分派；组体保持私有原貌不动）──
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("        // ══════════════ 入口（供 Program.Main 逐 mode 分派）══════════════")
    [void]$sb.AppendLine("        // 只做转发 + 一个**共享收敛前置**：组体本身是逐字搬来的私有方法，保持原貌不动。")
    foreach ($g in $info.groups) {
        if ($explicitEntry.ContainsKey($g)) {
            [void]$sb.AppendLine("        internal static void Run$g()")
            [void]$sb.AppendLine("        {")
            foreach ($l in $explicitEntry[$g]) { [void]$sb.AppendLine($l) }
            [void]$sb.AppendLine("        }")
        } elseif ($g -eq 'P') {
            # ⚠ P **不做**收敛前置：它的轮询本身就是被测语义（观察启动窗口），
            #   先等一次会把"启动期还没打完"这个被测状态消掉。
            [void]$sb.AppendLine("        // ⚠ P **故意不加**收敛前置：本组测的就是「启动期刚打完那 7 行」这个窗口，")
            [void]$sb.AppendLine("        //   先调 AwaitStartupDetection() 会把被测状态本身消掉（见生成器注释）。")
            [void]$sb.AppendLine("        internal static void RunP() => GroupP_$($methodSuffix['P'])();")
        } else {
            [void]$sb.AppendLine("        internal static void Run$g()")
            [void]$sb.AppendLine("        {")
            foreach ($l in $convergePre) { [void]$sb.AppendLine($l) }
            [void]$sb.AppendLine("            Group$($g)_$($methodSuffix[$g])();")
            [void]$sb.AppendLine("        }")
        }
    }

    [void]$sb.AppendLine("    }")
    [void]$sb.AppendLine("}")

    $groupFile = Join-Path $dir "$($proj -replace '-','')Group.cs"
    # 统一换行为 CRLF 之外保持原样：直接写字节，避免 PowerShell 追加换行
    [System.IO.File]::WriteAllText((Resolve-Path -LiteralPath $dir).Path + "\" + (Split-Path -Leaf $groupFile),
        $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "wrote $groupFile"
}
