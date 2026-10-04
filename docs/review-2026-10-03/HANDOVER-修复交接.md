# 交接文档：两日改动复查 → 修复推进（中途交接）

- **生成时间**：2026-10-03 07:46 UTC+8
- **交接状态**：用户要求「停止当前任务，打包交接给其他 Agent」。本文件是交接给下一 Agent 的快照。
- **交接人**：主 Agent（Hy3）
- **复查基线**：`e7de117` (v1.6.0-beta3) → `bdd4b0b`，7 提交 / 57 文件 / +5810 行
- **当前仓状态**：所有改动均在**工作区未提交**（遵守「未经实机验证不提交、git add 由维护者执行」纪律）。

---

## 0. 一句话结论

复查阶段**已完成并产出 4 份分线报告 + 汇总**。修复**只推进了一半**：

| 项 | 状态 | 说明 |
|---|---|---|
| P0-2 AVIF 增益图 full_range | 代码完成，**未编译验证** | 上次 `dotnet build` 撞 429 限流未跑成 |
| P1-5 六条门禁假绿 | 子代理改完，**未写报告、未验证** | 子代理撞限流前改了 6 脚本，但 FIX 报告没写 |
| P0-1 CJK 门禁基线 840→821 | **未启动** | `_probe-cjk-hardcode-scan.ps1` 我从未编辑 |
| P1-3 JXL 反函数注释/锚点 | **未启动** | — |
| P1-4 dngtool 公式分歧登记 | **未启动** | — |
| 全量构建 + 门禁回归 | **未启动** | — |

仓库当前有 **7 个未提交改动文件**（含 1 个 C#），均为本阶段产物。已清理子代理留下的 22 个 `tests/scripts/_mut-*.ps1` 临时变异文件。

---

## 1. 复查阶段已交付（无需重做）

`docs/review-2026-10-03/`：
- `README.md` — 汇总，含两条 P0 的实证数据
- `A-核心源码.md` — IsoBmffGainMapWriter / GainMapEncoder / GainMapDecoder / ThumbnailService 复审
- `B-JXL-EXIF-色彩链路.md` — JXL distance 单一真值、EXIF ISO 多标签、RAW→JXL PNG 中转
- `C-门禁脚本可靠性.md` — 六类假绿清单逐条核对
- `D-UI-CLI-双语-账本.md` — csproj/图标/双语/CJK 门禁/文档账本

**复查发现的核心 bug（均已端到端实证，非只读推演）**：

- **P0-1 CJK 门禁基线被自己放松 19 行**：`_probe-cjk-hardcode-scan.ps1:294` 自称「余量 0」，实测 UI 面 821 / 基线 840 ⇒ **余量 19**。变异验证：插入 19 行中文 ⇒ `PASS=14/0 exit=0` 一声不响；插第 20 行才转红。应把 `BASE_CS_UI` 从 **840 → 821**。
- **P0-2 AVIF 增益图 `colr.full_range` 恒写 full**：`GainMapEncoder.cs:621` 调 `Build()` **漏传第 7 参** ⇒ 默认 `true` ⇒ `0x80`。实测仓内 ffmpeg libaom-av1 `color_range=tv`、容器 colr 末字节 `0x00`。**每条 AVIF 增益图主图标签从 limited 被改 full**（Y∈[16,235] 被按 [0,255] 解读 → 黑阶抬起、对比度压缩）。
- **P1-3** `MapJxlDistanceInverse` 注释「严格互逆」与紧邻「±1 量化误差」自我打架；探针锚点 q30/q90 恰是互逆点 ⇒ 结构性抓不到差异。
- **P1-4** `tools/src/dngtool/dngtool.cpp:1392` 仍是旧公式 `(100-q)*15/100`，与 GUI 官方曲线不一致（已知、未登记）。
- **P1-5** 六条门禁有假绿形状（见 §2）。

---

## 2. 修复阶段当前进度（逐条）

### ✅ P0-2 AVIF 增益图 full_range（代码完成，未验证）
**文件**：`src/FfmpegGui/Services/IsoBmffGainMapWriter.cs`（+47 行）
**改动要点**：
- `Build()` 的 `baseFullRange` 默认值 `true` → `false`（yuv420p 约定 limited/tv，与 ffmpeg 实产一致）。
- `PatchColr` 改为**只读继承底图原 `colr` 的 full_range 位**（新增 `ReadNclxFullRange`），仅当底图无 nclx 型 colr 时才用兜底值；并把最终值回传，供 tmap 的 colr 对齐同一份像素。
- 注释写明 2026-10-03 实测证据（ffprobe `color_range=tv`、colr 末字节 `0x00`）与 libultrahdr `avifultrahdr.cpp:435/520` 权威口径。
**⚠ 下一 Agent 必须验证**：
1. `dotnet build src/FfmpegGui/FfmpegGui.csproj -c Release`（四项目都要：主项目 + ServiceProbe / UiTestHost / GainMapTestHost）
2. 实测：ffmpeg 编一张 tv-range AVIF → 走增益图管线 → ffprobe 确认**产物主图 colr 末字节仍为 `0x00`（tv）**，不再是 `0x80`。

### ⚠ P1-5 六条门禁假绿（子代理改完，未写报告、未验证）
**6 文件，合计 +214 / -25 行**（逐文件：colorfmt +18/-1、knob-liveness +72/-12、gainmap-thirdparty +16/-1、jbrd-e2e +18/-1、orientation-rewrap +57/-6、package-smoke +33/-4）。
**预期修复内容**（来自派给子代理的指令，子代理是否如实完成需复核）：
1. `verify-orientation-rewrap.ps1` 读空双假绿（:136-141）+ B 段无下限断言（:161-165）
2. `verify-package-smoke.ps1` 4 条裸否定式日志断言（:95/99/100/129）
3. `verify-encoder-knob-liveness.ps1` 全 undecidable 仍 exit 0（:226-232）+ 崩溃=诚实 误判（:209）
4. `verify-jbrd-e2e:29` / `verify-gainmap-thirdparty:38` / `verify-encoder-knob-liveness:40` 的 `$exe` 版本护栏后被环境变量覆盖可绕过
5. `verify-colorfmt-matrix.ps1` 参照 ICC 缺失降级 WARN 不计 FAIL（:18/49）
**⚠ 缺失与风险**：子代理**未写** `FIX-门禁假绿修复.md`，其变异验证证据我无法确认。**下一 Agent 必须**：
- 逐文件 code review 这 6 个脚本，确认上述修复真的落地；
- **重做变异验证**（改坏→转红、改回→转绿），按本仓铁律每条都要有证据；
- **补写** `docs/review-2026-10-03/FIX-门禁假绿修复.md`；
- 严格检查 **PS5.1 语法**（禁 `?:` / `??` / `?.` / `&&` / `||`），本机默认 PS7，PS5.1 解析错误会整脚本不执行 = 假绿。

### ❌ P0-1 CJK 门禁基线（未启动）
`_probe-cjk-hardcode-scan.ps1` 我**从未编辑**（git status 无此文件）。需：`BASE_CS_UI` **840 → 821**；重写 :276-294 归因段（「+16 来自 ServiceProbe」不成立——实测 +29 TAG 全来自 `src/FfmpegGui`）；变异验证刀口落在 821。

### ❌ P1-3 JXL 反函数注释失真（未启动）
`ImageEncoderArgs.cs` 的 `MapJxlDistanceInverse` 注释改为如实描述「近似互逆（±1）」，探针锚点从互逆点 q30/q90 挪到会暴露差异的点，或改弱性质断言。

### ❌ P1-4 dngtool 公式分歧（未启动）
`tools/src/dngtool/dngtool.cpp:1392` 旧公式登记进「已知刻意未动清单」（保持 GUI / RAW→DNG 体积语义不一致、待决策），避免后续会话重复发现。

### ❌ 全量构建与门禁回归（未启动）
四项目 Release 重建 + 跑受影响门禁集合（`verify-gainmap-thirdparty`、`verify-gainmap-engine`、`verify-encoder-knob-liveness`、CJK 扫描等）确认全绿且不引入新失败。

---

## 3. 仓库当前状态（交接快照）

```
 M src/FfmpegGui/Services/IsoBmffGainMapWriter.cs
 M tests/scripts/verify-colorfmt-matrix.ps1
 M tests/scripts/verify-encoder-knob-liveness.ps1
 M tests/scripts/verify-gainmap-thirdparty.ps1
 M tests/scripts/verify-jbrd-e2e.ps1
 M tests/scripts/verify-orientation-rewrap.ps1
 M tests/scripts/verify-package-smoke.ps1
?? docs/review-2026-10-03/   (5 个报告，复查产出)
```
- **已清理**：子代理留下的 22 个 `tests/scripts/_mut-*.ps1` 临时变异文件（已删除，纯 scratch）。
- **未提交**：所有改动均在工作区，未 `git add` / `commit`。

---

## 4. 下一 Agent 执行清单（建议顺序）

1. **P0-2 验证**：四项目 Release 构建 → 实测 AVIF 增益图产物主图 colr 末字节（预期 `0x00` / tv）。通过则 P0-2 关闭。
2. **P0-1**：改 `BASE_CS_UI` 840→821 + 重写归因段 + 变异验证（插 N 行中文确认刀口）。
3. **P1-5 复核 + 补报告**：code review 6 脚本 → 重做变异验证 → 补 `FIX-门禁假绿修复.md` + PS5.1 语法检查。
4. **P1-3**：修反函数注释与探针锚点。
5. **P1-4**：登记 dngtool 公式分歧进已知清单。
6. **全量回归**：四项目 Release 重建 0 警告 0 错误 + 跑受影响门禁集合全绿。
7. **提交**：验证通过后由维护者 `git add`（本仓纪律：add 由维护者执行，Agent 不自行 add/commit）。

---

## 5. 关键约束（本仓血泪教训，务必遵守）

- **限流**：本次撞 429，重置时间 **2026-10-03 08:00 UTC+8**。构建/工具调用密集时可能再撞，错峰或等重置。
- **NETSDK1060**：别动 `src/FfmpegGui/FfmpegGui.csproj`（本次改动未触发老雷，但动了就有风险）。
- **构建顺序**：改 `src/FfmpegGui` 必须重建四项目，否则 STALE 闸会拦。
- **PS5.1 双宿主**：门禁脚本禁 PS7+ 语法；静态兼容 ≠ 双宿主可用。
- **CJK 门禁**：XAML ≤13 / CsUi ≤**822**，fail-closed 且余量极紧；新增中文必须进 locale json 而非硬编码。
  （⚠ 本行原写 832，已于 2026-10-04 订正为 **822** —— 出处 `tests/scripts/_probe-cjk-hardcode-scan.ps1:339`
  `$BASE_CS_UI = 822`；本文件 §4 第 2 条的 `840→821` 亦已过时，现值同样为 822。）
- **未提交纪律**：未经实机测试不得 commit；`git add` 由维护者执行。
- **变异验证铁律**：任何门禁修复都必须「改坏→转红、改回→转绿」，无证据不算完成。
