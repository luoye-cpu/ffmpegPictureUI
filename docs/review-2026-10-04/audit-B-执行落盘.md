# 审查线 B — 执行/落盘层（标签 vs 像素）· 2026-10-04

> 命题：产物的**色彩声明**（ICC / CICP / TIFF 色度标签）是否与**像素的真实色彩**一致。
> 方法：以代码为准；每条发现给 `file:line` + 具体失败场景；已跑通真机端到端两格（见 P1-1 证据）。
> **快照说明**：审查期间工作区被并行修改（`src/**` mtime = 2026-10-04 10:26~10:27，出现新属性
> `FfmpegOptions.HasExplicitColorTarget`）。本报告基于 **HEAD `bdd4b0b` + 上述未提交工作树** 这一快照；
> 若后续再改，请按行号复核。

---

## ① 发现表（按级别排序）

| 级别 | 位置 | 问题 | 为何错（失败场景） | 证据 | 门禁覆盖? |
|---|---|---|---|---|---|
| **P1** | `QueueProcessor.cs:4360-4378`（注入）＋ `FfmpegCommandBuilder.Decision.cs:169-202`（消费） | **JXL(SDR) 输入的目标色被静默丢弃/覆盖**：该分支把「探测到的**输入**色」写进 `ColorPrimaries/ColorTrc`，而这两字段同时被 `DecideOutputColor` 当作**输出目标**（`advanced=HasExplicitColorTarget` 为真 ⇒ `hasExplicitTarget` 恒假 ⇒ `ColorSpace` 目标与用户显式 CP/CT 都不生效、也不加任何 zscale 转换）。代码注释 `:4372` 写「目标仍由 ColorSpace 决定」——**与代码相反**。 | 场景 A：`-i src_lossy.jxl -f png --color-space "Display P3"` ⇒ 期望转 P3 并标 `smpte432`；实际命令行 `-color_primaries bt709 -color_trc iec61966-2-1`（注入值）、产物 cICP=`primaries=1,transfer=13`（bt709/sRGB），**P3 目标消失**。场景 B：`--color-primaries smpte432 --color-trc iec61966-2-1`（用户显式要 P3）⇒ 同样被注入覆盖成 `bt709`，产物 cICP=1，**无任何告警**。 | 真机两格（日志见下）；`[pipe] 注入 JXL 真实色彩: p=bt709 t=iec61966-2-1` + `[cmd] … -color_primaries bt709 … -frames:v 1`；产物 `ffprobe color_primaries=bt709` | **未覆盖**（`tests/scripts` 无「JXL 输入 + `--color-space` 目标」断言） |
| **P1** | `FfmpegCommandBuilder.cs:832-840`（输入声明）＋ `FfmpegCommandBuilder.Decision.cs:173,197-202`（输出目标） | **`ColorPrimaries/ColorTrc` 一对字段两用的通用残余**：`BuildColorArgsSplit` 分支 1 把它们当 `-i` 前的**输入声明**；`DecideOutputColor` 高级分支把它们当**输出目标**。2026-10-03 的 `ColorSourceDecl*` 拆分**只覆盖了 RAW 路径**。`FfmpegOptions.cs:239` 注释称「`ColorPrimaries/ColorTrc` 从此只表示输出目标」——**与代码不符**（P1-1 即此结构的实例）。 | 任一「内部把探测到的输入色塞进 CP/CT」或「用户只想声明输入、另有 ColorSpace 目标」的路径，都会让同一值既当输入又当输出，二者语义冲突时**输出目标被静默吃掉**。 | 代码：`BuildColorArgsSplit` 分支 1 用 `HasExplicitColorParams` 取 CP/CT 作输入；`DecideOutputColor` 高级分支取同一对作 `outP/outT` | 部分（P1-1 未覆盖；RAW 路径已由 `ColorSourceDecl*` 覆盖） |
| P2 | `FfmpegCommandBuilder.cs:841-852` | **死分支**：`else if (!HasExplicitColorParams && CP非空 && CT非空) && (…)`。因 `HasExplicitColorParams` 现已值派生（= CP/CT/Matrix 任一非空），`!HasExplicitColorParams` ⇒ 三者全空 ⇒ 第二个合取项恒假 ⇒ **该支不可达**。 | 无行为后果（纯死代码），但 2026-10-04 值派生重构的残留会误导：读者以为「非高级模式下 CP/CT 也当输入声明」这条兼容逻辑仍在生效，实际早被分支 1 吸收。 | `HasExplicitColorParams` 定义 `FfmpegOptions.cs:190-193` | 不适用 |
| P2 | `QueueProcessor.cs:631`、`:4373`；`FfmpegOptions.cs:218` | **失效的 UI 布尔写入 + 过期注释**：`:631` 写 `UseAdvancedColorParameters=true` 并注「关键：启用高级模式让 CP/CT 生效」、`:4373` 同；但全仓已**无任何读点**（Q4 已核），该布尔自 2026-10-04 起纯 UI 状态。`FfmpegOptions.cs:218` 注释仍称 `ColorIntentFactory` 按该布尔判 `targetExplicit`——实际已改值派生。 | 无行为后果，但「写了却没作用」的开关 + 反向注释会在后续维护中诱发「改了开关以为生效」的误判。 | 全仓 `grep UseAdvancedColorParameters`：仅写入点（`MainWindow:2876`、`FfmpegOptions:47`、`QueueProcessor:631/4373/4954`）与注释，**零读点** | 不适用 |
| P2 | `FfmpegCommandBuilder.Decision.cs:519-561`（缓存键 `:530`） | **`EffectiveColorStrategy` 的 10 s 缓存键不完整（当前无后果，属埋雷）**：键 = `inputPath|Format|declared|JpegGainMap`，**缺** `ColorPrimaries/ColorTrc/Matrix`、`IccFilePath`、`ColorSpace`。 | 现状**无正确性问题**：`ResolveStrategy` 里唯一会改变策略**值**的分支是 GainMap 覆盖，它只依赖 `it.GainMapRequested`（在键中）；其余三支恒返回 `declared`。⚠ 但同文件的 `ResolveLegacyVerdictPlan`（`:582-586`）**刻意不缓存**并明写「用不全的键缓存裁决会引入静默错误」——两处口径相反。若日后装配 `it.Manual` 或给 `ResolveStrategy` 加依赖选项的分支，此缓存将静默返回旧策略。 | 代码：`:530` 键；`ResolveStrategy`（`ColorStrategyPlanning.cs:389-416`）；`it.Manual` 恒 null（见下条） | 未覆盖 |
| P2 | `QueueProcessor.cs:1741` | **`userSpecifiedColor` 用 `HasExplicitColorParams`（含 `ColorMatrix`）**：`--color-matrix X` 单独出现即被判「用户指定了色彩」⇒ `:1825` 的源 ICC 回带条件 `(!userSpecifiedColor || containerDropsIcc)` 对「不丢 ICC 的容器」（png/tiff/jpg）为假 ⇒ **跳过源 ICC 恢复**。 | 疑似「该嵌却没嵌」：携带策略 + `--color-matrix bt709` + 带源 ICC 的 PNG 源 ⇒ 源 ICC 不回带，而 png 无 iccgen 通路（`GetFormatColorCapabilities` png 返回 useIcc=false）⇒ 产物可能零色彩描述，查看器按 sRGB 解读广色域像素（洗白）。**未实测**（无现成 P3 PNG 素材），列为待验证。 | 代码：`:1741`、`:1825`；`GetFormatColorCapabilities` png 支 `FfmpegCommandBuilder.cs:1026-1027` | 未覆盖（待验证） |
| P3 | `ColorMapping/ColorIntentFactory.cs:172-222` | **`it.Manual` 从未装配**：`FromOptions` 的对象初始化器不含 `Manual`，全仓无 `it.Manual =` 写点 ⇒ `ResolveStrategy:405` 的 `it.Manual?.HasAny ?? false` **恒 false** ⇒ `GainMap + carry` 永远解析为 `Recommended`，`Manual` 分支死。 | 跨线（决策层）但影响执行层：GainMap 底图映射在「手动目标」下会走 Recommended 而非 Manual。属决策层归属，供线 A 参考。 | `ColorIntent.cs:160` 有字段；`ColorIntentFactory.FromOptions` 无赋值 | 未覆盖 |
| P3 | `QueueProcessor.cs:1400-1478`（PNG cICP 步）与 `:1484-1506`（引擎 ICC 步） | **顺序**：cICP 在**引擎目标 ICC 之前**写入。cICP 的 `hasIccp` 读的是「此刻」的文件状态（`:1408`），而引擎 ICC 随后才由 exiftool 嵌入。 | 若两处语义不一致（cICP 取 legacy `EffectiveOutputColorTokens`、ICC 取 `engPlan`），会产出 `cICP` 与 `iCCP` 互相矛盾的 PNG；`:1291-1293` 的守卫**只告警不阻断**。当前两条线同源（guard 未触发）⇒ 属**潜在窗口**，非现行缺陷。 | `:1408`、`:1409`、`:1484-1506`、`:1288-1294` | 部分（有告警，无阻断） |
| P3 | `QueueProcessor.cs:1548-1557` | **TIFF 目标 ICC 只在 `ColorPrimaries` 非空时触发**：`else if (opts.HasExplicitColorTarget && !IsNullOrWhiteSpace(opts.ColorPrimaries))`。 | 只给 `--color-trc`（无 primaries、无 ColorSpace）时 `tiffPrimaries` 保持 null ⇒ 跳过目标 ICC ⇒ TIFF 零色彩描述。TIFF 恒 SDR 且无 CICP 通路 ⇒ 下游按 sRGB 误读。场景罕见（只给 trc 的目标轴），列为低。 | `:1533-1557` | 未覆盖 |

> 说明：P1 两条为**同一结构**（一对字段两用）的具体与一般形态；P1-1 是可直接复现的实例。

---

## ② 逐格式一致性结论表（声明 vs 像素）

| 格式 | 声明什么（落盘标签） | 像素是什么 | 一致? | 判据出处 |
|---|---|---|---|---|
| png / apng | ffmpeg 输出侧 `-color_primaries/-color_trc`（写 cICP/iCCP）＋后置 `PngCicpService` 校正/补写 cICP 并剥离 `gAMA/cHRM/sRGB` | 引擎核按 `plan.Dst` 变换；legacy 由 `PixelChains`（zscale/tonemap）产出 | ✅（常规/引擎路径） | `RawColorPipeline.BuildEncodeArgs:907-940`；`QueueProcessor.cs:1400-1477`；`PngCicpService.cs:221-231` |
| tiff / tif | exiftool **后置目标 ICC**（Recommended/Manual）；CarryIcc 回带源 ICC；`capTrc` 恒 `iec61966-2-1` | 引擎变换 / legacy zscale；像素 trc = `dstT=capTrc` | ✅ | `QueueProcessor.cs:1559-1588`；`Decision.cs:999-1002` |
| webp | 编码器**丢弃** iccgen ICC ⇒ `NeedsPostEmbedIcc` 触发 exiftool 后置 ICC（无 CICP 通路） | zscale/引擎变换 | ✅ | `Decision.cs:470-471,618-619`；`QueueProcessor.cs:1642-1657` |
| jpg / jpegli | iccgen（legacy，APP2）/ cjpegli `-x color_space=`（裸流生效） | zscale | ✅ | `Decision.cs:417-421`；`CjpegliService.cs:281-288` |
| avif | nclx（编码器读**输入帧属性**）⇒ 引擎把 P/T 标签前置到 `-i` **之前**，矩阵留输出侧 | 引擎变换 | ✅ | `RawColorPipeline.BuildEncodeArgs:924-940` |
| jxl（作为**输出**） | cjxl `-x color_space=` / `-x icc_pathname=` / `--intensity_target`；ffmpeg-libjxl 亦前置 CICP | 引擎变换；**直编路线**不改像素，靠 `DirectEncodeTargetDiffers` 语义差闸改道 | ✅（有 #44 闸门） | `RawColorPipeline.cs:438-469`；`QueueProcessor.cs:2215-2262`；`Decision.cs:667-676` |
| gif | **无标注**（调色板无通路） | 规划层钉 sRGB 工作空间并映射像素 | ✅（按设计） | `ColorTransformPlan.cs:610`；`RawColorPipeline.cs:1000-1014` |
| jxr | `JxrEncApp` 不写 ICC ⇒ 编码成功后 exiftool 后置目标 ICC（`jxrTargetIcc`） | ffmpeg 中转 BMP/TIFF + JxrEncApp | ✅ | `QueueProcessor.cs:2615-2709`；`RawColorPipeline.cs:648-651` |
| **JXL 作为输入 → png/tiff/webp/avif/gif/jxr…** | **被注入写死的「输入色」**（`bt709/iec61966-2-1` 等） | **未转换**（用户目标被丢弃，链为空） | ❌ **否** | `QueueProcessor.cs:4360-4378`（P1-1） |

---

## ③ 已核实无问题

1. **Q4 第一半**：全仓 `grep UseAdvancedColorParameters` **无任何读点**——语义判据已全部改值派生（`HasExplicitColorParams` / `HasExplicitColorTarget`）。开关与值不一致的路径只剩 CLI（正是要修的那条），行为符合设计裁定。
2. **Q3 顺序**：`RestoreMetadataAsync`（`QueueProcessor.cs:990`）**先于** `ApplyColorPostStepsAsync`（`:994`）；后置内部次序为 sBIT→cICP→引擎 ICC→裁决点 ICC→TIFF ICC→保真 ICC，TIFF 目标 ICC 在元数据恢复**之后**、覆盖回带的源 ICC，符合「元数据先写、色彩最后定」的不变量。TIFF/保真路径的正确性有代码依据（`:1559`、`:1594`）。
3. **Q5 缓存**：`ProbeCache`（键=路径，10 s）只缓存**文件事实**，无正确性问题；`EffectiveOutputColorTokens` **不缓存**（每次重算 `DecideOutputColor`），无陈旧令牌问题；`EffectiveColorStrategy` 缓存当前无后果（见发现表 P2 埋雷条）。
4. **Q2 各格式**：png/tiff/webp/jpg/avif/jxl/gif/jxr 的**声明与像素均来自同一决策源**（引擎 `plan.Dst` 或 legacy `PixelChains`+`OutPrimaries`），逐格式未见「标 A 实 B」；JXR 的 ICC 后置（`:2615-2709`）明确处理了「请求目标==源色域时链为 0 也要嵌 ICC」的坑（#49）。
5. **PNG 块剥离**：`TryStripChunksSupersededByCicp` 只在**存在 cICP** 时剥离 `gAMA/cHRM/sRGB`，且不碰 `iCCP`（合法并存）；无 cICP 时不动（`PngCicpService.cs:158-168`）——正确。

---

## ④ 未覆盖 / 待验证

1. **P1-1 无门禁**：`tests/scripts` 内无以 `.jxl` 为**输入**、带 `--color-space`/`--color-primaries` 目标并断言产物标签的用例。建议新增：`src_lossy.jxl → png --color-space "Display P3"` 断言 `ffprobe color_primaries=smpte432`（现为 `bt709`，必红）。
2. **P2「`--color-matrix` 单独出现 ⇒ 跳过源 ICC 回带」未实测**（`QueueProcessor.cs:1741/1825`）：需一个带源 ICC 的广色域 PNG + `--color-strategy carry --color-matrix bt709 -f png`，检查产物是否残留 `iCCP`。
3. **`it.Manual` 恒 null**（P3 条）属决策层，建议线 A 复核 `ColorIntentFactory` 是否漏装配 `Manual`。
4. **PNG cICP 先于引擎 ICC 的顺序窗口**（P3 条）：当前两条线同源故未触发；建议把 cICP 步移到引擎 ICC 步**之后**，或让 cICP 也读 `engPlan` 并与之互校（不一致时阻断而非仅告警）。

---

### 附：P1-1 真机取证原文（节选）

```
# 场景 A：--color-space "Display P3"
[pipe] 注入 JXL 真实色彩: p=bt709 t=iec61966-2-1 m=gbr
[cmd] djxl "src_lossy.jxl" --output_format=ppm - | ffmpeg -y -color_primaries bt709 -color_trc iec61966-2-1
      -colorspace rgb -color_range pc -f ppm_pipe -i "-" … -vf iccgen -frames:v 1 "…\src_lossy.png"
[png3] 已写入 cICP（primaries=1, transfer=13 ← bt709/iec61966-2-1；策略=Recommended…）
产物 ffprobe: color_primaries=bt709  color_transfer=iec61966-2-1   ← 期望 smpte432(12)

# 场景 B：--color-primaries smpte432 --color-trc iec61966-2-1
[pipe] 注入 JXL 真实色彩: p=bt709 t=iec61966-2-1 m=gbr      ← 用户显式 smpte432 被覆盖
[png3] 已写入 cICP（primaries=1, transfer=13 …）
产物 ffprobe: color_primaries=bt709                            ← 期望 smpte432(12)
```
