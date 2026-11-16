# 48h 变更审查 · 报告与证据规范（批次 2026-11-16）

审查范围：`a9e2830..957cfa8`（18 提交 / 75 文件 / +8777 −1095）。HEAD=`957cfa8`，tag `1.6.0BETA17`。
本文件是四路（五份）子报告的**共同基准**，任何偏离都会在汇总裁决时被退回。

---

## 1. 只读红线（违反即整份作废）

- 不修改 `src/`、`tests/`（含被跟踪脚本）、`pack.ps1`、`CHANGELOG*`、版本位（`FfmpegGui.csproj` 的 `<Version>`）、`docs/` 正文。
- 不 `git commit` / `git push` / 不新建、不移动、不删除 tag / 不重新出包。
- 不运行 `-FullBaseline`（104 条全量），只跑与本链路相关的针对性门禁。
- 不运行 `tests/scripts/_sweep-output.py`（会删除他人产物）。
- 不"顺手修代码"。所有问题只登记。

## 2. 产物落盘位置（两个区，别混）

| 类型 | 位置 |
|---|---|
| 报告、findings CSV、文本/JSON 日志、哈希清单 | `docs/review-2026-11-16/review-evidence/`（`logs/<lane>/`） |
| 转码产物、图片、包、二进制中间件 | `tests/output/_tmp/review-2026-11-16/<lane>/`（gitignore 区，单件 <50MB） |

- 子进程临时件一律钉死在 `tests/output/_tmp/review-2026-11-16/<lane>/tmp`
  （PowerShell 里 `$env:TMP=$env:TEMP=$env:TMPDIR=<该路径>` 后再起 ffmpeg/宿主/gate）。
- 禁止在 `%TEMP%`、`C:\Temp`、`C:\PLAN\` 下新建任何目录或文件。

## 3. 工具绝对路径（本机 PATH 里都没有，必须写全）

- git：`C:\Program Files\Git\bin\git.exe`
- dotnet：`C:\Program Files\dotnet\dotnet.exe`（SDK 11.0.100 / 10.0.403）
- ffmpeg / ffprobe：`C:\PLAN\ffmpegPictureUI\publish\PLAN\ffmpeg-full\ffmpeg.exe` / `ffprobe.exe`
- exiftool：`C:\PLAN\ffmpegPictureUI\publish\PLAN\exiftool\`（目录内 exiftool 可执行）
- avifenc：`C:\PLAN\ffmpegPictureUI\publish\PLAN\artifacts\avifenc.exe`
- oxipng：`C:\PLAN\ffmpegPictureUI\publish\PLAN\artifacts\oxipng.exe`
- dngtool：`C:\PLAN\ffmpegPictureUI\publish\PLAN\artifacts\dngtool.exe`
- pngcicp：`C:\PLAN\ffmpegPictureUI\tools\src\pngcicp\bin\Release\net11.0\pngcicp.exe`
- JXL 工具链：`C:\PLAN\ffmpegPictureUI\publish\PLAN\jxl\bin\`（cjxl/djxl/jxlinfo）
- python：命令行 `python` 直接可用
- 被测二进制（beta17）：`C:\PLAN\ffmpegPictureUI\publish\build\FFmpegPictureUI-v1.6.0-beta17-x64-full\FfmpegGui.exe`（full 包内含 PLAN 工具链）；app 包（无 PLAN）：`...\-beta17-x64\FfmpegGui.exe`
- 测试宿主：`tests/projects/*/bin/Release/`（已构建，勿重编到共享 bin）

## 4. 判定规则：不许"看 diff 标题就下结论"

- 色彩/格式类结论必须落到**真实转码产物**的回读：ffprobe（`-show_streams -show_frames` 取 color_primaries/transfer/matrix/range、bits_per_raw_sample）、exiftool（ICC/XMP/CICP 标记）、pngcicp（PNG 的 cICP+iCCP 归一化视图）、jxlinfo。
- 计数类结论必须给出**命令 + 原始输出文件路径**，不能只写数字。
- 门禁能否"真红必须有**变异/负控**证据：改一个判据点（用副本，不动原件）或跑一次已知失败输入，观察是否 rc≠0。
- 文档里的每个数字都要能追到某条命令/日志；追不到就写"未验证+原因"，**不得写成通过**。
- 凡是"读了代码觉得对"的结论，等级最高只能到 MEDIUM，且必须在 verified 列写 `代码推断(未实测)`。

## 5. findings CSV（机械可汇总，四路同一表头）

路径：`docs/review-2026-11-16/review-evidence/findings-<lane>.csv`，UTF-8，逗号分隔，首行为表头，字符串内含逗号/换行必须用双引号包裹。

```
id,severity,file_line,title,repro,evidence,risk,suggestion,verified
```

- `id`：`<LANE>-01`、`<LANE>-02`…（LANE ∈ COLOR/FMT/UI/BUILD/GATE）
- `severity`：`CRITICAL` / `HIGH` / `MEDIUM` / `LOW`
- `file_line`：`相对仓库路径:行号`（多文件用分号分隔）
- `repro`：能重放的最小步骤（含完整命令或"读某函数第 N 行分支"）
- `evidence`：日志/产物路径 + 关键读数（必要时附 sha256 前 16）
- `verified`：`实测` / `代码推断(未实测)` / `未验证:<原因>`

**等级口径（全批次统一，子报告不得自行放宽）**

- `CRITICAL`：默认路径上的静默错误结果（色彩标记错、ICC/CICP 丢或写错、产物不可解码）、数据损坏、默认配置崩溃/卡死、发布包默认功能不可用。
- `HIGH`：已发布场景下的用户可见故障；门禁对真实回归"永远绿"；打包/版本/哈希口径错误；兼容性硬破坏（旧预设/CLI 行为漂移）。
- `MEDIUM`：边界条件、少用格式/路径、文档与代码不一致、门禁口径漂移但仍有覆盖、需特定环境才触发。
- `LOW`：措辞、注释、命名、可读性、无行为影响的口径不统一。

## 6. 报告正文结构（每份必须齐全）

```
# <lane 中文名> 审查报告（a9e2830..957cfa8）
## 0. 结论摘要（3-6 条，含最高级别）
## 1. 覆盖清单            ← 表格：本路分到的每个文件 → 结论/未发现问题/发现问题 id
## 2. 发现详情            ← 每条按 §7 的七段式
## 3. 门禁与负控实测      ← 跑了哪条、rc、日志路径；没跑的说明原因
## 4. 未验证项与原因
## 5. 跨模块候选（只登记现象，不下结论）← 例如 oxipng 同时影响 色彩/UI/打包
## 6. 复现命令附录
```

## 7. 单条发现七段式（缺一段即退回）

1. **级别** + `id`
2. **位置**：`文件:行`（函数名/分支名）
3. **现象**：一句话事实描述（不许夹结论性形容词）
4. **复现步骤**：命令级或代码分支级
5. **证据**：命令原文 + 日志/产物路径 + 关键读数（哈希/标记值/rc）
6. **风险评估**：影响哪些格式/场景/用户，是否会静默
7. **建议**：具体到改哪个函数/判据；**不写实现代码，不落地修改**

## 8. 已知事实（不要再推导，直接引用）

- beta17 已出包并推 origin，`1.6.0BETA17` → `957cfa8`；本轮禁止动 tag。
- `verify-ui-host` 在 beta17 构建为 **487 / 0**；`verify-ui-split-reproducible` **19 / 0**；
  `verify-package-gui-starts` **6 / 0 / 1 SKIP**（提权托盘限制）；build **0 errors / 5 warnings**。
- `verify-ui-split-union` 读数 **7 PASS / 4 FAIL**：旧宿主 477 名、拆分件 487 名、差 10 条启动检测。
  该差异的定性（口径漂移 vs 真实回归）**由 GATE 路给出证据，由总控统一裁决**，其它路不得自行下结论。

## 9. 冲突与去重

同一现象被两路都记到时，各自保留证据但**不各自裁决**；由总控在汇总表里合并为一条并注明两路证据路径。
跨模块（oxipng / 默认 max / 提权托盘 / 版本位）一律走 §6 第 5 节登记，不写入第 2 节的 CRITICAL 结论。
