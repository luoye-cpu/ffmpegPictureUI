# AVIF 动图实现：ffmpeg 参数传递全解析

> **本文性质**：对**当前软件**（`C:\PLAN\ffmpegPictureUI`，v 现状 HEAD 工作区）里「AVIF 动图」这条线的**实现现状**与**参数如何传给 ffmpeg** 的逐行取证记录。
> **行号基准**：本文件所有 `path:line` 取自本次撰写时的**工作区实际内容**（不是记忆、不是旧版本文档）。若后续代码变动，行号会漂移 —— 请以**锚点字符串**（本文每处都给出）为准重新定位。
> **不修改任何代码**：本文是**只读调查**的产物。文中所有「实测」字样均转述自源码注释中登记的既有实测结论；本文作者**本轮没有跑任何构建或测试**（见 §9 覆盖边界）。

---

## 0. TL;DR —— 一句话结论

**本软件的 AVIF 动图能力被切成两条互不相通的通路**：

| 方向 | 谁是执行体 | 参数怎么传 |
|---|---|---|
| **输入**（AVIF 动图 → 别的格式） | **ffmpeg** | 全靠 `ffprobe` 三态探测选路，再用 ffmpeg 跑 **两步法**（分轨提取颜色/alpha → `alphamerge` → 编 `gif`/`webp`） |
| **输出**（GIF → AVIF 动图） | **`avifenc.exe`（libavif），不是 ffmpeg** | 由**C# 手工拼** `avifenc` 命令行；ffmpeg 只负责**提取 RGBA 帧** |
| **输出**（别的动图/多帧源 → AVIF） | **ffmpeg** | `ImageEncoderArgs.BuildAvifOptions()` 按**编码器后端分域**发参数；**AVIF 的动图语义（帧率/循环）本身在 ffmpeg 这条路上是缺的**（见 §4.9 / §4.10） |

⚠ **最关键的一条**：`gif → avif` **不走 ffmpeg 编码**。代码里明确写着「FFmpeg 编码器丢弃 alpha，走 avifenc 两步法保留透明通道」(`QueueProcessor.cs:911`)。因此**讨论「AVIF 动图在 ffmpeg 上的参数传递」时，必须先区分方向** —— 这是最容易搞错的一点。

⚠ **次关键的一条（本文最重要的发现）**：**`--animation-fps` 与 `--animation-loop` 对 AVIF 输出在 ffmpeg 路径上完全不生效，且不出声**（§4.9）。而且**不是做不到**：本仓自己的注释两处登记过 `ffmpeg -h muxer=avif` 自报表里**有 `loop` 这个 AVOption**（`IsoBmffGainMapWriter.cs:13-14`、`GainMapEncoder.cs:551-552`），只是**没有任何代码用它**（§4.10）。avifenc 那条路则把循环**硬编码成 `infinite`**，同样不读 `AnimationLoop`。

---

## 1. 现状速查表：动图 AVIF 的全部入口

### 1.1 分派树（`QueueProcessor.ProcessAsync`）

分派点在 `src\FfmpegGui\Services\QueueProcessor.cs:911-982`。按顺序是**先到先得**的 `if/else if` 链：

```
inputExt == ".gif"  && Format == "avif"   → ProcessGifToAvifViaAvifencAsync   ← 走 avifenc，不走 ffmpeg
inputExt == ".avif" && Format ∈ {gif,webp} → ProcessAvifToGifWebpAsync        ← 走 ffmpeg 两步法
inputExt == ".jxl"                         → ProcessJxlInputAsync
inputExt == ".jxr"                         → ProcessJxrInputAsync
GainMap && Format ∈ {jpg,jpeg,avif} && !engineRoutable → ProcessGainMapJpegAsync
backend == Cjxl   && !engineRoutable       → ProcessCjxlAsync
backend == Cjpegli                         → ProcessCjpegliAsync
backend == Jxr    && !engineRoutable       → ProcessJxrAsync
backend == Dng                             → ProcessDngAsync
else                                       → 色彩引擎 / 传统 ffmpeg 主管线
```

**注意这里有一个重要的不对称**：

* **AVIF 作为输入**：只有目标是 `gif`/`webp` 时才进专用两步法；目标是 `jpg`/`png`/`avif`/`jxl`… 时**落到 else 主管线**，由 §2 的通用「多帧输入 → 单帧容器」逻辑处理。
* **AVIF 作为输出**：**除了 `.gif` 输入**这一格，其余全部走 `else` 主管线，即 `FfmpegCommandBuilder.BuildArguments()` → `case "avif":`。

### 1.2 探测函数清单（决定"这条命令怎么拼"的前置事实）

| 函数 | 位置 | 问什么 | 关键命令 |
|---|---|---|---|
| `IsAnimatedAvifAsync` | `QueueProcessor.cs:5426` | 这个 AVIF 是动图吗？ | `ffprobe -v error -show_entries stream=index,codec_type,nb_frames -of csv=p=0` |
| `ProbeAvifStreamRolesAsync` | `QueueProcessor.cs:5149` | 哪条流是颜色、哪条是 alpha？ | `ffprobe -v error -select_streams v -count_frames -show_entries stream=index,nb_read_frames -of csv=p=0` |
| `InputMayHaveMultipleFramesAsync` | `QueueProcessor.cs:5807` | 本次任务**输入**是否多帧？ | 转发给下面的 `InputHasMultipleFramesAsync` |
| `InputHasMultipleFramesAsync` | `QueueProcessor.cs:5828` | 同上，只看输入文件本身 | `.avif` 分支调 `IsAnimatedAvifAsync` |
| `IsAnimatedWebpAsync`（对照物） | `QueueProcessor.cs:5659` | webp 三态探测 | 两步 ffprobe（`format_name` + `count_packets`） |
| `IsoBmffGainMapProbe.HasTmapItemFast` | `IsoBmffGainMapProbe.cs:118` | 容器里有没有 `tmap` 派生项？ | **不用 ffmpeg**，纯 C# 读 ISO-BMFF 盒 |

---

## 2. 输入方向：AVIF 动图 → 其他格式（ffmpeg 参数传递）

### 2.1 第一步：三态动画探测 `IsAnimatedAvifAsync`

**签名**（`QueueProcessor.cs:5426`，这是门禁脚本的逐字锚点）：

```csharp
private static async Task<(bool animated, bool determinate)> IsAnimatedAvifAsync(string inputPath,
    Action<string>? log = null, CancellationToken ct = default)
```

**传给 ffprobe 的完整参数**（`QueueProcessor.cs:5456`）：

```
ffprobe -v error -show_entries stream=index,codec_type,nb_frames -of csv=p=0 "<inputPath>"
```

| 参数 | 为什么必须这么写 |
|---|---|
| `-v error` | 只留错误，stdout 干净可解析 |
| `-show_entries stream=index,codec_type,nb_frames` | `nb_frames` **必须带**（`QueueProcessor.cs:5454` 注释：判据已从「数流条数」改为「是否真有流带多帧」，缺该字段 ⇒ 全部流按「帧数未知」⇒ 不确定） |
| `-of csv=p=0` | 出 `index,codec_type,nb_frames` 三列逗号行，无表头 |
| **不带** `-select_streams` | 故意的：必须看到**所有**流 |

**判据链（逐条，`QueueProcessor.cs:5504-5537`）**：

```
解析出 videoStreams = [(index, nb_frames), ...]      ← 只看 codec_type == "video"
  ├─ videoStreams.Count < 2                     ⇒ (false, true)   单视频流 ⇒ 确定静态
  ├─ 全部流帧数已知 && maxFrames > 1            ⇒ (true,  true)   确有流多帧 ⇒ 确定动画
  ├─ 有流帧数读不到 (nb_frames <= 0)            ⇒ (false, false)  不确定 ← Indeterminate
  └─ 全为单帧 ⇒ 问容器：
        IsoBmffGainMapProbe.HasTmapItemFast():
          ├─ null（读不到/文件 > 32MB）          ⇒ (false, false)  不确定 ← Indeterminate
          ├─ true（有 tmap = 增益图）            ⇒ (false, true)   确定静态
          └─ false（无 tmap，第二条流是 alpha）  ⇒ (true,  true)   确定「多帧」⇒ 走两步法
```

⚠ **最后一条分支是刻意的**（`QueueProcessor.cs:5501-5502`）：**静态 AVIF + alpha** 与**增益图 AVIF** 的 ffprobe 形态**完全相同**（都是 2 条视频流、`nb_frames` 都是 1）。只有 `tmap` 盒能把两者分开。而「全为单帧 + 无 tmap」时第二流是 **alpha**，两步法本来就是为「分轨提取颜色+alpha」而设 ⇒ 保持旧行为（按多帧处理），改判静态会**丢 alpha**（`alpha_real.avif` 实测）。

**「不确定」的唯一出口**（`QueueProcessor.cs:5431-5435`）：

```csharp
void Indeterminate(string why)
{
    var msg = $"[avif] 动画探测{why} ⇒ 不确定：{Path.GetFileName(inputPath)}（按多帧处理）";
    if (log != null) log(msg + "\n"); else System.Diagnostics.Trace.WriteLine(msg);
}
```

六种「不确定」来源：ffprobe 起不来、被取消、超时、非 0 退出、输出不可解析、多流单帧但容器未决。**每一种都有点名日志**，绝不静默吞掉。

**超时机制**（`QueueProcessor.cs:5393`、`5401-5410`、`5471-5477`）：

* 默认预算 `DefaultAvifProbeTimeoutSec = 20`（秒）
* 环境变量覆盖：`FFMPEGGUI_AVIF_PROBE_TIMEOUT_SEC`，**夹紧到 `[1, 600]`**
* 实现：`CancellationTokenSource.CreateLinkedTokenSource(ct)` + `CancelAfter(TimeSpan.FromSeconds(timeoutSec))` + `Register(() => proc.Kill(entireProcessTree: true))`
* **保持异步**：`await ReadStdoutDrainingStderrAsync(proc)` 再 `await proc.WaitForExitAsync()`，**不用**同步 `.WaitForExit(`（门禁 `$mechOk` 逐字断言这一点）

### 2.2 第三步：`IsAnimatedAvifAsync` 的**三个**消费点（`#136` 纪律）

门禁 `tests\scripts\verify-avif-anim-probe.ps1:270-278` 用**正则逐点**钉死这三处，缺任何一处转红：

| # | 消费点 | 位置 | 逐字断言 |
|---|---|---|---|
| 1 | 预缩放跳过 | `QueueProcessor.cs:799` | `maxDimSkipAnimated = avifProbeAnimated \|\| !avifProbeDeterminate;` |
| 2 | 两步法入口 | `QueueProcessor.cs:3669` | `bool isAnimatedAvif = avifProbeAnimated \|\| !avifProbeDeterminate;` |
| 3 | 引擎路由输入侧子句 | `QueueProcessor.cs:5845` | `return avifProbeAnimated \|\| !avifProbeDeterminate;` |

三处**统一语义**：**探测不确定 ⇒ 保守按「多帧」**。依据是项目既定政策「宁可回退传统管线，也不冒『静默只保留首帧』的风险」(`QueueProcessor.cs:5795-5796`)。

> ⚠ 消费点 1 的 `if (!maxDimSkipAnimated && maxDimInputExt == ".avif")` 分支（`QueueProcessor.cs:794-800`）在「多帧/不确定」时**已被上面短路**，只对**静态确定**的 avif 执行。代码注释明说保留它是为了不打断门禁钉住的「三处消费 determinate」载体 (`:790-793`)。

### 2.3 `inputMayHaveMultipleFrames` 的四条消费链

单点求值于 `QueueProcessor.cs:383`：

```csharp
var inputMayHaveMultipleFrames = await InputMayHaveMultipleFramesAsync(captured, ct);
```

调用顺序（`QueueProcessor.cs:5816-5817`）：

```csharp
if (item.Options.AnimationFps.HasValue) return true;      // 用户显式要动图 ⇒ 多帧意图（短路，不探测）
return await InputHasMultipleFramesAsync(item, ct);       // 否则真探测
```

`.avif` 侧子句（`QueueProcessor.cs:5840-5846`）：

```csharp
if (inputExt == ".avif")
{
    var (avifProbeAnimated, avifProbeDeterminate) = await IsAnimatedAvifAsync(
        item.InputPath, s => { item.Log += s; }, ct);
    return avifProbeAnimated || !avifProbeDeterminate;
}
```

该变量的**四个消费点**（`QueueProcessor.cs:373-382` 注释登记）：

1. `ColorEngineRouter.ShouldRoute(options, outputPath, inputMayHaveMultipleFrames)` —— `:433`
2. GainMap 专用分派判据 —— `:946` / `:1001`
3. `TryRunColorEngineAsync(..., inputMayHaveMultipleFrames, ct)` —— `:987`
4. **预缩放跳过** `maxDimSkipAnimated = inputMayHaveMultipleFrames` —— `:789`

**为什么单点求值**：`.webp`/`.avif` 输入会真跑 ffprobe，实测 **47~83 ms** 量级 (`QueueProcessor.cs:5799`)。四处必须是**同一份**结果，否则就是"路由说能走、分派说不能走"。

**为什么动图输入必须跳过预缩放**（`QueueProcessor.cs:776-778`）：单帧 PNG 中间件会丢掉全部动画帧，且 `inputExt` 仍路由到动图两步法导致错乱（P1-N6）。缩放由各动图管线自身的 `AnimationScaleW` 等参数处理。

### 2.4 引擎路由拦截：动图输入被挡在纯 C# 引擎之外

`ColorEngineRouter.BlockReason`（`ColorEngineRouter.cs:199-211`）：

```csharp
if (inputIsAnimated) return new EngineBlock
{
    Code = EngineBlockCodes.AnimatedInput,
    Text = "输入为动画，引擎按单帧处理会丢帧",
    // 故意不给 Alternatives：本方法拿不到输入路径，无法区分"动画"来自 --animation-fps
    // 还是来自输入文件本身 ⇒ 复刻 IsAnimated 就是第二份口径
};
```

**为什么重要**：引擎是**单帧**处理器。若拦不住 ⇒ **静默丢帧** —— 正是 `#43` 修的那一格（旧实现「输出非 gif/apng 时直接 `return IsAnimated(item)`」，而 `IsAnimated` 对 `.avif` **恒 false** ⇒ 多帧 avif 输入 + 非 gif/apng 目标被判成单帧 ⇒ 引擎按单帧处理 ⇒ 静默丢帧，`QueueProcessor.cs:5781-5783`）。

**连带效应**（`QueueProcessor.cs:441-444`）：请求了色彩引擎但输入是 `.avif` 时，会打一条「本次由…接管」的点名日志。

### 2.5 两步法：AVIF 动图 → GIF/WebP 的**全部 ffmpeg 命令行**

`ProcessAvifToGifWebpAsync`（`QueueProcessor.cs:3656-3777`）。临时目录前缀 `avif2gifwebp_`（`PlatformServices.cs:539` 登记在清理清单）。

#### Step 0：静态回退（不是动图就不走两步法）

`QueueProcessor.cs:3666-3683`。静态 AVIF ⇒ 直接调通用构建器：

```csharp
var ffArgs = BuildArgsAndNote(item, item.Options, item.InputPath, outputPath);
```

避免 `-map 0:2` 找不到轨道而失败。

#### Step 1：分轨提取颜色 + alpha

**先探测流角色**（`QueueProcessor.cs:3696-3698`）：

```csharp
var (colorIdx, alphaIdx) = await ProbeAvifStreamRolesAsync(item.InputPath,
    AppSettingsService.Current.FfmpegPath, s => { item.Log += s; _onItemUpdated?.Invoke(item); }, ct);
```

`ProbeAvifStreamRolesAsync`（`QueueProcessor.cs:5149-5204`）的命令：

```
ffprobe -v error -select_streams v -count_frames -show_entries stream=index,nb_read_frames -of csv=p=0 "<inputPath>"
```

**选择规则**（`QueueProcessor.cs:5198-5200`，可验证、不猜语义）：

```csharp
var color = streams.OrderByDescending(s => s.frames).ThenBy(s => s.idx).First();   // 帧数最多；并列取 index 最小
int? alpha = streams.Where(s => s.idx != color.idx && s.frames == color.frames)   // 帧数**完全相同**才算 alpha
                   .OrderBy(s => s.idx).Select(s => (int?)s.idx).FirstOrDefault();
```

| 规则 | 理由（源码注释） |
|---|---|
| 帧数最多 = 颜色 | 实测本工具链的 avifenc 产物只有两条流（index0=1 帧、index1=10 帧，**两条都是彩色 av1/yuv420p、SATAVG 相同** ⇒ 根本无 alpha 平面）。旧写法写死 `-map 0:2`/`0:3` 直接 `exit=-22` |
| 帧数相同才算 alpha | alpha 平面必须与颜色**逐帧对齐** |
| 探不到任何视频流 ⇒ 回退 `(2, 3)` | `QueueProcessor.cs:5197`、`:5203`（catch 里也是 `(2,3)`）：「绝不因探测失败而改变已正常工作的路径」；对旧 4 流布局（primary/thumb 1 帧 + color 10 帧 + alpha 10 帧）依旧选出 `0:2`/`0:3` ⇒ **向后兼容，零行为变化** |
| `-count_frames` 必须带 | `QueueProcessor.cs:5160`：AVIF 容器里常不写 `nb_frames`，不数帧就分不出主图/首帧副本 |
| 超时 20 s，**同步** `WaitForExit(20000)` | `QueueProcessor.cs:5173`。与异步版 `IsAnimatedAvifAsync` **同机制、但未共用**（`:5575-5577` 说明：形态被门禁逐字锚点钉着，合并属后续切片） |
| 取消接线 | `ct.Register(() => proc.Kill(entireProcessTree: true))`（`:5171`），先到者生效 |

**然后跑两条提取命令**（`QueueProcessor.cs:3703-3706`）：

```csharp
var colorArgs = $"-y -i \"{item.InputPath}\" -map 0:{colorIdx} -fps_mode passthrough -start_number 0 \"{tempDir}\\c_%04d.png\"";
var alphaArgs = alphaIdx is int ai2
    ? $"-y -i \"{item.InputPath}\" -map 0:{ai2} -fps_mode passthrough -start_number 0 \"{tempDir}\\a_%04d.png\""
    : null;
```

| 参数 | 为什么 |
|---|---|
| `-map 0:{N}` | 流号**不得硬编码**（P7e，见上） |
| `-fps_mode passthrough` | 保持原帧时序 |
| `-start_number 0` | ⚠ **序号必须连续**：曾经写 `-frame_pts 1` 让 image2 用 PTS 当序号，一旦某条流 PTS 间隔 >1，写出的就是**带空洞**的序号，而 Step 2 按 `%04d` 顺序读会在第一个空洞处**静默截断**（退出码仍 0、状态「已完成」）⇒ 帧数丢失无人知晓 (`QueueProcessor.cs:3700-3702`) |
| 输出 PNG (`c_%04d.png`) | 无损中间件，保留 alpha |

**alpha 存在性判定**（`QueueProcessor.cs:3712-3721`）：

```csharp
var hasAlpha = Directory.GetFiles(tempDir, "a_*.png").Length > 0;
if (!hasAlpha && alphaArgs != null)   // 第一次没出帧 ⇒ 重试一次（静默）
    await FfmpegRunner.RunAsync(alphaArgs, s => { }, ...);
hasAlpha = Directory.GetFiles(tempDir, "a_*.png").Length > 0;   // 重新求值
```

帧率：`var fps = item.Options.AnimationFps ?? 33;`（`:3723`，默认 **33 fps**，接近原始 GIF）。

#### Step 2（GIF 出口）

`QueueProcessor.cs:3726-3741`：

```csharp
var dither = item.Options.GifDither ? "=dither=bayer:bayer_scale=5:diff_mode=rectangle" : "=dither=none";

// 有 alpha：单 pass 双输入
encodeArgs = $"-y -framerate {fps} -start_number 0 -i \"{tempDir}\\c_%04d.png\" "
           + $"-framerate {fps} -start_number 0 -i \"{tempDir}\\a_%04d.png\" "
           + $"-filter_complex \"[0:v][1:v]alphamerge,split[a][b];"
           + $"[a]palettegen=reserve_transparent=1:stats_mode=full:max_colors=256[p];"
           + $"[b][p]paletteuse{dither}\" -loop 0 \"{outputPath}\"";

// 无 alpha：单输入
encodeArgs = $"-y -framerate {fps} -start_number 0 -i \"{tempDir}\\c_%04d.png\" "
           + $"-vf \"split[a][b];[a]palettegen=stats_mode=full:max_colors=256[p];[b][p]paletteuse{dither}\" "
           + $"-loop 0 \"{outputPath}\"";
```

⚠ **A-13（2026-09-26）**：关抖动要**显式** `dither=none`（`paletteuse` 默认是 `sierra2_4a`，省略 ≠ 关）。与 `ImageEncoderArgs.BuildGifFilterChain` 同口径 —— 这是调色板链的**第二份拷贝**，两处必须一起改 (`QueueProcessor.cs:3728-3730`)。

#### Step 2（WebP 出口）

`QueueProcessor.cs:3742-3756`：

```csharp
var quality = item.Options.Quality;
var loop = item.Options.AnimationLoop;
if (loop < 0) loop = 0;                     // webp 没有"不循环"，-1 折成 0

// 有 alpha
encodeArgs = $"-y -framerate {fps} -start_number 0 -i \"{tempDir}\\c_%04d.png\" "
           + $"-framerate {fps} -start_number 0 -i \"{tempDir}\\a_%04d.png\" "
           + $"-filter_complex \"[0:v][1:v]alphamerge,format=yuva420p\" "
           + $"-c:v libwebp_anim -q:v {quality} -loop {loop} \"{outputPath}\"";

// 无 alpha
encodeArgs = $"-y -framerate {fps} -start_number 0 -i \"{tempDir}\\c_%04d.png\" "
           + $"-c:v libwebp_anim -q:v {quality} -loop {loop} \"{outputPath}\"";
```

#### 收尾

`QueueProcessor.cs:3767-3771`：`ExitCode` + `Status`，成功则 `await RestoreMetadataAsync(item, outputPath)`。`finally` 里删临时目录（`:3773-3776`）。

---

## 3. 输出方向 A：GIF → AVIF 动图（**avifenc**，不是 ffmpeg）

`ProcessGifToAvifViaAvifencAsync`（`QueueProcessor.cs:3780-3929`）。临时目录前缀 `avifenc_frames_`（`PlatformServices.cs:539`）。

### 3.1 为什么不用 ffmpeg 编码

`QueueProcessor.cs:911-914` 逐字：

```
// GIF → AVIF：FFmpeg 编码器丢弃 alpha，走 avifenc 两步法保留透明通道。
// ⚠ 可用性判断**不得写在本条件里**（P7f）：曾经写成 `&& HasAvifencAvailable()`，
//   于是 avifenc 缺失时整个意图会静默落到后面的通用 ffmpeg 分支（上面注释自己写着"丢 alpha"），
//   用户却看到"已完成"⇒ 违反"可用性诚实"。现在意图总是路由到两步法，由方法内部显式失败。
```

⇒ **分派条件里刻意不查 avifenc 是否存在**。意图总是路由到两步法，缺工具由方法内部**显式失败**（`QueueProcessor.cs:3784-3796`）：

```
[gif→avif] 未找到 avifenc.exe ⇒ 显式失败（不静默回退）。
  请任选其一：① 把 avifenc.exe 放入发布包 PLAN/artifacts/；
  ② 在设置里指定 AvifencPath 或 WindowsArtifactsDir；③ 改选其他输出格式。
```

`avifenc` 定位走**全局唯一入口** `PlatformServices.ResolveAvifencPath()`（`PlatformServices.cs:363-371`），四级顺序：手动路径 → `WindowsArtifactsDir` → PLAN 便携文件夹 → ffmpeg 同目录；每级还要求**没被证实起不来**（`IsToolUnlaunchable`）。这是 P7f 的结论：同一件事曾有三份拷贝且口径不一。

### 3.2 Step 1：ffmpeg 提取 RGBA 帧

`QueueProcessor.cs:3808`：

```
ffmpeg -y -i "<inputPath>" -pix_fmt rgba -fps_mode passthrough "<tempDir>\f_%04d.png"
```

| 参数 | 说明 |
|---|---|
| `-pix_fmt rgba` | **强制带 alpha**：GIF 的 pal8 透明索引 → rgba |
| `-fps_mode passthrough` | 不做帧率重采样（帧率交给后面 avifenc 的 `--fps`） |
| 输出 `f_%04d.png` | 按 `%04d` 序号，Step 2 按字典序读 |

失败 ⇒ `item.Status = $"失败 (帧提取退出码 {extractExit})"`，返回。零帧同理 (`:3824-3829`)。

### 3.3 Step 2：手工拼 avifenc 命令行

**参数计算**（`QueueProcessor.cs:3837-3849`）：

```csharp
var fps     = item.Options.AnimationFps ?? 33;                        // 默认 33fps
var quality = Math.Max(20, Math.Min(100, item.Options.Quality));      // avifenc -q 范围 0-100
var speed   = Math.Clamp(item.Options.AvifCpuUsed ?? 5, 0, 10);       // -s 0-10，默认 5
var threads = item.Options.Threads;
if (threads == 1)
{
    item.Log += "[gif→avif]   ⚠ 单线程会触发 avifenc 1.4.2 的段错误（≥4 帧），已提为 -j 2（上游缺陷，非本仓库）\n";
    threads = 2;
}
```

⚠ **P7h（实测）**：`avifenc` 1.4.2 在 `-j 1` 且**帧数 ≥ 4** 时段错误（`0xC0000005` / 退出码 `-1073741819`），与 alpha 平面、`--repetition-count` 均无关；`-j 2/4/8` 或不传 `-j` 全部正常。旧写法 `Math.Max(1, Options.Threads)` 在 `Threads=0`（auto，headless 即此值）下恒为 1 ⇒ **动画 GIF→AVIF 必崩**。

**命令结构**（`QueueProcessor.cs:3851-3862`）：

```csharp
var avifencArgs = new StringBuilder();
var headArgs = $"-q {quality} -s {speed}" + (threads >= 2 ? $" -j {threads}" : "");
avifencArgs.Append(headArgs).Append(' ');
var frameNames = pngFiles.Select(f => Path.GetFileName(f)).ToList();   // ⚠ 相对文件名
avifencArgs.Append(string.Join(' ', frameNames));
avifencArgs.Append(' ');
var outputAbsPath = Path.GetFullPath(outputPath);
var tailArgs = $"-o \"{outputAbsPath}\" --fps {fps} --repetition-count infinite";
avifencArgs.Append(tailArgs);
```

**最终形态**：

```
avifenc.exe -q <quality> -s <speed> [-j <threads>] f_0001.png f_0002.png … f_NNNN.png -o "<abs output>" --fps <fps> --repetition-count infinite
```

| 参数 | 语义 / 依据 |
|---|---|
| `-q <20..100>` | AVIF 质量。`Math.Max(20, Math.Min(100, Quality))` —— 下限 20 是刻意的 |
| `-s <0..10>` | speed presets（CPU 档位），来源 `Options.AvifCpuUsed ?? 5`，`Clamp(0,10)` |
| `-j <N>` | 线程。`Threads <= 0` ⇒ **不传**，由 avifenc 自适应；`Threads == 1` ⇒ 提到 2（规避上游段错误） |
| 帧文件 | **只传相对文件名**，配合 `WorkingDirectory = tempDir` |
| `-o "<绝对路径>"` | 输出用**绝对**路径（避免被工作目录影响） |
| `--fps <N>` | 动图帧率 |
| `--repetition-count infinite` | **无限循环**（硬编码） |

⚠ **P2-4（实测，`QueueProcessor.cs:3854-3856`）**：帧文件**只能传相对文件名**。绝对路径每帧 ~98 字符 ⇒ 约 330 帧就撞满 `CreateProcess` 的 32767 上限（420 帧 GIF 实测 `ErrorStartingProcess`「文件名或扩展名太长」）；相对文件名每帧 11 字符 ⇒ 约 2900 帧。

**命令行长度预检**（`QueueProcessor.cs:3867-3879`）：

```csharp
const int MaxWindowsCmdLine = 32767;
var cmdLineChars = avifencPath.Length + avifencArgs.Length + 3;
if (cmdLineChars > MaxWindowsCmdLine)
{
    // 显式失败 + 处置建议（不静默回退）
    item.Status = "失败 (帧数超出 avifenc 命令行上限)";
    return;
}
```

建议文案：① 减少帧数（裁短 GIF 或降低目标 fps）；② 改输出 GIF/WebP 等其他动画格式。

**进程启动**（`QueueProcessor.cs:3881-3892`）：

```csharp
var psi = new ProcessStartInfo
{
    FileName = avifencPath,
    Arguments = avifencArgs.ToString(),
    WorkingDirectory = tempDir,          // 帧文件按相对文件名传入，必须由这里解析（P2-4）
    RedirectStandardOutput = true,  StandardOutputEncoding = Encoding.UTF8,
    RedirectStandardError  = true,  StandardErrorEncoding  = Encoding.UTF8,
    UseShellExecute = false,
    CreateNoWindow = true
};
```

日志回显做**摘要**（`QueueProcessor.cs:3864-3865`）：整条可能有几十万字节，灌进 `item.Log` 只会把日志冲爆：

```
[gif→avif]   avifenc -q 90 -s 5 -j auto f_0001.png … f_0420.png（共 420 帧） -o "…" --fps 33 --repetition-count infinite
```

收尾（`:3919-3923`）：`Status = "已完成 (avifenc 两步法)"` 或 `"失败 (avifenc 退出码 N)"`，成功则 `RestoreMetadataAsync`。

**图中断处理**（`:3912-3917`）：`OperationCanceledException` ⇒ `process.Kill(true)` + `Status = "已停止"`。

---

## 4. 输出方向 B：其他源 → AVIF（**ffmpeg**，走通用主管线）

### 4.1 完整命令行布局（`FfmpegCommandBuilder.BuildArguments`，`:17`）

函数从 `:21` 起按顺序 append。**对 AVIF 目标**，实际顺序是：

| # | 位置 | 参数 | 条件 / 出处 |
|---|---|---|---|
| 1 | 首 | `-y` | 恒发 (`:22`) |
| 2 | `-i` 前 | `-color_primaries <P>` | `dec.DeclPrimaries` 非空 (`:45-48`) |
| 3 | `-i` 前 | `-color_trc <T>` | `dec.DeclTrc` 非空 (`:49-52`) |
| 4 | `-i` 前 | `-colorspace <M>` | `ToFfmpegInputMatrixName(dec.DeclMatrix)` 白名单非 null (`:64-68`) |
| 5 | `-i` 前 | `-color_range <pc\|tv>` | `ProbeInputColorRange(inputPath)` 非空 (`:72-76`) |
| 6 | — | `-i "<inputPath>"` | 恒发 (`:81-82`) |
| 7 | `-i` 后 | `-sws_flags accurate_rnd+full_chroma_int` | **仅** `isGifToAvif`（`.gif` 输入 + `avif` 目标）(`:78-89`) |
| 8 | | `-c:v <encoder>` | `options.Encoder` 非空 (`:92-96`)。**空则不写** ⇒ ffmpeg 为 `.avif` 自选，实测本仓随包构建选 `libaom-av1` |
| 9 | | `-threads <N>` | 恒发 (`:99-100`) |
| 10 | | 色彩像素链 `-vf <chain>` | `dec.PixelChains` 逐条 `AppendVideoFilter` (`:124`) |
| 11 | | `-pix_fmt <fmt>` | 见 4.2 |
| 12 | | `-color_range <out>` | `isRgbNativeFmt` 为假 且 `fmt != "gif"` (`:557-563`)。AVIF **会**发 |
| 13 | | `-map_metadata …` | 见 4.3 |
| 14 | | `-vf iccgen` / `format=rgb48le` | `dec.RunIccgen` 为真 (`:613-619`) |
| 15 | | `-colorspace <out>` | `!isRgbNativeFmt` 且矩阵名合法且非 `rgb` (`:642-647`) |
| 16 | | 最长边 scale 插入链首 | `EnableMaxDimension && MaxDimension > 0` (`:657-662`) |
| 17 | | **`-frames:v 1`** | 见 4.4 |
| 18 | | `-f apng` | 仅 encoder == `apng` (`:682-690`) |
| 19 | | **`case "avif":`** → `BuildAvifOptions(options)` | `:382-393` |
| 20 | | `-vf format=rgba,format=yuva420p` | **仅** `inputPath` 以 `.gif` 结尾 (`:388-392`) |
| 21 | | `"<outputPath>"` | 恒发 (`:692`) |
| 22 | | `-t <AnimationDuration>` | `AnimationDuration > 0` (`:694-701`) |
| 23 | | `-pix_fmt` + 可选 `-frames:v 1` + 第二 `-i` | alpha 保真分支，**仅** `fmt ∈ {png,apng,tiff,tif,webp}` (`:715-745`) ⇒ **AVIF 不受影响** |

### 4.2 位深与 `-pix_fmt`

**AVIF 的位深上限由编码器决定**（`ImageEncoderArgs.cs:773-778`）：

```csharp
public static int AvifMaxBitDepthForEncoder(string? encoder)
    => ClassifyAvifBackend(encoder) switch
    {
        AvifBackend.Libaom or AvifBackend.Nvenc => 12,
        _ => 10,
    };
```

⚠ **空编码器名 ⇒ 12，不是 10**（任务 #36，`ImageEncoderArgs.cs:746-756`）：空名意味着命令里**不写** `-c:v` ⇒ 由 ffmpeg 挑默认编码器，实测本仓随包构建挑的就是 `libaom-av1`（`Stream #0:0 -> #0:0 (png (native) -> av1 (libaom-av1))`）。两条口径曾在同一个类里相隔 350 行互相矛盾，代价是：CLI/headless **没有**任何入口能写 `options.Encoder` ⇒ 命令行 `--bit-depth 12` 被永久钳到 10，还播报「本工具对该容器实测可交付的上限 = 10bit」——**那句是假的**。

传统管线侧的转发（`FfmpegCommandBuilder.cs:510-511`）：

```csharp
var avifMaxBd = fmt == "avif" ? ColorMapping.ImageEncoderArgs.AvifMaxBitDepth(options) : int.MaxValue;
int ClampAvifBd(int bd) => Math.Min(bd, avifMaxBd);
```

**三支 `-pix_fmt` 生成**（`FfmpegCommandBuilder.cs:516-541`）：

| 支 | 条件 | 值 |
|---|---|---|
| ① | `Chroma` 显式且非 `auto` | `MapPixFmt(Format, Chroma, ClampAvifBd(EffectiveBitDepth()), …)` |
| ② | 无 `BitDepth` 且探测源位深 > 8 | `MapPixFmt(Format, Chroma, ClampAvifBd(min(源位深, capMaxBitDepth)), …)` |
| ③ | 有 `BitDepth` | `MapPixFmt(Format, Chroma, ClampAvifBd(BitDepth.Value), …)` |

`EffectiveBitDepth()`（`:497-503`）：有 `BitDepth` 用它；否则 `min(探测位深>8 ? 探测位深 : 8, capMaxBitDepth)`。

⚠ **AVIF + alpha 会被主动降级为普通 YUV**（`ImageEncoderArgs.cs:865-869`）：

```csharp
// AVIF 有 alpha 输入 → 回退普通 YUV，丢弃 alpha 确保兼容（防止 1027）
if (hasAlpha && fmt == "avif")
{
    // fall through to plain YUV below (alpha dropped)
}
```

理由（`:850-854`）：libsvtav1 仅支持 `yuva420p`；libaom-av1 无 4:2:2 alpha 支持；且「修复：AVIF 有 alpha 输入 → 回退普通 YUV（避免 libzimg 奇数尺寸 1027 错误）」。**这正是 GIF→AVIF 不走 ffmpeg 的结构性原因**（§3.1）。

最终 YUV 像素格式（`:873-884`）：`bd <= 8` 时按 `Chroma` 给 `yuv420p`/`yuv422p`/`yuv444p`；> 8 时给对应的 `*le` 变体。

### 4.3 元数据映射（AVIF 属「CICP 格式」）

`FfmpegCommandBuilder.cs:573`：

```csharp
bool isCicpFormat = fmt is "avif" or "jxl" or "png" or "apng";
```

⇒ AVIF 落在 `stripAllMeta && isCicpFormat` 支（`:583-593`），发出的正是：

```
-map_metadata 0
-map_metadata:s:v -1        ← 移除数据流映射（避免复制 ICC Profile）
-map_chapters -1
```

即**保留 CICP 色彩元数据、剥离流元数据**。

### 4.4 多帧容器：AVIF **不会**被插 `-frames:v 1`

`FfmpegCommandBuilder.cs:140-142`：

```csharp
bool targetHoldsMultipleFrames = fmt is "gif" or "webp" or "avif" or "apng"
    || (fmt == "jxl" && options.AnimationFps.HasValue);
bool insertFramesV1 = !targetHoldsMultipleFrames; // 目标容器不支持多帧时强制首帧
```

**判据只看目标容器能不能装多帧，与输入扩展名无关**（`:127-139`）。背景：多帧输入写单帧容器时 `image2` 封装器报 `Cannot write more than one file with the same name` ⇒ `Invalid argument` ⇒ 整条任务失败、零产物。AVIF 是多帧容器 ⇒ **不加** `-frames:v 1` ⇒ 保留全部帧。

⚠ 2026-09-18 修：旧判据是 `inputPath.EndsWith(".gif")` ⇒ 只保护 `.gif` 输入，`.webp`/`.apng` 动图写静态目标必失败（实测 `exit=1`、0 产物）。改成只看目标容器。

⚠ 对**单帧输入**加 `-frames:v 1` 是 no-op（实测产物逐像素不变，`verify-anim-static-target.ps1` 的单帧不回归格用 `PSNR=average:inf` 锁，`:135-136`）。

⚠ `jxl + AnimationFps` 必须**排除**（`:137-138`）：`libjxl_anim` 能装多帧，若也插 `-frames:v 1` 会把动图**静默截成 1 帧**（变异实测 M2 复现：`exit=0` 且 1 帧）。

### 4.5 GIF → AVIF 在 ffmpeg 路径上的遗留参数

`FfmpegCommandBuilder.cs:78-89`：

```csharp
var isGifToAvif = inputPath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
               && options.Format.Equals("avif", StringComparison.OrdinalIgnoreCase);
...
if (isGifToAvif)
{
    args.Add("-sws_flags");
    args.Add("accurate_rnd+full_chroma_int");
}
```

以及 `:387-392`：

```csharp
// GIF → AVIF：两步滤镜链 —— pal8→rgba（保留透明索引→alpha）+ rgba→yuva420p（编码器格式）
if (inputPath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
{
    args.Add("-vf");
    args.Add("format=rgba,format=yuva420p");
}
```

⚠ **这两处是历史遗留**：按 §1.1 的分派，`.gif` + `avif` 目标**总是**路由到 `ProcessGifToAvifViaAvifencAsync`（`QueueProcessor.cs:915-919`），永远到不了这里。保留它们的是**别的调用方**——例如 `CloneOptionsForFfmpeg`（`QueueProcessor.cs:5210-5218`）把 `avif` 目标映射成 `libsvtav1` 的动图回退路径。**读代码时不要误以为 GIF→AVIF 走 ffmpeg**。

### 4.6 `BuildAvifOptions`：按后端分域的参数表

**唯一真值** `ImageEncoderArgs.BuildAvifOptions`（`ImageEncoderArgs.cs:462-739`），传统管线 `case "avif"`（`:386`）与自研引擎出口（`ImageEncoderArgs.cs:163-165`）**共用同一份**（2026-09-17 从传统管线整体搬移）。

**后端分类** `ClassifyAvifBackend`（`:352-362`）：

```
"" 或 "libaom*"  → Libaom      ← ⚠ 空串按 Libaom 处理（有意的分歧）
"libsvt*"        → Svt
"av1_nvenc*"     → Nvenc
"av1_qsv*"       → Qsv
"av1_amf*"       → Amf
"av1_vaapi*"     → Vaapi
其余（librav1e…）→ Other
```

**质量轴统一映射**（`:46`）：

```csharp
public static int MapAvifCrf(int quality) => (int)Math.Round((100 - quality) * 63.0 / 100.0);
```

**质量参数逐后端**（`:468`、`:475-523`）：

```csharp
var crf = o.Lossless ? 0 : MapAvifCrf(o.Quality);
```

| 后端 | 发的参数 | 域 / 依据 |
|---|---|---|
| `Nvenc` | `-cq <1..63>` | 实测单调（cq 1/10/20/45/63 = 27907/23815/17032/5429/1542 B）。**0 是 auto 不是最好档** ⇒ 下限取 1。旧写法发 `-crf` ⇒ ffmpeg 报 `has not been used`，滑块**完全无效** |
| `Qsv` | `-global_quality <1..51>` | 自报表**无** crf/cq/qp；常量质量走通用 `AVCodecContext.global_quality`。上限压 51 落在各档共同域内 |
| `Amf` | `-rc cqp -qp_i <q> -qp_p <q>` | QP 是 **qindex 域**（-1..255），按 libaom `quantizer_to_qindex[]` 表换算 |
| `Vaapi` | `-global_quality <1..63>` | ⚠ 随包 ffmpeg **不含 av1_vaapi** ⇒ 无实测依据 |
| 默认（Libaom/Svt/Other） | `-crf <0..63>` | libaom 域 [-1..63]、libsvtav1 [0..63]。SVT 实测日志回显 `rate factor : CRF / 30.00` ⇒ 真生效 |

**tune**（`:527-595`）：显示串先过 `NormalizeTuneToken`（`:378-389`，语言无关 —— 旧代码拿中文字面量当键，英文界面下整条 IQ 静默失效）。

| 后端 | 处理 |
|---|---|
| `Libaom` | `psnr`/`ssim` ⇒ `-tune <tok>`；`iq` ⇒ `-usage allintra -aom-params tune=iq`；`vmaf` ⇒ **点名不出声**（本构建 libaom 不带 VMAF，发出去必崩 rc=127）；其余已知 token ⇒ 点名 |
| `Svt` | 唯一一个 `-svtav1-params tune=<N>` dict。映射 `psnr→1 / ssim→2 / vmaf→5 / ms_ssim→4 / vq→0`；`iq`(3) ⇒ 不发并点名（`Tune IQ only supports all-intra and low delay`，GUI 无 all-intra 通路） |
| 其他后端 | 任何已选 tune **都点名不发**（`:603-607`）—— 这个组合面板点不出来但 CLI/预设回放能到 |

⚠ **SVT 的 dict 必须合并成一次出现**（`:469-472`）：实测重复出现的 `-svtav1-params` 是**整体替换**而非合并（分两次发 `tune=5` 与 `lossless=1` 的产物，与只发 `lossless=1` **逐字节相同** `e7816f68`；合并成 `tune=5:lossless=1` 是另一个产物 `dff9419a`）⇒ 多个键必须用 `:` 连在同一次出现里。实现：`svtParams` 列表最后 `args.Add("-svtav1-params"); args.Add(string.Join(":", svtParams));`（`:643-644`）。

**libaom 私有项**（`:608-630`、`:648-666`）：

```csharp
if (backend == AvifBackend.Libaom && o.AvifCpuUsed.HasValue)
{ args.Add("-cpu-used"); args.Add(o.AvifCpuUsed.Value.ToString()); }        // 域 0..8

if (backend == AvifBackend.Libaom)
{
    var isAnimated = o.AnimationFps.HasValue || o.AnimationLoop >= 0 || o.AvifStillPicture == false;
    if (isAnimated)                       { args.Add("-still-picture"); args.Add("0"); }
    else if (o.AvifStillPicture == true)  { args.Add("-still-picture"); args.Add("1"); }
    if (o.AvifRowMt == true)              { args.Add("-row-mt"); args.Add("1"); }
}
```

⚠ **这里藏着 AVIF 动图在 ffmpeg 路径上最接近「动图语义」的一处**，而且它曾经是**恒真的死旋钮**（`:616-620`）：旧判据含 `o.AnimationLoop != 0`，而**静帧任务**里 `MainWindow` 给 `AnimationLoop` 的就是默认值 `-1`（`-1 != 0` **恒真**）⇒ `isAnimated` 恒真 ⇒ 面板上默认勾选的静帧复选框只能发出 `-still-picture 0`，`-still-picture 1` 这一支**不可达**。现按字面语义判动图：给了 fps、或循环计数有效、或用户显式取消静帧。

**aom 高级项**（`:648-666`，全 libaom 私有）：`-aq-mode <v>`、`-enable-cdef 0|1`、`-enable-intrabc 0|1`、`-denoise-noise-level <N>`（仅 `> 0` 时发）。

**SVT 速度轴**（`:631-638`、`:639-644`）：`-preset <AvifSvtPreset>`（域 -2..13，实测 14 报 out of range）；`lossless` 时 `svtParams.Add("lossless=1")`（A-12：SVT 的 `crf 0` **不是**无损，实测 45.89dB；真无损要 `lossless=1` ⇒ PSNR=inf）。

**硬件具名档位**（`:667-693`，`AvifHwPresetLevel` 夹紧 1..7）：

| 后端 | 参数 | 具名值依据 |
|---|---|---|
| `Nvenc` | `-preset p<1..7>` | 自报 `p1..p7`（实测 `veryslow` 报 Unable to parse） |
| `Qsv` | `-preset <veryfast…veryslow>` | 7 档数组按 `hwLevel-1` 索引（实测 `p7` 被拒） |
| `Amf` | `-quality <speed\|balanced\|quality>` | `hwLevel<=2→speed`、`<=5→balanced`、否则 `quality`（实测 `banana` 被拒） |
| `Vaapi` | `-compression_level <N>` | AVCodecContext 通用项，ffmpeg 层不会越界 |

**NVENC 空间 AQ**（`:700-729`）：

```csharp
if (o.AvifNvencSpatialAq == true)       { args.Add("-spatial-aq"); args.Add("1"); }
else if (o.AvifNvencSpatialAq == false) { args.Add("-spatial-aq"); args.Add("0"); }

var aqStrength = o.AvifNvencAqStrength;
if (aqStrength.HasValue && aqStrength.Value > 0)
{
    if (o.AvifNvencSpatialAq == true)
    { args.Add("-aq-strength"); args.Add(Math.Clamp(aqStrength.Value, 1, 15).ToString()); }
    else
        Console.WriteLine("[avif/av1_nvenc] -aq-strength dropped because spatial AQ is off: …");
}
```

依据：三向对照实测（固定 `-cq 20`）**省略 == `-spatial-aq 0`**（同哈希 `21eed557` / 17032 B），只有传 1 才变（`6d0b6211` / 16636 B）⇒ NVENC 侧默认本就是 0 ⇒ 勾选**必须显式发 1**。`-aq-strength` 域 1..15（实测 0 与 16 都是 out of range、rc=127）⇒ 0 的语义是**不设**；且官方语义「仅当 Spatial AQ 开启该字段才参与」（实测不开时传 5、传 8 与省略三者同哈希）⇒ 未开时**不发并出声**。

**低功耗**（`:730-737`）：`-low_power 0|1`，**只**发给 Qsv 与 Vaapi（四支自报表里只有 `av1_qsv` 有这一项）。

### 4.7 CLI 侧：AVIF 相关的全部开关

`CliParser.cs` 的 `ApplyExtraOption`（`:708-968`）。**AVIF 专属开关只有 4 个**（`:862-865`）：

```
--avif-cpu-used <N>        → Options.AvifCpuUsed          (int?,  ParseIntStrict)
--avif-still-picture <b>   → Options.AvifStillPicture     (bool?, ParseBoolStrict)
--avif-row-mt <b>          → Options.AvifRowMt            (bool?, ParseBoolStrict)
--avif-tune <s>            → Options.AvifTune             (string?, 原样收，不校验)
```

动图相关（`:928-933`）：

```
--animation-fps <N>        → Options.AnimationFps          (int?,  ParseIntStrict)
--animation-loop <N>       → Options.AnimationLoop         (0=无限, -1=不循环, >0=次数)
--animation-scale-w <N>    → Options.AnimationScaleW
--animation-duration <S>   → Options.AnimationDuration     (ParseDoubleStrict)
```

通用（`:178-228`、`:715-748`）：

```
-f, --format <fmt>         → Format（jpegli 归一为 jpg）
-e, --encoder <enc>        → EncoderBackend（**枚举，不是 options.Encoder 编码器名**）
-q, --quality <1-100>      → Quality，Math.Clamp(…, 1, 100)
-j, --jobs <N>             → Concurrency
--chroma <auto|4:4:4|4:2:2|4:2:0>
--bit-depth <auto|8|10|12|16>
```

🔴 **以下字段没有任何 CLI 入口**（搜索 `avif`、`svt`、`hw-preset`、`aq-mode`、`aq-strength`、`spatial-aq`、`cdef`、`intrabc`、`denoise`、`low-power` 全部落空）：

`AvifSvtPreset`、`AvifSvtTune`、`AvifHwPreset`、`AvifHwPresetLevel`、`AvifAqMode`、`AvifEnableCdef`、`AvifEnableIntrabc`、`AvifDenoiseLevel`、`AvifNvencAqStrength`、`AvifNvencSpatialAq`、`AvifLowPower`

⇒ 这些**只能靠 GUI 面板或预设回放**设置。

⚠ 三条 CLI 行为纪律：

1. `--animation-loop -1` 的空格形式**能正确消费**（D4 修复，`:929-931`：`LooksLikeOption` 已处理 `-1`）。
2. **未知 `--key` 只告警、不改退出码**（`:960-967`）⇒ 拼错选项的脚本**看不出来**。
3. **非法取值抛 `ArgumentException`**（`:970-978`）⇒ CLI 退出码 2，与上一条口径不同。
4. ⚠ `-e/--encoder` 映射到 **`EncoderBackend` 枚举**（`:194-206`），**不是** `FfmpegOptions.Encoder`（编码器名字符串）⇒ 这印证了 §4.2 的结论：**CLI/headless 没有任何入口能写 `options.Encoder`**，所以空编码器名 ⇒ `AvifMaxBitDepthForEncoder` 恒返回 12。

### 4.8 模型默认值（`FfmpegOptions.cs`）

```csharp
public int? AnimationFps { get; set; }              // :621  null = 不指定（默认）
public int  AnimationLoop { get; set; } = -1;       // :623  0=无限, -1=不循环, >0=次数
public bool GifPaletteOptimize { get; set; } = true;   // :625
public bool GifDither { get; set; } = true;            // :627
public int  AnimationScaleW { get; set; } = 0;         // :629  0 = 保持原始
public double AnimationDuration { get; set; } = 0;     // :631  0 = 不限制
public int  AvifHwPresetLevel { get; set; } = 4;       // :417
```

### 4.9 🔴 **关键缺口：AVIF 输出在 ffmpeg 路径上不发任何动图参数**

这是本文最重要的**发现**，请特别注意：

在 `BuildAvifOptions`（`ImageEncoderArgs.cs:462-739`）整段**739 行内**，**没有任何一处**发出：

* `-loop` / `-plays`（循环次数）
* `fps=` 滤镜（帧率）
* 任何 AVIF muxer 层面的动图时长参数

**对照**同文件里别的格式都发了：

| 格式 | 循环参数 | 出处 |
|---|---|---|
| `gif` | `-loop <AnimationLoop>`（`!= -1` 时） | `ImageEncoderArgs.cs:231` |
| `apng` | `-plays <AnimationLoop>`（`>= 0` 时） | `FfmpegCommandBuilder.cs:378-379` |
| `webp` | `fps=` 滤镜（`AnimationFps.HasValue` 时） | `FfmpegCommandBuilder.cs:289-293` |
| `jxl` | `libjxl_anim` + `fps=` 滤镜 | `FfmpegCommandBuilder.cs:420-433` |
| **`avif`** | **无** | — |

⇒ **在 ffmpeg 这条通路上**：`Options.AnimationFps` 与 `Options.AnimationLoop` 对 AVIF **输出毫无作用**。唯一被读到的地方是 `BuildAvifOptions:623` 的 `isAnimated` 判断 —— 而它只决定 `-still-picture 0/1` 与 `-row-mt`（且**仅 libaom 后端**）。

**AVIF 的真动图输出（带 `--fps` 和 `--repetition-count infinite`）目前只由 `avifenc` 那条路（§3）实现**，且**只对 `.gif` 输入开放**。

⚠ **本文不判定这是"缺陷"还是"有意的能力边界"**。源码中**没有**任何注释承诺或解释 AVIF 输出的帧率/循环参数；`docs\CHANGELOG.md` 的动图条目也只提「GIF → AVIF 两步编码（保留 alpha）」。⇒ **登记为待确认项**，见 §9。

### 4.10 🔴 缺口的具体形状：avif muxer **有** `loop` 选项，但代码从不用它

这条让 §4.9 的缺口从「不知道能不能做」升级为「**能做而没做**」—— 证据在本仓自己的注释里，且**两处独立记载**：

`src\FfmpegGui\Services\IsoBmffGainMapWriter.cs:13-14`：

```
/// 而 ffmpeg 的 `avif` muxer **不提供**增益图授权
/// （实测 `ffmpeg -h muxer=avif` 只有 `movie_timescale` / `loop`），libavif CLI 的增益图选项也被
/// 编译期开关门控 ⇒ **只能自己组装容器**。本类即该组装器。
```

`src\FfmpegGui\Services\GainMapEncoder.cs:551-552`（同款记载）：

```
/// `movie_timescale` / `loop`），libavif CLI 的增益图选项又被编译期开关门控 ⇒ 没有可用的现成命令。
```

⇒ **`ffmpeg -h muxer=avif` 自报表里有 `loop` 这个 AVOption**（本仓在两个不相干的模块里各自实测登记过），但**本仓没有任何一处给 avif 目标发 `-loop`**。

**对照表：`AnimationLoop` 在各动图容器上的兑现情况**

| 目标容器 | 发的参数 | 出处 | 兑现？ |
|---|---|---|---|
| `gif` | `-loop <N>`（`AnimationLoop != -1` 时） | `ImageEncoderArgs.cs:231` | ✅ |
| `webp` | `-loop <N>`（`AnimationLoop >= 0` 时） | `FfmpegCommandBuilder.cs:274-275` | ✅ |
| `apng` | `-plays <N>`（`AnimationLoop >= 0` 时） | `FfmpegCommandBuilder.cs:378-379` | ✅ |
| **`avif`（ffmpeg 路）** | **无** | — | ❌ **静默忽略** |
| **`avif`（avifenc 路）** | `--repetition-count infinite`（**硬编码**，不读 `AnimationLoop`） | `QueueProcessor.cs:3861` | ❌ **恒定无限循环** |

⚠ **两处"忽略"的形态不同**：
* ffmpeg 路：**不发参数** ⇒ 交 ffmpeg/编码器默认行为（未验）。
* avifenc 路：**发死了 `infinite`** ⇒ 用户即使把 `--animation-loop 3` 设成有限次，产物仍是**无限循环**，且日志里没有任何点名。

⚠ 按本仓「不可用/不生效必须出声」的既定纪律，这两处**都没有出声**。⇒ 见 §7 的 R12。

### 4.11 动图意图在**引擎出口**上的处理（与 legacy 略有不同）

引擎出口走 `RawColorPipeline.cs:987-998`（`ext == "avif"`），2026-09-17 接入：

```csharp
if (ext == "avif")
{
    if (!string.IsNullOrWhiteSpace(o.Encoder)) codec = $" -c:v {o.Encoder}";
    bool chromaSet = !string.IsNullOrWhiteSpace(o.Chroma)
                     && !o.Chroma.Equals("auto", StringComparison.OrdinalIgnoreCase);
    var srcBd = FfmpegCommandBuilder.ProbeInputBitDepthForEngine(inputPath);
    if (chromaSet || o.BitDepth.HasValue || srcBd > 8)
    {
        var bd = ImageEncoderArgs.AvifEffectiveBitDepth(o, srcBd);
        pixFmt = " -pix_fmt " + ImageEncoderArgs.MapPixFmt("avif", o.Chroma, bd, false, o.ColorRange);
    }
}
```

**两条与 legacy 对齐的实测依据**（`RawColorPipeline.cs:975-986`）：

| # | 事实 | 依据 |
|---|---|---|
| ① | 编码器选择策略一致（有则写 `-c:v`、无则**不写**） | 实测 avif muxer 的默认视频编码器是 `av1`，ffmpeg 落到 `libaom-av1`，且产物与显式 `-c:v libaom-av1` **逐字节相同**（641B / `C7F59F91…`） |
| ② | `-pix_fmt` 判据一致：色度显式 ‖ 位深显式 ‖ 源位深 > 8 ⇒ 才写 | ⚠ **不能无条件写**：不写时 libaom 自选 **`gbrp`**（RGB 4:4:4 全采样），引擎若擅自写 `yuv420p` 会把 4:4:4 降成 4:2:0 —— **5153B vs 10808B、PSNR 41.7dB，属静默质量回归** |

⚠ **引擎是单帧处理器**，动图 AVIF 输入由 `AnimatedInput` 拦在门外（§2.4）⇒ 这条出口只服务静帧。**动图 AVIF 输出不经引擎**。

⚠ **但 `CloneOptionsForFfmpeg` 的动图回退会强制静帧标志**（`QueueProcessor.cs:5255-5256`）：

```csharp
// AVIF 动图强制 still-picture=0
AvifStillPicture = false,
```

这条让 `BuildAvifOptions:623` 的 `isAnimated` 判真 ⇒ 发出 `-still-picture 0`。⇒ **这是动图意图在 ffmpeg 路径上被兑现的"唯一另一半"**（另一半是帧保留，见 §4.4）。注意它只对 **libaom 后端**有效：nvenc/qsv/amf/vaapi 上这个字段根本不发（§4.6）。

---

## 5. 端到端对照：同一素材的三条真实命令行

### 5.1 GIF → 动图 AVIF（走 avifenc）

```
# Step 1（ffmpeg）
ffmpeg -y -i "anim.gif" -pix_fmt rgba -fps_mode passthrough "…\avifenc_frames_<guid>\f_%04d.png"

# Step 2（avifenc，WorkingDirectory = 上面的 tempDir）
avifenc.exe -q 90 -s 5 -j 8 f_0001.png f_0002.png … f_0420.png -o "C:\out\anim.avif" --fps 33 --repetition-count infinite
```

### 5.2 动图 AVIF → 动图 WebP（走 ffmpeg 两步法）

```
# Step 0 探测
ffprobe -v error -show_entries stream=index,codec_type,nb_frames -of csv=p=0 "anim.avif"

# Step 1a 选流角色
ffprobe -v error -select_streams v -count_frames -show_entries stream=index,nb_read_frames -of csv=p=0 "anim.avif"

# Step 1b 分轨提取（假设 color=0:1，无 alpha）
ffmpeg -y -i "anim.avif" -map 0:1 -fps_mode passthrough -start_number 0 "…\avif2gifwebp_<guid>\c_%04d.png"

# Step 2 编码
ffmpeg -y -framerate 33 -start_number 0 -i "…\c_%04d.png" -c:v libwebp_anim -q:v 90 -loop 0 "C:\out\anim.webp"
```

### 5.3 静态 PNG → 静态 AVIF（通用主管线，libaom）

```
ffmpeg -y -i "src.png" -threads 8 -pix_fmt yuv420p -color_range pc -map_metadata 0 -map_metadata:s:v -1 -map_chapters -1 \
  -crf 6 -still-picture 1 -row-mt 1 -aq-mode variance -enable-cdef 1 -enable-intrabc 1 "C:\out\src.avif"
```

### 5.4 动图 GIF → 动图 AVIF **在 ffmpeg 路径上**（aborted / 不是默认行为）

```
ffmpeg -y -i "anim.gif" -sws_flags accurate_rnd+full_chroma_int -threads 8 \
  -pix_fmt yuv420p -color_range pc -map_metadata 0 -map_metadata:s:v -1 -map_chapters -1 \
  -crf 6 -still-picture 0 -row-mt 1 -vf "format=rgba,format=yuva420p" "C:\out\anim.avif"
```

⚠ 注意 `-pix_fmt yuv420p`（**无** `a`）与 `-vf format=…yuva420p` 并存 —— 前者是 §4.2 的 AVIF alpha 降级，后者是 §4.5 的遗留链。**两者语义冲突**，且这条命令**按当前分派不可达**（`.gif` + `avif` 恒走 avifenc）。仅作代码考古记录。

### 5.5 参数传递「有/无」总表

| 用户设置 | CLI 开关 | AVIF 输出（ffmpeg 路） | AVIF 输出（avifenc 路） |
|---|---|---|---|
| 质量 1-100 | `-q` | ✅ 按后端折成 crf/cq/qp/global_quality | ✅ `-q`（夹到 20..100） |
| 无损 | `--lossless` | ✅ crf 0（SVT 另发 `lossless=1`；NVENC 出声说不支持） | ❌ 无对应参数 |
| 位深 | `--bit-depth` | ✅ 按编码器上限钳制 | ❌ 无对应参数 |
| 色度采样 | `--chroma` | ✅ `-pix_fmt`（libsvtav1 444/422 会被引擎拒绝） | ❌ |
| 编码器 | `-e` / GUI 下拉 | ✅ `-c:v` + 后端私有项 | ❌ 恒用 avifenc 内置 |
| CPU/speed | `--avif-cpu-used` | ✅ `-cpu-used`（仅 libaom） | ✅ `-s`（夹 0..10，默认 5） |
| 线程 | `--threads` | ✅ `-threads` | ✅ `-j`（1⇒提 2；≤0⇒不发） |
| **帧率** | `--animation-fps` | 🔴 **不发任何参数**（只影响 libaom 的 `-still-picture`） | ✅ `--fps`（默认 33） |
| **循环** | `--animation-loop` | 🔴 **不发任何参数**（ffmpeg 路的 no-op；muxer 的 `loop` 存在但未用，见 §4.10） | 🔴 **硬编码** `--repetition-count infinite`，**不读** `AnimationLoop` |
| 动图缩放宽 | `--animation-scale-w` | ❌ BuildAvifOptions 不读它 | ❌ 不读 |
| 动图时长 | `--animation-duration` | ✅ `-t <S>`（`:694-701`，通用出口） | ❌ |
| AVIF tune | `--avif-tune` | ✅ 按后端（libaom/SVT 各一套；其余点名） | ❌ |
| AVIF aq/cdef/intrabc/denoise | `--avif-*` | ✅ 仅 libaom | ❌ |
| 硬件档位 | GUI 7 档 | ✅ 按后端映射 | ❌ |
| 色彩/ICC | `--color-*` | ✅ 完整 CICP + ICC 链路 | ⚠ 仅 ffmpeg 提取帧时；avifenc 侧无 CICP 参数 |

---

## 6. 门禁与回归锁

### 6.1 `tests\scripts\verify-avif-anim-probe.ps1`（19 条断言）

**要锁的缺陷**（`:3-8`）：`IsAnimatedAvifAsync` 原本把「不知道」当成「不是」—— 四条失败路径全部 `return false`，且**没有任何超时** ⇒ 动画 AVIF + 非 gif/apng 目标时，一次探测抖动就把「不知道」固化成「静态」⇒ 引擎按单帧处理 ⇒ **静默丢帧**。

**判据覆盖**：

| 段 | 内容 |
|---|---|
| ① 素材自检 2 条 | `still.avif` 恰好 1 条 video 流；`trunc.avif` 32 B 且 ffprobe **非 0 退出**（证明「不确定」触发器真是 ffprobe 失败，否则断言真空绿） |
| ① 源码形态 5 条 | 默认 20 s / 注入通道与夹紧 / 异步 + `CancelAfter` + `Register` + `Kill(entireProcessTree: true)` / **点名三处**消费第三态 / **反控**证明点名式载体有牙 |
| ② 行为 8 条（**双向**） | 静态 AVIF ⇒ 确定 ⇒ 走单命令 + **不**打不确定行（不误伤）；截断 AVIF ⇒ 不确定 ⇒ **点名**日志 + 走两步法 + **不**出现「输入为静态 AVIF」+ 零产物（有牙） |
| ③ 注入值不误伤 1 条 | `FFMPEGGUI_AVIF_PROBE_TIMEOUT_SEC=1` 下静态 AVIF 仍走单命令 |
| ④ 被测 exe mtime 一致 1 条 | 跑中途被重建则本轮读数作废 |
| ⑥ 残留自证 1 条 | 绿时清空运行目录 |
| ⑦ 真退出码 1 条 | **单独** `-Wait -PassThru` 跑一次取码 |

**源码形态锚点**（`:237`）—— 改动时必须同步更新这条锚点：

```powershell
private static async Task<(bool animated, bool determinate)> IsAnimatedAvifAsync(
```

**三处消费点的逐字正则**（`:270-274`）见 §2.2。⚠ **不得放宽成 `-ge`**（`:269`）。

**已登记的覆盖缺口**（`:40-47`，措辞不得大于扫描面）：

> **超时分支本身没有被行为实测。** 没能构造出让 `ffprobe -show_entries stream=index,codec_type` 在 AVIF 上**挂住**的素材：1 B / 截断 AVIF ⇒ ffprobe 秒退（exit 1，`moov atom not found`）⇒ 走「异常退出」分支；`mkfifo` 建出的 FIFO 在 .NET 侧 `Test-Path` 为 False；「造假 ffprobe.exe」需自备 PE 二进制。⇒ 本门禁对「超时可达 + Kill 生效」只给**源码形态自检**，**不**声称「已实测超时被触发」。

### 6.2 宿主陷阱（`:34-38`、`:55-60`）—— 写/改门禁时必读

* Windows PowerShell 5.1 下 `Start-Process -PassThru`（**不带 `-Wait`**）的 `.ExitCode` **恒为 `$null`**，而 `[int]$null == 0` ⇒ 写成 `$r.code -eq 0` 会**假绿**、`code -ne 0` 会**假红**。`WaitForExit()` 与 `Refresh()` **都救不回来**。
* **禁止 `Start-Process pwsh`**（宿主安全策略）。
* **杀树禁用 `$p.Kill($true)`**：该重载在 PS 5.1（.NET Framework）上**不存在** ⇒ 异常被吞、应用没被杀、宿主因重定向流收尾而**永久挂住**。用脚本内的 `KillTree`（先子后父）。
* 结尾必须**完整 if/else**：清理步会污染 `$LASTEXITCODE`。
* 双宿主兼容（PS 7 / PS 5.1）：禁三元 `? :` / `??` / `?.` / `&&` / `||`。

### 6.3 相关门禁

| 脚本 | 关系 |
|---|---|
| `tests\scripts\verify-gif-avif-framelist.ps1` | GIF→AVIF 帧列表（对应 §3.3 的 P2-4 命令行长度问题） |
| `tests\scripts\verify-anim-static-target.ps1` | §4.4 的「多帧输入写单帧容器」不回归格（`PSNR=average:inf` 锁） |
| `tests\scripts\verify-decision-delivery.ps1` ⑯ | §4.2 的 AVIF 位深上限（现场解析 `-h encoder=` 自报表，只对「不写 `-c:v` 时的默认编码器」与 `libsvtav1` 两行下判断） |
| `tests\scripts\verify-color-strategy.ps1` | 动图路由回归锁（`map-gif-*` 三格，`QueueProcessor.cs:5815` 提到「只有整轮能抓到」） |

---

## 7. 已知风险 / 脆弱点清单

| # | 位置 | 风险 |
|---|---|---|
| R1 | §4.9 | **AVIF 输出在 ffmpeg 路径上不发任何动图参数**（帧率/循环无效）。真动图输出只由 avifenc 路（仅 `.gif` 输入）实现 |
| R2 | `QueueProcessor.cs:3846` | `avifenc` 1.4.2 在 `-j 1` + ≥4 帧时**段错误**（上游缺陷）。已规避为 `-j 2`，但**不是修复** |
| R3 | `QueueProcessor.cs:3854-3856` | avifenc 帧列表走命令行 ⇒ 帧数上限约 2900（相对文件名）。超限显式失败 |
| R4 | `ImageEncoderArgs.cs:766-770` | `av1_nvenc ⇒ 12bit` **本轮没有被端到端验过**，且已知可疑：nvenc 自报表里**没有** `yuv420p12le`（只有 `p012le`/`p212le`/`yuv444p12msble`）⇒ 12bit + nvenc 大概率被编码器拒收。本机无 NVIDIA GPU ⇒ 登记待办 |
| R5 | `ImageEncoderArgs.cs:331-335` | `av1_vaapi` **随包构建不含**（`-h encoder=av1_vaapi` 报 `Codec is not recognized`）⇒ 该分支无任何实测依据 |
| R6 | `ImageEncoderArgs.cs:427-429` | `AvifQIndexFromCrf` 是**表算非实测**：本机无 AMD GPU（`amfrt64.dll` 打不开）⇒ AMF 是否按 AV1 qindex 解释、cqp 对 AV1 支持度，均未验 |
| R7 | `QueueProcessor.cs:5575-5577` | `IsAnimatedAvifAsync`（异步）与 `ProbeAvifStreamRolesAsync`（同步）**同机制但未共用**代码。形态被门禁逐字锚点钉着 ⇒ 合并需另开切片 |
| R8 | `QueueProcessor.cs:5500-5502` | 「全为单帧 + 无 `tmap`」被**保守判为多帧** ⇒ 静态 AVIF + alpha 会白跑两步法（有意的代价换「不丢 alpha」） |
| R9 | `IsoBmffGainMapProbe.cs:133` | `HasTmapItemFast` 的文件上限 `TmapFastMaxBytes = 32 MiB`。超限 ⇒ `null` ⇒ **不确定** ⇒ 保守按多帧（大 AVIF 会白跑两步法） |
| R10 | §6.1 | 超时分支**没有行为实测**（素材构造不出来），只有源码形态自检 |
| R11 | `QueueProcessor.cs:5358-5400` 附近 | 探测缓存策略：`AnimatedJxlCache` / `AnimatedWebpCache` **只缓存确定结果**；**avif 侧没有对应缓存** ⇒ 同一 avif 输入在四条消费链上每次都真跑 ffprobe（实测 47~83 ms） |
| **R12** | `ImageEncoderArgs.cs:616-630` vs `IsoBmffGainMapWriter.cs:13-14` | **`AnimationLoop` 对 AVIF 完全不兑现，且不出声**：ffmpeg 路不发 `-loop`（对照 gif `-loop` / apng `-plays` 都发了），avifenc 路**硬编码** `--repetition-count infinite`。而 avif muxer **自报表里确有 `loop`** ⇒ 「能做而没做」。见 §4.9 / §4.10 |
| **R13** | `ImageEncoderArgs.cs:378-389` + `:596-607` | **`--avif-tune` 的越界值只播报、不失败**：`NormalizeTuneToken` 对认不出的值**原样返回**，由分发层 `Console.WriteLine` 点名，**退出码仍是 0**。⇒ 自动化脚本若只看退出码，无法发现 tune 没生效 |
| **R14** | `ColorTransformPlan.cs:582` vs `ImageEncoderArgs.cs:773-778` | **两条位深口径不一致**：能力表 `["avif"] => MaxBitDepth = 12` 是**硬编码**、**不看编码器**；而 `AvifMaxBitDepthForEncoder` 对 SVT/QSV/AMF/VAAPI 返回 **10**（`FfmpegCommandBuilder.cs:1021-1024` 的裁决点用的是后者）⇒ 能力表对这四个后端**高报**。两处互不引用，**无任何注释承认此分歧** |
| **R15** | `QueueProcessor.cs:3716` | **alpha 提取失败被静默吞掉**：调 `FfmpegRunner.RunAsync(alphaArgs, s => { }, …)` —— 回调是**空 lambda**，返回码未接收、未判 0。失败时只有后面的存在性检查间接反映（`hasAlpha=false`），日志显示"alpha 无"，与"源本就无 alpha"**不可区分** |
| **R16** | `QueueProcessor.cs:3712` | alpha 存在性**冗余读取**：`:3712` 那次求值在 alpha 提取**之前**，此刻 `a_*.png` 必然不存在 ⇒ 恒 false ⇒ `if (!hasAlpha && alphaArgs != null)` 恒等于 `if (alphaArgs != null)`。**冗余但无害**；误读会得出错误心智模型 |
| **R17** | `QueueProcessor.cs:5168/5179/5185/5203` | `ProbeAvifStreamRolesAsync` 的「探测失败」与「无 alpha」**语义塌陷**：四处回落全 `return (2, 3)` ⇒ `alphaIdx = 3`（**非 null**）⇒ 调用方 `:3704` 的 `alphaIdx is int ai2` 判真 ⇒ **会把不存在的 `0:3` 当成 alpha 去 `-map`**。这是刻意的向后兼容（`:5145`「绝不因探测失败而改变已正常工作的路径」），但语义上无法区分 |
| **R18** | `QueueProcessor.cs:5173` vs `:5393` | **超时常量的"共享"是假的**：`:5393` 的 `DefaultAvifProbeTimeoutSec = 20` 注释自称「与 `ProbeAvifStreamRolesAsync` 的 20 s 口径一致」，但后者是**硬编码字面量** `WaitForExit(20000)` + 硬编码日志串 `（20s）`，**既不读该常量、也不读** `FFMPEGGUI_AVIF_PROBE_TIMEOUT_SEC` ⇒ 改常量会**静默分叉**；环境变量只对 `IsAnimatedAvifAsync` 生效 |
| **R19** | `QueueProcessor.cs:5439` vs `:5154-5156` | **同一条 AVIF 链上两个 ffprobe 解析器口径不同**：`IsAnimatedAvifAsync` 走全局唯一入口 `PlatformServices.ResolveFfprobePath`（`:122`），`ProbeAvifStreamRolesAsync` 自己从传入 `ffmpegPath` 同目录拼 `ffprobe.exe`（**绕过**该入口）。与 `PlatformServices.cs:355-357` 对 avifenc「所有提问都必须走本函数」的既定纪律**不同形** |
| **R20** | `QueueProcessor.cs:3686` vs `:3675` | **两步法分支的 `item.Command` 不是真命令行**：静态分支设 `"ffmpeg " + ffArgs`（`:3675`，可复现），两步法分支设 `$"[avif→{fmt}] 两步法: 分轨提取颜色+alpha → alphamerge 合并编码"`（`:3686`，**非命令行**），真命令行只进 `item.Log`（`:3708/:3715/:3761`）⇒ 读 `item.Command` 的消费者（GUI 命令行标签、`--dry-run`）在两步法下拿不到可复现命令 |
| **R21** | `QueueProcessor.cs:790-793` | **测试载体在约束产品代码结构**：预缩放点的 avif 探测支自认是结构性冗余（多帧/不确定已被上面短路），保留理由逐字是「为了不打断 `verify-avif-anim-probe.ps1` 钉住的『三处消费 determinate』的点名式载体」⇒ 对静态确定的 avif 会**多跑一次 ffprobe**（同命令同结论） |
| **R22** | `QueueProcessor.cs:441-448` | 旁路提示对 `.avif` **过宽**：判据是 `inputExt is ".jxl" or ".jxr" or ".avif"`，对**静态** `.avif` → webp（实际走普通单命令、与色彩引擎入口同分支）也会打「色彩引擎尚未接入该路径」 |

---

## 8. 二次开发：想改 AVIF 动图，该动哪里

| 目标 | 改这里 | 注意 |
|---|---|---|
| 让 AVIF 输出支持**帧率** | `ImageEncoderArgs.BuildAvifOptions`（加 `fps=` 滤镜需在 `FfmpegCommandBuilder` 层），或扩展 avifenc 路的准入条件 | ⚠ 改 `BuildAvifOptions` 会**同时**影响引擎出口与 legacy（单一真值）；加 `-vf` 要走 `AppendVideoFilter`/`InsertScaleFilterAtChainHead`，**不得**自己 `args.Add("-vf")`（会与色彩链冲突） |
| 让 AVIF 输出支持**循环次数** | 同上 + 确认 ffmpeg `avif` muxer 是否有对应 option | ⚠ 本仓 ffmpeg 的 `avif` muxer **不采纳** `-color_primaries/-color_trc`（见 `docs\AVIF_GAINMAP_ENCODER_2026-10-02.md:30`）⇒ 先 `ffmpeg -h muxer=avif` 核实有哪个 option 再动手 |
| 放开 **非 GIF 输入** → 动图 AVIF（走 avifenc） | `QueueProcessor.cs:915-919` 的分派条件 | ⚠ 需先解决「怎么从任意多帧源提取 RGBA 帧」并**保持帧序列**（参考 §3.2 的 `-fps_mode passthrough` / `-start_number`） |
| 给 avifenc 路加**色彩/CICP 参数** | `ProcessGifToAvifViaAvifencAsync` 的 `avifencArgs` 拼装 | ⚠ `avifenc` 有 `--nclx P/T/M` 与 `--icc`；但 `--lossless` 下 avifenc **强制 matrix=identity(0)**（`ColorTransformPlan.cs:581`） |
| 给 avif 探测加**缓存** | 照 `AnimatedWebpCache` 的模式（`QueueProcessor.cs:5689`/`:5709`）新建 `AnimatedAvifCache` | ⚠ **只缓存确定结果**（照 `AnimatedJxlCache`/`AnimatedWebpCache` 的既定纪律：把「不知道」写成 `false` 等于永久固化成「它不是动画」） |
| 调探测超时 | 环境变量 `FFMPEGGUI_AVIF_PROBE_TIMEOUT_SEC`（夹紧 1..600） | ⚠ `0`/负数会让看门狗**立刻**触发 ⇒ 所有 AVIF 判「不确定」⇒ 一律按多帧（保守但白跑两步法） |

**改动的硬约束**（本仓既定纪律，改前必读）：

1. **AVIF 编码参数是单一真值** `ImageEncoderArgs.BuildAvifOptions`，传统管线与引擎出口**共用**。不得在 `FfmpegCommandBuilder` 里再写第二份。
2. **AVIF 私有项只发 libaom**（`AvifBackend` 分域）。发到别的后端只会换来 `Codec AVOption … has not been used` 告警或直接 `rc=127`。
3. **不可用/不生效必须出声**（本仓最忌讳「静默降级」）：新增任何"设了但发不出去"的组合，都要 `Console.WriteLine` 点名。
4. **探测不确定 ⇒ 保守按多帧**（三态纪律）。且三个消费点必须**同时**写 `|| !…Determinate`（门禁逐点断言）。
5. **`.gif` + `avif` 的分派条件里不得加可用性判断**（P7f）—— 那会让缺工具变成静默改道。
6. 改 `IsAnimatedAvifAsync` 后必须同步更新 `verify-avif-anim-probe.ps1:237` 的签名锚点（否则门禁转红 —— 那是**真信号**，不是假红）。
7. **CJK 门禁**：新增诊断日志用中文可以，但**续行**上不能有非 sink 的中文（`_probe-cjk-hardcode-scan.ps1` 按行判定，`QueueProcessor.cs:405-409`）。

---

## 9. 覆盖边界（本文**没有**证明什么）

措辞不得大于扫描面。以下都是**未做**的事：

1. **没有构建、没有运行任何测试或门禁**。本文所有「实测」字样均为**转述**源码注释里登记的既有读数。
2. **没有验证运行时行为**：没有跑过 `FfmpegGui.exe --headless`，没有实测任何一条本文列出的命令行，没有确认 `avifenc.exe` / `ffmpeg.exe` 的存在性。
3. **没有读 git 历史**：行号是工作区当前内容，不是某个 commit。**行号会漂移** ⇒ 请用本文给出的锚点字符串重新定位。
4. **§4.9 的「AVIF 输出不发动图参数」是静态代码阅读结论**，覆盖 `ImageEncoderArgs.cs:462-739` 全段与 `FfmpegCommandBuilder.cs` 的 `case "avif":`。**没有**实测「给 FFmpeg 的 avif muxer 传 `-loop` 会怎样」。⇒ 标记为**待确认项**，不是已定性的缺陷。
5. **未穷尽** `FfmpegCommandBuilder.BuildArguments` 的 2088 行：本文追踪了与 AVIF 相关的 append 点（§4.1 表 23 项）。`DecideOutputColor`（在 `FfmpegCommandBuilder.Decision.cs`）这一**单一裁决点**的内部判据**未展开** —— 它决定色彩链与 CICP 标注，是另一个完整话题。
6. **未审** GUI 侧（`MainWindow.xaml` / `MainWindow.xaml.cs` 的 `LibaomAvifPanel`、`AvifTuneCombo`、静帧复选框等控件）的可见性/可达性 —— 只在 `ImageEncoderArgs.cs` 的注释里读到相关登记（如 `:599-602` 的「面板点不出来但 CLI/预设能到」）。
7. **未核** `docs\CHANGELOG.md` 之外的文档是否有更新的 AVIF 动图口径。

### 建议的后续取证（按优先级）

| # | 要证的事 | 怎么证 |
|---|---|---|
| 1 | §4.9/§4.10：AVIF 输出到底能不能带动图参数 | `ffmpeg -h muxer=avif` 确认 `loop` 的取值域；再用同一多帧源跑 `-loop N` 与否的 A/B，`ffprobe` 读回循环次数 |
| 2 | R4：`av1_nvenc` + 12bit 是否真被拒 | 需要能起 NVENC 的机器 |
| 3 | §2.5 的两步法在**有 alpha 的真动图 AVIF** 上的端到端读数 | 造 `alpha+anim` 的 AVIF，跑 → gif / webp，核对帧数与 alpha |
| 4 | R11：avif 探测无缓存的实际代价 | 多帧 avif 输入跑一次，数日志里 ffprobe 的调用次数 |
| 5 | R12：`AnimationLoop` 对 AVIF 的静默忽略是否有意 | 查 issue/设计文档；若无记载，则属**应出声而未出声** |
| 6 | R14：能力表 `MaxBitDepth = 12` 与 `AvifMaxBitDepthForEncoder` 的 10 如何收口 | 需先确认 SVT/QSV/AMF 的 12bit 是否真不可行 |
| 7 | `verify-gif-avif-framelist.ps1` 的当前读数 | 跑一次（需先 build Release exe） |

---

## 附录 A：按「参数 → 出处」反查

| ffmpeg/avifenc 参数 | 出处（file:line） |
|---|---|
| `-y` | `FfmpegCommandBuilder.cs:22` |
| `-i "<input>"` | `:81-82` |
| `-sws_flags accurate_rnd+full_chroma_int` | `:87-88` |
| `-c:v <enc>` | `:94-95` |
| `-threads <N>` | `:99-100` |
| `-pix_fmt <fmt>` | `:520-521` / `:531-532` / `:539-540` |
| `-color_range <in>` | `:75` |
| `-color_range <out>` | `:561-562` |
| `-color_primaries` / `-color_trc` | `:47` / `:51` |
| `-colorspace`（输入/输出） | `:67` / `:646` |
| `-map_metadata` 组 | `:586-592` |
| `-frames:v 1` | `:669-670`、`:742` |
| `-t <AnimationDuration>` | `:699-700` |
| `-crf` | `ImageEncoderArgs.cs:521` |
| `-cq` | `:482` |
| `-global_quality` | `:497` / `:515` |
| `-rc cqp` / `-qp_i` / `-qp_p` | `:506-508` |
| `-tune` | `:537` |
| `-usage allintra` / `-aom-params tune=iq` | `:541-542` |
| `-cpu-used` | `:612` |
| `-still-picture` | `:625` / `:627` |
| `-row-mt` | `:629` |
| `-preset`（SVT / NVENC / QSV） | `:637` / `:679` / `:684` |
| `-svtav1-params` | `:644` |
| `-quality`（AMF） | `:688` |
| `-compression_level`（VAAPI） | `:691` |
| `-spatial-aq` / `-aq-strength` | `:707` / `:709` / `:721` |
| `-low_power` | `:734` / `:736` |
| `-aq-mode` | `:652` |
| `-enable-cdef` | `:655` / `:657` |
| `-enable-intrabc` | `:660` / `:662` |
| `-denoise-noise-level` | `:665` |
| `avifenc -q` / `-s` / `-j` | `QueueProcessor.cs:3852` |
| `avifenc --fps` / `--repetition-count` | `:3861` |
| `avifenc -o` | `:3861` |
| `-pix_fmt rgba -fps_mode passthrough` | `:3808` |
| `-map 0:N -fps_mode passthrough -start_number 0` | `:3703-3706` |
| `alphamerge` / `format=yuva420p` | `:3735` / `:3750` |
| `-c:v libwebp_anim -q:v -loop` | `:3750` / `:3754` |
| `palettegen` / `paletteuse` | `:3735` / `:3739` |
| `-loop 0`（GIF 出口） | `:3735` / `:3739` |
| `-c:v <enc>`（引擎出口） | `RawColorPipeline.cs:989` |
| `-pix_fmt`（引擎出口） | `RawColorPipeline.cs:996` |

**⚠ 注意**：`-loop` 在**全仓**只为 `gif`(`ImageEncoderArgs.cs:231`) / `webp`(`FfmpegCommandBuilder.cs:274-275`) 发出；`-plays` **全仓仅一处**（`FfmpegCommandBuilder.cs:378-379`，apng）。**没有任何一处为 avif 目标发 `-loop`/`-plays`**（见 §4.10）。

## 附录 C：AVIF 位深口径的两条（不一致的）链路

| 消费点 | 取值 | 出处 |
|---|---|---|
| **能力表**（`ColorTransformPlan`）：规划层用 | **硬编码 `12`**，不看编码器 | `ColorTransformPlan.cs:582` |
| **裁决点**（`capBd`，真正施加钳制）：`FfmpegCommandBuilder` 用 | `AvifMaxBitDepth(options)` ⇒ libaom/nvenc **12**、其余 **10** | `FfmpegCommandBuilder.cs:1021-1024` → `ImageEncoderArgs.cs:773-778` |
| 传统管线 `-pix_fmt` 生成 | `ClampAvifBd(…)` 用同一函数 | `FfmpegCommandBuilder.cs:510-511`，用在 `:521/:532/:540/:722/:740` |
| 引擎出口 | `AvifEffectiveBitDepth(o, srcBd)` | `ImageEncoderArgs.cs:800-805` → `RawColorPipeline.cs:995` |

⚠ **R14**：能力表对 `libsvtav1`/`av1_qsv`/`av1_amf`/`av1_vaapi` **高报**（声明 12、实际上限 10）。两处互不引用，源码中**无任何注释承认此分歧**。

**`MapPixFmt` 的位深 → 像素格式映射**（`ImageEncoderArgs.cs:873-894`）：

```
bd <= 8  →  yuv444p / yuv422p / yuv420p            （按 Chroma）
bd > 8   →  yuv{444,422,420}p{depth}le
            depth: 12 → "12" | 16 → "16" | 其余 → "10"
```

⚠ `16` 这一支对 AVIF **不可达**（AVIF 先被钳到 ≤12）。`hasAlpha && fmt == "avif"` ⇒ **落到普通 YUV、丢弃 alpha**（`:865-869`）。

⚠ `GIF→AVIF` 的 alpha 像素格式走**另一条** `MapAvifAlphaPixFmt`（`FfmpegCommandBuilder.cs:1074-1093`）：bd ≤ 8 ⇒ `yuva444p`/`yuva422p`/`yuva420p`；bd == 10 ⇒ `yuva420p10le`；**其余（含 12/16）也回退 `yuva420p10le`**（`:1091-1092`「12/16-bit 编码器支持有限，回退到 10-bit」）。⚠ 它读 `options.BitDepth ?? 8` **未经钳制** ⇒ 16-bit 请求在此**静默产出 10-bit**。

## 附录 D：奇数尺寸修复（`scale=ceil(iw/2)*2:ceil(ih/2)*2`）

在 `FfmpegCommandBuilder.Decision.cs:358-363`：

```csharp
// 修复：输出为 YUV 4:2:0 时，先确保宽高为偶数（libzimg 要求 yuv420p 宽高可被 2 整除）。
// 通过 scale=ceil(iw/2)*2:ceil(ih/2)*2 在 zscale 前插入偶数化。
var needsEven = outMatrix == "bt709" || outMatrix?.StartsWith("bt2020") == true;
var preScale = needsEven ? "scale=ceil(iw/2)*2:ceil(ih/2)*2:flags=lanczos," : "";
d.PixelChains.Add($"format=rgb48le,{preScale}zscale=pin={srcP}:tin={srcT}:min={srcM}:p={dstP}:t={dstT}:m={dM}");
```

⚠ **门控条件**：只在**真的发生色彩转换**时插入（源 ≠ 目标、且不是 HDR→SDR，`:350-353`）。注释给的两个 libzimg 理由：奇数尺寸 4:2:0 不可整除（错误 **1027**）与 RGB 输入缺标签（**3074**）。出处也在 `QueueProcessor.cs:1465` —— 那里有一条正则**刻意不把**这个 token 误判成动图缩放宽。

## 附录 B：关键常量速查

| 常量 | 值 | 出处 |
|---|---|---|
| AVIF 探测超时默认值 | `20` 秒 | `QueueProcessor.cs:5393` |
| AVIF 探测超时环境变量 | `FFMPEGGUI_AVIF_PROBE_TIMEOUT_SEC`（夹紧 1..600） | `:5405-5406` |
| 流角色探测超时 | `WaitForExit(20000)` 毫秒 | `:5173` |
| 流角色探测失败兜底 | `(2, 3)` | `:5168` / `:5197` / `:5203` |
| `tmap` 快速探测文件上限 | `32 MiB` | `IsoBmffGainMapProbe.cs:133` |
| WebP 探测超时默认值 | 见 `DefaultWebpProbeTimeoutSec` | `QueueProcessor.cs:5556-5564` |
| Windows 命令行上限 | `32767` | `:3868` |
| avifenc `-q` 夹取范围 | `[20, 100]` | `:3838` |
| avifenc `-s` 夹取范围 | `[0, 10]`（默认 5） | `:3839` |
| 动图默认 fps | `33` | `:3723` / `:3837` |
| AVIF 位深上限（libaom/nvenc） | `12` | `ImageEncoderArgs.cs:776` |
| AVIF 位深上限（其余） | `10` | `ImageEncoderArgs.cs:777` |
| 质量 → crf 映射 | `round((100 - q) * 63 / 100)` | `ImageEncoderArgs.cs:46` |
| NVENC `-cq` 夹取 | `[1, 63]` | `:482` |
| QSV `-global_quality` 夹取 | `[1, 51]` | `:497` |
| AMF qindex 换算 | `max(1, crf>=63 ? 255 : crf==62 ? 249 : crf*4)` | `:431-435` |
| `-aq-strength` 夹取 | `[1, 15]` | `:721` |
| 硬件档位夹取 | `[1, 7]` | `:675` |

---

*本文由只读源码调查生成。所有 `file:line` 引用取自撰写时的工作区内容；代码演进后请以文中给出的锚点字符串重新定位。*
