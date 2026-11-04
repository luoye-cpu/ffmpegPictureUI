# ═══════════════════════════════════════════════════════════════════════════
# verify-tool-detection.ps1 — 第 95 条门禁（2026-11-04 检测一致性批）
#
# 跨设备检测不一致的修复点行为锁。背景：维护者报"很多设备上检测不一致"，Lead 复盘定位出
# 六条检测链（ffmpeg 本体 / PLAN 定位 / 编码器枚举 / GPU 报告 / 外部工具五级搜索 / 能力表）
# 上的六处放大器（B1 settings 失效路径钉死、B2 编码器缓存不分档、B3 GPU 空报告缓存、
# B4 cwd 依赖的 PLAN 发现、B5 扩展搜索/PATH 静默命中、B6 能力表只加不减）。
# 本闸锁的是**用户可观察的解析语义**（PlatformServices.EnsureFfmpegDirectoryResolved 契约）：
#   §1 失效 + Auto=true  ⇒ 出声 + 从 PLAN 重发现 + **持久化**（下次启动直接可用）
#   §2 失效 + 显式值     ⇒ 出声 + 本次会话改用 PLAN + **不覆写**用户选择（幂等，可恢复）
#   §3 伪 PLAN 拒绝      ⇒ 空壳 PLAN/ 目录（无 ffmpeg-full）配不出工具；cwd 空壳 PLAN/ 不被
#                          向上查找接受（LooksLikePlan）
#   §4 来源可见          ⇒ 工具报告逐项带 [来源]（PLAN/manual/app-adjacent/extended-search/PATH）
#   -SelfCheck 负控      ⇒ 把判据打到"路径本来就有效"的夹具上，必须转红（证明判据有牙；
#                          runner 不给本闸传该开关，此臂供人工调闸与未来接线用）
# 全部夹具在自家 run-dir（tests/output/tool-detect/<guid>）；--settings 指定的文件被 Save()
# 原位写回（AppSettingsService S-P0-2）⇒ 不污染用户配置；env 逐案设置并在 finally 还原。
# ═══════════════════════════════════════════════════════════════════════════
param([switch]$SelfCheck)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$pass = 0; $fail = 0

# 临时件根自钉（§4：裸跑单条门禁调试时要自己钉同一个根）
$tmpRoot = Join-Path $repo 'tests\output\_tmp\round-tmp'
New-Item -ItemType Directory -Force -Path $tmpRoot | Out-Null
$env:TMP = $tmpRoot; $env:TEMP = $tmpRoot; $env:TMPDIR = $tmpRoot

function CK([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Output "PASS  $name$(if ($detail) { "   $detail" })" }
    else     { $script:fail++; Write-Output "FAIL  $name   $detail" }
}

# ── 前提：开发件 exe 与随包 ffmpeg（publish\PLAN 整树 gitignore ⇒ 缺失时 fail-closed 点名）──
$exe = Join-Path $repo 'src\FfmpegGui\bin\Release\net11.0\win-x64\FfmpegGui.exe'
$planDir = Join-Path $repo 'publish\PLAN'
$ffmpegExe = Join-Path $planDir 'ffmpeg-full\ffmpeg.exe'
if (-not (Test-Path $exe))     { Write-Output 'FAIL 前提：开发件 exe 不存在（先 dotnet build -c Release）'; exit 1 }
if (-not (Test-Path $ffmpegExe)) {
    Write-Output ('FAIL 前提：publish\PLAN\ffmpeg-full\ffmpeg.exe 不存在 —— publish\PLAN 整树 gitignore、不随仓分发；' +
        '先出一次包（pack.ps1）或从既有包还原 publish\PLAN')
    exit 1
}

# run-dir（一轮一目录）
$outDir = Join-Path $repo ("tests\output\tool-detect\" + [guid]::NewGuid().ToString('N').Substring(0, 12))
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# 输入件：lavfi 造 4 帧 GIF（不依赖仓内语料）
$gif = Join-Path $outDir 'src.gif'
$mk = Start-Process -FilePath $ffmpegExe -ArgumentList @('-y','-hide_banner','-loglevel','error',
    '-f','lavfi','-i','testsrc2=s=64x48:d=0.2:r=10','-frames:v','4', $gif) -NoNewWindow -Wait -PassThru
CK '前提：lavfi 造 GIF 成功' ((Test-Path $gif) -and $mk.ExitCode -eq 0)

# env 保存/还原（finally）
$savedPlan = $env:FFMPEGGUI_PLAN_DIR
$savedFf = $env:FFMPEGGUI_FFMPEG_DIR

# JSON 夹具用的路径（字符串层转义，不用 -replace 的正则语义）
$missing = 'Q:\__definitely_missing_dir__'
$missingJson = $missing.Replace('\', '\\')          # Q:\\__definitely_missing_dir__
$planFf = Join-Path $planDir 'ffmpeg-full'
$planFfJson = $planFf.Replace('\', '\\')

function Invoke-Case([string]$settingsJson, [string]$tag, [string]$planEnv) {
    $sf = Join-Path $outDir "$tag.json"
    [System.IO.File]::WriteAllText($sf, $settingsJson)
    $od = Join-Path $outDir "out_$tag"
    New-Item -ItemType Directory -Force -Path $od | Out-Null
    if ($planEnv) { $env:FFMPEGGUI_PLAN_DIR = $planEnv } else { $env:FFMPEGGUI_PLAN_DIR = $null }
    $env:FFMPEGGUI_FFMPEG_DIR = $null
    & $exe --headless --log-level info -i $gif -o $od --format png --settings $sf 2>&1 |
        Tee-Object -Variable caseOut | Out-Null
    [PSCustomObject]@{
        Exit = $LASTEXITCODE; Log = ($caseOut -join "`n"); SettingsFile = $sf; OutputDir = $od
    }
}

try {
    if (-not $SelfCheck) {
        # ── §1 失效 + Auto=true ⇒ 重发现 + 持久化 ──
        # ⚠ 实参用空格分隔（命令模式下逗号会把多实参绑成一个数组 ⇒ $tag 落空、夹具写成 .json）
        $r1 = Invoke-Case "{`"FfmpegDirectory`": `"$missingJson`", `"FfmpegDirectoryAuto`": true}" 's1' $planDir
        CK '§1 转换 exit=0' ($r1.Exit -eq 0) "exit=$($r1.Exit)"
        CK '§1 点名失效路径' ($r1.Log.Contains('[ffmpeg-dir] configured directory is not usable'))
        CK '§1 从 PLAN 重发现（来源可见）' ($r1.Log -match 're-discovered from PLAN \(source=')
        $j1 = (Get-Content $r1.SettingsFile -Raw | ConvertFrom-Json)
        CK '§1 settings 已持久化为发现值' (($j1.FfmpegDirectory -eq $planFf) -and $j1.FfmpegDirectoryAuto)
        CK '§1 产物在' ((Get-ChildItem $r1.OutputDir -Filter '*.png' -ErrorAction SilentlyContinue | Measure-Object).Count -ge 1)

        # ── §2 失效 + 显式值（旧 settings 无 Auto 字段） ⇒ 会话改用、不覆写 ──
        $r2 = Invoke-Case "{`"FfmpegDirectory`": `"$missingJson`"}" 's2' $planDir
        CK '§2 转换 exit=0' ($r2.Exit -eq 0) "exit=$($r2.Exit)"
        CK '§2 点名「会话改用、不覆写」' ($r2.Log.Contains('session uses PLAN value (setting not overwritten)'))
        $j2 = (Get-Content $r2.SettingsFile -Raw | ConvertFrom-Json)
        CK '§2 settings 未被覆写（仍为原显式值）' ($j2.FfmpegDirectory -eq $missing)
        CK '§2 产物在' ((Get-ChildItem $r2.OutputDir -Filter '*.png' -ErrorAction SilentlyContinue | Measure-Object).Count -ge 1)

        # ── §3 伪 PLAN 拒绝 + 有效显式值不受干扰 ──
        # env 指向空壳目录：env 候选不作形状校验（显式意图），但 FindPlanSubDir 在空壳里必然落空
        # ⇒ 不发生 re-discovery；settings 的有效目录原样保留（auto:valid）。
        $bogus = Join-Path $outDir 'bogus-plan'
        New-Item -ItemType Directory -Force -Path $bogus | Out-Null
        $r3 = Invoke-Case "{`"FfmpegDirectoryAuto`": true, `"FfmpegDirectory`": `"$planFfJson`"}" 's3' $bogus
        CK '§3 转换 exit=0' ($r3.Exit -eq 0) "exit=$($r3.Exit)"
        CK '§3 不发生重发现（空壳 PLAN 配不出 ffmpeg-full）' (-not ($r3.Log -match 're-discovered from PLAN'))
        CK '§3 不误报失效' (-not ($r3.Log.Contains('configured directory is not usable')))
        CK '§3 ffmpeg 来源=app-adjacent（settings 目录命中③级）' ($r3.Log.Contains('ffmpeg: ✅ [app-adjacent]'))

        # ── §3b cwd 空壳 PLAN 拒绝（P4 LooksLikePlan）：cwd 里的空 PLAN/ 绝不能被向上查找接受 ──
        $cwdCase = Join-Path $outDir 'cwd-case'
        New-Item -ItemType Directory -Force -Path (Join-Path $cwdCase 'PLAN') | Out-Null
        $sf4 = Join-Path $outDir 's4.json'
        [System.IO.File]::WriteAllText($sf4, "{`"FfmpegDirectory`": `"$missingJson`"}")
        $env:FFMPEGGUI_PLAN_DIR = $null; $env:FFMPEGGUI_FFMPEG_DIR = $null
        Push-Location $cwdCase
        & $exe --headless --log-level info -i $gif -o (Join-Path $outDir 'out_s4') --format png --settings $sf4 2>&1 |
            Tee-Object -Variable case4 | Out-Null
        $exit4 = $LASTEXITCODE
        Pop-Location
        $log4 = $case4 -join "`n"
        CK '§3b cwd 空壳 PLAN 不被接受（无 source=cwd）' (-not ($log4 -match 'source=cwd'))
        CK '§3b 显式失效值不被覆写' (((Get-Content $sf4 -Raw | ConvertFrom-Json).FfmpegDirectory) -eq $missing)
        CK '§3b 转换 exit=0（开发机上 exe-dir PLAN junction 兜底）' ($exit4 -eq 0) "exit=$exit4"

        # ── §4 来源可见（P5）：随包 PLAN 在位 ⇒ 工具报告逐项带 [PLAN] ──
        $r5 = Invoke-Case "{`"FfmpegDirectory`": `"$missingJson`", `"FfmpegDirectoryAuto`": true}" 's5' $planDir
        $planLabels = ([regex]::Matches($r5.Log, '\[PLAN\]')).Count
        CK '§4 工具报告带 ≥6 个 [PLAN] 来源标签' ($planLabels -ge 6) "实得 $planLabels"
        CK '§4 ffmpeg 来源=PLAN' ($r5.Log.Contains('ffmpeg: ✅ [PLAN]'))
    }
    else {
        # ── 负控：判据打到"路径本来就有效"的夹具上，必须转红（证明失效判据有牙）──
        # 读法与 verify-png-structure 同族：-SelfCheck 期望本门禁转红。
        $rN = Invoke-Case "{`"FfmpegDirectoryAuto`": true, `"FfmpegDirectory`": `"$planFfJson`"}" 'nc' $planDir
        $toothless = ($rN.Log -match 'configured directory is not usable')
        if ($toothless) { Write-Output 'FAIL  负控失效：有效路径夹具竟命中「失效点名」⇒ 判据无牙' }
        else            { Write-Output 'PASS  负控：有效路径夹具上「失效点名」不命中（判据有牙，-SelfCheck 期望红即红）' }
        Write-Output ("===== tool-detect: PASS=$pass FAIL=$fail =====")
        exit 1   # 负控臂按契约转红
    }
}
finally {
    $env:FFMPEGGUI_PLAN_DIR = $savedPlan
    $env:FFMPEGGUI_FFMPEG_DIR = $savedFf
}

Write-Output ("===== tool-detect: PASS=$pass FAIL=$fail =====")
if ($fail -gt 0) { exit 1 }
exit 0
