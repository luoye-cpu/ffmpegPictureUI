# A3 逐像素内核与数值精度审查（HEAD = c18dc4a，只读）

范围：`src/FfmpegGui/Services/ColorMapping/ColorKernels.cs`(876) / `src/FfmpegGui/Services/SimdPixelOps.cs`(414) /
`ColorMapping/RawGamutFit.cs`(297) / `ColorMapping/GainMapJpegCodec.cs`(176)。
方法：全文读 + 调用图正向 grep + 计划层可达性回溯；未跑任何门禁（读数一律标 `未测`）。

---

## 0 一页结论

| # | 结论 | 标签 | 证据锚点 |
|---|---|---|---|
| **N1** | **16-bit 主出口的表快路径判据漏了 `EncodeScale` 与 `DstIsHlgScene` 两条** ⇒ 任何 ≥256,000 像素的 HDR 目标计划（`--bit-depth 16` + png/tiff/apng/jxl，`CanHdr=true`）在快路径上**整段跳过目标域换算**，参考白被编成 PQ code 65535（应为 38055，nits 差 **49×**）。同形状缺陷 2026-10-05 已在 8-bit 兄弟出口修掉（`ColorKernels.cs:696`），**16-bit 这处没修** | **真缺陷-静默失效** | `ColorKernels.cs:503-504` vs `:696`；`ColorTransformPlan.cs:417/438-439`；`RawColorPipeline.cs:1087` |
| N2 | `TransformRgba8` 静默丢弃 `Tone`/`LinearScale`/`EncodeScale`/`SrcIsHlgScene`/`DstIsHlgScene` 五个算子，只对 `clamp=false` 抛；产品调用数 = 0 ⇒ 与 N1 同一形状，但**当前不可达**（潜伏） | 真缺陷-静默失效（潜伏） | `ColorKernels.cs:326-394`（全文无这五个字段）；src 内 grep 命中 4 处全在本文件 |
| N3 | `SimdPixelOps.LogGainToByteScalar` 用 **截断**而非取整 ⇒ Ultra HDR 增益图系统性偏暗 均值 −0.5 LSB、上界 −1 LSB（兄弟实现同式，属"两路一起偏"不是分叉） | 真缺陷-精度 | `SimdPixelOps.cs:225`、`GainMapEncoder.cs:337` |
| N4 | `RawGamutFit` 的抽样步长**与其注释里点名的行宽成整数倍**（6024 % 24 == 0、6000 % 24 == 0）⇒「每行落在不同列、不踩去马赛克周期性」的前提为假，实际只覆盖 251 个列相位；偏差方向正是本文件自己标为危险的那一侧（推向 `fits-bt709`） | 真缺陷-精度 + 文档过期 | `RawGamutFit.cs:174-175`（算式：6024×4024=24,240,576 ⇒ stride=24） |
| N5 | `Exp2Approx` 在 x 恰为 `n+0.5` 的相位上会 gather `Exp2Lut[1025]`，表长只有 1025（有效 0..1024）⇒ 4 字节越界读；`frac2==0` 时数值仍正确，除非邻字是 NaN/Inf | 边界项（潜伏，未复现） | `SimdPixelOps.cs:60,395-397,402-403` |
| 旧主张 1 | **已修**：居中常量 `7.5f/16f` 在位，两条 8-bit 出口共用；"精确可表示码值 no-op"断言有 **三处**（其中 selftest 0c 覆盖两条路径 × 16 相位并带正向对照） | 未复现（已修） | `ColorKernels.cs:151,376,761`；`tests/core/Modes/contract.cs:759-819`；`tests/diagnostics/Modes/selftest.cs:101-142` |
| 旧主张 2 | **已修**：抖动全部走码域（`code16/65535*255 + t`），两条 8-bit 出口共用同一个 `EncodeTo8`；引擎侧抖动判据在 src 内恰好两处（`:369`/`:667`），线性域加法已不存在（其余 `Dither` 命中是 `GifDither` 的 ffmpeg bayer 链与计划层开关名，不构成第二副面孔） | 未复现（已修） | `ColorKernels.cs:779`、`:383-385`、`:763-765` |
| 旧主张 3 | **未修**：`EncodeFromLinearInPlace` 的 `<summary>` 仍声称复用 `SimdPixelOps.FloatToSrgb8`，方法体是纯标量循环（行号从 165 漂到 **190**） | 文档过期 | `ColorKernels.cs:190-219` |
| 旧主张 4 | 部分过期：矩阵内核**仍是纯标量**（自述未变）；AVX2 `FloatToSrgb8` 仍有真实产品消费者（GainMap 底图 PNG）；`AutoUseSimdBinaries` 从**零消费者变成 2 个**（`SimdKernelRouting` 单点判据，2026-09-27 起） | 文档过期（§4.4 的"零消费者"已不成立） | `SimdPixelOps.cs:259,266-279,101`；`SimdKernelRouting.cs:49-62`；`GainMapEncoder.cs:407`、`PsnrCalculator.cs:192`←`QualityAnalysisService.cs:250` |
| 旧主张 5 | **未修**：`RawColorPipeline.cs:169`（原 :161）仍写"降位只发生在变换那一次遍历"，而 `MapPixFmt` 对 10/12-bit 交付给 `yuv420p10le/12le` ⇒ 该档降位在 swscale。按项目记忆**不许为此改实现**，只欠注释 | 文档过期 | `RawColorPipeline.cs:169`；`ImageEncoderArgs.cs:855,889-890` |
| 旧主张 6 | **已修**：恒等三元消失（注释点名"旧的恒等三元已删"）；`clamp=false` 的三处硬拒绝都在（建表、`TransformRgba8`、`TransformRgb48leToRgb8`） | 未复现（已修） | `ColorKernels.cs:408-410,414,330-332,664-666` |

**一句话**：旧账 6 条里 4 条已修且都配了断言（1/2/6 + 4 的开关侧），2 条纯文案未改（3、5）；
本轮新增的最高危是 **N1** —— 它不是新算法，而是 2026-10-05 那次修复**只补了 8-bit 出口、漏了 16-bit 出口**，
且被 `pixels < 256_000 不建表` 这道阈值天然遮住：**所有现有内核级测试都是 128×128 或 16×16 ⇒ 永远走解析式 ⇒ 永远绿**。

---

## 1 方法与被推翻的假设

**被推翻的三条（我先入为主的猜错）**

1. 我以为"8-bit 出口的居中只有测试在保"。正向 grep 结果是三处断言（`contract.cs:759` C12、`contract.cs:821` C13、
   `selftest.cs:101` 0c），且 **0c 同时扫 16 相位 × {解析, 表} 两条路径**，还有一条"与 `want+1` 比必须恰差 1"的
   活体对照（`selftest.cs:143-155`）——比我预期的严。⇒ 旧主张 1 判"已修"不是靠注释，是靠可红的断言。
2. 我以为 `TransformRgb48leToRgb8` 的抖动相位用相对坐标会破 tile 一致性。实读 `:760` 是
   `int ax = (originIndex + i) % w, ay = (originIndex + i) / w;`，调用方 `RawColorPipeline.cs:1118/1167` 传
   `rowStart * w` = **绝对像素线性索引** ⇒ 相位取的是绝对坐标，tile/行带切分不改结果。⇒ 不是缺陷（与 `:371-376`
   那条注释的教训一致，两处 8-bit 出口用**两套 origin 约定**：一个线性索引、一个 x/y 分开；本身不算错，但极易接错，见 §4-U3）。
3. 我以为 `GamutMapScalar` 的"不越界严格 no-op"只在注释里成立。按 `:257-287` 逐式推演（`valMinusGray<0` 才更新
   `grayMixSaturation`、`valMinusGray<=0` 才可能更新 `grayMixLuminance`），加上 `1f/(x==0?1f:x)` 的除零保护，
   文档 `:243-255` 的四步推演与代码**逐行对得上**，且 `0·x` 在 IEEE 下精确为 0 ⇒ no-op 成立。⇒ 撤销怀疑。

**取证手法**：调用图用"正向命中数"定性（例：`TransformRgba8` 在 `src/**/*.cs` 命中 4 处 = 文档 2 + 定义 1 +
异常文案 1 ⇒ 产品消费者 0）；可达性沿 `ToSpec → OutBitDepth → out8 → 内核` 一路回读，不下结论于"应该不可达"；
所有数值后果只做**纸面算式**（PQ 定义式 + PowerShell 求值），端到端读数一律标 `未测`。

---

## 2 旧主张复验表（逐条：旧写法 / HEAD 现状 / 证据 / 标签）

| 旧主张（§4.3/§4.4 + 项目记忆） | HEAD 现状 | 证据（文件:行 + 原文） | 标签 |
|---|---|---|---|
| **1** 抖动中心不居中：`Bayer4[i] − 0.5f/4f` ⇒ 偏置 +0.344 LSB，6/16 相位抬 +1；居中应减 `7.5/16` | **已改成居中常量**，`0.5f/4f` 在 HEAD 只作为**历史说明**留在文档注释里（`ColorKernels.cs:143` 那一行），代码中不再出现；
Bayer 使用点在 src 内**恰好两处**（`:376` 与 `:761`，正向 grep `Bayer` 在 src 的其余命中都是 RAW/DNG 的 CFA 语义，与本内核无关） | `ColorKernels.cs:151` `private const float BayerCenter = 7.5f / 16f;` ⇢ 两个使用点 `:376` `Bayer4[(ax&3)+((ay&3)<<2)] - BayerCenter`（RGBA 出口）与 `:761` 同式（rgb48→rgb8 出口）。7.5/16 = 0.46875 与 n/16 都是 2 的幂商 ⇒ float 下逐值精确，`t ∈ [−0.46875, +0.46875]`、均值 0（PowerShell 读数：`center=0.46875 maxT=0.46875`） | 未复现（已修） |
| **1b** 有没有"精确可表示码值 no-op"断言 | **有，三处**，且覆盖两条 8-bit 出口 | `contract.cs:759` C12（产品出口、257 整数倍输入、`Check(lifted == 0 && dropped == 0, …)` at `:798`；对照 `:804`；反向哨兵"半档输入必须展开 ≥2 个输出" `:818`）；`selftest.cs:107-142` 0c（`for (int pass = 0; pass < 2; pass++) // 0=解析式 1=表驱动`，`Check(worstDev == 0 && badPhases == 0)`）；`contract.cs:821` C13（RGBA 出口，`Check(changed == 0)`） | 未复现（已修，断言在位） |
| **2** 抖动曾加在线性域再查 OETF（趾段斜率 12.92 放大），2026-09-26 改码域；核两条 8-bit 出口是否同面孔、有无别处残留 | **两条出口同面孔**：都把 `t` 加在 8-bit 码域上，都经同一个 `EncodeTo8` | `ColorKernels.cs:779` `double v = code16 / 65535.0 * 255.0 + t8;`（`code16` 是 16-bit 码，`/65535*255` 落到 8-bit 码轴后才加 `t8`）；调用点 `:383-385`（RGBA 出口，注释 `:377-382` 点名"必须加在码域，旧写法 `r += t/255f` 改动 213/768"）与 `:763-765`（rgb48→rgb8）。`grep Dither src/FfmpegGui/Services` 只命中 `ColorKernels.cs:36/369/667` + 计划层 `ColorTransformPlan.cs:200/346/407/416/777` ⇒ **无第三处线性域残留** | 未复现（已修） |
| **2b** 哨兵坑：判"抖动是否还活着"必须用会产生分数码值的输入 | HEAD 的哨兵**已经是带彩色的跨色域输入**，坑已填 | `contract.cs:865-878`：`⚠ 输入必须**带彩色**：三条通道相等时，行和为 1 的原色矩阵保中性 ⇒ 输出仍落在整数码上` ⇢ `srcC[i*4+1] = (byte)((i*7+3) % 256)` 与 `Spec("bt709","iec61966-2-1","smpte432",…)`（跨色域）。C12 侧 `:805-818` 用**半档**（`257k+128`）造分数码值 | 未复现（旧坑已闭） |
| **3** `ColorKernels.cs:165` 的 `<summary>` 声称复用 `SimdPixelOps.FloatToSrgb8`，体内是标量循环 | **未修**，行号漂到 **190**（因上面新增的 BayerCenter/文档块把代码整体下推 25 行） | `ColorKernels.cs:190` `/// <summary>就地：线性 float RGBA → 编码值（可选钳位）。复用 <see cref="SimdPixelOps.FloatToSrgb8"/> 仅当目标为 sRGB。</summary>` ⇢ 方法体 `:191-219` 是 `for (int c = 0; c < 3; c++) rgba[i + c] = (float)cv.FromLinear(v);`，全函数**没有** `SimdPixelOps` 任何调用（该文件内 `SimdPixelOps` 只出现在 `:190` 与 `:225`） | 文档过期（HEAD 仍有） |
| **4** 矩阵内核只有标量（`SimdPixelOps.cs:253` 自述）；`FloatToSrgb8` 有 AVX2 但只在 GainMap 路径；AVX2 与标量只承诺 ±1 LSB；`AutoUseSimdBinaries` 零消费者 | **前半仍成立，后半已过期**：①矩阵内核纯标量（自述在 `:259`，函数 `:266-279`）；②AVX2 只有一个（`FloatToSrgb8Avx2` `:101`），消费者确实只有 GainMap；③±1 LSB 承诺仍在 `:73`；④开关从 0 消费者变 **2** | `SimdPixelOps.cs:259` `实测（2026-09）：3×3 乘加无 pow，JIT 已良好向量化 → 保持标量实现。`；`:266` `public static void ApplyPrimariesMatrix(Span<float> rgba, double[] m9)` 体内无 Intrinsic。AVX2 产品消费：`GainMapEncoder.cs:407 SimdPixelOps.FloatToSrgb8(rBuf[..chunk], bBuf[..chunk])`（Ultra HDR 底图 PNG）。开关消费者：`SimdKernelRouting.cs:62 FloatToSrgb8UsesSimd` 与 `:49 ResolvePixelKernelPath()` ← `SimdPixelOps.cs:81` 与 `PsnrCalculator.cs:192`；后者产品入口 `QualityAnalysisService.cs:250 CalculateMultiFramePsnr`。全仓唯一读 `AutoUseSimdBinaries` 的非 UI 处 = `SimdKernelRouting.cs:37`（正向 grep 命中：UI 2 + 持久化 2 + 路由 1 + 测试若干） | 文档过期（§4.4 的"零消费者"与"矩阵路径本来就只有标量所以无回退可撤"两条：前者已不成立，后者仍成立但对 PSNR 那条不适用） |
| **4b** AVX2 与标量"只承诺 ±1 LSB"是否被兑现 | 承诺的**来源**是取整模式不同：标量 `MathF.Round`（half-to-even），AVX2 `+0.5f → CVTT`（half-up）⇒ 只在恰 `.5` 处差 1 档，与承诺一致；LUT 近似误差另计 | `SimdPixelOps.cs:97` `return (byte)Math.Clamp(MathF.Round(s * 255f), 0, 255);` vs `:136-137` `var scaled = Avx.Add(Avx.Multiply(s, v255), vHalf); var rounded = Avx.ConvertToVector256Int32(scaled);`（注释 `:135` 写"与 MathF.Round 一致"，**严格说不一致**——half-up ≠ half-to-even，差异只在 tie 上） | 文档过期（次要，量级在承诺内） |
| **5** 16→10/12-bit 实由 swscale 做，而 `RawColorPipeline.cs` 注释说"只发生在变换那一次遍历" | **未修**，行号漂到 **169**，措辞一字未改；实现侧确实只有 8-bit 在变换段降位 | `RawColorPipeline.cs:169` `bool out8 = plan.OutBitDepth == 8;  // 出口位深由 Plan 真驱动（解码固定 rgb48le，降位只发生在变换那一次遍历）` ⇢ `ImageEncoderArgs.cs:855` `return hasAlpha ? "rgba64le" : "rgb48le"; // 10/12/16 使用 48le 作为通用输出` 与 `:889-890` `10 => "yuva420p10le", 12 => "yuva420p12le"` ⇒ 10/12 的取整在 ffmpeg/swscale 手里。`ColorStrategyPlanning.cs:375-376` 也自陈"本式的值域是 {8,16}…目标 10/12 必然被取到上档 16" | 文档过期（按项目记忆：**不得为它改实现**，只改注释） |
| **6** `LookupEncode` 的恒等 `clamp` 三元（旧 P3）是否消失、"不钳位不建表"的 throw 是否还在 | **两者都成立**：死参数消失、throw 三处在位（并升级成硬约束） | `ColorKernels.cs:414` `double e = dst.FromLinear(i / 65535.0);   // i/65535 ∈ [0,1]，无需再钳（旧的恒等三元已删）`；`:408-410` `if (!clamp) throw new NotSupportedException("BuildEncodeLut16 的表定义域是线性 [0,1]…")`；`:330-332`（`TransformRgba8` 先拒后算）；`:664-666`（`TransformRgb48leToRgb8` 把"注释守约"升级成 throw）。`LookupEncode` 现签名 `:424` 不带 clamp 参数 ✓ | 未复现（已修） |
| **6b** 同形残留：`DecodeToLinearInPlace(…, float[]? eotfTable8 = null)` 的参数在体内**从未被引用** | 存在（新发现，见 §3-N7） | `ColorKernels.cs:163` 签名带 `float[]? eotfTable8 = null`，方法体 `:165-187` 无一处使用；grep `eotfTable8` 在 src 命中 **1** 处（就是声明行） | 文档过期/死开关（非像素） |

---

## 3 新发现

### N1【最高优先】16-bit 主出口的表快路径判据漏两条：`EncodeScale` 与 `DstIsHlgScene`
**真缺陷-静默失效**

判据本体（`ColorKernels.cs:503-504`）：

```csharp
bool fast = eotf != null && oetf != null && Math.Abs(scale - 1.0) < 1e-12 && !toneOn
            && !spec.SrcIsHlgScene && !gamut;
```

快路径循环（`:512-527`）只做 `eF[s[·]] → 3×3 乘加 → EncodeLerp(oF, ·)`，**没有** `encScale`（`:568-571`）也没有
`dstHlg` 的逆向 OOTF（`:572-580`）—— 这两段只在**解析式**分支里。兄弟出口 2026-10-05 修的时候把条件补齐了
（`ColorKernels.cs:696`）：

```csharp
if (tbl && (scaleOn || toneOn || encScaleOn || srcHlg || dstHlg))
{ eF = null; oF = null; tbl = false; }
```

⇒ 8-bit 出口检查 5 个条件，16-bit 出口只检查 3 个。`git log -S "encScaleOn || srcHlg || dstHlg"` 落在
`123f2bb`（2026-10-05），该次提交**没碰 `fast` 判据**。

可达性链（每一环都是实读，不是推测）：
1. `ColorTransformPlan.cs:417` `ClampLinearBeforeEncode = true` —— **ToSpec 的两个分支都恒 true**；
   全 `src` 内没有任何一处把它置成 `false`（正向 grep：`ClampLinearBeforeEncode *=` 只命中 `ColorKernels.cs:38` 声明 + `ColorTransformPlan.cs:408/417`）。
2. `:438-439` `if (dstNits > 0 && |dstNits-refWhite|>1e-9) s.EncodeScale = refWhite / dstNits;` ⇒ 目标 PQ 时 `EncodeScale = 203/10000 = 0.0203 ≠ 1`。
3. `ColorIntentFactory.cs:336` `if (!dstSdr) it.HdrOutput = true;` ⇒ **HDR 目标是合法输出**（不是 Reject 档）；`ColorTransformPlan.cs:535` `["png"] = new() { … CanHdr = true, MaxBitDepth = 16, RgbNative = true }`。
4. `ColorStrategyPlanning.cs:378-379` + `ColorTransformPlan.cs:949`（`Needs16Bit ⇒ dst.Curve.Kind is Pq or Hlg`）⇒ `OutBitDepth = 16` ⇒ `RawColorPipeline.cs:169` `out8 = false` ⇒ 走 `TransformRgb48le(…, stSrc, stDst)`（`:1121/:1169`）。
5. `RawColorPipeline.cs:1087` `if (!spec.ClampLinearBeforeEncode || pixels < 256_000) return (null, null);` +
   `TransferCurve.cs:31/33`（PQ/HLG 的 `CurveKind` **不是** `Table`）⇒ **表照建、fast 照真**。

量级（**纸面算式，非实测**：PQ 定义式，PowerShell 求值）：
`L=1.000000 → E=1.000000 code16=65535` / `L=0.020300 → E=0.580689 code16=38055 code8=148`
⇒ 快路径把"1.0 = 203 nit 的 SDR 参考白"直接编成 PQ 满码（= 10000 nit），**nits 差 49×**，与
`ColorTransformPlan.cs:425-427` 自陈的旧缺陷症状**同一条**（"SDR→PQ 过曝 49×"）。`dstHlg` 同理缺一次
`ApplyInverse`（少乘/少除 `dstNits=1000` 那一档）。

为什么所有现有断言抓不到（两解释 + 区分读数）：
- **解释 A（我的判定）**：`fast` 判据漏项。独有的可检验预测 —— 同一张 SDR→PQ 素材，**像素数跨过 256,000 这道坎**
  结果会突变（`--bit-depth 16`，512×500 对 506×506）；或同一张 4 MP 图把 `--bit-depth` 从 16 改成 8 就"变对"
  （8-bit 出口有 `:696` 保护）。A 能解释这两件事，B 不能。
- **解释 B（我排除掉的）**：上游根本不建表 / HDR 目标被拦。已用第 2/3/5 环的实读正面否掉。
- `未测`：A 的两个读数都还没跑（`selftest.cs:36` 是 128×128、`contract.cs` 是 16×16 ⇒ 全都在阈值下）。
  `selftest.cs:81` 作者其实**知道**这道坎存在（"小帧 <256k 像素就是不建表"），但只把它用在 8-bit 出口的 0b 上。

修法方向（不动精度水位、只补判据，属"把实现提到声明水位"）：`fast` 追加 `&& !encScaleOn && !dstHlg`，
与 `:696` 同一副面孔；并补一条**跨过建表阈值**的 HDR 表/解析互验（否则这道坎下次还会咬人）。

### N2 `TransformRgba8` 静默吞掉五个算子（潜伏，产品零调用）
**真缺陷-静默失效（当前不可达）**

`:326-394` 全文只读 `SrcCurve(经 srcEotfTable) / Matrix / GamutMap / Dither / ClampLinearBeforeEncode / DstCurve`，
**从不读** `spec.Tone`、`spec.LinearScale`、`spec.EncodeScale`、`spec.SrcIsHlgScene`、`spec.DstIsHlgScene`，
且只有一条守卫（`:330` 的 `clamp==false` throw）。这正是 `123f2bb` 在兄弟出口上修掉的那类"既错又骗"。
区分读数（说明**不是**已被 `TransformRgb48leToRgb8` 取代）：`src` 内 `TransformRgba8` 命中 4 处 = 文档 `:149/:422` +
定义 `:326` + 异常文案 `:332` ⇒ 产品消费者 0；类注释 `:313` 却仍写"8-bit BGRA/RGBA 路径（表加速；**供预览与缩略图**）"
⇒ 文案与接线不符（预览/缩略图实际走别处，本轮未追，见 §4-U4）。
标签组合：真缺陷-静默失效（潜伏）+ 文档过期。要么按兄弟出口补齐五算子，要么显式 throw 未接线的算子
（`GamutMapActive` 那种 `IsIdentity` 反证已经证明"开着就绝不能被跳过"是可表达的）。

### N3 `LogGainToByteScalar` 截断取整 ⇒ 增益图系统性偏暗
**真缺陷-精度**

```csharp
SimdPixelOps.cs:224-225
    float clamped = Math.Clamp(logGain, 0f, maxLog2Gain);
    return (byte)(clamped / maxLog2Gain * 255f);          // float→byte 是**向零截断**
```

误差 ∈ [−1, 0] LSB、均值 −0.5 LSB，方向恒定（增益偏小 ⇒ HDR 重建偏暗）。同文件的其它 8-bit 出口都取整
（`:97 MathF.Round`、`ColorKernels.cs:780 Math.Round`）⇒ **同类量化两种口径**。产品可达：
`GainMapEncoder.cs:315 ComputeGainMapGray`（Ultra HDR 增益图）。
注意兄弟实现同式（`GainMapEncoder.cs:337` 也是 `(byte)(clamped/maxLog2Gain*255f)`）⇒ 这是"两路一起偏"，
不是分叉；**修必须同时改两处**，否则 `:203` 承诺的"误差 < 1 LSB"语义会变成新的两副面孔。
`GainMapEncoder.cs:373`（`RescaleGainMap` 盒式平均后同样截断）同形，但在我范围外，一并登记。

### N4 `RawGamutFit` 抽样步长与行宽成整数倍：注释前提为假、且偏差落在危险侧
**真缺陷-精度 + 文档过期**

```csharp
RawGamutFit.cs:174-175
  //   步长与行宽（6024）不成整数倍 ⇒ 每行落在不同列，不会踩到去马赛克的周期性。
  if (stride <= 0) stride = Math.Max(1, (int)(pixels / TargetSamples));   // TargetSamples = 1_000_000 (:54)
```

纸面算式（未跑产品）：`6024×4024 = 24,240,576 ⇒ stride = 24`，而 `6024 % 24 = 0`（PowerShell 读数 `0`）；
`6000×4000 = 24,000,000 ⇒ stride = 24`，`6000 % 24 = 0`（读数 `0`）。
⇒ 列相位恒为 `i mod w ∈ {0, 24, 48, …}` ⇒ **只覆盖 251/6024 个列**（行数倒是全遍历，因 251 为奇数）。
文件自己在 `:25-27` 写明"不得下采样，因为那会把结论推向『装在 bt709 内』（危险方向）"——列混叠正是同一方向的
残留风险：与列相位相关的越界（去马赛克伪色、CLPA/高光条纹）会被系统性漏掉。
`docs/RAW2TIF_QA_DEFECT_2026-10-03.md:28` 与 `JXL_RAW_16BIT_ADVISORY:41` 确认 6024×4024 是本仓真实 fixture 尺寸。
区分读数（两种解释并存）：**解释 A**=混叠导致 `FracOutOf709` 低估 ⇒ A 的可检验预测是"同一份图用与行宽互质的步长
重跑，`FracOutOf709` / `MaxNegExcursion` 会变"；**解释 B**=越界像素空间分布无关 ⇒ 预测是"两种步长得同一读数（±抽样噪声）"。
`未测`（`MeasureRgb48` 有公开 `stride` 形参，`ServiceProbe gamutfit` 可直接对拍，不必动实现）。
注意：现有那条"实测 Pentax 黄花 20.98%"读数就是在这个混叠格点上取的 ⇒ 改判据会让历史读数重排（属升级，非降级）。

### N5 `Exp2Approx` 的 gather 在精确 `.5` 相位上越界一格
**边界项（潜伏 OOB 读，未复现）**

表长：`SimdPixelOps.cs:60` `var t = new float[(1 << LutBits) + 1];` ⇒ **1025 项，有效下标 0..1024**。
索引：`:395-396` `idxF = (frac + 0.5f) * 1024f; idx = ConvertToVector256Int32WithTruncation(idxF);` ⇒
`frac == +0.5f` 时 `idxF = 1024.0 ⇒ idx = 1024`，随后 `:403 GatherVector256(p, idx + 1)` 读 **p[1025]**（越界 4 字节）。
`frac = +0.5` 的来源（代码自带的不变式在 `:394` 的注释里写着 `idxF ∈ [0, 1024)`，但那正是**没被保证**的端点）：
`:390 rounded = Avx.RoundToNearestInteger(x)` 是 half-to-even ⇒ `x = n ± 0.5` 的 tie 里，`rounded` 取偶数，
于是 `frac = +0.5` 恰好出现在 `x ∈ {2.5, 6.5, …} ∪ {−1.5, −5.5, −9.5, …}`。本调用点 `x = (1/2.4)·log2(v)` 且
`v ≤ 1 ⇒ x ≤ 0` ⇒ 相关族是 `x = −(4k+1.5)` ⇔ `v = 2^(−2.4(4k+1.5))` ≈ `0.0825 / 2.18e-4 / 2.9e-7`：
第二条起就落在 `v ≤ 0.0031308` 的线性段（`:132-133` 的 blend 会改取 `lin`，越界读的**值**用不上但**地址**照读），
只有第一条 `v ≈ 0.0825`（中间调偏暗，像素量大）是"真会用到该结果"的相位。
而 `x` 要**逐位**等于 `-1.5` 需要 `log2Approx(v) = -3.6` 精确成立（`-3.6` 非二进制有理数，`exp + LUT 线性插值`
给不出）⇒ 命中概率 ~0，故标"未复现"。
后果分级：即使命中，`frac2 = idxF - idx = 0` ⇒ `fPart = v0 + (v1 - v0)·0 = v0`（`Exp2Lut[1024] = 2^0.5`，值正确），
**除非邻字位模式是 NaN/Inf**（`float[]` 相邻元素/对象头，通常不是）⇒ 属"低危但确实越界"。
对照：`Log2Approx`（`:369-370`）由构造安全 —— `m ∈ [1,2)` ⇒ `idxF ∈ [0,1024)` ⇒ `idx ≤ 1023`、`idx+1 ≤ 1024` ✓。
最小修法是 `Exp2Lut` 多留 1 项，或把 `idx` 上限钳到 `1023`（不改任何数值口径）。

### N6 舍入方式与标度因子的全出口清点（要求项，逐条实读）
**读数（一致的部分明确说一致）**：

| 出口/步骤 | 文件:行 | 取整方式 | 标度因子 | 备注 |
|---|---|---|---|---|
| `BuildEncodeLut16` | `ColorKernels.cs:415` | `Math.Round(double)` ⇒ **half-to-even** | `i/65535` → `×255` | 表本身无损（`:399` 论证成立） |
| `LookupEncode`（表→8bit） | `:427` | `(int)(v*65535f + 0.5f)` ⇒ **half-up，且在 float 里** | `×65535` | `v` 已钳 [0,1] ⇒ `:428` 的 `idx<0?:>65535?` 是**不可达的死守卫**（无害）。float 乘积误差 ≈ 65535·6e-8 ≈ 0.004 码 ⇒ 只在离 `.5` 边界 <0.004 时翻 1 档索引 ⇒ 8-bit 域最坏 1 LSB，**未测**（可量化） |
| `EncodeLerp`（表内插值） | `:828` | `(int)(delta*fr + 0.5f)` ⇒ **half-up** | — | 边界证明在位：`:824-825` 先 `idxF<=0 / >=65535` 早退 ⇒ `i0+1 ≤ 65535` **不越界** ✓ |
| `ToU16`（16-bit 主出口解析式） | `:806` | `(long)Math.Round(double)` ⇒ half-to-even | `×65535` | 与表路径 `EncodeLerp` 的 tie 口径不同 ⇒ 表/解析差 ≤1/65535，已被 `selftest.cs:76` 的 `lsb <= (kink?24:1)` 容差圈住 |
| `EncodeTo8`（两条 8-bit 出口共用） | `:777-780` | 解析分支 `Math.Round(e*65535.0)`；表分支 `EncodeLerp`；最后 `(long)Math.Round(v)` ⇒ half-to-even | `×65535` 后 `/65535×255` | **旧"漏乘 65535 致全黑"的分支已覆盖**：`:778` 两分支都产出 16-bit 码轴 ⇒ 记忆里的这条在 HEAD 无残留；`selftest.cs:79-98` 0b 显式钉住"表 ≡ 解析" |
| `ClampIdx` | `:809-813` | half-up | `×65535` | **整个方法在 src 内零调用**（正向 grep：只有定义）⇒ 死代码 |
| `FloatToSrgb8Scalar` | `SimdPixelOps.cs:97` | `MathF.Round` ⇒ half-to-even | `×255` | |
| `FloatToSrgb8Avx2` | `:136-137` | `+0.5f → CVTT` ⇒ half-up | `×255` | 与标量差仅在 tie（≤1 LSB，在 `:73` 承诺内） |
| `LogGainToByteScalar` | `:225` | **截断** | `×255` | 见 N3 |
| `MeasurePeakLinearRgb48` | `:615/:624-626` | 无取整（max） | `/65535` | 表/解析两支同值：`:459` 与 `:624` 都是 `curve.ToLinear(i/65535.0)` 同一构造 ✓ |
| `RawGamutFit` | `RawGamutFit.cs:183-186,220` | Y 直方图 `(int)Math.Round(·)` | `Full = 65535.0`（`:56`）| 与管线矩阵同源（`:69` `LoadPair`），未用 1023/4095 |

- **通道间一致性**：每个内核对 r/g/b 调的都是**同一个函数/同一张表**（`:389-391`、`:523-525`、`:585-587`、`:763-765`），
  alpha 一律原样透传（`:392`、`:786`、`SimdPixelOps.cs:176`）⇒ **没有发现逐通道不同的取整口径**。
- **10/12-bit 标度因子**：范围四文件内**不存在** 10/12-bit 的标度分支。正向 grep `1023|4095` 唯一命中是
  `SimdPixelOps.cs:368` 的 LUT 索引注释（`1023.9→1024 越界`，与位深无关）⇒ 没有"某档漏乘"的余地，
  也没有该档的实现可审（与 §2-旧主张 5 的"降位归 swscale"一致）。
- **钳位时机**（早钳 vs 晚钳）：解析式路径顺序为 `GMO → EncodeScale → dstHlg 逆 OOTF → clamp → OETF`
  （`:566-587`），8-bit 出口同序（`:740-765`，2026-10-05 补齐），参考实现同序（`:848-874`）⇒ **晚钳，无信息丢失**。
  唯一"早钳"发生在 `ClampLinearBeforeEncode=false` 被拒的三条表路径上（`:330/:408/:664` 全部 fail-loud）✓。
  `EncodeLerp` 在表两端饱和（`:824-825`）等价于对线性值钳位 ⇒ 与 `clamp=true` 语义一致 ✓。
- **f32 累加顺序**：矩阵三行都是 `m0*r + m1*g + m2*b` 的**同序**左结合（`:362-364`、`:518-520`、`:559-561`、
  `SimdPixelOps.cs:275-277`）⇒ 内核之间、内核与 SIMD 版之间不会因结合序不同而分叉；与 `TransformPixelReference`
  的 `double` 版（`:843-845`）比是 **float vs double** 的差（系数 `:339-341` 也降到 float），
  量级 ~1e-7 相对误差（远小于 0.03125 LSB 的居中余量），但参考实现的"double 口径"名字上没写明 ⇒ 属口径提示，不算缺陷。

### N7 `DecodeToLinearInPlace` 的 `eotfTable8` 是从未接线的死开关
**文档过期/死开关（非像素）**

```csharp
ColorKernels.cs:163
public static void DecodeToLinearInPlace(Span<float> rgba, in TransformSpec spec, float[]? eotfTable8 = null)
```
体内 `:165-187` 无任何引用；调用点只有 `:306`（两参）。grep `eotfTable8` src 命中 1 = 声明本身。
⇒ 与旧 P3 那条"恒等 clamp 参数"完全同形：**参数承诺了一条不存在的 8-bit 源表快路径**，
留着迟早有人在上面挂错期待。删参数（或实现）都不涉及像素。

### N8 边界与尾元素清点（要求项）
- **向量化尾巴**：`FloatToSrgb8Avx2` 有标量尾巴（`:149-150` `for (; i < src.Length; i++) dst[i] = FloatToSrgb8Scalar(...)`）✓。
- **4 倍数假设**：`for (i = 0; i + 4 <= n; i += 4)` 出现在 `ColorKernels.cs:173/198/296` 与
  `SimdPixelOps.cs:167/209/240/272/330/341` ⇒ 若长度非 4 的倍数，**尾部不足一像素的字节被静默留白**（不抛）。
  RGBA 契约下成立；`TransformRgba8` 还叠了一条 `int n = Math.Min(src.Length, dst.Length);`（`:333`）⇒
  缓冲给短时不报错、少处理若干像素（`dst` 尾部是池化数组的**旧内容**，不是 0）。标签：真缺陷-静默失效（低可达：
  该内核产品零调用）。建议：`src.Length`/`dst.Length` 与 `width*height*4` 三者对不上时抛，与 `:664` 同一口径。
- **3 通道假设 vs alpha**：rgb48le/rgb24 两条出口按 `o = i*3`、`p = i*3` 走（`:514-515`、`:710`），
  alpha 走另一条 ffmpeg 链（`RawColorPipeline.cs:1052-1076` 的 `alphaextract`/`alphamerge`），
  **两套通道数没有互相打破**；范围文件内无 `stackalloc`（正向 grep 0 命中），唯一的 `stackalloc` 在
  `GainMapEncoder.cs:396-397`（`float[512]`/`byte[512]` 对 `chunk ≤ 128`）✓ 有余量。
- **原地读写序**：`PlanarGbr16ToRgbPacked16`/`RgbPacked16ToPlanarGbr16`（`:784-803`）是 3↔1 通道布局互换，
  注释 `:783` 明说"同一缓冲内可原地不行，需两缓冲"，且 src 内零调用 ⇒ 契约诚实，仅登记为**未接线能力**。
- **整数溢出**：`RawGamutFit.cs:182/227` `int o = (int)(i * 6);` 中 `i` 是 `long` ⇒ 计算本身不溢出，
  **降到 int 时**要求 `pixels*6 ≤ 2^31`（即 ≤ 357.9 MP）。`.NET Core` 允许 byte[] 超过 2 GB ⇒ 理论上可达，
  失败模式是 `IndexOutOfRange`（响）而不是错位（静）⇒ 登记为边界项，优先级低。
  `RawColorPipeline.cs:1099` 用的是 `int len = checked((int)bytes);`（响）✓ 同族两处口径不同（一 `checked` 一不）。

### N9 性能项（不构成缺陷，登记）
- `GainMapJpegCodec.GbrPlanarToRgba:60-77` 为一次交错化分配 **4 个 `pixelCount`  sized 数组**
  （3 张平面 + 1 张 RGBA），且平面数组未走 `BufferPool`（同仓别处走了）⇒ 4K GainMap 一次 ~100 MB 短生命周期分配。
  可直接用 `MemoryMarshal.Cast<byte,float>` + 三平面偏移读写，省 3 张拷贝。
- 矩阵内核纯标量（N-旧主张 4）在 `ApplyPrimariesMatrix`（`SimdPixelOps.cs:266`）上仍成立，注释 `:259` 的
  "JIT 已良好向量化"是**断言不是读数**（未测）。同一函数在 `:225` 被 `ApplyMatrix` 复用 ✓ 无第二份系数逻辑。

### N10 `GamutMapScalar` 的两条几何/数值边界（要求项：凸包与保亮度侧）
- **保哪一侧**：`ColorKernels.cs:278-286` 的 `grayMix` 把越界像素向 `luminance`（目标色域的同亮度灰）混合，
  混合权重由 `PreserveSaturation=0.3` 定 ⇒ **默认保亮度**侧（`:59-62` 的文档与 libjxl 内部 CMS 取值一致），
  越界幅度信息**在钳位之前**使用（`:566`、`:740`）✓ 没有"直接削顶"。
- **BT.2020 ⊃ P3 的包含关系**：**不在本文件**（内核只消费 `DstLuminances`，不知道谁包含谁）。正向命中：
  几何凸包判据在 `ColorTransformPlan.cs:956-965` `GamutContainedIn ⇒ ColorSpaceDescriptor.ContainsGamut(src, dst)`，
  旧的"矩阵元素全非负"判据已被显式废弃（`:953` 注释）；`RawGamutFit` 只做 bt2020→bt709 的实测，
  **不涉及 P3**（`:69/:77` 只装载 `("bt2020","bt709")` 与反向）⇒ 本范围内**没有把严格包含关系误用成削顶的地方**。
- **凸包外投射是否保亮度**：`luminance` 用的是 **D50 适应后**的 Y 行（`ColorTransformPlan.cs:484-489` 自陈与
  libjxl 的 D65 系数逐项差 ~1e-2、行和仍为 1）。no-op 性质不受影响（凸组合仍成立）⇒
  但 `ColorKernels.cs:233-234` 的"**逐行移植** libjxl（不自行发明算法）"读起来像逐值复刻 ⇒
  标签：**文档过期**（算法同源、权重不同；内核注释未提这点，计划层注释提了）。
- NaN 输入：`GamutMapParams.IsValid`（`:116`）只查系数有限，不查像素。float 解码链若给出 NaN ⇒
  `luminance=NaN` ⇒ `Math.Clamp(NaN,0,1)=NaN` ⇒ 一路 NaN 到 `(long)Math.Round(NaN)=0`（变黑，不抛）⇒
  **未判定（需一份能造出 NaN 线性像素的 gbrpf32le 夹具）**。

---

## 4 未证边界（我没能给出读数的地方，以及各自缺什么证据）

- **U1｜N1 的端到端量级**：缺一次"同一 SDR→PQ 素材跨 256,000 像素阈值"的对拍（预测：小图白 = 38055、
  大图白 = 65535）。需要：`--bit-depth 16 -f png` + 显式 HDR 目标，两张尺寸，读回 TIFF/PNG 的 16-bit 白样本。
  没跑 ⇒ 全部 49× 数字都只是按 PQ 定义式算出来的，不是实测。
- **U2｜N4 的混叠后果**：缺"同图 stride=24 与 stride=25(互质)"的 `MeasureRgb48` 对拍（该函数是公开纯函数，
  `ServiceProbe gamutfit` 就能喂）⇒ 现在只能说"注释前提为假"，不能说"结论被翻转过"。
- **U3｜两条 8-bit 出口的 origin 约定不同**：`:374-375` 用 `(originX + px%width, originY + px/width)`，
  `:760` 用 `(originIndex+i)` 的线性索引拆 x/y。两种语义各自正确，但**互不可换**；缺一条把同一 tile 喂给
  两个内核并比相位的断言（现在 C12/C13 各测各的）。⇒ 未判定（需接线证据：若哪天 `TransformRgba8` 成活路，
  按 `:760` 的习惯传 `rowStart*w` 就错）。
- **U4｜"预览与缩略图"到底走哪条**：`TransformRgba8` 的产品消费者为 0 已正面确认，但**预览/缩略图的色彩变换
  实现位置我没追**（可能在 `ImageGeometry`/ffmpeg 侧）⇒ N2 的"文案过期"半条已定，"该怎么修"半条需那条 grep。
- **U5｜`LookupEncode` 的 float 索引误差是否会造出 8-bit 1 LSB**（`:427`，N6 表内标注"未测"）：
  需要一次全 65536 索引的表/解析逐码对拍（纯函数，几十毫秒）。
- **U6｜AVX2 与标量在 tie 上的 1 LSB 差**是否被 `tests/GainMapTestHost` 的 D28 断言实际覆盖过
  （文档 `SimdPixelOps.cs:23` 声称"由 D28 断言验证"，我未跑该宿主）⇒ 未判定（需 D28 的当前读数）。
- **U7｜`RawGamutFit.LoadPair:84-100` 的 `catch { }` 吞掉注册表异常后静默改用后备常数**，
  而 `Result` 里**没有字段记录用的是哪一副矩阵** ⇒ 若注册表在某条路径抛异常，"判据与产物脱钩 2.7e-4"这件事
  在日志里不可见。需要：让探针断言 `M2020To709` 来自注册表（`:64-65` 声称已钉，我未读那条探针代码）。
- **U8｜16→8 之外的降位档（10/12）在 HEAD 没有任何引擎内实现**，所以本 lane 对"swscale 取整无系统偏置"这条
  项目记忆**既不能证实也不能证伪**（按指令也不许为此改实现）；只登记了 §2-旧主张 5 的注释债。
- **U9｜NaN/Inf 线性像素的落点**（N10 末条）与 `SimdPixelOps` 各内核的 `eps`（`:172` 的 `1e-6f`、`:208` 的
  `0.001f`、`:223` 的 `maxLog2Gain<=0→1f`）在极端输入下的行为，都缺一份合成 float 夹具（现在全是整数码测试）。
