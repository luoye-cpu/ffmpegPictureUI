# GainMap 参照裁决备忘（实现备忘 lane R1 · 2026-10-11）

只研究、不改码。权威源 = 本机 checkout（未抓任何网页）：
`tools/src/libultrahdr`（Apache-2.0，下称 **LU**）、`tools/src/libavif`（下称 **LA**）、
`tools/src/libjxl`（见 §7，其快照**不含**增益图数学）。
本仓侧行号全部在 HEAD=c18dc4a 复核（`GainMapEncoder.cs`/`GainMapDecoder.cs`/`SimdPixelOps.cs` 工作树干净）。
FIX_PLAN_2026-10-11 的 F-5（offset）、F-6（分母）即本文 Q1/Q3，裁决标签：**裁决** = 参照给出一手代码依据；**未判定(需要X)** = 缺料点名。

## 0 一页裁决

| # | 问题 | 一句话裁决 | 标签 |
|---|---|---|---|
| Q1 | 正向公式与互逆性 | 参照不变量 = **元数据声明的 offset/min/max/γ 与前向实际用的是同一份数**（三条形：LU两趟 1e-7 声明+前向施加；LU一趟 0 声明+前向不用；LA 默认 1/64 声明+前向施加）⇒ 本仓选"前向按声明施加 1/64"修法，公式与 LA `gainmap.c:712` 逐字同形 | 裁决 |
| Q2 | 取整口径 | 本仓是**一趟**（量程=声明 headroom、无内容统计遍）⇒ 参照一趟锚 `encodeGain` 同为**截断**，`:337/:225` 的取整**不动**；只有整体升级两趟（min/max 改实测）才允许 `+0.5`。`:373` 块均值截断参照无对应物（参照点采样），参照不裁它 | 裁决（`:373` 交实现自选） |
| Q3 | 增益分母取哪个 | 参照的分母恒等于**解码器将从底图读到的那个（已钳、已量化的）显示值**；且架构上从不把 base 侧矩阵外推出可显示域 ⇒ 本仓应当按钳位（乃至 8-bit 量化）后的底图值回灌分母，而非钳前 float | 裁决 |
| Q4 | 多通道采样/通道数 | 参照 3 通道增益图 = **RGB 三分量 JPEG，采样因子全 1（4:4:4）**；1 通道 = 灰度单分量。读侧通道数**以 JPEG SOF numComponents 为准、不信 flag** ⇒ "声明与交付一致"是写侧义务：multiChannel=true 不得过 4:2:0；AVIF 交付 gray 则不得置 bit7 | 裁决 |
| Q5 | 逐通道参数 | ISO/`tmap` **允许** min/max/offset 逐通道不同（参照两趟真实写出不同值），γ 亦为逐通道字段（libavif 逐通道施加；libultrahdr LUT 路径逐通道、标量路径用 γ[0]——参照内部不一致，从严取逐通道）⇒ 本仓解侧只取 `c==0` 对第三方多通道件产生逐通道 affine 误差 = 色偏，修法=保留三套并按通道施加 | 裁决 |
| Q6 | F1 误差上界 | 与 headroom 自洽的纸面上界：`Δ = (boost−1)·offsetSdr ≤ (headroom−1)/64`（SDR 白单位）⇒ **1000nits 12.45 nits / 4000nits 59.33 nits**（旧头条 15.6/82.4 已被验证 lane 判死，不许复活）；端到端量级**未判定(需要 float 域往返 maxErr 一格读数)** | 裁决（上界）/ 未判定（实测） |

---

## Q1 正向增益精确公式（log2 底、各参数出现的位置、声明==使用）

### 参照条形（LU 两趟，默认质量路径的逆向：`mEncPreset != UHDR_USAGE_REALTIME` ⇒ `generateGainMapTwoPass`，jpegr.cpp:1051-1055）

前向（log2 域比率，**含 offset**）——`libultrahdr/lib/src/gainmapmath.cpp:773-782`：

```cpp
float computeGain(float sdr, float hdr) {
  float gain = log2((hdr + kHdrOffset) / (sdr + kSdrOffset));
  if (sdr < 2.f / 255.0f) {
    ...
    gain = (std::min)(gain, 2.3f);
  }
  return gain;
}
```

offset 常数——`libultrahdr/lib/include/ultrahdr/gainmapmath.h:549-550`：

```cpp
static const float kHdrOffset = 1e-7f;
static const float kSdrOffset = 1e-7f;
```

量化到字节（`+0.5` 取整，见 Q2）——`gainmapmath.cpp:784-789`：

```cpp
uint8_t affineMapGain(float gainlog2, float mingainlog2, float maxgainlog2, float gamma) {
  float mappedVal = (gainlog2 - mingainlog2) / (maxgainlog2 - mingainlog2);
  if (gamma != 1.0f) mappedVal = pow(mappedVal, gamma);
  mappedVal *= 255;
  return CLIP3(mappedVal + 0.5f, 0, 255);
}
```

**声明**（与前向同一份数）——`jpegr.cpp:1031-1042`（节选）：

```cpp
      gainmap_metadata->max_content_boost[i] = exp2(gainmap_max[i]);
      gainmap_metadata->min_content_boost[i] = exp2(gainmap_min[i]);
...
    std::fill_n(gainmap_metadata->gamma, 3, this->mGamma);
    std::fill_n(gainmap_metadata->offset_sdr, 3, kSdrOffset);
    std::fill_n(gainmap_metadata->offset_hdr, 3, kHdrOffset);
```

即：`min/max_content_boost` 存的是**线性 boost**，其 **log2** 才落 ISO 分数（读侧 `exp2(field)`，`gainmapmetadata.cpp:332-333`；写侧 `log2(from->max_content_boost[i])` 化分数，`gainmapmetadata.cpp` CONVERT 段 `:386-389`）。γ 默认 1.0（`ultrahdrcommon.h:443` `kGainMapGammaDefault = 1.0f`）。

解码施加——`gainmapmath.cpp:791-797`：

```cpp
Color applyGain(Color e, float gain, uhdr_gainmap_metadata_ext_t* metadata) {
  if (metadata->gamma[0] != 1.0f) gain = pow(gain, 1.0f / metadata->gamma[0]);
  float logBoost = log2(metadata->min_content_boost[0]) * (1.0f - gain) +
                   log2(metadata->max_content_boost[0]) * gain;
  float gainFactor = exp2(logBoost);
  return ((e + metadata->offset_sdr[0]) * gainFactor) - metadata->offset_hdr[0];
}
```

各参数出现的步骤：γ 只在**字节↔log2 的仿射域**（前向 `pow(norm,γ)`、解码 `pow(g,1/γ)`）；`min/max_content_boost` 是仿射端点；`offset_sdr` 加在**分母侧**（前向）与**基侧**（解码），`offset_hdr` 加在**分子侧**（前向）与**结果侧**（解码减）。严格互逆验证：前向 `b/255 = ((L−Lmin)/(Lmax−Lmin))^γ`，解码 `g=(b/255)^(1/γ)`、`L' = lerp(Lmin,Lmax,g) = L`、`h' = (s+o_s)·2^L − o_h`，代入 `2^L=(h+o_h)/(s+o_s)` 得 `h' = h` **恒等**（模量化）⇒ offset 必须两侧同在才闭环；本仓"前向无 offset、解侧有"恰好破坏此恒等。

### 参照条形之二（LU 一趟，Android 默认：`ultrahdrcommon.h:440` `kEncSpeedPresetAndroidDefault = UHDR_USAGE_REALTIME` ⇒ `jpegr.cpp:1051-1052` 走 `generateGainMapOnePass`）

`jpegr.cpp:724-728`：

```cpp
    std::fill_n(gainmap_metadata->max_content_boost, 3, hdr_white_nits / kSdrWhiteNits);
    std::fill_n(gainmap_metadata->min_content_boost, 3, 1.0f);
    std::fill_n(gainmap_metadata->gamma, 3, mGamma);
    std::fill_n(gainmap_metadata->offset_sdr, 3, 0.0f);
    std::fill_n(gainmap_metadata->offset_hdr, 3, 0.0f);
```

前向 `encodeGain`（`gainmapmath.cpp:758-771`，节选）不用 offset：

```cpp
  float gain = 1.0f;
  if (y_sdr > 0.0f) {
    gain = y_hdr / y_sdr;
  }
  ...
  float gain_normalized =
      (log2(gain) - log2MinContentBoost) / (log2MaxContentBoost - log2MinContentBoost);
  float gain_normalized_gamma = powf(gain_normalized, metadata->gamma[index]);
  return static_cast<uint8_t>(gain_normalized_gamma * 255.0f);
```

⇒ 声明 0、前向 0，**依然满足"声明==使用"不变量**。

### 参照条形之三（LA，1/64 的出处）

默认分数——`libavif/src/gainmap.c:21-24`（`avifGainMapSetEncodingDefaults`）：

```c
        gainMap->baseOffset[i] = (avifSignedFraction) { 1, 64 };
        gainMap->alternateOffset[i] = (avifSignedFraction) { 1, 64 };
```

前向**使用**这两个数——`gainmap.c:712-713`（`avifRGBImageComputeGainMap`）：

```c
                const float ratio = (alt + alternateOffset[c]) / (base + baseOffset[c]);
                const float ratioLog2 = log2f(AVIF_MAX(ratio, kEpsilon));
```

且当色域转换产生负值时**放大 offset、再把放大后的值写回元数据**（`gainmap.c:653-665` 调整、`:745-752` 写回 `alternateOffset/baseOffset`）⇒ 声明的仍是前向实际用的那份数。机制动机（partial application 时大 offset 有伪影，故宁选大色域+小 offset）：`gainmap.c:495-506` 注释"…a large offset causes artefacts when partially applying the gain map. …we want to do gain map math in the larger of the two color spaces"。解码侧同 LU——`gainmap.c:266-267`（lerp+`exp2f`，两侧 offset 均在）。

### 对本仓的裁决

F1 两修法参照都有先例（①=LA 式：声明 1/64 且前向施加；②=LU 一趟式：声明 0 且前向不用）。**裁①**，理由（非参照唯一裁定，是本仓既有事实使①成为"与参照同形且改动面最小"的一支）：本仓解侧 `GainMapDecoder.cs:1194/1208` 与 XMP/ISO 均已声明并施加 1/64（`GainMapEncoder.cs:32 KOffset=0.015625f`、`:838/:868-869`）；1/64 正是 libavif 的互操作默认值（仓内 libavif 交叉读数 offset=0.015625 见 HANDOVER）。修法：前向改 `L = log2((hdr+offset_hdr)/(sdr+offset_sdr))`（与 `gainmap.c:712` 同形；`Math.Max(ratio,1)` 地板与 `LogGainToByte` 钳位保持 byte0↔boost1 恒等，因 `(s+o)·2^0−o=s`）；顺带天然消解除零 eps。
**标签：裁决。**

---

## Q2 一趟 vs 两趟的量化取整；本仓趟数判定；取整该不该动

参照两条口径（原文见 Q1 引用块）：

| 趟数 | 参照函数 | 取整 | 量程来源 |
|---|---|---|---|
| 一趟 | LU `encodeGain`（gainmapmath.cpp:770） | **截断** `static_cast<uint8_t>(v * 255.0f)` | 声明值：min=1、max=hdrWhite/sdrWhite（jpegr.cpp:724-725） |
| 两趟 | LU `affineMapGain`（gainmapmath.cpp:788） | **`+0.5`** 舍入 | 实测内容 min/max（clamp 到 [-14.3,15.6]，jpegr.cpp:969-974） |
| LA（先量统计再量化，等效两趟） | 量化 `powf((v−min)/range, γ)`（gainmap.c:779-781）+ 落字节 `reformat.c:1929-1931` `(uint8_t)(0.5f + (rgbaPixel[c] * info->maxChannelF))` | **`+0.5`** 舍入 | 实测（去离群）min/max（gainmap.c:737-741 `avifFindMinMaxWithoutOutliers`） |

**本仓趟数判定**（读 `GainMapEncoder.cs:320-380` + 上游）：`:107-108` 量程 `maxLog2Gain` 直接由**声明的目标 nits** 得出（无任何内容统计遍）；`:321-328` 单循环从 float hdr/sdr 直出字节，`:333-338` `LogGainToByte` 钳 `[0,maxLog2Gain]` 后截断——与 `encodeGain:765-770` 同形（min=1 ⇒ log2Min=0，归一除数=maxLog2）。⇒ **本仓走一趟**（两趟形态 = 先扫实测 min/max 再把同一组实测值写进元数据端点，参照 jpegr.cpp:863-941 统计遍 + :988-1038 量化与声明）。

**裁决**：既然口径要与趟数一致，本仓当前（一趟）**保持截断**——`:337`/`SimdPixelOps.cs:225` 与参照一趟 `encodeGain:770` 等价，**不该动**；单把取整改 `+0.5` 会造成"声明端点仍为定值、字节序列却与参照一趟产物不等价"的半改状态（正是审查纪律禁止的那一步）。若要 `+0.5`，必须整体转两趟：先遍实测 per-channel min/max、钳 [-14.3,15.6]（jpegr.cpp:973-974）、声明==量化用同一组实测值，届时按 `affineMapGain:788` 用 round-to-nearest。
**两处例外（参照不裁，本仓自由）**：① `:373` 降采样块均值的第二段截断在参照**没有对应物**——参照是比例点采样（`jpegr.cpp:759/872` `sdr_sample_pixel_fn(..., mMapDimensionScaleFactor, x, y)`），不平均、因此天然只有一次量化 ⇒ 这一处不是"对齐参照"问题，改 round 或改点采样都不违反参照（改点采样离参照更近）。② 参照一趟的 sdr≤0 分支是 `gain=1.0`（`encodeGain:760-763`），本仓 eps=0.001 地板属 F10 议题，不在此裁。
**标签：裁决（趟数→口径映射）；`:373` 未判定(参照无对应物，交实现自选)。**

---

## Q3 增益分母：钳后显示值 vs 钳前线性值

参照不变量：**分母必须等于解码器将从底图 rendition 读回的同一个值**。三处一手证据：

1. 底图**先硬钳后存储**（钳位发生在 rendition 生成时，不在读取时）——`jpegr.cpp:2172-2177`（toneMap）：

```cpp
              Color sdr_rgb = hdrGamutConversionFn(sdr_rgb_linear_bt2100);

              // Hard clip out-of-gamut values;
              sdr_rgb = clampPixelFloat(sdr_rgb);

              Color sdr_rgb_gamma = srgbOetf(sdr_rgb);
```

   （`clampPixelFloat` = 钳 [0,1]：`gainmapmath.h:559-563`，`kMaxPixelFloat = 1.0f`。）

2. 增益分母**采自这张已钳已量化的底图**——两趟 `jpegr.cpp:871-885`（节选）：

```cpp
              sdr_rgb_gamma = sdr_sample_pixel_fn(sdr_intent, mMapDimensionScaleFactor, x, y);
...
            Color sdr_rgb = srgbInvOetf(sdr_rgb_gamma);
            sdr_rgb = sdrGamutConversionFn(sdr_rgb);
            sdr_rgb = clipNegatives(sdr_rgb);
```

   且默认（写 XMP 的互操作形态）`sdrGamutConversionFn = identityConversion`（`jpegr.cpp:609-622`）：**base 侧根本不过矩阵**——色域不同时被转换的是 HDR 侧进底图色域（`:613 hdrGamutConversionFn = getGamutConversionFn(sdr_intent->cg, hdr_intent->cg)`，注释 :618-619"from hdr cg to sdr cg"）⇒ 分母恒是底图自己的显示值，">1 被钳"这一支在本参照架构里**结构上不存在**（8-bit gamma 域采样反解回线性必 ≤1.0）。`clipNegatives` 只钳负（`gainmapmath.h:552`）。一趟路径同形（`jpegr.cpp:759-772`）。
   解侧对应物：`applyGainMap` 的基值同样取存储底图 `srgbInvOetf(get_pixel_fn(sdr_intent,...))`（`jpegr.cpp:1719-1731`）⇒ 编解码两侧分母同源。

3. LA 更直白：分母**直接读 base 存储像素**——`gainmap.c:676-697`：`avifGetRGBAPixel(baseRgbImage, i, j, ...)` → `baseGammaToLinear` →（必要时转色域）→ `gainmap.c:712` 的 `(alt + alternateOffset[c]) / (base + baseOffset[c])`。越界负值靠"选更大的计算色域"（`avifChooseColorSpaceForGainMapMath`，gainmap.c:508-543）+ **offset 吸收**（`:653-665`，maxOffset=0.1 钳，`:745-752` 写回声明）处理；base>1 不可能（存储量化值归一化 ≤1）。

**对本仓的裁决**：F3 的分歧点（`:152` 底图经 `FloatToSrgb8` 钳 [0,1] vs `:156` 增益除以钳前 `sdrLinear` float，2020→709 后 32.4% 通道 >1，验证 lane §1.3 数字）⇒ **参照裁"分母取钳位后的显示值"**，且更严格地说取"与写进底图同源、解码器读回的那个值"（本仓至少应把增益分母改为 `clamp01(sdrLinear)`；如要完全同形，还应先做 8-bit sRGB 量化→反量化回灌，即底图真正交付什么就除以什么）。参照的第三条路（LA：改在**更大的色域**做增益数学、用 offset 吸收负值、把最终 offset 写进声明）同样合法，但它改变 useBaseColorSpace 语义与元数据，改动面大于"分母回灌"。
**标签：裁决。**

---

## Q4 多通道增益图的采样与通道数约定

参照写侧——`jpegr.cpp:714-716` 通道数决定图像格式：

```cpp
  gainmap_img = std::make_unique<uhdr_raw_image_ext_t>(
      mUseMultiChannelGainMap ? UHDR_IMG_FMT_24bppRGB888 : UHDR_IMG_FMT_8bppYCbCr400,
      hdr_intent->cg, hdr_intent->ct, hdr_intent->range, map_width, map_height, 64);
```

JPEG 采样因子表——`jpegencoderhelper.cpp:41-42`（RGB888 全 1×1 ⇒ **4:4:4**）与 `:27-28`（400 = 单分量灰度）：

```cpp
    {UHDR_IMG_FMT_24bppRGB888,
     {1 /*h0*/, 1 /*v0*/, 1 /*h1*/, 1 /*v1*/, 1 /*h2*/, 1 /*v2*/, 1 /*maxh*/, 1 /*maxv*/}},
```

应用点：`jpegencoderhelper.cpp:165-171`（RGB888 ⇒ `input_components=3, JCS_RGB`；400 ⇒ `input_components=1, JCS_GRAYSCALE`）、`:189-192`（`cinfo.comp_info[i].h_samp_factor = factors[...]`）、`:197`（RGB 非 raw YCbCr ⇒ 色度零降采样）。⇒ **参照 RGB 增益图确为 4:4:4，灰度增益图确为单分量灰度 JPEG**。

读侧"布局信流不信 flag"——`jpegr.cpp:2843-2844`：

```cpp
    output.fmt =
        gainmap_image.numComponents == 1 ? UHDR_IMG_FMT_8bppYCbCr400 : UHDR_IMG_FMT_24bppRGB888;
```

元数据 bit7 的真实语义——`gainmapmetadata.cpp:127-135`（节选，含参照自认的规范歧义）：

```cpp
  // Always write three channels for now for simplicity.
  // TODO(maryla): the draft says that this specifies the count of channels of the
  // gain map. But tone mapping is done in RGB space so there are always three
  // channels, even if the gain map is grayscale. Should this be revised?
  const uint8_t channelCount = in_metadata->allChannelsIdentical() ? 1u : 3u;
```

（`kIsMultiChannelMask = (1u << 7)`：`gainmapmetadata.h:20`；读回 `channelCount = ((flags & kIsMultiChannelMask) != 0) * 2 + 1`：`gainmapmetadata.cpp:221`。）即 LU 把 bit7 用作"**参数集数**"而非图像通道数；参数集与图像通道数的耦合见 Q5。
LA 侧：`singleChannel = (gainMap->image->yuvFormat == AVIF_PIXEL_FORMAT_YUV400)`（`gainmap.c:587`），元数据**恒写三套分数**、灰度时把 ch0 复制到 c1/c2（`gainmap.c:743-752` `gainMapMinLog2[singleChannel ? 0 : c]`）⇒ "声明参数==实际参数"在灰度情形靠复制成立；编码器只接受 {444,422,420,400}（`write.c:2404-2407`），但那是 alt 图像项（`altPlaneCount==1 ⇒ YUV400 else YUV444`，`write.c:1062`——AVIF 侧辅助图非 YUV 即 4:4:4，没有 4:2:0 的辅助 RGB 语义）。

**对本仓的裁决**：① F4 成立且修法唯一——`GainMapEncoder.cs:500` 的 `--chroma_subsampling 420` 在 `multiChannel=true` 时必须改 4:4:4（或直接 RGB 分量交付），否则逐通道数据被色度下采样摧毁 = 声明与交付不符；参照读侧信流不信 flag（`:2843`）**救不了写侧**，一致性是生产者义务。② F5（AVIF 恒 gray 却置 bit7）：照参照，交付 1 通道就把参数**复制成一致三套并清 bit7**（LA 手法）或按 LU 让 bit7 反映"参数是否全同"；二者都要求 bit7/平面数/参数集三者自洽。③ 附带：本仓灰度图复制 RGB 再过 420 的做法（F12）参照没有——灰度就直接单分量灰度（`:27-28` 行）。
**标签：裁决。**

---

## Q5 解侧逐通道参数

**允许逐通道不同吗——允许**，且参照真写不同值：

- LU 两趟 multiChannel：`jpegr.cpp:1031-1034` 逐通道落 `min/max_content_boost[i] = exp2(gainmap_min/max[i])`（统计遍 `:908-911` per-channel min/max）；ISO 每通道五个字段独立序列化：`gainmapmetadata.cpp:386-396`（`log2(max)`、`log2(min)`、`gamma`、`baseOffset`、`alternateOffset` 均带 `[i]`）。
- LU 解侧施加逐通道：仿射端点与 offset——`gainmapmath.cpp:812-827`（`applyGain(Color e, Color gain,...)`：`logBoostR/G/B` 用 `[0]/[1]/[2]`，`offset_sdr[0..2]/offset_hdr[0..2]`）；LUT 路径连 γ 都逐通道——`gainmapmath.h:454-466`：

```cpp
    bool isSingleChannel = metadata->are_all_channels_identical();
    for (int i = 0; i < (isSingleChannel ? 1 : 3); i++) {
...
      this->mGammaInv[i] = 1.0f / metadata->gamma[i];
```

  （`are_all_channels_identical`：`ultrahdrcommon.h:218-224`。）**注意参照内部不一致**：非 LUT 的 `applyGain` 三通道共用 `gamma[0]`（`:813-815`），而 LUT 路径与 libavif 逐通道。
- LA 解侧全程逐通道（含 γ）——`gainmap.c:263-267`：

```c
                const float gainMapValue = gainMapRGBA[c];
                // Undo gamma & affine transform; the result is in log2 space.
                const float gainMapLog2 = lerp(gainMapMin[c], gainMapMax[c], powf(gainMapValue, gammaInv[c]));
                const float toneMappedLinear = (baseLinear + baseOffset[c]) * exp2f(gainMapLog2 * weight) - alternateOffset[c];
```

- LU 单通道件的"剩余通道复制 ch0"发生在**解析层**（`gainmapmetadata.cpp:274` 注释"Fill the remaining values by copying those from the first channel."）——即"只取通道 0"是参照对 **1 参数集** 文件的补全动作，不是对 **3 参数集** 文件的施加动作。

**本仓只取 `c==0` 的后果**（`GainMapDecoder.cs:768/:792` 两支均 `if (c==0)` 落标量字段）：对第三方 3 通道件（LU 两趟 / LA 产物，逐通道区间确有差异），通道 c 被按通道 0 的仿射解出：
`ΔL_c = (min_c−min_0)·(1−g) + (max_c−max_0)·g`（g 为通道 c 自己的归一化增益字节），叠加逐通道 offset 差被抹平 ⇒ 三通道恢复值出现**通道相关 log2 偏差 = 色偏**，且不能靠 display-boost 全局标度修正（`gainmapWeight` 只缩 L 不补通道差，`jpegr.cpp:1680-1689`）。对自家产物无害（`:863-870` 三通道写同值）⇒ 该缺陷只在解第三方件时发作（与 B1-F8 定性一致）。
**裁决**：解侧 `GainMapMetadata` 改为按通道数组（min/max/γ/baseOffset/alternateOffset 各 3），`channels==3` 时逐通道施加（照抄 `gainmap.c:263-267` 的逐通道形，γ 从严取逐通道）；`channels==1` 时按参照解析层复制 ch0。
**标签：裁决。**

---

## Q6 F1 误差量级：与 headroom 自洽的纸面上界

**公式**（参照式反推，非复刻近似）：现状前向 `2^L = h/s`（`GainMapEncoder.cs:325-327`、`SimdPixelOps.cs:215` 无 offset），解侧 `h' = (s+o)·2^L − o`（`GainMapDecoder.cs:1194`，`o = offsetSdr = offsetHdr = 1/64`，`GainMapEncoder.cs:32/:838`）⇒

`h' − h = o·(h/s − 1) = (boost − 1)/64`（白单位；验证 lane §1.1 已独立推导同式）。

前向钳位（`GainMapEncoder.cs:336` `Math.Clamp(logGain, 0, maxLog2Gain)`）与声明上限（`:841/:844`）把 boost 钉死在 `1 ≤ boost ≤ headroom`（headroom = hdrPeak/203，`:107`）⇒

**上界 = (headroom − 1)/64**：
- 1000 nits：headroom = 1000/203 = 4.9261 ⇒ 0.061345 白 ⇒ **12.45 nits**；
- 4000 nits：headroom = 19.7044 ⇒ 0.292256 白 ⇒ **59.33 nits**；
- 相对误差上界 = (boost−1)/(64·boost)，峰值处 1000nits = 1.245%、4000nits = 1.483%。

参照佐证该界不可再大：`computeGain` 的暗部守卫（`gainmapmath.cpp:775-780`）把 `sdr<2/255` 像素的 L 钳到 ≤2.3（boost≤4.93）——它**只压低**暗像素借光（黑底抬到码 34 那类，verify §1.1 黑底表），不改变全图最大误差点；最大误差点恒在 **boost=headroom 的高光像素**（误差随 boost 单调增）。旧头条"15.6/82.4 nits"反解 boost=5.91/27.0 > headroom，与公式定义域矛盾，已由验证 lane 判死，本上界为唯一自洽读数。
**标签：纸面上界 = 裁决。**

**实测需要哪一格读数**（不在本 lane 跑）：
1. 一格**进程内 float 域往返 maxErr**（`GainMapTestHost`，不经 JPEG 有损）：夹具 = 底图 `sdr ≡ 0.5`（或常量斜坡）、`hdr = sdr·headroom`，读数期望 **恰为 (headroom−1)/64·203 = 12.45 nits（1000 档）/ 59.33 nits（4000 档）**；显著超出 ⇒ 存在第二来源（转 §5 未判定）。
2. 一条**结构锁**（静态即红）："`BuildIso21496Metadata` 声明的 offset == 前向 `ComputeGainMap` 实际施加的 offset"（FIX_PLAN F-5/L-5 同款）。
端到端（含 JPEG 有损、底图 8-bit、ISO 分数取整）的合成量级：**未判定(需要第 1 格 + 现有 PSNR 段的 maxErr 读数)**。

---

## §7 libjxl 与未判定清单

- **libjxl 本机没有增益图数学**（判零前已正向命中）：`tools/src/libjxl/lib/include/jxl/gain_map.h:10` 自述 *"Utility functions to manipulate jhgm (gain map) boxes"*；`lib/extras/gain_map.cc` 对 `log2|powf|exp2` **零命中**（仅 bundle 序列化）。⇒ 本备忘不引用其数值语义。**未判定(需要X)**：若需 jxl 侧 apply 数学，本机没有一手实现。
- **未判定项汇总**：
  1. F1 端到端 nits 读数——需要 Q6 第 1 格（进程内 float 往返 maxErr）。
  2. `:373` 块均值第二截断——参照无对应物（参照点采样 `jpegr.cpp:759/872`），参照不裁；本仓自选（点采样最贴参照）。
  3. 逐通道 γ——参照内部两条路不一致（LUT 逐通道 vs 标量 `applyGain` 用 γ[0]），本备忘采"从严=逐通道"（libavif+LUT 同形）；如日后要求与 libultrahdr 标量路径**比特级**对齐，需要第二个参照件交叉读数。
  4. 两趟改造后 `[-14.3,15.6]` 钳与 `mMaxContentBoost` 建议值的落点（jpegr.cpp:969-985）——属 F9/量程议题延伸，超出本 memo 范围，未裁。
- 纪律自证：全程未改任何源文件、未做 git 写操作、未跑计时；唯一新文件 = 本文档（`docs/review-2026-10-11/gainmap-reference-memo.md`）。
