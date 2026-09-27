# P3.5b 退出门禁实验：E1（AVIF 能否携带任意 ICC / 能否 ICC+CICP 共存）
#                                    E2（JXL 能否同写 color_space 枚举与 icc_pathname）
# 结论要回写 ColorMappingEngine 的 FormatColorCaps（CanCarryArbitraryIcc / CanHaveBothIccAndCicp）。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$ff   = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$encl = "$root/publish/PLAN/artifacts/avifenc.exe"
$cjxl = "$root/publish/PLAN/jxl/bin/cjxl.exe"
$jinfo= "$root/publish/PLAN/jxl/bin/jxlinfo.exe"
$et   = "$root/publish/PLAN/exiftool/exiftool.exe"
# ⚠ 运行级隔离（GUID，2026-09-17）：固定 `$d` + 开头整目录删，会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
$d    = "$root/tests/output/validate/caps-e12_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force -Path $d | Out-Null
# ⚠ 无 pass/fail 计数 ⇒ 结尾**无条件自清**（见文件末尾）。

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$d/_exec.out"; $e = "$d/_exec.err"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e
  $global:LASTEXITCODE = $p.ExitCode
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}

# 只读 stdout 版（取「工具的值」专用）：exiftool 的标签值走 **stdout**，而随包 exiftool 是 perl 打包版
#   ⇒ 进程环境带着本机 perl 不认的 LC_ALL/LANG（MSYS 里起门禁 = C.UTF-8）时**每次调用先往 stderr 喷
#   locale warning**，合并取数就会把警告当值返回（本机实测：同一素材合并 394 B / 只读 149 B，
#   差的就是那 6 行警告）。读 avifenc/cjxl/ffmpeg 的进度与报错（**只在 stderr**）仍用上面的 Exec。
function ExecOut([string]$file, [string]$argStr){
  $o = "$d/_exec.out"; $e = "$d/_exec.err"
  $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e
  $global:LASTEXITCODE = $p.ExitCode
  return [System.IO.File]::ReadAllText($o)
}

function Show($tag) { Write-Host "`n=== $tag ===" -ForegroundColor Cyan }

# ── 素材：带 Display P3 ICC 的 16bit PNG ──
# ⚠ iccgen 的选项名是 color_primaries / color_trc（**不是**位置参数；第三个位置参数是布尔 force）
Exec $ff "-y -hide_banner -loglevel error -f lavfi -i `"smptehdbars=s=64x64`" -vf `"format=gbrpf32le,zscale=pin=bt709:tin=iec61966-2-1:min=gbr:p=smpte432:t=iec61966-2-1:m=bt709,iccgen=color_primaries=smpte432:color_trc=iec61966-2-1:force=1`" -pix_fmt rgb48be -frames:v 1 `"$d/p3.png`""
if ($LASTEXITCODE -ne 0) { Write-Host "  素材生成失败 rc=$LASTEXITCODE" -ForegroundColor Red; Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue; exit 1 }
# ⚠ 提取二进制 ICC 必须用 exiftool -w（PowerShell 重定向会按文本解码而损坏字节流）
#    且 -w 必须含格式码（%f），否则不会生成我们指定的文件名
Remove-Item "$d/p3.icc" -EA SilentlyContinue
Exec $et "-b -icc_profile -w `"$d/%f.icc`" `"$d/p3.png`"" | Out-Null  # 合并取数已论证：这条不取值——二进制 ICC 由 -w 直接写进文件，返回值整条丢进 Out-Null，警告文本喂不到任何判据
if (-not (Test-Path "$d/p3.icc")) { Get-ChildItem "$d/*.icc" | ForEach-Object { "    (发现 $($_.Name))" } }
$iccBytes = (Get-Item "$d/p3.icc" -EA SilentlyContinue).Length
Write-Output "素材: p3.png=$((Get-Item "$d/p3.png").Length)B（内嵌 ICC）, 提取 p3.icc=${iccBytes}B"
if ($iccBytes -lt 200) { Write-Host "  ICC 提取失败（后续 --icc 实验受限，但 E1a0 内嵌直通仍可测）" -ForegroundColor Yellow }

# ── E1a0：源 PNG 内嵌 ICC 能否直通 AVIF（产品真实路径：avifenc 读入并写 colr）──
Show "E1a0: AVIF 内嵌 ICC 直通（不加任何色彩参数）"
$rc0 = Exec $encl "--lossless `"$d/p3.png`" `"$d/e1a0.avif`""
Write-Output "  avifenc exit=$LASTEXITCODE  产物=$((Get-Item "$d/e1a0.avif" -EA SilentlyContinue).Length)B"
$read0 = ExecOut $et "-a -G1 -s -ICC_Profile -ColorPrimaries -TransferCharacteristics -MatrixCoefficients `"$d/e1a0.avif`""
($read0 -split "`r?`n" | ForEach-Object { "    $($_)" })
$hasIcc0 = [bool]($read0 -match "ICC_Profile")

# ── E1a1：显式 avifenc --icc 能否携带任意 ICC ──
Show "E1a1: AVIF 显式携带任意 ICC（avifenc --icc）"
$hasIcc1 = $false
if ($iccBytes -ge 200) {
  $rc1 = Exec $encl "--lossless --icc `"$d/p3.icc`" `"$d/p3.png`" `"$d/e1a1.avif`""
  Write-Output "  avifenc exit=$LASTEXITCODE  产物=$((Get-Item "$d/e1a1.avif" -EA SilentlyContinue).Length)B"
  $read1 = ExecOut $et "-a -G1 -s -ICC_Profile -ColorPrimaries -TransferCharacteristics `"$d/e1a1.avif`""
  ($read1 -split "`r?`n" | ForEach-Object { "    $($_)" })
  $hasIcc1 = [bool]($read1 -match "ICC_Profile")
} else { Write-Output "  跳过（无 .icc 文件）" }
Write-Output "  → E1a 结论：内嵌直通=$hasIcc0  显式 --icc=$hasIcc1"

# ── E1b：ICC 与 nclx(CICP) 能否共存 ──
#   ⚠ help 写的是 `--cicp,--nclx P/T/M` —— 是**斜杠分隔的单个参数**，不是三个位置参数
#   P/T/M = 12(smpte432)/1(bt709 trc)/6(bt2020nc 以外的值也行)；故意给一组与 ICC 不同的标签
Show "E1c: 仅写 nclx（对照，确认本工具能写 CICP）"
$rcC = Exec $encl "--lossless --nclx 12/1/6 -r full `"$d/p3.png`" `"$d/e1c.avif`""
Write-Output "  avifenc exit=$LASTEXITCODE  产物=$((Get-Item "$d/e1c.avif" -EA SilentlyContinue).Length)B"
if ($LASTEXITCODE -ne 0) { (($rcC -split "`r?`n") | Select-Object -First 3 | ForEach-Object { "    ! $($_)" }) }
((ExecOut $et "-a -G1 -s -ICC_Profile -ColorPrimaries -TransferCharacteristics -MatrixCoefficients `"$d/e1c.avif`"") -split "`r?`n" | ForEach-Object { "    $($_)" })

Show "E1b: AVIF 同时写 rICC 与 nclx（--icc + --nclx）"
$rc2 = Exec $encl "--lossless --icc `"$d/p3.icc`" --nclx 12/1/6 -r full `"$d/p3.png`" `"$d/e1b.avif`""
Write-Output "  avifenc exit=$LASTEXITCODE  产物=$((Get-Item "$d/e1b.avif" -EA SilentlyContinue).Length)B"
if ($LASTEXITCODE -ne 0) { (($rc2 -split "`r?`n") | Select-Object -First 4 | ForEach-Object { "    ! $($_)" }) }
$read2 = ExecOut $et "-a -G1 -s -ICC_Profile -ColorPrimaries -TransferCharacteristics -MatrixCoefficients `"$d/e1b.avif`""
($read2 -split "`r?`n" | ForEach-Object { "    $($_)" })
$hasIcc2 = [bool]($read2 -match "ICC_Profile"); $hasNclx2 = [bool]($read2 -match "ColorPrimaries")
Write-Output "  → ICC 存在=$hasIcc2  nclx(CICP) 存在=$hasNclx2  （共存=$($hasIcc2 -and $hasNclx2)）"
# 解码侧实际采信哪一个：读回像素色度是否按 P3(ICC) 还是按 2020(nclx) 解释
Exec $ff "-y -hide_banner -loglevel error -i `"$d/e1b.avif`" -pix_fmt rgb48le `"$d/e1b.png`"" | Out-Null
Write-Output "  解码回读 ok=$((Test-Path "$d/e1b.png"))"

# ── E2：JXL 同写 color_space 枚举与 icc_pathname ──
Show "E2a: cjxl 仅 icc_pathname"
$ppm = "$d/p3.ppm"
Exec $ff "-y -hide_banner -loglevel error -i `"$d/p3.png`" -pix_fmt rgb48le `"$ppm`"" | Out-Null
$rc3 = Exec $cjxl "`"$ppm`" `"$d/e2a.jxl`" -x icc_pathname=`"$d/p3.icc`" -d 0"
Write-Output "  cjxl exit=$LASTEXITCODE  产物=$((Get-Item "$d/e2a.jxl" -EA SilentlyContinue).Length)B"
# jxlinfo 的色彩/码流信息同样走 **stdout**（本机实测：stdout 有内容、stderr 空）⇒ 一并走 ExecOut。
#   ⚠ 这类调用点扫描器看不见（它的白名单只认 $et/$exif*/$fp/$ffprobe*），别指望第 56 条替你钉住。
((ExecOut $jinfo "`"$d/e2a.jxl`"") -split "`r?`n" | ForEach-Object { "    $($_)" })

Show "E2b: cjxl 同给 color_space=DisplayP3 与 icc_pathname"
$rc4 = Exec $cjxl "`"$ppm`" `"$d/e2b.jxl`" -x color_space=DisplayP3 -x icc_pathname=`"$d/p3.icc`" -d 0"
Write-Output "  cjxl exit=$LASTEXITCODE  产物=$((Get-Item "$d/e2b.jxl" -EA SilentlyContinue).Length)B"
$i2b = ExecOut $jinfo "`"$d/e2b.jxl`""
($i2b -split "`r?`n" | ForEach-Object { "    $($_)" })

Show "E2c: cjxl 仅 color_space=DisplayP3（对照，看两者是否产生不同码流）"
$rc5 = Exec $cjxl "`"$ppm`" `"$d/e2c.jxl`" -x color_space=DisplayP3 -d 0"
Write-Output "  cjxl exit=$LASTEXITCODE  产物=$((Get-Item "$d/e2c.jxl" -EA SilentlyContinue).Length)B"
$sizeA = (Get-Item "$d/e2a.jxl" -EA SilentlyContinue).Length
$sizeB = (Get-Item "$d/e2b.jxl" -EA SilentlyContinue).Length
$sizeC = (Get-Item "$d/e2c.jxl" -EA SilentlyContinue).Length
Write-Output "  尺寸: 仅ICC=${sizeA}B  ICC+枚举=${sizeB}B  仅枚举=${sizeC}B"

Show "判定汇总"
Write-Output ("  E1: AVIF 可携带任意 ICC = {0}（内嵌直通={1} / 显式 --icc={2}）；ICC 与 CICP 可共存 = {3}" -f ($hasIcc0 -or $hasIcc1), $hasIcc0, $hasIcc1, ($hasIcc2 -and $hasNclx2))
$bothSame = ($sizeB -eq $sizeC)
Write-Output ("  E2: JXL 同写时码流与'仅枚举'完全同尺寸 = {0} → {1}" -f $bothSame, $(if ($bothSame) { "疑似 ICC 被 color_space 覆盖（不能共存）" } else { "尺寸不同，看上节 jxlinfo 实际报告" }))
Write-Output "  （把上面 4 项结论写回 FormatColorCaps：avif.CanCarryArbitraryIcc / avif.CanHaveBothIccAndCicp / jxl.CanHaveBothIccAndCicp）"

# ⚠ 结尾自清（运行级隔离配套）：本脚本**无 pass/fail 计数** ⇒ 必须**无条件**自清 ——
#   `if ($fail -eq 0)` 在无计数脚本里 `$null -eq 0` 为 False，**永不成立**（§6 第 66 条已记这个坑）。
Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue
