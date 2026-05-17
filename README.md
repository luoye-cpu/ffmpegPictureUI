🖼️ FFmpeg 图片转换器  
🖼️ FFmpeg Image Converter

一个基于 Avalonia UI 的跨平台图片批量转换工具，底层调用系统已安装的 ffmpeg/ffprobe，将复杂的命令行参数变为直观的图形界面。  
A cross-platform batch image conversion tool based on Avalonia UI, leveraging the system-installed ffmpeg/ffprobe to turn complex command-line arguments into an intuitive graphical interface.

为什么需要它？  
Why do you need it?

FFmpeg 是处理图片和视频的瑞士军刀，但它的命令行语法对普通用户过于复杂。FFmpeg 图片转换器 专为图片处理场景设计，让你无需记忆任何参数，就能完成从简单格式转换到专业色彩调校的全部操作。  
FFmpeg is the Swiss Army knife for image and video processing, but its command-line syntax is overly complex for ordinary users. The FFmpeg Image Converter is designed specifically for image processing scenarios, allowing you to perform everything from simple format conversion to professional color adjustments without memorizing any parameters.

✨ 核心功能  
✨ Core Features

- 多格式支持：JPG, PNG, WebP, AVIF, TIFF 随心互转。  
- Multi-format support: Freely convert between JPG, PNG, WebP, AVIF, TIFF.

- 精细质量控制：可调节质量/压缩比、色度采样（4:4:4 / 4:2:2 / 4:2:0 等）、位深（8/10/12bit）。  
- Fine-grained quality control: Adjust quality/compression ratio, chroma subsampling (4:4:4 / 4:2:2 / 4:2:0, etc.), and bit depth (8/10/12bit).

- 高级色彩管理：支持指定色彩空间、原色、传输特性（primaries / trc / colorspace），适合对颜色有严格要求的场景。  
- Advanced color management: Specify color space, primaries, and transfer characteristics (primaries / trc / colorspace), ideal for scenarios that require strict color accuracy.

- 智能选项级联：根据所选输出格式，动态启用/禁用该格式支持的特性，避免输入无效参数。  
- Smart option cascading: Dynamically enable/disable features based on the selected output format, preventing invalid parameter input.

- 元数据保留：可选择保留 EXIF、ICC 配置等信息。  
- Metadata preservation: Optionally retain EXIF, ICC profiles, and other information.

- 批量处理队列：支持拖入多个文件，自动排队转换。  
- Batch processing queue: Drag and drop multiple files, automatically queuing them for conversion.

- 并发控制：可自定义同时运行的转换任务数量，充分利用多核 CPU。  
- Concurrency control: Customize the number of concurrent conversion tasks to fully utilize multi-core CPUs.

- 参数预设：将常用的转换方案保存为预设，一键调用。  
- Parameter presets: Save frequently used conversion schemes as presets for one-click reuse.

🚀 快速开始  
🚀 Quick Start

前提条件  
Prerequisites

- 操作系统：Windows 10/11（其他 .NET 10 支持的平台也可运行，但主要测试环境为 Windows）。  
- Operating System: Windows 10/11 (other platforms supported by .NET 10 can also run, but the primary test environment is Windows).

- .NET 10 SDK：请从 dotnet.microsoft.com 下载并安装。  
- .NET 10 SDK: Download and install from dotnet.microsoft.com.

- FFmpeg & FFprobe：必须已安装在系统中，并且路径已添加到系统 PATH 环境变量中。  
- FFmpeg & FFprobe: Must be installed on the system, and their paths must be added to the system PATH environment variable.

可以使用3FUI的ffmpeg 官网地址：3fui.top 和 ffmpegfreeui.top 仓库：github.com/Lake1059/FFmpegFreeUI（这也是一款很不错的ffmpeg UI软件，提供主要是视频压制） 在终端输入 ffmpeg -version 和 ffprobe -version，确认能正常输出版本信息。  
You can use FFmpeg from 3FUI (official websites: 3fui.top and ffmpegfreeui.top; repository: github.com/Lake1059/FFmpegFreeUI — which is also a great FFmpeg UI software, mainly for video compression). In the terminal, run `ffmpeg -version` and `ffprobe -version` to confirm that they output version information correctly.
