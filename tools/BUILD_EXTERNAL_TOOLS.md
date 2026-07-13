# Windows 外部工具自编译指南

> 适用于: Windows x64 | 编译器: Visual Studio 2026 + CMake

---

## 环境准备

```powershell
# 1. 安装 Visual Studio 2026（Community/Pro/Enterprise）
#    确保勾选: "使用C++的桌面开发" 工作负载

# 2. 安装 CMake（或使用 VS 自带的）
#    https://cmake.org/download/
#    或使用 winget: winget install cmake

# 3. 安装 Git
#    https://git-scm.com/
#    或使用 winget: winget install git.git

# 4. 安装 NASM（libaom/avifenc 需要）
#    https://www.nasm.us/
#    或使用 winget: winget install nasm

# 5. 克隆源码仓库（放在项目根目录的 tools/ 下）
mkdir tools\src
cd tools\src
```

---

## 一、编译 ultrahdr_app（libultrahdr）

```powershell
# 仓库: https://github.com/google/libultrahdr
cd tools\src
git clone --depth 1 https://github.com/google/libultrahdr.git
cd libultrahdr

# 编译
mkdir build && cd build
cmake .. -G "Visual Studio 18 2026" -A x64 `
    -DCMAKE_BUILD_TYPE=Release `
    -DUHDR_BUILD_EXAMPLES=ON `
    -DUHDR_BUILD_TESTS=OFF

cmake --build . --config Release --parallel

# 产物位置
# build\Release\ultrahdr_app.exe
# build\Release\ultrahdr.dll

# 复制到 PLAN
copy build\Release\ultrahdr_app.exe ..\..\..\publish\PLAN\windows-artifacts\ /Y
```

**版本**: 当前 `main` 分支（v1.4.0+，持续更新）
**优化**: `/O2 /GL /arch:AVX2` 已由 CMake Release 配置自动启用

---

## 二、编译 JxrEncApp / JxrDecApp（jxrlib）

```powershell
# 仓库: https://github.com/4creators/jxrlib (社区维护，支持 VS2026)
cd tools\src
git clone --depth 1 https://github.com/4creators/jxrlib.git
cd jxrlib

# 使用 CMake 构建
mkdir build && cd build
cmake .. -G "Visual Studio 18 2026" -A x64 `
    -DCMAKE_BUILD_TYPE=Release `
    -DBUILD_SHARED_LIBS=OFF

cmake --build . --config Release --parallel

# 产物位置
# build\Release\JxrEncApp.exe
# build\Release\JxrDecApp.exe

# 复制到 PLAN
copy build\Release\JxrEncApp.exe ..\..\..\publish\PLAN\windows-artifacts\ /Y
copy build\Release\JxrDecApp.exe ..\..\..\publish\PLAN\windows-artifacts\ /Y
```

**注意**: 官方 Microsoft jxrlib (https://github.com/microsoft/jxrlib) 仅支持旧版 VS。
# 社区 fork `4creators/jxrlib` 添加了 CMake + VS2026 支持。
若官方仓库已更新，优先使用官方版本。

---

## 三、编译 avifenc（libavif + libaom）

avifenc 依赖 libaom（AV1 编码器）和 dav1d（AV1 解码器）。

### 3.1 编译 libaom（AV1 编码器）

```powershell
cd tools\src
git clone --depth 1 https://aomedia.googlesource.com/aom
cd aom

mkdir build && cd build
cmake .. -G "Visual Studio 18 2026" -A x64 `
    -DCMAKE_BUILD_TYPE=Release `
    -DENABLE_TESTS=OFF `
    -DENABLE_DOCS=OFF `
    -DENABLE_NASM=ON `
    -DCONFIG_AV1_ENCODER=1 `
    -DCONFIG_AV1_DECODER=1 `
    -DCONFIG_LIBYUV=0 `
    -DCONFIG_WEBM_IO=0

cmake --build . --config Release --parallel

# 安装到本地目录
cmake --install . --prefix ..\..\install\aom --config Release
```

### 3.2 编译 dav1d（AV1 解码器，可选但推荐）

```powershell
cd tools\src
git clone --depth 1 https://code.videolan.org/videolan/dav1d.git
cd dav1d

mkdir build && cd build
# dav1d 使用 Meson 构建系统，Windows 上也可用 CMake 分支
# 推荐使用 vcpkg 安装 dav1d，或跳过（libaom 自带解码器）
```

### 3.3 编译 libavif

```powershell
cd tools\src
git clone --depth 1 https://github.com/AOMediaCodec/libavif.git
cd libavif

mkdir build && cd build
cmake .. -G "Visual Studio 18 2026" -A x64 `
    -DCMAKE_BUILD_TYPE=Release `
    -DAVIF_CODEC_AOM=ON `
    -DAVIF_CODEC_AOM_ENCODE=ON `
    -DAVIF_CODEC_AOM_DECODE=ON `
    -DAVIF_CODEC_DAV1D=OFF `
    -DAVIF_BUILD_APPS=ON `
    -DAVIF_BUILD_TESTS=OFF `
    -DAVIF_LIBYUV=ON `
    -DAVIF_LOCAL_LIBYUV=ON `
    -DAVIF_LOCAL_AOM=OFF `
    -DCMAKE_PREFIX_PATH="..\..\install\aom"

cmake --build . --config Release --parallel

# 产物位置
# build\Release\avifenc.exe
# build\Release\avifdec.exe

# 复制到 PLAN
copy build\Release\avifenc.exe ..\..\..\publish\PLAN\windows-artifacts\ /Y
copy build\Release\avifdec.exe ..\..\..\publish\PLAN\windows-artifacts\ /Y
```

---

## 四、一键编译脚本（保存为 build_tools.ps1）

```powershell
# tools\build_tools.ps1
param([switch]$SkipClone)

$ErrorActionPreference = "Stop"
$SrcDir = "$PSScriptRoot\src"
$PlanDir = "$PSScriptRoot\..\publish\PLAN\windows-artifacts"
$CmakeGen = "Visual Studio 18 2026"

if (-not $SkipClone) {
    # Clone repos
    Push-Location $SrcDir
    if (-not (Test-Path libultrahdr)) { git clone --depth 1 https://github.com/google/libultrahdr.git }
    if (-not (Test-Path jxrlib))      { git clone --depth 1 https://github.com/4creators/jxrlib.git }
    if (-not (Test-Path aom))          { git clone --depth 1 https://aomedia.googlesource.com/aom }
    if (-not (Test-Path libavif))      { git clone --depth 1 https://github.com/AOMediaCodec/libavif.git }
    Pop-Location
}

function Build-CMake {
    param($Name, $Path)
    Write-Host "`n=== Building $Name ===" -ForegroundColor Cyan
    Push-Location $Path
    if (Test-Path build) { Remove-Item -Recurse -Force build }
    mkdir build -Force | Out-Null; cd build
    cmake .. -G $CmakeGen -A x64 -DCMAKE_BUILD_TYPE=Release
    cmake --build . --config Release --parallel
    Pop-Location
}

# Build all
Build-CMake "libultrahdr" "$SrcDir\libultrahdr"
Build-CMake "jxrlib"      "$SrcDir\jxrlib"
Build-CMake "aom"          "$SrcDir\aom"
Build-CMake "libavif"      "$SrcDir\libavif"

# Copy to PLAN
Write-Host "`n=== Copying to PLAN ===" -ForegroundColor Cyan
mkdir $PlanDir -Force | Out-Null
@(
    "$SrcDir\libultrahdr\build\Release\ultrahdr_app.exe",
    "$SrcDir\jxrlib\build\Release\JxrEncApp.exe",
    "$SrcDir\jxrlib\build\Release\JxrDecApp.exe",
    "$SrcDir\libavif\build\Release\avifenc.exe",
    "$SrcDir\libavif\build\Release\avifdec.exe"
) | ForEach-Object {
    if (Test-Path $_) {
        copy $_ $PlanDir -Force
        Write-Host "  ✅ $(Split-Path $_ -Leaf)" -ForegroundColor Green
    } else {
        Write-Host "  ⚠️ $_ not found" -ForegroundColor Yellow
    }
}

Write-Host "`n=== Done ===" -ForegroundColor Green
```

---

## 五、版本对照与更新策略

| 工具 | 当前版本 | 最新稳定版 | 更新命令 |
|------|---------|-----------|---------|
| ultrahdr_app | v1.4.0 | main 分支 | `cd tools/src/libultrahdr && git pull` |
| JxrEncApp | — | — | `cd tools/src/jxrlib && git pull` |
| avifenc | 1.4.2 (aom 3.14.1) | 1.5.0+ | `cd tools/src/libavif && git pull` |

**建议更新频率**: 每季度重新编译一次，跟上上游最新优化。
