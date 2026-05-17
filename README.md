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

## 🚀 快速开始

### 前提条件

- **操作系统**：Windows 10/11（其他 .NET 10 支持的平台也可运行，但主要测试环境为 Windows）。
- **.NET 10 SDK**：请从 [dotnet.microsoft.com](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0) 下载并安装。
- **FFmpeg & FFprobe**：必须已安装在系统中，并且路径已添加到系统 `PATH` 环境变量中。
- 可以使用3FUI的ffmpeg 官网地址：3fui.top 和 ffmpegfreeui.top 仓库：github.com/Lake1059/FFmpegFreeUI（这也是一款很不错的ffmpeg UI软件，提供主要是视频压制）
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
