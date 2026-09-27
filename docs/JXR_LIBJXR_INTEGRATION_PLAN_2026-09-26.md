# JXR 走 ffmpeg 内置 `libjxr` 的接入规划（2026-09-26，**只做规划，本轮不实现**）

背景任务：给 JXR 补齐"ffmpeg 内置 libjxr"支持。
本轮结论：**编码侧被实测挡住**，用户已决定先与随包 ffmpeg 的发布者沟通 JXR 相关事项，
因此本文件只给规划 + 一份可直接递给维护者的最小复现包。

关联：`docs/ENCODING_OPTIONS_AUDIT_2026-09-26.md` 的 A-23 / A-24 / A-25 是本文的证据来源；
`docs/TESTING.md §6 第 111 条` 说明为什么现在只有一处采集点（本规划要接的就是那一处）。

---

## 1. 已确证的事实（全部本机实测，含对照组）

随包 ffmpeg = `publish/PLAN/ffmpeg-full/ffmpeg.exe`，版本 `git-2026-09-24-c0e8b139fd`。

| 事实 | 读数 |
|---|---|
| 编解码器都在 | `DEVILS jpegxr … (decoders: libjxr) (encoders: libjxr)`；**没有原生解码器兜底**（上游无 `AV_CODEC_ID_JPEGXR`） |
| 声称支持的像素格式 | `gray gray16le rgb24 bgr24 rgba bgra rgb48le rgba64le`；threading = none |
| **无质量控制** | `-q:v 10` / `-q:v 90` / `-compression_level 3` / `-quality 50` / `-crf 30` / 不传 ⇒ 六份产物 **逐字节相同**（3719 B，同一 sha256）；`-h encoder=libjxr` 打不出任何私有 AVOption |
| **产物无法解码** | 8 种像素格式全部：ffmpeg 自身报 `jxrlib JPEG XR decode failed (error=-1)`；**独立参照**微软 `JxrDecApp` 输出 **0 字节** |
| 失败是**静默**的 | 编码 `rc=0` 且文件写出；只有去解码才暴露 ⇒ 直接接成后端 = 用户拿到"退出码 0 + 打不开的 JXR" |
| 排除"用法错" | 对照组：`JxrDecApp` 与 ffmpeg 都能正常读现成素材 `tests/output/sources/src.jxr`（解出 589878 B BMP）；换 BMP 输入、加 `-update 1 -frames:v 1` 结果不变 |
| 排除"解码器坏" | 同一 ffmpeg 的 libjxr **解码**上述 fixture 成功 ⇒ 坏的是**编码**这条腿 |
| 不在上游 | 带**阳性对照**核查 `master/libavcodec/codec_id.h`：`AV_CODEC_ID_JPEGXL` 命中 2（取法通）而 `JPEG_XR/JPEGXR` 命中 **0**；全集仅 `JPEG2000/JPEGLS/JPEGXL/JPEGXL_ANIM/JPEGXS`。另 patchwork/trac 搜 `jpegxr` 零命中、`doc/codecs.texi` 与 `Changelog` 无该条 |

⇒ **本机的 `libjxr` 是带外补丁**（随包构建自带），其编码器产出无效码流，且没有任何质量旋钮。

两个附带发现（不属于本规划，但同源）：
- 仓内 4–5 处代码断言"本工具链的 ffmpeg 没有 jxr 编码器（实测 `-encoders` 无 jxr）"，
  位置：`Models/FfmpegOptions.cs:365`、`Services/ColorMapping/ColorEngineRouter.cs:19` 与 `:217`（**含一个活的路由条件**）、
  `ColorIntentFactory.cs:173`、`RawColorPipeline.cs:531-532/560/565`。这些"实测"已过期，实现本规划时要一并改掉，
  否则下一个人又会拿它当依据。
- 硬件编码器那条假缺席值得单独立项：RGB 源被协商成 `yuv444p` ⇒ `YUV444P not supported` 被折叠成
  `No capable devices found`（本机确有 RTX 5080/4060）。见 audit A-25。

---

## 2. 规划：三块，各自可独立验收

### B0 往返自检闸门（**任何启用动作的前置**，也是本机今天就能验完的一块）
新增一条能力判定（建议放 `Services/EncoderDetectionService.cs`，与既有
`SupportsJxlLosslessJpegAsync` 同一形状：以 ffmpeg 路径为键缓存、`ClearCache()` 里失效）：

```
ProbeFfmpegJxrRoundTripAsync(ffmpegPath) ->
  1) 用 lavfi 造一张**够大**的图（≥256×256；见 A-25 的尺寸坑）
  2) -c:v libjxr 编成 .jxr            （rc=0 且文件 > 0 字节）
  3) 再用同一 ffmpeg 把它解回原始像素   （rc=0 且真的产出帧）
  4) 比较解码像素与源：要求 PSNR = inf（若上游是无损档）或 ≥ 40 dB，
     且尺寸一致；任何一步失败 ⇒ 判"不可用"，并**点名败在哪一步**
  5) 阳性对照（判据自己的守门）：第 3 步先对 `tests/output/sources/src.jxr` 跑一遍，
     解不出来就判"自检失效"而不是"编码器不可用"——否则 fixture 腐烂会伪装成能力缺失
```
判据要点：**不许只用"编码器存在于 `-encoders`"当可用性**（今天就是反例：存在、能跑、rc=0、产物坏）。
这条自检同时是 B1 的开关与 B2 的看门人。

### B1 编码侧接入（等自检能过才开）
1. `FormatEncoderMap["jxr"]` 增加 `libjxr` 候选；`IsAvailable` 由 B0 的判定驱动
   —— 现有机制已经会按 `IsAvailable` 过滤编码器列表（`RefreshEncoderListAsync:1948`，注释即"可用性诚实原则"），
   所以**列表/选择无需新逻辑**，只要把判定接进去。
2. `QueueProcessor.cs` 的 JXR 出口分派（今天形如 `var useJxrEnc = targetFmt == "jxr";` 的无条件分支）
   改成按**用户所选编码器**分派，而不是按扩展名；选了 `libjxr` 就走普通 ffmpeg 出口，选 `JxrEncApp` 仍走外部工具。
3. 色彩引擎侧**不需要**动 `ColorEngineRouter.cs:215-217` 那个 block：它的进入条件要求
   `EncoderBackend != Ffmpeg`，而 `libjxr` 属 Ffmpeg 后端 ⇒ 天然不进入。要做的只是把该处的**过期注释**改准
   （它现在断言"JxrEncApp 是本工具链唯一的 jxr 编码器"）。
4. **不暴露质量旋钮**（实测无效）。要么在 `libjxr` 被选中时**禁用**质量滑块并给一句"内置 libjxr 无可调质量档"，
   要么不把它作为可选后端 —— 二者取一，**不允许**留一根不动的滑块在面板上（audit A-3/A-4 同族教训）。
5. 容器与扩展名：实测 image2 只认 `.jxr`（`.wdp`/`.jpx` 报 `Unable to choose an output format`）；
   ffmpeg 产物与 `JxrEncApp` 产物首部同为 `49 49 bc 01`（裸 codestream，非 HD Photo 容器）
   ⇒ **换后端不改变容器形态**，现有 ICC 后置步骤可沿用，不需要新写容器层。

### B2 解码侧收益（**与坏编码器无关，可以先行**）
JXR **输入**今天强制依赖外部 `JxrDecApp.exe`，且代码里写着"FFmpeg 不支持 JXR 格式"
（`QueueProcessor.ProcessJxrInputAsync`）。而本机实测 libjxr 解码器工作正常。
⇒ 把解码改为"优先 ffmpeg 内置、外部工具作为备选"，摘掉一条外部二进制依赖；
这条的验收完全在本机可做（产物像素与 `JxrDecApp` 输出对照）。
唯一要小心的坑：ffmpeg 侧读到的 `profile=unknown`，位深/alpha 的取值域要靠实测枚举而非声明。

### 验收与门禁
- 新增判据建议挂在**既有** `ServiceProbe` 上（如 `jxr` mode），而不是新建 `verify-*.ps1`：
  新建脚本会改运行器的 `$expectedScriptCount`（现 54）与运行器自哈希，成本更高。
- 必测三条：① B0 自检在本构建判"不可用"且点名败步；② 阳性对照不失灵（fixture 换成坏文件时要报"自检失效"）；
  ③ 编码器列表在未通过自检时**不含** libjxr（防"存在即可选"复发）。

---

## 3. 可直接递给随包 ffmpeg 发布者的最小复现包

> 目标问题：随包构建里的 `libjxr`（JPEG XR via jxrlib）**编码器产出无法被任何解码器读取的码流**，
> 且**没有任何质量参数**生效。请确认：这是哪一支带外补丁、是否有已知问题、能否升级到修好的版本，
> 或者在没有可用编码器时把 `libjxr` 从构建里摘掉。

复现步骤（Windows，路径用正斜杠以免被 shell 改写）：

```bash
FF=./ffmpeg.exe   # git-2026-09-24-c0e8b139fd，含 V....D libjxr (codec jpegxr)

# 1) 编码成功、退出码 0、文件写出
$FF -hide_banner -loglevel error -y -i src_8bit.png -c:v libjxr out.jxr   # → 3719 字节

# 2) 但同一构建解不回来
$FF -hide_banner -loglevel error -y -i out.jxr back.png
#   [libjxr] jxrlib JPEG XR decode failed (error=-1)

# 3) 独立参照：Microsoft jxrlib 的 JxrDecApp 也读不了（输出 0 字节）
JxrDecApp.exe -i out.jxr -o out.bmp -v
#   产物 0 字节；verbose 里 bitstream size 与实际文件大小不自洽

# 4) 对照：同一个 JxrDecApp 能正常读真·JXR（解出 589878 字节 BMP）
JxrDecApp.exe -i real_from_JxrEncApp.jxr -o ok.bmp        # → OK

# 5) 8 种像素格式全部如此
for pf in gray gray16le rgb24 bgr24 rgba bgra rgb48le rgba64le; do
  $FF -y -i src_8bit.png -pix_fmt $pf -c:v libjxr p_$pf.jxr
  $FF -y -i p_$pf.jxr p_$pf.png        # 全部 decode failed
done

# 6) 质量旋钮无作用：以下六个输出逐字节相同
for opt in "" "-q:v 10" "-q:v 90" "-compression_level 3" "-quality 50" "-crf 30"; do
  $FF -y -i src_8bit.png $opt -c:v libjxr q.jxr; sha256sum q.jxr
done
# $FF -hide_banner -h encoder=libjxr  ⇒ 除 supported pixel formats 外无任何 AVOption
```

需要对方回答的三件事：
1. `libjxr` 是哪一版补丁（分支/commit/作者）？上游 FFmpeg 没有 `AV_CODEC_ID_JPEGXR`，所以它必然是带外的。
2. 编码器产出无效码流是否已知？是否有修好的版本可以换？
3. 该补丁**是否设计上有质量参数**（`-q:v`/`-crf` 之类）？若无，是否应当在建包说明里写明
   "libjxr 仅固定档、无质量控制"，好让上层软件不去暴露质量滑块。

（另附一条与本包相关的独立观察：本构建的 `jpegxr` 只登记了 `decoders: libjxr`，**没有原生解码器兜底**；
而 libjxr 解码器实测可用 ⇒ 只要编码器修好，JXR 的编解码都可以不依赖外部 `JxrDecApp/JxrEncApp`。）

---

## 4. 本轮边界

- 未改任何生产代码：B0/B1/B2 全部只规划。
- 恢复实现的触发条件：**随包 ffmpeg 换成修好 libjxr 的构建**（发布者给新版本），或决定先做 B2
  （解码侧，本机今天就能全验，不依赖编码器修复）。
