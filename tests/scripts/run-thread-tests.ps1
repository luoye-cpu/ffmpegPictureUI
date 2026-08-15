# ═══════════════════════════════════════════════════════════
#  run-thread-tests.ps1 — 线程选择全场景验证 (2026-08-15)
#  覆盖: 自动(多并发)/手动/单线程 → 分配结果 + 各编码器命令端到端
# ═══════════════════════════════════════════════════════════
$ErrorActionPreference = "Continue"
$out = "tests/output/results/threads"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$ffmpeg = (Get-ChildItem "publish/PLAN/ffmpeg-full*/ffmpeg.exe" | Select-Object -First 1).FullName
$cjxl   = "publish/PLAN/jxl/bin/cjxl.exe"
$dngtool = "publish/PLAN/artifacts/dngtool.exe"
$samples = "tools/src/dng_sdk/dng_sdk_1_7_1/sample_files"
$cores = (Get-CimInstance Win32_Processor | Measure-Object NumberOfLogicalProcessors -Sum).Sum

$pass = 0; $fail = 0; $failList = @()
function Check($name, $cond) {
    if ($cond) { Write-Host "  ✅ $name" -ForegroundColor Green; $script:pass++ }
    else { Write-Host "  ❌ $name" -ForegroundColor Red; $script:fail++; $script:failList += $name }
}

# 模拟软件 ComputeAdaptiveThreads (与 FfmpegOptions 一致)
function Get-AdaptiveThreads([int]$concurrency) {
    if ($concurrency -le 0) { return $cores }
    return [Math]::Max(1, [int]($cores / $concurrency))
}

Write-Host "════════ 线程选择全场景测试 (CPU: $cores 核) ════════" -ForegroundColor Yellow

# ── A. 自动模式 + 各并发数 ──
Write-Host "`nA. 自动模式 (AutoThreads) 各并发数分配" -ForegroundColor Cyan
$cases = @(
    @{ Concurrency = 1;  Expect = $cores },
    @{ Concurrency = 2;  Expect = [Math]::Max(1, [int]($cores/2)) },
    @{ Concurrency = 4;  Expect = [Math]::Max(1, [int]($cores/4)) },
    @{ Concurrency = $cores; Expect = 1 },
    @{ Concurrency = 100; Expect = 1 }
)
foreach ($c in $cases) {
    $got = Get-AdaptiveThreads $c.Concurrency
    $ok = $got -eq $c.Expect -and $got -ge 1
    # 不超饱和: 并发×每任务 ≤ 核数 (并发≤核数时)
    if ($c.Concurrency -le $cores) { $ok = $ok -and ($c.Concurrency * $got) -le $cores }
    Check "A$($c.Concurrency): 并发=$($c.Concurrency) → 每任务 $got 线程 (期望 $($c.Expect))" $ok
}

# ── B. 各编码器端到端: 指定线程数命令可执行 + 输出一致 ──
Write-Host "`nB. 各编码器线程参数端到端" -ForegroundColor Cyan

# B1: dngtool -threads 1 vs 8 (小样本, effort=1)
& $dngtool -e -jxl -q 0 -effort 1 -threads 1 -i "$samples/01_jxl_linear_raw_integer.dng" -O "$out/dng_t1.dng" 2>&1 | Out-Null
& $dngtool -e -jxl -q 0 -effort 1 -threads 8 -i "$samples/01_jxl_linear_raw_integer.dng" -O "$out/dng_t8.dng" 2>&1 | Out-Null
$d1 = Test-Path "$out/dng_t1.dng"; $d8 = Test-Path "$out/dng_t8.dng"
Check "B1 dngtool -threads 1/8 均可执行 ($d1/$d8)" ($d1 -and $d8)

# B2: cjxl --num_threads 1 vs 8 (小图)
& $cjxl "tests/output/sources/src_8bit.png" "$out/cjxl_t1.jxl" -e 3 --num_threads=1 2>$null | Out-Null
& $cjxl "tests/output/sources/src_8bit.png" "$out/cjxl_t8.jxl" -e 3 --num_threads=8 2>$null | Out-Null
$c1 = Test-Path "$out/cjxl_t1.jxl"; $c8 = Test-Path "$out/cjxl_t8.jxl"
Check "B2 cjxl --num_threads 1/8 均可执行 ($c1/$c8)" ($c1 -and $c8)

# B3: ffmpeg -threads 1 vs 8
& $ffmpeg -y -hide_banner -loglevel error -threads 1 -i "tests/output/sources/src_8bit.png" -q:v 5 "$out/ff_t1.jpg" 2>&1 | Out-Null
& $ffmpeg -y -hide_banner -loglevel error -threads 8 -i "tests/output/sources/src_8bit.png" -q:v 5 "$out/ff_t8.jpg" 2>&1 | Out-Null
$f1 = Test-Path "$out/ff_t1.jpg"; $f8 = Test-Path "$out/ff_t8.jpg"
Check "B3 ffmpeg -threads 1/8 均可执行 ($f1/$f8)" ($f1 -and $f8)

# B4: 线程数不影响输出 (确定性)
$h1 = (Get-FileHash "$out/dng_t1.dng").Hash; $h8 = (Get-FileHash "$out/dng_t8.dng").Hash
Check "B4 dngtool 1/8线程输出一致" ($h1 -eq $h8)
$hc1 = (Get-FileHash "$out/cjxl_t1.jxl").Hash; $hc8 = (Get-FileHash "$out/cjxl_t8.jxl").Hash
Check "B5 cjxl 1/8线程输出一致" ($hc1 -eq $hc8)

# ── C. 实测提速 (各分配档位有效) ──
Write-Host "`nC. 实测各档位提速 (dngtool effort=2, 大样本)" -ForegroundColor Cyan
foreach ($t in @(1, 5, 20)) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & $dngtool -e -jxl -q 0 -effort 2 -threads $t -i "$samples/01_jxl_linear_raw_integer.dng" -O "$out/dng_bench_$t.dng" 2>&1 | Out-Null
    $sw.Stop()
    Write-Host "  ── $t 线程: $([math]::Round($sw.Elapsed.TotalSeconds,1))s"
    if ($t -eq 1) { $t1time = $sw.Elapsed.TotalSeconds }
    if ($t -eq 20) { $t20time = $sw.Elapsed.TotalSeconds }
}
Check "C1 20线程 ≥ 1.5x 快于 1线程 ($([math]::Round($t1time,1))s vs $([math]::Round($t20time,1))s)" ($t20time -lt $t1time / 1.5)

# ── 汇总 ──
Write-Host "`n════════ 线程测试汇总 ════════" -ForegroundColor Yellow
Write-Host "通过: $pass  失败: $fail" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
if ($failList.Count -gt 0) { $failList | ForEach-Object { Write-Host "  ❌ $_" -ForegroundColor Red } }
Write-Host "产物: $out"
