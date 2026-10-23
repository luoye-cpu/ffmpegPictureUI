# ═══════════════════════════════════════════════════════════
#  verify-acr-route.ps1 — 第 79 条（2026-10-24 接线）
#  被测：DNG 输入的 **ACR 路线**（`dngtool -rdp -rdspace …` + 产品侧的分流/声明）。
#
#  动机：2026-10-24 起 DNG 输入不再走 LibRaw，而是走 Adobe DNG SDK 的 `dng_render`：
#    · LibRaw **一张 opcode 都不执行**（OpcodeList1/2/3 全跳过）；SDK 三张全按规范施加。
#      实测 RAW-G.dng：同一文件、同一白平衡，四角/中心 = 1.70（不施加）vs 1.84（施加）。
#    · 渲染空间按**下游目标**选：bt709 出口渲 sRGB 原色（`-rdspace srgb`），其余渲 Rec2020。
#      两腿只差一次已知矩阵 + 域裁剪（实测 PSNR 82~87 dB），**但必须真的不同**（否则开关是空转）。
#    · `dng_render` 不旋转 ⇒ 工具按 `negative->Orientation()` 旋正（与旧 LibRaw `-T` 同行为）。
#  本闸把这三件事各配一条**有牙**判据，并把产品侧的分流/声明端到端钉住。
#
#  ⚠ 素材链外（`tests/output/raw_samples/*.ARW`）：缺失即点名红，不降级成 SKIP。
#  ⚠ 夹具现场生成（`dngtool -e`）：带镜头表的那份会写 3 条 OpcodeList3 —— 用它做"opcode 真的被执行"的正臂，
#    同源不含镜头校正的那份做负臂（两臂像素必须分叉，否则尺子是恒真的）。
#  ⚠ 空间对撞用 `_readback-acr-render.py`（memmap + 抽样），文件级"是否相同"另用 SHA256 全文件哈希。
# ═══════════════════════════════════════════════════════════
$ErrorActionPreference = 'Continue'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root

$dng = "$root/publish/PLAN/artifacts/dngtool.exe"
$ex  = "$root/publish/PLAN/exiftool/exiftool.exe"
$ffp = "$root/publish/PLAN/ffmpeg-full/ffprobe.exe"
$app = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $app)) { $app = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
$arw = "$root/tests/output/raw_samples/Sony_ILCE7S.ARW"
$idcArw = "$root/tests/output/raw_samples/Sony_ILCE7M2_IDC.ARW"
$rd  = "$root/tests/scripts/_readback-acr-render.py"
$d   = "$root/tests/output/results/acr-route"
Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $d | Out-Null

foreach ($p in @(@('dngtool', $dng), @('exiftool', $ex), @('ffprobe', $ffp),
                 @('被测 exe（Release 与 Debug 均无）', $app),
                 @('RAW 素材（链外，无法自备）', $arw),
                 @('带镜头表的 RAW 素材（链外，opcode 正臂的唯一来源）', $idcArw),
                 @('渲染读手', $rd))) {
    if (-not (Test-Path $p[1])) {
        Write-Output "FAIL 缺 $($p[0]): $($p[1])"
        Write-Output 'PASS=0 FAIL=1'
        exit 1
    }
}

$script:pass = 0; $script:fail = 0
function CK([bool]$ok, [string]$msg) {
    if ($ok) { $script:pass++; Write-Output "PASS $msg" }
    else     { $script:fail++; Write-Output "FAIL $msg" }
}
# 需要退出码的臂（Start-Process 只有 -PassThru 才给 rc）→ @(rc, stdout, stderr)
function ExecRC([string]$file, [string]$argStr) {
    $o = "$d/_r_o.txt"; $e = "$d/_r_e.txt"
    Remove-Item $o, $e -Force -ErrorAction SilentlyContinue
    $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput $o -RedirectStandardError $e
    $so = if (Test-Path $o) { [System.IO.File]::ReadAllText($o) } else { '' }
    $se = if (Test-Path $e) { [System.IO.File]::ReadAllText($e) } else { '' }
    return ,@([int]$p.ExitCode, $so, $se)
}
function TagVal([string]$tag, [string]$path) {
    $t = (ExecRC $ex "-n -s -s -$tag `"$path`"")[1]
    $t = ($t -replace '\r?\n', ' ').Trim()
    if ($t -eq '') { return '' }
    $p2 = $t -split ':', 2
    if ($p2.Count -ge 2) { return $p2[1].Trim() }
    return $t
}
function FirstArtifact([string]$dir, [string]$ext) {
    return Get-ChildItem $dir -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -eq $ext } | Select-Object -First 1
}
function LogText([string]$p) { if (Test-Path $p) { [System.IO.File]::ReadAllText($p) } else { '' } }

# ── 夹具：同源两件（带/不带镜头 opcode）+ 一件旋转件 ────────────────────────
$rA = ExecRC $dng "-e -lens-corr -i `"$idcArw`" -O `"$d/m2-op.dng`""
$rB = ExecRC $dng "-e -i `"$idcArw`" -O `"$d/m2-noop.dng`""
CK ($rA[0] -eq 0 -and $rB[0] -eq 0) "夹具生成：两份 DNG 都成功（rc=$($rA[0])/$($rB[0])）"
CK ($rA[2] -match 'lens-opcodes=3') "夹具成立：带镜头表那份写进 3 条 opcode（stderr 点名 lens-opcodes=3）"
Copy-Item "$d/m2-noop.dng" "$d/m2-rot.dng" -Force
$null = ExecRC $ex "-overwrite_original `"-Orientation#=6`" `"$d/m2-rot.dng`""
CK ((TagVal 'Orientation' "$d/m2-rot.dng") -eq '6') "旋转夹具成立：Orientation=6 已落盘（实得 '$(TagVal 'Orientation' "$d/m2-rot.dng")'）"

# ══════════════════════════════════════════════════════════════════════
# A 组：工具级（dngtool -rdp）
# ══════════════════════════════════════════════════════════════════════

# ① 渲染成功 + opcode 计数来自工具自己的读数
$p1 = ExecRC $dng "-rdp -rdspace rec2020 -i `"$d/m2-op.dng`" -O `"$d/op-rec2020.tif`""
CK ($p1[0] -eq 0 -and (Test-Path "$d/op-rec2020.tif")) "① -rdp 渲染成功（rc=$($p1[0])）"
CK ($p1[2] -match 'opcode1/2/3=0/0/3') "① 工具报出 opcode1/2/3=0/0/3（本件确有 3 条 OpcodeList3）"
CK ($p1[2] -match 'render=linear rec2020') "① 工具报出 render=linear rec2020"

# ② opcode 真的被执行：带/不带 opcode 的同一源，四角/中心必须分叉
$p2 = ExecRC $dng "-rdp -rdspace rec2020 -i `"$d/m2-noop.dng`" -O `"$d/noop-rec2020.tif`""
$rt = (ExecRC 'py' "`"$rd`" ratio `"$d/op-rec2020.tif`" `"$d/noop-rec2020.tif`"")[1]
$rel = 0.0
if ($rt -match 'rel=([0-9.]+)') { $rel = [double]$Matches[1] }
CK ($rel -gt 1.03) "② opcode 被执行：带暗角/畸变 opcode 的角/中比 = 不带 × $rel（>1.03 才算施加；读数 $($rt.Trim())）"

# ③ 空间开关**真的**换空间：两腿文件不同，但 srgb 腿 == clip(rec2020 腿 × 已知矩阵)
$p3 = ExecRC $dng "-rdp -rdspace srgb -i `"$d/m2-op.dng`" -O `"$d/op-srgb.tif`""
CK ($p3[0] -eq 0 -and (Test-Path "$d/op-srgb.tif")) "③ -rdspace srgb 成功（rc=$($p3[0])）"
$sp = (ExecRC 'py' "`"$rd`" space `"$d/op-rec2020.tif`" `"$d/op-srgb.tif`"")[1]
$ident = -1; $psnr = 0.0
if ($sp -match 'identical=(\d)') { $ident = [int]$Matches[1] }
if ($sp -match 'psnr=([0-9.]+)') { $psnr = [double]$Matches[1] }
CK ($ident -eq 0) "③ 负控（开关不是空转）：rec2020 腿与 srgb 腿的**全文件哈希不同**"
CK ($psnr -ge 70.0) "③ 两腿只差一次已知矩阵：clip(rec2020×M) 对 srgb 的 PSNR = $psnr dB（≥70）"

# ④ 未知空间必须拒绝（点名 + 无产物）
$p4 = ExecRC $dng "-rdp -rdspace bogus -i `"$d/m2-op.dng`" -O `"$d/bogus.tif`""
CK ($p4[0] -ne 0 -and -not (Test-Path "$d/bogus.tif") -and $p4[2] -match 'unknown -rdspace') `
   "④ 负控：未知 -rdspace 拒绝（rc=$($p4[0])，无产物，stderr 点名 unknown -rdspace）"

# ⑤ 朝向上限：Orientation=6 必须旋正（宽高互换），不是"原样写出"
$p5 = ExecRC $dng "-rdp -rdspace rec2020 -i `"$d/m2-rot.dng`" -O `"$d/rot.tif`""
$dm = (ExecRC 'py' "`"$rd`" dims `"$d/noop-rec2020.tif`" `"$d/rot.tif`"")[1]
$okShape = $false; $h1 = 0; $w1 = 0; $h2 = 0; $w2 = 0
if ($dm -match 'h=(\d+) w=(\d+) h2=(\d+) w2=(\d+)') {
    $h1 = [int]$Matches[1]; $w1 = [int]$Matches[2]; $h2 = [int]$Matches[3]; $w2 = [int]$Matches[4]
    $okShape = ($h1 -eq $w2) -and ($w1 -eq $h2) -and ($h1 -ne $w1)
}
CK ($okShape) "⑤ 朝向旋正：Orientation=6 产物宽高互换（正立 $($h1)x$($w1) vs 旋转件 $($h2)x$($w2)）"

# ⑥ 半成品不残留（写临时名再改名的路径契约）
$parts = @(Get-ChildItem $d -Recurse -File -Filter '*.part' -ErrorAction SilentlyContinue)
CK ($parts.Count -eq 0) "⑥ 无 .part 半成品残留（实得 $($parts.Count) 个）"

# ══════════════════════════════════════════════════════════════════════
# B 组：产品级（DNG 输入的分流 + 声明 + 元数据）
# ══════════════════════════════════════════════════════════════════════
$logDir = "$d/logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

# ⑦ DNG→JPEG 默认档：判据是 bt709 ⇒ 必须渲 sRGB 原色，且声明同步
$jd = "$d/out-jpg"
$r7 = ExecRC $app "--headless -i `"$d/m2-op.dng`" -o `"$jd`" -f jpg --log-level debug --log-file `"$logDir/jpg.log`""
$jpg = FirstArtifact $jd '.jpg'
$l7 = LogText "$logDir/jpg.log"
CK ($null -ne $jpg -and $r7[0] -eq 0) "⑦ DNG→JPEG 有产物（exe rc=$($r7[0])）"
CK ($l7 -match 'ACR 路线渲染空间=srgb') "⑦ 默认 SDR 出口 ⇒ 渲 sRGB 原色（日志 'ACR 路线渲染空间=srgb'）"
CK ($l7 -match '输入声明 bt709/linear') "⑦ 输入声明与渲染空间同源（日志 '输入声明 bt709/linear'）"
CK ($l7 -match 'opcode1/2/3=0/0/3') "⑦ 产品跑的确实是 SDK 臂且施加了 opcode（日志含 opcode1/2/3=0/0/3）"

# ⑧ DNG→PNG 默认档：判据是 bt2020（能承载 HDR 传递）⇒ 必须渲 Rec2020
$pd = "$d/out-png"
$r8 = ExecRC $app "--headless -i `"$d/m2-op.dng`" -o `"$pd`" -f png --log-level debug --log-file `"$logDir/png.log`""
$png = FirstArtifact $pd '.png'
$l8 = LogText "$logDir/png.log"
CK ($null -ne $png -and $r8[0] -eq 0) "⑧ DNG→PNG 有产物（exe rc=$($r8[0])）"
CK ($l8 -match 'ACR 路线渲染空间=rec2020') "⑧ 宽色域出口 ⇒ 渲 Rec2020（日志 'ACR 路线渲染空间=rec2020'）"
CK ($l8 -match '输入声明 bt2020/linear') "⑧ 输入声明与渲染空间同源（日志 '输入声明 bt2020/linear'）"
if ($null -ne $png) {
    $pcp = ((ExecRC $ffp "-v error -select_streams v:0 -show_entries stream=color_primaries -of csv=p=0 `"$($png.FullName)`"")[1] -replace '\r?\n', ' ').Trim()
    CK ($pcp -eq 'bt2020') "⑧ 产物 color_primaries=bt2020（实得 '$pcp'）"
} else {
    CK $false "⑧ 产物 color_primaries=bt2020（无产物 ⇒ 判红不判绿）"
}

# ⑨ 显式宽色域目标（Display P3）⇒ 仍渲 Rec2020（P3 不是 709；宽色域不丢信息）
$p3d = "$d/out-p3"
$r9 = ExecRC $app "--headless -i `"$d/m2-op.dng`" -o `"$p3d`" -f png --color-space `"Display P3`" --log-level debug --log-file `"$logDir/p3.log`""
$l9 = LogText "$logDir/p3.log"
CK ($l9 -match 'ACR 路线渲染空间=rec2020') "⑨ 显式 Display P3 目标 ⇒ 渲 Rec2020（日志 'ACR 路线渲染空间=rec2020'）"

# ⑩ 非 DNG 输入仍然走 LibRaw（ACR 路线不冒充覆盖它）
$ad = "$d/out-arw"
$r10 = ExecRC $app "--headless -i `"$arw`" -o `"$ad`" -f png --log-level debug --log-file `"$logDir/arw.log`""
$arwPng = FirstArtifact $ad '.png'
$l10 = LogText "$logDir/arw.log"
CK ($null -ne $arwPng -and $r10[0] -eq 0) "⑩ ARW→PNG 有产物（exe rc=$($r10[0])）"
CK ($l10 -match '非 DNG 输入 ⇒ LibRaw 臂') "⑩ ARW 走 LibRaw 臂且**点名**（DNG SDK 不读 ARW）"
CK ($l10 -match 'ACR 路线渲染空间=rec2020（非 DNG') "⑩ 非 DNG 的声明仍为 bt2020（相机原生近似）"

# ⑪ 元数据：SDK 中间件 TIFF 不带任何标签 ⇒ 相机厂/型号必须由源 RAW 白名单补回
if ($null -ne $jpg) {
    $mk = TagVal 'Make' $jpg.FullName
    $md = TagVal 'Model' $jpg.FullName
    CK ($mk -ieq 'SONY' -and $md -eq 'ILCE-7M2') "⑪ 相机厂/型号从源补回（Make='$mk' Model='$md'）"
} else {
    CK $false "⑪ 相机厂/型号从源补回（⑦ 无产物 ⇒ 判红不判绿）"
}

Write-Output ''
Write-Output "PASS=$pass FAIL=$fail"
if ($fail -gt 0) { exit 1 } else { exit 0 }
