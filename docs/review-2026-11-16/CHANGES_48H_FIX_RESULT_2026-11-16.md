# 48h 修复结果（`a9e2830..708d8d0`）· 统一交付

**审查基线**：`a9e2830..957cfa8`（18 提交 / 75 文件 / +8777 −1095），裁决见 `CHANGES_48H_REVIEW_RESULT_2026-11-16.md`（P0=0 / P1=3 / MEDIUM=12 / LOW=18 / INFO=1，共 34 条）。
**修复落点**：`main` 上一串未 push 的提交，HEAD 最终为 `708d8d0`（代码）+ 文档批。逐条闭环见 `FINDING_CLOSURE_2026-11-16.md`。

---

## 0. 摘要

| 项 | 结果 |
|---|---|
| P1（HIGH） | **3 / 3 修**（COLOR-01、COLOR-07、MA-01） |
| MEDIUM | **12 / 12 逐条关闭**（含 2 条文档侧、1 条测试侧） |
| LOW | 18 条逐条处置：**修复 12 / 接受 3 / 不改+待办 3** |
| INFO | 1 条不动作（正控通过，CRITICAL 候选不成立） |
| 代码提交 | 11 条（`7695161`…`708d8d0`，另 2 条纯文档） |
| 变更链路门禁 | 全部 **rc=0**，读数绑定 `FfmpegGui.dll` / `ServiceProbe.dll` 指纹 |
| 版本位 | **未动**，仍 `<Version>1.6.0-beta17</Version>` |
| tag | **未动**，`1.6.0BETA17` 仍指 `957cfa8` |
| 出包 | **未出**（按指令停在"可重出 beta17"状态） |

**一句话**：三条 HIGH 的共同成因是"carry 语义在 engine / legacy / 直连三条通路各写一遍"，把判定抽成单一真值后一并消失；两条红门禁不是口径漂移而是断言过期（已按规范重写并留旧读数作证）。

---

## 1. 版本与 tag 状态（用户两次改写策略后的落地）

- 第一次指令要求"修完升 `1.6.0-beta18`、打 tag、出包"；第二次**整体覆盖**为"不升版本位、不打新 tag、不出包，固定 `1.6.0-beta17`，修完停在可重出状态，等明确指令才 `pack.ps1`"。**按第二次执行**。
- `FfmpegGui.csproj` 的 `<Version>` 全程未改；`git tag` 未执行；`CHANGELOG*` 未改（GATE-04 因此推迟）。
- **事实**：两个 beta17 包（app 11.6 MB / full 56.4 MB）产出在 `957cfa8`（旧），而修复链现在在 `957cfa8` 之后 11 个提交。⇒ **包与 tag 指向同一个旧提交，但与当前 `main` 代码不一致**。处置建议见 §7.1。

---

## 2. 提交清单（按时间顺序，均在 `main`、未 push）

| # | 哈希 | 主题 | 覆盖 |
|---|---|---|---|
| 1 | `7695161` | docs：AGENTS §7 三条更正 + INDEX + 闭环表初版 | COLOR-03 / MA-02 / BUILD-07 |
| 2 | `521b318` | fix(color)：抽 `IccCarryPolicy` 单一真值 | COLOR-01 / COLOR-07 |
| 3 | `a15efcd` | test(gate)：decision-delivery 7 断言重写 + mirror-parity 抬表 + 红态闸 | MA-01 / MA-04 / GATE-02 |
| 4 | `d9f19fb` | docs：旧措辞对齐固定 beta17 + RELEASE_GATES §6 | （口径修订） |
| 5 | `17e4c7d` | chore(pack)：robocopy 全路径 / 红态闸接线 / app 档随附 oxipng / AOT 哈希口径 | BUILD-01 / BUILD-02 / BUILD-04 / MA-03 |
| 6 | `55369ff` | fix(color)：HDR→SDR 链末 `iec61966-2-1` + ICC/CICP 数值对撞 | COLOR-08 / COLOR-06 |
| 7 | `991f496` | chore(gate-runner)：死锁自动回收 + STALE 可操作提示 | GATE-05 |
| 8 | `6593af8` | fix(exif)：`perl532.dll` PATH 根因（code 126） | FMT-01（部分）/ FMT-02 |
| 9 | `e5ec98e` | fix(gainmap/deadcode)：nclx 矩阵断言 + HEIC 增益图 sato + alpha 拍平 + JXR 中转目录 | GATE-03 / GATE-06 + 4 条存量红 |
| 10 | `b66cb97` | docs：闭环表落哈希 + LOW/INFO 逐条处置 | （文档） |
| 11 | `49dbd3a` | fix(color)：legacy SDR 直通对齐写 `cICP=1/13` | COLOR-02 |
| 12 | `0be4c4c` | fix(queue)：停止状态单一真值 + 优雅停止/探测取消 | UI-01 / UI-02 / UI-03 / UI-05 / UI-06 |
| 13 | `0d281d9` | test(ui)：oxipng 面板显隐与默认 max 断言 + 五臂计数 487→497 | UI-04 |
| 14 | `708d8d0` | test(ui)：GATE-01 根因修复（旧宿主 GroupP 适配 `UiLogService` 迁移） | GATE-01 |

---

## 3. 逐条处置（34 条）

完整表见 `FINDING_CLOSURE_2026-11-16.md`（34 行，每行含级别 / 处置 / 一句理由 / 落点哈希）。要点：

### 3.1 三条 HIGH —— 一个共同成因
- **COLOR-01 / COLOR-07**：legacy 分支用 `pngCicpFirst` 判断"是否需要自生成 ICC"，而这个标记不看 `DeclaredStrategy`；直连路径又用"字段形状"推断"计划是否承诺 ICC"。两处各自为政 ⇒ 显式 carry→PNG 静默丢 `iCCP`、P3→WebP legacy 直连零 ICC。修法：新增 `Services/ColorMapping/IccCarryPolicy.cs`（58 行，`Skip` / `CarryIccOnly` / `CarryWithCicp` 三态，注释明写**禁止再用字段形状推断**），engine / legacy / 直连三处共用。
- **MA-01**：beta17 发布提交上 `verify-decision-delivery` 78/9、`verify-probe-mirror-parity` 89/2 两红未登记。修法：按新 avif16 路由重写 7 条过期断言（`--depth 12,4` 走 avifenc、legacy 12bit AVIF 期待 fail-closed），mirror-parity 调用位 101→102 / 总量 1005→1006 同批抬表；另立 `docs/RELEASE_GATES.md` + `_release-gate-check.ps1`（红门禁 ⇒ 拒绝出包）。

### 3.2 两条门禁的定性（不是"放宽断言"）
- `verify-decision-delivery` 的 7 条：旧断言按**旧 ffmpeg 位深标记/`-color_trc` 位置**写，`--depth 12,4` 已 fail-closed 91 ⇒ 断言过期，按现行路由重写。**旧读数（83P/1F，唯一红 = ⑮）已留档**，未用"改断言凑绿"。
- `verify-color-wiring` 的 `(1)` 逐字节对撞是**正控，全程未动**；它靠产品侧补 legacy `cICP` 转绿（负控见 §5.4）。

### 3.3 `verify-ui-split-union` 7P/4F 的最终根因（比审查结论更深一层）
审查阶段定性为"旧参照宿主测量失效"，修复阶段坐实**三层叠加**：
1. `UiLogService` 迁移后日志落在 `_uiLogLines` 集合且每行带 `[时间戳] [级别]` 前缀 ⇒ `GroupP` 按 `[detect] ` 前缀分类**恒 0 命中**；
2. 日志 flush 在 `DispatcherPriority.Background`，而 `RunJobs()` 默认只抽 `Normal` ⇒ UI 线程退出后集合里还是空；
3. 开窗前缺 `ApplyToolPaths` 稳态 ⇒ `FullDetectionAsync` 的 Step 2 被 `File.Exists(ffmpegPath)` 前置门挡住。
修后旧宿主直跑 **497 PASS / 0 FAIL**，union 闸 **11P/0F，new=497 old=497**。

---

## 4. 最终门禁读数

<!-- FINAL_READINGS -->

---

## 5. 负控清单（每条"真红"证据）

| # | 对应 finding | 负控做法 | 期望 | 实测 |
|---|---|---|---|---|
| 5.1 | COLOR-01 / COLOR-07 | `IccCarryPolicy.Decide` 首行改 `return Skip` | 复现旧症状 | P3→WebP legacy carry ICC **6366 B / ICC 空**（修后 7008 B 且可读） |
| 5.2 | MA-01 | 喂 `_release-gate-check.ps1` 一份 `rc=1` 的假 json 副本 | exit 1 | exit 1（真 json 全绿 ⇒ exit 0） |
| 5.3 | COLOR-08 / COLOR-06 | ServiceProbe 把产物 cICP 改写 `1/1` 而 `iCCP` 仍 P3 | 判失配 | 判失配（数值对撞真有牙） |
| 5.4 | COLOR-02 | 默认段条件改 `if (false && …)` 重 build | `(1)` 复红 | rc=1 / FAIL=1 / hash 回到 `DD10BC120F39…`（修前值）；还原后 162P/0F |
| 5.5 | GATE-05 | 假锁 `{"pid":999999}`（已死） | 自动回收并继续 | 打印"回收死锁 pid=999999"后继续，contract 164/0 |
| 5.6 | GATE-05 | 假锁 `{"pid":15748}`（活进程 explorer） | 拒跑 | exit 9，"另一个门禁运行器正在跑"，**活锁原样保留** |
| 5.7 | GATE-03 / GATE-06 四条红 | 各自改回旧行为 | 对应断言复红 | 四条逐条复红（详见 `e5ec98e` body） |
| 5.8 | GATE-01 | 修前基线留证 | 7P/4F | 修前 `union.out.txt` **7P/4F rc=1** ⇒ 修后 11P/0F |
| 5.9 | UI-01 / UI-02 | 旧代码形态推断（`Status="已停止/超时"` ≠ `IsStopped` 精确匹配；`_cts` 恒活 ⇒ `IsRunning` 恒 true） | 旧代码必红 | **未实跑负控**——新断言 G2b/G2c 是首次引入，旧代码已不存在于树中；见 §6.2 |

---

## 6. 未验证清单（不得写 PASS）

1. **GUI 真桌面复现格式切换卡死**：无自动化桌面会话，两条新断言只在 headless 宿主上验证。
2. **UI-01/02/03/05/06 的旧行为负控**：只有代码形态推断（§5.9），没有"在旧二进制上跑新断言"的对照臂。原因：新断言与产品改动同批落地，旧代码未留树。**要补就得先把断言单独 cherry-pick 到 `957cfa8` 上跑一次**，本轮未做。
3. **`CpuFeatureService.FullReport()` 的实际 stdout**：headless 无 `--diagnose` 开关（BUILD-08 建议补）。
4. **GPU 编码器 Verified 矩阵**：本机无对应硬件路径实测。
5. **全新用户首启 app 包路径**：`%APPDATA%` 有历史 settings 污染，未在干净账号下验证。
6. **BUILD-04 appstage 两态归因**：本机扩展搜索命中仓库 `publish/PLAN`，两态都压了 oxipng ⇒"随附后才生效"仅代码推断，**需隔离环境复测**。
7. **beta16 五项微基准 / beta17 全量构建 5 warnings / beta15 contract 164 对账**：读数在别的批次，本轮未重跑（GATE-04 推迟）。
8. **ProPhoto 非 D65 源 AVIF 兜底分支**、**HDR 源 AVIF 双写 ICC/CICP 一致性**（本轮只测 SDR P3）、**exiftool 重写 `iCCP` 后 cICP 逐块保留**、**APNG 帧保持**、**runner 式 rc（单跑 vs 运行器差异）**：均在审查报告 §8 挂账，本轮未收。
9. **AVIF/JXL 未标注 SDR 的口径**：本批只收窄到 `UnlabeledMeansSrgb=true` 的 PNG/APNG；"未标注不必然是 sRGB"决定了 AVIF/JXL 仍各按各的，**口径待产品单独拍板**。
10. **工作树内的并行未提交改动**（见 §7.4）：最终读数打在混合树上。

---

## 7. 遗留与需你拍板

### 7.1 beta17 的 tag / 包 / 代码三方不一致（需要你定处置）
- 现状：`tag 1.6.0BETA17 → 957cfa8`；两个 beta17 包（13:53 / 14:01）也产出在 `957cfa8`；修复链现在在其后 11 个提交。
- **若重出 beta17**：包内容会与 tag 指向的提交不同 ⇒ tag 与包脱钩。按 §8.1"重出必升版本、新打 annotated tag"，本不该同版本重出。
- 三个选项（我的建议是 **B**）：
  - **A. 升 `1.6.0-beta18` + 新 tag**：完全合规，但与你"不升版本"的指令相反 ⇒ 需你改口径。
  - **B. 固定 beta17 重出 + 不碰 tag**：包内与 CHANGELOG 显式标"重出、源提交 `<hash>`、内容≠tag 957cfa8"，并在 `RELEASE_GATES.md §6` 留档；需你**显式豁免 `AGENTS.md:208 §8.1`**。风险：外部只认 tag 的人会拿到旧包。
  - **C. 不重出，等下一版**：`main` 的修复留着，下一版一起发。风险：beta17 用户继续吃旧缺陷。
- **未决点**：§8.1 的豁免要你一句话点头（我不能替你改 §8）。

### 7.2 `AGENTS.md:208 §8.1` 冲突（现状：未改，挂起）
`RELEASE_GATES.md §6` 已写明"同 `<Version>` 重出会与既有 tag commit 脱钩，须显式标注重出/源提交/内容不同"，但 `AGENTS.md` 正文未动 —— 因为动它就是改发布纪律，需要你拍板。

### 7.3 接受但未收口的 LOW
- **FMT-01**（exiftool tmp 残留）：每条 PNG 出口旁留 `<out>.png_exiftool_tmp`；`-tmpdir` 能消但打坏 webp ICC（prophoto 42→41，已回退）。final PNG 的 cICP/ICC 由编码器侧写入且门禁回读正确 ⇒ 接受。
- **BUILD-03**（5 条存量警告）：接受，后续单独清理。
- **GATE-04**（CHANGELOG trace）：推迟，**一旦重出 beta17 必须补**。
- **BUILD-08**：AOT CPU/GPU 检测不失真（否定结论成立），建议补 headless `--diagnose` 便于门禁锁定，本轮不动作。

### 7.4 工作树里有另一条并行工作线的未提交改动（**不是本批、未被我提交**）
20:33–20:40 前后写入、之后静默的 11–12 个文件：`MainWindow.xaml(.cs)`、`FfmpegOptions.cs`、`ColorIntent(.Factory).cs`、`ColorStrategyPlanning.cs`、`ColorTransformPlan.cs`、`FormatCapabilitiesService.cs`、`GainMapEncoder.cs`、`IsoBmffGainMapWriter.cs`、`QueueProcessor.cs`、`verify-color-matrix-full.ps1`，外加两份未跟踪文档 `GAINMAP_UI_AND_BASE_GAMUT_ANALYSIS_2026-11-16.md`、`GAINMAP_UI_FIX_PLAN_2026-11-16.md`。内容是把 GainMap 面板显隐统一到 `GainMapEncoder.FormatCarriesGainMap`、AVIF 底图 `colr` 重写等。
- 本批**未改、未 add、未 revert** 它们（改动权不在本批）。
- 影响：§4 的最终读数是"提交链 + 这批外部改动"的**混合树**读数；**重出 beta17 前必须先处置它们**（提交并各自跑闸，或退回），否则包内容会带上未验代码。

---

## 8. 复现入口

```powershell
# 1) 构建（必须 -m:1：沙箱内多节点会 MSB1025）
C:\Program Files\dotnet\dotnet.exe build ffmpegPictureUI.sln -c Release -m:1

# 2) exiftool 的 perl532.dll 靠 PATH（DLL 在 exiftool_files 目录里）
$root = 'C:\PLAN\ffmpegPictureUI'
$env:PATH = "$root\publish\PLAN\exiftool\exiftool_files;$root\publish\PLAN\exiftool;$env:PATH"

# 3) 钉住 TMP 根（否则 verify-color-wiring 一类会批量假红）
$env:TMP = "$root\tests\output\_tmp\final-verify\tmp"
$env:TEMP = $env:TMP; $env:TMPDIR = $env:TMP

# 4) 变更链路门禁（逐条挑跑，禁止无选择器全量）
C:\Program Files\dotnet\dotnet.exe --version > $null
pwsh -NoProfile -File tests\scripts\verify-color-wiring.ps1
pwsh -NoProfile -File tests\scripts\verify-gainmap-isobmff.ps1
pwsh -NoProfile -File tests\scripts\verify-decision-delivery.ps1
pwsh -NoProfile -File tests\scripts\verify-ui-split-union.ps1

# 5) 出包前红态闸（全绿才放行）
pwsh -NoProfile -File tests\scripts\_release-gate-check.ps1
```

**读数绑定规则**：宣布"全绿"必须同时给出 `FfmpegGui.dll` / `ServiceProbe.dll` 的 `sha256[:16]` 与当轮日志同段——**不要用 `.exe`** 判定，AOT 单包无 `FfmpegGui.dll`、app/full 两包 exe 仅差 6 B（bundle header nonce）。
