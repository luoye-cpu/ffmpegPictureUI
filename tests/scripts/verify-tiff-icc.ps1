# 指令2验证：TIFF + 显式目标色彩空间 → 应嵌入「目标」ICC（遵循 JXL 策略），不再丢失/被源ICC覆盖
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*\Debug\*') { " (Debug fallback)" } else { " (Release)" }))
$et = "$root/publish/PLAN/exiftool/exiftool.exe"
$val = "$root/tests/output/validate"
# ⚠ 运行级隔离（GUID）：固定 `$out` + 开头整目录删，会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
#   本脚本**无 pass/fail 计数**（诊断型）⇒ 结尾无条件自清。
$out = "$val/tifficc_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force -Path $out | Out-Null

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$out/_exec.out"; $e = "$out/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}

# 只读 stdout 版（取「工具的值」专用）：exiftool 的标签值与 ffprobe 的 `-of csv` 字段值都走 **stdout**。
#   随包 exiftool 是 perl 打包版 ⇒ 进程环境带着本机 perl 不认的 LC_ALL/LANG（MSYS 里起门禁 = C.UTF-8）时
#   每次调用先往 stderr 喷 locale warning，合并取数会把警告当值：本机实测同一张 DisplayP3 图
#   合并 288 B（值 + 6 行警告）/ 只读 43 B（只有 「Profile Description : DisplayP3」）⇒ 下面
#   的 `$desc` / `$cicp` 两格读数（人眼判「目标 ICC 到底写进去没有」的唯一证据）会被撑脏。
#   产品日志不经 helper（走 `$out/<tag>.o` / `.e` + 各自的 Start-Process）⇒ 上面的合并版 Exec 本文件已无调用点。
function ExecOut([string]$file, [string]$argStr){
  $o = "$out/_exec.out"; $e = "$out/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return [System.IO.File]::ReadAllText($o)
}

function T($tag, $inFile, $cs, $preserve) {
    $dst = "$out/$tag"
    $a = "--headless -i `"$inFile`" --format tiff --color-space `"$cs`" --output `"$dst`""
    if ($preserve) { $a += " --preserve-metadata" }
    Start-Sleep -Seconds 2
    Start-Process -FilePath $exe -ArgumentList $a -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput "$out/$tag.o" -RedirectStandardError "$out/$tag.e" | Out-Null
    $tif = Get-ChildItem -Recurse $dst -Include *.tif,*.tiff -EA SilentlyContinue | Select-Object -First 1
    if (-not $tif) { Write-Host ("  {0,-22} NO OUTPUT" -f $tag) -ForegroundColor Red; return }
    $desc = (ExecOut $et "-q -q -ICC_Profile:ProfileDescription `"$($tif.FullName)`"") -join ""
    # ⚠ 下一行与上面同形（ffprobe 的 `-of csv` 值走 stdout，还额外 `.Trim()`），只是扫描器按变量名点名、
    #   这里传的是字面路径 ⇒ 没进站点清单；口径一并改只读，否则同一格里两半取数标准不一致。
    $cicp = (ExecOut "$root/publish/PLAN/ffmpeg-full/ffprobe.exe" "-v error -select_streams v:0 -show_entries `"stream=color_primaries,color_transfer`" -of csv=p=0 `"$($tif.FullName)`"").Trim()
    Write-Host ("  {0,-22} {1,7}B  ICC=[{2}]  cicp={3}" -f $tag, $tif.Length, $desc, $cicp) -ForegroundColor Green
}

Write-Host "`n=== TIFF + 目标色彩空间（无 --preserve-metadata，默认模式）===" -ForegroundColor Cyan
T "p3-default"   "$val/sdr_plain.png" "Display P3" $false
T "srgb-default" "$val/sdr_plain.png" "sRGB"       $false
T "adobe-default" "$val/sdr_plain.png" "Adobe RGB" $false
T "2020pq-default" "$val/sdr_plain.png" "BT.2020 PQ" $false

Write-Host "`n=== TIFF + 目标色彩空间（--preserve-metadata，源无ICC 不应剥离目标ICC）===" -ForegroundColor Cyan
T "p3-preserve"  "$val/sdr_plain.png" "Display P3" $true
T "2020-preserve" "$val/sdr_plain.png" "BT.2020"   $true

Write-Host "`n>> 期望: p3→'Display P3'类描述; srgb→sRGB; adobe/2020→BT.2020(超广归一); 2020pq→BT.2020 SDR(TIFF钳SDR)" -ForegroundColor Yellow
Write-Host "===== DONE =====" -ForegroundColor Cyan
# ⚠ 唯一目录自清（宿主批量删除守卫会拦整目录删 ⇒ 用 -EA SilentlyContinue，§6 第 57 条 ③）
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
