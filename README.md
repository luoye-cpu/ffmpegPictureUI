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

- **元数据处理**：保留全部 / 删除全部，集成 exiftool 后支持选择性删除 GPS、时间、相机信息、XMP 等。
  **Metadata handling**: Preserve all / Strip all; with exiftool integrated, selective deletion of GPS, time, camera info, XMP, etc.

- **元数据隐私保护**：集成 exiftool（可选），可选择性删除 GPS、相机信息、时间等敏感元数据。
  **Privacy protection**: Optional exiftool integration to selectively strip GPS, camera info, timestamps, and other sensitive metadata.

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

从 [Releases](https://github.com/luoye-cpu/PLAN-1/releases) 页面下载 `FfmpegGui-v1.3.0-win-x64.zip`，解压后运行 `FfmpegGui.exe`。压缩包内含 FfmpegGui.exe（单文件，框架依赖）+ cjxl.exe（可选的 JPEG→JXL 快速转码工具）。

Download `FfmpegGui-v1.3.0-win-x64.zip` from the [Releases](https://github.com/luoye-cpu/PLAN-1/releases) page, extract, and run `FfmpegGui.exe`. Package includes FfmpegGui.exe (single file, framework-dependent) + cjxl.exe (optional fast JPEG→JXL transcoder).

> ⚠️ 需要系统已安装 [.NET 10 运行时](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0) 和 FFmpeg。
> ℹ️ cjxl.exe 已内置，exiftool 需自行下载放入 ffmpeg 同目录。
> ⚠️ Requires [.NET 10 Runtime](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0) and FFmpeg installed on the system.
> ℹ️ cjxl.exe is bundled; exiftool must be downloaded separately and placed in the same directory as ffmpeg.

## � 更新日志 / Changelog
### v1.3.0 (2026-05-26)

- 📦 **单文件发布 + cjxl 内置 / Single-file publish + cjxl bundled**: FfmpegGui.exe 单文件发布（框架依赖），压缩包内含 cjxl.exe
- 🧹 **元数据重构 / Metadata overhaul**: 元数据保留从复选框改为下拉框（保留全部 / 删除全部）
- 🔒 **ExifTool 隐私清理 / ExifTool privacy cleaning**: 集成 exiftool（可选），支持选择性删除 GPS、时间、相机信息、全部 EXIF、XMP 等敏感元数据
- 🎨 **位深 auto 选项 / Bit depth auto**: 所有格式增加 auto 选项（不指定位深，由编码器自行判断），各格式位深范围经审核调整
- 🔍 **工具检测三优先级 / 3-tier tool detection**: cjxl/exiftool 检测顺序：手动指定 → 同目录 → 系统 PATH；顶部工具栏支持手动指定和重新检测
- 🗂️ **保持输入目录结构增强 / Preserve folder structure**: 支持保留最外层文件夹名，批量拖拽多文件夹时各自计算路径
- 💾 **预设增强 / Preset enhancement**: 导出/导入预设覆盖 MetadataMode、StripExif*、位深等全部新参数
- 🐛 **修复 / Fix**: 批量队列构建时位深未同步 auto 模式，exiftool 文件名支持 `exiftool(-k).exe`
### v1.2.3 (2026-05-26)

- 🎨 **重构 / Refactor**: UI 全面重构 — Complete UI overhaul
  - 右侧面板采用卡片式设计，圆角边框统一风格 — Card-style design for right-side panels with rounded corners
  - 三个主区域可通过 GridSplitter 自由拖动调整大小 — Three main areas freely resizable via GridSplitter
  - 按钮分组优化，工具栏使用 WrapPanel 自适应换行 — Button grouping optimized, toolbar uses WrapPanel
  - 顶部工具栏增加深色/浅色模式切换按钮 — Dark/Light mode toggle button added to top bar
- 🌓 **新增 / New**: 双色主题适配 — Dual theme support
  - 支持深色模式与浅色模式一键切换 — One-click switch between dark and light modes
  - 主题偏好自动保存，重启后保持 — Theme preference auto-saved, persists after restart
- 🐛 **修复 / Fix**: 多项 bug 修复 — Multiple bug fixes
  - 拖拽单个图片文件不会添加到已选文件列表 — Fixed single image file drag not adding to selected list
  - 拖拽多个文件夹后无法正常加入转换队列 — Fixed multi-folder drag preventing queue addition
  - 队列容量从并发数误用为总上限（1125 文件仅 128 入队）— Queue capacity misused as total limit instead of concurrency
  - 多个控件第二次更改时命令预览不更新 — Command preview not updating on second control change
- 🔧 **优化 / Improved**: cjxl JPEG→JXL 无损转码 — cjxl JPEG→JXL lossless transcoding
  - 命令新增 `--lossless_jpeg=1` 显式声明无损转码，消除 cjxl Note 警告 — Added `--lossless_jpeg=1` to explicitly declare lossless transcoding
  - 移除冗余的「强制保留元数据」选项（cjxl 默认自动保留 EXIF/XMP/ICC）— Removed redundant "Force metadata" option
  - 右侧命令预览正确显示 cjxl 命令而非 ffmpeg 命令 — Command preview correctly shows cjxl command
- 📦 **发布 / Release**: FfmpegGui-v1.2.3-win-x64 — Release package FfmpegGui-v1.2.3-win-x64

### v1.2.2 (2026-05-26)

- ⚡ **增强 / Enhanced**: 队列容量上限从 16 提升至 128 — Queue capacity limit raised from 16 to 128
- ✨ **新增 / New**: 「并行编码任务数」控件升级 — "Concurrent tasks" control upgrade
  - 支持直接键盘输入 1-128 的阿拉伯数字 — Direct keyboard input of Arabic numerals (1-128)
  - 支持上下按钮微调 — Up/down button fine-tuning
  - 实时过滤非数字字符输入 — Real-time non-numeric character filtering
  - 越界值自动修正并显示红色边框提示 — Auto-correct out-of-range values with red border feedback
  - ToolTip 提示 "请输入1-128之间的整数" — ToolTip: "Enter an integer between 1 and 128"
- 🔧 **改进 / Improved**: 队列数量显示格式调整为「队列: X/Y」— Queue count display format changed to "Queue: X/Y"
- 💾 **持久化 / Persistence**: 用户设置的队列数量自动保存到配置文件，重启后保持 — Queue size setting auto-saved to config file, persists after restart
- 📦 **发布 / Release**: FfmpegGui-v1.2.2-win-x64 — Release package FfmpegGui-v1.2.2-win-x64

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
