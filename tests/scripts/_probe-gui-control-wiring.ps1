# _probe-gui-control-wiring.ps1 —— 结构锁：「**声明了却没人接线**」的 GUI 控件必须为 0
#
# 为什么需要这一条（本条锁的立锁动机，2026-10-04 实测）：
#   本仓新增 `--raw-color-target`（三档）时**同时**加了 GUI 下拉 `RawColorTargetCombo`，
#   并在 `MainWindow.xaml.cs` 里 `FindControl` + 两处读取（`BuildPresetData` / 命令预览）。
#   但它**既没有初始选中、也没有 `SelectionChanged` 处理器** —— 而它的 8 个兄弟下拉**都有**初始选中。
#   后果（实测）：Avalonia 的 `ComboBox` 在无选中时 `SelectedItem == null`
#   ⇒ `RawColorTarget = RawColorTargetCombo?.SelectedItem as string` 恒为 **null**
#   ⇒ `QueueProcessor` 的 `?? "rec2020"` **永远生效** ⇒ **用户在 GUI 里选 `auto`/`rec709` 完全无效**，
#     且命令预览不刷新。而 **CLI 同参数完全正常**（实测产物 BT.709 ICC）⇒ 缺陷**只在 GUI**，
#     是典型的「交付了却不能用的控件」（宣称≠交付），且**任何现有门禁都抓不到**：
#     `verify-ui-*` 跑的是 UiTestHost 的行为断言，没人去点这个下拉，因此「控件是死的」不产生任何红。
#
#   为什么用**静态结构锁**而不是补一条 UI 行为断言：
#     ① 本缺陷的本质是「**接线漏了**」，是结构问题，静态可判、零成本、毫秒级；
#     ② 行为断言要驱动 GUI 下拉，成本高且容易写成"只验证控件存在"的假绿。
#     ③ 与 `_probe-geometry-single-source-scan.ps1` / `_probe-settings-clone-coverage.ps1` 同族。
#
# 判据（全部只看代码；⚠ 先剥掉**整行注释**再匹配 —— 注释里提一句就能把文本锁喂绿，本仓真踩过）：
#   ① 对 `MainWindow.xaml` 里**每一个** `<ComboBox x:Name="XCombo">`：
#      `MainWindow.xaml.cs` 必须同时存在
#        (a) `XCombo.SelectedIndex =`（初始选中）—— 否则 `SelectedItem` 恒 null；
#        (b) `XCombo.SelectionChanged +=`（变更响应）—— 否则改了不刷新/不生效。
#   ② 已知例外必须**显式登记**在下面的白名单里（附理由），不允许靠"没写判据"蒙混。
#   ③ 反控（防空跑）：白名单之外的 combo **必须 ≥ 8 个**（本仓基线；掉下去说明正则失效、
#      判据在空跑 ⇒ 判红）。
#
# 退出码：0 = 全绿；1 = 有失败（含"读不到文件"/"判据空跑"这类不可评估情形 —— fail-closed）。
#
# 双宿主兼容（PS7 / PS5.1）：禁 `?:` / `??` / `?.` / `&&` / `||`；
#   条件表达式一律 `$(if (...) {...} else {...})`。
# 代码串纯 ASCII（中文只放注释）：否则会被 `_probe-cjk-hardcode-scan` 计入 UI 桶（余量 0）。

$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

$script:pass = 0; $script:fail = 0
function CK([bool]$ok, [string]$msg) {
    if ($ok) { $script:pass++; Write-Output "  PASS $msg" }
    else     { $script:fail++; Write-Output "  FAIL $msg" }
}

$csPath   = Join-Path $root 'src/FfmpegGui/MainWindow.xaml.cs'
$xamlPath = Join-Path $root 'src/FfmpegGui/MainWindow.xaml'

if (-not (Test-Path $csPath) -or -not (Test-Path $xamlPath)) {
    Write-Output "FAIL cannot read MainWindow.xaml(.cs) -- fail-closed (not a pass)"
    Write-Output "PASS=0 FAIL=1"
    exit 1
}

# 剥掉**整行注释**（只留代码；行内 `//` 之后也剥掉）—— 防止"注释里写一句"把锁喂绿。
function Strip-Comments([string]$text) {
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($line in ($text -split "`r?`n")) {
        $t = $line
        if ($t.TrimStart().StartsWith('//')) { $out.Add(''); continue }
        $ci = $t.IndexOf('//')
        if ($ci -ge 0) { $t = $t.Substring(0, $ci) }
        $out.Add($t)
    }
    return ($out -join "`n")
}

$csCode   = Strip-Comments ([System.IO.File]::ReadAllText($csPath))
$xamlCode = Strip-Comments ([System.IO.File]::ReadAllText($xamlPath))

# ── ① 枚举 XAML 里所有 <ComboBox x:Name="...Combo"> ──
$xamlCombos = @()
foreach ($m in [regex]::Matches($xamlCode, '<ComboBox\s+x:Name="(?<n>[A-Za-z0-9_]+)"')) {
    $xamlCombos += $m.Groups['n'].Value
}
$xamlCombos = @($xamlCombos | Sort-Object -Unique)

# ── ② 已知例外白名单（必须显式登记 + 写明理由）──
#   每一项 = 「该 combo 不需要 (a)/(b)」的**具名理由**，不允许静默跳过。
#   ⚠ 2026-10-24 两图 GainMap 面板的**唯一**例外：`GainMapBaseGamutCombo` 的值在「加入队列」那一刻
#     才被读进 FfmpegOptions（面板没有命令预览，也没有任何随参数即时变化的 UI），
#     所以"变更响应"这一格**在设计上不存在**；写一个空处理器只会把锁喂绿。
#     它的"值真的生效"由无头 UI 冒烟 U9b（逐项读回入队项的 FfmpegOptions）看着。
#     编码器 大类/小类、格式下拉、质量滑块都走真实处理器（前者联动候选集，后两者联动启用态/数值框），
#     所以**不在**白名单里。
$exempt = @{
  'GainMapBaseGamutCombo' = 'add-time input (FfmpegOptions.JpegGainMapBaseGamut); effect covered by headless UI smoke U9b'
}

Write-Output ("  enumerated ComboBox x:Name in XAML: {0}" -f $xamlCombos.Count)

# ── ③ 反控：白名单外必须 ≥ 8 个（本仓实测基线；掉了说明正则/提取失效 ⇒ 判据在空跑）──
$judged = @($xamlCombos | Where-Object { -not $exempt.ContainsKey($_) })
CK ($judged.Count -ge 8) ("judged combo count >= 8 (actual {0}) -- guards against the extractor silently matching nothing" -f $judged.Count)

# ── ④ 逐控件判 (a) 初始选中 + (b) 变更响应 ──
#   ⚠⚠ 判据必须接受本仓**三条**合法接线路径（2026-10-04 实测定稿 —— 本锁初版只认 .cs 直赋，
#     把 21 个**完全合法**的控件判红，那是**过严的假红**；"不得为绿而放宽"的另一面是
#     **也不得把正确写法判红**）：
#     ① **.cs 直赋**：`X.SelectedIndex = <n>`（如 `FormatCombo` 在 :536 补设）；处理器 `X.SelectionChanged +=`
#     ② **本地化下拉**：`FillComboByLoc(..., "XCombo", ...)` —— 其实现内 `combo.SelectedIndex = idx`
#        （该函数自陈「首次填充时 ItemsSource 为空 ⇒ XAML 里写的 SelectedIndex 已被重置为 -1，
#          必须显式给默认索引」）⇒ 等价于初始化。
#     ③ **XAML 声明**：`<ComboBox x:Name="XCombo" SelectedIndex="0" SelectionChanged="X_SelectionChanged">`
#        —— 本仓大量控件走这条（如 `FormatCombo` / `ConversionModeCombo`）。
#   ⇒ 三个来源**任一命中**即算接线。
foreach ($name in $judged) {
    $q = [regex]::Escape($name)
    $initCs   = [regex]::IsMatch($csCode,   $q + '\.SelectedIndex\s*=')
    $initLoc  = [regex]::IsMatch($csCode,   'FillComboByLoc\s*\([^;]{0,220}?' + $q)
    $initXaml = [regex]::IsMatch($xamlCode, '<ComboBox\s+[^>]*x:Name="' + $q + '"[^>]*SelectedIndex\s*=')
    $handCs   = [regex]::IsMatch($csCode,   $q + '\.SelectionChanged\s*\+=')
    $handXaml = [regex]::IsMatch($xamlCode, '<ComboBox\s+[^>]*x:Name="' + $q + '"[^>]*SelectionChanged\s*=')
    $hasInit = $initCs -or $initLoc -or $initXaml
    $hasHand = $handCs -or $handXaml
    $viaI = $(if ($initCs) { 'cs' } elseif ($initLoc) { 'FillComboByLoc' } elseif ($initXaml) { 'xaml' } else { 'NONE' })
    $viaH = $(if ($handCs) { 'cs' } elseif ($handXaml) { 'xaml' } else { 'NONE' })
    CK $hasInit  ("{0}: initial selection set (via {1}) -- else SelectedItem stays null and the control is inert" -f $name, $viaI)
    CK $hasHand  ("{0}: SelectionChanged handler attached (via {1}) -- else changes never take effect" -f $name, $viaH)
}

# ── ⑤ 点名锁（本缺陷的**直接**回归锁）：`RawColorTargetCombo` 必须两个都在 ──
#   即使将来有人"顺手"把它加进白名单，这一条也会继续盯着它。
CK ([regex]::IsMatch($csCode, 'RawColorTargetCombo\.SelectedIndex\s*=')) `
   "RawColorTargetCombo: SelectedIndex initialised (P0 regression lock, 2026-10-04)"
CK ([regex]::IsMatch($csCode, 'RawColorTargetCombo\.SelectionChanged\s*\+=')) `
   "RawColorTargetCombo: SelectionChanged attached (P0 regression lock, 2026-10-04)"

# ── ⑥ D-1 extension lock (2026-10-12): the "late, no-format-change" refreshes must be protected ──
#   `UpdateOptionAvailability()` resets colour-space / bit-depth to index 0 and quality to the
#   format default (jpg = 92). That is correct when the USER changes the format, but two refreshes
#   fire with **no** format change: (a) the UI refresh posted at the tail of `FullDetectionAsync`,
#   which is enqueued AFTER the "[detect] done" line, and (b) `InitializeCapabilitiesAsync` right
#   after its await. Both silently destroy picks the user made in that window -- seen in the wild as
#   GroupO O2/O3 reddening intermittently (same binary, red then green seconds apart) with the
#   reading auto/auto/92. They must now call `RefreshOptionsKeepingTargets()`.
#   Why a source-shape lock at all: the behavioural judge (GroupO O4) invokes the protecting method
#   directly, so rewiring one of these two sites back to the raw call keeps O4 GREEN -- only this
#   lock sees the wiring. Teeth proven by mutation (MUT-W1 / MUT-W2 in docs/IMPLEMENT_PLAN_2026-10-11.md).
$wrapCalls = ([regex]::Matches($csCode, 'RefreshOptionsKeepingTargets\(\);')).Count
CK ($wrapCalls -eq 3) ("D-1 extension: RefreshOptionsKeepingTargets call sites == 3 (detect-tail post + capability init + HdrMode handler), actual $wrapCalls")
$capInit = ([regex]::Matches($csCode, '(?s)private async Task InitializeCapabilitiesAsync\(\).{0,420}?RefreshOptionsKeepingTargets\(\);')).Count
CK ($capInit -eq 1) "D-1 extension: InitializeCapabilitiesAsync body goes through the protecting wrapper (matched $capInit, expect 1)"
$capRaw = ([regex]::Matches($csCode, '(?s)private async Task InitializeCapabilitiesAsync\(\).{0,420}?UpdateOptionAvailability\(\);')).Count
CK ($capRaw -eq 0) "D-1 extension: InitializeCapabilitiesAsync must NOT call the raw reset refresh (matched $capRaw, expect 0)"
$detPost = ([regex]::Matches($csCode, '(?s)Dispatcher\.UIThread\.Post\(\(\) =>.{0,260}?RefreshOptionsKeepingTargets\(\);')).Count
CK ($detPost -ge 1) "D-1 extension: the detect-tail Dispatcher.UIThread.Post carries the protecting wrapper (matched $detPost, expect >= 1)"
#   反向对账（防"顺手把所有刷新都包起来"）：**换格式**的那几处必须仍然裸调，否则「切格式 ⇒ 重建候选集」
#   的既有语义被悄悄改掉。`FormatCombo_SelectionChanged` 是其中之一。
$fmtRaw = ([regex]::Matches($csCode, '(?s)private void FormatCombo_SelectionChanged\(.{0,900}?UpdateOptionAvailability\(\);')).Count
CK ($fmtRaw -ge 1) "scope guard: FormatCombo_SelectionChanged still calls the raw refresh (format change => reset is intended), matched $fmtRaw"

Write-Output ("PASS={0} FAIL={1}" -f $script:pass, $script:fail)
if ($script:fail -gt 0) { exit 1 } else { exit 0 }
