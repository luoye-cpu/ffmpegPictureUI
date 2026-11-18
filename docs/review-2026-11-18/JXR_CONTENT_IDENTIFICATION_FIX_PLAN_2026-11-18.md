# JXR 真实内容识别完整修复计划

- 日期：2026-11-18
- 状态：**规划稿，未改产品源码**
- 依据：ITU-T T.832 (06/2019) Annex A/C 原文；`JxrDecApp` 本机 usage；仓库现有 JXR 旁路、高位解码、色彩裁决与 cjxl/cjpegli 管道；合成 HDR JXR 复现读数
- 相关旧账：`docs/review-2026-11-17/COLOR_PIPELINE_LUT_ICC_IMPLEMENTATION_PLAN_2026-11-17.md`、`docs/review-2026-11-17/ICC_CICP_WARN_ONLY_2026-11-18.md`、`docs/review-2026-11-16/CHANGES_48H_FIX_RESULT_2026-11-16.md`

---

## 0. 结论先行

截图里那条链路是 **`.jxr` 输入 → `.jxl` 输出**，不是“JXR 输出”。画面过曝的直接原因不是 cjxl，也不是编码器，而是：

1. **JXR 容器里的真实色彩语义没有被识别。**
   现有代码只靠 `ffprobe pix_fmt` 和 `JxrDecApp` 写出的 TIFF 标签判断“半浮点 / 整数 / 3 通道 / 4 通道”，**完全不读 JXR 的 `PIXEL_FORMAT`、`COLOR_SPACE`、`PTM_COLOR_INFO`、`0x8773 ICC`**。因此：
   - 线性 scRGB 浮点被当成“线性”是对的，但没有任何 ICC/PTM 时也**没有把 1.0 的 SDR 白点语义、峰值、HDR 与否说清楚**；
   - 16-bit 整数被一律当成“已编码值、只搬深度”（`int-copy`），但 T.832 的整数 JXR 可以是 sRGB、BT.709、gamma 2.2/2.8、PQ、HLG；**只有 sRGB 档可以 int-copy**；
   - 3 通道半浮点/单精度没有对应直通臂，实际落进 8-bit 旧臂，输出 `rgb24`，高光被截断后又被放大回 sRGB 观感。

2. **JXR 旁路没有把“源”交给色彩裁决点。**
   `ProcessJxrInputAsync` 把 `item.InputPath` 临时换成 PAM，随后 `PipeFfmpegToCjxlAsync` 对 PAM 重新 `DecideOutputColor`；原始 JXR 的 ICC、峰值、传递函数、HDR 状态全部丢失。日志里的“裁决点未参与”就是这个缺口。

3. **线性 HDR 没有进入统一的色调映射。**
   旁路只认 `reinhard`，而且只支持 4 通道交错；`mobius`/`hable` 被当成“PQ/HLG 域算子”直接忽略；GUI 的 `auto` 被提交为 `none + Explicit=false`，旁路看到 `none` 就只做 `FloatToSrgb16Scalar` 的静默钳位。于是“默认 auto”与“显式 mobius”读数完全相同，高光死白。

**修复方向：先做一层“JXR 真实内容识别”，再把识别结果作为唯一的 `JxrSourceContext` 贯穿解码选臂、色彩裁决、色调映射、出口元数据与 GUI 警告。** 识别层不依赖 exiftool，不把 ICC/CICP 解析失败变成拒绝；识别失败只降级 + 条目标黄。

---

## 1. 目标与非目标

### 1.1 目标

- 对 `.jxr` / `.wdp` 输入，能从**容器本身**识别：
  - 文件头、IFD 链、主图/预览/页序列；
  - `PIXEL_FORMAT` 的通道数、位深、数值类型（UINT/SINT/Float/RGBE）、色彩族（RGB/Gray/YUV/CMYK/NCOMPONENT）、alpha、是否预乘；
  - `COLOR_SPACE`、`PTM_COLOR_INFO`（primaries/transfer/matrix/range）、`0x8773 ICC`；
  - 宽高、alpha 平面、空间变换（旋转/翻转）、profile/level；
  - 解码后的真实样本统计：线性域峰值、越白样本数、alpha 区间、HDR/SDR 判定、峰值 nits。
- 把识别结果统一成 `JxrSourceContext`，供：
  - 直通臂选择（不再用 ffprobe `pix_fmt` 当唯一真值）；
  - `ColorIntentFactory` / `DecideOutputColor` 的源描述；
  - JXR→JXL / JXR→JPEG(JPEGLI) / JXR→AVIF / JXR→PNG/TIFF/WebP / JXR→JXR 的出口；
  - GUI 媒体信息、队列警告、预览 HDR 标记。
- 所有颜色语义不确定的情况：**只警告、不拒绝、不零产物**；产物按最佳可用描述继续，日志点名“可能是猜的”。
- JXR 输出侧能写出真实可读的 `ICC` / `PTM_COLOR_INFO` / `COLOR_SPACE`，不再只写 `EXIF:ColorSpace=Uncalibrated` + `ImageDescription` 冒充 HDR。

### 1.2 非目标

- 不重写 `JxrDecApp` / `JxrEncApp`；仍以它们为解码/编码腿。
- 不把 CMYK / NCOMPONENT / YUV 全部做成第一等色彩管线；识别、点名、按可用路径解码即可。
- 不改“显式输出矩阵跨族冲突 = 直接拒绝（A）”的既有拍板；本计划只处理**源侧**识别。
- 不在本批实现 LUT ICC 的完整矩阵化；该能力依赖 `COLOR_PIPELINE_LUT_ICC_IMPLEMENTATION_PLAN_2026-11-17.md`，本计划只定义接入点与失败回退。
- 不跑全量门禁；按 AGENTS §3 只跑定向链路。

---

## 2. 现场证据与现有缺陷

### 2.1 复现读数（已在本机取得）

夹具：
- `tests/output/_tmp/jxr-hdr-repro/hdr_rgba16.jxr`：4×16-bit half-float，`ffprobe pix_fmt=rgbaf16le`，`JxrEncApp -c 25`；
- `tests/output/_tmp/jxr-hdr-repro/hdr_rgb16.jxr`：3×16-bit half-float，`ffprobe pix_fmt=rgbf16le`，`JxrEncApp -c 12`。

命令（`TMP/TEMP/TMPDIR` 已钉到 `tests/output/_tmp/jxr-hdr-repro/tmp`）：

```powershell
FfmpegGui.exe --headless -i hdr_rgba16.jxr -o out-auto -f jxl -e Cjxl --color-space sRGB
FfmpegGui.exe --headless -i hdr_rgba16.jxr -o out-mobius -f jxl -e Cjxl --color-space sRGB --color-tone-map mobius
FfmpegGui.exe --headless -i hdr_rgba16.jxr -o out-reinhard -f jxl -e Cjxl --color-space sRGB --color-tone-map reinhard
```

`ffmpeg signalstats` 16-bit 读数：

| 路径 | YAVG | YMAX | SATAVG | SATMAX |
|---|---:|---:|---:|---:|
| `auto` | 53608.5 | 60377 | 5631.49 | 29248 |
| 显式 `mobius` | 53608.5 | 60377 | 5631.49 | 29248 |
| 显式 `reinhard` | 45551.2 | 55855 | 9561.23 | 26878 |

`auto == 显式 mobius` 证明 Mobius 在这条通路上**被静默忽略**；`reinhard` 才把高光压下来并恢复饱和度。3 通道 `hdr_rgb16.jxr` 显式 `reinhard` 最终输出 `rgb24`，`YAVG=208.646 / YMAX=235`（8-bit 标度），说明 3 通道浮点档没有走高位深直通臂，落进了 8-bit 旧路。

### 2.2 现有代码缺口（按职责归类）

| 缺口 | 现有位置 | 后果 |
|---|---|---|
| 不解析 JXR 容器色彩标签 | `QueueProcessor.ProcessJxrInputAsync` / `TryJxrHighPrecisionAsync`；`JxrHighPrecisionDecode.JxrSampleKind` | 只按 ffprobe/TIFF 猜“浮点/整数”，ICC/PTM/COLOR_SPACE 全丢 |
| 选臂只认 4 档 | `JxrHighPrecisionDecode.ChoosePassthroughArm`（`af16`/`af32`/`rgba64`/`rgb48`） | 3 通道半浮点、3 通道单精度、fixed point 没有直通臂 |
| 浮点一律 scRGB | `JxrSampleKind.HalfFloat16/Float32` 注释与 `EncodeToPam16` | 有 ICC/PTM 时仍按 scRGB 编码，色域/传递函数错 |
| 整数一律 int-copy | `EncodeToPam16` 的 `encodedInt` | 非 sRGB 整数（PQ/HLG/BT.709）被当成 sRGB 码值，亮度和色域双错 |
| 只认 `reinhard` | `TryJxrPassthroughArmAsync` | `mobius`/`hable` 静默不生效；`auto` 落成 `none` |
| 只支持 4 通道映射 | `JxrHighPrecisionDecode.CanToneMap` | 3 通道浮点无法色调映射，退回钳位 |
| 源决策不传递 | `ProcessJxrInputAsync` 临时改 `item.InputPath`；`PipeFfmpegToCjxlAsync` 再 probe PAM | 原始 JXR 的 ICC/峰值/传递函数全部丢失 |
| 不区分预乘 alpha | `TryReadTiff` 只认 `ExtraSamples`，不读 `PIXEL_FORMAT` 的 `PRGBA/PBGRA` | 预乘 RGB 被当作直通 RGB 做 tone map/色域转换，颜色和 alpha 边缘错 |
| 不处理空间变换 | `JxrDecApp` 默认 `-O 0` | `SPATIAL_XFRM_PRIMARY` 非 0 的文件方向错 |
| JXR 输出只写 EXIF | `WriteJxrHdrMetadataAsync` | 只写 `EXIF:ColorSpace=Uncalibrated` + `ImageDescription`，不是 JXR 的 ICC/PTM；查看器仍按 sRGB 解读 |
| 媒体信息 HDR 判定错位 | `MediaInfo` / 预览 badge 使用不同来源 | 截图出现“右侧 SDR、预览 JXL HDR”的矛盾 |

### 2.3 T.832 的关键规范事实（实现必须遵守）

**文件头**（Annex A.5）：
- 2 字节 `0x4949`（`II`）；
- 1 字节 `0xBC`；
- 1 字节 `FILE_VERSION_ID = 1`；
- 4 字节 little-endian `FIRST_IFD_OFFSET`。

**IFD**（Annex A.6/A.7）：
- `NUM_ENTRIES` u16；
- 每项 12 字节：`FIELD_TAG` u16、`ELEMENT_TYPE` u16、`NUM_ELEMENTS` u32、`VALUES_OR_OFFSET` u32；
- 若 `NUM_ELEMENTS * SizeOfElement <= 4`，值内联；否则 `VALUES_OR_OFFSET` 是文件内偏移，且必须是偶数；
- `ZERO_OR_NEXT_IFD_OFFSET` u32 指向下一个 IFD，0 = 结束；
- 字段标签必须递增（非首项）。

**关键标签**：
- `0xA001 COLOR_SPACE`：USHORT ×1，可选；
- `0xBC01 PIXEL_FORMAT`：BYTE ×16，必需；
- `0xBC04 IMAGE_TYPE`：ULONG ×1，可选；bit0 = preview，bit1 = page；
- `0xBC05 PTM_COLOR_INFO`：BYTE ×4，可选；
- `0x8773 ICC profile`：UNDEFINED，变量，可选；
- `0xBC80/0xBC81` width/height；
- `0xBCC0/0xBCC1` image offset/byte count；
- `0xBCC2/0xBCC3` alpha offset/byte count；
- `0xBC02 SPATIAL_XFRM_PRIMARY`。

**COLOR_SPACE 语义（A.7.17）**：
- 值为 `1` 且 `PIXEL_FORMAT` 的 `Num = UINT` ⇒ 按 IEC 61966-2-1 sRGB 解释；
- 值为 `0xFFFF`（或任何非 1 的保留值按 0xFFFF 处理）⇒ sRGB 可能不是首选解释；
- **fixed point / floating point 不得令 `COLOR_SPACE = 1`**。

**PTM_COLOR_INFO（A.7.21 / Table A.7）**：
```
COLOR_PRIMARIES u8
TRANSFER_CHARACTERISTICS u8
MATRIX_COEFFICIENTS u8
RESERVED_K u7
FULL_RANGE_FLAG u1
```
- 不得在 `OUTPUT_CLR_FMT = CMYK/CMYKDIRECT/NCOMPONENT/RGBE` 时出现；
- 不得在 `OUTPUT_BIT_DEPTH` 不属于 `BD8/BD10/BD16/BD16S` 时出现；
- `COLOR_PRIMARIES`：1=BT.709/sRGB、2=未指定、4=BT.470 M/NTSC、5=BT.601-625/BT.470 B,G、6=BT.601-525/SMPTE 170M、7=SMPTE 240M、9=BT.2020；其余保留；
- `TRANSFER_CHARACTERISTICS`：1=BT.709、2=未指定、4=gamma 2.2、5=gamma 2.8、6=BT.601、7=SMPTE 240M、8=linear、11=xvYCC、12=BT.1361 extended、13=sRGB、16=PQ、18=HLG；其余保留；
- `MATRIX_COEFFICIENTS`：0=RGB/GBR（无矩阵）、1=BT.709、2=未指定、4=FCC、5=BT.601-625、6=BT.601-525、7=SMPTE 240M、8=YCgCo、9=BT.2020 non-constant、10=BT.2020 constant、14=ICTCP；RGB 输出时不得为 1/4/5/6/7/8/9/10/14；
- `FULL_RANGE_FLAG`：BD8/10/16 的 `baseOffset = 0`，有限范围按 `valueWhite/256*(219*E'+16)`；BD16S 的 `baseOffset=0`，但 A.7.21.5 对 fixed point 的完整解释不充分，实现按 s2.13 的 `1.0=8192` 解释并明确记录“range 未按整数公式施加”。

**ICC 优先（Annex C）**：
- ICC 是“无歧义解释”的首选；
- 无 ICC、`COLOR_SPACE != 1`、无 PTM 时，**unsigned integer RGB 按 sRGB**；
- **fixed point / floating point RGB 按 scRGB**（BT.709 原色 + 线性 CCTF，1.0 = perfect diffuse white，0.0 = zero photons），但**如果嵌入 ICC，ICC 优先**；
- float/fixed 的 ICC 应使用线性 CCTF（relative colorimetric）；如果 ICC 曲线不是线性，属于非典型/可能不兼容，只警告，不拒绝。

**PIXEL_FORMAT 与 JxrDecApp `-c` 的对应（本机 usage 实测）**：

| PIXEL_FORMAT | 通道/类型 | `JxrDecApp -c` |
|---|---|---:|
| `24bppRGB` | 3×8 UINT | 9 |
| `24bppBGR` | 3×8 UINT | 0 |
| `32bppBGR` | 3×8 UINT（BGR） | 34 |
| `48bppRGB` | 3×16 UINT | 10 |
| `48bppRGBFixedPoint` | 3×16 SINT s2.13 | 11 |
| `48bppRGBHalf` | 3×16 half | 12 |
| `96bppRGBFixedPoint` | 3×32 SINT s7.24 | 14 |
| `128bppRGBFloat` | 3×32 float | 15 |
| `32bppRGBA` | 4×8 UINT | 22 |
| `64bppRGBA` | 4×16 UINT | 23 |
| `64bppRGBAFixedPoint` | 4×16 SINT | 24 |
| `64bppRGBAHalf` | 4×16 half | 25 |
| `128bppRGBAFixedPoint` | 4×32 SINT | 27 |
| `128bppRGBAFloat` | 4×32 float | 28 |
| `16bppGray` | 1×16 UINT | 3 |
| `16bppGrayFixedPoint` | 1×16 SINT | 4 |
| `16bppGrayHalf` | 1×16 half | 5 |
| `32bppGrayFixedPoint` | 1×32 SINT | 7 |
| `32bppGrayFloat` | 1×32 float | 8 |
| `32bppRGBE` | 3×8 RGBE | 16 |
| `32bppCMYK` / `64bppCMYK` | 4ch CMYK | 17 / 18 |
| `32bppPBGRA` | 4×8 预乘 | 22 + unpremultiply |
| `64bppPRGBA` | 4×16 预乘 | 23 + unpremultiply |
| `128bppPRGBAFloat` | 4×32 预乘 | 28 + unpremultiply |

---

## 3. 识别层设计

### 3.1 新类型：`JxrContentProbe`（纯托管，不启进程）

建议文件：`src/FfmpegGui/Services/JxrContentProbe.cs`

职责：
1. 读文件头 `II 0xBC 01` + `FIRST_IFD_OFFSET`；
2. 遍历 IFD 链（上限 4096 项 / 4096 个 IFD，防恶意文件）；
3. 解析 `PIXEL_FORMAT`、`COLOR_SPACE`、`PTM_COLOR_INFO`、`0x8773 ICC`、宽高、alpha offset、`IMAGE_TYPE`、`SPATIAL_XFRM_PRIMARY`、`PROFILE_LEVEL_CONTAINER`；
4. 选择**主图 IFD**：跳过 `IMAGE_TYPE & 1 == 1` 的 preview；若有 page 序列，保留页列表，默认取第一张非 preview；
5. 所有偏移/长度做文件边界校验；失败返回 `JxrProbeResult.Failed(reason)`，不抛出到 UI；
6. 不读像素；不做色彩映射。

建议公开 API：

```csharp
public enum JxrNumericKind { UInt, SInt16Fixed, SInt32Fixed, HalfFloat, Float32, Rgbe, Unknown }

public enum JxrColorFamily { Rgb, Gray, Yuv, Cmyk, NComponent, Unknown }

public enum JxrAlphaMode { None, Interleaved, SeparatePlane, PremultipliedInterleaved, PremultipliedSeparate }

public sealed class JxrPixelFormatInfo
{
    public string Mnemonic;          // "64bppRGBAHalf"
    public byte[] Guid;              // 16 bytes
    public int Channels;             // NC
    public int BitsPerSample;        // 8/10/16/32
    public JxrNumericKind Numeric;
    public JxrColorFamily Family;
    public bool HasAlpha;
    public bool Premultiplied;
    public bool SeparateAlphaPlane;  // ALPHA_OFFSET present
}

public sealed class JxrContentInfo
{
    public bool Ok;
    public string? Error;
    public JxrPixelFormatInfo PixelFormat;
    public int Width, Height;
    public int ImageType;             // raw
    public bool IsPreview;
    public bool IsPage;
    public int PageIndex;
    public int PageCount;
    public int Orientation;           // SPATIAL_XFRM_PRIMARY 0..7
    public int ColorSpaceTag;         // 0/1/0xFFFF/...
    public JxrPtmColorInfo? Ptm;      // null = absent
    public byte[]? IccBytes;          // 0x8773
    public string? IccDescriptionHint;
    public bool HasAlphaPlaneOffset;
    public long ImageOffset, ImageByteCount, AlphaOffset, AlphaByteCount;
    public int ProfileIdc, Level;
    // 由识别器填充（不是容器原始字段）
    public ColorSpaceDescriptor? Descriptor;
    public JxrColorSource ColorSource;       // Icc / Ptm / ColorSpaceSrgb / ScRgbDefault / UnsignedSrgbDefault / Unknown
    public SourceIccStatus IccStatus;
    public bool IccReadable;
    public double NitsOfLinearOne;           // 1.0 对应 nits
    public double PeakNits;                  // 由帧统计回填
    public bool IsHdr;
    public string? PeakSource;
    public List<string> Warnings;
}
```

`JxrContentInfo` 应可序列化为 JSON，供 `ServiceProbe` 输出与门禁断言。

### 3.2 新类型：`JxrSourceContext`（贯穿链路）

建议文件：`src/FfmpegGui/Services/JxrSourceContext.cs`

```csharp
public sealed class JxrSourceContext
{
    public string InputPath;
    public JxrContentInfo Content;
    public ColorSpaceDescriptor Source;      // 识别层产物（可能带 ICC 临时路径）
    public SourceIccStatus SourceIccStatus;
    public double SourcePeakNits;            // 解码后实测；0=未测
    public double SourceSdrWhiteNits;        // 源 1.0 的 nits 语义
    public bool SourceIsHdr;
    public bool HasAlpha;
    public bool AlphaIsPremultiplied;
    public JxrNumericKind Numeric;
    public int Channels;
    public int BitDepth;
    public ColorToneMapIntent ToneMap;       // Auto / None / Mobius / Hable / Reinhard / Bt2446A
    public string? SourceIccTempPath;        // 0x8773 解出的临时 ICC；finally 删除
    public List<string> Warnings;
}
```

所有 `ProcessJxr*` 分支只读这个上下文，不再各自 `ProbeInputColorMetadata`。

### 3.3 色彩语义优先级（唯一真值）

按 T.832 Annex C 与项目既有“ICC 度量 > CICP 标签 > 无描述”的原则，JXR 源采用：

1. **`0x8773 ICC` 可读且能构造描述符**：
   - 用 ICC 的 `rXYZ/gXYZ/bXYZ` + `curv`/`para` 构造 `ColorSpaceDescriptor`（复用 `ColorSpaceIdentify.FromIccFile`）；
   - `ColorSource = Icc`，`Confidence = IccMetric`；
   - 若 ICC 是 LUT-only（A2B0/B2A0）且当前 `ColorSpaceIdentify` 返回 `null`：**不拒绝**；`IccStatus=Present/Unreadable`，保留 ICC 字节，走“携带 ICC + 不映射像素”或“目标显式时按最近具名空间 + 警告”；
   - 若 ICC 曲线不是线性而像素是 float/fixed：警告 `JXR-COLOR-04`，仍以 ICC 为准（显式 profile 优先于格式惯例）。
2. **`PTM_COLOR_INFO` 存在且字段完整**：
   - 由 primaries/transfer/matrix/range 构造 `ColorSpaceDescriptor`；
   - `ColorSource = Ptm`，`Confidence = CicpTag`；
   - 若 primaries/transfer 是 2（未指定）或保留值：只保留已知的一半，另一半按格式惯例补，降为 `CodecDefault` 并警告；
   - 若 `PIXEL_FORMAT` 是 float/fixed 而 PTM 存在：按规范 PTM 本不应存在，警告 `JXR-COLOR-05`，**ICC 优先；无 ICC 时用 scRGB 惯例，忽略 PTM**（不要把非法的 PTM 当成真值）。
3. **`COLOR_SPACE`**：
   - `COLOR_SPACE=1` 且 UINT：sRGB（BT.709 原色 + IEC 61966-2-1 曲线 + identity matrix）；
   - `COLOR_SPACE=0xFFFF` 或缺失且无 ICC/PTM：UINT 按 Annex C 的 sRGB 惯例；float/fixed 按 scRGB 线性惯例；
   - `COLOR_SPACE=1` 且 float/fixed：规范禁止，警告 `JXR-COLOR-06`，按 scRGB 惯例处理。
4. **数值族惯例**：
   - UINT RGB / Gray：sRGB；
   - Float/Fixed RGB / Gray：scRGB 线性（BT.709 原色，gamma=1.0）；
   - RGBE：按 float 处理（scRGB 线性）；
   - CMYK：无 ICC 时按 SWOP CMYK 惯例，但当前管线不把 CMYK 当 RGB；只识别、点名、走可解码路径；
   - NCOMPONENT n=3：按 sRGB；n>3：前四通道按 CMYK，其余丢弃（Annex C 建议），并警告。
5. **矛盾处理**：
   - ICC ↔ PTM：ICC 胜，记录两者值；
   - PTM ↔ `COLOR_SPACE`：PTM 胜（更具体）；
   - 容器 ↔ 实际解码形状：实际解码形状用于“怎么读像素”，容器标签用于“这些像素是什么”；若冲突，警告 `JXR-DECODE-01`，并降低 `Confidence` 到 `CodecDefault`，不拒绝。

### 3.4 数值与范围

- **UINT8/10/16**：按 PTM 的 transfer 解码到线性；只有 `TRANSFER_CHARACTERISTICS=13`（sRGB）且 `MATRIX=0` 时允许“码值直通”。
- **SINT16 (s2.13)**：`value / 8192.0` 得到归一化 E'；`SINT32 (s7.24)`：`value / 16777216.0`；
- **Half/Float32**：样本即线性（scRGB 或 ICC 曲线定义）；若 ICC 曲线不是线性，按 ICC 曲线解码；
- **有限范围**：
  - `BD8/10/16` + `FULL_RANGE_FLAG=false`：`E' = (code - 16*scale) / (219*scale)`，`scale = valueWhite/256`；
  - `FULL_RANGE_FLAG=true`：`E' = code / (valueWhite-1)`；
  - `BD16S`：按 s2.13 的 1.0=8192 解释；A.7.21.5 对 fixed point 的整数映射不完整，日志必须写明“fixed point range 未按整数公式施加”。
- **矩阵**：`MATRIX=0` 才允许 RGB 直通；`MATRIX=1/4/5/6/7/9/10/14` 表示 YUV/ICTCP 系数，若 `OUTPUT_CLR_FMT=RGB` 则文件非conforming，警告并按 identity 处理（不猜 YUV 转换）。
- **YUV JXR**：按 PTM 的 matrix 做 YUV→RGB（或交给 `JxrDecApp` 输出 RGB 后按 matrix 解释）；没有 PTM 时按 BT.709 假定并警告。
- **CMYK JXR**：识别 + 点名；不进入普通 RGB 色彩引擎，走 `JxrDecApp` 的 CMYK→RGB 路径或明确不支持。

### 3.5 Alpha、预乘、方向、页

- `PIXEL_FORMAT` 的 alpha 位是**是否存在 alpha 的唯一容器真值**；
- `ALPHA_OFFSET` 存在 ⇒ 独立 alpha 平面；否则为交错 alpha；
- `PBGRA/PRGBA/PRGBAFloat` ⇒ 预乘；色调映射/色域转换前必须 `unpremultiply` 到直通 alpha，alpha 保持不变；输出若要求预乘再按出口策略重新预乘；
- `4 通道但 ExtraSamples` 与实际 alpha 不一致 ⇒ 警告 `JXR-ALPHA-01`；若 alpha 非常量且无法恢复，才允许显式失败（不能静默拍平）；
- `SPATIAL_XFRM_PRIMARY != 0` ⇒ 给 `JxrDecApp` 传 `-O`，并在识别日志记录方向；解码后以实际 TIFF 宽高为准；
- `IMAGE_TYPE`：
  - bit0 preview：跳过，取对应主图；
  - bit1 page：记录页数；目标不是动画时只取第一页并警告“仅首帧”；目标是动画时保留页序列（沿用 `verify-jxr-anim-input` 的既有路径）。

### 3.6 HDR / SDR 与峰值

- **源 1.0 的 nits**：
  - UINT sRGB / BT.709 / gamma：`TransferCurve.SdrWhiteNits`（203）；
  - PQ：10000；
  - HLG：1000；
  - scRGB：203（若 ICC 有 `lumi` 且可读，用 `lumi` 的 Y 值）；
  - ICC 曲线为 PQ/HLG：按曲线给 10000/1000。
- **线性化**：所有通道先按源曲线 EOTF 到线性；UINT/固定点先做范围归一；float/half 直接读。
- **Peak**：全帧 RGB 最大线性值 × `NitsOfLinearOne`；`OverWhite` 为线性值 > 1.0 的 RGB 样本数；
- **IsHdr**：
  - `Curve.Kind` 是 PQ/HLG ⇒ true；
  - `Curve.Kind` 是 Linear 且 `Peak > 1.0 + ε` ⇒ true（scRGB HDR）；
  - `TRANSFER=11/12`（xvYCC/BT.1361 extended）且 `Peak > 1.0` ⇒ true；
  - 其余 ⇒ false。
- **峰值来源**：`PeakSource` 必须是 `"frame-scan"`（实测）或 `"icc-lumi"` / `"metadata"`；禁止把“名义 1000/10000”写成实测。
- **目标 SDR 白点**：`ColorSdrWhiteNits`（用户选项）默认 203，与源 `NitsOfLinearOne` 分开记录；不要把源 PQ 的 10000 当目标白点。

### 3.7 三源交叉校验

每次识别输出三行读数（ASCII 键值，便于门禁）：

```
[jxr-content] header=ok pixel_format=64bppRGBAHalf guid=... channels=4 alpha=separate premul=0 bpc=16F numeric=half family=rgb
[jxr-content] color_source=icc icc=present|readable primaries=bt709 transfer=linear matrix=gbr range=full nits1=203
[jxr-content] image=2560x1600 type=0 orient=0 pages=1 peak=4.2312 peak_nits=859.0 hdr=1 warnings=0
```

交叉校验：
1. 容器 `PIXEL_FORMAT` vs `JxrDecApp` 输出 TIFF 的 `SamplesPerPixel/BitsPerSample/SampleFormat`；
2. 容器 vs `ffprobe pix_fmt`（只作对照，不作真值）；
3. 容器 ICC/PTM vs 描述符；
4. `COLOR_SPACE=1` vs float/fixed；
5. alpha 声明 vs 实际 4 通道/alpha 平面。

不一致：`JXR-DECODE-01` / `JXR-COLOR-05` 警告；不改变“容器决定色彩语义、实际解码决定像素形状”的分工，除非形状冲突导致无法解释（此时降置信度 + 回退）。

---

## 4. 与现有色彩管线的接入

### 4.1 `ColorMetadata` 扩展

在 `FfmpegCommandBuilder.ColorMetadata` 增加：

```csharp
public JxrContentInfo? jxrContent;
public ColorSpaceDescriptor? jxrDescriptor;
public bool jxrDescriptorAuthoritative;
```

`ProbeInputColorMetadataCore` 对 `.jxr` / `.wdp`：

1. 先跑 `JxrContentProbe`；
2. 用识别结果填 `bitDepth/colorPrimaries/colorTrc/colorSpace/colorRange/hasAlpha/isRgb/pixFmt/iccStatus/iccDescriptor/peakNits/peakSource/sourceGamutName/primariesApproximated/width/height`；
3. `ffprobe` 只做交叉校验；若探针失败，回退现有 ffprobe/exiftool，并加 `JXR-PROBE-01` 警告；
4. 不因 ICC/CICP 不可读而 `exifReadFailed=true`；JXR 的 ICC 由容器直接读，不依赖 exiftool。

`ProbeInputIccDescriptor` 对 JXR 优先返回 `jxrDescriptor?.Clone()`；如果它是 ICC 派生，`SourceIccPath` 指向临时 ICC 文件（消费者只读，不删除）。

### 4.2 `ColorIntentFactory`

- 源描述优先级改为：`--icc-file` > JXR 自有 ICC/PTM > RAW/UltraHDR 中间件 > 容器 CICP > 惯例；
- 对 JXR：
  - `SourceIccStatus` 用容器三态（`Present/Unreadable/Absent`），不用 exiftool 的 `exifReadFailed`；
  - `intentWarnings` 加入 `JXR-COLOR-*`，不进入 `reason`（不拒绝）；
  - `FixHdrNits` 对 JXR 描述符已显式设好，不再二次覆盖；
  - `srcLinearHdr` 的例外要按来源分型：GainMap PFM 的线性 HDR 可维持“纯 OETF”旧口径；**JXR scRGB 抓帧必须进入线性 tone-map 分支**，不能共用同一条“纯 OETF”例外。
- 目标语义与现有 `EffectiveOutputColorTokens` 同源；不改显式输出矩阵跨族拒绝。

### 4.3 `DecideOutputColor` / `BuildArguments`

- `BuildArguments(options, inputPath, outputPath, probeInputPath)` 已支持“输入文件与探测文件分离”；JXR 所有 ffmpeg 直出分支都要传 `probeInputPath = 原始 JXR`；
- `PipeFfmpegToCjxlAsync` 增加 `sourceProbePath` 参数：
  - `inMeta = ProbeInputColorMetadata(sourceProbePath)`；
  - `dec = DecideOutputColor(options, sourceProbePath)`；
  - `ffArgs` 的 `-i` 仍是 PAM/中间件；
  - `BuildCjxlArguments(..., hdrMeta = 原始 JXR 的 meta)`；`intensity_target` 用原始 JXR 的 `PeakNits`；
- `PipeFfmpegToCjpegliAsync` 同样增加 `sourceProbePath`、`pixelDecision`；
- 不要再用“临时替换 `item.InputPath`”作为传递源语义的手段；把原始路径显式传参，避免 `item.InputPath` 被其它后置逻辑误读。

### 4.4 色调映射

统一使用 `ColorMapping.ToneMap`（`ToneMap.ApplyRgb` / `ToneMap.Apply`），不要为 JXR 再写一套曲线：

- 输入：解码后的线性 RGB（1.0 = 源 SDR 白点，按 `NitsOfLinearOne` 归一）；
- `ToneMapParams`：
  - `Mode` 由 `JxrSourceContext.ToneMap` 决定；
  - `SdrWhiteNits = 目标 SDR 白点`（默认 203）；
  - `PeakNits = 实测峰值`（`PeakSource=frame-scan`）；
  - `Knee = 0.3`（Mobius 默认）；
- `auto` 语义：
  - 源 HDR + 目标 SDR ⇒ `Mobius`；
  - 源 HDR + 目标 HDR ⇒ `None`；
  - 源 SDR + 目标任意 ⇒ `None`；
- 显式：
  - `none` ⇒ 钳位，但必须警告 `JXR-TONEMAP-01`，日志给 `peak/overWhite`；
  - `mobius` / `hable` / `reinhard` ⇒ 在线性域用 `ToneMap` 执行；
  - `bt2446a` ⇒ 该算子是编码域亮度+色差校正；JXR 线性 scRGB 没有目标 coded-domain 时**不适用**，警告 `JXR-TONEMAP-02`，不得静默当 none；
- 3 通道 / 4 通道：`ToneMap.ApplyRgb` 按 `channels` 步长处理；alpha 保留；预乘先 unpremultiply；
- 色调映射后：目标色域矩阵转换 → 目标曲线编码 → 输出。

**整数 JXR 的例外**：`TRANSFER=13`（sRGB）+ `MATRIX=0` 才允许 `int-copy`；其余整数档必须先按 PTM 曲线 EOTF 到线性，再走 `ToneMap`/目标编码。不能把所有 `UInt16` 都标成 `int-copy`。

---

## 5. 出口矩阵

### 5.1 源语义 × 目标

| 源 | 目标 | 处理 |
|---|---|---|
| SDR sRGB | SDR | 直通/色域转换；无 tone map |
| SDR sRGB | HDR PQ/HLG | 线性化 → 目标曲线编码；`intensity_target` 按目标/选项 |
| scRGB linear, peak ≤1.0 | SDR | 纯 OETF 编码；无 tone map |
| scRGB linear, peak >1.0 | SDR | `auto`/`mobius` 线性域 tone map → 目标曲线 |
| scRGB linear, peak >1.0 | HDR PQ/HLG | 不 tone map；线性值按 `NitsOfLinearOne` 换算到目标 PQ/HLG |
| PQ/HLG integer/float | SDR | EOTF 到线性 → Mobius/Hable/Reinhard → 目标曲线 |
| PQ/HLG | HDR 同族 | 直通/色域转换；保留峰值与 `intensity_target` |
| ICC P3/Adobe/ProPhoto | 任意 | ICC 度量矩阵 → 目标；LUT-only 不能矩阵化时携带 ICC + 警告 |
| YUV/CMYK | 任意 | 先由 JxrDecApp/PTM 转 RGB；不能转时明确不支持，不静默当 RGB |

### 5.2 容器

| 目标 | 色彩/元数据要点 |
|---|---|
| `jxl` | SDR ⇒ `-x color_space=sRGB`；HDR ⇒ `color_space` + `--intensity_target`；ICC 路径用 `icc_pathname`；动画用既有 jhgm/帧路径 |
| `jpg/jpeg/jpegli` | 只 SDR；GainMap 开关决定 HDR 承载；底图 tone map；不做“传统 HDR / 增益图 HDR”二选一；ICC v4.4 策略按既有 |
| `avif` | 静态 HDR 可写 CICP/ICC；alpha 走 avifenc 辅助流；不要强制 SDR；ICC/CICP 双写按既有 `ExitWritesIccFor` 口径 |
| `png` / `apng` | 16-bit；cICP/iCCP；HDR 目标按能力；SDR 目标 tone map |
| `tiff` | 16-bit；ICC；HDR 目标按能力；不要只写像素不写描述 |
| `webp` | SDR 硬钳；HDR 源必须 tone map；ICC 按既有 |
| `jxr` | `JxrEncApp` 编码；输出必须写 `0x8773 ICC` / `0xBC05 PTM` / `0xA001 COLOR_SPACE`；不能只写 EXIF `ImageDescription` |
| `gif` / `ppm` / `pam` | 无色彩通路；钉 sRGB + 警告；不假装 HDR |
| `jpeg` + GainMap | JXR 源 HDR 时用实测峰值生成增益图；底图 tone map 到 SDR；GainMap 关掉时只输出正确 SDR 底图；不要把 JXR 当 SDR 源 |

### 5.3 JXR 输出元数据（新增）

`JxrEncApp` 不能写 ICC；现有 `WriteJxrHdrMetadataAsync` 只写 EXIF，是假 HDR 标注。计划新增 `JxrMetadataWriter`：

- 输入：输出 `.jxr` + `JxrContentInfo`/目标描述符；
- 操作：
  1. 若目标 ICC 可生成：插入/替换 `0x8773` UNDEFINED 条目，ICC 字节追加到文件尾，更新 `NUM_ELEMENTS` 与 `VALUES_OR_OFFSET`；
  2. 若目标 primaries/transfer/matrix 可命名：插入/替换 `0xBC05 PTM_COLOR_INFO`（BYTE×4，内联）；
  3. 若目标为 UINT sRGB：写 `0xA001 COLOR_SPACE=1`；否则写 `0xFFFF` 或不写；
  4. 不写与 ICC 冲突的 PTM；ICC 优先；若只能写 CICP/PTM，日志点名“ICC 未写入”；
- 约束：更新 IFD 后保持标签递增、偏移偶数、文件尾对齐；写入前备份/失败回滚；不破坏 `JxrEncApp` 的 codestream；
- 若直接写 IFD 风险过高，先做“只写 ICC 的 append + IFD 重排”最小实现，PTM 作为下一阶段。

---

## 6. 分阶段实施

### P0：识别层（不改执行行为）

**改动**
- 新增 `JxrContentProbe.cs`、`JxrSourceContext.cs`；
- 新增 `JxrContentInfo` JSON 序列化；
- `ServiceProbe` 增加 `jxrcontent` mode（如采用）：输入路径 → 输出 JSON + ASCII 摘要；
- `FfmpegCommandBuilder.ColorMetadata` 增加字段，但 `ProbeInputColorMetadataCore` 对 JXR 先只填日志，暂不接管决策；
- 新增 `tests/scripts/verify-jxr-content-id.ps1`（见 §7）；
- 日志加 `[jxr-content]` 行；不改变输出像素。

**验收**
- 对合成 4 类夹具，字段与 T.832 表一致；
- 对坏 ICC、截断 IFD、float+`COLOR_SPACE=1` 只警告；
- 现有 `verify-jxr-highbitdepth` 读数不变（除新增日志行）。

### P1：选臂与像素形状

**改动**
- `ChoosePassthroughArm` 改为 `ChoosePassthroughArm(JxrContentInfo)`：
  - `64bppRGBAHalf -> 25`、`128bppRGBAFloat -> 28`、`64bppRGBA -> 23`、`48bppRGB -> 10`；
  - 新增 `48bppRGBHalf -> 12`、`128bppRGBFloat -> 15`、`48bppRGBFixedPoint -> 11`、`96bppRGBFixedPoint -> 14`、`64bppRGBAFixedPoint -> 24`、`128bppRGBAFixedPoint -> 27`；
  - `32bppRGBE -> 16`；灰度按表；
  - 预乘 PBGRA/PRGBA 用对应非预乘臂 + 解码后 unpremultiply（或 `JxrDecApp` 直接支持则优先）；
- `TryJxrHighPrecisionAsync` 以容器 `PIXEL_FORMAT` 为真值；`ffprobe` 仅作交叉校验；
- `TryReadTiff` 增加 `SInt16Fixed`/`SInt32Fixed`，并按 `8192/16777216` 归一；
- `JxrHighBitFrame` 增加 `NumericKind`、`IsPremultiplied`、`ColorFamily`；
- 3 通道半浮点/单精度走 `-c 12/15`，不再落 8-bit；
- 失败回退链保留，但只在容器解析失败/臂执行失败时启用。

**验收**
- 合成 3 通道半浮点输出 16-bit PAM（不是 `rgb24`）；
- 4 通道预乘夹具的 alpha 边缘不出现暗边；
- `verify-jxr-highbitdepth` 新增 `-c 12/15` 断言。

### P2：决策传递与色调映射

**改动**
- `ProcessJxrInputAsync` 在解码前建立 `JxrSourceContext`；
- `TryJxrPassthroughArmAsync`：
  - 删掉“只认 reinhard”；
  - 用 `ToneMap` + `JxrSourceContext.ToneMap`；
  - `auto` 读 `!ColorToneMapExplicit`；
  - 3/4 通道通用；整数非 sRGB 先 EOTF；
  - 输出 `[jxr] HP-METRICS` 增加 `tonemap=... peak_nits=... hdr=...`；
- `PipeFfmpegToCjxlAsync` / `PipeFfmpegToCjpegliAsync` 增加 `sourceProbePath`，传原始 JXR 的 `dec`；
- 直接 ffmpeg 分支 `BuildArgsAndNote` 传 `probeInputPath`；
- `ProcessJxrToAvifWithAlphaAsync` 使用 tone-mapped PAM/原始决策；
- `DecideOutputColor` 的 `srcLinearHdr` 例外按来源分型；JXR scRGB 进入线性 tone map。

**验收**
- `auto` 与显式 `mobius` 同值（这是正确行为），但与显式 `none` 明显不同；
- 3 通道 `hdr_rgb16` 的 `auto`/`mobius` 有高光压缩、输出 16-bit；
- 显式 `reinhard` 仍可复现旧读数；
- `none` 必须打 `JXR-TONEMAP-01`，不能静默。

### P3：出口元数据与 GUI 警告

**改动**
- `JxrMetadataWriter`（ICC/PTM/COLOR_SPACE）；
- `WriteJxrHdrMetadataAsync` 不再用 EXIF 描述冒充 HDR；无 ICC 时写 PTM/COLOR_SPACE，或明确“无描述”；
- `RestoreMetadataAsync` 不覆盖 JXR 色彩标签；无 exiftool 时用 ffmpeg 回退，缺失只 info 不 warning；
- `MediaInfo` 的 HDR/SDR 与峰值改用 `JxrContentInfo`；
- 队列 `MarkWarning` 用统一 code；
- GUI 的 `ColorToneMapCombo auto` 在 JXR 路径生效；`ColorMatrixCombo` 的 auto 保持。

**验收**
- JXR 输出可被 `JxrContentProbe` 回读：ICC/PTM/COLOR_SPACE 与目标一致；
- 坏 ICC 仍产出文件，条目标黄；
- `StandardDynamicRange` 不再与预览 badge 矛盾。

### P4：可选扩展

- fixed point 完整解码（如果 P1 只识别未解码）；
- YUV/CMYK 的 JXR 解码路径；
- `SPATIAL_XFRM_PRIMARY` 的 `-O` 实测；
- 多页/动画的页序列元数据；
- ICC `lumi` 读取；
- LUT ICC 矩阵化接入（依赖独立计划）。

---

## 7. 测试与门禁

### 7.1 新门禁 `verify-jxr-content-id.ps1`

建议放在 `tests/scripts/`，平铺；若采用 `ServiceProbe jxrcontent`，断言 JSON 字段；否则用 headless 日志的 `[jxr-content]` 行。

覆盖夹具：

| 夹具 | 目的 | 断言 |
|---|---|---|
| `rgba16_half` | 4ch half，scRGB | `pixel_format=64bppRGBAHalf`、`channels=4`、`numeric=HalfFloat`、`color_source=scrgb-default`、`hdr` 随峰值 |
| `rgb16_half` | 3ch half | `48bppRGBHalf`、`-c 12`、输出 16-bit PAM |
| `rgb32_float` | 3ch float | `128bppRGBFloat`、`-c 15` |
| `rgba32_float` | 4ch float | `128bppRGBAFloat`、`-c 28` |
| `rgb16_uint_ptm_srgb` | 整数 sRGB + PTM | `color_source=ptm`、`transfer=srgb`、允许 `int-copy` |
| `rgb16_uint_ptm_pq` | 整数 PQ | `hdr=1`、`transfer=pq`、**不得** `int-copy` |
| `rgb16_uint_ptm_hlg` | 整数 HLG | `hdr=1`、`transfer=hlg` |
| `rgb16_uint_limited` | 有限范围 | `range=tv`、线性化后峰值不偏移 |
| `rgba16_half_icc_p3` | ICC P3 | `color_source=icc`、`primaries=smpte432`、`icc=present` |
| `bad_icc` | 坏 ICC | `icc=unreadable`、有 `JXR-COLOR-01`、产物非零 |
| `float_color_space_1` | 非conforming | `JXR-COLOR-06`，不拒绝 |
| `ptm_on_float` | 非conforming | `JXR-COLOR-05`，ICC/scRGB 优先 |
| `premultiplied_rgba` | 预乘 | `premul=1`、unpremultiply 后 alpha 边缘正确 |
| `orient6` | 旋转 | `orient=6`、输出宽高交换 |
| `truncated_ifd` | 截断 | 探针失败、回退、警告、不零产物 |
| `preview_page` | 预览+页 | 选择非 preview 主图，页数正确 |

### 7.2 扩展既有门禁

- `verify-jxr-highbitdepth.ps1`：
  - ②b 整数 `-c 23` 仍 int-copy；
  - 新增 3 通道 `-c 12`、`-c 15`；
  - ⑨ 盲试顺序仍保留，但“选臂”以容器为准；
  - `HP-METRICS` 新增 `tonemap=`、`peak_nits=`、`hdr=`、`source=container`；
  - 断言 `auto` 与 `mobius` 同值、与 `none` 不同。
- `verify-jxr-anim-input.ps1`：页序列不回归；页面选择不误取 preview。
- `verify-jxr-backend-choice.ps1`：`-e` 兑现性不变。
- `verify-color-wiring.ps1`：JXR→JXL 的 SDR/HDR 两条链路各加一格；不依赖全量。
- `verify-decision-delivery` 类：JXR 原始源决策必须到达 cjxl/cjpegli，不能再出现“裁决点未参与”。

### 7.3 门禁登记

若新增 `verify-jxr-content-id.ps1`：
- `tests/scripts/_run-step3-gates.ps1` 的 `$scriptList` 加一项；
- `$expectedScriptCount` 104 → 105；
- runner 散文条数同步；
- 若加 `ServiceProbe` mode，`tests/projects/Tests.Shared/TestGroups.cs` 加组，`_probe-test-group-coverage.ps1` 认领；
- `docs/TESTING.md` 的 module registry 若涉及新模块，按 `_module-registry.json` 更新，不手改生成段；
- 只跑定向：`-Scripts verify-jxr-content-id.ps1,verify-jxr-highbitdepth.ps1,...`，不跑 `-FullBaseline`。

### 7.4 负控

- 把 `JxrContentProbe` 的 `PIXEL_FORMAT` 表故意改错一档 ⇒ 门禁必须红；
- 把 `auto` 改回 `none` ⇒ 门禁必须红；
- 把 ICC 解析失败改成拒绝 ⇒ 门禁必须红（违反正向要求）；
- 把 3 通道 `-c 12` 删掉 ⇒ 门禁必须红；
- 把 `COLOR_SPACE=1` 在 float 上当真 ⇒ 门禁必须红。

---

## 8. 验收口径

宣布“JXR 识别修复完成”必须同时满足：

1. **识别**：合成夹具的 `[jxr-content]` 字段与 T.832 表一致；真实 Game DVR 样本（用户提供后）字段与 `JxrDecApp`/WIC 读数一致；
2. **像素**：`hdr_rgba16.jxr` 的 `auto` 与显式 `mobius` 同值，且与显式 `none` 明显不同；`hdr_rgb16.jxr` 不再输出 `rgb24`；
3. **决策**：`cjxl`/`cjpegli` 管道的 `-i` 前声明、`-x color_space`、`--intensity_target` 来自原始 JXR，不来自 PAM；
4. **出口**：JXR 输出的 ICC/PTM/COLOR_SPACE 可被本探针回读；JPEG/AVIF/JXL 的 HDR/SDR 语义与目标一致；
5. **不拒绝**：坏 ICC、PTM 冲突、`COLOR_SPACE=1` on float、截断 IFD 都有产物或明确非色彩原因失败，且条目标黄；
6. **绑定**：每条门禁读数绑定当轮 `FfmpegGui.dll` / `ServiceProbe.dll` 的 `sha256[:16]`，不拿旧日志充数；
7. **文档**：本计划在 `docs/INDEX.md` 登记；实施结果另写 `JXR_CONTENT_IDENTIFICATION_FIX_RESULT_2026-11-18.md`，不回改本计划。

---

## 9. 风险与待拍板

| 风险/待定 | 建议默认 | 说明 |
|---|---|---|
| fixed point 是否完整解码 | **P1 识别 + P2 支持 s2.13/s7.24** | `JxrDecApp -c 11/14/24/27` 已存在；如实现成本高，先识别并点名，不要把它当 UINT |
| YUV/CMYK 范围 | **识别 + 点名 + 可解码路径** | 不把 CMYK 塞进 RGB 引擎；YUV 按 PTM matrix 转 RGB |
| ICC LUT-only | **携带 + 警告，不拒绝** | 依赖独立 LUT ICC 计划；本批只接入 `ColorSpaceIdentify` |
| scRGB 1.0 的 nits | **203；ICC `lumi` 存在时用 `lumi`** | 不要把 PQ 的 10000 当 scRGB 白点 |
| JXR 输出 ICC/PTM | **直接写 IFD，不用 EXIF 冒充** | `JxrEncApp` 不写 ICC；exiftool 的 EXIF 字段不是 JXR 色彩标签 |
| `bt2446a` 在 JXR 线性域 | **不适用则点名警告** | 不静默当 none，也不硬套编码域公式 |
| 是否新增 `ServiceProbe jxrcontent` | **建议新增** | 便于门禁做字段级断言；需同步 `TestGroups` 与计数 |
| 真实 Game DVR 样本 | 需用户提供到 `tests/output/_tmp/jxr-real/` | 当前环境无法访问截图里的 `C:\Users\Kate\...`；合成夹具不能替代真实 ICC/COLOR_SPACE 分布 |

---

## 10. 提交切分（建议）

1. `feat(jxr): add container content probe and source context`（P0：探针/数据模型/日志/内容门禁，不改像素）
2. `fix(jxr): select passthrough arm and decode numeric kind from PIXEL_FORMAT`（P1：选臂/3ch/fixed point/预乘）
3. `fix(color): deliver original JXR decision and route linear HDR through Mobius`（P2：决策传递/tone map/整数曲线）
4. `feat(jxr): write ICC/PTM color metadata and surface warnings`（P3：出口元数据/GUI 警告）
5. `test(jxr): expand content-id and high-bit-depth gates`（如与 1 同批则合并，保持一次提交主题 ≤3）

每批只跑定向门禁；构建后记录 `FfmpegGui.dll` / `ServiceProbe.dll` 指纹。不要与 Mobius 默认切换、ICC/CICP 警告化等未提交改动混为一批。

---

## 附录 A：T.832 取值速查

### A.1 `COLOR_PRIMARIES`

| 值 | 语义 | 本项目 token |
|---|---|---|
| 1 | BT.709 / sRGB / sYCC / xvYCC | `bt709` |
| 2 | 未指定 | 无（按惯例补） |
| 4 | BT.470 M / NTSC 1953 / FCC | `bt470m` |
| 5 | BT.601-625 / BT.470 B,G / PAL / SECAM | `bt470bg` |
| 6 | BT.601-525 / SMPTE 170M / NTSC | `smpte170m` |
| 7 | SMPTE 240M | `smpte240m` |
| 9 | BT.2020 / BT.2100 | `bt2020` |

### A.2 `TRANSFER_CHARACTERISTICS`

| 值 | 语义 | 本项目 token |
|---|---|---|
| 1 | BT.709 / BT.1361 / BT.2020 | `bt709` |
| 2 | 未指定 | 无 |
| 4 | gamma 2.2 | `bt470m` / `gamma22` |
| 5 | gamma 2.8 | `bt470bg` / `gamma28` |
| 6 | BT.601 / SMPTE 170M | `smpte170m` |
| 7 | SMPTE 240M | `smpte240m` |
| 8 | linear | `linear` |
| 11 | xvYCC | 当前 `TransferCurve` 无精确 token；警告 + 以 BT.709 近似 |
| 12 | BT.1361 extended | 当前无精确 token；警告 + 以 BT.709 近似 |
| 13 | sRGB / sYCC | `iec61966-2-1` |
| 16 | PQ / ST 2084 | `smpte2084` |
| 18 | HLG / BT.2100 | `arib-std-b67` |

### A.3 `MATRIX_COEFFICIENTS`

| 值 | 语义 |
|---|---|
| 0 | RGB/GBR，无矩阵 |
| 1 | BT.709 |
| 2 | 未指定 |
| 4 | FCC |
| 5 | BT.601-625 |
| 6 | BT.601-525 / SMPTE 170M |
| 7 | SMPTE 240M |
| 8 | YCgCo |
| 9 | BT.2020 non-constant |
| 10 | BT.2020 constant |
| 14 | ICTCP |

### A.4 `PIXEL_FORMAT` 的 `Num` / `BPC`

- `BD8` = 8-bit UINT；
- `BD10` = 10-bit UINT；
- `BD16` = 16-bit UINT；
- `BD16S` = 16-bit signed fixed point（s2.13，1.0=8192）；
- `BD32S` = 32-bit signed fixed point（s7.24，1.0=16777216）；
- `BD16F` = IEEE half（s5e10）；
- `BD32F` = IEEE float32（s8e23）；
- `BD1WHITE1` / `BD1BLACK1` = 黑白 1-bit；
- `RGBE` = 32bppRGBE（3×8-bit mantissa + 8-bit shared exponent）。

---

## 附录 B：本轮不落地但需保留的接口

- `ColorSpaceDescriptor` 当前要求 `RgbToXyzD50` 才能映射；LUT-only ICC 的 `IsMappable=false`。本计划要求 JXR 识别层在此时**仍返回一个描述符**（`RgbToXyzD50=null`、`SourceIccPath` 指向 ICC），由规划层选择“携带”而不是拒绝。
- `TransferCurve` 当前没有 xvYCC/BT.1361 精确曲线；JXR 识别层遇到 11/12 时记录 `CurveKind=Unknown`/近似，并在 `Warnings` 里点名。等 LUT/曲线扩展落地后，再替换。
- `FixHdrNits` 目前按曲线给 PQ=10000/HLG=1000；JXR 识别层应显式设置 `NitsOfLinearOne`，不要让通用兜底覆盖 scRGB 的 203/ICC `lumi`。
- `ColorStrategyPlanning` 的 `srcLinearHdr` 分支当前把“线性 HDR”当成“纯 OETF 可直出”；JXR scRGB 必须走 tone map。建议在 `ColorIntent` 增加 `SourceKind`（`GainMapPfm` / `JxrScRgb` / `RawLinear`）代替全局放宽，避免改掉已验证的 GainMap 码域分叉。

---

## 附录 C：与用户既有硬约束的对应

| 约束 | 本计划落点 |
|---|---|
| ICC/CICP 不准确只警告、不拒绝 | §3.3 / §4.2 / §7.4；所有 JXR-COLOR 警告不设 exit 91 |
| GUI 必须真实可用 | §5.2 / §8；GUI 的 auto、媒体信息、条目标黄同步 |
| 无 exiftool 用 ffmpeg 回退，不黄警，只提示“没有这玩意” | §4.1 / P3；JXR 色彩识别不依赖 exiftool；缺失只 info |
| HDR 输入即使开 GainMap 也要正确输出 HDR | §5.2 的 JPEG GainMap 行；底图 tone map、增益图用实测峰值 |
| JPEG 只保留 GainMap 开关 | §5.2；不做“传统 HDR / 增益图 HDR”二选一 |
| `auto` 要下传 primaries/trc/matrix | §4.4 / §5.1；JXR 的 `auto` 不再落成 `none` |
| HDR→JPEG 无 GainMap 映射要正确 | §5.1 / §5.2；SDR 目标必须 Mobius/显式曲线，不允许静默钳位 |
| 默认 HDR→SDR 用 Mobius、显式 hable 保留 | §4.4；`ToneMap` 单一实现 |
| 显式输出矩阵跨族冲突 A 直接拒绝 | §1.2；本计划不改 |

---

## 附录 D：实现结果（2026-11-18）

本计划已落地，核心不是“把 JXR 都当 sRGB”，而是**先读容器再决定**：

- `JxrContentProbe` 直接解析 T.832 文件头/IFD，真实字段已实测：`hdr_rgba16.jxr` 为
  `64bppRGBAHalf / 4 通道 / Float / BD16F / color_source=scrgb-default / primaries=bt709 /
  transfer=linear / nits=203`，文件头 `49 49 BC 01 20 00 00 00`（version=1，first IFD=32）。
- `PIXEL_FORMAT` 驱动 `JxrHighPrecisionDecode.ChoosePassthroughArm`，实测 `-c 25` 命中
  `HalfFloat16`；容器不可读才回退 ffprobe/盲试。
- HDR→SDR 在线性域执行，默认 Mobius；显式 `--color-tone-map none` 只钳位并警告。
  JXR→JXL 显式 sRGB：`src=Linear srcNits=203 dst=sRGB map=mobius`，解码后
  `YAVG=44861.8 / YMAX=55857`（旧 auto 路径是 `YAVG≈53579.6 / YMAX≈60371` 的钳位读数）。
- HDR 输入 auto：JXL 出 `Rec.2100 / PQ / 16-bit`；PNG 出 `BT.2020 / PQ / Identity / full=1`；
  AVIF alpha 两步法出 `bt2020 / smpte2084`。显式 JPEG sRGB 无 GainMap 时
  `YAVG=184.662 / YMAX=234`，高光未裁。
- JXR 出口按 T.832 重建 IFD：SDR 写 `COLOR_SPACE=1`（回读 `ColorSpace=sRGB`），
  HDR 写 `PTM 9/16/0/1`（探针回读 `color_source=ptm / hdr_nominal=true`）；
  ICC 注入后回读 `icc_status=Present / icc_bytes=616 / descriptor=Display P3`。
- 坏 ICC 不再拒绝：`icc_status=Unreadable / icc_bytes=200` 时仅 `MarkWarning`，
  产物仍生成（`bad_icc.jxl` 1,092 B，退出 0）。
- 定向门禁：`verify-jxr-highbitdepth.ps1` 118 PASS / 0 FAIL / 1 SKIP；
  `verify-jxr-backend-choice.ps1` 15 PASS / 0 FAIL。全 solution build 0 errors / 0 warnings。

已知边界：fixed point 只有代码路径、尚无真实夹具；YUV/CMYK/NCOMPONENT 仍只识别+警告；
`SPATIAL_XFRM_PRIMARY` 只警告未施加；`scRGB` 峰值仍靠整帧扫描而非 ICC `lumi`。

