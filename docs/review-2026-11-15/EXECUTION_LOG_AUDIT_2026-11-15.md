# 执行日志机制审查（2026-11-15）

## 0. 结论

这次审查把两件事分开看：

1. **日志显示链路**：日志确实能从后台检测走到右侧「执行日志」TextBox，但没有时间戳、级别、来源、容量上限、持久化或统一写入入口；存在后台线程直接写 UI 控件、日志乱序/丢失、完成提示早于实际完成的风险。
2. **日志内容语义**：截图里的编码器编译状态基本正确，但 **GPU 设备「可用」并不是编码能力验证**；其中 `VA-API (Linux): ✅ 可用` 是确定的误报，`av1_amf: DeviceFoundUntested` 是设备归属误判，`[detect] 全部检测完成` 在运行时验证和格式报告尚未结束时就被打印。

因此这份日志**不能作为“当前 GPU 编码能力已完整验证”的依据**。它可以作为“检测过程已跑到某一步”的线索，但关键结论需要按下面逐项读。

本轮只做审查，没有改动 `src/`。

---

## 1. 审查对象与证据

- 用户截图：`C:\Users\20210\AppData\Local\Doubao\User Data\ClipboardTemp\2f2b916b-1876-456c-8d17-30b9cf8a11e0.png`
- 产品源码：`C:\PLAN\ffmpegPictureUI\src\FfmpegGui\`
- ffmpeg：`publish\PLAN\ffmpeg-full\ffmpeg.exe`
- 本机 GPU：
  - Intel(R) UHD Graphics（RaptorLake-S）
  - NVIDIA GeForce RTX 4060 Laptop GPU
  - **没有 AMD 显卡**
- 取证日志：`tests/output/_tmp/execution-log-audit-2026-11-15/gpu-probe.txt`

复跑命令的核心形态：

```powershell
& $ff -hide_banner -init_hw_device <device> `
  -f lavfi -i color=c=black:s=256x256 -vframes 1 -f null -
```

同时跑了真实编码器 1 帧测试：

```powershell
& $ff -hide_banner -nostats -f lavfi `
  -i color=c=black:s=256x256:r=1 -vframes 1 -c:v <encoder> -f null -
```

---

## 2. 执行日志的显示链路

### 2.1 当前形态

- 主窗口日志控件是只读 `TextBox`：`src/FfmpegGui/MainWindow.xaml:1128-1131`。
- 启动检测在 `FullDetectionAsync()` 里通过局部 `Log()` 写入：
  `src/FfmpegGui/MainWindow.xaml.cs:1383-1391`。
- `Log()` 的实现是：

```csharp
void Log(string msg) => Dispatcher.UIThread.Post(() =>
{
    if (LogText != null) LogText.Text += msg + "\n";
});
```

这意味着：

- **没有时间戳**：每条消息只是 `msg + "\n"`，截图里的顺序无法换算成时间。
- **没有级别**：INFO/WARN/ERROR 只靠人工看 emoji 和文本，日志本身没有 level 字段。
- **没有来源**：无法区分“设备探测”“编码器验证”“格式报告”“UI 操作”是哪个阶段写入的。
- **没有统一入口**：大量地方直接 `LogText.Text += ...`，另一些地方走 `Log()`，还有 `Dispatcher.UIThread.InvokeAsync`。

### 2.2 后台线程直接写 UI 控件

`FullDetectionAsync` 整体在 `Task.Run` 内运行
（`MainWindow.xaml.cs:1386` 起）。其中：

- `src/FfmpegGui/MainWindow.xaml.cs:1450`

```csharp
ExternalToolsDetector.ProbeFfmpeg(null, m => {
    if (LogText != null) LogText.Text += m;
});
```

- `src/FfmpegGui/MainWindow.xaml.cs:1457`

```csharp
ExternalToolsDetector.ProbeAllTools(m => {
    if (LogText != null) LogText.Text += m;
});
```

这两个回调都在线程池线程上执行，却直接改 Avalonia UI 控件。Avalonia 控件有 UI 线程亲和性，正确做法是 `Dispatcher.UIThread.Post/InvokeAsync` 后再写。当前写法至少有两个风险：

1. 运行时抛出线程访问异常时，会被外层的 `catch { }` 吞掉，**日志静默丢失**；
2. 即使运行时没有立即抛异常，也可能与 `Log()` 投递的消息交错，导致日志顺序不可信。

同一段里 `Log()` 是安全的，`LogText.Text +=` 是不安全的——这不是统一的日志链路。

### 2.3 完成提示的时序

`MainWindow.xaml.cs:1468` 先完成 `DetectAsync()`，随后：

- 打印设备/编码器初始状态；
- 在 `:1495` 启动后台 `ValidateEncodersAsync()`；
- 在 `:1513` 立刻打印 `[detect] 全部检测完成`；
- 在 `:1530-1533` 再启动延迟 2 秒的 `ReportFormatCapabilityStatusAsync()`。

所以这条“全部检测完成”实际只代表：

> “设备初始化 + 编码器编译状态 + 初始报告已生成；**运行时验证和格式报告还没完成**。”

截图里它后面还跟着一排 `DeviceFoundUntested`，以及 `[format] JPG ...`，正好证明这一点：**文案比实际状态更早。**

`ValidateEncodersAsync()` 每个编码器可能要跑两次 8 秒测试（256/512），四个待验证编码器最坏可拖到几十秒；完成后只会在 `verifiedCount > 0` 时追加一条“运行时验证完成”，失败一个都不点名。用户在“全部检测完成”之后看到的仍是未验证状态，很容易误以为所有编码器都可用。

### 2.4 容量、持久化、滚动与清空

当前主日志：

- 没有 `MaxLength`、没有行数上限、没有截断策略；
- `Text +=` 每次重写整个字符串，长日志会越来越慢；
- 没有 `LogText.CaretIndex` / `ScrollToEnd` 的显式逻辑，追加后是否自动跟随最新行取决于 TextBox 默认行为，不能保证；
- 没有清空按钮/清空命令；
- 没有落盘；程序退出后日志消失；
- 主日志不保存每条任务的 ffmpeg stdout/stderr，真正的逐任务日志在详情窗口的 `ImageDetailWindow.xaml:104-114`，是另一套内存 TextBox。

所以“执行日志”这个标题下实际上有两套日志面：

| 日志面 | 内容 | 是否持久化 | 上限 |
|---|---|---|---|
| 主窗口 `LogText` | 启动/检测/队列状态/格式报告 | 否 | 无 |
| 详情窗口 `InfoLog` | 单个队列项的运行日志 | 否 | `MaxHeight=300`（只是显示高度） |
| CLI `ConsoleQueueObserver` | 命令行运行日志，支持 `LogFile` | 可选 | 另有实现 |

主日志没有记录每个任务的 ffmpeg 输出；详情日志不会汇总回主日志。用户如果只看主窗口，无法从「执行日志」判断某个文件实际用了哪个编码器、是否真的编码成功。

### 2.5 失败可见性

多处检测失败被吞掉：

- `CpuFeatureService.Detect()` 外层 `catch { }`（`MainWindow.xaml.cs:1447`）；
- `ExternalToolsDetector.ProbeFfmpeg` / `ProbeAllTools` 外层 `catch { }`（`:1454` / `:1462` 有异常日志，但内部逐项异常可能没有）；
- GPU 设备探测 `TryDetectDeviceViaInitAsync()` 的 `catch { }`（`GpuCapabilityService.cs:282-285`）；
- `GetCompiledEncodersAsync()` 的 `catch { }`（`:369`）；
- `ValidateEncodersAsync()` 任务外层 `catch { }`（`MainWindow.xaml.cs:1508`）；
- 格式报告每个格式的 `catch { }`（`:1555`）。

这些吞异常让“没有日志”与“检测通过”“检测失败”无法区分。例如 `-encoders` 超时返回空集合，代码会把所有 GPU 编码器标成 `NotCompiled`，而不是标成“检测失败/未知”。

---

## 3. GPU 设备检测是否准确

### 3.1 探测命令实际验证的是什么

`DetectHardwareDevicesAsync()` 固定探测五类设备：

`src/FfmpegGui/Services/GpuCapabilityService.cs:200-210`

```csharp
await TryDetectDeviceViaInitAsync(ffmpegPath, "d3d11va", devices);
await TryDetectDeviceViaInitAsync(ffmpegPath, "cuda", devices);
await TryDetectDeviceViaInitAsync(ffmpegPath, "qsv", devices);
await TryDetectDeviceViaInitAsync(ffmpegPath, "vulkan", devices);
await TryDetectDeviceViaInitAsync(ffmpegPath, "vaapi", devices);
```

每次执行的命令是：

```text
-hide_banner -init_hw_device <type> -f lavfi -i color=black:256x256
-vframes 1 -f null -
```

这里只初始化了硬件设备上下文，后面的帧仍然是默认的 **software `wrapped_avframe`**，没有选择硬件解码器，也没有选择硬件编码器。因此：

- `d3d11va` 成功 → 只说明 **D3D11 设备上下文能创建**，不证明 D3D11 视频加速/编码可用；
- `cuda` 成功 → 只说明 **CUDA 上下文能创建**，不等于 NVENC 某个编码器可用；
- `qsv` 成功 → 只说明 **QSV 设备能初始化**，不等于 QSV 的所有编码格式可用；
- `vulkan` 成功 → 只说明 Vulkan 设备能创建，不证明 Vulkan 编码可用。

顶部那句 `[GPU 设备] ... ✅ 可用` 的“可用”是**设备初始化级别**，不是编码器级别。这个标签需要改成语义更窄的说法，否则会误导。

### 3.2 `VA-API (Linux)` 是确定误报

本机实际结果：

| 探测 | 退出码 | stderr 关键内容 |
|---|---:|---|
| `d3d11va` | 0 | 正常编码统计 |
| `cuda` | 0 | 正常编码统计 |
| `qsv` | 0 | 正常编码统计 |
| `vulkan` | 0 | 正常编码统计 |
| `vaapi` | **-12** | `Device creation failed: -12.` / `Cannot allocate memory` |

而 `ffmpeg -hwaccels` 的实际输出里**根本没有 `vaapi`**：

```text
cuda
dxva2
qsv
d3d11va
opencl
vulkan
d3d12va
amf
```

代码为什么仍然标成可用：

`GpuCapabilityService.cs:248-270` 的核心逻辑是：

```csharp
var stderr = await RunFfmpegStderrAsync(...);
if (!string.IsNullOrWhiteSpace(stderr))
{
    var failed = lower.Contains("cannot open") ||
                 lower.Contains("failed to create") ||
                 lower.Contains("no device") ||
                 lower.Contains("error initializing") ||
                 lower.Contains("impossible to convert") ||
                 (lower.Contains("dll") && lower.Contains("failed to open"));

    if (!failed)
        devices.Add(... IsAvailable = true ...);
}
```

三个问题同时存在：

1. **没有检查进程退出码**。`vaapi` 已经 `exit=-12`，仍然进入“非失败”分支。
2. **失败关键字漏了 `device creation failed`**。实际 stderr 是 `Device creation failed`，而列表里写的是 `failed to create`，字面不匹配。
3. **`VA-API` 是 Linux 专用 API，Windows 上没有做 OS 适用性判断**。即使探测失败，也不应该显示成绿色的“可用”。

结论：截图里的 `VA-API (Linux): ✅ 可用` **不准确，属于确定的误报**；而且它会让 `GpuCapabilityReport.HasAnyGpu` 在没有其他 GPU 的 Windows 机器上也返回 true，因为 `HasAnyGpu` 只是 `Devices.Any(d => d.IsAvailable)`（`GpuCapabilityService.cs:55`）。

### 3.3 `av1_amf` 的设备归属也是误判

`GpuCapabilityService.cs:292-304` 的映射：

```csharp
("mjpeg_amf", "AMD AMF MJPEG", "d3d11va"),
("av1_amf",   "AMD AMF AV1",   "d3d11va"),
```

本机没有 AMD 显卡，但 `d3d11va` 初始化成功（NVIDIA/Intel 的 D3D11 设备都可以让它成功），于是：

- `av1_amf` 被标成 `DeviceFoundUntested`；
- 实际执行 `av1_amf` 编码时失败：

```text
[AMF] DLL amfrt64.dll failed to open
[av1_amf] Failed to create hardware device context (AMF)
exit=-1313558101
```

`d3d11va` 是“通用 D3D11 设备”，不是“AMD AMF 设备”。用 `d3d11va` 代表 AMF，必然在非 AMD 机器上产生假阳性。`ffmpeg -hwaccels` 明明有 `amf` 项；AMF 自己的设备初始化会因 `amfrt64.dll` 失败而返回错误，才更接近真实设备归属。

同理，`mjpeg_amf` 在本机未编译，显示 `NotCompiled` 是正确的；但 `av1_amf` 的 `DeviceFoundUntested` 不正确。

### 3.4 `QuickEncodeTestAsync` 的成功判定不可靠

`GpuCapabilityService.cs:378-430` 的关键逻辑：

```csharp
var stderr = await RunFfmpegStderrAsync(ffmpegPath, args, 8000);

if (string.IsNullOrWhiteSpace(stderr))
    continue;

var hardFail = ...;
if (hardFail) return false;

var success = lower.Contains("video:") ||
              lower.Contains("frame=") && lower.Contains("fps=") ||
              lower.Contains("encoded");
```

问题：

- **没有检查退出码**：`RunFfmpegStderrAsync()` 只返回 stderr，不返回 exit code（`:480-515`）。
- 使用了 `-nostats`，正常进度行 `frame=`/`fps=` 基本不出现；成功主要依赖 `video:`。
- ffmpeg 的失败摘要里也会出现 `video:0KiB`（例如 “Nothing was written ... video:0KiB”），所以 `video:` 不是成功标志。
- 硬失败列表是启发式；如果某个编码器失败文本不在表里，可能被误判为成功。

本机实际编码器结果：

| 编码器 | 实际退出码 | 说明 | 当前验证逻辑会怎么判 |
|---|---:|---|---|
| `mjpeg_qsv` | 0 | 正常编码 | 可成功 |
| `av1_nvenc` | 0 | 正常编码 | 可成功 |
| `av1_qsv` | **-40** | `Current codec type is unsupported` / `Invalid argument` | 硬失败命中，失败 |
| `av1_amf` | **-1313558101** | `DLL amfrt64.dll failed to open` / `Invalid argument` | 硬失败命中，失败 |

这张表说明截图里的 `DeviceFoundUntested` 是“**还没跑验证**”，而不是“验证通过”。如果验证跑完，`mjpeg_qsv`、`av1_nvenc` 应该变 `Verified`；`av1_qsv`、`av1_amf` 应该变 `Failed`。截图恰好停在验证之前，所以它展示的是**中间态**。

另外，`av1_qsv` 的失败原因是 `Current codec type is unsupported`；这可能是 QSV runtime/默认参数限制，不一定是“硬件绝对不支持 AV1”。`QuickEncodeTestAsync` 只用默认参数做一次 smoke test，失败结果只能说明“默认参数下跑不通”，产品文案却容易读成“硬件不支持”。这也是可靠性问题。

### 3.5 编译状态解析本身基本正确

`GetCompiledEncodersAsync()`（`GpuCapabilityService.cs:345-371`）按 `line[1] == 'V'` 取视频编码器，再按第一段空格切名称。实际 `ffmpeg -encoders` 输出形如：

```text
 V....D av1_nvenc            NVIDIA NVENC av1 encoder
 V..... av1_qsv              AV1 (Intel Quick Sync Video acceleration)
 V....D av1_amf              AMD AMF AV1 encoder
 V..... mjpeg_qsv            MJPEG (Intel Quick Sync Video acceleration)
```

这个解析对截图里的 `NotCompiled` 列表是成立的：

- `mjpeg_nvenc` 不在实际列表里 → `NotCompiled` 正确；
- `mjpeg_amf`、`mjpeg_vaapi` 不在列表里 → `NotCompiled` 正确；
- `av1_vaapi` 不在列表里 → `NotCompiled` 正确。

但要注意：解析失败时返回空集合，随后所有编码器都会被标成 `NotCompiled`，这不是“未编译”，而是“检测失败”。日志没有把这两种情况分开。

### 3.6 状态语义存在三套口径

同一份 GPU 状态在三个地方含义不同：

- `GpuCapabilityService.GpuEncoderStatus.IsUsable`：只有 `Verified` 才是 true（`GpuCapabilityService.cs:42`）。
- `EncoderDetectionService.EncoderInfo.IsAvailable`：`Verified` **和** `DeviceFoundUntested` 都算可用（`EncoderDetectionService.cs:74-90`）。
- `MainWindow.UpdateGpuEncoderWarning()`：`DeviceFoundUntested` 显示黄色“验证尚未完成”警告（`MainWindow.xaml.cs:4690-4696`）。

所以同一个 `DeviceFoundUntested`：

- 在能力报告里“不可用”；
- 在编码器下拉框筛选里“可用”；
- 在警告面板里“有设备但未验证”。

这不是一个统一的“可用”定义。用户截图顶部看到“设备可用”，下面看到“DeviceFoundUntested”，产生疑问是必然的。

另外：

- `GpuCapabilityReport.VerifiedCount` 只数 `Verified`，`AvailableCount` 名义上像“可用数”，实际数的是 `DeviceFoundUntested`（`:56-57`），命名会误导；
- `HasNvidiaNvenc` 检查的是 `mjpeg_nvenc`（`:94`），本机这个编码器未编译，所以该方法返回 false；但 `av1_nvenc` 实际可用。方法名叫 NVENC，口径却是 MJPEG NVENC，存在潜在误判。

### 3.7 截图逐行判定

| 截图行 | 判定 | 依据 |
|---|---|---|
| `Direct3D 11 Video Acceleration: ✅ 可用` | **设备初始化通过，不等于编码可用** | `d3d11va exit=0`；探测帧仍是 software wrapped_avframe |
| `NVIDIA CUDA / NVENC: ✅ 可用` | **CUDA 设备通过；NVENC 编码器并未整体验证** | `cuda exit=0`；`mjpeg_nvenc` 实际未编译；`av1_nvenc` 跑一帧成功但截图时未验证 |
| `Intel QuickSync Video: ✅ 可用` | **QSV 设备通过；MJPEG 可用，AV1 默认参数失败** | `qsv exit=0`；`mjpeg_qsv exit=0`；`av1_qsv exit=-40` |
| `Vulkan: ✅ 可用` | **设备初始化通过；没有 Vulkan 编码器验证** | `vulkan exit=0`；未跑 Vulkan 编码测试 |
| `VA-API (Linux): ✅ 可用` | **确定误报** | `vaapi exit=-12`；`-hwaccels` 不含 vaapi；Windows 上不适用 |
| `mjpeg_qsv — DeviceFoundUntested` | **快照时未验证；实际可跑通** | 实际 `mjpeg_qsv exit=0`；验证后会变 Verified |
| `mjpeg_nvenc — NotCompiled` | **准确** | 实际 `-encoders` 没有该名称 |
| `mjpeg_amf — NotCompiled` | **准确** | 实际 `-encoders` 没有该名称 |
| `mjpeg_vaapi — NotCompiled` | **准确** | 实际 `-encoders` 没有该名称 |
| `av1_qsv — DeviceFoundUntested` | **快照时未验证；实际默认参数失败** | 实际 `av1_qsv exit=-40`；验证后会变 Failed |
| `av1_nvenc — DeviceFoundUntested` | **快照时未验证；实际可跑通** | 实际 `av1_nvenc exit=0`；验证后会变 Verified |
| `av1_amf — DeviceFoundUntested` | **设备归属误判** | 本机无 AMD；`d3d11va` 泛化成功；实际 `av1_amf exit=-1313558101` |
| `av1_vaapi — NotCompiled` | **准确** | 实际 `-encoders` 没有该名称 |
| `[detect] 全部检测完成` | **误导** | 运行时验证和格式报告都在它之后才启动/完成 |
| `[format] JPG - 编码: mjpeg, 封装: ✅` | **编译/封装能力成立，非运行时写盘证明** | `GetFormatStatusAsync` 只查 `-encoders` 和 `-muxers`（`EncoderDetectionService.cs:617-648`） |

---

## 4. 哪些是准确的

为了不让结论变成“全部不可信”，下面这些是本次能确认成立的：

- `ffmpeg -encoders` 的编译状态列表与截图里的 `NotCompiled` 一致；
- `d3d11va`、`cuda`、`qsv`、`vulkan` 的设备初始化确实成功；
- `mjpeg_qsv` 在当前机器上实际能编码一帧；
- `av1_nvenc` 在当前机器上实际能编码一帧；
- `mjpeg_nvenc`、`mjpeg_amf`、`mjpeg_vaapi`、`av1_vaapi` 确实没有编译；
- `JPG` 的 `mjpeg` 编码器和 image2 muxer 在当前 ffmpeg 里确实存在。

这些是“编译/设备层面的真话”；问题在于日志把**设备初始化**、**编码器编译**、**运行时编码验证**三种状态混在同一条时间线上，并用了“可用”“全部检测完成”这种更强的词。

---

## 5. 优先级与修复建议（本轮未实施）

### P0：先修“确定的假话”

1. **设备探测必须检查 exit code**
   - `RunFfmpegStderrAsync()` 返回 `(stderr, exitCode, timedOut)`；
   - `exitCode != 0` 直接判不可用；
   - 失败关键字补 `device creation failed`、`cannot allocate memory`、`failed to set value`、`error parsing global options`、`initialization failed`、`not available` 等；
   - 成功不再依赖“stderr 非空”。

2. **VA-API 做 OS 适用性判断**
   - Windows 上不探测 `vaapi`，日志显示 `VA-API (Linux)：不适用`，或直接从设备列表移除；
   - 只有 Linux 才走 `-init_hw_device vaapi`。

3. **AMF 不再用 `d3d11va` 代替**
   - 用 `amf` 设备探测，或先确认 AMD 适配器（`Win32_VideoController.AdapterCompatibility` 含 AMD）；
   - 非 AMD 机器应显示 `CompiledNoDevice`，而不是 `DeviceFoundUntested`。

4. **“全部检测完成”改为真实阶段文案**
   - 在 `:1513` 改成 `[detect] 基础检测完成；GPU 运行时验证后台进行中`；
   - 或在 `ValidateEncodersAsync()` 完成后才打印“全部检测完成”，并同时打印 Verified/Failed 明细；
   - 格式报告也纳入同一完成条件，或明确标成“格式报告后台中”。

### P1：让日志链路可靠

1. 引入唯一的 `UiLogSink.Append(level, source, message)`：
   - 所有 `LogText.Text +=` 改走 sink；
   - sink 内部 `Dispatcher.UIThread.Post`；
   - 带时间戳、级别、来源；
   - 失败/超时不再 `catch { }` 静默吞掉。

2. 给主日志加：
   - 最大行数/字节数（例如 5000 行或 1 MB，超出截断并插入一行“已截断”）；
   - 自动滚到底部；
   - 清空按钮；
   - 可选落盘 `execution.log`（至少错误级别）。
   现有 `crash.log`/`tray.log`/CLI `LogFile` 是分散的，不能替代执行日志。

3. 将 `ExternalToolsDetector` 的两个回调改为 sink 调用；不要把 UI 控件引用传进后台回调。

4. `ValidateEncodersAsync()` 完成后：
   - 逐个打印 `Verified` / `Failed` 及 stderr 摘要；
   - 触发一次 `RefreshEncoderListAsync()` 和 `UpdateGpuEncoderWarning()`；
   - 即使 `verifiedCount == 0` 也要打印一行“0 个通过，N 个失败”。

### P2：把“能力”与“运行时验证”分层

1. 顶部设备状态改为：
   - `D3D11 设备初始化：✅`（不是“视频加速可用”）
   - `CUDA 设备初始化：✅`（NVENC 编码器另行列表）
   - `QSV 设备初始化：✅`
   - `Vulkan 设备初始化：✅`
   - `VA-API：不适用（Windows）`

2. `DeviceFoundUntested` 不要在 UI 的“可用”集合里混用：
   - 下拉框可以展示，但必须明确“待验证”；
   - `IsAvailable` 与 `IsUsable` 只能保留一个口径，或明确拆成 `CanTry` / `Verified`。

3. `QuickEncodeTestAsync` 改为：
   - 进程 exit code 为 0；
   - 输出确实有非零编码帧（不要用 `video:` 判断）；
   - 失败时保留 stderr 摘要，标成“默认参数 smoke test 失败”，不要直接断言“硬件不支持”。

### P3：格式报告措辞

`[format] JPG - 编码: mjpeg, 封装: ✅` 目前是“编译了编码器 + 注册了 muxer”，不是“已实际写出一张 JPG”。建议：

```text
[format] JPG - 编码: mjpeg（已编译）, 封装: image2（已注册）
```

如果要宣称“可用”，应加一条真实的 1 帧写盘测试。

---

## 6. 结论一句话

**日志的“传输”能工作，但“语义”不可靠：**

- `VA-API (Linux) ✅ 可用` 是确定的误报；
- `av1_amf DeviceFoundUntested` 是设备归属误判；
- `DeviceFoundUntested` 是“尚未验证”，不是“可用”；
- `[detect] 全部检测完成` 早于运行时验证和格式报告，属于时序误导；
- 日志本身缺少时间戳、级别、来源、持久化和容量控制，后台还有直接写 UI 控件的问题。

---

## 7. 证据文件

- `tests/output/_tmp/execution-log-audit-2026-11-15/gpu-probe.txt`
  - 五类设备 `-init_hw_device` 的 exit code/stderr；
  - `mjpeg_qsv` / `av1_qsv` / `av1_nvenc` / `av1_amf` 实际 1 帧编码结果；
  - `ffmpeg -encoders` / `-hwaccels` 输出。
- 关键源码：
  - `src/FfmpegGui/Services/GpuCapabilityService.cs:200-210, 238-286, 292-304, 345-371, 378-430, 480-515`
  - `src/FfmpegGui/Services/EncoderDetectionService.cs:74-90, 617-648`
  - `src/FfmpegGui/MainWindow.xaml.cs:1383-1391, 1450-1457, 1464-1513, 1530-1555, 4690-4696`
  - `src/FfmpegGui/MainWindow.xaml:1128-1131`
  - `src/FfmpegGui/ImageDetailWindow.xaml:104-114`
