# 全面无死角测试方案（组合覆盖扩编）

> **日期**：2026-10-07 ｜ **依据**：Lead 实测（`docs/TEST_EXECUTION_LEAD_2026-10-07.md`）
> ＋ 基线（`docs/TEST_REPORT_BASELINE_2026-10-07.md`）＋ 覆盖率审计（`docs/TEST_COVERAGE_AUDIT_2026-10-07.md`）
> **性质**：规划 + 可执行设计。**本轮未改任何 `src/` 与 `tests/` 源码**；凡标「需拍板」的改动等用户点头。

---

## 一、当前真实覆盖面（实测，非估计）

| 面 | 规模 | 读数 | 状态 |
|---|---|---|---|
| ServiceProbe 逐 mode | 46 mode / 9 组 | **37 全绿，826 PASS / 0 FAIL** | ✅ 健康 |
| GainMap 宿主（旧 + 拆 3 工程） | 103 断言 | 旧宿主 `103 通过 / 0 失败 / 0 已知问题`；拆 = **29+59+15 = 103** 逐字一致 | ✅ 拆分无损 |
| UI 组（5 工程，真 `MainWindow`） | 409 断言 | **409/409**（`verify-ui-*` 亦各 409/0） | ✅ 健康 |
| 受管门禁脚本 | 72 条 | 独立跑全绿（除 §6.1 的 1 条环境前置） | ✅ 健康 |
| 实机 CLI 组合矩阵（Lead 自建） | 63 格 | 61 PASS（2 条为我判据写错） | ✅ 新增 |
| 实机 GUI 真窗口 UIA（Lead 自建） | 43 断言 | 37 PASS（6 条为我 AutomationId/时序写错，修正后全绿） | ✅ 新增 |
| **L1/L2/L3 组合用例** | **197 + 171 + 4624** | **从未被执行** | ❌ **最大缺口** |

---

## 二、缺口一：368 条组合用例"写了但没人跑"（最高杠杆）

**事实**（Lead 独立复核，`docs/TEST_EXECUTION_LEAD_2026-10-07.md` §2b）：
- `_lib-axes.ps1`（197 条 L1）、`_lib-matrix.ps1`（171 条 L2 + 5 张 L3 矩阵）**只生成不执行**；
- 唯一执行层 `verify-oracle-matrix.ps1` **不在** `_run-step3-gates.ps1` 的 `$scriptList` 里
  （⚠ 该脚本名只出现在**注释** `:970` / `:1115` —— 用全文 `grep` 会误判为"已在清单"）；
- ⇒ **72 条受管门禁里没有任何一条跑组合用例**。

**Lead 实测：叫醒它即可，且冒烟档极便宜**

| 档位 | 命令 | 实测 |
|---|---|---|
| 冒烟 | `verify-oracle-matrix.ps1 -Layer L1 -Smoke` | **14 s**，`pass=15 fail=0` |
| 放量 | `verify-oracle-matrix.ps1 -Layer L1 -MaxCases 40` | 128 s，`pass=48 fail=4`（4 条见下） |

**接线前必须先修的前提（否则接进来一个恒红门禁）**：
L1 的 `encoder` 轴**缺格式配对** —— `Get-SingleAxisCases`（`_lib-axes.ps1:317`）的
`$base` 不含 `-f`，而默认格式是 **`jxl`**（实测 `--dry-run` 产 `.jxl`）⇒
`-e Cjpegli` / `-e Jxr` / `-e Dng` 必然被产品**正确拒绝**（`所选编码器后端 X 不适用于目标格式 jxl`），
`-f dng` 对非 RAW 输入也被正确拒绝（`失败 (非 RAW 输入)`）。
⇒ 这是**用例合法域声明不完整**，不是产品缺陷（同 `docs/TESTING.md` §6 第 65 条）。

**落地设计（三步，均属 `tests/` 侧）**
1. **修 `encoder` 轴**：改为「编码器 × 其兼容格式」的配对枚举
   （`Ffmpeg/Cjxl → jxl`；`Cjpegli → jpg/jpegli`；`Jxr → jxr`；`Dng → dng` 且**仅对 RAW 源**），
   并给 `dng` 轴固定一个 RAW 夹具（或显式标为「需 RAW 源」而跳过）。
2. **接冒烟进清单**：把 `-Layer L1 -Smoke` 包成一条**带 `PASS=n FAIL=m` 汇总**的门禁脚本接进 `$scriptList`，
   ⚠ 必须带 **pass 下限**（防「整段没跑 ⇒ exit 0」的零断言假绿，见 `docs/TESTING.md` §6 第 105 条）。
3. **条数账同步**：`$expectedScriptCount` + 运行器三种散文形态 + `docs/TESTING.md` §3.5/§6 + `docs/HANDOVER.md`
   （`docs/TESTING.md` §3.5 已列「增删一条门禁要动的账全清单」五处，缺一即假绿）。

---

## 三、缺口二：选项级零覆盖（审计已证，按抓缺陷概率排序）

| 优先级 | 选项 | 现状 | 为什么值得测 | 最小设计 |
|---|---|---|---|---|
| **P0** | `--no-overwrite` | `ps1 ACTIVE=0`、`.cs` 0 | 2026-10-05 才修的**P0 数据损失**，修复点是新代码 | 3 格：目标不存在⇒产出；目标存在+默认⇒覆盖（哈希变）；目标存在+`--no-overwrite`⇒**哈希与 mtime 均不变** + rc≠0 + 点名。**Lead 已手工实测行为正确**，缺的只是锁 |
| **P0** | `--hdr-mode` | `ps1 ACTIVE=0`、`.cs` 0 | 唯一双零的 HDR 开关；与 `--jpeg-gain-map` **写同一持久位** | 4 格两两 + 2 格顺序对调（`gainmap`/`traditional`），断言两者等价且互不覆盖 |
| **P1** | `--strip-time/-camera/-all-exif/-xmp` | 4 个细分开关 ps1+cs 全零 | 隐私承诺；`ExifToolService` 六处真实消费 | 2 容器 × 4 开关 + 1 格全剥；判据读**产物**的对应标签而非命令行 |
| **P1** | `-e Jxr`/`Dng` × 兼容性判定 | `IsCompatibleWith` 5 分支 switch，41 个 live `.cs` 命中 0 | 注释自述防「错标文件」 | 进程内 50 格：`{5 后端} × {13 格式}` 逐格断言「兼容⇒产出 / 不兼容⇒显式拒绝且点名」 |
| **P1** | `L1/L2` 秒级冒烟 | 见 §二 | 让 368 条既有用例开始承重 | 见 §二 落地设计 |
| **P2** | Orientation `6`/`3` | 现有夹具**全是 8** | `ImageGeometry.cs:42-46` 只有两态，`Math.Abs` 抹平 `6` 的负号处**零断言** | 造 8 态夹具，断言显示尺寸 + 旋正像素；重点 `6`/`3` |
| **P2** | `--color-sdr-white`/`-peak` | 断言走对象赋值、非 CLI 驱动 | 设计者自己在 `ToneMapping.cs:53` 标注两量混用风险 | CLI 驱动 4 格，断言峰值换算的目标域读数 |
| **P2** | `--log-format JsonLines` | `ConsoleQueueObserver.cs:203-218` 分支从未执行 | 日志格式是**消费者契约**（门禁自己 grep 日志） | 1 格：`--log-format JsonLines` ⇒ 每行可 `ConvertFrom-Json` 且字段齐备 |
| **P2** | `ui-sweep` 的 `J8` 期望表 | 把 `P3 PQ` 与 `BT.2020 PQ` 当两个可区分载体 | 二者出口 token **逐字相同**（`bt2020/smpte2084/bt2020nc`）⇒ 该格**恒假**（`docs/TESTING.md` §6 第 74 条同族） | **合并为一格**，或每格前复位高级三元组；⚠ **不得**改产品 token 映射 |
| **P2** | `ui-sweep` 的 `J1z` | 环境前置写成硬 `FAIL` | 文案自述 `no hardware encoder item in this environment`；同段 `L1a/L1b` 解析断言**全 PASS** ⇒ 能力是好的 | 改为 `SKIP` + 点名（保留可见性，不谎报红） |
| **P3** | `--jpeg-gain-map-downsample`/`-quality`/`-target-nits` | 是 `_lib-matrix` 的矩阵轴，受管门禁 0 命中 | 设计者认为值得笛卡尔 | 借 §二 的执行层，无需新增 |
| **P3** | `--auto-threads` × `-j` | 运行时分支 + GUI 第二套算法 | 两套算法漂移风险 | 2 格；⚠ **不断言具体核数**（会假红），只断言「线程数随并发变化」的方向性 |

**另记（审计未量化，需补）**：
- `-f dng` **零产品路径覆盖**（`run-pipeline-tests.ps1` 直连 `dngtool.exe`，绕过产品）；
- **半成品清理**零覆盖（与 `--no-overwrite` 耦合）；
- exiftool ~90 字段**未逐字段清点**（登记为未量化项）。

---

## 四、缺口三：组合维度覆盖度对照（src 取值数 vs 测试覆盖）

| 维度 | src 取值 | 已覆盖 | 缺口 |
|---|---|---|---|
| 格式 | **13**（`jpg,jpeg,png,webp,avif,jxl,jxr,tiff,bmp,gif,apng,dng,jpegli`） | 主流 7–9 | `bmp` / `jpegli` / `dng`(产品路径) |
| 编码器 | **5** | 5（但缺「× 兼容格式」配对） | §二 的配对 |
| 色彩策略 × 引擎 | 4 × 3 = **12** | 12（`verify-color-strategy`） | `manual` 缺 primaries-only 格（= **L-1 缺陷的藏身处**） |
| 位深 | **4**（8/10/12/16） | 4 | — |
| TRC / primaries token | 词表 ~**112** case key | 部分 | 见 §三 P1 `IsCompatibleWith` |
| HDR tonemap | **4**（reinhard/hable/mobius/bt2446a） | 4（Lead 矩阵实测 6/6） | — |
| 动图容器 | **5**（gif/webp/apng/avif/jxl） | 5（Lead 实测 5/5） | 帧率/循环/时长/缩放的**组合** |
| GainMap 开关 | 4 | 部分 | §三 P0/P3 |
| 几何 | max-dimension × orientation **8** 态 | 8 态中的 **1** 态（全 8） | §三 P2 |
| 元数据 strip | **6** | 2 | §三 P1 |

---

## 五、执行顺序（按「先修跑法 → 再补锁 → 后放量」）

**第 1 步 · 固化跑法（否则一切读数不可信）**
```powershell
$rd = "C:\PLAN\ffmpegPictureUI\tests\output\_ramdisk"
New-Item -ItemType Directory -Path $rd -Force | Out-Null
$env:TEMP = $rd; $env:TMP = $rd                 # ← 必需（46 处测试代码硬编码 Path.GetTempPath()）
$env:FFMPEGGUI_RAMDISK = $rd                    # ← 必需（产品 GetTempDir()）
$env:FFMPEGGUI_PLAN_DIR = "$PWD\publish\PLAN"   # ← UI 组必需，缺则 exit 98
```
⚠ **必须串行或每门禁独立目录**：把 `TEMP` 指到同一共享目录再并发跑，会造出**新的假红**。
实测（Lead 第 2 轮全量 **21 → 9** 项失败，逐项独立跑全部转绿）：
`verify-gamut-map` 并发 27/9 → 独立 **36/0**；`color-token-normalize` 30/3 → **33/0**；
`hlg-route` 3/3 → **19/0**；`gainmap-engine` → **140/0**；`ffmpeg-heartbeat` → **68/0**；
`format-regression` → **19/0**；`colorfmt-matrix` → `2/0 WARN=4`。
⚠ 已知**两项恒非产品缺陷**，判读时排除：
① `runner`（子进程 loopback 被沙箱挡，环境类别 B）；
② `verify-ui-*` 里的 `J1z`（硬件编码器**环境前置**，文案自述 `in this environment`）。
⚠ **`FFMPEGGUI_PLAN_DIR` 在循环里被覆盖会让宿主 fail-fast 拒绝启动**（exit 98，打印
`FATAL: FFMPEGGUI_PLAN_DIR is not set`）—— 这是产品**有意的安全闸**（防无界工具扫描），
批量跑多个 UI 工程时**必须在每轮内重设**。

**第 2 步 · 修 2 条产品真缺陷（需用户拍板）**
- **L-1**：`ColorIntentFactory.cs:164-167` 只看 `ColorTrc` + `ManualIntent.Create` 零调用点
  ⇒ 建议同时修「判据」与「装配」（只修一个不够，详见 Lead 记录 §2）；
- **L-2**：`diagnostics/Program.cs:37` / `engine/Program.cs:47` 的 `args[1..]` 与探针体 `args[1]` 错位
  ⇒ 统一为传完整 `args`（或探针体改 1-based），并同步探针条数账。

**第 3 步 · 补 §三 的 P0/P1 锁**（每条都要**变异验牙**：改坏 `src/` ⇒ 当场转红 ⇒ 还原）

**第 4 步 · 接线 L1/L2 冒烟**（§二），让 368 条用例开始承重

**第 5 步 · 放量**：`verify-oracle-matrix.ps1 -Layer L1`（197 条，实测 ≈3 s/条 ⇒ ≈10 min），
再择窗推进 L2/L3（⚠ L3 全量 ≈2.5 h，宿主会回收 >≈20 min 的后台任务 ⇒ 用
`-SkipPerMatrix 0,150,300,… -MaxPerMatrix 150` 逐窗推进）

---

## 六、纪律（本仓既定，不得违反）

1. **不得**用「放宽/删除断言」换绿；**不得**用 `Thread.Sleep` 冒充等待（`docs/TESTING.md` §1.2 铁律）。
2. 判「某脚本在不在受管清单」**必须解析数组体**，不能全文 `grep`（注释会喂绿 —— 本仓 §6 第 23/98 条，
   且 **Lead 本轮亲自踩过**）。
3. **首跑读数不得作为结论**：本轮 `ui-sweep` 首跑报 `J8 BT.2020 PQ` 红，连跑 6 次后 6/6 全绿
   ⇒ 瞬态必须复跑确认。
4. 「零断言 ⇒ 红」闸是兜底：新门禁**必须**有 `PASS=n FAIL=m` 形状的汇总行（否则被记「可能没跑」）。
5. 不判红的桶**不许**用 `FAIL` 前缀（运行器会把 `^FAIL` 当失败明细回显）。
6. 增删受管门禁要同步**五处账**（数组 / `$expectedScriptCount` / 运行器三种散文形态 /
   `docs/TESTING.md` / `docs/HANDOVER.md`）。
