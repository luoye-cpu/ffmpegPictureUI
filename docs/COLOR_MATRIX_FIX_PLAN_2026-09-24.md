# 色彩矩阵修复规划（2026-09-24）

取证的数字全在 `COLOR_MATRIX_AUDIT_2026-09-24.md`，本文件只写**怎么修、按什么顺序、每步怎么算验收、哪几处需要维护者点头**。

## 0. 依赖与顺序

```
P1 原色表填正 ──┬── P5 colormath 接进 54 条清单 ── 里程碑 2（ColorKit 色彩数学层）
                ├── P4 容差收口（依赖 P1 的同矩阵白名单结论）
                └── P4.4 线性余量（clamp=false 实现）── P3 像素路径清退 zimg（已定案：完全走自研）
P2 抖动中心 ────┘（与 P1 无耦合，可并行；但 P3 的每一格对拍都要用它先落地）
```
**执行顺序**：P1 → P2 → P4.4 → P3（逐格迁、逐格对拍）→ P4 其余 → P5。
整轮只在 **P1 完、P3 三格全迁完、P5 完** 三个点各跑一次；其余用 `-Scripts` 子集（见项目记忆「整轮不是每步的验证手段」）。
凡改 `src/FfmpegGui` 都要**同批重建四项目**（src / ServiceProbe / UiTestHost / GainMapTestHost）并手核四份 `FfmpegGui.dll` 指纹。

---

## P1 原色表填正（P1 级缺陷，先做）

**改什么**：`ColorSpaceRegistry.cs:544-561` 的 `PrimariesTable`。当前 5 个 token（`bt470m/bt470bg/smpte170m/smpte240m/jedec-p22`）
共用一组常数、`smpte428` 是 `smpte431` 的副本。

**常数从哪来 —— 三条锚，缺一条就不许填**：
1. **锚 A｜外部实现**：zimg。取法已固化（`docs/COLOR_MATRIX_AUDIT_2026-09-24.md §7`），由矩阵**反解 xy**：
   `RGB→XYZ = M⁻¹·(线性矩阵)`，与 `PrimariesToXyz` 同族，可写成一次性脚本。
2. **锚 B｜自洽不变量**（不依赖任何数值）：每行之和 = 1（白→白，与白点无关）、`M(a→b)·M(b→a) ≈ I`、`det > 0`、同原色返回 null。
   这条在本次审查里已经证明有效——它抓出了我自己重排平面序时的 4 个错。
3. **锚 C｜公布值抽样对账**：`bt2020`（BT.2080 表）、`bt709`/`sRGB`、`smpte432`（EG 432-1）三组我们手上已有权威值，
   实测与 zimg 差 ≤4e-6 ⇒ **先证明锚 A 可信，再用它去填锚 A 独占的那些 token**。

**P1 的填表数据已经跑出来了（2026-09-25，第二套独立推导贴 zimg 实测矩阵）**
`%TEMP%/p1search/search.py`：自己实现一份 `xy+白点 → RGB→XYZ（Bradford M⁻¹·L·M）`，与产品的推导**不是同一份代码**，
拿它去贴 zimg 实测矩阵，逐 token 结果：

| token | 最佳白点 | max\|Δ\| | 结论 |
|---|---|---|---|
| bt470bg / smpte170m / smpte240m / jedec-p22 | D65 (0.3127,0.3290) | **3.4e-6 ~ 4.9e-6** | ✅ 原色集贴死 ⇒ 这四组该各自分开填，别再共用 |
| smpte431 | DCI (0.314,0.351) | 2.3e-4 | ✅ 白点对（残差来自 lcms 只给 4 位小数） |
| bt470m | **Illuminant C** (0.3101,0.3162) | 1.6e-4 | ⚠ 白点定了是 C，但 4 位小数的原色xy不够精确 |
| film | Illuminant C | 2.1e-4 | 同上 |
| **smpte428** | — | **1.9 对不上** | ❌ **定案：xy 参数化表达不了它**（H.273 的蓝原色是 (0,0)，y=0 ⇒ 归一化退化）。两锚"冲突"其实不需要裁决 |

**P1 落实状态（2026-09-25，子 agent 改码 + 主循环读 diff 验收）**：`ColorSpaceRegistry.cs` 已拆表
（`bt470bg`=EBU 0.640/0.290/0.150、`smpte170m`=`smpte240m`=SMPTE-C 0.630/0.310/0.155、`bt470m`=0.670/0.210/0.140 +
新增 `IlluminantC`、`jedec-p22` 保留原那组并标明"旧表被五 token 共用的就是它"、`smpte428` 与 `film` **故意不收录**走 fail-closed）。
⇒ 主循环还剩两件**测试侧**的活（不许由改码的人顺手做）：
① `ServiceProbe` 的 `colormath` 里 `null` 只放行 `film` ⇒ `smpte428→null` 现在必红，要改成"film 与 smpte428 都必须 null"（方向反过来，不是放宽）；
② `:5614` 那条"不同标准不得同矩阵"要改成**显式白名单** `{smpte170m≡smpte240m}`（zimg 自己也让两者同原色），其余重复仍红。
③ 另记一条 Lane A 顺带挖到的**标签层**遗留：`ColorTransformPlan.cs:858` 仍把 601 系归一族选标签、且漏了 `smpte240m` ⇒ 属"选标注 token"而非"乘矩阵"，单独一批处理。
① **`smpte428` 的正确处置是从表里删掉、走 fail-closed**，并把"`smpte428 → null`"写成 `colormath` 的显式断言（不是挑一个"看着像"的原色填上，也不是二选一抄谁）；
② 表要能表达"C 白点"这类非 D65 白点 ⇒ `bt470m`/`film` 那两格填完仍只能贴到 ~2e-4，**要再严必须拿更高精度的 xy**（回 H.273 表 1 或 zimg 常数），别把 1e-3 当"验证过了"；
③ `colormath` 的容差应**分档**而不是一个数：可精确表达者 ≤1e-5、受 lcms 输出精度限制者 ≤5e-4，并把每档的**依据**写进断言文案。

**原来的"禁止凭记忆填常数"这条不变**：上面每组值都是**测**出来的（zimg 实测 + 第二套推导复现），不是背出来的。

**新判据（在 `ServiceProbe colormath` 内）**：
- 11 格逐元素对 zimg ≤1e-3 ⇒ 当前 6 红转 0 红；
- "不同标准不得算成同一矩阵"这条**不能一刀切**：zimg 自己认为 `smpte170m ≡ smpte240m`（同 SMPTE-C 原色，差异只在传递）
  ⇒ 改成**显式白名单**：允许的重复必须逐对写出理由，白名单外的重复即红。当前白名单预期只含这一对。
- 保留 `bt709→null`（同原色不映射）与 `film→null`（未收录必须 fail-closed）两条已定方向的断言。

**验收**：`ServiceProbe colormath` + `matrix`（原有 23 条一条不许退化）+ `-Scripts verify-color-strategy,verify-color-caps,verify-color-wiring` + 一次整轮。
**风险**：识别侧（`ColorSpaceIdentify.cs:64/79`、`ColorantsD50`）与映射侧共用这张表 ⇒ 填正会**同时改变** ICC 判色域的结果。
所以必须跑 `iccname` 与 `decision` 两个探针，不能只看 `colormath` 转绿。

### P1 收口状态（2026-09-26）

- ①②（测试侧两件）✅ 已做：`colormath` 现为 `pass=12 fail=0`，`null` 放行表 = `{film, smpte428}` 且**各带理由**，
  重复对白名单 = `{smpte170m≡smpte240m}` 一对（M2 变异实测：白名单外塞进 `smpte428≡smpte431` ⇒ 立刻红）。
- ③（标签层 `MatrixTokenFor` 漏 `smpte240m`）⇒ **决定不改代码**，理由写在这里而不是留成待办：
  该函数拿到的输入**只有 primaries token**，而 YUV 矩阵系数在 H.273 里是**独立字段**
  （`ColorSpaceDescriptor` 根本没有源矩阵字段，见 `:35` 只有 `PrimariesToken`）。
  ⇒ 给 `smpte240m` 补一条 `=> "smpte240m"` 等于**替源猜一个它没声明过的系数**；
  现行为 `_ => null` 落到 `RawColorPipeline.cs:781` 的 `if (!IsNullOrWhiteSpace(plan.OutMatrix))` ⇒ **不写 `-colorspace`**
  （容器侧即 unspecified），属"不知道就不宣称"，与本项目"调用方不得猜测 / 不得只改标签"的口径一致。
  ⚠ 反过来说：现有那几条**归族**映射（`bt2020→bt2020nc`、`smpte432/431→bt709`、601 系→`smpte170m`）之所以成立，
  是因为那三条是**我们自己选定的输出目标**（约定成对），不是从源继承来的。要真正修这条，
  得先给描述符加"源矩阵系数"字段并让它贯穿探测 ⇒ 属独立能力，不在色彩矩阵修复范围内。
- ✅ ④（容差**分档**）已做（2026-09-26）：`colormath` 不再是单一 1e-3 ⇒ **A 档 1e-5**（`bt2020 1.14e-6`、`bt470bg 3.38e-6`、
  `smpte432 3.95e-6`、`smpte170m/smpte240m 4.91e-6`、`jedec-p22 4.92e-6`；档值依据 = 锚 A 读数只有 5 位小数 ⇒ 舍入地板 5e-6）、
  **B 档 5e-4**（`bt470m 1.76e-4`、`smpte431 2.36e-4`；依据 = 白点位数上限，收紧要先取更高精度常数再同锚复测）。
  未登记档位的 token ⇒ 判红并点名（默认走宽档就是这条锁漏齿的方式）。牙数：把 `bt2020` 的 G 从 0.170 抄成 **0.1701**
  ⇒ `实差 2.83e-4` 转红，而旧的 1e-3 会**放过**这一格（这就是分档买到的东西）。

---

## P2 8-bit 抖动中心（P2，可与 P1 并行）

**改什么**：`ColorKernels.cs:615` 的 `Bayer4[i] - 0.5f/4f` ⇒ 减数应为 `7.5f/16f`（居中）；
`:335` 是同一处口径（且在**线性域**加 `t/255f`，趾段斜率 12.92 会把位移放大到 ~10 码值）⇒ 两处一起对齐，别留两副面孔。

**新判据（关键：先有牙再改）**：
- C12「**精确可表示的码值开抖动必须逐字节 no-op**」：造一张 16×16 图，16 个 Bayer 相位每个都被覆盖，
  源取 8-bit、经 `format=rgb48le` 升位后走 8-bit 降位内核 ⇒ 输出必须逐字节等于输入。
  变异验证：把减数改回 `0.5f/4f`，C12 必须红（现 C10/C11 只测 tile 一致与单调，**不依赖这 0.344 偏置**，已核实 → 修它不会撞既有断言）。
**需点头的选项 B**：源位深 ≥ 出口位深且本可 1:1 表示时，**整条抖动直接跳过**（比居中更彻底，"不花这笔钱"）。
代价是要引入"何时该抖"的判据（源位深/曲线拐点）。默认建议：**先居中（P2 本体），跳过另立一项**。

**验收**：`-Probes bitdepth,selftest` + `-Scripts verify-gamut-map,verify-decision-delivery`（这两个比产物字节/PSNR，抖动改了必动它们）。

### P2 收口状态（2026-09-26）

- ✅ **中心偏移两处都已对齐**：`BayerCenter = 7.5f/16f`（`ColorKernels.cs:140`）被 `:361`（RGBA 出口）与 `:660`（rgb48→rgb8 出口）共用
  ⇒ `t ∈ [−0.46875, +0.46875]`、均值 0、`|t| < 0.5`。
- ✅ **C12 已建且先验过牙**（在 `contract` mode，`pass=115 fail=0`）：判据 = 16×16 图覆盖全部 16 个 Bayer 相位、
  源取整条 8bit 码轴的**精确可表示值**（16bit = 257×码值）⇒ 开抖动降位后必须逐字节等于输入。
  变异（把减数改回 `0.5f/4f`）实测：**`抬升 288 / 下降 0`，最差相位 48 次**（288/768 = 37.5% = 理论值 6/16，
  且**只抬不降** ⇒ 是系统性提亮，不是噪声）；同时 **C10/C11 五条全部照旧绿** ⇒ 印证"既有断言对这 0.344 偏置无感"，
  这条锁补的正是那个洞。C12 自带两条陪跑判据在变异下也保持绿（关掉抖动的对照、半档输入必须展开成 ≥2 种输出的哨兵）
  ⇒ 红只能来自"no-op 被破坏"，不会来自"抖动没跑"。源文件按字节备份还原（`sha256=f8ef53d9912488bf` 一致）。
- ✅ **抖动加在哪个域 —— 已修（2026-09-26，原判"必须单独一批"的前提被实测推翻）**。
  当时列的三个阻塞理由里，(i) 成立但代价比预估小：不必换表，`EncodeTo8(oetf=null, …)` 的**解析式分支**就能拿到分数码值
  （表的值域是整数 8bit ⇒ 加不上 t，所以走的正是"不查表"那一支）；(ii) **不成立** —— 经全仓点名核对，
  `TransformRgba8` 的**产品调用数为 0**（4 个调用点全在 `ServiceProbe`），`SimdPixelOps` 里**没有**这条出口的 SIMD 版本、
  也没有任何 `tests/scripts` 门禁观测它的字节/PSNR ⇒ 不动任何像素基线；(iii) 仍成立但只剩"别在两副面孔里挑"这一条。
  实测账（`contract` = **119/0**）：
  | 判据 | 修复前（线性域） | 修复后（码域） |
  |---|---|---|
  | C13 对照（不抖时 identity 直通逐字节还原） | 破坏 0 处 ✅ | 破坏 0 处 ✅ |
  | C13 主判据（8bit→8bit 精确可表示 ⇒ 开抖动仍须还原） | **被改变 213/768、最低受损码值 2** ⇒ 红 | 改动 0 ✅ |
  | C13 幅度上限（与源码值偏离 ≤1 档） | **最大 6 档** ⇒ 红 | 0 ✅ |
  | C13 哨兵（跨色域矩阵 + 非中性输入 ⇒ 抖动仍生效且 ≤1） | —（当时还没这条） | 改动 185/768、最大 1 档 ✅ |
  ⚠ 两次变异分别咬两个方向：把抖动**改回线性域** ⇒ 上面两条红（`213 / 6`）；把抖动**整段静默关掉** ⇒
  三条 no-op 判据全绿、**只有哨兵红**（`改动 0/768`）⇒ 哨兵是防"用降级冒充修复"的那一颗牙，不是装饰。
  源码按备份还原后四份 `FfmpegGui.dll` = **`61182f654460859a`**（一致），cjk 仍 `14/0`（新增的都是 `//` 注释与无引号文案）。
- ⚠ **仍开：②（tonemap 链标签 `bt709` 而像素是纯 γ(1/2.4)）** ⇒ 不许用改标签收口。2026-09-26 用本机 `zscale` 复测（`nullsrc→geq` 造线性 16bit 斜坡、`tin=linear:t=<trc>`，脚本 `qoder-scratch/zimg-trc-probe.py`）：
  | `t=` | 与**定义式**最大偏离 | 与纯 γ(1/2.4) 最大偏离 | 判定 |
  |---|---|---|---|
  | `bt709` | 0.1103 = **7231 LSB@16b = 28.1 LSB@8bit** | 0.0039（＝读数地板） | 纯 γ(1/2.4)，与审计 §3 同向（登记值 27.2；差的那 ~1 码是本探针输出落在 8bit 网格的地板，`t=linear` 那行同样带 0.0038 地板可对照） |
  | `iec61966-2-1` | 0.0039（＝地板） | 0.0493 = 12.6 LSB@8bit | **分段＝sRGB 定义式** |
  ⇒ 岔路判定：zimg 能正确做 sRGB 分段、但不能做 BT.709 定义式分段 ⇒ "把链换成 `t=iec61966-2-1` 并同步改标签"**不是**本案的修法
  （那会把 PNG 出口的 cICP 从 `bt709/bt709` 改成 `bt709/iec61966-2-1` ⇒ 改交付，且 §P3 那条锁 `:126-128` 正盯着它）。
  唯一自洽的修法仍是 P3 那一格：**该链的编码段走自研内核 OETF**（`Std` 分支 `ColorKernels.cs:396-398` + `TransformSpec.Tone`）。

  ### ②的产品级定位（2026-09-26 实跑四格 + 两格，脚本 `qoder-scratch/t709probe.ps1` / `t709leg.ps1`）
  | 跑法 | 产物字节 | 解码 md5 | 结论 |
  |---|---|---|---|
  | 默认格（HDR→8bit PNG）`--color-709-curve std` | 39594 | `b5239a37dac7` | 走**引擎**（进程内）⇒ 编码用的是本仓 `TransferCurve.Bt709`（α=1.099 / toe 4.5 / 拐点 0.08125，**定义式**）⇒ **这一格的像素已在标签水位，②不适用** |
  | 同上 `--color-709-curve zimg` | 39594 | `b5239a37dac7` | **与 std 逐字节相同** |
  | 显式 tonemap 格 `--color-engine engine --color-tone-map reinhard` × {std, zimg} | 34109 / 34109 | 同为 `20c2bbe13b8e` | 两跑**也逐字节相同** |
  | `--color-engine legacy --color-tone-map reinhard` × {std, zimg} | 39594 / 39594 | — | 命令行里坐实了 `zscale=pin=bt2020:tin=linear:min=gbr:p=bt709:t=bt709:m=bt709` ⇒ 编码确实是 zimg 的 γ(1/2.4) ⇒ **②只活在这条路上** |
  ⇒ 两条结论：
  ① **②的射程比我登记时窄**：默认路径已经是自研定义式编码，遗留链才是失配点 ⇒ P3 迁 `Decision.cs:263` 时**不要**顺带去动默认格（会白白改交付）。
  ② 新查出一条**口径开关的诚实性问题**（独立于 ②）：`--color-709-curve` 实测对**已走进程内**的格子完全无效（std/zimg 产物逐字节相同），
     因为它只在 `ColorTransformPlan.cs:639` 处用来**关掉 H2**；格子本来就走 H1 时这个开关没有任何作用对象。
     ⇒ 用户"选 zimg 对齐历史产物"却拿到同一样东西 = 静默无效参数（与 §4.5 那类"参数存在而不生效"同型）。
     处置方向二选一，需定：让 `Zimg` 在进程内路径也真的换成 γ(1/2.4) 曲线（=兑现承诺），或在该值落到 H1 格子时**点名播报"本格不适用此开关"**。
  ⚠ 顺带看见但**没有追**的一件事（不当作结论）：`--color-engine legacy` 那两格的 exiftool 读 cICP 返回空，而引擎格读到 `BT.709`
     ⇒ 可能是遗留链的 PNG 没走 cICP 后置（真缺口），也可能是我这条读取口径不对（同一次读法在引擎格上取到了值 ⇒ 至少"读法完全失效"不成立）。
     下批要判它：同一份产物用 `PngCicpService.HasColorChunk` 与 `ffmpeg` 两路各读一次再说。

---

## P3 像素路径清退 zimg —— **已定案：完全走自研管线**（维护者 2026-09-24 口径）

混合态（"zscale 做矩阵 + 内核只补 OETF"）**不算数**。凡改变像素的色彩操作，执行者只能是自研内核。
已核实：`TransformSpec.Tone`（`ColorKernels.cs:33`）这个挂载点**本来就在**，且 `RawColorPipeline.cs:221/291`
已经有一条 `gbrpf32le` 线性浮点通道（GainMap 在用）⇒ 缺的不是算子，是**把传统链的格子迁过去**。

### 清退清单（按"改不改像素"划界，实测行号）

| 站点 | 现在做什么 | 是否改像素 | 自研替代 | 迁移动作 |
|---|---|---|---|---|
| `FfmpegCommandBuilder.Decision.cs:263` | tonemap 链末级 `zscale=p=bt709:t=bt709` | **是** | 内核 OETF（Std 分支，`ColorKernels.cs:396-398`）+ `Tone` 挂 `ToneMapParams` | 该链改成"解码→线性 f32→内核（tonemap+矩阵+OETF）→编码"，即走 `RawColorPipeline` |
| `Decision.cs:317` | 目标空间映射 `zscale=pin/tin/p/t/m` | **是** | 内核 `Matrix` + `SrcCurve/DstCurve`（`ColorTransformPlan.ToSpec` 已会产） | 让该格 `ShouldRoute` 成立；引擎不可走时**硬失败**，不得静默回退到 zscale |
| `Decision.cs:359` / `ColorSpaceRegistry.cs:203/242/259`（`BuildUltraWide*` / `BuildGamutTransform*`，串体 `:280-281`） | 超广归一到 BT.2020（`gbrpf32le`+`colorchannelmixer`+两次 zscale） | **是** | 内核线性域 3×3 + GMO（`GamutMap*`，`:47-60`） | 同上；这三条 `Build*Filter` 迁移后**应成死码并删除** |
| `QueueProcessor.cs:2264 / :4146 / :4226` | 三条历史修补链（含 `--color-engine legacy` 的显式路径） | **是** | 同上 | 先定归属：属"用户显式选 legacy"的保留为**明示的旧行为**，属"引擎不可走时偷偷退回"的改为硬失败 |

⚠ **迁 `Decision.cs:263` 那一格之前，先解掉一条现存的"反向锁"**（2026-09-26 复核时发现）：
`ColorStrategyPlanning.cs:597-599` 写着引擎**刻意不接管 tonemap**，理由正是"引擎的 SDR 标定是 `bt709/iec61966-2-1`（sRGB 曲线），
而传统链末级是 `bt709/bt709` ⇒ 接管会改变交付"，并指向 `:126-128` 对 PNG cICP 的锁。
⇒ 两条路径对"SDR 出口该是哪条曲线"的分歧，**就是这条链至今留在 zscale 上的唯一理由**；
而上面 ② 的实测表明传统侧那个 `t=bt709` 连自己宣称的 bt709 都不是（实为纯 γ(1/2.4)）
⇒ 所以这一步的正确顺序是：**先定样"SDR 出口曲线"（bt709 定义式 or sRGB，二者都要像素与标签一致）**，
再同时改 `:597-599` 的接管判据与 `:126-128` 的交付锁 —— 反过来先迁链会立刻撞上这两处断言。
定样属**交付语义变更**，需维护者点头（选 bt709 定义式 ⇒ PNG cICP 保持 `bt709/bt709`、像素变；选 sRGB ⇒ 标签改 `iec61966-2-1`、两路一致）。
| `GainMapJpegCodec.cs:36` | `zscale=t=linear:npl=<nits>`（编解码侧线性化） | **是** | 自研 PQ/HLG EOTF（`TransferCurve.Pq`、`SimdPixelOps.PqEotfScalar`、`Bt2100Ootf`） | 线性化也归自研（否则 §3 那类"zimg 的 bt709 其实是 γ2.4"的口径差会留在**输入侧**） |
| `IccProfileService.cs:319`（`zscale … ,iccgen`） | 生成参照 ICC | 否（生成**标签**） | 自研已有 `IccProfileBuilder` | **保留 zscale/iccgen 作外部锚点**，只是不得再当生产依赖 |

### 顺序与前置（关键依赖）

1. **P1 必须先做完**：清退之后不再有"zimg 顺手把这事做了"的兜底，原色表填正是自研路径的承重墙。
2. **P4.4 从 P3 级提升为阻塞项**：`LookupEncode` 的 `clamp=false` 分支是死的（`ColorKernels.cs:363`），
   而 HDR→SDR 走自研后，线性域必须容得下 >1.0 的余量（`RawColorPipeline.cs:221` 那条 `gbrpf32le` 契约）。
   **先把这条实现对，再迁 tonemap**，否则迁移本身会制造静默削顶。
3. **每迁一格都要与现状对拍**，不许"迁完就宣布等价"：
   新链产物 vs 旧 zscale 链产物做逐像素比较，判据按算子分档——
   曲线类（OETF/EOTF）偏差上限 **1 LSB@16b**；矩阵类上限 **2 LSB@16b**（两边浮点路径不同而已）；
   tonemap 类另用既有 oracle（`verify-color-tonemap.ps1`，参照 `vf_tonemap`）证明**自研不劣于外部**，
   而不是"自研和旧的自研一致"。BT.2446 / ReinhardSegmented 至今只有自证（审计 §4.1 与记忆一致）⇒ **这两格迁移前必须先补外部锚**。
4. 每迁一格的验收包：同批重建四项目 + `-Probes decision,curve,matrix,engine,selftest` + `-Scripts verify-color-wiring,verify-decision-delivery,verify-engine-firstframe,verify-color-tonemap`；**三格全部迁完再跑一次整轮**。

### 顺带得到的收益（不是做它理由，但要记账）

- `ColorTransformPlan.cs:687` 的 `Backend=H2(zscale)` 宣称与 `FindPixelColorEntry`（`:711`，生产零调用）一起失去存在意义
  ⇒ 审计里的 D3 类"宣称无消费者"自动消解，而不是靠改文案消解。
- `--color-709-curve` 的 `Zimg` 口径（纯 γ2.4 对照）**变成纯历史对照项**：它不再有生产语义。
  撤掉它属**能力/声明降级** ⇒ 不自行处理，登记为待决策（见 §末尾）。


---

## P2.5 `--bit-depth` 是"看起来能设、其实不设"（NC2 定性后新增，P2 级）

机制已定位到两处（读码 + 实测，非推测）：

1. `ColorStrategyPlanning.cs:266-267` 决定 `p.OutBitDepth` 的式子里**只可能出现 8 或 16 两个值**：
   `Action != Map || ColorLoss == None ? 16 : (容器上限≤8 且不需 16bit 中间件 且 RgbNative ? 8 : 16)`。
   ⇒ 用户给的 10 / 12 **从来没进过这个式子**；而 `BitDepthReduced` 那条播报（`:122`）只在 `OutBitDepth == 8` 时触发
   ⇒ 所以 `--bit-depth 12` 打到 png 既不出 12 位、也不报任何降级（实测关键词命中 = 0）。
2. `CliParser.cs:573-583` 的 `case "bit-depth"` 把值收进 `options.BitDepth`（`auto` 之外走 `int.Parse`），
   **没有值域校验** ⇒ `47` 能过；`:807` 那条注释自己已经点过这个形状（「能解析但语义可疑」）。

后果分两类，都落在维护者禁止的"静默"那一侧：**值域不校验**（47 被接受）+ **可满足也不兑现**（10/12 静默变 8/16）。

**实测补强（2026-09-25，ffprobe pix_fmt：rgb24=8bit / rgb48=16bit）**：
`png` 与 `tiff` 出口下，请求 `8→rgb24 ✅`、`10→rgb48`、`12→rgb48`、`16→rgb48 ✅`、`47→rgb48`，全部 `rc=0` 且日志无位深字样。
⇒ 拆成两条不同性质的账：**PNG** 的 10/12 是"容器没有该模式"（必须播报或拒绝，属修法②的可接受档）；
**TIFF** 的 10/12 是**可达但未实现**（TIFF `BitsPerSample` 明确支持 10/12 ⇒ 容器能兑现、代码不兑现 ⇒ 纯缺陷，修法②在这里不是取舍而是漏做）。
⚠ `jxl` 那一行**不能据此判定**：ffprobe 对 JXL 报的 `pix_fmt` 是解码器输出采样格式、不等于码流存的位深 ⇒ 要判得用 `jxlinfo`（oracle 库的 `Get-JxlInfo` 给 native 位深）。

修法（两步，第二步要不要做取决于是否真要 10/12 出口）：
- **① 值域收口（必做，小）**：`--bit-depth` 只接受 `{8,10,12,16}`，其余**显式拒绝**并点名合法集合。
  配套断言（`verify-color-matrix-full.ps1` 的 CAP 尺子已就位）：非法值必须 `exit≠0 且无产物`。
- **② 让 10/12 真兑现，或明确说不支持（需点头）**：兑现 = 把 `OutBitDepth` 的式子从二值扩到四值，
  并让 `ImageEncoderArgs`（`:548-558` 一带已经在处理 10/12 的 `pix_fmt`）按它选格式；
  不支持 = 在规划层直接产出"该出口容器不支持 10/12"的**响亮拒绝**。
  ⚠ 中间档"给 16 但按 10/12 用"是**升级不是降级**，可以接受，但**必须播报**（沿用 `BitDepthReduced` 那条通道，
  或新增一个 `BitDepthRaised`），否则仍是静默改变用户请求的语义。

**full 层已跑完（2026-09-25，360 格 = 6 源 × 3 目标 × 5 容器 × 4 位深），P2.5 从"一条负控红"升级成"网格级事实"**：

| 尺子 | 读数 |
|---|---|
| CAP | **144 格 SILENT-DOWNGRADE**（0 个 CAP-FAIL、0 个不洁拒绝）⇒ 凡是"容器承载不了请求位深"的格子，产品**一律 rc=0 照样出图**，无一处拒绝、无一处播报 |
| LBL | 192 个成功格里 **62 格既读不到 CICP 也读不到 ICC** ⇒ 候选交付缺陷，**未定性**（要先排除我的 icc 字段取法；jxl/avif 走 ICC 时 `Get-ColorMetadata` 的 ICC 描述可能不在我读的那个字段上） |
| NUM | **120 格与 zimg 参照全部 ≥60 dB、0 红** ⇒ 能出图的那些格子里，矩阵/曲线的**数值**是对的 |
| RT | 9 组往返全绿（独立于任何外部实现，排掉转置与色适应顺序错） |
| NC1/NC1b | 绿（PSNR 22.75 ⇒ 尺子有牙） |
| NC2 | 红（47 被接受）⇒ 与 CAP 那 144 格是**同一个根因**：`OutBitDepth` 只产 {8,16} + 无值域校验 |

⇒ 修法优先级因此提高：**CAP 的 144 格不是"缺测试"，是缺一次校验**。落 ① 值域收口 + ② TIFF 真兑现 10/12 + ③ 不能兑现时走 `BitDepthReduced` 播报或拒绝，之后 CAP 应当从 144 掉到 0；LBL 那 62 格另立一条归因任务，**别和位深这件事混成一个**。

**LBL 那 62 格已归因（2026-09-25，抽样 exiftool 实测）——两半，性质不同**：
- **TIFF 一半是我的 harness 错**：`m_srgb8_sRGB_tiff8` 与 `m_srgb8_BT2020_tiff16` 都带 ICC
  （`ICC_Profile:ProfileDescription = "RGB built-in"`、`BitsPerSample = 8 8 8 / 16 16 16`），
  而我的判据只读 `Get-ColorMetadata` 的 `.Primaries`/`.IccDescription` ⇒ 读错了字段就判成"无标注"。
  ⚠ 注意"能证明**有** ICC"不等于"能证明是**哪个** ICC"——`"RGB built-in"` 这种描述文字按本仓既有结论**不能**当色域判据，
  要判空间得读 rXYZ/gXYZ/bXYZ 色度算出来。
- ~~**webp 一半是候选真缺陷**~~ ⇒ **已被 Lane E 实测否定，我那条是取数错**：Lane E 用 `exiftool -j -G1 -ICC_Profile` 读到取证格
  `m_srgb8_BT2020_webp8/base8.webp` **带 ICC**（`RGB built-in` 584 B，色度就是 BT.2020 的红列 0.67348/0.27904、白点 0.9642/1/0.8249，
  且与同格 png/tiff 内嵌 ICC 的 md5 一致）；我那次跑的是 `-j -G1 -ICC_Profile -ProfileDescription ...` 一整串旗标 ⇒ 取空当作"没有"。
  **又一次「缺席结论必须由同码子在别处抓到东西来背书」**（本仓第 N 次踩同型：`icc/ffprobe 取数污染` 那批教训的又一例）。
  72 个 webp 里 48 个（BT.2020 / P3 目标）**全部带 ICC**，24 个无标注的**恰好都是 sRGB 目标** ⇒ 那是**有声明、有播报**的出口语义
  （能力表 `ColorTransformPlan.cs:491-493` + 降级码 `ColorStrategyPlanning.cs:164-167`，日志会打
  `⚠️ 降级（UnlabeledAssumedSrgb）：产物无色彩标注…` 与 `裁决=ProceedWithDegradation`）⇒ **不是缺陷**。
- ✅ **原「Lane E 留了一条未验的真风险」— 已证实是真缺陷，并已修复 + 上锁（2026-09-26）**。
  复现（`qoder-scratch/lblrisk*.ps1`，源 = 挂 Display P3 ICC 的 PNG，`--color-strategy carry --color-space BT.2020`）：
  | 出口 | 修复前 | 修复后 |
  |---|---|---|
  | webp（legacy 通路） | 产物 **96 B、ICC 0 B**、裁决 `Proceed` | **706 B、ICC 584 B**（= 源那张） |
  | webp（engine 通路） | 同上（96 B / 0 B） | 同上（706 B / 584 B） |
  | png / jpg | 本来就带 ICC | 不变（704 B / 873 B，字节数与色度均未动） |
  根因是**两条各自合理的排除叠加**出的空档：`QueueProcessor.cs:1612` 的 `!userSpecifiedColor`（"别覆盖用户选的空间"）
  + P7a 后置的 `Action != CarryIcc`（"携带的 ICC 由元数据恢复负责"）⇒ webp 编码器丢 ICC 时**没人负责嵌入**，
  而日志仍写「跳过源文件 ICC Profile 恢复」+ 裁决 `Proceed` ⇒ 宣称与产物相反。
  修法是**按能力表**收口（`CapsFor(fmt).EncoderWritesGeneratedIcc=false` 才补带源 ICC），不是按格式名 if-else；
  像素判据用 `EffectiveColorStrategy==CarryIcc`（生效值，不是声明值），所以 S2 被 GainMap 之类覆盖后不会误回带。
  隔离验证：把补上的 ICC 用 exiftool 剥掉 ⇒ 文件回到 **96 B** 且解码像素 md5 与带 ICC 时**逐字节相同**
  ⇒ 本改动只动元数据、不动像素。
  回归锁：`verify-decision-delivery.ps1` 新增 ⑭（8 条，51 → 59），判据是"产物 ICC 的红原色 == **源** ICC 的红原色"
  （两边同一个 helper 现读，不写死标准值）+ 两条前置挡住真空（夹具自己读得到 ICC、且确实是非 sRGB 广色域）。
  ⚠ 已验牙：把条件改回 `&& !userSpecifiedColor` 重建 ⇒ ⑭ 的 **4 条转红**（两条"读不到 ICC" + 两条"仍写跳过"），
  前置两条仍绿（它们量的是夹具不是产物）⇒ 红的正是行为本身。还原后 `sha256=a1a968a419bbec38…` 与变异前逐字节一致。

## P2.5 收口（2026-09-26 晚）：升降档播报已修，另开 #36 / #37 两笔

**已修并整轮绑定（`9fb25bf7770e7abe`，整轮 54 条「全部通过」+ 全层网格 `PASS=19 FAIL=0`）**：
`--bit-depth` 在**非 RGB 原生容器**上被钳掉后零播报（full 层 71 格真静默：webp 54 / avif 17）。
根因**不在**判据，而在**证据**：请求档在进规划层之前就被 `Decision.cs` 的 capBd 就地改写与
`ColorIntentFactory.cs:145` 的 `Math.Min` 连钳两次 ⇒ ①b 永远看不到"用户要 10"。
修法（不动任何判据、不新增 UI 文案）：
`FfmpegOptions.BitDepthRequested`（钳制**前**记录，只记更深那次）→ `ColorIntent.RequestedBitDepth`
→ `CollectDegradations` ①b 新增**非 RgbNative 分支**，复用既有 `BitDepthReduced` 码。
本层交付档的**唯一事实源**是 `PlanPolicy.TargetBitDepth`（= 两处钳制之后的值，也正是
`EffectiveBitDepth → MapPixFmt` 的输入）⇒ 网格 §2a 拿产物 `pix_fmt` 实测对账，**播报与矛盾 0 格**。
复跑读数：`strategy 70/1 → 71/0`（F3 转绿，红的成因改由 F0a–F0e 合成自测 + F2 真日志负控来咬）·
网格真静默 **71 → 0**、承诺账 **0**、LBL **0**、NUM 96 格 0 红、RT 9 组 0 红。

**新开 #36｜AVIF 12bit**：承诺账当场抓出 12 格 `avif 12 ⇒ 交付 10 并播报`。查清是**顺序问题**：
`AvifMaxBitDepth` 在 `o.Encoder` 仍为空（auto）时按"候选编码器交集"取 10（`libsvtav1` 只到 10），
而那 12 格实际命令行落在 ffmpeg 默认的 **`libaom-av1`**（可到 12）⇒ **钳制发生在编码器已知之前**。
产品现在不静默（已播报），所以本批只把裁判前提按"本路径从未承诺 12"收窄（`$capDepths.avif ⇒ @(8,10)`）；
抬上限要动 avif 位深/体积/PSNR 基线 ⇒ 单独一批。

**#37 已实现（2026-09-26 晚）**：24 格 `jxl + BT.2020` 的拒产**已改为交付**。
**真凶不是能力位**（我第一版推断错了，登记在此）：`jxl.CanGenerateIccFromCicp=false` 在所有消费者处都
与 `CanCarryArbitraryIcc=true` 是**或**关系 ⇒ 翻它一格都不改变。真正的拦截点是
`ColorStrategyPlanning.EnforceContainerAnnotationRules` 的**择一**：容器不能同时带 ICC 与 CICP 时，
它只要看到"ICC 是生成的（`IccPath==null`）"就**剥 ICC 留 CICP** —— 而 `bt2020/bt709` 恰好
**落不进 cjxl 的 `color_space` 枚举** ⇒ 留下一个出口写不出的标注、剥掉唯一能兑现的 ICC ⇒ 执行体只能抛。
修法 = 择一时先问"**这条 CICP 本次出口写不写得下**"，写不下就保留生成 ICC。判据仍是同一个
`CapsAcceptsCicp`，不另立枚举表。
⚠⚠ **两次实测把自己打回来**（都登记在此，免得被当成"顺手就对了"）：
 ① **必须按出口判**：`CicpSpaceEnums` 钉的是 **cjxl** 的枚举宽度，ffmpeg 的 libjxl 出口能写任意 CICP
   （`verify-color-wiring` (10) 的交叉证明格实测）。无条件按枚举剥 CICP ⇒ 那条本来能交付的路
   改成"附一张 ffmpeg 出口写不进的 ICC" ⇒ **产物 0 字节**（`RawColorPipeline` 的 JXL 附 ICC 诚实门抛）。
 ② **不能读 `p.ExternalEncoderExit`**：那个位是 `Plan()` 在 `PlanWithPolicy` **返回之后**才装配的
   （`ColorStrategyPlanning.cs:40`），在择一函数里恒为 false ⇒ 第一版据此写的分支**静默失效**，
   cjxl 侧三条断言同时转红才暴露。正解是把出口事实**作参数传进来**（`pol.ExternalEncoderExit`）。
验收（实测）：`verify-color-wiring` (10) 段重写为钉**新契约**四条 —— 交付、jxlinfo 读回
「Rec.2100 primaries + 709 transfer」、命令行点名 `-x icc_pathname=`、**旧拒绝文案不得复现**
⇒ **105/0**；`caps 12/0`；整轮 54 条 **「全部通过」（exit=0）**，绑定二进制 **`ab21aa4f89688bef`**
（四份一致，19:0x 四项目 `-t:Rebuild`）。
⚠ 顺带一条裁判词形坑（同族第二次）：LBL 的 BT.2020 式只认 `BT.2020/BT.2100`，而 **jxlinfo 写的是
`Rec.2100 primaries`** ⇒ 4 格交付成功的产物被自己的尺子判成"静默无标注"；已按实测词形加
`Rec\.?\s?2100`（**只扩词形不扩语义**），并先把离线复算与脚本读数对齐（4 格逐格同名）才动正则。
⚠ **仍未清**：`FormatColorCaps` 没有"后端"这一维 ⇒ 现在靠调用方把出口标志递进来；能力表侧的
根治（后端维度 / 或按出口分表）另批，登记在任务里。

## P2.7 SDR 出口定样（维护者 2026-09-26 = A）＋ 每容器标签账 ＋ 各源空间→Rec.709 矩阵值

**定样**：按 **A（声明与像素必须一致，不许拿标签冒充）** 执行；落到**图像容器上即 sRGB（`iec61966-2-1`）**。
`bt709` 三元组只保留给**有规范 CICP 通道的容器**（AVIF 的 `colr nclx`）与 YUV 出口的 `-colorspace`。
依据不是偏好，而是"这条标签在该容器里到底有没有人读"（下表右列为实测或能力表 `VerifiedBy` 的取证口径）：

| 容器 | 写 ICC | 写 CICP | 未标注即 sRGB | ICC 与 CICP 共存 | 取证 |
|---|---|---|---|---|---|
| png / apng | ✅ iCCP | ✅ cICP（PNG 3.0） | ✅ | ✅（优先级 cICP>iCCP>sRGB>cHRM/gAMA） | 实测产物字节层 cICP=`1/1/0`（+fullrange=1）；引擎格另有 584B sRGB iCCP ⇒ 见 #31 |
| jpg / jpeg / jpegli | ✅ APP2 | ❌ **无 CICP 通道** | ✅ | 不适用 | 能力表 `CanCicp` 未置真，`VerifiedBy=exiftool APP2` |
| webp | ✅（**只能后置**，libwebp 丢弃 iccgen 的 ICC） | ❌ | ✅ | 不适用 | `EncoderWritesGeneratedIcc=false` 实测 |
| tiff / tif | ✅（须在元数据恢复之后） | ❌ **非规范** | ❌（查看器行为不一） | 不适用 | `NoCicpReason=ContainerNonNormative` |
| jxl | ✅（cjxl `-x icc_pathname`；exiftool 写不进） | ✅ 枚举仅 sRGB/DisplayP3/2100PQ/HLG | ❌ | ❌ **ICC 胜出** | E2 实测：同给 `color_space`+`icc_pathname` ⇒ 与"仅 ICC"**逐字节相同** |
| avif | ✅（`--icc`；exiftool 不能后置写） | ✅ `--nclx P/T/M` | ❌ | ✅ | E1 实测；⚠ `--lossless` 下 avifenc 强制 matrix=identity(0) |
| heic / heif | ❌ 无写入通路 | ❌ | — | — | 全保守能力（无 heif muxer / 无 heifenc） |
| jxr / gif / ppm | ❌ | ❌ | — | — | 无 ICC/CICP 写通路（caps 门禁点名 SKIP 的成因） |

⇒ **结论**：图像侧唯一"到处都生效"的描述是 **ICC**。给 jpg/webp/tiff 写 `bt709` 是写进一个不存在的字段；
给 jxl 写枚举会被 ICC 覆盖。所以 A 在图像容器上的实现只能是 **sRGB ICC**。
matrix 字段维持现状：RGB 原生容器实测写 `matrix=0`（identity/GBR）是**对的** —— 它们不该声明 Y′C′B′C′ 系数。
迁移动作 = 把 `ColorStrategyPlanning.cs:865`（twin 顺序本就 sRGB 优先）与遗留 tonemap 链出口对齐，
并同步 `:597-599` 的"引擎不接管 tonemap"与 `:126-128` 的 bt709/bt709 交付锁；连带重录 HDR→SDR 的 PSNR 类基线。

**各源空间 → Rec.709 的线性域 3×3**（行主序；`ServiceProbe colormath` 现场打印的本仓值 vs 锚 A(zimg) 与 maxErr，非凭记忆）：

| 源空间 | 本仓 3×3（行主序） | maxErr | 档 |
|---|---|---|---|
| bt470m | 1.4860 −0.4035 −0.0825 / −0.0251 0.9542 0.0709 / −0.0272 −0.0442 1.0714 | 1.76e-4 | B（Illuminant C 白点位数） |
| bt470bg | 1.0440 −0.0440 0 / 0 1 0 / 0 0.0118 0.9882 | 3.38e-6 | A |
| smpte170m | 0.9395 0.0502 0.0103 / 0.0178 0.9658 0.0164 / −0.0016 −0.0044 1.0060 | 4.91e-6 | A |
| smpte240m | 同上（**与 170m 同原色 SMPTE-C**，差异只在传递） | 4.91e-6 | A（白名单 `{170m≡240m}`） |
| bt2020 | 1.6605 −0.5876 −0.0728 / −0.1246 1.1329 −0.0083 / −0.0182 −0.1006 1.1187 | 1.14e-6 | A |
| smpte431 | 1.1575 −0.1550 −0.0025 / −0.0415 1.0456 −0.0041 / −0.0181 −0.0783 1.0964 | 2.36e-4 | B（影院白 0.314,0.351） |
| smpte432 | 1.2249 −0.2249 0 / −0.0421 1.0421 0 / −0.0196 −0.0786 1.0983 | 3.95e-6 | A |
| jedec-p22 | 1.0253 −0.0265 0.0013 / 0.0194 0.9480 0.0326 / −0.0018 −0.0014 1.0032 | 4.92e-6 | A |
| film / smpte428 | **不收录** ⇒ fail-closed（film=精度不足；428 的 XYZ 三角原色无法用 xy 表达），不猜 | — | — |

（A 档 1e-5 = 锚读数舍入地板 5e-6；B 档 5e-4 = 白点位数上限；收紧 B 档须先按权威源取更高精度常数再同锚复测。）

✅ **#30 已证否，并据此更正本文件上方那段"没有追"的写法**：遗留链 PNG **确实写了 cICP**
（字节层 `primaries=1 transfer=1 matrix=0 fullrange=1`），我先前"读不到标注"是 exiftool 的
`-ColorTransferCharacteristics` 旗标名不命中 PNG cICP 的 transfer 字段（同一次读法只回吐了 primaries 一项）
⇒ 又是"缺席结论没带正对照"。正向对照 = 字节层读块 + ffprobe 读到 `color_primaries=bt709`。
真正的疑点改立为 **#31**：cICP 与 iCCP（以及 gAMA/cHRM）并存的产物违反本仓自定口径。

---

## #31 PNG 的 cICP 与 iCCP / gAMA+cHRM 并存 —— 拆成两条，(ii) 已修（2026-09-26 晚）

**取证（不采信那条 lane 的报告，自己在当前树上重跑）**：脚本与块清单见
`tests/output/_diag_31cur/`（`run_cells.ps1` = HDR→8bit PNG 三格，`run_sdr_cells.ps1` = SDR 源三格，
`chunks.py` = 独立块遍历）。修复**前**实测：

| 格 | 产物块序 | cICP | 与它并存的低优先级块 |
|---|---|---|---|
| 引擎 + HDR→SDR | `IHDR cICP pHYs iCCP IDAT…` | `1/1/0/1` | iCCP（sRGB 那张，584B/363B） |
| 默认(auto) + HDR→SDR | `IHDR pHYs cICP cHRM gAMA IDAT…` | `1/1/0/1` | **gAMA=45455(γ2.2) + cHRM** |
| SDR 源 + `--color-space sRGB` | `IHDR cICP pHYs sRGB cHRM gAMA IDAT…` | `1/13/0/1` | **sRGB + cHRM + gAMA(γ2.2)** |

⇒ 两个**互相独立**的缺陷，代价与修法不同，必须分开：

* **(ii) 并存本身**：写了 cICP 还留着被它取代的 `gAMA/cHRM/sRGB`，且 `gAMA=1/2.2` 与
  `cICP.transfer=13`（sRGB 分段）数值上就是两条不同曲线 ⇒ 认 cICP 的读者与只认 gAMA 的老读者
  解出不同结果。**本仓口径早已写明"优先级 cICP>iCCP>sRGB>cHRM/gAMA""写 cICP 消除 gAMA"**
  （`PngCicpService.cs:134/:161`），此前只落在注释、没落在代码。
  ✅ **已修**：新增 `PngCicpService.TryStripChunksSupersededByCicp`，调用点放在 `[png3]` 分支**之外**
  （实测默认格的 cICP 由**源文件带过来**（`src_hdr_pq.png` 自身含 `cICP=09 10 00 01`）、生产日志里
  没有任何 `[png3]` 行 ⇒ 只在"本服务写了 cICP"那一支剥离必漏）。iCCP **不在剥离名单**（PNG 允许并存，
  且定样要 sRGB ICC；两者一致时并存是有意双描述）。
  新门禁 `verify-png3-interop.ps1` ⑥ 段 7 条：前置 cICP 在位 + `gAMA`/`cHRM`/`sRGB` 三条**分开**判
  （合计一条会在只剩一块时也绿）+ 剥离动作必须在日志可见 + **两条负控**。实测 **16/0**。
* **(i) 出口曲线与标签不符**：HDR→SDR 链末级 `zscale=…t=bt709`，而 zimg 把 `t=bt709` 实现成
  **纯 γ(1/2.4)**；产物标签写 `cICP.transfer=1`（BT.709 定义式是**分段**）。CICP 里**没有**
  "纯 γ2.4"这个编号 ⇒ 不能靠改标签收口 ⇒ 属任务 #26②（与 P3「像素路径清退 zimg」同批，
  要动交付、需 PSNR 重基线）。**本轮不碰**。

⚠⚠ **本轮挖出的门禁侧真 bug（比修复本身更值得记）**：PowerShell 的移位**保留左操作数类型**，
`[byte]0x10 -shl 8` = **0**（当场实测），而 `[int]$b -shl 8` = 4096。⇒ 本仓三处
PNG/像素遍历里的 `[int](($b[$pos] -shl 24) -bor …)` **一直**把 IDAT 长度读成 0，
遍历从第一个大 IDAT 起就失步（`e6_mutated.png` 实测：PS 报 205 块/200 错，独立实现报 13 块/0 错）。
受影响并已修：`verify-png3-interop.ps1`（`Dump-Png` + `Get-CrcBad` 两处）、
`_probe-png-cicp-production.ps1`、`_probe-zimg-noop-check.ps1`（16-bit 样本求和同族）。
⇒ 后果要说准：**"产物块序里没有 gAMA"这类断言在修之前是恒真的**（它压根没走到 IDAT 之后的块，
而冲突块本来就在前面 ⇒ 结论恰好还对，但判据没有牙）。把它抓住的正是我这次新加的**负控夹具** ——
第一版夹具是 PS 手工拼块，反而暴露了遍历自己坏了；换成仓库里跟踪的
`tests/fixtures/png_cicp_conflicting_gama.png`（`zlib.crc32` 生成、`ffmpeg -v error` 解码零告警，
断言里同时钉"夹具 CRC 全对"与"读法能同时抓到 cICP+gAMA"）。
教训：**凡是"某样东西不在输出里"的断言，必须配一条"同一样东西在夹具里、读法必须抓到它"的负控**；
没有它，遍历失步/类型截断都会伪装成绿色。

## P2-② 收口（2026-09-26 晚）：tonemap 链的 SDR 出口改成 sRGB，像素提到标签水位

**改动一处默认值**：`FfmpegCommandBuilder.Decision.cs` 的 tonemap 链末级
`var tmDstT = "bt709"` ⇒ **`"iec61966-2-1"`**（`capT`（TIFF/JPEG/WebP 的 SDR 硬钳）与用户显式目标仍优先，
语义未动）。改完实测（`tests/output/_diag_31cur/`）：三格（engine/auto/legacy）命令行链末一律
`t=iec61966-2-1`，PNG 标签一律 `cICP transfer=13`，ffprobe 回读 `gbr/bt709/iec61966-2-1`。

**为什么是改像素而不是改标签**（维护者 2026-09-26 定样 A「声明与像素必须一致」+「图像文件选 sRGB」）：
zimg 把 `t=bt709` 实现成**纯 γ(1/2.4)**，而 `cICP.transfer=1` 声称的是 BT.709 **定义式**（分段，
线性段 0.018/4.5，与 sRGB 的 0.04045/12.92 不同）；**H.273 里没有"纯 γ2.4"这个编号**
⇒ 标签侧无解，只能把像素抬到某条**有编号且真实现**的曲线。选 sRGB 的三条理由都落在实现上：
zimg 真有该分段（本仓登记过"有 sRGB 分段、无 BT.709 分段"）、CICP=13 兑现得了、
引擎侧 `SdrTwinOf` 的 SDR 候选表**本来就把它排第一** ⇒ 两条管线在此收敛
（修复前实测：引擎像素=sRGB 而标签=bt709，legacy 像素=γ2.4 标签=bt709 —— **两边各错一半**）。

**改交付的代价（量化，不许含糊）**：HDR→8bit PNG 新旧产物 **PSNR ≈35.5 dB**（r36.4/g34.8/b35.3，
γ2.4→sRGB 的中间调位移；默认/legacy 两格字节数 39534 → 42676）。
⚠ **代价之外暴露的是覆盖缺口**：这么大的交付变化，**整轮 54 条改前改后都全绿**
⇒ 此前没有任何门禁钉过这条链的出口曲线。补上的锁 = `verify-decision-delivery.ps1` **⑮**（59/0 → **63/0**）：
① 链末解析出的 `t=` 必须 == 产物回读的 `color_transfer`（**两侧都从现场取**，不硬编码比字符串）；
② 且必须等于 `iec61966-2-1`（否则"两边一致地停在 bt709"这种一致的错也能过）；
③④ 负控：用**生产写块器**（`ServiceProbe png3 … 1 1`）把标签改成 cICP=1 ⇒ 判据**必须**失配，
并自证改写后确实读回 bt709（排除"读不到"冒充"抓到"）。
⚠ 写这把锁的过程中自己踩到一条：**ffprobe 的 `-of csv` 不按 `-show_entries` 的请求顺序输出**
（请求 `color_primaries,color_transfer` 实得 `iec61966-2-1,bt709`）⇒ 位置索引把 primaries 当成 transfer 用，
第一版因此把**正确的产物判成失配**。`_lib-oracle.ps1` 用 JSON 具名字段正是躲这个；新增判据一律按字段名取。

**顺带失效的一条旧理由**：`PlanRecommended` 里"HDR→PNG(8bit) 显式交回传统管线"原本靠
"引擎出口(sRGB) ≠ 传统出口(bt709/bt709)"成立；现在两者相同 ⇒ 这条交回**只剩路径选择**，
不再是正确性要求 ⇒ 注释已改写，P3「像素路径清退 zimg」落地时连同它一起撤。

## #36 收口（2026-09-26 深夜）：AVIF 位深上限改由「实际会跑的编码器」推

**事实**（取证 `tests/output/t36/run_cells.ps1` + `probe_encoders.ps1`，日志与产物同目录）：

| 项 | 读数 |
|---|---|
| 随包 ffmpeg 对 `.avif` **不写 `-c:v`** 时选的编码器 | `libaom-av1`（映射行原文 `-> av1 (libaom-av1)`）|
| `libaom-av1` 自报像素格式 | 含 `yuv420p12le / yuv422p12le / yuv444p12le / gbrp12le` ⇒ 上限 **12** |
| `libsvtav1` / `av1_qsv` / `av1_amf` 自报 | 只到 `yuv420p10le` / `p010le` ⇒ **10**（旧表这三行本来就对）|
| `av1_nvenc` 自报 | 含 `p012le / p212le / yuv444p12msble` ⇒ 有 12bit 的格式（但**名字对不上** `MapPixFmt` 给的 `yuv420p12le` ⇒ 单列待办，本机无 NVIDIA GPU 验不了）|
| 独立参照（不经产品，直接喂 ffmpeg）| `-c:v libaom-av1 -pix_fmt yuv420p12le` ⇒ rc=0、产物 `pix_fmt=yuv420p12le` |
| 修复前产品 `--format avif --bit-depth 12` | 交付 `yuv420p10le`，播报「本工具对该容器实测可交付的上限 = 10bit」⇒ **那句是假的** |
| 修复后同一命令 | 交付 `yuv420p12le`、零 `BitDepth*` 码、命令行仍**不含** `-c:v` |

**根因是同一个类里相隔 350 行的两条口径**：`ClassifyAvifBackend` 早就写明「空编码器名按 `Libaom` 处理
（不给 `-c:v` 时 ffmpeg 为 avif 选的默认编码器就是 libaom-av1）」，而 `AvifMaxBitDepth` 用
`options.Encoder ?? ""` 做前缀匹配 ⇒ 空串落 else 支得 10。CLI 侧**没有任何入口**能写 `options.Encoder`
（`--encoder` 映射到 `EncoderBackend` 枚举，不是编码器名）⇒ 12bit 在命令行上**永久不可达**；
UI 侧还有第三份副本（`MainWindow` 的 `(isLibaom || isNvenc) ? 12 : 10`），同样漏掉「留空」这一支
⇒ 编码器留空时下拉只给到 10，而那一格实跑 libaom。

**修法 = 把实现提到声明的水位**（不动判据）：拆出
`AvifMaxBitDepthForEncoder(string?) => ClassifyAvifBackend(...) switch { Libaom or Nvenc => 12, _ => 10 }`，
`AvifMaxBitDepth` 只转发，`MainWindow` 的位深下拉改调它 ⇒ 全仓只剩一条「编码器名 → 上限」的规则，
`FormatColorCaps["avif"].MaxBitDepth = 12` 这条**声明**第一次被实现追上。
⚠ 副作用如实登记：**auto（用户没给位深）+ 源位深 > 10** 的 AVIF 出口从 10bit 变 12bit ——
这不是顺带送的功能，是同一条规则改完的必然结果（旧值 10 本来就是那个错常数的副产品）；
交付基线由全层网格重测。取证格里同输入的体积变化 2861 → 2858 B（`--crf 16`，256×256）。

**锁**（`verify-decision-delivery` 新增 ⑯，**63/0 → 73/0**）——三把尺子 + 结构锁，全部现场取数：
① 裁判**自己**解析 `ffmpeg -h encoder=` 的自报表推上限，并从一条**不写 `-c:v`** 的真实编码日志里
读「默认到底是哪一个」；`AvifMaxBitDepthForEncoder` 对任意编码器名都能算，但 ⑯ 只对**两行**下判断 ——
默认编码器（现测 `libaom-av1` ⇒ 12）与 `libsvtav1`（现测 10），后者只为证明尺子能区分而存在
⇒ 随包构建哪天换了默认编码器，本段先红在前提那条，不静默跟着变（nvenc/qsv 两行本机无从实测，
已按 #40 单独登记，⑯ 不替它们背书）；
② 产品交付：`--bit-depth 12` 的 `[cmd]` 必须含 `-pix_fmt yuv420p12le`、**不含** `-c:v`、产物回读 12bit、
日志不得再出现 `BitDepth*`；
③ 钳制仍活着：`--bit-depth 16` 的交付档必须 == ① 的实测上限，且**既不是 16 也不是 10**
（16 ⇒ 有人把钳制整个删了，libaom 无 16bit YUV 会编不出；10 ⇒ 又退回「编码器未知」的老口径），
并点名 `BitDepthReduced(16→12)`；
④ 尺子自证：`libsvtav1`(实得 10) 必须与默认编码器(实得 12) 被解析器**区分开** —— 两边数一样时 ② 就是恒等式；
⑤ 结构锁：上限只能由 `ClassifyAvifBackend` 推导，且 `MainWindow` 不得再出现 `? 12 : 10` 副本
（⚠ 匹配前**先剥整行注释**：不剥的话，解释这次修复的注释自己就会把门禁判红 —— 本轮实测踩过）。

**网格侧撤掉那笔收窄**：`$capDepths.avif` 由 `@(8,10)` 回到 `@(8,10,12)` —— 原收窄理由「本路径从未承诺
12」在修复后不再成立；承诺账从此**要求** avif 12 的 18 格（6 源 × 3 目标）真交付 12bit，若被钳回 10 会以
`reduced-announced` 落进承诺账而不是溜过去。
⚠ **UI 那半边只有结构锁、没有行为锁**：⑯ 的结构锁只保证 `MainWindow` 不再自带 `? 12 : 10` 副本，
**没有**断言"编码器留空时下拉真的给出 12"。下一批可在 `UiTestHost` 补一条：格式=AVIF + 编码器=auto
⇒ `BitDepthCombo.Items` 必须含 `12`（读**真实 items**，不读推断值）。
读数：整轮与全层网格见 `docs/HANDOVER_2026-09-22.md` 最近一轮那一行（绑定二进制指纹，不在此复写）。

## #39 + #41 收口（2026-09-26 深夜）：撤掉"AVIF+P3 高位深 ⇒ 8bit"这条无据特例，并补上让它能溜过去的尺子

**先记一笔我自己的假陈述**：第一版把网格读数写成"产品**静默**把 10/12 削成 8（规划层 ①b 那条去重条件吞掉播报）"。
错了 —— 那是只看了 CSV 首行（bd=8 那格确实 `announced=False`，因为请求==交付本来不该播）就外推。
单格实跑取证（`tests/output/t36/attribute_41.ps1`，engine/auto/legacy × 8/16bit 源共 6 格）**每一格都播了**
`降级（BitDepthReduced）：出口位深降为 8bit（本次目标 12bit）`，CSV 里这些格 `announced=True` ⇒ 产品这半边诚实。

**#41 = 尺子的洞（已补）**：`verify-color-matrix-full.ps1` 的 `$pixDepth` 只有 `yuv420p10le/12le/16le`，
**没有 8bit YUV 那一族**（`yuv420p / yuv422p / yuv444p / yuva420p / yuvj420p`）⇒ `Get-DeliveredDepth` 对这些回 `$null`
⇒ "播报 vs 实测"两条 MISANNOUNCED 判据与**承诺账**在 avif/webp 上从不执行。
实测规模（补词前后对账）：全表 `announced=True 且 delivered 空` = **avif 18 格 + webp 54 格**
（取证 `tests/output/t36/check_announced.ps1`）。⚠ 该脚本上方旧注释把 `got=?` 归因成"webp/avif 容器给不出位深"，
**那是把尺子缺词误记成容器的性质** —— ffprobe 一直给得出 `pix_fmt`，只是本表认不出来。
补词后重跑全层网格：**承诺账当场抓出 12 格**（`avif × Display P3` 的 bd=10/12 × 6 个源），
读数 `CAP承诺? … 能力表说支持、产品却交付了 8bit` ⇒ `PASS=18 FAIL=1`（`tests/output/t36/grid-t36-run3.log`）。
⇒ 顺序是刻意的：**先只补尺子拿到红**，再动产品；红不许用改表或放宽账目来消。

**撤档后的 A/B 收尾读数**（`tests/output/t36/grid-t36-run4.csv`，按 `fmt=avif` 逐档分组实测）：

| 请求 | 格数 | verdict | 实测交付档 |
|---|---|---|---|
| avif 8 | 18 | 全 `ok` | **8** |
| avif 10 | 18 | 全 `ok` | **10** |
| avif 12 | 18 | 全 `ok` | **12** |
| avif 16 | 18 | 全 `reduced-announced` | **12**（钳制仍活着） |

⇒ 整轮 `PASS=19 FAIL=0 cells=360`、承诺账 **0**（run3 的那 12 格红全部消失，且不是靠改表：
表的档位没动、判据没放宽，是产品把请求的档真交付了）。

**#39 = 那条特例本身（已删）**：理由只是注释里"Display P3 + 10/12/16-bit 在 libaom 上有回归风险（保守策略）"，
全仓**没有任何实测记录**。决定性取证（`tests/output/t36/probe_39b.ps1`）拿**产品自己那条** avif+P3 命令行
逐字复用（含 `zscale=…p=smpte432:t=iec61966-2-1:m=bt709,iccgen -crf 16`）、**只换 `-pix_fmt`**：

| `-pix_fmt` | rc | 字节 | ffprobe 回读 | PSNR（对 8bit 产物）| stderr |
|---|---|---|---|---|---|
| `yuv420p`（产品现状）| 0 | 5505 | `primaries=smpte432 / trc=iec61966-2-1 / matrix=bt709` | — | 无 |
| `yuv420p10le` | 0 | 5595 | **同一套标签** | **51.45 dB** | 无 |
| `yuv420p12le` | 0 | 5435 | **同一套标签** | **50.99 dB** | 无 |

⇒ "回归"不可复现：三档都编得出来、nclx 标签都落得进去、像素与 8bit 档同源。
⚠ 还量到这条特例**连自己声明的都没做到**：它写"回退 sRGB"，实际标签仍是 P3 —— nclx 来自 zscale 写进帧的
色彩属性，而特例给的 `capP/capT` 只在 tonemap 链（`tmDstP = capP ?? tmDstP`）被消费 ⇒ 真正咬人的只有
`capBd=8` 那一项，即"无据削掉用户明确要的位深"。⇒ 删除，avif 分支统一回 `AvifMaxBitDepth`（#36 那条唯一真值）。

**镜像断言一起改（这才是这批改动的真实工作量）**：
· `tests/scripts/_lib-matrix.ps1` 的折叠类 **F5 `bit-depth-avifp3-8`**（"8/10/12/16 四值等价于 8"）整条删除
  ⇒ 覆盖账是**变多**：avif+Display P3 的 bit-depth 从"1 格代表 4 档"变成 4 档各跑；
· 同文件的 L3 矩阵 **`bitdepth-container`** 的 Rationale/Evidence 也钉着那条已删的行 ⇒ 同步改写，
  并把取证换到 `AvifMaxBitDepthForEncoder` / avif 分支 / `options.BitDepth = capBd` 三处现行行；
· ⚠⚠ 顺手量到一笔**静默腐烂**：`_lib-matrix.ps1` 的行锚定取证**在本次改动前就有 6/19 条失效**
  （`Decision.cs:147` 的整行式已被 #35 拆成块、`:867/:877/:486/:1871` 全部行号漂移），
  而校它的 `verify-oracle-matrix.ps1`（5010 例 ≈2.5 h）**有意不在 54 条清单里** ⇒ 没人跑 ⇒ 腐烂无人知。
  已全部修到 21/21 通过（检查器 `tests/output/t39/check_matrix_evidence.ps1`，只读源文件、秒级），
  并把散文里的行号复写去掉（**行号只许写在 Evidence 字段里**，漂了 A21/A30 会响）。
· `verify-color-strategy` ③f 从"只要求不静默"升级为新契约：**F4** `avif + P3 + 12` ⇒ 必须真交付 12bit
  （出口侧 `-pix_fmt` 取日志**最后一个** `-pix_fmt` —— 引擎路线前面那条 `rgb48le` 是中间件格式，
  取第一个会把正确产物判红，本轮踩过）；**F5** `avif + P3 + 16` ⇒ 交付必须 == 编码器上限 12、
  恰好播一次 `BitDepthReduced(16→12)` ⇒ ③f **71/0 → 77/0**。

**#39 撤档带来的另一条交付变化（HDR 源这一路网格量不到）**：被删的那支返回过 `capP="bt709"/capT="iec61966-2-1"`，
而 tonemap 链里写的是 `tmDstP = capP ?? target.primaries`（`Decision.cs:268`）⇒ **HDR 源 + 显式 P3 目标时，
用户要的 P3 会被 capP 静默换成 bt709**。全层网格的 6 个源全是 SDR ⇒ 这条路它测不到，于是单独量了
（`tests/output/t39/hdr_p3_check.ps1`）：默认/引擎格 `exit=0`、产物 5855 B、
ffprobe 回读 `pix_fmt=yuv420p12le color_primaries=smpte432 color_trc=iec61966-2-1`、零 `BitDepth*` 码
⇒ 撤档后这条链**按用户所选落地**（改前是 8bit + bt709 语义）。并把这四条做成锁：
`verify-decision-delivery` **⑰**（**73/0 → 77/0**；含一条 sRGB 对照格必须读回 `bt709`，
否则"primaries==smpte432"可能只是解析器在找一个固定串）。

⚠ **同一次量测里抓到一条既有缺陷，与 #39 无关**：`--color-engine legacy` 在"HDR 源 + 显式广色域 SDR 目标"上
硬失败（链里只挂 `iccgen`、没走 tonemap ⇒ ffmpeg `Not yet implemented`、零产物、半成品已删）。
判它不是本轮引入的两条实测：**bd=8 那格旧特例根本管不到**（条件是 `>8`）也失败；换成 **BT.2020** 目标
（从来不在那条特例的 `ColorSpace == "Display P3"` 判据里）同样失败 ⇒ 登记 **#43**。
⑰ 因此刻意**只锁能出货的默认/引擎路** —— 不把 legacy 的失败钉成"必须失败"，那等于把缺陷焊死。

⚠ 归因过程自己踩的坑（已写进判据注释）：解析 `[cmd]` 时用 `\[cmd\][^\r\n]*` 取"最后一条匹配"，
读到的是运行器自己那句心跳文案"（**不改动 [cmd] 行**）"—— 散文里含 `[cmd]` ⇒ 关键判据被读成全 False。
真命令一律用 `\[cmd\]\s+ffmpeg` 锚定（`RunAvif` 与本段的 `reread_43.ps1` 都已改）。

⚠ 操作坑（本轮实测，值得记进交接）：**只改注释也会把整轮挡在 STALE 闸外** ——
`STALE` 判的是 mtime，而 MSBuild 在"内容相同"时**不重写目标 dll**（普通 `dotnet build` 之后四份 dll
仍是 23:30，源码却是 00:09）⇒ 运行器 `GATES-EXITCODE=2` 拒跑。
正确做法是对四个项目都 `-t:Rebuild`：重建后 dll mtime 变新（00:13）而 **SHA256 仍是
`1881E2F76539CAEF`** —— 这反过来把"纯注释改动不改交付"从推断变成了读数，本轮 grid/round 的指纹绑定因此不用重跑。


**待办（本轮不做，已登记 #42）**：`verify-oracle-matrix` 的**结构自检**（A1..A30，秒级）与它的 5010 例执行层是
绑在同一个手工长跑门禁里的 ⇒ 建议把"只跑结构自检"接进 54 条清单（新增一条 `_probe-matrix-evidence`），
否则任何一次行号漂移都要等有人手跑 2.5 h 才看得见。

## #38 收口（2026-09-27 凌晨）：能力表的"枚举集"这一位改回它真正的归属——**出口**，不是容器

**改的东西**：`FormatColorCaps.CicpSpaceEnums` → **`ExternalOutletCicpSpaceEnums`**（把归属写进名字），
`CapsAcceptsCicp(caps, d, bool externalEncoderOutlet)` —— **第三个参数刻意不给默认值**，
漏传就是编译不过，而不是悄悄沿用"容器 = cjxl 的限制"这个错前提（这比正则结构锁强：结构锁只能查形状，
这查的是存在性）。函数体只在 `externalEncoderOutlet==true` 时查枚举，否则回到 `CanCicp` 的自由 nclx。
连带：
· `ColorStrategyPlanning` 里 **8 个调用点**全部递进 `pol.ExternalEncoderExit`（`CarryCicpLabels` 用 `p.`），
  `NameableSupersetFor` / `SdrTwinOf` 两个 helper 加同名参数；
· S3 的拒绝文案不再一律怪"容器枚举集"—— 只有外部出口时才提枚举名；
· **#37 当年手写的 `externalEncoderExit && !CapsAcceptsCicp(...)` 前缀删掉**：出口维度现在由判据自己承担，
  那条特判成了重复（择一分支回到 `else if (!CapsAcceptsCicp(caps, cicpProbe, externalEncoderExit))`）。
· `ServiceProbe` 那条 ProPhoto 负断言显式传 `true` 并注明"这条讲的就是 cjxl 枚举"。

**A/B：六格交付逐字不变**（改前 `tests/output/t38/pre_*.log`、改后 `post_*`，同一源、同一参数）：

| 格 | 改前 | 改后 | jxlinfo 回读（两侧一致）|
|---|---|---|---|
| `-e Cjxl` + BT.2020 | 9294 B | 9294 B | Rec.2100 primaries / 709 transfer（走 `-x icc_pathname=stdicc_bt2020_bt709_bt2020nc.icc`）|
| `-e Cjxl` + Display P3 | 9650 B | 9650 B | P3 primaries / sRGB transfer（`-x color_space=DisplayP3`）|
| `-e Cjxl` + sRGB | 9104 B | 9104 B | sRGB primaries / sRGB transfer |
| `-e Ffmpeg` + BT.2020 | 8883 B | 8883 B | Rec.2100 primaries / 709 transfer（`-color_primaries bt2020 -color_trc bt709` 直写）|
| `-e Ffmpeg` + Display P3 | 9075 B | 9075 B | P3 primaries / sRGB transfer |
| `-e Ffmpeg` + sRGB | 8380 B | 8380 B | sRGB primaries / sRGB transfer |

⇒ **#38 是"口径修"而不是"交付修"**：ffmpeg-libjxl 那一路本来就在直写 nclx（表却说着"不能命名"），
所以修完没有产物变化 —— 这正是要 A/B 的原因：**没有读数的"只是重构"不算结论**。
指纹：`1881E2F76539CAEF`（#39/#41）→ **`3553E6665C4AD6F0`**（#38，四份一致）。绑定读数：见
`docs/HANDOVER_2026-09-22.md` 最近一轮那一行（整轮 54 条 + 全层网格 360 格，期望与 #39/#41 那轮逐项相同）。



## #43 收口（2026-09-27 凌晨）：avif/jxl/jxr 的 tonemap 豁免不再无条件，显式 SDR 目标恢复兑现

**根因一行**（改前 `FfmpegCommandBuilder.cs:1156`）：`if (fmt is "avif" or "jxl" or "jxr") return false;`
—— 只要目标格式是这三个就永不 tonemap。而同一个函数下方 `:1157-1163` 的注释写着，
**一模一样的错在 PNG 上已经修过一次**（原文摘录："旧写法无条件 return false，会把『用户显式要 SDR 目标
（如 Display P3）』也一起豁免：结果既不 tonemap 又被目标转换块排除 → PQ 像素塞进 SDR 标签容器，
**iccgen 处理不了 HDR 帧标签而直接转换失败**"）。#43 就是那段话在 avif 上的复现 —— 只是它**失败得更彻底**
（直接零产物）。

**改法（与 PNG 支同形，且拿 auto 列自证不误伤）**：`noExplicitTarget` 提到函数开头，
两条豁免支（avif/jxl/jxr 与 PNG/APNG）**共用同一个计算**；avif 支改成
`if (fmt is "avif" or "jxl" or "jxr") { if (noExplicitTarget) return false; }`（现 `:1187-1190`）。
⚠ **没有**照抄 PNG 的 `bd > 8` 前提：那是 PNG 自己"能不能标 HDR"的代理，
而 AVIF/JXL 在 8bit 下同样能用 nclx 标 PQ ⇒ 抄过来会凭空改掉一批 auto 格。
显式 HDR 目标（`P3 PQ`）仍由下面"目标为 HDR ⇒ 不 tonemap"那段拦住，语义不动。

**A/B：20 格逐格带 SHA16**（HDR/PQ 源 `tests/output/validate/src_hdr.png`；
改前 `tests/output/t43/matrix2_pre_measure.log`（dll `3553E6665C4AD6F0` 的留存产物做离线复测）、
改后 `tests/output/t43/matrix2_post43b.log`（dll `1F1C2814B3D462DE`））：

| 格（legacy） | 改前产物 = 字节/SHA16 | 改后 | 判定 |
|---|---|---|---|
| avif × auto | 5247 B / `7B24C02FE376F435`，标签 bt2020 | 5247 B / `7B24C02FE376F435` | **逐字不变** ✔ |
| avif × `P3 PQ` | 5247 B / `7B24C02FE376F435` | 5247 B / `7B24C02FE376F435` | **逐字不变** ✔ |
| avif × Display P3 | **零产物**，`exit=1`，命令行只挂 `-vf iccgen`，`Error while filtering: Not yet implemented` | 4169 B / `E82E2A372F7732DF`，`smpte432` + trc `iec61966-2-1`，链 `tonemap=hable→zscale→iccgen` | 修好 ✔ |
| avif × sRGB | 零产物，`exit=1` | 4281 B / `5595267D8488EA3F`，`bt709` | 修好 ✔ |
| avif × BT.2020 | 零产物，`exit=1` | 4725 B / `FE7E1AC885B02789`，`bt2020` | 修好 ✔ |
| jxl × 五格 | auto/P3/sRGB/BT.2020 **同一份** 18235 B / `387E3F40D905A96B`（标签 bt2020）+ `P3 PQ` 18204 B | 逐字节相同 | 本支**管不到** ⇒ 转 #44 |
| engine 十格（avif+jxl） | 5855 / 5992 / 5623 / 5247 / 12954 / 12202 / 13653 / 18204 B（SHA16 见日志） | 全部逐字节相同 | **未被牵连** ✔ |

⇒ 交付面变化 = 恰好"显式 SDR 目标 × avif × legacy"三格从**硬失败**变成**按目标出货**；
其余 17 格逐字节不变。**注意这不是"零风险改动"**：三格原本连产物都没有，
所以"改前 vs 改后"只能比命令行与退出码，像素正确性由 ⑰b 的标签对账 + 整轮/网格绑定承担。

**覆盖面的诚实账（勿把 #43 读成"三个格式都修了"）**：
`avif or jxl or jxr` 这一行三个格式共用，但 legacy 下**只有 avif 真的走 FfmpegCommandBuilder 的链**：
· **jxl legacy = `cjxl 直接编码`旁路**（日志里连 ffmpeg 的 `[cmd]` 都没有，命令行是
  `cjxl.exe 源.png 目标.jxl -x color_space=DisplayP3`）⇒ 传了 `-x color_space` 而输出逐字节不变，
  是 cjxl 侧静默忽略 + 本仓不做交付端回读对账；
· **jxr legacy = `JxrEncApp` 两步法旁路**（现测 `tests/output/t43/jxr_png_check.log`：auto 与显式 P3 得到
  **同一份 152166 B / `20D3E0175E9D12D8`**，标签 `color_primaries=unknown color_transfer=unknown`，
  中间那条 ffmpeg 命令行里 tonemap/zscale/iccgen 全无）；
· 两条旁路都自己打了响亮的 `[色彩后置] ⚠️ 本通路的命令行不由 FfmpegCommandBuilder 产出 ⇒ 裁决点未参与`
  ⇒ 症状是"目标被吃"，不是"零播报"。**另记任务 #44**（属 P7a-3 未接线那一族）。
· **PNG 支顺带复测**（共用同一谓词，diff 只删掉它自己那份局部声明）：legacy auto = 43777 B 保持
  HDR 直通（`smpte2084`/`bt2020`）、legacy Display P3 = 98501 B 走 `tonemap+zscale+iccgen` 交回
  `smpte432`/`iec61966-2-1` ⇒ 未被牵连。

**锁**：`verify-decision-delivery` ⑰ 从"只锁能出货的引擎路"升成两路都锁 —— 新增 **⑰b 四条**
（legacy×P3 必须出货、legacy×P3 的 `smpte432`+`iec61966-2-1`、legacy×sRGB 对照必须读回 `bt709`、
**legacy×auto 不许出现 tonemap 且产物 trc 必须仍是 `smpte2084`**）⇒ 该门禁 **77 → 81 条**，
单跑 `pass=81 fail=0 skip=0`。最后那条是本次唯一能抓住"谓词写反"的读数（见下）。
**绑定读数（四份 dll = `1F1C2814B3D462DE`）**：整轮 54 条「运行器汇总：全部通过」`GATES-EXITCODE=0`、
脚本结果行 54 条全部 `exit=0`（非零 0）、`[WARN]` 行 **0**、探针 22 mode 合计 **590/0**、
`_probe-cjk-hardcode-scan` **14/0**（UI 面 **827 ≤ 基线 837**，本轮当场重测）、
`runner-self-sha256=FE846C90C18EB85A lines=1075 bytes=95990`（起/收尾逐位一致）；
全层网格 **`PASS=19 FAIL=0 cells=360`**（CAP 违规 **0** ‖ 已播报升档 108 · 降档 72、承诺账 **0**、
LBL 无标注 **0**、NUM 108 格 **红 0**、RT 9 组 **红 0**、NC 四项到场齐）。
日志 `tests/output/t43/round-and-grid-console.log`（01:45:37 → 02:04:17）、
`tests/output/t43/grid-t43-run6.log`（02:04:17 → 02:15:10）。
⚠ **网格读数与上一档逐项相同是预期的**，别读成"这次改动没被测到"：
`verify-color-matrix-full` 的三个驱动层一律带 `--color-engine engine`（`:257` / `:408` / `:411`）
⇒ legacy 路不在它的覆盖面内，那条路是 ⑰b 在管；本轮 20 格矩阵才是 legacy 交付面的直接读数。

⚠ **第一版把豁免写反了，而且是矩阵抓出来的、不是我看代码看出来的**：
`return noExplicitTarget;`（本函数返回的是"**要不要** tonemap"，豁免应当 `return false`）⇒
症状恰好是**两列同时错**：该逐字不变的 auto 格被强加 tonemap（5247 B → 4281 B），
该修的三个显式目标格命令行**一字未动**（仍然 iccgen 零产物）。
⇒ 规矩：**改这条豁免必须跑 20 格矩阵**，"看代码顺眼"零检出力。

**⑰b 的变异验牙**（`tests/output/t43/mutate_43.ps1` → `mutate_43.log`，2026-09-27 02:2x）：

| 步骤 | 读数 |
|---|---|
| 变异锚点唯一性自检 | `if (noExplicitTarget) return false;` 命中 **恰好 1 处**（≠1 就 throw，避免误改 PNG 支那一条） |
| 变异体（退回无条件 `return false` = 改前语义） | dll `1F1C2814B3D462DE` → **`A98FA96357417760`** |
| 跑 `verify-decision-delivery` | **`pass=78 fail=3`** ⇒ 运行器 `GATES-EXITCODE=1`；红的**恰好**是 ⑰b 前三条（"必须出货"读到 `exit=1 有产物=False 日志含 Not yet implemented=True`、两条标签断言实得空串） |
| 第四条（auto 反向锁） | **保持绿** ⇒ 分工正确：前三条咬"豁免太宽"，第四条只咬"谓词写反"，一条变异不会把四条一起点亮 |
| 还原 | 源文件与备份**逐字节相同**（`Copy-Item` 字节级还原，不用 `WriteAllText`）+ 四项目 `-t:Rebuild` ⇒ dll **回到 `1F1C2814B3D462DE`** ⇒ 本轮整轮/网格的绑定在变异实验之后依然成立 |

⚠ 本轮另抓到两处**自己写的 harness 假读数**（都已修，判据注释里留了字）：
1. `Get-ChildItem -LiteralPath $od -Include *.avif,*.jxl -Recurse` —— **PS 5.1 在 `-LiteralPath` 上
   根本不应用 `-Include`** ⇒ 候选把 `o.txt`（日志）也算进去，`Sort Length -Descending` 一旦日志更大，
   "产物字节"那一列读的就是日志大小。改成按 `Extension` 显式过滤 + 候选数自检 + 记 SHA16。
   （同一族的 `-Include` 失效此前只记过 `-Path` 通配那一种形态。）
2. `& runner @('-Scripts','x')` —— **数组 splat 是按位置灌参数**，PowerShell 不对数组元素做参数名识别
   ⇒ `-Scripts` 落进运行器第 1 个位置参数 `$Probes`，被当成**探针 mode** 跑：
   日志多一条 `[-Scripts] exit=2 ⚠未执行(未知 mode…)`、汇总"失败 1 项"、退出码 1，
   而 22 个探针**一个都没跑**。启动器 `tests/output/_run_gates_t35.ps1` 改用**哈希表 splat**。
   （运行器那侧是响亮失败的 ⇒ 不改它。）
3. 顺带一条取数口径：本构建 `ffprobe -show_entries stream=color_trc` 对 png/jxl/avif **恒空**，
   键名要用 `color_transfer`（源 PNG 用同一命令量得出 `smpte2084` ⇒ 不是探针坏，是键名）。
   ⇒ 任何"产物 trc 不是 PQ"的断言都必须配一个能读出非空 trc 的对照格，否则就是恒真。




## #44 定性完成、尚未修（2026-09-27 凌晨）：legacy 的两条外部工具旁路不吃用户目标

**症状**（HDR/PQ 源，`--color-engine legacy`）：jxl 的目标 ∈ {auto, Display P3, sRGB, BT.2020} 四格得到
**同一份 18235 B / `387E3F40D905A96B`**、标签仍是 `bt2020`；jxr 的 auto 与显式 P3 得到
**同一份 152166 B / `20D3E0175E9D12D8`**、标签 `unknown`。两侧日志都有响亮的一句
`[色彩后置] ⚠️ 本通路的命令行不由 FfmpegCommandBuilder 产出 ⇒ 裁决点未参与`
⇒ **判据只能打"目标被吃"，不能打"零播报"**。engine 路同参数给出 smpte432/12954、bt709/12202、bt2020/13653
三种不同产物 ⇒ 不是能力缺失，是**这条旁路没接线**。

**机制已定量**（`tests/output/t44/cjxl_x_probe2.log`）—— 两句话都不准确，准确规则只有一句：
**输入自带色彩元数据（ICC/CICP）时 cjxl 以输入为准、`-x` 被静默忽略；输入不带时才生效**。
· HDR PNG：不给 `-x` / 给 `DisplayP3` / 给 `sRGB` ⇒ **同一份 16399 B / `A9DB6D0E82285B1E`**；
· 同源转 PPM：8051 / 8492 / 8053 B 三份各不同，jxlinfo 分别报 `sRGB` / `P3 primaries` ⇒ 生效；
· **无内嵌 ICC** 的 SDR PNG：8061 vs 8433 B ⇒ 也生效 ⇒ 决定因素不是"容器 vs 裸流"。
（顺带一条 token 账：`-x color_space=Rec2020` 被 cjxl **拒收** exit=1、不产出 —— 别把这个值抄进映射表。）
⇒ `CjxlService.cs:326` 的"与输入格式无关"与 `QueueProcessor.cs:1913-1915` 的"仅 PPM/PAM 生效"
**两处注释已按实测改写**（纯注释改动，四份 dll 仍 `1F1C2814B3D462DE` 逐字节不变）。

⚠ **"半个修法"比现状更坏，这条已经定量否证**：看起来最小的改法是"有显式目标时改走 ffmpeg 管道，
让 `-x` 生效"。但 `-x` **只写声明、不重映射像素** ⇒ 产物会"声称 P3 而像素仍是原域"，
正是本仓最忌讳的"改了标签没改像素"那一族。判据设计本身也有教训：
最初想用 PSNR 区分"只改标签 vs 真转像素"（假设解码会把像素渲染进显示域），
实测三格全落在 27.42 / 28.27 / 29.68 dB **同一档** ⇒ **该判据判别力为零，我的假设错了**；
换成查解回件自带的色彩描述才定案：`p3.png` 带 `ProfileDescription: DisplayP3`、`n1.png` 无 ICC，
而两者像素差只在有损噪声档 ⇒ **只写声明**（同 §"量一个 API 前先确认它的语义"）。

**修法三选一**（默认建议 ①，另两条有硬前提；决策输入见本文末"需要维护者点头的事"第 5b 条）：
① 接**同源登记**（P7a-3 原计划）：先用既有 legacy 色彩链（#43 之后 avif 那条已能兑现目标）把**像素**转好，
   再交给 cjxl / JxrEncApp；② 交付端**回读对账**（jxlinfo 能读出 `Color space: … primaries … transfer`，
   但要处理它的 `couldn't load` / exit=1 失败形态）；③ 响亮拒绝该组合 —— **能力面降级，要授权**。

### #44-① 施工单（2026-09-27 由只读测绘代理产出，主循环照此施工）

缩写：`QP` = `src/FfmpegGui/Services/QueueProcessor.cs`、`DEC` = `…FfmpegCommandBuilder.Decision.cs`、
`FCB` = `…FfmpegCommandBuilder.cs`、`RCP` = `…ColorMapping/RawColorPipeline.cs`、
`CJX` = `…CjxlService.cs`、`CEH` = `…ColorEncodingHelper.cs`。

**调用链已确认**：分叉在 `QP:392`（`engineRoutable = ColorEngineRouter.ShouldRoute(...)`），
显式 `legacy` ⇒ `CER:100-102 Requested=false` ⇒ `CER:173` 早退 ⇒ `QP:714`（jxl）/ `QP:729`（jxr）的
`&& !engineRoutable` 成立 ⇒ 进旁路。**这个分叉点不能动**（它是用户的显式逃生门，改道等于取消 legacy）。

| 插点 | 位置 | 要做什么 |
|---|---|---|
| **B** | `QP:1968` 直编前 | 先 `dec = DecideOutputColor(item.Options, item.InputPath)`；**只有 `dec.PixelChains.Count == 0` 才允许直编**（保住 ICC 副本技巧与 JPEG 无损重封装这条性能路）；需要转像素就改道插点 A。判据读"链"不读意图，与 `QP:1218 LogInjectedScale` 同口径 |
| **A** | `QP:2812`（`PipeFfmpegToCjxlAsync`） | ffmpeg 侧改用 `dec.Decl*` 拼输入声明 + `-vf "<string.Join(",", dec.PixelChains)>"`；cjxl 侧改传 `colorSpaceOverride = CEH.MapPrimariesTransfer(dec.OutPrimaries, dec.OutTrc)`（与引擎 `RCP:466` **同签名同参数序**）；随后 `NoteColorDecisionSource(item, item.Options, item.InputPath)`。⚠ 同源之后 `BuildPipeColorArgs`（`QP:4108`）的显式目标分支 `4145-4185` 与高级模式分支 `4133-4144` **变死代码 ⇒ 收缩/删除，不许留两份**；⚠ `QP:2896` 还有**第二次** `BuildPipeColorArgs` 调用（`PipeFfmpegToExternalEncoderAsync` 内部自己拼 ffmpeg argv，与 `:2817` 展示给用户的 `item.Command` 是两个来源）⇒ 不同步改就会"改一处不生效" |
| **C** | `QP:2307-2318`（jxr 第一步） | `colorArgs ← dec.Decl*`（`BuildJxrColorArgs` `QP:2552` **整套退役**，它是第三套自算色彩判定）；`filterArg ← dec.PixelChains`；删 `QP:2291-2306` 那段**注释自证恒为 null 的死守卫**；登记后让链尾 `EmbedDroppedFilterIccAsync` 给 JXR 补目标 ICC（JxrEncApp 写不了 ICC，exiftool 可写可读，`RCP:546-548` 实测） |
| **E** | `QP:880-891` | **不动**。登记之后这三条旁路自动不再打"裁决点未参与"；未登记的（djxl→cjxl、GIF/AVIF 两步法、Cjpegli、DNG）照旧 —— 门禁 ⑩/⑫ 在钉这个二选一 |

**一个把改造量砍掉一大截的判断**：管道路线交给 cjxl 的是**裸 PPM 流**，而 §#44 的实测规则是
"输入**不**自带色彩元数据时 `-x` 生效" ⇒ 这条路线**不需要 ICC、也不需要 `icc_pathname`**：
像素由我们的链转好，出口用 `-x color_space=` 声明即可（`RCP:424-446` 引擎就是这个形态）。
⇒ 原以为必须新增的"执行体向链尾声明我已写 ICC"机制（测绘报告称之为插点 D）**在 jxl 管道路线上可以不要**；
只有"目标不可用 CICP 命名"（超广保真那类）的情形才需要它 —— 那种情形**先不改道**，维持现状并让它继续登记 + 响亮播报。
⚠ 这是一条**待落地复核**的简化，不是已证结论。

**必须一起改的判据（红是对的方向，别删断言）**
· `verify-decision-delivery.ps1:529` 钉的是"直连 cjxl 分支不参与色彩映射 ⇒ 两条通路的**有意**差异"、
  `:531-532` 钉"两路像素不同（PSNR<30 dB）" ⇒ 同源后两格转红，**红正确**，要按新契约重写（两路应趋同）。
· `verify-color-wiring.ps1:586-588`（legacy JXR 不再硬改标）会变红 = 行为确实变了 ⇒ 按新实测重写；
  但 `:594-637` 的**负控**（无标签 16-bit 不得出现 `primariesin=bt2020`）必须继续为真 ⇒ 新链一旦回去猜
  Rec.2020，那两格红是**真回归**。
· ⚠ 误红预防：`verify-webp-anim-probe.ps1:759-766` 逐字匹配 `[cjxl] 直接编码 (输入: .webp` 与
  "直接编码失败…"两行文案 ⇒ 插点 B 必须**只在需要转像素时**才跳过直编，且这三句文案一字不动
  （`.webp` 是无目标 auto 格，理想上根本不该改道）。
· ⚠ `verify-jxr-anim-input.ps1:262-265` 要求 jxr 第一步 `[cmd] ffmpeg` 含 `-frames:v 1` 且以 `input.(bmp|tiff)"` 结尾
  ⇒ 只加 `-vf` 安全，改中间文件名/扩展会误红。

**施工约束（会当场卡住门禁的两条）**
· `_lib-matrix.ps1` 的 A21/A30 会**真去读源码行**：`DEC:144/147/155`、`CER:101`、`FCB:971/981/1013/2041`、
  `ImageEncoderArgs:715/763` 等都是行锚 ⇒ **新代码一律加到 `DEC` 尾部**（`:621` 类右括号之前），
  别在第 144 行之前插任何行，否则 4 条锚纯漂移转红（第 55 条门禁默认在清单里，一定会响）。
  `QP` **无任何行锚**（只有散文引用）⇒ 可自由增删行。
· `_probe-cjk-hardcode-scan` 的 UI 面基线现 **827 / 上界 837 ⇒ 余量只有 10** ⇒ 新增日志文案一律 **ASCII**，
  别写中文行，否则要么撑破棘轮、要么被迫第八次抬基线。

**盲区（不会红，但别读成"没改坏"）**
`verify-color-matrix-full.ps1` 三层驱动 `:257`/`:408`/`:411` **全部带 `--color-engine engine`** ⇒
360 格矩阵**完全测不到 legacy 旁路**；且 `:216` 的 `$fmts` 无 `jxr` ⇒ **jxr 零矩阵覆盖**。
旁路修完必须**新增 legacy 层**（至少 `{jxl, jxr} × {auto, P3, sRGB, BT.2020} × legacy`）才有针对性判据；
`ServiceProbe decision`（`Program.cs:3071`）也只跑 png/jpg/webp/tiff，`Program.cs:1184` 那句未兑现的 TODO
「完整"直接路径 vs cjxl 管道"同构待 P7 接线后补」就是本次该补的白盒锁位置。

**⚠ 施工单被实现阶段纠正的三处（2026-09-27，落地后回读；照原单施工会踩空）**
1. **`:4379` 不是第二处 `BuildPipeColorArgs`** —— 它调的是 `FfmpegCommandBuilder.BuildArguments(pipeOptions, "-", out, realInputForProbe)`
   （djxl→ffmpeg 码流路线）。真正的"第二处自拼 ffmpeg argv"在 `PipeFfmpegToExternalEncoderAsync` 内部（原 `:2896`），
   且它与 `:2817` 展示给用户的 `item.Command` 是**两个来源** ⇒ 已收敛成"一次拼装、传 `prebuiltFfmpegArgs` 进去"。
2. **`BuildPipeColorArgs` 的显式目标分支不是死代码** —— 还有 **Cjpegli** 两个调用方（`PipeFfmpegToCjpegliAsync`
   与通用回退）⇒ 保留原样。"消灭第二份拷贝"这一条只对 **jxl 管道 + jxr 第一步** 成立，不能顺手删函数。
3. **改道判据不能只看 `PixelChains.Count > 0`** —— 出口语义必须**落得进 cjxl 的 `color_space` 枚举**
   （只有 `sRGB / DisplayP3 / Rec2100PQ / Rec2100HLG`；SDR **BT.2020** 在 cjxl 无枚举，
   `MapPrimariesTransferToCjxl("bt2020","bt709") → null`）。若照原单硬改道，会得到
   "像素已转 BT.2020、标签却是 cjxl 对裸流的默认解读" ⇒ 正是本节上面警告过的"**半个修法更坏**"。
   ⇒ 现在闸门是"链非空 **且** 出口可命名"。后果（**如实登记**）：**jxl 路线的 SDR BT.2020 目标列仍未修**，
   产物与改动前逐字节相同；已修的是 sRGB / Display P3 / PQ / HLG 四类目标。这一格要真修必须走 ICC，
   而 exiftool 写 JXL ICC 无效（`verify-color-caps.ps1:52` 已钉）⇒ **是独立一块工作，别当 #44 已完**。
4. 施工单没提的第二个管道路线调用方：`ProcessJxlInputAsync` 的 jxl/jxr 输入 → cjxl 管道（现 `:3723`）
   在调用前把 `item.InputPath` **临时换成中间文件**并在 `finally` 里删除 ⇒ 本次新增的 `NoteColorDecisionSource`
   会把**一个注定消失的探测输入**登记进去，链尾再按它复算一次裁决。登记面扩大后这是**已知毛刺**（实现代理已点名），
   要在验收里专门看一眼：jxl/jxr 的后置步骤是否真的都是 no-op。（不许当已知使用）：① `ColorTransformPlan`（引擎）与
`OutputColorDecision`（legacy）之间**没有字段级对拍探针**，两套结构各调一次本身就是第二份口径；
② 除 `plan.AttachIcc=false` 外没有任何"执行体声明已写 ICC"的机制；③ `--intensity_target` 在输入带元数据时
是否也被 cjxl 忽略，**无实测**；④ `CloneOptionsForFfmpeg`（约 `QP:4540-4555`）搬了哪些色彩字段未逐行核对；
⑤ `ProcessJxlInputAsync` 的 djxl→cjxl（`QP:3688`）**链路上根本没有 ffmpeg** ⇒ 要做像素转换必须新增一步，
超出"插点"级别，未列入最小改动。





## #44 收口进展（2026-09-27 下午，dll `C2908736C143A609`）：jxl/jxr 两列都已按目标交付，只剩一格
**改前**（本文上面那张 20 格表）：jxl legacy 四目标 = 同一份 18235 B；jxr legacy auto/P3 = 同一份 152166 B。
**改后**（`tests/output/t44/matrix44.ps1 -Prefix post44b`，逐格哈希 + `djxl` 解回原生像素 md5 反证"只改标签"）：
· jxl legacy P3 → `7322 B/smpte432/iec61966-2-1`、sRGB → `7019 B/bt709/iec61966-2-1`（像素域真的变了：
  auto 与 bt2020 解回同 md5、P3/sRGB/P3 PQ 三者互不相同）；
· jxr legacy P3/sRGB/BT.2020 → 281370 / 252220 / 521624 B（互不相同，链里 `tonemap=hable…zscale=…p=smpte432:t=iec61966-2-1`，
  并有 `[jxr] target ICC embedded via exiftool`）；
· **无误伤**：jxl legacy `auto`(`18235/387E3F40…`) 与 `P3 PQ`(`18204/64D73D99…`)、engine 五格、jxr 全部十格**逐字节不变**。

**根因不在我施工单猜的两处**（既不是"jxl 走 `AttachIcc` 表达目标"、也不是 `MapPrimariesTransfer` 返回 null）：
是裁决点**内部**把 `isRgbNativeFmt`（`:122`，本意"RGB 原生容器别写 `-colorspace`"）**复用**成了"跳过 tonemap 链"，
而它的许可支只补了 `tiff`/`png` ⇒ **四员里只剩 JXL 没有许可** ⇒ `链数` 恒 0（先建观测面才量出来：这五格 `链数` 全 0）。
⇒ 新判据是"**直编路线交付的输入侧声明 `dec.Decl*` ≠ 裁决点要求的出口 `dec.Out***"（`DirectEncodeTargetDiffers`），
链数只作充分条件保留；fail-closed 方向是"**任一侧未命名就不改道**"。
同一条因同时解释 #43 的同族复现（`--encoder ffmpeg` + jxl + 显式目标 ⇒ `iccgen` 拿到 PQ 帧 ⇒ `Not yet implemented`、
零产物）：补上 `jxlNeedsTonemap` 许可后那三格改为**真出货**（7186/6917/8221 B，标签 smpte432、bt709、bt2020），
auto 与 P3 PQ 仍 `17603/6D65E98C…` 不变。

**唯一未修的一格已量到出路**：jxl 直编 + **SDR BT.2020**（cjxl 无该 `color_space` 枚举，实测 `-x color_space=Rec2020`
`exit=1` 拒收）⇒ 现在维持现状 + 响亮点名（`[cjxl] WARNING: target … has no cjxl color_space enum …`，不再是静默）。
出路已实测铺好：**`-x icc_pathname=` 在裸 PPM 上生效** —— `tests/output/t44/icc_pathname_probe.ps1`：
无 flag `42139 B/92278FBF…`（jxlinfo `sRGB primaries`）vs `icc_pathname=bt2020.icc` `42374 B/5FFED91E…`
（jxlinfo `584-byte ICC profile, CMM "lcms"`）。⇒ 该格应走"链转像素 + ICC 描述"，而不是永远停在点名；
⚠ 这与"exiftool **后置**写 JXL ICC 无效"（`verify-color-caps.ps1:52`）**不是同一件事**：那条仍成立。

## #44 收口（2026-09-27 傍晚，dll `892E320F955BDC8A` → 本轮重绑）：最后一格已按目标交付

上面那格（jxl 直编 + SDR BT.2020）已按"链转像素 + `-x icc_pathname=` 描述"落地：
`QueueProcessor.ProcessCjxlAsync` 在 `cjxlSpace==null` 且需要改道时，用裁决点的 **出口** 语义
（`dec.OutPrimaries/OutTrc/MatrixForOutput`）生成标准 ICC 并递给管道路 ⇒ 该格从"目标被吃掉"变成"按目标交付"。

**产物层读数**（`tests/output/t44/matrix44.ps1`，20 格 = {jxl,jxr}×{legacy,engine}×{auto,P3,sRGB,BT.2020,P3-PQ}）：

| 格 | 改前（`post44b`，与 auto **逐字节相同**）| 改后（`post44c`）|
|---|---|---|
| `jxl_legacy_bt2020` | `18235 B / 387E3F40…` | `8245 B / 559C2C33…`，`remap=True`，标签回读 `Rec.2100 primaries, 709 transfer` |
| 其余 **19 格** | — | **字节不变**（auto `18235/387E3F40`、P3-PQ `18204/64D73D99`、5 个 engine 格、10 个 jxr 格）|

⇒ 只动了该动的那一格，无附带漂移。⚠ `jxr_engine_{p3,bt2020,p3pq}` 三格 `exit=1 无产物` 是**改动前就有**的
（`matrix44_post.log` 同读数），归 engine 路另一条线，不是本轮回归。

**验收 `tests/output/t44/check44c_bt2020.ps1` = PASS 14 / FAIL 0**，五组判据各自带对照：
① 标签回读 == 请求目标（对照：auto 格回读 `PQ transfer` ⇒ 该判据不恒真）；
② 目标格解码 != auto != 修复前（修复前那一格与 auto 的解码 md5 **完全相同** `8EDE4AC1…` = 缺陷现场复现）；
③ 产物 == 喂进 cjxl 的像素：与"本格 `[cmd]` 的 ffmpeg 侧单独跑一遍"的参照对拍 **34.41 dB**，
   同一判据打在修复前产物 = **9.58 dB**（对照件能触发门槛）；参照的 `-vf` 链先与日志逐字比对，防"拿旧尺量新链"；
④ 分支可证：`[cmd]` 同时含 `p=bt2020:t=bt709` 与 `-x "icc_pathname=`，且 **SDR 目标不发 `--intensity_target`**；
⑤ 无 HDR 空洞：`JxlColorSpaceEnums` 含 `Rec2100HLG` ⇒ PQ/HLG 目标永远走枚举、走不到这条 ICC 路；
   而 `IccProfileService.GetOrGenerateStandardIcc` 对 `smpte2084/arib-std-b67` **返回 null**（拒绝生成，不造假标签），
   此时本路落到"响亮 WARNING + 不改道"分支 ⇒ 最坏结局是"点名"，不是"静默错色"。

### 两条本轮实测出的**工具事实**（都推翻过我自己的初判）

1. **`.jxl` 里没有字面 ICC**：cjxl 会把 `-x icc_pathname` 的 profile **归一成 CICP 枚举**。取证方式=拿 ICC
   中段 48 字节回搜 `.jxl` 原始字节，**三格全部不存在**（含 auto 那份从源 PNG 带来的 4324 B ICC）。
   `djxl → PNG` 时才展开成一份规范 536 B ICC，两路（legacy/engine）展开结果**逐字节相同**。
   ⇒ 判"目标是否交付"只能读 `jxlinfo` 的 Color space 行；把 PNG 的 iCCP 当成"容器存了我们那个文件"是**过度解读**。
2. **不能用 djxl 的 `.ppm` 做 PSNR**：它写 `P6 / maxval 65535`，ffmpeg 的 pnm 解码器读不了 16-bit ⇒
   字节错位，任意两格之间都只剩 ~10 dB。本轮最初就是被这条骗到，差点把"两路差 10 dB"报成缺陷。
   改走 `.png`（djxl 出 16-bit PNG，ffmpeg 读作 `rgb48be`）后数字才可信。
   ⇒ 顺带一条纪律：PSNR 这类数要**两个独立实现**对得上才能写进报告（本轮 py 手算 34.41 vs ffmpeg 34.407023）。

### ⚠ 新登记一条**不属于 #44** 的发现：legacy 与 engine 在 HDR→SDR 格上整体差 ~10 dB

修好上面那条取数坑后重测：**同目标的跨路 PSNR 全部落在 10.1–10.6 dB**（p3=10.12 / srgb=10.45 / bt2020=10.24），
而**同路跨目标**是 27–32 dB ⇒ 这不是目标没兑现，是**色调映射契约本身两路不同**：

| | 出口均值（16-bit）| 峰值 | `--intensity_target` | 白点 |
|---|---|---|---|---|
| legacy（ffmpeg `tonemap=hable:param=0.5`）| ~18.8k | ~44k | **不发** | SDR 满量程 |
| engine（进程内 H1 内核）| ~30.4k | 65535 | **发 1000** | `white=203nit headroom=49.26` |

⇒ 两路对"HDR 源转 SDR 目标"给出的**码值域不同**（engine 把 203 nit 白映射到 1000 nit 峰值的标签体系里）。
哪一路是契约需要维护者定：这属**跨路一致性**（与 #28/#43 同族），**不是** #44 的"旁路吃目标"，
故本轮**不动任何一边的行为**，只登记。⚠ 判据缺口同时在 `#49`（网格无 legacy 层）——补上那层就会稳定看见这条。




## #49 落地（2026-09-27 傍晚，dll `3197C2F8F1F314D8`）：网格补 LEGACY 通路层 —— 当天就抓到一个新的真缺陷

`verify-color-matrix-full.ps1` 新增 **§6c LEGACY 层**：2 个 SDR 源（`p38` / `bt20208`）× `{jxl, jxr}`
× 3 个显式目标 × **两条引擎路** = **24 格**，并把 `LEGACY` 计入 §7 的**到场账**（缺席即汇总红）。
补的不是格式覆盖，是**通路**覆盖：此前 360 格每格都写死 `--color-engine engine`，⇒ #43/#44 那一族
"legacy 旁路吃目标"的缺陷**在 360 格里一格都看不见**（网格全绿、缺陷在场）。

### ① 当场抓到并已修：legacy JXR 在「目标 == 源色域」时**零标注**

`-f jxr` + `--color-space` 指定到**源自己那个色域**（P3 源 → Display P3、BT.2020 源 → BT.2020）⇒
产物**既无 ICC 也无 CICP**，而日志还写着「用户已指定色彩空间，跳过源文件 ICC Profile 恢复」。
根因链在 #44 的插点 C（`QueueProcessor.cs`）：

* 目标 == 源 ⇒ 裁决点 `PixelChains.Count == 0`（**不改像素是合法的**）；
* 但旧写法把 **ICC 的生成**写在 `if (PixelChains.Count > 0)` **里面** ⇒ 不生成、也不嵌入；
* 元数据恢复那侧又按「用户指定了色彩空间」跳过回带源 ICC ⇒ **两头都不负责**；
* JXR 没有 CICP 可退 ⇒ 查看器按 sRGB 解读 P3/BT.2020 数值 = 广色域被洗白。

修法：**ICC 的生成/嵌入与"像素转没转"解耦** —— 只要裁决点给得出成对的出口 token 就生成并嵌入；
`if (jxrPixelsRemapped)` 那道后置闸改成 `jxrPixelsRemapped || jxrTargetIcc != null`。
实测（`exiftool -ICC_Profile:ChromaticityChannel1`，描述文字不作判据）：修后两格红原色
**0.67999,0.32001**（Display P3）/ **0.70799,0.29201**（BT.2020）；quick 层 LEGACY 违规 **2 → 0**；
⚠ 而 `matrix44.ps1` 的 **20 格逐字节不变** —— HDR 源的出口是 PQ/HLG，`GetOrGenerateStandardIcc`
对 HDR 曲线**返回 null**（拒绝生成、不造假标签）⇒ 走不到本分支，无附带影响。

### ② 登记为任务 #53（不修）：同一请求两路结局不同

legacy 出产物并嵌上目标 ICC，engine 那侧按能力表 `CanCarryArbitraryIcc=false` 对广色域 **exit=1 零产物**
（干净拒绝、非静默）。LEGACY 层把它打成**读数**而不判红 —— 判红等于替维护者拍板"档①要不要做"。
另附一条实测：engine+jxr 的 `auto` 与 `sRGB` 两格**字节相同是合法的**（日志显示两格计划同为
「Map→sRGB + `UnlabeledAssumedSrgb`」），所以 (C) 那条"显式目标必须与 auto 不同字节"只打 legacy 路。

### ③ 两条判据口径教训（都已写进脚本注释，防止下次重犯）

* **跨路像素 PSNR 的绝对阈值在"两次独立有损编码"之间不成立**：第一版判据写 `两路互比 ≥40 dB`，
  实测连**同源同目标**（`bt20208 → BT.2020`，变换本应是恒等）都只有 **39.87 dB** ⇒ 那个门槛量的是
  有损扩散不是色彩。改成**方向判别**：产物必须离"按 zimg 转到目标空间"的参照更近、离"源原样存值"更远
  （对照件自带 ⇒ "目标被吃掉"那种形态会当场翻向）。
* **参照链含 `t=bt709` 时对 engine 不适用**：`zscale t=bt709` 实为纯 γ(1/2.4)，而引擎按 **BT.709-6 定义式**
  编码（本仓自己的**口径黑名单**，见 `verify-color-caps.ps1` (f)：两者 8-bit 最大差 27.2 LSB ⇒ 相对量
  ~19.5 dB、与位深无关）。实测签名：`bt20208 → sRGB jxl` 上 engine 离本参照 **14.68 dB**、离源原样 **18.72 dB**，
  而 legacy（走的正是 zimg 那条链）离参照 **28.32 dB**、离源 **11.69 dB**
  ⇒ 判 engine "没转像素"是**拿错的尺子量对的像素**。处置=该情形只判 legacy，engine 侧仍由
  (A) 标签 + (C) 字节对照两把尺看住，并**打印跳过格数**（本轮 5 格）而不是静默少跑。
* 取数容器仍按 #44 那条：jxl 一律 `djxl → PNG` 再比，**不用 `.ppm`**（`maxval 65535` ffmpeg 读不了），
  且双方先 `format=rgb24` 归一（两路出口位深可以不同）。

## P4 容差口径与注释对账（P2，依赖 P1）

1. **`EquivalenceTolerances` 六个常量四个是死的**（`MatrixToIdentity`/`MatrixElements`/`CurveParams`/`Chromaticity` 消费点 0），
   真正在用 `1e-9`（`ColorTransformPlan.cs:760`）、`1e-5`（`:208`）、`2e-3`（`ColorSpaceIdentify.cs:32`）——相差 6 个数量级，
   说明"等价判定"和"识别归属"本就是两件事被混在一个词下。
   做法：先给每个物理含义命名（识别容差 / 直通容差 / 曲线容差），**能接管就接管、接管不了就删**（不留 `// removed` 之类的壳）。
2. **收紧 `KnownSpaceTol = 2e-3`**：这会让一部分素材从"识别为某空间"退回 unknown ⇒ **属能力面变化，需授权**。
   先出决策输入：对既有素材跑一遍 `colormath`/`iccname`，统计"收紧到 1e-4 / 2e-4 后有多少格退回"。
   配套夹具：**偏移量落在 1e-3~2e-3 之间的合成 ICC 必须被报出差异**（现在会被静默判等并走 L0 无标注直通）。
3. **三处注释与实现不符**（改文字，不动像素）：`RawColorPipeline.cs:161` 的降位归属（实际 swscale 做 10/12-bit，
   且既有实测已证明该步取整无偏、参数不起作用 ⇒ **只改注释，别改实现**）、`ColorKernels.cs:165` 的 `FloatToSrgb8` 文案（挂在标量方法上）、
   `ColorSpaceRegistry.cs:555` 的"同原色族"（P1 一并改掉）。
4. **`LookupEncode` 死三元**（`:363` 两分支同式）：`ClampLinearBeforeEncode=false` 的语义在表路径上根本没实现。
   产品当前两处都传 `true`、且 `RawColorPipeline.cs:920` 在不钳位时不建表 ⇒ 潜伏未爆。
   小决策：**实现该分支**（表定义域扩到 >1）还是**删参数**并在不满足前提时显式失败。默认建议后者（少一条静默路）。

---

## P5 收尾：把 colormath 接进清单 + 台账

- **接法**：P1 让它 0 红之后，把 `colormath` 追加进 `_run-step3-gates.ps1:7` 的 `-Probes` 默认值，
  同时改三处散文条数与 `docs/TESTING.md §6` 新条目（运行器第 17 针只比"数组 vs 常量"，**锁不住散文**，这是踩过的坑）。
- 若想在修之前就接：需要显式登记"已知红"（与 `verify-oracle-matrix` 同类处置）⇒ **不建议**，会让整轮失去基线意义。
- 台账：`HANDOVER_2026-09-22.md` §0/§4/§5 对应行改状态，`.workbuddy-ai/memory/` 与项目记忆同步。

**✅ 已接（2026-09-26）**，并且是**先验牙再接线**：

| 步骤 | 读数 |
|---|---|
| 变异前基线 | `colormath pass=12 fail=0` |
| M1 `bt470bg` 的 G 由 (0.290,0.600) 塌回 bt709 的 (0.300,0.600)（= 修复前"多 token 共用一套常数"） | `pass=11 fail=1` ⇒ 红条读数 `实差 4.40E-002` |
| M2 把已**故意不收录**的 `smpte428` 按旧写法补成 `smpte431` 的副本 | `pass=10 fail=2` ⇒ ①`实差 1.99E+000`（≈199% 满量程）②白名单外重复对 `smpte428≡smpte431` |
| 还原 | `sha256=b8175f16ffeb129b…` 与变异前**逐字节一致**（用 `cp` 字节备份还原，不用 `WriteAllText`），重建后回到 `12/0` |

散文三处已同步：`docs/TESTING.md:209`（`tooldetect` 不在默认清单 ⇒ 计数不含它）、`docs/TESTING.md:219`（22 个 mode；**546 的求和仍是前 21 个**，`colormath` 单跑 12/0，合计待整轮）、
`docs/HANDOVER_2026-09-22.md:274`（"探针段从 22 个 mode 降到 1 个"）。接线后运行器 `shape-selfcheck=OK（17 针…）`、
`runner-self-sha256` 由 `9FC8C70FC2F4CAC3` 变为 `FE846C90C18EB85A`（**改了运行器自身 ⇒ 上一轮整轮的二进制绑定结论即失效**）。

---

## 里程碑 2 的可行性已顺手量过（降低后续不确定性）

把 11 个色彩数学文件（`SimdPixelOps` + `ColorMapping/{Bt2100Ootf,ColorKernels,ColorSpaceDescriptor,ColorSpaceIdentify,ColorVerdict,EngineBlock,ToneMapping,TransferCurve}` + `ColorSpaceRegistry` + `IccProfileBuilder`，共 3505 行）
扔进临时 net10 工程独立编：**只有两个类型缺失** —— `DescriptorConfidence`（声明在 `ColorIntent.cs:8`）、`ColorAction`（声明在 `ColorTransformPlan.cs:11`）。
且 **net10 与 net11 的错误集逐条相同** ⇒ 框架下限不是问题（另已实测 `Vector512`/`Avx512BW`/`Half` 在 net8 参照集即可编译）。
⇒ 真正的工作量是"把这两个枚举从耦合文件里挪进纯集合"，不是重设计。
但**顺序上仍排在 P1+P4 之后**：那张原色表一旦成为对外 API，P1 两格就变成对外的错。

## 附录 A｜P1 的双锚实测数据（2026-09-24）

**锚 A = zimg**（`zscale` float 通道，方法见审计 §7）；**锚 B = lcms2**（`iccgen` 生成标准 ICC 后读 `chrm` 的原生色度对；
本构建里 `chrm` 体 = type + reserved + count(=3) + 6 个 s15Fixed16，**只有三原色、不含白点** ⇒ 白点仍须由 `wtpt` + `chad` 逆适应取，未完成）。

| token | 锚 B 三原色 xy（lcms `chrm`） | 本仓表里现在写的 | 判定 |
|---|---|---|---|
| bt709 | (0.640,0.330)(0.300,0.600)(0.150,0.060) | 同 | ✅ **自检基准**，还原精确 |
| bt2020 | (0.708,0.292)(0.170,0.797)(0.131,0.046) | 同 | ✅ 两锚一致（zimg 侧 maxErr 1.1e-6） |
| smpte432 | (0.680,0.320)(0.265,0.690)(0.150,0.060) | 同 | ✅ (4e-6) |
| smpte431 | (0.680,0.320)(0.265,0.690)(0.150,0.060) | 同，白 (0.314,0.351) | ✅ (2.4e-4) |
| **bt470m** | **(0.670,0.330)(0.210,0.710)(0.140,0.080)** | (0.630,0.340)(0.295,0.605)(0.155,0.077) | ❌ 完全不同，两锚同判 |
| **bt470bg** | **(0.640,0.330)(0.290,0.600)(0.150,0.060)** | 同上 | ❌ |
| **smpte170m** | **(0.630,0.340)(0.310,0.595)(0.155,0.070)** | 同上 | ❌ |
| **smpte240m** | **(0.630,0.340)(0.310,0.595)(0.155,0.070)** | 同上 | ❌（与 170m 同原色，两锚都同意这一点） |
| **jedec-p22** | (0.630,0.340)(0.295,0.605)(0.155,0.077) | 同上 | ✅ **反证成立**：表里那组共用常数就是 P22 自己的 |
| film | (0.681,0.319)(0.243,0.692)(0.145,0.049) | 无条目 | ⚠ 能力缺口（现走 fail-closed） |
| **smpte428** | **(0.735,0.265)(0.274,0.718)(0.167,0.009)**（= DCI-P3 影院原色） | = smpte431 的副本 | ❌ **且两锚互相矛盾，见下** |

### ⚠ 未决：两个锚对 `smpte428` 给出**不同**的原色

- 锚 A（zimg）：`pin=smpte428 → bt709` 矩阵 = `[[3.14666,−1.66646,−0.48019],[−0.99552,1.95576,0.03976],[0.06359,−0.21456,1.15097]]`
  ⇒ 与"CIE 1931 XYZ 三角原色（(1,0)(0,1)(0,0)、白 1/3,1/3）"这一 H.273 定义相容。
- 锚 B（lcms2）：`chrm` 给的是 DCI-P3 影院原色 (0.735,0.265)(0.274,0.718)(0.167,0.009)。

⇒ **不许挑一个顺眼的抄进表**（这正是本附录开头立的规矩）。要定这一格必须回 ITU-T H.273 表 1 正文
（本机未 vendor zimg/lcms 源码，`tools/src` 只有 aom/libavif/libjxl/libultrahdr 等）。
在此之前 `smpte428` 保持 **fail-closed**（映射返回 null，不猜）——注意这跟"现在它是 smpte431 的副本"完全不同：
现状是**拿着错的矩阵去乘像素**，改后是**明说不支持**。

### 还缺的两件数据

1. 各 token 的**白点**：`wtpt` + `chad` 逆适应（`chad` 在本构建里是 `sf32`、无 count ⇒ 9 个 s15Fixed16 直接从体首排；
   下一步做，且做完要用 bt709 已知白点 (0.3127,0.3290) 自检）。
2. `smpte428` 的 H.273 原文定义。

取数脚本：`C:\Users\20210\AppData\Local\Temp\p1icc\dump_primaries.py`（临时件；定稿时随 `_probe-*` 落进 `tests/scripts` 才算有锁）。


---

## 需要维护者点头的事（2026-09-24 更新）

**已定案**：P3 = 完全走自研管线（混合态不算数）⇒ 原"P3 选 A 还是 B"作废，改为下面第 5、6 两条新出现的边界。

1. ~~P3 路线~~ ⇒ 定案，见 P3 节。
2. **P2 选项 B**：要不要干脆"能 1:1 表示就不抖"（比居中更彻底，但引入新判据）。
3. **P4.2 收紧识别容差**：会让部分素材退回 unknown ⇒ 能力面变化，要授权；且需先跑统计再定数值。
4. **P4.4 死三元**：实现"不钳位"还是删参数 + 显式失败。
   ⚠ 定案 P3 之后这一条**不再是可选项**：自研 tonemap 必须保 >1.0 余量 ⇒ 默认按"实现该分支"走，
   除非另有指示。
5. **`--color-engine legacy` 的去留**：清退 zimg 后，"显式选传统链"这条用户可见语义要么保留为**明示的旧行为**
   （意味着 zscale 路径不删、只是不再自动进入），要么整条撤销（**能力/声明降级**）。默认建议：先保留为明示语义，
   把"引擎不可走时偷偷退回 zscale"这一支改成硬失败——这既满足"完全走自研"，又不撤用户手里的东西。
   ⚠ #43/#44 的读数给这条添了新事实：**legacy 下三条出口的水位不一样**——avif 走 `FfmpegCommandBuilder`
   （#43 已修好，显式目标能兑现）、jxl 走 `cjxl 直接编码`、jxr 走 `JxrEncApp 两步法`（后两条**吃目标**）。
   ⇒ "legacy 是一条一致的旧管线"这个前提**不成立**，去留讨论要按三条出口分别定。
5b. **#44 的修法三选一（默认建议 ①，另两条各有硬前提）**：
   ① 接**同源登记**（P7a-3 原计划）：让外部工具路也经色彩裁决点，先用既有 legacy 色彩链把**像素**转好再交给
     cjxl / JxrEncApp。工作量最大，但这是唯一"把实现提到声明水位"的路。
   ② **交付端回读对账**（产物标签 vs 请求目标，不符即显式失败）：⚠ 前置未验——本机 `ffprobe` 对 jpegxl
     读不出 `color_transfer`（png/avif 读得出），**读数拿不到就不许写判据**，得先确认 jxlinfo/exiftool 口径。
   ③ **响亮拒绝**该组合：属能力面降级 ⇒ 要授权，且它治不了"标签与像素不符" only 的场合。
   ⚠ 半个修法比现状更坏，已写进任务 #44：直接改走"管道 + `-x color_space`"会让 cjxl **只改容器标签、不改像素**
   ⇒ 从"目标被忽略"变成"宣称 P3 实为 BT.2020/PQ"。任何修法都必须同时证明**像素**落到目标域。
6. **`--color-709-curve Zimg`（纯 γ2.4 对照）撤不撤**：清退后它在生产上不再有语义，只剩历史产物对照价值 ⇒
   撤掉属降级，**不自行处理**。


---

## 2026-09-27 晚间批次收口（dll `7F994E4E93227FFE` → `0DD34999459893F7`）

**这一批做完的东西分三类，别混成一个"还有一堆"的数字：**

**A. 产品侧真改动（两处，都改 `src`）**
1. **#55 契约定案落地**（`7F994E4E93227FFE`）：维护者定案「HDR→SDR 色调映射以自研管线（引擎）为准」。落三件事 —— `ColorEngineRouter.ExplicitlyRequested` 的 XML 文档写明定案与**唯一**允许的偏离（`auto` 因 DNG/动画回落传统管线）；`QueueProcessor` 在该回落时**必须点名**两路契约不同（ASCII 文案 ⇒ cjk UI 面仍 825、余量 12）；`verify-color-engine-xcheck.ps1` 两条新判据（前置「本次不适用引擎」在场 ⇒ 反真空 + 点名文案在场）。变异 = `if(false)` 掉那句 `Log` ⇒ 前置仍 PASS、点名判据 FAIL、`rc=1`。
   ⚠ **明确不做**：把 legacy 的 tone-map 拉平成与引擎数值一致 —— 它是明示的诊断/对拍通路，拉平等于自废。
2. **lane E：面板「SIMD 优化」终于有消费者**（`0DD34999459893F7`，22:31 同批四项目重建 0 警告 0 错误）：新增判据单点 `SimdKernelRouting`，`PsnrCalculator.SquaredDiffSum` 与测试钩子 `SquaredDiffSumForced` **共用同一张分派表**，`SimdPixelOps.FloatToSrgb8` 改走同一开关。默认（开）路径逐位不变（探针 S2/S2b/S3 + S4/S6e 错位负控）。门禁 = 第 57 条 `verify-simd-switch-fallback.ps1` **17/0/1SKIP**；牙 = 把守卫反向 ⇒ **PASS 17→13、FAIL 0→4**，还原后源文件逐字节一致。

**B. 门禁/尺子侧（不改交付，但抓出的都是真问题）**
- **#54 合并取数逐条分诊**：站点 **84 → 0**（判据从"债不得增加"升级成"零未分诊站点"）；补 `reValRead` 变量名第二轮盲区（`$ex`/`$ji`/`$jxlinfo`）当场新冒出 3 处，其中 `verify-color-strategy` 的 jxlinfo 位深读数**确实喂 F1 断言** ⇒ 改 `ExecOut`。修掉这把尺自己造的假红（`-ManagedOnly` 口径共现 21 < 全局下限 30 ⇒ 分档 30/18）。剥标记变异 ⇒ 站点 1 条判红，还原逐字节一致。
- **`verify-decision-delivery` 两处静默错读数**：多行 ffprobe csv 先 `-join` 再 `[int]`（实测动图源两行 `1`/`10` ⇒ 旧尺读成 1010；zh-CN 下 `[int]"1,1"`=11）⇒ 新增 `ProbeInts` 逐行转数取 `Maximum` + 合成两行同值自证 + `-of json` 当独立尺；pix_fmt 由"拼起来再 `-match`"改成逐条流全查。
- **⑩ 段夹具选择**：`tests/output/results/` 是共享出口，色彩矩阵 22:08 放进去一张 4719 B 静帧 ⇒ 20:28 全绿的同一条判据 22:20 单跑变 4 红。改成逐候选量帧数、只接受 ≥2 帧，并打印选中素材。改后 **84/0**（原 81/0）。
- **`verify-prophoto-faithful` 两条死断言**：`-notmatch "Corrupted"` 的词是凭印象写的，实测 exiftool 面对坏 ICC 打的是 `Bad length ICC_Profile`（stdout）/ `Truncated ICC profile`（stderr）⇒ 换实测词形 + **同址正向锁**（把生成的 ProPhoto ICC 截一半嵌进副本，同一把尺必须报出问题）。单跑 **27/0**。
- **#37 lane F**：网格 §6b 稳定拒绝码判据（`Get-StableRejection` + 12 条合成自测 7 红 5 绿 + 载体账回产品源码逐字核对）、§6c LEGACY 24 ⇒ **48 格**（补 png/tiff），quick 层实跑 `PASS=24 FAIL=0 cells=72`、到场账 48、每容器缺席=无、通路账违规 0。那 4 个无产物格全是 #53 已登记的 jxr 广色域分歧 ⇒ 读数不判决。
- **归档三条已腐烂的诊断脚本**（`final-verify.ps1` / `regtest-fix.ps1` / `verify-remaining.ps1` ⇒ `tests/scripts/_archived_diag/`）：它们硬编码 `publish\build\FFmpegPictureUI-dev-x64-full\FfmpegGui.exe`，而这个路径 `pack.ps1` 从不产出 ⇒ 永远跑不到被测物。`run-color-regress.ps1` 里"清单内脚本不存在"由静默 SKIP 改成 **FAIL**。

**C. 仍未做（不粉饰）**
- 本批唯一一次 57 条绑定轮在跑（`tests/output/t60/round57-console.log`）；绿了才谈提交授权。
- `verify-png3-interop` 的"ffmpeg 解码零告警"判据在 locale 污染环境下会**假红**（值只在 stderr ⇒ 不能改只读）。处置属**改判据口径**，等维护者点头。
- #40（av1_nvenc / av1_qsv 位深许可 vs `MapPixFmt` 名不符）本机无 GPU，只登记。
- ⚠ 一条流程账：这一晚我把"验证一项改动"和"给整批绑指纹"混过，出现为单项朝整轮靠、以及起整轮前不建日志目录白等一次通知 —— 已由维护者当面纠正并写进 `docs/TESTING.md §7` 的增量验证口径（改哪跑哪，整轮一批一次）。
