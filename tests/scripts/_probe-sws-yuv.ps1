# 实测②：YUV 目标路径（AVIF 用的那条）上 -sws_flags / -sws_dither 是否有影响
# 不需要参考公式：只比较各变体之间的**逐样本差异**。全同 ⇒ 参数无关（不必钉）；有差 ⇒ 必须钉并写明取哪一个。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$d  = "$root/tests/output/validate/swsyuv"
if (Test-Path $d) { Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $d | Out-Null
$W = 1024; $H = 64
$raw = New-Object 'byte[]' ($W * $H * 6)
for ($i = 0; $i -lt $W * $H; $i++) {
  $r = [int](($i * 4051 + 13) -band 65535); $g = [int](($i * 2731 + 977) -band 65535); $b = [int](($i * 6151 + 311) -band 65535)
  $o = $i * 6
  $raw[$o]=$r -band 0xFF; $raw[$o+1]=($r -shr 8) -band 0xFF
  $raw[$o+2]=$g -band 0xFF; $raw[$o+3]=($g -shr 8) -band 0xFF
  $raw[$o+4]=$b -band 0xFF; $raw[$o+5]=($b -shr 8) -band 0xFF
}
[System.IO.File]::WriteAllBytes("$d/in.raw", $raw)

function Conv([string]$tag, [string]$fmt, [string]$globals) {
  $out = Join-Path $d ($tag + ".bin")
  $a = "-y -hide_banner -loglevel error $globals -f rawvideo -pix_fmt rgb48le -video_size ${W}x${H} -i `"$d/in.raw`" -vf `"format=rgb48le,format=$fmt`" -frames:v 1 -f rawvideo `"$out`""
  $p = Start-Process -FilePath $ff -ArgumentList $a -NoNewWindow -Wait -PassThru -RedirectStandardError "$d/e.txt"
  if ($p.ExitCode -ne 0) { Write-Output "  $tag 失败 rc=$($p.ExitCode)"; return $null }
  return [System.IO.File]::ReadAllBytes($out)
}
function DiffVsBase([string]$name, $base, $v) {
  if ($null -eq $v) { return }
  if ($base.Length -ne $v.Length) { Write-Output ("  {0,-26} 长度不同({1} vs {2})" -f $name, $base.Length, $v.Length); return }
  $diff = 0; $maxd = 0; $sum = 0.0
  for ($i = 0; $i -lt $v.Length; $i++) {
    if ($v[$i] -ne $base[$i]) {
      $diff++
      $dd = [Math]::Abs([int]$v[$i] - [int]$base[$i])
      if ($dd -gt $maxd) { $maxd = $dd }
      $sum += [int]$v[$i] - [int]$base[$i]
    }
  }
  $pct = 100.0 * $diff / $v.Length
  $mean = if ($diff -gt 0) { $sum / $diff } else { 0 }
  Write-Output ("  {0,-26} 差异样本={1,7:F2}% 最大|Δ|={2,3} 平均={3,7:F3}（字节级 {4}）" -f `
    $name, $pct, $maxd, $mean, $(if ($diff -eq 0) { "完全相同" } else { "不同" }))
}

Write-Host "=== yuv444p10le（4:4:4，只考察矩阵+降位，不含子采样）：以默认参数为基准 ===" -ForegroundColor Cyan
$base = Conv "b444" "yuv444p10le" "-color_range pc"
DiffVsBase "accurate_rnd"      $base (Conv "v1" "yuv444p10le" "-color_range pc -sws_flags accurate_rnd")
DiffVsBase "full_chroma_int"   $base (Conv "v2" "yuv444p10le" "-color_range pc -sws_flags full_chroma_int")
DiffVsBase "accnd+full_chroma" $base (Conv "v3" "yuv444p10le" "-color_range pc -sws_flags accurate_rnd+full_chroma_int")
DiffVsBase "dither=none"       $base (Conv "v4" "yuv444p10le" "-color_range pc -sws_dither none")
DiffVsBase "dither=error_diff" $base (Conv "v5" "yuv444p10le" "-color_range pc -sws_dither error_diffusion")
DiffVsBase "dither=bayer"      $base (Conv "v6" "yuv444p10le" "-color_range pc -sws_dither bayer")

Write-Host "`n=== yuv420p10le（AVIF 实际用的 4:2:0，含色度下采样）===" -ForegroundColor Cyan
$base2 = Conv "b420" "yuv420p10le" "-color_range pc"
DiffVsBase "accurate_rnd"      $base2 (Conv "u1" "yuv420p10le" "-color_range pc -sws_flags accurate_rnd")
DiffVsBase "full_chroma_int"   $base2 (Conv "u2" "yuv420p10le" "-color_range pc -sws_flags full_chroma_int")
DiffVsBase "accnd+full_chroma" $base2 (Conv "u3" "yuv420p10le" "-color_range pc -sws_flags accurate_rnd+full_chroma_int")
DiffVsBase "dither=error_diff" $base2 (Conv "u4" "yuv420p10le" "-color_range pc -sws_dither error_diffusion")
Write-Host "`n判读：'完全相同' 表示该参数在此路径无效（不必钉）；有差异则表示 AVIF/GIF→AVIF 的取色由这些参数决定。" -ForegroundColor DarkGray
