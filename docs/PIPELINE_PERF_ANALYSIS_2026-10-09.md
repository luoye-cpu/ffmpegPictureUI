# 管线性能分析（2026-10-09）

## 0. 判词

**单张图的墙钟时间里 81–91% 花在编码之外。** 实测：产品转一张 1920×1080 jpg 用 2.033 s，而把它**自己日志里打出的同一条 ffmpeg 命令**逐字重跑只要 0.174 s（有产物）⇒ 真编排开销 ≈ 1.86 s。`png` 88.8%、`webp` 81.3%。

所以最值得优化的不是任何编码器旋钮，而是**每任务的固定成本**：探测链（dry-run 就要 0.78–0.81 s）+ 元数据后置链（jpg 族 4 次 exiftool）+ GUI 入队前逐图建命令。编码器本身在这批读数里只占 1/10。

## 1. 测量条件（每条读数自带，过期就重跑）

- 被测：`src/FfmpegGui/bin/Release/net11.0/win-x64`，`FfmpegGui.dll` sha256 前 16 = `C7C68592C5E49928`（含本批 R0-b 改动）
- 调用方式：`Start-Process -PassThru` + `WaitForExit` 限时 120–300 s + 超时可杀树；**一律 best-of-3 取 min**（沿用 `tests/diagnostics/Modes/bench.cs` 的方法论）
- 夹具（先自检再生成读数；前两版就是栽在"夹具没生成还在计时"）：
  `flat.png` 1920×1080 低熵 102 KB｜`nois.png` 1920×1080 真实熵 5,060 KB｜`nbig.png` 3840×2160 真实熵 20,272 KB
- 守卫：每臂要求产物数达标，不足直接记 `INVALID`，不当读数
- `TMP/TEMP` **未改动**（挪进仓库会触发挂死，见 §6）；`--log-level debug` 的测量污染 = 0.034 s（E 段实测）
- 机器状态：C 盘 99% 满（剩 13 G）；`ffmpeg.exe` 被拦写 `%TEMP%` ⇒ **色彩引擎/GainMap 路径本机当前不可测**（§6）
- 脚本与原始日志：`tests/output/jpegli-prog/{perf-battery3,4,5,6}.ps1`、`perf3.out`、`perf4.out`、`perf5.out`、`perf6b.out`

## 2. 实测账

### 2.1 编码之外 vs 编码本身（逐字对照，最硬的一条）

| 格式 | 产品 | 产品自己的命令逐字重跑 | 真编排开销 | 占比 | 最简手敲 |
|---|---|---|---|---|---|
| jpg(mjpeg) | 2.033 s | 0.174 s（产物 1） | 1.859 s | **91.4%** | 0.163 s |
| png | 2.417 s | 0.271 s（产物 1） | 2.146 s | **88.8%** | 0.164 s |
| webp | 2.005 s | 0.375 s（产物 1） | 1.630 s | **81.3%** | 0.171 s |

### 2.2 固定开销下界：`--dry-run`（探测 + 入队 + 建命令，完全不编码）

`jpg 0.797 s`｜`png 0.781 s`｜`webp 0.808 s`（1920×1080 真实熵）；低熵小图时 `jxl` 只要 0.184 s ⇒ 这 0.8 s 里大头是**逐格式的探测/元数据准备**，不是通用启动。

单进程成本（同步等待，3 次取 min）：`exiftool` 一次 **0.170 s**｜`ffmpeg` 解码到 null 0.055 s｜`ffprobe show_streams` 0.039 s。

### 2.3 每任务实际起的进程（从 `--log-file` 数）

| 臂 | ffmpeg 行 | 外部编码器行 | exiftool 行 | 日志总行 |
|---|---|---|---|---|
| jpg(mjpeg) | 1 | 0 | **4** | 90 |
| jpg(JPEG LI) | 0 | 1 | **4** | 30 |
| webp | 1 | 0 | **4** | 92 |

⇒ 4 × 0.17 s ≈ **0.68 s/图** 只花在 exiftool 上（与 §2.2 的 0.8 s 量级吻合）。审计侧给出的每任务启动次数为 6（png/avif）～8（dng），其中 exiftool 3–5 次、ffprobe 2–3 次。

### 2.4 并发曲线（16 张 1920×1080 真实熵 → jpg，产物 16/16 才计数）

`-j 1` 27.09 s（1.693 s/图）｜`-j 4` 7.49 s（0.468）｜`-j 8` 4.67 s（**0.292**）⇒ 8 线程只拿到 5.8× 加速（效率 73%），而逐字 ffmpeg 下限是 0.174 s/图 ⇒ 剩余差距就是 §2.1 那笔固定开销在并发下被放大。

### 2.5 后端差异（8 MP 真实熵，单张）

`jpg(JPEG LI) 1.42 s` vs `jpg(mjpeg) 2.82 s`｜`webp 3.43 s`｜`png 5.66 s` ⇒ **mjpeg 这条路比 jpegli 多付约 1.4 s**，且它俩的 exiftool 次数相同（§2.3 各 4 行）⇒ 差额在编码前置/后置的步骤数上，需 P5 拆分才能定性。

## 3. 优化清单（按实测成本排序，每条带判据）

### P1 把每任务探测链的 exiftool/ffprobe 合并 —— 预期 0.3–0.5 s/图（**① 已落地见 §7**）

- 现状：同一次探测里 exiftool 起两次（`FfmpegCommandBuilder.cs:1407` 读色彩标签、`:1441` 抽 ICC）；ffprobe 为拿旋转信息再起第二次（`:1680`，而 `:1593` 已查到宽高）；探测缓存 TTL 只有 **10 s**（`:1303-1305`）⇒ 编码超 10 s 的任务下一轮整组重付。
- 改法：一次 `exiftool -json -G` 顺带 `-icc_profile>` 出文件；把 rotation side data 折进第一次 ffprobe 的 `-show_entries`；缓存键从"路径+10 s"改成**任务作用域**（同 `MediaProbeCache` 的 24 h/mtime 思路，两者目前互不相通）。
- 判据：`--dry-run` 墙钟（当前 0.797 s）与日志里 exiftool 行数下降；且 `verify-decision-delivery`、`verify-color-caps`、`verify-metadata-privacy` 三条不转红。
- 风险：合并后丢掉 `IccProfileService.cs:58` 那条"ICC 提取超时"的独立点名行 ⇒ 必须在合并调用上保留等价超时点名，否则违反"失败要响"。

### P2 元数据后置链合并 —— 预期 0.34–0.5 s/图（jpg 族）

- 现状：4 次 exiftool（复制 `QueueProcessor.cs:1955/1960`、JFIF `:2011`、隐私清理已并入复制见 `ExifToolService.cs:529-531`、条件性 `:1149`、ICC 回带 `:1986`）。
- 改法：JFIF 写入并入同一条复制命令；RAW 补漏（`:2087`）与复制合成一次白名单写入。
- 判据：§2.3 的 exiftool 行数 4 → ≤2；产物标签集合用 `verify-metadata-privacy` 与 `verify-gainmap-thirdparty` 的读回口径不变。

### P3 GUI 入队前逐图建命令 —— 批量启动前 N×250 ms（UI 线程）

- 现状：`MainWindow.xaml.cs:3126-3140` 对每个 item 串行 `BuildQueueItemCommand(qi)`，其中 `EffectiveColorStrategy` 的代码注释自己写着 S2 策略下 **中位 +250 ms/图**（主要 exiftool 抽 ICC，`FfmpegCommandBuilder.Decision.cs:536-541`）。另外"仅显示报错"勾选时每次 item 更新都全量重过滤（`:3205-3208`）。
- 改法：建命令移到后台/懒建（第一项开始时才需要），过滤改增量。
- 判据：点"开始"到第一项 `[cmd]` 落日志的墙钟，N=20 与 N=100 对比当前应呈线性下降。

### P4 并发效率 73% 的天花板：引擎路径不吃线程参数

- 现状：`FfmpegCommandBuilder.cs:99-100` 无条件下发 `-threads`，而**色彩引擎（默认执行路径）手搓的两条 ffmpeg 命令里 grep `threads` 零命中**（`RawColorPipeline.cs`），队列的自适应线程（`QueueProcessor.cs:263-266`）只喂到 legacy 线 ⇒ 并发 8 时每个任务的 ffmpeg 各自吃满核（N×核数超饱和），且全局**只有内存钳制**（`ClampConcurrencyByMemory` :142-152）没有 CPU 钳制。
- 改法：把同一份 `options.Threads` 下发到引擎的两条命令；或给并发加 CPU 轴钳制。
- 判据：`-j 8` 下的 §2.4 读数（当前 4.67 s/16 图）与每任务 ffmpeg CPU% 峰值；改后墙钟降而 RSS 不升才算真收益。
- 注：`cjpegli --num_threads` 那道门被恒 false 的 `CjpegliMultiThreadAvailable` 挡住（D-2 已登记），**且该工具是否真支持此参数本机未测** ⇒ 不在本清单里许愿。

### P5 mjpeg 路比 jpegli 路多 1.4 s（8 MP）—— 先拆分再谈优化

- 判据（先做这个）：同一张 `nbig.png` 两路各自 `--dry-run`/完整/probe 三段计时差，加日志步骤序号对齐 ⇒ 定位到具体步骤后再列改法。**当前只有总差值，没有拆分**，不许直接动手。

### P6 落盘中转的可管道化点（引擎路本机不可测 ⇒ 只登记）

- 已读原文确认：`QueueProcessor.cs:3158-3166` 那次 ffmpeg 的唯一作用是给无头 `gbrpf32le` 裸流套一个 PFM 头（纯格式壳，像素搬运都算多余）；GainMap 的 `hdr.rgba`（`:2655`）+ `ReadAllBytesAsync`（`:2688`）+ `base.png → base.jpg`（`GainMapEncoder.cs:151/174`，而 cjpegli 支持 stdin，见 `JxlPipelineService.cs:48`）。
- 判据（等 §6 解堵后）：每任务 ffmpeg/cjpegli 子进程数 4 → 2、临时目录峰值字节 ≈ 0。

### P7 只记账、不动的（改产物或已优化过）

- `format=rgb48le` 恒挂（`RawColorPipeline.cs:99`）、YUV 目标恒插一次 `scale`（`FfmpegCommandBuilder.Decision.cs:377-378`）、`-compression_level 0` 对 ppm/pam 是 no-op ⇒ 前两者改变产物，不能当性能项删。
- 全帧缓冲不进池是**有意的正确性取舍**（`RawColorPipeline.cs:1134-1138`：`BufferPool.Rent` 内部钳到 256 MB，请求超限会拿到较小数组）⇒ 不是疏漏，别当优化目标。
- 曲线表 `BuildTables` 每次建 384 KB/2×65536 次 pow，代码自估"几 ms"且有 25 万像素门槛（`:1082-1090`）⇒ 未测出量级前不排名。

## 4. 被否证的候选（别再拿出来当优化项）

- `PsRenderService` 的"两条异步命令间固定睡 1 s"：grep `Task.Delay(1000)` **零命中**，且它只服务于工具状态检测（`MainWindow.xaml.cs:5192/5206`），不在图像队列。
- `QueueItem.Log` 是"平方成本"：实现是**快照长度 + 锁内只接增量**（`QueueItem.cs:66-91`，注释还记录了为什么不能用最长公共前缀缝合）⇒ 已优化过。
- 用"切默认后端到 cjpegli"来提速：会丢 ICC 标注（见 `docs/JPEGLI_GAINMAP_PANEL_PLAN_2026-10-09.md` §1c 的 584 B ICC vs 零标注实测），是色彩正确性问题不是性能收益。

## 5. 本轮我自己造过的三类假读数（留档防重犯）

1. 用 `& $exe` 给 WinExe 计时 ⇒ 只量到进程创建时间（0.006 s、产物 0），整批作废。
2. `TimeCase` 建了输出目录却没把 `--output` 传给产品 ⇒ rc=0 但零产物，差值全是假的。
3. 夹具生成命令用了这个 ffmpeg 不存在的 `noise=shape=` 选项 ⇒ 输入根本不存在，相关臂 rc=2。
⇒ 现在每个臂都带"产物数守卫 + 夹具自检 + 限时杀树"，这三样是这次分析能出数的前提。

## 6. 环境级阻塞（不是优化项，但挡住一半测量）

- `ffmpeg.exe` 被拦写 `%TEMP%`（`Permission denied`）；同一目录 `py` 可写、ffmpeg 写仓库目录也可写 ⇒ 按可执行文件的路径策略拦 Temp。后果：所有走色彩引擎/GainMap 的任务（`cms_*.rgb48`、`hdr.rgba`）一律 `exit -13` ⇒ **oracle L1 的 171 条红全部由此而来，不是代码回归**。
- C 盘 99% 满（剩 13 G），Temp 内 16,457 项。
- 把 `TMP/TEMP` 指进仓库树内试图绕开 ⇒ **挂死 47 分钟**（无子进程、临时目录空、无输出）。**成因未定位**，不写成结论；要绕开得换路线（例如给中间件目录加一个配置项，指到工作区外的可写卷——这本身也是 P6 的一部分）。

## 7. 实现记录（同日收口）

### P1-a 已落地：探测合并（省一整次 exiftool/图）

改动三处：

- `ExifToolService.ReadColorTagsAsync` 的 argv 追加 `-ProfileDescription -b -icc_profile` —— 实测一次调用即在 JSON 里同时给出 `ICC_Profile:ProfileDescription` 与 `ICC_Profile:ICC_Profile`（`base64:` 前缀）。
- `IccProfileService.ExtractIccFromBase64`（新）：把 base64 还原成临时 .icc，**校验口径逐条照抄旧路径**（<128 B ⇒ 视为无 ICC；`IsValidIccProfile` 不过 ⇒ 删文件按无 ICC 处理），"这算不算真 ICC"仍只有一份标准。
- `FfmpegCommandBuilder.ProbeInputColorMetadataCore`：不再另起 `ExtractIccToTempFile`；同时删掉那句 `if (... || true)` 的恒真死条件。

验收（`tests/output/jpegli-prog/`）：

| 判据 | 读数 |
|---|---|
| ICC 逐字节等价（旧两步法 vs 合并探测） | `icc_equiv.py`：**fail=0，带ICC样本=3**（`srgb.jpg`/`p3.jpg`/`p3.png` 旧 584 B = 新 584 B 逐字节相同；`noicc.jpg` 两边都无）——脚本带两道守卫：文件不存在即拒判、带 ICC 样本 <2 即判红（防"两边都空"混成假通过） |
| 计时（同夹具同 harness，best-of-3） | `--dry-run`：jpg **0.797 → 0.644 s**、png 0.781 → 0.615、webp 0.808 → 0.575（省的量正好 ≈ 一次 exiftool 0.170 s） |
| 色彩门禁 A/B（新构建 vs HEAD 构建） | `decision-delivery` 58/16/skip4 **两侧完全相同**；`color-strategy` 47/26 **相同**；`color-caps` 16/0 **相同** ⇒ 无回归 |
| 构建 | 0 警告 0 错误；`p1a.patch` 为回退凭据 |

### P1-b / P1-c 不做（判据不成立，不是偷懒）

- P1-b（第二个 ffprobe 折回第一个）：单次 ffprobe 实测 **0.039 s**，收益是 P1-a 的 1/4；而那次"多余"的探测是**显示尺寸**（`ImageGeometry`，注释里带 6000×4000 锁错边的实测教训），绕开它就是造第二份宽高口径（本仓为它上过结构锁）。⇒ 不做。
- P1-c（探测缓存 TTL 10 s → 任务作用域）：**没有读数证明重复探测真的发生**（同任务内探测行只出现一次），且 `#26` 有过"把取消退化值写进缓存"的前科。⇒ 先证明收益再动，本轮不做。

### P2（元数据后置链合并）待决策，不在本轮做

一次 exiftool = 0.170 s，jpg 族后置里 JFIF（`QueueProcessor.cs:2010-2011`）、ICC 恢复（`:1986`）、RAW 补漏（`:2007`）各起一次 ⇒ 合并理论省 ~0.34 s/图（约 17% 单图墙钟）。**但**今天每步各有独立退出码与独立点名（ICC 恢复失败有专门两行诊断、补漏失败与复制失败分开处理），合并成一条命令就只剩一个退出码 ⇒ 拿错误归因换 0.34 s。这是取舍不是技术障碍，需要用户点头才做。

### P4（引擎路径补 `-threads`）本机取不到判据 ⇒ 不动

目标路径正是被 §6 的 Temp 拦截打死的那条（引擎/`cms_*.rgb48`）。且 `-threads` 放在解码段影响的是解码器线程，而引擎的大头是**滤镜图**（要 `-filter_threads`/`-filter_complex_threads`，本仓当前一处都没发）。⇒ 没有可测判据前不改；§6 解堵后先测 `-j 8` 墙钟与每任务 ffmpeg 线程数再定。

### 本轮新增的环境观测（影响任何计数型读数）

同一构建连跑 `decision-delivery` 得到 **64/10** 与 **58/16** 两种结果 ⇒ 该门禁在本机当前状态下**本身有抖动**（Temp/盘压相关）。因此上面的 A/B 用的是"新旧条数是否相同"而非"是否全绿"，且这条抖动需单独登记，别拿它当代码回归。



## 8. 未证边界

- §2.1 的占比来自**单张**任务；批量 `-j 8` 时同一份探测链是否被复用（TTL 10 s 的窗口在并发下命中率如何）未测。
- P5 的 1.4 s 差额只有总账，没有分步账。
- 引擎路、GainMap 路的全部性能数**本机当前取不到**（§6）。
- 并发读数只有 `-j 1/4/8`，没测 `-j 16`，也没测大图并发（大图会把固定开销摊薄，占比会低于 81–91%）。
- `jxl` 那组 §2.3 的行数来自一条失败臂（rc=1，Temp 拦截）⇒ 未计入结论。
