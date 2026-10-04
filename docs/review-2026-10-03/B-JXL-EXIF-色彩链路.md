# B 线独立复查：JXL 编码参数 / EXIF ISO / 色彩链路

> **复查性质**：从零开始、不信既有结论的**只读**复审。全过程未修改任何源码、未 `git add/commit`、未执行 `dotnet build`。
> **基准**：`e7de117`（v1.6.0-beta3） → **HEAD** `bdd4b0b`（区间 7 个提交）
> **结论行号**：均为 HEAD（bdd4b0b）当前源码行号
> **取证手段**：`git diff` / `git log` / `grep` / 逐行读源码 / 独立数值核算（Python 复算双精度与有理数两条路径）

---

## 0. 结论速览

| # | 承诺 / 声明 | 判定 |
|---|---|---|
| 1a | `MapJxlDistance` 逐符等于 `q≥100→0；否则 0.1+(100-q)*0.09`，Round 1 位 AwayFromZero | **已兑现** |
| 1b | 5 处重复副本全部收敛到单一真值 | **已兑现**（实际收敛 6 个调用点） |
| 1c | 第 6 处副本 `tools/src/dngtool/dngtool.cpp` 未改 | **属实**（旧公式 `(100-quality)*15.0/100.0` 仍在） |
| 1d | `MapJxlDistanceInverse` 与正函数**严格互逆** | **未兑现**（0..100 中 10 个 q 往返差 ±1） |
| 2 | EXIF ISO 多标签保序回退 | **基本兑现**，但退出码不检查、带单位/括号的值被静默跳过 |
| 3 | RAW→JXL 的 PNG 中转保元数据 | **已兑现**（分支/唯一性/清理/不回退 四条都对） |
| 4 | `UsesLosslessJpegRewrap` 5 条判据 + 与 modular 互斥 | **已兑现**（5 条全在；modular 确实获胜） |
| 5 | `--jxl-modular` 三条路径都接到实参 | **部分兑现**（CLI/预设 ✅，GUI 在 cjxl 后端下**够不着**） |
| 6 | libaom tune「认不出的值」不再折成空串 | **已兑现**（归一化层），但**下游点名漏了 libaom 与 SVT 两格** |
| 7 | QueueProcessor 后端非 cjxl 的点名 | **已兑现**（CLI 与预设两条路径都覆盖，且是单一分派点） |

**新发现的问题 7 组（含子项）+ 1 个接线缺口**：
D-1 `MapJxlDistanceInverse` 不严格互逆（§1.4）；D-2 注释自称 `q75→2.35` 与代码实际发出的 `2.4` 矛盾（§1.5）；D-3a ISO 值带单位/括号被静默跳过、D-3b 退出码完全不看（§2.3/§2.4）；D-4 中转临时文件前缀 `jxl_meta_` 未登记进清扫表（§3.5）；D-5 中转 PNG 触发措辞失配的重复播报（§3.6）；D-6 `JxlModular` 字段被 DNG 模式覆写（§5.3）；D-7/D-7b/D-7c tune 点名在 libaom 与 SVT 上漏兜（§6.2-6.4）。
缺口 G-1：`--jxl-modular` 的 GUI 复选框在 cjxl 后端下不可见（§5.2）。

---

## 1. JXL distance 公式收敛到单一真值

### 1.1 【已兑现】公式本身与声明逐符相符

`src/FfmpegGui/Services/ColorMapping/ImageEncoderArgs.cs:61-66`：

```csharp
public static double MapJxlDistance(int quality)
{
    int q = Math.Clamp(quality, 0, 100);
    if (q >= 100) return 0.0;
    return Math.Round(0.1 + (100 - q) * 0.09, 1, MidpointRounding.AwayFromZero);
}
```

逐符核对：`q≥100 → 0` ✅（`ImageEncoderArgs.cs:64`）；`0.1 + (100-q)*0.09` ✅（`:65`）；保留 1 位小数 ✅；`MidpointRounding.AwayFromZero` ✅。
`Clamp(0,100)` 是新增的保护（旧实现 `Math.Round((100-quality)*15.0/100.0,1)` 无钳制，q>100 会出负值），属顺带加固，与声明不冲突。

### 1.2 【已兑现】调用点确实全部收敛（且比声明多收敛一处）

全仓 grep `MapJxlDistance`（排除 `tests/output/` 下的历史拯救副本与变异备份）后，生产代码里**不存在任何第二份算式**，只剩 6 个外部调用点 + 1 个同文件内的发送点：

| 文件:行号 | 形态 |
|---|---|
| `src/FfmpegGui/Services/ColorMapping/ImageEncoderArgs.cs:206` | `-distance` 实参（ffmpeg libjxl 有损档） |
| `src/FfmpegGui/Services/CjxlService.cs:432` | cjxl JPEG 输入有损档 |
| `src/FfmpegGui/Services/CjxlService.cs:445` | cjxl 非 JPEG 输入有损档 |
| `src/FfmpegGui/Models/FfmpegOptions.cs:565` | `MapJxlDistanceForward` 转调 |
| `src/FfmpegGui/Models/FfmpegOptions.cs:566` | `MapJxlDistanceInverse` 转调 |
| `src/FfmpegGui/Services/FfmpegCommandBuilder.cs:1025` | 私有包装转调（供 `:443` 发送） |
| `src/FfmpegGui/MainWindow.xaml.cs:1491` | UI 预览 |

其中 `FfmpegCommandBuilder.cs:1025` 是声明里没提到、但也已收敛的第 6 处。未发现"漏网的第 6 处副本"（生产代码中）。

仍需登记的历史副本（**均非生产代码**，不影响运行）：
- `tests/output/worktree_salvage/e6ede830/files/src/FfmpegGui/Models/FfmpegOptions.cs:495` — 基线前的拯救副本，仍是旧公式。
- `src/FfmpegGui/Services/RawService.cs:230` — 只有**注释**里写着 `(100-3)*15/100 = 14.55f`，无活代码（描述 dngtool 的历史坑），不构成副本。

### 1.3 【已兑现（属实）】`tools/src/dngtool/dngtool.cpp` 仍是旧公式 —— 第 6 处副本未收敛

`tools/src/dngtool/dngtool.cpp:1391-1392`：

```cpp
settings->SetDistance ((quality <= 0) ? 0.0f
                                      : (float)((100 - quality) * 15.0 / 100.0));
```

这是**活跃的** C++ 代码，走 DNG 的 JXL 压缩分支（`-jxl` + `-jxlq`，见 `:1509` 与 `src/FfmpegGui/CliParser.cs:803` 的 `dng-jxl-quality`）。它不受 `MapJxlDistance` 约束，用的仍是 `(100-q)*15/100`。

- 与"声称那里仍有一份旧公式未改"**逐字相符**。
- 与 `docs/JXL_RAW_16BIT_ADVISORY_2026-10-02.md:27` 的"消灭 5 处副本"措辞**不冲突**（那份清单只列 C# 侧），但该文档**没有**把 dngtool 这条登记为已知未收敛项 ⇒ 建议补登记。
- 影响面：只影响 `--format dng --dng-compression 1` 的 DNG 内嵌 JXL 质量轴，不影响 `jxl` 目标格式的产出。**是否要一并换成官方曲线是一个待决的取舍**（DNG 内嵌 JXL 的"quality"语义历史上就是另一条轴），此处只确认"未改"这一事实。

### 1.4 【未兑现 / 与声明不符】`MapJxlDistanceInverse` **不是**严格互逆 —— 缺陷 D-1

`ImageEncoderArgs.cs:73-77`：

```csharp
public static int MapJxlDistanceInverse(double distance)
{
    if (distance <= 0) return 100;
    return (int)Math.Clamp(Math.Round(100 - (distance - 0.1) / 0.09), 0, 100);
}
```

文档首句（`ImageEncoderArgs.cs:70`）写的是"与正函数**严格互逆**"，紧接着 `:71` 又写"往返存在 ±1 的量化误差（如 q75 → 2.4 → 74）"。**两句自相矛盾**，而"严格互逆"是错的。

按题目要求逐个人工核算（同时用双精度与有理数两条路径复算，结论一致）：

| q | 精确值 `0.1+0.09(100-q)` | 双精度实际值 | 正函数输出 `d` | 反函数 `inv(d)` | 往返 |
|---|---|---|---|---|---|
| 30 | 6.40 | 6.4 | **6.4** | 30 | ✅ |
| 50 | 4.60 | 4.6 | **4.6** | 50 | ✅ |
| 75 | 2.35 | 2.3500000000000000888 | **2.4** | **74** | ❌ −1 |
| 90 | 1.00 | 1.0 | **1.0** | 90 | ✅ |
| 95 | 0.55 | 0.5499999999999999334 | **0.5** | **96** | ❌ +1 |
| 99 | 0.19 | 0.19 | **0.2** | 99 | ✅ |
| 100 | — | — | **0.0** | 100 | ✅ |

全段扫 `q = 0..100`：**10 个 q 往返不等**，全部落在 `100-q ≡ 5 (mod 10)` 上，即
`q ∈ {5, 15, 25, 35, 45, 55, 65, 75, 85, 95}`，误差方向随双精度落在半点的哪一侧而定（q=5/95 为 +1，其余为 −1）。

根因是**结构性**的，不是浮点噪声：
- 正函数值 `0.1 + 0.09k = (10 + 9k)/100`（k = 100−q）只有在 `9k ≡ 0 (mod 10)` 即 `k ≡ 0 (mod 10)` 时才恰好是 1 位小数；其余全部带第 2 位小数 ⇒ 1 位取整必然引入 ≤0.05 的偏移。
- 反函数除以 0.09 ⇒ 偏移被放大到 ≤ 0.05/0.09 ≈ **0.556 个质量单位** ⇒ 四舍五入后整段 ±1。

**实际影响**：`MapJxlDistanceInverse` 的唯一消费者是 `FfmpegOptions.ParseQualityFromDisplay`（`Models/FfmpegOptions.cs:591`，用户在"实际参数"输入框里手输 distance 反解滑块）。可见症状：输入 `2.35` → 滑块 75 → 显示框回显 `2.4`（因为 `MapJxlDistance(75)=2.4`）⇒ 用户手输的值"跳一下"。不崩、不丢数据，但确实不是严格互逆。

**为什么门禁抓不到**：`tests/ServiceProbe/Program.cs:3276-3279` 的 `qa-①` 锚点恰好选了 `q30 ⇒ 6.4` 与 `q90 ⇒ 1.0`——两个都是 `k ≡ 0 (mod 10)` 的**恰好互逆点**。探针因此对这个 ±1 缺陷**结构性不敏感**（同理 `docs/JXL_RAW_16BIT_ADVISORY_2026-10-02.md:27` 的变异验证也只锁了这两个锚点）。建议补一条断言：对 `q ∈ {75, 85, 95}` 断言往返值，或直接把文档首句的"严格互逆"改成"±1 量化误差内互逆"。

### 1.5 【新发现的缺陷】注释自称的 `q75→2.35` 与代码实际发出的 `2.4` 矛盾 —— 缺陷 D-2

`ImageEncoderArgs.cs:56` 的可追溯性注释写：

> 实测三点与 cjxl `-q` 产物**逐字节等长**（1200x800 16-bit）：q30→6.4 · **q75→2.35** · q90→1.0。

但本函数对 `q=75` 输出的是 **2.4**（1 位取整，见 1.4 表），而 `-d 2.35` 与 `-d 2.4` 的产物**不可能逐字节等长**。对照 `.workbuddy/memory/2026-10-02.md:12`，`2.35` 是 **cjxl 官方 `-q 75` 的等价 distance**，不是本实现发出的值。

⇒ 这条注释把"官方等价点"误写成"本实现的实测点"。要么改注释为 `q75→2.4（≈官方 -q75 的 2.35）`，要么（若真要做到逐字节等长）去掉 1 位取整、直接发 `2.35`。**当前代码发的是 2.4，注释说的是 2.35，二者必有一处与实测对不上** —— 这是本轮唯一一处"注释与代码可证伪地不一致"的地方，建议实测复验后二选一改掉。

### 1.6 【诚实边界（已知取舍）】

- `q < 30` 段：官方走非线性曲线（实测 q20≈9.05、q10≈14.8、q0 > 15），本实现全段用同一条直线 ⇒ q0 只到 9.1，比官方**保守**（压得轻、体积偏大）。已在 `ImageEncoderArgs.cs:57-59` 明确登记为刻意取舍。✅ 属诚实的已知取舍。
- `MapJxlDistance` 的 `q≥100 → 0` 与 `opts.Lossless` 走的是两条独立路径（`CjxlService.cs:427/439`、`ImageEncoderArgs.cs:200`），二者结果一致，无冲突。

---

## 2. EXIF ISO 多标签回退（`ExifToolService.ReadIsoAsync`）

改动位置：`src/FfmpegGui/Services/ExifToolService.cs:97-145`（命令 `:108`，解析循环 `:128-134`，超时 `:120-141`）。

### 2.1 【已兑现】(a) 确实跳过 `-f` 打印的 `-`，不会当数值或当 0

```csharp
Arguments = $"-f -s -s -s -ISO -PhotographicSensitivity -ISOSpeedRatings -SonyISO -ISOSetting \"{imagePath}\"",   // :108
foreach (var raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))                     // :128
{
    var line = raw.Trim();
    if (string.IsNullOrEmpty(line) || line == "-") continue;                                                        // :131
    if (int.TryParse(line.Split('.')[0], out var iso) && iso > 0)                                                   // :132
        return iso;
}
```

`line == "-"` 精确跳过 ✅；`-` 不会被 `int.TryParse` 也不可能被当成 0（`-` 解析失败即 `continue`）✅。与 `.workbuddy/memory/2026-10-02.md:38` 的负控（"无 EXIF 的 cjxl 产物五行全 `-` ⇒ 返回 null"）一致。

### 2.2 【已兑现】(b) 取值顺序符合声明的优先级

循环按**输出行序**取第一个可解析的正整数，而命令行标签序为
`ISO → PhotographicSensitivity → ISOSpeedRatings → SonyISO → ISOSetting`（`:108`），与 `:93` 的声明顺序逐字一致 ✅。

- 前提（exiftool 按命令行顺序输出这 5 行）由负控"五行全 `-`"与正控"`-ISO` **首行**均为 400"（`.workbuddy/memory/2026-10-02.md:37-38`）间接佐证 ✅。
- 该顺序依赖的是 exiftool 的行为而非本仓代码；本仓未见单独钉住这一前提的回归断言 ⇒ 列为**诚实边界**。

### 2.3 【新发现的缺陷】(c) 带单位/括号的值不崩，但会被**静默跳过** —— D-3a

逐类核算（`int.TryParse(line.Split('.')[0])`：默认 `NumberStyles.Integer`，允许前后空白与前导符号，**不允许**千分位/单位/括号）：

| 输入 | `Split('.')[0]` | `TryParse` | 行为 | 结论 |
|---|---|---|---|---|
| `"-1"` | `-1` | ✅ −1 | `iso > 0` 假 ⇒ `continue` | 不崩、不误判为 0 ✅ |
| `"0"` | `0` | ✅ 0 | `> 0` 假 ⇒ `continue` | 正确跳过 ✅ |
| `"AUTO"` / `"auto"` | 原串 | ❌ | `continue` | 不崩 ✅ |
| `"ISO 400"` | `"ISO 400"` | ❌ | `continue`，**落入下一个标签** | 不崩，但该值取不到 |
| `"400 (equivalent)"` | `"400 (equivalent)"` | ❌ | `continue`，**落入下一个标签** | 不崩，但该值取不到 |
| `"100.5"` | `"100"` | ✅ 100 | 取 100 | 与注释"取整数部分"一致 ✅ |
| `"1,600"` | `"1,600"` | ❌ | `continue` | 不崩，取不到 |

⇒ 题目要求"会不会崩或误判"：**不崩、不误判为 0** ✅；但 `"ISO 400"` / `"400 (equivalent)"` 这类 Sony `ISOSetting` 的常见写法会被**静默丢弃**（不是解析成 400，也不是报错，而是当作"这个标签没有"）。由于 `-ISO` 排在最前且通常已返回 400，实际影响小；但若某个素材只有 `ISOSetting` 且带尾巴，就会回落到"读不到 ISO"——与"多标签回退"的意图相悖。属于**可用但不够强的解析**，建议（不改语义前提下的最小修补）先取第一个整数 token，例如正则 `\d+` 的首个匹配。

### 2.4 【未兑现 / 与声明不符】(d) 超时有、退出码**完全没有**处理 —— D-3b

- **超时**：`cts.CancelAfter(5s)` 与外部 `ct` 链接（`:120-121`），`ReadToEndAsync`/`WaitForExitAsync` 都传 `cts.Token`（`:124-125`），捕获 `OperationCanceledException` 后 `Kill(entireProcessTree:true)` 并**出声**（`:136-141`）⇒ 超时路径 ✅ 兑现，且是"显式失败 + 点名"，不静默。
- **stderr**：`BeginErrorReadLine()`（`:118`）排空管道，避免 4KB 缓冲写满堵死 ✅。
- **退出码**：**从未读取**。`p.ExitCode` 在本方法里一次都没出现。exiftool 非 0 退出（文件不可读/损坏）但 stdout 仍有内容时，本方法照常解析并返回；exiftool 因参数错误在 stderr 打印 `Nothing to do.` 且 stdout 为空时，返回 null（结果碰巧正确，但归因是"没有 ISO 元数据"，与事实不符）。

⇒ "超时与退出码处理"这一条**只兑现了一半**。修法成本极低（在 `:125` 之后加一次 `p.ExitCode != 0` 的出声分支），建议补上。

### 2.5 【诚实边界】下游钳制不出声

`QueueProcessor.ResolveCjxlPhotonNoiseIsoAsync:2015` 把读到的 ISO 钳到 `100..3200`。ISO=50 或 ISO=12800 的素材会被静默改成 100 / 3200，日志只打印钳制**后**的值（`item.Log` 打印的是 `iso.Value` 原值与钳制后值，`:2016`，读者能看出差异）。属可接受，但"钳制"本身没有独立点名 ⇒ 诚实边界。

---

## 3. RAW→JXL 的 PNG 中转保元数据（`RawColorPipeline.EncodeJxlViaCjxlAsync`）

改动位置：`src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs:499-595`。

### 3.1 【已兑现】(a) 分支覆盖了 SDR / alpha / HDR 的全部组合，无落空格

```csharp
bool hdrTarget = ColorEncodingHelper.IsHdrDescriptor(d);                    // :515
bool usePngCarrier = !hdrMeta.hasAlpha && !hdrTarget
    && !string.IsNullOrWhiteSpace(inputPath) && File.Exists(inputPath);     // :516-517
```

| 组合 | 走向 | 是否符合声明 |
|---|---|---|
| SDR + 无 alpha + 源在盘 | PNG 中转（`:528-551`） | ✅ |
| SDR + **有 alpha** | PAM + alphamerge 管道（`:557-571`） | ✅ 声明即如此 |
| **真 HDR 目标**（PQ/HLG）+ 无 alpha | PPM 管道（`:572-577`） | ✅ 声明即如此 |
| HDR + alpha | PAM 管道（`:557` 分支先判 alpha） | ✅ 合理 |
| 源路径为空 / 文件不存在 | PPM 管道 + 点名"源文件不可读"（`:518-522`） | ✅ 有兜底且出声 |

`else` 分支内部再按 `hdrMeta.hasAlpha` 二分成 PAM / PPM（`:557` / `:572`），与改动前的结构逐字一致 ⇒ 不存在"落空组合走进错误默认分支"。

### 3.2 【已兑现】(b) 临时文件路径唯一，并发不会互相覆盖

`RawColorPipeline.cs:532`：

```csharp
pngTmp = Path.Combine(PlatformServices.GetTempDir(), $"jxl_meta_{Guid.NewGuid():N}.png");
```

`Guid.NewGuid():N` ⇒ 每次调用（含并发队列多任务）文件名互不相同 ✅。目录来自 `PlatformServices.GetTempDir()`（`Services/PlatformServices.cs:474`，用户缓存目录 → RAMDISK → /dev/shm → 系统临时目录），与其他中转件同一套策略，**不会落到源文件目录** ✅。

### 3.3 【已兑现】(c) `finally` 在所有异常路径都执行，且删不到用户源文件

```csharp
try { ... }                                                      // :526-591
finally { if (pngTmp != null) { try { File.Delete(pngTmp); } catch { } } }   // :592-595
```

- `pngTmp` 在 try 内首行赋值，随后任何 `throw`（`:542` PNG 落盘失败、`:600` 空产物，以及 `RunWithOptionsAsync` 内部的异常）都会经过 `finally` ✅。
- 被删对象恒为本方法自己创建的 GUID 文件名 ⇒ **不可能**删到用户源文件 ✅。
- `catch { }` 吞掉删除失败（文件被占用等），属既有风格，不静默改写产物语义 ⇒ 可接受。

### 3.4 【已兑现】(d) "失败不回退"在代码里没有隐藏的 catch 兜成 PPM

- 落盘失败处**显式抛错**，且文案点明不回退（`:541-544`）：
  `throw new InvalidOperationException("cjxl 出口：PNG 中转落盘失败（ffmpeg 退出码 …）⇒ 不回退 PPM 管道…")`。
- 本方法 `try` 只有 `finally`、**没有 `catch`**（`:526-595`）⇒ 不存在局部兜底。
- 调用链上唯一捕获点 `QueueProcessor.cs:1262-1266` 把异常映射成 `return (true, 92, plan)`（**任务失败**，退出码 92），不切回 PPM、也不切回 ffmpeg libjxl ✅。
- `RawColorPipeline.cs:211-214`（`TransformFileAsync`）在 `ec != 0` 或空产物时抛错，同样不提供"换载体重试"的分支 ✅。

⇒ "失败不回退"这一承诺在源码层面**完全成立**。

### 3.5 【新发现的缺陷】中转临时文件前缀未登记进清扫表 —— D-4

`PlatformServices.cs:532-543` 的僵尸清扫表：

```csharp
TempDirPrefixes = { "raw_", "gainmap_", "gmdecode_", "uhdr_linear_", "jxr_", "jxr_input_",
                    "avif2gifwebp_", "avifenc_frames_", "ffmpeg_ps_check" };
TempFilePrefixes = { "icc_extract_", "qa_src_", ... };
```

**`jxl_meta_` 两个表都不在**。⇒ 进程被强杀 / 硬崩溃导致 `finally` 没跑到的场合，这些 16-bit PNG（6024×4024 可达数百 MB）会永久留在缓存目录，且 24 小时清扫也不会收。修法：把 `"jxl_meta_"` 加进 `TempFilePrefixes`。（注：清扫逻辑 `PlatformServices.cs:557` 只遍历 `EnumerateDirectories`，`TempFilePrefixes` 是否真的被文件级清扫用到需一并核对 —— 若未用，则本条需改成补文件级清扫。）

### 3.6 【新发现的缺陷】`JxlLosslessJpeg=true` 时，中转 PNG 会触发措辞失配的播报（且打两遍）—— D-5（轻微）

PNG 分支里 `BuildCjxlArguments` 被调用两次：一次仅为打日志（`:545-546`，带 `log`），一次在 `RunWithOptionsAsync` 内部（`:299-300`，也带 `logCallback`）。当 `opts.JxlLosslessJpeg == true` 时，两次都会命中 `CjxlService.cs:379-382` 的点名：

> `--lossless_jpeg is requested but the input is not a JPEG file (pipe/raw stream): …`

而此刻的输入其实是**中转 PNG**，不是"pipe/raw stream" ⇒ 措辞与事实不符，且同一条消息在一条任务日志里出现两次。结论本身（本次不重封装、是像素重编码）是**对的**，只是理由文本误导。建议把该文案改成中性（"input is not a JPEG (pipe/PNG/raw stream)"）或让 PNG 中转路径只在实跑那次传 log。

### 3.7 【诚实边界】

- 走 PPM/PAM 的两格（alpha、真 HDR）会**点名**"源 EXIF/XMP 不会保留"（`:518-522`）⇒ 可用性诚实已兑现，但**这两格确实仍丢元数据**，是刻意取舍（PNG 表达不了 HDR 语义 / alphamerge×PNG 组合未验）。
- 注释自称"中转 PNG 由 rawvideo 写出、无色彩 chunk ⇒ `-x color_space=` 照常生效"（`:505-510`），该结论**依赖实测**，静态复审无法证伪也无法证实 ⇒ 列为诚实边界，建议由实机门禁（`verify-color-wiring` / `verify-jbrd-e2e.ps1`）补一条"RAW→JXL 产物必须能读回 ISO/EXIF"的断言（当前 `tests/scripts/` 下未见这一条）。

---

## 4. `CjxlService.UsesLosslessJpegRewrap`：5 条判据与 modular 互斥

### 4.1 【已兑现】5 条判据**全部**真的实现了

`src/FfmpegGui/Services/CjxlService.cs:271-274`：

```csharp
=> opts != null && IsJpegInputPath(inputPath) && opts.JxlLosslessJpeg
   && string.Equals(opts.Format, "jxl", StringComparison.OrdinalIgnoreCase)
   && !JpegCarriesGainMap(inputPath)
   && opts.JxlModular != true;
```

| 判据 | 位置 | 兑现 |
|---|---|---|
| ① 输入是 JPEG（全仓唯一判据 `IsJpegInputPath`，`:203-207`） | `:271` | ✅ |
| ② 开关 `JxlLosslessJpeg` 为真 | `:271` | ✅ |
| ③ 目标格式必须是 jxl（2026-10-01 补） | `:272` | ✅ |
| ④ 源不携带增益图（2026-10-01 补，新增之一） | `:273` + `JpegCarriesGainMap:227-246` | ✅（含 512 项路径缓存与只读头解析） |
| ⑤ 未强制模块化（新增之二） | `:274` `opts.JxlModular != true` | ✅ |

`!= true` 的语义正确：面板"未勾选"（`null`）含义是"不强制模块化"，而 cjxl 的 `-m 0` 是"**强制** VarDCT"，两者不等价 ⇒ 只有显式 `true` 才否决（`:269-270` 已说明）✅。

同源消费点确认：本谓词被 `CjxlService.BuildCjxlArguments:415`（决定 `--lossless_jpeg=1`）与 `QueueProcessor.cs:1714`（决定 Orientation 标签去留）**两处共用同一份实现** ✅，与"两处必须同源"的设计意图一致（`tests/ServiceProbe/Program.cs:5219-5221` 也有断言）。

### 4.2 【已兑现】与 `--modular=1` 互斥，且两者同给时 **modular 获胜**

- `opts.JxlModular == true` ⇒ 判据 ⑤ 为假 ⇒ `UsesLosslessJpegRewrap` 返回 **false** ⇒ 不进入 `:415` 的 `-d 0 --lossless_jpeg=1` 分支，转而走 `:421` 的 `isJpegInput` 分支并显式发 `--lossless_jpeg=0` ⇒ **✅jbrd 无损重封装确实失效** ✅。
- `--modular=1` 在 `:395-403` 无条件发出，且**排在** distance/lossless_jpeg 之前 ⇒ 命令行里两者并存时 modular 生效、jbrd 不生效 ✅。
- 同时**出声**：`:398-402` 打印 `--jxl-modular and lossless JPEG repacking are structurally exclusive … => modular mode wins, this run re-encodes pixels` ✅（`tests/scripts/verify-jbrd-e2e.ps1:339-347` 的 G1/G3 断言即对这两点）。

小瑕疵（不影响结论）：该点名条件（`:398-399`）只查 `JxlLosslessJpeg && isJpegInput && format==jxl`，**不查增益图**。若源本身带增益图（重封装本就不可能），仍会打出"modular mode wins"这句 —— 结论仍成立（确实在重编码），只是归因不完整。

---

## 5. `--jxl-modular` 是否真的接成实参

### 5.1 【已兑现】CLI 与预设两条路径

| 路径 | 位置 | 兑现 |
|---|---|---|
| CLI | `src/FfmpegGui/CliParser.cs:732` `case "jxl-modular": options.JxlModular = ParseBoolStrict(key, value);` | ✅ |
| 预设 → 模型 | `src/FfmpegGui/Models/FfmpegOptions.cs:69` `if (p.JxlModular.HasValue) JxlModular = p.JxlModular;` + `Models/PresetData.cs:34` | ✅ |
| 预设 → UI 回放 | `src/FfmpegGui/MainWindow.xaml.cs:5915` `if (JxlModularCheck != null && p.JxlModular.HasValue) JxlModularCheck.IsChecked = …` | ✅ |
| 预设内置值 | `src/FfmpegGui/Services/PresetManagerService.cs:88/102/117` | ✅ |

下游两个后端都真的发参数：
- ffmpeg/libjxl：`ImageEncoderArgs.cs:202` 与 `:208`（无损/有损两支都发 `-modular 1`）+ `FfmpegCommandBuilder.cs:446-447` ✅（探针 `ServiceProbe/Program.cs:3283` 的 `qa-③` 钉着）
- cjxl：`CjxlService.cs:395-397` 发 `--modular=1` ✅（`verify-jbrd-e2e.ps1:342` 钉着）

⇒ "此前是被采集但无消费者的空选项"这一说法在**当前 HEAD 已不成立**。

### 5.2 【未兑现 / 与声明不符】GUI 路径在 **cjxl 后端下够不着** —— 缺口 G-1

`JxlModularCheck` 挂在 `JxlFfmpegPanel` 内（`src/FfmpegGui/MainWindow.xaml:626-634`，`JxlModularCheck` 在 `:632`），而：

```csharp
if (JxlFfmpegPanel != null) JxlFfmpegPanel.IsVisible = backend != EncoderBackend.Cjxl;   // MainWindow.xaml.cs:2441
if (JxlCjxlPanel   != null) JxlCjxlPanel.IsVisible   = backend == EncoderBackend.Cjxl;   // :2443
```

⇒ **后端选 cjxl 时该复选框不可见**，用户无法为 cjxl 路线勾选强制模块化。采集侧 `MainWindow.xaml.cs:2919` 是 `useAdvCodec ? JxlModularCheck?.IsChecked : null`（不看可见性），所以只有两条绕行路径能置位：① 先在 ffmpeg 后端勾上、再切到 cjxl（值残留）；② 预设/CLI。

结论：`--jxl-modular` 到 `--modular=1` 的**实参接线三条路径都通**，但**"GUI 手动置位"这条在 cjxl 后端下是断的**。这与"只接了一两条"的怀疑不同——接的比声称的多一条（预设也通），只是 GUI 这一格有可见性缺口。建议：把 `JxlModularCheck` 提到 `JxlCodecPanel` 层（两个后端共用一只控件，与 `JxlLosslessJpegCheck` 的处理同款，`MainWindow.xaml:649-656` 有先例与理由）。

### 5.3 【新发现的缺陷】`JxlModular` 字段被 DNG 模式覆写 —— D-6（潜在语义冲突）

`src/FfmpegGui/MainWindow.xaml.cs:3000`（RAW→DNG 分支内）：

```csharp
// 兼容旧字段（供其他路径读取）
options.JxlModular = useJxl;          // useJxl = DngCompressionCombo.SelectedIndex == 1
```

这会把 `:2919` 刚采集到的"强制模块化"**无条件覆盖**成"DNG 是否用 JXL 压缩"。当前不产生错误产物（DNG 分支下 `Format=="dng"`，`UsesLosslessJpegRewrap` 的判据 ③ 为假、`BuildCjxlArguments` 不被调用），但同一个 `bool?` 字段被两个互不相关的语义复用，正是本仓反复修过的"两套映射必然漂移"的形状。建议改用一个只写不读的兼容字段（如 `DngUseJxlCompression`）或加注释锁死。

---

## 6. libaom-av1 的 tune：`NormalizeTuneToken` 与分发

### 6.1 【已兑现】"默认档/未选择" 与 "认不出的值" **已经被区分开**

`src/FfmpegGui/Services/ColorMapping/ImageEncoderArgs.cs:378-389`：

```csharp
var u = ((display ?? "").Trim()).ToUpperInvariant();
if (u.Length == 0) return "";                                        // 未选择
if (u.StartsWith("IQ", …)) return "iq";
if (u.Contains("MS_SSIM", …)) return "ms_ssim";                      // 必须在 SSIM 之前
if (u.Contains("VMAF", …)) return "vmaf";
if (u.Contains("PSNR", …)) return "psnr";
if (u.Contains("SSIM", …)) return "ssim";
if (u == "DEFAULT" || u == "默认") return "";                        // 默认档 ⇒ 不发、不播报
return u;                                                            // 认不出 ⇒ 原样返回，不许冒充"没选"
```

- 旧写法尾部是 `return "";`（认不出也折成空串）⇒ **现在确实改掉了** ✅。
- 默认档字面量已核对：zh-CN `avif.tune.default` = `"默认"`（`Resources/Locales/zh-CN.json:227`）、en-US = `"Default"`（`:227`）⇒ `u == "默认"` / `u == "DEFAULT"` 精确命中，**没有更长的变体** ⇒ 不会把默认档误判成"认不出" ✅。
- `MS_SSIM` 判据排在 `SSIM` 之前（`MS_SSIM` 含 `SSIM` 子串），顺序正确 ✅。

⇒ 题目最关心的那一条（"两者是否仍都返回空串"）：**不是**，已正确区分。

### 6.2 【未兑现 / 与声明不符】"由中心播报统一兜"的自述与实现矛盾 —— libaom 上认不出的值**完全无声** —— D-7

`ImageEncoderArgs.cs:558-560` 的注释明确写：

> 认不出的值（`--avif-tune film` 这类，`NormalizeTuneToken` 现在原样返回）由下面 switch **之后**的中心播报统一兜 ⇒ 同一个值不该挨两条措辞不同的播报。

但"中心播报"（`:603-607`）的实现是：

```csharp
if (backend is not (AvifBackend.Libaom or AvifBackend.Svt)
    && (tuneTok.Length > 0 || svtTuneTok.Length > 0))
    Console.WriteLine("[avif] tune=… is not expressible on encoder '…' …");
```

它**显式排除**了 `Libaom` 与 `Svt`。而 libaom 分支的 `default`（`:554-566`）只对 **已认得** 的 token 出声（`if (tuneTok.Length > 0 && IsKnownTuneToken(tuneTok))`，`:561`）。

⇒ **`backend = libaom` + 认不出的值**（如 `--avif-tune film` ⇒ `tuneTok = "FILM"`，`IsKnownTuneToken("FILM") == false`）落到：
- libaom `default` 支：`IsKnownTuneToken` 为假 ⇒ **不播报**；
- 中心播报：`backend is not (Libaom or Svt)` 为假 ⇒ **不播报**。

**两处都不播报 ⇒ 静默丢弃，正是本次要消灭的形状。** 注释说"由中心播报兜"，代码却把它排除在外 —— 声明与实现直接矛盾。修法：中心播报的条件改成只排除 `Svt`（Svt 分支自己有 `else if`），或把 libaom `default` 的 `IsKnownTuneToken` 门去掉。

### 6.3 【新发现的缺陷】SVT 后端上认不出的值同样无声 —— D-7b

`case AvifBackend.Svt`（`:569-594`）：`SvtTuneValue("FILM")` 返回 `null` ⇒ 走 `else if (tuneTok == "iq" && svtTuneTok.Length == 0)`（`:587`）⇒ 条件不满足 ⇒ **什么都不发、什么都不说**；中心播报又因 `is not (… or Svt)` 排除 Svt ⇒ 静默。

### 6.4 【新发现的缺陷】libaom 后端 + 只设了 **SVT 专用** tune ⇒ 两条播报都不覆盖 —— D-7c

`libaom` 分支只看 `tuneTok`（通用 tune），**完全不看 `svtTuneTok`**；中心播报在 libaom 上也排除。⇒ 用户用 `--avif-svt-tune vq`（VQ 是 SVT 专有，`KnownTuneTokens` 里也没有 `"vq"`，见 `:392`）配 libaom 后端时：不发参数、不播报。
同理 `SvtTuneValue` 认得 `"vq"`（`:414` `"vq" => "0"`），但 `KnownTuneTokens`（`:392`）**漏了 `"vq"`** ⇒ 即便将来中心播报覆盖到 libaom，`vq` 也会被当成"认不出"。建议把 `"vq"` 补进 `KnownTuneTokens`。

### 6.5 【已兑现】硬件后端整支跳过 tune 现在**会出声**

`:603-607` 覆盖 `Nvenc / Qsv / Amf / Vaapi / Other` 五支，且条件是 `tuneTok.Length > 0 || svtTuneTok.Length > 0`（即"未选择/默认档"折成的空串不触发）⇒ **默认档与已选值被正确区分**，不会给默认档假告警 ✅。这一半确实修好了。

---

## 7. QueueProcessor 的"后端非 cjxl 且勾了噪声/渐进"点名

位置：`src/FfmpegGui/Services/QueueProcessor.cs:724-744`（判据 `:733-734`，两条消息 `:736-743`）。

### 7.1 【已兑现】覆盖 CLI 与预设回放两条路径

- 该块位于 `ProcessAsync`（`QueueProcessor.cs:219`）的**分派链之前**：与 `colorPostStepsDone`（`:706`）和 `if (inputExt == ".gif" …)`（`:750`）**同一缩进层级**（均为 28 空格）⇒ 在同一条无条件的语句序列上，**不在任何 `if` 内**。
- 它排在 `ResolveCjxlPhotonNoiseIsoAsync`（`:720-722`）之后、所有专用通路分支（`:750` 起，含 `ProcessCjxlAsync:798`、`ProcessJxlInputAsync:765`、引擎插入点 `:821`）之前 ⇒ **任何来源**（GUI / CLI `--cjxl-photon-noise-iso` / `--cjxl-progressive` / 预设回放）**都必然经过** ✅。
- 判据 `EncoderBackend != Cjxl && Format == "jxl"` 与"FFmpeg libjxl 结构性没有这两个选项"的实测结论一致，且文案给出了出路（"请把后端切到 cjxl"）✅。

### 7.2 【诚实边界】

- 若自动 ISO 读取失败，`ResolveCjxlPhotonNoiseIsoAsync:2023` 会把 `CjxlAutoPhotonNoise` 置回 `false` ⇒ 本次点名不会触发。此场景下用户"勾了"，但连"已放弃"的那句日志在 `:2020` 已出声 ⇒ 不静默。**可接受**，但两条日志分处两个函数，读者需要拼起来看。
- 该点名只覆盖噪声与渐进两项；cjxl 侧还有 `--num_threads` 在 ffmpeg 后端无对应项，未点名 ⇒ 范围外的同类缺口，登记备查。

---

## 8. 本区间其它新增行为的快速核验（非四项承诺，一并记录）

| 项 | 位置 | 判定 |
|---|---|---|
| `HdrImplementationMode` 是 `JpegGainMap` 的强类型视图（不是第二个存储字段） | `Models/FfmpegOptions.cs:337-345`（getter/setter 都落在 `JpegGainMap`） | 【已兑现】确实两向同源 |
| AVIF 增益图（`tmap` 派生项）能力位 | `ColorMapping/ColorTransformPlan.cs:581-590`（`SupportsGainMap = true` + 三路验证依据） | 【已兑现】 |
| `--color-engine legacy` 下 avif + 增益图不再静默降级 | `QueueProcessor.cs:777-788`（条件加入 `avif`） | 【已兑现】 |
| 缩略图能力位按实测赋值 | `Services/FormatCapabilitiesService.cs:231-246`（jpg/png/apng/webp/avif/jxl = true，tiff/gif/jxr/dng = false，未测一律 false） | 【已兑现】诚实原则一致 |
| `RestoreMetadataAsync` 收口 + 缩略图排在色彩后置之前 | `QueueProcessor.cs:1606-1614` | 【已兑现】顺序即不变量，注释已锁 |
| djxl PNM 浮点失败改走 ffmpeg 直读，并出声 | `QueueProcessor.cs:4218-4240` | 【已兑现】换路 + 点名，无静默 |
| 预设克隆补齐缩略图三字段 | `QueueProcessor.cs:4919-4921`、`Models/FfmpegOptions.cs:105-107` | 【已兑现】两处克隆点都在本批补齐 |

---

## 9. 建议的最小修补清单（按优先级）

1. **D-1 / D-2**：`ImageEncoderArgs.cs:70` 的"严格互逆"改为"±1 量化误差内互逆"；并复验 `:56` 的 `q75→2.35` 究竟是官方等价点还是本实现实测点，二者择一改对。给探针补 `q∈{75,85,95}` 的往返断言（`ServiceProbe/Program.cs:3276` 附近）。
2. **D-7 / D-7b / D-7c**：把 `:603` 的中心播报条件改成只排除 `Svt`（或让 libaom `default` 去掉 `IsKnownTuneToken` 门），并把 `"vq"` 补进 `KnownTuneTokens`（`:392`），使"认不出的值"在 libaom / SVT 上也出声。
3. **D-3b**：`ExifToolService.ReadIsoAsync` 补一次 `p.ExitCode != 0` 的出声分支。
4. **G-1**：把 `JxlModularCheck` 从 `JxlFfmpegPanel`（`MainWindow.xaml:632`）提到 `JxlCodecPanel` 层，使 cjxl 后端也能手动置位。
5. **D-4**：把 `"jxl_meta_"` 加入 `PlatformServices` 的临时文件清扫前缀表（并确认 `TempFilePrefixes` 确实被文件级清扫消费）。
6. **D-6**：`MainWindow.xaml.cs:3000` 的 DNG 兼容覆写改用独立字段，解除 `JxlModular` 的双语义。
7. **D-5**：`CjxlService.cs:381` 文案里的 `(pipe/raw stream)` 改为中性，或让 PNG 中转路径只传一次 log。
8. **登记**：把 `tools/src/dngtool/dngtool.cpp:1392` 的旧公式补登记进 `docs/JXL_RAW_16BIT_ADVISORY_2026-10-02.md` 的"未收敛副本"清单（是否换成官方曲线另议）。

---

## 10. 本次复审的方法边界（诚实声明）

- 全程**只读**：未改源码、未 `git add/commit`、未 `dotnet build`。因此所有"实测"结论均来自仓内既有记录（`.workbuddy/memory/2026-10-02.md`、`docs/JXL_RAW_16BIT_ADVISORY_2026-10-02.md`、`tests/output/t*`），本次**没有**独立重跑任何实机验证。
- 可静态证伪的部分（公式逐符、往返数值、分支覆盖、grep 全仓、行号定位）均已逐条自己算过/看过，不采信注释措辞；其中 **D-1 / D-2 / D-7** 三条是纯静态即可证伪的"注释与代码矛盾"。
- 依赖运行时行为的结论（exiftool 的 5 行输出顺序、中转 PNG 是否真的无色彩 chunk、`-x` 是否对 PNG 生效、cjxl/djxl 的产物字节级等价）**本次无法证实或证伪**，已在对应小节标注为诚实边界。
