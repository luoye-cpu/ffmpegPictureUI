# UI 高级编码器选项：正确性 / 可用性 / 缺口 分析（2026-10-07）

> **问题**（用户原话，三问）：
> ① UI 高级编码器选项**在不同编码器上是否正确**？② 这些选项**是否可用**？不可用的**进行调整**；
> ③ 有没有**可用但 UI 没有对应选项**的高级编码器选项？
>
> **方法**：静态接线审计（49 个字段 × UI × 命令行）＋ Lead 实机探针（真跑 + 字节级对照）＋
> 两名 teammate 的独立取证（`docs/ADV_ENCODER_MATRIX_2026-10-07.md`、`docs/ADV_ENCODER_GAP_PLAN_2026-10-07.md`，
> 原始日志 `tests/output/_advmatrix/` 222 个文件、`docs/_gapscout_raw/` 31 个文件）。
> **纪律**：本文只写**实测到**的；「未验证」单列；已修复的与仍开放的**分开写**。

---

## 0. 先说结论（三问直答）

| 问题 | 结论 |
|---|---|
| ① 是否**正确** | **基本正确，且质量高于预期**：AVIF 已按 **6 个后端**分域（`ClassifyAvifBackend`）、每个后端发**自己的**参数域；跨后端残留**已在 2026-09-26 重写中修掉**（§2 字节级反证）。UI 也**按后端联动显隐**高级面板（§1.1 逐条锚点）。逐格式实测：png/apng/tiff/gif/jxl/jxr/dng **全 OK**（改值真改字节）。 |
| ② 是否**可用** | **可用**：49 个高级编码字段**三面齐备、0 处「有声明无实现」**（§1）；后端不兼容时 UI 会收走面板、命令行也会按后端过滤（§1.1 / §2.2）。唯一真问题是 **14 条「选项被忽略」的告警在 GUI 里看不见**（WinExe 无控制台）⇒ 用户以为设上了。**已修**（§3，task-5 完成并复核）。 |
| ③ **缺口** | **7 类**（§4）。⚠ **Lead 复核后把 teammate 的两条 P0 都降了级**（§5.1）：NVENC `-rc` 因本机 `av1_nvenc` 不可用而**数字不可复核** ⇒ P1；avifenc `--target-size` 实测呈**阶梯且不命中目标** ⇒ P2。**当前性价比最高的一条是「JXR 面板补 `-d`/`-s`」**（顺带解已确证的 A-17）。 |

---

## 1. 静态接线审计（49 个高级编码字段 × 三面）

**判据**：字段必须同时出现在 ① `FfmpegOptions` ② UI 采集 ③ 命令行消费；任一缺失即「有声明、无实现」。

**结果：49 项中 46 项三面齐备**。剩下 3 项经核实**全是我自己的命名猜测错误**，不是缺陷：

| 我的猜测名 | 实际名 | 核实 |
|---|---|---|
| `WebpCompression` | `WebpCompressionLevel` | `FfmpegOptions.cs:292`；命令行消费在 `ImageEncoderArgs.cs:129-144` |
| `WebpLossless` | （无损走通用 `Lossless` + `WebpLosslessPanel` 可见性） | `MainWindow.xaml.cs:2431` |
| `JpegliProgressiveCombo` | 控件名，字段是 `CjpegliProgressiveId` | `CliParser.cs` |

⇒ **「有声明、无实现」在当前代码里 0 处**（历史那批已在 09-26 重写中清掉；
`FfmpegOptions.cs:365` 仍有一处**过期注释**写 `"int"/"fastint"/"float"`，仅注释，不影响行为）。

### 1.1 【正面证据】UI **确实**按「后端」联动显隐高级面板

问②「这些选项是否可用」的核心是：**切到不兼容的后端时，界面有没有把不该用的选项收走**。
Lead 读 `MainWindow.xaml.cs` 的 `UpdateCodecPanelVisibility`（`:2386` 起）确认**已系统化做到**：

| 格式 | 后端 | UI 行为（源码锚点） |
|---|---|---|
| `jxl` | `Cjxl` | `JxlCjxlPanel` 可见、`JxlFfmpegPanel` 隐藏（`:2466-2468`） |
| `jxl` | `Ffmpeg` | 反向（同上） |
| `jxl` | 动图 | **强制**只显示 FFmpeg 面板（cjxl 不支持动画），并禁用 `LosslessCheck`（`:2457-2461`、`:2470-2472`） |
| `jxl` + jbrd | 仅 `Cjxl` **且**非动图 | `JxlLosslessJpegPanel` 才可见 —— **注释里写明理由**：ffmpeg 的 libjxl 出口实测**不写** jbrd 盒（`:2477-2480`） |
| `jpg`/`jpeg` | `Cjpegli` vs `Ffmpeg` | `JpegliCodecPanel` / `JpegCodecPanel` 二选一（`:2483-2491`） |
| `avif` | 6 个后端 | `UpdateAvifEncoderPanel()`（`:2521-2558`）逐后端切 7 个子面板：`Libaom/Svt/Nvenc/Qsv/Vaapi/Amf` + 兜底 |
| `avif` | 动图 | `AvifStillPictureCheck` 强制禁用并取消勾选（`:2445-2449`） |

⚠ 并且**采集侧也一致**：高级选项一律由 `useAdvCodec`（"高级编码选项"复选框）门控
（如 `MainWindow.xaml.cs:2916-2931`），**不是**由"哪个子面板可见"门控 ——
⇒ 这是**正确的**：值的可用性由**后端参数分域**（`ImageEncoderArgs` 的 `backend == …`）裁决，
而不是由 UI 显隐裁决；两者职责分离，不会出现"面板藏了但值还在发"的错配
（§2.2 已给该分域的逐行证据）。

**结论**：问②的答案是「**可用**，且 UI 已按后端收走不该用的选项」。
唯一的可用性缺口不是"选项没用"，而是 **§3 的「告警 GUI 看不见」**。

---

## 2. 「跨后端残留」——**已在 HEAD 修复**（字节级反证）

### 2.1 现象（teammate 独立复现 + Lead 复核，**两者读数逐字一致**）

把 **libaom 私有项**喂给 `libsvtav1`：

```powershell
ffmpeg -y -i src8.png -frames:v 1 -c:v libsvtav1 -crf 16 -cpu-used 4 -still-picture 1 -f avif a.avif
ffmpeg -y -i src8.png -frames:v 1 -c:v libsvtav1 -crf 16                          -f avif b.avif
```
| 产物 | 大小 | MD5 |
|---|---|---|
| `a.avif`（带 libaom 私有项） | 4934 B | `41FF6793…` |
| `b.avif`（不带） | 4934 B | `41FF6793…` |

**逐字节相同**；`-cpu-used 0/4/8` 三档也全同（都 4934 B / `41FF6793…`）
⇒ 确认 **libaom 私有项在 SVT 上静默无效**，ffmpeg 只给一行
`Codec AVOption cpu-used … has not been used for any stream`（极易淹没在日志里）。

### 2.2 但**产品侧已不这么发**（当前代码逐行核对）

`src/FfmpegGui/Services/ColorMapping/ImageEncoderArgs.cs`：
```csharp
// L611  结构/速度轴 -cpu-used：libaom 私有项（自报域 0..8）
//       实测对 nvenc / svt 它只会得到 Codec AVOption cpu-used has not been used ⇒ 旧代码白发。
if (backend == AvifBackend.Libaom && o.AvifCpuUsed.HasValue)   // ← 已按后端门控
{ args.Add("-cpu-used"); args.Add(o.AvifCpuUsed.Value.ToString()); }
```
同族门控**全部到位**（同一函数内）：`-still-picture` / `-row-mt` 仅 `Libaom`；
SVT 只发 `-preset` + 单一 `-svtav1-params` dict；硬件四后端各有具名档位。
⇒ **2.1 描述的是 2026-09-26 重写之前的旧行为**。判据：该文件 `git diff HEAD` **为空**
（无未提交改动）⇒ 门控来自 HEAD，不是本轮新加的。

### 2.3 为什么 teammate 会得出相反结论（**方法论教训，值得记**）

- teammate 用的是**真跑 ffmpeg 手写命令**（§2.1）—— 那确实复现了旧行为，
  **但它证明的是"ffmpeg 会忽略"这一底层事实，不是"产品会这样发"**。
- 产品是否这样发，取决于 `backend == AvifBackend.Libaom` 这个**门控**，
  而 `o.Encoder`（ffmpeg 编码器名，如 `libsvtav1`）**只能由 GUI 的组合框设置**
  （`MainWindow` 的 `EncoderCombo`）—— **CLI 没有对应键**
  （`-e` 收的是 `EncoderBackend` **枚举**：`Ffmpeg/Cjxl/Cjpegli/Jxr/Dng`；
  传 `-e libsvtav1` 会被**响亮拒绝**：`错误: 未知编码器`，exit 2 —— 这个 fail-closed 行为本身是对的）。
  ⇒ **headless 手写命令走不到 SVT 分支**，teammate 实际复现的是"绕过产品直接调 ffmpeg"。
- **教训**：判「产品是否发某参数」必须走**产品的代码路径**（GUI 或等价入口），
  手写 ffmpeg 命令只能证明**编码器的**行为，不能证明**产品的**行为。
  这正是本仓 `TESTING.md` §6 第 13 条「口径纠正：脚本并不驱动产品 exe」的同一类错误。

### 2.4 既有门禁已经守住了这块

`tests/scripts/verify-encoder-knob-liveness.ps1` 实测 **rc=0**：
```
活=19  死(两端都没发)=0  被拒(已播报)=1  未判(夹具无判别力)=1  缺口(未做)=1  合计=22
```
其中 `缺口=1` 是**登记在案的覆盖缺口**（SVT 后端的 `--avif-tune` 臂），
且脚本里逐字写着：「上一臂跑的是默认后端 = libaom，SVT 后端的 tune **没有任何臂**……先在这里留名，
免得"未验证"被表上的 `-avif-tune 活` 冒充过去」⇒ **诚实记账，不是假绿**。

---

## 3. 【真问题】14 条「选项被忽略」告警在 GUI 里不可见

### 3.1 证据

- `src/FfmpegGui/FfmpegGui.csproj`：**`<OutputType>WinExe</OutputType>`**
  ⇒ GUI 模式**没有控制台**；`Program.cs` 的 GUI 路径也无 `AllocConsole`/`AttachConsole`。
- 两处服务共 **14 条** `Console.WriteLine("⚠ [xxx] …")`（`ImageEncoderArgs.cs` 10 处 + `FfmpegCommandBuilder.cs` 4 处）。

### 3.2 受影响的消息（内容本身质量很高，问题只在**通道**）

| 位置 | 消息（节选） |
|---|---|
| `ImageEncoderArgs.cs:99` | `⚠ [jpeg] --lossless true ignored: the mjpeg encoder is lossy…` |
| `:135` / `:145` | `⚠ [webp] --webp-preset ignored in lossless mode…` / `--webp-compression ignored for lossy WebP…` |
| `:486` | `[avif/av1_nvenc] --lossless true is not honoured…` |
| `:549` / `:562` / `:582` / `:591` / `:605` | AVIF tune 在各后端上的不可表达/被丢弃说明 |
| `:723` | `[avif/av1_nvenc] -aq-strength dropped because spatial AQ is off…` |
| `FfmpegCommandBuilder.cs:159` / `:163` / `:233` / `:268` | `⚠ [jpeg] --jpeg-progressive ignored…`（并**指引**改用 `-e Cjpegli`）/ webp 两条 |

**实测样例**（headless，能看到）：
```
⚠ [webp] -compression_level is pinned by -preset drawing (measured byte-identical output
   for level 0/4/6); use --webp-preset none to let -compression_level apply.
```
⇒ 同一条消息，**headless 用户看得到、GUI 用户看不到**。这是「可用性诚实」原则的一个缺口。

### 3.3 影响与处置（**已修并复核**）

- **影响**：GUI 用户设了某高级选项、以为生效，实际被忽略/降级，**界面无任何提示**。
  对 AVIF tune 尤其明显（`vmaf` 在本构建不可用、`film` 不可表达、SVT 上 `tune=IQ` 会 abort 已被主动拦下 —— 全都只打在 console）。
- **处置（task-5，已完成）**：
  - `ImageEncoderArgs` 新增 `AsyncLocal<Action<string>?> WarnSink` + `EmitWarn(msg)`：
    **先 `Console.WriteLine`（headless/门禁逐字不变）**，再转发给 sink（未注入 ⇒ 行为与改动前**逐字相同**）。
    ⚠ 选 `AsyncLocal` 而非普通 static 的理由（**Lead 已核实成立**）：队列项**多线程并发**，
    ambient 值会把 A 项的告警串进 B 项。且两个调用点都是**同步** `BuildArguments` ⇒ 语义正确。
  - 14 处调用点全部改走 `EmitWarn`（`ImageEncoderArgs` 10 + `FfmpegCommandBuilder` 4），**字符串一字未改**；
    剩余 3 处 `Console.WriteLine` 经核实是 2 处文档注释 + 1 处 `EmitWarn` 自身实现
    （⇒ 不是漏改，Lead 已逐处核对）。
  - `MainWindow.xaml` 新增 `EncoderOptionWarning` 面板（照既有 `GpuEncoderWarning` 形状）；
    `MainWindow.xaml.cs` 的 `WithEncoderOptionWarnings`（作用域装 sink + **去重** + `finally` 还原）
    接在**两个真实预览建命令点**。

**双向验证读数（Lead 独立复跑）**：

| 验证 | 读数 |
|---|---|
| headless 未变（门禁） | `verify-encoder-knob-liveness` **rc=0**，`活=19 死=0`，`PASS=19 FAIL=0` |
| CJK 桶未增加 | `_probe-cjk-hardcode-scan` **rc=0**，`UI 面 833 ≤ 833`、`XAML 13 ≤ 13`（**余量 0、未抬基线**） |
| 构建 | `FfmpegGui` **0 警告 0 错误** |
| 回归 | `format-regression 19/0`、`color-wiring 142/0`、`cli-strict 409/0`、`ui-param-defects 409/0` |
| GUI 可见（teammate 一次性宿主） | **13 PASS / 0 FAIL**（面板可见 + 文本含真实告警串 + 去重 + 切格式清空 + 默认 sink 为 null + 负控） |

⚠⚠ **两条如实标注的边界（不要当成已解决）**：
1. **转换执行期（queue-run）的告警不在该面板上** —— 只接了**预览**路径。
   原因：若接 `QueueProcessor` 写 `item.Log`，`ConsoleQueueObserver.FlushItemLog` 在 debug 级会把
   `item.Log` **回显到 stdout** ⇒ 告警行**翻倍**、破坏门禁 grep 的文本（teammate **实测到并主动回退**，
   `QueueProcessor.cs` 最终 **0 diff**）。⇒ 转换期告警仍在 headless stdout + 详情窗口 `item.Log`。
   要覆盖它需另做一条**不经 stdout 回显**的 GUI-only 通道（独立小改动，**未做**）。
2. **GUI 可见性只有一次性证据、无长期回归锁** —— teammate 本写了 13 条断言的 UI 测试，
   但 `verify-ui-split-union.ps1` 硬要求「旧宿主断言名并集 == 409 且 extra == 0」，加断言会把那条绿门禁弄红。
   ⇒ 它**撤回**了全部 harness 改动（0 diff）并改用一次性宿主取证（证据留
   `tests/output/_ws_task5_gui_probe_evidence.txt`）。**Lead 裁定本轮不接长跑清单**
   （不应为了接测试而改一条刚修好的锁）；若要长期锁，需**独立一项**同步改 union 锁的 409 与白名单。

---

## 4. 缺口盘点（③ 问）——**7 类，按优先级**

来源：teammate 用**本仓二进制**逐个问出来（`ffmpeg -h encoder=…`、`cjxl --help -v -v`、`avifenc --help`、
`JxrEncApp`），并对每个候选**实测能出产物**。「文档里有」不算数。

| 优先级 | 缺口 | 实测证据 | 价值 |
|---|---|---|---|
| **P1** | **NVENC 缺 `-rc` / `-qp`** | 方向成立：`-rc` 是 NVENC 一等公民，且本仓**已在 AMF 发 `-rc cqp`**（口径不一致）。⚠ **但本机 `av1_nvenc` 报 `No capable devices found` ⇒ teammate 的 `11877/22727/7928 B` 三档数字在本机**不可复核**（详见 §5.1）⇒ 由 P0 降级 | 补它可消除与 AMF 的口径不一致；**实现前须在有可用 av1_nvenc 的机器上取基线** |
| **P1** | **JXR 面板 0 个控件**，而 `JxrEncApp` 有 12+ 选项 | `-d 1`=7912B vs `-d 3`=**10318B**；`-s 2`=**2827B**（−64%）（teammate 实测；`-d` 的域已见 `jxrenc_q.txt`） | `-d` 正是已确证缺陷 **A-17**（q≤49 静默降 4:2:0）的正解 ⇒ **本表中性价比最高的一条** |
| **P1** | **cjxl `-e` 上限被卡在 9** | 已核 XAML：`MainWindow.xaml:656 CjxlEffortBox Maximum="9"`；`cjxl -e 10` 实测 rc=0 合法 | 走 cjxl 路线的用户**永远够不到最高压缩档**。⚠ `JxlEffortBox`(:650) 保持 9 是**对的**（ffmpeg libjxl 域到 9），**别顺手改** |
| **P2** | **avifenc `--target-size`** | ⚠ **Lead 复核后降级**：真跑呈**阶梯**（2000→3570 / 5000→3570 / 10000→7930 / 30000→7930 / 60000→7930），**不命中**目标；`--help` 自承 up to 7× slower（详见 §5.1） | 方向成立（能"按体积上限"约束），但**收益被高估**、精度差、代价高 ⇒ 由 P0 降为 P2 |
| **P2** | avifenc `--qalpha` | GIF→AVIF 主场就是保透明，却把 alpha 与色度压同一个 `-q` | 透明边缘质量 |
| **P2** | libsvtav1 `-qp`（与 `-crf` 语义不同：`-crf 40`=5688B vs `-qp 40`=9174B） | teammate 实测 | 精确码控 |
| **P2** | cjxl `--faster_decoding`（0..4 产物互异）、NVENC `-tune uhq`、avifenc `enable-chroma-deltaq`、NVENC `-multipass`、avifenc `--progressive`、cjxl `--resampling`/`--gaborish`、WebP **动图** `-cr_threshold`、libjxl `-xyb` | 各自附实测（⚠ 凡涉 `av1_nvenc` 的条目同 §5.1：本机不可复核） | 收益明确但非紧急 |

### 4.1 明确「**有意不加**」的（附理由，避免后人误加）

| 候选项 | 不加的理由（实测） |
|---|---|
| mjpeg `-progressive` | 🔴 **本构建 ffmpeg 根本没有这个选项**：`-progressive 1` ⇒ `Unrecognized option`；`-h encoder=mjpeg` 搜 progressive **零命中**。本仓把 `JpegProgressiveCombo` 隐藏并注明理由是**正确的诚实设计** —— 谁把它显示出来接到 mjpeg，就是**每选必失败**，重演已删的 `-dct float`（实测 `exit -22`） |
| NVENC `-temporal-aq` | 实测**惰性**：0/1 产物 MD5 完全相同 ⇒ 加它等于重犯已修的 A-4 |
| `--jxl-preserve-ultrahdr` | cjxl **明确拒绝**：`Unknown argument: --preserve_ultrahdr` exit=1 ⇒ 不发是正确的 |
| `--dng-highlight-mode` | 直连 `dngtool -H 0/1/3/9` 同输入 **4 个 MD5 完全相同** ⇒ **dngtool 侧无效果**；GUI 已正确下发 `-H`（**非产品缺陷**） |
| libaom `-tune vmaf` | 本机 libaom **构建无 VMAF**（`-tune vmaf` ⇒ `exit -22 Unable to parse`）；GUI 已主动播报 ⇒ 正确 |

### 4.2 「该删的」= **0 条**

历史三处已全清干净：`-dct float` 已从 XAML 删除（现值仅 `auto/int/fastint`）；
`SvtStillPictureCheck` 已不存在；`HwPresetCombo` 死码已随重写清除。
唯一残留是 `FfmpegOptions.cs:365` 的**过期注释**（仅注释，不影响行为）。

---

## 5. 【订正】teammate 的两条 P0 与 HEAD 实际状态不符
| teammate 结论 | 复核结果 |
|---|---|
| **A6「SVT 无损是假的」** | ✅ **底层事实正确**（Lead 独立复现：`-crf 0` ⇒ 2752 B / PSNR **45.17 dB**；`-svtav1-params lossless=1` ⇒ 10211 B / PSNR **inf**，SVT 自报 `BRC mode: Lossless Coding`）。❌ **但产品侧已在 HEAD 修**：`ImageEncoderArgs.cs:642` `if (backend == AvifBackend.Svt && o.Lossless) svtParams.Add("lossless=1");`，且其上的注释逐字记着同一组实测（`crf 0 = PSNR 45.89dB`、真无损要 `lossless=1`）。⇒ **不是待办缺口** |
| **D-2「跨后端残留」** | ❌ **已在 HEAD 修**（§2.2 逐行核对 + `git diff HEAD` 为空） |
| **D-3「`--avif-tune=film` 静默丢弃」** | ❌ 已在 HEAD **播报**：实测 `⚠ … tune=film is not expressible …no tuning flag is emitted`（且门禁 `verify-encoder-knob-liveness` 的 `N` 臂正在断言这条播报，`PASS`） |
| **D-1「`--dry-run` 与真实路径不是同一份代码，预览会撒谎」** | ✅ **真问题且未被修**（见 §6）。这一条是 teammate 最有价值的发现 |

⚠ **判据（给后人）**：这三条都属「**底层工具事实**成立、但**产品已不这样做**」。
区分方法 = 看**产品代码路径**（`ImageEncoderArgs` / `FfmpegCommandBuilder` 里的 `backend == …` 门控），
而不是看手写 ffmpeg 命令的结果。

### 5.1 【Lead 复核】gap-scout 的两条 **P0 缺口**证据不足，**降级**

我逐条复核了 `ADV_ENCODER_GAP_PLAN` 的 P0，结果**两条都需要降级**（这不是说方向错，而是**证据不足以支撑 P0**）：

**P0-①（NVENC `-rc`/`-qp`）：本机 `av1_nvenc` 根本不可用 ⇒ 那批数字无法复核。**
```
$ ffmpeg -c:v av1_nvenc … -f avif out.avif
  [av1_nvenc] No capable devices found
  Error while opening encoder …
  -> 产物 0 字节
$ ffmpeg -c:v h264_nvenc … -f mp4 out.mp4     # 对照：h264 可用
  -> 19474 B   ✅
```
- 本机确有 RTX 5080 + RTX 4060、ffmpeg 也**列出了** `av1_nvenc`，
  但实际打开会话时报 `No capable devices found`（`-gpu 0` 同样失败）
  ⇒ **本机无法真跑 av1_nvenc**（`h264_nvenc` 可以，说明不是 ffmpeg 构建问题）。
- 而 gap-scout 的证据目录 `docs/_gapscout_raw/` 里，`nvenc` 相关文件**只有选项表**
  （`enc_av1_nvenc.txt` = `ffmpeg -h encoder=av1_nvenc`），
  **没有任何一份记录了 `11877/22727/7928 B` 的原始日志**（我在整个 raw 目录搜 `11877`：**0 命中**）。
- ⇒ **结论**：`-rc` 是 NVENC 一等公民、UI 缺它确实值得补（方向成立），
  但「三档产物差异巨大」这个**论据本身在本机不可复核** ⇒ **P0 降为 P1**，
  且实现时**必须在有可用 av1_nvenc 的机器上先取到基线**（否则无法断言"生效"）。

**P0-②（avifenc `--target-size`）：它**不是**精确的体积预算控制。**
真跑（`--min 0 --max 63 -a end-usage=q -a cq-level=30 -j all`，320×240 源）：
| `--target-size` | 实产 |
|---|---|
| 2000 | **3570 B** |
| 5000 | 3570 B |
| 10000 | **7930 B** |
| 30000 | 7930 B |
| 60000 | 7930 B |
| 不带（基线） | 3570 B |
- ⇒ 它呈**阶梯式**响应（3570 / 7930 两档），且**明显低于**目标值
  （要 20000 得 7930，要 60000 还是 7930）——**不是**"命中预算"。
- gap-scout 报的「`--target-size 20000` ⇒ 19883 B（命中）」在**我的复现里不成立**
  （同参数只得 7930 B）；差异可能来自其质量参数/素材尺寸不同
  （它备注用了 512×512 与不同 cq）。⇒ 该能力**方向成立但收益被高估** ⇒ **P0 降为 P2**。
- ⚠ `--help` 自承 `up to 7 times slower` ⇒ 慢 7 倍换"大概小一点"，性价比需重新评估。

**§5.1 的元教训**（与本仓 §6 第 65 条同族）：**P0 的论据必须是可复核的读数**。
「本地跑不出、但报告里有个精确数字」这种情况，**必须降级并标注不可复核**，
否则实现者会拿着一个无法验证的基线去"修"，最后无法判断修没修好。

---

## 6. 【真问题 · 已立锁】`--dry-run` 预览与真实执行路径分离（少报编码器）

### 6.1 Lead 独立复核（两臂都有原始读数）

`--dry-run` 走 `MainWindow.BuildQueueItemCommand`（`Program.cs:338-346`），
与真实执行路径（`QueueProcessor` / `RawService`）**不是同一份代码**。

**臂 1 — `-f jxr -e Jxr`**：
```
预览: ffmpeg -y -i "…\src8.png" -threads 0 -map_metadata -1 -map_chapters -1 -frames:v 1 "…\src8.jxr"
真实: rc=0，产物 16838 B，ffprobe codec = jpegxr
      日志: [色彩] JxrEncApp 命令行: -i "…"
```
⇒ 预览**只写 `ffmpeg`**，而真实核心编码器是 **JxrEncApp** —— 用户从预览里**完全看不出**真实要跑什么。

**臂 2 — `-f dng -e Dng --dng-bit-depth 8`**（源：真 RAW `rh_0.dng` 1307014 B）：
```
预览: dngtool -e -i "…\rh_0.dng" -O "…\rh_0.dng" -jxl -effort 7 -decode_speed 1
                       ↑ 有 -jxl / -effort / -decode_speed，**漏 -4**（即 --dng-bit-depth 8）
真实: RawService 会发 -4 ⇒ 产物字节数显著变化（teammate 实测 1307014 → 546798 B）
```
⇒ 预览**漏报**一个真实生效的参数。

### 6.2 影响（**这是本轮最重要的方法论风险**）

任何**以 dry-run 为准**的自动化审计都会得到**假阴性**。
teammate 明确指出：若只信 dry-run，会**误报两条不存在的缺陷**
（「dng-bit-depth 失效」、「JXR 后端没接上」）。
本仓同族教训：`TESTING.md` §6 第 13 条「脚本并不驱动产品 exe ⇒ 证据力被高估」。

⚠ **对本文自身的影响**：正因如此，本文所有「生效」结论**一律以真跑产物为准**
（`ffprobe` / `jxlinfo` / 字节数 / PSNR），**不以 dry-run 为据**。

### 6.3 已立门禁（Lead 新增，**带牙**）—— **已接线为受管第 73 条**

`tests/scripts/verify-dryrun-parity.ps1` —— 判据**三条 + 负控**，双向覆盖：

| 判据 | 含义 |
|---|---|
| **A 多报** | 预览声称的 exe **必须**都出现在真实日志里（预览不许承诺没发生的事） |
| **B 少报** | 真实用到的**编码器类**工具（`cjxl/djxl/cjpegli/JxrEncApp/JxrDecApp/avifenc/dngtool`）**必须**出现在预览里 |
| **C 实参级** | 预览与真实的 **dngtool 实参串**必须**逐字同源**（抓 `-4`/`-jxlq` 这类**实参漂移**） |

⚠ 判据 B 只认**编码器类**工具：`ffmpeg`/`ffprobe`/`exiftool` 是**辅助/后置**步骤
（元数据恢复、探查），真实侧多出它们不构成预览缺陷 ⇒ 判它们会**假红**
（实测：png 臂真实含 `exiftool` 而不在预览里）。

**四臂**：`jxr` / `png` / `jpg` / `dng`（dng 臂需**真 RAW 源**，用仓内夹具
`tests/fixtures/raw_src_8bit.dng`；缺件 ⇒ **SKIP 点名**，fail-closed 不给绿）。

**最终读数（Lead 独立复跑）**：
```
PASS jxr：预览未声称真实没用到的工具（预览=[JxrEncApp] 真实=[exiftool,ffmpeg,JxrEncApp]）
PASS jxr：真实用到的**编码器类**工具都出现在预览里（真实编码器=[JxrEncApp]）
PASS dng：预览与真实的 dngtool **实参逐字同源**
          （预览=[-e -i <path> -O <path> -jxl -jxlq 0 -4 -effort 7 -decode_speed 1] …）
PASS 负控C①：散文行不被误当命令行（真实侧含 -4）
PASS 负控C②：-4 的实参漂移**确实**被比出（预览缺 -4 ≠ 真实含 -4）
… DRYRUNPARITY RESULT: pass=16 fail=0      rc=0
```

### 6.4 ⚠ 接线时发现并封住的**门禁自身假阴性**（值得单独记）

第一版门禁的头注释声称锁**两处**缺陷（① jxr 少报 ② dng 漏 `-4`），
但 `$arms` 里**只有 jxr/png/jpg 三臂 —— 没有 dng 臂** ⇒ **第 ② 处没有任何断言在锁**。
**变异实测**（把 dng 预览改成恒 16 位，即复现"漏 `-4`"）：
```
修前（无 dng 臂）：DRYRUNPARITY RESULT: pass=10 fail=0   rc=0   ← 缺陷在、门禁全绿（假阴性）
补 dng 臂 + 判据 C 后：pass=15 fail=1   rc=1                 ← 转红 ✅
```
⇒ 这正是「**宣称的覆盖范围 > 实际的覆盖范围**」（本仓 §6 第 87 条同族）。
⚠ 附带纠正：task 描述里我写的「目标 `pass=11`」是**笔误**（3 臂 × 3 + 1 负控 = 10）。

### 6.5 修复方式：**结构级单一真源**（不是"补两行参数"）

| 抽取的单一真源 | 原先判据处数 |
|---|---|
| `RawService.BuildEncodeToDngArguments(...)` | 预览 + 执行 = **2 处** → 1 处 |
| `JxrService.ResolveQuality(...)` | 两个真实出口（`ProcessJxrAsync` / `RawColorPipeline.EncodeJxrViaJxrEncAppAsync`）+ 预览 = **3 处** → 1 处 |

⇒ 达到"同源"的防漂移效果，改动面却最小（比"dry-run 整体改走执行层"小得多）。
**顺带修好第三处漂移**：dng 预览不只漏 `-4`，还漏 `-jxlq`，且用了**已废弃的 `-q`**
（`RawService:197` 注释记载：`-q` 是 demosaic 质量，dngtool 把它当 JXL 质量 3 ⇒ 严重有损）。

**回归（Lead 独立跑，全绿）**：`png-signature 4/0`、`format-regression 19/0`、
`decision-delivery 87/0`、`jxr-anim-input 59/0`、`cjk 14/0`（`UI 面 833 ≤ 833` **未抬基线**）；
真跑 `-f jxr` 产物 `16838 B / ffprobe codec=jpegxr`（与修前逐字节同基线）。

---

## 7. 未验证项（诚实标注，**不作推断**）

| 项 | 为什么未验证 |
|---|---|
| `av1_nvenc` / `av1_qsv` / `av1_amf` / `av1_vaapi` 四后端**生效性** | 本机无对应硬件（只有 Intel UHD + 2×NVIDIA，**无 AMD 卡**；`av1_vaapi` 本构建 `-encoders` 零命中）⇒ 只验到**参数分域**（不发错项），未验**生效** |
| `--jpeg-gain-map*`（Ultra HDR）全系列命令行取证 | 本轮未做 ⇒ 未验证 |
| `avifenc --target-size` 在 ≥4K 上的命中精度与耗时倍率 | help 自承 up to 7× slower，未实测大图 |
| `JxrEncApp -l` 在 q≥0.5 下的差异 | 未实测 |
| `librav1e` | 本机 ffmpeg 未编译该编码器 |

---

## 8. 建议的落地顺序（按 Lead 复核后的优先级）

1. ✅ **task-5（已完成并复核）**：14 条告警接 GUI 可见面 —— 纯增量、零破坏，双向读数齐备（§3.3）。
2. 🔄 **task-6（进行中）**：`--dry-run` 少报编码器（D-1），验收工具 = 新建的 `verify-dryrun-parity.ps1`（当前正锁着该缺陷）。
3. **P1 最高性价比**：**JXR 面板补 `-d` / `-s`** —— 顺带解已确证缺陷 A-17（q≤49 静默降 4:2:0），
   且 `JxrEncApp` 的域已有原始证据（`docs/_gapscout_raw/jxrenc_q.txt`）。
4. **P1 次之**：cjxl `-e` 上限 9→10（一行 XAML；⚠ 别动 `JxlEffortBox` 的 9）。
5. **P1（需先取基线）**：NVENC `-rc`/`-qp` —— **必须先找到一台 `av1_nvenc` 真能跑通的机器**记录基线，
   否则"修好了没有"无法判断（§5.1）。
6. **P2**：avifenc `--qalpha`、libsvtav1 `-qp`、cjxl `--faster_decoding` 等。
7. **独立项**：把 `verify-dryrun-parity.ps1` 接进受管清单（前提 = D-1 修完且 rc=0；
   接线要同步 `$expectedScriptCount` 72→73 与四处文档，见 `TESTING.md` §3.5 的「账全清单」）。

⚠ 每一步都要**变异验牙**（改坏 ⇒ 门禁转红 ⇒ 还原），并同步 `docs/TESTING.md` 的条数账（若增删受管门禁）。

---

## 9. 本轮新增/新建物清单

| 类型 | 路径 | 说明 |
|---|---|---|
| 门禁（**新增并已接线**） | `tests/scripts/verify-dryrun-parity.ps1` | dry-run 预览 vs 真实执行的**三判据**（多报/少报/实参级）同源性锁，4 臂 + 负控 + 反控。**已成为受管第 73 条**（`$expectedScriptCount` 72→73，四处账已同步，`shape-selfcheck=OK`）。实测 **16/0**、~11 s |
| 夹具（**新增，须随仓提交**） | `tests/fixtures/raw_src_8bit.dng` | 246 576 B；dng 臂需**真 RAW 源**。✅ 已确认**不被 gitignore**（`check-ignore` 非零）。⚠ 若漏提交 ⇒ dng 臂 fail-closed SKIP（不假绿但失效） |
| 分析报告 | `docs/ADV_ENCODER_ANALYSIS_2026-10-07.md` | 本文 |
| 队友交付 | `docs/ADV_ENCODER_MATRIX_2026-10-07.md` | 高级选项 × 后端 实机矩阵（222 个原始日志） |
| 队友交付 | `docs/ADV_ENCODER_GAP_PLAN_2026-10-07.md` | 缺口盘点（31 个原始证据文件，`docs/_gapscout_raw/`） |
| Lead 探针 | `tests/output/_lead_adv_probe2.ps1` 等 | 跨后端分域取证脚本（在 gitignored 的 `tests/output/` 下） |

### 9.1 收口后仍需人工确认的一项

`tests/fixtures/raw_src_8bit.dng`（246 KB 二进制）**尚未 `git add`** ——
本会话按纪律不做提交动作。⇒ **提交时务必纳入**，否则干净检出上 dng 臂会 SKIP（不假绿，但该臂失效）。
