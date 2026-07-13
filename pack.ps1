# FFmpegPictureUI 打包脚本
# 用法: .\pack.ps1 [-Version "1.5.0"] [-Variant "full|min"]
param(
    [string]$Version = "1.5.0",
    [ValidateSet("full", "min")]
    [string]$Variant = "full"
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
if ($Variant -eq "full") {
    $PackageName = "FFmpegPictureUI-v$Version-$Arch-full"
} else {
    $PackageName = "FFmpegPictureUI-v$Version-$Arch"
}
$OutputDir = "$BuildDir\$PackageName"
$ArchivePath = "$PublishDir\$PackageName.7z"

Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "  FFmpegPictureUI 打包工具" -ForegroundColor Cyan
Write-Host "  版本: v$Version | 架构: $Arch | 类型: $Variant" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan

# Step 1: 发布
Write-Host "`n[1/4] dotnet publish..." -ForegroundColor Yellow
dotnet publish $ProjectDir\FfmpegGui.csproj `
    -c Release -r $Rid `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=true `
    -p:Version=$Version `
    -o $OutputDir

if ($LASTEXITCODE -ne 0) { throw "发布失败" }
Write-Host "   ✅ 发布完成 → $OutputDir" -ForegroundColor Green

# Step 2: 复制 PLAN（完整包）
if ($Variant -eq "full") {
    Write-Host "`n[2/4] 复制 PLAN 组件包..." -ForegroundColor Yellow
    $PlanDest = "$OutputDir\PLAN"
    if (Test-Path $PlanSource) {
        Copy-Item -Path "$PlanSource\*" -Destination $PlanDest -Recurse -Force
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
if (Get-Command "7z" -ErrorAction SilentlyContinue) {
    Push-Location $OutputDir
    7z a -mx9 "$ArchivePath" * | Out-Null
    Pop-Location
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
