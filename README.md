# 🖼️ FFmpegPictureUI — FFmpeg 图片转换器

**v1.5.3** — 2026-08-15 Release | Cross-platform batch image/animation/video converter built on Avalonia UI.
基于 Avalonia UI 的跨平台批量图片/动图/视频转换工具，封装 `ffmpeg`/`ffprobe` + 外部编码器 (`cjxl`/`djxl`/`cjpegli`/`JxrEncApp`/`JxrDecApp`).

QQ 交流群：754439779 | [点击加群](https://qm.qq.com/q/M2181PvCkW)

---

## ✨ Core Features / 核心功能

| Feature 功能 | Description 说明 |
|---|---|
| **Multi-format / 多格式** | JPEG, PNG, WebP, AVIF, JPEG XL, TIFF — plus animated: GIF, WebP (animated), APNG, AVIF (animated), JPEG XL (animated). JPEG LI 已整合为 JPEG 的 cjpegli 编码器选项 |
| **Encoder backend / 编码器后端** | Selectable ffmpeg / cjxl / cjpegli per format; cjxl for JXL lossless JPEG repack — 每种格式可选不同编码器后端 |
| **Quality control / 质量控制** | Quality slider (snap-to-tick) + format-aware numeric input — 滑块吸附整数 + 格式感知数字输入框 (JPEG q:v 2-31, JXL distance 0-15, etc.) |
| **Advanced codec options / 高级编码选项** | Per-format advanced panels: DCT algo, progressive mode, Huffman optimize, adaptive quant, sjpeg backend, PSNR target, lossless compression level, row-mt, still-picture, modular mode — 按格式独立高级面板 |
| **Color management / 色彩管理** | sRGB/BT.709/BT.2020 PQ/HLG 快速选择；CICP (H.273) 始终启用；4 种 ICC 模式（无/携带/烘焙+嵌入/仅烘焙）；iccgen 自动生成标准 ICC；zscale 双向烘焙；HDR→SDR 色调映射降级；BT.2020 自动位深联动；Gain Map RGB 建议 |
| **JXL Intelligence / JXL 智能** | Auto-detects JPEG-reconstruction vs native codestream; byte-level inspection (`JxlInspector`); picks optimal pipeline |
| **JPEG-LI / JPEG-LI** | `cjpegli` 作为 JPEG 格式的编码器后端选项，提供完整高级配置（色度子采样、渐进模式等）|
| **CPU SIMD / CPU 指令集** | Auto-detects AVX2/AVX/SSE4 capable binaries; runtime probe validates compatibility |
| **Batch queue / 批量队列** | Drag & drop; configurable concurrency (1–128); stop-after-queue |
| **Metadata editing / 元数据编辑** | ~90-field panel via exiftool; 9 categories (Basic, DateTime, Camera, Shooting, GPS, Image, IPTC, XMP, Color); double-click file opens editor — ~90字段9大分类exiftool编辑器，双击文件打开 |
| **Privacy cleaning / 隐私清理** | Strip GPS, timestamps, camera info, all EXIF, XMP |
| **Quality analysis / 质量分析** | SSIM + PSNR post-encode; auto-detects lossless; **.NET native PSNR** (AVX512/AVX2/SSE2, 20× faster than ffmpeg filter); **target-domain analysis** (RGB-native formats → RGB, YUV-native → YUV, matches ffmpeg 0.0000dB); **bit-depth normalized** (8/10/16-bit, MaxValue scaling) |
| **Presets / 预设** | 29 built-in presets with secondary management window; save/load/import user presets — 29个内置预设+二级管理窗口，支持保存/加载/导入 |
| **Dual theme / 双色主题** | Dark/Light mode; queue text adapts — 队列文字颜色自适应主题 |
| **Bilingual UI / 双语界面** | 中文 / English one-click toggle, top-right button; JSON resource files — 右上角按钮一键切换 |
| **Format filter / 格式筛选** | Checkbox window to enable/disable recognized image formats; persists to settings — 勾选启用的图片格式，持久化保存 |
| **Animation mode / 动图模式** | Mode toggle (Still/Animated); FPS/loop/scale/duration controls (auto or manual); per-format advanced animated panels; video input support — 模式切换，帧率/循环/缩放/时长参数，视频输入支持 |
| **Lossless lock / 无损锁定** | PNG/TIFF/APNG auto-lock quality at max, disable slider — 无损格式自动锁定最高质量 |
| **Search drag-drop / 搜索拖放** | Windows Search result files correctly resolved via Shell namespace paths — Windows 搜索结果拖放正确解析 |
| **Gain Map (Ultra HDR) / 增益图** | JPEG 输出支持 Gain Map HDR 编码（需 libultrahdr）；自动检测编码器可用性 — Ultra HDR JPEG with backward compat |
| **Real-time progress / 实时进度** | 详情窗口实时更新命令+进度，支持 ffmpeg/cjxl/cjpegli/djxl/管道全部后端 — live command & progress for all backends |

---

## 🔧 External Tools / 外部工具依赖

| Tool 工具 | Status 状态 | Role 用途 |
|---|---|---|
| `ffmpeg` + `ffprobe` | ✅ Required / 必需 | Core encoding/decoding, media probing |
| `cjxl` / `djxl` / `cjpegli` | ⭐ Recommended / 推荐 | JXL transcode, decoding, JPEG-LI encoding |
| `JxrEncApp` / `JxrDecApp` | ⭐ Recommended / 推荐 | JPEG XR encoding/decoding (Microsoft jxrlib) |
| `avifenc` | ⚪ Optional / 可选 | GIF → AVIF two-step encoding with alpha preservation |
| `dngtool` | ⭐ Recommended / 推荐 | DNG 1.7 JXL 解码/编码, RAW 去马赛克 (LibRaw + Adobe DNG SDK) |
| `exiftool` | ⚪ Optional / 可选 | Metadata editing, privacy cleaning, ICC profile embedding |


## 🚀 Quick Start / 快速开始

### Prerequisites / 前提条件

- **OS / 系统**: Windows 10/11 (其他 .NET 11 平台应可运行)
- **.NET 11 Runtime**: [Download / 下载](https://dotnet.microsoft.com/en-us/download/dotnet/11.0)
- **FFmpeg**: Install and ensure `ffmpeg -version` works / 安装并确认终端可运行 `ffmpeg -version`

```bash
# Clone and build / 克隆并构建
git clone https://github.com/luoye-cpu/PLAN-1.git
cd PLAN-1/ffmpegPictureUI
dotnet build src/FfmpegGui/FfmpegGui.csproj -c Release
dotnet run --project src/FfmpegGui/FfmpegGui.csproj
```

Or download from [Releases / 发布页](https://github.com/luoye-cpu/ffmpegPictureUI/releases).

---

## 🔬 JXL Pipeline / JXL 转换管线

The app inspects JXL file type at byte level and picks the optimal path:

应用通过字节级检测判断 JXL 文件类型并自动选择最优路径：

### Scene A — JPEG Reconstruction / 场景 A — JPEG 套壳

```
.jxl (JPEG-wrapped)  ──djxl──▶  .jpg (bit-exact, zero quality loss)
                                 .jpg（位级还原，零质量损失）
```

### Scene B — Native Codestream / 场景 B — 原生 JXL

```
.jxl (native)  ──djxl──▶  PNG stream  ══pipe══▶  cjpegli  ──▶  .jpg (preferred)
                 ──djxl──▶  temp PNG   ──▶  cjpegli       ──▶  .jpg (fallback)
                 ──ffmpeg libjxl──▶  mjpeg                ──▶  .jpg (last resort)
```

---

## 🏗️ Project Structure / 项目结构

```
ffmpegPictureUI/
├── src/FfmpegGui/
│   ├── Models/           AppSettings, FfmpegOptions, QueueItem, PresetData
│   ├── Services/         FfmpegCommandBuilder, FfmpegRunner, QueueProcessor,
│   │                     CjxlService, DjxlService, CjpegliService,
│   │                     JxlInspector, JxlPipelineService, JxrService,
│   │                     ExternalToolsDetector, CpuFeatureService,
│   │                     ExifToolService, FormatCapabilitiesService,
│   │                     EncoderDetectionService, QualityAnalysisService,
│   │                     ColorEncodingHelper, IccProfileService,
│   │                     PresetManagerService, RawService, UltrahdrService,
│   │                     GpuCapabilityService, PlatformServices,
│   │                     LocalizationService, PsRenderService, PsnrCalculator,
│   │                     SimdPixelOps (Photoshop ACR 验证, .NET 原生 PSNR, SIMD)
│   ├── Controls/         MetadataEditor
│   ├── Resources/Locales/ zh-CN.json, en-US.json
│   ├── LocExtension.cs   XAML localization markup extension
│   ├── MainWindow.xaml   Primary UI
│   ├── MainWindow.xaml.cs UI logic
│   ├── FormatFilterWindow.axaml  Format filter dialog
│   ├── PresetManagerWindow.axaml Preset manager window
│   ├── ProgressWindow.xaml Progress UI
├── tools/                Verification utilities
├── tests/                Testing (output/ ignored by git, see docs/TESTING.md)
└── publish/              Publish output
```

---

## 📝 Changelog / 更新日志

### v1.5.3 (2026-08-15) — 原生 PSNR + 目标域质量分析 + SIMD 加速

**📊 .NET 原生 PSNR（替代 ffmpeg psnr filter）**
- 新增 `PsnrCalculator.cs` — 纯 .NET 实现，**无外部依赖**，60MP 大图 **20× 加速**（359ms→17.6ms）
- 自动 dispatch: AVX512BW → AVX2 → SSE2 → 标量回退
- 位深归一化: `MaxValue = (1 << bitsPerSample) - 1`，8/10/16-bit 统一计算
- 多帧语义: `average` = 全局 MSE 聚合，`min`/`max` = 逐帧极值（与 ffmpeg 一致）

**🎯 目标域质量分析（彻底解决跨格式域偏差）**
- RGB 系格式（PNG/TIFF/JXL/APNG/GIF/BMP）→ RGB 域（rgb24/rgb48le）
- YUV 系格式（JPEG/WebP/AVIF/HEIC/JXR）→ YUV 域（yuv444p/yuv444p16le）
- 与 ffmpeg psnr filter **逐位一致（0.0000dB，D37 断言）**
- `scale=out_range=pc` 统一 full range（消除 limited vs full 值域错乱）
- UI 质量分析结果标注 `(RGB)` / `(YUV)` 域

**⚡ GainMap SIMD 加速**
- 新增 `SimdPixelOps.cs` — 4 个热点，实测数据驱动取舍
- `FloatToSrgb8` AVX2 **2.5×**（0.15→0.06ms，1024 项 LUT + gather 插值）
- 已集成 GainMapEncoder（ReinhardToSdr / WriteBgra8PngAsync 批量转换 / ComputeGainMap 灰度）
- 已集成 GainMapDecoder（SrgbToLinearRgba）


### v1.5.2 (2026-08-13) — 管线修复与元数据增强 / Pipeline Fix & Metadata Enhancements

**🔧 管线修复**
- **修复 ffprobe 色彩字段错位解析** — `-show_entries` 逗号分隔仅最后一个字段生效，实际输出 `pix_fmt,color_space,color_primaries,color_transfer`（无 bits_per_raw_sample），此前 primaries/transfer 一直被交换。修正解析 + 位深解析器重写（yuvj420p→8、yuv420p10le→10、rgb48le→16 全覆盖）
- **修复 RAW 预处理临时目录泄漏** — `raw_{GUID}` 目录任务完成后统一清理
- **JXR 命令显示过期** — 显示实际 PPM/PAM 管道命令

**🖼 编码增强**
- **JPEG XL 无损重封装开关** — 高级选项可独立关闭（关闭时显式 `--lossless_jpeg=0` 重新编码）
- **光子噪声自动 ISO** — 可选从每张输入照片 EXIF 自动读取 ISO（每图独立，比固定值准确）

**🏗 平台**
- **NativeAOT 打包** — 2 版本发布（单文件版 / 完整版含 PLAN），全部 NativeAOT 编译，无需 .NET Runtime
- **PLAN ffmpeg 目录模糊匹配** — 目录名包含 "ffmpeg-full" 即自动识别（如 ffmpeg-full-2026.7.24）

---

### v1.5.1 (2026-07-27) — 色彩管线修复 / Color Pipeline Fix

**🎨 色彩管理修复**
- **修复 SDR→HDR 像素未转换** — 16-bit TIFF 等无元数据输入手动指定 BT.2020 PQ/HLG 时，输出仅有 HDR 标签但像素未做电光转换（画面偏暗）。`BuildColorArgsSplit` 简化模式改为返回实际输入色彩，新增通用目标色域 zscale 转换逻辑
- **修复 JXL→PNG 卡死** — 管道模式下 `inputPath="-"` 被传给 ffprobe 探测函数导致无限阻塞。三个探测函数添加管道守卫
- **HDR→SDR 排除判断** — 简化模式 zscale 转换排除 HDR→SDR 场景（交给 tonemap 处理，避免高光裁剪）

---

### v1.5.0 (2026-07-23) — 正式版 / Stable Release

**🎛 编码器与质量**
- **AVIF 深度优化** — libaom 新增 aq-mode（自适应量化，Variance/Complexity）、CDEF 方向增强滤波、帧内块复制(intrabc)、胶片颗粒合成(denoise 0-50)；NVENC 新增 aq-strength(0-15)+空间自适应量化(spatial-aq)；QSV/VAAPI 新增低功耗模式(low_power)
- **硬件编码器 7 档精细预设** — NVENC(p1~p7) / QSV(veryfast~veryslow) / VAAPI(compression_level 1~7) 独立面板，默认最高质量(p7/veryslow/7)
- **29 个内置预设** — 覆盖 AOM×4 / SVT×4 / NVENC×3 / QSV×3 / JPEG LI×3 / JXL×4 / WebP×3 / PNG×2 / TIFF / Ultra HDR / GIF，全部使用最新参数
- **智能默认值** — 不勾选"高级编码选项"即可获得优化输出，所有参数内置高质量默认（cpu-used=4, still-picture=1, aq-mode=variance, huffman=optimal...）
- **PNG 增强** — 6 种预测模式带中文场景说明 + DPI 打印分辨率（默认不设，纯可选）

**🌐 界面与体验**
- **双语界面** — 中文 / English 一键切换，右上角按钮即时生效，JSON 资源文件
- **简洁模式** — 同窗口极简覆盖视图，拖放文件直接入队，自动编码开关
- **GPU UI 加速** — Windows ANGLE/D3D11 渲染，GPU/CPU 按钮可切换
- **便携化部署** — 配置与预设存于 exe 同目录，零 %AppData% 依赖，拷贝即用

**🎨 色彩管理**
- **ICC 系统重写** — 4 种新模式：①无ICC(CICP) ②携带ICC ③烘焙+嵌入 ④仅烘焙；zscale 像素烘焙；iccgen 自动生成标准 ICC
- **CICP 始终启用** — H.273 色彩标记在所有模式生效；非 CICP 格式非 sRGB 时自动嵌入 ICC
- **HDR→SDR 自动降级** — 输出格式不支持 HDR 时自动色调映射；双重转换冲突检测与锁定
- **色彩空间快速选择** — sRGB / BT.709 / BT.2020 PQ / BT.2020 HLG；BT.2020 自动 ≥10-bit

**🔧 工具与架构**
- **工具面板重构** — 3 列水平布局（JXL库|exiftool|artifacts）；紧凑状态栏后台检测完自动显示 ✅/❌；PLAN 便携包自动识别
- **GPU 编码器检测** — 启动时自动检测 NVENC/QSV/AMF 可用性并逐编码器运行时验证
- **检测模块重写** — 全异步后台管线，真实超时保护，增量日志
- **打包优化** — 精简调试符号 ~100MB；Resources/Locales/ 多语言资源自动包含

**🐛 修复与优化**
- 消除重复 settings.json I/O；7 个外部工具并行检测
- Windows 搜索拖放路径正确解析
- 输出类型 WinExe，无 CMD 窗口


<details>
<summary>v1.5.0 Beta 版本详情 / Beta Version Details</summary>

### v1.5.0 BETA3 (2026-07-15)
- **色彩空间重构** — 简化选择器：sRGB / BT.709 / BT.2020 PQ / BT.2020 HLG；移除 BT.601；选择后自动填充 primaries/trc/matrix；BT.2020 根据源位深自动 ≥10-bit
- **ICC 系统重写** — 4 种新模式（无ICC / 携带ICC / 烘焙+嵌入 / 仅烘焙）；zscale 像素烘焙；iccgen 自动生成标准 ICC；烘焙目标跟随色彩空间选择
- **CICP 始终启用** — H.273 标记在所有模式生效；非 CICP 格式非 sRGB 时自动嵌入 ICC
- **HDR→SDR 降级** — 输出格式不支持 HDR 时自动 zscale+tonemap；位深比较警告
- **双重转换锁定** — ICC 烘焙时锁定手动色彩参数，6 种冲突场景检测
- **打包优化** — 移除原生 .pdb 调试符号 ~100MB；55 项管线测试矩阵全部通过

### v1.5.0 BETA2 (2026-07-14)
- **简洁模式** — 同窗口极简覆盖视图；拖放直接入队；自动编码开关；预设同步主界面
- **GPU 编码器检测** — 启动时自动检测 QSV/NVENC/AMF；逐编码器运行时验证；✅⚡⚠️❌ 彩色状态提示
- **GPU UI 加速** — ANGLE/D3D11 渲染（Windows）；GPU/CPU 一键切换；`--no-gpu` 命令行回退
- **启动优化** — 消除重复 I/O；7 个外部工具 Task.WhenAll 并行检测；GPU 验证延迟到后台
- **便携化部署** — 配置/预设存于 exe 同目录 `presets/`；零 %AppData% 依赖；拷贝即用

### v1.5.0 BETA (2026-07-14)
- **ICC 色彩管理 v1** — 外部 .icc/.icm 加载；exiftool/iccgen 嵌入；zscale 像素烘焙；sRGB~Rec.2100 完整色彩空间映射
- **预设系统 v2.0** — 24 内置预设 + 二级管理窗口 + 用户 JSON 预设 CRUD
- **工具面板重构** — 3 列水平布局；紧凑状态栏后台检测完自动显示 ✅/❌；PLAN 便携包自动识别
- **检测模块重写** — 全异步 3 阶段后台管线；每步 8s 超时；增量 Dispatcher 日志
- **AVIF 编码器面板** — AOM/SVT/NVENC/QSV/AMF 各自独立选项，编码器切换时动态切换面板
- **动图与 RAW** — 视频转动图时长限制；dngtool RAW 解码自动检测；扩展 RAW 格式支持

</details>

<details>
<summary>v1.4.5 及更早 / v1.4.5 & Earlier</summary>

- **v1.4.5** — Windows 搜索结果拖放路径正确解析（Shell 命名空间）；JXL 无损 JPEG 重封装（直接复制 DCT 系数，5-10× 速度）；Linux ARM 迁移技术分析完成
- **v1.3.0** — UI 卡片化重构（圆角阴影卡片容器）；深色/浅色双主题一键切换；GridSplitter 弹性三区布局；ExifTool 隐私清理（GPS/时间/相机/EXIF/XMP 选择性删除）
- **v1.2.0** — 批量队列引擎（ConcurrentQueue + 并发 1-128 + 失败重试）；元数据编辑器（~90 字段 9 大分类 + 双击编辑）；格式筛选窗口；预设系统 v1.0；CPU SIMD 指令集自动检测
- **v1.0.0** — 初始发布：JPEG/PNG/WebP/AVIF/JXL/TIFF 多格式编码；质量滑块；外部编码器集成（ffmpeg/cjxl/cjpegli）；命令行构建与预览

</details>

## 📄 License / 许可

This project is licensed under the **GNU General Public License v3.0 (GPL 3.0)**. See [../LICENSE](../LICENSE) for the full text, which also includes third-party license notices for all dependencies (Avalonia MIT, SkiaSharp MIT, FFmpeg LGPL/GPL, libjxl BSD 3-Clause, ExifTool GPL, etc.).

> This product includes DNG technology under license by Adobe.  — 本产品包含 Adobe 授权的 DNG 技术（dngtool 使用 Adobe DNG SDK）。

本项目采用 **GNU General Public License v3.0 (GPL 3.0)** 许可。完整文本（含全部依赖的第三方许可证声明）见 [../LICENSE](../LICENSE)。

> ⚠️ GPL 3.0 is a strong copyleft license. If you distribute modified versions of this software (including in binary form), you must also make the source code available under GPL 3.0.
>
> ⚠️ GPL 3.0 是强传染性许可证。若你分发本软件的修改版本（含二进制形式），你必须同时以 GPL 3.0 开源其源代码。
