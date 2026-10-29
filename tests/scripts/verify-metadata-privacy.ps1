# verify-metadata-privacy.ps1 —— 元数据隐私门禁（2026-09-15 新增）
# 政策口径（2026-10-20 用户裁定统一）：**默认不勾选 = 全保留**，剥除只在用户显式要求时发生。
#   背景：本闸 2026-09-15 立项时 `StripExifGps` 的默认值是 true（"隐私默认"），
#   而 GUI 的复选框初值从来没勾 ⇒ 两个真值互相矛盾（CLI/模型/预设剥、界面不剥）。现四处默认统一成 false，
#   于是这个闸的两条默认断言随之翻向：默认**保留** GPS，显式 `--strip-gps` 才**必须剥**。
#   同一段后置链还负责「把源文件的描述性元数据复制回来」，两者都会**静默失效**：
#   不报错、退出码 0、日志照样写"隐私清理完成" ⇒ 只能靠断言守护。
# 断言：
#   ① 默认转换：输出**仍有 GPS**（默认全保留这条政策生效）；
#   ② 默认转换：输出**仍有**描述性元数据（ImageDescription / Make）⇒ 证明 ① 不是因为"整条后置没跑"；
#   ③ **反向活格子**：`--strip-gps` 时输出**无 GPS** ⇒ ① 与 ③ 同尺两向分叉，谁把默认改回"剥"
#      或把显式剥除改成空转，都会当场红一条（只有一条断言时无法区分这两种坏法）。
#   ④ `--strip-metadata` + **png** ⇒ 产物无 GPS、无描述性元数据（2026-09-21 新增：
#      png/apng 的 ffmpeg 侧用 `-map_metadata 0` 保 CICP，会绕过隐私删除）；
#   ⑤ **负控**：`--preserve-metadata` + png ⇒ 产物**有 GPS** ⇒ 证明 ④ 非空跑。
#   ⑥ RAW→PNG 的拍摄字段（`MeteringMode`/`DateTimeOriginal`）必须落盘 —— 同时锁「补回生效」
#      与「PNG 的 exiftool **写入通道**通」（ffmpeg 的 `eXIf` 里带 TIFF 专用 `StripOffsets`，
#      不做复位时 exiftool 以退出码 1 放弃写入）；
#   ⑦ **负控**：RAW→PNG + `--strip-metadata` ⇒ 无拍摄字段 ⇒ 证明 ⑥ 非空跑。
#      ⚠ ⑥⑦ 需要真 RAW 素材（该缺陷只在「源是 dngtool 中间 TIFF」时触发）；
#        素材缺失（未受版本控制）时**点名 SKIP、不给绿灯**。
#   ⑧ 复制元数据的**宣称与现实**（2026-10-29 B5）：非 Safe 变体（`--encoder Cjpegli` 走的那条）
#      对 HD Photo 源报「0 image files updated」而退出码 0，日志却已写「复制元数据」⇒ 产物什么都不带。
#      ⑧a 无元数据源必须点名「0 项落地」；⑧b 有元数据源必须落地且不点名（正控）；
#      ⑧c/⑧d 锁命令行形态与"总结行只有一份实现"。功能半边的实机读数见本节注释与 ledger。
# ⚠ 调外部 exe 一律 Start-Process + 单个参数字符串（见 HANDOVER §C.5 的坑）。
# 用法：pwsh -NoProfile -File tests/scripts/verify-metadata-privacy.ps1
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$et = "$root/publish/PLAN/exiftool/exiftool.exe"
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
$val = "$root/tests/output/metapriv_$([guid]::NewGuid().ToString('N').Substring(0, 8))"

$pass = 0; $fail = 0; $skip = 0
function Say($t) { Write-Output $t }
# ⚠ 2026-09-21（`TESTING.md` 第 81 条同族 · 口径统一）：**整门禁跳过也打印汇总并计数**。
#   原先三条前置守卫直接 `exit 0`、**不打印汇总** ⇒ 门禁在「有汇总」与「无汇总」两种形态间摇摆，
#   读者/运行器无法用同一口径判读（本仓第 62 条要求无汇总的一律**单列**）。
function SkipAll([string]$why) {
    $script:skip++
    Write-Output "SKIP $why"
    Write-Output "===== PASS=0 FAIL=0 SKIP=$skip（⚠ 整门禁未运行，不是通过）====="
    exit 0
}
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
# ⑧ 起用的紧凑判据（其余各档维持原有 if/else 写法，不回头改历史读数）
function CK($ok, $msg) { if ($ok) { $script:pass++; Say "PASS $msg" } else { $script:fail++; Say "FAIL $msg" } }

if (-not (Test-Path $exe)) { SkipAll "FfmpegGui.exe 未构建（先 dotnet build）" }
if (-not (Test-Path $et)) { SkipAll "缺 exiftool（publish/PLAN/exiftool）" }

New-Item -ItemType Directory -Path $val -Force | Out-Null

# ── 素材：带 GPS + 描述性元数据的 JPEG ──
$src = "$val/src.jpg"
Run-Exe $ff "-y -v error -f lavfi -i `"testsrc=size=320x200:rate=1`" -frames:v 1 `"$src`"" 'ff-src'
Run-Exe $et "-overwrite_original -GPSLatitude=39.9042 -GPSLongitude=116.4074 -ImageDescription=HELLO-META -Make=TestCam -Model=TestModel `"$src`"" 'et-write'
$gpsIn = Et "-s3 -GPSLatitude `"$src`""
if ([string]::IsNullOrWhiteSpace($gpsIn)) { SkipAll "素材写入 GPS 失败（exiftool 未生效）" }
Say "素材已带 GPS=$gpsIn"

# ── ① 默认：应保留 GPS（2026-10-20 政策）；② 描述性元数据应仍在 ──
$d1 = "$val/default"; New-Item -ItemType Directory -Path $d1 -Force | Out-Null
Run-Exe $exe "--headless -i `"$src`" --format jpg --output `"$d1`" --log-file `"$d1/log.txt`"" 'app-default'
$o1 = Get-ChildItem "$d1/*" -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in @('.jpg', '.jpeg') } | Select-Object -First 1
if (-not $o1) { $fail++; Say "FAIL 默认转换未产出 jpg" }
else {
    $g = Et "-s3 -GPSLatitude `"$($o1.FullName)`""
    if (-not [string]::IsNullOrWhiteSpace($g)) { $pass++; Say "PASS ① 默认输出保留 GPS=「$g」（政策=默认不勾选全保留）" }
    else { $fail++; Say "FAIL ① 默认输出 GPS 被剥掉了 ⇒ 默认值又被改回 true（或后置的显式删除在无请求时触发）" }

    $desc = Et "-s3 -ImageDescription `"$($o1.FullName)`""
    $make = Et "-s3 -Make `"$($o1.FullName)`""
    if ($desc -match 'HELLO-META' -and $make -match 'TestCam') {
        $pass++; Say "PASS ② 描述性元数据仍被复制（ImageDescription=$desc, Make=$make）"
    }
    else {
        $fail++; Say "FAIL ② 描述性元数据丢了（ImageDescription=「$desc」Make=「$make」）⇒ 元数据复制静默失效"
    }
}

# ── ③ 反向活格子：--strip-gps（显式请求）⇒ GPS 必须被剥掉 ──
$d2 = "$val/strip"; New-Item -ItemType Directory -Path $d2 -Force | Out-Null
Run-Exe $exe "--headless -i `"$src`" --format jpg --output `"$d2`" --strip-gps --log-file `"$d2/log.txt`"" 'app-strip'
$o2 = Get-ChildItem "$d2/*" -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in @('.jpg', '.jpeg') } | Select-Object -First 1
if (-not $o2) { $fail++; Say "FAIL ③ 显式 --strip-gps 转换未产出 jpg" }
else {
    $g2 = Et "-s3 -GPSLatitude `"$($o2.FullName)`""
    if ([string]::IsNullOrWhiteSpace($g2)) {
        $pass++; Say "PASS ③ 显式 --strip-gps 时 GPS 确被剥掉（读回=[$g2]）⇒ 与 ① 同尺两向分叉，两条都不是空跑"
    }
    else {
        $fail++; Say "FAIL ③ 显式 --strip-gps 时 GPS 仍在（$g2）⇒ 用户明确要求剥除却静默保留（2026-10-05 那条 P0 的形状复发）"
    }
}

# ── ④ PNG 输出的隐私剥离（2026-09-21 新增）──
# 背景：ffmpeg 为保住 png/apng 的 `cICP`/`cHRM`/`gAMA` 色彩 chunk，对这两种格式用
#   `-map_metadata 0`（而非 `-map_metadata -1`，见 FfmpegCommandBuilder 的 isCicpFormat 分支）
#   ⇒ **源 EXIF（含 GPS）会被一并带进产物**；而 `StripAll` 原先在 RestoreMetadataAsync 里
#   **直接 return** ⇒ 既不复制、也不清理 ⇒ **隐私删除被静默绕过**。
#   实测（2026-09-21）：`--strip-metadata` 下 jpg / webp / tiff 产物 GPS **均无**，**png / apng 却带 GPS**。
#   ⚠ 该路径还需先清掉 ffmpeg 写坏的 `eXIf` 块（IFD0 里带 TIFF 专用的 `StripOffsets`，
#     exiftool 读取即报 `Error reading StripOffsets data in IFD0` 并以退出码 1 放弃写入）。
# 断言：
#   ④ `--strip-metadata` + png ⇒ 产物**无 GPS 且无描述性元数据**（剥离真的生效）；
#   ⑤ **负控** `--preserve-metadata` + png ⇒ 产物**有 GPS** ⇒ 证明 ④ 不是空跑。
$d3 = "$val/png-strip"; New-Item -ItemType Directory -Path $d3 -Force | Out-Null
Run-Exe $exe "--headless -i `"$src`" --format png --output `"$d3`" --strip-metadata --log-file `"$d3/log.txt`"" 'app-png-strip'
$o3 = Get-ChildItem "$d3/*" -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -eq '.png' } | Select-Object -First 1
if (-not $o3) { $fail++; Say "FAIL ④ PNG --strip-metadata 未产出 png" }
else {
    $g3 = Et "-s3 -GPSLatitude `"$($o3.FullName)`""
    $desc3 = Et "-s3 -ImageDescription `"$($o3.FullName)`""
    if ([string]::IsNullOrWhiteSpace($g3) -and [string]::IsNullOrWhiteSpace($desc3)) {
        $pass++; Say "PASS ④ PNG + --strip-metadata 产物无 GPS、无描述性元数据（剥离生效）"
    }
    else {
        $fail++; Say "FAIL ④ PNG + --strip-metadata 产物仍有元数据（GPS=「$g3」ImageDescription=「$desc3」）⇒ ffmpeg 的 -map_metadata 0 绕过了隐私删除"
    }
}

$d4 = "$val/png-keep"; New-Item -ItemType Directory -Path $d4 -Force | Out-Null
Run-Exe $exe "--headless -i `"$src`" --format png --output `"$d4`" --preserve-metadata --log-file `"$d4/log.txt`"" 'app-png-keep'
$o4 = Get-ChildItem "$d4/*" -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -eq '.png' } | Select-Object -First 1
if (-not $o4) { $fail++; Say "FAIL ⑤ 负控 PNG --preserve-metadata 未产出 png" }
else {
    $g4 = Et "-s3 -GPSLatitude `"$($o4.FullName)`""
    if (-not [string]::IsNullOrWhiteSpace($g4)) {
        $pass++; Say "PASS ⑤ 负控 PNG + --preserve-metadata 时 GPS 保留（$g4）⇒ ④ 的断言能识别 GPS 有无，非空跑"
    }
    else {
        $fail++; Say "FAIL ⑤ 负控 PNG + --preserve-metadata 时 GPS 仍被剥掉 ⇒ ④ 的断言无法区分 GPS 有无，属空跑"
    }
}

# ── ⑥ RAW 输入 → PNG：拍摄字段必须真的落进产物（2026-09-21 新增）──
# 这一条**同时锁两件事**：
#   ① 「RAW 输入的拍摄字段补回」在 **PNG** 出口也生效 —— `MeteringMode` / `DateTimeOriginal`
#      **只可能由补漏写入**（中间 TIFF 只带 ISO/ExposureTime/FNumber/FocalLength，ffmpeg 也不带它们）；
#   ② PNG 的 **exiftool 写入通道真的通** —— ffmpeg 写出的 `eXIf` 里 IFD0 **照搬了 TIFF 专用的
#      `StripOffsets`**（小端字节 `11 01`），exiftool 读取即报
#      `Error reading StripOffsets data in IFD0` 并**以退出码 1 放弃写入**
#      ⇒ 不做 `-EXIF:all=` 复位时这两项**必缺**。
#   ⚠⚠ 实测该缺陷**只在「源是 dngtool 的中间 TIFF」时触发** —— ffmpeg 自己产出的 TIFF 走同一条路
#     **不复现**（本门禁第一版拿合成 TIFF/JPEG 当源，写成了**空跑**：变异后仍 PASS）。
#     ⇒ 判据**必须用真 RAW**，不可用合成素材替代。
#   ⚠ 素材未受版本控制（`tests/output/**` 被 `.gitignore`）⇒ 缺失时**点名 SKIP、不给绿灯**。
$rawSrc = $null
foreach ($d in @("$root/tests/output/raw_samples", "$root/tests/output/raw_samples2")) {
    $cands = Get-ChildItem $d -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in @('.cr2', '.cr3', '.nef', '.arw', '.pef', '.srw', '.iiq', '.orf', '.rw2', '.mrw', '.rwl', '.3fr') } |
        Sort-Object Length
    foreach ($c in $cands) {
        # ⚠ 必须挑**源本身带 DateTimeOriginal** 的样本：扫描仪 NEF 之类没有该字段，
        #   拿它当素材会让 ⑥ 变成「源就没有 ⇒ 产物没有」的**假红**。
        $probe = Et "-s3 -DateTimeOriginal `"$($c.FullName)`""
        if (-not [string]::IsNullOrWhiteSpace($probe)) { $rawSrc = $c; break }
    }
    if ($rawSrc) { break }
}
if (-not $rawSrc) {
    Say "SKIP ⑥⑦ 无 RAW 素材（tests/output/raw_samples*/）⇒ **不给绿灯**："
    Say "        RAW→PNG 的「拍摄字段补回 + PNG 写入通道」**行为**锁未跑。"
    Say "        参数形态锁见 ServiceProbe metaraw 的 B6；取得素材后本项自动启用。"
}
else {
    $d7 = "$val/rawpng"; New-Item -ItemType Directory -Path $d7 -Force | Out-Null
    Run-Exe $exe "--headless -i `"$($rawSrc.FullName)`" --format png --output `"$d7`" --preserve-metadata --log-file `"$d7/log.txt`"" 'app-rawpng'
    $o7 = Get-ChildItem "$d7/*" -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -eq '.png' } | Select-Object -First 1
    if (-not $o7) { $fail++; Say "FAIL ⑥ RAW→PNG 未产出 png" }
    else {
        $mm7 = Et "-s3 -MeteringMode `"$($o7.FullName)`""
        $dt7 = Et "-s3 -DateTimeOriginal `"$($o7.FullName)`""
        if ((-not [string]::IsNullOrWhiteSpace($mm7)) -and (-not [string]::IsNullOrWhiteSpace($dt7))) {
            $pass++; Say "PASS ⑥ RAW→PNG 拍摄字段已落盘（MeteringMode=$mm7, DateTimeOriginal=$dt7）⇒ 补回 + PNG 写入通道均通"
        }
        else {
            $fail++; Say "FAIL ⑥ RAW→PNG 缺拍摄字段（MeteringMode=「$mm7」DateTimeOriginal=「$dt7」）⇒ 补回未生效，或 PNG 的 exiftool 写入失败（eXIf 缺陷复发？）"
        }

        $d8 = "$val/rawpng-strip"; New-Item -ItemType Directory -Path $d8 -Force | Out-Null
        Run-Exe $exe "--headless -i `"$($rawSrc.FullName)`" --format png --output `"$d8`" --strip-metadata --log-file `"$d8/log.txt`"" 'app-rawpng-strip'
        $o8 = Get-ChildItem "$d8/*" -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -eq '.png' } | Select-Object -First 1
        if (-not $o8) { $fail++; Say "FAIL ⑦ 负控 RAW→PNG --strip-metadata 未产出 png" }
        else {
            $mm8 = Et "-s3 -MeteringMode `"$($o8.FullName)`""
            $dt8 = Et "-s3 -DateTimeOriginal `"$($o8.FullName)`""
            if ([string]::IsNullOrWhiteSpace($mm8) -and [string]::IsNullOrWhiteSpace($dt8)) {
                $pass++; Say "PASS ⑦ 负控 RAW→PNG + --strip-metadata 无拍摄字段 ⇒ ⑥ 的断言能识别有无，非空跑"
            }
            else {
                $fail++; Say "FAIL ⑦ 负控 RAW→PNG + --strip-metadata 仍有拍摄字段（MeteringMode=「$mm8」DTO=「$dt8」）⇒ ⑥ 无法区分，属空跑"
            }
        }
    }
}

# ── ⑧ 复制的宣称 vs 现实：0 项落地必须点名（2026-10-29 B5）────────────────────────
# 缺陷（本机实机读数，非推断）：`CopyMetadataAsync`（protectColorMetadata=false 那条，
#   backend=Cjpegli/Dng 才走到）用 `-all:all` 按**标签名**逐项映射。对 HD Photo 源（其 XMP 挂在
#   exiftool 解析不出的命名空间前缀上，读回是 `XMP-prefix0` 而非 `XMP-dc`）⇒ JPEG 侧找不到可写目标
#   ⇒ exiftool 报「0 image files updated」而**退出码 0**；日志却早已写下「复制元数据」
#   ⇒ 产物 0 个 XMP/EXIF，用户以为带上了。同一对源/目标走 Safe 变体（显式组选择器 `-XMP:all` 等
#   = 整组搬运）实测**能落地** ⇒ 两臂的差别从来不是"要不要保护色彩标签"，而是"复制得动复制不动"。
# 修复两半：① 非 Safe 变体补上同一组选择器（功能）；② 两个变体都读 exiftool 的总结行，落地 0 件时点名（诚实）。
# ⚠ 功能半边**给不出可移植的行为判据**（本轮实测过三种替身：PNG 挂 XMP 写不进去；JPEG 挂未注册
#   命名空间 ⇒ 两臂都「1 updated」但都不落地，不判别；lavfi 造源本身无元数据）。只有真 HD Photo
#   件能复现「-all:all 0 项 / 组选择器 1 项且落地」这个分叉，而那是维护者的 11 MB 私件，不入仓。
#   ⇒ 功能半边由「形态锁（⑧c/⑧d）+ 实机读数」承担。实机读数（异环 .jxr → jpg，同一件 exe 前后）：
#     448981 B、无 XMP  ⇒  452016 B、含 XMP，且与 Safe 变体的产物**逐字节相同**（sha16 0cc3675e8a2f7564）。
#   本档 ⑧a/⑧b 锁的是诚实半边（可移植、有牙）。
$dm = "$val/copy-noop"; New-Item -ItemType Directory -Path $dm -Force | Out-Null
$srcBare = "$val/bare.png"
Run-Exe $ff "-y -v error -f lavfi -i testsrc=size=64x64:rate=1 -frames:v 1 `"$srcBare`"" 'mk-bare'
if (-not (Test-Path $srcBare)) { $fail++; Say "FAIL ⑧ 前置：造不出无元数据源（lavfi testsrc）" }
else {
    # ⑧a 无元数据的源 ⇒ 必须点名"0 项落地"（旧行为：只说"复制元数据"，然后什么都不带）
    Run-Exe $exe "--headless -i `"$srcBare`" --format jpg --encoder Cjpegli --output `"$dm`" --log-level debug --log-file `"$dm/log.txt`"" 'app-bare'
    $logA = ''
    if (Test-Path "$dm/log.txt") { $logA = [System.IO.File]::ReadAllText("$dm/log.txt") }
    CK (($logA -match '复制元数据') -and ($logA -match '0 项落地')) `
       "⑧a 源无可复制标签时，日志在「复制元数据」之后**点名 0 项落地**（实机：无元数据 png → jpg）"
    # ⑧b 正控：有元数据的源 ⇒ 不许出现"0 项落地"（证明 ⑧a 不是恒真的空判据）
    $dk = "$val/copy-land"; New-Item -ItemType Directory -Path $dk -Force | Out-Null
    Run-Exe $exe "--headless -i `"$src`" --format jpg --encoder Cjpegli --output `"$dk`" --log-level debug --log-file `"$dk/log.txt`"" 'app-land'
    $logB = ''
    if (Test-Path "$dk/log.txt") { $logB = [System.IO.File]::ReadAllText("$dk/log.txt") }
    $oB = Get-ChildItem "$dk/*" -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -eq '.jpg' } | Select-Object -First 1
    $gB = ''
    if ($oB) { $gB = Et "-s3 -GPSLatitude `"$($oB.FullName)`"" }
    Write-Output ("  [⑧b 读数] 产物=" + $(if ($oB) { "$($oB.Name) $($oB.Length)B" } else { "(无)" }) + " GPS=「$gB」")
    CK (($logB -match '复制元数据') -and ($logB -notmatch '0 项落地') -and (-not [string]::IsNullOrWhiteSpace($gB))) `
       "⑧b **正控**：有元数据的源确实落地（GPS 读回 $gB）且**不**报 0 项 ⇒ ⑧a 能区分两种情形"

    # ⑧c/⑧d 形态锁：功能半边（组选择器）+ 总结行只有一份实现
    $ets = [System.IO.File]::ReadAllText("$root/src/FfmpegGui/Services/ExifToolService.cs")
    $cmAt = $ets.IndexOf('Task<int> CopyMetadataAsync(', [System.StringComparison]::Ordinal)
    $safeAt = $ets.IndexOf('Task<int> CopyMetadataSafeAsync(', [System.StringComparison]::Ordinal)
    if ($cmAt -lt 0 -or $safeAt -lt 0 -or $safeAt -le $cmAt) {
        CK $false "⑧c 形态锁找不到两个复制变体的方法体（改名了？判据要跟着改，不许静默放行）"
    } else {
        $cmBody = $ets.Substring($cmAt, $safeAt - $cmAt)
        CK (($cmBody -match '-XMP:all') -and ($cmBody -match '-EXIF:all') -and ($cmBody -match '-ICC_Profile')) `
           "⑧c 非 Safe 变体的命令行里组选择器与 -ICC_Profile 都在（整组搬运才落得动，`-all:all` 单项映射对 HD Photo 源 0 项）"
    }
    $defCount = ([regex]::Matches($ets, 'static int ParseUpdatedCount')).Count
    $callCount = ([regex]::Matches($ets, 'ParseUpdatedCount\(')).Count
    CK (($defCount -eq 1) -and ($callCount -ge 3)) `
       "⑧d 总结行计数只有一份实现（定义 $defCount 处、引用 $callCount 处），两个复制变体都读它（镜像再生 = 本仓反复的坏法）"
}

# ── 附注：完整复制路径（protectColorMetadata=false ⇒ CopyMetadataAsync）的隐私吸收 ──
#    2026-10-29 更新：该分支现在**被 ⑧ 覆盖到两格**——命令行形态（⑧c）与"落地几件"的宣称一致性
#    （⑧a/b，走的正是这条分支：`--encoder Cjpegli`）。
#    仍未纳入的是它的**并入隐私清理**那一格：`--strip-gps` 与本分支的组合此前只有代码对称性保证
#    （两个变体共用 ApplyAbsorbStripAfterCopyAsync，2026-10-20 收成一处），运行期断言仍缺。
#    · `--format jpegli` 已于 2026-09-21 复核为可用（`EncoderDetectionService.cs:38` 把它当 `jpg`
#      的编码器别名 ⇒ 产物 `.jpg`、`rc=0`）⇒ 不构成排除理由；
#    · `--format dng` 走 raw 专用链路，元数据由 dngtool/exiftool 另行处理。

Say "`n===== PASS=$pass FAIL=$fail SKIP=$skip ====="
if ($fail -eq 0) { Remove-Item -Recurse -Force $val -ErrorAction SilentlyContinue }
else { Say "工作目录保留供排查: $val" }
exit $(if ($fail -eq 0) { 0 } else { 1 })
