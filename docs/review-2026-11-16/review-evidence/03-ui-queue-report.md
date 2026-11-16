# UI 卡死 / 并发 / 队列停止 / 执行日志契约 / oxipng 面板 审查报告（a9e2830..957cfa8）

## 0. 结论摘要

1. **最高级别 = MEDIUM**（本路无 CRITICAL/HIGH 实锤；一条门禁盲区按 spec §9 登记为 HIGH 候选，跨路口径由总控裁决）。
2. **诊断文档的根因与实际修复对得上**：两条 UI 线程硬阻塞均已拆除——`ProbeCurrentSourceBitDepth` 只剩缓存读（MainWindow.xaml.cs:4236-4244），ffprobe 走 `Task.Run`（:4316），UI 线程上全仓仅剩的两处 `GetAwaiter().GetResult()`（:4373-4374）在线程池函数 `ProbeSourceBitDepthBlocking` 内；`LogText.Text` 全仓唯一写入点是批量 flush（:1422），可见面是 `LogList` 虚拟化列表（XAML:1130）。修复文档 §1 的 5 条措施逐条可在代码里点名。
3. **取消/停止的判定不是唯一真值**：主编码分支已按 c213a51 统一抛 OCE→「已停止」（QueueProcessor.cs:1448-1449→1626-1630），但 cjxl/cjpegli/djxl 管道分支仍返回「已停止/超时」（:4835），与 `IsStopped => Status == "已停止"` 精确匹配（QueueItem.cs:224）不一致 ⇒ 停止在管道中途的项不会被重排队、反而进「仅显示报错」过滤（UI-01）。
4. **无重复 worker、无 CTS 复用**：`Start()` 先 Cancel 旧 CTS、等旧 runner ≤3s（QueueProcessor.cs:66-89），每轮新建 CTS（:87）；但优雅停止（`StopAfterCurrentQueue`）从不 Cancel，`IsRunning` 停后恒真（:35），简洁模式会误判队列运行中（UI-02）。
5. **oxipng 面板**：显隐判定只认输出格式（静态 PNG 显示、APNG 隐藏，MainWindow.xaml.cs:2802-2811），默认 `SelectedIndex=2`=max 与采集侧 `_pngOptimizerValues` 钳制兜底一致（:3331-3333），en/zh 两语 6 个 key 齐全；但该迁移**零 UI 断言**（UI-04 门禁盲区）。
6. **针对性门禁实测**：ui-mode 臂 59 PASS / 0 FAIL（rc=0）、ui-core 臂 84 PASS / 0 FAIL（rc=0），TMP 钉死在 `tests/output/_tmp/review-2026-11-16/ui/tmp`，日志在 `logs/ui/`。

## 1. 覆盖清单

| 文件 | 结论 | 发现 id |
|---|---|---|
| src/FfmpegGui/MainWindow.xaml | LogList 虚拟化列表 + LogText 隐藏镜像（1130-1140）；PngOptimizerPanel 移入 StillOptionsPanel 顶层（459） | —（布局正确）；UI-04（无断言） |
| src/FfmpegGui/MainWindow.xaml.cs（+563/−299） | 日志 flush/格式切换/HDR 位深/队列通知合并全部核查 | UI-02（间接）、UI-04、UI-05、UI-06 |
| src/FfmpegGui/Services/QueueProcessor.cs（+338/−72） | Start/Stop/CTS/取消分支全链路核查 | UI-01、UI-02、UI-03 |
| src/FfmpegGui/Services/UiLogService.cs（新增） | 统一入口、锁外 sink、按行展开、FileShare.ReadWrite | —（与 1a2c0da 契约一致） |
| src/FfmpegGui/Resources/Locales/en-US.json | +icc.version/tip，改 carry/recommended 文案；png.optimizer.* 6 key 在 | — |
| src/FfmpegGui/Resources/Locales/zh-CN.json | 同上，两语 key 齐全 | — |
| docs/review-2026-11-16/UI_FREEZE_DIAGNOSIS_2026-11-16.md | 根因排序 P0=日志 TextBox 全量重排 / P0=同步 ffprobe，与代码现状对照成立 | — |
| docs/review-2026-11-16/UI_FREEZE_FIX_2026-11-16.md | §1 五条措施逐条点名核对均已落地 | — |
| docs/review-2026-11-15/EXECUTION_LOG_AUDIT_2026-11-15.md | §8 修复记录与现状一致（Attach 不再分线程、统一前缀） | — |

## 2. 发现详情

### UI-01（MEDIUM）管道分支用户停止写成「已停止/超时」，不匹配 IsStopped 精确判定

1. **级别+id**：MEDIUM / UI-01
2. **位置**：`src/FfmpegGui/Services/QueueProcessor.cs:4835`（`PipeFfmpegToExternalEncoderAsync`）；判定真值 `src/FfmpegGui/Models/QueueItem.cs:224`；落地点 `QueueProcessor.cs:3648`、`:3708`
3. **现象**：取消时管道三处 `catch (OperationCanceledException)` 只杀进程不重抛（:4797、:4812、:4825），随后 :4832-4835 检查 `linkedToken.IsCancellationRequested` 并 `return (-1, "已停止/超时")`；调用方把它原样写进 `item.Status`。
4. **复现步骤**：代码分支级——选 cjxl/cjpegli 编码器跑 PNG/JPEG 转换，任务进行中点「停止」；外层 catch OCE（:1626）不会触发（管道已吞掉），Status 停在「已停止/超时」。
5. **证据**：`QueueItem.cs:224` `IsStopped => Status == "已停止"`（精确相等）；`QueueProcessor.cs:186` 重排条件 `item.IsStopped || (item.IsFailure && item.ExitCode != 0)`——「已停止/超时」两条都不满足 ⇒ 不重排；`MainWindow.xaml.cs:3755` `ApplyErrorOnlyFilter` 条件 `HasError || !IsCompleted`——该条 !IsCompleted=true ⇒ 反而出现在「仅显示报错」里。c213a51 只补了主 ffmpeg 分支（diff 实测仅 +4 行）。
6. **风险评估**：仅影响 cjxl/cjpegli/djxl 管道编码途中点停止的场景；用户可见症状 = 该项既不随「重新排队」复活、又被错误归类进报错过滤；状态口径与 c213a51 自述「统一标已停止」的语义不一致。不影响产物安全（finally 对 ExitCode=-1 走 TryDiscardPartialOutput，:1645-1647）。
7. **建议**：:4835 返回串改「已停止」（超时语义已在当行日志 `[{encoderTag}-pipe] 超时或取消` 里）；或把 `IsStopped` 改前缀匹配 `StartsWith("已停止")`。改后补一条 ui-end-to-end 断言。

### UI-02（MEDIUM）优雅停止后 IsRunning 恒真

1. **级别+id**：MEDIUM / UI-02
2. **位置**：`src/FfmpegGui/Services/QueueProcessor.cs:35`（IsRunning 定义）、`:259-262`（优雅停止 break）
3. **现象**：`StopAfterCurrentQueue` 走 `break` 退出消费循环，全程不 Cancel；`IsRunning => _cts != null && !_cts.IsCancellationRequested` 在循环退出后仍返回 true。
4. **复现步骤**：完整模式勾选「完成当前队列后停止」→ 队列自然跑完 → `_queueProcessor.IsRunning` 仍为 true；此时切到简洁模式点开始，`SimpleStartQueue_Click`（MainWindow.xaml.cs:5089）因 IsRunning=true 直接拒绝并提示「队列已在运行中，如需重启请先停止」。
5. **证据**：`MainWindow.xaml.cs:5386` `CheckAutoEncode` 同样判 `_queueProcessor.IsRunning`；`OnQueueStopped`（:3640-3648）只把 MainWindow 自己的 `_isQueueRunning` 置 false，不动 QueueProcessor 的 CTS。
6. **风险评估**：既有形状（非本批引入），但本批 c213a51/ee25a16 未收编；触发条件 = 用过优雅停止后走简洁模式/自动编码。用户看到「队列明明空了却不让开始」。
7. **建议**：`IsRunning` 改为 `_cts != null && !_cts.IsCancellationRequested && _runner != null && !_runner.IsCompleted`；或优雅停止 break 前把 `_cts` 置 null。

### UI-03（LOW）Start() 重启旧 CTS 不 Dispose

1. **级别+id**：LOW / UI-03
2. **位置**：`QueueProcessor.cs:66-70`
3. **现象**：重启路径 `_cts.Cancel(); _cts = null;`，无 Dispose（对照 `RebuildSemaphore` :96-101 旧信号量有 Dispose）。
4. **复现步骤**：代码通读——每次重复点「开始」泄漏一个 CTS。
5. **证据**：同上。
6. **风险**：频率极低，无功能影响。
7. **建议**：`var old = _cts; old?.Dispose()` 放 try/catch。

### UI-04（MEDIUM，HIGH 候选待总控裁决）门禁盲区：oxipng 面板显隐迁移零 UI 断言

1. **级别+id**：MEDIUM / UI-04（按任务书与 spec §9，是否升 HIGH 由总控裁决）
2. **位置**：`src/FfmpegGui/MainWindow.xaml.cs:2802-2811`；`src/FfmpegGui/MainWindow.xaml:459`
3. **现象**：c5f18e7 把 PngOptimizerPanel 从 PngCodecPanel 移到 StillOptionsPanel 顶层，并新增「静态 PNG 才显示」判定；全 `tests/projects` 树 Grep `PngOptimizer` 仅命中 `ServiceProbe/Parts/48_ProbePngOpt.cs`（服务层 BuildArgs/超时/路径探测），5 个 UI 臂工程零引用。
4. **复现步骤**：实测——本路跑 ui-core 臂（含 ui-panel-visibility）日志全文检索 `oxipng|PngOptimizer` 零命中；即「APNG 出口不该显示该面板」这一用户可见行为没有任何门禁可观测。
5. **证据**：`docs/review-2026-11-16/review-evidence/logs/ui/ui-core.out.txt`（84 PASS / 0 FAIL）；`git show c5f18e7`。
6. **风险评估**：未来若有人把该面板错误挂回动图出口，门禁永远绿；「切格式/混合队列时面板闪烁残留」同样不可观测。
7. **建议**：ui-panel-visibility 组补 2~3 条：PNG→IsVisible=true；APNG（动图模式）→false；`PngOptimizerCombo.SelectedIndex==2`。

### UI-05（LOW）停止后日志连出矛盾文案

1. **级别+id**：LOW / UI-05
2. **位置**：`MainWindow.xaml.cs:3571`（「队列已停止」）与 `:3645`（OnQueueStopped 无条件「队列已完成」）
3. **现象**：用户点停止后，执行日志先出「队列已停止」，runner finally 紧接着 Post「队列已完成」。
4. **复现步骤**：代码通读——`Stop()`→cts Cancel→ProcessAsync finally→`_onQueueStopped?.Invoke()`，无原因参数。
5. **证据**：同上。
6. **风险**：纯文案矛盾，无状态影响。
7. **建议**：OnQueueStopped 带原因参数，用户停止分支不写「队列已完成」。

### UI-06（LOW）旧代位深探测进程不取消；回灌 Post 无异常保护

1. **级别+id**：LOW / UI-06
2. **位置**：`MainWindow.xaml.cs:4315-4340`
3. **现象**：`_probeGeneration` 代际守卫只在回灌时丢结果；已 `Task.Run` 出去的 ffprobe（最长 3s+Kill）照跑；回灌委托整段无 try/catch。
4. **复现步骤**：快速连续切 BT.2020 PQ/HLG → 并发起多个 ffprobe；窗口关闭后回灌碰已释放控件异常无人接。
5. **证据**：`ProbeSourceBitDepthBlocking`（:4344-4379）无 ct 参数。
6. **风险**：短命进程、3s 上限，最坏残留几个 ffprobe；不会卡 UI（在后台线程）。
7. **建议**：探测带 ct（代际变化时终止 ffprobe）；回灌委托包 try/catch。

## 3. 门禁与负控实测

| 门禁 | 命令 | rc | 日志 |
|---|---|---:|---|
| ui-mode 臂（简洁模式/端到端/G2 慢任务停止/gainmap） | `Tests.UiMode.exe <tests/output/sources>`（TMP/TEMP/TMPDIR 钉 `tests/output/_tmp/review-2026-11-16/ui/tmp`，`FFMPEGGUI_PLAN_DIR=publish/PLAN`） | 0 | `logs/ui/ui-mode.out.txt`：`UI 测试汇总: 59 PASS / 0 FAIL`、`PROBE RESULT: pass=59 fail=0`；含 G2「停止后状态=已停止/子进程已死/无半截产物/队列无已完成项」4 条 PASS |
| ui-core 臂（格式→命令/面板显隐/编码器联动/预设往返） | 同上方式跑 `Tests.UiCore.exe` | 0 | `logs/ui/ui-core.out.txt`：`UI 测试汇总: 84 PASS / 0 FAIL`、`PROBE RESULT: pass=84 fail=0` |

- 未跑全量 5 臂 487（beta17 已绿，且任务书禁止全量）；未跑 `_run-step3-gates.ps1 -FullBaseline`。
- **负控/变异**：本轮未对判据做变异（只读审查红线）；ui-core 日志对 oxipng 零命中本身即「该断言不存在」的反证（UI-04）。
- **手工复现格式切换卡死**：未用打包 exe 开真实桌面窗口复现（无自动化桌面会话）；替代证据 = 无头宿主真实 MainWindow 的 O4/O5 组（晚到刷新后目标色域/位深/质量/所选后端保持，59 条内全 PASS），即 `_formatRefreshGen` 代际丢弃逻辑已被真控件链路覆盖。真实 GPU 渲染帧时间未测（诊断文档 §1 边界同此声明）。

## 4. 未验证项与原因

1. **打包 exe 真实桌面窗口手工复现格式切换**：未验证（无自动化桌面会话；headless 宿主已覆盖同一 UI 线程/同一 handler，但无头读数≠GPU 帧时间，诊断文档同一边界）。
2. **UI-01/UI-02 的运行时复现**：代码推断(未实测)——管道中途点停止、优雅停止后简洁模式启动两条路径未在无头宿主实际点一遍。
3. **日志 flush 的 P95 耗时**：未复测；修复文档 §2 的读数（2k 行 flush 424ms）引用自作者探针工程（已删除），本路只核对了代码结构（250/批、5000 行、4k 字符镜像）与之一致。
4. **「仅显示报错」过滤在批量刷新下的行为**：代码推断（FlushQueueUiUpdates 整批只重建一次集合，:3597-3600）。
5. `verify-ui-split-union` 的 7 PASS/4 FAIL 定性：按 spec §8 由 GATE 路裁决，本路不下结论。

## 5. 跨模块候选（只登记现象，不下结论）

- **oxipng 面板**同时影响 UI（本路）与打包/色彩路（oxipng 默认 max、acTL 跳过语义在 QueueProcessor:1498/1522 的 png/apng 分支）——面板显隐与服务端 acTL 跳过的一致性已在代码注释互指，但无共同断言。
- **停止状态文案**（「已停止」vs「已停止/超时」vs「队列已完成」）与 GATE 路的 G2 断言面（目前只锁主 ffmpeg 分支）相关。
- `UiLogService.ConfigureFile` 落盘路径与 IPC/CLI 日志口径是否同文件，未在本路展开。

## 6. 复现命令附录

```powershell
# TMP 钉死（审查区）
$env:TMP=$env:TEMP=$env:TMPDIR='C:\PLAN\ffmpegPictureUI\tests\output\_tmp\review-2026-11-16\ui\tmp'
$env:FFMPEGGUI_PLAN_DIR='C:\PLAN\ffmpegPictureUI\publish\PLAN'

# ui-mode（G2 队列停止等 59 条）
& 'C:\PLAN\ffmpegPictureUI\tests\projects\ui-mode\bin\Release\net11.0\win-x64\Tests.UiMode.exe' `
  'C:\PLAN\ffmpegPictureUI\tests\output\sources'
# rc=0；日志 docs\review-2026-11-16\review-evidence\logs\ui\ui-mode.out.txt

# ui-core（面板显隐等 84 条）
& 'C:\PLAN\ffmpegPictureUI\tests\projects\ui-core\bin\Release\net11.0\win-x64\Tests.UiCore.exe' `
  'C:\PLAN\ffmpegPictureUI\tests\output\sources'
# rc=0；日志 ...\logs\ui\ui-core.out.txt

# 队列停止语义（代码定位，无需运行）
& 'C:\Program Files\Git\bin\git.exe' -C C:\PLAN\ffmpegPictureUI show c213a51   # 主分支 +4 行取消收口
```
