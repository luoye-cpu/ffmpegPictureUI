# verify-png-signature.ps1 —— PNG/APNG 产物结构门禁（2026-09-15 新增，针对实测到的静默损坏）
# 背景（实测）：只写 `-c:v apng` 而不指定 `-f apng` 时，`.png` 后缀会让 ffmpeg 走 image2/png 封装器，
#   产出**没有 PNG 签名、没有 IHDR、也没有 IEND 的裸 APNG 块流**（实测 2404 字节，首字节即 fcTL，
#   ffprobe 报 `Invalid PNG signature`）——而**退出码是 0、状态「已完成」**，属静默损坏。
#   受影响通路：ProcessCjxlAsync / ProcessCjpegliAsync 的 ffmpeg 回退、JXR→ffmpeg 标准编码
#   （共同点：都经 CloneOptionsForFfmpeg，它把 png 目标也映射成 apng 编码器）。
# 本门禁断言：凡 PNG/APNG 产物，必须 ①以 PNG 8 字节签名为首 ②含 IHDR ③含 IEND。
#   并带**负控**：手工跑 `-c:v apng`（不加 -f apng）必须被判为缺签名 ⇒ 证明断言不是空跑。
# ⚠ 调用外部 exe 一律用 Start-Process -Wait（本仓范式；`&` 在某些宿主里静默起不来）。
# 用法：pwsh -NoProfile -File tests/scripts/verify-png-signature.ps1
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root
$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"
$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$jxrEnc = "$root/publish/PLAN/artifacts/JxrEncApp.exe"
$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
$val = "$root/tests/output/pngsig"

$pass = 0; $fail = 0; $skip = 0
# ⚠ 2026-09-21（`TESTING.md` 第 81 条同族）：**整门禁跳过必须打印汇总并计数**。
#   原先三条跳过路径直接 `exit 0`、**不打印任何汇总** ⇒ 读者只看到一句 SKIP，
#   分不清「跑完了且干净」与「一条断言都没跑」。
function SkipAll([string]$why) {
    $script:skip++
    Write-Output "SKIP $why"
    Write-Output "===== PASS=0 FAIL=0 SKIP=$skip（⚠ 整门禁未运行，不是通过）====="
    exit 0
}
# ⚠ 用**单个参数字符串**而不是数组：`Run-Exe $ff @('a','b')` 这种「函数 + 数组实参」的绑定
#   会把数组元素当成多个位置参数，$argv 只拿到第一个元素 ⇒ ffmpeg 实际只收到 `-y`，
#   静默什么都不生成（实测踩过：素材全空、却因为后续步骤用旧残留而看起来像通过）。
function Run-Exe([string]$file, [string]$argStr) {
    $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru
    return $p.ExitCode
}
# 产物结构判定：签名 + IHDR + IEND（三缺一即判损坏）
function Test-PngStruct([string]$path) {
    if (-not (Test-Path $path)) { return @{ Ok = $false; Why = '文件不存在' } }
    $b = [System.IO.File]::ReadAllBytes($path)
    $sig = @(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
    if ($b.Length -lt 8) { return @{ Ok = $false; Why = '文件过短' } }
    for ($i = 0; $i -lt 8; $i++) { if ($b[$i] -ne $sig[$i]) { return @{ Ok = $false; Why = ("缺 PNG 签名（首字节 0x{0:X2}，应为 0x89）" -f $b[0]) } } }
    $s = [System.Text.Encoding]::ASCII.GetString($b)
    if ($s.IndexOf('IHDR') -lt 0) { return @{ Ok = $false; Why = '缺 IHDR' } }
    if ($s.IndexOf('IEND') -lt 0) { return @{ Ok = $false; Why = '缺 IEND（文件被截断）' } }
    return @{ Ok = $true; Why = '' }
}

if (-not (Test-Path $exe)) { SkipAll "FfmpegGui.exe 未构建（先 dotnet build）" }
if (-not (Test-Path $jxrEnc)) { SkipAll "缺 JxrEncApp（publish/PLAN/artifacts）" }

# ⚠ 每次运行用独立工作目录：固定目录在并发/上次残留（锁住的子目录删不掉）时会串味，
#   实测踩过——上一轮的输出目录残留 + Remove-Item 只删掉根下文件，导致素材看起来“生成了又消失”。
$val = "$root/tests/output/pngsig_$([guid]::NewGuid().ToString('N').Substring(0, 8))"
Remove-Item -Recurse -Force $val -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $val -Force | Out-Null

# ── 素材（全部经 Start-Process + 单个参数字符串）──
$e1 = Run-Exe $ff "-y -v error -f lavfi -i `"testsrc=size=320x200:rate=1`" -frames:v 1 `"$val/src.png`""
$e2 = Run-Exe $ff "-y -v error -f lavfi -i `"testsrc=size=160x120:rate=10:duration=1`" `"$val/anim.gif`""
$e3 = Run-Exe $ff "-y -v error -i `"$val/src.png`" `"$val/src.bmp`""
$e4 = Run-Exe $jxrEnc "-i `"$val/src.bmp`" -o `"$val/src.jxr`""
Write-Output ("素材生成: exit=$e1/$e2/$e3/$e4  目录=" + ((Get-ChildItem $val -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }) -join ','))
if (-not (Test-Path "$val/src.png") -or -not (Test-Path "$val/anim.gif") -or -not (Test-Path "$val/src.jxr")) {
    SkipAll "素材生成不完整（src.png / anim.gif / src.jxr 三缺一）"
}
Write-Output ("素材: " + ((Get-ChildItem $val -File | ForEach-Object { "$($_.Name)=$($_.Length)" }) -join ' '))

# ── 各通路转换（jxr2png 是本次实测的现场；其余为防回归对照）──
function Run-Case([string]$name, [string]$inFile, [string]$fmt) {
    $d = "$val/$name"
    New-Item -ItemType Directory -Path $d -Force | Out-Null
    if (-not (Test-Path $inFile)) {
        $script:fail++
        Write-Output ("  FAIL {0,-10} 素材缺失：{1}" -f $name, $inFile)
        return
    }
    $argStr = "--headless -i `"$inFile`" --format $fmt --output `"$d`""
    Start-Process -FilePath $exe -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput "$d/out.txt" -RedirectStandardError "$d/err.txt" | Out-Null
    $out = Get-ChildItem -Path "$d/*" -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in @('.png', '.apng') } | Select-Object -First 1
    if (-not $out) { $script:fail++; Write-Output ("  FAIL {0,-10} 未产出 PNG/APNG" -f $name); return }
    $r = Test-PngStruct $out.FullName
    if ($r.Ok) { $script:pass++; Write-Output ("  PASS {0,-10} {1,-12} {2,7}B 结构完整" -f $name, $out.Name, $out.Length) }
    else { $script:fail++; Write-Output ("  FAIL {0,-10} {1,-12} —— {2}" -f $name, $out.Name, $r.Why) }
}

Write-Output "`n### PNG/APNG 产物结构 ###"
Run-Case 'png2png' "$val/src.png" 'png'
Run-Case 'jxr2png' "$val/src.jxr" 'png'      # ← 本次缺陷现场
Run-Case 'gif2apng' "$val/anim.gif" 'apng'

# ── 负控：手工去掉 -f apng 必须被判缺签名（证明上面的断言能识别该损坏）──
$neg = "$val/neg_apng_no_f.png"
Run-Exe $ff "-y -v error -i `"$val/src.bmp`" -c:v apng `"$neg`"" | Out-Null
$rn = Test-PngStruct $neg
if (-not $rn.Ok) {
    $pass++
    Write-Output ("  PASS {0,-10} 去掉 -f apng 被判为损坏（{1}）⇒ 断言有效" -f 'negctrl', $rn.Why)
}
else {
    $fail++
    Write-Output "  FAIL negctrl 去掉 -f apng 仍被判正常 ⇒ 本门禁识别不了该损坏，断言无效"
}

Write-Output "`n===== PASS=$pass FAIL=$fail SKIP=$skip ====="
# 成功即清理本次工作目录；失败则保留供排查（否则每跑一次就多一个 GUID 目录）
if ($fail -eq 0) { Remove-Item -Recurse -Force $val -ErrorAction SilentlyContinue }
else { Write-Output "工作目录保留供排查: $val" }
exit $(if ($fail -eq 0) { 0 } else { 1 })
