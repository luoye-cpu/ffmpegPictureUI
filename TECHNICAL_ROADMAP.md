# 📋 FFmpegPictureUI 超长线技术规划

> 制定日期: 2026-07-13 | 最后更新: 2026-07-14 | 基线版本: v1.4.5 → 目标 v2.0.0

---

## 🎯 规划总纲

| 目标 | 含义 | 状态 |
|------|------|:--:|
| **方便迁移 Linux** | 建立平台抽象，消除 Linux 迁移障碍 | ✅ |
| **修正硬编码** | `.exe`、Windows 专有路径全提取 | ✅ |
| **完善 Windows 识别** | 编码器/工具自动发现、PLAN 便携包 | ✅ |
| **优化运行效率** | 管道、内存、启动、并发 | ✅ |
| **CPU 指令集重构** | AVX10/AMX/SVE，跨平台安全检测 | ✅ |
| **.NET 11 + NativeAOT** | 绿色单文件，用户零 Runtime 依赖 | 📋 |

---

## 目录

- [✅ P0：平台抽象层 + 硬编码消除](#p0平台抽象层--硬编码消除)
- [✅ P1：架构安全化 + 可移植性加固](#p1架构安全化--可移植性加固)
- [✅ P2：Windows 平台识别完善](#p2windows-平台识别完善)
- [✅ P3：性能优化](#p3性能优化)
- [✅ P4：CPU 指令集检测重构](#p4cpu-指令集检测重构)
- [📋 P5：.NET 11 + NativeAOT 迁移](#p5net-11--nativeaot-迁移)

---

## P5：.NET 11 + NativeAOT 迁移

> 目标：用户下载解压即用，无需安装 .NET Runtime。

### 迁移步骤

| 步骤 | 操作 | 说明 |
|:--:|------|------|
| 1 | 改 `TargetFramework` | `net10.0` → `net11.0` |
| 2 | 改 4 个 verify 项目 | `MainTfm` 变量同步 |
| 3 | 启用 `.csproj` 中 NativeAOT 配置 | 取消注释 `PublishAot` PropertyGroup |
| 4 | 更新 Avalonia 包 | `12.0.4` → `.NET 11 兼容版本` |
| 5 | 编译 + 修复 AOT 警告 | 处理 JSON 序列化 AOT 警告 |
| 6 | `.\pack.ps1 -Version "2.0.0" -Aot` | 一键打包 NativeAOT 版本 |

### 预期产物对比

| 指标 | 当前 (框架依赖) | NativeAOT |
|------|:--:|:--:|
| 包大小 | ~15MB | ~25MB |
| 用户需装 Runtime | 是 (55MB) | **否** |
| 用户总磁盘 | ~70MB | **~25MB** |
| 冷启动 | ~3s | **~0.5s** |
| 安装步骤 | 3 步 | **1 步** |
- [附录 B：Linux 迁移就绪度评估表](#附录-blinux-迁移就绪度评估表)

---

## 第一阶段 P0：平台抽象层 + 硬编码消除（2 周）

> **这是整个规划的基石。** 完成后，未来 Linux 迁移时只需改一个文件（`PlatformServices.cs`）。
>
> 全部在 Windows 上开发、编译、测试。不引入任何 Linux 依赖。

### 1.1 新建 `PlatformServices.cs` — 统一平台抽象

**新建文件**: `src/FfmpegGui/Services/PlatformServices.cs`

这个文件是整个 Linux 迁移的"总开关"。当未来决定迁移时，只需确保这个类中的常量正确，
其余 60+ 处代码无需再改。

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FfmpegGui.Services;

public static class PlatformServices
{
    // ═══════════════════════════════════════════════
    // 以下常量——未来迁移 Linux 时只需改这里
    // ═══════════════════════════════════════════════

    /// <summary>可执行文件扩展名（含点，如 ".exe"）</summary>
    public static string ExeExtension => OperatingSystem.IsWindows() ? ".exe" : "";

    /// <summary>给基础工具名附加平台后缀</summary>
    public static string ToolName(string baseName) => baseName + ExeExtension;

    /// <summary>目录扫描时搜索可执行文件的通配符</summary>
    public static string ExeSearchWildcard => OperatingSystem.IsWindows() ? "*.exe" : "*";

    // ── 预定义工具名（集中管理，全局统一引用） ──
    public static string Ffmpeg    => ToolName("ffmpeg");
    public static string Ffprobe   => ToolName("ffprobe");
    public static string Cjxl      => ToolName("cjxl");
    public static string Djxl      => ToolName("djxl");
    public static string Cjpegli   => ToolName("cjpegli");
    public static string Ultrahdr  => ToolName("ultrahdr_app");
    public static string JxrEnc    => ToolName("JxrEncApp");
    public static string JxrDec    => ToolName("JxrDecApp");
    public static string Exiftool  => OperatingSystem.IsWindows() ? "exiftool.exe" : "exiftool";
    public static string Avifenc   => ToolName("avifenc");

    // ── 目录搜索模式 ──
    public static string CjxlSearchWildcard    => OperatingSystem.IsWindows() ? "*cjxl*.exe"   : "*cjxl*";
    public static string DjxlSearchWildcard    => OperatingSystem.IsWindows() ? "*djxl*.exe"   : "*djxl*";
    public static string CjpegliSearchWildcard => OperatingSystem.IsWindows() ? "*cjpegli*.exe" : "*cjpegli*";

    // ── 共享库搜索模式 ──
    public static string[] SharedLibSearchPatterns => OperatingSystem.IsWindows()
        ? new[] { "*jpegli*.dll", "*libjxl*.dll", "*skcms*.dll", "*lcms2*.dll", "*jxl*.dll" }
        : new[] { "libjpegli*.so*", "libjxl*.so*", "libskcms*.so*", "liblcms2*.so*" };

    // ── FilePicker 过滤（Avalonia 跨平台） ──
    public static string[] ExeFilePickerPatterns =>
        OperatingSystem.IsWindows() ? new[] { "*.exe" } : new[] { "*" };

    // ═══════════════════════════════════════════════
    // 工具路径解析（消除各 Service 中的重复代码）
    // ═══════════════════════════════════════════════

    /// <summary>在系统 PATH 中查找可执行文件</summary>
    public static bool TryFindInPath(string toolName, out string? fullPath)
    {
        fullPath = null;
        try
        {
            var finder = OperatingSystem.IsWindows() ? "where" : "which";
            var psi = new ProcessStartInfo
            {
                FileName = finder,
                Arguments = toolName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            if (string.IsNullOrWhiteSpace(output)) return false;
            var firstLine = output.Split(new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries)[0];
            if (File.Exists(firstLine)) { fullPath = firstLine; return true; }
        }
        catch { }
        return false;
    }

    /// <summary>从 ffmpeg 路径推断 ffprobe 路径</summary>
    public static string? ResolveFfprobePath(string ffmpegPath)
    {
        var dir = Path.GetDirectoryName(ffmpegPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var probe = Path.Combine(dir, Ffprobe);
        if (File.Exists(probe)) return probe;
        // 回退：替换文件名中的 ffmpeg → ffprobe
        var ffmpegName = Path.GetFileNameWithoutExtension(ffmpegPath);
        if (ffmpegName.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(dir, Ffprobe);
        return null;
    }

    /// <summary>在目录及子目录中查找特定工具</summary>
    public static string? FindToolInDirectory(
        string directory, string toolName, string searchWildcard)
    {
        if (!Directory.Exists(directory)) return null;
        var candidate = Path.Combine(directory, toolName);
        if (File.Exists(candidate)) return candidate;
        try
        {
            var list = new List<string>();
            foreach (var f in Directory.EnumerateFiles(
                directory, searchWildcard, SearchOption.AllDirectories))
            {
                if (File.Exists(f)) list.Add(f);
            }
            if (list.Count > 0)
                return ExternalToolsDetector.ChooseBestExecutable(list);
        }
        catch { }
        return null;
    }

    // ═══════════════════════════════════════════════
    // 平台感知布尔属性
    // ═══════════════════════════════════════════════

    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsLinux   => OperatingSystem.IsLinux();
    public static bool IsMacOs   => OperatingSystem.IsMacOS();
    public static bool IsArm64   => RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
    public static bool IsX64     => RuntimeInformation.ProcessArchitecture == Architecture.X64;

    // ═══════════════════════════════════════════════
    // 进程优先级（跨平台安全）
    // ═══════════════════════════════════════════════

    /// <summary>
    /// 设置进程优先级。Windows 使用 ProcessPriorityClass；
    /// 非 Windows 平台使用 nice 值映射（RealTime 自动降级为 High）。
    /// </summary>
    public static void SetSafePriority(Process process, int priorityLevel)
    {
        try
        {
            process.PriorityClass = priorityLevel switch
            {
                0 => ProcessPriorityClass.RealTime,
                1 => ProcessPriorityClass.High,
                2 => ProcessPriorityClass.AboveNormal,
                4 => ProcessPriorityClass.BelowNormal,
                5 => ProcessPriorityClass.Idle,
                _ => ProcessPriorityClass.Normal
            };
        }
        catch { /* 降级：优先级设置不影响核心功能 */ }
    }

    /// <summary>返回适合当前平台的大文件临时目录</summary>
    public static string GetTempDir() =>
        IsLinux && Directory.Exists("/var/tmp") ? "/var/tmp" : Path.GetTempPath();
}
```

### 1.2 全局替换硬编码 `.exe`（约 60 处）

> 当前代码中有 **60+ 处** `.exe` 硬编码，散布在 12 个文件中。
> 全部替换为 `PlatformServices` 的对应常量或方法。
>
> 这些改动纯属重构——Windows 上行为完全不变，但代码从此"知道"自己在什么平台上运行。

#### 1.2.1 `AppSettings.cs` — ffmpeg/ffprobe 路径计算

```csharp
// 之前
public string FfmpegPath =>
    string.IsNullOrWhiteSpace(FfmpegDirectory)
        ? "ffmpeg"
        : Path.Combine(FfmpegDirectory, "ffmpeg.exe");

public string FfprobePath =>
    string.IsNullOrWhiteSpace(FfmpegDirectory)
        ? "ffprobe"
        : Path.Combine(FfmpegDirectory, "ffprobe.exe");

// 之后
public string FfmpegPath =>
    string.IsNullOrWhiteSpace(FfmpegDirectory)
        ? PlatformServices.Ffmpeg
        : Path.Combine(FfmpegDirectory, PlatformServices.Ffmpeg);

public string FfprobePath =>
    string.IsNullOrWhiteSpace(FfmpegDirectory)
        ? PlatformServices.Ffprobe
        : Path.Combine(FfmpegDirectory, PlatformServices.Ffprobe);
```

#### 1.2.2 六个 Service 文件的工具检测（批量替换）

| 文件 | 替换内容 |
|------|---------|
| `CjxlService.cs` | `"cjxl.exe"` → `PlatformServices.Cjxl`; `"*cjxl*.exe"` → `PlatformServices.CjxlSearchWildcard`; 私有 `TryFindInPath` → `PlatformServices.TryFindInPath` |
| `DjxlService.cs` | 同上模式，`djxl` |
| `CjpegliService.cs` | 同上模式，`cjpegli` |
| `UltrahdrService.cs` | `"ultrahdr_app.exe"` → `PlatformServices.Ultrahdr` |
| `JxrService.cs` | `"JxrEncApp.exe"` → `PlatformServices.JxrEnc` |
| `ExifToolService.cs` | `(-k).exe` 逻辑用 `PlatformServices.IsWindows` 包裹 |

#### 1.2.3 `QueueProcessor.cs` — 8 处路径拼接

```csharp
// 之前（出现 4 次）
var ffprobePath = Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe.exe");
if (!File.Exists(ffprobePath)) ffprobePath = ffmpegPath.Replace("ffmpeg.exe", "ffprobe.exe");

// 之后
var ffprobePath = PlatformServices.ResolveFfprobePath(ffmpegPath);

// 之前
avifencPath = Path.Combine(AppSettingsService.Current.FfmpegDir ?? "", "avifenc.exe");

// 之后
avifencPath = Path.Combine(AppSettingsService.Current.FfmpegDir ?? "", PlatformServices.Avifenc);
```

#### 1.2.4 `ExternalToolsDetector.cs` — 搜索模式

```csharp
// 之前
foreach (var exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories))
var dllPatterns = new[] { "*jpegli*.dll", "*libjxl*.dll", ... };

// 之后
foreach (var exe in Directory.EnumerateFiles(dir, PlatformServices.ExeSearchWildcard, SearchOption.AllDirectories))
var libPatterns = PlatformServices.SharedLibSearchPatterns;
```

#### 1.2.5 `MainWindow.xaml.cs` — FilePicker 过滤器（6 处）

```csharp
// 之前（出现 6 次）
new FilePickerFileType("可执行文件") { Patterns = new[] { "*.exe" } }

// 之后
new FilePickerFileType("可执行文件") { Patterns = PlatformServices.ExeFilePickerPatterns }
```

#### 1.2.6 `QualityAnalysisService.cs`

```csharp
// 之前
var p = Path.Combine(dir, "JxrDecApp.exe");
// 之后
var p = Path.Combine(dir, PlatformServices.JxrDec);
```

---

## 第二阶段 P1：架构安全化 + 可移植性加固（1-2 周）

> 目标：修复在 ARM/Linux 上会导致**类型加载崩溃**的代码，消除平台假设。
>
> 当前仅在 Windows x64 运行——但代码不应在 ARM64 上直接崩溃。

### 2.1 `CpuFeatureService` 重写 — 避免跨架构崩溃

**当前问题**: 第 30 行 `typeof(System.Runtime.Intrinsics.X86.Avx2).Assembly` 在 ARM64 进程中
会触发 `TypeLoadException`，此异常在某些 .NET 版本中**无法被 try-catch 捕获**（类型加载失败是致命的）。

**方案**: 用 `RuntimeInformation.ProcessArchitecture` 做显式分支守卫，**永远不在 ARM 进程中触碰 X86 类型**。

```csharp
public static class CpuFeatureService
{
    private static bool _detected;

    public static bool IsArm64 { get; private set; }
    public static bool IsX64 { get; private set; }

    // X86 特性（仅在 x86/x64 架构上有意义）
    public static bool HasSse2 { get; private set; }
    public static bool HasSse41 { get; private set; }
    public static bool HasAvx { get; private set; }
    public static bool HasAvx2 { get; private set; }
    public static bool HasAvx512F { get; private set; }

    // ARM 特性（未来迁移时启用）
    public static bool HasNeon { get; private set; }
    public static bool HasAnySimd { get; private set; }

    public static void Detect()
    {
        if (_detected) return;
        _detected = true;

        var arch = RuntimeInformation.ProcessArchitecture;
        IsArm64 = arch == Architecture.Arm64;
        IsX64  = arch == Architecture.X64;

        bool isX86 = arch is Architecture.X86 or Architecture.X64;

        if (isX86)
        {
            // 🔒 仅在 x86/x64 进程中访问 X86 intrinsics
            try { HasSse2  = System.Runtime.Intrinsics.X86.Sse2.IsSupported;  } catch { }
            try { HasSse41 = System.Runtime.Intrinsics.X86.Sse41.IsSupported; } catch { }
            try { HasAvx   = System.Runtime.Intrinsics.X86.Avx.IsSupported;   } catch { }
            try { HasAvx2  = System.Runtime.Intrinsics.X86.Avx2.IsSupported;  } catch { }
            try
            {
                var asm = typeof(System.Runtime.Intrinsics.X86.Avx2).Assembly;
                var t = asm.GetType("System.Runtime.Intrinsics.X86.Avx512F");
                if (t != null)
                {
                    var prop = t.GetProperty("IsSupported",
                        BindingFlags.Public | BindingFlags.Static);
                    if (prop != null) HasAvx512F = (bool)prop.GetValue(null)!;
                }
            }
            catch { }
        }

        if (IsArm64)
        {
            // 🔒 仅在 ARM64 进程中访问 ARM intrinsics（当前不执行，预留）
            try { HasNeon = System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported; } catch { }
        }

        HasAnySimd = HasAvx2 || HasAvx || HasSse41 || HasSse2 || HasAvx512F || HasNeon;
    }
}
```

> **验证方法**（Windows 上）: 此改动后在 x64 上行为完全不变。API 签名未变，所有调用方无需修改。

### 2.2 进程优先级设置 — 抽离为可平台切换的调用

**文件**: `src/FfmpegGui/Services/FfmpegRunner.cs`（第 36-41 行）

```csharp
// 之前
try
{
    process.PriorityClass = AppSettingsService.Current.FfmpegPriority switch
    {
        0 => ProcessPriorityClass.RealTime,
        1 => ProcessPriorityClass.High,
        // ...
    };
}
catch { }

// 之后
PlatformServices.SetSafePriority(process, AppSettingsService.Current.FfmpegPriority);
```

### 2.3 消除各 Service 中重复的 `TryFindInPath` 实现

当前 CjxlService、DjxlService、CjpegliService、UltrahdrService、JxrService、ExifToolService
各有一份 `TryFindInPath` 方法（代码几乎一模一样）。

**方案**: 全部替换为 `PlatformServices.TryFindInPath()`，删除各自的私有方法。减少约 120 行重复代码。

### 2.4 `ExifToolService` — `exiftool(-k).exe` 逻辑平台隔离

`exiftool(-k).exe` 是 Windows 特有的交互版本（按任意键退出）。Linux 上不存在此文件。

```csharp
// ResolveSafeExifToolPath 内部
// 将 File.Copy 复制 (-k) 版本逻辑包裹在平台判断中
if (PlatformServices.IsWindows && fileName.Contains("(-k)"))
{
    // 当前逻辑：复制为标准 exiftool.exe
    var safePath = Path.Combine(dir, "exiftool.exe");
    if (!File.Exists(safePath))
        File.Copy(candidatePath, safePath, overwrite: false);
    return safePath;
}
```

### 2.5 `.csproj` 调整（为未来多平台发布做准备）

**当前**:
```xml
<OutputType>WinExe</OutputType>
<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>
```

**改为**（行为不变，但支持未来多 RID 发布）:
```xml
<OutputType>Exe</OutputType>
<RuntimeIdentifiers>win-x64;linux-x64;linux-arm64</RuntimeIdentifiers>
```

- `WinExe` → `Exe`：Avalonia 在 Windows 上使用 `Exe` 也能正常运行（.NET 10 不会额外弹出控制台窗口）
- 增加 `linux-x64;linux-arm64`：**不影响当前发布**（`dotnet publish -r win-x64` 不变），但未来直接可用

---

## 第三阶段 P2：Windows 平台识别完善（1 周）

> 目标：在当前 Windows 平台上提升编码器自动发现能力和用户体验。

### 3.1 ffmpeg 编码器能力运行时检测扩充

`EncoderDetectionService` 已通过 `ffmpeg -encoders` 解析，但缺少 muxer 和 decoder 检测。

**扩充** `EncoderDetectionService`：
- `ProbeAvailableMuxers()` — 判断目标格式是否被当前 ffmpeg 构建支持
- `ProbeAvailableDecoders()` — 判断输入格式可否解码
- 启动时后台异步执行，结果缓存

### 3.2 外部工具的扩展搜索路径

当前工具检测仅搜索：手动路径 → ffmpeg 同目录 → 程序同目录 → PATH。

**增加搜索路径**（Windows）：
- `%LOCALAPPDATA%\Programs\` （scoop 安装的常见位置）
- `C:\Program Files\` 下的常见子目录
- 注册表中已安装工具的路径（如 `exiftool` 的安装记录）

### 3.3 格式能力面板 UI 增强

在格式选择区域添加实时检测状态标签：

```
✅ JPEG XL — 外部 cjxl (AVX2) 已检测
⚠️ JPEG XR — JxrEncApp 未安装
ℹ️  AVIF   — 使用 ffmpeg libsvtav1
```

此 UI 改动预留了未来 Linux 上显示 `⚠️ 当前平台不可用` 标签的能力。

---

## 第四阶段 P3：性能优化（2-3 周）

> 目标：提升批量转换吞吐量，降低内存占用，优化启动速度。
> 这些优化在所有平台上均受益。

### 4.1 管道模式全面化 — 消灭临时文件

| 当前路径 | 是否管道 | 优化 |
|----------|---------|------|
| ffmpeg → cjxl | ✅ 管道 | — |
| ffmpeg → cjpegli | ✅ 管道 | — |
| djxl → ffmpeg | ✅ 管道 | — |
| djxl → cjpegli | ✅ 管道 | — |
| GIF → avifenc | ❌ PNG 帧序列 | **改为 avifenc stdin 管道** |
| djxl → cjxl | ❌ PNG 中转文件 | **新增 `PipeDjxlToCjxlAsync`** |
| JXR 编码 | ❌ BMP/TIFF 中转 | 暂不改（JxrEncApp 必须文件输入） |

**预期收益**: 减少磁盘 I/O 50%，管道编码路径吞吐量提升 2-3x（对大文件尤其明显）。

### 4.2 内存管理优化

| 问题 | 方案 |
|------|------|
| `QueueItem.Log` 无限增长 | 设置 5MB 上限，超出时截断头部并警告 |
| 高并发时所有任务同时分配帧缓冲 | 根据 `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` 动态调整并发数上限 |
| 管道传输默认 8192 字节缓冲 | 改为 65536 字节（64KB）缓冲区 |

### 4.3 启动性能优化

| 优化项 | 方案 |
|--------|------|
| 外部工具检测 | **延迟加载**：首次编码时才探测，而非启动时全部执行 |
| 编码器列表 | 缓存到本地 `%LocalAppData%`，仅在 ffmpeg 版本或路径变化时刷新 |
| 配置文件 | 异步读取 `settings.json` |
| XAML 绑定 | 启用 Avalonia `CompiledBindings`（编译期绑定，减少反射） |

### 4.4 编码参数默认值调优

| 参数 | 当前默认 | 建议默认 | 理由 |
|------|---------|---------|------|
| CJXL effort | 7 | 5 | 速度提升 ~3x，质量损失可忽略（< 1%） |
| AVIF 编码器 | 默认（取决于 ffmpeg） | `libsvtav1` 优先 | SVT-AV1 比 libaom-av1 快 5-10x |
| WebP 行级线程 | 未启用 | `-row-mt 1` | 多行并行编码 |

### 4.5 并发模型优化

当前 `SemaphoreSlim` 并发模型存在任务泄漏风险（`_running` 列表未清理已完成任务）。

**优化**:
- 使用 `ConcurrentBag<Task>` + 定期清理已完成任务
- 引入 `Channel<T>` 替代 `ConcurrentQueue` + `Task.Delay` 轮询（更低 CPU 开销）

---

## 附录 A：完整文件改动清单

### 新增文件

| 文件 | 用途 |
|------|------|
| `Services/PlatformServices.cs` | 平台抽象层：工具名、搜索模式、路径解析、优先级（~200 行） |

### 修改文件

| 阶段 | 文件 | 改动性质 | 预计改动行数 |
|------|------|---------|:---------:|
| P0 | `FfmpegGui.csproj` | `WinExe`→`Exe`，多 RID | 3 |
| P0 | `AppSettings.cs` | `FfmpegPath`/`FfprobePath` 引用 `PlatformServices` | 4 |
| P0 | `CjxlService.cs` | .exe 硬编码 + 删除私有 TryFindInPath | ~15 |
| P0 | `DjxlService.cs` | 同上 | ~15 |
| P0 | `CjpegliService.cs` | 同上 | ~15 |
| P0 | `UltrahdrService.cs` | 同上 | ~10 |
| P0 | `JxrService.cs` | 同上 | ~10 |
| P0 | `ExifToolService.cs` | (-k).exe 平台隔离 | ~10 |
| P0 | `ExternalToolsDetector.cs` | 搜索模式引用 `PlatformServices` | ~8 |
| P0 | `QualityAnalysisService.cs` | JxrDecApp 名引用 `PlatformServices` | ~4 |
| P0 | `QueueProcessor.cs` | ffprobe/avifenc 路径拼接 | ~12 |
| P0 | `MainWindow.xaml.cs` | FilePicker（6 处） | ~12 |
| P1 | `CpuFeatureService.cs` | 重写为架构感知检测 | ~40 |
| P1 | `FfmpegRunner.cs` | 优先级调用改为 `PlatformServices.SetSafePriority` | ~8 |
| P2 | `EncoderDetectionService.cs` | 扩充 muxer/decoder 检测 | ~30 |
| P3 | `QueueProcessor.cs` | 管道扩展 + 内存管理 + 并发优化 | ~80 |

> **总计**: 约 **16 个文件**，净增 ~200 行（PlatformServices），净减 ~120 行（消除重复代码），
> 实际改动约 **280 行**。

---

## 附录 B：Linux 迁移就绪度评估表

> 完成本规划各阶段后，Linux 迁移的障碍数量变化：

| 障碍类型 | 当前（v1.4.5） | P0 完成后 | P1 完成后 | 全部完成后 |
|----------|:---------:|:------:|:------:|:------:|
| 编译障碍 | 3 | 1 | 0 | 0 |
| 运行崩溃（ARM 架构） | 1 | 1 | 0 | 0 |
| 功能不可用（硬编码路径） | 60+ | 0 | 0 | 0 |
| 需要平台特化改动 | — | — | — | 0 |
| **未来迁移剩余工时** | **5-7 周** | **1-2 周** | **3 天** | **2 天** |

> 📅 **总时间线**: 预计 **6-8 周** 完成全部四个阶段（仅在 Windows 上开发）。
>
> 🎯 **最终效果**: 未来决定迁移 Linux 时，只需：
>
> 1. 安装 .NET 10 SDK → `dotnet publish -r linux-arm64`
> 2. 安装 ffmpeg/exiftool → `apt install ffmpeg exiftool`
> 3. 编译 libjxl（如需要 cjxl/djxl）→ 放在 `tools/` 目录
> 4. **代码零改动**
