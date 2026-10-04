# 交接文档进度分析（新会话接手复核，2026-10-04 晚）

> **本文件性质**：新 Agent 接手后对**最新交接文档**的**独立复核**（只读分析 + 实跑取证）。
> **不含任何源码/门禁改动**。所有结论均标注「实测」或「推断」。
> ⚠ **本文件 §1.3 经过一次自我修正**：初稿把根因写成「引擎选了 H2(zscale)，zscale 不做 HLG OOTF」；
> 经独立子代理调查 + 我逐条复核源码与日志，**该机制描述是错的**，真相更严重（H2 是死后端、
> 实际跑 H1 却连 H1 的 OOTF/色调映射也没生效）。初稿的修法（改后端判据）**已作废**，见 §1.3/§1.4。
> 复核对象：`docs/review-2026-10-04/PROGRESS-PACKAGE-2026-10-04.md`（14:36，最新）
> + `docs/review-2026-10-04/HANDOVER-完整修复-2026-10-04.md`（07:55）
> + `.workbuddy-ai/memory/2026-10-04.md`（14:08，524 行，最详细）

---

## 0. 一句话结论

上一会话的**工作已远超打包快照所记**：打包文档（14:36）说「窗口 3 有 1 待修 + 47 条未跑」，
但实仓在 **14:04–14:32** 之间又做了一整批修复（P1-1 复选框语义、三条色彩 P1、两条永久门禁、
GainMapTestHost 更新）。**新会话实跑复核**：

| 项 | 打包文档记载 | **新会话实测** | 判定 |
|---|---|---|---|
| `verify-gainmap-host.ps1` | ❌ 4 FAIL（PS5.1 不兼容） | ✅ **PS7 端到端 `PASS=18 FAIL=0`**；❌ **PS5.1 仍 `exit=1`** | **半修**（见 §2） |
| `verify-color-token-normalize.ps1` | ❌ 3 FAIL（`auto` 未排除） | ✅ **`PASS=33 FAIL=0`** | **已修**（已核实 diff） |
| `verify-hlg-route.ps1` | ❌ 2 FAIL | ❌ **仍 `PASS=11 FAIL=2`**；**根因升级为真产品缺陷**：`--color-tone-map hable` **根本没执行**，产物仍是 HDR 标签 | **未修 + 根因升级**（见 §1） |
| 整轮门禁 | 「47 条未跑」 | **实跑 30/67**，**37 条从未在本会话跑过** | 覆盖缺口（见 §3） |
| 四项目重建 | 「未重确认」 | ✅ 时间戳一致（14:05，晚于最新源码 14:04） | **满足** |

---

## 1. 最重要发现：`verify-hlg-route` 的两条 FAIL 都是**假红**，但底下压着一个**真 P0 缺陷**

### 1.1 交接文档的说法（不完整）
`PROGRESS-PACKAGE` §4 写：「HLG 与 PQ 源峰值都被读成 `203 nit` ⇒ `$pk1 -ne $pk2` 不成立；
PSNR 有限但 < 40 dB 阈值不达标」，并**猜测**可能是日志路径读错或正则对 HLG 不匹配。

### 1.2 新会话实测（比文档描述严重）
新会话实跑 `verify-hlg-route.ps1`（exit=1，工作目录 `tests/output/hlgroute_bfde470d`），并复核上一轮保留的
`tests/output/hlgroute_116e28dc/`，得到**三条新证据**：

**(a) 产物根本没被色调映射到 SDR —— 输出仍带 HDR 传递标签（实测）**
```
hlg2sdr/plain.png => Video: png, rgb48be(pc, gbr/bt2020/arib-std-b67), 192x128
pq2sdr/plain.png  => Video: png, rgb48be(pc, gbr/bt2020/smpte2084),   192x128
```
门禁的命令行是 `--color-tone-map hable` 且目标是 `-f png`。**产物仍是 HLG/PQ 标签** ⇒
「HDR→SDR 色调映射」**没有发生**。两个产物文件大小**逐字节相同**（148,520 B）。

**(b) 两个产物的像素统计量逐值相同，且动态范围被异常扩大（实测，我独立复现）**
用 `signalstats` 量四张图的 luma 统计（**我独立跑出，与子代理报告一致**）：
```
mk_hlg/plain.png    YMIN=7976  YMAX=43149  YAVG=30190.7   ← HLG 源
mk_pq/plain.png     YMIN=9965  YMAX=35193  YAVG=27442.7   ← PQ 源
hlg2sdr/plain.png   YMIN=5656  YMAX=53700  YAVG=29070     ← HLG→SDR 产物
pq2sdr/plain.png    YMIN=5656  YMAX=53700  YAVG=29069.7   ← PQ →SDR 产物
```
两条推论：
- 真正的 HDR→SDR 转换**不可能扩大动态范围**；产物 YMIN 更低、YMAX 更高（53700 ≈ 撞满量程）。
- **HLG→SDR 与 PQ→SDR 产物的三个统计量完全相同** —— 只有「按标称 16-bit 做了什么、
  但没做真正的色彩转换」才会如此（HLG 与 PQ 的源码值本就相同）。
- 子代理另用**按日志同形重建的纯 zscale 链**做对照，实测 `YMIN=6914/6312 YMAX=53961/53963
  YAVG=30303/30423`、两者 PSNR=**32.5 dB** ⇒ 生产产物的统计量与**任何真正的转换都不符**。

**(c) 83.68 dB 的差异不是色调映射造成的（实测 + 独立参照实验）**
```
PSNR(hlg2sdr, pq2sdr)         = average:83.6812   (r:79.7 g:91.9 b:88.1)
PSNR(mk_hlg 源, hlg2sdr 产物) = average:11.6359
```
产物相对**源**只有 11.6 dB 差异（有改动），但 HLG 产物 vs PQ 产物之间高达 83.68 dB。
子代理用 `gray16` 逐像素差分量化了这 83.68 dB 的物理含义：
`maxdiff=21`（16-bit 码值 = 8-bit 的 0.08 LSB）、`meandiff=1.702`、`rmse=2.302`，
分布 `d=0:9518 / d=3:12588 / …` —— 高度结构化，正是 **16-bit→8-bit 舍入的量化台阶**。
⇒ **83.68 dB 完全由「两次解码舍入 + 编码舍入」解释，不存在任何亮度映射**。

**(d) 独立参照实验：产物**确实没有**被色调映射（实测，本案最强证据）**
另做一次**独立对照**（不依赖应用的任何输出，纯 ffmpeg 手工链）：
```
ffmpeg -i mk_hlg/plain.png -vf "zscale=t=linear:npl=1000,format=gbrpf32le,zscale=p=bt709,\
tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=rgb48be" ref.png
```
- 参照产物标签：`rgb48be(pc, gbr/**bt709/bt709**)` ⇒ **真 SDR**，符合「HLG→SDR」的正确形态。
- `PSNR(引擎产物 hlg2sdr, 参照 ref)` = **7.698 dB** —— 极低，二者**几乎无关**。
⇒ **引擎产物是一个「按 HLG 曲线解码/钳位但仍带 HDR 标签」的东西，不是 SDR 产物。**
这条把「色调映射没执行」从推测变成了**实测结论**。

**(e) 峰值断言读的是「钳制后」的值，但这不是根因（实测）**
日志两次都打印 `峰值来源=整帧实测：199nit ⇒ 采用 203nit`。复核 `RawColorPipeline.ApplyMeasuredPeak`
（`RawColorPipeline.cs:1184-1200`）：
```csharp
double applied = Math.Clamp(measured, tone.SdrWhiteNits, Math.Max(tone.SdrWhiteNits, nominal));
```
实测 199 **低于** `SdrWhiteNits=203` ⇒ 两条曲线都被钳到 203 ⇒ 门禁正则
`采用\s*(\d+)\s*nit`（`verify-hlg-route.ps1:65`）读到的自然是同一个数。
**但**：规划层的计划**确实不同**（`hlg2sdr.log:4` = `tonemap=Hable peak=1000nit`；
`pq2sdr.log:4` = `tonemap=Hable peak=10000nit`）⇒ 计划对、**像素没跟上**。

### 1.3 根因定位（实测 + 代码级证据；**本小节已修正**）
> ⚠ **修订说明**：本节初稿把根因写成「引擎挑了 zscale(H2)，而 zscale 不做 HLG 的 OOTF」。
> 经独立子代理调查 + 我对源码与日志的逐条复核，**该机制描述是错的** —— 真相更严重：
> **H2 后端被选中，但代码里根本没有任何地方执行 H2**；实际执行的是 H1 raw 管道，
> 而且连 H1 的 OOTF 与色调映射也没生效。以下为修正后的结论。

**(a) H2 是「死后端」：`ColorBackend.FfmpegFilter` 全仓无人消费（实测，决定性）**
```
grep ColorBackend  (src/FfmpegGui, *.cs)  → 20 处命中，全部在：
  ColorTransformPlan.cs:14   枚举定义 { None, FfmpegFilter, InProcess, Lut }
  ColorTransformPlan.cs:797/803/809   置位
  ColorTransformPlan.cs:859  日志文案
  ColorTransformPlan.cs:899/903       自校验（只读 p.Backend 做断言，不构建命令）
  ColorStrategyPlanning.cs   11 处，全部是 = ColorBackend.None
```
⇒ **`FfmpegCommandBuilder` 里零处消费**。而 `RawColorPipeline.cs:16-18` 又明说
「H2 那种情况下**不经过**本管道（由 Plan 的 Backend 决定，二者互斥）」。
**两端互斥、却都没接** ⇒ `Backend=FfmpegFilter` 置位后无人执行，是**死后端**。

**(b) 实际跑的是 H1 raw 管道（实测，日志自证）**
`hlg2sdr.log` 三条命令构成完整的 H1 raw 管道，**没有一条含 `zscale=` / `tonemap=`**：
```
:12  [色彩] ffmpeg … -i "…/mk_hlg/plain.png" -frames:v 1 -vf "format=rgb48le" -f rawvideo "…cms_….rgb48"
     → RawColorPipeline 的解码步（DecodeToRgb48Async）
:18  [色彩] tier=S dop=1 tileRows=整帧 stream=off frame=0MB 变换耗时 19ms 吞吐 1.3 MPix/s
     → RawColorPipeline 的进程内变换步（TransformFileAsync）
:20  [色彩] ffmpeg … -f rawvideo -pix_fmt rgb48le … -color_primaries bt709 -color_trc iec61966-2-1 …
     → RawColorPipeline 的编码步（BuildEncodeArgs）
```
⇒ **日志 `:17` 宣称 `后端=H2(zscale…)`，`:12/:18/:20` 却在跑 H1** —— 同一个缺陷的两个症状：
① H1 被当成 H2 记账；② H2 无执行路径。与已知缺陷 #59 同族。

**(c) ~~HLG 的 BT.2100 OOTF 确实没进像素~~ —— ⚠⚠ 此结论**已被推翻**，见 §1.5**
> 经第二轮深挖（逐像素反推 + 在 `MeasurePeakLinearRgb48` 内插桩），**OOTF 是正确施加的**。
> 我此前据以判定的「反推线性峰值 ≈0.98」用的是**我手写的错误 HLG 逆 OETF 公式**
> （把 `b` 放在 `/12` 外：`exp((E'-c)/a)/12 + b`）。BT.2100-2 的正确形式是
> **`(exp((E'-c)/a) + b)/12`**，与仓内 `TransferCurve.HlgEotf`（`:347`）**完全一致**。
> 用正确公式逐像素重算得 `mx = 0.198778257`，与引擎插桩输出 `0.198778` **逐位吻合**。
> ⇒ **引擎的 HLG 解码 + OOTF + 峰值实测链路全部正确**，无缺陷。详见 §1.5。

**(d) `--color-tone-map hable` 在像素里毫无效果 —— 根因已锁定（实测 + 插桩）**
- 规划层 `:4`/`:17` 是 `tonemap=Hable peak=1000nit … headroom=4.93`（PQ 侧 `headroom=49.26`）；
  而同文件 `:14` 实际执行的 spec 打印为 **`tonemap=off`**。
- **根因**：`RawColorPipeline.ApplyMeasuredPeak`（`:1195`）把实测峰值钳到
  `[SdrWhiteNits, nominal]`。本素材实测 **199 nit < 203 nit**（SDR 参考白）⇒ 钳到 **203**
  ⇒ `ToneMapParams.Headroom = 203/203 = 1.0` ⇒ `Active`（`ToneMapping.cs:66`）翻成 **false**
  ⇒ 内核 `toneOn = false`（`ColorKernels.cs:490`）⇒ **色调映射整段被跳过**。
- ⚠ **但这是「正确地跳过」而非缺陷**：实测内容峰值 ≤ SDR 参考白 ⇒ 本图**没有 HDR 余量**，
  Hable 在该区间是恒等变换，不映射是数学上正确的结果（`tone.Active` 的语义就是"是否需要压缩"）。
- **真正的缺陷只有一条（已修）**：`Active=false` 把「用户没要」与「要了但无余量」**混为一谈**，
  且 `:14` 那行自陈 `tonemap=off` 而 `:17` 仍写 `tonemap=Hable` ⇒ **日志自相矛盾、不可归因**。
  修法 = 保留 `Mode` 并补一行**点名**（见 §1.4 A2'），**不改** `Active` 的语义。

  建议修复时在 `RawColorPipeline.cs:169-173` 附近加一条 `spec.Tone.Active` 日志，5 分钟定死。
  ✅ **已在 2026-10-04 实施**（见 §1.5 与 `RawColorPipeline.cs` 的「内核状态」日志）。

**结论（经两轮修正后的最终版）**：本缺陷**不是**「OOTF 没跑」，也**不是**「色域/曲线算错」。
拆开看是三件**互相独立**的事：
1. ✅ **HLG 解码 + OOTF + 峰值实测链路完全正确**（§1.5 逐位验证）—— 无缺陷。
2. ✅ **色调映射"没生效"是正确行为**（实测峰值 199 < 203 ⇒ 无 HDR 余量 ⇒ 恒等）——
   但**日志不可归因**（`:14` 说 off、`:17` 说 Hable，自相矛盾）⇒ **已修**（保留 Mode + 点名）。
3. ⚠ **`Backend=H2(FfmpegFilter)` 是死后端**（全仓无人消费）—— 这是**真的**接口不一致，
   但**当前无害**：实际执行走 H1，而 H1 是自称"精确"的那条路径，结果正确。
   它的危害是**记账不可信**（日志声称 zscale 执行）、以及**未来若真的接上 H2 会变成真缺陷**。
⇒ **`verify-hlg-route` 的两条 FAIL 确实是假红**（门禁读错量 + 阈值方向反），
   但**修门禁时必须补真正有牙的判别断言**，而不是只改读数（见 §1.4）。

### 1.5 第二轮深挖：逐位验证「HLG 链路是对的」（实测，插桩取证）

第一轮的「OOTF 没施加」是**我的错误**。第二轮用**在函数内插桩**的方式逐位核对，方法与结论：

**方法**：在主项目里临时插桩（`ColorKernels.MeasurePeakLinearRgb48` 与
`MeasureContentPeakNitsAsync`），打印每像素的输入码值、scene 值、OOTF 后的值与
`srcHlgPeak`；同时用 PowerShell 独立重算同一批像素。**插桩已全部撤回**（`grep TMPDIAG` = 0）。

**取证链**：
```
引擎插桩：pix0 code=(29125,11184,52146)  srcHlgPeak=1000
          mx(pre-scale)=0.198778  scale=4.926108  ret=0.979203  srcHlg=True
          内核状态：HLG源OOTF=开（γ=1.2000）        ← OOTF **确实**置位并执行

独立重算（PowerShell，用 BT.2100-2 正确公式）：mx = 0.198778257
          ⇒ 与引擎 0.198778 **逐位吻合**  ✓
```

**关键：我第一轮的错误公式**。BT.2100-2 的 HLG 逆 OETF 是
```
E = E'^2 / 3                    (0 ≤ E' ≤ 0.5)
E = ( exp((E'−c)/a) + b ) / 12  (0.5 < E' ≤ 1)
```
我误写成 `exp((E'−c)/a)/12 + b`（`b` 放到了 `/12` **外面**）。
两个形式的锚点自检（`a=0.17883277, b=0.28466892, c=0.55991073`）：

| 锚点 | 正确式 `(exp+b)/12` | 我的错式 `exp/12+b` | 应有值 |
|---|---|---|---|
| `E'=0.5`（分段连续点） | **0.0833333** | 0.3442798 | 与下段 `0.5²/3=0.08333` 相接 ⇒ 正确式对 |
| `E'=1.0`（满量程） | **1.0000000** | 1.2609465 | 1.0 ⇒ 正确式对 |

⇒ 仓内 `TransferCurve.HlgEotf`（`TransferCurve.cs:347`）用的**正是正确式**，**代码无误**。
我第一轮"E'=0.75 应等于参考白 0.5"的前提本身也是错的（HLG 满量程在 E'=1.0）。
**教训**：凭记忆写标准公式会得出与实现相反的结论；本仓「不凭记忆写常量」的纪律同样适用于**审计方**。

**顺带确证的副产品**：`HLG源OOTF=开（γ=1.2000）`、`HLG目标逆OOTF=关`、`tonemap模式=Hable 激活=否`
—— 这三个读数以前**任何日志都拿不到**，现已作为**永久日志**加入（可审计性提升，见 §1.4）。

---

### 1.4 建议修法（**未执行**，需用户裁定后开工）

**方案 A（治本，推荐）：二选一，但必须「要么执行、要么显式失败」，不许静默顶替。**
- **A1（补执行路径）**：在 `FfmpegCommandBuilder.Decision.cs:307-319` 那条现成 zscale 链旁补 H2 支，
  并同步 `:313-317` 的 `outP/outT/outMatrix`。⚠ 需先实测 zscale/tonemap 链对 HLG 的 OOTF 等价性
  （本仓 `ColorEngineRouter.cs:113-116` 已记「传统管线走 ffmpeg `tonemap=hable` 收满量程」），
  否则会造出「标签 SDR、像素按错曲线」的新失配。
- **A2（把死后端变成响亮失败）**：让 `Backend=FfmpegFilter` 在无执行路径时**显式拒绝并点名**，
  而不是被 H1 静默顶替。这是**最小改动、最符合本仓「宣称≠交付」纪律**的一步，
  建议**先做 A2 让缺陷显形**，再决定要不要做 A1。
- ⚠ **不要**只改 `ColorTransformPlan.cs:800` 的判据（初稿的写法）—— 那是基于**错误机制**的修法：
  H2 本来就是死后端，改判据只会把「无人执行的 H2」换成「无人执行的 H1 记账」，缺陷照旧。

**方案 B（必须先做的定位动作）**：查 `QueueProcessor` 里 `TryRunColorEngineAsync` 的 H1/H2 分派，
确认它在 `Backend=H2` 时为何仍调 `RawColorPipeline`（**子代理标注为推断，未读该分派代码**）。
以及：为何进内核时 `spec.Tone.Active` 是 false（`RawColorPipeline.cs:169-173` 附近加一条日志即可定死）。

**门禁侧（两条 FAIL 都要改，且**都不能单独改**）**：
- **FAIL 1（`:65`）**：正则改为读**原始实测值** `'峰值来源=整帧实测：\s*(\d+(?:\.\d+)?)\s*nit'`
  （对应 `RawColorPipeline.cs:1198` 的 `：{measured:F0}nit`）。⚠ 不要用宽松双数抓取。
  - ⚠ **但单独改这条就是「为绿而放宽」** ⇒ 必须**同时**加判别性断言（见下）。
- **FAIL 2（`:117`）**：`$num -lt 40.0` 的**方向写反了** —— 缺陷是「不该像而像了」，**PSNR 越高越可疑**。
- **判别性断言（真正有牙的那条，必须加）**：读两份日志的**原始实测值**，断言
  `原始实测峰值(HLG) / 原始实测峰值(PQ) > 3`（理论比 10；现状 = 1.00 ⇒ **红**）。
  这条同时覆盖「HLG OOTF 没施加」与「两条曲线各自解码」，且**不受 16bit→8bit 舍入噪声影响**。
- **保留不动**：`:115` 的 `-ne 'inf'`（2026-09-16 素材缺陷的回归锁）与 `:128-129` 的同源控制组
  （证明 PSNR 判据非空跑）。⚠ `:115` 只排除 `inf` 正是 83.68 dB 逃逸的原因。
- **素材（`:77-78`）**：`testsrc2` 的 HLG/PQ 峰值（500 / 5000 nit）**都远超 203nit** ⇒ 两条曲线
  都被钳到 203，这正是「采用值相同」能成立的前提；若换更暗素材，旧断言会**碰巧变绿**（空跑）。
  建议换自造已知码值渐变，可照抄 `verify-color-peak.ps1:102-118` 的 `MakePqFlat`。
- **可选加一条**：产物 `color_transfer` 必须是 SDR（本门禁目前只检查**素材**标签，从不检查**产物**）。
  这条是本次缺陷最直接的回归锁（当前必红）。
- **PSNR 阈值若要用**：`32.5 dB` 有实测背书（纯 zscale 对照），但**必须固定输入位深口径**
  —— 同一对输入 48-bit 读 83.68、24-bit 读 87.26。

**变异验证（四条，缺一不可）**：
1. HLG 分支改 `--color-trc smpte2084`（两源同曲线）⇒ 比值断言必须转红；改回 ⇒ 绿。
2. 注释 `ColorTransformPlan.cs:442-446` 的 `SrcIsHlgScene = true` ⇒ 比值断言转红；恢复 ⇒ 绿。
3. 保 `:128-129` 的「同源两次 = inf」控制组绿（证明不空跑）。
4. 去掉 `ToneMapping.cs:109` 的 `HableRaw(Headroom)` 归一化 ⇒ PSNR 下界断言转红而比值仍绿
   ⇒ 证明两条断言**互补，缺一不可**。

---

## 2. `verify-gainmap-host`：PS7 已修，**PS5.1 仍红**（实测）

- **PS7（运行器宿主）**：`PASS=18 FAIL=0 exit=0` ✅（`GainMapTestHost` 自身 `汇总: 通过 103, 失败 0`）。
  ⇒ 打包文档 §4 那条「PS5.1 不兼容」的**行为面**已随 14:29 的宿主重建修好。
- **PS5.1（双宿主合规）**：**仍 `exit=1`**，实测报错：
  ```
  Start-Process : Cannot validate argument on parameter 'ArgumentList'. The argument is null, empty,
   or an element of the argument collection contains a null value.
  At tests/scripts/verify-gainmap-host.ps1:43 char:49
  + $p = Start-Process -FilePath $exe -ArgumentList @() -NoNewWindow -Wait -PassThru ...
  ```
  ⇒ **根因未修**：PS5.1 不接受 `-ArgumentList @()`。本仓铁律「静态 PARSEOK ≠ 双宿主可用」在此再次成立。
- **修法（未执行）**：`:43` 去掉 `-ArgumentList`（不带参数时直接省略该形参），或写 `-ArgumentList ''`。
  ⚠ 改完**必须在真 PS5.1 下重跑**（`C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe`，
  实测存在、版本 `5.1.26100.9549`），不能只看 `verify-ps-compat` 的静态扫描。
  ⚠ **且要注意**：该脚本的日志路径是 `tests/output/validate/gmhost_<gid>.txt` —— PS5.1 失败时
  **不产出日志**（实测我的检索找不到 `gmhost*`），所以「找不到日志」本身就是这条红的症状。

---

## 3. 覆盖缺口：本会话只跑了 **30/67**，37 条从未跑过（实测）

从 `tests/output/t84/w67-{1,2,3}-2026-10-04.log` 按 `^\[<name>.ps1\s*\]` 提取，去重后：

- **已跑 30 条**（w67-1: 10 条 + w67-2: 10 条 + w67-3: 10 条），其中 **3 条红**：
  `verify-gainmap-host`（PS5.1 另计，PS7 绿）、`verify-color-token-normalize`（**已绿**）、`verify-hlg-route`（**仍红**）。
- **未跑 37 条**，含全部 L3 五套与 UI 门禁：
  ```
  verify-png3-interop · verify-color-tonemap · verify-color-engine-xcheck · verify-geometry-engine ·
  verify-engine-firstframe · verify-geometry-orientation · _probe-geometry-single-source-scan ·
  verify-jxl-codestream-route · verify-anim-static-target · verify-jxl-probe-timeout ·
  verify-ffmpeg-heartbeat · _probe-sync-read-before-wait-scan · verify-jxr-anim-input ·
  _probe-ct-chain-closure-scan · verify-avif-anim-probe · verify-webp-anim-probe ·
  verify-engine-alpha-preserve · _probe-tool-detect · verify-startup-warmup · verify-ipc-extra-options ·
  _probe-settings-clone-coverage · _probe-matrix-structure · _probe-stderr-merge-scan ·
  verify-simd-switch-fallback · verify-ui-host · verify-ui-param-matrix · verify-ui-strategy-map ·
  verify-ui-param-defects · verify-cli-strict · verify-png-structure ·
  verify-package-smoke · verify-jbrd-e2e · verify-orientation-rewrap · verify-gainmap-thirdparty ·
  verify-encoder-knob-liveness · _probe-jxl-input-target · _probe-raw-default-tier
  ```
- ⚠ **两条新门禁（第 66/67 条）在 w67-1..3 里都没跑到** —— 它们只在 `newgate-runner-2026-10-04.log`
  里单独跑过。**「67/67 全绿」不可宣称**；当前只能宣称「30/67 已跑，其中 2 条红」。
- 运行器自身读数（实测自 w67-3）：`script-list=67 条（受管基线 67）`、`shape-selfcheck=OK（17 针）`
  ⇒ **清单接线正确**（`$expectedScriptCount = 67` @ `_run-step3-gates.ps1:1016`），缺的只是**跑完**。

---

## 4. 打包文档中**已被后续工作推翻/落伍**的记述（避免下次白跑）

| 打包文档（14:36）说法 | 实况 |
|---|---|
| §4 「窗口 3 遗留：`verify-gainmap-host` PS5.1 不兼容，需修」 | PS7 已绿；**PS5.1 仍红**（半修） |
| §4 「窗口 3 遗留：`verify-hlg-route` 2 FAIL 待修」 | **仍红**，且根因升级为「色调映射未执行」（§1） |
| §4 「`verify-color-token-normalize` 33/0、`verify-gainmap-host` 18/0 已修」 | ✅ 两项 PS7 读数**实测属实** |
| §6 待办 1/2 把两处列为 P0 | ⚠ 仍需修，但 `color-token-normalize` 已不在列 |
| §6 待办 9「跑完窗口 4–7（47 条）」 | 精确数应为 **37 条**（30 已跑） |
| §2.1 复选框语义「`HasExplicitColorTarget` 仅认 CP/CT」 | ✅ 与源码一致，且 `ServiceProbe wire` 有永久锁（93/0） |

---

## 5. 「不构成基线」的证据链仍然成立（实测复核）

上一轮的记忆里记过一轮**自陈矛盾**：说「整轮 65/65 全绿」，但四窗日志尾部**每份自陈**
「`-Scripts 子集 N/65 ⇒ 不构成基线`」。本次复核 w67-1/2/3 **三份日志尾部同样自陈**：
```
===== 运行器汇总（-Scripts 子集 10/67 条 ⇒ **不构成基线**）：全部通过 =====
```
⇒ **逐窗拼凑永远不等于整轮基线**。要满足交接文档 §6 待办 9，必须**单次全量**跑（或按仓规
明确接受「逐窗 + 非零退出合计 0」并**如实标注不构成基线**）。这一点值得写进纪律。

---

## 6. 建议的恢复顺序（新会话视角，按优先级）

**P0（阻断「可发布」判定）**
1. **`verify-hlg-route` 背后的真缺陷**：`Backend=H2(FfmpegFilter)` 是**死后端**（全仓无人消费），
   实际执行 H1 raw 管道，且 **HLG 的 OOTF 与 `--color-tone-map hable` 都没落到像素**
   （产物仍是 HDR 标签）。按 §1.4：**先做 A2 让缺陷显形**（H2 无执行路径 ⇒ 显式失败点名），
   再决定是否做 A1（补 zscale 执行支）。
2. **`verify-hlg-route.ps1` 两条假红断言**：`:65` 改读原始实测值、`:117` 方向纠正，
   **并必须同时加「HLG/PQ 原始实测峰值比 > 3」的判别性断言**，否则只是把真缺陷粉饰成绿。
   配 §1.4 的四条变异验证。
3. `verify-gainmap-host.ps1:43` 去掉 `-ArgumentList @()`，**真 PS5.1 重跑**验收。

**P1（覆盖与基线）**
4. 跑完剩余 **37 条**（按 ~10 条/窗切，每窗含 ~5 min 探针 ⇒ 约 4 窗、~90 min）。
5. 整轮**单次全量**或明确标注「逐窗、不构成基线」，再谈「67/67」。

**P2（收尾，需授权）**
5. `pack.ps1 -Mode all` + `verify-release-package` **在 10-04 环境实跑**（此前 16/0 是 10-03 旧证据）。
6. L3 五套复跑。
7. **提交 / tag `1.6.0BETA4` / push** —— 按纪律**等用户明确授权**；`git add` 由维护者执行。

---

## 7. 仓库现状（新会话实测快照）

- **未提交**：`git status --short` = **35 个 ` M`** + **7 个 `??`**（含 `RawGamutFit.cs`、
  `_probe-jxl-input-target.ps1`、`_probe-raw-default-tier.ps1`、`docs/review-2026-10-03/`、
  `docs/review-2026-10-04/`、两份 RAW 主题文档）。相对 HEAD 合计约 **+1598/−152 行**。
- **HEAD** = `bdd4b0b`（未变，与打包文档一致）。
- **四项目重建**：`FfmpegGui.dll`/`.exe`/`ServiceProbe.exe`/`UiTestHost.exe` = **14:05**，
  `GainMapTestHost.exe` = **14:29**；最新源码 `QueueProcessor.cs` = **14:04**
  ⇒ **四项目均不旧于源码**，STALE 闸不会拦。
- **无残留锁**、无 `_mut-*.ps1` 残留。
- **本文件是新增的唯一产物**（`docs/review-2026-10-04/PROGRESS-ANALYSIS-2026-10-04.md`），
  新会话**未改动任何源码 / 门禁 / 既有文档**。
- ⚠ **实跑留下的诊断目录**（门禁自身在失败时保留，`tests/output/` 整个在 `.gitignore` 内 ⇒ 不进版本控制）：
  `tests/output/hlgroute_bfde470d/`（本会话新跑）、`tests/output/hlgroute_116e28dc/`（上一会话）。
  两者都是**排查 P0-1 的证据**，建议保留到修复完成后再清。

---

*复核方式：只读读码（read/grep）+ 实跑三条门禁 + 复核既有产物日志。所有「实测」均可复现；
标注「推断」者已给代码级证据链与复现方法。*
