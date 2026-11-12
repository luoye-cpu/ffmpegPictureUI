# 交接：测试模块化批 5·剩余三项（在途快照，2026-11-13）

> 接续 `docs/HANDOVER-TEST-MODULE-BATCH5-2026-11-12.md`（死支处置已收口入库）。
> 本文是**在途快照**：三文件改动未提交（见 §2），接手人按 §4 验收后提交。
> 本文未跟踪（?? 状态），随 C4 提交一并入库或单独 docs 提交均可。

---

## 1. 会话总账（已完成并入库的两笔）

| 提交 | 内容 | 关键读数 |
|---|---|---|
| `077e5b7` | 批 5·死支处置 + 覆盖探针 fullrun 证据链（清单 104 不动） | 探针 **40/0**（⑥-d 14→0、档②=52）、union **11/0 @473**、registry **9/0** |
| `4873c57` | FFMPEGGUI_* env 路径分隔符归一化（B1/B5 检测一致性家族收尾） | VTD §6 **30/0**、负控按契约红、sln 构建指纹 `AE52F1969040884C`；§3.4 老账（①②③）同批收口 |

上批（批 5 第一窗口）的死支处置细节见 `HANDOVER-TEST-MODULE-BATCH5-2026-11-12.md`（已随 `077e5b7` 入库）。

## 2. 在途：剩余项① C4 per-script 超时表（三文件，未提交）

### 2.1 改动清单（git status 实况）

```
 M tests/scripts/_module-registry.json      （locks.script-timeouts 新增，2 条）
 M tests/scripts/_run-step3-gates.ps1       （装载点 + 查表点 + to= 读数 + ⑲ 针）
 M tests/scripts/verify-module-registry.ps1 （⑤ 判据 + M4 负控）
```

### 2.2 设计（已定稿并实测）

- **单一真值** = `_module-registry.json` 的 `locks."script-timeouts".table`：
  `verify-color-strategy.ps1=1200`、`verify-png-optimizer.ps1=1200`（实测 max 243.8s / 748.6s，×3 余量口径同 §2）。
- **runner**：装载点在 `$scriptList` 定义**之后**（幽灵键校验要对清单；第 ⑲ 针管三形在场）；
  查表点 `$_sTimeout` 传 `-TimeoutSec`；结果行加 `to=<n>s` 读数；表只许**抬**不许紧
  （低于全局默认 = 账本 ⑤ 判红）。
- **⑲ 针**（runner 形态自检第 19 针）：`registry-timeouts=OK`×1 / `=FAIL`×1 /
  `scriptTimeoutTable.ContainsKey`×1 / `-TimeoutSec $_sTimeout`×1 / `超时 $_sTimeout`×1。
  当前形态自检读数：**OK=1 FAIL=1 ContainsKey=1 传参=1 记账=1 装载块=1 装载在清单后=True**（全对齐）。
- **registry ⑤ 判据**：键在册/只许抬/装载+查表点在位（三形对账）；M4 负控 = 删
  `script-timeouts` 键必须被抓红。实测：selfcheck **11/0**（4/4 caught）、positive **10/0**。
- fail-closed：账本读不到/锁缺失/表空/幽灵键 ⇒ runner `registry-timeouts=FAIL` +
  `Exit-WithSummary`（超时表装载失败）。

### 2.3 最后一次抽样轮的归因（runner-smoke2.log，**不要重蹈**）

日志：`tests/output/_tmp/batch5-c4/runner-smoke2.log`。rc=1 有**两个独立原因**：

1. **调用方式（我的错，非 runner 问题）**：`pwsh -File` **不剥单引号**——
   `-Scripts 'a','b'` 把带引号字面量传进 runner ⇒ 两个名字（带 `'` 前后缀）与清单不匹配被拒。
   **正确写法**：`-Scripts "verify-module-registry.ps1,verify-color-strategy.ps1"`（双引号）。
2. `probe:bench 35/2`：§7 已登记的**计时噪声族**（sRGB→P3 ≥40 实 39.6/33.4、8b 地板 25 实
   20.4/15.0、H1≤H2×2.0 实 2.06×；宿主负载相关）。批 4·3+4 收口时同款登记过
   （"子集 rc=1 唯一因 probe:bench 35/2"）。**与 C4 改动无关**，验收口径：受管轮 rc=1 因
   bench 噪声可接受（登记在案即可）。

## 3. 未开始：剩余项② TESTING.md 逐条索引从账本生成

方案骨架（批 5 方案 §2 已立项）：生成脚本读 `_module-registry.json` → 产出
`docs/TESTING.md` 的逐条索引段（marker 对 `<!-- module-index:begin/end -->`）；
漂移闸 = 生成段与现存段逐字节对撞（手工改生成段 = 红）。建议实现为
`tests/scripts/_gen-testing-index.ps1`（生成）+ 漂移检查并入 registry 门禁（⑤⑥），
不新增清单条目。TESTING.md 体量 ~6200 行，索引段放文件头目录区。

## 4. 接手人验收清单（按序）

1. **重跑抽样轮**（修正引号）：
   `pwsh -NoProfile -File tests/scripts/_run-step3-gates.ps1 -Scripts "verify-module-registry.ps1,verify-color-strategy.ps1"`
   期望：`registry-timeouts=OK（2 条：…）`、19 针 OK、`[verify-color-strategy.ps1] … to=1200s`、
   rc=0（或 rc=1 因 probe:bench 噪声——登记即可）；`[verify-module-registry.ps1] … 11/0`。
2. **fail-closed 负控**（临时改 JSON 后必须还原）：备份 `_module-registry.json` →
   删 `locks.script-timeouts` → 重跑（期望 `registry-timeouts=FAIL` + 装载失败退出）→ 还原 →
   `git diff` 确认零残留。
3. **受管基线核验**：`script-list=104 条` 不变；`-Scripts` 传错名仍响亮拒绝。
4. **提交拆分**：C4 一笔（三文件 + 本 HANDOVER + 账本两处，见 §5）；
   剩余项②一笔；AGENTS §7 批 5 条目随最后一笔。
5. **账本三份**：TEST_MODULE_PLAN §4 批 5 行（补 C4/索引两小批）；TESTING.md 批 5 段
   （死支处置 + 分隔符归一化 + C4 + 索引生成）；AGENTS §7 批 5 条目（补剩余半句）。

## 5. 验收口径提醒（本窗口实测强化过的几条）

- 裸跑 runner 前自钉 TMP：`TMP/TEMP/TMPDIR = C:\PLAN\ffmpegPictureUI\tests\output\_tmp\round-tmp`（Windows 风格绝对路径）。
- `-Scripts` 参数必须带 `.ps1` 全名；**pwsh -File 不剥单引号**（本窗口实测踩过，见 §2.3）。
- 探针/coverage 裸跑输出是 GBK：`python decode('gbk')` 或 pwsh 读后解码。
- 提权会话跑受管轮：门禁 85 ⑥④ 红是真读数（托盘黑洞家族，与代码无关）。
- registry 门禁的负控语义：`-SelfCheck` 下 M1–M4 必须全 caught；正向跑不许带 `-SelfCheck`。

## 6. 会话教训（新，本窗口实测）

- **⑲ 针改三处时会自打脸**：装载点/查表点/记账引用三形分别落位，任何一处拼错
  （如 `$_sTimeout` 写成 `$sTimeout`）⑲ 就红——这正是它的价值；先跑形态自检再跑轮。
- **runner 装载点位置**：必须在 `$scriptList` 定义之后（幽灵键校验要对清单）；
  放前面会"装载成功但键全幽灵"（smoke1 的 exit=1）。
- **pwsh -File 的引号语义**：单引号字面量原样传参 ⇒ 逗号分隔的多值参数必须用双引号整体包裹。
- **三臂对照实验的对照臂也要登记**：批 4 现象复现时 A/B/C 三轮读数全要进账本，
  缺一轮接手人就得重跑（本窗口 C 轮当时没跑全，交接时补记）。

---

## 7. 接手轮追记（2026-11-13 同日晚：C4 与剩余项② 均已落地）

### 7.1 交接件里两处缺陷（**已修；勿按 §2.2 原文行事**）

1. **fail-closed 其实是 fail-open（严重）**：§2.2 写「装载失败 ⇒ `Exit-WithSummary`」，而 `Exit-WithSummary`
   的出口是「`$failedItems` 为空 ⇒ 打『运行器汇总：全部通过』并 `exit 0`」⇒ 删掉账本键后整轮**照绿**，
   这正是 §2.2 自己声称要防的「静默退化成全默认」。改法：`Release-GatesLock; exit 2`（与形态自检块同形）。
   实测：删键 ⇒ `registry-timeouts=FAIL (locks.script-timeouts.table 缺失) ⇒ 拒跑 exit 2`、**rc=2**、零脚本执行。
2. **⑲ 针不够（变异验证发现）**：原五形只钉到「记账行 `超时 $_sTimeout`」。把结果行 `to=` 的 `-f` 实参换回
   `$ScriptTimeoutSec` 时 **⑲ 全绿** ⇒ 读者看到的超时读数可以静默变回全局默认。现 ⑲ 为**五形 + 位置**：
   `registry-timeouts=OK`×1 / `=FAIL`×1 / `scriptTimeoutTable.ContainsKey`×1 / `-TimeoutSec $_sTimeout`×1 /
   `超时 $_sTimeout`×1 / `$r2.ElapsedSec, $_sTimeout, $tag`×1 / 装载块在 `$scriptList = @(` 之后。

### 7.2 剩余项② 落地（方案 §3 的**一处更正**）

- **段位置 = 文末 §九，不是「文件头目录区」**：`docs/HANDOVER.md:3984` 记载两条门禁按行号引用 `TESTING.md`
  （`_probe-test-group-coverage.ps1` 的 `:1115`、`_probe-cjk-hardcode-scan.ps1` 的 `:1828`），头部插入会把
  这两个锚整体挪位 ⇒ 只许**追加在原文末字节之后**（本轮插入点 = 原文件 699464 B 处，正文行号零位移）。
- 生成器 = `verify-module-registry.ps1 -EmitIndex`（**不新增清单条目、不新增脚本文件**：门禁自己带生成模式，
  省掉第三处登记面）。漂移闸 = 该门禁 **⑥**（`-SelfCheck` 下 M5 变异 = 改账本不重生成）。
- 渲染确定性：分组按 `domains` 声明序、组内按 id **序数**排序、carrier 按 gate 名排序 ⇒ 连跑两次 `-EmitIndex`
  `TESTING.md` 哈希不变（`38C0123358EB8EE2`）。
- **换行符不硬编码（同日补，实文件验证）**：渲染随本文档现存 EOL（CRLF 优先，否则 LF）。原硬编码 CRLF ⇒
  `core.autocrlf` 不同的树（checkout 成 LF）上 ⑥ **恒红且重生成治不好**（生成物又被打回 CRLF）。实测把
  `TESTING.md` 整体转 LF ⇒ ⑥ 仍 PASS（段 8677 B = 8777 − 100 个 CR）、`-EmitIndex` 后 CRLF 计数仍 0；还原
  后哈希相等。

### 7.3 本轮验收读数（受管子集，runner 起；取证 `tests/output/_tmp/batch5-c4/`）

| 项 | 读数 |
|---|---|
| 形态自检 | `shape-selfcheck=OK（19 针…）` |
| 超时表装载 | `registry-timeouts=OK（2 条：verify-color-strategy.ps1=1200, verify-png-optimizer.ps1=1200）` |
| color-strategy | **78/0 @267.1 s**，结果行 `to=1200s` |
| module-registry | **12/0**（正向 11 + `5/5 mutations caught`） |
| 受管基线 | `script-list=104 条（受管基线 104）`；`-Scripts` 打错名 rc=1 点名拒绝 |
| fail-closed 负控 | 删键 ⇒ FAIL 读数 + rc=2；还原后 JSON sha256 与备份**相等** |
| 索引段 | 67 条 / 101 行 / 8777 B；`-EmitIndex` 幂等；手改生成段与「改账本不重生成」两支实文件变异各红 |
| 子集 rc | rc=1 **仅因** `probe:bench` 单核地板噪声（36/1；§7 已登记族，与本次改动无关） |

### 7.4 未做 / 待办

- **全量 104 条整轮未跑**（批 5 全程只跑受管子集）；出包前仍需一次全量基线。
- 提交拆分仍按 §4.4（C4 一笔 + 索引一笔）；本窗口未执行 `git add`（约定：由维护者执行）。
- 账本三处已同批更新：`TEST_MODULE_PLAN §4` 两行、`TESTING.md` 文末段（C4+索引）、`AGENTS §7` 批 5 收口条目。

---

## 8. 全量轮（2026-11-13 03:47–05:47，104 条 / **2h00m53s** / rc=1）

取证：`tests/output/_tmp/fullrun-1113/full3.log`（前两轮分别在 55/104、37/104 被人为中止，见 §8.4）。

### 8.1 结论先行

- **批 5 全绿**：`registry-timeouts=OK（2 条）`、`shape-selfcheck=OK（19 针）`、
  `[verify-module-registry.ps1] 12/0`、`[verify-color-strategy.ps1] 78/0 to=1200s`、
  `[verify-png-optimizer.ps1] exit=0 to=1200s`、`script-list=104`。
- **rc=1 由 8 条构成，全部与批 5 无关**（下表）。其中 3 条是**本窗口当场修掉的**（见 §8.2），
  另有 1 条是**门禁污染产品设置**这一新发现的缺陷（也已修 + 变异验证）。
- **便携 settings.json 在本轮全程未被改动**（`B32A61A8DAEA` 前后相等）⇒ 污染源已封。

### 8.2 本窗口修掉的 4 处（各带变异验证）

| 项 | 根因（证据） | 修法 | 验证 |
|---|---|---|---|
| `verify-startup-warmup` S1/S1b/S8f/S8g 共 10 条红 | `Program.cs:293-303` 自 `e7d1292`（2026-11-11）起追加 ` [PLAN]/[manual]/[app-adjacent]/[extended-search]/[PATH]`；门禁正则要求行尾即 ✅/❌（报告读成 0 行） | 允许**白名单内**标签（不写 `.*`） | 48/0 |
| 同上：**产品设置被污染**（本轮最严重） | S8f/S8g 用 env 覆盖 + `FFMPEGGUI_FFMPEG_DIR`=空目录 ⇒ 子进程 `EnsureFfmpegDirectory` 走重发现分支 ⇒ `Save()` 把**整份 `_current`**（含覆盖值）落盘 ⇒ `DngToolPath=<ffmpeg.exe>`、`WindowsArtifactsDir=<门禁临时空目录>`。时间戳对撞：settings.json mtime `02:54:54` == 该门禁日志 mtime；下游症状 = `verify-metadata-privacy` ⑥「RAW→PNG 未产出 png」，其日志里 dngtool 打 ffmpeg banner | ① 两跑带 `--settings <临时副本>`（`Save()` 写 `_loadedFrom` 且 explicit 分支不回退）；② 新判据 **S8h** 前后对撞便携 settings.json sha256 | 摘掉 `--settings` ⇒ S8h 红且污染**逐字复现**；加回 ⇒ 48/0 且 sha 不变 |
| `verify-ffmpeg-heartbeat` ① | `RunAsync` 调用点 26 → 实 27；第 27 处 = `QueueProcessor.cs:6814`（`e592227` 2026-11-04 Telegram 贴纸降码率重编码，走默认 `heartbeat=true`） | 锁值改 27 + 记账（`git blame -L 6805,6820`） | 68/0 |
| `_probe-matrix-structure` A21×2 / A30×4 | 六处行号锚随 11-11 起三笔 src 批漂移：1156→1329、1168→1341、852→878、2563→2767、804→830、1200→1373（`git log -S` 证字面量自 2026-07/09 起未变 ⇒ 纯行漂移） | 按 found-at 重钉 + 逐条记账（+173/+204/+26） | 38/0（原 36/2） |

### 8.3 剩余 8 红（另起一轮；按根因分类）

| 脚本 | 读数 | 根因/归属 |
|---|---|---|
| `verify-color-engine-xcheck` | 跨仓对撞 fail=1/10（`IsoBmffGainMapProbe` 11 处字段不等：`ref_libavif_solo_gainmapgrid.avif` 宿主读出 tmap+dimg(itemId=4/av01)，**库读 dimg 候选 0**） | **库侧滞后**：`C:\PLAN\ColorGainKit\src\GainMapKit\IsoBmffGainMapProbe.cs` 709 行 / 最后改动 2026-10-12，宿主 1407 行 / 2026-11-11（`9c67701` 修复批 B）。该仓还有 2 个未提交改动（`SimdPixelOps.cs` / `SelfTest/Program.cs`）。**需在库仓处理（工作区外）** |
| `verify-ui-host` / `verify-ui-split-union` | 同一根因：ui-mode **G2 `workers=[] elapsed≈40 s`**（前提「24MP 条目 IsProcessing」30 s 未达成）⇒ 旧宿主 472/1 | 待定位（两处测试代码同形：`uimodeGroup.cs:415` 与 `UiTestHost/Program.cs:979`）。⚠ 本会话为**提权**（High integrity），与「托盘 err=5」同族可疑 |
| `verify-package-gui-starts` ⑥ | 托盘 `Shell_NotifyIcon(NIM_ADD) denied (err=5) — elevated or restricted process context?` | **已登记族**：提权会话读数（文档早有「提权跑受管轮 ⇒ ⑥④ 红是真读数」），与代码无关 |
| `verify-color-token-normalize` | **900 s 超时挂死**（输出停在 R5 之后）；**单跑 52 s / 64 断言全绿** | 待定位：整轮内挂死、单跑不复现 ⇒ 疑与残留进程/文件锁或负载相关（前两轮被人为中止过） |
| `verify-release-package` | `exiftool.exe` / `dngtool.exe` 与 `publish/PLAN` 指纹不等（14/2，包 v1.6.0-beta12） | 出包侧：包内工具树快照落后于 `publish/PLAN`（非本批改动） |
| `_probe-host-temp-sites` | 实扫 41 vs 表 39；`65_ProbeMediaSvc.cs` 新增 2 站点未入表 | 真实漂移：新增 TEMP 站点必须同批进表并写生命周期（该探针的设计目的正是拦这个） |
| `_probe-color-matrix-scan` | `convmatrix_engine` 锚点 miss（`RawColorPipeline` 里 `var convMatrix = …` 形态变了）+ `CjpegliService` 的 `xyb` 命中 6（须 0） | 真实漂移：T6 若已接线 xyb，事实表须重校准；锚点字面量随 src 批变化 |

### 8.4 前两轮为什么废掉（记账，避免重蹈）

- 第 1 轮（02:02 起）：跑到 55/104 被人工中止。**该轮把 settings.json 污染了**（02:54:54），
  所以第 2 轮从起跑就是"dngtool=ffmpeg"的坏环境。
- 第 2 轮（02:57 起）：跑到 37/104 被中止，此时已发现污染。
- 中止后必须手工处理：`.gates.lock` 残留（运行器**不自动删**，只提示「疑似残留锁，请手工确认后删除」；
  本次已按 pid 存活核查后删除）+ 确认无 `pwsh` 门禁进程残留（注意 `Get-CimInstance` 里
  `-Command "…"` 包装进程的命令行会**含**该脚本名，属假阳性）。


## 9. 接手轮追记二（2026-11-13 06:0x–07:0x，8 红处置）

上一轮的 8 红逐条落地，**清单 104 条不动、没有新增门禁**：3 条真漂移修绿、1 条加诊断、2 条不改判据。

### 9.1 已修（含变异验证）

| 门禁 | 根因 | 修法 | 变异/读数 |
|---|---|---|---|
| `_probe-host-temp-sites` | 探针 ① 扫「含 `GetTempPath` 的行」**不剥注释**，同文件 ③（坏根名）早剥了——同一支探针两处口径不一致 ⇒ `65_ProbeMediaSvc.cs:21` 的说明注释被当第 2 个站点 | ① 与 ③ 同口径 + 补表项 `65_ProbeMediaSvc.cs = 1`（`mediasvc_<8hex>`，`:33` 建 / finally 删）+ 更正两处失效行号（`Program.cs:7823-7831` 自批 3 拆分起不存在；`PlatformServices.cs:501-552`→`:645-695`） | 现量 **40 == 40**、5/0；活代码加站点 ⇒ `41 vs 40`+逐文件差 1 红；**仅注释提及 ⇒ 绿** |
| `_probe-color-matrix-scan` | T6（`0f6c11a`，2026-11-11）已在 `CjpegliService.cs:269` 接线 `sb.Append(" --xyb")`，而 ④ 锁「`xyb` 命中==0」——其自身文档写明「T6 接线后重跑会红=预期」 | 按约定**重校准**：锚点重钉 `RawColorPipeline.cs:1023` 新形态；新增证据锚 `cjpegli_xyb_append`；④ 由**计数锁**改**形态锁**（append 恰 1 + auto 守卫行在场）；`report.md` §2/C9 同批改 | **331/0**（原 4 红）；报告里 C9 的证据行现解析到 `CjpegliService.cs:269` |
| `verify-color-token-normalize` | 轮内 900 s 挂死**未复现**（单跑绿）。真缺陷：`Run-Exe` 是裸 `Start-Process -Wait`——**没有 timeout**，一步挂死就只能等运行器收尸，且日志尾部因缓冲丢失 | `WaitForExit(180s)` + `taskkill /T /F` 杀树 + 超时记 `$runTimeouts` + **末尾新增断言**「子进程无挂死」点名完整命令行（`[[GATE-TIMEOUT]]` 前缀） | **65/0 @46 s**（原 64/0 @50 s）；`$runTimeoutSec=1` ⇒ **3 次超时 / 41 s / rc=1** 并打出被挂死的命令行 |
| ui-mode **G2**（连带 `verify-ui-host` + `verify-ui-split-union`） | 轮内 `workers=[]` **不可复现**（单跑 473/0、受管子集 exit=0、改动后再跑 473/0 ⇒ 3 绿 1 红）。结构性缺陷：两个等待**共用** `swG2` 的 40 s 预算，前一个耗满 30 s ⇒ 后一个只剩 ~10 s；旧读数只有 `workers=[]` | 各自独立计时（30 s / 15 s）+ 读数带 `processing/procWait/workerWait/status=[…]/log=[…尾部]` | 变异（条件改 `>100`）实测：`processing=True procWait=69ms workerWait=9ms status=[处理中] log=[…队列开始，并行: 16 个任务…]` |

### 9.2 不改判据的两条（要人拍板 / 跨仓）

- **`verify-release-package`（14/2）= 包过期，不是缺陷**：归档出包 **2026-11-04 09:57**，而 `publish/PLAN`
  的 exiftool 更新于 **11-05 09:54**、dngtool 重建于 **11-09 03:56**。处置 = 下次出包重切；
  顺带更正脚本头那句失效陈述「有意不进受管清单」（`_run-step3-gates.ps1:1797` 实际已登记）。
  ⚠ 这意味着**只要包不是刚出的，整轮就不可能全绿**——要宣布基线必须先决定这条怎么办。
- **`verify-color-engine-xcheck` = 库侧滞后**（工作区外，未动）：宿主 `41375a2`（2026-11-02）把增益图候选
  分 **codec item** 与 **`grid` 派生项**两类；`C:\PLAN\ColorGainKit` 的 `IsoBmffGainMapProbe` 停在
  `79c3f4b`（2026-10-12）只认 codec ⇒ 夹具 `ref_libavif_solo_gainmapgrid.avif` 的增益图是 grid ⇒ 候选 0
  ⇒ 11 处字段不等。修在库侧（需授权）。
