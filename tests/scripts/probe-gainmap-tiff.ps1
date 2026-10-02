# 指令3(GainMap/UltraHDR) + 指令2(TIFF ICC) 真实工具链取证
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
# ⚠ 2026-10-01 修死路径：这里原先只查 `$root/publish/PLAN/artifacts/ultrahdr_app.exe` —— 实测该目录
#   只有 JxrDecApp / JxrEncApp / avifenc / dngtool **四件**，ultrahdr_app 从来不在里面 ⇒
#   两处 `Start-Process -FilePath $uhdr` 直接报"系统找不到指定的文件"，而本探针是诊断型
#   （`$ErrorActionPreference = Continue`、无 pass/fail 计数）⇒ 那份取证输出**静默缺了整段第三方读数**。
#   权威位置 = 本机构建的 libultrahdr 示例程序（与 `verify-gainmap-thirdparty.ps1` 同一把尺）。
$uhdr = "$root/publish/PLAN/artifacts/ultrahdr_app.exe"
if (-not (Test-Path $uhdr)) { $uhdr = "$root/tools/src/libultrahdr/build/Release/ultrahdr_app.exe" }
if (-not (Test-Path $uhdr)) {
  Write-Output "[gate] ultrahdr_app 两处都不在位 ⇒ 本探针的第三方段**无读数**（不等于「产物没有增益图」）：$uhdr"
  Write-Output "[gate] 修法：构建 tools/src/libultrahdr（CMake + MSVC），产物在 build/Release/ultrahdr_app.exe"
  $uhdr = $null
}
$et = "$root/publish/PLAN/exiftool/exiftool.exe"
$fp = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$val = "$root/tests/output/validate"
# ⚠ 运行级隔离（GUID）：固定 `$out` + 开头整目录删，会被**并发的同名实例**互删 ⇒ 假红（§6 第 66 条）。
#   本脚本**无 pass/fail 计数**（诊断型）⇒ 结尾无条件自清。
$out = "$val/uhdrfix_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force -Path $out | Out-Null

# 取子进程 stdout 一律走 Start-Process（`& exe` 在部分宿主静默失败：输出为空 ⇒ 假红/空值）
function Exec([string]$file, [string]$argStr){
  $o = "$out/_exec.out"; $e = "$out/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}

# 只读 stdout 的取数：随包 exiftool 是 perl 打包版，环境带本机 perl 不认的 LC_ALL/LANG 时
#   每次调用都往 stderr 喷 locale 警告（实测 244 字节 6 行）⇒ 读「工具写在 stdout 的单个值」
#   必须只取 stdout，否则警告并进值里，取证打印会被撑成看不懂的形态。
#   本脚本无 pass/fail 计数（诊断型），但下面两处是把单个值当结论读的，仍按只对口径取数。
function ExecOut([string]$file, [string]$argStr){
  $o = "$out/_exec.out"; $e = "$out/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return [System.IO.File]::ReadAllText($o)
}

function App($argStr, $tag) {
    $so = "$out/$tag.out"; $se = "$out/$tag.err"
    Start-Sleep -Seconds 2
    $p = Start-Process -FilePath $exe -ArgumentList $argStr -Wait -NoNewWindow -PassThru -RedirectStandardOutput $so -RedirectStandardError $se
    return (Get-Content $so -ErrorAction SilentlyContinue | Select-String "队列处理").Line
}

Write-Host "===== ultrahdr_app usage (参考工具) =====" -ForegroundColor Cyan
# ⚡ 两处 ultrahdr 调用**都要先证工具在位**：本探针的既有用法（第 10 行那条死路径）就是"读数缺席但没人
#   知道是没跑还是没产物" ⇒ 不判红（诊断型脚本无 pass/fail 契约），但把"没跑"写成一句可 grep 的留痕，
#   别让它伪装成"跑了且无输出"。
if ($uhdr) {
  $u = Start-Process -FilePath $uhdr -ArgumentList "" -Wait -NoNewWindow -PassThru -RedirectStandardOutput "$out/uhdr_usage.out" -RedirectStandardError "$out/uhdr_usage.err"
  Get-Content "$out/uhdr_usage.out","$out/uhdr_usage.err" -ErrorAction SilentlyContinue | Select-Object -First 25
} else {
  Write-Host "  [usage] ultrahdr_app 不在位 ⇒ 无 usage 读数（不在这里代写用法，免得把没跑的说成跑过的）"
}

Write-Host "`n===== 指令3: app 编码 Ultra HDR JPEG (src_hdr.png, 1000nits) =====" -ForegroundColor Cyan
$q = App "--headless -i `"$val/src_hdr.png`" --format jpg --jpeg-gain-map true --jpeg-gain-map-target-nits 1000 --output `"$out/gm`"" "gm_enc"
Write-Host "  $q"
$gm = Get-ChildItem -Recurse "$out/gm" -Filter *.jpg -ErrorAction SilentlyContinue | Select-Object -First 1
if ($gm) {
    Write-Host "  [SIZE] $($gm.Length) bytes -> $($gm.FullName)" -ForegroundColor Green
    Write-Host "  --- exiftool 元数据 (hdrgm XMP / MPF / ISO) ---"
    (Exec $et "-G -a `"$($gm.FullName)`"") -split "`r?`n" | Select-String -Pattern "hdr|Gain|MPF|MPImage|ISO|Container|Primary" | Select-Object -First 30  # 合并取数已论证：全量标签转储按行喂 Select-String 供人读，不喂断言；保留 stderr 恰好让 exiftool 自身的报错也出现在取证输出里
    Write-Host "  --- ultrahdr_app 解码验证 (mode 探测) ---"
    if (-not $uhdr) { Write-Host "    [SKIP] ultrahdr_app 不在位 ⇒ 下面两行 mode 读数**没有**（不是解码失败）" -ForegroundColor Yellow }
    foreach ($m in $(if ($uhdr) { 1,2 } else { @() })) {
        $uo = Start-Process -FilePath $uhdr -ArgumentList "-m $m -i `"$($gm.FullName)`" -o `"$out/uhdr_dec_m$m.jpg`"" -Wait -NoNewWindow -PassThru -RedirectStandardOutput "$out/uhdr_dec_m$m.out" -RedirectStandardError "$out/uhdr_dec_m$m.err"
        $dec = Get-ChildItem "$out/uhdr_dec_m$m.jpg" -ErrorAction SilentlyContinue
        Write-Host "    mode=$m exit=$($uo.ExitCode) out=$(if($dec){$dec.Length}else{'none'})B  err=$((Get-Content "$out/uhdr_dec_m$m.err" -EA SilentlyContinue | Select-Object -First 1))"
    }
} else { Write-Host "  [FAIL] no gain map jpeg produced" -ForegroundColor Red }

Write-Host "`n===== 指令2: TIFF + 显式 Display P3 目标 → 检查嵌入 ICC 是否为目标(P3)还是源(sRGB) =====" -ForegroundColor Cyan
# 源: 普通 sRGB PNG; 目标: Display P3 TIFF。像素应转到 P3, ICC 应为 P3(遵循 JXL 策略), 而非源 sRGB
$q2 = App "--headless -i `"$val/sdr_plain.png`" --format tiff --color-space `"Display P3`" --preserve-metadata --output `"$out/tiffp3`"" "tiff_p3"
Write-Host "  $q2"
$tif = Get-ChildItem -Recurse "$out/tiffp3" -Include *.tif,*.tiff -ErrorAction SilentlyContinue | Select-Object -First 1
if ($tif) {
    Write-Host "  [SIZE] $($tif.Length) bytes"
    $iccDesc = (ExecOut $et "-ICC_Profile:ProfileDescription -ICC_Profile:ProfileClass -b `"$($tif.FullName)`"") -split "`r?`n"
    Write-Host "  [嵌入ICC ProfileDescription] $iccDesc"
    $cicp = (ExecOut $fp "-v error -select_streams v:0 -show_entries `"stream=color_primaries,color_transfer`" -of csv=p=0 `"$($tif.FullName)`"") -split "`r?`n"
    Write-Host "  [CICP] $cicp"
    Write-Host "  >> 期望(遵循JXL): ICC=Display P3;  若=sRGB 则确认源ICC覆盖目标ICC的Bug" -ForegroundColor Yellow
} else { Write-Host "  [FAIL] no tiff produced" -ForegroundColor Red }

Write-Host "`n===== DONE =====" -ForegroundColor Cyan
# ⚠ 唯一目录自清（宿主批量删除守卫会拦整目录删 ⇒ 用 -EA SilentlyContinue，§6 第 57 条 ③）
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
