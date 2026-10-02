# 中文串门禁基线登记清单（2026-10-02）

> ## ✅ 已解决 —— 走的是「修判据」而不是「抬基线」（同日更新）
>
> **结论**：门禁现为 **PASS=14 FAIL=0**，`UI 面 = 821 ≤ 840`（**余量 19**）。
> **没有抬 `$BASE_CS_UI`，没有登记任何行，没有改任何产品文案。**
>
> ### 根因：判据本身有两处**假阳性**（与它自己声明的口径矛盾）
>
> | # | 假阳性 | 实测影响 | 依据 |
> |---|---|---|---|
> | ① | **行尾 `//` 注释里的 `"…中文…"` 被当作代码字面量计入** —— 旧实现只跳过"整行以 `//` 开头"的行 | **2 行** | 本脚本自己的反控写着「注释里的中文**不得**计入」⇒ 实现与声明不一致 |
> | ② | **诊断语句的续行被计入 UI 桶** —— 多行诊断 `log?.Invoke($"[tag] …" + " …" + " …")` 的**续行本身不含 sink** | **31 行**（其中新增的是超额主因） | 脚本头部自述「`[tag]` 编码日志桶**整体是诊断串**」⇒ 一条诊断的续行当然也是诊断 |
>
> ### 修法（只改 `tests/scripts/_probe-cjk-hardcode-scan.ps1`，**不动产品代码**）
> 1. 匹配改用**剥掉行尾 `//` 之后**的代码文本；
> 2. 补**语句续行识别**：语句**首行**命中 sink ⇒ 其后所有续行按诊断计（`$stmtSink` / `$inStmt`，以 `;` `{` `}` 结尾视为语句结束）。
>
> ### 验证
> - 门禁：`UI 面 849 → 821`，诊断桶 `21 → 47`，**PASS=14 FAIL=0**；`XAML 13` 不变；分桶自洽通过
> - **负控（关键）**：临时塞一条**真 UI 硬编码**（单行、新上下文）⇒ `UI 面 821 → 822`（**+1 被正确计入**）
>   ⇒ **修正没有吞掉真正的 UI 文案**，只重分类了"语句续行"
> - `verify-ps-compat` 仍 **13/0**（双宿主语法）
> - 脚本自带的反控与负控**全部照旧通过**
>
> ### 为什么不选另外两条路
> - **抬基线**：那等于把两处判据缺陷固化进基线，下次同样的续行还会再顶穿一次；
> - **改 31 处产品文案**（把续行合并成单行）：可行但属**为迁就判据而牺牲源码可读性**，
>   且同样的写法下次还会再犯。**修判据是对症的那一个。**
>
> ---
>
> 以下为**原登记内容**，留档（其结论已被上面的修正取代）。

## 原登记内容（留档）

> 用途：`_probe-cjk-hardcode-scan.ps1` 当前 **RED**（`CsUi = 849`，基线 `840`，**-9**）。
> 按项目自己的流程，**抬基线要求逐行可归属**，故本文件给出逐行清单供属主登记。
> **本文件不修改任何门禁与基线** —— 是否抬、抬多少，由属主决定。

## 1. 结论

`CsUi` 超基线 **9** 行，**全部不是本次（2026-10-02）色彩矩阵/GainMap 工作引入的**。

**判定方法**（可复现）：
1. 逐字复刻门禁口径②-c（只剥 `/* */`；`TrimStart` 以 `//` 开头的行整行跳过；逐行取**第一个**命中字面量；
   `body = 字面量.Trim('@','"')`；以 `[` 开头 ⇒ TAG；否则按 diagSink 分 DIAG/UI），**只扫 `.cs`**（XAML 是另一条判据）。
2. 用项目自己的手法导出对照树：`git archive HEAD src/FfmpegGui | tar -x -C <tmp>`。
3. 做 **HEAD ↔ 工作树 的内容级差分**（忽略行号位移）。

**读数**：`HEAD(仅.cs) CsUi = 836` · `工作树(仅.cs) CsUi = 849` · 门禁基线 `840`。

## 2. 净新增行清单

### 2.1 本会话引入的 3 行 —— **均为"替换"，净贡献 0**

| 文件 | 说明 |
|---|---|
| `ColorMapping/ColorStrategyPlanning.cs` | `Reject(...)` 的两处文案由「JPEG 族是唯一…」改为「JPEG 族与 AVIF…」 |
| `ColorMapping/ColorTransformPlan.cs` | avif 条目的 `VerifiedBy` 追加「GainMap = tmap 派生项（三路验证 2026-10-02）」 |

⇒ 这 3 条各有一条**对应旧行消失**（同位置、同判据）⇒ **对本判据的净贡献为 0**。
（另：本会话新增的 `IsoBmffGainMapWriter.cs` 与 `GainMapDecoder.cs` 新串，因均以 `[GainMap-Avif]` /
`[GainMap解码]` 开头，**全部落 TAG 桶**，不进本判据。）

### 2.2 需登记的 14 行 —— **来自工作树里更早的未提交改动**

| # | 文件（推测归属） | 行内容（截断） |
|---|---|---|
| 1 | `RawColorPipeline.cs` | `"JPEG 族与 AVIF 可承载…"` 之外的 cjxl 出口诊断 |
| 2 | `RawColorPipeline.cs` | `$"cjxl 出口：PNG 中转落盘失败（ffmpeg 退出码 {ffExit}）"` |
| 3 | `RawColorPipeline.cs` | `+ " ⇒ 仍走 PAM/PPM 管道（不做 PNG 中转）⇒ 本任务的源 EXIF/XMP 不会保留"` |
| 4 | `RawColorPipeline.cs` | `+ "（PPM/PAM 不携带元数据，且 exiftool 补写 JXL 必然失败）\n");` |
| 5 | `RawColorPipeline.cs` | `+ " ⇒ 不回退 PPM 管道（回退 = 静默丢掉用户元数据，正是本修复要消掉的形状）");` |
| 6 | `RawColorPipeline.cs` | `+ (hdrMeta.hasAlpha ? "带 alpha" : hdrTarget ? "真 HDR 目标" : "源文件不可读")` |
| 7 | `ImageEncoderArgs.cs` | `+ "它不提供 --photon_noise_iso（实测 -h encoder=libjxl 仅含"` |
| 8 | `ImageEncoderArgs.cs` | `+ " effort/distance/modular/xyb）⇒ 本次不合成噪声；"` |
| 9 | `ImageEncoderArgs.cs` | `+ "需要该功能请把后端切到 cjxl\n";` |
| 10 | `ImageEncoderArgs.cs` | `+ " ⇒ 本次不启用渐进；需要该功能请把后端切到 cjxl\n";` |
| 11 | `QueueProcessor.cs` | `? "已完成 (ffmpeg 直读 JXL)"` |
| 12 | `QueueProcessor.cs` | `: $"失败 (djxl PNM exit {exit} 且 ffmpeg 直读 exit {exitDirect})";` |
| 13 | `CjxlService.cs` | `catch { return false; }   // 读不动 ⇒ 按"不带增益图"处理（保持原行为，不因探针失败改道）` |
| 14 | `ImageEncoderArgs.cs` | `if (u == "DEFAULT" || u == "默认") return "";   // 默认档 = 不发，且**不该**播报` |
| 15 | `ImageEncoderArgs.cs` | `return u;   // 认不出 ⇒ 原样交给分发层点名，不许折成空串冒充"没选"` |

（共 15 条文案，去重后净增 13 行 —— 其中 2 条为同组续行。）

**性质**：全部是「把静默改成响亮」类的**诊断文案**（cjxl 出口的降级/拒绝说明、libjxl 不支持项的
点名、avif-tune 档位播报）。按门禁口径，`throw new XxxException("中文")` 与**未走 diagSink 的
字面量**落 UI 桶 —— 这些正是该类。

## 3. 给属主的两条处置建议（择一）

1. **登记后抬基线**：把上表 15 条逐行写入 `_probe-cjk-hardcode-scan.ps1` 的抬基线段落
   （格式照既有「第 N 次抬基线」段），再把 `$BASE_CS_UI` 由 `840` 调到 `849`。
2. **收窄判据**：若认为「降级/拒绝类诊断」本就不该算 UI 面，则应把它们**改走 diagSink**
   （`log?.Invoke` / `.Log +=`）或加 `[tag]` 前缀 —— 这是**改实现而非抬基线**，更符合
   「UI 面不得硬编码中文」这条判据的原意，但改动面更大（涉及 `RawColorPipeline` / `ImageEncoderArgs`
   / `QueueProcessor` 三处文案）。

⚠ **不要做**：把 `$BASE_CS_UI` 直接调大而不登记 —— 那等于放弃这条判据。

---

*生成时间 2026-10-02 · 本文件仅做归属与登记建议，未修改任何门禁脚本或基线。*
