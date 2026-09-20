$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
# ⚠ 运行级隔离（GUID，2026-09-17）：固定 `$d` + 开头整目录删，会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
$d  = "$root/tests/output/validate/ztf_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force -Path $d | Out-Null

function Z($tag, $tin, $t) {
  $raw = Join-Path $d "$tag.raw"
  # 必须把 -vf 的滤镜串作为**单个带引号参数**传入：Start-Process 会按空格拆坏它
  # （实测被拆成 tin="=gbr" 并报 Unable to parse "tin" option value "=gbr"）。
  # ⚠️ PowerShell 陷阱：`"...tin=$tin:min=..."` 会把 `$tin:min` 当**作用域限定变量**展开成空，
  # 导致滤镜变成 tin=:min=gbr 并报 “Unable to parse "tin" option value "=gbr"”。必须写 ${tin}。
  $args2 = "-y -hide_banner -loglevel error -f lavfi -i `"color=gray:s=4x4`" -vf `"format=rgb48le,zscale=pin=bt709:tin=${tin}:min=gbr:rin=pc:p=bt709:t=${t}:m=bt709:r=pc,format=rgb48le`" -frames:v 1 -f rawvideo `"$raw`""
  $p = Start-Process -FilePath $ff -ArgumentList $args2 -NoNewWindow -Wait -PassThru
  if (-not (Test-Path $raw)) { Write-Output ("  {0,-14} tin={1,-14} t={2,-14} 无输出 (exit {3})" -f $tag,$tin,$t,$p.ExitCode); return }
  $b = [System.IO.File]::ReadAllBytes($raw)
  $v = $b[0] + ($b[1] * 256)
  Write-Output ("  {0,-14} tin={1,-14} t={2,-14} out16={3,6} norm={4:F5}" -f $tag,$tin,$t,$v,($v/65535))
}

$in16 = 32896; $in = $in16 / 65535.0
$linSrgb = [Math]::Pow(($in + 0.055) / 1.055, 2.4)
$bt709OfLin = 1.099 * [Math]::Pow($linSrgb, 0.45) - 0.099
$srgbOfLin  = 1.055 * [Math]::Pow($in, 1/2.4) - 0.055
Write-Host "输入: lavfi gray → 16bit $in16 (归一 $in)" -ForegroundColor Yellow
Write-Host ("我们的曲线: sRGB解码→线性 = {0:F5} (16bit {1}); 该线性→BT.709编码 = {2:F5} (16bit {3}); 该输入按线性看待→sRGB编码 = {4:F5} (16bit {5})" -f `
  $linSrgb, [Math]::Round($linSrgb*65535), $bt709OfLin, [Math]::Round($bt709OfLin*65535), $srgbOfLin, [Math]::Round($srgbOfLin*65535)) -ForegroundColor Cyan
Z "srgb2srgb"  "iec61966-2-1" "iec61966-2-1"
Z "7092709"    "bt709"        "bt709"
Z "srgb2709"   "iec61966-2-1" "bt709"
Z "srgb2lin"   "iec61966-2-1" "linear"
Z "lin2srgb"   "linear"       "iec61966-2-1"
Z "lin2709"    "linear"       "bt709"

# ⚠ 结尾自清（运行级隔离配套）：本脚本**无 pass/fail 计数** ⇒ 必须**无条件**自清 ——
#   `if ($fail -eq 0)` 在无计数脚本里 `$null -eq 0` 为 False，**永不成立**（§6 第 66 条已记这个坑）。
Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue
