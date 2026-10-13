# ═══════════════════════════════════════════════════════════════════════════════
# verify-probe-mirror-parity.ps1 —— 「内核断言的两份拷贝」的**同形态尺**（2026-10-13 批次 G 探针面）
#
# 【要钉的性质】`contract`/`bench`/`selftest`/`tooldetect`/`i18n`/`xcheck`/`psnr`/`peak` 这 8 个 mode
#   在内核侧有**两份独立拷贝**：
#     · **被执行的那份** = `tests/ServiceProbe/Program.cs`（运行器 `$Probes` 只调它）；
#     · **镜像那份**     = `tests/core/Modes/*.cs` 与 `tests/diagnostics/Modes/*.cs`
#       （`<OutputType>Exe</OutputType>`、`dotnet test` 打它只还原不跑还 exit 0 ⇒ **没有任何执行者**）。
#   本仓已实测踩过一次「只写进镜像 ⇒ `contract` 报 137/0 全绿而那条断言一条没跑」。
#   ⇒ 合并/删除是后面一步，**这一步先把「两份漂了没漂」变成读数**。
#
# 【为什么是「标签多重集」而不是整文件比字节】两份的**插值变量名与注释本来就不同**
#   （SP 写 `{name}`、镜像写 `{nameC20}`；SP 注释「两副面孔不留」镜像没有）⇒ 整文件比对会
#   把「文案没变、只是变量改了名」判成漂。⇒ 取 `Check(...)` 里的**中文字面片段**拼成标签，
#   把 `{...}` 归一成同一个槽位（**变量名不算差异，文案算**），再比**多重集**（重复次数也管）。
#   ⇒ 与 Python 版取样器（`tests/output/step2/probe_mirror_ruler.py`）是**两个独立实现**，
#     实测逐 mode 完全一致：92/91、5/5、18/18、27/27、5/5、6/6、2/2、8/8（合计 163/162）。
#
# 【今日实测】bench/selftest/tooldetect/i18n/xcheck/psnr/peak = 多重集**完全相等**；
#   contract = SP 92 / 镜像 91，差**恰好 1 条**：`C20-8b …8-bit 出口送表与不送表差须在容差内`。
#   ⇒ 那 1 条**不是欠账**：镜像自己第 44 行写着「要加断言请加在那一份上；写在这里会全绿但一条不执行」
#     ⇒ 镜像是**冻结参照件**，把新断言搬进死代码只会制造「以为有人执行」的更坏假象。
#   ⇒ 处理 = 下面 `$delta` 里**按名字登记**，并用反控逼它「恰好命中 1 条 × 1 次」：
#     漂移必须点名，不许静默变大（登记项一旦被同步 ⇒ 命中数变 0 ⇒ 当场红，逼人来删登记）。
#
# 【变异验牙四支】读数 `tests/output/step2/mut_parity_readout.txt`，各只红该红那格、逐字节还原：
#   T1 只改镜像一条标签**文案** ⇒ tooldetect 红 ｜ T2 把镜像一条 `Check(` 改名 ⇒ peak 红
#   T3 只改被执行那份的 `C20-8b` ⇒ contract **两格**红（未登记差 + 登记命中 0）
#   T4 **两侧同步**改同一条标签 ⇒ 必须**全绿**（假阳性对照：这把尺量「漂」不是量「变」）
#
# ⚠ 差集元素一律 **[pscustomobject]**，不用 `@($k,$d)`：PowerShell 的 `+=` 会把「数组套数组」
#   **摊平** ⇒ 实测 `$e[0]` 变成 Char、`.Substring` 当场 InvalidOperation，而门禁**已经打了几条
#   PASS 之后**才崩 ⇒ 只看结尾会把它读成「没红」（本文件第一版就是这么死的，靠原始流才看清）。
# ═══════════════════════════════════════════════════════════════════════════════

param([int]$MinLabelsPerMode = 2,
      # `-Census`：**不判定**，只把尺伸到「所有 `tests/*/Modes/*.cs`」上量一遍漂了多少。
      #   为什么要它：本门禁受管的只有 8 个 mode，其余镜像拷贝漂没漂**没有读数** ⇒
      #   "要不要合并/删除"这个决定不能拍脑袋，要先有逐 mode 的差集规模。
      #   ⚠  census 走的是**同一批取样函数**（不是另写一把尺），所以它的数与判定段可比。
      #   ⚠  census 段永远 exit 0：它是取数，不是裁决；裁决在下面 8 mode 的判定段。
      [switch]$Census)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $root

$pass = 0; $fail = 0
function CK([bool]$ok, [string]$msg) {
    if ($ok) { Write-Host "PASS $msg" -ForegroundColor Green; $script:pass++ }
    else     { Write-Host "FAIL $msg" -ForegroundColor Red;   $script:fail++ }
}
Write-Host "===== probe-mirror-parity =====" -ForegroundColor Cyan

$SP = 'tests/ServiceProbe/Program.cs'
# 模式表：mode ⇒（被执行那份的方法名，镜像文件，镜像方法名）
$modes = [ordered]@{
    'contract'   = @{ sp = 'ProbeContract';   mir = 'tests/core/Modes/contract.cs';          mm = 'ProbeContract' }
    'bench'      = @{ sp = 'ProbeBench';      mir = 'tests/diagnostics/Modes/bench.cs';      mm = 'ProbeBench' }
    'selftest'   = @{ sp = 'ProbeSelfTest';   mir = 'tests/diagnostics/Modes/selftest.cs';   mm = 'ProbeSelfTest' }
    'tooldetect' = @{ sp = 'ProbeToolDetect'; mir = 'tests/diagnostics/Modes/tooldetect.cs'; mm = 'ProbeToolDetect' }
    'i18n'       = @{ sp = 'ProbeI18n';       mir = 'tests/diagnostics/Modes/i18n.cs';       mm = 'ProbeI18n' }
    'xcheck'     = @{ sp = 'ProbeXcheck';     mir = 'tests/diagnostics/Modes/xcheck.cs';     mm = 'ProbeXcheck' }
    'psnr'       = @{ sp = 'ProbePsnr';       mir = 'tests/diagnostics/Modes/psnr.cs';       mm = 'ProbePsnr' }
    'peak'       = @{ sp = 'ProbePeak';       mir = 'tests/diagnostics/Modes/peak.cs';       mm = 'ProbePeak' }
}
# 已登记的有意差（前缀匹配）。⚠ 每条登记**必须写声明条数**（Labels）与**声明重复次数**（Times）：
#   命中 0、多于声明、或少于声明都判红 —— "登记"不是豁免，是把漂移的**大小**也钉住。
#   第一版这里写死"恰好 1 条 × 1 次"，2026-10-13 加 `C20-AX` 那一族（7 条同名前缀）时
#   只能被迫"一条一条登记 7 次"⇒ 那种写法既啰嗦又守不住"8 条"这种增量，故改成带计数的登记。
$delta = @{
    'contract' = @(
        @{ Prefix = 'C20-8b '; Labels = 1; Times = 1 }
        @{ Prefix = 'C20-AX '; Labels = 7; Times = 7 }
    )
    'bench' = @(
        # 2026-10-13 新增的 8-bit 交付出口档：**源文本**里 4 条标签各 1 次（前置 / ≥25MPix/s /
        # 增益 ≥2× / 档位数量）。⚠ 这里 Times 量的是"源文本出现次数"，不是运行时会跑几次 ——
        #   循环体只有一份源文本，两格共用同一模板 ⇒ 第一版按"3 模板 × 2 格 = 6"登记，实测被自己
        #   打回（尺报 3 条/3 次）。⇒ "每一档都还在"这件事不能指望这把尺，已由 `8b 档位数量`
        #   那条**运行时**断言钉住（数跑过的格子数 == 声明格子数 == 2）。
        @{ Prefix = '8b '; Labels = 4; Times = 4 }
    )
}

# ── 取样器（两份用**同一个函数**量 ⇒ 尺不偏袒任何一侧）──
function Get-MethodText([string]$text, [string]$name) {
    $m = [regex]::Match($text, "(?m)^[ \t]*(?:private\s+|internal\s+|public\s+)?static[^\r\n]*\b$name\s*\(")
    if (-not $m.Success) { return $null }
    $ob = $text.IndexOf('{', $m.Index + $m.Length)
    if ($ob -lt 0) { return $null }
    $depth = 0
    for ($j = $ob; $j -lt $text.Length; $j++) {
        $c = $text[$j]
        if ($c -eq '{') { $depth++ }
        elseif ($c -eq '}') { $depth--; if ($depth -eq 0) { return $text.Substring($m.Index, $j - $m.Index + 1) } }
    }
    return $null
}
function Get-CheckLabels([string]$body) {
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($m in [regex]::Matches($body, '\bCheck\s*\(')) {
        $i = $m.Index + $m.Length; $depth = 1; $j = $i
        while ($j -lt $body.Length -and $depth -gt 0) {
            $c = $body[$j]
            if ($c -eq '(') { $depth++ } elseif ($c -eq ')') { $depth-- }
            $j++
        }
        $seg = $body.Substring($i, $j - $i - 1)
        # 标签 = 这条 `Check` 的**第一个**「像名字」的字面量（断言名本身），**不是**整段拼接。
        # ⚠ 第一版把该调用里的所有字面量拼成一个标签，实测造出 **981 字符**的"标签"：
        #   两份只要**语句顺序**不同（或一侧多写了详情文案）就整条判成漂 —— 那是把
        #   「文案的位置」当成了「断言的存在性」。本锁判的是后者 ⇒ 只取名字，忽略详情串。
        #   （抓到的实例：`gamutfit` 的 (f)/(g) 两段在两份里先后不同 ⇒ 3 条"镜像独有"假红。）
        foreach ($s in [regex]::Matches($seg, '"((?:[^"\\]|\\.)*)"')) {
            $v = [regex]::Replace($s.Groups[1].Value, '\{[^{}]*\}', '{}')      # 插值槽位归一：变量名不算差异
            $v = $v.Trim()
            if ($v -match '[一-鿿]' -or $v.Length -gt 6) { $out.Add($v); break }   # 只取「像标签」的第一个片段
        }
    }
    return $out
}
function Cmp-Multiset($a, $b) {
    $ha = @{}; foreach ($x in $a) { $ha[$x] = 1 + [int]$ha[$x] }
    $hb = @{}; foreach ($x in $b) { $hb[$x] = 1 + [int]$hb[$x] }
    $onlyA = New-Object System.Collections.Generic.List[object]
    $onlyB = New-Object System.Collections.Generic.List[object]
    foreach ($k in $ha.Keys) {
        $dd = $ha[$k] - [int]$hb[$k]
        if ($dd -gt 0) { [void]$onlyA.Add([pscustomobject]@{ Label = $k; Times = $dd }) }
    }
    foreach ($k in $hb.Keys) {
        $dd = $hb[$k] - [int]$ha[$k]
        if ($dd -gt 0) { [void]$onlyB.Add([pscustomobject]@{ Label = $k; Times = $dd }) }
    }
    return @{ OnlyA = $onlyA; OnlyB = $onlyB }
}
function Test-Registered([string]$label, $reg) {
    foreach ($p in $reg) { if ($label.StartsWith([string]$p.Prefix)) { return $true } }
    return $false
}

if ($Census) {
    # ── 普查（不是判定）：拿**同一把尺**量所有镜像拷贝，回答"合并这一步到底要动多少东西" ──
    Write-Host '===== census：镜像全集的漂移量（本段不判红绿，只是读数）=====' -ForegroundColor Cyan
    $spTextC = [System.IO.File]::ReadAllText((Join-Path $root $SP))
    $files = @(Get-ChildItem -Path (Join-Path $root 'tests') -Recurse -Filter '*.cs' |
               Where-Object { $_.FullName -match '\\Modes\\' -and $_.FullName -notmatch '\\(bin|obj)\\' } |
               Sort-Object FullName)
    Write-Host ("镜像 mode 文件 {0} 份（tests/*/Modes/*.cs，剔除 bin/obj）" -f $files.Count)
    $rows = @()
    foreach ($f in $files) {
        $ftxt = [System.IO.File]::ReadAllText($f.FullName)
        foreach ($mm in [regex]::Matches($ftxt, "(?m)^[ \t]*(?:private\s+|internal\s+|public\s+)?static[^\r\n]*\b(Probe[A-Za-z0-9_]+)\s*\(")) {
            $name = $mm.Groups[1].Value
            $mirBody = Get-MethodText $ftxt $name
            $spBody = Get-MethodText $spTextC $name
            $rel = $f.FullName.Substring($root.Length + 1) -replace '\\', '/'
            if (-not $spBody) {
                $rows += [pscustomobject]@{ file = $rel; method = $name; sp = '<无同名>'; mir = (Get-CheckLabels $mirBody).Count; onlySp = '-'; onlyMir = '-' }
                continue
            }
            $ls = Get-CheckLabels $spBody; $lm = Get-CheckLabels $mirBody
            $d = Cmp-Multiset $ls $lm
            $os = 0; foreach ($e in $d.OnlyA) { $os += [int]$e.Times }
            $om = 0; foreach ($e in $d.OnlyB) { $om += [int]$e.Times }
            $rows += [pscustomobject]@{ file = $rel; method = $name; sp = $ls.Count; mir = $lm.Count; onlySp = $os; onlyMir = $om }
        }
    }
    $rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
    $both = @($rows | Where-Object { $_.sp -ne '<无同名>' })
    $drift = @($both | Where-Object { [int]$_.onlySp -gt 0 -or [int]$_.onlyMir -gt 0 })
    Write-Host ("两侧都有同名方法的 mode：{0} 份；其中有漂移的：{1} 份；镜像侧孤本（SP 无同名）：{2} 份" -f `
            $both.Count, $drift.Count, @($rows | Where-Object { $_.sp -eq '<无同名>' }).Count)
    Write-Host ("漂移标签总数：只被执行那份 {0} 条、只镜像那份 {1} 条" -f `
            (($both | ForEach-Object { [int]$_.onlySp } | Measure-Object -Sum).Sum),
            (($both | ForEach-Object { [int]$_.onlyMir } | Measure-Object -Sum).Sum))
    Write-Host '（census 不是验收读数：它只说明"合并要动多少"，不说明"哪一侧对"）' -ForegroundColor Yellow
    exit 0
}

$spText = [System.IO.File]::ReadAllText((Join-Path $root $SP))
CK ($spText.Length -gt 100000) "前置：被执行那份读到了 $($spText.Length) 字符（$SP）"

$sumSp = 0; $sumMir = 0
foreach ($mode in $modes.Keys) {
    $cfg = $modes[$mode]
    $mirPath = Join-Path $root $cfg.mir
    if (-not (Test-Path $mirPath)) { CK $false "$mode 镜像文件在位（$($cfg.mir)）"; continue }
    $spBody = Get-MethodText $spText $cfg.sp
    $mirBody = Get-MethodText ([System.IO.File]::ReadAllText($mirPath)) $cfg.mm
    if (-not $spBody -or -not $mirBody) {
        CK $false "$mode 两侧都解析到方法体（SP=$([bool]$spBody) 镜像=$([bool]$mirBody)）⇒ 签名形状变了，尺失效"
        continue
    }
    $ls = Get-CheckLabels $spBody
    $lm = Get-CheckLabels $mirBody
    $sumSp += $ls.Count; $sumMir += $lm.Count
    $d = Cmp-Multiset $ls $lm
    $reg = @()
    if ($delta.ContainsKey($mode)) { $reg = @($delta[$mode]) }

    # 1) 未登记的差 ⇒ 一律红（两个方向都管）
    $unA = @($d.OnlyA | Where-Object { -not (Test-Registered $_.Label $reg) })
    $unB = @($d.OnlyB | Where-Object { -not (Test-Registered $_.Label $reg) })
    $det = ''
    if ($unA.Count -or $unB.Count) {
        $l2 = @()
        foreach ($e in @($unA | Select-Object -First 3)) { $t = [string]$e.Label; $l2 += ('        只SP   x' + $e.Times + ' ' + $t.Substring(0, [Math]::Min(90, $t.Length))) }
        foreach ($e in @($unB | Select-Object -First 3)) { $t = [string]$e.Label; $l2 += ('        只镜像 x' + $e.Times + ' ' + $t.Substring(0, [Math]::Min(90, $t.Length))) }
        $det = "`n" + ($l2 -join "`n")
    }
    CK ($unA.Count -eq 0 -and $unB.Count -eq 0) ("$mode 标签多重集相等（SP=$($ls.Count) 镜像=$($lm.Count)；未登记差 只SP=$($unA.Count) 只镜像=$($unB.Count)）$det")

    # 2) 登记表每条必须**恰好命中声明的条数与次数**（防登记过期后变成「什么都放过」：
    #    命中少 = 那条断言没了 / 命中多 = 有人往同一前缀里加了没登记的断言，两边都要红）
    foreach ($ent in $reg) {
        $pfx = [string]$ent.Prefix; $wantL = [int]$ent.Labels; $wantT = [int]$ent.Times
        $hitA = @($d.OnlyA | Where-Object { $_.Label.StartsWith($pfx) })
        $hitB = @($d.OnlyB | Where-Object { $_.Label.StartsWith($pfx) })
        $n = 0
        foreach ($e in $hitA) { $n += [int]$e.Times }
        CK ($hitA.Count -eq $wantL -and $hitB.Count -eq 0 -and $n -eq $wantT) ("$mode 登记差「$pfx…」恰好命中：只SP $wantL 条 × 共 $wantT 次（实 只SP=$($hitA.Count) 条/共 $n 次，只镜像=$($hitB.Count)）")
    }
    # 3) 反控：两侧都不是空的取样器产物（防路径/签名错导致「两边都 0 条 ⇒ 相等」）
    CK ($ls.Count -ge $MinLabelsPerMode -and $lm.Count -ge $MinLabelsPerMode) ("$mode 反控：两侧各取到 $($ls.Count)/$($lm.Count) 条标签（下限 $MinLabelsPerMode）")
    # 4) 反控：归一化没把标签压成同一条
    $uniq = @($ls | Sort-Object -Unique).Count
    CK ($uniq -ge [Math]::Ceiling(0.8 * $ls.Count)) ("$mode 反控：SP 侧去重后 $uniq / $($ls.Count) 条（归一化没把所有标签压成一个）")
}
CK ($sumSp -ge 150 -and $sumMir -ge 150) "总量反控：SP 取到 $sumSp 条、镜像 $sumMir 条（>=150 ⇒ 8 个 mode 都真被读到）"
CK ($modes.Count -eq 8) "结构反控：模式表 8 项（实 $($modes.Count)）⇒ 少一项就是少一个 mode 的账"

# ── 全镜像**方向锁**（2026-10-13 批次 G 探针面第二半，先量后钉）──────────────────────
# 上面 8 个 mode 用的是"多重集相等 + 逐条登记"，其余 23 个镜像 mode 此前**无人管**。
# 普查（`-Census`）实测：31 份镜像全部有同名被执行方法；其中 6 份有漂移，
# **方向清一色是「只在被执行那份有」**（只镜像那份 = 0 条）⇒ 冻结参照件没往里加东西。
# ⇒ 这里加一条**覆盖面大、又不要求往死代码里搬断言**的方向锁：
#     · 镜像侧独有标签必须为 0 —— 它正是本批踩过的那次事故形态（"写进镜像 ⇒ 全绿但一条没跑"）；
#     · 被执行那份的标签数**不得少于**镜像（少 = 被执行面丢了断言）。
# ⚠ 刻意**不**判"两侧完全相等"：镜像按定案就是冻结件，新断言只写执行面 ⇒ 差是设计如此，
#   要它相等就得把断言往死代码里搬（那才是更坏的假象）。逐条登记制仍保留在上面 8 个 mode。
Write-Host '===== 全镜像方向锁（31 份，不要求相等，只要求"死代码里不许独有"）=====' -ForegroundColor Cyan
$dirRows = 0; $dirMirOnlyTotal = 0; $dirSpFewer = 0; $dirSampledZero = 0
foreach ($f in @(Get-ChildItem -Path (Join-Path $root 'tests') -Recurse -Filter '*.cs' |
                 Where-Object { $_.FullName -match '\\Modes\\' -and $_.FullName -notmatch '\\(bin|obj)\\' } |
                 Sort-Object FullName)) {
    $ftxt = [System.IO.File]::ReadAllText($f.FullName)
    foreach ($md in [regex]::Matches($ftxt, "(?m)^[ \t]*(?:private\s+|internal\s+|public\s+)?static[^\r\n]*\b(Probe[A-Za-z0-9_]+)\s*\(")) {
        $name = $md.Groups[1].Value
        $mirBody = Get-MethodText $ftxt $name
        $spBody = Get-MethodText $spText $name
        $rel = $f.FullName.Substring($root.Length + 1) -replace '\\', '/'
        if (-not $spBody) { CK $false "方向锁 $rel::$name 被执行那份无同名方法 ⇒ 这份镜像没有执行者（$rel）"; continue }
        $ls = Get-CheckLabels $spBody; $lm = Get-CheckLabels $mirBody
        $d = Cmp-Multiset $ls $lm
        $os = 0; foreach ($e in $d.OnlyA) { $os += [int]$e.Times }
        $om = 0; foreach ($e in $d.OnlyB) { $om += [int]$e.Times }
        $dirRows++
        $dirMirOnlyTotal += $om
        if ($ls.Count -lt $lm.Count) { $dirSpFewer++ }
        if ($lm.Count -lt 1 -or $ls.Count -lt 1) { $dirSampledZero++ }
        # 红的时候必须**点名那几条**：只给条数就没法分辨"死代码里真有断言"与"文案各写各的"，
        # 而这两者的处置完全不同（前者要搬回执行面，后者只是噪声）。
        $detail = ''
        if ($om -gt 0) {
            $parts = @()
            foreach ($e in $d.OnlyB) { $t = $e.Label; $parts += '只镜像 x' + $e.Times + ' ' + $t.Substring(0, [Math]::Min(70, $t.Length)) }
            foreach ($e in $d.OnlyA) { $t = $e.Label; $parts += '只执行 x' + $e.Times + ' ' + $t.Substring(0, [Math]::Min(70, $t.Length)) }
            $detail = "`n        " + (($parts | Select-Object -First 8) -join "`n        ")
        }
        CK ($om -eq 0 -and $ls.Count -ge $lm.Count) ("方向锁 $name：镜像独有 $om 条（须 0）、被执行 $($ls.Count) ≥ 镜像 $($lm.Count)（只执行面多 $os 条=设计如此）[$rel]$detail")
    }
}
CK ($dirRows -ge 31) "方向锁覆盖面反控：量了 $dirRows 个镜像 mode（>=31 ⇒ 31 份 Modes/*.cs 都进了账，没被静默跳过）"
CK ($dirSampledZero -eq 0) "方向锁取样反控：没有任何一格的取样条数塌成 0（塌 0 会让上面两条恒真）——实塌 $dirSampledZero 格"
CK ($dirMirOnlyTotal -eq 0) "方向锁总账：31 份镜像里「只在死代码里有的断言」共 $dirMirOnlyTotal 条（须 0）"
CK ($dirSpFewer -eq 0) "方向锁总账：被执行那份比镜像少的 mode 数 = $dirSpFewer（须 0，少=执行面丢了断言）"

Write-Host "===== probe-mirror-parity: PASS=$pass FAIL=$fail =====" -ForegroundColor Cyan
if ($fail -gt 0) { exit 1 } else { exit 0 }
