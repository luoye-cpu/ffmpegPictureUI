# 实测：swscale 的 16→10/12bit 降位是否有偏
# 隔离变量：RGB planar → RGB planar（无矩阵、无色度子采样），只考察"降位量化"本身。
# 参数用**全局** -sws_flags / -sws_dither（与产品 FfmpegCommandBuilder:66 的用法一致），
# 并用 scale=format= 强制经 swscale 转换。
# ⚠ 函数名不可用 Measure（与内置别名 measure=Measure-Object 冲突，会报"找不到位置参数"）。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
# ⚠ 运行级隔离（GUID，2026-09-17）：固定 `$d` + 开头整目录删，会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
$d  = "$root/tests/output/validate/swsdither_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force -Path $d | Out-Null

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$d/_exec.out"; $e = "$d/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}

$W = 4096; $H = 4
# ⚠ 斜坡步长 16 在 12-bit 下恰好落在可精确定位上→根本不会发生舍入（测不到东西）。
# 这里用乘法同余生成密集伪随机码，遍历大量余数，才能真正触发 16→10/12 的取整。
$raw = New-Object 'byte[]' ($W * $H * 6)
for ($i = 0; $i -lt $W; $i++) {
  $v = [int](($i * 9971 + 7) -band 65535)
  $lo = $v -band 0xFF; $hi = ($v -shr 8) -band 0xFF
  for ($y = 0; $y -lt $H; $y++) {
    $o = ($y * $W + $i) * 6
    $raw[$o]=$lo; $raw[$o+1]=$hi; $raw[$o+2]=$lo; $raw[$o+3]=$hi; $raw[$o+4]=$lo; $raw[$o+5]=$hi
  }
}
[System.IO.File]::WriteAllBytes("$d/ramp.raw", $raw)
Exec $ff "-y -hide_banner -loglevel error -f rawvideo -pix_fmt rgb48le -video_size `"${W}x$H`" -i `"$d/ramp.raw`" -compression_level 1 `"$d/ramp.png`"" | Out-Null
$plane = $W * $H

function DevStat([string]$tag, [string]$outFmt, [int]$bits, [string]$globalArgs) {
  $out = Join-Path $d ("o_" + ($tag -replace '[^0-9A-Za-z]','_') + ".bin")
  # 与生产一致：两个 format 滤镜之间由框架自动插入 scale（swscale）完成降位；
  # 因此用**全局** -sws_flags / -sws_dither 才能作用到那次转换（scale=format= 不是合法选项）。
  $a = "-y -hide_banner -loglevel error $globalArgs -i `"$d/ramp.png`" -vf `"format=rgb48le,format=$outFmt`" -frames:v 1 -f rawvideo `"$out`""
  $p = Start-Process -FilePath $ff -ArgumentList $a -NoNewWindow -Wait -PassThru -RedirectStandardError "$d/e.txt"
  if ($p.ExitCode -ne 0 -or -not (Test-Path $out)) {
    Write-Output ("  {0,-24} 失败 rc={1}" -f $tag, $p.ExitCode)
    (Get-Content "$d/e.txt" -EA SilentlyContinue | Select-Object -Last 1) | ForEach-Object { Write-Output ("        " + $_.ToString().Trim()) }
    return
  }
  $b = [System.IO.File]::ReadAllBytes($out)
  $mask = [int][Math]::Pow(2, $bits) - 1
  $scale = $mask / 65535.0
  $sumDev = 0.0; $maxDev = 0; $off1 = 0; $exact = 0; $gt = 0; $lt = 0
  for ($i = 0; $i -lt $plane; $i++) {          # 灰阶斜坡三平面同值，测 R 平面即可
    $src = $raw[$i*6] + ($raw[$i*6+1] * 256)
    $got = ($b[$i*2] + ($b[$i*2+1] * 256)) -band $mask
    $want = [int][Math]::Round($src * $scale)   # round-to-nearest 参考值
    $dev = $got - $want
    $sumDev += $dev
    $ad = [Math]::Abs($dev)
    if ($ad -gt $maxDev) { $maxDev = $ad }
    if ($dev -gt 0) { $gt++ } elseif ($dev -lt 0) { $lt++ } else { $exact++ }
    if ($ad -eq 1) { $off1++ }
  }
  Write-Output ("  {0,-24} 平均={1,8:F4} 最大|D|={2,2} 精确={3,6:F2}% 差1={4,6:F2}% 高/低={5}/{6}" -f `
    $tag, ($sumDev / $plane), $maxDev, (100.0 * $exact / $plane), (100.0 * $off1 / $plane), $gt, $lt)
}

Write-Host "=== 恒等 16→16（校验测量方法本身：应全为 0 偏差）===" -ForegroundColor Cyan
DevStat "16to16 identity" "gbrp16le" 16 ""
Write-Host "`n=== 10-bit：rgb48 → gbrp10le ===" -ForegroundColor Cyan
DevStat "10 default(现状)"     "gbrp10le" 10 ""
DevStat "10 accurate_rnd"      "gbrp10le" 10 "-sws_flags accurate_rnd"
DevStat "10 dither=none"       "gbrp10le" 10 "-sws_dither none"
DevStat "10 dither=errdiff"    "gbrp10le" 10 "-sws_dither error_diffusion"
DevStat "10 accnd+dither=none" "gbrp10le" 10 "-sws_flags accurate_rnd -sws_dither none"
Write-Host "`n=== 12-bit：rgb48 → gbrp12le ===" -ForegroundColor Cyan
DevStat "12 default(现状)"     "gbrp12le" 12 ""
DevStat "12 accurate_rnd"      "gbrp12le" 12 "-sws_flags accurate_rnd"
DevStat "12 dither=none"       "gbrp12le" 12 "-sws_dither none"
DevStat "12 dither=bayer"      "gbrp12le" 12 "-sws_dither bayer"
Write-Host "`n判读：平均偏差 != 0 → 存在系统性舍入偏置；最大|D| > 1 → 取整方式不是 round-to-nearest。" -ForegroundColor DarkGray

# ⚠ 结尾自清（运行级隔离配套）：本脚本**无 pass/fail 计数** ⇒ 必须**无条件**自清 ——
#   `if ($fail -eq 0)` 在无计数脚本里 `$null -eq 0` 为 False，**永不成立**（§6 第 66 条已记这个坑）。
Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue
