# verify-gainmap-memory.ps1 —— 每任务内存预留 vs 实测峰值（2026-09-15 新增，针对 P2-8）
# 背景：QueueProcessor 曾按固定 200MB/任务 钳制并发。实测（4K HDR 源 + GainMap 纯托管编码）峰值 **680.3MB**
#   ⇒ 低估 3.4 倍，高并发 + 大图可 OOM；而普通 ffmpeg 通路 4K 只 53.7MB（流式），200MB 又高估 3.7 倍。
# 本门禁做七件事：
#   ① 从源码解析出**声明的**两档预留（plainMB / linearMB），要求 `实测峰值 ≤ 声明值`（预留不得低于现实）；
#   ② 要求 `GainMap 实测峰值 > plainMB`（证明两档划分有依据；哪天 GainMap 变省内存了这条会红，提示合并档位）；
#   ③ 负控：用故意过小的预留（64MB）去套 GainMap 实测 ⇒ 必须判为「预留不足」，证明断言能识别低估。
#   ④ 并发钳制的 OOM 边界（2026-10-11 新增）：用 `DOTNET_GCHeapHardLimit` 把**可用内存**钉成固定夹具
#      L=2304MB，请求 `-j 8` ⇒ `ClampConcurrencyByMemory` 会选 C=floor(L/linearMB)；预留偏低时它放进 3 并发
#      ⇒ 实测 `System.OutOfMemoryException`、8 个任务只出 6 个产物。断言看**产物计数**，不看内存读数。
#      同臂先跑一条 `-j 2` 控制臂（恒应 8/8），使"缺产物"只能归因到超量准入而不是夹具本身。
#   ⑤ 准入门读数的**第二个独立来源**对账（2026-10-12 新增，实现见 src/FfmpegGui/Services/MemoryAdmission.cs）：
#      产品侧 `GlobalMemoryStatusEx` 打印的总物理量/空闲量，与 CIM（Win32_ComputerSystem /
#      Win32_OperatingSystem）各比一次。互操作结构体布局错（或忘赋 dwLength）**通常照样返回 true、返回垃圾**
#      ⇒ 只有第二个读者能抓。总量是稳定量 ⇒ 容差 512MB；空闲量会变 ⇒ 只比量级（max(2048MB, 25%)）。
#   ⑥ G 臂（**默认 SKIP**）：G-1 进程内**确定性**走完"等待→超时→仍继续"（牙齿在这里），
#      G-2 是 8K×4 的**不回退**臂（产物齐 + 峰值有界；日志是否点名只作记录，见下方注释），
#      G-3 是**析取式**"并发需求 > 起臂前空闲 ⇒ 准入必须点名"（需求用实测 3363MB×4，不从被检常数派生），
#      G-4 把 G-2/G-3 在**回退管线**（`--color-engine legacy`）上再证一次（F-5/F-6 只是源形锁，这条才是行为证据）。
#      开法：`$env:GMMEM_HEAVY='1'`（G-2 与 G-4 各要吃十几 GB 并压出分钟级抖动）。
#   ⑦ F 臂：准入门的**源形锁**——`RawColorPipeline`（引擎出口）与 `QueueProcessor`（legacy / 引擎不可走
#      时轮到的回退管线）**两条入口都必须挂同一道门**，且都要传本帧像素数、都要早于整帧分配；
#      再用第二条独立顺序事实反证索引机器本身没坏
#      （E/G 只量 `MemoryAdmission` 自己 ⇒ 把调用点删了它们照样全绿 = 无牙）。
#   ⇒ ①②③ 的量纲是"预留 vs 实测峰值"，④⑥⑦ 是"预留/准入 vs 并发下放不放得下、这道门在不在链路上"，⑤ 是"读数本身可不可信"。
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

# ── 断言 E：准入门的读数必须有**第二个独立来源**对账（2026-10-12 新增）──
# `GlobalMemoryStatusEx` 的结构体布局写错（或忘了先赋 dwLength）通常**照样返回 true**、返回一堆垃圾
#   ⇒ 只有拿第二个读者的数才能抓到。第二个读者 = CIM：总物理量走 Win32_ComputerSystem（稳定量，
#   容差收到 512MB），空闲量走 Win32_OperatingSystem.FreePhysicalMemory（会变 ⇒ 只放 max(2048MB, 25%)，
#   用来抓"数量级/布局错"，不用来比精度）。
$admitOut = (& $prb 'memadmit') 2>&1 | Out-String
$mAdmit = [regex]::Match($admitOut, 'ADMIT availMB=(\d+) totalMB=(\d+) load=(\d+)')
$mSum   = [regex]::Match($admitOut, 'PROBE RESULT: pass=(\d+) fail=(\d+)')
if (-not $mAdmit.Success) {
    $fail++
    Write-Output "FAIL E 准入门探针没给出 ADMIT 读数行 ⇒ 无法与独立来源对账（探针没跑？模式名不对？）"
} else {
    $pE = [int]$mSum.Groups[1].Value; $fE = [int]$mSum.Groups[2].Value
    if ($fE -eq 0 -and $pE -ge 4) {
        $pass++; Write-Output ("  PASS E-1 探针自证 {0} 条通过 / {1} 红（① 读数自洽 + ② 三点拟合 ≤15%）" -f $pE, $fE)
    } else {
        $fail++; Write-Output ("  FAIL E-1 探针自证 {0}/{1}（①② 有红 ⇒ 逐条见下）" -f $pE, $fE)
        ($admitOut -split "`r?`n" | Where-Object { $_ -match '^FAIL' }) | ForEach-Object { Write-Output ("      " + $_) }
    }
    $prodTotal = [int]$mAdmit.Groups[2].Value          # totalMB（稳定量，用来抓结构体布局错）
    $prodAvail = [int]$mAdmit.Groups[1].Value          # availMB（会变，只比量级）
    $cimTotalMB = [math]::Round(((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1MB))
    $cimFreeMB  = [math]::Round(((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory * 1KB) / 1MB)
    $dTotal = [math]::Abs($prodTotal - $cimTotalMB)
    if ($dTotal -le 512) {
        $pass++; Write-Output ("  PASS E-2 总物理量与独立来源一致：产品 {0}MB vs CIM {1}MB（差 {2}MB ≤512）" -f $prodTotal, $cimTotalMB, $dTotal)
    } else {
        $fail++; Write-Output ("  FAIL E-2 总物理量差 {0}MB（产品 {1} vs CIM {2}）⇒ 互操作结构体/参数不对，读数不可信" -f $dTotal, $prodTotal, $cimTotalMB)
    }
    $tolAvail = [math]::Max(2048, $cimFreeMB * 0.25)
    $dAvail = [math]::Abs($prodAvail - $cimFreeMB)
    if ($dAvail -le $tolAvail) {
        $pass++; Write-Output ("  PASS E-3 空闲量与 CIM 同量级：产品 {0}MB vs CIM {1}MB（差 {2}MB ≤ 容差 {3:N0}）" -f $prodAvail, $cimFreeMB, $dAvail, $tolAvail)
    } else {
        $fail++; Write-Output ("  FAIL E-3 空闲量差 {0}MB（产品 {1} vs CIM {2}）超出容差 {3:N0} ⇒ 准入门会按错读数放行/拦住" -f $dAvail, $prodAvail, $cimFreeMB, $tolAvail)
    }
}

# ── 断言 F：准入门必须**接在链路上**、且必须在那次整帧分配之前（2026-10-12 新增，源形锁）──
#   E/G 两只臂量的都是 `MemoryAdmission` **自己**；把 `RawColorPipeline` 里那一行删掉，它们照样全绿
#   ⇒ 判据无牙（本仓反复踩的同一条：探针活着 ≠ 有人调它）。四条各有分工：
#   F-1 调用点唯一（删了=0 处 ⇒ 红；重复=2 处 ⇒ 红）；
#   F-2 需求量传的是**本帧像素数**——传常量就是"永远够"，门形同虚设；
#   F-3 位置早于 `cms_gm_` 临时缓冲（晚于分配 = 先把内存占了再问）；
#   F-4 同一套 IndexOf 必须也量出另一条**独立**的顺序事实（`cms_gm_` 早于 `GainMapEncoder.EncodeAsync`）
#       ⇒ 反证"顺序机器"本身没坏（若两个索引都恒为 -1 或恒正，F-3 就是恒真的假绿）。
$rawSrc = "$root/src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs"
if (-not (Test-Path $rawSrc)) {
    $fail++
    Write-Output "FAIL F 找不到 $rawSrc ⇒ 无法核对准入门接线（出口文件搬走也要有人知道，不许静默跳过）"
} else {
    $rc = Get-Content $rawSrc -Raw
    $nCall  = ([regex]::Matches($rc, 'MemoryAdmission\.WaitAsync\(')).Count
    $iCall  = $rc.IndexOf('MemoryAdmission.WaitAsync(')
    $iAlloc = $rc.IndexOf('cms_gm_')
    $iEnc   = $rc.IndexOf('GainMapEncoder.EncodeAsync')
    if ($nCall -eq 1) {
        $pass++; Write-Output ("  PASS F-1 准入门调用点唯一（命中 {0} 处，字符偏移 {1}）" -f $nCall, $iCall)
    } else {
        $fail++; Write-Output ("  FAIL F-1 准入门调用点命中 {0} 处（期望 1）⇒ 0 处=链路没了这道门，>1 处=同一帧重复排队" -f $nCall)
    }
    if ($rc -match 'MemoryAdmission\.WaitAsync\(\(long\)w \* h') {
        $pass++; Write-Output "  PASS F-2 需求量传的是本帧像素数 (long)w*h（不是常量 ⇒ 8K 那一档的实测才落得到门上）"
    } else {
        $fail++; Write-Output "  FAIL F-2 准入门的需求量不是 (long)w*h ⇒ 它按别的东西放行，实测 8K=3363MB 那条账对白记了"
    }
    if ($iCall -ge 0 -and $iAlloc -gt $iCall) {
        $pass++; Write-Output ("  PASS F-3 准入在整帧分配之前（调用偏移 {0} < 临时缓冲偏移 {1}）" -f $iCall, $iAlloc)
    } else {
        $fail++; Write-Output ("  FAIL F-3 准入没有早于整帧分配（调用={0} 分配={1}）⇒ 先占内存再问 = 门不起作用" -f $iCall, $iAlloc)
    }
    if ($iAlloc -ge 0 -and $iEnc -gt $iAlloc) {
        $pass++; Write-Output ("  PASS F-4 顺序机器反证：同法量出解码临时缓冲({0}) 早于编码({1})" -f $iAlloc, $iEnc)
    } else {
        $fail++; Write-Output ("  FAIL F-4 独立的顺序事实量不出来（缓冲={0} 编码={1}）⇒ 索引本身不可信，F-3 是恒真的" -f $iAlloc, $iEnc)
    }
}

# ── 断言 F-5/F-6：GainMap 的**回退管线**必须挂同一道门（2026-10-12）──
#   `QueueProcessor.ProcessGainMapJpegAsync` 在「--color-engine legacy」或「引擎不可走」（增益图 + 最长边缩放）
#   时才轮到，它同样是整帧驻留（RAW 12B/px + RGBA 16B/px + GainMapEncoder 三份 float）。
#   本仓既有教训就是"两条入口只改了一条"（`GainMapJpegCodec` 的存在理由）⇒ 引擎出口装了门而这里没装 = 回退路径没门。
$qps = "$root/src/FfmpegGui/Services/QueueProcessor.cs"
if (-not (Test-Path $qps)) {
    $fail++
    Write-Output "FAIL F-5 找不到 $qps ⇒ 无法核对回退管线的准入接线（文件搬走也要有人知道）"
} else {
    $qc = Get-Content $qps -Raw
    $nQ = ([regex]::Matches($qc, 'MemoryAdmission\.WaitAsync\(')).Count
    $pxQ = $qc -match 'MemoryAdmission\.WaitAsync\(\(long\)width \* height'
    $iQ = $qc.IndexOf('MemoryAdmission.WaitAsync(')
    $iRead = $qc.IndexOf('File.ReadAllBytesAsync(rawPath')
    if ($nQ -eq 1 -and $pxQ) {
        $pass++; Write-Output ("  PASS F-5 回退管线同一道门：调用点唯一（偏移 {0}）且需求量传本帧像素数 (long)width*height" -f $iQ)
    } else {
        $fail++; Write-Output ("  FAIL F-5 回退管线准入不符（调用点 {0} 处，期望 1；像素数形参={1}）⇒ legacy / 引擎不可走时没有门" -f $nQ, $pxQ)
    }
    if ($iQ -ge 0 -and $iRead -gt $iQ) {
        $pass++; Write-Output ("  PASS F-6 回退管线的准入早于整帧读入（调用 {0} < 读入 {1}）" -f $iQ, $iRead)
    } else {
        $fail++; Write-Output ("  FAIL F-6 回退管线准入没有早于整帧读入（调用={0} 读入={1}）⇒ 先把内存占了再问" -f $iQ, $iRead)
    }
}

# ── 断言 G（**默认 SKIP**）：8K 压力臂，证明准入门在真缺内存时确实介入且不静默 OOM ──
# 单任务预计 ≈3354MB（拟合）⇒ 4 并发 = 13.4GB，超过本机空闲；没有准入门时实测是逐任务 OOM。
# 默认不跑：它要吃十几 GB、会把机器压成分钟级抖动，且与计时判据互斥。
# 开关：`$env:GMMEM_HEAVY = '1'`（本仓"两条异步命令间睡固定时长=赌"的规矩同样适用 ⇒ 本臂不读时间，只读**产物数 + 日志点名**）。
if ($env:GMMEM_HEAVY -eq '1') {
    # G-1：**确定性**走完"等待 → 超时 → 仍继续"这条完整路径（进程内，预算压到 2s）。
    #   牙齿在这里 —— 不靠把真机器压到缺内存才碰得到那条分支（第一版就是这么写的，结果 8K×4 全成功、
    #   日志一句点名都没有 ⇒ 判据永远碰不到 = 无牙）。P-1..P-5 见 ServiceProbe 的 memadmit pressure。
    $press = (& $prb 'memadmit' 'pressure') 2>&1 | Out-String
    $mP = [regex]::Match($press, 'PROBE RESULT: pass=(\d+) fail=(\d+)')
    if ($mP.Success -and [int]$mP.Groups[2].Value -eq 0 -and [int]$mP.Groups[1].Value -ge 5) {
        $pass++; Write-Output ("  PASS G-1 准入门完整路径 P-1..P-5 全绿（pass={0} fail={1}）" -f [int]$mP.Groups[1].Value, [int]$mP.Groups[2].Value)
    } else {
        $fail++; Write-Output ("  FAIL G-1 准入门路径不自洽（读数={0}）⇒ 「会等/会点名/会放行」至少一条没做到" -f $mP.Value)
        ($press -split "`r?`n" | Where-Object { $_ -match '^FAIL' }) | ForEach-Object { Write-Output ("      " + $_) }
    }
    # G-2：**不回退**臂。4 份 8K、-j 4，判据 = 4/4 产出且峰值有界（≤ 4× 单任务实测峰值 + 20%）。
    #   日志里有没有 `[MemoryAdmission]` 只作**记录**：当时空闲够就不该出现，硬要求它 = 又一个无牙判据。
    #   ⚠ 必须带 `--log-level Debug`：`ConsoleQueueObserver.cs:167` 把 `item.Log` 按 Debug 回显，
    #     默认 Info 级别下这条链的日志**整段不可见**（本仓 2026-10-05 记过同族）。
    $hIn = "$val/h8k"; New-Item -ItemType Directory -Path $hIn -Force | Out-Null
    $hOut = "$val/h8kout"; New-Item -ItemType Directory -Path $hOut -Force | Out-Null
    Run-Exe $ff "-y -v error -f lavfi -i `"testsrc=size=7680x4320:rate=1`" -frames:v 1 -pix_fmt rgb48le `"$hIn/big.png`"" | Out-Null
    $hExp = @()
    for ($i = 1; $i -le 4; $i++) {
        Copy-Item "$hIn/big.png" "$hIn/b$i.png" -Force
        $hExp += "$hOut/b$i.jpg"
    }
    # 起臂**前**采一次空闲：G-3 要的是"当时到底放不放得下 4 帧"这个前提，跑完再采量的是别的时间点。
    $freeStartMB = [math]::Round(((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory * 1KB) / 1MB)
    $rH = Measure-PeakMBMulti "--headless -i `"$hIn`" --format jpg --jpeg-gain-map true --log-level Debug -j 4 --output `"$hOut`" --log-file `"$hOut/log.txt`"" 'heavy8k' $hExp "$hOut/log.txt"
    $admitted = (Test-Path "$hOut/log.txt") -and ((Get-Content "$hOut/log.txt" -Raw -ErrorAction SilentlyContinue) -match '\[MemoryAdmission\]')
    $freeNowMB = [math]::Round(((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory * 1KB) / 1MB)
    $capMB = [math]::Round(4 * 3363 * 1.2)          # 单任务实测峰值(步骤②那轮 8K=3363MB) × 4 + 20%
    if ($rH.OkN -eq $rH.Total -and $rH.MB -le $capMB) {
        $pass++; Write-Output ("  PASS G-2 8K×4 全产出、峰值 {0}MB ≤ 有界 {1}MB（日志点名准入={2}，当时空闲 {3}MB）" -f $rH.MB, $capMB, $admitted, $freeNowMB)
    } else {
        $fail++; Write-Output ("  FAIL G-2 8K×4：产物 {0}/{1}、峰值 {2}MB（界 {3}MB）、OOM={4} ⇒ 大帧并发没有回退这件事不成立" -f $rH.OkN, $rH.Total, $rH.MB, $capMB, $rH.Oom)
    }
    # G-3：**并发需求超过当时空闲时，准入门必须可见**（析取式，2026-10-12）。
    #   需求用**实测单任务峰值 3363MB × 4**，不用产品自己的拟合常数 —— 判据若拿被检量派生就等于没检。
    #   另一支是诚实出口：起臂前空闲就真够 4 帧时，日志里没有准入点名是**正确的**（硬要求它出现 = 空闲机器上恒红）。
    #   牙齿：把 F-1 那个调用点删掉后，这一支在本机（空闲 11.9GB < 13.4GB）**必红**；F-1 只证"在"，这一条证"用上了"。
    $needAllMB = 4 * 3363
    if ($freeStartMB -ge $needAllMB -or $admitted) {
        $pass++; Write-Output ("  PASS G-3 起臂前空闲 {0}MB vs 4 帧实测需求 {1}MB ⇒ {2}" -f `
            $freeStartMB, $needAllMB, $(if ($freeStartMB -ge $needAllMB) { '空闲本就够，准入不点名是对的' } else { '需求已超空闲且日志点名的准确入了' }))
    } else {
        $fail++; Write-Output ("  FAIL G-3 起臂前空闲 {0}MB < 4 帧需求 {1}MB 而日志**零**准入点名 ⇒ 准入门没接在这条链上" -f $freeStartMB, $needAllMB)
    }
    # G-4：**同一件事在回退管线上再证一次**（`--color-engine legacy`）。
    #   G-2/G-3 走的是引擎出口；把 QueueProcessor 里那道门删掉它们照样全绿 ⇒ F-5/F-6 只是**源形锁**，
    #   这条才是"回退路径上真有位子在看门"的行为证据。判据与 G-2/G-3 同式（产物齐 + 峰值有界 + 析取点名）。
    #   代价：再吃一遍十几 GB ⇒ 与 G-2 一样只在显式开关下跑。
    $lOut = "$val/h8kout_legacy"; New-Item -ItemType Directory -Path $lOut -Force | Out-Null
    $lExp = @()
    for ($i = 1; $i -le 4; $i++) { $lExp += "$lOut/b$i.jpg" }
    $freeStartL = [math]::Round(((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory * 1KB) / 1MB)
    $rL = Measure-PeakMBMulti "--headless -i `"$hIn`" --format jpg --jpeg-gain-map true --color-engine legacy --log-level Debug -j 4 --output `"$lOut`" --log-file `"$lOut/log.txt`"" 'heavy8k_legacy' $lExp "$lOut/log.txt"
    $admittedL = (Test-Path "$lOut/log.txt") -and ((Get-Content "$lOut/log.txt" -Raw -ErrorAction SilentlyContinue) -match '\[MemoryAdmission\]')
    if ($rL.OkN -eq $rL.Total -and $rL.MB -le $capMB -and ($freeStartL -ge $needAllMB -or $admittedL)) {
        $pass++; Write-Output ("  PASS G-4 legacy 路 8K×4 全产出、峰值 {0}MB ≤ {1}MB（起臂前空闲 {2}MB vs 需求 {3}MB，准入点名={4}）" -f `
            $rL.MB, $capMB, $freeStartL, $needAllMB, $admittedL)
    } else {
        $fail++; Write-Output ("  FAIL G-4 legacy 路 8K×4：产物 {0}/{1}、峰值 {2}MB（界 {3}MB）、起臂前空闲 {4}MB vs 需求 {5}MB、准入点名={6}、OOM={7}" -f `
            $rL.OkN, $rL.Total, $rL.MB, $capMB, $freeStartL, $needAllMB, $admittedL, $rL.Oom)
    }
} else {
    $skip++
    Write-Output "SKIP G 准入门压力臂未跑（G-1 要起进程、G-2 要吃十几 GB）⇒ 显式 GMMEM_HEAVY=1 才跑；**不是**通过"
}

Write-Output "`n===== PASS=$pass FAIL=$fail SKIP=$skip ====="
# 成功即清理本次工作目录；失败则保留供排查。
# ⚠ 在**被沙箱接管的 agent 环境**里 `Remove-Item -Recurse` 会被拦截并静默失败（实测两次跑完仍留目录），
#   所以在那种环境里会在 tests/output/ 下累积 gmmem_* 目录（每个约 70MB，含 4K 素材）——正常 shell 下不会有此问题。
if ($fail -eq 0) { Remove-Item -Recurse -Force $val -ErrorAction SilentlyContinue }
else { Write-Output "工作目录保留供排查: $val" }
exit $(if ($fail -eq 0) { 0 } else { 1 })
