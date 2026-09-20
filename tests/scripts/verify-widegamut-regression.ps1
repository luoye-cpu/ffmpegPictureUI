# 广色域回归：Display P3(SDR smpte432) 与 P3-PQ(HDR smpte432/smpte2084) 源 → 各格式
# 验证 Fix#1 的 cicpWide(smpte432/smpte431) 分支 + P0-A/B + Bug2 在广色域源下无回归
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
$ffprobe = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$val = "$root/tests/output/validate"
$col = "$root/tests/output/results/color"
# ⚠ 运行级隔离（GUID）：固定 `$out` + 开头整目录删，会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
#   唯一目录没人替你清 ⇒ 结尾自清（照 `verify-gainmap-memory.ps1` 既有范式）。
$out = "$val/widereg_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force -Path $out | Out-Null

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$out/_exec.out"; $e = "$out/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}

$pass = 0; $fail = 0
function Run-Case($tag, $inFile, $fmt, $extra) {
    $dst = "$out/$tag"
    $argStr = "--headless -i `"$inFile`" --format $fmt --output `"$dst`""
    foreach ($e in $extra) { $argStr += " `"$e`"" }
    $so = "$out/$tag.out"; $se = "$out/$tag.err"
    $p = Start-Process -FilePath $exe -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
         -RedirectStandardOutput $so -RedirectStandardError $se
    $produced = Get-ChildItem -Recurse $dst -Include *.$fmt,*.jpg,*.jpeg,*.tif,*.tiff -ErrorAction SilentlyContinue | Select-Object -First 1
    $q = (Get-Content $so -ErrorAction SilentlyContinue | Select-String "队列处理").Line
    if ($null -ne $produced -and $produced.Length -gt 0) {
        $meta = (Exec $ffprobe "-v error -select_streams v:0 -show_entries `"stream=color_primaries,color_transfer`" -of csv=p=0 `"$($produced.FullName)`"") -split "`r?`n"
        Write-Host ("  PASS {0,-24} {1,7}B  cicp={2}" -f $tag, $produced.Length, $meta) -ForegroundColor Green
        $script:pass++
    } else {
        $errTxt = (Get-Content $se -ErrorAction SilentlyContinue | Where-Object { $_ -ne "" }) -join " | "
        Write-Host ("  FAIL {0,-24} [{1}] {2}" -f $tag, $q, $errTxt) -ForegroundColor Red
        $script:fail++
    }
}

$p3 = "$col/a2_srgb_p3.png"; $p3pq = "$col/a3_p3pq.png"

Write-Host "`n### Display P3 SDR (smpte432/iec61966-2-1) → auto ###" -ForegroundColor Cyan
foreach ($f in @("jpg","png","webp","avif","gif","tiff")) { Run-Case "p3-$f-auto" $p3 $f @() }
Write-Host "`n### Display P3 SDR → 显式 Display P3 目标（须保广色域, 不回落 sRGB）###" -ForegroundColor Cyan
foreach ($f in @("png","avif","tiff")) { Run-Case "p3-$f-p3tgt" $p3 $f @("--color-space","Display P3") }
Write-Host "`n### Display P3 SDR → sRGB 目标（SDR-only 格式须色域转换）###" -ForegroundColor Cyan
foreach ($f in @("jpg","webp")) { Run-Case "p3-$f-srgb" $p3 $f @("--color-space","sRGB") }
Write-Host "`n### P3-PQ HDR (smpte432/smpte2084) → auto ###" -ForegroundColor Cyan
foreach ($f in @("jpg","png","webp","avif")) { Run-Case "p3pq-$f-auto" $p3pq $f @() }
Write-Host "`n### P3-PQ HDR → BT.2020 PQ 目标 ###" -ForegroundColor Cyan
foreach ($f in @("png","avif","webp")) { Run-Case "p3pq-$f-pq" $p3pq $f @("--color-space","BT.2020 PQ") }

Write-Host "`n===== PASS=$pass FAIL=$fail =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
# ⚠ 唯一目录自清（宿主批量删除守卫会拦整目录删 ⇒ 用 -EA SilentlyContinue，§6 第 57 条 ③）；失败则保留供排查
if ($fail -eq 0) { Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue }
# ⚠ **退出码语义**（2026-09-17 补，§6 第 58 条同类）：此前本脚本**全文无 `exit`** ⇒ 恒 `exit 0`；
#   而运行器/CI **只看退出码** ⇒ 它红了运行器也不知道（"恒绿门禁"）。
#   ⚠ 必须显式 `else { exit 0 }`：宿主删除守卫会污染 `$LASTEXITCODE`（实测变 2）⇒ 只写前半句会假红。
if ($fail -gt 0) { exit 1 } else { exit 0 }
