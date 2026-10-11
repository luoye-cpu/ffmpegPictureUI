# A5 · ICC/CICP 标注落地审查（HEAD = c18dc4a，只读）

## 0 一页结论

| # | 结论 | 标签 |
|---|---|---|
| 1 | **chad 方向判定完成：本项目写的是 native→D50，行主序，正确**。旧主张留下的"方向待定"可以销案。双锚：lcms `cmsvirt.c:148` `_cmsAdaptationMatrix(&CHAD, NULL, &WhitePointXYZ, cmsD50_XYZ())`（From=native, To=D50）+ iccgen 参照 profile 实测（把 chad 按行主序乘 D65 → 得 D50，误差 ≤1e-5；转置乘得 (1.0155,1.0287,0.7538) 全错） | 未复现→已判定(正确) |
| 2 | **`ColorSpaceRegistry.BrdfordAdapt` 的锥矩阵第三行末元是 1.0633，标准 Bradford / lcms 是 1.0296**（`ColorSpaceRegistry.cs:808` vs `cmswtpnt.c:236-241`）。后果：本项目生成的 ICC 的 chad 与参照差 **4.9e-4**，rXYZ/gXYZ/bXYZ 与参照差 **2.1e-4 ~ 2.8e-4**（把锥矩阵换成 1.0296 复算同一条链 ⇒ 残差降到 **5.1e-6**，即 40× 劣化完全归因于这一个常数） | 真缺陷-标准（精度代价 ≈0.13 LSB@8bit / 16 LSB@16bit，不构成可见误差；但它同时是像素矩阵的来源） |
| 3 | **保真 ICC 判据在 HEAD 仍只认 ProPhoto**（`IccProfileBuilder.cs:113`，行号未漂移）。但"Adobe RGB 源被归一 = 削色"这一说法**被实测推翻**：Adobe RGB 三条原色全在 BT.2020 三角形内、**越界面积 0.000%** ⇒ 归一不削色。真问题是另一面：**写侧已经能产 Adobe RGB (1998) ICC（`CurveFor:72-73`+`DescriptionFor:85`），判据却不许用它** ⇒ 明明可以"像素不动、只换标签"的无损出口被放弃，改成真的乘一遍矩阵。附带：Display P3 红原色实测在 BT.2020 **外**（y=0.320 > 边 0.3183），ProPhoto **23.5% 面积在 BT.2020 外** | 能力缺口（死代码，非"只改标签"违规）＋交 A4 的定量证据 |
| 4 | **没有任何一处拿 ICC 描述串当色域判据**。顺序固定为"色度+曲线度量 → 才兜底描述猜测"（`ColorSpaceIdentify.cs:132-134`），且实测 iccgen/lcms 产物描述恒为 `RGB built-in` ⇒ 兜底对本仓自产物永远不命中。唯一残留风险：兜底串 `GuessColorSpace` 只给 primaries 不给 TRC（`rec.2020`→`Rec.2020`，PQ 信息丢失） | 真缺陷-标准（低危，仅兜底路径） |
| 5 | **S2 的双重校验仍在**：色度 `DetectIccPrimaries`（容差 2e-3）+ 曲线 `CicpTokenOfCurve`（逐条等价判定，不命中返回 null），`QueueProcessor.cs:1537-1552` 要求**两者都命中**才写 cICP；任一不中 ⇒ `SkipSourceIcc`。该出口有日志（`:1569`），不是静默 | 已判定(符合判据) |
| 6 | **`IsValidIccProfile` 只查 `acsp` 魔数 + 长度≥128**（`IccProfileService.cs:545-549`）。负样本实测**全部通过**：128 B 全零+acsp / 截断到 300 B / 头 size 字段被改坏 —— 都判"合法 ICC"。而它同时是缓存命中门（`:341`、`:456`）与用户 PLAN/iccs 文件接受门（`:443`） | 真缺陷-静默失效（半截/坏 ICC 会被永久缓存；下游读取返回 null 才兜住） |
| 7 | **S3（仅 CICP）的 `StripIcc` 只清计划意图，交付链没有任何"删掉已在文件里的 ICC"的动作**（`ColorStrategyPlanning.cs:872-878` 全仓唯一 4 个消费点都在计划层；`QueueProcessor.cs:1578` 只写"应被剥离"的告警；`ExifToolService.cs:821` 明写"不删 ICC"）⇒ PNG/JPEG/WebP 在 S3 下可交付 **cICP + 源 iCCP 两套矛盾描述** | 真缺陷-标签自相矛盾（代码确证；产物未测） |
| 8 | **JXL 的"EXIF 后置有效"未复现**：exiftool 对 bare codestream（ffmpeg libjxl 产物，`FF 0A` 开头）**连 `-Comment=` 都拒**，`-m` 也拒，报 `Will wrap JXL codestream in ISO BMFF container for writing`、`0 image files updated`、文件逐字节不变。cjxl 有 `--container` 开关（`cjxl_main.cc:156-159`，元数据存在时才自动开 `:1141-1147`），**本仓从未传**（grep 无命中） | 未复现（需"产物是容器化还是裸流"这一维证据才能定案） |
| 9 | PQ 的 `curv` LUT：**1024 项里 57 项为 0**（EOTF 值 <0.5 u16 LSB ⇒ 亮度 ≤0.076 nit 全塌成纯黑），u16 线性步长 = 10000/65535 = **0.153 nit**。这是"把 PQ 塞进 ICC matrix/TRC"的固有上限，不是实现瑕疵；但注意 skcms 对 PQ 的归一惯例是 **1.0 ↔ 10000/203**（`skcms.cc:2618`），本项目是 **1.0 ↔ 10000** ⇒ 两个实现对"PQ ICC 的 1.0 是什么"不同理解 | 能力缺口（表示法边界）＋未判定(需消费端对拍) |
| 10 | 三个结构性规范陷阱复验：tag 目录升序 ✓（实测 11 项 ordinal 升序，二分+线性兜底 ✓）、4 字节对齐与 pad ✓、头 size == 实际字节 ✓（672/636/2688 三格全等）。**唯一致命项：`BuildText` 写的 `textType` 不带 NUL 终止符**（`IccProfileBuilder.cs:296-302`），而 lcms 写侧恒带（`cmstypes.c:965` "including separator"），同文件的 `BuildMluc` 反倒知道要 NUL（`:279`，注释还点名 exiftool 会报 Corrupted） | 真缺陷-标准（`cprt` 版权标签，不参与颜色转换，低危） |
| 11 | `DetectIccPrimaries` **不校验 tag 类型**，而同仓兄弟函数 `ReadIccColorants:197` 校验（`ReadTagType != "XYZ "` ⇒ null）。rXYZ 若为别的类型但 ≥20 B，前者会把负载当 XYZ 读 ⇒ 喂给 cICP 的"是否标准空间"判定可被误判 | 真缺陷-标准（低概率，两读路不对称） |
| 12 | **同一仓库有两份 D50**：写入端 `IccProfileBuilder.cs:31 {0.9642,1,0.8249}` = lcms 规范值（`lcms2.h:282-284`）✓；读取锚点端 `ColorSpaceRegistry.cs:706 IccD50Xy={0.34570,0.35850}` 推导得 {0.96429,1,0.82485} ⇒ ProPhoto（原生白就是 D50）本该 chad=单位阵，实测生成的是 diag≈(0.99995,1.00005,0.99974) | 真缺陷-精度（量级 2.6e-4；与"复用 ColorSpaceRegistry＝全流程唯一语义来源"的自述不符） |
| 13 | 交付面完备性：色彩后置唯一入口 `ApplyColorPostStepsAsync`（`QueueProcessor.cs:1484`）串 6 步；非主分支必须做过同源登记（`NoteColorDecisionSource:1443`，唯一写入点）才复用同一入口，否则**显式记"裁决点未参与"**（`:1128`）⇒ 历史上的"carry+指定空间+webp 零标注"型空档在 HEAD 没有同类复发（webp 走 `NeedsPostEmbedIcc` 后置，`Decision.cs:490`） | 已判定(通路闭合，fail-closed 有出声) |

## 1 方法与被推翻的假设

**三个独立实现做裁决锚**（本轮全部实测，不是引用旧文档）：
1. `tools/src/jpegli/third_party/lcms`（ICC 规范实现）：`cmsvirt.c:117-160` `cmsCreateRGBProfileTHR`、`cmswtpnt.c:232-267` `_cmsAdaptationMatrix`/`_cmsAdaptMatrixToD50`、`cmstypes.c:6052`（chad = 9×s15Fixed16ArrayType）、`cmstypes.c:925/965`（textType 读写）、`include/lcms2.h:282-284`（cmsD50X=0.9642 / cmsD50Z=0.8249）。
2. `C:\PLAN\VQBench\PLAN\ffmpeg-full\ffmpeg.exe`（git-2026-09-24，含 lcms2/iccgen/libjxl/libwebp）`-f lavfi … iccgen` 生成 5 张参照 profile（bt709×iec61966-2-1 / bt2020×bt2020-10 / smpte432 / bt709×linear / jedec-p22×bt709），逐 tag 解析。
3. `tools/src/libjxl/third_party/skcms/skcms.cc:2618-2629`（PQ 的业界归一）。

**被推翻/改写的旧假设（逐条）**
- 「chad 方向需对照 lcms 参照 profile 定」→ **不需要了**，两锚同判：native→D50、行主序；本项目写对了。
- 「Adobe RGB 归一会削色，所以保真判据漏了它」→ **削色不成立**（越界面积实测 0.000%）；漏的是"无损标注出口被死代码供着"。
- 「生成 ICC 的描述恒为 RGB built-in」→ **实测确认**：5 张 iccgen/lcms profile 的 `desc` 全是 `RGB built-in`、`cprt` 全是 `No copyright, use freely`、`creator='lcms'`。
- 「iccgen 对 PQ 明确 Not yet implemented」→ **实测确认**：`color_trc=smpte2084` 报 `Not yet implemented in FFmpeg, patches welcome`（`IccProfileService.cs:334` 的硬挡因此是必要的，不是过度保守）。
- 「ICC 后置无效、EXIF 后置有效」于 JXL → **后半句本轮未复现**（见 §0#8）。
- 「`ColorMapping/ImageEncoderArgs.cs` 有容器能力表（`CanCarryArbitraryIc` 等）」→ **范围文档过期**：该文件只有 JXL 注释（`:952`）；能力表实体在 `ColorMapping/ColorTransformPlan.cs:21`（字段）与 `:535-597`（14 格式表）。
- 「纯托管 ICC 写入的三处规范陷阱」→ 三处按 HEAD 复验：①TRC 方向（code→linear）**守住**（`para` type3 参数序 g,a,b,c,d 与参照 profile 逐值一致：sRGB = 2.399994/0.947861/0.052139/0.077393/0.040451）；②mluc 无 BOM+NUL 结尾**守住**（`:279`，偏移 28 与参照一致）；③PQ 传递函数常数**守住**（`SimdPixelOps.cs:295-299` m1=2610/16384、m2=2523/32、c1=3424/4096、c2=2413/128、c3=2392/128，逐条对 ST 2084；EOTF 符号顺序 `:307-316` 正确，且 `eotf(1.0)=1.0` 自检通过）。

**测量口径与两处环境限制（必须先声明，否则读数不可归因）**
- 本轮**没有跑产品本体**（不改源、不建工程）：`IccProfileBuilder.Build` / `IccProfileService` 读路的结论来自**逐行转写复算**（`tests/output/…`外的临时目录 `a5icc/builder.py`、`icctest2.py`），产出物按 C# 源码逐字节重放；所有 `文件:行` 已回读确认。
- 本机 shell **无法让 `cjxl.exe` / `avifenc.exe` / `exiftool.exe` 创建任何文件**（一律 `Could not open … for writing` / `Error creating file …_exiftool_tmp`，`-m`、绝对路径、换目录、`-o` 全试；同目录 `ffmpeg.exe` 写入正常）⇒ 这是环境限制不是产品缺陷。**绕行**：用 cygwin perl 5.42 + 仓内自带的 `exiftool_files/lib`（v13.59）跑 exiftool 的读写逻辑 ⇒ 写侧本轮可用（读侧 `exiftool.exe` 本来就可用）。cjxl/avifenc 无等价绕行，相应格子记 `未测`。

## 2 旧主张复验表

| 旧主张 | HEAD 复验动作 | 读回证据 | 标签 |
|---|---|---|---|
| ①`IccProfileBuilder.cs:128-140` 写 D50 语义 rXYZ 并同步写 chad，方向待定 | `cmsvirt.c:148` 语义 + 5 张 iccgen profile 的 chad 字节 ×D65 | `g_bt709_iec61966-2-1`: chad 行主序乘 D65 → (0.964196,1.000004,0.824908) vs 期望 D50 (0.964203,1,0.824905)；转置 → (1.0155,1.0287,0.7538)。rXYZ 按**列 0** 复算：与参照 max\|Δ\|=6.3e-6（lcms 锥矩阵）；按行 0 → 0.163（即转置读法直接错 16 个单位） | 已判定(本项目写法正确) |
| ②保真判据只认 ProPhoto（旧 :113） | 回读 `IccProfileBuilder.cs:110-114`＋调用点 `FfmpegCommandBuilder.cs:1369,1381`＋`IccProfileService.cs:427` | 行号未漂移，判据仍是 `s?.Key == "ProPhoto RGB"`；`NamedIccSlug:501-503` 也只收 prophoto/bt2100-pq ⇒ Adobe RGB 走不到保真出口 | 能力缺口（＋"削色"理由被推翻） |
| ③描述文字不可作色域判据 | grep 全仓 `GuessColorSpace` 消费点 | 唯一消费点 `ColorSpaceIdentify.cs:134`，位置在度量失败**之后**；`FromIccFile:41-43` 要求色度与曲线**都**读得出否则 null | 已判定(守住) |
| ④各容器 ICC/CICP 落盘能力 | 见 §3 逐格"写侧动作→读回证据" | 本轮亲测 8 格、未测 3 格 | 见 §3 |
| ⑤S2 双重校验（色度+曲线） | `QueueProcessor.cs:1541-1557`＋`Decision.cs:490` | `ip != null && it != null` 才改标签；否则 `SkipSourceIcc` 并留日志 | 已判定(守住，判据止于"无标注+出声") |

## 3 容器能力矩阵（HEAD 实测口径）

图例：✅=本轮亲测（写侧动作＋读回证据齐全）｜❌=亲测为否｜📄=只有旧文档/能力表 `VerifiedBy` 背书、HEAD 无本轮实测｜⛔=本机无法执行

| 容器 | 写侧动作（本轮实际执行） | 读回证据 | 与能力表是否一致 | 标签 |
|---|---|---|---|---|
| **PNG / ICC** | `ffmpeg -i src.png -vf format=rgb24,iccgen=…:color_trc=bt2020-10` | chunk 序 `IHDR,pHYs,iCCP(359 B),IDAT,IEND`；解压后 584 B ICC，`ProfileDescription=RGB built-in` | ✅ 一致（`EncoderWritesGeneratedIcc` 默认 true） | 已判定 |
| **PNG / CICP** | 同一命令，色彩标签**放 `-i` 之前** vs **之后** | 前：`cICP=[9,16,0,1]`（bt2020/PQ/matrix0/full1）+ `cHRM`；后：**完全没有 cICP** | ⚠ 补强：`PngCicpService.cs:11-14` 说"帧带色彩标签时编码器会写 cICP"，**但当 output 选项给就不写** ⇒ 后置补写这条不能省 | 已判定（并修正注释的适用条件） |
| **PNG / 双描述** | 同上带 cICP 的产物 | `cICP` 与 `cHRM` 并存；`TryStripChunksSupersededByCicp` 的剥离名单只有 gAMA/cHRM/sRGB，**iCCP 有意不剥**（`:146-150`） | ✅ 一致（`CanHaveBothIccAndCicp=true`；#31 定案成立） | 已判定 |
| **PNG / exiftool 后置** | `exiftool -overwrite_original -icc_profile<=bt2020.icc` | `1 image files updated`；`iCCP` 出现，`ProfileDescription=RGB built-in` | ✅ | 已判定 |
| **JPEG / ICC** | `-vf format=yuvj444p,iccgen=…` | APP2 存在，`ProfileDescription=RGB built-in` | ✅（`VerifiedBy="exiftool APP2"`） | 已判定 |
| **TIFF / ICC（编码器）** | `-vf format=rgb48le,iccgen=…` | 读回 `ProfileDescription=RGB built-in` ⇒ **本构建 tiff 编码器确实落盘** | ✅ 一致（证实 2026-09-15 那条纠正，旧"不写"结论已废） | 已判定 |
| **TIFF / exiftool 后置** | 同上 `-icc_profile<=` | `1 image files updated` + 读回 | ✅ | 已判定 |
| **TIFF / CICP** | — | TIFF 无 CICP 通路，S3 在 `ColorStrategyPlanning.cs:540-560` 走"隐含 sRGB 直通"或 **Reject** | ✅ 一致（`CanCicp=false` + `NoCicpReason=ContainerNonNormative`） | 已判定 |
| **WebP / ICC（编码器）** | `-c:v libwebp -q:v 80` + iccgen | 产物里 **ICCP FourCC 一个都没有**；exiftool 读不出 ProfileDescription | ✅ 一致（`EncoderWritesGeneratedIcc=false`，"iccgen 的 ICC 被丢"复现） | 已判定 |
| **WebP / exiftool 后置** | `-icc_profile<=` 到 libwebp 产物 | `1 image files updated` + `ProfileDescription=RGB built-in` | ✅（`VerifiedBy="exiftool ICCP"`） | 已判定 |
| **JXL / ffmpeg-libjxl / CICP** | 色彩标签在 `-i` 前 / 后 各一次 | 前：`Color space: RGB, D65, Rec.2100 primaries, PQ transfer, intent Relative`；后：回落 `sRGB primaries, sRGB transfer` ⇒ **标签必须在 `-i` 之前** | ✅ 复现旧主张 | 已判定 |
| **JXL / ffmpeg-libjxl / ICC** | `-vf iccgen` 后喂 `-c:v libjxl` | `jxlinfo -v` **无任何 ICC 行**；只剩 CICP，且被折成 `Rec.2100 primaries, **709 transfer**, intent **Perceptual**` ⇒ ICC 被丢并按 CICP 近似 | ✅ 复现（并新增：连 transfer 都被换掉、intent 被改） | 已判定 |
| **JXL / exiftool 后置** | `-m -icc_profile<=` 与 `-m -Comment=` 各一次到 bare codestream（首字节 `ff 0a`） | 两次都是 `Error: [minor] Will wrap JXL codestream in ISO BMFF container for writing` + `0 image files updated`，产物字节数不变（1082→1082） | ⚠ **旧"EXIF 后置有效"未复现**；本仓从未传 cjxl `--container`（`cjxl_main.cc:156-159` 有该开关，`:1141-1147` 仅在输入自带元数据时自动开） | 未复现（需判容器化与否） |
| **JXL / cjxl `-x icc_pathname` / `-x color_space`** | — | ⛔ 本机 cjxl 无法创建输出；且 `tools/src/libjxl` 里 `icc_creator`/`skcms_CreateICC` 源码未 checkout（`third_party/skcms` 只有 skcms.cc/h） | 📄 E2/E1 那两条"字节完全相同、ICC 胜出"只有旧产物背书，HEAD 无本轮实测 | 未测 |
| **AVIF / ICC+nclx** | avifenc 被沙箱拒写；`ffmpeg` 本构建无 avif muxer（无 `--enable-libavif`，`-still_picture` 选项不存在） | ⛔ | 📄 E1（`--icc`+`--nclx` 可共存、`--lossless` 强制 matrix=identity）只有旧实测背书 | 未测 |
| **HEIF / HEIC** | `ffmpeg -i src.png out.heic` 与 `-c:v libx265 out2.heic` | 两次 `Unable to choose an output format for 'out.heic'` + `Error initializing the muxer: Invalid argument` | ✅ 一致（能力表全 false 是对的） | 已判定 |
| **JXR** | 未执行（本轮无 JXR 通路取证） | — | 📄 `VerifiedBy` 自述"保守未翻位"，并记下 exiftool 实测可携带 ICC 但服务侧无通路 | 未测（能力位与事实不同步是有意的） |

## 4 新发现

**N1 `textType` 缺 NUL 终止符（写侧规范）** — `IccProfileBuilder.cs:296-302`
```csharp
private static byte[] BuildText(string ascii)
{
    var ms = new MemoryStream(); var w = new BinaryWriter(ms);
    w.Write(Ascii4("text")); WriteU32(w, 0);
    w.Write(Encoding.ASCII.GetBytes(ascii));
```
lcms 写侧对 textType 恒带结尾 NUL（`cmstypes.c:965`：`// Note there is an extra "\0" at the end` / `// Write it, including separator`）。同文件的 mluc 分支反而知道这条规则（`:279` `text + "\0"`，`:275` 注释还点名"带 BOM 会被 exiftool 报 Corrupted ProfileDescription"）。⇒ 规则只落在一处、没落到另一处。低危（`cprt` 不参与颜色转换），但严格校验器会红。标签：**真缺陷-标准**。

**N2 `DetectIccPrimaries` 不校验 tag 类型，与兄弟函数不对称** — `IccProfileService.cs:154` vs `:197`
```csharp
if (size < 20 || (long)off + 20 > icc.Length) return null;          // :154（只查界）
if (ReadTagType(icc, off) != "XYZ ") return null;                    // :197（还查类型）
```
后果：rXYZ 若被写成别的类型（长度仍 ≥20），`DetectIccPrimaries` 会把该类型的字节当 XYZ 读并可能"命中"某个已知空间 ⇒ `PngCicpService.DecideLabel` 拿到被误判的 `iccIsStandardSpace` ⇒ 写出与像素不符的 cICP（cICP 优先级高于 iCCP）。标签：**真缺陷-标准**（触发概率低，方向性错误）。

**N3 `IsValidIccProfile` 只做魔数检查 ⇒ 负样本全通过** — `IccProfileService.cs:539-552`
按 HEAD 逐行转写后跑"参考样本→余量为 0→负样本"三档锚：
| 输入 | IsValidIccProfile | 色度读取 |
|---|---|---|
| 完整 584 B 参照 ICC | True | (0.67348,0.16566,0.12505 / 0.27904,0.67534,0.04561 / −0.00194,0.02998,0.79684) ✓ |
| 截断到 300 B | **True** | null |
| tagCount 改成 40（超出文件） | **True** | null（`FindIccTag:515` 界检有效） |
| rXYZ offset 指向 EOF 之后 | **True** | null（`:154/:196` 界检有效） |
| rXYZ 类型改 `para` | **True** | null（仅 `ReadIccColorants` 有效） |
| 128 B 全零 + 注入 `acsp` | **True** | null |
| 头 size 字段改成 999999，文件不动 | **True** | 照常读出 |
即：**读侧界检是好的**（没有"少读字段还越界/崩"的失步风险），坏在**校验门与读侧不同源**：坏 ICC / 半截 ICC 被 `IsValidIccProfile` 认下来后，会进缓存（`:341` `stdicc_*`、`:456` `named_*.v{WriterVersion}.icc`）并被当"这有 ICC"用；`ExtractIccFromBase64:105-108` 同样只看长度+魔数。标签：**真缺陷-静默失效**。最小修法口径：加"头 size == 实际长度"和"tagCount 目录自洽 + 关键 tag 可读"三条（都是 ICC.1 自带的检查，不是新发明）。

**N4 S3 的"只留 CICP"在交付端没兑现，可交付自相矛盾的双描述** — 计划层 `ColorStrategyPlanning.cs:872-878` 的 `StripIcc` 只把 `AttachIcc/IccPath/IccPrimaries/IccTrc/IccMatrix/IccFromGenerator` 清掉（=不再生成、不再附着），全仓 4 个调用点（`:590/:597/:766/:1063`）**无一在文件上执行删除**；`ExifToolService.cs:821` 明写"⚠ 不删 ICC"；`QueueProcessor.cs:1578` 遇到残留只留一句 `⚠️ 仅CICP 策略下输出仍含 iCCP（应被剥离）`。⇒ PNG/JPEG/WebP × S3 若经 `-map_metadata 0` 或元数据恢复带回源 ICC，产物里就是"新 cICP + 源 iCCP"两套矛盾描述（且 cICP 优先级更高，老查看器只认 ICC 会解成源色域）。标签：**真缺陷-标签自相矛盾**（代码确证，产物未测）。

**N5 两份 D50 常量并存** — `IccProfileBuilder.cs:31` `{0.9642,1,0.8249}`（= lcms `lcms2.h:282-284` 规范值，也 = 5 张参照 profile 的 wtpt 0.964203/1/0.824905 的同一定点量化）vs `ColorSpaceRegistry.cs:706` `IccD50Xy={0.34570,0.35850}` ⇒ 推导 {0.96429,1,0.82485}。ProPhoto 的原生白就是 D50，本该 chad = 单位阵，转写实得 diag≈(0.99995,1.00005,**0.99974**) + 非对角 ≈4.6e-5。⇒ 与 `IccProfileBuilder.cs:26` 自述"复用 ColorSpaceRegistry＝全流程唯一色彩语义来源"不符（写入端用的是自己的字面量，读取锚点用的是推导值）。标签：**真缺陷-精度**（量级 2.6e-4，8-bit 下不可见）。

**N6 Bradford 锥矩阵常数（本轮最硬的定量结论）** — `ColorSpaceRegistry.cs:808`
```csharp
var M = new[] { 0.8951, 0.2664, -0.1614, -0.7502, 1.7135, 0.0367, 0.0389, -0.0685, 1.0633 };
```
lcms（`cmswtpnt.c:236-241`，注释就叫 Bradford）第三行是 `0.0389, -0.0685, 1.0296`。归因实验（同一条 PrimariesToXyz→BradfordAdapt→Mat3Mul 链，只换这一个数）：
| 空间 | 用 1.0633（HEAD）与参照 max\|Δ\| | 用 1.0296 与参照 max\|Δ\| |
|---|---|---|
| chad (bt709) | 4.93e-4 | **5.08e-6** |
| 色度矩阵 bt709 | 2.145e-4 | 6.25e-6 |
| 色度矩阵 bt2020 | 2.847e-4 | 6.63e-6 |
| 色度矩阵 smpte432 | 2.566e-4 | 7.22e-6 |
| 色度矩阵 jedec-p22 | 2.089e-4 | 7.26e-6 |
消费面不止标签：`ColorantsD50`（读 ICC 判原色的锚，容差 2e-3）与 `NamedSpaceColorantsD50`/`LinearMatrixBetweenPrimaries`（**真像素矩阵**）都过这个函数；`ColorSpaceIdentify.cs:73` 命中已知空间后还会把实测矩阵**替换成锚值**（"消除定点取整噪声"），于是这 2.1e-4 直接进入像素路径。⇒ 判据①（符合真实标准）不满足；判据②（无意义精度损失）勉强：8-bit 0.13 LSB、16-bit ≈16 LSB。标签：**真缺陷-标准**（并吃掉 2e-3 容差预算的 ~14%，不改变判定结果）。

**N7 PQ 用 ICC `curv` 表达的固有上限 + 与 skcms 的归一分歧** — `IccProfileBuilder.cs:75-79, 258-266`
1024 点均匀按 code 采样、u16 存线性 ⇒ 实测 **57 项为 0**（0.5 LSB=0.076 nit 以下全黑），单调 ✓，末项 65535 ✓；n=4096 时 0 项变 228（因为 0 区是按亮度切的，采样越密落入越多），分段线性最大误差 0.150 nit@n=1024 / 0.079 nit@n=4096。而 skcms 的"PQ 参考 TRC"注释（`skcms.cc:2618`）写的是 **scaled such that 1.0 will map to 10,000 / 203** ⇒ 与本项目"1.0 = 10000 nit"是两种归一（ICC 的 `curv` 值域被 0..1 钉住，白归一装不下 49.26 峰值，所以严格说 ICC 里只有峰归一可行；skcms 那条走的是它内部的 7 参 PQish，不是 ICC curv）。⇒ 结论应是"**ICC 表达 HDR 有天花板，HDR 应走 CICP**"，而不是"PQ ICC 精度可调"。另外：`Math.Clamp(c.TableEntries,3,4096)` 允许 4096 但硬编码选 1024，选型依据（"同 libjxl icc_creator 做法"）在本 checkout 里**无法核实**（icc_creator 源码不在）。标签：**能力缺口（表示法边界）** + **未判定(需消费端对拍)**。

**N8 CICP 三元组自洽性** — 未发现"matrix 与 primaries 不匹配"的产出格子：
- `PngCicpService.TryInsertCicp:97` 把 matrix 硬编 0、full 硬编 1（PNG 是 RGB 原生 ⇒ 正确），实测产物 `[9,16,0,1]` 印证；
- 我给的 `-colorspace bt709` + `-color_primaries bt2020` 组合，ffmpeg PNG 编码器自己把 matrix 落成 0，没有把矛盾写出去；
- `ColorSpaceRegistry.ValidateCicpTriple:757-763` 有 bt2020↔矩阵、bt709/p3↔bt2020nc 两条不匹配告警；缺口：`smpte431`（DCI-P3）与非 431 系矩阵的组合不在表内，且 `knownPrim` 白名单（`:734`）不含 `bt470bg/smpte170m/bt470m/jedec-p22` ⇒ 这些格会先报"非工具链常用值"而不是更准确的"矩阵不匹配"。标签：文档过期/覆盖不全（低危，非阻拦）。
- `unlabeledIsFine`（`Decision.cs:486-487`）只看 primaries+trc 不看 matrix —— 对 RGB 容器是对的，但同一判据若被用到 YUV 容器上会失守；本轮没有发现这种复用。

**N9 `GetOrGenerateStandardIcc` 的缓存键有一维是空的** — `IccProfileService.cs:340,352-353`：`zscale=pin=bt709:tin=iec61966-2-1:min=bt709:p=…:t=…:m=…` + `iccgen`。ICC 只由 (primaries, trc) 决定（matrix 在 RGB 的 ICC 里没有对应物），但文件名带 `_{matrix}` ⇒ 同一张 ICC 会被生成/缓存多次。无正确性影响。标签：性能项/存储冗余。

## 5 未证边界

1. **cjxl 出口的两条老证据**（E2「ICC 与 CICP 不可共存、ICC 胜出、产物与仅 ICC 字节全等」、09-26「`-x icc_pathname` 写 BT.2020 且解码像素 sha 不变」）—— 本轮 ⛔ 无法产 JXL。能力表 `["jxl"] VerifiedBy` 目前只有旧产物背书。要复现只需：能写文件的 shell + 同一输入跑 4 格（仅 ICC / 仅 color_space / 两者 / 都不给）比 sha。
2. **AVIF 全行**：avifenc 被沙箱拒写、本 ffmpeg 无 avif muxer。E1 那两条（`--icc` 可写、`--icc+--nclx` 共存、`--lossless` 强制 identity）与 `heic/heif` 之外的 AVIF 支持面，HEAD 无本轮实测。
3. **JXL 后置元数据的"容器化与否"**：`cjxl_main.cc:1141-1147` 说明输入自带 EXIF/XMP/IPTC/jumbf/jhgm 时会自动开 container ⇒ 那条路上 exiftool 可写；裸流（PPM 管道、无元数据输入）上不可写（实测拒）。判"旧主张是否被推翻"需要按产品真实出口分档跑一次。
4. **产品级格子**（本轮全部为"工具级实测 + 源码静态审查"，未跑 `FfmpegGui`）：S1/S2/S3/S4 × 14 格式 × {保真 / 指定空间 / carry / cicp-only} 的"色彩后置是否都过、有无叠加排除型空档"的**穷举**，需要 `verify-color-strategy`/`verify-color-wiring` 那两类批次跑起来才有格子级结论。§0#13 只证明"入口结构上没有静默分支"，不证明"每格结局正确"。
5. **N4 的可观测性**：S3→PNG/JPEG/WebP 是否真的会交付残留 iCCP，取决于 `-map_metadata 0` 与元数据恢复的实际行为；静态可证的是"没有任何一处删 ICC"。缺一张实拍产物。
6. **Adobe RGB 归一的传递函数代价**：`ColorSpaceRegistry.cs:205` 自述"仅改 primaries，传递函数按 sRGB 曲线保持（Adobe~2.2≈sRGB）"。Adobe RGB (1998) 是纯 γ2.2，sRGB 是分段曲线 ⇒ 这两者在暗部可分（`TransferCurve.Gamma22` 与 `Srgb` 的等价判定 `CicpTokenOfCurve:961-966` 把它们当成**不同** token，说明项目自己也认为不等价）。代价量级未测（需一次 γ2.2→sRGB 分段的往返 PSNR/ΔE 实测）。
7. **PQ ICC 的消费端解释**：`curv` 里 1.0 = 10000 nit 这个约定，被 skcms/ColorSync/Windows ICM 分别怎么解释 —— 未对拍（缺可执行的第三方 CMM；`tifficc`/`icclint` 本机无）。这决定 N7 是"边界"还是"缺陷"。
8. **`IccGamutCandidates = {bt709, smpte432, bt2020}`（`IccProfileService.cs:134`）**：读 ICC 判原色只认这三家 ⇒ Display P3 之外的（DCI-P3、P22、EBU3213）一律返回 null。是否够用于 GainMap 底图判色域，属 A4/B1 边界，本轮只登记。
