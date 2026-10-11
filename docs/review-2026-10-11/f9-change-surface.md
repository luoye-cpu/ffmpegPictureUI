# F-9 实现面清单（lane R2，只调研不改码）—— 传统 tonemap/zscale 链 `t=bt709` 标签/像素失配

绑定构建指纹：`src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.dll` sha256 前 16 = **17D1AA61D18B384A**（本轮实算，与主循环口径一致）。
引用 ServiceProbe 读数：`tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe decision` 本轮实跑 = `60 格、pass=4 fail=0`——该 exe 与上述 dll 同宿构建，**宿主可能滞后 src**，以下所有"现状"读数均声明绑该指纹。

任务定性原文锚：`docs/FIX_PLAN_2026-10-11.md:68-71`（F-9：五格清单、可达性复验、撤 6 格豁免、"改标签了事…被维护者禁过"）；
豁免定性锚：`tests/scripts/verify-color-engine-xcheck.ps1:52-59`（引 `docs/MANUAL_VERIFICATION.md:223-224`、`docs/archive/BUILD_HANDOVER.md:123-124/:151`）、`docs/TESTING.md:2372-2397`（第 58 条，6 格白名单原文）、`docs/HANDOVER.md:1630-1643`（#29 表，含"恒 exit 0 ⇒ 没有门禁"处置）。
前提锚（不许动）：`tests/scripts/verify-color-caps.ps1:95-115` —— 「zscale t=bt709 与纯 γ2.4 一致…⇒ 引擎必须自己按标准定义式编码（禁 H2）」「与定义式确实不同…不可混用」。修 legacy 后这条文案的辖域应从"引擎"扩到"引擎与 legacy 命令行 std 口径"（文案账，非判据）。

---

## 1. 五格路径逐格清单（谁写 t=bt709、谁能走到、修后像素由谁产）

公共事实（静态推证）：
- 容器/标签侧不存在独立的"输出 `-color_trc`"参数——`FfmpegCommandBuilder.cs:45-52` 只在 **`-i` 前**写输入声明；产物 trc 由链末 zscale 的帧标签传递（PNG 由 cICP 后置用 `EffectiveOutputColorTokens` 的 `outT` 写，`QueueProcessor.cs:1524`；JXL/JXR/ICC 后置同读 outT：`QueueProcessor.cs:1759/2393/2844`）。⇒ **改像素不许动 outT**，否则标签被连带改写（违反维护者"不改标签"决定）。
- `Decision.cs` 的链只在 **legacy 执行体**（显式 `--color-engine legacy`、auto 回落：动画/DNG/引擎不可走格式、cjxl 两步法前置、`ColorStrategyPlanning.cs:677-678` 的 `PlanDelegatedHdrSdr` png8 直通回交）时真的跑 ffmpeg；默认 auto 引擎格像素由 H1 进程内内核产（`ColorTransformPlan.cs:800-825` 已删 H2 死后端），目标曲线经 `TransferCurve.ForCicpTransfer("bt709")→Bt709`（`TransferCurve.cs:110-111,407`）= **定义式，已诚实**。⇒ 本缺陷面 = legacy 命令行的出口像素，五格全在命令生成侧。

### 格 A `src/FfmpegGui/Services/FfmpegCommandBuilder.Decision.cs:307`
`tmDstT = capT ?? target.trc ?? "bt709";`（简化模式显式目标 + HDR→SDR tonemap 链；链串在 `:323-325`：`zscale=pin=…:tin=linear:min=gbr:p={tmDstT→t=}:m=bt709`）
- 到达组合：HDR(PQ/HLG) 源 + `--color-space` ∈ {BT.709、BT.2020、DCI-P3、Adobe RGB、ProPhoto、ColorMatch}（`ColorSpaceRegistry.cs:66/84/124/133/138/143` 的 `OutTrc="bt709"`）+ 非 manual 策略；容器 = png/apng/avif/jxl/jxr（jpg/webp/tiff 被 `capT=iec61966-2-1` 钳走，`FfmpegCommandBuilder.cs:998-1001`，不变）；执行体 = legacy 或 auto 下 jxl 前置/delegated 格。
- 修后像素生产者：候选 (a) 链拆式（见 §1-方案）(b) 交回内核（违反逃生门契约，否）(c) 换标签 iec（改标签，否）。

### 格 B `:332`
`outP = tmDstP; outT = tmDstT;` —— **标签传播点，非独立源头**（与验证 lane 一致）。它把 A/C 的 tmDstT 交出去成为宣称值（PNG cICP=1、ICC trc=bt709、UI 文案）。修后**语义不变、数值不变**；它的角色变成"锁"：outT==bt709 ⇒ 命令行出口必须已是定义式像素（新判据的锚点）。

### 格 C `:313`
`tmDstT = capT ?? options.ColorTrc!;`（manual 策略 + `--color-trc bt709`（`HasExplicitColorTarget`，`FfmpegOptions.cs:213-215`）+ HDR 源）
- 到达组合：`--color-strategy manual --color-trc bt709`（高级参数在位即算，`Decision.cs:166-173` 值派生判据）+ 同 A 的容器族。
- 注意 `:213-222` 的宣称值分支独立算 `outT = capT ?? options.ColorTrc` ⇒ 用户标签 bt709 与链同 token，**标签是用户承诺**，只能提像素。

### 格 D `:379`
`d.PixelChains.Add($"format=rgb48le,{preScale}zscale=pin={srcP}:tin={srcT}:min={srcM}:p={dstP}:t={dstT}:m={dstM}");`，`dstT = capT ?? targetParams.trc!`（`:350`）
- 到达组合：**SDR 源** + 显式目标 BT.709/BT.2020/超广名（同 A 的 spec 表）+ 非 manual + `!d.Faithful`；容器 png/apng/avif/jxl/gif/mp4 族（jpg/webp/tiff 被 capT 钳）。
- ⚠ 双端暴露：源若 bt709 标签（`srcT=inputTrc ?? …`，`:355`），`tin=bt709` 的**解码**也是 zimg γ2.4 EOTF ⇒ 修要连解码端一起拆（见 §5）。

### 格 E `:409-410`（D1 超广归一，唯一默认参数可达）
`ultraFilter = ColorSpaceRegistry.BuildUltraWideToBt2020Filter(pm.sourceGamutName, uwHdr, uwHlg);`
`(np, nt, nm) = ColorSpaceRegistry.UltraWideNormalizeTokens(...);` → `nt=spec.OutTrc="bt709"`（`ColorSpaceRegistry.cs:227/236-241`），串在 `BuildFilter :285-286`：`…zscale=pin={outP}:tin=linear:p={outP}:t={outT}:m={outM}`；首段 `zscale=tin={encIn}:t=linear` 中 `encIn=ExactTransferName(...)` 对 BT.2020-SDR 源回 `"bt709"`（`:352-366`）⇒ 同族解码端也失配。
- 到达组合：源探测为 UltraWide（Adobe RGB、SDR BT.2020、DCI-P3、ColorMatch；**ProPhoto 被保真优先截走**：`Decision.cs:119-136` + `FfmpegCommandBuilder.cs:1364-1382` + `IccProfileBuilder.cs:110-114`（`IsFaithfulOnly` 仅 ProPhoto）；HDR-trc 源 `:1368` 不截、走 PQ/HLG 支不产 bt709）+ 无显式目标 + 非 manual + 非 carry。默认 auto 下像素走引擎（诚实）；**legacy、jxl 的 cjxl 两步前置（`QueueProcessor.cs:4892/4933` 也调同一 BuildFilter/BuildToBt2020Filter，stdT 常为 bt709）** ⇒ 这两条路的产物是缺陷体。
- 出口 sRGB 支 `:415 BuildGamutTransformFilter(src,"sRGB")` → t=iec61966-2-1（zimg 有真分段 sRGB）⇒ **不在本缺陷面**。

### 方案与最小改动
- **O1（最小改动，推荐）**：一个共享 helper「trc==bt709 且口径==std ⇒ 出口编码改拆式」：`zscale=…:t=linear:m=…`（线性域完成 primaries/矩阵）→ `lutrgb=r='…':g='…':b='…'`（BT.709-6 定义式分段 OETF，常数 α=1.099、β=0.099、d=0.08125、趾 4.5、γ=1/0.45，与 `TransferCurve.cs:110-111` 同源）→ `setparams=color_primaries=…:color_trc=bt709:colorspace=…`（找回帧标签）。**已正向命中**：随包 ffmpeg `-h filter=setparams` 列 `color_trc bt709=1/iec61966-2-1=13/linear=8`，`-filters` 列 `lutrgb`。三处调用点：`Decision.cs:323-325`、`Decision.cs:379`、`ColorSpaceRegistry.BuildFilter:277-287`（后者需把口径/拆式参数传进去，同时覆盖 QueueProcessor:4892/4933 的 jxl 路）。标签（outT、`nt`、`:332/:383/:423` 传播）一律不动。`zimg` 口径 ⇒ 原样 `t=bt709` 一字不改（复刻历史产物，与 `TransferCurve.Bt709Zimg` 的立项理由同构，`TransferCurve.cs:141-152`），并点名。
  - ⚠ 需实测两点：① 解码端（格 D/E 源 bt709 标签的 `tin=bt709`）是否同步拆为 `lutrgb` EOTF（16bit 整型域量化地板）；② lutrgb 表在 rgb48le 的求值上限=65535（表达式 `pow` 精度）与下游 iccgen 读帧标签生成 ICC 的 trc 是否仍走定义式（`QueueProcessor.cs:1683` 注释暗示同源，需读回）。
- **O2**：格 A/C/D/E 交回进程内内核 —— 违反 Decision.cs:20「命令行是唯一执行体」逃生门契约，且 `ColorTransformPlan.cs:800-817` 已裁定"本档不存在 H2"，否。
- **O3**：token+标签换 iec61966-2-1 —— 对**用户显式承诺 bt709** 的格 C（及格 A/D 的显式目标）＝改标签，违维护者决定；对格 E 的自造标签虽属"两边都改"先例（`:298` + `verify-decision-delivery` ⑮ 定样 :721-722），但 ⑮ 的定样明确限定"**未指定目标**的 SDR 默认出口"，不覆盖显式目标 ⇒ 否。

## 2. `--color-709-curve std|zimg` 可否直接扩到传统链 —— 可以（复用，不新造）

既有链条（现仅引擎侧消费）：
- 选项默认：`src/FfmpegGui/Models/FfmpegOptions.cs:548-549`（`std`=ITU-R 定义式；zimg=对齐历史产物）；
- 严格解析：`CliParser.cs:784-791`（`ParseTokenStrict … "std","zimg"`，非法值报错——同文件 :1020-1022 登记"已收口"）；
- 唯一现消费者：`ColorMapping/ColorIntentFactory.cs:322-323`：`if (string.Equals(o.Color709Curve,"zimg",…)) it.Transfer709 = Transfer709Curve.Zimg;`
- 计划层语义：`ColorTransformPlan.cs:171`（默认 Std）、`:735-736`（Std ⇒ `h2Possible=false`，禁把 bt709 交给 zscale）、`:750-753`（Zimg ⇒ 出口曲线换 `Bt709Zimg`）、`:823-824`（口径黑名单点名）、`:833`（`ZimgDivergentTransfer = bt709|bt470bg|smpte240m`，**但只有 bt709 有实测背书**，`:836-844`）、`:864-881`（`Transfer709Note` 不许静默）；
- 轴登记：`tests/scripts/_lib-axes.ps1:263`。

复用要串的判定点（最小改动清单）：
1. `DecideOutputColor`（`FfmpegCommandBuilder.Decision.cs:107+`）**本来就持 options** ⇒ 在三个 emit 点前加同一判据：`!zimg口径 && trc=="bt709"` ⇒ 拆式；否则原样。（判据函数直接复用 `ColorTransformPlan.ZimgDivergentTransfer`——已是 `internal static`，同一 assembly。）
2. `ColorSpaceRegistry.BuildUltraWideToBt2020Filter:219 / BuildToBt2020Filter:264 / BuildFilter:277`：加"出口 trc 的兑现方式"参数（由调用方按口径传），避免 registry 自己读全局。
3. `QueueProcessor.cs:4892/4933`（cjxl 两步前置）调用点跟着传（同判据）。
4. 点名：仿 `Transfer709Note` 在 legacy 日志出一行（`[色彩裁决]` 或 `[cmd]` 前注释行）——zimg 口径下"标签 bt709/像素 γ2.4 是刻意的历史对齐"，std 口径下拆式生效可静默（Std 本就是默认，`ColorTransformPlan.cs:849-852` 的同款理由）。
调用点账：CliParser 1、Options 1、ColorIntentFactory 1（不动）、Decision emit 2（:325/:379）、Registry emit 1（:285，带 4 个上游 caller：Decision:409/415 + QueueProcessor:4892/4933）、点名 helper 1。

## 3. 镜像断言清单（改语义前必须找全；状态 = 修后预判）

⚠ 双拷贝注意（项目记忆「contract 断言有两份拷贝」）：`decision/contract/engine` 断言在 `tests/ServiceProbe/Program.cs`（门禁实跑）与 `tests/core|Modes、tests/engine/Modes`（拆分副本）各一份——**两处都要改**，只改副本会全绿但零执行。

| # | 位置 | 内容 | 预判 |
|---|---|---|---|
| 1 | `verify-color-engine-xcheck.ps1:44-49`（5 用例）、`:62-69`（白名单）、`:77-93`（判红载体） | 引擎 vs zimg 参照逐像素 | 撤 4 格豁免**不动参照**⇒ `bars16/t2 × BT.2020→sRGB、sRGB→BT.2020` 4 格**红**；参照（`Program.cs:7499-7567`，B 路 :7532-7533 用裸 `tin/t=…bt709`）同步改拆式后**转绿**。需新增：无（它自己就是要转绿的旧牙）。 |
| 2 | `verify-decision-delivery.ps1:689-736`（⑮：链末 t==标签兑现 + 定样 iec61966-2-1 + 负控）| 默认 tonemap 格出口 | **绿**（⑮ 钉的是"未指定目标默认"=iec 支 :298，拆式只接管 bt709 支）。**需新增**：⑮b —— legacy 显式 BT.2020/BT.709 目标格：std 下命令行末段不得出现裸 `:t=bt709`、须含拆式+lutrgb+setparams、产物标签仍 trc=1；zimg 口径反向锁（必须仍 `:t=bt709`，逐字节等于历史）。现状**零覆盖**（这正是缺陷存活原因，⑮ 注释 :695-696 自证"此前没有任何门禁钉过这条链"）。 |
| 3 | 同上 `:182-195`（③ hdr/png8 cICP `-match 'bt709'` 走 primaries）、`:860-881`（⑰）、`:883-916`（⑰b：legacy P3 格 trc 必 `iec61966-2-1`、auto 反向锁不得出现 tonemap） | tonemap 出口标签 | **绿**（P3/sRGB 目标链本就走 iec；⑰b 反向锁判据不受拆式影响）。 |
| 4 | `verify-color-strategy.ps1`（全文 grep 无 t=bt709/链末曲线断言；:276/:288 只打 png.CanHdr 误触发与 Reject 形状） | 策略层 | **绿**。 |
| 5 | `verify-color-tonemap.ps1:33-82`（Hable/Mobius 算子 vs vf tonemap） | 算子不是 OETF | **绿**（零相关）。 |
| 6 | `ServiceProbe decision`（`Program.cs:4237+` ≡ `tests/core/Modes/decision.cs:81-120`）：取**最后一个 zscale** 的 `t=` 与宣称对账（注释 :81-82「必须取最后一个」） | 宣称落实网格 | **修后必红**：宣称 bt709 vs 末级 zscale `t=linear` ⇒ `宣称未落实` 计数>0 ⇒ `Check(mismatch==0)` 红。**需要新增/改**：落实形态加第 6 种——`setparams=color_trc=<claimT>`（+`lutrgb` 在位证明像素域已换）。本轮实跑（指纹 17D1AA61D18B384A）= 60 格 4/0 绿，改前先落判据扩展再改产线，否则红无因。 |
| 7 | `ServiceProbe curve`（`Program.cs:1214-1330`：Bt709 锚 :1223/1226、Bt709Zimg 锚 :1294-1306、两口径差量级 :1313-1321「不得变成 token 的默认解读」） | 曲线常数 | **绿**（常数不动；拆式表达式必须与该锚同式，作实现约束）。 |
| 8 | `ServiceProbe contract` C7/C7b（`Program.cs:1879-2016` ≡ `tests/core/Modes/contract.cs:430-482`）+ `engine` #29 端到端（`Program.cs:4462-4543` ≡ `tests/engine/Modes/engineab.cs:132-210`：std≠zimg 字节、max 24–32LSB@8bit） | 计划层口径 | **绿**（引擎侧一字不动）；#29 的 24–32LSB 带恰好是拆式的期望差量级，可作 legacy 新断言的参照界。 |
| 9 | `verify-color-caps.ps1:95-115`（(f) zimg γ2.4 前提防版本漂移） | 前提锚 | **绿且必须继续绿**（拆式不改 zimg 行为）；文案辖域扩（见头注）。 |
| 10 | `verify-color-wiring.ps1:199-234`（(6) 引擎 vs legacy **均值亮度** 相对差<10%，实测 7.9%，注释 :203-204/:226-227/:233 明写"两侧编码曲线本就不同、不要求逐点同值"） | 跨管线口径分歧的镜像豁免 | **绿**（修后差值塌缩到编码噪声）；但注释即"第二套口径说辞"——**需要新增**：该格升级为逐点/PSNR 同域断言（拆式生效后引擎==legacy 同口径），并删注释里的分歧申述，否则留文档性镜像。 |
| 11 | `verify-color-matrix-full.ps1:818/:867-874/:943`（`$curveBlocked = srcTrc或dstTrc==bt709 ⇒ 跳过 LEGACY 像素参照`）+ `:654/:662`（cjxl-legacy 裁决行文案含 `出口=bt2020/bt709`） | 因 zimg γ2.4 而豁免的参照比较 | 跳过逻辑**修后失去理由 ⇒ 恒绿无牙**（需新增：撤 `curveBlocked`，让那批格吃上定义式参照，量级界抄 #29 的 24–32LSB）；`:654/:662` 文案匹配**绿**（标签不动）。 |
| 12 | `run-color-tests.ps1:116-122`（A4：手写 `-vf …t=bt709` 参照命令 + "CICP bt709 系"断言） | 外部参照命令（非产品链） | **绿**——⚠ 它的 `-vf` 串**正是缺陷形态**，改产品时不许顺手"同步修参照"，否则失去对照臂。 |
| 13 | `tests/ui-cli/uicliGroup.cs:169/:212-213`、`tests/UiTestHost/Program.cs:3015/:3058-3059`（`zimg2` 严格拒绝、大小写归一） | 选项面 | **绿**（解析不动）。 |
| 14 | `_lib-axes.ps1:263`（轴 std|zimg 已登记） | 矩阵/取证轴的可用性 | **绿**；legacy 新格要进矩阵取证时直接复用该轴。 |
| 15 | `verify-gainmap-engine.ps1:590`（手写 zscale 参照命令 `t=iec61966-2-1`） | 外部参照 | **绿**（不经 iec 支）。 |
| 16 | `ServiceProbe xcheck` 参照实现（`Program.cs:7522-7541`）+ 判据（:7564-7566 PSNR≥55 / maxDiff≤1500 / ΔE≤1.5） | 参照口径 | **需要改**（(1) 的另一半）；判据阈值本身不动。 |

（**镜像断言合计：16 项**——其中"修后必红需配套"2 项：#6 decision 落实形态、#1/#16 xcheck 参照；"需新增锁"4 项：#2 ⑮b、#10 逐点、#11 撤 curveBlocked、#17 见下缺口；其余预判绿。）

### 点名缺口（五格之外的同族 emit，无任何像素级牙）
`QueueProcessor.cs:4892/4933`（cjxl 两步法前置调 `BuildToBt2020Filter/BuildUltraWideToBt2020Filter` ⇒ t=bt709 链，**默认 auto 也走**）与 `QueueProcessor.cs:4906`（`zscale=transferin=bt709:transfer=PQ` 的**解码端**同族失配）。decision-delivery 的 jxl 格只量标签与链数（:627 注），xcheck 不覆盖 tonemap/cjxl 路 ⇒ 修时**必须新增**该路红绿判据（jxl 产物 djxl 解回像素 vs 定义式参照，抄 matrix-full 的既有手法）。

## 4. `$knownNonDefects` 6 格逐格（`verify-color-engine-xcheck.ps1:62-69`）

| 格 | 行 | 豁免理由（原文摘录） | 撤除后接管 |
|---|---|---|---|
| `bars16.png/BT.2020→sRGB` | :63 | "zimg 的 bt709 是纯 γ2.4（与定义式分歧）" | 接管 = xcheck 自身该格 + 修后参照（#16 拆式）⇒ 转绿；**不修参照就撤 ⇒ 无人接管，红**。 |
| `bars16.png/sRGB→BT.2020` | :64 | 同上 | 同上（编码端）。 |
| `bars16.png/PQ(BT.2020)→sRGB` | :65 | "⚠ 2026-09-28 部分已修…剩余差异=无 tone mapping 时 HDR 高光被硬裁切…**不是『不可修的口径分歧』**" | **与 bt709 无关**：F-9 不接 tone-map 接入 ⇒ **保留豁免、撤除"bt709 γ2.4"依据措辞**；若整条删除则红且无人接管 ⇒ 点名新缺口（挂在 #55 tonemap 契约接入的账上，FIX_PLAN:71 同款对账）。 |
| `t2.png/BT.2020→sRGB` | :66 | 同 :63 | 同 :63。 |
| `t2.png/sRGB→BT.2020` | :67 | 同 :64 | 同 :64。 |
| `t2.png/PQ(BT.2020)→sRGB` | :68 | 同 :65 | 同 :65。 |

⇒ **撤 4（:63/:64/:66/:67），改文案留 2（:65/:68）**。同时 :10 的变异自述（"移除任一条⇒该格转红"）与新白名单规模要在 :5-9 注释里同步改账（该注释也是镜像文案）。

## 5. 产物影响面（字节会变什么）

静态可推（判据 = 出口 trc 恰为 `bt709` 且口径 std 且**像素走命令行链**）：
- **变（legacy 显式）**：`--color-engine legacy` + HDR 源 + `--color-space {BT.709, BT.2020, DCI-P3, Adobe RGB, ProPhoto*, ColorMatch}` 或 `--color-strategy manual --color-trc bt709` ⇒ png/apng/avif/jxl/jxr（**阴影区**像素位移，编码端 Δ max≈27LSB@8bit / ≈6986@16bit；标签零变化）。*ProPhoto 显式目标不截保真（`:1379` 目标≠源 ⇒ 正常进链）。
- **变（默认 auto 可达）**：UltraWide SDR 源（Adobe RGB / SDR-BT.2020 / ColorMatch / DCI-P3）+ 目标 auto/缺省 + (i) legacy/auto 回落格，(ii) **jxl 的 cjxl 两步前置（QueueProcessor:4892/4933）——默认也走** ⇒ jxl 一族像素变（需实测确认默认 auto 下 remap 闸门 `:2375 PixelChains.Count>0` 的触达面）；(iii) gif/mp4 等引擎不承接格式的回落。
- **不变**：jpg/webp/tiff（capT=iec61966-2-1 钳，`FfmpegCommandBuilder.cs:998-1001` 及注释 :1191）；默认 auto + 引擎可走的格（H1 本就定义式）；`zimg` 口径全部格（拆式不介入，历史逐字节可复现）；PQ/HLG 输出格；D1 的 sRGB 支（:415）。
- **位深**：与位深无关（8/10/12/16 全同判据）；16-bit 域差量级 6986 LSB（任务数），需以锚点式（#7）复核表达式后再采信。
- **需实测项**：① jxl 默认路触达面；② `tin=bt709` 解码端是否同批拆（格 D/E 源侧）——拆则影响面再扩"源带 bt709 标签"的全部 std 格；③ lutrgb@rgb48le 量化地板与 iccgen 读回的 ICC trc（读回 exiftool，按"写进产物的元数据要被第二个读者读回"规矩）；④ `--color-engine legacy` + `--color-trc bt709` 对 gif/jxr 的出口（无 CICP 通路容器：标签走 ICC 后置，`QueueProcessor.cs:2844`）。
- **PSNR 基线**：本仓无数值型产物基线库；受影响比较器 = xcheck 阈值（:7564-7566，修参照后**重跑**）、wiring(6) 均值（<10%，重跑应大幅收窄）、matrix-full 的 `lgCurveSkip` 计数（:943，撤豁免后归零）。"PSNR 峰值参考用错域"先例（`tests/core/Modes/contract.cs:50-51`：PQ 1.0=10000nit 当 203nit，把探针自身缺陷豁免成口径分歧）⇒ 新参照比较臂**必须先证明两臂真的分叉**（std vs zimg 产物 md5 不同、且与 γ2.4 参照差落在 24–32LSB@8bit 带内）再谈转绿。#55 的"两路 HDR→SDR 差 ~10dB"账与本修复正交（tonemap 算子/白点，不动），xcheck :97-123 的回落点名断言预判绿。

## 6. 先红后绿验收顺序（全部绑指纹，改前红态不许用"改完看起来对"）

0. **底账**：记 `FfmpegGui.dll` sha256[0:16]（本轮=17D1AA61D18B384A）；`ServiceProbe decision` 现状 60 格 4/0（已实跑，见头注——注意它只量"宣称==命令行"，量不到标签/像素失配，故它绿不是无罪证明）。
1. **结构红（改前必得）**：对 §1 每格跑一条 `--color-engine legacy --dry-run`?（无 dry-run 就 `-i 夹具 -o 目录` 后读 `[cmd]` 行）；断言末级 zscale 串含 `:t=bt709` 且出口宣称=bt709 ⇒ 命令形态证据（A: `--color-space BT.2020`+hdr 源→png；C: `--color-strategy manual --color-trc bt709`+hdr→png；D: sdr 源→avif `--color-space BT.2020`；E: AdobeRGB 源→jxl auto）。
2. **像素红（改前必得）**：同格产物 `ffmpeg -f rawvideo` 解回 rgb48，与**独立参照**（同 ffmpeg 生成：`t=linear,lutrgb=<定义式>,setparams`；即修后形态做参照臂）逐像素比 ⇒ max|Δ|≥24 LSB@8bit（带界抄 #29 的 24–32）判红坐实；另与 γ2.4 参照比应≈量化地板（自证 zimg 口径归因，防"参照坏了"假红，遵 `--对照臂必须真的分叉` 规矩）。
3. **门禁红**：撤 xcheck :63/:64/:66/:67 ⇒ 跑 `verify-color-engine-xcheck.ps1` ⇒ 恰 4 格红、exit 1（红态入账）。
4. **新锁先红后绿**：decision-delivery 加 ⑮b（std 拆式 + zimg 反向锁两条）与 decision 模式的 setparams 落实形态（先只加判据不改编产线 ⇒ ⑮b 对现有 bt709 格红，落实形态分支保持 decision 网格绿）。
5. **改产线（§1 O1）⇒ 复绿序**：xcheck 4 格（改参照后）→ ⑮b → decision 网格（含新形态）→ ⑮/③/⑰/⑰b → wiring(6) 逐点升级 → matrix-full 撤 curveBlocked → caps(f)（全程绿）→ tonemap/strategy/engine/contract/curve/uicli（全程绿，抽查）→ #29 端到端 std≠zimg 保持。
6. **产物回归矩阵**（抄 `tests/output/t43/matrix2_pre_measure.log` 手法）：改动前后逐组合 md5 —— 预期变 = legacy×{A,C,D,E} std 格 + jxl 默认路（若实测坐实）；预期**逐字节不变** = 引擎默认格、jpg/webp/tiff 格、zimg 口径格、PQ/HLG 格、auto 无目标 SDR-srgb 格（"不误伤"锁，`NeedsHdrToSdrTonemap:1214-1215` 的旧训）。
7. **收尾指纹**：全部绿后重记 `FfmpegGui.dll` 新指纹并把 §5"需实测"四项的读数写进交付账（读不到的登记"未判定"，不猜）。
