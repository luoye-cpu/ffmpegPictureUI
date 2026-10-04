# ARW→TIF「跑分不正确」归因 · 2026-10-03

> 样本：`C:\Users\20210\Desktop\TEST\1-1.TIF`（Sony 参照）、`1-2.ARW`（源）。
> 结论一句话：**ARW→TIF 的产物是「线性、无色彩标定」的 dcraw 中间件本身（像素逐字节相同），
> 根本没做 linear→显示域的映射** ⇒ 比正常显影暗约 3 倍；而应用自带的「质量分析」拿它自己当"源"比，
> 因此永远报「无损/完全一致」——分数从根上就不成立。

---

## 〇、先确认两件事

| 事实 | 取证 |
|---|---|
| **1-1.TIF 与 1-2.ARW 是同一次拍摄** | `DateTimeOriginal` 同为 `2026:09:26 05:45:18`、`WB_RGBLevels` 同为 `602 256 375`、ISO 400 / f6.3 / 1/160 / 50mm 全同 |
| **1-1.TIF 是 Sony Imaging Edge 的官方显影（参照件）** | `Software = Edit 4.1.01.06121`、带完整 Sony maker notes（`Quality=RAW`、`PictureProfile=PP2`）；6000×4000、16-bit RGB、无压缩 |

⇒ 参照件的形态是**显示域（gamma 编码）的 sRGB 16-bit TIFF**。

---

## 一、根因链（每一步都有实测）

### 1. 中间件是**线性**的
`RawService.PreProcessAsync`（`RawService.cs:175`）执行：
```
dngtool -d -T -o 0 -q 3 -W -H 1 -6 -i 1-2.ARW -O <tmp>/1-2_raw.tiff
```
实测产物 6024×4024 `rgb48le`，`Software = dcraw v9.26`、**无任何色彩标签**（`ColorSpace: Uncalibrated`、无 ICC），
`signalstats YAVG = 12374.3`。

### 2. 应用的 ARW→TIF 产物**就是这份中间件**（像素逐字节相同）
`FfmpegGui.exe --headless -i 1-2.ARW -o out -f tiff` 实测：
- 产物 6024×4024 `rgb48le`、`YAVG = 12374.3`（与中间件**完全相同**）
- 两路 rawvideo 比对 `cmp` ⇒ **逐字节相同**；SSIM = **1.000000 (inf)**
- 应用自己的日志：
  ```
  [色彩引擎] 决策：None 色彩损失=None｜源描述置信度低（CodecDefault，按惯例假定）→ 不改像素
  [色彩引擎] 计划为直通（不改像素）⇒ 本次由 ffmpeg 完成重编码
  [cmd] ffmpeg -y -color_primaries bt709 -color_trc linear -i ".../1-2_raw.tiff" ... "1-2.tiff"
  ```

### 3. 为什么引擎判「直通」——它**看不见**"源是线性"这件事
`ColorIntentFactory.FromOptions`（`ColorIntentFactory.cs:47-54`）：
```
// ── 源描述符：外部 ICC → 源内嵌 ICC → 容器 CICP 标签 → 业界惯例假定 ──
var meta = FfmpegCommandBuilder.ProbeInputColorMetadata(inputPath);
```
源描述符是**探测那个中间件文件**得到的 —— 它没有任何色彩标签 ⇒ 落到「业界惯例假定 = sRGB」，
置信度 `CodecDefault`；而同文件注释（`:53-54`）写明：**`CodecDefault` 被规划层的置信度闸门挡住不许改像素**。

⚠ 关键矛盾：应用**知道**它是线性（`QueueProcessor.cs:562-578` 明确把 `ColorTrc=linear` 写进选项，
并作为 ffmpeg 的**输入**标志 `-color_trc linear` 下发），**但这份知识从未进入色彩引擎的源描述符**。
⇒ 引擎按"sRGB 源 → sRGB 目标"判恒等，像素一个都不动。

### 4. 后果一：产物暗约 3 倍
| | YAVG（16-bit） | 归一 |
|---|---|---|
| 应用产物 / 线性中间件 | **12,374.3** | 18.9% |
| Sony 参照 1-1.TIF | **36,693.6** | 56.0% |

两者正是 linear ↔ sRGB 传输函数的位移关系（0.189 经 sRGB 编码 ≈ 0.473）。
⇒ 任何按显示域解读的查看器/评分器看到的都是一张**明显偏暗、发灰**的图。

### 5. 后果二：显式 `--color-space sRGB` 更糟 —— **标签与像素矛盾**
加 `--color-space sRGB` 重跑：引擎**仍判直通**（YAVG 不变 = 12,374.3），但日志多出一行
`[tiff] 已嵌入目标色彩空间 ICC（sRGB），与转换后像素一致` ⇒ 实际是**在线性像素上嵌了 sRGB ICC**。
这正是本仓最忌讳的「宣称 ≠ 交付」。

---

## 二、「跑分不正确」的直接原因：质量分析对 RAW 任务结构性不成立

`ProgressWindow.xaml.cs:303` → `QualityAnalysisService.AnalyzeAsync(_item.InputPath, _item.OutputPath)`。
对 RAW 任务，`InputPath` 在两个时相是两种东西：

| 时相 | `InputPath` | 比较对象 | 结果 |
|---|---|---|---|
| 任务进行中 | `QueueProcessor.cs:560` 已改成**线性中间件** | 中间件 vs 产物 | 像素同一份 ⇒ **SSIM 1.0 / PSNR ∞** ⇒ UI 报「🔒 无损编码 — 输出与源图完全一致」= **假完美** |
| 任务结束后 | `QueueProcessor.cs:1055` 已还原成**原始 .ARW** | ARW vs 产物 | ffmpeg **解不了 ARW**（见下）⇒ 拿不到任何 SSIM/PSNR ⇒ 报错 |

**ffmpeg 解不了 ARW（实测）**：
```
ffprobe 1-2.ARW → codec_name=tiff, width=0, height=0, pix_fmt=unknown
ffmpeg  -i 1-2.ARW → [dec:tiff] Decoding error: Invalid data found when processing input
```
把这条比较逐字复现（源=ARW、产物=TIF）：
```
[in#0/tiff_pipe] Could not find codec parameters for stream 0 (Video: tiff, none): unspecified size
Error binding filtergraph inputs/outputs: Invalid argument
```

⇒ 无论哪个时相，**RAW→TIF 的"跑分"都没有意义**。

### 附：即使勉强比出数来，也是错的
把"线性中间件"与其**自身**的 sRGB 版本（同像素、只换传输函数）按应用的原样滤镜图比：
```
SSIM R:0.567657 G:0.576294 B:0.556592 All:0.566848 (3.63 dB)
PSNR r:11.99 g:11.97 b:12.10 average:12.02 dB
```
一份**无损**的 16-bit TIF 会被打成 ≈0.57 SSIM / ≈12 dB PSNR。

---

## 三、判定：哪些是缺陷、哪些是设计取向待定

**确定是缺陷（有实测证据）**
1. **`--color-space sRGB` 时把 sRGB ICC 嵌在线性像素上** ⇒ 标签与像素矛盾，产物必然被解读错。
2. **质量分析对 RAW 任务零判别力**：源侧要么是中间件（假完美）、要么是不可解码的 ARW（报错）；
   且它**不告诉用户"这类任务无法评分"**，而是给出一个数或一段 ffmpeg 报错。
3. **应用自相矛盾**：对 ffmpeg 声称输入是 `linear`，对色彩引擎却按"惯例 sRGB"处理 ⇒ 同一份文件两套描述。

**待产品裁定的设计取向**
- ARW→TIF 到底该产出**显示域 sRGB**（与 Sony/常规显影一致、可直接看图）还是**场景线性工作件**
  （给后续调色用）？现状是"**声称**走色彩引擎、**实际**直通"，两种意图都没兑现：
  - 若目标 = 显示域 ⇒ 必须把 `ColorTrc=linear` 作为**源描述符**喂给引擎（或直接让 dngtool 出显示域）。
  - 若目标 = 线性工作件 ⇒ 应在 UI/文档明说，并**禁止**把 sRGB ICC 嵌上去；同时质量分析必须拒绝评分。

---

## 四、修复方向（按性价比排序，均未实施）

1. **把 RAW 中间件的色彩描述显式喂给引擎**（治本，改动最小）：
   RAW 路径已知道 `primaries=bt709 / trc=linear` ⇒ 在装配 `ColorIntent` 时把这对值作为
   **源描述符**（置信度提到 `Explicit`/`IccMetric` 同级），使置信度闸门放行 `Action=Map`，
   从而真正执行 linear→目标域的映射。⚠ 必须同时确认"引擎的 linear 曲线"与 dngtool 的线性口径一致。
2. **`--color-space sRGB` 时禁止"直通 + 嵌 ICC"的组合**：直通时不得改标签（或改标签必须同时改像素），
   二者只能同真。
3. **质量分析对 RAW 任务显式拒绝并点名**：源是 RAW（或 `InputPath` 已被替换过）⇒ 直接给出
   "该任务无法做 SSIM/PSNR 评分（源为相机 RAW，与产物不同域）"，**不产出任何数字**；
   并把"源描述置信度低"这类既有降级登记也一并展示，避免"假完美"。
4. 复跑时补一条产物级判据：ARW→TIF 的产物**不得**是源中间件的逐字节副本（可用 YAVG 比值或直方图门限）。

---

## 五、附：RAW 到底「有没有色彩空间」、该怎样标定

### 5.1 三种东西要分开
| 概念 | RAW 有没有 | 说明 |
|---|---|---|
| **色彩空间**（原色基 + 白点 + 传输函数 + 范围） | **没有** | RAW 不属于任何标准空间，贴 sRGB/BT.709 都是假话 |
| **设备特性**（相机原色 / 白平衡状态 / CFA 相位） | **有** | 用 DNG 的 `ColorMatrix1/2` + `ForwardMatrix` + `CalibrationIlluminant` 描述；**一个 ICC 表达不精确**（相机原色非物理可实现、且强依赖光源） |
| **状态位** | **有** | 线性（扣黑电平后 ≈ 正比光子）、是否已白平衡、是否已去马赛克 |

⇒ 严格表述：RAW = **场景参考 + 设备原生原色 + 线性**。

### 5.2 标定阶梯（四步，每步只能标它已经变成的东西）
1. **CFA RAW** ⇒ 无可标。只能靠 ColorMatrix 描述设备特性。
2. **相机线性 RGB**（去马赛克 + WB）⇒ `transfer=linear` **有据可标**；`primaries` **不能**标标准原色
   ⇒ 要么**不写任何色彩声明**（Uncalibrated），要么写**相机输入 ICC / DCP**。**绝不能写 bt709**。
3. **线性工作空间**（相机矩阵 → XYZ / 线性 sRGB）⇒ **第一次**可以贴标准标签：
   `primaries=bt709 + transfer=linear`（或 P3 / Rec.2020）。
4. **显示域**（应用 OETF）⇒ 标 sRGB ICC 或 CICP（primaries=1 / transfer=13 / matrix=1）。

**核心要点**：`transfer` 与 `primaries` 是**两件独立的事**；本仓只做对了一半。
且**标什么就必须是什么**——改标签必须同时改像素。

### 5.3 对照本仓实测
- dngtool 自述语义（`dngtool.cpp:250`）：`-o <n> output color space (**0=raw camera**)`
  ⇒ `-o 0` 的产物是 **相机原色 + 线性**。
- 应用贴的 `-color_primaries bt709 -color_trc linear`：
  - `trc=linear` ✅（实测：中间件按 linear→sRGB 转换后 `YAVG 12374 → 26288`，向参照 `36694` 靠拢；
    且其原始量级远低于 sRGB 编码应有的量级 ⇒ 确为线性）
  - `primaries=bt709` ❌ **错**（数据在相机原色里）—— 这是**第二个**标签错误
    （第一个是 §一.5 的「直通 + 嵌 sRGB ICC」）。
- 引擎因源文件无标签 ⇒ `CodecDefault` ⇒ 闸门不许改像素 ⇒ 直通
  ⇒ **既没转原色、也没转传输函数**，产物就是相机线性数据。

### 5.4 一个容易搞错的直觉
`1-1.TIF` **不是色度学参照**：它带 Sony Picture Profile（`PP2`）与机内色调曲线，是一份「**look**」，
不是「正确解」。所以目标不该是「让产物等于 1-1.TIF」，而是**链路上每一步诚实标定**；
要不要复刻 Sony 的 look 是**另一层**（tone mapping / 风格化），不能与色彩管理混为一谈。

### 5.5 因此「应该怎么样标定」的落地口径
- **若目标是显示域 TIF**：RAW 预处理要么直接产出已应用相机矩阵 + 目标 OETF 的数据（可试 `-o 1` = sRGB），
  要么保留 `-o 0` 但把 `bt709/linear` 作为**源描述符喂给色彩引擎**，由引擎完成 ②→④。
- **若目标是线性工作件**：`primaries` 必须改成诚实表述（相机原色无法用 CICP 表达 ⇒ 不标、或附相机 ICC），
  并**禁止**嵌 sRGB ICC。
- **两条路都需要先修的一件事**：把「源是线性」**显式喂给引擎**（当前是断的），
  否则 `CodecDefault` 闸门永远把计划压成直通。

---

# 附二：TIFF 能不能承载 HDR —— 完整能力分析（2026-10-03）

> 触发：决定 `ColorTransformPlan` 能力表里 `tiff.CanHdr` 该取 true 还是 false。
> **结论：规范层「能」，我们的工具链层「不能」⇒ 保持 `CanHdr=false` 是诚实的。**

## 一、TIFF 有哪几条色彩表达通路（逐条实测）

| 通路 | 支持？ | 实测证据 |
|---|---|---|
| **CICP**（容器原生标签） | ❌ **无** | ffmpeg 的 tiff muxer 不写 CICP；`exiftool -CICP` 对 .tif 报 `Warning: Tag 'CICP' is not defined`；仓库能力表本就记 `CanCicp=false`（`CicpUnavailableReason.ContainerNonNormative`） |
| **ICC profile**（tag 34675） | ✅ **有** | `exiftool "-icc_profile<=bt2020.icc" -overwrite_original x.tif` ⇒ `1 image files updated`，读回 `ProfileDescription: RGB built-in`（584 B ICC） |
| **位深 / 采样格式** | 16-bit int 上限 | `ffmpeg -h encoder=tiff` 的 Supported pixel formats = `rgb24 rgb48le pal8 rgba rgba64le gray ya8 gray16le ya16le monob monow yuv420p …` ⇒ **无 32-bit float**；实测 `-pix_fmt gbrpf32le` 被**静默降成** 16-bit（`BitsPerSample: 16 16 16`） |

⇒ **唯一能承载 HDR 的通路是 ICC**。位深不是阻断项（PQ 10–12 bit 足够，TIFF 有 16-bit 富余），**阻断项是描述通路**。

## 二、ICC 能不能表达 HDR？—— 规范层：**能**（但很新）

ICC 官方修正案 *ICC.2 amendment: CICP type and tag* 原文（本机下载原文抽取）：

> "This amendment is to ICC.2:2019, and adds the **CICP tag and type already included in ICC.1:2022**.
> **The CICP tag supports HDR workflows by enabling standard video encoding parameters to be encoded as metadata in an ICC profile.**"

- 标签签名 `'cicp'`（`63696370h`），类型 `cicpType`；规范引用 **Rec. ITU-T H.273 / ISO/IEC 23091-2**（即 CICP 标准）。
- 约束：「the colour encoding specified by the CICP tag content **shall be equivalent to** the data colour space encoding represented by this ICC profile」
  ⇒ ICC 里写 CICP 不是"贴个标签"，**必须与 profile 自身的变换等价**。

## 三、但**我们的工具链造不出 HDR ICC**（硬阻断，实测）

```
ffmpeg -vf "format=rgb48le,iccgen=color_primaries=bt2020:color_trc=smpte2084" ...
  → [vf#0:0] Error while filtering: Not yet implemented in FFmpeg, patches welcome
  → exit=2，零产物
```
⚠ `iccgen` 的选项表里**列着** `smpte2084`(16) / `arib-std-b67`(18)，**实现却拒绝** —— 这是"宣称 ≠ 实现"。
⇒ 本仓唯一的 ICC 生成器只能产 **SDR 传递**（bt709 / linear / iec61966-2-1 / bt2020-10 等）。

即便从外部拿到 PQ ICC 塞进 TIFF：**ffmpeg 侧读不回** —— 对带 ICC 的 TIFF，`ffprobe` 报
`color_space=unknown color_primaries=unknown color_transfer=unknown` ⇒ 我们自己的下游（色彩引擎、质量分析）都看不见它。

## 四、消费端现实

- **32-bit float TIFF** 确实是 VFX/ACES 语境里的「场景线性 HDR 载体」（Photoshop 32-bit 模式、Nuke 等），
  但那是**场景线性**、不是**显示参考的 PQ/HLG**；且我们的编码器**写不出 32f**。
- **显示参考 HDR（PQ/HLG + Rec.2020）的 TIFF**：ICC 的 `cicp` 标签 2022/2026 才进规范，
  主流查看器/系统（Windows 照片、资源管理器、多数浏览器）对 TIFF 的 ICC-HDR 支持基本没有
  ⇒ **即使写对也没人认**。

## 五、判定与建议

### 5.0 ⚠ 先更正一个容易被误读的口径：把「能力」拆成**三轴**

上一节说「TIFF 不支持 HDR」**只对「HDR 传递」成立**，不等于「TIFF 装不下 Rec.2020」。三轴各自独立：

| 轴 | TIFF 能力 | 依据（实测） |
|---|---|---|
| **位深** | ✅ 16-bit int ／ ❌ 32-bit float | ffmpeg tiff 编码器 pix_fmt 表；`-pix_fmt gbrpf32le` 被静默降成 16-bit |
| **原色** | ✅ **任意**（Rec.2020 / P3 / ProPhoto…） | 走 ICC（tag 34675），exiftool 可写可读回 |
| **传递** | ✅ SDR（sRGB / BT.709 / BT.2020 曲线…）<br>❌ **PQ / HLG** | `iccgen` 拒绝 smpte2084 / arib-std-b67（`Not yet implemented`） |

⇒ **16-bit + Rec.2020 完全可做**，实测两步都通：
1. `ffmpeg -vf "format=rgb48le,iccgen=color_primaries=bt2020:color_trc=bt709"` ⇒ 584 B ICC，
   `ServiceProbe icc-dump` 读出 `rXYZ=[0.6735,0.2790] gXYZ=[0.1657,0.6753] bXYZ=[0.1250,0.0456]`
   （**精确 BT.2020 原色** + D65）+ `rTRC: para type=3 2.2222 0.9097 0.0903 0.2222 0.0812`（BT.709 曲线）。
2. `exiftool "-icc_profile<=该ICC" -overwrite_original x.tif` ⇒ `1 image files updated`，
   读回 `BitsPerSample: 16 16 16` + `ProfileDescription: RGB built-in`。

且仓库 `ColorSpaceRegistry` 里**本来就有**这条既定通路：「DCI-P3 → **BT.2020 SDR**（bt709 + 生成 ICC）」。

### 5.1 因此钳制应分**三档**（更正上一轮问的两档）

| 档 | 容器 | RAW 默认目标 |
|---|---|---|
| **A** | 能承载 HDR **传递**：`avif` / `jxl` / `png`(16-bit) | **Rec.2020 + PQ** |
| **B** | 不能承载 HDR 传递、但**能带任意 ICC**：**`tiff`** / `webp` / `jpg` / `jpegli` | **Rec.2020 + SDR 传递**（bt2020 原色 + bt709 曲线，走生成的 2020 ICC）← **本轮新增档** |
| **C** | **无任何色彩通路**：`gif` / `jxr` / `ppm` | `bt709 + sRGB` |

⚠ 这与上一轮 Q2 的答案（非 HDR 容器 ⇒ bt709+sRGB）**冲突**：按 Q2，TIFF 会失去 Rec.2020；
而本轮实测表明 TIFF **能**保住 Rec.2020。⇒ 需按 5.1 重新确认落点。

### 5.2 判定（不变）

**保持 `tiff.CanHdr = false`**（该位**只**管「HDR 传递」，**不**妨碍 Rec.2020 —— 后者由 `CanCarryArbitraryIcc=true`
+ `CanGenerateIccFromCicp=true` 已允许），并把理由从单一 `ContainerNonNormative` 扩展为三条：
① 无 CICP 通路；② 工具链造不出 HDR ICC（`iccgen` 拒绝 PQ/HLG）；③ 消费端不支持 ICC-HDR 的 TIFF。

- 想要 Rec.2020 + PQ 的 RAW 默认 ⇒ **目标容器选 avif / jxl / png-16bit**（能力表 `CanHdr=true`，且走 CICP）。
- **TIFF 按已定口径钳成 `bt709 + sRGB`**。
- 将来若要开 TIFF 的 HDR，前置条件是：① 有 PQ/HLG 的 ICC 生成能力（给 `iccgen` 打补丁，或自带
  PQ ICC 模板 + 采样曲线）；② 消费端确认；③ 相应门禁与评审（属「能力扩张」）。

---

## 七、实施记录：RAW 默认输出目标「三档」（2026-10-03，已实测）

### 7.1 口径（用户裁定）
| RAW 输入 + 用户**未**选目标 | 输出目标 |
|---|---|
| 容器能承载 HDR **传递**（avif / jxl / png-16bit…） | **Rec.2020 + PQ** |
| **TIFF（唯一例外）** | **Rec.2020 + SDR**（bt2020 原色 + sRGB 曲线，走生成的 2020 ICC） |
| 其余（webp / jpg / jpegli / gif / jxr / ppm…）= 默认路线 | **bt709 + sRGB** |

用户显式选目标 ⇒ 用用户的目标（仍受容器能力钳制）。线性中间件 `1.0 = SDR 白点 203 nits`。

### 7.2 先解决的结构性障碍
`ColorPrimaries/ColorTrc` 原先**同时**充当两个角色：
- **输入声明** —— `FfmpegCommandBuilder.BuildColorArgsSplit`（写在 `-i` 之前）；
- **输出语义** —— `FfmpegCommandBuilder.DecideOutputColor`（高级分支 `outP/outT`）。

而 dngtool 的中间件是**线性**的 ⇒ 输入侧要 `linear`、输出侧要 PQ/sRGB。一对字段表达不了两个值，
结果就是「输入被正确标 linear、输出被同一份 linear 拖住」——**这才是 §一 那条「直通」缺陷的机制**。

⇒ 新增运行时字段 `ColorSourceDeclPrimaries/Trc`（**不进 `PresetData`**），只喂输入侧与引擎的源描述符；
`ColorPrimaries/ColorTrc` 从此**只**表示输出目标。

### 7.3 改动点（`src/FfmpegGui/**` 6 文件 / +150 −43，含 1 个非本主题文件）
| 文件 | 改动 |
|---|---|
| `Models/FfmpegOptions.cs` | 新增 `ColorSourceDeclPrimaries/Trc`（运行时、非持久化） |
| `Services/FfmpegCommandBuilder.cs` | `BuildColorArgsSplit` **最前**新增分支：`ColorSourceDecl*` 在位则只认它 |
| `Services/ColorMapping/ColorIntentFactory.cs` | ① 源描述符改用 `FromCicp(ColorSourceDecl*)`；② `targetExplicit` 改为「RAW 默认 + `caps.HasColorPath` 才判显式」（无色彩通路容器仍留 `Target==null`，保住规划层那条「钉成 sRGB」的 GIF 例外）。<br>⚠ **2026-10-04 续修（P1-3）**：① 的置信度由 `FromCicp` 默认的 `CicpTag` **显式抬到 `NamedIcc`** —— 原先它**恰好等于**闸门阈值 `MinConfidenceForMapping`，靠 `>=` 才放行，枚举一重排即零告警退回「直通」；现不依赖枚举序，且有 `ServiceProbe wire` 的枚举序锁守着。 |
| `Services/QueueProcessor.cs` | RAW 默认块：写输入声明 + 按三档写输出目标（`rawCanHdrTransfer ? (bt2020,pq) : rawIsTiff ? (bt2020,srgb) : (bt709,srgb)`）；HDR 判据用 `CanHdr`（**不**叠 GainMap，否则 Ultra HDR 底图会变成 PQ） |
| `Services/ColorMapping/ImageEncoderArgs.cs` | 注释性质调整（+3 −3） |
| `Services/IsoBmffGainMapWriter.cs` | ⚠ **含非本主题改动（AVIF range 修复，+39 −8）** —— 属 P0-2，非本 RAW 三档主题 |

### 7.4 实测（headless，改动后的产物）
```
ARW→TIF : [RAW] 输入声明 bt2020/linear；输出目标 bt2020/iec61966-2-1（能承载 HDR 传递=False，TIFF 例外=True）
          [色彩引擎] 决策：Map｜后端=H2(zscale)     ← 不再是「直通（不改像素）」
          [色彩引擎] ✅ 6024x4024 变换 611ms
ARW→AVIF: [RAW] 输出目标 bt2020/smpte2084（能承载 HDR 传递=True）
          产物 ffprobe: pix_fmt=yuv420p12le color_primaries=bt2020 color_transfer=smpte2084 color_space=bt2020nc
```
⇒ TIFF 得 **Rec.2020 + SDR**、AVIF 得 **Rec.2020 + PQ（自动 12-bit）**，均与口径一致。

### 7.5 验证与收口（2026-10-04 完成）
- **四项目重建**：0 警告 0 错误。
- **本次改动引发的两处红，按既有流程收口**：
  - `_lib-matrix` 的 A21/A30 行号锚 **+11 漂移**（新增分支插在 `BuildColorArgsSplit` 顶部）⇒
    966/976/1008/2050 → **977/987/1019/2061**（`grep -n` 复核实况 + 字面量唯一 + pin 演进注释）⇒ **8/0** ✓
  - CJK **UI 面 821 → 822**（成因 = `FromCicp(..., "RAW 中间件（dngtool 去马赛克线性输出）")` 的描述符名，
    非 UI 文案但按 ②-c fail-closed 分桶必落 UI 桶）⇒ 显式登记后抬 1，余量仍 0 ⇒ **14/0** ✓
- **受管色彩子集 7/7 全绿**：`color-wiring 142/0` · `color-strategy 78/0` · `decision-delivery 87/0` ·
  `colorfmt PASS=2/FAIL=0/WARN=4` · `prophoto-faithful 27/0` · `gainmap-engine 140/0` · `color-engine-xcheck 全通过`；
  **探针 25/25 = 669 pass / 0 fail / 1 skip**（与改动前逐项一致）。
- **变异验证**：**M1**（`ColorTrc` 恒 `srgb`）⇒ ARW→AVIF 丢 PQ（`color_transfer=iec61966-2-1`）；
  **M2**（去掉 `|| rawIsTiff`）⇒ ARW→TIF 降 `bt709`。两次均原样撤回、无残留。
- **子集收口（非整轮）**：⚠ 单窗跑会被宿主在 ~24 min 回收 ⇒ 按 10/10/5/5 **分四窗**用 `-Scripts` 收口，
  四窗非零退出合计 **0**；但四窗日志**每份尾部自陈**「`-Scripts` 子集 N/65 ⇒ **不构成基线**」（分别为 10/10/5/5 条）
  ⇒ 诚实口径为「**子集 30–37/65 全绿，另有 28–35 条本轮未跑**」（把这些日志里实际产出 `exit=` 行的脚本去重统计 **≤37 条**）。
  日志 `tests/output/t84/{w1,w2,w3a,w3b}-2026-10-04.log`。
- **出包 + 验包 + L3 打包装复跑**：`pack.ps1 -Mode all` ⇒ exit=0；`verify-release-package` ⇒ **16/0**
  （⚠ 该证据出自 `tests/output/t84/verifypkg-fix-2026-10-03.log`，是 **10-03 的旧证据**，10-04 当天未重跑）；
  L3 五套 **17/0 · 100/0 · 28/0 · 13/0 · 19/0**（各自取到 `SUBJ(被测)=v1.6.0-beta4 (L3 出货包)`）。
- 文档：CHANGELOG 中英双语 · `docs/TESTING.md` 的 CsUi 判据行 · `MEMORY.md` 门槛 · 当日日志。
- ✅ **2026-10-04 结案：5 条 CS8602 已清零**（全量 `--no-incremental` 重编 = **0 警告 0 错误**）。
  - **病因（已查明）**：根因是 `QueueProcessor.cs:606` 的 `(captured.Options.Format ?? "")` ——
    `x ?? ""` 会让 Roslyn **反推 `Format` 可能为空**并向后传播；CS8602 的列号报的是**成员访问链起点**
    （`captured`），不是真正为空的 `Format` ⇒ 前四次「对着 `captured` 修」的尝试全部无效。
  - **定位过程（4 次判别，逐次排除）**：① 声明处 `var captured = item!;` ⇒ 无效；
    ② 显式 `if (captured is null) continue;` 守卫 ⇒ 无效；③ `captured.Options!.Format` ⇒ 无效；
    ④ **`captured.Options.Format!` ⇒ 该处告警消失** ⇒ 一度锁定为 `Format`。
  - **真相**：13 处 `!` 压制的是**幻影**警告（其中 **8 处从未告警**），`FfmpegOptions.Format` 声明为非空
    （`public string Format { get; set; } = "jpg";`）⇒ **无真实空引用路径**；「5 条 CS8602」是本轮 `:606` 自造的。
  - **修法（本轮已按此修复）**：① 删除 `QueueProcessor.cs:606` 的 `?? ""`；② 撤掉全部 13 处 `!`；
    四项目 Release `--no-incremental` 全量重编 ⇒ **0 警告 0 错误**。

### 7.6 用户显式目标路径复核 —— 又查出一处**真缺陷**（2026-10-04 已修）
- **缺陷**：RAW 默认块的用户判定只查 `UseAdvancedColorParameters` + `ColorPrimaries`/`ColorTrc`，
  **漏了非高级模式下的 `ColorSpace`**（CLI `--color-space sRGB`、GUI 友好名走的正是这条）
  ⇒ RAW 默认块**照样触发并覆盖用户选择**。实测 `--color-space sRGB` 交付 `bt2020 + sRGB`（用户要 bt709 原色）。
- **修法**：① 把**输入声明** `ColorSourceDecl*` 移出用户判定（**恒写** —— 用户选了目标时中间件**仍是线性**）；
  ② 用户判定补上 `ColorSpace` 这一维（`rawUserChoseTarget`）。
- **修复后实测**：`sRGB`→TIF 得 **BT.709 原色**（ICC 实测 `rXYZ=[0.4360,0.2225] gXYZ=[0.3851,0.7169] bXYZ=[0.1431,0.0606]`）
  且诚实报 `色彩损失=Clipping`；`BT.2020 PQ`→AVIF 得 `smpte2084`；未选 ⇒ 三档默认仍生效。

### 7.6.1 最终裁定（2026-10-04 续）：复选框语义 —— 修正 §7.6 的**中间态**
> ⚠ §7.6 记的是**中间态**（判据 = `UseAdvancedColorParameters ? CP/CT : ColorSpace` 二选一）。
> 用户当日给出**权威设计口径**，据此做了最终实现：
> - 「高级色彩参数 ⚙」复选框 = **纯 UI 开关**（面板可见性 / 能否选模式 2/3/4），**不参与色彩决策**；
>   勾选与不勾选**不得**造成策略差别。**模式 1** 允许三个下拉取 `auto`（走自动默认）；
>   **模式 2/3/4** 必须手动指定。RAW→RAW 不支持该模式。
> - 语义判据统一改为**值派生** `FfmpegOptions.HasExplicitColorParams`（CP/CT/Matrix 任一非空），
>   11 处读点不再读 UI 布尔 ⇒ **CLI 的 `--color-primaries`/`--color-trc` 从此真正生效**
>   （实测 ARW→TIF 得 BT.709，此前无论改不改都被交付 bt2020）。
> - 连带修：TIFF 目标 ICC 后置门控放宽（覆盖精确参数路径 + RAW 默认目标）⇒ ARW→TIF **默认档**
>   由「零色彩声明」变为 **BT.2020 ICC**（日志宣称 bt2020 而产物不声明 = 宣称≠交付）。
> 取证：`.workbuddy-ai/memory/2026-10-04.md` 的「P1-1 最终裁定与实现」节（含三格实测 ICC 色度）。

### 7.7 仍未做
提交 / tag / push（等授权）。

---

## 六、复现命令（可直接粘贴）


```bash
FF=C:/PLAN/ffmpegPictureUI/publish/PLAN/ffmpeg-full/ffmpeg.exe
DT=C:/PLAN/ffmpegPictureUI/publish/PLAN/artifacts/dngtool.exe
EXE=C:/PLAN/ffmpegPictureUI/publish/build/FFmpegPictureUI-v1.6.0-beta4-x64-full/FfmpegGui.exe

# 1) 复现线性中间件
"$DT" -d -T -o 0 -q 3 -W -H 1 -6 -i 1-2.ARW -O /tmp/1-2_raw.tiff
# 2) 复现应用的产物
"$EXE" --headless -i 1-2.ARW -o /tmp/out -f tiff --log-level Trace --log-file /tmp/trace.log
# 3) 看"直通"决策
grep -E "色彩引擎|cmd\] ffmpeg" /tmp/trace.log
# 4) 比亮度（产物应 = 中间件，且远低于 Sony 参照）
"$FF" -i /tmp/out/1-2.tiff -vf signalstats,metadata=print:file=- -frames:v 1 -f null -
```

---

# 附三：ARW→TIF「体积变大」的成因分解（2026-10-04）

> 触发：用户问「为什么 RAW 转成 TIFF 后体积变大了」。
> **结论：这是格式与处理链的结构性结果，不是缺陷。** 主因是**去马赛克** ——
> 每像素由 1 个采样变成 3 个采样，这一项单独就贡献 **×3**；两侧都未压缩、位深未虚增。

## 1. 实测字节账（同一对文件，同一次曝光）

| 项 | 1-2.ARW | 1-1.TIF |
|---|---|---|
| 文件总大小 | 49,579,614 B（47.3 MiB） | 144,072,694 B（137.4 MiB） |
| 像素载荷 | 48,674,304 B（占 99.13%） | 144,000,000 B（占 99.95%） |
| 非像素部分 | 905,310 B（含 743,200 B 内嵌 JPEG 预览 @144,034） | 72,694 B |
| 几何 | 6048×4024 | 6000×4000 |
| SamplesPerPixel | 1（Photometric=32803，CFA） | 3（Photometric=2，RGB） |
| BitsPerSample | 14 | 16（3 个分量） |
| Compression | 1（未压缩） | 1（未压缩） |
| 条带布局 | 1 条 @889344，RowsPerStrip=4024 | 4000 条 × 36,000 B，RowsPerStrip=1 |

两条恒等式（与文件大小逐字节吻合，无残差）：

- ARW 载荷 = 6048 × 4024 × 1 × 2 = **48,674,304**（14-bit 样本装在 16-bit 字里；
  抽样 2 万个样本最大值 8123 < 2¹⁴，证实确为 14-bit 而非 16-bit）
- TIF 载荷 = 6000 × 4000 × 3 × 2 = **144,000,000**，＋72,694 表头 = **144,072,694 = 文件大小**

## 2. 因子分解

| 因子 | ARW | TIF | 倍率 |
|---|---|---|---|
| 通道数 | 1 | 3 | **×3.0000** ← 主因 |
| 有效像素 | 6048×4024 = 24,337,152 | 6000×4000 = 24,000,000 | ×0.9861 |
| 位深（存储口径） | 14-bit ／ 2 B | 16-bit ／ 2 B | ×1.0000 |
| 压缩 | 无 | 无 | ×1.0000 |
| **乘积** | | | **×2.9584**（像素层） |

整文件比值 = 144,072,694 ÷ 49,579,614 = **2.9059**，略低于像素层的 2.9584；
差额来自 ARW 侧多出的 0.9 MiB 附带数据（表头 + 743 KB 内嵌 JPEG 预览）。

## 3. 对照边界（回答「为什么恰好是这个倍数」）

| 反事实假设 | 结果 |
|---|---|
| ARW 若为索尼**压缩** RAW（lossy ARW2，约 24 MB） | 比值升至约 **×6** |
| TIFF 若用 **8-bit** | 72,000,000 B ⇒ **比 ARW 还小** |
| TIFF 若用**无损压缩**（LZW/Deflate + Predictor=2，经验 30–50%） | 约 **86 MB** |
| TIFF 若不裁边（保留 6024×4024） | ＋1.4 MB（Sony 渲染器已裁到有效像素 6000×4000） |

⇒ 当前的 ×2.9 属于**最温和的一档**：两侧都未压缩、位深没有虚增、且渲染器还裁掉了边缘像素。

## 4. 与本仓产物的关系

本仓 RAW 路径的中间件同样是「dcraw 线性 16-bit RGB、未压缩」（见 §一.1），且**不裁边** ⇒ 产物比 Sony 参照更大：

- A7II（6024×4024）：6024×4024×3×2 = **145,443,456 B ≈ 138.7 MiB**，比 1-1.TIF 大 1.4 MB
- 实测 A7RM4 样本 `tests/output/results/raw2tiff.tiff`：**361,305,440 B = 344.6 MiB**
  （9504×6336×3×2 = 361,304,064，`Software = dcraw v9.26`）

⇒ 体积膨胀本身不是本仓的缺陷；但**裁边**与**是否启用 TIFF 无损压缩**是本仓可自主决定、
且能直接缩小产物的两个旋钮（当前均未启用）。

---

# 附四：这个 RAW 的「色彩实际覆盖范围」实测（2026-10-04）

> 触发：用户问「这个 RAW 文件的色彩实际覆盖范围有多大」。
> **结论要分两层**：① **传感器原生色域** ≈ **3.7–3.9 × sRGB** 的 xy 面积，覆盖可见色域的 **~85%**；
> ② **这张照片实际用到的颜色很小** —— 99% 的像素只占 sRGB 面积的 **21%**，
> 且 **99.94% 的像素落在 sRGB 之内**（超出者幅度 < 满量程 1.4%，属噪声量级）。
> ⚠ ② 只对**这一张**成立，不能外推成「RAW 不需要广色域」（见 §5）。

## 0. 口径：RAW 本身没有色域，能测的是两件事

沿用 §5.1 的结论（RAW = 场景参考 + 设备原生原色 + 线性，不属于任何标准空间）。因此：
- **可测 A**：**传感器/相机原生色域** —— 由相机矩阵定义，是「能区分多少颜色」的上界。
- **可测 B**：**这张照片实际用到的色彩范围** —— 由像素本身决定。

## 1. 测量链的建立与自证（关键：先把工具链钉死，再谈数字）

`dngtool -o <n>` 的语义**实测反解**（与 LibRaw `out_rgb[]` 表一致）：

| `-o` | 实测含义 | 取证 |
|---|---|---|
| 0 | **相机原色**（线性，已 WB） | 与 `-o 1` 相差一个真实矩阵，且该矩阵 = 复算 `rgb_cam` 的**转置**（maxdiff 0.037） |
| 1 | sRGB（线性） | `o1→o4` = `prophoto_rgb`（maxdiff 0.0087）；`o1→o5` = `xyz_rgb`（maxdiff 0.0488） |
| 4 | ProPhoto **D65**（线性） | 同上 |
| 5 | **XYZ** | 同上 |

**输出确为线性**（这是本附所有色度计算的前提）：`o4 = M·o0` 的自由 3×3 拟合残差仅 **0.042%**；
若假设 gamma（1.4 / 1.8 / 2.0 / 2.2 / 2.4）残差升到 **49%–85%** ⇒ 排除 gamma 编码。

**相机矩阵**：dcraw（`dcraw.c:8202`）与 LibRaw（`colordata.cpp:1782`）**两处独立来源**给出同一
ILCE-7M2 矩阵 `{5271,-712,-347, -6153,13653,2763, -1601,2366,7242}`（= Adobe ColorMatrix2 / D65）。

**白点自证**：该矩阵作用在 (1,1,1) 上得 xy = **(0.3127, 0.3290) = D65** ✓；
实测照片中位色度 xy = (0.3134, 0.3414)，同样贴近 D65 ✓。

**三口径互证**（解析 / 实测 o0→sRGB / 实测 o0→XYZ）算出的色域面积 **极差仅 3%**。

## 2. 参考色域在 CIE xy 上的面积（本次测量的标尺）

| 色域 | xy 面积 | 占可见色域 |
|---|---|---|
| 可见色域（光谱轨迹 + 紫边） | 0.3305 | 100% |
| sRGB | 0.1121 | 33.9% |
| Adobe RGB (1998) | 0.1511 | 45.7% |
| DCI-P3 D65 | 0.1520 | 46.0% |
| Rec.2020 | 0.2119 | 64.1% |
| ProPhoto (D65) | 0.2770 | 83.8% |
| ACES AP0 | 0.3956 | 119.7% |

## 3. A：传感器原生色域（三口径）

| 口径 | xy 面积 | × sRGB | × AdobeRGB | 占可见色域 | 覆盖可见色域 |
|---|---|---|---|---|---|
| A 解析（dcraw 流水线定义） | 0.4186 | 3.74× | 2.77× | 127% | 84.2% |
| B 实测 o0→sRGB | 0.4327 | 3.86× | 2.86× | 131% | 86.1% |
| C 实测 o0→XYZ | 0.4189 | 3.74× | 2.77× | 127% | 86.2% |

口径 A 的原色（xy）：**R (0.6606, 0.2872) · G (0.1416, 1.2285) · B (0.0478, −0.2146)**，白点 D65。
覆盖度：sRGB **100%** · Adobe RGB **100%** · DCI-P3 **99.0%** · Rec.2020 **98.5%** · ProPhoto **95.6%**。

⚠⚠ **必须点名的两件事**：
1. **G / B 原色是虚色**（y = 1.23 与 −0.21，远超可见色域边界 y∈[0.005, 0.834]）⇒ 「3.7× sRGB」
   这个面积**大部分由无对应可见光的虚色贡献**，不能读成「能拍出 3.7 倍的可见颜色」。
   有物理意义的读法是「**覆盖可见色域的 ~85%**」与「**完整包含 sRGB 与 Adobe RGB**」。
2. 原生色域**并不完整包含可见色域** —— 缺的那 ~15% 主要在**深红角**（相机的 R 通道在深红区响应不足）。
   这与 §5.3 的结论一致：相机原色**不能用 CICP/标准原色表述**。

## 4. B：这张照片实际用到的色彩范围（n = 970,025 采样，stride 5）

| 指标 | 实测 |
|---|---|
| 中位色度 xy | (0.3134, 0.3414)（≈ D65） |
| 色度范围 | x ∈ [0.1225, 0.4958] · y ∈ [0.1448, 0.5700] |
| 全部像素凸包面积 | 0.1051 = **0.94 × sRGB** = 31.8% 可见色域 |
| **中央 99% 像素凸包面积** | **0.0237 = 0.21 × sRGB = 7.2% 可见色域** |
| 超出 **sRGB** 的像素 | **0.0554%**（幅度：最大 −0.014 满量程，即 1.4%） |
| 超出 Adobe RGB | 0.0003% |
| 超出 DCI-P3 D65 | 0.0010% |
| 超出 Rec.2020 | 0.0001% |

**交叉验证（两条独立证据）**：
1. **与 Sony 参照 `1-1.TIF` 对照**：参照中位 xy = (0.3062, 0.3244)、凸包 0.0814 = 0.73 × sRGB、
   超出 sRGB 像素 **0.0000%** ⇒ 与本文产物结论同向。
2. **图像内容核对**（缩略图目视）：画面是**白纸 + 黑线稿为主**，仅少量紫/绿插画
   ⇒ **低饱和场景**，与「99% 像素只占 sRGB 的 21%」相符，排除测量伪影。

## 5. 对产品的含义

1. **就这一张而言**：交付 sRGB **不丢色**（99.94% 在内）；改交 Rec.2020 / P3 也不会更「对」。
2. ⚠⚠ **不可外推**：传感器原生色域确实远大于 sRGB（**3.7×**，且完整包含 sRGB/AdobeRGB）。
   高饱和场景（花卉、霓虹、夕阳、彩灯）会显著超出 sRGB ——
   本仓 §7 那条「RAW 默认 TIFF ⇒ Rec.2020 + SDR」的取向**依然正确**，本附不构成反例。
3. **可复现命令**（本附全部数字的来源）：

```bash
D=C:/PLAN/ffmpegPictureUI/publish/PLAN/artifacts/dngtool.exe
for o in 0 1 4 5; do "$D" -d -T -o $o -q 3 -W -H 1 -6 \
  -i "C:/Users/20210/Desktop/TEST/1-2.ARW" -O "tests/output/gamut/o$o.tiff"; done
# 之后：解析 16-bit 未压缩 TIFF → 按 §1 的矩阵关系转 XYZ → 统计 xy
```
⚠ `-o 0` 与 `-o 1` **不是同一个空间**（差一个真实矩阵），把 `-o 0` 当 sRGB 用会得到错误的色度。

---

# 附五：能否按内容自动选择 bt709 / bt2020？—— 可行性分析（2026-10-04）

> 触发：用户提出「分析 RAW 真实表达的色彩空间，然后自动选择 Rec.709 还是 Rec.2020」。
> **结论：技术上可行、判据干净、成本可忽略；但建议做成显式 `auto` 选项，不改为默认档。**
> 理由见 §四（代价不对称 + 批次一致性 + 可复现性）。

## 一、「RAW 真实表达的色彩空间」要拆成两层，只有一层能用来做选择

| 层 | 是什么 | 能否逐图决定目标色域 |
|---|---|---|
| **设备原生色域** | 相机矩阵决定，逐**机型**固定 | ❌ **不能**。附四已测：A7II 原生色域 ≈ 3.7× sRGB，且**完整包含 sRGB/AdobeRGB** ⇒ 从这一层看答案永远是「bt2020」 |
| **该图实际用色** | 逐图可测 | ✅ **能**。这是自动选择唯一可用的量 |

⇒ 自动选择只能基于第二层。⚠ 语义上它不是「RAW 是什么色彩空间」，而是「**这张图用到了多少**」。

## 二、判据设计：必须把「暗部噪声」与「真实内容」分开（本附的核心发现）

在 `tests/output/raw_samples/` 的 16 个不同厂商/格式样本上实测（13 个可解码；X3F、RPi.raw 解码失败）。
**总体出 bt709 的像素比例**：median **0.0585%**、max **2.72%**（PhaseOne）/ 2.24%（Pentax）。

⚠⚠ **但这个总数没有判别力。** 按亮度分位拆开后，成因分成**两类**：

| 样本 | 出 bt709 的亮度分布 | 成因 |
|---|---|---|
| 1-2.ARW（A7II，白纸黑线稿） | 只在最暗两档（0.268% / 0.224%），亮度 > 7 k 后**全为 0** | **暗部噪声**（不可见） |
| Canon_EOS40D · Sony_ILCE7S · Minolta · Samsung | 同上，全部集中在最暗档 | **暗部噪声** |
| **Pentax_istDL（黄色花卉）** | 最亮档 **20.98%**，次亮档 0.368% | **真实高饱和内容** |
| PhaseOne_P40 | 各档 1–5%，最亮档 0.01% | 混合 |

⇒ **判据必须带亮度地板**（建议：亮部 = 亮度 ≥ 99.9 分位亮度的 10–25%）。否则：
阈值放宽 ⇒ **切掉真实颜色**；阈值收紧 ⇒ 因噪声**永不选 bt709**，功能等于没有。

**加亮度地板后的决策表**：

| 样本 | 亮部出 bt709 | 亮部出 bt2020 | 应选 |
|---|---|---|---|
| 1-2.ARW（A7II） | **0.0000%** | 0.0000% | bt709 |
| Pentax_istDL（黄花） | **3.82%** | **0.211%** | bt2020 |
| PhaseOne_P40 | 1.74% | 0.0086% | bt2020 |

⇒ 两类被**干净分开**；且 **bt2020 是有效兜底**：最坏情形只切 **0.2%** 亮部像素（bt709 是 **3.8%**）。

## 三、实现形态（成本可忽略）

1. 在**已有的线性中间件**上抽样（stride N，约 40 万样本足够）⇒ **不需要额外解码**。
2. 中间件已声明为 bt2020/linear ⇒ 用**现成矩阵**转 XYZ→xy，**不需要相机矩阵**。
3. 只统计**亮度 ≥ 地板**的像素。
4. 判据（建议阈值，偏保守）：亮部出 bt709 **< 0.1%** 且 最大超出幅度（bt709 线性值的最小负值）**> −0.5% 满量程**。
5. ⚠ **决策必须落日志**（实测值 + 阈值 + 结论），否则就是静默行为改变。

## 四、为什么不建议改成默认（五条）

1. ⚠⚠ **代价不对称**：误判成 bt709 = **不可逆丢色**；误判成 bt2020 = 数据完好，只是非色彩管理查看器下偏淡（**可恢复**）。
   ⇒ 阈值必须偏保守，而这会把收益压到接近 0。
2. **阈值是判断题**：「切多少算可接受」是产品口径，不是技术事实。
3. **批次一致性**：本仓是队列批处理 ⇒ 逐图判会让**同一批产出混合色彩空间标签**。
4. **可复现性**：同参数不同图 ⇒ 不同结果，用户无法预测。
5. **「宣称 ≠ 交付」风险面**：新增一个可能静默改变交付语义的分支。

## 五、建议方案（按推荐度）

**A（推荐）默认档不动 + 报告实测**：保持 bt2020 默认，但在日志/UI 输出实测覆盖：
```
[色彩引擎] 用色实测：亮部出 bt709 = 0.0000%（99% 像素仅占 bt2020 面积的 9.3%）
          ⇒ 如需最大兼容性可显式指定 sRGB / bt709
```
用户拿到信息自己决定，管线**不做隐式切换**。

**B（可选）显式 `auto` 模式**：`--color-target auto`，判据与阈值写进文档，决策必须落日志。默认关。

**C 边界**：`auto` **只作用于 SDR 路径**；HDR 路径固定 bt2020 + PQ（PQ + bt709 原色虽合法但非标准组合）。

## 六、边界与教训

- ⚠ **分析只能基于「解出来的图」**，而解出来已经过了相机矩阵 ⇒ 结果受矩阵精度限制（dcraw 矩阵是折中解）。
- ⚠⚠ **单张样本的教训**：手上的 A7II 样本恰好是**低饱和场景**（附四：99% 像素只占 sRGB 面积的 21%）。
  若只看它就下「RAW 不需要广色域」的结论，**会被 Pentax 黄花样本直接证伪**（亮部 3.82% 出 bt709）。
  ⇒ 任何阈值都必须在**多机型样本集**上验证，不能靠单张。
- 复现：`tests/output/gamut/step10_batch.py`（批量判据）、`step11_noise.py`（亮度分位）。
  两者都用 `dngtool -o 4`（ProPhoto D65）解码后转 XYZ 统计，**不受 sRGB 裁切影响**。
