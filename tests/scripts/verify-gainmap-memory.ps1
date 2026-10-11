# verify-gainmap-memory.ps1 —— 每任务内存预留 vs 实测峰值（2026-09-15 新增，针对 P2-8）
# 背景：QueueProcessor 曾按固定 200MB/任务 钳制并发。实测（4K HDR 源 + GainMap 纯托管编码）峰值 **680.3MB**
#   ⇒ 低估 3.4 倍，高并发 + 大图可 OOM；而普通 ffmpeg 通路 4K 只 53.7MB（流式），200MB 又高估 3.7 倍。
# 本门禁做四件事：
#   ① 从源码解析出**声明的**两档预留（plainMB / linearMB），要求 `实测峰值 ≤ 声明值`（预留不得低于现实）；
#   ② 要求 `GainMap 实测峰值 > plainMB`（证明两档划分有依据；哪天 GainMap 变省内存了这条会红，提示合并档位）；
#   ③ 负控：用故意过小的预留（64MB）去套 GainMap 实测 ⇒ 必须判为「预留不足」，证明断言能识别低估。
#   ④ 并发钳制的 OOM 边界（2026-10-11 新增）：用 `DOTNET_GCHeapHardLimit` 把**可用内存**钉成固定夹具
#      L=2304MB，请求 `-j 8` ⇒ `ClampConcurrencyByMemory` 会选 C=floor(L/linearMB)；预留偏低时它放进 3 并发
#      ⇒ 实测 `System.OutOfMemoryException`、8 个任务只出 6 个产物。断言看**产物计数**，不看内存读数。
#      同臂先跑一条 `-j 2` 控制臂（恒应 8/8），使"缺产物"只能归因到超量准入而不是夹具本身。
#   ⇒ ①②③ 的量纲是"预留 vs 实测峰值"，④ 的量纲是"预留 vs 并发下真放得下的任务数"。
# 用法：pwsh -NoProfile -File tests/scripts/verify-gainmap-memory.ps1
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
$prb = "$root/tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
if (-not (Test-Path $prb)) { $prb = "$root/tests/ServiceProbe/bin/Debug/net11.0/win-x64/ServiceProbe.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $prb + $(if ($prb -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
$src = "$root/src/FfmpegGui/Services/QueueProcessor.cs"
$val = "$root/tests/output/gmmem_$([guid]::NewGuid().ToString('N').Substring(0, 8))"

$pass = 0; $fail = 0; $skip = 0
# ⚠ 2026-09-21（`TESTING.md` 第 81 条同族 · 口径统一）：**整门禁跳过也打印汇总并计数**。
#   原先三条前置守卫直接 `exit 0`、**不打印汇总** ⇒ 门禁在「有汇总」与「无汇总」两种形态间摇摆，
#   读者/运行器无法用同一口径判读（本仓第 62 条要求无汇总的一律**单列**）。
function SkipAll([string]$why) {
    $script:skip++
    Write-Output "SKIP $why"
    Write-Output "===== PASS=0 FAIL=0 SKIP=$skip（⚠ 整门禁未运行，不是通过）====="
    exit 0
}
function Run-Exe([string]$file, [string]$argStr) {
    return (Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru).ExitCode
}
# 启动并采样峰值内存（MB）。间隔 40ms，够密以捕捉 4K GainMap 的瞬时峰值。
# ⚠ 2026-09-16：退出码在本宿主下**取不到**（实测 `$p.ExitCode` 为 $null），曾把"转换其实成功"
#   误判为"转换未成功"（产物与日志都证明成功：`完成 1 项, 失败 0 项`）⇒ 改为**以产物判成功**：
#   产物存在且非空 = 成功；退出码仅作参考信息回显。
function Measure-PeakMB([string]$argStr, [string]$tag, [string]$expectFile) {
    $so = "$val/$tag.out.txt"; $se = "$val/$tag.err.txt"
    $p = Start-Process -FilePath $exe -ArgumentList $argStr -NoNewWindow -PassThru `
        -RedirectStandardOutput $so -RedirectStandardError $se
    $peak = 0
    while (-not $p.HasExited) {
        try { $p.Refresh(); if ($p.WorkingSet64 -gt $peak) { $peak = $p.WorkingSet64 } } catch { }
        Start-Sleep -Milliseconds 40
    }
    $ec = $null
    try { $p.WaitForExit(); $ec = $p.ExitCode } catch { }
    $ok = (Test-Path $expectFile) -and ((Get-Item $expectFile).Length -gt 0)
    return @{ MB = [math]::Round($peak / 1MB, 1); Exit = $ec; Ok = $ok }
}
# 并发臂：一次跑 N 个任务，返回**产出计数**与进程峰值。判据看计数（OOM 会让个别任务缺产物）。
function Measure-PeakMBMulti([string]$argStr, [string]$tag, [string[]]$expectFiles, [string]$logPath) {
    $so = "$val/$tag.out.txt"; $se = "$val/$tag.err.txt"
    $p = Start-Process -FilePath $exe -ArgumentList $argStr -NoNewWindow -PassThru `
        -RedirectStandardOutput $so -RedirectStandardError $se
    $peak = 0
    while (-not $p.HasExited) {
        try { $p.Refresh(); if ($p.WorkingSet64 -gt $peak) { $peak = $p.WorkingSet64 } } catch { }
        Start-Sleep -Milliseconds 40
    }
    try { $p.WaitForExit() } catch { }
    $okN = 0
    foreach ($f in $expectFiles) { if ((Test-Path $f) -and ((Get-Item $f).Length -gt 0)) { $okN++ } }
    $blob = ''
    foreach ($t in @($so, $se, $logPath)) {
        if ($t -and (Test-Path $t)) { $blob += (Get-Content $t -Raw -ErrorAction SilentlyContinue) }
    }
    return @{ MB = [math]::Round($peak / 1MB, 1); OkN = $okN; Total = $expectFiles.Count;
              Oom = ($blob -match 'OutOfMemory') }
}
if (-not (Test-Path $exe)) { SkipAll "FfmpegGui.exe 未构建（先 dotnet build）" }
if (-not (Test-Path $prb)) { SkipAll "缺 ServiceProbe（png3 打标要用）" }

# ── ① 从源码解析声明的两档预留 ──
$srcText = [System.IO.File]::ReadAllText($src)
$mPlain = [regex]::Match($srcText, 'const\s+int\s+plainMB\s*=\s*(\d+)')
$mLinear = [regex]::Match($srcText, 'const\s+int\s+linearMB\s*=\s*(\d+)')
if (-not $mPlain.Success -or -not $mLinear.Success) {
    Write-Output "FAIL 源码里找不到 plainMB / linearMB 声明（EstimatePerTaskMemoryMB 被改动？本门禁需要它们保持可解析）"
    exit 1
}
$plainMB = [int]$mPlain.Groups[1].Value
$linearMB = [int]$mLinear.Groups[1].Value
Write-Output "声明预留: plainMB=${plainMB}MB  linearMB=${linearMB}MB"

New-Item -ItemType Directory -Path $val -Force | Out-Null

# ── ② 4K HDR 素材（16-bit + bt2020/PQ，与 GainMap 编码前置条件一致）──
$src4k = "$val/src4k_hdr.png"
Run-Exe $ff "-y -v error -f lavfi -i `"testsrc=size=3840x2160:rate=1`" -frames:v 1 -pix_fmt rgb48le `"$src4k`"" | Out-Null
if (-not (Test-Path $src4k)) { SkipAll "4K 素材生成失败" }
# 打 cICP 标签（bt2020/PQ）；输出重定向到文件，避免子进程 stdout 混进门禁输出
$soTag = "$val/png3.out.txt"
Start-Process -FilePath $prb -ArgumentList "png3 `"$src4k`" 0 9 16" -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $soTag -RedirectStandardError "$val/png3.err.txt" | Out-Null

# ── ③ 实测两条通路的峰值 ──
$dGm = "$val/gm"; New-Item -ItemType Directory -Path $dGm -Force | Out-Null
$rGm = Measure-PeakMB "--headless -i `"$src4k`" --format jpg --jpeg-gain-map true --output `"$dGm`" --log-file `"$dGm/log.txt`"" 'gm4k' "$dGm/src4k_hdr.jpg"
Write-Output ("  GainMap 4K : exit={0} 峰值={1} MB 产物={2}" -f $rGm.Exit, $rGm.MB, $rGm.Ok)

$dPl = "$val/plain"; New-Item -ItemType Directory -Path $dPl -Force | Out-Null
$rPl = Measure-PeakMB "--headless -i `"$src4k`" --format jpg --output `"$dPl`" --log-file `"$dPl/log.txt`"" 'plain4k' "$dPl/src4k_hdr.jpg"
Write-Output ("  普通    4K : exit={0} 峰值={1} MB 产物={2}" -f $rPl.Exit, $rPl.MB, $rPl.Ok)

if (-not $rGm.Ok -or -not $rPl.Ok) {
    $fail++
    Write-Output "FAIL 转换未成功（产物缺失或为空），无法比较内存（GainMap 产物=$($rGm.Ok) / 普通 产物=$($rPl.Ok)）"
} else {
    $pass++
    Write-Output ("  PASS 两条通路都产出非空产物（退出码仅作参考：GainMap={0} / 普通={1}）" -f $rGm.Exit, $rPl.Exit)
}

# 断言 A：声明预留不得低于实测
if ($rGm.MB -le $linearMB) {
    $pass++; Write-Output ("  PASS A 声明预留 {0}MB ≥ GainMap 实测 {1}MB（预留没在骗人）" -f $linearMB, $rGm.MB)
} else {
    $fail++; Write-Output ("  FAIL A 声明预留 {0}MB < GainMap 实测 {1}MB ⇒ 并发钳制会低估内存，高并发可 OOM" -f $linearMB, $rGm.MB)
}
if ($rPl.MB -le $plainMB) {
    $pass++; Write-Output ("  PASS B 普通档预留 {0}MB ≥ 普通通路实测 {1}MB" -f $plainMB, $rPl.MB)
} else {
    $fail++; Write-Output ("  FAIL B 普通档预留 {0}MB < 普通通路实测 {1}MB" -f $plainMB, $rPl.MB)
}
# 断言 C：两档划分有依据（GainMap 确实比普通通路吃内存）
if ($rGm.MB -gt $plainMB) {
    $pass++; Write-Output ("  PASS C GainMap 实测 {0}MB > 普通档预留 {1}MB ⇒ 两档划分有依据（GainMap 确实需要单独一档）" -f $rGm.MB, $plainMB)
} else {
    $fail++; Write-Output ("  FAIL C GainMap 实测 {0}MB ≤ 普通档预留 {1}MB ⇒ 两档已无差别，可考虑合并档位（本门禁该改）" -f $rGm.MB, $plainMB)
}
# 负控：过小的预留必须被判「预留不足」
$tooSmall = 64
if ($rGm.MB -gt $tooSmall) {
    $pass++; Write-Output ("  PASS negctrl 用 {0}MB 套 GainMap 实测 {1}MB 会判为预留不足 ⇒ 断言 A 能识别低估（非空跑）" -f $tooSmall, $rGm.MB)
} else {
    $fail++; Write-Output ("  FAIL negctrl {0}MB 竟能覆盖实测 {1}MB ⇒ 断言 A 失去意义" -f $tooSmall, $rGm.MB)
}

# ── 断言 D：并发钳制的 OOM 边界（2026-10-11 新增）──
# 夹具可用内存 L=2304MB 由 `DOTNET_GCHeapHardLimit` 造；实测该变量确实改变**被进程**看到的
#   `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` ⇒ 它正是 `ClampConcurrencyByMemory` 唯一的操作数。
# ⚠ L 是**写死的常数，不从 linearMB 派生**：若 L = k×linearMB，那么常数一改夹具跟着缩放，
#   钳制选出的并发数永远不变 ⇒ 断言退化成恒真（本仓"构造恒等"陷阱）。
#   固定 L=2304 时钳制并发 = floor(2304/linearMB)：linearMB=768 ⇒ 3 并发 ⇒ 每任务实际只分到 768MB，
#   而单任务实测峰值 818.4MB ⇒ **真 OOM**（实测 8 个任务只出 6 个产物，日志点名 System.OutOfMemoryException）。
$fixDir = "$val/conc8"; New-Item -ItemType Directory -Path $fixDir -Force | Out-Null
$outDir = "$val/conc8out"; New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$expMany = @()
for ($i = 1; $i -le 8; $i++) {
    $n = "g{0:d2}.png" -f $i
    Copy-Item $src4k (Join-Path $fixDir $n) -Force
    $expMany += (Join-Path $outDir ($n -replace '\.png$', '.jpg'))
}
if ((Get-ChildItem $fixDir -File).Count -ne 8) {
    $fail++
    Write-Output "FAIL D 8 份 4K 夹具未生成齐（复制失败）⇒ 本臂读数无效"
} else {
    $env:DOTNET_GCHeapHardLimit = '0x{0:X}' -f ([long]2304 * 1MB)
    # ClampConcurrencyByMemory 有下限 1（Math.Max(1, …)），读数按它实际会选的并发数报
    $effC = [math]::Max(1, [math]::Floor(2304 / $linearMB))
    Write-Output ("  [D] 硬限 L=2304MB ⇒ 钳制应选并发 C=max(1, floor(2304/{0}))={1}" -f $linearMB, $effC)
    $rD0 = Measure-PeakMBMulti "--headless -i `"$fixDir`" --format jpg --jpeg-gain-map true -j 2 --output `"$outDir`" --log-file `"$outDir/log2.txt`"" 'dctrl' $expMany "$outDir/log2.txt"
    Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    $rD  = Measure-PeakMBMulti "--headless -i `"$fixDir`" --format jpg --jpeg-gain-map true -j 8 --output `"$outDir`" --log-file `"$outDir/log8.txt`"" 'd8' $expMany "$outDir/log8.txt"
    Remove-Item env:DOTNET_GCHeapHardLimit -ErrorAction SilentlyContinue

    # 控制臂：请求 2 并发（钳制只会取 min(2, C)）⇒ 必须 8/8 产出，证明夹具本身跑得通
    if ($rD0.OkN -eq $rD0.Total) {
        $pass++; Write-Output ("  PASS D-ctrl L=2304MB、-j 2 ⇒ 产物 {0}/{0}（峰值 {1}MB）⇒ 两并发放得下，夹具可用" -f $rD0.OkN, $rD0.MB)
    } else {
        $fail++; Write-Output ("  FAIL D-ctrl L=2304MB、-j 2 只产出 {0}/{1}（OOM 命中={2}）⇒ 夹具/素材变了，本门禁的边界读数失效" -f $rD0.OkN, $rD0.Total, $rD0.Oom)
    }
    # 判据臂：请求 8 并发 ⇒ 内存钳制必须把它压到放得下的数目。
    # 本臂**红过的形态**（= 它有效）：linearMB=768 ⇒ C=3 ⇒ 每任务只分到 768MB < 单任务实测峰值 818.4MB
    #   ⇒ 8 个任务只出 6 个产物（2026-10-11 实测）。提到 1152 ⇒ C=2 ⇒ 8/8、峰值 2323MB ⇒ 转绿。
    if ($rD.OkN -eq $rD.Total) {
        $pass++; Write-Output ("  PASS D L=2304MB、请求 -j 8 ⇒ 钳制选 C={0}（每任务 {1}MB）、产物 {2}/{2}（峰值 {3}MB）" -f $effC, [math]::Floor(2304/$effC), $rD.OkN, $rD.MB)
    } else {
        $fail++; Write-Output ("  FAIL D L=2304MB、请求 -j 8 ⇒ 钳制选 C={0}（每任务只分到 {1}MB）、产物 {2}/{3}（OOM 命中={4}）⇒ 预留 {5}MB 低于并发实测需求" -f $effC, [math]::Floor(2304/$effC), $rD.OkN, $rD.Total, $rD.Oom, $linearMB)
    }
}

Write-Output "`n===== PASS=$pass FAIL=$fail SKIP=$skip ====="
# 成功即清理本次工作目录；失败则保留供排查。
# ⚠ 在**被沙箱接管的 agent 环境**里 `Remove-Item -Recurse` 会被拦截并静默失败（实测两次跑完仍留目录），
#   所以在那种环境里会在 tests/output/ 下累积 gmmem_* 目录（每个约 70MB，含 4K 素材）——正常 shell 下不会有此问题。
if ($fail -eq 0) { Remove-Item -Recurse -Force $val -ErrorAction SilentlyContinue }
else { Write-Output "工作目录保留供排查: $val" }
exit $(if ($fail -eq 0) { 0 } else { 1 })
