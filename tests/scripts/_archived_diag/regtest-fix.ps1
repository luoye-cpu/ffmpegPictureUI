# 修复回归测试 v3: 使用参数数组正确调用
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$exe = "$repoRoot\publish\build\FFmpegPictureUI-dev-x64-full\FfmpegGui.exe"
$ffprobe = (Get-ChildItem "$repoRoot\publish\PLAN\ffmpeg-full*\ffprobe.exe" | Select-Object -First 1).FullName
$base = "$repoRoot\tests\output\results\matrix\safe_sources"
$logFile = "$repoRoot\tests\output\results\regtest_fix_v3.log"
Remove-Item $logFile -ErrorAction SilentlyContinue

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$env:TEMP/_exec.out"; $e = "$env:TEMP/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}

function Log($msg, $color = "White") {
    Write-Host $msg -ForegroundColor $color
    Add-Content -Path $logFile -Value $msg -Encoding UTF8
}

Log "修复回归测试 v3 $(Get-Date -Format 'HH:mm:ss')" "Yellow"

# 每个用例: id, 格式, 编码器, 源文件, 色彩空间(可空), 位深(可空)
$cases = @(
    @("jpeg_p3","jpg","Ffmpeg","png_sdr_s.png","Display P3","8"),
    @("webp_p3","webp","Ffmpeg","png_sdr_s.png","Display P3","8"),
    @("webp_bt2020pq","webp","Ffmpeg","png_sdr_s.png","BT.2020 PQ","8"),
    @("webp_bt2020hlg","webp","Ffmpeg","png_sdr_s.png","BT.2020 HLG","8"),
    @("webp_p3pq","webp","Ffmpeg","png_sdr_s.png","P3 PQ","8"),
    @("avif_p3_8","avif","Ffmpeg","png_sdr_s.png","Display P3","8"),
    @("avif_p3_10","avif","Ffmpeg","png_sdr_s.png","Display P3","10"),
    @("avif_p3_12","avif","Ffmpeg","png_sdr_s.png","Display P3","12"),
    @("tiff_bt2020pq_8","tiff","Ffmpeg","png_sdr_s.png","BT.2020 PQ","8"),
    @("tiff_bt2020pq_16","tiff","Ffmpeg","png_sdr_s.png","BT.2020 PQ","16"),
    @("tiff_p3pq_8","tiff","Ffmpeg","png_sdr_s.png","P3 PQ","8"),
    @("tiff_p3pq_16","tiff","Ffmpeg","png_sdr_s.png","P3 PQ","16"),
    @("webp_srgb_hdr","webp","Ffmpeg","hdr_avif_s.avif","sRGB","8"),
    @("gif_png","png","Ffmpeg","gif_s.gif","",""),
    @("gif_tiff","tiff","Ffmpeg","gif_s.gif","",""),
    @("16bit_jpg","jpg","Ffmpeg","tiff16_s.png","",""),
    @("16bit_webp","webp","Ffmpeg","tiff16_s.png","",""),
    @("16bit_avif","avif","Ffmpeg","tiff16_s.png","","16")
)

$pass = 0; $fail = 0; $failList = @()
$sw0 = [System.Diagnostics.Stopwatch]::StartNew()

foreach ($c in $cases) {
    $id = $c[0]; $fmt = $c[1]; $enc = $c[2]; $srcName = $c[3]; $cs = $c[4]; $bd = $c[5]
    $src = "$base\$srcName"
    $outDir = "$repoRoot\tests\output\results\regen_v3\$id"
    if (Test-Path $outDir) { Remove-Item "$outDir\*" -Force -ErrorAction SilentlyContinue } else { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

    # 构建参数数组（关键：用数组而非字符串）
    $argStr = "--headless -i `"$src`" -o `"$outDir`" -f $fmt -e $enc -j 1 --threads 4"
    if ($cs -ne "") { $argStr += " --color-space `"$cs`"" }
    if ($bd -ne "") { $argStr += " --bit-depth $bd" }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $r = Exec $exe $argStr | Out-String
    $sw.Stop()

    $ok = $r -match "完成 1 项, 失败 0 项"
    $probe = ""
    if ($ok) {
        $outFile = Get-ChildItem $outDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -notin @("stdout.log","stderr.log","args.txt") } | Select-Object -First 1
        if ($outFile) {
            try { $probe = (Exec $ffprobe "-v error -select_streams v:0 -show_entries stream=pix_fmt,color_primaries,color_transfer -of csv=p=0 `"$($outFile.FullName)`"" | Out-String).Trim() } catch { $probe = "probe-error" }
        }
        $pass++
        Log "  [PASS] $id ($([math]::Round($sw.Elapsed.TotalSeconds,1))s) $probe" "Green"
    } else {
        $fail++
        $failList += $id
        $err = ($r -split "`n" | Where-Object { $_ -match "失败|失败" } | Select-Object -First 1).Trim()
        Log "  [FAIL] $id ($([math]::Round($sw.Elapsed.TotalSeconds,1))s): $err" "Red"
    }
}

$sw0.Stop()
Log ""
Log "════════ 回归完成: PASS=$pass FAIL=$fail TOTAL=$($pass+$fail) 用时=$([math]::Round($sw0.Elapsed.TotalMinutes,1))min ════════" "Yellow"
if ($fail -gt 0) { Log "失败项: $($failList -join ', ')" "Red"; exit 1 } else { exit 0 }