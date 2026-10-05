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

# ── ③ 组表里每个 mode 都必须是**真的**（三重取证，防拼错）────────────────────
#     ⚠⚠ 2026-10-05 修订：原先只认「在 `$Probes` 或出现在某个 tests/scripts/*.ps1 的文本里」。
#       该口径对 `ServiceProbe` 那批成立（它们要么在 `$Probes`、要么被专项脚本直接调），
#       但对**新拆出来的 UI/宿主组**是**误伤**：那些组的 mode 是各自 exe 的 CLI 参数，
#       由**组工程自身**承载，既不必进 `$Probes`、也不出现在任何 ps1 文本里。
#       ⇒ 按本仓铁律「**不得把正确的构造判红**」，把取证扩为**任一**成立即可：
#         (a) 在 `$Probes` 默认清单里（ServiceProbe 侧的正常归属）；
#         (b) 出现在任何 `tests/scripts/*.ps1` 的文本里（专项门禁脚本直接调用的 20 个 mode）；
#         (c) **所属组有自己的工程目录与 csproj**，且该 mode 名是**该组 `Program.cs` 里真实分派的分支**
#             —— 这是 UI/宿主组这一类"mode 由组自身定义"的正确取证方式。
#       ⚠ 判据**没有被放宽**：拼错的 mode 名（如 `simdswtich`）仍会 (a)(b)(c) 全不命中而转红
#         （变异验证保留，见下方 ③ 的验证记录）。原来的 ③ 依旧拦得住它要拦的东西。
$allScripts = ''
foreach ($f in (Get-ChildItem 'tests/scripts' -Filter '*.ps1' -File)) {
    $allScripts += (Get-Content $f.FullName -Raw) + "`n"
}
# 预先读入每个组的 Program.cs（(c) 的证据源）
$groupProgram = @{}
foreach ($g in $groupMap.Keys) {
    $pf = "tests/$g/Program.cs"
    if (Test-Path $pf) { $groupProgram[$g] = Get-Content $pf -Raw }
}
$unregistered = @()
foreach ($kv in $groupMap.GetEnumerator()) {
    $gname = $kv.Key
    foreach ($mode in $kv.Value) {
        if ($mode -in $probes) { continue }                                   # (a)
        if ($allScripts -match [regex]::Escape($mode)) { continue }           # (b)
        # (c) 该 mode 必须是本组 Program.cs 里真实出现的分派分支
        if ($groupProgram.ContainsKey($gname) -and
            $groupProgram[$gname] -match ('"' + [regex]::Escape($mode) + '"')) { continue }
        $unregistered += "$gname/$mode"
    }
}
CK ($unregistered.Count -eq 0) "③ 组表每个 mode 都真实存在（三重取证 a/b/c 全不命中：$(if ($unregistered.Count) { $unregistered -join ', ' } else { '无' })）"

# ── ⑤ 声明的 mode 必须**真的被分派**（2026-10-05 审查补：堵住"声明了却没实现"）──────
#     ⚠ 立这条的动机（审查实测发现的**真实覆盖缺口**）：`tests/Tests.Shared/TestGroups.cs`
#       给 `engine` 组声明了 4 个 mode（`engine`/`gamut`/`gamutfit`/`simdswitch`），
#       但 `tests/engine/Program.cs` 当初**只分派了 `gamutfit`** ⇒ 其余三个被"接受"却
#       **静默跑 0 条断言**：`engine` 旧宿主 35/0 → 新组 **0/0**、`simdswitch` 28/0 → **0/0**。
#       ③ 抓不到它：③ 只要求 mode **作为引号字面量出现**，而 `--list` 输出里就有它
#       ⇒ 属"形式上存在、实质上没人跑"，正是本仓最忌的**假覆盖**。
#     ⇒ 判据：每个声明的 mode 必须在**本组 `Program.cs` 的 `mode ==` 比较**里出现
#       （即真的进了分派链），而非仅出现在 `--list`/注释/字符串里。
#     ⚠ 反控：判据段必须真的解析到 ≥1 个 `mode ==` 比较（防正则失配导致恒绿）。
$notDispatched = @()
$dispatchParsed = 0
foreach ($kv in $groupMap.GetEnumerator()) {
    $gname = $kv.Key
    if (-not $groupProgram.ContainsKey($gname)) { continue }
    $prog = $groupProgram[$gname]
    foreach ($mode in $kv.Value) {
        # 真实分派形态： mode == "xxx"   或   mode is "--list" or "-l" 之外的单串比较
        if ($prog -match ('mode\s*==\s*"' + [regex]::Escape($mode) + '"')) { $dispatchParsed++; continue }
        $notDispatched += "$gname/$mode"
    }
}
CK ($dispatchParsed -ge 40) "⑤-a 反控：解析到 ≥40 个 `mode ==` 分派（实 $dispatchParsed）—— 防正则失配导致恒绿"
CK ($notDispatched.Count -eq 0) "⑤ 组表每个 mode 都**真的被分派**（声明了却没实现：$(if ($notDispatched.Count) { $notDispatched -join ', ' } else { '无' })）"

# ── 信息行：两边的计数与差集（便于人看，不参与判据）────────────────────────
Write-Host ("ℹ      组表并集 = {0} 个 mode；运行器 `$Probes = {1} 个" -f $union.Count, $probes.Count)
$onlyRunner = @($probes | Where-Object { $_ -notin $union })
$onlyGroups = @($union | Where-Object { $_ -notin $probes })
Write-Host ("ℹ      仅运行器有（组表缺，应为 0）= {0}" -f $onlyRunner.Count)
Write-Host ("ℹ      仅组表有（由专项门禁脚本调用，正常）= {0}：{1}" -f $onlyGroups.Count, ($onlyGroups -join ', '))

Write-Host "===== test-group-coverage: PASS=$pass FAIL=$fail =====" -ForegroundColor Cyan
if ($fail -gt 0) { exit 1 } else { exit 0 }
