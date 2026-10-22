# ═══════════════════════════════════════════════════════════════════════════════
# verify-ui-split-reproducible.ps1 —— 「UI 拆分是**生成物**」这件事的可执行判据（2026-10-12 批次 G）
#
# 【要钉住的性质】5 个 `tests/ui-*/*Group.cs` 必须**逐字节等于**
#   `tests/scripts/_gen-ui-split.ps1` 以 `tests/projects/UiTestHost/Program.cs` 为唯一真值重新生成的结果。
#
# 【为什么必须有它】2026-10-05 物理拆分时立约「组体逐字搬、断言文本一字未改」，
#   并由 `verify-ui-split-verbatim.ps1` 逐行比旧宿主与拆分件。但**逐字比**这件事本身
#   挡不住"只改拆分侧、不回灌真值"：2026-10-07 的 J1z/J8 修复就是只手改在 `ui-sweep` 里，
#   于是 verbatim 只能**整组放行 J**（J 组 798 行从此不进比较），而参照件旧宿主还在跑
#   **修复前**的代码 ——「唯一真值」名存实亡。同类手改共 5 次（J1z/J8/H10/O4/O5 与三个包装）。
#   ⇒ 本锁把"可复现"从**声称**变成**读数**：任何一次绕过生成器的手改，当场红。
#
# 【判据与反控】
#   R-1..R-5  5 个工程逐字节全等（sha256）
#   R-6       生成器确实**读了真值**：改掉真值里一个组体的一行 ⇒ 生成物必须随之变化
#             （没有这条，"生成器输出恒定 + 活树恒定"会造出一份什么都不检的假绿）
#   R-7       反控：5 份生成物都非空且行数在活树同侧量级（防路径错导致空文件互相"全等"）
#   R-8       换行尾必须是**纯 CRLF**（生成器脚本自身是 LF，曾经把 61 行裸 LF 注进生成物，
#             编辑器一保存就归一 ⇒ "重跑+比字节"永远对不上）
#   R-9       **零死包装**：每个活树文件里发出的转发包装，都必须被该文件的组体/入口引用
#             （手加过 `ResetAdvancedColorToAuto`：零调用点的死包装，正是绕过生成器的痕迹）
#   R-10      **双拷贝等值锁**：旧宿主 `SelectColorSpaceSettled` 的方法体必须与
#             `UiHostShared.UiHost` 那一份逐行相等（旧宿主不引用共享底座 ⇒ 只能同名各实现，
#             那就把"两份不许漂"做成读数。批次 G 下一步才谈把旧宿主改成转发，见账本）
#   R-11      旧宿主 `Skip` 的**形状锁**：打 `[SKIP]`、计 `_skip`，且**绝不**动 `_pass`/`_fail`
#             （参照件的汇总行形状被 union 锁按 `N PASS / M FAIL` 读取，改动会让参照失效）
# ═══════════════════════════════════════════════════════════════════════════════

param(
    [switch]$LeaveScratch
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $root

$pass = 0; $fail = 0
function CK([bool]$ok, [string]$msg) {
    if ($ok) { Write-Host "PASS $msg" -ForegroundColor Green; $script:pass++ }
    else     { Write-Host "FAIL $msg" -ForegroundColor Red;   $script:fail++ }
}

Write-Host "===== ui-split-reproducible =====" -ForegroundColor Cyan

$gen     = 'tests/scripts/_gen-ui-split.ps1'
$srcTruth = 'tests/projects/UiTestHost/Program.cs'
$projs   = [ordered]@{
    'ui-core'  = 'uicoreGroup'
    'ui-mode'  = 'uimodeGroup'
    'ui-color' = 'uicolorGroup'
    'ui-sweep' = 'uisweepGroup'
    'ui-cli'   = 'uicliGroup'
}

# ── 前置：三样东西必须在位（缺一样就红，不许"没得比 ⇒ 不判"）──────────────────
CK (Test-Path $gen)      "前置：生成器在位（$gen）"
CK (Test-Path $srcTruth) "前置：唯一真值在位（$srcTruth）"
$liveMissing = @($projs.Keys | Where-Object { -not (Test-Path "tests/projects/$_/$($projs[$_]).cs") })
CK ($liveMissing.Count -eq 0) "前置：5 个拆分件都在位（缺 $(if($liveMissing.Count){$liveMissing -join ','}else{'0'})）"
if ($fail -gt 0) {
    Write-Host "===== ui-split-reproducible: PASS=$pass FAIL=$fail（前置不成立，拒绝出绿）=====" -ForegroundColor Red
    exit 1
}

# Windows 上刚写完的目录常有滞留句柄（杀软/索引器），`Remove-Item` 一次就失败 ⇒ 静默留下垃圾。
# ⚠ 实测第一版就是"每次都少删最后一个写的目录"（跑 6 次留 6 份 560KB）⇒ 重试 + 开局清扫。
function Remove-Tree([string]$p) {
    if (-not $p -or -not (Test-Path $p)) { return $true }
    for ($k = 0; $k -lt 6; $k++) {
        Remove-Item -Recurse -Force $p -ErrorAction SilentlyContinue
        if (-not (Test-Path $p)) { return $true }
        Start-Sleep -Milliseconds 300
    }
    return -not (Test-Path $p)
}
foreach ($d in (Get-ChildItem -LiteralPath (Join-Path $root 'tests/output/step2') -Directory -Filter 'uigen*' -ErrorAction SilentlyContinue)) {
    Remove-Tree $d.FullName | Out-Null
}
$scratch = Join-Path $root ("tests/output/step2/uigen-{0}-{1}" -f $PID, (Get-Date).Ticks)
$clean   = Join-Path $root ("tests/output/step2/uigenclean-{0}-{1}" -f $PID, (Get-Date).Ticks)
$mutant  = Join-Path $root ("tests/output/step2/uigenmut-{0}-{1}" -f $PID, (Get-Date).Ticks)
function Invoke-Gen([string]$dst, [string]$srcAbs = (Join-Path $root $srcTruth)) {
    # 生成器在 Set-Location 之后按**相对路径**读 Src ⇒ 先把真值拷进这份空仓库根。
    # ⚠ `$srcAbs` 必须可换：R-6 要把**改过的真值副本**喂进去；若这里恒拷活树那一份，
    #   注入会被自己的拷贝覆盖 ⇒ 对照臂永远"没变"（第一次跑 R-6 就是这样判红的——
    #   判据的假红最先暴露的永远是**判据自己**的洞）。
    New-Item -ItemType Directory -Force -Path (Join-Path $dst 'tests/projects/UiTestHost') | Out-Null
    Copy-Item $srcAbs (Join-Path $dst 'tests/projects/UiTestHost/Program.cs') -Force
    $out = & (Join-Path $root $gen) -RepoRoot $dst 2>&1
    return @{ Text = ($out | Out-String); Threw = (@($out | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })).Count -gt 0 }
}
function GenFile([string]$dst, [string]$proj) {
    return Join-Path $dst ("tests/projects/{0}/{1}.cs" -f $proj, $projs[$proj])
}
function Sha([string]$p) { return (Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash }

try {
    # ══════════════ R-1..R-5：逐字节全等 ══════════════
    $g = Invoke-Gen $scratch
    CK (-not $g.Threw) "生成器无错误退出（5 次 wrote）"
    $genOk = $true
    foreach ($proj in $projs.Keys) {
        $nf = GenFile $scratch $proj
        $lf = "tests/projects/$proj/$($projs[$proj]).cs"
        if (-not (Test-Path $nf)) { CK $false "R $proj：生成器没产出文件"; $genOk = $false; continue }
        $hN = (Sha $nf).Substring(0, 16); $hL = (Sha (Join-Path $root $lf)).Substring(0, 16)
        $lnN = ([System.IO.File]::ReadAllLines($nf)).Count; $lnL = ([System.IO.File]::ReadAllLines((Join-Path $root $lf))).Count
        CK ($hN -eq $hL) ("R-1 {0,-9} 逐字节全等（gen {1} 行 {2} / live {3} 行 {4}）" -f $proj, $lnN, $hN, $lnL, $hL)
        if ($hN -ne $hL) {
            $d = Compare-Object ([System.IO.File]::ReadAllLines($nf)) ([System.IO.File]::ReadAllLines((Join-Path $root $lf)))
            $only = @($d | Where-Object { $_.SideIndicator -eq '>' })
            Write-Host ("      活树独有 {0} 行，前 3 行：" -f $only.Count) -ForegroundColor DarkGray
            $only | Select-Object -First 3 | ForEach-Object { Write-Host ("        " + $_.InputObject.Trim().Substring(0, [Math]::Min(120, $_.InputObject.Trim().Length))) -ForegroundColor DarkGray }
        }
    }

    # ══════════════ R-6：生成器真的在读真值（改一行 ⇒ 生成物必须变）══════════════
    #   这一条把"恒等式两侧都不动"的假绿堵掉：若生成器读错了路径而输出固定文本，
    #   改真值不会改变输出 ⇒ 本条红。变异打在**独立副本**上，绝不碰活树、也不碰 $scratch。
    $shaBefore = Sha (GenFile $scratch 'ui-sweep')
    $mutSrc = Join-Path $root ("tests/output/step2/uigenmut-src-{0}.cs" -f $PID)
    $text = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes((Join-Path $root $srcTruth)))
    $needle = '            Safe("J1z '
    $nth = ([regex]::Matches($text, [regex]::Escape($needle))).Count
    CK ($nth -ge 1) "R-6 前置：真值里命中 J1z 锚点 $nth 处（防'改不到却说改了'）"
    $text2 = $text.Replace($needle, "            // __gencheck_probe__`r`n" + $needle)
    CK ($text2 -ne $text) "R-6 前置：注入真的改了真值副本（替换未命中 ⇒ 对照臂根本没动）"
    [System.IO.File]::WriteAllBytes($mutSrc, [System.Text.Encoding]::UTF8.GetBytes($text2))
    $null = Invoke-Gen $mutant $mutSrc
    $shaAfter = Sha (GenFile $mutant 'ui-sweep')
    CK ($shaBefore -ne $shaAfter) "R-6 牙齿：真值加一行 ⇒ ui-sweep 生成物哈希随之改变（防'生成物与真值无关'的假绿）"

    # ══════════════ R-7/R-8：生成物量级 + 纯 CRLF ══════════════
    #   R-7 需要**未被 R-6 污染**的那一份 ⇒ 重跑一次干净生成（$clean 在文件头声明）
    $null = Invoke-Gen $clean
    $thin = @()
    $bareLf = @{}
    foreach ($proj in $projs.Keys) {
        $lf = Join-Path $root ("tests/projects/{0}/{1}.cs" -f $proj, $projs[$proj])
        $b  = [System.IO.File]::ReadAllBytes($lf)
        $s  = [System.Text.Encoding]::UTF8.GetString($b)
        $lines = ($s -split "`r?`n").Count
        $nf = GenFile $clean $proj
        $gl = if (Test-Path $nf) { ([System.IO.File]::ReadAllLines($nf)).Count } else { 0 }
        if ($gl -lt 400 -or $lines -lt 400) { $thin += "$proj(gen=$gl,live=$lines)" }
        # 裸 LF = LF 总数 - CRLF 总数（byte[] 没有 .Count($x) 这种方法，别凭记忆写 API）
        $bareLf[$proj] = ([regex]::Matches($s, "`n")).Count - ([regex]::Matches($s, "`r`n")).Count
    }
    CK ($thin.Count -eq 0) ("R-7 反控：5 份生成物与活树都在 ≥400 行量级（薄 $(if($thin.Count){$thin -join ';'}else{'0'})）—— 防空文件互比假绿")
    $badEol = @($bareLf.Keys | Where-Object { $bareLf[$_] -ne 0 })
    CK ($badEol.Count -eq 0) ("R-8 换行尾纯 CRLF：5 个活树文件裸 LF 全为 0（非 0 的：$(if($badEol.Count){$badEol -join ','}else{'无'})）")
    CK ((Sha (GenFile $clean 'ui-sweep')) -eq $shaBefore) "R-8b 重跑生成器**两次**得到同一字节（幂等 ⇒ 生成物不含时间戳/PID 之类的非确定内容）"

    # ══════════════ R-9：零死包装（不查生成器，只看活树自身）══════════════
    #   声明区 = 类里到第一个「组」标记之前；使用区 = 第一个「组」标记到文件末尾。
    #   每个包装名必须在**使用区**（剥掉行注释后）出现；出现即算引用（漏判会编译失败，误判只多留包装）。
    $dead = @()
    foreach ($proj in $projs.Keys) {
        $lf = Join-Path $root ("tests/projects/{0}/{1}.cs" -f $proj, $projs[$proj])
        $ls = [System.IO.File]::ReadAllLines($lf)
        $grpIdx = -1
        for ($i = 0; $i -lt $ls.Count; $i++) { if ($ls[$i] -match '组 [A-Z] ═+$') { $grpIdx = $i; break } }
        if ($grpIdx -lt 0) { $dead += "$proj(找不到组标记)"; continue }
        $decl = $ls[0..($grpIdx - 1)]
        $use  = @($ls[$grpIdx..($ls.Count - 1)] | ForEach-Object { $_ -replace '//.*$', '' }) -join "`n"
        foreach ($d in $decl) {
            if ($d -match '^\s*private\s+(?:static|const)\s+.*?[\s<](\w+)(?:<\w+>)?\s*(?:\(|=>|\{|\s*=)') {
                $nm = $Matches[1]
                if ($use -cnotmatch "\b$nm\b") { $dead += "$proj/$nm" }
            }
        }
    }
    CK ($dead.Count -eq 0) ("R-9 零死包装：每个转发包装都被本文件的组体/入口引用（死包装：$(if($dead.Count){$dead -join ','}else{'0 个'})）")

    # ══════════════ R-10：双拷贝等值锁（旧宿主 vs UiHostShared）══════════════
    function Get-MethodBodyLines([string[]]$ls, [string]$sigRegex) {
        $s = -1
        for ($i = 0; $i -lt $ls.Count; $i++) { if ($ls[$i] -match $sigRegex) { $s = $i; break } }
        if ($s -lt 0) { return $null }
        $depth = 0; $seen = $false
        for ($i = $s; $i -lt $ls.Count; $i++) {
            $code = ($ls[$i] -replace '//.*$', '')
            foreach ($ch in $code.ToCharArray()) {
                if ($ch -eq '{') { $depth++; $seen = $true }
                elseif ($ch -eq '}') { $depth--; if ($seen -and $depth -eq 0) { return @($ls[($s + 1)..($i - 1)]) } }
            }
        }
        return $null
    }
    $old  = [System.IO.File]::ReadAllLines((Join-Path $root $srcTruth))
    $base = [System.IO.File]::ReadAllLines((Join-Path $root 'tests/projects/UiHostShared/UiHost.cs'))
    $bOld  = Get-MethodBodyLines $old  '^\s*(?:private\s+)?static\s+bool\s+SelectColorSpaceSettled\('
    $bBase = Get-MethodBodyLines $base '^\s*public\s+static\s+bool\s+SelectColorSpaceSettled\('
    CK ($bOld -ne $null -and $bBase -ne $null) "R-10 前置：两份 SelectColorSpaceSettled 都解析到方法体（旧宿主 $(if($bOld){$bOld.Count}else{'null'}) 行 / UiHost $(if($bBase){$bBase.Count}else{'null'}) 行）"
    if ($bOld -and $bBase) {
        $diffCnt = 0; $firstDiff = ''
        $n = [Math]::Max($bOld.Count, $bBase.Count)
        for ($i = 0; $i -lt $n; $i++) {
            $x = if ($i -lt $bOld.Count)  { $bOld[$i].Trim() }  else { '<EOF>' }
            $y = if ($i -lt $bBase.Count) { $bBase[$i].Trim() } else { '<EOF>' }
            if ($x -cne $y) { $diffCnt++; if (-not $firstDiff) { $firstDiff = "行$($i+1): old=[$x] base=[$y]" } }
        }
        CK ($diffCnt -eq 0 -and $bOld.Count -eq $bBase.Count) ("R-10 牙齿：双拷贝逐行相等（$($bOld.Count) 行，差异 $diffCnt 行$(if($firstDiff){'；'+$firstDiff}else{''})）")
    }

    # ══════════════ R-11：旧宿主 Skip 的形状锁 ══════════════
    $bSkip = Get-MethodBodyLines $old '^\s*(?:private\s+)?static\s+void\s+Skip\('
    if ($bSkip) {
        $t = ($bSkip -join "`n")
        $prints = $t -match '\[SKIP\]'
        $counts = $t -match '_skip\+\+'
        $touchesPf = ($t -match '_pass\+\+') -or ($t -match '_fail\+\+')
        CK ($prints -and $counts -and -not $touchesPf) "R-11 牙齿：旧宿主 Skip 打 [SKIP] + 计 _skip + **不动** _pass/_fail（打印=$prints 计数=$counts 误动=$touchesPf）"
    } else {
        CK $false "R-11 牙齿：旧宿主里解析到 Skip 方法体（没解析到 ⇒ 参照件没有 Skip，J1z 的修复无从落地）"
    }
}
finally {
    if (-not $LeaveScratch) {
        foreach ($d in @($scratch, $clean, $mutant)) { Remove-Tree $d | Out-Null }
        if ($mutSrc -and (Test-Path $mutSrc)) { Remove-Item -Force $mutSrc -ErrorAction SilentlyContinue }
        $left = @(Get-ChildItem -LiteralPath (Join-Path $root 'tests/output/step2') -Directory -Filter 'uigen*' -ErrorAction SilentlyContinue).Count
        if ($left) { Write-Host ("  [info] 有 {0} 份 scratch 没删净（Windows 句柄滞留，下次开局会清扫）" -f $left) -ForegroundColor DarkGray }
    } else { Write-Host "scratch kept: $scratch / $clean / $mutant" -ForegroundColor DarkGray }
}

Write-Host "===== ui-split-reproducible: PASS=$pass FAIL=$fail =====" -ForegroundColor Cyan
if ($fail -gt 0) { exit 1 } else { exit 0 }
