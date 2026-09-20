# verify-color-caps.ps1 —— 色彩能力表行为自检（规划 §5′.5，P3.5b 退出门禁）
# 目的：FormatColorCaps 里的每一条"能不能"都必须由**实测**背书；工具链升级后若行为漂移，本脚本必须 FAIL，
#       防止引擎按过时假设静默产出错误标注（本项目已多次因"凭印象的能力假设"吃过亏）。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$ff   = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$et   = "$root/publish/PLAN/exiftool/exiftool.exe"
$cjxl = "$root/publish/PLAN/jxl/bin/cjxl.exe"
$jinfo= "$root/publish/PLAN/jxl/bin/jxlinfo.exe"
$av   = "$root/publish/PLAN/artifacts/avifenc.exe"
$d    = "$root/tests/output/validate/caps"
if (Test-Path $d) { Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $d | Out-Null

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$d/_exec.out"; $e = "$d/_exec.err"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e
  $global:LASTEXITCODE = $p.ExitCode
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}
$pass = 0; $fail = 0
function CK($ok, $msg) { if ($ok) { $script:pass++; Write-Output "  PASS $msg" } else { $script:fail++; Write-Output "  FAIL $msg" } }
function RC($args2) { $p = Start-Process -FilePath $ff -ArgumentList $args2 -NoNewWindow -Wait -PassThru -RedirectStandardError "$d/e.txt"; return $p.ExitCode }

Write-Host "=== 工具链版本（能力表 VerifiedOn 的依据）===" -ForegroundColor Cyan
Write-Output ("  ffmpeg   : " + (((Exec $ff "-hide_banner -version") -split "`r?`n") | Select-Object -First 1))
Write-Output ("  exiftool : " + ((Exec $et "-ver") | Out-String).Trim())
Write-Output ("  cjxl     : " + (((Exec $cjxl "--version") -split "`r?`n") | Select-Object -First 1))
Write-Output ("  avifenc  : " + (((Exec $av "--version") -split "`r?`n") | Select-Object -First 1))

# ── 素材：带 Display P3 ICC 的 16bit PNG + 提取的纯 ICC ──
# ⚠ 生成机制已抽到 `_lib-color-assets.ps1`（**唯一一份**）：`verify-color-wiring.ps1` (8) 段同样需要
#   这份素材。此前两处各写一份会让"P3 素材"有两个口径；且 wiring 直接读本脚本的输出目录会形成
#   「门禁依赖另一门禁的遗留产物」（干净 tests/output 上必红）—— 见 docs/TESTING.md §6。
. "$root/tests/scripts/_lib-color-assets.ps1"
$asset  = New-P3IccAsset -Dir $d -Ffmpeg $ff -Exiftool $et
$iccLen = $asset.IccLength
CK ($iccLen -gt 200) "素材就绪：p3.png 内嵌 ICC 可提取（$iccLen B）"
function IccOf($f) { [bool](((Exec $et "-a -s -ICC_Profile `"$f`"") | Out-String).Trim() -match "ICC_Profile") }
function CicpOf($f) { (((Exec $et "-a -G1 -s -ColorPrimaries -TransferCharacteristics `"$f`"") | Out-String)) }

Write-Host "`n=== (a) iccgen 不支持 HDR 曲线（必须失败）===" -ForegroundColor Cyan
$r = RC "-y -hide_banner -loglevel error -f lavfi -i color=gray:s=16x16 -vf `"format=gbrpf32le,iccgen=color_primaries=bt2020:color_trc=smpte2084:force=1`" -frames:v 1 `"$(Join-Path $d 'a.avif.png')`""
CK ($r -ne 0) "iccgen + smpte2084 失败（rc=$r）→ HDR 只能走 CICP 枚举"

Write-Host "`n=== (b) exiftool 不能后置写 JXL/AVIF 的 ICC ===" -ForegroundColor Cyan
$null = Exec $ff "-y -hide_banner -loglevel error -f lavfi -i color=red:s=16x16 -frames:v 1 `"$d/plain.png`""
$null = RC "-y -hide_banner -loglevel error -i `"$(Join-Path $d 'plain.png')`" -c:v libjxl -distance 0 `"$(Join-Path $d 'b.jxl')`""
$null = Exec $et "-overwrite_original `"-icc_profile<=$d/p3.icc`" `"$d/b.jxl`""
CK (-not (IccOf "$d/b.jxl")) "exiftool 写 JXL ICC 无效（读回无 ICC）→ JXL 必须走 cjxl -x icc_pathname"
$null = Exec $av "--lossless `"$d/plain.png`" `"$d/b.avif`""
$null = Exec $et "-overwrite_original `"-icc_profile<=$d/p3.icc`" `"$d/b.avif`""
CK (-not (IccOf "$d/b.avif")) "exiftool 写 AVIF ICC 无效（读回无 ICC）→ AVIF 必须由 avifenc 在编码时写"

Write-Host "`n=== (c)(e) JXL：icc_pathname 可写；与 color_space 不可共存（E2）===" -ForegroundColor Cyan
$null = Exec $ff "-y -hide_banner -loglevel error -i `"$d/p3.png`" -pix_fmt rgb48le `"$d/p3.ppm`""
$null = Exec $cjxl "`"$d/p3.ppm`" `"$d/c_icc.jxl`" -x icc_pathname=`"$d/p3.icc`" -d 0"
$null = Exec $cjxl "`"$d/p3.ppm`" `"$d/c_enum.jxl`" -x color_space=DisplayP3 -d 0"
$null = Exec $cjxl "`"$d/p3.ppm`" `"$d/c_both.jxl`" -x color_space=DisplayP3 -x icc_pathname=`"$d/p3.icc`" -d 0"
$hasI = [bool](Test-Path "$d/c_icc.jxl"); $hasB = [bool](Test-Path "$d/c_both.jxl")
$jiIcc = if ($hasI) { ((Exec $jinfo "`"$d/c_icc.jxl`"") | Out-String) } else { "" }
CK ($jiIcc -match "ICC profile") "cjxl -x icc_pathname 写入 ICC 成功（jxlinfo 可见；注：exiftool 读不到，不能用它验 JXL）"
# 重要工具链事实：exiftool 对 JXL **连读都读不出 ICC** → 引擎的“标注自洽断言”对 JXL 必须走 jxlinfo
CK (-not (IccOf "$d/c_icc.jxl")) "exiftool 无法读出已写入的 JXL ICC ⇒ JXL 回读验证必须用 jxlinfo（写进 §7 A2 工具选型）"
if ($hasI -and $hasB) {
  $si = (Get-Item "$d/c_icc.jxl").Length; $sb = (Get-Item "$d/c_both.jxl").Length
  $ji = (Exec $jinfo "`"$d/c_both.jxl`"") | Out-String
  CK ($si -eq $sb -and $ji -match "ICC profile") "E2：同给 color_space+icc_pathname 与「仅 ICC」字节相同($sb B) 且 jxlinfo 只报 ICC ⇒ JXL 不能 ICC/CICP 共存（引擎必须择一，默认 ICC）"
} else { CK $false "E2：cjxl 产物缺失，无法判定共存性" }

Write-Host "`n=== (d) AVIF：avifenc 可携带任意 ICC，且可与 nclx 共存（E1）===" -ForegroundColor Cyan
$null = Exec $av "--lossless --icc `"$d/p3.icc`" `"$d/p3.png`" `"$d/d_icc.avif`""
$null = Exec $av "--lossless --icc `"$d/p3.icc`" --nclx 12/1/0 `"$d/p3.png`" `"$d/d_both.avif`""
CK ((Test-Path "$d/d_icc.avif") -and (IccOf "$d/d_icc.avif")) "E1：avifenc --icc 成功且 ICC 可读回 ⇒ AVIF 可携带任意 ICC"
$b2 = if (Test-Path "$d/d_both.avif") { IccOf "$d/d_both.avif" } else { $false }
$bothTxt = if (Test-Path "$d/d_both.avif") { CicpOf "$d/d_both.avif" } else { "" }
CK ($b2 -and $bothTxt -match "ColorPrimaries") "E1：同一文件内 ICC 与 nclx 共存（S1 可产出 ICC+CICP 双描述）"

Write-Host "`n=== (f) zscale 的 t=bt709 仍是纯 γ2.4（口径黑名单前提，防版本漂移）===" -ForegroundColor Cyan
$W = 256
$raw = New-Object 'byte[]' ($W * 6)
for ($i = 0; $i -lt $W; $i++) {
  $v = [int][Math]::Round($i / ($W - 1.0) * 65535.0)
  $o = $i * 6
  $raw[$o] = ($v -band 0xFF); $raw[$o+1] = (($v -shr 8) -band 0xFF)
  $raw[$o+2] = $raw[$o]; $raw[$o+3] = $raw[$o+1]; $raw[$o+4] = $raw[$o]; $raw[$o+5] = $raw[$o+1]
}
[System.IO.File]::WriteAllBytes("$d/ramp.raw", $raw)
$null = RC "-y -hide_banner -loglevel error -f rawvideo -pix_fmt rgb48le -video_size ${W}x1 -i `"$d/ramp.raw`" -color_primaries bt709 -color_trc iec61966-2-1 -color_range pc `"$(Join-Path $d 'ramp.png')`""
$null = RC "-y -hide_banner -loglevel error -i `"$d/ramp.png`" -vf `"format=gbrpf32le,zscale=pin=bt709:tin=iec61966-2-1:min=gbr:rin=pc:p=bt709:t=bt709:m=bt709:r=pc,format=rgb48le`" -f rawvideo `"$d/z709.raw`""
$b = [System.IO.File]::ReadAllBytes("$d/z709.raw")
$m = ($b[128 * 6] + ($b[128 * 6 + 1] * 256)) / 65535.0          # 输入第 128 格（≈0.50172）
$c = 128 / ($W - 1.0)
$L = if ($c -le 0.04045) { $c / 12.92 } else { [Math]::Pow(($c + 0.055) / 1.055, 2.4) }
$g24 = [Math]::Pow($L, 1 / 2.4)
$alf = 1.099 * [Math]::Pow($L, 0.45) - 0.099
Write-Output ("  实测={0:F5}  纯γ2.4={1:F5}  BT.709-6定义式={2:F5}" -f $m, $g24, $alf)
CK ([Math]::Abs($m - $g24) -lt 0.005) ("zscale t=bt709 与纯 γ2.4 一致（差 {0:F5}）→ 引擎必须自己按标准定义式编码（禁 H2）" -f [Math]::Abs($m - $g24))
CK ([Math]::Abs($m - $alf) -gt 0.02)  ("zscale t=bt709 与 BT.709-6 定义式确实不同（差 {0:F5}）→ 非同一口径，不可混用" -f [Math]::Abs($m - $alf))

Write-Host "`n=== (h) HEIC/HEIF 写入通路（当前：无）===" -ForegroundColor Cyan
$hm = ((Exec $ff "-hide_banner -h muxer=heif") | Out-String).Trim()
$noMuxer = $hm -match "Unknown format"
$rcH = RC "-y -hide_banner -loglevel error -f lavfi -i color=red:s=16x16 -c:v libx265 -frames:v 1 `"$(Join-Path $d 'h.heic')`""
$heicOk = ($rcH -eq 0) -and ((Get-Item "$d/h.heic" -EA SilentlyContinue).Length -gt 0)
$heifEnc = [bool](Get-ChildItem "$root/publish" -Recurse -Filter "heifenc.exe" -EA SilentlyContinue)
CK ($noMuxer -and -not $heicOk -and -not $heifEnc) `
   ("HEIF 无写入通路（heif muxer 缺失={0} 直接写出成功={1} heifenc 存在={2}）→ 能力表 heic/heif 全保守" -f $noMuxer, $heicOk, $heifEnc)
if (-not ($noMuxer -and -not $heicOk -and -not $heifEnc)) {
  Write-Output "    ⚠ 工具链已具备 heif 写入能力：必须重测 E1同型实验并更新 FormatColorCaps[heic/heif]！"
}

Write-Host "`n=== (g) 未实测项（必须显式标注，不得当作已知事实）===" -ForegroundColor Cyan
Write-Output "  SKIP heic/heif ICC：由 (h) 已确认无写入通路 → ICC/CICP 共存性无意义（不再当推断事实）"
Write-Output "  SKIP jxr：本构建无 ICC/CICP 写通路（保守能力）→ 依赖 JxrService 实测结论"

Write-Host "`n===== caps 自检: PASS=$pass FAIL=$fail =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
exit $(if ($fail -eq 0) { 0 } else { 1 })
