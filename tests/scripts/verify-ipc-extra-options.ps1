# verify-ipc-extra-options.ps1 -- IPC 透传轴回归锁（P1-C，2026-09-19 新增）
#
# 背景（定性结论：**漏接线**，不是有意留空）：
#   `IpcService.SubmitJobPayload.ExtraOptions`（IpcService.cs:67）声明了却从未被应用 ——
#   `HandleSubmitJob` 只取 Format/EncoderBackend/Quality 建 FfmpegOptions（:337-342）。
#   对照 `StatusPayload` 的三个计数：作者**显式**写了注释说明"拿不到 / 保留 0"（:381-397），
#   而 ExtraOptions 处**一个字都没有** ⇒ 一处登记、一处漏登记，性质不同。
#   修复：让 IPC 复用 CLI 的 key->字段映射 `CliParser.ApplyExtraOption`（同一套语义，不另写平行表）。
#
# 本脚本锁什么（全部是**行为**断言：真实命名管道 + 真实 GUI 实例 + 真实产物 + ffprobe 回读）：
#   A1  就绪：Ping 往返拿到 pong
#   B*  正控 1：ExtraOptions { chroma = 4:2:0 }（jpg）⇒ 受理 + 产物 pix_fmt = yuvj420p
#   C*  正控 2：ExtraOptions { chroma = 4:2:2 }（jpg）⇒ 受理 + 产物 pix_fmt = yuvj422p
#   D*  正控 3：ExtraOptions { bit-depth = 16 }（png）⇒ 受理 + 产物 pix_fmt 为 rgb48*
#   E*  向后兼容：**不带** ExtraOptions 键（jpg）⇒ 受理 + pix_fmt = yuvj444p（= 产品默认）
#   F*  向后兼容：ExtraOptions = **空字典**（jpg）⇒ 同上（空 != 缺失，两条都要锁）
#   G*  取值口径：color-gamut-map = bogus ⇒ 必须**显式失败**且消息点名该 key，且不产出文件
#   H1  settings.json 的 EnableIpcServer 恢复原值
#
# ⚠ 期望值全部是**本机实测**的，不是照抄文档 —— 记下来免得后人"改回去"：
#   · png(8bit rgb24) --默认--> jpg 的 pix_fmt 是 **yuvj444p**（不是 420！）。
#     Chroma="auto" 时不写 -pix_fmt，mjpeg 编码器对 rgb24 输入自选 444。
#     所以"chroma 轴生效"的判据必须用**显式非默认值**：4:2:0 -> yuvj420p / 4:2:2 -> yuvj422p。
#     曾经按"设 4:4:4 应得到 444"写断言 ⇒ 修复前后**都**是 444 ⇒ 该断言**没有牙**（实测踩到）。
#   · png 默认 -> rgb24；bit-depth=16 -> rgb48be（ffprobe 报存储端序，故只断言 rgb48 前缀）。
#   · 映射见 ImageEncoderArgs.MapPixFmt:486-513；key->字段见 CliParser.cs:542-543。
#
# 有牙（变异验证已做）：删掉 HandleSubmitJob 里的 ApplyExtraOption 调用 ⇒
#   B3/C3/D3 转红（产物退回默认）、G1/G2 转红（非法值不再被拒）；E3/F3 仍绿（它们锁的是"没设"）。
#
# ⚠ 需要真实 GUI 实例：脚本临时把 exe 同目录 settings.json 的 EnableIpcServer 改成 true，
#   在 finally 里按原文恢复。跑之前**不能有**正在运行的 FfmpegGui 实例（管道名按用户 SID 派生）。
# ⚠ 管道客户端必须用 ReadAsync 读；实测 `DataAvailable` 轮询会让对端 Write 报 Pipe is broken。
# 用法：pwsh -NoProfile -File tests/scripts/verify-ipc-extra-options.ps1
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$exeDir = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64"
$exe = "$exeDir/FfmpegGui.exe"
$settingsPath = "$exeDir/settings.json"
$val = "$root/tests/output/ipcextra_$([guid]::NewGuid().ToString('N').Substring(0, 8))"

$pass = 0; $fail = 0
function Say($t) { Write-Output $t }
function CK($ok, $msg) { if ($ok) { $script:pass++; Say "  PASS $msg" } else { $script:fail++; Say "  FAIL $msg" } }

$settingsBackup = $null
$proc = $null

# ── 前置条件（缺一即失败，不做静默跳过）──
if (-not (Test-Path $exe)) { Say "FAIL 未找到构建产物: $exe（先 dotnet build src/FfmpegGui/FfmpegGui.csproj -c Release）"; Say "IPCEXTRA RESULT: pass=0 fail=1"; exit 1 }
if (-not (Test-Path $settingsPath)) { Say "FAIL 未找到 $settingsPath"; Say "IPCEXTRA RESULT: pass=0 fail=1"; exit 1 }
$running = @(Get-Process FfmpegGui -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Say "FAIL 已有 $($running.Count) 个 FfmpegGui 实例在跑（管道名按用户 SID 派生，会串台）：$($running.Id -join ',')"
    Say "IPCEXTRA RESULT: pass=0 fail=1"; exit 1
}

New-Item -ItemType Directory -Path "$val/out" -Force | Out-Null
$logPath = "$val/run.log"
function L($m) { [System.IO.File]::AppendAllText($logPath, ('[{0:HH:mm:ss.fff}] {1}' -f (Get-Date), $m) + "`r`n") }
L "start val=$val"

# ── ffmpeg / ffprobe：优先取 settings.json 的 FfmpegDirectory，退回 exe 同目录 PLAN ──
$cfgText = [System.IO.File]::ReadAllText($settingsPath)
$ffDir = ''
$m = [regex]::Match($cfgText, '"FfmpegDirectory"\s*:\s*"((?:[^"\\]|\\.)*)"')
if ($m.Success) { $ffDir = $m.Groups[1].Value -replace '\\\\', '\' }
$ffmpeg = "$ffDir/ffmpeg.exe"
if (-not (Test-Path $ffmpeg)) { $ffDir = "$exeDir/PLAN/ffmpeg-full"; $ffmpeg = "$ffDir/ffmpeg.exe" }
$ffprobe = "$ffDir/ffprobe.exe"
if (-not (Test-Path $ffmpeg)) { Say "FAIL 未找到 ffmpeg（settings.json 的 FfmpegDirectory 与 $exeDir/PLAN/ffmpeg-full 都不可用）"; Say "IPCEXTRA RESULT: pass=0 fail=1"; exit 1 }
if (-not (Test-Path $ffprobe)) { Say "FAIL 未找到 ffprobe: $ffprobe"; Say "IPCEXTRA RESULT: pass=0 fail=1"; exit 1 }
L "ffmpeg=$ffmpeg"

# ── 源素材：8-bit RGB PNG（8-bit 才能让 chroma 轴落到 yuvj 系列）──
$src = "$val/src.png"
Start-Process -FilePath $ffmpeg -ArgumentList "-y -v error -f lavfi -i testsrc=size=64x64:rate=1 -frames:v 1 `"$src`"" -Wait -NoNewWindow -PassThru -RedirectStandardOutput "$val/_ff.out" -RedirectStandardError "$val/_ff.err" | Out-Null
if (-not (Test-Path $src)) { Say "FAIL 造源失败：$([System.IO.File]::ReadAllText("$val/_ff.err"))"; Say "IPCEXTRA RESULT: pass=0 fail=1"; exit 1 }
L "src ok"

# ── 管道客户端（ReadAsync 读，带硬超时）──
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$pipeName = "ffmpegpictureui-$sid"

function Read-IpcLine($client, [int]$timeoutSec) {
    $sb = New-Object System.Text.StringBuilder
    $buf = New-Object byte[] 8192
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $remain = [int][math]::Max(200, ($deadline - (Get-Date)).TotalMilliseconds)
        $t = $client.ReadAsync($buf, 0, $buf.Length)
        if (-not $t.Wait($remain)) { return $null }
        $n = $t.Result
        if ($n -le 0) { return $null }
        [void]$sb.Append([System.Text.Encoding]::UTF8.GetString($buf, 0, $n))
        $s = $sb.ToString()
        $i = $s.IndexOf("`n")
        if ($i -ge 0) { return $s.Substring(0, $i) }
    }
    return $null
}

# 一次请求 = 一条新连接（服务端每接受一条连接就新建管道实例，短连接最不容易踩到状态残留）
function Invoke-Ipc([int]$type, $payloadJson, [int]$timeoutSec) {
    $c = $null
    try {
        $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
        $c.Connect(3000)
    } catch {
        if ($c) { try { $c.Dispose() } catch { } }
        return $null
    }
    try {
        $body = @{ Id = 100 + $type; Type = $type; Payload = $payloadJson }
        $line = ($body | ConvertTo-Json -Compress -Depth 8) + "`n"
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($line)
        $c.Write($bytes, 0, $bytes.Length)
        $c.Flush()
        $respLine = Read-IpcLine $c $timeoutSec
        if ($null -eq $respLine) { return $null }
        return ($respLine | ConvertFrom-Json)
    } catch {
        return $null
    } finally {
        if ($c) { try { $c.Dispose() } catch { } }
    }
}

function Wait-File([string]$path, [int]$seconds) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $seconds -and -not (Test-Path $path)) { Start-Sleep -Milliseconds 250 }
    return (Test-Path $path)
}

function Get-PixFmt([string]$file) {
    Start-Process -FilePath $ffprobe -ArgumentList "-v error -select_streams v:0 -show_entries stream=pix_fmt -of default=nw=1:nk=1 `"$file`"" -Wait -NoNewWindow -RedirectStandardOutput "$val/_probe.out" -RedirectStandardError "$val/_probe.err" | Out-Null
    return ([System.IO.File]::ReadAllText("$val/_probe.out")).Trim()
}

# 提交一个任务；$extra 传 $null 表示"不带 ExtraOptions 键"，传 @{} 表示"空字典"
function Submit-Job([string]$tag, [string]$fmt, $extra) {
    $outDir = "$val/out/$tag"
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    $inner = [ordered]@{
        InputPaths = @($src); OutputDirectory = $outDir; Format = $fmt; EncoderBackend = 0
        Quality = 90; Concurrency = 1; PreserveStructure = $false
    }
    if ($null -ne $extra) { $inner['ExtraOptions'] = $extra }
    $resp = Invoke-Ipc 0 ($inner | ConvertTo-Json -Compress -Depth 8) 30
    L "submit $tag fmt=$fmt extra=$($extra | ConvertTo-Json -Compress) -> Success=$($resp.Success) Msg=$($resp.Message)"
    return @{ Resp = $resp; OutDir = $outDir; File = "$outDir/src.$fmt" }
}

# 一次"正控"：受理 + 落盘 + pix_fmt 命中
function Check-Axis([string]$prefix, [string]$tag, [string]$fmt, $extra, [string]$expectPattern, [string]$expectDesc) {
    $r = Submit-Job $tag $fmt $extra
    CK ($r.Resp -and $r.Resp.Success) "${prefix}1 $tag 被受理（Success=$($r.Resp.Success)「$($r.Resp.Message)」）"
    $got = Wait-File $r.File 90
    CK $got "${prefix}2 $tag 产物落盘（$($r.File)）"
    if ($got) {
        $pf = Get-PixFmt $r.File
        L "$tag pix_fmt=$pf"
        CK ($pf -match $expectPattern) "${prefix}3 $tag 透传轴生效：pix_fmt=$pf（期望 $expectDesc；不生效会退回默认）"
    }
}

try {
    # ── 临时打开 IPC 服务器（改 exe 同目录 settings.json，finally 恢复）──
    $settingsBackup = $cfgText
    $patched = [regex]::Replace($cfgText, '"EnableIpcServer"\s*:\s*(true|false)', '"EnableIpcServer": true')
    if ($patched -eq $cfgText) { CK $false "settings.json 里找不到 EnableIpcServer 键，无法开启 IPC 服务器" }
    [System.IO.File]::WriteAllText($settingsPath, $patched, (New-Object System.Text.UTF8Encoding($false)))
    L "settings patched (changed=$($patched -ne $cfgText))"

    # ⚠ 必须 Redirect*：GUI 子进程会继承调用方的 stdout（实测：它会把自己的工具探测输出，
    #   如 cjpegli 的 usage，直接喷进门禁日志；更糟的是它会让**调用方**的 stdout 管道
    #   一直不关闭 —— 用 `& script | Out-String` 这种写法时表现为"脚本跑完了却不返回"）。
    $proc = Start-Process -FilePath $exe -WorkingDirectory $exeDir -ArgumentList '--no-gpu' -PassThru `
        -RedirectStandardOutput "$val/_gui.out" -RedirectStandardError "$val/_gui.err"
    L "gui pid=$($proc.Id)"

    # ── A1 就绪门：Ping 重试直到拿到 pong（GUI 启动 + UI 线程可用）──
    $pong = $null
    $t0 = Get-Date
    for ($i = 1; $i -le 40; $i++) {
        Start-Sleep -Milliseconds 700
        $r = Invoke-Ipc 6 $null 3
        if ($r -and $r.Success -and $r.Message -eq 'pong') { $pong = $r; break }
    }
    $elapsed = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
    L "readiness attempts=$i elapsed=${elapsed}s"
    CK ($null -ne $pong) "A1 Ping 往返拿到 pong（服务端就绪，${elapsed}s）"
    if ($null -eq $pong) { throw "IPC 服务端未就绪（GUI 起了吗？settings.json 的 EnableIpcServer 生效了吗？）" }

    # ── B/C/D 正控：三条不同轴的正控 ──
    Check-Axis 'B' 'chroma420' 'jpg' @{ 'chroma' = '4:2:0' } '^yuvj420p$' 'yuvj420p'
    Check-Axis 'C' 'chroma422' 'jpg' @{ 'chroma' = '4:2:2' } '^yuvj422p$' 'yuvj422p'
    Check-Axis 'D' 'bd16' 'png' @{ 'bit-depth' = '16' } '^rgb48' 'rgb48*'

    # ── E/F 向后兼容：缺失键 / 空字典 都要与修复前逐字一致（= 产品默认 yuvj444p）──
    Check-Axis 'E' 'nokey' 'jpg' $null '^yuvj444p$' '产品默认 yuvj444p'
    Check-Axis 'F' 'emptydict' 'jpg' @{} '^yuvj444p$' '产品默认 yuvj444p'

    # ── G 取值口径：非法 color-gamut-map 必须显式拒绝（与 CLI 同口径，不得静默忽略）──
    $bad = Submit-Job 'bad' 'jpg' @{ 'color-gamut-map' = 'bogus' }
    CK ($bad.Resp -and (-not $bad.Resp.Success)) "G1 非法 color-gamut-map 被**显式拒绝**（Success=$($bad.Resp.Success)；静默忽略会报 true）"
    CK ($bad.Resp -and ($bad.Resp.Message -match 'color-gamut-map')) "G2 拒绝消息点名了出错的 key（「$($bad.Resp.Message)」）"
    Start-Sleep -Seconds 3
    CK (-not (Test-Path $bad.File)) "G3 被拒绝的任务没有偷偷入队产出文件"
} catch {
    Say "  FAIL 异常中断: $($_.Exception.Message)"
    $fail++
    L "exception: $($_.Exception.Message)"
} finally {
    if ($proc -and -not $proc.HasExited) { & taskkill /PID $proc.Id /T /F 2>&1 | Out-Null }
    if ($null -ne $settingsBackup) { [System.IO.File]::WriteAllText($settingsPath, $settingsBackup, (New-Object System.Text.UTF8Encoding($false))) }
    L "cleanup done (settings restored)"
}

$restored = [regex]::Match([System.IO.File]::ReadAllText($settingsPath), '"EnableIpcServer"\s*:\s*(true|false)').Groups[1].Value
CK ($restored -eq 'false') "H1 settings.json 的 EnableIpcServer 已恢复原值（实为 $restored）"

Say ""
Say "===== ipc-extra-options: PASS=$pass FAIL=$fail ====="
Say "IPCEXTRA RESULT: pass=$pass fail=$fail"
if ($fail -eq 0) {
    Remove-Item -Recurse -Force $val -ErrorAction SilentlyContinue
    exit 0
}
Say "工作目录保留供排查: $val"
exit 1
