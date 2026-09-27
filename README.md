**简体中文** | [English](README.en.md)

# 🖼️ FFmpegPictureUI — FFmpeg 图片转换器

**v1.6.0** — 2026-09-28 发布 · 基于 Avalonia UI 的跨平台批量图片 / 动图 / 视频转换工具，封装 `ffmpeg` / `ffprobe` + 外部编码器（`cjxl` / `djxl` / `cjpegli` / `JxrEncApp` / `JxrDecApp`）。

QQ 交流群：754439779 | [点击加群](https://qm.qq.com/q/M2181PvCkW)

---

## ✨ 核心功能

| 功能 | 说明 |
|---|---|
| **多格式** | JPEG, PNG, WebP, AVIF, JPEG XL, TIFF — 动图：GIF, WebP (动态), APNG, AVIF (动态), JPEG XL (动态)；JPEG-LI 已整合为 JPEG 的 `cjpegli` 编码器选项 |
| **编码器后端** | 每种格式可选 `ffmpeg` / `cjxl` / `cjpegli` 编码器；`cjxl` 用于 JXL 无损 JPEG 重封装 |
| **质量控制** | 质量滑块（吸附整数）+ 格式感知数字输入框（JPEG q:v 2-31，JXL distance 0-15 等） |
| **高级编码选项** | 按格式独立高级面板：DCT 算法、渐进模式、Huffman 优化、自适应量化、sjpeg 后端、PSNR 目标、无损压缩级别、row-mt、still-picture、modular 模式 |
| **色彩管理** | sRGB/BT.709/Display P3/BT.2020 PQ/HLG 快速选择；CICP (H.273) 与 ICC 双标定；4 种**色彩策略**（推荐 / 携带源 ICC / 仅CICP / 手动）；`iccgen` 自动生成标准 ICC；**ProPhoto/ROMM 像素原样保真（仅靠 ICC 描述，不归一削色）**；**纯托管 ICC 写入器**（iccgen 无法命名的空间，无需外部 .icc 资源）；zscale 双向烘焙；HDR→SDR 色调映射降级；BT.2020 自动位深联动；TV/PC 色彩范围（auto 跟随输入，AVIF 原生支持）；Gain Map RGB 建议 |
| **JXL 智能** | 自动检测 JPEG 重建 vs 原生码流；字节级检测（`JxlInspector`）；自动选择最优管线 |
| **JPEG-LI** | `cjpegli` 作为 JPEG 格式的编码器后端选项，提供完整高级配置（色度子采样、渐进模式等）|
| **CPU SIMD** | 自动检测 AVX2/AVX/SSE4 兼容二进制；运行时探测验证兼容性 |
| **批量队列** | 拖放输入；可配置并发（1–128）；队列完成后自动停止 |
| **元数据编辑** | ~90 字段 9 大分类 exiftool 编辑器（Basic, DateTime, Camera, Shooting, GPS, Image, IPTC, XMP, Color），双击文件打开 |
| **隐私清理** | 剥离 GPS、时间戳、相机信息、全部 EXIF、XMP |
| **质量分析** | 编码后 SSIM + PSNR；自动检测无损；**.NET 原生 PSNR**（AVX512/AVX2/SSE2，比 ffmpeg filter 快 20×）；**目标域分析**（RGB 原生格式 → RGB，YUV 原生 → YUV，与 ffmpeg 0.0000dB 一致）；**位深归一化**（8/10/16-bit，MaxValue 缩放） |
| **预设** | 29 个内置预设 + 二级管理窗口，支持保存/加载/导入用户预设 |
| **双色主题** | 深色/浅色主题；队列文字颜色自适应 |
| **双语界面** | 中文 / English 一键切换，右上角按钮即时生效，JSON 资源文件 |
| **格式筛选** | 勾选启用的图片格式，持久化保存到设置 |
| **动图模式** | 模式切换（静态/动图）；帧率/循环/缩放/时长控制（自动或手动）；按格式独立动画高级面板；支持视频输入 |
| **无损锁定** | PNG/TIFF/APNG 无损格式自动锁定最高质量、禁用滑块 |
| **搜索拖放** | Windows 搜索结果经 Shell 命名空间路径正确解析 |
| **Gain Map (Ultra HDR)** | **全纯托管编解码，无 libultrahdr 依赖**：编码用所选 JPEG 后端（`cjpegli`/`ffmpeg mjpeg`），底层符合 ISO 21496-1 + XMP(hdrgm) + MPF 三重元数据；解码为纯托管 ISO/MPF 容器直读（exiftool 仅做交叉校验/兜底）；底图可选 **sRGB SDR**（默认，兼容 Android/libultrahdr）或 **BT.2100 PQ HDR**（实验性，容器内保留完整 HDR） |
| **实时进度** | 详情窗口实时更新命令 + 进度，支持 ffmpeg/cjxl/cjpegli/djxl/管道全部后端 |

---

## 🔧 外部工具依赖

| 工具 | 状态 | 用途 |
|---|---|---|
| `ffmpeg` + `ffprobe` | ✅ 必需 | 核心编解码、媒体探测 |
| `cjxl` / `djxl` / `cjpegli` | ⭐ 推荐 | JXL 转码、解码、JPEG-LI 编码 |
| `JxrEncApp` / `JxrDecApp` | ⭐ 推荐 | JPEG XR 编码/解码 (Microsoft jxrlib) |
| `avifenc` | ⚪ 可选 | GIF → AVIF 两步编码（保留 alpha） |
| `dngtool` | ⭐ 推荐 | DNG 1.7 JXL 解码/编码、RAW 去马赛克 (LibRaw + Adobe DNG SDK) |
| `exiftool` | ⚪ 可选 | 元数据编辑、隐私清理、ICC 嵌入 |

---

## 🚀 快速开始

### 前提条件

- **系统**：Windows 10/11（其他 .NET 11 平台应可运行）
- **.NET 11 运行时**：[下载](https://dotnet.microsoft.com/en-us/download/dotnet/11.0)
- **FFmpeg**：安装并确认终端可运行 `ffmpeg -version`

```bash
# 克隆并构建
git clone https://github.com/luoye-cpu/ffmpegPictureUI.git
cd ffmpegPictureUI
dotnet build src/FfmpegGui/FfmpegGui.csproj -c Release
dotnet run --project src/FfmpegGui/FfmpegGui.csproj
```

或从 [Releases 发布页](https://github.com/luoye-cpu/ffmpegPictureUI/releases) 下载。

---

## ⚙️ 无界面批处理（Headless CLI）

支持 `--headless` 模式，无需 GUI 即可批量转换，适合脚本/CI/服务器后台任务。

### 基本用法

```bash
# 批量转换目录下所有图片为 JXL（无损，保留目录结构）
FfmpegGui.exe --headless -i D:\Photos -o D:\Converted -f jxl -e Cjxl --preserve-structure

# 指定质量/并发转为 AVIF
FfmpegGui.exe --headless -i *.jpg -o out -f avif -q 85 -j 4

# 使用预设
FfmpegGui.exe --headless -i input.mov -o out -f jxl --preset "JXL Lossless"

# 仅预览命令不执行（dry-run）
FfmpegGui.exe --headless --dry-run -i *.jpg -o out -f jxl -e Cjxl

# 结构化日志双写（控制台 + JSONL 文件）
FfmpegGui.exe --headless -i *.jpg -o out -f jxl \
  --log-file logs/convert.jsonl --log-format JsonLines
```

### 主要参数

| 参数 | 说明 |
|------|------|
| `-i/--input` | 输入文件/目录/通配符（逗号分隔多个） |
| `-o/--output` | 输出目录 |
| `--preserve-structure` | 保留输入目录结构 |
| `-f/--format` | 输出格式 (jpg/png/webp/avif/jxl/jxr/tiff/gif/dng) |
| `-e/--encoder` | 编码器后端 (Ffmpeg/Cjxl/Cjpegli/Jxr/Dng) |
| `-q/--quality` | 质量 1-100 |
| `-j/--concurrency` | 并发任务数（默认 CPU 核心数） |
| `--auto-threads` | 自动分配每任务线程数 |
| `--settings <file>` | 指定配置文件 |
| `--log-file <file>` | 日志输出文件 |
| `--log-level <lvl>` | Error/Warn/Info/Debug/Trace |
| `--log-format <fmt>` | Text/JsonLines |
| `--dry-run` | 仅打印命令不执行 |
| `--preset <name>` | 使用内置/用户预设 |
| `--strip-*` | 隐私清理（--strip-gps/--strip-time/--strip-camera/--strip-all-exif/--strip-xmp） |

### 配置分层优先级

```
环境变量 (FFMPEGGUI_*) > --settings 文件 > 便携 settings.json > 用户配置目录 > 默认
```

常用环境变量：`FFMPEGGUI_FFMPEG_DIR`、`FFMPEGGUI_OUTPUT_DIR`、`FFMPEGGUI_JXL_LIB_DIR`、`FFMPEGGUI_PLAN_DIR`、`FFMPEGGUI_GPU`。

外部工具探测兜底：`FFMPEGGUI_EXT_SEARCH_DIRS`（`;` 分隔的目录列表）。设置后，探测的"系统扩展搜索"一级**只用这些根**，
不再递归 `%LOCALAPPDATA%\Programs`、`C:\Program Files` 与 PATH 里的每个目录（该级实测在 PATH 含大树时会把启动拖成分钟级）。
未设置时行为不变。

### 系统托盘

关闭主窗口默认最小化到系统托盘（而非退出），队列后台继续运行。托盘菜单支持「显示主窗口 / 暂停队列 / 退出」。

---

## 🔬 JXL 转换管线

应用通过字节级检测判断 JXL 文件类型并自动选择最优路径：

### 场景 A — JPEG 套壳（JPEG Reconstruction）

```
.jxl (JPEG 套壳)  ──djxl──▶  .jpg (位级还原，零质量损失)
```

### 场景 B — 原生（Native Codestream）

```
.jxl (原生)  ──djxl──▶  PNG 流 ══管道══▶  cjpegli  ──▶  .jpg (首选)
              ──djxl──▶  临时 PNG   ──▶  cjpegli  ──▶  .jpg (回退)
              ──ffmpeg libjxl──▶  mjpeg     ──▶  .jpg (兜底)
```

---

## 🏗️ 项目结构

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
│   │                     PresetManagerService, RawService,
│   │                     GpuCapabilityService, PlatformServices,
│   │                     LocalizationService, PsRenderService, PsnrCalculator,
│   │                     SimdPixelOps（Photoshop ACR 验证, .NET 原生 PSNR, SIMD）
│   ├── Controls/         MetadataEditor
│   ├── Resources/Locales/ zh-CN.json, en-US.json
│   ├── LocExtension.cs   XAML 本地化标记扩展
│   ├── MainWindow.xaml   主界面
│   ├── MainWindow.xaml.cs UI 逻辑
│   ├── FormatFilterWindow.axaml  格式筛选对话框
│   ├── PresetManagerWindow.axaml 预设管理窗口
│   ├── ProgressWindow.xaml 进度窗口
├── tools/                验证工具
├── tests/                测试（output/ 被 git 忽略，见 docs/TESTING.md）
└── publish/              发布输出
```

---

## 📝 更新日志

当前版本 **v1.6.0**（2026-09-28）。完整版本历史见 **[CHANGELOG.md](CHANGELOG.md)**。


## 📄 许可

本项目采用 **GNU General Public License v3.0 (GPL 3.0)** 许可。

- 完整许可文本：[LICENSE](LICENSE)
- 版权与必需声明：[NOTICE](NOTICE)
- 第三方组件许可清单：[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
- 各第三方许可原文：[licenses/](licenses/)

> This product includes DNG technology under license by Adobe.
> 本产品包含 Adobe 授权的 DNG 技术（dngtool 使用 Adobe DNG SDK）。

> ⚠️ GPL 3.0 是强传染性许可证。若你分发本软件的修改版本（含二进制形式），你必须同时以 GPL 3.0 开源其源代码。

> FFmpeg 是 Fabrice Bellard 的商标。本项目与 FFmpeg 项目无隶属关系，亦未获其背书。
