# _probe-jxl-input-target.ps1 —— 门禁：JXL 作为**输入**时，SDR-JXL 的「输入色声明」不得侵占
# 「输出目标」字段（ColorPrimaries/ColorTrc），否则用户 `--color-space` 被静默吞掉。
#
# 背景（2026-10-04 色彩实现完整审查 线D P1-1）：
#   SDR-JXL 输入原先把「输入色声明」写进 ColorPrimaries/ColorTrc，而那对字段在 DecideOutputColor
#   里是**输出目标** ⇒ 用户 `--color-space "Display P3"` 被静默吞掉，产物 CICP 仍是 bt709。
#   已修：改走 ColorSourceDecl*（只喂输入侧，不兼作输出目标）。
#   本脚本给这条修复一个**永久判据**（含负控，防断言恒真）。
#
# 用法： pwsh -NoProfile -File tests/scripts/_probe-jxl-input-target.ps1
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
# ⚠ 回退是静默的 —— 门禁可能测的不是你以为的那个二进制。⇒ 一律打印被测 exe 与构建类型（本仓惯例）。
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$fp = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
# ⚠⚠ 2026-10-04 修（与同批其它门禁对齐）：输出目录必须**运行级 GUID 隔离**。
#   旧写法是固定目录 `.../jxl-input-target` + `Remove-Item ... -ErrorAction SilentlyContinue`，
#   有两处隐患（审计实测）：
#    ① Windows 的 `Remove-Item -Recurse -Force` 会**部分删除**：若某文件被独占句柄占用，
#       它会**连同父路径一起存活**，而 `-ErrorAction SilentlyContinue` 把失败**吞掉**；
#    ② 本脚本的断言形态是「有产物 ⇒ 断言其值」，**只检查"有没有"**、不检查"是不是本次生成的"
#       ⇒ 残留的**上一轮**产物会让本轮**空跑也 PASS**（假绿）。
#   同批其它门禁全部用 GUID 隔离（`verify-hlg-route.ps1:37` 等）⇒ 本条原本是**逆惯例的回归**。
$d = "$root/tests/output/validate/jxlinput_$([guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Force -Path $d | Out-Null
Write-Output "  run-dir = $d"

$script:pass = 0; $script:fail = 0
function Assert([bool]$ok, [string]$msg) {
    if ($ok) { $script:pass++; Write-Output "PASS $msg" }
    else     { $script:fail++; Write-Output "FAIL $msg" }
}
# ⚠ 子进程一律 Start-Process -Wait -PassThru（不带 -Wait 时 PS5.1 的 ExitCode 恒 $null）。
# ⚠⚠ 2026-10-04（`_probe-stderr-merge-scan.ps1` 第 56 条转红后修）：本 helper **本就是"只读 stdout"形态**
#   —— stdout/stderr 分流到 `_$tag.out` / `_$tag.err`，取值一律用 `.out`。但第 56 条的判据只认
#   helper **名字**（`ExecOut` / `ExecStdoutOnly` / `ExifVal`），叫 `Run` 时认不出 ⇒ 记"合并取数"站点
#   并把本条判红（实测：本文件 :39 + `_probe-raw-default-tier.ps1` :43/:92/:95 = 4 条 > 债账基线 0）。
#   ⇒ 改用既有只读 helper 的可识别名字 `ExecStdoutOnly`（语义逐字相符），不要放宽第 56 条。
function ExecStdoutOnly([string]$file, [string[]]$argList, [string]$tag) {
    $o = "$d/_$tag.out"; $e = "$d/_$tag.err"
    $p = Start-Process -FilePath $file -ArgumentList $argList -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput $o -RedirectStandardError $e
    return @{ code = $p.ExitCode; outPath = $o
              out = [System.IO.File]::ReadAllText($o); err = [System.IO.File]::ReadAllText($e) }
}
# ffprobe 取**单个**字段：⚠ 合并 `stream=color_primaries,color_transfer` 时 ffprobe 按**结构体字段序**
#   输出（实测为 transfer,primaries），**不是**请求顺序 ⇒ 按位置取会张冠李戴。逐字段单查杜绝顺序陷阱。
function Primaries([string]$f) {
    $r = ExecStdoutOnly $fp @('-v','error','-select_streams','v:0','-show_entries','stream=color_primaries','-of','csv=p=0',"`"$f`"") 'probe'
    return ($r.out -replace '\r?\n',' ').Trim()
}

# ── fail-closed：缺工具直接点名红，不静默 SKIP ─────────────────────────────────
if (-not (Test-Path $exe)) { Write-Output "FAIL 缺被测 exe（Release 与 Debug 均无）: $exe"; Write-Output "PASS=0 FAIL=1"; exit 1 }
if (-not (Test-Path $fp))  { Write-Output "FAIL 缺 ffprobe: $fp";  Write-Output "PASS=0 FAIL=1"; exit 1 }
if (-not (Test-Path $ff))  { Write-Output "FAIL 缺 ffmpeg: $ff";   Write-Output "PASS=0 FAIL=1"; exit 1 }

# ── 输入自备：用**被测 exe** 造一个 SDR JXL（不从 sources/ 读遗留件）──────────────
$png = "$root/tests/output/sources/src_8bit.png"
if (-not (Test-Path $png)) {
    $png = "$d/src_8bit.png"
    $g = ExecStdoutOnly $ff @('-y','-hide_banner','-loglevel','error','-f','lavfi','-i','testsrc2=s=64x48','-frames:v','1',"`"$png`"") 'mksrc'
    Write-Output "  （无 sources/src_8bit.png，用 ffmpeg 现造 exit=$($g.code)）"
}
if (-not (Test-Path $png)) { Write-Output "FAIL 输入造不出（ffmpeg 未产出 $png）"; Write-Output "PASS=0 FAIL=1"; exit 1 }

$mk = ExecStdoutOnly $exe @('--headless','-i',"`"$png`"",'-o',"`"$d/mk`"",'-f','jxl','--log-file',"`"$d/mk.log`"") 'mk'
$jxl = Get-ChildItem "$d/mk" -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -eq '.jxl' } | Select-Object -First 1
if (-not $jxl) { Write-Output "FAIL 造 JXL 失败（exe exit=$($mk.code)）: $($mk.err.Trim())"; Write-Output "PASS=0 FAIL=1"; exit 1 }
Write-Output "  源 JXL = $($jxl.FullName)"

# ── 正断言：--color-space "Display P3" ⇒ 产物 color_primaries 必须是 smpte432（修前为 bt709）──
$p3d = "$d/p3"
$r1 = ExecStdoutOnly $exe @('--headless','-i',"`"$($jxl.FullName)`"",'-o',"`"$p3d`"",'-f','png','--color-space','"Display P3"','--log-file',"`"$d/p3.log`"") 'p3'
$p3 = Get-ChildItem $p3d -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -eq '.png' } | Select-Object -First 1
if (-not $p3) {
    Assert $false "正断言 JXL+Display P3 有产物（exe exit=$($r1.code)）"
} else {
    $prim = Primaries $p3.FullName
    Assert ($prim -eq 'smpte432') "正断言 JXL+Display P3 ⇒ color_primaries=smpte432（实得 '$prim'）"
}

# ── 负控：同一 JXL **不带** --color-space ⇒ 不得为 smpte432（防断言恒真、无区分力）──
$negd = "$d/neg"
$r2 = ExecStdoutOnly $exe @('--headless','-i',"`"$($jxl.FullName)`"",'-o',"`"$negd`"",'-f','png','--log-file',"`"$d/neg.log`"") 'neg'
$neg = Get-ChildItem $negd -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -eq '.png' } | Select-Object -First 1
if (-not $neg) {
    Assert $false "负控 有产物（exe exit=$($r2.code)）"
} else {
    $primNeg = Primaries $neg.FullName
    if ($primNeg -eq 'smpte432') {
        Assert $false "负控失效：不带 --color-space 仍得 smpte432 ⇒ 正断言恒真、无区分力（实得 '$primNeg'）"
    } else {
        Assert $true "负控 不带 --color-space ⇒ 非 smpte432（实得 '$primNeg'）"
    }
}

Write-Output "PASS=$($script:pass) FAIL=$($script:fail)"
if ($script:fail -gt 0) { exit 1 } else { exit 0 }
