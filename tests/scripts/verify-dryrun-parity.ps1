# verify-dryrun-parity.ps1 —— `--dry-run` 预览 与 真实执行 的**同源性**门禁（2026-10-07 新增）
#
# ══════════════════════════════════════════════════════════════════════════════
# 立锁动机（**实测，不是假想**）
# ══════════════════════════════════════════════════════════════════════════════
# `--dry-run` 走的是 `MainWindow.BuildQueueItemCommand`，而真实执行走
# `QueueProcessor` / `RawService` 等**另一条路径** ⇒ 两者**不是同一份判据**。
# 实测两处**直接不一致**（2026-10-07）：
#   ① `-f jxr -e Jxr`
#        预览: `ffmpeg … -map_metadata -1 … "src8.jxr"`
#        真实: 调 **JxrEncApp**（`QueueProcessor.ProcessJxrAsync`），产物 `ffprobe` 报 `jpegxr`
#   ② `-f dng -e Dng --dng-bit-depth 8`
#        预览: `dngtool … -jxl -effort 7 -decode_speed 1`      （**漏 `-4`**）
#        真实: `RawService` 会发 `-4` ⇒ 产物字节数显著变化
#
# **为什么必须立这条锁**：任何**以 dry-run 为准**的审计/断言都会得到**假阴性** ——
#   teammate 明确指出：若只信 dry-run，会误报两条**不存在**的缺陷
#   （"dng-bit-depth 失效"、"JXR 后端没接上"）。
#   本仓已有同族教训（§6 第 13 条：「脚本并不驱动产品 exe ⇒ 证据力被高估」）。
#
# ⚠ 本锁**只断言"预览不得声称自己用了 A、实际却用 B"**这一件事，
#   **不要求**预览与真实命令行逐字相同（真实执行有临时路径/管道，逐字比会**假红**）。
#   判据取**可观测的稳定事实**：
#     · 预览里出现的**可执行名集合**必须 ⊆ 真实日志里出现的可执行名集合
#       （允许真实侧多 —— 它有中间步骤；**不允许**预览声称了一个真实没用到的 exe）
#     · 真实产物必须存在且非空（否则"预览对不对"无从谈起 ⇒ 记 SKIP 并点名，不给绿）
#
# 用法：pwsh -NoProfile -File tests/scripts/verify-dryrun-parity.ps1
# 退出码 0/1；末行 `DRYRUNPARITY RESULT: pass=n fail=m`
# ══════════════════════════════════════════════════════════════════════════════
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root

$exe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }
Write-Output ("[gate] exe=" + $exe + $(if ($exe -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
if (-not (Test-Path $exe)) { Write-Output 'FATAL: FfmpegGui.exe 不存在'; Write-Output 'DRYRUNPARITY RESULT: pass=0 fail=1'; exit 1 }

$ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
$work = Join-Path $root ("tests/output/drypar_" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work -Force | Out-Null

$pass = 0; $fail = 0; $skip = 0
function CK([bool]$ok, [string]$msg) {
    if ($ok) { Write-Output "PASS $msg"; $script:pass++ }
    else     { Write-Output "FAIL $msg"; $script:fail++ }
}
function SKIPX([string]$msg) { Write-Output "SKIP $msg"; $script:skip++ }

# 素材：8bit PNG（覆盖 jxr 臂）
$src = Join-Path $work 'src8.png'
& $ff -y -hide_banner -loglevel error -f lavfi -i "testsrc2=s=320x240:d=1" -frames:v 1 $src 2>&1 | Out-Null
if (-not (Test-Path $src)) { Write-Output 'FATAL: 素材生成失败'; Write-Output 'DRYRUNPARITY RESULT: pass=0 fail=1'; exit 1 }

# ── 从一段文本里抽「可执行名」集合 ──
# 只认**行首/空格后**的已知工具名，避免把路径里的子串当工具。
function Get-ExeNames([string]$text) {
    $known = @('ffmpeg', 'ffprobe', 'cjxl', 'djxl', 'cjpegli', 'JxrEncApp', 'JxrDecApp', 'dngtool', 'avifenc', 'exiftool')
    $found = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($line in ($text -split "`r?`n")) {
        foreach ($k in $known) {
            if ($line -match ('(^|[\s"]|/)' + [regex]::Escape($k) + '(\.exe)?([\s"]|$)')) { [void]$found.Add($k) }
        }
    }
    return $found
}

# ── 从一段文本里抽某个工具的**整条命令**（实参串）──
# ⚠ 必须跳过**散文行**：真实日志里 `dngtool` 会以散文形式先出现
#   （如 `[RAW] dngtool 编码 DNG: foo.dng`），它不是命令行。
#   判据取「工具名之后紧跟 `-` 开头的实参」⇒ 散文行被自然排除（实测踩到过，见下）。
function Get-ToolArgs([string]$text, [string]$tool) {
    foreach ($line in ($text -split "`r?`n")) {
        # 已剥掉任意 [标签] 前缀后再判行首，避免把散文误当命令
        $bare = $line -replace '^\s*(\[[^\]]*\]\s*)+', ''
        if ($bare -match ('^' + [regex]::Escape($tool) + '(\.exe)?\s+(-.*)$')) {
            return $Matches[2].Trim()
        }
    }
    return $null
}

# ── 取"编码旋钮"集合：剥掉输出路径与输入路径，只留参数名+值 ──
# ⚠ 路径必须剥：预览写 `<preDir>`、真跑写 `<realDir>`，路径不同是**必然**的，比它会假红。
# ⚠ 形参**不能**叫 `$args`：它是 PowerShell 自动变量（实参数组），赋值会被吞掉
#   ⇒ 函数恒返回空串 ⇒ 断言**恒真（假绿）**。实测踩到过，故用 `$argStr`。
function Get-ArgKnobs([string]$argStr, [string]$tool) {
    $s = $argStr
    # 剥 -O "<path>" / -o "<path>"（输出路径，两臂必然不同）
    $s = $s -replace '\s-[Oo]\s+"[^"]*"', ' -O <path>'
    # 剥 -i "<path>"（输入路径可能带不同前缀，如 tests\ vs 绝对路径）
    $s = $s -replace '\s-i\s+"[^"]*"', ' -i <path>'
    # 归一多余空白
    $s = ($s -replace '\s+', ' ').Trim()
    return $s
}

# ── RAW 源夹具（dng 臂专用）──
# ⚠ dng 输出需要**传感器 RAW 数据**；普通 PNG 会被产品**正确拒绝**（「非 RAW 输入」）
#   ⇒ 拿 PNG 跑 dng 臂会因"真跑未产出"而 SKIP ⇒ 永远验不到实参漂移（假绿）。
# 夹具**必须在仓内且被 git 跟踪**：`tools/src/dng_sdk/**` 与 `tests/output/**` 都是 gitignored
#   （前者是手动下载的 SDK，后者是跑测产物目录）⇒ 两者都**不可**当长期夹具。
$dngSrc = Join-Path $root 'tests/fixtures/raw_src_8bit.dng'

# ── 逐臂：跑一次 dry-run + 一次真跑，比对「预览声称的 exe」是否都在真实里 ──
$arms = @(
    @{ Label = 'jxr'; Src = $src; Args = @('-f', 'jxr', '-e', 'Jxr') },
    @{ Label = 'png'; Src = $src; Args = @('-f', 'png') },
    @{ Label = 'jpg'; Src = $src; Args = @('-f', 'jpg') },
    # dng 臂：D-1 的**第②处**形态（`--dng-bit-depth 8` 漏 `-4`）。
    # ⚠ 缺夹具时**fail-closed**（下面对该臂点名 SKIP，不给绿）——不得因夹具缺失而静默变绿。
    @{ Label = 'dng'; Src = $dngSrc; Args = @('-f', 'dng', '-e', 'Dng', '--dng-bit-depth', '8') }
)

foreach ($a in $arms) {
    # ── fail-closed：dng 臂缺夹具 ⇒ 点名 SKIP（不给绿），不得静默跳过 ──
    if (-not (Test-Path $a.Src)) {
        SKIPX "$($a.Label)：夹具不存在 ($($a.Src)) ⇒ 该臂**未验证**、不计入通过；请补回 tests/fixtures/raw_src_8bit.dng"
        continue
    }
    $preDir = Join-Path $work ("$($a.Label)_pre")
    $realDir = Join-Path $work ("$($a.Label)_real")
    New-Item -ItemType Directory -Path $preDir -Force | Out-Null
    New-Item -ItemType Directory -Path $realDir -Force | Out-Null

    # ① dry-run
    $dryArgs = @('--headless', '--dry-run', '-i', $a.Src, '-o', $preDir) + $a.Args
    $dryOut = Join-Path $work "$($a.Label).dry.txt"
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    foreach ($x in $dryArgs) { $psi.ArgumentList.Add($x) }
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $psi.WorkingDirectory = $root
    $p = [System.Diagnostics.Process]::Start($psi)
    $so = $p.StandardOutput.ReadToEnd(); $se = $p.StandardError.ReadToEnd(); $p.WaitForExit()
    $dryTxt = $so + "`n" + $se
    Set-Content -Path $dryOut -Value $dryTxt -Encoding UTF8

    # ② 真跑（debug 级，让 [cmd]/工具命令行进日志）
    $realArgs = @('--headless', '-i', $a.Src, '-o', $realDir, '--log-level', 'debug') + $a.Args
    $realOut = Join-Path $work "$($a.Label).real.txt"
    $psi2 = New-Object System.Diagnostics.ProcessStartInfo
    $psi2.FileName = $exe
    foreach ($x in $realArgs) { $psi2.ArgumentList.Add($x) }
    $psi2.RedirectStandardOutput = $true; $psi2.RedirectStandardError = $true
    $psi2.UseShellExecute = $false
    $psi2.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi2.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $psi2.WorkingDirectory = $root
    $p2 = [System.Diagnostics.Process]::Start($psi2)
    $so2 = $p2.StandardOutput.ReadToEnd(); $se2 = $p2.StandardError.ReadToEnd(); $p2.WaitForExit()
    $realTxt = $so2 + "`n" + $se2
    Set-Content -Path $realOut -Value $realTxt -Encoding UTF8

    $prod = @(Get-ChildItem $realDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Length -gt 0 })
    if ($prod.Count -eq 0) {
        SKIPX "$($a.Label)：真跑未产出 ⇒ 预览与真实的对照无从判定（rc=$($p2.ExitCode)），本条**未验证**、不计入通过"
        continue
    }

    $preExes  = Get-ExeNames $dryTxt
    $realExes = Get-ExeNames $realTxt

    # ── 判据 A（多报）：预览声称用到的 exe，必须**每一个**都真的出现在真实日志里 ──
    # 形态：预览说"我要跑 X"，实际没跑 X ⇒ 用户被误导。
    $phantom = @($preExes | Where-Object { -not $realExes.Contains($_) })
    CK ($phantom.Count -eq 0) ("{0}：预览未声称真实没用到的工具（预览=[{1}] 真实=[{2}]{3}）" -f `
        $a.Label, (($preExes | Sort-Object) -join ','), (($realExes | Sort-Object) -join ','), `
        $(if ($phantom.Count) { "；⚠ 预览声称但真实未出现: " + ($phantom -join ',') } else { '' }))

    # ── 判据 B（少报）：真实用到的**编码器类**工具，必须出现在预览里 ──
    # ⚠ 这条是 D-1 的**真正形态**：`-f jxr -e Jxr` 的预览只写 `ffmpeg`，
    #   而真实核心编码器是 **JxrEncApp** ⇒ 用户从预览里**完全看不出**真实要跑什么。
    # ⚠ 只对**编码器类**工具判（cjxl/djxl/cjpegli/JxrEncApp/JxrDecApp/avifenc/dngtool）：
    #   `ffmpeg`/`ffprobe`/`exiftool` 是**辅助/后置**步骤（元数据恢复、探查），
    #   真实侧多出它们**不构成**预览缺陷 ⇒ 判它们会**假红**（实测：png 臂真实含 exiftool 而不在预览里）。
    $encoderTools = @('cjxl', 'djxl', 'cjpegli', 'JxrEncApp', 'JxrDecApp', 'avifenc', 'dngtool')
    $missingInPreview = @($realExes | Where-Object { $_ -in $encoderTools -and -not $preExes.Contains($_) })
    CK ($missingInPreview.Count -eq 0) ("{0}：真实用到的**编码器类**工具都出现在预览里（真实编码器=[{1}]{2}）" -f `
        $a.Label, (($realExes | Where-Object { $_ -in $encoderTools } | Sort-Object) -join ','), `
        $(if ($missingInPreview.Count) { "；⚠ 预览**少报**了: " + ($missingInPreview -join ',') + " ⇒ 用户看不出真实要跑什么" } else { '' }))

    # 反向自检：真实侧必须至少有一个工具（防"两边都空 ⇒ 恒真"的空断言）
    CK ($realExes.Count -gt 0) ("{0}：真实日志里确实解析到工具（{1} 个）—— 防两侧空集导致上一条恒真" -f $a.Label, $realExes.Count)

    # ── 判据 C（实参级）：预览与真实的命令**实参**必须逐字同源 ──
    # ⚠ 为什么必须加这条（2026-10-07，D-1 的第②处）：判据 A/B 只比**可执行名集合**，
    #   对 `-4` / `-jxlq` 这类**实参漂移**天然无感 —— 实测（Lead 复核在案）：
    #   把 dng 预览改成恒 16 位（=复现"漏 -4"）后本脚本仍 pass=10 fail=0 ⇒ **缺陷在、门禁全绿**，
    #   正是本仓最忌的**假阴性**。故对**外部编码器**（参数式工具）补一条实参级断言。
    # ⚠ 只对**参数式**工具判（dngtool/cjxl/cjpegli/avifenc）：
    #   JxrEncApp 走 `-i/-o` 但真实侧的中转路径是**运行时临时名**（`<tmp>/input.bmp`）
    #   ⇒ 预览只能给符号名，逐字比会**假红**；其"用哪个编码器"已由判据 B 锁住。
    $paramTools = @('dngtool', 'cjxl', 'cjpegli', 'avifenc')
    foreach ($pt in $paramTools) {
        if ($pt -notin $realExes -or $pt -notin $preExes) { continue }
        $preArgs  = Get-ToolArgs $dryTxt  $pt
        $realArgs = Get-ToolArgs $realTxt $pt
        if ($null -eq $preArgs -or $null -eq $realArgs) { continue }
        # 归一：把**输出路径**剥掉（预览与真跑写到不同目录，路径必然不同；比它只会假红），
        # 只比**编码旋钮**（pix/depth/effort/quality/compression/…），那才是 D-1 漂移的所在。
        $preKnobs  = Get-ArgKnobs $preArgs  $pt
        $realKnobs = Get-ArgKnobs $realArgs $pt
        CK ($preKnobs -eq $realKnobs) ("{0}：预览与真实的 {1} **实参逐字同源**（预览=[{2}] 真实=[{3}]{4}）" -f `
            $a.Label, $pt, $preKnobs, $realKnobs, `
            $(if ($preKnobs -ne $realKnobs) { "；⚠ 实参漂移 ⇒ 预览说的命令不是真实要跑的命令" } else { '' }))
    }
}

# ── 负控：判据必须能识别"预览声称了一个真实没用到的 exe" ──
# 用合成文本验证 Get-ExeNames + phantom 判据有牙（不依赖产品）。
$fakeePre  = "dngtool -e -i a.dng -O b.dng -jxl -effort 7"
$fakeReal  = "[色彩] JxrEncApp 命令行: -i x -o y"
$fp = @((Get-ExeNames $fakeePre) | Where-Object { -not (Get-ExeNames $fakeReal).Contains($_) })
CK ($fp.Count -eq 1 -and $fp[0] -eq 'dngtool') "负控：合成样本能识别'预览声称 dngtool、真实只用 JxrEncApp'（实得 phantom=[$($fp -join ',')]）"

# ── 负控（判据 C）：实参级断言必须有牙，且**不得**被散文行污染 ──
# 合成样本同时锁两件事：
#   ① 散文行（`dngtool 编码 DNG: foo`）不被当成命令行 ⇒ 提取器取到的是**真命令行**；
#   ② `-4` 的漂移**确实**能被 `Get-ArgKnobs` 比出来（这才是 D-1 第②处的形态）。
$cPre  = "dngtool -e -i `"a.dng`" -O `"p.dng`" -jxl -jxlq 0 -effort 7 -decode_speed 1"
$cReal = "[RAW] dngtool 编码 DNG: a.dng`n[RAW] dngtool -e -i `"a.dng`" -O `"r.dng`" -jxl -jxlq 0 -4 -effort 7 -decode_speed 1"
$cPreKnobs  = Get-ArgKnobs (Get-ToolArgs $cPre  'dngtool') 'dngtool'
$cRealKnobs = Get-ArgKnobs (Get-ToolArgs $cReal 'dngtool') 'dngtool'
CK ($cRealKnobs -like '*-4*') "负控C①：散文行不被误当命令行，提取到的是真命令行（真实侧含 -4，实得=[$cRealKnobs]）"
CK ($cPreKnobs -ne $cRealKnobs) "负控C②：`-4` 的实参漂移**确实**被比出（预览=[$cPreKnobs] ≠ 真实=[$cRealKnobs]）—— 防该断言恒真成假绿"

Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue

Write-Output ''
Write-Output ("DRYRUNPARITY RESULT: pass={0} fail={1}" -f $pass, $fail)
if ($fail -gt 0) { exit 1 } else { exit 0 }
