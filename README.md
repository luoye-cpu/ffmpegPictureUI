# 🖼️ FFmpegPictureUI — FFmpeg 图片转换器

**v1.5.0 BETA2** — Cross-platform batch image/animation/video converter built on Avalonia UI.
基于 Avalonia UI 的跨平台批量图片/动图/视频转换工具，封装 `ffmpeg`/`ffprobe` + 外部编码器 (`cjxl`/`djxl`/`cjpegli`/`ultrahdr_app`/`JxrEncApp`/`JxrDecApp`).

QQ 交流群：754439779 | [点击加群](https://qm.qq.com/q/M2181PvCkW)

---

## ✨ Core Features / 核心功能

| Feature 功能 | Description 说明 |
|---|---|
| **Multi-format / 多格式** | JPEG, PNG, WebP, AVIF, JPEG XL, TIFF — plus animated: GIF, WebP (animated), APNG, AVIF (animated), JPEG XL (animated). JPEG LI 已整合为 JPEG 的 cjpegli 编码器选项 |
| **Encoder backend / 编码器后端** | Selectable ffmpeg / cjxl / cjpegli per format; cjxl for JXL lossless JPEG repack — 每种格式可选不同编码器后端 |
| **Quality control / 质量控制** | Quality slider (snap-to-tick) + format-aware numeric input — 滑块吸附整数 + 格式感知数字输入框 (JPEG q:v 2-31, JXL distance 0-15, etc.) |
| **Advanced codec options / 高级编码选项** | Per-format advanced panels: DCT algo, progressive mode, Huffman optimize, adaptive quant, sjpeg backend, PSNR target, lossless compression level, row-mt, still-picture, modular mode — 按格式独立高级面板 |
| **Color management / 色彩管理** | Color space, primaries, TRC (optional advanced mode); ICC profile embed/bake with external ICC file support — 色彩空间/基准/TRC，ICC 嵌入/烘焙（外部 ICC 文件） |
| **JXL Intelligence / JXL 智能** | Auto-detects JPEG-reconstruction vs native codestream; byte-level inspection (`JxlInspector`); picks optimal pipeline |
| **JPEG-LI / JPEG-LI** | `cjpegli` 作为 JPEG 格式的编码器后端选项，提供完整高级配置（色度子采样、渐进模式等）|
| **CPU SIMD / CPU 指令集** | Auto-detects AVX2/AVX/SSE4 capable binaries; runtime probe validates compatibility |
| **Batch queue / 批量队列** | Drag & drop; configurable concurrency (1–128); stop-after-queue |
| **Metadata editing / 元数据编辑** | ~90-field panel via exiftool; 9 categories (Basic, DateTime, Camera, Shooting, GPS, Image, IPTC, XMP, Color); double-click file opens editor — ~90字段9大分类exiftool编辑器，双击文件打开 |
| **Privacy cleaning / 隐私清理** | Strip GPS, timestamps, camera info, all EXIF, XMP |
| **Quality analysis / 质量分析** | SSIM + PSNR post-encode; auto-detects lossless |
| **Presets / 预设** | 24 built-in presets with secondary management window; save/load/import user presets — 24个内置预设+二级管理窗口，支持保存/加载/导入 |
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
| `dcraw` | ⚪ Optional / 可选 | Camera RAW (Bayer) → linear 16-bit TIFF demosaic |
| `exiftool` | ⚪ Optional / 可选 | Metadata editing, privacy cleaning, ICC profile embedding |

> **v1.5.0 BETA** — 外部工具面板重新设计为 3 列水平布局：
> - 📦 **JXL 参考库**（文件夹）— 自动检测 cjxl / djxl / cjpegli
> - 🏷 **exiftool**（文件）— 元数据编辑与 ICC 嵌入
> - 🔧 **artifacts**（文件夹）— 自动检测 ultrahdr_app / JxrEncApp / JxrDecApp / avifenc / dcraw
>
> 紧凑状态栏默认隐藏，后台检测完成后自动显示全部工具状态（✅/❌）。PLAN 便携包自动识别，支持手动指定路径。

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
│   │                     EncoderDetectionService, QualityAnalysisService,
│   │                     IccProfileService, PresetManagerService,
│   │                     PlatformServices, PlanFolderDetector
│   ├── Controls/         MetadataEditor
│   ├── MainWindow.xaml   Primary UI
│   ├── MainWindow.xaml.cs UI logic
│   ├── FormatFilterWindow.axaml  Format filter dialog
│   ├── PresetManagerWindow.axaml Preset manager window
│   ├── ProgressWindow.xaml Progress UI
├── tools/                Verification utilities
└── publish/              Publish output
```

---

## 📝 Changelog / 更新日志

### v1.5.0 BETA2 (2026-07-14)

- **Simple Mode / 简洁模式** — Minimal overlay view within the same window; drag-drop files directly into queue; auto-encode toggle starts processing automatically; preset switcher syncs to main UI parameters; dual-list layout (selected files + conversion queue). / 同窗口内极简覆盖视图；拖放文件直接入队；自动编码开关；预设切换同步主界面参数；双列表布局。
- **GPU Encoder Detection / GPU 编码检测** — Auto-detects QuickSync/NVENC/AMF hardware encoders at startup; runs quick encode validation test per encoder; shows color-coded warning hints (✅⚡⚠️❌) in encoder dropdown when hardware is unavailable. / 启动时自动检测 QSV/NVENC/AMF 硬件编码器；逐编码器快速验证；编码器下拉框彩色警告提示。
- **GPU UI Acceleration / GPU UI 加速** — ANGLE/D3D11 rendering backend for Windows (Vulkan/OpenGL for Linux); toggleable GPU/CPU button in top toolbar; `--no-gpu` CLI fallback. / Windows ANGLE/D3D11 渲染后端（Linux Vulkan/OpenGL）；GPU/CPU 切换按钮；`--no-gpu` 命令行兜底。
- **Startup Optimization / 启动优化** — Removed duplicate settings.json I/O; parallelized 7 external tool detections via Task.WhenAll; deferred GPU encoder runtime validation to background idle. / 消除双重 settings.json I/O；7 个外部工具并行检测；GPU 编码器运行时验证延迟到后台空闲。
- **Portable Storage / 便携化** — Settings & user presets now stored in exe directory (`presets/` subfolder); zero %AppData% dependency; full copy-paste deployment. / 配置和用户预设存储于 exe 同目录；零 %AppData% 依赖；拷贝即用。

### v1.5.0 BETA (2026-07-14)

- **ICC Color Management / ICC 色彩管理** — Load external .icc/.icm profiles; embed ICC metadata via exiftool (JPEG/PNG/TIFF/WebP) or iccgen filter (AVIF/JXL); bake pixels from source to target color space via zscale; bake+embed dual mode; built-in sRGB/Adobe RGB/Display P3/DCI-P3/ProPhoto/Rec.2020/Rec.2100 mapping. / 加载外部 .icc/.icm；exiftool/iccgen 嵌入 ICC 元数据；zscale 烘焙像素转换；烘焙+嵌入双模式；完整色彩空间映射表。
- **Preset System v2.0 / 预设系统 v2.0** — 24 built-in presets (JPEG LI×3, JXL×4, AVIF×10, WebP×3, PNG×2, TIFF, Ultra HDR, GIF); secondary PresetManagerWindow with list/detail/apply/save/import/delete; user presets CRUD persisted as JSON. / 24 内置预设（JPEG LI×3/JXL×4/AVIF×10/WebP×3/PNG×2/TIFF/Ultra HDR/GIF）；二级预设管理窗口；用户预设 JSON 持久化。
- **Tools Panel Redesign / 工具面板重构** — 3-column horizontal layout (JXL libs | exiftool | artifacts); compact status bar hidden by default, auto-shows after background detection; full 9-tool coverage with ✅/❌ indicators; PLAN portable pack auto-recognition. / 3 列水平布局（JXL库|exiftool|artifacts）；紧凑状态栏默认隐藏、后台检测完自动显示；全 9 工具 ✅/❌ 状态；PLAN 便携包自动识别。
- **Detection Module Rewrite / 检测模块重写** — Full async background pipeline with 3-phase execution (filesystem → ffmpeg → tools); real 8s timeout per step via Task.WhenAny; incremental Dispatcher logging; serialized ffmpeg calls to prevent dual-instance deadlock. / 全异步后台管线 3 阶段执行；每步真实 8s 超时；增量 Dispatcher 日志；ffmpeg 串行调用防死锁。
- **AVIF Encoder Panels / AVIF 编码器面板** — Per-encoder backend sub-panels: AOM (cpu-used/tune/still-picture/row-mt), SVT-AV1 (preset/tune), NVENC/QSV/AMF (hardware presets); dynamic panel switching on encoder selection. / 编码器子面板动态切换：AOM/SVT/NVENC/QSV/AMF 各自独立选项。
- **Animation & RAW / 动图与 RAW** — Video-to-animation duration limit; dcraw RAW demosaic with auto-detection; expanded RAW format support (Canon/Nikon/Sony/Fujifilm/Olympus/Panasonic/Pentax/Others). / 视频转动图时长限制；dcraw RAW 解码自动检测；扩展 RAW 格式支持。


<details>
<summary>Earlier versions / 更早版本</summary>

- **v1.4.5** — Windows Search drag-drop fix, Linux ARM migration analysis, JXL lossless JPEG repack / Win 搜索拖放修复、Linux ARM 迁移分析、JXL 无损重封装
- **v1.3.0** — UI cards redesign, dual theme, GridSplitter layout, exiftool privacy cleaning / UI 卡片重构、双色主题、GridSplitter 布局、exiftool 隐私清理
- **v1.2.0** — Batch queue, metadata editor, format filter, preset v1.0, CPU SIMD detection / 批量队列、元数据编辑器、格式筛选、预设 v1.0、CPU 指令集检测
- **v1.0.0** — Initial release: multi-format encoding, quality slider, external encoder integration / 初始发布：多格式编码、质量滑块、外部编码器集成

</details>

## 📄 License / 许可

This project is licensed under the **GNU General Public License v3.0 (GPL 3.0)**. See [../LICENSE](../LICENSE) for the full text, which also includes third-party license notices for all dependencies (Avalonia MIT, SkiaSharp MIT, FFmpeg LGPL/GPL, libjxl BSD 3-Clause, ExifTool GPL, etc.).

本项目采用 **GNU General Public License v3.0 (GPL 3.0)** 许可。完整文本（含全部依赖的第三方许可证声明）见 [../LICENSE](../LICENSE)。

> ⚠️ GPL 3.0 is a strong copyleft license. If you distribute modified versions of this software (including in binary form), you must also make the source code available under GPL 3.0.
>
> ⚠️ GPL 3.0 是强传染性许可证。若你分发本软件的修改版本（含二进制形式），你必须同时以 GPL 3.0 开源其源代码。
