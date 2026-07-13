# 外部工具管理重构规划 v2.0

> 日期: 2026-07-14 | 基线: v1.5.0 | 目标: 三选项简化 + PLAN 精简

---

## 一、现状分析

### 1.1 当前设置项（7 个独立字段）

| AppSettings 字段 | 绑定 Service | PLAN 子目录 |
|:--|:--|:--|
| `FfmpegDirectory` | ffmpeg / ffprobe | `PLAN/ffmpeg-full/` |
| `CjxlPath` | CjxlService + DjxlService | `PLAN/jxl-*-static/bin/` |
| `CjpegliPath` | CjpegliService | 同上 |
| `ExifToolPath` | ExifToolService | `PLAN/exiftool-*/` |
| `AvifencPath` | QueueProcessor 内联 | `PLAN/windows-artifacts/` |
| `UltrahdrPath` | UltrahdrService | 同上 |
| `JxrPath` | JxrService | 同上 |

### 1.2 问题

- **UI 冗余**: 7 个输入框 + 14 个按钮，占用大量界面空间
- **命名混乱**: `CjxlPath` 实际也管 djxl，`CjpegliPath` 实际与 cjxl 同目录
- **PLAN 映射脆弱**: 子目录名硬编码版本号（`exiftool-13.58_64`、`jxl-x64-windows-static`）
- **扩展性差**: 每新增一个工具就要加字段、加 Service、加 UI

---

## 二、目标架构

### 2.1 新设置项（3 个 + ffmpeg 保留）

| 设置字段 | 类型 | 含义 |
|:--|:--|:--|
| `FfmpegDirectory` | 目录 | ffmpeg / ffprobe（不变） |
| `ExifToolPath` | **文件** | exiftool(.exe) 可执行文件路径 |
| `JxlLibDir` | **目录** | JPEG XL 参考库目录（含 cjxl/djxl/cjpegli） |
| `WindowsArtifactsDir` | **目录** | Windows 构建产物目录（含 ultrahdr/JxrEnDec/avifenc/aomenc） |

> `AvifencPath`、`UltrahdrPath`、`JxrPath`、`CjxlPath`、`CjpegliPath` **全部废弃**。

### 2.2 PLAN 目录简化

```
PLAN/                          ← 程序自动识别
├── ffmpeg-full/               ← ffmpeg/ffprobe
├── jxl/                       ← cjxl.exe, djxl.exe, cjpegli.exe (扁平化)
├── exiftool/                  ← exiftool.exe, exiftool(-k).exe
└── artifacts/                 ← 全部 Windows 构建产物
    ├── ultrahdr_app.exe
    ├── JxrEncApp.exe
    ├── JxrDecApp.exe
    ├── avifenc.exe
    ├── avifdec.exe
    ├── aomenc.exe
    └── aomdec.exe
```

> 旧版 `jxl-x64-windows-static/bin/`、`exiftool-13.58_64/exiftool-13.58_64/` 目录在迁移后删除。

---

## 三、AppSettings 模型变更

```csharp
public class AppSettings
{
    // ── 保留 ──
    public string? FfmpegDirectory { get; set; }

    // ── 变更：目录 → 文件 ──
    public string? ExifToolPath { get; set; }        // 指向 exiftool.exe 文件

    // ── 新增：替代 CjxlPath + CjpegliPath ──
    public string? JxlLibDir { get; set; }           // JPEG XL 参考库目录

    // ── 新增：替代 AvifencPath + UltrahdrPath + JxrPath ──
    public string? WindowsArtifactsDir { get; set; } // Windows 构建产物目录

    // ── 废弃（保留字段兼容旧配置，标记 [Obsolete]）──
    [Obsolete("使用 JxlLibDir 替代")]
    public string? CjxlPath { get; set; }
    [Obsolete("使用 JxlLibDir 替代")]
    public string? CjpegliPath { get; set; }
    [Obsolete("使用 WindowsArtifactsDir 替代")]
    public string? AvifencPath { get; set; }
    [Obsolete("使用 WindowsArtifactsDir 替代")]
    public string? UltrahdrPath { get; set; }
    [Obsolete("使用 WindowsArtifactsDir 替代")]
    public string? JxrPath { get; set; }

    // ... 其余字段不变 ...
}
```

---

## 四、GUI 变更

### 4.1 工具栏简化

```
┌─────────────────────────────────────────────────────────────┐
│ FFmpeg 目录  [____________] [浏览]   输出目录 [____________]  │
│                                                             │
│ exiftool     [____________] [浏览✎]  ← 选 exe 文件           │
│ jxl 参考库    [____________] [浏览📁]  ← 选文件夹              │
│ artifacts    [____________] [浏览📁]  ← 选文件夹              │
│                                          [🔄 重新检测]        │
└─────────────────────────────────────────────────────────────┘
```

### 4.2 XAML 变更（伪代码）

```xml
<!-- Row 1: exiftool (文件选择) -->
<StackPanel Orientation="Horizontal">
  <TextBlock Text="exiftool" Width="72"/>
  <TextBox x:Name="ExifToolPathBox" Width="240" IsReadOnly="True"/>
  <Button Content="浏览..." Click="BrowseExifTool_Click"/>
  <Button Content="✕" Click="ClearExifToolPath_Click"/>
</StackPanel>

<!-- Row 2: jxl 参考库 (文件夹选择) -->
<StackPanel Orientation="Horizontal">
  <TextBlock Text="jxl 参考库" Width="72"/>
  <TextBox x:Name="JxlLibDirBox" Width="240" IsReadOnly="True"/>
  <Button Content="浏览..." Click="BrowseJxlLibDir_Click"/>
  <Button Content="✕" Click="ClearJxlLibDir_Click"/>
  <!-- 检测状态标签 -->
  <TextBlock x:Name="JxlLibStatus" Text=""/>
</StackPanel>

<!-- Row 3: artifacts (文件夹选择) -->
<StackPanel Orientation="Horizontal">
  <TextBlock Text="artifacts" Width="72"/>
  <TextBox x:Name="ArtifactsDirBox" Width="240" IsReadOnly="True"/>
  <Button Content="浏览..." Click="BrowseArtifactsDir_Click"/>
  <Button Content="✕" Click="ClearArtifactsDir_Click"/>
  <TextBlock x:Name="ArtifactsStatus" Text=""/>
</StackPanel>
```

### 4.3 文件夹选择后的实时校验

```
用户选择 jxl 参考库目录 → 立即扫描：
  ✅ cjxl.exe (v0.11.2, AVX2)
  ✅ djxl.exe (v0.11.2, AVX2)
  ✅ cjpegli.exe (v0.11.2)
  ❌ 未找到 djxl → 状态标签显示 "⚠️ 缺少 djxl.exe"

用户选择 artifacts 目录 → 立即扫描：
  ✅ ultrahdr_app.exe (v1.4.0)
  ✅ JxrEncApp.exe
  ✅ avifenc.exe (v1.4.2)
  ⚠️ JxrDecApp.exe 缺失（不影响编码，仅质量分析不可用）
```

---

## 五、后端检测逻辑

### 5.1 统一检测优先级（所有 Service 共用）

```
① 用户手动指定 (AppSettings 字段)
② PLAN 自动检测 (程序同目录/PLAN/)
③ ffmpeg 同目录 / 程序同目录
④ 扩展搜索路径 (LocalAppData, Program Files)
⑤ 系统 PATH
```

### 5.2 Service 变更

| Service | 旧行为 | 新行为 |
|:--|:--|:--|
| `CjxlService` | 读 `CjxlPath`/`CjpegliPath` | 读 `JxlLibDir` |
| `DjxlService` | 读 `CjxlPath`/`CjpegliPath` | 读 `JxlLibDir` |
| `CjpegliService` | 读 `CjpegliPath`/`CjxlPath` | 读 `JxlLibDir` |
| `UltrahdrService` | 读 `UltrahdrPath` | 读 `WindowsArtifactsDir` |
| `JxrService` | 读 `JxrPath` | 读 `WindowsArtifactsDir` |
| `ExifToolService` | 读 `ExifToolPath`（目录） | 读 `ExifToolPath`（**文件**） |
| avifenc（内联） | 读 `AvifencPath` | 读 `WindowsArtifactsDir` |

### 5.3 CjxlService.Detect() 伪代码

```csharp
public static void Detect()
{
    // ① 手动指定 (JxlLibDir)
    var dir = AppSettingsService.Current.JxlLibDir;
    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
    {
        var cjxl = FindInDir(dir, "cjxl");
        if (cjxl != null) { _detectedPath = cjxl; return; }
    }

    // ①alt 兼容旧设置 (CjxlPath / CjpegliPath)
    dir = MigrateLegacyPath();
    if (dir != null) { /* 同上 */ }

    // ② PLAN
    var plan = TryFindInPlanFolder("cjxl");
    if (plan != null) { _detectedPath = plan; return; }

    // ③ 同目录
    // ④ 扩展搜索
    // ⑤ PATH
    // ... (不变)
}

// 旧设置自动迁移
private static string? MigrateLegacyPath()
{
    var legacy = AppSettingsService.Current.CjxlPath
              ?? AppSettingsService.Current.CjpegliPath;
    if (!string.IsNullOrEmpty(legacy))
    {
        // 迁移到新字段
        AppSettingsService.Current.JxlLibDir = legacy;
        AppSettingsService.Current.CjxlPath = null;
        AppSettingsService.Current.CjpegliPath = null;
        AppSettingsService.Save();
        return legacy;
    }
    return null;
}
```

### 5.4 每工具在目录内的检测方法

```csharp
/// <summary>在目录中查找工具（用于 artifacts 目录的多工具检测）</summary>
public static List<ToolCapability> ScanArtifactsDir(string dir)
{
    var results = new List<ToolCapability>();
    if (!Directory.Exists(dir)) return results;

    void Check(string name, string toolName, Action<string>? onFound = null)
    {
        var path = Path.Combine(dir, PlatformServices.ToolName(toolName));
        results.Add(new ToolCapability
        {
            Name = name,
            Path = File.Exists(path) ? path : null,
            IsAvailable = File.Exists(path)
        });
        if (File.Exists(path)) onFound?.Invoke(path);
    }

    Check("ultrahdr_app", "ultrahdr_app", p => UltrahdrService.SetPath(p));
    Check("JxrEncApp",    "JxrEncApp",    p => JxrService.SetPath(p));
    Check("JxrDecApp",    "JxrDecApp");
    Check("avifenc",      "avifenc");
    Check("avifdec",      "avifdec");
    Check("aomenc",       "aomenc");
    Check("aomdec",       "aomdec");

    return results;
}
```

---

## 六、PLAN 自动检测更新

### 6.1 新 PlanSubDirs 映射

```csharp
private static readonly Dictionary<string, string[]> PlanSubDirs = new()
{
    [Ffmpeg]    = new[] { "ffmpeg-full" },
    [Ffprobe]   = new[] { "ffmpeg-full" },
    // JXL 三工具统一到 jxl/ 目录
    [Cjxl]      = new[] { "jxl" },
    [Djxl]      = new[] { "jxl" },
    [Cjpegli]   = new[] { "jxl" },
    // exiftool 简化
    [Exiftool]  = new[] { "exiftool" },
    // 全部 artifacts 统一
    [Ultrahdr]  = new[] { "artifacts" },
    [JxrEnc]    = new[] { "artifacts" },
    [JxrDec]    = new[] { "artifacts" },
    [Avifenc]   = new[] { "artifacts" },
    [Aomenc]    = new[] { "artifacts" },
};
```

### 6.2 PLAN 目录新旧对照

```
旧 (v1.4.5)                         新 (v2.0)
─────────────────────────────────────────────────────
PLAN/                               PLAN/
├── ffmpeg-full/        ──不变──▶   ├── ffmpeg-full/
├── jxl-x64-windows-static/         ├── jxl/
│   └── bin/            ──扁平化──▶  │   ├── cjxl.exe
│       ├── cjxl.exe                 │   ├── djxl.exe
│       ├── djxl.exe                 │   └── cjpegli.exe
│       └── cjpegli.exe             │
├── exiftool-13.58_64/              ├── exiftool/
│   └── exiftool-13.58_64/ ──简化──▶│   ├── exiftool.exe
│       └── exiftool.exe             │   └── exiftool(-k).exe
├── windows-artifacts/              ├── artifacts/
│   ├── ultrahdr_app.exe ──重命名──▶│   ├── ultrahdr_app.exe
│   ├── JxrEncApp.exe               │   ├── JxrEncApp.exe
│   └── ...                         │   └── ...
└── 使用说明.txt          ──不变──▶  └── 使用说明.txt
```

---

## 七、兼容性处理

### 7.1 JSON 反序列化时自动迁移

```csharp
public static AppSettings Load()
{
    var settings = JsonSerializer.Deserialize<AppSettings>(json);
    
    // 自动迁移旧字段
    if (settings.JxlLibDir == null)
    {
        settings.JxlLibDir = settings.CjxlPath ?? settings.CjpegliPath;
        // 不清空旧字段，保留以便回滚
    }
    if (settings.WindowsArtifactsDir == null)
    {
        settings.WindowsArtifactsDir = settings.AvifencPath
                                    ?? settings.UltrahdrPath
                                    ?? settings.JxrPath;
    }
    
    return settings;
}
```

### 7.2 旧配置文件打开后行为

```
用户升级到 v2.0，settings.json 中还写着:
  "CjxlPath": "D:\\tools\\jxl\\bin"
  "CjpegliPath": null

程序启动 → AppSettings.Load() → 自动设置:
  JxlLibDir = "D:\\tools\\jxl\\bin"
  日志: "[migration] 已从 CjxlPath 迁移到 JxlLibDir"

下次保存时 → 旧字段不再写入（仅保留 JxlLibDir）
```

---

## 八、文件改动清单

| 操作 | 文件 | 说明 |
|:--:|:--|:--|
| 🔧 修改 | `Models/AppSettings.cs` | 新增 JxlLibDir/WindowsArtifactsDir，废弃旧字段，自动迁移 |
| 🔧 修改 | `MainWindow.xaml` | UI 从 7 输入框缩减为 3+1 |
| 🔧 修改 | `MainWindow.xaml.cs` | 删除 6 个 Browse*_Click 方法，新增 2 个 + 校验标签更新 |
| 🔧 修改 | `Services/CjxlService.cs` | 改用 JxlLibDir + 自动迁移 |
| 🔧 修改 | `Services/DjxlService.cs` | 同上 |
| 🔧 修改 | `Services/CjpegliService.cs` | 同上 |
| 🔧 修改 | `Services/UltrahdrService.cs` | 改用 WindowsArtifactsDir |
| 🔧 修改 | `Services/JxrService.cs` | 同上 |
| 🔧 修改 | `Services/ExifToolService.cs` | 文件模式 + 目录回退兼容 |
| 🔧 修改 | `Services/QueueProcessor.cs` | avifenc 检测改用 WindowsArtifactsDir |
| 🔧 修改 | `Services/PlatformServices.cs` | 更新 PlanSubDirs 映射 |
| 🔧 修改 | `Services/ExternalToolsDetector.cs` | 新增 ScanArtifactsDir() |
| 📁 重命名 | `publish/PLAN/` 子目录 | jxl-*→jxl, exiftool-*→exiftool, windows-artifacts→artifacts |
| 🔧 修改 | `pack.ps1` | 打包路径适配新目录名 |
| 🔧 修改 | `PACKAGING_SPEC.md` | 更新目录结构文档 |

---

## 九、实施顺序

```
Phase A (1天): AppSettings 模型变更 + 自动迁移逻辑
Phase B (1天): Service 层适配 (Cjxl/Djxl/Cjpegli → JxlLibDir)
Phase C (1天): Service 层适配 (Ultrahdr/Jxr/avifenc → WindowsArtifactsDir)
Phase D (1天): UI 重构 (XAML + 事件处理)
Phase E (半天): PLAN 目录重命名 + 打包脚本更新
Phase F (半天): 全量测试 + 旧配置兼容性验证
```

> 预计总工时: **4-5 天**
