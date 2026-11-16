# UI 格式切换卡死：运行时定位报告（2026-11-16）

## 0. 结论

**格式切换 handler 本身不是主因。** 在真实 `MainWindow` 的 Avalonia 无头宿主上实测：

- `FormatCombo_SelectionChanged`（含 `UpdateOptionAvailability`、`RefillOutMatrixComboForFormat`）**6~15 ms**；
- `UpdateOptionAvailability` 单独 **6.19 ms**；
- `RefillOutMatrixComboForFormat` **0.25 ms**；
- `ColorSpaceCombo` 选 `BT.2020 PQ`（HDR 分支）**160 ms**，其中包含同步 `ffprobe`。

**已经测出的最大 UI 线程杀手是主日志 TextBox：**

- 启动后主日志 `LogText.Text.Length = 2,420`，追加一行约 **53 ms**（首次赋值后布局另计 53 ms）；
- 10,000 字符时，追加一行 `AppendUiLogLine` 的 `set_ms` 约 **214~253 ms/行**；
- 50,000 字符时，一次布局 `Pump` 约 **1,451 ms**；同量级另一次运行 60 s 内未返回；
- 100,000 / 200,000 字符时，25 s 内未返回；
- 2,000,000 字符单行时，进程连续占用 CPU 超过 6 分钟，未返回（374 s CPU 后外部终止）。

这不是“日志多占一点内存”，而是 **每次 `LogText.Text = 整段字符串` 都触发全量文本布局，成本随长度超线性增长**。一旦主日志到达 10k 量级，每追加一行就能让 UI 线程停顿约 0.2 s；到达 50k 量级则达到秒级。格式切换、popup 关闭、鼠标抬起后的重绘都排在同一个 UI 队列里，于是表现成“菜单按下了但没收起、界面不刷新”，切后台/切回来触发新的渲染周期才补上。

`ProbeCurrentSourceBitDepth()` 是**第二条独立硬阻塞**：它在 UI 线程同步启动 `ffprobe`，超时上限 3 s，且超时后仍在 UI 线程 `GetResult()` 等管道。21 KB 的 PNG 实测 60~166 ms；大文件、慢盘、畸形 AVIF/JXL 可以吃满 3 s，甚至更久。它足以单独解释“工作一次后切格式卡死”，尤其用户当前停在 `BT.2020 PQ` 时。

## 1. 方法与边界

- 使用 `tests/projects/UiHostShared` 的 `UiHost.Bootstrap()` 建立真实 `FfmpegGui.MainWindow`（Avalonia Headless，`UseHeadlessDrawing=true`）；
- 反射调用真实 private 方法：`ProbeCurrentSourceBitDepth`、`UpdateOptionAvailability`、`AppendUiLogLine`；
- 通过真实控件赋值触发真实事件：`FormatCombo.SelectedItem`、`ColorSpaceCombo.SelectedItem`；
- 输入文件：`tests/output/_tmp/final-fix/src_8bit.png`（21,162 B，8-bit PNG）；
- ffprobe：`publish/PLAN/ffmpeg-full/ffprobe.exe`；
- 一次性探针工程 `tests/projects/UiLatencyProbe` 仅用于本轮，读完后已删除；产品 `src/` 未改一行。

**边界**：这是 Avalonia 无头布局/真实控件链路的 CPU 读数，不等同于桌面 GPU 渲染的帧时间；但它测的是同一套 `TextBox`、同一套 UI 线程和同一套 handler，长度—成本关系对真实窗口同样成立。真实窗口还有 GPU 合成，可能略快，但不会改变 10k→0.2 s、50k→秒级的数量级。

## 2. 读数

### 2.1 基本链路

| 项目 | 读数 |
|---|---:|
| 启动后 `LogText` 长度 | 2,420 字符 |
| `ProbeCurrentSourceBitDepth` 第 1/2/3 次 | 166.22 / 60.56 / 90.89 ms |
| `UpdateOptionAvailability`（auto） | 6.19 ms |
| `ColorSpaceCombo` 选 `BT.2020 PQ` | 160.09 ms |
| `RefillOutMatrixComboForFormat` | 0.25 ms |
| `FormatCombo` AVIF→PNG handler | 15.02 ms |
| 同次 `Pump(20)`（异步续体） | 27.33 ms |
| 同次切完后 `ColorSpaceCombo.SelectedItem` | `auto`（被 `2532` 行复位） |

### 2.2 主日志 TextBox

| 文本长度 | 操作 | 读数 |
|---:|---|---:|
| 2,366 | 首次设置 + `Pump(2)` | set 0.67 ms；pump **53.66 ms** |
| 2,384 | 追加第 1 行 + `Pump(4)` | set 0.54 ms；pump **51.73 ms** |
| 2,402 | 追加第 2 行 | set **54.69 ms**；pump 0.78 ms |
| 2,420 | 追加第 3 行 | set **52.97 ms**；pump 1.11 ms |
| 10,062 | 首次设置 + `Pump(2)` | set 0.25 ms；pump **231.05 ms** |
| 10,080 | 追加第 1 行 | set **252.55 ms** |
| 10,098 | 追加第 2 行 | set **218.99 ms** |
| 10,116 | 追加第 3 行 | set **213.71 ms** |
| 50,614 | 首次设置 + `Pump(2)` | set 0.54 ms；pump **1,450.67 ms** |
| 50,614 | 另一次同量级运行 | 60 s 内未返回（CPU 57 s） |
| 101,304 | 首次设置 + `Pump(2)` | 25 s 内未返回（CPU 26.5 s） |
| 202,684 | 首次设置 + `Pump(2)` | 25 s 内未返回（CPU 26.8 s） |
| 2,000,000（单行） | 首次设置 + `Pump` | >6 min、CPU 374 s，外部终止 |

结论：`AppendUiLogLine` 的 `LogText.Text = next` + `CaretIndex = next.Length` 是 O(文本长度) 以上的操作。**2.4k 已经是每行 50 ms 级；10k 是 0.2 s 级；50k 是秒级；当前上限 2,000,000 完全不可用。**

### 2.3 格式切换与日志叠加

- 2.4k 日志已布局后：`FormatCombo` TIFF→PNG handler **6.41 ms**，`Pump(8)` **11.87 ms**；
- 10k 日志已布局后：handler **8.61 ms**，`Pump(8)` **13.08 ms**；
- 但 10k 日志的首次设置/布局本身已 **231 ms**，每追加一行又 **214~253 ms**。

所以：
- **handler 不慢**，慢的是它排在日志布局/追加任务后面；
- 格式切换还会让大 TextBox 参与窗口重新布局，进一步把 `LogText` 的长度成本带入切换路径；
- 用户看到的“菜单不收起”是 UI 队列被日志任务占住，不是 `FormatCombo` 内部逻辑死循环。

## 3. 根因排序

### P0（已测出、确定）

**`AppendUiLogLine` 全量重写 `LogText.Text`，且上限 2,000,000 字符。**
这是本轮唯一测到能造成 0.2 s~秒级~分钟级 UI 线程停顿的路径。只要主日志到 10k 量级，每次追加就是 0.2 s；到 50k 就是秒级；用户工作一轮/批量添加后很容易到这个量级。

放大因素：

- `UiLogService.Attach` 在 UI 线程直接调用 `AppendUiLogLine`（`438-442`），UI 线程日志没有排队/合并；
- `AppendUiLogLine` 每行还设置 `CaretIndex = next.Length`，会触发行尾滚动/重新布局；
- `QueueProcessor` 外部进程 stdout/stderr 逐行 `item.Log += ...; _onItemUpdated?.Invoke(item)`（`5386-5393`、`6101-6108`），`OnQueueItemUpdated` 再逐条 `Dispatcher.UIThread.Post`（`3490-3532`）；即使这些 post 不写主 `LogText`，它们也会把 UI 队列排满，让格式切换/重绘排在后面；
- 若“仅显示报错”开启，`OnQueueItemUpdated` 每条 post 都调用 `ApplyErrorOnlyFilter()`/`RefreshSimpleErrorFilter()`，重建整个 `ObservableCollection` 并替换 `ItemsSource`，这是另一条可放大的 O(队列长度) 路径。

### P0（代码级确定的硬阻塞）

**`ProbeCurrentSourceBitDepth()` 在 UI 线程同步 `ffprobe`。**
21 KB 文件实测 60~166 ms；`WaitForExit(3000)` 上限 3 s；超时后 `outTask.GetAwaiter().GetResult()` / `errTask.GetAwaiter().GetResult()` 仍在 UI 线程等待管道。工作过一次后 `_inputPath` 有值，才会真正启动 ffprobe；HDR 分支（`BT.2020 PQ`）会走到这里。截图中的 BT.2020 PQ 正是触发态。

### P1（不是当前主因，但应修）

- `UpdateOptionAvailability` 大量 `Items.Clear/Add/SelectedIndex`，每次切格式把 `ColorSpaceCombo` 重置到 auto（`2532`），会触发嵌套 `SelectionChanged`；`_suppressCommandRegen` 只抑制命令重建，不抑制这些嵌套 handler；
- `FormatCombo_SelectionChanged` 没有入口 `IsDropDownOpen=false`，也没有把重活延后到 popup 关闭之后；
- `RefreshEncoderListAsync` 缓存未命中时 `GetAllEncodersAsync` 在首个 `await` 前于 UI 线程 `Process.Start`（`EncoderDetectionService.cs:243-269`）。

## 4. 修复建议

### 4.1 先修日志（最高优先级，直接对应最大读数）

不要再用单个 `TextBox` 承载 2M 字符。两条可行路线：

**路线 A（推荐）：虚拟化日志列表**

- 把 `LogText` 换成 `ListBox`/`ItemsControl` + `VirtualizingStackPanel`，用环形 `ObservableCollection` 只保留最近 N 行（例如 5,000 行）；
- 后台日志只入有界队列，UI 侧每 50~100 ms 合并一次，最多取 200 行追加；
- 滚动只在新行到达时做一次，不再每行设置 `CaretIndex`。

**路线 B（改动小）：保留 TextBox，但严格限长 + 合并刷新**

- `UiLogTextMaxChars` 从 2,000,000 降到 **20,000~50,000** 量级；按本轮读数，50k 仍可能 1.45 s，所以如果保留 TextBox，建议先按 **10k~20k** 设上限，并配合合并刷新；
- `AppendUiLogLine` 改为“入队 + 一次 flush 赋值”，不要每行 `LogText.Text = next`；
- flush 时最多取 200 行拼一次，且只在 flush 时设置 `CaretIndex`/滚动；
- `UiLogService.Attach` 不再在 UI 线程直接调用 `AppendUiLogLine`，统一走队列；
- `UiLogService.Write` 的 `_sink` 调用移到 `lock(Gate)` 外，先取 sink 快照。

按本轮数据，**仅把 2M 改小而不做合并刷新仍会在 10k 时每行 0.2 s**，所以必须同时做批量/合并。

### 4.2 `ProbeCurrentSourceBitDepth` 移出 UI 线程并缓存

- 缓存键 `(inputPath, LastWriteTimeUtc, Length, ffprobePath)`；命中直接返回；
- 未命中时返回上次值/8，后台异步探测；结果回 UI 线程后，只有当前仍是 HDR 才应用；
- 用 `Process.WaitForExitAsync(ct)` + `CancellationTokenSource(3s)`，超时 kill 后在**线程池**排空 stdout/stderr；
- 绝不在 `ColorSpaceCombo_SelectionChanged` 里同步 `WaitForExit`/`GetResult`。

### 4.3 格式切换先关 popup、再延后重活

```csharp
private int _formatRefreshGen;

private void FormatCombo_SelectionChanged(...)
{
    if (FormatCombo != null)
        FormatCombo.IsDropDownOpen = false;

    var gen = ++_formatRefreshGen;
    Dispatcher.UIThread.Post(() =>
    {
        if (gen != _formatRefreshGen) return;
        UpdateOptionAvailability();
        RefillOutMatrixComboForFormat();
        _ = RefreshEncoderListAsync(gen);
    }, DispatcherPriority.Background);
}
```

并给选项重建加 `_suppressOptionEvents` 守卫，重建前保存 `ColorSpaceCombo.SelectedItem`，重建后按值恢复；不要每次切格式都重置到 auto。

### 4.4 队列通知合并

- `OnQueueItemUpdated` 改成每个 item 一个 pending 标志，最多 10 Hz 刷新；
- `QueueProcessor` 的 `_onItemUpdated` 从“每行一次”改成 dirty flag + 批量通知；
- `QueueItem.Log` 用 `StringBuilder`/环形缓冲，`+=` 换成 `AppendLog`，上限下调；完整日志落文件，UI 只留尾部；
- “仅显示报错”过滤不要在每条更新上重建集合；只在状态变化时做一次。

## 5. 尚未完成/下一步

1. **用户实际 `LogText.Text.Length` 尚未采集**：本轮启动后是 2,420；若实际会话仍停在这个量级，日志不是当前主因，`Probe` 的 3 s 超时更可能是；若已经到 10k 以上，日志是确定主因。建议在真实 GUI 里加一条启动/切换时的 `[ui-latency] logLen=... probeMs=... pending=...`，跑一次实际任务确认。
2. 真实桌面窗口的 GPU 渲染帧时间尚未测；无头读数只证明 UI 线程 CPU 成本量级。
3. 本轮未改 `src/`、未出包；beta15 包未受影响。
4. 一次性探针工程已从 `tests/projects/UiLatencyProbe` 删除；原始读数保存在 `tests/output/_tmp/ui-latency-probe-20261116/*.out.txt`。

## 6. 验收口径（修复后）

- `LogText.Text` 长度不超过新上限（推荐虚拟化后 5,000 行，或 TextBox 10k~20k 字符）；
- 单次日志追加/flush **P95 < 16 ms**；
- `FormatCombo_SelectionChanged` **P95 < 50 ms**；
- `ProbeCurrentSourceBitDepth` 在 UI 线程上的耗时恒为 0（只允许后台）；
- 连续切 10 次格式：popup 每次收起，窗口 100 ms 内重绘，不依赖切后台/切回来补刷新。
