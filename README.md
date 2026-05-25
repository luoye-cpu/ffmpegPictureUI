# 🖼️ FFmpeg 图片转换器 / FFmpeg Image Converter

一个基于 Avalonia UI 的跨平台图片批量转换工具，底层调用系统已安装的 `ffmpeg`/`ffprobe`，将复杂的命令行参数变为直观的图形界面。

A cross-platform batch image conversion tool based on Avalonia UI, leveraging the system-installed `ffmpeg`/`ffprobe` to turn complex command-line arguments into an intuitive graphical interface.

## 为什么需要它？ / Why do you need it?

FFmpeg 是处理图片和视频的瑞士军刀，但它的命令行语法对普通用户过于复杂。**FFmpeg 图片转换器** 专为图片处理场景设计，让你无需记忆任何参数，就能完成从简单格式转换到专业色彩调校的全部操作。

FFmpeg is the Swiss Army knife for image and video processing, but its command-line syntax is overly complex for ordinary users. **FFmpeg Image Converter** is designed specifically for image processing scenarios, allowing you to perform everything from simple format conversion to professional color adjustments without memorizing any parameters.

## ✨ 核心功能 / Core Features

- **多格式支持**：JPG, PNG, WebP, AVIF, JXL, TIFF 随心互转。
  **Multi-format support**: Freely convert between JPG, PNG, WebP, AVIF, JXL, TIFF.

- **精细质量控制**：可调节质量/压缩比、色度采样（4:4:4 / 4:2:2 / 4:2:0 等）、位深（8/10/12bit）。
  **Fine-grained quality control**: Adjust quality/compression ratio, chroma subsampling (4:4:4 / 4:2:2 / 4:2:0, etc.), and bit depth (8/10/12bit).

- **高级色彩管理**：支持指定色彩空间、原色、传输特性（primaries / trc / colorspace），适合对颜色有严格要求的场景。
  **Advanced color management**: Specify color space, primaries, and transfer characteristics (primaries / trc / colorspace), ideal for scenarios requiring strict color accuracy.

- **智能选项级联**：根据所选输出格式，动态启用/禁用该格式支持的特性，避免输入无效参数。
  **Smart option cascading**: Dynamically enable/disable features based on the selected output format, preventing invalid parameter input.

- **元数据保留**：可选择保留 EXIF、ICC 配置等信息。
  **Metadata preservation**: Optionally retain EXIF, ICC profiles, and other information.

- **批量处理队列**：支持拖入多个文件，自动排队转换。
  **Batch processing queue**: Drag and drop multiple files, automatically queuing them for conversion.

- **并发控制**：可自定义同时运行的转换任务数量，充分利用多核 CPU。
  **Concurrency control**: Customize the number of concurrent conversion tasks to fully utilize multi-core CPUs.

- **参数预设**：将常用的转换方案保存为预设，一键调用。
  **Parameter presets**: Save frequently used conversion schemes as presets for one-click reuse.

- **质量分析**：编码完成后提供 SSIM + PSNR 客观质量分析，支持无损编码自动识别。
  **Quality analysis**: Provides SSIM + PSNR objective quality analysis after encoding, with automatic lossless encoding detection.

## 🚀 快速开始 / Quick Start

### 前提条件 / Prerequisites

- **操作系统**：Windows 10/11（其他 .NET 10 支持的平台也可运行，但主要测试环境为 Windows）。
  **Operating System**: Windows 10/11 (other platforms supported by .NET 10 can also run, but the primary test environment is Windows).

- **.NET 10 SDK**：请从 [dotnet.microsoft.com](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0) 下载并安装。
  **.NET 10 SDK**: Download and install from [dotnet.microsoft.com](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0).

- **FFmpeg & FFprobe**：必须已安装在系统中，并且路径已添加到系统 `PATH` 环境变量中。
  在终端输入 `ffmpeg -version` 和 `ffprobe -version`，确认能正常输出版本信息。
  **FFmpeg & FFprobe**: Must be installed on the system, and their paths must be added to the system `PATH` environment variable.
  Run `ffmpeg -version` and `ffprobe -version` in the terminal to confirm they work correctly.

> 💡 推荐使用 [3FUI 的 FFmpeg](https://3fui.top) 或 [FFmpegFreeUI](https://ffmpegfreeui.top)（仓库：[Lake1059/FFmpegFreeUI](https://github.com/Lake1059/FFmpegFreeUI)），这是一款优秀的 FFmpeg GUI 软件，主要面向视频压制场景。
> 💡 We recommend using FFmpeg from [3FUI](https://3fui.top) or [FFmpegFreeUI](https://ffmpegfreeui.top) (repo: [Lake1059/FFmpegFreeUI](https://github.com/Lake1059/FFmpegFreeUI)), an excellent FFmpeg GUI tool primarily for video compression.

### 从源码构建并运行 / Build from Source

```bash
# 克隆仓库 / Clone the repo
git clone https://github.com/luoye-cpu/PLAN-1.git
cd PLAN-1/ffmpeg-gui

# 构建项目 / Build the project
dotnet build src/FfmpegGui/FfmpegGui.csproj

# 直接运行 / Run directly
dotnet run --project src/FfmpegGui/FfmpegGui.csproj
```

### 直接下载运行 / Download & Run

从 [Releases](https://github.com/luoye-cpu/PLAN-1/releases) 页面下载 `FfmpegGui-v1.2.1-win-x64.zip`，解压后运行 `FfmpegGui.exe`。

Download `FfmpegGui-v1.2.1-win-x64.zip` from the [Releases](https://github.com/luoye-cpu/PLAN-1/releases) page, extract, and run `FfmpegGui.exe`.

> ⚠️ 需要系统已安装 [.NET 10 运行时](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0) 和 FFmpeg。
> ⚠️ Requires [.NET 10 Runtime](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0) and FFmpeg installed on the system.

## 📝 更新日志 / Changelog

### v1.2.1 (2026-05-25)

- 🧹 **优化 / Improved**: 队列控制栏 UI 改进 — Queue control bar UI improvements
  - 「清空转换队列」按钮移至队列工具栏最前面，仅在队列停止后可点击 — "Clear queue" button moved to front, only enabled when queue is stopped
  - 移除冗余的「清空已选」按钮 — Removed redundant "Clear selected" button
  - 「完成当前队列后停止」默认勾选 — "Stop after current queue" checked by default
- 🐛 **修复 / Fix**: 「清空转换队列」按钮现在可以清除所有非运行中的任务（已完成/已出错/待处理） — "Clear queue" now removes all non-running items (completed/errored/pending), not just pending ones
- 📦 **发布 / Release**: 发布包内置 cjxl.exe — Release package includes cjxl.exe

### v1.1.0 (2026-05-18)

- 🚀 **新增 / New**: 集成 cjxl.exe，JPEG→JXL 无损重封装极速模式 — Integrated cjxl.exe for ultra-fast JPEG→JXL lossless transcoding
  - 直接复制 JPEG DCT 系数，无需解码像素，速度提升 5-10× — Directly copies JPEG DCT coefficients without decoding, 5-10× faster
  - cjxl.exe 已内置在发布包中，开箱即用 — cjxl.exe bundled in the release package, ready to use
  - 三级检测：cjxl 优先 → ffmpeg libjxl → 提示安装 — Three-tier detection: cjxl first → ffmpeg libjxl → install hint
  - UI 颜色区分：青色(cjxl) / 橙色(ffmpeg) / 橙色(提示) — Color-coded UI: teal(cjxl) / orange(ffmpeg) / orange(hint)
- ⚡ **增强 / Enhanced**: JPEG→JXL 未来 ffmpeg 升级 libjxl 后自动启用 lossless_jpeg 参数 — Auto-enables ffmpeg -lossless_jpeg param when ffmpeg upgrades libjxl in the future

### v1.0.1 (2026-05-18)

- 🔧 **修复 / Fix**: 质量分析(SSIM/PSNR)在部分环境下失效的问题 — Quality analysis (SSIM/PSNR) not working in some environments
  - 修复仅解析 stderr 导致 stdout 输出的分析结果被忽略 — Fixed parsing only stderr, ignoring results output to stdout
  - 添加 30 秒超时保护，防止 ffmpeg 挂起导致 UI 卡死 — Added 30-second timeout to prevent UI freeze from ffmpeg hang
  - 修复串行读取 stdout/stderr 可能导致的管道死锁 — Fixed potential pipe deadlock from serial stdout/stderr reads
  - 改用 `filter_complex` + `-map` 替代 `-lavfi`，提升各 ffmpeg 版本兼容性 — Switched to `filter_complex` + `-map` for better ffmpeg version compatibility
- 🔧 **修复 / Fix**: 无损编码(PNG/TIFF)质量分析不显示数据 — Lossless encoding (PNG/TIFF) quality analysis showing no data
  - PSNR=∞(inf) 现正确识别为无损编码 — PSNR=∞(inf) now correctly recognized as lossless
  - 自动标注 "🔒 无损编码 — 输出与源图完全一致" — Auto-labels "🔒 Lossless — output identical to source"
- ✨ **新增 / New**: 分辨率一致性预检，两图尺寸不匹配时给出明确提示 — Resolution consistency pre-check with clear mismatch warning
- ✨ **新增 / New**: 文件存在性前置校验，避免文件缺失时无意义报错 — File existence pre-validation to avoid meaningless errors
- 🛡️ **增强 / Enhanced**: 正则解析支持科学计数法和 RGB 通道标签 — Regex parsing supports scientific notation and RGB channel labels

### v1.0.0

- 🎉 首个正式版本 — First official release
