# ProPhoto/ROMM「像素原样 + 仅靠 ICC」保真路径实测 (点三)
#   1) 造 ProPhoto 源（给 sRGB 测试图附着纯托管生成的 prophoto-rgb.icc）
#   2) 服务级: ServiceProbe faithful（ICC 结构/确定性）
#   3) PNG→PNG (推荐策略): 断言 RGB 样本逐位一致 + 输出带 ProPhoto ICC + 无 CICP 假标签
#   4) PNG→JPG / →TIFF / →WebP: 同上（有损容器的 ICC/CICP 标记一致性）
#   5) 显式目标 = ProPhoto (手动策略): 仍保真
#   6) ProPhoto → AVIF: 容器无法后置附着 ICC → 应保持既有 BT.2020 归一（不打「保真」日志）
#   7) 回归: 纯托管生成 ICC 的色度数值与已发布 ProPhoto(D50) 矩阵一致
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$exe   = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
$probe = "$root/tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
if (-not (Test-Path $probe)) { $probe = "$root/tests/ServiceProbe/bin/Debug/net11.0/win-x64/ServiceProbe.exe" }
$et    = "$root/publish/PLAN/exiftool/exiftool.exe"
$ff    = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$fp    = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
# ⚠ 运行级隔离（GUID）：固定 `$out` + 开头整目录删，会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
#   唯一目录没人替你清 ⇒ 结尾自清（照 `verify-gainmap-memory.ps1` 既有范式）。
$val   = "$root/tests/output/validate"; $out = "$val/ppro_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force -Path $out | Out-Null

# 取子进程输出一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）；回写 $LASTEXITCODE 保持原语义
function Exec([string]$file, [string]$argStr){
  $o = "$out/_exec.out"; $e = "$out/_exec.err"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e
  $global:LASTEXITCODE = $p.ExitCode
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}

$pass=0;$fail=0
function Ok($m){ Write-Host "  PASS $m" -ForegroundColor Green; $script:pass++ }
function No($m){ Write-Host "  FAIL $m" -ForegroundColor Red;   $script:fail++ }
function Run($a,$tag){
  Start-Sleep -Seconds 2
  Start-Process -FilePath $exe -ArgumentList $a -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput "$out/$tag.o" -RedirectStandardError "$out/$tag.e" | Out-Null
}
# 取输出文件的 RGB 样本哈希（无损比较像素，忽略压缩/元数据差异）
function PixHash($f,$pix){
  $raw = "$out/$([IO.Path]::GetFileNameWithoutExtension($f))-$pix.raw"
  Exec $ff "-y -v error -i `"$f`" -pix_fmt $pix -f rawvideo `"$raw`"" | Out-Null
  if(-not (Test-Path $raw)){ return $null }
  (Get-FileHash $raw -Algorithm SHA256).Hash
}
function IccDesc($f){ (Exec $et "-a -G1 -s -ProfileDescription -ProfileColorSpace -ICCProfileDescription `"$f`"").Trim() }
function Cicp($f){ (Exec $fp "-v error -select_streams v:0 -show_entries `"stream=color_primaries,color_transfer`" -of csv=p=0 `"$f`"").Trim() }

Write-Host "`n### 1) 造 ProPhoto 源 ###" -ForegroundColor Cyan
Copy-Item "$root/tests/output/sources/src_8bit.png" "$out/src_prophoto.png"
# 保真 ICC 路径由 ServiceProbe 输出（带写入器版本号，不硬编码文件名）
$ppIcc = $null
if (Test-Path $probe) {
  $fo = Exec $probe "faithful `"ProPhoto RGB`" yes"
  $mm = [regex]::Match($fo, "icc=(\S+?\.icc)")
  if ($mm.Success) { $ppIcc = $mm.Groups[1].Value.Trim() }
}
if (-not $ppIcc -or -not (Test-Path $ppIcc)) { No "无法定位纯托管生成的 ProPhoto ICC"; Write-Host "`n===== PASS=$pass FAIL=$fail =====" -ForegroundColor Red; exit 1 }
Ok "保真 ICC: $(Split-Path -Leaf $ppIcc) ($((Get-Item $ppIcc).Length) B)"
Exec $et "-overwrite_original -m `"-icc_profile<=$ppIcc`" `"$out/src_prophoto.png`"" | Out-Null
$srcDesc = IccDesc "$out/src_prophoto.png"
if ("$srcDesc" -match "ProPhoto") { Ok "源已附着 ProPhoto ICC ($srcDesc)" } else { No "源 ICC 异常: '$srcDesc'" }
$srcDump = Exec $et "-a -G1 -s `"$out/src_prophoto.png`""
if ($srcDump -notmatch "Corrupted") { Ok "exiftool 可无损解析生成的 mluc desc" } else { No "exiftool 报 Corrupted（生成器 mluc 格式有误）" }
$srcHash = PixHash "$out/src_prophoto.png" "rgb48le"
Ok "源像素哈希 $srcHash"

Write-Host "`n### 2) 服务级 ICC 自检 (确定性 + 结构 + 色度) ###" -ForegroundColor Cyan
if (Test-Path $probe) {
  $po = Exec $probe "faithful `"ProPhoto RGB`" yes"
  if($po -match "PROBE RESULT: pass=\d+ fail=0"){ Ok "ServiceProbe faithful 全通过 ($(([regex]::Matches($po,'(?m)^PASS')).Count) 项)" }
  else { No "ServiceProbe faithful 存在失败`n$po" }
  $dump = Exec $probe "icc-dump `"$ppIcc`" rXYZ gXYZ bXYZ"
  # 已发布 ProPhoto(D50) 矩阵: rXYZ(.79776,.28807,0) gXYZ(.13519,.71184,0) bXYZ(.03131,0,.82510)
  if($dump -match "rXYZ: XYZ \[0\.79\d+, 0\.288\d+, 0\.000\d+\]"){ Ok "rXYZ 与公开 ProPhoto D50 矩阵一致" } else { No "rXYZ 异常`n$dump" }
  if($dump -match "gXYZ: XYZ \[0\.135\d+, 0\.711\d+, 0\.000\d+\]"){ Ok "gXYZ 一致" } else { No "gXYZ 异常" }
  if($dump -match "bXYZ: XYZ \[0\.031\d+, 0\.000\d+, 0\.82[45]\d+\]"){ Ok "bXYZ 一致" } else { No "bXYZ 异常" }
} else { Write-Host "  SKIP 未构建 ServiceProbe" -ForegroundColor Yellow }

Write-Host "`n### 3) PNG → PNG (推荐策略, 应逐位保真) ###" -ForegroundColor Cyan
Run "--headless --log-level debug -i `"$out/src_prophoto.png`" --format png --color-strategy recommended --output `"$out/o_png`"" "png"
$o = (Get-ChildItem -Recurse "$out/o_png" -Filter *.png -EA SilentlyContinue | Select-Object -First 1).FullName
$log = (Get-Content "$out/png.o" -EA SilentlyContinue) -join "`n"
if(-not $o){ No "无 PNG 输出" } else {
  $h = PixHash $o "rgb48le"
  if($h -eq $srcHash){ Ok "RGB 样本逐位一致（像素未转换）" } else { No "像素被改动 ($h)" }
  if((IccDesc $o) -match "ProPhoto"){ Ok "输出带 ProPhoto ICC" } else { No "输出缺 ProPhoto ICC: $(IccDesc $o)" }
  if(((Exec $et "-a -G1 -s `"$o`"") -join "") -notmatch "Corrupted"){ Ok "输出 ICC 可被 exiftool 无损解析" } else { No "输出 ICC 解析报 Corrupted" }
  if($log -match "\[保真\]"){ Ok "日志走了保真路径" } else { No "日志未见保真附着" }
  if((Cicp $o) -notmatch "bt709|smpte432|bt2020"){ Ok "未写误导性 CICP（$((Cicp $o) -replace '\s','')）" } else { No "输出仍带 CICP 标签: $(Cicp $o)" }
}

Write-Host "`n### 4) PNG → JPG / TIFF / WebP ###" -ForegroundColor Cyan
foreach($fmt in @("jpg","tiff","webp")){
  Run "--headless --log-level debug -i `"$out/src_prophoto.png`" --format $fmt --output `"$out/o_$fmt`"" $fmt
  $f = Get-ChildItem -Recurse "$out/o_$fmt" -Include *.$fmt -EA SilentlyContinue | Select-Object -First 1
  $lg = (Get-Content "$out/$fmt.o" -EA SilentlyContinue) -join "`n"
  if(-not $f -or $f.Length -eq 0){ No "$fmt 无输出"; continue }
  if($lg -match "\[保真\]"){ Ok "$fmt 走保真路径" } else { No "$fmt 未走保真路径" }
  $d = IccDesc $f.FullName
  if("$d" -match "ProPhoto"){ Ok "$fmt 输出带 ProPhoto ICC" } else { No "$fmt 输出缺 ProPhoto ICC: $d" }
  if($fmt -eq "tiff"){
    $th = PixHash $f.FullName "rgb48le"
    if($th -eq $srcHash){ Ok "$fmt 像素逐位一致" } else { No "$fmt 像素被改动" }
  }
}

Write-Host "`n### 5) 显式目标 = ProPhoto (手动策略) 仍保真 ###" -ForegroundColor Cyan
Run "--headless --log-level debug -i `"$out/src_prophoto.png`" --format png --color-strategy manual --color-space `"ProPhoto RGB`" --output `"$out/o_manual`"" "man"
$mo = (Get-ChildItem -Recurse "$out/o_manual" -Filter *.png -EA SilentlyContinue | Select-Object -First 1).FullName
$ml = (Get-Content "$out/man.o" -EA SilentlyContinue) -join "`n"
if($mo){
  if($ml -match "\[保真\]"){ Ok "手动 ProPhoto 目标走保真" } else { No "手动 ProPhoto 未保真" }
  if((PixHash $mo "rgb48le") -eq $srcHash){ Ok "手动 ProPhoto 像素逐位一致" } else { No "手动 ProPhoto 像素被改动" }
} else { No "手动 ProPhoto 无输出" }

Write-Host "`n### 6) ProPhoto → AVIF (容器不能后置附着 ICC → 保持归一化, 不打保真日志) ###" -ForegroundColor Cyan
Run "--headless --log-level debug -i `"$out/src_prophoto.png`" --format avif --output `"$out/o_avif`"" "avif"
$av = Get-ChildItem -Recurse "$out/o_avif" -Include *.avif -EA SilentlyContinue | Select-Object -First 1
$al = (Get-Content "$out/avif.o" -EA SilentlyContinue) -join "`n"
if($av -and $av.Length -gt 0){ Ok "AVIF 正常产出 ($($av.Length) B)" } else { No "AVIF 失败" }
if($al -notmatch "\[保真\]"){ Ok "AVIF 未误用保真路径（ICC 无法附着）" } else { No "AVIF 错误地走了保真路径" }

Write-Host "`n### 7) 非超广色域不受影响 (sRGB→png 不应打保真日志) ###" -ForegroundColor Cyan
Run "--headless --log-level debug -i `"$root/tests/output/sources/src_8bit.png`" --format png --output `"$out/o_srgb`"" "srgb"
$sl = (Get-Content "$out/srgb.o" -EA SilentlyContinue) -join "`n"
if($sl -notmatch "\[保真\]"){ Ok "sRGB 源不走保真路径" } else { No "sRGB 源误走保真路径" }
$so = (Get-ChildItem -Recurse "$out/o_srgb" -Filter *.png -EA SilentlyContinue | Select-Object -First 1).FullName
if($so -and (PixHash $so "rgb48le") -eq (PixHash "$root/tests/output/sources/src_8bit.png" "rgb48le")){ Ok "sRGB→PNG 像素未改动" } else { No "sRGB→PNG 像素变化" }

Write-Host "`n===== PASS=$pass FAIL=$fail =====" -ForegroundColor $(if($fail -eq 0){"Green"}else{"Red"})
# ⚠ 唯一目录自清（宿主批量删除守卫会拦整目录删 ⇒ 用 -EA SilentlyContinue，§6 第 57 条 ③）；失败则保留供排查
if ($fail -eq 0) { Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue }
# ⚠ **退出码语义**（2026-09-17 补，§6 第 58 条同类）：此前本脚本**全文无 `exit`** ⇒ 恒 `exit 0`；
#   而运行器/CI **只看退出码** ⇒ 它红了运行器也不知道（"恒绿门禁"）。
#   ⚠ 必须显式 `else { exit 0 }`：宿主删除守卫会污染 `$LASTEXITCODE`（实测变 2）⇒ 只写前半句会假红。
if ($fail -gt 0) { exit 1 } else { exit 0 }

