$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
# ⚠ 运行级隔离（GUID，2026-09-17）：固定 `$d` + 开头整目录删，会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
$d  = "$root/tests/output/validate/ztf2_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force -Path $d | Out-Null

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$d/_exec.out"; $e = "$d/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}

# 1) 造一张**带 CICP 标签**的 16bit PNG（lavfi 源无标签，zscale 的 tin= 是否生效存疑）
Exec $ff "-y -hide_banner -loglevel error -f lavfi -i `"color=gray:s=4x4`" -vf `"format=rgb48le`" -color_primaries bt709 -color_trc iec61966-2-1 -color_range pc `"$d/tagged_srgb.png`""
# 2) 不带 tin（让 zscale 读文件标签）：sRGB → bt709
Exec $ff "-y -hide_banner -loglevel error -i `"$d/tagged_srgb.png`" -vf `"format=gbrpf32le,zscale=p=bt709:t=bt709:m=bt709:r=pc,format=rgb48le`" -frames:v 1 -f rawvideo `"$d/a_read_tag.raw`""
# 3) 带 tin=iec61966-2-1（显式声明源曲线）：sRGB → bt709
Exec $ff "-y -hide_banner -loglevel error -i `"$d/tagged_srgb.png`" -vf `"format=gbrpf32le,zscale=pin=bt709:tin=iec61966-2-1:min=gbr:rin=pc:p=bt709:t=bt709:m=bt709:r=pc,format=rgb48le`" -frames:v 1 -f rawvideo `"$d/b_explicit_tin.raw`""
# 4) 同一输入 → sRGB（应恒等，作为量程/位深自检）
Exec $ff "-y -hide_banner -loglevel error -i `"$d/tagged_srgb.png`" -vf `"format=gbrpf32le,zscale=pin=bt709:tin=iec61966-2-1:min=gbr:rin=pc:p=bt709:t=iec61966-2-1:m=bt709:r=pc,format=rgb48le`" -frames:v 1 -f rawvideo `"$d/c_identity.raw`""
# 5) 同一输入 → bt470m (gamma 2.2)
Exec $ff "-y -hide_banner -loglevel error -i `"$d/tagged_srgb.png`" -vf `"format=gbrpf32le,zscale=pin=bt709:tin=iec61966-2-1:min=gbr:rin=pc:p=bt709:t=bt470m:m=bt709:r=pc,format=rgb48le`" -frames:v 1 -f rawvideo `"$d/d_gamma22.raw`""

function FirstU16($f) { if (-not (Test-Path $f)) { return -1 }; $b = [System.IO.File]::ReadAllBytes($f); $b[0] + ($b[1] * 256) }
$id = FirstU16 "$d/c_identity.raw"
Write-Output ("  c 恒等 (sRGB→sRGB)   = {0}  (期望 32896 → 不同则说明量程/位深处理有偏移)" -f $id)
Write-Output ("  a 读标签 (→bt709)    = {0}" -f (FirstU16 "$d/a_read_tag.raw"))
Write-Output ("  b 显式 tin (→bt709)   = {0}" -f (FirstU16 "$d/b_explicit_tin.raw"))
Write-Output ("  d 显式 tin (→bt470m)  = {0}" -f (FirstU16 "$d/d_gamma22.raw"))
$lin = [Math]::Pow((32896.0 / 65535.0 + 0.055) / 1.055, 2.4)
Write-Output ("  我们的预测: →bt709 = {0}   →gamma2.2 = {1}   (线性={2:F5})" -f `
  [Math]::Round((1.099 * [Math]::Pow($lin, 0.45) - 0.099) * 65535), [Math]::Round([Math]::Pow($lin, 1 / 2.2) * 65535), $lin)

# ⚠ 结尾自清（运行级隔离配套）：本脚本**无 pass/fail 计数** ⇒ 必须**无条件**自清 ——
#   `if ($fail -eq 0)` 在无计数脚本里 `$null -eq 0` 为 False，**永不成立**（§6 第 66 条已记这个坑）。
Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue
