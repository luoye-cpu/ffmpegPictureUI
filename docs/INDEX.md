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
| `GAINMAP_ROUTES_AND_PATCH_PLAN.md` | 1 | 增益图路由与修补方案（现行） |
| `MANUAL_VERIFICATION.md` | 1 | 手工验证清单 |
| `RAW2TIF_QA_DEFECT_2026-10-03.md` | 1（**前缀名**） | `QueueProcessor.cs:808` 以 `RAW2TIF_QA_DEFECT §7.5` 指它；按全名数引用会漏，故留顶层 |
| `ENCODING_OPTIONS_AUDIT_2026-09-26.md` | 1 | 高级编码选项审查 |
| `DNG_SONY_IDC.md` / `DNG_BUILD.md` | 1 / 1 | DNG 厂商制式与构建 |
| `COLOR_MATRIX_AUDIT_2026-09-24.md` | 1 | 色彩矩阵审计 |
| `AVIF_GAINMAP_ENCODER_2026-10-02.md` | 1 | AVIF 增益图编码 |
| `GAINMAP_VENDOR_ANNOTATION_AUDIT_2026-10-02.md` | 1 | 厂商增益图注记审查 |
| `COLOR_ENGINE_INTERFACE_PLAN.md` | 1 | 色彩引擎接口 |
| `HANDOVER_2026-09-22.md` / `HANDOVER_2026-10-02.md` | 2 / 2 | 旧交接口（有门禁指它，故留原位） |
| `HANDOVER_2026-10-10.md` | 0 | 最新交接口，现行账本的前身 ⇒ 留顶层不进归档 |

## 2. 分批审查（`review-YYYY-MM-DD/`，被门禁按名点名，位置固定）

`review-2026-10-03`（8）· `review-2026-10-04`（18）· `review-2026-10-10`（19，含 `lanes/` `verify/`）·
`review-2026-10-11`（4）· `review-2026-10-16`（1，`test-value-audit.md` 被 `verify-probe-mirror-parity` 引用）

## 3. 归档（`archive/`，28 份 + `_gapscout_raw/`）

一次性审计报告与已执行计划，2026-10-22 从顶层移入。**判据 = 按前缀名（去掉尾部日期）在 `docs/` 外数命中，
零命中才可移**；`RAW2TIF_QA_DEFECT_2026-10-03` 因 `QueueProcessor.cs` 用短名指它而退回顶层。

`ADV_ENCODER_*`（3）· `TEST_*_2026-10-07`（6）· `PIPELINE_AUDIT_*`（2）· `PIPELINE_REVIEW` ·
`HANDOVER` 旧制式（`AGENT_TASK_SPLIT` `BUILD_HANDOVER` `JXL_ISSUES_HANDOVER` `PIPELINE_REVIEW_2026-09-14`
`HANDOVER_2026-09-16`）· `DISTRIBUTION_COMPLIANCE_AUDIT` · `LICENSE_TERMS_VERIFICATION` ·
`EXHAUSTIVE_TEST_PLAN` · `RAW_COLOR_TARGET_MODE_PLAN_2026-10-04` ·
`GAINMAP_HEIC_AVIF_ANALYSIS` · `JXL_RAW_16BIT_ADVISORY_2026-10-02` · `JXR_LIBJXR_INTEGRATION_PLAN_2026-09-26` ·
`COLOR_MATRIX_USABILITY_AUDIT_2026-10-02` · `CJK_BASELINE_REGISTRATION_2026-10-02` ·
`AVIF_ANIMATED_FFMPEG_PARAMETERS` · `TEST_SPLIT_DESIGN_2026-10-05` · `TOOLCHAIN_UPGRADE_PLAN`

归档里的内容是**时间点读数**：布局迁移、改名不回改其中的旧路径。要更正就新写一条更正条目。
