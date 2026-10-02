# 高级编码选项全量审查（2026-09-26）

范围：`高级色彩 / 高级编码` 面板涉及的**全部**编码器与格式选项面 —— libaom-av1、SVT-AV1、
NVENC/QSV/VAAPI/AMF、ffmpeg libjxl/libjxl_anim、cjxl、mjpeg、cjpegli、png/apng/gif/libwebp/tiff、
JXR(JxrEncApp)、DNG(dngtool)。

方法：每个选项做**四列对账** —— ① UI 控件与其可见/可用门控 ② 代码实发的命令行 ③ **官方语义**
（本机 `ffmpeg -h encoder=X` / 工具自带 usage / 本机 `tools/src` 的上游源码 / 规范原文）
④ 实机证据（随包 ffmpeg `publish/PLAN/ffmpeg-full`，git-2026-09-24）。

分工：5 个并行子 agent 分 lane 取证，**主循环对最重的结论独立复跑**。
本文件里标 🔷 的条目是主循环亲自复现过的；标 ⬜ 的是仅凭子 agent 取证（可信度次一级，已注明）；
标 ❌ 的是主循环**驳回**子 agent 结论的条目。

真值源背景：控件→`FfmpegOptions` 的采集已于本日收敛为唯一入口 `CollectOptionsFromUiAsync`
（见 `TESTING.md §6 第 111 条`），所以本轮所有 ② 列只需读一处；但**队列内部仍有第二处独立拼装**
（见 A-1），且 DNG/JXR 的预览与 `item.Command` 也仍各自手拼（见 A-8）。

---

## 一、确证缺陷（按严重度）

### A-1 🔷 队列内部又有一份"字段搬运漏项"：JPEG→JXL 时**用户的无损被丢**
`Services/QueueProcessor.cs:1955-1963` 为 JPEG 输入重建 `jpegOpts`，只搬
`Quality / JxlEffort / CjxlProgressive / CjxlPhotonNoiseIso / JxlLosslessJpeg / Threads` 六项，
**没搬 `Lossless`**（`JxlModular`、色彩相关、`CjxlAutoPhotonNoise` 同样没搬）。
⇒ 用户勾了「无损」，`CjxlService` 收到 `Lossless=false` ⇒ 实发 `-d 1.5` 而非 `-d 0`。
证据：⬜ 同图 `-d 0` = 47390 B vs `-d 1.5` = 14971 B。
连带：`item.Command` 用**完整** opts 生成 ⇒ **队列详情里显示的命令 ≠ 实际执行的命令**。
这是 `TESTING.md §6 第 111 条` 同一缺陷类在队列侧的残留实例。

### A-2 🔷 状态标签与日志自相矛盾
紧接 A-1 的 `QueueProcessor.cs:1976`：`item.Status = isJpegInput ? "已完成 (cjxl 无损重封装)" : ...`
—— **无条件**写"无损重封装"，与 `JxlLosslessJpeg` 是否真生效无关；而同函数上面一行的日志确实按它分了支。

### A-3 🔷 质量滑块对四个硬件后端完全无效（宣称≠交付）
`-crf` 对 `av1_nvenc/av1_qsv/av1_amf/av1_vaapi` 不是合法 AVOption，代码却恒发（`ImageEncoderArgs.cs:286-289`）。
主循环独立复现：同一素材、同一构建，`-crf 10` 与 `-crf 60` 产物 **16512 B、sha256 前缀同为 `35dc3ded`**，
且 ffmpeg 自己警告 `AVOption crf ... has not been used`；改 `-cq 20 / -cq 45` 才变（14417 / 4698 B）。
⇒ 面板上 `hw.hint`「质量由上方滑块控制」是假宣称。正解：硬件后端应走 `-cq`（NVENC/QSV）等各自的质量轴。

### A-4 🔷 NVENC「空间自适应量化」复选框完全惰性
`ImageEncoderArgs.cs:410` 只在**取消勾选**时发 `-spatial-aq 0`，勾选时什么都不发。
主循环三向对照：省略 == `-spatial-aq 0`（同哈希 `35dc3ded`），而 `-spatial-aq 1` 产物不同（`6998a669`，16477 B）
⇒ NVENC 默认本就是 0，两个勾选态等价 ⇒ **该复选框不可能产生任何影响**。
（❌ 驳回子 agent 的"合规，语义差异仅为改默认"判定 —— 它没做"省略/传默认/传有效值"三向对照。）

### A-5 🔷 NVENC `aq-strength` 允许 0，官方区间是 [1,15]
XAML `Min=0`（`MainWindow.xaml:587`），官方 help `from 1 to 15`。主循环复现：`-aq-strength 0` ⇒
`Value 0.000000 for parameter 'aq-strength' out of range [1 - 15]`、`rc=127` ⇒ **把"0=关闭AQ"当成合法档位**。
zh 文案 `avif.aq.hint` 也跟着说"0=关闭 AQ"。另注：官方文档里 aq-strength 只在 spatial AQ 开启时才被使用（见 A-4）。

### A-6 ⬜ SVT-AV1 的 tune 用 1 基写成 0 基 ⇒ 选错目标 + 选 SSIM 必崩
`ImageEncoderArgs.cs:341-344` 发 `-svtav1-params tune=1/2/3` 对应 PSNR/SSIM/VMAF，而库自报
`0=VQ 1=PSNR 2=SSIM 3=IQ 4=MS_SSIM 5=VMAF` ⇒ 整体错位一格：选 VMAF 实得 PSNR、选 SSIM 实得 IQ，
而 **IQ 只支持 all-intra ⇒ 直接 `exit 127` 崩**。同函数 libaom 侧 `vmaf_without_preprocessing` 该值不存在（硬失败）。
建议修复顺序：先让 GUI 的"失败要响亮"这条不被动到，再逐值对表。

### A-7 🔷 9 处拿**中文显示串**当参数键比对 ⇒ 英文界面下整条选项静默失效
`ImageEncoderArgs.cs:293/309/328/337/393/398/400/402/404`。主循环核 `Resources/Locales`：
`avif.tune.iq` zh=`IQ (图像优化)` / en=`IQ (Image Optimized)` ⇒ **en-US 下 `case` 永不匹配 ⇒ IQ 什么都不发**；
`!= "默认"` 这类哨兵在英文下恒真 ⇒ 进了 switch 又落空。
（另：`HwPresetCombo` 在 XAML 已不存在（grep 命中 0），故 393-404 整段是**死码**；
但同样的"中文字面量当键"模式在别的分支是活的。）
⇒ 正确做法是比 token/枚举，显示串只用于渲染。本仓已有 `_probe-i18n-scan.ps1`，但它不查这种"值比对"。

### A-8 ⬜ DNG / JXR 的"展示命令"与"执行命令"不是一串
DNG 预览（`MainWindow.xaml.cs:1399`）与 `BuildQueueItemCommand`（`:3162`）手拼 `-jxl -q <n>`，
而 dngtool 的 JXL 质量真实旗标是 **`-jxlq`**（实测 `-q 3`/`-q 50` 对 JXL 质量**无效**，与 `-jxlq 0` 逐字节相同）
⇒ 照 UI 显示的命令手敲必得"无损"。预览路径也没纳入本日刚收敛的唯一采集点。

### A-9 🔷 参数未按编码器分域下发 ⇒ 硬件/SVT 上常年带一堆被忽略的参数
`ImageEncoderArgs.cs:290/322/326` 只按 `isSvt` 排除 libaom 项 ⇒ 四个硬件后端照收
`-cpu-used / -still-picture / -row-mt / -aq-mode / -enable-cdef / -enable-intrabc / -aom-params`
（⬜ 实机 8 条 `has not been used`）。其中一条最危险：⬜ zh 默认 tune 走 `-usage allintra`，
而 **AMF 有同名私有项** ⇒ `Unable to parse "usage" option value "allintra"` ⇒ **默认配置下 av1_amf 必失败**。

### A-10 ⬜ 两个"默认勾选"互相否决，且否决条件与面板可见性不一致
JXL：`JxlPreserveUltrahdrCheck` 与 `JxlLosslessJpegCheck` 默认都勾（`MainWindow.xaml:631/642`），
采集侧的否决条件是 `!(useAdvCodec && preserveUltrahdr)`。
⇒ 主循环改写子 agent 的结论（它说"默认配置下 jbrd 永不生效"是**过头的**：默认 `UseAdvancedCodec` 关，否决不生效）。
真实形状更糟：那两个框只在"高级编码"打开时**看得见**，一打开就默认勾上并否决 jbrd；
而它们**隐藏时那个勾仍参与运算**并放行 jbrd ⇒ 面板可见性与生效条件不一致，
正是 `IsAnimationMode()` 那条规矩（"看不见却能改命令"）要避免的形状。

### A-11 ⬜ SVT 面板上的 `SvtStillPictureCheck` 是死控件
全仓无 `?.IsChecked` 消费方（只有 `:582` 挂了 `RegenerateCommand` 回调）；实发的 `-still-picture`
取自**隐藏的 libaom 那只复选框**。而 libsvtav1 **根本没有**这个 AVOption（`svtav1-params still-picture=1` 报
`Error parsing option`）⇒ SVT 上不存在正确接法 ⇒ 应删控件而非补接线。

### A-12 ⬜ 无损宣称在 SVT 上是假的
质量轴只发 `-crf = round((100-q)*0.63)`，从不发 `-qp/-b:v`。libaom 下 `crf 0` 实测 PSNR=**inf**（真无损）；
**SVT 下 `crf 0` 实测 46.04 dB（有损）**，需 `svtav1-params lossless=1` 才无损（22391 B、inf）。
⇒ 同一勾"无损"在两个编码器一个真一个假。

### A-13 ⬜ GIF 的"关闭抖动"关不掉；"不循环"表达不出来
① 取消 `GifDitherCheck` 后仍是裸 `paletteuse`，而该滤镜**默认 sierra2_4a**（官方默认值≠关）
⇒ 三态互异：bayer 29959 / 裸 30073 / `dither=none` **26714** B ⇒ 缺一句 `dither=none`。
② 提示写"-1=不循环"，但代码 `AnimationLoop != -1` 才写 `-loop` ⇒ `-1` **永不进命令行**
⇒ 落到 gif muxer 默认 `0=无限`；手工 `-loop -1` 实测确实产出无 NETSCAPE 的真·不循环。

### A-14 ⬜ PNG 的"强制无损"被实现成"恒 `-compression_level 0`"
同图 level0 = 595629 B vs level9 = 52807 B ⇒ **体积 11.3×**；无损语义并不要求关掉 deflate。
（既定产品语义"PNG 强制无损"本身没问题，问题是无损 ⊃ 压缩级别被同一常量绑死。）

### A-15 ⬜ WebP `compression_level` 的适用范围判断错误（且告警用户看不见）
代码注释与告警称它是"无损模式专有 zlib 级别"，实测**有损也生效**（9716/7562/7432）、
**动图无损也生效**（20694/17164）。它是 AVCodecContext 通用项，落 libwebp `method(0-6)`。
⇒ 一个可用旋钮被误判成"仅无损"而藏掉。
⚠ 另：这类告警走 `Console.WriteLine`，而产品是 `WinExe` 且 src 内无 `AttachConsole/SetOut`
⇒ **GUI 下用户完全看不见这些降级告警**（只有 headless CLI 可见）。
（✅ 同时**证实**"无损时丢 `-preset`"是对的：`-lossless 1 -preset photo` 产出 `VP8` 而非 `VP8L`，真会静默退有损。）

### A-16 ⬜ APNG 能力被静默截短
只发 `-f apng -c:v apng` + fps/scale/`-plays`，**不发** `-pred/-dpi/-compression_level`。
实测同一 ffmpeg 的 apng 编码器**吃** `-pred sub`（全 1 滤波）与 `-dpi 300` ⇒ 是软件没接，不是格式不支持。
`-plays` 零门禁覆盖（实测 0/1/3 → exiftool `AnimationPlays` 各就各位，缺省 1）。

### A-17 ⬜ JXR 面板零控件 ⇒ 官方 12 个开关全默认，且有一条静默降色度悬崖
`MainWindow.xaml:758-763` 的 `JxrCodecPanel` 只有两个 `TextBlock`（主循环核实）。
实发恒为 `-i -o -q`，`JxrEncApp` 的 `-d/-f/-o/-s...` 一个不传。按官方 usage：
**`-d` 未设时 `q≥0.5→4:4:4`、`q<0.5→4:2:0`** ⇒ 主循环核 GUI 映射 `(100-q)*25/100`
⇒ **质量 ≤49 静默降到 4:2:0，无控件无提示**（⬜ 实测 q0.30 产物 == 强制 `-d 1`，PSNR 32.71 dB；`-d 3` 是 44.30 dB）。
另：面板文案称"支持 8/16/32-bit、浮点、Alpha"，而管线只产 bgr24/rgb48le/bgra/rgba64le ⇒ **32-bit/浮点无通路**。

### A-18 ⬜ JXL / cjpegli 的质量轴与后端真实域不吻合
主循环用**本机 libjxl checkout** 定权威：`tools/src/libjxl/lib/jxl/encode.cc:1626`
`JxlEncoderDistanceFromQuality: q≥100→0, q≥30→0.1+(100-q)*0.09, else 二次式`。
GUI 用线性 `MapJxlDistanceForward = (100-q)*15/100`（`FfmpegOptions.cs:495`）。
主循环实测：`cjxl -q 90` 与 `-d 1.0` 产物**等长 17352 B**（证实官方 q90≡d1.0），
GUI 在 q90 下发 `-d 1.5` = 13819 B（小 20%）⇒ **滑块显示 90，实际相当于 libjxl 自身口径 ~84**。
cjpegli 侧同理（默认 q92→d2.0 vs 原生 0.82；同一名义质量下 ⬜ 16839 B vs 24906 B，差 32%）。
⇒ 同一条滑块在 JXL 用 0–15、在 cjpegli 用 0–25，跨后端也不可比。**cjxl/cjpegli 原生都吃 `-q`**，
最简正解是直接传 `-q` 而不是自己折算。

### A-19 🔷 `cjxl --photon_noise_iso=auto` 这个形式不存在 ⇒ 命令直接跑不通
主循环独立复现：`=auto` ⇒ `rc=1 "Error parsing flag --photon_noise_iso=auto"`；`=3200` ⇒ `rc=0`（17362 B）。
⇒ 勾「自动读取 EXIF ISO」而 EXIF 无 ISO 时，编码与预览都给出一条跑不通的命令（子 agent 称代码只写"跳过"日志
而不清标志 ⇒ 仍发 `=auto`）。

**2026-09-30 主循环复案（本条当时只修了一半，现已修完）**：下面「修复状态」表曾记 `已修 / 无独立锁`，
那句"已修"只对**传统路线**成立——清标志那一步内联在 `QueueProcessor.ProcessCjxlAsync` 里，
而 `:714` 的分派写成 `backend == Cjxl && !engineRoutable` ⇒ **引擎可走（= 默认路线）根本进不到它**，
`RawColorPipeline` 的 cjxl 出口于是把未解析的 `auto` 直接发给编码器。
实机读数（`tests/output/t62/`，cjxl v0.11.2，891x981 16-bit，与用户截图同尺寸同位深）：
`--photon_noise_iso=auto` ⇒ 写入 0 B、cjxl 退出码 1、产物 0 B、写方 broken pipe；
同参数只换成 `=400` 或不给 ⇒ 退出码 0、产物 188942 / 188932 B；
单独给 `-x color_space=sRGB` ⇒ 退出码 0（排除"标注参数被拒"这条备选解释）。
**后果不是"报错"而是"挂 30 分钟"**：cjxl 提前退出后没人再读 ffmpeg 的 stdout，5.2 MB 流写满管道缓冲即
永久阻塞，而 `pa.WaitForExitAsync(token)` 的 token 只有 30 分钟超时会被触发 ⇒ 用户停在"处理中"，
最后拿到的是"超时或已取消"，真因埋在后面那行 stderr 里。
**同批实测的第二条**：`-d 0`（数学无损）叠加 `--photon_noise_iso=400` ⇒ djxl 回读 **99.46%** 的 16-bit
样本偏离源（平均偏差 30875/65535），而 jxlinfo 对两者都报 `(possibly) lossless`（噪声由解码端按帧头参数
合成，体积只多 10 B ⇒ 从文件大小查不出来）。⇒ 该组合不再发给编码器，UI 侧无损时置灰。

### A-20 ⬜ `-dct float` 是 UI 提供、ffmpeg 拒绝的
主循环核实 `MainWindow.xaml:707-710` 的 `JpegDctCombo` 含 `float`；mjpeg 的 `-dct` 合法值无 float
⇒ 子 agent 实测 `Error setting option dct to value float`、编码失败。

### A-21 ⬜ dngtool 的 `-linear` 被**静默忽略**，而 UI 与文档都写"会被引擎拒绝"
真 Bayer CR2 上加/不加 `-linear` **逐字节相同且 exit=0**。
⇒ `docs/RAW_PIPELINE_AUDIT.md §3.3` 的记录、`dng.linear.hint` 文案与 `MainWindow.xaml.cs:38/1082` 均已过期。
另：`-jxlq 100` ⇒ distance 0.00（无损），与 usage「1-100 lossy」不符；`-effort 0`/`-decode_speed 9`
⇒ dngtool **不校验范围**，exit=0 静默回落 effort 7；`--dng-jxl-decode-speed` 在 CLI 侧缺席
（`CliParser.cs:732-737` 只有 6 个 `dng-*`）；采集回退 `?? 4` 与预览回退 `?? 1` 不一致。

### A-22 ⬜「保留 CFA（Bayer 原始）」的产物不是 CFA DNG
实测 `SamplesPerPixel 3`、`PhotometricInterpretation 34892 (Linear Raw)`、**无 CFAPattern**
⇒ UI 标签与产物不符（与 `RAW_PIPELINE_AUDIT` 里 `color3_image` 那条吻合）。
`-H 0/1/2` 三产物 md5 全同 ⇒ 旧结论"高光模式对保留 CFA 的 DNG 无效"今天仍成立，且 `dng.highlight.hint` 提示诚实 ✅。

### A-23 🔷 三处代码断言"本工具链的 ffmpeg 没有 jxr 编码器"，实测**已有**
`FfmpegOptions.cs:365`、`ColorEngineRouter.cs:19/217`、`ColorIntentFactory.cs:173`、`RawColorPipeline.cs:531-565`
都写着"实测 `-encoders` 无 jxr"。主循环今日实测：`V....D libjxr  JPEG XR via jxrlib (codec jpegxr)`，
且 `DEVILS jpegxr (decoders: libjxr) (encoders: libjxr)`。⇒ 注释与 `ColorEngineRouter.cs:217` 那个
**活的路由条件**都建立在已失效的"实测"上（它们恰是 JXR 内置后端接入的拦路处，见 B 任务）。

### A-24 🔷 本构建的 `libjxr` **编码器产出无效码流**，且质量旋钮全部无作用
- 8 种像素格式（含 16-bit 与 alpha）产物**全部无法解码**：ffmpeg 自身 libjxr 报
  `jxrlib JPEG XR decode failed (error=-1)`；**独立参照**微软 `JxrDecApp` 输出 0 字节。
- 质量无控制：`-q:v 10/90`、`-compression_level 3`、`-quality 50`、`-crf 30` 六份产物**逐字节相同**；
  `-h encoder=libjxr` 打不出任何私有 AVOption。
- 失败是**静默**的：`rc=0` 且文件写出，只有去解码才暴露。
- 对照组（排除"用法错"）：`JxrDecApp` 与 ffmpeg 都能正常读现成素材 `tests/output/sources/src.jxr`
  （解出 589878 B BMP）；换 BMP 输入、加 `-update 1 -frames:v 1` 结果相同。
- 上游状态：主循环对 `master/libavcodec/codec_id.h` 做了**带阳性对照**的核查 ——
  `AV_CODEC_ID_JPEGXL` 命中 2 次（证明取法通），`JPEG_XR/JPEGXR` 命中 **0**，
  全集只有 `JPEG2000/JPEGLS/JPEGXL/JPEGXL_ANIM/JPEGXS` ⇒ **本机这条 libjxr 是带外补丁**，
  上游无 `jpegxr` 文档/codec_id/Changelog 依据（子 agent 另查 patchwork/trac 亦零命中）。
  标准本体是 ITU-T **T.832**（profile↔像素格式映射的原文细节**未取到**，本轮不作断言）。

### A-25 🔷 硬件编码器报 "No capable devices found" 是**误导性归因**（对本机也是）
主循环第一次探 NVENC 拿到 `No capable devices found`，差点据此写下"本机无 GPU 编码能力"。
实因：RGB PNG 被协商成 `yuv444p` ⇒ 真实报错是 `YUV444P not supported`，被折叠成了那句假缺席；
本机确实有 RTX 5080 + RTX 4060（`Win32_VideoController` 实证）。另一条同族：`h264_nvenc` 用 64×64
测试图会报 `Frame Dimension less than the minimum supported value` —— **探针尺寸本身要够大**。
⇒ GUI 送 RGB 源走 NVENC 会撞同一个坑，值得单独查 pix_fmt 协商。

---

## 二、主循环驳回子 agent 的结论（都记下来，免得下位照抄）

| 子 agent 说 | 主循环读数 | 结论 |
|---|---|---|
| "NVENC spatial-aq 合规" | 省略 == `-spatial-aq 0`（同哈希），传 1 才变 | ❌ 实为**惰性复选框**（A-4） |
| "默认配置下 jbrd 快速路永不生效" | 默认 `UseAdvancedCodec=false` ⇒ 否决不生效 | ❌ 表述错，但**暴露出可见性与生效条件不一致**这个更真的问题（A-10） |
| "本机有 N 卡，av1_nvenc 可真跑"（并给出 7887 B 等数字） | 主循环首跑报 `No capable devices`，真因是 `YUV444P not supported` | ✅ 结论方向成立，但**必须先配对 pix_fmt**；本文件采用主循环自己复现的数字（A-3） |
| "quality<0.5→4:2:0 只是文档说明" | GUI 映射使 q≤49 落进该域 | ⬜ 升级为确证缺陷 A-17（子 agent 自己给了 PSNR，我没复跑到那一步） |

---

## 三、审过且**合规**的（省下位重复劳动）

- libaom：`cpu-used`(0-8，UI 范围正确)、`tune psnr/ssim`、`enable-cdef`、`row-mt`（勾选=默认，取消才变）、
  `still-picture`（动图下 2.1× 体积，语义正确）、12-bit 钳制。
- SVT：`preset`(-2..13 范围内)、10-bit 钳制（传 12 自动降 10，与 10-bit 同哈希）。
- libjxl(ffmpeg)：`effort` 1-9、`modular`、`lossless→-d 0`（像素级验证无损）、
  `lossless_jpeg` 的能力探测门有效（该 ffmpeg 无此 AVOption ⇒ 探测返回 false ⇒ 不会误发）。
- mjpeg：`-huffman`、`-dct int/fastint`；渐进下拉**有意隐藏**且注释点名"mjpeg 不支持渐进"（✅ 诚实）。
- cjpegli：`-p` 映射（-1 不发、0/2 正确且 `-p 2` 更小）、`--fixed_code` 与 baseline 的强绑、
  `--chroma_subsampling` 五项逐值吻合、`--noadaptive_quantization` 生效；
  `psnr-target`/`jpeg_encoder(sjpeg)`/`num_threads` **面板已隐藏且不发**，而 cjpegli 确实无这些参数（✅ 一致）。
- PNG `-pred` 六档逐档自洽；PNG/TIFF `-dpi` 属元数据-only（像素 md5 不变，**合法**）；
  TIFF 四算法（raw1/lzw/deflate/packbits，**无 zip/ppdcb**）+ 16-bit 出口 + 预设端到端闭环（本日刚修）。
- APNG：`-plays`/fps/scale/`-t` 时长均生效；WebP：`-preset` 五档、`-loop` 写 ANIM、无损丢 preset 正确。
- 动图轴单一真值 `IsAnimationMode()` 门控有效：静态模式下隐藏面板的残留值**确实不进命令行**。

---

## 四、未验清单（诚实标注）

- 全部**硬件产物**层面（QSV/AMF/VAAPI 无对应硬件；`av1_vaapi` 在本 Windows 构建的 `-encoders` 零命中
  ⇒ VAAPI 面板本机永不可达）；只有参数拼装/取值域层面的判据。
- GUI 目视（本轮只跑无头宿主，未截图）。
- 4K 素材、16-bit/HDR 素材上的 JXL 全链、`RawColorPipeline:466` 的 cjxl 色彩组合与 `--intensity_target`。
- `aq-mode=complexity` 是否已被 libaom 上游彻底弃用（只拿到"同哈希"的实测，未拿到文稿）。
- cjxl 官方 patch series / jxrlib 仓库 URL **未取到**（不凭记忆写地址）。

---

## 五、建议修复顺序（按"用户会不会被静默骗"排）

1. **A-1 + A-2**（无损被丢 + 状态说谎）：同一处代码，一行搬运 + 一处状态条件，影响真实产物。
2. **A-6 / A-12 / A-20 / A-19**（选了必崩、无损是假的、必然失败的参数值）：都是"响亮失败"级别的正确性洞。
3. **A-3 / A-4 / A-5 / A-18**（质量轴与硬件后端）：一个改动可同时修掉"滑块无效 + 复选框惰性 + 0 越界 + 质量口径"。
4. **A-7**（中文字面量当键）：一次性把 9 处换成 token 比对，并给 `_probe-i18n-scan.ps1` 加"值比对"判据。
5. **A-9**（参数分域下发）：按编码器建"参数白名单"，顺带把 A-11 的死控件删掉。
6. **A-13 / A-14 / A-15 / A-16**（GIF/PNG/WebP/APNG 语义与能力）：文案与实发一起对齐官方默认值。
7. **A-17**（JXR 面板零控件 + 4:2:0 悬崖）与 **A-21/A-22**（dngtool 静默忽略、Linear Raw 冒充 CFA）：
   都属于"宣称>交付"，要么补控件要么改文案。
8. **A-8**（DNG/JXR 预览手拼）：并入本日刚立的唯一采集点。
9. **A-23/A-24**（libjxr 事实修正 + 往返自检闸门）：与 JXR 内置后端接入同批做。
10. **告警通道**（A-15 附带）：`Console.WriteLine` 在 `WinExe` 下无处送达 ⇒ 降级告知要改走 item.Log/状态栏。

---

## 六、修复状态（同日实现批次，4 条并行 lane + 主循环集成）

| 条目 | 状态 | 锁 |
|---|---|---|
| A-1 队列丢无损 / A-2 状态说谎 | **已修**：JPEG 分支不再另起搬运清单，两分支同传 `item.Options` | ⚠ 无端到端锁（见下） |
| A-3 硬件后端吃 `-crf` | **已修**：按后端分轴（NVENC `-cq` / QSV·VAAPI `-global_quality` / AMF `-rc cqp`） | `R1a-h`、`R9a-b` |
| A-4 spatial-aq 惰性 | **已修**：勾选显式发 `1` | `R4a-b` |
| A-5 `aq-strength` 允许 0 | **已修**：XAML 下限 1 + 采集侧钳 `[1,15]` + 未开空间 AQ 时不发 | `R4c-e` |
| A-6 SVT tune 错位 / libaom 假 tune 值 | **已修**：按库自报表 0 基映射；`-svtav1-params` 合并成一次出现 | `R2a-f`、`R3d-e` |
| A-7 中文字面量当参数键 | **已修**：显示串统一过 `NormalizeTuneToken` | `R3a-d` |
| A-9 参数不分域 | **已修**：libaom 私有项只发 libaom | `R1c`、`R9a-b` |
| A-12 SVT 无损是假的 | **已修**：发 `lossless=1`；**新发现** `tune=4/5` 会把 lossless 打掉 ⇒ 无损时只放行 0/1/2 | `R2f` |
| A-13① GIF 关不掉抖动 | **已修**：`dither=none`，**两处拷贝同改**（`ImageEncoderArgs` 与 `QueueProcessor` 的调色板链） | `R6a-b` |
| A-14 PNG 无损绑死 level 0 | **已修**：解绑，级别按质量下发（强制无损本身未动，仍由 J9a/L2 钉） | `R7a-c` |
| A-15 WebP 压缩级别适用范围 | **已修**：有损/动图同样放行（钳 0-6） | `R8a` |
| A-16 APNG 不发 pred/dpi | **已修**：补发 | `R8b-c` |
| 附带发现：静帧 `-still-picture` 永不可达 | **已修**（旧判据 `AnimationLoop != 0` 在静帧恒真） | `R5` |
| A-19 `photon_noise_iso=auto` | **09-26 只修了传统路线 ⇒ 09-30 修完**：ISO 解析上提到 `:714` 分叉之前（三条 cjxl 出口共用一份已解析 options）；`BuildCjxlArguments` 加 fail-closed 兜底（真执行宁可省略也**不发占位串**，`auto` 只留在预览）；无损（distance=0）时不发噪声并点名；管道传输失败立刻杀 ffmpeg 并把编码器 stderr 带进异常（不再等 30 分钟报"超时"） | `wire ①-⑤`、`(10g)`×6、`J9d-a~d`（各含正控与变异验牙） |
| A-20 `-dct float` | **已修**：从下拉移除（历史预设/CLI 写 float 会原样透传并响亮失败） | **10-01 补上独立锁**：`verify-encoder-knob-liveness.ps1`（清单第 65 条）的 `K jpg --jpeg-dct` 臂实测 `lo rc=0 / hi rc=1` ⇒ 越界值确实被**响亮**拦下；另有 `N jpg --jpeg-dct float` 命名臂钉住"要么非零退出、要么有播报，只有 rc=0 且全程没一句话才判红"。⚠ 本批同时纠正该套件自己写错的一句标签（旧文本把这条说成"**面板值域**被拒绝 = 缺陷复现"，而 float 已不在下拉里 ⇒ 改记 `WARN 越界被拒(不计入)`，并取消"没被拒就直接记活"的捷径） |
| A-26（09-30 新增，无独立小节，账在本表；⚠ 与上面第三节的 A-25「No capable devices」不是同一件事）jbrd 从未被激活 | **已修**：`JxlLosslessJpeg` 默认 `false` + 采集式「面板不可见⇒false」+ 与默认勾着的「保留 Ultra HDR」无条件相与，**三道**叠加 ⇒ 默认走 `-d 3.8` 有损重编码而用户以为无损；且 95 个门禁里**没有一条**问过 app 自己发没发 `--lossless_jpeg=1`。现开关默认启用、与「输入真是 JPEG」相与成**有效值**、JPEG/JXL 两格共用一只控件 | `⑩`×7（含 jxlinfo 独立读回、PNG 负控、XAML 单份结构锁）、`jbrd-①~⑥`、`J16a/b` |
| A-27（09-30 新增，同上无独立小节）ffmpeg-libjxl 用 `-distance 0` 冒充 jbrd ⇒ 质量轴被吞 | **已修**：删掉 `FfmpegCommandBuilder` 那条特例，jxl 质量档一律走 `BuildJxlOptions`；「该后端做不到 jbrd」由任务日志点名（实测四档产物逐字节同尺寸 59,685 B ⇒ 现各档不同） | `qa-①②③`、`⑪`（`EncoderUnsupportedSetting` 的 jxl 格首次真跑，四臂两两只差一个变量） |
| A-28（10-01 新增）jbrd 默认翻转带出的**取向回归** | **已修**：`JxlLosslessJpeg` 的有效值只与到"输入是 JPEG"，而同一个布尔又被元数据恢复当作"这次像素没动"的唯一判据（`CjxlService.UsesLosslessJpegRewrap`）⇒ JPEG→JPEG/PNG/WebP/AVIF 也把 `Orientation=8` 留在已旋正的像素上 = 查看器二次旋转。出货包实测四格全中（源 coded 640x480 → 产物 480x640 且标签仍 8）；补上"目标必须是 jxl"这一与后四格标签均丢，jxl(jbrd) 仍正确带走取向 | `verify-color-wiring (12)`×8（含夹具自检与反真空臂）、`verify-jxl-codestream-route ⑫`×4 |
| A-29（10-01 新增）app 自产 HDR/float JXL **无法回灌** | **已修（10-01 同日，两半都在产品里）**：① 反向管道 —— `QueueProcessor` 的 djxl→PNM 中转失败支原本 `return false`（任务静默失败），现改走 **ffmpeg 直读 `.jxl`** 并在终态串点名用的是哪条路（djxl 的 PPM 出口不支持 float 图像 = `JxlDecoderSetImageOutBitDepth failed` 的根因）；② 正向不再冒充无损 —— `CjxlService.UsesLosslessJpegRewrap` 加第五条判据 `!JpegCarriesGainMap(源)`（读头 256 KB 由 `GainMapDecoder.JpegContainer` 判定，512 项缓存），带增益图的 JPEG 一律 `--lossless_jpeg=0` 且命令行/日志**诚实命名**，不再"宣称无损而把增益图抹平" | `verify-jbrd-e2e.ps1`（清单第 62 条）ultrahdr 臂：无 reconstruction 盒 + 全条命令里查不到 `--lossless_jpeg=1` + 反向恰好取回一件 + **往返像素必须不逐比特相同**（反真空）；float 回读臂走 `已完成 (ffmpeg 直读 JXL)` |
| A-30（10-01 新增）`--jxl-modular` 在 cjxl 后端静默丢弃 | **已修**：`BuildCjxlArguments` 现发 `--modular=1`；它与 jbrd **结构互斥**（jbrd 依赖 varloss 通道），两者同时给出时 **modular 获胜并播报**这一覆盖关系（不是静默二选一）。原条目里"面板 `JxlFfmpegPanel` 已按后端收窄 ⇒ UI 用户碰不到"那句仍然成立，但 CLI 一侧的"设了不生效且无声"已消除 | `verify-jbrd-e2e.ps1` 的 G1–G5（含反真空臂"勾与不勾产物 SHA 必须不同"）+ `verify-encoder-knob-liveness.ps1` 的 `K jxl --jxl-modular` 臂（修复前读数：两端 SHA 相同 `97AFF2ED823C1FC2`、45 条 `[cmd]` 里 `modular` 命中 0） |
| A-18 质量轴与后端真实域不符（JXL/cjpegli 的 q↔d 折算） | **未修**（A-27 修的是「被常量吞掉」，本条是「折算公式与 libjxl 自己的域不同」，两者不是同一件事） | — |
| A-13② GIF 表达不出"不循环" | **未修**（不能简单恒发 `-loop`：采集侧给静帧恒填 `-1` ⇒ 会把所有默认 GIF 摘掉 NETSCAPE、动图输入变播一次。要做须把 `AnimationLoop` 改可空哨兵，会连带重写上面刚改的 `isAnimated` 判据 ⇒ 单独一批） | — |
| A-17 JXR 面板零控件 / q≤49 静默 4:2:0 | **未修**（需界面决策与新增双语串） | — |
| A-21 / A-22 dngtool 文案过期、"保留 CFA"实出 Linear Raw | **未修** | — |
| A-23 / A-24 libjxr | **未实现**，见 `docs/JXR_LIBJXR_INTEGRATION_PLAN_2026-09-26.md` | — |
| 遗留死字段 `AvifHwPreset`（`HwPresetCombo` 已不在 XAML） | **未删**：要扫 MainWindow/PresetData/FfmpegOptions/Apply/CliParser 5 处，与本日的预设完整性面重叠 ⇒ 单独一批 | — |

**本轮验证读数（必须连来源一起看）**
- `UiTestHost`（**Debug 产物直跑宿主**，不是门禁绑定）：**388 PASS / 0 FAIL**、`exit=0`，
  其中新增 **R 组 35 条**（日志 `tests/output/rgroup-restored.log`）；Debug 两项目 **0 警告 0 错误**。
- 变异验牙：往 `BuildAvifOptions` 强行加发 `-crf` ⇒ `R1b`、`R1d` 转红（`386/2`）；还原后回到 388/0，
  `grep -c MUTATION` = 0（`tests/output/rgroup-mutation.log`）。
- ⚠ **整轮绑定至今未取得**，两次都是环境问题、不是代码问题：
  · 第一次尝试：本会话一次 `dotnet build -t:Rebuild` 撞上并发会话占用同一份 bin
    （另一会话的 `--headless` 色彩批跑，PID 76372，14:41 起）⇒ 删得掉、回拷被拒，
    `bin/Release/.../FfmpegGui.dll` 一度缺失。已重建修复（四份 dll 一致）。
    教训：共享工作树里**不要** `-t:Rebuild`，也不要为解锁去结束别人的进程。
  · 重跑那一轮（`tests/output/gates-run-encoding-fixes-2026-09-26.log`）被**运行器自己判作废**：
    「源码漂移：跑期间源码被改动 ⇒ 本轮结论不对应当前工作区」。计数 4 项，逐条归因后
    **没有一条来自本会话的改动**（时间线：本会话最后源码改动 15:32:13 ＜ Release 构建 15:35:53 ＜
    并发会话改 `ColorIntentFactory.cs` 15:54:01 与 `ColorStrategyPlanning.cs` 15:55:04）：
    ① ② `verify-ipc-extra-options` 两条（`exit=1` + 零断言）＝ 跑时有 3 个 `FfmpegGui` 实例活着，
      该门禁按既有规矩 fail-closed 拒跑（不是产品缺陷）；
    ③ `verify-color-strategy` F3（`PASS=70 FAIL=1`）＝ webp/avif 位深零播报，消息自标「任务#35 未修完」，
      点名 `Decision.cs:151` / `ColorIntentFactory.cs:145` —— 正落在并发会话那条 lane；
    ④ 漂移判定本身。
  · 那一轮里**有效**的信号：5 条 UI 门禁跑的都是新码 `399 PASS / 0 FAIL`；avif / webp / jxl / gif /
    tiff / pipeline / heartbeat / geometry / png3 等产物型门禁全部 `exit=0`
    ⇒ 担心的"参数分域改动撞 golden 参照"没有出现。
  · 第二轮（`tests/output/gates-run-encoding-fixes-round2.log`，Release 重建后跑）：54 条里**只有 1 条非零**
    = `verify-ipc-extra-options`（另有一条同脚本的"零断言"重复计数），原因是当时有 3 个 `FfmpegGui.exe`
    活着、管道名按用户 SID 派生会串台 ⇒ 该门禁 fail-closed 拒跑。那 3 个实例的 exe 路径全在
    `.qoder/worktrees/agent-general-purpose-ab7631cb/`（**另一个会话的 worktree**，已挂两个多小时），
    不属本会话，也未去结束它们。
  · 上一轮那条 `verify-color-strategy` F3（webp/avif 位深零播报）在这一轮**自己转绿**
    ⇒ 反证它是并发会话改到一半的产物，与本表的修复无关。
  · UI 宿主在 Release 产物上 **399 PASS / 0 FAIL**；其余 53 条（含 avif/webp/jxl/gif/tiff/png3/
    geometry/heartbeat/pipeline/cli-strict 等产物型与 golden 型）全部 `exit=0`
    ⇒ 本表的参数分域修复**没有撞坏任何既有参照**。
  · 但整轮**仍被运行器判作废**（`ColorStrategyPlanning.cs` 在跑期间被改）。⇒ 绑定读数要等
    这台工作树既没有并发源码写入、也没有别的会话的 `FfmpegGui` 实例活着时再取一次。
- ⚠ A-1/A-2 只有代码级证据，**没有端到端产物判据**。要补的那条：同一 JPEG 输入、勾无损、关 jbrd
  ⇒ 产物与手工 `cjxl -e 7 -d 0 --lossless_jpeg=0` 参照逐字节相同，且 `item.Command` 与实发一致。
