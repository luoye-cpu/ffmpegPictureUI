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
