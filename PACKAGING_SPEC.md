# 📦 FFmpegPictureUI 打包规范

> 版本: 1.2 | 最后更新: 2026-11-02（按 pack.ps1 现行行为与 beta10 实档校订） | 适用于 v1.6.0+ / For v1.6.0+

---

## 一、发布产物命名规则

### 命名格式

```
{AppName}-v{Version}-{Arch}[-{Variant}].{Ext}
```

| 字段 | 说明 | 示例 |
|------|------|------|
| `AppName` | 固定 `FFmpegPictureUI` | FFmpegPictureUI |
| `Version` | 语义化版本号，可带预发布后缀；`pack.ps1` 未显式传 `-Version` 时自动读 csproj `<Version>` | 1.6.0-beta10 |
| `Arch` | CPU 架构标识（当前 pack.ps1 写死 x64 / win-x64） | x64 |
| `Variant` | 可选。`full`=含 PLAN 外部工具包；省略=单文件（不含 PLAN） | full |
| `Ext` | 压缩格式（当前固定 7z） | 7z |

> **注意**: 所有版本统一使用 **NativeAOT 编译**（无 .NET Runtime 依赖），两版本区别仅是否包含 `PLAN/` 外部工具文件夹。

### 命名示例

| 产物 | 名称 |
|------|------|
| Windows x64 单文件版（NativeAOT，不含 PLAN） | `FFmpegPictureUI-v1.6.0-beta10-x64.7z` |
| Windows x64 完整版（NativeAOT + PLAN 工具包） | `FFmpegPictureUI-v1.6.0-beta10-x64-full.7z` |

> 当前只产出 Windows x64 两档 7z。Linux ARM64 / tar.gz **没有脚本支撑**（`pack.ps1` 的 param 仅 app/full/all，`$Rid` 写死 `win-x64`），故不列幻影产物行。

---

## 二、PLAN 文件夹结构规范

打包时，外部工具组件统一放入 `PLAN/` 子目录，程序启动时自动识别。

### 2.1 目录结构

```
FFmpegPictureUI-v1.6.0-beta10-x64-full/
├── FfmpegGui.exe                    ← 主程序（NativeAOT 单文件，自含 .NET 运行时）
├── libSkiaSharp.dll / libHarfBuzzSharp.dll / av_libglesv2.dll
│                                    ← AOT 无法嵌入的 3 个原生渲染库，随目录分发
├── LICENSE / NOTICE / THIRD-PARTY-NOTICES.md ← 法律文件（缺失即拒绝打包）
├── licenses/                        ← 各第三方许可原文
├── Resources/
│   └── Locales/                     ← 多语言资源（zh-CN.json 默认 / en-US.json）
└── PLAN/                            ← 外部组件根目录（程序自动识别）
    ├── 使用说明.txt                  ← 用户使用指南（仅 full 模式生成）
    ├── ffmpeg-full/                 ← FFmpeg 预编译包（ffmpeg.exe / ffprobe.exe / *.dll）
    ├── jxl/bin/                     ← cjxl.exe / djxl.exe / cjpegli.exe / jxlinfo.exe
    ├── exiftool/                    ← exiftool.exe
    └── artifacts/                   ← JxrEncApp.exe / JxrDecApp.exe / avifenc.exe / dngtool.exe
```

### 2.2 PLAN 子目录识别规则

程序按以下优先级自动识别：

| 子目录 | 识别条件 | 自动配置项 |
|--------|---------|-----------|
| `PLAN/ffmpeg-full/` | 目录存在且含 `ffmpeg(.exe)` | `FfmpegDirectory` |
| `PLAN/jxl/bin/` | 目录存在 | `JxlLibDir`（含 cjxl/djxl/cjpegli） |
| `PLAN/exiftool/` | 目录存在且含 `exiftool(.exe)` | `ExifToolPath` |
| `PLAN/artifacts/` | 目录存在 | `WindowsArtifactsDir`（含 Jxr/avifenc/dngtool） |

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
# 推荐：省略 -Version（自动读 src/FfmpegGui/FfmpegGui.csproj 的 <Version>，避免与项目版本漂移）
.\pack.ps1 -Mode app     # 单文件版（NativeAOT，不含 PLAN，需 .NET 11+ SDK）
.\pack.ps1 -Mode full    # 完整版（NativeAOT + PLAN 组件包）
.\pack.ps1 -Mode all     # 一键全部 2 个版本（默认）

# 显式 -Version 仅用于覆盖 csproj；pack 不对账 csproj，错配要靠
# verify-release-package / verify-package-gui-starts 的版本对撞兜底
```

> **注**: NativeAOT 发布后 SkiaSharp/HarfBuzz/ANGLE 3 个原生渲染库（`libSkiaSharp.dll`/`libHarfBuzzSharp.dll`/`av_libglesv2.dll`）无法嵌入 exe，
> 会以独立文件形式出现在程序目录（.NET 平台限制，`IncludeNativeLibrariesForSelfExtract` 仅对 CoreCLR 生效），
> 打包时自动随目录一起压缩，属预期行为。

> **出包只留最新一档**（2026-10-28 定，AGENTS.md §1）：出新包后，旧版本的
> `publish/FFmpegPictureUI-v<旧版>-x64*.7z` 与 `publish/build/` 同名解包目录**直接删除**
> （`publish/PLAN` 随包工具链与 `publish/rescued-*` 存证不在删除范围）。理由是防"读数打在哪个包上"答错；
> 留证据靠日志与哈希（`ProductVersion`、包内 exe `sha256[:16]`），不留旧包。
> `pack.ps1` 本体**不**自动删旧档，此规则靠人工执行 + `verify-release-package` 的多版本歧义拒绝兜底。

手动发布命令（与 pack.ps1 等价，**必须两步**）：

```powershell
# ⚠ 2026-10-28 实测：让 publish 自己重编（单步 publish）时，编译产物里没有 Avalonia 资源表
#   （exe 字节里 `!AvaloniaResources` / `!XamlIlPopulate` 0 命中），出货包 GUI 一键都打不开
#   （XamlLoadException: No precompiled XAML for FfmpegGui.App，0xC0000409）——beta9/beta10 两版事故。
#   正确顺序 = 先 build，再 publish -p:NoBuild=true：
dotnet build src/FfmpegGui/FfmpegGui.csproj -c Release --nologo

# 然后发布（单文件版与完整版同一步骤，仅 -o 目录不同）：
dotnet publish src/FfmpegGui/FfmpegGui.csproj `
    -c Release -r win-x64 `
    -p:PublishAot=true `
    -p:NoBuild=true `
    -p:SelfContained=true `
    -p:PublishSingleFile=true `
    -p:IlcOptimizationPreference=Speed `
    -p:InvariantGlobalization=true `
    -p:Version=1.6.0-beta10 `
    -o publish/build/FFmpegPictureUI-v1.6.0-beta10-x64/
# 完整版把 -o 换成 publish\build\FFmpegPictureUI-v1.6.0-beta10-x64-full\
```

### 3.3 组装完整包

（pack.ps1 的 full 模式自动完成以下两步；等价手工步骤如下——与旧版 xcopy 写法不同，注意先删目标再 robocopy 排除）

```powershell
# 1) 复制 PLAN：先删目标目录（防旧文件残留混装，10-01 事故对策），再用 robocopy
#    排除编译期元数据（.lib / .pc / .cmake 与 pkgconfig、cmake 目录运行时无用）
Remove-Item -Recurse -Force publish\build\FFmpegPictureUI-v1.6.0-beta10-x64-full\PLAN -ErrorAction SilentlyContinue
robocopy publish\PLAN publish\build\FFmpegPictureUI-v1.6.0-beta10-x64-full\PLAN `
    /E /XF *.lib *.pc *.cmake /XD pkgconfig cmake

# 2) 使用说明：以 publish\PLAN\使用说明.txt 为模板替换 {VERSION}/{ARCH}/{DATE} 占位符
#    → 输出到 publish\build\FFmpegPictureUI-v1.6.0-beta10-x64-full\PLAN\使用说明.txt
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

完整版打包时自动生成 `PLAN/使用说明.txt`（单文件版不含 PLAN、不生成），包含以下内容：

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
  LICENSE / NOTICE /
  THIRD-PARTY-NOTICES.md — 许可与声明
  licenses/            — 第三方许可原文
  PLAN/                — 外部组件包（程序自动识别）
    ├── ffmpeg-full/   — FFmpeg 编解码引擎
    ├── jxl/bin/       — JPEG XL 工具 (cjxl/djxl/cjpegli)
    ├── exiftool/      — 元数据编辑工具
    └── artifacts/     — Ultra HDR / JPEG XR 编码器

🖼️ 支持的格式
  输入: JPEG, PNG, WebP, AVIF, JPEG XL, JPEG XR, TIFF, HEIC, DNG, BMP, GIF
  输出: JPEG, PNG, WebP, AVIF, JPEG XL, JPEG XR, TIFF, GIF, APNG
  动图: GIF, WebP(动), APNG, AVIF(动), JPEG XL(动)

🔧 外部工具说明
  • FFmpeg        — 核心编解码引擎（必需）
  • cjxl/djxl     — JPEG XL 高性能编码/解码（推荐）
  • cjpegli       — 高质量 JPEG 编码（推荐）
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
  GitHub: https://github.com/luoye-cpu/ffmpegPictureUI

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  版本: v{VERSION} | 架构: {ARCH} | 构建日期: {DATE}
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

### 4.2 生成时机

- 仅 `-Mode full` / `-Mode all`（完整版）打包时生成；单文件版不含 PLAN，不生成
- 占位符 `{VERSION}`, `{ARCH}`, `{DATE}` 由 pack.ps1 用 `-Version`（或自动读取的 csproj `<Version>`）与系统日期替换
- ⚠ 模板正本 = `publish\PLAN\使用说明.txt`（随包工具链维护、不入 git）。2026-11-02 之前模板的「文件结构」段
  仍写着旧目录名 `jxl-*/`、`exiftool-*/`、`windows-artifacts/` ⇒ **beta10 包内说明书是旧文案**，下次出包起随本节更正

---

## 五、版本号管理

### 5.1 版本号位置

`src/FfmpegGui/FfmpegGui.csproj`:
```xml
<Version>1.6.0-beta10</Version>
<AssemblyVersion>1.6.0.0</AssemblyVersion>
<FileVersion>1.6.0.0</FileVersion>
```

### 5.2 更新流程

1. 修改 `.csproj` 中的 `<Version>` 标签（`pack.ps1` 未显式传 `-Version` 时自动读取它，推荐省略 `-Version`）
2. 执行打包流程（§3.2）
3. 发布稳定版时再更新 `README.md` / `README.en.md` 的版本行与 CHANGELOG 条目
4. 打 annotated tag：现行命名**无 v 前缀、无连字符**——正式 `1.6.0`、预发布 `1.6.0BETA10`
   ⚠ 2026-11-02 审查结论：beta10 时代同一 tag 曾四次重指（版本号不升版的重出），
   「tag = 可重现出包点」已松动；重出时应升版本号新打 tag，不要移动旧 tag

---

## 六、精简包 vs 完整包

| 特性 | 精简包 (`-x64.7z`) | 完整包 (`-x64-full.7z`) |
|------|:--:|:--:|
| 主程序 | ✅ | ✅ |
| PLAN 文件夹 | ❌ | ✅ |
| ffmpeg/cjxl/exiftool | ❌（需用户自行安装） | ✅（开箱即用） |
| 压缩后体积 | ~11.4 MB（beta10 实测 11,430,807 B） | ~55.9 MB（beta10 实测 55,853,619 B） |
| 适用场景 | 已有 ffmpeg 环境的用户 | 新用户 / 便携使用 |

---

> 📅 本规范自 v1.5.2 起生效。历史版本保留旧命名规则以兼容已发布 Release。
