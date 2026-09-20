# _probe-ct-chain-closure-scan.ps1 —— 结构锁 ①：`ct` 链闭合（「持有 ct 却漏传」）
#
# 为什么需要（缺陷形态）：
#   `#23` 只读审计实测：`src/FfmpegGui` 下**直连 `Process.Start(` 共 65 处**，其中 **18 处**
#   是「**调用点所在方法自己持有 `CancellationToken`**（形参 `ct` / 体内 `linkedToken`），
#   却在调用下一跳时**没把 `ct` 交出去**」⇒ 用户按「停止」按不动那一跳里的子进程。
#   （该 18 处的清单与逐字锚点见 `docs/HANDOVER.md` ⑨ ③；`#26` 逐处接线。）
#
#   ⚠ **为什么还要另立这一条**（现有锁的扫描面缺口）：
#     · `_probe-cancel-propagation-scan.ps1` 的扫描面**只有** `FfmpegRunner.RunAsync` 调用点；
#     · `_probe-sync-read-before-wait-scan.ps1` 管的是「同步读有没有架空超时」；
#     ⇒ **没有任何一条锁**管「持有 ct 的方法有没有把 ct 交下去」。本锁补这一格。
#
# 判据（**只有一条**，刻意不扩）：
#   同一文件内，一处调用满足**全部**三条 ⇒ 命中：
#     ① 被调方法在**同文件内**有**唯一**同名定义，且其**形参表**含 `CancellationToken`；
#     ② 该调用的**实参表**里**没有**交出 `ct` / `linkedToken` / `*.Token` / `token`（含具名实参 `ct: ct`）；
#     ③ 该调用点**所在方法自己持有** ct —— 形参表含 `CancellationToken`，或方法体内声明了
#        `var linkedToken = ...`（即「持有者」）。
#   ⚠ **条件 ③ 是本锁的收窄，不是疏漏**：它把 ③b（全链无 `ct`，如 `Program.Main` / Avalonia 事件处理器
#     / 同步命令生成链 `BuildArguments`）**排除在判据面外** —— 那些地方**没有 ct 可交**，
#     把「漏传」算到它们头上会让断言变糊（`docs/TESTING.md §6` 第 74 条）。
#     ⇒ 已知落在外面的 ③a（**隔跳**形态，如 `IsAnimatedJxl` ← `InputMayHaveMultipleFramesAsync(item, ct)`
#       ← `ProcessAsync(ct)`）本锁**不表态**，见文件尾「缺口」。
#       ⚠ 2026-09-18（`#136` 切片 B2 之后）更正：该中间跳**已带 `ct` 形参**
#         （`InputMayHaveMultipleFramesAsync(QueueItem, CancellationToken)`）⇒ 本例**现在其实已闭合**；
#         保留它只作「隔跳形态」的**形状举例**，**不再**是缺口实例（旧文本写「无形参」已过期）。
#   ⚠ 判据只认**代码**、**不认注释**（整行 `//`、行尾 `//`、`/* */` 一律剥掉）——`#26` 在源码里
#     逐字引用了「旧写法没传 ct」这类文本，不剥注释会造**假红**。负控 `skip_comment.cs` 钉住这一点。
#   ⚠ 同名方法**不唯一**时**判不了** ⇒ 记入**跳过数**、不参与命中（负控 `skip_overload.cs` 钉住）。
#
# 基线口径：**不得增加**，不是「必须为 0」（`§6` 第 55 条同族教训：长期红的锁会被无视）。
#   ⚠ 基线处**显式登记**：见下方 `$BaseSites` 的注释（含残留清单的出处）。
#   降了**不判红**，但会打印**余量**让进度可见。
#
# 用法：pwsh -NoProfile -File tests/scripts/_probe-ct-chain-closure-scan.ps1（非 0 退出 = 判据被违反）
#      报告落 tests/output/_probe-ct-chain-closure.txt（可 -Report 覆盖）
param([string]$Report = '')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Report) { $Report = Join-Path $root 'tests/output/_probe-ct-chain-closure.txt' }

# 基线（**不得增加**）：命中站点数。
#   ⚠ 登记口径：数值由**实测**得出（`#26` 收尾时双宿主各跑一次，读数写进 `docs/TESTING.md §6` 与
#     `docs/HANDOVER.md` ⑨）。本锁**不做跨跳上溯**（见文件头条件③），因此它钉住的是
#     「**近端**持有者漏传」这一类；隔跳 ③a 另在文档里登记，不靠本锁兜。
$BaseSites = 0

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

# ── 工具：方法头（起点行 / 结束行 / 名字 / 形参表）────────────────────────────────
# 方法头可能跨多行（参数表换行）⇒ 从起点行向后最多并 8 行，直到见到 `{`。
# ⚠ 名字必须取**方法名**而不是返回类型：`public static (string a, string b) Foo(` 的**第一个** `(`
#   属于**元组返回类型** ⇒ 先剥修饰符，再剥元组返回类型，剩下的第一个「标识符 + 可选泛型 + (」才是方法名。
function Get-MethodHeads([string[]]$L) {
    $heads = @()
    for ($i = 0; $i -lt $L.Count; $i++) {
        if ($L[$i] -match '^\s{2,}(?:private|public|internal|protected)[^=;]*\(' -and $L[$i] -notmatch '=>') {
            $buf = $L[$i]
            $j = $i
            while (($j + 1) -lt $L.Count -and ($j - $i) -lt 8 -and ($buf -notmatch '\{')) {
                $j++
                $buf = $buf + ' ' + $L[$j]
            }
            $h = $buf.Trim()
            $modRe = '^(?:private|public|internal|protected|static|async|partial|override|virtual|sealed|new|unsafe|extern|abstract|readonly)\b\s*'
            while ($h -match $modRe) { $h = ($h -replace $modRe, '').Trim() }
            if ($h.StartsWith('(')) {
                # 元组返回类型：跳到匹配的 ')'
                $d = 0; $p = 0
                while ($p -lt $h.Length) {
                    $c = $h[$p]
                    if ($c -eq '(') { $d++ }
                    elseif ($c -eq ')') { $d--; if ($d -eq 0) { $p++; break } }
                    $p++
                }
                $h = $h.Substring($p).Trim()
            }
            $name = ''
            $params = ''
            $mm = [regex]::Match($h, '([A-Za-z_]\w*)\s*(?:<[^<>]*>)?\s*\(')
            if ($mm.Success) {
                $name = $mm.Groups[1].Value
                $open = $h.IndexOf('(', $mm.Groups[1].Index + $mm.Groups[1].Length)
                if ($open -ge 0) {
                    $d = 0; $k = $open
                    while ($k -lt $h.Length) {
                        $c = $h[$k]
                        if ($c -eq '(') { $d++ }
                        elseif ($c -eq ')') { $d--; if ($d -eq 0) { break } }
                        $k++
                    }
                    if ($k -lt $h.Length) { $params = $h.Substring($open + 1, $k - $open - 1) }
                }
            }
            $heads += @{ start = $i; end = $j; name = $name; params = $params }
        }
    }
    return $heads
}

function Get-HeadAt([array]$heads, [int]$lineIdx) {
    $cur = $null
    for ($k = 0; $k -lt $heads.Count; $k++) {
        if ($heads[$k].start -gt $lineIdx) { break }
        $cur = $heads[$k]
    }
    return $cur
}

function Test-HoldsCt([array]$heads, [array]$L, [int]$lineIdx) {
    $hd = Get-HeadAt $heads $lineIdx
    if ($hd -eq $null) { return $false }
    if ($hd.params -match 'CancellationToken') { return $true }
    # 体内声明过 `var linkedToken = ...`（或 `var linked = CreateLinkedTokenSource` 后取 `.Token`）
    $to = $L.Count - 1
    for ($k = 0; $k -lt $heads.Count; $k++) {
        if ($heads[$k].start -gt $lineIdx) { $to = $heads[$k].start - 1; break }
    }
    $body = (@($L[$hd.start..$to]) -join "`n")
    return ($body -match '\blinkedToken\b' -or $body -match 'CreateLinkedTokenSource')
}

# ── 判据本体：抽取调用实参表（括号配平）────────────────────────────────────────────
function Get-CallArgs([string]$line, [int]$openIdx, [ref]$closed) {
    $d = 0; $k = $openIdx
    while ($k -lt $line.Length) {
        $c = $line[$k]
        if ($c -eq '(') { $d++ }
        elseif ($c -eq ')') { $d--; if ($d -eq 0) { break } }
        $k++
    }
    if ($k -ge $line.Length) { $closed.Value = $false; return '' }
    $closed.Value = $true
    return $line.Substring($openIdx + 1, $k - $openIdx - 1)
}

# 实参里有没有「交出 ct」——`ct` / `linkedToken` / `x.Token` / `token` / 具名 `ct:` / `cancellationToken:`
function Test-PassedCt([string]$argText) {
    if ($argText -match '\bct\s*:') { return $true }
    if ($argText -match '\bcancellationToken\s*:') { return $true }
    if ($argText -match '\blinkedToken\b') { return $true }
    if ($argText -match '\btoken\b') { return $true }
    if ($argText -match '\.Token\b') { return $true }
    if ($argText -match '(^|[,\(\s])ct\s*($|[,\)\s])') { return $true }
    return $false
}

# ── 扫描一个语料目录 ────────────────────────────────────────────────────────────
function Scan-Corpus([string]$dir, [string]$label) {
    $acc = @{ files = 0; targets = 0; inface = 0; hits = @()
              skipNoCt = 0; skipNoHolder = 0; skipUnbalanced = 0; skipDupName = 0; skipHead = 0 }
    # ⚠ 必须排除 `bin/` `obj/`（与 `_probe-sync-read-before-wait-scan.ps1` 同口径）：
    #   不过滤会把生成物算进扫描面 ⇒ 「扫描文件数」这个读数就不诚实了。
    $files = @(Get-ChildItem -Recurse -Path $dir -Filter *.cs -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
    $acc.files = $files.Count
    foreach ($f in $files) {
        $rel = $f.FullName
        if ($label -ne 'TEMP') { $rel = $f.FullName.Substring($root.Length + 1) }
        $code = Get-CodeText ([System.IO.File]::ReadAllText($f.FullName))
        $L = @($code -split "`n")
        $heads = @(Get-MethodHeads $L)

        # 同文件内「有 ct 形参」的方法名 ⇒ 形参表；**同名不唯一**的记入 skipDupName，不参与判据
        $map = @{}
        $dup = @{}
        foreach ($hd in $heads) {
            if ($hd.name -eq '') { continue }
            if ($map.ContainsKey($hd.name)) {
                $dup[$hd.name] = $true
            } else {
                $map[$hd.name] = $hd.params
            }
        }
        foreach ($nm in $dup.Keys) { if ($map.ContainsKey($nm)) { $map.Remove($nm) } }
        $acc.skipDupName += $dup.Count
        $acc.targets += $map.Count

        for ($i = 0; $i -lt $L.Count; $i++) {
            $line = $L[$i]
            # 方法头所在行不参与（否则定义处会被当成调用点）
            $inHead = $false
            foreach ($hd in $heads) { if ($i -ge $hd.start -and $i -le $hd.end) { $inHead = $true; break } }
            foreach ($nm in $map.Keys) {
                $re = '\b' + [regex]::Escape($nm) + '\s*\('
                foreach ($m in [regex]::Matches($line, $re)) {
                    if ($inHead) { $acc.skipHead++; continue }
                    $open = $line.IndexOf('(', $m.Index + $nm.Length)
                    if ($open -lt 0) { continue }
                    $closed = $true
                    $callArgs = Get-CallArgs $line $open ([ref]$closed)
                    if (-not $closed) { $acc.skipUnbalanced++; continue }
                    # ① 目标有形参 ct？
                    if ($map[$nm] -notmatch 'CancellationToken') { $acc.skipNoCt++; continue }
                    # ③ 调用点所在方法自己持有 ct？
                    $holds = Test-HoldsCt $heads $L $i
                    if (-not $holds) { $acc.skipNoHolder++; continue }
                    $acc.inface++
                    # ② 这一跳交了吗？
                    if (-not (Test-PassedCt $callArgs)) {
                        $owner = '<顶层>'
                        $hd2 = Get-HeadAt $heads $i
                        if ($hd2 -ne $null) { $owner = $hd2.name }
                        $acc.hits += ('{0}:{1}  {2} ⇒ 调 {3}(...) 未交 ct' -f $rel, ($i + 1), $owner, $nm)
                    }
                }
            }
        }
    }
    return $acc
}

try {
    Say "宿主 PowerShell: $($PSVersionTable.PSVersion)  (Edition=$($PSVersionTable.PSEdition))"
    Say "判据：同一文件内 —— 目标有形参 ct + 调用点所在方法**自己持有** ct + 这一跳**没交** ct ⇒ 命中"
    Say "基线（**不得增加**）：命中 = $BaseSites 处"

    # ── ① 真实语料 ──────────────────────────────────────────────────────────────
    $real = Scan-Corpus "$root/src/FfmpegGui" 'REAL'
    Say ""
    Say "── ① 真实语料 src/FfmpegGui（排除 bin/obj）──"
    Say ("  扫描 .cs 文件：{0} 个" -f $real.files)
    Say ("  同文件唯一「有 ct 形参」的方法名：{0} 个" -f $real.targets)
    Say ("  判据面内站点（目标有 ct 形参 **且** 调用点持有 ct）：{0} 处" -f $real.inface)
    Say ("  命中判据（持有 ct 却没交）：{0} 处" -f $real.hits.Count)
    if ($real.hits.Count -gt 0) {
        Say "  ── 命中明细 ──"
        $real.hits | ForEach-Object { Say "    $_" }
    }
    Say ("  ── 跳过数（**必须打印**，否则跳过会伪装成干净）──")
    Say ("     目标方法无 ct 形参（不在判据面内）      ：{0} 处" -f $real.skipNoCt)
    Say ("     调用点所在方法**不持有** ct（③b/跨跳）  ：{0} 处" -f $real.skipNoHolder)
    Say ("     同名方法不唯一（判不了）                ：{0} 个名字" -f $real.skipDupName)
    Say ("     方法头自身（定义处，不是调用）          ：{0} 处" -f $real.skipHead)
    Say ("     实参表括号未配平（判不了）              ：{0} 处" -f $real.skipUnbalanced)

    # ── ② 负控（证明判据**有牙**，且不误伤）─────────────────────────────────────
    # ⚠ 语料一律用运行级 GUID 目录（§6 第 66 条：固定目录会被并发同名实例互删 ⇒ 假红）。
    $nc = Join-Path $env:TEMP ('ctchain_' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $ncEmpty = Join-Path $nc 'empty'
    New-Item -ItemType Directory -Path $nc -Force | Out-Null
    New-Item -ItemType Directory -Path $ncEmpty -Force | Out-Null
    $nl = [char]10
    function Write-Corpus([string]$name, [string[]]$bodyLines) {
        $p = Join-Path $nc $name
        [System.IO.File]::WriteAllText($p, ($bodyLines -join $nl), (New-Object System.Text.UTF8Encoding($false)))
    }

    # ②-1 坏形态：持有者漏传 ⇒ **必须**被抓到
    Write-Corpus 'bad_holdnotpass.cs' @(
        'class T {',
        '    private void Target(string x, System.Threading.CancellationToken ct = default) { }',
        '    private async Task M(System.Threading.CancellationToken ct) {',
        '        await Target(path);',
        '    }',
        '}'
    )
    # ②-2 正确形态：持有者交了 ct ⇒ **不得**误报
    Write-Corpus 'good_pass.cs' @(
        'class T {',
        '    private void Target(string x, System.Threading.CancellationToken ct = default) { }',
        '    private async Task M(System.Threading.CancellationToken ct) {',
        '        await Target(path, ct);',
        '    }',
        '}'
    )
    # ②-3 正确形态：具名实参 `ct: ct` ⇒ **不得**误报
    Write-Corpus 'good_namedarg.cs' @(
        'class T {',
        '    private void Target(string x, System.Threading.CancellationToken ct = default) { }',
        '    private void M(System.Threading.CancellationToken ct) {',
        '        Target(path, ct: ct);',
        '    }',
        '}'
    )
    # ②-4 正确形态：交的是 `linkedToken` ⇒ **不得**误报
    Write-Corpus 'good_linked.cs' @(
        'class T {',
        '    private void Target(string x, System.Threading.CancellationToken ct = default) { }',
        '    private void M(System.Threading.CancellationToken ct) {',
        '        using var linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);',
        '        var linkedToken = linked.Token;',
        '        Target(path, linkedToken);',
        '    }',
        '}'
    )
    # ②-5 判据面外：调用者**不持有** ct（③b）⇒ **不得**误报（没有 ct 可交）
    Write-Corpus 'out_notct.cs' @(
        'class T {',
        '    private void Target(string x, System.Threading.CancellationToken ct = default) { }',
        '    private void M() {',
        '        Target(path);',
        '    }',
        '}'
    )
    # ②-6 判据面外：目标**无** ct 形参 ⇒ **不得**误报
    Write-Corpus 'out_targetnoct.cs' @(
        'class T {',
        '    private void Target(string x) { }',
        '    private void M(System.Threading.CancellationToken ct) {',
        '        Target(path);',
        '    }',
        '}'
    )
    # ②-7 判不了：同名方法不唯一 ⇒ **不得**误报，且必须计入跳过数
    Write-Corpus 'skip_overload.cs' @(
        'class T {',
        '    private void Target(string x, System.Threading.CancellationToken ct = default) { }',
        '    private void Target(string x, int n) { }',
        '    private void M(System.Threading.CancellationToken ct) {',
        '        Target(path);',
        '    }',
        '}'
    )
    # ②-8 坏形态**只在注释里** ⇒ **不得**误报（钉住剥注释）
    Write-Corpus 'skip_comment.cs' @(
        'class T {',
        '    private void Target(string x, System.Threading.CancellationToken ct = default) { }',
        '    private void M(System.Threading.CancellationToken ct) {',
        '        // 旧写法：await Target(path);  ← 没交 ct',
        '        await Target(path, ct);',
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
    $hitBad = @($bad.hits | Where-Object { $_ -match '[\\/]bad_holdnotpass\.cs:' }).Count
    CK-Ctl ($hitBad -eq 1) ("坏形态必须被抓到（bad_holdnotpass.cs，实 {0}）" -f $hitBad)
    # 不误伤：good_pass / good_namedarg / good_linked / out_notct / out_targetnoct / skip_overload / skip_comment 一处都不许命中
    # ⚠ 语料里**只有** bad_holdnotpass.cs 一处合法命中 ⇒ 命中总数应为 1（按文件点算，见 #22 的教训）。
    CK-Ctl ($bad.hits.Count -eq 1) ("正确形态与判据面外形态**一处都没被误报** —— 命中总数 = 1（实 {0}）" -f $bad.hits.Count)
    CK-Ctl ($bad.skipNoHolder -ge 1) ("判据面外（调用者不持有 ct）被正确跳过（实 {0}）" -f $bad.skipNoHolder)
    CK-Ctl ($bad.skipNoCt -ge 1) ("目标无 ct 形参被正确跳过（实 {0}）" -f $bad.skipNoCt)
    CK-Ctl ($bad.skipDupName -ge 1) ("同名方法不唯一被计入跳过数（实 {0}）" -f $bad.skipDupName)
    CK-Ctl ($bad.inface -eq 5) ("判据面内站点计数正确：bad 1 + good_pass 1 + good_namedarg 1 + good_linked 1 + skip_comment 1 = 5（实 {0}）" -f $bad.inface)
    CK-Ctl ($emp.files -eq 0 -and $emp.hits.Count -eq 0 -and $emp.inface -eq 0) `
        ("空语料 ⇒ 0 文件 / 0 判据面内站点 / 0 命中（实 文件 {0} / 面内 {1} / 命中 {2}）" -f $emp.files, $emp.inface, $emp.hits.Count)

    # 清理临时语料（用 .NET API 绕过宿主批量删除守卫，§6 第 57/67 条）
    try { if (Test-Path $nc) { [System.IO.Directory]::Delete($nc, $true) } } catch { }

    # ── ③ 断言 ─────────────────────────────────────────────────────────────────
    Say ""
    Say "── ③ 断言（口径：**不得增加**）──"
    $fail = 0
    if ($real.hits.Count -gt $BaseSites) {
        Say ("  FAIL 命中 {0} 处 > 基线 {1} 处 ⇒ 新增了「持有 ct 却漏传」的站点" -f $real.hits.Count, $BaseSites)
        $fail++
    } else {
        $head = $BaseSites - $real.hits.Count
        Say ("  PASS 命中 {0} 处 ≤ 基线 {1} 处（余量 {2}）" -f $real.hits.Count, $BaseSites, $head)
        if ($head -gt 0) {
            Say ("       提示：余量 {0} 表示已修好 {0} 处 —— 请把基线收紧到 {1}（改本脚本的 `$BaseSites`），否则这把锁会一直松着" -f $head, $real.hits.Count)
        }
    }
    if ($ctlFail.Count -gt 0) {
        Say ("  FAIL 负控 {0} 条未通过 ⇒ 判据**不可信**（有牙/不误伤都必须成立）" -f $ctlFail.Count)
        foreach ($cf in $ctlFail) { Say ('        ' + $cf) }
        $fail++
    } else {
        Say ("  PASS 负控 {0} 条全部通过（有牙：持有者漏传必被抓到；不误伤：交了 ct/具名/linkedToken/判据面外/同名重载/注释内 0 命中；空语料 0/0）" -f $ctl)
    }

    if ($fail -eq 0) {
        Say ""
        Say "PASS `ct` 链**没有新增**「调用点持有 ct 却没交下去」的形态"
        Say ("      ⚠ 本结论**只覆盖**：src/FfmpegGui 下 {0} 个 .cs、判据面内 {1} 处站点（近端持有者）" -f $real.files, $real.inface)
        Say "      ⚠ 它**不**覆盖："
        Say "        · **隔跳** ③a（持有者在更上一跳，如 `IsAnimatedJxl` ← 带 `ct` 的 `InputMayHaveMultipleFramesAsync(item, ct)` ← `ProcessAsync(ct)`）"
        Say "        · ③b（全链无 ct：`Program.Main` / Avalonia 事件处理器 / 同步命令生成链 `BuildArguments`）"
        Say "        · 同名方法不唯一的调用点（判不了，已计入跳过数）"
        Say "      ⚠ 它是**形态判定，未实证取消生效** —— 本锁不跑任何 exe，"
        Say "        不能证明「用户按停止时子进程一定会被终止」。"
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
