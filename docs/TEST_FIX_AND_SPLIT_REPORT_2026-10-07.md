# 完整修复 + 测试项目细分 交付报告（2026-10-07）

> **范围**：① 修复上一轮定性的全部缺陷；② 把测试项目**继续细分**到「一个 mode 一个文件」；
> ③ 把组合覆盖从「367 条用例休眠」推进到**可执行**。
> **纪律**：所有读数来自实测；改动只落在 `src/` 的必要修复与 `tests/` 的测试面；
> 凡属行为变更的都在本文件里逐条说明理由与验收方式。

---

## 一、修复清单（全部实测验证）

### 1.1 L-1：`manual` 策略 + 仅 `--color-primaries` 被误拒（**两层**）

**用户可见症状**：命令被拒、零产物，且提示语说「未提供任何目标色彩描述」——
而用户**明明给了** primaries。

**第一层（判据）**：`ColorIntentFactory.cs` 的 `userTargetExplicit` 在
`HasExplicitColorTarget`（CP **或** CT 非空）成立时**只**看 `ColorTrc`
⇒ 只给 `--color-primaries` 时 `targetExplicit=false` ⇒ `it.Target=null`
⇒ `PlanManual` 立即 Reject。
**修**：改为「**CP 或 CT 任一在位即算显式目标**」（CP 与 CT 同属目标轴，
`FfmpegOptions.HasExplicitColorTarget` 的定义本身就把二者并列）。

**第二层（决策出口语义）**：`FfmpegCommandBuilder.Decision.cs` 的高级分支
同样是 `if (advanced && ColorTrc 非空)` ⇒ 只给 primaries 时**整条分支被跳过**，
落进"源语义基线"⇒ 产物标注**仍是源的 P3**（`smpte432`），
即「命令被接受、但用户要的 bt2020 一个字节都没交付」——**静默不交付**，比第一层更隐蔽。
**修**：判据改为「CP 或 CT 任一在位即进高级分支」，缺失的一半按既有回退取值。

**第三层（描述符半缺补齐，必要配套）**：放宽判据后，**只给一半**的输入会走到
`ColorSpaceDescriptor.FromCicp(tp, tt)`，而它要求**两个都能命名**
（任一 null ⇒ 返回 null，见 `ColorSpaceDescriptor.cs:76-79`）
⇒ 报 `目标语义 bt2020/ 无法命名`、exit 90。
**修**：用**用户给的另一半**补齐再试（色域取用户值、传递函数回退到源），
仍不可命名才拒绝；两个方向都补（实测：只补一侧 ⇒ `--color-trc pq` 仍红）。

**验收**（实机读产物标注，第三方 `ffprobe`/`jxlinfo`）：

| 输入 | 修复前 | 修复后 |
|---|---|---|
| `--color-strategy manual --color-primaries bt2020` | **rc=1、零产物**、报"未提供目标" | **rc=0**，产物 `color_primaries=bt2020` |
| `--color-primaries bt709` | 同上 | rc=0，`bt709` |
| `--color-primaries smpte432` | 同上 | rc=0，`smpte432` |
| `--color-trc smpte2084` | 同上 | rc=0 |
| `--color-trc linear` | 同上 | rc=0，`jxlinfo` 报 **Linear transfer function** |
| 两者都给 / `--color-space` | rc=0 | rc=0（**无回归**） |

⇒ **不止"放行"，而是"真兑现"**：`primaries` 与 `trc` 的值分别被独立验证生效。

### 1.2 L-2：物理拆分后 `args[1..]` 与探针体 `args[1]` 错位一格

`tests/diagnostics/Program.cs:37` 与 `tests/engine/Program.cs:47` 传 `args[1..]`，
而探针体是从旧宿主**逐字抽出**的（下标约定 `args[0]`=mode 名）
⇒ 实参整体前移一格 ⇒ **给足实参也打 `usage:` + `fail=1`**。

**修**：两处改为传**完整 `args`**。**验收**：
```
Tests.Diagnostics.exe psnr <a> <b>     修复后 ⇒ PASS 两图尺寸相同…  PROBE RESULT: pass=2 fail=0
Tests.Engine.exe gamut <jpg>           修复后 ⇒ 进入真逻辑（不再打 usage）
```
⚠ 与 `TESTING.md` §6 第 24 条**不同**：那讲「**不给**实参 ⇒ usage」（正确契约）；
本条是「**给足了**仍 usage」= 拆分引入的回归。

### 1.3 D-②：`WriteRgb48ToPngAsync` / `GainMapEncoder` 未过规范化器

本仓自己在 `ColorSpaceRegistry.cs:368-387` 逐字写明：ffmpeg 只认**自己的**常量名，
不规范化就拼命令行 ⇒ `Unable to parse` ⇒ **退出码 -22、零产物**；
而 `FfmpegOptions.cs:257/262` 的 setter **确实**调了规范化器
⇒ 这两处是**唯一**没过规范化器的公开出口（口径不一致）。

**修**：两处都在 **API 内**过 `NormalizePrimariesToken` / `NormalizeTrcToken` / `NormalizeMatrixToken`。
⚠ **不是**让调用方改传 `bt709`（那属改判据换绿）。
**验收**：`xcheck` 的 SDR 格从**硬崩 `-22`** 变为 **`PSNR=73.19dB` 全绿**（3/3）；
`verify-color-engine-xcheck` 整条门禁 rc=0、`全部通过（白名单豁免 6 格）`（与文档基线一致）。

### 1.4 组合套件「休眠」修复：`encoder` 轴格式配对 + `dng` 前提声明

`verify-oracle-matrix.ps1`（L1/L2/L3 组合用例的**唯一执行层**）**不在受管清单**
⇒ **367 条已存在的组合用例从未被执行**。叫醒后实测 **4 条 FAIL**，逐条定性：

| FAIL | 定性 |
|---|---|
| `L1-encoder-02/04/05`（`-e Cjpegli` / `Jxr` / `Dng`，**无 `-f`**） | **用例合法域不完整**：默认格式是 `jxl`，与后端结构性不相容 ⇒ 产品**正确拒绝**（文案极清楚） |
| `L1-format-12`（`-f dng` 对非 RAW 源） | 同上：产品正确拒绝「非 RAW 输入」 |

**修（都在测试侧，**不动产品**）**：
① 新增 `Get-EncoderCompatibleFormat`（**单一真值**：「后端 ⇒ 兼容格式」配对表），
在 **L1 生成处**下发配对 `-f`；
② 给 `dng` 声明 `Precondition = requires-raw-source`，执行层**点名 SKIP**（不判红、也不静默消失）。

⚠⚠ **修的过程中被结构锁抓到一个我自己引入的缺陷（值得记）**：
第一版把配对写进 `Get-AxisTokenOverrides`（**L1/L2/L3 共用**的 token 表）
⇒ L2 的 `encoder × format` 组合**重复发 `-f`** ⇒ 撞 `_lib-matrix.ps1` 的 **A10 卫生判据**
（`bad=L2-0001:-f,…` 从 0001 一直红到 L3 全部）。
⇒ 配对只下发在 **L1**（那里没有 `format` 轴参与）。**修后 A10 恢复 `pass=8 fail=0`。**
这正是本仓"改一处、另一处静默受害"的典型形态 —— 靠**结构自检当场抓到**。

**验收**：`-Layer L1` 全量 197 条 ⇒ **`pass=191 fail=0`**，2 条**点名 SKIP**（dng 缺 RAW 源），
**无一条假红**。

### 1.5 J8 / J1z：UI 断言的两个设计缺陷

**J1z（环境前置被写成硬 FAIL）**：
原文 `Check("J1z 硬件编码器可选（前置）", hw != null, "no hardware encoder item in this environment")`
—— 失败文案自己就写着 "*in this environment*"，却写成硬 `FAIL`
⇒ 环境不满足时**恒定假红**，且把 `verify-ui-split-union` 的判据 ② 一起带红。
**修**：① 无硬件项 ⇒ `Skip(...)`（点名、计 skip、不判红）；
② 有硬件项 ⇒ 断言**不变量**：`-c:v` 是单个 token 且不含 `⚡`/空格/`—`
（这正是 J1z 当初登记的缺陷形态：图标前缀污染 `-c:v`）。
⚠⚠ **第一版我写成硬编码 `== "av1_nvenc"`，在本机当场红**（实得 `av1_amf`）——
本机 GPU 检测先命中 **amd** 项，而 `av1_amf` 同样合法 ⇒ 我**锁了"某台机器的取值"而不是"不变量"**
（`TESTING.md` §6 第 116 条「判据词形要回工具实测，不能照抄记忆」同族）。
⇒ 改为**形态判据**（非空 ∧ 无空格 ∧ 无 `⚡` ∧ 无 `—`），与既有同族断言 `L1b` 同口径，对机器差异免疫。
**验收**：`ui-sweep` **139/0 skip=0**，连跑 3 次稳定。

**J8（异步复位吞掉赋值 ⇒ 失败格在轮次间漂移）**：
诊断行实测 **`want=P3 PQ comboNow=auto`** —— 赋值被吞掉。
**根因（不是猜测，是实测定位）**：`UpdateFormatCapabilities()` 在 `hasHdr` 变化时
**重建 Items 并把 `ColorSpaceCombo.SelectedIndex` 复位成 0**（`MainWindow.xaml.cs:2245`），
而其触发链含 **fire-and-forget 的 `RefreshEncoderListAsync()`**（`:2051`，async）
⇒ 直接 `cs.SelectedItem = v` 后立刻读，可能读到**被复位后的 `auto`**；
异步落地时机不同 ⇒ 红格在 `P3 PQ` / `BT.2020 PQ` / `J6 HLG` 之间漂移。
**修**：新增 `SelectColorSpaceSettled`（边选边等，直到目标**稳定在位**；等不到就**报红**）。
⚠ 这是**等待正向完成条件**，不是 `Thread.Sleep` 冒充等待（§1.2 铁律）；且**未放宽**任何期望值。
⚠ 产品侧已由 **CLI 反证为正确**：`--color-space "P3 PQ"` 与 `"BT.2020 PQ"` 实测产出**逐字相同**
且都正确的 `zscale p=bt2020:t=smpte2084` ⇒ **不得**为让该格变绿去改产品。

**验收**：`ui-sweep` 整组从 **137/2** ⇒ **139/0**，连跑 3 次稳定；
`verify-ui-split-union` **11/0**（含两条新反控，防改名白名单吞掉断言删除）。

---

## 二、测试项目**二级细分**（用户核心诉求）

### 2.1 为什么还要细分

2026-10-05 做到了「一个**子系统**一个工程」，但工程内部仍是**单个巨型 `*Group.cs`**
（`core` **2437 行**、`diagnostics` **1274 行**）⇒ 改一个 mode 仍要碰整份文件。
本轮做到「**一个 mode 一个文件**」。

### 2.2 成果（5 个巨型文件 → 31 个 mode 文件）

| 工程 | 原文件 | 拆分后 | mode |
|---|---|---|---|
| `core` | `CoreGroup.cs` **2437 行** | **69 行** + `Modes/`×7 | 7 |
| `diagnostics` | `DiagnosticsGroup.cs` **1274 行** | **136 行** + `Modes/`×8 | 8 |
| `geometry` | `GeometryGroup.cs` **1076 行** | **66 行** + `Modes/`×4 | 4 |
| `color` | `ColorGroup.cs` **728 行** | **70 行** + `Modes/`×8 | 8 |
| `engine` | `GamutFit.cs` **693 行** | **53 行** + `Modes/`×4 | 4 |

**关键设计**：类改 `partial` ⇒ **`Program.cs` 一字未动**，所有调用点零改动。
共享 helper（被 ≥2 个 Probe 调用，如 `Check`）**留在原文件**（只此一份）。

### 2.3 验收（三道，全部通过）

1. **逐字性证明**：`Modes/*.cs` 的**每一个代码行**都能在原始备份里找到
   ⇒ **6243 行全部命中、缺失 0**（唯一新增的是工具生成的 ASCII 头注释）。
2. **读数逐字一致**：`core` 332/1→332/1 ｜ `color` 126/0→126/0 ｜ `engine` 82/1→82/1 ｜
   `geometry` 75/1→75/1 ｜ `diagnostics` 82/6→82/6。**断言零漂移**。
3. **结构锁全绿**：组表覆盖 8/0、cjk 14/0、ps-compat 13/0、ui-split-verbatim 20/0。

### 2.4 工具（B 类，有意不入受管清单）

`_gen-mode-split.ps1`（切片）+ `_gen-mode-split-reduce.ps1`（缩减原文件为共享底座）。
⚠ 判据取**权威来源** `Program.cs` 的分派行 —— 第一版用正则匹配 `Run\w+`
会凭空多出 `Capture`/`Cap`/`Task`/`Cli` 四个**伪 mode**（`RunCapture` 这类 helper 被误认）。

### 2.5 过程中修的两把锁（都是"改对了反而红"）

- **`_probe-cjk-hardcode-scan`**：我第一版在生成头里写**中文**注释 ⇒ 该锁立刻报
  `836 ≤ 基线 833`。⇒ 生成头改**纯 ASCII**（该锁的「C# UI 面」桶维护者裁定不得增加，已抬升 6 次）。
  顺带：我的产品修复里新增了 3 处 `"目标"` 字面量 ⇒ 也转红；改为**提成局部常量复用**
  ⇒ 回到 `833 ≤ 833`（**余量 0**，未抬基线）。
- **`verify-ui-split-verbatim`**：本锁要求 17 个 UI 组与旧宿主**逐字一致**，
  而 J1z/J8 是**有意修复**。⇒ 引入**已登记偏离组**（当前恰 1 项 `GroupJ_`，附理由），
  其余 16 组仍逐字比较 + 反控计数 2991 行 + 「命中数 ≥1」防白名单过期。
  ✅ **变异验牙（实测）**：把 `GroupL_` 里一条真断言改名 ⇒ 立刻报 `差异 1 行`、exit 1。
  ⚠ 放行**不减牙**的补偿：断言数由 `verify-ui-split-union.ps1` 独立把关。

---

## 三、未修项（**有意**，需用户拍板）

| 项 | 状态 | 理由 |
|---|---|---|
| `bench` 单核门槛失真 ≈5× | **未改** | 判据失效而非性能回归。处置建议：按当前机器**重新标定**并留实测分布，或降级为只回显；**不得**悄悄改数字 |
| `--no-overwrite` / `--hdr-mode` / 4 个细分 strip 开关 等零覆盖项 | **未加锁** | 已在 `TEST_EXPANSION_PLAN_2026-10-07.md` 给出按抓缺陷概率排序的设计；加锁属增量工作 |
| `verify-oracle-matrix` 接线进受管清单 | **未接** | 本文已完成**前提**（L1 全绿）；接线另行评估耗时（L3 全量 ≈2.5 h，宿主会回收 >20 min 任务） |

---

## 四、全量门禁最终读数

修复 + 细分完成后跑**全量 72 条受管门禁**（串行、带四个环境变量）：

| 轮次 | 失败项 | 说明 |
|---|---|---|
| 本轮**修复前** | **21 项** | 全部为 `%TEMP%` 环境假红（详见 `TEST_EXECUTION_LEAD_2026-10-07.md` §4） |
| 修复中途 | 2 项 | `probe:runner`（环境）+ `verify-ui-split-union`（我的期望值写错） |
| **最终（复跑确认）** | **1 项** | **只剩 `probe:runner`** —— 环境（子进程 loopback 被沙箱挡），已完整定性为非缺陷 |

**最终一轮运行器汇总（逐字）**：
```
===== 运行器汇总：失败 1 项 =====
    probe:runner (exit=1)
⇒ 退出码 1
```

**关键门禁全部转绿**（最终一轮实测节选）：

| 门禁 | 读数 |
|---|---|
| `verify-decision-delivery`（L-1 主要受影响面） | **87 / 0** |
| `verify-color-wiring` | **142 / 0** |
| `verify-color-strategy` | **78 / 0** |
| `verify-color-engine-xcheck`（D-② 修复面） | **全部通过**（白名单豁免 6 格） |
| `verify-gainmap-engine` | **140 / 0** |
| `verify-jbrd-e2e` | **100 / 0** |
| `verify-encoder-knob-liveness` | 19 / 0（活=19、死=0） |
| `_probe-matrix-structure`（A10 被我的中间版本撞红过） | **8 / 0** |
| `_probe-cjk-hardcode-scan` | **14 / 0** |
| `verify-ui-split-verbatim` | **20 / 0** |
| `verify-ui-split-union` | **11 / 0** |
| `verify-ui-host` / `-param-matrix` / `-strategy-map` / `-param-defects` / `cli-strict` | 各 **409 / 0** |
| `verify-usability-e2e` | **84 / 0**（81 格） |
| `verify-ps-compat` | 13 / 0 |

⇒ **产品级回归：0；结构性回归：0。**

---

## 五、环境跑法（**必须**，否则一切读数不可信）

```powershell
$rd = "C:\PLAN\ffmpegPictureUI\tests\output\_ramdisk"
New-Item -ItemType Directory -Path $rd -Force | Out-Null
$env:TEMP = $rd; $env:TMP = $rd                 # 必需：46 处测试代码硬编码 Path.GetTempPath()
$env:FFMPEGGUI_RAMDISK = $rd                    # 必需：产品 PlatformServices.GetTempDir()
$env:FFMPEGGUI_PLAN_DIR = "$PWD\publish\PLAN"   # UI 组必需，缺则 fail-fast(exit 98)
```
⚠ **必须串行或每门禁独立 TEMP**：共享同一目录并发跑会造出**新的假红**
（实测 `verify-gamut-map` 并发 27/9 → 独立 **36/0**）。
⚠ 已知两项**恒非产品缺陷**：`runner`（子进程 loopback 被沙箱挡）、
`verify-ui-*` 的 `J1z`（现已改 SKIP ⇒ 不再红）。
