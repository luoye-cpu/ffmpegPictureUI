# 🖼️ FFmpeg 图片转换器

一个基于 Avalonia UI 的跨平台图片批量转换工具，底层调用系统已安装的 `ffmpeg`/`ffprobe`，将复杂的命令行参数变为直观的图形界面。

## 为什么需要它？

FFmpeg 是处理图片和视频的瑞士军刀，但它的命令行语法对普通用户过于复杂。**FFmpeg 图片转换器** 专为图片处理场景设计，让你无需记忆任何参数，就能完成从简单格式转换到专业色彩调校的全部操作。

## ✨ 核心功能

- **多格式支持**：JPG, PNG, WebP, AVIF, TIFF 随心互转。
- **精细质量控制**：可调节质量/压缩比、色度采样（4:4:4 / 4:2:2 / 4:2:0 等）、位深（8/10/12bit）。
- **高级色彩管理**：支持指定色彩空间、原色、传输特性（primaries / trc / colorspace），适合对颜色有严格要求的场景。
- **智能选项级联**：根据所选输出格式，动态启用/禁用该格式支持的特性，避免输入无效参数。
- **元数据保留**：可选择保留 EXIF、ICC 配置等信息。
- **批量处理队列**：支持拖入多个文件，自动排队转换。
- **并发控制**：可自定义同时运行的转换任务数量，充分利用多核 CPU。
- **参数预设**：将常用的转换方案保存为预设，一键调用。
- **质量分析**：编码完成后提供 SSIM + PSNR 客观质量分析，支持无损编码自动识别。

## 🚀 快速开始

### 前提条件

- **操作系统**：Windows 10/11（其他 .NET 10 支持的平台也可运行，但主要测试环境为 Windows）。
- **.NET 10 SDK**：请从 [dotnet.microsoft.com](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0) 下载并安装。
- **FFmpeg & FFprobe**：必须已安装在系统中，并且路径已添加到系统 `PATH` 环境变量中。  
  在终端输入 `ffmpeg -version` 和 `ffprobe -version`，确认能正常输出版本信息。

### 从源码构建并运行

```bash
# 克隆仓库
git clone https://github.com/luoye-cpu/PLAN-1.git
cd PLAN-1/ffmpeg-gui

# 构建项目
dotnet build src/FfmpegGui/FfmpegGui.csproj

# 直接运行
dotnet run --project src/FfmpegGui/FfmpegGui.csproj
```

### 直接下载运行

从 [Releases](https://github.com/luoye-cpu/PLAN-1/releases) 页面下载 `ffmpeg-gui-avalonia-win-x64-v1.0.1.zip`，解压后运行 `FfmpegGui.exe`。

> 需要系统已安装 [.NET 10 运行时](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0) 和 FFmpeg。

## 📝 更新日志

### v1.0.1 (2026-05-18)

- 🔧 **修复**: 质量分析(SSIM/PSNR)在部分环境下失效的问题
  - 修复仅解析 stderr 导致 stdout 输出的分析结果被忽略
  - 添加 30 秒超时保护，防止 ffmpeg 挂起导致 UI 卡死
  - 修复串行读取 stdout/stderr 可能导致的管道死锁
  - 改用 `filter_complex` + `-map` 替代 `-lavfi`，提升各 ffmpeg 版本兼容性
- 🔧 **修复**: 无损编码(PNG/TIFF)质量分析不显示数据的问题
  - PSNR=∞(inf) 现正确识别为无损编码
  - 自动标注 "🔒 无损编码 — 输出与源图完全一致"
- ✨ **新增**: 分辨率一致性预检，两图尺寸不匹配时给出明确提示
- ✨ **新增**: 文件存在性前置校验，避免文件缺失时无意义报错
- 🛡️ **增强**: 正则解析支持科学计数法和 RGB 通道标签

### v1.0.0

- 🎉 首个正式版本