# FFmpegPictureUI 打包脚本 — 2 版本发布（全部 NativeAOT 编译，目标 x86-64-v3 / AVX2）
# 用法:
#   单文件版 (仅随附 oxipng):  .\pack.ps1 -Mode app
#   完整版 (含 PLAN 组件包):   .\pack.ps1 -Mode full
#   一键全部 2 个版本:         .\pack.ps1 -Mode all (默认)
#
# 版本号省略时自动从 src/FfmpegGui/FfmpegGui.csproj 的 <Version> 读取，
# 避免与项目版本漂移。也可显式指定： .\pack.ps1 -Version "1.6.0"
#
# 打包时会强制随附法律文件（缺失即失败，不会产出不合规的包）：
#   LICENSE                GNU GPL-3.0 逐字全文
#   NOTICE                 本项目版权与必需声明（含 Adobe DNG 声明）
#   THIRD-PARTY-NOTICES.md 第三方组件许可清单
#   licenses/              各第三方许可原文
param(
    [string]$Version = "",
    [ValidateSet("app", "full", "all")]
    [string]$Mode = "all",
    [switch]$NoCompress  # 跳过 7z 压缩（调试/CI 时快速验证打包逻辑）
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectDir = "$ScriptDir\src\FfmpegGui"
$PublishDir = "$ScriptDir\publish"
$BuildDir = "$ScriptDir\publish\build"
$PlanSource = "$PublishDir\PLAN"

# ── 版本号：未显式指定时从 csproj 读取，避免与项目版本漂移 ──
if (-not $Version) {
    $CsprojPath = "$ProjectDir\FfmpegGui.csproj"
    $Version = "0.0.0"
    if (Test-Path $CsprojPath) {
        $m = Select-String -Path $CsprojPath -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
        if ($m) { $Version = $m.Matches[0].Groups[1].Value }
    }
    if ($Version -eq "0.0.0") {
        throw "无法确定版本号：请用 -Version 显式指定，或确认 $CsprojPath 存在且含 <Version>"
    }
    Write-Host "   i 未指定 -Version，已从 csproj 读取: $Version" -ForegroundColor Cyan
}

# 架构
$Arch = "x64"
$Rid = "win-x64"

# ── 包命名 ──
function Get-PackageName([string]$mode) {
    switch ($mode) {
        "app"  { return "FFmpegPictureUI-v$Version-$Arch" }
        "full" { return "FFmpegPictureUI-v$Version-$Arch-full" }
    }
    throw "未知模式: $mode"
}

# ── 单个版本打包（统一 NativeAOT 编译，区别仅是否含 PLAN）──
function Invoke-Pack([string]$mode) {
    $PackageName = Get-PackageName $mode
    $OutputDir = "$BuildDir\$PackageName"
    $ArchivePath = "$PublishDir\$PackageName.7z"

    $modeLabel = switch ($mode) {
        "app"  { "单文件版 (NativeAOT, 不含 PLAN)" }
        "full" { "完整版 (NativeAOT + PLAN 组件包)" }
    }
    Write-Host "`n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
    Write-Host "  $modeLabel" -ForegroundColor Cyan
    Write-Host "  v$Version | $Arch | $PackageName" -ForegroundColor Cyan
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan

    # 清理旧产物，避免残留
    # ⚠ 10-01 实测教训：中途 kill 过一次 pack.ps1 后，输出目录被残留进程锁住，这句 `Remove-Item`
    #   报 "The process cannot access the file … being used by another process" 却**没有中止脚本**
    #   ⇒ 后面照常压缩并打印"打包完成"，产出的是一个**新旧混合状态**的目录（当时 PLAN 只剩 49 个文件）。
    #   清不掉就必须停下来：拿旧目录出包比不出包糟糕得多。
    if (Test-Path $OutputDir) {
        Remove-Item -Recurse -Force $OutputDir -ErrorAction SilentlyContinue
        if (Test-Path $OutputDir) {
            throw "[$mode] 无法清掉旧产物目录（被其他进程占用）：$OutputDir ⇒ 中止。先确认没有正在运行的 pack.ps1 / 7z / 被测 exe，再重试；不要重用该目录。"
        }
    }
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    # 本轮起点时间：给后面的"完整性闸"判断 PLAN/法律文件确实是**这一次**复制进来的
    $runStart = Get-Date

    # Step 1: NativeAOT 发布（所有版本统一 AOT 编译）
    Write-Host "`n[1/4] dotnet publish (NativeAOT)..." -ForegroundColor Yellow
    # ⚠ 必须先 build、再 publish -p:NoBuild=true（2026-10-28 实测）。
    #   让 publish 自己重编时，编译产物里没有 Avalonia 的资源表（exe 字节里
    #   `!AvaloniaResources` / `!XamlIlPopulate` 0 命中，件小约 8 MB），出货包在
    #   App.Initialize() 抛 XamlLoadException: No precompiled XAML found for FfmpegGui.App，
    #   进程以 0xC0000409 自杀 —— 表现为：包能解压、headless 能跑、L3 五套件全绿，
    #   但 GUI 一键都打不开（v1.6.0-beta9 与 beta10 两版都如此；beta8 的包内件有载荷，能起）。
    #   单变量实测：去掉 PublishSingleFile、TrimMode=partial、把 ILC 钉到 rc.1 均无效；
    #   build + publish -p:NoBuild=true ⇒ 23,923,712 B、载荷在位、窗口句柄非 0。
    dotnet build "$ProjectDir\FfmpegGui.csproj" -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "[$mode] 预构建失败（发布前必须有一份带资源表的编译件）" }
    dotnet publish $ProjectDir\FfmpegGui.csproj `
        -c Release -r $Rid `
        -p:PublishAot=true `
        -p:NoBuild=true `
        -p:SelfContained=true `
        -p:PublishSingleFile=true `
        -p:IlcOptimizationPreference=Speed `
        -p:IlcInstructionSet=x86-64-v3 `
        -p:InvariantGlobalization=true `
        -p:Version=$Version `
        -o $OutputDir
    if ($LASTEXITCODE -ne 0) { throw "[$mode] 发布失败" }
    Write-Host "   ✅ 发布完成 → $OutputDir" -ForegroundColor Green

# Step 1.5: 清理调试符号（SkiaSharp/HarfBuzz .pdb ~102MB，仅供调试）
$pdbFiles = Get-ChildItem "$OutputDir\*.pdb" -ErrorAction SilentlyContinue
if ($pdbFiles) {
    $pdbFiles | Remove-Item -Force
    $savedMB = [math]::Round(($pdbFiles | Measure-Object Length -Sum).Sum / 1MB, 1)
    Write-Host "   ✅ 已删除调试符号，节省 ${savedMB}MB" -ForegroundColor Green
}

# Step 1.6: 确保多语言资源文件存在（单文件发布可能不输出 Content，兜底复制）
$LocalesDest = "$OutputDir\Resources\Locales"
if (-not (Get-ChildItem "$LocalesDest\*.json" -ErrorAction SilentlyContinue)) {
    $LocaleSrc = "$ProjectDir\Resources\Locales"
    if (Test-Path "$LocaleSrc\*.json") {
        New-Item -ItemType Directory -Force -Path $LocalesDest | Out-Null
        Copy-Item "$LocaleSrc\*.json" $LocalesDest -Force
        Write-Host "   ✅ 已兜底复制多语言资源文件 (zh-CN/en-US)" -ForegroundColor Green
    } else {
        Write-Host "   ⚠️ 未找到多语言资源源文件: $LocaleSrc" -ForegroundColor Yellow
    }
}

# Step 1.7: PLAN 工具链版本校验清单（full 模式）
# ⚠️ 2026-08-15: dotnet publish 输出目录可能残留旧 PLAN 快照（dngtool 等外部工具
#    不随源码编译更新），此处打印主 PLAN 关键工具版本，便于打包前确认无旧版混入。
#    完整版 Step 2 会用 robocopy 从主 PLAN 全量覆盖（含删除旧目录），故仅需提示。
if ($mode -eq "full" -and (Test-Path $PlanSource)) {
    Write-Host "`n[1.7] PLAN 工具链版本清单..." -ForegroundColor Yellow
    $planTools = @(
        "$PlanSource\artifacts\dngtool.exe",
        "$PlanSource\artifacts\oxipng.exe",
        "$PlanSource\ffmpeg-full\ffmpeg.exe",
        "$PlanSource\jxl\bin\cjxl.exe",
        "$PlanSource\exiftool\exiftool.exe"
    )
    foreach ($t in $planTools) {
        if (Test-Path $t) {
            $f = Get-Item $t
            Write-Host "   ✅ $(Split-Path $t -Leaf): $($f.LastWriteTime.ToString('yyyy-MM-dd HH:mm')) ($([math]::Round($f.Length/1KB,0)) KB)" -ForegroundColor Green
        } else {
            Write-Host "   ⚠️ 缺失: $t" -ForegroundColor Yellow
        }
    }
    # 输出目录已有旧 PLAN？提示会被 Step 2 覆盖
    $oldDng = "$OutputDir\PLAN\artifacts\dngtool.exe"
    if (Test-Path $oldDng) {
        $old = (Get-Item $oldDng).LastWriteTime
        $new = (Get-Item "$PlanSource\artifacts\dngtool.exe").LastWriteTime
        if ($old -lt $new) {
            Write-Host "   ⚠️ 输出目录 dngtool 旧于主 PLAN ($($old.ToString('MM-dd HH:mm')) < $($new.ToString('MM-dd HH:mm')))，Step 2 将全量覆盖" -ForegroundColor Yellow
        }
    }
}

# Step 2/3: 复制 PLAN + 生成使用说明 (仅完整版)
if ($mode -eq "full") {
    Write-Host "`n[2/4] 复制 PLAN 组件包..." -ForegroundColor Yellow
    $PlanDest = "$OutputDir\PLAN"
    if (Test-Path $PlanSource) {
        # 先删除目标 PLAN 目录及其内容（避免旧文件残留导致冲突）
        if (Test-Path $PlanDest) {
            Remove-Item -Recurse -Force $PlanDest -ErrorAction SilentlyContinue
            # 等待文件系统释放锁
            Start-Sleep -Milliseconds 500
        }
        # 使用 robocopy 避免 PowerShell Copy-Item 的容器/叶节点冲突
        # /XF 排除 .lib(静态导入库) / .pc(pkg-config) / .cmake(CMake 包配置) —— 均为编译期元数据，运行时无用
        # /XD 排除 pkgconfig 与 cmake 目录
        # ⚠ BUILD-01：robocopy 必须用全路径。PATH 不含 System32 的会话（受限容器/精简环境）裸调会
        #   "术语 'robocopy' 不会被识别"并直接中止（ErrorActionPreference=Stop）。
        $robocopy = Join-Path $env:SystemRoot 'System32\robocopy.exe'
        if (-not (Test-Path -LiteralPath $robocopy)) {
            throw "[$mode] 找不到 robocopy: $robocopy —— 请确认 Windows 安装完整，或手工把 PLAN 复制到 $PlanDest"
        }
        & $robocopy $PlanSource $PlanDest /E /XF *.lib *.pc *.cmake /XD pkgconfig cmake /NFL /NDL /NJH /NJS /NC /NS
        if ($LASTEXITCODE -ge 8) { throw "复制 PLAN 失败 (robocopy exit code: $LASTEXITCODE)" }
        Write-Host "   ✅ PLAN 组件已复制" -ForegroundColor Green
    } else {
        throw "PLAN 源目录不存在: $PlanSource —— 完整版无法打包。请先准备 PLAN 组件包（见 PACKAGING_SPEC.md），或改用 -Mode app。"
    }

    Write-Host "`n[3/4] 生成使用说明..." -ForegroundColor Yellow
    $ReadmeTemplate = "$PlanSource\使用说明.txt"
    if (Test-Path $ReadmeTemplate) {
        $content = Get-Content $ReadmeTemplate -Raw -Encoding UTF8
        $content = $content.Replace("{VERSION}", $Version)
        $content = $content.Replace("{ARCH}", $Arch)
        $content = $content.Replace("{DATE}", (Get-Date -Format "yyyy-MM-dd"))
        $outReadme = "$PlanDest\使用说明.txt"
        Set-Content -Path $outReadme -Value $content -Encoding UTF8
        Write-Host "   ✅ 使用说明已生成 → $outReadme" -ForegroundColor Green
    } else {
        throw "缺少使用说明模板: $ReadmeTemplate"
    }
} else {
    Write-Host "`n[2/4] app 档随附 oxipng.exe (PNG 默认优化器)..." -ForegroundColor Yellow
    # BUILD-04 / MA-03：app 包默认 pngopt=max，但过去不随附 oxipng.exe ⇒
    #   PngOptimizerService 运行时打一行"未检测到 oxipng，已跳过优化"就静默降级。
    #   现在把单文件 oxipng.exe（~1MB，MIT，THIRD-PARTY-NOTICES.md 已登记）放到
    #   <包>/PLAN/artifacts/oxipng.exe —— PlatformServices.LooksLikePlan 认 artifacts/ 子目录
    #   ⇒ PlanFolderPath 锁定本地这个最小 PLAN；其他工具（ffmpeg/cjxl/exiftool/avifenc/dngtool）
    #   在该最小 PLAN 里找不到，自然回落到外部 PATH / 用户设置，不影响既有检测顺序。
    $appPlanDir = "$OutputDir\PLAN\artifacts"
    $appOxipng = Join-Path $appPlanDir 'oxipng.exe'
    $srcOxipng = Join-Path $PlanSource 'artifacts\oxipng.exe'
    if (-not (Test-Path -LiteralPath $srcOxipng)) {
        throw "[$mode] 缺少 oxipng 源文件: $srcOxipng —— app 档必须随附 oxipng（默认 pngopt=max）。请先准备 PLAN 组件包。"
    }
    New-Item -ItemType Directory -Force -Path $appPlanDir | Out-Null
    Copy-Item -LiteralPath $srcOxipng -Destination $appOxipng -Force
    Write-Host "   ✅ 已随附 oxipng.exe ($([math]::Round((Get-Item $appOxipng).Length/1KB,0)) KB) → PLAN\artifacts\" -ForegroundColor Green

    Write-Host "`n[3/4] 跳过 (app 档不生成使用说明，见 PACKAGING_SPEC.md)" -ForegroundColor Yellow
}

    # Step 3.5: 随附法律文件（GPL-3.0 §4/§5/§6 要求随二进制分发，缺失即失败）
    Write-Host "`n[3.5/4] 随附法律文件..." -ForegroundColor Yellow
    foreach ($lf in @("LICENSE", "NOTICE", "THIRD-PARTY-NOTICES.md")) {
        $lfSrc = "$ScriptDir\$lf"
        if (-not (Test-Path $lfSrc)) {
            throw "缺少必需的法律文件: $lfSrc —— 拒绝打包不合规的产物"
        }
        Copy-Item $lfSrc "$OutputDir\$lf" -Force
        Write-Host "   ✅ $lf" -ForegroundColor Green
    }
    $licDirSrc = "$ScriptDir\licenses"
    if (-not (Test-Path $licDirSrc)) {
        throw "缺少 licenses/ 目录: $licDirSrc —— 拒绝打包不合规的产物"
    }
    Copy-Item $licDirSrc "$OutputDir\licenses" -Recurse -Force
    $licCount = (Get-ChildItem $licDirSrc -File).Count
    Write-Host "   ✅ licenses/ ($licCount 个许可原文)" -ForegroundColor Green

    # 清除作者本机运行时状态（含绝对路径，且会让用户继承他人设置）
    $staleSettings = "$OutputDir\settings.json"
    if (Test-Path $staleSettings) {
        Remove-Item $staleSettings -Force
        Write-Host "   ✅ 已移除 settings.json（作者本机运行时状态，程序会自动重建）" -ForegroundColor Green
    }

    # Step 4: 压缩
    Write-Host "`n[4/4] 压缩打包..." -ForegroundColor Yellow

    # ── 完整性闸：压缩**之前**核对目录内容（10-01 出包事故的对策）────────────────────
    # 当时的实况：清理旧目录失败被吞掉 ⇒ 目录里 PLAN 只剩 49 个文件（缺 exiftool/ 与 artifacts/），
    # 而脚本一路走到"✅ 压缩完成 / 🎉 全部打包完成"。出包的门槛不能只看"没报错"，必须看**内容**。
    $need = @("FfmpegGui.exe", "LICENSE", "NOTICE", "THIRD-PARTY-NOTICES.md")
    $needDirs = @("licenses")
    if ($mode -eq "full") {
        $need += @("PLAN\ffmpeg-full\ffmpeg.exe", "PLAN\jxl\bin\cjxl.exe", "PLAN\jxl\bin\djxl.exe",
                   "PLAN\jxl\bin\jxlinfo.exe", "PLAN\exiftool\exiftool.exe",
                   "PLAN\artifacts\JxrEncApp.exe", "PLAN\artifacts\JxrDecApp.exe",
                   "PLAN\artifacts\avifenc.exe", "PLAN\artifacts\dngtool.exe",
                   "PLAN\artifacts\oxipng.exe")
        $needDirs += @("PLAN\exiftool", "PLAN\artifacts", "PLAN\ffmpeg-full", "PLAN\jxl")
    } else {
        # app 档：仅随附 oxipng（BUILD-04）；其余外部工具走 PATH / 用户设置，不在包内。
        $need += @("PLAN\artifacts\oxipng.exe")
        $needDirs += @("PLAN\artifacts")
    }
    $miss = @()
    foreach ($n in $need)      { if (-not (Test-Path (Join-Path $OutputDir $n))) { $miss += $n } }
    foreach ($d in $needDirs)  { if (-not (Test-Path (Join-Path $OutputDir $d))) { $miss += "$d\" } }
    $gotFiles = @(Get-ChildItem $OutputDir -Recurse -File -EA SilentlyContinue).Count
    # PLAN 组件包共 ~568 个文件；含 PLAN 的完整包总文件数应 ≥ 560（robocopy 排除了 .lib/.pc/.cmake）
    # app 档：exe + 原生库 + licenses + Resources + oxipng ≈ 30+ 文件；下限取 20 留余量。
    $minFiles = $(if ($mode -eq "full") { 560 } else { 20 })
    if ($miss.Count -gt 0 -or $gotFiles -lt $minFiles) {
        throw "[$mode] 完整性闸未过：缺 $(if ($miss.Count) { ($miss -join ', ') } else { '(无缺项)' })；文件数 $gotFiles（要求 ≥ $minFiles）⇒ 拒绝压缩出包"
    }
    Write-Host "   ✅ 完整性闸通过：$gotFiles 个文件，必需项齐备" -ForegroundColor Green

    if ($NoCompress) {
        Write-Host "   ⏭️ 已跳过压缩 (-NoCompress)" -ForegroundColor Yellow
        Write-Host "   产物目录: $OutputDir" -ForegroundColor Yellow
        return
    }
    $sevenZip = Get-Command "7z" -ErrorAction SilentlyContinue
    if (-not $sevenZip -and (Test-Path "C:\Program Files\7-Zip\7z.exe")) {
        $sevenZip = [pscustomobject]@{ Source = "C:\Program Files\7-Zip\7z.exe" }
    }
    if ($sevenZip) {
        # 删除旧压缩包，避免 7z 'a' 追加模式残留旧条目
        Remove-Item $ArchivePath -Force -ErrorAction SilentlyContinue
        # 极限压缩（实测最优）：
        #   -mx9       极限等级；7z 按文件架构自动加 BCJ/BCJ2（勿手动 -m0，勿加 mc=1e9 / -mqs，均负优化）
        #   -md=3840m  字典上限（写 4095m 会被钳制到此值）
        #   -mfb=273   单词大小上限
        #   -ms=on     固实压缩
        #   -mmt=1     单线程（压缩率最高）
        # '*' 由 7z 在 WorkingDirectory 内自展开，递归打包全部内容
        $zipArgs = 'a -t7z -mx9 -md=3840m -mfb=273 -ms=on -mmt=1 "' + $ArchivePath + '" *'
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $sevenZip.Source
        $psi.Arguments = $zipArgs
        $psi.WorkingDirectory = $OutputDir
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $false   # 继承父控制台，实时显示 7z 压缩进度
        $proc = [System.Diagnostics.Process]::Start($psi)
        try { $proc.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::High } catch { }  # 提升 7z 到系统高优先级
        $proc.WaitForExit()
        if ($proc.ExitCode -ne 0) { throw "压缩失败 (7z exit code: $($proc.ExitCode))" }
        Write-Host "   ✅ 压缩完成 → $ArchivePath" -ForegroundColor Green
    } else {
        Write-Host "   x 未找到 7z 命令，无法生成压缩包" -ForegroundColor Red
        Write-Host "     未压缩目录: $OutputDir" -ForegroundColor Yellow
        Write-Host "     请安装 7-Zip，或用 -NoCompress 明确表示只要目录" -ForegroundColor Yellow
        throw "缺少 7z：拒绝静默产出未压缩产物"
    }

    Write-Host "`n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
    Write-Host "  [$mode] 打包完成!" -ForegroundColor Green
    Write-Host "  产物: $OutputDir" -ForegroundColor Green
    if (Test-Path $ArchivePath) {
        $size = (Get-Item $ArchivePath).Length / 1MB
        Write-Host "  压缩包: $ArchivePath ($([math]::Round($size, 1)) MB)" -ForegroundColor Green
    }
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
}

# ── 入口 ──
Write-Host "`n═══════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  FFmpegPictureUI 打包工具 (2 版本发布)" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════" -ForegroundColor Cyan

# ── 出包前红态闸（MA-01/GATE-02）：任何复制/压缩动作之前 fail-closed ──
# 调 tests/scripts/_release-gate-check.ps1 读 tests/output/release-gates.json；
# 清单缺失 / 任一 rc!=0 ⇒ exit 1，不产包。helper 自身已做 fail-closed（缺字段/类型错按红）。
# 本脚本只负责"调到它"并把它的退出码翻译成可操作提示。
$gateScript = "$ScriptDir\tests\scripts\_release-gate-check.ps1"
if (-not (Test-Path -LiteralPath $gateScript)) {
    throw "缺少出包红态闸脚本: $gateScript —— 拒绝出包。请确认仓库完整。"
}
Write-Host "`n[gate] 出包前红态闸校验..." -ForegroundColor Yellow
& powershell -NoProfile -ExecutionPolicy Bypass -File $gateScript
$gateRc = $LASTEXITCODE
if ($gateRc -ne 0) {
    Write-Host "[gate] 红态闸未过 (rc=$gateRc) —— 拒绝出包。" -ForegroundColor Red
    Write-Host "       下一步：按 docs/RELEASE_GATES.md 跑本批变更链路的门禁，" -ForegroundColor Yellow
    Write-Host "       把 (gate 名, rc, 日志路径, 产物 sha256[:16]) 写进 tests/output/release-gates.json，再重跑 pack.ps1。" -ForegroundColor Yellow
    exit $gateRc
}
Write-Host "[gate] 红态闸通过，继续打包。" -ForegroundColor Green

if ($Mode -eq "all") {
    foreach ($m in @("app", "full")) {
        Invoke-Pack $m
    }
    Write-Host "`n🎉 全部 2 个版本打包完成!" -ForegroundColor Green
} else {
    Invoke-Pack $Mode
}
