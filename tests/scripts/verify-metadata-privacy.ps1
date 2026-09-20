# verify-metadata-privacy.ps1 —— 元数据隐私门禁（2026-09-15 新增）
# 背景：`FfmpegOptions.StripExifGps` 默认 **true** ⇒「默认剥掉 GPS」是面向用户的**隐私承诺**，
#   但此前**没有任何自动化覆盖**（既存门禁只查色彩标签，没人查 GPS 是否真的被剥掉）。
#   同一段后置链还负责「把源文件的描述性元数据复制回来」，两者都会**静默失效**：
#   不报错、退出码 0、日志照样写"隐私清理完成" ⇒ 只能靠断言守护。
# 断言：
#   ① 默认转换：输出**无 GPS**（隐私承诺生效）；
#   ② 默认转换：输出**仍有**描述性元数据（ImageDescription / Make）⇒ 证明不是"把元数据全剥了"；
#   ③ **负控**：`--no-strip-gps` 时输出**有 GPS** ⇒ 证明 ① 的断言能识别 GPS 的有无（非空跑）。
# ⚠ 调外部 exe 一律 Start-Process + 单个参数字符串（见 HANDOVER §C.5 的坑）。
# 用法：pwsh -NoProfile -File tests/scripts/verify-metadata-privacy.ps1
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$et = "$root/publish/PLAN/exiftool/exiftool.exe"
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
$val = "$root/tests/output/metapriv_$([guid]::NewGuid().ToString('N').Substring(0, 8))"

$pass = 0; $fail = 0
function Say($t) { Write-Output $t }
# 所有子进程输出都重定向到工作目录：门禁自己的 stdout 只留 PASS/FAIL
function Run-Exe([string]$file, [string]$argStr, [string]$tag = 'run') {
    Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput "$val/$tag.out.txt" -RedirectStandardError "$val/$tag.err.txt" | Out-Null
}
# exiftool 取值（-s3 = 只输出值），每次都重定向，避免子进程输出混进门禁输出
function Et([string]$argStr) {
    $o = "$val/et.out.txt"
    Start-Process -FilePath $et -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput $o -RedirectStandardError "$val/et.err.txt" | Out-Null
    return ([System.IO.File]::ReadAllText($o)).Trim()
}

if (-not (Test-Path $exe)) { Say "SKIP FfmpegGui.exe 未构建（先 dotnet build）"; exit 0 }
if (-not (Test-Path $et)) { Say "SKIP 缺 exiftool（publish/PLAN/exiftool）"; exit 0 }

New-Item -ItemType Directory -Path $val -Force | Out-Null

# ── 素材：带 GPS + 描述性元数据的 JPEG ──
$src = "$val/src.jpg"
Run-Exe $ff "-y -v error -f lavfi -i `"testsrc=size=320x200:rate=1`" -frames:v 1 `"$src`"" 'ff-src'
Run-Exe $et "-overwrite_original -GPSLatitude=39.9042 -GPSLongitude=116.4074 -ImageDescription=HELLO-META -Make=TestCam -Model=TestModel `"$src`"" 'et-write'
$gpsIn = Et "-s3 -GPSLatitude `"$src`""
if ([string]::IsNullOrWhiteSpace($gpsIn)) { Say "SKIP 素材写入 GPS 失败（exiftool 未生效）"; exit 0 }
Say "素材已带 GPS=$gpsIn"

# ── ① 默认：应剥掉 GPS；② 描述性元数据应仍在 ──
$d1 = "$val/default"; New-Item -ItemType Directory -Path $d1 -Force | Out-Null
Run-Exe $exe "--headless -i `"$src`" --format jpg --output `"$d1`" --log-file `"$d1/log.txt`"" 'app-default'
$o1 = Get-ChildItem "$d1/*" -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in @('.jpg', '.jpeg') } | Select-Object -First 1
if (-not $o1) { $fail++; Say "FAIL 默认转换未产出 jpg" }
else {
    $g = Et "-s3 -GPSLatitude `"$($o1.FullName)`""
    if ([string]::IsNullOrWhiteSpace($g)) { $pass++; Say "PASS ① 默认输出无 GPS（隐私承诺生效）" }
    else { $fail++; Say "FAIL ① 默认输出仍有 GPS=「$g」⇒ 隐私承诺失效（StripExifGps 默认应为 true）" }

    $desc = Et "-s3 -ImageDescription `"$($o1.FullName)`""
    $make = Et "-s3 -Make `"$($o1.FullName)`""
    if ($desc -match 'HELLO-META' -and $make -match 'TestCam') {
        $pass++; Say "PASS ② 描述性元数据仍被复制（ImageDescription=$desc, Make=$make）"
    }
    else {
        $fail++; Say "FAIL ② 描述性元数据丢了（ImageDescription=「$desc」Make=「$make」）⇒ 元数据复制静默失效"
    }
}

# ── ③ 负控：--no-strip-gps ⇒ GPS 应保留 ──
$d2 = "$val/nostrip"; New-Item -ItemType Directory -Path $d2 -Force | Out-Null
Run-Exe $exe "--headless -i `"$src`" --format jpg --output `"$d2`" --no-strip-gps --log-file `"$d2/log.txt`"" 'app-nostrip'
$o2 = Get-ChildItem "$d2/*" -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in @('.jpg', '.jpeg') } | Select-Object -First 1
if (-not $o2) { $fail++; Say "FAIL 负控转换未产出 jpg" }
else {
    $g2 = Et "-s3 -GPSLatitude `"$($o2.FullName)`""
    if (-not [string]::IsNullOrWhiteSpace($g2)) {
        $pass++; Say "PASS ③ 负控 --no-strip-gps 时 GPS 保留（$g2）⇒ ① 的断言能识别 GPS 有无，非空跑"
    }
    else {
        $fail++; Say "FAIL ③ 负控 --no-strip-gps 时 GPS 仍被剥掉 ⇒ ① 的断言无法区分 GPS 有无，属空跑"
    }
}

# ── ④ 完整复制路径（protectColorMetadata=false ⇒ CopyMetadataAsync）的隐私吸收**未纳入本门禁** ──
#    原因：该分支只在 backend ∈ {Cjpegli, Dng} 时走到，而实测可达的两条路都不好用：
#      · `--format jpegli` 目前是**坏的**（既有 bug，与本次改动无关）：产物扩展名被写成 `.jpegli`，
#        ffmpeg 报 `Unable to choose an output format for '…src.jpegli'`，转换直接失败；
#      · `--format dng` 走 raw 专用链路，元数据由 dngtool/exiftool 另行处理。
#    ⇒ 该分支的「并入隐私清理」目前只有**代码对称性**保证，没有运行期断言。已在 HANDOVER §D 记为开放项。

Say "`n===== PASS=$pass FAIL=$fail ====="
if ($fail -eq 0) { Remove-Item -Recurse -Force $val -ErrorAction SilentlyContinue }
else { Say "工作目录保留供排查: $val" }
exit $(if ($fail -eq 0) { 0 } else { 1 })
