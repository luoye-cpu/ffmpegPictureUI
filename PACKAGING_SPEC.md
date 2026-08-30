# 📦 FFmpegPictureUI 打包规范

> 版本: 1.1 | 最后更新: 2026-08-30 | 适用于 v1.5.5+ / For v1.5.5+

---

## 一、发布产物命名规则

### 命名格式

```
{AppName}-v{Version}-{Arch}[-{Variant}].{Ext}
```

| 字段 | 说明 | 示例 |
|------|------|------|
| `AppName` | 固定 `FFmpegPictureUI` | FFmpegPictureUI |
| `Version` | 三位语义化版本号 | 1.5.2 |
| `Arch` | CPU 架构标识 | x64, arm64 |
| `Variant` | 可选。`full`=含 PLAN 外部工具包；省略=单文件（不含 PLAN） | full |
| `Ext` | 压缩格式 | 7z, zip |

> **注意**: 所有版本统一使用 **NativeAOT 编译**（无 .NET Runtime 依赖），两版本区别仅是否包含 `PLAN/` 外部工具文件夹。

### 命名示例

| 产物 | 名称 |
|------|------|
| Windows x64 单文件版（NativeAOT，不含 PLAN） | `FFmpegPictureUI-v1.5.2-x64.7z` |
| Windows x64 完整版（NativeAOT + PLAN 工具包） | `FFmpegPictureUI-v1.5.2-x64-full.7z` |
| Linux ARM64 单文件版 | `FFmpegPictureUI-v1.5.2-arm64.tar.gz` |
| Linux ARM64 完整版 | `FFmpegPictureUI-v1.5.2-arm64-full.tar.gz` |

---

## 二、PLAN 文件夹结构规范

打包时，外部工具组件统一放入 `PLAN/` 子目录，程序启动时自动识别。

### 2.1 目录结构

```
FFmpegPictureUI-v1.5.2-x64-full/
├── FfmpegGui.exe                    ← 主程序（由 dotnet publish 生成）
├── FfmpegGui.dll                    ← 主程序集
├── *.dll                            ← 运行时依赖
├── Resources/                       ← 程序资源
│   └── Locales/                     ← 多语言资源
│       ├── zh-CN.json              ← 中文 (默认)
│       └── en-US.json              ← 英文
├── PLAN/                            ← 外部组件根目录（程序自动识别）
│   ├── 使用说明.txt                  ← 用户使用指南（打包时自动生成）
│   ├── ffmpeg-full/                 ← FFmpeg 预编译包
│   │   ├── ffmpeg.exe
│   │   ├── ffprobe.exe
│   │   └── *.dll
│   ├── jxl/                          ← libjxl 预编译包
│   │   └── bin/
│   │       ├── cjxl.exe
│   │       ├── djxl.exe
│   │       └── cjpegli.exe
│   ├── exiftool/                     ← ExifTool
│   │   └── exiftool.exe
│   └── artifacts/                    ← 其他自编译工具
│       ├── ultrahdr_app.exe
│       ├── libuhdr.dll
│       ├── JxrEncApp.exe
│       ├── JxrDecApp.exe
│       ├── avifenc.exe
│       ├── libgcc_s_seh-1.dll        ← GCC 运行时（ultrahdr 依赖）
│       ├── libstdc++-6.dll
│       ├── libwinpthread-1.dll
│       └── libjpeg-9__.dll
└── FFmpegPictureUI.runtimeconfig.json
```

### 2.2 PLAN 子目录识别规则

程序按以下优先级自动识别：

| 子目录 | 识别条件 | 自动配置项 |
|--------|---------|-----------|
| `PLAN/ffmpeg-full/` | 目录存在且含 `ffmpeg(.exe)` | `FfmpegDirectory` |
| `PLAN/jxl/bin/` | 目录存在 | `JxlLibDir`（含 cjxl/djxl/cjpegli） |
| `PLAN/exiftool/` | 目录存在且含 `exiftool(.exe)` | `ExifToolPath` |
| `PLAN/artifacts/` | 目录存在 | `WindowsArtifactsDir`（含 ultrahdr/Jxr/avifenc） |

> **重要**: 仅在用户**未手动配置**对应路径时才自动填充。用户手动设置的路径优先级更高。

---

## 三、打包流程

### 3.1 前置准备

```bash
# 1. 确保代码已更新到目标版本号
dotnet build src/FfmpegGui/FfmpegGui.csproj -c Release

# 2. 更新版本号（FfmpegGui.csproj 中的 <Version> 标签）

# 3. 准备外部工具包（放入 publish/PLAN/ 目录）
```

### 3.2 发布命令（推荐使用 pack.ps1，一键 2 版本，全部 NativeAOT）

```powershell
# 单文件版（NativeAOT，不含 PLAN，需 .NET 11+ SDK 编译）
.\pack.ps1 -Version "1.5.2" -Mode app

# 完整版（NativeAOT + PLAN 组件包）
.\pack.ps1 -Version "1.5.2" -Mode full

# 一键全部 2 个版本（默认）
.\pack.ps1 -Version "1.5.2" -Mode all
```

> **注**: NativeAOT 发布后 SkiaSharp/HarfBuzz/ANGLE 3 个原生渲染库（`libSkiaSharp.dll`/`libHarfBuzzSharp.dll`/`av_libglesv2.dll`）无法嵌入 exe，
> 会以独立文件形式出现在程序目录（.NET 平台限制，`IncludeNativeLibrariesForSelfExtract` 仅对 CoreCLR 生效），
> 打包时自动随目录一起压缩，属预期行为。

手动发布命令（与 pack.ps1 等价）：

```bash
# Windows x64 单文件版（NativeAOT）
dotnet publish src/FfmpegGui/FfmpegGui.csproj `
    -c Release -r win-x64 `
    -p:PublishAot=true `
    -p:SelfContained=true `
    -p:PublishSingleFile=true `
    -p:IlcOptimizationPreference=Speed `
    -p:InvariantGlobalization=true `
    -o publish/build/FFmpegPictureUI-v1.5.2-x64/

# Windows x64 完整版（NativeAOT，另需复制 PLAN/ 目录与使用说明）
dotnet publish src/FfmpegGui/FfmpegGui.csproj `
    -c Release -r win-x64 `
    -p:PublishAot=true `
    -p:SelfContained=true `
    -p:PublishSingleFile=true `
    -p:IlcOptimizationPreference=Speed `
    -p:InvariantGlobalization=true `
    -o publish/build/FFmpegPictureUI-v1.5.2-x64-full/
```

### 3.3 组装完整包

```bash
# 将 PLAN 文件夹复制到完整包目录
xcopy /E /I publish\PLAN publish\build\FFmpegPictureUI-v1.5.2-x64-full\PLAN\

# 生成使用说明文档（见第四章）
# → 输出到 publish\build\FFmpegPictureUI-v1.5.2-x64-full\PLAN\使用说明.txt
```

### 3.4 压缩打包

```bash
# 7-Zip 极限压缩（实测最优配置）
#   -mx9       极限等级；7z 按文件架构自动加 BCJ/BCJ2 过滤器（勿手动 -m0，勿加 mc=1e9 / -mqs）
#   -md=3840m  字典 3840 MiB（LZMA2 上限）
#   -mfb=273   单词大小上限
#   -ms=on     固实压缩
#   -mmt=1     单线程（压缩率最高）
7z a -t7z -mx9 -md=3840m -mfb=273 -ms=on -mmt=1 `
    publish\FFmpegPictureUI-v1.5.2-x64.7z `
    publish\build\FFmpegPictureUI-v1.5.2-x64\*

7z a -t7z -mx9 -md=3840m -mfb=273 -ms=on -mmt=1 `
    publish\FFmpegPictureUI-v1.5.2-x64-full.7z `
    publish\build\FFmpegPictureUI-v1.5.2-x64-full\*
```

**进程优先级（自动最高）**

7z 为 CPU 密集型长任务，应自动拉满调度优先级以缩短打包时间。`pack.ps1` 在启动 7z 后会**自动**将其 `PriorityClass` 设为 **High**（脚本已内置，无需手动操作）。手动压缩时，用下面这段与脚本同构的 PowerShell 片段即可一行「自动拉满」——它自动定位 7z、启动、提权、等待结束：

```powershell
function Invoke-7zMax {
    param([string]$Arguments)
    $exe = (Get-Command 7z -ErrorAction SilentlyContinue).Source
    if (-not $exe -and (Test-Path "C:\Program Files\7-Zip\7z.exe")) { $exe = "C:\Program Files\7-Zip\7z.exe" }
    if (-not $exe) { throw "未找到 7z.exe" }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe; $psi.Arguments = $Arguments; $psi.UseShellExecute = $false
    $p = [System.Diagnostics.Process]::Start($psi)
    try { $p.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::High } catch { }  # 启动后立即提权
    $p.WaitForExit(); return $p.ExitCode
}
# 用法（参数整体作为字符串传入，路径含空格时自行加引号）：
Invoke-7zMax 'a -t7z -mx9 -md=3840m -mfb=273 -ms=on -mmt=1 "out.7z" *'
```

- **High 即安全最高档**：`RealTime` 会抢占系统调度与磁盘 I/O 线程，压缩时整机易卡死、且 7z 自身读写反被拖慢，**禁用 `RealTime`**。
- **GUI 手动压缩**：7z GUI 不能自设优先级，需在任务管理器将 `7z.exe` 设为「高」，或用 `start /HIGH 7z a ...`。

---

## 四、使用说明文档自动生成

每次打包时，自动生成 `PLAN/使用说明.txt`，包含以下内容：

### 4.1 模板

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  FFmpegPictureUI v{VERSION} — 使用说明
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

📌 运行要求
  • Windows 10/11 或更高版本
  • 无需安装任何运行环境（NativeAOT 独立编译）

🚀 快速开始
  1. 解压所有文件到任意目录
  2. 双击运行 FfmpegGui.exe
  3. 程序会自动检测 PLAN 文件夹中的外部工具
  4. 拖入图片即可开始转换

📁 文件结构
  FfmpegGui.exe        — 主程序
  PLAN/                — 外部组件包（程序自动识别）
    ├── ffmpeg-full/   — FFmpeg 编解码引擎
    ├── jxl-*/bin/     — JPEG XL 工具 (cjxl/djxl/cjpegli)
    ├── exiftool-*/    — 元数据编辑工具
    └── windows-artifacts/ — Ultra HDR / JPEG XR 编码器

🖼️ 支持的格式
  输入: JPEG, PNG, WebP, AVIF, JPEG XL, JPEG XR, TIFF, HEIC, DNG, BMP, GIF
  输出: JPEG, PNG, WebP, AVIF, JPEG XL, JPEG XR, TIFF, GIF, APNG
  动图: GIF, WebP(动), APNG, AVIF(动), JPEG XL(动)

🔧 外部工具说明
  • FFmpeg        — 核心编解码引擎（必需）
  • cjxl/djxl     — JPEG XL 高性能编码/解码（推荐）
  • cjpegli       — 高质量 JPEG 编码（推荐）
  • ultrahdr_app  — Ultra HDR JPEG 编码（可选）
  • JxrEncApp     — JPEG XR 编码（可选）
  • exiftool      — 元数据编辑与隐私清理（可选）

❓ 常见问题
  Q: 提示"未检测到 ffmpeg"？
  A: 确保 PLAN/ffmpeg-full/ 目录存在且包含 ffmpeg.exe。
     也可以在设置中手动指定 ffmpeg 路径。

  Q: 如何更新外部工具？
  A: 替换 PLAN/ 下对应子目录中的文件即可，
     程序每次启动时自动重新检测。

  Q: 迁移到其他电脑？
  A: 将整个程序文件夹复制到目标电脑即可（绿色免安装）。
     无需安装 .NET 运行时（NativeAOT 已内置）。

📞 反馈与交流
  QQ 群: 754439779
  GitHub: https://github.com/luoye-cpu/PLAN-1

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  版本: v{VERSION} | 架构: {ARCH} | 构建日期: {DATE}
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

### 4.2 生成时机

- 每次执行打包脚本时自动生成
- 占位符 `{VERSION}`, `{ARCH}`, `{DATE}` 从 `.csproj` 和系统获取

---

## 五、版本号管理

### 5.1 版本号位置

`src/FfmpegGui/FfmpegGui.csproj`:
```xml
<Version>1.5.5</Version>
<AssemblyVersion>1.5.5.0</AssemblyVersion>
<FileVersion>1.5.5.0</FileVersion>
```

### 5.2 更新流程

1. 修改 `.csproj` 中的 `<Version>` 标签
2. 更新 `README.md` 中的版本号
3. 执行打包流程
4. 在 GitHub Releases 中创建对应 tag: `v1.5.5`

---

## 六、精简包 vs 完整包

| 特性 | 精简包 (`-x64.7z`) | 完整包 (`-x64-full.7z`) |
|------|:--:|:--:|
| 主程序 | ✅ | ✅ |
| PLAN 文件夹 | ❌ | ✅ |
| ffmpeg/cjxl/exiftool | ❌（需用户自行安装） | ✅（开箱即用） |
| 压缩后体积 | ~15 MB | ~120 MB |
| 适用场景 | 已有 ffmpeg 环境的用户 | 新用户 / 便携使用 |

---

> 📅 本规范自 v1.5.2 起生效。历史版本保留旧命名规则以兼容已发布 Release。
