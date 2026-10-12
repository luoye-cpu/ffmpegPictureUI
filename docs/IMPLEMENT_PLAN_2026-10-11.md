# 完整实现规划（2026-10-11，续 `FIX_PLAN_2026-10-11.md`）

口径（维护者定的两条硬规矩，全程适用）：**① 按标准/一手参照实现，不许凭记忆或凭"规范的名字"猜规范内容；② 每一片都要有能红的判据——先拿改前的红态证据，再改，再复绿，再绑构建指纹。**

当前已验状态：F-1 / F-2 / F-4 / F-10 已落地（F-1、F-2 已做变异验牙；F-10 进程内全绿但**兑现锁 L-10 欠**）；F-3 已按机器标定重新落地（新指纹 `17D1AA61D18B384A`，**待重跑归属**）。源码未提交。

---

## 批次 B —— F-3 收口（进行中，只剩验收）
- **已做**：`ValidateIccProfile(path, out reason)`（ICC.1 的 MUST 项：`128 ≤ size ≤ 文件长`、`acsp`、class ∈ 七种、cs 为四字符码、PCS ∈ {`XYZ `,`Lab `}、tag 计数与目录边界、**签名严格升序**）；`IsValidIccProfile` 保持原签名以免动调用面；响亮拒绝走同文件既有的 `log ?? Trace.WriteLine` 写法，两条提取路径（`ExtractIccToTempFile` / `ExtractIccFromBase64(…, log)`）都接上了 log。
- **白名单是被机器标定的，不是猜的**：本机 lcms 自带 17 件实测 ⇒ 10 个合法件全收（`spac` 类 test3/test4 的 PCS 实测是 `Lab ` ⇒ 不必为 abstract 扩 `none`）；三个负样本各死于**不同**判据（`toosmall` 声明 448>158 / `bad` class+PCS 非码 / `bad_mpe` cs 含非打印字节）。
- **待做**：L-3 锁写进**被执行的那份探针**（`tests/ServiceProbe/Program.cs`）：10 收 + 3 拒 **逐样本点名原因**（不许只校"拒了 3 个"）+ `alltags.icc`（65 tag）专门压"升序+边界"那一格；然后重跑 17 条子集重新绑指纹。
- **风险**：收紧后过去"带非法 ICC 当合法用"的输入会变成"无 ICC"⇒ 标注可能变。缓解已内建（拒绝必响 + 正例必收），若某件厂商 profile 被判非法，按负例对待并**先取 lcms 的行为对齐**再放宽，不许放宽到"回到只校 acsp"。

## 批次 C —— GainMap 数值（F-5/F-2/F-3/F-4/F-8）
前置：等 `docs/review-2026-10-11/gainmap-reference-memo.md`（lane R1）到手，**六条裁决全以 libultrahdr/libavif/libjxl 本机一手实现为准**，memo 里标"未判定"的不许落地。
- C-1 **F-5 offset 互逆**：按 memo 裁决二选一（前向施加声明的 offset / 声明改为实际值），锁 = 往返 `‖decode(encode(x))−x‖` 上界（进程内 `GainMapTestHost`，**不起产品 exe**）+ 一条"元数据声明 == 前向实际用"的结构锁。
- C-2 **F-2 取整**：先定趟数（一趟 or 两趟），取整口径**跟趟数走**；锁 = 与参照同一趟式逐点比对。
- C-3 **F-3 钳位顺序**：分母取参照所取的那个值；判据只能打"编解码自洽"，**不许**打"更好看"。越界率（正确矩阵复算 = 32.4%）与恢复暗度（≈39%）各测一次做前后对照。
- C-4 **F-4/F-8 通道与色度采样**：先把"声明"降到与实现同一句话（flags 说多通道而数据 4:2:0 = 说谎），再把实现提到声明水位；第三方读回必须由**第二个独立读者**命中同一事实。
- 每项都要先红后绿，且 F-1 那族"两副面孔"的教训适用：**同一份数学不许有两份实现拷贝**（本仓已因 `contract` 两份拷贝踩过一次）。

## 批次 D —— 兑现锁（欠账清零）
- **L-10（F-10 的兑现，任务 #9）**：四格断言进被执行的那份探针 —— ① BT.601-7 分段式往返闭合 + 趾段斜率对 `dng_color_space.h:134-140` 逐点；② `smpte170m` 源的产物 `-color_trc` 读回必须是 `smpte170m` 且**不许**落 null/告警路；③ ICC 的 TRC 与像素实际编码曲线同源；④ `Adobe RGB` 仍 = γ2.2 没被带跑。变异验牙：把 `Bt601` 退回 `Gamma22` ⇒ ①②必须红。
- **L-4（F-4 的兑现）**：截断样本 ⇒ `Determinate=false` + 具名原因；"无增益图"必须是**确定**才能报 false（三态不许塌两态）。
- **L-7（性能回归无人看守）**：`bench` 进受管清单 + 绑 `FfmpegGui.dll` 前 16；`PQ→sRGB 单核≥40 MPix/s` 那格 FAIL 要么登记成已知红、要么随 F-1/F-8 转绿。**⚠ 这会动 73 条受管基线（`$scriptList` 与 `$expectedScriptCount` 同步）** ⇒ 属"会动基线"的改动，做之前先量有多少读者观测它（本仓规矩：需单独一批的先量再写）。
- **L-6**：交给 lane（`_probe-test-group-coverage.ps1` 单文件改造）；回归后由我核 diff 与读数。

## 批次 E —— F-9（改已出货产物，等备忘）
等 `f9-change-surface.md`（lane R2）。方向已定：**把像素提到标签水位**（走 `TransferCurve.Bt709` 定义式），不是改标签；复用既有 `--color-709-curve std|zimg` 机制优先于新造（"先找既有机制做最小改动"）。撤 `$knownNonDefects` 那 6 格时，每格必须点名"撤后由哪条断言接管"，没人接管就是新缺口。**先中 25 条红态证据 → 再改 → 再复绿 → 重跑 17 条 + 全量**。

## 批次 F —— 性能（只在正确性清零后动）
F-8 表缓存（保 F-1 修复后的吞吐，键必须含曲线对/位深/clamp）→ F-12 探测链剩余 2 次 exiftool → F-11 元数据链的点名补齐（真合并仍待点头）。每条要"墙钟下降 **且** 点行数不减"双判据，且**计时必须单机跑**（并发期禁测）。

## 批次 G —— 跨仓与收尾
- `C:\PLAN\ColorGainKit\src\ColorMatrix\ColorSpaceRegistry.cs:780` 的同一个 `1.0633`：**另一个公开仓，需点头**才改；改法与本仓一致（附 lcms/skcms/libjxl 三锚 + 行和≈1）。
- `contract` 两份拷贝的消除（保留被执行的一份，另一份要么删要么改成转发）——这是本轮踩坑的根因修复。
- 收尾账：`docs/PIPELINE_AUDIT_ADVISORY_2026-10-11.md` 的撤回清单与"未证边界"按实际落地情况更新；**不主动 commit/push/tag**。

## 每批统一的验收口径
1. 读数必须指到日志/探针输出文件；2. 判据必须在"无修复"状态下红过一次（变异验牙），否则记"无牙"不算锁；3. 每批结束重绑 `FfmpegGui.dll` 前 16 并重跑受影响子集；4. `枚举为空必须拦`、`汇总数==点名条数`；5. 环境性红（如 TEMP 不可写把计数压成 0）要先重定向复跑再归因，不许直接当回归、也不许当没这回事。

### 2026-10-11 批次 B/D 收口读数 + 一条自纠
- **L-3**（`iccname`，被门禁执行）：正样本 9/9 全收（第 10 件 `test4.icc` 本机不存在，打 SKIP 不给绿）；lcms 三件坏件全拒且各给非空原因；三支合成负样本各点名 `size` 判据。`iccname` = **17/0**。
- **L-4**（`avifgm`）：干净件必须「确定·无」；顶层截断、meta 子链断裂都必须 `Determinate=false` + 具名原因。`avifgm` = **14/0**。
- **L-10**（`colormath`）：`γ=1/0.45`、`1/C=4.5`、`D/4.5=0.0180555556` 贴 DNG SDK `β=0.0180539685108078`（2e-6）、`CicpToken=smpte170m`、往返闭合偏差 `0.0000000000`、反向哨兵 `Adobe RGB==bt470m` ⇒ **19/0**。
  变异验牙：路由退回 `Gamma22` ⇒ `FAIL L-10 CICP 码 6…（实 bt470m）`、`18/1`；还原 ⇒ `19/0`。
  ⚠ 锁的覆盖面边界（如实记）：常数那几条读的是 `TransferCurve.Bt601` 字段本身，变异"路由"时不红 ⇒ 只有路由那条咬住；"170M 源端到端 `-color_trc` 读回"仍是**未判定**（需 wiring 级判据）。
- **撤回我记错的欠账**：`FindIccTag`（`IccProfileService.cs:506-536`）本来就有线性扫描兜底，注释点名的正是 skcms/Google 产的非升序目录 ⇒ 读者无须修，删升序判据零代价；我给它编的"二分前提"就是那次过度收紧的来源。
- 待办：批次 C/E 两条实现 lane 落地后统一重建 23 份宿主 + 重跑 17 条子集重绑指纹；L-7 与批次 G 仍开。

### 先量再写：三项待做的实测影响面（本轮量完，未动手）
- **⚠ F-1 与 `bench` 门槛互相冲突（我上一批改动带出来的新后果）**：门槛写在 `tests/diagnostics/Modes/bench.cs:147-151`，注释自述其标称口径是「**Release、表驱动快路径**」，对每一格无条件 `Check(single >= 40, …)`。而 F-1 的正确修法恰恰是**让算子激活（含 `EncodeScale≠1` 的 HDR 目标、`DstIsHlg`）时回落到解析路** ⇒ 这类格按定义就到不了 40 MPix/s（实测解析路单核 6.0–9.9）。历史基线里 `FAIL PQ→sRGB 单核 6.5/7.7` 已是同族先兆。**结论**：`bench` 在接进受管清单（L-7）之前，必须先把门槛按"该格的快路径是否被合法关闭"分档——解析路给它自己的地板，表路保留 40；分档依据取自 `TransformSpec` 的算子位，不许凭格子名字猜。当前**未跑 bench 复测**（lane 并发期间禁跑计时），落地前先取一次安静机器的分档读数。
- **L-7 的真实代价（量过）**：`bench` 是 `tests/diagnostics` 这个 **Exe** 的 mode（`Program.cs:50`），不是 `verify-*.ps1` ⇒ 接法属于 `$Probes`（清单 `:43`，被 `Test-RunnerShape` **第 11 针逐字钉住**），而不是 `$scriptList`（`:1040`，条数常量 `$expectedScriptCount = 73` 在 `:1039`，且**第 ⑰ 针**要求"清单散文条数==常量"三种形态各命中）。⇒ 动它=同时改两处被钉字面量 + 运行器散文 ⇒ **`runner-self-sha256` 必变，历史轮"同一把尺"的前提失效**（本仓既有规矩）。另外 `_probe-test-group-coverage.ps1` 也引用条数，而它此刻正被 lane 改 ⇒ 两条必须**串行**，不可同批。
- **`contract` 双拷贝的执行面（已证死）**：`tests/core/Tests.Core.csproj` 是 `<OutputType>Exe</OutputType>`，且 `tests/scripts/*.ps1`、`pack.ps1` 里对 `Tests.Core` **零引用**（正向对照：`verify-gainmap-*` 等确实被引用）⇒ 那份 `tests/core/Modes/contract.cs` 没有任何执行面，本轮 C20 写进去时报"全绿"正是因为没人跑它。处置属**批量删除**，按本仓规矩先给档位再动：①只删重复的 `contract.cs`（保留被执行那份）；②不删、改成 `#if` 关掉并在文件头写明"无执行面，勿在此加断言"；③把它接进 `$Probes`（等于给同一份数学第二个执行者，与"同一份数学只一份实现"的口径相冲）。默认取 **②**（可逆、零删除、把坑写成显式警告），要 **①** 需要单独一句确认。

### 批次 C / L-6 收口（2026-10-11，两条 lane 都撞 150 轮上限中止，由主循环实读 diff + 跑门禁验收）
- **批次 C 已验收**：C lane 留下 5 个源文件 +206/+128/+44/+46/+59 行改动，主循环实读后确认它是**照参照备忘落的**，三条关键决策逐条核过：
  **F-5** `KOffsetSdr/KOffsetHdr` 同进前向（`Log2Gain(hdr, sdr, KOffsetHdr, KOffsetSdr)`、`ComputeGainMapGray(...)`），产物读回 `offsetSDR=0.0156 offsetHDR=0.0156` ⇒ "声明==使用"成立；
  **F-2 依参照保留截断**（未被"顺手改成 +0.5"），并留 C2a 哨兵（一趟口径=截断，锚 `gainmapmath.cpp:770`，改 round 的前置=整体转两趟）；
  **F-3** 分母改用 `sdrStored`，C3b 证明夹具造出"钳前≠交付"（`raw=1.2923/stored=1.0000`，luma `0.2044/0.2126`）；
  **F-4/F-8** AVIF 出口 `gmPixFmt = multiChannel ? "yuv444p" : "gray"`，兜底只在同语义内兜、绝不退 4:2:0；解侧逐通道 min/max/γ/offset 各自施加（C7a `maxΔ=2.57e-4`、C7b 通道差 `ΔGR=0.2595 ΔGB=0.1438`）。
  读数：`verify-gainmap-managed **43/0**`、`verify-gainmap-isobmff **52/0**`、`verify-gainmap-engine **140/0**`（日志 `tests/output/gates-2026-10-11-batchc{,2,3}.log`）。
- **两条红都判为"尺子的取数对象错"，不是产品缺陷**（逐条给证据）：
  ① lane 写的 F-4 AVIF 锁用 `-notmatch 'ffmpeg, "yuv420p"'`，撞上的却是**底图**那趟 `EncodeAvifFileAsync(basePng, …, "yuv420p", …)`——SDR 底图走 4:2:0 合法、不在 F-4 射程内。改成正面钉三件事：成套表达式在、方法内 420 只出现一次且属于 basePng 那趟、`gmPng` 两趟都不得带 420 ⇒ 若谁把增益图改成 420 仍必红。
  ② lane 写的 F-2 锚锁只往 `SimdPixelOps.cs` 的 `LogGainToByteScalar(` **之前** 3000 字符找那两句一手锚，而锚实际写在 `GainMapEncoder.cs:393-395` ⇒ 量错文件。我第一版修的时候**又犯第二个错**：把窗口取在标记之前，而锚在标记的**下两行** ⇒ 仍红；第二版改为"以标记为起点向后取 3000"才转绿。⇒ 教训入账：**"注释即契约"这类锚锁必须把窗口方向也当成判据的一部分，否则尺子会把存在的锚判成被删。**
- **L-6 已落地**（`_probe-test-group-coverage.ps1` +554/−76，主循环实跑）：判据从"全文子串匹配"改成**参数位置上的真实调用关系**，输出 `PASS=18 FAIL=2`——两条 FAIL 是**发现**（fail-closed 设计），逐名点名 8 个零调用点 mode：`bench, bitdepth, gainmap-decode, gainmap-encode, gainmap-struct, gamut, pq, ultrahdr`；两条账目自检 `⑦-a 四档汇总 66 == 组表并集 66`、`⑦-b 逐名点名 66 == 汇总 66` 均 PASS。与 D1 独立结论一致（拆分工程无人跑、bench 无看守）。
- ⚠ 这 8 条里除了 `bench`（L-7，需先按"快路径是否被合法关闭"分档门槛）之外，`gainmap-encode/decode/struct` 三个拆分 Exe 工程是否该接进受管清单属**另一笔基线变更**，与 L-7 同批处理，不在本轮偷偷塞进去。

### 批次 G-1：ColorGainKit 跟随更新（已授权，2026-10-11；未提交）
同步了本仓四项中的**四项**（该库无 `IccProfileService`，故 F-3 不适用）：
- **F-2** `src/ColorMatrix/ColorSpaceRegistry.cs:780` 末项 `1.0633 → 1.0296`（同一转写错误随抽库带过去了）。
- **F-1** `src/ColorMatrix/ColorKernels.cs` 的 `fast` 判据补 `!encScaleOn && !dstHlg`，算子声明上提到判据处复用。
- **F-4** `src/GainMapKit/IsoBmffGainMapProbe.cs` 加 `boxScanIncomplete`（顶层与 meta 子链两处），截断 ⇒ `Determinate=false` + 具名原因。
- **F-10** 新增 `TransferCurve.Bt601`（分段 0.45 式，DNG SDK 一手常数入注释）+ `ForCicpTransfer` 的 `smpte170m/6/bt601` 从 `Gamma22` 改指 `Bt601` + `ExactTransferName` 增 `"smpte170m"/"bt601" => "smpte170m"`。

**四把锁**（`tests/ColorMatrix.SelfTest`、`tests/GainMapKit.SelfTest`）：**ColorMatrix 51/0、GainMapKit 81/0**。L-2 用**外部锚**（zimg 实测的 smpte431→bt709 发布值，容差 1e-5；改回 1.0633 实测 maxErr=2.36e-4 即红）。变异验牙全过：L-1 退回旧判 ⇒ **2 红（27,480 / 9,309 码）**；L-4 退回 fail-open ⇒ **2 红（`det=True` 冒充"确定无"）**；L-2 退回常数 ⇒ 1 红。还原后回到 51/0、81/0。

**我在这侧连踩三个自己的坑，都当场抓住并纠正（照实记，别当没发生）**：
1. 第一次把锁**追加到文件尾**——那文件尾是汇总行 + `return fail…` 之后的不可达区 ⇒ 编译过、计数照旧 43/0、一条不执行。这是"双拷贝/写了没人跑"那一族的**第三次**复发。
2. 搬到"汇总行之前"时，锚 `Console.WriteLine($"===== GainMapKit SelfTest` 在**文件头注释里也有一份** ⇒ 插入落进注释区，同样静默不执行。最终锚定到最后一条真实 `CK(...)` 之后才对。
3. 中途一次切片把注释区之后的真实代码一起删了（`uhdr/grid/solo` 声明丢失、编译报 CS0841）⇒ `git checkout` 恢复该文件，改为**只插入、零删除**。
⇒ 教训入形：**在这个仓系里新增断言，必须验证"新断言的标签出现在输出里"**；计数没变就是没跑，"0 错误 + exit 0"完全不等于执行。

### F-9 落地状态纠正 + 一条稳定的内存红（2026-10-11，主循环自查）
- **差点把"没接上"的 F-9 报成已验证**：`git apply -3` 在 CSR 失败时是**整体原子回滚**，我随后只用 `patch` 补了 CSR ⇒ `Decision.cs`/`QueueProcessor.cs` 两个调用点其实没改，而拆式参数**带默认值 ⇒ 编译照过、行为不变**。已补打这两个文件（+42/−6、+13/−2，`Decision.cs:342-347` 实调 `NeedsDefinitionExit`/`BuildDefinitionExitTail`），`DLL = 31904497E0921918`。**教训**：给公共方法加"带默认值的参数"时，`git apply` 的原子回滚会留下"定义在、调用点不在"的假落地——验收要看**调用点 diff 是否存在**，不能只看编译与汇总行。
- 那轮 16/17 全绿的归属是 **`DD68A0FD51FC19E7`（未接线版）** ⇒ 它**不覆盖 F-9**，只覆盖 F-3/批次 C/F-10。F-9 接线后必须重跑。
- **我取直证时选错了格子**：HDR 源 + `--color-space BT.2020` 在 legacy 下走的是 `-vf iccgen`（ICC 路线），根本没进 `tmDstT` 那条 tonemap 链 ⇒ 不能拿它当 F-9 的证据。两臂的 `[png3] 已写入 cICP（primaries=9, transfer=1 ← bt2020/bt709）` 至少独立证明了**出口标签未动**这一半。正确取法照 `f9-change-surface.md` §1 的格 A/C/D 组合（HDR 源 + 显式 BT.709 类目标 + 非 manual + 容器 ∈ png/avif/jxl/jxr，且必须避开被 `capT` 钳走的 jpg/webp/tiff）。
- **`verify-gainmap-memory` 的 FAIL 是稳定实测、不是噪声**：三次交错复跑 **817.5 / 818.8 / 817.3 MB** vs 判据的"声明预留 768MB"。批次 C 之前同一门禁是 5/0（实测 ≤768）⇒ 归因到 **F-4/F-8 让多通道增益图真的交付 3 平面（yuv444p/444）**，峰值随之上移。这是**内存钳制的声明值落后于实现**（`GainMapKit` 侧同一改动同样受影响）。处置方向=按实测把声明预留提到水位（并给并发钳制加读数），**不是**放宽判据；需要单独一批，且要顺带确认 `-j` 高并发下的 OOM 边界。

### F-9 后续：xcheck 参照臂修完，**部分推翻"不可修口径分歧"这个旧定案**（2026-10-11）
- 改法（保持参照独立性）：出口 trc 为 `bt709` 时，参照臂 = `zscale t=linear`（色度学仍归 zimg）+ `lutrgb=BT.709-6 定义式`，常数逐字取自本机 `dng_color_space.h:134-140`（α=1.0992968268094429、β=0.0180539685108078、趾段 4.5、γ=0.45），**不经本仓曲线代码**。
- 读数：`sRGB→BT.2020` 两格由"豁免红"变为真通过 —— **71.85dB / 66.12dB，maxDiff 77 / 108**。⇒ 旧结论"zimg 参照口径分歧、不是引擎缺陷、不要试图修绿"里，**出口侧那部分是错的**：真正不合规的是被当标准用的 zimg `t=bt709`（纯幂 γ2.4）。
- 已摘掉因此变成**死豁免**的 2 个键（`bars16.png/sRGB→BT.2020`、`t2.png/sRGB→BT.2020`），并在脚本里写明撤销依据。⚠ 收益要说准：这两格当时已经通过，摘键的效果是**今后它们真回归会红**，不是"把红修绿"。
- 仍豁免 4 格，其中 2 格 `BT.2020→sRGB`（PSNR 25.48 / 35.55，ΔE max 13.9 / 20.6）的分歧已**重新定位到入口侧** `tin=bt709`（zimg 按 γ2.4 解码），修法与出口侧同形（入口用定义式反解 + `tin=linear`）；另 2 格 PQ 的理由本就不是 bt709（未接 tonemap 的高光硬裁切），保留。
- 编译踩坑记录（同一类错误第三次）：Python 往 C# 字符串里写 `\,` 时被吃掉一层反斜杠 ⇒ CS1009；最终按行号整行重写并**当场核反斜杠个数**才过。**转义层级只要跨语言就必须用显式字符（`chr(92)`），不能靠字符串里数反斜杠。**

### 步骤①完成：xcheck 豁免 6 → 2（2026-10-11，入口侧同形修）
- `BT.2020→sRGB` 两格的分歧确在**入口反解**：参照臂改成「自己按 BT.709-6 定义式反解（`lutrgb`，编码域拐点 4.5×β=0.0812428582986351、α−1=0.0992968268094429，常数同 DNG SDK）+ `zscale tin=linear`」⇒ 实测 **72.82dB / 71.92dB（maxDiff 49 / 498，ΔE max 0.670 / 1.370）**，两格真通过。
- 已摘掉那 2 个失效键并写明依据；`verify-ps-compat 13/0`（改了门禁脚本当场跑，按本仓规矩）。
- **累计**：出口侧 2 格 + 入口侧 2 格 = 原 6 格豁免里的 4 格，被证明是**参照实现不合规**（把 zimg 的 γ2.4 当成 BT.709 标准），不是引擎缺陷，也不是"不可修口径分歧"。剩 2 格 = PQ 无 tonemap 的高光硬裁切约定差异（理由文本已是实情，保留）。
- 修的过程中我又犯了一次老毛病（`DefInv` 用在声明之前，CS0841）与一次"assert 失败导致整段未写入却像已生效"（那次 xcheck 显示的 `豁免 2 格` 其实是"键还在、只有 2 格真失败"）⇒ 都以复跑纠正，**摘键必须看运行器给的豁免计数变化**。
- 产品自 `31904497E0921918` 归属轮之后未再改动（只动 `tests/`）⇒ 那轮 16/17 对产品仍成立，其中两条红：`decision` 已由取数阶段修正转 `4/0`；`verify-gainmap-memory` 的 817MB vs 声明 768MB 仍开（=步骤②）。

### 步骤②完成：每任务内存预留 768 → **1152**（2026-10-11，按实测提水位，不给判据放水）
- **取数口径**与 `verify-gainmap-memory.ps1` 逐字同夹具（testsrc 生成 16-bit `rgb48le` PNG → `ServiceProbe png3 … 0 9 16` 打 cICP=9/16 → 采本进程 `WorkingSet64`、40ms 间隔、Release）。脚本 `tests/output/step2/gm-mem-arms.ps1`。
- **单任务实测**：1080p(2.07MP) **263.5** / 4K(8.29MP) **818.4**（本轮三次 817.2/817.2/818.4；上一轮 817.5/818.8/817.3 ⇒ 稳定）/ 8K(7680×4320, 33.2MP) **3363.1** MB ⇒ 拟合 ≈ **70MB + 99MB/百万像素**（批次 C 前是 65 + 74/MP ⇒ 每像素 +15~25B，归因到 F-4/F-8 让多通道增益图**真的**交付 3 平面）。普通通路 4K 复测 **152.3~157.0** MB（四次读数）⇒ `plainMB=192` 未动、仍够。
- **并发夹逼 = 这一步的真正读数**：用 `DOTNET_GCHeapHardLimit` 把可用内存钉成固定夹具；先证明该变量确实改变**被进程**看到的 `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`（32380 → 1536 实测跟随），而它正是 `QueueProcessor.ClampConcurrencyByMemory` 唯一的操作数。8 个 4K 增益图任务，每任务实际分到：
  | 每任务可得 | 产物 | 现象 |
  |---|---|---|
  | 768MB（C=3 @ L=2304） | **6/8** | `System.OutOfMemoryException` |
  | 896MB（C=2 @ L=1792） | **7/8** | 同上 |
  | 1024MB（C=3 @ L=3072） | **8/8** | 通过 |
  | 1152MB（C=2 @ L=2304） | **8/8** | 峰值 2329MB |
  ⇒ 硬底线落在 **(896, 1024]**；取 **1152 = 通过点 1024 × 1.125**（= 单任务实测 818.4 的 1.41 倍）。失败形态是逐任务捕获 + 退出码 92 + 日志点名，**不静默**。
- **本机代价 = 0**：`availableMB/linearMB` 由 42 降到 28，而请求并发上限本就是 `-j` 默认（核数 20）⇒ 钳制不再是约束项；小内存机器（8GB）由 10→8 并发，而这正是实测放得下的数目。
- ⚠ **本机的收益也不是"防住了 OOM"**，要说清：`ClampConcurrencyByMemory` 除的是 `TotalAvailableMemoryBytes`（**总量**，实测 = 32380MB），而同时测到 **freePhysical 只有 9296MB** ⇒ 本机 768 与 1152 两档都**从不生效**（42/28 都 > 请求的 20）。这一步修的是**声明与现实的偏差**（判据 A/D），不是"消除了一次本机会发生的 OOM"。**按总量除**这件事本身是另一条设计缺陷（审计 lane C2/V-C2 已点名"CPU 轴仍缺"，本轮补上实测数：20 个增益图任务按实测 818MB/任务 = 16.4GB > 当时空闲 9.3GB），改它 = 换准入口径、会动并发行为 ⇒ 登记进建议、等单独裁决。
- **进程峰值随并发饱和**：C=1/2/4/6/8 ⇒ 0.82 / 3.72 / 4.47 / 4.97 / 5.35 GB ⇒ 平坦除数在低并发侧**不足**、在高并发侧**过度保守**，属**模型**局限而非数值问题。两条已知缺口已按实测写进方法注释（① Ultra HDR 输入无入队期标志；② 常数不随像素缩放 ⇒ 8K 实测 3363MB = 预留的 2.9 倍，而 7680 是产品自己允许的最长边）。**根治 = 按源像素数做准入**，与"入队期不做文件嗅探"这条既有设计约束冲突 ⇒ 登记成建议，步骤②不擅自改架构。
- **新判据（断言 D）与它的控制臂**：L=**2304** 是写死的常数，**不从 `linearMB` 派生** —— 若 L=k×linearMB，常数一改夹具跟着缩放、钳制选出的并发数永远不变 ⇒ 断言退化成恒真（本仓"构造恒等"陷阱）。D 请求 `-j 8` ⇒ 要求 8 份产物齐；D-ctrl 请求 `-j 2` ⇒ 恒应 8/8，使"缺产物"只能归因到**超量准入**而不是夹具坏了。
- **变异验牙**：`linearMB` 退回 768 重建 ⇒ `FAIL A`（768 < 817.8）+ `FAIL D`（C=3、产物 7/8、OOM 命中=True），D-ctrl 在两态都绿 = 合格对照；还原 1152 ⇒ 该门禁 **PASS=7 FAIL=0**。改过门禁脚本当场跑 `verify-ps-compat 13/0`。
- 环境坑（我自己犯的，已修并记进记忆）：`py` 文本模式读 + `newline=''` 写 ⇒ 把 `QueueProcessor.cs` 整文件 CRLF 换成 LF（git 里看不出、`warning: LF will be replaced by CRLF` 才暴露）⇒ 恢复只能 `rb`/`wb` 逐字节转；`git diff --numstat` 回到 **41/14** 才算干净。另一条：bash 里 **`*>` 会被当 glob** 展开成 cwd 的文件名灌给 pwsh ⇒ 运行器把 `CHANGELOG.md` 绑到 `[int]$ProbeTimeoutSec` 直接拒跑（日志 0 字节、exit 1），改用 bash 重定向 `> file 2>&1`。
- 重建与归属：本步是 `31904497E0921918` 之后**第一次产品码变动** ⇒ 22 份宿主逐个 `-t:Rebuild`（`dotnet build ffmpegPictureUI.sln -t:Rebuild` **不够**，solution 只登记 4 个工程，实测另 19 份宿主仍带旧 dll），23 份 `FfmpegGui.dll` 逐份一致 = **`022f928195756f09`**；17 条子集重绑轮 = `tests/output/gates-2026-10-11-step2.log`，**读数见下一节回填**。

### 步骤②收尾读数（17 条子集重绑轮，绑 `022F928195756F09`）
- **17 条脚本逐条 `exit=0`**：`wiring 142/0`(100.6 s) · `strategy 78/0`(205 s) · `decision-delivery pass=87 fail=0 skip=0`(92.1 s) · `caps 16/0` · `prophoto-faithful 27/0` · `gainmap-engine 140/0` · `gainmap-managed 43/0`（内含进程内 `CHECKS=34 pass=34`）· `gainmap-isobmff 52/0`（含 §3c HEIC hvc1 点名 + 负控 + "被测 exe 未中途重建"）· **`gainmap-memory 7/0`(49.6 s，本轮含新断言 D)** · `gamut-map 36/0` · `color-token-normalize 33/0` · `png3-interop 16/0` · `color-tonemap 15/0` · `colorfmt-matrix 2/0 WARN=4`（表格型，WARN≤6 不计 FAIL）· **`engine-xcheck 全部通过（白名单豁免 2 格）**（步骤①从 6 格降到 2 格，本轮第一次以 2 格过）· `simd-switch 17/0+①b 28/0`。
- `shape-selfcheck=OK（17 针…）`、`runner-self-sha256=05AE64C1617CCBC5 lines=1359`（起/收尾逐位一致 ⇒ 轮内运行器未动）、无 `.gates.lock` 残留。
- 探针段我这次**没传 `-Probes`**（上一批 fixa/fixb 传的是 16 mode 子集）⇒ 跑了默认 **26 mode**，`25 绿 + 1 红`：`contract 139/0`、`decision 4/0`（宣称未落实 0 格）、`colormath 19/0`、`iccname 17/0`、`engine 35/0`、`avifgm 14/0`、`geometry 52/0`、`thumbnail 40/0`…
- **唯一红 = `probe:runner` `pass=7 fail=1`（"静默但正常退出的子进程不被误杀"，详情 `target is not ffmpeg (cmd.exe)`）= 既有环境假红，不是本轮引入**。三条独立证据：① 与 beta7 三轮逐字同读数（`gates-round-beta7.log:30`、`gates-round-beta7-redirect.log:22`、`jpegli-prog/round_beta7_console.log:31` 全是 `7/1, 8.7 s`）；② 2026-10-07 已定根因（沙箱下 `cmd /c ping` 回 rc=1「PING: transmit failed」，而同机 `cmd /c ver`/`echo` rc=0 ⇒ 挡在 loopback 不在探针逻辑），当期结论 **"不为它改探针或产品"**；③ 非沙箱轮（`t51/round56-console.log`、09-2x 各轮）它是 `pass=8 fail=0`。⇒ 登记为已知环境红，本轮**未**为它动任何文件。
- ⚠ 流程教训一条：**子集轮必须显式传 `-Probes`**，否则默认 26 mode 会带进一条与本次改动无关的既有环境红，读数口径就与上一批（16 mode）不可比，看起来像"新增红"。本轮已按 26 mode 的口径如实记下。

## 步骤③（L-7）：bench 接进受管探针清单 + 吞吐阈值按"快路径是否被合法关闭"分档（2026-10-12）
**做前读数**（静机，54 s）：`pass=9 fail=2`，两条红都是
`PQ→sRGB(变曲线+裁切) 单核 ≥40MPix/s（实 5.2 / 5.5）`，同一格自打的"快路径增益 = 1.0×"就是原因：
⚠ **这条读数的日志文件 `tests/output/l7-bench-1.log` 后来被同名复跑覆盖了**（我做 3 连跑稳定性时用同一个
`$i` 命名，把第一份"做前态"冲掉了）⇒ 此处的 `9/2` 与 5.2/5.5 来自当场 console 回显，不作盘上可复核。
可复核的等价证据是任何一份现存 bench 日志里 PQ 格的 `single` 读数（5.6~6.7 MPix/s）：旧判据 `≥40` 套在它身上
**恒红**，这是算术事实，不需要那份被覆盖的日志。
PQ 源 ⇒ `LinearScale = 10000/203 = 49.26 ≠ 1` ⇒ F-1 之后查表快路径**必须**关掉 ⇒ 走解析式是**正确行为**，
而阈值拿 SDR 格的 40 去套它 ⇒ 那格**永远**红 ⇒ 整条 bench 被当成"已知假红"，于是性能账事实上无人看守
（bench 也不在任何受管清单里，唯一调用点是非受管的 `run-thread-tests.ps1`）。

**改动四处**
1. `ColorKernels.TableFastPathOperatorsAllow(spec)` —— 把内核 48-bit 主路径的 `fast` 判据抽成一个公开静态方法，
   内核自己改调它（`bool fast = eotf != null && oetf != null && TableFastPathOperatorsAllow(spec)`，行为逐字不变）。
   注释里写明：**别拿它去替换 8-bit 兄弟出口那一处**（那处循环能在查表后仍执行 GMO，所以 GMO 不关表，条件刻意不同）。
2. `bench` 接进运行器**默认探针清单**（`$Probes` 26 → 27 mode）。⇒ 走的是探针段、**没动 73 条受管基线**
   （计划文档原写"进 `$scriptList`"，实测不必：多包一层 `.ps1` 只是给同一个宿主再加一层解析，
   且会连带动第 ⑬/⑭/⑰ 三针。这条偏离如实登记在这里，不当作"按原计划完成"。）
3. `ProbeTimeoutSec` **120 → 180** + 第 ⑪ 针的钉值同步。依据 = 本段自己的规矩"max × ≈3 向上取整"：
   bench 实测 54 s 成为新的最慢项（旧 max `geometry` 33.8 s），120/54 = 2.2× ⇒ 重新结算 54×3=162 ⇒ 180。
   ⚠ 抬的是**上限**不是判据。顺手抓到一条旧漂移：第 ⑫ 针的散文仍写 `= 480`，实际 2026-10-02 已重标成 900
   ⇒ 现量陈述与常量不符（`Test-RunnerShape` 只管常量只管不到散文这句话），已改为 900 并注明是漏改。
4. 阈值**分档**（两份拷贝同步：被执行侧 `ServiceProbe` + `tests/diagnostics/Modes/bench.cs`）：
   快路径适用 ⇒ ≥40；快路径被合法关闭 ⇒ ≥3（解析式水位）。加两条自证：
   `独立分档 == 内核判据` 的对账断言；分档与**实测增益**一致（适用档 ≥2×、关闭档 ≤1.3×）。

**我自己先写错的一版，被变异当场打死（这条是本步最值得记的）**
第一版分档直接调 `CK.TableFastPathOperatorsAllow(spec)` 取档 ⇒ 变异 M1（判据整支关表）实测
`pass=16 fail=1`，唯一那条红是**无关**的端到端项：内核一关表，分档跟着一起改口，10× 的吞吐回落被
"重新分档"吸收成合法低档 ⇒ **无声放过**。这正是本仓"构造恒等 vs 独立参照"那条陷阱的又一实例。
⇒ 分档改成从**这一格声明的色度学**推（曲线归一域峰值：PQ=10000 / HLG=1000 / 其余=203 ⇒ 两侧都是
SDR 参考白才该走表），换算与生产 `ColorTransformPlan.ToSpec()` 同口径但在探针里**独立写一份**当对照臂。

**验牙读数**（`tests/output/l7-mut-M1.log` / `l7-mut-M2.log`，两态都 `0 警告 0 错误`、终态 MUTATION 残留 0）
- 基线（还原后）：**`pass=23 fail=0`**（原 9/2 ⇒ 新增 6 条对账 + 6 条分档增益，全绿）。
- M1（内核判据整支关表）：**`pass=11 fail=12`** ⇒ 每格三连红：对账（独立=True／内核=False）、
  `单核 ≥40（快路径适用，实 6.2~8.5）`、`增益 ≥2×（实 1.0×）`。
- M2（内核判据不再拒绝 nits 系数 ⇒ HDR 格错误进快路径、少乘 49×）：**`pass=19 fail=4`** ⇒
  对账（独立=False／内核=True）+ `关闭档增益必须 ≈1×（实 11.8× / 10.4×）`；像素侧另有 `contract C20` 逐字节锁兜住。

**同批抓到的性能事实（只登记，不动手）**
- HDR→SDR 格 5.5 MPix/s 不是天花板：`LinearScale` 是**常数**，可以折进 EOTF 表 ⇒ 全 HDR 格免费 ≈10×。
  这条比清单里的 S0（表缓存）更值钱，且与 F-1 的修复不冲突（折叠后仍必须走同一条判据）。
- GainMap 批实测**确实**随 `-j` 变（8 份 4K：`-j 2/4/6/8` = 17.7/10.9/11.7/12.9 s，峰值 3.72/4.47/4.97/5.35 GB）
  ⇒ 否证 C2/V-C2 的"改 `-j` 看吞吐在 GainMap 批必然不红"；成立的是另一半：`GainMapEncoder.cs` 内部无
  `Parallel.`/`Dop` ⇒ **单图延迟没有旋钮**。已把更正写进 `docs/PIPELINE_AUDIT_ADVISORY_2026-10-11.md` §2 与 §4。

**步骤③收尾读数（绑 `97DFCB9A8E2467AF`）**
- 归属轮 `tests/output/gates-2026-10-12-l7b.log`：**「运行器汇总：全部通过」**，17 条脚本逐条 `exit=0` +
  探针 25 mode 全绿（`bench 23/0 @39.2s`、`gainmap-memory 7/0 @48.4s`、`xcheck 全过（豁免 2 格）`、
  `wiring 142/0`、`strategy 78/0`、`decision-delivery 87/0`、`isobmff 52/0`、`engine 140/0`…）；
  `shape-selfcheck=OK（17 针）`（新的 ⑪=180 与 ⑫ 散文纠正都在这一针下过）、
  **`runner-self-sha256=1C785039CDA1A86C lines=1364 bytes=130109`**（旧值 `05AE64C1617CCBC5 lines=1359 bytes=129322`）
  ⇒ ⚠ **运行器这把尺换了**：历史轮"同一把尺"的前提自本批起失效，跨轮比数要带上这个 sha；无 `.gates.lock` 残留。
- ⚠ 口径如实记：这一轮我显式传的 `-Probes` 是 **25 个** —— 刻意去掉既有环境红 `runner`（下面单独补跑），
  另外**漏了** `selftest`。补跑 `tests/output/l7-补两个mode.log`：`selftest pass=40 fail=0 skip=1`、
  `runner pass=7 fail=1`（与 beta7 三轮逐字同读数，仍是 `cmd /c ping` 的沙箱假红，见 2026-10-07 定性）⇒
  默认清单 27 mode 的完整口径 = **26 绿 + 1 条既有环境红**。
- `_probe-test-group-coverage`：**`PASS=18 FAIL=2`**，零调用点由 **8 → 7**（`bench` 进档①，`$Probes` 解析到 27），
  剩余点名：`bitdepth`、`gainmap-decode`、`gainmap-encode`、`gainmap-struct`、`gamut`、`pq`、`ultrahdr`；
  档③ 仍 1 条（`icc`，唯一真调用点在非清单的 `ensure-fixtures.ps1:L97`）⇒ **这条守卫仍按设计保持红**。
  它头部两处现量陈述（"26 个"、"零调用点 8 个"）已同步；改过门禁脚本当场跑 `verify-ps-compat 13/0`。
- 端到端那条 `H1 ≤ H2×2.0` 在 bench 变成受管**之后**才暴露出自己的问题：同一构建、同一台机器，
  旧写法（先 3 次 H2 再 3 次 H1）五跑 = **1.68/1.88/1.96/1.97/2.00**，带并发活动时两跑 = **2.09/2.53**
  ⇒ 判据贴在分布边缘，红不红取决于当时谁在动机器。根因是**分段取样**：单向漂移整笔落到一条臂上，而比值是两臂相除。
  改成**交错 5 轮**（每轮 H2→H1 各一次，各取 min）后五跑 = **1.83/1.87/1.89/1.90/1.90**（散布 0.85×→0.07×）。
  **阈值没动**。剩下的是一条事实陈述：H1 引擎端到端 ≈ zscale 单进程的 1.9 倍（三进程 + 整帧内存变换），
  要压它走性能清单 S0/表折叠，不许回头调这条判据。
- ⚠ 我自己两次踩到的 harness 坑（都写进记忆）：① 后台命令尾巴上 `echo "RC=$?"` 会把通知里的退出码顶成 echo 的 0
  ⇒ 上一轮明明「失败 1 项」而通知报"completed (exit code 0)"，判成败必须读日志自己的汇总行；
  ② 把验收脚本 `| head -8` 会 SIGPIPE 杀掉它，只留下第一行输出（本仓第 150 行那条的同族，我又犯一次）。

**剩余（步骤③之后仍开）**：~~全量 73 条宣布新基线~~（**已做，见文末「① 收口」，绑 `A95CCD5F80837E76`**）；
~~`verify-ui-split-union` 里 `H10` 的 `Pump(6)` 换成确定性 settle~~（**同日已修**：交错 A/B 13/48→0/48，
见《H10 的固定时长等待换成确定性收敛》）；**新查出同形状但不同类的一条**：`ui-gainmap-toggle`（组 O）
轮内红 6 条、单跑 8/8 绿 ⇒ 判"读在重建中间窗口"还是"产品真冲掉选项"的实验已设计好，单列一批；
`bench` 40 档静机最小项 45.0 ⇒ 余量 11%，
负载下假红只能静机复跑（已写进门禁注释）；`tests/core`+`tests/diagnostics` 的断言双拷贝（批次 G，#13）；
`F-8` 端到端量级；`L-3b` 正样本 `test4.icc`（**本机两份 lcms checkout 里都没有**，只有 test0/1/2/3/5 ⇒ 那条 SKIP 是真的）；
ColorGainKit 未提交（现已提交，见下）。

## 性能提案 (a)：解码侧 nits 缩放接进表快路径（2026-10-12，实测把方案砍掉一半）
**原提案**是"把 `LinearScale` 折进 EOTF 表 ⇒ 全 HDR 格免费 ≈10×"。落地前先量，结果**两处被否证、一处被证明**：
1. **不用折表**：解析分支本来就是「查表得 float ⇒ 乘 `LinearScale` ⇒ 矩阵 ⇒ 乘 `EncodeScale` ⇒ `EncodeLerp`」
   ⇒ 乘法直接进快循环就是同运算、同顺序、同舍入；折表反而有害（`Oetf` 表按**线性域均匀采样**，
   编码侧折表等于换插值基）。
2. **编码侧不能走表**（实测，不是推理）：SDR→PQ 的表路径 vs 解析式最大差 **1457 码@16bit**
   （首个不同在码位 3）、8-bit 出口 **6 码** ⇒ 不是 1 LSB 舍入，是 PQ 编码曲线近黑极陡而采样均匀
   ⇒ **保持关表**；要解它需要先做"编码域均匀采样的第二张表"（未做，已登记）。
3. **解码侧可以**：PQ→sRGB / PQ→P3 / PQ→BT.2020-SDR 三格走表后与解析式差 **≤1 码@16bit**，
   而 **8-bit 交付位深上差 0**（`C20-8b` 量到）⇒ 与既有 SDR 快路径同量级，不是新增的精度损失。

**收益（bench，静机多跑）**：`PQ→sRGB` 单核 **5.5 → 117.0 / 154.5 / 163.8 MPix/s（21~30×）**，
多核最高 **1099.6 MPix/s**；`sRGB→PQ` 仍解析式 **4.2 MPix/s**（低档阈值 3，余量 28%）。

**C20 从"构造恒等"升级成真比较（本轮自查又抓到一条）**：改动前 `EncodeScale≠1` / `DstIsHlg` 两格
`fast=false` ⇒ 送表与不送表**两支都走解析式** ⇒ 比出的 `maxDiff=0` 什么也没证。现在 7 格 × 两条断言
（「快路径开关须与声明一致」+「差值须在容差内」），`contract 153/0`；两份拷贝（被执行侧
`ServiceProbe/Program.cs` + `tests/core/Modes/contract.cs`）同格同步。
**变异**：M3 删掉快循环里的解码乘法 ⇒ 三格红、最大差 **55,508 码**（≈85% 满量程，正是 F-1 那一类）；
M4 放开编码侧关表条件 ⇒ 对账红 + 最大差 **27,480 码**。终态 MUTATION 残留 0、构建 0 警告 0 错误。

**bench 补低档活臂**：收窄后三格全落"≥40"档 ⇒ "≥3"档没有格子经过、它对账臂永远不会红 ⇒
加第四格 `sRGB→PQ(目标域换算 ⇒ 关表)` ⇒ 两档各有活臂，`bench 30/0`（原 23/0）。
⇒ 例外规则一条：**阈值分档体系被收窄后，每一档都必须还有至少一个活格子**，否则那档的对账是死码。

**归属**：产品码改动 ⇒ 23 枚宿主统一到 **`B7F816D097A5FA43`**；色彩链 19 条脚本 + 23 mode 探针轮
**「运行器汇总：全部通过」**（`tests/output/gates-2026-10-12-p2a.log`；`contract 153/0`、`bench 30/0@57.2s`、
`_probe-matrix-structure` 重钉后 `MATRIX 38/0`、xcheck 全过豁免 2 格、`wiring 142/0`、`gainmap-memory 7/0`）；
运行器这把尺仍是 `1C785039CDA1A86C`（本轮未动运行器）。

**性能提案 (b) 的接缝（已定位，未动手）**：大头不在 `RawColorPipeline`（>160MB 已有流式 L 档兜住），
而在 `GainMapEncoder.EncodeAsync:142-186` 一次租 4 份 `float[pixelCount*4]`（≈64B/px ⇒ 8K 就是 2.1GB，
正是实测 3363MB 的来源）。准入门应放在那三行租 buffer 之前，需求用实测拟合 `70MB + 99MB/MP`，
空闲物理内存取 `GlobalMemoryStatusEx`（本仓首次 P/Invoke，全仓现 0 处；win-x64-only 可接受），
**有上限地等、超时后响亮点名继续** ⇒ 不新增死锁面，只把"静默 OOM"换成"可见的等待"。

## 性能提案 (b) 落地：大帧驻留路径的内存准入（2026-10-12）

**做了什么**
- 新文件 `src/FfmpegGui/Services/MemoryAdmission.cs`：需求 `EstimateMb(pixels) = 70MB + 99MB/百万像素`
  （三点实测拟合：1080p 263.5 / 4K 818.4 / 8K 3363.1 MB）；空闲量取 `GlobalMemoryStatusEx.ullAvailPhys`
  （本仓首次 P/Invoke）；不够就**有上限地等**（默认 60s、500ms 轮询）；超时后**响亮点名继续**。
  取不到读数（非 Windows / API 失败 / 读数不自洽 `0<avail≤total ∧ load≤100`）⇒ 返回 false ⇒ 调用方**不拦**：
  宁可没这道门，也不拿一个假读数去放行或拦住任务。
- **接缝两处，同一道门**（这是本轮自查补的一条，见下"接缝位置的一处纠正"）：
  `RawColorPipeline.cs:312`（引擎出口的 GainMap 整帧解码之前，传 `(long)w * h`）与
  `QueueProcessor.cs:2708` 的 GainMap **回退管线**（`int pixelCount = width * height;` 之后、整帧
  `ReadAllBytesAsync` 之前，传 `(long)width * height`）。

**为什么不放在 `EstimatePerTaskMemoryMB`**：那里是**不随像素缩放**的平坦除数（设计约束：只读入队期已知的选项标志、
不做文件嗅探），而这两个接缝处 `w×h` 是**已知的** ⇒ 估算能落到本帧真正要占的量上。
⇒ 步骤② 记的两条缺口里，「平坦除数 vs 8K=3363MB ⇒ 2.9× 欠预留」这一条由 (b) 关闭；
「`ClampConcurrencyByMemory` 用的是 `TotalAvailableMemoryBytes`（**总量**，本机 32380MB）而非空闲量 ⇒ 在本机永不生效」
仍开着 —— 按像素/压力准入就是那条根修的第一步。

**为什么是"等"而不是"拒"**：用户政策「能不拒绝就不拒绝，只警告」；等待有上限 ⇒ 两个互相等对方释放内存的任务
不可能永久挂住；真 OOM 仍由逐任务捕获报出（(b) 只是把静默换成可见）。

**判据与变异（十支，逐支只红它该红的那条）**
探针 `ServiceProbe memadmit`（① 读数自洽 + ② 三点拟合 ≤15% ⇒ `pass=4 fail=0`，
`tests/output/memadmit-1.log`）与 `memadmit pressure`（P-1..P-5 ⇒ `pass=5 fail=0`，同目录 `memadmit-pressure-1.log`）。
门禁 `verify-gainmap-memory.ps1` 默认臂 **16/0 SKIP=1**（`gmmem-F5-1.log`），显式 `GMMEM_HEAVY=1` 时 **20/0 SKIP=0**
（`gmmem-heavy-2.log`）。变异逐支读数在 `tests/output/mut-MUT*.log`：

| 变异 | 改了什么 | 期望转红 | 实读 |
|---|---|---|---|
| MUT1 | 总物理量读 `ullTotalVirtual` | E-2 | 产品自洽判据 ① 与 P-1..5 **全绿**，门禁 `15/1` 只红 E-2（差 134,185,348MB）|
| MUT2 | 空闲量读 `ullAvailPageFile` | E-3 | ① 仍绿，红 E-3（产品 14415MB vs CIM 9589MB，容差 2,397MB）|
| MUT5 | `PerMpMB 99 → 60` | ② | ② 三点全红（偏差 26.2 / 30.6 / 38.7%），门禁 `13/1` |
| MUT3 | 永远判「够」⇒ 不等不点名 | P-1/P-2/P-3 | 压力臂 `pass=2 fail=3` |
| MUT4b | 删掉「够就静默返回」 | P-4 | P-4 红（实发 2 条）|
| MUT6 | 摘掉**引擎出口**调用点 | F-1/F-2/F-3 | `13/3`，F-4/F-5/F-6 仍绿 |
| MUT7 | 引擎出口需求量写成常量 | F-2 | `15/1` 只红 F-2 |
| MUT8 | 摘掉**回退管线**调用点 | F-5/F-6 | `14/2` |
| MUT9 | 回退管线需求量写成常量 | F-5 | `15/1`，F-6 绿 |
| MUT10 | 把回退管线的调用点挪到整帧读入**之后** | F-6 | `15/1`，F-5 绿 |

⇒ **E-2/E-3 的存在理由被 MUT1/MUT2 直接量出来了**：结构体读错字段时产品自己的自洽判据**放行**，
只有第二个独立读者（CIM `Win32_ComputerSystem` / `Win32_OperatingSystem`）抓得到。
⇒ F 六支是**源形锁**：变异只改源文本、不重建（重建与这条判据无关）⇒ 同轮 A–D/E 臂测的还是上一版 dll，
不参与该判读；MUT10 是 F-6 的**独牙**证明（顺序错了 ⇒ F-5 绿而 F-6 红）。

**真机行为证据（重臂，各吃十几 GB）**：G-2 引擎路 8K×4 全产出、峰值 10114.4MB ≤ 有界 16142MB；
**G-3** 起臂前空闲 9542MB < 4 帧实测需求 13452MB 而日志确实有 `[MemoryAdmission]` 点名 ⇒ 「需求超空闲时这道门真的拦过」；
**G-4** legacy 路 8K×4 全产出、峰值 11297.7MB ≤ 16142MB（起臂前空闲 11408MB < 需求，点名=True）。
G-3/G-4 都是**析取式**：另一支「起臂前空闲本就够 4 帧」是诚实出口（硬要求点名 = 空闲机器上恒红）；
需求量用**实测 3363MB × 4**，不用产品自己的拟合常数（判据从被检量派生 = 没检）。

**接缝位置的一处纠正**：(a) 段末尾我预判「门应放在 `GainMapEncoder.EncodeAsync:142-186` 那三行租 buffer 之前」——
落地时否证：`EncodeAsync` 收到的 `pixels` **已经是调用方分配好的整帧**，门装在那儿只拦得住第 4 份之后的量。
⇒ 真实落点是两个**上游调用方**在整帧解码/读入之前。同一次落地还查出覆盖缺口：只给引擎出口装门 ⇒
`--color-engine legacy` 或「引擎不可走」（增益图 + 最长边缩放）时轮到 `QueueProcessor` 那条专用管线，**它没门**
（本仓老教训的同族：两条入口只改了一条，`GainMapJpegCodec` 的存在理由就是它）。

**自查抓到的一处文案夸大**：`MemoryAdmission` 文档注释原写「三点各自偏差 ≤6%」，实跑是 **4.5% / 8.9% / 0.3%**
（判据上限 15%）⇒ 已改成实测数并指到 `memadmit` ②。

**没做到的（登记）**
- 准入只管「整帧租 buffer 之前」这两处；`BufferPool`/LOH 侧、`GainMapDecoder` 的整帧读入没有接。
- G 臂默认 SKIP ⇒ 每轮整轮只有 E/F 的 16 条在跑；「会等 / 会点名 / 会放行」的完整路径只在显式重臂里有牙。
- 等待预算 60s 是拍的（没量过「真等多久才等得到」）；预算到了仍继续 ⇒ 极端并发下照样可能 OOM，只是不再静默。
- legacy 那条路只有一条析取判据背书（G-4），它需要显式开关才跑。

**归属**：产品码改动（`MemoryAdmission.cs` 新增 + `RawColorPipeline.cs` + `QueueProcessor.cs`）⇒ 逐工程 `-t:Rebuild`
后 **23 枚 `FfmpegGui.dll` 同为 `A95CCD5F80837E76`**（`build-failures=0`）。

## ① 收口：宣布新基线（2026-10-12，绑 `A95CCD5F80837E76`）

**这一轮**：`tests/output/gates-round-2026-10-12-full5.log`，73 条受管脚本 + 27 mode 探针，
**脚本 71 绿 / 3 红**（两条命名豁免 + 一条本轮完成归因的抖动），**探针 754 pass / 1 fail**（那 1 条就是命名豁免 ①）。
尺与件：`runner-self-sha256=1C785039CDA1A86C lines=1364 bytes=130109`（起/收尾逐位一致），
`FfmpegGui.dll=A95CCD5F80837E76`（23 枚同）/ `FfmpegGui.exe=18C70C341A8F2460` / `ServiceProbe.exe=2D4EBBAA395FBACD`
/ `UiTestHost.exe=E8550650A3A4F591` / `GainMapTestHost.exe=21D9F558D135599C`；无 `.gates.lock` 残留、无超时击杀、无源码漂移。
磁盘双采样（本轮起进启动器）：起 **32.21GB** → 收 **26.87GB** ⇒ 整轮自身留 ~5.3GB 夹具。

**命名豁免 ①** `probe:runner 7/1` —— 沙箱把子进程认成 `cmd.exe` ⇒「target is not ffmpeg ⇒ 拒绝注入 `-progress pipe:1`」，
与 beta7 / p2b / full3 逐字同读数。环境假红，**判据一字未动**。
**命名豁免 ②** `_probe-test-group-coverage 18/2` —— 按设计红：登记 7 个零调用点 mode
（`bitdepth`/`gainmap-decode`/`gainmap-encode`/`gainmap-struct`/`gamut` …）与 1 个非清单调用点（`icc`）。
它锁的是"这些 mode 还没进受管清单"这件事本身 ⇒ 红就是台账，不是坏掉的判据。

**第三条：本轮从"未署名抖动"变成"有名有率"**
轮内红的是 `ui-color` mode 的 `H10 命令含smpte432且非bt2020` —— 整行由运行器的逐条留痕文件存着
（`tests/output/_gate_script_verify-ui-split-union.ps1.txt:20`，`PASS=10 FAIL=1` 的那 1 条），
standalone 单跑同 exe 同参数则 `15/0`。
复现率实测（`tests/output/step2/h10_rate.py`，逐次跑 `Tests.UiColor.exe <sources> ui-color`）：
**负载窗口 54 次运行红 6 次（≈11%）**，随后的安静窗口 **20 次全绿** ⇒ 与「两条异步命令之间睡固定时长 = 赌」
这一已知形态同族：H10 写的是 `csCombo.SelectedItem = "Display P3"; Pump(6); SetFormat("PNG"); Regen()`，
用**固定 pump** 等异步联动收敛，负载下没收住 ⇒ `Regen()` 按选择生效前的状态建命令。
⚠ 失败行的命令串被 `Snip()` 截在 `-compression_lev…`，所以"读到的是哪个残留状态"这一层**没有量到**，只归到机制类。
⇒ 判据不放宽、不改文案；欠账登记为「H10 的 `Pump(6)` 换成确定性 settle」，属批次 G 邻域
（UI 断言三份逐字拷贝 + 413 计数钉 + verbatim/union 两把闸都在这一带），本批不动。
**（更正：这条在同一个交易日里已经做完，见下一节《H10 的固定时长等待换成确定性收敛》；本节保留当时读数原样。）**

**两条作废轮的账（写清楚，别让下一位重新猜）**
- `full3`：跑到一半我改了一行文档注释 ⇒ 运行器漂移闸判废（「本次结论不对应当前工作区 ⇒ 必须重建后重跑」）。
  该轮其余 71 条脚本全绿，且 `full2` 的两条 UI 分裂红（verbatim/union）在这一指纹上已复验转绿。
- `full4`：22 条红由 **C: 写满** 摧毁。签名三件：`ffmpeg` 子进程退出 **-28 = AVERROR(ENOSPC)**、
  红全部落在"依赖产物生成"的臂（夹具"复制失败"、`PASS=0`、两条 900s 超时击杀），纯计算臂 `contract 153/0` 照绿；
  **第二个读者** = Windows 事件日志 05:52 `ESENT -529`「数据库无法写入日志驱动器…磁盘空间不足」（系统自己的进程也被同一件事卡住）。
  同指纹单跑三条红脚本当场全绿（`tests/output/preflight-1.log`：`format-regression 19/0`、
  `colorfmt-matrix WARN=4 不判红`、`gainmap-managed` 全绿）⇒ 两条都不是代码结论。
  ⇒ 处方进的是启动器而不是判据：开局/收尾各采一次 `AvailableFreeSpace` 写进日志，低于 6GB 直接拒绝起跑。

## H10 的固定时长等待换成确定性收敛（2026-10-12，基线第三条红的收口）

**读数先摆**（交错 A/B/A/B，同一个 I/O 负载夹具，每臂 24 跑；`tests/output/h10-ab-io.log`）：

| 臂 | 内容 | 红/总 |
|---|---|---|
| A1 | 修好前 | **8/24** |
| B1 | 修好后 | 0/24 |
| A2 | 修好前 | **5/24** |
| B2 | 修好后 | 0/24 |

合计 修好前 **13/48**、修好后 **0/48**；Fisher 单侧 **p≈4.8e-5**；若真率仍是 27%，48 跑全绿的概率只有 **2.6e-7**。
换臂读数跟着翻 ⇒ 归属到这次改动，不是时段漂移。

⚠ **一条负结论**：纯 CPU 负载（20 个满载 `while($true){}`）**复现不出**（修好前 0/20，见 `load_and_rate.ps1`）；
换成 I/O 负载（4 个 ffmpeg 反复写 4K 无损大文件再删，`io_and_rate.ps1`）才复现 ⇒ **争的是磁盘不是算力**，
这正好解释它为什么只在整轮里出现、单跑恒绿。证伪夹具要挑对被检量的那根轴（[[feedback-adversarial-fixture-order]]）。

**机制是量到的，不是推的**：把「选中之后再走一次真实格式跃迁」（`SetFormat("JPG"); SetFormat("PNG")`）插进组体
⇒ **4/4 稳定红**，诊断行 `comboNow=auto idx=0`，**整条命令里连色彩参数都没有**。
根因 = `UpdateOptionAvailability()` 重建 `ColorSpaceCombo` 的 Items 并把 `SelectedIndex` 复位成 0
（`MainWindow.xaml.cs:2237-2258`），触发链含 fire-and-forget 的 `RefreshEncoderListAsync`（`:2051`）
与启动探测收尾的 `Dispatcher.UIThread.Post`（`:1347-1351`）。旧写法 `SelectedItem = …; Pump(6); SetFormat("PNG")`
同时踩两条：**顺序错**（格式联动排在选中之后）+ **拿固定轮数冒充等待**（§1.2 铁律：两条异步命令之间睡固定时长=赌）。

**改法（只动测试侧，产品一字未改）**：`SetFormat("PNG")` 先走完，再用**既有**的
`UiHost.SelectColorSpaceSettled`（`:570-597`，2026-10-07 为 J8 同一个坑加的：先切到必然不同的项保证真实跃迁，
再轮询到"连续两轮在位"，被复位就重选，等不到返回 false）。等不到时新增一条**响亮报红**的前置断言
`H10 Display P3 能稳定选中（前置，防异步复位吞掉赋值）`，并留一行 `[H10dbg] comboNow=… idx=…` 诊断（与 J8 同法）。
**原断言名与期望一字未动** ⇒ 绿态断言名集合不变 ⇒ union 的 413 计数钉照旧（旧宿主实跑 `413 PASS / 0 FAIL`）。

**三份拷贝的代价（诚实记账）**：组体必须在 `tests/UiTestHost/Program.cs` 与 `tests/ui-color/uicolorGroup.cs`
逐字一致（GroupH 不在 verbatim 的豁免表里）⇒ 同步成 **2342 字节**两侧同段逐字相等，`verbatim 20/0`（比对行数 3064→3084）。
为此在旧宿主里补了一份**同名本地实现**——旧宿主不引用 `UiHostShared`，且它自己的 `W` 与共享底座的 `W`
是两个窗口 ⇒ 没法转发。**这是新增的第二份实现**，记进批次 G 的账（#13）。

**两支牙**（脚本 `tests/output/step2/h10_teeth.py`，两支都 `REVERTED identical=True` 后逐工程重建）：
- **CT1 产品变异**：`ColorSpaceRegistry.cs` 把 Display P3 的 `OutPrimaries` 改成 `bt2020` ⇒ **原断言**红
  （命令实际变成 `-color_primaries bt709 …`）⇒ 当场证明"等正向条件"没把判据做成恒真；同轮 `H1` 一起红（同一张表，预期）。
- **CT2 夹具变异**：settle 的目标改成不存在的色域 ⇒ **新前置断言**红，读数
  `选中后 combo 实际停在「<null>」⇒ 本格读数不可信`。

**顺手量到一条会影响所有历史结论的事实**：`FfmpegGui.dll` 烤进了
`AssemblyInformationalVersion = 1.6.0-beta7+<完整 commit sha>`（SDK 的 SourceRevisionId 默认行为，
`Directory.Build.props`/csproj 里查不到任何相关开关）⇒ **代码一字不动、只多一个提交，sha16 就变**
（`A95CCD5F80837E76`@`559d871` → `46FE4D894871042B`@`942af28`；23 枚宿主仍一致、`git diff HEAD -- src` 为空）。
⇒ 报"某轮绑 X 指纹"必须**同时写当时的 HEAD**。取证一行：
`py -c "d=open(dll,'rb').read(); i=d.find(b'<短sha>'); print(d[i-40:i+50])"`。

**新登记的一条红（H10 的同族形状，但**不能**照搬修法）**：`ui-gainmap-toggle`（组 O）在子集轮里红 6 条
（`O2a/O2b/O2c/O3a/O3b/O3c`：色域 `Display P3→auto`、位深 `8→auto`、质量 `74→92` 而默认是 84），
随后单跑 8/8 全绿。它断言的**就是"三项必须保持"** ⇒ 用"边等边重选"去让它变绿，等于把"产品真把用户选项冲掉了"
这件事抹掉。判别的读数设计好了：把 `gm.SelectedIndex` 变更后的取值改成"轮询三个控件连续 K 次读数不变再取"——
稳态回到 pick ⇒ 读在重建的中间窗口（改测试）；稳态仍是默认 ⇒ 产品缺陷（改产品，且当场量到红）。
这条要动 `ui-mode` 的组体 ⇒ 牵动旧宿主同段与 verbatim/union 两把闸 ⇒ **单列一批**，不混进本批。

## D-1 同族扩展：三处「没换格式的晚到刷新」不再冲掉用户所选（2026-10-12）

**触发点是宣布基线时的第三条红**，追下去发现它不是 H10 那一条，而是同一族的三类无条件复位
（`UpdateOptionAvailability()` 与 `RefreshEncoderListAsync()`）。整条链每一步都有日志，包括**否证我自己解释**的那一步。

1. **间歇红复现**：union 子集轮里 GroupO 的 `O2/O3` 六条红，读数 `Display P3→auto`、位深 `8→auto`、质量 `74→92`；
   同一份二进制 standalone 单跑 `ui-gainmap-toggle` 8 次全绿 ⇒ 频次型，不是确定性回归。
2. **机制定位（读产品码，不靠猜）**：`HdrMode_Changed` **本来就有** D-1 的快照—回放；冲掉三项的是
   `FullDetectionAsync` 收尾那拍 —— `Dispatcher.UIThread.Post{ RefreshEncoderListAsync; UpdateExifToolPanelState;
   UpdateOptionAvailability; … }` 排在 `[detect] 全部检测完成` **之后**入队，那拍**裸调**刷新 ⇒
   落地即永久复位（没有任何东西会把它写回来）。`92` 的出处是 `FfmpegOptions.GetDefaultQuality("jpg")` 的
   `_ => 92`，不是我一开始猜的"另一档编码器的默认"。
3. **产品修法**：把 D-1 的快照—回放抽成 `RefreshOptionsKeepingTargets()`，让两处**没换格式**的晚到刷新
   （探测收尾 Post、`InitializeCapabilitiesAsync` 的 `await` 之后）走它；**切格式 / 应用预设那几处仍然裸调**
   ——「切格式 ⇒ 重建候选集 ⇒ 回默认」是期望语义，一字没动。
4. **新判据 O4**：反射直接调那两处共用的保护方法 ⇒ **确定性**，不靠等时机。
   修前读数 `FAIL O4 … RefreshOptionsKeepingTargets 不存在`；修后 `O4a/O4b/O4c` 全绿。
5. **顺手查出第三处，而且比前两处严重**：`RefreshEncoderListAsync` 跨 `await GetEncodersForFormatAsync` 之后
   **无条件** `SelectedIndex = 该格式默认` ⇒ 任何一次列表刷新都会悄悄把用户所选后端换掉，
   正好违反该方法上方自己写的「可用性诚实…避免造成『实际编码器 ≠ 用户所选』」。
   - 我先给出**错误解释**："读得太早"。于是做机制判决：把空转泵 `Pump(8) → Pump(60)` ⇒
     **仍 2/20 红** ⇒ 我自己的假设被自己否证（不是读晚了，是产品换了选择）。
   - 加 `O5`：反射**跑完**那一次刷新再断言所选后端 + 命令里的 `-c:v` ⇒ 修前 **3/3 确定性红**
     （`期望=mjpeg — MJPEG (Motion JPEG)`）；修后 13/0。
   - 修法与 `SetComboByValue` 同语义：**新列表里还有原选项就保留，没有才回落默认**
     （切格式 ⇒ 旧后端不在新列表 ⇒ 行为与今天完全一致）。
   - 兑现读数：同一条 I/O 负载夹具，`ui-sweep` 修前 **2/20 红**（J1/J2 之间换位置红）、修后 **0/20**。
6. **结构锁扩到 ⑥**（`_probe-gui-control-wiring.ps1` 77→82）：保护方法调用点必须 == 3、能力初始化体内
   必须走它且不得裸调、探测收尾 Post 必须带它；再加一条**反向对账**——「切格式那处仍然裸调」，
   防我"顺手把所有刷新都包起来"偷偷改语义。牙（全部 `REVERTED identical=True` 后重建）：
   MUT-W1 把 Post 改回裸调 ⇒ `80/2`；MUT-W2 把能力初始化改回裸调 ⇒ `79/3`；
   MUT-W3 摘掉两条回写 ⇒ `O2a/O2b/O3a/O3b/O4a/O4b` 六条红（证明行为判据也咬，不只签名）。
7. **计数钉两次抬** 413→417→419（O4 四条 + O5 两条），每次都在旧宿主与拆分并集**量到同数**之后才抬；
   verbatim 比对行数 3064→3188，GroupO 方法体两份逐字 10678 字节一致。

**边界（不写成已完成）**
- `UpdateOptionAvailability()` 本体没动（内部仍是无条件复位），只换调用点 ⇒ 语义面收敛在两处晚到刷新。
- 还有一类**没被免疫**：`RegenerateCommand()` 是 `async void`，用例在它的 await 窗口里读 `CommandText`
  仍可能读到上一版。O4/O5 是"反射等真正完成"绕开的；判据面要彻底免疫需要产品给一个**完成信号**
  （seam），这条登记着、本批没做。
- 调查中一条子代理回报**编造了两个不存在的 helper 名与行号**（`RestoreInputsBeforeOptionRefresh` /
  `SelectComboItemKeepScroll`，我 grep 命中 0 当场否证）⇒ 转抄二手证据句之前必须自己 grep 一遍（老规矩再兑现一次）。

## full8：重绑基线 + 工具链恢复（2026-10-12 晚）

**基线读数**（`tests/output/gates-round-2026-10-12-full8.log`，17:4x–19:08）：73 条受管脚本
**72 绿 / 1 红**（唯一红 `_probe-test-group-coverage` = 按设计登记零调用点 mode），探针 27 mode
**红 2 条**（`runner` 沙箱假红、`bench` 见下），尺 `1C785039CDA1A86C`，
被测件 `FfmpegGui.dll=224ACA81AB6591BA` / `FfmpegGui.exe=B2C457DCCB092C4B` /
`ServiceProbe.exe=B1672D72ED2606B9` / `UiTestHost.exe=FF86AFE5AAF401F7` / `GainMapTestHost.exe=BAB1C182671DCC37`，
HEAD=`942af28`，无锁残留、无超时、无源码漂移。磁盘双采样 36.15GB → 99.93GB（这台机器的清理器在轮跑期间腾了 60GB）。

**`probe:bench` 那条红归因到负载，不是代码**：红的不是吞吐档而是**对账臂**
（关表档"实测增益必须 ≈1×"，轮内实读 **1.4×** > 阈值 1.3），且该 mode 用时 **71.6s vs 平时 57-59s**
⇒ 整段被外部 I/O 拖慢。静机同二进制复跑两次：**30/0，比值 1.1× / 1.0×**（`bench-quiet-{1,2}.log`）。
⇒ 登记一条方法账：吞吐档早就改成"best-of-N 取最优"，这条**比值型对账臂仍是单样本**；
把它也改成 min-of-N 是估计算法对齐、**不是放宽阈值**，但改完必须用变异重验
（放开编码侧关表条件 ⇒ 比值仍要 >1.3 转红）——本批未做，排在批次 G 之后。

**工具链被清理器删掉的三件（`publish/PLAN/artifacts/` 是 gitignored，git 恢复不了）**：
`avifenc.exe`、`dngtool.exe`、`ultrahdr_app.exe`。恢复过程与两处新发现：
- `avifenc` 不必重编：`tools/src/libavif/build/Release/avifenc.exe` 就在盘上，
  跑出来 `Version: 1.4.2 (aom [enc]:3.15.0)`（与 `BUILD_TOOLS.md` 钉的死值一致）。
  ⚠ 只拷 exe 不够：它动态依赖同目录 `avif.dll`（实测 `error while loading shared libraries: avif.dll`），
  部署清单本来就是**两个文件**（`tools/build_tools.ps1:354-358`）⇒ 一次"恢复工具链"要按清单整批拷，别凭印象挑。
- `dngtool` 也不必重编库：`tools/src/dngtool/build` 里 168 个 obj + `dng_sdk.lib` + `libjxl/build/lib/Release/jxl.lib`
  都在 ⇒ 只做增量链接就出 `dngtool.exe`。
  ⚠ 顺带查出 `build_tools.ps1` 自身两个会让恢复变灾难的缺陷：① 7a 的"已构建"守卫查
  `libjxl/build/Release/jxl.lib`，而 CMake 实际输出在 `build/lib/Release/jxl.lib` ⇒ 照脚本跑会**白重编整个 libjxl 0.8**；
  ② 7b 起手 `Remove-Item -Recurse -Force $DngToolBuild` ⇒ 会把可增量链接的树连 obj 一起删光。
  两条都登记未改（改守卫就要配一支"守卫路径 == 部署路径"的判据，否则改完还是假的）。
- `ultrahdr_app` **不需要恢复**：`build_tools.ps1:215/347` 已把它从工具链移除（纯 C# GainMapEncoder 取代），
  只有第三方见证门禁引用它，而那门禁缺件时是**正确 SKIP**（不是假绿）。
- 恢复后受影响 5 条门禁当场转绿：`verify-color-caps`、`verify-metadata-privacy`、
  `verify-gif-avif-framelist`（原为整批 SKIP ⇒ 现在真跑真过）、`_probe-raw-default-tier`、
  `verify-usability-e2e 84/0`（含之前红的 `gif→avif` 格）。
- 文档过期两处（本轮只登记，未改）：`tools/BUILD_TOOLS.md` 的表写 ffmpeg=git-2026-08-06，
  实际出货件是 **git-2026-10-11-edadf711a9**（与本机 `C:\PLAN\FFmpeg` 同一构建）；同表仍把
  `ultrahdr_app` 列为"VS 自编译"，而脚本已移除它。
