# ═══════════════════════════════════════════════════════════
#  外部工具自编译脚本 (Windows PowerShell)
#  用法: .\build_tools.ps1 [-SkipClone] [-CleanAfterBuild] [-SimdLevel AVX2|AVX512|Native|Baseline] [-Avx512Libs "aom,jxl,avif"]
#  前提: Visual Studio 2022/2026 + CMake + Git + NASM
#
#  v4.0 变更:
#  - libjxl → 0.12.0 独立编译 cjxl/djxl/cjpegli (渐进无损 -40%, 编码 2-5x 提速)
#  - (已移除) libultrahdr: 由纯 C# GainMapEncoder/GainMapDecoder 取代，不再编译
#  - libaom → 3.15.0 (AV1 IQ 调优)
#  - libavif 链接本地 aom (-DAVIF_LOCAL_AOM=ON)
#  - zlib → 1.3.2
#  - 新增 -Avx512Libs 参数: 指定哪些库使用 AVX-512 (逗号分隔: aom,jxl,avif)
#  - aom 加入 git 管理自动更新
#  - 产物输出路径修正为 publish/PLAN/
#
#  v3.0 变更:
#  - 新增 DNG SDK 1.7.1 + LibRaw + libjxl → dngtool (DNG/RAW 解码 + DNG 编码)
#  - 构建步骤 [6/6] → [7/7]
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
    [string]$SimdLevel = "AVX2",
    [string]$Avx512Libs = "aom,jxl,avif"  # 哪些库启用 AVX-512 (仅当 SimdLevel=AVX512 时生效)
)

$ErrorActionPreference = "Stop"
$SrcDir     = "$PSScriptRoot\src"
$PlanBase   = "$PSScriptRoot\..\publish\PLAN"
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

# ── AVX-512 针对性分发 ──
# 默认全部 AVX2; 若 SimdLevel=AVX512 且库在 $Avx512Libs 中, 该库用 /arch:AVX512
$avx512Set = @($Avx512Libs -split ',') | ForEach-Object { $_.Trim().ToLower() }
function Get-ArchFlag([string]$lib) {
    if ($SimdLevel -eq "AVX512" -and $avx512Set -contains $lib.ToLower()) {
        return "/arch:AVX512"
    }
    return ($SimdFlags -join ' ')
}

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  build_tools v4.0 | VS $VsVer | SIMD: $SimdLevel" -ForegroundColor Cyan
Write-Host "  AVX-512 库: $Avx512Libs" -ForegroundColor Gray
Write-Host "  cmake: $CmakeExe" -ForegroundColor Gray
Write-Host "  产物: cjxl/djxl/cjpegli + JxrEnc/Dec + avifenc + dngtool" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan

# ── 0. 环境检查 ──
Write-Host "`n[0/7] 检查编译环境..." -ForegroundColor Yellow

# git 自动探测 (PATH 或常见安装路径)
$gitCmd = Get-Command git -ErrorAction SilentlyContinue
if (-not $gitCmd) {
    $gitCandidates = @(
        'C:\Program Files\Git\cmd\git.exe',
        'C:\Program Files\Git\bin\git.exe',
        "$env:LOCALAPPDATA\Programs\Git\cmd\git.exe"
    )
    foreach ($c in $gitCandidates) { if (Test-Path $c) { $gitCmd = $c; break } }
}
if (-not $gitCmd) { throw "git not found. 请安装 Git 或将其加入 PATH: winget install Git.Git" }
if ($gitCmd -is [string]) {
    # 全路径: 加入 PATH 前缀方便后续调用
    $env:PATH = (Split-Path $gitCmd) + ';' + $env:PATH
    Write-Host "  git: $gitCmd (已加入 PATH)" -ForegroundColor Green
} else {
    Write-Host "  git: $($gitCmd.Source)" -ForegroundColor Green
}

$nasm = Get-Command nasm -ErrorAction SilentlyContinue
if ($nasm) { Write-Host "  NASM: $($nasm.Source)" -ForegroundColor Green }
else {
    $nasmCandidates = @('C:\Program Files\NASM\nasm.exe')
    foreach ($c in $nasmCandidates) { if (Test-Path $c) { $nasm = $c; break } }
    if ($nasm -is [string]) {
        $env:PATH = (Split-Path $nasm) + ';' + $env:PATH
        Write-Host "  NASM: $nasm (已加入 PATH)" -ForegroundColor Green
    } else {
        Write-Host "  NASM: not found (slower C fallback)" -ForegroundColor Yellow
    }
}
Write-Host "  cmake: $(& $CmakeExe --version 2>&1 | Select-Object -First 1)" -ForegroundColor Green
Write-Host "  git:   $(git --version 2>&1)" -ForegroundColor Green

# cmake 加入 PATH，确保脚本内裸 `cmake` 调用可用 (VS 自带 cmake 不在 PATH)
$env:PATH = (Split-Path $CmakeExe) + ';' + $env:PATH

# ── 1. 拉取/更新源码 ──
if (-not $SkipClone) {
    Write-Host "`n[1/7] 更新源码..." -ForegroundColor Yellow
    New-Item $SrcDir -ItemType Directory -Force | Out-Null
    Push-Location $SrcDir

    $repos = [ordered]@{
        "jxrlib"      = "https://github.com/4creators/jxrlib.git"
        "aom"         = "https://aomedia.googlesource.com/aom"
        "libavif"     = "https://github.com/AOMediaCodec/libavif.git"
        "libjxl"      = "https://github.com/libjxl/libjxl.git"
        "jpegli"      = "https://github.com/google/jpegli.git"
        "zlib"        = "https://github.com/madler/zlib.git"
        "libraw"      = "https://github.com/LibRaw/LibRaw.git"
    }

    # 固定版本: 拉取指定 tag (若 repos 中定义了 tag)
    $Tags = @{
        "aom"         = "v3.15.0"
        "libavif"     = "v1.4.2"
        "libjxl"      = "v0.12.0"
        "zlib"        = "v1.3.2"
        "libraw"      = "0.22"
    }

    foreach ($kv in $repos.GetEnumerator()) {
        $name = $kv.Key; $url = $kv.Value
        $tag = $Tags[$name]
        if (Test-Path "$name\.git") {
            Write-Host "  update $name $(if($tag){"(-> $tag)"})..."
            Push-Location $name
            git fetch --tags --depth 1 origin 2>&1 | Out-Null
            if ($tag) {
                git checkout $tag 2>&1 | Out-Null
            } else {
                git reset --hard FETCH_HEAD 2>&1 | Out-Null
            }
            Pop-Location
            Write-Host "  done: $name" -ForegroundColor Green
        } elseif (Test-Path $name) {
            Write-Host "  skip: $name (not a git repo)" -ForegroundColor Gray
        } else {
            Write-Host "  clone $name ..."
            git clone --depth 1 --branch $tag $url $name 2>&1 | Out-Null
            Write-Host "  done: $name" -ForegroundColor Green
        }
    }
    Pop-Location
} else {
    Write-Host "`n[1/7] skip clone (-SkipClone)" -ForegroundColor Yellow
}

# ── 2. 编译 libaom (静态库，供 avifenc 链接) ──
Write-Host "`n[2/7] 编译 libaom 3.15.0 (SIMD=$(Get-ArchFlag 'aom'))..." -ForegroundColor Yellow
Push-Location "$SrcDir\aom"
if (Test-Path build) { Remove-Item -Recurse -Force build }
New-Item build -ItemType Directory -Force | Out-Null; Set-Location build

& cmake .. -G "Visual Studio 18 2026" -A x64 `
    -DCMAKE_BUILD_TYPE=Release `
    -DCMAKE_CXX_FLAGS="$(Get-ArchFlag 'aom')" `
    -DCMAKE_C_FLAGS="$(Get-ArchFlag 'aom')" `
    -DENABLE_TESTS=OFF -DENABLE_DOCS=OFF `
    -DENABLE_NASM=ON -DCONFIG_LIBYUV=0 -DCONFIG_WEBM_IO=0 `
    -DCONFIG_AV1_ENCODER=ON -DCONFIG_AV1_DECODER=OFF `
    -DENABLE_EXAMPLES=OFF 2>&1 | Out-Null
cmake --build . --config Release --parallel $env:NUMBER_OF_PROCESSORS 2>&1 | Out-Null
cmake --install . --prefix "$InstallDir\aom" --config Release 2>&1 | Out-Null
Pop-Location
Write-Host "  done: libaom" -ForegroundColor Green

# ── 3. 编译 libavif → avifenc ──
Write-Host "`n[3/7] 编译 libavif 1.4.2 (avifenc, SIMD=$(Get-ArchFlag 'avif'))..." -ForegroundColor Yellow
Push-Location "$SrcDir\libavif"
if (Test-Path build) { Remove-Item -Recurse -Force build }
New-Item build -ItemType Directory -Force | Out-Null; Set-Location build

& cmake .. -G "Visual Studio 18 2026" -A x64 `
    -DCMAKE_BUILD_TYPE=Release `
    -DCMAKE_CXX_FLAGS="$(Get-ArchFlag 'avif')" `
    -DCMAKE_C_FLAGS="$(Get-ArchFlag 'avif')" `
    -DAVIF_CODEC_AOM=SYSTEM -DAVIF_CODEC_AOM_ENCODE=ON -DAVIF_CODEC_AOM_DECODE=OFF `
    -DAVIF_CODEC_DAV1D=OFF -DAVIF_BUILD_APPS=ON `
    -DAVIF_BUILD_TESTS=OFF `
    -DAVIF_LIBYUV=LOCAL `
    -DAVIF_ZLIBPNG=LOCAL -DAVIF_JPEG=LOCAL `
    "-DCMAKE_PREFIX_PATH=$InstallDir\aom" 2>&1 | Out-Null
cmake --build . --config Release --parallel $env:NUMBER_OF_PROCESSORS 2>&1 | Out-Null
Pop-Location
Write-Host "  done: libavif" -ForegroundColor Green

# ── 4. (已移除) libultrahdr/ultrahdr_app: 由纯 C# GainMapEncoder 取代，不再编译 ──

# ── 5. 编译 jxrlib → JxrEncApp + JxrDecApp (CMake 支持) ──
Write-Host "`n[5/7] 编译 jxrlib (SIMD=$(Get-ArchFlag 'jxr'))..." -ForegroundColor Yellow
Push-Location "$SrcDir\jxrlib"
if (Test-Path build) { Remove-Item -Recurse -Force build }
New-Item build -ItemType Directory -Force | Out-Null; Set-Location build

& $CmakeExe .. -G $CmakeGen -A x64 `
    -DCMAKE_BUILD_TYPE=Release `
    -DCMAKE_C_FLAGS="$(Get-ArchFlag 'jxr')" 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "  FAILED: cmake configure" -ForegroundColor Red; Pop-Location; exit 1 }
cmake --build . --config Release --parallel $env:NUMBER_OF_PROCESSORS 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "  FAILED: cmake build" -ForegroundColor Red; Pop-Location; exit 1 }
Pop-Location
Write-Host "  done: jxrlib (JxrEncApp + JxrDecApp)" -ForegroundColor Green

# ── 6. 编译 libjxl 0.12.0 → cjxl/djxl/cjpegli ──
Write-Host "`n[6/7] 编译 libjxl 0.12.0 (cjxl/djxl/cjpegli, SIMD=$(Get-ArchFlag 'jxl'))..." -ForegroundColor Yellow
if (-not (Test-Path "$SrcDir\libjxl\CMakeLists.txt")) {
    Write-Host "  ⚠️ 缺少 libjxl 源码，跳过 libjxl 构建 (需 git clone https://github.com/libjxl/libjxl.git)" -ForegroundColor Yellow
} else {
    Push-Location "$SrcDir\libjxl"
    if (Test-Path build) { Remove-Item -Recurse -Force build }
    New-Item build -ItemType Directory -Force | Out-Null; Set-Location build

    & $CmakeExe .. -G $CmakeGen -A x64 `
        -DCMAKE_BUILD_TYPE=Release `
        -DCMAKE_C_FLAGS="$(Get-ArchFlag 'jxl')" `
        -DCMAKE_CXX_FLAGS="$(Get-ArchFlag 'jxl')" `
        -DBUILD_TESTING=OFF `
        -DJPEGXL_ENABLE_TOOLS=ON -DJPEGXL_ENABLE_DEVTOOLS=OFF `
        -DJPEGXL_ENABLE_BENCHMARK=OFF -DJPEGXL_ENABLE_EXAMPLES=OFF `
        -DJPEGXL_ENABLE_MANPAGES=OFF -DJPEGXL_ENABLE_JNI=OFF `
        -DJPEGXL_ENABLE_PLUGINS=OFF -DJPEGXL_BUNDLE_LIBPNG=OFF `
        -DJPEGXL_ENABLE_SJPEG=ON -DJPEGXL_ENABLE_OPENEXR=OFF 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "  FAILED: libjxl configure" -ForegroundColor Red; Pop-Location }
    else {
        & $CmakeExe --build . --config Release --parallel $env:NUMBER_OF_PROCESSORS 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Host "  FAILED: libjxl build" -ForegroundColor Red; Pop-Location }
        else { Write-Host "  done: libjxl 0.12.0" -ForegroundColor Green; Pop-Location }
    }
}

# ── 6.2 编译独立 jpegli (google/jpegli) ──
Write-Host "`n[6.2/7] 编译 google/jpegli (cjpegli/djpegli, SIMD=$(Get-ArchFlag 'jpegli'))..." -ForegroundColor Yellow
if (-not (Test-Path "$SrcDir\jpegli\CMakeLists.txt")) {
    Write-Host "  ⚠️ 缺少 jpegli 源码，跳过 (需 git clone https://github.com/google/jpegli.git)" -ForegroundColor Yellow
} else {
    Push-Location "$SrcDir\jpegli"
    if (Test-Path build) { Remove-Item -Recurse -Force build }
    New-Item build -ItemType Directory -Force | Out-Null; Set-Location build

    & $CmakeExe .. -G $CmakeGen -A x64 `
        -DCMAKE_BUILD_TYPE=Release `
        -DCMAKE_C_FLAGS="$(Get-ArchFlag 'jpegli')" `
        -DCMAKE_CXX_FLAGS="$(Get-ArchFlag 'jpegli')" `
        -DJPEGLI_ENABLE_FUZZERS=OFF -DBUILD_TESTING=OFF `
        -DJPEGLI_BUNDLE_LIBPNG=ON 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "  FAILED: jpegli configure" -ForegroundColor Red; Pop-Location }
    else {
        & $CmakeExe --build . --config Release --parallel $env:NUMBER_OF_PROCESSORS 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Host "  FAILED: jpegli build" -ForegroundColor Red; Pop-Location }
        else { Write-Host "  done: jpegli" -ForegroundColor Green; Pop-Location }
    }
}

# ── 6.3 部署 jpegli 产物 ──
Write-Host "`n[6.3/7] 部署 jpegli 产物到 PLAN/jxl/bin..." -ForegroundColor Yellow
$JpegliBinSrc = "$SrcDir\jpegli\build\tools\Release"
$JpegliLibSrc = "$SrcDir\jpegli\build\lib\Release"
if (Test-Path $JpegliBinSrc) {
    Copy-Item "$JpegliBinSrc\cjpegli.exe" "$PlanBase\jxl\bin\" -Force -ErrorAction SilentlyContinue
    Copy-Item "$JpegliBinSrc\djpegli.exe" "$PlanBase\jxl\bin\" -Force -ErrorAction SilentlyContinue
    Get-ChildItem "$JpegliLibSrc\*.dll" -ErrorAction SilentlyContinue | ForEach-Object { Copy-Item $_.FullName "$PlanBase\jxl\bin\" -Force }
    Write-Host "  cjpegli.exe, djpegli.exe, jpegli_*.dll deployed" -ForegroundColor Green
}

# ── 6.5 部署 libjxl 产物 ──
Write-Host "`n[6.5/7] 部署 libjxl 产物到 PLAN/jxl/bin..." -ForegroundColor Yellow
$JxlBinSrc = "$SrcDir\libjxl\build\tools\Release"
$JxlBinDst = "$PlanBase\jxl\bin"
New-Item $JxlBinDst -ItemType Directory -Force | Out-Null
$jxlTargets = @("cjxl.exe", "djxl.exe", "jxlinfo.exe", "jxltran.exe")
foreach ($t in $jxlTargets) {
    $src = Join-Path $JxlBinSrc $t
    if (Test-Path $src) {
        Copy-Item $src "$JxlBinDst\$t" -Force
        $kb = [math]::Round((Get-Item $src).Length / 1KB, 1)
        Write-Host "  $t  ($kb KB)" -ForegroundColor Green
    } else {
        Write-Host "  MISSING: $t" -ForegroundColor Yellow
    }
}
# 部署 libjxl 运行时 DLL
$jxlDlls = @("jxl.dll", "jxl_cms.dll", "jxl_threads.dll", "jxl_dec.dll", 
             "brotlicommon.dll", "brotlidec.dll", "brotlienc.dll")
foreach ($dll in $jxlDlls) {
    $src = Join-Path "$SrcDir\libjxl\build\lib\Release" $dll
    $src2 = Join-Path "$SrcDir\libjxl\build\tools\Release" $dll
    $src3 = Join-Path "$SrcDir\libjxl\build\third_party\brotli\Release" $dll
    $realSrc = if (Test-Path $src) { $src } elseif (Test-Path $src2) { $src2 } elseif (Test-Path $src3) { $src3 } else { $null }
    if ($realSrc) { Copy-Item $realSrc "$JxlBinDst\$dll" -Force }
}

# ── 6.6 编译 zlib 1.3.2 ──
if (Test-Path "$SrcDir\zlib\CMakeLists.txt") {
    Write-Host "`n[6.6/7] 编译 zlib 1.3.2 (SIMD=$(Get-ArchFlag 'zlib'))..." -ForegroundColor Yellow
    Push-Location "$SrcDir\zlib"
    if (Test-Path build) { Remove-Item -Recurse -Force build }
    New-Item build -ItemType Directory -Force | Out-Null; Set-Location build

    & $CmakeExe .. -G $CmakeGen -A x64 `
        -DCMAKE_BUILD_TYPE=Release `
        -DCMAKE_C_FLAGS="$(Get-ArchFlag 'zlib')" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "  FAILED: zlib configure" -ForegroundColor Red; Pop-Location }
    else {
        & $CmakeExe --build . --config Release --parallel $env:NUMBER_OF_PROCESSORS 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Host "  FAILED: zlib build" -ForegroundColor Red; Pop-Location }
        else {
            & $CmakeExe --install . --prefix "$InstallDir\zlib" --config Release 2>&1 | Out-Null
            Write-Host "  done: zlib 1.3.2" -ForegroundColor Green; Pop-Location
        }
    }
} else {
    Write-Host "  ⚠️ 缺少 zlib 源码 (git clone https://github.com/madler/zlib.git)，跳过 zlib 构建" -ForegroundColor Yellow
}

# ── 6.8 部署基础产物 ──
Write-Host "`n[6.8/7] 部署产物到 PLAN/artifacts..." -ForegroundColor Yellow
New-Item $PlanDir -ItemType Directory -Force | Out-Null

# 清理废弃旧文件（ultrahdr_app 已由纯 C# GainMapEncoder 取代）
$obsolete = @("avifdec.exe", "avifgainmaputil.exe", "libaom.dll", "ultrahdr_app.exe", "libuhdr.dll")
foreach ($f in $obsolete) {
    $p = Join-Path $PlanDir $f
    if (Test-Path $p) { Remove-Item $p -Force; Write-Host "  removed: $f" -ForegroundColor Gray }
}

$targets = @(
    @{Src="$SrcDir\jxrlib\build\Release\JxrEncApp.exe";       Name="JxrEncApp.exe"},
    @{Src="$SrcDir\jxrlib\build\Release\JxrDecApp.exe";       Name="JxrDecApp.exe"},
    @{Src="$SrcDir\libavif\build\Release\avifenc.exe";        Name="avifenc.exe"},
    @{Src="$SrcDir\libavif\build\Release\avif.dll";            Name="avif.dll"}
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
if (Test-Path "$PlanBase\jxl\bin\cjxl.exe") {
    $v = & "$PlanBase\jxl\bin\cjxl.exe" --version 2>&1
    Write-Host "  cjxl: $v" -ForegroundColor Gray
}

# ── 7. 编译 dngtool (LibRaw + DNG SDK 1.7.1 + libjxl) ──
Write-Host "`n[7/7] 编译 dngtool (DNG SDK 1.7.1 + LibRaw + libjxl)..." -ForegroundColor Yellow

# 环境要求: cmake 4.x 需要此变量以兼容旧版 cmake_minimum_required
$env:CMAKE_POLICY_VERSION_MINIMUM = "3.5"

$DngSdkRoot = "$SrcDir\dng_sdk\dng_sdk_1_7_1"
$DngSdkExists = Test-Path "$DngSdkRoot\dng_sdk\source\dng_validate.cpp"
$LibRawExists = Test-Path "$SrcDir\libraw\libraw\libraw.h"

if (-not ($DngSdkExists -and $LibRawExists)) {
    Write-Host "  ⚠️ 缺少 DNG SDK 或 LibRaw 源码，跳过 dngtool 构建" -ForegroundColor Yellow
    Write-Host "  DNG SDK: $DngSdkExists (需手动下载 dng_sdk_1_7_1.zip 解压到 $SrcDir\dng_sdk)" -ForegroundColor Yellow
    Write-Host "  LibRaw:  $LibRawExists (需 git clone https://github.com/LibRaw/LibRaw.git)" -ForegroundColor Yellow
} else {
    # 7a. 构建 DNG SDK 自带的 libjxl (0.8, 与 dng_jxl.cpp 版本匹配)
    $JxlSrc = "$DngSdkRoot\libjxl\libjxl"
    $JxlBuild = "$JxlSrc\build"
    if (-not (Test-Path "$JxlBuild\Release\jxl.lib")) {
        Write-Host "  [7a] 构建自带 libjxl 0.8 (供 DNG SDK 链接)..." -ForegroundColor Gray
        if (Test-Path $JxlBuild) { Remove-Item -Recurse -Force $JxlBuild }
        New-Item $JxlBuild -ItemType Directory -Force | Out-Null
        Push-Location $JxlBuild
        & $CmakeExe .. -G $CmakeGen -A x64 `
            -DCMAKE_BUILD_TYPE=Release `
            -DBUILD_TESTING=OFF `
            -DJPEGXL_ENABLE_TOOLS=OFF -DJPEGXL_ENABLE_DEVTOOLS=OFF `
            -DJPEGXL_ENABLE_BENCHMARK=OFF -DJPEGXL_ENABLE_EXAMPLES=OFF `
            -DJPEGXL_ENABLE_MANPAGES=OFF -DJPEGXL_ENABLE_JNI=OFF `
            -DJPEGXL_ENABLE_PLUGINS=OFF -DJPEGXL_BUNDLE_LIBPNG=OFF `
            -DJPEGXL_ENABLE_SJPEG=OFF -DJPEGXL_ENABLE_OPENEXR=OFF 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Host "  FAILED: libjxl configure" -ForegroundColor Red; Pop-Location }
        else {
            & $CmakeExe --build . --config Release --parallel $env:NUMBER_OF_PROCESSORS 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) { Write-Host "  FAILED: libjxl build" -ForegroundColor Red; Pop-Location }
            else { Write-Host "  done: libjxl 0.8" -ForegroundColor Green; Pop-Location }
        }
    } else {
        Write-Host "  [7a] libjxl 0.8 已构建，跳过" -ForegroundColor Gray
    }

    # 7b. 构建 dngtool
    $DngToolSrc = "$SrcDir\dngtool"
    $DngToolBuild = "$DngToolSrc\build"
    Write-Host "  [7b] 构建 dngtool..." -ForegroundColor Gray
    if (Test-Path $DngToolBuild) { Remove-Item -Recurse -Force $DngToolBuild }
    New-Item $DngToolBuild -ItemType Directory -Force | Out-Null
    Push-Location $DngToolBuild
    & $CmakeExe .. -G $CmakeGen -A x64 `
        -DCMAKE_BUILD_TYPE=Release `
        -DDNG_JXL_LIB_DIR="$JxlBuild\Release" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "  FAILED: dngtool configure" -ForegroundColor Red; Pop-Location }
    else {
        & $CmakeExe --build . --config Release --parallel $env:NUMBER_OF_PROCESSORS 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Host "  FAILED: dngtool build" -ForegroundColor Red; Pop-Location }
        else {
            # 部署
            $DngToolExe = "$DngToolBuild\Release\dngtool.exe"
            if (Test-Path $DngToolExe) {
                Copy-Item $DngToolExe "$PlanDir\dngtool.exe" -Force
                $kb = [math]::Round((Get-Item $DngToolExe).Length / 1KB, 1)
                Write-Host "  done: dngtool.exe ($kb KB)" -ForegroundColor Green
            } else {
                Write-Host "  MISSING: dngtool.exe" -ForegroundColor Yellow
            }
            Pop-Location
        }
    }
}

if ($CleanAfterBuild) {
    Write-Host "`nclean build artifacts..." -ForegroundColor Yellow
    Get-ChildItem "$SrcDir\*\build" -Directory | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "`n=== BUILD COMPLETE ===" -ForegroundColor Green
Write-Host "  output: $PlanDir" -ForegroundColor Green
Write-Host "  simd:   $SimdLevel" -ForegroundColor Green
