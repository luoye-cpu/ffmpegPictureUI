# 05 — 测试门禁可红性 / 文档数字可追溯 / beta15→16→17 行为漂移

- **变更范围**：`a9e2830..957cfa8`（18 commits，`git log --oneline` 见下）
- **审查日期**：2026-11-16（本机 UTC+8）
- **性质**：只读。未修改任何被跟踪文件；结束时 `git status --short` 仅 `?? docs/review-2026-11-16/review-evidence/`。未 commit/push/tag/出包，未跑 `-FullBaseline`。
- **本路独占 16 文件**：`CHANGELOG.md`、`CHANGELOG.en.md`、`README.md`、`README.en.md`、`docs/INDEX.md`、`docs/TESTING.md`；`tests/projects/UiHostShared/UiHost.cs`、`tests/projects/UiTestHost/Program.cs`(+159/−32)、`tests/projects/ui-color/uicolorGroup.cs`、`ui-mode/uimodeGroup.cs`、`ui-sweep/uisweepGroup.cs`；`tests/scripts/_module-registry.json`、`verify-probe-mirror-parity.ps1`、`verify-startup-warmup.ps1`、`verify-ui-host.ps1`、`verify-ui-split-union.ps1`。
- **范围内提交**：`957cfa8`(release beta17) `ee25a16` `c213a51` `c5f18e7` `eb9bf1d` `c8f20a4` `1ad449c`(release beta16) `39ed867` `a4227a1` `84f5faf`(release beta15) `928ec89` `2b3f0ee` `1a2c0da` `e2b9d2e` `5769b39`。

---

## 0. 结论速览

| 项 | 结论 |
|---|---|
| 最高级别 | **MEDIUM**（无 HIGH / 无 CRITICAL） |
| 条数分布 | MEDIUM ×2，LOW ×4（共 6 条，见 `findings-GATE.csv` id `GATE-01..06`） |
| **split-union 定性** | **(c) 另立一类 —— 旧参照宿主 GroupP 测量不稳定**（非口径漂移 a，非真实回归 b） |
| 门禁可红性 | `verify-probe-mirror-parity` 与 `verify-startup-warmup` 负控均**证到 rc≠0**（见 §4） |
| 文档数字 | beta17 运行读数可追溯；beta16 微基准 / beta15 contract 164 / beta17 全量 5 warnings 为本轮未复现项 |
| zh/en | CHANGELOG beta15/16/17 三段**逐条同义同批**，无中英文漂移 |
| tag↔commit 归属 | beta15↔928ec89、beta16↔39ed867/1a2c0da/e2b9d2e、beta17↔c5f18e7/c213a51/ee25a16，与 `git log` **一致**，无错误归因 |

---

## 1. 核心任务 A —— `verify-ui-split-union` 7 PASS / 4 FAIL 的定性

### 1.1 原始输出（贴名字，不只给数字）

来源：`logs/gate/split-union-1344.txt`（2026-11-16 13:44 人工轮，即 `tests/output/_tmp/oxipng-ui-build/split-union.out.txt`）。

```
[gate] oldNameCount=477  newNameCount=487
old=476 PASS / 1 FAIL (exit=1)   new=487 PASS / 0 FAIL
missing=0  extra=10
extra(新名不在旧并集)=
  P2 每条结论行都以 OK/未找到 收尾
  P3 [avifenc]
  P3 [cjpegli]
  P3 [cjxl]
  P3 [djxl]
  P3 [dngtool]
  P3 [exiftool]
  P3 [JxrEncApp]
  P4 不存在'未知名字却带结论'的行
  P5 对照组：P3 逐名累加必须恰好吃掉全部 7 条结论行
renamedPairs(旧名->新名)=0
waivedOld=[]  waivedNew=[]
===== verify-ui-split-union: PASS=7 FAIL=4 =====
```

4 条 FAIL 的逐条身份（对照脚本 `verify-ui-split-union.ps1` 的 CK 检查）：

| CK | 判据 | 实测 | 结果 |
|---|---|---|---|
| 0 前置 | 仓库安静 | 干净 | PASS |
| **1a** | `oldNames.Count == $expectedOldCount(487)` | oldNameCount=**477** | **FAIL** |
| **1b** | `oldFail == 0` | old=476 PASS / **1 FAIL** | **FAIL** |
| 2a | 五拆分件全组 `newFailTotal==0` | new=487/0 | PASS |
| 3a | `missing==0`（旧名不在新并集） | missing=0 | PASS |
| **3b** | `extra==0`（新名不在旧并集） | extra=**10** | **FAIL** |
| 3a2 | waivedOld.Count == renamedPairs(0) | 0 | PASS |
| 3b2 | waivedNew.Count == 0 | 0 | PASS |
| **3c** | `newNames.Count == oldNames.Count` | 487 vs 477 | **FAIL** |
| 3d | `newNames.Count == $expectedOldCount(487)` | 487 | PASS |
| 4 | 反控两侧名字表非空 | 非空 | PASS |

即 7 PASS = {0, 2a, 3a, 3a2, 3b2, 3d, 4}，4 FAIL = {1a, 1b, 3b, 3c}。

### 1.2 这 10 条的真实出处与早返回机制

多出的 10 条全部是 **P2 / P3×7 / P4 / P5**（启动检测=工具结论行），它们**不是**拆分件独有的新断言——`Grep` 证实同一批 Check 调用在两处都有：
- `tests/projects/UiTestHost/Program.cs:2116/2141/2165`（旧单体）
- `tests/projects/ui-cli/uicliGroup.cs:400/425/449`（拆分件 ui-cli）

关键在旧单体 `Program.cs:2131-2134` 的**早返回**：

```csharp
Check("P1 七个槽位都打出了结论行（槽位数 = 日志行数）", verdicts.Count == names.Length,
      $"★测量失败（不是产品失败）★：用时 {elapsed:F1}s；结论行 {verdicts.Count}、其它 [detect] 诊断行 {otherDetect}、疑似空槽位 {suspect}");
if (verdicts.Count != names.Length) return;   // ← P1 不过，后面 P2/P3/P4/P5 整组不发射
Check("P2 每条结论行都以 OK/未找到 收尾", ...);
...
```

我 14:29 直接运行旧宿主（`logs/gate/old-host-direct.txt`，退出码 1）抓到了那唯一一条 FAIL 的原文：

```text
[FAIL] P1 七个槽位都打出了结论行（槽位数 = 日志行数）
  ★测量失败（不是产品失败）★：用时 45.0s；结论行 0、其它 [detect] 诊断行 0、疑似空槽位 0
```

**不是"检测慢"，而是 45s 内 LogText 里一条 `[detect]` 行都没有**（结论行 0、连 ffmpeg 诊断行都 0）。于是 P1 FAIL → `return` → P2/P3×7/P4/P5 共 10 个名字不发射。旧宿主因此只吐 477 名（P0/P1 仍发射，故不在 missing/extra 里）。这与 13:44 轮的 extra 名单**逐项吻合**。

### 1.3 为什么旧宿主观测不到、而拆分侧能

对比两侧 bootstrap（`UiHostShared/UiHost.cs` vs 旧单体 `Program.cs:119-126`）：

- **拆分侧 `Bootstrap()`**（`UiHost.cs:269`）在 `BootWindow()` **之前**先 `ApplyToolPaths()`：落实 `st.FfmpegDirectory=…/publish/PLAN/ffmpeg-full`、`st.JxlLibDir` 等（`UiHost.cs:297+`）。窗口一 Show，`InitControls()` fire-and-forget 的 `FullDetectionAsync()` 触发时工具路径已就位 ⇒ 7 条结论行正常打出 ⇒ P1 过 ⇒ P2-P5 全发射 ⇒ ui-cli=72/0。
- **旧单体**（`Program.cs:119-126`）是裸 `W=new MainWindow(); W.Show(); Pump(10); GroupP_StartupDetectLog();`，工具路径 historically 要到 GroupG 才设置（`UiHost.cs:274-280` 注释明说旧宿主只在 GroupG 做这三件事）。开窗瞬间检测触发时，宿主没预置稳态 ⇒ 45s 轮询拿到 0 行。

`ui-cli-strict.out.txt` / `verify-ui-host-beta17.out.txt` 证明拆分侧 `ui-startup-detect 12/0`、`verify-ui-host 487/0` 全绿——**产品检测本身没坏**，坏的是旧单体参照宿主的观测前提。

### 1.4 二选一裁决：(c)

| 候选 | 是否成立 | 判据 |
|---|---|---|
| **(a) 口径漂移**（旧宿主/并集统计未同步、locks 未同批抬） | **否** | 三处名字计数真值一致：`verify-ui-split-union.ps1:98 $expectedOldCount=487`；`verify-ui-host.ps1` 五件臂 84+59+103+169+72=**487**；`_module-registry.json` locks `ui-host-arms=487`{84,59,103,169,72}、`ui-split-union=487`。Program.cs 里 P2-P5 的 Check 调用位确实在（不是没 back-port）。数字是同批抬到 487 的。 |
| **(b) 真实回归**（4 条 FAIL 断言在拆分件真跑不过 / 断言被削弱） | **否** | 2a=PASS：五拆分件合计 487 PASS / 0 FAIL，没有任何拆分侧断言失败或被削弱；4 条 FAIL 全是"旧宿主 vs 并集"的**比较类 CK**，不是产品断言。旧宿主那条 P1 自己标注"★测量失败（不是产品失败）★"。 |
| **(c) 另立一类** | **是** | 旧单体参照宿主的 GroupP 观测前提失效（裸开窗、未预置工具路径稳态），导致它在该轮拿 0 条检测行；P1 的早返回设计又把这个**测量失败**放大成"少发射 10 个名字"，进而引爆 1a/1b/3b/3c 四条比较类 FAIL。复现 2/2（13:44 人工轮 + 14:29 直跑）。这是**参照宿主测量不稳定 / 闸结构脆弱**，不是产品回归，也不是锁数没对齐。 |

> 附一个过程层观察：闸头注释里历次抬表（line 47/58/62/…）都写"实测旧宿主 N PASS / 0 FAIL"，唯独 2026-11-14 抬到 487 那批（line 94-97）**没有**"旧宿主 487/0"这句，只引用了拆分侧数字。这与"旧单体观测前提已失效、抬表只按拆分侧证据"一致——是 GATE-01 的成因注脚。

### 1.5 三处计数真值是否同一处

- 断言名字数（487）：union `$expectedOldCount`、ui-host 五件臂之和、registry locks——**三处一致**，是同一真值。
- **477 不是第四个硬编码真值**，而是旧宿主该轮的运行时发射数（P1 早返回的症状），脚本不接受它（1a/3c 据此红）。
- **19**（`verify-ui-split-reproducible`）是另一维度：生成件可复现的文件对数（`tests/projects/ui-*/*.Group.cs` ↔ Program.cs 切片），不是断言名字数，与 487 不同维，不构成"真值不止一处"。
- 结论：**断言名字真值单一（487）**；不存在 473/477/487 三套口径并存。AGENTS.md §7 的"473"是 2026-11-13 的过期日期记录（见 GATE-06）。

### 1.6 runner 重跑（任务要求第 4 步）

按指示用 `_run-step3-gates.ps1 -Scripts verify-ui-split-union.ps1`、TMP 钉 `tests/output/_tmp/review-2026-11-16/gate/tmp` 重跑，被两道闸挡住（`logs/gate/runner-split-union.txt`）：
1. 残留锁 `tests/output/.gates.lock`（已死 pid 26836，13:38 轮），runner 不自动 reap——手工删（锁在产物区、未跟踪）；
2. STALE 新鲜度闸：`FfmpegGui.dll 13:33 < FfmpegGui.csproj 13:49`，`git status` 证明内容未变（仅 mtime 移动，疑并行审查 lane 构建）。

按"勿重编到共享 bin"未重建，故 runner 式 rc 本轮未取。结论以 13:44 人工轮（7P/4F）+ 14:29 旧宿主直跑（476/1，P1=0 行）为准。**未跑 `verify-ui-host` 全量**（已知 487/0）。

---

## 2. 核心任务 B —— 门禁"能真红"负控

| 门禁 | 负控手法（不动被跟踪文件） | 结果 | 证据 |
|---|---|---|---|
| `verify-probe-mirror-parity.ps1` | 在 `tests/_negctrl_probe_dup.cs`（未跟踪、跑完即删）写入第二份 `internal static void ProbeWire(string[] args)` | **基线 89 PASS/2 FAIL → 变异后 87 PASS/4 FAIL，rc=1**。新增红：`FAIL ⑤ 长出第二份定义的 = 1 条（实 ProbeWire@tests/_negctrl_probe_dup.cs）` 与 `FAIL ⑤-z …实 1：ProbeWire`。删文件后 `git status` 干净。 | `logs/gate/mirror-parity-baseline.txt`、`logs/gate/mirror-parity-negctrl.txt` |
| `verify-startup-warmup.ps1` | 临时把未跟踪的构建产物 `src/FfmpegGui/bin/Release/.../FfmpegGui.exe` 移走（Debug 回退不存在） | **rc=1**，输出 `FAIL 缺被测 exe（不静默给绿）`（并落到 Debug fallback 路径）。事后 Move-Item 还原，`Test-Path`=True。 | `logs/gate/startup-warmup-negctrl.txt` |

补充：`verify-startup-warmup.ps1` 脚本自身已登记 M1–M12 共 13 例历史变异（line 28-46，全部当场红），并有内置行为负控 S8f（垃圾 PE 必须 ❌）/S8g（真 PE 必须 ✅）。本路又独立证到"缺 exe→fail-closed"前门有牙。

> 注：mirror-parity 基线本身在 HEAD 就是红的（2 FAIL），那是 GATE-02（contract 102 vs 登记 101），与我注入的负控无关；负控的 delta 是 89P/2F → 87P/4F，正好多出 ⑤ 与 ⑤-z 两条。

---

## 3. 核心任务 C —— 文档数字可追溯 + 行为漂移

### 3.1 CHANGELOG beta15/16/17 数字逐条溯源

| 数字（zh/en 一致） | 可追溯性 | 证据 |
|---|---|---|
| beta17 `verify-ui-host 487/0` | ✅ 可追 | `logs/gate/verify-ui-host-beta17.txt`：`aggregate: 487 PASS / 0 FAIL`、`UIHOST RESULT: pass=487 fail=0` |
| beta17 `ui-startup-detect 12/0` | ✅ 可追 | `ui-startup-detect.out.txt`：`12 PASS / 0 FAIL`、`PROBE RESULT: pass=12 fail=0` |
| beta17 `ui-cli-strict 60/0` | ✅ 可追 | `ui-cli-strict.out.txt`：`60 PASS / 0 FAIL`、`pass=60 fail=0` |
| beta17 `verify-ui-split-reproducible 19/0` | ✅ 可追 | `split-repro.out.txt`：`ui-split-reproducible: PASS=19 FAIL=0` |
| beta17 `build 0 errors / 5 warnings` | ⚠ 部分 | 本证据集 `build.log` 是增量构建（14.55s，仅测试工程）显示"0 警告/0 错误"；"5 warnings"来自全量 Release 构建，本轮未复现 |
| beta16 `2k flush 424ms / 10k 871ms / 50k 2740ms / 隐藏镜像 3996 字符 / 位深缓存 1.09ms` | ❌ 未追到原始读数 | 本轮日志集无对应微基准输出；数字 likely 来自 UI 卡死修复批的专项计时，但本路无法对账 |
| beta15 `contract 164/0` | ⚠ 未本轮复跑 | 164 是 contract 模式**运行次数**；mirror-parity 锁的 101/102 是**源码 Check 调用位**，两种口径不矛盾但不可直接对账 |
| beta15/16/17 包大小 / sha256 | ➖ 该三段本就没写 | 对比 beta13 段有体积+sha16，beta15/16/17 release 段无包哈希；读数落在独立 docs 提交 `a4227a1`(beta15)、`c8f20a4`(beta16) |

zh/en 三段逐条比对：特性描述与运行读数**逐条同义同批**，无中英文漂移。

### 3.2 tag / commit 归属核对

| 版本 | agent-hint 对应提交 | `git log` 实际 | 一致性 |
|---|---|---|---|
| beta15 | `928ec89` | `928ec89 fix(color): 双写 ICC+CICP，静态 AVIF 经 avifenc --icc 承载`（release `84f5faf`） | ✅ 与 CHANGELOG"ICC 双写/AVIF --icc"一致 |
| beta16 | `39ed867`/`1a2c0da` | `39ed867 消除格式切换卡死与日志重排阻塞`、`1a2c0da 收口执行日志前置前缀与共享读`、`e2b9d2e 合并执行日志统一入口与 GPU 检测收口`（release `1ad449c`） | ✅ 与"日志虚拟化/批量写/位深后台探测"一致 |
| beta17 | `c213a51`/`c5f18e7`/`ee25a16` | `c5f18e7 PNG oxipng 独立为格式后处理档位`、`c213a51 用户停止时 ffmpeg 取消不再写成失败`、`ee25a16 稳定 G2 慢任务停止的前置队列状态`（release `957cfa8`） | ✅ 与 headline/停止诚实/ui-mode G2 一致 |

不会把修复归错版本。

### 3.3 `verify-module-registry` 默认对撞（生成物漂移）

`logs/gate/module-registry-default.txt`：**11 PASS / 0 FAIL，rc=0**。
- `entries=67`（= AGENTS §7"模块账本 67"）、`scriptList literals=104`（= "门禁 104"）、五件臂 84/59/103/169/72、`param-matrix-j=126`。
- ⑥ `TESTING.md 索引段与账本一致（段 8777 B；101 行；67 条）`——**生成段无漂移**。
- 仅 AGENTS.md §7 的"473 / ui-color=89"为 2026-11-13 过期日期记录（现值 487/103），属文档 staleness 而非生成物漂移（GATE-06）。

### 3.4 `docs/INDEX.md` 新条目核对

- `review-2026-11-14`（INDEX line 55，标"6"）：磁盘 6 个 .md + 1 个 .csv，与内联列举一致 ✅。
- `review-2026-11-16`（INDEX line 56，标"2"）：磁盘 2 个 .md（DIAGNOSIS/FIX）+ review-evidence/ 工作目录，一致 ✅。
- **`review-2026-11-15`：磁盘存在 3 个 .md（COLOR_MATRIX_IO_ANALYSIS / EXECUTION_LOG_AUDIT / ICC_STANDARD_AUDIT，均 2026-11-15），INDEX.md 全文 grep `11-15`/`EXECUTION_LOG_AUDIT`/`ICC_STANDARD_AUDIT` 零命中** ➖ 漏登记（GATE-03）。

---

## 4. 关键发现（Top 3）

1. **GATE-01（MEDIUM）`verify-ui-split-union` 7P/4F 是旧参照宿主测量失效，不是产品回归也不是锁数漂移。** 旧单体 GroupP 的 P1 轮询 45s 拿到 0 条 `[detect]` 行（"测量失败，不是产品失败"），早返回吞掉 P2-P5 共 10 个名字，连锁引爆 1a/1b/3b/3c。拆分侧 487/0 全绿、三处计数锁一致 487。定性 (c)。
2. **GATE-02（MEDIUM）`verify-probe-mirror-parity` 在 HEAD 基线即红**：contract 源码 Check 调用位实取 102、登记表锁 101（总量 1006 vs 1005）。`928ec89`(beta15) 加了一个 contract 断言却未同批抬表。负控证明该闸的"第二份定义→红"锁有牙。
3. **GATE-03（LOW）`docs/INDEX.md` 漏登 `review-2026-11-15/`（3 篇）**；另有 GATE-04 文档数字 trace 缺口（beta16 微基准、beta17 全量 5 warnings、beta15 contract 164 未本轮复现）与 GATE-05 基础设施（残留死锁 + STALE mtime 挡住 runner 重跑）。

---

## 5. 未验证 / 无法复现清单

- **runner 式 `verify-ui-split-union` rc 未取到**：被残留死锁（已清）+ STALE mtime 闸挡住；按规约未重建共享 bin。改用 13:44 人工轮 + 14:29 旧宿主直跑交叉确认。
- **beta16 五项微基准读数**（424/871/2740ms、3996 字符、1.09ms）本轮无原始日志可对账。
- **beta17 "5 warnings"**：本轮 build.log 为增量构建（0 警告），全量 Release 的 5 条警告未复现。
- **beta15 `contract 164/0`**：未本轮复跑 contract 探针；与 mirror-parity 调用位 102 是不同口径。
- **`verify-ui-host` 全量未跑**（已知 487/0，按指示不重跑）。
- 负控只覆盖本路独占的两条闸（mirror-parity / startup-warmup）；`verify-ui-host.ps1`、`verify-ui-split-union.ps1` 本身的可红性以其内部历史负控注释与本路 1.1 的实测 FAIL 为准。

## 6. 交付物

- 报告：`docs/review-2026-11-16/review-evidence/05-gates-docs-report.md`（本文件）
- CSV：`docs/review-2026-11-16/review-evidence/findings-GATE.csv`（GATE-01..06）
- 日志：`docs/review-2026-11-16/review-evidence/logs/gate/`（split-union-1344.txt、old-host-direct.txt、runner-split-union.txt、mirror-parity-baseline.txt、mirror-parity-negctrl.txt、startup-warmup-negctrl.txt、module-registry-default.txt、verify-ui-host-beta17.txt）
