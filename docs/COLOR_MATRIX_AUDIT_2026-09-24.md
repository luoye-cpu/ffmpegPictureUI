# 色彩矩阵审查报告（2026-09-24）

范围：`ColorSpaceRegistry`（原色表 / 矩阵推导 / 色适应）、`TransferCurve`+`Bt2100Ootf`（传递函数）、
`ColorKernels`+`SimdPixelOps`（逐像素执行）、`ColorTransformPlan`+`ColorStrategyPlanning`+`QueueProcessor`（标签↔像素裁决）。
GainMap 容器层与抽库另见 `HANDOVER_2026-09-22.md §4 第 11 行`。

两条判据按维护者口径执行：**①符合真实标准/行业情况；②不允许"只改标签"和"无意义的精度损失"**。
本报告里每一条"缺陷"都标了它属于哪一类，以及修它会不会踩到②。

---

## 0. 一页结论

| # | 结论 | 类 | 证据 |
|---|---|---|---|
| 1 | **原色表把 5 个不同标准压成同一组常数，而这组恰好只是 JEDEC-P22 的** ⇒ `bt470m/bt470bg/smpte170m/smpte240m` 四个 token 拿 P22 矩阵算，逐元素实差 **0.052–0.461** | ①不符合真实情况 | `ServiceProbe colormath` 对 zimg 实测（见 §2 表） |
| 2 | **`smpte428` 被写成 `smpte431` 的副本**（CICP 10 = ST 428-1，原色是 CIE-XYZ 三角、白 1/3,1/3）⇒ 实差 **1.99** | ① | 同上 |
| 3 | **zimg 的 `t=bt709` 实为纯 γ(1/2.4)**，与 BT.709-6 定义式在阴影区差到 **27 LSB@8bit**（mean 7.5）；传统 tonemap 出口把 `t=bt709` 写进命令行、同时把 `-color_trc bt709` 写进容器 ⇒ **标签对标准不成立** | ②里的"只改标签"陷阱，且**不能靠改标签修** | §3 实测 |
| 4 | **8-bit 出口的有序抖动中心不居中**：`Bayer4[i] − 0.5f/4f` ⇒ 偏置 **+0.344 LSB**，16 个相位里 **6 个**会把"本可 1:1 精确表示"的码值抬 +1 | ②无意义精度损失 | `ColorKernels.cs:120-126 / 615` 读码 + 算术（§4.3） |
| 5 | **`CarryIcc`/`None` 计划交回 ffmpeg 重编码**（`QueueProcessor.cs:1126-1131`），而保真 ICC 判据只认 ProPhoto（`IccProfileBuilder.cs:113`）⇒ 非 ProPhoto 超广源可能出现"计划说像素零改动、实际被归一" | ②嫌疑，**未定案**（需一次产物对拍，见 §6） | 委托点已核实到行 |
| 6 | `EquivalenceTolerances` 六个常量里**四个是死的**（`MatrixToIdentity`/`MatrixElements`/`CurveParams`/`Chromaticity` 消费点 = 0），实际等价判定用的是散落的 `1e-9`/`1e-5`/`2e-3` | ①口径失控（该类的注释自己写着"禁止散落魔数"） | grep 计数（§4.5） |

**同时给出正面结论**（避免报告只报坏消息）：矩阵布局与乘序全链一致（行主序 × 列向量，构造端/执行端/ICC 读写端四处同形）；
Bradford 复合顺序 `M⁻¹·L·M` 正确且已修对历史错误；ICC 写出走 D50 语义并同步写 `chad`，没有"未适应值落盘"的路径；
`bt2020 / smpte432 / smpte431 / jedec-p22 / bt709` 五格与 zimg 逐元素一致到 **≤2.4e-4**；
查表用 double 构建、f32 逐像素，未发现为省事而降精度的地方。

---

## 1. 方法（以及我自己被推翻的三条）

四线并行只读审查（常数表 / 传递函数 / 逐像素 / 裁决层），外加两组**外部参照实测**：
矩阵对 zimg 对拍、BT.709 曲线对拍。所有子代理结论我都回读原文，其中**三条不成立**，记录如下（这三条若直接抄进报告就是假陈述）：

1. 「`EquivalenceTolerances` 全仓零引用」⇒ 错。该类有 7 个引用点（`ColorSpaceDescriptor.cs:245`、`ColorStrategyPlanning.cs:389/399/429` 等）。
   真相是**其中四个常量**零引用。
2. 「`ColorKernels.cs:165` 声称复用 `SimdPixelOps.FloatToSrgb8` 是空头」⇒ 半错。`FloatToSrgb8` 确实被 `GainMapEncoder.cs:377` 调用；
   错的是那句 `<summary>` 挂在 `EncodeFromLinearInPlace` 上，而该方法体内是标量循环。属**文案与所在方法不符**。
3. 我自己第一版的 `colormath` 判据：把 `bt709→bt709` 返回 null 判成 FAIL。null 是"同原色不映射"的**契约**，
   判红是我写反了方向；同时 `film` 未收录是 fail-closed 而非错值。两处已按正确方向改写。

另有两条 harness 假象，值得单独记住：
- 最初用 `color=red` 源 + 16-bit 出口量 zimg，得到"每个 token 都是单位阵"——那是**负值与 >1 被出口削平**的假象。
  换 `gbrpf32le` float 通道后矩阵才现形。
- PowerShell 变量**大小写不敏感**：计数器 `$n` 与 `for` 边界的 `$N` 是同一个变量，被覆盖成 0 ⇒ 样本数 0、
  比较空转，却照样打印出一句"判读：zimg 走的是纯 γ(1/2.4)"。**空断言能产出看起来正确的结论**。改名后同一条判据给出 255 个样本。

参照可信度自检：`pin=bt709→bt709` 必须是精确单位阵 ✅；`pin=bt2020` 必须等于 ITU BT.2080 发布值、
也等于本仓 `ProbeMatrix` 里已入库的期望常量 ✅（实差 1.1e-6）。

---

## 2. 实测：本仓原色表 vs zimg（外部参照）

测法：单位基向量以 `gbrpf32le` 喂进本机 ffmpeg（git-2026-08-06，含 zscale/zimg），
`zscale=pin=<T>:tin=linear:min=gbr:p=bt709:t=linear:m=gbr` 只换原色不换曲线，读回三列构成矩阵。
判据 `Tol = 1e-3`（zimg 输出只到 5 位小数）。

| token | 本仓 vs zimg 逐元素 maxErr | 折合满量程 | 定性 |
|---|---|---|---|
| bt709 | — 契约 null | — | ✅ 正确（不映射） |
| **bt470m** | **4.61e-1** | ~46% | ❌ 错（zimg: R 列 1.4862；本仓给 P22 的 1.0253） |
| **bt470bg** | **5.20e-2** | ~5% | ❌ 错（zimg 给 ≈单位阵带 1.044 白点归一） |
| **smpte170m** | **8.57e-2** | ~9% | ❌ 错 |
| **smpte240m** | **8.57e-2** | ~9% | ❌ 错（与 170m 在 zimg 里确实同原色，本仓"同"对了这一对、但整族值仍错） |
| film | null | — | ⚠ 能力缺口，走 fail-closed（安全） |
| bt2020 | 1.14e-6 | — | ✅ |
| **smpte428** | **1.99e+0** | ~199% | ❌ 冒充 smpte431；ST 428-1 是 XYZ 原色三角 |
| smpte431 | 2.36e-4 | — | ✅（D63 白点写对了） |
| smpte432 | 3.95e-6 | — | ✅ |
| jedec-p22 | 4.92e-6 | — | ✅（⇒ 反证：表里那组共用常数**就是 P22**） |

`colormath` 末尾还有一条独立断言：**"不同标准不得算成同一矩阵"**，实测 **11 对违规**
（470m/470bg/170m/240m/jedec-p22 五个 token 两两同矩阵 = 10 对，加 smpte428≡smpte431）。
探针总读数：**pass=6 fail=6**。

根因是 `ColorSpaceRegistry.cs:555` 那句注释自述的判断：
「（470M/470BG/170M/240M/FCC/P22 同原色族，差异在传递/矩阵）」——**"同原色族"这一步跨过了标准边界**，
差异不只在校传递，原色本身四套不一样。

**活体范围**（决定定级）：`LinearMatrixBetweenPrimaries` 在产品侧目前只有一个消费者
——`GainMapEncoder.cs:126`（UltraHDR 底图色域映射，`sourcePrimaries → bt709/bt2020`）。
该路径的现实输入是 bt2020/P3/bt709（三格全对），所以要害是：
①一旦有 601 系标签的 SD 素材被拿去生成 GainMap，矩阵错 ~9% 满量程；
②**里程碑 2 把这张表作为公共 API 暴露给外部后，外部消费者的输入域不再由我们限定** ⇒ 同一格缺陷从"低概率"直接变"P1"。

---

## 3. 实测：zimg 的 bt709 编码到底是哪条曲线

线性灰斜坡 256 点 → `zscale:t=bt709` → 读 16-bit 码值，与两条候选分别比：

| 参照 | max | mean |
|---|---|---|
| ITU-R BT.709-6 定义式（α=1.0993, β=0.08178，趾段 4.5L） | **6978 LSB@16b** | 1921 |
| 纯 γ(1/2.4) | **9 LSB@16b** | 3.2 |

⇒ **zimg 把 `bt709` 实现为纯 γ(1/2.4)**，与定义式的偏差折合 **27.2 LSB@8bit（max）/ 7.5 LSB@8bit（mean）**，
主要在阴影（趾段被整段跳过）。本仓 `ColorIntent.cs:85` 注释写的"拟合 RMS 7e-5"只在**中高调**成立，
它没说明趾段——按现在的措辞读，会让人以为差异可忽略。

产品侧已有对策，但只覆盖一半：`Transfer709Curve.Std`（默认）在 `ColorTransformPlan.cs:639` 关掉 H2 通道，
使**引擎自己**用 `Bt709` 定义式编像素（`ColorKernels.cs:396-398`）。
**没覆盖的那一半**是传统 tonemap 链：`FfmpegCommandBuilder.Decision.cs:236` 定 `tmDstT="bt709"`、
`:263` 把它塞进 `zscale=...:t={tmDstT}`，出口再写 `-color_trc bt709` ⇒ 像素是 γ2.4、标签是 bt709，
且不受 `--color-709-curve` 管辖。

> 修法只能是**把像素提到标签的水位**（走内核 OETF，或让 tonemap 出口也过 Std 分支），
> 不能把标签改成 "gamma2.4" 了事——CICP 里没有那个 transfer，且那是维护者点名禁止的"只改标签"。

---

## 4. 逐层结论

### 4.1 常数来源
SDR 曲线全部归一到 7 参数规范形（`TransferCurve.cs:11-12`），PQ/HLG 为闭式；常数硬编码但**逐条可核**，
`Bt2100Ootf.cs:9-16` 连 BT.2100 的 Note 编号都引了。风险不在这层，在 §2 的原色表。

### 4.2 色适应与 ICC 语义
`BradfordAdapt` 的复合为 `M⁻¹·L·M`（`ColorSpaceRegistry.cs:730-738`）——历史错序已修，注释里留着当年
"同白点时 L=单位阵 ⇒ 看不出问题"的教训。ICC 写出端 `IccProfileBuilder.cs:128-140` 先解原色、再算 `chad`、
写 D50 语义的 rXYZ/gXYZ/bXYZ，**没有未适应值直接落盘的路径**。
两处待核：`PrimariesXyD50`（:216-227）反推 xy 时未做 D50→D65 逆适应，其自辩只在"点在三角形内"时成立；
`IccProfileBuilder.cs:130/140` 的 `chad` 方向（native→D50 还是 D50→native）需对照 lcms 参考 profile 定。

### 4.3 逐像素精度
- **抖动不居中（本报告第 4 条）**：`Bayer4` 取 0..15/16，`−0.5f/4f = −2/16` ⇒ t ∈ [−2/16, +13/16]，均值 **+5.5/16 = +0.344 LSB**。
  居中写法应减 `7.5/16`。后果：8-bit 源经 `format=rgb48le` 后码值恒为 257 的整数倍（本可 1:1 无损降位），
  16 个相位中 6 个被抬 +1 ⇒ 纯色与平滑渐变上会长出 Bayer 斑。**这是纯粹白花钱的精度损失**，
  与"是否要抖动"无关——保留抖动、只把中心摆正即可，属②的负例（不是降级，是修实现）。
- ~~`ColorKernels.cs:363` `clamp ? Clamp(x,0,1) : Clamp(x,0,1)` 两分支同式~~ ⇒ **已修（2026-09-25 Lane C）**：`LookupEncode` 不再带
  恒等的 `clamp` 参数，且表 + `clamp=false` 的组合在建表处就 `throw`（同 §P2 的"不钳位不建表"约定）⇒ 死开关消失。
- ✅ **已修（2026-09-26）**：`TransformRgba8` 的抖动量原先加在**线性域**（旧 `:335-336`，现为 `:351-362`）再查 OETF
  ⇒ 趾段斜率 12.92 把它放大。~10 是我当时的**估算**，实测口径如下（`contract` 的 C13，源码值扫遍 0–255、16×16 覆盖 16 个相位）：
  **改动 213/768 个码值、最大偏离 6 档、最低受损源码值 = 2**（全在暗部）；修成**码域**加法后同一判据是
  `改动 0 / 最大偏离 0`，而"抖动仍生效"的反向哨兵（跨色域矩阵 + 非中性输入）给出 `改动 185/768、最大偏离 1 档`。
  ⚠ 该路径**产品零调用**（"只被门禁养着"这一条不变）⇒ 本修的是"两条 8-bit 出口不留两副面孔"，
  以及 P3 把自研出口定样时不许把这副面孔复制成活路。
  ⚠ 顺带记一条取数教训：哨兵第一版用**灰阶**输入 ⇒ 报"改动 0/768"，看着像"我把抖动修没了"；
  真因是三条通道相等时行和为 1 的原色矩阵**保中性** ⇒ 输出落在整数码上、抖动本就无事可做。
  ⇒ 判"抖动是否还活着"必须用**会产生分数码值**的输入（带彩色 + 矩阵）。
- 16→10/12-bit 降位实际由 swscale 做，而 `RawColorPipeline.cs:161` 的注释说降位"只发生在变换那一次遍历" ⇒ 注释与实现半对不上。
  （既有实测已证明 swscale 该步取整无系统偏置、且 `accurate_rnd`/`sws_dither` 等参数对它不起作用，见项目记忆
  「色彩管线精度架构、降位归属与 GPU 边界」⇒ **不要为此改产品实现**，只需把注释改对。）

### 4.4 SIMD 与"回退"承诺
矩阵内核**只有标量实现**（`SimdPixelOps.cs:253` 自述"保持标量实现"），`FloatToSrgb8` 有 AVX2 但只在 GainMap 路径用，
且 AVX2 与标量只承诺 ±1 LSB（`tests/GainMapTestHost/Program.cs:565`）。
`AutoUseSimdBinaries`（面板"SIMD 优化"复选框）**零消费者** —— 这条已在 `HANDOVER §4 第 10 行` 登记待决策，
本报告只补一个事实：**它连"回退到标量"这条路都不存在，因为矩阵路径本来就是标量**，
所以撤掉这个复选框与实现它是两回事。

### 4.5 容差的口径失控
`EquivalenceTolerances`（`ColorIntent.cs:134-148`，注释写着"禁止散落魔数"）六个常量实测消费点：
`ContainmentEpsilon` 2、`MinConfidenceForMapping` 5、其余 **4 个为 0**。
真正在用的等价判定散在：`ColorSpaceIdentify.cs:32 KnownSpaceTol = 2e-3`、
`ColorTransformPlan.cs:760` 的 `1e-9`、`:208` 的 `1e-5`。
**2e-3 是要害**：它在 `ColorSpaceIdentify.cs:73` 把最接近的理论值直接赋给 `d.RgbToXyzD50`（"采用理论值"），
随后 `Equivalence()` 判等价 ⇒ 走 L0 无标注直通。2e-3 的矩阵系数差折合 12-bit 出口约 8 LSB、8-bit 约 0.5–0.6 LSB（阴影 ×3.5）
⇒ 一个被偏移 1.5e-3 的 ICC 会被静默判成"同空间、不用改像素也不用带标签"。
需要一条夹具：**偏移量落在 1e-3 ~ 2e-3 之间的合成 ICC，必须报出差异**（现在不会）。

---

## 5. 缺陷清单（按定级）

| 级 | 缺陷 | 属哪一类禁令 | 修法方向 |
|---|---|---|---|
| P1 | 原色表 5 token 共用 P22 常数（§2） | ① | 按 H.273 分表填正；填正前**先把这些 token 判为"未知"走 fail-closed**，别拿错的算 |
| P1 | `smpte428` 冒充 `smpte431`（§2） | ① | 同上；ST 428-1 是 XYZ 三角，不能当 P3 |
| P1 | tonemap 链标签 `bt709` / 像素 γ2.4（§3） | ②只改标签 | 像素提到标签水位（走内核 OETF 或让该链受 Std 管辖）；**不得改标签了事** |
| P2 | 8-bit 抖动中心偏 +0.344 LSB（§4.3） | ②无意义精度损失 | 减数改 `7.5/16`，并补"精确可表示码值 no-op"断言 |
| P2 | `KnownSpaceTol = 2e-3` 吸附 + 死容差常量（§4.5） | ①/②交界 | 统一走 `EquivalenceTolerances`；对 2e-3 单独做偏移夹具 |
| P2 | `CarryIcc`→legacy 委托后仍可能改像素（§0 第 5 条） | ② | 需一次端到端对拍定案（§6）；若成立，则把 S1-carry 也纳入 PlanDelegated 那类守卫 |
| P3 | `LookupEncode` 死三元（§4.3） | ②潜伏 | 要么实现"不钳"分支，要么删参数并显式报错 |
| P3 | 注释与实现不符三处：`RawColorPipeline.cs:161` 降位归属、`ColorKernels.cs:165` 的 SIMD 文案、`ColorSpaceRegistry.cs:555` 的"同原色族" | ① | 改注释（这三条**本来就该改文字**，不涉及像素） |
| P3 | `film` 未收录、JXR"像素改而零标注"、GIF 无标注通路 | 能力缺口 | 已 fail-closed/已诚实报错的，登记为能力边界，不算缺陷 |

---

## 6. 未定案 / 下一步

1. **S1-carry 是否真的被 legacy 改了像素**（§0 第 5 条）：委托点已核到 `QueueProcessor.cs:1126-1131`、
   保真判据只认 ProPhoto 已核到 `IccProfileBuilder.cs:113`，但"最终产物色度是否变了"要一次端到端实测：
   Adobe RGB(带 ICC) → png/jxl auto 目标，回读产物 ICC 色度，应等于源 Adobe 原色；若读到 0.708（BT.2020）即定案。
2. `colormath` 探针**未接进 54 条清单**：它当前含 6 条真红（就是 §2 的结论），接进去会让整轮不可用。
   接法两选一，需维护者点头：**(a)** 先修原色表再接锁；**(b)** 先接为"已知红"并在 `TESTING.md §6` 登记（与 `verify-oracle-matrix` 同类的处置）。
3. `chad` 方向与 `PrimariesXyD50` 逆适应两点，需一份 lcms 参考 profile 作外部锚（本机 `ffmpeg -f lavfi iccgen` 可生成，未做）。
4. 里程碑 2（色彩数学层进 ColorKit）**在这些修完之前不宜开始**：那张原色表一旦成为外部 API，P1 两格就变成对外的错。

---

## 6-B. 全网格实测的**未收口状态**（2026-09-25 凌晨，别当成结论）

新建了 `tests/scripts/verify-color-matrix-full.ps1`（源空间 × 目标空间 × {8,10,12,16} × {png,webp,tiff,jxl,avif}，
四把独立尺子 CAP/LBL/NUM/RT + 负控 NC1/NC2）。**它目前不可信，本轮没有用它下任何产品结论**：

1. **已归因（是我的驱动错，不是产品）**：`--headless -o <路径>\art.png` 时，产品**把 `art.png` 当成输出目录**——
   实测生成 `mx3/art.png/`（目录）与真产物 `mx3/art.png/in.png`（按输入文件名命名）。
   于是 `Test-Path` 返回 True（目录也算"存在"）、`[IO.File]::ReadAllBytes` 报 `Access … is denied`、
   `Get-Item` 报 `Attr=Directory` ⇒ 我那三把需要读产物的尺子（LBL/NUM/NC1）**全部取空**，`made=True` 是假的。
   复现式（10 秒）：`FfmpegGui.exe --headless -i <d>\in.png -o <d>\art.png -f png --bit-depth 8 --color-space sRGB`
   → 日志 `[2/1] in.png → 已完成`、exit 0，而 `find` 给出 `art.png`（DIR）与 `art.png/in.png`（FILE）。
   ⇒ **修驱动方向**：产物路径不能由 `-o` 猜，必须"跑完后枚举 `-o` 目录取实际文件"；且 CAP 尺子里 `made` 的判定
   要改成"存在**且是文件**"（现在的 `Test-Path` 会把目录算成有产物 ⇒ 静默假绿，属我自己踩过的同族坑）。
   ⚠ **留给产品的独立一问（未定性）**：`-o` 带扩展名时变目录、而 L1 那些**扩展名省略**的用例能正常出文件并被
   ffprobe 读到 ⇒ 这条分叉是有意设计（多输入批量输出到目录）还是缺陷？需读 `CliParser`/`QueueProcessor` 的
   输出路径构造点再判，不要凭这次的观察就下结论。
2. 已修掉的三处 harness 自身缺陷（都是"看起来正常其实是空断言"的形状）：
   ① 产物名模板把扩展名写成位深（`..._png8.8` ⇒ reader 按扩展名派发失败）；
   ② **NUM/RT 在"比过 0 格"时照样判绿** ⇒ 已改为 `($numN -gt 0) -and (...)`；
   ③ 诊断脚本里把函数参数命名为 `$args`（PS 保留自动变量）⇒ 整个函数静默不产出，只打印了第一行。
3. 顺带记下**候选但未复验**的产品线索：`--bit-depth 47` 疑似未被拒（NC2 红）。它出自同一次可疑调用，**不作数**，
   复验方式：修好 ① 之后单独跑 `-i <png> -o <png> -f png --bit-depth 47`，要求 exit≠0 且无产物。

**本仓成熟地基的读数（可信，与上面无关）**：
`pwsh -File tests/scripts/verify-oracle-matrix.ps1 -Layer L1` ⇒ **194 格：177 过 / 17 红，280 s**（平均 1443 ms/格）。
17 条红的 reason 全部是 `no-product(exit=1)`，其中 6 条落在色彩轴
（`--color-space BT.2020`、`--color-strategy carry|manual`、`--icc-mode CarryIcc`、`--color-tone-map reinhard|hable|mobius|bt2446a`）、
5 条落在编码器/格式轴（dng / Cjpegli / Jxr / Dng）。
⇒ **这 17 条尚未逐条归因**：必须先区分"本机缺外部工具导致的合法拒绝"与"真缺陷"，再决定计入审计。这是下一轮的第一件事。

## 6-C. 驱动第二轮读数（2026-09-25）：LBL 已可信，其余三把仍在 harness 手上

`-Layer quick` 实测（24 格）：**PASS=4 FAIL=6**，逐把尺子的状态：

| 尺子 | 状态 | 读数 |
|---|---|---|
| **LBL 标签** | ✅ **可信并已转绿** | 20 个成功格**全部**带对了标注：`→sRGB` = `BT.709`(png cICP)/`sRGB primaries`(jxl)、`→Display P3` = `SMPTE EG 432-1`/`P3 primaries`、`→BT.2020` = `BT.2020, BT.2100`。第一轮的 20 条"无标注"是**我的 matcher 只比 CICP token**、而 exiftool/jxlinfo 给人话名 ⇒ 假红 |
| **CAP 位深/容器** | ⚠ 出红但**未归因** | 4 格 `→BT.2020 jxl8/jxl16` = `rc=1 且无产物`（同目标换 png 出口正常）⇒ 要么 cjxl 侧真缺陷、要么是参数组合不合理被正确拒绝。**必须读产品 stderr 才能定性**，本轮没抓到 |
| **NUM / RT** | ❌ 驱动没修好，读数不作数 | 参照链 ffmpeg 命令 20 次取数失败；RT 因产物在 `-o` 目录里而没解析到文件 ⇒ 两把都还没真正比过一次 |
| **夹具（源空间轴）** | ❌ P3 / BT.2020 源**四格都没生成** | `,iccgen`（无参）与 `iccgen=iccfile=` 两种写法都没出文件；`ExecCli` 把 stderr 丢了 ⇒ 下一步就是**把 stderr 打出来**再修 |
| **NC2** | ⚠ **候选真缺陷（已两次观察到）** | `--bit-depth 47` 与 `--bit-depth 10/12` 打到 png 出口时 **exit=0 且有产物** ⇒ 非法/不支持的位深没被拒。需要读产物真实位深（`pix_fmt` 那次取数没打出来）确认是"静默降级"还是"按 8-bit 出且标注自洽" |

**根因已找到（一处 PowerShell 语法陷阱同时打坏夹具与 NUM）**：双引号串里 `$pp:t=...` 被解析成**作用域/驱动器限定符** `${pp:t}` ⇒ 变量整个变空、
zscale 收到 `p===gbr` 而报 `Undefined constant`。凡是"变量紧跟冒号"的滤镜串一律写成 `${var}:`。修好后 quick 层读数：
72 格 = 夹具 6/6、**LBL 60/60 绿**、**NUM 60 格与 zimg 参照链全部 ≥60 dB、0 红**、RT 仍 0 组（产物在 `-o` 目录里未解析）。

**那 12 格 CAP 红已定性 = 我的期望错，不是产品缺陷（反而是正确的诚实拒绝）**：
`→BT.2020 + jxl` 的 stderr 原文是
「cjxl 出口：计划要求标注 bt2020/bt709，但该语义落不进 cjxl 的 `color_space` 枚举（仅 sRGB / Display P3 / Rec.2100 PQ / HLG），
且计划未提供 ICC ⇒ **拒绝产出一个标注兑现不了的 JXL**」+ 给出路（`-e Ffmpeg` 或改可枚举目标）+ `exit 92`。
⇒ 网格的 CAP 判据要把这类"语义装不进容器枚举"的拒绝列为**合法拒绝白名单**，并**反向断言**它必须红在 rc≠0 且日志点名原因。
⚠ 同一条日志里还确认了 §3 的实现侧现状：`后端=H1(进程内融合内核) [口径黑名单: zimg 的 bt709 实为纯 γ2.4 → 禁 H2]`
⇒ 引擎路径确实按 Std 走定义式；缺的只是**传统 tonemap 链不受该门管辖**那半边。

**NC2 已定性 = 真缺陷（静默降级，属维护者明令禁止那一类）**：`-f png --bit-depth 47` 与 `--bit-depth 12` 均
`rc=0 + 有产物`，且**整段 Debug 日志里没有任何**「位深/降级/上限/拒绝」字样（关键词命中 = 空）。
对照：`-f jxl --bit-depth 8` 那条路径**会**打 `⚠️ 降级（BitDepthReduced）：出口位深降为 8bit（容器上限）`
⇒ 播报机制本来就有，只是 PNG 侧与"值域校验"没接上。修法（窄）：① `--bit-depth` 只接受 {8,10,12,16}，其余**显式拒绝**
（现在 `int.Parse` 后无人校验 ⇒ 47 能过）；② 出口容器无法满足所请求位深时，走同一个 `BitDepthReduced` 播报或拒绝，
**不许静默改**。取证命令同 §7。

**`-Layer full` 第一次尝试：中断在 105/360 格，没有读数**（2026-09-25）。
日志停在 `== 2) 网格 full` 那行之后，工作目录 `tests/output/validate/colormx_203cc1dc` 里最后一格是 `m_srgb16_BT2020_webp8`；
20 秒内格数不增（105 → 105）且 `tasklist` 里既无 `pwsh` 也无 `FfmpegGui` ⇒ **后台任务被回收**，不是产品挂死。
⇒ 驱动有两个必须修的观察缺陷：① **网格循环全程不打进度** ⇒ 中断后看不出跑到哪、卡在哪；
② 收尾用 `Remove-Item $d` 会连证据一起删 ⇒ 中断时什么留不下（应只在全绿后清）。
修完再跑 full，**别把 quick 层的结论当成 full 层的**。

**更正（2026-09-25，覆盖上文两条"待修"）**：上面 §6-C 里"驱动有两个必须修的观察缺陷 / 修完再跑 full"已经**做完并复验**——
逐格进度、有红时保留工作目录并自动落 CSV、NC1 换成"同源两目标互比"（PSNR 22.75 dB ⇒ 尺子有牙）、
CAP 新增 `refused-semantics` 档（合法拒绝必须 rc≠0 + 日志点名 + 无产物，否则仍红）。
quick 层最终读数 **PASS=10 FAIL=1**（唯一红 = 产品缺陷 `--bit-depth`），full 层 360 格正在跑。
另外 NC2 的定性已从"未确认"变成**实测确认**，且分性质：PNG 的 10/12 = 容器无此模式但未播报；**TIFF 的 10/12 = 容器能兑现而代码不兑现 ⇒ 纯缺陷**（证据表见 `COLOR_MATRIX_FIX_PLAN` P2.5）。

## 7. 可复现命令

```powershell
# 本仓矩阵 vs zimg（当前读数 pass=6 fail=6）
.\tests\ServiceProbe\bin\Release\net11.0\win-x64\ServiceProbe.exe colormath

# zimg 的 bt709 是哪条曲线（当前读数：与定义式 max 6978 / 与 γ2.4 max 9，样本 255）
pwsh -NoProfile -File C:\Users\20210\AppData\Local\Temp\z709\probe709.ps1

# 单 token 手测（float 通道，别用 16-bit 出口，会被削平）
$f='C:\PLAN\VQBench\PLAN\ffmpeg-full\ffmpeg.exe'; $T=$env:TEMP
printf '\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x80\x3f' > $T\fR.raw   # gbrpf32le: G,B,R 平面
& $f -f rawvideo -pix_fmt gbrpf32le -video_size 1x1 -i $T\fR.raw `
  -vf "zscale=pin=4:tin=linear:min=gbr:p=bt709:t=linear:m=gbr,format=gbrpf32le" `
  -frames:v 1 -f rawvideo -y $T\z.raw; od -An -tf4 $T\z.raw
```
