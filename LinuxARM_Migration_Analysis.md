# FFmpegPictureUI v1.5.0 → Linux ARM 迁移分析报告 / Migration Analysis

> 分析日期 / Analysis Date: 2026-07-23 | 基线版本 / Baseline: v1.5.0 | 目标平台 / Target: Linux ARM (aarch64)

---

## 一、项目概览

| 项目属性 | 当前值 |
|----------|--------|
| 框架 | .NET 10.0 |
| UI 框架 | Avalonia 12.0.4 |
| 输出类型 | `WinExe` |
| 运行时标识 | `win-x64;linux-x64;linux-arm64` (已配置) / Already configured |
| 发布模式 | 单文件 (PublishSingleFile)，非自包含 |
| 外部工具 | ffmpeg, ffprobe, cjxl, djxl, cjpegli, ultrahdr_app, JxrEncApp, JxrDecApp, exiftool, avifenc |

---

## 二、迁移阻力分级

| 等级 | 描述 | 数量 |
|------|------|------|
| 🔴 **阻断级** | 不改无法编译/运行 | 3 项 |
| 🟠 **高风险** | 可编译但功能崩溃/异常 | 7 项 |
| 🟡 **中风险** | 需适配但可降级运行 | 8 项 |
| 🟢 **低风险** | 仅需配置/验证 | 5 项 |

---

## 三、阻断级问题（🔴）

### 3.1 `.csproj` 目标平台硬编码

**文件**: `src/FfmpegGui/FfmpegGui.csproj`

```xml
<!-- 当前 -->
<OutputType>WinExe</OutputType>
<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>

<!-- 需改为 -->
<OutputType>Exe</OutputType>
<RuntimeIdentifiers>linux-arm64;linux-musl-arm64;win-x64</RuntimeIdentifiers>
```

**影响**: `WinExe` 在 Linux 上无效（Linux 不需要 Windows 子系统标记），`RuntimeIdentifiers` 仅含 `win-x64` 无法发布 Linux ARM 二进制。

### 3.2 `CpuFeatureService` 直接引用 X86 硬件 intrinsic

**文件**: `src/FfmpegGui/Services/CpuFeatureService.cs`（第 23-31 行）

```csharp
// 🔴 这些类型在 Linux ARM 上不存在，会导致 TypeLoadException
try { HasSse2  = System.Runtime.Intrinsics.X86.Sse2.IsSupported; } catch { }
try { HasAvx2  = System.Runtime.Intrinsics.X86.Avx2.IsSupported; } catch { }
var asm = typeof(System.Runtime.Intrinsics.X86.Avx2).Assembly;  // 💥 直接崩溃
```

**分析**: .NET 在 ARM64 上**不包含** `System.Runtime.Intrinsics.X86` 命名空间。虽然代码有 try-catch，但 `typeof(X86.Avx2)` 在 ARM 上会直接触发 `TypeLoadException: Could not load type`，且此异常在某些 .NET 版本中无法被 catch 捕获（类型加载失败是致命的）。第 30 行的 `typeof(System.Runtime.Intrinsics.X86.Avx2).Assembly` 会在**类初始化阶段**就崩溃。

### 3.3 `ProcessPriorityClass` 在 Linux 上语义不同

**文件**: `src/FfmpegGui/Services/FfmpegRunner.cs`（第 36-41 行）

```csharp
process.PriorityClass = AppSettingsService.Current.FfmpegPriority switch
{
    0 => ProcessPriorityClass.RealTime,    // Linux 上需要 root 权限
    1 => ProcessPriorityClass.High,        // Linux 上等价 nice -11
    2 => ProcessPriorityClass.AboveNormal,  // 有效
    4 => ProcessPriorityClass.BelowNormal,
    5 => ProcessPriorityClass.Idle,
    _ => ProcessPriorityClass.Normal
};
```

**分析**: `ProcessPriorityClass.RealTime` 在 Linux 上要求 `CAP_SYS_NICE` 能力或 root 权限，非特权进程调用将抛出 `System.ComponentModel.Win32Exception`。需包装为安全降级逻辑。

---

## 四、高风险问题（🟠）

### 4.1 外部工具文件名硬编码 `.exe` 后缀（全局性问题）

遍布 **6 个 Service 文件 + MainWindow + QueueProcessor**，共 **30+ 处**硬编码 `.exe`：

| 位置 | 硬编码示例 | 出现次数 |
|------|-----------|---------|
| `AppSettings.cs` | `Path.Combine(FfmpegDirectory, "ffmpeg.exe")` | 3 |
| `CjxlService.cs` | `"cjxl.exe"`, `"*cjxl*.exe"` | 5 |
| `DjxlService.cs` | `"djxl.exe"`, `"*djxl*.exe"` | 5 |
| `CjpegliService.cs` | `"cjpegli.exe"`, `"*cjpegli*.exe"` | 5 |
| `UltrahdrService.cs` | `"ultrahdr_app.exe"` | 4 |
| `JxrService.cs` | `"JxrEncApp.exe"` | 4 |
| `ExifToolService.cs` | `"exiftool.exe"`, `"exiftool(-k).exe"` | 3 |
| `QueueProcessor.cs` | `"ffprobe.exe"`, `"ffmpeg.exe"`, `"avifenc.exe"` | 6 |
| `QualityAnalysisService.cs` | `"djxl.exe"`, `"JxrDecApp.exe"` | 4 |
| `MainWindow.xaml.cs` | FilePicker 过滤器 `"*.exe"` | 5 |

**Linux ARM 上应为**: `ffmpeg`, `ffprobe`, `cjxl`, `djxl`, `cjpegli`, `ultrahdr_app`, `JxrEncApp`, `exiftool` 等（无后缀）。

### 4.2 MainWindow FilePicker 过滤器硬编码

**文件**: `src/FfmpegGui/MainWindow.xaml.cs`

文件选择对话框设置了 `Patterns = new[] { "*.exe" }`，Linux 上可执行文件无统一扩展名，需改为无扩展名过滤或使用 MIME 类型。

### 4.3 `TryFindInPath` 对 `where`/`which` 的依赖

**文件**: 6 个 Service 文件

```csharp
// 已有的兼容逻辑 ✅
FileName = OperatingSystem.IsWindows() ? "where" : "which"
```

此部分**已正确处理**跨平台 PATH 搜索（`where` vs `which`），无需修改。但所有搜索的目标文件名仍是 `.exe` 后缀，导致 `which cjxl.exe` 在 Linux 上永远找不到（实际文件名为 `cjxl`）。

### 4.4 `QueueProcessor` 中 `ffprobe.exe` 路径拼接

**文件**: `src/FfmpegGui/Services/QueueProcessor.cs`（第 1299-1335 行）

```csharp
var ffprobePath = Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe.exe");
if (!File.Exists(ffprobePath)) ffprobePath = ffmpegPath.Replace("ffmpeg.exe", "ffprobe.exe");
```

需改为平台自适应：
```csharp
var ext = OperatingSystem.IsWindows() ? ".exe" : "";
var ffprobePath = Path.Combine(dir, $"ffprobe{ext}");
```

### 4.5 `AppSettings.FfmpegPath` / `FfprobePath` 计算属性

**文件**: `src/FfmpegGui/Models/AppSettings.cs`（第 55-65 行）

```csharp
[JsonIgnore]
public string FfmpegPath =>
    string.IsNullOrWhiteSpace(FfmpegDirectory) ? "ffmpeg"
    : Path.Combine(FfmpegDirectory, "ffmpeg.exe");  // 🔴

[JsonIgnore]
public string FfprobePath =>
    string.IsNullOrWhiteSpace(FfmpegDirectory) ? "ffprobe"
    : Path.Combine(FfmpegDirectory, "ffprobe.exe");  // 🔴
```

当 `FfmpegDirectory` 为空时走 PATH（正确）；但填充后强制附加 `.exe`。

### 4.6 `ExternalToolsDetector` 的 DLL 扫描和 `.exe` 搜索

**文件**: `src/FfmpegGui/Services/ExternalToolsDetector.cs`

```csharp
// 仅搜索 *.exe（Linux 无此概念）
foreach (var exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories))

// DLL 模式（Linux 为 .so）
var dllPatterns = new[] { "*jpegli*.dll", "*libjxl*.dll", ... };
```

### 4.7 `ExifToolService` 的 `exiftool(-k).exe` 兼容逻辑

**文件**: `src/FfmpegGui/Services/ExifToolService.cs`（第 56-58 行）

```csharp
var names = OperatingSystem.IsWindows()
    ? new[] { "exiftool.exe", "exiftool(-k).exe" }
    : new[] { "exiftool" };
```

此处的 Windows/Linux 分支**正确**，但 `ResolveSafeExifToolPath` 方法（第 103-124 行）内部仍有 `.exe` 硬编码的复制逻辑，且调用了 `File.Copy` 复制 `(-k)` 版本—此逻辑仅在 Windows 上有意义（Linux 的 exiftool 不存在 `(-k)` 版本）。

---

## 五、中风险问题（🟡）

### 5.1 外部工具 Linux ARM 编译可行性

这是迁移成功的最关键外部约束：

| 工具 | 上游支持 Linux ARM? | 编译难度 | 备注 |
|------|---------------------|---------|------|
| **ffmpeg / ffprobe** | ✅ 完全支持 | 低 | 大多数 Linux ARM 发行版提供预编译包；`apt install ffmpeg` 即可 |
| **cjxl / djxl / cjpegli** (libjxl) | ✅ 官方支持 ARM NEON | 中 | libjxl 支持 ARM NEON SIMD；需从源码 cmake 编译；AArch64 预编译二进制不常见 |
| **exiftool** | ✅ Perl 脚本，天然跨平台 | 极低 | Perl 解释器即可运行 |
| **ultrahdr_app** (libultrahdr) | ⚠️ 实验性 | **高** | Google 的 libultrahdr 主要目标平台为 Android (ARM)，桌面 Linux ARM 需自行编译；依赖 libjpeg-turbo |
| **JxrEncApp / JxrDecApp** (jxrlib) | 🔴 基本不支持 | **极高** | Microsoft jxrlib 是 Windows 优先的 C++ 库；Linux x64 编译已有社区 patch，ARM 需大量适配工作（字节序、SIMD 等） |
| **avifenc** (libavif) | ✅ 支持 | 低 | 大多数发行版有预编译包 |

**结论**: JxrEncApp/JxrDecApp 和 ultrahdr_app 是 Linux ARM 迁移的最大外部依赖阻碍。

### 5.2 `Process.Kill(entireProcessTree: true)` 平台差异

`entireProcessTree: true` 在 .NET 中用于 Windows 上终止整个进程树（通过 `TerminateProcess` + Job Object 或 `taskkill /T`）。Linux 上 .NET 会发送 `SIGTERM` 给进程组，语义类似但行为不完全一致——子进程可能不会被递归终止。

### 5.3 临时文件路径和文件权限

代码大量使用 `Path.GetTempPath()`（正确，跨平台），但 Linux 上 `/tmp` 可能被 `tmpfs` 挂载或定期清理，大文件临时操作需改用 `/var/tmp` 或自定义目录。

### 5.4 编码器优先级控件（"Windows 进程优先级"）

UI 标签和代码注释中标注为"Windows 进程优先级"，在 Linux 上：
- `RealTime` → 需 root，否则崩溃
- `Idle` → Linux 无直接等价，映射到 `nice 19`
- 需重新标注为"进程优先级"并调整内部映射

### 5.5 Windows Search 拖放解析

README 中提到"Windows Search result files correctly resolved via Shell namespace paths"—此功能依赖 Windows Shell API，在 Linux 上完全不可用（但非核心功能，可降级）。

### 5.6 管道模式的 `/tmp` 大文件风险

`JxlPipelineService` 和 `QueueProcessor` 中的管道操作消除了中间临时文件（✅ 有利于 Linux），但需要验证 Linux 上管道缓冲区大小差异（默认 64KB vs Windows 的更大缓冲区）。大量数据传输可能因管道阻塞而死锁。

### 5.7 单文件发布配置

当前 `.csproj`:
```xml
<PublishSingleFile>true</PublishSingleFile>
<SelfContained>false</SelfContained>
```

`SelfContained=false` 意味着用户必须安装 .NET 10 Runtime。Linux ARM 上 .NET 10 的可用性取决于目标发行版，某些 ARM 发行版（如 Raspberry Pi OS）可能需要从微软仓库手动安装。建议同时提供 `--self-contained` 版本。

### 5.8 `avifenc` 路径检测回退

`QueueProcessor.cs` 中 avifenc 路径检测硬编码了 `"avifenc.exe"` 搜索和 `FfmpegDir` 同目录检测，需改为平台自适应。

---

## 六、低风险问题（🟢）

### 6.1 Avalonia UI 跨平台兼容性

Avalonia 12.0.4 **原生支持 Linux**（X11 和 Wayland），且有 ARM64 运行时支持。`UsePlatformDetect()` 可自动识别。✅ 无代码修改即可运行。

### 6.2 颜色转换 / zscale 滤镜

`FfmpegCommandBuilder` 中的 HDR→SDR 色调映射使用 `zscale` 滤镜，这是 ffmpeg 内置滤镜，与平台无关。✅

### 6.3 JXL 字节级检测 (`JxlInspector`)

纯 C# 实现的文件头检测，无平台依赖。✅

### 6.4 JSON 配置序列化 (`AppSettingsService`)

使用 `System.Text.Json`，路径使用 `Path.Combine`，跨平台兼容。✅

### 6.5 元数据编辑 (`MetadataEditor`, `ExifToolService`)

exiftool 是 Perl 脚本，跨平台。✅（需确保 Perl 解释器已安装）

### 6.6 FFmpeg 编码器检测 (`EncoderDetectionService`)

通过 `ffmpeg -encoders` 输出解析，跨平台。✅

---

## 七、完整修改清单

### 7.1 项目文件修改 (`.csproj`)

| 项 | 当前值 | 目标值 |
|----|--------|--------|
| `OutputType` | `WinExe` | `Exe` |
| `RuntimeIdentifiers` | `win-x64` | `win-x64;linux-arm64;linux-musl-arm64` |
| 新增 | — | `<SelfContained>false</SelfContained>` 不变，但发布脚本需支持 `--self-contained` 可选 |

### 7.2 需要抽象的平台层

建议新建 `Services/PlatformServices.cs`：

```csharp
public static class PlatformServices
{
    public static string ExeExtension => OperatingSystem.IsWindows() ? ".exe" : "";
    public static string ToolName(string baseName) => baseName + ExeExtension;
    public static string SearchExePattern => OperatingSystem.IsWindows() ? "*.exe" : "*";
    public static string SharedLibPattern(string name) =>
        OperatingSystem.IsWindows() ? $"{name}.dll" :
        OperatingSystem.IsMacOS() ? $"lib{name}.dylib" : $"lib{name}.so";
}
```

### 7.3 文件级修改清单

| 文件 | 改动类型 | 影响范围 |
|------|---------|---------|
| `FfmpegGui.csproj` | 修改 | `OutputType`, `RuntimeIdentifiers` |
| `CpuFeatureService.cs` | **重写** | 用 `RuntimeInformation.ProcessArchitecture` 安全检测 ARM；X86 intrinstics 全部包裹 `#if NET` 条件编译或反射保护 |
| `AppSettings.cs` | 修改 | `FfmpegPath`/`FfprobePath` 使用平台后缀 |
| `CjxlService.cs` | 修改 | 所有 `.exe` 搜索替换为 `PlatformServices.ToolName()` |
| `DjxlService.cs` | 修改 | 同上 |
| `CjpegliService.cs` | 修改 | 同上 |
| `UltrahdrService.cs` | 修改 | 同上 |
| `JxrService.cs` | 修改 | 同上 |
| `ExifToolService.cs` | 修改 | 移除 `(-k).exe` Windows 特化逻辑（Linux 上保留为 no-op） |
| `ExternalToolsDetector.cs` | 修改 | 搜索模式改用 `SearchExePattern`；DLL→SO 库搜索 |
| `FfmpegRunner.cs` | 修改 | `ProcessPriorityClass.RealTime` 包装 try-catch 降级；Linux 上用 `nice` 映射 |
| `QueueProcessor.cs` | 修改 | 6 处 `.exe` 路径拼接；avifenc 路径搜索 |
| `QualityAnalysisService.cs` | 修改 | `JxrDecApp.exe` 搜索；`.exe`→平台后缀 |
| `MainWindow.xaml.cs` | 修改 | FilePicker 过滤器；标题文字 |
| `FormatCapabilitiesService.cs` | 修改 | HTTP 远端地址已 404（可清理或更新） |
| `MainWindow.xaml` | 不改 | XAML 是跨平台的 ✅ |

**预估总修改点: ~60-80 处**

### 7.4 新增/修改的文件总数

约 **12 个文件** 需修改，**1 个新文件** (`PlatformServices.cs`) 需创建。

---

## 八、外部工具策略建议

### 8.1 推荐策略：捆绑 + 自动检测

```
publish/FFmpegPictureUI-linux-arm64/
├── FFmpegPictureUI          (自包含单文件或依赖运行时)
├── tools/
│   ├── ffmpeg               (ARM64 静态链接)
│   ├── ffprobe
│   ├── cjxl                 (ARM NEON 优化)
│   ├── djxl
│   ├── cjpegli
│   └── exiftool
└── README.md
```

### 8.2 降级方案

对于无法在 Linux ARM 上运行的工具：

| 工具 | 状态 | 降级策略 |
|------|------|---------|
| `ultrahdr_app` | 🔴 困难 | 若 ffmpeg 编译了 `libultrahdr`，使用 `-c:v libultrahdr` 代替；否则隐藏 Gain Map 面板 |
| `JxrEncApp/JxrDecApp` | 🔴 极难 | 隐藏 JXR 格式选项（或标注"仅 Windows"） |
| `avifenc` | 🟢 可用 | 使用系统包管理器安装 |

### 8.3 ffmpeg 的 libjxl 集成

如果目标 Linux ARM 上的 ffmpeg 编译时包含 `--enable-libjxl`，则 cjxl/djxl 可作为可选加速工具（类似当前 Windows 逻辑：有则优先，无则使用 ffmpeg 内置 `libjxl` 编码器）。

---

## 九、CPU 特性检测重写方案

### 当前问题

`CpuFeatureService.cs` 直接使用 `System.Runtime.Intrinsics.X86.*`，在 ARM 上会类型加载失败。

### 推荐方案

```csharp
public static class CpuFeatureService
{
    public static bool HasAvx2 { get; private set; }
    public static bool HasNeon { get; private set; }
    public static bool IsArm64 { get; private set; }

    public static void Detect()
    {
        IsArm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        if (IsArm64)
        {
            // ARM64 intrinsic 安全检测
            try { HasNeon = System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported; } catch { }
            // X86 属性全部 false，不尝试访问 X86 类型
            HasAvx2 = false; // etc.
        }
        else
        {
            // X86/X64 intrinsic 安全检测（仅在此分支访问 X86 类型）
            try { HasAvx2 = System.Runtime.Intrinsics.X86.Avx2.IsSupported; } catch { }
            try { HasNeon = false; } catch { }
            // ...
        }
    }
}
```

要点：**永远不要**在 ARM64 进程中使用 `typeof(X86.*)` 或访问 `X86.*` 命名空间的任何成员。

---

## 十、迁移工作量估算

| 阶段 | 工作内容 | 预估工时 |
|------|---------|---------|
| **阶段1: 编译通过** | .csproj 修改、CPU 检测重写、.exe 平台抽象化 | 2-3 天 |
| **阶段2: 功能验证** | Linux ARM 环境搭建、所有编码路径测试 | 3-5 天 |
| **阶段3: 外部工具编译** | 交叉编译 ffmpeg/libjxl/exiftool 等 | 3-7 天（取决于经验） |
| **阶段4: 降级适配** | JXR/UltraHDR 降级逻辑、UI 动态隐藏 | 2-3 天 |
| **阶段5: 测试与发布** | 集成测试、性能对比、打包发布 | 2-3 天 |

**总计预估: 12-21 人天**（取决于开发者对 Linux ARM 生态的熟悉程度和外部工具编译顺利度）。

---

## 十一、总结

**主要结论**: FFmpegPictureUI 迁移到 Linux ARM **技术上完全可行**。核心 UI 框架 (Avalonia) 原生跨平台，.NET 10 对 ARM64 支持成熟。主要迁移工作量集中在三个方面：

1. **消除 Windows 特化代码**（~60-80 处 `.exe` 硬编码、进程优先级、FilePicker 等）
2. **修复 ARM 不兼容的运行时检测**（CPU intrinstics 类型加载崩溃）
3. **外部工具链适配**（JxrEncApp/ultrahdr_app 可能需要降级或隐藏）

建议采用"功能降级"策略：首先确保核心转换功能（ffmpeg + libjxl + exiftool）在 Linux ARM 上 100% 可用，JXR 和 Ultra HDR 作为可选增强在不可用时自动隐藏 UI 面板，保证用户体验不退化。
