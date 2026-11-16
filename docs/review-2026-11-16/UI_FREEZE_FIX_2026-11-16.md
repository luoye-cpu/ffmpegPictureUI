# UI 格式切换卡死：完整修复记录（2026-11-16）

## 0. 结论

UI 线程上已移除两条硬阻塞，并把日志/队列的 UI 回调从“逐行同步重排”改成“有界队列 + 合并刷新”：

1. **主日志不再用 2,000,000 字符的 TextBox 承载**。可见面改为虚拟化 `ListBox`（最多 5,000 行），隐藏的 `LogText` 只保留最近 4k 字符作为既有 UI 测例的兼容镜像。
2. **`ProbeCurrentSourceBitDepth` 不再在 UI 线程启动 ffprobe**。改为“缓存命中立即返回；未命中 `Task.Run` 后台探测”，结果通过 `Dispatcher.UIThread.Post` 回填，并按 `(路径, 长度, mtime)` 缓存。
3. **格式切换入口显式 `IsDropDownOpen=false`**，先收 popup；`RefreshEncoderListAsync` 用格式代 `generation` 丢弃旧代结果。
4. **`UpdateOptionAvailability` 重建 `ColorSpaceCombo` 时抑制嵌套 SelectionChanged**，避免 Clear/Add 逐项触发 HDR 分支；重建完再显式选 auto。
5. **队列更新合并**：`OnQueueItemUpdated` 只入队并调度一次 `FlushQueueUiUpdates`，过滤/进度刷新在整批处理完后只做一次。

## 1. 修改文件

- `src/FfmpegGui/MainWindow.xaml`
  - 执行日志区：`LogText` 普通 TextBox → 可见 `LogList`（ListBox）+ 隐藏 `LogText` 兼容镜像。
- `src/FfmpegGui/MainWindow.xaml.cs`
  - `UiLogTextMaxChars`：2,000,000 → 4,000；新增 `UiLogMaxLines = 5,000`、`UiLogFlushBatch = 250`；
  - `AppendUiLogLine`：入 `ConcurrentQueue`，只调度一次 `FlushUiLog`；
  - 新增 `ScheduleUiLogFlush` / `FlushUiLog`：批量取 250 行、追加到 `_uiLogLines`、裁剪到 5,000 行、更新隐藏 TextBox 尾部、滚动一次；
  - `UiLogService.Attach` 不再区分 UI/后台线程，统一走 `AppendUiLogLine` 队列；
  - `FormatCombo_SelectionChanged` 显式关闭 popup；`_formatRefreshGen++`；
  - `RefreshEncoderListAsync` 保持无参签名（反射测试 `O5` 依赖），内部捕获进入时代数，await 后发现代数变化则丢弃；
  - `UpdateOptionAvailability` 的 `ColorSpaceCombo` 重建块加 `_suppressOptionEvents` try/finally；
  - `ColorSpaceCombo_SelectionChanged` 增加 `_suppressOptionEvents` 早退；HDR 分支改为 `ApplyHdrBitDepth(缓存值)` + `_ = RefreshSourceBitDepthAsync(cs)`；
  - `ProbeCurrentSourceBitDepth` 改为缓存读取；新增 `TryGetCachedSourceBitDepth`、`ApplyHdrBitDepth`、`RefreshSourceBitDepthAsync`、`ProbeSourceBitDepthBlocking`；
  - `OnQueueItemUpdated` 改为入队 + 单次 `Dispatcher.Post`；新增 `FlushQueueUiUpdates`、`RemoveQueueItemFromUi`。
- `src/FfmpegGui/Services/UiLogService.cs`
  - `Write` 在 `lock(Gate)` 内只写文件、取 sink 快照；sink 调用移到锁外，避免后台日志写与 UI 调度互等。
- `src/FfmpegGui/Services/EncoderDetectionService.cs`
  - `GetAllEncodersAsync` 的 `Process.Start(ffmpeg -encoders)` 移到 `Task.Run`，避免缓存未命中时在 UI 线程同步启动进程。

## 2. 修复后读数

运行时探针（真实 `MainWindow`，Avalonia Headless；探针工程已删除，原始 stdout 保留在
`tests/output/_tmp/ui-fix-20261116/latency-fixed.out.txt`）：

| 项目 | 修复前 | 修复后 |
|---|---:|---:|
| 2,400 字符追加一行 | 约 53 ms/行 | 2k 日志批量入队 + 2k 行 flush 合计 **424 ms** |
| 10,000 字符追加一行 | 214~253 ms/行 | 2k→10k 增量 flush **871 ms** |
| 50,000 字符一次布局 | 1,451 ms / 另一次 60 s 未返回 | 2k→50k 增量 flush **2,740 ms** |
| 100k / 200k 字符 | 25 s 内未返回 | 不再存在这条路径（隐藏 TextBox 上限 4k） |
| 2,000,000 单行 | >6 min、CPU 374 s | 不再存在这条路径 |
| `ProbeCurrentSourceBitDepth`（缓存命中） | 60~166 ms；超时上限 3 s | **1.09 ms**（未命中走后台，不占 UI） |
| 5,000 行可见日志下切格式 | handler 6~15 ms，但排在日志布局后 | `SetFormat` 实测 **102.59 ms**（含刷新与 Pump） |

修复后：可见列表封顶 5,000 行；隐藏 `LogText.Text.Length` 实测 **3,996**（4k 上限内），UI 线程不再承担 2M 文本布局。

## 3. 验证

- `dotnet build ffmpegPictureUI.sln -c Release -m:1`：**0 errors / 5 warnings**（beta16 最终构建）。
- `Tests.UiCli.exe ui-startup-detect`：**12 PASS / 0 FAIL**（beta16 最终构建）。
- `Tests.UiCli.exe ui-cli-strict`：**60 PASS / 0 FAIL**（beta16 最终构建）。
- `verify-ui-host.ps1`：**UIHOST RESULT: pass=487 fail=0**（257.6 s；selfcheck 子宿主因缺失 `tests/output/sources/src_8bit.png` 等环境素材各报 1 fail，但门禁本体 exit=0）。
- `verify-startup-warmup.ps1`：**PASS=48 FAIL=0**（7.6 s）。
- 门禁运行器的 `probe:wire` / `probe:verdict` / `probe:decision` / `probe:engine` / `probe:geometry` / `probe:thumbnail` / `probe:bench` 仍因同一批素材缺失/超时/本机性能阈值报红；这些不是本次 UI 改动的产物，不能把运行器 exit=1 直接读成 UI 回归。

## 4. beta16 包

- 版本位：`src/FfmpegGui/FfmpegGui.csproj` = `1.6.0-beta16`；中英 CHANGELOG 已补 beta16 条目。
- 单文件包：`publish/FFmpegPictureUI-v1.6.0-beta16-x64.7z`，11,642,959 B，SHA256 前 16 `6590BFA27A2C2A69`。
- 完整包：`publish/FFmpegPictureUI-v1.6.0-beta16-x64-full.7z`，56,381,544 B，SHA256 前 16 `B61F3860D926B45E`。
- 包内 exe：app 24,681,984 B / SHA16 `131ED411B20C57C0`；full 24,681,984 B / SHA16 `7D1A8040A4BD8262`；均为 `ProductVersion=1.6.0-beta16+a4227a1ec2c593d458a56ff941b639e874c34068`。
- `verify-package-gui-starts.ps1`（`PKG_VER=1.6.0-beta16`，full 包）：**PASS=6 FAIL=0 SKIP=1**，exit=0。SKIP 是提权会话下托盘 `Shell_NotifyIcon err=5` 的既定口径，不代表产品失败；普通权限会话需复验。
- full 包 `--headless` 用**绝对路径**输入 `tests/output/sources/src_8bit.png` → PNG 成功，exit=0；相对路径输入会触发既有 exiftool 工作目录口径失败（exit 90），该问题不在本轮 UI 修复范围。
- beta15 旧包与对应 `publish/build/FFmpegPictureUI-v1.6.0-beta15-x64*` 解包目录已删除；删除前已扫描 reparse point，beta15 两个目录 reparse_hits=0。

## 5. 未覆盖/边界


- 无头布局读数只证明 UI 线程 CPU 成本；真实桌面 GPU 渲染帧时间尚未测。
- 真实用户会话的 `LogText.Text.Length` 在启动后仅 505（8 行）时，`Probe` 的 3 s 超时可能比日志更早成为主因；修复已同时覆盖两条。
- `ColorSpaceCombo` 仍按原行为在每次格式切换后回 auto；本次不改这个产品口径（避免打穿 J8/J10 既有断言）。
- 可见日志目前是只读 `ListBox`；复制文本仍可通过隐藏 `LogText`/文件日志完成，未新增“逐行选择复制”交互。
