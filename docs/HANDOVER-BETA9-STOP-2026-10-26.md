# 交接文档：beta9 出包任务 — 中止快照与完整进度打包

> ⚠️ **续接指针（2026-10-26 09:xx 添加）**：本文件之后的复核与处置见
> `docs/HANDOVER-BETA9-STOP2-2026-10-26.md`（两份子 agent 复核全文存于
> `tests/output/beta9-review-reports/`）。以 STOP2 为准续接，本文件保留为原始快照。

> 生成时间：2026-10-26 08:0x（GMT+8）
> 触发：用户于 08:01 明确要求 **"停止你手里面的任务，打包目前整个对话的内容和任务进度，停止一切操作，只做打包交接文档"**。
> 范围：本文件是对"从 beta8 起后续所有提交 + 工作树未提交改动 → 出 beta9"这一整轮工作的完整打包。
> 证据纪律：凡本会话（2026-10-26 08:0x 实测）亲眼核实的，标 ✅；凡来自上一轮对话摘要、本会话未重跑的，标 ⚠️（待复核）。

---

## 0. 一句话状态

**beta9 的"代码准备"已大体落盘（版本号已改、32+ 文件改动在工作树），但"验证与出包"被用户中止：整轮门禁未跑完且观测到 7 个 bench 失败，出包（pack/验包/L3/提交/tag）一项都未做。所有"已修/全绿"的断言须在恢复后由一次干净整轮复核。**

---

## 1. 当前精确状态（本会话实测）

| 项 | 实测结果 | 来源 |
|---|---|---|
| 版本号 | `<Version>1.6.0-beta9</Version>` | `src/FfmpegGui/FfmpegGui.csproj:19` ✅ |
| 工作树改动 | **33 个文件已修改、未提交**（清单见 §3） | `git status --short` ✅ |
| 整轮门禁任务 | schtasks `\FfmpegGuiGates20261026d` 已终止（Status→Ready，进程树死亡，无 `.gates.lock` 残留），任务定义已删除 | `schtasks /end` + `schtasks /delete` ✅ |
| 整轮日志 | `tests/output/full-round-20261026d.log`（5663 字节）被 `^C` 中断，**未完成 111/111** | 读日志尾部 ✅ |
| 七项目重建 | ⚠️ 上一轮摘要称"0 警告 0 错误重建完成"，本会话未重跑，属待复核断言 | 上一轮摘要 |
| CJK 棘轮基线 | ⚠️ 上一轮称 UI 面 901=基线（余量 0），本会话未重测 | 上一轮摘要 |

---

## 2. 任务目标回顾（为什么有这一轮）

目标：**出包 `v1.6.0-beta9`**，包含：

1. beta8 之后、HEAD 之前的两个提交：
   - `6fc9719` — DNG 读路径改走 DNG SDK（ACR 路线）+ 第 79 条门禁
   - `0b07b1c` — 两图 GainMap 合成收口 + JPG/WebP 改按 BT.601（#84）+ JXR 实机三缺陷（#81–#83），+3310 行，新增门禁 #80–#84
   - （再早的 `732f448` 是 beta8 出包读数写实，不属 beta9 增量）
2. **工作树 33 个未提交改动**（AVIF ICC #73 收口、GainMap 三态、LibRaw 线性修复、CJK 基线 901、判据四处修改等）。

决策（Explore-2 方案 b）：整轮 111/111 是在**工作树**上跑的，出包必须含工作树全部 ⇒ 先修应修项、再出包、再提交。

---

## 3. 工作树 33 个已修改未提交文件（git status 实测）

根目录 / 文档：
- `AGENTS.md`
- `THIRD-PARTY-NOTICES.md`
- `docs/HANDOVER.md`
- `docs/IMPLEMENT_PLAN_2026-10-11.md`
- `docs/TESTING.md`

`src/FfmpegGui/`：
- `CliParser.cs`
- `FfmpegGui.csproj`（版本号 beta8→beta9）
- `MainWindow.xaml` / `MainWindow.xaml.cs`
- `Models/FfmpegOptions.cs`
- `Resources/Locales/en-US.json` / `zh-CN.json`
- `Services/ColorMapping/ColorIntent.cs`
- `Services/ColorMapping/ColorIntentFactory.cs`
- `Services/ColorMapping/ColorStrategyPlanning.cs`
- `Services/ColorMapping/ColorTransformPlan.cs`
- `Services/ColorMapping/TransferCurve.cs`
- `Services/ExifToolService.cs`
- `Services/FfmpegCommandBuilder.cs`
- `Services/GainMapComposeService.cs`
- `Services/GainMapEncoder.cs`
- `Services/IccProfileService.cs`
- `Services/QueueProcessor.cs`

测试 / 工具：
- `tests/projects/ServiceProbe/Program.cs`
- `tests/scripts/_lib-matrix.ps1`
- `tests/scripts/_probe-cjk-hardcode-scan.ps1`
- `tests/scripts/_readback-acr-render.py`
- `tests/scripts/_run-step3-gates.ps1`
- `tests/scripts/verify-acr-route.ps1`
- `tests/scripts/verify-gainmap-compose.ps1`
- `tests/scripts/verify-jxl-probe-timeout.ps1`
- `tests/scripts/verify-prophoto-faithful.ps1`
- `tools/src/dngtool/dngtool.cpp`

> 注意：摘要里说"32 文件"，实测为 **33**（多 `THIRD-PARTY-NOTICES.md`）。以实测为准。

---

## 4. 整轮门禁实测结果（关键：未完成 + 出现 7 个 bench 失败）

`tests/output/full-round-20261026d.log` 尾部观测（按出现顺序）：

- 多数探针 PASS：`wire / verdict / selftest / iccname / plan / curve / matrix / colormath / ootf / hlgwire / bt2446 / decision / engine / runner / settings / procstreams / encoding / ipc / i18n / geometry / metaraw / thumbnail / oog / avifgm / gamutfit` 均 `pass=N fail=0`。
- **`bench` 探针：`exit=1`，`pass=30 fail=7`** —— 7 个性能判据失败，具体：
  - `sRGB→PQ(目标域换算 ⇒ 关表)` 单核 ≥3MPix/s（快路径被合法关闭，实 2.6）
  - `sRGB→P3(矩阵同曲线)` 单核 ≥40MPix/s（快路径适用，实 28.5）
  - `BT.2020→sRGB(含裁切)` 单核 ≥40MPix/s（实 29.5）
  - `PQ→sRGB(变曲线+裁切)` 单核 ≥40MPix/s（实 34.7）
  - （重复一条 sRGB→PQ）
  - `8b PQ→sRGB（HDR 源走主力 8-bit 交付）` 单核 ≥25MPix/s（实 21.4；日志附注"修前实测 16.4~22.7，白算的 FromLinear 一回来就红"）
- 随后 `verify-ps-compat PASS=13`、`verify-color-caps PASS=16`、`verify-color-wiring PASS=153`，然后日志末尾出现 **`^C`**（用户中断）。

### ⚠️ 与上一轮摘要的关键矛盾（必须带入下一手）

上一轮摘要声称："早前 5 条红全处置：contract 160/0、jxl-probe-timeout 51/0、matrix-structure 38/0、mirror-parity 85/0、**bench 本轮 37/0（反证为负载抖动，两轮互为对照臂）**"。

但 **20261026d 日志实测 `bench exit=1 pass=30 fail=7`**。两种可能：
- (a) 摘要里的"bench 37/0"来自更早的一轮（非 20261026d），且本会话未复核；
- (b) 20261026d 确实出现 7 个 bench 失败，摘要的"全绿"结论需推翻重验。

**交接裁定：bench 是否达标在 beta9 视角下属【未决项】**。整轮被 `^C` 中断、111/111 不成立；即便其余探针 PASS，也不能据此宣布全绿。必须由一次干净整轮确认 bench 是真实回归还是负载抖动。

---

## 5. 已落地改动清单（来自上一轮摘要，⚠️ 本会话未重跑复核）

- `FfmpegGui.csproj`：`<Version>1.6.0-beta8</Version>` → `<Version>1.6.0-beta9</Version>`（CRLF 不变）✅ 实测已改。
- `ColorIntentFactory.cs`（CRLF）：新增【唯一真值】注释 + `public static bool ExitWritesIccFor(string fmt) => fmt != "avif";`；装配处 `ExitWritesIcc = fmt != "avif"` → `ExitWritesIcc = ExitWritesIccFor(fmt)`。
- `tests/projects/ServiceProbe/Program.cs`（LF）：MakeIntent 改用 `ExitWritesIccFor(fmt)`；C5 注释改"两侧必须独立"并独立推导 `expect`。
- `verify-acr-route.ps1`：⑦/⑧/⑨ 锚点后各加一条 CK 读下发点日志（断言 28→31）。
- `_probe-cjk-hardcode-scan.ps1`：`$BASE_CS_UI = 901`（原 885），带换回法归因注释。
- `verify-jxl-probe-timeout.ps1`：`$probe2MaxSec = 30`（原 15）+ ①-⑦ 源码形态自检。
- `_lib-matrix.ps1`：行锚 `:2386` → `:2400`（C 批净 +14 行）。
- `docs/IMPLEMENT_PLAN_2026-10-11.md`：追加 §49.8（五小节）；`docs/HANDOVER.md`：追加 H-9~H-13。

### 上一轮"自述与事实矛盾"修复（⚠️ 待复核）
- `Program.cs:1883` 曾抄第二份 `fmt != "avif"`，与 `:2190`"读产品装配值"注释矛盾 ⇒ C5 对产品回退无牙。修复为抽 `ExitWritesIccFor` + 探针调产品真值 + `expect` 独立推导。
- 变异验证（上一轮做）：产品真值改 `=> true` ⇒ contract **158/2（红）**；还原 ⇒ **160/0（绿）**。本会话未重跑该验证。
- 第 79 条 ⑦⑧⑨ 下发点断言缺口：变异（RawService 下发恒 rec2020）证明决策日志仍 PASS、新 ⑦ 断言 FAIL ⇒ 缺口实测、新断言有牙。本会话未重跑。

---

## 6. 环境坑（务必带入下一手，避免重踩）

- **55 分钟后台长任务会被基础设施杀**（exit `0x40010004` / 1073807364，无 END）⇒ 改用 `schtasks` 一次性任务脱离 WorkBuddy 进程树启动。
- **异常终止残留 `.gates.lock`** ⇒ 确认占用 pid 死亡后手工删（本会话已确认无残留）。
- **TMP 重定向**到 `tests/output/_tmp`。
- **沙箱注入致 `dotnet test` 必失败**（testhost 加载 hostfxr.dll 报 0x80070005）⇒ 同样用 schtasks 绕；`dotnet build` 不受影响。
- **EOL 约定**：多数 `.ps1` 与 `Program.cs` 纯 LF；`ColorIntentFactory.cs`、`FfmpegGui.csproj` 为 CRLF。编辑一律用 Python 字节级替换保 EOL，禁止混用。
- **STALE 闸**：运行器 `_run-step3-gates.ps1`（`expectedScriptCount=84`）要求**七项目**全部重建（非四项目），源码 mtime 必须早于二进制；子集自陈"N/84 ⇒ 不构成基线"。

---

## 7. 判据红线（不可为出包而破）

- 不许放宽判据换绿；判据必须"改回旧行为会红"（变异验证 / 反真空 / 负控）。
- 同一公式/映射不许有第二处副本（JXL distance 曾 5 处副本的教训）。
- 失败/回退必须点名（P9 型静默丢弃坑）。
- 抬基线（CJK 棘轮）必须用**换回法**实测归因（`git archive HEAD` 导出仓库外跑探针），禁止为撑棘轮改代码。

---

## 8. 遗留未决项（来自上一轮摘要，本会话未处理）

1. 账本 §47.9 称"#81–#84 未跟踪"与提交事实矛盾 → 待修文档。
2. CJK 注释算术（880 vs 881）矛盾 → 待修。
3. B3-1 默认（不带 `-e`）臂后端由 `cjpegli`/`cjxl` 变 `ffmpeg` → 需作者确认/登记。
4. 第 79 条 ⑦⑧⑨ 下发点断言（已在 `verify-acr-route.ps1` 加 CK，但整轮未验证）。
5. C5 副本修复（ExitWritesIccFor）变异验证未在本会话重跑。
6. **【新增·本会话发现】`bench` 在 20261026d 实测 7 失败**，与"bench 37/0 全绿"摘要矛盾 → 必须由干净整轮裁定。

---

## 9. 出 beta9 的精确下一步（待用户恢复后执行，本会话未做）

1. 复核工作树 33 文件 diff 与意图一致（见 §3 清单）。
2. **重跑一次干净整轮**（schtasks 脱离进程树，≥60min 预算），确认 111/111；**重点盯 `bench` 7 失败是真实回归还是抖动**。
3. 若 bench 确为回归 → 定位并修；若为抖动 → 两轮对照臂互证后放行（不可仅靠"上一轮说全绿"）。
4. 补齐 **CHANGELOG**，覆盖三段：DNG/ACR 路线、GainMap+JXR+BT.601、AVIF ICC。
5. 修 §8 文档矛盾（账本 §47.9、CJK 算术、B3-1 后端登记）。
6. `pack.ps1 -Mode all` → `verify-release-package` → L3 五套件；`publish/PLAN` 升 beta9（beta8 会就地覆盖，须升）。
7. 提交工作树 33 文件 + 两个提交 → 打 tag `beta9`（git add 由维护者执行）。

---

## 10. 本会话已执行 / 已停止的操作清单

已执行（仅"停止 + 核查 + 写文档"范畴内）：
- 终止 schtasks `\FfmpegGuiGates20261026d`（Status→Ready，进程树死亡，无 `.gates.lock`）。
- 删除该一次性任务定义。
- 只读核查：git 状态、版本号、进程、锁、整轮日志尾部。

**未执行（遵守用户"停止一切操作"）**：任何 `dotnet build`、整轮门禁重跑、`pack.ps1`、`verify-release-package`、L3 五套件、git 提交、打 tag。

---

## 11. 决策与上下文索引

- 用户主指令（本会话）：停止任务、只出交接文档。
- beta9 设计决策来源：`docs/IMPLEMENT_PLAN_2026-10-11.md` §49（尤其 §49.8）、`docs/HANDOVER.md` H-9~H-13。
- 门禁体系：`tests/scripts/_run-step3-gates.ps1`（84 受管脚本 + 27 探针 = 111 条）。
- CJK 棘轮：`tests/scripts/_probe-cjk-hardcode-scan.ps1`（`$BASE_CS_UI`，换回法归因）。
- 单一真值：`ContainerYuvMatrix`（`ColorSpaceRegistry.cs:671`，BT.601）；`ExitWritesIccFor`（`ColorIntentFactory.cs`，AVIF 静态出口写不进 ICC）。
