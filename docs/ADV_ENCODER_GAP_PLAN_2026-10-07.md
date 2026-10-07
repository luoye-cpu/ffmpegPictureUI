# 高级编码选项缺口盘点（2026-10-07）

**问题**：本仓工具链**真的支持**、但 UI 里**没有对应控件**的高级编码选项有哪些？

**唯一权威来源 = 本仓实际二进制**（不采信网上的选项表；版本差异会让外部表失真）。

| 工具 | 路径 | 实测版本 |
|---|---|---|
| ffmpeg | `publish/PLAN/ffmpeg-full/ffmpeg.exe` | `git-2026-09-26-8864fd0aec`（libavcodec 63.14.100） |
| cjxl / cjpegli / jxlinfo | `publish/PLAN/jxl/bin/` | JPEG XL encoder **v0.11.2** `332feb1` |
| avifenc | `publish/PLAN/artifacts/avifenc.exe` | **1.4.2**（dav1d 1.5.3 / aom **3.14.1**） |
| JxrEncApp | `publish/PLAN/artifacts/JxrEncApp.exe` | Copyright 2013 Microsoft |
| dngtool | `publish/PLAN/artifacts/dngtool.exe` | DNG SDK **1.7.1**；`libraw: yes` / `dngsdk: yes (DNG 1.7 JXL support)` |

**方法**：① `ffmpeg -h encoder=<name>` 抄下该编码器**私有 AVOption 全集**；② 外部工具 `--help` / `-v -v --help` / 无参调用；③ 与 `src/FfmpegGui/MainWindow.xaml` 里**实际存在的 221 个 x:Name 控件**逐一对照（不是与记忆对照）；④ 每个候选缺口**真跑一次**，能出产物才算数。

> ⚠ **判据纪律**：本文一律区分「**选项被接受**（rc=0）」与「**选项真的改变了产物**（字节/哈希不同）」。只有后者才算"可用旋钮"，只有前者是"惰性参数"。凡未实测到的一律标 **未验证**。

---

## 〇、结论速览

- **真缺口（值得加）7 条**：`-rc`/`-qp` 码控、`-multipass`、`-tune uhq`、`-rgb_mode`（NVENC）；`--faster_decoding`、`--resampling`/`--epf`/`--gaborish`（cjxl）；avifenc 的 `--progressive`/`--target-size`/`-a`（高级字典）。
- **最高优先级 3 条**：① NVENC 无 `-rc` ⇒ 用户拿不到 constqp/VBR/CBR 三档；② avifenc `--target-size` ⇒ **唯一能"按体积交付"** 的旋钮；③ cjxl `--faster_decoding` ⇒ 解码端性能旋钮，本仓已实现编码端却漏了解码端。
- **"该删的"**：**0 条**（历史上 A-20 的 `-dct float` 已删干净，见 §四）。
- **"有意不加"（附理由）**：libaom 的 ~40 个 `enable-*` 调色板、NVENC `-temporal-aq`（**实测惰性**）、`-gpu`、`--container` 等。
- **本轮新发现（重要）**：`-progressive` 这个选项在本构建的 ffmpeg 里**根本不存在**（详见 §五.1）——这不是缺口而是**必须避免接线的陷阱**。

---

## 一、主表：编码器 / 真实可用选项 / UI 是否已暴露 / 缺口 / 实测证据

图例：✅已暴露 · ⚠️缺口(真) · ⛔有意不加 · ➖不适用/本机无硬件

### 1.1 NVENC（`av1_nvenc`）— **最大缺口区**

本机实证有卡（`-gpu list` 原始输出）：
```
[av1_nvenc @ ...] [ GPU #0 - < NVIDIA GeForce RTX 5080 > has Compute SM 12.0 ]
[av1_nvenc @ ...] [ GPU #1 - < NVIDIA GeForce RTX 4060 Laptop GPU > has Compute SM 8.9 ]
```

| 真实可用选项（自报域） | UI 控件 | 判定 | 实测证据（原命令 → 结果） |
|---|---|---|---|
| `-preset` (0..9, 默认 p4) | `NvencPresetCombo` | ✅ | — |
| `-cq` (0..63) | 质量滑块（经 `-cq` 下发） | ✅ | `ImageEncoderArgs.cs:482`；cq30=5771B vs cq50=2397B |
| `-tune` hq(1)/uhq(5)/ll/ull/lossless | 无 | ⚠️**缺口** | 见 §二.A2 |
| `-rc` constqp(0)/vbr(1)/cbr(2) | 无 | ⚠️**缺口** | 见 §二.A1 |
| `-qp` (-1..255) | 无（仅 constqp 下有意义） | ⚠️**缺口** | 见 §二.A1 |
| `-multipass` disabled/qres/fullres | 无 | ⚠️**缺口** | 见 §二.A3 |
| `-rgb_mode` yuv420/yuv444/disabled | 无 | ⚠️**缺口** | 见 §二.A4 |
| `-rc-lookahead`, `-lookahead_level`, `-tf_level` | 无 | ⛔ | 视频向；静帧无 lookahead 收益 |
| `-temporal-aq` | 无 | ⛔**实测惰性** | 见 §三.T1 |
| `-spatial-aq` / `-aq-strength` | `AvifNvencSpatialAqCheck` / `AvifNvencAqBox` | ✅ | （A-4/A-5 遗留问题另计） |
| `-level` / `-tier` / `-profile` | 无 | ⛔ | 容器/兼容性向，静帧用不满 |
| `-gpu` | 无 | ⛔ | 见 §三.T2 |

### 1.2 libaom-av1 / libsvtav1

| 真实可用选项 | UI 控件 | 判定 | 证据 |
|---|---|---|---|
| libaom `-crf`(-1..63) `-cpu-used`(0..8) `-tune` psnr/ssim | `AvifCpuUsedBox` `AvifTuneCombo` | ✅ | psnr=9881B vs ssim=8254B（真生效） |
| libaom `-usage` good/realtime/allintra | 经 IQ tune 间接使用 | ✅ | `ImageEncoderArgs.cs:541` |
| libaom `-row-mt` `-enable-cdef` `-enable-intrabc` `-still-picture` `-aq-mode` | 各自复选框 | ✅ | — |
| libaom `-denoise-noise-level` | `AvifDenoiseBox` | ✅ | 0=9881B vs 20=10131B（真生效） |
| libaom `-enable-restoration` | 无 | ⛔**实测同尺寸** | 0/1 均 9881B ⇒ 该素材上无差异，收益不确定 |
| libaom ~40 个 `enable-*-partitions` / `enable-*-intra` / `enable-*-comp` | 无 | ⛔ | 见 §三.T3 |
| **libsvtav1 `-qp`** (-1..63) | 无 | ⚠️**缺口** | 见 §二.A5 |
| **libsvtav1 `svtav1-params lossless=1`** | 无（"无损"勾选只发 `-crf 0`） | ⚠️**缺口** | 见 §二.A6（**语义缺陷**） |
| libsvtav1 `-preset`(-2..13) | `SvtPresetBox` | ✅ | — |
| libsvtav1 `-dolbyvision` | 无 | ⛔ | 需 RPU 输入，本管线不产 |

### 1.3 cjxl（v0.11.2，`-v -v --help` 共 116 行选项）

| 真实可用选项 | UI 控件 | 判定 | 证据 |
|---|---|---|---|
| `-d` / `-q` / `-e`(1..10) | `CjxlEffortBox`（仅到 9，见 §二.C5） | ✅/⚠️ | `-e 10` 实测 rc=0，24291B |
| `-p, --progressive` | `CjxlProgressiveCheck` | ✅ | `CjxlService.cs:493` |
| `-m, --modular` | `JxlModularCheck` | ✅ | `CjxlService.cs:440` |
| `-j, --lossless_jpeg` | `JxlLosslessJpegCheck` | ✅ | `CjxlService.cs:462` |
| `--photon_noise_iso` | `CjxlPhotonNoiseBox` | ✅ | `CjxlService.cs:512` |
| `--intensity_target` | 内部（HDR 出口算好） | ✅ | `CjxlService.cs:555/600` |
| `-x color_space / icc_pathname` | 走色彩面板 | ✅ | `CjxlService.cs:549/594` |
| `--num_threads` | `ThreadsBox` | ✅ | `CjxlService.cs:452` |
| **`--faster_decoding=0..4`** | 无 | ⚠️**缺口** | 见 §二.C1 |
| **`--resampling=-1\|1\|2\|4\|8`** | 无 | ⚠️**缺口** | 见 §二.C2 |
| **`--ec_resampling`** | 无 | ⚠️**缺口** | 同 C2 |
| **`--epf=-1\|0..3`** | 无 | ⚠️**缺口** | 见 §二.C3 |
| **`--gaborish=0\|1`** | 无 | ⚠️**缺口** | 见 §二.C3 |
| `--group_order` / `--center_x/y` | 无 | ⛔ | 仅在 progressive 下有观感差异 |
| `--container` / `--compress_boxes` / `--brotli_effort` | 无 | ⛔ | 容器开销级；对图片用户无感 |
| `--codestream_level` | 无 | ⛔ | 兼容性向，默认最宽 |
| `--premultiply` / `--keep_invisible` | 无 | ⛔ | 与既有 alpha 语义冲突，见 §三.T4 |
| `--already_downsampled` / `--upsampling_mode` | 无 | ⛔ | 需配套 --resampling 才安全 |
| `-a, --alpha_distance` | 无 | ⛔ | 本仓 alpha 走无损约定，见 §三.T5 |

### 1.4 cjpegli

| 真实可用选项 | UI 控件 | 判定 | 证据 |
|---|---|---|---|
| `-q` / `-d` | 质量滑块 | ✅ | — |
| `-p 0..2` | `JpegliProgressiveCombo` | ✅ | `-p 0`=10623B / `-p 2`=10075B |
| `--chroma_subsampling=444\|440\|422\|420` | `JpegliChromaCombo` | ✅ | 444=27430 / 440=22446 / 422=23436 / 420=22030 |
| `--noadaptive_quantization` | `JpegliAdaptiveQuantCheck` | ✅ | — |
| `--fixed_code` | 经 Optimize 间接 | ✅ | — |
| `--num_reps` | 无 | ⛔ | 纯基准测试用，不改变交付产物语义 |
| `--std_quant` | 无 | ⛔ | 见 §三.T6 |
| `--xyb` | 无 | ⛔ | 见 §三.T7 |
| `-x icc_pathname/color_space` | 走色彩面板 | ✅ | — |

### 1.5 avifenc 1.4.2（**整条 `gif→avif` 两步法专用管线**）

现状：`QueueProcessor.cs:3852` 只发 `-q / -s / -j` + 帧名 + 输出名 —— 即 **3 个旋钮**。而 `--help` 列出 **40+ 个**。

| 真实可用选项 | UI 控件 | 判定 | 证据 |
|---|---|---|---|
| `-q/--qcolor` `-s/--speed` `-j/--jobs` | 质量/单线程 | ✅ | `QueueProcessor.cs:3852` |
| **`--progressive`** | 无 | ⚠️**缺口** | 见 §二.B1 |
| **`--target-size S`** | 无 | ⚠️**缺口** | 见 §二.B2 |
| **`-a/--advanced KEY=V`**（aom 字典：`aq-mode` `cq-level` `end-usage` `sharpness` `tune` `enable-chroma-deltaq`） | 无 | ⚠️**缺口** | 见 §二.B3 |
| **`--qalpha Q`** | 无 | ⚠️**缺口** | 见 §二.B4 |
| `-d/--depth 8\|10\|12` | 无 | ⚠️**缺口** | 见 §二.B5 |
| `-y/--yuv` `--sharpyuv` `-r/--range` | 无 | ⛔ | 与色彩面板真值冲突，见 §三.T8 |
| `--cicp/--nclx` `--icc` `--exif` `--xmp` | 色彩/元数据面板代管 | ✅ | `ColorTransformPlan.cs:579` |
| `--tilerowslog2` `--tilecolslog2` `--autotiling` | 无 | ⛔ | 自动分块已是默认且更优 |
| `-l/--lossless` | `LosslessCheck` | ✅ | — |
| `--grid` `--layered` `--scaling-mode` `--duration` `-k` | 无 | ⛔ | 多输入/序列特性，本管线单图语义 |
| `--pasp` `--crop` `--clap` `--irot` `--imir` `--clli` | 无 | **另见 §五.2（属元数据缺口，不属编码选项）** |
| `--min/--max/--minalpha/--maxalpha` | 无 | ⛔ | **已被官方标记 Deprecated**（help 原文） |

### 1.6 其他编码器

| 编码器 | 真实可用选项 | UI | 判定 |
|---|---|---|---|
| `mjpeg` | `-huffman` `-dct`(auto/int/fastint) `-force_duplicated_matrix` `-quantizer_noise_shaping` `-noise_reduction` `-ps` | `JpegHuffmanCombo` `JpegDctCombo` | ✅（其余 ⛔ 见 §三.T9） |
| `png`/`apng` | `-dpi` `-dpm` `-pred`(0..5) | `PngPredCombo` `PngDpiBox` | ⚠️`-dpm` 见 §二.D1 |
| `libwebp`/`libwebp_anim` | `-lossless` `-preset` `-cr_threshold` `-cr_size` `-quality` | `WebpPresetCombo` `WebpCompressionBox` | ⚠️`-cr_threshold` 见 §二.D2 |
| `tiff` | `-dpi` `-compression_algo`(raw/lzw/deflate/packbits) | `TiffCompressionCombo` `TiffDpiBox` | ✅ |
| `gif` | `-gifflags`(offsetting/transdiff) `-gifimage` `-global_palette` | `GifPaletteCheck` `GifDitherCheck` | ⛔ 见 §三.T10 |
| `libjxl`/`libjxl_anim`(ffmpeg) | `-effort`(1..9) `-distance` `-modular` **`-xyb`** | `JxlEffortBox` `JxlModularCheck` | ⚠️`-xyb` 见 §二.D3 |
| `libjxr` | **无任何私有 AVOption**（`-h encoder=libjxr` 只有 2 行头） | `JxrCodecPanel`（0 控件） | ⚠️ 见 §二.D4 |
| `JxrEncApp` | `-q` `-c` **`-d`** `-l` **`-f`** **`-p`** **`-V`** **`-H`** **`-U`** `-b` `-a` `-Q` `-F` **`-s`** | 0 控件 | ⚠️ 见 §二.D4 |
| `dngtool` | `-lossless` `-jxl[-q]` `-effort` `-decode_speed` `-linear` | `Dng*` 面板 | ✅（已全覆盖） |

---

## 二、真缺口：逐条建议（为什么值得加 / 接线面 / 优先级）

### A. NVENC 码控与调优（最高优先）

#### A1 ⚠️**P0** — NVENC 无速率控制模式选择（`-rc`），且无 `-qp`

- **为什么值得加**：UI 只有一条 `-cq` 恒量质量轴。而 `-rc` 是 NVENC 的**模式开关**，三档产物差异巨大（512×512 同源同构建）：`vbr`=11877B / `cbr`=22727B / `constqp`=7928B，**MD5 两两不同**（`82C75E93…` / `31F81137…` / `20C20DAD…`）。具体问题：① 交付要求**限定体积上限**时用户拿不到 `cbr`；② 要**可复现的固定 QP**（跨机比对、回归基线）时 `cq` 不可控，必须 `constqp`+`-qp`（实测 `-qp 20`=18340B vs `-qp 45`=14666B，单调生效）。
- **接线面**：`AvifCodecPanel` → `NvencAvifPanel` 内新增 `NvencRcCombo`（`自动/cqp/vbr/cbr`）；`FfmpegOptions` 加 `NvencRc`；下发点在 `ImageEncoderArgs.BuildAvifOptions` 的 `case AvifBackend.Nvenc:`（现 `:477-490`），紧邻 `-cq` 之前。`constqp` 时改发 `-rc constqp -qp <MapAvifCrf>`（复用已有 `crf` 变量，域 1..63 已在 `-cq` 处钳好）。
- **优先级**：**P0**。理由：这是**码控语义**缺口（不是调参锦上添花），且现成 `crf` 变量可直接复用，改动面极小。
- **是否"有意不加"**：否。`-rc` 是 NVENC 一等公民选项，且本仓已在同类后端（AMF）发了 `-rc cqp`（`ImageEncoderArgs.cs:506`）⇒ **NVENC 不发 `-rc` 是口径不一致**，不是设计取舍。

#### A2 ⚠️**P1** — NVENC `-tune uhq`（超高质量档）

- **为什么值得加**：`-tune uhq` 实测**真改变产物**（`hq`=11877B vs `uhq`=12097B），是 NVENC 官方为"静态图片质量优先"提供的一档。对"图片工具"这一产品定位，`uhq` 比默认 `hq` 更贴题。
- **接线面**：`NvencAvifPanel` 加 `NvencTuneCombo`（`默认/hq/uhq`，**不要**暴露 `ll`/`ull`/`lossless`——那是延迟/视频语义，且 `lossless` 在本构建 AV1 上实测 `not supported`）；下发点在 `-cq` 附近。
- **优先级**：P1。
- **是否"有意不加"**：否，但要注意**只暴露 hq/uhq 两档**，其余三档属有意不加（见 §三.T11）。

#### A3 ⚠️**P2** — NVENC `-multipass qres|fullres`（两遍编码）

- **为什么值得加**：实测三档单调递增且互异：`disabled`=11877B / `qres`=12097B / `fullres`=12565B ⇒ 是"用时间换质量"的正经旋钮。**注意反直觉**：该选项在本编码器上**体积变大**是质量更高的表现（同 QP 下保留更多细节），不是变差。用户做**交付级单图**时会愿意开 `fullres`。
- **接线面**：`NvencAvifPanel` 加 `NvencMultipassCombo`（`关闭/qres/fullres`，默认关闭）。
- **优先级**：P2（收益真实但非必需；且语义需在 hint 里说清"更慢、通常更大但更好"）。
- **是否"有意不加"**：否。

#### A4 ⚠️**P2** — NVENC `-rgb_mode yuv444`（RGB 输入保真）

- **为什么值得加**：**这条直击本仓一个已确证的失败模式**。实测把 RGB PNG 直接喂 `av1_nvenc` 会报：
  ```
  [av1_nvenc @ ...] YUV444P not supported
  [av1_nvenc @ ...] No capable devices found
  ```
  而 `av1_nvenc` 自报支持 `bgr0 bgra rgb0 rgba gbrp …`，并有 `-rgb_mode yuv444` 专门处理"packed RGB 输入"。加上它可让**无 `-vf format=` 的 RGB 直通路径**有救，也顺带消掉那句**误导性的** "No capable devices found"（A-25 记录过同一坑）。
- **接线面**：`NvencAvifPanel` 加 `NvencRgbModeCombo`（`默认(yuv420)/yuv444/disabled`）；更稳妥的做法是**内部自动**在输入为 packed RGB 时补 `-rgb_mode yuv444`，不给控件。
- **优先级**：P2（若做自动，升 P1）。
- **是否"有意不加"**：否。注：本机确实有 2 张 N 卡，那句"无可用设备"是**错误的归因**。

#### A5 ⚠️**P1** — libsvtav1 的 `-qp`（独立于 `-crf` 的质量轴）

- **为什么值得加**：`libsvtav1 -h` 同时列出 `-crf`(0..63) 与 `-qp`(0..63)，二者**语义不同且产物不同**：同值 40 下 `-crf 40`=5688B vs `-qp 40`=9174B。UI 只走 `-crf`。需要精确 QP 控制（如匹配其他工具产线）时无路可走。
- **接线面**：`SvtAvifPanel` 加 `SvtRateModeCombo`（`CRF/QP`）+ 复用现有质量滑块值；下发点在 `ImageEncoderArgs.BuildAvifOptions` 的 SVT 分支。
- **优先级**：P1。
- **是否"有意不加"**：否。

#### A6 ⚠️**P0（语义缺陷，不只是缺控件）** — SVT 的"无损"是假的，缺 `svtav1-params lossless=1`

- **为什么值得加**：这是**宣称与交付不符**。实测（512×512，`-preset 8`）：
  - `-crf 0` ⇒ 6933B，PSNR **46.12 dB**（**有损**）
  - `-svtav1-params lossless=1` ⇒ 27911B，PSNR **inf**（**真无损**）
  ⇒ UI 勾"无损"在 SVT 后端上**得不到无损**。这不是"选项缺失"，是**功能承诺不成立**。
- **接线面**：无需新控件。在 SVT 分支检测 `o.Lossless` 时，把 `lossless=1` 并入已有的**单一** `svtav1-params` dict（`ImageEncoderArgs.cs:472` 起，注意该文件已注明**重复出现是整体替换**，必须合并成一次 `:` 连接）。
- **优先级**：**P0**（与 A1 并列最高）。这是"用户勾了无损却拿到有损"的正确性问题。
- **是否"有意不加"**：否。libaom 侧 `crf 0` 已实测 = inf（真无损）⇒ 两条后端口径必须一致。

### B. avifenc 两步法管线（整体欠暴露）

#### B1 ⚠️**P2** — avifenc `--progressive`

- **为什么值得加**：为 AVIF 生成**渐进式渲染**分层（单帧输入即可），实测 rc=0、8214B。用于"大图先出缩略预览"的观感。注意：与 **ffmpeg 那个不存在的 `-progressive`** 完全不同（见 §五.1），这是 avifenc **自己的、真实存在**的旗标。
- **接线面**：该管线目前**零面板控件**（只有质量/线程）。最小接线：在 `QueueProcessor` GIF→AVIF 的 `headArgs`（`:3852`）追加，配一个全局开关。
- **优先级**：P2。
- **是否"有意不加"**：否。

#### B2 ⚠️**P0** — avifenc `--target-size S`（按目标体积交付）

- **为什么值得加**：**这是全仓唯一"按交付体积定编码"的能力**，其他所有后端都只能给质量、猜体积。实测 `--target-size 20000` ⇒ 产出 **19883 B**（命中）。对"网页首屏预算 20KB""邮件附件不能超 100KB"这类**硬约束**场景，质量滑块永远试不出来。
- **接线面**：`AvifCodecPanel` 加 `AvifTargetSizeBox`（字节，0=关闭）；`FfmpegOptions` 加 `AvifTargetSize`；在 GIF→AVIF 管线 `headArgs` 注入 `--target-size`。
- **优先级**：**P0**。理由：唯一性 + 解决的是"猜"这个真实痛点；且 avifenc 侧零改动成本。
- **是否"有意不加"**：否。**但须记代价**：help 原文标明"up to 7 times slower"，hint 要写清。

#### B3 ⚠️**P1** — avifenc `-a/--advanced`（aom 高级字典）

- **为什么值得加**：`avifenc --help` 明列可传 `aq-mode` / `cq-level` / `end-usage` / `sharpness` / `tune` / `enable-chroma-deltaq` 等 aom 键。实测 `-a aq-mode=3 -a cq-level=30` ⇒ rc=0、5474B（**显著小于**默认 6953B ⇒ 真生效）。其中 `enable-chroma-deltaq=1` 是**色度保真**开关，对本仓大量处理的彩色图片有直接价值。
- **接线面**：`AvifCodecPanel` 加"高级参数"文本框（危险区，需校验白名单），或只挑 `enable-chroma-deltaq` 做单个复选框（更安全）。
- **优先级**：P1（建议**先只做 `enable-chroma-deltaq` 复选框**，自由字典留到后面）。
- **是否"有意不加"**：自由文本字典**部分属有意不加**（易拼错、无校验、跨版本漂移）；但**具名键**属真缺口。

#### B4 ⚠️**P1** — avifenc `--qalpha Q`（alpha 独立质量）

- **为什么值得加**：AVIF 的 alpha 是独立平面、独立质量。现管线把 `-q` 一值通吃 ⇒ 带透明通道的图（本仓 GIF→AVIF 的主场！）**alpha 也被同一质量压**。可单独提高 alpha 质量或压到无损。这是**该管线主要用途（保透明）**上的直接质量旋钮。
- **接线面**：同 B1 的位置；可给 `AvifQAlphaBox`，或默认 `--qalpha 100`（alpha 近无损）作为**内部改进**。
- **优先级**：P1。
- **是否"有意不加"**：否。

#### B5 ⚠️**P2** — avifenc `-d/--depth 8|10|12`

- **为什么值得加**：本管线（GIF→AVIF）源多为 8-bit ⇒ 收益有限；但同管线若将来接 HDR 源，`-d 10` 是必需。注意 `--help` 里 `D,Dextension` 的**16-bit 组合（8,8 / 12,4 / 12,8）**是 AVIF 高位深的唯一通路。
- **接线面**：`AvifCodecPanel` 的位深下拉（`BitDepthCombo` 已有，需确认是否覆盖此管线）。
- **优先级**：P2（当前输入域用不满）。
- **是否"有意不加"**：接近有意不加——**当前 GIF 源场景下确无收益**，标注为 P2 低优先即可。

### C. cjxl（编码端已很全，缺的是"解码端/感知端"）

#### C1 ⚠️**P1** — cjxl `--faster_decoding=0..4`

- **为什么值得加**：这是 JXL 的**解码性能**旋钮（help 原文："higher values improve decode speed at the expense of quality or density"）。实测各档位**产物互异**：`0`=22311B / `1`=22368 / `2`=22227 / `3`=22227 / `4`=15497（`4` 显著变小）。**痛点很具体**：本仓是"图片浏览/交付"工具，用户对"打开快"的诉求真实；调它比调 `-e` 更对症（`-e` 只影响编码耗时）。
- **接线面**：`JxlCjxlPanel` 加 `CjxlFasterDecodingCombo`（`默认/0/1/2/3/4`）；`FfmpegOptions` 加字段；下发点在 `CjxlService.cs` 的 `-e` 附近（`:449`）。
- **优先级**：**P1**（本仓已实现编码 effort 却漏了解码 effort，属**轴线不完整**）。
- **是否"有意不加"**：否。

#### C2 ⚠️**P2** — cjxl `--resampling` / `--ec_resampling`

- **为什么值得加**：`--resampling=2` ⇒ 11877B（**相对 22311B 小 47%**），是**大幅压缩**的正当手段（色度降采样）。用于"缩略图/网页图"场景性价比极高。`--ec_resampling` 同理作用于 alpha 等额外通道。
- **接线面**：`JxlCjxlPanel` 加 `CjxlResamplingCombo`（`默认/1/2/4/8`）。
- **优先级**：P2。
- **是否"有意不加"**：**部分属有意不加的边界**——`--resampling` 会**真实降低分辨率**，与"无损/精修"面板语义有冲突风险，需在 hint 明示"会降色度分辨率"。若产品坚持"不悄悄降质"，则应保持不加，或只在"有损"路径暴露。

#### C3 ⚠️**P2** — cjxl `--epf` / `--gaborish`

- **为什么值得加**：二者都是**感知滤波**旋钮，实测真生效：`--epf=0`=22200B（vs 默认 22311B）、`--gaborish=0`=**15353B**（小 31%）。`gaborish=0` 对**锐利线条/文字/截图**类图像是明显的体积/清晰度权衡点——这正是"图片工具"的常见素材。
- **接线面**：`JxlCjxlPanel` 加 `CjxlEpfCombo`（默认/0/1/2/3）与 `CjxlGaborishCombo`（默认/关/开）。建议合并进一个"感知滤波"折叠区。
- **优先级**：P2。
- **是否"有意不加"**：`--epf` 属**有意不加的边缘**（默认由编码器自适应，手动调多为负收益）；`--gaborish=0` 则是真缺口（对文字/截图有明确用途）。

#### C4 ⚠️**P3（请勿加）** — cjxl `--container` / `--compress_boxes` / `--brotli_effort`

- 属元数据容器开销级，对图片用户无可感差异，且 `--brotli_effort` 极慢。**建议明确标为有意不加。**

#### C5 ⚠️**P1（已确证真缺口）** — cjxl `-e` 的域是 **1..10**，UI 把 cjxl 也卡在 9

- **实测**：`cjxl -e 10` ⇒ rc=0，24291B（**合法且可用**）。而 `ffmpeg -h encoder=libjxl` 自报 `-effort (from 1 to 9)` ⇒ **两条后端域不同**。
- **已确证的缺口**（读 XAML 原文，非推测）：
  - `MainWindow.xaml:650` `JxlEffortBox` `Minimum="1" Maximum="9"` —— **正确**（ffmpeg libjxl 域到 9）。
  - `MainWindow.xaml:656` `CjxlEffortBox` `Minimum="1" Maximum="9"` —— **错误**：它驱动的是**外部 cjxl.exe**，而 cjxl 真实域到 **10**。
  ⇒ 用户走 cjxl 路线时**永远够不到最高一档**（该档是 cjxl 为"最小体积"提供的）。这与 A-18（质量轴跨后端不可比）属同一族：**同一名义档位在两条后端上域不一致**。
- **接线面**：`MainWindow.xaml:656` 的 `CjxlEffortBox.Maximum` 由 `9` 改 `10`。⚠ 需同时确认 `CjxlService` 侧没有对 effort 做 `Clamp(1,9)` 的二次钳制（若有，两处都要改），以及 `FfmpegOptions`/预设校验的域。
- **优先级**：**P1**（一行属性修正，收益是"最高压缩档可达"；须连带查钳制点）。
- **是否"有意不加"**：否。**注意** `JxlEffortBox` 保持 9 是对的，不要"顺手"把它也改成 10 —— 那是 ffmpeg 后端，`-effort 10` 在 ffmpeg 上域外。

### D. 其他格式

#### D1 ⚠️**P3** — png/apng 的 `-dpm`（点/米）

- **实测**：`-dpm 11811` rc=0。与 `-dpi` 是**同一元数据的两种单位**（11811 dpm ≈ 300 dpi）。UI 只有 `PngDpiBox`。
- **为什么值得加**：**基本不值得**。MET 规范偏好 dpm，但现代消费链几乎只认 dpi，且二者可互算。**建议标为有意不加**，仅在"面向印刷/MET 交付"时再考虑。
- **优先级**：P3（有意不加）。

#### D2 ⚠️**P2** — libwebp 的 `-cr_threshold` / `-cr_size`（条件补充）

- **为什么值得加**：help 原文为 "Conditional replenishment threshold/block size"。实测 `-cr_threshold` 在**静态** `libwebp` 上 rc=0 但产物与默认**等大（1244B）**；在**动图** `libwebp_anim` 上 `-cr_threshold 100` ⇒ 2946B（vs 默认 3072B ⇒ **真变化**）。
- **结论**：它是**动图专用**旋钮（决定"哪些块不必重发"），对静态图无意义。UI 已有动图面板 ⇒ **值得加在动图路径**。
- **接线面**：`AnimationPanel` 或 `WebpCodecPanel` 的动图分支加 `WebpCrThresholdBox`。
- **优先级**：P2（动图场景限定）。
- **是否"有意不加"**：否；但**不要**放到静态 WebP 路径（会得到一个恒无效的旋钮，重演 A-4 那类"惰性控件"）。

#### D3 ⚠️**P2** — ffmpeg `libjxl` 的 `-xyb`

- **为什么值得加**：`-h encoder=libjxl` 自报 `-xyb <int> Use XYB-encoding for lossy images (default 1)`——**本仓从未暴露**。实测 `-xyb 0`=16141B vs `-xyb 1`=10011B（**差 38%**）。XYB 是 JXL 有损感知色彩空间；关掉它会走非 XYB 路径（对特定合成图/像素画有意义）。
- **接线面**：`JxlFfmpegPanel`（ffmpeg libjxl 路线）加 `JxlXybCheck`。
- **优先级**：P2。
- **是否"有意不加"**：**倾向有意不加**——XYB 是 JXL 的设计核心，关掉几乎总是更差；它更像是"调试/兼容"开关。若产品不想给用户"变差的开关"，可不加。**这是最典型的"有意不加"候选**。

#### D4 ⚠️**P1** — JXR：`JxrCodecPanel` 有 **0 个控件**，而 `JxrEncApp` 有 12+ 个选项

- **现状核实**：XAML 里 `JxrCodecPanel` 段落仅含 `TextBlock`（**主循环早前亦已核实**）；工具侧 `JxrEncApp -?` 列出 `-q -c -d -l -f -p -t -v -V -H -U -b -a -Q -F -s`。
- **本次实测（真跑出产物）**：
  - `-d 1`（4:2:0）⇒ 7912B vs `-d 3`（4:4:4）⇒ **10318B** ⇒ **`-d` 真生效**
  - `-s 2`（skip highpass）⇒ **2827B**（vs 7912B，**小 64%**）⇒ **`-s` 真生效**
  - `-l 2` ⇒ 7912B（与默认同 ⇒ 该素材上未观测到差异）
- **为什么值得加**：**`-d` 是本仓一个已确证缺陷的正解**。`JxrEncApp` 的规则是"未设 `-d` 时 q≥0.5→4:4:4、q<0.5→**4:2:0**"，而 GUI 映射 `(100-q)*25/100` ⇒ **质量 ≤49 静默降 4:2:0，无控件无提示**（A-17 已记录）。**暴露 `-d` 即可让用户锁死 4:4:4**，把静默降质变成可控。
- **接线面**：`JxrCodecPanel` 加 `JxrChromaCombo`（`自动/仅Y/4:2:0/4:2:2/4:4:4` → `-d 0..3`）+ `JxrSkipSubbandsCombo`（`-s`）。下发点：JXR 现在是外部进程调用，需在拼 `-i/-o/-q` 处追加。
- **优先级**：**P1**（`-d` 直接修一个已确证的静默降质；`-s` 附带）。
- **是否"有意不加"**：否。JXR 面板"零控件"本身更像**未完成**而非设计。

---

## 三、有意不加（附理由）——**请勿**当缺口补

| # | 选项 | 不加的理由 |
|---|---|---|
| T1 | NVENC `-temporal-aq` | **实测惰性，加了就是假控件**。`-temporal-aq 0` 与 `1` 产物 **MD5 完全相同**（`67F098E862A39FE228FE6C74E12244FE`，均 20068B）⇒ 同 A-4 的"惰性复选框"形状。**加它等于重犯已修过的错。** |
| T2 | NVENC `-gpu` | 本机多卡（5080 + 4060），暴露选卡会让"同配置不同产物"不可复现；且图片任务用不满。**不改**。 |
| T3 | libaom ~40 个 `enable-*-partitions/intra/comp` | 全是 `default auto` 的**码流特性开关**，手动关只会更差/掉兼容；逐条暴露等于把编码器调参面板抄进 UI，违背"图片工具"定位。**有意不加。** |
| T4 | cjxl `--premultiply` / `--keep_invisible` | 与既有 alpha 语义冲突：本仓对 alpha 走"无损/保留"约定，`--keep_invisible=0` 会**丢弃不可见像素颜色**⇒ 用户看到的透明区颜色在重编后变黑。**有意不加。** |
| T5 | cjxl `-a/--alpha_distance` | 同上：本仓 alpha 走约定路径（`--ec_resampling` 亦同）。单独给 alpha 有损距离会破坏"alpha 不被悄悄压"的承诺。**有意不加。** |
| T6 | cjpegli `--std_quant` | 实测 `--std_quant`=28849B **大于**基线 27430B ⇒ 回到 Annex K 标准量化表，**通常更差**，只在"必须与他方标准表逐字节比对"时有意义。**有意不加。** |
| T7 | cjpegli `--xyb` | 实测 rc=0（22473B，vs 基线 27430B ⇒ 生效）。但 cjpegli 的 XYB 是**实验性研究路径**，本仓 JPEG 产品定位下无交付价值。**有意不加**（若将来做"感知质量研究"再议）。 |
| T8 | avifenc `-y/--yuv` `--sharpyuv` `-r/--range` | **会与色彩面板真值打架**：`ColorTransformPlan.cs:80` 已记录 `--lossless` 下 avifenc 强制 matrix=identity、传 6/9 会报错。让编码器侧再定一次 yuv/range ⇒ 两份真值，正是本仓反复清理过的缺陷类。**有意不加**（真值只保留在色彩面板）。 |
| T9 | mjpeg `-quantizer_noise_shaping` `-noise_reduction` `-ps` `-error_rate` `-force_duplicated_matrix` `-mpv_flags`… | `-error_rate`/`-ps` 是 **RTP 流**专用；`-force_duplicated_matrix` help 明写 "useful for rtp streaming"。其余是 mpegvideo 祖传调参，对静帧 JPEG 无正当用途。**有意不加。** |
| T10 | gif `-gifflags` `-gifimage` | `-gifimage` 是"每帧独立图像"模式，与动图压缩**背道而驰**；`-gifflags` 的 `offsetting`/`transdiff` 默认已开且是**正确的**默认。**有意不加。** |
| T11 | NVENC `-tune ll/ull/lossless` | `ll`/`ull` 是**低延迟视频**语义（图片无延迟概念）；`lossless` 在本构建 AV1 上实测 **not supported**（`-tune lossless` 与 `-rc constqp` 均 rc=127，见 `ImageEncoderArgs.cs:483-484`）⇒ 暴露必失败档位。**有意不加**（只放 hq/uhq，见 A2）。 |
| T12 | avifenc `--min/--max/--minalpha/--maxalpha` | **官方 help 原文标记 `Deprecated, use -q 0..100 / --qalpha instead`** ⇒ 加已弃用接口是负资产。**有意不加。** |
| T13 | libaom `-enable-restoration` | 实测 `0` 与 `1` 同尺寸（9881B）⇒ 该素材上无差异；且默认 `auto` 由编码器自适应，手动干预多为负收益。**有意不加**（未构成"可用旋钮"证据）。 |

---

## 四、"该删的"（UI 选项对应的工具能力已不存在）

### 结论：**本轮 0 条** —— 且这是**好消息**（说明前几轮已清理干净）

已逐条复核历史上被点名"该删"的三处，**均已修复**：

| 历史条目 | 本轮复核 | 证据 |
|---|---|---|
| A-20：`JpegDctCombo` 含 `float`（ffmpeg 拒绝） | ✅ **已删** | XAML 现值仅 `auto/int/fastint`，且带注释说明为何不留 float；`FfmpegOptions.cs:365` 的 `"int" / "fastint" / "float"` **注释已过期**（建议顺手改注释，非功能问题） |
| A-11：SVT 面板 `SvtStillPictureCheck` 死控件 | ✅ **已不存在** | 现 XAML 无该控件名（221 个 x:Name 中无匹配） |
| A-7：`HwPresetCombo` 死码 | ✅ **已不存在** | `ImageEncoderArgs.cs:393-404` 那段已随重写清除 |

**另发现一处"注释过期"（非代码缺陷，建议顺手修）**：
- `src/FfmpegGui/Models/FfmpegOptions.cs:365` 注释仍写 `"int" / "fastint" / "float"`，而 XAML 已无 `float`。**仅注释，不影响行为。**

> **对"该删的"给出定论**：本仓当前**不存在**"UI 提供了工具已不支持的选项"这类必须删除的项。唯一需要注意的不是"删"而是"**别加**"——见 §五.1。

---

## 五、本轮额外发现（重要，非缺口但影响接线正确性）

### 五.1 🔴 `-progressive` 在本构建的 ffmpeg 里**根本不存在** —— 接线时**绝不可**加

这是本轮最容易踩的坑，且**与文档/直觉相反**（JPEG 确实有渐进式，很容易想当然认为 ffmpeg 有 `-progressive`）。

**实测**：
```
$ ffmpeg -y -i src.png -c:v mjpeg -progressive 1 out.jpg
Unrecognized option 'progressive'.
Error splitting the argument list: Option not found
```

**交叉验证**（排除"拼错位置"）：`-progressive 0` 同样失败；`ffmpeg -h encoder=mjpeg` 全文搜索 `progressive` **零命中**；`ffmpeg -h full` 里出现的 `progressive` 全部属于 `field_order` 枚举值、`idet`/`phase`/`setparams` 滤镜、以及 `vanc_progressive_frame`——**没有任何一条是 mjpeg 的编码选项**。

**结论与建议**：
- 本构建 mjpeg **只能出基线 JPEG**（与其自报 AVOption 一致：只有 `-huffman` 与 `-force_duplicated_matrix`）。
- 若要渐进式 JPEG，**唯一通路是 cjpegli**（`-p 1` / `-p 2`，实测 `-p 0`=10623B vs `-p 2`=10075B，真生效）。
- **本仓现状是对的**：`JpegProgressiveCombo` 存在但 **`IsVisible="False"`（有意隐藏）**，且注释写明"ffmpeg mjpeg 不支持渐进，该值仅供 cjpegli/Gain Map 路径使用"。**这是一处诚实且有意的设计，不要"修"它。**
- ⚠ 若将来有人把该控件**显示出来**并接到 mjpeg 命令上 ⇒ 就是**每选必失败**（重演已删的 `-dct float`）。

### 五.2 avifenc 的 `--pasp/--crop/--clap/--irot/--imir/--clli` 属**元数据**缺口，不属"编码选项"

`--help` 列出这些属性写入能力（旋转/镜像/宽高比/内容亮度）。它们**不是编码质量选项**，而本仓的元数据侧走 `ExifToolService`。是否需要在 GIF→AVIF 管线里补 `--irot/--imir`（可从 EXIF Orientation 派生）**建议单独立项**，不混入本文的编码选项缺口。

### 五.3 一条"误导性报错"值得记进 hint

RGB 直通 NVENC 时：
```
[av1_nvenc] YUV444P not supported
[av1_nvenc] No capable devices found     ← 这句是假的
```
本机 `-gpu list` 明确回显 **2 张 N 卡**。A-25 记录过同一坑。若采纳 **A4（`-rgb_mode yuv444`）**，这条误导性报错会一并消失。

### 五.4 代码现状比 `ENCODING_OPTIONS_AUDIT_2026-09-26.md` 新得多（勿照抄旧结论）

本轮抽查发现多处以**已修复**：
- **A-13**（GIF 关抖动关不掉）⇒ 已修：`ImageEncoderArgs.cs:286` 现在显式发 `dither=none`；实测 `dither=none`=9137B vs 默认=9265B（确有差异）。
- **A-3**（质量对硬件后端无效）⇒ 已修：`ImageEncoderArgs.cs:475-522` 现已按后端分域发 `-cq`/`-global_quality`/`-rc cqp+-qp_i`。
- **A-6/A-7**（SVT tune 错位、中文串当键）⇒ 已修：`NormalizeTuneToken` 已引入，`-tune vmaf` 在 libaom 上改为**出声不发送**而非必崩。
- **A-16**（APNG 不吃 `-pred/-dpi`）⇒ **部分仍在**：`ImageEncoderArgs.cs:115-121` 的 `case "png": case "apng":` **共用同一分支**，`-pred`/`-dpi` **会**下发给 apng ⇒ **此条已修**（ffmpeg 侧实测 apng 确吃：`-pred sub -dpi 300` rc=0、2867B vs 基线 1737B）。但 `-dpm` 仍未暴露（§二.D1）。
- **A-15**（WebP `compression_level` 被误判"仅无损"）⇒ **仍未修**：`ImageEncoderArgs.cs:143-146` 依旧在有损路径 `Console.WriteLine` 告警并丢弃该值。⚠ 且该告警走 `Console.WriteLine`，而产品是 **WinExe** ⇒ **GUI 用户看不见**。**建议单列一任务**（属"已暴露选项的语义错误"，不属本文的"未暴露缺口"）。

---

## 六、落地计划（按优先级）

### P0（正确性/唯一能力，建议本迭代）
1. **A6** SVT `lossless=1` 并入单一 `svtav1-params` dict ⇒ 修"勾了无损却拿到有损"（实测 inf vs 46.12 dB）。
2. **A1** NVENC `-rc`（`cqp/vbr/cbr`）+ `-qp` ⇒ 补齐码控模式；复用现成 `crf` 变量。
3. **B2** avifenc `--target-size` ⇒ 全仓唯一"按体积交付"能力（实测命中 19883/20000）。

### P1（真缺口，收益明确）
4. **D4** JXR `-d`（锁死 4:4:4，消除 q≤49 静默降 4:2:0）+ `-s`。
5. **B4** avifenc `--qalpha`（GIF→AVIF 保透明场景的质量轴）。
6. **A5** libsvtav1 `-qp`。
7. **C1** cjxl `--faster_decoding`（补上"解码 effort"这条缺失的轴）。
8. **A2** NVENC `-tune uhq`（只放 hq/uhq）。
9. **B3** avifenc `enable-chroma-deltaq`（先做单个具名复选框，不放自由字典）。
10. **C5** `CjxlEffortBox.Maximum` 9 → 10（已确证：`MainWindow.xaml:656`；cjxl 实测接受 `-e 10`；⚠ `JxlEffortBox` 保持 9）。

### P2（有价值但非必需）
11. **A3** NVENC `-multipass`；12. **A4** NVENC `-rgb_mode`（建议做成自动）；13. **B1** avifenc `--progressive`；14. **C2/C3** cjxl `--resampling` / `--gaborish`；15. **D2** WebP 动图 `-cr_threshold`；16. **D3** libjxl `-xyb`（倾向不加）；17. **B5** avifenc `--depth`。

### P3 / 有意不加
`-dpm`（D1）、`--container`/`--brotli_effort`（C4）、以及 §三 全表。

### 待核项（**未验证**，不凭印象断言）
- ~~`CjxlEffortBox.Maximum` 实际值~~ ⇒ **已核**：`MainWindow.xaml:656` = 9，确证为真缺口（见 §二.C5）。
- **QSV / AMF / VAAPI 全部行为**：本机 `Win32_VideoController` 只有 Intel UHD（无 Arc 独显）+ 2×NVIDIA，**无 AMD 卡** ⇒ `av1_amf`/`av1_qsv` 的**生效性未验**，只有 ffmpeg 层选项域与解析证据。VAAPI 本构建 `-encoders` 零命中。
- `avifenc --target-size` 在**大图**（≥4K）上的命中精度与耗时倍率（help 称 up to 7× slower）**未验**。
- `JxrEncApp -l`（overlapping）在**高质量档**（q≥0.5）下的差异**未验**（本轮仅测 q=0.4 同值）。

---

## 附：证据留存位置

全部原始输出已落盘（便于复核，非交付物）。

**入库部分**（`docs/_gapscout_raw/`，8 个 / 共 30 KB）—— 含**唯一实测读数**或本仓外部工具的 help 原文：
`jxrenc_q.txt`、`progressive_evidence.txt`、`cjxl_help.txt`、`cjxl_help_vv.txt`、`cjpegli_help.txt`、
`cjpegli_help_v.txt`、`avifenc_help.txt`、`dngtool_help.txt`。

**未入库部分**（已移至 gitignored 的 `tests/output/_gapscout_raw_dumps/`，23 个 / 共 1374 KB）——
**纯转储、一条命令即可重生成**，且其中单个文件（`ffmpeg_h_full.txt` 1.30 MB）比仓库里任何源码都大：
`ffmpeg_h_full.txt`（实测 = `ffmpeg -h full`，17 762 行、0 秒可重生）、`encoders_all.txt`、
`enc_<encoder>.txt`（23 个 = `ffmpeg -h encoder=<x>`）、`big.png`、`src.png`。
> ⚠ 处置理由：仓库既有约定是「所有测试产物进 `tests/output/`（gitignore）」；
> 而 `docs/TESTING.md` 第 122 条裁定「判据住在 gitignored 目录 = 没有判据」⇒
> **有派生结论价值的读数入库、纯可重生成的转储不入库**，两者各按其约定处置。

**环境说明**：跑编码时按 Lead 指定设置 `FFMPEGGUI_RAMDISK` / `TEMP` / `TMP` 指向仓库内 `tests/output/_ramdisk`（本机沙箱禁止子进程写 `%TEMP%`，否则 `Permission denied`）；所有产物落在仓库内路径。

**纪律声明**：本轮**未修改** `src/` 与 `tests/` 下任何文件；唯一写入的交付物为本文件（及上述证据目录）。所有"可用/不可用"结论均附本仓二进制原始输出；凡未实测处一律标注 **未验证**。
