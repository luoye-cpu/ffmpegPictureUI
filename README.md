# 🖼️ FfmpegPictureUI — FFmpeg 图片转换器

An Avalonia UI-based cross-platform batch image conversion tool that wraps `ffmpeg`/`ffprobe` with an intuitive GUI. Integrates `cjxl`/`djxl`/`cjpegli` from the [JPEG XL reference implementation](https://github.com/libjxl/libjxl) for optimal JXL ↔ JPEG workflows.

基于 Avalonia UI 的跨平台图片批量转换工具，将 `ffmpeg`/`ffprobe` 命令行封装为直观图形界面，并集成 [JPEG XL 参考实现](https://github.com/libjxl/libjxl) 的 `cjxl`/`djxl`/`cjpegli` 以实现最优 JXL ↔ JPEG 转换。

---

## ✨ Core Features / 核心功能

| Feature 功能 | Description 说明 |
|---|---|
| **Multi-format / 多格式** | JPEG, JPEG LI, PNG, WebP, AVIF, JPEG XL, TIFF |
| **Encoder backend / 编码器后端** | Selectable ffmpeg / cjxl / cjpegli per format; cjxl for JXL lossless JPEG repack — 每种格式可选不同编码器后端 |
| **Quality control / 质量控制** | Quality slider, chroma subsampling (auto/4:4:4/4:2:2/4:2:0/4:4:0), bit depth |
| **Advanced codec options / 高级编码选项** | Per-format advanced panels: DCT algo, progressive mode, Huffman optimize, adaptive quant, sjpeg backend, PSNR target, lossless compression level, row-mt, still-picture, modular mode — 按格式独立高级面板 |
| **Color management / 色彩管理** | Color space, primaries, TRC (optional advanced mode) |
| **JXL Intelligence / JXL 智能** | Auto-detects JPEG-reconstruction vs native codestream; byte-level inspection (`JxlInspector`); picks optimal pipeline |
| **JPEG-LI / JPEG-LI** | `cjpegli` encoding with full options; falls back to ffmpeg mjpeg when unavailable |
| **CPU SIMD / CPU 指令集** | Auto-detects AVX2/AVX/SSE4 capable binaries; runtime probe validates compatibility |
| **Batch queue / 批量队列** | Drag & drop; configurable concurrency (1–128); stop-after-queue |
| **Metadata editing / 元数据编辑** | 39-field panel via exiftool; 5 categories (read/modify/restore/clear) |
| **Privacy cleaning / 隐私清理** | Strip GPS, timestamps, camera info, all EXIF, XMP |
| **Quality analysis / 质量分析** | SSIM + PSNR post-encode; auto-detects lossless |
| **Presets / 预设** | Save/load conversion presets; export/import JSON |
| **Dual theme / 双色主题** | Dark/Light mode; auto-persists |

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
│   └── MainWindow.xaml.cs UI logic
├── tools/                Verification utilities
└── publish/              Publish output
```

---

## 📝 Changelog / 更新日志

### v1.3.3 (2026-06-03)

- 🧩 **Unified Encoder Backend / 编码器统一后端**: `cjpegli` and `cjxl` are now selectable encoder options in the encoder dropdown — cjpegli 和 cjxl 作为编码器下拉框中的独立可选选项
- 🎛️ **Advanced Codec Panels / 高级编码面板**: Full advanced options per format — 每个格式的完整高级编码选项:
  - **JPEG**: DCT algorithm (`auto`/`int`/`fastint`/`float`)
  - **JPEG LI**: chroma subsampling, progressive mode, Huffman optimize, adaptive quantization, sjpeg backend, PSNR target
  - **WebP**: lossless compression level (0–6)
  - **AVIF**: row-level multithreading (`row-mt`), still-picture mode default enabled
  - **JPEG XL**: effort, modular mode (cjxl native), lossless-jpeg hint
- 🔒 **Thread locking / 线程锁定**: Auto-locks single-thread for encoders that don't support multi-threading (cjpegli) — 对不支持多线程的编码器自动锁定单线程
- 🔤 **Format display names / 格式大写名称**: All format names now capitalized (`JPEG`, `JPEG LI`, `JPEG XL`, `PNG`, `WebP`, `AVIF`, `TIFF`) — 全部格式名大写
- 🔄 **JPEG LI independent / JPEG LI 独立**: `JPEG LI` is now a separate format entry from `JPEG`, each with its own encoder options — JPEG LI 从 JPEG 中独立为单独格式选项
- 📐 **Visually-lossless defaults / 视觉无损默认**: AVIF quality default raised to 90, still-picture default checked — AVIF 默认质量提升到 90，静态图片模式默认勾选
- 🛡️ **SkiaSharp vulnerability fix / 漏洞修复**: Upgraded SkiaSharp 2.88.3 → 2.88.6 (CVE-2023-4863 / GHSA-j7hp-h8jx-5ppr)
- 🐛 **Fixes / 修复**: Auto-option fallback when advanced codec panel not checked; format normalization for all UI paths — 不勾选高级编码时选项自动回退；全面统一格式名映射

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
