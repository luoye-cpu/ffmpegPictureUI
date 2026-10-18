# 索尼 RAW 编辑元数据的搬运与读回（dngtool）

适用范围：`dngtool -e` 把**非 DNG 的索尼 RAW（ARW）**转成 DNG 时，对 Imaging Edge
写入的调整参数、厂商注记与预览的处理方式。只写办法与状态，判断依据与过程记在
`tests/output/sony-dng-analysis/READINGS.md`。

## 1. 一句话结论

ARW 里那批编辑值**不翻译、不重解释**：`dngtool -e` 把**整个源文件**按规范
`OriginalRawFileData`（tag 50828 / 0xC68C）嵌进产物，配 `OriginalRawFileName`(50829)
与 `OriginalRawFileDigest`(50973)。读回靠 `dngtool -idc`，它先取 50828 里的原件、
取不到就直接读源 RAW，两边的输出必须逐字节相同。产物里**没有我方自造的容器**。

## 2. 规范载体 50828 的字节布局（写侧 = 规范原文，读侧 = 第二个独立实现）

标签内容按 DNG 1.7.1 §"OriginalRawFileData" 的 8 块头部起（**大端**，与 TIFF 自身字节序无关）：

| 偏移 | 长度 | 内容 |
|---|---|---|
| 0 | 4 | 数据 fork 的**未压缩**长度 |
| 4 | 4 | 资源 fork 长度（本机恒 0） |
| 8 | 4 | Mac 类型码 |
| 12 | 4 | Mac 创建者码 |
| 16 | 16 | 同样四块，给 `.THM` 文件（本机全 0） |
| 32 | `(ForkBlocks+1)×4` | 每 64 KiB 一块的**压缩后**长度，末项是余数块 |
| — | … | 逐块的 zlib（规范里叫 "ZIP"）流，**块间独立**，可单块解 |

两处口径不是推出来的，是由解码方钉住的：

- 块表里的长度**以 ForkLength 字段起点为基准**（exiftool `lib/Image/ExifTool/DNG.pm` 的
  `ProcessOriginalRaw` 用 `hdrLen = 4*(n+2)`、`p2 = pos + Get32u(pos+4)` ⇒ 它按这个基准走；
  换成"以块表起点为基准"它当场解出坏数据）。
- 解码方是 **exiftool 自己**，不是本工具：判据 ②b 拿 `exiftool -b -OriginalRawImage` 解出来的
  字节与源算 SHA256 对撞 ⇒ 编码不合规或漏字节都会立刻红（门禁实测 6,160,384 B → 5,880,033 B，95.4%）。
- `OriginalRawFileDigest`(50973) **不由我方计算**：SDK 在写文件时调 `dng_negative::FindOriginalRawFileDigest()`
  （`dng_negative.cpp:2460`，由 `dng_image_writer.cpp:8674` 触发）对**将要落进 50828 的那份数据**取 MD5 ⇒
  它是"嵌入件的指纹"而不是原件的指纹。所以 ②c 只要求它与嵌入件同现（标准字段齐不齐），
  还原证明归 ②b 的 SHA256，两者不互相顶替。

读侧另有一对**只在内存里**的窗口上限（head 16 MB / tail 8 MB，`kMetaHeadCap`/`kMetaTailCap`）：
`-idc` 直接读 RAW 时按 `HEAD=[0,pixelStart)`、`TAIL=[rawEnd,EOF)` 两段搭一个虚拟地址空间走索尼的链
（HEAD 从原 TIFF 基址起算 ⇒ 段内所有 IFD/子目录偏移逐字成立）。超限就不解析并打警告（不静默截断）。
**产物里没有这两段的任何痕迹**——它们从来不是容器，只是解析用的取数窗口。

## 3. 读回：`dngtool -idc <arw|dng>`

输出一个 JSON 到 stdout（失败走 stderr + 非零退出，绝不输出半截 JSON）：

```
{"origin":"arw|dng-embedded-original","sr2Dir":N,"idcDir":N,"currentVersion":N,
 "versionDirs":N,"chosenEntries":N,"chosenOffset":N,"versionOffsets":[N,N],
 "changedCount":N,"changed":[{"tag":"0x8018","type":9,"from":[0],"to":[200]},...],
 "entries":[{"tag":"0x8008","type":3,"count":1,...}]}
```

- 走的是索尼那条链：IFD0 `0xC634` → SR2 目录 → `0x7241`（IDC2_IFD）→ `0xd000 CurrentVersion`
  / `0xd001 VersionIFD`（**两元素 int32u 数组**，非链表）→ 各版本目录；链上目录的数据偏移
  一律**自相对**，`chosenEntries` 取 `CurrentVersion` 指向的那一版。
- `changed` = 生效版 vs **初始版(hop 0)** 逐项不同的 tag。**索尼把编辑就写在这一对目录里**：
  本机四件 ARW 的 hop 0 全是机身初值（`0x8018=0`、`IDCCreativeStyle=1`），生效版才是导出后的值
  （`0x8018=200`、`IDCCreativeStyle=7`）⇒ "源到底被改了什么"不需要标定就能读，差异条数 14/15/14/14。
  两处口径由 exiftool 独立复核（`tests/scripts/_crosscheck-idc-changed.py`）：
  名字表的 hex 位数要按 **3~4 位**匹配（`0x201` 是三位数，按四位正则整条漏掉）；
  `0x0201/0x0202`（TIFF 的 JPEGInterchangeFormat/Length）两版存的都是 `00 00 00 00`，
  但 exiftool 按 `IsOffset` 把 0 解析成**各自目录的基址** ⇒ 它自己报出一条伪差异
  （49578034 vs 49578824 恰好就是两个版本目录的地址）⇒ 我方 diff 显式排除这两个 tag。
- 刻意**不抄标签名表**：只出 tag/type/count/值，名字由调用方与 exiftool 对齐
  （避免本仓出现第二份"索尼语义真值"抄件）。
- `origin` 是唯一允许源与产物两边不同的字段；其余（含 `sr2Dir`/`idcDir`/`versionOffsets` 这些地址）
  两边必须逐字节相同。

## 4. 搬运开关与隐私谓词

| 设置 | 命令行 | 嵌入原件 50828 | ExifIFD 拍摄字段 | 预览/缩略图 |
|---|---|---|---|---|
| 默认（`PreserveAll`，含默认剥 GPS） | 无额外参数 | 嵌 | 补 | 原字节内嵌 |
| `--strip-metadata`（StripAll）或 `StripExifAll` | `-no-raw-meta` | 不嵌 | 不补 | 不嵌 |

谓词只有一份：`QueueProcessor.DngCarriesRawMetadata(options)`，预览侧（dry-run）与
执行侧都调它。GPS 单独走既有 `StripExifGps` 路径，不影响上表。

⚠ 但 50828 存的是**整个源文件** ⇒ 源里若真有 GPS，那份 GPS 就随原件在产物内（改嵌入件
就不是原件，所以不改）。裁法与 cjxl/jbrd 一致：**不因为一个标签就把整条搬运关掉**
（那是降级），而是两层各自点名——产品日志说明"仍搬运、容器层已排除、要严格剥除请用
`--strip-metadata`"，工具在 stderr 打 ASCII 键 `gps-in-embedded-original=1`。
第 77 条的 ⑮ 组锁住这三件事（GPS 源照嵌、容器层读不出 GPS、键必须出现；
`-no-raw-meta` 臂上键必须消失）。

## 5. 为什么不直接翻译成 DNG 原生位

本机 DNG SDK 1.7.1 里可用的表达位都已核对存在：`ApplicationNotes`(700/XMP)、
`AsShotNeutral`(50728)、`BaselineExposure/Noise/Sharpness`(50730/31/32，**没有**
Saturation/Contrast 两支)、`ProfileToneCurve`(50940)、`ProfileHueSatMapData1`(50938)、
`ProfileLookTableDims/Data`(50981/82)、`ExtraCameraProfiles`(50933)、
`OpcodeList1/2/3`(51008/09/51022)、`OriginalRawFileData`(50828)、BigTable 系列。
Opcode 只有 14 种固定形态（无表达式语法），`MapTable` 是任意 16-bit 单通道 LUT，
径向畸变 `kMaxTerms=15`、暗角 `kNumTerms=5`。

问题在**量纲**：IDC 里的 `BrightnessAdj=200` 这类整数没有本机可查的原始单位定义，
不标定就写进 `BaselineExposure` 属于造数。所以当前状态：

| IDC 面 | 载体 | 状态 |
|---|---|---|
| 调整参数数值面（creative style、D-Range、降噪等） | 嵌入原件 50828 | **已搬、已可机检读回**，并在转换日志里逐条点名（`idc-changed=N`） |
| 同上 → DNG 原生表达位 | `BaselineExposure` / `ProfileHueSatMapData1` 等 | **未接线**（需先标定，见 §6）。⚠ `BaselineExposure` 描述的是**已施加**的曝光（`dng_negative.cpp:5052` `fStage3Gain = 2^BaselineExposure`，`dng_render.cpp:977` 再把它减掉）⇒ 交付未改像素的负片时往它写"用户想加多少"是谎报，读方会朝反方向调 |
| 明度曲线 `ToneCurveBrightness{X,Y}` | 主 profile 的 `ProfileToneCurve`(50940) | **通道已备好**（`-idc-look`，见 §5b），等标定值；本机四件实测该曲线是恒等 ⇒ 不自动写 |
| 三通道曲线 `ToneCurve{R,G,B}{X,Y}` | **`OpcodeList3`(51022) 的 `dng_opcode_MapTable`，每通道一支** | **通道已实现**（`-idc-look` 的 `curveR/G/B` 键，见 §5c），等标定值。两处自我更正：① 上一版记成"标准无位"是错的 —— profile 装不下（`dng_camera_profile.h:320` 只有单条 `dng_tone_curve`），但 `dng_area_spec` 带 `fPlane`/`fPlanes`（`dng_misc_opcodes.h:73-74`）；② 我第一次把它放进 `OpcodeList2`(51009) 也错 —— list 2 作用在**去马赛克之前**（`dng_negative.h:913`），那时只有一个平面，谈不上分通道；只有 **list 3 = post demosaic**（同文件 :917）才对。代价实测：表 `count=0x10000` 条 uint16 = 128 KiB/支，三支的标签数据 393,376 B（产物 68,064,926 → 68,458,314 B），且 `DNGBackwardVersion` 由 SDK 自动从 1.1.0.0 抬到 **1.3.0.0** |
| 镜头校正表（三张） | 标准**有** opcode 位：`kOperWarpRectilinear`(1)/(14) + `kOperFixVignetteRadial`(3) | **已搬进原件；位有我先前说漏了。** `dng_lens_correction.h:10` 原文就写 "geometric distortion, lateral chromatic…"；径向 `kMaxTerms=15` **逐通道**（`fData[kMaxColorPlanes][15]`）+ 逐通道切向 `fTanParams[P]` + `HasLateralCA()` ⇒ 横向色差有正规表达；暗角 `kNumTerms=5` 的 r² 多项式。真约束**只是表到模型的对应**，而那条已在 §5e 实测掉：先前记的"横向色差 32 项→15 项残差 18.2% ⇒ 装不下"是**我的读法错**（把 32 项当一条序列拟），拆成两条 16 点剖面后 15 项残差 0.00%/量化台阶 25%；表值单位也有一手来源（三家独立实现，见 §5e），不再以"无一手材料"为由不做 |
| 预览 JPEG / 缩略图 | IFD0 预览 + SubIFD1 第二预览 | 原字节内嵌，实测 `Sony_ILCE7S.ARW` 那一份 734,118 B / 8,908 B 未重编码 |
| 白平衡 / `ColorTemperatureAdj` | `AsShotNeutral` = (1/`RedBalance`, 1, 1/`BlueBalance`) | **已随源兑现；"把 K 换算成中性"这条路被实测否证，不做**。产物中性值就是源增益的倒数（7S 实测 1/2.86328=0.34925、1/1.33984=0.746356，与 exiftool 读回的 `AsShotNeutral` 逐字对上）；而 K→xy 需要的 Planckian/daylight 拟合**既不在 DNG 规范里也不在本机 SDK 里**（`grep -rniE "kelvin\|planck\|tanner\|colortemperature"` 对 `dng_sdk/source/*.{h,cpp}` 命中 0，同一条命令在已知符号 `dng_1d_function` 上命中 21 ⇒ 探针活着）⇒ 自己拟合就是造数。本机唯一带 IDC 的那件 `ColorTemperatureAdj` 还**没被改动过**（不在 `changed` 里）⇒ 连可证的落点都没有 |
| ExifIFD 拍摄参数 | DNG `ExifIFD` | 逐条补写（快门/光圈/ISO/焦距/镜头/时间戳；`MaxApertureValue` 走 `FNumberToApertureValue` 换 APEX，镜头项需 EXIF 版本 ≥ 0230） |
| 白点 | 标准位 `AsShotNeutral`(50728) / `AsShotWhiteXY`(50729) / `CameraNeutral` | **已兑现——本轮实测，先前两条说法（"无位"、"未判定/编辑丢失"）都作废。** 把 7M2 用 `dngtool -d -o 0 -6 -T` 解到相机空间后，在 IDC `GrayPoint` 给的 (3981,1766) 取 16×16 块：归一化 R/G/B = **(0.9709, 1, 0.9458)**，与 12 个随机对照块同量级（0.94–0.98 / 0.77–0.98）⇒ 源里那组 `RedBalance/BlueBalance` 就是按这个灰点定的，产物 `AsShotNeutral` 取其倒数已经带着这次编辑，没有"第二次"可写。坐标本身仍是重复登记的同一点（8 值 = 4×(3981,1766)） |

⚠ 有一条曾经说得太满，现此更正：**"结构上能逐字落标准位的 11 条"不等于"现在就该搬"**。实测——

- 本机四件的 `Cropping/CropArea` 全是 `0 / [0,0,0,0]`、三对 `ToneCurve{R,G,B}{X,Y}` 全是 `(0,511)→(0,511)`
  恒等 ⇒ 往 `DefaultCropOrigin/Size`、`ProfileToneCurve` 里写这些值**一个像素都不改变**，
  只是造出"参数已进标准位"的观感（本仓硬规矩：不许只改标签）。
- `DefaultCropOrigin/DefaultCropSize` 这套位**已有权威**：它们由解码器的 margin/ActiveArea 推出，
  且规范上 DefaultCrop 是**相对 ActiveArea** 的偏移（`dngtool.cpp` 里 2026-09-20 那条修复就是为此）
  ⇒ 再拿 Sony 的 CropArea 去覆盖，是在两个坐标系之间猜，属于制造新缺陷。
- `ToneCurveBrightness{X,Y}` 那一条根本没有对应标准位（DNG 的曲线按 R/G/B 三通道，没有"明度"通道）。

⇒ 所以搬运的**前置**是 §6 的标定与坐标序的一手材料；在拿到之前，能诚实交付的是
"改动被逐条点名 + 整份原件按规范 50828 在档 + `-idc` 读回"，这也是转换日志里那行 `idc-changed=N` 的作用。

## 5b 已经接上的标准位：`ProfileToneCurve`（主 profile，`-idc-look`）

载体选择的两条依据都是实测，不是偏好：

- 规范原文（`DNG_Spec_1_7_1_0.pdf`）：`ProfileToneCurve` = 50940 (0xC6FC)，FLOAT，
  Count = Samples\*2，**Usage: IFD 0 or Camera Profile IFD**，每对 (x,y) 在 [0,1] 线性域、
  x 严格递增，且 "DNG readers should interpolate the curve using a cubic spline"。
- 不走 `ExtraCameraProfiles`(50933)：本机 SDK 1.7.1 的两段式尺寸预算在这条路上不闭合。
  产物里 extras 指向的块只剩 8 字节块头（`II` + magic `0x4352` + IFD 偏移 8），
  而 **`StripOffsets` 恰好等于块址+8** ⇒ 条带数据把 profile 目录整个盖掉了
  （`dng_image_writer.cpp:9669` 起；读手能识别这个块，所以这条判断可复核，见 §7）。
  规范也说了 extras "should only be used if there is more than one camera profile" ⇒ 单条曲线本就不需要它。

用法（**工具级，默认不启用**；产品侧不接线，等标定值）：

```
dngtool -e -i 输入.arw -O 输出.dng -idc-look 规格.txt
```

`规格.txt`：`curve=x:y x:y ...`（≥2 点、x 严格递增、全在 [0,1]、≤ `kMaxToneCurvePoints`=8192）；
可选 `policy=0|1|2`；`#` 起头算注释。三条 fail-closed：缺 `curve` 行、x 不递增、不认识的键
⇒ 非零退出且**不产出**。"缺 curve 行"必须显式判：`dng_tone_curve` 的默认值是 `SetNull()`
生成的 `(0,0)(1,1)` 恒等线（`dng_tone_curve.cpp:38-49`），它 `IsValid()` 为真 —— 只查 IsValid
就等于放行"规格里根本没写曲线"（实测放过一次，已补）。

⚠ 两点未证边界，写在这里而不是藏在结论里：
① 索尼的 `ToneCurve{Brightness,R,G,B}{X,Y}` 定义在 0..511 域、空间未定 ⇒ 归一化成 [0,1] 是**我方约定**，
   所以工具不自动映射 IDC 值，只接受外部给的曲线；
② 交付的是控制点，读者按三次样条插值 ⇒ 若机内实现是分段线性，**逐值不相同**，接标定时要单独量这条差。

## 5c 分通道曲线落在 `OpcodeList3`：`-idc-look` 的 `curveR/G/B`

```
dngtool -e -i 输入.arw -O 输出.dng -idc-look 规格.txt
规格.txt:  curveR=x:y x:y ...   curveG=...   curveB=...   （与 curve= 互斥）
```

- 形状：每支 = `dng_opcode_MapTable`（code 7，minVersion 1.3.0.0），`dng_area_spec` 的
  `fPlane` 取 0/1/2、`fPlanes=1`，表 65536 项 uint16。表在**解析期**就展开，所以坏曲线在解码
  之前非零退出（与 ⑭ 同形）；`curve=` 与 `curveR/G/B=` **互斥** —— 读者会同时应用 profile 的
  `ProfileToneCurve` 和 OpcodeList3，同写就是把一条曲线施加两次。
- ⚠ **area 不许留空矩形**（本轮踩出来并当场改的）：头文件与 `Overlap()` 的注释都写"空 area =
  整幅"（`dng_misc_opcodes.cpp:213-219`），但 `dng_area_spec::GetData` 先按 `fArea.H()/W()`
  （空矩形 = 0）钳制 pitch ⇒ 1 被钳成 0，再落到 `if (fArea.IsEmpty ()) { if (fRowPitch != 1 ||
  fColPitch != 1) ThrowBadFormat(); }`（同文件 :141-155）⇒ Adobe `dng_validate` 与自家
  `dngtool -verify` 双双报 error 100006。**注释说的便利语义不可往返，必须写具体整幅矩形**
  （尺寸取 `Stage1Image()->Bounds()`，与读者 `dng_inplace_opcode::Apply` 用的 `image->Bounds()` 同）。
- 恒等表**不写**（逐点 `table[i]==i` ⇒ 零像素变化，写了只是造"参数已进标准位"的观感），
  并打 ASCII 键 `opcode-skipped=N` 供门禁复核；写了的支数打 `opcode-channels=N`。
- 一手读数（`tests/output/opc/`）：结构由第二个实现 `_readback-dng-opcodes.py` 判 —— 解完正好落在
  byteCount 上、area 非空、pitch=1、表内容与规格逐点回算（最大 1 码）；
  **像素级"读者真施加"由 Adobe `dng_validate -3` 三臂渲染对账**：恒等臂与基准**逐字节相同**、
  非恒等臂 72,721,702 个平面值不同、且等于按规格算出的 LUT（精确 0.9908、最大 1 码 = 表的黑电平
  调整舍入地板，判据取 2 码）。

## 5d XMP：机制是标准的，可写的内容只有两条（本轮实测）

先前我把"不写 XMP"记成"不想引入私有 schema"—— 这是**把机制和内容混了**，在此更正：
`ApplicationNotes`(700) 就是 DNG 规范指定的 XMP 位置，XMP 本身是 ISO 16684-1，不算发明。

- 构建事实：本仓 SDK 以 `qDNGUseXMP=0` 配（`tools/src/dngtool/CMakeLists.txt:44` 的注释），
  `dng_xmp.cpp` 编进库但 writer 不发 `tcXMP`，且 `dng_xmp_sdk.cpp` 依赖 Adobe XMP Toolkit（本机无）
  ⇒ **dngtool 走 SDK 这条路写不了 XMP**。
- 可用路径已实测：`exiftool -XMP:CreateDate=… -XMP:ModifyDate=…` 能把 packet 写进 **IFD0 tag 700**
  （本机读回：type=1 count=2807，头 60 字节是 `<?xpacket begin='\xef\xbb\xbf' id="W5M0MpCehiHzreSzNTczkc9d"?>`），
  加完之后 Adobe `dng_validate` = **Validation complete**、`dngtool -verify` 照过 ⇒ 合规；
  这条路对本仓不是新机制（ICC/GPS 后处理已经在用 exiftool）。
- **已落地（本轮实现）**：IDC `VersionCreateDate/VersionModifyDate` 的文本本身就是 XMP 日期格式
  （实测 `2026:09:26 23:16:37` / `2026:09:26 23:40:29`）⇒ **零换算**直落 `xmp:CreateDate/xmp:ModifyDate`。
  实现三处：`RawService.ReadIdcVersionDatesAsync`（源文本按 `IsXmpDateForm` 过滤，读不到不是失败）、
  `ExifToolService.WriteIdcXmpDatesAsync`（**再检一遍形状才拼命令行** —— 操作数来自源文件）、
  `QueueProcessor.WriteIdcVersionDatesAsync`（只在 `carryRawMetadata ∧ ¬StripExifTime` 下调用 ⇒ 隐私请求优先；
  exiftool 缺席 / 源无 IDC 链 / 写入失败 三种情况各自点名，跳过 ≠ 成功）。
- **不能写的**：`crs:*`（Camera Raw 设置）。本机 exiftool 没有这张表 ——
  `Warning: Sorry, crs:Temperature doesn't exist or isn't writable` + `Nothing to do` ⇒ 要写就只能自己拼
  命名空间和值域，那才是"发明的实现"；何况索尼的 0..511 / ±档位与 Adobe 的 0..150、±100 之类值域
  **没有一手对应材料**，换算就是造数。

## 5e 镜头三表 → 标准 opcode：换算约定、拟合残差、以及三个真读者的实测

**换算约定有一手来源**（Sony 自己 0 文档；三家独立实现一致）：darktable 4.6 `src/common/exif.cc:1383-1405`
+ `src/iop/lens.cc:2224-2248`、RawTherapee `rtengine/lensmetadata.cc:160-163`（自注改编自 dt 4.6）、
rawmakase `crates/rawmakase-model/src/lens/embedded.rs:88-134`。约定：首字=样本数；畸变 `1+d/2^14`、
横向色差 `1+x/2^21`（前 n 个 R、后 n 个 B，G 恒 1）、暗角是透射指数 `2^(2^(v/2^13−1)−0.5)`；
半径归一到**整幅解码帧**（7M2 = 6000×4000）最远角。本机交叉验证：暗角末项 3152 ⇒ **1.1117×**（0.1528 EV）。

**拟到 DNG 标准形式的残差**（`tests/output/sony-dng-analysis/knot_ambiguity.py`，日志 `probe*.log` 同目录的 `lens-probe/`）：
暗角 → `FixVignetteRadial` 的 r²..r¹⁰ 五项，全域最大残差 **0.000084**（表幅 0.008%）；
畸变/横向色差 → 径向 warp 15 项（逐平面），残差 ≤0.1 px 级。⇒ **表达力不是瓶颈。**

**读者实测**（`tests/output/lens-probe/lens_probe.py`，日志 `probe3.log`；注入法 = 只改载体 51022 那条
IFD 表项的 count+value，列表追加在文件尾。⚠ 值 ≤4 字节必须内联，只改 value 不改 count 会让读者按旧
count 读出一片假 opcode）：

| 臂 | Adobe dng_validate 1.7.1 | LibRaw 0.22.1 (rawpy) | exiftool |
|---|---|---|---|
| `FixVignetteRadial` required / optional | rc=0，**都施加**：角/中比 ×[1.1124 1.1110 1.1123]，对上表预测 1.1117 | 解码 OK | 名字照打 `FixVignetteRadial` |
| `WarpRectilinear2`（只给平面 1 系数） | rc=0，**只动绿平面**：外环改动比例 R/G/B = 0.99%/**95.45%**/0.97% ⇒ 逐平面 warp = 横向色差的正规表达 | 解码 OK | `WarpRectilinear2` |
| `[14 optional] + [1 required]` | rc=0，像素与"只 14"**逐点相同** ⇒ 规范 p.105 的跳过规则真生效（`dng_opcode_list.cpp:147-152`） | 解码 OK | — |
| `[14 required] + [1 required]` | rc=0，两条都施加（R/B 随 ID=1 一起动：82.1%/92.7%/89.4%） | 解码 OK | — |
| 未知 ID=999 **required** | **rc=106「File format is invalid」**（`dng_opcode_Unknown::Apply` 里 `if (!Optional()) ThrowBadFormat()`，`dng_opcodes.cpp:226-239`） | 解码 OK（⇒ "能打开"不等于健康） | `[opcode 999]` |
| 未知 ID=999 **optional** | rc=0，**0 个平面值改变**（负控干净） | 解码 OK | — |

**已落地的写法**（`dngtool -lens-corr`，**默认关** —— 它改渲染像素，且产物与 Adobe 的转换结果分叉）：
① 三张表**都写**。我先前那句"横向色差那张不写（32 点按 R/B 读时两条剖面都非单调 ⇒ 索引语义未定）"
   被 darktable 的取数代码直接否证：`src/common/exif.cc:1398-1406` 逐字写着
   `ca_r[i]=toLong(i+1); ca_b[i]=toLong(nc+i+1)`，并硬校验 `2*nc == CA 计数`（本机 32=2×16 ✓）。
   非单调是索尼的数据形状，不是我的读法问题 ⇒ 按权威代码写，不猜。
② warp 发 **`[14 optional] + [1 required]` 成对且相邻**（实测 SDK 跳掉后一条；darktable 自己的
   解析器 `src/common/dng_opcode.c:224-283` 只认 ID 1/3、认不到 14 时仅 `dt_print` 不抛，
   且它把 opcode 存在性记进 `img->exif_correction_type = CORRECTION_TYPE_DNG` ⇒ 不会二次校正）；
   ID=1 那一支**只含偶次项单独重拟**（v1 的 `PutData` 只取 `fData[0,2,4,6]`），拟不到水位就不发兜底支。
③ 暗角与 ID=1 标 **required**（Adobe 自带样本就是 required）；SDK 按 `MinVersion(false)` 自动记账
   （`dng_image_writer.cpp:7944-7953`）⇒ required 的 1.3 项把 `DNGBackwardVersion` 抬到 1.3.0.0，
   而 optional 的 1.6 项**不抬**（自家产物实测：DNGVersion 1.4.0.0 / Backward 1.3.0.0）。
④ 结点位取 dt 原式 `r_i=(i+0.5)/(nc−1)`（`lens.cc:2225`，末点落在角外 r=1.0333）；
   A↔B 的后果上界（暗角 ≤0.010 EV、畸变 ≤2.08 px）已写进源码注释，不等平场标定件。
⑤ 增益**不做二次归一**：表首项 0 ⇒ 该结点增益本就是 1.0，两份实现对齐靠同一口径而不是巧合。

**落地后的实测读数**（`Sony_ILCE7M2_IDC.ARW` → `lens-on.dng`，工具 = 新编 dngtool）：

```
[dngtool-e] lens-vignette=applied nc=16 sw=257 maxres=0.000319 cornergain=1.104491
[dngtool-e] lens-warp2=applied planes=3 sw=1 ca=per-plane maxres=0.000058/0.000074/0.000175
[dngtool-e] lens-warp1=applied maxres=0.000053
[dngtool-e] lens-opcodes=3 已写进 OpcodeList3(51022) 作用域=6048x4024
LENS-READ OK ... → 3 支 [3,14,1] | 暗角: 残差=0.000319 ... warp2: flags=1 ... warp1: flags=0 偶次项残差=0.000053
```

体积代价 **+764 B**（68,064,926 → 68,065,690；对比三支 MapTable 的 393,376 B）。
写侧与读侧给出的**同一个残差数**（0.000319）来自两份独立实现（C++ 拟合 / python 逐结点回算）。
fail-closed 的形状：机内开关明确为 0 ⇒ `lens-skipped=<项>:switch-off`；表缺席 ⇒
`lens-tables=absent (<点名>)`；结点 <5（暗角五项）或 <3（warp）/ 残差超界 / 映射非单调 / 系数恒等
⇒ 逐条点名且**一条不写**。开关是**三态**（-1 读不出 / 0 明确关 / 非 0 开）：读不出时按 dt 口径照常
校正，不把它当成"关"—— 开关的真实形状是 `type=3(SHORT) count=1`（7M2 = 257/1/1），
按参数表那种 `type=8` 去读会读失败。

在册锁：第 77 条新增 **⑱ 组**（a 换算等价 · b 反控 · c 无表负控 · d 变异验牙 · e 与 look 通道共存 ·
f 像素级"读者真施加"）。默认档 **46/0**、重臂档 **47/0**（`DNGOPC_HEAVY=1 DNGLENS_HEAVY=1`）。

⚠ **撤回**先前两条说法：「RawSpeed 会对非 optional 硬失败 ⇒ 我们标不了 optional」（SDK 三个镜头类的构造函数
就把 flags 当入参：`dng_lens_correction.h:580/605/735`）与「必须标 optional 才安全」（Adobe 自带的
`sample_files/03_jxl_bayer_raw_integer.dng` 就是 **OpcodeList3 = WarpRectilinear，flags=0**，rc=0 且能渲染）。
另：RawSpeed 全仓**没有 OpcodeList3 的消费者**（`DngDecoder.cpp` 只查 LIST1 :592 / LIST2 :622；
`TiffTag.h:334` 那个 `OPCODELIST3 = 0xC742` 是错的，真 tag 51022 = 0xC76E）⇒ 放 list 3 的东西对它不可见。

**顺带量到的自家现状**：产品现在写的 3 支 required `MapTable` 也在 list 3 —— Adobe 施加，
RawSpeed 整条不看，darktable 的解析器不认 ID=7（只打印）。⇒ "look 曲线通道在别的读者里不生效"
不是假设，是上面这张表的同一机制。



## 6. 标定夹具

`tests/scripts/_fit-idc-scale.py`：喂 (IDC 原值, 外部实测值) 样本对，最小二乘出
`slope/intercept`，**不满足条件就拒绝出表**（非零退出、不写 NaN）：样本 < 5、
最大残差/量程 > 2%、或 IDC 取值只有一个点（x 无自由度）。

`py tests/scripts/_fit-idc-scale.py --selftest` 用合成已知斜率自检 7 条，其中包含
两条反控（期望值错位一个量级必须被抓、NaN 必须让 JSON 判据抛）。标定数据本身要
同一场景在 Imaging Edge 按已知档位导出后实测，属于链外动作。

## 7. 在册锁与复现

第 77 条 `tests/scripts/verify-dng-metadata-transport.ps1`（**41 判据，实测 41/0 @47.3 s**；
`DNGOPC_HEAVY=1` 时 42 判据，那一档实测 37/0 @47.2 s 是 ⑯e 刚接上时的读数）：
②规范载体（exiftool 的 `DNG.pm` 解 50828 回来的字节与源同 SHA256；50829/50973 必须在场而
`DNGPrivateData` 必须**缺席**；DNG→DNG 携带）、
③预览/缩略图原字节、④ExifIFD 逐条、⑤负控（`-no-raw-meta` 三段全零而像素摘要不变）、
⑥DNG→DNG 不落非 DNG 分支、⑦`-idc` 两边逐字节相同的语义 + 条目下限 + 负控、
⑧非 ASCII 输入名能转出产物且嵌入件读得回、⑨`changed` 与 exiftool 两版差异逐 tag 同集合同数值、
⑩日志 `idc-changed=N` 等于 exiftool 侧的数、⑪抹平单项后三处同步降 1、
⑫`-idc-look` 的曲线逐点落进 IFD0 的 `ProfileToneCurve` 且标签集合差恰好"只多 0xC6FC"、
⑬不加该参数时不得出现 `ProfileToneCurve`（⑫ 的反控）、⑭三张坏规格必须非零退出且**不产出**、
⑮GPS 臂（夹具由 exiftool 现场把 GPS 写进 7S 那份的副本；照嵌 + 容器层无 GPS + ASCII 键点名，
`-no-raw-meta` 臂上键必须消失）、
⑯分通道曲线落 `OpcodeList3`：⑯a 结构由独立读手判（落边界 / area 非空 / pitch=1 / 表逐点回算），
⑯b 不加参数时 51022 必须缺席，⑯c 恒等表**不写**且按 `opcode-skipped` 点名，⑯d 混给 `curve`+`curveR`
与不认识的键 ⇒ 非零退出不产出，⑯e（重臂）三臂渲染对账"读者真施加"，
⑰IDC 版本时间戳落进标准 XMP 位（⑰a 源侧两条日期形状即 XMP 日期、⑰b 按产品同法写入后独立读手逐字相等、
⑰c 写完 `dngtool -verify` 仍过、⑰d 无 IDC 链那份 `-idc` 非零 ⇒ 读不到就不写、⑰e **产品接线的源形锁**：
双条件守卫必须紧贴调用点 + 两处形状复检都在位）。
辅助件（都不是在册脚本，由 ⑨-⑯ 调用）：`tests/scripts/_crosscheck-idc-changed.py`、
`tests/scripts/_make-synth-idc-revert.py`、`tests/scripts/_readback-dng-profile.py`、
`tests/scripts/_readback-dng-opcodes.py`、`tests/scripts/_dng-opcode-render-ab.py`。

⑮ 组的两支变异读数（改 `dngtool.cpp` → 重建 → 跑本条 → 逐字节还原，`cmp` 证相等）：
把"源含 GPS 就关掉整条搬运"那一版复刻回去 ⇒ **30/2**，红的恰好只有 ⑮a（嵌入件与源不再同
SHA）与 ⑮c（解回的嵌入件里没有 GPS 了），其余 30 条全绿 ⇒ 这组判据抓的正是"GPS 引起降级"
而不是别的；变异第一版把 `carryRawMetadata = false` 写进了 `if` 的花括号**之外**（那句
fprintf 是裸语句，没有花括号），于是对所有源都关搬运 ⇒ **15/17**，②③④⑦⑧⑩⑪ 一起转红。
后者是个意外收获：它顺带证明这 17 条确实看着"三段搬运"本身，不是恒绿。

⑯ 组的两支变异各自**只红自己那条**（改 `dngtool.cpp` → 重建 → 跑本条 → 还原，还原后的差异用
`diff` 逐行给出、`grep -c MUTATION` = 0）：把 area 换回"空矩形 = 整幅"那一版 ⇒ **35/1**，红的只有
⑯a（编码 rc=0、ASCII 键照打，但独立读手 rc=1 ⇒ 没有第二实现就是静默坏件）；摘掉恒等跳过 ⇒
**35/1**，红的只有 ⑯c（`opcode-skipped` 命中 0 且产物里冒出 51022）。

```
pwsh -NoProfile -File tests/scripts/verify-dng-metadata-transport.ps1
```

⚠ ⑦ 组不能用 `Sony_ILCE7S.ARW` 那份素材：它的 `0x7240` 指向一个**空目录**
（exiftool `-v3`："SonyIDC directory with 0 entries"），拿它当源臂会让恢复判据退化成恒真；
所以另需链外件 `tests/output/raw_samples/Sony_ILCE7M2_IDC.ARW`。两份素材任一缺失一律点名红。

⚠ 跑像素/色彩路时把 `TMP`/`TEMP` 指进工作区（本机按进程拦 `%TEMP%` 写，否则
ffmpeg 报 `Permission denied`、`exit -13`，表现成一批无关红）。
