# ═══════════════════════════════════════════════════════════════════════════════
# _probe-test-group-coverage.ps1 —— 测试组覆盖一致性（2026-10-05 物理拆分时新增）
#
# 【为什么必须有】
#   拆分后「哪些 mode 归哪个组」是**手工维护**的表（`tests/Tests.Shared/TestGroups.cs`），
#   而运行器 `_run-step3-gates.ps1` 另有一份**独立**的默认探针清单（`$Probes`，26 个）。
#   两者一旦漂移，就会出现「运行器要跑某个 mode，但**没有任何组**认领它」
#   ⇒ 该 mode **永远不被任何组跑到** = **假覆盖**（看板上一切正常，实际没人跑）。
#   本仓有第 54 条的前车之鉴：`_probe-settings-clone-coverage.ps1` **存在但没上清单** ⇒ 谁也不跑。
#
# 【判据】
#   ① **运行器 `$Probes` 的每一个 mode 都必须至少被一个组认领**（否则转红并点名）。
#   ② `TestGroups` 里出现的每个组名都必须有**对应的工程目录**（否则转红）——
#      防「表里写了组、工程没建」这种"宣称≠交付"。
#   ③ `TestGroups` 里每个组声明的 mode 都必须在运行器里有据可查（在 `$Probes`，
#      或在运行器/门禁脚本的注释与调用里出现）—— 防拼错 mode 名（拼错即无人跑）。
#   ④ **反控**：判据段本身必须真的读到非空的组表与探针清单（防被清空成恒绿）。
#
# 【口径说明】③只要求"有据可查"而不是"必须在 $Probes"：本仓有 20 个 mode 是**专项门禁脚本**
#   直接调用的（如 `bitdepth`/`faithful`/`orientation`/`xcheck`），它们**不在** `$Probes` 默认清单里，
#   但确实有人跑。把它们判红是**误伤**（本仓明令：不得把正确的构造判红）。
# ═══════════════════════════════════════════════════════════════════════════════

$ErrorActionPreference = 'Continue'
Set-Location (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))

$pass = 0; $fail = 0
function CK([bool]$ok, [string]$msg) {
    if ($ok) { Write-Host "PASS $msg" -ForegroundColor Green; $script:pass++ }
    else     { Write-Host "FAIL $msg" -ForegroundColor Red;   $script:fail++ }
}

$groupsCs   = 'tests/Tests.Shared/TestGroups.cs'
$runnerPs1  = 'tests/scripts/_run-step3-gates.ps1'

Write-Host "===== test-group-coverage =====" -ForegroundColor Cyan

# ── 前置（fail-closed：缺文件即红，不 SKIP）──────────────────────────────────
foreach ($p in @($groupsCs, $runnerPs1)) {
    if (-not (Test-Path $p)) { CK $false "前置：$p 存在（缺它无法判定覆盖）"; Write-Host "===== PASS=$pass FAIL=$fail ====="; exit 1 }
}

# ── 读组表：从 TestGroups.cs 解析 `["组名"] = new[] { "m1", "m2" },` ──────────
$gText = Get-Content $groupsCs -Raw
$groupMap = @{}
foreach ($m in [regex]::Matches($gText, '\["(?<g>[a-z0-9\-]+)"\]\s*=\s*new\[\]\s*\{(?<modes>[^}]*)\}')) {
    $g = $m.Groups['g'].Value
    $modes = [regex]::Matches($m.Groups['modes'].Value, '"([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
    $groupMap[$g] = @($modes)
}

# ── 读运行器的 $Probes 默认清单 ─────────────────────────────────────────────
$rText = Get-Content $runnerPs1 -Raw
$pm = [regex]::Match($rText, "param\(\[string\]\`$Probes\s*=\s*'(?<list>[^']+)'")
if (-not $pm.Success) {
    CK $false "前置：能在运行器里解析出 `$Probes 默认清单（解析失败 ⇒ 判红，不静默放过）"
    Write-Host "===== PASS=$pass FAIL=$fail ====="; exit 1
}
$probes = @($pm.Groups['list'].Value -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })

# ── ④ 反控：判据段必须真的读到东西 ─────────────────────────────────────────
CK ($groupMap.Count -ge 9) "④-a 反控：解析到 ≥9 个组（实 $($groupMap.Count)）—— 防组表被清空成恒绿"
CK ($probes.Count -ge 26)  "④-b 反控：解析到 ≥26 个运行器探针（实 $($probes.Count)）—— 防 `$Probes 被清空成恒绿"

$union = @($groupMap.Values | ForEach-Object { $_ } | Sort-Object -Unique)
CK ($union.Count -ge 46) "④-c 反控：组表 mode 并集 ≥46（实 $($union.Count)）"

# ── ① 运行器的每个 mode 都必须被某组认领 ───────────────────────────────────
$unowned = @($probes | Where-Object { $_ -notin $union })
CK ($unowned.Count -eq 0) "① 运行器 `$Probes 的每个 mode 都有组认领（未认领：$(if ($unowned.Count) { $unowned -join ', ' } else { '无' })）"

# ── ② 组表里的每个组都必须有对应工程目录 ───────────────────────────────────
$missingProj = @()
foreach ($g in $groupMap.Keys) {
    # 工程目录名 = tests/<组名>（与 TestGroups 的键同名，见 docs/TEST_SPLIT_TEMPLATE.md）
    if (-not (Test-Path "tests/$g")) { $missingProj += $g }
}
CK ($missingProj.Count -eq 0) "② 组表里每个组都有工程目录（缺目录：$(if ($missingProj.Count) { $missingProj -join ', ' } else { '无' })）"

# ── ③ 组表里每个 mode 都必须"有据可查" ─────────────────────────────────────
#     有据可查 = 在 $Probes 里，**或**在整个 tests/scripts/ 里的任何脚本文本中出现过
#     （专项门禁脚本直接调用的那 20 个 mode 属于这一类）。
$allScripts = ''
foreach ($f in (Get-ChildItem 'tests/scripts' -Filter '*.ps1' -File)) {
    $allScripts += (Get-Content $f.FullName -Raw) + "`n"
}
$unregistered = @()
foreach ($mode in $union) {
    if ($mode -in $probes) { continue }
    if ($allScripts -match [regex]::Escape($mode)) { continue }
    $unregistered += $mode
}
CK ($unregistered.Count -eq 0) "③ 组表每个 mode 都有据可查（既不在 `$Probes、也没被任何脚本调用：$(if ($unregistered.Count) { $unregistered -join ', ' } else { '无' })）"

# ── 信息行：两边的计数与差集（便于人看，不参与判据）────────────────────────
Write-Host ("ℹ      组表并集 = {0} 个 mode；运行器 `$Probes = {1} 个" -f $union.Count, $probes.Count)
$onlyRunner = @($probes | Where-Object { $_ -notin $union })
$onlyGroups = @($union | Where-Object { $_ -notin $probes })
Write-Host ("ℹ      仅运行器有（组表缺，应为 0）= {0}" -f $onlyRunner.Count)
Write-Host ("ℹ      仅组表有（由专项门禁脚本调用，正常）= {0}：{1}" -f $onlyGroups.Count, ($onlyGroups -join ', '))

Write-Host "===== test-group-coverage: PASS=$pass FAIL=$fail =====" -ForegroundColor Cyan
if ($fail -gt 0) { exit 1 } else { exit 0 }
