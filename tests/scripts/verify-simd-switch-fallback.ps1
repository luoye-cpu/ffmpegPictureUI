# verify-simd-switch-fallback.ps1 —— 面板「SIMD 优化」复选框**真有消费者**的门禁（2026-09-27 新增）
#
# 立锁动机（已核实的缺陷，不是臆测）：
#   `AppSettings.AutoUseSimdBinaries` 由复选框写入并持久化（MainWindow.xaml.cs 的 IsCheckedChanged
#   → AppSettingsService.Save()），启动时回填；zh/en 两条 `tip.simd` 都在**承诺**一个回退
#   （中文「关闭后逐像素回退，仅用于排查」/ 英文「turning it off falls back to scalar code」）。
#   但修复前**全仓没有任何代码读它决定走不走 SIMD**：
#     · `PsnrCalculator.SquaredDiffSum` 无条件按 Avx512BW → Avx2 → Sse2 选路；
#     · `SimdPixelOps.FloatToSrgb8` 无条件看 `Avx2.IsSupported`。
#   ⇒ 声明与实现冲突。本仓铁律「降级不是修复」：把实现提到声明的水位
#     （判据单点 = `SimdKernelRouting`），而不是把 tooltip 改弱。
#
# 四段判据（① ② 是行为，③ ④ 是结构；行为优先，结构只补「判据只此一份」这一条）：
#   ① 行为：断言本体在 `ServiceProbe simdswitch`（进程内、确定性合成像素，不依赖素材/外部 exe/
#      特定硬件，详见该 mode 的头注释 S0..S6）。本脚本只编排 + 汇总，并**要求它真的跑够了条数**
#      （下限断言 pass>=25，防"mode 整段没跑 ⇒ exit 0 的恒绿"）。
#   ② 配置 → 内核这一跳的端到端：同一个 exe 用 `FFMPEGGUI_AUTO_SIMD=false` / `=true` 各跑一次，
#      两次的 `startup-flag` 必须等于 env 给的值、`startup-kernel` 必须分别是 scalar / 非 scalar，
#      且两次的选路**必须不同**。
#      ⚠ **这一条在修复前必红**（旧代码无视配置，两次都会打印同一条 SIMD 路）⇒ 就是本锁的牙；
#        「标量分支只是写着没被调用」「开关读错对象」同样当场红。
#   ③ 判据只此一份（结构）：`src/FfmpegGui` 里除 `CpuFeatureService.cs`（纯能力上报，不选路）外，
#      `.IsSupported` 只能出现在 `SimdKernelRouting.cs`；且读 `AutoUseSimdBinaries` 的文件集合
#      扣除「声明 / UI 写入 / 环境变量覆盖 / 落盘克隆」四类白名单后**必须恰为** `SimdKernelRouting.cs`
#      —— **空集合也判红**（消费者被删掉时它不该看起来"干净"）。
#   ④ 站点确实接在这份判据上（结构，防"写好但没被调用"）：`PsnrCalculator` 必须经
#      `SimdKernelRouting.ResolvePixelKernelPath()`，`SimdPixelOps` 必须经
#      `SimdKernelRouting.FloatToSrgb8UsesSimd`，且 `PsnrCalculator.cs` 里四个内核的调用点
#      **恰好 4 处**（= 分派表只有一张，生产与测试钩子共用；变 8 处即红）。
#
# ⚠ 未接进 `_run-step3-gates.ps1` 的清单（条数与运行器指纹由主循环统一改）。
# ⚠ 运行级 GUID 隔离 + 绿时自清（本仓并发约束：同一脚本不得有两个并发实例互相踩读数）。
#
# ── 本锁的牙（哪些输入会让它红）────────────────────────────────────────────
#   静态层 ③④ 的判据**已在 2026-09-27 用内存变异体实跑证伪过**（不落盘、不改源文件）：
#     M1 把三级 `.IsSupported` 判据抄回 `SquaredDiffSum`（= 修复前的形态）
#        ⇒ PsnrCalculator 的 .IsSupported 由 0 → **3** ⇒ ③a 红；内核调用点由 4 → **8** ⇒ ④c 红。
#     M2 把 `SimdPixelOps` 的判据换成直读 `Avx2.IsSupported`（= 旁路开关）
#        ⇒ 该文件 .IsSupported 由 0 → **1** ⇒ ③a 红；且 ④b 命中变 False ⇒ ④b 红。
#     M3 删掉消费端那一行读取（`UserAllowsSimd => true`，= 回到「有声明无实现」）
#        ⇒ 消费端集合由 {SimdKernelRouting.cs} → **空** ⇒ ③d 红（空集合也判红，不给「干净」）。
#   行为层 S4 / S6e（同一个探针内的错位负控）与 ②a..②e（env 两跑）**尚未实跑**
#   ⇒ 由主循环构建后跑一次本脚本确认；②c 在修复前的代码上**必然红**（旧代码两次都选 SIMD 路）。
#
# 用法：pwsh -NoProfile -File tests/scripts/verify-simd-switch-fallback.ps1
# 退出码：0 = 全绿；1 = 有失败。**自带 PASS=/FAIL=/SKIP= 汇总行**（运行器按它分类，零断言会被判红）。
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root

$pass = 0; $fail = 0; $skip = 0
function Say($t) { Write-Output $t }
function CK($ok, $msg) {
    if ($ok) { $script:pass++; Say "  PASS $msg" }
    else { $script:fail++; Say "  FAIL $msg" }
}
function Grab($text, $key) {
    $m = [regex]::Match($text, [regex]::Escape($key) + '=(\S+)')
    if (-not $m.Success) { return '' }
    return $m.Groups[1].Value
}

$runDir = Join-Path $root 'tests/output/_simd-switch-fallback'
$null = New-Item -ItemType Directory -Force -Path $runDir

$pr = "$root/tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
$fellBack = $false
if (-not (Test-Path $pr)) {
    $pr = "$root/tests/ServiceProbe/bin/Debug/net11.0/win-x64/ServiceProbe.exe"; $fellBack = $true
}
Say ('[gate] exe=' + $pr + $(if ($fellBack) { ' (Debug fallback)' } else { ' (Release)' }))
if (-not (Test-Path $pr)) {
    CK $false '前置：ServiceProbe exe 在（缺 ⇒ 先构建 tests/ServiceProbe，不静默给绿）'
} else {
    CK $true '前置：ServiceProbe exe 在（路径见上一行）'
}

# ── 跑一次探针：$envVal 决定「SIMD 优化」这一位的配置来源（'' = 不设，走 settings.json/默认） ──
function Run-Probe([string]$tag, [string]$envVal) {
    $so = Join-Path $runDir ($tag + '.out'); $se = Join-Path $runDir ($tag + '.err')
    $prev = $env:FFMPEGGUI_AUTO_SIMD
    if ($envVal -ne '') { $env:FFMPEGGUI_AUTO_SIMD = $envVal }
    $code = $null; $text = ''; $startErr = ''
    try {
        $p = Start-Process -FilePath $pr -ArgumentList @('simdswitch') -Wait -NoNewWindow -PassThru `
             -RedirectStandardOutput $so -RedirectStandardError $se
        $code = $p.ExitCode
        try { $text = [System.IO.File]::ReadAllText($so) + "`n" + [System.IO.File]::ReadAllText($se) } catch { }
    } catch {
        # ⚠ 这里**不能** Say/Write-Output：函数里落到成功流的东西会变成返回值的一部分（⇒ $off.text 不再是字符串）。
        $startErr = $_.Exception.Message
    } finally {
        if ($null -ne $prev) { $env:FFMPEGGUI_AUTO_SIMD = $prev }
        else { Remove-Item Env:FFMPEGGUI_AUTO_SIMD -ErrorAction SilentlyContinue }
        Remove-Item -LiteralPath $so, $se -Force -ErrorAction SilentlyContinue
    }
    $m = [regex]::Match($text, 'PROBE RESULT: pass=(\d+) fail=(\d+)(?: skip=(\d+))?')
    $np = 0; $nf = 0; $ns = 0; $has = $m.Success
    if ($has) {
        $np = [int]$m.Groups[1].Value; $nf = [int]$m.Groups[2].Value
        if ($m.Groups[3].Success) { $ns = [int]$m.Groups[3].Value }
    }
    return [pscustomobject]@{ code = $code; text = $text; hasSummary = $has; p = $np; f = $nf; s = $ns; startErr = $startErr }
}

# ── ① + ② 行为锁 ──
Say ''
Say '── ① 行为：ServiceProbe simdswitch（关）──'
$off = Run-Probe 'off' 'false'
if ($off.startErr -ne '') { Say ('  | 探针进程没起来：' + $off.startErr + '（下面那条必红，不给绿）') }
foreach ($line in ($off.text -split "`r?`n")) { if ($line -ne '') { Say ('  | ' + $line) } }
$msgOff = '① 行为锁全绿且跑够条数（pass=' + $off.p + ' fail=' + $off.f + ' skip=' + $off.s + ' exit=' + $off.code + '，下限 pass>=25）'
CK ($off.hasSummary -and $off.f -eq 0 -and $off.code -eq 0 -and $off.p -ge 25) $msgOff
CK ($off.text -notmatch 'unknown command') '① 该 mode 确实在被测 dll 里（不是 unknown command ⇒ 防陈旧产物假绿）'
$skip += $off.s

Say ''
Say '── ① 行为：ServiceProbe simdswitch（开）──'
$on = Run-Probe 'on' 'true'
if ($on.startErr -ne '') { Say ('  | 探针进程没起来：' + $on.startErr + '（下面那条必红，不给绿）') }
$msgOn = '①b 开关=开那一跑同样全绿（pass=' + $on.p + ' fail=' + $on.f + ' skip=' + $on.s + ' exit=' + $on.code + '）⇒ 默认路径没因加判据而变坏'
CK ($on.hasSummary -and $on.f -eq 0 -and $on.code -eq 0 -and $on.p -ge 25) $msgOn
$skip += $on.s

Say ''
Say '── ② 配置（FFMPEGGUI_AUTO_SIMD）→ 选路 的端到端 ──'
$flagOff = Grab $off.text 'startup-flag'; $kernOff = Grab $off.text 'startup-kernel'
$flagOn  = Grab $on.text  'startup-flag'; $kernOn  = Grab $on.text  'startup-kernel'
$sse2    = Grab $off.text 'isa-sse2'
Say ('  读数：关⇒flag=' + $flagOff + ' kernel=' + $kernOff + ' ／ 开⇒flag=' + $flagOn + ' kernel=' + $kernOn + ' ／ isa-sse2=' + $sse2)
CK ($flagOff -eq 'False') ('②a 配置 False 真的落到 AutoUseSimdBinaries 上（实 startup-flag=' + $flagOff + '）')
CK ($flagOn -eq 'True') ('②b 配置 True 真的落到 AutoUseSimdBinaries 上（实 startup-flag=' + $flagOn + '）')
CK ($kernOff -eq 'scalar') ('②c 开关关 ⇒ 选路必须是 scalar（实 ' + $kernOff + '）⇒ 修复前这里必红')
if ($sse2 -eq 'True') {
    CK ($kernOn -ne '' -and $kernOn -ne 'scalar') ('②d 开关开且本机有 SSE2 ⇒ 选路必须非 scalar（实 ' + $kernOn + '）⇒ 回退不得反向吃掉默认路')
    CK ($kernOn -ne $kernOff) ('②e 两态选路必须不同（开=' + $kernOn + ' 关=' + $kernOff + '）⇒ 否则它仍是空开关')
} else {
    Say ('  SKIP ②d/②e 本机判不了（isa-sse2=' + $sse2 + ' ⇒ 没有第二条路可比）；不判红、也不当已验')
    $skip += 2
}

# ── ③④ 结构锁 ──
Say ''
Say '── ③④ 结构：判据单点 + 生产站点接线 ──'
# 剥注释：整行 // 与 /* */（含跨行）。已知局限与 _probe-cjk-hardcode-scan 同款（按文本级剥）。
function Get-CodeLines([string]$path) {
    $out = New-Object System.Collections.Generic.List[string]
    $inBlock = $false
    foreach ($l in (Get-Content -LiteralPath $path)) {
        $t = $l
        if ($inBlock) {
            $e = $t.IndexOf('*/', [StringComparison]::Ordinal)
            if ($e -lt 0) { continue }
            $t = $t.Substring($e + 2); $inBlock = $false
        }
        $b = $t.IndexOf('/*', [StringComparison]::Ordinal)
        while ($b -ge 0) {
            $e = $t.IndexOf('*/', $b + 2, [StringComparison]::Ordinal)
            if ($e -lt 0) { $t = $t.Substring(0, $b); $inBlock = $true; break }
            $t = $t.Substring(0, $b) + $t.Substring($e + 2)
            $b = $t.IndexOf('/*', [StringComparison]::Ordinal)
        }
        if ($t.TrimStart().StartsWith('//', [StringComparison]::Ordinal)) { continue }
        if ($t.Trim().Length -gt 0) { $out.Add($t) }
    }
    return $out
}
$srcFiles = @(Get-ChildItem -Path "$root/src/FfmpegGui" -Recurse -Filter *.cs |
              Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
CK ($srcFiles.Count -ge 40) ('③ 下限：扫到的 src/FfmpegGui/*.cs = ' + $srcFiles.Count + ' 个 ⇒ 否则本段是在空扫')

$isaFiles = New-Object System.Collections.Generic.List[string]
$isaTotal = 0
$routingHits = 0
foreach ($f in $srcFiles) {
    $hits = @(Get-CodeLines $f.FullName | Where-Object { $_ -match '\.IsSupported\b' })
    if ($hits.Count -eq 0) { continue }
    $isaTotal += $hits.Count
    $isaFiles.Add($f.Name + ':' + $hits.Count)
    if ($f.Name -eq 'SimdKernelRouting.cs') { $routingHits = $hits.Count }
}
Say ('  读数：.IsSupported 命中（剥注释后）共 ' + $isaTotal + ' 处，分布于 ' + $(if ($isaFiles.Count) { $isaFiles -join ', ' } else { '(0)' }))
$offenders = @($isaFiles | Where-Object { ($_ -split ':')[0] -ne 'SimdKernelRouting.cs' -and ($_ -split ':')[0] -ne 'CpuFeatureService.cs' })
CK ($offenders.Count -eq 0) ('③a 选路判据只此一份：除 SimdKernelRouting.cs 与 CpuFeatureService.cs（能力上报，不选路）外没有第二处 .IsSupported（实命中 ' + $(if ($offenders.Count) { $offenders -join ', ' } else { '无' }) + '）')
CK ($routingHits -ge 4) ('③c 单点里真有 ISA 优先级链：SimdKernelRouting.cs 的 .IsSupported >= 4（三级链 + FloatToSrgb8 那条，实得 ' + $routingHits + '）⇒ 否则是空壳')

# 读 AutoUseSimdBinaries 的文件集合，扣除四类白名单后必须恰为 SimdKernelRouting.cs（空也判红）
$whitelist = @('AppSettings.cs', 'AppSettingsService.cs', 'MainWindow.xaml.cs')
$readers = New-Object System.Collections.Generic.List[string]
foreach ($f in $srcFiles) {
    $hit = @(Get-CodeLines $f.FullName | Where-Object { $_ -match 'AutoUseSimdBinaries' })
    if ($hit.Count -gt 0) { $readers.Add($f.Name) }
}
$consumers = @($readers | Where-Object { $whitelist -notcontains $_ } | Sort-Object -Unique)
Say ('  读数：出现 AutoUseSimdBinaries 的文件 = ' + $(if ($readers.Count) { (($readers | Sort-Object -Unique) -join ', ') } else { '(0)' }))
Say ('  读数：扣掉声明/UI/持久化白名单后的消费端 = ' + $(if ($consumers.Count) { $consumers -join ', ' } else { '(空) ⇒ 消费者不存在' }))
CK ($consumers.Count -eq 1 -and $consumers[0] -eq 'SimdKernelRouting.cs') ('③d 该开关的消费端恰为 SimdKernelRouting.cs（实 ' + $(if ($consumers.Count) { $consumers -join ', ' } else { '空 ⇒ 又被删了' }) + '）')

$psnrPath = Join-Path $root 'src/FfmpegGui/Services/PsnrCalculator.cs'
$srgbPath = Join-Path $root 'src/FfmpegGui/Services/SimdPixelOps.cs'
$both = ((Test-Path -LiteralPath $psnrPath) -and (Test-Path -LiteralPath $srgbPath))
CK $both '④ 前置：两个内核文件都在（不在 ⇒ 上面③已判红，这里再记一次以免被当成"没跑"）'
if ($both) {
    $psnrTxt = Get-Content -LiteralPath $psnrPath -Raw
    $srgbTxt = Get-Content -LiteralPath $srgbPath -Raw
    CK ($psnrTxt -match 'SimdKernelRouting\.ResolvePixelKernelPath') '④a SquaredDiffSum 确实经单点判据取路（PsnrCalculator 调了 ResolvePixelKernelPath）'
    CK ($srgbTxt -match 'SimdKernelRouting\.FloatToSrgb8UsesSimd') '④b FloatToSrgb8 确实经同一判据（SimdPixelOps 调了 FloatToSrgb8UsesSimd）'
    $kernelCalls = [regex]::Matches($psnrTxt, 'SquaredDiffSum(?:Avx512|Avx2|Sse2|Scalar)\(a, b\)').Count
    CK ($kernelCalls -eq 4) ('④c 内核分派表只有一张：四个内核的调用点恰好 4 处（实得 ' + $kernelCalls + ' ⇒ 8 处就是钩子与生产各写了一遍）')
}

Say ''
Say ('===== simd-switch-fallback: PASS=' + $pass + ' FAIL=' + $fail + ' SKIP=' + $skip + ' =====')
Remove-Item -LiteralPath $runDir -Recurse -Force -ErrorAction SilentlyContinue
if ($fail -gt 0) { exit 1 }
exit 0
