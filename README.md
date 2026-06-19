# 🖼️ FFmpegPictureUI — FFmpeg 图片转换器

**v1.4.5 正式版 — Ultra HDR 编码器、JPEG XR 支持、管道编码、折叠工具栏、元数据安全模式。**

An Avalonia UI-based cross-platform batch image/animation/video conversion tool that wraps `ffmpeg`/`ffprobe` with an intuitive GUI. Integrates external encoders: `cjxl`/`djxl`/`cjpegli` (JPEG XL), `ultrahdr_app` (Ultra HDR), `JxrEncApp`/`JxrDecApp` (JPEG XR).

基于 Avalonia UI 的跨平台图片/动图/视频批量转换工具，将 `ffmpeg`/`ffprobe` 命令行封装为直观图形界面，集成外部编码器：`cjxl`/`djxl`/`cjpegli`（JPEG XL）、`ultrahdr_app`（Ultra HDR）、`JxrEncApp`/`JxrDecApp`（JPEG XR）。

QQ 交流群：754439779  点击链接加入群聊【FFmpegPictureUI图像处理软件】：https://qm.qq.com/q/M2181PvCkW****

---

## ✨ Core Features / 核心功能

| Feature 功能 | Description 说明 |
|---|---|
| **Multi-format / 多格式** | JPEG, PNG, WebP, AVIF, JPEG XL, TIFF — plus animated: GIF, WebP (animated), APNG, AVIF (animated), JPEG XL (animated). JPEG LI 已整合为 JPEG 的 cjpegli 编码器选项 |
| **Encoder backend / 编码器后端** | Selectable ffmpeg / cjxl / cjpegli per format; cjxl for JXL lossless JPEG repack — 每种格式可选不同编码器后端 |
| **Quality control / 质量控制** | Quality slider (snap-to-tick) + format-aware numeric input — 滑块吸附整数 + 格式感知数字输入框 (JPEG q:v 2-31, JXL distance 0-15, etc.) |
| **Advanced codec options / 高级编码选项** | Per-format advanced panels: DCT algo, progressive mode, Huffman optimize, adaptive quant, sjpeg backend, PSNR target, lossless compression level, row-mt, still-picture, modular mode — 按格式独立高级面板 |
| **Color management / 色彩管理** | Color space, primaries, TRC (optional advanced mode) |
| **JXL Intelligence / JXL 智能** | Auto-detects JPEG-reconstruction vs native codestream; byte-level inspection (`JxlInspector`); picks optimal pipeline |
| **JPEG-LI / JPEG-LI** | `cjpegli` 作为 JPEG 格式的编码器后端选项，提供完整高级配置（色度子采样、渐进模式等）|
| **CPU SIMD / CPU 指令集** | Auto-detects AVX2/AVX/SSE4 capable binaries; runtime probe validates compatibility |
| **Batch queue / 批量队列** | Drag & drop; configurable concurrency (1–128); stop-after-queue |
| **Metadata editing / 元数据编辑** | ~90-field panel via exiftool; 9 categories (Basic, DateTime, Camera, Shooting, GPS, Image, IPTC, XMP, Color); double-click file opens editor — ~90字段9大分类exiftool编辑器，双击文件打开 |
| **Privacy cleaning / 隐私清理** | Strip GPS, timestamps, camera info, all EXIF, XMP |
| **Quality analysis / 质量分析** | SSIM + PSNR post-encode; auto-detects lossless |
| **Presets / 预设** | Save/load conversion presets; export/import JSON |
| **Dual theme / 双色主题** | Dark/Light mode; queue text adapts — 队列文字颜色自适应主题 |
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
| `ultrahdr_app` | ⭐ Recommended / 推荐 | Gain Map / Ultra HDR JPEG encoding (Google reference) |
| `JxrEncApp` / `JxrDecApp` | ⭐ Recommended / 推荐 | JPEG XR encoding/decoding (Microsoft jxrlib) |
| `avifenc` | ⚪ Optional / 可选 | GIF → AVIF two-step encoding with alpha preservation |
| `exiftool` | ⚪ Optional / 可选 | Metadata editing, privacy cleaning |

> The **JPEG XL 参考实现库** setting (one directory containing `cjxl.exe`/`djxl.exe`/`cjpegli.exe`) is saved as `CjxlPath`. The app auto-selects the best SIMD-optimized binary for your CPU.
>
> 设置中 **JPEG XL 参考实现库** 字段保存包含 `cjxl.exe`/`djxl.exe`/`cjpegli.exe` 的目录路径，应用自动选择与 CPU 指令集匹配的最优二进制。

---

## 🚀 Quick Start / 快速开始

### Prerequisites / 前提条件

- **OS / 系统**: Windows 10/11 (其他 .NET 10 平台应可运行)
- **.NET 10 Runtime**: [Download / 下载](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- **FFmpeg**: Install and ensure `ffmpeg -version` works / 安装并确认终端可运行 `ffmpeg -version`

```bash
# Clone and build / 克隆并构建
git clone https://github.com/luoye-cpu/PLAN-1.git
cd PLAN-1/ffmpegPictureUI
dotnet build src/FfmpegGui/FfmpegGui.csproj -c Release
dotnet run --project src/FfmpegGui/FfmpegGui.csproj
```

Or download from [Releases / 发布页](https://github.com/luoye-cpu/PLAN-1/releases).

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
│   │                     JxlInspector, JxlPipelineService,
│   │                     ExternalToolsDetector, CpuFeatureService,
│   │                     ExifToolService, FormatCapabilitiesService,
│   │                     EncoderDetectionService, QualityAnalysisService
│   ├── Controls/         MetadataEditor
│   ├── MainWindow.xaml   Primary UI
│   ├── MainWindow.xaml.cs UI logic
│   ├── FormatFilterWindow.axaml  Format filter dialog
│   ├── FormatFilterWindow.axaml.cs
│   ├── ProgressWindow.xaml Progress UI
├── tools/                Verification utilities
└── publish/              Publish output
```

---

## 📝 Changelog / 更新日志

### v1.4.5 (2026-06-20)

> 🎯 **正式版** — Ultra HDR 独立编码器、JPEG XR 全面支持、管道编码消除临时文件、折叠工具栏、元数据色彩保护。

- 🗺️ **Ultra HDR 独立编码器 / Ultra HDR encoder**: 集成 Google `ultrahdr_app.exe` 作为 JPEG 独立编码器后端（场景0：单一HDR RAW → 自动SDR基底+增益图）；Gain Map 面板在 ultrahdr 后端下始终可见；新增 `UltrahdrPath` 配置项
- 🖼️ **JPEG XR 全面支持 / JPEG XR support**: 新增 JXR 输出格式；集成 Microsoft jxrlib `JxrEncApp.exe`/`JxrDecApp.exe`；支持 32 种像素格式、无损/有损、Alpha 通道、色度子采样；JXR 质量分析自动用 JxrDecApp 解码后对比
- 🔧 **管道编码消除临时文件 / Pipe encoding**: TIFF→JXL/TIFF→JPEG 等原先通过 PNG 临时文件中转的流程全部替换为 `ffmpeg stdout → encoder stdin` 直通管道（`PipeFfmpegToExternalEncoderAsync`），零磁盘中间文件
- 📂 **折叠工具栏 / Collapsible toolbar**: 顶部外部工具路径面板改为可折叠设计；折叠态显示紧凑状态指示器（✅/❌ 各工具检测状态）；展开后显示完整路径配置；新增 JxrEncApp 路径行
- 🎨 **元数据色彩保护 / Metadata color safety**: 新增 `CopyMetadataSafeAsync` 安全模式，exiftool 仅复制 EXIF/IPTC/XMP 描述性标签，跳过 ICC_Profile/ColorSpace/ColorPrimaries/TransferFunction，保护编码器内嵌色彩元数据；Ultra HDR/CJXL/CJPEGLI/FFmpeg 路径默认启用
- 🔄 **元数据恢复回退 / Metadata fallback**: exiftool 不可用时自动回退到 ffmpeg 重新封装恢复元数据（`RestoreMetadataViaFfmpegAsync`）
- 🐛 **WebP 无损失效修复 / WebP lossless fix**: 无损模式显式 `-q:v 100` + `-compression_level` 范围防护 + 禁止有损预设（picture/photo）+ 强制 RGBA 像素格式防 YUV 截断退化
- 🐛 **元数据增强 / Enhanced metadata**: 统一所有格式编码后调用元数据恢复；`-map_metadata:s:v` 流级映射；`PreConvertToPngAsync` 传递 `-map_metadata 0`
- 📊 **检测优先级统一 / Detection priority**: 所有外部工具检测统一为：手动路径 > 同目录 > 系统 PATH

> 🎯 **正式版** — Avalonia 12 现代化 UI、视频转动图、AVIF 编码器面板分离、进程优先级控制。

- 🎨 **Avalonia 12 现代化 UI / Modern UI**: 升级至 Avalonia 12.0.4，全局圆角卡片设计（按钮/输入框 6px、面板 10px），阴影效果，自定义强调色 —— Upgraded to Avalonia 12 with rounded card-style UI, shadows, custom accent color
- 🎬 **视频转动图支持 / Video to animation**: 动图模式可输入 .mp4/.mov/.mkv/.avi/.webm/.wmv/.flv 视频文件；新增时长限制参数 (-t)；格式筛选窗口支持视频格式 —— Video input in animation mode with duration limit
- 🔧 **AVIF 编码器面板分离 / AVIF encoder panels**: libaom-av1 / libsvtav1 / 硬件编码器各自独立高级面板；新增 SVT preset 0-13 控件；硬件编码质量预设（快/平衡/高质量）；tune=IQ 支持 (-aom-params tune=iq) —— Separate advanced panels per encoder with IQ tune for libaom
- 📏 **JPEG-LI butteraugli distance / JPEG-LI distance**: 质量参数统一为 butteraugli distance (0-15)，与 JPEG XL 一致 —— Quality unified to butteraugli distance, same as JXL
- ⚙ **进程优先级 / Process priority**: 底部新增 Windows 进程优先级下拉（实时/高/高于正常/正常/低于正常/低），实时生效 —— Windows process priority control with 6 levels
- 🔍 **格式筛选视频支持 / Format filter video**: 格式筛选窗口底部追加"🎬 视频格式（动图模式）"分隔区，7 种视频格式可独立勾选 —— Video format filter section at bottom
- 🐛 **JXL 质量分析修复 / JXL quality analysis fix**: JXL 源文件 SSIM/PSNR 分析自动用 djxl 解码为临时 PNG 后对比 —— auto djxl decode for JXL quality analysis
- 🐛 **进度窗口状态修复 / Progress window fix**: 已完成任务不再显示残留的解码进度 —— completed tasks show final status instead of stale progress

### v1.4.3 (2026-06-08)

> 🎯 **正式版** — JXL 管道死锁修复、Gain Map 支持、JPEG LI 整合、实时进度与命令更新。

- 🔧 **JXL 管道死锁修复 / JXL pipeline deadlock fix**: 三处管道代码重构（`PipeDjxlToFfmpegAsync`、`JxlPipelineService`），消除 CopyToAsync/WaitForExit 循环等待；新增进程强制清理 finally 块 —— three pipeline methods rewritten to eliminate deadlock
- 🗺️ **Gain Map (Ultra HDR) 支持 / Gain Map support**: JPEG 格式新增 Gain Map 面板（增益图质量、目标亮度）；自动检测 libultrahdr 编码器；不可用时优雅隐藏 —— auto-detect libultrahdr, graceful degradation
- 🔀 **JPEG LI 整合 / JPEG LI consolidation**: 移除独立 "JPEG LI" 格式选项，cjpegli 作为 JPEG 编码器后端使用，选中后显示完整 JPEG LI 高级选项 —— cjpegli now a JPEG encoder option
- 🔒 **APNG 无损锁定 / APNG lossless lock**: 动图模式 APNG 质量强制 100% 且滑块禁用 —— APNG quality locked at max in animation mode
- 📡 **详情窗口实时更新 / Detail window live update**: 双击队列项打开的窗口实时更新当前执行命令和进度，覆盖 ffmpeg/cjxl/cjpegli/djxl/管道全部后端 —— live command + phase-aware progress for all backends
- 🧹 **UI 清理 / UI cleanup**: 移除 JXL 青色无损检测提示框，检测信息整合到执行日志；日志消息全面细化（工具可用性、输入类型、编码参数）—— removed cyan JXL hint box, refined log messages
- 🐛 **其他修复 / Other fixes**: cjpegli 管道移除不兼容的 `--num_threads` 参数；JXL 输入 PNG 中转逻辑精确化（仅外部编码器需要时触发）
- 🐛 **其他修复 / Other fixes**: 加入HEIC输入支持，加入DNG输入支持

### v1.4.2 (2026-06-06)

> 🎯 **正式版** — 动图质量分析与编码选项全面修复。

- 🔬 **SSIM/PSNR 动图质量分析修复 / Animated quality analysis fix**: 三处关键修复彻底解决动图质量测试分数异常低的问题 —— Three critical fixes for animated SSIM/PSNR:
  - Regex 取最后一帧汇总平均值（而非第一帧偏分）—— `Regex.Matches[^1]` instead of `Regex.Match`
  - `setpts=N` 按帧序号对齐（而非 `PTS-STARTPTS` 保留帧间隔导致不同帧率错位对比）—— frame-index alignment instead of time-based
  - `settb=1/1000,split` 标准化时间基准 + 独立帧拷贝避免 PSNR 全 `inf` —— timebase normalization + split for independent filter chains
  - 多轨 AVIF 自动选择帧数最多的动画轨（跳过高 fps 封面轨）—— auto-select best video stream via ffprobe
- 🎛️ **动图高级编码选项修复 / Animated codec options fix**: 
  - JXL 动图模式隐藏 cjxl 后端选项（渐进式解码、光子噪声不适用于动画）—— hide cjxl options for animated JXL
  - AVIF `-tune` 值越界修复：UI 0-5 → libaom -1/0/1 正确映射 —— tune value mapping for libaom range
  - APNG / WebP / AVIF 动图全高级选项编码验证通过 —— all animated advanced options verified
- 🐛 **其他修复 / Other fixes**: `WebpLosslessPanel` 动图模式可见性恢复；编码器列表动图 JXL 过滤 cjxl

### v1.4.1 BETA (2026-06-05)

> ⚠️ **测试版本** — 动图编码修复版。

- 🎞 **AVIF → GIF 透明+色彩修复 / AVIF→GIF alpha+color fix**: 颜色流和 alpha 流分离解码后 alphamerge 合并，修复单 pass 色彩压缩问题 —— dual-stream extraction avoids color loss
- 🎞 **AVIF → WebP 透明通道 / AVIF→WebP alpha**: 同样分轨提取+合并方案保留完整透明通道 —— same dual-stream approach for animated WebP
- 🧹 **移除 avifenc 集成 / avifenc integration removed**: 编码器后端选项已完全移除；GIF→AVIF 两步法保留为独立工具路径 —— encoder backend removed, two-step path preserved
- 🎨 **动图编码器面板重设计 / Animated codec panel redesign**: WebP/AVIF/JXL 动图模式面板可见性细化 —— per-format animated panel visibility refined
- 🐛 **JXL 静态图编码修复 / JXL static encoding fix**: 动图判断条件修正；palettegen 语法修复；拖放目录结构修复 —— JXL detection, palettegen syntax, drag-drop fixes
- 🔄 **Metadata 保留扩展 / Metadata preservation**: 外部工具路径新增 8 个 exiftool 调用点

### v1.4.0 BETA (2026-06-04)

> ⚠️ **测试版本** — 包含大量实验性动图编码功能。

- 🎞 **Animation mode / 动图模式**: 静态/动图切换，格式列表动态变化，FPS/循环/缩放参数（留空=auto）—— Still/Animated toggle with auto options
- 🎬 **5 animated formats / 5种动图格式**: GIF, WebP animated, PNG (APNG), AVIF animated, JPEG XL animated —— full animated encoding support
- 🎚 **Animation parameter panel / 动图参数面板**: FPS (1-60), loop count, scale width with auto option
- 🔍 **Advanced animated codec panels / 动图高级面板**: 各动图格式专属编码选项 —— per-format animated codec options
- 👁 **Error-only queue filter / 仅显示报错**: 一键过滤已完成项，聚焦报错任务
- 📝 **Metadata editor expansion / 元数据编辑器扩展**: 39→~90 字段，9 大分类，双击文件打开
- 🐛 **exiftool stalling / search drag-drop / JSON type fixes**

### v1.3.4 (2026-06-04)

- 🎚️ Format-aware quality input with snap-to-tick slider; PNG/TIFF lossless lock; dark mode queue text fix; cjpegli `--distance`; image format filter; deduplicated filter arrays; metadata editor expanded to ~90 fields

### v1.3.3 (2026-06-04)

- 🧩 Unified encoder backend (cjpegli/cjxl/ffmpeg); advanced codec panels per format; thread locking; smart fallback via PNG intermediate; SkiaSharp CVE fix; queue progress ETA

### v1.3.2 (2026-06-03)

- 🔬 JXL smart pipeline (byte-level detection, djxl→cjpegli pipe); JPEG-LI format; SIMD detection; unified lib path

### v1.3.1 (2026-06-02)

- 📝 Metadata editing panel (39 fields, 5 categories); queue error red highlight

### v1.3.0 (2026-05-26)

- 📦 Single-file publish; metadata mode dropdown; exiftool privacy cleaning; bit depth auto; 3-tier tool detection; preserve folder structure; full preset coverage

### v1.2.3 (2026-05-26)

- 🎨 UI overhaul (cards, GridSplitter); dual theme; drag/queue fixes; cjxl lossless JPEG

### v1.2.2 · v1.2.1 · v1.1.0 · v1.0.1 · v1.0.0

See git history for details / 详见 git 提交记录。

## 📄 License / 许可

This project is licensed under the **GNU General Public License v3.0 (GPL 3.0)**. See [../LICENSE](../LICENSE) for the full text, which also includes third-party license notices for all dependencies (Avalonia MIT, SkiaSharp MIT, FFmpeg LGPL/GPL, libjxl BSD 3-Clause, ExifTool GPL, etc.).

本项目采用 **GNU General Public License v3.0 (GPL 3.0)** 许可。完整文本（含全部依赖的第三方许可证声明）见 [../LICENSE](../LICENSE)。

> ⚠️ GPL 3.0 is a strong copyleft license. If you distribute modified versions of this software (including in binary form), you must also make the source code available under GPL 3.0.
>
> ⚠️ GPL 3.0 是强传染性许可证。若你分发本软件的修改版本（含二进制形式），你必须同时以 GPL 3.0 开源其源代码。
