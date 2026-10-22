# 色彩矩阵可用性审计报告（2026-10-02）

> **审计对象**：`src/FfmpegGui/Services/ColorMapping/`（18 文件 / 7,908 行）＋ `Services/ColorSpaceRegistry.cs` ＋ 出口接线（`FfmpegCommandBuilder.Decision.cs`、`RawColorPipeline.cs`、`ImageEncoderArgs.cs`）＋ GainMap 子系统。
> **审计目的**：判断该矩阵作为「**全方面容纳所有图片色彩处理**」的底座，其**可用性**（正确性 / 覆盖度 / 可扩展性 / 执行精度 / 可验证性 / 工程风险）是否成立。
> **审计方式**：**从零开始**。不采信既有文档结论；源码重读 ＋ 5 路并行独立审计 ＋ 探针实测 ＋ 主审对高影响结论逐条复核。
> **证据分级**：🟢**实测**（本机运行探针/命令取得读数）｜🔵**复核**（主审亲自 grep/读码确认）｜🟡**代理**（并行审计代理读码报告，主审未逐条复核）

---

## 摘要（TL;DR）

**总体评级：中（偏上）—— 数学内核强，工程边界弱。**

三句话结论：

1. **数学内核可信**。原色表、色适应、乘序、PQ/HLG/BT.2446 常数经与 zimg 及发布值对拍**全部通过**：`colormath 12/0`、`matrix 15/0`、`curve 44/0`、`ootf 11/0`、`bt2446 18/0`、`contract 136/0`、`verdict 41/0`、`iccname 10/0`。**本轮未发现 P1 级缺陷。**
2. **「全方面容纳所有色彩处理」当前不成立**，但缺口性质分三类：①**结构性**（xy 参数化在数学上表达不了 XYZ 三角 ⇒ ST 428-1 永久拒收）；②**政策性**（有意识地 fail-closed 不收录）；③**未实现**（BT.2390 EETF、BT.2446 B/C、AVIF/HEIC 的 GainMap 编码）。第 ① 类不是「还没填表」，靠补数据解决不了。
3. **最大的可用性风险不是算错，而是「看起来能用其实不生效」**。已复核出：1 个只写不读的关键字段、1 个整类死支、6 个容差常量中 4 个零消费、1 个**结构上永不可能失败**的门禁、4 个含真断言却未接线的探针 mode。这类缺陷制造**虚假信心**，比算错更难发现——因为它们让「全绿」失去信息量。

**与 2026-09-24 旧审计的关系**：旧报告的核心指控（原色表 5 token 共用 P22 常数、`smpte428` 冒充 `smpte431`）**已被修复并经本次独立实测确认**（`bt470m` 实差从 0.461 → 1.76e-4；`smpte428` 改为 fail-closed 返回 null）。⇒ **不要再按旧报告行事**，但旧报告的方法论（外部参照对拍、变异验证）仍然有效。

---

## 0. 审计方法与证据纪律

| 环节 | 做法 |
|---|---|
| 独立取数 | 直接运行 `ServiceProbe.exe`（Release 产物，2026-10-02 01:20 构建），不经任何文档转述 |
| 并行深审 | 5 路只读代理：数学层 / 架构层 / 执行层 / GainMap / 测试层 |
| 主审复核 | 对 5 路报告中**全部高影响结论**逐条 grep/读码复核；**推翻了 1 条**（见 §3 注） |
| 反假绿 | 对「零消费」「死码」类结论一律用全仓 grep 计数，而非单文件判断 |
| 只读承诺 | 全程未修改任何源文件、未执行 `dotnet build`（共享工作树纪律） |

**本报告不做什么**：不重复旧审计已收口的 P1 修复过程；不把「定案」当「落地」；不把代理的推断写成事实（凡未复核者标 🟡）。

---

## 1. 六维评级总表

| 维度 | 评级 | 一句话依据 | 证据 |
|---|---|---|---|
| **数学正确性** | **强** | 原色/色适应/乘序/曲线常数与 zimg 及 ITU 发布值逐元素一致 | 🟢🔵 |
| **覆盖度** | **中** | 主流 SDR/HDR 完备；但有 3 类缺口，其中 1 类结构性不可补 | 🔵🟡 |
| **执行精度** | **中** | 8-bit 抖动已居中且码域正确；但矩阵内核**无 SIMD**，YUV 出口抖动承诺不兑现 | 🔵🟡 |
| **架构可抽取性** | **中偏弱** | 与 `FfmpegCommandBuilder` **双向循环依赖**；`RawColorPipeline` 本质是 ffmpeg 编排器 | 🔵🟡 |
| **GainMap** | **中** | JPEG Ultra HDR 闭环强（第三方认证）；AVIF 解码实测；HEIC 仅合成件；AVIF/HEIC **不可编码** | 🟡 |
| **可验证性** | **中偏上** | 纯函数探针有牙且多数有变异记录；但存在无牙门禁与未接线 mode | 🔵🟡 |

---

## 2. 能力覆盖矩阵——「全方面」能否成立

### 2.1 原色（CICP / H.273 primaries）

**已支持且与 zimg 逐元素对拍通过**（🟢 `colormath`）：`bt709` · `bt470m` · `bt470bg` · `smpte170m` · `smpte240m` · `bt2020` · `smpte431` · `smpte432` · `jedec-p22`。
**具名空间层**：sRGB · Adobe RGB · ProPhoto RGB · Display P3 · BT.2020 SDR/HDR 等。

| 缺口 | 性质 | 说明 |
|---|---|---|
| **8 film** | 政策性 | 精度不足，`fail-closed` 返回 null（安全） |
| **10 smpte428 / ST 428-1** | **结构性** | XYZ 三角的蓝原色 `y=0` ⇒ **xy 参数化在数学上表达不了**。不是「补数据」能解决，需要新的参数化或特例路径 |
| ColorMatch RGB | **实现缺陷** | `ColorSpaceRegistry.cs:142-144` 注册了 Spec，但**缺 `Rp/Gp/Bp/Wp`** ⇒ 任何矩阵转换 fail-closed。头注释（`:16`）称「归一化到 BT.2020」与实现不符 🔵 |

### 2.2 传递函数

**已支持**：CICP 1/4/5/6/7/8/13/14/15 · 16 PQ · 18 HLG；`Bt2100Ootf`（正/逆）· `Bt2446MethodA`（§4.1＋§4.2）· `ToneMapping`。

| 缺口 | 性质 | 说明 |
|---|---|---|
| 9 log100 · 10 log316 | 政策性 | `TransferCurve.cs:396` 显式 `null`，注释「不常用且无本项目素材」🔵 |
| 11 xvYCC · 12 BT.1361 · 17 smpte428(γ2.6) | 政策性 | 落到 `:397` 默认 `null`（fail-closed，安全）🔵 |
| **BT.2390 EETF** | 未实现 | `verify-color-tonemap.ps1:84-86` 显式 SKIP，**不给绿灯**（诚实） |
| **BT.2446 Method B/C** | 未实现 | 只有 Method A |

### 2.3 出口容器

`png` · `apng` · `tiff` · `webp` · `jpg/jpeg/jpegli` · `avif` · `gif` · `jxl` · `jxr` 均有能力表条目与标注通路。

### 2.4 GainMap

| 组合 | 状态 |
|---|---|
| JPEG Ultra HDR 编码 | **端到端实测**，且被第三方 `libultrahdr` 逐字段认证（`gainmap-thirdparty 13/0`）🟡 |
| JPEG Ultra HDR 解码 | 端到端实测（PSNR ≥30 dB 往返）🟡 |
| AVIF 增益图 识别＋解码 | **真实素材**端到端实测（`gainmap-isobmff 52/0`）🟡 |
| HEIC 增益图 识别＋解码 | **仅合成件**（元数据借自 AVIF 样本），**真实设备素材缺失** 🟡 |
| **AVIF / HEIC 增益图 编码** | **未实现**（`ColorTransformPlan.cs:541-546`：`SupportsGainMap` 仅 jpg/jpeg/jpegli）🔵 |

### 2.5 缺口的五类归因（决定「全方面」的准确含义）

1. **结构性不可补**：ST 428-1 / XYZ 三角。
2. **政策性拒收（安全）**：film、log100/316、xvYCC、BT.1361。
3. **未实现的标准**：BT.2390 EETF、BT.2446 B/C。
4. **能力不对称**：AVIF/HEIC 只解不编；标注层能写 `film=8`/`smpte428=17`（`FfmpegCommandBuilder.cs:938/946-948`）而数学层读不回（🟡）——**「能标注」与「能处理」是两件事**。
5. **素材/验证缺口**：真实 HEIC 增益图；第三方 XMP-only JPEG 增益图解码像素正确性。

> **判词**：把「全方面容纳所有图片色彩处理」当作**已达成**是危险的；当作**设计方向**是合理的。建议把目标重述为「**主流 SDR/HDR 全通路 + 边缘标准显式 fail-closed**」——后者其实已经做到了，而且是这套系统真正的优点。

---

## 3. 缺陷清单

> 分级：**P1** = 会导致错误交付 / 数据损坏；**P2** = 静默错配 / 验证盲区 / 能力宣称与实现不符；**P3** = 注释、死码、文档不一致。
> **本轮 P1 数量 = 0。**

### P2

| # | 缺陷 | 位置 | 证据 | 影响 |
|---|---|---|---|---|
| 1 | **死码簇：只写不读 / 恒 null / 零消费** | 见下表 | 🔵 | 制造虚假信心；读者以为有保障 |
| 2 | **容差纪律失效** | `ColorIntent.cs:134-148` | 🔵 | 声明「禁止散落魔数」，实为 4/6 常量零消费，等价判定散落 5 处魔数（`1e-9`/`1e-5`/`1e-4`/`2e-3`/`5e-3`） |
| 3 | **门禁结构上不可能红** | `tests/scripts/verify-colorfmt-matrix.ps1` | 🔵 | 全文无 `$pass`/`$fail`/`PASS=` 计数（仅前置条件 `exit 1`）⇒ 运行器归入 table-only，其断言永无失败可能 |
| 4 | **4 个含真断言却未接线的探针 mode** | `_run-step3-gates.ps1:20` 的 `$Probes` 字面量 | 🔵 | `pq`(13/0) · `bitdepth`(11/0) · `icc` · `gamut` 均不在默认 22 mode 清单，也无脚本调用 |
| 5 | **探针段缺「零断言闸」** | `_run-step3-gates.ps1:825-836` | 🟡 | 只查 `exit` 与 `fail≠0`，不查 `pass=0` ⇒ 整段 SKIP 可假绿（脚本段有闸，探针段没有） |
| 6 | **P3「清退 zimg」定案未落地** | 见下表 | 🔵🟡 | 计划书写「已定案：完全走自研管线」，但**改像素的外部滤镜站点仍在** ⇒「矩阵掌控所有像素改动」当前不成立 |
| 7 | **ColorMatch RGB 无色度定义** | `ColorSpaceRegistry.cs:142-144` | 🔵 | 该空间任何矩阵转换 fail-closed；注释与实现不符 |
| 8 | **引擎路径不消费 `plan.Backend`** | `QueueProcessor.cs:1222` | 🟡 | H1/H2 后端选择在引擎路径是死字段（仅记日志） |
| 9 | **GainMap 超广色域源疑似「只改标签不转像素」** | `GainMapJpegCodec.cs:112-115` ＋ `ColorSpaceRegistry.cs:132-138` | 🟡 | Adobe RGB/ProPhoto 的 `OutPrimaries="bt2020"` ⇒ 可能缺真实像素转换。**可达性窄，未实测，不作事实断言** |
| 10 | **双管线 HDR→SDR 契约差 ~10 dB** | `QueueProcessor.cs:1137` | 🔵 | 回退 legacy 时差异**已点名播报**（非静默），但两路结局不同本身是可用性缺口 |

#### 死码簇明细（P2-1）

| 项 | 位置 | 状态 |
|---|---|---|
| `LosslessForcesIdentityMatrix` | `ColorTransformPlan.cs:83`（声明）/`:583`（唯一赋值） | **只写不读**（全仓 0 读取点）🔵 |
| `ManualIntent` 整类 / `ColorIntent.Manual` | `ColorIntent.cs:100-132` / `:160` | `Create()` 零调用；`Manual` **从未赋值** ⇒ `PlanManual` 的 `m?.HasAny`、`BuildManualTarget` 恒走 null 支 🔵 |
| `ColorMetrics` 整类 | `ColorScheduler.cs:105-174` | 零消费者 🟡 |
| `CachedMatrix`/`MatrixCache`/`ClearCache`/`KeyOf` | `ColorTransformPlan.cs:618-619/1021-1046` | 缓存层整体死码 🟡 |
| `FindPixelColorEntry` | `ColorTransformPlan.cs:869` | 零消费者——**正是它要防的那类缺陷的守卫本身没接线** 🟡 |
| `ColorVerdict` 5 个降级码 | `ColorVerdict.cs:68/106/108/110/112` | 词表 > 生产，从不产生 🟡 |
| `ColorBackend.Lut` / `AlternativeKind.Target` | `ColorTransformPlan.cs:14` / `ColorVerdict.cs:25` | 枚举值从不使用 🟡 |

> ⚠ **主审推翻的一处代理结论**：代理报告暗示「Manual 策略（S4）失效」。**复核后不成立**——`ManualIntent` 确是死码，但**「高级色彩三元组」功能经另一条路生效**：`ColorIntentFactory.cs:113` 调 `EffectiveOutputColorTokens(o, …)` 读取 `ColorPrimaries/ColorTrc/ColorMatrix`，在 `:127` 装配成 `it.Target`；`PlanManual`（`ColorStrategyPlanning.cs:612`）走 `it.Target ?? BuildManualTarget(m)`，**`Target` 非 null 时功能正常**。⇒ 定性为 **P2 死码**，而非 P1 功能损坏。

#### 「仍靠外部工具改像素」的站点（P2-6 明细）

| 位置 | 滤镜 | 改像素 |
|---|---|---|
| `FfmpegCommandBuilder.Decision.cs:302` | `tonemap` ＋ `zscale`（HDR→SDR） | **是** 🔵 |
| `FfmpegCommandBuilder.Decision.cs:356` | `zscale`（目标色域映射） | **是** 🔵 |
| `ColorSpaceRegistry.cs:280`（`BuildGamutTransform*`，经 `Decision.cs:386/392`） | `colorchannelmixer` ＋ `zscale`（超广归一） | **是** 🔵 |
| `GainMapJpegCodec.cs:36` | `zscale=t=linear` | **是** 🟡 |
| `QueueProcessor.cs:4508/4588/4604` | `zscale`（cjxl/HDR 直编旁路） | **是** 🟡 |
| `ImageEncoderArgs.cs:254-261` | `palettegen/paletteuse`（GIF 量化） | **是** 🟡 |

### P3

| # | 缺陷 | 位置 | 证据 |
|---|---|---|---|
| 1 | `dither=on` 日志在 YUV 出口**不兑现**：`NeedsDither = TargetBitDepth≤8 && (Matrix≠null \|\| NeedsCurveChange)`，但 YUV 出口 `OutBitDepth` 恒取 16（`ColorStrategyPlanning.cs:378-379`）⇒ 走不到 8-bit 抖动内核；`ToLogLine` 仍打印 `dither=on`（`ColorTransformPlan.cs:346`）。⚠ 16-bit 中间件是**经过 A/B 实测的有意设计**（`:363-373`），故这是**日志口径**问题而非像素缺陷 | `ColorTransformPlan.cs:346/772` ＋ `ColorStrategyPlanning.cs:378` | 🔵 |
| 2 | 注释与实现不符 3 处：`ColorKernels.cs:190`（SIMD 文案）、`ColorSpaceRegistry.cs:555`（「同原色族」）、`RawColorPipeline.cs:161`（降位归属） | 同左 | 🔵🟡 |
| 3 | `ResolvePrimariesToken` 危险反查（`bt2020` 可命中 P3 色度）；当前 0 消费点 | `ColorSpaceRegistry.cs:521` | 🟡 |
| 4 | `ValidateCicpTriple` 白名单漏 5 个合法 primaries ⇒ 误报 | `ColorSpaceRegistry.cs:707` | 🟡 |
| 5 | 过期读数：`gainmap-engine` 记忆/文档记 124/0，**实为 138/0**；`GAINMAP_HEIC_AVIF_ANALYSIS.md:8` 记 29/0，**实为 52/0** | 文档 | 🟡 |

---

## 4. 正向确认（应保留的资产）

审计不应只报坏消息。以下经独立复核确认**成立且是真实优势**：

1. **矩阵布局与乘序全链一致**：行主序 × 列向量，构造端 / 执行端（`ColorKernels.cs:362-364`）/ ICC 读写端（`IccProfileService.cs:165`、`DetectIccPrimaries:132`）四处同形。🔵
2. **色适应正确**：`BradfordAdapt` 复合为 `M⁻¹·L·M`（`ColorSpaceRegistry.cs:787`），历史错序已修。🔵
3. **8-bit 抖动已居中且已修到码域**：`BayerCenter = 7.5f/16f`，`t∈[−0.46875,+0.46875]`、均值 0 ⇒ 对任意整数码值 `Round(v+t)==v` 全 16 相位成立；`contract` 的 C12/C13 判据经变异验证（改回 `0.5f/4f` ⇒ 288/768 系统性抬升转红）。🟢
4. **fail-closed 是默认姿态**：未收录标准返回 `null` 而非猜值；非法参数（`--bit-depth 47`、`--color-engine engin`、`--color-709-curve zimg2`、`--chroma 4:4:0`）**均被 CLI 显式拒绝**（实测 `rc≠0`、无产物）。🟢🔵
5. **契约拒绝带出路**：如「cjxl 出口语义装不进枚举」⇒ `exit 92` ＋ 点名原因 ＋ 替代建议。🟢
6. **GainMap 第三方独立认证**：`libultrahdr` 读自家产物，boost/capacity/gamma 全字段对账通过。🟡
7. **诚实的能力边界登记**：BT.2390 显式 SKIP 而非给绿灯；SDR 源不写零增益假 Ultra HDR 且点名。🟡

---

## 5. 综合建议

### 5.1 立即做（低成本、高收益，建议按序）

1. **给 `verify-colorfmt-matrix.ps1` 补上 `PASS=/FAIL=` 汇总与 `exit` 语义**——否则它是一张**永远绿的表**。这是当前性价比最高的一条。
2. **把 4 个未接线 mode（`pq`/`bitdepth`/`icc`/`gamut`）接进 `$Probes` 默认清单**，并同步四处散文（`$expectedScriptCount`、第 ⑪/⑫/⑰ 针、`docs/TESTING.md` §3.5、`docs/HANDOVER.md` 清单行）。它们已含真断言，白跑等于浪费。
3. **给探针段加「零断言闸」**（`pass=0` 必须红），与脚本段对齐。否则某个 mode 整段 SKIP 仍是绿。
4. **处置死码簇**：对每一项做「接线或删除」二选一，**不要留着**。优先级：`LosslessForcesIdentityMatrix`（avif 无损保证是否真的需要？）＞ `FindPixelColorEntry`（守卫本身没接线）＞ `ManualIntent`（决定 S4 是走 `Target` 还是 `ManualIntent`，二选一后删另一边）＞ 其余缓存/枚举/降级码。
5. **统一容差真值**：把散落的 `1e-9`/`1e-5`/`1e-4`/`2e-3`/`5e-3` 收进 `EquivalenceTolerances`，并给 `2e-3` 那条补「偏移量落在 1e-3~2e-3 的合成 ICC 必须报出差异」的夹具。
6. **修正过期读数**（记忆与 `GAINMAP_HEIC_AVIF_ANALYSIS.md:8`），避免后续按错基线判断回归。

### 5.2 中期（需要决策与排期）

1. **P3「清退 zimg」要么落地、要么改口径**。当前状态是「计划书说定案、代码里站点全在」，这是最容易被误读为「已完成」的一类账。若暂不落地，应在文档与 UI 文案上把口径改成「legacy 为**可达回退**，其像素路径由 zscale 承担」。
2. **明确 ST 428-1 的处置**：要么承认它是**永久边界**并写进能力表（推荐，成本低且诚实），要么引入 XYZ 专用路径（成本高，且需新的参数化）。
3. **AVIF/HEIC 的 GainMap 编码**：若「全方面」包含 HDR 交付，这是最显眼的空洞（只解不编）。建议先做**产品裁定**：是否在路线图内。
4. **补真实 HEIC 增益图素材**，并把第三方 XMP-only JPEG 增益图的解码像素正确性纳入门禁（现有 `gainmap` 探针把「ISO APP2 存在」当硬断言，对 XMP-only 文件必红 ⇒ **它不能充当第三方文件验证器**）。
5. **矩阵内核的 SIMD**：当前矩阵路径**纯标量**（`SimdPixelOps.cs:266` 自述「保持标量实现」），唯一 AVX2 路径 `FloatToSrgb8` 只服务 GainMap。若性能是目标，这是明确的可优化面；若性能非目标，建议**撤掉面板上「SIMD 优化」复选框**（它与色彩矩阵无关，易误导）。
6. **给色适应/白点补独立门禁**：目前只被 `matrix` 的行和=1 与 `colormath` 间接覆盖，无专项断言。

### 5.3 关于抽成独立库（ColorKit / 里程碑 2）

**结论：可以抽，但只能抽「数学内核 + 无 IO 的 Plan」，不能连执行层一起抽。**

| 分层 | 可抽取性 | 障碍 |
|---|---|---|
| `ColorKernels` / `TransferCurve` / `ColorSpaceRegistry` / `ColorSpaceDescriptor` / `Bt2100Ootf` / `Bt2446MethodA` / `ToneMapping` | **✅ 低障碍**（纯托管、无 Avalonia、无进程） | 无 |
| `ColorTransformPlan` / `ColorStrategyPlanning` / `ColorEngineRouter` / `ColorScheduler` | **⚠ 需改造** | 依赖 `FfmpegOptions`（Models）、`FfmpegCommandBuilder`、`IccProfileService`/`RawService` ⇒ 需先抽接口 |
| `RawColorPipeline` | **❌ 不可作为库核心** | 本质是 ffmpeg/cjxl/jxr 的**命令行编排器** ＋ 临时文件 ＋ `AppSettingsService.Current` 全局单例 |

**最大障碍 = 与 `FfmpegCommandBuilder` 的双向循环依赖**（命名空间级）：`ColorMapping → FfmpegCommandBuilder`（`ColorIntentFactory.cs:49/113/189`、`RawColorPipeline.cs:97/253/468/614`）与 `FfmpegCommandBuilder → ColorMapping`（`FfmpegCommandBuilder.cs:319/386/467/921/1017`、`Decision.cs:464/539/546/592`）。🟡

**建议**：里程碑 2 **先只抽数学内核**（收益立即可得：可在另一个 GitHub 项目中复用与单测），执行层留在宿主。等接口层（`I*Options`/`I*Probe`）抽干净后再抽 Plan。**不要**为了「架构好看」把 ffmpeg 编排器一起搬走。

**⚠ 前置条件**：抽库前必须先把 §3 的死码簇与容差真值处理掉——**把只写不读的字段和零消费的容差一起暴露成公共 API，等于把债转成对外契约**。

### 5.4 对「全方面容纳所有图片色彩处理」这一设计目标的建议

建议把目标**重述**为三档，而不是一个笼统的「所有」：

- **第一档（已达成，应作为卖点）**：主流 SDR/HDR 全通路——sRGB / P3 / Adobe / ProPhoto / BT.2020 / PQ / HLG，全部容器，且像素改动由自研内核承担。
- **第二档（显式 fail-closed，应写进能力表）**：film、ST 428-1、log100/316、xvYCC、BT.1361 —— **拒收并点名**，不猜、不静默。
- **第三档（明确不在范围内）**：BT.2390 EETF、BT.2446 B/C、AVIF/HEIC 增益图编码 —— 需要产品裁定是否进路线图。

这样定义后，「可用性」就从「是否无所不能」变成「**在每个承诺的格子上是否可信**」——而后者才是这套矩阵真正的强项。

---

## 6. 未验证 / 存疑（诚实边界）

以下项**本轮未取得确证**，不作事实断言：

1. `LosslessForcesIdentityMatrix` 未接线是否导致 **avif 无损产物实际错误**（仅证实「无读取点」，未跑产物对拍）。
2. **GainMap 超广色域源的底图映射**是否真的只改标签不转像素（读码推断，可达性窄，未实测）。
3. **HEIC 增益图真实素材**的正确性（现有覆盖仅合成件）。
4. **第三方 XMP-only JPEG 增益图**解码的像素正确性。
5. `10/12-bit` 全程无抖动（仅由 swscale 降位）的实际可见轮频带。
6. 引擎降级日志（`BitDepthRaised` 等）的端到端行为（受「不建产物」约束，仅读码）。
7. `verify-color-matrix-full.ps1`（82 KB，4 裁判＋4 负控）与 `verify-oracle-matrix.ps1` 本轮**未跑**（有意排除的长跑）。
8. 双管线 HDR→SDR 的 ~10 dB 差异为**文档转述 + 读码**，未复跑对拍。

---

## 7. 可复现命令

```powershell
# 0) 前置：ffmpeg 目录（探针亦可自动上溯 publish/PLAN/ffmpeg-full）
$env:FFMPEGGUI_FFMPEG_DIR = "C:/PLAN/ffmpegPictureUI/publish/PLAN/ffmpeg-full"
$P = ".\tests\ServiceProbe\bin\Release\net11.0\win-x64\ServiceProbe.exe"

# 1) 数学层对撞（本报告核心读数来源）
& $P colormath      # 期望 pass=12 fail=0
& $P matrix         # 期望 pass=15 fail=0
& $P curve          # 期望 pass=44 fail=0
& $P ootf           # 期望 pass=11 fail=0
& $P bt2446         # 期望 pass=18 fail=0
& $P contract       # 期望 pass=136 fail=0
& $P verdict        # 期望 pass=41 fail=0
& $P iccname        # 期望 pass=10 fail=0
& $P plan --strategy manual   # 观察 S4 经 it.Target 生效

# 2) 死码与门禁复核（本报告 §3 的取证式）
Select-String -Path src/FfmpegGui/Services/ColorMapping/*.cs -Pattern "LosslessForcesIdentityMatrix"
Select-String -Path src/FfmpegGui/Services/ColorMapping/*.cs -Pattern "EquivalenceTolerances\."
Select-String -Path tests/scripts/verify-colorfmt-matrix.ps1 -Pattern '\$pass|\$fail|PASS='

# 3) 非法参数应被显式拒绝（fail-closed 姿态）
& ".\src\FfmpegGui\bin\Release\net11.0\win-x64\FfmpegGui.exe" --headless -i in.png -o out.png -f png --bit-depth 47 --dry-run
```

---

## 附录：与 2026-09-24 旧审计的差异

| 旧审计结论 | 本次独立核验 | 处置 |
|---|---|---|
| 原色表 5 token 共用 P22 常数（P1） | **已修复**：`bt470m` 实差 0.461 → 1.76e-4；`colormath` 转绿 | 结案，勿重做 |
| `smpte428` 冒充 `smpte431`（P1） | **已修复**：改为 fail-closed 返回 null，进入「故意不收录」白名单 | 结案，勿重做 |
| 8-bit 抖动中心偏 +0.344 LSB（P2） | **已修复**：`BayerCenter=7.5f/16f`，C12/C13 变异验证通过 | 结案，勿重做 |
| `EquivalenceTolerances` 4 常量零消费 | **仍然成立**（本轮复核确认） | 仍开放，见 §5.1-5 |
| P3「清退 zimg」 | **已定案但未落地**（站点仍在） | 仍开放，见 §5.2-1 |
| tonemap 链标签/像素失配（P2-②） | 射程已收窄至 legacy 链；默认路径走自研定义式 | 部分开放 |

---

*报告完成时间：2026-10-02 · 全程只读，未修改任何源文件，未执行构建。*
