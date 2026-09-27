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
# ⚠ **取值一律走 ExecOut（只读 stdout）**，不要用上面的 `Exec`：`Exec` 把 stderr 并进返回值是有意的
#   （有的判据只能从 stderr 读 ffmpeg 的 `frame=`/`Corrupted`），但 exiftool 是 perl 打包的，
#   环境里带着本机 perl 不认的 `LC_ALL/LANG` 时**每次调用都往 stderr 喷 locale warning** ⇒
#   拿 `Exec` 的返回值做**负断言**（`-not (IccOf …)`）会被污染撑成恒真 = 假绿（#51 数出的两处之一）。
function ExecOut([string]$file, [string]$argStr){
  $o = "$d/_out.out"; $e = "$d/_out.err"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e
  $global:LASTEXITCODE = $p.ExitCode
  return [System.IO.File]::ReadAllText($o)
}

Write-Host "=== 工具链版本（能力表 VerifiedOn 的依据）===" -ForegroundColor Cyan
Write-Output ("  ffmpeg   : " + (((ExecOut $ff "-hide_banner -version") -split "`r?`n") | Select-Object -First 1))
Write-Output ("  exiftool : " + ((ExecOut $et "-ver") | Out-String).Trim())
Write-Output ("  cjxl     : " + (((ExecOut $cjxl "--version") -split "`r?`n") | Select-Object -First 1))
Write-Output ("  avifenc  : " + (((ExecOut $av "--version") -split "`r?`n") | Select-Object -First 1))

# ── 素材：带 Display P3 ICC 的 16bit PNG + 提取的纯 ICC ──
# ⚠ 生成机制已抽到 `_lib-color-assets.ps1`（**唯一一份**）：`verify-color-wiring.ps1` (8) 段同样需要
#   这份素材。此前两处各写一份会让"P3 素材"有两个口径；且 wiring 直接读本脚本的输出目录会形成
#   「门禁依赖另一门禁的遗留产物」（干净 tests/output 上必红）—— 见 docs/TESTING.md §6。
. "$root/tests/scripts/_lib-color-assets.ps1"
$asset  = New-P3IccAsset -Dir $d -Ffmpeg $ff -Exiftool $et
$iccLen = $asset.IccLength
CK ($iccLen -gt 200) "素材就绪：p3.png 内嵌 ICC 可提取（$iccLen B）"
function IccOf($f) { [bool](((ExecOut $et "-a -s -ICC_Profile `"$f`"") | Out-String).Trim() -match "ICC_Profile") }
function CicpOf($f) { (((ExecOut $et "-a -G1 -s -ColorPrimaries -TransferCharacteristics `"$f`"") | Out-String)) }
# ⚠ 负断言的前置**正对照**：`IccOf` 后面要被用在"产物**不该**有 ICC"的判据上 ⇒ 必须先证明
#   这把尺子在"真有 ICC"的素材上读得到，否则它恒返回 $false 时那些负断言全是空的（假绿）。
CK (IccOf $asset.Png) '前置：IccOf 在同一张已知带 ICC 的素材 PNG 上读得到 ICC ⇒ 后面的"无 ICC"负断言才有意义'

Write-Host "`n=== (a) iccgen 不支持 HDR 曲线（必须失败）===" -ForegroundColor Cyan
$r = RC "-y -hide_banner -loglevel error -f lavfi -i color=gray:s=16x16 -vf `"format=gbrpf32le,iccgen=color_primaries=bt2020:color_trc=smpte2084:force=1`" -frames:v 1 `"$(Join-Path $d 'a.avif.png')`""
CK ($r -ne 0) "iccgen + smpte2084 失败（rc=$r）→ HDR 只能走 CICP 枚举"

Write-Host "`n=== (b) exiftool 不能后置写 JXL/AVIF 的 ICC ===" -ForegroundColor Cyan
$null = Exec $ff "-y -hide_banner -loglevel error -f lavfi -i color=red:s=16x16 -frames:v 1 `"$d/plain.png`""
$null = RC "-y -hide_banner -loglevel error -i `"$(Join-Path $d 'plain.png')`" -c:v libjxl -distance 0 `"$(Join-Path $d 'b.jxl')`""
$null = Exec $et "-overwrite_original `"-icc_profile<=$d/p3.icc`" `"$d/b.jxl`""  # 合并取数已论证：exiftool 写 ICC / -b 抽取到文件，返回值不喂任何断言（读值一律走 ExecOut）
CK (-not (IccOf "$d/b.jxl")) "exiftool 写 JXL ICC 无效（读回无 ICC）→ JXL 必须走 cjxl -x icc_pathname"
$null = Exec $av "--lossless `"$d/plain.png`" `"$d/b.avif`""
$null = Exec $et "-overwrite_original `"-icc_profile<=$d/p3.icc`" `"$d/b.avif`""  # 合并取数已论证：exiftool 写 ICC / -b 抽取到文件，返回值不喂任何断言（读值一律走 ExecOut）
CK (-not (IccOf "$d/b.avif")) "exiftool 写 AVIF ICC 无效（读回无 ICC）→ AVIF 必须由 avifenc 在编码时写"

Write-Host "`n=== (c)(e) JXL：icc_pathname 可写；与 color_space 不可共存（E2）===" -ForegroundColor Cyan
$null = Exec $ff "-y -hide_banner -loglevel error -i `"$d/p3.png`" -pix_fmt rgb48le `"$d/p3.ppm`""
$null = Exec $cjxl "`"$d/p3.ppm`" `"$d/c_icc.jxl`" -x icc_pathname=`"$d/p3.icc`" -d 0"
$null = Exec $cjxl "`"$d/p3.ppm`" `"$d/c_enum.jxl`" -x color_space=DisplayP3 -d 0"
$null = Exec $cjxl "`"$d/p3.ppm`" `"$d/c_both.jxl`" -x color_space=DisplayP3 -x icc_pathname=`"$d/p3.icc`" -d 0"
$hasI = [bool](Test-Path "$d/c_icc.jxl"); $hasB = [bool](Test-Path "$d/c_both.jxl")
$jiIcc = if ($hasI) { ((ExecOut $jinfo "`"$d/c_icc.jxl`"") | Out-String) } else { "" }
CK ($jiIcc -match "ICC profile") "cjxl -x icc_pathname 写入 ICC 成功（jxlinfo 可见；注：exiftool 读不到，不能用它验 JXL）"
# 重要工具链事实：exiftool 对 JXL **连读都读不出 ICC** → 引擎的“标注自洽断言”对 JXL 必须走 jxlinfo
CK (-not (IccOf "$d/c_icc.jxl")) "exiftool 无法读出已写入的 JXL ICC ⇒ JXL 回读验证必须用 jxlinfo（写进 §7 A2 工具选型）"
if ($hasI -and $hasB) {
  $si = (Get-Item "$d/c_icc.jxl").Length; $sb = (Get-Item "$d/c_both.jxl").Length
  $ji = (ExecOut $jinfo "`"$d/c_both.jxl`"") | Out-String
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
$hm = ((ExecOut $ff "-hide_banner -h muxer=heif") | Out-String).Trim()
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
Write-Host "`n=== (g) jxr 的 ICC 写/读通路（2026-09-27：此处原为一行**纯打印 SKIP**，现换成实测）===" -ForegroundColor Cyan
# 为什么必须换成实测：原 SKIP 写的是「本构建无 ICC/CICP 写通路」⇒ 让能力表的
#   `FormatColorCaps[jxr].CanCarryArbitraryIcc=false` 看起来有工具链背书。实测（取证 `tests/output/t50/`）
#   已否证：**exiftool 对 .jxr 能写能逐字节读回**，所以「无通路」这一半依据是假的（保守结论目前仍成立，
#   但剩下的依据只有「产品侧未接线」。⇒ 本条钉的是**通路存在**这一事实：哪天工具链退化到写不进/读不回，
#   本条要红（那时才有理由回到 SKIP，或去改能力表）。
# ⚠ 本条**只钉工具链事实**，不改任何格的结局 —— 档①（把 CanCarryArbitraryIcc 翻 true）会连锁改
#   ColorStrategyPlanning 的 5 处判据与 360 格网格，属能力扩张，**需维护者授权**，见 FIX_PLAN #52。
# ⚠ 回读一律 exiftool，**不许走 ffmpeg**：ffmpeg 解 .jxr 不导出 ICC（实测）。
$jxrOut = "$d/g.jxr"
$rcJ = RC "-y -hide_banner -loglevel error -i `"$($asset.Png)`" -c:v libjxr -pix_fmt rgb48le `"$jxrOut`""
$made = (Get-Item $jxrOut -EA SilentlyContinue)
CK ($rcJ -eq 0 -and $made -and $made.Length -gt 0) `
   ("jxr 夹具就绪：ffmpeg 以 -c:v libjxr 可编（rc=$rcJ，$($made.Length) B）→ 本构建确有 jxr 编码器")
function IccBytes($f) {
  $w = "$d/_readback"; if (Test-Path $w) { Remove-Item -Recurse -Force $w -EA SilentlyContinue }
  New-Item -ItemType Directory -Force -Path $w | Out-Null
  Exec $et "-q -icc_profile -b -w `"$w/%f.icc`" `"$f`"" | Out-Null  # 合并取数已论证：exiftool 写 ICC / -b 抽取到文件，返回值不喂任何断言（读值一律走 ExecOut）
  $hit = @(Get-ChildItem $w -Filter '*.icc' -EA SilentlyContinue)     # ⚠ 按 Extension 挑，不用 -Include
  if ($hit.Count -ne 1) { return $null }
  return [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($hit[0].FullName))
}
$srcIcc = IccBytes $asset.Icc          # 参照：素材 ICC 自己走同一读法（同一条抽取路线，排除取数偏置）
$pre    = IccBytes $jxrOut
if (-not $pre) {
  Exec $et "-overwrite_original -m `"-icc_profile<=$($asset.Icc)`" `"$jxrOut`"" | Out-Null  # 合并取数已论证：exiftool 写 ICC / -b 抽取到文件，返回值不喂任何断言（读值一律走 ExecOut）
  $post = IccBytes $jxrOut
  $n = if ($post) { $post.Length } else { 0 }
  CK ($post -ne $null -and $post -eq $srcIcc) `
     ("jxr ICC 逐字节写回可读：exiftool 写入后读回 == 输入 ICC（base64 $n 字符，src=$($srcIcc.Length)）")
  $dec = "$d/g_post.tiff"
  $jd  = "$root/publish/PLAN/artifacts/JxrDecApp.exe"
  # 16-bit 夹具 ⇒ `-c 10`（48bppRGB）；实测 8-bit 夹具上 -c 10 会 rc=-106 ⇒ 位深与 -c 必须配对
  $rcD = if (Test-Path $jd) { (Start-Process -FilePath $jd -ArgumentList "-i `"$jxrOut`" -o `"$dec`" -c 10" -Wait -NoNewWindow -PassThru).ExitCode } else { -99 }
  $decOk = (Get-Item $dec -EA SilentlyContinue)
  CK ($rcD -eq 0 -and $decOk -and $decOk.Length -gt 0) `
     ("嵌 ICC 后 JxrDecApp 仍可解（rc=$rcD，$($decOk.Length) B）⇒ ICC 写入是非破坏性的")
} else {
  # 负控失效：未写入就读到了 ICC ⇒ 说明 .jxr 自带 ICC 或取数路线恒真，本条测不出东西 ⇒ 判红让人来分诊
  CK $false 'jxr 前置失效：写入**前**就能读到 ICC ⇒ 抽取路线无牙（须人工分诊，不许静默放行）'
}

Write-Host "`n===== caps 自检: PASS=$pass FAIL=$fail =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
exit $(if ($fail -eq 0) { 0 } else { 1 })
