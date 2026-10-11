# A2 · 传递函数 / OOTF / 色调映射出口标签 —— 只读审查报告

HEAD = c18dc4a（v1.6.0-beta7），工作树干净，本轮未修改任何源文件（唯一写入 = 本文件 + `tests/output/pipeline-audit-2026-10-10/tmp/A2/` 下的取证夹具，该目录被 `.gitignore:78 output/` 忽略）。

权威参照：`C:\PLAN\VQBench\PLAN\ffmpeg-full\ffmpeg.exe` = git-2026-09-24-c0e8b139fd，`--enable-libzimg --enable-libplacebo --enable-lcms2`，含 zscale/iccgen/tonemap（实测 `-filters` 命中）。
⚠ 取证卫生：仓内 `publish/PLAN/ffmpeg-full/ffmpeg.exe` 与上面那份**不是同一个二进制**（MD5 `1918860c…` vs `fac083f6…`），而既有脚本 `_probe-zimg-transfer-table.ps1:3` 用的是仓内那份。本轮全部读数出自权威那份。

---

## 0 一页结论

| # | 结论 | 标签 | 关键实测读数 |
|---|---|---|---|
| 1 | zimg 把 `t=bt709` 实现成纯 γ(1/2.4) —— **成立** | 复验通过 | 对纯幂 max 10.1 LSB@16 / RMS 5.95e-5；对 BT.709-6 定义式 max 6986（256 点采样 6978） |
| 2 | 引擎默认格已把像素提到标签水位；**传统链仍有 5 格把 `t=bt709` 写进命令行又写 `-color_trc bt709`** | 真缺陷-标准 | 消费者按定义式解码 → 光域最大差 4748 LSB@16 = **7.25% 满量程**（297 LSB@12） |
| 3 | `--color-709-curve` 只管**引擎 H1**，传统链一格都管不到 | 真缺陷-标准（管辖面） | `Color709Curve` 消费点全仓 1 处（`ColorIntentFactory.cs:322`），`Decision.cs` 零命中 |
| 4 | 记忆更正成立：γ2.4 现在是显式开关，且**逐格点名不静默** | 复验通过 | `TransferCurve.cs:132` CicpToken=null、`ColorTransformPlan.cs:750-753`、`:866-878` |
| 5 | `ColorIntent.cs:85` 的"拟合 RMS 7e-5"数字可核，**旧文档"只在中高调成立"是反的** | 文档过期(轻) / 旧主张不成立 | 趾段(L≤0.02) RMS 4.5e-6 最小，最差在 L>0.02（1.5e-4） |
| 6 | PQ(ST 2084) 常数逐个正确，且与 zimg 独立实现吻合到 6 位小数 | 复验通过 | 工程式 100nit→0.508078，zimg 实测 0.508080；`PqOetf(1.0)=1.000000000` |
| 7 | HLG a/b/c ✓ BT.2100-2 Table 5；但 `CurveKind.Hlg` 文档"系统级 / 1.0=参考白"两处失真 | 文档过期 | 实测 `HlgOetf(1.0)=1.000000`（含 OOTF 才是系统级）；参考白在码 0.75（实测 0.2648→0.749879） |
| 8 | **`ColorTransformPlan.cs:812` 说"zscale 不做 HLG OOTF"被实测推翻** | 真缺陷-标准（依据性文字） | zimg `t=arib-std-b67` @linear1.0 = 0.629618 = OOTF(α=1000,γ=1.2)∘OETF 预测值；纯逆 OETF 应 0.99984 |
| 9 | zimg 的 HDR 线性域归一是 **1.0 ↔ 100 cd/m²**，引擎是 203 ⇒ 传统链 SDR→HDR 与引擎差 2.03× | 真缺陷-精度（跨管线） | `sRGB 1.0 →zscale→ PQ 0.508080`（=100 nit）；引擎 EncodeScale=203/10000 → 0.580689（=203 nit） |
| 10 | `ForCicpTransfer("smpte170m") => Gamma22` 与标准不符；zimg 又把它做成 γ2.4 | 真缺陷-标准 | γ2.2 vs BT.601/709 分段 EOTF：max 2752 LSB@16 = **4.20% FS**；zimg 实测 0.749462（γ2.4 预测 0.75038 / 定义式 0.70683） |
| 11 | 趾段↔幂段衔接让 BT.709 **往返不闭合**（注释"实测差 ≤10 LSB"不成立） | 真缺陷-精度(小) | maxRT = **16.26 LSB@16**（1.02 LSB@12）@code 5338；跨界 EOTF 下降 3.39 LSB@16；sRGB/ProPhoto/Bt709Zimg = 0.00 |
| 12 | `ToneMapping.cs:31-34` 仍宣称 BT.2390 含 EETF —— 与裁决点自相矛盾 | 文档过期 | 裁决面已对：`ColorStrategyPlanning.cs:329-333`、`CliParser.cs:827` |
| 13 | 「ICC 的 TRC 必须派生自像素实际编码的 trc」：**源侧成立、出口侧在 zimg 格被有意豁免且日志仍宣称一致** | 真缺陷-标准 | `ColorTransformPlan.cs:1018 p.IccTrc = p.OutTransfer`（声明值）；`QueueProcessor.cs:1615` 断言"与像素一致"；iccgen 实测 para3 = 定义式(2.222229,0.909668,0.090332,0.222229,0.081238) |
| 14 | 「zscale 不支持 smpte428」成立；代码只在**用户手填 token** 一条路上试图交出去（响亮失败） | 能力缺口(已fail-closed) | 实测 `t=smpte428` / `tin=smpte428` → "Undefined constant…" exit=-22 零产物；iccgen 的 `color_trc=smpte428` 同样拒 |
| 15 | `--color-hdr-peak` 承诺能设 HLG 的 L_W，**实际接不到** | 真缺陷-静默失效 | `ColorHdrPeakNits` 全仓消费点 = `ColorIntentFactory.cs:413-415` 一处；`nitsOfLinearOne:` 具名实参 0 个调用点 ⇒ α 恒 1000、γ 恒 1.2 |
| 16 | Hable/Mobius 与 ffmpeg 上游逐字一致、mobius 默认拐点 0.3 **得到实测背书**；但 `hable:param=0.5` 是死参数 | 文档过期(轻) | 本构建 tonemap 算子表：none/linear/gamma/clip/reinhard/hable/mobius（**无 bt2446a**）；hable 空/0.5/0.9 输出全等 8063；mobius 默认=param0.3→30157，param0.4→32182 |
| 17 | Method A 常数自洽性全部通过；只有"精确落到 1000.0 nits"用词过强 | 文档过期(轻) | 实测 255^E(1.246567) = **999.80 nits**；knee 0.7399/0.9909 处差 0.0；Table4 在 Y''=70 连续（174.09/174.08） |

一句话：**引擎侧（默认路径）的传递函数数学站得住**；失分集中在 (a) 传统链 5 个 `t=bt709` 格——标签/ICC 说定义式、像素是 γ2.4、且开关管不到；(b) 两句被实测推翻的"依据性注释"（HLG OOTF、≤10 LSB）；(c) zimg 与引擎的 HDR 白点归一不同源（100 vs 203）；(d) `smpte170m→γ2.2` 这一条对标准不成立。

---

## 1 方法（含被推翻的假设）

**取证手段**
1. 曲线对拍：256×256 灰斜坡（每像素 = 线性码值 i，rgb48le）→ `zscale=pin=bt709:tin=<A>:min=gbr:rin=pc:p=bt709:t=<B>:m=bt709:r=pc` → 回读 R 通道 65536 点，与"逐字转录自标准/自本仓 C# 常数"的参照曲线比 max/mean/RMS（`a2curves.ps1`、`a2icc.ps1`、`a2numeric2.ps1`、`a2chain.py`）。夹具自检：字节数 393216 == 65536×3×2、`px0.R=0 px1.R=1 px65535.R=65535` 全过。
2. ICC 反解：`iccgen=color_trc=X:force=1` 写 TIFF → 手工定位 `acsp` + tag 表 → 读 rTRC 的 `para` functionType 与 s15Fixed16 参数（同脚本内）。
3. 产品算子的"逐字对齐"主张：取上游 `libavfilter/vf_tonemap.c` 原文（hable/mobius 函数体）逐式比对，再用本构建实测默认值敏感性。
4. 静态面：全仓 grep 消费点（开关管辖面、峰值单位、ICC 出口、fail-closed 拦截点），每条判"缺席"前都做了正向命中。

**被推翻的假设（我自己的，先记账）**
- ✗ "BT.709 的 `d=0.08125` 是无出处自造值"。实测 `iccgen` 为 bt709 写出的 para3 **d=0.081238**，与 0.08125 差 1.2e-5 ⇒ 它跟的是 lcms2 的取值，不是拍脑袋；真正的问题是**注释没写这个出处**，而同文件 240M 的 d 用的是另一套推导法（斜率×断点 4×0.0228=0.0912）⇒ 两条曲线的 d 不同源（见 N2）。
- ✗ "zimg 的 HLG 只做逆系统 γ、不做 OOTF"（仓内 `:812` 也是这么说的）。实测 zimg **含 OOTF**，见结论表第 8 行。
- ✗ "趾段偏差 ~21 LSB@16、8bit 下 0.26 LSB"。实测前向台阶 16.2 LSB@16 = 0.06 LSB@8；**0.26 是 10-bit 的数**（N2）。
- ✗ "旧文档 mean=1921 能通过"。四种采样轴（65536 线性均匀 / 256 线性均匀 / 256 码均匀 / 1024 线性均匀）全部落在 3041–4172，**取不到 1921**（max=6978 倒是能在 256 点线性均匀下精确复现）。
- ✗ 复合链一次跑废：我用 `min=bt709:m=bt2020nc` 且漏 `rin=pc` 去模拟 D1 链，实际走的是"RGB→YUV 矩阵 + 量程换算"支，且自建 709→2020 矩阵也有错 ⇒ 那组读数（max 15780/20458）**作废**，登记进 §4。作废后重做的"恒等三明治"检验才是有效读数：`tin=bt709:t=bt709`（不动 primaries）输出码值 == 输入码值（抽样点 33767→33767、16137→16137）⇒ **γ2.4 的解码/编码自抵消**，这直接改变第 2 行的严重性判法（见 N14 的"两解释区分读数"）。

---

## 2 旧主张复验表

### 主张 1（doc §3 + 记忆更正）：γ2.4 落点与开关管辖面

**1-a zimg 侧**（复验成立）
`TransferCurve.cs:116-118` 原文：
> `编码差在中间调最大约 7231/65535 ≈ 28.1 LSB@8bit（实测：zscale=t=bt709 的像素与 ITU-R BT.709-6 定义式最大差 7231 LSB@16bit，与纯 γ(1/2.4) 只差 8bit 量化地板 0.0039）`

实测（权威 ffmpeg，65536 点线性均匀斜坡）：

| 对比 | max | mean | 备注 |
|---|---|---|---|
| zimg `t=bt709` vs 纯 pow(1/2.4) | 10.1 LSB@16 | 3.2 | RMS **5.95e-5** |
| zimg `t=bt709` vs BT.709-6 定义式 | **6986**（256 点：6978） | 3059（256 点：3041；码均匀：4165） | argmax 落在线性码 1110（趾段） |
| zimg `tin=bt709` vs 定义式 EOTF（**光域**） | **4748 LSB@16 = 7.25% FS** | 3059 | 与 `pow2.4 vs 定义式` 手算完全一致 |

⇒ 注释里的 **7231 未复现**（本构建 6986；doc 的 6978 在 256 点采样下精确复现）；doc 的 **mean 1921 未复现**（口径不明，见 §4）。"≈28.1 LSB@8bit"相应应为 **27.2**。标签：**未复现/文档过期**（数值层面），主张本身（zimg=纯幂）**成立**。

**1-b 引擎路径落点**（复验成立，且水位已对齐）
`ColorTransformPlan.cs:733-736`：
```csharp
// 口径黑名单（实测）：zimg 对 bt709=纯 γ2.4，与标准定义式分歧；bt470bg/smpte240m 未验证
bool zimgDivergent = ZimgDivergentTransfer(dst.TransferToken);
if (zimgDivergent && policy.Transfer709 == Transfer709Curve.Std) h2Possible = false;
```
`ColorTransformPlan.cs:750-753`（开关落到像素）：
```csharp
if (policy.Transfer709 == Transfer709Curve.Zimg)
{
    var zimgExit = ZimgExitCurve(dst.TransferToken);
    if (zimgExit != null) { p.DstCurve = zimgExit.Value; zimgExitApplied = true; }
}
```
`ZimgExitCurve`（`:843-844`）只对 `bt709` 有背书：
```csharp
internal static TransferCurve? ZimgExitCurve(string? trcToken)
    => trcToken == "bt709" ? TransferCurve.Bt709Zimg : null;
```
**但 H2 已于 2026-10-05 删除执行体**（`:799-817`），⇒ HEAD 上"引擎路径"恒走 H1：Std=定义式像素（`TransferCurve.Bt709`），Zimg=γ2.4 像素（`Bt709Zimg`），**标签/ICC 一律按目标描述符**（`:748-749` 明写"出口标注一个字都不动"）。开关管辖格（`:866-878` 逐格出声）：
- 兑现：`act=Map` 且出口 trc==bt709（H1）；
- 不兑现且点名：GainMap 出口（`:869-870`）、非 Map（`:876`）、bt470bg/smpte240m（`:877`）、其余 trc（`:878`）。

**1-c 传统 tonemap / zscale 链落点（残留的"标签对标准不成立"格）**
`FfmpegCommandBuilder.Decision.cs:298`（默认格已修，这是 #26② 的成果）：
```csharp
var tmDstT = "iec61966-2-1";
```
实测支持这个选择的正确性：zimg `t=iec61966-2-1` vs IEC 61966-2-1 定义式 max **10.8 LSB@16**（分段实现存在），而 `t=bt709` max 6986 ⇒ "选 sRGB 而不是 bt709"在传统链里是唯一能让标签兑现的 SDR 出口。

仍有 5 格把 `bt709` 交给 zimg：
| 格 | 位置 | 触发条件 | 是否受 `--color-709-curve` 管辖 |
|---|---|---|---|
| tonemap + 显式目标 trc 缺失兜底 | `Decision.cs:307 tmDstT = capT ?? target.trc ?? "bt709"` | `--color-space "BT.709"`（UI 下拉有这项：`MainWindow.xaml.cs:2240 ColorSpaceCombo.Items.Add("BT.709")`） | ✗ |
| tonemap + 高级 trc | `Decision.cs:313 tmDstT = capT ?? options.ColorTrc!` | `--color-trc bt709` | ✗ |
| 出口标签同步 | `Decision.cs:332 outP = tmDstP; outT = tmDstT;` | 同上 ⇒ `-color_trc bt709`/PNG cICP=1 | ✗ |
| SDR→SDR / SDR→HDR 显式目标 zscale | `Decision.cs:379 …t={dstT}…` | 目标 spec.OutTrc=bt709 的一切命名空间 | ✗ |
| **D1 超广归一（默认、用户无需选择）** | `Decision.cs:409-410` + `ColorSpaceRegistry.cs:227`（`outT = hdr ? … : spec.OutTrc`） | Adobe RGB/ProPhoto/ColorMatch/DCI-P3/SDR-BT.2020 源 + 宽容器 + `--color-space auto` | ✗ |
| 源侧线性化把 bt709 当"可精确表达" | `ColorSpaceRegistry.cs:347,359`（`ExactTransferName`：`"bt709" or "709" or "bt2020-10" or "bt2020-12" => "bt709"`） | 同上（这条链的 `tin=`） | ✗ |

`ColorSpaceRegistry.cs:65-66`（"BT.709" 目标三元组）：
```csharp
Add(new Spec { Key = "BT.709", Kind = GamutKind.Standard,
    OutPrimaries = "bt709", OutTrc = "bt709", OutMatrix = "bt709",
    IccgenPrimaries = "bt709", IccgenTrc = "bt709", CjxlColorSpace = "sRGB",
```
⇒ **像素 γ2.4 + `-color_trc bt709`（CICP 1，语义=定义式）+ iccgen 出的 ICC TRC=定义式**（实测 para3 参数见主张 4）。消费者按标准解码 ⇒ 光域最大 7.25% FS 偏差。标签：**真缺陷-标准**（不是"只改标签"能收口，CICP 无纯 γ2.4 编号；唯一修法仍是把像素提到水位）。

**区分读数（两解释并存）**：γ2.4 的 decode→encode 三明治**自抵消**（实测恒等链 out==in），所以这条缺陷的实际暴露面是
 (i) 外部消费者用定义式 EOTF 解码（7.25% FS），以及
 (ii) 链内的线性域消费者（primaries 矩阵、GMO 判定、tonemap）拿到错线性光；
而**不是**"与历史产物不一致"——对历史产物而言 γ2.4 才是同形的。⇒ 修法不能选"保持像素、改标签"，也不能选"什么都不改"，只能提水位或撤标签。

**1-d 管辖面判据**：`Color709Curve` 全仓消费点 = `CliParser.cs:791`（解析）→ `FfmpegOptions.cs:549`（存储）→ `ColorIntentFactory.cs:322-323`（引擎意图装配）→ `ColorTransformPlan`/`ColorIntent.ToPolicy`。**`FfmpegCommandBuilder.Decision.cs` 零命中** ⇒ doc §3 那句"不受 `--color-709-curve` 管辖"在 HEAD **仍然成立**，且管不到的不止 tonemap 一格。

### 主张 2：`ColorIntent.cs:85` "拟合 RMS 7e-5" 的适用范围

`ColorIntent.cs:85-86`：
```csharp
/// BT.709 编码口径（实测分歧）：zscale 把 `t=bt709` 实现为纯 γ1/2.4（拟合 RMS 7e-5），
/// 而 BT.709-6 定义式（α/β）与同构建 lcms2 生成的 BT.709 ICC 一致。
```
实测：zimg vs 纯 pow(1/2.4) 的 **RMS = 5.95e-5**（全 65536 点）、趾段(L≤0.02) RMS **4.5e-6**、L>0.02 RMS 6.0e-5（max 1.5e-4）。⇒
- 数字**可核**，语义是"γ1/2.4 拟合 zimg 曲线的残差"，不是"zimg 与标准的距离"；
- doc §3 的批评"**只在**中高调成立、没说明趾段"方向**反了**——趾段拟合得最好；
- 仍存在的瑕疵：这句没写"谁拟合谁"。标签：**文档过期(轻)**；旧主张本身：**不成立**。
- 附带复验 doc §3 对标准的转述："BT.709-6 定义式（α=1.0993, β=0.08178）"：建议书正文是 **α=1.099、β=0.099、趾段 4.5、L 断点 0.0181**（我实测 lcms2 para3 反解出 α=1/0.909668=**1.09930**、β=0.090332/0.909668=**0.09930**，而 0.08178≈幂支在 L=0.0181 处的**编码域值** 1.0993·0.0181^0.45−0.0993=0.08175）⇒ doc 把断点误标成 β。标签：**文档过期**。

### 主张 3：7 参数规范形常数逐个对标准核

`TransferCurve.cs:11-12` 的规范形定义：`linear = c·x+f (|x|<d)` / `(a·x+b)^g+e (|x|≥d)`。

| 曲线 | 位置 | 标准 | 逐字核对 | 实测/推导 |
|---|---|---|---|---|
| sRGB | `:100-101` g=2.4 a=1/1.055 b=0.055/1.055 c=1/12.92 d=0.04045 | IEC 61966-2-1 | ✓（d 用 0.04045 而非旧误值 0.03928） | 与 iccgen 写出的 ICC 参数逐项差 ≤6.4e-6；EOTF 差 **0.26 LSB@16** |
| BT.709 | `:110-111` g=1/0.45 a=1/1.099 b=0.099/1.099 c=1/4.5 **d=0.08125** | BT.709-6 α/β/4.5/L0.0181 | ✓ 主体；d 与文本(0.081/0.08145)不同源 | 与定义式文本 EOTF 差 **max 4 LSB@16 (0.01% FS)** ⇒ d 取 0.08125 在像素上不可见；d=0.08125≈lcms2 的 0.081238 |
| BT.709(zimg) | `:132-133` g=2.4 其余 0，**CicpToken=null（有意）** | 非标准（复刻历史产物） | ✓ 自证不命名 | 往返 0.00；与 zimg 恒等链同形 |
| BT.470M/BG | `:136-141` 纯幂 2.2 / 2.8 | H.273 4/5 | ✓ | zimg 实测 pow(1/2.2) max 10.7、pow(1/2.8) max 9.1 ⇒ 纯幂无争议 |
| **SMPTE 170M** | `:391 "smpte170m" or "6" => Gamma22` | H.273 6 = BT.601-6/SMPTE 170M，**与 BT.709 同一条 0.45 分段** | ✗ | γ2.2 vs 定义式 EOTF max **2752 LSB@16 = 4.20% FS**；zimg 实测把它做成 γ2.4（见 N10） |
| SMPTE 240M | `:174-175` g=1/0.45 a=1/1.1115 b=0.1115/1.1115 c=1/4.0 d=0.0912 | SMPTE 240M-1995 | ✓（0.45 与 4×0.0228 的推导自洽） | 台阶仅 **3.9 LSB@16**；往返 3.87 LSB@16 |
| ProPhoto | `:144-145` g=1.8 c=1/16 d=16/512 | IEC 61966-2-5（V=16L, L≤1/512） | ✓ | 往返 0.00 |
| linear | `:178-179` | H.273 8 | ✓ | — |
| PQ | `:194-198` m1=2610/16384, m2=2523/4096×128, c1=3424/4096, c2=2413/4096×32, c3=2392/4096×32 | SMPTE ST 2084 | ✓ **五个常数全对**；且写成有理数，避开了"c1=0.83525"这个流传的截断错值 | `PqOetf(1.0)=1.000000000`、`PqEotf(1.0)=1.000000000`、往返 max **0.000 LSB@16**；与 zimg 独立实做到 6 位小数（100nit：0.508078 vs 0.508080） |
| HLG | `:201-203` a=0.17883277 b=0.28466892 c=0.55991073；`:344-354` 分段 | BT.2100-2 Table 5 | ✓ 三常数与分段（1/12 ↔ 0.5）全对 | `HlgOetf(1/12)=0.500000`、`HlgEotf(0.5)=0.083333`、往返 0.000；参考白锚：L_W=1000/γ=1.2 下 203nit ↔ 码 0.75（实测 `HlgOetf(0.2648)=0.749879`） |
| iec61966-2-4 / xvycc / bt1361 / log / log100 / log316 / smpte428 | `ForCicpTransfer` `:385-398` 无条目 ⇒ null | — | 引擎**未实现** | 实测 zimg：`iec61966-2-4` **可用**(exit 0)、`bt1361`/`log`/`smpte428` 拒（Undefined constant）、`log100`/`log316` 解析过但运行期 swscaler 报 -129 ⇒ 0 字节 | ⇒ **能力缺口(已 fail-closed)**；注意 `FfmpegCommandBuilder.cs:980-982` 的 `TransferToCicp` 仍能**写** 11/12/17 号标签，而引擎不能按其编解码 |

**Bt2100Ootf 常数出处可核性**：`Bt2100Ootf.cs:9-15` 引的是
```
F_D = α·Y_S^(γ−1)·E ;  Y_S = 0.2627R+0.6780G+0.0593B ;  γ = 1.2 + 0.42·Log10(L_W/1000)
```
系数与 γ 公式 ✓ 与 BT.2100-2 Table 5/Note 一致（0.2627/0.6780/0.0593 和为 1，实测 `LumaPrime` 同值，`Bt2446MethodA.cs:33-34`）；`Apply/ApplyInverse` 的代数反解（`:18-19`）与 `Scale` 的 α·ys^(γ−1)（`:66-70`）自洽。**但**注释里"Note 5f / 5h / 5i"这三个条款号我无建议书原文可校 ⇒ **未判定(需 BT.2100-2 文本)**。β 黑位提升项（`:15` 提到 `β=3(L_B/L_W)^(1/γ)`）在 HEAD **未被实现**（`Apply` 只做 α·Y^(γ−1)·E）⇒ 黑位提升缺位：能力缺口，代码未宣称做了（`:9-16` 只是把它列为"显示 EOTF 是合成式"的事实），不计缺陷但记入 §4。

### 主张 4：两条项目记忆的裁决点

**(a)「ICC 的 TRC 必须派生自像素实际编码的 trc」**
- 源侧：成立。`IccProfileService.cs:211-249` 逐字读 rTRC 的 `para` functionType 0–4（`case 3: FromParametric(P(0),P(1),P(2),P(3),P(4),0,0,"ICC para3")`）与 `curv` ⇒ 曲线来自**实际编码**。
- 出口侧：**有一格不成立**。`ColorTransformPlan.cs:1013-1018`：
```csharp
// ICC 的 TRC 必须跟着**像素实际用的那条 trc**走（= OutTransfer，已含容器钳制）。
if (p.OutTransfer != null)
{
    …
    p.IccTrc = p.OutTransfer;      // 钳位发生时它就是 iec61966-2-1 ⇒ TIFF 旧行为不变
```
  而 `:988 p.OutTransfer = dst.Curve.CicpToken`（= 目标**声明**的 token），与 `:752` 换掉的 `p.DstCurve`（zimg 格 = `Bt709Zimg`，CicpToken 故意 null）**不是同一个对象**。⇒ `--color-709-curve zimg` 时 ICC 的 TRC 来自声明而非实际编码。
  更糟的是日志把这格宣称成一致：`QueueProcessor.cs:1615` `已嵌入目标 ICC（…），与引擎转换后的像素一致`。标签：**真缺陷-标准**（豁免有意的、`:748-749` 写明了，但"ICC 派生自实际编码"这条记忆规则在该格没有收口，且对外文案说了不成立的话）。区分读数：像素 vs ICC 的实测光域差 = 7.25% FS（4748 LSB@16），远超"可以忽略"级别；而 Std 默认格三者自洽（工程曲线 vs iccgen ICC 参数差 ≤2.5e-4 ⇒ 像素差 8.6 LSB@16 = 0.03 LSB@8，可核）。
- 另有一条**同类但更早**的路径：传统 D1/显式目标格里 ICC 由 iccgen 出（`IccProfileService.cs:355-356` 的 `zscale=…t={trc},iccgen`），trc 同样取自声明 token ⇒ 主张 1-c 的 5 个格一并把"错曲线"写进 ICC。

**(b)「zscale 不支持 smpte428 传输特性」**
- 复验成立（权威构建实测：`t=smpte428` 与 `tin=smpte428` 都 `Undefined constant or missing '(' in 'smpte428'` → exit -22 → 零产物；`iccgen=color_trc=smpte428` 同样被拒，虽然 `color_primaries` 里有 smpte428=10）。
- 仓内裁决点齐全、**没有试图把它交给 zscale 的正常路径**：
  - `ColorSpaceRegistry.cs:364`（`_ => null, // gamma1.8 / smpte428 等：不可精确表达`）⇒ `ExactTransferName` 不放行 ⇒ `ResolveInputCurve` 返回 null ⇒ 传统链不成链（`BuildFilter` 不被调用）；
  - `:623-631` 明写"smpte428 故意不收录（原色侧）"，`:76-77` 记录"zscale 不识别 smpte428"；
  - `TransferCurve.cs:151-153` 的 DCI-P3 用 `Gamma26` 且 `CicpToken=null` ⇒ 不会被命名出去。
- 唯一泄漏路：**用户手填** `--color-trc smpte428` → `Decision.cs:313/379` 直拼 `t=`。而那条出声告警 `ColorSpaceRegistry.cs:743-744`
```csharp
if (t == "smpte428")
    warns.Add("传递 smpte428(DCI-P3 gamma) zscale 不支持，将被归一化");
```
  只挂在 GUI（`ValidateCicpTriple` 全仓唯一调用点 = `MainWindow.xaml.cs:3983`），CLI 侧无拦截 ⇒ 用户拿到 `-22` 与一句"Unable to parse 't'"。标签：**能力缺口(已 fail-closed，但 CLI 面缺点名)**；"将被归一化"这句本身也与实际行为（硬失败）不符 ⇒ 文档过期。

---

## 3 新发现

**N1 · 趾段↔幂段衔接导致往返不闭合（真缺陷-精度，量级小但注释读数被推翻）**
`TransferCurve.cs:226-229`（正）与 `:246-247`（逆）：
```csharp
if (ax < D) return s * (C * ax + F);          // 线性趾段
…
double linAtToe = C * D + F;                  // 趾段在线性域的端点
if (C != 0.0 && D > 0.0 && al < linAtToe) return s * ((al - F) / C);
```
逆函数以**趾支端点** `C·D` 为界，而正函数在 `E=D` 处跳到幂支 ⇒ 码值落在 `[D, D+gap)` 的像素被解到趾支范围、再编码回到趾支 ⇒ 单边位移。实测（逐字转录本仓常数，65536 码点）：
| 曲线 | decode→encode 最大偏差 | @码 | 跨界 EOTF 下降 |
|---|---|---|---|
| Bt709 | **16.26 LSB@16**（1.02 LSB@12 / 0.25 LSB@10 / 0.06 LSB@8） | 5338 | 3.39 LSB@16 |
| Smpte240M | 3.87 LSB@16 | 5980 | 0.72 LSB@16 |
| Srgb / ProPhoto / Bt709Zimg | **0.00** | — | 0.00 |

对照注释 `TransferCurve.cs:107-108`："影响：表插值与解析式在拐点附近可能选不同分支（实测差 ≤10 LSB），自测对带台阶的曲线放宽到 24 LSB" ⇒ **≤10 LSB 被推翻（16.26）**，但仍在自家 24 LSB 放宽阈内 ⇒ 门禁不会红，属"注释里的实测读数不可复现"。可修方向（不改标准、不改标签）：逆函数的分支界改用**幂支端点**（`ToLinear(D⁺)`）而非 `C·D`，往返即闭合。

**N2 · `Bt709` 注释的两处单位/出处问题（文档过期）**
`TransferCurve.cs:104-108`：
```
/// BTS.709/BT.2020 SDR：α=1.099 β=0.099，γ=1/0.45，toe 斜率 4.5，拐点 d=0.08125。
/// ⚠️ 该定义里线性段是幂段的**切线**（标准系数取整后不完全相切），在 code≈0.0813 处
/// 留有 ~21 LSB（0.03%）的台阶——…8bit 下仅 0.26 LSB 不可见。
```
实测：前向台阶 **16.2 LSB@16 = 0.06 LSB@8 = 0.25 LSB@10 = 1.02 LSB@12**（用 α=1.099/β=0.099/L0.0181 与本仓 d=0.08125 各算一遍，16.2/16.3）。⇒ "21 LSB" 无对应口径（16.2 才是前向台阶，34 是往返跳变）；**"8bit 0.26 LSB" 实为 10-bit 的数**（8-bit 是 0.06）。另外"线性段是幂段的切线"这句在 d=0.08125 下**不成立**（实测两段在界点不等，差 16.2 LSB@16；真正的切线点在 L=0.01798 且系数取整后无法同时相切）——同文件 `:156-160` 给 240M 的 d 用的是"斜率×断点"（4×0.0228=0.0912），若把同一套推导用于 709 应得 **4.5×0.0181=0.08145**，而代码取 0.08125 ⇒ 注释未写出它的真正出处（= lcms2/iccgen 的 0.081238，实测）。两条曲线的 d 推导法不同源这件事本身就值得在注释里点名。

**N3 · `:812` 的"H2 的 zscale 不做 BT.2100 HLG OOTF"被实测推翻（真缺陷-标准，依据性文字）**
`ColorTransformPlan.cs:811-814`：
```csharp
//     就会变成真缺陷"的隐雷（两套算子口径分歧：H2 的 zscale 不做 BT.2100 HLG OOTF，
//     而 H1 的 `Bt2100Ootf` 做 —— 这正是 `verify-hlg-route` 那条 OOTF 断言的立足点）。
```
实测：`zscale=…t=arib-std-b67` 在 linear=1.0 处给 **0.629618**；
- 不含 OOTF 的纯 OETF：`HlgOetf(1.0)` = **1.000000**（⇒ 若 zimg 不做 OOTF，此处应≈1.0，实际差 24679 LSB@16）；
- 含 OOTF（α=L_W=1000、γ=1.2、线性域 1.0=100 nits）：`HlgOetf((100/1000)^(1/1.2))` = **0.62925** ⇒ 与实测差 24 LSB@16；第二锚点 linear=0.2648 预测 0.38133 vs 实测 **0.381506**。
⇒ zimg 的 arib-std-b67 **确实带 OOTF**。删除 H2 的裁定（2026-10-05）不依赖这句，但作为裁定依据的文字是假的，且 `verify-hlg-route` 若以此句为"两套算子必然分歧"的立足点，其前提需要重写。标签：**真缺陷-标准**（陈述与工具实际行为相反）。

**N4 · zimg 的 HDR 线性域 = 1.0 ↔ 100 cd/m²，与引擎 `SdrWhiteNits=203` 不同源（真缺陷-精度，跨管线）**
实测（权威构建，rgb48le）：
```
PQ  linear 1.0 -> code 0.508080      HLG linear 1.0 -> code 0.629618
ST 2084 参照：100nit=0.508078  203nit=0.580689  10000nit=1.000000
sRGB 码 1.0 --zscale(tin=iec61966-2-1:t=smpte2084)--> 0.508080 (=100.0 nit)
```
引擎侧（`ColorTransformPlan.cs:433-439`）：
```csharp
double refWhite = Tone is { Active: true } ? Math.Max(1.0, Tone.SdrWhiteNits) : TransferCurve.SdrWhiteNits;
… if (dstNits > 0 && …) s.EncodeScale = refWhite / dstNits;
```
⇒ 同一"SDR→BT.2020 PQ/HLG"意图，引擎把参考白落在 **203 nits**，传统 zscale 链落在 **100 nits**，差 **2.03×**（约 1 档）；容器标签（CICP 16/18）**不带白点**，因此两种产物标签相同、显示亮度不同。可达格：`Decision.cs:379`（显式目标 `BT.2020 PQ`/`BT.2020 HLG`/`P3 PQ`，SDR 源）。标签：**真缺陷-精度**（跨管线同意图不同像素，正是本项目反复修的"三处不一致"类）；严重性依赖 HDR 目标是否真走传统链——`--color-engine legacy` 或引擎拒绝时暴露。区分读数：这不是"谁错"的问题（100 与 203 都是有人用的约定），而是**两管线未在同一处收敛、且标签无法表达差异**；修法=把传统链的 HDR 出口也过 `refWhite=203` 的换算（或明确拒绝该格并点名）。

**N5 · `smpte170m → Gamma22`：对标准不成立，且与 zimg 也不同（真缺陷-标准）**
`TransferCurve.cs:391`：
```csharp
"smpte170m" or "6" => Gamma22,          // BT.601-525/170M 与 2.2 同族（业界按 gamma2.2 处理）
```
H.273 编号 6（SMPTE 170M / BT.601-6 525）的传递特性与 BT.709 同为 `1.099L^0.45−0.099 / 4.5L` 分段。实测三方：
| 谁 | smpte170m 的编码值（linear≈0.502） | 光域偏差 |
|---|---|---|
| 标准（=709 分段） | 0.70683 | 基准 |
| 引擎（γ2.2） | — | EOTF 差 max **2752 LSB@16 = 4.20% FS**（172 LSB@12） |
| zimg 实测 | **0.749462** | 与纯 γ2.4（0.75038）一致 ⇒ zimg 也**不是**分段 |
⇒ 同一 CICP token 三方三样，且 `ZimgDivergentTransfer`（`:840`）黑名单是 `"bt709" or "bt470bg" or "smpte240m"`，**不含 smpte170m** ⇒ 若哪天放行 H2 会静默换曲线。标签：**真缺陷-标准**（引擎把 6 号解码成 γ2.2 与标准不符；注释的"业界按 gamma2.2 处理"不足以对抗 H.273 定义）。注：`bt470m`(4)=2.2 ✓、`bt470bg`(5)=2.8 ✓ 这两条与 H.273 一致，只有 6 号错。

**N6 · `ToneMapping.cs:31-34` 残留的 BT.2390-EETF 归属（文档过期；对外面已干净）**
```csharp
/// <summary>ITU-R Report BT.2390 EETF（带线性段的 Hermite 样条滚降，拐点偏移 0.5）。
/// 未实现：本工具链无参考实现可用、且尚未拿到标准公式原文，**绝不凭记忆填常数**；
/// 选用时按"未就绪"拒绝而非静默降级。</summary>
Bt2390 = 3,
```
与 HEAD 的裁决点自相矛盾：`ColorStrategyPlanning.cs:331-333`（"已核对现行 Report ITU-R BT.2390-12 正文，全文不含 EETF 定义"）、`ColorIntentFactory.cs:342`（"bt2390 无可靠出处…不走引擎"）、`CliParser.cs:827`（"bt2390 recognized, unsupported"）⇒ **记忆定案在代码/CLI/规划层都已落地，只有这条 enum 注释仍把算子的形式与参数（拐点偏移 0.5）归属给 BT.2390**。对外宣称面复查：`--color-tone-map` 帮助只列 `reinhard|hable|mobius|bt2446a` ✓，无 UI 下拉、无文档把它当标准曲线 ⇒ 唯余该注释。标签：**文档过期**。

**N7 · BT.2446 Method A 的常数自洽性（复验通过，仅"精确"用词过强）**
`Bt2446MethodA.cs:75-76`：
```csharp
/// 硬锚点：Y'=1 ⇒ Y''=255 ⇒ E=1.2466 ⇒ 255^E = 1000.0 nits = Lmax（建议书常数自洽，可校验读法）。
```
实测：E(255) = 2.8305e-6·255² − 7.4622e-4·255 + 1.2528 = **1.246567** ⇒ 255^E = **999.80 nits**（比 1000.0 低 0.02%）⇒ 锚点方向正确、"精确落到"应改为"≈"。其余全部自洽（实测）：Step2 三段 knee 在 0.7399 / 0.9909 处两段差 **0.0**，`Step2Knee(1.0)=1.000000`；Table 4 在 Y''=70 处连续（174.09 vs 174.08 nit，跳变 0.001% Lmax）；`Rho(10000)=33.000000`。`ToneMapping.cs:137-152` 的值域换算（`k = SdrWhiteNits/PeakNits` → 1.0=L_HDR）与 `:144-151` 的"编码域三明治"（反 BT.2020 线性域→交回 dstCurve 编码）与记忆定案一致 ✓；文件头 `:14-17` 明确"§4.1 的 Y' 与 §4.2 的 Y' 不是同一编码 ⇒ 不能拿往返恒等当门禁" ✓ 无误导标签。

**N8 · tonemap 算子与 ffmpeg 的逐字比对（复验通过 2 项 + 死参数 1 项 + 未收口的能力入口 1 项）**
- Hable：`ToneMapping.cs:87-89` `(x*(x*a + b*c) + d*e) / (x*(x*a + b) + d*f) − e/f`，与上游 `vf_tonemap.c::hable()` 逐字相同 ✓（常数 a=.15 b=.50 c=.10 d=.20 e=.02 f=.30 ✓，`hable(0)=0` 手算/实测一致）。
- Mobius：`:92-99` 与上游 `mobius(in,j,peak)` 同式，仅多一条 `denom` 退化保护；`x<=j` vs 上游 `in<=j` ✓；默认 `Knee=0.3`（`:51`）**得到实测背书**：本构建 `tonemap=mobius` 与 `mobius:param=0.3` 输出全等（white px16=30157），`param=0.4`→32182、`0.5`→33237。
- 施加对象：上游算子作用在 `max(R,G,B)`，与 `ToneMapping.cs:123 float maxY = Math.Max(r, Math.Max(g, b));` 同 ✓（注释 `:115-118` 的"不用亮度做分母"与 ffmpeg 一致而非"项目独造"）。
- **死参数**：`Decision.cs:321-322`
```csharp
var tmCurve = string.IsNullOrWhiteSpace(options.TonemapCurve) ? "hable" : options.TonemapCurve!.Trim().ToLowerInvariant();
var tmTone = tmCurve == "hable" ? "hable:param=0.5" : tmCurve;
```
  实测 `tonemap=hable`、`hable:param=0.5`、`hable:param=0.9` 输出**逐字节同**（8063）⇒ ffmpeg 的 hable 不吃 param；注释"保留 param=0.5 观感"不成立（无害但误导）。标签：**文档过期(轻)**。
- **未 fail-closed 的入口**：本构建 `-h filter=tonemap` 的算子枚举是 `none/linear/gamma/clip/reinhard/hable/mobius`，**没有 bt2446a**；而 `CliParser.cs:838 case "tonemap-curve": options.TonemapCurve = value;` 无白名单直传 ⇒ 用户 `--tonemap-curve bt2446a`（引擎侧合法名）在传统链会 ffmpeg 报错零产物。标签：**能力缺口（未 fail-closed 到 CLI 面，但运行期响亮）**。

**N9 · `--color-hdr-peak` 与 HLG 的 L_W：承诺落空（真缺陷-静默失效）**
`ColorStrategyPlanning.cs:352-354`：
```csharp
p.Advice = $"HLG 按 BT.2100-2 Table 5 OOTF 处理（α=L_W={p.Src.NitsOfLinearOne:F0}nit, "
         + $"γ={FfmpegGui.Services.ColorMapping.Bt2100Ootf.SystemGamma(p.Src.NitsOfLinearOne):F4}）；"
         + "如需按实际显示峰值，用 --color-hdr-peak 指定 L_W。";
```
静态实测（正向命中 + 缺席判据都做了）：`ColorHdrPeakNits` 的消费点只有 `ColorIntentFactory.cs:413-415` → 写 `it.HdrPeakNits` → 只进 `Tone.PeakNits`（`:333`）；`NitsOfLinearOne` 由 `FixHdrNits`（`:440-444`）恒置 PQ=10000 / HLG=1000，且 `nitsOfLinearOne:` 具名实参**全仓 0 个调用点** ⇒ HLG 的 α 与 γ **不可配置**，`--color-hdr-peak` 改不动它（γ 恒 1.2000）。标签：**真缺陷-静默失效**（选项被文档承诺的作用不发生）。
配套语义风险（现在不咬人，修 N9 时会咬）：`HdrPeakNits` 的来源含 **MaxCLL**（内容峰值，`FfmpegCommandBuilder.cs:1796-1797`），而 OOTF 的 α 是**显示名义峰值**（`Bt2100Ootf.cs:11-12` 引对了原文）。HEAD 把两者隔离着（正是 N9 的"落空"救了它）⇒ 若按 advice 文案去"接通 --color-hdr-peak→L_W"，必须同时把 MaxCLL 与 mastering `max_luminance` 分途（后者本来就是显示峰值，前者不是），否则内容峰值会同时改 α 和 γ。标签：**未判定→（需产品级 HLG A/B 读数）**，见 §4。
单位与有理数解析（本条**复验通过**）：`:1743-1744` 明写"键是 `max_content` 不是 max_cll/MaxCLL"、"`max_luminance` 打印为有理数字符串 `40000000/10000`，值已是 cd/m² ⇒ 必须按 num/den 解析"，`ExtractJsonNumber`（`:1806+`）按 num/den 取商 ✓；`ProbePeakNits` 的优先级 MaxCLL > BS.2086（`:1796-1800`）与 `ff_determine_signal_peak` 对齐 ✓；无超时/取消静默（`:1780-1793` 两条都点名并回退名义峰值）✓。HLG 的 OOTF 施加序（`ColorKernels.cs:171-183` 解码侧先 OOTF 再 `/L_W`、`:197-211` 编码侧先 `×dstNits` 再逆 OOTF）经代数核对与"1.0=203nits 中间域"自洽 ✓（往返锚点：ref white 203nit ↔ HLG 码 0.7499，实测 `HlgOetf(0.2648)=0.749879`；203nit ↔ PQ 码 0.580689）。

**N10 · 曲线求值数值面其余读数（未发现缺陷的项也列，供后续 lane 引用）**
- f32/f64：`TransferCurve.cs:260-295` 的批量入口一律 `double` 求值后回落 `float` 存储 ⇒ 单样本舍入 ≤6e-8 相对（远小于 16-bit 的 1.5e-5 LSB），无累积（就地逐样本）✓。
- 查表分辨率：`ColorKernels.cs:456-462`（65536 项 `Eotf float[]` / `Oetf ushort[]`，索引即 16-bit 码）⇒ 这条路径是**枚举不是近似**（注释 `:443` 的主张成立）；量化地板 = 1/65535 ✓。`:409-418 BuildEncodeLut16` 在 `clamp=false` 时抛 `NotSupportedException`（拒绝装成功）✓，`:398-401` 把"表定义域就是线性[0,1]"写清 ✓。
- 非单调区：`FromTable`（`:67-75`）对下降 >1e-6 的表返回 null（不可逆 ⇒ 上层携带 ICC）✓；实测各解析曲线 EOTF 在趾段界点有**下降跳变**（Bt709 3.39 LSB@16 / 240M 0.72），属 ITU 定义性质而非实现错（见 N1/N2）。
- `TableFromLinear`（`:312`）`return t[0] == 0f && l <= 0f ? 0.0 : 0.0;` ⇒ **两分支同值的死条件**（卫生项，无像素影响）。
- `Equivalent`（`:360-377`）注释称"ICC 用 s15Fixed16 存参数（量化 ~3e-5）"⇒ 实测 iccgen/lcms2 与工程常数在 BT.709 的 **a、b 两项差 2.50e-4**（>1e-4 容差）而 sRGB 只差 ≤6.4e-6 ⇒ 1e-4 容差的"理由"（量化）不足以覆盖"实现取整分歧"，后果：由 iccgen/任何 lcms2 生成的 BT.709 ICC 的 TRC 会被判"不等价" ⇒ `CicpTokenOfCurve`（`FfmpegCommandBuilder.cs:955-967`）返回 null、`SameCurveAs`（`ColorSpaceDescriptor.cs:192`）为 false ⇒ BT.709-ICC 源被当"不可命名空间"（携带而非映射）。标签：**真缺陷-精度（判据过严导致能力丢失）+ 文档过期（注释给的量化上界不对）**；像素影响 = 0（保守方向不错标签），性能影响 = 多一次携带/放弃映射。

**N11 · 出口标签侧的其余复验（通过项）**
`Decision.cs:330-333` 把 tonemap 出口的宣称值同步成链末 zscale 的 `p=/t=`（`outP = tmDstP; outT = tmDstT;`）✓，且 `d.TonemappedToSdr = true; d.PixelsUnchanged = false` ✓——旧"PQ 帧塞 iccgen / 宣称停在 PQ"那一类已收口；`capT`（JPEG/WebP/TIFF）恒 `iec61966-2-1`（`FfmpegCommandBuilder.cs:1000/1010/1020`）✓ ⇒ 受限格式的 SDR 钳位格标签兑现；`iccgen` HDR 拒（实测 `-1163346256` + "Not yet implemented"）与 `IccProfileService.cs:334-335` 的提前 return null 一致 ✓。

---

## 4 未证边界（不写成结论）

1. **产品级 A/B 未测**：同一素材跑 `--color-engine engine|legacy` 比像素。仓内最新构建产物是 `src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.dll`（Sep 30 22:11），HEAD 提交时间 2026-10-10 ⇒ 旧二进制不能当 HEAD 读数，`--dry-run` 那一路我放弃执行。本报告第 2/9/10 行的"格可达"来自源码路径 + UI 项存在性，非端到端产物。
2. **D1 复合链的合成像素误差量级未测**：我那轮 `a2chain.ps1` 参数写错（`min=bt709:m=bt2020nc` 且缺 `rin=pc` ⇒ 走成 YUV 矩阵+量程支；自建 709→2020 矩阵亦有误），读数 max 15780/20458 **作废**。要按 `ColorSpaceRegistry.cs:284-285` 的真实形状重做：`format=gbrpf32le,zscale=tin=X:t=linear,colorchannelmixer=…,zscale=pin=P:tin=linear:p=P:t=bt709:m=M`，并拿 ffmpeg 自己给的矩阵当参照（不自造）。
3. **doc §3 的 mean=1921 未复现**（四轴 3041–4172）。要收口需要 doc 当时的采样表/脚本；在此之前该 mean 只能记"未复现"，`TransferCurve.cs:117` 的 7231 同理。
4. **BT.2100-2 的 Note 5f/5h/5i 条款号未判定**（需建议书原文；仓内只有 `tools/std/bt2446-1.txt`）。γ 公式与系数本身已与多方独立实现（zimg 实测、本仓、libultrahdr 口径）互证。
5. **HLG 的 OOTF 缺黑位提升 β**：`Bt2100Ootf.cs:15` 引了合成式显示 EOTF 含 β=3(L_B/L_W)^(1/γ)，`Apply` 未实现该项。是否需要（静像 L_B≈0）未测 ⇒ 记能力缺口，不计缺陷。
6. **10-bit/12-bit HLG 的 OETF 归一是否受量程影响未测**：`RawColorPipeline.cs:99` 的解码段只有 `format=rgb48le`（注释 `:114` 明写"不写 zscale 避免隐式色彩转换"），有限量程( tv )源的展开依赖 swscale 按帧 `color_range` 自动处理。对**已标 tv 的 RGB 容器**（cICP range=tv 的 PNG / JPEG XR）是否展开，我没有读数；若不展开，HLG 的 1.0=峰值归一会整档偏 8%。这条与 range lane 交叉，需 `--color-range`/源标签的端到端读数。
7. **GainMap / UltraHDR 出口的 709 口径**：`ColorTransformPlan.cs:869-870` 自称"底图编码由 GainMapEncoder 自持、计划层出口曲线不被消费"——我只读了注释，未进 `GainMapEncoder` 核实它用的是哪条曲线（若是 bt709 定义式，则 zimg 开关在 GainMap 格"不作用"的说法正确；若它另有默认，需登记）。
8. **zimg 的"1.0=100 nits"是否本构建特有**：只有一个 ffmpeg 构建可测，未与第二份 zimg/libplacebo 交叉 ⇒ N4 的"约定"归因是单点读数（数值本身可复现，归因待第二锚）。

---

### 取证索引（全部在本目录，夹具可重跑）
`a2curves.ps1`（11 条曲线的双向对拍 + smpte428 拒绝）、`a2icc.ps1`（iccgen para 参数反解 + HDR nits 锚点 + 传统链 SDR→HDR 形状）、`a2numeric2.ps1`（往返/台阶/PQ-HLG 锚点/2446 自洽）、`a2rms.ps1`（RMS 与趾段分解）、`a2chain.py`（复合链——**参数错误，读数作废，见 §4.2**）、`ramp_in.raw`/`src_def709.raw`/`src_g24.raw`/`o_*.raw`/`chain_*.raw`/`identity.raw`。
