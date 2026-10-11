# GainMap 端到端接线审查 — lane B3（UI/CLI → Options → 裁决 → 命令行/内核）

审查件：HEAD `c18dc4a`（v1.6.0-beta7，2026-10-10 08:20 +0800），工作树干净，只读审查。
实跑：`ServiceProbe wire` = **pass=93 fail=0**（`tests/ServiceProbe/bin/Release/net11.0/win-x64`，构建件 2026-10-09 23:54，
**早于** HEAD 前一个 commit `9971739`；但 `9971739` 未触碰 `GainMapEncoder.cs`/`GainMapJpegCodec.cs`/`RawColorPipeline.cs`/`ColorEngineRouter.cs`
⇒ 本 lane 引用的行号一律取自 HEAD 源码，探针读数只作"路由格子行为"佐证）。

---

## 0 一页结论

**接线总体是通的**：GainMap 6 个面板选项（开关/质量/目标 nit/类型/底图色域/分辨率）在 HEAD **全部有真实消费点**，
无"设了没人读"的死选项；两条出口（引擎 `RawColorPipeline.TransformGainMapAsync` 与专用 `QueueProcessor.ProcessGainMapJpegAsync`）
共用同一份解码串与参数公式（`GainMapJpegCodec`），单一真值成立。旧主张 1(b)(c)、4（缩放组合）、5 在 HEAD **已收口或主张过期**。

但查出 **6 条新缺陷 + 4 处文档过期**，按严重度：

| # | 一句话 | 标签 |
|---|---|---|
| N1 | **avif + GainMap**：HDR 模式对 avif 可见，但 GainMap 子面板只在 jpg/jpeg 显示 ⇒ 五个子选项藏在隐形控件里被采集；且 avif 出口硬编码 `-crf 18 -b:v 0`，用户在 AVIF 面板设的质量/cpu-used/tune/位深**全部无声丢弃** | 真缺陷-静默失效 |
| N2 | **Ultra HDR 输入 × GainMap 输出**：`DecodedUltraHdrPeakNits` 已被写入 options 并被色彩装配层读取，但 `GainMapJpegCodec.ResolveEncodeParams` **不读它**，只重新探测（此时输入已被换成线性 PFM，无 PQ 标签）⇒ `IsHdrInput=false` ⇒ `headroom=1` ⇒ 走"SDR ⇒ 普通 JPEG"短路，增益图不写 | 真缺陷-精度（端到端未测） |
| N3 | 复选框「增益图质量跟随主图」实际是**恒 75**：两条出口都写 `>=0 ? 值 : 75`，底图用 `Options.Quality` ⇒ 主图 90 时增益图仍是 75，与字典文案（zh/en 双语）正面矛盾 | 真缺陷-静默失效（宣称≠交付） |
| N4 | GainMap 峰值来源标签说谎：`JpegGainMapTargetNits` 模型默认 1000 ⇒ 永远 `>0` ⇒ `PeakSourceText` 恒打"用户指定"，GUI 用户从没指定过；同一行还会打出 `源峰值 203 nits（来源：用户指定）` 的自相矛盾读数 | 真缺陷-静默失效 |
| N5 | **auto 峰值档在 GUI 与预设两条路都不可达**（夹 [200,10000] + `>0` 守卫），而轴登记表把 `0` 列为合法取值 ⇒ GUI 用户对 4000nit 素材恒按 1000nit 建 headroom | 能力缺口 + 真缺陷-精度（窄） |
| N6 | 预设回放三处**齐**、但两腿语义分叉：模型腿 `if (p.JpegGainMap)` 关不掉开关（UI 腿能关）；`Downsample`/`MultiChannel` 无条件覆写，违反本仓 2026-09-26 已立的"旧预设不得覆盖"规则 | 真缺陷-静默失效（窄） |
| N7 | 门禁格子缺口：`verify-dryrun-parity` 四臂无 gainmap；`ProbeDecision` 网格从不置 `JpegGainMap`；`uicore` N 组预设两腿往返锁只测 `ColorStrategy` ⇒ N1/N6 这类"预览说 A 实际做 B"落在既有机制的空格 | 覆盖缺口 |
| N8 | 文档过期 4 处：FfmpegOptions/QueueProcessor×2/RawColorPipeline 仍写"GainMap+最长边缩放仍被显式拦"；`HdrMode` 自述"消费方应读本属性"但**零消费者**；`ParseGainMapHdrCf` + `JpegGainMapHdrCfCombo` 为死代码（XAML 已无该控件） | 文档过期 |

不建议动的：`--color-sdr-white` 在 GainMap 路不改 headroom（用 `KSdrWhiteNits=203` 常量，对齐 libultrahdr），
且日志逐字打出 `SDR白点 203nits` ⇒ 属**已点名的能力边界**，不算静默。

---

## 1 方法（含 grep 定案的原始命中）

### 1.1 字段级"有无消费者"定案 grep

命令：`grep -rn "\b<字段名>\b" --include=*.cs src` （剔除 `obj/`、`bin/`）。逐字段的命中清单（写点 W / 读点 R）：

| 字段 | W（写点） | R（读点 = 消费者） |
|---|---|---|
| `JpegGainMap` | `CliParser.cs:897`（`--jpeg-gain-map`）、`CliParser.cs:905`（经 `HdrMode`）、`MainWindow.xaml.cs:2981`、`:5953` | `ColorEngineRouter.cs:101`、`:401`、`ColorIntentFactory.cs:293`、`:363`、`QueueProcessor.cs:435`、`:942`、`:999`、`:5272`、`MainWindow.xaml.cs:3404` |
| `JpegGainMapQuality` | `CliParser.cs:910`、`MainWindow.xaml.cs:2982`、`:5954` | `RawColorPipeline.cs:349`、`QueueProcessor.cs:2703` |
| `JpegGainMapTargetNits` | `CliParser.cs:911`、`MainWindow.xaml.cs:2983`、`:5955` | `GainMapJpegCodec.cs:119/123/131`、`ColorEncodingHelper.cs:118/133` |
| `JpegGainMapDownsample` | `CliParser.cs:912`、`MainWindow.xaml.cs:2985`、`:5958` | `RawColorPipeline.cs:370`、`QueueProcessor.cs:2729` |
| `JpegGainMapMultiChannel` | `CliParser.cs:913`、`MainWindow.xaml.cs:2986`、`:5956` | `RawColorPipeline.cs:368`、`QueueProcessor.cs:2727` |
| `JpegGainMapBaseGamut` | `CliParser.cs:916`、`MainWindow.xaml.cs:2987`、`:5957` | `RawColorPipeline.cs:371`、`QueueProcessor.cs:2730` |
| `HdrMode` | `CliParser.cs:905` | **无**（仅 `FfmpegOptions.cs:401-404` 自身定义） |
| `ParseGainMapHdrCf` / `JpegGainMapHdrCfCombo` | `MainWindow.xaml.cs:487`（FindControl）、`:724-725`（挂 SelectionChanged） | **无调用者**（`:5845` 定义处即唯一出处） |

反向哨兵（证明这套 grep 有牙，能命中已知有消费者的选项）：同一条 `grep -rn "\bJpegGainMapBaseGamut\b"` 命中 5 个读点
（`RawColorPipeline.cs:371`、`QueueProcessor.cs:2730` 及两处日志串），故上表 `HdrMode`/`HdrCf` 的"零消费者"不是搜索姿势造成的假阴性。

XAML 侧定案：`grep -n "JpegGainMap|HdrMode" src/FfmpegGui/MainWindow.xaml` 共 13 处命中
（`524/527` HdrModeCombo、`694/696` 面板、`703` 类型、`706` 底图、`710/714/717/720/723` 质量、`730/733` 亮度、`740` 分辨率），
**没有** `JpegGainMapHdrCfCombo` ⇒ 那只控件确实已从 UI 消失，仅代码残留。

### 1.2 两条出口的单一真值验证
解码串/平面序/渐进映射均只有一处实现，两条出口都调它：
`GainMapJpegCodec.cs:33-35`「⚠ 本串是**唯一真值**：传统 GainMap 管线与引擎 GainMap 出口都调它，禁止第二份。」
调用点：`RawColorPipeline.cs:315/320/333/360`、`QueueProcessor.cs:2646/2660/2670/2700/2720`（同一批函数，无本地重写）。

### 1.3 实跑
```
ServiceProbe wire  →  PROBE RESULT: pass=93 fail=0
  PASS jpg + JpegGainMap ⇒ 路由层不再拦截（GainMap 已接入引擎）
  PASS jpg + JpegGainMap ⇒ 规划层不得 Reject（实为 Map）
  PASS 负控：png + JpegGainMap ⇒ 规划层 Reject 并建议改 AVIF/JXL
  PASS GainMap × 最长边缩放 ⇒ 路由层**不再**拦截（实得 Code=null）
  PASS 负控：同一输入换成动画 ⇒ 仍须拦（Code=AnimatedInput）
  PASS GeometryWithGainMap 已不再生产 ⇒ 该组合无替代项
```
（TMP/TEMP 已指到 `tests/output/tmp-b3`；`wire` 为纯进程内判定，不触发 ffmpeg，故无 exit -13 假红风险。）

### 1.4 未跑
本 lane 未跑端到端 CLI（沙箱 `%TEMP%` 限制 + 整轮门禁禁令）⇒ 所有"产物形态"级结论标 `未测`，
并引用已有存档读数时标明其构建时间。

---

## 2 旧主张复验表

| 主张 | 判定 | HEAD 证据 |
|---|---|---|
| ①a「nits 默认 203」 | **已收口**，且是**正确的行业标准值** | `GainMapEncoder.cs:31` `public const float KSdrWhiteNits = 203f;   // 行业标准 SDR 白点 (libultrahdr kSdrWhiteNits)`；两条出口都显式传 `sdrWhiteNits: GainMapEncoder.KSdrWhiteNits`（`RawColorPipeline.cs:367`、`QueueProcessor.cs:2726`），日志逐字回显 `SDR白点 {…}nits`（`GainMapEncoder.cs:112`） |
| ①b「peakNits 未拷回」 | **已修**，唯一拷回点存在 | `FfmpegCommandBuilder.cs:1541-1549`「此前本行缺失：ProbeWithFfprobe 里读了 side data 却没人拷贝，/ 导致两条消费路径（引擎装配 / GainMap 日志）拿到的 peakNits 永远是 0（实测）」+ `if (meta.peakNits <= 0 && ffMeta.peakNits > 0) { meta.peakNits = ffMeta.peakNits; meta.peakSource = ffMeta.peakSource; }`。GainMap 侧经 `GainMapJpegCodec.cs:108-109` 消费同一份 |
| ①c「ffprobe 键名与有理数单位」 | **已修**，键名与单位都收口 | `FfmpegCommandBuilder.cs:1740-1744`「max_luminance——后者是调色监视器的峰值…旧实现取两者 Math.Max ⇒ 过度压暗」「`max_luminance` 打印为**有理数字符串** "40000000/10000"，其值已是 cd/m²（=4000）」；实现 `:1798 ExtractJsonNumber(json,"max_luminance","MasteringDisplayMaxLuminance")`、`:1822 ParseRationalOrDecimal(...)`、`:1837 token.Split('/')` |
| ①d（lane B1 F9）「峰值 auto 档 GUI 不可达、夹在 [200,10000]」 | **成立**，从 UI 侧独立复验；见 §3 可达性表 | `MainWindow.xaml.cs:5837-5842` `ParseGainMapNits()` 只有 `Math.Clamp(n,200,10000)` 与 `return 1000` 两条出边 ⇒ 永不返回 0；`MainWindow.xaml:730` `Slider Minimum="200" Maximum="10000"`；`MainWindow.xaml.cs:5895` 同步器再夹一次；预设两腿 `FfmpegOptions.cs:112` / `MainWindow.xaml.cs:6121` 都是 `>0` 守卫 |
| ①e（新分叉，非 B1 所述） | GUI 与 CLI 对"峰值"这根轴**方向相反** | `ColorHdrPeakNits`（引擎色调映射用）**全仓无 GUI 写点**（`grep ColorHdrPeakNits` = `CliParser.cs:839` + `ColorIntentFactory.cs:413/415` 两处），⇒ GUI 恒 0=auto；`JpegGainMapTargetNits`（GainMap 用）**GUI 恒 ≥200** ⇒ 恒非 auto。两条轴一个是"永远自动"、一个是"永远手动"，同一面板里并存 |
| ②「暴露规范：默认结果语言、高级面板精确参数；GainMap 属开关」 | **符合**，且 HEAD 未越权加格 | 字典取值全是结果语言：`zh-CN.json:347` `"gainmap.ds.half": "1/2 分辨率（推荐）"`、`:344 base.srgb "sRGB SDR（兼容性最好）"`、`:360 type.rgb "RGB 多通道（色彩更准）"`；开关本体是二选一 `:361-362 hdr.mode.*`。计划文档未兑现项：`docs/JPEGLI_GAINMAP_PANEL_PLAN_2026-10-09.md:348` §17b「**J1 半兑现**…**增益图路不成立** —— 面板值在采集侧被钉成 `JpegProgressiveId=0`」「**J2 未兑现**（增益图路）：三种面板设置下底图 md5 逐字节相同」⇒ 属**已登记未实现**，本 lane 只复验 HEAD 是否偷偷变了（见 N5，未变），不当新缺陷报 |
| ③「简洁模式=纯覆盖层；加选项补 PresetData+Build+Apply 三处」 | **三处结构齐**，但回放有拿不到的格 | PresetData：`PresetData.cs:45-55` 六字段齐。Build：`MainWindow.xaml.cs:5912 BuildPresetData()` → `:5953-5958` 六字段全写。Apply：模型腿 `FfmpegOptions.cs:110-115`、UI 腿 `MainWindow.xaml.cs:6105-6130` + `:6261`。缺口见 N6 |
| ④「GainMap × 最长边缩放仍在引擎出口外」 | **主张过期**（HEAD 已由引擎承接） | `ColorEngineRouter.cs:83-85`「「GainMap + 最长边几何缩放」（已消解）…⇒ 该组合现在 `ShouldRoute=true`、由引擎 GainMap 出口产出真 Ultra HDR」+ `:353` 删除记录；`wire` 实测两条 PASS。**但** `RawColorPipeline.cs:281-287` 仍留一条 throw：该支路在真实调度下不可达，因为 `QueueProcessor.cs:421-422` 注释「再往下的预缩放（:649）与 Ultra HDR 解码都会改写 `InputPath`」⇒ 中继已把输入换成缩过的临时件 ⇒ `BuildMaxDimensionScaleFilter` 恒返回 null。存档读数（**10-10 00:21 件，早于 `9971739`**）：`tests/output/_gate_script_verify-gainmap-engine.ps1.txt:89-99` 全 PASS，含「底图 == 128x96（ffprobe 读回 128,96）」与「产物仍是真 Ultra HDR 容器（顶层 2 幅图）」 |
| ⑤「编码器不可用应显式报错」「外部工具探测四处口径不一」 | **已收口** | GainMap 只依赖 cjpegli/ffmpeg，两处都是"两者皆缺才失败"且点名：`RawColorPipeline.cs:291-292`、`QueueProcessor.cs:2614-2621`「两者皆不可用才报错（不静默产出无增益图的普通 JPEG）」；cjpegli 缺失但 ffmpeg 可用时渐进档**仍点名**：`GainMapEncoder.cs:521-523` `WARN: JPEG progressive level N requested, but cjpegli is unavailable … output is baseline (option not applied)`。探测口径单点：`CjpegliService.IsAvailable/DetectedPath`（`CjpegliService.cs:23/28`），调用方 `MainWindow:1243`、`EncoderDetectionService:358/419`、`RawColorPipeline:291`、`QueueProcessor:2614` **都读同一个服务**，无第二份路径拼接；avifenc 不参与 GainMap（AVIF 出口走 ffmpeg libaom-av1，`GainMapEncoder.cs:656`），与 `QueueProcessor.cs:5301-5304`「不得另加 HasAvifencAvailable() 这类薄包装」不冲突 |

---

## 3 选项 × 格子可达性表

### 3.1 GainMap 面板六控件（GUI）
| 控件（XAML） | 采集函数 | 值域（GUI） | 值域（模型/CLI） | 消费点 | GUI 可达但被丢弃的取值 |
|---|---|---|---|---|---|
| `HdrModeCombo`（`xaml:527`，可见性 `fmt is jpg/jpeg/avif` @`cs:2413`） | `cs:2981/5953` `SelectedIndex==1` | 传统 \| 增益图 | `--hdr-mode gainmap\|traditional` | Router/IntentFactory/QueueProcessor | **子面板仅 jpg/jpeg 可见**（`cs:2507-2509`、`3849-3851`）⇒ avif 选中"增益图 HDR"后五个子选项藏在隐形控件里 |
| 类型 `JpegGainMapTypeCombo`（`xaml:703`） | `cs:5813-5819` | 灰度 \| RGB | bool | `RawColorPipeline:368`、`QP:2727` | — |
| 底图 `JpegGainMapBaseCombo`（`xaml:706`） | `cs:5822-5823` | srgb \| bt2020 | 同 + `bt2100` 别名（`GainMapEncoder:110-111`） | `RawColorPipeline:371`、`QP:2730` | GUI 采不到 `bt2100`（无害，与 bt2020 同义） |
| 质量 `FollowMainCheck`+`QualityBox`（`xaml:710/720`） | `cs:5825-5832`（勾选⇒-1） | -1 \| 1..100 | 同 | `RawColorPipeline:349`、`QP:2703` | **-1 ⇒ 恒 75，不跟主图**（N3） |
| 目标亮度 `NitsSlider`+`NitsBox`（`xaml:730/733`） | `cs:5837-5842` **夹 [200,10000]** | 200..10000 | `0=auto`、CLI 任意 | `GainMapJpegCodec:119-124` | **0(auto) 不可达**（N5）；<200 被抬到 200 |
| 分辨率 `DownsampleCombo`（`xaml:740`） | `cs:5852-5864`，**未勾高级编码选项 ⇒ 恒 2** | 1/2/4/8/16 | 同 | `RawColorPipeline:370`、`QP:2729` | 高级面板未展开时档位被钉 2（与 `ParseGainMapHdrCf` 同族的"采集侧收窄"，但此条有 XAML 提示语 `gainmap.res.hint`） |
| （死）`JpegGainMapHdrCfCombo` | `cs:5845-5849` **零调用者** | — | — | — | 控件已从 XAML 删除，代码残留 4 处（N9） |

### 3.2 GainMap × 组合格子
| 组合 | 执行体 | 结果 | 判定 |
|---|---|---|---|
| jpg × GainMap（默认 auto，后端 Ffmpeg） | 引擎出口 | 真 Ultra HDR | 正常（`wire` PASS + `verify-gainmap-engine` §1） |
| jpg × GainMap × 后端 Cjpegli（**GUI 有 cjpegli 时的默认**，`EncoderDetectionService.cs:419`） | `BlockReason=ExternalBackend` ⇒ 专用管线 | 真 Ultra HDR，但面板四旋钮丢弃 | **叠加空档**（N5）：`GainMapEncoder.cs:500` 硬编码 `--chroma_subsampling 420`，`grep Cjpegli 该文件 = 3 次（仅注释/路径），`CjpegliChromaSubsampling/AdaptiveQuant/Optimize/PsnrTarget` 出现 **0 次** |
| jpg × GainMap × 最长边缩放 | 引擎出口（中继预缩放后 scaleFilter=null） | 128x96 + images=2 | 正常；旧主张过期（§2 ④） |
| jpg × GainMap × 动画输入 | `AnimatedInput` ⇒ 专用管线，`-frames:v 1` 取首帧 | 单帧 Ultra HDR | `GainMapJpegCodec.cs:40-43` 已点名多帧陷阱 ⇒ 出声，非静默 |
| jpg × GainMap × **Ultra HDR 输入** | 输入被换成线性 PFM 后仍进 GainMap 出口 | 疑似退化成普通 JPEG（headroom=1） | **N2 真缺陷-精度**，端到端未测 |
| avif × GainMap | 引擎出口（`SupportsGainMap=true` @`ColorTransformPlan.cs:588`） | 容器对（tmap），编码参数丢 | **N1 真缺陷-静默失效** |
| png/bmp/heic × GainMap | 规划层 Reject 或 `exit 91` | 不出产物 | 能力缺口（已 fail-closed）：`ColorStrategyPlanning.cs:282-292` + `QueueProcessor.cs:999-1009` |
| SDR 源 × GainMap | 出口内短路 | 普通 JPEG + 点名 | 正常（`GainMapEncoder.cs:217-222`），**但 `item.Status` 仍写"已完成 (Gain Map 纯托管编码)"**（`QueueProcessor.cs:2737`）⇒ 文案与产物不符（N8） |
| `--color-tone-map` × GainMap | 出口自持分段 Reinhard | 曲线不生效但点名 | 正常（`ColorStrategyPlanning.cs:269-271` 登记 `ToneMapNotAppliedToGainMap` + `RawColorPipeline.cs:303-304` 出声）；自动兜底（`ToneMapAuto`）不入降级表，靠 `:303` 那行说清 ⇒ 可接受 |
| `--color-hdr-peak` × GainMap | GainMap 头room 走 `GainMapJpegCodec` 独立链 | 两链不共享峰值 | 一致性可接受（同一 `ProbeInputColorMetadata` 源），但 `--color-sdr-white` 对 GainMap 无效 ⇒ 已点名（日志 `SDR白点 203nits`）⇒ 未判定（非缺陷） |

### 3.3 预设回放可达性
| 选项 | 模型腿 `FfmpegOptions.ApplyPresetData` | UI 腿 `MainWindow.ApplyPresetData` | 判定 |
|---|---|---|---|
| `JpegGainMap=false` | `:110 if (p.JpegGainMap)` ⇒ **关不掉** | `:6261 …?1:0` ⇒ 能关 | N6 两腿分叉 |
| `JpegGainMapQuality=-1`（跟随） | `:111 >=0` ⇒ 无法复位到 -1 | `:6115 IsChecked = p.<0` ⇒ 能 | 半分叉 |
| `JpegGainMapTargetNits=0`（auto） | `:112 >0` ⇒ **拿不到** | `:6121 >0` ⇒ **拿不到** | N5 一致地不可达 |
| `JpegGainMapDownsample` | `:113` 无条件 ⇒ 旧预设把用户档位刷成 2 | `:6123-6130` 无条件 ⇒ 同 | N6 |
| `JpegGainMapMultiChannel` | `:114` 无条件 ⇒ RGB 增益图被刷成灰度 | `:6109` 无条件 ⇒ 同 | N6 |
| `JpegGainMapBaseGamut` | `:115 IsNullOrEmpty` 守卫 ⇒ 正确 | `:6110-6112`（null ⇒ 落 srgb） | 基本正确 |
| 简洁模式漂移提示 | `SimpleCoreSignature` 只有 Format/Mode/Backend/Quality/Lossless（`:4940-4945`）⇒ 改 GainMap 不显示"(自定义)" | 同 | N7 窄 |

---

## 4 新发现

### N1 `avif + GainMap`：面板分叉 + AVIF 编码参数无声丢弃 —— 真缺陷-静默失效
```
MainWindow.xaml.cs:2413   if (HdrModeCombo != null) HdrModeCombo.IsVisible = fmt is "jpg" or "jpeg" or "avif";
MainWindow.xaml.cs:2507   if (JpegGainMapPanel != null)
MainWindow.xaml.cs:2508       JpegGainMapPanel.IsVisible = HdrModeCombo?.SelectedIndex == 1
MainWindow.xaml.cs:2509                               && (FormatCombo?.SelectedItem as string)?.ToLowerInvariant() is "jpg" or "jpeg";
```
开关对 avif 开放，子面板对 avif 关闭 ⇒ 用户切到 avif + 增益图后看不见（但仍在采集）质量/nit/类型/底图/分辨率五只控件；
字典还写着「增益图适用于 JPEG 与 AVIF」（`zh-CN.json:563 tip.gainmap.enable`）。AVIF 面板照常显示（`:2447-2448`），
而 GainMap 的 AVIF 出口根本不走那条编码链：
```
GainMapEncoder.cs:656   var args = $"-y -i \"{pngPath}\" -frames:v 1 -c:v libaom-av1 -crf 18 -b:v 0 -pix_fmt {pixFmt}"
RawColorPipeline.cs:55  if (plan.GainMap)
RawColorPipeline.cs:56      return await TransformGainMapAsync(...)     // 在常规编码出口之前分流
```
⇒ 用户设的 AVIF 质量/crf/cpu-used/tune/row-mt/位深在本次全部不生效，全仓唯一出处 `grep "crf 18" = GainMapEncoder.cs:656`，
无对应降级项（`Degradations` 只登记 `ToneMapNotAppliedToGainMap`）。
本仓已有同类正解：`MainWindow.xaml.cs:2517-2518`「Unsupported containers hide the whole panel **AND reset the checkbox**,
so a value that can never be honored does not stay behind an invisible control.」⇒ GainMap 这条违反该原则。
修法方向（不改行为只给建议）：要么把子面板可见性条件与 `HdrModeCombo` 同域（含 avif），
要么 avif 选中增益图时把五只子控件 disable+复位并留一行说明；AVIF 出口的 crf 应与 `ImageEncoderArgs.BuildAvifOptions` 同源。

### N2 `Ultra HDR 输入 × GainMap 输出`：峰值字段写了但 GainMap 侧不读 —— 真缺陷-精度（端到端未测）
```
QueueProcessor.cs:533          captured.Options.DecodedUltraHdrPeakNits = decoded.Value.peakNits;
ColorIntentFactory.cs:422      else if (o.DecodedUltraHdrPeakNits > 0)
GainMapJpegCodec.cs:108        var meta = FfmpegCommandBuilder.ProbeInputColorMetadata(inputPath);
GainMapJpegCodec.cs:111        p.IsHdrInput = meta.colorTrc is "smpte2084" or "arib-std-b67";
GainMapJpegCodec.cs:122        p.PeakNits = p.IsHdrInput ? (float)p.Nits : GainMapEncoder.KSdrWhiteNits;
```
定案 grep：`DecodedUltraHdrPeakNits` 全仓读点 = `CjxlService.cs:574/575` + `ColorIntentFactory.cs:422/424`，**不含 `GainMapJpegCodec`**。
而 `QueueProcessor.cs:527` 已把 `captured.InputPath` 换成线性 PFM（`:535` 注释「1.0 = SDR 白点 203nits」），
PFM 无 PQ 标签 ⇒ `IsHdrInput=false` ⇒ `PeakNits=203` ⇒ `headroom=203/203=1` ⇒ 落 `GainMapEncoder.cs:217-222` 的 SDR 短路，
日志会说「输入为 SDR（headroom ≤ 1，增益区间为 0）」——而输入其实是带增益图的 Ultra HDR。
同族前例已在 HEAD 修过一半（`:418-421` 那条 2026-10-04 审查就是同一根因的色调映射支路），GainMap 支路是漏掉的另一半。
未测：需 HDR 夹具 + TMP 重定向臂跑 `--jpeg-gain-map true` 读回 `images=` 才能定产物形态。

### N3 「增益图质量跟随主图」实为恒 75 —— 真缺陷-静默失效
```
RawColorPipeline.cs:349   var gmq = options.JpegGainMapQuality >= 0 ? options.JpegGainMapQuality : 75;
RawColorPipeline.cs:351   float baseDist = Math.Clamp((100f - options.Quality) * 0.25f, 0.5f, 6.0f);
QueueProcessor.cs:2703    var gmq = item.Options.JpegGainMapQuality >= 0
QueueProcessor.cs:2704        ? item.Options.JpegGainMapQuality : 75;
```
字典两语都承诺一致：`zh-CN.json:565 "增益图质量与主图质量保持一致"` / `en-US.json:565 "Keep gain map quality the same as the main image"`。
⇒ 主图 90 时底图 d=2.5、增益图 d=6.25→夹到 6.0（≈质量 76→75 档），是**两档质量**而非"跟随"。
CLI 侧注释也自认这是"默认 75"（`CliParser.cs:1009` `JpegGainMapQuality = -1`（用默认 75…）），
⇒ 属**标签与实现谁都没错、但二者互斥**的经典格，符合判据②要消灭的形态。一行改：`-1 ⇒ options.Quality`（两条出口同改）。

### N4 峰值来源标签说谎 —— 真缺陷-静默失效（决策自证）
```
FfmpegOptions.cs:376    public int JpegGainMapTargetNits { get; set; } = 1000;
GainMapJpegCodec.cs:123 p.PeakSourceText = o.JpegGainMapTargetNits > 0 ? "用户指定"
GainMapJpegCodec.cs:124     : p.MetaPeak > 0 ? $"HDR 静态元数据 {metaPeakFrom}" : "名义 1000，无元数据";
RawColorPipeline.cs:298 log?.Invoke($"[GainMap]   源峰值 {ep.PeakNits:F0} nits（来源：{ep.PeakSourceText}）\n");
```
模型默认就是 1000 ⇒ `>0` 恒真 ⇒ GUI/未带该 CLI 参数的运行都打"用户指定"；
叠加 N2 时还会打出"源峰值 **203** nits（来源：用户指定）"这种自相矛盾行（203 是白点常量，用户从没指定过它）。
`FfmpegOptions.cs:619` 对本家兄弟轴写的是"来源会写进日志…可审计"，GainMap 这根轴没做到同等诚实。

### N5 GainMap × cjpegli 面板四旋钮：HEAD 复核仍未接 —— 真缺陷-静默失效（已在 §17b J2 登记，未修）
```
GainMapEncoder.cs:496   var progArg = progressiveLevel >= 0 ? $" -p {progressiveLevel}" : "";
GainMapEncoder.cs:500   Arguments = $"\"{pngPath}\" \"{jpgPath}\" --distance {distance:F1} --chroma_subsampling 420{progArg}",
```
定案 grep（`GainMapEncoder.cs` + `GainMapJpegCodec.cs` 内）：`CjpegliChromaSubsampling`/`CjpegliAdaptiveQuant`/`CjpegliOptimize`/`fixed_code`/`num_threads` **0 命中**；
渐进档只有 `JpegProgressiveId`（隐藏下拉）经 `ProgressiveLevelOf` 接入。
计划文档已把它记在"未兑现"账上（`docs/…2026-10-09.md:33` §1.1 R3 + `:348` §17b J1/J2），R0-a/§15/§16 三项明写不随包 ⇒ 不按新缺陷计，按"已登记未修 + 本 lane 复核确认 HEAD 未变"报。

### N6 预设回放：两腿分叉 + 无条件覆写违反本仓 null 规则 —— 真缺陷-静默失效（窄）
```
FfmpegOptions.cs:110   if (p.JpegGainMap) JpegGainMap = p.JpegGainMap;
FfmpegOptions.cs:113   JpegGainMapDownsample = p.JpegGainMapDownsample;
FfmpegOptions.cs:114   JpegGainMapMultiChannel = p.JpegGainMapMultiChannel;
PresetData.cs:167      // with the advanced-mode UI state, so a preset has to describe that state completely.
PresetData.cs:168      // These nullable axes are the ones the schema never had. null = "an old preset predates
```
模型腿被 `CliParser.cs:693 options.ApplyPresetData(preset.Data)` 使用（headless `--preset`），UI 腿被简洁模式使用
⇒ 同一个 `JpegGainMap=false` 的预设：CLI `--jpeg-gain-map true --preset X` 关不掉（守卫式赋值），GUI 能关。
`Downsample`/`MultiChannel` 是非可空值类型 ⇒ 旧预设（无该字段）反序列化成 2/false 后**无条件覆盖用户当前档位**，
正是 `PresetData.cs:143-145` 为 `AnimationLoop`、`:171-184` 为 `ConversionMode/CjxlEffort/RawColorTarget` 已立规则要消灭的形态。
未测：需一条真旧预设 JSON 跑往返（`uicore` N 组只测 `ColorStrategy`，见 N7）。

### N7 既有"宣称 vs 交付"机制没覆盖的格子 —— 覆盖缺口
| 机制 | 现覆盖 | 缺的 GainMap 格 |
|---|---|---|
| `verify-dryrun-parity.ps1:107-114` | 4 臂：`jxr`/`png`/`jpg`/`dng` | **无 gainmap 臂** ⇒ N1"预览给 ffmpeg avif -crf<用户值>、实际 GainMapEncoder -crf 18"这类幻影可过（该脚本立锁动机正是同类，`:18-21`） |
| `ServiceProbe ProbeDecision`（`Program.cs:4202-4213`） | 3 源 × 4 格式 × 5 目标，`ColorStrategy=Recommended` | 从不置 `JpegGainMap` ⇒ 裁决行与 GainMap 出口的对账无格 |
| `ui-core` N 组两腿往返（`uicoreGroup.cs:460-520`） | 只测 `ColorStrategy` | GainMap 六字段的"预设→控件→采集"往返无锁 ⇒ N6 不可见 |
| `MainWindow.xaml.cs:3404` 预览 | `JpegGainMap && fmt is jpg/jpeg` | avif+GainMap 无预览分支 ⇒ 直接落通用 ffmpeg 预览（与 N1 同格） |
| `tests/ui-mode/uimodeGroup.cs:378-430` | O1/O2/O3：开关保持色域/位深/质量 | 不验开关后五只子控件的采值正确性 |

### N8 失败/取消语义与半成品 —— 混合（多数已收口）
| 段 | 失败时 | 产物/临时件 |
|---|---|---|
| 尺寸探测 | `item.ExitCode=-1`、`失败 (无法获取图像尺寸)`（`QP:2632-2637`） | 无产物 |
| zscale 线性化 | 先回退直解，再失败 ⇒ `失败 (线性解码退出码 N)`（`QP:2666-2683`） | tempDir finally 删（`:2753`） |
| 像素长度不足 | 下界检查后失败（`QP:2689-2696`、`RawColorPipeline:330-331`） | 同上 |
| 底图/增益图编码 | `return false` ⇒ `失败 (GainMapEncoder 编码失败)`（`GainMapEncoder:175-179/202-206`） | 未写 outputPath |
| Ultra HDR 封装（定位不到 SOS） | `return false` 且明写"也未产出假 Ultra HDR"（`:227-231`） | 未写 |
| SDR 短路 | **`return true`** + `已完成 (Gain Map 纯托管编码)`（`QP:2737`） | 产物是普通 JPEG，**状态行谎报** |
| 取消 | `catch (OperationCanceledException) { item.Status = "已停止"; }`（`QP:2741-2744`；`GainMapEncoder:247` 上抛） | temp 删；**outputPath 若已开写不清**（JPEG 封装步 `WriteJpegGainMapFile` 直写目标路径）⇒ 未测 |
| 中间件落盘（登记给 C3） | 专用管线：`tempDir/gainmap_<guid>/` 内 `hdr.rgba` + `base.png/gm.png` + `base.jpg/gm.jpg`；引擎出口：`cms_gm_<guid>.rgba` 单件 + GainMapEncoder 内自己的 tempDir ⇒ **GainMap 一趟有 2 层临时目录** | 两处 finally 各删各（`QP:2753`、`GainMapEncoder:244`、`RawColorPipeline:382`） |

### N9/N10 死代码与文档过期 —— 文档过期
- `MainWindow.xaml.cs:250/487/724-725/5845-5849`：`JpegGainMapHdrCfCombo` 在 XAML 已不存在 ⇒ `FindControl` 恒 null，四个守卫分支与 `ParseGainMapHdrCf()`（注释还写着"当前仅支持 0=p010"）永不可达。
- `FfmpegOptions.cs:398`「⇒ 消费方**读**该用本属性（语义自明）；**不要**再新增读 `JpegGainMap` 的判定点。」与 HEAD 相反：`HdrMode` 零读者，全部 9 个判定点读 `JpegGainMap`（§1.1 表）。
- `FfmpegOptions.cs:527`「只剩「GainMap + 最长边缩放」这条组合仍被显式拦（底图与增益图必须同几何）」、`:534`「本条清单只剩 **DNG/动画** 与「GainMap + 最长边缩放」」、`QueueProcessor.cs:940`「显式 --color-engine legacy，或引擎不可走（如 GainMap + 最长边缩放）」、`:1307`「（GainMap + 最长边缩放的组合仍显式拦，见 Router）」⇒ 四处均与 `ColorEngineRouter.cs:83/353` 的"已消解/已删除"及 `verify-gainmap-engine` §8 实测冲突。
  另外 `QueueProcessor.cs:940` 把"显式 `--color-engine legacy`"列为专用管线的触发条件，而 `ColorEngineRouter.cs:55-67/100-102` 明写 **GainMap 不受 legacy 开关管辖**（`Requested()` 里 `o.JpegGainMap ||` 前置）⇒ 该注释描述的分支在 HEAD 恒不成立。
- `tests/scripts/_lib-axes.ps1:309-316` 的 `-Source` 指向 `CliParser.cs:596-601`，HEAD 那几行是"输出目录自我吞噬"的注释，真实解析在 `:897-917` ⇒ 轴登记表行号漂移。

---

## 5 未证边界

1. **N2 的产物形态**：需 HDR Ultra HDR 夹具 + TMP 重定向臂跑 `-f jpg --jpeg-gain-map true`，用 `ServiceProbe gainmap` 读回 `images=` 才能定"退化成普通 JPEG"是否真发生（代码侧已定案：峰值字段无读者）。⇒ 当前标 `真缺陷-精度（接线）/ 未测（产物）`。
2. **N1 的 avif 参数丢弃幅度**：`-crf 18` 与用户质量档的体积/PSNR 差未量化（C3 性能侧同格）。
3. **N3/N6 的实测对账**：三种面板质量档下的增益图 md5 差异、旧预设 JSON 往返，均未跑。
4. **取消留半成品**：`WriteJpegGainMapFile`/`File.WriteAllBytesAsync(outputPath)` 中途取消后是否留残件，未做取消注入实验。
5. **读数件 ≠ HEAD**：`wire` 93/0 来自 2026-10-09 23:54 构建、`verify-gainmap-engine` §8 存档读数来自 10-10 00:21，两者都早于 `9971739`（08:19）；该 commit 未触碰 GainMap 四件源文件，但触碰了 `QueueProcessor.cs`(+5) 与 `MainWindow.xaml.cs`(+38) ⇒ 涉及这两文件的行号我按 HEAD 读，按存档读数的行为结论需复跑一次才算 HEAD 绑定。
6. **`--color-engine legacy` + GainMap 的实际归属**：由 `Requested()` 的 `o.JpegGainMap ||` 前置判断，legacy 也走引擎（`ColorEngineRouter.cs:55-67`），但 HEAD 未实跑该臂 ⇒ 未判定（需一条 `legacy + --jpeg-gain-map true` 的产物级读数）。
7. `HdrImplementationMode` 只出现在 `CliParser:906` 与属性自身（`ColorStrategy.cs:50`），没有任何 UI/日志用强类型模式说话 ⇒ GUI 的"传统 HDR"格实际是"关闭 GainMap"，非独立实现路径；是否算能力表述缺口属产品裁定，本 lane 只登记。
