# lane C1 · 子进程启动与探测链固定成本（HEAD = c18dc4a，只读审查）

> 口径声明：本 lane **没有跑产品 exe、没跑任何计时**。下面所有数字都是**静态计数**（沿调用点数进程）
> 或**估算**（标明误差方向）。唯一用到的实测数据来自两处：`docs/PIPELINE_PERF_ANALYSIS_2026-10-09.md`
> 的旧读数，与仓库里既有的任务日志 `tests/output/jpegli-prog/mx_icc/mj2.log`（HEAD 期产物，用于归因）。

---

## 0 一页结论（HEAD 每任务进程数表）

**参考任务**：输入 1920×1080 静像；`ColorEngine=auto`（默认，`FfmpegOptions.cs:547`）、
`ColorStrategy=Recommended`（默认，`FfmpegOptions.cs:516`）、`MetadataMode=PreserveAll`（默认，`:282`）、
`StripExifGps=true`（默认，`:469`）、无最长边限制、非动画、非 UltraHDR 输入、非 RAW 输入。
"引擎路"= 计划真的改像素（`RawColorPipeline.TransformFileAsync` 走到底）；
"传统路"= 显式 `--color-engine legacy` **或** 引擎直通/被挡（两者进程形态相同，见 §1.3）。

| 目标格式（**输入也是同族静像**）| 路 | ffmpeg | ffprobe | **exiftool** | 外部编码器 | 其中**日志看不见**的 exiftool |
|---|---|---|---|---|---|---|
| jpg | 引擎路 | 2 | 3 | **7** | 0 | 4（探测、意图抽 ICC、MPImage2、JFIF）|
| jpg | 传统路 | 1 | 2 | **6** | 0 | 4 |
| png | 引擎路 | 2 | 3 | **5** | 0 | 2 |
| png | 传统路 | 1 | 2 | **4** | 0 | 2 |
| webp | 引擎路 | 2 | 3 | **5** | 0 | 2 |
| webp | 传统路 | 1 | 2 | **5** | 0 | 2 |
| jxl(Ffmpeg 后端) | 引擎路 | 2 | 3 | **4** | 0 | 2 |
| jxl(Cjxl 后端) | 引擎 cjxl 出口 | 2 | 3 | **4** | 1(cjxl) | 2 |
| avif | 引擎路 | 2 | 3 | **4** | 0（avifenc 只在 `gif→avif` 两步法）| 2 |
| tiff | 引擎路 | 2 | 3 | **4** | 0 | 2 |
| heic(输入→png / →jpg) | 引擎路 | 2 | 3 | **4 / 6** | 0 | 2 / 3（→jpg 多一个静默 JFIF）|
| heic(输出) | — | 0 | 0 | 0 | 0 | 直接拒绝（`Program.cs:237-242`），零进程 |

**每格怎么读**（静态计数口径，全部按"默认参数 + 非 HDR 源 + 磁盘 ICC 缓存已热"）：
- exiftool 基线 **4** = ①`ReadColorTagsAsync`（`FfmpegCommandBuilder.cs:1407`，静默）
  ②意图装配的 `ExtractIccToTempFile`（`ColorIntentFactory.cs:113`，静默）
  ③元数据复制（`QueueProcessor.cs:1955/1960`）④ **复制后又一次 GPS/XMP 显式删除**（`ExifToolService.cs:687-695`，只留 stdout）。
- 增量：`jpg` 输出 +JFIF(`:2011`，静默) +引擎目标 ICC 后置(`:1612`)；`png/webp` 引擎路 +ICC 后置(`:1612`)，
  `webp` 传统路 +`EmbedDroppedFilterIccAsync`(`:1763`，因 libwebp 丢 iccgen)；`jpg` **输入** +MPImage2(`:3134`，静默)；
  `tiff/jxl/avif` 无恒定增量（TIFF ICC 需显式目标 `:1673`、cjxl 出口自己写 ICC `RawColorPipeline.cs:635-641`）。
- ffprobe 基线 **2**（`FfmpegCommandBuilder.cs:1608` csv + `:1695` 显示尺寸）；引擎路 **+1**（`RawColorPipeline.cs:86→1319`）；
  **HDR(PQ/HLG) 源再 +1**（`:1761 ProbePeakNits`，各格都适用）。`.webp/.avif/.jxl` 作**输入**时各 +1（`QueueProcessor.cs:5837/5844/5834`）。
- ffmpeg：引擎路恒 2（解码到 `cms_*.rgb48` + 从 `.out` 编码），传统路 1；cjxl 出口仍是 2（解码 + PNG 中转或 PPM 管道）+1 cjxl。

三条必须单独说的账外事实（都是静态可判，旧账里没有）：

1. **`exiftool` 的"每次"与"每行"不是一回事。** 旧文 §2.3 的 "jpg 族 4 次" 是**日志行数**，
   不是进程数。上面 jpg(传统路) 那一格产生的 `[exiftool]` 行恰好也是 **4 行**
   （`mj2.log:88/91/92/115`），但里面只有 **2 个**进程；另外 4~5 个进程**一行都不留**或只留 stdout。
2. **每任务恒定多付 2 次 exiftool，且这两次都不在旧账的射程内**：
   - `ColorIntentFactory.cs:113` 仍调 `ExtractIccToTempFile`（旧的两步法）——
     P1-a 只把 `ProbeInputColorMetadataCore` 里那一次合并了，**意图装配里这一次没管**。
   - `ExifToolService.cs:687-695`（2026-10-05 的 JXL GPS 修复）在**默认隐私开关**下
     无条件再跑一次显式删除，**与目标格式无关**。
3. **冷启动/首张另有 +1 ffmpeg +1 exiftool**：`IccProfileService.GetOrGenerateStandardIcc`
   第一次为某个 `(primaries,trc,matrix)` 组合生成标准 ICC 时要跑 ffmpeg iccgen（`:354-357`）
   再用 exiftool 从 PNG 里抽出 ICC（`:390`）；磁盘缓存 `stdicc_*.icc` 命中后为 0（`:340-341`）。
   批量摊薄，单张计时会算进下界。

---

## 1 方法与静态计数口径

### 1.1 什么叫"一次进程"
只数 `Process.Start(...)`/`FfmpegRunner.RunAsync`/`ExifToolService.RunRawAsync` 的**实际执行点**，
按调用链可达性判定"本格会不会走到"。不数 `File.Exists`/目录扫描（无子进程）。

### 1.2 定案用的 grep（先做正向命中）
- 正向对照（证明该串真的会出现在日志里）：`安全复制元数据` → **3052 命中 / 3036 文件**（`tests/output/**`）。
- 定案负向命中（证明 JFIF 那一次**从不留日志**）：`JFIFVersion` → **12 命中，全部在 `tests/output/*.patch`**
  （源码补丁），**任务日志 0 命中**。代码理由：
  `ExifToolService.cs:901-914`
  ```csharp
  var args = $"-overwrite_original -JFIFVersion=1.02 \"{targetPath}\"";
  await RunWriteAsync(targetPath, args);
  ```
  第三个形参 `logCallback` **没传**，且整块包在 `catch { }` 里 ⇒ 成功/失败都不出声。
- 同族第二处：`ExifToolService.cs:917-959 GetTagAsync` 成功路径只返回字符串，不写日志
  ⇒ `IsUltraHdrJpegAsync`（`QueueProcessor.cs:3126-3138`）那一次 exiftool 也不出声。
- 缓存两套的定案 grep：`MediaProbeCache` → 仅 2 处（定义 `MediaProbeCache.cs:12` +
  唯一用法 `MediaInfoService.cs:42`），而 `MediaInfoService` 的 4 个调用点全在
  `MainWindow.xaml.cs:2793/5660/6466/5660…`（GUI 详情窗）与 `MediaInfoParser.cs:66` ⇒ **不在队列路径**。

### 1.3 一条典型任务的调用顺序（HEAD 行号）
```
①编码前   QueueProcessor.cs:383  InputMayHaveMultipleFramesAsync   → .webp/.avif 各 1 ffprobe；.jxl 1 ffprobe(-count_frames，带无 TTL 缓存)
          :509  IsUltraHdrJpegAsync(.jpg/.jpeg 输入)             → 纯托管失败时 1 exiftool(GetTagAsync，静默)
②建命令   :987 TryRunColorEngineAsync → :1342 ColorIntentFactory.FromOptions
              ├ ColorIntentFactory.cs:49  ProbeInputColorMetadata  ┐同一缓存组
              └ ColorIntentFactory.cs:113 ExtractIccToTempFile     ┘→ 1 exiftool(独立于探测链)
          :1039 ResolveLegacyVerdictPlan（仅 engPlan==null 时）→ 又一次 FromOptions ⇒ 又一次 1 exiftool
          :1073 BuildArguments → Decision.cs:126/159/398 ProbeInputColorMetadata(缓存命中)
          探测组本体 = FfmpegCommandBuilder.cs:1407 ReadColorTagsAsync(1 exiftool)
                       :1608 ProbeWithFfprobe(1 ffprobe, csv)
                       :1695 ImageGeometry.DisplaySizeAsync(1 ffprobe, -read_intervals 解第一帧)
                       :1761 ProbePeakNits(源为 PQ/HLG 时再 +1 ffprobe)
          引擎路另加：RawColorPipeline.cs:86 ProbeSizeAsync → :1319-1320 又 1 次 DisplaySizeAsync(ffprobe)
③编码     引擎 = RawColorPipeline.cs:132(解码到 cms_*.rgb48) + :236(从 .out 读回) = 2 ffmpeg
          传统 = QueueProcessor.cs:1080 FfmpegRunner.RunAsync = 1 ffmpeg
④编码后   :1105 RestoreMetadataAsync → :1955/:1960 copy(1 exiftool) → Safe 变体 :687-695 显式删除(1 exiftool)
          :1972 EffectiveColorStrategy（**编码后**，缓存可能已过期）
          :1986 CopyIccProfileAsync（仅 S2 生效）｜:2007 RAW 补漏（仅 RAW 输入）｜:2011 JFIF（仅 jpg）
          :1138-1141 独立隐私清理（默认被 CanAbsorbStrip 吸收 ⇒ 常态 0 次）
          :1109 ApplyColorPostSteps → :1543(png cICP, 仅 S2)/:1612(引擎 ICC)/:1690(TIFF)/:1713(保真)/:1763(webp)
```

### 1.4 探测缓存现状（HEAD 值）
| 缓存 | 位置 | 键 | TTL/上限 | 备注 |
|---|---|---|---|---|
| `ProbeCache` | `FfmpegCommandBuilder.cs:1303-1306` | **只有路径** | **10.0 s**（未变）；≥256 条 **Clear()** | 与旧文 `:1303-1305` 同一处，**行号零漂移** |
| `StrategyCache` | `FfmpegCommandBuilder.Decision.cs:514-517` | 路径+格式+声明策略+GainMap | **10.0 s**，≥256 Clear() | 成本闸：`declared != CarryIcc` 直接返回（`:548`）⇒ 默认零开销 |
| `MediaProbeCache` | `MediaProbeCache.cs:14-15` | 路径+mtime | **24 h** | **与上面两套仍互不相通**；且不在队列路径 |
| `AnimatedJxlCache` / `AnimatedWebpCache` | `QueueProcessor.cs:5873/5890` | 路径 | **无 TTL、只缓存确定值** | 旧文 P1-c 点名的"无 TTL"处仍在 |

**会重付整组的情形（静态可判，三种）**：
1. 任务墙钟 > 10 s ⇒ 编码后的 `:1972`/`ApplyColorPostSteps:1528/1674` 全部落在过期缓存上
   （1080p 单线程不会中招；RAW/4K/`-j 8` 抢槽会）。
2. `InputPath` 被中间件改写：`:527`（UltraHDR→PFM）、`:594`（RAW→TIFF）、`:860`（预缩放→PNG/JXL）。
   缓存键是**路径** ⇒ 改写前若已探过一次原文件，改写后**必然**再付整组（exiftool+2 ffprobe）。
3. 一批不同文件数超过 256 ⇒ `ProbeCache.Count >= ProbeCacheMax` 触发整表 `Clear()`（`:1332-1333`），
   跨任务复用归零。单任务内仍命中，故只影响"同源多输出"。

### 1.5 检测类缓存有没有 `_detected` 假修复
- `ExifToolService`：**已按纪律修好**——`Detect` 有 `_attempted` 门控（`:51-59`），
  `DetectCore` 开头即 `_detected=true`（`:226-227`）⇒ 失败也只扫一遍；`ClearCache` 是唯一复位口（`:36-39`）。
  Cjxl/Cjpegli/Djxl/Jxr 四兄弟是 `lock(_gate){ if(!_detected) Detect(); }` + throw 守卫，同族形态，**不重扫**。
- `ExternalToolsDetector`：headless 只走 `EnsureAllDetected`（`:708-716`），注释与实现一致——
  **不做版本探测** ⇒ dry-run 的 0.6 s 里**不含** `--version` 进程（版本探测只在 `ProbeAllTools`，`:723+`，GUI 面板用）。
  `ChooseBestExecutable` 会对候选逐个 `ProbeExecutable`（`:463-469`）起子进程，但受 `_attempted` 门控，
  **每进程一遍**。
- `RawService`：旧文记忆点的那条"备忘录式假修复"**仍在源码里被点名**，且已按实测改正：
  `RawService.cs:73-75`
  > `探测必须在本方法里做，不能只靠 ProbeAllTools 把结论写进 PlatformServices 的备忘录：本方法开头有 _detected 一次性缓存……`
  现状是 `Detect()` 内部逐候选 `Launchable(cand)`（`:77-82`）。⇒ **登记为"文档过期/历史假修复已修"**，
  残留风险只有一条：`_detected` 无失效时间，工具在进程存活期间变得不可启动时不会回流（低优先，非本次性能项）。
- `EncoderDetectionService` / `FormatCapabilitiesService`：全仓调用点**只有 MainWindow**
  （`MainWindow.xaml.cs:1261/1268/2169/2519/2751/5021/5517`）⇒ 队列/headless **不付费**。
  `SupportsJxlLosslessJpegAsync` 以 ffmpeg 路径为键缓存（`EncoderDetectionService.cs:463-476`），
  `FormatCapabilitiesService._cache`（`:45`）同理 ⇒ 无每任务成本。

---

## 2 与 2026-10-09 账本的差异表

| 旧账（§  位置） | HEAD 复核 | 标签 |
|---|---|---|
| §3 P1「exiftool 起两次：`:1407` 读标签、`:1441` 抽 ICC」 | **ICC 那次已合并**，现为 `:1407` + `:1456 ExtractIccFromBase64`（旧 `:1441` 已是注释块尾）。**但 `ColorIntentFactory.cs:113` 那次独立 `ExtractIccToTempFile` 未动** ⇒ 每任务仍付 1 次 | 文档过期 + 新性能项 |
| §3 P1「ffprobe 为拿旋转再起第二次（`:1680`，而 `:1593` 已查到宽高）」 | 行号漂移：csv 探测现在 **`:1608-1611`**（旧 `:1593`→`:1611`，+18）；旋转/显示尺寸调用现在 **`:1693-1698`**（旧 `:1680`，+13）。次数未变（仍是 2 次/组） | 文档过期 |
| §3 P1「探测缓存 TTL 只有 10 s（`:1303-1305`）」 | **值与行号都未漂移**：`FfmpegCommandBuilder.cs:1305 = 10.0`。新增信息见 §1.4 三条重付情形 | 未复现→改判性能项 |
| §3 P1「两套缓存互不相通」 | **HEAD 仍两套**（`ProbeCache` 10 s / `MediaProbeCache` 24 h+mtime），且 `MediaProbeCache` 根本不在队列路径 | 未复现（原样成立） |
| §3 P1 风险「合并后丢 `IccProfileService.cs:58` 的独立点名」 | 该点名**仍在** `:58`（`ExtractIccToTempFile` 内），因为**旧两步法并未被删**、只是探测侧不再调它 ⇒ 风险变成"两条 ICC 口径并存"，不是"点名丢失" | 文档过期 |
| §2.3 表「jpg 族 exiftool **4**」 | 4 是**日志行数**。HEAD 的 jpg 真进程数 = **6（传统路）～7（引擎路）**，其中 2 个完全静默（`GetTagAsync`/JFIF）、1 个只以 stdout 出现（GPS 显式删除）。`mj2.log:88-115` 可逐条对上 | 真缺陷-静默失效(审计侧) / 读数口径 |
| §3 P2「4 次里可合：JFIF `:2011`、ICC 恢复 `:1986`、RAW 补漏 `:2007`」 | 行号**全部未漂移**（`:1986`/`:2007`/`:2011` 逐字命中）。但**可合面变了**：`:1986` 仅 S2 生效（默认 0 次）、`:2007` 仅 RAW 输入（默认 0 次）⇒ 默认路径上真正恒定发生的是 **copy + GPS 显式删除 + JFIF**，而"GPS 显式删除"这次数旧账**没记** | 性能项（P2 需重开） |
| §3 P2「一次 exiftool = **0.170 s**」 | 源码里另一份口径：`ExifToolService.cs:527-529` 写「单次启动就要 **~850ms**」，`QueueProcessor.cs:1137` 与 `:1784` 也按 850 ms 记账。**两个数差 5 倍**，谁都没在 HEAD 复测 ⇒ 所有 P2/P1 收益换算都不稳 | 未判定(需实测) |
| §7 P1-c「没有读数证明重复探测真的发生」 | 静态证据补上了 §1.4 的三条，其中**改写 InputPath** 那条是必然发生（UltraHDR/RAW/预缩放三格）；但"同任务内探测行只出现一次"仍成立 ⇒ 10 s TTL 本身**不是**默认路径的重付原因 | 未复现→改判性能项 |
| §7 P4「引擎两条 ffmpeg 命令 grep `threads` 零命中」 | **HEAD 仍是零命中**（`RawColorPipeline.cs` 内 `threads` = 0），`:99-100` 的 `-threads` 行号未漂移 | 未复现（原样成立） |
| §3 P6「PFM 头壳 `:3158-3166`」 | 行号漂移：现在 **`QueueProcessor.cs:3162-3166`**（+4）。那次 ffmpeg 的唯一作用仍是给无头 `gbrpf32le` 套 PFM 头 | 文档过期 |
| §3 P6「`hdr.rgba`（`:2655`）+ `ReadAllBytesAsync`（`:2688`）」 | 行号大漂移：现在 **`RawColorPipeline.cs:308`** 与 **`:329`**（旧账指的是 `QueueProcessor` 专用管线）。专用管线仍在（`QueueProcessor.cs:2610 ProcessGainMapJpegAsync`），但**默认已由引擎 GainMap 出口接手** | 文档过期 |

---

## 3 候选清单（成本 · 判据 · 风险 · 谁点头）

> 成本一律写成 **静态推算**，并注明它依赖哪个旧实测；旧实测本身有 0.170 s / 0.85 s 两个口径，
> 所以每条都给"若 0.170 成立 / 若 0.85 成立"两档。**误差方向：本表倾向低估**（未计入并发下进程创建争用）。

### C1-1 意图装配的 ICC 抽取改用合并探测 —— **性能项，最干净的一条**
- 现状：`ColorIntentFactory.cs:109-118`
  ```csharp
  if (src == null) { try {
      var (iccPath, iccDesc) = IccProfileService.ExtractIccToTempFile(inputPath);
      src = ColorSpaceIdentify.FromIccFile(iccPath, iccDesc);
  ```
  同一次调用前面 `:49` 已经付过 `ProbeInputColorMetadata`，而那条探测**已经把 ICC 以 base64 取回**
  （`FfmpegCommandBuilder.cs:1407` + `ExifToolService.cs:169` 的 `-b -icc_profile`）。
- 省：**1 次 exiftool / 任务**（静态）。0.170 s 口径 ⇒ 单张 ≈ 8%；0.85 s 口径 ⇒ ≈ 30%。
- 判据（不空转）：① `--dry-run` 与完整任务的 **exiftool 进程数**下降（用 §4-D 的 argv 捕获法，**不要用日志行数**）；
  ② ICC **逐字节对拍**沿用 `icc_equiv.py` 的两道守卫（文件不存在即拒判、带 ICC 样本 <2 即红）；
  ③ `verify-color-caps` / `verify-decision-delivery` 条数不变。
- 风险：**低**。产物不变（同一份 ICC 字节，同一 `ColorSpaceIdentify.FromIccFile` 度量口径）；
  唯一坑是 `ExtractIccFromBase64` 目前**只在 `FfmpegCommandBuilder` 内部**被消费
  （`ExifToolService.cs:168` 注释自陈"消费者只有 ProbeInputColorMetadataCore"），要么把 base64 透出，
  要么在探测结果里带上 ICC 临时路径 ⇒ **别新增第二份"这算不算真 ICC"的标准**（`IccProfileService.cs:89-91` 的纪律）。
- 谁点头：不需要用户（无产物变化、无归因损失）；主循环实测确认后即可动手。

### C1-2 GPS 显式删除收窄到"真的会泄漏的容器" —— **性能项，但改判据**
- 现状：`ExifToolService.cs:687-695` 在 `absorbNow` 为真时**无条件**再跑一次
  `-overwrite_original -m "-GPS:all=" "-XMP:all=" "目标"`。默认 `StripExifGps=true` ⇒ **每个 PreserveAll 任务都付**。
  实测证据（`mj2.log`）里它表现为**第二块** `0 image files updated / 1 image files unchanged`。
- 省：**1 次 exiftool / 任务**（非 JXL 目标；含一次**整文件重写**，大图上是磁盘带宽）。
- 判据：① 对 jpg/png/webp/tiff 各一格，产物 GPS/XMP 读回**逐标签相同**（`verify-metadata-privacy` 口径）；
  ② jxl 格必须仍剥净（旧实测的失败面）；③ 记录省下的进程数（§4-D 法）。
- 风险：**改产物风险 = 有**。旧注释只证了 **JXL** 上"排除式不生效"（`:674-681`），
  把删除式撤出非 JXL 容器**是在外推**。必须先在 §4-E 里对每个容器实测"只靠排除式能否剥净 GPS"，
  否则不许动。**谁点头：用户**（隐私承诺的边界，本仓把 GPS 默认当契约）。

### C1-3 JFIF 并入 copy 那一次命令 —— **P2 的实身，拿归因换秒，要显式标价**
- 现状：`QueueProcessor.cs:2010-2011` → `ExifToolService.cs:901-914`（**无日志、无退出码消费、异常被吞**）。
- 省：**1 次 exiftool / 任务**（jpg/jpeg/jpegli 族）。
- 判据：产物 JFIF 版本标签读回不变（`-jfif`/`JFIFVersion`），且 `verify-metadata-privacy` 不转红。
- 风险：**当前这一步本来就没有归因**（静默），所以"合并会丢点名"在这里**不成立**——
  正确顺序是：**先给 `EnsureJfifHeaderAsync` 补独立点名（0 成本、不改产物）**，再谈合并；
  合并后退出码与 copy 合一，确实丢"JFIF 单独失败"的可点名性 ⇒ 属换秒，**谁点头：维护者**。

### C1-4 UltraHDR 的 MPImage2 兜底并入色彩标签探测 —— **性能项（仅 jpg/jpeg 输入）**
- 现状：`QueueProcessor.cs:3126-3138`：纯托管判据（`GainMapDecoder.IsUltraHdrManagedAsync`）失败后
  才 `ExifToolService.GetTagAsync(path,"MPImage2")` ⇒ **普通 JPEG 恒付 1 次且不出声**。
- 省：**1 次 exiftool / jpg 输入任务**。
- 判据：UltraHDR 输入仍被识别（`verify-gainmap-*` 相关格），SDR JPEG 不误判；进程数按 §4-D 计。
- 风险：**低-中**。`ReadColorTagsAsync` 的 argv（`ExifToolService.cs:169`）目前是**色彩标签专用**，
  把 `MPImage2` 塞进去会让那条探测多一个消费者、并把"探测链"和"输入结构判定"两件事绑在一条命令上
  —— 一旦 MPImage2 读取超时，`ExifToolService.cs:194` 那条点名会同时代表两件事失败 ⇒
  **点名粒度下降**。更保守的做法：让 `IsUltraHdrJpegAsync` **复用已缓存的探测 JSON**（键同为路径）
  而不是加标签 ⇒ 省同一次、不合并判据。
- 谁点头：不需要用户（产物不变）。

### C1-5 引擎的第二次显示尺寸探测去掉 —— **性能项（仅引擎路）**
- 现状：`RawColorPipeline.cs:86` → `:1319-1320`
  ```csharp
  public static Task<(int w, int h)> ProbeSizeAsync(...) =>
      ImageGeometry.DisplaySizeAsync(path, AppSettingsService.Current.FfprobePath, log: null, ct);
  ```
  而 `FfmpegCommandBuilder.cs:1693-1698` 在**同一次缓存探测**里已经调过**同一个**
  `ImageGeometry.DisplaySizeAsync` 并把结果写进 `meta.width/height`。
- 省：**1 次 ffprobe / 引擎路任务**。
- 判据：`verify-geometry-single-source` 不转红（它锁的就是"不得出现第二份 `-show_entries stream=width,height`"
  的**实现**；本改动是把调用点从 2 个减到 1 个，不新增实现）；rawvideo 长度自检
  `RawColorPipeline.cs:149-155`（`rawLen == w*h*6` 精确等式）必须继续成立。
- 风险：**低但有一个真坑**：引擎可能拿到**改写后的 InputPath**（RAW/PFM/预缩放），
  与缓存里那次（同一路径，因为 `:1342` 的 `inputPath` 与 `TransformFileAsync` 的入参同源）应当一致；
  若某条通路把 `log:null` 那次探测的**超时点名**（`ImageGeometry.cs:148-150`）当唯一凭据，减调用点会减点名 ⇒
  保留：改成"先读缓存，未命中才探"，而不是"删掉调用点"。
- 谁点头：不需要用户。

### C1-6 HDR 源的第三次 ffprobe 与尺寸探测合并（`-show_frames` 形态相同）—— **拿归因换秒**
- 现状：`FfmpegCommandBuilder.cs:1761-1772`（`ProbePeakNits`，`-read_intervals "%+#1" -show_frames
  -show_entries frame_side_data_list -of json`）与 `ImageGeometry.cs:50-52`（同一条命令 +
  `-show_entries stream=width,height`）**输出形态几乎同一份**。
- 省：1 次 ffprobe，**只对 PQ/HLG 源**。
- 判据：`verify-hdr-peak-*` 的 `max_content` / `max_luminance` 有理数解析读数不变；
  `displaymatrix rotation` 仍能从 side_data 里取到。
- 风险：**中**。两条独立点名会合成一条：`FfmpegCommandBuilder.cs:1784`（峰值超时）与
  `ImageGeometry.cs:148-150`（尺寸超时）是**两条不同回退**（一个回退名义峰值、一个回退 coded 尺寸）
  ⇒ 合并后**回退口径也合一**，这是本仓最忌讳的"两个不同含义共用一个信号"。
  价值 ≈ 一次 ffprobe（0.039 s 起，见 C1-7），**建议不做，或只在确认尺寸探测变贵之后重估**。

### C1-7 重估"第二个 ffprobe 只值 0.039 s"这个前提 —— **未判定(需实测)**
- 旧文 §7 P1-b 用 **0.039 s**（`ffprobe show_streams`）给旋转探测定价，因此判"收益只有 P1-a 的 1/4"。
  但 HEAD 那次探测是 `-read_intervals "%+#1" -show_frames -show_entries frame_side_data_list`
  （`ImageGeometry.cs:50-52`，注释 `:28-29` 自己写着"**需解出第一帧**，实测 256×256 PNG 均势 ~100 ms"）。
- 静态推算：256×256 已 ~100 ms ⇒ **1920×1080 的显示尺寸探测大概率 ≫ 0.039 s，误差方向：只会更贵**
  （解帧成本随像素数走）。若如此，P1-b 的"不做"结论是**在错的定价上做的**。
- 判据：把 P1-b 的定价换成"实测 `ImageGeometry.BuildProbeArguments` 那条命令在 1080p/4K 的墙钟"
  再决定；**不改产物**，只需主循环跑。

### C1-8 dry-run 下界里到底有几成是宿主启动 —— **未判定(需实测)，但会影响所有换算**
- 旧文把 `--dry-run` 的 0.797/0.644 s 主要归给"逐格式探测/元数据准备"。
  HEAD 静态：dry-run 只走 `Program.cs:338-348` → `MainWindow.BuildQueueItemCommand`（`:3441` BuildArguments）
  ⇒ 进程 = **1 exiftool + 2 ffprobe**（`--color-engine` 不参与，`FromOptions` 在 dry-run **不被调用**）。
  而旧文自己给的地板是"低熵小图 jxl 只要 **0.184 s**"。
- 静态推算：`0.644 − 0.184 − 0.170 ≈ 0.29 s` **无处可归**，最可能是那两次 `-read_intervals`/csv ffprobe
  比 0.039 s 贵得多（与 C1-7 同向）。**误差方向：若 0.184 s 里已含一次 exiftool，则 ffprobe 占比更高、
  宿主启动更低。**
- 判据：**0 个进程的 `--dry-run`（不存在的输入 / 空输入清单）当纯宿主下界**，与 1 图 dry-run 相减，
  把 0.644 s 拆成"宿主 / exiftool / ffprobe×2"三段。这条不做，任何"省一次进程值多少秒"的换算都在猜。

### C1-9 落盘中转现状（旧 P6 的复核，只登记不动）
| 中间件 | HEAD 状态 | 进程影响 | 判据 |
|---|---|---|---|
| `cms_*.rgb48` / `.out` | **仍落盘**：`RawColorPipeline.cs:108` 写、`:132-133` ffmpeg `-f rawvideo` 到文件、变换读写 `.out`、`:236` 编码再读盘 | 解码/编码各 1 ffmpeg，**无法靠管道减少进程数**（进程数不变，只省磁盘往返）；字节 ≈ `w*h*6` ×2 写 ×2 读（1080p ≈ 33 MB/张） | 判据应是**磁盘字节与峰值 RSS**，不是进程数 |
| `hdr.rgba` + `ReadAllBytesAsync` | **仍落盘**：`RawColorPipeline.cs:308` 写、`:329` 整读 | 同上；`w*h*12` | 同上 |
| PFM 头壳 | **仍是独立 ffmpeg**：`QueueProcessor.cs:3162-3166`，唯一作用给无头 `gbrpf32le` 套头 | **−1 ffmpeg** 可行（让 `GainMapDecoder` 自己写 PFM 头，纯格式壳，不动像素）| 判据：PFM 逐字节等价（头 13 B + 像素体 memcmp）；`--dry-run` 不含它 ⇒ 必须用完整任务计 |
| `base.png → base.jpg`（GainMap） | **仍在**：`GainMapEncoder.cs:150-152`/`:159-161`，且每次 PNG 前还落一个 `.raw`（`:450-455`） | 底图+增益图 = **2 ffmpeg(→PNG) + 2 cjpegli(PNG→JPEG)**；cjpegli **支持 stdin**（`JxlPipelineService.cs:53-66` 已是管道范式）⇒ 可减到 2 进程 | 判据：底图 ICC 与增益图字节读回逐字节等价 + `verify-gainmap-thirdparty`；**产物不能变**（GainMap 是产物结构） |
| cjxl 出口的 PNG 中转 | **HEAD 新增落盘**（`RawColorPipeline.cs:556-578`，2026-10-02 为救元数据）——旧 P6 说"该改成 stdin 管道"的方向与它**相反** | +1 ffmpeg；但 PPM/PAM 管道支（`:596-604` + `:616-617`）仍在，HDR/alpha 才走 | 别把它当退化：它换回了源 EXIF，判据应是元数据读回，不是进程数 |

### C1-10 已确认**不是**问题的（别再列进优化清单）
- `ExternalToolsDetector`/`EncoderDetectionService`/`FormatCapabilitiesService` 在 headless/队列路径
  **零进程**（§1.5）。旧文 §4 否证的两条（`PsRenderService` 睡 1 s、`QueueItem.Log` 平方成本）在 HEAD
  未变，且都不在队列。
- 独立隐私清理那次（`QueueProcessor.cs:1149`）默认被 `CanAbsorbStrip`（`ExifToolService.cs:535-537`）吸收，
  **常态 0 次**；`CopyIccProfileAsync`（`:1986`）与 `RestoreRawCaptureFieldsAsync`（`:2007`）默认也各 0 次。
  ⇒ 旧 P2 的"4 次里合掉 JFIF/ICC/RAW 三次"在 HEAD 只能合掉 **JFIF 一次**，其余要等对应格触发。

---

## 4 需要实测才能定案的格子（配方给主循环，本机安静时跑）

> 每条都带**产物守卫**与**失败即 INVALID**，沿用旧文 §5 的三条教训（不建输出目录=假读数、
> `& $exe` 量 WinExe 只量到进程创建、夹具没生成还在计时）。

- **A. exiftool 单次启动的真实成本（0.170 vs 0.85 口径冲突）**
  同一命令两种条件：① `exiftool -json -G <1080p.jpg>`（HEAD 探测 argv）② `exiftool -ver`。
  各 best-of-5 + **中位数并报**，同时记录 `LANG/LC_ALL`（`mj2.log` 里每次调用都带 4 行 perl locale 警告，
  可能把 stderr 排空成本算进墙钟）。**判据**：两个口径哪个成立，直接改写 C1-1/2/3/4 的收益表。
- **B. 显示尺寸探测（`ImageGeometry.BuildProbeArguments`）的像素相关性** → 定 C1-7。
  `256×256 / 1920×1080 / 3840×2160` 三档，各 best-of-3；对照臂 = 旧 csv 那条
  （`-show_entries stream=width,height,pix_fmt,…`）。**产物无关**，纯探测，可直接跑 ffprobe 不必起产品。
- **C. 每任务**真**进程数**（替代日志行数口径）→ 定 §0 整张表。
  配方：用 argv 捕获，不要按 `[exiftool]` 字符串数行。两种任一种：
  ① 把 `ExifToolService.DetectedPath`/`FfmpegPath` 指到 shim（bat/ps1 记录 `%*` 后再转发真程序）；
  ② `Get-CimInstance Win32_Process -Filter "Name='exiftool.exe'"` 10 ms 轮询采样 + 去重（会漏，**只给下界**）。
  夹具：单张 1080p jpg→jpg，**分别跑引擎路 / `--color-engine legacy` 两臂**，各臂产物数=1 才计数。
  **判据**：§0 表每格 ±0 才算对上；对不上就是我把某个静态分支算错了，请回退本表。
- **D. dry-run 0.644 s 的三段拆解** → 定 C1-8。
  三臂：① 不存在的输入（应零子进程、应报错退出）② `--dry-run` 1 图 ③ `--dry-run` 1 图 + `--color-engine legacy`。
  ② − ① = 探测链；② − ③ 应≈0（若不为 0，说明 dry-run 里 `FromOptions` 有可达路径，我的静态账要改）。
- **E. GPS 排除式在非 JXL 容器上到底生不生效** → 定 C1-2（**这是隐私承诺，必须先证**）。
  夹具：带 GPS 的 jpg；对 png/jpg/webp/tiff 四目标，跑两种命令形态各一次：
  ① `-TagsFromFile src -all:all --GPS:all --XMP:all …`（排除式）② 排除式后再 `-GPS:all=`（现 HEAD 行为）。
  判据：`exiftool -v3` 逐段读回 GPS 标签集合**完全为空**才算"排除式够用"。任一格式残留 ⇒ C1-2 直接作废。
- **F. TTL 10 s 是否真的在批量/慢任务上重付** → 定 §1.4 情形 1。
  配方：同一批跑 `-j 1` 与 `-j 8`，夹具选一张**编码 > 10 s** 的（8K 或 16-bit RAW→PNG），
  用 C 的 argv 捕获数进程；判据 = 同一路径的 `ReadColorTagsAsync` 出现次数 ≥2。
  旧文 §8 也点名这条未测，是并发占比能否摊薄的唯一判据。
- **G. UltraHDR / RAW / 预缩放三格的"整组重付"**（§1.4 情形 2，静态已判定会重付，缺量级）。
  夹具：`uhdr.jpg`（带 MPF）、一张 `.ARW`、一张 6000×4000 配 `--max-dimension 2000`。
  判据：探测组（exiftool + 2 ffprobe）出现**次数** = 2 ⇒ 证实；=1 ⇒ 我的静态链漏了早退分支。
