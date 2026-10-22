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
## 14. 载体换成规范 50828 + ⑮ GPS 不降级臂 + full18 整轮（2026-10-17）

用户判据原话：「我要求的是标准 dng，不是私有实现 … dng 实现上不应该掺杂任何你所谓发明的实现」。
本轮把上一批自造的东西全部清掉，只留规范位。

### 14.1 删除自造容器，改走规范位

- **删**：`BuildClosureBlock`（'DMSL' 记录表 + null 结尾厂商名，写在 `DNGPrivateData` 里）、
  `SlabsFromDngClosure`、`SetPrivateData` 调用点。规范把 DNGPrivateData 定义为"相机厂商给自己
  的 raw 转换器留的私有数据"，且要求块内偏移一律**相对该块起点** —— 原文件的 TIFF 偏移相对的是
  原文件基址 ⇒ 搬进去即违规；且规范允许任何应用丢弃它（"只有本工具读懂"正是它不该用的原因）。
- **立**：`OriginalRawFileData`(50828) 按规范 8 块头 / **大端** / 每 64 KiB 一个独立 zlib 流，
  嵌**整个源文件**；`OriginalRawFileName`(50829) = 输入基名；`OriginalRawFileDigest`(50973)
  **不由我方计算** —— SDK 在 `dng_image_writer.cpp:8674` 调 `FindOriginalRawFileDigest()`
  （`dng_negative.cpp:2460`）对将落进 50828 的那份数据取 MD5。
- 块表基准两处口径由**第二个独立实现**裁决（exiftool `lib/Image/ExifTool/DNG.pm::ProcessOriginalRaw`
  用 `hdrLen = 4*(n+2)`、`p2 = pos + Get32u(pos+4)` ⇒ 以 ForkLength 字段起点为基准）。
- 读侧的 `head/tail` 两段**降级为纯内存窗口**（`ReadRawMetaWindows`，上限改名 `kMetaHeadCap`/
  `kMetaTailCap`）：只服务 `-idc` 直接读 RAW 与预览定位，产物里不留任何痕迹。
  `-idc` 取数顺序 = 先解 50828、取不到才当 RAW 读 ⇒ `origin` 是 `arw` | `dng-embedded-original`。
- 审计过"产物里还有没有我方发明的东西"：`dngtool.cpp` 里对 negative/profile 的调用全部是 SDK
  标准 setter（`SetOriginalRaw*` / `SetBlackLevel` / `SetActiveArea` / `SetCameraCalibration` /
  `profile->SetToneCurve` …），无裸标签注入、无 XMP/ApplicationNotes 自写。

### 14.2 一处自我撤回：源含 GPS 不许把整条搬运关掉

上一轮我在 dngtool 里做成"源含 GPS(IFD0 0x8825) ⇒ `carryRawMetadata=false`"，另加了 `-embed-original`
开关当逃生口。**这条被三点否证，已整支撤销**：
① 拿降级当修复 —— 一个标签关掉拍摄字段/嵌入原件/预览三段能力；
② 与本仓既有裁法冲突 —— `QueueProcessor.DngCarriesRawMetadata` 早就裁定"只有整组描述性元数据
   都不要才放弃搬运，仅 GPS 时照搬"（与 cjxl/jbrd 同一条），工具层单方面拒绝等于跟产品日志
   "本次仍搬运"打架；
③ 新加开关违反"默认行为不留旋钮"。
⇒ 现形：**照嵌 + 两层响亮点名**（产品日志说明代价、工具 stderr 打 ASCII 键
`gps-in-embedded-original=1`），严格剥除走**已有**的 `-no-raw-meta`。`-embed-original` 与
`forceEmbedOriginal` 形参/局部量/解析分支全删。
实测四臂（`tests/output/gate77-gps-arm.log`）：GPS 源编码 rc=0、解回的嵌入件与源 SHA256 全等
`137adfdb14ec7a41…`、产物可见标签 `-GPSLatitude` 为空、嵌入件内 GPS 读回 35.6812 与源同值。
本机四件真件全无 GPS ⇒ 夹具 = 7S 副本 + exiftool 现场注入 GPS，注入本身作前置判据（红则点名）。

### 14.3 第 77 条：② 组换形 + ⑮ 组新增 —— 32/0

`tests/scripts/verify-dng-metadata-transport.ps1`（**32 判据，实测 32/0 @33.1 s**，
日志 `tests/output/gate77-timed.log`、`gates-round-2026-10-17-full18.log` 内轮读数 32/0 @34.2 s）：
- ②a 长度相等、②b **exiftool 解回的字节与源同 SHA256**（合规性由第二个实现裁决；
  6,160,384 B → 5,880,033 B = 95.4%，无损 JPEG 出口）、②c 50829/50973 同现**且 `DNGPrivateData`
  必须缺席**、②d DNG→DNG 携带后仍可解回同哈希。
- ⑤a 负控同步换形：`-no-raw-meta` 臂的嵌入名必须为空。
- ⑮ 组四臂见 14.2。变异两支：复刻"GPS ⇒ 关整条搬运" ⇒ **只红 ⑮a/⑮c（30/2）**；
  变异第一版把赋值插在**裸 fprintf 之外**（那句 `if` 没花括号）⇒ 对所有源都关搬运 ⇒
  **15/17**（②③④⑦⑧⑩⑪ 一起红）。后者不算验牙（落点错），但它意外当了 17 条的正控 ⇒ 记为
  [[feedback-adversarial-fixture-order]] 第十七种形态。跑完 `cmp` 证源码逐字节还原相等。

### 14.4 顺手修掉 `-verify` 的两处谎话（改动由两臂分叉证明）

- 旧文案无条件打印 `✅ JXL 解码成功`，而无损 JPEG 出口也照打 ⇒ 说的是自己。改成
  `Stage1 主图读回成功（压缩=<实测>）`。
- 压缩方式原先读 `info.fIFD[0]->fCompression` —— 本机产物主图在 **SubIFD**，IFD0 恒是 7 ⇒
  JXL 出口被误报成无损 JPEG。改成 SDK 自己的取法 `info.fIFD[info.fMainIndex]`
  （`dng_negative.cpp:3294`）。两臂现读数：`on.dng → comp=7`、`jxl.dng → comp=52546 (=ccJXL)`。
- 外部符合性读数：**Adobe `dng_validate` 1.7.1 rc=0 两臂**（`tests/output/conform/dngvalidate-{on,jxl}.log`），
  只剩 `Too little padding on … edge of CFA image` 两/三条警告，无错误。

### 14.5 full18 整轮（06:56:22 起跑 / 07:27:34 收尾，31 分，日志 `tests/output/gates-round-2026-10-17-full18.log`）

- `script-list=77 条（受管基线 77）`；尺 `runner-self-sha256=1DA88E16BFAAEBFE lines=1503 bytes=146062`
  **起/收相同**（换尺原因：本轮改了运行器内 #77 的散文注释 ⇒ 与 full14 的尺不可比，历史轮照旧不重绑）。
- `[WARN]` 命中 0 ⇒ 无源码漂移、无归属不明；`.gates.lock` 收尾无残留；磁盘 36.38 → 36.25 GB。
- 被测件：`FfmpegGui.dll=7395A0B2AB38873F`、`FfmpegGui.exe=291228273200D800`、
  `ServiceProbe.exe=B589B68B660C4842`、`UiTestHost.exe=9015E9D78BEB4E94`、
  `GainMapTestHost.exe=25AEB812BA0A8833`；14 目标 `-t:Rebuild` **0 失败**、13 份宿主同 sha
  （另 4 份属清单外工程，与 full14 同形）。
- **77 条里 76 条 exit=0**，两条红逐名点出：
  ① `probe:runner 7/1` —— 既有沙箱假红（与 beta7/full9~14 逐值同），不为它改探针或产品；
  ② `verify-ipc-extra-options.ps1` `pass=0 fail=1`：**FAIL 已有 1 个 FfmpegGui 实例在跑（管道名按
     用户 SID 派生，会串台）：47196**。该进程 `StartTime=2026-10-16 21:10:36`、
     `Path=publish/build/FFmpegPictureUI-v1.6.0-beta7-x64/FfmpegGui.exe` ⇒ **用户自己开着的出货包实例**，
     不是本轮造出来的残留；门按设计 fail-closed，**我没有去关它**。要复跑这条得先由用户关掉那个窗口。
     ⇒ 因此本轮**不宣布新基线**：整轮口径 = 76 绿 + 1 环境红（IPC）+ 1 沙箱红（runner）。
- 轮内本链路三条：`#77 32/0@34.2s`、`verify-dryrun-parity 16/0`、`_probe-raw-default-tier 12/0`。

### 14.6 未证边界（照实记）

- 50828 的**资源 fork 与 `.THM` 四块恒为 0**：本机素材没有 .THM 伴生文件，也没测过"有 .THM 的索尼件"
  该怎么填 ⇒ 别把它当"规范里 THM 位没用"的结论。
- ZIP 分块只解 **fork 0**：fork 1~3 我没有解码路径（因为没有内容可解）⇒ 若将来遇到带资源 fork 的
  原件，这条读取路径是缺的。
- ⑮ 组证明的是"工具不再因 GPS 降级 + 容器层读不出 GPS"，**没有**证明"下游消费者会去读嵌入件里的
  GPS 并据此判断" —— 那是别的程序的行为，未测。
## 15. 近似路径的两条落地：`OpcodeList3` 分通道已接、色温换算被实测否证（2026-10-18 01:04–01:41，full19）

用户判据是「标准 dng，不掺杂发明的实现」⇒ 这轮把"标准里没有位"的三族数据按**近似路径**逐条裁决，
能落规范位的落地，落不了的写清为什么不做，**没有新增任何自造容器或新开关**。

### 15.1 已落地：分通道曲线 → 规范 `OpcodeList3`(51022)

- 形状：三支 `dng_opcode_MapTable`（code=7、minVersion=1.3.0.0），每支 `dng_area_spec` 的
  `fPlane`=0/1/2、`fPlanes`=1，表 `count=0x10000` 项 uint16。CLI 复用 `-idc-look` 的规格文件，
  新增键 `curveR/curveG/curveB`（[dngtool.cpp:1855 起](file:///C:/PLAN/ffmpegPictureUI/tools/src/dngtool/dngtool.cpp)）。
- **与 `curve=` 互斥**：读者会同时应用 profile 的 `ProfileToneCurve` 与 OpcodeList3 ⇒ 同写就是把一条曲线
  施加两次 ⇒ parser 直接拒（不是警告）。
- **恒等表不写**：逐点 `table[i]==i` ⇒ 零像素变化，写了只是造"参数已进标准位"的观感；跳过时打 ASCII 键
  `opcode-skipped=N`，写了打 `opcode-channels=N`（两把键都刻意 ASCII，中文句在非 UTF-8 码页会变形，
  与 ⑩ 的 `idc-changed=` 同一条理由）。
- 表在**解析期**展开 ⇒ 坏曲线在解码/编码之前非零退出且不产出（与 ⑭ 同形）。
- 代价实测：标签数据 393,376 B（产物 68,064,926 → 68,458,314 B，即 3×128 KiB + 头），
  `DNGBackwardVersion` 由 SDK 自己从 1.1.0.0 抬到 **1.3.0.0**（不是我手改的）。

### 15.2 两处自我更正（都在同一轮里被读者当场打回）

1. **放错列表**：第一版写进 `OpcodeList2`(51009)。list 2 作用在**去马赛克之前**
   （`dng_negative.h:913`），那时只有一个平面，"分通道"根本不成立；只有 **list 3 = post demosaic**
   （同文件 :917）可以。⇒ 上一轮我在文档里把这条写成 "OpcodeList2" 也要一并改（已改，§5 表）。
2. **信了注释没做往返**：头文件与 `Overlap()` 注释都写"空 area = 整幅"，我据此写空矩形 ⇒
   `dng_area_spec::GetData` 先按 `fArea.H()/W()`（空矩形=0）把 pitch 从 1 钳成 0，再被 `IsEmpty` 分支
   `ThrowBadFormat`（`dng_misc_opcodes.cpp:141-155`）⇒ Adobe `dng_validate` 与自家 `dngtool -verify`
   双双 error 100006。**最坏的地方在于编码侧 rc=0、日志照打** ⇒ 没有第二个读者就是静默坏件。
   修法是写 `Stage1Image()->Bounds()` 的整幅矩形（与读者 `dng_inplace_opcode::Apply` 用的
   `image->Bounds()` 同一坐标系）。
3. **我还差点把坏件罪名扣在被测件上**：我的新读手起初报"51022 的偏移=8,890，落在嵌入原件内部 ⇒ 写坏了"，
   而 Adobe 打印的同一标签 offset 逐字是 39,527,098。真因是我自己的 `struct` 格式串写成 `'HHIH'`
   （TIFF entry 的值场是 **4** 字节 ⇒ 只取到低 16 位，大偏移被静默截断；小偏移看不出）。
   ⇒ 已进 [[feedback-binary-reader-anchors]]：判"偏移对不对"要拿第二个读者的打印值对撞，
   先怀疑自己的解析器。

### 15.3 判据（第 77 条 ⑯ 组，32 → 36，重臂 37）

`tests/scripts/verify-dng-metadata-transport.ps1`：**默认档 36/0 @44.0 s**、
`DNGOPC_HEAVY=1` 时 **37/0 @47.2 s**（日志 `tests/output/gate77-opcodes.log`、`gate77-opcodes-heavy.log`）。
- ⑯a 结构 + ASCII 键 + **独立读手** `tests/scripts/_readback-dng-opcodes.py`（自解 TIFF：解完正好落在
  byteCount、area 非空、pitch=1、表内容与规格逐点回算 ≤1 码）。
- ⑯b 反控：不加参数 ⇒ 51022 必须缺席。
- ⑯c 恒等 ⇒ 不写（键命中 + 产物无 51022）。
- ⑯d `curve`+`curveR` 混给、`curveQ` 不认识的键 ⇒ 非零退出且不产出。
- ⑯e（重臂，默认不跑 ⇒ 记未判不记绿）：Adobe `dng_validate -3` 三臂渲染 stage-3 → `rgb48le` →
  `tests/scripts/_dng-opcode-render-ab.py` 对账。一手读数：恒等臂与基准**逐字节相同**；非恒等臂
  **72,721,702 个平面值不同**；且等于按规格算出的 LUT —— 精确 0.9908、最大 1 码（= SDK 对表做黑电平
  调整 `fBlackAdjustedTable` 的舍入地板 ⇒ 判据取 2 码，不取"实测刚好过"的 1 码）。
- 变异两支**各只红自己那条**：空 area 版 ⇒ 35/1 只红 ⑯a（编码照绿、读手 rc=1）；摘掉恒等跳过 ⇒
  35/1 只红 ⑯c。还原后 `grep -c MUTATION` = 0、`diff` 只剩该改的那一行。

### 15.4 实测否证：不做 K→中性色换算（原"第一梯队 D 路"）

- 产物 `AsShotNeutral` 已经是源增益的倒数：7S 实测 `1/2.86328 = 0.34925`、`1/1.33984 = 0.746356`，
  与 exiftool 读回的 `(0.34925 1 0.746356)` 逐字对上 ⇒ **白平衡这一面本来就落在标准位上了**，缺的不是位。
- 缺的是"把 `ColorTemperatureAdj=6000` 换成中性值"的公式：K→xy 的 Planckian/daylight 拟合
  **不在 DNG 规范里**（DNG 只登记 named illuminant 与实测 neutral），**也不在本机 SDK 里** ——
  `grep -rniE "kelvin|planck|tanner|colortemperature"` 对 `dng_sdk/source/*.{h,cpp}` 命中 0，
  同一条命令在已知符号 `dng_1d_function` 上命中 21 ⇒ 探针活着（[[capability-absence-needs-probe-alive-check]]）。
  自己拿外部拟合写进产物 = 造数，违反"不许只改标签/不许造数"。
- 本机唯一带 IDC 链的那件 `ColorTemperatureAdj` **没被改动过**（不在 `changed` 的 14 条里）⇒ 连可验证的
  落点都没有。**结论：登记为不做，不是待办。**
- 顺带把 IDC 58 条做了命名账：exiftool `SonyIDC.pm` v1.08 命中 **52/58**（`IDCCreativeStyle`、`BrightnessAdj`、
  `D-RangeOptimizer*`、`ToneCurve{Brightness,R,G,B}{X,Y}`、`PeripheralIllum*`、`GrayPoint`…），
  只有 `0x9015/0x9016/0x901B/0x901C/0x901D/0x9100` 无名 ⇒ "难点不是不认识这些字段，是没有单位定义"。

### 15.5 full19 整轮（01:04:57 起跑 / 01:41:05 收尾，36 分，跨了午夜 ⇒ 标签 10-17、时间戳 10-18）

`tests/output/gates-round-2026-10-17-full19.log`：**77 条里 76 条 exit=0**，唯一红 =
`probe:runner 7/1`（既有沙箱假红，与 beta7/full9~18 逐值同）。
上一轮挡住的 `verify-ipc-extra-options` 这次绿（用户已关掉那个 beta7 出货包实例）。
- 尺 `runner-self-sha256=DC76C01DA00B7AE6 lines=1514 bytes=147442` **起/收相同**（又换尺：本轮改了运行器
  #77 的散文注释 ⇒ 与 full18 的 `1DA88E16BFAAEBFE` 不同尺，跨轮比数要带这个 sha）。
- 被测件与 full18 **逐字同指纹**：`FfmpegGui.dll=7395A0B2AB38873F`、`FfmpegGui.exe=291228273200D800`、
  `ServiceProbe.exe=B589B68B660C4842`、`UiTestHost.exe=9015E9D78BEB4E94`、`GainMapTestHost.exe=25AEB812BA0A8833`
  ⇒ 本轮没动产品码，**full18+full19 归属同一组交付件**，这组指纹可作为现值。
- `[WARN]` 0（无源码漂移）、无 `.gates.lock` 残留、磁盘 19.66 → 19.46 GB（>6 GB 闸门）。
- 汇总面：56 条带 `PASS=n FAIL=m` 的脚本合计 **1891/0**；探针 46 个 mode 合计 **1201/1**（那 1 就是 runner）。
- 轮内 #77 = **36/0 @42.9 s**（⑯e 重臂在默认档记 SKIP/未判）。

### 15.6 明确不做与未证边界

- **C 路（XMP `ApplicationNotes` 里写 `crs:*` 相机设置）不做**：它能最高保真地让 ACR/LR 显示档位，
  但那是把 Adobe 私有 schema 塞进产物 —— 与本批判据"不掺杂发明的实现"直接冲突 ⇒ 要用户明确改口径才动。
- **A 路（标定后写 profile）仍等标定数据**：`_fit-idc-scale.py` 夹具在位、拒绝出表的条件已测，
  缺的是同一场景按已知档位导出后的实测值（链外动作）。
- 未证边界：`curveR/G/B` 的 x 域 [0,1] 与"段内线性插值"是**我方约定**（读者对 profile 曲线按三次样条插值，
  两条载体形状不同）；`GrayPoint` 那 8 个值（`3981/1766` ×4）到底是样本坐标还是编码值仍未判定；
  无名 6 条（含 `0x9100=65536`）语义未知；`fBlackAdjustedTable` 的 ±1 码舍入只量了本机这一件素材。
### 15.7 用户追问后的两处更正（2026-10-18 02:04 实测，覆盖 15.4/15.6 的相关说法）

用户两句质疑都成立，逐条给证据：

1. **"镜头校正/白点 DNG 侧应该有标准定义"——对，我先前写成"无位"是错的。**
   - 镜头：`dng_lens_correction.h:10` 原文 "Opcodes to fix lens aberrations such as geometric distortion,
     **lateral chromatic**…"；`kMaxTerms=15` 是**逐通道**的（`fData[kMaxColorPlanes][kMaxTerms]`），
     另加逐通道切向 `fTanParams[P]`（:358）与 `HasLateralCA()`（:548），还有第二代 `WarpRectilinear2`(code 14)；
     暗角 `dng_vignette_radial_params::kNumTerms=5`。⇒ **位有**。真约束是**对应关系与单位**：
     本机 READINGS 第 30 行实测 畸变 16→15 项残差 0.24%、暗角 0.02%、**横向色差 32 项→15 项残差 18.2%** ⇒
     若那张表是 32 点 LUT，15 项多项式装不下；而 LibRaw 本机不暴露 `distors` ⇒ 表值单位无一手材料 ⇒ **仍未判定**，
     但定性要改成"位有、待表形状/单位材料"，不是"标准无位"。
   - 白点：`dng_tag_codes.h:279` `tcAsShotWhiteXY = 50729`（再加 `AsShotNeutral` 50728 / `CameraNeutral`）⇒ **位有**；
     无位的只是 `GrayPoint` 那种"采样点坐标"的表达。8 个值 = `3981/1766` 重复四次，7M2 画幅 6000×4000 ⇒
     落在框内，更像 (x,y) 重复登记；坐标还是编码值仍未判定 ⇒ 不换算不写。产物现有 `AsShotNeutral`
     是源 `RedBalance/BlueBalance` 的倒数，与这次灰点编辑无关。
2. **"XMP 为什么不写"——我先前把机制和内容混了。** `ApplicationNotes`(700) 就是规范指定的 XMP 位，XMP 是
   ISO 16684-1，不是发明。实测：`exiftool -XMP:CreateDate/-XMP:ModifyDate` 把 packet 写进 IFD0 **700**
   （type=1 count=2807，头 60 字节带 `<?xpacket … W5M0MpCehiHzreSzNTczkc9d?>`），加完 Adobe `dng_validate`
   = *Validation complete*、`dngtool -verify` 照过 ⇒ 合规可走。dngtool 自己写不了：本仓 SDK 以
   `qDNGUseXMP=0` 配（`tools/src/dngtool/CMakeLists.txt:44`），`dng_xmp_sdk.cpp` 要 Adobe XMP Toolkit（本机无）
   ⇒ 唯一现成的路是**产品侧既有的 exiftool 后处理**（ICC/GPS 已经在用）。
   但**诚实可写的内容只有两条**：IDC `VersionCreateDate/VersionModifyDate` 本身就是 XMP 日期格式
   （实测 `2026:09:26 23:16:37` / `2026:09:26 23:40:29`）⇒ 零换算。`crs:*` 写不了也不该自己造：本机 exiftool
   报 `Sorry, crs:Temperature doesn't exist or isn't writable` + `Nothing to do` ⇒ 只能手拼命名空间与值域，
   而索尼档位整数与 Adobe 值域没有一手对应材料 ⇒ 那才是造数。
   ⇒ 结论从"C 路不做"改成：**C 路的一半（xmp 两条日期）可做且该做**，落点是产品侧 exiftool 后处理
   （改产品码 ⇒ 要重建 14 份宿主 + 整轮），另一半（`crs:*` 设置）保持不做。
## 16. ⑰ 组：IDC 版本时间戳落进标准 XMP 位 + 镜头表查明（2026-10-18 02:04–03:20）

用户令「很多玩意事实上是能写入的，只是缺少正确的办法 ⇒ 去找正确的办法并完整实现」。本轮把三条"我先前判过死"的路各自追到实证：

### 16.1 已实现：`xmp:CreateDate` / `xmp:ModifyDate` ← IDC `0xD100/0xD101`

- 为什么这一条**零换算**：IDC 存的就是 20 字符 `YYYY:MM:DD HH:MM:SS`，与 XMP 日期同形（实测
  `2026:09:26 23:16:37` / `23:40:29`）⇒ 不是"等标定"，是能直接落。载体 = 规范指定的
  `ApplicationNotes`(700)，本机实测由 exiftool 落地并通过 Adobe `dng_validate`。
- 落点选择（**dngtool 写不了 700**，两条一手证据）：`tools/src/dngtool/CMakeLists.txt:59,172` 定义
  `qDNGUseXMP=0`，且 `dng_xmp.cpp` 里 `qDNGUseXMPSDK` 命中 **0** 处 ⇒ 1.7.1 的 XMP 只能走 Adobe XMP Toolkit
  （本机无）。⇒ 正确落点 = 产品侧**既有**的 exiftool 后处理，不是新机制（ICC/GPS 早已这么走）。
- 三处新代码：`RawService.ReadIdcVersionDatesAsync` + `ParseIdcVersionDates` + `IsXmpDateForm`；
  `ExifToolService.BuildIdcXmpDatesArgs` + `WriteIdcXmpDatesAsync`；`QueueProcessor.WriteIdcVersionDatesAsync`。
  两道**独立**的形状复检（源文本 ⇒ 命令行参数是不信任边界：不校验就是注入面）；三种"不写"各自点名
  （exiftool 缺席 / 源无 IDC 链 / 形状不合），取消走 `OperationCanceledException` 上抛而不是吞成"读不到"。
  隐私优先：只在 `carryRawMetadata ∧ ¬StripExifTime` 下调用。
- 判据：#77 加 ⑰a–⑰e ⇒ 默认档 **41 判据 41/0 @47.3 s**（32 → 36 → 41）。⑰e 是产品接线的**源形锁**
  （不重建也能红）。变异：守卫退化成 `if (success)` ⇒ **40/1 只红 ⑰e**；还原后与备份 `diff` 逐字相同。
- 外部符合性读数：写入后 Adobe `dng_validate` = *Validation complete*、`dngtool -verify` rc=0。

### 16.2 我自己写的新代码里有一个必然红，被在册判据提前抓住（叫停了正在跑的整轮）

第一版 `ReadIdcVersionDatesAsync` 开了 `RedirectStandardError = true` 却只读 stdout ⇒ 正是
`_probe-stderr-drain-scan`（在册）要抓的形状（约 4 KB 管道，stderr 写满就阻塞在写上，队列槽位被永久占住；
P2-3 治过同一类）。当场量到红→绿：把那一行改回"只读 stdout" ⇒
`FAIL 疑似未排空的方法：1 个 … RawService.cs:314 声明=1 排空=0`；还原后 `cmp` 逐字相同，
扫描回"62 处声明，全部排空"。⇒ 修法不是就地再写一份排空，而是把 `QueueProcessor` 的 private
`ReadStdoutDrainingStderrAsync` **收口到 `PlatformServices`**（原 3 个调用点 + 新增 1 个共用一份），
并补 `SetSafePriority` 与取消即杀进程树。**如果没有在册扫描器，这会是下一轮 40 分钟里的一条神秘卡死。**

### 16.3 镜头三表：位置与格式已查明，先前的"加密读不到"是错的

派研究 lane 查证（结论都有出处）：三张表在**图像 SubIFD**（IFD0 `0x014A` 指过去）里，**明文**——
`0x7031/0x7032` 暗角（首字 16 ⇒ 16 项 int16s）、`0x7034/0x7035` 横向色差（32 项）、`0x7036/0x7037` 畸变（16 项）；
本机 `Sony_ILCE7M2_IDC.ARW` 直接按字节读到（SubIFD @0x22f8a，载荷 @143604/143638/143704，
暗角 `[16, 0,16,64,144,…,3152]` 前几项恰为 16·i²）。加密的是另外两处：SR2Private 流（密钥 tag `0x7221`）
与逐镜头目录的替换密码 `c=(b³)%249` ⇒ **拿"加密"当不实现的理由是错的**。
DNG 侧可表达性（本机 SDK 逐行）：暗角 `g = 1 + Σ k_i r^{2i+2}`，`kNumTerms=5`，**一份曲线给所有平面**；
径向 warp `kMaxTerms=15` **逐平面** + 每平面 2 项切向 + `WarpRectilinear2`(code 14) 更宽。
⇒ 暗角/畸变与标准同族（本机残差 0.02% / 0.24%），横向色差那张是 32 点带符号表，与"逐平面径向 warp"不同族。

**仍未判定 ⇒ 因此不写**：`0x703x` 的归一化基数与参考尺寸**无权威定义** —— LibRaw 0.22/master、dcraw、
rawler/dnglab 都不解析这三个标签；DNG SDK `dng_info.cpp:1712-1720` 直接放弃（"Sony private data is
encrypted, sorry."）；预期参考实现 `saidnik/srw` 已从 GitHub 消失。⇒ 唯一可辩护的路是对 Imaging Edge
输出做黑箱标定（缺素材）。已落 project memory `sony-lens-tables-and-dng-opcodes.md`，避免下次重新搜一遍。
⚠ **本段已被 §17 取代（2026-10-19）**：『无权威定义』这句是当时搜索面不够 —— darktable / RawTherapee / rawmakase
三家独立实现给的是同一套约定，本机交叉对撞也上了（暗角末项 3152 ⇒ 1.1117×）。三张表**都已落地**（`-lens-corr`），
不需要 Imaging Edge 黑箱标定。原文留着不删，是为了记下『把二手推断当结论』这一次的实际代价。

### 16.4 顺带修的一处错位

`IsStripRequested` 的 `<summary>`（"判据只此一份"）在上一批插入后悬到了 `DngCarriesRawMetadata` 头上
⇒ 描述对象错位，已归位（纯注释，无行为变化）。

### 16.5 full21 整轮（02:45:25 起跑 / 03:20:08 收尾，35 分，日志 `tests/output/gates-round-2026-10-18-full21.log`）

- `script-list=77 条（受管基线 77）`；**76 条 exit=0**，唯一红 = `probe:runner 7/1`（既有沙箱假红，逐值同前几轮）。
- 尺 `runner-self-sha256=1F58333F16E1BA72 lines=1521 bytes=148421` **起/收相同**（又换尺：⑰ 组接进运行器注释
  ⇒ 与 full18/19 的尺都不可比）；`[WARN]` 命中 **0** ⇒ 无源码漂移；`.gates.lock` 无残留；磁盘 15.62 → 15.37 GB。
- 被测件（本轮 14 目标 `-t:Rebuild` 0 失败后重绑）：`FfmpegGui.dll=D0A24A2318AA117D`、`FfmpegGui.exe=291228273200D800`、
  `ServiceProbe.exe=B589B68B660C4842`、`UiTestHost.exe=9015E9D78BEB4E94`、`GainMapTestHost.exe=25AEB812BA0A8833`；
  **现值 = 这一组**（full18/19 的 `7395A0B2AB38873F` 起不再归属当前树）。
- 本链路轮内读数：#77 **41/0 @46.3 s**（含 ⑰a-⑰e）、`_probe-stderr-drain-scan` 62 处声明全排空、
  `_probe-cjk-hardcode-scan` 14/0（新增中文都在诊断桶，余量未动）、`verify-metadata-privacy` 绿、
  `verify-dryrun-parity` 16/0（dng 臂的实参级同源仍成立 ⇒ 新写的 XMP 步没让预览与真实分叉）。
- 另有一条**被中途叫停的整轮**要记账：`2026-10-18-full20` 起跑约 5 分钟后被我停掉，原因是我自己新写的
  `ReadIdcVersionDatesAsync` 未排空 stderr，`_probe-stderr-drain-scan` 必然判红（见 16.2）⇒ 该轮**不产出读数**，
  停轮后留下的 `.gates.lock` 先确认持有者 pid 38300 已不存在才删除；无孤儿进程残留（复查 0 个）。


## 17. 镜头三表 → 规范 opcode 落地（2026-10-19，`dngtool -lens-corr`）

用户令「完整完成实现」。前置是同一句追问的另一半：「重新思考一下相关规范和实现，在 PR 等上测试能否
正确识别并解码」⇒ 本轮先把**读者侧**测清楚，再按测出来的形状写。

### 17.1 测出来的四件事（全部 RUN，日志 `tests/output/lens-probe/probe3.log`）

- **换算约定有权威，我先前两条"拦路"的说法都是错的**。darktable 4.6 / RawTherapee / rawmakase 三家
  独立实现一致；结点位 `r_i=(i+0.5)/(nc−1)` 直接抄 `lens.cc:2225` 原式。横向色差"索引语义未定"
  被 `exif.cc:1398-1406` 的逐字取数代码否证（前 nc=R、后 nc=B，并硬校验 `2*nc==CA 计数`）⇒ 撤回，
  三张表**都写**。
- **flags 可选**：三个镜头 opcode 的构造函数就把 flags 当入参（`dng_lens_correction.h:580/605/735`），
  `Spool` 逐字写出（`dng_opcode_list.cpp:216-218`）⇒ 我 10-18 记的"SDK 没口子标 optional"作废。
- **required 不是雷**：Adobe 自带 `sample_files/03_jxl_bayer_raw_integer.dng` 的 OpcodeList3 就是
  **required** 的 WarpRectilinear；而**未知 ID** 标 required 会让 Adobe 自己的读者拒件（rc=106，
  `dng_opcodes.cpp:226-239`），标 optional 则零施加零报错（0 个平面值改变 = 干净负控）。
- **RawSpeed 不看 list 3**：`DngDecoder.cpp` 只查 LIST1(:592)/LIST2(:622)，`TiffTag.h:334` 那个
  `OPCODELIST3 = 0xC742` 是错的（真 tag 51022 = 0xC76E）⇒ 放 list3 的东西对它不可见；
  darktable 有**自己的**解析器（`common/dng_opcode.c:224-283` 认 ID 1/3，认不到 14 只 `dt_print`），
  并把存在性记进 `img->exif_correction_type = CORRECTION_TYPE_DNG` ⇒ **不会二次校正**（我原先担心的
  双重校正被这条否证）。

### 17.2 写法（被实测反过来决定的四处）

1. warp 发 **`[WarpRectilinear2 optional] + [WarpRectilinear required]` 成对且相邻**：
   实测 SDK 会跳掉后一条（像素与"只发 14"逐点相同 = 规范 p.105 的跳过规则真生效），而只认 1.3 的
   读者（darktable）拿得到 ID=1 那一支。相邻是硬条件 ⇒ 由 `⑱a` 的读手钉住。
2. ID=1 那一支**只含偶次项、单独重拟**（v1 的 `PutData` 只取 `fData[0,2,4,6]`，
   `dng_lens_correction.cpp:1908-1911`）；拟不到 0.0020 水位就**不发兜底支**，不硬凑。
3. 暗角与 ID=1 标 **required**。SDK 按 `MinVersion(false)` 自动记 `DNGBackwardVersion`
   （`dng_image_writer.cpp:7944-7953`）⇒ optional 的 1.6 项不抬版本，自家产物实测 Backward 仍 1.3.0.0。
4. 恒等 / 表缺 / 开关明确关 / 结点不足 / 残差超界 / 径向映射非单调 ⇒ **逐条点名且一条不写**
   （`不造观感` 那条硬规矩）。开关是**三态**：读不出 ≠ 关，只有明确 0 才跳过（dt 根本不查它）。

### 17.3 代码与判据

- 写侧 `tools/src/dngtool/dngtool.cpp`：`ReadLensTables`（自己走 LE TIFF：IFD0→0x14A→图像 SubIFD→
  0x7031/2、0x7034/5、0x7036/7）+ `FitPowers`（基函数最小二乘，正规方程 + 部分主元；奇异即拒）+
  `ApplyLensOpcodes`。ASCII 键 `lens-vignette= / lens-warp2= / lens-warp1= / lens-skipped= /
  lens-tables=absent`。
- 读侧第二实现 `tests/scripts/_readback-dng-lens.py`：**不 import 写侧**，自己解源 ARW 与产物的大端
  列表，逐结点回算增益/半径比例并与源表对撞（死线写在本文件：暗角 0.0025、warp 0.0006），
  再判 flags、[14][1] 相邻、镜头支不重复；`--stderr <写侧 stderr 落盘>` 让它把**点名过的跳过**与
  **静默缺席**分开判（前者放行、后者判红）⇒ 它不必假设"三支恒在场"，那条假设只是本夹具恰好成立。
  v1 支的判法是残差而不是"奇次位为 0"——v1 载荷只有四个偶次项，奇次位是**读手自己填的 0**，
  那条断言永不可能触发（2026-10-19 审查删掉的死牙）。
- 造变异件**不在读手里**：`_mutate-dng-lens.py` 独立注入八类缺陷（k4 / v1val / flags14 / vigopt /
  center / order / dup14 / dropvig）。分家的理由与 ⑯ 组同一条——判件与造件共用入口时，判据走偏
  造件也跟着走偏（⑱f 的空跑正是这么溜过去的）。像素臂 `--render` 仍在读手内（它判的是 Adobe
  渲染出来的裸流，与"造系数缺陷"不是一件事）。
- 第 77 条新增 **⑱ 组**九臂（⑱a–⑱h + ⑱g2）；顺带把 `_readback-dng-opcodes.py` 从"只认 MapTable=7"放宽到已知 ID
  集合 {1,3,7,14}（否则 `-idc-look` 与 `-lens-corr` 同时开 = 全保真的自然组合，会被判据过窄误杀）。

### 17.4 读数（写侧与读侧两份独立实现给出同一个数）

```
[dngtool-e] lens-vignette=applied nc=16 sw=257 maxres=0.000319 cornergain=1.104491
[dngtool-e] lens-warp2=applied planes=3 sw=1 ca=per-plane maxres=0.000058/0.000074/0.000175
[dngtool-e] lens-warp1=applied maxres=0.000053
[dngtool-e] lens-opcodes=3 已写进 OpcodeList3(51022) 作用域=6048x4024
LENS-READ OK ... → 3 支 [3,14,1] | 暗角: 残差=0.000319 ... warp2: flags=1 ... warp1: flags=0
```

体积代价 **+764 B**（68,064,926 → 68,065,690）。变异验牙：暗角 k4 放大 20% ⇒ 残差
0.000319 → **0.021154** ⇒ 判红（`⑱d`）。

### 17.5 重臂那一次先是**空跑**，抓它花了两轮（本轮最该记住的一条）

⑱f 第一版只判"读手 rc 非 0 就红"。实测日志里它是 **PASS 而整轮输出没有任何 `LENS-RENDER` 行**
——读手按当时那份 argv 取位（少算一格，`--render` 自己占了 argv[1]）必然崩，崩了却没把臂带红；
**成因未判定**，我不给这条写因果。修法是让空跑结构上不可能：三件事同时成立才算过
（rc=0、读数非空、读数含 `LENS-RENDER OK`），再加输入文件存在性与 `W×H×6` 尺寸守卫
（喂非 rgb48 的短文件 ⇒ 当场点名"不是 rgb48le"，实测过）。改完的真读数：环带提升
1.0946/1.0943/1.0954 对预测 1.1117，20,000 点抽样 **19,985** 不同。

同轮另一条测量学纠正：同一对渲染，**最外 32 像素的角块**给 1.4536/1.4785/1.5259 ——
角块与环带为何背离**未判定**（界外采样后的边界处理？还是最外 24 像素内径向斜率太陡？）⇒ 判据一律用 r∈[0.90,0.98) 的**环带**，角块只登记读数不参与判定。

### 17.6 现值与边界

- 第 77 条现值：默认档 **55/0 @43 s**（终件独跑；整轮 full23 内首跑的是 48/0 @63.6 s，随后 ⑱h 补一格）；
  `DNGOPC_HEAVY=1 DNGLENS_HEAVY=1` 时 **57/0 @54 s**。两次独跑期间无其他门禁并发（bench 复跑停在
  "等编译退场"的循环里），但本机仍有一个 ffmpeg 静态编译在跑 ⇒ 墙钟是**含负载值**，只作量级不作判据。
  ⚠ 该现值已被 §17 取代（默认档 67/0、重臂 69/0，当时 ⑳ 组 12 臂），**现值见 §19：默认档 70/0**（⑳ 组 15 臂；重臂本轮未重跑 ⇒ 69 那个数当前不成立）。
  演进 32 → 36（⑯）→ 41（⑰）→ 47（⑱ 七臂）→ 48（⑱d 十支变异 + ⑱g2）→ 49（⑱h 真素材两格）→ 51（②e/②f 嵌入原件完整性）→ 55（⑲ 丢弃政策锁四条）。
- 产品侧入口已在 §17.7 落地（10-19 当日续），本条原先"没有入口"的陈述作废。
- 盘：本轮 C 盘空闲 59 GB；⑱d 的变异件是整幅无损 DNG，**每件 68 MB** ⇒ 门禁里判完即删，峰值只多一件。


### 17.7 产品侧入口（用户令「顺手把产品侧入口的代码也实现了」，10-19 当日续）

入口照同族 `DngHighlightMode` 的落点抄齐（那个旋钮在 src 里有 9 个接线点）：选项字段 + 预设字段与
`ApplyPreset` 合并行 + CLI `--dng-lens-correction`（`ParseBoolStrict` ⇒ 非法值响亮报错并给值域，
实测 `'maybe'` 被拒）+ GUI 勾选框（说明只进灰字提示行，标签只写身份）与采集/预设采集/预设回放三处
+ 执行侧与预览侧两处传参 + 双语两份词条（各 599 键）+ `_lib-axes.ps1` 登记该轴。

⚠ 唯一真源这次帮了大忙：`-lens-corr` 只在 `RawService.BuildEncodeToDngArguments` 追加一处，
`--dry-run` 预览与真实执行共用它 ⇒ 结构上不会再出现「预览声称的命令不是实际跑的那条」。
⑱g 就是钉这套接线的源形锁。

端到端读数：`--headless --dry-run` 三臂（true / false / 缺省）打印的实参差异正确 ⇒ 只有 true 带
`-lens-corr`；真跑 `--headless -i <ARW> --format dng --dng-lens-correction true` ⇒ 5.8 s、
67,440,788 B，独立读手在**产品产物**上判 OK（`3 支 [3,14,1]`，残差 0.000319 与工具侧同一个数）。
⑱g 的牙：摘掉预览侧那一处传参 ⇒ **只红 ⑱g**（46/1，消息点名"预览与实参会分叉"），还原后 47/0
且文件 sha256 与变异前逐字节相符（`8aa18e81f11ef857`）。第 77 条现值见 §17.6（默认档 51/0、
两把重臂同开 53/0），⑱g 自己在 10-19 晚被抽成纯函数并补上 ⑱g2 自验牙（§17.8）。链路复跑：gui-control-wiring **82/0**、cjk 棘轮 **14/0**、
dryrun-parity **16/0**、i18n-scan 与 test-group-coverage exit=0。14 个工程 `-Rebuild` **0 错误**；
`FfmpegGui.dll` = `B89FB95424938807`（1 份）/ `800B10EDE2D48215`（13 份）。

⚠ 本轮两条自己的操作错，记下来当规矩用：
1. 批量补丁脚本**重跑了一遍** ⇒ 选项/预设/CLI 三处被插两次（重复属性当场编译错）。根因是锚点在
   替换文本里仍然存在 ⇒ "幂等"假设不成立。⇒ 补丁脚本一次只跑一遍，重跑前先断言锚点计数。
2. 变异脚本的 `restore` 分支在 mode 判断**之前** assert 原始锚点 ⇒ 变异态没能自动还原，靠 sha16
   手工还原并核对相符。⇒ 还原路径必须无条件先于任何断言。


### 17.8 审查轮的完整修复（用户令「做一次完整修复」，10-19 晚）

审查对象是 ⑱ 组自己写的判据。修完的现值：**55/0（默认档）· 57/0（两把重臂）**（含随后的 ⑲ 政策锁）（含随后补的 ②e/②f）；⚠ 现值已被 §17 取代 = 67/0 与 69/0；⑱d 十支变异**各打各的
判据** + 三支点名放行，⑱g2 六处各删一次**各只红自己那条**，⑱h 用**真素材**钉住两侧同尺。逐条列改了什么、
以及怎么证明它不是空转：

| 发现 | 处置 | 证明它有牙的方式 |
| --- | --- | --- |
| ⑱f 的容差 10%：接受区间 [1.02, 1.223]，"只施加 2%"也放行 | 收到 5%，并把"本臂不锁幅度精确性"写进判据文档 | 同一对渲染实测 1.0946/1.0943/1.0954 对 1.1117 ⇒ 1.5%，余量 3.3 倍（重臂 50/0 里跑过） |
| 读手硬要求"三支恒在场"，而写侧允许**合法跳过**（开关明确为 0 / 恒等 / 样本数不够） | 加 `--stderr`：点名过的缺席放行，静默缺席判红 | 造 `dropvig/dropw1/dropboth` 三件：无点名必须红、配一份点名过的 stderr 必须绿 ⇒ **两个方向都有活格子** |
| "v1 支奇次位必须为 0"是**死断言**（v1 载荷只有四个偶次项，奇次位是读手自己填的 0） | 删除，改判四项偶次模型对源表的残差 | 残差那条由 `v1val` 变异触发（0.000053 → 0.053394 判红） |
| 判件与造件同入口（读手自带 `--mutant-out`） | 撤掉，独立成 `_mutate-dng-lens.py`（十类） | 每支变异打印"改了哪个偏移、从什么值到什么值"，门禁按**指定那一条**点名文字判命中 |
| ⑱g 六条 if 摊在脚本里 ⇒ 这条源形锁从没被验过牙 | 抽成纯函数 `Get-LensPlumbingBreaks` + ⑱g2 六份内存副本 | ⑱g2 实测 6/6（每处删一次只红对应那条） |
| 镜头支重复没有上限（同学校正被施加两次） | 读手加"每类最多一支" | `dup14`（把 ID=1 的 code 字段改成 14）触发「镜头支重复 [14]」 |
| **两侧不同尺**：源有 CA 表但机内开关明确为 0 时，写侧按规矩跳过并点名，读侧却仍按"表在 ⇒ 期望 CA 因子"算 ⇒ 换一件开关为 0 的素材就是假红（与 10-19 早些时候"开关按 type=8 去读 ⇒ 三条腿全判成关"同一类） | 读手的 `source_expect` 采纳同一个三态尺：`sw['ca'] is False` 或写侧点名过 ⇒ 不再期望 CA | 新增 **⑱h**：把**源** ARW 的开关标签（type=3/count=1、值内联）清零 ⇒ 走真写侧，要求"点名 + 不写那一支 + 读手放行 + 不给点名时必须红"四件事。读数特征即判别式：CA 在场三支残差 0.000058/0.000074/0.000175 各不相同，CA 关掉三支恒等 0.000074 |
| ⑱d 的放行臂喂给读手的是**合成**的点名串 ⇒ 只证明读手认得那个字串，不证明写侧真会那样点名 | 补 ⑱h（真素材变体走真写侧） | 写侧实测打印 `lens-skipped=vignette:switch-off` / `lens-skipped=chromatic-aberration:switch-off` |

**修我新写的代码时撞见的两处，也一并记账**：
1. `return ,@($b)` + 调用点 `@(Get-…)` ⇒ 数组套了一层，`.Count` **恒为 1**、`$nb[0] -eq '字符串'` 退化成
   数组比较而"永远命中"。这条锁一旦这样写就是**稳定的假绿 + ⑱g 恒红**。改法：函数内 `return ,$b`、
   调用点直接赋值不包 `@()`；并用"元素必须是 String"的自检把套娃抓出来（实测六份副本元素类型全为
   String、真源码为 0 条破坏）。
2. 变异工具改 51022 时先犯了两个 TIFF 错：`bytearray` 切片塞进**更短**的片段会整体左移 ⇒ 全文件偏移
   失步；把 count 写到 `entry+8` 改的其实是**载荷偏移**（count 在 `entry+4`）。两处都表现为"读手报
   载荷越界而不是报我该报的那条" ⇒ 加了总长不变守卫（不等就直接拒写并点名）。

同轮读数与两处红的归因：#77 默认档 **55/0 @43 s**、重档 **57/0 @54 s**（终件独跑，两次都无并发）。
整轮 full23 = **75 绿 + 2 探针红**，两条都是**有先例的既有登记项**，与本轮改动无关：

- `runner` 7/1（"静默但正常退出的子进程不被误杀"，读数 `target is not ffmpeg (cmd.exe)`）：
  `docs/FIX_PLAN_2026-10-14.md` 已把同一格登记为**沙箱既有环境红（当时已记"历史三轮同值 7/1"）**，
  本轮 full22 的另一个二进制（`D0A24A2318AA117D`）与 full23 的 `800B10EDE2D48215` 都是同一句读数 7/1
  ⇒ 同值延续，跨两个不同的被测件。不为它改探针或产品。
- `bench` 36/1（sRGB→PQ 关表档"实测增益 ≈1×"量到 1.3×）：同一格在 10-14 那轮量到 1.4× 并已登记为
  **轮内负载假红**（当时的证据是独跑三次 37/0、读数 1.0×）。本轮再补六次：**同一个被测件**
  （`800B10EDE2D48215`）独跑六次全部 37/0 —— 相位 A（有编译进程）51.4/50.9/50.9 s，相位 B（编译退场）
  48.9/49.8/49.8 s（B 的第三次起跑时又有编译进程进场，读数不变）。
  负载只把整轮时长改动 2–4%，而该臂读的是**比值** ⇒ 与 10-14 同因（比值臂在轮内继承同期负载）。
  ⚠ 沿用 10-14 那条自我警告：这条只能记"环境红/偶发"，**不许**写成"该臂设计上必然红"。
### 17.9 「真损失部分到底能不能被正确读取」——本机 PowerShell 实测（10-19 深夜）

跑法：`tests/output/lens-probe/loss-roundtrip-test.ps1`（它内部再调 `loss-visibility.py`），
日志 `tests/output/lens-probe/loss-roundtrip.log`。三件素材（7M2 IDC ／ 7S ／ GPS 夹具），三层读法：

| 层 | 读数 |
| --- | --- |
| ① 只读 DNG 可见层（exiftool 不解嵌入件） | `Sony*` **0／0／0**、IDC 两个版本目录 **0／0／0**、`SonyIDC/SR2` **0／0／0**、镜头/畸变/暗角/CA 原表 **2／0／0**（那 2 条是 EXIF 的 `LensInfo`/`LensModel`，不是原表）、GPS **0／0／0** ⇒ 普通读者**看不到**它们 |
| ② 按规范解回 50828 再读 | census 行数 391／276／281 ⇒ 解回件**同数**，`Compare-Object` 逐行差异 **0 行**；分族：厂商私有 157／148／148、IDC 版本目录 104／0／0、SR2/SonyIDC 26／25／25、镜头原表 28／20／20、GPS 0／0／5 —— **每族都与源相等** |
| ③ 普通 raw 读者（LibRaw 0.22.1 / rawpy 0.27.1） | 源 vs DNG 差 **5 项**：`white_level` 16383→**15360**、`color_matrix` 全 0→**真矩阵**、`sizes.crop_width/height` 6000×4000→**0**、`tone_curve` 不同；而 `other.iso/shutter/aperture/focal`、`lens.model='FE 50mm F1.8'`、`black_level` **两边相同** ⇒ ④ 组那条 ExifIFD 搬运确实被普通读者读到了。源 vs **解回件** 差 **0 项** |

**判词**：真损失是**可见层损失，不是字节损失**。只要读者按规范去解 50828，那些条目一条不少地回来
（LibRaw 再读解回件与原件逐项一致）；只读 DNG 的读者则连 `white_level`、`crop` 这些数值都会读到不同值。

顺带**否证掉我自己差点写下的一句话**：没有现成读者校验嵌入件的完整性。把 50828 载荷第 2084 字节翻掉
（文件总长不变；exiftool 解回只剩 16,252,928 B 且哈希变了 ⇒ 证明这一位真在有效载荷里）之后，
Adobe `dng_validate -v` 仍输出 "Validation complete"（rc=0）并照打 `OriginalRawFileDigest`，
自家 `dngtool -verify` 也 rc=0 ⇒ 新增 **②e/②f**（`tests/scripts/_check-dng-original-digest.py`），
按规范 p.55 原话执行——"This tag is an MD5 digest of **the data stored in** the OriginalRawFileData tag"
（校验对象是存储的那串压缩字节，不是解回后的原件）。五件产物实算全等，例如
`ORIG-DIGEST OK 50828 存储=39,522,045 B MD5=a71356eee30f50762ffd791e3b1c9511`。（当时的 #77 现值 51/0 @66 s ／ 53/0 @72 s；随后 §17.10 的丢弃政策把它改写成 55/0 ／ 57/0，嵌入件判据挪到显式归档档。）

⚠ 两条本轮新踩的读数坑（都差点让我写下错结论）：
1. exiftool 里**同一份数据的两个名字量的是两件事**：`-b -OriginalRawFileData` 给**存储的压缩字节** 39,522,045 B，
   `-b -OriginalRawImage` 才给**解回后的原件** 49,579,614 B ⇒ 用错名字会得出"嵌入件比源少 10 MB"的假结论。
2. TIFF 条目 `count > 4` 时值字段是**偏移**不是内联值：按内联去取 50973 的 16 字节会读到条目区尾巴
   （实得 `d4135b02…`），差点报成"写侧 digest 不符"。⇒ 判"标签值对不对"要拿第二个实现对撞
   （这里 exiftool 与 dng_validate 各自打印的 `a71356ee…` 与实算三方一致才算数）。
3. 顺手记一条操作纪律：`exiftool -Tag=文件` 是**写**而不是提取（我这么敲过一次，把一件 68 MB 产物
   削成 28.5 MB；靠重跑整条 #77 复原并复验读数）。提取要用 `-b` + stdout 重定向。
### 17.10 丢弃政策（10-19 深夜，用户定：不在官方规范、普通软件又完全读不到的 ⇒ 丢弃）

规则一句话：**不靠规范载体偷渡厂商私有件**。50828 这个载体本身是标准的（规范 p.42 定义它存"压缩后的
整个原文件"），但它装的东西本机实测是 **303 条 Sony/IDC/SR2 私有项**（Sony 157 + IDC 两版 104 +
SR2/SonyIDC 26 + 私有镜头原表等 16）。
⚠ 口径按 10-19 深夜的复查改准（原来写"无标准位"是过头的）：
  · 规范**确实**给了私有容器——p.14 原文 "DNG allows proprietary data to be stored using private tags,
    private IFDs, and/or a private MakerNote. It is recommended that manufacturers use the DNGPrivateData
    and MakerNoteSafety tags…" ⇒ 正确的说法不是"没有对应规范"，而是"**没有语义位，只有私有容器可选**"；
  · 而 Sony 这批**不落在 MakerNote 里**：实测源 ARW、默认产物、归档产物的 EXIF MakerNote 都是 **0 字节**
    ⇒ 157 条全在 Sony 自己的 IFD 链（SubIFD 0x7031–0x7037、`SR2DataIFD*`、0x7240 的 IDC 目录）。
    想用 50740 DNGPrivateData 装它们也不行：规范 p.41 要求"偏移必须相对块起点、自包含、字节序无关"，
    跨 IFD 偏移不满足 ⇒ 塞进去就是假标签（我们删自造 `DMSL` 块正是这个原因）；
  · "无法被正确软件读取"要限定为"**用于成像的解码器不消费**"：LibRaw 0.22.1 读源与读默认产物只差 5 项
    参数（白电平/矩阵/裁边/曲线），**没有一项是 Sony 私有语义**；而 exiftool 能把这 303 条逐个命名读出来
    ⇒ 不能写成"任何正确软件都读不到"。
  ⇒ 结论：满足"无语义位 + 解码器不消费"的是 IDC/SR2/私有原表那批；另有约 28 条**有 EXIF 标准语义位**的
    拍摄字段与 GPS 是本次顺手切掉的（见下方欠账表），它们不满足丢弃条件。

落点：`dngtool` 默认不再嵌入，`-embed-raw` 才嵌（归档档）。

执行前必须先量承重事实，否则这句话就不成立：**DNG 自己那份 sensor 平面是原件的逐位拷贝**。
实测（独立实现，不做黑/白电平缩放、不解 mosaic）：

```
RAW-PLANE OK Sony_ILCE7M2_IDC.ARW ↔ idc-on.dng 逐位相同（24337152 点全等，min=510 max=16383）
```
7S 与 7M2、嵌入档与丢弃档，四种组合都是 **不同点数 0、max|Δ| 0** ⇒ 丢弃只动元数据，不动图像。

代价与判据变化：

| 项 | 实测 |
| --- | --- |
| 体积 | 7S 12,437,330 → **6,557,228 B（−47.3%）**；7M2 68,065,690 → **28,543,570 B（−58.1%）** |
| #77 | 51/0 → **55/0 @43 s**（重档 57/0 @54 s）：新增 ⑲a–⑲d 四条，②/⑦/⑧/⑪/⑮c 改指 `-embed-raw` 归档档 |
| 仍在场 | ExifIFD 拍摄字段（④）、预览/缩略图原字节（③）、`ProfileToneCurve`（⑫）、XMP 两条日期（⑰）、镜头三支 opcode（⑱） |
| 随丢弃消失 | 303 条私有项（含 IDC 58 条生效版本的原始字节、SR2 链、私有镜头原表）＋ 归档能力本身 |

⚠ **一条不许说成格式边界**的欠账：实测还有 **约 28 条有 EXIF 标准位**的拍摄字段（`ExposureProgram`、
`MeteringMode`、`Flash`、`ExposureCompensation`、`BrightnessValue`、`LightSource`、`SensitivityType`、
`FocalLengthIn35mmFormat`、`FlashpixVersion`、`XResolution/YResolution/ResolutionUnit` …）与 **GPS**
在可见层是 0 命中 —— 它们本该"提上来"而不是跟着丢，本轮按用户"直接丢弃，先不补"的选择未做。
⑮b 因此在产物上换了点名：默认档发 `gps-not-carried=1`（并写明"没搬是我方漏项，不是格式边界"），
归档档发 `gps-in-embedded-original=1`。
⇒ **这段已作废**：同一夜用户否掉"先不补"（"我要求的是能被 ps 等处理的"），下一节 §17 把这半做完；
⑮b 的判据也随之翻向（现在要求 GPS 整档在场且逐字同值）。

## §17 2026-10-20：补全标准位这一半做完了（含我自己造的一处回归）

**规矩的两半**：能被 conformant 读者（PS/Camera Raw/exiftool/LibRaw）解码的**必须搬进标准位**；
搬不动的**必须点名**；完全无法解码的厂商私有件一律不进产物（§16 的丢弃政策不变，由 ⑲ 组锁）。

修掉的六处，全部由新加的独立读手**当场**抓到（不是先想好再补验证）：

| 旧行为 | 抓它的格子 | 修法 |
| --- | --- | --- |
| 标准位搬运 = 我手抄的 21 条表 ⇒ 表外静默丢（源件实测 43~52 条标准位） | ⑳a 存在性对撞 | 逐条喂 Adobe 自己的 `dng_exif::ParseTag`（`dng_exif.cpp:822`，正常解析 TIFF 走的就是它）⇒ 表由 SDK 持有，不认领的才进 `exif-std-unmapped` |
| GPS 整档不搬，且 ⑮b 把"没搬"写成格式边界 | ⑳c ＋ ⑮b 翻向 | GPS/Interop/ExifIFD 全档枚举；⑮b 改成"整档在场 + 逐字同值 + 键不发"，`gps-not-carried` 降级成缺陷报警器 |
| DNG 出口对 10 条标准位**没有发射点** | ⑳b（未后置件必缺这 10 条） | 产品后置 `CarryStdTagsToDngAsync` 用 `-TagsFromFile` 抄 ⇒ 取值的是 exiftool，我方不拼任何来自源内容的字符串；⑳l 单条抽取证明每条各有牙 |
| `0x0112 Orientation` 恒写 1 | ⑳g 两臂分叉 | 源值经 `dng_metadata::SetBaseOrientation` 落到出口（`dng_image_writer.cpp:8488` 取的就是它） |
| `0x0213 YCbCrPositioning` 恒写 0（EXIF 只允许 1/2），源的 2 被丢 | ⑳k | 派生类设 protected `fIFD`（不改第三方源码）；源未声明时按 EXIF/JFIF 默认落 1 |
| **我自建**的 TIFF 类型尺寸表把 `ttSRational(10)` 抄成 4 字节 ⇒ 两条 SRATIONAL 当内联，产物里写进的是源件指针 4240 与下一条的 tag+type `0x000A9204` | ⑳a 的**值档**（只比存在性时这条会漏过：标签在场而值是乱数） | 布局常数一律取 `TagTypeSize()`；⑳h 加"手抄表不许复活"的形状锁 |

**另一处回归同样是本轮自己造的**：GPS 搬进容器后，产品**当时的**隐私默认（`StripExifGps` 那时=true）　⚠ 该默认已在 §18 被用户裁定推翻（口径＝默认不勾选、全保留）——本节里的"打穿"说的是当时的默认值。
在 DNG 出口被打穿 —— 端到端首跑读到默认档产物 GPS 在场。顺藤又摸出一处**既存**缺陷：那次
"复制之后必须补的显式删除"原来**只在 `CopyMetadataSafeAsync` 里**，而 DNG 出口
`protectColorMetadata` 为假时走 `CopyMetadataAsync` ⇒ 排除式 `--GPS:all` 单独用不生效
（2026-10-05 实测过这件事，当时只补给了一条分支 = 一半的路径没牙）。修法不是回退搬运：
① 工具加 `-no-gps` 并点名 `gps-suppressed=1`（工具默认仍搬，"不搬"必须显式请求），执行侧与
`--dry-run` 预览侧都按 `Options.StripExifGps` 传同一个真源；② 补删收成一份实现
`ApplyAbsorbStripAfterCopyAsync`，两个复制变体都调它 ⇒ ⑳h 钉"实现恰 1 份、调用点恰 2 处、
删除串只出现在共用实现里"。

**读数**（每条都指到 `tests/output/dng-stdcarry/` 里的日志）：

| 项 | 实测 |
| --- | --- |
| 第 77 条 | **55/0 → 67/0**（`gate77-run6.log`）→ **70/0**（§19，`gate77-app-2.log`）；两把重臂同开 **69/0**（`gate77-heavy.log`，⚠ §19 未重跑；⑯e/⑱f 读数与 10-19 一致：非恒等臂 72,721,702 个平面值不同、环带提升 1.0946/1.0943/1.0954 对预测 1.1117） |
| ⑳a（IDC 件走产品同法） | 源标准位 52 条 ⇒ `缺(不豁免)=0`、`值不等=0`、值档比较 **47** 条（反真空哨兵下限 30） |
| ⑳b | 未跑后置的同一件 `缺=10`，逐条点名 = 0001,0002,011A,011B,0128,9101,9102,A000,A002,A003 |
| ⑳e（负控） | `-no-raw-meta` 那份 `缺=42` ⇒ ⑳a 那个 0 不是空集也不是恒绿 |
| ⑳d | 后置只动元数据：LibRaw 解出 `24,337,152` 点全等；`dngtool -verify` rc=0；Adobe `Validation complete` |
| ⑳m | `-no-gps` 产物 GPS=[] 而键命中=1；默认档 GPS=[35.6812] 键命中=0 |
| 端到端（`e2e-product.log`） | 默认档 GPS=[] 且那 10 件全在场；`--no-strip-gps` 时 GPS 在场（两臂分叉） |
| 同链路另外三条闸 | `verify-metadata-privacy` **7/0** ｜ `verify-dryrun-parity` **16/0**（dng 臂两侧实参逐字相同且都含 `-no-gps`）｜ `_probe-cjk-hardcode-scan` **14/0** |

**未判定 / 边界（别当已修）**：
- ⚠ 本节那张"10 条无发射点"名单已被 **§19 扩成标准位全集**（61 条）；
- 本机四件真件全无 GPS，也没有 `Orientation≠1` 的件 ⇒ GPS、方向位、镜头值三格用的是
  **exiftool 现场注入的合成夹具**（注入本身作前置判据，红则点名 ⇒ 不是拿无 GPS 的源空跑）。
- 放行是有名单的：空/零值可省略只允许 `0x010E`、`0x829D`、`0x920A`、`0x9286`、`0xA405` 五条
  （实测 11 条零/空值里只有这 5 条真的缺席），其余零值标签缺席一律判红；`0x0201/0x0202`
  属"这份文件自己的字节布局"⇒ 不照抄，同一内容由 ⑳j 拿产物自己的 `PreviewImageLength` 对撞（743200 == 743200）。
- 值档只放行 `0x0103 Compression` 一条（它描述**产物自己**的像素编码，源 6 → 产物 7 才是对的）；
  `--values-strict` 可以把它拉回分母当对照臂。
- **本轮未动**的一处既存观察：DNG 出口的复制阶段仍会把源件整包 MakerNotes（实测 93 条）搬进产物
  —— 那是 `CopyMetadataAsync` 的 `-all:all` 形状，与 §16 的丢弃政策是两件事（MakerNote 由 exiftool
  逐个命名读得出 ⇒ 不属于"完全无法解码"），要改得另立判据，不在本轮范围内动它。

## §18 2026-10-20：把"默认剥 GPS"这个残留真值统一掉（用户裁定 A 案）

**起因是我写的一句陈述**：§17 里我写"产品的隐私默认 = `StripExifGps` 默认 true"，用户指出
**口径早改了：默认不勾选、全保留**。回代码量的结果是"半改"——四处里只有一处跟上了：

| 入口 | 改前 | 依据（改前） |
| --- | --- | --- |
| GUI 复选框 | **false**（未勾） | `MainWindow.xaml:849` 无 `IsChecked`；回写只在选预设时（`MainWindow.xaml.cs:6128`，调用点 4841/5947） |
| CLI 不给标志 | true | `CliParser.cs` 的 `opts.StripGps ?? true` —— 三态形参（`:57 bool?`）本意就是"不给=不覆盖模型默认"，被这个 `?? true` 抵消 |
| 模型字段 | true | `FfmpegOptions.cs:525` |
| 预设字段 | true | `PresetData.cs:130` |

⇒ 同一份带 GPS 的源件，走 GUI 有 GPS、走 CLI 没 GPS。仓里本来就留着这件事的自证：
`CliParser.cs:54-56` 写着"只改模型默认值不生效（变异实测 verify-metadata-privacy 仍全绿）"，
TESTING 第 58 条也记着"隐私默认有两处独立真值…建议合并为单一真值（未做）"⇒ **现在做了**。

**改动**：四处默认统一到 false（含 GUI 控件缺席时的回退），另把 9 处把"默认 true"当事实陈述的注释
改成现值并注明是旧史（`RawService`、`QueueProcessor` ×3、`ExifToolService` ×2、`CjxlService`、
`FfmpegOptions`、`CliParser` ×3）。**没动**判据本身：`IsStripRequested` / `CanAbsorbStrip` /
`DngCarriesRawMetadata` 读的都是**开关的值**，与默认值无关；jxl 的 jbrd 取舍读 `StripExplicit`
（只有显式请求才置位）⇒ 默认档仍是逐字节可逆重封装，这次统一不碰它。

**闸的翻向**（第 58 条，2026-09-15 立项以来第一次改断言方向）：
①「默认输出**无** GPS」⇒「默认输出**仍有** GPS」；③「`--no-strip-gps` 时保留」⇒「`--strip-gps` 时必须剥掉」。
两条同尺反向 ⇒ "默认被改回剥"和"显式剥除成空转"这两种坏法各红一条，任何一条单独存在都不算证明。

**读数**（都在盘上，⚠ 这行的 #77 67/0 是 §19 之前的状态，现值 **70/0**）：第 58 条 **7/0**（`A-verify-metadata-privacy.ps1.log`）；同链路 #77 **67/0**、
`verify-dryrun-parity` **16/0**、`_probe-cjk-hardcode-scan` **14/0**。产品端到端三档
（`tests/output/dng-stdcarry/e2e-product-A.log`）：默认档 DNG 的 GPS=「35 deg 40' 52.32" N」在场、
`--no-strip-gps` 在场、`--strip-gps` 为空，而**那 10 个标准位三档都在场** ⇒ 剥 GPS 不牵连别的标签。
另：本轮把 `-no-gps` 接进 DNG 出口的动机（"GPS 搬进容器会打穿默认档"）在 A 案之后不再成立，
但**接线本身保留** —— 它是"用户要剥就写侧不剥、且出声"的唯一实现，且 ⑳m 两臂仍指着它。

**未判定 / 未做**：
- GUI 端到端未跑（闸是 headless CLI 档）；界面初值=未勾这条只由源码证明（XAML 无 `IsChecked`）。
- 用户**已存过**的预设 JSON 若带 `StripExifGps=true`，仍会剥 —— 那是显式存储的选择，本轮不改写别人的预设。
- §17 那张表里"默认档无 GPS"的端到端读数属于**改前**状态（当时 CLI 默认 true），已在那节挂了指针。


## §19 2026-10-20：跨厂商跑量把"10 条名单"扩成"标准位全集"（含我自己造的 8 小时错值）

起因：§17/§18 那份"SDK 无发射点的 10 条"名单只在**索尼两件 + 现场注入的夹具**上量出来。
本轮把 14 件跨厂商真 RAW 全跑一遍，名单立刻不够用 —— CR3 缺 24、RAF 缺 21、MRW 缺 6、
RW2/RWL 各缺 16、NEF/ORF/IIQ/SRW 各缺 1（`tests/output/dng-stdcarry/vendor-sweep5.log`）。

**我自己造的错（跨厂商跑量当场抓到，不是推出来的）**：`-TagsFromFile` 用**裸名**会取错那一份。
CR3 里 `ModifyDate` 同时有 [IFD0]=`2021:05:12 12:21:21` 与 [QuickTime]=`2021:05:12 18:21:21+08:00`
两份，裸名复制取到 QuickTime 那条 UTC 写进 IFD0 的本地时间位 ⇒ **差 8 小时**；
`ColorSpace`/`Contrast` 在源件里也有 [ExifIFD] 与厂商组两份。⇒ 名单里每条**两侧都带组限定**
（`-Group:Name<Group:Name`：目的侧不限定会写错容器，源侧不限定会取错那一份）。

**第二处我自己的方法错**：第一版跨厂商实测（`_vendors.py`）在脚本里**另写了一遍那 10 条名单**
⇒ 它量的是我的镜像而不是产品，"后置有没有接进产品"这件事根本没被判。改成 `_vendors2.py`
跑 `FfmpegGui.exe --headless` 之后才第一次把"非 TIFF 头容器（CR3/RAF/MRW：dngtool 的遍历
直接 `notTiff` 回退，一条 EXIF 都读不到）"与"嵌套 IFD（RW2/RWL）"这两类缺口归到**产品后置**上。

**改了什么（办法）**：
- 后置名单 = `ExifToolService.DngCarry{Ifd0,Time,Camera,Exif,Interop}` 五张分组数组（61 条），
  时间/镜头两组分别由 `StripExifTime` / `StripExifCamera` 跳过，GPS 整组由 `StripExifGps` 跳过。
- **APEX 那一族（0x9201/0x9202/0x9203/0x9205）明确不进后置**：14 件里它们从未报"缺"（SDK 有发射点），
  而复制要经 exiftool 的 PrintConv 往返 ⇒ 实测 Sony IDC 把源里逐位相同的 `434/256` 换成
  连分数近似 `13166/7763`（偏 4e-4）。后置的边界因此回到"**只补没有发射点的**"，
  并由 ⑳h 把这条写成判据（名单里出现这四条即红）。
- 尺 `_check-exif-standard-coverage.py` 加三个分档，性质都是"把混在一格的两件事分开量"，
  不是放宽：`同值异形式`（exiftool 写入会把 800/100 约分成 8/1、450/100 成 45/1 ⇒ 同一个数换写法）、
  `不可比`（`-v2` 对长/二进制值直接印 `[snip]` ⇒ 我的读法看不见值，Olympus 的 0x9286 实测 `-b` 取出 0 字节）、
  `相对偏差`（每条真不等附 `|Δ|/|值|` 读数，"差 0.04%"与"差 3 档"必须能分开）；
  日期串只规范化分隔符（`.`↔`:`），不解析时区；生产者身份四件（Make/Model/Software/ExifVersion）
  从**值档**放行、**存在性照旧必须**（Adobe 的转换器也把 Model 写成去厂商前缀的机型名，PS 按那个键查机型库）。
- 闸：⑳h 的源形锁不再由闸手抄名单，改成锁形状（五张数组在、条目数下限 55、18 条必须点名在场、
  4 条 APEX 必须在场即红、组限定与三道隐私开关各一条）；新增 **⑳n/⑳o/⑳p 三臂跑产品端到端**。
  ⑳n/⑳o 的"后置真跑过"用**产物正控**（`0x011A` 与 Interop `0x0001` 在场 —— 这两条 SDK 没有发射点，
  只有后置能写），不用日志串：实测 `--log-file` 只写汇总、不含逐项日志，拿它当判据会假红。

**读数（都在盘上）**：
- 产品端到端 14 件（`vendor-app-2.log` + `remeasure-2.log`）：**缺(不豁免) 14/14 件 = 0**；
  值档残留 14 条全部落在 APEX/焦距这一族，最大相对偏差 **2.91%**（Olympus E-10 `MaxApertureValue`
  源 206/100 → 产物 2/1），其余 ≤9.7e-3。
- Adobe `dng_validate -3` 对扩展搬运后的 7 件产物全部 rc=0 且打 `Validation complete`；
  LibRaw sensor 平面 7 件逐位全等（CR3 8,551,424 / RAF 6,311,936 / MRW 3,174,464 / RW2 2,015,104 /
  IIQ 10,363,904 / 7S 5,271,552 / ORF 3,799,104 点）⇒ 搬运只动元数据。
- #77 **70/0**（`gate77-app-2.log`；此前 67/0。⚠ 两把重臂 DNGOPC_HEAVY/DNGLENS_HEAVY 本轮**未重跑**
  ⇒ 重臂现值本轮未判，别沿用上轮的 69）；#58 **7/0**；`verify-dryrun-parity` **16/0**；
  `_probe-cjk-hardcode-scan` **14/0**（注释不计入；UI 面 852 ≤ 基线 852 —— CLI 帮助那三条过期声明
  是**就地改写**、没加行）。

**未判定 / 边界（别当已修）**：
- APEX 与 0x920A/0xA20E/0xA20F 的**存储分数**由 LibRaw→DNG SDK 从浮点重算，后置不接这件事（见上）
  ⇒ 差多少已量出来并附在每条读数里，**不写成"已搬对"**。
- 同一文件对同一标签写两份（CR3 0x9202、CR2 0xA20F、IDC 0x9205）⇒ 不同读者取到不同那一份属**源侧歧义**，
  我的搬运不裁决、也不假装裁决过。
- `RPi.raw`、`Sigma_DP1.X3F`：dngtool（LibRaw）打不开 ⇒ 产品 rc≠0 零产物，是点名失败不是静默降级。
- **本轮未判**：ServiceProbe 的 `contract/wire/metaraw/i18n` 四档探针没跑 —— 运行器按 fail-closed 拒跑
  （`src/FfmpegGui/Services/GainMapDecoder.cs` 与 `IsoBmffGainMapProbe.cs` 在 5:10 被**并发会话**改过，
  比 4:58 的构建产物新）。要跑就得重建，而重建会把别人未提交的在制品混进我这份被测件 ⇒ 读数不可归因，
  所以不重建、只登记未判。源码级那两把（`_probe-test-group-coverage.ps1` **#69 = 37/0** 与
  `_probe-cjk-hardcode-scan.ps1` **14/0**）是纯读源，本轮实跑过。
- ⑳p 的跨厂商素材未受版本控制 ⇒ 缺席时点名 SKIP（记未判，不给绿灯）。
- GUI 端到端仍未跑；`--dry-run` 侧只判到参数形态（#77 的 ⑳h 与 dryrun-parity）。


## §20 2026-10-20 晚：追问"私有 IFD 能不能转进标准 DNG 位并被正确识别"（实测，未开工）

问的是可行性，所以这一节只有读数与判据缺口，没有实现。

**一、已经成立的那一层（而且比我上一轮说的更好）**
MakerNote 本来就住在**标准位**里：链 = `IFD0 → 0x8769 ExifOffset → 0x927C MakerNoteCanon(6744 B)`，
其下 `CanonCameraSettings / CanonFocalLength / CanonShotInfo / CanonCameraInfo40D / PictureStyleInfo /
CanonAFInfo2 / CanonFileInfo / CropInfo / AspectInfo` 全在。按"键→值"逐条对撞（不用值集合，那种读法会把
差异洗掉）：**Canon 199 键同名 / 值不同 0 条；Sony 115 / 0；Olympus 48 / 0** ⇒ 携带是真的、值也是真的。
- 缺的两件事：① `MakerNoteSafety (50741)` **从没写过**（规范用它声明"内部偏移是否自包含"）；
  ② 这条携带完全靠复制阶段 `-all:all` 的副作用 ⇒ **没有任何判据看着它**。已经因此丢过一格：
  **Panasonic RW2 的 22 条 PanasonicRaw 私有条目在产物里是 0 条**（源 22 → 产物 0，静默）。
- 反证"随手搬"会坏：把 `-ExifIFD:MakerNote` 裸抄进 dngtool-only 产物后，**子表一条都读不出来**
  （Canon 211→0、Olympus 48→0、Sony 148→0），而 Nikon 那份把 Adobe 校验器打到 **rc=106**
  ⇒ 塞进"私有袋"（`DNGPrivateData 50740`，规范写明那是留给相机厂商自己的 raw 转换器的）不等于被识别。

**二、被正确识别的标准载体（本机一手清单）**
`dng_opcodes.h:29-97` 的 opcode 全集只有 14 种：WarpRectilinear/Fisheye、FixVignetteRadial、
FixBadPixelsConstant/List、TrimBounds、MapTable、MapPolynomial、GainMap、Delta/Scale PerRow/PerColumn、
WarpRectilinear2(1.6)。§5e 的读者实测已经钉住"转成 opcode 后 Adobe 真施加"（暗角角/中比实测 1.1124 vs
表预测 1.1117；逐平面 warp 只动绿平面 95.45%）⇒ 同一条通路可以吃到别家：**Fuji `VignettingParams`/
`GeometricDistortionParams`/`ChromaticAberrationParams`、Panasonic `DistortionParam*`、Olympus
`LensDistortionParams`、Samsung `Vignetting`/`ChromaticAberration`**（成本=每家一张换算表 + 拟合残差闸门）。
另两条有标准位但没做的载体：机内色彩配方 → `dng_camera_profile`（`ProfileName/HueSatMapDims/HueSatMapData1..3`
50936-50939 + 51107 + 52537、`ProfileToneCurve`），噪声测量 → `NoiseProfile 51041`（SDK 有
`tag_dng_noise_profile` 发射点，`dng_image_writer.cpp:3159-3165`），遮罩/坏点 → `MaskedAreas 50830` 与
`FixBadPixelsList`。
分母（14 件私有档条目按标签名分桶，`tests/output/dng-stdcarry/_privclass.py`，合计 **6,304** 条）：
可归到"有标准载体"的：色彩矩阵/白平衡 232、色调曲线/风格 223、裁剪/边界 87、镜头失真 49、色散 17、暗角 14、
降噪旋钮 14（这条只有"测量值"能进 NoiseProfile，"开关/强度"没有载体）；**没有标准载体的**：AF/AE 点阵 49、
厂商未命名/整块加密 5,020，以及"曝光/测光/对焦"343 条里已被 EXIF 标准位覆盖的其余部分。

**三、顺带撞出的一处合规缺陷（它才是"被正确识别"的前提）**
`Nikon_CoolscanV.NEF` 的 DNG —— **dngtool-only 与产品都是 `dng_validate` rc=106**
「Missing or invalid SamplesPerPixel (IFD 0)」⇒ 归因在 dngtool/SDK 侧，与本节的元数据阶段无关。
根因（SDK 源码 `dng_ifd.cpp:3879-3885`）：`piLinearRaw` 要求 `SamplesPerPixel == 相机 profile 的 fColorPlanes`，
而这件是 **3 样点 LinearRaw、无 profile/无 ColorPlanes**（实测：源 Photometric=RGB/Samples=3；
产物 Photometric=Linear Raw/Samples=3/ColorPlanes=空；Sony 对照=CFA/Samples=1）⇒ 校验器按 [1,1] 卡死。
14 件里 **13/14 通过**，而这 13/14 只是本轮跑出来的数，**没有任何在册臂拿着 `dng_validate` 逐厂商判它**。

**四、下一批的账（等一句"做哪条"，各自都能判）**
- a. 修 LinearRaw/ColorPlanes 一致性，并把 `dng_validate` 接进 ⑳p 的每厂商臂（合规性从"偶然跑出来"变成在册判据）。
- b. MakerNote 的携带加键+值逐条判据（复用 ⑳ 的方法）+ 写侧按实测自包含性声明 `MakerNoteSafety`，顺手堵住 RW2 那 22 条静默丢。
- c. 别家镜头表 → OpcodeList3（复用 ⑱ 的读者判据与拟合残差闸门）。
- d. 无标准载体的那一档继续丢，但**写侧点名条数**（现在只有政策句，没有"丢了几条"的读数）。

**撤回**：上一轮结论里"厂商私有 IFD 一律丢弃"**说过头了** —— MakerNote 那一层实际带着、且值逐条相同；
准确说法是"**未声明安全性、也未受判据保护的携带**"，而 RW2 的 22 条是已经发生的静默丢失。


## §21 2026-10-20 晚：私有件锁落地（㉑），并更正我自己两处说法

**做了什么**：新增在册读手 `tests/scripts/_check-dng-private-carry.py`（厂商注记组按"组:名"逐条对撞）
+ 第 77 条 `㉑` 组两臂（重臂档 `DNGPRIV_HEAVY=1`，跑**产品链路**）：`㉑a` Adobe `dng_validate` 逐厂商、
不合格者只允许在点名表里；`㉑b` 携带锁 + 丢弃条数点名。实现文档见 DNG_SONY_IDC §5i。

**分档的权威是工具的回话，不是我的推测**（每个组名都对应一次本机探针
`exiftool -TagsFromFile <源> -<组>:all <DNG>`）：`FujiIFD/FujiIFD2/RAF/RAF2/Version0/Version1/SonyIDC/SR2*`
明说 `Warning: No writable tags set`；`PanasonicRaw/MinoltaRaw/NikonCapture/SamsungRaw/SigmaRaw/OlympusRaw`
不报错但 "0 updated / 1 unchanged"、到达条目 +0 ⇒ 两类的结果一样：**搬运通路到不了**。
所以 §20 里我写的那句"RW2 的 22 条是**已经发生的静默丢失**"**说法不准**：丢失是真的，
但它不是搬运漏抄，而是没有可写目标 ⇒ 准确的表述是"**这类内容只能靠换算进标准载体**（镜头三表 →
OpcodeList3；坏点表 → `opFixBadPixelsList`）"。已在本节更正。

**真正确认的可修缺口只有一格**：CR3 的厂商注记 **198 条整组没进产物**，而我拿"已经有注记块的 DNG"
做目标跑 `-all:all` 时**带进来了 93 条** ⇒ 说明不是格式边界，是复制通路的形状问题（目标件里
没有可挂靠的注记结构时 exiftool 就不写）。`㉑b` 把它登记为 `WARN 可修缺口`（不判红、但必须出现），
新增任何"既不在点名表、又不是无写目标"的整组丢失一律判红。

**我自己的一处读数错（差点据此做出错的结论）**：第一版比较用的是**值集合**
（`{源值} ∩ {产物值}`），于是 Canon 报"值交集只有 93/199"，我当场把它写成"携带了但一半值是错的"。
按 `组:名 → 值` 逐条重算后：**199 键同名、值不同 0 条**（Sony 115/0、Olympus 48/0）⇒ 携带是完好的。
教训就是记忆里那条：判据的形状会造出假象，值集合不是对撞。

**顺带定量的既存合规缺陷（本轮仍未修）**：`Nikon_CoolscanV.NEF` 的 DNG 在 Adobe 官方校验器上
**rc=106「Missing or invalid SamplesPerPixel (IFD 0)」**，dngtool-only 与产品**都是** ⇒ 归因在
dngtool/SDK 侧。根因（源码一手）：`dng_ifd.cpp:3879-3885` 对 `piLinearRaw` 要求
`SamplesPerPixel == 相机 profile 的色平面数`，而这件是 3 样点 LinearRaw、产物里没有 profile ⇒ 按 [1,1] 卡死。
14 件里 **13/14 通过**，而这件事**本轮之前没有任何在册臂在看**（⑳p 只判标准位携带）。
`㉑a` 现在把它钉成"不合格集合必须 ⊆ 点名表"。

**读数（全部在新构建件上复跑：`FfmpegGui.dll` sha256 前 16 = `68DBB4190B013A86`）**：
- 第 77 条默认档 **70/0** + 一行 `SKIP ㉑`；重臂档（`DNGPRIV_HEAVY=1`）**71/0**，`㉑` 覆盖 8 件**产品**：
  私有件锁 8 件全 `priv=0`；`dng_validate` 7 件 rc=0，唯一不合格 = 点名的 `Nikon_CoolscanV`（rc=106）；
  可修缺口 `WARN=1`（CR3 的 198 条注记）。日志 `gate77-priv-heavy-E.log` / 默认档 `linkE-verify-dng-metadata-transport.ps1.log`。
- 同链路：#58 **7/0**、`verify-dryrun-parity` **16/0**、`_probe-cjk-hardcode-scan` **14/0**、
  `_probe-test-group-coverage`（#69）**37/0**（⑪-e 账目 40 = 点名 7 + 族内 20 + 显式 13 + 未归属 0，未因新读手而变化）。
- **这把尺有牙**（错配必须红，实测三臂）：源=IDC vs 产物=7S ⇒ 组内缺键 8、值不同 72 ⇒ 红；
  源=7S vs 产物=IDC ⇒ 缺键 1、值不同 72 ⇒ 红；跨厂商（Olympus 源 vs Panasonic 产物）⇒ 无共同组 ⇒ 打 `SKIP` 不给绿灯。
- 丢弃点名的实际内容（14 件全量，非闸内）：`FujiIFD/FujiIFD2/RAF/RAF2`、`SonyIDC/SR2/SR2SubIFD/SR2DataIFD*/Version0-1`、
  `PanasonicRaw`、`MinoltaRaw`、`NikonCapture` 全部由工具回话归到"无可写目标"；未命名私有 ID 与二进制载荷
  （`Canon:CRWParam/Flavor`、`Minolta:PreviewImage`、`PhaseOne:RawData/SensorDefects`）单独点名，
  其中 `SensorDefects` **有**标准载体（`opFixBadPixelsList`）⇒ 记进 `KNOWN_UNTRANSLATED`，新增同类判红。

**我自己造的一处破坏（是被并发会话的提交消息抓到的，不是我自己先发现）**：改 `--help` 那三条过期声明时，
我把 ASCII 双引号写进了**普通字符串字面量**里 ⇒ `CliParser.cs:440` 起 CS1003/CS1010 一片，**整棵主树编不动**，
别人一次构建被打回。而我那次改完**没有编译就继续跑闸**（用的还是 4:58 的旧件）⇒ 读数当时仍成立，
但树的状态是坏的，且闸看不见这件事（⑳h/⑱g 读的是源码形状，编译不过它照样能绿）。
处置：引号改「」；先**另建输出目录**验语法（`dotnet build -o tests/output/dng-stdcarry/buildchk` ⇒ 0 错 0 警，
不碰被测件），确认后才重建主产物并用新件复跑上面所有数。
教训：改了 `src/` 就当场 `dotnet build`；"闸绿"不等于"树能编"，这两个判据不能互相代。

**本轮未做（各自都有明确前置）**：
- LinearRaw 的色平面声明（需要把 SDK 的 profile/mosaic 装配路径读通，`dngtool` 里现在完全不碰
  `fColorPlanes`/`fPhotometricInterpretation`，改动跨 SDK 契约 ⇒ 单开一批）。
- `MakerNoteSafety (50741)` 的声明：需要一个**逐件可判**的自包含性判据（现在的证据只有
  "子表逐条同值"这种全量对撞，产品里不该每图跑一次），设计未定 ⇒ 不做未测的声明。
- 别家镜头表 → OpcodeList3（Fuji/Panasonic/Olympus/Samsung 各一张换算表 + 拟合残差闸门，复用 §5e/⑱）。
- `PhaseOne:SensorDefects` → `opFixBadPixelsList`（已在 `KNOWN_UNTRANSLATED` 点名，新增同类判红）。


## §22 2026-10-20 深夜：把语料从 14 件扩到 51 件重扫，"这格洞是谁家的"换了答案

扩料来源（全在仓库里、此前只用了第一份）：`tests/output/raw_samples`(14) + `raw_samples2`(10：Nikon D1H/D2H/D2Hs/E5700/**COOLSCAN IV·V**、Olympus E-10、**Sigma DP1/SD14/SD15 X3F**、Minolta MRW、RPi raw) + `raw_samples3`(11：Canon 40D/**PowerShot S2 IS**、Fuji X10/X-S1、**Hasselblad CFV-50c/H3D/Lunar**、**Leaf Credo 40 ×2**、Leica D-LUX5 RWL ×2、Minolta Dynax5D/Alpha-7) + `raw_samples4`(X-A1/X-A10)。
扫法：每件 → `dngtool -e -lossless` → Adobe `dng_validate -3`（脚本 `tests/output/dng-stdcarry/_widescan.py`，日志 `widescan-1.log`；验完即删产物）。派生夹具（gps/sim/rec/ori/lensval/中文名…）已排除。

**读数：候选 51 · 编码失败 4 · 官方校验不合格 3 · 合格但有 WARN 42**

1. **rc=106 不是"Nikon Coolscan 一家"，而是"多样点 LinearRaw 这个形状"，且本机整类全中**：
   `Nikon_Nikon_COOLSCAN_IV_ED_1.nef`、`Nikon_Nikon_COOLSCAN_V_ED_0.nef`、`Nikon_CoolscanV.NEF` **三件全部 rc=106**，
   而校验器在错误之前先给了根因的另一半：
   `*** Warning: IFD 0 AnalogBalance is not allowed with unknown color planes ***`（**6 件**报这条，其中 3 件随后判错）。
   ⇒ 与 §21 的 `dng_ifd.cpp:3879-3885` 对得上：色平面数只能从 profile/ColorMatrix 类条目推，扫描仪件没有 ⇒ 按 [1,1] 卡死。
   **按牌子找会漏，按形状找才找全** —— 这就是把 14 件扩到 51 件换来的第一条。
2. **Foveon X3F 整族进不了 DNG，而且原因不是"没链解码器"**：`Sigma_DP1.X3F / DP1_7 / SD14_8 / SD15_9` **4/4 enc=2**
   `Unsupported file format or not RAW file`；但我先怀疑"build 没带 x3f"，回读构建清单**否证**了：
   `tools/src/dngtool/build/libraw_static.vcxproj:389-390` 明确编了 `src/x3f/x3f_parse_process.cpp` 与 `x3f_utils_patched.cpp`；
   而本机 rawpy（自带另一份 LibRaw）能开同一件，`raw_image.shape=(1792, 2688, 3)` ⇒ **三层 Foveon 数据是拿得到的**。
   ⇒ 成因未判（下一步查 `identify()` 的 X3F 分支 / 件版本 / dngtool 侧的前置校验），**不当已修、也不当"LibRaw 不支持"**。
3. **41/47 合格件都带 Adobe 的几何警告**：`Too little padding on left edge of CFA image` + top edge 各 41 件
   （SDK 原文 `dng_ifd.cpp:4805`）⇒ 我们写的遮罩/有效区边距普遍小于 CFA 周期要求。Warning 不判错，但**这是系统性的**，
   单件看会以为偶发 ⇒ 登记为待查（改它会动像素几何，需要单独判据）。另有 2 件 `Zero entry in LensInfo tag--should be undefined`。
4. **注记携带可外推到新厂商**（跑产品 + `㉑b` 的同一把尺，日志 `axisb-1.log`）：
   `Minolta_DiMAGE_5_3.mrw`、`Nikon_D2Hs_6.nef`、`Olympus_E-10_4.orf` 三件 **`PRIV-CARRY OK`（同名键同值、组内不缺）**
   ⇒ §21 的"档 1 随 MakerNote 块到得了"不是 Sony/Canon 特例。Minolta 的 `MinoltaRaw=24` 整组未携带，与 14 件里同源同结论。
   `Canon_PowerShot_S2_IS_1.cr2` 是**新边界**：源里 exiftool 根本不给 Canon 组（私有组=空）⇒ 携带无从谈起，锁打 `SKIP` 不给绿灯。
5. **⑳ 那把标准位尺在新增件上报红两处，性质不同，都不是新洞**：
   - APEX 一族（0x9202/0x9205/0x920A）相对偏差 6e-4 ~ 2.9e-2 ⇒ §19/§21 已量过的"SDK 从浮点重算"同一类，新厂商同样存在；
   - **新形状**：`Canon_PowerShot` 的 `0x0132 ModifyDate` 源值是**非日期字节**（`v.':..ni!..L.....U`）⇒ 尺按"缺(不豁免)"判红，
     实际是**源值畸形 ⇒ exiftool 拒绝复制**。这一格既不是搬运漏、也不是无标准位，登记为**尺的期望需要第三档**（畸形源值），
     在册臂目前只在 Sony 夹具上判 `不等=0` ⇒ 若把臂扩到 51 件，先补这档，否则会一片红（红得对，但没人看得懂）。

**我自己的一处方法错（本节的探针里）**：`① 可达性探针`把 `-<厂商组>:all` 直接当写目标去试，于是
Minolta/Nikon/Olympus/Panasonic 这些**注记内的子组**全部"没到达、也没回话"。这不能证明"没有可写目标"——
注记子组只能随 `0x927C` 整块走。真正由探针定性的是**独立 IFD 表**那几类（`MakerUnknown`/`FujiIFD2`/`RAF`/
`SonyIDC`/`Version0` 回了 `No writable tags set`），其余必须回到产品级实测（`②`）才算数。
⇒ 结论没变，但证据来源换了；不写清这一点，下一轮就会有人拿这张探针表去证明"某家带不了"。

**下一批的账（新增，等一句"做哪条"）**：b1 X3F 打不开的成因（`identify` 分支/件版本）；b2 CFA 边距警告的
口径（41 件普遍，改它动几何 ⇒ 要单独像素判据）；b3 ⑳ 尺的"畸形源值"第三档 + 把 ㉑/⑳p 的厂商集扩到 51 件；
b4 §21 那三条未做（LinearRaw 色平面、`MakerNoteSafety` 逐件判据、别家镜头表换算）。


## §23 2026-10-20 深夜：内容级换算普查——"私有内容能不能搬进标准 DNG"按语义类定量回答

问的不是"注记块带没带过来"，而是**每一条私有内容的语义，标准 DNG 有没有对应载体、映射是精确还是要拟合、读者会不会施加**。
载体编号全部一手取自 `dng_tag_codes.h` 与 `dng_opcodes.h`（opcode 只有 14 种，见 §20）；
普查脚本 `tests/output/dng-stdcarry/_semmap.py`，**按 exiftool 给的标签名归堆**（每类点名前几条 ⇒ 不猜语义），
语料 = 4 个 raw_samples* 目录里的 **44 件厂商件**（排除派生夹具），私有条目总计 **≈9,517 条**（分类到语义类 1,445，其余 8,072 条名字本身不可分类）。

| 语义类 | 条目 | 主要在谁家 | 标准载体（一手编号） | 映射性质 | 现状 |
|---|---|---|---|---|---|
| 色调曲线/机内风格 | 316 | Fuji 158 / Canon 78 / Sony 42 | ProfileToneCurve(50940)+opMapTable=7→OpcodeList2(51009)+HueSatMap(50937-9) | **只有"生效值"能拟合**；`ColorToneStandard/Portrait…` 这类是菜单预设表 ⇒ 搬进去等于给读者假指令 | Sony 的生效 look 已做（5b/5c/⑯）；别家未做 |
| 镜头标识 | 219 | Canon/Sony/Sigma/Nikon | LensInfo(50736)+EXIF 0xA432-0xA435 | 直接换算 | **已完成**（⑳+⑱） |
| 裁剪/有效区 | 152 | Canon 50 / Fuji 48 | DefaultCropOrigin/Size(50719/20)、ActiveArea(50829)、MaskedAreas(50830) | 精确 | 多数已由 LibRaw 落到产物 ⇒ 需逐件分辨"已做/未做"（未判） |
| 遮罩/黑电平/量程 | 117 | Canon 45 / Fuji 32 / Pentax/Sony/Samsung | BlackLevel(50714)/WhiteLevel(50717)/AsShotNeutral(50728)/AnalogBalance(50727) | 数值换算，**但必须同时声明色平面数** | IIQ/Leaf 的黑电平声明已修（§30，闸 ㉓）；逐行/逐列增量**判做**（残差 ≤4 码）；COOLSCAN 的色平面数那层仍缺 ⇒ 现为"拒绝出件"而非静默产坏件（㉑） |
| 机身标识/计数 | 116 | Sony/Sigma/Canon | EXIF 0xA430/0xA431 + XMP | 已完成（⑳） | ✅ |
| AF/AE 点阵 | 101 | Canon 38 / Sony 13 | **无**（EXIF/DNG 都没有位） | 不可表达 | 继续丢 |
| 厂商未命名/整块 | 91 | Hasselblad 61 | 只有 DNGPrivateData(50740)+MakerNoteSafety(50741) | 存得下、普通软件认不出 | 按 §19 政策丢 |
| 色彩配方/矩阵 | 86 | Sony 34 / Hasselblad 17 | ColorMatrix1(50721)/CameraCalibration1(50723)/ForwardMatrix1(50964) | 语义等价，但**要标定拟合，错了就是对色偏色** | 未做（风险最高） |
| 畸变/几何 | 74 | **Leica 24 / Sony 22 / Fuji 8 / Panasonic 8 / Hassy 7 / Olympus 2** | opWarpRectilinear=1 / WarpFisheye=2 / WarpRectilinear2=14 | 径向系数拟合（Sony 残差 ≤0.1 px） | Sony/Fuji/Panasonic **与 Leica** 已做（⑱/⑱y/§26 —— Leica 走的是同一**容器签名**通路，见 §31）；Hassy 的 7 条其实是 Lunar=索尼旧制式加密区、Olympus/Samsung 无一手语义 ⇒ §31 |
| 预览/图像字节 | 58 | Sigma/Canon/Sony | DNG 预览位 | 布局件不照抄（⑳j 已锁） | ✅ |
| 噪声/降噪旋钮 | 46 | Sony 22 | NoiseProfile(51041) 只收**测量值** | 开关/强度无载体 ⇒ 不可表达 | 继续丢 |
| 时间/时区 | 27 | Sony/Pentax/Sigma/Leaf | EXIF 0x9010-0x9012 + XMP(⑰) | 部分完成 | 部分 ✅ |
| 横向色散 | 21 | Fuji 8 / Sony 8 / Hassy 3 / Canon 1 / Samsung 1 | 与畸变同载体（逐平面系数） | 成对发出 | Sony/Fuji ✅（⑱/§26）；Hassy 的 3 条实为 Lunar 的加密区、Canon 那条是**设置开关**不是表 ⇒ 见 §31 |
| 暗角/渐晕 | 18 | Fuji 8 / Sony 6 / Hassy 3 / Samsung 1 | opFixVignetteRadial=3→OpcodeList3(51022) | 径向多项式拟合（Sony 残差 8.4e-5） | Sony/Fuji ✅；**Panasonic/Leica 这一族只有畸变表、无暗角表**（⑱y 实跑 `lens-opcodes=2`）⇒ §31 |
| 坏点/缺陷表 | 3 | **Leaf/PhaseOne `SensorDefects`** | opFixBadPixelsList=5 / MaskedAreas(50830) | 坐标+值 ⇒ **精确映射** | **已做**（§27 裁决、§28 实现、闸 ㉒；不认类型与越界坐标点名丢弃） |

**由此得出的判据（不只是待办）**：换算的准入门槛不是"有没有载体"，而是**"这条内容是不是本次拍摄的生效值"**。
同一个语义类里两者都有：Sony IDC 的分通道曲线是生效值 ⇒ 已拟成 OpcodeList2；`Canon:ColorToneStandard/Portrait/…`
是整机菜单表 ⇒ 搬进 ProfileToneCurve 会让读者把"没选的风格"当成"这次的颜色意图"。
⇒ 换算前必须先定位"生效的那一条"（索尼的改动点名机制 §10/⑰ 已经证明怎么找：版本 0 是机身初值，生效版本才是）。

**下一批排序（量 × 可判性 × 风险）**：
- P1 黑电平/白平衡族（117 条）→ 50714/50717/50727/50728。语义直接对应，且**顺带修掉 §22 那三件 COOLSCAN 的
  rc=106**（同一处缺色平面声明）⇒ 一格两得，优先。
- P2 镜头族（畸变 74 + 横向色散 21 + 暗角 18 = 113 条，Leica/Fuji/Panasonic/Samsung/Hasselblad）→ OpcodeList3，
  复用 §5e 的换算通路与 ⑯e/⑱f 的读者渲染分叉判据。**每家一张换算表**，且要按 §5e 的教训读权威解析代码
  （darktable/RT）定索引与归一化，不许猜。
- P3 `SensorDefects`（3 条，Leaf/PhaseOne）→ opFixBadPixelsList：量最小但唯一可**精确**映射的。
- P4 色彩配方（86 条）→ 矩阵/HueSatMap：需标定，错了就是对色偏色 ⇒ 排在有像素判据之后。
- 不做：AF/AE 点阵（101）、降噪开关（46 里的大部分）、菜单预设（316 里的非生效部分）、未命名块（91 + 未归类 8,072）。


## §24 2026-10-20 深夜：动手前的实测否证——P1 那格我写错了，撤回

**先量再改**（`exiftool -s3` 逐件读 5 家产品）：

| 件 | BlackLevel | WhiteLevel | AsShotNeutral | AnalogBalance | ColorMatrix1 | ForwardMatrix1 | ActiveArea | DefaultCropSize |
|---|---|---|---|---|---|---|---|---|
| Sony 7S | 512 | 15360 | 0.34925… | 1 1 1 | 0.8346… | 0.6531… | 0 0 1872 … | 2784×1872 |
| Canon 40D | 0 0 0 | 65535… | 0.610532… | 1 1 1 | 0.9798… | 0.6775… | 0 0 1296 … | 1944×1296 |
| Fuji X-S1 | 0 | 4095 | 0.610266… | 1 1 1 | 1.7287… | 0.3839… | 0 48 2144 … | 2848×2144 |
| PhaseOne P40 | 0 | 64508 | 0.902435… | 1 1 1 | 1.0416… | 0.5549… | 6 36 2746 … | 3658×2740 |
| **Nikon COOLSCAN** | 0 0 0 | 65535… | **无** | 1 1 1 | **无** | **无** | 0 0 546 8 | 818×546 |

⇒ **§23 表里"遮罩/黑电平/量程 = 未做"这格写错了，撤回。** 这一族（117 条）已由 LibRaw→DNG SDK 落进标准位；
P1 的"实现"如果照 §23 那张表去做，就是把已有的机制再抄一遍（正是 #69 ⑤ 防再抄锁要抓的形状）。
真正的缺口只剩一处：**非 CFA 多样点的色平面声明**。

**那一处的成因（全部一手）**：写侧 `dng_image_writer.cpp:8091` 用 `rawImage.Planes()` 定 `SamplesPerPixel`；
校验侧 `dng_ifd.cpp:3879-3885` 要求 `piLinearRaw` 的 `SamplesPerPixel == fCameraProfile.fColorPlanes`；
而平面数在 DNG 里**只能由 `ColorMatrix1` 的 count 反推**（`dng_shared.cpp:288-296`：`planes = tagCount / 3`）——
`AnalogBalance`/`AsShotNeutral` 那两条也推平面数，但都带 `fDNGVersion == 0` 前置（TIFF 兼容分支，`:2366-2372`、`:2419-2425`），
真 DNG 用不上。COOLSCAN 三件产物正是"3 样点 LinearRaw + 没有 ColorMatrix1" ⇒ rc=106，
校验器先给的 `AnalogBalance is not allowed with unknown color planes` 就是这条的白话版。

**卡点（不能由我替他选的那一步）**：LibRaw 对该机给出 `color_matrix` 与 `rgb_xyz_matrix` **实测全 0** ⇒ 没有可搬的标定。
两条路各牺牲什么：
- 写 `ColorMatrix1 = identity(9)` ⇒ 合规了，但等于**替设备编了一个基线矩阵**（读者会拿它做色彩变换）⇒ 属于"发明的实现"，
  而且错了就是对色偏色；
- 不出件（点名"该机无标定，LinearRaw 无法合规表达"）⇒ 诚实，但**削掉扫描仪 RAW 的能力**（降级）。
⇒ 按"降级要授权、不发明实现"的规矩，这一格停在决策点，等一句方向。

**关于"全面实现"的范围与预算**：P2 是 5 家 × (权威布局读源 + 拟合 + 残差闸门 + 读者渲染分叉)，P4 需要标定与像素判据，
P3 需要 IIQ `SensorDefects` 的布局与 opcode 写入。本会话剩余预算不足以把它们做成**可验收的整体**，
而半成品正是本仓反复登记过要避免的形状 ⇒ 停在实测否证 + 决策点，不在这里开一条会烂尾的 C++ 改动。
下一批建议按"每家一个可验收单元"分包并行（P2 各家互不依赖、按文件天然分 lane），主循环只做集成与判据验收。


## §25 2026-10-20 深夜：五路 lane 的验收与重排 + COOLSCAN 从"点名放行"改成"拒绝出件"

**lane 执行事实**：5 条只读 lane 并行派出；3 条（fuji / samsung-hasselblad / badpix）在
"每日 Chat 额度用尽"处中断，**没有写 FINDINGS.md，但产物都在**
`tests/output/lens-lanes/<lane>/`（脚本、探针输出、夹具）。主循环逐条复核了它们留下的读数，
不采信"lane 说要做什么"，只采信文件里的数字。

| lane | 结论 | 一手证据（已由主循环复核） | 处置 |
|---|---|---|---|
| panasonic | **不能落地** ⇒ 已解封（§26） | `0x0119` = 32 B / 16×int16；校验和 `c=(73c+byte)%0xffef` 与配对 `PanasonicRaw.pm:697-728` 逐字对上，4 件 `ALL OK`；系数 a/b/c 归属与归一化矩形无一手出处，候选解释角位移跨 17–298 px | §25 当时只记 `lens-convention=unavailable`；§26 用 darktable 一手实现解封并落地 |
| linearraw | **(C) 无诚实基线** | `Nikon Scan` 件被 dcraw 直接判 `is_raw=0`（`dcraw.c:5813-5821`）；LibRaw `adobe_coeff` 无该机 ⇒ `simple_coeff` 返回 0；规范 p.33 要求非单色 DNG 必须有 ColorMatrix1；X3F 的 `USE_X3FTOOLS` 全树未定义（两个 TU 整体 `#ifdef`）| 采纳，落成写侧硬门（见下） |
| fuji | **卡在单位** ⇒ 已解封（§26） | 三表结构、11/10/11 结点、头部 `R_max`（1761 / 2942）与有效区半对角对得上；暗角按 `g=100/T` 拟合 5 项残差 7.18e-5；畸变若取 `D=1e-3·r` 则 even-only 恰合（残差 4.4e-7）——但该残差对**任意标量倍数**都成立，判不出 `1e-3` 还是"像素"；光心取"有效区中心"是推断 | §25 当时不落地；§26 用 dt `_init_coeffs_md_v2` 的 `dist/100+1` 定死单位（lane 的 1e-3 读法差 10 倍）并落地 |
| samsung + hasselblad | **无可用语义** | EX1 确有 `0xa050 int32s[8]` / `0xa051 int16u[22]` / `0xa052 int16u[15]` 三表，但 exiftool 全部标 `Unknown=>1`、只做解密不解释；H3D/CFV 的注记里 lens 类命中 0 条；另实测 H3D/CFV50/EX1 的产物 `dng_validate` rc=0，删掉 ColorMatrix1 才出现硬错 | 不落地 |
| badpix | **载体成立、效果未证 + 相位有冲突** | opcode 5 夹具被 dng_validate 解析出 2878 点并**真施加**（op2 改 2833 点 / op1 改 2860 点）；`size` 不符与 required 未知 opcode 均被拒；**越界坐标（-5 / 99999）不被拒**；`phase_one_correct` 只在 `raw2image` 里跑（`raw2image.cpp:79,330`），dngtool 编码路只 `unpack()` 后直读 `rawdata` ⇒ 产物**没有**施加坏点修复；但所列点在产物里与随机控制无差别（严格邻域均值的 `|Δ|<=1` 命中 1.6% vs 1.7%，中位 `|Δ|` 66 vs 59），且规范 p.112 的 BayerPhase 0–3 与 SDK `IsGreen`（`dng_bad_pixels.h:280-283`）的奇偶映射**正好相反** | 不落地；记两个卡点 |

**我自己的一处假设修正**：初读 `load_mfbacks.cpp` 的 `phase_one_correct` 时先判"LibRaw 已经在 load 阶段修过坏点、
再写 opcode 就是二次修复"；回查调用点后撤回——它只在 `raw2image()` 里被调用，而 dngtool 的 DNG 编码路
（`dngtool.cpp` 的 `unpack()` → 直读 `raw_data`）从不走 `raw2image`。所以 P3 的性质是"**产物确实缺这条修复**"，
不是"已兑现"；不落地的原因换成了上面两条**证据不足**，而不是重复施加。

⚠ 上表是 lane 当轮的判词。其中 panasonic / fuji 两行的"不落地"**已在 §26 解封并落地**
（转机 = 找到 darktable 的一手实现，不是放宽判据）；linearraw / samsung / badpix 三行维持。

### 已落地：非 CFA 多样点无标定 ⇒ 写侧拒绝（不再产出 Adobe 判 106 的件）

`dngtool.cpp` 三处：
1. `colorMatrixUsable` 只在一个**非退化**矩阵真的 AddProfile 后置真（DNG 输入走无损复制；私有 RAW 走 `cam_xyz`）；
2. 私有 RAW 的 `valid` 原只查 `-100..100` 区间 ⇒ **全零也能过**（COOLSCAN 的 `cam_xyz` 正是全零），
   现补 `maxAbs >= 1e-6 && |det| >= 1e-6`；
3. 在 `planes = isBayer ? 1 : 3` 之后、写件之前：`planes > 1 && !colorMatrixUsable` ⇒
   打印 `refuse-linearraw-without-colormatrix`（含 Make/Model/Software/平面数）+ exit 9。
   规约原文与平面数依据：规范 p.33「The ColorMatrix1 tag is required for all non-monochrome DNG files.」
   与同页「Count ColorPlanes * 3」。

**读数（本轮实跑）**：
- `Nikon_CoolscanV.NEF` / `Nikon_Nikon_COOLSCAN_IV_ED_1.nef` / `Nikon_Nikon_COOLSCAN_V_ED_0.nef`
  三件均 exit 9、零产物、日志点名；
- 正向对照：P40+ / Canon 40D / Olympus E-10 / Fuji X-S1 / 一份带 CM 的 DNG 重编码，全部 rc=0 且
  `dng_validate` rc=0（0 Error，仅已知 CFA padding warning）；
- 产品链路（`FfmpegGui.exe --headless`）：Coolscan 项失败、无 dng、log 含点名与 `dngtool-e 退出码 9`；
- 在册 #77 整套**重臂档 71/0**（当时默认档仍 70/0；同日再加 ⑱x/⑱y 两臂后默认档 **72/0**、
  三把重臂同开 **75/0** ⇒ 读数在 §26）——㉑ 从"点名放行的 rc=106"改成"必须拒绝出件"，
  判据读三样（非零 rc / 无产物 / 点名），不再校验一个不该存在的产物。
- 部署：`publish/PLAN/artifacts/dngtool.exe` 已换成本轮构建（旧件备份在
  `tests/output/dng-stdcarry/dngtool_prev_20261020.exe`）。

### 仍未做的与解封条件（不猜、不发明）

| 项 | 现在为什么不动 | 解封需要的那一票 |
|---|---|---|
| ~~Fuji 畸变/CA~~ | **已解封 → §26**：dt `_init_coeffs_md_v2` 给出 `dist/100+1`、CA `ca+1`（lane 的 1e-3 读法差 10 倍） | — |
| ~~Fuji 暗角~~ | **已解封 → §26**：dt v2 的 `vig=V/100` + `buf/=sf` ⇒ 线性增益 `100/V`；中心与 dt 同为图像中心 | — |
| ~~Panasonic RWL~~ | **已解封 → §26**：dt `common/exif.cc:1541-1597` 的字段归属 + `lens.cc:2445-2493` 的两点不动点 | — |
| Samsung EX1 | 三表存在但只有解密、无 ValueConv/公式 | 一个一手解码实现 |
| ~~P3 坏点~~ | **两条卡点已由实测消掉 → §27**：相位档 = `(0,0)` 是否绿点（{0,3}/{1,2}）；坐标基 = 存储整幅绝对坐标；"效果不可证"由**合成植入离群值**夹具取代（不必等一帧坏点可见的素材） | 只剩开关归属那一格（§27 末），属政策选择不是技术缺口 |
| CR3 93/198 | 私有过期，未列入本轮；㉑b 已点名 WARN | 属"复制通路形状"，与标准位无关，单列 |

## §26 2026-10-20 收尾：Fuji / Panasonic 私有镜头表落地为标准 opcode

**为什么再开一轮**：用户要求"不落地的相关继续扩展"。§25 里 Fuji 的"卡在单位"与 Panasonic 的
"系数归属无一手实现"都不是"标准位不存在"，而是**当时没找到可引用的实现**。本轮把 darktable master
的 Fuji/Panasonic 解码读进来后，两家都落成规范 opcode；Samsung/P3 仍缺一手实现或判据，维持不动。

### 一手来源（本地副本 `tests/output/refsrc/`，逐段核过）

| 厂商 | 表结构与换算 | 落点 |
|---|---|---|
| Fuji | `dt_src_common_exif.cc:1408-1495`：三表 23/31/23（nc=11）或 19/29/19（nc=9）；`knots[i]`、`distortion[i]`、`ca_r/b[i]`；CropMode 2/4 ⇒ `cropf=1.25` | `dngtool.cpp:2343-2414` |
| Fuji 渲染口径 | `dt_src_iop_lens.cc:2102-2130`（v1：`dist/100+1`、`ca+1`、`1-(1-V/100)`，随后 **平方**）与 `:2286-2293`（v2：**不平方**，`vig=V/100`；`_preprocess` 尾声 `buf[idx+c] /= sf` ⇒ 线性增益 `100/V`） | `dngtool.cpp:2714-2762`、`:2823-2838` |
| Panasonic | `dt_src_common_exif.cc:1541-1597`：`0x0119` = 16×int16 LE；`(v[7]&0x0f)==1` 才启用；`scale=1/(1+v[5]/32768)`、`a=v[8]/32768`、`b=v[4]/32768`、`c=v[11]/32768` | `dngtool.cpp:2416-2450` |
| Panasonic 渲染口径 | `dt_src_iop_lens.cc:2445-2493`：`Ru = Rd + scale·(aRd³+bRd⁵+cRd⁷)`，两次不动点反解 `Rd = r/f(Rd)`，输出 `dr = Rd/r` | `dngtool.cpp:2839-2865` |

⚠ 两处容易照抄错的细节：
- Fuji 暗角在 dt 的**v1** 路径会平方（`vig*=vig`），v2 不平方。若按 v1 的平方值当增益，会写成
  `(100/V)²`，角部直接从 1.33 变成 1.76。本轮按 v2（现役管线）取 `100/V`。C++ 注释仍引 v1 的行号，
  语义正确但引用不精确 ⇒ 只记在这里，不为一行注释重建出货件。
- 畸变是 `f = 1 + D/100`，不是 §25 lane 曾写的 `D/1000·r`（差 10 倍）。lane 当时也承认"任意标量倍数
  都自洽"——这正是缺一手实现时不能落地的原因。

### 归一化：两套半径基数的换算

Fuji/Panasonic 的表都归一到**可见裁切图**半对角；DNG 的 `FixVignetteRadial`/`WarpRectilinear*`
按读者自己的图像最远角归一。写侧（`dngtool.cpp:2330-2341`）：
`xScale = hypot(w/2,h/2) / hypot(max(cx,raw_w-cx), max(cy,raw_h-cy))`，`center=(cx/raw_w, cy/raw_h)`。
本机读数：X-S1 `xScale=0.978815 center=(0.5000,0.5000)`；DLUX5 `0.924827 (0.4636,0.5000)`；
LX7 `0.949389 (0.4753,0.5000)`。

读手侧（`_readback-dng-lens.py::dng_norm`）**不读写侧日志**，而是从产物自己的
`ImageWidth/ImageLength/ActiveArea/DefaultCropOrigin/DefaultCropSize` 复算同一基数。
⚠ 本机产物的 `DefaultCropOrigin` 是 `type=5 (SRATIONAL)`，只接受 SHORT/LONG 的读手会当场报
`0xC61F 形状异常`——本读手第一版就是这样被抓到的，随后补了 type 5/10 分支。

### 读数（本轮实跑，日志在 `tests/output/dng-stdcarry/`）

| 臂 | 读数 |
|---|---|
| Fuji `_lens_fuji.log` | `nc=11 cropMode=0 cropf=1.00 xScale=0.978815 center=(0.5000,0.5000)`；暗角 `maxres=0.000072 cornergain=1.326260`，`k=0.341394 -0.00802312 0.0262944 -0.0350387 0.0160344`；warp2 三平面 `maxres=0.000282/0.000181/0.000266`；warp1 `0.000004`；`lens-opcodes=3` |
| Fuji `_dv__lens_fuji.log` | Adobe `dng_validate`：`Validation complete`（无 error） |
| Fuji 独立读手 | `LENS-READ OK … 暗角: 残差=0.000072 … warp2 残差 R/G/B=0.000282/0.000181/0.000266 ca=per-plane | warp1 偶次项残差=0.000004`，rc=0 |
| Panasonic `_lens_Leica_DLUX5.log` | `enabled=1 scale=1.000000 a=0.094391 b=0.021332 c=-0.027466 xScale=0.924827 center=(0.4636,0.5000)`；warp2 三平面 `maxres=0.000447`；warp1 `0.000111`；`lens-opcodes=2` |
| Panasonic `_dv__lens_Leica_DLUX5.log` | `Validation complete` |
| Panasonic 独立读手 | `LENS-READ OK … warp2 残差 R/G/B=0.000447/0.000447/0.000447 ca=off | warp1 偶次项残差=0.000111`，rc=0；暗角缺席且**源无暗角表**（RW2 这条不写暗角） |
| LX7 开关 off | 写侧 `lens-skipped=distortion:switch-off`、`lens-opcodes=0`；读手 `--expect-absent` rc=0 |
| Sony 7M2 回归 | 同一读手 rc=0：`0.000319` / `0.000058,0.000074,0.000175` / `0.000053`（与 10-19 的读数一致，说明加两家分派没动索尼通路） |

### 判据的牙（变异必须真的分叉）

`tests/output/dng-stdcarry/_lens_mutation_teeth.log`：

| 变异 | 读数 |
|---|---|
| 对照（原件） | Fuji rc=0、Panasonic rc=0 |
| Fuji 暗角 `k0` 高位字节改成 `0x40`（opcode 3, body+0） | 残差 **21435.346288** > 0.0025 ⇒ rc=1 |
| Panasonic warp2 平面 0 径向首项高位字节（opcode 14, body+4） | 残差 **65505.678443** > 0.0006，另外两平面仍 `0.000447` ⇒ rc=1（只打自己那条） |

⚠ 第一版变异改的是 `body+7`（大端 double 的**尾数最低字节**），两件产物都仍然 rc=0 ——
"把系数打坏"必须改**高位字节**（body+0），否则变异和判据一起假绿。这条自纠也记进日志。

### 门禁与部署

- `verify-dng-metadata-transport.ps1` ⑱x/⑱y 现在各含**两段**：① Adobe 解析出对应 opcode（形状）；
  ② 独立读手从源表逐结点复算（含负控 `--expect-absent`）。首版只锁 ①，本轮补上 ②。
- 默认档 #77 实测 **72 PASS / 0 FAIL**（`tests/output/dng-stdcarry/_gate77_lens_readback.log`）。
  **三把重臂同开**（`DNGOPC_HEAVY=1 DNGLENS_HEAVY=1 DNGPRIV_HEAVY=1`）实测 **75 PASS / 0 FAIL**
  （`_gate77_allheavy_20261021.log`）：72 + ⑯e + ⑱f + ㉑。㉑ 覆盖 8 件产品链路，
  `dv=0/priv=0` 七件 + Coolscan `refuse=1`，CR3 可修缺口的 WARN 仍为 1 条（未变）。
  重臂档在 10-20 那两把同开的旧值 57/0 是加 ⑱x/⑱y **之前**的数，本轮已用 75/0 取代。
- 没加任何新产品选项：`-lens-corr` / GUI 勾选已由 ⑱g 的六处源形锁管着，RAF/RW2 只是
  `ReadLensTables` 的容器分派（`dngtool.cpp:2452-2478`）。
- 出货件仍是 17:30 的构建（`publish/PLAN/artifacts/dngtool.exe`）；本轮之后只改了
  `_readback-dng-lens.py`、门禁脚本与文档，未再动 C++ ⇒ 无需重建。

**产品端到端**（不是只测 dngtool）：`FfmpegGui.exe --headless -i <RAF/RWL> --format dng
--dng-lens-correction true` 两件均 rc=0、各产出一件 DNG，独立读手对两件产物分别复算
rc=0，残差与直调 dngtool 完全相同（Fuji `0.000072/0.000282,0.000181,0.000266/0.000004`；
Panasonic `0.000447×3/0.000111`）。日志：`tests/output/dng-stdcarry/_app_lens_e2e.log`
与 `_app_lens_e2e_readback.log`。⇒ 新增容器只靠 `ReadLensTables` 分派就接到了既有开关，
产品侧没有第二份换算。

### 仍未落地（与 §25 的差异）

| 项 | 现在的状态 | 解封条件 |
|---|---|---|
| Samsung EX1 | 三表存在但无公式：darktable 只清 Samsung2 元数据，dnglab 无 `0xa050/51/52`，RawTherapee 无命中 | 一个一手解码实现（或厂商文档） |
| P3 坏点 | 与 §25 相同：载体成立、效果不可证 + 规范 p.112 与 SDK `IsGreen` 的奇偶映射相反 | 一帧坏点真可见的素材 + 同色邻居判据裁决相位 |
| CR3 93/198 | 与 §25 相同，㉑b 点名 WARN | 单列，属复制通路形状 |

## §27 2026-10-21 P3 的两条解封条件做完（相位档 + 坐标基），实现随后落在 §28

§25 给 P3 记了两个卡点：①所列点在当前帧无异常证据；②规范 p.112 的 BayerPhase 与 SDK `IsGreen` 的奇偶映射相反。
本轮两条都**不需要规范正文、也不需要"某帧坏点恰好可见"**就能判：合成夹具 + Adobe 读者。
夹具与判读手 = `tests/scripts/_probe-badpix-phase.py`（一次性取证，**不在** 77 条清单内）。

### 一手依据（SDK 源码，不是推断）

| 事实 | 位置 |
|---|---|
| `IsGreen(row,col) = ((row+col+phase+(phase>>1)) & 1)==0` ⇒ 四相位只有两档：{0,3} 与 {1,2} | `dng_bad_pixels.h:279-282` |
| 载荷布局 `u32 size(=12+8p+16r) | u32 phase | u32 pCount | u32 rCount | (i32 row,i32 col)* | rect*`，全大端 | `dng_bad_pixels.cpp:694-731` |
| 该 opcode 只接**单平面 + 16-bit**，否则 `ThrowBadFormat` | `dng_bad_pixels.cpp:770-786` |
| `badPoint.v/h` **同时**被用作缓冲坐标与 `IsGreen` 的自变量 ⇒ 坐标基与相位基必然同域 | `dng_bad_pixels.cpp:1053-1059` |
| 主 IFD 只由 `NewSubfileType==sfMainImage` 认定（标签缺席 ⇒ "Unable to find main image IFD"） | `dng_info.cpp:2717-2724` |

### 读数

夹具：合成 64×64 **非压缩** DNG，`GRBG` / `RGGB` 两种排列，三色给三个分离电平（G=1000 R=3000 B=500），
植入 10 个离群值 15000（两类奇偶都有），写进 `OpcodeList1`(51008) 的 `FixBadPixelsList`(ID=5, required)，
观测点 **`dng_validate -2`**（Stage 2：坏点表已施加、仍是单平面 ⇒ 比 `-3` 少一层去马赛克平滑）。
尺子自标定：色间距 = 通道/颜色电平中位数之间的最小差；偏差 = 该点相对**同色邻域**（行列都偶偏移）中位数的最大差。

| 臂 | 读数（`_badpix_phase.log`） |
|---|---|
| base（无表） | 比值 **28.995**，残留 10/10 ⇒ 反控成立：不修就明显离群 |
| GRBG phase=0 / 3 | 比值 **0.004**，残留 **0/10** ⇒ 同色修复 |
| GRBG phase=1 / 2 | 比值 **5.003**，残留 **8/10** ⇒ 跨色污染 |
| RGGB phase=1 / 2 | 比值 **0.004**，残留 **0/10** |
| RGGB phase=0 / 3 | 比值 **4.998**，残留 **2/10** |

⇒ **相位档规则（实测）**：`(0,0)` 是绿点（GRBG）⇒ `phase ∈ {0,3}`；`(0,0)` 非绿（RGGB）⇒ `phase ∈ {1,2}`。
残留数在两种排列下分别是 8/10 与 2/10（不是 10/10），原因是**只有 R/B 点会被污染**：G 点走非绿支时
取 ±2 正交，仍是绿色 ⇒ 无害。⇒ 判据必须两类都种点，否则"另一半恒真"。
坐标级证据（同一批件，`(10,10)` 是 G 点、`(10,11)` 是 R 点）：base=(60003, 60003)；phase0=(4020, **12017**)；
phase1=(4012, **2008**) ⇒ 错相位把 R 点填成了 B 电平，正是 §25 担心的"另一种非绿色插进 R"。

| 坐标基臂（边界 top=4,left=2，和为偶数） | 读数（`_badpix_origin.log`） |
|---|---|
| `coord=abs`（列表用**存储整幅**坐标） | 残留 0/10（相位对时） |
| `coord=crop`（列表用 ActiveArea 裁切后坐标） | 残留 **10/10**，且最大偏差 = base 的 28.995（一点没修） |

⇒ **坐标基（实测）**：`FixBadPixelsList` 的 row/col 是**存储整幅的绝对坐标**，ActiveArea 不重新定基；
相位基与它同域（偶数边界下两档结论一致，加上源码里同一对 `(v,h)` 既当坐标又喂 `IsGreen`）。
dcraw/LibRaw 侧本来就是绝对坐标（`p1raw(row,col)` 按 `raw_height/raw_width` 判界）⇒ 与 IIQ 表直接对得上。

### 我自己在这条链上犯的三处判读错（都当场被自己的数据推翻）

1. **`-1` 与 `-3` 之争**：第一次跑 `-1` 时五臂读数完全相同，我据此写进注释"`-1` 落在施加之前、逐字节相同"。
   随后 md5 复核否证了这个"逐字节"表述（当时同名 stem 的输出已被后续 `-3` 运行覆盖）。重做后的实测是：
   `-1` 三份 md5 **确实相同**（都取到 `edbcc16649e1…`，植入值原样 15000）⇒ 结论成立；`-2` 起三份分开。
   教训：**判据换观测面时输出文件名也必须换**，否则旧件冒充新读数；这条已改成 `obs_*` 前缀。
2. **crop 臂把边界减了两次**（写进 opcode 时减一次、测量位置又减一次）⇒ crop 臂报"零残留"，与 abs 臂并列"都干净"，
   判据自相矛盾才暴露。修正后两臂分叉（abs 0/10、crop 10/10）。
3. **CFA 域用混合颜色取中位数**：切到 `-2` 后四档相位全被判红（比值≈4），因为 R 点(≈12017) 与
   混合邻域中位数(≈G 4012) 天然差 4 个色距 ⇒ 那是尺子错、不是产物错。改成"行列都偶偏移的同色邻域"后恢复分叉。
4. 造件侧还有四条**由读者判出来的**规范细节（不是猜的）：主 IFD 必须有 `NewSubFileType=0`；
   `BlackLevel` 只接 Short/Long/**Rational(type 5)**，写 SRational 报 unexpected type；
   `CFAPattern` 是 **0x828E(33422)**（exiftool 那张表里的 0xA302 是 `CFAPatternExif`，拿它当 CFAPattern 会
   报 "colors not included in the CFAPlaneColor tag"）；缺 `ColorMatrix1` 会被判 unknown color plane count。

### 仍未做：实现本身，卡在开关归属（⚠ 本节当时停在这里；用户随后给的判据把这一格定了，实现见 §28）这一格

P3 的技术不确定项已全部消掉，剩下的是**工程 + 一个不由我定的政策选择**：
- 读源：IIQ 的 `meta_offset` 来自 MOBJ 记录的 **tag 0x0110**（`mediumformat.cpp:117-120`），
  而 LibRaw 公开的 `imgdata.color.dng_levels.rawopcodes[]` 只对 **DNG 的 51009** 填充（`tiff.cpp:1711-1721`）
  ⇒ IIQ 走不到那条公开字段，得自己走 MOBJ：记录 `(tag,len,offset)`，块内 tag `0x0400` = SensorDefects，
  条目 `(col,row,type,pad)` 小端（type 129=坏点、131/137=坏列；`load_mfbacks.cpp:198-300` 同读法）。
- **开关归属（这一格我不替他定）**：坏点表和镜头表一样**改渲染像素**。`-lens-corr` 的既有政策是
  "改渲染 ⇒ 默认关"。三条路各牺牲什么：
  ① 塞进 `-lens-corr` ⇒ 零新选项，但**私自扩大**那个开关的语义（勾"镜头校正"的人不一定要修传感器坏点）；
  ② 无条件搬运 ⇒ 忠实还原（DNG 不会比源 RAW 更难看），但推翻了"改渲染默认关"这条已定政策；
  ③ 新开关 ⇒ 要六处产品接线（真源/异步转参/执行侧/预览侧/CLI/双语词条）+ ⑱g 式源形锁，是**一整批**工作。
  ⇒ 按"降级要授权、不私自扩语义"的规矩，停在决策点等一句方向；本轮不猜着写 C++。

## §28 2026-10-21 IIQ SensorDefects → 标准 OpcodeList1：按"PS 能正确识别就搬"落地

§27 把开关归属留成一道选择题，用户回的是判据不是选项："思考一下这个坏点表是否正确可用？在 DNG 那边是否可以
被正确识别？如果 PS 能正确识别就搬"。本机把它测出来了，答案是"能"，于是按**无条件搬运**落地：
不设新开关、不进 GUI、不进预设、XML/csproj 里不加条目。
与 `-lens-corr` 默认关不矛盾：镜头表是**风格选择**（勾不勾都自洽），这张表是机身对**本次拍摄的传感器内容**的声明，
不写的后果是 DNG 比源 RAW 更难看（机身本来会藏住的点在 DNG 里显形）；要原始 sensor 值仍有两条路——
源件本身、以及 `-embed-raw` 归档档。

### "PS 能不能正确识别"这一格怎么答的（全是本机读数，不是推测）

| 证据 | 读数 |
|---|---|
| ACR 的实现认这条 opcode | 字节层扫 `C:\Program Files\Common Files\Adobe\Plug-Ins\CC\File Formats\Camera Raw.8bi`（235,882,472 B，随本机 Photoshop 2026）：`FixBadPixelsList` 命中 2 次，同表还有 `FixBadPixelsConstant`、`WarpRectilinear2`(2)、`FixVignetteRadial`(3)、`DeltaPerRow`(2)，以及 SDK 的运行时串 `Bad number of vignette opcode coefficients.` |
| 这串不是"只给验证器用的表" | `Bayer Phase` 在该文件里命中 **0** 次 ⇒ 带校验期打印的那条 `#if qDNGValidate` 路径没编进 ACR；而 opcode 名是 `dng_bad_pixels.cpp:587-591` 作为**构造参数**传给 `dng_filter_opcode` 的 ⇒ 名字串在场说明反序列化构造函数被链进去了 |
| Adobe 参考读者读到位并真施加 | `dng_validate -v` 打 `Opcode: FixBadPixelsList, minVersion = 1.3.0.0, flags = 0` + `Bayer Phase: 1`，`Validation complete`；`-2` 渲染两臂分叉见下表 |
| **未直接测的那一层** | 没有真的启动本机 Photoshop 打开一次（会弹 ACR 对话框、动到用户的 PS）⇒ 这一层记**未判**，不写成绿 |

### 代码（`tools/src/dngtool/dngtool.cpp`）

- `ReadPhaseOneDefects`：照 LibRaw 的走法，不另发明 —— `identify.cpp:540-542` 在头 32 字节里找 `IIII`/`MMMM` 当 base；
  base+4 处 `(u32>>8)` 必须是 `'Raw'`(0x526177)；base+8 = 目录偏移；记录 `(tag,type,len,data)` 四个 u32，
  **tag 0x0110 的 data 是内联值**（块偏移，相对 base）；块头 u16 字节序标记 + 跳 6 字节 + u32 = 块内目录偏移；
  块内记录 `(tag,len,data)` 三个 u32（data 相对块首）；tag **0x0400** = SensorDefects，每条 8 字节 `(col,row,type,pad)`。
  ⚠ LibRaw 那个公开字段 `imgdata.color.dng_levels.rawopcodes[]` 只对 **DNG 的 51009** 填充
  （`tiff.cpp:1711-1721` + `identify.cpp:1577`），IIQ 的 meta 走的是 MOBJ tag 0x0110 ⇒ 公开字段取不到，必须自己走。
- 类型集合**只认 129（坏点）与 131/137（坏列）**：本机 P40 有 2 条 `type=138`，dcraw/LibRaw 也不处理
  ⇒ 不猜语义、一条不写，用 ASCII 键 `badpix-unknown-type=2` 点名。
- 越界坐标丢弃并点名 `badpix-out-of-range=N`：§25 量过 Adobe 读者**不拒**越界坐标 ⇒ 写进去会静默改到别处。
- 落点 `negative.OpcodeList1()`：`dng_opcode_FixBadPixelsList(list, phase)`，flags=required，minVersion 1.3.0.0；
  相位按 §27 实测档位——存储 `(0,0)` 是绿点 ⇒ 0，否则 1；而"存储 (0,0) 的颜色" = CFAPattern 原点（有效区）颜色
  ± `(top_margin + left_margin)` 的奇偶。坏列写成 `dng_rect(0, col, rawH, col+1)`。
- 线格式的一处**一手纠正**（我第一版独立读手因此判红，红的是读手不是产物）：
  `dng_opcode_list::Spool`（`:213-219`）每条只写 `ID | minVersion | flags`，**没有独立的记录长度场**；
  那个长度 u32 是 `FixBadPixelsList::PutData`（`:699`）自己写在参数最前面的。我按"framing 有 size、参数里再有 size"
  去解，读出 `size=1 pCount=0 rCount=8` 这种胡话。

### 读数（都在盘上）

| 臂 | 读数 |
|---|---|
| P40 写侧（`_badpix_p40.log`） | `badpix-src raw=2880 pts=2878 rects=0 oob=0 unknown-type=2 幅面=3712x2792`；`badpix-list=applied points=2878 rects=0 bayerPhase=1 丢弃越界=0 丢弃不认类型=2` + 一条 WARNING 点名不认类型 |
| 独立读手 `_readback-dng-badpix.py` | `BADPIX-READ OK 点集逐条相同、坏列相同、不认类型未进、相位档与产物 CFA 一致` |
| ㉒d 真素材相位分叉（`_badpix_realphase.log`） | 两臂不同像素 **2239**，其中 **100.0%** 落在列点 ±2 像素内；5966 个远离列点的对照点两臂 **0** 个不同；列点相对同色邻域中位数的偏差 中位 **45.0**（正确档）vs **167.5**（错档，3.72×），p95 1446.0 vs 3057.0 |
| ㉒c 负控 | Fuji RAF 产物里没有 FixBadPixelsList（读手 rc=0）⇒ 不是逢件就写 |
| 门禁默认档 | **75/0** = 72 + ㉒a/㉒b/㉒c（`_gate77_badpix.log`） |
| 部署 | `publish/PLAN/artifacts/dngtool.exe` = 8,161,280 B / md5 前 12 `ad3f8cd07bc7`，与 build 目录那份逐字节相同；旧件备份 `tests/output/dng-stdcarry/dngtool_prev_badpix_20261021.exe`（`731cda577589`） |

⚠ 本轮我自己造成的两次空转都记在这儿：① 第一次真素材分叉实验**忘了渲染件是有效区**（Stage 2 输出 3658×2740
而不是存储的 3712×2792），拿存储坐标直接量 ⇒ 两臂读数"完全一样"，差点写下"相位不起作用"；减去 ActiveArea 原点后才分叉。
② 上面那条线格式误读。两次都是判据/夹具错，不是产物错。
⚠ §27 里我写过一句"Adobe 自己的 DNG 转换器就带这张表"——**本机没有那个转换器，没测过**，该句已在 §27 就地改掉。

### 仍未做

- 真开一次 Photoshop/ACR 的人眼级确认（上表那一层"未判"）。
- Samsung EX1：仍无一手解码实现（darktable 只清 Samsung2 元数据、dnglab 无 0xa050/51/52、RawTherapee 无命中）。
- CR3 的 93/198 条厂商注记：㉑b 继续以 `WARN 可修缺口` 点名，属复制通路形状，与标准位无关。
- 整轮（77 条在册）与提交：都等单独授权。

## §29 2026-10-21 深化：普查"同类缺陷" + 量到并修掉 DNG→DNG 丢 opcode 列表

用户要求"分析其他私有 RAW 是否也有类似问题"。这一类的准确定义（§28 之后才成型）：
**机身/源件里声明了、有标准位、conformant 读者能解码的内容，在我们的产物里没有**。派了两条只读 lane 普查，
主循环逐条复核 —— **lane 的三条大说法被否证**，而我自己在复核过程中量到一条真的、且更严重的。

### lane 的说法与复核结果

| lane 的说法 | 我的复核 | 结论 |
|---|---|---|
| `canon_600_correct()` 影响本机 **22 件 CR2**（理由："注记里没有 `Equivilent` 键"） | `canon_600_load_raw` 只由 `identify.cpp:1982-2010` 那张**硬编码机型表**（PowerShot 600 / A5 / EOS D30 / D60 等）决定；本机 Canon 语料只有 `EOS 40D`×3、`EOS R6`×2、`PowerShot S2 IS`×1，**一台都不在表里**；`Equivilent` 那条是 `raw_predict6/canon_pow`，与 canon_600 无关 | **否证**（语料里零样本）。类本身成立：`raw2image.cpp:198/549` 的判据是 `load_raw == canon_600_load_raw ∧ S.width < S.raw_width` |
| Leaf Credo 40_8 有 **405 条 type=130 坏行 + 88 条 type=131 坏列** | 独立走一遍 MOBJ：两件 Leaf 的 `0x0400` 都是 `len=16040` = **2005 条，type 直方图 {129:2005}**；lane 自己的算术也不自洽（2498×8=19984 ≠ 它报的 19648） | **否证**；⇒ 我"丢弃不认类型"的名单不需要扩到 130/131 |
| 18 件索尼都有 `0x7308 Sensor Active Area` + `0x730c Sensor Redundancy Markers`（⇒ 可落 FixBadPixelsList） | `exiftool -v3` 在 `Sony_ILCE7S.ARW` 里数到的是 `0x7310/0x7311/0x7312×14/0x7313×14/0x7314×14/0x7315×14`，**没有** 0x7308/0x730c | **本机素材上未复现** ⇒ 记未判定，不据此写代码 |
| Fuji 的 RAF 里"没有机身声明的坏点数据，是纯算法降噪" | 独立检索 RAF 的 `IFD1/IFD2/RD` 三段，未找到坏点形状；lane 用 Phase One 当正控证明其检索方法能命中 | **采信**（有正控） |
| Phase One `0x0403`（光学黑区）被 `phase_one_subtract_black` 逐行减黑，无标准位可表达 | 与另一条 lane 独立同结论；本仓 `dngtool.cpp` 的 DNG 路径确认不做黑电平减法 | **采信"没搬"这一半，但"无标准位"这半句已被 §30 推翻**：区域有 50830 MaskedAreas（SDK 读写都在，`dng_negative.cpp:2633` + `dng_image_writer.cpp:1994`，LibRaw 也解 `tiff.cpp:1691`），逐行/逐列的增量有 50715/50716；真正不做的原因是**信息量与精度**，不是载体缺席——见 §30 的实测残差 |

### 我量到的那条（更要紧，且已在本节修掉）：DNG→DNG 再编码丢掉全部三张 opcode 列表

`dngtool` 的 DNG 输入路只"无损复制"黑/白电平那些标签，**opcode 列表一条都不带**：

| 件 | 再编码前 | 再编码后（修之前） |
|---|---|---|
| `_lens_fuji.dng` | 51022 = 752 B（3 支：暗角 + warp2 + warp1） | **51022 消失** |
| `_badpix_p40.dng` | 51008 = 23,056 B（1 支，2878 点） | **51008 消失** |

⇒ 影响面不只新代码：⑯ 的分通道曲线、⑱ 的镜头校正、㉒ 的坏点表，**在"再编码一次"这个动作后全部静默蒸发**——
正是本轮修的那类缺陷的镜像面。

修法（`dngtool.cpp:4285-4325`）：LibRaw 已经把 `51008/51009/51022` 的原始字节收进**公开**字段
`imgdata.color.dng_levels.rawopcodes[0..2]`（`tiff.cpp:1700-1726` + `identify.cpp:1530-1540`；IIQ 取不到是因为它走 MOBJ，
DNG 取得到），所以用 `dng_stream(ro.data, ro.len)` + `dng_opcode_list::Parse` 把三张列表原样装回 negative。
前提我核过而不是假设：再编码前后 `ImageWidth/ActiveArea/DefaultCropOrigin/DefaultCropSize/BlackLevel/WhiteLevel`
**逐字段相同**（exiftool 实测），⇒ 列表里的坐标仍然指向同一批像素。
解析失败 ⇒ `refuse-dng-opcode-loss`（含列表名、字节数、SDK 错误码）+ exit 9，**不出件**——
"丢掉它 = 换个渲染"，与 §25 结尾 COOLSCAN 那一格同一个政策。

### 读数

| 项 | 读数 |
|---|---|
| 保留生效（直调） | `dng-opcode-preserved=OpcodeList3(51022) bytes=752 支数=3`、`...OpcodeList1(51008) bytes=23056 支数=1` |
| 不重复追加 | 连编两次：`_lens_fuji_reenc2.dng` 仍是 51022=752 B（读手数到 3 支）；`_badpix_reencode.dng` 读手 `pCount=2878 rCount=0` 与源件**逐字相等** |
| 携带件仍正确 | 再编码后的 Fuji 件过镜头读手（残差 `0.000072`，与首次编码同一数）；再编码后的 P40 件过坏点读手 rc=0；两件 `dng_validate` 均 rc=0 |
| 新判据 | **㉒f**（默认档）：DNG→DNG 不丢也不重复 |
| 门禁 | 默认档 **76/0**（`_gate77_default_final.log`）；只开 `DNGBADPIX_HEAVY=1` 时 **78/0**（`_gate77_dngreenc.log`）；四把重臂同开按构造 81，**本轮未测**。⚠ 本节这三个数已被 §30 取代（同日又加 ㉓ 组五臂）：默认档现值 **81/0**，五把重臂同开 **87/0** |
| 部署 | `publish/PLAN/artifacts/dngtool.exe` = 8,161,792 B / md5 前 12 `64705c64e6ed`；上一版备份 `dngtool_prev_badpix_20261021.exe`（坏点carrier 之前）与本轮中段那份 `ad3f8cd07bc7` |

### 本轮顺手补的活格子（㉒e）

真素材里**没有任何一件**声明坏列（三件 IIQ 的 type 只有 129 与 P40 的 2 条 138）⇒ 写侧的 rect 分支与
"越界必须丢"分支在真件上永远走不到。用 `_make-badpix-fixture.py` 造"只改缺陷表、一个像素都不动、文件长度不变"的
Leaf 夹具（自带直方图自检），实测 `rects=2 / 丢弃越界=2 / 丢弃不认类型=1`，产物过独立读手与 Adobe ⇒ 两支不再裸奔。

⚠ 我自己这一节的两次判据错（都当场纠正）：① 新加的 ㉒e/㉒f 首版把 `CK` 的括号多数了一个 ⇒ 整份闸脚本解析失败，
被 PS 解析器抓（顺手把解析检查落成 `tests/scripts/_check-ps-parse.ps1`，按记忆里那条"这类工具不能只存在于被忽略的目录"）；
② 独立读手最初把"越界"只按坏点统计，与写侧的口径差 1 ⇒ 两侧同尺之后 2 对 2。

### 仍未做

- Canon 600 那一类：本机零样本 ⇒ 要做得先有 D30/D60/PowerShot 600 素材，且它是"解码期烘焙"不是"元数据搬运"，需另定政策。
- ~~Phase One 光学黑（0x0403）：无标准位 ⇒ 不做~~ ⇒ **本节这句被 §30 推翻并就地订正**：标准位在（50830 区域 + 50715/50716 逐列/逐行增量）；
  真正"不做"的理由是残差上限只有 4 个 16-bit 码而 50716 只有"每行一个位置"装不下它的左右两半，且区域信息已由 ActiveArea 完整决定。
- 索尼 0x7308/0x730c：本机素材未复现 ⇒ 未判定，等有那种机型的件再说。
- 真开一次 Photoshop 的人眼级确认（§28 那层"未判"）；整轮与提交：等授权。

## §30 2026-10-21 P1 收口：Phase One/Leaf 黑电平声明修补（㉓ 组）+ 同类普查的四格判定

### 机制由三处一手代码钉死（不是推测）
| 位置 | 内容 |
|---|---|
| `load_mfbacks.cpp:717` | `maximum = 0xfffc - ph1.t_black` —— 写在 `phase_one_load_raw_c()` 尾部，**属 unpack()**，所以旧产物的 WhiteLevel 就是它 |
| `raw2image.cpp:76/327` → `phaseone_processing.cpp:51` | 真正的扣减 `val = src - bl` 只在 raw2image 里发生，我们不走 ⇒ 平面仍带偏置 |
| `mediumformat.cpp:143` | `ph1.t_black = data`：MOBJ 顶层 tag **0x021D**，标量**内联**在第 4 个 u32（不是偏移） |
| `dng_linearization_info.cpp:143-152` + `:359` | 读者侧公式 `渲染值 = (v − BlackLevel)/(WhiteLevel − BlackLevel) ×65535`（`scale=1/(white−maxBlack)`、`y = x − fBlackLevel[0][0][plane]`）|

⇒ 旧产物是"分母按扣减后、分子没扣"的自相矛盾声明。

### 影响数（Adobe 自己的渲染，`㉓f` 重臂）
| 读数 | 新声明 (1024/65532) | 等长改回旧声明 (0/64508) |
|---|---|---|
| p0.1（低分位） | 33 | 1073 |
| 中位 | 1788 | 2828 |
| 65535 饱和像素 | **0** | **17** |
| 变异件与原件不同的字节数 | — | **2**（只许动这两处，`ndiff>8` 直接判红） |

两臂之差在低分位与中位上**都恰好 1040** = 1024/64508×65535 ⇒ 整幅被抬 1.59% 满量程，不是"只有黑场"。

### 撤回我上一轮说过的一句话
"跨度仍是 64508 ⇒ **中间调逐位不变**"是**错的**：分母确实没动，但旧声明少扣了一次黑电平，
于是**每个未饱和样本都被抬高 1040/65535**。"逐位不变"只对**存储平面**成立
（㉓c 用 `_cmp-raw-plane-identity.py` 量到 `10363904` 点全等，min=716 max=65508）。

### 修法（`dngtool.cpp` 黑白电平块）与不做什么
只在 `isBayer ∧ ¬outFloat ∧ dng_version==0 ∧ blackLevel==0 ∧ t_black>0 ∧ t_black+maximum≤65535` 时
把声明改成 `BlackLevel←t_black`、`WhiteLevel←maximum+t_black`；8-bit 出口沿用既有的 `(x+128)/256` 同一条缩放；
点名 ASCII 键 `p1-levels`。**不加开关**——这不是"改渲染的内容搬运"，是把已经发出去的假标签改对；
**不替源扣黑电平**——平面里实测存在 716..1023 这一段（低于声明黑电平的噪声），扣了才是真丢数据。

### 同类普查另外四格的判定（#32 收口）
- **逐行/逐列黑电平**（`_probe-iiq-black.py` 一次性取证，不在 77 条清单）：
  | 件 | 数组 | 条数 | 值域 | 两半不等的条数 | 最大半差 |
  |---|---|---|---|---|---|
  | P40 | 0x0223 按行 | 2792 = raw_height | [−6,0] | 1620 | 2 |
  | P40 | 0x0225 按列 | 3712 = raw_width | [0,2] | 2254 | 2 |
  | Leaf 40_7 | 0x0223 / 0x0225 | 5536 / 7372 | [−4,1] / [0,2] | 2657 / 4307 | 4 / 2 |
  每条存 **2 个 int16** 按 split_col/split_row 取半，而标准位 50715/50716 只有"每列/每行一个位置"
  ⇒ **不可精确表达**；可表达部分的误差上限 4 个 16-bit 码 = 0.006% 满量程，比刚修的黑场偏置小 260 倍、
  低于 16-bit 渲染的量化地板 ⇒ 按"不做半截推断"**判做**（不做代码，残差在此留数）。
- **MaskedAreas(50830)**：载体在（SDK `dng_negative.cpp:2633` 写、`dng_ifd.cpp:2065` 读，LibRaw `tiff.cpp:1691` 也解），
  所以 §29 里"无标准位可表达"那半句已被就地订正；但"哪些像素是光学黑"已由 `ImageWidth/ImageLength`+`ActiveArea`
  完全决定（full−active），再写 50830 **不增信息** ⇒ 不算同类缺陷。
  ⚠ 这句话的**适用边界要说清**：它只对"光学黑=边距"的件成立。LibRaw 的 `sizes.mask[]` 全部来自
  `identify.cpp` 里按机型硬编码的表（`:1060-1081` Canon 表、`:2034-2060` PowerShot SX220/S120/G16），
  其中 **G16 那条是 `rows 0..80 × cols 0..16` 而它的 `top_margin=29`** ⇒ 矩形**压在有效区内部**，
  那种件就必须写 50830（ActiveArea 表达不了）。本机语料里**没有** G16/S120/SX220 ⇒ 这一格记**未判定**，
  不据此写代码，也不许反过来把它当"已排除"。
- **Sigma/Foveon `BadPixels`**：`LibRaw/src/decoders/` 里**没有 foveon 解码器**；`BadPixels` 在 `LibRaw/src` 的
  唯一命中是 `rawspeed_glue.cpp:247` 的 `d->interpolateBadPixels = FALSE`（开关赋值，不是读者），
  dcraw 的 `foveon_camf_matrix(dim,"BadPixels")`（`tools/src/dcraw/dcraw.c:3532`）没有被任何在编代码调用
  ⇒ 这一格先被"X3F 在本机 LibRaw 根本开不了件"挡住，元数据层无话可说；lane 的"0 命中"计数要改成
  "1 命中、但那 1 命中不是读者"。
- **平场块 0x0401/0x0410/0x0416/0x040b/0x041f**：全部只在 `phase_one_correct()`（`raw2image.cpp:79/330` 才调）里生效，
  即"源没烘焙、要我们施加"的那一类 ⇒ 属于**改渲染 + 需拟合**（网格带斜率插值、按 CFA 只施加偶数列 `:180-185`），
  与 §25 的开关政策同一格，不在本轮做。

### 我这轮自己的三处 harness 错（都当场抓住并改掉）
1. 新写的 `iiq_black_level()` 按 TIFF 习惯把 MOBJ 的 4 字节标量当**偏移**去取 ⇒ 读出 337978587
   （= 文件第 1024 字节处的垃圾）而函数不报错。判据落成"认得 MOBJ 标量是内联"并写进 docstring。
2. 变异臂第一版按"偏移+8 字节"改写 WhiteLevel ⇒ 该标签其实是 **SHORT 内联**（type=3 cnt=1），
   差点改成"两臂同一张图"的假绿；改成按 byteCount 分内联/外链两条路，并加两条自检：
   回读必须是 (0,64508) + 与原件不同的字节数 ≤8。
3. `_readback-dng-badpix.py` 拆层重构时我先写出一段自相矛盾的 `mb = (hit[2] if e=='<' else hit[2])`
   与 `base = e and b.find(...)`（从众语法）⇒ 靠"先 py_compile 再实跑 ㉒ 的原判据"抓回，重构后 ㉒a/㉒b 仍 rc=0。

### 读数
| 项 | 读数 |
|---|---|
| 门禁默认档 | **81/0**（76 + ㉓a–㉓e），`tests/output/results/_lvl/gate77-default.log` |
| `DNGBADPIX_HEAVY=1 DNGLEVEL_HEAVY=1` | **84/0**，`_lvl/gate77-heavy.log` |
| 五把重臂同开（`DNGOPC_HEAVY=1 DNGLENS_HEAVY=1 DNGBADPIX_HEAVY=1 DNGPRIV_HEAVY=1 DNGLEVEL_HEAVY=1`） | **87/0**（**最终件 `cfddefceb68a0d5a` 复跑**，`_lvl/gate77-allheavy2.log`；先前用 `531456046a13bb8a` 那版也是 87/0，两版只差注释与一行日志文案）。三条 SKIP 是"源无有效值"的 FNumber/FocalLength/LensModel，属④组既有口径，不是未跑的臂 |
| 部署 | `publish/PLAN/artifacts/dngtool.exe` sha256 前 16 `cfddefceb68a0d5a`（= `tools/src/dngtool/build/Release/dngtool.exe` 同值；默认档 81/0 是用**这一版**复跑的，`_lvl/gate77-default2.log`）；上一版 `531456046a13bb8a` 只差注释与那行日志文案；修补前的旧件备份在 `_lvl/dngtool.pre-levels.exe`（gitignored） |

## §31 2026-10-21 动手 A 类（镜头族）之前先把账本纠正：普查按 Make 归堆，把"表在谁家"记错了

用户说"先动手 A 类"。动手前的第一件事是**回材料核每一条**，结果三条里有两条我上一轮报错了：

| 我报的说法 | 实测 | 纠正 |
|---|---|---|
| "Leica 24 条有表没换算" | `ReadLensTables` 的容器分派看**签名**不看 Make（`dngtool.cpp:2600-2605` 认 `II U\0` = PanasonicRaw）⇒ D-LUX 5 的 `.rwl` 早就走 Panasonic 通路；**而且 ⑱y 的"开关 on 正控"用的就是 `Leica_DLUX5.RWL`**（闸脚本里的 `$panaOn`）| Leica **已搬且已入闸**。三件实跑：`lens-kind=panasonic … lens-opcodes=2`，残差 warp2 `0.000447`×3 平面、warp1 `0.000111` |
| "Hassy 7+3+3 条畸变/CA/暗角" | `Hasselblad_H3D.3FR` 与 `CFV-50c` 的 exiftool 全表里**没有任何镜头表**；13 条全部来自 `HASSELBLAD_Lunar_4.arw` —— 而 Lunar 是索尼机身，表在 **Sony 旧制式 Tag94xx** 区：`0x064a VignettingCorrParams int16s[16]`、`0x066a ChromaticAberrationCorrParams int16s[32]`、`0x06ca DistortionCorrParams int16s[16]`（本机 `exiftool_files/lib/Image/ExifTool/Sony.pm:8942-8955`），该区要过 `ProcessEnciphered`/`Decipher`（`Sony.pm:11539`）| 不是"另一家"，是**同一族的第二个存放制式**（加密区）。实跑点名 `lens-tables=absent (图像 SubIFD 里没有 0x7032/0x7037/0x7035)` ⇒ 现有索尼通路读的明文位在这件上确实不存在 |
| "Samsung 1 暗角 + 1 CA"，而我第一次 grep 报 0 命中 | census 的参数是 `-a -u -G1 -s -e`，我第一遍用 `-a -G1 -n`（**没带 `-u`**）⇒ 0 命中是我的读法废的。带上 `-u` 后 EX1 出三张表：`0xa050 Vignetting`(int16u×15)、`0xa051 ChromaticAberration`(×22)、`0xa052 Distortion`(int32s×8) | 表在盘上；但 exiftool 给这三个定义标了 **`Unknown => 1`**（`Samsung.pm:630-651`）⇒ 只有数据类型，**没有语义/量纲**。同族教训：负断言的检索参数必须与被比对方一致 |

**为什么这三家今天不动手（不是拖）**：本机与可达的公开实现里没有任何一方解析这些表——
LibRaw 不解析（`grep -n "0x064a|1610|1642|1738|Tag9405" src/metadata/*.cpp` = 0 命中，公开结构里也没有 `libraw_lens_t`）；
dcraw 无索尼镜头表；`tools/src` 下没有 darktable/rawspeed checkout；外部检索只回到 exiftool 的标签页与 SEO 聚合页（聚合页不能当依据）。
按"取不到原文就别改像素 / 不许凭记忆写常数"，**给一张没有一手量纲说明的表做拟合 = 造观感**，比继续丢更坏。
另有硬缺口：Lunar / EX1 / E-10 **各只有 1 件样本** ⇒ 连"这张表随镜头/焦距变不变"都判不了，没有分叉轴就没有判据。

**要动它们的前置条件（可执行，缺一条就不做）**：
1. 取到外部一手解析并**镜像进仓**、把出处钉到行（与 §5e/⑱ 同法：darktable `common/exif.cc` + `iop/lens.cc`，或 rawspeed 对应 Parser）；
2. 同机型**第二件样本**（不同镜头或不同焦距）做常量对照 ⇒ 才有"表真的变了"的正控；
3. 换算表 + 残差死线写进读手 + ⑯e/⑱f 的读者渲染分叉臂 + 变异验牙。
成本：Lunar 一支 ≈ Decipher 移植（约 40 行 C++，须先复现 exiftool 的权威字节）+ 一张换算表 + 读手扩形 + 4 臂；
Samsung/Olympus 各再加"语义出处"这一层，**当前取不到 ⇒ 排在其后并等材料**。

**A 类纠正后的真实清单**：镜头族只剩「同一 Sony 族的加密区存放」（Lunar 13 条）+「无一手语义的 Samsung/Olympus」（4 条）；
色调曲线/机内风格的**别家生效值**与色彩配方 86 条仍是 A2/A3，两格都卡在"先定位生效那一条 / 先有标定依据"，不是缺 plumbing。

## §32 2026-10-21 凌晨：去 GitHub 取一手材料核 §31 的三家（结论：三家仍不动手，但拿到两条可引的硬事实）

先答工具链：**RAW→DNG 只有 dngtool 一条路**（`tools/src/dngtool/dngtool.cpp`，链 LibRaw 静态库 + DNG SDK 1.7.1 + libjxl 0.8；
GUI 侧 `RawService` 只认 `PLAN/artifacts/dngtool.exe`，缺件即点名不回退 ffmpeg）。所以"别人怎么做"要看的不是转换器而是**谁解析这些私有表**。

镜像（gitignored，`tests/output/upstream-ref/`，取回后只 grep 不整份入上下文）：

| 项目 / 取回件 | 提交 | 量到的事实 |
|---|---|---|
| darktable `src/common/exif.cc`（6821 行，md5 前 8 `971333c2`） | master `57340f7d129c`（2026-10-20） | Sony 三表**只认新制式**：`Exif.SubImage1.{Vignetting,ChromaticAberration,Distortion}CorrParams`，DNG 回环时等价于 `Exif.Image.0x7032/0x7035/0x7037`（`exif.cc:1385-1391` + 映射表 `:3257-3259`）。**校验规则在 `:1396-1399`**：`nc = dist[0]`、`nc ≤ 16`、`2*nc == ca[0]`、`nc == vig[0]`；CA 是"前 nc 条 R、后 nc 条 B"（`:1401-1405`）。0x064a/0x066a/0x06ca（Lunar 那族加密区）**全文件 0 命中** |
| darktable 同一文件 + `src/iop/lens.cc`（4920 行，`de4ef51c`） | 同上 | Olympus 段（`exif.cc:1497-1538`）读的是 **`Exif.OlympusIp.0x150a`**（4 值：kd2/kd4/kd6 + 角半径）与 **`0x150c`**（6 值：car0/2/4 + cab0/2/4），`lens.cc:2382-2398` 用它们 ⇒ 这是 **E-M1 世代**的表；本机 E-10 的 `[Olympus] Lens Distortion Params`（6 值）**不是同一个键**，无人解析 |
| darktable 同一文件 | 同上 | Samsung 的 `0xa050/0xa051/0xa052` **0 命中** |
| rawspeed 文件树 | develop `0c16fee77725`（2026-10-12） | Sony/Samsung/Olympus 只有**解压器**（`src/librawspeed/decompressors/{SonyArw1,SonyArw2,SamsungV0-2,Olympus}Decompressor.cpp`），元数据侧没有这三家镜头表 |
| LibRaw `src/metadata/makernotes.cpp`（817 行，`46798d06`）+ 本仓链接的整棵 `tools/src/LibRaw/src` | master 该文件 | `0x064a/1610/1642/1738/Tag9405` **0 命中**；公开结构里也没有 `libraw_lens_t` |

两条可以直接用的收获：
1. **我们已有的 Sony 通路本来就在跑 darktable 那条硬校验**：`dngtool.cpp:3004` 的
   `ca[0] == 2*nc && ca.size() >= 1+2*nc`（nc 取 `dist[0]`，`:3003`）与暗房规则逐字一致；黑电平臂另按 `vig[0]` 自取（`:2900`）。
   ⇒ 不必新加校验，**把出处钉上就行**。
2. **Lunar 那一族用同一条规则一量就不成立**（实测：`dist[0]=4` 而 `vig[0]=0`、`ca[0]=-1176`）⇒ 旧制式不是"新布局换个标签号"，
   是另一套 framing。这给 §31 的"不猜"补了一条**可复算的证据**，而不是只靠"没人解析"。
   > ⚠ 2026-10-22 **更正本条归因**（见 §41.1-2）：数字本身是**解密后**的 `Tag9405a` 那一份，没读错；
   > 但"旧制式是另一套 framing"判错了——同一件的 `SR2SubIFD` 那份**带计数前缀**，规则在它上面成立
   > （`nc=11`、`2nc=22==ca[0]`、`nc==vig[0]`），且与图像 SubIFD 的 `0x7032/35/37` 整段逐字节相等。
   > ⇒ 正确的说法是"旧制式有**多种存放**，其中无前缀的那份不适用这条规则"，不是"另一套数学"。

语料侧的对照读数（同一把尺，`exiftool -a -u -G1 -s -e` 取三表首元素）：
`Sony_ILCE7M2_IDC.ARW` `dist[0]=16 vig[0]=16 ca[0]=32`（合规则）｜`Sony_ILCE7S.ARW` `dist[0]=0 vig[0]=11 ca[0]=22`
（畸变那支本就无表，vig/ca 自洽）｜`HASSELBLAD_Lunar_4.arw` `dist[0]=4 vig[0]=0 ca[0]=-1176`（**不合规则**）。
⇒ 本轮**不改产品代码**；三家仍按 §31 的三条前置条件等材料。

## §33 2026-10-21 凌晨：扩围到 GitHub 全仓扫描（dnglab / RawTherapee / Exiv2 / metadata-extractor / LibRaw 上游）

取数脚本落在 gitignored 的 `tests/output/upstream-ref/`（`survey_upstream.py` 全仓扫描、`survey_focus.py` 定点、
`survey_two_more.py` 补两家），镜像文件都留在 `focus/` 与同名子目录里，**可复跑复核**。
出口要先走本机 SOCKS5（`127.0.0.1:10808`）——`urllib` 直连被反复 reset，一个文件能卡满 60 s（第一版就白跑了一轮）。

| 仓 @提交 | 结果 |
|---|---|
| Exiv2/exiv2 @main `081bb55f06c9` | **收获最大的一家**：① `src/tags_int.cpp:814-819` 明确定义新制式 `0x7032/0x7035/0x7037 = Vignetting/CA/Distortion CorrParams`（与 darktable 读的同一族）；② `sonymn_int.cpp:2305-2331` 给出**索尼注记的加/解密算法**（256 项 `i³ mod 249` 码表，`sonyTagDecipher/Encipher`，在 `tiffcomposite_int.cpp:1108-1110` 接线）——这正是 Lunar 那一族要的插装；③ `olympusmn_int.cpp:165` 有 `{0x0206, "LensDistortionParams"}`（E-10 那一族）与 `:986` 的 E-M1 段 `0x1011`；④ `tags_int.cpp:2129` 另给了尼康 `0xa40f/0xa410` Distortion/CA 的**存在性**。**但三族数组内部的字段含义（哪个是条数、半径基、增益基）仍然没有任何注释** |
| LibRaw/LibRaw @master `2caab57e269c` + 本仓链接的整棵 `LibRaw/src` | `0x064a/0x066a/0x06ca`、`0xa050/51/52`、OlympusIp `0x150a/0x150c` **全部 0 命中**；`src/metadata/olympus.cpp:158` 的 `case 0x0206` 是 **E-System 的另一目录**（`ilm.MaxAp4MaxFocal`，光圈），与 Exiv2 里 E-10 的 `0x0206 = LensDistortionParams` **同号不同目录** ⇒ 又一个"按号取数必须带限定目录"的活例（[[metadata-copy-needs-group-qualification]] 同类） |
| dnglab/dnglab（同类的私有 RAW→DNG 转换器）+ 其内嵌 rawler | 已落盘的 20 个候选文件里 `vignett/distortion/chromatic` **0 命中**；它带的 `lens_make/lens_id/lensfstops/nef/lensdata.rs` 都是**镜头身份**，不是厂商校正表 ⇒ 与我们 §26 的定位一致：转换器圈普遍把"表"交给 Lensfun/ACR，不自己换算。（sha 等全仓扫描那轮跑完再登记） |
| RawTherapee/RawTherapee | 扫描仍在进行（30+ 文件已落盘，暂无三族命中），跑完补登 |
| drewnoakes/metadata-extractor | **本轮没查到 ≠ 没人做**：递归列目录超出 GitHub 未认证配额（`api.github.com` 403），两次路径猜法都 404。要么下次带 token 查，要么直接 `git clone` 后本地 grep |

**可执行的下一步（按解锁度排）**：
1. Lunar 那一族现在**只差字段含义**：插装算法（Exiv2 的码表）+ 标签号与数组长度（exiftool `Sony.pm:8942-8955`）都已一手可得；
   缺的是"16/32/16 里第几个是条数、半径以什么为单位、增益以什么为基"。唯一可能的推断路径是**拿一件同时带新旧两制式表的身体**对拍，
   本机没有 ⇒ 要么外部找到一件，要么维持不做。**这条不猜的界线不动**。
   > ⚠ 2026-10-22 **本条两处都已过期**：①插装**不缺**（两把密码本地可得，见 §41.1）；②"本机没有那件身体"**判错了**——
   > `tests/output/raw_samples/Sony_ILCE7M2_IDC.ARW` 同时带图像 SubIFD 的 `0x7032/35/37` 与 SR2SubIFD 的 `0x797D/80/82`，
   > 实测整段逐字节相等、开关也逐个相等 ⇒ 对拍就在本机做完（§41.2），路由已落地（§41.3，闸 ㉙ 组）。
   > 还活着的界线只有一条：`Tag9405a` 那**无前缀**的一份约定仍未定（本机只一件旧制式身体 ⇒ 没有分叉轴，不猜）。
2. 尼康 `0xa40f/0xa410` 是这次扫描里**新出现的一格**（Z 系列的畸变/CA 开关与参数），本机语料没有 Z 件 ⇒ 登记为"素材缺"，不当已知能力。
3. metadata-extractor 用带 token 或 clone 的方式补查，一次就够。

## §34 2026-10-21 凌晨：lane-bd 回话与本册一条在册读数的推翻——㉑b 的「93/198」是写死的字符串

四路 lane 里 **lane-bd** 先回。它带回一条与在册数字冲突的读数（"CR3 在 dngtool 产物上跑 `-all:all` 只到 0/198"），
而 §21/㉑b 记的是 93/198。主循环**自己复量**裁定，两边都不对：

| 口径 | 复量读数 | 说明 |
|---|---|---|
| dngtool 产物**未跑后置** | Canon 组条目 = **0**（源同尺 = 224） | 工具侧确实一条注记都不写（`dngtool.cpp:2187/2193-2198` 判 CR3 为 ISO-BMFF ⇒ `exif-std-unmapped=notTiff`）⇒ lane-bd 的"0/198"量的就是这一层，但它把这当成"通路天花板"，错 |
| ⚠**本行已被 §39 撤回**：那是 `-all:all` 写在 `-tagsFromFile` 之前的非规范顺序造出来的，**产品命令形状下到达数 = 0**。同一件产物跑 exiftool 后置（`-all:all -tagsFromFile 源`） | Canon 组条目 = **217**；按**不同键名**算：源 222 键 ⇒ 产物 217 键，**缺 5** | 缺的 5 条逐名点出：`CompressorVersion`、`ThumbnailImage`、`Unknown_CTData/Unknown_CCDT`、`Unknown_CTBO`、`Unknown_free` ⇒ 前三类是 exiftool **无可写定义**/被拒的缩略图，不是格式边界 |
| 后置后的合法性 | `dng_validate` 仍 `Validation complete`；但 **`MakerNoteSafety(50741)` 是空的**（后置没写它） | "能带进来"与"带得合规"是两件事，后者是这次复量新暴露的缺口 |

**判词：㉑b 那句"实测 -all:all 能带 93/198 条"不是读数，是硬编码字符串**——位置
`tests/scripts/_check-dng-private-carry.py:64`（`KNOWN_OPEN_DEFECT = {('Canon', '.cr3'): '实测 -all:all 可带 93/198 条 …'}`），
它随 ㉑b 的 WARN 文案打进报告 ⇒ 违反本册反复立的"文档里每个计数都是读数、判据消息不许转抄结论"。
93 的真实出处是 §21 那轮**另一种目标件**（已存在 Canon 注记块时）的读数，被当成通路常数留在了字符串里。

**要做的修法**（等 lane-c-policy / lane-f-docs 回来一起集成，避免两个口径并存）：
① 把该常数改成**现场测量**：后置到达计数（不同键名）+ 缺项逐条点名 + `MakerNoteSafety` 存在性判据；
② 判据分两层写（工具侧 0 / 后置侧 ~~217~~ ⇒ §39 复量后两面都是 0），别再合成一个数、也别用第二种命令形状；
③ 负控沿用同一把尺在未后置件上必须报"缺=全部"，防"常数 0"与"空集"两种假过。默认档臂数不变（81/0），只换 ㉑b 的文案与判词强度。

## §35 2026-10-21 凌晨：E 类 X3F 打不开的根因坐实——`USE_X3FTOOLS` 这个宏在整条构建链里从未定义

lane-e-x3f 的判定是 A 类（库里没有 X3F 解码代码），**成因是整份源文件被条件宏挡掉，不是源文件没进清单**。
主循环逐条复量，五条全部成立：

| 复量项 | 读数 |
|---|---|
| 条件宏包住了整份文件 | `tools/src/libraw/src/x3f/x3f_utils_patched.cpp:1` 与 `x3f_parse_process.cpp:47` 都是 `#ifdef USE_X3FTOOLS` |
| 构建定义里确实没有它 | `tools/src/dngtool/CMakeLists.txt:113-118` 的 `target_compile_definitions(libraw_static PUBLIC …)` 只有 `USE_DNGSDK`/`qDNGSupportJXL`；全仓 `USE_X3FTOOLS` 只出现在 LibRaw 的 Changelog/doc 与注释里 |
| 编出来是**空翻译单位** | `build/libraw_static.dir/Release/x3f_parse_process.obj` = **535 B**（同目录 `identify.obj` = 241,333 B） |
| 失败点精确到行 | `src/metadata/identify.cpp:688` 命中 `FOVb` → `:690` 调 `parse_x3f();` 且自带注释 `/* Does nothing if USE_X3FTOOLS is not defined */` → `raw_count` 保持 0 → `src/utils/open.cpp:1355` `if (P1.raw_count < 1) return LIBRAW_FILE_UNSUPPORTED`（-2 = `libraw_const.h:815`，文案由 `dngtool.cpp:457` 打印） |
| 与 rawpy 的二进制对照 | `publish/PLAN/artifacts/dngtool.exe` 里 `x3f_*` 串 **2** 次（是 `libraw.h:505` 那个恒存在的字段名），rawpy 自带 `raw_r.dll` 里 **7** 次 |

排除项也成立：C（文件子类型/加密）不成立——四件头都是 `FOVb`、版本 `0x00020002`/`0x00020003`，件内 `SecT`/`dpQu`/`Encr` 命中 0，
且 `x3f_utils_patched.cpp:375-397` 对 `<4.0` 各分支齐备；本仓 LibRaw 0.22.0 与 rawpy 的 0.22.1 同一条码流，只差这个宏。
lane 还在自己的目录里用 MSVC 14.51 加 `/DUSE_X3FTOOLS` 编了同两份文件：**exit=0**，产出 167,476 B + 232,567 B（对照空 TU 的 535 B）。

**修法与代价**：`CMakeLists.txt:113` 那组定义里补 `USE_X3FTOOLS`（`PUBLIC` 会传给主程序），代价是重新 configure + 全链，
库体积按实测 obj 约 **+400 KB**。
⚠ **开宏只解决"能 open"**：Foveon 会置 `is_foveon=1`、`raw_pitch = raw_width*6`（`x3f_parse_process.cpp:213-214`），
而 dngtool 只有"CFA（filters!=0）"与"线性 color3（filters==0）"两条写件路（`dngtool.cpp:572/3282/3332`）——
`unpack → 写 DNG` 这段本轮未验证 ⇒ 验收**不许只测 `-info`**，必须测出件、`dng_validate`、以及平面逐位对撞。
另：`CamF`/`BadPixels` 这一格**不随宏解决**——`src/x3f/*.cpp` 里只读 `ISO/CAMMANUF/CAMMODEL/LENSMODEL` 一类标量属性
（`x3f_parse_process.cpp:152-203`），全仓 `BadPixels` 唯一命中仍是 `rawspeed_glue.cpp:247` 的 `interpolateBadPixels=FALSE`；
可移植的参考实现在本仓 `tools/src/dcraw/dcraw.c:3251 foveon_camf_matrix` + `:3532` 的消费点。

**执行纪律**：本条**尚未实施**——lane-c-policy 正在用 `publish/PLAN/artifacts/dngtool.exe` 做对照实验，
中途换二进制会让它的读数混成两把尺；等它落地后再开宏、重链、按 §30/㉓ 的方式补 E 类判据（新臂形状：
四件 X3F 都能出件 + `dng_validate` rc=0 + 源/产物平面逐位对撞 + 未开宏的旧件必须判红）。

## §36 2026-10-21 凌晨：lane-f-docs 回来——用官方 DNG 1.7.1.0 规范把 F 类的"未判定"换成判定（主循环已抽验）

一手件在 `tests/output/lane-f-docs/`（`DNG_Spec_1_7_1_0.pdf` + `dng_spec.txt` + Apple 文档 JSON/WWDC 转写）。
⚠ **Apple ProRAW 白皮书 PDF 取不到**（apple.com 三处路径均 404）⇒ Apple 侧只有 WWDC21-10160 转写、support.apple.com/119916、
AVFoundation 文档三处；**标签级的 ProRAW 落点与私有 XMP 前缀如实记"取不到"**，不拿二手转述顶替。

主循环抽验（不转抄 lane 的话）：

| 抽验项 | 结果 |
|---|---|
| 深度/语义遮罩/gain-map 的标签号真在 SDK 里 | ✅ `dng_tag_codes.h:347-351`（`tcDepthFormat 51177 … tcDepthMeasureType 51181`）、`:353-355`（`tcProfileGainTableMap 52525 / tcSemanticName 52526 / tcSemanticInstanceID 52528`）、`:363`（`tcMaskSubArea 52536`）、`:367/371`（`tcRGBTables 52543 / …Map2 52544`） |
| dngtool 碰过这些写侧 API 没有 | ✅ **0 次**：`SetDepthMap / AppendSemanticMask / SetTransparencyMask / SetMaskedArea` 在 dngtool.cpp 里计数为 0 ⇒ 次级 IFD 从不产出也从不继承 |
| 预览搬运是否真只在非 DNG 分支 | ✅ `dngtool.cpp:3904` 起 `if (!isDngInput && carryRawMetadata)`，预览/缩略图读取与 `AppendJpegPreview` 都在块内（`:4040-4048`）⇒ **DNG→DNG 丢预览**成立 |

> ⚠ 2026-10-21 白天更正（详见 §40.3）：上表"写侧 API 调用 0 次"是**当时**的读数；现在已真调用
> `ReadTransparencyMask / ReadDepthMap / ReadSemanticMasks` + `SetMaskedAreas`（遮罩/深度逐像素相同）。
> 仍未搬的只剩预览/替代族与 XMP(700)（后者受 `qDNGUseXMP=0` 限制）。
| `MakerNoteSafety` 取值 | ✅ SDK 建模成 bool：`dng_negative.h:369/2304 SetMakerNoteSafety (bool)`、`dng_negative.cpp:3974 (fMakerNoteSafety == 1)` ⇒ 规范只有 **0/1**，§21 待办里"按哪个值声明"要按此收窄，不许实现成三值 |
| "Advanced Mosaic" 术语 | ✅ 官方 PDF 文本 `grep -ci "advanced mosaic"` = **0** ⇒ 该词不存在，不能当"有规范位"的依据（本仓文档从未用过它，无需订正） |
| `ProfileEmbedIfd(50933)` 的叫法 | 本仓文档没有这个错误叫法；正确是 `ExtraCameraProfiles=50933`、`ProfileEmbedPolicy=50941` |

### 判定：F 类里哪些"其实本机就能判"
- **能做出可执行判据**：透明遮罩（NSFT 4/5）、深度图（NSFT 8/9 + 51177-51181）、语义遮罩（NSFT 65540 + PI 52527 + 52526/52528/52536）、
  `MaskedAreas(50830)`、`ProfileGainTableMap(52525/52544)`、`RGBTables(52543)`、XMP 包（tag 700）、增强图（NSFT 16 + 51182）、**预览（NSFT 1/10001）**。
  夹具形状：用 SDK 写侧 API（`AppendSemanticMask dng_negative.h:3064`、`SetTransparencyMask :2890`、`SetDepthMap :7596`、`SetMaskedArea :2503`）
  造 `base.dng` = 主图 + 透明遮罩 + 深度 + 两个语义遮罩 + MaskedAreas 一矩形 + XMP(700) + GainTableMap；
  dngtool 重编码后判据 = 输入↔输出比对三元组集合 **{NewSubFileType 集合 × 标签号集合 × 字节长度}**，缺任一项即判"静默丢位"。
  **先让判据红、不改本体**（同 §30/㉒f：先量出洞，再谈修）。
- **必须真样本才能判**：`DNGPrivateData(50740)`（内容是厂商私有，能否自包含搬运无从判）、XMP 私有命名空间那一半、
  extra camera profiles 的读者行为、Apple ProRAW 的实际标签组合。
- **不是缺口 / 术语纠正**：`50973` 是 **50828 内容的 MD5**（不是原文件哈希，SDK 自动补，writer:8679）；
  `ColorPlanes` 确实不是标签号，平面数唯一由 `CFAPlaneColor(50710)` 的 count 声明（spec p.26/p.33 里 ColorPlanes 只是 count 变量名）
  ⇒ 本仓 §22 的实测成立；`BaselineExposure/AnalogBalance/LRL/AsShotNeutral` 只对 DNG 输入保留**与规范边界一致**
  （非 DNG 源本无此概念，编造才是发明）。

## §37 2026-10-21 凌晨：lane-c-policy 四条判词 + 我又量出一条**8-bit 白电平假标签**（已修，闸 ㉓g，默认档 82/0）

lane-c-policy 的四条结论，主循环逐条复量后的处置：

1. **整件原件 50828 默认关 = 对的**（保持 opt-in）。复量支持：SDK 里 50828 只有三处用途（存/算 digest/重发，
   `dng_negative.cpp:2460,2479,3632-3652`），**没喂进解码路**；`dng_validate` 两臂 `NewRawImageDigest` 逐字相同；
   代价实测 CR3 +64.0% / IIQ +83.5%。另复现"翻载荷一个字节 Adobe 仍 rc=0 且照印原 digest" ⇒ ②e/②f 的 digest 锁仍是唯一守手。
2. **厂商私有块**：不拿 `DNGPrivateData` 去凑 CR3 那 105 条（lane 四臂读数：dngtool-only=0、后置到无注记目标=0、
   裸抄 CMT3 到 0x927C=0 条被解析、Adobe `MakN` 只回 1 条并打 11 条 `Bad offset`；**基址约定本轮没查到**，不许升级成"没人支持"）。
   但 **CR2 那一类是真能补的缺口**：对照臂 EOS 40D 裸抄 0x927C ⇒ **148/211 同名同值**，剩 63 条载荷本就在块外。
   `MakerNoteSafety` 只有 0/1（SDK 建模 bool，§36 已抽验）；exiftool 读取时无视它；Adobe 侧缺省/0 = 再存一次就把注记扔掉 ⇒
   **必须先有逐件自包含判据（块内每个偏移都落在块内、`Bad offset` 计数=0）才准标 1**。
3. **GPS/时间默认全保留 = 对的**，不改（丢弃只在显式请求时发生且 `gps-suppressed=1` 无条件点名）。
   顺手抓到一处过期声明：`dngtool.cpp:4509` 注释仍写"产品隐私默认 StripExifGps=true"，与 §18 已统一的 false 矛盾。
4. **精度档那条不是"按档丢精度"，是假标签**——本册自己该修的：`bps≤8` 时黑电平按 `(x+128)/256` 缩放，
   白电平却写死 255。主循环复量（同一件 Sony IDC）：

   | 臂 | 修前声明 | 修后声明 | 8-bit 平面实测 |
   |---|---|---|---|
   | `-4` 出口 | BlackLevel=**2** / WhiteLevel=**255** | BlackLevel=**2** / WhiteLevel=**60** | max=**63**（=16383>>8 ⇒ 平面是 >>8 搬下来的，不是归一化到 255） |
   | 16-bit 回归 | 512/15360 | 512/15360（不变） | — |
   | IIQ `-4` | — | 4/**255**（65532/256 进位到 256，钳在 8-bit 上限） | — |

   修法：删掉 8-bit 那条 `white = 255` 短路，让白电平与黑电平走**同一条** `(x+128)/256`，再钳 `(1<<bps)-1`；
   点名 ASCII 键 `bps-downgrade=16->%u white=%u`。
   **新臂 ㉓g**（默认档）的期望值不写常数：同一次跑里先编 16-bit 臂取它自己的 `BlackLevel/WhiteLevel`，
   再按 `(x+128)/256` 向下取整推 8-bit 该声明什么 ⇒ 读数 `512/15360 ⇒ 必须 2/60，实得 2/60`。
   门禁：默认档 **82/0**（`tests/output/results/_main/gate77-after-bpsfix.log`），部署 exe sha256 前 16 `669776f580e77f0d`。

### 我这一轮自己的两个错（都要留痕）
- 用 Edit 插 ㉓g 时**误删了重臂的 `$lvAb = ExecRC …` 那一行**（我把 old_string 选成了"要插入点之后的第一行"）。
  当场发现，补回并跑 `_check-ps-parse.ps1`（PS_OK）+ 整条闸复跑（82/0，㉓f 仍在册）才算修完。
- 我想复现 lane 的"Adobe 渲染均值比"，**自己写的内联 TIFF 读手是错的**：把 IFD 项的 `(type,count,value)` 元组
  直接当 `(W,..)` 解，读出"尺寸 4x1 / bps=3"的鬼数。⇒ 那条比值我**没有复现**，本节不写它当读数，
  ㉓g 只用声明级判据；渲染级判据留作 DNGLEVEL_HEAVY 的扩展臂（形状见下）。

### 待办（本轮明确不做，理由在上）
`-jxlq` 的 ASCII 键（现只有 SDK 那行 `JXL encode … distance`，产物有 `JXLDistance` 标签）；
`dngtool.cpp:4509` 过期注释；`-4/-6` 的 help 文案（写"bit depth (16)"但实测 `-4` ⇒ BitsPerSample=8）；
CR2 注记真能补的那 148 条；逐件自包含判据 + `MakerNoteSafety=1`；㉑b 常数改现场测量（§34）。

## §38 2026-10-21 凌晨：五仓全仓扫描收官（补上 §33 欠的 RawTherapee 与 metadata-extractor）

`tests/output/upstream-ref/survey.log`（脚本 `survey_upstream.py`，逐文件镜像留在同目录，可复跑）。命中数按三族 ID/名字统计：

| 仓 @提交 | 候选文件 | 三族命中 | 判读 |
|---|---|---|---|
| dnglab/dnglab @main `99ef87759ad9` | 20 | 6（全是 `CorrParams` 名字级） | 只有镜头**身份**与开关描述，无校正表消费点 |
| **RawTherapee/RawTherapee @dev `b2ee504c47e5`** | 86 | 31 | **第三家一手读者**：`rtengine/lensmetadata.cc:212-214` 读 `Exif.SubImage1.{Distortion,ChromaticAberration,Vignetting}CorrParams`（与 darktable 同一族=**新制式**），`:521/:529` 读 **OlympusIp `0x150a`/`0x150c`**（E-M1 世代，同 darktable） |
| Exiv2/exiv2 @main `081bb55f06c9` | 54 | 13 | §36 已抽验：新制式定义 + 索尼注记 `Decipher` 码表 + E-10 的 `0x0206` 名字 |
| drewnoakes/metadata-extractor @main `0aaf9d6f9d55` | 90 | 17 | 逐条看**全是开关类描述符**（`PanasonicRawDistortionDescriptor`、`OlympusCameraSettingsMakernote*` 的 `TagDistortion/Correction`），**没有一处读校正表数值** ⇒ 补上 §33 那格"因配额没查到"的空白：**查过了，确实没有** |
| LibRaw/LibRaw @master `2caab57e269c` | 47 | 0 | 与本仓链接树同结论（另见 §35：`USE_X3FTOOLS` 未定义是另一回事） |

**结论加固（不改判词，只把"没人解析"从推测升成实测）**：
新制式索尼三表有**两家**一手消费者（darktable `exif.cc:1385-1408` + RawTherapee `lensmetadata.cc:212-214`），语义与校验规则可交叉对拍；
Olympus 的 `OlympusIp 0x150a/0x150c`（E-M1 世代）同样两家都有 ⇒ 若拿到那种机型的件，换算依据已在手；
而**旧制式 Tag94xx（`0x064a/0x066a/0x06ca`）、Samsung `0xa050/51/52`、E-10 的 `0x0206` 表值，五仓 0 消费者** ⇒ §31/§33 的"不许猜"三条界线继续成立。
## §39 2026-10-21 凌晨：撤回我自己在 §37 里写的「后置侧 217/222」——那是参数顺序造的假读数

我自己先前那份"CR3 后置能带 217 个 Canon 键"的复量**作废**。干净的三臂复量（同一件 fresh 产物，逐臂 `cp` 起点相同）：

| 命令形状 | 产物里的 Canon 键数 | MakerNote 字节 | Adobe |
|---|---|---|---|
| 未后置（`dngtool -e -lossless` 直出） | **0**（源 = 224） | 0 | Validation complete |
| **产品实际用的形状** `-overwrite_original -m -TagsFromFile 源 -all:all -ICC_Profile …`（`ExifToolService.cs:510-516`） | **0** | 0 | Validation complete |
| 同一形状去掉 `-m` | **0** | 0 | Validation complete |
| `-MakerNotes:all`（`CopyMetadataSafeAsync`，:694-695） | **0** | 0 | Validation complete |

⇒ lane-c-policy 的"后置到无注记目标 = 0"是**对的**；我的 217 来自第一次那条 `-all:all` 写在 `-tagsFromFile` **之前**的非规范顺序，
那次 exiftool 还打了 `Adjusted MakerNotes base by 8000` —— 它写出来的东西不是产品通路会写出来的东西，**不能当产品读数**。
教训与既有那条同源：**复量必须逐字用被测代码构造的那条命令**（这次我按 `ExifToolService.cs` 读了参数顺序才崩出自己的假数）。

判词据此校正：**CR3 厂商注记仍是整组缺口**（工具侧 0、后置侧也 0），"能不能正确嵌入"在 CR3 上**未证明**；
lane-c-policy 的 CR2 对照臂（裸抄 0x927C ⇒ 148/211 同名同值）才是"真能补"的那格。
㉑b 的常数改现场测量时，**期望值取"产品命令形状"下的到达数**（本档实测 = 0），别再引入第二种命令形状。










## §40 2026-10-21 白天：F 类从"拒绝"改成"真搬运"；崩溃根因坐实并修掉；㉖ 判据换成像素级（默认档 89/0）

lane-crash 的 agent 因每日额度中断，但它把根因与修法写进了 `tests/output/lane-crash/REPORT.md`（199 行）；
主循环接手后**逐条复量**、补完搬运、重建部署并跑闸。

### 40.1 崩溃：`0xC0000409` = 未捕获 C++ 异常 ⇒ terminate ⇒ `__fastfail`，就一处
- 最小触发集**与输入内容无关**：`dngtool -e -i <任意件> -O <父目录不存在>/o.dng`（`-O` 指到已存在目录名同样）。
  修前实测 `rc=3221226505`、无产物；无次级 IFD 的 `p40.dng` 一样崩 ⇒ 上一轮把 ㉖b 的 rc=2 归因成"F 类件跑不动"是**错的归因**。
- 到行：`dng_file_stream(outputPath, true)` **在 try 之外**（本构建 `qDNGValidate=1` ⇒ `dng_file_stream.cpp:39-41`
  `ReportError + ThrowSilentError` ⇒ `throw dng_exception`；`dng_exception` 不派生自 `std::exception`，`main` 无 catch）。
- 修法：把流构造收进自己的 try，两个 catch 都点名 ASCII 键 **`refuse-output-unopenable`** + `return 6`（沿用文件里"写失败=6"）。
- 复量（我本机，两个 exe 同一命令形状）：

  | 臂 | 修前 exe | 修后 exe |
  |---|---|---|
  | `-O <不存在目录>/o.dng` | **3221226505**，无产物 | **6** + `refuse-output-unopenable`，无产物 |
  | `-e -i <不存在输入>` | 2（LibRaw 自己报） | 2（不变） |
  | `-d … -O <不存在目录>` | 5（LibRaw writer 报） | 5（不变） |

### 40.2 我上一轮写的 `-embed-raw` 豁免是**假判词**（撤回）
旧代码对"输入是 DNG 且有遮罩族次级 IFD"在 `-embed-raw` 时打 `preserved-in-50828` 并继续出件。
实测：`-embed-raw` 对 **DNG 输入**只搬"源里已有的 50828"（`isDngInput && embedOriginal` 分支），**不会把本件嵌进去** ⇒
同一夹具加 `-embed-raw` 产物仍 2958 B、无 50828、次级 IFD 全丢，而日志却说"整件嵌进 50828 原样带走"。
⇒ 该分支已删；拒绝判词也改成"保留原文件是唯一不丢这些位的办法（-embed-raw 对 DNG 输入只搬源里已有的 50828）"。

### 40.3 F 类真搬运：SDK 的读取器 + 写侧本来就是一套
- 依据（一手）：`dng_negative.h:2890/2936/3064` 有 `SetTransparencyMask / SetDepthMap / AppendSemanticMask`，
  **读取器** `dng_negative.cpp:7479 ReadTransparencyMask / :7607 ReadDepthMap / :7817 ReadSemanticMasks`；
  写侧 `dng_image_writer.cpp:8919/8950/9232` 会写这三类 IFD ⇒ 不必自己拼 IFD。
- 分档（逐值，`dng_info.cpp:2789/2819/2879` 都是 `==` 整值比）：
  **可搬 4/8/0x10004** ⇒ 读进 negative；**拒绝 5/9/0x10005 组合档 + 读不到 0xFE + 表外陌生值** ⇒ `refuse-secondary-ifd-loss`（exit 9）；
  **预览/替代族 0/1/16/17/32/33/0x10001** ⇒ 只点名 `carry-skipped=secondary-ifd-other`（在册欠账）。
- `MaskedAreas(50830)` 也真搬：`dng_rect` 顺序 **T,L,B,R**（`dng_ifd.cpp:2079-2084`），坐标是**存储整幅绝对坐标**
  ⇒ 只有源 `ImageWidth/Length` == 产物 `rawWidth/rawHeight` 时才写，否则拒绝（不把坐标指到别的像素）。
- 深度语义位（51177-51181）在 `dng_shared` 的 protected 字段里 ⇒ 用源 `dng_negative::Parse` 后的公开 getter 搬到新 negative。
- **XMP(700) 仍搬不了**：本构建 `qDNGUseXMP=0`（`CMakeLists.txt:59`）⇒ `SetXMP` 被编译掉；产品侧由 exiftool 后置
  （⑰ 组就是这么验的）。dngtool 单跑必须点名 `carry-skipped=dng-main-content-bit`，README/判据都不许写成"已搬"。

### 40.4 判据尺换形：标签数/byteCount 三元组会把 SDK 的结构重排当成丢位
旧判据对次级 IFD 逐标签比 `{标签号,byteCount}`。搬成功后实测它报红 12 条，**全是假红**：
SDK 把条带 `0x111/0x116/0x117(273/277/278/279)` 改写成**分块**(324/325)，深度语义 51177-51181 从次级 IFD **搬到主 IFD**
（`dng_image_writer.cpp` 的 `mainIFD.Add(&tagDepthFormat…)`）。⇒ 改成量**读者能不能解出同一份内容**：
- 遮罩/深度：`dng_validate -transparency / -depthMap` 两边各导一份，**逐像素比**（tifffile）：实测
  透明遮罩 64×64 uint8 `max|Δ|=0`、深度 64×64 uint16 `max|Δ|=0`；
- 语义遮罩：比 52526/52528/52536 的值（没有公开导出面 ⇒ 像素级记未判）；
- `MaskedAreas`：比 4 个坐标值的原始字节；
- 产物必须 `dng_validate -2` rc=0；
- 未搬的两档必须**被点名**，否则退 1（静默丢位）。旧 exe 同一判据退 **1**（三张次级 IFD + MaskedAreas 全缺席、两档都没点名）。

### 40.5 读数
- 默认档：**PASS=89 FAIL=0**（`tests/output/results/_main/gate77-carry-default.log`），部署 exe sha256 前 16 `cc481061c84ac6c3`。
  ㉖b 现为真判据（PASS 行里带像素对撞与两条"在册欠账"）；㉖c/㉖d/㉖e/㉖f/㉖g 见上表与负控。
- 重臂 `DNGBADPIX_HEAVY=1 DNGLEVEL_HEAVY=1` **本轮未复跑**（旧数 84/0 是 ㉖ 进册前）⇒ 记未判，不记绿。

### 40.6 仍欠（在册，不写成"已做"）
1. ~~预览/替代族次级 IFD（DNG→DNG）仍不搬，只点名~~ **已收口**（§40.9）：JPEG 原字节搬成产物预览；
2. ~~XMP(700) dngtool 单跑仍不搬（构建限制）；产品侧 exiftool 是否覆盖 DNG→DNG 的 XMP 本轮未量~~
   **已收口**（§40.9）：dngtool 单跑仍写不了（`qDNGUseXMP=0`），产品侧白名单已补 `-XMP:all` 并有端到端两臂；
3. ~~Foveon X3F 产物平面数 4 vs 源 3~~ **已判**（§40.7）：那是 rawpy 的读手形状（另一份 LibRaw），
   产物声明 `SamplesPerPixel=3`、Adobe 与 exiftool 都读 3，存储三平面与渲染都逐位相同；
4. ~~E 类 X3F 判据四臂~~ **已落**：㉗a/㉗b（四件能开 + DP1 电平/存储/渲染 + 修前负控），并顺手修掉
   非 Bayer 的 `WhiteLevel=65535` 假声明（§40.7）。

### 40.7 E 类补刀：`USE_X3FTOOLS` 打开后暴露的第二条假声明（非 Bayer ⇒ WhiteLevel 写死 65535）
- 现象（一手）：Sigma DP1 产物声明 `WhiteLevel=65535`，但存储值 ≤2100（源 `raw_bps=12`、`colordata.maximum=2100`）。
  两个独立读者同向：rawpy `postprocess` 均值 **0.62**（源 31.5），Adobe `dng_validate -2` 渲染 16-bit 均值 **37/58/66**（修后 1159/1800/2057）。
- 成因到行：`else if (!isBayer || raw_bps >= 16) white = 0xFFFF;` —— "线性 DNG 像素已是 16-bit 全范围"这个前提对
  Foveon 不成立（数据仍是 12-bit 域）。与 §37 的 8-bit 假标签、§30 的黑电平假声明同一族：**声明不等于数据域**。
- 修法：非 Bayer 时若 `colordata.maximum ∈ (0, 65535)` 就写源声明值，否则才回 65535；点名 `WhiteLevel: 线性源按源声明 %u`。
  像素一个不动（存储三平面与源逐位相同，`max|Δ|=0`）。
- 读数：修后 rawpy `white_level=2100`、渲染 `max|Δ|=0`（源均值 31.61，产物 31.61）；**修前 exe 负控**复现
  `white_level=65535`、渲染均值比 **0.019** ⇒ 判据有牙。
- 闸：㉗a（四件都能开 + DP1 电平/存储/渲染一致）、㉗b（负控复现假声明）⇒ 默认档 **91/0**
  （`tests/output/results/_main/gate77-x3f-default.log`），部署 exe sha256 前 16 `dd3b9f6fca42686c`。
- ⚠ 读手侧形状要分清：rawpy 把产物读成 `(1792,2688,4)`、`color_desc='RGBG'`、第 4 平面全零——
  那是**它的**多平面读法（另一份 LibRaw 构建），**不是产物声明**：exiftool 与 Adobe `dng_validate` 都读
  `SamplesPerPixel=3`，且渲染与源逐位相同。别把"读手形状"写成"产物多了一个平面"。

### 40.8 自查补的空档：`MaskedAreas(50830)` **单独存在**时不会被搬（判据夹具的形状把它盖住了）
- 发现方式：复审自己写的搬运入口，而不是判据抓到的——`if (isDngInput && !sub.carryable.empty ())` 才进
  `CarrySecondaryIfds`，而 `MaskedAreas` 写在同一个函数里 ⇒ **只有 50830、没有任何遮罩族次级 IFD** 的 DNG
  整段不调用 ⇒ 静默丢位。㉖ 的夹具两者都有，所以 ㉖b 量不到这个组合。
  教训与 [[feedback-adversarial-fixture-order]] 同向：判据夹具要覆盖"只有其中一项"的稀疏组合，不能只造全副。
- 修（两处）：入口加 `|| hasMaskedAreasTag`（`hasMaskedAreasTag` 由 `sub.mainBits` 里的 0x0C68E 决定）；
  `fopen` 失败从 `if (fp) { … }` 的静默跳过改成 `refuse-secondary-ifd-loss`（`MaskedAreas 读取时打不开源文件`）。
- 验牙（变异，实测两臂）：临时撤掉 `|| hasMaskedAreasTag` 重建 ⇒ ㉖b 退 **1** 并点名
  `50830 单独存在时产物里缺席（静默丢位）`；恢复重建 ⇒ rc=0、坐标 16 B 逐字节相同。
- 在册臂：`_probe-dng-secondary-ifd-carry.py` 新增 1e——用夹具模块的 `build(path, drop=(…))` 造
  **NSFT=[0] + 只有 50830** 的变体（先自检 `NSFT==[0]` 且 50830 在，再跑 `dngtool -e -lossless`，比 50830 原始字节）。
  夹具生成器同时加了 `--help` 防误写闸（我把 `--help` 当输出路径时在仓库根写出过一个名为 `--help` 的 DNG 和一张预览 JPEG，已删）。
- 探针全文落盘：`tests/output/results/dng-meta/sec-carry/probe.log`（㉖b 的 PASS 行只摘最后 4 行，
  1e 那条在中间 ⇒ 证据不能只活在控制台）。
- **最终读数**：默认档 **91/0**（`tests/output/results/_main/gate77-final-20261021c.log`），
  部署 exe sha256 前 16 **`fb5c495784cafb72`**。重臂（DNGBADPIX_HEAVY + DNGLEVEL_HEAVY）本轮仍未复跑 ⇒ 记未判。

### 40.9 在册欠账收口：预览族真搬成预览、产品侧 XMP 白名单补上（默认档 93/0）

**1) 预览/替代族（NSFT 0/1/16/17/32/33/0x10001）从"只点名"改成"真搬 JPEG 原字节"**
- 入口 `dngtool.cpp:4916-4918`（`isDngInput && !sub.other.empty()`）：逐个子 IFD 走 `ReadSubIfdJpeg`
  （`:2308`；Compression 6/7、`JPEGTables(0x0200)` 拼接、`0x0201/0x0202` 或条带 0x0111/0x0117、≤64 条、SOI 校验），
  读出的原始字节交给 `AppendJpegPreview`（**不重编码**），第一个 NSFT∈{0,1} 的当 primary。
- 读不出来的仍点名 `WARN carry-skipped=secondary-ifd-other`（未压缩/Deflate 预览不猜）⇒ 出声层不变。
- 判据：㉖b 的 PASS 行多了 `SEC-CARRY 读数 preview NSFT=1：JPEG 原字节逐字节相同（697 B）` ——
  从产物里按 0x0201/0x0202 或条带取回 JPEG 字节与夹具原字节逐字节比，不是"尺寸对得上"。
- 负控：修前 exe 过同一条判据仍退 1（㉖g）⇒ 这条绿是搬出来的，不是尺子松出来的。

**2) DNG 出口的 XMP：产品默认串原来整条缺失**
- 位置 `ExifToolService.BuildDngStdTagsCarryArgs`（`src/FfmpegGui/Services/ExifToolService.cs:1099`）补
  `-XMP:all`（仅 `!StripXmp`），与另一条构建器（`:833`）同形。
- 验法不靠"我看了代码"：`ServiceProbe dng-xmp-args`（`tests/ServiceProbe/Program.cs:8806-8807`）直接调产品构建器
  校验两臂参数串；再用产品参数跑 exiftool 端到端：源 `xmp:CreatorTool/Rating/dc:subject` 到达产物且
  `dngtool -verify` rc=0；`--strip-xmp` 臂三项读回全空 ⇒ 两臂真分叉（㉘a/㉘b）。
- 变异验牙：临时删掉 `-XMP:all` 行重建 ⇒ ㉘a 退 1；恢复重建 ⇒ rc=0（同名行出现两次，变异要带上下文定位）。
- 已知边界：`-XMP:all` 只复制**已知命名空间**，夹具自造的 `syn:` 命名空间仍不搬（exiftool 的行为，不是白名单漏项）。
- dngtool 单跑仍写不了 XMP（`qDNGUseXMP=0`）⇒ ㉖b 的"出声层"继续点名，与产品侧后置不冲突。

**3) 读数**
- 默认档 **PASS=93 FAIL=0**（`tests/output/results/_main/gate77-xmp.log`），部署 exe sha256 前 16 **`410e8f273d7bee39`**
  （`publish/PLAN/artifacts/dngtool.exe` 与 `tools/src/dngtool/build/Release/dngtool.exe` 同值，已核）。
- 新增臂：㉘a（端到端 XMP）、㉘b（两臂分叉）；㉖b 的判据扩到预览字节。
- 重臂 `DNGBADPIX_HEAVY=1 DNGLEVEL_HEAVY=1` **已复跑**：**PASS=96 FAIL=0**（多出的 3 条 = ㉒d/㉒e/㉓f），
  日志 `tests/output/results/_main/gate77-heavy-20261021.log`，墙钟 148.5 s；同轮探针 `probe:runner` 仍是
  本机既有沙箱红（`target is not ffmpeg (cmd.exe)`，非本轮引入）。

### 40.10 整轮暴露的五条登记类红（都是"新东西没进册"，不是功能坏）

第一轮 78 条整轮（`tests/output/results/_main/full-20261021.log`，产物指纹 `FfmpegGui.dll=67E32DD244A74164`）
失败 6 项 ⇒ 逐条归因、逐条修，**没有一条靠放宽判据**：

| 红 | 一手读数 | 归因 | 处置 |
|---|---|---|---|
| `verify-ps-compat.ps1` | `FAIL 正文无 PS7+ 专属语法（命中 34 处）` | ⑳–㉘ 各臂在本族会话里用了 `?? ''`（HEAD 该文件 `??` 计数 = **0**，工作树 = **34 行 / 61 处** ⇒ 全是我加的） | 逐处换成正文里就成立的 `[string](…)`（null ⇒ 空串，与原语义同形；一处行含两处 ⇒ 61 处全替；替器 `tests/output/tmp-gate/fix-coalesce.py`，从右往左配对括号，在 gitignored 目录里 ⇒ 一次性）⇒ **13/0** |
| `_probe-matrix-structure.ps1` | `A21 … :196 (found at 197)`、`A30 … :272 (found at 273)` | `CliParser.cs` 被并发改动 ⇒ 行号 pin 漂 1（这条锁的设计就是"行号失效当场响"） | `_lib-matrix.ps1` 两条 Evidence 抬号 196→197、272→273 ⇒ **8/0** |
| `_probe-test-group-coverage.ps1` ⑨-b | `门外 mode 未登记 1 个：dng-xmp-args` | 新 mode 的调用点在 **py 取数器内部的 subprocess** ⇒ #69 的 AST 分类器看不见 ⇒ 被算成"门外" | 在册门禁**自己直调** `ServiceProbe dng-xmp-args`（新臂 ㉘c），再按「有调用点就必须进账」并进 `TestGroups.cs` 的 `ServiceProbe` 组（与 `color`/`framelen` 同规，**不进豁免表**）⇒ **37/0** |
| `verify-probe-mirror-parity.ps1` ③-a | `每个派发 mode 都在登记表里（缺：dng-xmp-args）` | 同上：新 mode 没钉条数 | 登记表 2 加 `'dng-xmp-args' = 2`（= 源文本里 2 个 `Check` 调用位，与各 mode 同口径），和数注释 832→**834** ⇒ **84/0**（原 82/1） |
| `probe:bench` | 轮内 `8b PQ→sRGB 实 21.0`、`8b sRGB→P3 实 21.2`（阈值 25） | **同一份二进制单跑 = 56.3 / 46.7 ⇒ 37/0** ⇒ 轮内塌到 21 是负载，不是内核回归 | 登记为轮内计时伪影；不抬阈值、不改判据（[[gate-loop-discipline]]：计时红要 A/B）。负载来源本轮**没有一手证据** ⇒ 只登记不指认 |
| `probe:runner` | `target is not ffmpeg (cmd.exe)` | 本机既有沙箱红（10-07/10-10/10-21 多轮同一条） | 不动 |

- 新默认档读数：**㉘ 组由两臂变三臂** ⇒ `verify-dng-metadata-transport.ps1` 默认档 **94/0**
  （`tests/output/results/_main/gate77-after-regs.log`）。㉘c 的牙齿与 ㉘a/㉘b 同源（同一条
  `BuildDngStdTagsCarryArgs`，§40.9 已做过"删 `-XMP:all` ⇒ 判据退 1"的变异）⇒ 本轮未对 ㉘c 单独重做变异。
- ⚠ 三条"登记类"红的共同形状：**加一样东西要同时落四处**（代码 / 组表 / 条数表 / 在册脚本调用点）。
  少任何一处，红的是账，不是功能——而账红会让整轮不可绑基线。

### 40.11 第二轮整轮（修完四条登记红 ⇒ 只剩本机既有那一条沙箱红）

- 日志 `tests/output/results/_main/full-20261021b.log`（收尾 2026-10-22 02:40），
  被测产物指纹逐字与第一轮同一档：`FfmpegGui.dll=67E32DD244A74164`、`FfmpegGui.exe=26AD22B197735683`、
  `ServiceProbe.exe=B9B5D51906AC1A7A`、`UiTestHost.exe=1CA4D5572DA12888`、`GainMapTestHost.exe=593D8381CDCCC42E`；
  运行器自身 `runner-self-sha256=2D7CC55CD67FB6C4 lines=1590`（两轮同尺）。
- **脚本面 78 条全部 exit=0**（`script-list=78 条（受管基线 78）`），带 `PASS=n FAIL=m` 汇总的条目合计 **2035/0**；
  `verify-dng-metadata-transport.ps1` 在轮内 = **94/0**（120.8 s）。
- **探针面 27 行合计 776/1**，那 1 就是 `probe:runner` 的"静默但正常退出的子进程不被误杀"
  （`target is not ffmpeg (cmd.exe)`）——本机既有项，10-07/10-10/10-21 四轮同一条，非本轮引入。
- `probe:bench` 本轮 **exit=0（37/0，52.3 s）**，同一条 8b 两臂从 21.0/21.2 回到 56.3/46.7 ⇒
  与"单跑 A/B"同向，**第一轮那两条计时红确认为轮内负载伪影**；阈值一字未动。
- ⚠ 基线绑定的口径：这一轮**没有**重跑 pack/L3，也没有改 `src`（第二轮相对第一轮只动了
  `tests/scripts/*`、`tests/Tests.Shared/TestGroups.cs` 与文档）⇒ 交付面与第一轮同一份二进制。

## §41 2026-10-22 晨：Sony 旧制式（Lunar 那一族）不是"解不开"，是**读错了存放位**——第二路由已落地

### 41.1 先撤回 §31 的两条判词（都是一手读数能推翻的）
1. ~~"差插装（解密不可得）"~~ **撤回**。两把密码都在本机可得，且都自写成第二实现验过：
   - 注记块替换密码：`Sony.pm:11517` 的注释直接写明 `c = b³ mod 249`（249-255 不翻译）。
     我自己按公式造表实量：**256 项 256 个像 ⇒ 双射，碰撞类 0 个**；exiftool 注释点名的
     「0-1、82-84、165-167、248」不是互撞而是**不动点**（`enc[b]==b`，公式产生 9 个 + 不翻译区自等 7 个）。
     机理：249=3·83，立方在 Z/3 与 Z/83（gcd(3,82)=1）上都双射，CRT ⇒ 0..248 双射。
     独立性对答案没抄它的 `tr///`：拿 `Sony.pm:1826-1850` 自记的 0x9400「密文首字节→明文首字节」14 组比，
     **13/14 对上**，唯一不符的 `0xd0` 正是该文件 `:1830` 自己标 `decoding appears not valid` 的那条 ⇒ 真反证 0。
   - SR2SubIFD 的 key-stream：`Sony.pm:11488 sub Decrypt`（大端 32 位字异或，`words=int(len/4)` ⇒ 尾字节不参与），
     本仓链接的 **LibRaw 在树内也有同一把**（`tools/src/libraw/src/metadata/sony.cpp:98 sony_decrypt`、
     `:2168 parseSonySR2`、`tiff.cpp:750/756` 认 0x7200/0x7221）⇒ 不是"没有参考实现"。
2. ~~"旧制式用 darktable 那条规则一量就不成立（`dist[0]=4`、`vig[0]=0`、`ca[0]=-1176`）⇒ 是另一套 framing"~~
   **判词方向对、归因错**。这条规则的"不合"不是因为旧制式换了数学，而是因为**量在了没有计数前缀的那一份**上：
   同一件 Lunar 里三张表各有多份存放，实测（`tests/output/sony-enciph/readout.txt`）：
   | 存放口径 | 数组长度 | `nc=dist[0]` | `2nc==ca[0]` | `nc==vig[0]` | 判 |
   |---|---|---|---|---|---|
   | SR2SubIFD `0x797D/7980/7982`（解密后，**带前缀**） | 17/33/17 | 11 | 22==22 | 11==11 | **合** |
   | Tag9405a `0x064a/066a/06ca`（解密后，**无前缀**） | 16/32/16 | 4 | 8 vs −1176 | 4 vs 0 | 不合 |
   | Tag9405a **不解密直读**（对照量，不是读数） | 16/32/16 | 64 | 128 vs −1149 | 64 vs 0 | 不合 |
   ⇒ §31 记的 `4 / 0 / −1176` **正是解密后 Tag9405a 那一份**（不是密文，我原先怀疑"量在没解密的字节上"也不成立）。

### 41.2 决定"能不能沿用现成换算"的那条轴：**同一件里两种存放同时在场**
§31 写的"唯一可能的推断路径是拿一件同时带新旧两制式表的身体对拍，本机没有"——**这句也不成立**：
本机 `tests/output/raw_samples/Sony_ILCE7M2_IDC.ARW` 就是那件身体（图像 SubIFD 有新制式三条，SR2SubIFD 也有旧制式三条）。
我自己手读（不走 lane 的脚本）量到：
- `0x7032 vs 0x797D`、`0x7035 vs 0x7980`、`0x7037 vs 0x7982` ⇒ **整段逐字节相等**（17/33/17 项）；
- 开关 `0x7031/0x7034/0x7036` vs SR2 的 `0x797C/0x797F/0x7981` ⇒ 逐个相等（`257 / 1 / 1`）。
⇒ 旧制式那份就是**同一张表、同一套约定**，只是换了存放位 + 换了开关的标签号；因此不需要新数学，
只需要**多一条 IFD 路由**。`Sony.pm` 只给 0x797D/7980/7982 命名（`:10571-10573`），不给开关名 ⇒
开关身份由上面这条"同一件两存对拍"钉住，不是按相邻猜的。

### 41.3 落地
- `tools/src/dngtool/dngtool.cpp`：新增 `SonySr2KeyStream` / `BlkInt16Seq` / `BlkSwitch` / `IfdU32Scalar` /
  `ReadSonySr2Lens`，在 `ReadLensTables` 的两个失败出口（IFD0 无 0x14A、图像 SubIFD 无三表）改成先试 SR2 路由。
  路由：IFD0 `0xC634`(内联 u32) → SR2Private IFD 的 `0x7200` 偏移 / `0x7201` 长度 / `0x7221` 密钥 →
  读块、key-stream 解密 → 块内 IFD 取 `0x797D/7980/7982`（**值指针按 TIFF 绝对**，另一种"相对块起点"已被
  exiftool 否证：`verdict.SR2SubIFD[blkrel] 3 族不等 ⇒ 被否证`）+ 开关 `0x797C/797F/7981`。
  只认**内联**标量（type 1/7 count≤4、4/13 count==1、3 count==1），外链一律当"取不到"去点名，不猜。
- 两条"不许静默"补在发射侧：源件开关是 `(vig=257, ca=1, dist=0)` ⇒ 畸变点名 `switch-off`；
  CA 表在场但当前实现把它挂在 warp 上发，畸变关了就没法顺手发 ⇒ 新点名
  `lens-skipped=chromatic-aberration:distortion-off-and-no-standalone-warp`（在册欠账，见 41.5）。
- 取证件：`tests/scripts/_probe-sony-enciphered-lens.py`（独立解码 + 与 exiftool 逐值/逐地址对撞；
  退出码 0=对撞通过 1=不等 2=取不到）；读数 `tests/output/sony-enciph/readout.txt`。

### 41.4 读数
- 实跑（新 exe）：`lens=SR2SubIFD 旧制式路由命中（密钥 0x44332211 块 56958 B） 三表可见性 vig=1 ca=1 dist=1 开关 vig=257 ca=1 dist=0`
  → `lens-vignette=applied nc=11 maxres=0.001692 cornergain=1.222364` → `lens-opcodes=1 已写进 OpcodeList3(51022)`；
  **Adobe 自己的读者** `dng_validate -v` 打印 `Parsing OpcodeList3: 1 opcode / Opcode: FixVignetteRadial, minVersion = 1.3.0.0` + `Validation complete`（rc=0）。
- 闸：新增 ㉙a（路由命中 + 暗角进标准位 + Adobe 认出 opcode + 两条不许静默的点名齐）、
  ㉙b（**计数由第二实现钉住**：dngtool 报 `nc=11`，独立解码器读同一件 SR2SubIFD vig 首项 = 11；
  这条臂自带分叉：错口径"相对块起点"量到的是 `nc=1015`，不必重建就有一票推翻它）、
  ㉙c（负控：修前 exe 同一输入必须仍 `lens-tables=absent` 且不出现 SR2 路由）。
  ⇒ 默认档 **97/0**（`tests/output/results/_main/gate77-sr2arms.log`），
  重臂 `DNGBADPIX_HEAVY=1 DNGLEVEL_HEAVY=1 DNGLENS_HEAVY=1` **101/0**（`.../gate77-heavy-20261022.log`，
  其中 ⑱f 像素级"读者真施加"环带提升 1.0946/1.0943/1.0954 对预测 1.1117、抽样不同 19985/20000）。
- 部署：`publish/PLAN/artifacts/dngtool.exe` 与 `tools/src/dngtool/build/Release/dngtool.exe` 同值
  sha256 前 16 **`172b7f59ab7d75a3`**；上一档留成负控 `tests/output/results/_main/dngtool.before-sr2route.exe`（`410e8f273d7bee39`）。
- `verify-ps-compat` 13/0（本轮新增的闸内代码避开了 PS7 专属语法）。
- ⚠ **本批未跑整轮，且必须登记两件互相影响的事**（共享机器归因，见 [[shared-box-readings-need-attribution]]）：
  ① 另一会话在 07:14:49 起了整轮并持有 `tests/output/.gates.lock`（`pid=54148`），我到 07:35 仍在跑 ⇒ 我这批不再起第二轮
     （仓库锁不允许，且两轮并发只会双双不可归因）；
  ② 我那三次 DNG 闸（07:26:50 `gate77-sr2route.log`、07:29:47 `gate77-sr2arms.log`、
     07:32:37 `gate77-heavy-20261022.log`）**与它的整轮时间重叠** ⇒ 它那一轮的**计时型臂**（`probe:bench`、
     `verify-ffmpeg-heartbeat` 的 `elapsed ≥ 3 s` 那类）可能被我的负载污染，读数要按"并发期"打折看；
     反过来我的 ㉙/㉓/㉖ 各臂都不是计时判据（像素逐位、标签逐值、读者解析），不受这条影响。
  ③ 顺带一条读数事实：`FfmpegGui.dll` 已被那次改动重建成 **`66bfd797a501aa69`**（上一轮绑定的是
     `67E32DD244A74164`）⇒ §40.11 那两轮整轮**不再覆盖当前树**，本批的 DNG 侧收口只由上面两个窄读数担保。

### 41.5 这一格现在真正还缺的（别再写成"等插装"）
1. ~~**CA 在"畸变关"时发不出去**~~ **已收口（2026-10-23，§43）**：现发一条几何恒等 × 逐平面的
   `WarpRectilinear2`，Adobe 读者解出、系数由独立表钉住（㉙d/㉙e）。
2. `Tag9405a`/`Tag9405b` 那两份**无计数前缀**的数组约定仍未定（第 0 项不是条数：7M2 的 `0x350 vig` 首项=0、
   `0x064 dist` 首项=3 且与带前缀那份去掉首项后**前 16 项逐等** ⇒ 像是"去前缀 + 平移一位"的形状，但只一件身体，不写成结论）。
3. 同一天区内有**两张内容不同的畸变表**（SR2SubIFD 那份等于 `Tag2010b@0x1a23` 的前 11 项，而 `Tag9405a@0x06ca` 是另一张）
   ⇒ 选哪张由"满足 darktable 规则 + 与图像 SubIFD 逐字节相等"这条轴定，已落在 SR2SubIFD 那份；但这是**一台身体**上的对拍，
   旧制式本机仍只有 Lunar 一件 ⇒ 「这张表随镜头/焦距变不变」没有分叉轴。
4. Samsung EX1 / Olympus E-10 那 4 条：本机零一手语义，维持不做。
5. ⚠ 两条**在册臂的依赖物不在版本控制里**（与 §40 的 ㉖f 同形态，登记而不是假装它稳）：
   ㉙b 的独立解码实现 `tests/scripts/_probe-sony-enciphered-lens.py` 目前是**未跟踪**文件，
   ㉙c 的负控 exe `tests/output/results/_main/dngtool.before-sr2route.exe` 住在 **gitignored** 目录里。
   ⇒ 一次 `git clean -xdf` 之后 ㉙b/㉙c 会各自走 `SKIP ⇒ 记未判`（不是绿，判据本身是 fail-closed 的），
   但这条路由的"负控有牙"就只能靠上一节那台负控 exe 的重新留档恢复。要提交这一批时，
   `.py` 取数器必须一起进索引。

## §42 2026-10-22 晚：本批收尾读数与三条环境事实（不改判据，只把归属写清）

- **回归证明（新制式不被回退抢路）**：同一件 `Sony_ILCE7M2_IDC.ARW`，旧 exe（`410e8f273d7bee39`）与新 exe
  （`172b7f59ab7d75a3`）各跑一次 `-e -lossless -lens-corr`，`lens-*` 四行 **`diff` 为空**
  （`lens-vignette=applied nc=16 sw=257 …` / `lens-warp2=applied planes=3 …` / `lens-warp1=applied …` /
  `lens-opcodes=3`）⇒ 新增的 SR2 回退只在主路取不到时才走，新制式行为逐字未变。
- **两条重臂本轮仍未跑**（⑯e `DNGOPC_HEAVY`、㉑ `DNGPRIV_HEAVY`）：拦它的是**磁盘**，不是判据——
  收尾时实测 `C:` 总 904 GB / 已用 901 GB / 余 **3 GB**（清掉我自己这一族会话在 `tests/output/tmp-gate`
  留下的 368 MB 中间件后余 4 GB）。本仓一枚整幅无损 DNG 就是 68 MB 级（§31 第 6 条踩过），两档重臂 +
  另一会话同时在跑整轮的条件下把盘写满，代价是**别人的轮**一起废 ⇒ 处置是**不跑**，登记未判，不放宽、也不硬试。
  取数器 `tests/output/tmp-gate/wait-lock-then-twoheavy.sh` 带了同一把磁盘闸（`< 8 GB ⇒ DISK-GUARD 不跑`），
  它在这两档真跑之前会自己再量一次余量。
- **并发归属写清**：另一会话 17:55:49 起了整轮（`pid=54992`，锁文件逐字记录），当晚仍在进行；
  那一轮用的是**当前树**（含我 07:24 部署的 `dngtool.exe=172b7f59ab7d75a3` 与 #77 的 ㉙ 三臂）⇒
  如果它绿，那是**它那一轮**对本批的覆盖，不记成我起的轮；我这一批的收口仍只由 §41.4 的两个窄读数担保
  （默认档 97/0、三重臂 101/0）。我**不再起第二轮**：锁被别人持有、C 盘余量不够、且并发两条 #77
  会互相 `Remove-Item $d` 造假红（§31 第 4 条）。
- 本族会话留下的两格欠账已进任务表：**#41**（CA 独立于畸变的发射通路——㉙a 已点名、不许静默）、
  **#33** 收窄后的两条（Samsung EX1 / Olympus E-10 一手语义；`Tag9405a` 无前缀那份的约定）。
  **#34**（86 条色彩配方 + 别家生效曲线）仍卡在"生效值定位法"这一件工具性缺口上，本轮没有新证据。

### 42.1 补三条收尾读数（18:36–18:47）

1. **本批已被那一轮覆盖到，只是不记在我名下**：并发会话 17:55:49 起的那轮 18:36 收尾，
   `script-list=78 条（受管基线 78）`，`verify-dng-metadata-transport.ps1` **轮内 exit=0、PASS=97 FAIL=0（105.7 s）**
   ⇒ ㉙ 三臂在整轮条件下绿；该轮绑的被测产物是 `FfmpegGui.dll=7BC916DB0FA8727A`（不是我这两轮的
   `67E32DD244A74164`——中间 `src` 又被重建过一次），它的运行器仍是 `2D7CC55CD67FB6C4 lines=1590`。
2. **磁盘闸如实生效**：`tests/output/tmp-gate/wait-lock-then-twoheavy.sh` 18:36:42 等到锁清、量到余量 4 GB ⇒
   判 `DISK-GUARD 不跑`，⑯e/㉑ 两档重臂**仍是未判**（没有为了"补齐数字"硬试）。
3. **`probe:bench` 现在是"边界阈值 × 本机计时底噪"的合成红，不是某一条代码回归**（**非本批**；
   先记下我自己在这一格上犯的读数错：**第一次我把没逐次捕获的比值写成"1.4/1.4/1.4/1.4/1.0"**，
   那是把汇总数当成了读数 ⇒ 下面这版只列当场抓到的）：
   | 时刻 | 读数 | 当场红的是哪条（逐字捕获） |
   |---|---|---|
   | 07:31–07:32（空机、TMP 重定向） | **37/0** 两次（8b 两臂 56.3 / 46.7 MPix/s） | 无 |
   | 17:55–18:36 那一轮**轮内** | **34/3** | `sRGB→PQ(关表) 单核 ≥3MPix/s 实 3.0`、同臂**实 2.9**、`8b sRGB→P3 单核 ≥25 实 22.0` |
   | 18:38 单跑 | 36/1 | `…实测增益必须 ≈1×（实 1.4×）` |
   | 18:42:14 / 18:43:28 / 18:44:42 / 18:45:55 | 36/1 ×4 | **红项身份那四次没捕获 ⇒ 不写成"同一条臂"** |
   | 18:47 单跑 | 比值臂 **PASS 实 1.0×**；同次 `sRGB→PQ` 单核 3.5、多核扩展比 6.8×、dop=8/20 都 375 ms ⇒ 23.6 MPix/s | — |
   | 18:49:37 / 18:50:41 / 18:51:53 | 36/1 · **37/0** · 36/1 | 比值臂 ≈1× ／ 无 ／ `sRGB→PQ 单核 ≥3MPix/s`（3.0 边界那条） |
   ⇒ 三条不同的臂在相邻几次运行里**轮流**红，且 07:32 空机曾全绿、18:50 又出过一次全绿 ⇒
   合理解释是这台机器此刻的计时底噪把读数推到 1.3 / 3.0 / 25 这些**紧贴阈值**的边界上；
   **不指认代码回归，也不抬阈值、不改判据**（那是把仪器的问题结算成代码的结论）。
   要定性需要"同机旧二进制 A/B + 交错 A/B/A"（[[gate-loop-discipline]]）——本机 `publish` 里
   **没有第二份 `ServiceProbe.exe`** ⇒ 对照臂取不到，这条**按未判登记**，留给下一位有旧件的人。
4. **默认档在空机上复跑，绿是复现的**：18:5x 无锁、无并发轮时 `verify-dng-metadata-transport.ps1` =
   **PASS=97 FAIL=0**（`tests/output/results/_main/gate77-idle-20261022.log`，rc=0）⇒ §41.4 那条与并发整轮
   重叠的读数不再是唯一见证；跑完 `C:` 余量仍 4 GB（该脚本自清运行级目录）。

## §43 2026-10-23：CA 独立于畸变的发射通路（#41 收口）+ 三个负控 exe 的归位

### 43.1 做了什么
- `tools/src/dngtool/dngtool.cpp` 的 Sony 畸变分支加一格 `caOnly`：源开关 `(swDist=0, swCa=1, ca[0]==2*nc, 3≤nc≤16)`
  时不再"点名跳过"，而是发一条 `WarpRectilinear2(optional)`，**几何恒等（s≡1）× 逐平面 CA 缩放**
  （`caScale = 1 + ca[i]/2^21`，前 nc 个给 R、后 nc 个给 B）。`WarpRectilinear(1)` 这一格**不发**——
  它只带几何、不区分平面，发了不改变任何像素。
- 取"能达标的最低阶"策略在 CA-only 格把水位从 5e-4 收到 **5e-5**（共享的畸变臂不变）：Lunar 的表用一次项拟合
  残差 2.58e-4/2.17e-4（边缘 6048 px 宽上 ≈1.6 px 的残余色散），二次项 4.03e-5/2.14e-5（≈0.12/0.07 px）。
  实测 dngtool 停在一阶时是前者 ⇒ 收紧后停在二次项。
- 失败/恒等仍逐项点名：`lens-skipped=chromatic-aberration:{fit|residual|identity}`；旧的那种
  `distortion-off-and-no-standalone-warp` 只剩给"表不成对/越界"的一格。

### 43.2 读数（Lunar `-e -lossless -lens-corr`）
```
lens=SR2SubIFD 旧制式路由命中（密钥 0x44332211 块 56958 B） 三表可见性 vig=1 ca=1 dist=1 开关 vig=257 ca=1 dist=0
lens-vignette=applied nc=11 sw=257 maxres=0.001692 cornergain=1.222364
lens-skipped=distortion:switch-off
lens-ca=standalone 畸变关、CA 开 ⇒ warp2 几何恒等 + 逐平面缩放 nc=11
lens-warp2=applied planes=3 sw=0 ca=per-plane geo=identity maxres=0.000040/0.000000/0.000021
lens-opcodes=2 已写进 OpcodeList3(51022)
```
Adobe 读者（`dng_validate -v`，本仓构建）打出 `Opcode: WarpRectilinear2, minVersion = 1.6.0.0, flags = 1`，
三平面径向系数 = `[0.999385, 0.001526, -0.001601, 0…] / [1, 0, 0, …] / [1.000779, -0.001046, 0.001304, 0…]`，
`Validation complete`。把这三行系数在 11 个结点（`(i+0.5)/(nc-1)`）上对独立解码器 0x7980 表换算值逐点比：
**最大偏差 3.97e-5**（闸 1e-4）；绿平面严格恒等。期望值由 `_probe-sony-enciphered-lens.py` 的读数算，不采信 dngtool 的 `maxres`。

### 43.3 回归：新制式（7M2）一个位没动
- 旧发布件（`172b7f59ab7d75a3`）与新件各跑 `-e -lossless -lens-corr` 同一件 `Sony_ILCE7M2_IDC.ARW`：
  `lens-*` 日志**只差新增的 `geo=dist` 标记**（`maxres=0.000058/0.000074/0.000175` 三值逐字相同）；
  `dng_validate -v` 抽出 `Opcode/Plane/Radial params/min_valid/max_valid` 共 35 行，**diff 为空**
  ⇒ 收紧的水位只作用在 `caOnly` 格，畸变+CA 的既发载荷逐系数不变。
- 新制式主路仍先于 SR2 回退命中（`lens=` 行不出现 `SR2SubIFD`）。

### 43.4 闸：#77 加 ㉙d/㉙e，两条负控各自归位
- ㉙a 换成"CA 不再跟着丢"：要求 `lens-ca=standalone`、`geo=identity`、Adobe 打出 `WarpRectilinear2`，
  且旧点名 `distortion-off-and-no-standalone-warp` **必须消失**（与新点名互斥，且由 ㉙d 的旧件给正控）。
- ㉙d（负控②）：CA 独立发射**之前**的发布件 `dngtool.before-ca-standalone.exe`（`172b7f59ab7d75a3`，
  即 §41.4 的部署件）同一输入必须仍点名旧跳过、无 `lens-ca=standalone`、产物无 `WarpRectilinear2`。
- ㉙e（系数对撞）：见 43.2，闸 1e-4，实测 3.97e-5，并同时要求绿平面恒等。
- ㉙c 的负控 exe 本轮**被清扫洗掉过**（`tests/output/results/_main/` 是 gitignored 的产物区）：用 HEAD 源
  （`git show HEAD:tools/src/dngtool/dngtool.cpp`，无 SR2 路由、无崩溃修）在现成 build 目录重编、留档为
  `dngtool.before-sr2route.exe`（sha16 **`51018ac98e8332dd`**），随后把工作树源按字节还原（`sha256sum -c` 通过）再重编。
  注意：MSVC 重编的哈希不保证与旧件相同（本机实测两次同源构建 `cb476516…` vs `4abd84bb…`）⇒ 负控按**行为**认，
  不按哈希认。同一份 HEAD 构建顺带兼作 ㉖f/㉗a 的修前 exe（实测 `-O` 到不存在目录 rc=**-1073740791=0xC0000409**；
  DP1 X3F 仍写 `WhiteLevel=65535`）⇒ 把它另存为 `dngtool.before-crashfix.exe` 后，㉖f/㉖g/㉗a/㉗b 从"未判"回到可判。
- 部署：`publish/PLAN/artifacts/dngtool.exe` = `tools/src/dngtool/build/Release/dngtool.exe`，sha256 前 16 **`4abd84bb148bcd39`**；
  `verify-ps-compat` 13/0。

### 43.5 读数与一条未归因的假红
| 次 | 读数 | 说明 |
|---|---|---|
| ① 02:44（controls 缺） | 95/0 | ㉖f/㉖g/㉗a/㉗b 走"未判"（`before-crashfix.exe` 不在位）；㉙a–e 全绿 |
| ② 02:48 | **98/1** | ⑳p 的 Olympus_E10 臂：产品 `rc=0` 但 `vend-Olympus_E10/` **一个文件都没写**（连 `log.txt` 也没有）⇒ 覆盖判据 rc=2；同次 ⑳n/⑳o 与另外三件都绿 |
| ③ 02:51 / ④ 02:55 | **99/0 · 99/0** | 空机、无锁；㉙e 偏差 3.97e-5、绿平面恒等 |
- ②的那一格**不作结论**：产品 `rc=0 且无产物`是"静默 no-op"的形状，但只出现一次、同轮其它产品臂正常，
  且当时无第二条产品实例可 A/B ⇒ 按**未归因**登记（若复现，先查 headless 队列的单实例/写盘路径，
  别把它归给 dngtool——那两条链路没有交集）。证据留在 `tests/output/lens41/gate77-ca2.log`。
- **整轮已跑**（`tests/output/lens41/round-ca-heavy.log`，78 条受管脚本，五把重臂全开）：**77 绿 / 1 红**，
  #77 轮内 **PASS=105 FAIL=0（264.3 s）** ⇒ ⑯e/⑱f/㉑/㉒d/㉒e/㉓f 六臂从"未判"转绿（105 = 默认档 99 + 六臂）。
  唯一红 = `probe:bench`：`8b sRGB→P3 单核 ≥25MPix/s（实 24.3）`，与 §42.1 同族的**边界阈值 × 本机计时底噪**，
  本轮不做代码归因、不抬阈值。被测产物指纹（起飞与落地同值）：`FfmpegGui.dll=472D518E971DB226`、
  `ServiceProbe.exe=B9B5D51906AC1A7A`、`UiTestHost.exe=1CA4D5572DA12888`、`GainMapTestHost.exe=593D8381CDCCC42E`、
  `dngtool.exe=4abd84bb148bcd39`；运行器 `F1CE9994C976B381 lines=1590`。
  ⚠ 归属：`src/` 在 02:41 被**另一会话**改过（`QueueProcessor.cs` 等），`FfmpegGui.dll` 是它 02:50 的构建 ⇒
  这一轮的 77 绿覆盖的是**当前树**（含它的改动），不是"只有我这一批"的读数；DNG 侧的新判据由 ㉙a–e 自己给数。

### 43.6 欠账变化
- §41.5 第 1 条（CA 在畸变关时发不出）**已收口**；`Tag9405a` 无前缀约定（第 2/3 条）与 Samsung/Olympus（第 4 条）不变。
- §41.5 第 5 条更新：三条负控 exe（`before-sr2route` / `before-ca-standalone` / `before-crashfix`）都住在 gitignored 的
  `tests/output/results/_main/`，本轮已按 sha16 记档并写清重建办法；`_probe-sony-enciphered-lens.py` 仍**未跟踪**，
  提交这批时 `tests/scripts/_probe-sony-enciphered-lens.py` 必须一起进索引（㉙b/㉙e 都靠它给数）。

## §44 2026-10-23：私有 RAW→DNG 保留/损失审计（读者矩阵 · 逐实例账本 · 裁剪与色彩收口）

> 本轮是**审计 + 修一处**，不是"再加一批标签"。判词先行：
> 1) 9,517 条的口径是**厂商组标签实例**，母体 = 44 件样本里 41 件有厂商组的文件（3 件零条目：
>    `RPi.raw`、`RaspberryPi_RP_ov5647_11.raw` 是 LibRaw-only 的裸数据；`Canon_PowerShot_S2_IS_1.cr2`
>    其实是**已转换的 DNG**，FileType=DNG，无厂商组）。
> 2) "93 条可修复（CR3）"**不存在**：那是写死的字符串（§34 已撤回），真实读数见 §44.5。
> 3) "86 条色彩配方/矩阵"是名字正则的类级计数，逐实例只有 **34** 条（§44.4）。
> 4) "152 条裁剪"按逐实例重算是 **176**（164 + 12 条 CR3 的 Sensor*Border，§44.3）。
> 5) 8,072 条"未分類"按逐实例是 **7,156 条未审计**；差 916 条是规则表新增可判定类后转出的，
>    **不等于**语义已核完。
> 6) 本轮真正落地的一处行为改动：厂商内嵌裁剪 → `DefaultCrop`（13 件映射、4 件拒绝、其余无来源）。
>    它由 ㉚a–d 四臂 + 机身 JPEG 对拍钉住，**不是**按名字猜。

### 44.1 读者能力矩阵（读 / 解析 / 施加 / 像素验证 四层，分开写）

| 读者（版本） | EXIF/标准位 | OpcodeList1/2/3 | XMP | 预览 | 实测到哪一层 | 证据 |
|---|---|---|---|---|---|---|
| Adobe DNG SDK 1.7.1（`dng_validate` sha16 `69bd4a1e0406df37`） | 读 | **三条都施加** | 读（受构建开关） | 读/写 | **像素级验证**：⑱f 环带增益、㉚a 渲染尺寸、㉜ MAD | `dng_negative.cpp:4855/4922/5232`；本机 ⑱f/㉚/㉜ 日志 |
| ExifTool 13.59 | 读/写 | **只读不施加**（把列表当字节串打印） | 读/写 | 读/写 | 标签层 | 本机 `exiftool -v3` 读数 |
| LibRaw 0.22.0（dngtool 内置） | 读 | **只存不施加**：三张表按原始字节存进 `rawopcodes[0..2]`，处理链里零处使用 | 读 | 读 | 元数据层 | `tiff.cpp:1700-1725`；`grep -rn rawopcodes src/postprocessing src/utils` = 0 |
| rawpy 0.27.1 / LibRaw 0.22.1 | 读 | 同上（存不施加） | 读 | 读 | 像素级**另一份构建**：X3F/裁剪/色彩对撞都用它 | `rawpy.sizes.crop_*` = `raw_inset_crops[0]`；`postprocess` 用 `sizes.width/height`（不吃 DefaultCrop） |
| RawSpeed（`tests/output/lens-probe/rs_DngDecoder.cpp`，2026-10-18 取件） | 读 | **List1 施加；List2 只对 lossy DNG(0x884c) 施加；本路径无 List3** | — | — | 源码层 | `rs_DngDecoder.cpp:591-645` |
| darktable（`tests/output/lens-probe/dt_dng_opcode.c`，同日取件） | 读 | List3 只认 **1/3**（14 打印 unsupported）；List2 只认 GainMap，**MapTable 不认** | — | — | 源码层 | `dt_dng_opcode.c:117/224-285` |
| ACR / Photoshop | — | — | — | — | **未验证**：本轮没有跑 ACR/PS（GUI 自动化不在本审计范围） | 不把 `dng_validate` 当 ACR |

⚠ 两条不能混的口径：**"读者能读标签" ≠ "读者施加它"**（LibRaw/ExifTool/RawSpeed-List3）；
**"dng_validate 施加" ≠ "ACR 也施加"**（ACR 的 profile 选取、镜头配置、预览路径都不同）。
§44.4 的 ForwardMatrix 读数就是"哪张矩阵真被 dng_validate 用"的实测，不是猜。

### 44.2 总账（`tests/output/dng-audit/run4/`，`--verify` 可复算）

母体 **9,517** = 已审计 **2,361（24.8%）** + 未审计 **7,156**。处置互斥求和 = 等级求和 = 类求和 = 9,517。

| 处置 | 条数 | 含义 |
|---|---:|---|
| carry_verified | 174 | 值进了标准位，且**回读逐值相同**（裁剪 31、镜头表/开关、坏点、预览等） |
| carry_indirect | 451 | 经 LibRaw/派生量进标准位（ActiveArea、BlackLevel、AsShotNeutral、传感器幅面） |
| carry_gap | 5 | **标准位存在、值也在源里，但产品没搬**（可修缺口；不是"无载体"） |
| info_duplicate_standard | 341 | 同值已在标准组另有一份并被 ⑳ 后置搬（厂商副本） |
| drop_no_carrier | 929 | 规范没有对应位（机内设置/状态/开关） |
| drop_semantics_unknown | 386 | 有载体但其语义/方向/量纲未定义 ⇒ **不冒名写入** |
| policy_discarded | 75 | 源容器布局 / 注记版本号等，按政策丢弃 |
| unaudited | 7,156 | 规则表未覆盖，**不据此断言无价值** |

证据等级：verified 814 / source_inferred 1,223 / pending 7,480。
未审计的 7,156 里 **5,443 是不透明块**（`Sony_SR2DataIFD_0x*`、`Samsung_Type2_0x*`、`Unknown_*`），
其余 1,713 是长尾名字；它们**不是"已证明无用"**，只是还没有一条能指到读数的语义。

类分布（前 12）：不透明 5,443｜未归类 1,655｜白平衡 346｜色调 324｜标准字段副本 277｜
机内风格 258｜镜头标识副本 198｜机身状态 188｜**裁剪 176**｜AF 96｜机身标识 86｜镜头校正表 75。

⚠ 旧账的 690/603 两个数**在在册日志里找不到可复现口径**（没有对应脚本/日志），本轮不再引用；
152→176、86→34、8,072→7,156 都换成逐实例重算，且 `--verify` 从 ledger 重算三类和。

### 44.3 裁剪/有效区：176 条，13 件映射、4 件拒绝、其余无来源

口径：`ActiveArea`(50829) = LibRaw 可见区（存储平面里属于图像的部分）；`DefaultCrop`(50719/20)
= 机身默认取景框，**原点相对 ActiveArea**。二者不必相等 —— Adobe 官方样例
`dng_sdk_1_7_1/sample_files/03_jxl_bayer_raw_integer.dng` 就是 ActiveArea 9600×6376、
DefaultCrop 9504×6336@(32,20)。

**改动**（`tools/src/dngtool/dngtool.cpp`）：把 LibRaw `raw_inset_crops[0]`（0.20 起标注为
"vendor specified crop"）映射进 DefaultCrop，门槛四条：厂商解析器**真的给了原点**、值在
ActiveArea 内、≥55%（与 `adjust_to_raw_inset_crop` 的 maxcrop 同阈）、且只在白名单 make 上做
（Canon/Fuji/Panasonic/Leica/Pentax/Nikon/Olympus/Kodak/Epson；**Sony 的 0xb02b 只有宽高**，
cleft=0 是缺省不是证据 ⇒ 不做）。

逐件读数（`tests/output/dng-crop-map/run-b/crop-map.csv`，44 行）：
**mapped 13**（Canon 40D×2、PowerShot S2 IS、Fuji×5、Leica×3、Panasonic LX7、Pentax istDL；
`dng_validate -tif` 渲染尺寸逐一等于 DefaultCrop）、**vendor-wh-only 3**（Sony 7M2/7S/Lunar）、
**outside-active 1**（EOS R6 的内嵌裁剪 3408×2272 比 ActiveArea 3407×2271 大 1 px ⇒ 拒绝）、
**无厂商裁剪 22**、**转不出件 5**（RPi×2 + Coolscan×3）。

负控与原点证据：
- ㉚b：Sony 与 R6 必须**拒绝**且保持满幅（日志里点名 `rejected: vendor-wh-only(no origin)` /
  `outside-active`）；
- ㉚c：修前 exe（`tests/output/results/_main/dngtool.before-croproute.exe`）同一 Canon 输入仍写
  `1944×1296@0,0`、无 vendor 点名 ⇒ ㉚a 的绿是修出来的；
- ㉚d：Canon 的 (4,3) 是 LibRaw 把 AspectInfo(0,0) + SensorInfo **相加**合成出来的，用**机身 JPEG**
  （1936×1288，与裁剪框同尺寸）做滑窗对拍：最优 **(4,3) corr=0.9422**，次优 (5,3)=0.8696，
  (3,3)=0.7765 ⇒ 原点有独立证据，不是合成怪癖。

仍未映射（如实登记，不猜）：E-10 `OlympusImageWidth/Height` 2240×1680 无原点、且可见区
2256×1684 的边框**不是黑边**（列/行均值 85~163，非 masked）；EX1 `SensorAreas`
`0 0 3688 2780 16 16 3664 2752`（full/valid 两组的字段序只有 ExifTool 一行注释）；
Minolta/Sigma 的 `ImageWidth` 比 LibRaw 可见区小 8~11 px、无原点；Sony 见上。

### 44.4 色彩：34 条（不是 86），产物矩阵不是厂商矩阵，读手真正用的是 ForwardMatrix

逐实例 34 = 色彩矩阵 C1 **12** + 变体 C3 **2** + 设置 C2 **18** + JPEG 输出矩阵 C4 **2**。
旧 86 的来源是正则 `color_?matrix|cc_?|chroma|color_?data|matrix|filter`：同一遍名字命中会捞进
`ChromaticAberrationParams`（镜头 CA 表）、`Sony_SR2DataIFD_0x78cc`（不透明块）、`FilterEffect*`
（机内风格）等；按逐实例重算后这些各归各类。

逐值对撞（`tests/scripts/_probe-color-matrix-diff.py`，读数在
`tests/output/dng-audit/run4/color-matrix-diff.txt`）：14 条厂商矩阵（Olympus/Samsung/Sony/
Hasselblad-Lunar/Pentax A2·B2/PhaseOne/Leaf）与产物 `ColorMatrix1/2` **没有一条相等** ——
直接或逆矩阵取小后，最佳残差仍在 **0.38~0.83**。定点的尺度由行和定（Samsung=256、Sony=1024、
Pentax A2/B2=8192），不是硬编码猜的。⇒ 这 14 条的状态是 `drop_semantics_unknown`：
产物标准位由 LibRaw `adobe_coeff`（= Adobe ColorMatrix2 标定）填，厂商矩阵的坐标方向/光源没有
文档定义，**不冒名"已搬运"**。

"哪张矩阵真正生效"由 ㉜ 的渲染对撞回答（`_probe-dng-color-effect.py`，Sony 7M2 产物，
dng_validate 1.7.1 默认 8-bit sRGB）：
- `AsShotNeutral` R×2 ⇒ 逐像素 MAD **38.531**（WB 是活的）；
- 只改 `ColorMatrix1` 第一行 ×0.8 ⇒ MAD **0.117**（几乎不动）；
- 删掉 `ForwardMatrix1/2` ⇒ 回退到 ColorMatrix，MAD **1.605**（= 13.7×）⇒ **DNG SDK 用的是 FM**。
⚠ 直接缩放 FM 会破坏 D50 行归一化、`dng_validate` 判 106（实测）⇒ 用"删除"做开/关对照。
这不是 ACR 的证明；ACR 未跑。

设置类逐值核对：`NDFilter`/`InternalNDFilter`/`ColorCompensationFilter`/`ColorFilter`/`BWFilter`
本机样本值为 0/Off ⇒ 无图像效果；`CoringFilter`=1792（非 0）是降噪阈值，DNG 无 coring 位，
渲染细节会与机内不同；`ColorMatrixNumber`/`ColorDataVersion` 是编号；Samsung
`ColorMatrixSRGB/AdobeRGB` 是 **JPEG 输出**矩阵，不是 raw 矩阵。

### 44.5 CR3 的 198/93：93 不存在；198 是"Canon 组唯一键名"，母体按实例是 220

- **198** = Canon 组 **200 条实例去重后的 198 个键名**（`Unknown_CCDT` 出现 3 次）；
- 母体按**实例**算 = **220 = Canon 200 + CanonCustom 20**，**在** 9,517 之内；
- **93** 是 `_check-dng-private-carry.py:64` 里写死的字符串（§34 已裁定撤回）。两种命令形状的
  真实到达数是 **0 / 0**：dngtool 对 CR3 判 notTiff 不写注记；产品后置命令形状下也是 0（§39）。
- 实际可修类（按读数，不按 93）：
  1. `SensorLeft/Top/Right/BottomBorder` 4 条 **已间接搬运**：值与产物 `ActiveArea=108 156 2379 3563`
     逐值相同（P4，carry_indirect）；
  2. `ImageUniqueID`（EXIF 0xA420）**只在 [Canon] 组**，后置按 ExifIFD 组限定 ⇒ 没搬 ⇒
     `carry_gap`（1 条；Sigma X3F 的厂商副本与 ExifIFD 值还不同，4b vs 00，**不能**无条件回退取任意组）；
  3. `PeripheralLightingSetting`/`ChromaticAberrationSetting` 等是**镜头校正开关**，但 Canon 的
     镜头表没有实现（L1 只覆盖 Sony/Fuji/Panasonic）⇒ 有开关无表，属独立大件；
  4. 其余 110 条是机内设置/不透明块（Flash/Bracket/Ambience/Canon_0x* 等），无标准位或按政策丢弃。
⇒ 没有"93 条待修清单"；可执行的是"1 条标准位缺口 + 1 个 Canon 镜头表大件"。

### 44.6 COOLSCAN：3 件拒绝是**缺矩阵**，不是缺 SamplesPerPixel

3 件（`Nikon_CoolscanV.NEF`、`Nikon_Nikon_COOLSCAN_V_ED_0.nef`、`Nikon_Nikon_COOLSCAN_IV_ED_1.nef`）
`dngtool -e` 均 rc=9（`refuse-linearraw-without-colormatrix`）；它们在母体里贡献 **74 条**厂商实例
（25 + 49），但拒绝是**文件级**结果，不是逐实例丢弃。

源侧事实（ExifTool 直读，SubIFD）：`SamplesPerPixel=3`、`BitsPerSample=16 16 16`、
`PhotometricInterpretation=RGB` —— **通道数/布局/光度学是可靠可判的**；LibRaw 也报 colors=3、
filters=0（4 分量缓冲 RGBG，第 4 分量是 G 副本）。真正的阻塞是 `cam_xyz` 全零：没有相机原生→XYZ
标定，而 DNG 1.7.1 要求非单色 DNG 必带 ColorMatrix1（其 Count 才是平面数的声明方式）。源件
Exif `ColorSpace=sRGB` 是**扫描输出色彩空间**，不能当"raw 已是线性 sRGB"的证据。
⇒ **维持显式拒绝**（不编造矩阵）。若将来要开，需要 Nikon Scan/Capture One 给出该扫描仪的
设备→XYZ 标定，或明确按"已编码 sRGB 图"走非 DNG 出口，而不是塞 identity 矩阵。

### 44.7 图像效果可复现测试：有开/关 + 像素验证的格子

| 效果 | 开/关对照 | 像素级验证 | 落点 |
|---|---|---|---|
| 畸变+CA+暗角 | 加/不加 `-lens-corr` | Adobe 渲染环带 1.0946 对表预测 1.1117（≤5%）；20,000 点抽样 19,985 不同 | ⑱f / ⑱x / ⑱y |
| 坏点表 | 有表/无表 | 点集逐条相同 + Adobe 解析出 FixBadPixelsList | ㉒a/㉒b/㉒f |
| 黑电平声明 | 修补前/后 | 存储平面逐位相同（只动元数据） | ㉓c |
| 裁剪 | 厂商裁剪 vs 满幅 | 机身 JPEG 原点对拍 0.9422；`dng_validate` 渲染尺寸 = DefaultCrop | ㉚a/㉚d |
| 色彩 | ASN×2 / 删 FM / 改 CM1 | MAD 38.531 / 1.605 / 0.117 | ㉜ |

**没有的**（不写成有）：单独"暗角开/关"的像素臂（⑱f 是三支一起施加）；色彩**准确度**没有
ACR/PS 对照（只有"字段是否被 dng_validate 施加"）；预览 ≠ RAW 处理语义只在尺寸/opcode 层，
没有"用预览当解码结果"的反例臂。

### 44.8 代码与测试变更（本轮）

1. `tools/src/dngtool/dngtool.cpp`：DefaultCrop ← `raw_inset_crops[0]`（白名单 + 四条门槛 +
   点名 `default-crop=vendor|active(... rejected: ...)`）。新 exe sha256 前 16 = `8cd334a61b847973`；
   旧件留档 `tests/output/results/_main/dngtool.before-croproute.exe`。
2. `tests/scripts/_readback-dng-lens.py`：归一化基从 DefaultCrop 改为 **ActiveArea**（opcode 的
   坐标基按规范是 ActiveArea；旧写法只在"DefaultCrop==ActiveArea"时才对）。修前 ⑱x/⑱y 两条红，
   修后 105/0。
3. `tests/scripts/verify-dng-metadata-transport.ps1`：新增 ㉚a–d（裁剪映射/两负控/机身 JPEG 对拍）、
   ㉛（账本自洽，total 钉 9,517）、㉜（色彩开/关渲染 MAD）。
4. 新探针（均平铺在 `tests/scripts/`，读数为证）：
   `_probe-dng-crop-map.py`（44 件三侧对账）、`_probe-crop-preview-align.py`（原点对拍）、
   `_probe-color-matrix-diff.py`（14 条矩阵逐值）、`_probe-dng-color-effect.py`（三处变异渲染）。
5. `_audit-dng-meta-carry.py`：新增 crop/color 逐件覆盖、`carry_gap` 处置、`--crop-map` 输入，
   并把"处置和=等级和=类和=总数"做成 `--verify`。

**读数**：#77 默认档 **105/0**（`tests/output/dng-crop/verify77-crop7.log`；99 + ㉚×4 + ㉛ + ㉜）。
**整轮已复跑**（新 dngtool）。full22 **38 红**，根因是沙箱拦 ffmpeg 往 `%TEMP%\cms_*.rgb48` 写
中间件（`exit -13`，`tests/output/validate/fmtmatrix_4ef00c74/run_webp.out` 逐行可见），**不是**
dngtool 改动；把 `TMP/TEMP/TMPDIR` 指进 `tests/output/_tmp` 后 full23 = **77 绿/1 红**
（`tests/output/gates-round-2026-10-23-full23.log`），唯一红是 `bench` 两条计时阈值
（多核扩展 `4.0×` vs ≥4×、8b 单核 `24.3` vs ≥25 MPix/s）；同二进制空载独跑 `bench` = **37/0**
（43.5/46.1 MPix/s，表增益 6.2×/6.9×，端到端 H1/H2=1.76×）⇒ 轮内争用，不是回退。
被测产物：`FfmpegGui.dll` sha16 `8e2b801cad9f3a90`、dngtool `8cd334a61b847973`、
runner self-sha16 `F1CE9994C976B381`。五把重臂在新默认档下**未复跑**（预期 111，不写成读数）。

### 44.9 后续清单（按收益/成本排序）

1. ~~**整轮复跑**（新 dngtool `8cd334a6`）~~ → 已做（full23，见 44.8）：38 红全是环境，剩
   `bench` 属计时类，须空载复跑；独跑已 37/0。**仍不能**据此宣布 78/78 —— 轮内那条红没消失。
2. **`ImageUniqueID` 条件搬运**（carry_gap 5）：低代码量；规则必须是"有 ExifIFD 取 ExifIFD，
   否则按 make 取厂商组"，否则 Sigma 的两份不同值会被写错。需要 ⑳ 一条新臂。
3. **`ExifImageWidth/Height` 与 DefaultCrop 的一致性**：后置把源的 ExifImageWidth 原样抄进 DNG
   （RAF/RWL 里那常是**预览/JPEG** 尺寸：Fuji 2048×1536 vs DefaultCrop 2816×2116；Sony 6000×4000
   vs 6024×4024）。要么按产物几何重写，要么明确"这是源 EXIF 原值"并给判据；现状是**没有判据**。
4. **E-10/EX1/Samsung/Minolta/Sigma 的厂商裁剪**：需要独立原点证据（厂商 SDK / Capture One 对拍）
   才能进 DefaultCrop；在此之前保持 `drop_semantics_unknown`。
5. **Canon/Nikon 镜头校正表**：开关在源里、表没实现；高成本，且 ACR 自带 profile ⇒ 先评估
   "不搬是否已被 ACR 的 profile 覆盖"，再决定是否做。
6. **厂商矩阵采纳**（PhaseOne/Leaf/Pentax/Sony/Samsung/Olympus）：高成本、需要 ACR/Capture One
   的色彩对拍；当前"不冒名"是正确状态。
7. **外部读者缺口登记**（不是本仓缺陷）：RawSpeed 不施加 List3；darktable 不施加 WarpRectilinear2(14)
   与 MapTable(7) ⇒ "产物在第三方软件里没效果"可能只是**读者不支持**，判据要按读者分档。

### 44.10 未验证 / 开放问题（不许写成结论）

- **ACR/PS 行为未验证**：本轮只有 DNG SDK 1.7.1 的施加读数。
- RawSpeed/darktable 的取件是 2026-10-18 的快照，**版本号未钉**；其上游之后的变化未复核。
- Samsung `SensorAreas` 的字段序、E-10/EX1 的原点、Minolta/Sigma 的小尺寸图，都只有
  "名字+数值形状"的证据，**没有**厂商文档或第二实现。
- CR3 的 110 条未审计条目里可能还有标准位副本（本轮只逐值核了 4 条边界 + ImageUniqueID）；
  其余按"未审计"处理，不据此断言丢失。
- Coolscan 的扫描数据到底是线性还是已编码 sRGB，**未验证**；当前拒绝不依赖这个判断。

