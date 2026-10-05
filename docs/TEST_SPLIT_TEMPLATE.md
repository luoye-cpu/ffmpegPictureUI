# 测试拆分：组工程模板（供各子代理照抄）

> 本轮目标：**一次拆完全部 15 个工程**，三个旧宿主保留作**兼容层**（`Main` 委派到各组）。
> 本文件是**统一模板**，避免各组各写一套。

---

## 1. 目录与命名

```
tests/<group>/Tests.<Group>.csproj      AssemblyName = Tests.<Group>
tests/<group>/Program.cs                入口（独立可跑）
tests/<group>/<Name>.cs                 承载抽出的 Probe* 方法（可多个文件）
```

## 2. csproj 模板（逐字照抄，只改三处）

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>Tests.GROUP</AssemblyName>
    <RootNamespace>Tests.GROUP</RootNamespace>
    <NoWarn>$(NoWarn);CS0618;MSB3884</NoWarn>
    <SelfContained>true</SelfContained>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <PublishTrimmed>false</PublishTrimmed>
    <PublishAot>false</PublishAot>
    <PublishSingleFile>false</PublishSingleFile>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Tests.Shared\Tests.Shared.csproj" />
  </ItemGroup>
</Project>
```

⚠ `SelfContained=true` **必须**：被引用的 `FfmpegGui` 是自包含 WinExe，引用者必须同为自包含。

## 3. Program.cs 模板

```csharp
using System;
using Tests.Shared;

namespace Tests.GROUP
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Harness.InitExternalTools();
            var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            var known = TestGroups.ModesOf("GROUP");
            if (mode is "--list" or "-l") { foreach (var m in known) Console.WriteLine(m); return 0; }
            if (mode != "" && Array.IndexOf(known, mode) < 0)
            {
                Console.Error.WriteLine($"错误: 本组不认识 mode '{mode}'。可用: {string.Join(", ", known)}");
                return 2;
            }
            <逐 mode 分派；mode=="" 跑全组>
            Console.WriteLine(Harness.SummaryLine());
            return Harness.ExitCode();
        }
    }
}
```

## 4. 抽取规则（保证读数与旧宿主逐条一致）

1. **方法体逐字复制**，不改逻辑、不改字符串（改一个字符就可能改断言文本，而门禁在 grep 那些文本）。
2. 文件顶部补齐 **using 与 using-alias**，与 `tests/ServiceProbe/Program.cs:1-26` 一致：
   ```csharp
   using System; using System.IO; using System.Text; using System.Linq;
   using System.Collections.Generic; using Tests.Shared; using FfmpegGui.Services;
   using TC = FfmpegGui.Services.ColorMapping.TransferCurve;
   using CK = FfmpegGui.Services.ColorMapping.ColorKernels;
   using CM = FfmpegGui.Services.ColorMapping.ColorMetrics;
   using CS = FfmpegGui.Services.ColorMapping.ColorSpaceDescriptor;
   using CE = FfmpegGui.Services.ColorMapping.ColorMappingEngine;
   using CI = FfmpegGui.Services.ColorMapping.ColorIntent;
   using CA = FfmpegGui.Services.ColorMapping.ColorAction;
   using CB = FfmpegGui.Services.ColorMapping.ColorBackend;
   using CL = FfmpegGui.Services.ColorMapping.ColorLoss;
   using ST = FfmpegGui.Models.ColorStrategy;
   using DC = FfmpegGui.Services.ColorMapping.DescriptorConfidence;
   using OR = FfmpegGui.Services.ColorMapping.OverrideReason;
   using AC = FfmpegGui.Services.ColorMapping.AnnotChoice;
   using CSC = FfmpegGui.Services.ColorMapping.CarryScope;
   using MI = FfmpegGui.Services.ColorMapping.ManualIntent;
   using CP = FfmpegGui.Services.ColorMapping.ColorTransformPlan;
   using GF = FfmpegGui.Services.ColorMapping.RawGamutFit;
   ```
   ⚠ 多写无害、漏写必 CS0103。
3. **共享 helper**：旧宿主里的 `Check`/`Skip`/`RunCap`/`RunCapture`/`RunTask`/`InitExternalTools`/
   `Sha`/`ReadU16Le`/… 一律**转发**到 `Harness`，**不要**复制实现：
   ```csharp
   private static void Check(bool ok, string msg) => Harness.Check(ok, msg);
   private static void Skip(string msg) => Harness.Skip(msg);
   ```
   ⚠ 若某 helper 只在**一个** probe 里用（如 `ReadBe32`、`NclxColrBox`），把它**连同**该 probe 一起搬走。
4. **共享静态字段**：只有 `_pass/_fail/_skip`（已在 `Harness`）。若发现别的静态字段被多组共用，
   **停下来报告**，不要各自复制（会造成两组读数分叉）。

## 5. 验收（每组必须做）

```powershell
# 1) 新组单独跑 == 旧 mode 读数
dotnet build tests/<group>/Tests.<Group>.csproj -c Release --no-restore
$new = & tests/<group>/bin/Release/net11.0/win-x64/Tests.<Group>.exe <mode>
$old = & tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe <mode>
# 两者的 PASS/FAIL 计数与断言文本必须逐条一致
```

⚠ **旧宿主此刻还没改成委派** ⇒ 它是独立的参照实现，正好用来对拍读数。
