# ═══════════════════════════════════════════════════════════
#  run-full-matrix-test.ps1 — FFmpegPictureUI 完整实机测试
#  覆盖: 格式组合矩阵 + HDR BT.2020/SDR × 色彩深度组合
#  被测主体: publish/build/FFmpegPictureUI-dev-x64-full (最新构建)
#  测试素材: D:\TEST2 (实机文件)
#  输出: tests/output/results/matrix/ (git 忽略)
# ═══════════════════════════════════════════════════════════
$ErrorActionPreference = "Continue"
$root = "C:\PLAN\ffmpegPictureUI"
Set-Location $root

# ── 被测主体 ──
$exe = "$root\publish\build\FFmpegPictureUI-dev-x64-full\FfmpegGui.exe"
$ffprobe = (Get-ChildItem "$root\publish\PLAN\ffmpeg-full*\ffprobe.exe" | Select-Object -First 1).FullName
$exif = (Get-ChildItem "$root\publish\PLAN\exiftool\exiftool.exe" | Select-Object -First 1).FullName

# ── 输出目录 ──
$out = "$root\tests\output\results\matrix"
$logFile = "$root\tests\output\results\matrix_test.log"
New-Item -ItemType Directory -Force -Path $out | Out-Null

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$out\_exec.out"; $e = "$out\_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}

# ── 测试结果记录 ──
$script:results = [System.Collections.Generic.List[pscustomobject]]::new()
$script:passCount = 0
$script:failCount = 0
$script:totalSw = [System.Diagnostics.Stopwatch]::StartNew()

function Log([string]$msg, [string]$color = "White") {
    $ts = Get-Date -Format "HH:mm:ss"
    $line = "[$ts] $msg"
    Write-Host $line -ForegroundColor $color
    Add-Content -Path $logFile -Value $line -Encoding UTF8
}

# ── 执行单次转换并记录结果 ──
function Run-Test {
    param(
        [string]$TestId,
        [string]$InputFile,
        [string]$Format,
        [string]$Encoder,
        [hashtable]$ExtraArgs = @{},
        [string]$ColorSpace = "",
        [string]$BitDepth = "",
        [string]$Category = "format"
    )
    # 为每个测试创建独立输出目录 (-o 接受目录)
    $testOutDir = "$out\$Category\$TestId"
    New-Item -ItemType Directory -Force -Path $testOutDir | Out-Null

    $cliArgs = @("--headless", "-i", $InputFile, "-o", $testOutDir, "-f", $Format, "-e", $Encoder, "-j", "1")
    if ($ColorSpace -ne "") { $cliArgs += @("--color-space", $ColorSpace) }
    if ($BitDepth -ne "") { $cliArgs += @("--bit-depth", $BitDepth) }
    foreach ($kv in $ExtraArgs.GetEnumerator()) {
        $cliArgs += @("--$($kv.Key)", "$($kv.Value)")
    }
    $argStr = ($cliArgs | ForEach-Object { if ($_ -match ' ') { "`"$_`"" } else { $_ } }) -join " "

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $testDesc = "${TestId}: $(Split-Path $InputFile -Leaf) -> $Format/$Encoder"
    if ($ColorSpace) { $testDesc += " [$ColorSpace]" }
    if ($BitDepth) { $testDesc += " ${BitDepth}bit" }

    Log "  -- $testDesc" "Cyan"

    try {
        $captureOut = (Exec $exe $argStr | Out-String)
        $sw.Stop()
        $elapsed = $sw.Elapsed.TotalSeconds

        $success = $captureOut -match "已完成" -and $captureOut -notmatch "失败 \(退出码" -and $captureOut -notmatch "失败:"
        # 查找输出目录下生成的文件（保留原文件名，扩展名可能变）
        $expectedExt = ".$Format"
        $inputBase = [System.IO.Path]::GetFileNameWithoutExtension($InputFile)
        $outFiles = Get-ChildItem $testOutDir -File -ErrorAction SilentlyContinue
        $outFile = $outFiles | Where-Object { $_.Name -eq "$inputBase$expectedExt" } | Select-Object -First 1
        if (-not $outFile) {
            # 回退：找该目录下任意文件
            $outFile = $outFiles | Select-Object -First 1
        }
        $fileExists = $outFile -ne $null
        $actualOutPath = if ($fileExists) { $outFile.FullName } else { "" }

        $lineMatch = ($captureOut -split "`n" | Where-Object { $_ -match "已完成|失败|队列处理完毕" } | Select-Object -Last 2)
        $exitLine = ($lineMatch -join " | ")

        if ($success -and $fileExists) {
            $script:passCount++
            $fileKB = [math]::Round($outFile.Length / 1KB, 1)
            $probe = ""
            try {
                $probeOut = Exec $ffprobe "-v error -select_streams v:0 -show_entries `"stream=pix_fmt,color_space,color_primaries,color_transfer`" -of csv=p=0 `"$actualOutPath`""
                $probe = ($probeOut -split "`r?`n" -join "").Trim()
            } catch { $probe = "probe-error" }
            Log "    [PASS] (${elapsed}s, ${fileKB}KB) probe=$probe" "Green"
            $script:results.Add([pscustomobject]@{
                TestId=$TestId; Category=$Category; Status="PASS"
                Input=[System.IO.Path]::GetFileName($InputFile); Format=$Format; Encoder=$Encoder
                ColorSpace=$ColorSpace; BitDepth=$BitDepth
                Elapsed=[math]::Round($elapsed,1); SizeKB=$fileKB; Probe=$probe; Log=$exitLine
            })
        } else {
            $script:failCount++
            $errShort = ($captureOut -split "`n" | Where-Object { $_ -match "失败|错误|error|Error" } | Select-Object -First 1).Trim()
            if (-not $errShort) { $errShort = $exitLine }
            Log "    [FAIL] (${elapsed}s): $errShort" "Red"
            $script:results.Add([pscustomobject]@{
                TestId=$TestId; Category=$Category; Status="FAIL"
                Input=[System.IO.Path]::GetFileName($InputFile); Format=$Format; Encoder=$Encoder
                ColorSpace=$ColorSpace; BitDepth=$BitDepth
                Elapsed=[math]::Round($elapsed,1); SizeKB=0; Probe=""; Log=$errShort
            })
        }
    } catch {
        $sw.Stop()
        $script:failCount++
        $elapsed = $sw.Elapsed.TotalSeconds
        Log "    [EXCEPTION] $($_.Exception.Message) (${elapsed}s)" "Red"
        $script:results.Add([pscustomobject]@{
            TestId=$TestId; Category=$Category; Status="FAIL"
            Input=[System.IO.Path]::GetFileName($InputFile); Format=$Format; Encoder=$Encoder
            ColorSpace=$ColorSpace; BitDepth=$BitDepth
            Elapsed=[math]::Round($elapsed,1); SizeKB=0; Probe=""; Log=$_.Exception.Message
        })
    }
}

# ═══════════════════════════════════════════════════════════
# 测试素材清单 (D:\TEST2)
# ═══════════════════════════════════════════════════════════
$testFiles = @{
    png_sdr  = "D:\TEST2\图片.png"
    tiff16   = "D:\TEST2\DSC00106_16Bit.TIF"
    gif_anim = "D:\TEST2\PLAN-1.gif"
    jxr      = "D:\TEST2\NTE (Neverness To Everness) Screenshot 2026.07.08 - 13.03.04.68.jxr"
    hdr_avif = "D:\TEST2\RGBS6695-HDR\RGBS6695-HDR.avif"
    hdr_jxl  = "D:\TEST2\RGBS6695-HDR\RGBS6695-HDR.jxl"
    hdr_dng  = "D:\TEST2\RGBS6695-HDR\RGBS6695-HDR.dng"
    hdr_compat_avif = "D:\TEST2\RGBS6695-HDR\RGBS6695-HDR-最大兼容.avif"
    hdr_compat_jxl  = "D:\TEST2\RGBS6695-HDR\RGBS6695-HDR-最大兼容.jxl"
    hdr_compat_jpg  = "D:\TEST2\RGBS6695-HDR\RGBS6695-HDR-最大兼容Only.jpg"
}

$webpSrc = "D:\TEST2\WebP_ScenarioImage\04_ScenarioImage"
$webpFiles = @(Get-ChildItem $webpSrc -Filter *.webp -ErrorAction SilentlyContinue)
if ($webpFiles.Count -gt 0) { $testFiles.webp1 = $webpFiles[0].FullName }
if ($webpFiles.Count -gt 1) { $testFiles.webp2 = $webpFiles[1].FullName }

# ═══════════════════════════════════════════════════════════
# 所有输出格式定义 (格式 → 默认编码器后端)
# ═══════════════════════════════════════════════════════════
$outputFormats = @(
    @{fmt="jpg";  enc="Ffmpeg";   desc="JPEG"},
    @{fmt="png";  enc="Ffmpeg";   desc="PNG"},
    @{fmt="webp"; enc="Ffmpeg";   desc="WebP"},
    @{fmt="avif"; enc="Ffmpeg";   desc="AVIF"},
    @{fmt="tiff"; enc="Ffmpeg";   desc="TIFF"},
    @{fmt="jxl";  enc="Cjxl";     desc="JPEG XL (cjxl)"},
    @{fmt="jxl";  enc="Ffmpeg";   desc="JPEG XL (ffmpeg)"},
    @{fmt="jpg";  enc="Cjpegli";  desc="JPEG (cjpegli)"},
    @{fmt="jxr";  enc="Jxr";      desc="JPEG XR"},
    @{fmt="gif";  enc="Ffmpeg";   desc="GIF"},
    @{fmt="dng";  enc="Dng";      desc="DNG"}
)

# 色彩空间定义
$colorSpaces = @(
    @{name="sRGB";        hdr=$false},
    @{name="Display P3";  hdr=$false},
    @{name="BT.2020 PQ";  hdr=$true},
    @{name="BT.2020 HLG"; hdr=$true},
    @{name="P3 PQ";       hdr=$true}
)

# 位深定义
$bitDepths = @(8, 10, 12, 16)

# ═══════════════════════════════════════════════════════════
# Layer 1: 冒烟格式矩阵 — 每代表源 × 每格式 (验证管线连通)
# ═══════════════════════════════════════════════════════════
Log "═══════════════════════════════════════════════════════" "Yellow"
Log "Layer 1: 冒烟格式矩阵 (每代表源 × 每格式)" "Yellow"
Log "═══════════════════════════════════════════════════════" "Yellow"

$smokeSources = @("png_sdr", "tiff16", "hdr_avif", "hdr_jxl", "webp1", "gif_anim")
$testId = 100

foreach ($srcKey in $smokeSources) {
    if (-not $testFiles.ContainsKey($srcKey)) {
        Log "  [SKIP] $srcKey (文件不存在)" "DarkGray"
        continue
    }
    $inFile = $testFiles[$srcKey]

    foreach ($fmt in $outputFormats) {
        $testId++
        if ($srcKey -eq "gif_anim" -and $fmt.fmt -in @("dng","jxr")) {
            Log "  [SKIP] GIF -> $($fmt.desc) (不适用)" "DarkGray"
            continue
        }
        if ($fmt.fmt -eq "dng" -and $srcKey -notin @("hdr_dng")) {
            continue
        }
        Run-Test -TestId "L1-$testId" -InputFile $inFile `
            -Format $fmt.fmt -Encoder $fmt.enc `
            -Category "L1_smoke"
    }
}

# ═══════════════════════════════════════════════════════════
# Layer 2: 核心矩阵 — 格式 × 位深 × 色彩空间
# ═══════════════════════════════════════════════════════════
Log ""
Log "═══════════════════════════════════════════════════════" "Yellow"
Log "Layer 2: 核心矩阵 (格式 x 位深 x 色彩空间)" "Yellow"
Log "═══════════════════════════════════════════════════════" "Yellow"

$coreSdr = $testFiles.png_sdr
$coreHdr = $testFiles.hdr_avif

$testId = 200
foreach ($fmt in $outputFormats) {
    if ($fmt.fmt -in @("gif","dng")) { continue }
    foreach ($cs in $colorSpaces) {
        foreach ($bd in $bitDepths) {
            $testId++
            $srcFile = if ($cs.hdr) { $coreHdr } else { $coreSdr }

            # JPEG 仅支持 8bit
            if ($fmt.fmt -eq "jpg" -and $bd -ne 8) { continue }
            # JXR 仅 8bit
            if ($fmt.fmt -eq "jxr" -and $bd -ne 8) { continue }

            Run-Test -TestId "L2-$testId" -InputFile $srcFile `
                -Format $fmt.fmt -Encoder $fmt.enc `
                -ColorSpace $cs.name -BitDepth "$bd" `
                -Category "L2_core"
        }
    }
}

# ═══════════════════════════════════════════════════════════
# Layer 3: HDR 专项 — HDR 源 × HDR/SDR 目标色彩空间 × 位深
# ═══════════════════════════════════════════════════════════
Log ""
Log "═══════════════════════════════════════════════════════" "Yellow"
Log "Layer 3: HDR 专项 (HDR 源 x 色彩转换 x 位深)" "Yellow"
Log "═══════════════════════════════════════════════════════" "Yellow"

$testId = 300
$hdrSources = @("hdr_avif", "hdr_jxl", "hdr_dng")
foreach ($srcKey in $hdrSources) {
    if (-not $testFiles.ContainsKey($srcKey)) { continue }
    $inFile = $testFiles[$srcKey]
    foreach ($fmt in @( @{fmt="avif"; enc="Ffmpeg"}, @{fmt="jxl"; enc="Cjxl"}, @{fmt="jpg"; enc="Ffmpeg"}, @{fmt="png"; enc="Ffmpeg"}, @{fmt="webp"; enc="Ffmpeg"}, @{fmt="tiff"; enc="Ffmpeg"} )) {
        foreach ($cs in $colorSpaces) {
            foreach ($bd in @(10, 16)) {
                $testId++
                if ($fmt.fmt -eq "jpg" -and $bd -ne 8) { continue }
                Run-Test -TestId "L3-$testId" -InputFile $inFile `
                    -Format $fmt.fmt -Encoder $fmt.enc `
                    -ColorSpace $cs.name -BitDepth "$bd" `
                    -Category "L3_hdr"
            }
        }
    }
}

# ═══════════════════════════════════════════════════════════
# Layer 4: 特殊输入测试 — RAW/DNG/JXR/GIF/16-bit TIFF/WebP
# ═══════════════════════════════════════════════════════════
Log ""
Log "═══════════════════════════════════════════════════════" "Yellow"
Log "Layer 4: 特殊输入测试 (RAW/JXR/动图/16bit/WebP)" "Yellow"
Log "═══════════════════════════════════════════════════════" "Yellow"

$testId = 400

# 4a: DNG (HDR RAW) → 各未测试格式
if ($testFiles.ContainsKey("hdr_dng")) {
    foreach ($fmt in $outputFormats) {
        $testId++
        Run-Test -TestId "L4-$testId" -InputFile $testFiles.hdr_dng `
            -Format $fmt.fmt -Encoder $fmt.enc `
            -Category "L4_special_dng"
    }
}

# 4b: JXR → 各格式 (验证 JxrDecApp 解码链)
if ($testFiles.ContainsKey("jxr")) {
    foreach ($fmt in $outputFormats | Where-Object { $_.fmt -ne "jxr" }) {
        $testId++
        Run-Test -TestId "L4-$testId" -InputFile $testFiles.jxr `
            -Format $fmt.fmt -Encoder $fmt.enc `
            -Category "L4_special_jxr"
    }
}

# 4c: 16-bit TIFF → 各格式
if ($testFiles.ContainsKey("tiff16")) {
    foreach ($fmt in $outputFormats | Where-Object { $_.fmt -notin @("dng") }) {
        $testId++
        Run-Test -TestId "L4-$testId" -InputFile $testFiles.tiff16 `
            -Format $fmt.fmt -Encoder $fmt.enc `
            -Category "L4_special_tiff16"
    }
}

# 4d: GIF 动图 → 各格式
if ($testFiles.ContainsKey("gif_anim")) {
    foreach ($fmt in $outputFormats | Where-Object { $_.fmt -notin @("dng","jxr","tiff") }) {
        $testId++
        Run-Test -TestId "L4-$testId" -InputFile $testFiles.gif_anim `
            -Format $fmt.fmt -Encoder $fmt.enc `
            -Category "L4_special_gif"
    }
}

# 4e: WebP 场景 → 各格式 (SDR 大规模)
if ($testFiles.ContainsKey("webp1")) {
    foreach ($fmt in @( @{fmt="jpg"; enc="Ffmpeg"}, @{fmt="png"; enc="Ffmpeg"}, @{fmt="avif"; enc="Ffmpeg"}, @{fmt="jxl"; enc="Cjxl"}, @{fmt="webp"; enc="Ffmpeg"} )) {
        $testId++
        Run-Test -TestId "L4-$testId" -InputFile $testFiles.webp1 `
            -Format $fmt.fmt -Encoder $fmt.enc `
            -Category "L4_special_webp"
    }
}

# ═══════════════════════════════════════════════════════════
# 汇总报告
# ═══════════════════════════════════════════════════════════
$script:totalSw.Stop()
$total = $script:passCount + $script:failCount
$totalTime = $script:totalSw.Elapsed.TotalMinutes

Log ""
Log "═══════════════════════════════════════════════════════" "Yellow"
Log "测试完成: PASS=$script:passCount FAIL=$script:failCount TOTAL=$total 耗时=$([math]::Round($totalTime,1))min" "Yellow"
Log "═══════════════════════════════════════════════════════" "Yellow"

# 生成结果 CSV
$csvPath = "$out\matrix_results.csv"
$script:results | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
Log "CSV 结果: $csvPath"
Log "日志: $logFile"

# 输出失败列表
if ($script:failCount -gt 0) {
    Log "" "Red"
    Log "── 失败明细 ──" "Red"
    $script:results | Where-Object { $_.Status -eq "FAIL" } | ForEach-Object {
        Log "  [$($_.TestId)] $($_.Input) -> $($_.Format)/$($_.Encoder) cs=$($_.ColorSpace) bd=$($_.BitDepth)" "Red"
        Log "      $($_.Log)" "DarkRed"
    }
}

if ($script:failCount -gt 0) { exit 1 } else { exit 0 }