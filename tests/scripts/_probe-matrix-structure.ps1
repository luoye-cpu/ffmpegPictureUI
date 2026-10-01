# ══════════════════════════════════════════════════════════════════════════════
# 第 55 条门禁（2026-09-27 接线，任务 #42）：色彩矩阵清单的**结构自检**
# ══════════════════════════════════════════════════════════════════════════════
# 为什么单独一条：`_lib-matrix.ps1` 的折叠表 / 语义矩阵每条都带 `Evidence`
# （`<repo 相对路径>:<行号>|<字面片段>`），A21/A30 会**真的去读那一行**核对片段还在不在。
# 但在此之前，跑它的只有 `verify-oracle-matrix.ps1` —— 那是一条**有意不入 54 条清单**的
# 2.5 h 长跑门禁 ⇒ 行号漂移只能等某个手动跑它的人偶然看见。实测已经腐烂过 **6/19 条**
# （2026-09-27 上午核账），而今天（#39/#43 改了 `FfmpegCommandBuilder.cs` 的行）当场又漂一条：
# `metadata-core` 的 pin 从 2011 漂到 2041，2026-09-30 再漂到 2055（显示尺寸轮往同一文件插了 14 行）。⇒ 秒级自检必须进清单，不能绑在长跑上。
#
# 成本实测 5.5 s（两遍 = 约 11 s），不起产品进程、不产生任何转换。
#
# 退出码契约（与 `_lib-matrix.ps1` 文件头同规）：末行恒为
#   `MATRIXSTRUCT RESULT: pass=N fail=M`，且 `if ($fail -gt 0) { exit 1 } else { exit 0 }` 成对出现。
#   ① 恒 exit 0 = 假绿；② 守卫只写 `exit 1` 不写 `else { exit 0 }` = 污染 $LASTEXITCODE = 假红。
$ErrorActionPreference = 'Continue'
$fail = 0
$pass = 0
function CK {
    param([bool]$Ok, [string]$Msg)
    if ($Ok) { $script:pass++; Write-Output ('  PASS ' + $Msg) }
    else { $script:fail++; Write-Output ('  FAIL ' + $Msg) }
}

$scriptDir = $PSScriptRoot
$lib = Join-Path $scriptDir '_lib-matrix.ps1'
if (-not (Test-Path -LiteralPath $lib)) {
    Write-Output 'MATRIXSTRUCT RESULT: pass=0 fail=1'
    Write-Output '  FAIL 库文件缺失，无法自检'
    exit 1
}

# dot-source 零副作用（库内已处理 `_lib-axes.ps1` 的 param 覆盖陷阱）。
. $lib

# ── 第一遍：真实数据 + 库自带的 B1..B7 正/负控 ──
$run1 = @(Invoke-MatrixSelfTest)
$resultLine = ''
foreach ($l in $run1) { if ([string]$l -match '^MATRIX RESULT:') { $resultLine = [string]$l } }
CK ($resultLine -ne '') ('结果行存在（打印 ' + $run1.Count + ' 行，其中结果行 = [' + $resultLine + ']）')
if ($resultLine -eq '') {
    # 前置不足：后面的账全都没意义，直接红并中止（不许"读不到就当过"）
    Write-Output 'MATRIXSTRUCT RESULT: pass=1 fail=1'
    exit 1
}
$m = [regex]::Match($resultLine, 'pass=(\d+) fail=(\d+)')
CK $m.Success ('结果行可解析（实得 [' + $resultLine + ']）')
if (-not $m.Success) { Write-Output 'MATRIXSTRUCT RESULT: pass=1 fail=2'; exit 1 }
$p1 = [int]$m.Groups[1].Value
$f1 = [int]$m.Groups[2].Value

# 两本账必须一致：打印出来的汇总 vs 库内 $script:MatrixSelfTestResult（同一事实两个写者）。
$varPass = -1; $varFail = -1
if (Test-Path -Path 'variable:MatrixSelfTestResult') {
    $varPass = [int]$script:MatrixSelfTestResult['Pass']
    $varFail = [int]$script:MatrixSelfTestResult['Fail']
}
CK (($varPass -eq $p1) -and ($varFail -eq $f1)) (`
    '打印汇总与 $MatrixSelfTestResult 一致（打印 pass=' + $p1 + ' fail=' + $f1 +
    '，变量 pass=' + $varPass + ' fail=' + $varFail + '）——不一致 ⇒ 有一本账是假的')

# 断言总数下限：防"清单被清空 ⇒ 0 红 ⇒ 全绿"。现测 38 条（A1..A31 + B1..B7）。
CK (($p1 + $f1) -ge 30) ('自检断言总数 = ' + ($p1 + $f1) + '（下限 30；掉了说明有人删了检查项而不是删了被检查的东西）')
CK ($f1 -eq 0) ('结构自检零红（实得 fail=' + $f1 + '）')
if ($f1 -gt 0) {
    # ⚠ 这一小段是 2026-09-27 补的：上一版这里写「红项见上方 FAIL 行」，但本探针把 $run1 **全数吞掉**
    #   （只取结果行）⇒ 那句话指向的是不存在的内容，门禁红了却给不出证据。红项必须**就地回显**。
    foreach ($l in $run1) {
        $s = [string]$l
        if ($s -match '^\s*FAIL') { Write-Output ('     ↳ ' + $s) }
    }
}

# 取证条目非空：A21（折叠表）与 A30（矩阵）都是**负断言**，checked=0 时它们恒绿 ⇒ 必须拦。
$evLine21 = ($run1 | Where-Object { [string]$_ -match '^  (PASS|FAIL) A21 ' } | Select-Object -First 1)
$evLine30 = ($run1 | Where-Object { [string]$_ -match '^  (PASS|FAIL) A30 ' } | Select-Object -First 1)
$c21 = [regex]::Match([string]$evLine21, 'checked=(\d+)')
$c30 = [regex]::Match([string]$evLine30, 'checked=(\d+)')
CK ($c21.Success -and [int]$c21.Groups[1].Value -ge 1) ('A21 折叠取证确有条目在被核对（实得 checked=[' + $c21.Groups[1].Value + ']）')
CK ($c30.Success -and [int]$c30.Groups[1].Value -ge 1) ('A30 矩阵取证确有条目在被核对（实得 checked=[' + $c30.Groups[1].Value + ']）')
Write-Output ('  取证规模: A21 checked=' + $c21.Groups[1].Value + '  A30 checked=' + $c30.Groups[1].Value)

# ── 第二遍：变异 —— 删掉一对两两覆盖 ⇒ A7 必须转红（证明本门禁有牙，不是恒绿）──
$run2 = @(Invoke-MatrixSelfTest -InjectBadPair)
$resultLine2 = ''
foreach ($l in $run2) { if ([string]$l -match '^MATRIX RESULT:') { $resultLine2 = [string]$l } }
$m2 = [regex]::Match($resultLine2, 'pass=(\d+) fail=(\d+)')
$a7 = [string](($run2 | Where-Object { [string]$_ -match '^  FAIL A7 ' } | Select-Object -First 1))
$a7short = if ($a7) { $a7.Trim().Substring(0, [Math]::Min(120, $a7.Trim().Length)) } else { '<缺>' }
CK ($m2.Success -and [int]$m2.Groups[2].Value -ge 1 -and ($a7 -match 'missing=[1-9]')) (`
    '变异注入（删掉一对两两覆盖）⇒ 自检必须转红且点名 A7（实得 [' + $resultLine2 + ']，A7 行 [' + $a7short + ']）')

Write-Output ('MATRIXSTRUCT RESULT: pass=' + $pass + ' fail=' + $fail)
if ($fail -gt 0) { exit 1 } else { exit 0 }
