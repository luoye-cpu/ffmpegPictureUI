# ═══════════════════════════════════════════════════════════
#  run-full-matrix-test-safe.ps1 — FFmpegPictureUI 完整实机测试（内存安全版）
#  安全措施:
#   1. 大图预缩小 (≤850px 工作副本), 大图高负载问题由 Layer 1 全分辨率冒烟覆盖
#   2. 固定 -j 1 并发 + --threads 4 (ffmpeg 不吃满全核)
#   3. 每测试独立进程 + 超时 taskkill /T /F 杀进程树
#   4. 测试前内存水位检查 (<6GB 等待)
#  覆盖: 格式组合矩阵 + HDR BT.2020/SDR × 色彩深度组合
#  ═══════════════════════════════════════════════════════════
$ErrorActionPreference = "Continue"
$root = "C:\PLAN\ffmpegPictureUI"
Set-Location $root

$appDir  = "$root\publish\build\FFmpegPictureUI-dev-x64-full"
$exe     = "$appDir\FfmpegGui.exe"
$ffexe   = (Get-ChildItem "$root\publish\PLAN\ffmpeg-full*\ffmpeg.exe"  | Select-Object -First 1).FullName
$ffprobe = (Get-ChildItem "$root\publish\PLAN\ffmpeg-full*\ffprobe.exe" | Select-Object -First 1).FullName
$env:FFMPEGGUI_PLAN_DIR = "$appDir\PLAN"

$out     = "$root\tests\output\results\matrix"
$logFile = "$root\tests\output\results\matrix_safe.log"
$srcSet  = "$out\safe_sources"
New-Item -ItemType Directory -Force -Path $out, $srcSet | Out-Null

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$out\_exec.out"; $e = "$out\_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}

$script:results = [System.Collections.Generic.List[pscustomobject]]::new()
$script:passCount = 0; $script:failCount = 0; $script:timeoutCount = 0
$script:totalSw = [System.Diagnostics.Stopwatch]::StartNew()

function Log([string]$msg, [string]$color = "White") {
    $line = "[$(Get-Date -Format 'HH:mm:ss')] $msg"
    Write-Host $line -ForegroundColor $color
    Add-Content -Path $logFile -Value $line -Encoding UTF8
}

function Get-FreeGB { (Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1MB }

function Wait-ForMemory([double]$minGB) {
    for ($i = 0; $i -lt 24; $i++) {
        if ((Get-FreeGB) -ge $minGB) { return $true }
        Log "    ⏳ 可用内存 $([math]::Round((Get-FreeGB),1))GB < ${minGB}GB, 等待..." "DarkYellow"
        Start-Sleep -Seconds 5
    }
    return (Get-FreeGB) -ge $minGB
}

function Kill-AppLeftovers {
    Get-Process -Name FfmpegGui, ffmpeg, ffprobe, cjxl, cjpegli, djxl, dngtool, JxrEncApp, JxrDecApp, exiftool -ErrorAction SilentlyContinue |
        ForEach-Object { Log "    ⚠️ 清理残留进程 $($_.Name)($($_.Id))" "DarkYellow"; try { $_.Kill() } catch { } }
}

# ── 核心函数: 独立进程 + 超时强杀 ──
function Invoke-App([string]$argString, [string]$stdoutLog, [string]$stderrLog, [int]$timeoutSec) {
    $p = Start-Process -FilePath $exe -ArgumentList $argString `
        -WorkingDirectory $appDir -NoNewWindow -PassThru `
        -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog
    $timedOut = $false
    if (-not $p.WaitForExit($timeoutSec * 1000)) {
        $timedOut = $true
        & taskkill /T /F /PID $p.Id 2>$null | Out-Null
        try { $p.WaitForExit(10000) | Out-Null } catch { }
    }
    if ($p.ExitCode -is [void]) { $script:exitCode = -1 } else { $script:exitCode = $p.ExitCode }
    return $timedOut
}

function Run-Test {
    param(
        [string]$TestId, [string]$InputFile, [string]$Format, [string]$Encoder,
        [string]$ColorSpace = "", [string]$BitDepth = "",
        [hashtable]$ExtraArgs = @{},
        [string]$Category = "test", [int]$TimeoutSec = 100
    )
    $testOutDir = "$out\$Category\$TestId"
    New-Item -ItemType Directory -Force -Path $testOutDir | Out-Null
    $stdoutLog = "$testOutDir\stdout.log"; $stderrLog = "$testOutDir\stderr.log"

    Wait-ForMemory 6 | Out-Null
    Kill-AppLeftovers

    # 构建参数串
    $argString = "--headless -i `"$InputFile`" -o `"$testOutDir`" -f $Format -e $Encoder -j 1 --threads 4"
    if ($ColorSpace) { $argString += " --color-space `"$ColorSpace`"" }
    if ($BitDepth)   { $argString += " --bit-depth $BitDepth" }
    foreach ($kv in $ExtraArgs.GetEnumerator()) {
        $val = "$($kv.Value)"; if ($val -match ' ') { $val = "`"$val`"" }
        $argString += " --$($kv.Key) $val"
    }

    $desc = "${TestId}: $(Split-Path $InputFile -Leaf) -> $Format/$Encoder"
    if ($ColorSpace) { $desc += " [$ColorSpace]" }
    if ($BitDepth) { $desc += " ${BitDepth}bit" }
    Log "  -- $desc" "Cyan"

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $timedOut = Invoke-App $argString $stdoutLog $stderrLog $TimeoutSec
        $sw.Stop()
        $elapsed = $sw.Elapsed.TotalSeconds
        $outText = (Get-Content $stdoutLog -Raw -ErrorAction SilentlyContinue) + (Get-Content $stderrLog -Raw -ErrorAction SilentlyContinue)

        $inputBase = [System.IO.Path]::GetFileNameWithoutExtension($InputFile)
        $expectedExt = ".$Format"
        $outFile = Get-ChildItem $testOutDir -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq "$inputBase$expectedExt" -or $_.Name -eq "$inputBase$expectedExt.png" } |
            Select-Object -First 1
        if (-not $outFile) {
            $outFile = Get-ChildItem $testOutDir -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -notin @("stdout.log","stderr.log") } | Select-Object -First 1
        }
        $fileOk = $null -ne $outFile

        if ($timedOut) {
            $script:failCount++; $script:timeoutCount++
            Log "    [TIMEOUT] ${elapsed}s > ${TimeoutSec}s (已杀进程树)" "Red"
            $script:results.Add([pscustomobject]@{ TestId=$TestId; Category=$Category; Status="TIMEOUT"
                Input=$InputFile; Format=$Format; Encoder=$Encoder; ColorSpace=$ColorSpace; BitDepth=$BitDepth
                Elapsed=[math]::Round($elapsed,1); SizeKB=0; Probe=""; Log="超时 ${TimeoutSec}s" })
            return
        }

        $okLine  = $outText -match "完成 1 项, 失败 0 项"
        if ($okLine -and $fileOk) {
            $script:passCount++
            $fileKB = [math]::Round($outFile.Length / 1KB, 1)
            $probe = ""; try {
                $probe = (Exec $ffprobe "-v error -select_streams v:0 -show_entries `"stream=pix_fmt,color_space,color_primaries,color_transfer`" -of csv=p=0 `"$($outFile.FullName)`"" | Out-String).Trim()
            } catch { $probe = "probe-error" }
            Log "    [PASS] ${elapsed}s/${fileKB}KB $probe" "Green"
            $script:results.Add([pscustomobject]@{ TestId=$TestId; Category=$Category; Status="PASS"
                Input=$InputFile; Format=$Format; Encoder=$Encoder; ColorSpace=$ColorSpace; BitDepth=$BitDepth
                Elapsed=[math]::Round($elapsed,1); SizeKB=$fileKB; Probe=$probe; Log="" })
        } else {
            $script:failCount++
            $err = ($outText -split "`n" | Where-Object { $_ -match "失败|错误|error" } | Select-Object -First 1)
            if (-not $err) { $err = "未生成输出文件" } else { $err = $err.Trim() }
            Log "    [FAIL] ${elapsed}s: $err" "Red"
            $script:results.Add([pscustomobject]@{ TestId=$TestId; Category=$Category; Status="FAIL"
                Input=$InputFile; Format=$Format; Encoder=$Encoder; ColorSpace=$ColorSpace; BitDepth=$BitDepth
                Elapsed=[math]::Round($elapsed,1); SizeKB=0; Probe=""; Log=$err })
        }
    } catch {
        $sw.Stop(); $script:failCount++
        Log "    [EXCEPTION] $($_.Exception.Message)" "Red"
        $script:results.Add([pscustomobject]@{ TestId=$TestId; Category=$Category; Status="FAIL"
            Input=$InputFile; Format=$Format; Encoder=$Encoder; ColorSpace=$ColorSpace; BitDepth=$BitDepth
            Elapsed=[math]::Round($sw.Elapsed.TotalSeconds,1); SizeKB=0; Probe=""; Log=$_.Exception.Message })
    }
}

# ═══════════════════════════════════════════════════════════
# 第 0 步: 生成缩小工作副本 (≤850px, 减小编码内存占用)
# ═══════════════════════════════════════════════════════════
Log "════════ 生成缩小工作副本 ════════" "Yellow"
Wait-ForMemory 8 | Out-Null

# SDR PNG (850px, 8bit)
Exec $ffexe "-y -threads 4 -hide_banner -loglevel error -i `"D:\TEST2\图片.png`" -vf `"scale='min(850,iw)':-2:flags=lanczos`" -update 1 `"$srcSet\png_sdr_s.png`"" | Out-Null
# 16-bit TIFF → 小分辨率 16-bit PNG (保留位深测试价值)
Exec $ffexe "-y -threads 4 -hide_banner -loglevel error -i `"D:\TEST2\DSC00106_16Bit.TIF`" -vf `"scale=850:-2:flags=lanczos`" -pix_fmt rgb48le -update 1 `"$srcSet\tiff16_s.png`"" | Out-Null
# HDR AVIF (BT.2020 PQ 10-bit 小副本)
Exec $ffexe "-y -threads 4 -hide_banner -loglevel error -i `"D:\TEST2\RGBS6695-HDR\RGBS6695-HDR.avif`" -vf `"scale=850:-2:flags=lanczos`" -c:v libaom-av1 -crf 25 -pix_fmt yuv444p10le -strict experimental -color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc `"$srcSet\hdr_avif_s.avif`"" | Out-Null
# GIF 小副本 (6 帧加速动图测试)
Exec $ffexe "-y -threads 4 -hide_banner -loglevel error -i `"D:\TEST2\PLAN-1.gif`" -vf `"fps=8,scale=480:-2:flags=lanczos`" -frames:v 6 -loop 0 `"$srcSet\gif_s.gif`"" | Out-Null
# WebP 小副本
$wp = Get-ChildItem "D:\TEST2\WebP_ScenarioImage" -Recurse -Filter *.webp | Select-Object -Skip 1 -First 1
Exec $ffexe "-y -threads 4 -hide_banner -loglevel error -i `"$($wp.FullName)`" -vf `"scale=850:-2:flags=lanczos`" -update 1 `"$srcSet\webp_s.webp`"" | Out-Null

$srcListText = (Get-ChildItem $srcSet -File | ForEach-Object { "$($_.Name)($([math]::Round($_.Length/1KB,0))KB)" }) -join " "
Log "工作副本: $srcListText" "Yellow"

# 源文件完整性校验 (任一缺失则中止)
$requiredSrcs = @("png_sdr_s.png","tiff16_s.png","hdr_avif_s.avif","gif_s.gif","webp_s.webp")
$missing = $requiredSrcs | Where-Object { -not (Test-Path "$srcSet\$_") }
if ($missing) { Log "❌ 工作副本生成失败: $($missing -join ', ') — 中止测试" "Red"; exit 2 }

# Bootstrap: 用 app 生成小 HDR JXL 源 (单次受控调用)
if (-not (Test-Path "$srcSet\hdr_jxl_s.jxl")) {
    $bootDir = "$srcSet\_boot"
    New-Item -ItemType Directory -Force -Path $bootDir | Out-Null
    Log "Bootstrap: 生成小 HDR JXL 源..." "Yellow"
    Invoke-App "--headless -i `"$srcSet\hdr_avif_s.avif`" -o `"$bootDir`" -f jxl -e Cjxl -j 1 --threads 4 --color-space `"BT.2020 PQ`"" "$bootDir\so.log" "$bootDir\se.log" 120 | Out-Null
    $bootJxl = Get-ChildItem $bootDir -Filter *.jxl -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($bootJxl) { Move-Item $bootJxl.FullName "$srcSet\hdr_jxl_s.jxl" -Force }
    Get-Content "$bootDir\so.log" -Raw -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_ -match "完成 1 项, 失败 0 项") { Log "Bootstrap HDR JXL ✅" "Green" } else { Log "Bootstrap HDR JXL 输出: $($_.Substring(0,[math]::Min(200,$_.Length)))" "DarkYellow" }
    }
    Remove-Item $bootDir -Recurse -Force -ErrorAction SilentlyContinue
}

# ═══════════════════════════════════════════════════════════
# 测试素材 (缩小版 + 特殊格式原样)
# ═══════════════════════════════════════════════════════════
$T = @{
    png_sdr   = "$srcSet\png_sdr_s.png"
    tiff16    = "$srcSet\tiff16_s.png"
    hdr_avif  = "$srcSet\hdr_avif_s.avif"
    hdr_jxl   = "$srcSet\hdr_jxl_s.jxl"
    hdr_dng   = "D:\TEST2\RGBS6695-HDR\RGBS6695-HDR.dng"
    gif       = "$srcSet\gif_s.gif"
    webp      = "$srcSet\webp_s.webp"
    jxr       = "D:\TEST2\NTE (Neverness To Everness) Screenshot 2026.07.08 - 13.03.04.68.jxr"
}

$SDRcs = @("sRGB", "Display P3")
$HDRcs = @("sRGB", "BT.2020 PQ", "BT.2020 HLG", "P3 PQ")
$ALLcs = @("sRGB", "Display P3", "BT.2020 PQ", "BT.2020 HLG", "P3 PQ")

# ═══════════════════════════════════════════════════════════
# Layer 2: 核心矩阵 (SDR 源 × SDR 色彩 × 位深; HDR 源 × HDR 色彩 × 位深)
# ═══════════════════════════════════════════════════════════
Log ""; Log "════════ Layer 2: 核心矩阵 ════════" "Yellow"
$id = 200

# PNG: 2cs×2bd
foreach ($cs in $SDRcs) { foreach ($bd in 8,16) {
    $id++; Run-Test "L2-$id" $T.png_sdr png Ffmpeg $cs "$bd" @{} "L2_core" 60 } }
# JPEG Ffmpeg: 2cs×8bit
foreach ($cs in $SDRcs) {
    $id++; Run-Test "L2-$id" $T.png_sdr jpg Ffmpeg $cs "8" @{} "L2_core" 60 }
# JPEG cjpegli: 2cs
foreach ($cs in $SDRcs) {
    $id++; Run-Test "L2-$id" $T.png_sdr jpg Cjpegli $cs "8" @{} "L2_core" 60 }
# WebP: 8bit×5cs
foreach ($cs in $ALLcs) {
    $id++; Run-Test "L2-$id" $T.png_sdr webp Ffmpeg $cs "8" @{} "L2_core" 60 }
# AVIF: 5cs×(8,10,12)bit — HDR cs 源用 hdr_avif_s
foreach ($cs in $SDRcs) { foreach ($bd in 8,10,12) {
    $id++; Run-Test "L2-$id" $T.png_sdr avif Ffmpeg $cs "$bd" @{} "L2_core" 90 } }
foreach ($cs in @("BT.2020 PQ","BT.2020 HLG","P3 PQ")) { foreach ($bd in 8,10,12) {
    $id++; Run-Test "L2-$id" $T.hdr_avif avif Ffmpeg $cs "$bd" @{} "L2_core" 90 } }
# TIFF: 5cs×(8,16)bit
foreach ($cs in $SDRcs) { foreach ($bd in 8,16) {
    $id++; Run-Test "L2-$id" $T.png_sdr tiff Ffmpeg $cs "$bd" @{} "L2_core" 60 } }
foreach ($cs in @("BT.2020 PQ","P3 PQ")) { foreach ($bd in 8,16) {
    $id++; Run-Test "L2-$id" $T.hdr_avif tiff Ffmpeg $cs "$bd" @{} "L2_core" 60 } }
# JXL cjxl: 5cs×(8,16)bit
foreach ($cs in $SDRcs) { foreach ($bd in 8,16) {
    $id++; Run-Test "L2-$id" $T.png_sdr jxl Cjxl $cs "$bd" @{} "L2_core" 60 } }
foreach ($cs in @("BT.2020 PQ","BT.2020 HLG","P3 PQ")) { foreach ($bd in 8,16) {
    $id++; Run-Test "L2-$id" $T.hdr_avif jxl Cjxl $cs "$bd" @{} "L2_core" 60 } }
# JXL ffmpeg: 2cs×(8,16)
foreach ($cs in @("sRGB","BT.2020 PQ")) {
    $jxlSrc = if ($cs -match "2020") { $T.hdr_avif } else { $T.png_sdr }
    foreach ($bd in 8,16) {
        $id++; Run-Test "L2-$id" $jxlSrc jxl Ffmpeg $cs "$bd" @{} "L2_core" 60
    }
}
# JXR: 8bit sRGB
$id++; Run-Test "L2-$id" $T.png_sdr jxr Jxr "sRGB" "8" @{} "L2_core" 90
# HDR→SDR 降级专项 (hdr→sRGB/P3 16bit png)
foreach ($cs in $SDRcs) {
    $id++; Run-Test "L2-$id" $T.hdr_avif png Ffmpeg $cs "16" @{} "L2_core" 60 }

# ═══════════════════════════════════════════════════════════
# Layer 3: HDR 专项 (HDR 源 × 4 目标色彩 × 按格式位深)
# ═══════════════════════════════════════════════════════════
Log ""; Log "════════ Layer 3: HDR 专项 ════════" "Yellow"
$id = 300
foreach ($srcKey in @("hdr_avif","hdr_jxl","hdr_dng")) {
    $src = $T[$srcKey]
    $to = if ($srcKey -eq "hdr_dng") { 240 } else { 90 }
    foreach ($cs in @("BT.2020 PQ","BT.2020 HLG")) {
        $id++; Run-Test "L3-$id" $src jxl Cjxl $cs "16" @{} "L3_hdr" $to
    }
    foreach ($cs in @("sRGB","P3 PQ")) {
        $id++; Run-Test "L3-$id" $src png Ffmpeg $cs "16" @{} "L3_hdr" $to
    }
    $id++; Run-Test "L3-$id" $src tiff Ffmpeg "sRGB" "16" @{} "L3_hdr" $to
    $id++; Run-Test "L3-$id" $src avif Ffmpeg "BT.2020 PQ" "10" @{} "L3_hdr" (20+$to)
    $id++; Run-Test "L3-$id" $src webp Ffmpeg "sRGB" "8" @{} "L3_hdr" $to
    $id++; Run-Test "L3-$id" $src jpg Ffmpeg "sRGB" "8" @{} "L3_hdr" $to
}

# ═══════════════════════════════════════════════════════════
# Layer 4: 特殊输入 (RAW DNG / JXR / 动图 GIF / 16bit / WebP)
# ═══════════════════════════════════════════════════════════
Log ""; Log "════════ Layer 4: 特殊输入 ════════" "Yellow"
$id = 400
# 4a: DNG (原分辨率, 长超时)
foreach ($f in @(@("jxl","Cjxl"), @("jpg","Cjpegli"), @("jpg","Ffmpeg"), @("png","Ffmpeg"), @("webp","Ffmpeg"), @("avif","Ffmpeg"), @("tiff","Ffmpeg"))) {
    $id++; Run-Test "L4-$id" $T.hdr_dng $f[0] $f[1] "sRGB" "" @{} "L4_dng" 240 }
# 4b: JXR (原样)
foreach ($f in @(@("png","Ffmpeg"), @("jpg","Ffmpeg"), @("webp","Ffmpeg"), @("avif","Ffmpeg"), @("tiff","Ffmpeg"), @("jxl","Cjxl"), @("gif","Ffmpeg"))) {
    $id++; Run-Test "L4-$id" $T.jxr $f[0] $f[1] "sRGB" "" @{} "L4_jxr" 120 }
# 4c: GIF 动图 (小副本)
foreach ($f in @(@("png","Ffmpeg"), @("webp","Ffmpeg"), @("avif","Ffmpeg"), @("jxl","Cjxl"), @("gif","Ffmpeg"), @("tiff","Ffmpeg"))) {
    $id++; Run-Test "L4-$id" $T.gif $f[0] $f[1] "sRGB" "" @{} "L4_gif" 90 }
# 4d: 16bit 小副本 → 各格式 (检验位深保留)
foreach ($f in @(@("png","Ffmpeg"), @("webp","Ffmpeg"), @("avif","Ffmpeg"), @("jxl","Cjxl"), @("tiff","Ffmpeg"), @("jpg","Ffmpeg"))) {
    $id++; Run-Test "L4-$id" $T.tiff16 $f[0] $f[1] "sRGB" "" @{} "L4_tiff16" 60 }
# 4e: WebP 样张
foreach ($f in @(@("png","Ffmpeg"), @("avif","Ffmpeg"), @("jxl","Cjxl"))) {
    $id++; Run-Test "L4-$id" $T.webp $f[0] $f[1] "sRGB" "" @{} "L4_webp" 60 }

# ═══════════════════════════════════════════════════════════
# 汇总
# ═══════════════════════════════════════════════════════════
$script:totalSw.Stop()
$total = $script:passCount + $script:failCount
Log ""
Log "═══════════════════════════════════════════════════════" "Yellow"
Log "矩阵测试完成: PASS=$($script:passCount) FAIL=$($script:failCount) (TIMEOUT=$($script:timeoutCount)) TOTAL=$total 用时=$([math]::Round($script:totalSw.Elapsed.TotalMinutes,1))min" "Yellow"
Log "═══════════════════════════════════════════════════════" "Yellow"

$csv = "$out\matrix_results_safe.csv"
$script:results | Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8
Log "CSV: $csv"

if ($script:failCount -gt 0) {
    Log "── 失败明细 ──" "Red"
    $script:results | Where-Object Status -ne "PASS" | ForEach-Object {
        Log "  [$($_.TestId)] $($_.Format)/$($_.Encoder) cs=$($_.ColorSpace) bd=$($_.BitDepth): $($_.Log)" "DarkRed"
    }
    exit 1
} else { exit 0 }