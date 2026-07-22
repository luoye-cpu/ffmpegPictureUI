# 外部工具编译说明 / Build Tools Guide

> 最后更新 / Last Updated: 2026-07-23  
> 编译环境: Visual Studio 2026 (18) Community + CMake 4.3.1-msvc1 + NASM 2.16.01

---

## 外部工具清单

| 工具 | 源码 | 当前版本 | 编译方式 | SIMD | 用途 |
|------|------|---------|---------|------|------|
| **ffmpeg** | 预编译 | git-2026-07-05 | mingw64 (x86-64-v3) | v3 | 主转码引擎 |
| **ffprobe** | 预编译 | git-2026-07-05 | mingw64 | - | 媒体探测 |
| **cjxl** | libjxl | v0.11.2 | 预编译 | AVX2,SSE2 | JPEG→JXL |
| **djxl** | libjxl | v0.11.2 | 预编译 | AVX2,SSE2 | JXL→图像 |
| **cjpegli** | libjxl | v0.11.2 | 预编译 | AVX2,SSE2 | JPEG 编码 |
| **ultrahdr_app** | libultrahdr | v1.4.0+ | VS 2026 自编译 | AVX2 | Ultra HDR |
| **JxrEncApp** | jxrlib | 2026-07-14 | VS 2026 CMake 自编译 | AVX2 | JPEG XR 编码 |
| **JxrDecApp** | jxrlib | 2026-07-14 | VS 2026 CMake 自编译 | AVX2 | JXR 解码 |
| **avifenc** | libavif | 1.4.2 | 自编译 | AVX2 | AVIF 编码 |
| **exiftool** | Image-ExifTool | 13.58 | 预编译 | - | 元数据处理 |

### 已移除工具 (v2.0)

| 工具 | 原因 |
|------|------|
| **avifdec** | 项目未使用 |
| **avifgainmaputil** | 项目未使用 |
| **aomenc** | 项目未使用 |
| **aomdec** | 项目未使用 |

---

## Windows 编译

### 前提

```powershell
winget install Git.Git
winget install Kitware.CMake      # 若未安装 VS 2026
```

- Visual Studio 2022 或 2026 (含 C++ 桌面开发)
- NASM (随 Strawberry Perl 安装或单独安装)

### 一键编译

```powershell
cd tools
.\build_tools.ps1 -SimdLevel AVX2
```

参数:
- `-SkipClone`: 跳过 git pull (离线构建)
- `-SimdLevel`: AVX2 (默认) / AVX512 / Native / Baseline
- `-CleanAfterBuild`: 编译后清理中间文件

### 产物位置

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
