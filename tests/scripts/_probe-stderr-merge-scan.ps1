# ══════════════════════════════════════════════════════════════════════════════
# 第 56 条门禁（2026-09-27 接线，任务 #51）：合并式取数（stdout+stderr 一起返回）
#   被用来读 **工具的值**（exiftool 标签 / ffprobe 字段）⇒ 必须要么改用只读 stdout，要么留字面理由
# ══════════════════════════════════════════════════════════════════════════════
# 为什么要有这一条（不是又一把"规范洁癖"尺）：
#   随包 exiftool 是 **perl 打包**的。只要进程环境里带着本机 perl 不认的 `LC_ALL` / `LANG`
#   （从 MSYS/bash 侧起门禁就会出现 `C.UTF-8`），它**每次调用都往 stderr 喷 locale warning**。
#   于是把 stdout+stderr 拼起来返回的 helper，读出来的"值"其实是警告文本，后果分两种、都坏：
#     · **假绿**：负断言被撑成恒真 —— 例 `'' -notmatch 'a'`（读不到 pix_fmt ⇒ "素材确实不透明"通过）、
#       两侧同污时 `-ne` 变成"两条警告互比"（`run-pipeline-tests` 的 ColorMatrix1 就是这个形状）；
#     · **假红**：值前面多了几行警告，归一化没剥干净 ⇒ 两格对照判"不同"。
#   本轮（2026-09-27）实测清点并修掉 4 处：`verify-engine-alpha-preserve.ps1`（Get-PixFmt 空值喂负断言）、
#   `run-pipeline-tests.ps1`（ColorMatrix1 两行改用既有 `ExifVal`）、`verify-color-caps.ps1`
#   （`IccOf`/`CicpOf` 新增 `ExecOut` + 一条"取数尺有牙"正对照）、`verify-color-strategy.ps1`
#   （`$cicp`/`$hasIcc` 改 `ExecOut` + 正对照）。⚠ 另有 **14 处合并是故意的**（PSNR/SSIM/`^frame=`/
#   `Corrupted`/产品日志**只在 stderr**）⇒ 本尺**不**禁止合并，只要求"读工具的值"这一类不许裸用合并。
#
# ⚠ 这把尺的**能力边界**（必须说清，别当成数据流分析）：它只做**文件级共现**判断 ——
#   同一文件里「合并 helper」+「用该 helper 调 exiftool/ffprobe 读值」同时出现 ⇒ 违规，除非
#   ① 该文件另有**只读 stdout** 的取数函数并已用在这些调用上，或 ② 调用行留了字面理由
#   `合并取数已论证`。它不追踪变量流向，所以"读了值但拿去干别的"它看不见 —— 那是**清单**，
#   靠打印逐条文件让人复查，不靠它自动判正确。
#
# 用法：pwsh -NoProfile -File tests/scripts/_probe-stderr-merge-scan.ps1 [-Verbose]
param([switch]$List, [switch]$ManagedOnly)
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

$pass = 0; $fail = 0
function CK($ok, $msg) {
    if ($ok) { $script:pass++; Write-Output "  PASS $msg" }
    else     { $script:fail++; Write-Output "  FAIL $msg" }
}

# ── 三类形态的字面判据 ──────────────────────────────────────────────────────────
# 合并型 fetch：一次函数返回里把 stdout 与 stderr 拼起来（两种既有写法都认）
$reMerge  = @('ReadAllText\(\$o\).*ReadAllText\(\$e\)', 'text\s*=\s*\(\$t \+ .*\+ \$t2\)')
# 只读 stdout 的 fetch（修法就是引入它）
$rePure   = @('return \[System\.IO\.File\]::ReadAllText\(\$o\)\s*$', 'ReadAllText\(\$o\)\s*\)?\s*$')
# 用 helper 读**工具的值**：exiftool / ffprobe / jxlinfo 作为被调变量，**或**直接写路径字面量
#   ⚠ 2026-09-27 由 lane 分诊时暴露的盲区：只认 `$et/$fp` 变量名的话，
#     `Exec "publish/PLAN/jxl/bin/jxlinfo.exe" ...` 这类**字面路径**调用扫不到 ⇒ 值同样在 stdout、
#     同样会被合并污染（jxlinfo 实测不吃 LC_ALL，但"要不要豁免"必须由锁**看见**之后再判，不能靠看不见）。
#   ⚠ 同日第二轮同类盲区：同一把尺在别的脚本里叫 **`$ex`（exiftool）/ `$ji`/`$jxlinfo`** ⇒ 变量名单按
#     `et|exif|fp` 列会漏掉 5 个脚本里的 exiftool 取值。⇒ 变量名一律补齐，顺序上把长的放前面（`jxlinfo`
#     必须排在 `jxl` 之前，否则 `\b` 把它切掉）。
#   ⚠ **不含** `$pr`/`$sp`（= ServiceProbe，.NET 进程）：本锁打的是"perl 打包工具往 stderr 喷 locale
#     warning 污染值"这一类；.NET 探针的 stderr 只在异常时有内容，且探针侧已把异常行加 `FAIL ` 前缀
#     （见 `ServiceProbe/Program.cs`）⇒ 风险不同型，由 `verify-simd-switch-fallback.ps1` 的
#     "pass>=25 + 拒绝 unknown command"下限锁住，不在这里造 25 条假债。
$reValRead = '\b(Exec|ExecAll|Run)\s+(\$(et|exiftool|exif|ex|fp|ffprobe|jxlinfo|jinfo|ji|jxl)\b|"[^"]*(exiftool|ffprobe|jxlinfo)[^"]*")'

$files = @(Get-ChildItem -LiteralPath (Join-Path $root 'tests/scripts') -Filter '*.ps1' -File |
           Sort-Object Name)
if ($ManagedOnly) {
    # 受管清单 = 运行器数组里那 55 条门禁本体（不含 `_probe-*` / 手工长跑 / 夹具脚本）。
    # 理由：合并取数只有在**每轮都跑**的门禁里才会把假绿/假红稳定带进基线；清单外的探针照样打，
    #       但那是"债"，不是"每轮都会咬人的尺"。两种口径分开看，别混成一个数字。
    $runnerTxt = [System.IO.File]::ReadAllText((Join-Path $root 'tests/scripts/_run-step3-gates.ps1'))
    $managed = @([regex]::Matches($runnerTxt, "'(verify-[a-z0-9-]+\.ps1)'") | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    if ($managed.Count -lt 40) {
        Write-Output ("  FAIL 受管清单只解析到 {0} 条 ⇒ 解析失效，-ManagedOnly 判红（不静默当 0）" -f $managed.Count)
        Write-Output 'STDERRMERGE RESULT: pass=0 fail=1'
        exit 1
    }
    $files = @($files | Where-Object { $managed -contains $_.Name })
    Write-Output ("  受管清单口径：{0} 条门禁纳入扫描" -f $files.Count)
}
if ($files.Count -lt 40) {
    Write-Output ("  FAIL 枚举到 {0} 个脚本，少于下限 40 ⇒ 扫描范围本身失效，本条判红（不静默放行）" -f $files.Count)
    Write-Output 'STDERRMERGE RESULT: pass=0 fail=1'
    exit 1
}

$viol = New-Object System.Collections.Generic.List[string]
$mergedFiles = 0
foreach ($f in $files) {
    $txt = [System.IO.File]::ReadAllText($f.FullName)
    $isMerged = $false
    foreach ($r in $reMerge) { if ($txt -match $r) { $isMerged = $true; break } }
    if (-not $isMerged) { continue }
    $mergedFiles++
    $hasPure = $false
    foreach ($r in $rePure) { if ($txt -match $r) { $hasPure = $true; break } }
    # 逐行找"用合并 helper 读工具值"的调用点
    $lines = $txt -split "`r?`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $ln = $lines[$i]
        if ($ln -notmatch $reValRead) { continue }
        if ($ln.TrimStart().StartsWith('#')) { continue }        # 注释里提到不算站点
        if ($ln -match '合并取数已论证') { continue }               # 留了字面理由
        if ($hasPure -and $ln -notmatch '\bExec\s+\$') { continue }  # 已有只读版且此行不是 Exec ⇒ 不判
        if ($hasPure -and $ln -match '\b(ExecOut|ExecStdoutOnly|ExifVal)\b') { continue }
        $viol.Add(("{0}:{1}" -f $f.Name, ($i + 1)))
    }
}

Write-Host "=== 合并式取数扫描（第 56 条）===" -ForegroundColor Cyan
Write-Output ("  枚举脚本 {0} 个；其中定义合并 fetch 的 {1} 个" -f $files.Count, $mergedFiles)
if ($List -or $viol.Count -gt 0) {
    foreach ($v in $viol) { Write-Output ("  站点 " + $v) }
}
# 基线账（**实测**，不是"达标线"）：
#   2026-09-27 上午由 #51 清点出 **84** 处「用合并 fetch 读工具值」站点 ⇒ 当日由任务 #54 逐条分诊：
#   值确实只在 stderr 的（PSNR/SSIM/`^frame=`/`Corrupted`/产品日志/`-ver` 摘要/写操作）留字面
#   『合并取数已论证』并写明理由；该改只读 stdout 的改 `ExecOut`/`ExecStdoutOnly`/`ExifVal`。
#   同日把 `$reValRead` 的变量名单补齐（`$ex`/`$ji`/`$jxlinfo`）后**新冒出 3 处**，也已分诊完毕
#   （strategy 的 jxlinfo 位深取值改只读；png3 的两处 = 摘要打印 + 写操作）。
#   ⇒ **现值 0**：本条自此从"债不得增加"升级为**零未分诊站点**（新增一行裸合并取值即红）。
# ⚠ 0 不代表"所有合并读法都对"，只代表"每一处合并取工具值都已点名并写了理由"。
#   理由写错的那一类（如把"值在 stdout"论证成"值在 stderr"）由逐条打印 + 人工复查兜，不靠这把尺。
$debtBaseline = 0
$debtMsg = ("合并取数读工具值的站点 {0} 条 ≤ 债账基线 {1}（超出 ⇒ 新增站点，" -f $viol.Count, $debtBaseline)
$debtMsg += '要么改用只读 stdout，要么在调用行留『合并取数已论证』并说明为什么值在 stderr）'
CK ($viol.Count -le $debtBaseline) $debtMsg

# ── ② 今日五处收口的结构锁（防"改完又被改回去"）──
#   2026-09-27 由 #51 把这五处的**工具取值**改判只读 stdout，并各自加了正对照
#   （caps/strategy 的"取数尺有牙"、alpha-preserve 的 `$null` 前置、run-pipeline 的"取数为空即 throw"）。
#   这里钉的是**形态 + 逐文件残留数**：残留不是"达标"，是**已核过不喂断言**的行，逐个写明：
#     · `verify-color-caps.ps1` 原残留 5（`-ver` 只打印 · 两处 `-icc_profile<=` 写入 · 两处 `-b -w` 抽取到文件）
#       —— 任务 #54 已给这 5 行逐一留字面『合并取数已论证』⇒ 判据口径下**基线降到 0**。
#     · 其余三个文件残留必须为 **0**。
#   ⚠ 计数是**棘轮**：新增一行合并取值 ⇒ 红；把残留改成 0（真收口）也会红 ⇒ 必须同时在这里除名，
#   这正是"名单不许静默腐烂"的口径（同 #42/#55 的取证锁）。
$remediatedExpected = [ordered]@{
    'verify-color-caps.ps1'            = 0
    'verify-color-strategy.ps1'        = 0
    'run-pipeline-tests.ps1'           = 0
    'verify-engine-alpha-preserve.ps1' = 0
}
foreach ($name in $remediatedExpected.Keys) {
    $exp = $remediatedExpected[$name]
    $p = Join-Path $root ('tests/scripts/' + $name)
    if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { CK $false "$name 在位（收口锁的前置）"; continue }
    $t = [System.IO.File]::ReadAllText($p)
    $back = 0
    foreach ($ln in ($t -split "`r?`n")) {
        if ($ln.TrimStart().StartsWith('#')) { continue }
        if ($ln -match $reValRead -and $ln -notmatch '合并取数已论证') { $back++ }
    }
    # "纯净"的两种成立方式：① 文件里有只读取数函数；② 文件里**根本没有**合并 fetch（如 alpha-preserve 用
    #    `& $fp … 2>$null` + 空值返回 `$null`，本来就不拼 stderr）
    $hasMergeHelper = $false
    foreach ($r in $reMerge) { if ($t -match $r) { $hasMergeHelper = $true; break } }
    $hasPure = ($t -match 'function\s+(ExecOut|ExecStdoutOnly)\b') -or (-not $hasMergeHelper)
    CK ($back -eq $exp -and $hasPure) ("收口锁：$name 合并取工具值 = {0}（基线 {1}）、只读取数在位={2}" -f $back, $exp, $hasPure)
}

# ── ③ 扫描器自己的牙：共现基数必须真的被量到（正则失效会伪装成"零违规"）──
# ⚠ 下限**必须随口径变**：2026-09-27 实测 全仓 94 个脚本里 38 个定义合并 fetch，
#   而 `-ManagedOnly`（只看清单内 43 条门禁）口径下只有 **21** 个 ⇒ 拿全局下限 30 去量受管子集，
#   会让 `-ManagedOnly` 这一档**恒红**（自己造的假红，与产品无关）。两档各自钉底。
$mergedFloor = if ($ManagedOnly) { 18 } else { 30 }
$coMsg = ("合并 fetch 的共现基数被真正量到（实得 {0} 个文件，下限 {1} ⇒ 掉了说明正则失效，" -f $mergedFiles, $mergedFloor)
$coMsg += '此时站点数会假降 ⇒ 本条判红）'
CK ($mergedFiles -ge $mergedFloor) $coMsg

Write-Output ("STDERRMERGE RESULT: pass={0} fail={1}" -f $pass, $fail)
if ($fail -gt 0) { exit 1 } else { exit 0 }
