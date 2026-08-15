# FFmpegPictureUI 打包脚本 — 2 版本发布（全部 NativeAOT 编译）
# 用法:
#   单文件版 (不含 PLAN):      .\pack.ps1 -Version "1.5.2" -Mode app
#   完整版 (含 PLAN 组件包):   .\pack.ps1 -Version "1.5.2" -Mode full
#   一键全部 2 个版本:         .\pack.ps1 -Version "1.5.2" -Mode all (默认)
param(
    [string]$Version = "1.5.2",
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
    if (Test-Path $OutputDir) { Remove-Item -Recurse -Force $OutputDir }
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

    # Step 1: NativeAOT 发布（所有版本统一 AOT 编译）
    Write-Host "`n[1/4] dotnet publish (NativeAOT)..." -ForegroundColor Yellow
    dotnet publish $ProjectDir\FfmpegGui.csproj `
        -c Release -r $Rid `
        -p:PublishAot=true `
        -p:SelfContained=true `
        -p:PublishSingleFile=true `
        -p:IlcOptimizationPreference=Speed `
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
        robocopy $PlanSource $PlanDest /E /NFL /NDL /NJH /NJS /NC /NS
        if ($LASTEXITCODE -ge 8) { throw "复制 PLAN 失败 (robocopy exit code: $LASTEXITCODE)" }
        Write-Host "   ✅ PLAN 组件已复制" -ForegroundColor Green
    } else {
        Write-Host "   ⚠️ PLAN 源目录不存在: $PlanSource" -ForegroundColor Yellow
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
    }
} else {
    Write-Host "`n[2/4] 跳过 (单文件版不含 PLAN)" -ForegroundColor Yellow
    Write-Host "`n[3/4] 跳过 (单文件版不生成使用说明)" -ForegroundColor Yellow
}

    # Step 4: 压缩
    Write-Host "`n[4/4] 压缩打包..." -ForegroundColor Yellow
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
        Write-Host "   ⚠️ 未找到 7z 命令，跳过压缩" -ForegroundColor Yellow
        Write-Host "   手动压缩目录: $OutputDir" -ForegroundColor Yellow
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

if ($Mode -eq "all") {
    foreach ($m in @("app", "full")) {
        Invoke-Pack $m
    }
    Write-Host "`n🎉 全部 2 个版本打包完成!" -ForegroundColor Green
} else {
    Invoke-Pack $Mode
}
