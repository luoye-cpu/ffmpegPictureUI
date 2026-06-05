# 🖼️ FfmpegPictureUI — FFmpeg 图片转换器

**⚠️ v1.4.1 BETA — 测试版本，包含实验性动图编码功能，可能有未发现的 Bug。**

An Avalonia UI-based cross-platform batch image/animation conversion tool that wraps `ffmpeg`/`ffprobe` with an intuitive GUI. Integrates `cjxl`/`djxl`/`cjpegli` from the [JPEG XL reference implementation](https://github.com/libjxl/libjxl).

基于 Avalonia UI 的跨平台图片/动图批量转换工具，将 `ffmpeg`/`ffprobe` 命令行封装为直观图形界面，并集成 [JPEG XL 参考实现](https://github.com/libjxl/libjxl) 的 `cjxl`/`djxl`/`cjpegli`。

---

## ✨ Core Features / 核心功能

| Feature 功能 | Description 说明 |
|---|---|
| **Multi-format / 多格式** | JPEG, JPEG LI, PNG, WebP, AVIF, JPEG XL, TIFF — plus animated: GIF, WebP (animated), APNG, AVIF (animated), JPEG XL (animated) |
| **Encoder backend / 编码器后端** | Selectable ffmpeg / cjxl / cjpegli per format; cjxl for JXL lossless JPEG repack — 每种格式可选不同编码器后端 |
| **Quality control / 质量控制** | Quality slider (snap-to-tick) + format-aware numeric input — 滑块吸附整数 + 格式感知数字输入框 (JPEG q:v 2-31, JXL distance 0-15, etc.) |
| **Advanced codec options / 高级编码选项** | Per-format advanced panels: DCT algo, progressive mode, Huffman optimize, adaptive quant, sjpeg backend, PSNR target, lossless compression level, row-mt, still-picture, modular mode — 按格式独立高级面板 |
| **Color management / 色彩管理** | Color space, primaries, TRC (optional advanced mode) |
| **JXL Intelligence / JXL 智能** | Auto-detects JPEG-reconstruction vs native codestream; byte-level inspection (`JxlInspector`); picks optimal pipeline |
| **JPEG-LI / JPEG-LI** | `cjpegli` encoding with full options; falls back to ffmpeg mjpeg when unavailable |
| **CPU SIMD / CPU 指令集** | Auto-detects AVX2/AVX/SSE4 capable binaries; runtime probe validates compatibility |
| **Batch queue / 批量队列** | Drag & drop; configurable concurrency (1–128); stop-after-queue |
| **Metadata editing / 元数据编辑** | ~90-field panel via exiftool; 9 categories (Basic, DateTime, Camera, Shooting, GPS, Image, IPTC, XMP, Color); double-click file opens editor — ~90字段9大分类exiftool编辑器，双击文件打开 |
| **Privacy cleaning / 隐私清理** | Strip GPS, timestamps, camera info, all EXIF, XMP |
| **Quality analysis / 质量分析** | SSIM + PSNR post-encode; auto-detects lossless |
| **Presets / 预设** | Save/load conversion presets; export/import JSON |
| **Dual theme / 双色主题** | Dark/Light mode; queue text adapts — 队列文字颜色自适应主题 |
| **Format filter / 格式筛选** | Checkbox window to enable/disable recognized image formats; persists to settings — 勾选启用的图片格式，持久化保存 |
| **Animation mode / 动图模式** | Mode toggle (Still/Animated); FPS/loop/scale controls (auto or manual); per-format advanced animated panels — 模式切换，帧率/循环/缩放参数(auto可用) |
| **Lossless lock / 无损锁定** | PNG/TIFF auto-lock quality at max, disable slider — 无损格式自动锁定最高质量 |
| **Error-only filter / 仅显示报错** | Toggle to hide completed items, show only errors + in-progress — 一键屏蔽正常完成项，聚焦报错任务 |
| **Search drag-drop / 搜索拖放** | Windows Search result files correctly resolved via Shell namespace paths — Windows 搜索结果拖放正确解析 |

---

## 🔧 External Tools / 外部工具依赖

| Tool 工具 | Status 状态 | Role 用途 |
|---|---|---|
| `ffmpeg` + `ffprobe` | ✅ Required / 必需 | Core encoding/decoding, media probing |
| `cjxl` / `djxl` / `cjpegli` | ⭐ Recommended / 推荐 | JXL transcode, decoding, JPEG-LI encoding |
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

### v1.4.0 BETA (2026-06-04)

> ⚠️ **测试版本** — 包含大量实验性动图编码功能，欢迎反馈 Bug。

- 🎞 **Animation mode / 动图模式**: Still/Animated mode toggle; dynamic format list per mode; FPS, loop count, scale width controls with auto (empty) option — 静态/动图模式切换，格式列表动态变化，帧率/循环/缩放参数均支持留空auto
- 🎬 **5 animated format support / 5种动图格式**: GIF (palette optimization + dither), WebP animated, PNG (APNG) via `apng` encoder + `-f apng`, AVIF animated via `-still-picture 0`, JPEG XL animated via `cjxl` external tool — 全面支持5种动图格式编码
- 🎚 **Animation parameter panel / 动图参数面板**: FPS (1-60), loop count (0=infinite/-1=no loop), scale width; empty = auto (follow source) — 动图专属参数卡片式面板
- 🔍 **Advanced animated codec panels / 动图高级面板**: GIF palettegen+dither toggles, APNG info panel, WebP/AVIF/JXL existing panels adapted for animation — 各动图格式的高级编码选项面板
- 👁 **Error-only queue filter / 仅显示报错**: Checkbox to hide completed items, showing only errors + in-progress tasks; auto-refreshes on status change — 一键过滤已完成项，聚焦报错任务
- 📝 **Metadata editor expansion / 元数据编辑器扩展**: 39 → ~90 fields across 9 categories; double-click file opens editor with ffprobe media info — 字段数量翻倍，新增IPTC/XMP/色彩配置等分类
- 🐛 **exiftool stalling fix / exiftool 挂起修复**: Process deadlock fixed (async stream reads); `exiftool(-k).exe` automatically cloned to `exiftool.exe` to avoid keypress wait — 进程死锁修复，自动处理(-k)版本
- 🐛 **Search-result drag-drop / 搜索拖放修复**: Windows Search result files resolved via dual-format fallback (`DataFormats.Files` → `FileNames`); Shell namespace paths properly handled — 双格式回退机制解决搜索结果拖放失效
- 🐛 **JSON value type fix / 元数据读取修复**: `JsonElement.GetString()` replaced with `JsonElementToString()` to handle numeric/boolean/null types in exiftool output — 修复数字/布尔/空类型导致的元数据读取崩溃

### v1.3.4 (2026-06-04)

- 🎚️ **Format-aware quality input / 格式感知质量输入**: NumericUpDown replaced with TextBox showing actual codec values (JPEG q:v 2–31, JXL distance 0–15, AVIF CRF 0–63, etc.) — 数字输入框显示各格式实际编码参数值，滑块和输入框双向正反映射
- 🎯 **Snap-to-tick slider / 吸附式滑块**: `TickFrequency="1" IsSnapToTickEnabled="True"` ensures every integer value is selectable — 滑块吸附到整数，确保所有质量值可选
- 🔒 **PNG/TIFF quality lock / 无损格式锁定**: Quality slider+input auto-lock at max value and disabled for lossless-only formats — PNG/TIFF 自动锁定最高质量并禁用
- 🌓 **Dark mode queue text fix / 深色模式队列文字修复**: Queue items correctly switch to white text in dark mode via theme-aware `ErrorToColorConverter` + binding refresh — 队列文字在深色模式下正确切换为白色
- 🏷️ **JPEGli -d parameter / JPEGli 距离参数**: cjpegli uses `--distance` (butteraugli) instead of `--quality` for perceptually uniform control — cjpegli 改用感知均匀的 butteraugli 距离参数
- 🔍 **Image format filter / 图片格式筛选**: Checkbox window to toggle which file extensions are recognized; persisted to settings; cleans up non-matching files — 勾选窗口选择可识别的图片格式，持久化保存，自动清理不匹配文件
- 🧹 **Deduplicated filter arrays / 去重硬编码**: All 6 hardcoded format extension arrays unified into `AppSettings.GetEnabledExtensions()` — 全部 6 处硬编码格式数组统一集中管理
- 📝 **Enhanced metadata editor / 元数据编辑器增强**: Expanded from 39 to ~90 fields across 9 categories (Basic, DateTime, Camera, Shooting, GPS, Image, IPTC, XMP, Color); double-click selected files opens metadata editor with ffmpeg media info — 从 39 字段扩展到 ~90 字段 9 大分类，双击已选文件打开含 ffmpeg 媒体信息的元数据编辑器
- 🐛 **Search-result drag-drop fix / 搜索拖放修复**: Files dragged from Windows Search results now correctly resolve Shell namespace paths via `TryGetLocalPath()` — 从 Windows 搜索结果拖放的文件通过 TryGetLocalPath 正确解析 Shell 路径

### v1.3.3 (2026-06-04)

- 🧩 **Unified Encoder Backend / 编码器统一后端**: `cjpegli` and `cjxl` are now selectable encoder options in the encoder dropdown; auto-fallback to ffmpeg when external tool unavailable — cjpegli 和 cjxl 作为编码器下拉框中的独立可选选项，不可用时自动回退到 ffmpeg
- 🎛️ **Advanced Codec Panels / 高级编码面板**: Full advanced options per format, auto-switches by backend — 每个格式的完整高级编码选项，按编码器后端自动切换:
  - **JPEG LI (cjpegli)**: chroma subsampling (auto/444/422/420/440), progressive mode, Huffman optimize, adaptive quantization, sjpeg backend + PSNR target
  - **JPEG XL (cjxl)**: effort 1–9, progressive decode, photon noise ISO 0–3200; lossless JPEG repack auto-detect
  - **JPEG XL (ffmpeg libjxl)**: effort 1–9, modular mode, lossless_jpeg hint
  - **JPEG (ffmpeg mjpeg)**: Huffman strategy, DCT algorithm (auto/int/fastint/float)
  - **WebP**: preset + lossless compression level (0–6)
  - **AVIF**: cpu-used, tune, usage/preset, still-picture (default ON), row-mt
  - **TIFF**: compression algo (raw/lzw/deflate/packbits)
- 🔒 **Thread locking / 线程锁定**: Auto-locks single-thread for encoders that don't support multi-threading (cjpegli) — 对不支持多线程的编码器自动锁定单线程
- 🔤 **Format display names / 格式大写名称**: All format names capitalized (`JPEG`, `JPEG LI`, `JPEG XL`, `PNG`, `WebP`, `AVIF`, `TIFF`) — 格式名统一大写
- 🔄 **JPEG LI independent / JPEG LI 独立**: `JPEG LI` is a separate format from `JPEG`; outputs standard `.jpg` files — JPEG LI 独立为单独格式，输出标准 .jpg 后缀
- 📐 **Visually-lossless defaults / 视觉无损默认**: AVIF quality default 90, still-picture default checked — AVIF 默认质量 90 并开启静态图片模式
- 🛡️ **SkiaSharp vulnerability fix / 漏洞修复**: SkiaSharp 2.88.3 → 2.88.6 (CVE-2023-4863 / GHSA-j7hp-h8jx-5ppr); no .pdb in release
- ⏱️ **Queue progress display / 队列进度显示**: Real-time elapsed time + N/M completed + ETA based on average task duration — 实时已用时间 + N/M 完成数 + 基于平均耗时的剩余预估
- 🔁 **Smart fallback / 智能回退**: cjxl/cjpegli auto-retry via ffmpeg→PNG intermediate when direct encoding fails (e.g. AVIF/WebP input) — cjxl/cjpegli 直接编码失败自动通过 ffmpeg 转 PNG 重试
- 🎨 **Theme-aware labels / 主题感知标签**: All `Gray` foreground labels replaced with `DynamicResource` for readability in dark mode — 全部灰色标签改为动态资源，深色模式下清晰可读
- 🐛 **Fixes / 修复**: Auto-option fallback when advanced codec panel unchecked; all format lookups via `NormalizeFormat()`; jpegli output extension corrected to `.jpg` — 高级面板不勾选时正确回退；格式名统一映射；jpegli 输出后缀修正

### v1.3.2 (2026-06-03)

- 🔬 **JXL Smart Pipeline / JXL 智能管线**: Byte-level JXL type detection (`JxlInspector`) distinguishes JPEG-reconstruction from native codestream; auto-selects optimal path — 字节级 JXL 类型检测，自动区分 JPEG 套壳 vs 原生码流并选最优路径
- 🔧 **Stream pipeline / 流式管道**: `djxl` → `cjpegli` process-to-process pipe with zero intermediate file I/O — 进程间管道，无需磁盘中间文件
- 🆕 **JPEG-LI format / JPEG-LI 格式**: New `jpegli` output option; prefers `cjpegli` encoding, falls back to ffmpeg mjpeg — 新增 jpegli 输出选项，优先 cjpegli 编码
- 🧠 **SIMD detection / SIMD 检测**: Startup probe of ffmpeg/cjxl/djxl/cjpegli SIMD capabilities; auto-selects optimal binary — 启动时探测全部工具的 SIMD 编译能力
- 🔍 **Unified lib path / 统一库路径**: `CjxlPath` → "JPEG XL 参考实现库", single directory manages cjxl/djxl/cjpegli — 一个目录管理全部工具
- 🐛 **Fix / 修复**: cjpegli detection when CjxlPath points to a file; `FfmpegCommandBuilder` `jpegli` case — cjpegli 检测增强；命令构建补全
- 🧹 **Cleanup / 清理**: Removed unused candidate panel and probe details UI — 移除未使用 UI 元素

### v1.3.1 (2026-06-02)

- 📝 Metadata editing panel (39 fields, 5 categories, exiftool) / 元数据编辑面板
- 🐛 Queue error items red highlight; null Converter text fix / 队列报错标红修复

### v1.3.0 (2026-05-26)

- 📦 Single-file publish; 🧹 Metadata mode dropdown; 🔒 exiftool privacy cleaning; 🎨 Bit depth auto; 🔍 3-tier tool detection; 🗂️ Preserve folder structure; 💾 Full preset coverage

### v1.2.3 (2026-05-26)

- 🎨 UI overhaul (cards, GridSplitter, WrapPanel); 🌓 Dual theme; 🐛 Drag/queue/refresh fixes; 🔧 cjxl `--lossless_jpeg=1`

### v1.2.2 · v1.2.1 · v1.1.0 · v1.0.1 · v1.0.0

See git history for details / 详见 git 提交记录。

## 📄 License / 许可

This project is licensed under the **GNU General Public License v3.0 (GPL 3.0)**. See [../LICENSE](../LICENSE) for the full text, which also includes third-party license notices for all dependencies (Avalonia MIT, SkiaSharp MIT, FFmpeg LGPL/GPL, libjxl BSD 3-Clause, ExifTool GPL, etc.).

本项目采用 **GNU General Public License v3.0 (GPL 3.0)** 许可。完整文本（含全部依赖的第三方许可证声明）见 [../LICENSE](../LICENSE)。

> ⚠️ GPL 3.0 is a strong copyleft license. If you distribute modified versions of this software (including in binary form), you must also make the source code available under GPL 3.0.
>
> ⚠️ GPL 3.0 是强传染性许可证。若你分发本软件的修改版本（含二进制形式），你必须同时以 GPL 3.0 开源其源代码。
