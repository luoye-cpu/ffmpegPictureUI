# DNG 工具链构建依赖说明

`dngtool`(DNG 1.7 JXL 解码/编码)依赖两个**第三方库**,它们**不属于本项目源码**,需按以下步骤手动准备。`.gitignore` 已排除,不会进入仓库。

## 依赖清单

| 依赖 | 用途 | 获取方式 | 所需修改 |
|---|---|---|---|
| **Adobe DNG SDK 1.7.1** | DNG 解析/写入 (含 JXL 压缩) | 下载官方 zip 解压 | 见下文 |
| **LibRaw** | 相机 RAW 解码 (25+ 格式) | `git clone` | 见下文 |
| DNG SDK 自带 libjxl 0.8 | JXL 编解码 (必须 0.8, 与 dng_jxl.cpp 匹配) | 随 DNG SDK 附带 | 无 |

## 准备步骤

### 1. DNG SDK 1.7.1

```
下载: http://download.adobe.com/pub/adobe/dng/dng_sdk_1_7_1.zip  (~80MB)
解压到: tools/src/dng_sdk/  (目录结构: tools/src/dng_sdk/dng_sdk_1_7_1/)
```

**必需修改** (dng_sdk 源码, 编译时 `qDNGUseXMP=0` 不启用 XMP):

1. `dng_sdk/source/dng_jxl.cpp` — 两处 XMP 代码需用 `#if qDNGUseXMP` 包裹:
   - 约 805 行 (JXL 元数据写入处)
   - 约 3012 行 (JXL 元数据解析处)
2. 编译时排除以下文件 (由 `tools/src/dngtool/CMakeLists.txt` 处理):
   - `dng_validate.cpp` (主程序)
   - `dng_update_meta.cpp` (XMP 依赖)
   - `dng_xmp.cpp` / `dng_xmp_sdk.cpp` (XMP 依赖)

### 2. LibRaw

```
获取: git clone https://github.com/LibRaw/LibRaw.git tools/src/libraw
(当前使用 master, 含 DNG SDK 集成支持)
```

**必需修改** (编译时由 `tools/src/dngtool/CMakeLists.txt` 处理):

1. 编译宏: `USE_DNGSDK` + `qDNGSupportJXL`
2. **必须排除 3 个占位文件** (否则 `dcraw_process` 返回 NOT_IMPLEMENTED):
   - `postprocessing_ph.cpp`
   - `preprocessing_ph.cpp`
   - `write_ph.cpp`
3. 运行时启用 DNG SDK 路径 (dngtool.cpp 内):
   ```cpp
   processor.set_dng_host(&host);
   processor.imgdata.rawparams.use_dngsdk = LIBRAW_DNG_ALL;
   processor.imgdata.rawparams.options |= (1u << 27); // DNG_STAGE23_IFPRESENT_JPGJXL
   ```

## 构建

```powershell
# 完整构建 (含 [7/7] dngtool):
tools/build_tools.ps1

# 仅 dngtool (依赖已就绪时):
cmake --build tools/src/dngtool/build --config Release
```

`build_tools.ps1` 的 `[7/7]` 步骤会检测上述依赖,缺失时提示并跳过。

## 为什么这样设计

- **第三方库不进仓库**:体积大 (DNG SDK 源码 ~40MB + LibRaw ~3MB),且不属于本项目内容;与 `aom/dcraw/jxrlib` 的处理一致
- **修改以编译参数/排除文件方式固化**:集中在 `dngtool/CMakeLists.txt` 与 `dngtool.cpp`,避免直接改动第三方源码
- 唯一无法参数化的两处 `dng_jxl.cpp` XMP 修改,建议保留在本地 (或提交为补丁时需标注版本)
