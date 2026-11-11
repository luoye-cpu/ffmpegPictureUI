# verify-log-jsonlines.ps1 — `--log-format JsonLines` 格式正确性 E2E（批 4·1，docs/TEST_MODULE_PLAN_2026-11-12.md）
# 盲区#9 收口（2026-11-12 调研：log-file 仅被 8+ 门禁当 Text 抓取通道引用，JsonLines 逐行字段从未被断言过）。
#
# 产品行为（源码钉死，本门禁端到端兑现；证据 ConsoleQueueObserver.cs）：
#   · LogEvent schema（:27-38）：短码字段 t/l/c/m/f/p/tot/dur/code；null 省略（WhenWritingNull）
#     ⇒ 每行必有 t、l、m；字段顺序 = 声明序；单行输出（WriteIndented=false）。
#   · t = DateTimeOffset.UtcNow 序列化 round-trip "O" ⇒ UTC 偏移写作 +00:00（不是 Z）。
#   · l = 级别枚举小写（:221）∈ {error,warn,info,debug,trace}（Trace 无产出点）。
#   · c 实跑全集 = {progress, item_completed, item_log, queue_completed}。
#   · 过滤语义（:213）：level 大于阈值即丢，Text/JsonLines 两档统一 ⇒ 默认 Info 下
#     progress（Debug 级）与 Debug 级 item_log 不进日志；--log-level debug 解锁。
#   · 双写（:248-272）：stdout 与文件同一行文本 ⇒ 文件行必须逐字出现在 stdout。
#   · 文件（:63-78）：目录自动创建、覆盖式（append:false）、打开失败不崩 ⇒ stderr
#     「⚠️ 无法打开日志文件: …」且进程继续（退出码不受影响）。
#   · 非法档位 fail-closed：CliParser ParseEnumStrict 抛 ArgumentException ⇒ 用法错 exit 2。
#   · --log-format 缺省 = LogFormat.Text（CliParser.cs:61）⇒ --log-file 单独用是 Text 文件。
#   · JSONL 行的 message 不含 Text 档字形前缀（═══/❌/⚠️ 只在 Text 分支拼，:236-241）。
#   ⚠ 中文按 System.Text.Json 默认转义为 \uXXXX ⇒ 一切判读必须 parse 后比对，不许裸串匹配。
# ⚠ 启动用 ProcessStartInfo(UseShellExecute=$false)：提权会话 Start-Process 弹「打开文件-安全警告」
#   模态框阻塞（2026-11-12 实测，见 AGENTS §7 GA 条目）。
param([switch]$SelfCheck, [switch]$KeepWork)
$ErrorActionPreference = 'Stop'
$scriptsDir = $PSScriptRoot
$repoRoot = Split-Path -Parent (Split-Path -Parent $scriptsDir)
$exe = Join-Path $repoRoot 'src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe'
$ff = Join-Path $repoRoot 'publish/PLAN/ffmpeg-full/ffmpeg.exe'

$roundTmp = Join-Path $repoRoot 'tests/output/_tmp/round-tmp'
New-Item -ItemType Directory -Force -Path $roundTmp | Out-Null
$env:TMP = $roundTmp; $env:TEMP = $roundTmp; $env:TMPDIR = $roundTmp
$env:FFMPEGGUI_PLAN_DIR = Join-Path $repoRoot 'publish/PLAN'

$script:pass = 0
$script:fail = 0
function CK([bool]$cond, [string]$name) {
    if ($cond) { $script:pass++; Write-Output "  PASS  $name" }
    else { $script:fail++; Write-Output "  FAIL  $name" }
}

foreach ($need in @($exe, $ff)) { if (-not (Test-Path $need)) { throw "缺被测件/工具：$need" } }

$guid = [guid]::NewGuid().ToString('N').Substring(0, 12)
$work = Join-Path $repoRoot "tests/output/log_jsonlines/$guid"
New-Item -ItemType Directory -Force -Path $work | Out-Null
Write-Output "  work=$work"

# ── 判读链（负控被测对象：集中定义，内存可单测）─────────────────────────────────
# ⚠ 实测教训（2026-11-12 首轮）：pwsh7 ConvertFrom-Json 会把 ISO8601 串**自动转成 [DateTime]**
#   （转本地时区）⇒ 线上的 +00:00 信息在解析对象里已丢失 ⇒ t 的线格式锁必须在**原始行**上做。
function Test-RawTimestamp([string]$line) {
    # t = DateTimeOffset.UtcNow 的 round-trip "O" 序列化 ⇒ 必为 精密小数 + 全零偏移（不是 Z）
    $m = [regex]::Match($line, '"t":"(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+)([+-]\d{2}:\d{2})"')
    if (-not $m.Success) { return $false }
    try { $o = [DateTimeOffset]::Parse($m.Groups[1].Value + $m.Groups[2].Value) } catch { return $false }
    return ($o.Offset -eq [TimeSpan]::Zero)
}
function Get-ParsedLine([string]$line) {
    if ([string]::IsNullOrWhiteSpace($line)) { return $null }
    try { return $line | ConvertFrom-Json } catch { return $null }
}
function Test-MandatoryFields($j) {
    # 每行必有 l（级别小写枚举）、m（非空串）——null 省略契约下的不变量（t 由 Test-RawTimestamp 在原始行锁）
    if ($null -eq $j) { return $false }
    $pl = $j.PSObject.Properties['l']; $pm = $j.PSObject.Properties['m']
    if (-not $pl -or -not $pm) { return $false }
    # ⚠ -in/-notin 大小写不敏感（'INFO' 会撞过 'info'）⇒ 级别/类别枚举一律 -ccontains
    if ($pl.Value -isnot [string] -or -not (@('error', 'warn', 'info', 'debug', 'trace') -ccontains $pl.Value)) { return $false }
    if ($pm.Value -isnot [string] -or $pm.Value.Length -eq 0) { return $false }
    return $true
}
function Test-CategoryEnum($j) {
    # c 缺省允许（schema 可空）；出现时必须是实跑全集之一
    $pc = $j.PSObject.Properties['c']
    if (-not $pc) { return $true }
    return ($pc.Value -is [string] -and (@('progress', 'item_completed', 'item_log', 'queue_completed') -ccontains $pc.Value))
}
function Test-NoTextGlyphs([string]$line) {
    # JSONL 行不带 Text 档字形前缀（═══/❌/⚠️ 只在 Text 分支拼）
    return -not ($line.Contains([char]0x2550) -or $line.Contains([char]0x274C) -or $line.Contains([char]0x26A0))
}
function Test-NoDebugAtInfo($j) {
    # Info 阈值下不许出现 debug 行（:213 过滤语义；progress 类全是 Debug 级 ⇒ 也不许出现）
    $pl = $j.PSObject.Properties['l']
    if ($pl -and $pl.Value -eq 'debug') { return $false }
    $pc = $j.PSObject.Properties['c']
    if ($pc -and $pc.Value -eq 'progress') { return $false }
    return $true
}
function Test-QueueCompletedCounts($j) {
    # 汇总行的 p/tot 必须是整数（序列化自 int?；pwsh ConvertFrom-Json 把整数落成 [long]，
    #   首轮实测 -is [int] 过紧误杀 ⇒ 接受 int/long，字符串/浮点仍拒）
    $pp = $j.PSObject.Properties['p']; $pt = $j.PSObject.Properties['tot']
    if (-not $pp -or -not $pt) { return $false }
    $pInt = ($pp.Value -is [int] -or $pp.Value -is [long])
    $tInt = ($pt.Value -is [int] -or $pt.Value -is [long])
    return ($pInt -and $tInt)
}

# ── 启动器（CreateProcess，防提权弹窗；参数走 ArgumentList 免引号拆碎）──────────
function Run-Headless([string]$tag, [string]$inputPath, [string]$outdir, [string[]]$extra) {
    New-Item -ItemType Directory -Force -Path $outdir | Out-Null
    $outF = Join-Path $work "$tag.stdout"
    $errF = Join-Path $work "$tag.stderr"
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    foreach ($a in @('--headless', '-i', $inputPath, '--format', 'png', '--output', $outdir, '--png-optimizer', 'off')) { [void]$psi.ArgumentList.Add($a) }
    foreach ($a in $extra) { [void]$psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $so = $p.StandardOutput.ReadToEnd(); $se = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    [IO.File]::WriteAllText($outF, $so); [IO.File]::WriteAllText($errF, $se)
    return @{ code = $p.ExitCode; out = $so; err = $se; outdir = $outdir }
}

# ── 夹具：基件 PNG ──────────────────────────────────────────────────────────
$basePng = Join-Path $work 'base.png'
$mk = New-Object System.Diagnostics.ProcessStartInfo
$mk.FileName = $ff
$mk.Arguments = "-y -f lavfi -i testsrc2=size=256x256:rate=1 -frames:v 1 `"$basePng`""
$mk.UseShellExecute = $false; $mk.CreateNoWindow = $true
$p = [System.Diagnostics.Process]::Start($mk); $p.WaitForExit()
CK ((Test-Path $basePng) -and (Get-Item $basePng).Length -gt 100) "夹具 PNG 现造成功（$(if (Test-Path $basePng) { (Get-Item $basePng).Length } else { 0 })B）"

# ── A1 主臂：log-file + JsonLines（默认 Info），全行 schema 锁 ────────────────
$logA = Join-Path $work 'a.jsonl'
$rA = Run-Headless 'a' $basePng (Join-Path $work 'out_a') @('--log-file', $logA, '--log-format', 'JsonLines')
CK ($rA.code -eq 0) "A: exit=0（实得 $($rA.code)）"
CK (Test-Path $logA) "A: 日志文件在（覆盖式创建）"
$aLines = @(Get-Content $logA)
CK ($aLines.Count -ge 2) "A: 行数 ≥2（实得 $($aLines.Count)——item_completed + queue_completed 至少各一）"
$aBad = 0; $aObjs = @()
foreach ($ln in $aLines) { $o = Get-ParsedLine $ln; if ($null -eq $o -or -not (Test-RawTimestamp $ln) -or -not (Test-MandatoryFields $o) -or -not (Test-CategoryEnum $o)) { $aBad++ } else { $aObjs += $o } }
CK ($aBad -eq 0) "A: 全部 $($aLines.Count) 行过 schema 锁（t=ISO+00:00 原始行 / l=小写枚举 / m 非空 / c∈实跑全集；坏 $aBad 行）"
$glyphBad = @($aLines | Where-Object { -not (Test-NoTextGlyphs $_) })
CK ($glyphBad.Count -eq 0) "A: 零 Text 字形行（═══/❌/⚠️ 只属 Text 档；实得 $($glyphBad.Count)）"
$infoBad = @($aObjs | Where-Object { -not (Test-NoDebugAtInfo $_) })
CK ($infoBad.Count -eq 0) "A: Info 阈值过滤兑现（零 debug 行 / 零 progress 类；违 $($infoBad.Count)）"
$aQ = @($aObjs | Where-Object { $_.PSObject.Properties['c'] -and $_.c -eq 'queue_completed' })
CK ($aQ.Count -eq 1) "A: 恰一条 queue_completed 汇总行（实得 $($aQ.Count)）"
CK (($aQ.Count -eq 1) -and (Test-QueueCompletedCounts $aQ[0])) "A: 汇总行 p/tot 为 int"
CK (($aQ.Count -eq 1) -and ($aQ[0].p -eq 1) -and ($aQ[0].tot -eq 1)) "A: 汇总 p=1 tot=1（单任务完成）"
$aDone = @($aObjs | Where-Object { $_.PSObject.Properties['c'] -and $_.c -eq 'item_completed' -and $_.PSObject.Properties['code'] -and $_.code -eq 0 })
CK ($aDone.Count -ge 1) "A: item_completed 带 code=0 ≥1 行（实得 $($aDone.Count)）"
$aMsg = @($aObjs | Where-Object { $_.m -match '完成 1 项' })
CK ($aMsg.Count -ge 1) "A: 汇总消息 parse 后含「完成 1 项」（中文 \uXXXX 转义必须 parse 后比对）"

# ── A2 双写等价：文件行 ⊆ stdout 行（同一行文本两路写，:248-272）───────────────
$aStdoutLines = @($rA.out -split "`r?`n" | Where-Object { $_ -ne '' })
$missing = @($aLines | Where-Object { $aStdoutLines -notcontains $_ })
CK ($missing.Count -eq 0) "A2: 文件行逐字出现在 stdout（双写等价；缺 $($missing.Count) 行）"

# ── B 臂：--log-level debug 解锁 progress 明细 ───────────────────────────────
$logB = Join-Path $work 'b.jsonl'
$rB = Run-Headless 'b' $basePng (Join-Path $work 'out_b') @('--log-file', $logB, '--log-format', 'JsonLines', '--log-level', 'debug')
CK ($rB.code -eq 0) "B: exit=0（实得 $($rB.code)）"
$bObjs = @(); $bBad = 0
foreach ($ln in @(Get-Content $logB)) { $o = Get-ParsedLine $ln; if ($null -eq $o) { $bBad++ } else { $bObjs += $o } }
CK ($bBad -eq 0) "B: 全行 JSON 可解析（坏 $bBad）"
$bProg = @($bObjs | Where-Object { $_.PSObject.Properties['c'] -and $_.c -eq 'progress' })
CK ($bProg.Count -ge 1) "B: debug 级解锁 progress 类 ≥1 行（实得 $($bProg.Count)）"
CK (($bProg.Count -ge 1) -and @($bProg | Where-Object { $_.PSObject.Properties['f'] -and $_.f }).Count -eq $bProg.Count) "B: progress 行带 f（条目名）"
CK (@($bObjs | Where-Object { $_.PSObject.Properties['l'] -and $_.l -eq 'debug' }).Count -ge 1) "B: debug 级行在日志里出现（过滤阈值下移兑现）"

# ── C 臂：--log-file 不带 --log-format ⇒ 默认 Text（非 JSON）─────────────────
$logC = Join-Path $work 'c.txt'
[void](Run-Headless 'c' $basePng (Join-Path $work 'out_c') @('--log-file', $logC))
$cLines = @(Get-Content $logC -ErrorAction SilentlyContinue)
$nonJson = @($cLines | Where-Object { $null -eq (Get-ParsedLine $_) })
CK ($cLines.Count -ge 1 -and $nonJson.Count -ge 1) "C: 默认档是 Text（$($cLines.Count) 行中 $($nonJson.Count) 行非 JSON）"

# ── D 臂：JsonLines 不带 log-file ⇒ JSON 行走 stdout、stderr 无告警 ───────────
$rD = Run-Headless 'd' $basePng (Join-Path $work 'out_d') @('--log-format', 'JsonLines')
CK ($rD.code -eq 0) "D: exit=0（实得 $($rD.code)）"
$dLines = @($rD.out -split "`r?`n" | Where-Object { $_ -ne '' })
$dJson = @($dLines | Where-Object { $_.StartsWith('{') })
$dBad = @($dJson | Where-Object { $null -eq (Get-ParsedLine $_) })
CK ($dBad.Count -eq 0) "D: stdout 全部 JSON 行可解析（'{' 头 $($dJson.Count) 行，坏 $($dBad.Count)）"
# ⚠ stdout 同时承载 Console.WriteLine 旁路播报（工具检测/队列准备）——verify-encoder-knob-liveness
#   在案的「三通道」现实，本门禁把它从「意外噪声」升格为**被钉行为**（无声消失 = 红，有意识变更 = 改锁）。
$dBypass = @($dLines | Where-Object { $_ -match '正在检测外部工具|正在准备任务队列' })
CK ($dBypass.Count -ge 1) "D: 旁路播报在 stdout（检测/准备 $($dBypass.Count) 行——旁路通道现实被钉）"
$dQ = @($dLines | ForEach-Object { Get-ParsedLine $_ } | Where-Object { $_ -and $_.PSObject.Properties['c'] -and $_.c -eq 'queue_completed' })
CK ($dQ.Count -eq 1) "D: stdout 恰一条 queue_completed（实得 $($dQ.Count)）"
CK ($rD.err -notmatch '无法打开日志文件') "D: stderr 无日志文件告警（本臂没要文件）"

# ── E 臂：非法档位 fail-closed（ParseEnumStrict ⇒ 用法错 exit 2）──────────────
$rE = Run-Headless 'e' $basePng (Join-Path $work 'out_e') @('--log-format', 'nonsense')
CK ($rE.code -eq 2) "E: --log-format nonsense ⇒ exit 2（实得 $($rE.code)）"
CK ($rE.err + $rE.out -match 'Text \| JsonLines') "E: 报错点名合法档位（Text | JsonLines）"

# ── F 臂：日志文件打不开不崩（stderr 告警 + 进程继续 + 产物正常）────────────────
$badDir = Join-Path $work 'bad<name'
$rF = Run-Headless 'f' $basePng (Join-Path $work 'out_f') @('--log-file', (Join-Path $badDir 'x.jsonl'), '--log-format', 'JsonLines')
CK ($rF.code -eq 0) "F: 打不开日志文件不崩 exit=0（实得 $($rF.code)）"
CK ($rF.err -match '无法打开日志文件') "F: stderr 点名告警（无法打开日志文件）"
$fProd = Join-Path $rF.outdir 'base.png'
CK ((Test-Path $fProd) -and (Get-Item $fProd).Length -gt 100) "F: 产物正常交付（文件侧静默缺失不拖累任务）"

# ── 负控（内存，零进程）：判读链口径 ────────────────────────────────────────
$ncOk = $true
if ($SelfCheck) {
    Write-Output "  [selfcheck] judgement-chain teeth (in-memory)"
    $ncRed = 0
    $good = '{"t":"2026-11-12T08:00:00.1234567+00:00","l":"info","c":"queue_completed","m":"\u5b8c\u6210 1 \u9879","p":1,"tot":1}'
    $jGood = Get-ParsedLine $good
    if (-not (Test-RawTimestamp $good) -or -not (Test-MandatoryFields $jGood)) { Write-Output '    (negctl) 合法行被判坏 => schema 锁过紧!'; $ncRed++ }
    if (-not (Test-QueueCompletedCounts $jGood)) { Write-Output '    (negctl) 合法汇总行被判坏 => p/tot 锁过紧!'; $ncRed++ }
    if (-not (Test-NoDebugAtInfo $jGood)) { Write-Output '    (negctl) info 汇总行被判 debug 违例 => 过滤锁过紧!'; $ncRed++ }
    $noT = '{"l":"info","m":"x"}'
    if (Test-RawTimestamp $noT) { Write-Output '    (negctl) 缺 t 的行时间戳锁放行 => 原始行锁没牙!'; $ncRed++ }
    $noM = '{"t":"2026-11-12T08:00:00.1234567+00:00","l":"info"}'
    if (Test-MandatoryFields (Get-ParsedLine $noM)) { Write-Output '    (negctl) 缺 m 的行被放行 => 必需字段锁没牙!'; $ncRed++ }
    $zTs = '{"t":"2026-11-12T08:00:00.1234567Z","l":"info","m":"x"}'
    if (Test-RawTimestamp $zTs) { Write-Output '    (negctl) Z 后缀时间戳被放行 => +00:00 契约锁没牙!'; $ncRed++ }
    $badLv = '{"t":"2026-11-12T08:00:00.1234567+00:00","l":"INFO","m":"x"}'
    if (Test-MandatoryFields (Get-ParsedLine $badLv)) { Write-Output '    (negctl) 大写级别被放行 => 小写枚举锁没牙!'; $ncRed++ }
    $badCat = '{"t":"2026-11-12T08:00:00.1234567+00:00","l":"info","c":"bogus","m":"x"}'
    if (Test-CategoryEnum (Get-ParsedLine $badCat)) { Write-Output '    (negctl) 未知 category 被放行 => 类别枚举锁没牙!'; $ncRed++ }
    $glyph = '{"t":"2026-11-12T08:00:00.1234567+00:00","l":"info","m":"═══ 队列处理完毕"}'
    if (Test-NoTextGlyphs $glyph) { Write-Output '    (negctl) 带 ═══ 的行被放行 => 字形锁没牙!'; $ncRed++ }
    $dbg = '{"t":"2026-11-12T08:00:00.1234567+00:00","l":"debug","c":"progress","m":"x"}'
    if (Test-NoDebugAtInfo (Get-ParsedLine $dbg)) { Write-Output '    (negctl) debug/progress 行被 Info 判据放行 => 过滤锁没牙!'; $ncRed++ }
    $strP = '{"t":"2026-11-12T08:00:00.1234567+00:00","l":"info","c":"queue_completed","m":"x","p":"1","tot":1}'
    if (Test-QueueCompletedCounts (Get-ParsedLine $strP)) { Write-Output '    (negctl) p 落成字符串被放行 => int 锁没牙!'; $ncRed++ }
    $notJson = '这不是 JSON'
    if ($null -ne (Get-ParsedLine $notJson)) { Write-Output '    (negctl) 非 JSON 串被解析成功 => 解析器没牙!'; $ncRed++ }
    CK ($ncRed -eq 0) "selfcheck: judgement chain works as designed ($ncRed defects)"
    $ncOk = ($ncRed -eq 0)
}

# ── 清理 + 汇总 ─────────────────────────────────────────────────────────────
if (-not $KeepWork) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
else { Write-Output "  [keepwork] $work" }
Write-Output ""
Write-Output "===== log-jsonlines: PASS=$($script:pass) FAIL=$($script:fail) ====="
if ($script:fail -gt 0 -or -not $ncOk) { exit 1 }
exit 0
