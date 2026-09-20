# zscale 传递函数定位实验（P0）
# 目的：
#  1) 判定 zscale 输出是否存在 8-bit 量化（值是否为 257 的整数倍）——解释此前恒定的 ~1.6% 偏移
#  2) 用整条 16-bit 灰阶斜坡拟合 bt709 到底是 BT.709-6 定义式(α/β) 还是纯 gamma
#  3) 对照组：不加 zscale 的纯 format 往返，确认偏移归属
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
# ⚠ 运行级隔离（GUID，2026-09-17）：固定 `$d` + 开头整目录删，会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
$d  = "$root/tests/output/validate/ztf3_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force -Path $d | Out-Null

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$d/_exec.out"; $e = "$d/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}

$W = 512; $H = 4
# 1) 造 16-bit 灰阶斜坡 raw（rgb48le），每列码值 = round(i/(W-1)*65535)
$raw = New-Object 'byte[]' ($W * $H * 6)
for ($i = 0; $i -lt $W; $i++) {
  $v = [int][Math]::Round($i / ($W - 1.0) * 65535.0)
  for ($y = 0; $y -lt $H; $y++) {
    $o = ($y * $W + $i) * 6
    $raw[$o] = ($v -band 0xFF); $raw[$o+1] = (($v -shr 8) -band 0xFF)
    $raw[$o+2] = $raw[$o]; $raw[$o+3] = $raw[$o+1]
    $raw[$o+4] = $raw[$o]; $raw[$o+5] = $raw[$o+1]
  }
}
[System.IO.File]::WriteAllBytes("$d/ramp.raw", $raw)
# 打成带标签的 16bit PNG（sRGB 源）
Exec $ff "-y -hide_banner -loglevel error -f rawvideo -pix_fmt rgb48le -video_size `"${W}x$H`" -i `"$d/ramp.raw`" -color_primaries bt709 -color_trc iec61966-2-1 -color_range pc -compression_level 1 `"$d/ramp_srgb.png`""
Write-Output ("输入: {0}x{1} 16-bit 灰阶斜坡, 标签 bt709/iec61966-2-1/pc  文件 {2}B" -f $W,$H,(Get-Item "$d/ramp_srgb.png").Length)

function Run($tag, $vf) {
  $out = "$d/$tag.raw"
  $p = Start-Process -FilePath $ff -ArgumentList "-y -hide_banner -loglevel error -i `"$d/ramp_srgb.png`" -vf `"$vf`" -frames:v 1 -f rawvideo `"$out`"" -NoNewWindow -Wait -PassThru
  if (-not (Test-Path $out)) { Write-Output "  $tag : 无输出"; return $null }
  $b = [System.IO.File]::ReadAllBytes($out)
  $v = New-Object 'int[]' $W
  for ($i = 0; $i -lt $W; $i++) { $v[$i] = $b[$i*6] + ($b[$i*6+1] * 256) }   # 第 0 行 R 通道
  return ,$v
}
# 对照组 A：完全不经 zscale（只验证 解码→float→编码 的往返是否无损）
$ctrl = Run "ctrl_nozscale" "format=gbrpf32le,format=rgb48le"
# 对照组 B：zscale 恒等（同曲线同原色）
$idn  = Run "z_identity" "format=gbrpf32le,zscale=pin=bt709:tin=iec61966-2-1:min=gbr:rin=pc:p=bt709:t=iec61966-2-1:m=bt709:r=pc,format=rgb48le"
$b709 = Run "z_to709"    "format=gbrpf32le,zscale=pin=bt709:tin=iec61966-2-1:min=gbr:rin=pc:p=bt709:t=bt709:m=bt709:r=pc,format=rgb48le"
$g22  = Run "z_to470m"   "format=gbrpf32le,zscale=pin=bt709:tin=iec61966-2-1:min=gbr:rin=pc:p=bt709:t=bt470m:m=bt709:r=pc,format=rgb48le"
$lin  = Run "z_tolin"    "format=gbrpf32le,zscale=pin=bt709:tin=iec61966-2-1:min=gbr:rin=pc:p=bt709:t=linear:m=bt709:r=pc,format=rgb48le"

function Fingerprint($name, $v) {
  if ($null -eq $v) { return }
  $mult257 = 0; $nonZero = 0
  for ($i = 1; $i -lt $W; $i++) { if ($v[$i] -gt 0) { $nonZero++; if (($v[$i] % 257) -le 1 -or (257 - ($v[$i] % 257)) -le 1) { $mult257++ } } }
  Write-Output ("  {0,-16} 257整数倍占比 = {1,5:F1}%  (高→存在 8bit 量化)" -f $name, (100.0 * $mult257 / [Math]::Max(1,$nonZero)))
}
Write-Host "`n=== 1) 量化指纹 ===" -ForegroundColor Cyan
Fingerprint "对照(无zscale)" $ctrl
Fingerprint "zscale 恒等"    $idn
Fingerprint "zscale→bt709"   $b709

# 2) 预测模型（PowerShell 独立实现，不引用项目代码）
function EncSrgb($l)   { if ($l -le 0.0031308) { 12.92*$l } else { 1.055*[Math]::Pow($l,1/2.4)-0.055 } }
function DecSrgb($v)   { if ($v -le 0.04045) { $v/12.92 } else { [Math]::Pow(($v+0.055)/1.055, 2.4) } }
function Enc709($l)    { if ($l -lt 0.0181) { 4.5*$l } else { 1.099*[Math]::Pow($l,0.45)-0.099 } }
function EncG24($l)    { [Math]::Pow($l,1/2.4) }
function EncG22($l)    { [Math]::Pow($l,1/2.2) }

function Fit($name, $v, [scriptblock]$pred) {
  if ($null -eq $v) { return }
  $sse = 0.0; $n = 0; $mx = 0.0
  for ($i = 32; $i -lt $W; $i++) {          # 跳过最低段（暗部量化影响）
    $codeIn = $i / ($W - 1.0)
    $expect = & $pred $codeIn
    $e = ($v[$i] / 65535.0) - $expect
    $sse += $e*$e; $n++; if ([Math]::Abs($e) -gt $mx) { $mx = [Math]::Abs($e) }
  }
  Write-Output ("  {0,-22} RMS={1,8:F5}  max={2,8:F5}" -f $name, [Math]::Sqrt($sse/$n), $mx)
}
Write-Host "`n=== 2) 拟合：zscale→bt709 的输出 vs 三种候选模型（误差按归一化码值） ===" -ForegroundColor Cyan
Fit "BT.709-6 定义式(α/β)" $b709 { param($c) Enc709 (DecSrgb $c) }
Fit "纯 gamma 1/2.4"       $b709 { param($c) EncG24 (DecSrgb $c) }
Fit "恒等(=sRGB 不变)"      $b709 { param($c) $c }
Write-Host "`n=== 3) 拟合：zscale→bt470m(γ2.2 声明) ===" -ForegroundColor Cyan
Fit "γ=1/2.2 编码"          $g22  { param($c) EncG22 (DecSrgb $c) }
Fit "BT.709 定义式"         $g22  { param($c) Enc709 (DecSrgb $c) }
Write-Host "`n=== 4) 恒等路径是否无损（偏移归属定位） ===" -ForegroundColor Cyan
Fit "zscale恒等=输入"       $idn  { param($c) $c }
Fit "无zscale=输入"         $ctrl { param($c) $c }
Fit "sRGB解码再编码=输入"   $idn  { param($c) EncSrgb (DecSrgb $c) }
Write-Host "`n=== 5) 线性输出对照（sRGB→linear，检验 EOTF 是否正确） ===" -ForegroundColor Cyan
Fit "线性=sRGB EOTF"        $lin  { param($c) DecSrgb $c }
Fit "线性=恒等"              $lin  { param($c) $c }
Write-Host "`n=== 6) 抽样码值表（输入 → 各路径输出） ===" -ForegroundColor Cyan
Write-Output ("  {0,6} {1,8} {2,8} {3,8} {4,8} {5,8}" -f "in8bit","输入16","恒等","→bt709","→bt470m","→linear")
foreach ($i in @(1,16,32,64,128,192,256,384,511)) {
  $idx = [int][Math]::Round(($i - 1) / 255.0 * ($W - 1))
  Write-Output ("  {0,6} {1,8} {2,8} {3,8} {4,8} {5,8}" -f $i, $idx,
    ($(if($ctrl){$ctrl[$idx]}else{'-'})), ($(if($idn){$idn[$idx]}else{'-'})),
    ($(if($b709){$b709[$idx]}else{'-'})), ($(if($g22){$g22[$idx]}else{'-'})), ($(if($lin){$lin[$idx]}else{'-'})))
}

# ⚠ 结尾自清（运行级隔离配套）：本脚本**无 pass/fail 计数** ⇒ 必须**无条件**自清 ——
#   `if ($fail -eq 0)` 在无计数脚本里 `$null -eq 0` 为 False，**永不成立**（§6 第 66 条已记这个坑）。
Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue
