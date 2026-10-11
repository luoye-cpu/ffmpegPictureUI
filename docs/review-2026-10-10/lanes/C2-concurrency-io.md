# C2 并发/线程/进程管理/IO 只读审查 —— 2026-10-10

HEAD = `c18dc4a`（工作树干净，审查全程未改源文件、未做 git 写操作、未起产品 exe 计时）。
本机口径：i7-13650HX，`Environment.ProcessorCount` = **20**（14 物理核），物理内存 **31.6 GB**。
载体更正：本仓 GUI 是 **Avalonia**（`MainWindow.xaml.cs` + `.xaml`/`.axaml`），不是 WinForms；下文按 Avalonia 口径读 `Dispatcher.UIThread`。

---

## 0 一页结论

| # | 结论 | 标签 | 一句话判据 |
|---|---|---|---|
| C2-1 | **引擎线的托管行带并行度恒 = 全机核数，与 `-j` 无关** ⇒ `-j N` 时 N×核数超饱和在 HEAD **仍成立**，但机制不是 P4 说的那条 | 性能项（静态可推，需实测定量） | `QueueProcessor.cs:1413-1415` 硬编码 `1` ⇒ `ColorScheduler.ForItem(1,…)` ⇒ `Dop = ComputeAdaptiveThreads(1) = 20` |
| C2-2 | **引擎线（默认执行路径）的两个 ffmpeg 子进程完全没有挂死守卫** —— 既无心跳也无超时，只靠调用方 `ct` | 真缺陷-静默失效（覆盖面，非产物） | `RawColorPipeline.cs:1322-1341` `RunToEndAsync` 全文无 `CancelAfter`/`Task.WhenAny`/`-progress`；6 个站点（:132/:236/:314/:319/:761/:1266/:1302）全走它 |
| C2-3 | `RunToEndAsync` 的排空顺序是**先 stderr 到 EOF、再 stdout** ⇒ 子进程写 stdout 即双向阻塞；**现有两条探针结构上都测不到这一形** | 未复现（形态在、当前无 stdout 写方）＋探针覆盖缺口 | `:1338-1340`；`_probe-stderr-drain-scan` 只判 stderr 有无人排（实测 60/60 PASS），`_probe-sync-read-before-wait-scan` 显式把 `ReadToEndAsync` 排除在判据外 |
| C2-4 | **UI 无节流**：每行 ffmpeg 输出 ⇒ 一次 `Dispatcher.UIThread.Post` ⇒ 勾「仅显示报错」时**每次 Post 全量重建两个 ObservableCollection** | 性能项（静态口径可推） | `QueueProcessor.cs:1080-1084` + `:3205-3208` + `:3291-3301` + `:4763-4769` |
| C2-5 | 「开始」按钮**无 `IsQueueRunning` 守卫**，且 `Start()` 在 UI 线程上同步 `prev.Wait(3 s)` | 真缺陷-静默失效（在跑项被静默取消）＋UI 冻结 | `MainWindow.xaml.cs:3154-3157` 直调 `StartProcessing()`；`QueueProcessor.cs:71` |
| C2-6 | 内存钳制的口径是**物理内存总量**而非可用量 ⇒ 本机算出的上限是 168 / 42，`-j ≤ 42` 时钳制恒不生效；**CPU 轴仍缺**（P4 复验成立） | 真缺陷-静默失效（守卫恒真） | `QueueProcessor.cs:146-149` 用 `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` |
| C2-7 | 全仓**无 Job Object**，杀树靠 `Kill(true)` 逐 pid 递归 | 未复现（本仓无 pid 消费面） | `grep JobObject\|CreateJobObject\|AssignProcessToJobObject` = 0 命中；正向对照 `Kill(true\|entireProcessTree)` 命中 20 处 |
| C2-8 | `BufferPool` >256 MB **走直配、不会被钳小**；`RawColorPipeline.cs:1134` 与 `docs §7 P7` 的「会被钳小」说法与实现矛盾 | 未判定（文档漂移，行为无缺陷） | `BufferPool.cs:20-21` vs `RawColorPipeline.cs:1134-1136` |
| C2-9 | `SetSafePriority` 允许 `RealTime`（level 0）⇒ 与 C2-1/C2-2 的心跳判死互相作用（RT 子进程饿死本进程线程池 ⇒ 合法任务被误判挂死） | 未判定（需实测） | `PlatformServices.cs:423-441`；`AppSettings.cs:83` 默认 3 |

旧账复验：**P4 的三个行号锚点全部未漂移**，但 P4 的因果写反了一处（见 §2）；**HANDOVER `:345`「其余 20 处未启用心跳」在 HEAD 已过期**（心跳改成默认 opt-out，27/27 站点在面上），真正未覆盖的是**不经 `FfmpegRunner` 的方法级站点：60 个里有 27 个既无超时也无心跳**（见 §2.3 表）。

---

## 1 方法与正向命中自证

只做了三类动作：读源、跑既有的**只读探针**、`ffmpeg -h` / `-loglevel verbose` 读参数默认（不起产品 exe、不计时）。

### 1.1 四条既有探针实测读数（`-Report` 全部指到仓外 `C:\temp\c2probe\`，未写任何仓库文件）

| 探针 | HEAD 读数 | 基线 | 判定 |
|---|---|---|---|
| `_probe-stderr-drain-scan.ps1` | `RedirectStandardError=true` 声明 **60** 处，每个用到它的方法都排空 | — | **PASS** |
| `_probe-sync-read-before-wait-scan.ps1` | A 类直连 `Process.Start(` **62** 处（参与判据 61/跳过 1）+ B 类 `new Process{}` **7** 处；命中 **0/0**；观察项 0；负控 8/8 | A≤0、B≤0 | **PASS，余量 0**（即该锁已到「必须为 0」） |
| `_probe-ct-chain-closure-scan.ps1` | 判据面内站点 **66**，命中 **0**；跳过：目标无 ct 形参 1743 / 调用点不持有 ct **17** / 同名不唯一 14 名 / 方法头 1132 / 括号不配平 **124**；负控 7/7 | 命中≤0 | **PASS，余量 0**；⚠ 124 处「判不了」与 17 处「不持有 ct」是本锁的两块盲面（C2-3 正落在这两块之外、见 §1.3） |
| `_probe-cancel-propagation-scan.ps1` | `FfmpegRunner.RunAsync` 调用点 **27**（已传 ct 26 / 豁免 1 / 漏传 **0**） | 漏传=0 | **PASS** |

⇒ 「ct 断点还剩几处」这一问的定案读数：**在两条既有锁的判据面内 = 0 处**。面外余量是「隔跳 ③a」与「全链无 ct ③b」两类，本锁**不表态**（`_probe-ct-chain-closure-scan.ps1` 文件尾自己写明）。C2-3 的 `RunToEndAsync` 与 `MediaInfoService`/`ExifToolService.WriteMetadataAsync` 的无超时 `WaitForExitAsync()` 属 ③b 类，**没有任何一条锁钉着**（见 §3.3）。

### 1.2 正向命中自证（每条「没有」都先跑一次「有」）

| 否证主张 | 否证 grep | **正向对照 grep（必须命中）** |
|---|---|---|
| 引擎的两条 ffmpeg 命令里没有 `threads` | `grep -c threads RawColorPipeline.cs` = **0** | `grep -c -- "-loglevel\|-vf\|rawvideo" RawColorPipeline.cs` = **24** |
| 全仓没下发 `-filter_threads`/`-filter_complex_threads` | `grep -rn filter_threads src/` = **0**（仅 `docs/` 与 `tests/output/` 的账本命中） | `grep -rn '"-threads"' src/` = **FfmpegCommandBuilder.cs:99** |
| 全仓没有 Job Object | `grep -rn "JobObject\|AssignProcessToJobObject" src/` = **0** | `grep -rn "Kill(true\|entireProcessTree" src/` = **20 处** |
| `PreConvertToPngAsync` 是死代码 | `grep -rn PreConvertToPng src/` = **仅 :3277 定义行** | `grep -rn ProbeBitDepthAsync src/` = 定义 :3209 **+ 调用 :2791** |
| 按 pid 切日志段不存在（⇒ pid 复用无消费面） | `grep -rn "process.Id\|proc\.Id\|\[pid" src/` = **0** | 同轮 `Kill(true)` grep = 20 命中（证明 grep 通到该文件） |

### 1.3 自制的第三个扫描（补既有锁的缺口，只读）

`_probe-*` 三条都不问「**有没有超时/心跳**」，只问「排空/顺序/传没传 ct」。故自写一个方法级守卫扫描（落 `C:\temp\c2probe\guardscan.ps1`，仓外），对 `src/FfmpegGui` 84 个 .cs 按方法切段，统计含进程启动站点的 60 个方法的 T/K/CT/HB/RUN 五面。

**必须声明该扫描自身的口径与两处失真**：
- `T`（超时）认：`WaitForExit(<数字>` / `Task.WhenAny` / `.WaitAsync(` / `CancelAfter` / `new CancellationTokenSource(TimeSpan…)` / `new CancellationTokenSource(<数字>` / `WaitGuardedAsync`。
- 第一版漏了 `new CancellationTokenSource(TimeSpan.FromMinutes(30))` 这种构造式超时（`RawColorPipeline.cs:806` 就是这一形），已修正后重跑 ⇒ 下方表是**修正后**的读数。
- `RUN`/`HB` 的模式含 `RunToEndAsync` 自身 ⇒ `RawColorPipeline.cs:1328` 那行 `RUN=1` 是**自指假命中**，该方法的 T=0/HB=0 才是结论。
- **已抓到的两处口径失真（必须写出来，别让它伪装成干净）**：
  ① 第一版 `T` 不认 `new CancellationTokenSource(TimeSpan…)` 构造式超时（`RawColorPipeline.cs:806` 就是这一形）⇒ 已修正后重跑，下方表是修正后的读数。
  ② `T` 的模式要求 `WaitForExit(` 后**紧跟数字** ⇒ `QueueProcessor.cs:6010 proc.WaitForExit(timeoutSec * 1000)`（变量表达式）**被判成 T=0，是假阴**；该行已在表内手工更正。
  ⇒ 因此下表的「无超时」计数**偏保守**（真实值只会更小）；引用这些数字时按「形态站点上界」读，不按精确计数读。
- 静态计数，**不证明任何一处会挂死**，只给出「具备该形态的站点数」。

---

## 2 与 2026-10-09 P4 / 旧守卫清单的复验表

### 2.1 P4 四个锚点

| 账本原话（`docs/PIPELINE_PERF_ANALYSIS_2026-10-09.md:144-146`） | HEAD 行号 | 复验结论 |
|---|---|---|
| `FfmpegCommandBuilder.cs:99-100` 无条件下发 `-threads` | **99-100 未漂移**：`:98 // 线程：总是传递`｜`:99 args.Add("-threads");`｜`:100 args.Add($"{options.Threads}");` | 成立 |
| 色彩引擎手搓的两条 ffmpeg 命令 grep `threads` 零命中（`RawColorPipeline.cs`） | 引擎 ffmpeg 站点在 `:132-133`（解码）与 `:235-236`＋`BuildEncodeArgs`（`:927`）——**`:132` 与 `BuildEncodeArgs` 全文零 `threads`** | 成立；「两条」的措辞应更正为「**引擎唯一的子进程出口 `RunToEndAsync` 及其全部 6 个调用站点**」 |
| 队列自适应线程（`QueueProcessor.cs:263-266`）只喂 legacy 线 | **263-266 未漂移** | 成立，但**喂到的面比"legacy 线"更宽**：`Options.Threads` 同时被 `CjxlService.cs:452`、`CjpegliService.cs:287`（`--num_threads`）、`RawService.cs:178/251`（dngtool `-threads`）、`QueueProcessor.cs:3854`（avifenc `-j`）、`MainWindow.xaml.cs:1519` 消费 —— **只有引擎的两条 ffmpeg 命令拿到 0** |
| 全局只有内存钳制 `ClampConcurrencyByMemory`(:142-152)，无 CPU 钳制 | **142-152 未漂移** | 成立。补一条：该钳制在 HEAD **恒不生效**（见 §0 C2-6） |

**P4 有一处因果写反**（会误导下一次的改法）：账本写「`-threads` 放在解码段影响的是解码器线程」。HEAD 实测 `-i` 在 `:80-81`、`-threads` 在 `:99` ⇒ 它是**输出侧选项 ⇒ 管的是编码器线程**；`ffmpeg -h full` 里 `-threads <int> ED.VA (default 1)` 也印证 E/D 两态由**位置**决定。真正的解码器线程轴与滤镜图线程轴（`-filter_threads`）在 HEAD **一条都没设**。

### 2.2 HANDOVER 旧心跳清单（`docs/HANDOVER.md:344-345, 426, 558, 566`）

| 旧账 | HEAD 读数 | 判定 |
|---|---|---|
| 「JXL 链 **4** 处显式开启、其余 **20** 处未启用（= 24 个调用点）」 | `FfmpegRunner.cs:57` `bool heartbeat = true`（**opt-out**，`#15-b` 类级修法）⇒ HEAD **27 个 `RunAsync` 调用点全部默认在心跳面上**；4 处 `heartbeat: true` 字面量仍在（`QueueProcessor.cs:4038/4056/4148/4162`，`QueueProcessor.cs:4580` 自述为「JXL 链的文档锚点」）。全仓**无任何调用点传 `heartbeat: false`**（`grep heartbeat` 在非 `FfmpegRunner.cs` 只有那 4 处 + `:4580` 注释） | 「20 处未启用」= **已过期，应划掉**；HEAD 的启用/未启用不是按站点算的，而是按**能不能注入**算的（见下行） |
| — | 真正决定生效面的是 `CanInjectProgressPipe`（`FfmpegRunner.cs:231-239`）：目标名不含 `ffmpeg`、或参数含 `-progress`/`image2pipe`/`pipe:1`/裸尾 `-` ⇒ **拒绝注入并响亮登记**，退回旧双信号判据（`:179-203`，对忙等型失明）。当前会拒绝的实形：`RawColorPipeline.cs:598/604` 与 `QueueProcessor.cs:3381` 的 `-f image2pipe … -` —— 但这三条**都是手搓 `Process.Start`，不经 `FfmpegRunner`**，所以拒绝分支在生产上**打不到**（`FfmpegRunner.cs:228` 自述） | **未复现**（判据面为空）；风险登记：一旦有人把这三条改走 `FfmpegRunner`，就会落进「无心跳」分支且**只有日志可见** |
| — | `RunAsync` 27 站点中，`ffmpegPath` 实参一律是 `AppSettingsService.Current.FfmpegPath`（= `…/ffmpeg.exe`）⇒ `IsFfmpegTarget`(`:249-257`) 恒真。已知残余：用户改名二进制 ⇒ 心跳被跳过（`:246-247` 自述，响亮日志） | **未复现** |

### 2.3 挂死守卫覆盖面表（HEAD 数出来的，静态计数）

方法级站点 **60** 个含进程启动；三面都缺（无超时 **且** 无心跳 **且** 不经 `FfmpegRunner`）= **27** 个（**上界**，见 §1.3 的口径失真 ②）；无 Kill = 15 个；无 ct = 22 个。

**按「是否在队列每任务路径上」列成表**（✓=有该面，✗=无）：

| 站点（文件:方法首行） | 队列路径 | T 超时 | K Kill | CT ct | HB 心跳 | 归属 |
|---|---|---|---|---|---|---|
| `Services/ColorMapping/RawColorPipeline.cs:1322 RunToEndAsync`（引擎 6 站点唯一出口，启动站点在 `:1328`） | **是（默认路径）** | ✗ | ✗ | ✓ | ✗ | **C2-2 真缺陷-覆盖面** |
| `Services/ColorMapping/RawColorPipeline.cs:805 PipeToEncoderAsync` | 是（cjxl 出口） | ✓ 30 min | ✓×5 | ✓ | ✗ | 已登记；⚠ 30 min 槽位占用 |
| `Services/QueueProcessor.cs:3541 PipeFfmpegToCjxlAsync` | 是 | ✓ | ✓×5 | ✓ | ✗ | 有超时无心跳 |
| `Services/QueueProcessor.cs:4440 PipeDjxlToCjpegliAsync` | 是 | ✓ | ✓×5 | ✓ | ✗ | 同上 |
| `Services/QueueProcessor.cs:4716 PipeDjxlToFfmpegAsync` | 是 | ✓ | ✓×5 | ✓ | ✓(RUN) | 闭合 |
| `Services/QueueProcessor.cs:5466 InputMayHaveMultipleFramesAsync` | 是 | ✓ | ✓ | ✓ | ✓(RUN) | 闭合（`#136` 补的看门狗） |
| `Services/QueueProcessor.cs:5600 ProbeAnimatedWebpAsync`（同机制） | 是 | ✓ | ✓ | ✓ | ✓(RUN) | 闭合 |
| `Services/QueueProcessor.cs:5981 ProbeAnimatedJxl`（启动站点 `:6002`） | 是 | ✓ `WaitForExit(timeoutSec*1000)`（`:6010`，**我脚本漏认，见下**） | ✓ 杀树(`:6013`) | ✗（无形参 ct） | ✗ | 已闭合（`#15` 的同步读改异步读那一修）；取消面属 ③b「全链无 ct」 |
| `Services/GainMapEncoder.cs:468/508/669`（3 站点） | 是（GainMap 路） | ✗ | ✗ | ✓ | ✗ | 只靠调用方 ct |
| `Services/GainMapDecoder.cs:1307 RunFfmpegAsync` | 是（UltraHDR 输入） | ✗ | ✗ | ✓ | ✗ | 同上 |
| `Services/CjpegliService.cs:187 RunWithOptionsAsync` | 是 | ✗ | ✓×2 | ✓ | ✗ | ⚠ 超时在 helper（`_probe-sync-read-before-wait-scan` 缺口①自认判据落空） |
| `Services/DjxlService.cs:149`／`JxrService.cs:175`／`RawService.cs:314` | 是 | ✗ | ✓ | ✓ | ✗ | 无心跳无超时 |
| `Services/ExifToolService.cs:1156 ReadMetadataAsync`（启动站点 `:1184`）/ `:1237 WriteMetadataAsync`（`:1279`） | 否（元数据编辑器 / 详情窗，调用点已包 `Task.Run`） | ✗ | ✗ | ✗ | ✗ | 两处**裸 `await p.WaitForExitAsync()`**（`:1195` / `:1289`），无 ct、无超时 |
| `Services/MediaInfoService.cs:29 GetMediaInfoAsync` | 否（详情窗/预览） | ✗ | ✗ | ✗ | ✗ | **三处**裸 `WaitForExitAsync()`（`:34` / `:66` / `:87`） |
| `Services/QualityAnalysisService.cs:339/419` | 否（质量分析） | ✗ | ✗/✓ | ✗/✓ | ✗ | 分析链另有 30 s CTS（`HANDOVER:792` 已登记） |
| `Services/EncoderDetectionService.cs:254/497/633` | 启动期一次 | ✗ | ✗ | ✗ | ✗ | 探测缓存后不重复 |
| `Services/FormatCapabilitiesService.cs:265` | 启动期一次 | ✗ | ✗ | ✗ | ✗ | 同上 |
| `Services/GpuCapabilityService.cs:431/473` | 启动期/UI 空闲 | ✓ | ✓ | ✗ | ✗ | 有超时+Kill |
| `Services/ExternalToolsDetector.cs:58/124/524/897/929/963/996` | 工具检测 | 混合(4 处 ✓) | ✓ | ✗ | ✗ | 非队列路径 |
| `MainWindow.xaml.cs:3800/5360` | UI | ✓/✗ | ✓/✗ | ✗ | ✗ | `:3800` 是同步 `GetAwaiter().GetResult()` 系（见 §3.6） |

**汇总数**（口径：方法级、静态计数，误差不影响方向）：`FfmpegRunner` 心跳面 = 27 站点（HEAD 全默认开）；**队列路径上 T=0 且 HB=0 的方法共 9 个**（`RunToEndAsync` 一个方法背后是 6 个引擎 ffmpeg 站点；`GainMapEncoder` 3 站点；`DjxlService`/`JxrService`/`RawService` 各 1；`CjpegliService`/`GainMapDecoder` 各 1）—— 这是**按上表逐行数出来的**，不是靠 27 那个自动化总数。旧账「4 启用 / 20 未启用」换成 HEAD 口径应是「**27/27 在心跳判据面上，另 60 个方法级站点全部不在**」。

---

## 3 新发现

### C2-1 ★ 引擎线的 `Dop` 与 `-j` 脱钩 —— HEAD 真正在超饱和的那一条轴（性能项）

```
QueueProcessor.cs:1413            // 并发暂取 1：DOP 属于调度层话题，接线阶段先把"结果对不对"钉住再谈吞吐
QueueProcessor.cs:1414-1415       var st = await ColorMapping.RawColorPipeline.TransformFileAsync(
                                      inputPath, outputPath, plan.ToSpec(), plan, s => Log(s), ct, 1,
```
`RawColorPipeline.cs:43` 形参 `int concurrency = 1` ⇒ `:157 ColorScheduler.ForItem(concurrency, w, h, 6)` ⇒ `ColorScheduler.cs:44`
`Dop = max(1, ComputeAdaptiveThreads(1))`；`FfmpegOptions.cs:662-666` `ComputeAdaptiveThreads(1)` = `ProcessorCount/1` = **20**；
`ColorScheduler.cs:88-89` `Parallel.For(0, bands, new ParallelOptions{MaxDegreeOfParallelism = 20})`。

⇒ 队列 `-j 8` 且 8 项都走引擎 ⇒ **8 × 20 = 160 个 CPU-bound 工作项**抢 20 个逻辑核；`-j` 到 128（UI 上限 128，`MainWindow.xaml.cs:1802`；`QueueProcessor.cs:50 SetConcurrency` **无上限**）⇒ 128 × 20。

**这与 P4 是同一条症状、不同一条机制**：`Options.Threads` 那条轴（自适应 `20/8=2`）确实只喂 legacy/cjxl/cjpegli/dngtool/avifenc；而引擎自己的**托管**那条轴根本没接队列并发。`ColorScheduler.cs:10-12` 的设计自述写的是「队列并发（`QueueProcessor._concurrency` + 其内存钳制）与 `ComputeAdaptiveThreads` 已经决定了每个任务能用多少核，本类只是把那份份额用掉，**不做二次限流**」⇒ **调用点传 1 直接违约**：拿到的是全机份额。

- 预计收益（**静态推算**）：`-j 8` 引擎批的墙钟改善上限取决于行带内核占单任务的比例，**未知** ⇒ 只能给口径：`ColorScheduler.ToLogFragment()` 每项打一行 `dop=20`（`:97-98`），改前后各跑一批数一下 `dop=` 与墙钟即可。误差方向：**高估收益**（若行带内核远快于 ffmpeg 解码/编码，改 Dop 几乎不动墙钟）。
- 判据：同一批 `-j 8` 改前/改后墙钟 + 日志里 `dop=` 从 20 变 3（`20/8`）；产物逐字节必须不变（`RunBands` 写的是互不重叠的行带，`Dop` 只影响切法 ⇒ `TileRows=0` 时 `rows = ceil(rowTotal/Dop)`、带数 = Dop，改 Dop 会改**行的分组** ⇒ **必须先证实 `ColorKernels.TransformRgb48le*` 逐行无跨行状态**，否则这就是改产物）。
- 风险：**中-高**。`body(start, rowCount)` 里用了 `w, rowStart * w`（`:1110` 传 `rowStart*w` 作为「首行全局像素偏移」，说明内核确实按行带定位）；带数变化只平移偏移，理论上产物不变 —— 但要靠 A/B 逐字节证明，不能靠推理。
- 需要的机器条件：**Temp 可写**（`docs §6` 的引擎路被 `%TEMP%` 拦截打死 ⇒ 本机现在跑不了引擎批）。这是本项的**唯一硬阻塞**。

### C2-2 ★ 引擎子进程零挂死守卫（真缺陷-静默失效，覆盖面）

```
RawColorPipeline.cs:1322  private static async Task<int> RunToEndAsync(string exe, string args, Action<string>? log, CancellationToken ct)
:1337                       string err = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
:1338                       await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
:1339                       await p.WaitForExitAsync(ct).ConfigureAwait(false);
```
6 个站点全走它（`:132` 解码、`:236` 编码、`:314`/`:319` GainMap 解码重试、`:761`、`:1266`、`:1302`）。**没有** `CancelAfter`／`Task.WhenAny`／`-progress`／`Kill` —— 三面（T/K/HB）皆空，只有 `ct`；且取消时 `WaitForExitAsync(ct)` 抛 OCE 后**不 Kill 子进程**（靠 `ReadToEndAsync(ct)` 关管道造成的破管理论上让它自己退）。⇒
1. 忙等型挂死（`HANDOVER` #15 那一形：畸形 `.jxl` 让解复用器空转）**在引擎线上无判死**，只等用户点「停止」；一个槽位永久占住。
2. `FfmpegRunner` 的 `-124 HangExitCode` 在引擎线**永远出不来** ⇒ 引擎挂死的任务与「用户取消」在退出码上**不可区分**（都走 `OperationCanceledException` ⇒ `Status="已停止"`）。这**削弱错误归因**，正好撞维护者判据。
- 预计收益：不是吞吐，是**挂死不再占用槽位 + 归因可分**。静态口径：`FfmpegRunner` 的默认判死是 120 s（`:35 DefaultHeartbeatTimeoutSec`）vs 引擎 = ∞。
- 判据：畸形输入（1 B `.jxl`）走 `--color-engine engine` 必须限时退非 0 且日志点名「无进度心跳」；合法 4K 单帧 `libjxl -effort 9` 不被误杀 —— 与 `tests/scripts/verify-ffmpeg-heartbeat.ps1` ②③ 同一格，只是把站点换成引擎。
- 风险：**中**。解码那条写的是 `-f rawvideo "{tmp}"`（**落文件，stdout 不承载数据**）⇒ `-progress pipe:1` **可以**安全注入；真正不能注入的是 `:598/604` 与 `QueueProcessor.cs:3381` 那三条 `-f image2pipe … -`，而它们走的是手搓 `Process.Start`（`PipeToEncoderAsync`）、不经 `RunToEndAsync` ⇒ **逐站点判，别一把梭**。
- 机器条件：需 Temp 可写。

### C2-3 排空顺序的经典死锁形 + 既有探针的覆盖缺口（未复现，登记为缺口）

`RawColorPipeline.cs:1337-1338`：**先把 stderr 读到 EOF，再读 stdout**。子进程若在 stderr 未关之前往 stdout 写满管道缓冲（Windows 匿名管道 ~4 KB；同文件 `:848` 的注释自己写的是 64 KB 量级并记录了实测 5.2 MB 流阻塞），两侧互等 ⇒ 永久卡住，且**没有任何超时**（C2-2）⇒ 只能靠取消。
HEAD 判定：**当前无 stdout 写方 ⇒ 不会真卡**（6 个站点的命令都是写文件，`grep` 无 `-` 尾参 / `pipe:1`）⇒ 标签「未复现（形态存在）」。
**探针缺口点名**（这是可交付的结构性结论）：
- `_probe-stderr-drain-scan.ps1` 判据是「`RedirectStandardError = true` 的声明数 ≤ 排空动作数」⇒ `:1337` 一句 `StandardError.ReadToEndAsync` 正好把它喂绿，**它不问 stdout**。
- `_probe-sync-read-before-wait-scan.ps1` 文件头显式写「`ReadToEndAsync()` **不**参与判据」⇒ 这一形正好从缝里过去。
⇒ 需要第 4 条锁：同一方法内「`RedirectStandardOutput = true`」的声明数 ≤ stdout 排空动作数（与现有 stderr 锁对偶）。**这条我只给判据，不新写探针**（越界）。

### C2-4 ★ UI 无节流：每行子进程输出 ⇒ 一次 O(N) 全量重过滤（性能项）

`QueueProcessor.cs:1080-1084`（主编码路径）：
```
exitCode = await FfmpegRunner.RunAsync(args, s =>
{
    captured.Log += s;
    _onItemUpdated?.Invoke(captured);
}, AppSettingsService.Current.FfmpegPath, ct);
```
⇒ `MainWindow.xaml.cs:3171 OnQueueItemUpdated` 每次 `Dispatcher.UIThread.Post`，`:3205-3208`（**行号未漂移**）：
```
if (ShowErrorsOnlyCheck?.IsChecked == true)
    ApplyErrorOnlyFilter();
if (SimpleErrorsOnlyCheck?.IsChecked == true)
    RefreshSimpleErrorFilter();
```
`ApplyErrorOnlyFilter`（`:3291-3297`）与 `RefreshSimpleErrorFilter`（`:4763-4769`）都是 `new ObservableCollection<>(_queueView.Where(...))` + 重赋 `ItemsSource` ⇒ **每次 O(N) 枚举 + 整列表重排**。
- 静态计数：QueueProcessor 里 `_onItemUpdated?.Invoke` = **159** 处；其中 **≥20 处**在子进程输出回调 lambda 里（`grep "Log += s; _onItemUpdated"` 命中 20+，主路径 :1082 是其一）。
- 预计收益（**静态推算，口径**）：N 张 × 每张 L 行 ffmpeg 输出 × 每次全量过滤 O(N) ⇒ UI 线程工作量 **O(N²·L)**；「改前/改后」的尺子是「点开始到第一项 `[cmd]` 落日志的墙钟」和 UI 线程繁忙率，不是产物。误差方向：L 未知 ⇒ 只给量级，不给秒数。
- 判据：`P3` 账本给的判据（N=20 vs N=100 呈线性下降）可直接复用；再加一条「勾着『仅显示报错』跑 100 张，`Dispatcher.UIThread` 队列长度不出现锯齿」。
- 风险：**低但有名** —— 节流会改变「更新合并」的时序，`QueueItem.Log` 的增量拼接不变量不依赖更新时序（`QueueItem.cs:52/72-97` 是**快照长度 + 锁内只接增量**），但 `verify-ui-*` 族里若有按更新次数计数的断言会转红。
- 机器条件：本机即可（UI 批测），无需 Temp 解堵。

### C2-5 「开始」无幂等守卫 + UI 线程同步 `Wait(3 s)`（真缺陷-静默失效 + UI 冻结）

`MainWindow.xaml.cs:3154-3157`：
```
private void StartQueue_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
{
    StartProcessing();
}
```
`StartProcessing()`（`:3117`）**不查** `_isQueueRunning` / `QueueProcessor.IsRunning`；对比 `IpcService.cs:397-398` **查了**（`if (added > 0 && !mainWindow.IsQueueRunning) mainWindow.StartProcessing();`）⇒ 同一件事两条入口一条有门一条没门。
`QueueProcessor.cs:59-73`：`Start()` 先 `_cts.Cancel()`（⇒ 全部在跑项被取消 → `catch (OperationCanceledException)` → `Status="已停止"`，`QueueProcessor.cs:1196-1199`），再 **UI 线程上 `prev.Wait(TimeSpan.FromSeconds(3))`**。
⇒ 用户运行中再点「开始」= 在跑的活儿全部静默改判「已停止」并重新排队（`RequeueStoppedAndFailed`，`:158-172`），且界面**卡住最长 3 秒**。
次生窗口：3 s 后不管旧任务有没有退干净都会 `RebuildSemaphore()` → `old.Dispose()`（`:88-93`）⇒ 旧任务 `finally` 里的 `sem.Release()`（`:1221`）抛 `ObjectDisposedException`；因状态已在 `catch` 里落好（`:1196-1205` 早于 `finally`），**不会留下「处理中」僵尸**，但异常只被旧循环 `catch {}`（`:1235`）吞掉 ⇒ 静默。
- 判据：运行中二次点「开始」应 no-op 且 UI 不冻结 >100 ms；`_running` 那个字段（`:18`）**全仓零使用**（`grep _running` = 1 命中，即声明行）——顺手登记死字段。
- 风险：低（加门一行）。
- 机器条件：本机即可。

### C2-6 内存钳制恒不生效（真缺陷-静默失效）＋ CPU 轴仍缺

```
QueueProcessor.cs:146   var memInfo = GC.GetGCMemoryInfo();
:147   var availableMB = memInfo.TotalAvailableMemoryBytes / (1024 * 1024);
:148   var maxByMemory = (int)Math.Max(1, availableMB / Math.Max(1, perTaskMB));
```
`TotalAvailableMemoryBytes` 是 **GC 可用的物理内存总量**（≈ 全机 RAM），**不是空闲量**；`:56` 注释与 `:141` doc-comment 都写的是「**系统可用内存**」⇒ 注释与口径不符。
本机读数：31.6 GB ⇒ `plainMB=192`（`:123`）算出 **168**、`linearMB=768`（`:124`）算出 **42**。UI 的 `-j` 上限是 **128** ⇒ **普通档钳制恒不生效**，GainMap/RAW 档只在 `-j > 42` 才咬（且 42 是「全用满」的口径，没扣 OS 与其它进程）。
另外 `EstimatePerTaskMemoryMB`（`:111-139`）是**整队一票否决**：队列里只要有一张 GainMap/RAW 就把**每一项**按 768 MB 预留 ⇒ 与「按内容分两档」的注释意图（`:105`）不一致，实机上一张 UltraHDR 混进 500 张普通 jpg 就把上限从 168 压到 42。
**CPU 轴**：`grep ProcessorCount` 在 `QueueProcessor.cs` = **0 命中**（正向对照：`FfmpegOptions.cs:650/664`、`CliParser.cs:35`、`ExifToolService.cs:1332-1333` 命中）⇒ 仍无 CPU 钳制，P4 该条复验成立。
- 判据：读 `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` vs `AvailableMemoryBytes`（.NET 10+ 有 `AvailableMemoryBytes`，那才是空闲量）；改后 `-j 8` 在内存吃紧时应当被压下、在空闲时不动。
- 风险：**中** —— 换成真实空闲量会让钳制**第一次真正咬人**，可能把用户设的 `-j 8` 压到 1-2，表现为「莫名变慢」。建议做成下限保护（例如不低于 `min(requested, 2)`）并**点名日志**，否则违反「失败要响」。

### C2-7 共享状态与缓存跨任务串用（逐条判定）

| 对象 | HEAD 事实 | 标签 |
|---|---|---|
| `FfmpegCommandBuilder.cs:1303-1334 ProbeCache` | `Dictionary` + `ProbeCacheLock` 双向持锁 ⇒ **无结构竞态**。但键 = **纯 `inputPath`，不含 mtime**（`:1317`），TTL 10 s（`:1305`），容量 256 时**整表 `Clear()`**（`:1333`）⇒ 同一路径在 10 s 窗口内被覆盖过（典型：输出目录 = 输入目录、或重排队）会拿到**上一次的位深/ICC**；256 张以上的批次缓存必然被整表丢弃 ⇒ P1/§2.3 的每图探测成本被放大 | 真缺陷-静默失效（键缺 mtime，`MediaProbeCache` 同族已修过这一形：`MediaProbeCache.cs:31` 键含 `LastWriteTimeUtc.Ticks`）；Clear 阈值部分是性能项 |
| `MediaProbeCache.cs:13-14,40-43` | 键含 mtime（对），但**无容量上限**；`MaxAge = 24 h` 按 `LastWrite` 淘汰 ⇒ 照片类输入**几乎全部恒过期**，只在 `_cache.Count % 100 == 0` 时真清（并发下会漏触发）⇒ 净效果：本批内留着、跨批无界涨（每条约 ffprobe JSON 数 KB；静态估 N=5000 ⇒ 数十 MB 常驻，误差方向**低估**） | 性能项 + 未判定（无实测 RSS 读数） |
| `QueueProcessor.cs:5873,5963,5971 AnimatedJxlCache` | **无 TTL、键纯路径**，但 `:5971` 只在 `determinate` 为真时写，`:1328` 注释自述「取消/超时值不写」⇒ 毒缓存不成立 | **未复现（已闭合）** |
| `GpuCapabilityService.cs:63-70,105-130` | `_cachedReport` **非 volatile**；`CachedReport` getter 持锁（`:67-70`），但 `:73/:76/:79/:82` 四个 `HasXxx` **绕过 getter 直读字段**；`DetectAsync` 无 single-flight（`:107-111` 只在锁内查、锁外探测、锁内写）⇒ 并发调用会**各起一份探测进程组**（`Step1 -hwaccels` + `Step2 -encoders`）；`ValidateEncodersAsync`（`:136-158`）**就地改缓存对象的可变字段** `status.Availability`，与队列侧 `EncoderDetectionService.cs:336` / `MainWindow.xaml.cs:4378` 的读并发 | 真缺陷-竞态（**x64 TSO 下不可实测** ⇒ 只能按代码判据处理：读走 `CachedReport`、探测用 `Task` 单飞、报告改不可变快照）。标签：**未判定(需 TSAN 或 ARM 侧证据)** |
| `QueueProcessor.cs:18 _running` / `:3277 PreConvertToPngAsync` | 前者声明后零使用；后者零调用方（正向对照 `ProbeBitDepthAsync` 有调用）⇒ `PreConvertToPngAsync` 若被接线，其 `ffmpeg_preconv_{guid}.png`（`:3279`）**不在 `tempDirsToCleanup` 里**、只 `MarkAsTemporaryFile`（`:3299`，且仅成功路径）⇒ 失败路径留盘 | 死代码登记；当前**未复现**（无调用方 ⇒ 无泄漏） |

### C2-8 `BufferPool` / ArrayPool：钳小不成立，但有一条反向不变量（未判定-文档漂移）

`BufferPool.cs:13` `MaxPooledLength = 256 MB`；`:20-21` `RentByte(len) => len > MaxPooledLength ? new byte[len] : ArrayPool<byte>.Shared.Rent(len)` ⇒ **超上限是直配精确数组，不会被钳小**。因此：
- `RawColorPipeline.cs:1134-1136` 的注释「`BufferPool.Rent` 内部会 `Math.Min(length, 256MB)` 钳制，请求大于池上限时会拿到**小于请求量**的数组（直接用会越界）」**与实现相反**；`docs/PIPELINE_PERF_ANALYSIS_2026-10-09.md:150`（P7）把这条当成「有意的正确性取舍、别当优化目标」的**理由**引用了同一句错话。
- 结论仍是「不动它」，但理由要换：`PoolUseLimitBytes = 4 MB`（`:1139`）是**防「大数组进池后被 gen2 通知裁掉、池收益 ≈ 0」**，而不是防越界。⇒ 标签：**真缺陷-文档漂移**（有牙：下一次有人照注释推理就会推错方向）。
- **核了「钳小后有没有长度断言」**：不需要断言，因为不会钳小；但**另一条不变量确实被认真守住了** —— `TransformStreamedAsync`（`:1153-1172`）读/写一律 `AsMemory(0, bytes)`/`AsSpan(0, oBytes)`；`GainMapEncoder.cs:446-454` 注释「池化数组长度可能 > 请求；严格只写 exactBytes，避免脏尾」。`ReturnSmart`（`:1141-1144`）与 `RentSmart` 的阈值对称（4 MB 本身是 2 的幂 ⇒ 向上取整不会越阈）⇒ **无归还漏池子**。

**新发现的一条（性能项，量级小）**：SIMD 内核按**池化后的数组全长**扫描，而不是按 `pixelCount`：
```
SimdPixelOps.cs:168   for (int i = 0; i + 4 <= hdr.Length; i += 4)     // ReinhardToSdr
SimdPixelOps.cs:208   for (int i = 0; i + 4 <= hdr.Length; i += 4)     // ComputeGainMapGray
SimdPixelOps.cs:273   for (int i = 0; i + 4 <= rgba.Length; i += 4)    // ApplyPrimariesMatrix
```
`GainMapEncoder.cs:118-121` 的 `normHdr`/`sdrLinear` 是 `RentFloat(pixelCount*4)` ⇒ 长度被向上取整到 2 的幂，多算的那一段读的是**上一个任务留下的脏尾**（`clearArray:false`，`BufferPool.cs:27`），写回 `sdr`/`gain` 的尾区，而消费端一律按 `pixelCount` 取 ⇒ **产物无影响，纯多算**。
静态口径：4K（`3840×2160×4 = 33,177,600`）→ 取整 `2^25 = 33,554,432` ⇒ 多算 **+1.1%**；最坏（请求刚过 2 的幂）接近 **2×**；平均（请求在 `(N/2, N]` 上均匀）≈ **1.39×**。误差方向：这是**上界口径**，实际取决于素材像素数落在取整区间的哪一段。
`ComputeGainMapGray` 的越界风险核过：`gain = RentByte(pixelCount)` 与 `hdr = RentFloat(pixelCount*4)` 的取整恒满足 `gain.Length ≥ hdr.Length/4` ⇒ **不会 IndexOutOfRange**。
⇒ 改法（把 `pixelCount*4` 显式切成 `Span`）预计收益 **1%–90% 的那 4 个内核**，不是产物变更；但**必须先实测 `dop=`/内核占比再排优先级**，本机判据不足 ⇒ 归 §4。
另：`GainMapEncoder.cs:353-368 RescaleGainMap` 在内层 `for(dx)` 里 `float[] sum = new float[channels]` ⇒ 4K÷4 静态估 **518,400 次小数组/图**；提到循环外 + `Array.Clear` 是产物中性的。

### C2-9 `RealTime` 优先级与挂死判死的相互作用（未判定，需实测）

`PlatformServices.cs:423-435` 的映射 `0 => ProcessPriorityClass.RealTime`，`AppSettings.cs:83` 默认 3（Normal）但 UI 可选 0；`FfmpegRunner.cs:134` 与 `RawColorPipeline.cs:816/828`、`CjpegliService.cs:199`、`CjxlService.cs:351` 都无条件应用。
机制：`RealTime` 的 N 个 ffmpeg 会**饿死本进程线程池**（Normal）⇒ `FfmpegRunner.cs:155-164` 的判死是**墙钟**（`DateTime.UtcNow - lastProgressTicks`），而 `lastProgressTicks` 由 `OutputDataReceived` 回调（线程池线程）更新 ⇒ 回调被饿住 ⇒ **合法任务被判成挂死、退 `-124`**。这正违反维护者判据（错误归因被削弱：编码失败与挂死混成同一码）。
- 判据：`FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC`（`FfmpegRunner.cs:41-46`，注入小阈值）+ 把优先级设 0、`-j` 拉高，跑一张 4K 合法长编码，看是否出现「无进度心跳」。
- 建议（只登记不动手）：`SetSafePriority` 把 `RealTime` 档改成 `High`，或在队列并发 ≥ 核数时禁 0 档 —— 需用户点头（会改行为）。

### C2-10 IO / 缓冲 / 临时目录（并发冲突与清理）

| 点 | HEAD 事实 | 标签 |
|---|---|---|
| 临时目录/文件**同名碰撞** | 一律 `Guid.NewGuid():N`：`QueueProcessor.cs:575/810/818/848/2111/2304/2623/2772/3148/3279/3661/3800`、`PlatformServices.cs:466`、`RawColorPipeline.cs:107`（`cms_{guid}.rgb48`）⇒ **无碰撞面** | **未复现** |
| 清理覆盖失败路径 | 任务级 `finally`（`QueueProcessor.cs:1213-1221`）统一删 `tempDirsToCleanup` 并把 `captured.InputPath` 还原（P1-5）；`Stop()` **不再**兜底删（`:180-181` 注释写明旧写法的泄漏已修）。引擎侧 `finally { TryDelete(tmp); TryDelete(tmpOut); }`（`:246-248`/`:382`/`:1271`/`:1307`）、`PipeToEncoder`（`:782-785`）⇒ **取消/异常路径都跑到** | **未复现（闭合）** |
| 例外：`GainMapEncoder`/`GainMapDecoder` 的中间件 | `GainMapEncoder.cs:207/210/223` 把 `base.png`/`base.jpg`/`gainmap.jpg` 落 `tempDir`，靠 `:243 finally { Directory.Delete(tempDir, true) }`；但 `:210 File.ReadAllBytes(basePath)` / `:223 ReadAllBytes(gmPath)` 是**整文件进内存**（底图 JPEG，尺寸 = 产物，KB~MB 级，可接受） | 未复现 |
| **`ReadAllBytesAsync` 整帧进内存的真点** | `RawColorPipeline.cs:329`（`cms_*.rgb48` 整帧读回，4K 16bit = **49.8 MB**，8K = 199 MB）、`:1269`（同族）。P6 已登记同类（`hdr.rgba`）。⇒ 属「可管道化」而非缺陷 | 性能项（已在 P6 账上，行号漂移：`docs:146` 引 `QueueProcessor.cs:3158-3166` → HEAD 该 ffmpeg 站点在 **`:3165`**；引 `:2688` → HEAD 引擎侧读回在 **`RawColorPipeline.cs:329`**） |
| 大文件全进内存的 UI 侧 | `MainWindow.xaml.cs:3800 ProbeCurrentSourceBitDepth` 用同步 `GetAwaiter().GetResult()`（`:3827-3828`）——在 UI 线程上是同步等子进程；`FfmpegCommandBuilder.cs:1407-1408/1696-1697/1794-1795` 的同类已被 `:1404-1406` 的 `Task.Run` 包法修掉（2026-08-14 死锁修复），但 `ImageGeometry.cs:122`/`IccProfileService.cs:354`/`PlatformServices.cs:88-111`/`ExifToolService.cs:344-387` 仍是同步 `GetAwaiter().GetResult()` 族 | 真缺陷-潜伏（UI 线程同步等子进程；这些方法是否在 UI 线程被调需调用图证据）⇒ 归 §4 |

### C2-11 UI 线程成本（P3 复验 + 定位更正）

- 旧账 `MainWindow.xaml.cs:3126-3140`「串行 `BuildQueueItemCommand`」：HEAD 该循环在 **`:3125-3143`**（几乎未漂移），但它**只在 `PreserveInputFolderStructure` 为真时跑**。批量入队的真正串行点在 **`AddToQueue_Click :2798-2830`**：
  ```
  foreach (var file in _selectedFiles) { ... await AddSingleToQueue(file, baseDir); }
  ```
  `AddSingleToQueue :3055-3064` 内 `await CollectOptionsFromUiAsync(inputPath)` + `item.Command = BuildQueueItemCommand(item)` ⇒ **逐项 await，全部在 UI 线程**，且 `:3441` 走 `FfmpegCommandBuilder.BuildArguments`（含 `GetCachedProbe` 的 exiftool+ffprobe 链）。
  ⇒ P3 主张**在 HEAD 仍成立**（仍串行），但**引用的位置偏了**：P3 说的「点开始到第一项 `[cmd]` 落日志」的计时起点应是「点添加到队列」，不是「点开始」。
- 「仅显示报错」全量重过滤：**仍每次更新全量跑**（`:3205-3208` 行号未漂移），见 C2-4。
- 进度刷新节流：**无**。`OnQueueItemUpdated`（`:3171`）没有 coalesce/debounce；状态字段本身走 `INotifyPropertyChanged`（`QueueItem.cs:29-41`）绑定，所以**额外的 Post 只是为了重排过滤集合** ⇒ 节流点很干净（把两个 `if (...IsChecked)` 收成一个 `DispatcherTimer`/帧合并即可）。
- 死代码顺登记：`MainWindow.xaml.cs:5360 OpenCacheDirMenu_Click` 含进程启动且 T/K/CT 三面皆无（`guardscan` 读数），但它是「打开缓存目录」菜单项（`explorer`），非队列路径。

---

## 4 需实测的格子（配方，交主循环）

> 全部按维护者判据写：改后**产物逐字节必须不变**，且**不许合并/丢失点名**。

| 格 | 机器条件 | 配方（改前/改后各一次，best-of-3） | 通过判据 |
|---|---|---|---|
| **M1 引擎 Dop 超饱和（C2-1）** | **需 `%TEMP%` 可写**（`docs §6`：ffmpeg 被拦写 Temp ⇒ 引擎路 `exit -13`）。C 盘当前 99% 满、剩 13 G ⇒ 先清盘或把 `AppSettings.CacheDirectory` 指到工作区外可写卷（`PlatformServices.cs:478-488` ①档，就是为此留的口子，**别用 TMP/TEMP 绕**：账本记了 47 分钟挂死且成因未定位） | 同一批 16 张 4K 真实熵 + `--color-engine engine`，`-j 8`；两臂只差 `QueueProcessor.cs:1415` 的 `1` → `_concurrency`。**臂间必须先跑一次逐字节对拍**（同图、`-j 1`、两臂各产一张 ⇒ `certutil -hashfile` 比 SHA256） | ①产物哈希全等；②`-j 8` 墙钟下降 **且** RSS 不升（P4 原判据）；③日志 `dop=` 从 20 变 3（`ColorScheduler.cs:98 ToLogFragment`） |
| **M2 引擎线挂死守卫缺失（C2-2）** | 同上（Temp 可写） | 畸形 1 B `00` .jxl，`--color-engine engine`，注入 `FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC=2`；**反向**臂：合法 4K 单帧 `libjxl -effort 9` 不被误杀 | 正臂：限时自行结束、日志逐字出现判死点名、退出码非 0 且**与「已停止」可区分**；反臂：产物 1、日志**无**判死行。照 `verify-ffmpeg-heartbeat.ps1` ②/③ 的口径抄，把站点换成引擎 |
| **M3 `-filter_threads` / 解码器线程轴的默认值（P4 遗留）** | 需 Temp 可写（要跑真滤镜图） | 取默认值：`ffmpeg -loglevel debug` 跑一次带 `scale`/`zscale` 的滤镜图并数 OS 线程；或直接查该 build（`git-2026-10-07-e5a08f7c0e`）的 `avfilter/graph.c` `nb_threads` 兜底。⚠ 我本机跑的 `-loglevel verbose` 只打出 `[vf#0:0] Starting thread...` **一行**，取不到数 ⇒ **此项判据缺失，只能记「未判」，不许写成结论** | 有默认值读数才谈下发 `-filter_threads`；否则维持 P4 的「不动」 |
| **M4 UI 更新成本（C2-4 / C2-11）** | 本机即可（不需要 Temp 解堵） | N=20 与 N=100 各一批，勾「仅显示报错」；计时起点 = 点「添加到队列」，终点 = 最后一项状态落定；对照 UI 线程繁忙率 | N 从 20→100 时墙钟增幅**远小于**平方；加一条「Post 次数 ≤ 状态变更次数」的计数断言 |
| **M5 二次点「开始」（C2-5）** | 本机即可 | 队列跑 8 张时再点一次「开始」 | 期望：no-op + UI 冻结 < 100 ms；现状预期会看到在跑项变「已停止」并重新排队 ⇒ 记为回归证据 |
| **M6 内存钳制口径（C2-6）** | 需能压内存的机器（或人为把空闲 RAM 压到 <4 GB） | 读 `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` 与 `.AvailableMemoryBytes` 两个数；在空闲紧张时 `-j 8` 看是否被压 | 两数差被打印出来 ⇒ 才能定「换成空闲量」会不会把用户 `-j 8` 压到 1-2；不压就不是性能项而是风险 |
| **M7 GainMap 内核多算 + `RescaleGainMap` 内层分配（C2-8）** | 需 Temp 可写（GainMap 路） | 同一张 4K HDR，三臂：①原样；②`Span` 按 `pixelCount*4` 显式收窄；③再把 `sum` 提到循环外。逐字节比产物 | 三臂产物哈希全等；②③ 各自 `st.ProcessMs`（`RawColorPipeline.cs:216`/`:184` 已有 ms 读数，可当免费分步账）下降才有意义 |
| **M8 `RealTime` 优先级 × 心跳判死（C2-9）** | 本机即可，但要**盯着**（RT 档有让机器失响应风险 ⇒ 高危，主循环自行决定是否跑） | 优先级下拉选首项（=0 ⇒ `RealTime`），`-j` = 核数，`FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC` 设小值，跑合法 4K 长编码 | 若出现「无进度心跳」判死 ⇒ 证实饿死线程池这条机制；否则降级为「理论风险，仅登记」 |
| **M9 UI 线程同步等子进程族（C2-10 末行）** | 本机即可 | 给 `MainWindow.xaml.cs:3800`、`ImageGeometry.cs:122`、`IccProfileService.cs:354`、`PlatformServices.cs:88`、`ExifToolService.cs:344-387` 各打一张调用线程 + 耗时（栈上有无 `Dispatcher` 帧） | 只有**证明**存在「UI 线程 → 同步 GetResult → 子进程」这条链才立项；否则撤销 |
| **M10 `ReadToEndAsync` 先 stderr 后 stdout 的可触发性（C2-3）** | 需 Temp 可写 | 给 `RunToEndAsync` 的某一站点塞一条会写 stdout 的 ffmpeg 参数（如临时加 `-progress pipe:1`），观察是否互锁 | 抓到互锁 ⇒ 第 4 条探针（stdout 版 stderr-drain 锁）立即接线；抓不到 ⇒ 只登记形态 |

**顺手要跑的既有门禁**（我给不了读数，因为跑它们要起产品 exe）：`verify-ffmpeg-heartbeat.ps1`、`verify-jxl-probe-timeout.ps1`、`_probe-sync-read-before-wait-scan.ps1`、`_probe-ct-chain-closure-scan.ps1`、`_probe-cancel-propagation-scan.ps1`、`_probe-stderr-drain-scan.ps1`。后四条我已在 HEAD 实跑，读数见 §1.1。

---

## 附：本报告未覆盖 / 越界声明

- 未新增或修改任何探针/门禁脚本；§1.3 的守卫扫描落在仓外 `C:\temp\c2probe\guardscan.ps1`，其输出是**静态计数**，不可当行为证据。
- 未起产品 exe 做任何计时；`publish/PLAN/ffmpeg-full/ffmpeg.exe`（`git-2026-10-07-e5a08f7c0e`）只跑了 `-h full` / `-version` / 一条 `-loglevel verbose` 的 `lavfi→null`（取默认值用，无计时语义）。
- 色彩正确性、产物语义、GainMap 数学归 A/B 两族 lane；headless CLI 与 UI 一致性归别的 lane。
- `docs/HANDOVER.md` 的行号引用是**旧账原文**，我没有逐条回读 HANDOVER 全文，只回读了 `:338-345 / :426 / :520-570` 这几段与心跳覆盖面直接相关的原文。
