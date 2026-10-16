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
- C-4 **F-7 通道与色度采样**（原文写作「F-4/F-8」= **lane 取证号 F4/F5/F8**，不是 FIX_PLAN 的 F-4/F-8；⚠ 命名空间撞车：FIX_PLAN 里 F-4=增益图探针三态、F-8=**表缓存**，而 F-7 才是本条）：先把"声明"降到与实现同一句话（flags 说多通道而数据 4:2:0 = 说谎），再把实现提到声明水位；第三方读回必须由**第二个独立读者**命中同一事实。
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
- ~~`C:\PLAN\ColorGainKit\src\ColorMatrix\ColorSpaceRegistry.cs:780` 的同一个 `1.0633`：**另一个公开仓，需点头**才改~~ ⇒ ✅ **已落地并已提交**（2026-10-13 复核）：改法与本仓一致（附 lcms/skcms/libjxl 三锚 + 行和≈1）。
  取证：那仓 `src/ColorMatrix/ColorSpaceRegistry.cs:785` 现读 `…, -0.0685, 1.0296 }`，`git log -S"1.0296"` 命中 **`79c3f4b`**（该文件 `git status` 干净 ⇒ 非在飞改动），本地领先 origin **1 笔** ⇒ **只剩 push**（远端动作，仍要点头）。
- `contract` 两份拷贝的消除（保留被执行的一份，另一份要么删要么改成转发）——这是本轮踩坑的根因修复。⚠ 2026-10-13 更正：先前把这条记成「UI 三份拷贝的编号错」时我差点写错——`F4/F5/F8` 是 **lane 取证号**（见上面 C-4 那条的消歧），不是账本编号混用；先回读原文再下「这是错」的判语。
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
  **F-7**（lane F4/F5/F8）AVIF 出口 `gmPixFmt = multiChannel ? "yuv444p" : "gray"`，兜底只在同语义内兜、绝不退 4:2:0；解侧逐通道 min/max/γ/offset 各自施加（C7a `maxΔ=2.57e-4`、C7b 通道差 `ΔGR=0.2595 ΔGB=0.1438`）。
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


---

## 批次 G（UI 面）：把拆分件收回「生成物」这个身份 —— 2026-10-13

**接手时的一句话事实**：`UiTestHost/Program.cs` 号称 17 个 UI 组的「唯一真值」，而它实际上已经不是了。
先量的不是"怎么改"，而是"这句话现在还成不成立"：

```
pwsh -File tests/scripts/_gen-ui-split.ps1 -RepoRoot <一份只含 Program.cs 的空仓库根>
     → 把 5 份生成物与活树做 sha256 逐字节对撞
```

**实测：5 份里 2 份不是生成物。**

| 工程 | 生成物行数 | 活树行数 | 差在哪 |
|---|---|---|---|
| ui-core / ui-mode / ui-cli | — | — | 逐字节全等 |
| ui-color | 925 | 927 | 活树多 1 个手加包装 `SelectColorSpaceSettled`（含 1 行注释） |
| ui-sweep | 1092 | 1172 | 活树多 3 个包装（`Skip`/`ResetAdvancedColorToAuto`/`SelectColorSpaceSettled`）+ **J1z 修复 38 行** + **J8 修复 36 行** + 1 行注释各写各的 |

⚠ 另有 **61 处裸 LF** 的差异不是内容：**生成器脚本自身是 LF 编码**，`$usingBlock`/`$forwarders` 两段
here-string 于是带裸 LF 落进生成物，而活树被编辑器保存过 ⇒ 归一成纯 CRLF ⇒ **"重跑 + 比字节"这件事
在此之前永远对不上**，与有没有人手工改过无关。这一半只有先量字节才会看见。

**真正的缺陷不是"有两份拷贝"，是"真值这一侧在跑修复前的代码"**：verbatim 因此只能整组放行 `GroupJ_`
⇒ **J 组 798 行退出逐字比较**，而参照件旧宿主还在跑 J1z 的假红断言与 J8 的 `cs.SelectedItem = v; Pump(8)`。
`verify-ui-split-union.ps1` 还得靠一条 **J1z 改名白名单**（3a2/3b2 要求"恰好命中 1 条"）才绿——
那两条反控当时的含义是"承认两侧不同名"。

### 改法（四条，顺序要紧）

1. **回灌**：J1z（环境前置改 `Skip` + 无条件形态断言）、J8（改用 `SelectColorSpaceSettled` 等异步联动落地
   + `[J8dbg]` 诊断行）、以及旧宿主缺的 `Skip` helper，全部搬进 `Program.cs`（`+91/−3`）。
   搬运用**从被执行那份逐行切片**（`feed_j.py`：先断言边界行内容，再取 140..178 / 346..383），不手抄。
   回灌方向上的例外只有一处：拆分侧那句"与 UiTestHost/Program.cs 的同名臂逐字一致"是**转述**，
   真值里该留的是原判据句（`②的三个部件缺任何一个都判红 ⇒ 钉死"改标签不改像素"`）⇒ 让生成器把它刷回拆分侧。
2. **包装集合改为「按实际引用推导」**：`$forwarders` 变成 40 条**候选池**，生成器把池切成条目，
   每个工程只发它的**组体 + 入口**真用到的。判据 = 剥掉行注释后的文本里出现该名字。
   两种错都不静默：漏发 ⇒ 编译当场失败；只在字符串字面量里出现 ⇒ 多一个没人调的私有包装（无警告、无害）。
   ⇒ 当场清掉 `ResetAdvancedColorToAuto` 这个**零调用点死包装**（每工程裁掉 12–23 条）。
   ⚠ 入口必须一起扫：H 的 `$colorPre` 与 L 的 AVIF 定型前置都调 `SetFormat(...)`，只扫组体会裁掉入口在用的包装。
   ⇒ 顺带把入口文本抽成 `Get-Entry`，让"参与扫描的入口"与"写进文件的入口"是同一次产出。
3. **生成器输出统一 CRLF**（`($sb -split "`r?`n") -join "`r`n"`）⇒ 字节比对从此与编辑器保存无关。
4. **两道旧豁免归零**（是收紧，不是放宽）：verbatim 的 `$waivedGroups = @{}` + 新增反控
   「登记表必须为空 / 实际跳过 0 组 / 真正比较了 17 组」；union 的 `$renamedPairs = @()`（3a2/3b2 现在要求命中 **0**）。

### 判据：新门 `verify-ui-split-reproducible.ps1`（受管第 74 条，清单 73 → 74）

19 条，全部**源码形状**判据 ⇒ 不需要重建任何二进制（实测 ≈40 s，主要花在跑 3 次生成器）。
R-1..R-5 五份逐字节全等 · R-6 **改真值 ⇒ 生成物必变** · R-7 量级反控（防路径错导致空文件互比"全等"）·
R-8 纯 CRLF · R-8b 生成器**幂等**（同输入两次同一字节，防把 PID/时间戳写进生成物）· R-9 **零死包装** ·
R-10 **双拷贝等值锁**（旧宿主与 `UiHost` 两份 `SelectColorSpaceSettled` 逐行相等，实测各 26 行、差异 0）·
R-11 旧宿主 `Skip` 形状锁（打 `[SKIP]`、计 `_skip`、**不动** `_pass`/`_fail`）。

接线同批改的四处条数陈述：`$expectedScriptCount` 73→74 + 运行器三种散文形态（`全跑 N 条`／
`清单条数以实测为准 = N 条`／`受管基线：脚本清单 = **N** 条`，由第 ⑰ 针对账）+ 脚本段时长预算
`2015 s → 2070 s` + `docs/TESTING.md` §3.5 链首一条。

### 当场量到的读数（都有留痕）

- 回灌+生成+重建后：`GENCHECK total=5 drift=0`；7 工程 `-t:Rebuild` **0 警告 0 错误**
  （旧宿主第一版带出 1 条 `CS8604`，是拆分侧当年就有的同一处 ⇒ 真值里补 `v!` 后两侧一起归零）。
- `verify-ui-split-verbatim` **22/0**：17 组全比、**Group J 798 行逐字一致**、比较 3263 行、登记表 0、跳过 0。
- `verify-ui-split-union` **11/0**：旧宿主 **419 PASS / 0 FAIL**、并集 **419**、missing/extra 各 0、
  白名单命中 **0**（两侧同名）。留痕 `tests/output/step2/union_after_feedj.log`。
- `verify-ui-split-reproducible` **19/0**。
- **变异四支，各只红该红那条**（`tests/output/step2/mut_g_readout.txt`，跑完逐字节还原相等）：
  ① 活树加一行注释 ⇒ `R-1 ui-color` 红（1 条）；② 给 ui-cli 塞一个它没调用的 `Snip` 包装 ⇒ `R-9`＋`R-1`（2 条）；
  ③ 只改 `UiHost` 那份 `Pump(6)`⇒`(7)` ⇒ `R-10` 红（读到 `行10: old=[Pump(6);] base=[Pump(7);]`）；
  ④ 旧宿主 `Skip` 里加 `_pass++` ⇒ `R-11` 红（`误动=True`）。

### 自我更正（这一轮被当场推翻的三条）

1. **R-6 的牙先咬到我自己的实现**：第一版把"改真值"注入写进 `$scratch/tests/UiTestHost/Program.cs`，
   而 `Invoke-Gen` 每次都从活树重新拷贝真值 ⇒ 注入被自己覆盖 ⇒ 该条判红。修法是给 `Invoke-Gen` 加
   可变 `$srcAbs`，变异打在独立目录。**对照臂不动就不算对照**（[[feedback-no-unverified-narration]] 又中一次）。
2. **G2 的靶子选错**：第一版拿 `UiState` 当"ui-cli 没用到的包装"，而 `uicliGroup.cs:63` 的 M 组**真的调用它**
   ⇒ 造出来的是重复声明，`R-9` 不红是**对的**。换 `Snip`（该文件 `grep -w` 命中 0）才成。
3. **注释归属写反**：条目切分第一版把"非声明行"并进**上一条**的尾巴 ⇒ `Skip` 上方两行注释挂到了 `Check`，
   于是未引用 `Skip` 的工程照样发出那段注释、包装本体却被裁掉（注释与包装分家）。
   这条是**我自己在 diff 里看见的**，不是编译报错——编译不会管注释。

另有一条**环境假红**要记账：回灌后第一次跑 union 得 `7/4`，红的不是 UI 拆分而是
`I3 ProPhoto保真 :: GetFaithfulIcc 返回 null`——机制 = 该服务把命名 ICC 写进 `PlatformServices.GetTempDir()`，
而我的启动器是从 MSYS bash 起的，`TMP=/tmp` 被子进程继承 ⇒ 写不进去。
按整轮启动器的同一姿势（`TMP/TEMP` 指到工作区 + 清 `LC_ALL/LANG/LC_CTYPE` + `FFMPEGGUI_FFMPEG_DIR`）复跑 ⇒ **11/0 @419**。
⚠ 顺带记一条我自己的启动器缺陷：上一版只并了 `2>&1`，而门禁用 `Write-Host`（information 流）⇒ 日志 0 字节、
读数只活在终端里。现已 `6>&1 2>&1 | Tee-Object`。

### 批次 G（UI 面）没做的事（欠账，按要付的代价排）

- **旧宿主的基础设施仍是第二份实现**（`Pump`/`Find`/`Check`/`TempDir`… 一整块，不只那两个 helper）：
  它不引用 `UiHostShared`，而 `UiHost` 的 helper 绑的是 `UiHost.W` ⇒ 改成转发要连带把参照件的
  Avalonia 引导换掉，**换了之后 union 的"参照实现"就与拆分件共享底座**（并集判据仍成立，独立性下降一档）。
  ⇒ 这一步按 #18 的纪律**先装尺子再谈合并**，本批只做等值锁 R-10/R-11 把它钉住。
- `UiHost.ResetAdvancedColorToAuto` 现在**全仓零调用点**（J8 的最终修法用了 `SelectColorSpaceSettled`）：
  死包装已由推导消掉，`UiHost` 里那个 public helper 留着未删——它的文档记载了两条**真知识**
  （`SelectComboItem` 按值查下标 ⇒ 同值不触发；选 `auto` 不足以复位高级三元组）。要删得先给这两条找落点。
- 运行器被改 ⇒ `runner-self-sha256` 必然漂移 ⇒ **上一轮全绿不再归属当前树** ⇒ 本批收尾按纪律
  逐工程重建 23 个目标 + 重跑整轮（读数见下面「### 整轮读数」）。

### 整轮读数（`tests/output/gates-round-2026-10-13-full9.log`；起跑 00:22:33 / 首条留痕 00:24:55 / 收尾 00:57:25，执行 ≈32.5 min，清单 **74** 条）

- **尺与归属**：`runner-self-sha256=FDA160BC74426619 lines=1393 bytes=133476` 起/收**一致**
  （旧尺 `1C785039CDA1A86C lines=1364 bytes=130109` 自此作废）；`shape-selfcheck=OK（17 针）`；
  `script-list=74 条（受管基线 74）`；无 STALE、无漂移警告、`.gates.lock` 正常释放；
  磁盘 88.91 → 85.83 GB（开局 6 GB 闸门未触发）。
- **被测件**：`artifact-sha16 FfmpegGui.dll=3F7ED22634403E33`（23 个目标 `-t:Rebuild` 全成功、
  23 枚 `FfmpegGui.dll` **同一 sha**）；`FfmpegGui.exe=0D6B7E63BAE6EDD5`、`UiTestHost.exe=295C5CAEFA3F0594`、
  `ServiceProbe.exe=6B8BBC81EB8CFA34`、`GainMapTestHost.exe=3135507C6F48B6B6`。
- **汇总 = 失败 2 项，两条都是先前已点名的既有红，本批没有新增红**：
  ① `probe:runner 7/1`（沙箱把子进程认成 `cmd.exe` ⇒ 拒注 `-progress pipe:1`；与 beta7/p2b/full5–full8 同读数，
     2026-10-07 已定论「不为它改探针或产品」）；
  ② `script:_probe-test-group-coverage`（**按设计红**：7 个零调用点 mode `bitdepth/gainmap-decode/gainmap-encode/
     gainmap-struct/gamut/…` + 1 个非清单调用点 `icc`）。
- 其余 **73 条脚本全 exit=0**，本批三把 UI 锁在**轮内**的留痕读数：
  `verify-ui-split-verbatim` **22/0** · `verify-ui-split-union` **11/0**（`new=419 old=419`）·
  `verify-ui-split-reproducible` **19/0**（轮内 `R-10` 读到「旧宿主 26 行 / UiHost 26 行、差异 0」）。
- 参照件驱动的 5 条 UI 门禁在轮内全部打 `===== UI 测试汇总: 419 PASS / 0 FAIL =====`
  （`verify-ui-host` / `verify-ui-param-matrix` / `verify-ui-strategy-map` / `verify-ui-param-defects` /
  `verify-cli-strict`，该串在轮日志里恰好命中 **5** 次）
  ⇒ **回灌后的旧宿主自己在跑修复后的 J1z/J8，且不再需要改名白名单**。
- 顺带留档（与本批无关但下轮可比）：`bench 30/0`（full8 那条比值臂负载假红本轮安静时段不复现）、
  `contract 153/0`、`_probe-cjk-hardcode-scan 14/0`（本批新增中文注释没顶破任何一档棘轮）、
  `_probe-gui-control-wiring 82/0`、`verify-usability-e2e 84/0`。
- ⇒ **新基线现值 = `3F7ED22634403E33`（74 条 = 73 绿 + `_probe-test-group-coverage` 按设计红；
  探针段 `runner` 沙箱红）**。宣布口径沿用：**逐条点名，不压成总数**。
- ⚠ **但这枚指纹绑的是「提交前那一刻」的构建件**（本批是**先跑整轮、后提交**）：实测盘上
  `src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.dll` 里烧的版本串 =
  `1.6.0-beta7+5fee5b991f779beb254a32809433297d2dbe4069`，即批次 G（UI 面）那笔提交的**父提交**
  （本批落在 `39397db`）。⇒ **`src/**` 一字节没动也会让 dll 指纹变**：这是 .NET 8+ 的 SDK **默认**行为 ——
  `IncludeSourceRevisionInInformationalVersion` 默认 `true`，把 `SourceRevisionId`（= commit sha）追加进
  `AssemblyInformationalVersion`（权威口径取 MS Learn 该属性条目原文「controls whether the `SourceRevisionId`
  value is appended to the `InformationalVersion` assembly attribute」；全仓 `*.csproj`/`*.props`/`*.targets`
  grep 该属性与 `AssemblyInformationalVersion` **命中 0**，且本仓**根本没有 `Directory.Build.props`**
  （`ls` 报 No such file ⇒ 不是「找到了但没设」，是这层文件不存在）⇒ 走的确实是 SDK 默认，
  实测 dll 里就是 `+5fee5b9…`）。
  ⚠ 两处我自己的错，留在账上：① 第一版把这句写成「不是我能选的行为」是**夸大** —— 它可以选
    （显式置 `false` 或钉死版本串），但那是产品级发布元数据的决定（会改对外版本口径、要配自己的判据）
    ⇒ 登记为未做，不在本批范围；② 第一次「取证」跑的是 `grep … Directory.Build.props FfmpegGui.csproj`，
    stdout 空我就当成「查过、没设」—— 实测该命令**退出码是 2**（`Directory.Build.props` 在本仓根本不存在，
    stderr 明写 `No such file or directory`）。⇒ **grep 并不静默，是我丢掉了退出码和 stderr**：
    退出码 **0=命中 / 1=真没命中 / 2=目标有问题**，2 与 1 是两个不同结论；下缺席断言要当场 `; echo exit=$?`，
    并加一层**目标正对照**（实测 `grep -c net11.0 FfmpegGui.csproj` = 3、exit=0，证这条命令能命中东西）。
  ⇒ 下一轮整轮会绑一个新 sha16；**跨轮比数要同时带 `runner-self-sha256` + 提交号**，
    只写 sha16 不够（同族旧账：`97DFCB9A`→`224ACA81`→现值，每一跳都混着「改码」与「只是提交了」两种原因）。


---

## 批次 G（探针面）与批次 F 首批 + 台账卫生（2026-10-13 下午，绑 `full10`）

### 1. 探针面先装尺（#18 的第一半，`verify-probe-mirror-parity.ps1` = 受管第 75 条）

内核断言有**两份拷贝**：被执行的那份是 `tests/ServiceProbe/Program.cs`（`$Probes` 只调它），
镜像是 `tests/core/Modes/*.cs` + `tests/diagnostics/Modes/*.cs` —— 两边都是 `Exe`，
`dotnet test` 打 Exe 只还原不跑还 exit 0，且全仓除生成器注释外**零调用者**
⇒ 往镜像写断言的真实结果是「全绿但一条不执行」（本仓 2026-10-11 实测踩过 C20 一次）。

**先量再谈合并**（一手读数，`tests/output/step2/probe_mirror_ruler.py` 与门禁是**两个独立实现**）：

| mode | 被执行那份 | 镜像 | 标签多重集 |
|---|---|---|---|
| contract | 92 | 91 | 差**恰好 1 条**（`C20-8b`） |
| bench / selftest / tooldetect / i18n / xcheck / psnr / peak | 5 / 18 / 27 / 5 / 6 / 2 / 8 | 同左 | **完全相等** |

判据 = `Check(...)` 里的**字面片段**拼成标签、`{...}` 插值槽位归一（**变量名不算差异、文案算**）后比多重集；
不比整文件 —— 两份的注释与变量名本来就不同（SP `{name}` vs 镜像 `{nameC20}`），比字节的锁活不过第一次合理改名。

**那 1 条差是登记，不是豁免**：镜像 `contract.cs:44` 自己写着「要加断言请加在那一份上；
写在这里会全绿但一条不执行」⇒ 它是**冻结参照件**，把新断言搬进死代码只会制造「以为有人执行」的更坏假象。
登记项由反控逼「恰好 1 条 × 1 次」⇒ 哪天被同步掉（命中 0）或又漂出第二条，**当场红**。

变异验牙四支（`tests/output/step2/mut_parity_readout.txt`，逐字节还原相等）：
只改镜像一条标签**文案** ⇒ `tooldetect` 红（1 条）；把镜像一条 `Check(` **改名** ⇒ `peak` 红（只SP 1、只镜像 0）；
只改被执行那份的 `C20-8b` ⇒ `contract` **两格**红（未登记差 + 登记命中 0）；
**两侧同步**改同一条 ⇒ 必须全绿（假阳性对照：这把尺量「漂」不是量「变」）。读数 28/0。

### 2. 批次 F 首批 = `F-11` 的「分步点名」那一半，并当场量出一条新缺陷 `F-13`

`ExifToolService.EnsureJfifHeaderAsync` 原先**不传日志回调 + `catch { }` 全吞**，
调用方 `QueueProcessor` 紧接着就打「元数据恢复完成」⇒ 把「没做成」记成「做完了」。
改法（不合并调用，性能账留给 `F-12`）：返回 rc、接收 `Action<string>?`、调用点接上 `item.Log`、
异常与「exiftool 不可用」各自点名（新增 `SkippedNoExiftool` 哨兵，**跳过 ≠ 成功**）。

`metaraw` 探针新增 D 组三条判据（加在**被执行那份**里；`metaraw` 不在那张 8-mode 表内 ⇒ 不引入新漂移）：
D1 交付件确实带 JFIF（exiftool 读回，第二个读者）｜ D2 这一步必须留痕（见证行 `[exiftool] JFIF 头写入 rc=`）｜
D3 见证行里的 rc **必须等于**方法返回的 rc（防日志与行为脱钩）。实测 `metaraw 20/0`。

**⚠ 新发现 `F-13`（实测，登记）**：`exiftool 13.59` 对本仓 ffmpeg 产的 JPEG 写 `-JFIFVersion=1.02`
**恒失败**（`Warning: Sorry, JFIFVersion is not writable` + rc=1，分组限定写法同样被拒），
而产物**本来就带** `[JFIF] JFIFVersion : 1.02`（APP0 由编码器写）⇒ 这一步是**常驻 no-op**，
手机兼容性目标实际由编码器兑现。修复前这件事**在日志里完全看不见**。
未证边界：只量了 `lavfi testsrc → mjpeg` 一条路径，**未逐出口（cjpegli / libjpeg-turbo / jxl→jpg 中转）量过 APP0 是否恒在**
⇒ 不把「所有 JPEG 出口都自带 JFIF」写成结论。D1 断言的是**这一格产物**，正是为此。

变异验牙（把"点名"退回修复前的静默）：`metaraw 18/2` —— **D1 仍绿、D2/D3 双红**，
证明红的是"没留痕"而不是产物；还原后 sha 回到 `5a86e88d3b5f7db9`、重建 ⇒ `20/0`。

### 3. 三条被我自己证伪的账本记载（改账本，不是改代码）

- **`bench` 比值臂「还是单样本」是错的**：两臂自 `a4c7cfd`（10-07 14:44）起就都是 **min-of-3**
  （快臂 `ms = Math.Min(...)`、慢臂 `slowMs = Math.Min(...)`），比 full8 早五天
  ⇒ full8 那条 `1.4×>1.3` 的真实成因是**负载**（同 mode 用时 71.6 s vs 平时 57–59 s），不是采样数。
  ⇒ 本批**不动任何阈值**（放宽判据要授权），只补**极差读数**：两臂各自 3 次的 max−min 打进参考行
  （实测极差 0.3–33 ms 不等 ⇒ 负载下的比值本来就该带这个上下文看）。
  ⚠ 过程错两条也记上：① 我先误把极差读数打到了**镜像** `tests/diagnostics/Modes/bench.cs`
  ——`bench` 实际由 `ServiceProbe.exe` 执行 ⇒ 已 `git checkout` 还原再改被执行那份；
  ② 我为了跑 bench 直接起 `Tests.Diagnostics.exe` 得 rc=2 —— 那个宿主根本不跑 bench（又一次"探针活着才能问能力"）。
- **`verify-gainmap-managed` 的「6 条 HDR 底陈旧断言待 P6 清理」是过期记载**：
  该门禁 `:134-146` 已在 2026-10-11 把它们换成「废弃开关必须无效 + 带它与不带它的产物逐像素一致
  （降级后 PSNR ≥ 50 dB）」并自带负控 ⇒ 要改的是账本，不是代码。
- **「整轮自留 ~5.3GB 夹具（`gmmem_*`）」量错了**：实测残留 `tests/output/gmmem_*` **21 个目录合计 27130 KB**
  （≈4 MB/个，不是 70 MB/个）；且在本沙箱环境里 `Remove-Item -Recurse -Force` **删得掉**
  （拿真实遗留目录做实验，`Test-Path` 转假）⇒ 旧注释说的"沙箱拦截致静默失败"不成立。
  堆积的真实原因是「**失败臂有意保留**供排查」+「被中途叫停的重压臂来不及跑到收尾」。
  ⇒ 现在 `verify-gainmap-memory.ps1` 开局清扫 30 分钟外的旧 `gmmem_*`（时间护栏避开并发实例）、
  收尾改成重试 + `cmd rmdir` 兜底 + 删不净就点名；默认臂读数不退化（**16/0 SKIP=1**）。

### 4. 台账卫生两处（#19）

- `IMPLEMENT_PLAN` 把 AVIF 通道/色度那件事写成「F-4/F-8」，而 `FIX_PLAN` 里 **F-7** 才是它
  （F-4=探针三态、F-8=表缓存）⇒ 已改成「F-7（lane 取证号 F4/F5/F8）」并加命名空间提示；
  ⚠ 我第一版判它是"编号错"，回读 `FIX_PLAN:56` 才发现括号里的 `F4/F5/F8` 是** lane 取证号**——
  先回读原文再下"这是错"的判语。
- 批次 G 首条「跨仓 `1.0633` 需点头才改」**已落地**：`ColorGainKit/src/ColorMatrix/ColorSpaceRegistry.cs:785`
  现读 `1.0296`，`git log -S"1.0296"` 命中 `79c3f4b`（该文件 `git status` 干净）⇒ 只剩 push。
- `tools/BUILD_TOOLS.md` 两行过期按实测改：ffmpeg 写 `git-2026-08-06` 而 `ffmpeg.exe -version` 是
  `git-2026-10-11-edadf711a9`；`ultrahdr_app` 仍列「VS 自编译」而 `build_tools.ps1:8/215/348` 已移除它
  （并进了 `$obsolete`）⇒ 表行划废 + `artifacts/` 目录行改列现存件 + 那段"4. libultrahdr"加"已不执行"警示。

### 5. 接线与整轮（`full10`）

清单 74 → **75**：`$expectedScriptCount` + 运行器三种散文形态（第 ⑰ 针对账）+ 数组条目 + `TESTING.md` §3.5。
⚠ **第一轮被自己的条数锁拦下**：我插了"第 75 条"的注释块却**忘了加数组元素** ⇒
`运行器汇总：失败 3 项 … runner:脚本清单条数 74 ≠ 基线 75` —— 这道锁正是为这种事设计的（不是误报）。
补上元素后 `script-list=75 条（受管基线 75）`，子集轮 `全部通过`。

### 整轮读数（full10，日志 `tests/output/gates-round-2026-10-13-full10.log`；起跑 18:05 前后 / 收尾 18:48:06）

- 尺：`runner-self-sha256=2F006D117E640FBB lines=1419 bytes=136734`（起/收**一致**）；`shape-selfcheck=OK`；
  `script-list=75 条（受管基线 75）`；无 STALE、无漂移警告；磁盘 77.68 → 77.5 GB。
- 件：`FfmpegGui.dll=FCF8849390AA6786`、`ServiceProbe.exe=54B919EFFAAF44B6`、`UiTestHost.exe=DC2F23D5816BAC82`
  （**新基线值**；上一值 `3F7ED22634403E33` 随本批 `src/**` 改动作废）。
- 脚本段 **74 条 exit=0 / 1 条非 0**；汇总**失败 2 项，仍是先前点名的两条既有红**：
  `probe:runner 7/1`（沙箱 `cmd.exe`）· `script:_probe-test-group-coverage`（按设计红：7 零调用点 + `icc` 非清单调用点）。
- 本链路门禁轮内留痕：verbatim **22/0** · union **11/0** · reproducible **19/0** ·
  **probe-mirror-parity 28/0**（新锁首轮进清单即绿）· `_probe-gui-control-wiring` **82/0** · `verify-ps-compat` **13/0**。
- 参照件驱动的五条 UI 门禁轮内 `UI 测试汇总: N PASS / 0 FAIL` 命中 **5** 次；
  `bench 30/0`、`contract 153/0`、`cjk-hardcode 14/0`、`verify-gainmap-memory 16/0 SKIP=1`。
- ⚠ **留痕文件名教训**：本轮之前我用同一启动器**复跑了一次 full9**，写死的 `$log` 把第一轮证据
  原地覆盖（账本引用的文件名与内容错位）⇒ 启动器现在默认带时间戳 Tag，旧的那份已改名保住
  （`gates-round-2026-10-13-full9b-partial.log` = 漏数组元素那轮的现场）。

### 6. 这批仍没做的（按代价与授权排，不含糊过去）

- **`F-12`（探测链剩余 2 次 exiftool）未动**：`ColorIntentFactory:113` 仍走旧两步法抽 ICC。
  它要改产品探测路径 + 按账本原判据量「`--dry-run` 墙钟下降**且** exiftool 点名单元不减」⇒ 需要独立的
  计时 A/B 与 `decision`/像素链验收，不适合塞在装尺这批的尾巴上做半截。
- **两份拷贝的"合并"没做**（本批只装尺）：合并 = 让 `tests/core`/`tests/diagnostics` 那份消失或转成转发，
  需要执行面判据（否则等于把"没有执行者"这个根因原样搬走）。
- `UiHost.ResetAdvancedColorToAuto` 仍零调用点（删它之前要先给「选 `auto` 不足以复位高级三元组」那条
  规范事实找落点，现在只有它的注释记着）。
- `L-3b` 正样本 `test4.icc` 本机仍无（`find` 实测两份 lcms checkout 只有 test0/1/2/3/5）⇒ 那条 SKIP 依旧为真。
- push / tag / 本批提交：等用户点头（远端动作与提交都是逐次授权）。

---

## 批次 F（F-12 落地）+ ⑱ 探针面第二半（方向锁）+ L-3b 收口（2026-10-13 晚）

取证目录：`tests/output/f12/`（`toolshim` 源码 + `f12-exiftool-count.ps1` + `gps-exclusion-probe.ps1`
+ `mut13.py` / `mut75.py` / `mut-clone-dynamic.py` + 各臂日志）。全部 gitignored，但每条读数都有文件名可指。

### 1. F-12 改了什么（探测链剩余的第 1 次 exiftool）

| 文件 | 动作 |
|---|---|
| `ColorMapping/ColorSpaceIdentify.cs` | 新增 `IdentifyIcc(iccPath, hint) → (descriptor, name)`；`IdentifyIccSpaceName` 改成对它的一行委托 ⇒ "怎么认 ICC 空间"仍只有一份实现 |
| `FfmpegCommandBuilder.cs` | `ColorMetadata` 加 `iccDescriptor` 字段；探测核改调 `IdentifyIcc` 并把描述符**接回 struct**；新增公开入口 `ProbeInputIccDescriptor()`（返回 `Clone()`） |
| `ColorMapping/ColorSpaceDescriptor.cs` | 新增 `Clone()`（含 9 元素矩阵与四组色度数组的深拷贝） |
| `ColorMapping/ColorIntentFactory.cs` | 删掉旧两步法整块（`ExtractIccToTempFile` → `FromIccFile` → `TryDeleteIcc`），改为一次 `ProbeInputIccDescriptor(inputPath)` |
| `ExifToolService.cs` | 合并探测的取消/超时点名改成同时说出「ICC 一并失去 ⇒ 按无 ICC、无语义标签处理」（FIX_PLAN 那条"等价点名"风险条的兑现） |
| `ColorMapping/RawColorPipeline.cs` | 只改注释：它点名了被删掉的旧路径（`ColorIntentFactory` 走 `ExtractIccToTempFile`），改成"两种形态下 `SourceIccPath` 都指向已回收的临时件" |

### 2. 判据换形（撤回我自己先前引用的一条，并给替代读数）

**FIX_PLAN 写的「`--dry-run` 墙钟下降」量不到这件事** —— 实测：dry-run 一臂 `exiftool` 进程数
**before=1 / after=1（差 0）**，因为 `ColorIntentFactory.FromOptions` 在 dry-run **不可达**
（坐实 lane C1 §C1-8 的静态判断；也把我先前引用的 P1-a「dry-run 计时反映探测合并收益」的适用面否掉——
P1-a 省的那次在探测核里，dry-run 走得到；F-12 省的这次在装配层，dry-run 走不到）。
⇒ 判据换成三条可动的：**完整任务 exiftool 进程数 −1**、**完整任务墙钟下降**、**产物等价**。

取数方法：把 `FFMPEGGUI_EXIFTOOL_PATH` 指向一个「记录 argv 后逐字节转发」的 shim
（`tests/output/f12/shim`，.NET apphost）。**为什么不用日志行数**：lane C1 §0 注 1 已证
「`[exiftool]` 行数 ≠ 进程数」；**为什么不用 WMI 轮询**：只能给下界。
shim 自己的保真自检（用前必检，它自己是错误源的话整张表作废）：
stdout 逐字节相同（含 `-b` 的二进制与 `-json` 的文本）、退出码相同、一次调用恰记一行、
`SHIM_REAL` 缺失必须 rc=95 出声（不许把"没跑"记成"跑了"）、少传一段参数 ⇒ 输出立刻不同（比对有牙）。

夹具：320×240 PNG 内嵌 Display P3 ICC（584 B，读回 sha 与参照件 `p3.icc` **同**；夹具守卫：`<128 B` 直接停）。
A/B 用的是两个真实构建件换进同一个 `FfmpegGui.exe`（apphost 只认同目录的 dll）：
`before=FCF8849390AA6786`（full10 绑定的那枚，仍留在 23 份测试宿主目录里）/ `after=ECBCCEDFAB4577C2`。

| 臂（同输入同命令，只差 dll） | exiftool 进程数 | 完整任务墙钟 min-of-5 |
|---|---|---|
| png → png（引擎 auto） | 4 → **3** | 2.243 → **2.001 s**（−0.242） |
| png → png（`--color-engine legacy`） | 4 → **3** | — |
| png → jpg（引擎 auto） | 5 → **4** | 2.636 → **2.242 s**（−0.394） |
| **对照臂** `--dry-run` | 1 → 1（差 0） | 0.929 → 0.937 s（+0.008 ⇒ 本轮噪声地板） |

- 单进程定价（同一条合并探测 argv，best-of-7 单机）：**min 282 ms / 中位 289 ms**。
  ⇒ 与 P1-a 记的 **0.170 s** 不同，也与源码注释的 **850 ms** 不同 —— lane C1 §2 那条
  「两个口径差 5 倍、谁都没在 HEAD 复测」的未定案，本机今日有了第三个数；上表的墙钟降幅（0.24–0.39 s）
  与它同量级，**不是**与 0.170 s 同量级。所有按 0.170 s 折算的历史收益估算都要按这个口径重读。
- **产物等价**（两臂各跑一次，逐件对撞三样）：解码像素 sha256 同 / 读回 ICC sha 同 /
  exiftool 标签名集合同（png、jpg 两格都同，产物字节数也同）。
  ⚠ 刻意**不**比产物整件 sha256：exiftool 重写会带时间戳类字段，逐字节不同 ≠ 内容不同（本仓既有口径）。
- ⚠ **反空转闸（实踩过一次才加的）**：第一版计时臂中途崩掉，产品目录里留下旧 dll ⇒
  下一轮开头"备份新件"备份到的是旧件 ⇒ 两臂跑**同一个二进制**，读数会"几乎相等"，
  看上去像"这个改动不影响墙钟"。现在脚本开头硬拦 `在盘 == 旧备份` 并 throw，且换臂包在
  `try/finally` 里（崩了也还原）。上面那组"相等"的废读数没进任何结论。

### 3. 锁与变异（每条都有红态）

- 探针 `wire` 加 **5 条**（`wire` 93 → **98/0**）：前置两侧都取到描述符（任一侧 null 就点名，
  防"两边都空 ⇒ 相等"）· ① 与独立两步法逐字段相同（参照臂用**旧生产实现** `ExtractIccToTempFile` →
  `FromIccFile`，它仍被 QueueProcessor/GainMapDecoder 消费，不是我造的平行口径）· ①b 九元素矩阵
  **逐个精确相等**（两臂解析同一份 ICC 字节 ⇒ 容差 0）· ② 消费者写的四个字段（Name/Confidence/Nits/矩阵[0]）
  都不回流缓存 · ③ 装配的源描述符仍是 `IccMetric`+`IsIccDerived`。
  - 变异（重建两轮做动态实验）：把 `ProbeInputIccDescriptor` 的 `.Clone()` 摘掉 ⇒
    **②③ 两条红**（③ 也红 = 被写坏的那份直接流进了装配），还原后 98/0。
- `verify-color-wiring.ps1` 新增 **(13) 段，8 条结构锁**（纯静态、不起进程、剥整行注释后再匹配 ——
  本仓的注释会点名"被折叠掉的旧 API"，全文子串会把"注释提到"读成"代码在用"）：
  装配侧 `ExtractIccToTempFile` 命中 0 · 正向对照同正则在 QueueProcessor 命中 6 ·
  `ProbeInputIccDescriptor` 入口恰 1 · `.iccDescriptor?.Clone()` 恰 1 ·
  `meta.iccDescriptor = ` 恰 1（struct 按值改必须接回）· argv 确实带 `-b -icc_profile` ·
  取消/超时点名恰一条且**同时**说 `色彩标签+ICC` 与 `按无 ICC` · `GuessColorSpace` 兜底口径只有一份。
  - 变异六发，各只红该红那条（还原后全绿）：M1 摘 Clone / M2 装配侧退回旧两步法（红 2 条）/
    M3 探测核漏接回 struct / M4 点名删掉 ICC 后果 / M6 点名主语去掉 ICC / M5 复制出第二份兜底口径。
  - ⚠ **M4 第一版打偏**：只替换了前半句 `色彩标签+ICC`→`色彩标签`，尾巴「ICC 一并失去」还在 ⇒
    锁"没牙"是我判据写错（判字形不判语义），不是锁弱。改成要求同一行同时含 `色彩标签+ICC` **与**
    `按无 ICC` 之后，删后果、删主语两发都当场红。
  - ⚠ 还修了一条**过宽**判据：第一版写"所有 `被取消/超时` 行都要含 ICC"，实测 `1/3` 红 —— 该文件里
    ISO 探测、通用标签读取各有自己的取消行，与 ICC 无关。定点只管合并探测那一条。

### 4. ⑱ 探针面第二半：尺从 8 个 mode 扩到 31 个（方向锁），**合并仍未做**

`verify-probe-mirror-parity.ps1` 加了 `-Census`（用**同一批取样函数**，不另造尺）与**方向锁**段：
- 普查读数：`tests/*/Modes/*.cs` 共 **31** 份，**全部**在执行面有同名方法（零孤本）；
  **25 份零漂移**；6 份有漂，方向清一色「只在被执行那份有」（只镜像那份 = 0 条）；
  漂移标签总数：只执行面 **65** 条、只镜像 **0** 条。最大的三格：
  `ProbeSettingsSave` 56 vs 11（+45）、`ProbeWire` 99 vs 93（+6，含本批 5 条）、
  `ProbeColorMathAgainstZimg` 12 vs 5（+7）、`ProbeIccName` 9 vs 6（+3）、`ProbeContract` 92 vs 91（+1，已登记）。
- 方向锁（31 格逐格 + 3 条总账 + 1 条取样反控）：
  **镜像独有标签必须为 0**（正是本批踩过的事故形态：写进死代码 ⇒ 全绿但一条没跑）；
  执行面条数 **不得少于**镜像（少 = 执行面丢了断言）；一格都不许取样塌 0。
  ⚠ 刻意**不**判"两侧相等"：镜像按定案是冻结参照件，要求相等就是要求往死代码里搬断言。
  相等判据仍保留在原 8 个 mode 的受管段（含 `C20-8b` 那条登记差，复验恰好 1×1）。
- **标签身份改形（这一把尺自己也被实测打回一次）**：第一版把一条 `Check(...)` 里的**所有**字面量拼成
  一个标签 ⇒ 造出 **981 字符**的"标签"，两侧只要语句顺序不同就整条判成漂 —— 实测 `gamutfit` 报了
  3 条"镜像独有"假红（逐字节对撞见 `tests/output/f12/gamutfit_diff.py`：两边是同几条断言，
  差在 (f)/(g) 先后与一句详情文案）。⇒ 标签改成**只取第一个像名字的片段**。
  换口径后重验三例：T1 只改执行面文案 ⇒ 该 mode 红（且旧文案在镜像里成孤儿 ⇒ 方向锁也红，正确）；
  T2 只往镜像加断言 ⇒ 方向锁红；T4 两侧同步改 ⇒ **零红**（这把尺量「漂」不是量「变」）。
  本门禁判定数 **28 → 63**。

### 5. L-3b 收口：不是缺素材，是我清单里的路径写错（先前那条登记作废）

`iccname` 的 L-3 正样本清单原先写 `plugins/test_profiles/test4.icc`，而 vendored lcms 那个目录里
**根本没有 test4**（实测只有 test0/1/2/3/5）；仓内实存的是 **`testbed/test4.icc`**
（头实测 `class=spac data=RGB PCS=Lab`，61,020 B —— 与该段注释本意「含 spac 类 test3/test4」同族）。
⇒ 改路径，并把判据从 `accOk >= 9`（缺件恰好被余量吸收 = 分母自己缩小）改成
**`accMissing == 0 && accOk == 清单条数`**。读数：`清单 10、在位 10、收下 10` PASS，那条
`L-3 缺正样本 … SKIP` 消失，`iccname 17/0`。
⚠ 撤回先前那句登记：「本机两份 lcms checkout 只有 test0/1/2/3/5」—— `testbed/` 里 test0–test5 是全的，
缺的是我没核对第二个目录，不是上游要 fetch。

### 6. F-12 标题里的第 2 次 exiftool：按实测**作废**（不动它，所以不需要点头）

第 2 次是复制之后那次无条件的 GPS/XMP 显式删除（lane C1 的 C1-2，「谁点头：用户」）。
§4-E 的配方跑完了（`gps-exclusion-probe.ps1`；命令形态**从产品自己的 argv 里抓**，不手抄）：

| 目标容器 | 目标原有 GPS | 只靠排除式后 | 结论 |
|---|---|---|---|
| jpg | 5 条 | **5 条** | 排除式不够 ⇒ 显式删除必须留 |
| webp | 5 条 | **5 条** | 同上 |
| tiff | 5 条 | **5 条** | 同上 |
| png | 5 条 | **0 条** | 够用（它的复制命令自带 `-EXIF:all=` 复位） |
| jxl | — | 不判定 | 这一轮产品**根本没走复制步骤**（只起了 MPImage2 探测 + 合并探测 2 条） |

反空转成立：jpg/webp/tiff 三格都残留 ⇒ 读回口径是活的，「png 那格干净」不是"大家都干净"的同义反复。
⇒ "省 1 次 exiftool/任务"在默认出货族（jpg/webp/tiff）上**不成立**；最多只在 png 族省 1 次，
代价是给隐私动作加一条按容器分叉的规则（分错一次就是坐标泄露）。**本批不动它**，
并把 C1-2 从"待点头的性能项"改判为"实测否决"。顺带一条与 C1 表的出入待查：
jxl 臂这轮没跑复制/删除（C1 把 GPS 删除记成"与目标格式无关"），本轮不再补测，登记在此。

### 7. 本轮链路读数（子集轮 `tests/output/gates-round-f12-probes.log`，`-SkipScripts`）

`contract 153/0` · `wire 98/0` · `verdict 41/0` · `iccname 17/0`（SKIP 已消）· `plan 39/0` ·
`decision 4/0`（60 格：宣称未落实 0、跑了 tonemap 仍宣称 HDR 0、ICC 后置判据不符 0）·
`engine 35/0` · `metaraw 20/0` · `colormath 19/0` · `curve 44/0` · `matrix 15/0` ·
`settings 11/0` · `encoding 6/0` · `verify-ps-compat 13/0` · 产品构建 **0 警告 0 错误**。
整轮（75 条）与 23 份构建件指纹在下一节重绑。

### 8. 这批仍没做 / 要点头的

- **两份拷贝的合并或删除**：尺已覆盖 31 格，**合并本身**仍未做。三档（含代价）见下，等一句选择：
  ① 删 31 份镜像（`git rm`，一次性；实测"只镜像 0 条" ⇒ 按标签口径不丢断言，但注释/详情文案会丢，
  且 6 格差集要逐格确认；收益 = 本仓不再有"两份拷贝"这一类缺陷的土壤）；
  ② 镜像转成转发（把每个 `Probe*` 换成调 ServiceProbe 那份 —— 同一份数学获得**第二个执行者**，
  与"同一份数学只一份实现"相冲，先前定案已否过）；
  ③ 原样留着 + 方向锁看着（当前状态，零风险，代价是 31 份死代码继续长肉）。
- `L-*` 里 L-3b 已收口；跨仓同错那一档仍按前定（不在本仓动）。
- 提交：本批 **9 改 + 1 新**（含 `tests/output/f12/` 之外的 6 个产品/门禁文件）等点头；push/tag 不动。

### 9. full12：重绑基线（2026-10-13 晚，日志 `tests/output/gates-round-2026-10-13-full12.log`）

**先更正上一条**：那句「本批 9 改 + 1 新」是写早了（它描述的是装尺那一批）。本批收口时实况 =
**17 改 + 1 新（`tests/scripts/verify-probe-mirror-parity.ps1`），`git diff --shortstat` = 791 insertions / 45 deletions**。

- 尺：`runner-self-sha256=2F006D117E640FBB lines=1419 bytes=136734`（起/收**一致** ⇒ 与 full10 同一把尺，跨轮可比）；
  `shape-selfcheck=OK（17 针）`；`script-list=75 条（受管基线 75）`；无 STALE、无漂移警告；磁盘 67.08 → 66.48 GB。
- **新基线件 = `FfmpegGui.dll=ECBCCEDFAB4577C2`**（取代 full10 的 `FCF8849390AA6786`）；
  `FfmpegGui.exe=BE0A3023826123AE`、`ServiceProbe.exe=54B919EFFAAF44B6`、`UiTestHost.exe=DC2F23D5816BAC82`、
  `GainMapTestHost.exe=0B1510ADA80D8606`。23 份宿主内层 `FfmpegGui.dll` 逐份同 sha（重建后实测 23/23 相同）。
- 汇总 **失败 2 项，仍是先前点名的两条既有红**：`probe:runner 7/1`（沙箱 `cmd.exe`）·
  `script:_probe-test-group-coverage`（按设计红：7 个零调用点 + `icc` 非清单调用点）。其余 **73 条 exit=0**。
- 本批改动的门禁面读数（逐条，不压总数）：探针段 `wire 98/0`（**93 → 98**，F-12 的 5 条）、
  `contract 153/0`、`iccname 17/0`（**L-3 的 `缺正样本 SKIP` 已消失**）、`decision 4/0`（60 格零不符）、
  `plan 39/0`、`verdict 41/0`、`engine 35/0`、`metaraw 20/0`、`geometry 52/0`、`bench 30/0`、`gamutfit 19/0`。
  脚本段 `verify-color-wiring **142/0 → 150/0**`（(13) 段 +8）、`verify-probe-mirror-parity **28/0 → 63/0**`
  （方向锁 31 格 + 普查开关）、`_probe-matrix-structure 38/0`（A30 行锚重钉 2099→2132 后复绿）、
  `verify-gainmap-host 18/0`、`verify-webp-anim-probe 44/0`、`verify-decision-delivery`/
  `verify-format-regression 19/0`/`verify-color-caps 16/0`/`verify-metadata-privacy 7/0`/
  `verify-gainmap-memory 16/0`、`_probe-cjk-hardcode 14/0`（**余量未动** ⇒ 本批没有新增中文硬编码行）、
  `verify-ps-compat 13/0`、UI 三锁 `22/0`、`11/0 @419`、`19/0`。产品构建 **0 警告 0 错误**（23 目标各一遍）。
- ⚠ **上一轮（full11）的三条新红已逐条归因，且有一条确实是我的**：
  · `_probe-matrix-structure` —— **确定性、属本批**：F-12 往 `FfmpegCommandBuilder.cs` 插了 33 行 ⇒
    `metadata-core` 的 A30 行锚 `2099 → 2132`。按既有惯例重钉并附成因（`grep -n` 复核实况行 + 确认该
    字面量全文件唯一），复跑 `38/0` ⇒ 这条红是**设计生效**，不是误报。
  · `verify-gainmap-host` 的 `D16d 多线程不慢于单线程` 与 `verify-webp-anim-probe` 的 `③(b)/③(d)` ——
    **争用窗口**：判据不是"哪一格红"（两轮红在**完全不同的格**），而是**每格耗时**：该门禁自己的注释写
    "实测每个用例 2~5 s 自行结束；60 s 是余量上限"，红态同一格 34.1 s / 触顶 60.3 s 无退出码，
    绿态回到 1.2–2.8 s。full12 两格都 `18/0`、`44/0` ⇒ 与本批代码无关。
    ⚠ 归因过程中踩到一条**新的臂失效**：为了跑「交错 A/B/A + 改动前二进制」去取旧件，发现
    23 份宿主的 `-t:Rebuild` 已把全仓那枚 `FCF8…` **全部覆盖**，而我留的换臂备份又被更早一次中途崩掉的
    换臂污染成同一个哈希 ⇒ 换臂脚本的**反空转闸**当场拒绝（两臂同件即 throw）。
    教训 = 对照用的旧二进制要在重建**之前**落成带哈希命名的私有快照；没有它就不许写"两臂一致所以非我"。
    所以这两条红的归属证据是「同件复绿 + 耗时对照」，**不是** A/B/A —— 报告里按前者引用。

### 10. 现在仍未做的（不含糊过去）

- **探针面 31 份镜像的合并/删除**（§8 三档，等选）——本批把尺扩到了 31 格，但没动镜像本体。
- `UiHost.ResetAdvancedColorToAuto` 的落点问题（同上批登记，未变）。
- **F-10 `smpte170m` 传递函数**：仍按"等一手标准原文"挂着，本批未动（冲突口径未解，见 FIX_PLAN F-10）。
- push / tag / 提交：逐次授权，等用户点头。

---

## 10. 本批尾巴上的四件补做（2026-10-13 深夜，full12 之后；用户令"没做完的去做完"）

**① (a) 的"没量吞吐"那一半 —— 量出一个真缺陷并修掉**
欠账原文：性能提案 (a) 的 HDR→SDR 表快路径只在 **16-bit** 出口量了吞吐，8-bit 交付出口只量了等价性。
取数工装 `tests/output/f8bench8/`：同 spec 构造规则、同合成帧、同"单核 min-of-3"，只把被计时的内核
从 `TransformRgb48le` 换成 `TransformRgb48leToRgb8`。

| 8-bit 交付出口 | 修前 MPix/s | 修后 MPix/s | 表 vs 解析增益（修后） | 同 spec 的 16-bit 出口（修后） |
|---|---|---|---|---|
| PQ→sRGB（HDR→jpg/png 主力） | 22.7 | **64.0 ~ 69.9** | 8.2 ~ 8.6× | 135 ~ 150 |
| sRGB→P3 | 16.4 | **45.8 ~ 49.2** | 4.4 ~ 5.3× | 89 ~ 97 |
| BT.2020→sRGB | 14.8 | **27.8 ~ 61.3** | 3.9 ~ 5.2× | 89 ~ 139 |
| sRGB→PQ（编码侧 ⇒ 必须关表） | 5.5 | 4.3 ~ 5.7 | **1.0×**（两臂同解析 ⇒ 自洽） | 4.0 ~ 5.2 |

- 根因（读码即得）：`ColorKernels.EncodeTo8` **无条件**先算 `curve.FromLinear(linear)`（幂函数 + 分段），
  再在 `oetf != null` 时把结果**扔掉** ⇒ 每像素白做三次 pow。那 6× 的"16-bit 比 8-bit 快"不是位深属性，是这颗白算。
- 修法：两支各算各的（有表只做 `EncodeLerp`）。**输出逐字节不变** —— 凭据是 `C20-8b`（送表 ≡ 不送表，容差 0）
  照旧 PASS + 整轮里的像素链门禁复跑，不是我一句"应该一样"。
- 看守：`bench` 加两格 8-bit 出口档 ⇒ `bench 30/0 → 37/0`（前置 + ≥25MPix/s + 增益 ≥2× + 档位数量）。
  阈值钉在**修前值之上**（25 vs 16.4~22.7）：变异 M9 把白算的 pow 加回来 ⇒ 实测两格红（20.1 / 11.7），还原 37/0。

**② 8-bit 出口的快路径判据提成具名方法（同一轮的第二条锁）**
`TransformRgb48leToRgb8` 原先把"哪些算子要关表"裸写在循环前一句 `if (tbl && (encScaleOn || toneOn || srcHlg || dstHlg))` 里 ⇒
①测试侧只能再抄一份条件；②**它和自己的注释已经漂过一次** —— 注释声称 `encScaleOn` 已摘掉，代码里它一直在
（真实只摘过 `scaleOn`），而 `TableFastPathOperatorsAllow` 的文档又把 8-bit 条件引成少一项的另一副面孔
⇒ 两处注释都在描述一个不存在的实现（这正是"注释自洽可以为假话"的实例）；③它与 16-bit 判据只差 GMO 一轴，
而这件"只差一轴"的事实代码里无处可查。
- 处置：新增 `ColorKernels.TableFastPathOperatorsAllowRgb8(spec)`，内核改调它；注释按代码实况重写并写明
  `encScaleOn` 为什么**必须留**（编码侧表是线性域均匀采样 ⇒ 实测差 1457 码@16bit / **8-bit 出口 6 码**）。
- 行为锁：contract 的 **`C20-AX`** 六轴逐格点名两条判据（GMO 那轴实得 `16-bit=False / 8-bit=True`）
  ⇒ `contract 153/0 → 160/0`。结构锁：`verify-color-wiring (13)` 加 3 条（具名判据命中 2、
  16-bit 侧正向对照也 2、裸写条件残留 0）⇒ (13) 由 8 条变 **11 条**，门禁合计 `142/0 → 150/0`（F-12 的 8 条 + 这 3 条… 实为 11 条，读数见整轮）。
- 变异：M7 把 GMO 一轴合成一条 ⇒ `C20-AX GamutMap` 单条红；M8 把条件裸写回去 ⇒ (13) 两条红；均逐字节还原复绿。
- ⚠ 顺带修了 #75 自己的两处口径：① 差集元素的字段名 `Count` 会被 PowerShell 的成员枚举语义吃掉
  （打印出来"3 条/共 3 次"而不是"3 条/共 6 次"）⇒ 改名 `Times`；② 这把尺量的是**源文本**，
  循环体只有一份源文本 ⇒ 两格共用同一模板时次数只算 1，"每一档都还在"**不能**指望它，
  改由 `8b 档位数量` 那条**运行时**断言钉（跑过的格数 == 声明格数 == 2）。登记表现在是带计数的
  `@{Prefix;Labels;Times}`：`C20-8b` 1×1、`C20-AX` 7×7、`8b ` 4×4 ⇒ `probe-mirror-parity 63/0 → 65/0`。

**③ `UiHost.ResetAdvancedColorToAuto` 的落点 —— 收口（连带删掉死包装）**
当时不敢删，是因为"选 `auto` **不足以**复位高级三元组"这条产品行为（`MainWindow.xaml.cs:3594-3600` 的 `auto`
分支只清冲突提示；色域联动本身按值查下标赋值 ⇒ 同值不触发 `SelectionChanged`）只有那个死包装的注释记着。
⇒ 新增 **`J8d`** 把事实变成断言（把高级 trc 设为非 auto → 选 `ColorSpaceCombo=auto` → 断言 trc **保持原值**；
结尾恢复控件，不污染后续组），事实有落点之后才删死包装（`UiHost.cs` −23 行）+ 重跑生成器（`ui-sweep` +30 行）。
⇒ verbatim **22/0**、reproducible **19/0**、union **11/0 @421**（`$expectedOldCount` 419→421，同前例附成因，
且 3c 新旧集仍相等才抬）；`UiTestHost` 运行时 **421 PASS / 0 FAIL**。
⚠ 我先前**直接从 bash 跑** `verify-ui-host.ps1` 拿到一条 `I3 ProPhoto保真 :: GetFaithfulIcc 返回 null` 红 ——
那是本仓已定论的 `TMP=/tmp` 环境红（继承 MSYS 的 TMP），换回整轮启动器的姿势即 0 FAIL；不是产品问题，也没拿它当结论。

**④ `F-10 smpte170m`：按 FIX_PLAN 的准入条件保持"未判定"，本批不动代码**
计划写的是"必须先取 SMPTE 170M / ITU-R H.273 表 3 一手文本，取到前登记为未判定，不许按 4.20% 改代码"。
本轮把两条取数路都走了：
- 本机 checkout：`tools/src/**` 的 C/C++ 源里按 `transfer…170m` / `170m…(gamma|2.2|0.45|2.4)` 搜 ⇒ **0 命中**；
  实存的是三个**互不相同**的实现口径（libavif `colr.c` 无趾段 0.45 幂、zimg `t=smpte170m` 纯 γ2.4、
  DNG SDK 给 709/601 同一条分段常数）⇒ 实现之间不能互相当标准。
- 网络：命中的全是**二手转录**（RFC 9559 把 H.273 表 3 抄进 Matroska、HarmonyOS 头文件、博客），
  按本仓纪律（二手聚合页不能当标准/能力结论）**不判**。
⇒ 现状维持 `smpte170m` 挂纯 γ2.2，账上继续记"未判定 + 要的是 SMPTE ST 170 / ITU-R BT.601-7 正文"。
   要推进需要一个能读到正文的入口，不是再多几条二手对照 —— 这条不在本轮凑。

**构建面**：产品 + 7 个 UI 工程 + ServiceProbe 全部 **0 警告 0 错误**。①② 都动了产品代码 ⇒
`FfmpegGui.dll` 指纹再变，**full12 那枚 `ECBCCEDFAB4577C2` 已过期** ⇒ 基线须在补跑的那一轮上重绑。

### 11. full13：重绑基线（2026-10-13 23:13:51 收，日志 `tests/output/gates-round-2026-10-13-full13.log`）

- 尺：`runner-self-sha256=2F006D117E640FBB lines=1419 bytes=136734`（起/收一致 ⇒ 与 full10/full12 **同一把尺**）；
  `shape-selfcheck=OK（17 针）`；`script-list=75 条（受管基线 75）`；无 STALE、无漂移；磁盘 64.59 → 63.19 GB。
- **新基线件 = `FfmpegGui.dll=6210C29DC28A18F4`**（取代 `ECBCCEDFAB4577C2`）；
  `FfmpegGui.exe=BE0A3023826123AE`、`ServiceProbe.exe=54B919EFFAAF44B6`、`UiTestHost.exe=DC2F23D5816BAC82`、
  `GainMapTestHost.exe=0B1510ADA80D8606`。23 份宿主的 `FfmpegGui.dll` 逐份同 sha（实测 24 份含产品 bin 全等）；
  已在重建**之前**把该件另存 `tests/output/f12/FfmpegGui.6210C29DC28A18F4.dll`（上一轮教训：重建会吃掉对照臂）。
- **73 条 exit=0 / 失败 2 项，仍是先前点名的两条既有红**：`probe:runner 7/1`（沙箱 `cmd.exe`）、
  `script:_probe-test-group-coverage`（按设计红：7 零调用点 + `icc` 非清单调用点）。
  ⚠ full11 那两条负载计时红本轮**没有复现**（`verify-gainmap-host 18/0`、`verify-webp-anim-probe 44/0`），
  且本轮新增的计时档也在同一次负载下过了 ⇒ 与 §9 的归因一致（争用窗口，不是代码）。
- 本批改动相关的读数（逐条，不压总数）：
  `contract **160/0**`（153 → +7 条 C20-AX）、`wire **98/0**`（+5 条 F-12）、
  `selftest 40/0`、`iccname **17/0 且 L-3 的缺正样本 SKIP 已消失**`、`decision 4/0`（60 格零不符）、
  `metaraw 20/0`、`bench **37/0**`（30 → +7：8-bit 出口两格 ×3 条 + 档位数量 + 前置）、
  `verify-color-wiring **153/0**`（142 → +11：F-12 的 8 条 + 8-bit 判据 3 条）、
  `verify-probe-mirror-parity **65/0**`（28 → 65：方向锁 31 格 + 登记制带计数）、
  `_probe-matrix-structure 38/0`（A30 行锚 2099→2132 重钉后）、
  UI 三面 `verbatim 22/0`、`union 11/0 @421`、`reproducible 19/0`、`verify-ui-host 421 PASS / 0 FAIL`、
  `_probe-gui-control-wiring 82/0`、`verify-ps-compat 13/0`、`_probe-cjk-hardcode 14/0`（**余量未动**）、
  `verify-format-regression 19/0`、`verify-color-caps 16/0`、`verify-metadata-privacy 7/0`、
  `verify-decision-delivery pass=1/0`、`verify-gainmap-memory 16/0`、`verify-usability-e2e 84/0 (81 格)`。
- 未提交量 = **17 改 + 1 新**（`tests/scripts/verify-probe-mirror-parity.ps1` 未跟踪）。
  本轮之后仍未做的只有两件，都要用户开口才动：① 31 份镜像的合并/删除（§8 三档）；② 本地提交的分笔授权。

## 12. 探针面镜像 5 工程的整体删除 + 第 75 条换形（2026-10-14）

**授权**：维护者在两档里选了「**整体删 5 个镜像工程**」。⚠ 我先给的另一档（"只删那 31 份镜像文件"）
是**基于错误的结构假设**提出的 —— 那些 `Modes/*.cs` 是各工程**自己组体的切片**（组体 + 入口 + csproj 是同一条链），
不是可以单独扔掉的副本；发现后当面向用户更正并重新选档，本节按选定的那一档执行。

**删了什么**：`git rm -r tests/{core,color,diagnostics,engine,geometry}` + `tests/scripts/_gen-mode-split.ps1`
= **46 份文件 / 7,761 行**（31 份 `Modes/*.cs` + 5 份组体 + 5 份 `Program.cs` + 5 份 csproj，另加生成器），
连带 `rm -rf` 掉各自的 `bin/obj`（否则陈旧 dll 仍被"同 sha"清点算进来）。
**谁认领那 31 个 mode**：`tests/Tests.Shared/TestGroups.cs` 删掉 `core/color/engine/geometry/diagnostics`
五个组键，新增 **`["ServiceProbe"]`** 一组认领（组名 = 工程目录名；运行器 `$Probes` 调的就是它的执行面）。

**第 75 条 `verify-probe-mirror-parity.ps1` 换形（判定 65 → 108，实测 `108/0`，纯读源文本、不需要重建）**：
- **② 存在锁**：5 个目录不在盘 ＋ 全仓 `tests/**/Modes/*.cs == 0` ＋ 切片生成器不在位
  ＋ **`git ls-files` 里五目录 0 条目**（想把镜像用 git 捞回来也会红）。
- **③ 条数锁**：镜像当参照件时，"执行面丢没丢断言"是它顺带兜住的；参照件没了 ⇒ 换成把
  **56 个派发 mode 各自的 `Check` 标签条数**逐名钉死（合计 **813**，**只许升不许降**）。
  塌 0 的 5 个 mode（`color`/`icc-dump`/`tonemapcurve`/`ipc-server`/`piperaw-server`）逐名登记为
  "取数器 / 跨进程驻留端 ⇒ 判据在调用脚本里"，并由 **③-d** 要求"实塌 0 的集合 == 登记集合"
  ⇒ 取样器整体失配时这里先接住，不会变成"一堆 0 == 一堆 0"的恒绿。
- **④⑤ 剩余双拷贝**：`tests/{encode,gainmap,io,metadata}` 仍是 ServiceProbe 的镜像 ⇒
  ④ 把 **16 个同名 `Probe*`**（其中 15 个有执行面对应）的第二份定义点**逐名钉死**（新增第二份 = ④-z 当场点名，
  合并掉也必须回来改登记）；⑤ 方向锁（镜像侧独有标签必须 0、执行面 ⊇ 镜像）＋ ⑤-a「两侧同塌 0 的格
  恰好 `ProbeIccDump`」＋ ⑤-c 总账。实测今日：镜像独有 **0** 条，执行面比镜像多的三格
  = `ProbeAvifGainMap` 14 vs 11、`ProbeMetadataRaw` 20 vs 17、其余 12 格相等。
- **变异验牙七支**（各只红该红那一格 / 那一格+它的总账，跑完逐字节还原相等，脚本 `tests/output/f12/mut75b.py`、`mut75c.py`）：
  M1 删执行面一条 Check ⇒ `③ peak 8→实 7` + `③-e` ｜ M2 往镜像加一条 ⇒ `⑤ ProbeGainMap` + `⑤-c` ｜
  M3 加一个未登记的派发 `case` ⇒ **只** `③-a` ｜ M4 新增第二份 `ProbeMatrix` 定义 ⇒ **只** `④-z` ｜
  M5 重造 `tests/core/Modes/contract.cs` ⇒ `② 目录` + `② Modes` ｜
  M7 把 `tests/encode` 写进"该消失的目录" ⇒ `② 目录` + `② git 索引（实 3 条，逐名点名）`
  —— **这一支专门证明 git 那枚针有牙**（M5 造的是未跟踪文件，打不到它）。
  **M6 反控**：执行面加一条 Check **同时**抬该格登记 ⇒ **全绿**（"只许升"这条路走得通、锁不是恒红）。
- ⚠ **总量判据改过一次**：第一版另钉一个独立总数 813 ⇒ M6 反控被自己绊红（抬一格要联改两处），
  现改成"登记表各格之和" ⇒ 抬一个 mode 只改它自己那一格。改后 M6 复跑 `108/0`。
- ⚠ **修掉一条会让方向锁双双恒真的取样坑**：方法体正则原先不排除**表达式体包装**
  （`internal static void RunBitDepth(string[] a) => ProbeBitDepth(a)…` 被当成定义点 ⇒ 从包装行往后找 `{`
  ⇒ 该格条数**塌成 0**；实测 `tests/encode` 的 `ProbeBitDepth` 被量成 0，真值 7 ⇒ "SP ≥ 镜像"与
  "镜像独有 0"同时恒真）。现：签名行含 `=>` 不算定义点，且定义点**按文件去重**。
- ⚠ 本仓踩过的原始事故形态（本条存在的理由）：「断言只写进镜像 ⇒ `contract` 报 137/0 全绿而那条**一次没跑**」。

**第 69 条 `_probe-test-group-coverage.ps1` 的两处解析失配（都是"转红"方向，不是假绿，但红得没有信息量）**：
① 组表正则用 `[a-z0-9\-]+` ⇒ **不认带大写的组名** ⇒ 新组整体隐形，表现为 `① 20 个 mode 无组认领` +
   `④-c 并集塌到 35`（不是覆盖真丢）；② ⑤ 只认 `mode == "x"` 这一种分派形态 ⇒ `ServiceProbe/contract, …`
   31 条被判"声明了却没实现"。现：字符集放宽到 `[A-Za-z0-9\-]+`；⑤ 同时认 `case "x":`
   （同文件 `Get-OwnerHint` 早就认这个形态 ⇒ 两处口径此前不一致）。
牙齿复验 **M8**：把 `case "gamutfit":` 改名 ⇒ **只** ⑤ 红并逐名点名，基线那 2 条红未动。
修后实测 **18/2**，两条红（**7 个零调用点** + `icc` 非清单调用点）与删除前 full13 的日志**逐名同集**，
差别只在 `gamut`/`pq` 的归属由「组 engine/color」变成「组 ServiceProbe」。

**构建面**：重建清单 **23 → 18 个目标**（1 产品 + 17 测试工程；逐名清单落在
`tests/output/f14/rebuild-18.ps1`，`.sln` 仍只登记 4 个工程 ⇒ "必须逐工程 `-t:Rebuild`" 这条没变）。
实测 **18/18 RC=0、每个目标 0 警告 0 错误**；
`FfmpegGui.dll = 5F0E0B22E4FFD48C` 共 **18 份同 sha**（17 份宿主 Release + 产品 Release bin）。
另有 4 份不同 sha 是**本批未触及的陈旧件**（`tests/ServiceProbe/bin/Release/.../publish/`、
`tests/{GainMapTestHost,UiTestHost}/bin/Debug/`、`src/.../bin/Debug/`）⇒ 处置与 full13 一致，不参与"同 sha"对账。
⚠ 换臂快照在重建**之前**落盘：`tests/output/f14/FfmpegGui.<sha16>.dll`（含 full13 那枚 `6210C29DC28A18F4`）
—— 上一轮的教训：`-t:Rebuild` 会把全仓旧件吃掉，事后无法对账。

**本轮之后仍未做**：① `tests/{encode,gainmap,io,metadata}` 那 15 个同名镜像方法的删除（**要点头**；
实测镜像独有 0 条 ⇒ 现状安全，但它们是死代码）；② full14 整轮重绑基线（读数见下一节）；③ 本地提交的分笔授权。

⚠ **运行器里描述第 75 条的那段注释（`_run-step3-gates.ps1:1270-1293`，含"镜像 = `tests/core/Modes/*.cs`
+ `tests/diagnostics/Modes/*.cs`"、"比 8 个 mode 的多重集"、"变异四支"等）本批刻意没改** —— 动运行器 ⇒
`runner-self-sha256` 必变 ⇒ 历史轮"同一把尺"的前提失效（本仓既有规矩）。这把尺要留给"下一批确实要改
运行器行为"的时候；本批的代价只是一段**过期注释**（判据本身已在上面更正）⇒ 在此点名，不当作"文档已同步"。

## 13. full14：镜像删除后重绑基线（2026-10-14 00:33 起跑 / 01:08:32 收尾，日志 `tests/output/gates-round-2026-10-14-full14.log`）

- **尺未换**：`runner-self-sha256=2F006D117E640FBB lines=1419 bytes=136734` 起/收一致 ⇒ 与 full10/full12/full13
  **同一把尺**，跨轮读数直接可比（本批刻意没动运行器，见 §12 末段）。
  `shape-selfcheck=OK（17 针）`；`script-list=75 条（受管基线 75）`；无 STALE、无漂移警告、`.gates.lock` 正常释放；
  磁盘 60.62 → 60.51 GB（6 GB 闸门未触发）。
- **新基线件 = `FfmpegGui.dll=5F0E0B22E4FFD48C`**（取代 full13 的 `6210C29DC28A18F4`）；
  `FfmpegGui.exe=DBA61AA8F2DB90F3`、`ServiceProbe.exe=C5C6E255874B4189`、`UiTestHost.exe=52FC29B42FF64FF3`、
  `GainMapTestHost.exe=31152D6432E03BF4`。18 个目标 `-t:Rebuild` **18/18 RC=0、每个 0 警告 0 错误**，
  18 份宿主内层 `FfmpegGui.dll` 同 sha；对照件在重建**前**已另存 `tests/output/f14/FfmpegGui.<sha16>.dll`。
- **失败 2 项 = 与 full13 逐名同集的两条既有红**，没有新增：
  ① `probe:runner 7/1`（沙箱把子进程认成 `cmd.exe` 的既有假红，本仓定论"不为它改探针或产品"）；
  ② `script:_probe-test-group-coverage`（**按设计红**：7 个零调用点 mode + `icc` 非清单调用点，逐名点名）。
- 探针段 27 个 mode 除 `runner` 外全绿：`contract 160/0`、`wire 98/0`、`verdict 41/0`、`plan 39/0`、
  `curve 44/0`、`matrix 15/0`、`colormath 19/0`、`engine 35/0`、`gamutfit 19/0`、`selftest 40/0 skip1`、
  `iccname 17/0`、`metaraw 20/0`、`thumbnail 40/0`、`bench 37/0@60.7s`、`geometry`、`decision 60 格 0 不符`。
- **本批改动的轮内读数**：`verify-probe-mirror-parity` **108/0 @7.9 s**（旧形态 65/0 @0.1–0.3 s ⇒
  成本上升，静机复测 8.8/8.2/7.1 s，脚本段预算未动、不触超时）；`_probe-test-group-coverage` **18/2**；
  `verify-color-wiring 153/0`、`_probe-cjk-hardcode-scan 14/0`（**余量未动**）、`_probe-matrix-structure 38/0`、
  `verify-ps-compat 13/0`、UI 三锁 `verbatim 22/0` / `union 11/0 @421` / `reproducible 19/0`、
  `verify-ui-host 421 PASS/0 FAIL`、`_probe-gui-control-wiring 82/0`、`verify-gainmap-host 18/0`。
- ⚠ **一处口径必须说清，否则会被读成漂移**：第 75 条钉的是**源文本调用位**（`settings` 56 / `ipc` 13 / `plan` 18 /
  `bench` 9…），而轮内探针打印的是**运行次数**（`settings` 11 / `ipc` 10 / `plan` 39 / `bench` 37…）——
  循环体只有一份源文本、运行时按格展开 ⇒ **两个数天然不等，不是回归**。判据文本里也写了这句（`口径=源文本调用位不是运行次数`）。
- ⚠ **PS 双宿主**：`verify-ps-compat` 只保证"能解析 + 无 PS7 专属语法"。本条另做了一次 **PS 5.1 实跑**：
  `powershell -NoProfile -File …` ⇒ `108/0`、rc=0、11.0 s（负载下）⇒ 双宿主"可跑"是量过的，不是声称的。
- 未提交量 = **5 改 + 47 删**（47 = 46 份镜像文件 + `_gen-mode-split.ps1`）；未跟踪 0。
- 本批之后仍欠（都在 §12 与下面"仍未做"里点名，不另开口径）：**提交分笔授权**；剩余 4 份镜像工程
  （`tests/{encode,gainmap,io,metadata}` 的 15 个同名方法）删不删；7 个零调用点 mode 的接线或删除；
  `F-10` 一手标准原文；`F-13` 常驻 no-op；`F-8` 端到端量级。

⚠ **本批违反了「长验收轮期间不许改被测树」这条纪律，如实点名并给出为什么这一轮仍成立**：
我在 full14 轮中（00:37:30，构建完成于 00:26–00:27、轮起跑约 00:33）改了 `tests/Tests.Shared/TestGroups.cs`
里**一行注释**的日期（`2026-10-13` ⇒ `2026-10-14`，写错的事实不能留）。三条论证：
① **绑定的被测件不可能含这个文件**：`src/FfmpegGui/FfmpegGui.csproj` **没有任何 `ProjectReference`**，
   引用 `Tests.Shared.csproj` 的只有 13 份**测试宿主**工程 ⇒ `artifact-sha16 FfmpegGui.dll=5F0E0B22E4FFD48C`
   与这行注释无因果（这一点是 grep 工程引用表实测的，不是推断）。
② **门禁读的是源文本**：唯一消费 `TestGroups.cs` 的 #69 在轮内跑于脚本段（晚于 00:37）⇒ 它读到的是**改后**文本，
   输出的 18/2 归属当前文件状态。
③ **代码侧改动全部早于构建**：`git diff` 过滤注释行后只剩"5 个组键删除 + `["ServiceProbe"]` 新增"这一处，
   而那处发生在 `-t:Rebuild` **之前** ⇒ 宿主二进制与它一致。
⇒ 结论：full14 的**基线归属成立**；但正确做法仍是**先改完再起跑**。若哪天要严格到"没有任何源文件比 dll 新"，
   重建会改掉宿主目录里那枚 `FfmpegGui.dll` 的时间戳与 sha ⇒ 反而要重跑整轮 —— 这一轮的取舍在此写明，让读者能复核。
