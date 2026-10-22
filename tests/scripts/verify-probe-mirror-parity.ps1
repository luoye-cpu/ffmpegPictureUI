# ═══════════════════════════════════════════════════════════════════════════════
# verify-probe-mirror-parity.ps1 —— 探针面「不许再有第二份拷贝」存在锁 + 执行面逐 mode 条数锁
#
# 【2026-10-14 的形态变化】这一条原先是**两份拷贝的漂移尺**（拿 `tests/core/Modes/*.cs` 与
#   `tests/diagnostics/Modes/*.cs` 当参照件，比标签多重集）。用户选定「整体删 5 个镜像工程」后
#   （`tests/{core,color,diagnostics,engine,geometry}` = 31 份 `Modes/*.cs` + 5 份组体/入口 + 5 份
#   csproj，约 7,000 行；那 31 个 mode 改由 `TestGroups.cs` 的 `["ServiceProbe"]` 一组认领），
#   **参照件没了** ⇒ 本门禁换成两件它原来保不住的性质：
#     ② **存在锁**：被删的镜像形态不许回来（目录 / `tests/**/Modes/*.cs` / 切片生成器 / git 索引）。
#     ③ **条数锁**：镜像原先顺带充当"被执行那份有没有丢断言"的**见证人**；删掉后见证人必须换人 ——
#        把 `ServiceProbe/Program.cs` 每个**派发 mode** 的 `Check` 标签条数**逐名钉死**。
#     ④⑤ **剩余双拷贝的登记表 + 方向锁**：`tests/{encode,gainmap,io,metadata}` 这四份工程曾是
#        ServiceProbe 的镜像（15 个同名 `Probe*` 方法，实测无独有标签）。它们不在 10-14 那次授权内，
#        ⇒ 当时把"还剩几处"逐名钉住：**新增**第二份当场红，**合并掉**也必须回来改登记。
#
# 【2026-10-16 的第二次收口】价值普查（`docs/review-2026-10-16/test-value-audit.md` A-1）量到那四份
#   镜像工程共 1,773 行 / 134 个 `Check` 调用，**每批都被重建却没有任何门禁执行其 exe**（全仓唯一提到
#   它们的地方就是本文件的登记表 ④），而 ⑤ 的判据本身写死「镜像独有断言 = 0」⇒ 按断言口径删除不丢
#   任何东西；那 15 个 mode 的执行体一直是 ServiceProbe（7 个在运行器默认探针清单、8 个由在册门禁按
#   参数位置调用），已并入 `TestGroups.cs` 的 `["ServiceProbe"]` 组。
#   ⇒ ④⑤ 从"方向锁"降级成一件更简单的事：**执行面每个 `Probe*` 方法只许有一份定义**；
#     登记表里只剩一个合法同名项 `ProbeCodedSizeForTestAsync`（GainMapTestHost 与其拆分件之间的私有
#     helper，ServiceProbe 里没有 ⇒ 不参与"只此一份"判据，只登记"这个重名是有的"）。
#     `$zeroMirrorCells` 随镜像一起清空 ⇒ 原来那条"两侧同塌 0 逐名等于登记"会变成 0==0 的恒真格，
#     所以 ⑤-a 一并撤下，不留假锁。
#
# 【为什么判据读源文本而不是跑二进制】丢一条断言是**源码事件**；读文本 ⇒ 变异不需要重建
#   （与 #74 的"重跑生成器 + cmp"同族）。代价是"条数"量的是**调用位**而非**运行次数**
#   （循环体只有一份源文本 ⇒ `bench` 钉 9 而不是运行时的格数）。这条口径写在 ③ 的输出里。
#
# 【取样器的一条旧坑（本次修掉，别再改回去）】方法体正则在第一版**没有排除表达式体包装**：
#   `internal static void RunBitDepth(string[] a) => ProbeBitDepth(a)…` 这类行会被当成"方法体起点"，
#   于是从包装行往后找 `{` ⇒ 取到一段不相干文本 ⇒ 该格条数**塌成 0**。实测 `tests/encode` 的
#   `ProbeBitDepth` 被这套旧取样量成 0（真值 7）⇒ 方向锁的"SP ≥ 镜像"与"镜像独有=0"会**双双恒真**。
#   现在：签名行含 `=>` 一律不算定义点，且定义点按**文件去重**。
#
# ⚠ 差集元素一律 [pscustomobject]，不用 `@($k,$d)`（PowerShell 的 `+=` 会摊平数组套数组，
#   见本文件 2026-10-13 版的头注释）。
# ═══════════════════════════════════════════════════════════════════════════════

param([switch]$Census)     # -Census：不判定，只打印「逐 mode 实测 vs 登记」差表（抬数时用）

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $root

$pass = 0; $fail = 0
function CK([bool]$ok, [string]$msg) {
    if ($ok) { Write-Host "PASS $msg" -ForegroundColor Green; $script:pass++ }
    else     { Write-Host "FAIL $msg" -ForegroundColor Red;   $script:fail++ }
}
Write-Host "===== probe-mirror-parity（存在锁 + 执行面条数锁 + 只此一份锁）=====" -ForegroundColor Cyan

$SP = 'tests/projects/ServiceProbe/Program.cs'

# ── 登记表 1：镜像工程删除登记（形态不许回来）──
#   2026-10-14 删 5 份（core/color/diagnostics/engine/geometry，31 份 Modes/*.cs + 组体 + csproj）；
#   2026-10-16 删 4 份（encode/io/metadata/gainmap，1,773 行 / 134 个 Check，exe 无人执行）。
$deadDirs = @('tests/core', 'tests/color', 'tests/diagnostics', 'tests/engine', 'tests/geometry',
              'tests/encode', 'tests/io', 'tests/metadata', 'tests/gainmap')
$genScript = 'tests/scripts/_gen-mode-split.ps1'    # 把「一组一文件」切成「一 mode 一文件」的生成器

# ── 登记表 2：逐派发 mode 的 Check 标签条数（2026-10-14 实测钉死）──
# ⚠ **只许升不许降**：降 = 执行面丢了断言（本锁存在的唯一理由）；升 = 加了断言，
#   必须把这里的数抬起来并写来历（与本仓 UI 并集锁 `$expectedOldCount` 同一套规矩）。
# ⚠ 值为 **0** 的那几项不是"采样坏了"，是这些 mode 本身**没有 Check** ——
#   它们要么是取数器（`color`/`icc-dump`/`tonemapcurve` 只打印，判据在调用它们的 .ps1 里），
#   要么是跨进程往返的驻留端（`ipc-server`/`piperaw-server`）。下面 ③-d 把"哪些 mode 该是 0"
#   也钉住 ⇒ 取样器若整体失配（方法体取不到、塌 0 变多），当场红而不是静默放行。
$counts = [ordered]@{
    'avifgm' = 14; 'bench' = 9; 'bitdepth' = 7; 'bt2446' = 18; 'color' = 0
    'colormath' = 12; 'contract' = 99; 'curve' = 14; 'decision' = 5; 'dng-xmp-args' = 2
    'encoding' = 6
    'engine' = 20; 'faithful' = 10; 'firstframe' = 15; 'gainmap' = 6; 'gainmap-isobmff' = 12
    'gamut' = 2; 'gamutfit' = 19; 'geometry' = 43; 'hlgwire' = 3; 'i18n' = 5
    'icc' = 5; 'icc-dump' = 0; 'iccname' = 9; 'ipc' = 13; 'ipc-client' = 2
    'ipc-raw-client' = 4; 'ipc-server' = 0; 'ipc-submit' = 3; 'matrix' = 13; 'memadmit' = 7; 'framelen' = 5
    # 2026-10-22 P3 跨仓对撞（宿主 vs ColorGainKit，同进程反射）。口径同样是**源文本调用位**：
    #   运行时会跑 10 条（`Log2Gain` 那一格在 foreach 里、两个 floorAtOne 各跑一次），但**条数锁量的是形状**，
    #   与上面 `framelen` 同形。Census 实测 got=9 ⇒ 登记 9（抬数需同轮写来历，不许凭运行读数抬）。
    'cgkparity' = 9
    'metaraw' = 20; 'oog' = 7; 'ootf' = 11; 'orientmeta' = 11; 'orientation' = 19
    'peak' = 8; 'peakmeasure' = 14; 'piperaw-client' = 5; 'piperaw-server' = 0; 'plan' = 18
    'png3' = 1; 'pq' = 5; 'procstreams' = 3; 'psnr' = 2; 'runner' = 8
    # ⚠ `settings` 56 → **11**（2026-10-22 修尺，不是"发现执行面丢了 45 条断言"）：旧 `Get-MethodText`
    #   朴素数花括号 ⇒ 字符串里的 `{0}` 也计入深度 ⇒ `ProbeSettingsSave` 那一格从第 6790 行一路吃到
    #   第 7870 行，把 ProbeRunnerGuard / cgkparity / ProbeIccName 等**邻居方法的 45 个 Check 吞进来**。
    #   配对器改成跳注释/跳字符串后重跑 `-Census`：60 格里**只有这一格**变（其余逐格相同），
    #   总量 848 → 803（差的正是被吞的 45）。⇒ 登记值以修好的尺为准；两把尺（源文本调用位 vs 运行次数）不许对齐这条不变。
    'selftest' = 18; 'settings' = 11; 'simdswitch' = 18; 'thumbnail' = 21; 'tonemap' = 22
    'tonemapcurve' = 0; 'tooldetect' = 27; 'ultrahdr' = 4; 'vendor-hdr' = 19; 'verdict' = 35; 'wire' = 99
    'xcheck' = 6
}
# 登记表各值之和 = 2026-10-14 实测 813；2026-10-15 加 `vendor-hdr` ⇒ 829，同日审查再加 V16/V17/V18 ⇒ 832，
# 2026-10-21 夜加 `dng-xmp-args`（产品侧 XMP 两臂串，账本 §40.9）⇒ **834**。
#   `vendor-hdr` = **19 个源文本调用位**，而 `gainmap-isobmff` §3d 的**运行时**通过数 = **18**；差 1 的两处成因不同：
#     ① V8/V9 与「夹具缺席」那条 fail-closed 支互斥（源文本 2 条、执行 1 条）；
#     ② V16/V17 是**一个 foreach 调用位跑两格**（源文本 1 条、执行 2 条）。
#   ⇒ 本锁量调用位、那条门禁量执行数，两把尺互补，**不许互相「对齐」**。
# 本登记表**不单独钉一个总数** ——
# 总数是各格之和，抬某个 mode 只改它自己那一格（单点真值；M6 反控验的就是这条通路）。
$regSum = 0
foreach ($k in $counts.Keys) { $regSum += [int]$counts[$k] }
$zeroModes = @('color', 'icc-dump', 'ipc-server', 'piperaw-server', 'tonemapcurve')

# ── 登记表 3：ServiceProbe 之外**仍然存在**的同名 Probe* 定义点（2026-10-16 起 = 1 名）──
#   2026-10-14 实测 16 名（前 15 名 = `tests/{encode,gainmap,io,metadata}` 四份工程里 ServiceProbe
#   探针的镜像副本）；那四份工程已于 **2026-10-16 整体删除**（价值普查 A-1：1,773 行 / 134 个
#   Check / exe 无人执行）⇒ 登记表塌到下面这 1 名。
#   剩下的这名 `ProbeCodedSizeForTestAsync` 是 GainMapTestHost 与其三段拆分之一之间的**同名私有 helper**
#   （ServiceProbe 里没有 ⇒ 不参与 ⑤ 的"只此一份"判据，只登记"这个重名是有的"）。
$dups = [ordered]@{
    'ProbeCodedSizeForTestAsync' = @('tests/projects/GainMapTestHost/Program.cs', 'tests/projects/gainmap-decode/GainMapDecodeGroup.cs')
}
# 两侧同塌 0 的镜像格登记表：**2026-10-16 随最后四份镜像一起清空**（原 `ProbeIccDump` 那格）。
# 清空后如果照旧保留"两边同塌 0 的格 == 登记"这条判据，它就成了 `0 == 0` 的恒真格 ⇒ 整条撤下，
# 由 ⑤「执行面每个 Probe* 只许有一份定义」代替（同一件事的更强形态）。
# 历史：`$zeroMirrorCells` 与差集比较器 `Cmp-Multiset` 服务的是"比两边标签多重集"的旧方向锁，
# 那份锁已于 A-1 后不再可达 ⇒ 两者一并删除，不留死代码（本仓规矩：删符号要连注释与残留一起清）。

# ── 取样器（同一把尺量两侧，不偏袒任何一份拷贝）──
# 定义点 = 签名行且**不是**表达式体包装（`(?!.*=>)`，见文件头那条旧坑）
$SIG = "(?m)^[ \t]*(?:private\s+|internal\s+|public\s+)?(?!.*=>)static[^\r\n]*\b(Probe[A-Za-z0-9_]+)\s*\("
function Get-MethodText([string]$text, [string]$name) {
    $m = [regex]::Match($text, "(?m)^[ \t]*(?:private\s+|internal\s+|public\s+)?(?!.*=>)static[^\r\n]*\b$name\s*\(")
    if (-not $m.Success) { return $null }
    $ob = $text.IndexOf('{', $m.Index + $m.Length)
    if ($ob -lt 0) { return $null }
    # ⚠ 配对器必须**跳过注释与字符串**（2026-10-22 修，测量见 §③ 注释与
    #   tests/output/ruler_census/）：旧写法把 `"…{0}…"` 里的花括号当代码深度数 ⇒
    #   `settings` 那一格从第 6790 行一路吃到第 7870 行，把 ProbeRunnerGuard / cgkparity /
    #   ProbeIccName 等邻居方法的 45 个 `Check` 全吞进自己 ⇒ 登记值 56 其实是**坏尺的产物**
    #   （真值 11），而且我新写一个方法插在那段区间里，就会让 `settings` 当场 +9 假漂移。
    $depth = 0
    $j = $ob
    $n = $text.Length
    while ($j -lt $n) {
        $c = $text[$j]
        if ($c -eq '/' -and ($j + 1) -lt $n -and $text[$j + 1] -eq '/') {
            $k = $text.IndexOf("`n", $j); if ($k -lt 0) { return $null }; $j = $k + 1; continue
        }
        if ($c -eq '/' -and ($j + 1) -lt $n -and $text[$j + 1] -eq '*') {
            $k = $text.IndexOf('*/', $j + 2); if ($k -lt 0) { return $null }; $j = $k + 2; continue
        }
        if ($c -eq '"' -and ($j - 1) -ge 0 -and $text[$j - 1] -eq '@') {
            $k = $j + 1
            while ($k -lt $n) {
                if ($text[$k] -eq '"') {
                    if (($k + 1) -lt $n -and $text[$k + 1] -eq '"') { $k += 2; continue }
                    break
                }
                $k++
            }
            $j = $k + 2; continue
        }
        if ($c -eq '"') {
            $k = $j + 1
            while ($k -lt $n) {
                if ($text[$k] -eq '\') { $k += 2; continue }
                if ($text[$k] -eq '"') { break }
                $k++
            }
            $j = $k + 1; continue
        }
        if ($c -eq "'") {
            $k = $j + 1
            while ($k -lt $n) {
                if ($text[$k] -eq '\') { $k += 2; continue }
                if ($text[$k] -eq "'") { break }
                $k++
            }
            $j = $k + 1; continue
        }
        if ($c -eq '{') { $depth++ }
        elseif ($c -eq '}') {
            $depth--
            if ($depth -eq 0) { return $text.Substring($m.Index, $j - $m.Index + 1) }
        }
        $j++
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
        # 标签 = 这条 Check 的**第一个**「像名字」的字面量（不是整段拼接；插值槽位归一 ⇒ 变量名不算差异）
        foreach ($s in [regex]::Matches($seg, '"((?:[^"\\]|\\.)*)"')) {
            $v = [regex]::Replace($s.Groups[1].Value, '\{[^{}]*\}', '{}')
            $v = $v.Trim()
            if ($v -match '[一-鿿]' -or $v.Length -gt 6) { $out.Add($v); break }
        }
    }
    return $out
}
function Get-MirrorFiles {
    @(Get-ChildItem -Path (Join-Path $root 'tests') -Recurse -Filter '*.cs' |
      Where-Object { $_.FullName -notmatch '\\(bin|obj|output)\\' } |
      ForEach-Object { ($_.FullName.Substring($root.Length + 1) -replace '\\', '/') })
}
# 全仓 Probe* 定义点索引：name -> 文件列表（按文件去重）
function Get-SiteMap($files) {
    $map = @{}
    foreach ($f in $files) {
        $t = [System.IO.File]::ReadAllText((Join-Path $root $f))
        foreach ($m in [regex]::Matches($t, $SIG)) {
            $n = $m.Groups[1].Value
            if (-not $map.ContainsKey($n)) { $map[$n] = New-Object System.Collections.Generic.List[string] }
            if (-not $map[$n].Contains($f)) { [void]$map[$n].Add($f) }
        }
    }
    return $map
}
# ServiceProbe 的派发表：case "mode": ProbeY(...)  ⇒ mode -> 方法名
function Get-Dispatch($text) {
    $d = [ordered]@{}
    foreach ($m in [regex]::Matches($text, '(?m)^\s*case\s+"(?<mode>[a-z0-9\-]+)":')) {
        $seg = $text.Substring($m.Index + $m.Length, [Math]::Min(400, $text.Length - $m.Index - $m.Length))
        $mm = [regex]::Match($seg, '\b((?:Probe|Dump)[A-Za-z0-9_]+)\s*\(')
        if ($mm.Success -and -not $d.Contains($m.Groups['mode'].Value)) { $d[$m.Groups['mode'].Value] = $mm.Groups[1].Value }
    }
    return $d
}

$spText = [System.IO.File]::ReadAllText((Join-Path $root $SP))
$files  = @(Get-MirrorFiles)
$sites  = Get-SiteMap $files
$disp   = Get-Dispatch $spText

# ── ① 前置反控：枚举不许为空（空枚举 = 恒绿的假门禁，本仓第 55 条）──
CK ($spText.Length -gt 100000) "① 前置：被执行那份读到 $($spText.Length) 字符（$SP）"
CK ($files.Count -ge 20) "① 前置：tests 下参与扫描的 .cs = $($files.Count)（>=20；2026-10-14 实 31 = 18 份工程的 Program/Group + 3 份共享底座，2026-10-16 A-1 删掉四份无人执行的镜像 ⇒ 现 23 = 9 份工程/底座的 Program+Group + 3 份共享底座；这条只管「扫描域被清空」，不管份数）"
CK ($sites.Count -ge 50) "① 前置：解析到 Probe* 方法名 $($sites.Count) 个（>=50 ⇒ 取样正则没失配）"
CK ($disp.Count -ge 50) "① 前置：解析到派发 mode $($disp.Count) 个（>=50 ⇒ case 表形状没变）"
if ($spText.Length -lt 1000 -or $files.Count -lt 1 -or $disp.Count -lt 1) {
    CK $false "① 枚举为空 ⇒ 拒绝判定，响亮判红（读到 $($spText.Length) 字符 / $($files.Count) 文件 / $($disp.Count) mode）"
    Write-Host "===== probe-mirror-parity: PASS=$pass FAIL=$fail =====" -ForegroundColor Cyan
    exit 1
}

if ($Census) {
    Write-Host '===== census：逐 mode 实测 vs 登记（本段不判红绿，抬数时用）=====' -ForegroundColor Cyan
    $rows = @()
    foreach ($mode in $disp.Keys) {
        $b = Get-MethodText $spText $disp[$mode]
        $n = -1
        if ($b) { $n = @(Get-CheckLabels $b).Count }
        $w = -1
        if ($counts.Contains($mode)) { $w = [int]$counts[$mode] }
        $rows += [pscustomobject]@{ mode = $mode; method = $disp[$mode]; want = $w; got = $n; delta = $n - $w }
    }
    $rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
    Write-Host ('登记 mode {0} / 派发 mode {1}；条数合计 实 {2} / 登记表之和 {3}' -f `
        $counts.Count, $disp.Count, (($rows | ForEach-Object { $_.got } | Where-Object { $_ -ge 0 } | Measure-Object -Sum).Sum), $regSum)
    Write-Host '（census 是取数，不是裁决）' -ForegroundColor Yellow
    exit 0
}

# ── ② 存在锁：被删的镜像形态不许回来 ──
foreach ($d in $deadDirs) {
    $when = if ($d -in @('tests/encode', 'tests/io', 'tests/metadata', 'tests/gainmap')) { '2026-10-16 A-1 删除' } else { '2026-10-14 删除' }
    CK (-not (Test-Path (Join-Path $root $d))) "② 镜像工程目录 $d 不在盘（$when）"
}
$modesFiles = @($files | Where-Object { $_ -match '/Modes/' })
$mfList = '无'
if ($modesFiles.Count) { $mfList = (($modesFiles | Select-Object -First 5) -join ', ') }
CK ($modesFiles.Count -eq 0) "② 全仓 tests/**/Modes/*.cs = 0（实 $($modesFiles.Count)：$mfList）⇒ 一 mode 一文件的镜像切片不许再生"
CK (-not (Test-Path (Join-Path $root $genScript))) "② 切片生成器 $genScript 不在位（它在=随时能把组体再切成一批无人执行的镜像）"
$tracked = @()
$gitOk = $true
try {
    $argsGit = @('ls-files', '--') + $deadDirs
    $tracked = @(& git -C $root $argsGit 2>$null)
    if ($LASTEXITCODE -ne 0) { $gitOk = $false }
} catch { $gitOk = $false }
if (-not $gitOk) { CK $false "② git ls-files 没跑成（fail-closed：这条锁的前提是能在索引里查这 $($deadDirs.Count) 个目录）" }
else {
    $trk = @($tracked | Where-Object { $_ })
    $tkList = '无'
    if ($trk.Count) { $tkList = (($trk | Select-Object -First 5) -join ', ') }
    CK ($trk.Count -eq 0) "② git 索引里这九目录 0 条目（实 $($trk.Count)：$tkList）⇒ 用 git 回退把镜像捞回来也会红"
}

# ── ③ 执行面逐 mode 条数锁（镜像删掉后，"丢断言"的见证人换成这份表）──
$notRegistered = @($disp.Keys | Where-Object { -not $counts.Contains($_) })
$notDispatched = @($counts.Keys | Where-Object { -not $disp.Contains($_) })
$nrList = '无'; if ($notRegistered.Count) { $nrList = ($notRegistered -join ', ') }
$ndList = '无'; if ($notDispatched.Count) { $ndList = ($notDispatched -join ', ') }
CK ($notRegistered.Count -eq 0) "③-a 每个派发 mode 都在登记表里（缺：$nrList）⇒ 新加 mode 必须同时钉它的条数"
CK ($notDispatched.Count -eq 0) "③-b 登记表里没有失效条目（多：$ndList）⇒ 删 mode 必须同时删它的登记"

$sum = 0; $zeroReal = @(); $unparsed = @()
foreach ($mode in $disp.Keys) {
    if (-not $counts.Contains($mode)) { continue }
    $want = [int]$counts[$mode]
    $body = Get-MethodText $spText $disp[$mode]
    if (-not $body) { $unparsed += "$mode($($disp[$mode]))"; continue }
    $n = @(Get-CheckLabels $body).Count
    $sum += $n
    if ($n -eq 0) { $zeroReal += $mode }
    $note = ''
    if ($n -lt $want) { $note = ' ⇒ 少 = 执行面丢了断言（本锁就是为这件事装的）' }
    if ($n -gt $want) { $note = ' ⇒ 多 = 加了断言，请抬登记表并写来历（只许升）' }
    CK ($n -eq $want) "③ $mode 标签条数 = $want（实 $n，方法 $($disp[$mode])，口径=源文本调用位不是运行次数）$note"
}
CK (@($unparsed).Count -eq 0) "③-c 每个登记的 mode 都取到方法体（取不到 = 签名形状变了 ⇒ 尺失效，实 $($unparsed.Count)：$(($unparsed | Select-Object -First 6) -join ', '))"
$zrList = (($zeroReal | Sort-Object) -join ','); $zeList = (($zeroModes | Sort-Object) -join ',')
CK (@($zeroReal).Count -eq $zeroModes.Count -and $zrList -eq $zeList) "③-d 塌 0 的 mode 逐名等于登记（登记 $zeList ｜ 实 $zrList）⇒ 取样整体失配会被这里接住"
CK ($sum -eq $regSum -and $regSum -ge 700) "③-e 总量：实取 $sum 条 == 登记表各 mode 之和 $regSum（>=700 才证明 $($counts.Count) 个 mode 真被读到）⇒ 单点真值在 ③ 那张表，抬一个 mode 只改它自己那一格"

# ── ④ 剩余双拷贝登记表：数量与归属逐名钉死 ──
foreach ($name in $dups.Keys) {
    $act = @()
    if ($sites.ContainsKey($name)) { $act = @($sites[$name] | Where-Object { $_ -ne $SP } | Sort-Object) }
    $exp = @($dups[$name] | Sort-Object)
    $same = ($act.Count -eq $exp.Count)
    if ($same) { for ($i = 0; $i -lt $act.Count; $i++) { if ($act[$i] -ne $exp[$i]) { $same = $false } } }
    $det = ''
    if (-not $same) { $det = "（实 $($act -join ',') ｜ 钉 $($exp -join ',')）" }
    CK $same "④ $name 的第二份定义点逐名等于登记$det"
}
# ── ④-z 已并入 ⑤-z（镜像清零后，"新增第二份"与"执行面只此一份"是同一件事，两条判据不必并存）──

# ── ⑤ 只此一份锁：执行面（ServiceProbe）里定义的每个 Probe* 方法，全仓不许有第二份定义点 ──
#   2026-10-16 之前的形态是"方向锁"（比 SP 与镜像两边的标签多重集，要求镜像独有 = 0 且 SP ≥ 镜像）——
#   那需要镜像存在。镜像全删完之后，方向锁的两条恒真前提（两边都取到 0 条）就会把锁变成装饰，
#   所以这里改量**定义点基数**：`$sites[name]` 去掉 SP 自身之后必须为空。
$spOwnNames = @($sites.Keys | Where-Object { @($sites[$_]) -contains $SP } | Sort-Object)
CK ($spOwnNames.Count -ge 30) "⑤ 前置：ServiceProbe 自己定义的 Probe* 方法 = $($spOwnNames.Count) 个（须 ≥30 ⇒ 取样域没被清空才有资格谈只此一份）"
$second = @()
foreach ($n in $spOwnNames) {
    $others = @($sites[$n] | Where-Object { $_ -ne $SP })
    if ($others.Count -gt 0) { $second += "$n@$($others -join ',')" }
}
$s2List = '无'
if ($second.Count) { $s2List = (($second | Select-Object -First 8) -join ' | ') }
CK ($second.Count -eq 0) "⑤ 执行面 $($spOwnNames.Count) 个 Probe* 方法里，长出第二份定义的 = $($second.Count) 条（须 0；实 $s2List）⇒ 又有人把断言抄进无人执行的拷贝，这里当场红"
$newDup = @()
foreach ($name in $sites.Keys) {
    if ($dups.Contains($name)) { continue }
    if (@($sites[$name]).Count -ge 2) { $newDup += $name }
}
$nd2List = '无'; if ($newDup.Count) { $nd2List = ($newDup -join ', ') }
CK ($newDup.Count -eq 0) "⑤-z 除登记的 $($dups.Count) 名同名 helper 外，没有别的 Probe* 长出第二份定义（实 $($newDup.Count)：$nd2List）"

Write-Host "===== probe-mirror-parity: PASS=$pass FAIL=$fail =====" -ForegroundColor Cyan
if ($fail -gt 0) { exit 1 } else { exit 0 }
