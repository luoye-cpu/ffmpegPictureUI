# ═══════════════════════════════════════════════════════════════════════════════
# _verify-ui-split-verbatim.ps1 —— 证明拆出的组体**逐字**等于旧宿主（2026-10-05）
#
# 判据：对 17 个组，逐行比较「旧宿主 Program.cs 里该组方法的行」与
#       「新工程 Group.cs 里同名方法的行」，必须**完全相等**（含缩进/全角标点）。
# 为什么需要它：5 条 UI 门禁 grep 断言文本；手抄必然漂移。这个脚本把"逐字"
#       从"声称"变成"可复核"。
# ═══════════════════════════════════════════════════════════════════════════════

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $root

$src = Get-Content 'tests/UiTestHost/Program.cs' -Encoding utf8

# 旧宿主里每个 GroupX_ 的行区间
$starts = [ordered]@{}
for ($i = 0; $i -lt $src.Count; $i++) {
    if ($src[$i] -match '^\s*static\s+void\s+(Group[A-Z]_[A-Za-z0-9_]+)\s*\(') { $starts[$Matches[1]] = $i }
}
$names = @($starts.Keys)

# ⚠ 与生成器同口径：方法体结束 = 与之配对的右花括号行（花括号配平扫描）。
#   不能取"下一组起始 - 1"：**最后一个组**（N）没有下一组，取到 EOF 会把其后的
#   RunNegativeControl / Snip / 类与命名空间右花括号一并算作"组体"，
#   而生成文件（正确地）不含它们 ⇒ 假报 16 条不一致（实测踩过）。
function Get-MethodEnd([int]$startLine) {
    $depth = 0; $seen = $false
    for ($i = $startLine - 1; $i -lt $src.Count; $i++) {
        $code = ($src[$i] -replace '//.*$', '')
        foreach ($ch in $code.ToCharArray()) {
            if ($ch -eq '{') { $depth++; $seen = $true }
            elseif ($ch -eq '}') {
                $depth--
                if ($seen -and $depth -eq 0) { return $i + 1 }
            }
        }
    }
    return $src.Count
}

$bounds = @{}
for ($k = 0; $k -lt $names.Count; $k++) {
    $s = $starts[$names[$k]]
    $e = Get-MethodEnd $s
    if ($k + 1 -lt $names.Count -and $e -ge $starts[$names[$k + 1]]) { $e = $starts[$names[$k + 1]] - 1 }
    $g = ($names[$k] -replace 'Group(\w)_.*', '$1')
    $bounds[$g] = @($src[$s..($e - 1)])   # ⚠ $s 是 0-based（$starts 存的是 $i）
}

$map = [ordered]@{
    'ui-core'  = @('A', 'B', 'C', 'D', 'N')
    'ui-mode'  = @('E', 'F', 'G', 'O')
    'ui-color' = @('H', 'I', 'K', 'R')
    'ui-sweep' = @('J', 'L')
    'ui-cli'   = @('M', 'P')
}

$pass = 0; $fail = 0; $script:waivedGroupHits = 0; $script:comparedGroups = 0
function CK([bool]$ok, [string]$msg) {
    if ($ok) { Write-Host "PASS $msg" -ForegroundColor Green; $script:pass++ }
    else     { Write-Host "FAIL $msg" -ForegroundColor Red;   $script:fail++ }
}

Write-Host "===== ui-split-verbatim =====" -ForegroundColor Cyan

$totalLines = 0
foreach ($p in $map.Keys) {
    $file = "tests/$p/$($p -replace '-','')Group.cs"
    if (-not (Test-Path $file)) { CK $false "$file exists"; continue }
    $gen = Get-Content $file -Encoding utf8

    foreach ($g in $map[$p]) {
        $orig = $bounds[$g]
        # 在生成文件里定位同名方法
        $gi = -1
        for ($i = 0; $i -lt $gen.Count; $i++) {
            if ($gen[$i] -match "^\s*static\s+void\s+Group$($g)_") { $gi = $i; break }
        }
        if ($gi -lt 0) { CK $false "$p : Group$($g)_ 方法存在"; continue }

        $bad = @()
        # ⚠ 两侧都从**签名行**开始比（旧宿主侧 $bounds[$g][0] 就是 `static void GroupX_…`）：
        #   生成器把组体整段搬来（含方法前的注释块归上一组/本组由生成器另行排版），
        #   故比较窗口从签名行起算 —— 与 Get-MethodEnd 的结束边界配对。
        # ── 有意偏离组登记表：**2026-10-12 批次 G 清空**─────────────────────────────
        # 本锁的原始契约是「拆分 = 逐字复制」。2026-10-07 曾在 J1z/J8 上破例：修复只改在
        #   拆分侧、没回灌旧宿主 ⇒ 只能整组放行 J（放行粒度=组，见下方历史教训），代价是
        #   **J 组 1000+ 行从此不进逐字比较**，而旧宿主那一份还在跑**改之前**的 J1z/J8。
        # 现已把 J1z 的「环境前置改 Skip + 无条件形态断言」与 J8 的「等异步联动落地再读」
        #   **回灌进旧宿主**（`tests/UiTestHost/Program.cs`），登记表归零 ⇒ 17 组全部逐字比较。
        # ⚠ 以后若真需要放行：**必须先让两份拷贝各自成立**，再在此登记并同批改下面那条
        #   `$waivedGroups.Count -eq 0` 的反控（改锁要显式、可审，不许静默扩大放行面）。
        $waivedGroups = @{}
        if ($waivedGroups.ContainsKey($g)) {
            $script:waivedGroupHits++
            Write-Host ("  [info] {0} / Group{1}_ 属**已登记的有意偏离组** ⇒ 跳过逐字比较：{2}" -f $p, $g, $waivedGroups[$g]) -ForegroundColor DarkGray
        } else {
            $script:comparedGroups++
            for ($j = 0; $j -lt $orig.Count; $j++) {
                $o = $orig[$j]
                $n = if ($gi + $j -lt $gen.Count) { $gen[$gi + $j] } else { '<EOF>' }
                if ($o -cne $n) { $bad += "  line $($j+1):`n    old=[$o]`n    new=[$n]" }
            }
        }
        $totalLines += $orig.Count
        CK ($bad.Count -eq 0) ("$p / Group{0}_ —— {1} 行逐字一致{2}" -f $g, $orig.Count,
            $(if ($bad.Count) { "（差异 $($bad.Count) 行）`n" + ($bad -join "`n") } else { '' }))
    }
}

# ── 反控：判据段必须真的读到东西（防路径错 ⇒ 0 行也"全绿"）──────────────────
# ⚠ 阈值口径：改用**花括号配平**的真实方法体后，尾部空行不再计入
#   （旧口径 3331 行里有 ~340 行是组间空行/下一组前导注释）。17 个组的真实
#   方法体合计 2991 行 ⇒ 门槛取 2800（留出正常维护余量，仍远高于"路径错=0"）。
CK ($totalLines -gt 2800) "反控：比较了 $totalLines 行（>2800，防路径错导致空比）"
CK ($bounds.Count -eq 17) "反控：旧宿主里解析出 17 个组（实 $($bounds.Count)）"
# ⚠ 偏离登记表必须**为空**（2026-10-12 起）：一旦有人往里加组，本条立刻红，
#   逼着"要不要放行"这件事在门禁输出里可见，而不是悄悄少比一个组。
CK ($waivedGroups.Count -eq 0) "登记表为空：没有任何组被允许跳过逐字比较（实 $($waivedGroups.Count) 个）"
CK ($script:waivedGroupHits -eq 0) "实际跳过 0 个组（实 $($script:waivedGroupHits)）⇒ 17 组全部逐字比较"
CK ($comparedGroups -eq 17) "反控：真正逐字比较了 17 个组（实 $($comparedGroups)）"

Write-Host "===== ui-split-verbatim: PASS=$pass FAIL=$fail =====" -ForegroundColor Cyan
if ($fail -gt 0) { exit 1 } else { exit 0 }
