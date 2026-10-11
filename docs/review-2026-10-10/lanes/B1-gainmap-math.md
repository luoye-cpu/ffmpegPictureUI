# B1 · GainMap 数学审查（HEAD=c18dc4a，工作树干净，只读）

范围：`src/FfmpegGui/Services/GainMapEncoder.cs`(1021)、`GainMapDecoder.cs`(1316)、
`ColorMapping/GainMapJpegCodec.cs`(176)、`SimdPixelOps.cs` 的 `FloatToSrgb8`/`ReinhardToSdr`/`ComputeGainMapGray`、
`PsnrCalculator.cs`(355)。
权威参照全部取自本机 checkout：`tools/src/libultrahdr`（UltraHDR 官方实现，含 ISO 21496-1 编解码与两趟增益区间）、
`tools/src/libavif`（ISO-BMFF `tmap` 语义 + Apple 件样本）。
`tools/src/libjxl` 这份快照**没有** gain map 数学（只有 `lib/extras/gain_map.cc` 的 jhgm bundle 序列化），故不作为参照。

---

## 0 一页结论

| # | 结论 | 标签 | 证据强度 |
|---|---|---|---|
| F1 | **元数据声明 `offsetSDR=offsetHDR=1/64`，但前向增益完全不用它** ⇒ 编码正向不是解码逆运算。误差 = `(boost−1)/64`，数值复算：1000nits 峰值 +15.6 nits、4000nits +82 nits；黑底借到 boost=2 被抬到 **sRGB 码 34**（3.2 nits），借到 4.9 ⇒ 码 70 | 真缺陷-标准 + 真缺陷-精度 | 代码行 + 参照行 + 复算表 |
| F2 | 增益字节与降采样字节都是 `(byte)` **截断**而非 round-to-nearest ⇒ 恒负偏置（两次 ⇒ 最多 −1 档）：1000nits 下 HDR 恒暗 0.31–0.63%，4000nits 0.59–1.18% | 真缺陷-精度 | 代码 + `affineMapGain` 对照 |
| F3 | **底图通道 >1 被 `FloatToSrgb8` 截到 1，而增益按截前值算** ⇒ 恢复值按比例丢。BT.2020 源 + 默认 sRGB 底：2020 立方体 **29.4%** 的颜色映射到 709 后通道 >1（最大 1.6475），饱和 2020 红峰值像素 R 恢复比真值暗 **39%**。根因是「先在源色域按 max(r,g,b) 定膝，后做底图原色矩阵」 | 真缺陷-精度（含文档过期：注释称参照是"先 convert 再编"） | 矩阵自算 + 代码行 |
| F4 | `multiChannel=true` 时增益图仍硬编码 `--chroma_subsampling 420` ⇒ 逐通道增益被色度降采样摧毁；参照 RGB 增益图走 **4:4:4** | 真缺陷-标准 | 代码 + `jpegencoderhelper.cpp:41-42` |
| F5 | AVIF 出口恒按 `gray` 编增益图，却在 tmap 元数据按 multiChannel 置 flags bit7 ⇒ **宣称 3 通道、交付 1 通道** | 真缺陷-静默失效 | `:582` vs `:849` |
| F6 | XMP 的 RDF 命名空间 URI 拼错：`1999-02-22-rdf-syntax-ns#` 应为 `1999/02/22-rdf-syntax-ns#`；且 hdrgm 字段写在**主图**XMP（参照写副图） | 真缺陷-标准（对外互操作） | `:899` vs `jpegrutils.cpp:887/924`、`jpegr.cpp:1141` |
| F7 | `verify-gainmap-managed.ps1` 注释断言"现行实现不写 XMP、exiftool 读不到 hdrgm"⇒ **与代码和自家门禁读数都矛盾**（真实成因是 F6） | 文档过期 + 错误归因 | `:74-78` vs 门禁日志 `:55` PASS |
| F8 | 解码侧只取通道 0 的 `gainMapMin/Max/Gamma/offset`，第三方多通道件（参照两趟会为三通道写不同区间）的增益被按通道 0 统一施加 ⇒ 色偏 | 真缺陷-标准（解侧） | `:768/:792` vs `jpegr.cpp:1031-1038` |
| F9 | **峰值自动档在 GUI 不可达**：`ParseGainMapNits` 夹到 [200,10000]、模型默认 1000 ⇒ `>0 用户优先` 恒成立 ⇒ 4000nits 素材被按 1000 截顶；400nits 素材只用 42.5% 量程 ⇒ 有效档宽 2.35×、相对误差 1.48% | 能力缺口 + 静默质量损失 | `MainWindow.xaml.cs:5837-5842`、`GainMapJpegCodec.cs:119-121` |
| F10 | 缺参照那道**暗像素增益上限**守卫（`sdr<2/255 ⇒ gain≤2.3 log2`），与 F1 叠加成黑位借光（halo） | 真缺陷-精度（缺守卫） | `gainmapmath.cpp:775-780` |
| — | 旧主张 1/2/3 逐条复验：**成立**（底图确为 SDR + log2 比率增益、峰值确已接线并进增益上限、PSNR 峰值参考已改为满量程 scale、三重坑三处都在位）。旧主张 4：`FloatToSrgb8` 的 ±1 LSB **不跨增益档**（增益来自 float，不经 8-bit），但底图自身 1 LSB 在暗部等效 12.6 个增益档 ⇒ 瓶颈是底图 8-bit 而非 SIMD | 复验通过 / 未复现（3414/889 魔数全仓零命中） | §2 §3 |

一句话：**语义与容器骨架大体对（ISO 字段顺序、MPF 偏移、flags 位、log2 区间、useBaseColorSpace 都对得上 libultrahdr），坏在"声明的数与算的数不是同一份"** —— offset 写了不用（F1）、字节量化只截不入（F2）、底图钳位不回灌（F3）、通道数说了不算（F4/F5）、XMP 命名空间拼错（F6）。

---

## 1 方法与被推翻的假设

1. **正向命中优先**：每条"没实现/只改标签"的判词前，先 grep 实现体；判"死代码"前先看调用点。
   例：`GainMapEncoder.ReinhardToSdr`(:263) / `SegmentedReinhardMap`(:291) / `FloatToSrgb8`(:680) 三个私有方法
   **调用点为零**（`grep` 只命中定义与其自身互调）⇒ 活跃路径是 `SimdPixelOps`，但 `SimdPixelOps.cs:160` 仍写
   「与 GainMapEncoder.ReinhardToSdr 语义一致（误差 < 1e-6）」——两者语义**根本不同**（死的那条 y>1 硬钳 1.0，
   活的这条 knee=0.75 起 S 形压缩）⇒ 文档过期，且是"改错文件就白改"的陷阱。
2. **数值不靠嘴**：把源码表达式逐行复刻成脚本再算（复刻式：`SimdPixelOps.cs:185-194` 的 `SegmentedReinhardScalar`、
   `GainMapEncoder.cs:333-338` 的 `LogGainToByte`、`GainMapDecoder.cs:1188-1194` 的增益施加、`GainMapEncoder.cs:835` 的
   `round(maxLog2*100)/100`），把误差**逐项拆开**单独测（见 §3.5）。
   ⚠ 这是"复刻复算"，**不是**跑产品代码路径（未含 JPEG 有损项），因此端到端 PSNR 读数一律记 `未测`。
3. **被推翻的假设（我自己带的先验，两条被证伪）**：
   - 先验「base 是 SDR 曲线而 gain 用线性比率算 = 常见错法」——**错**。ISO 21496-1/UltraHDR 的定义就是
     `gainMapLog2 = lerp(min,max,g^(1/γ))`、`alt = (base+offsetS)·2^log2 − offsetH`
     （`libavif/src/gainmap.c:266-267`，注释在 `include/avif/avif.h:644-656`），
     即"SDR 底 + 线性域 log2 比率"**正是标准语义**；本项目这条合规。
   - 先验「709→2020 底图映射会把值推出 [0,1]」——**方向反了**。自算矩阵（D65 同白点，无适应）
     709→2020 全正、单位立方体仍落在 [0,1]（max=1.0000，越界 0.00%）；越界发生在**反向** 2020→709
     （29.4% 越界，max 1.6475）——而 2020→709 恰是默认配置（`baseGamut=srgb` + BT.2020 PQ 源）。
4. **纪律**：未跑的不写成结论。本机 `%TEMP%` 按可执行文件被拦（色彩引擎/GainMap 任务成片 `exit -13`）
   ⇒ 未起 `GainMapTestHost`、未起产品 exe；唯一"读数"是仓库里**已存**的门禁日志
   （`tests/output/final-verify-gainmap-engine.log`，2026-09-19），引用时已注明日期。

---

## 2 旧主张复验表

| 旧主张（出处） | HEAD 复验 | 标签 |
|---|---|---|
| 「GainMap 功能重构：移除 PQ HDR 底，实现 Rec.2020 SDR 底与像素级映射」 | **成立**。`EncodeAsync` 只有 `baseGamut∈{srgb,bt2020}`(:110-113)，底图恒 `sdrLinear→sRGB gamma→8bit`(:152, `WriteBgra8PngAsync`)；`BuildIso21496Metadata` 写死 `baseHdrHeadroom=0/1`、`alternateHdrHeadroom=log2(headroom)/100`(:843-845)、XMP `BaseRenditionIsHDR=False`、`GainMapMin=0`(:885,920-922)；底图色域≠源时**真做线性域 3×3 矩阵**并两侧同施加(:137-142)。PQ 底残留：`JpegGainMapBaseHdr`/`jpeg-gain-map-base-hdr`/`BaseHdr` 在 `src/FfmpegGui` 零命中（`docs/TESTING.md:1200-1201` 同一判据，我复跑 grep 仍零命中） | 复验通过 |
| 「GainMap 峰值接线修复」 | **成立但受 F9 限幅**。链路：`GainMapJpegCodec.ResolveEncodeParams`(:119-122) → `RawColorPipeline.cs:365-367` / `QueueProcessor.cs:2646-2649,2726` 传 `hdrPeakNits` → `headroom=hdrPeakNits/sdrWhiteNits`(:107) → `maxLog2Gain`(:108) → **字节分母**(:337) **且** ISO `alternateHdrHeadroom`(:844) → 解侧 `peakNits=203·2^HdrCapacityMax`(`GainMapDecoder.cs:322-323`) → 拷回 `QueueProcessor.cs:533` → `CjxlService.cs:574-575 --intensity-target` / `ColorIntentFactory.cs:422-424`。峰值确实进增益上限，无"算了不传"。⚠ 但 GUI 侧 `ParseGainMapNits`(:5837-5842) 永远返回 ≥200 ⇒ 元数据档只在 CLI `--jpeg-gain-map-target-nits 0` 可达 | 复验通过 + 能力缺口(F9) |
| 「PSNR 度量口径纠正 / D10 断言正述」 | **成立（在往返闭环里）**。`tests/GainMapTestHost/Program.cs:1227-1231`：两侧先统一到"1.0=SDR 白"域，峰值参考取 `scale=hdrPeak/203` 而非 1.0，注释自证旧写法低估 25.9dB(hdr4000)/13.9dB(hdr1000)。产品侧 `QualityAnalysisService.cs:129-160` 按**编码器原生域**选 rgb24/yuv444p(16le)、peak 取满量程（10/12-bit 由 ffmpeg 左移到 16-bit 满量程 ⇒ 用 16 与用 10+64 等价，无口径错）。`D10` 编号在当前 docs/tests 内**零命中**（grep `D10`）⇒ 该编号只存在于项目记忆，无法对齐到具体断言 | 复验通过 / 编号未复现 |
| 「三重坑：nits 默认 203」 | **在位**。203 只作 SDR 白点：`GainMapEncoder.cs:31 KSdrWhiteNits=203f`；目标峰值默认 `FfmpegOptions.cs:376 = 1000`。SDR 源时 `PeakNits=203`(:122) ⇒ `headroom=1 ⇒ maxLog2Gain=0` ⇒ **短路成普通 JPEG 并点名**(:217-222, AVIF 同口径 :606-611)，属主动决定而非静默 | 复验通过 |
| 「三重坑：peakNits 未拷回」 | **在位**。`FfmpegCommandBuilder.cs:1543-1548` 显式把 ffprobe 的 side-data 峰值拷回（注释点名"此前本行缺失 ⇒ 永远是 0"），并带 `peakSource`；下游日志自证来源 `GainMapJpegCodec.cs:123-124` | 复验通过 |
| 「三重坑：ffprobe 键名与有理数单位」 | **在位**。`FfmpegCommandBuilder.cs:1796` 取 `max_content`（候选名含 `MaxCLL/max_cll`）、`:1798` 取 `max_luminance`；`ParseRationalOrDecimal`(:1829-1843) 按 `num/den` 解析 `"40000000/10000"⇒4000`，分母 0/格式不符 ⇒ 0（不猜）。MaxCLL 优先于 mastering（对齐 `ff_determine_signal_peak`） | 复验通过 |
| 「`FloatToSrgb8` AVX2 与标量只承诺 ±1 LSB，会不会跨一个增益量化档」 | **不跨**（该函数只进底图，增益来自 float），但**底图 1 LSB 本身等效远多于 1 个增益档**：见 §3.6 表（码 16 ⇒ 12.6 档，码 128 ⇒ 2.7 档）。两条路径唯一实质差异是平局取整规则（标量 `MathF.Round`=to-even `SimdPixelOps.cs:97`；AVX2 `floor(x+0.5)` :136-137）⇒ 只影响产物字节可复现性，不影响精度水位。D28 断言（`GainMapDecodeGroupD.cs:312` `maxDiff<=1`）与承诺一致 | 复验通过（并给出真正的瓶颈项） |
| 「fdiv 魔数 3414/889 对应 8-bit 1/255 步长」 | **未复现**：`3414`/`889` 在 `src/`、`docs/`、`tests/` 与 `git log -S` 全零命中；现有写法是真除法 `clamped/maxLog2Gain*255f`(:337) 与 `byte/255`(`GainMapDecoder.cs:1188`) | 未复现 |

---

## 3 增益数学逐条核对（含往返误差上界推导）

### 3.1 增益定义与字段语义（对齐 libultrahdr / ISO 21496-1）

标准侧（参照行，逐字）：

```
libavif/src/gainmap.c:266-267
  const float gainMapLog2 = lerp(gainMapMin[c], gainMapMax[c], powf(gainMapValue, gammaInv[c]));
  const float toneMappedLinear = (baseLinear + baseOffset[c]) * exp2f(gainMapLog2 * weight) - alternateOffset[c];
```

```
libultrahdr/lib/src/gainmapmath.cpp:773-774, 784-789, 791-797
  float computeGain(float sdr, float hdr) { float gain = log2((hdr + kHdrOffset) / (sdr + kSdrOffset)); ... }
  uint8_t affineMapGain(...) { ... if (gamma != 1.0f) mappedVal = pow(mappedVal, gamma); mappedVal *= 255; return CLIP3(mappedVal + 0.5f, 0, 255); }
  Color applyGain(...) { logBoost = log2(min_content_boost)*(1-gain) + log2(max_content_boost)*gain; return ((e + offset_sdr)*gainFactor) - offset_hdr; }
```

项目侧：

| 项 | 项目 | 标准/参照 | 判定 |
|---|---|---|---|
| 增益量 | `log2(HDR/SDR)`，线性映射到字节（`GainMapEncoder.cs:156` → `SimdPixelOps.cs:215-216`） | 同 | ✅ 不是"比值直接编码"，也不是 PQ 域比率 |
| `max/min_content_boost` | 写的是 **log2** 值（`:835-841` `gainMaxN=round(maxLog2*100)`，D=100） | ISO 二进制字段即 log2（`gainmapmetadata.cpp:386-389` 写 `log2(max_content_boost)`） | ✅ 域对（内部 `content_boost` 是线性，落到字段才取 log2） |
| `gamma` | 恒 1/1（`:837`），解侧 `pow(g,1/γ)`（`:1187`） | 参照 `γ=1` 时不施加 | ✅ 自洽 |
| `offsetSDR/HDR` | 元数据写 **1/64**（`:838` `offsetN=1,offsetD=64`；XMP 同值 `:887,924-925`），**前向不使用**（`:325`, `SimdPixelOps.cs:215`） | 参照前后向同值（`jpegr.cpp:1041-1042` = `kSdrOffset/kHdrOffset` = 1e-7；一趟路径直接写 0.0，`jpegr.cpp:727-728`） | ❌ **F1**：写了不用。1/64 本身合法（第三方件确用此值：`docs/HANDOVER.md:1894` libavif 交叉读数 offset=0.015625） |
| `base/alternateHdrHeadroom` | 0/1 与 log2(headroom)/100（`:843-845`），XMP `HDRCapacityMin=0`、`HDRCapacityMax=gainMaxLog2`（`:926-927`） | `gainmapmetadata.cpp:417-421` 写 `log2(hdr_capacity_*)`；`jpegr.cpp:1043,1047` `hdr_capacity_max = hdr_white_nits/kSdrWhiteNits` | ✅ 同式 |
| flags | bit7 多通道、bit6 useBaseColorSpace、bit3 公共分母、bit2 backward（`:847-850`，解 `:740-748`） | `gainmapmetadata.h:20-21`(1<<7,1<<6)、`gainmapmetadata.cpp:140(=4),158(=8)` | ✅ 位对 |
| ISO 字段顺序 | `min_ver,wri_ver,flags, baseN baseD altN altD, 每通道 (min,max,γ,baseOff,altOff) 各带分母`（`:854-869`） | `gainmapmetadata.cpp:175-186` 独立分母支完全同序 | ✅ |
| 公共分母模式 | 恒不用（走独立分母） | 参照优先用公共分母 | ⚪ 合法但多 20B/通道；无精度影响 |
| XMP 字段数值 | `GainMapMin/Max`、`HDRCapacityMin/Max` 写 **log2**（`:921-927`） | `jpegrutils.cpp:929-935` 同样写 `log2(...)` | ✅ 不是"只改标签"，数也对 |
| 版本 | `hdrgm:Version 1.0`（`:913`）、ISO `min/writer_version=0`（`:854-855`） | `kJpegrVersion="1.0"`（`ultrahdrcommon.h:446`） | ✅ 声称 v1.0 与实际字段一致；额外写了 AndroidX 风味字段 `MapPrimary/MapSecondary/MapImageRatio/Channels/UseBaseRenderingColorSpace`（参照不写，属超集，非 v1.1 声明） |
| fdiv 魔数 | 无（真除法） | — | 未复现 |

**方向一致性**：解侧 `GainMapDecoder.cs:1188-1194` 与参照逐字同形 ✅；编侧 `:156→SimdPixelOps.cs:206-218`
的 `log2(hLum/sLum)` 与解侧 `2^(255 档反查)` 在 **offset=0** 假设下互为逆（同向：base→alternate）✅；
一旦 offset≠0 就不再互逆 ⇒ F1。解侧对 `BaseRenditionIsHdr`（反向）明确拒绝（`:169-173` BMFF 分支返回 null；
JPEG 分支只告警仍按 SDR 底解读 `:282-283`）⇒ **两条分支口径不一致**（同向性上留了一条"告警后照旧"的路），
`未判定`：这条差异是否有实测覆盖，需 lane B2/门禁点名。

### 3.2 量化：位深、档宽、往返误差上界

只 8-bit（`LogGainToByte` 产 byte，`WriteGrayOrRgbPngAsync` → PNG → JPEG），**没有 10/12-bit 增益图路径**
（能力缺口，参照 libultrahdr 也是 8-bit ⇒ 不算偏离标准，但挡了更高精度）。

档宽（增益图 8-bit、全量程 `[0,log2(headroom)]`）：

| 目标峰值 | headroom | maxLog2 | 档宽(log2) | 相对亮度步长 | 截断偏置(½档) |
|---|---|---|---|---|---|
| 1000 nits | 4.926 | 2.3005 | 0.00902 | **0.627%** | 0.313% |
| 2000 nits | 9.852 | 3.3004 | 0.01294 | 0.901% | 0.450% |
| 4000 nits | 19.704 | 4.3005 | 0.01686 | 1.176% | 0.586% |
| 10000 nits | 49.26 | 5.6218 | 0.02205 | 1.540% | 0.767% |

**往返误差上界（解析）**。记底图线性值 `s`（1.0=SDR 白）、恢复值 `h′`、真值 `h = s·2^L`：

1. 量化（截断，含降采样再截断，合计偏置 ∈(−1,0] 档，跨度 ±1 档）：
   `|h′/h − 1| ≤ 2^{maxLog2/255} − 1` ⇒ 1000nits ≤ **0.63%**，4000nits ≤ **1.18%**。
2. offset 失配（F1，主导项）：`h′ − h = (boost − 1)·offsetSdr`，`boost = h/s` ⇒
   **上界 = (headroom − 1)/64** ⇒ 1000nits = 0.0614（**12.5 nits**）、4000nits = 0.292（**59.3 nits**）；
   实测复算峰值误差 15.6 / 82.4 nits（与"截断+8bit 底图"叠加后同量级，见 3.5）。
3. 底图 8-bit 量化（不被增益补偿，因增益由 float 算）：`|Δh| ≤ 2^L·Δs`，中调 ≈ 6.9 nits @1000。
4. ISO 分数分母 100（F11）：声明上限与实际分母差 ≤0.005 log2 ⇒ 恢复值整体标度差 ≤**0.35%**（实测 3.4 nits @1000）。

`headroom` 的钳制方式是**截顶**（`LogGainToByte` 把 `logGain` 钳到 `[0,maxLog2Gain]`，`:336`），不是归一：
当内容真实峰值 > 声明峰值（F9：GUI 恒按 1000nits），高于声明的高光被直接截到 255 ⇒ HDR 细节静默丢失，
只在日志里出现"峰值 1000nits（来源：用户指定）"一句。参照两趟法按**实测** min/max 定区间
（`jpegr.cpp:973-974` 夹区间到 `[-14.3,15.6]`、`:1031-1038` 落 per-channel min/max boost），既满量程又不截顶。

**多通道 vs 单通道**：`:325-327` 逐通道独立（不取最大）⇒ 原则上无"取 max 致整体偏亮"的缺陷；
但因为 `ReinhardToSdr` 是**每像素统一 scale**（`SimdPixelOps.cs:171-175`），三通道比率恒等（除被钳位者），
所以 multiChannel 实际几乎不带来独立信息 ⇒ F4 的 420 摧毁在数值上损失有限，但**声明与交付不符**仍成立。
真正的逐通道风险在解侧：F8（第三方件的逐通道区间被丢掉，参照两趟会为三通道写不同区间 `jpegr.cpp:1031-1038`）。

### 3.3 底图色域映射与增益的耦合（F3 细节）

```
GainMapEncoder.cs:129
                SimdPixelOps.ReinhardToSdr(normHdr, sdrLinear, headroom);
GainMapEncoder.cs:140-141
                    SimdPixelOps.ApplyPrimariesMatrix(normHdr, gamutMtx);
                    SimdPixelOps.ApplyPrimariesMatrix(sdrLinear, gamutMtx);
GainMapEncoder.cs:152,156
                await WriteBgra8PngAsync(sdrLinear, w, h, basePng, ffmpeg, log, ct);   // ← FloatToSrgb8 钳 [0,1]
                byte[] gainMapPixels = ComputeGainMap(normHdr, sdrLinear, w, h, multiChannel, maxLog2Gain);
```

`ApplyPrimariesMatrix` 明确不钳位（`SimdPixelOps.cs:264` 注释），于是 `sdrLinear` 映射后可能 >1；
底图把它钳到 1，增益却除以 >1 的原值 ⇒ 恢复值 = `1·boost` 而非 `sdr·boost` ⇒ 少 `sdr` 倍。
自算矩阵（D65→D65，无适应）：

```
BT.2020(线性)->BT.709(线性):
  1.647517 -0.580147 -0.067370
 -0.123577  1.131299 -0.007721
 -0.018009 -0.016570  1.034579
2020 立方体 68921 采样：通道>1 的占 29.4%（最大 1.6475）；负值同比例（参照也钳负：jpegr.cpp:772,785 clipNegatives）
```

算例（默认 sRGB 底 + BT.2020 PQ 源 + 1000nits，饱和 2020 红峰值像素）：
`norm=(4.926,0,0)` → TM 得 `(1,0,0)`（2020 域）→ 映射得 `(1.6475,−0.124,−0.018)` → 底图存 `(1.0,0,0)`；
灰度增益按亮度比 = 4.926 ⇒ 恢复 `(4.93,0,0)`，真值 `(8.116,−0.609,−0.089)` ⇒ **R 暗 39%**，且丢失负通道
（负通道不可显示，可接受；问题只在 >1 被钳而不回灌）。
参照为何不犯：它的 SDR intent **本来就产在底图色域内**（钳位是色调映射的一部分），两侧都在 base gamut 里
取比率（`jpegr.cpp:769-772,785` 先两侧 `clipNegatives`+底图色域转换，`:792-798` 才 `encodeGain`）。
⇒ 修法二选一：矩阵先于定膝（并用亮度而非 max(r,g,b) 定膝 + 真正 gamut fit），或**按钳位后的底图重算增益**。

### 3.4 降采样与插值

`RescaleGainMap`(:344-377)：ceil 尺寸 + 块均值（对**已量化字节**求均值 = log2 域算术平均 = 几何均值 boost，
语义可接受，参照是按比例点采样 `getSamplePixelFn(..., mMapDimensionScaleFactor)`）；
但均值后又 `(byte)(sum/count)` 截断（:373）⇒ 第二段负偏置（F2）。
解侧双线性 + 像素中心 −0.5 约定（`GainMapDecoder.cs:1164-1176`）✅，并按**解码缓冲**钳索引、
按**声明逻辑尺寸**归一（:1168-1175 的注释即历史越界修复）✅。
`MapPrimary/MapSecondary` 写出与读回同字段（`:917-918` / `:995-998`）✅。

### 3.5 逐项误差实测（复刻复算，非产品路径）

夹具：`y ∈ [0, headroom]` 均匀 20001 点，`offset=1/64`，底图 8-bit，ISO 分母 100，字节截断。

| 峰值 | 组合最坏误差 | 仅 offset（其余理想） | 仅截断（其余理想） | 仅底图 8-bit | 仅 ISO 分数取整 |
|---|---|---|---|---|---|
| 1000 nits | +0.0786（16.0 nits） | **+0.0767（15.6 nits）** | −0.0305（恒负，100% 负向） | −0.0340（6.9 nits） | −0.0167（3.4 nits） |
| 4000 nits | +0.346（70.2 nits） | **+0.406（82.5 nits）** | −0.228（恒负） | −0.195（39.6 nits） | −0.120（24.4 nits） |

黑底借光表（F1 的空间表现；底图=0、经 4×降采样+双线性借到 boost 的情形）：

| 借到的 boost | 抬升(白相对) | nits | 等效 sRGB 码 |
|---|---|---|---|
| 1.25 | 0.00391 | 0.79 | **13** |
| 1.5 | 0.00781 | 1.59 | **22** |
| 2.0 | 0.01563 | 3.17 | **34** |
| 3.0 | 0.03125 | 6.34 | **49** |
| 4.926（1000nits 满档） | 0.06134 | 12.45 | **70** |
| 19.7（4000nits 满档） | 0.29219 | 59.3 | **147** |

⇒ 参照 `computeGain` 的暗像素守卫（`sdr<2/255 ⇒ gain=min(gain,2.3)`，`gainmapmath.cpp:775-780`）正是为压掉
这一项而存在；项目**没有**（只有 `eps=0.001` 的除零保护，且比参照的 2/255 阈值小 8 倍）。

**门禁为什么看不见 F1**（重要，避免"绿即无罪"）：`tests/GainMapTestHost/Program.cs:1237` 阈值 30 dB；
offset 项在均匀高动态夹具上的 RMS 折算约 48 dB 量级（远优于 30），实测 36.5 dB 由底图 JPEG 有损主导
⇒ **现阈值对 F1 完全不敏感**（不是"实测通过所以无事"，是"实测根本测不到"）。标签：验证缺口。

### 3.6 `FloatToSrgb8` 的 ±1 LSB 与增益档换算

```
SimdPixelOps.cs:97   return (byte)Math.Clamp(MathF.Round(s * 255f), 0, 255);        // 标量：to-even
SimdPixelOps.cs:136-137 var scaled = Avx.Add(Avx.Multiply(s, v255), vHalf); var rounded = Avx.ConvertToVector256Int32(scaled);   // SIMD：floor(x+0.5)
```

两路唯一系统性差异 = 平局规则（`s·255` 恰为 k+0.5 时差 1 LSB），LUT 近似误差量级 ~1e-7（1024 段线性插值，
`|d²log2/d f²|max=1.4427` ⇒ ≤1.7e-7）≪ 1 LSB ⇒ "±1 LSB"承诺诚实，D28 断言 `maxDiff<=1` 与之一致。

底图 1 LSB 折算成增益档（`bin = maxLog2(1000nits)/255 = 0.00902`）：

| sRGB 码 | +1 LSB 的 log2 变化 | 相当于几个增益档 |
|---|---|---|
| 16 | 0.1134 | **12.6** |
| 64 | 0.0441 | 4.9 |
| 128 | 0.0243 | 2.7 |
| 200 | 0.0161 | 1.8 |
| 250 | 0.0131 | 1.5 |

结论：`FloatToSrgb8` 的 ±1 LSB **不会**跨一个增益量化档（它根本不进增益字节）；
但"底图 8-bit 量化 ≫ 增益量化"在暗部成立（12.6 档），这是 JPEG 增益图设计的固有下限（参照同形），
项目额外的一点是**增益由钳位前的 float 算**（§3.3），使这一固有下限被放大成系统性偏差。

### 3.7 `PsnrCalculator` 与度量口径

- 峰值取 `MaxValueForBits(bits)`、MSE 取样本均值（`:52-58,71-73`）✅ 与惯例一致；16-bit 走标量、
  8-bit 走 SIMD，两条路共用同一 MSE 定义 ✅。
- 溢出账：SSE2 分支 `int32` 分块累加，单 lane 每轮 ≤8×255² = 520200，`ChunkRounds=4096` ⇒ 峰值 2.1307e9，
  距 `int32.MaxValue` 只剩 **0.78%**（`:295-297` 注释的"4096×2M≈8G < int32.MaxValue/4"写法与真实上界
  对不上，只是结论侥幸成立）⇒ 性能项/脆弱性：`ChunkRounds` 一旦上调即静默溢出，建议每轮归约或改 int64 累加。
- 多帧尾段（`:126-139`）把不足一帧的字节当**独立帧**参与 min/max ⇒ 报告的 `psnr_min` 可能来自半帧，
  与 ffmpeg `psnr` filter 的"整帧极值"语义不同 ⇒ 文档过期（`:90-93` 承诺"语义与 ffmpeg psnr filter 一致"）。
- `isRgb` 只作标注、不改数值（`:45` 注释已明说）✅ 无口径陷阱。

---

## 4 新发现（按可修性排序，附"红着"的验证方案）

1. **F1 offset 声明/施加不一致（先修这个）** — 标签：真缺陷-标准 + 真缺陷-精度。
   证据：`GainMapEncoder.cs:838,868-869,887,924-925`（写 1/64）vs `:325-327`/`SimdPixelOps.cs:215`（前向比率无 offset）；
   `GainMapDecoder.cs:1194` 施加 offset。
   修法二选一：前向改 `log2((hdr+offsetHdr)/(sdr+offsetSdr))`（与参照同形、且天然消解除零 eps），
   或元数据 offset 改 0（参照一趟路径即 0.0）。
   验证（不许放宽）：夹具含 `sdr=0, boost=headroom` 的像素，断言 `|decoded − 真值| ≤ 1 个增益档·sdr + 0.5 LSB 底图`，
   **当前必红**（预测 15.6 nits）；并加一条"元数据 offset>0 ⇒ 前向必须含它"的结构锁（读 `BuildIso21496Metadata` 的
   offset 分数，与 `ComputeGainMap` 的表达式做互逆断言）。
2. **F2 截断偏置** — 真缺陷-精度。`:337`、`:373`（另 `SimdPixelOps.cs:225` 同源）。
   对照 `affineMapGain` 的 `CLIP3(mappedVal + 0.5f, 0, 255)`。
   验证：对同一 log2 增益扫 ±0.4 档，断言量化误差不带符号偏置（当前 100% 负向）；断言 round-to-nearest 后
   往返峰值误差比当前降 ≥0.3 档。
3. **F4 多通道增益图被 4:2:0 摧毁** — 真缺陷-标准。`:500` 硬编码 `--chroma_subsampling 420`；
   参照 RGB 增益图 4:4:4（`jpegencoderhelper.cpp:41-42`）。
   验证：`multiChannel=true` 时断言副图 SOF 采样因子 = `1x1,1x1,1x1`（现产为 `2x2` ⇒ 红）。
4. **F5 AVIF 出口通道数宣称与交付不符** — 真缺陷-静默失效。`:582` 恒 `gray` vs `:849` bit7。
   验证：断言 tmap `flags` 的多通道位 == 增益图 item 的平面/分量数（AVIF 现产红）；或在 multiChannel 时改走
   `yuv444p`/rgb 并点名。
5. **F6 XMP RDF 命名空间 URI 拼错**（`:899`）+ hdrgm 写在主图（参照写副图，`jpegr.cpp:1141`、`jpegrutils.cpp:915-940`）。
   标签：真缺陷-标准（对外互操作；exiftool 容忍，见 `tests/output/final-verify-gainmap-engine.log:55` 2026-09-19 PASS）。
   验证：把 URI 改正后断言 exiftool 仍读到 `XMP-hdrgm:Channels`（回归护栏）；再用严格 XML 解析（或 libultrahdr
   `parseXmpMetadata`）断言能解出 `GainMapMax`——当前 URI 下严格解析器应失败 ⇒ 新断言先红，属正确红。
6. **F7 门禁注释与代码/读数互相矛盾**（`verify-gainmap-managed.ps1:74-78` 称"不写 XMP"）⇒ 文档过期；
   该处 6 条断言已被改成"用可推导期望值校验"，把原本能暴露 F6 的交叉校验**换掉了**（改法本身没错，但结论行
   把"读不到"归因成"没写"，掩盖了真成因）。
7. **F8 解侧丢弃逐通道区间**（`GainMapDecoder.cs:761-799` 只在 `c==0` 落值；`GainMapMetadata` 只有标量字段 `:44-49`）
   ⇒ 第三方多通道件色偏。参照两趟会写三通道不同区间（`jpegr.cpp:1031-1038`）。
8. **F9 峰值自动档 GUI 不可达 + 截顶静默**（`MainWindow.xaml.cs:5837-5842` 夹 [200,10000]、`FfmpegOptions.cs:376` 默认 1000、
   `GainMapJpegCodec.cs:119-121` 用户档优先）。4000nits 素材按 1000 截顶无任何点名；400nits 素材浪费量程
   （实测只用 42.5% ⇒ 有效档宽 2.35×、相对误差 1.48%）。
   验证：给 auto 档（GUI 可表达 0），或在"实测内容峰值 > 声明峰值"时点名截顶；断言 = 声明 1000、喂 4000nits 斜坡，
   HDR 侧 >1000 的部分必须要么在日志里点名、要么被保留。
9. **F10 缺暗像素增益上限守卫**（参照 `gainmapmath.cpp:775-780`，项目只有 `eps=0.001`）。与 F1 合成 halo。
10. **F11 ISO 分数分母 100**（`:835`）：声明值量化到 0.01 log2 而字节量化用未取整值 ⇒ 两侧标度差 ±0.35%。
    小项，但属"自家产物自身不完全互逆"，与 F1 同类；改法：字节量化也用同一个取整后的 `gainLog2N/100`。
11. **F12 灰度增益图复制成 RGB 再过 420**（`:434-441`，注释自陈"未验证的假设：cjpegli 不支持灰度 JPEG 输入?"）。
    数值无损（等通道 ⇒ Y=值、色度中性），代价是体积/时间；`cjpegli` 选项表确无 `--grayscale`
    （`tools/src/jpegli/tools/cjpegli.cc:35-110` 全表）⇒ 假设方向对但**未实测灰度 PNG 输入** ⇒ 记 `未测`，标签：性能项。
12. **死代码 + 反向注释**（`GainMapEncoder.cs:259-294,679-687` 三个零调用私有方法；
    `SimdPixelOps.cs:160` 声称与死代码语义一致）⇒ 文档过期 + 陷阱；标签另有 A4 lane 覆盖等价性，此处只登记行号。
13. **PsnrCalculator 两处**：SSE2 int32 累加余量仅 0.78%（`:295-297`）；多帧尾段被当独立帧进 min/max
    与"语义与 ffmpeg psnr filter 一致"的承诺不符（`:90-93` vs `:126-139`）。

---

## 5 未证边界（写清楚缺什么证据）

- **端到端数值未测**：F1/F2/F3/F11 的量级来自**逐行复刻的复算**，未跑 `GainMapTestHost`/产品 exe
  （本机 `%TEMP%` 拦截 ⇒ 成片 `exit -13` 风险）。要跑：`TMP/TEMP` 指到 `tests/output/pipeline-audit-2026-10-10/tmp_b1/`、
  先跑 host 的 D28/D29/D30 作夹具自检（自检红就整批作废），再取 B 段 PSNR/maxErr。标签：未测。
- **F6 的外部可读性未判定**：exiftool 容忍（自家日志为证），但 AndroidX/Adobe/严格 XMP 解析器是否据此**放弃**
  hdrgm 标签 ⇒ 未判定(需一台能跑 UltraHDR API 的参照件或 libultrahdr `parseXmp` 实测)。
- **cjpegli 灰度输入能力未测**（见 F12）：需一次 `cjpegli gray.png out.jpg` 实跑。
- **ffmpeg 是否对带 ICC 的底图解算色彩**：本构建 `-h full` 无 `apply_icc`（只有 `icc_profiles` 解析/生成与
  muxer 侧 `prefer_icc`），据此判"不解算 ⇒ 解侧『按 ICC 标原色、像素原样』是对的"（`GainMapDecoder.cs:293-294,324-343`），
  但这是**静态推断** ⇒ 未判定(需一条"底图 ICC=bt2020 ⇒ 解出像素值不变"的实测断言)。
- **HLG 源的 `npl` 语义**：`GainMapJpegCodec.cs:111` 把 PQ 与 HLG 同判为 HDR 输入并用同一 `npl` 串；
  HLG 的名义峰值与 PQ 不同义 ⇒ 未判定(需 zimg/ffmpeg 侧 HLG `npl` 行为的实测或文档锚)。
- **ISO-BMFF 增益图 item 不带 colr**：libavif 注释要求增益图 `colorPrimaries/transferCharacteristics = 2`
  （`include/avif/avif.h:634-637`），项目 `:582` 传 `null` ⇒ 但 libavif 施加时**不**对增益样本做传递解码
  （`gainmap.c:266` 直接用 `gainMapValue`）⇒ 数值上无影响，容器合规交 lane B2 判定。
- **反向（HDR 底）两条分支口径不一**（`GainMapDecoder.cs:169-173` BMFF 返回 null vs `:282-283` JPEG 仅告警仍按 SDR 底）：
  是否会让 JPEG 侧反向件产出错图 ⇒ 未判定(需一个 `BaseRenditionIsHDR=True` 的 JPEG 夹具)。
- **门禁盲区已证但读数未测**：`GainMapTestHost` 30 dB 阈值对 F1 不敏感（§3.5 由 RMS 折算推断，
  未实测该 RMS 的产物侧读数）⇒ 标签：未判定(需实跑 B 段 maxErr)。
