# _probe-raw-default-tier.ps1 —— 门禁：RAW 默认档的「HDR 承载力」判据必须用**实际交付档**，
# 不是「请求档 ?? 8」。否则 16-bit 容器（RAW 中间件恒 16-bit）被误判成 8-bit ⇒ 默认档错误降到
# bt709/sRGB，而产物实际是 rgb48be。
#
# 背景（2026-10-04 色彩实现完整审查 线D P1-2）：
#   判据原为 `BitDepth ?? 8`（**请求**档），而实际交付位深**跟随源**（dngtool 中间件恒 16-bit）
#   ⇒ ARW→PNG 默认档错误降到 bt709/sRGB（产物却是 rgb48be）。已修：按实际交付档判
#   ⇒ 16-bit 容器默认档变 Rec.2020 + PQ。
#   三档（用户未选目标时）：① 能承载 HDR 传递（png-16bit…）⇒ Rec.2020 + PQ；
#                          ② TIFF（唯一例外，ICC 可描述广色域但无 HDR 传递）⇒ Rec.2020 + SDR；
#                          ③ 其余（webp/jpg…）⇒ bt709 + sRGB。
#
# 用法： pwsh -NoProfile -File tests/scripts/_probe-raw-default-tier.ps1
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
# ⚠ 回退是静默的 —— 一律打印被测 exe 与构建类型（本仓惯例，让读数可追溯）。
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$fp = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$ex = "$root/publish/PLAN/exiftool/exiftool.exe"
# ⚠⚠ 2026-10-04 修（与同批其它门禁对齐）：输出目录必须**运行级 GUID 隔离**。
#   旧写法固定目录 + `Remove-Item ... -ErrorAction SilentlyContinue` 有两处隐患（审计实测）：
#    ① Windows 的 `Remove-Item -Recurse -Force` 会**部分删除**（被独占句柄占用的文件连同父路径存活），
#       而 `-ErrorAction SilentlyContinue` 把该失败**吞掉**；
#    ② 断言形态是「有产物 ⇒ 断言其值」，只查"有没有"⇒ 残留的上一轮产物会让本轮**空跑也 PASS**（假绿）。
#   同批 `verify-hlg-route.ps1:37` / `verify-gainmap-host.ps1:36` 等全部 GUID 隔离 ⇒ 本条原为逆惯例回归。
$d = "$root/tests/output/validate/rawtier_$([guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Force -Path $d | Out-Null
Write-Output "  run-dir = $d"

$script:pass = 0; $script:fail = 0
function Assert([bool]$ok, [string]$msg) {
    if ($ok) { $script:pass++; Write-Output "PASS $msg" }
    else     { $script:fail++; Write-Output "FAIL $msg" }
}
# ⚠ 子进程一律 Start-Process -Wait -PassThru（不带 -Wait 时 PS5.1 的 ExitCode 恒 $null）。
# ⚠⚠ 2026-10-04（`_probe-stderr-merge-scan.ps1` 第 56 条转红后修）：本 helper **本就是"只读 stdout"形态**
#   —— stdout/stderr 分流到 `_$tag.out` / `_$tag.err` 两个文件，下面 `Probe`/值读取一律取 `.out`。
#   但第 56 条的判据只认 helper **名字**（`ExecOut` / `ExecStdoutOnly` / `ExifVal`），
#   叫 `Run` 时它**认不出**这是只读版 ⇒ 按"合并取数读工具值"记 4 个站点、把本条判红
#   （实测：`_probe-jxl-input-target.ps1:39` + 本文件 :43/:92/:95 = 4 条 > 债账基线 0）。
#   ⇒ 采用与既有只读 helper **同一个可识别名字** `ExecStdoutOnly`（语义逐字相符：只从 stdout 取值，
#     stderr 另存供诊断），使判据能正确归入"已有只读版"。
function ExecStdoutOnly([string]$file, [string[]]$argList, [string]$tag) {
    $o = "$d/_$tag.out"; $e = "$d/_$tag.err"
    $p = Start-Process -FilePath $file -ArgumentList $argList -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput $o -RedirectStandardError $e
    return @{ code = $p.ExitCode; outPath = $o; errPath = $e
              out = [System.IO.File]::ReadAllText($o); err = [System.IO.File]::ReadAllText($e) }
}
# ffprobe 取**单个**字段：⚠ 合并 `stream=pix_fmt,color_primaries,color_transfer` 时 ffprobe 按
#   **结构体字段序**输出，不是请求顺序 ⇒ 按位置取会张冠李戴。逐字段单查杜绝顺序陷阱。
function Probe([string]$f, [string]$field) {
    $r = ExecStdoutOnly $fp @('-v','error','-select_streams','v:0','-show_entries',"stream=$field",'-of','csv=p=0',"`"$f`"") "probe_$field"
    return ($r.out -replace '\r?\n',' ').Trim()
}
function FirstArtifact([string]$dir, [string]$ext) {
    return Get-ChildItem $dir -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -eq $ext } | Select-Object -First 1
}

# ── fail-closed：缺工具/缺素材直接点名红，不静默 SKIP ───────────────────────────
if (-not (Test-Path $exe)) { Write-Output "FAIL 缺被测 exe（Release 与 Debug 均无）: $exe"; Write-Output "PASS=0 FAIL=1"; exit 1 }
if (-not (Test-Path $fp))  { Write-Output "FAIL 缺 ffprobe: $fp";  Write-Output "PASS=0 FAIL=1"; exit 1 }
if (-not (Test-Path $ff))  { Write-Output "FAIL 缺 ffmpeg: $ff";   Write-Output "PASS=0 FAIL=1"; exit 1 }
if (-not (Test-Path $ex))  { Write-Output "FAIL 缺 exiftool: $ex"; Write-Output "PASS=0 FAIL=1"; exit 1 }
# ⚠ 链外素材：ARW 无法自备 ⇒ 缺失即点名红（不许降级成 SKIP）。
$arw = "$root/tests/output/raw_samples/Sony_ILCE7S.ARW"
if (-not (Test-Path $arw)) { Write-Output "FAIL 缺 RAW 素材（链外，无法自备）: $arw"; Write-Output "PASS=0 FAIL=1"; exit 1 }

# ── ① 16-bit 容器默认档 = Rec.2020 + PQ（ARW→PNG 默认档）───────────────────────
$pngd = "$d/png"
$r1 = ExecStdoutOnly $exe @('--headless','-i',"`"$arw`"",'-o',"`"$pngd`"",'-f','png','--log-file',"`"$d/png.log`"") 'png'
$png = FirstArtifact $pngd '.png'
if (-not $png) {
    Assert $false "① ARW→PNG 有产物（exe exit=$($r1.code)）"
} else {
    $pf = Probe $png.FullName 'pix_fmt'
    $pcp = Probe $png.FullName 'color_primaries'
    $pct = Probe $png.FullName 'color_transfer'
    Assert ($pf -like 'rgb48*')  "① ARW→PNG pix_fmt 以 rgb48 开头（实得 '$pf'）"
    Assert ($pcp -eq 'bt2020')   "① ARW→PNG color_primaries=bt2020（实得 '$pcp'）"
    Assert ($pct -eq 'smpte2084') "① ARW→PNG color_transfer=smpte2084（实得 '$pct'）"
}

# ── ② 8-bit 上限容器仍走 SDR 档：ARW→WebP 不得升到 bt2020（防「一刀切升档」）─────
# ⚠⚠ 2026-10-04 修（**原断言恒真 = 假绿**，审计实测）：
#   旧写法断言「webp 产物 `color_primaries` 不得为 `bt2020`」。但 **webp 的 muxer 根本不写 CICP**：
#     · `FfmpegCommandBuilder.Decision.cs:185`：`CanWriteContainerCicp = fmt is "png" or "apng" or "avif" or "jxl"`
#       ⇒ **webp 被排除**，产品**永远**不会往 webp 里写 CICP；
#     · 实测佐证：强行 `-color_primaries bt2020 -color_trc smpte2084` 编 webp ⇒ ffprobe 仍读 `unknown,unknown`；
#       且产品跑 `--raw-color-target rec2020` 与 `rec709` 两档，webp 产物 `color_primaries` **都是 unknown**。
#   ⇒ 旧断言**无论产品对错都 PASS**，零判别力，且虚增 PASS 数（宣称的"反控"名不副实）。
#   修法：改断言**真正会变的那个可观测面** —— 日志里的输出目标。实测 rec2020 档下 webp 输出目标为
#     `bt709/iec61966-2-1`（因为 webp 8-bit 上限 ⇒ 判据档=8 ⇒ 走 SDR 档）⇒ 断言它**不得**是 `bt2020/*`。
#     ⚠ 这条**有牙**：若哪天"一刀切升档"把 8-bit 容器也升级成 bt2020，日志目标会变成 `bt2020/...` ⇒ 转红。
$webpd = "$d/webp"
$r2 = ExecStdoutOnly $exe @('--headless','-i',"`"$arw`"",'-o',"`"$webpd`"",'-f','webp','--log-level','debug','--log-file',"`"$d/webp.log`"") 'webp'
$webp = FirstArtifact $webpd '.webp'
if (-not $webp) {
    Assert $false "② ARW→WebP 有产物（exe exit=$($r2.code)）"
} else {
    $wlog = if (Test-Path "$d/webp.log") { [System.IO.File]::ReadAllText("$d/webp.log") } else { '' }
    $wm = [regex]::Match($wlog, '输出目标\s+([a-z0-9\-]+/[a-z0-9\-]+)')
    if (-not $wm.Success) {
        Assert $false "② ARW→WebP 日志里读不到「输出目标」（不可评估 ⇒ 判红不判绿）"
    } else {
        $wt = $wm.Groups[1].Value
        # 8-bit 上限容器必须落在 SDR 档（bt709/...）；不得被"一刀切"升成 bt2020/...
        Assert ($wt -notlike 'bt2020/*') "② ARW→WebP 输出目标不得为 bt2020（实得 '$wt'）⇒ 8-bit 上限容器未被一刀切升档"
        # 正控（防空跑）：同素材的 PNG 默认档**必须**是 bt2020/*，否则说明这条判据的环境前提不成立
        Assert ($wt -like 'bt709/*') "② ARW→WebP 输出目标为 SDR 档 'bt709/*'（实得 '$wt'）"
    }
}

# ── ③ TIFF 例外档 = 16-bit + Rec.2020 + SDR（有目标 ICC；日志宣称输出目标 bt2020/iec61966-2-1）──
$tifd = "$d/tiff"
$r3 = ExecStdoutOnly $exe @('--headless','-i',"`"$arw`"",'-o',"`"$tifd`"",'-f','tiff','--log-level','debug','--log-file',"`"$d/tiff.log`"") 'tiff'
$tif = FirstArtifact $tifd '.tiff'
if (-not $tif) {
    Assert $false "③ ARW→TIFF 有产物（exe exit=$($r3.code)）"
} else {
    $bps = ExecStdoutOnly $ex @('-BitsPerSample','-s',"`"$($tif.FullName)`"") 'bps'
    Assert ($bps.out -match '16 16 16') "③ ARW→TIFF BitsPerSample 含 '16 16 16'（实得 '$((($bps.out -replace '\r?\n',' ') -replace '\s+',' ').Trim())'）"
    # exiftool -icc_profile -b 输出**二进制** ICC 到 stdout ⇒ 用落盘文件的字节数判定（不按文本读）。
    $icc = ExecStdoutOnly $ex @('-icc_profile','-b',"`"$($tif.FullName)`"") 'icc'
    $iccBytes = (Get-Item $icc.outPath).Length
    Assert ($iccBytes -gt 0) "③ ARW→TIFF 有目标 ICC（-icc_profile -b 字节数=$iccBytes > 0）"
    # ⚠ 2026-10-04 加强（原只判"字节数>0" ⇒ **任意** ICC 都能过，含 sRGB）：改判**原色确实是 BT.2020**。
    #   实测判据（本机 exiftool `-ICC_Profile:all -s` 取值，BT.2020 与 BT.709 天然可分）：
    #     BT.2020：RedMatrixColumn=0.67348 · GreenMatrixColumn=0.16566 **0.67534** · Blue=0.12505
    #     BT.709 ：RedMatrixColumn=0.43604 · GreenMatrixColumn=0.38512 **0.71690** · Blue=0.14305
    #   ⇒ 取 **Green 的 y 列**（0.675 vs 0.717）与 **Red 的 x 列**（0.673 vs 0.436）作为判别量，
    #     两者都是数量级可分，绝不是"浮点容差级"的差别。
    $co = ExecStdoutOnly $ex @('-ICC_Profile:all','-s',"`"$($tif.FullName)`"") 'iccfields'
    $cot = $co.out
    $redX  = [regex]::Match($cot, 'RedMatrixColumn\s*:\s*([0-9.]+)').Groups[1].Value
    $grnY  = [regex]::Match($cot, 'GreenMatrixColumn\s*:\s*[0-9.]+\s+([0-9.]+)').Groups[1].Value
    if (-not $redX -or -not $grnY) {
        Assert $false "③ ARW→TIFF ICC 原色字段读不到（redX='$redX' grnY='$grnY'）⇒ 不可评估，判红不判绿"
    } else {
        $rx = [double]$redX; $gy = [double]$grnY
        # BT.2020: redX≈0.673 / grnY≈0.675 ；BT.709(sRGB): redX≈0.436 / grnY≈0.717
        Assert (([Math]::Abs($rx - 0.673) -lt 0.01) -and ([Math]::Abs($gy - 0.675) -lt 0.01)) `
            "③ ARW→TIFF 目标 ICC 原色为 **BT.2020**（redX=$redX≈0.673 / grnY=$grnY≈0.675；BT.709 应为 0.436/0.717）"
    }
    $log = [System.IO.File]::ReadAllText("$d/tiff.log")
    Assert ($log -match '输出目标 bt2020/iec61966-2-1') "③ TIFF 日志出现 '输出目标 bt2020/iec61966-2-1'（有目标 ICC 的宣称）"
}

# ── ④ `rec709` 与显式目标色域**同时给出**时必须**点名冲突**（不得静默丢弃）──────────────
# ⚠ 立锁动机（2026-10-04 审查 P1 实测）：`rawUserChoseTarget` 门挡住整块默认档逻辑（含 `rec709`
#   「强制 bt709 + sRGB，覆盖容器能力」）⇒ `--raw-color-target rec709 --color-space sRGB` 时
#   旧日志只说「用户已指定目标色域 ⇒ 保留用户配置」，**一个字都不提 rec709**
#   ⇒ 用户以为 rec709 生效了，实际被丢弃，且与 `CliParser` 的帮助文本矛盾。
#   本仓铁律「静默丢弃不点名」正是要守的东西 ⇒ 现在必须出现点名字样。
#   ⚠ 断言**同时**要求「仍有用户目标生效」⇒ 防止有人用"改成 rec709 生效"来蒙混（那会改变既有语义）。
$cd = "$d/conflict"
$rc = ExecStdoutOnly $exe @('--headless','-i',"`"$arw`"",'-o',"`"$cd`"",'-f','tiff',
    '--raw-color-target','rec709','--color-space','sRGB','--log-level','debug','--log-file',"`"$d/conflict.log`"") 'conflict'
$clog = if (Test-Path "$d/conflict.log") { [System.IO.File]::ReadAllText("$d/conflict.log") } else { '' }
if ($clog -eq '') {
    Assert $false "④ rec709+显式目标 的日志读不到（exe exit=$($rc.code)）⇒ 不可评估，判红不判绿"
} else {
    Assert ($clog -match 'rec709.{0,40}不生效|不生效.{0,40}rec709') `
        "④ rec709 与显式目标同时给出时**点名 rec709 本次不生效**（静默丢弃 ⇒ 转红）"
    # 反向控制：用户目标仍必须生效（不许改成"rec709 覆盖用户"来换绿）
    Assert ($clog -match '用户已指定目标') "④ 反向控制：用户显式目标仍然优先（未被 rec709 覆盖）"
    # 反向控制：单独给 rec709（无目标）时**不得**出现「不生效」告警（防误报噪声）
    $sd = "$d/solo"; 
    $rs = ExecStdoutOnly $exe @('--headless','-i',"`"$arw`"",'-o',"`"$sd`"",'-f','tiff',
        '--raw-color-target','rec709','--log-level','debug','--log-file',"`"$d/solo.log`"") 'solo'
    $slog = if (Test-Path "$d/solo.log") { [System.IO.File]::ReadAllText("$d/solo.log") } else { '' }
    Assert (($slog -match '色域模式=rec709') -and ($slog -notmatch '不生效')) `
        "④ 单独给 rec709（无显式目标）⇒ 正常生效且**无**误报冲突告警"
}

Write-Output "PASS=$($script:pass) FAIL=$($script:fail)"
if ($script:fail -gt 0) { exit 1 } else { exit 0 }
