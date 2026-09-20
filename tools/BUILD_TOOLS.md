# 外部工具编译说明 / Build Tools Guide

> 最后更新 / Last Updated: 2026-09-01  
> 编译环境: Visual Studio 2026 (18) Community + CMake 4.3.1-msvc1 + NASM 3.02

---

## 外部工具清单

| 工具 | 源码 | 当前版本 | 编译方式 | SIMD | 用途 |
|------|------|---------|---------|------|------|
| **ffmpeg** | 预编译 | git-2026-08-06 | mingw64 (x86-64-v3) | v3 | 主转码引擎 |
| **ffprobe** | 预编译 | git-2026-08-06 | mingw64 | - | 媒体探测 |
| **cjxl** | libjxl | **v0.12.0** | VS 2026 自编译 | AVX2/AVX512 | JPEG→JXL / 渐进无损 -40% |
| **djxl** | libjxl | **v0.12.0** | VS 2026 自编译 | AVX2/AVX512 | JXL→图像 / 解码加速 |
| **cjpegli** | libjxl | **v0.12.0** | VS 2026 自编译 | AVX2/AVX512 | JPEG 编码 (独立 jpegli) |
| **ultrahdr_app** | libultrahdr | **v2.0.2** | VS 2026 自编译 | AVX2/AVX512 | Ultra HDR (含 HEIF/AVIF 容器) |
| **JxrEncApp** | jxrlib | 2026-07-14 | VS 2026 CMake 自编译 | AVX2 | JPEG XR 编码 |
| **JxrDecApp** | jxrlib | 2026-07-14 | VS 2026 CMake 自编译 | AVX2 | JXR 解码 |
| **avifenc** | libavif | 1.4.2 | VS 2026 自编译 | AVX2/AVX512 | AVIF 编码 (链接本地 aom 3.15.0) |
| **aom (静态库)** | aom | **v3.15.0** | VS 2026 自编译 | AVX2/AVX512 | AV1 编码核心库 |
| **zlib** | zlib | **v1.3.2** | VS 2026 自编译 | AVX2/AVX512 | 压缩库 |
| **dngtool** | LibRaw + DNG SDK | 0.22 / 1.7.1 | VS 2026 自编译 | AVX2 | DNG/RAW 编解码 |
| **exiftool** | Image-ExifTool | 13.59 | 预编译 | - | 元数据处理 |

### 已移除工具 (v2.0)

| 工具 | 原因 |
|------|------|
| **avifdec** | 项目未使用 |
| **avifgainmaputil** | 项目未使用 |
| **aomenc** | 项目未使用 (改用静态库) |
| **aomdec** | 项目未使用 |

---

## Windows 编译

### 前提

```powershell
winget install Git.Git
winget install Kitware.CMake      # 若未安装 VS 2026
winget install NASM.NASM          # NASM 3.02+
```

- Visual Studio 2026 (含 C++ 桌面开发) / VS 2022 回退
- NASM 3.02+ (已加入 PATH)

### 一键编译

```powershell
cd tools
# 默认 AVX2 (兼容所有 x64 CPU)
.\build_tools.ps1 -SimdLevel AVX2

# 高端部署: 开启 AVX-512 (需要目标 CPU 支持)
.\build_tools.ps1 -SimdLevel AVX512 -Avx512Libs "aom,jxl,avif"
```

参数:
- `-SkipClone`: 跳过 git fetch/checkout (离线构建)
- `-SimdLevel`: AVX2 (默认) / AVX512 / Native / Baseline
- `-Avx512Libs`: 仅当 SimdLevel=AVX512 时生效，逗号分隔指定库 (默认: aom,jxl,avif)
- `-CleanAfterBuild`: 编译后清理中间文件

### 产物位置

```
publish/PLAN/
├── artifacts/           ← ultrahdr_app, JxrEncApp, JxrDecApp, avifenc, dngtool
├── ffmpeg-full/         ← ffmpeg, ffprobe (预编译)
├── jxl/bin/             ← cjxl, djxl, cjpegli, djpegli, jxlinfo (自编译 0.12.0)
└── exiftool/            ← exiftool (预编译)
```

---

## 版本固化策略

脚本内置 `$Tags` 哈希表固定各库版本，确保可复现：

```powershell
$Tags = @{
    "libultrahdr" = "v2.0.2"
    "aom"         = "v3.15.0"
    "libavif"     = "v1.4.2"
    "libjxl"      = "v0.12.0"
    "zlib"        = "v1.3.2"
    "libraw"      = "0.22"
}
```

> 升级版本只需修改 `$Tags`，脚本会自动 `git fetch --tags --depth 1 origin` 并 `git checkout <tag>`。

---

## Linux 编译 (含 ARM)

```bash
#!/bin/bash
# build_tools.sh — Linux/ARM 交叉编译

SRC_DIR="$(dirname "$0")/src"
INSTALL_DIR="$SRC_DIR/install"
PLAN_DIR="$(dirname "$0")/../publish/PLAN/artifacts"
JOBS=$(nproc)

# SIMD: x86 → -mavx2, ARM → -march=armv8.4-a+sve2
SIMD_FLAGS="-mavx2 -mfma"  # 或 "-march=armv8.4-a+sve2" (ARM)
BUILD_TYPE=Release

mkdir -p "$SRC_DIR" "$INSTALL_DIR" "$PLAN_DIR"

# 1. aom (v3.15.0)
git -C "$SRC_DIR/aom" fetch --tags --depth 1 origin
git -C "$SRC_DIR/aom" checkout v3.15.0
cmake -S "$SRC_DIR/aom" -B "$SRC_DIR/aom/build" \
    -G "Unix Makefiles" -DCMAKE_BUILD_TYPE=$BUILD_TYPE \
    -DCMAKE_C_FLAGS="$SIMD_FLAGS" -DCMAKE_CXX_FLAGS="$SIMD_FLAGS" \
    -DENABLE_TESTS=OFF -DENABLE_DOCS=OFF -DENABLE_NASM=ON \
    -DCONFIG_AV1_ENCODER=ON -DCONFIG_AV1_DECODER=OFF
cmake --build "$SRC_DIR/aom/build" -j$JOBS
cmake --install "$SRC_DIR/aom/build" --prefix "$INSTALL_DIR/aom"

# 2. zlib (v1.3.2)
git -C "$SRC_DIR/zlib" fetch --tags --depth 1 origin
git -C "$SRC_DIR/zlib" checkout v1.3.2
cmake -S "$SRC_DIR/zlib" -B "$SRC_DIR/zlib/build" \
    -G "Unix Makefiles" -DCMAKE_BUILD_TYPE=$BUILD_TYPE
cmake --build "$SRC_DIR/zlib/build" -j$JOBS
cmake --install "$SRC_DIR/zlib/build" --prefix "$INSTALL_DIR/zlib"

# 3. libavif (v1.4.2) → avifenc (链接本地 aom)
git -C "$SRC_DIR/libavif" fetch --tags --depth 1 origin
git -C "$SRC_DIR/libavif" checkout v1.4.2
cmake -S "$SRC_DIR/libavif" -B "$SRC_DIR/libavif/build" \
    -G "Unix Makefiles" -DCMAKE_BUILD_TYPE=$BUILD_TYPE \
    -DCMAKE_C_FLAGS="$SIMD_FLAGS" -DCMAKE_CXX_FLAGS="$SIMD_FLAGS" \
    -DAVIF_CODEC_AOM=ON -DAVIF_CODEC_AOM_ENCODE=ON \
    -DAVIF_CODEC_DAV1D=OFF -DAVIF_BUILD_APPS=ON \
    -DAVIF_LOCAL_AOM=ON \
    -DCMAKE_PREFIX_PATH="$INSTALL_DIR/aom;$INSTALL_DIR/zlib"
cmake --build "$SRC_DIR/libavif/build" -j$JOBS

# 4. libultrahdr (v2.0.2) — 启用 HEIF/AVIF
git -C "$SRC_DIR/libultrahdr" fetch --tags --depth 1 origin
git -C "$SRC_DIR/libultrahdr" checkout v2.0.2
cmake -S "$SRC_DIR/libultrahdr" -B "$SRC_DIR/libultrahdr/build" \
    -G "Unix Makefiles" -DCMAKE_BUILD_TYPE=$BUILD_TYPE \
    -DCMAKE_C_FLAGS="$SIMD_FLAGS" -DCMAKE_CXX_FLAGS="$SIMD_FLAGS" \
    -DUHDR_BUILD_EXAMPLES=ON -DUHDR_BUILD_DEPS=1 \
    -DUHDR_ENABLE_HEIF=ON -DUHDR_ENABLE_AVIF=ON
cmake --build "$SRC_DIR/libultrahdr/build" -j$JOBS

# 5. jxrlib
cmake -S "$SRC_DIR/jxrlib" -B "$SRC_DIR/jxrlib/build" \
    -G "Unix Makefiles" -DCMAKE_BUILD_TYPE=$BUILD_TYPE \
    -DCMAKE_C_FLAGS="$SIMD_FLAGS"
cmake --build "$SRC_DIR/jxrlib/build" -j$JOBS

# 6. libjxl (v0.12.0) → cjxl/djxl/cjpegli
git -C "$SRC_DIR/libjxl" fetch --tags --depth 1 origin
git -C "$SRC_DIR/libjxl" checkout v0.12.0
cmake -S "$SRC_DIR/libjxl" -B "$SRC_DIR/libjxl/build" \
    -G "Unix Makefiles" -DCMAKE_BUILD_TYPE=$BUILD_TYPE \
    -DCMAKE_C_FLAGS="$SIMD_FLAGS" -DCMAKE_CXX_FLAGS="$SIMD_FLAGS" \
    -DBUILD_TESTING=OFF \
    -DJPEGXL_ENABLE_TOOLS=ON -DJPEGXL_ENABLE_DEVTOOLS=OFF \
    -DJPEGXL_ENABLE_BENCHMARK=OFF -DJPEGXL_ENABLE_EXAMPLES=OFF \
    -DJPEGXL_ENABLE_MANPAGES=OFF -DJPEGXL_ENABLE_JNI=OFF \
    -DJPEGXL_ENABLE_PLUGINS=OFF -DJPEGXL_BUNDLE_LIBPNG=OFF \
    -DJPEGXL_ENABLE_SJPEG=ON -DJPEGXL_ENABLE_OPENEXR=OFF
cmake --build "$SRC_DIR/libjxl/build" -j$JOBS

# 7. 部署
cp "$SRC_DIR/aom/build/libaom.a" "$INSTALL_DIR/aom/lib/" 2>/dev/null
cp "$SRC_DIR/libavif/build/avifenc" "$PLAN_DIR/avifenc"
cp "$SRC_DIR/jxrlib/build/JxrEncApp" "$PLAN_DIR/JxrEncApp"
cp "$SRC_DIR/jxrlib/build/JxrDecApp" "$PLAN_DIR/JxrDecApp"
cp "$SRC_DIR/libultrahdr/build/ultrahdr_app" "$PLAN_DIR/ultrahdr_app"
mkdir -p "$PLAN_DIR/../jxl/bin"
cp "$SRC_DIR/libjxl/build/tools/cjxl" "$PLAN_DIR/../jxl/bin/cjxl"
cp "$SRC_DIR/libjxl/build/tools/djxl" "$PLAN_DIR/../jxl/bin/djxl"
cp "$SRC_DIR/libjxl/build/tools/cjpegli" "$PLAN_DIR/../jxl/bin/cjpegli"

```
publish/PLAN/
├── artifacts/           ← ultrahdr_app, JxrEncApp, JxrDecApp, avifenc
├── ffmpeg-full/         ← ffmpeg, ffprobe (预编译)
├── jxl/bin/             ← cjxl, djxl, cjpegli (预编译)
└── exiftool/            ← exiftool (预编译)
```

---

## Linux 编译 (含 ARM)

```bash
#!/bin/bash
# build_tools.sh — Linux/ARM 交叉编译

SRC_DIR="$(dirname "$0")/src"
INSTALL_DIR="$SRC_DIR/install"
PLAN_DIR="$(dirname "$0")/../publish/PLAN/artifacts"
JOBS=$(nproc)

# SIMD: x86 → -mavx2, ARM → -march=armv8.4-a+sve2
SIMD_FLAGS="-mavx2 -mfma"  # 或 "-march=armv8.4-a+sve2" (ARM)
BUILD_TYPE=Release

mkdir -p "$SRC_DIR" "$INSTALL_DIR" "$PLAN_DIR"

# 1. aom
cmake -S "$SRC_DIR/aom" -B "$SRC_DIR/aom/build" \
    -G "Unix Makefiles" -DCMAKE_BUILD_TYPE=$BUILD_TYPE \
    -DCMAKE_C_FLAGS="$SIMD_FLAGS" -DCMAKE_CXX_FLAGS="$SIMD_FLAGS" \
    -DENABLE_TESTS=OFF -DENABLE_DOCS=OFF -DENABLE_NASM=ON \
    -DCONFIG_AV1_ENCODER=ON -DCONFIG_AV1_DECODER=OFF
cmake --build "$SRC_DIR/aom/build" -j$JOBS
cmake --install "$SRC_DIR/aom/build" --prefix "$INSTALL_DIR/aom"

# 2. zlib
cmake -S "$SRC_DIR/zlib" -B "$SRC_DIR/zlib/build" \
    -G "Unix Makefiles" -DCMAKE_BUILD_TYPE=$BUILD_TYPE
cmake --build "$SRC_DIR/zlib/build" -j$JOBS
cmake --install "$SRC_DIR/zlib/build" --prefix "$INSTALL_DIR/zlib"

# 3. libavif → avifenc
cmake -S "$SRC_DIR/libavif" -B "$SRC_DIR/libavif/build" \
    -G "Unix Makefiles" -DCMAKE_BUILD_TYPE=$BUILD_TYPE \
    -DCMAKE_C_FLAGS="$SIMD_FLAGS" -DCMAKE_CXX_FLAGS="$SIMD_FLAGS" \
    -DAVIF_CODEC_AOM=ON -DAVIF_BUILD_APPS=ON \
    -DCMAKE_PREFIX_PATH="$INSTALL_DIR/aom;$INSTALL_DIR/zlib"
cmake --build "$SRC_DIR/libavif/build" -j$JOBS

# 4. libultrahdr
cmake -S "$SRC_DIR/libultrahdr" -B "$SRC_DIR/libultrahdr/build" \
    -G "Unix Makefiles" -DCMAKE_BUILD_TYPE=$BUILD_TYPE \
    -DCMAKE_C_FLAGS="$SIMD_FLAGS" -DCMAKE_CXX_FLAGS="$SIMD_FLAGS" \
    -DUHDR_BUILD_EXAMPLES=ON
cmake --build "$SRC_DIR/libultrahdr/build" -j$JOBS

# 5. jxrlib → JxrEncApp + JxrDecApp (CMake 支持)
cmake -S "$SRC_DIR/jxrlib" -B "$SRC_DIR/jxrlib/build" \
    -G "Unix Makefiles" -DCMAKE_BUILD_TYPE=$BUILD_TYPE \
    -DCMAKE_C_FLAGS="$SIMD_FLAGS"
cmake --build "$SRC_DIR/jxrlib/build" -j$JOBS

# 6. 部署
cp "$SRC_DIR/libultrahdr/build/ultrahdr_app" "$PLAN_DIR/"
cp "$SRC_DIR/libavif/build/avifenc"         "$PLAN_DIR/"
cp "$SRC_DIR/jxrlib/build/JxrEncApp"        "$PLAN_DIR/"
cp "$SRC_DIR/jxrlib/build/JxrDecApp"        "$PLAN_DIR/"
# 注意: ultrahdr_app 需要 libuhdr.so (Linux) 或 libuhdr.dll (Windows)
cp "$SRC_DIR/libultrahdr/build/libuhdr."*   "$PLAN_DIR/"

echo "Done. Output: $PLAN_DIR"
```

### ARM 注意事项

- ARM64 下 NASM 不可用，aom 使用 C 回退（较慢）
- SVE/SVE2: 使用 `-march=armv8.4-a+sve2`
- jxrlib: ✅ 已支持 CMake 跨平台编译

---

## SIMD 优化说明

| 级别 | MSVC (/arch:) | GCC/Clang (-m) | 适用 CPU |
|------|---------------|-----------------|----------|
| SSE2 | `/arch:SSE2` | `-msse2` | 所有 x86-64 |
| AVX2 | `/arch:AVX2` | `-mavx2 -mfma` | Haswell+ (2013) |
| AVX-512 | `/arch:AVX512` | `-mavx512f -mavx512bw` | Skylake-X+ (2017) |
| Native | `/favor:blend` | `-march=native` | 自动检测 |

libaom/libavif 包含**运行时 CPU 检测**，即使编译时未指定 SIMD 也能在运行时使用最优指令。

---

## 版本更新流程

1. `git pull` 各仓库 (`tools/src/`)
2. `.\build_tools.ps1 -SimdLevel AVX2`
3. 复制产物至 `publish/PLAN/artifacts/`
4. 验证: `cjxl --version` / `avifenc --version` / `ultrahdr_app --help`
5. 更新本文件版本号
