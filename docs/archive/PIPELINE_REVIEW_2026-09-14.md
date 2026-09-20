# ffmpegPictureUI 软件管线审查报告（2026-09-14）

> 审查方式：只读审查，未改动任何代码。生成者：AtomCode（glm5.3-flash）。
> 用途：完整管线交接文档 —— 任何接手 agent 应先读本文档，再读 `docs/HANDOVER.md`。
> 范围：`src/FfmpegGui/**`（GUI + headless 共用核心）。`publish/`、`bin/`、`obj/` 为构建产物，不在审查范围。

---

## 1. 项目速览

C#/.NET 图像转码 GUI（Avalonia，支持 NativeAOT），封装 ffmpeg / cjxl / cjpegli / djxl / JxrEncApp / avifenc / dngtool / exiftool 等外部工具，另含纯 C# 色彩引擎（ColorMapping）与纯托管 Ultra HDR (GainMap) 编解码、ICC v4 profile 生成器。GUI 与 headless（`--headless`）共用同一条队列管线。

### 1.1 分层结构

```
Program.cs / CliParser.cs          入口：GUI 或 headless 分流
MainWindow.xaml.cs                 GUI 层（队列操作、设置触发点、工具检测编排）
App.xaml.cs                        Avalonia 应用层（托盘/主题/本地化/IPC 启动）
Models/                            AppSettings / FfmpegOptions / QueueItem / Preset*
Services/ColorMapping/             纯 C# 色彩引擎（下文 §3 重点）
Services/ColorSpaceRegistry.cs     色彩空间注册表（单一真值来源，§3.1）
Services/ColorEncodingHelper.cs    cjxl 参数映射薄层（§3.4）
Services/FfmpegCommandBuilder(.Decision).cs  ffmpeg 参数拼装 + 色彩裁决点（§3.2）
Services/QueueProcessor.cs         队列核心调度（§2）
Services/DefaultQueueOrchestrator.cs         队列编排包装
Services/CjxlService / CjpegliService / DjxlService / JxlPipelineService / JxrService
Services/GainMapEncoder / GainMapDecoder     纯托管 Ultra HDR
Services/IccProfileService / IccProfileBuilder  ICC 提取/生成
Services/PngCicpService            PNG cICP/sBIT chunk 补写
Services/AppSettingsService / LocalizationService / ExternalToolsDetector / ...
```

---

## 2. 编码执行链（一张图从入队到输出）

### 2.1 流程

1. **入队**：`IQueueOrchestrator.StartAsync(items, concurrency, ct)` → `DefaultQueueOrchestrator` 逐项 `_processor.Add(item)`，`QueueProcessor.Start(concurrency)` 起后台消费，headless 语义下 `StopAfterCurrentQueue()`。
2. **调度**：`QueueProcessor.ProcessAsync` 轮询 `ConcurrentQueue`，`SemaphoreSlim` 控并发（并发数按内存钳制，每任务估算 200MB）。
3. **单项预处理**（会改写 `captured.InputPath` 指向临时文件）：
   - Ultra HDR JPEG → `GainMapDecoder` 解为 PFM 线性 HDR 临时文件；
   - RAW → `RawService`（dngtool → 16bit TIFF，`raw_{guid}` 临时目录）；
   - MaxDimension 超限 → ffmpeg 预缩放为 `scale_{guid}` 临时 PNG。
4. **格式分发**：GIF→AVIF(avifenc)、AVIF→GIF/WebP、JXL 输入、JXR 输入、GainMap JPEG、cjxl、cjpegli、JXR、DNG 各有专链；否则走主分支：先 `TryRunColorEngineAsync`（纯 C# 引擎，失败码 90/91/92），未接管则 `FfmpegCommandBuilder.BuildArguments` → `FfmpegRunner.RunAsync`。
5. **外部编码器回退**：cjxl/cjpegli 直连失败 → `PipeFfmpegToExternalEncoderAsync`（ffmpeg stdout → PPM/PAM → 编码器 stdin，10 分钟超时）；JXL 输入走 djxl→cjxl/cjpegli/ffmpeg 管道族。
6. **后置链**（成功后顺序执行）：元数据回写（exiftool 优先，ffmpeg 回退）→ PNG sBIT/cICP 补写 → ICC 补嵌（裁决点登记的语义）→ TIFF ICC → 保真 ICC 附着 → 隐私清理 → 可选 `.png` 后缀。
7. **收尾**：`finally` 删任务临时目录；`OperationCanceledException`→"已停止"，其他异常→"失败: …"。`ConsoleQueueObserver` 把进度/日志以 Text/JSONL 写 stdout+文件。

### 2.2 执行链问题（按严重度）

#### P0

| # | 问题 | 位置 |
|---|------|------|
| P0-1 | **headless 整条队列仅 120s 硬超时**：`WaitForCompletionAsync` 的 CTS 作用于整队而非单项，批量转图必然超时 → `TimeoutException` + `Stop()`，批处理永远无法完成 | `DefaultQueueOrchestrator.cs:97-105` |

#### P1

| # | 问题 | 位置 |
|---|------|------|
| P1-1 | `CjxlService` 执行无取消令牌、无超时：`WaitForExitAsync()` 不传 ct，用户点停止或 cjxl 挂死 → 进程永不退出、并发槽位泄漏、队列卡死（对比 CjpegliService/DjxlService 均有 5min 超时） | `CjxlService.cs:243,294` |
| P1-2 | `FfmpegRunner` 无任何超时，只有 ct 取消；ffmpeg 挂死时所有直连调用无限等待 | `FfmpegRunner.cs:45-51` |
| P1-3 | 元数据回退"先 `File.Delete(output)` 再 `File.Move(temp→output)`"非原子，Move 失败即**输出文件彻底丢失**。应改 `File.Move(temp, output, overwrite:true)` | `QueueProcessor.cs:1091-1092` |
| P1-4 | 所有管线**直写最终输出路径** + 无条件 `-y`：失败/取消留下半截损坏文件冒充合法输出，且静默覆盖已有文件。建议临时文件+成功后原子替换 | `FfmpegCommandBuilder.cs:679`、`QueueProcessor.cs:372/1392/1539` 等 |
| P1-5 | 取消/失败后 `item.InputPath` 已被改写为临时文件且临时目录已删，`RequeueStoppedAndFailed` 重跑必然失败 | `QueueProcessor.cs:270/319/383` vs `:79-93` |
| P1-6 | `_pendingRawDirs` 仅在 `Stop()` 清理；正常跑完一批（不走 Stop）临时目录全部残留 | `QueueProcessor.cs:1850` vs `:102-117` |

#### P2

| # | 问题 | 位置 |
|---|------|------|
| P2-1 | `item.Log += s` 多线程字符串拼接无锁，并发丢日志行 | `QueueProcessor.cs:467-469` 等 |
| P2-2 | `ConsoleQueueObserver._completedCount++` 非原子；跨线程改 `Console.ForegroundColor` 交错 | `ConsoleQueueObserver.cs:100,229-235` |
| P2-3 | ffprobe 探测重定向 stderr 却从不读取，错误输出 >4KB 可塞满管道死锁（三处） | `QueueProcessor.cs:1880-1888` 等 |
| P2-4 | avifenc 帧列表拼命令行，长 GIF 超 Windows 32K 参数上限 | `QueueProcessor.cs:2391-2399` |
| P2-5 | cjpegli/djxl 未设 `StandardOutputEncoding = UTF8`，Unicode 路径日志乱码 | `CjpegliService.cs:198-206`、`DjxlService.cs:142-150` |
| P2-6 | 状态机依赖中文魔法字符串前缀（"已完成"/"失败"）互相耦合，改措辞即破坏终态判定 | `DefaultQueueOrchestrator.cs:127-133`、`QueueItem.cs:136-139` |
| P2-7 | ProcessAsync 200ms 轮询 + tasks 列表条件清理（`%10==0`），长队列列表持续增长 | `QueueProcessor.cs:160-177` |
| P2-8 | GainMap 管线内存估算（200MB/任务）与实际（4K 图 >500MB）不符，多并发可 OOM | `QueueProcessor.cs:63-73`、`GainMapEncoder.cs:98-99` |

> **处置状态（2026-09-15 更新，本表本身是 09-14 的发现快照不再改动）**：
> 已修并有实测/变异验证的：P0-1、P1-1、P1-2、P1-3、P1-5、P1-6、P2-1、P2-2、P2-3、P2-4、P7g、P7h、P7a-3 第三轮；
> 支撑层 S-P0-1、S-P0-2、S-P1-1。逐条证据（含命令与数字）在 `docs/TESTING.md` §6。
> **P1-4 只做了事后兜底**（确证失败 + 队列静默后丢弃残缺输出），"每条管线先写临时文件、成功后原子替换"
> 属结构级改动（约 15 个落盘点 + `-y` 语义），按用户"不得改变软件结构"的约束**待拍板**。
> P6 侧的 `verify-gainmap-managed` 仍有 **6 条陈旧断言**（按已移除的"PQ HDR 底"契约写，现行 GainMap 是 SDR 底）⇒ **25/6 属门禁陈旧，不是产品回归**。
> ⚠ 本机存在并发编辑：该脚本 07:56 一度出现改好的 31/0 版本，08:05 又回退成 2026-09-10 的旧版（实测 mtime 与 7 处「HDR 底」）⇒ 引用别人的结论前先重跑。
> 仍开放：P2-5（子进程 stdout/stderr 编码未统一，实测远不止审查报告列的两个文件）、P2-6、P2-7、P2-8
> 与支撑层 S-P1-2…S-P1-6；色彩侧剩 P5 的 `verify-color-engine-xcheck` PQ→sRGB 一格。

### 2.3 支撑层问题摘要（详见子审查，按严重度）

#### P0

| # | 问题 | 位置 |
|---|------|------|
| S-P0-1 | **`AppSettingsService.Save()` 手写克隆漏掉 `EnableIpcServer`/`IccDirectory` 两个有效字段**：任何一次设置变更落盘都会把它们抹成默认值，重启后 IPC 服务器永远起不来、`IccDirectory` 被 PlanFolderDetector 回填后又丢 | `AppSettingsService.cs:131-153`（对比 `AppSettings.cs:25,72`） |
| S-P0-2 | **设置读写不对称 + 保存失败静默吞掉**：Load 可从 `%APPDATA%` 回退读，Save 只写 exe 同目录；安装到只读目录时 `UnauthorizedAccessException` 被 `catch { }` 吞掉，设置永不保存且无感知 | `AppSettingsService.cs:13-14,157-159` |

#### P1

| # | 问题 | 位置 |
|---|------|------|
| S-P1-1 | settings.json 写入非原子（无 temp+Replace、无 .bak），损坏后 `Load` 的 `catch{}` 静默重置为全新默认，**全部用户设置无提示丢失**；MainWindow 每次控件变更立即全量写盘，风险窗口不小 | `AppSettingsService.cs:53-59,157` |
| S-P1-2 | IPC"单实例"未接线：`TryConnectAsync` 全仓库无调用点，第二次启动真双开 | `IpcService.cs:101-113` |
| S-P1-3 | IPC 服务器在非 UI 线程直接操作 MainWindow（AddItemsToQueue/Close 等），Avalonia 非线程安全 | `App.xaml.cs:213`、`IpcService.cs:259-331` |
| S-P1-4 | IPC 服务器阻塞式 `WaitForConnection()` 无 token、无 try/catch，fire-and-forget 下管道失败静默失联；一次只服务一个客户端 | `IpcService.cs:158-170` |
| S-P1-5 | `FormatCapabilitiesService._cache` 普通 Dictionary，初始化线程写与 UI 线程读并发；`format.ToLower()` 应为 Invariant | `FormatCapabilitiesService.cs:36,40` |
| S-P1-6 | IPC 客户端 `ReadLineAsync()` 不传 token，10s 响应超时实际不生效，可无限挂起 | `IpcService.cs:127-133` |

#### P2（支撑层，摘要）

- 无 `SettingsVersion` 字段，迁移只能靠字段嗅探（`AppSettings.cs`）。
- `--settings` 指向不存在文件时静默回退（`AppSettingsService.cs:44-45`）。
- 极端回退字典仅 ~85 键且含废弃的 `icc.mode.*` 键，生效时大量 UI 显示原始键名（`LocalizationService.cs:208-296`）。
- `ProbeAllTools()` 内部 Detect 无 try 保护、dngtool 判空不严、同步串行起多进程（`ExternalToolsDetector.cs:405-515`）。
- IPC `HandleGetStatus` 返回恒 0 的假数据，`HandleStopAsync` 实际是 Pause（`IpcService.cs:283-289,320`）。
- QueueItem 状态为中文硬编码，英文界面下队列仍显示中文状态（`QueueItem.cs:17,136`）。
- locale 文件 en-US/zh-CN 目前逐行对齐（211 键抽查一致 ✅），但无构建期一致性校验。
- `BufferPool` 委托 `ArrayPool.Shared`，注意 `RentByte` 可能返回**比请求更长的数组**，调用方只能用前 length 个元素（隐式契约，未见越界使用）。

**支撑层正面结论**：JSON source-gen 上下文（`AppJsonContext`）覆盖完整，无反射序列化遗漏；工具检测五级降级链完整；`MediaProbeCache` 用 `ConcurrentDictionary` 正确。

---

## 3. 色彩矩阵链路深度审查（重点）

### 3.1 架构：三个层次 + 一个注册表

色彩处理分三层，接手者必须理解各自边界：

| 层 | 入口 | 职责 |
|----|------|------|
| **注册表层** | `ColorSpaceRegistry`（612 行，静态） | 全流程**单一色彩真值来源**：显示名 → Spec（ffmpeg CICP tokens / cjxl 缩写 / 色度 xy / 白点 / GamutKind）。所有映射、滤镜生成、cjxl 参数都从这里出 |
| **裁决层** | `FfmpegCommandBuilder.DecideOutputColor`（93-412 行，约 320 行状态机） | ffmpeg 主分支的色彩决策：决定像素链、CICP 标签、ICC 嵌入方式 |
| **引擎层** | `Services/ColorMapping/`（ColorMappingEngine + ColorTransformPlan + ColorSpaceDescriptor + 内核） | 纯 C# 进程内色彩引擎（H1），可精确处理 zscale 盲区（ProPhoto 1.8、A2B profile、GMO 色域压缩） |
| **薄映射层** | `ColorEncodingHelper` | 仅 cjxl/cjpegli 参数映射（color_space 缩写 + intensity_target），无像素逻辑 |

### 3.2 数学核心核查结论（重点验证过，可放心依赖）

以下实现经过逐行核对，**数学上正确**：

1. **`PrimariesToXyz`**（`ColorSpaceRegistry.cs:539-556`）：标准 Lindbloom 构造法。N 矩阵（列=原色 XYZ）解 `N·S=W` 得通道缩放，`RGB→XYZ = N·diag(S)`。✅
2. **`BradfordAdapt`**（`:566-575`）：`M⁻¹·L·M` 正确复合。**注意注释里记录的历史坑**：旧写法 `L·M·M⁻¹` 因同白点时 L=I 而从未暴露，跨白点（ProPhoto D50↔sRGB D65）才出错，由 lcms2 ICC 比对抓出。**改这里必须保留行和校验测试**。✅
3. **`Invert3x3`**（`:597-611`）：伴随矩阵法，det<1e-12 返回零矩阵（调用方 `LinearMatrix` 需注意零矩阵≠null，但下游 `MatrixTo` 的行和校验会兜住）。✅
4. **`MatrixTo`**（`ColorSpaceDescriptor.cs:170-186`）：经 XYZ_D50 复合 `dst⁻¹·src`，**自带行和≈1 白点守恒校验**（阈值 5e-3，注释解释了 ProPhoto D50 定义差异 ~1.4e-3 的来源），失败返回 null 而非硬转。这是整个引擎"宁可不映射也不写错标签"原则的落点。✅
5. **`ContainsGamut`**（`:236-248`）：几何包含判据（同侧法内点测试，源原色 xy 落入目标三角形），**取代了旧的"矩阵元素全非负"错判**；边界松弛 epsilon 处理原色重合（sRGB⊆sRGB、P3 与 2020 共用蓝原色）的情况。✅
6. **`LinearMatrix(from,to)`**（`ColorSpaceRegistry.cs:281-293`）：`PrimariesToXyz → Bradford 适应 → 目标 XYZ→RGB 逆矩阵`，链路正确。✅

### 3.3 设计决策记录（接手前必读，均为实测踩坑后修正）

这些决策**看起来违反直觉但都是修复过回归的**，不要"顺手改回"：

1. **超广色域归一化（D1）**：Adobe RGB/ProPhoto 等 CICP 无法命名的空间 → 像素矩阵转入 BT.2020（`P3⊂BT.2020` 超集零削色）+ 附标准 ICC，而不是丢弃或猜标签。SDR 归一用 bt709 传递，HDR 源保留 PQ/HLG（`BuildUltraWideToBt2020Filter(displayName, hdr, hlg)` 的 hdr 参数就是为此，SDR 若被烘成 PQ 会变暗）。
2. **DCI-P3 / P3 PQ / P3 HLG / 裸 BT.2020 都标记 `Kind=UltraWide`** 走归一化分支（`ColorSpaceRegistry.cs:78-115`）：因为 cjxl 无对应枚举 / zscale 不认 smpte428 / iccgen 不支持 HDR。裸 "BT.2020" 特意解析为 **SDR**（bt2020+bt709），HDR 必须显式 "BT.2020 PQ/HLG"——修复过 SDR 源被当 PQ 解码变暗的回归（D-2）。
3. **zimg 口径黑名单**（`ColorTransformPlan.cs:391-394`）：libzimg 的 bt709 实为纯 γ2.4，与标准定义式分歧 → 默认强制走 H1 引擎，避免"标签说 A、像素是 B"。`policy.Transfer709=Zimg` 可显式放行。
4. **保真路径（FaithfulIccPath）**：ProPhoto 等超广源 → **像素一帧不动 + 附着命名 ICC**（Photoshop/darktable 惯例），且此时不写任何 CICP 标记（写 bt709 与 ProPhoto ICC 冲突，bt2020 是削色假标签）。`d.Faithful=true` 会清空全部输入声明并置 `AttachIcc=false`（iccgen 只能产出可命名空间 ICC，会失配）。
5. **iccgen 三约束**（`DecideOutputColor:369-397`）：仅 RGB 域（10bit+ YUV 报错，需先 `format` 转 RGB）→ `d.IccgenNeedsRgb`；不支持 HDR 传递（smpte2084/arib）→ `frameIsHdr` 时跳过；产出会被部分编码器丢弃（已知 WebP）→ `d.NeedsPostEmbedIcc` 由队列后置嵌入同一张 ICC。
6. **PNG/TIFF/APNG/JXL 是 RGB 原生格式**：`-colorspace`（YUV 矩阵）会与其色彩块冲突，但 `-color_primaries/-color_trc` 仍需保留。
7. **S2"只携带不推断"**：`fromSource`（出口语义来自源容器 CICP）在任何改像素的链跑过后必须置假；`QueueItem.ColorPostOptions/ColorPostProbeInput` 成对登记裁决点，未登记的任务**只许记录"裁决点未参与"，绝不拿按源推断的语义贴标签**（P7d 教训）。
8. **tonemap 链**（`DecideOutputColor:203-249`）：`format=yuv444p,tonemap=...,format=rgb48le,zscale=pin=..:tin=linear:...` —— tonemap 输出 linear 泄漏标签，曾实测 PNG cICP 错 + 画面过暗（YAVG 12416 vs 30770）；末级 zscale 必须同步更新 `outMatrix`，否则 RGB 源的 `m=gbr` 会传给 YUV 编码器导致 0 字节输出。
9. **像素等价即跳过**（`PlanCore:425-429`）：矩阵为恒等且曲线不变 → `ColorAction.None` 只修标注，绝不为"统一"做无谓重编码。

### 3.4 色彩层发现的问题

| 级别 | 问题 | 位置 | 说明 |
|------|------|------|------|
| P1 | **`Resolve` 关键词匹配顺序脆弱**："adobergb" 分支在 "adobe" 之后冗余（永不可达）；`n.Contains("p3")` 会把任意含 "p3" 的名字（如假设的未来 "xxp3xx"）吸进 P3 族；`Contains("2020")||Contains("2100")` 同理宽泛 | `ColorSpaceRegistry.cs:139-174` | 目前上游名称受控，暂无实害；但新增 ICC 推断名/描述串时容易误入歧途。建议先精确后模糊并补测试 |
| P1 | **`Invert3x3` 奇异时返回零矩阵而非 null**：`PrimariesToXyz` 退化输入（共线原色）下 `LinearMatrix` 会拿到全零矩阵，行和=0≠1 恰好会被 `MatrixTo` 校验拦下，但 `LinearMatrixBetweenPrimaries`/`CachedMatrix` 等旁路调用点若不加行和校验，零矩阵会静默把像素变黑 | `ColorSpaceRegistry.cs:597-611` | 建议 `Invert3x3` 直接返回 null 或在所有调用点统一校验 |
| P2 | **`ValidateCicpTriple` 的 knownPrim 漏 `smpte170`/`bt470bg`/`film`** 等合法 ffmpeg token，会误报"非工具链常用值"（仅告警不改行为） | `ColorSpaceRegistry.cs:498-525` | 低风险，按需扩充 |
| P2 | **`ColorMatch RGB` Spec 无色度定义**（无 Rp/Gp/Bp/Wp），任何涉及它的矩阵转换返回 null → 归一化分支静默不生效，仅剩标签路径 | `ColorSpaceRegistry.cs:131-133` | 建议补色度或明确文档化为"仅标签" |
| P2 | **`MapToIntensityTarget(FfmpegOptions)` 高级模式漏 HLG 峰值**：HLG 的名义峰值依赖 OOTF/场景，代码对 HLG 与 PQ 同样回落 1000/用户值——可接受，但注释只提 PQ | `ColorEncodingHelper.cs:71-102` | 交接知悉即可 |
| P2 | **`UltraWideNormalizeTokens` 无参重载默认 SDR**：老调用点若未传 hdr/hlg，HDR 源会被描述成 bt709。现存调用点已传参，但新代码易踩 | `ColorSpaceRegistry.cs:221-230` | 建议废弃无参重载 |
| P2 | **BT.2020 SDR 的 `CjxlColorSpace=null`**：显式选 "BT.2020"(SDR) 目标 + cjxl 输出时没有枚举，实际由 ICC 携带描述——行为正确但依赖调用方理解 `null` 的语义是"不要写 color_space，靠 icc_pathname" | `ColorSpaceRegistry.cs:112-115` | 文档化 |
| ✅ | `ColorEncodingHelper.MapPrimariesTransfer` 已完全委托 `ColorSpaceRegistry`，无独立真值；`DecideOutputColor` 的 ICC/tonemap/归一化互斥通过 `skipHdrTonemap`/`skipAutoBt2020Zscale`/`d.Faithful` 三开关实现，核对未见漏路径 | — | — |

### 3.5 色彩链路验证手段（供接手 agent 回归）

- `docs/TESTING.md` 与 `tests/`（ServiceProbe / UiTestHost / GainMapTestHost）。
- 历史上最有效的验证法：**用 lcms2/djxl 解码输出与理论矩阵值比对**（BradfordAdapt 的坑就是这么抓的）；PNG 输出检查 cICP chunk（`PngCicpService`）+ 像素 YAVG（linear 泄漏检测）。
- 修改 `ColorSpaceRegistry` 后，重点回归：ProPhoto→sRGB（跨白点适应）、Adobe RGB→BT.2020（归一化）、P3 PQ→Rec2100PQ（cjxl 标记 + intensity_target）、SDR BT.2020 源不被当 PQ。

---

## 4. 交接注意事项（给下一个 agent）

1. **约束：不得改变软件结构**（用户明确要求）。上述问题清单只记录现状与建议，未做任何修改。
2. 修改色彩逻辑前先读 §3.3 的八条决策记录——多数"怪异"分支是实测回归的修复，配套注释都带日期与实测数据。
3. 中文注释是该项目的一等公民（大量踩坑记录在注释里），**保留原文**，不要翻译或精简。
4. 状态机/队列状态使用中文字符串耦合（P2-6/S-P1 侧同理），改动措辞需全局搜索。
5. 构建验证：`dotnet build ffmpegPictureUI.sln`；headless 冒烟：`dotnet run -- --headless <输入> --format png`（注意 P0-1 的 120s 队列超时会影响大批量测试）。
6. 修复优先级建议：P0-1（120s 队列超时）→ S-P0-1（Save 克隆漏字段）→ P1-3/P1-4（输出原子性）→ S-P1-1（settings 原子写）→ 色彩层 P1 两项。
7. ICC/色彩相关不要绕过 `ColorSpaceRegistry` 另起真值（历史上多次回归都源于旁路硬编码）。

## 5. 文件职责速查表

| 文件 | 职责（一句话） |
|------|----------------|
| Program.cs | 入口分流 GUI/headless，渲染后端配置，headless 批处理并设退出码 |
| CliParser.cs | headless 命令行解析，组装 QueueItem/JobSpec |
| MainWindow.xaml.cs | 主窗口全部 UI 逻辑：队列操作、设置持久化触发点、工具检测编排 |
| App.xaml.cs | 托盘/主题/本地化/IPC 启动 |
| Models/AppSettings.cs | 持久化设置模型 + ffmpeg/ffprobe 路径计算属性 |
| Models/FfmpegOptions.cs | 单次转码全部编码参数 + quality↔编码器参数双向映射 |
| Models/QueueItem.cs | 队列条目：INPC 状态/日志（5MB 截断）/计时 + 色彩裁决点登记字段 |
| Models/AppJsonContext.cs | NativeAOT 源生成 JSON 上下文（覆盖完整） |
| Services/QueueProcessor.cs | 队列核心：并发调度 + 单项全流程 + 后置链 + 临时文件生命周期 |
| Services/DefaultQueueOrchestrator.cs | 编排包装：终态去重、完成 TCS、120s 兜底超时（P0-1） |
| Services/FfmpegCommandBuilder.cs | ffmpeg 参数拼装（-i 前输入色彩覆盖 / -i 后输出矩阵） |
| Services/FfmpegCommandBuilder.Decision.cs | 色彩裁决点 DecideOutputColor（§3.2） |
| Services/FfmpegRunner.cs | ffmpeg 进程统一执行器（UTF8、优先级、取消杀树；无超时 P1-2） |
| Services/ColorSpaceRegistry.cs | 色彩空间注册表与矩阵数学（§3） |
| Services/ColorEncodingHelper.cs | cjxl color_space/intensity_target 映射薄层 |
| Services/ColorMapping/* | 纯 C# 色彩引擎（H1）：Plan 规划 + 内核 + 曲线/色调映射 |
| Services/CjxlService.cs | cjxl 检测/参数/执行（无 ct/超时 P1-1） |
| Services/CjpegliService.cs / DjxlService.cs / JxlPipelineService.cs | cjpegli/djxl 及双进程管道 |
| Services/JxrService.cs | JxrEncApp 拼装与执行 |
| Services/GainMapEncoder.cs / GainMapDecoder.cs | 纯托管 Ultra HDR 编/解码 |
| Services/IccProfileService.cs | ICC 提取（exiftool）/解析/临时管理 |
| Services/IccProfileBuilder.cs | 纯托管 ICC v4 matrix/TRC 生成（iccgen 盲区） |
| Services/PngCicpService.cs | PNG cICP/sBIT chunk 二进制补写 |
| Services/AppSettingsService.cs | 三层配置加载 + 环境变量覆盖 + v2.0 迁移 + 保存（S-P0） |
| Services/LocalizationService.cs | 三级降级本地化 |
| Services/ExternalToolsDetector.cs / EncoderDetectionService.cs / FormatCapabilitiesService.cs / CpuFeatureService.cs / GpuCapabilityService.cs | 工具/能力探测族 |
| Services/MediaInfoService.cs / MediaInfoParser.cs | ffprobe JSON 探测与解析（带缓存） |
| Services/IpcService.cs | Named Pipe IPC（单实例未接线、线程模型问题 S-P1） |
| Services/ExifToolService.cs | exiftool 检测 + 元数据读写 + ICC 嵌入 |
| Services/BufferPool.cs | ArrayPool 封装（Rent 可能返回更长数组，隐式契约） |
