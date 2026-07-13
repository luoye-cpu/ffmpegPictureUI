# ═══════════════════════════════════════════════════════════
#  外部工具自编译脚本 (Windows PowerShell)
#  用法: .\build_tools.ps1 [-SkipClone] [-CleanAfterBuild] [-SimdLevel AVX2|AVX512|Native|Baseline]
#  前提: Visual Studio 2022/2026 + CMake + Git + NASM
#
#  v2.1 变更:
#  - 自动检测 VS 2026 → VS 2022 回退
#  - 强制使用 VS 自带 cmake (支持 "Visual Studio 18 2026" 生成器)
#  - 产物输出到 PLAN/artifacts，自动清理废弃文件
#  - /arch:AVX2 默认启用
# ═══════════════════════════════════════════════════════════
param(
    [switch]$SkipClone,
    [switch]$CleanAfterBuild,
    [ValidateSet("AVX2","AVX512","Native","Baseline")]
    [string]$SimdLevel = "AVX2"
)

$ErrorActionPreference = "Stop"
$SrcDir     = "$PSScriptRoot\src"
$PlanBase   = "$PSScriptRoot\..\publish\FFmpegPictureUI_v1.4.5-max_win-x64\PLAN"
$PlanDir    = "$PlanBase\artifacts"
$InstallDir = "$SrcDir\install"

# ── 自动检测 VS 版本 ──
$vs2026 = Resolve-Path "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" -ErrorAction SilentlyContinue
$vs2022 = Resolve-Path "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" -ErrorAction SilentlyContinue

if ($vs2026) {
    $CmakeExe = $vs2026.Path
    $CmakeGen = "Visual Studio 18 2026"
    $VsVer    = "2026"
} elseif ($vs2022) {
    $CmakeExe = $vs2022.Path
    $CmakeGen = "Visual Studio 17 2022"
    $VsVer    = "2022"
} else {
    # 回退: 使用 PATH 中的 cmake + Ninja
    $CmakeExe = (Get-Command cmake -ErrorAction Stop).Source
    $CmakeGen = "Ninja"
    $VsVer    = "Ninja (GCC)"
}

# ── SIMD 标志 ──
$SimdFlags = switch ($SimdLevel) {
    "AVX512"   { @("/arch:AVX512") }
    "AVX2"     { @("/arch:AVX2") }
    "Native"   { @("/favor:blend") }
    "Baseline" { @("/arch:SSE2") }
}

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  build_tools v2.1 | VS $VsVer | SIMD: $SimdLevel" -ForegroundColor Cyan
Write-Host "  cmake: $CmakeExe" -ForegroundColor Gray
Write-Host "  产物: ultrahdr_app + JxrEnc/Dec + avifenc" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan

# ── 0. 环境检查 ──
Write-Host "`n[0/6] 检查编译环境..." -ForegroundColor Yellow
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw "git not found" }
$nasm = Get-Command nasm -ErrorAction SilentlyContinue
if ($nasm) { Write-Host "  NASM: $($nasm.Source)" -ForegroundColor Green }
else       { Write-Host "  NASM: not found (slower C fallback)" -ForegroundColor Yellow }
Write-Host "  cmake: $(& $CmakeExe --version 2>&1 | Select-Object -First 1)" -ForegroundColor Green
Write-Host "  git:   $(git --version 2>&1)" -ForegroundColor Green

# ── 1. 拉取/更新源码 ──
if (-not $SkipClone) {
    Write-Host "`n[1/6] 更新源码..." -ForegroundColor Yellow
    New-Item $SrcDir -ItemType Directory -Force | Out-Null
    Push-Location $SrcDir

    $repos = [ordered]@{
        "libultrahdr" = "https://github.com/google/libultrahdr.git"
        "jxrlib"      = "https://github.com/4creators/jxrlib.git"
        "aom"         = "https://aomedia.googlesource.com/aom"
        "libavif"     = "https://github.com/AOMediaCodec/libavif.git"
    }

    foreach ($kv in $repos.GetEnumerator()) {
        $name = $kv.Key; $url = $kv.Value
        if (Test-Path "$name\.git") {
            Write-Host "  update $name ..."
            Push-Location $name
            git fetch --depth 1 origin 2>&1 | Out-Null
            git reset --hard FETCH_HEAD 2>&1 | Out-Null
            Pop-Location
            Write-Host "  done: $name" -ForegroundColor Green
        } elseif (Test-Path $name) {
            Write-Host "  skip: $name (not a git repo)" -ForegroundColor Gray
        } else {
            Write-Host "  clone $name ..."
            git clone --depth 1 $url $name 2>&1 | Out-Null
            Write-Host "  done: $name" -ForegroundColor Green
        }
    }
    Pop-Location
} else {
    Write-Host "`n[1/6] skip clone (-SkipClone)" -ForegroundColor Yellow
}

# ── 2. 编译 libaom (静态库，供 avifenc 链接) ──
Write-Host "`n[2/6] 编译 libaom (SIMD=$SimdLevel)..." -ForegroundColor Yellow
Push-Location "$SrcDir\aom"
if (Test-Path build) { Remove-Item -Recurse -Force build }
New-Item build -ItemType Directory -Force | Out-Null; Set-Location build

& cmake .. -G "Visual Studio 18 2026" -A x64 `
    -DCMAKE_BUILD_TYPE=Release `
    -DCMAKE_CXX_FLAGS="$SimdCxxFlags" `
    -DCMAKE_C_FLAGS="$SimdCxxFlags" `
    -DENABLE_TESTS=OFF -DENABLE_DOCS=OFF `
    -DENABLE_NASM=ON -DCONFIG_LIBYUV=0 -DCONFIG_WEBM_IO=0 `
    -DCONFIG_AV1_ENCODER=ON -DCONFIG_AV1_DECODER=OFF `
    -DENABLE_EXAMPLES=OFF 2>&1 | Out-Null
cmake --build . --config Release --parallel $env:NUMBER_OF_PROCESSORS 2>&1 | Out-Null
cmake --install . --prefix "$InstallDir\aom" --config Release 2>&1 | Out-Null
Pop-Location
Write-Host "  done: libaom" -ForegroundColor Green

# ── 3. 编译 libavif → avifenc ──
Write-Host "`n[3/6] 编译 libavif (avifenc, SIMD=$SimdLevel)..." -ForegroundColor Yellow
Push-Location "$SrcDir\libavif"
if (Test-Path build) { Remove-Item -Recurse -Force build }
New-Item build -ItemType Directory -Force | Out-Null; Set-Location build

& cmake .. -G "Visual Studio 18 2026" -A x64 `
    -DCMAKE_BUILD_TYPE=Release `
    -DCMAKE_CXX_FLAGS="$SimdCxxFlags" `
    -DCMAKE_C_FLAGS="$SimdCxxFlags" `
    -DAVIF_CODEC_AOM=ON -DAVIF_CODEC_AOM_ENCODE=ON `
    -DAVIF_CODEC_DAV1D=OFF -DAVIF_BUILD_APPS=ON `
    -DAVIF_BUILD_TESTS=OFF -DAVIF_LIBYUV=ON `
    -DAVIF_LOCAL_LIBYUV=ON -DAVIF_LOCAL_AOM=OFF `
    "-DCMAKE_PREFIX_PATH=$InstallDir\aom" 2>&1 | Out-Null
cmake --build . --config Release --parallel $env:NUMBER_OF_PROCESSORS 2>&1 | Out-Null
Pop-Location
Write-Host "  done: libavif" -ForegroundColor Green

# ── 4. 编译 libultrahdr → ultrahdr_app ──
Write-Host "`n[4/6] 编译 libultrahdr (SIMD=$SimdLevel)..." -ForegroundColor Yellow
Push-Location "$SrcDir\libultrahdr"
if (Test-Path build) { Remove-Item -Recurse -Force build }
New-Item build -ItemType Directory -Force | Out-Null; Set-Location build

& cmake .. -G "Visual Studio 18 2026" -A x64 `
    -DCMAKE_BUILD_TYPE=Release `
    -DCMAKE_CXX_FLAGS="$SimdCxxFlags" `
    -DCMAKE_C_FLAGS="$SimdCxxFlags" `
    -DUHDR_BUILD_EXAMPLES=ON -DUHDR_BUILD_TESTS=OFF 2>&1 | Out-Null
cmake --build . --config Release --parallel $env:NUMBER_OF_PROCESSORS 2>&1 | Out-Null
Pop-Location
Write-Host "  done: libultrahdr" -ForegroundColor Green

# ── 5. 编译 jxrlib → JxrEncApp + JxrDecApp (CMake 支持) ──
Write-Host "`n[5/6] 编译 jxrlib (SIMD=$SimdLevel)..." -ForegroundColor Yellow
Push-Location "$SrcDir\jxrlib"
if (Test-Path build) { Remove-Item -Recurse -Force build }
New-Item build -ItemType Directory -Force | Out-Null; Set-Location build

& $CmakeExe .. -G $CmakeGen -A x64 `
    -DCMAKE_BUILD_TYPE=Release `
    -DCMAKE_C_FLAGS="$($SimdFlags -join ' ')" 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "  FAILED: cmake configure" -ForegroundColor Red; Pop-Location; exit 1 }
cmake --build . --config Release --parallel $env:NUMBER_OF_PROCESSORS 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "  FAILED: cmake build" -ForegroundColor Red; Pop-Location; exit 1 }
Pop-Location
Write-Host "  done: jxrlib (JxrEncApp + JxrDecApp)" -ForegroundColor Green

# ── 6. 部署产物 ──
Write-Host "`n[6/6] 部署到 PLAN/artifacts..." -ForegroundColor Yellow
New-Item $PlanDir -ItemType Directory -Force | Out-Null

# 清理废弃旧文件
$obsolete = @("avifdec.exe", "avifgainmaputil.exe")
foreach ($f in $obsolete) {
    $p = Join-Path $PlanDir $f
    if (Test-Path $p) { Remove-Item $p -Force; Write-Host "  removed: $f" -ForegroundColor Gray }
}

$targets = @(
    @{Src="$SrcDir\libultrahdr\build\Release\ultrahdr_app.exe"; Name="ultrahdr_app.exe"},
    @{Src="$SrcDir\jxrlib\build\Release\JxrEncApp.exe";       Name="JxrEncApp.exe"},
    @{Src="$SrcDir\jxrlib\build\Release\JxrDecApp.exe";       Name="JxrDecApp.exe"},
    @{Src="$SrcDir\libavif\build\Release\avifenc.exe";        Name="avifenc.exe"}
)

foreach ($t in $targets) {
    if (Test-Path $t.Src) {
        Copy-Item $t.Src "$PlanDir\$($t.Name)" -Force
        $kb = [math]::Round((Get-Item $t.Src).Length / 1KB, 1)
        Write-Host "  $($t.Name)  ($kb KB)" -ForegroundColor Green
    } else {
        Write-Host "  MISSING: $($t.Name)" -ForegroundColor Yellow
    }
}

# 输出版本信息
Write-Host "`n--- 版本摘要 ---" -ForegroundColor Cyan
if (Test-Path "$PlanDir\avifenc.exe") {
    $v = & "$PlanDir\avifenc.exe" --version 2>&1
    Write-Host "  avifenc: $v" -ForegroundColor Gray
}

if ($CleanAfterBuild) {
    Write-Host "`nclean build artifacts..." -ForegroundColor Yellow
    Get-ChildItem "$SrcDir\*\build" -Directory | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "`n=== BUILD COMPLETE ===" -ForegroundColor Green
Write-Host "  output: $PlanDir" -ForegroundColor Green
Write-Host "  simd:   $SimdLevel" -ForegroundColor Green
