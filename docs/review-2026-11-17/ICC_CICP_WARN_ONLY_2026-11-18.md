# ICC/CICP 源侧问题改为“只警告、不拒绝”（2026-11-18）

## 结论

源侧 ICC / CICP 的**存在性不可判定、内容不可读、合法但不可矩阵化**不再触发 `exit 90 / 91`
和零产物；任务继续产出，条目通过 `QueueItem.MarkWarning` 标黄，GUI「仅显示警告」可筛选。

保留的拒绝只有**用户显式选择的输出矩阵跨族冲突**（`OutMatrixCapability.Rejected`），
因为该冲突是“像素语义 A、标签语义 B”的结构性不可能，不属于“源描述读不准”。

## 代码落点

| 文件 | 变更 |
|---|---|
| `Services/ColorMapping/IccCarryPolicy.cs` | `SourceIccStatus.Unreadable` 返回 `Skip`，不再 `FailClosed` |
| `Services/ColorMapping/ColorStrategyPlanning.cs` | `PlanCarry` 对 `Unreadable` 返回 `None + ProceedWithDegradation`，附 `SourceIccUnverified`；`PlanCore` 对源不可映射、`MatrixTo` 为空、曲线不可精确表达改为携带/无标注 + 降级 |
| `Services/ColorMapping/ColorVerdict.cs` | 新增稳定 Code：`SourceIccUnverified`、`SourceCicpUnverified`、`SourceColorUnmappable` |
| `Services/ColorMapping/ColorIntentFactory.cs` | 内嵌 ICC 不可读、外部 `--icc-file` 无效/不可解析、ICC 合法但无矩阵描述均追加 `intentWarnings` |
| `Services/FfmpegCommandBuilder.Decision.cs` | 不再置 `IccCarryFailClosed` |
| `Services/QueueProcessor.cs` | `RefuseCarryIccUnreadable` → `WarnCarryIccUnreadable`；`intent == null`、非输出矩阵的 `plan.IsOk == false` 均回退传统管线 + `MarkWarning`；`plan.Verdict.Degradations` 逐条 `MarkWarning` |
| `tests/projects/ServiceProbe/Parts/16_ProbePlan.cs` | 断言由“源不可识别 → 拒绝”改为“降级不映射（警告）”；新增 LUT-only 描述符用例 |

## 手工实测（本机）

命令统一为 `FfmpegGui.exe --headless --format png --color-strategy <carry|recommended> --log-level Debug`。

| 用例 | 结果 |
|---|---|
| 坏 ICC `tests/output/_tmp/round-tmp/bad_icc.png` + `carry` | `exit=0`，产物 3475 B，`裁决=ProceedWithDegradation`，`SourceIccUnverified` + `UnlabeledAssumedSrgb` |
| 同一坏 ICC + `recommended` | `exit=0`，产物 3388 B，命令行/日志有 `source ICC exists but is unreadable` 警告 |
| 正常 P3 `tests/output/results/color/a2_srgb_p3.png` + `carry` | `exit=0`，产物 36123 B；本机 exiftool 读取失败时同样降级为警告并出图 |
| LUT display ICC `tests/output/_tmp/color-audit-20261117-.../cases/lut-icc/lut_display.png` + `carry` | `exit=0`，产物 53478 B，`SourceIccUnverified` 警告；不再 exit 91 |

产物/日志：
- `tests/output/_tmp/bad-icc-carry-20261118/`
- `tests/output/_tmp/bad-icc-recommended-20261118/`
- `tests/output/_tmp/p3-warn-check/`
- `tests/output/_tmp/lut-warn-20261118/`

## 构建与测试边界

- `dotnet build ffmpegPictureUI.sln -c Release -m:1`：**0 警告 / 0 错误**。
- `ServiceProbe plan`：**43 pass / 0 fail**（`exit=0`）。
- 构建指纹：`FfmpegGui.dll`=`060070F34B8E0DED`，`ServiceProbe.dll`=`493847A7043204B9`。
- `verify-color-wiring.ps1` 定向跑出现大量 `0B` / `Failed to load Perl DLL` / `System.IO.FileNotFoundException: System.Linq` /
  `系统资源不足`。逐条日志指向本机进程资源枯竭（提交内存 127 GB、多个子进程 `0xC000001D`），
  **不是色彩逻辑回归**；该轮读数不可作为验收结论。
- 未完成：在同一最终树上重跑 `verify-color-wiring` / `verify-color-out-matrix` 并绑定上述
  `FfmpegGui.dll` / `ServiceProbe.dll` 指纹。

## 设计边界（需维护者确认）

- 显式输出矩阵跨族冲突仍 `exit 91 / 零产物`；若“不做任何拒绝”也覆盖这一格，需要同批改
  `verify-color-out-matrix` 的 C/D/I 组断言。
- 合法 LUT ICC 目前仍被 `SourceIccStatus` 归为 `Unreadable`（因为 `ProbeInputColorMetadataCore`
  只把“有矩阵描述符”算 `Present`）；本次只保证“警告 + 出图”，尚未把 LUT ICC 原样携带。
  完整 LUT/CMS 携带属 `COLOR_PIPELINE_LUT_ICC_IMPLEMENTATION_PLAN_2026-11-17.md` 的范围。
