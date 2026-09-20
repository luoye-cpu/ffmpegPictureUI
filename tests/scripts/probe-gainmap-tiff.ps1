# 指令3(GainMap/UltraHDR) + 指令2(TIFF ICC) 真实工具链取证
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
$uhdr = "$root/publish/PLAN/artifacts/ultrahdr_app.exe"
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

function App($argStr, $tag) {
    $so = "$out/$tag.out"; $se = "$out/$tag.err"
    Start-Sleep -Seconds 2
    $p = Start-Process -FilePath $exe -ArgumentList $argStr -Wait -NoNewWindow -PassThru -RedirectStandardOutput $so -RedirectStandardError $se
    return (Get-Content $so -ErrorAction SilentlyContinue | Select-String "队列处理").Line
}

Write-Host "===== ultrahdr_app usage (参考工具) =====" -ForegroundColor Cyan
$u = Start-Process -FilePath $uhdr -ArgumentList "" -Wait -NoNewWindow -PassThru -RedirectStandardOutput "$out/uhdr_usage.out" -RedirectStandardError "$out/uhdr_usage.err"
Get-Content "$out/uhdr_usage.out","$out/uhdr_usage.err" -ErrorAction SilentlyContinue | Select-Object -First 25

Write-Host "`n===== 指令3: app 编码 Ultra HDR JPEG (src_hdr.png, 1000nits) =====" -ForegroundColor Cyan
$q = App "--headless -i `"$val/src_hdr.png`" --format jpg --jpeg-gain-map true --jpeg-gain-map-target-nits 1000 --output `"$out/gm`"" "gm_enc"
Write-Host "  $q"
$gm = Get-ChildItem -Recurse "$out/gm" -Filter *.jpg -ErrorAction SilentlyContinue | Select-Object -First 1
if ($gm) {
    Write-Host "  [SIZE] $($gm.Length) bytes -> $($gm.FullName)" -ForegroundColor Green
    Write-Host "  --- exiftool 元数据 (hdrgm XMP / MPF / ISO) ---"
    (Exec $et "-G -a `"$($gm.FullName)`"") -split "`r?`n" | Select-String -Pattern "hdr|Gain|MPF|MPImage|ISO|Container|Primary" | Select-Object -First 30
    Write-Host "  --- ultrahdr_app 解码验证 (mode 探测) ---"
    foreach ($m in 1,2) {
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
    $iccDesc = (Exec $et "-ICC_Profile:ProfileDescription -ICC_Profile:ProfileClass -b `"$($tif.FullName)`"") -split "`r?`n"
    Write-Host "  [嵌入ICC ProfileDescription] $iccDesc"
    $cicp = (Exec $fp "-v error -select_streams v:0 -show_entries `"stream=color_primaries,color_transfer`" -of csv=p=0 `"$($tif.FullName)`"") -split "`r?`n"
    Write-Host "  [CICP] $cicp"
    Write-Host "  >> 期望(遵循JXL): ICC=Display P3;  若=sRGB 则确认源ICC覆盖目标ICC的Bug" -ForegroundColor Yellow
} else { Write-Host "  [FAIL] no tiff produced" -ForegroundColor Red }

Write-Host "`n===== DONE =====" -ForegroundColor Cyan
# ⚠ 唯一目录自清（宿主批量删除守卫会拦整目录删 ⇒ 用 -EA SilentlyContinue，§6 第 57 条 ③）
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
