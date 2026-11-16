# 第 1 路色彩语义审查报告（CICP/ICC 收口 · 命令行/预设语义）

- 范围：`a9e2830..957cfa8`
- 被测二进制：`publish/build/FFmpegPictureUI-v1.6.0-beta17-x64-full/FfmpegGui.exe`（CLI 实测）；门禁跑 `src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe`（DLL 构建时间 2026-11-16 13:56，晚于源码 11-15，为当前构建）
- 工具：`tools/src/pngcicp/bin/Release/net11.0/pngcicp.exe`、`publish/PLAN/exiftool/exiftool.exe`、`publish/PLAN/ffmpeg-full/ffprobe.exe`
- TMP 钉死：`tests/output/_tmp/review-2026-11-16/color/tmp`
- 只读审查：未改源码/测试/文档正文/版本位，未 commit/push/tag/出包，未跑 104 条全量门禁。

## 1. 结论摘要

| 项 | 值 |
|---|---|
| 最高级别 | **HIGH**（COLOR-01 / COLOR-07） |
| 总数 | 8（HIGH 2 / MEDIUM 4 / LOW 1 / INFO 1） |
| 分布 | HIGH=2 · MEDIUM=4 · LOW=1 · INFO=1 |
| 门禁 | `verify-color-strategy` **绿 78/0 rc=0**；`verify-decision-delivery` **红 78/9 rc=1**（其中产品缺陷 2 条、门禁未随新路由更新 7 条，见 §3.1） |

`c7ac5ab` 之后"CICP 优先、ICC 兜底"在**默认 engine/auto 路径上确实收敛成单一真值**（规划层 `ColorStrategyPlanning` + `QueueProcessor` PNG 后置 + `RawColorPipeline` AVIF avifenc），双写时 ICC 内部 cicp 与容器 cICP/nclx 逐值一致，未发现 CRITICAL 级播放器分叉根因。但存在两处真问题：(a) legacy 路径对显式 carry 在 PNG 上丢掉 ICC，与 engine 结论分叉；(b) 本变更集新引入的 avif16 `--depth 12,4` 路由让 `verify-decision-delivery` 的 ⑯/⑰b/⑱ 段失配转红，门禁未随新路由更新。

## 2. 审查要点逐条

### 2.1 优先级真值（CICP 优先/ICC 兜底是否单一）
- engine/auto 路径：单一。`SetLabelsFrom`（ColorStrategyPlanning.cs）按"CICP 可命名→容器 CICP；不可命名→ICC 兜底；两者可共存→按容器 `CicpFirstWhenBoth` 决定"决策；PNG 由 `QueueProcessor` 后置 iCCP/cICP，AVIF 由 `RawColorPipeline` 经 avifenc `--icc --cicp` 同写。
- **分叉点（COLOR-01）**：legacy 路径在 `FfmpegCommandBuilder.Decision.cs:569-582` 对 png/apng 无条件 `d.AttachIcc=false`，不看 `DeclaredStrategy`，与规划层对显式 CarryIcc 的双写意图冲突。默认 auto 选 engine，所以终端用户无感；但 `--color-engine legacy` 或回退 legacy 的出口会静默少写 ICC。

### 2.2 双载体双写（真实转码 + 回读）
| 场景（P3 D65 源 a2_srgb_p3.png） | cICP/nclx | ICC | 结论 |
|---|---|---|---|
| recommended → PNG（engine/legacy） | 12/13/0/full | 无 | CICP-first ✓ |
| carry → PNG（engine/auto） | 12/13/0/full | iCCP 405B v4.4.0 P3/sRGB | 双写 ✓ |
| carry → PNG（legacy） | 12/13/0/full | **无** | **分叉 COLOR-01** |
| recommended → AVIF（engine） | nclx 12/13/0/full | v4.4.0 P3/sRGB | avifenc `--cicp 12/13/0 --icc ...v44cicp.icc` 双写 ✓ |
| recommended → WebP | —（ffprobe yuv420p/tv） | 无 | WebP 恒 tv、不写 ICC ✓ |
| recommended → JPEG | —（yuvj420p/pc） | v4.4.0 SMPTE EG432-1 | ICC-only（JPEG 无 CICP 通路）✓ |

双写时 ICC 内部 `cicp` tag（primaries=SMPTE EG432-1、transfer=sRGB、matrix=Identity、Full=Full）与容器标签逐值一致，未发现"双写互相矛盾"。

### 2.3 ICC v4.4 重写与源 ICC 烘焙
- 版本解析修复正确：`IccProfileService` 读 `bytes[8] + (bytes[9]>>4)`，实测 exiftool 报 `ProfileVersion 4.4.0`（旧逻辑会把 0x40 误读成 64）。
- 自生成 ICC 含 `cicp` tag（`IccProfileBuilder.BuildCicp`）、`chrm`、`chad`、`wtpt`、stamped `ProfileID`（MD5），exiftool 全绿解析。
- `cicp` tag `Matrix` 恒 0、`FullRange=1`（RGB 数据空间，规范正确）；与容器 cICP full=1 一致。
- **未覆盖**：源 ICC 为非 D65（如 ProPhoto D50）时的色度适应矩阵烘焙路径，本次只有 P3 D65 源，未实测 `NormalizeSourceIccToV44` 的 D50 适应分支。

### 2.4 AVIF 16bit / HDR / 范围 / 曲线
- engine 路径：P3 源 16bit → AVIF 走 `avifenc --depth 12,4`（Sample Transform），nclx=12/13/0，实测交付 12bit，`verify-color-strategy` F4/F5 全绿，plan 层无"悄悄降 8bit"。
- legacy 路径：12bit AVIF 现按 exit 91 fail-closed 拒绝（"不静默降 12bit"），这是有意行为，但门禁 ⑰b 仍期待 exit=0 ⇒ COLOR-02。

### 2.5 兼容性（旧预设/CLI）
- `--icc-profile-version v9` → rc=2 拒绝（闭值校验）。
- 未知 `--some-future-flag` → rc=0 + 告警"已忽略"（既有契约）。
- `FfmpegOptions.IccProfileVersion` 默认 `v4`；`PresetData` 回放 `if (p.IccProfileVersion != null)` 不覆盖，null 停默认（与 pngopt max 同契约，不会把档位带回 off）。
- WebP 恒 tv、JPEG auto 压回 pc（yuvj420p）口径不变。

### 2.6 门禁覆盖判断
- `verify-color-strategy`：绿。覆盖 4 策略 × SDR/广色域/HDR × png/avif/jpg/jpg+gainmap，含 F4/F5 avif12/16 位深。
- `verify-decision-delivery`：**红 78/9**（COLOR-02）。
- **覆盖缺口**：两条门禁都不交叉校验"ICC 内部原色 == 容器 CICP"（COLOR-06），也不测 p3→png carry 的 engine/legacy parity（COLOR-01）。

### 2.7 账本数字可追溯性
- 已对到命令/日志：双写 ICC+CICP 一致（本报告 §2.2 转码）、v4.4.0 版本解析（exiftool）、avifenc `--cicp 12/13/0 --icc`（avif-rec.stdout）。
- **未验证**：`ICC_VERSION_COMPARISON.md` §2.4/§6.6 的 skcms 样本体积（584/2612/8708 B）与"自生成 544 B/10 tag"未独立复测；`COLOR_MATRIX_9X9_2026-11-14.csv` 矩阵取值未独立重推导；ProPhoto→AVIF 的 `--cicp 2/2/0` 兜底分支无 ProPhoto 夹具未实测。

### 2.8 ⑭直连 / ⑮曲线补取证（追加）

**⑭ WebP 直连 carry 丢 ICC（COLOR-07，建议 HIGH）**
- 复现：P3 源 → WebP，`--color-strategy carry --color-space BT.2020`。engine 产物 7008B、exiftool 读回 ICC v4.4.0、ChromaticityChannel1=0.67999（=源 P3）；legacy 产物 6366B、ffprobe `color_primaries=unknown/color_transfer=unknown`、exiftool 读不到 ICC。
- 调用链：legacy 命令行 `-vf iccgen -colorspace bt470bg` → libwebp 丢 iccgen 的 ICC → `QueueProcessor.cs:3020 planPromisesGeneratedIcc`（AttachIcc=true/IccPrimaries!=null/IccPath=null）命中 → `:3045` 打"计划承诺生成标准 ICC…跳过源 ICC 回带，交由后置步骤落地" → 但落标准 ICC 的后置只在 engine 路径（`:2525`）跑 ⇒ legacy 零标注。
- **A 判定：本批新引入**。`git log -S "计划承诺生成标准 ICC"` = c7ac5ab（在 a9e2830..957cfa8 内）；a9e2830 该字符串不存在，legacy 走 `else` 分支 `CopyIccProfileAsync` 回带源 ICC。断言文本本身旧（a9e2830 已有），但本批新增的短路分支让它从"偶发红（exiftool flaky）"变成"确定性红"。

**⑮ HDR→8bit PNG 链末曲线 vs 标签（COLOR-08，建议 MEDIUM）**
- 复现：HDR(PQ) 源 → PNG `--bit-depth 8`。pngcicp 读回 `cICP primaries=1 transfer=1`（=bt709/bt709）；但 ffmpeg 命令行 `zscale=…:p=bt709:t=iec61966-2-1:m=bt709`——像素实际用 sRGB 分段曲线。
- 两条真值来源：**标签** = `ColorStrategyPlanning.PlanDelegatedHdrSdr:1593-1594` 写死 `OutPrimaries/OutTransfer/OutMatrix="bt709"`，经 `[png3] 已写入 cICP（…transfer=1 ← bt709/bt709）` 落容器；**像素链** = legacy zscale 构建 `t=iec61966-2-1`。
- **A 判定：本批前既已失败（存量红）**。a9e2830 时 `PlanDelegatedHdrSdr` 已写死 bt709，且 a9e2830 源码注释自承"链末默认改成 t=iec61966-2-1…正是 PlanDelegatedHdrSdr 注释里批判过的旧错形态"。本批对该门禁的唯一改动是 exiftool 退避重试探针，未触及此分支。bt709 OETF 与 sRGB 分段仅在趾部不同，影响小。


## 3. 覆盖清单（26 文件逐个）

| # | 文件 | 方式 | 结论 |
|---|---|---|---|
| 1 | src/Models/ColorStrategy.cs | git diff | exit=91 重写，已读 |
| 2 | src/Models/FfmpegOptions.cs | git diff | IccProfileVersion 默认 v4，已读 |
| 3 | src/Models/PresetData.cs | git diff | null 不覆盖契约，已读 |
| 4 | src/CliParser.cs | git diff + 实测 | 闭值校验 rc=2，已读 |
| 5 | Services/ColorMapping/ColorIntent.cs | git diff | 已读 |
| 6 | Services/ColorMapping/ColorIntentFactory.cs | git diff | ExitWritesIcc 改为排除 webm，已读 |
| 7 | Services/ColorMapping/ColorStrategyPlanning.cs | git diff | PlanCarry/EnsureAnnotation，已读 |
| 8 | Services/ColorMapping/ColorTransformPlan.cs | git diff | 能力表 CanHaveBothIccAndCicp，已读 |
| 9 | Services/ColorMapping/RawColorPipeline.cs | git diff + 实测 | avifenc --icc --cicp 同写，已读 |
| 10 | Services/FfmpegCommandBuilder.Decision.cs | git diff + 行号核对 | 569-582 PNG strip（COLOR-01），已读 |
| 11 | Services/IccProfileBuilder.cs | git diff + exiftool | cicp tag/matrix=0/FullRange=1，已读 |
| 12 | Services/IccProfileService.cs | git diff + exiftool | 版本解析修复 v4.4.0，已读 |
| 13 | Services/PngCicpService.cs | git diff + pngcicp | cICP 写/剥旧块，已读 |
| 14 | docs/review-2026-11-14/ICC_VERSION_COMPARISON.md | 通读 | 口径已过期→见 COLOR-03：§54/§111/§212 仍写"AVIF 不写 ICC"，本批 avifenc --icc 双写推翻 |
| 15 | docs/review-2026-11-14/COLOR_ANNOTATION_PRIORITY.md | 通读 | 口径已过期→见 COLOR-03：§67 表把 avif 与 png 并列"只写 CICP"，本批 AVIF 改双写；PNG 的 CICP 优先原则仍成立 |
| 16 | docs/review-2026-11-14/COLOR_MATRIX_9X9_2026-11-14.csv | 只读表头/取值说明 | 未验证+原因：9×9 矩阵取值未独立重推导（本路只判色彩语义，不重算矩阵） |
| 17 | docs/review-2026-11-14/COLOR_MATRIX_IO_ANALYSIS_2026-11-14.md | 通读 | 口径已过期→见 COLOR-03：§27 行9"AVIF ExitWritesIcc=false/不写 ICC"被本批推翻；矩阵数学层与 exiftool fail-open 结论与本批无关仍成立 |
| 18 | docs/review-2026-11-14/COLOR_MATRIX_IO_FIX_PLAN_2026-11-14.md | 通读 | 口径已过期→见 COLOR-03：文档抬头自带 2026-11-15 更正，自承 PNG 双写读数作废；P1-C 系列（v4.4 重写/cicp tag）与本批代码一致 |
| 19 | docs/review-2026-11-14/COLOR_UI_LINKAGE_AUDIT.md | 通读 | 口径已过期→见 COLOR-03：§48"AVIF 静态出口写不进 ICC"已被推翻；UI 锁（isBaking）结论属 UI 路，本路不判 |
| 20 | docs/review-2026-11-14/ICC_SPEC_VERSIONS_AND_LICENSING.md | 通读 | 未发现问题：规范/许可调研（lcms2/WIC/ColorSync 支持矩阵、再分发合规），与本批代码改动无冲突 |
| 21 | docs/review-2026-11-15/COLOR_MATRIX_IO_ANALYSIS_2026-11-15.md | 通读 | 口径已过期→见 COLOR-03：§12-13 抬头已自更"AVIF 走 avifenc --icc"，但 §37-38 仍写"静态 AVIF 不写 ICC 只能 CICP"，自相矛盾且被本批实测推翻 |
| 22 | docs/review-2026-11-15/ICC_STANDARD_AUDIT_2026-11-15.md | 通读 | 口径已过期→见 COLOR-03：§37/§163"AVIF 无 ICC"是 11-15 时间点读数，本批 avifenc --icc 后 AVIF 已双写；ICC 文件结构（chrm/chad/cicp/v4.4.0）结论经本批 exiftool 实测仍成立 |
| 23 | tests/.../17_ProbeContract.cs | git diff | exit=91 断言，已读 |
| 24 | tests/.../61_ProbeIcc.cs | git diff | v2/v4 位深探针，已读 |
| 25 | tests/scripts/verify-color-strategy.ps1 | git diff + 实跑 | 78/0 绿，已读 |
| 26 | tests/scripts/verify-decision-delivery.ps1 | git diff + 实跑 + 复现 | 78/9 红（COLOR-02/07/08），已读 |

### 3.1 9 条 FAIL 二分表（verify-decision-delivery rc=1）

| # | FAIL 断言 | 归类 | 一句理由 |
|---|---|---|---|
| 1 | ⑭ 直连通路内嵌源 ICC | **产品缺陷 COLOR-07** | legacy WebP carry 被 `planPromisesGeneratedIcc` 短路，零 ICC 零 CICP（本批新引入） |
| 2 | ⑮ 链末曲线==产物标签 | **产品缺陷 COLOR-08** | 链 t=iec61966-2-1 vs cICP 标签 transfer=bt709（存量红，a9e2830 已存在） |
| 3 | ⑯ 请求12⇒命令行/产物都12bit | 门禁未随新路由更新 | avif12 现走 avifenc `--depth 12,4`，门禁找 ffmpeg 命令行位深标记（实得空） |
| 4 | ⑯ 降档要点名 | 门禁未随新路由更新 | 16→12 走 Sample Transform 不算降档，门禁仍期待降级文案 |
| 5 | ⑰b legacy+显式P3 必须出货 | 门禁未随新路由更新 | legacy 12bit AVIF 现 exit 91 fail-closed（不静默降档），门禁仍期待 exit=0 |
| 6 | ⑰b legacy P3 格 primaries smpte432 | 门禁未随新路由更新 | 同上，exit 91 无产物故 primaries 读空 |
| 7 | ⑰b 对照组 legacy sRGB | 门禁未随新路由更新 | 同上 |
| 8 | ⑰b 不误伤锁 legacy auto | 门禁未随新路由更新 | 同上 |
| 9 | ⑱-4 PQ 臂 -color_trc 必须在 -i 前 | 门禁未随新路由更新 | PQ→AVIF 现走 avifenc，命令行无 ffmpeg `-color_trc`/`-i` 可查（位置=-1） |

合计：产品缺陷 **2**（⑭、⑮）｜门禁未随新 avif16 路由更新 **7**（⑯×2、⑰b×4、⑱-4×1）。


## 4. 3 条最关键发现

1. **COLOR-01（HIGH）** — `src/FfmpegGui/Services/FfmpegCommandBuilder.Decision.cs:569-582`：显式 `carry` 在 P3→PNG 上，engine/auto 双写 iCCP+cICP，legacy 仅写 cICP。证据：`tests/output/_tmp/review-2026-11-16/color/out/png-carry-engine`（iCCP 405B）vs `png-carry-legacy`（无 iCCP），pngcicp info 对撞。
2. **COLOR-02（MEDIUM）** — `tests/scripts/verify-decision-delivery.ps1:962-983`：新 avif16 `--depth 12,4` 路由让 ⑯/⑰b/⑱ 共 9 处 FAIL（legacy 12bit AVIF 现 exit 91 fail-closed；门禁仍找 ffmpeg 命令行标记）。证据：`logs/color/verify-decision-delivery.log` + 复现 `out/repro-17b.stdout`。
3. **COLOR-03（MEDIUM）** — `docs/review-2026-11-14/ICC_VERSION_COMPARISON.md:54,111,212`（及 AGENTS.md §7）仍写"AVIF 静态出口不写 ICC"，但 928ec89 后 AVIF 经 avifenc 同写 ICC+CICP。证据：`out/avif-rec/a2_srgb_p3.avif` exiftool 同时有 nclx 与 v4.4 ICC，`avif-rec.stdout` 的 avifenc 命令行。

## 5. 未验证清单

- skcms 样本体积（584/2612/8708 B）与自生成"544 B/10 tag"未独立复测。
- `COLOR_MATRIX_9X9_2026-11-14.csv` 矩阵取值未独立重推导。
- ProPhoto（非 D65）源 → AVIF 的 `--cicp 2/2/0` 兜底分支与 `NormalizeSourceIccToV44` D50 适应烘焙，无夹具未实测。
- HDR（PQ/HLG）源在 AVIF 双写路径下 ICC 内部 cicp tag 与容器 nclx 的一致性，仅测了 SDR P3。

## 6. 交付物路径

- 本报告：`docs/review-2026-11-16/review-evidence/01-color-core-report.md`
- findings：`docs/review-2026-11-16/review-evidence/findings-COLOR.csv`
- 日志：`docs/review-2026-11-16/review-evidence/logs/color/`（含 verify-color-strategy.log、verify-decision-delivery.log、diff-*.txt、*.stdout）
- 转码产物：`tests/output/_tmp/review-2026-11-16/color/out/`
