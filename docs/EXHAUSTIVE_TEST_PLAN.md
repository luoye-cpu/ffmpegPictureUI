# 穷举组合测试 · 长线规划

> 建立日期：2026-09-19 · 状态：P0 已完成，P1 待启动
> 配套地基：`tests/scripts/_lib-axes.ps1`（参数轴注册表）· `tests/scripts/_lib-oracle.ps1`（独立校验 oracle）

---

## 0. 一句话目标

用**独立于被测实现**的 oracle，对软件**所有可穷举的输入组合**做分层覆盖，锁死三件事：
**参数传递正确**、**四大模式语义正确**、**产出文件正确**。

---

## 1. 三个验收面（可分别判定，互不掩盖）

| 面 | 含义 | 判据（必须可机械判定） |
|---|---|---|
| **A 参数传递** | UI / CLI / IPC 三入口 → 实际命令行或色彩计划 | **逐 token 一致**，且能区分「显式设为默认值」与「未设」 |
| **B 四大模式** | `ColorStrategy` S1–S4 × `ColorEngine` auto/engine/legacy | 语义 + 拒绝规则 + 降级登记三者一致 |
| **C 产出正确** | 像素 / 元数据 / 容器 / 几何 | **外部 oracle 裁决**，不接受「文件非空 / exit 0」 |

⚠ 本仓现状：C 面存在大量「文件非空 / 退出码 0」式弱断言（`verify-widegamut-regression.ps1` 18 格仅验存在性；4 条表格型脚本无 PASS/FAIL 契约）。**升级弱断言是本规划的显式目标之一。**

---

## 2. 参数空间实测（来自 `_lib-axes.ps1`，2026-09-19）

```
Get-CaseSummary  ->  轴 57 / 取值 194 / 单变量用例 194
Group : color 23 · encoder 7 · runtime 7 · anim 6 · metadata 6 · quality 4 · geometry 2 · format 1 · engine 1
Kind  : enum 26 · bool 16 · continuous 14 · path 1
```

**为什么不能做全笛卡尔积**：即便只取「格式 11 × 编码器 5 × 色度 4 × 位深 4 × 色彩空间 13 × 策略 4 × 引擎 3 × 元数据 8 × 动图 4」≈ **10⁷**；再乘质量 / 几何 / GainMap / DNG 即 **10¹²** 量级。
⇒ **物理不可跑**（对照：现有 **48** 条门禁整轮已需 **≈16 min / 949–984 s**；⚠ 2026-09-19 P4-A 接线前为 42 条）。

---

## 3. 分层覆盖策略（替代全交叉）

| 层 | 做法 | 规模量级 | 专抓什么 |
|---|---|---|---|
| **L1 单变量扫描** | 每轴遍历其全部取值，其余取默认 | **194** | 「某取值不可达 / 被静默忽略」 |
| **L2 关键轴对 pairwise** | 正交表，覆盖两两组合 | ~10³ | 交互缺陷（单变量永远抓不到） |
| **L3 语义真矩阵** | **只**在有语义耦合的轴间做全交叉 | ~10³ | 色彩决策 / 编码器语义 |
| **L4 拒绝路径全枚举** | 每个 `BlockCode` / `Degradation.Code` 一条正向 + 一条**负控** | ~60 | 降级与拒绝的「宣称 == 实做」 |

**L3 的三组真矩阵（已确认存在语义耦合）**：
1. `ColorStrategy(4) × 源(3) × 目标(5) × 格式(4) × ColorEngine(3)`
2. `format(11) × encoderBackend(5) × chroma(4) × bitDepth(4)`
3. `metadataMode(2) × strip位(5) × 容器(6)`

---

## 4. 阶段划分

### P0 基础设施 —— ✅ **本轮已完成**

| 交付物 | 内容 | 实测证据 |
|---|---|---|
| `tests/scripts/_lib-axes.ps1` | 57 轴注册表 + L1 用例生成 + 结构自检 19 项 + 3 条负控 | 双宿主 `AXES RESULT: pass=19 fail=0`；变异验证（注入空取值轴）转红→改回转绿 |
| `tests/scripts/_lib-oracle.ps1` | 11 个 oracle 封装，全部返回 `Ok=$true/$false` + `Error`，**无缓存**，带硬超时 | 11/11 可用；双宿主 `PASS=38 FAIL=0`；PSNR 正控 `Infinity/IsIdentical=True`、负控 `52.41`；失败路径 7 个函数全部 `Ok=False` 且不抛异常 |
| 参数空间与缺陷侦察 | 见 §5、§6 | 三路并行侦察，结论均附 `文件:行` |

### P1 参数传递（UI / CLI / IPC）

| 子任务 | 内容 | 交付 | 前置 |
|---|---|---|---|
| **P1-A** | 修 `UiTestHost` 的两条红 + 加硬超时 + 固化 `FFMPEGGUI_PLAN_DIR` 前置 | 宿主全绿 | 见 D2/D3 |
| **P1-B** | UI 控件穷举 → 命令串 **token 级**断言（每控件 × 每取值） | 新脚本 `verify-ui-param-matrix.ps1` | P1-A |
| **P1-C** | **IPC `ExtraOptions` 未应用**（D1）⇒ 先定性、再修复、加回归锁 | 修复 + 门禁 | 定性结论 |
| **P1-D** | 四大模式 UI 映射逐值验证（`IccMode` 单选 → `ColorStrategy`，含 **4→3 折叠**） | 断言集 | — |
| **P1-E** | CLI 三处「静默忽略」修复（D4/D5/D6） | 修复 + 回归锁 | — |

### P2 组合矩阵

| 子任务 | 内容 |
|---|---|
| **P2-A** | 色彩真矩阵（L3-1）：策略 × 源 × 目标 × 格式 × 引擎 |
| **P2-B** | 编码器矩阵（L3-2）：格式 × 后端 × 质量 × 色度 × 位深 |
| **P2-C** | 元数据矩阵（L3-3）：模式 × strip 位 × 容器 |
| **P2-D** | 几何 / 动图 / RAW 专项矩阵（含 DNG 六轴） |
| **P2-E** | 拒绝路径全枚举（L4）：`BlockCode` × `Degradation.Code` |
| **P2-F** | L2 pairwise 正交表（跨组轴对） |

### P3 独立正确性裁决

| 子任务 | 内容 |
|---|---|
| **P3-A** | 像素 oracle：`psnr` / 变换后 `psnr`（zscale）/ `djxl` / `ultrahdr` 独立解码 |
| **P3-B** | 元数据回读一致性：**宣称值 == 产物里真有的东西**（exiftool / jxlinfo / ffprobe） |
| **P3-C** | 感知质量**基线带**（butteraugli / ssimulacra2），用于有损格式的「不低于阈值」判据 |
| **P3-D** | 容器结构合规：PNG chunk（⚠ **需补 CRC**，现有实现不校验）、JXL 码流、AVIF |

### P4 固化

| 子任务 | 内容 |
|---|---|
| **P4-A** | ✅ **已完成（2026-09-19）**：6 条新门禁纳入清单 ⇒ **42 → 48**（第 43~48 条）。6 条**都带自身计数汇总**（`PASS=n FAIL=m` + 显式 `exit 1` / `else exit 0`）⇒ 都不进 `$tableOnly` / `$noSelfSummary`（那两个数组的条数锁 4 / 7 不变）。运行器内部已逐条登记实测读数。⚠ **`verify-oracle-matrix.ps1` 有意不入清单**（5010 条组合 + 独立裁判，实测 1.6~2.0 s/用例 ⇒ 约 2.5 h，远超 `ScriptTimeoutSec 480`），按**手工长跑**执行（`-Layer L1` ≈ 6 min / `-Layer L2` ≈ 5 min / `-Smoke` ≈ 8 s）。 |
| **P4-B** | ✅ **已完成（2026-09-19）**：`HANDOVER` / `COLOR_ENGINE_INTERFACE_PLAN §7` / `TESTING §3.5` / **本文件** 已同步为 48（历史实测记录里的「42」按既有范式加注保留，不改写历史）。⚠ 项目记忆（`.workbuddy-ai/memory/**`）单独维护。 |
| **P4-C** | ✅ **已完成（2026-09-19）**：6 条新门禁**实测合计 32.4 s**（`ui-host` 6.4 / `param-matrix` 6.3 / `strategy-map` 6.1 / `param-defects` 6.3 / `cli-strict` 6.0 / `png-structure` 1.4）⇒ 对整轮（≈950 s）是 **+3.4%**，预算无碍。⚠ 前 5 条各跑一遍**全量** `UiTestHost`（受控冗余 ≈ 24 s），换来「某个分组被静默删掉」能被各自锚点抓住。 |

---

## 5. ⚠ 已发现的缺陷与阻塞项（**均为实测，附证据**）

### D1 ⚠⚠ **IPC 无法表达任何透传轴**（阻塞 P1-C，进而阻塞整个「IPC 入口」面）

`src/FfmpegGui/Services/IpcService.cs:321-377`：`HandleSubmitJob` 只取 `Format` / `EncoderBackend` / `Quality` 建 `FfmpegOptions`（`:337-342`）；
`SubmitJobPayload.ExtraOptions`（`:67`）**反序列化后从未被应用**（全类仅此一处出现）。
⇒ IPC **无法选四大模式、色彩策略、GainMap、元数据等任何透传轴**。
附带：`StatusPayload` 的 `TotalItems` / `CompletedItems` / `FailedItems` **恒 0**（`:70-76`、`:379-400`，未接线）。

### D2 ⚠⚠ **`tests/UiTestHost` 裸跑必挂**（环境耦合，与 §3「PLAN Junction」同族但症状更重）

`tests/UiTestHost/bin/.../PLAN` **不存在** ⇒ `PlanFolderPath` 向上误命中 `C:\PLAN`（假阳性，因仓库恰位于 `C:\PLAN\ffmpegPictureUI`）⇒ 外部工具探测落入**无超时**的扩展路径枚举（`C:\Program Files` + `%LocalAppData%\Programs` + 每个 PATH 目录）⇒ **>13 min 不收敛**（`dotnet-stack` 显示栈恒在 `FileSystemEnumerator.MoveNext`，CPU 178%）。
✅ 设 `FFMPEGGUI_PLAN_DIR=<repo>\publish\PLAN` 后 **6 s 跑完**。
⚠ 对照：`tests/GainMapTestHost/bin/.../PLAN` **存在** ⇒ 它裸跑正常。**两个宿主行为不同纯属该目录差异。**

### D3 ⚠ **`UiTestHost` 两条红的定性**（都不是产品回归）

- **H14 = 宿主自身过期**：`ColorSpaceRegistry.cs:214` 实际签名为 4 参（第 4 参有默认值），宿主用 **3 参** `GetMethod` ⇒ 返回 `null` ⇒ NRE 计 FAIL。
- **B2 = 断言与环境耦合**：UI 有**两个**色度控件 —— 通用 `ChromaCombo`（值 `4:2:0`）与 cjpegli 专用 `JpegliChromaCombo`（值 `420`）；cjpegli 路由**只读后者**（`MainWindow.xaml.cs:1417`）⇒ 通用下拉在 cjpegli 路由下**不进命令**。断言拿整条命令串查 `420` 才红。

### D4 ⚠⚠ **CLI 负数值被静默吞掉**

`CliParser.cs:274` 判据为 `!args[i+1].StartsWith("-")` ⇒ `--animation-loop -1` 的 `-1` **不被消费**，该键反被置为 `"true"`（`:280`），随后 `int.Parse` 抛错被 `:659` 的兜底 catch **吞掉**。
⇒ **「设了 -1」与「没设」在外部不可区分**。测试与用例生成器必须改走 `--key=value` 内联形式。

### D5 ⚠ **非法取值口径不一致**（测试必须知道每条轴属于哪一类）

- **静默忽略**（`TryParse` 无 else）：`--color-strategy`（`:636`）、`--icc-mode`（`:632`）
- **显式抛错**：`--color-gamut-map`（`:559`）、`--jpeg-gain-map-base-gamut`（`:603`）

### D6 ⚠ **`--bit-depth auto` 静默等于没设**

UI 有 `auto` 下拉项，但 `CliParser.cs:543` 是 `int.Parse` ⇒ 该取值被吞掉，**且无告警**。

### D7 ⚠ **三条「UI 可达但 CLI 不可达」的轴**

`DngJxlDecodeSpeed`（`FfmpegOptions.cs:240`）· `AppendPngExtension`（`:392`）· `--metadata-mode`（**该参数名不存在**；只有 `--strip-metadata` / `--preserve-metadata`，且后者 `:215-219` 会**连带清空 5 个 strip 位**，**不是单变量开关**）。

### D8 ⚠ **CLI 的色彩空间域比 UI 宽 6 个**

`P3 HLG` / `BT.2020` / `DCI-P3` / `Adobe RGB` / `ProPhoto RGB` / `ColorMatch RGB` 在 `ColorSpaceRegistry.Resolve` 内可达，**UI 不暴露** ⇒ 超广色域归一与保真携带分支**只能在 CLI 侧测**。

### D9 ⚠ 其它已实测项

- `--icc-mode` 的 **4** 个值只产出 **3** 个策略（`ColorStrategy.cs:47-53`：`None` 与 `BakeToStandard` 同归 `Recommended`）⇒ 矩阵应折叠为**等价类**，否则会重复劳动。
- `--tonemap-curve` 在引擎路径**必被拦**（`ColorEngineRouter.cs:335`）。
- GainMap 下采样 `1/16` 在 UI 可达且 `RescaleGainMap` **无上界校验**；`EncodeAsync` 的形参默认值 4 在产品路径**从不生效**（调用方恒传 `Math.Max(...,1)`）。
- `-f jpegli` ≡ `-f jpg`（`:150` 处改写）。
- **两个宿主都没有负控** ⇒ 它们自身的正确性无保障。
- oracle 侧：`exiftool` 对 `.jxl` **连 ICC 都读不出**（必须走 `jxlinfo`）；`dngtool -info` 对非 RAW 返回**错误 JSON 但退出码 0**（必须以 JSON 字段判据）；`ultrahdr_app` 无参退出码为 `-1`。

---

## 6. 素材语料现状与缺口

**已有**（`tests/output/sources/`，23 个）：静态 8/16-bit · 灰度 · RGBA · 有损 JPEG（含 ICC+EXIF+**GPS**）· WebP 有损/无损 · AVIF · TIFF · JXL（无损/有损/裸码流/JPEG 重封装）· JXR · 动图 4 种（gif/webp/apng/avif）· HDR（PQ/HLG，md5 已确认互异）· `srgb.icc`。

**缺口**：
- GainMap / UltraHDR 源（`tests/output/gainmap/` 由 `GainMapTestHost` 产出，**属链外遗留产物**且目录在 `.gitignore` 内）
- 透明**动图**（现由 `verify-decision-delivery.ps1` ⑪ 现场造）
- 多帧 AVIF 的帧数守恒素材
- 超广色域源（Adobe RGB / ProPhoto / ColorMatch / DCI-P3）—— 与 D8 对应

---

## 7. 本仓硬约束（每个子 agent 必须逐条遵守）

1. **不提交**、不 `git add`（由维护者执行）
2. **禁止在用户实时桌面上弹窗 / 做鼠标自动化** ⇒ UI 验证走 `UiTestHost`（Headless + 反射）
3. **不改 `src/FfmpegGui/FfmpegGui.csproj`**（会让整个构建坏掉且不可恢复）
4. 每条**新断言必须变异验证**；改断言**必须同时补负控**，不得为了绿而放宽
5. **同一文件同一时刻只允许一个写者**；⚠ 同一条消息里对同一文件发多个 `Edit` 会互覆
6. 长跑**一律前台发起**（显式后台任务约 2 min 被宿主杀）；跑前确认无锁；跑后核对产物完整性
7. `tests/scripts/**` **禁 PS7+ 语法**；新增脚本必须**双宿主实测**（5.1 与 7）
8. 代码串**优先纯 ASCII**（cjk 扫描余量 0，fail-closed）
9. 临时文件一律用**运行级 GUID 目录**（固定目录会被并发同名实例互删 ⇒ 假红）
10. 外部调用失败必须**显式记账**（返回 `$null`/`Ok=$false`），**绝不静默沿用上一轮的值**

---

## 8. 风险登记

| 风险 | 说明 | 处置 |
|---|---|---|
| **R1** | IPC 缺口的**定性结论未知**（设计如此 vs 漏接线） | P1-C 先出定性结论，再决定修不修 |
| **R2** | 门禁整轮已 **16 min**，新增长跑脚本会推高 | P4-C 设时长预算，超预算的脚本不进清单 |
| **R3** | 部分素材链外且位于 `.gitignore` 内 ⇒ **新克隆不可复现** | 与 §8.53 追踪缺口一并处置 |
| **R4** | 两个宿主**无负控** ⇒ 自身正确性无保障 | P1-A 补负控 |
| **R5** | 本规划会新增多个脚本 ⇒ **追踪缺口继续扩大** | 每阶段结束同步 `2026-09-19.md §8.53` 清单 |

---

## 9. 每阶段的交付规范（子 agent 汇报格式）

每个子 agent 必须交付：
1. **文件路径**（且声明只新增/只改这一个文件）
2. **实测输出原文**（不是转述）
3. **双宿主结果**（5.1 与 7 各一行）
4. **变异验证证据**（转红 → 改回 → 转绿，两次输出）
5. **`git status --porcelain` 输出**（证明没碰别人的文件）
6. ⚠ **实测与预期不符之处**（最有价值的一项，必须具体）
7. **不确定项**（明确写「不确定」，不得用猜测填充）

> 反面模式（本仓已有教训）：只汇报「我打算做什么」、把 `exit 0` 当通过、用回显肉眼判断文件内容。
