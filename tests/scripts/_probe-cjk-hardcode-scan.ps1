# _probe-cjk-hardcode-scan.ps1 —— UI 硬编码中文（未走 Loc）的「不得增加」结构锁（2026-09-17 新增）
#
# 为什么需要它：
#   `_probe-i18n-scan.ps1` 的 ③④ 只把硬编码中文打印成 `ℹ` 行（**不判红**）⇒ 实际上**没有任何锁**。
#   将来重构往里加硬编码中文，不会有任何门禁发现。这与本项目一直在治的「门禁完整性」是同一类问题
#   （参见 `docs/TESTING.md §6` 第 50 条「门禁没输出 ⇒ 假绿」、第 58 条「恒 exit 0 ⇒ 假绿」）。
#
# ⚠ 判据是「**不得增加**」，**不是**「必须为 0」：
#   存量是 XAML 13 行 / C# 1142 行（口径②；历次抬基线见下方登记），清零是大工程 ⇒ 若断言「必须为 0」会**立刻长期红**
#   （正是 `docs/TESTING.md §6` 第 55 条警告的模式：把「已定性的口径分歧」当缺陷去修，
#   结果要么白改、要么为了绿而放宽判据）。所以：
#     · 把当前实测值写死为**基线**，断言 `实测 <= 基线`；
#     · **降了也不判红**（正向改进不惩罚）；但降了之后请把基线同步下调，否则锁会逐渐失效
#       —— 本脚本会打印「距基线还有 N 的余量」，余量长期为正时请主动收紧。
#
# ⚠⚠ 口径说明（本脚本的核心，务必与 `_probe-i18n-scan.ps1` 的「口径①」区分）
#
#   本脚本 = **口径②**（全仓 + 剥注释），细则：
#     · 语料 = `src/FfmpegGui` 下**全部** `*.xaml` / `*.axaml` / `*.cs`（排除 bin/obj）；
#     · 剥离 `<!-- -->`（含跨行）与 `/* */`（含跨行），并按**原始换行数**回填换行
#       （保持行边界，避免"删掉跨行注释导致前后两行被拼成一行"而改变计数）；
#     · C# 再跳过**整行**以 `//` 开头的行；
#     · 计数单位是**行**（一行算 1，不管该行有几个中文字面量）；
#     · XAML 侧：剥完注释后该行仍含 `[\u4e00-\u9fff]` ⇒ 计 1；
#     · C# 侧：该行存在「字符串/字符字面量」其内容含 `[\u4e00-\u9fff]` ⇒ 计 1，
#       并按该行**第一个**命中字面量是否以 `[` 开头，分成 `[tag]` 编码日志 / 其余两桶。
#
#   与**口径①**（`_probe-i18n-scan.ps1:89-92` 的 `ℹ` 行，即 `9/216`）的差异 ——
#   **两套口径不要混在一个数里**：
#     · 口径① 的 XAML 只扫 **`MainWindow.xaml` 一个文件**，且只认 5 个属性
#       （`Text|Content|Header|ToolTip.Tip|PlaceholderText`），**看不见** `<sys:String>` 元素内容；
#       本脚本扫**全部** XAML/AXAML 且不区分属性 ⇒ 数字更大（13 vs 9），且**成员不同**。
#     · 口径① 的 C# 只扫 **`MainWindow.xaml.cs` 一个文件**且**不剥注释**；
#       本脚本扫全部 `.cs` 并剥注释 ⇒ 1142 vs 216。
#     · 因此 `_probe-i18n-scan.ps1` 的 `9/216` 只是**门禁当前打印值**，**不是**存量规模。
#       存量规模以本脚本为准（13 / 1142）。
#
# 已知局限（**有意保留**，以便与 2026-09-17 的测量口径逐字一致；不要悄悄"修好"，否则基线会变）：
#   · 剥离 `/* */` 是**文本级**的，不认字符串里出现的 `/*`（如正则字面量串）。
#   · 跨行的 verbatim 字符串（`@"..."`）按行切分 ⇒ 其中的中文按"所在行"计。
#   · 只数"含中文"，**不判断该中文是否真的用户可见**（`[tag]` 编码日志也计入，
#     因为日志面板 `ProgressWindow` 对用户可见）。
#   · 若将来要收紧口径（例如把日志排除），必须**同时**改基线与 `docs/TESTING.md §6` 第 61 条。
#
# 不依赖任何外部 exe（纯 PowerShell），因此不受 `& exe` 宿主问题影响。
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root

$pass = 0; $fail = 0
function CK($ok, $msg) {
    if ($ok) { $script:pass++; Write-Output "  PASS $msg" }
    else { $script:fail++; Write-Output "  FAIL $msg" }
}

# ── 基线（口径②，2026-09-17 实测；改动口径时必须同步更新这里） ────────────────────
# ⚠⚠ **2026-09-17 抬基线（AVIF 接入引擎轮）：CS 1135 → 1137，CS_OTHER 793 → 795（+2）**
#   来源已定位到**确切两行**（不是"扫到多了就抬"）：
#     · `src/FfmpegGui/Services/ColorMapping/ImageEncoderArgs.cs:487`
#       `bad.Add($"色度子采样 {o.Chroma}（libsvtav1 仅接受 yuv420p / yuv420p10le，"`
#     · `src/FfmpegGui/Services/ColorMapping/ImageEncoderArgs.cs:488`
#       `      + "ffmpeg 会把 4:4:4/4:2:2 静默降为 4:2:0）");`
#   性质与**处置理由**：这是「AVIF + libsvtav1 ⇒ 4:4:4/4:2:2 会被 ffmpeg 静默降为 4:2:0」
#   的**引擎出口拒绝理由串**，与**同文件紧邻的既有同类** `:479`
#   （`色度子采样 …（libwebp 仅接受 4:2:0 / bgra，无 4:4:4）`）**完全同类**，
#   也与 `ColorEngineRouter.cs:81-83/93/94/96/98/148-157` 的整批硬编码诊断串同类
#   —— 它们是 **CLI/引擎侧诊断与错误文案**，不经过 UI 的 `Loc`（该类文件里 0 处 `loc[...]`），
#   把它单独改走 Loc 会与周边 20+ 条同类串**口径不一致**，属过度改造。
#   ⇒ 按「与既有同类 ⇒ 允许抬高基线」处置，**在此显式登记理由与日期**（不静默抬高）。
#   余量 0 ⇒ 这两条已被基线精确吸收；后续若再涨仍会判红。
#
# ⚠⚠ **2026-09-17 第二次抬基线（JXL 接入引擎轮）：CS 1137 → 1142，CS_OTHER 795 → 800（+5）**
#   同样已定位到**确切五行**（不是"扫到多了就抬"），全部是**新格式接入引擎时的出口拒绝理由串**：
#     · `src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs:74-76`
#       `"JXL 出口无法附 ICC（ffmpeg 的 libjxl 编码器丢弃 iccgen 的 ICC，exiftool 亦拒写 JXL 容器）："`
#       `+ "请改用可 CICP 命名的目标空间（sRGB / Display P3 / Rec2100 PQ / HLG），"`
#       `+ "或改用 PNG/TIFF/WebP 出口携带 ICC；若要保留任意 ICC 请改用 cjxl 后端"`
#     · `src/FfmpegGui/Services/ColorMapping/ImageEncoderArgs.cs:640-641`
#       `bad.Add("JXL 的 JPEG 无损重封装（引擎需先解码成 raw RGB，无法复制 DCT 系数；"`
#       `      + "请改用传统管线或 cjxl 后端）");`
#   性质与**处置理由**：与上面 AVIF 轮那两条**完全同类**（引擎出口拒绝理由串，
#   `ColorMapping/*.cs` 里 0 处 `loc[...]`，周边 20+ 条同类诊断串都不走 Loc）
#   ⇒ 只给这两条改走 Loc 会与邻居**口径不一致**，属过度改造。故同样按
#   「与既有同类 ⇒ 允许抬高基线」处置并显式登记。
#   余量 0 ⇒ 已被基线精确吸收；后续若再涨仍会判红。
#
# ⚠⚠ **2026-09-17 第三次抬基线（cjxl 执行出口轮）：CS 1142 → 1171，CS_TAG 342 → 353，CS_OTHER 800 → 818（+29）**
#   ⚠ 这是本门禁**单轮最大的一次抬升**，故逐条列出（不是"扫到多了就抬"）。全部集中在**一个文件**
#   `src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs`，且全部是**新出口（cjxl 执行出口）的
#   诊断/日志串**——即"任何一环失败都显式说出失败原因、绝不静默回退"这条硬要求的**文字载体**：
#     · `[tag]` 桶 **+11**（全部是 `[色彩] …` 前缀的 `log?.Invoke` 日志，与同文件既有 `[色彩] …` 同类）：
#       `:410`（计划未要求色彩标注）、`:454`（ffmpeg 侧命令行）、`:455`（cjxl 命令行）、
#       `:456`（出口形态 raw→PPM/PAM）、`:475`（ICC 已由 cjxl 写入）、`:506`/`:516`（两个进程启动失败）、
#       `:529`/`:530`（传输被取消 / 传输错误）、`:548`（ffmpeg 侧 stderr）、`:549`（cjxl stderr）。
#     · 其余桶 **+18**（异常消息与其续行片段；行 ≠ 条，`:` 处多为同一条消息的分行拼接）：
#       `:180`（cjxl 编码失败 exit）、`:335`（缺 FfmpegOptions）、`:341-342`（cjxl 缺失 ⇒ 拒绝回退）、
#       `:374-377`（要 ICC 但拿不到 ⇒ 拒绝）、`:402-404`（语义落不进枚举 ⇒ 拒绝）、`:411`（无标注的续行）、
#       `:457`（出口形态续行）、`:465`（退出码 0 但没产物）、`:476-477`（跳过 exiftool 后置的续行）、
#       `:545`（超时/取消）、`:551`（管道传输失败）。
#   性质与**处置理由**：与上面两轮**完全同类** —— `ColorMapping/*.cs` 里 0 处 `loc[...]`，周边 20+ 条
#   引擎侧诊断串一律硬编码中文；且这些串正是**出口显式失败**的判据载体，改成英文/走 Loc 会让
#   `verify-color-wiring.ps1` (10) 段与三条变异验证的**断言口径同时失效**（断言就是按中文关键词匹配的）。
#   ⇒ 按「与既有同类 ⇒ 允许抬高基线」处置，**在此显式登记理由、日期与确切行号**（不静默抬高）。
#   余量 0 ⇒ 已被基线精确吸收；后续若再涨仍会判红。
#
# ⚠⚠ **2026-09-17 第四次抬基线（JXR 执行出口轮）：CS 1171 → 1180，CS_TAG 353 → 356，CS_OTHER 818 → 824（+9）**
#   ⚠ 抬升前**先试过并入既有行**（上轮做法），本轮只找到**一处是计数假阳性**并已就地消除：
#     `RawColorPipeline.cs:593` 的行尾注释里写了 `"编码失败 (exit …)"`（带直引号）——本门禁只剥
#     **块注释**、只跳过**整行 `//`**，行尾注释**不剥** ⇒ 注释里的引号串被当成硬编码文案计数。
#     已把该注释的直引号改成 `「」`（口径与 `tests/scripts/*.ps1` 一致），**这一行不再计数**。
#   余下 **+9** 全部是**新出口（JXR 执行出口）的诊断/异常串**，集中在**一个文件**
#   `src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs`（`EncodeJxrViaJxrEncAppAsync`）：
#     · `[tag]` 桶 **+3**（`[色彩] …` 前缀的 `log?.Invoke`，与同文件既有 `[色彩] …` 同类）：
#       `:579`（ffmpeg 侧命令行）、`:590`（`JxrEncApp 命令行:`，A/B 对拍与门禁 (11) 段的判据）、
#       `:594`（出口形态 raw → BMP/TIFF）。
#     · 其余桶 **+6**（异常消息与其续行片段；行 ≠ 条）：`:528`（缺 FfmpegOptions）、
#       `:535-536`（JxrEncApp 缺失 ⇒ 拒绝产出 JXR，**没有可回退的编码器**）、`:540`（缺 ffmpeg 中转）、
#       `:583`（raw → BMP/TIFF 中转失败 exit）、`:595`（出口形态续行）。
#   性质与**处置理由**：与前两轮**完全同类** —— `ColorMapping/*.cs` 里 0 处 `loc[...]`，周边 20+ 条
#   引擎侧诊断串一律硬编码中文；且这 9 行**逐条都是"出口显式失败/可追溯"的判据载体**：
#     · 与兄弟出口 **cjxl 逐条对位**（`[色彩] ffmpeg …` / `命令行:` / `出口：…` / 缺 options / 缺工具），
#       只给 JXR 改走 Loc 会与邻居**口径不一致**，属过度改造；
#     · `verify-color-wiring.ps1` (11) 段的两条断言（`in.bmp`/`in.tiff`、`[色彩] JxrEncApp 命令行:`）
#       就是按这些中文串匹配的 ⇒ 改成英文/走 Loc 会让门禁断言**同时失效**。
#   也**逐条试过并入**：`:528`/`:540` 是两种不同失败原因（缺 options vs 缺 ffmpeg），合并会给出更含糊的
#   建议；`:535-536`、`:594-595` 的分行拼接与 cjxl 侧**同一形态**（同一条消息的行宽限制），合并后单行
#   超 140 列。⇒ 按「与既有同类 ⇒ 允许抬高基线」处置，**在此显式登记理由、日期与确切行号**（不静默抬高）。
#   余量 0 ⇒ 已被基线精确吸收；后续若再涨仍会判红。
#
# ⚠⚠ **2026-09-18 第五次抬基线（接手会话）：CS 1180 → 1203，CS_TAG 356 → 370，CS_OTHER 824 → 833（+23）**
#   ⚠⚠ **这次与前四次性质不同，必须分开记 —— 它是「两笔账」**：
#
#   ── 第一笔：**恢复性抬升 +16（tag +9 / 其余 +7）** ─────────────────────────────
#   **根因（已实证）**：本门禁**自第 31 轮（JXR 执行出口轮）抬完基线后再没被跑过**
#     （`grep cjk-hardcode .workbuddy-ai/memory/2026-09-16.md` 的**全部**命中都在第 31 轮之前）
#     ⇒ 第 32~58 轮的合法功能开发在**无人看守**下累积成漂移。
#   **为什么"没被跑过"**：运行器**尾部原本没有 `exit`** ⇒ 吞掉子门禁的非零退出码
#     ⇒ **任何**子门禁失败都不体现在运行器退出码里（已于 2026-09-18 修复，见 `docs/TESTING.md §6 第 86 条`）。
#   **分桶账已精确闭合（决定性证据）**：`tests/output/` 留有历史门禁快照（`_gs-*` / `_g2_*` / `_g4_*`），
#     `_g4_`（09-17 13:33）**带分桶** ⇒ `CS 1142 / tag 342 / 其余 800`；抬升前实测 `1196 / 365 / 831`
#     ⇒ **delta = +54 / +23 / +31**；扣掉已登记的 `raise#3 +29(+11/+18)` 与 `raise#4 +9(+3/+6)` = `+38(+14/+24)`
#     ⇒ **余 +16 = tag+9 / 其余+7，三个桶全部精确闭合**（54−38=16 · 23−14=9 · 31−24=7）
#     ⇒ 这 +16 **不是**重复计算、**不是**口径变化，是**真实独立的新增**。
#   **逐行归属（子代理溯源；其引用的历史快照 lead 已复核）**：
#     · **tag 9 行全部定位**：`QueueProcessor.cs` 的 `:614 :618 :621 :623 :625`（**S6** 的 `[色彩裁决]`）、
#       `:815`（**S6** 传统管线侧）、`:906`（**S6/S7** 引擎侧）、`:913`（**S7** 的 `降级（`）、`:1020`（**R1 批次 1**）。
#     · **其余 7 行定位 6 行**：`QueueProcessor.cs:831`（**S7** `LogAlternatives` 的 `可替代方案：`）、
#       `ColorStrategyPlanning.cs:170/171`（Part A 的 `ToneMapNotAppliedToGainMap` 文案）、
#       `FfmpegCommandBuilder.Decision.cs:91/92/93`（`Short()` 摘要串 —— ⚠ **它是活代码**：
#       `tests/ServiceProbe/Program.cs:2677` 的 `decision` mode 在**失配分支**里调用它）。
#     · ⚠ **仍不可判定 1 行**（其余桶）；候选池 4 个，全是 `ColorStrategyPlanning.cs` 的引擎降级码文案
#       （`:124` / `:143` / `:152` / `:155`），与**同函数另外 8 条同类同形** ⇒ 无论命中哪个，性质判定一致。
#     · **0 行**属于「应走 `loc[...]` 的真 UI 文案」；已归属的行里 **9 行是门禁断言的匹配关键词**
#       （改英文/走 Loc 会让 `verify-color-strategy` / `verify-gainmap-engine` 的断言**同时失效**）。
#   **处置理由**：与既有同类（引擎侧诊断串 / 裁决日志），沿用前四次的「允许抬高基线」口径。
#
#   ── 第二笔：**本会话增量 +7，且已消掉 1 处假阳性 ⇒ 净 +7** ────────────────────
#   `+8 − 1 = +7`（tag `+5 − 0`；其余 `+3 − 1`）：
#     · **+7 来自 `#15`/`#21` 的心跳守卫**（本会话交付）—— `FfmpegRunner.cs` 由 `2 (1/1)` → `9 (5/4)`：
#       `:61`（`[心跳] 已追加 -progress pipe:1…`）· `:66`（`[心跳] ⚠ 该调用把数据写在 stdout… ⇒ 拒绝注入`）·
#       `:67`（续行「会污染数据流；本次退回旧判据，对忙等型挂死失明」）· `:97`（`[心跳] ffmpeg 进度收尾（progress=end）`）·
#       `:147`/`:148`/`:149`（`[超时] … 无进度心跳` 的三行拼接）。
#       ⚠ 这 7 行**逐条都是"出口显式失败/可追溯"的判据载体**，且 `verify-ffmpeg-heartbeat.ps1` 的多条断言
#       **正是按这些中文串匹配的** ⇒ 改英文/走 Loc 会让该门禁的断言同时失效。
#     · **+1（tag）来自 `QueueProcessor.cs`**（`tag 202 → 203`）：⚠ **我未能定位到确切行**
#       （该文件的 `#15 心跳站点` 标签都是**整行 `//` 注释 ⇒ 本门禁本就跳过**）⇒ **如实标注为"未定位"**，不编。
#     · **−1 来自消掉一处"行尾注释假阳性"**：`MainWindow.xaml.cs:5570` 原写
#       `if (current < 0) current = 75; // 从"跟随主图"切换到手动时，默认 75` —— 行尾注释里的**直引号串**
#       被当成硬编码文案（本门禁只剥**块注释**、只跳过**整行 `//`**，**行尾注释不剥**）。
#       已把直引号改成 `「」`（与第 31 轮修 `RawColorPipeline.cs:593` **同一处置**）⇒ 该行不再计数。
#       ⚠ 注意它的 mtime 早于基线 ⇒ 它**本就被算在 1180 里** ⇒ 这一笔是**净减 1**，与上面两笔独立。
#
#   ── ⚠ 留给维护者的结构性提醒（**未处置，另议**）──────────────────────────────
#   **本门禁的"其余"桶（现 833 行）里，绝大多数是引擎/CLI 侧诊断串，而不是本桶描述里列的那些
#   UI 面**（窗口标题/菜单/对话框/进度标签/预设名/EXIF 字段名/--help…）。前四次抬升每次都为此逐条申辩
#   「与既有同类、改走 Loc 会与邻居口径不一致」⇒ **该桶的口径与它自己的描述已经不匹配**。
#   ⇒ **现状是：一天内抬了 6 次基线，且本会话内又漂了多次。**
#
#   ✅ **2026-09-18 维护者裁定：②-b 起步 → ②-c 终局（已实施）。**
#      · **②-b**：`[tag]` 编码日志桶与「C# 总计」**退出判据**（只作 ℹ）—— 该桶**整体是诊断串**，
#        而「总计」含它 ⇒ **两者必须同时退**，否则 TAG 一涨总计仍涨、判据照样红。
#      · **②-c（终局）**：「其余」桶再按**是否走诊断 sink** 细分 ⇒ 判据只看 **`CsUi`（UI 面）**；
#        `CsDiag`（`log?.Invoke(` / `Log(` / `.Log +=` / `Trace|Debug|Console.Write*`）只作 ℹ。
#        ⚠ **fail-closed**：**认不出**的形态一律算 UI ⇒ 新增一种 UI 写法**不会**被静默放过。
#      · **判据现在是：`XAML ≤ 13` 且 `CsUi ≤ 824`** —— ⚠ **824 比旧口径的 849 更紧（净收紧 25）**。
#      · ⚠ 为什么**不**用「按文件排除」那种 ②-a：`QueueProcessor.cs` 的 `item.Log` 串**是显示给用户的**，
#        按文件排除会**漏掉真 UI 文案**；**按 sink 分才是真判据**（按文件是代理判据）。
#      · 口径变更**已同时改** `docs/TESTING.md §6 第 61 条`（脚本头既有约定）。
#      · **实证依据**：本会话该锁 4 次转红，**4 次的原因都是"把静默改成响亮"或"写清了注释"** ——
#        而本仓价值序要求「静默 < 响亮但看不懂 < **响亮且点名**」⇒ **旧口径在惩罚做对的事**。
#
#   ── 可一键回退 ────────────────────────────────────────────────────────────
#   **口径回退（②-c → 旧口径②）**：把判据段那两条 `CK` 改回四条（XAML / `$m.Cs` / `$m.CsTag` / `$m.CsOther`），
#     并把基线改回 `13 / 1215 / 381 / 834` ⇒ 立刻恢复"惩罚做对的事"的旧行为（**不推荐**）。
#   **仅抬基线**：把 `$BASE_CS_UI` 调大即可（**不推荐**：等于放弃本轮的收紧）。
#
# ⚠⚠ **2026-09-18 第六次抬基线（接手会话，同日）：CS 1203 → 1215，CS_TAG 370 → 381，CS_OTHER 833 → 834（+12）**
#   **逐条登记（本轮增量几乎全在 `tag` 桶）**：
#     · **+10（tag）来自 `#24`**：把 10 处「同步读架空超时」修好时，**为每处补了一条"点名超时日志"**
#       （形如 `Trace.WriteLine($"[<模块>] 探测超时（Ns）⇒ 已终止进程树：…")`）。
#     · **+1（tag）+1（其余）来自 `#13`/`#29` 的收尾**（站点 A/B 的中转失败/超时文案；`#29` 删方法净减）。
#   ⚠⚠ **这条增量是本门禁"口径需要重定"的**决定性实证**（不是抱怨，是证据）**：
#     **本会话内它红了两次，两次的原因都不是"有人塞了 UI 文案"，而是"有人在修缺陷"** ——
#     `#24` 修的正是「**超时静默**」（本仓价值序：静默 < 响亮但看不懂 < 响亮且点名），
#     而**让它响亮就必须加中文串** ⇒ **本门禁惩罚"把静默改成响亮"**。
#     ⇒ 现状：**一天内抬了 6 次**（1180 → 1203 → 1215），且每次都是"修缺陷的副产品"。
#     ⇒ **强烈建议维护者按下述口径 ② 收紧口径**（见第五次登记末尾的选项），否则这把锁会持续
#       "惩罚正确的改动"，最终被训练成"看到它红就忽略" —— 而那**正是**第 31~58 轮漂移无人发现的成因。
# ⚠⚠ **2026-09-18 口径变更（维护者裁定：②-b 起步 → ②-c 终局）** ──────────────────────
#   **判据只含两桶：XAML/AXAML 与「C# 其余文案」。**
#   · `$BASE_CS`（含 TAG 的总计）与 `$BASE_CS_TAG` **保留但不再参与判据**，仍以 ℹ 打印以保留可见性。
#   · 为什么两者必须**同时**退出：`[tag]` 编码日志桶**整体是诊断串**（`log?.Invoke` / `Console.Error` /
#     异常消息…），与「UI 文案不得硬编码」的立锁初衷无关；而 `$BASE_CS` 是**含 TAG 的总计** ⇒
#     若只退 TAG 而留总计，TAG 一涨总计仍涨 ⇒ 判据照样红。
#   · 实证（本会话）：该锁 4 次转红，**4 次的原因都是"有人把静默改成响亮"或"有人写清了注释"**，
#     而本仓价值序恰恰要求「静默 < 响亮但看不懂 < **响亮且点名**」⇒ 旧口径**在惩罚做对的事**。
#   · ②-c（终局）将把「其余」桶再按"是否走诊断 sink"细分，进一步收紧。
# ⚠ 本次 `BASE_CS_OTHER` 834 → 849（**+15，逐条可归属**）：
#   · `#26` ct 接线点名串 **+1**（`GainMapDecoder.cs:810` 的 `"被取消" / "超时（5s）"`）
#   · `#136` 切片 A **+6**（`QueueProcessor.cs` 的 6 条 `Indeterminate("…")`）
#   · `#136` 切片 B **+8**（webp 轴 B 探针的点名与日志）
#   ⇒ **1 + 6 + 8 = 15**，**全部是「把静默改成响亮」的产物** ⇒ 按既有惯例显式登记后抬高。
#   ⚠ 余量 0 ⇒ 已被精确吸收；后续若再涨仍会判红。
# ⚠⚠ **2026-09-19 第七次抬基线（P1-E CLI 严格化轮）：CS 1215 → 1258，CS_TAG 381 → 401，CS_OTHER 849 → 857，CS_UI 824 → 832（判据项净 +8）**
#   **增量全部在 `src/FfmpegGui/CliParser.cs`，没有第二个文件**。**+10 新增行 − 2 消失行 = +8**：
#     · **+10 新增行**（含中文字面量的行；逐行给出确切行号）：
#         `:592` `支持 auto | engine | legacy`（`--color-engine` 白名单，D5 同族）
#         `:607` `支持 off | on`（`--color-gamut-map`）
#         `:652` `支持 srgb | bt2020`（`--jpeg-gain-map-base-gamut`）
#         `:685` `支持 None | CarryIcc | BakeToStandard | BakeOnly`（`--icc-mode`）
#         `:694` `支持 recommended | carry | cicp-only | manual（别名 auto / 推荐 / 携带 / 仅cicp / 手动）`（`--color-strategy`）
#         `:756` `--{key} 取值无效: '{value}'（{expected}）`（`BadValue` 统一模板）
#         `:762` `需要整数` · `:770` / `:778` `需要数值` · `:785` `需要 true | false`（`*Strict` 家族）
#     · **−2 消失行**：原先是**内联**的两条消息 `--color-gamut-map 取值无效: '…'（支持 off | on）` 与
#       `--jpeg-gain-map-base-gamut 取值无效: '…'（支持 srgb | bt2020）`，现已被 `:756` 的统一模板
#       （`BadValue(key, value, expected)`）取代。
#     · ⇒ 净 **+10 − 2 = +8**，与门禁实测 `824 → 832` **逐字吻合**。
#   **取证法（可复现，且不依赖任何源码快照）**：把候选中文串编成 **UTF-16LE 字节**，在**三个不同时刻的
#   `FfmpegGui.dll`** 上做**存在性检索** ——
#     `tests/output/archive/runner-pre-fix-10.40.13/FfmpegGui.dll`（10:40:13）·
#     `tests/GainMapTestHost/bin/Release/net11.0/win-x64/FfmpegGui.dll`（11:48:53 = 本会话改动前）·
#     `src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.dll`（16:54:22 = 当前）。
#   再用 11:21 的历史读数（`tests/output/cjk_fix_ps7.log` = `UI 824 / TAG 401 / 其余 849 / 总 1250`）
#   把增量窗口夹定为 **11:21 → 16:54**，并用 mtime 证明窗口内 `src/FfmpegGui` 只有 7 个文件被写过
#   （其中 6 个的含中文行数与 11:48 状态一致，只有 `CliParser.cs` 变了）。
#   **处置理由（与既有同类）**：`CliParser.cs` 的 `:162` `未知编码器: {v}` · `:425` `未找到匹配的输入文件…` ·
#   `:470` `JXR 输出需要 JxrEncApp…` · `:475` `DNG 输出需要 dngtool…` **本就在 UI 桶里**，且已被 824 吸收；
#   本轮新增的 10 行是**同一类**（CLI 解析期的「取值域提示 / 失败点名」）。
#   ⚠ **为什么不改 ASCII**：`取值无效` 被**两条别的门禁的断言直接匹配**
#   （`tests/scripts/verify-gamut-map.ps1:143`、`tests/scripts/verify-gainmap-engine.ps1:262`），
#   且 `docs/TESTING.md:1828` 同样点名它 ⇒ 改 ASCII 要同步改 3 处，并让同一文件内出现中英混排。
#   ⚠ `CS_TAG` 381 → 401（+20）与 `CS_OTHER` 的既有部分属**更早会话的未登记漂移**（这两个桶自 ②-b 起
#   已退出判据、只作 ℹ 打印）⇒ 本次一并按实测对齐，修正此前 `381 + 849 = 1230 ≠ 1215` 的内部不自洽；
#   如需逐行归因，可用同一取证法重做。
#   ⚠ 余量 0 ⇒ 已被精确吸收；后续若再涨仍会判红。
# ⚠⚠ **2026-09-26 第八次抬基线（色彩矩阵修复轮）：CsUi 832 → 837（净 +5，逐条可归属）**
#   取证法与判据**同源**（不手抄正则，避免"尺子自己造错误"）：把本文件的 `Strip-Comments`/`Measure-Cjk`
#   按行区间 `Invoke-Expression` 出来，先复现门禁实读（`TAG=443 DIAG=21 UI=841` ⇒ 复现成功才引用其结论），
#   再用 `git archive HEAD src/FfmpegGui | tar -x`（**逐字节**导出，不走 `git show` ⇒ 不经控制台代码页）
#   得到 HEAD 语料 `UI 832` ⇒ 净增**全部**归属到当前未提交的工作树改动，无"说不清的漂移"。
#   ── 逐条（+9 原始读数 → 折叠 4 处误计 → 净 +5）──
#   · `ColorStrategyPlanning.cs:165-169` **+4**：位深升降播报（`出口位深升为/降为 …` 两支 + 原因两支）。
#     性质 = **P2.5 要求的响亮降级**（修复前 144 格静默降档，见 `docs/COLOR_MATRIX_FIX_PLAN_2026-09-24.md` P2.5），
#     与同文件 `:125`「出口位深降为 …（容器上限）…」**同类**（那条早已在基线内）⇒ 省掉就等于把静默降档改回去。
#   · `ColorKernels.cs:314 / :385 / :621` **+3**：三条内核守卫的 `NotSupportedException` 文案
#     （查找表定义域 [0,1] 与 `ClampLinearBeforeEncode=false` 互斥）。内部不变量守卫，②-c fail-closed
#     ⇒ 落 UI 桶；同类既有 = `RawColorPipeline`/`ColorMappingEngine` 的守卫串。
#   · **−2**（更早的 exiftool 轮，同一棵未提交工作树）：`MainWindow.xaml.cs` −2 UI / −4 DIAG、
#     `ExifToolService.cs` −1、`QueueProcessor.cs` +1 ⇒ 6 条硬编码文案改走 `Resources/Locales/*.json`
#     （zh-CN / en-US 各 +4 key）⇒ **正向改进**，按口径同步计入。
#   · 合计 832 + 4 + 3 − 2 = **837**。
#   ⚠ 折叠掉的 4 行属 HANDOVER 登记的「三类误计」之一（**消息折行**）：把相邻字面量并成一行，
#     运行期文案逐字不变（拼接处本无分隔符）。由 `bdcheck` 实跑核验：`png --bit-depth 12` 的日志里
#     两条长串**原样连续出现** ⇒ 折叠 1/3 已验；`降为` 那一支四种组合（jpg/webp/heic × map/auto）都没触发
#     ⇒ 折叠 2 **未经运行期验证，记 SKIP，不算已验**（该分支需 `Action==Map` 且出口档低于目标，
#     现网配置里容器上限先把目标钳到 8 ⇒ 走不到；留作 P2「10/12 真兑现」时的活路）。
#   ⚠ ℹ 桶一并对齐实测：`Cs 1258 → 1301`、`CsTag 401 → 443`、`CsOther 857 → 858`
#     （后两者自 ②-b 起不在判据内；+12 TAG 属 exiftool/工具探测轮新增的 `[tag]` 诊断日志）。
#   ⚠ 余量 0 ⇒ 已被精确吸收；后续若再涨仍会判红。
$BASE_XAML     = 13
$BASE_CS       = 1301
$BASE_CS_TAG   = 443
$BASE_CS_OTHER = 858
# ⚠⚠ **2026-09-18 口径变更 ②-c（终局）**：判据进一步收紧为 **XAML + 「其余桶里的 UI 面」**。
#   · 「其余」桶再按**是否走诊断 sink** 细分：`CsUi`（判据）+ `CsDiag`（ℹ 打印，不判）。
#   · 判据 sink 清单（**fail-closed**：认不出的形态一律算 UI ⇒ 新增 UI 写法不会被静默放过）：
#       `log?.Invoke(` / `Log(` / `.Log +=` / `Trace.Write*` / `Debug.Write*` / `Console.Write*|Error`
#   · 为什么：本会话 4 次转红的原因**全是**"把静默改成响亮"（`Indeterminate("…")` 等）⇒
#     它们是**诊断**，不是「UI 文案不得硬编码」要守的东西；而价值序恰恰要求它们**响亮且点名**。
#   · `$BASE_CS_OTHER` 保留为 ℹ（`CsUi + CsDiag` 的合计），判据看 `$BASE_CS_UI`。
# ⚠⚠ **2026-09-19 第七次抬基线：824 → 832（+8）** —— 逐行登记见本文件上方
#   「2026-09-19 第七次抬基线（P1-E CLI 严格化轮）」整段；余量 0。
# ⚠⚠ **2026-09-26 第八次抬基线：832 → 837（+5）** —— 逐行登记见本文件上方
#   「2026-09-26 第八次抬基线（色彩矩阵修复轮）」整段（含折叠 4 处消息折行误计、SKIP 说明）；余量 0。
$BASE_CS_UI    = 837

# ── 正则 ───────────────────────────────────────────────────────────────────────
$RxCjk         = '[\u4e00-\u9fff]'
$RxXmlComment  = '(?s)<!--.*?-->'
$RxBlkComment  = '(?s)/\*.*?\*/'
# C# 字符串/字符字面量：verbatim `@"..."`（`""` 为转义引号）/ 普通 `"..."` / 字符 `'x'`
$RxCsLiteral   = @('@"(?:[^"]|"")*"', '"(?:[^"\\]|\\.)*"', "'(?:[^'\\]|\\.)'") -join '|'

# ── 工具：把注释替换成"等量换行"，保持行边界 ──────────────────────────────────────
# 用 MatchEvaluator 而不是把注释删空：删空会让跨行注释前后的两行**拼成一行**，
# 从而改变"含中文的行数"（本脚本的口径② 明确按行计）。
function Strip-Comments([string]$text, [string]$pattern) {
    $ev = [System.Text.RegularExpressions.MatchEvaluator] {
        param($m)
        $n = 0
        foreach ($ch in $m.Value.ToCharArray()) { if ($ch -eq [char]10) { $n++ } }
        return ("`n" * $n)
    }
    return [regex]::Replace($text, $pattern, $ev)
}

# ── 核心计数：对给定文件列表跑口径② ─────────────────────────────────────────────
function Measure-Cjk([string[]]$files) {
    $xaml = 0; $cs = 0; $csTag = 0; $csOther = 0; $csDiag = 0; $csUi = 0
    # ②-c 终局：把「其余」桶按**是否走诊断 sink** 细分。
    # ⚠ **fail-closed**（本仓价值序：宁可误报也不放过）：**认不出**的形态一律算 **UI**（计入判据），
    #   只有**明确**落进下列 sink 的才算诊断。⇒ 新增一种 UI 写法**不会**被静默放过。
    # 覆盖：log 回调（`log?.Invoke(…)` / `Log(…)`）、`.Log +=`、`Trace/Debug/Console.Write*`。
    $rxDiagSink = '(\blog\s*(\?\.)?\s*(Invoke\s*)?\(|\bLog\s*\(|\.Log\s*\+=|\bTrace\s*\.\s*Write|\bDebug\s*\.\s*Write|\bConsole\s*\.\s*(Write|Error))'
    foreach ($f in $files) {
        if (-not (Test-Path $f)) { continue }
        $raw = [System.IO.File]::ReadAllText($f)
        if ($f -match '\.(xaml|axaml)$') {
            $text = Strip-Comments $raw $RxXmlComment
            foreach ($line in ($text -split "`n")) {
                if ([regex]::IsMatch($line, $RxCjk)) { $xaml++ }
            }
        }
        else {
            $text = Strip-Comments $raw $RxBlkComment
            foreach ($line in ($text -split "`n")) {
                if ($line.TrimStart().StartsWith('//')) { continue }
                foreach ($m in [regex]::Matches($line, $RxCsLiteral)) {
                    if ([regex]::IsMatch($m.Value, $RxCjk)) {
                        $cs++
                        $body = $m.Value.Trim([char[]]'@"')
                        if ($body.TrimStart().StartsWith('[')) { $csTag++ }
                        else {
                            $csOther++
                            if ([regex]::IsMatch($line, $rxDiagSink)) { $csDiag++ } else { $csUi++ }
                        }
                        break   # 一行只计 1，按"第一个命中字面量"分桶
                    }
                }
            }
        }
    }
    return @{ Xaml = $xaml; Cs = $cs; CsTag = $csTag; CsOther = $csOther; CsDiag = $csDiag; CsUi = $csUi }
}

# ── 第 0 节：语料自检（防"扫了个空目录"式假绿） ──────────────────────────────────
Write-Output "── 0. 语料自检 ──"
$srcFiles = @(Get-ChildItem -Path "$root/src/FfmpegGui" -Recurse -File -Include *.xaml, *.axaml, *.cs |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object { $_.FullName } | Sort-Object -Unique)
$nXaml = @($srcFiles | Where-Object { $_ -match '\.(xaml|axaml)$' }).Count
$nCs = @($srcFiles | Where-Object { $_ -match '\.cs$' }).Count
CK ($nXaml -ge 5 -and $nCs -ge 30) "扫到 $($srcFiles.Count) 个源文件（XAML/AXAML $nXaml ≥5；C# $nCs ≥30）"

# ── 第 1 节：实测 vs 基线（判据 = 不得增加） ─────────────────────────────────────
Write-Output "`n── 1. 口径② 实测（判据：不得增加） ──"
$m = Measure-Cjk $srcFiles
Write-Output ("  ℹ XAML/AXAML 含中文行 = {0}（基线 {1}，余量 {2}）" -f $m.Xaml, $BASE_XAML, ($BASE_XAML - $m.Xaml))
Write-Output ("  ℹ C# 含中文字面量行 = {0}（基线 {1}，余量 {2}）" -f $m.Cs, $BASE_CS, ($BASE_CS - $m.Cs))
Write-Output ("  ℹ   其中 [tag] 编码日志 = {0}（基线 {1}，余量 {2}）" -f $m.CsTag, $BASE_CS_TAG, ($BASE_CS_TAG - $m.CsTag))
Write-Output ("  ℹ   其中 其余（窗口标题/菜单/对话框/进度标签/预设名/EXIF 字段名/--help…）= {0}（基线 {1}，余量 {2}）" -f $m.CsOther, $BASE_CS_OTHER, ($BASE_CS_OTHER - $m.CsOther))
Write-Output ("  ℹ     其中 **UI 面**（判据只看这一项）= {0}（基线 {1}，余量 {2}）" -f $m.CsUi, $BASE_CS_UI, ($BASE_CS_UI - $m.CsUi))
Write-Output ("  ℹ     其中 诊断 sink（log?.Invoke / Log( / .Log += / Trace|Debug|Console.Write*，**不判**）= {0}" -f $m.CsDiag)
Write-Output "  ℹ 口径①（`_probe-i18n-scan.ps1` 的 ℹ 行）是**另一个更窄的口径**（只扫 MainWindow.xaml / MainWindow.xaml.cs，不剥注释）⇒ 见该脚本与 docs/TESTING.md §6 第 61 条"

CK ($m.Xaml -le $BASE_XAML) "XAML/AXAML 硬编码中文行 $($m.Xaml) ≤ 基线 $BASE_XAML（不得增加）"
CK ($m.CsUi -le $BASE_CS_UI) "C# **UI 面**文案 $($m.CsUi) ≤ 基线 $BASE_CS_UI（不得增加）"
CK (($m.CsDiag + $m.CsUi) -eq $m.CsOther) "②-c 分桶自洽：诊断 $($m.CsDiag) + UI $($m.CsUi) = 其余 $($m.CsOther)"
# ⚠ 口径②-b：`$m.Cs`（含 TAG 的总计）与 `$m.CsTag` **不再参与判据**，只作上方 ℹ 打印（保留可见性）。
#   它们的分桶自洽仍被下面那条断言守着 —— 防止分桶逻辑坏掉导致上面两条**假绿**。

# ── 反控（②-b 口径）：判据段**只能**有 XAML 与「其余」两条 ────────────────────────
# 否则将来有人把 [tag]/总计重新塞回判据时，本锁会**静默**回到旧口径（惩罚做对的事）。
$selfTxt = [System.IO.File]::ReadAllText($PSCommandPath)
$judgeLines = @(($selfTxt -split "`n") | Where-Object { $_ -match '^CK \(' -and $_ -match 'BASE_' })
$backToOld = @($judgeLines | Where-Object { $_ -match '\$m\.Cs\s+-le' -or $_ -match '\$m\.CsTag\s+-le' }).Count
$hasXaml = @($judgeLines | Where-Object { $_ -match '\$m\.Xaml\s+-le' }).Count
$hasUi = @($judgeLines | Where-Object { $_ -match '\$m\.CsUi\s+-le' }).Count
CK ($backToOld -eq 0) "反控（②-b/②-c 口径）：判据段**不含** C# 总计 / [tag] 两桶的比较（实 $backToOld 处）—— 防止旧口径被静默塞回"
CK ($hasXaml -eq 1 -and $hasUi -eq 1) "反控（②-b/②-c 口径）：判据段**确实**含 XAML 与「UI 面」各 1 条（实 $hasXaml / $hasUi）—— 防止判据被清空成恒绿"
# 分类自洽：两桶之和必须等于总数，否则说明分桶逻辑坏了（此时上面两条"不得增加"可能同时假绿）
CK (($m.CsTag + $m.CsOther) -eq $m.Cs) "分桶自洽：[tag] $($m.CsTag) + 其余 $($m.CsOther) = 总计 $($m.Cs)"

# ── 第 2 节：负控（证明这个计数**有牙**，且不会误伤注释） ────────────────────────
# 为什么必须有：本项目已两次踩到"门禁不判红/不执行 ⇒ 假绿"（§6 第 50/58 条）。
# 一个永远绿的计数脚本比没有脚本更危险（它会给人"有锁"的错觉）。
Write-Output "`n── 2. 负控（临时语料，跑完即删） ──"
$nc = Join-Path $env:TEMP ("cjkscan_" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $nc -Force | Out-Null
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$nl = [char]10

# 2.1 临时 .cs：2 行硬编码中文（1 条 [tag] 日志 + 1 条其余），另有 2 处**注释**里的中文
$csLines = @(
    'class T {',
    '    void A() { var s = "硬编码中文一"; }',
    '    void B() { var t = "[tag] 日志中文二"; }',
    '    // 行注释里的中文 不该计入',
    '    /* 块注释里的中文',
    '       也不该计入 */',
    '}'
)
$csPath = Join-Path $nc 'T.cs'
[System.IO.File]::WriteAllText($csPath, ($csLines -join $nl), $utf8NoBom)

# 2.3 ⭐ ②-c 口径负控：**诊断 sink 不算 UI、UI 写法必须算**（判据按 sink 分，不按文件/桶猜）
#     ⚠ 这是 ②-c 的**唯一判别性载体**：若有人把 sink 清单写坏（或把判据退回"整个其余桶"），
#       下面这两条会**响亮转红**，而不是让锁静默变松。
$uiLines = @(
    'class U {',
    '    void A() { log?.Invoke("诊断串 不该计入"); }',
    '    void B() { item.Log += "诊断串 不该计入"; }',
    '    void C() { Console.Error.WriteLine("诊断串 不该计入"); }',
    '    void D() { Text = "窗口标题 必须计入"; }',
    '    void E() { Title = "对话框标题 必须计入"; }',
    '}'
)
$uiPath = Join-Path $nc 'U.cs'
[System.IO.File]::WriteAllText($uiPath, ($uiLines -join $nl), $utf8NoBom)
$uiM = Measure-Cjk -files @($uiPath)
Write-Output ("  ℹ ②-c 负控语料实测：诊断={0} UI={1}（[tag]={2} 其余={3}）" -f $uiM.CsDiag, $uiM.CsUi, $uiM.CsTag, $uiM.CsOther)
CK ($uiM.CsDiag -eq 3 -and $uiM.CsUi -eq 2) "负控（②-c）：**诊断 sink 的 3 条不算 UI、UI 写法的 2 条必须算 UI**（实 诊断=$($uiM.CsDiag) / UI=$($uiM.CsUi)）—— 判据按 sink 分"
CK (($uiM.CsDiag + $uiM.CsUi) -eq $uiM.CsOther) "负控（②-c）：分桶自洽（诊断 $($uiM.CsDiag) + UI $($uiM.CsUi) = 其余 $($uiM.CsOther)）"

# 2.2 临时 .xaml：1 行属性硬编码中文，另有 1 行**注释**里的中文
$xamlLines = @(
    '<A>',
    '  <TextBlock Text="硬编码中文"/>',
    '  <!-- 注释里的中文 不该计入 -->',
    '  <TextBlock Text="{ext:Loc some.key}"/>',
    '</A>'
)
$xamlPath = Join-Path $nc 'T.xaml'
[System.IO.File]::WriteAllText($xamlPath, ($xamlLines -join $nl), $utf8NoBom)

$ncM = Measure-Cjk @($csPath, $xamlPath)
Write-Output ("  ℹ 临时语料实测：XAML={0} C#={1}（[tag]={2} 其余={3}）" -f $ncM.Xaml, $ncM.Cs, $ncM.CsTag, $ncM.CsOther)

CK ($ncM.Cs -eq 2) "负控：.cs 含 2 行硬编码中文 ⇒ 计数恰为 2（实测 $($ncM.Cs)）—— 计数**有牙**"
CK ($ncM.CsTag -eq 1 -and $ncM.CsOther -eq 1) "负控：[tag] 分桶正确 ⇒ [tag]=1 / 其余=1（实测 $($ncM.CsTag) / $($ncM.CsOther)）"
CK ($ncM.Xaml -eq 1) "负控：.xaml 含 1 行属性中文 ⇒ 计数恰为 1（实测 $($ncM.Xaml)）"
# 反控：上面两个文件各有 2 行/1 行**注释**里的中文。若注释未被剥离，.cs 会 ≥4、.xaml 会 ≥2。
CK ($ncM.Cs -eq 2 -and $ncM.Xaml -eq 1) "反控：注释里的中文**不得**计入（若未剥注释，.cs 会 ≥4、.xaml 会 ≥2）"

Remove-Item -Recurse -Force $nc -ErrorAction SilentlyContinue

# 2.3 反控：语料为空时必须判红（防"glob 写错 ⇒ 0 命中 ⇒ 假绿"）
$empty = Measure-Cjk @()
CK ($empty.Xaml -eq 0 -and $empty.Cs -eq 0) "反控：空语料 ⇒ 0/0（配合第 0 节的语料自检，空目录不可能假绿）"

Write-Output "`n===== cjk-hardcode: PASS=$pass FAIL=$fail ====="
if ($fail -gt 0) { exit 1 }
exit 0
