# verify-package-gui-starts.ps1 —— **出货包/开发件的 GUI 必须真能把窗口起起来**
#
# 这条为什么必须存在（2026-10-28 实况）：v1.6.0-beta10 出包后 `verify-release-package` 16/0、
# L3 五套件 17/0·100/0·28/0·24/0·19/0 **全绿**，而维护者双击包内 exe **一键都打不开**——
# 包内件在 `FfmpegGui.App.Initialize()` 抛
# `Avalonia.Markup.Xaml.XamlLoadException: No precompiled XAML found for FfmpegGui.App` 后以
# 0xc0000409 自杀。根因是 NativeAOT 产物里**没有 Avalonia 的编译 XAML 载荷**（`!AvaloniaResources`
# / `!XamlIlPopulate` 在 beta9/beta10 的 exe 字节里 0 命中，beta8 的 exe 里有）。
# ⇒ 全仓 84 条门禁**没有一条**把包内 exe 当"要开窗口的程序"起过一次：L3 一律 `--headless`，
#   而 headless 路径根本不碰 Skia/XAML。这条补的就是那个盲区。
#
# 判据（两档被测，与其余 L3 同一约定：默认 Release 产物 ⇒ 每轮跑；`$env:PKG_VER` ⇒ 打出货包）：
#   ① 被测在位且版本串相符（两档都缺/不符 ⇒ exit 2，不产出读数，绝不回退 Debug）；
#   ② **存活**：不带 `--headless` 起进程，观察 `AliveSec` 秒内不退 ⇒ PASS；退了 ⇒ FAIL，
#      并把 stderr 里首条异常类型/消息点名（"死在哪"必须写在读数里，不能只报"起不来"）；
#   ③ **窗口句柄**：轮询 `MainWindowHandle` 非 0 ⇒ PASS；到点仍为 0 ⇒ **WARN 不判红**
#      （无人值守会话/无交互桌面时窗口本就来不到前台，拿它判红就是一台假红机器）；
#   ④ **关窗行为与设置一致**：默认档（`CloseToTray=true`）关窗**应当**隐藏到托盘——进程在、
#      主窗口句柄归 0，这是产品契约；窗口句柄仍非 0 ⇒ WARN 并强杀（「别在桌面上留一扇窗」）；
#      ⚠ 2026-10-31 实况：托盘图标 `IsVisible` 曾被写死 `false`，唯一的退出入口挂在**一枚默认不亮的
#        图标**上 ⇒ 用户侧表现是「托盘没图标 + 关窗关不掉」，而本门禁当时把「关窗后进程还在」
#        直接当契约记下了 ⇒ 契约本身没被任何人验过。⑥ 补的就是那半条；
#   ⑥ **托盘图标真的出现（运行时自证）**：`tray.log` 必须有 `create=ok` 且 `IsVisible=True`。
#      日记由 `App.LogTray` 落盘到 `FFMPEGGUI_DIAG_DIR`（本门禁按臂点名）。这是全仓**唯一**
#      「托盘图标在运行时真被点亮」的判据——静态断言只能查资源存在/位图可解码，
#      查不出「窗口起来时图标可见」；
#   ⑦ **托盘设关时关窗必须真退**：第二臂 `FFMPEGGUI_CLOSE_TO_TRAY=false` ⇒ 关窗后
#      `ExitGraceSec` 内必须退出（托盘不常驻就不许把用户锁进不可见进程）；
#   ⑤ **负控（证明②这把尺活着）**：拿同一份 exe 以 `--headless -i <不存在的输入>` 起——它必然
#      很快退出 ⇒ 此时②的存活判据**必须**报"不存活"；若它报"存活"，说明观测根本没生效 ⇒ 本条判红。
# ⚠ 只读 `MainWindowHandle`，**绝不读 `MainWindowTitle`**：后者会向目标进程发消息取文本，
#   进程不响应时调用方直接挂死（本仓 2026-10-28 实测连挂两次，每次 60~180 秒）。
# 退出码：0 = 全绿（允许 WARN），1 = 有失败，2 = 被测不在位/版本不符。
# ⚠ 双宿主兼容（PS 7 / PS 5.1）：禁三元 `? :` / `??` / `?.` / `&&` / `||`；双引号串内禁全角弯引号（用 「」）。
param(
    [int]$AliveSec = 12,
    [int]$HandlePollSec = 12,
    [int]$ExitGraceSec = 6
)
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$d = "$root\tests\output\validate\gui_start"
if (Test-Path $d) { Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $d | Out-Null
# 临时件钉进产物区：沙箱会按进程拦 %TEMP% 写（本仓反复踩过的成片假红源头）
$env:TMP = $d; $env:TEMP = $d; $env:TMPDIR = $d
# 托盘/关窗链路的诊断落盘目录（App.LogTray 的第一优先级；按臂再指子目录）
$env:FFMPEGGUI_DIAG_DIR = $d

if ($env:PKG_VER) {
    $pkgVer = $env:PKG_VER
    $pkg = "$root\publish\build\FFmpegPictureUI-v$pkgVer-x64-full"
    $exe = "$pkg\FfmpegGui.exe"
    if (-not (Test-Path $exe)) { "FAIL 被测包不在位：$exe ⇒ 先跑 pack.ps1 -Mode full，或用 PKG_VER 指到实际发包版本"; exit 2 }
    $pv = (Get-Item $exe).VersionInfo.ProductVersion
    if ($pv -notlike "$pkgVer*") { "FAIL 包内版本串与期望不符：期望 $pkgVer* 实得 $pv（$exe）"; exit 2 }
    "SUBJ(被测) = v$pkgVer (L3 出货包)  ProductVersion=$pv"
} else {
    $exe = "$root\src\FfmpegGui\bin\Release\net11.0\win-x64\FfmpegGui.exe"
    if (-not (Test-Path $exe)) { "FAIL Release 被测产物不在位：$exe ⇒ 先构建 Release；本门禁不回退 Debug"; exit 2 }
    $pv = (Get-Item $exe).VersionInfo.ProductVersion
    "SUBJ(被测) = Release 开发产物 (L2+)  ProductVersion=$pv"
}
$sha16 = (Get-FileHash $exe -Algorithm SHA256).Hash.Substring(0, 16)
"exe=$exe  sha16=$sha16  存活观察=${AliveSec}s  句柄轮询=${HandlePollSec}s"

$script:R = @()
function CK($name, $ok, $detail) {
    $script:R += [pscustomobject]@{ n = $name; ok = [bool]$ok }
    '{0}  {1}   {2}' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail
}
function WK($name, $detail) {
    Write-Output ('WARN  ' + $name + '   ' + $detail)
}
# 起 GUI 档（不带 --headless）⇒ 存活 + 句柄
function Start-GuiArm([string]$tag, [string]$closeToTray) {
    $o = "$d\$tag.o"; $e = "$d\$tag.e"
    $diag = "$d\$tag"
    New-Item -ItemType Directory -Force -Path $diag | Out-Null
    $env:FFMPEGGUI_DIAG_DIR = $diag
    if ($closeToTray) { $env:FFMPEGGUI_CLOSE_TO_TRAY = $closeToTray }
    else { Remove-Item Env:\FFMPEGGUI_CLOSE_TO_TRAY -ErrorAction SilentlyContinue }
    $p = Start-Process -FilePath $exe -PassThru `
         -RedirectStandardOutput $o -RedirectStandardError $e
    $alive = -not $p.WaitForExit($AliveSec * 1000)
    $handle = 0
    if ($alive) {
        $deadline = (Get-Date).AddSeconds($HandlePollSec)
        while ((Get-Date) -lt $deadline) {
            $p.Refresh(); $handle = $p.MainWindowHandle
            if ($handle -ne 0) { break }
            if ($p.HasExited) { break }
            Start-Sleep -Milliseconds 300
        }
        if ($handle -eq 0) { $p.Refresh(); $handle = $p.MainWindowHandle }
    }
    [pscustomobject]@{ proc = $p; alive = $alive; handle = $handle; stderr = $e; stdout = $o; diag = $diag }
}
# 收尾三态判读：退出 / 隐藏到托盘 / 关不动。
#   ⚠ 关窗行为自 2026-10-31 起是**三态**（App.xaml.cs:331-346，旧的无条件拦截已废）：
#     ① AllowFullExit（已走退出链）⇒ 放行，进程必须退；
#     ② CloseToTray && 托盘可用 ⇒ args.Cancel=true + 隐藏到托盘 ⇒ "CloseMainWindow 后进程还在"属**预期**，
#        由 ⑥ 的 tray.log（create=ok IsVisible=True）自证，不在这里判红；
#     ③ 其余（用户没选托盘 / 托盘起不来）⇒ 真退出 ⇒ ⑦ 断言必须在 ExitGraceSec 内退干净。
#   本函数只产读数，三态各给各的。
function First-Exception([string]$path) {
    if (-not (Test-Path $path)) { return '' }
    $t = @(Get-Content $path -ErrorAction SilentlyContinue)
    $m = $t | Where-Object { $_ -match 'Exception|Unhandled|故障|未处理' } | Select-Object -First 1
    if ($m) { return $m.Trim() }
    if ($t.Count -gt 0) { return ($t[0]).Trim() }
    return ''
}

# ── ②③④ GUI 档 ────────────────────────────────────────────────────────
$arm = $null
try {
    $arm = Start-GuiArm 'gui' 'true'
    $code = ''
    if (-not $arm.alive) { try { $code = '0x{0:X8}' -f $arm.proc.ExitCode } catch { $code = '?' } }
    CK 'gui 存活：包内/开发件 exe 不带 --headless 必须活过观察窗' $arm.alive `
       $(if ($arm.alive) { "存活 ${AliveSec}s（NativeAOT 的编译 XAML 载荷在位）" }
         else { "exit=$code 且 stderr 首条：$(First-Exception $arm.stderr)" })
    if ($arm.alive) {
        if ($arm.handle -ne 0) { CK 'gui 主窗口句柄非 0' $true "MainWindowHandle=0x$('{0:X}' -f $arm.handle.ToInt64())" }
        else { WK 'gui 主窗口句柄' '观察窗内句柄仍为 0 ⇒ 无交互桌面时属预期，不判红（存活判据才是本条的牙）' }
        # ⑥ 托盘图标真的出现（运行时自证）：静态断言查不出这一条
        $trayLog = "$($arm.diag)\tray.log"
        $trayTxt = ''
        if (Test-Path $trayLog) { $trayTxt = (@(Get-Content $trayLog -ErrorAction SilentlyContinue) -join ' | ') }
        $trayOk = ($trayTxt -match 'create=ok') -and ($trayTxt -match 'IsVisible=True')
        CK '⑥ 启动即常驻：托盘图标被点亮（tray.log create=ok + IsVisible=True）' $trayOk `
           $(if ($trayOk) { ($trayTxt -split ' \| ')[0] }
             else { "tray.log=" + $(if ($trayTxt) { $trayTxt } else { '（缺失：App.LogTray 没落盘 或 托盘创建被跳过）' }) })
        [void]$arm.proc.CloseMainWindow()
        Start-Sleep -Seconds 4
        $arm.proc.Refresh()
        if ($arm.proc.HasExited) {
            # CloseToTray=true 且托盘可用 ⇒ 关窗本该隐藏，退出说明拦截没生效（设置/托盘链路断了）
            CK '④ 默认档关窗：应隐藏到托盘继续跑（队列不断），实测却退出了' $false ("exit=0x{0:X8}" -f $arm.proc.ExitCode)
        } elseif ($arm.proc.MainWindowHandle -eq 0) {
            CK '④ 默认档关窗：隐藏到托盘（进程在、主窗口句柄归 0）' $true ("pid=" + $arm.proc.Id + " 仍在")
        } else {
            WK 'gui 收尾' ("CloseMainWindow 后窗口句柄仍非 0 ⇒ 关窗请求没生效（pid=" + $arm.proc.Id + "），已强杀")
        }
    }
} finally {
    if ($arm -and -not $arm.proc.HasExited) { Stop-Process -Id $arm.proc.Id -Force -ErrorAction SilentlyContinue }
    Get-Process FfmpegGui -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } |
        ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
}

# ── ⑦ 第二臂：托盘设关（CLOSE_TO_TRAY=false）⇒ 关窗必须真退 ───────────────
#   这一臂钉的是「不许把用户锁进不可见进程」：托盘不常驻时，关窗是唯一出口，
#   它必须真的结束进程，而不是又被 args.Cancel 拦下藏起来。
$arm2 = $null
try {
    $arm2 = Start-GuiArm 'gui_exit' 'false'
    $arm2Log = "$($arm2.diag)\tray.log"
    $arm2Txt = ''
    if (Test-Path $arm2Log) { $arm2Txt = (@(Get-Content $arm2Log -ErrorAction SilentlyContinue) -join ' | ') }
    CK '⑦ 托盘设关：启动时图标不点亮（IsVisible=False）' ($arm2Txt -match 'IsVisible=False') $arm2Txt
    if ($arm2.alive) {
        [void]$arm2.proc.CloseMainWindow()
        $gone = $arm2.proc.WaitForExit($ExitGraceSec * 1000)
        if (-not $gone) { $arm2.proc.Refresh() }
        CK ("⑦ 托盘设关：关窗后 ${ExitGraceSec}s 内必须退出（不许藏进不可见进程）") $gone `
           $(if ($gone) { "已退出 exit=0x{0:X8}" -f $arm2.proc.ExitCode } else { "pid=" + $arm2.proc.Id + " 仍在且未退出" })
    } else {
        CK '⑦ 托盘设关：第二臂必须能起起来' $false '进程未存活，本臂无读数'
    }
} finally {
    if ($arm2 -and -not $arm2.proc.HasExited) { Stop-Process -Id $arm2.proc.Id -Force -ErrorAction SilentlyContinue }
}
Remove-Item Env:\FFMPEGGUI_CLOSE_TO_TRAY -ErrorAction SilentlyContinue

# ── ⑤ 负控：同一份 exe 走"必然立刻退出"的形态，②的尺必须量出"不存活" ───────
$neg = "$d\neg.o"; $nge = "$d\neg.e"
$nope = "$d\no_such_input.png"
$np = Start-Process -FilePath $exe -PassThru -NoNewWindow `
      -ArgumentList @('--headless', '-i', $nope, '-o', "$d\neg_out", '-f', 'jpg') `
      -RedirectStandardOutput $neg -RedirectStandardError $nge
$negDied = $np.WaitForExit(($AliveSec * 1000))
CK '负控：headless + 不存在的输入必须很快退出（否则②的存活尺是死的）' $negDied `
   $(if ($negDied) { "已退出 exit=0x{0:X8}" -f $np.ExitCode } else { "观察窗 ${AliveSec}s 内未退出 ⇒ 本条判红" })
if (-not $negDied) { Stop-Process -Id $np.Id -Force -ErrorAction SilentlyContinue }

$pass = @($script:R | Where-Object { $_.ok }).Count
$fail = @($script:R | Where-Object { -not $_.ok }).Count
"===== gui-starts: PASS=$pass FAIL=$fail ====="
if ($fail -gt 0) { exit 1 }
exit 0
