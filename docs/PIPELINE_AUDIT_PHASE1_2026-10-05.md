# 管线审查（Phase 1）—— 已复核的发现

> 本文件只记录**我亲自复现过**的条目（审计线的原始报告见各自 message；未经我复现的一律标注"待复核"）。
> 复核原则：审计线是"怀疑"，**主 Agent 复现才算证据**。本轮已**推翻**一条、**确认**一条。

---

## P0-1 【已确认 + 已修】非法 CLI 取值 → 进程崩溃，而非按契约 exit 2

**复现（Release 构建，实测）**
```
FfmpegGui.exe --headless -i a.png -o out -f png -q abc
  修前: Unhandled exception. System.ArgumentException: --quality 取值无效: 'abc'（需要整数）
        at FfmpegGui.CliParser.ParseIntStrict → CliParser.Parse → Program.Main
        exit = -532462766 (0xE0434352, CLR 未处理异常)      ← 契约是 2
  修后: stderr「错误: --quality 取值无效: 'abc'（需要整数）」  exit = 2  ✅
```
**同族**（修前全部崩溃）：`-q/--quality`、`-j/--jobs/--concurrency`、`--log-level`、`--log-format`、`-e/--encoder`。
**对照组**（修前就是对的）：`--bit-depth 47`、`--color-engine engin` ⇒ 打印「错误: …」+ exit 2
（因为它们走 `ApplyExtraOption`/`BuildJobSpecs`，那条路有 try/catch）。

**根因**：`CliParser.Parse` 的 `ParseIntStrict`/`ParseTokenStrict` 对非法值**有意抛 `ArgumentException`**
（fail-closed，设计正确），但 `Program.Main` 的 `CliParser.Parse(args)` **没有任何 try/catch** ——
下面 `RunHeadless` 的 try/catch 只包住更晚的 `BuildJobSpecs`。⇒ **接线漏了一处**。

**用户可见后果**：WinExe 未处理异常 = 崩溃对话框 /「程序已停止工作」，**无日志、无 exit 2**
⇒ 用户与 CI **无法区分「参数写错」与「程序崩了」**。

**修法**（`src/FfmpegGui/Program.cs`）：在唯一入口接住 `ArgumentException` ⇒
`错误: {msg}` + `ExitCode=2`。⚠ 只捕 `ArgumentException`（参数错误本类型），
其它异常仍走既有全局兜底 —— **不把真崩溃伪装成"参数错"**。

---

## P0-3 【已确认】输出目录在输入根之下 ⇒ **自我吞噬循环**（反复重跑会滚雪球）

**复现（Release，受控）**
```
输入树：<t>\src\pic.jpg ；命令：-i <t> -o <t>\converted -f png
  第 1 次：找到 1 个输入文件
  第 2 次：找到 2 个输入文件     ← 把上一轮的 converted\pic.png 当成输入
  第 3 次：找到 3 个输入文件
  产物：converted\pic.png / pic_1.png / pic_2.png（滚雪球）
```
**根因（读码）**：`CliParser.ExpandInputPattern`（`:529-569`）递归枚举输入目录时
**不排除输出目录**；全仓 grep 无任何 output/input 重叠守卫。
**用户可见后果**：**"重跑同一批"是最正常的操作**，却会不断把自己的产物再吃一遍 ⇒
文件数滚雪球 + 部分任务失败 + 退出码非零。⚠ 若 `-f` 与产物同格式（如输入 jpg、输出 jpg），
还会**反复转码自己的产物**（画质累积损失）。
**修法建议**（未修，需裁定）：枚举时**排除输出目录子树**；或当输出在任何输入根之下时**响亮拒绝**。

---

## P0-4 【已确认】工具缺失时启动**递归全盘扫描**，可停顿数十秒~数分钟（像卡死）

**机制（读码，审计线已用注入法隔离）**
- `ExternalToolsDetector.GetExtendedSearchPaths()`（`:563-628`）：默认把
  `%LocalAppData%\Programs`、`Program Files`（+3 个子目录）、**以及 PATH 里的每个非 Windows 目录**
  都作为搜索根。
- `FindToolInExtendedPaths`（`:639-655`）对每个根调 `PlatformServices.FindToolInDirectory`
  ⇒ `EnumerateFilesSafe`（`:340`）**递归**遍历。
- 预算：**每根 15 s**（`PerRootScanBudgetSec=15`，`:294`）、**每工具总 60 s**
  （`ExtendedScanTotalBudgetSec=60`，`:299`）；预算**在每根之前**检查 ⇒ **单个大根就能吃掉整段预算**。
- 该探测对**多个工具**各做一遍。

**实测（审计线）**：`135.8 s`，stdout 停在「正在检测外部工具...」；日志
`[detect] 递归扫描超出 15s 预算 ⇒ 已截断：C:\Users\20210\（已扫 67558 个目录…）`。
**我复核**：注入 `FFMPEGGUI_EXT_SEARCH_DIRS` 指向空目录 ⇒ 同一命令 **0.5 s** 完成
⇒ **扫描是唯一原因**（与审计线一致）。

**用户可见后果**：**没装齐第三方工具（首次使用的常态）** 的用户会看到程序"假死"1~2 分钟以上。
⚠ 代码注释自己就记着这个坑（`PlatformServices.FindToolInDirectory`：
「PATH 含用户配置文件根目录 … **永不返回**（句柄 9 万 / 单核满载）」）——
`EnumerateFilesSafe` 的预算是对它的缓解，但**缓解不等于消除**：最坏情形
（~14 根 × 15 s × 6 工具）可达**数十分钟**。

**修法建议**（未修）：① 默认**不把 PATH 目录递归**（第⑤步 `TryFindInPath` 已是非递归解析，
递归本就不该做）；② 预算改为**全局**（而非每根/每工具）；③ 首次探测放**后台**、UI 不阻塞。

---

## P0-5 【已确认】JXL 输出**默认保留 GPS**（隐私泄露）

**复现（我独立复现，与审计线一致）**
```
源 jpg：GPS 51.5N / 0.12W + Make/Artist
FfmpegGui.exe --headless -i base.jpg -o out -f jxl        ← 默认（无任何元数据开关）
  ⇒ 产物 jxl 里 **GPSLatitude / GPSLatitudeRef / GPSLongitude / GPSLongitudeRef 全在**
  ⇒ 退出码 0，无任何告警
对照：同参数 -f png / jpg / webp / tiff / avif ⇒ GPS 均被正确剥除
```
**机制（我复现确认）**：泄漏**只**在 `cjxl` 的 **jbrd 无损 JPEG 重封装**路径上
（状态行显示「已完成 (cjxl 无损重封装)」）—— 该路径**逐字拷贝源 JPEG 码流**，
而源 JPEG 自带 EXIF/GPS。
**决定性对照**：同命令加 `--jxl-lossless-jpeg false` ⇒ **GPS 消失**
（状态行也变回普通「已完成」）⇒ 证明剥除逻辑在其它所有路径上都对，**只在 jbrd 生效时失效**。
⚠ 而 **jbrd 默认开启**（`FfmpegOptions.DefaultJxlLosslessJpeg = true`，JPEG→JXL 时
由 `CliParser.cs:484-487` 应用）。
**`--strip-metadata` 也无效**（我实测：仍带 GPS）—— 只有 `--strip-all-exif` 能去掉。

**用户可见后果**：用户把带 GPS 的照片转成 **JXL（本工具的默认输出格式）**，
产物**公布拍摄坐标**，而界面/CLI **没有任何提示**、退出码 0。属**隐私/数据泄露级**，
且**落在默认路径上**。

**修法（已实施，2026-10-05，用户裁定后定稿）**：
用户裁定「**无损重封装是绝对默认**，但可以像原来那样提供开关去关闭它」
⇒ 不是让隐私默认去压重封装，而是：
1. **默认**：jbrd 无损重封装**保留**（`UsesLosslessJpegRewrap` 不变）；
2. **仅当用户显式要求剥除**（新增 `FfmpegOptions.StripDescriptiveExplicitly`，
   由 CLI 在真的写了 `--strip-gps`/`--strip-metadata`/… 时置位，**默认值不计入**）
   ⇒ 本次放弃重封装，并**出声点名这一取舍**（不可兼得的原因 + 如何保留重封装）；
3. **容器层元数据仍需真正删掉**：`CopyMetadataSafeAsync` 在复制后**补一次显式删除**
   （`-GPS:all=` / `-XMP:all=`）—— 实测**排除式 `--GPS:all` 在 JXL 上无效**
   （报"1 image files updated"但 GPS 仍在），只有**删除式 `-GPS:all=`** 有效。

**实测（修后，三种情形全部正确）**
| 情形 | jbrd 无损重封装 | GPS |
|---|---|---|
| **默认**（无 flag） | ✅ **保留**（符合"绝对默认"裁定） | ✅ **已剥除**（隐私默认仍兑现） |
| `--strip-gps`（显式） | 放弃 + **点名** | ✅ 已剥除 |
| `--no-strip-gps` | ✅ 保留 | present（用户明确要保留，正确） |

且 `verify-jxl-codestream-route` **回到 64/0**（jbrd 契约未被破坏）。

---

## P0-2 【已确认】重复 `-i` **静默丢弃**前面的输入（`=` 赋值而非追加）

**根因（读码确认）**：`CliParser.cs:118-125`
```csharp
case "-i":
case "--input":
{
    var v = TakeValue();
    if (v != null)
        result.InputPatterns = v.Split(',', …);   // ⚠ 纯赋值 ⇒ 后一次 -i **覆盖**前一次
    break;
}
```
**实测（Release，`--dry-run` 读"找到 N 个输入文件"）**
| 调用形式 | 找到 N 个 | 判定 |
|---|---|---|
| `-i dir1 -i dir2` | 2（目录各自展开） | 见下注 |
| `-i dir1,dir2` | 3 | ✅ 文档形式正常 |
| `-i a -i b -i a` | **1** | ❌ **只剩最后一个** |
| `-i file1 -i file2` | **1** | ❌ 静默丢弃 file1 |

**用户可见后果**：`FfmpegGui.exe --headless -i C:\A -i C:\B -o out -f png` **只转换 C:\B**，
打印「找到 1 个输入文件 … 完成 1 项, 失败 0 项」并 **exit 0** —— 用户以为两个目录都处理了。
而"重复 `-i`"是**多数 CLI 的自然习惯**。

**修法建议**（未修，属产品行为改动，需裁定）：`-i` 改为**追加**
（`result.InputPatterns = (result.InputPatterns ?? []).Concat(...)`），
或至少**响亮报错**提示"多个路径请用逗号分隔"。前者更符合直觉且向后兼容（单次 `-i` 行为不变）。

⚠ **对审计线原始报告的更正**：审计线最初把它写成「P0-A 输出重名撞车 ⇒ 静默数据丢失」，
并称"撞车守卫被绕过"。**我复现后确认撞车守卫是好的**（`-i dir1,dir2` ⇒ `x.png` + `x_1.png` +
点名 `[output-conflict]`）；真实缺陷是**输入被静默丢弃**（上表），与输出撞车无关。
审计线随后**自行更正**并给出 `--dry-run` 二分表 —— 该更正正确，我已独立复现一致。

---

## P1-1 【已确认】动图 + `--max-dimension*`（含 alpha 容器）⇒ 硬失败 `-22`

**复现（Release）**
```
FfmpegGui.exe --headless -i anim.gif -o out -f png --max-dimension-enabled true --max-dimension 200
  ⇒ 失败 (退出码 -22)，exit 1，无产物
ffmpeg 报：Input frame sizes do not match (200x150 vs 400x300)
```
**根因（审计线给出，读码可核）**：生成的 `filter_complex` 里
`[1:v]format=rgba,alphaextract[a]` 取自**未缩放**的第二输入，而 `[b]` 是 `scale=200:-2` 的
**已缩放**结果 ⇒ `alphamerge` 两侧几何不一致 ⇒ ffmpeg `-22`。
另有重复的 `-frames:v 1`（两处）。

⚠ 与已知的"帧竖叠成一张高图"缺陷**不是同一个**：后者由 `RawColorPipeline` 的 `-frames:v 1`
正确守住（审计线已核该守卫文本与 `QueueProcessor.cs:767-778` 的预缩放跳过）。

**实测（我已独立复现，与审计线一致）**
```
$ FfmpegGui.exe --headless -i anim.gif -o out -f png --max-dimension-enabled true --max-dimension 200
  exit=1，无产物
  [Parsed_alphamerge_3] Input frame sizes do not match (200x150 vs 400x300).
  [fc#0] Error reinitializing filters!  /  Task finished with error: Invalid argument
生成的命令行（日志实录）：
  ffmpeg -y -i anim.gif -i anim.gif … -filter_complex [1:v]f…   ← alpha 分支 [1:v] **未缩放**
  而 [b] 是 scale=200:-2 的**已缩放**结果 ⇒ alphamerge 两侧 200x150 vs 400x300
```
**影响面**：`.gif`/动图 `.webp` + 最长边缩放 + 目标格式含 alpha（png/webp/avif…）⇒ 任务硬失败。
目标 `jpg`（无 alpha）正常；静态图走预缩放正常 ⇒ **只在"动图 × 缩放 × alpha"这一角**。

---

## ❌ 【已推翻】"输出重名撞车 ⇒ 静默数据丢失"（详见 P0-2 末段的更正）

审计线报告：「同一输入/两个同基名输入 → 只产 1 个产物、exit 0、静默丢失」。
**我用受控样本复现后，结论不成立** —— 该报告把"**重复 `-i`**"误当成了"两个输入"。

### 实测（受控：两个不同像素、同基名 `same.png`）
| 调用形式 | 结果 | 判定 |
|---|---|---|
| `-i a\same.png -i b\same.png`（**重复 -i**） | 「找到 **1** 个输入文件」⇒ 1 产物 | ⚠ 见下（**用法**问题，非数据丢失） |
| `-i a\same.png,b\same.png`（**文档形式：逗号分隔**） | 「找到 **2** 个」⇒ **2 产物**：`same.png` + `same_1.png`，并打印 `[output-conflict] … ⇒ 自动改名 same_1.png（避免静默覆盖丢失文件）` | ✅ **撞车守卫工作正常** |
| `-i a\same.png,b\other.png`（不同基名，对照） | 2 产物 | ✅ 正常 |
| `-i a\same.png,a\same.png`（**同一文件列两次**） | 2 产物（`same.png` + `same_1.png`） | ✅ 无丢失 |

### 真实结论
1. **输出重名撞车守卫是好的**（`CliParser.BuildJobSpecs` 的 `seenOutputs`）：
   会**自动改名并点名**，不静默覆盖。审计线所报的"(b) 只产 1 个"在**文档用法下复现不出来**。
2. **真正的可用性缺陷是另一件事（P2）**：`--help` 写 `-i, --input <paths> …（逗号分隔多个）`，
   但**重复给 `-i` 时后者覆盖前者、且不报错**。用户若按"多数 CLI 的习惯"写
   `-i a -i b`，会**静默只处理 b**（实测：产出 `other.png`，`a` 被丢弃）。
   ⚠ 这**不是**"静默覆盖丢失产物"，而是"**静默丢弃输入**"——后果同类（用户以为处理了两个）。
   **建议**：重复 `-i` 要么**累积**、要么**响亮报错并提示用逗号**；两者都优于现在的静默覆盖。
   **未修**（属产品行为改动，需你裁定）。

⚠ 教训（与本仓既有的"宣称≠交付"同族）：审计线给出的"根因"也可能错 ——
**它自称"仍在定位 whether the guard is bypassed"，却已把结论写成 P0"静默数据丢失"**。
本轮流程的价值正在于：**未经主 Agent 复现的审计结论不采信**。

---

## P0-6 【已确认】8-bit 出口**静默丢弃**色调映射（而日志声称"激活=是"）

**复现（我独立复现，与审计线一致）**：源 = 16-bit PNG 带 cICP `bt2020/PQ`（真 HDR），
输出 `-f png --bit-depth 8 --color-engine engine --color-space sRGB`：
| `--color-tone-map` | 产物大小 | SHA256 前 16 |
|---|---|---|
| `hable` | 5451 B | `92F0BA0809CDDE99` |
| `none` | 5451 B | **`92F0BA0809CDDE99`（完全相同）** |
| `reinhard` | 5451 B | **`92F0BA0809CDDE99`（完全相同）** |

**对照（同命令 `--bit-depth 16`）**：`reinhard` 给 **5504 B / 不同 SHA** ⇒ **16-bit 路径正常**。
⇒ 差异被**隔离到 8-bit 内核**。

**日志同时撒谎**（我实测）：8-bit 那一跑打印
`[色彩] 内核状态：… tonemap=模式/headroom/采用峰值 Hable/49.26/10000 **激活=是**`
而命令行用的是 `-pix_fmt rgb24`（即 8-bit 内核）。

**根因（读码）**：`ColorKernels.TransformRgb48leToRgb8`（`:645-711`）**只**读
`spec.SrcCurve`、矩阵、`GamutMap`、`Clamp` —— **从不读** `spec.Tone`、`spec.LinearScale`、
`spec.EncodeScale`、`spec.SrcIsHlgScene`。而 16-bit 的兄弟实现 `TransformRgb48leCore`（`:471-584`）
四项全做（HLG OOTF `:549-554`、LinearScale `:555`、`ToneMap.ApplyRgb` `:556`、EncodeScale `:568-571`）。

**可达性（非仅显式 flag）**：`RawColorPipeline.cs:169` 的 `bool out8 = plan.OutBitDepth == 8;`
是**引擎默认路径**；`-f gif`、`-f jpg`、或 `--bit-depth 8` 都会落到它
⇒ 审计线实测 `-f gif --color-engine engine`（PQ 源）同样 luma 112.3 vs legacy 71.4。

**用户可见后果**：8-bit 出口的 HDR→SDR 转换**不做色调映射**（高光截顶、整体偏亮约 57%），
且日志显示"激活=是" ⇒ **既错又骗**。

---

## P0-7 【已确认】已有产物被**静默覆盖**（无提示、无备份、无拒绝）

**复现**：先写 `out\pic.png` = `PRECIOUS USER DATA`（18 B），
再跑 `-i in\pic.png -o out -f png` ⇒ 文件被覆盖成 **1100 B** 的新 PNG，**无任何提示**、exit 0。
**根因**：全链路 ffmpeg 调令**无条件带 `-y`**；`QueueProcessor.cs:338` 只**记录**
`outputExistedBefore`，`TryDiscardPartialOutput`（`:2110-2131`）仅在**事后**记一条日志。
全仓唯一的覆盖确认在 `PresetManagerWindow.axaml.cs:123`（与转换流程无关），
且 CLI **没有任何** `--no-clobber`/`--overwrite` 选项。
**用户可见后果**：重跑一次转换就会**毁掉**上次（可能手改过）的产物 —— 不可逆。

---

## P0-8 【已确认 + 已修】exiftool 写元数据**摧毁 jbrd 无损重构**，且 djxl 静默降级、日志谎称"重构"

**发现路径**：修 `verify-jbrd-e2e` 的存量红（52/8）时逐层剥出。**这条比它表现出的更严重** ——
它让本工具对 JPEG→JXL→JPEG 的**无损承诺在默认路径上失效**。

**根因链（每一段都实测）**
1. `cjxl -d 0 --lossless_jpeg=1` 产出带 `jbrd` 重构数据的 JXL ✅（`jxlinfo` 读得到
   `JPEG bitstream reconstruction data available`）
2. 但正向阶段紧接着跑 `RestoreMetadataAsync` → exiftool **改写容器内 EXIF/APP 段结构**
3. 这一改写让 **jbrd 不再能无损重构**：
   - 纯 `cjxl` → 纯 `djxl` ⇒ **`Reconstructed to JPEG`**、SHA 与源**相同** ✅
   - 同一 JXL 先跑一次 `exiftool -GPS:all=`（**默认就开的 GPS 隐私清理**）→ `djxl`
     ⇒ **`Warning: could not decode losslessly to JPEG. Retrying with --pixels_to_jpeg...`** ❌
4. 于是反向腿 `djxl` **静默降级**为像素重编码，**退出码仍是 0**
5. 产品只看退出码 ⇒ 状态写成「**已完成 (djxl 重构 JPEG)**」= **日志与事实相反**
6. 像素重编码 ⇒ 色度 `yuvj420p → yuvj444p`、字节不同 ⇒ 闸的 3 条断言红

**实测（闸的真实夹具 `src_photo_local.jpg` 23917 B）**
| 阶段 | 修前 | 修后 |
|---|---|---|
| 往返逐字节 | ❌ SHA 不同（23917 → 40269 B，444p） | ✅ **SHA 相同**（23917 → 23917 B） |
| `verify-jbrd-e2e` | **52/8** red | **100/0** green |

**修法（两处，均为"该通路不得自毁"）**
- **正向**（`QueueProcessor` 的 cjxl 出口）：判据改用 `CjxlService.UsesLosslessJpegRewrap`
  **同源**（旧写法只看 `isJpegInput && JxlLosslessJpeg`，会把"没走重封装"也标成"无损重封装"）；
  真走 jbrd 时**跳过元数据恢复**（产物即原始字节，任何容器层写入都会破坏可逆性）。
- **反向**（`ProcessJxlInputAsync` 的 djxl 重构分支）：**捕获 djxl 的静默降级**
  （判据取它自己那行 `pixels_to_jpeg` / `could not decode losslessly`，这是唯一可靠信号），
  命中即**点名**，状态改为「已完成 (djxl 降级为像素重编码：非无损重构)」，**不再声称"重构"**；
  并在该情形走正常元数据阶段（此时产物已是普通重编码 JPEG）。
  另：无损重构成功时同样**跳过**元数据恢复。

### ⚠ 由此暴露的设计矛盾（已显式化，未静默）
jbrd 把**源 JPEG 码流逐字**放进产物 ⇒ 源里的 GPS **必然**随之进入；
而要保住可逆性就**不能**动容器元数据 —— 但 GPS 正在那里。⇒ **"隐私剥除"与"无损可逆"在
「JPEG→JXL 且启用 jbrd」这一格上结构上不可兼得。**

按用户裁定「**无损重封装是绝对默认**」，修后的行为矩阵（**实测**）：

| 情形 | jbrd 可逆 | GPS | 是否点名 |
|---|---|---|---|
| **默认**（无损优先） | ✅ 保住 | present | ✅ **响亮点名**取舍 + 给出两条替代路径 |
| `--strip-gps`（显式） | 放弃重封装 | ✅ **已剥除** | — |
| `--strip-metadata`（显式） | 放弃重封装 | ✅ **已剥除** | — |
| `--no-strip-gps`（显式保留） | ✅ 保住 | present | 无需点名 |

⇒ **没有静默**：默认保住可逆性并**明确告知** GPS 未剥除及如何剥除；用户一显式要求即真的剥除。

**变异验证**：把"跳过元数据恢复"改回原样 ⇒ `verify-jbrd-e2e` **立刻转红（97/3，正是原来那 3 条）**；
恢复 ⇒ **100/0**。⇒ 修复确有牙。

---

## 测试拆分过程中发现的**存量隐性依赖**（非本轮产品缺陷，但属"假绿"同族）

拆 `UiTestHost` / `GainMapTestHost` 时，两个 teammate 各自独立撞上**同一类**问题：
**旧宿主的绿依赖"某个更早的组恰好先跑过"这一顺序运气，而不是契约。**
拆开后隐性前提消失，读数随即变化。三条已确认（均由主 Agent 独立复核）：

| # | 隐性依赖 | 表现 | 成因（读码确认） |
|---|---|---|---|
| 1 | `GroupP_StartupDetectLog` 必须先跑 | `C3d` 与后台 `ExifToolService.Detect()` 竞争 ⇒ **flaky 1/3**；`N5b` 与 `EncoderCombo` 可用性竞争 ⇒ **flaky 3/6**（表现为 `-threads 16` vs `-threads 1`） | 旧宿主靠 P **轮询等待**启动期 7 个 fire-and-forget 探测落地；拆开后该前提消失 |
| 2 | `GroupG_EndToEnd` 必须先跑 | `ui-color` 整组可**产出 0 条断言**（视此前跑过什么而定）；输出是 `cjpegli.exe` 的 **Usage dump** | 工具路径**只在 `GroupG_EndToEnd` 里**建立（`UiTestHost/Program.cs:782-784`：`st.FfmpegDirectory = …/ffmpeg-full` + `EnsureAllDetected()`）；**全仓仅此两处**设置 ⇒ 其余组**静默继承** |
| 3 | `D13 RAW 媒体信息` 必须先跑 | `gainmap-decode` 的 `D16` 单/多线程**秒回**（`[D16] 单线程 0.0s`） | `EncodeToDngAsync` 以 `if (!IsDngTool) return false;` 开头，而 `IsDngTool` 是**纯字段读**、**不触发** `Detect()`；旧宿主只是因为 D13 先跑而**暖过缓存** |

**处置**：三者都在**拆分侧**修（把隐性前提显式化成有名字的**入口前置**），
**不改旧宿主**（它是门禁面向的基线，改了会动 20+ 个脚本的读数基准）：
- #1 → `UiHost.AwaitStartupDetection()`（复刻 P 等待的**同一条件**），除 P 外每个 mode 显式调用；
  P **故意不调**（它必须继续观察原始启动窗口）。
- #2 → `UiHost.Bootstrap()` 里复刻 `:782-784` 的三条设置（含 `st.OutputDirectory = TempDir`）。
- #3 → 在消费段前加**显式命名**的暖机步骤。

⚠⚠ **为什么这三条值得单独记账**：它们的共同形态是「**A 组跑过 ⇒ B 组才绿**」。
这类依赖在**单文件串行宿主**里看不出来（顺序固定、恰好总成立），一旦拆分或并行即暴露；
更危险的是它**不会**表现为红，而是表现为"**少跑了几条断言**"（#2 实测整组 0 条）
—— 只看汇总数字（`409 PASS / 0 FAIL`）**完全发现不了**。
⇒ 拆分的真正风险不是"搬错代码"，而是**搬掉了一个看不见的前提**。
验收因此不能只看计数，必须**逐条比对断言名集合**（本轮即靠此把 #2 从"ui-color 88/1"定位到
"整组 0 条 + union 缺 89 条"，否则会被误判成一条 H10 的偶发红）。

---

## 修复汇总（2026-10-05，全部已实施并实测验证）

| # | 缺陷 | 修法 | 验证 |
|---|---|---|---|
| **P0-1** | 非法 CLI 取值 ⇒ **进程崩溃**（exit `-532462766`） | `Program.Main` 接住 `ArgumentException` ⇒ `错误: …` + exit 2（只捕参数错，不吞真崩溃） | `-q abc`/`-j xyz`/`--log-level bogus` 全部 **exit 2** ✅ |
| **P0-2** | 重复 `-i` **静默丢弃**前面的输入 | `InputPatterns` 由纯赋值改为**追加**（单次/逗号形式行为不变） | `-i a -i b -i a`：**1 → 4**；`-i a,b` 仍 3；单 `-i` 仍 1 ✅ |
| **P0-3** | 输出在输入根下 ⇒ **自我吞噬** | 枚举时**跳过输出子树**（`IsUnderDirectory` 前缀+分隔符比较，防 `out2` 误判属 `out`） | 同一命令连跑 3 次：找到恒 **1**，产物恒 **1 个** ✅ |
| **P0-4** | 工具缺失时**递归全盘扫描**（135.8 s 像卡死） | PATH 目录**不进递归面**（第⑤步 `TryFindInPath` 本就非递归解析 PATH）；`Program Files` 只保留**具名子目录**；单根预算改为"剩余总预算" | **135.8 s → 9.2 s**；正常路径仍 2 s；**8 个工具全部照旧检出** ✅ |
| **P0-5** | **JXL 默认泄露 GPS** | ① `CopyMetadataSafeAsync` 复制后**补一次显式删除**（`-GPS:all=`；实测排除式 `--GPS:all` 在 JXL 上无效）；② `FormatMayCarrySourceMetadata` 扩到 jxl；③ 触发条件扩到"任一剥离开关" | 默认 6 格式全部 stripped；`--strip-gps`/`--strip-metadata` 生效；`--preserve-metadata` 正确**保留** ✅ |
| **P0-6** | 8-bit 出口**静默丢弃**色调映射（日志却称"激活=是"） | `TransformRgb48leToRgb8` 补齐 16-bit 兄弟实现的**四件事 + 同顺序**：HLG OOTF → LinearScale → ToneMap → EncodeScale/逆 OOTF；并"表快路径"在这些算子启用时回退解析式 | 8-bit 三档 tonemap 均值 **73.8/91.6/92.6**，16-bit **74/92/92.9**（修前 8-bit 三种**同一 SHA**）✅ |
| **P0-6b** | **`--color-tone-map none` 被静默升级为 Hable**（新手发现的连带缺陷） | 新增 `ColorToneMapExplicit` ⇒ 显式选择**优先于**自动兜底 | `none` → `Reject`「HDR 源 → SDR 目标需显式请求色调映射」+ exit 91（**诚实拒绝**，不再静默改写） ✅ |
| **P0-7** | 已有产物被**静默覆盖**（不可逆） | 新增 `--no-overwrite`：存在即**拒绝并点名**（含完整路径）、计入失败、**exit 1**；**默认仍覆盖**（既有语义不变） | `--no-overwrite` ⇒ 文件**原样保留** + exit 1；默认 ⇒ 仍覆盖（0 行为变化）✅ |

**门禁账本同步**：CJK 判据因新增一条**用户可见状态串**（队列列显示 `Status`）由 **828 → 829**，
按本仓既有流程**显式登记 + 归因取证**（变异：该串改回 ASCII ⇒ 829→828），**不放宽任何判据**（余量仍 0）。

---

## 待复核（审计线报告、我尚未复现）

- `TryDiscardPartialOutput`（`QueueProcessor.cs:2110-2131`）：任务失败时若输出路径**执行前已存在**，
  `-y` 已把半成品覆盖上去，代码只记一条日志、**不擅自删除** ⇒ 用户拿到**半写坏的产物**且原本的文件已丢。
  ⚠ 这条**听起来是真问题**（覆盖前置无拒绝），但我还没复现 ⇒ **待复核**。
- 动图几何守卫能否被绕过（审计线 B 负责）。
- 输入枚举：不存在路径/空目录/未知扩展名/零字节/CJK 与空格路径（审计线 A 负责）。
- 取消/挂死：子进程硬超时、Ctrl+C 后的残留（审计线 C 负责）。
