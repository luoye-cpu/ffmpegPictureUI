# .NET 11 升级可行性分析与展望 / .NET 11 Migration Analysis

> 基线 / Baseline: .NET 10.0 | 当前版本 / Current: v1.5.0 (已发布 / Released) | 预计 .NET 11 发布: 2026 年 11 月 (LTS)

---

## 一、当前 .NET 10 基线确认

| 项目 | TFM | 状态 |
|------|-----|:--:|
| `FfmpegGui.csproj` | `net10.0` | ✅ |
| `VerifyCjxl.csproj` | `net10.0` | ✅ |
| `VerifyMulti.csproj` | `net10.0` | ✅ |
| `VerifyPreset.csproj` | `net10.0` | ✅ |
| `VerifyQueue.csproj` | `net10.0` | ✅ |
| `Avalonia` | 12.0.4 | ✅ (支持 .NET 8/9/10) |

> **确认**: 全部 5 个项目统一 `net10.0`，无混用 TFM。Verify 项目的 HintPath 已改为 `$(MainTfm)` 变量引用，升级时仅需改一处。

---

## 二、.NET 10 → .NET 11 升级步骤

### 预计升级工作量：1 天

```bash
# 1. 修改 TargetFramework
#    src/FfmpegGui/FfmpegGui.csproj:  net10.0 → net11.0
#    tools/*/Verify*.csproj:           net10.0 → net11.0 (TargetFramework)
#    tools/*/Verify*.csproj:          net10.0 → net11.0 (MainTfm 变量)

# 2. 更新 NuGet 包
dotnet list package --outdated
# Avalonia 预计需升级到 12.1.x（.NET 11 兼容版本）

# 3. 编译 + 修复兼容性问题
dotnet build
dotnet test

# 4. 发布
dotnet publish -c Release -r win-x64
```

### .NET 10 代码兼容性

| API 类别 | .NET 10 当前使用 | .NET 11 兼容性 |
|----------|:--:|:--:|
| `System.Runtime.Intrinsics.X86.*` | ✅ 基础类型 | ✅ 完全兼容，.NET 11 可能新增类型 |
| `System.Runtime.Intrinsics.Arm.*` | ✅ AdvSimd/Aes/Crc32 | ✅ 完全兼容 |
| `System.Diagnostics.Process` | ✅ 全部使用 | ✅ 无破坏性变更 |
| `System.Text.Json` | ✅ 序列化 | ✅ 向后兼容 |
| `System.IO.*` | ✅ 文件操作 | ✅ 完全兼容 |
| `System.Threading.*` | ✅ 并发模型 | ✅ 完全兼容 |
| `Avalonia 12.x` | ✅ 12.0.4 | ⚠️ 需升级到 .NET 11 兼容版本 |
| `OperatingSystem.Is*()` | ✅ 跨平台判断 | ✅ 向后兼容 |

---

## 三、.NET 11 预期收益分析

### 3.1 JIT 编译器改进

| 特性 | 当前 (.NET 10) | .NET 11 预期 | 对本项目的影响 |
|------|:--:|:--:|------|
| SIMD 自动向量化 | AVX2 级别 | 可能扩展至 AVX-512/AVX10 | 管道数据处理更快 |
| 内联优化 | Tiered JIT | 更激进的内联策略 | 热路径 CPU 密集计算更快 |
| ARM64 JIT | NEON 优化 | 更好的 NEON/SVE 调度 | Linux ARM 迁移受益最大 |
| PGO (动态) | TieredPGO | 改进的配置文件引导优化 | 启动后自适应优化 |

### 3.2 运行时改进

| 特性 | .NET 10 | .NET 11 预期 | 收益 |
|------|---------|-------------|------|
| **NativeAOT** | 实验性 | **正式 GA**（预期） | 冷启动 <1s，单文件 <20MB |
| **R2R (ReadyToRun)** | 部分支持 | 改进的预编译 | 首次运行 JIT 开销降低 |
| **GC 改进** | DATAS 模式 | 更低延迟的 GC | 大文件处理时内存回收更平滑 |
| **线程池** | 默认 | 改进的工作窃取 | 高并发转换任务调度更均匀 |

### 3.3 库改进

| 库 | .NET 10 | .NET 11 预期 | 收益 |
|------|---------|-------------|------|
| `System.Text.Json` | v9 | v10 源码生成器增强 | 配置文件加载更快 |
| `Regex` | 源码生成 | 改进的源码生成 | `FFmpeg -encoders` 输出解析提速 |
| `HttpClient` | 默认 | 改进的连接池 | 远端能力查询更快 |

### 3.4 C# 语言特性

| 特性 | C# 13 (.NET 10) | C# 14 (.NET 11 预期) | 代码简化 |
|------|:--:|:--:|------|
| `params` 集合 | `params Span<T>` | 扩展支持 | 减少数组分配 |
| `field` 关键字 | 预览 | 正式 | 属性代码更简洁 |
| 扩展类型 | — | 可能引入 | 减少工具类 |
| 空合并增强 | `??` | 可能扩展 | 减少 null 检查代码 |

---

## 四、NativeAOT 专项分析

### 4.1 什么是 NativeAOT

将 C# 代码直接编译为**本地机器码**（无 JIT，无需 .NET 运行时）。

### 4.2 对本项目的优势

| 优势 | 当前 (JIT) | NativeAOT |
|------|-----------|-----------|
| 启动时间 | ~2-3s (JIT编译) | **<0.5s** |
| 安装要求 | 用户需安装 .NET 10 Runtime | **零依赖**（绿色单文件） |
| 二进制大小 | ~15MB (框架依赖) | ~25MB (含运行时) |
| 内存占用 | ~80MB (含JIT缓存) | ~50MB |
| 峰值性能 | TieredPGO 自适应 | 编译时优化，略低于 PGO |

### 4.3 当前阻塞

| 阻塞项 | 状态 | 备注 |
|--------|:--:|------|
| Avalonia NativeAOT 支持 | ⚠️ 部分 | 需要 Avalonia 12.1+ 的 NativeAOT 兼容性 |
| `System.Diagnostics.Process` | ✅ | NativeAOT 完全支持 |
| 反射探测（CpuFeatureService） | ⚠️ | NativeAOT 默认裁剪反射，需配置 rd.xml |
| 第三方库 | ⚠️ | 需逐个验证 |

### 4.4 预期可用时间线

```
.NET 10 (当前) → NativeAOT 未正式推荐
.NET 11 (2026.11) → NativeAOT 正式 GA，Avalonia 兼容
.NET 12 (2027.11) → NativeAOT 完全成熟
```

---

## 五、升级收益量化估算

| 指标 | .NET 10 (当前) | .NET 11 + NativeAOT | 提升 |
|------|:--:|:--:|:--:|
| 冷启动 | ~3s | ~0.5s | **6x** |
| 内存基线 | ~80MB | ~50MB | **1.6x** |
| JIT 预热后性能 | 100% | ~105% | 5% |
| ARM64 性能 | 100% | ~115% | 15% |
| 发布包大小 (无PublishTrimmed) | ~70MB | ~25MB | **2.8x** |
| 用户安装步骤 | 3 步 | **1 步**（解压即用） | — |

---

## 六、升级建议

### 短期（当前）：保持 .NET 10

- ✅ 稳定性优先，.NET 10 已充分验证
- ✅ 分发时附带 .NET 10 Runtime 链接
- ✅ 使用 `SelfContained=false` 减小包体积

### 中期（2026 Q4）：.NET 11 预览版测试

- 🔬 在 `net11.0-preview` 分支验证编译
- 🔬 测试 Avalonia 12.1 兼容性
- 🔬 评估 NativeAOT 可行性
- 🔬 验证反射探测（CpuFeatureService）在 NativeAOT 下行为

### 长期（2027+）：正式升级 .NET 11

- 🚀 `TargetFramework` 切至 `net11.0`
- 🚀 评估启用 NativeAOT（需 AOT 兼容性测试）
- 🚀 同步更新 README、打包脚本、使用说明中的 .NET 版本引用

---

## 七、多 TFM 同时构建（可选）

如需同时支持 .NET 10 和 .NET 11：

```xml
<!-- 主项目 -->
<TargetFrameworks>net10.0;net11.0</TargetFrameworks>
```

但**不推荐**：会增加 NuGet 恢复时间、编译时间和 CI 复杂度。建议直接切换，不混用。

---

> 📅 **建议升级时间窗口**: 2027 年 Q1（.NET 11 发布 3 个月后，生态稳定）
>
> 📦 **主要升级价值**: NativeAOT 绿色单文件 → 用户无需安装 .NET Runtime
