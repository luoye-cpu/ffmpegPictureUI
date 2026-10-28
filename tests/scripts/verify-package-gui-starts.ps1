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
#   ④ **关闭必须真退**：`CloseMainWindow` 后 `ExitGraceSec` 秒内退出；不退则强杀并 **WARN**
#      （这条不是产品缺陷判据，是"别让一轮门禁在桌面上留一扇窗"）；
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
function Start-GuiArm([string]$tag) {
    $o = "$d\$tag.o"; $e = "$d\$tag.e"
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
    [pscustomobject]@{ proc = $p; alive = $alive; handle = $handle; stderr = $e; stdout = $o }
}
# 收尾三态判读：退出 / 隐藏到托盘 / 关不动。
#   ⚠ 本产品的契约是"关窗即隐藏到托盘"（App.xaml.cs:199-205：Closing 里 !AllowFullExit ⇒ args.Cancel=true
#     + HideMainWindowToTray）⇒ "CloseMainWindow 后进程还在"**不是缺陷**，早期版本把它报成 WARN 是
#     判据期待写错（16:2x 实测 -ExitGraceSec 25 仍不退，读源码才对上）。三态各给各的读数。
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
    $arm = Start-GuiArm 'gui'
    $code = ''
    if (-not $arm.alive) { try { $code = '0x{0:X8}' -f $arm.proc.ExitCode } catch { $code = '?' } }
    CK 'gui 存活：包内/开发件 exe 不带 --headless 必须活过观察窗' $arm.alive `
       $(if ($arm.alive) { "存活 ${AliveSec}s（NativeAOT 的编译 XAML 载荷在位）" }
         else { "exit=$code 且 stderr 首条：$(First-Exception $arm.stderr)" })
    if ($arm.alive) {
        if ($arm.handle -ne 0) { CK 'gui 主窗口句柄非 0' $true "MainWindowHandle=0x$('{0:X}' -f $arm.handle.ToInt64())" }
        else { WK 'gui 主窗口句柄' '观察窗内句柄仍为 0 ⇒ 无交互桌面时属预期，不判红（存活判据才是本条的牙）' }
        [void]$arm.proc.CloseMainWindow()
        Start-Sleep -Seconds 4
        $arm.proc.Refresh()
        if ($arm.proc.HasExited) {
            CK 'gui 收尾：关窗即退（AllowFullExit 档）' $true ("自然退出 exit=0x{0:X8}" -f $arm.proc.ExitCode)
        } elseif ($arm.proc.MainWindowHandle -eq 0) {
            CK 'gui 收尾：窗口已隐藏到托盘（本产品的关窗契约，不是关不掉）' $true ("pid=" + $arm.proc.Id + " 仍在但主窗口句柄归 0")
        } else {
            WK 'gui 收尾' ("CloseMainWindow 后窗口句柄仍非 0 ⇒ 关窗请求没生效（pid=" + $arm.proc.Id + "），已强杀")
        }
    }
} finally {
    if ($arm -and -not $arm.proc.HasExited) { Stop-Process -Id $arm.proc.Id -Force -ErrorAction SilentlyContinue }
    Get-Process FfmpegGui -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } |
        ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
}

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
