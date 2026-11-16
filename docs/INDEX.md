# docs 索引（结构层）

三类，按"能不能动位置"分。**状态**列 = 现在读它还有没有意义；**引用**列 = 仓外（`src` / `tests/scripts` /
`pack.ps1` / README / CHANGELOG）命中它的文件数，非零就动不得（改了要同步引用点）。

## 1. 承重（位置固定）

| 文件 | 引用 | 用途 |
|---|---|---|
| `TESTING.md` | 68 | 测试规范正文；顶部有 2026-10-22 布局迁移读法 |
| `HANDOVER.md` | 24 | 累积账本；末段 H-14 = beta9 出包读数与未了结（push 待裁决），此前各段按时间点留档 |
| `RAW_PIPELINE_AUDIT.md` | 4 | RAW 链路审计 |
| `IMPLEMENT_PLAN_2026-10-11.md` | 4 | **现行任务账本** |
| `JPEGLI_GAINMAP_PANEL_PLAN_2026-10-09.md` | 5 | GainMap 面板设计（`CliParser` / `FfmpegCommandBuilder` 指它） |
| `COLOR_MATRIX_FIX_PLAN_2026-09-24.md` | 7 | 色彩矩阵修复（`src` 4 个文件指它） |
| `PIPELINE_TEST_AUDIT_2026-10-01.md` | 3 | 管线测试审计 |
| `TEST_SPLIT_TEMPLATE.md` / `TEST_SPLIT_PLAN_2026-10-05.md` | 3 / 0+链接 | 拆分模板（新测试工程按它写） |
| `PIPELINE_PERF_ANALYSIS_2026-10-09.md` | 2 | 性能分析 |
| `FIX_PLAN_2026-10-14.md` | 2 | 修复计划（现行） |
| `TEST_MODULE_PLAN_2026-11-12.md` | 0（新，2026-11-12） | **测试模块化现行账本**：机读模块账本+不落后针（批 1）、三个高危盲区 E2E（批 2）、ServiceProbe 源级 partial 拆分（批 3）、中低盲区（批 4）、卫生与文档（批 5）；三路调研底账与已裁定决策（partial/两线并行/rc.1 顺延） |
| `GAINMAP_ROUTES_AND_PATCH_PLAN.md` | 1 | 增益图路由与修补方案（现行） |
| `MANUAL_VERIFICATION.md` | 1 | 手工验证清单 |
| `AUXC_HEIC_GAINMAP_PLAN_2026-11-02.md` | 0（新，2026-11-02） | Apple `auxC` HEIC 增益图（DJI 实拍形态）的**可行性设计**：box 树/`iref`/`ipma` 一手读数、`grid` 载荷 8 B 中尾部 4 B 未读、`auxl` 反查未通两处缺口、Apple 增益口径三候选（§5 **低置信**）、§6 逐点改动、§7 判据草案。**本批未实施**（理由与三件 `sha256[:16]` 见 `HANDOVER-2026-11-02.md` §3.7）⇒ 位置可动，暂不进归档 |
| `HANDOVER-JXL-GAINMAP-2026-11-08.md` | 0（新，2026-11-08） | JXL 增益图**交接/可行性设计**（任务交接，非账本读数）：一手生态调研缺口、jhgm 形状一手读数、P2-1「先量生态再开写侧」口径、§5 扩展点清单。写侧/读侧此后已按解析级标准落地（`3cf0fb9`/`917dd2e`，见 AGENTS §7 与 `docs/TESTING.md` 第 96/97 条）⇒ 位置可动，暂不进归档 |
| `ACR_FULL_ROUTE_PLAN.md` | 0（新，2026-11-01） | DNG→ACR 全量路线的**可行性设计**（链 A/链 B、R1~R3 风险、M1~M5 待测格）；维护者裁定「只做设计+探针」⇒ 不含任何产品侧路由改动。位置可动，但被 `CORRECTION-2026-10-31-C-FULLDRIVE.md` 按名指它（AGENTS §7 只描述性提及 ACR 路线，未点名本文件——2026-11-02 核） |
| `RAW2TIF_QA_DEFECT_2026-10-03.md` | 1（**前缀名**） | `QueueProcessor.cs:808` 以 `RAW2TIF_QA_DEFECT §7.5` 指它；按全名数引用会漏，故留顶层 |
| `ENCODING_OPTIONS_AUDIT_2026-09-26.md` | 1 | 高级编码选项审查 |
| `DNG_SONY_IDC.md` / `DNG_BUILD.md` | 1 / 1 | DNG 厂商制式与构建 |
| `COLOR_MATRIX_AUDIT_2026-09-24.md` | 1 | 色彩矩阵审计 |
| `AVIF_GAINMAP_ENCODER_2026-10-02.md` | 1 | AVIF 增益图编码 |
| `GAINMAP_VENDOR_ANNOTATION_AUDIT_2026-10-02.md` | 1 | 厂商增益图注记审查 |
| `COLOR_ENGINE_INTERFACE_PLAN.md` | 1 | 色彩引擎接口 |
| `HANDOVER_2026-09-22.md` / `HANDOVER_2026-10-02.md` | 2 / 2 | 旧交接口（有门禁指它，故留原位） |
| `HANDOVER_2026-10-10.md` | 0 | 旧交接口（其后已有 `HANDOVER-BETA9-STOP2-2026-10-26.md` 与 `review-2026-10-31/HANDOVER-2026-10-31.md`），现行账本的前身 ⇒ 留顶层不进归档 |
| `FIX_PLAN_2026-10-11.md` | 1 | 修复计划（`tests/projects/GainMapTestHost/Program.cs:1763` 注释按名指它；2026-11-02 补登记——此前漏册） |
| `HANDOVER-BETA9-STOP-2026-10-26.md` / `HANDOVER-BETA9-STOP2-2026-10-26.md` | 0 / 0 | beta9 中止快照（时间点账本，STOP 的续接指针指向 STOP2，互为指针）；2026-11-02 补登记 |
| `HANDOVER-2026-11-02.md` | 0 | 单会话时间点交接快照：全仓审查/MD 校订批次（`2a7d354`/`bbfde16` 已推送）、HEIC grid 增益图修复批次（`41375a2`，含 §1d 判据与变异 I 登记）、§3.10 **批次 D 六项收口**（含第三方 GainMap 语料与取向探针取数口径）；累积账本仍看 `HANDOVER.md`。⚠ **语料路径已迁**：原文记的仓外 `C:\PLAN\gainmap-corpora\` 目录已删（2026-11-03 工作区边界立规，见 §3.10 与仓内 `AGENTS.md §1`），现落 `tests/output/corpus/gainmap-iso21496/`（3 件，整树 gitignore，永不随仓分发） |
| `HANDOVER-NET11-GA-PACKAGING-2026-11-11.md` | 0（新，2026-11-12 登记） | .NET 11 GA 升级收尾 + 出包任务的交接快照（前会话 11-11 23:2x 停止点）：GA SDK 状态、AOT 步骤与五项验收清单、风险与坑；接手批进展见该文 §8（AOT 已验收、代表门禁 GA 下全绿、版本裁定 1.6.0-rc.1、出包顺延至测试模块化批后）。位置可动，暂不进归档 |
| `HANDOVER-TEST-MODULE-BATCH5-2026-11-12.md` | 0（新，2026-11-12） | 测试模块化批 5·死支处置 + 覆盖探针 fullrun 证据链的交接/收口快照：死支三删一留、⑥-d 回归根因（union 表键经 varMap 注入）与实参元素修法、40/0 战况；后续批（索引生成 / C4 超时表）指针在 §4。位置可动，暂不进归档 |
| `RELEASE_GATES.md` | 2（新，2026-11-16 登记） | **出包前红态闸**：红态禁止发布、按链路选必跑门禁、机读清单 `tests/output/release-gates.json` 格式、`_release-gate-check.ps1` fail-closed 对账、§6 版本与 tag 纪律（同版本重出须显式标注"重出/源提交/与 tag 1.6.0BETA17 内容不同"；§8.1 冲突待用户拍板豁免）。被 `tests/scripts/_release-gate-check.ps1:32,76` 按路径指它 ⇒ **承重，位置固定** |

## 2. 分批审查（`review-YYYY-MM-DD/`，被门禁按名点名，位置固定）

`review-2026-10-03`（8）· `review-2026-10-04`（18）· `review-2026-10-10`（19，含 `lanes/` `verify/`）·
`review-2026-10-11`（3 份 .md + 1 份 `f9-partial-lane-E.patch`）· `review-2026-10-16`（1，`test-value-audit.md` 被 `verify-probe-mirror-parity` 引用）·
`review-2026-10-31`（5，`AUDIT-独立性审查-2026-10-31.md`；独立性安全/正确性审查判词总账，被测件 `f89b247b60dcf1ef` == HEAD。
**已有门禁文件按名点名本目录**（`_run-step3-gates.ps1:1640` 注释引 `docs/review-2026-10-31`——此前写的「尚无任何门禁按名点名它」已过时，2026-11-02 更正）⇒ 位置事实上已被钉；但 §8.2 记了与总账/各 lane 的 7 处不一致，改名前先读那一段；
一手证据全在**仓外** `..\ffmpegPictureUI-audit\`（`evidence/` + `lanes/*/FINDINGS.md`），本仓不随附产物。
第二份是**追加更正条目** `CORRECTION-2026-10-31-S8.md`（S8 修复落地 / 第 87 条入列 / §7.3 表外三条的兑现与仍未兑现项，含 65283 vs 65269 的口径差）；
第三份 `CORRECTION-2026-10-31-B-2REDS.md`（两条既存红真修：第 56 条两处合并取数站点收窄为只读 stdout、㉛ 期望总数改为
`exiftool -ver` 的版本函数；原档与前一份里「建议独立一批」的处置由它收口）；
原档**一字未改**——带日期的账本不回改，要更正就新写一条，这条规矩对它自己同样适用）；
第四份 `CORRECTION-2026-10-31-C-FULLDRIVE.md`（**全量推进未了结项**：清单 87→94、⑪-d WARN 清零、宿主 TEMP 站点第 94 条、白点内核 `pixFmt` 回拷与 10/12-bit 读数、`65283` 判尺对撞、`.xaml` 不生成 `x:Name` 的机制、UI 执行面 5 条切拆分件与条数锁精确化、文件锁假红根因。并与 `ACR_FULL_ROUTE_PLAN.md` 成对）
第五份 `HANDOVER-2026-10-31.md`（beta10 独立性审查的**交接文档** v1.0：S1–S10 结论速览、被测件绑定 `f89b247b60dcf1ef`、9 批次产出索引。原放仓库根，2026-11-02 按 §1 目录契约（根目录只放法律/说明/规范白名单）移入本目录，内容未改）
`review-2026-11-13`（2，2026-11-13 工作区审计：`WORKSPACE_AUDIT_2026-11-13.md` 构成/测试归置/AGENTS 精简/Git tag 核对与提交笔数分析；`AGENTS_HISTORY_2026-11-13.md` 旧 `AGENTS.md §7` 的逐字快照，只作参考、不是当前口径）
`review-2026-11-14`（6，2026-11-14 色彩标注口径批：`COLOR_ANNOTATION_PRIORITY.md` 默认推荐改为「CICP 优先 / CICP 不可达才 ICC 兜底」的优先级阶梯、四条回退触发与播报落点、显式策略零漂移论证、旧默认→新默认的不兼容面、改锁与新增负控清单、§9 四模式对照表、§9a 自生成 ICC 版本策略（v4.4 默认 / JPEG 可选 v2.0 / 源 ICC 永不重写）、§10 策略单选不再自动联动（含断言↔表格对号）；`COLOR_UI_LINKAGE_AUDIT.md` 色彩面板状态机逐控制项审计（控制项 × 当前状态 × 锁定原因 × 真实实现依据 × 判定 × 回归）、真实冲突 vs 纯策略副作用分类、`isBaking` 混用拆除与 I11/I12 断言归属；`ICC_SPEC_VERSIONS_AND_LICENSING.md` ICC 版本沿革（v2 / 4.3 / 4.4 / iccMAX）与工具链/容器支持度矩阵、规范文本版权·profile 许可·商标·专利分层核查（含 color.org 一手原文与只读实测）；`ICC_VERSION_COMPARISON.md` v2/v4.0/v4.3/v4.4/ICC.2 的八维能力对比（矩阵·TRC / LUT / MPE / HDR / 宽色域 / 容器与查看器 / 实测体积 / 选型）与场景选型结论表，§6 已裁决「全量 v4.4」（含原「不建议一刀切」证据保留、落地实测代价 544 B / 432 B 与 JPEG 默认版本争议记录；独立复核 `COLOR_MATRIX_IO_ANALYSIS_2026-11-14.md`（矩阵与输入/输出组合，含 `COLOR_MATRIX_9X9_2026-11-14.csv`，不基于上述四文），修复实现账本 `COLOR_MATRIX_IO_FIX_PLAN_2026-11-14.md`）
`review-2026-11-15`（3，2026-11-15 色彩/日志审计批：`COLOR_MATRIX_IO_ANALYSIS_2026-11-15.md` 矩阵输入输出组合复核（§12-13 已更 AVIF 双写口径、§37-38 仍写旧口径，自相矛盾处以 AGENTS §7 更正条目为准）；`EXECUTION_LOG_AUDIT_2026-11-15.md` 执行日志通路审计（为 beta16 日志虚拟化/批量写批铺垫）；`ICC_STANDARD_AUDIT_2026-11-15.md` ICC 标准与许可核查（§37/§163 为当日时间点读数，ICC 结构结论仍成立））
`review-2026-11-16`（2+2，2026-11-16 UI 卡死修复批：`UI_FREEZE_DIAGNOSIS_2026-11-16.md` 运行时定位报告（主日志 TextBox 布局风暴、`ProbeCurrentSourceBitDepth` UI 线程 ffprobe、队列逐行 Post 三层放大器与读数）；`UI_FREEZE_FIX_2026-11-16.md` 完整修复记录（日志有界队列 + 虚拟化列表、位深缓存/后台探测、格式代与 popup 收口、选项事件抑制、队列合并刷新；修复后读数、beta16 包哈希与验证口径））。同批 48h 变更审查（`a9e2830..957cfa8`）落地两份总控文档：`CHANGES_48H_REVIEW_RESULT_2026-11-16.md` 五路并行统一裁决（34 条发现 HIGH3/MEDIUM12/LOW18/INFO1，结论默认路径正确、回退路径丢 ICC；版本策略固定 `1.6.0-beta17`、修复落 main、停在可重出状态）；`FINDING_CLOSURE_2026-11-16.md` 34 条 finding 的重出 beta17 闭环表（ID/级别/处置/理由/对应提交，随修复批增量落哈希）。证据子目录 `review-evidence/`（五路分报告 + 5 份 findings CSV + logs/；**审查证据，非承重文档**，引用以本目录两份总控 md 为准）。`CHANGES_48H_FIX_RESULT_2026-11-16.md` 修复结果统一交付（34 条逐条处置 = 修 26 / 接受 3 / 不改+待办 5；14 条提交链；变更链路门禁读数与 dll 指纹绑定；负控清单；未验证 10 项；beta17 tag/包/代码三方不一致的 A/B/C 处置建议与 `AGENTS.md §8.1` 豁免待批）。本目录两份 `GAINMAP_UI_*` 属并行工作线临时文档，按约定不入本索引

## 3. 归档（`archive/`，28 份 + `_gapscout_raw/`）

一次性审计报告与已执行计划，2026-10-22 从顶层移入。**判据 = 按前缀名（去掉尾部日期）在 `docs/` 外数命中，
零命中才可移**；`RAW2TIF_QA_DEFECT_2026-10-03` 因 `QueueProcessor.cs` 用短名指它而退回顶层。

`ADV_ENCODER_*`（3）· `TEST_*_2026-10-07`（5：COVERAGE_AUDIT / EXECUTION_LEAD / EXPANSION_PLAN / FIX_AND_SPLIT_REPORT / REPORT_BASELINE）· `PIPELINE_AUDIT_*`（2）· `PIPELINE_REVIEW` ·
`HANDOVER` 旧制式（`AGENT_TASK_SPLIT` `BUILD_HANDOVER` `JXL_ISSUES_HANDOVER` `PIPELINE_REVIEW_2026-09-14`
`HANDOVER_2026-09-16`）· `DISTRIBUTION_COMPLIANCE_AUDIT` · `LICENSE_TERMS_VERIFICATION` ·
`EXHAUSTIVE_TEST_PLAN` · `RAW_COLOR_TARGET_MODE_PLAN_2026-10-04` ·
`GAINMAP_HEIC_AVIF_ANALYSIS` · `JXL_RAW_16BIT_ADVISORY_2026-10-02` · `JXR_LIBJXR_INTEGRATION_PLAN_2026-09-26` ·
`COLOR_MATRIX_USABILITY_AUDIT_2026-10-02` · `CJK_BASELINE_REGISTRATION_2026-10-02` ·
`AVIF_ANIMATED_FFMPEG_PARAMETERS` · `TEST_SPLIT_DESIGN_2026-10-05` · `TOOLCHAIN_UPGRADE_PLAN`

归档里的内容是**时间点读数**：布局迁移、改名不回改其中的旧路径。要更正就新写一条更正条目。
