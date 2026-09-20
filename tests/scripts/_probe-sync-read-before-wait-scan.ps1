# _probe-sync-read-before-wait-scan.ps1 —— 结构锁：进程启动站点不得「同步 ReadToEnd 排在 WaitForExit 之前」
#
# 为什么需要（缺陷形态，2026-09-18 实测）：
#   `QueueProcessor.IsAnimatedJxl` 里把**同步阻塞读**写在 `WaitForExit` **之前**：
#       `var output = proc.StandardOutput.ReadToEnd().Trim(); proc.WaitForExit(30000);`
#   ⇒ 子进程不关管道就永不返回 ⇒ **超时是死代码**，超时后的 `Kill` 也不可达。
#   实测：1 字节畸形 `.jxl` 让整个编码队列**永久挂死**（该处已修；进程级验证另有专项门禁
#   `verify-jxl-probe-timeout.ps1`）。
#
#   ⚠ **为什么还要另立这一条**（现有锁的扫描面缺口）：
#     · `_probe-cancel-propagation-scan.ps1` 的扫描面**只有** `FfmpegRunner.RunAsync` 调用点；
#     · `_probe-stderr-drain-scan.ps1` 管的是「stderr 有没有人排空」；
#     · `verify-jxl-probe-timeout.ps1` 只覆盖**两个点名站点**（站点 1 / 站点 2）的进程级行为。
#     ⇒ **没有任何一条锁**管「全仓直连进程启动站点里，同步读是否架空了超时」。本锁补这一格。
#
# 判据（**只有一条**，刻意不扩）：
#   在**同一个进程变量**上，「同步 `ReadToEnd()`」的位置不得**早于**「`WaitForExit(...)`」。
#   · 只认 `StandardOutput.ReadToEnd()` / `StandardError.ReadToEnd()`（**同步**重载）。
#     `ReadToEndAsync()` **不**参与判据 —— 异步读不会阻塞等待线程。
#   · 配对限定在**同一方法体内**、**同一变量名**上，用 `\b` 词边界 ⇒ `p` 不会与 `wp`/`proc`/`p2` 互串。
#     ⚠ 这一条是必需的，不能退化成「方法内第一次读 vs 第一次等待」：`ExifToolService.TryFindInPath`
#       里 `p.WaitForExit(5000)`（:287）**早于** `wp.StandardOutput.ReadToEnd()`（:306）
#       ⇒ 取「方法内第一次」会漏掉 `wp` 那一处（假绿）。负控 `twoprocs.cs` 专门钉住这一点。
#   · ⚠ 判据只认**代码**、**不认注释**：`IsAnimatedJxl` 的注释里**正逐字引用**了旧代码的
#     `StandardOutput.ReadToEnd()` 与 `WaitForExit(30000)`，且文本顺序恰好是「读在前」
#     ⇒ 不剥注释会把它判成**假红**。负控 `comment.cs` / `trailing.cs` 钉住这一点。
#   · ⚠ **本锁不判**另外三条属性（超时后是否 Kill / 是否杀进程树 / 有无取消通道 / 失败是否可见）。
#     它们**不能**由一条源码扫描同时判定；硬塞进来只会让断言变糊（`docs/TESTING.md §6` 第 74 条）。
#
# 覆盖面的**两个已知缺口**（本锁措辞不得大于扫描面，`§6` 第 87 条）：
#   ① 若等待被抽到 helper，方法体内就没有 `x.WaitForExit(` ⇒ 该站点判据**落空**，本锁**不表态**。
#      实测实例：`CjxlService.cs:226` 的 `process.Start()` 之后直接
#      `return await WaitGuardedAsync(process, ct, logCallback);`（30 分钟守卫在 helper 里）。
#   ② 本锁只查**源码形态**，不运行任何 exe ⇒ 它**不能**证明任何一处「会挂死」，
#      只能证明「**具备该形态的站点数没有增加**」。
#
# 基线口径：**不得增加**，不是「必须为 0」（`§6` 第 55 条同族的教训：长期红的锁会被无视）。
#   2026-09-18 只读审计实测 **12** 处 ⇒ 断言 `实测 <= 基线`。这 12 处是**已知债**，正在由 #24 修。
#   ⚠ 2026-09-18（#29）删除 2 份**仓内不可达**的残留实现（`CjpegliService.TryFindInPath` /
#     `DjxlService.TryFindInPath`，零调用方，见 HANDOVER ⑨）⇒ 该形态站点降为 **10** 处，
#     基线同步收紧 **12 → 10**（否则基线里会留 2 处永远修不到的幽灵债务、余量恒 +2）。
#   ⚠ 2026-09-18（#24 收尾）这 10 处**已全部修好**（照 `FfmpegCommandBuilder.ProbeWithFfprobe`
#     的四步范式：先挂异步读 → `WaitForExit(n)` → 超时 `Kill(entireProcessTree: true)` → 点名日志）
#     ⇒ 基线同步收紧 **10 → 0**。**「不得增加」在 0 处等价于「必须为 0」**：这 10 处是已修好的
#     真实站点（不是长期红的债），一旦回归必须立刻判红。
#   降了**不判红**，但会打印**余量**让进度可见；余量长期为正时提示收紧基线。
#   ⚠ 两类站点**分开统计、分开打印**（口径不同）：
#     · A 类 = 直连 `Process.Start(`（实测 63 处，`#29` 前为 65）
#     · B 类 = 实例化后 `.Start()`，即 `new Process { ... }` + `<var>.Start()`（实测 7 处）
#     B 类实测 **0** 处命中判据 ⇒ 基线 0，等价于「必须为 0」（它本来就绿，不是长期红）。
#
# 用法：pwsh -NoProfile -File tests/scripts/_probe-sync-read-before-wait-scan.ps1（非 0 退出 = 判据被违反）
#      报告落 tests/output/_probe-sync-read-before-wait.txt（可 -Report 覆盖）
param([string]$Report = '')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Report) { $Report = Join-Path $root 'tests/output/_probe-sync-read-before-wait.txt' }

# 基线（**不得增加**）：A 类 **0** 处（`#24` 已把 10 处全部修好；原 12 → `#29` 收紧为 10 → `#24` 收尾收紧为 0）；B 类 0 处。
$BaseDirect = 0
$BaseInstance = 0

$lines = New-Object System.Collections.Generic.List[string]
function Say([string]$t) { Write-Output $t; $script:lines.Add($t) }

# ── 工具：剥注释（整行 `//`、行尾 `//`、`/* */` 块）──────────────────────────────
# 行尾 `//` 只在「该行 `//` 之前的双引号个数为偶数」时才算注释（避免把串内的 `//` 当注释）。
function Get-CodeText([string]$text) {
    $t = [regex]::Replace($text, '/\*[\s\S]*?\*/', '')
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($ln in ($t -split "`r?`n")) {
        if ($ln.TrimStart().StartsWith('//')) { $out.Add(''); continue }
        $q = $ln.IndexOf('//')
        if ($q -ge 0) {
            $head = $ln.Substring(0, $q)
            $nq = ([regex]::Matches($head, '"')).Count
            if (($nq % 2) -eq 0) { $ln = $head }
        }
        $out.Add($ln)
    }
    return ($out -join "`n")
}

# ── 工具：方法起点行号（与 ⑧ 结构锁 / `_probe-stderr-drain-scan.ps1` 同一套定位法）──
function Get-MethodStarts([string[]]$L) {
    $res = @()
    for ($i = 0; $i -lt $L.Count; $i++) {
        if ($L[$i] -match '^\s{2,}(?:private|public|internal|protected)[^=;]*\(' -and $L[$i] -notmatch '=>') { $res += $i }
    }
    return $res
}

function Get-MethodNameAt([string[]]$L, [int]$lineIdx, [int[]]$starts) {
    $name = '<顶层>'
    for ($k = 0; $k -lt $starts.Count; $k++) {
        if ($starts[$k] -gt $lineIdx) { break }
        $t = $L[$starts[$k]].Trim()
        $t = [regex]::Replace($t, '<[^<>]*>', '')
        $t = $t -replace '\(.*$', ''
        $name = ($t.Trim() -split '\s+')[-1]
    }
    return $name
}

function Get-MethodEndLine([int]$lineIdx, [int[]]$starts, [int]$lineCount) {
    $to = $lineCount - 1
    for ($k = 0; $k -lt $starts.Count; $k++) {
        if ($starts[$k] -gt $lineIdx) { $to = $starts[$k] - 1; break }
    }
    return $to
}

# ── 判据本体：同一变量上，同步 ReadToEnd 是否早于 WaitForExit ──────────────────────
function Test-Site([string]$body, [string]$varName) {
    $v = [regex]::Escape($varName)
    $readRe = '\b' + $v + '\s*\.\s*Standard(?:Output|Error)\s*\.\s*ReadToEnd\s*\(\s*\)'
    $waitRe = '\b' + $v + '\s*\.\s*WaitForExit\s*\('
    $waitAsyncRe = '\b' + $v + '\s*\.\s*WaitForExitAsync\s*\('
    $mRead = [regex]::Match($body, $readRe)
    $mWait = [regex]::Match($body, $waitRe)
    $mWaitAsync = [regex]::Match($body, $waitAsyncRe)
    $rIdx = -1; $wIdx = -1; $waIdx = -1
    if ($mRead.Success) { $rIdx = $mRead.Index }
    if ($mWait.Success) { $wIdx = $mWait.Index }
    if ($mWaitAsync.Success) { $waIdx = $mWaitAsync.Index }
    $bad = ($rIdx -ge 0) -and ($wIdx -ge 0) -and ($rIdx -lt $wIdx)
    # 观察项（**不判红**）：同步读早于 `WaitForExitAsync` —— 同一族形态，但本锁判据只取
    # `WaitForExit(...)`（team-lead 裁定「判据只取一条」）。单独计数并打印，不参与 PASS/FAIL。
    $obs = ($rIdx -ge 0) -and ($waIdx -ge 0) -and ($rIdx -lt $waIdx) -and (-not $bad)
    return @{ bad = $bad; obs = $obs; rIdx = $rIdx; wIdx = $wIdx }
}

# ── 扫描一个语料目录 ────────────────────────────────────────────────────────────
function Scan-Corpus([string]$dir, [string]$label) {
    $acc = @{ direct = 0; instance = 0; novar = 0
              badDirect = @(); badInstance = @(); obsDirect = 0; obsInstance = 0; files = 0 }
    # ⚠ 必须排除 `bin/` `obj/`（与 `_probe-stderr-drain-scan.ps1` / `_probe-cancel-propagation-scan.ps1`
    #   同口径）：实测 `src/FfmpegGui` 下共 84 个 .cs，其中 **6 个是生成物**（obj 里的 AssemblyInfo 等）。
    #   不过滤会把生成物算进扫描面 ⇒ 「扫描文件数」这个读数就不诚实了（虽然它们不含 `Process.Start(`，
    #   当前不影响命中数 —— 但读数会随构建产物变化而漂移）。
    $files = @(Get-ChildItem -Recurse -Path $dir -Filter *.cs -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
    $acc.files = $files.Count
    foreach ($f in $files) {
        $rel = $f.FullName
        if ($label -ne 'TEMP') { $rel = $f.FullName.Substring($root.Length + 1) }
        $code = Get-CodeText ([System.IO.File]::ReadAllText($f.FullName))
        $L = @($code -split "`n")
        $starts = @(Get-MethodStarts $L)

        # 本文件里「实例化后 .Start()」的变量名（来自 `new Process { ... }`）
        $instVars = @()
        for ($i = 0; $i -lt $L.Count; $i++) {
            foreach ($m in [regex]::Matches($L[$i], '([A-Za-z_]\w*)\s*=\s*new\s+Process\s*\{')) {
                $instVars += $m.Groups[1].Value
            }
        }

        for ($i = 0; $i -lt $L.Count; $i++) {
            $line = $L[$i]
            $hits = @()
            foreach ($m in [regex]::Matches($line, 'Process\.Start\s*\(')) {
                $hits += @{ kind = 'A'; idx = $m.Index; var = '' }
            }
            foreach ($m in [regex]::Matches($line, '([A-Za-z_]\w*)\s*\.\s*Start\s*\(\s*\)')) {
                if ($instVars -contains $m.Groups[1].Value) {
                    $hits += @{ kind = 'B'; idx = $m.Index; var = $m.Groups[1].Value }
                }
            }
            foreach ($h in $hits) {
                if ($h.kind -eq 'A') { $acc.direct++ } else { $acc.instance++ }
                $varName = $h.var
                if ($h.kind -eq 'A') {
                    # 变量名 = 本行 `Process.Start(` 之前最近的 `<name> = `（可能隔着 `System.Diagnostics.`）
                    # ⚠ `=` 右侧的类型路径必须**可空**：`using var p = Process.Start(psi);` 里 `=` 与
                    #   `Process.Start(` 之间是空的（只有空格）——写成必选会让 58/65 个站点取不到变量名。
                    $pre = $line.Substring(0, $h.idx)
                    $vm = [regex]::Match($pre, '([A-Za-z_]\w*)\s*=\s*(?:[A-Za-z_][\w\.]*)?\s*$')
                    if ($vm.Success) { $varName = $vm.Groups[1].Value }
                }
                if (-not $varName) { $acc.novar++; continue }
                $to = Get-MethodEndLine $i $starts $L.Count
                $body = (@($L[$i..$to]) -join "`n")
                $r = Test-Site $body $varName
                $owner = Get-MethodNameAt $L $i $starts
                if ($r.bad) {
                    $item = ('{0}:{1}  {2}  变量={3}' -f $rel, ($i + 1), $owner, $varName)
                    if ($h.kind -eq 'A') { $acc.badDirect += $item } else { $acc.badInstance += $item }
                }
                if ($r.obs) {
                    if ($h.kind -eq 'A') { $acc.obsDirect++ } else { $acc.obsInstance++ }
                }
            }
        }
    }
    return $acc
}

try {
    Say "宿主 PowerShell: $($PSVersionTable.PSVersion)  (Edition=$($PSVersionTable.PSEdition))"
    Say "判据：同一进程变量上，同步 ReadToEnd() 不得早于 WaitForExit(...)"
    Say "基线（**不得增加**）：A 类直连 = $BaseDirect 处（#24 已把 10 处全部修好 ⇒ 在 0 处「不得增加」等价于「必须为 0」）；B 类实例化 = $BaseInstance 处"

    # ── ① 真实语料 ──────────────────────────────────────────────────────────────
    $real = Scan-Corpus "$root/src/FfmpegGui" 'REAL'
    Say ""
    Say "── ① 真实语料 src/FfmpegGui（排除 bin/obj）──"
    Say ("  扫描 .cs 文件：{0} 个" -f $real.files)
    Say ("  A 类 直连 Process.Start( ：{0} 处（参与判据 {1} / 无进程变量跳过 {2}）" -f $real.direct, ($real.direct - $real.novar), $real.novar)
    Say ("  B 类 new Process{{...}} + .Start() ：{0} 处" -f $real.instance)
    Say ("  命中判据（同步读在 WaitForExit 之前）：A 类 {0} 处 / B 类 {1} 处" -f $real.badDirect.Count, $real.badInstance.Count)
    if ($real.badDirect.Count -gt 0) {
        Say "  ── A 类命中明细（基线 0 ⇒ 新增即为回归；本锁只保证不再增加）──"
        $real.badDirect | ForEach-Object { Say "    $_" }
    }
    if ($real.badInstance.Count -gt 0) {
        Say "  ── B 类命中明细（基线 0 ⇒ 新增即为回归）──"
        $real.badInstance | ForEach-Object { Say "    $_" }
    }
    $obsTotal = $real.obsDirect + $real.obsInstance
    Say ("  观察项（**不判红**）：同步读早于 WaitForExitAsync 的站点 {0} 处（A {1} / B {2}）" -f $obsTotal, $real.obsDirect, $real.obsInstance)

    # ── ② 负控（证明判据**有牙**，且不误伤）─────────────────────────────────────
    # ⚠ 语料一律用运行级 GUID 目录（§6 第 66 条：固定目录会被并发同名实例互删 ⇒ 假红）。
    $nc = Join-Path $env:TEMP ('syncwait_' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $ncEmpty = Join-Path $nc 'empty'
    New-Item -ItemType Directory -Path $nc -Force | Out-Null
    New-Item -ItemType Directory -Path $ncEmpty -Force | Out-Null
    $nl = [char]10
    function Write-Corpus([string]$name, [string[]]$bodyLines) {
        $p = Join-Path $nc $name
        [System.IO.File]::WriteAllText($p, ($bodyLines -join $nl), (New-Object System.Text.UTF8Encoding($false)))
    }

    # ②-1 坏形态 ⇒ **必须**被抓到
    Write-Corpus 'bad.cs' @(
        'class T {',
        '    void M() {',
        '        using var p = Process.Start(psi);',
        '        var s = p.StandardOutput.ReadToEnd();',
        '        p.WaitForExit(3000);',
        '    }',
        '}'
    )
    # ②-2 正确形态：异步读在等待之前 ⇒ **不得**误报
    Write-Corpus 'good_async.cs' @(
        'class T {',
        '    void M() {',
        '        using var p = Process.Start(psi);',
        '        var s = p.StandardOutput.ReadToEndAsync();',
        '        p.WaitForExit(3000);',
        '    }',
        '}'
    )
    # ②-3 正确形态：读在等待之后 ⇒ **不得**误报
    Write-Corpus 'good_readafter.cs' @(
        'class T {',
        '    void M() {',
        '        using var p = Process.Start(psi);',
        '        p.WaitForExit(3000);',
        '        var s = p.StandardOutput.ReadToEnd();',
        '    }',
        '}'
    )
    # ②-4 坏形态**只在整行注释里**（`IsAnimatedJxl` 的真陷阱）⇒ **不得**误报
    Write-Corpus 'comment.cs' @(
        'class T {',
        '    void M() {',
        '        // 旧写法：var s = p.StandardOutput.ReadToEnd(); p.WaitForExit(30000);',
        '        using var p = Process.Start(psi);',
        '        p.WaitForExit(3000);',
        '    }',
        '}'
    )
    # ②-5 坏形态**只在行尾注释里** ⇒ **不得**误报（钉住行尾注释剥离）
    Write-Corpus 'trailing.cs' @(
        'class T {',
        '    void M() {',
        '        using var p = Process.Start(psi);',
        '        p.WaitForExit(3000);   // 注意：不要写成 p.StandardOutput.ReadToEnd() 在前',
        '    }',
        '}'
    )
    # ②-6 双进程 + 「第一次等待早于第二次读」⇒ 只许命中 **1** 处（钉住「按变量配对」）
    Write-Corpus 'twoprocs.cs' @(
        'class T {',
        '    bool M() {',
        '        using var p = Process.Start(psi);',
        '        p.BeginErrorReadLine();',
        '        p.WaitForExit(5000);',
        '        if (p.ExitCode == 0) {',
        '            using var wp = Process.Start(whichPsi);',
        '            var output = wp.StandardOutput.ReadToEnd().Trim();',
        '            wp.WaitForExit(5000);',
        '        }',
        '        return false;',
        '    }',
        '}'
    )
    # ②-7 B 类坏形态 ⇒ **必须**被抓到
    Write-Corpus 'instancebad.cs' @(
        'class T {',
        '    void M() {',
        '        using var process = new Process { StartInfo = psi };',
        '        process.Start();',
        '        var s = process.StandardOutput.ReadToEnd();',
        '        process.WaitForExit(3000);',
        '    }',
        '}'
    )
    # ②-8 B 类正确形态（事件驱动读）⇒ **不得**误报；且计时器 `.Start()` **不得**被误算成 B 类
    Write-Corpus 'instancegood.cs' @(
        'class T {',
        '    void M() {',
        '        timer.Start();',
        '        using var process = new Process { StartInfo = psi };',
        '        process.Start();',
        '        process.BeginOutputReadLine();',
        '        process.BeginErrorReadLine();',
        '        process.WaitForExitAsync(ct);',
        '    }',
        '}'
    )

    $bad = Scan-Corpus $nc 'TEMP'
    $emp = Scan-Corpus $ncEmpty 'TEMP'

    Say ""
    Say "── ② 负控（临时语料：$nc）──"
    $ctl = 0; $ctlFail = @()
    function CK-Ctl($ok, [string]$msg) {
        if ($ok) { $script:ctl++ } else { $script:ctlFail += $msg }
        Say ("    {0} {1}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $msg)
    }
    # 有牙：坏形态必须被抓到
    # ⚠ 语料里**有两处**合法命中：`bad.cs`（1）与 `twoprocs.cs` 的 `wp`（1）⇒ 总数应为 2。
    #   「不得误报」的判据因此**不能**写成「总数 == 1」，而要按**文件**分开点算（否则会把
    #   负控自己的合法命中当成误报 —— 本脚本初版就写错过这一条）。
    $hitBad = @($bad.badDirect | Where-Object { $_ -match '[\\/]bad\.cs:' }).Count
    $hitTwo = @($bad.badDirect | Where-Object { $_ -match '[\\/]twoprocs\.cs:' }).Count
    CK-Ctl ($hitBad -eq 1) ("坏形态必须被抓到（bad.cs，实 {0}）" -f $hitBad)
    CK-Ctl ($bad.obsDirect -eq 0) ("坏形态不得落入观察项（实 {0}）" -f $bad.obsDirect)
    CK-Ctl ($bad.badInstance.Count -eq 1) ("B 类坏形态必须被抓到（instancebad.cs，实 {0}）" -f $bad.badInstance.Count)
    # 不误伤：good_async / good_readafter / comment / trailing / instancegood 一处都不许命中
    CK-Ctl ($hitTwo -eq 1) ("twoprocs.cs 只许命中 1 处（wp 那一处；p 的等待在 wp 的读之前 ⇒ 不得连坐）（实 {0}）" -f $hitTwo)
    CK-Ctl ($bad.badDirect.Count -eq 2) `
        ("正确形态（异步读 / 读在等待后 / 整行注释内 / 行尾注释内）**一处都没被误报** —— A 类命中总数 = bad.cs(1) + twoprocs.cs(1) = 2（实 {0}）" -f $bad.badDirect.Count)
    CK-Ctl ($bad.direct -eq 7) ("A 类站点计数正确：bad + good_async + good_readafter + comment + trailing + twoprocs(2) = 7（实 {0}）" -f $bad.direct)
    CK-Ctl ($bad.instance -eq 2) ("B 类站点计数正确：instancebad+instancegood = 2，且计时器 timer.Start() 未被误算（实 {0}）" -f $bad.instance)
    CK-Ctl ($emp.direct -eq 0 -and $emp.instance -eq 0 -and $emp.badDirect.Count -eq 0 -and $emp.badInstance.Count -eq 0) `
        ("空语料 ⇒ 0 站点 / 0 命中（实 站点 {0}+{1} / 命中 {2}+{3}）" -f $emp.direct, $emp.instance, $emp.badDirect.Count, $emp.badInstance.Count)

    # 清理临时语料（用 .NET API 绕过宿主批量删除守卫，§6 第 57/67 条）
    try { if (Test-Path $nc) { [System.IO.Directory]::Delete($nc, $true) } } catch { }

    # ── ③ 断言 ─────────────────────────────────────────────────────────────────
    Say ""
    Say "── ③ 断言（口径：**不得增加**）──"
    $fail = 0
    if ($real.badDirect.Count -gt $BaseDirect) {
        Say ("  FAIL A 类命中 {0} 处 > 基线 {1} 处 ⇒ 新增了「同步读架空超时」的站点" -f $real.badDirect.Count, $BaseDirect)
        $fail++
    } else {
        $head = $BaseDirect - $real.badDirect.Count
        Say ("  PASS A 类命中 {0} 处 ≤ 基线 {1} 处（余量 {2}）" -f $real.badDirect.Count, $BaseDirect, $head)
        if ($head -gt 0) {
            Say ("       提示：余量 {0} 表示已修好 {0} 处 —— 请把基线收紧到 {1}（改本脚本的 `$BaseDirect`），否则这把锁会一直松着" -f $head, $real.badDirect.Count)
        }
    }
    if ($real.badInstance.Count -gt $BaseInstance) {
        Say ("  FAIL B 类命中 {0} 处 > 基线 {1} 处 ⇒ 新增了该形态的站点" -f $real.badInstance.Count, $BaseInstance)
        $fail++
    } else {
        Say ("  PASS B 类命中 {0} 处 ≤ 基线 {1} 处（余量 {2}）" -f $real.badInstance.Count, $BaseInstance, ($BaseInstance - $real.badInstance.Count))
    }
    if ($ctlFail.Count -gt 0) {
        Say ("  FAIL 负控 {0} 条未通过 ⇒ 判据**不可信**（有牙/不误伤都必须成立）" -f $ctlFail.Count)
        foreach ($cf in $ctlFail) { Say ('        ' + $cf) }
        $fail++
    } else {
        Say ("  PASS 负控 {0} 条全部通过（有牙：坏形态必被抓到；不误伤：异步读/读在等待后/注释内 0 命中；双进程按变量配对；空语料 0/0）" -f $ctl)
    }

    if ($fail -eq 0) {
        Say ""
        Say "PASS 进程启动站点**没有新增**「同步 ReadToEnd 排在 WaitForExit 之前」的形态"
        Say ("      ⚠ 本结论**只覆盖**：src/FfmpegGui 下 A 类 {0} 处 + B 类 {1} 处（共 {2} 处）的**源码形态**" -f $real.direct, $real.instance, ($real.direct + $real.instance))
        Say "      ⚠ 它**不**覆盖：等待被抽到 helper 的站点（判据落空，见文件头缺口①）；"
        Say "        也**不**覆盖超时后是否 Kill / 是否杀进程树 / 有无取消通道 / 失败是否可见（另三条属性）。"
        Say "      ⚠ 它**不**证明任何一处「会挂死」——只证明「该形态的站点数没有增加」。"
        $code0 = 0
    } else {
        Say ""
        Say ("FAIL 见上面 {0} 条" -f $fail)
        $code0 = 1
    }
}
catch {
    Say "EXCEPTION: $($_.Exception.GetType().Name): $($_.Exception.Message)"
    Say ($_.ScriptStackTrace -replace "`r?`n", ' | ')
    $code0 = 9
}

$dir = Split-Path -Parent $Report
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
[System.IO.File]::WriteAllLines($Report, $lines, (New-Object System.Text.UTF8Encoding($false)))
exit $code0
