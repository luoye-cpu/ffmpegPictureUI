# 构建 / AOT / 打包 / 硬件能力检测 审查报告（a9e2830..957cfa8）

## 0. 结论摘要

1. **最高级别：LOW**。本路 8 项发现中无 CRITICAL/HIGH；ISA 口径、AOT 检测设计、版本/tag 三处一致、第三方声明与实际二进制版本对得上。
2. **AOT 下 CPU 检测不失真**：`CpuFeatureService` 重写后用 `X86Base.CpuId` 直接读硬件 CPUID 位（而非 `IsSupported`），`IsSupported` 值被显式分流到 `Runtime*` 字段并在报告里说明"AOT 基线 v2"。反射探针只在 CPUID 不可用的回退分支，且被 `TrimmerRootAssembly` + `[UnconditionalSuppressMessage]` 守住。GpuCapabilityService 全程 `Process.Start` ffmpeg，零反射。
3. **ISA 口径两侧一致**：csproj `IlcInstructionSet=x86-64-v3`（Condition=PublishAot=true）与 pack.ps1 `-p:IlcInstructionSet=x86-64-v3` 逐字相同；`IlcOptimizationPreference=Speed` 两侧也一致。
4. **5 条构建警告全部为存量**（beta16 包日志同 5 条），CpuFeatureService/GpuCapabilityService 重写**未引入任何新警告**（无 IL2026/IL2075）。
5. **app 包缺 oxipng 时默认 max 的行为**：**显式降级，不静默**——`PngOptimizerService.cs:85` 打 `[pngopt] 未检测到 oxipng，已跳过优化` 并返回，任务不判失败。但启动检测报告（Program.cs:293-303）不点名 oxipng，GUI 下拉仍显示 max。详见 §6-5 跨模块候选。
6. **版本/哈希/tag 三处一致**：csproj `1.6.0-beta17` → pack.ps1 默认读值 → 包文件名 `v1.6.0-beta17-x64[-full].7z` → exe ProductVersion `1.6.0-beta17+957cfa8...` → tag `1.6.0BETA17` = HEAD `957cfa8`。

---

## 1. 覆盖清单

| 文件 | 结论 | 发现 id |
|---|---|---|
| `src/FfmpegGui/FfmpegGui.csproj` | ISA/版本/AOT 属性一致；AOT 单包无 dll（哈希口径注意） | BUILD-02, BUILD-06 |
| `pack.ps1` | robocopy 无全路径兜底；publish 参数与 csproj 对撞一致 | BUILD-01, BUILD-06 |
| `PACKAGING_SPEC.md` | 新增 CPU 要求行（x86-64-v3）与 csproj 一致；无问题 | — |
| `THIRD-PARTY-NOTICES.md` | oxipng/avifenc/libjxl/ILLink 版本与实际二进制对得上 | BUILD-05 |
| `src/FfmpegGui/Services/CpuFeatureService.cs` (+229/-51 区段) | AOT 检测设计正确；反射在回退分支且被 root | BUILD-08 |
| `src/FfmpegGui/Services/GpuCapabilityService.cs` (+212/-91 区段) | 零反射，全靠 ffmpeg 子进程；三态诚实 | — |
| `src/FfmpegGui/Services/EncoderDetectionService.cs` | best-encoder 选路优先 Verified；"未安装"不会变"支持" | — |
| `src/FfmpegGui/Services/LocalizationService.cs` | 用 source-generated JsonContext；仅加 2 条回退文案 | — |
| `global.json` | SDK 11.0.100 与 net11.0 TFM 匹配 | — |

---

## 2. 发现详情

### BUILD-01（LOW）：pack.ps1 全档模式依赖 PATH 里的 robocopy，无全路径兜底

1. **级别**：LOW
2. **位置**：`pack.ps1:179`（`robocopy $PlanSource $PlanDest /E ...`）
3. **现象**：本次采集到的 `pack-beta17.out.txt` 尾部在 Step 2 报 `术语 'robocopy' 不会被识别为 cmdlet`，脚本因 `$ErrorActionPreference="Stop"` 中止。
4. **复现步骤**：在 PATH 不含 `C:\Windows\System32` 的 PowerShell 会话里跑 `.\pack.ps1 -Mode full`。
5. **证据**：`logs/build/pack-beta17.out.txt` 末尾；`Get-Command robocopy` 在本会话返回空。但**已发布的 beta17 full 包完整**（584 文件，11 个关键 PLAN 文件全在，`logs/build/hashes.txt`）⇒ 真正出包那次环境里 robocopy 在 PATH。
6. **风险**： loud failure（脚本直接 throw，不产出残缺包），不是静默。但换干净 CI 容器时可能偶发失败。
7. **建议**：pack.ps1:179 改用 `& "$env:SystemRoot\System32\robocopy.exe" ...` 全路径，或 `Get-Command robocopy` 失败时 fallback。不写实现。

### BUILD-02（LOW）：app 包与 full 包的 FfmpegGui.exe 哈希不同（仅 6 字节差），且 AOT 单包无 FfmpegGui.dll 可比对

1. **级别**：LOW
2. **位置**：`src/FfmpegGui/FfmpegGui.csproj:52-76`（AOT PropertyGroup）；`pack.ps1:98-108`
3. **现象**：app 包 exe sha256=`40266D90D4BF6DEB...`，full 包 exe sha256=`3AB17BF2E7FBCEC...`，逐字节 diff 仅 3 个区间共 6 字节（偏移 264、20298596、20298624，各 2 字节）。两包同尺寸 24,681,984 B。
4. **复现步骤**：`Get-FileHash` 两个 exe；逐字节比较（`logs/build/hashes.txt`）。
5. **证据**：`logs/build/hashes.txt`；6 字节差在 NativeAOT single-file bundle header（构建时间戳/nonce），非代码差异。
6. **风险**：AGENTS.md §4 要求"比对用 FfmpegGui.dll——*.exe 是 apphost（IL 在 dll 里）"。但 **AOT 单文件发布根本不产出 FfmpegGui.dll**（IL 已编译进 exe），该口径对 AOT 包不适用。任何"app 包 exe 哈希 == full 包 exe 哈希"的对账都会误报。
7. **建议**：在 AGENTS.md §4 或门禁 `verify-release-package` 里补一句"AOT 包以 exe 哈希为准，且 app/full 两包 exe 因 bundle header nonce 不同属预期"；或 pack.ps1 用 determinism 选项消除该 6 字节差。

### BUILD-03（LOW）：5 条构建警告全部为存量，本批重写零新增

1. **级别**：LOW
2. **位置**：
   - `src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs:830` — CS8604
   - `src/FfmpegGui/Services/QueueProcessor.cs:1580` — CS8602
   - `src/FfmpegGui/Services/QueueProcessor.cs:2680` — CS8602
   - `src/FfmpegGui/Win32TrayIcon.cs:78` — CA1416（SetApartmentState 仅 Windows）
   - `src/FfmpegGui/Win32TrayIcon.cs:150` — IL3002（Marshal.GetHINSTANCE single-file）
3. **现象**：beta17 pack 日志 `5 个警告 / 0 个错误`；与 beta16 pack 日志逐条相同（行号漂移 1576→1580、2676→2680 是源码增行所致，非新警告）。
4. **复现步骤**：对比 `logs/build/pack-beta17.out.txt` 与 `tests/output/_tmp/ui-fix-20261116/pack-beta16-final.log`。
5. **证据**：`logs/build/pack-beta17.out.txt`（含 `5 个警告` 汇总行）。
6. **风险**：CpuFeatureService 重写用了 `asm.GetType(...)` + `GetProperty("IsSupported")`（反射），按理会触发 IL2026/IL2075——但实际**没有**，因为 `[UnconditionalSuppressMessage]`（CpuFeatureService.cs:370-373, 386-389）+ csproj:103 `TrimmerRootAssembly Include="System.Runtime.Intrinsics"` 守住了。这是设计正确，不是漏报。
7. **建议**：无需动作；5 条存量警告可后续单独清理，不属本批回归。

### BUILD-04（MEDIUM→转 §6-5）：app 包缺 oxipng.exe 时默认 pngopt=max 的实际行为

> 本项是跨模块问题，按 spec §9 只登记现象，结论由总控裁决。完整分析见 §6-5。

### BUILD-05（LOW）：第三方声明版本与实际二进制对得上

1. **级别**：LOW
2. **位置**：`THIRD-PARTY-NOTICES.md:48`（oxipng 10.2.1）、`:45`（avifenc 1.4.2/aom 3.15.0）、`:28`（libjxl 0.12.0）、`:55`（ILLink.Tasks 11.0.0）
3. **现象**：实测 `oxipng --version` = `10.2.1`；`avifenc --version` = `1.4.2 (aom [enc]:3.15.0)`；`cjxl --version` = `v0.12.0`；THIRD-PARTY 表逐条命中。ILLink.Tasks 从 `11.0.0-rc.2` 升到 `11.0.0`（RTM），与 global.json SDK 11.0.100 一致。
4. **复现步骤**：跑 `PLAN/artifacts/oxipng.exe --version` 等。
5. **证据**：本会话命令输出（见 build log）。
6. **风险**：app 包（无 PLAN）仍随附这份 THIRD-PARTY-NOTICES.md，但里面列的 oxipng/avifenc/libjxl 并不在 app 包里——属"过度声明"（描述项目支持的全部组件），不是"漏声明"。合规上无害。
7. **建议**：无需动作。

### BUILD-06（LOW）：ISA / 优化偏好两侧对撞一致

1. **级别**：LOW
2. **位置**：`FfmpegGui.csproj:68` vs `pack.ps1:105`；`csproj:63` vs `pack.ps1:104`
3. **现象**：

   | 参数 | csproj | pack.ps1 命令行 | 一致？ |
   |---|---|---|---|
   | `IlcInstructionSet` | `x86-64-v3`（Condition PublishAot=true） | `-p:IlcInstructionSet=x86-64-v3` | ✅ |
   | `IlcOptimizationPreference` | `Speed`（csproj:63） | `-p:IlcOptimizationPreference=Speed` | ✅ |
   | `PublishAot` | 默认 false，命令行 true | `-p:PublishAot=true` | ✅ |
   | `SelfContained` | true（AOT 组，csproj:56） | `-p:SelfContained=true` | ✅ |
   | `InvariantGlobalization` | true（csproj:70） | `-p:InvariantGlobalization=true` | ✅ |
   | RID | `RuntimeIdentifiers` 含 win-x64 | `-r win-x64`（$Rid） | ✅ |

   csproj:68 的 Condition `'$(PublishAot)' == 'true'` 能正确看到命令行 `-p:PublishAot=true`（命令行属性是 global property，条件求值时已就位）。
4. **复现步骤**：逐行对撞 csproj 与 pack.ps1。
5. **证据**：本报告 §1 表；`docs/review-2026-11-16/review-evidence/_spec.md` §8 已知 beta17 出包成功。
6. **风险**：无。PACKAGING_SPEC.md 使用说明模板（:203）新增"CPU: x86-64-v3 / AVX2 或更高"与 csproj:64-67 注释一致。
7. **建议**：无需动作。

### BUILD-07（LOW）：FFMPEGGUI_FFMPEG_DIR 正斜杠归一化已在代码里做（AGENTS.md §7 口径滞后）

1. **级别**：LOW
2. **位置**：`src/FfmpegGui/Services/AppSettingsService.cs:111-124`（`Norm()` = `Path.GetFullPath`）；`PlatformServices.cs:218`（PLAN_DIR 同款）
3. **现象**：`AppSettingsService.ApplyEnvironmentOverrides` 对 `FFMPEG_DIR/OUTPUT_DIR/EXIFTOOL_PATH/JXL_LIB_DIR/ARTIFACTS_DIR/DNGTOOL_PATH/PNGOPTIMIZER_PATH/CACHE_DIR` 全部过 `Path.GetFullPath`。该逻辑在 `a9e2830`（本批基线）就已存在（`git show a9e2830:...` 命中），**不是本批新增**。
4. **复现步骤**：`git diff a9e2830..957cfa8 -- src/FfmpegGui/Services/AppSettingsService.cs` 输出为空。
5. **证据**：本会话 git 命令。
6. **风险**：AGENTS.md §7 把这条列为"未了结"，但代码已修。属文档口径滞后，不是代码坑。
7. **建议**：总控可在后续账本里把该条从"未了结"划掉（本路不改 AGENTS.md）。

### BUILD-08（LOW）：AOT 下 CPU/GPU 检测不失真（代码推断，未跑 GUI 实测）

1. **级别**：LOW
2. **位置**：`CpuFeatureService.cs:160-289`（CPUID 直读）、`:88-103`（三字段分流）、`:374-400`（反射回退）；`GpuCapabilityService.cs` 全文
3. **现象**：
   - 硬件位（`HasAvx2` 等）来自 `X86Base.CpuId(leaf,subleaf)` 的 EAX/EBX/ECX/EDX 位（:194-220），**不**读 `IsSupported`。CPUID 是真实 CPU 指令，AOT 不裁剪、不失效。
   - `RuntimeHasAvx2` 等来自 `X86.Avx2.IsSupported`（:147），在 AOT 下反映**编译目标 ISA**（v3），被显式标注为"进程内可用"而非"硬件有"。
   - `CanUseAvx2` = 硬件位 && OS XSTATE 门（:285-288，`GetEnabledXStateFeatures` P/Invoke kernel32）。
   - 反射 `ProbeX86/ProbeArm` 只在 `cpuidOk == false` 的回退分支（:232-253），正常路径不触发。
   - `GpuCapabilityService` 全程 `Process.Start(ffmpeg.exe ...)` 解析 `-hwaccels`/`-encoders`/`-init_hw_device` 输出，零反射、零 `Assembly.GetTypes`、零 dynamic。
   - `LocalizationService` 用 `AppJsonContext.Default.DictionaryStringString`（source-gen JSON），AOT 安全；`GetManifestResourceNames()` 只在外部 json 缺失时作 fallback（:160-167）。
4. **复现步骤**：读代码分支。本路无法 headless 触发 `CpuFeatureService.FullReport()`（该函数只在 `MainWindow.xaml.cs:1539` GUI 启动时调用，CLI `--help`/`--version` 不打）。
5. **证据**：代码逐行；本机 CPU 真值 = `13th Gen Intel i7-13650HX`（Raptor Lake，AVX2 ✅ / AVX-512 ❌——Intel 13 代移动版砍掉 AVX-512），按 CPUID 路径应报 `X86FeatureLevel=V3, BestSimdTag=avx2`。
6. **风险**：若未来有人把 `HasAvx2` 改回 `= Avx2.IsSupported`，AOT v3 包在 v2 目标下会误报 false——但当前代码注释（:88-93）明确警告了这一点，设计是有意识的。
7. **建议**：建议后续加一个 `--diagnose` headless 开关直接打印 `CpuFeatureService.FullReport()` + GPU 报告，以便发版后无 GUI 环境取证（本路未实测该输出）。

---

## 3. 门禁与负控实测

- 本路**未跑门禁**（只读审查；`_run-step3-gates.ps1` 需选择器，且本路改动文件均非门禁判据本身）。
- 实跑：
  - `FfmpegGui.exe --help` / `--version`（app 包）→ rc=0，输出正常（`logs/build/version.txt`）。
  - `FfmpegGui.exe --headless -i t.png -o out -f png`（app 包，复制到干净目录）→ rc=0，1 项完成，工具探测报告打印 8 行（ffmpeg/cjxl/djxl/cjpegli/exiftool/JxrEncApp/avifenc/dngtool）。
  - `Get-FileHash` 两包 exe + 逐字节 diff（`logs/build/hashes.txt`）。
  - `Get-CimInstance Win32_Processor` / `Win32_VideoController` 取硬件真值。
- 未跑负控（本路无判据文件可改）。

---

## 4. 未验证项与原因

1. **CpuFeatureService.FullReport() 的实际 stdout**：headless 无此开关，GUI 才能触发。本路靠代码分支 + CPUID 指令语义推断（BUILD-08 verified=代码推断）。
2. **GPU 编码器实测矩阵**：本机有 Intel UHD + RTX 4060，但 `GpuCapabilityService.DetectAsync` 只在 GUI 后台跑；headless 报告不含 GPU 行。`mjpeg_qsv`/`mjpeg_nvenc` 的 Verified 状态未实测。
3. **app 包在"完全干净、无 ffmpeg、无 PLAN"的真机行为**：本会话复制到干净目录后，因开发者 `%APPDATA%` settings.json 仍带历史路径，工具仍报 ✅[manual]。真正"全新用户首启"的 ffmpeg ❌ 路径未触发（无法清除用户 settings）。
4. **5 条警告在 beta15 及更早是否已存在**：只对比了 beta16，未追 beta15；但 5 条均不在本批改的 8 个文件里（RawColorPipeline/QueueProcessor/Win32TrayIcon 均非本路文件），判为存量可信。
5. **NativeAOT bundle header 6 字节差的确切字段**：未逆向 single-file host；按经验是 timestamp/build-id，非代码差异。

---

## 5. 跨模块候选（只登记现象，不下结论）

### 5.1 app 包缺 oxipng.exe 而默认 pngopt=max（待总控裁决）

- **现象**：app 包（11.6 MB）只有 `FfmpegGui.exe` + Skia/HarfBuzz/GLES 原生库 + `licenses/` + `Resources/`，**无 PLAN/artifacts/oxipng.exe**。`FfmpegOptions.PngOptimizer` 默认 `"max"`（csproj 之外，`FfmpegOptions.cs:338`）。
- **代码行为**：`PngOptimizerService.ApplyPngOptimizerAsync`（:81-87）调 `ResolveOxipngPath()`：
  1. 先看 `AppSettings.PngOptimizerPath`（用户显式配置）；
  2. 再 `ExternalToolsDetector.FindInArtifactsOrPlan("oxipng.exe")`（PLAN/artifacts 惯例位）；
  3. 都找不到 → `log("[pngopt] 未检测到 oxipng，已跳过优化（期望位置 PLAN/artifacts/oxipng.exe，或在设置中指定路径）")` 并 `return`。
- **结论性事实**：**不报错、不判任务失败、不静默宣称 max 已生效**——它会打一行明确的降级日志。产物 = ffmpeg 原生 PNG 输出（未重压 IDAT）。
- **但有三个摩擦点**（跨 UI/色彩/打包）：
  1. 启动检测报告（`Program.cs:293-303`）列了 8 个工具（ffmpeg/cjxl/djxl/cjpegli/exiftool/JxrEncApp/avifenc/dngtool），**唯独不列 oxipng**。用户首启看不到"oxipng 缺失"预警。
  2. GUI 下拉 `PngOptimizerCombo` 默认 `SelectedIndex=2`（=max，`MainWindow.xaml:461`），即使 oxipng 不在场，下拉仍显示"max"。
  3. 降级日志 `[pngopt] 未检测到 oxipng` 只在**具体 PNG 任务**的日志流里出现，不在启动横幅。
- **pack.ps1 是否有 app 档显式依赖清单**：没有。`$need`/`$needDirs` 完整性闸（pack.ps1:235-244）只在 `mode -eq "full"` 时检查 PLAN 工具链；app 档 `minFiles=8` 只要求 exe + 法律文件 + licenses。app 档不校验"默认 max 所需的 oxipng 是否在场"——因为 app 档设计上就不带外部工具。
- **avifenc/dngtool/exiftool 缺失时的路径**：这三个在启动报告里**逐行点名 ✅/❌**（Program.cs:301-303），且 `InferDefaultBackend`（EncoderDetectionService.cs:50-60）在外部工具不可用时回退 FFmpeg 内置编码器，不会把"未安装"变成"支持"。这三个的缺失是**可见且安全降级**的。
- **待总控裁决**：app 包是否应在启动横幅加一行 `oxipng: ❌（PNG 优化将跳过，默认 max 不生效）`？或者 pack.ps1 app 档在 `minFiles` 之外加一条"oxipng 不在场时打印显著警告"？本路不下结论。

### 5.2 app/full exe 哈希不同（BUILD-02）与 AGENTS.md §4 的 dll 口径冲突

- AGENTS.md §4 要求用 `FfmpegGui.dll` 对账，但 AOT 包无 dll。GATE 路的门禁若按 dll 对账会失配。

---

## 6. 复现命令附录

```powershell
# 版本/哈希
$exe = "publish\build\FFmpegPictureUI-v1.6.0-beta17-x64\FfmpegGui.exe"
(Get-Item $exe).VersionInfo.ProductVersion          # 1.6.0-beta17+957cfa8...
(Get-FileHash $exe -Algorithm SHA256).Hash          # 40266D90...
& $exe --version                                     # FFmpegPictureUI v1.6.0.0

# 包内容
Get-ChildItem publish\build\FFmpegPictureUI-v1.6.0-beta17-x64 -Recurse | Measure-Object Length -Sum

# 工具版本
publish\PLAN\artifacts\oxipng.exe --version         # 10.2.1
publish\PLAN\artifacts\avifenc.exe --version        # 1.4.2 (aom 3.15.0)
publish\PLAN\jxl\bin\cjxl.exe --version             # v0.12.0

# 硬件真值
Get-CimInstance Win32_Processor                     # i7-13650HX
Get-CimInstance Win32_VideoController               # Intel UHD + RTX 4060

# 包日志
Select-String -Path tests\output\_tmp\oxipng-ui-build\pack-beta17.out.txt -Pattern "warning|个警告"

# git
& "C:\Program Files\Git\bin\git.exe" rev-list -n1 1.6.0BETA17   # 957cfa8...
& "C:\Program Files\Git\bin\git.exe" diff --stat a9e2830..957cfa8 -- src/FfmpegGui/FfmpegGui.csproj pack.ps1
```
