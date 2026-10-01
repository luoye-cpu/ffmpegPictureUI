# 多格式回归：验证 BuildColorArgsSplit（8-bit cICP HDR/广色域源如实声明）不破坏其它 ffmpeg-direct 格式
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
$ffprobe = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$val = "$root/tests/output/validate"
# ⚠ 运行级隔离（GUID）：固定 `$out` + 开头整目录删，会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
#   唯一目录没人替你清 ⇒ 结尾自清（照 `verify-gainmap-memory.ps1` 既有范式）。
$out = "$val/fmtreg_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force -Path $out | Out-Null

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$out/_exec.out"; $e = "$out/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}

# 只读 stdout 版（取「工具的值」专用）：ffprobe 的 `-of csv` 字段值走 **stdout**，合并 stderr 会把报错行
#   当值填进每格的 `trc=` 读数（本机实测 ffprobe 失败时 stdout 0 B / stderr 166 B 全是报错）。
#   产品日志不经 helper（走 `$out/<tag>.out` + Get-Content）⇒ 上面的合并版 Exec 在本文件已无调用点。
function ExecOut([string]$file, [string]$argStr){
  $o = "$out/_exec.out"; $e = "$out/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return [System.IO.File]::ReadAllText($o)
}

$pass = 0; $fail = 0
function Run-Case($srcName, $inFile, $fmt, $extra) {
    $tag = "$srcName-$fmt" + $(if ($extra.Count -gt 0) { "-tgt" } else { "-auto" })
    $dst = "$out/$tag"
    $argStr = "--headless -i `"$inFile`" --format $fmt --output `"$dst`""
    foreach ($e in $extra) { $argStr += " `"$e`"" }
    $so = "$out/$tag.out"; $se = "$out/$tag.err"
    $p = Start-Process -FilePath $exe -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
         -RedirectStandardOutput $so -RedirectStandardError $se
    $ext = $fmt; if ($fmt -eq "jpg") { $ext = "jpg" }
    $produced = Get-ChildItem -Recurse $dst -Include *.$fmt,*.jpg,*.jpeg,*.tif,*.tiff -ErrorAction SilentlyContinue | Select-Object -First 1
    $errTxt = (Get-Content $se -ErrorAction SilentlyContinue | Where-Object { $_ -ne "" }) -join " | "
    if ($null -ne $produced -and $produced.Length -gt 0) {
        $meta = (ExecOut $ffprobe "-v error -select_streams v:0 -show_entries `"stream=color_primaries,color_transfer`" -of csv=p=0 `"$($produced.FullName)`"") -split "`r?`n"
        Write-Host ("  PASS {0,-26} {1,7}B  trc={2}" -f $tag, $produced.Length, $meta) -ForegroundColor Green
        $script:pass++
    } else {
        Write-Host ("  FAIL {0,-26} exit={1} err={2}" -f $tag, $p.ExitCode, $errTxt) -ForegroundColor Red
        $script:fail++
    }
}

$hdr16 = "$val/src_hdr.png"; $hdr8 = "$val/src_hdr8.png"; $sdr = "$val/sdr_plain.png"
$formats = @("jpg", "png", "avif", "tiff", "gif")

Write-Host "`n### 16-bit HDR 源 (auto) ###" -ForegroundColor Cyan
foreach ($f in $formats) { Run-Case "hdr16" $hdr16 $f @() }
Write-Host "`n### 8-bit HDR 源 (auto) — Bug1 修复后新声明为 HDR ###" -ForegroundColor Cyan
foreach ($f in $formats) { Run-Case "hdr8" $hdr8 $f @() }
Write-Host "`n### 8-bit HDR 源 + sRGB 目标 (SDR-only 格式须 tonemap) ###" -ForegroundColor Cyan
foreach ($f in @("jpg", "png", "tiff", "gif")) { Run-Case "hdr8" $hdr8 $f @("--color-space", "sRGB") }
Write-Host "`n### SDR 源 (auto) 回归 ###" -ForegroundColor Cyan
foreach ($f in $formats) { Run-Case "sdr" $sdr $f @() }

Write-Host "`n===== PASS=$pass FAIL=$fail =====" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
# ⚠ 唯一目录自清（宿主批量删除守卫会拦整目录删 ⇒ 用 -EA SilentlyContinue，§6 第 57 条 ③）；失败则保留供排查
if ($fail -eq 0) { Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue }
# ⚠ **退出码语义**（2026-09-17 补，§6 第 58 条同类）：此前本脚本**全文无 `exit`** ⇒ 恒 `exit 0`；
#   而运行器/CI **只看退出码** ⇒ 它红了运行器也不知道（"恒绿门禁"）。
#   ⚠ 必须显式 `else { exit 0 }`：宿主删除守卫会污染 `$LASTEXITCODE`（实测变 2）⇒ 只写前半句会假红。
if ($fail -gt 0) { exit 1 } else { exit 0 }
