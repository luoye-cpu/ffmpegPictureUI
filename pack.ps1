# FFmpegPictureUI 打包脚本
# 用法:
#   框架依赖 (默认):   .\pack.ps1 -Version "1.5.0" -Variant full
#   NativeAOT (.NET 11 SDK 安装后): .\pack.ps1 -Version "1.5.0" -Variant full -Aot
param(
    [string]$Version = "1.5.0",
    [ValidateSet("full", "min")]
    [string]$Variant = "full",
    [switch]$Aot  # 启用 NativeAOT 编译（需 .NET 11+ SDK）
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

# 命名
$modeTag = if ($Aot) { "aot" } else { "" }
if ($Variant -eq "full") {
    if ($modeTag) { $PackageName = "FFmpegPictureUI-v$Version-$Arch-full-$modeTag" }
    else          { $PackageName = "FFmpegPictureUI-v$Version-$Arch-full" }
} else {
    if ($modeTag) { $PackageName = "FFmpegPictureUI-v$Version-$Arch-$modeTag" }
    else          { $PackageName = "FFmpegPictureUI-v$Version-$Arch" }
}
$OutputDir = "$BuildDir\$PackageName"
$ArchivePath = "$PublishDir\$PackageName.7z"

$modeLabel = if ($Aot) { "NativeAOT (无 Runtime 依赖)" } else { "框架依赖 (需 .NET Runtime)" }
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "  FFmpegPictureUI 打包工具" -ForegroundColor Cyan
Write-Host "  版本: v$Version | 架构: $Arch | 类型: $Variant | $modeLabel" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan

# Step 1: 发布
Write-Host "`n[1/4] dotnet publish..." -ForegroundColor Yellow

if ($Aot) {
    # NativeAOT: 独立可执行文件，纯机器码，用户无需 Runtime
    dotnet publish $ProjectDir\FfmpegGui.csproj `
        -c Release -r $Rid `
        -p:PublishAot=true `
        -p:SelfContained=true `
        -p:PublishSingleFile=true `
        -p:IlcOptimizationPreference=Speed `
        -p:InvariantGlobalization=true `
        -p:Version=$Version `
        -o $OutputDir
} else {
    # 框架依赖: 用户需安装 .NET Runtime
    dotnet publish $ProjectDir\FfmpegGui.csproj `
        -c Release -r $Rid `
        --self-contained false `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=false `
        -p:Version=$Version `
        -o $OutputDir
}

if ($LASTEXITCODE -ne 0) { throw "发布失败" }
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

# Step 2: 复制 PLAN（完整包）
if ($Variant -eq "full") {
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

    # Step 3: 生成使用说明.txt
    Write-Host "`n[3/4] 生成使用说明..." -ForegroundColor Yellow
    $ReadmeTemplate = "$PlanSource\使用说明.txt"
    if (Test-Path $ReadmeTemplate) {
        $content = Get-Content $ReadmeTemplate -Raw -Encoding UTF8
        $content = $content.Replace("{VERSION}", $Version)
        $content = $content.Replace("{ARCH}", $Arch)
        $content = $content.Replace("{DATE}", (Get-Date -Format "yyyy-MM-dd"))
        if ($Aot) {
            # NativeAOT 版本：移除 .NET Runtime 安装要求
            $content = $content -replace "\.NET 10\.0 运行时（如未安装请先下载）[\s\S]*?dotnet/10\.0", "本版本已编译为独立可执行文件（NativeAOT），无需额外安装运行环境。"
        }
        $outReadme = "$PlanDest\使用说明.txt"
        Set-Content -Path $outReadme -Value $content -Encoding UTF8
        Write-Host "   ✅ 使用说明已生成 → $outReadme" -ForegroundColor Green
    }
} else {
    Write-Host "`n[2/4] 跳过 (精简包不含 PLAN)" -ForegroundColor Yellow
    Write-Host "`n[3/4] 跳过 (精简包不生成使用说明)" -ForegroundColor Yellow
}

# Step 4: 压缩
Write-Host "`n[4/4] 压缩打包..." -ForegroundColor Yellow
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
Write-Host "  打包完成!" -ForegroundColor Green
Write-Host "  产物: $OutputDir" -ForegroundColor Green
if (Test-Path $ArchivePath) {
    $size = (Get-Item $ArchivePath).Length / 1MB
    Write-Host "  压缩包: $ArchivePath ($([math]::Round($size, 1)) MB)" -ForegroundColor Green
}
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
