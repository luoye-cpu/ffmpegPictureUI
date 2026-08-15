# ═══════════════════════════════════════════════════════════
#  dngtool — DNG/RAW 解码 + DNG 编码 CLI 构建
#  LibRaw (LGPL) + DNG SDK 1.7.1 (Adobe, MIT) + libjxl (BSD-3) + zlib
#
#  用法:
#    cmake .. -G "Visual Studio 18 2026" -A x64 -DCMAKE_BUILD_TYPE=Release
#    cmake --build . --config Release
# ═══════════════════════════════════════════════════════════
cmake_minimum_required(VERSION 3.20)
project(dngtool C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(MSVC)
  # /MD 动态 CRT: 与 libjxl 0.8 (MSVCP140.dll 动态) 匹配
  add_compile_options(/O2 /W3 /EHsc /utf-8 /MD)
  add_link_options(/NODEFAULTLIB:libcmt.lib /NODEFAULTLIB:libcpmt.lib)
endif()

# ── 路径 ──
set(DNG_SDK_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../dng_sdk/dng_sdk_1_7_1")
set(DNG_SDK_SRC "${DNG_SDK_ROOT}/dng_sdk/source")
set(JXL_SRC_INC "${DNG_SDK_ROOT}/libjxl/libjxl/lib/include")
set(JXL_CLIENT_INC "${DNG_SDK_ROOT}/libjxl/client_projects/include")
set(ZLIB_SRC "${CMAKE_CURRENT_SOURCE_DIR}/../zlib")
set(LIBRAW_SRC "${CMAKE_CURRENT_SOURCE_DIR}/../libraw")

# JXL 库目录: 优先 DNG SDK 自带 libjxl 0.8 构建产物 (版本匹配), 回退 publish 的 0.11.2
set(DNG_JXL_LIB_DIR "" CACHE PATH "DNG SDK bundled libjxl build output (Release)")
if(DNG_JXL_LIB_DIR AND EXISTS "${DNG_JXL_LIB_DIR}/jxl.lib")
  set(JXL_LIB_DIR "${DNG_JXL_LIB_DIR}")
else()
  set(JXL_LIB_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../../publish/PLAN/jxl/lib")
endif()

# ═══════════════════════════════════════════════════════════
# dng_sdk 静态库 (排除主程序 dng_validate.cpp 与 XMP 相关文件)
# ═══════════════════════════════════════════════════════════
file(GLOB DNG_SDK_SOURCES "${DNG_SDK_SRC}/*.cpp")
list(REMOVE_ITEM DNG_SDK_SOURCES
    "${DNG_SDK_SRC}/dng_validate.cpp"   # 主程序
    "${DNG_SDK_SRC}/dng_update_meta.cpp" # 依赖 XMP SDK
    "${DNG_SDK_SRC}/dng_xmp.cpp"         # XMP 实现 (qDNGUseXMP=0)
    "${DNG_SDK_SRC}/dng_xmp_sdk.cpp"     # XMP SDK 封装
)

add_library(dng_sdk STATIC ${DNG_SDK_SOURCES})
target_include_directories(dng_sdk PUBLIC
    "${DNG_SDK_SRC}"
    "${JXL_SRC_INC}"
    "${JXL_CLIENT_INC}"
    "${ZLIB_SRC}"
)
target_link_libraries(dng_sdk PUBLIC zlib_static)
target_compile_definitions(dng_sdk PUBLIC
    _CONSOLE _CRT_SECURE_NO_DEPRECATE
    qWinOS=1 qMacOS=0 qVisualC=1 _WINDOWS=1 BIB_MULTI_THREAD=1
    qDNGUseXMP=0
    qDNGValidate=1   # dng_globals 的 gDumpLineLimit 等符号 (dng_validate 需要)
)

# ═══════════════════════════════════════════════════════════
# zlib (源码, 仅编译所需)
# ═══════════════════════════════════════════════════════════
add_library(zlib_static STATIC
    "${ZLIB_SRC}/adler32.c"   "${ZLIB_SRC}/compress.c"
    "${ZLIB_SRC}/crc32.c"     "${ZLIB_SRC}/deflate.c"
    "${ZLIB_SRC}/gzclose.c"   "${ZLIB_SRC}/gzlib.c"
    "${ZLIB_SRC}/gzread.c"    "${ZLIB_SRC}/gzwrite.c"
    "${ZLIB_SRC}/infback.c"   "${ZLIB_SRC}/inffast.c"
    "${ZLIB_SRC}/inflate.c"   "${ZLIB_SRC}/inftrees.c"
    "${ZLIB_SRC}/trees.c"     "${ZLIB_SRC}/uncompr.c"
    "${ZLIB_SRC}/zutil.c"
)
target_include_directories(zlib_static PUBLIC "${ZLIB_SRC}")
if(MSVC)
  target_compile_definitions(zlib_static PRIVATE _CRT_SECURE_NO_DEPRECATE)
endif()

# ═══════════════════════════════════════════════════════════
# LibRaw 静态库 (USE_DNGSDK 启用 DNG SDK 集成, 含 JXL-DNG 解码)
# ═══════════════════════════════════════════════════════════
file(GLOB LIBRAW_SOURCES
    "${LIBRAW_SRC}/src/*.cpp"
    "${LIBRAW_SRC}/src/decoders/*.cpp"
    "${LIBRAW_SRC}/src/decompressors/*.cpp"
    "${LIBRAW_SRC}/src/demosaic/*.cpp"
    "${LIBRAW_SRC}/src/integration/*.cpp"
    "${LIBRAW_SRC}/src/metadata/*.cpp"
    "${LIBRAW_SRC}/src/postprocessing/*.cpp"
    "${LIBRAW_SRC}/src/preprocessing/*.cpp"
    "${LIBRAW_SRC}/src/tables/*.cpp"
    "${LIBRAW_SRC}/src/utils/*.cpp"
    "${LIBRAW_SRC}/src/write/*.cpp"
    "${LIBRAW_SRC}/src/x3f/*.cpp"
)
# 排除占位实现 (无后处理工具时的桩, 会覆盖真实实现)
list(REMOVE_ITEM LIBRAW_SOURCES
    "${LIBRAW_SRC}/src/postprocessing/postprocessing_ph.cpp"
    "${LIBRAW_SRC}/src/preprocessing/preprocessing_ph.cpp"
    "${LIBRAW_SRC}/src/write/write_ph.cpp"
)

add_library(libraw_static STATIC ${LIBRAW_SOURCES})
target_include_directories(libraw_static PUBLIC
    "${LIBRAW_SRC}"
    "${LIBRAW_SRC}/libraw"
    "${DNG_SDK_SRC}"
    "${JXL_SRC_INC}"
    "${JXL_CLIENT_INC}"
)
target_compile_definitions(libraw_static PUBLIC
    _WINDOWS UNICODE _UNICODE WIN32 _ENABLE_EXTENDED_ALIGNED_STORAGE WIN64
    LIBRAW_BUILDLIB NDEBUG
    USE_DNGSDK
    qDNGSupportJXL
)
target_link_libraries(libraw_static PUBLIC dng_sdk zlib_static)

# ═══════════════════════════════════════════════════════════
# dngtool 主程序
# ═══════════════════════════════════════════════════════════
add_executable(dngtool dngtool.cpp)

# libjxl 0.8 静态库 (与 DNG SDK dng_jxl.cpp 版本匹配)
set(JXL08_LIB_DIR "${DNG_SDK_ROOT}/libjxl/libjxl/build/lib/Release")
set(JXL08_TP_DIR "${DNG_SDK_ROOT}/libjxl/libjxl/build/third_party")

if(EXISTS "${JXL08_LIB_DIR}/jxl-static.lib")
  target_link_libraries(dngtool PRIVATE
      libraw_static
      dng_sdk
      zlib_static
      "${JXL08_LIB_DIR}/jxl-static.lib"
      "${JXL08_LIB_DIR}/jxl_threads-static.lib"
      "${JXL08_TP_DIR}/brotli/Release/brotlidec-static.lib"
      "${JXL08_TP_DIR}/brotli/Release/brotlienc-static.lib"
      "${JXL08_TP_DIR}/brotli/Release/brotlicommon-static.lib"
      "${JXL08_TP_DIR}/highway/Release/hwy.lib"
      "${JXL08_TP_DIR}/skcms-obj.dir/Release/skcms-obj.lib"
  )
else()
  # 回退: 使用 publish 的 libjxl 0.11.2 (版本可能不匹配, 仅诊断用)
  target_link_libraries(dngtool PRIVATE
      libraw_static
      dng_sdk
      zlib_static
      "${JXL_LIB_DIR}/jxl.lib"
      "${JXL_LIB_DIR}/jxl_threads.lib"
      "${JXL_LIB_DIR}/jxl_cms.lib"
      "${JXL_LIB_DIR}/brotlidec.lib"
      "${JXL_LIB_DIR}/brotlienc.lib"
      "${JXL_LIB_DIR}/brotlicommon.lib"
      "${JXL_LIB_DIR}/hwy.lib"
  )
endif()

# ═══════════════════════════════════════════════════════════
# dng_validate (Adobe DNG SDK 官方校验器) — 诊断 dngtool 输出兼容性
# ═══════════════════════════════════════════════════════════
add_executable(dng_validate "${DNG_SDK_SRC}/dng_validate.cpp")
target_include_directories(dng_validate PRIVATE
    "${DNG_SDK_SRC}"
    "${JXL_SRC_INC}"
    "${JXL_CLIENT_INC}"
    "${ZLIB_SRC}"
)
target_compile_definitions(dng_validate PRIVATE
    _CONSOLE _CRT_SECURE_NO_DEPRECATE
    qWinOS=1 qMacOS=0 qVisualC=1 _WINDOWS=1 BIB_MULTI_THREAD=1
    qDNGUseXMP=0 qDNGSupportJXL=1 qDNGValidateTarget=1
)
target_link_libraries(dng_validate PRIVATE
    dng_sdk
    zlib_static
    "${JXL08_LIB_DIR}/jxl-static.lib"
    "${JXL08_LIB_DIR}/jxl_threads-static.lib"
    "${JXL08_TP_DIR}/brotli/Release/brotlidec-static.lib"
    "${JXL08_TP_DIR}/brotli/Release/brotlienc-static.lib"
    "${JXL08_TP_DIR}/brotli/Release/brotlicommon-static.lib"
    "${JXL08_TP_DIR}/highway/Release/hwy.lib"
    "${JXL08_TP_DIR}/skcms-obj.dir/Release/skcms-obj.lib"
)
