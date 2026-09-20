# GPU FP32 收益实测（三）：内核净吞吐 + FP16 vs FP32 精度差
# 关键语法（本会话实测得出）：
#   -init_hw_device opencl=<name>:<platform>.<device>        如 opencl=ocl:0.0
#   program_opencl=source=c\:/path/k.cl                      盘符冒号必须转义为 \:
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$d  = "$root/tests/output/validate/gpu2"
$kesc = "c\\:/PLAN/ffmpegPictureUI/tests/output/validate/gpu2/k.cl"
$W = 3840; $H = 2160; $MP = 8.3
$base = "-hide_banner -loglevel error -init_hw_device opencl=ocl:0.0 -filter_hw_device ocl"

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$d/_exec.out"; $e = "$d/_exec.err"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e
  $global:LASTEXITCODE = $p.ExitCode
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}

function FF([string]$a2) {
  $p = Start-Process -FilePath $ff -ArgumentList $a2 -NoNewWindow -Wait -PassThru -RedirectStandardError "$d/e3.txt"
  return $p.ExitCode
}
$script:r = @{}
function Tim([string]$tag, [string]$a2, [int]$n = 3) {
  $best = [double]::MaxValue; $rc = 0
  for ($k = 0; $k -lt $n; $k++) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew(); $rc = FF $a2; $sw.Stop()
    if ($sw.Elapsed.TotalMilliseconds -lt $best) { $best = $sw.Elapsed.TotalMilliseconds }
  }
  $script:r[$tag] = @{ ms = $best; rc = $rc }
  Write-Output ("  {0,-34} {1,9:F1} ms  exit={2}" -f $tag, $best, $rc)
}

Write-Host "=== GPU 内核净吞吐（4K 逐平面）===" -ForegroundColor Cyan
Tim "h0 upload+download(无内核)"   "$base -y -i `"$d/src.png`" -vf `"format=gbrpf32le,hwupload,hwdownload,format=gbrpf32le`" -frames:v 1 -f null -"
Tim "h1 +identity 内核"           "$base -y -i `"$d/src.png`" -vf `"format=gbrpf32le,hwupload,program_opencl=source=$kesc`:kernel=ident,hwdownload,format=gbrpf32le`" -frames:v 1 -f null -"
Tim "h2 +FP32 曲线内核(pow×2)"     "$base -y -i `"$d/src.png`" -vf `"format=gbrpf32le,hwupload,program_opencl=source=$kesc`:kernel=curve32,hwdownload,format=gbrpf32le`" -frames:v 1 -f null -"
Tim "h3 +FP16 曲线内核"            "$base -y -i `"$d/src.png`" -vf `"format=gbrpf32le,hwupload,program_opencl=source=$kesc`:kernel=curve16,hwdownload,format=gbrpf32le`" -frames:v 1 -f null -"
if ($script:r["h1 +identity 内核"].rc -ne 0) { Get-Content "$d/e3.txt" | Select-Object -Last 5 | ForEach-Object { "    $($_.ToString().Trim())" } }
else {
  $h0 = $script:r["h0 upload+download(无内核)"].ms
  $id = $script:r["h1 +identity 内核"].ms; $c32 = $script:r["h2 +FP32 曲线内核(pow×2)"].ms
  Tim "h4 解码 only(CPU 基线)" "-y -hide_banner -loglevel error -i `"$d/src.png`" -vf format=rgb48le -f null -"
  $dec = $script:r["h4 解码 only(CPU 基线)"].ms
  $rt  = [Math]::Max(0, $h0 - $dec)
  Write-Output ("  → GPU 往返税 = {0:F1} ms；identity 内核净 = {1:F1} ms；FP32 曲线内核净 = {2:F1} ms" -f $rt, [Math]::Max(0,$id-$h0), [Math]::Max(0,$c32-$h0))
  Write-Output ("  → 对比：同一变换 CPU 表驱动内核 ≈ 7.5 ms（20核）/ 128 ms（按 68 MPix/s 估）；GPU 含往返 = {0:F1} ms" -f ($rt + [Math]::Max(0,$c32-$h0)))
}

Write-Host "`n=== FP16 vs FP32 精度差（4K 全图，ffmpeg psnr）===" -ForegroundColor Cyan
foreach ($k in @("curve32","curve16")) {
  $null = FF "$base -y -i `"$d/src.png`" -vf `"format=gbrpf32le,hwupload,program_opencl=source=$kesc`:kernel=$k,hwdownload,format=gbrpf32le`" -frames:v 1 `"$(Join-Path $d "o_$k.png")`""
}
if ((Test-Path "$d/o_curve32.png") -and (Test-Path "$d/o_curve16.png")) {
  $o = Exec $ff "-hide_banner -i `"$d/o_curve32.png`" -i `"$d/o_curve16.png`" -lavfi psnr -f null -"
  ($o -split "`r?`n" | Select-String "PSNR|average" | Select-Object -Last 1 | ForEach-Object { "  $($_.ToString().Trim())" })
} else { Write-Output "  跳过（输出未生成）"; Get-Content "$d/e3.txt" | Select-Object -Last 4 | ForEach-Object { "    $($_.ToString().Trim())" } }
