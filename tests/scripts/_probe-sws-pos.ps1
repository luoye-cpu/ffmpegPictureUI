# 实测③：-sws_flags 放在 -i 之前(全局) vs 之后(产品当前写法) 是否生效
# ⚠ 函数名不可用单字母(r/h/m 等均为 PowerShell 内置别名)。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$d  = "$root/tests/output/validate/swsyuv"
$W = 1024; $H = 64

function RunOne([string]$tag, [string]$argLine) {
  $tag = $tag.Trim()                              # ⚠ 必须 trim：标签补齐空格会生成 "g_none  .bin" 这种文件名
  $out = Join-Path $d ($tag + ".bin")
  if (Test-Path $out) { Remove-Item $out -Force -ErrorAction SilentlyContinue }
  $p = Start-Process -FilePath $ff -ArgumentList $argLine -NoNewWindow -Wait -PassThru -RedirectStandardError "$d/ee.txt"
  $exists = Test-Path $out
  Write-Output ("  {0,-12} rc={1,3} 产物={2} size={3}" -f $tag, $p.ExitCode, $exists, (Get-Item $out -EA SilentlyContinue).Length)
  if (-not $exists) { (Get-Content "$d/ee.txt" -EA SilentlyContinue | Select-Object -Last 1) | ForEach-Object { "        " + $_.ToString().Trim() } }
}

$inA  = "-f rawvideo -pix_fmt rgb48le -video_size ${W}x${H} -i `"$d/in.raw`""
$vf   = "-vf `"format=rgb48le,format=yuv420p10le`""
RunOne "g_before" "-y -hide_banner -loglevel error -sws_flags accurate_rnd+full_chroma_int $inA $vf -frames:v 1 -f rawvideo `"$d/g_before.bin`""
RunOne "g_after " "-y -hide_banner -loglevel error $inA -sws_flags accurate_rnd+full_chroma_int $vf -frames:v 1 -f rawvideo `"$d/g_after.bin`""
RunOne "g_none  " "-y -hide_banner -loglevel error $inA $vf -frames:v 1 -f rawvideo `"$d/g_none.bin`""

function CmpFiles([string]$x, [string]$y) {
  $x = $x.Trim(); $y = $y.Trim()
  $pa = "$d/$x.bin"; $pb = "$d/$y.bin"
  if (-not (Test-Path $pa) -or -not (Test-Path $pb)) { Write-Output "  $x vs $y : 有文件缺失，跳过（不可判读）"; return }
  $a = [System.IO.File]::ReadAllBytes($pa); $b = [System.IO.File]::ReadAllBytes($pb)
  if ($a.Length -ne $b.Length) { Write-Output "  $x vs $y : 长度不同 $($a.Length)/$($b.Length)"; return }
  $df = 0; $maxd = 0
  for ($i = 0; $i -lt $a.Length; $i++) { if ($a[$i] -ne $b[$i]) { $df++; $dd = [Math]::Abs([int]$a[$i] - [int]$b[$i]); if ($dd -gt $maxd) { $maxd = $dd } } }
  Write-Output ("  {0} vs {1} : 差异字节={2,7} ({3,5:F2}%) 最大|Δ|={4}" -f $x, $y, $df, (100.0 * $df / $a.Length), $maxd)
}
Write-Host "`n=== 两两比较（同尺寸才可比）===" -ForegroundColor Cyan
CmpFiles "g_before" "g_none"
CmpFiles "g_after"  "g_none"
CmpFiles "g_before" "g_after"
