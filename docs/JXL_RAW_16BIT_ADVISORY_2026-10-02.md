# JPEG XL 光子噪声传递 + RAW/16-bit TIFF 转码管线 —— 建议性报告

> 日期：2026-10-02 ｜ 性质：**全程只读分析 + 实机验证**，未改任何源码、未 build、未提交
> 素材：`C:\Users\20210\Desktop\TEST\`（`DSC00118.TIF` 144 MB / `DSC00121.ARW` 49 MB / `DSC00123.ARW`）
> 工具链：`publish/PLAN/ffmpeg-full`（ffmpeg/ffprobe）、`publish/PLAN/jxl/bin`（cjxl v0.11.2 332feb1）、`publish/PLAN/exiftool`、`publish/PLAN/artifacts/dngtool.exe`

---

## 0. 结论速览

| # | 结论 | 严重度 | 证据强度 |
|---|---|---|---|
| **A** | **自动光子噪声 100% 失效**：代码只读 `PhotographicSensitivity`，而三种素材该标签**全为空**，`-ISO` 却能读到 400 | **P0** | 实机复现 |
| **B** | **ffmpeg libjxl 后端结构性不支持光子噪声**（AVOptions 仅含 effort/distance/modular/xyb），且丢弃时**无点名** | **P0** | 实机复现 |
| **C** | **质量→distance 映射与 libjxl 官方口径不符**，默认档下产物比官方同档小 **38%** —— 这是「无法正确压缩」主因 | **P0** | 实机复现 |
| **D** | 默认路径（ffmpeg→PPM 管道→cjxl）**元数据全丢**，且 exiftool **补不回** JXL | **P1** | 实机复现 |
| **E** | RAW 默认注入 `trc=linear`，但 dngtool 产物实为 **BT.709 gamma**；实测标注 linear vs sRGB 使体积差 **27%** | **P1** | 实机 + 历史文档 |
| **F** | 16-bit 传递链**本身是健康的**（PPM `MAXVAL 65535`、解码端 16-bit RGB）—— 位深不是问题 | 排除 | 实机复现 |

---

## 0b. 修复进度（同日已实施，构建 0 警告 0 错误）

| 项 | 状态 | 改动 | 验证 |
|---|---|---|---|
| **A 光子噪声 ISO** | ✅ 已修 | `ExifToolService.ReadIsoAsync` 改多标签保序回退（`-f` + `ISO → PhotographicSensitivity → ISOSpeedRatings → SonyISO → ISOSetting`） | 正控三种素材均读 400；负控（无 EXIF 的 JXL）全 `-` ⇒ null |
| **C distance 公式** | ✅ 已修 | `ImageEncoderArgs.MapJxlDistance` 换 libjxl 官方曲线；`CjxlService:424/437`、`FfmpegOptions:552/553`、`MainWindow:1472` **全部改为调用同一真值**（消灭 5 处副本） | 变异验证：改回旧公式 ⇒ 探针 `wire` 的 qa-① **转红**（87/1）；改回新公式 ⇒ **转绿**（88/0）。产物 q75：507 KB → **800 KB**（+57.6%），与官方 `-q 75` 只差 2.2% |
| **B 后端能力点名** | ✅ 已修 | `QueueProcessor`（cjxl ISO 解析之后）新增：后端非 Cjxl 且目标 jxl 且用户勾了噪声/渐进 ⇒ 写日志点名并指路 | 构建通过；UI 侧本就由 `JxlCjxlPanel` 可见性挡住，此条补 CLI/预设路径 |
| **D 元数据** | ✅ **已修**（引擎 cjxl 出口 SDR 走 PNG 中转） | `RawColorPipeline.EncodeJxlViaCjxlAsync` 双分支 | 实机端到端：`DSC00121.ARW → .jxl`（20 s）产物 **ISO=400 / ILCE-7M2 / DateTimeOriginal / LensModel 全保留**、XMP 2.7 KB 在、6024×4024 **16-bit** 不变、带 ISOBMFF 容器；门禁 verify-color-wiring **142/0** |
| **E 色彩标注** | ⚠ 刻意未动 | 未动 | `RAW_PIPELINE_AUDIT §3.5` 警告：当前错位是自洽的，单边改 `trc` 会破坏平衡 |

**新增探针判据同步**：`tests/ServiceProbe/Program.cs` qa-① 期望值 `10.5 / 1.5` → `6.4 / 1.0`。

---

## 1. 素材实测基线

| 项 | `DSC00118.TIF` | `DSC00121.ARW` | dngtool 中间 TIFF |
|---|---|---|---|
| 尺寸 | 6000×4000 | 6024×4024（解后） | 6024×4024 |
| 位深 | **16-bit**（`rgb48le` / exiftool `16 16 16`） | **14-bit** | **16-bit** |
| 体积 | 144 MB | 49 MB | 145 MB |
| EXIF ISO | **400** | **400** | **400**（已搬运） |
| `-PhotographicSensitivity` | **空** | **空** | **空** |
| `-ISO` | 400 | 400 | 400 |
| 色彩标签 | 全 `unknown`（无 primaries/trc/ICC） | — | 无 ICC |

关键：ffprobe 把 TIFF 识别为 **`format_name=tiff_pipe`**（pipe 型 demuxer），这是后续元数据探测能力受限的起点。

---

## 2. RAW / 16-bit TIFF → JPEG XL 管线如何运作

### 2.1 决定性事实：RAW 先变成 16-bit TIFF，之后一切都按「TIFF 输入」走

`RawService.cs:175` 的 dngtool 参数（`-d -T -o 0 -q 3 -W -H<mode> -6`）产出**线性（名义）16-bit TIFF**，随后 `QueueProcessor.cs:560` 执行 `captured.InputPath = rawTiff;` —— 输入被狸猫换太子。
⇒ **RAW→JXL 与 16-bit TIFF→JXL 是同一条代码路径**，用户直接给的 16-bit TIFF 只是省掉了第一步。

### 2.2 决策流程（判据与分叉点）

```
输入 (扩展名 ∈ RawService.RawExtensions, RawService.cs:23-45)
  │
  ├─ 目标 == "dng" ? ── 是 ──► ProcessDngAsync (QueueProcessor.cs:789)，跳过预处理
  └─ 否
     ├─ !RawService.IsAvailable ──► 抛错，不回退 (:530-535)
     └─ dngtool -d -T -o 0 -q 3 -W -H<m> -6     (RawService.cs:175)
        └─► 16-bit TIFF → captured.InputPath = rawTiff  (:560)
            └─► 注入 RAW 默认色彩 (:565-593)
                ColorPrimaries = 目标能承载 HDR ? bt2020 : bt709
                ColorTrc      = "linear"（恒）     ★ 见 §6
                │
                ▼  ★此后 = 普通「TIFF → jxl」流程★
   backend = options.EncoderBackend  (默认: 有 cjxl ⇒ Cjxl, EncoderDetectionService.cs:55)
   engineRoutable = ColorEngineRouter.ShouldRoute  (单点求值 QueueProcessor.cs:349-361)
      │
      ├─ engineRoutable ──► TryRunColorEngineAsync → RawColorPipeline
      │                       └─ EncodeJxlViaCjxlAsync (RawColorPipeline.cs:375)
      │                          ffmpeg -f rawvideo … -vf format=rgb48le -c:v ppm -
      │                          └─► cjxl - out.jxl   ← 走 BuildCjxlArguments
      │
      ├─ backend==Cjxl && !engineRoutable ──► ProcessCjxlAsync (QueueProcessor.cs:771)
      │     ├─ cjxl 直读文件 ── **TIFF 不在 cjxl 输入白名单** ⇒ 必失败
      │     └─ 回退 PipeFfmpegToCjxlAsync (:2197)
      │
      └─ backend==Ffmpeg ──► FfmpegCommandBuilder → ImageEncoderArgs.BuildJxlOptions
                              -distance/-effort/-modular，**不经 cjxl**
```

### 2.3 实测：整链能跑通，且很快

`dngtool(2.5s) → ffmpeg→PPM 管道 → cjxl`（6024×4024 16-bit）：

| 参数 | 产物 | 耗时 |
|---|---|---|
| `-d 3.8`（GUI 默认 Quality=75） | **507,495 B** | 2.7 s |
| `-d 0`（无损） | 76,308,012 B | 13.6 s |

⇒ **不是「编不出来」，而是「压得太狠」**。145 MB 源出 507 KB = 0.167 bpp，对 24 MP 照片属于严重过度压缩。

---

## 3. 问题 A：自动光子噪声 100% 失效（P0）

### 3.1 根因：exiftool 标签选错

`ExifToolService.cs:98`：

```csharp
Arguments = $"-s -s -s -PhotographicSensitivity \"{imagePath}\"",
```

**实机验证**（三种素材逐一跑）：

| 素材 | `-PhotographicSensitivity` | `-ISO` |
|---|---|---|
| 用户 16-bit TIFF | **（空）** | **400** |
| Sony ARW | **（空）** | **400** |
| dngtool 中间 TIFF | **（空）** | **400** |

`exiftool -a -G1 -s` 全量列出可见，Sony 只写 `[ExifIFD] ISO` 与 `[Sony] SonyISO / ISOSetting / BaseISO`，**从不写 `PhotographicSensitivity`**（该 tag 属 EXIF 2.3，Sony 未采用）。

### 3.2 后果链

`ResolveCjxlPhotonNoiseIsoAsync`（`QueueProcessor.cs:1963-1982`）拿到 `null` ⇒ 清除 `CjxlAutoPhotonNoise` ⇒ `CjxlService.cs:468-473` 走 fail-closed 分支，**省略 flag 并打日志**。
⇒ 用户勾了「自动读取 EXIF ISO」，拿到**一份没有任何噪声的产物**，且那行日志在半数调用点（`QueueProcessor.cs:3060/3073/4017`）根本没接 `log` 委托 —— 完全无声。

### 3.3 重要更正（推翻既有推断）

历史材料与本轮初判都假设「RAW 的 ISO 拿不到是因为 dngtool 中间 TIFF 不搬运 EXIF」。**实测否证**：dngtool 产物 `[ExifIFD] ISO = 400` 完好。真正原因是 tag 名选错，与中间产物换路径无关。

### 3.4 建议

把读取链改成**多标签回退**：`-ISO`（exiftool 复合标签，会回退 ISOSpeedRatings）优先，其次 `-PhotographicSensitivity`，再其次 `-ISOSpeed`/`-ISOSpeedRatings`，最后 `-SonyISO`/`-ISOSetting` 类厂商 tag。任一命中即返回。

---

## 4. 问题 B：ffmpeg 后端结构性不支持光子噪声（P0）

`ffmpeg -h encoder=libjxl` 实测输出：

```
libjxl AVOptions:
  -effort   <int>   1..9   (default 7)
  -distance <float> -1..15 (default -1)
  -modular  <int>   0..1   (default 0)
  -xyb      <int>   0..1   (default 1)
```

**无 `photon_noise_iso`、无 `progressive`、无 `intensity_target`。**
而代码侧 `ImageEncoderArgs.BuildJxlOptions`（`ImageEncoderArgs.cs:170-186`）也确实从不发射这两个参数，且 `UnsupportedEncoderSettings` 拒绝清单里没有它们 ⇒ **走到 ffmpeg 后端时用户勾的噪声/渐进被静默吞掉，连一句点名都没有**。

这正是本项目反复登记的同型坑（记忆 §4 模式 P9「降级不点名」）。

**建议**：后端选 Ffmpeg 且目标 JXL 时，UI 置灰光子噪声与渐进两项，并在命令预览里明确点名「ffmpeg libjxl 不支持」。

---

## 5. 问题 C：质量→distance 映射与官方不符（P0，「无法正确压缩」主因）

### 5.1 两套公式

| 来源 | 公式 |
|---|---|
| 本项目（`ImageEncoderArgs.cs:52` / `CjxlService.cs:437`） | `d = 0.15 × (100 − q)` |
| libjxl 官方 `JxlEncoderDistanceFromQuality`（q≥30） | `d = 0.1 + 0.09 × (100 − q)` |

### 5.2 实机对照（6024×4024 16-bit，同一份 PPM）

| GUI 滑块 | 实发 `-d` | 本项目产物 | 官方同档 `-q` | 官方产物 | 体积差 |
|---|---|---|---|---|---|
| 75（**默认**） | 3.8 | **507,495 B** | `-q 75` → `-d 2.35` | 817,623 B | **−38%** |
| 90 | 1.5 | 1,212,163 B | `-q 90` → `-d 1.0` | 1,708,670 B | **−29%** |
| — | — | — | `-q 95` | 2,994,747 B | — |

**副证**：`-q 90` 产物 1,708,670 B 与 `-d 1.0` **逐字节等长**；`-q 75` 与 `-d 2.35` 亦等长 ⇒ 官方映射确认无误，本项目公式偏激进。

小图（256×256）复核：`-q 90` = 8481 B = `-d 1.0` 8481 B，`-d 1.5` = 5749 B，**−32%**。

### 5.3 ⚠ 澄清：`-q` 在本节只是**标定基准**，不是建议下发的参数

先回答一个必然会被问到的问题：**JPEG XL 用的就是 `-d`（distance），最终下发的也一直是 `-d`，从来没有改成 `-q`。**
上面 §5.2 表格里出现 `-q`，是因为它是**测量用的参照物**：cjxl 的 `-q` 由 libjxl 内部用官方公式换算成 distance，
是最权威的「官方口径」样本 —— 我用它产出的体积来校验"我们的 `-d` 公式算出来的值是否与官方等价"。

**为什么不干脆直接传 `-q`**（这个念头在落地时被否掉了）：

| 理由 | 实测依据 |
|---|---|
| cjxl 的 `-q` 与 `-d` **互斥** | `cjxl --help` 原文：`Mutually exclusive with --distance.` / `Mutually exclusive with --quality.` |
| **ffmpeg 的 libjxl 编码器根本没有 `-q`** | `ffmpeg -h encoder=libjxl` 实测 AVOptions 只有 `-effort / -distance / -modular / -xyb` |
| ⇒ 若 cjxl 路径发 `-q`、ffmpeg 路径发 `-d`，两条后端**无法 A/B 对拍** | 本项目明确要求「切换管线时产物必须可 A/B 对拍」（`ImageEncoderArgs.BuildEncoderOptions` 注释） |

**结论：两条后端统一发 `-d`，把公式校准到与官方 `-q` 等价** —— 这正是 §0b 里 C 项做的事。

落地后两条路径的实际形态（探针 `wire` 实得串 + 源码）：

| 路径 | 实得命令行片段 |
|---|---|
| cjxl | `"-" "o.jxl" -e 7 --num_threads=16 **-d 2.4** --photon_noise_iso=400`（q=75） |
| cjxl | `"-" "o.jxl" -e 7 --num_threads=16 **-d 0.0**`（q=100 ⇒ 数学无损） |
| ffmpeg libjxl | `-threads 16 **-distance 6.4** -pix_fmt rgb24`（q=30）/ `**-distance 1.0**`（q=90） |

源码定位：`CjxlService.cs:425/438` 发 `-d`；`ImageEncoderArgs.cs:200/206` 发 `-distance`。

---

## 6. 问题 D/E：元数据与色彩标注

### 6.1 元数据在默认路径全丢，且补不回（P1）

- 默认路径末端是 `ffmpeg … -c:v ppm - | cjxl -`。**PPM 不携带任何元数据** ⇒ 实测 cjxl 产物无 EXIF box。
- 加 `--container 1` 也只是套了个 ISOBMFF 壳，**仍无 EXIF**（源头就没有）。
- **exiftool 写回 JXL 实测失败**：
  `Error: [minor] Will wrap JXL codestream in ISO BMFF container for writing` → `1 files weren't updated due to errors`。
  （与代码注释 `RawColorPipeline.cs:535-538`「exiftool 写 JXL 恒失败」一致）
- 反直觉对照：**ffmpeg libjxl 直编反而自动带 ISOBMFF 容器 + Brotli EXIF 29,934 B**，产物 49,694 B vs cjxl 19,797 B —— 那 2.5 倍差距**几乎全是元数据**，不是压缩能力差异（同输入对照下两者仅差 0.5%）。

**建议**：元数据敏感场景要么改用 ffmpeg 后端，要么在 cjxl 侧用 `-x exif=<二进制>` 注入（cjxl v0.11.2 支持该 hint；注意本轮尝试用 `exiftool -b -EXIF` 从 TIFF 提取得到 0 字节，需改用直接拷贝 EXIF IFD 的方式）。

### 6.2 RAW 默认标注与事实不符（P1）

`QueueProcessor.cs:585-586` 对 RAW 注入 `ColorPrimaries=bt2020|bt709`、`ColorTrc="linear"`（恒）。
但 `docs/RAW_PIPELINE_AUDIT.md:128-139` 已实测：dngtool 经 `convert_to_rgb()` 无条件执行 `gamma_curve(...)`，产物是**相机色域 + BT.709 gamma**，**不是线性**。

**实机量化影响**（同一份 16-bit 像素，仅改 color_space 标注）：

| `-x color_space` | 产物 |
|---|---|
| 无标注 | 8,481 B |
| `sRGB` | 8,483 B |
| `RGB_D65_SRG_Rel_Lin` | **6,158 B（−27%）** |
| `RGB_D65_202_Rel_Lin` | 6,496 B（−23%） |

⇒ **标注错误会直接改变编码行为**（cjxl 依据 TRC 决定量化策略）。体积差 27% 背后是暗部画质的实打实差异。

**好消息**（排除一个隐患）：cjxl v0.11.2 **接受** `RGB_D65_SRG_Rel_Lin` / `RGB_D65_202_Rel_Lin`（rc=0），不会硬失败退出——所以这不是「处理不了」而是「处理错了」。

---

## 6b. 「两条后端互斥」到底各丢什么 —— 逐项清单（2026-10-02 实测）

统一测试件：源 `DSC00118.TIF` 裁 1200×800 / `rgb48le` / `-d 2.4 -e 7`。
⚠ 修正一处早前误算：下表图像体积按**同一 distance** 对比（PNG 中转 38,413 B 与 PPM 路径 38,413 B **完全一致**；
早前拿 `-d 3.8` 的旧数 19,797 去比 `-d 2.4`，得出的"大 3 倍"是无效对比）。

### B1 · cjxl 后端（默认路径 `ffmpeg → PPM 管道 → cjxl`）

| 项 | 有/无 | 证据 |
|---|---|---|
| `--photon_noise_iso` | ✅ 有（A 项修复后真正可用） | cjxl `-h -v -v -v -v` 有此参数；探针实得 `-d 2.4 --photon_noise_iso=400` |
| `--progressive` | ✅ 有 | `CjxlService.cs:441` |
| `-x color_space=` / `--intensity_target` | ✅ 有 | 引擎规划层提供，实测 `RGB_D65_SRG_Rel_Lin` 等串均被接受 |
| `-x icc_pathname=` | ✅ 有（能力具备） | cjxl `-x` hint 支持 |
| `--modular` / `-e` / `--lossless_jpeg` | ✅ 有 | — |
| **EXIF** | ❌ **全丢** | jxlinfo 无 Exif box；`exiftool -ISO` 读空 |
| **XMP** | ❌ **全丢** | 同上 |
| 容器 | ❌ 裸码流（未传 `--container`） | 历史坑：曾致 `JxlInspector.DetectType` 误判（已修） |
| 事后补写 | ❌ **补不回** | exiftool 写 JXL 实测失败：`Will wrap JXL codestream in ISO BMFF container` + `1 files weren't updated` |

**根因**：管道中间载体是 **PPM/PAM**，该格式结构上不携带任何元数据 —— 不是 cjxl 的锅，是载体选错了。

### B2 · FFmpeg libjxl 后端

| 项 | 有/无 | 证据 |
|---|---|---|
| **EXIF** | ✅ **保留**（Brotli 29,662 B） | jxlinfo：`Brotli-compressed Exif metadata: 29934 compressed bytes`；`-ISO` = 400 |
| **XMP** | ✅ **保留**（2,309 B） | `exiftool -XMP` 可读出二进制 |
| 容器 | ✅ 自动带 ISOBMFF | jxlinfo 列出 `JXL ` / `ftyp` / `jxlp` box |
| `--photon_noise_iso` | ❌ **结构性不存在** | `ffmpeg -h encoder=libjxl` 仅 `-effort/-distance/-modular/-xyb` |
| `--progressive` | ❌ 结构性不存在 | 同上 |
| `--intensity_target` | ❌ 结构性不存在 | 同上 |
| `-x icc_pathname` / `-x color_space` | ❌ 无此 AVOption | 同上 |
| `--modular` / `-e` / `-d` | ✅ 有 | 四个旋钮里占了三个 |

### B3 · 第三条路（**已实测可行**）：PNG 中转

`ffmpeg → PNG（带 eXIf chunk）→ cjxl 读 PNG`

| 检查项 | 结果 |
|---|---|
| 位深 | ✅ 保住（`16-bit RGB`，与 PPM 路径一致） |
| 图像体积 | ✅ **38,413 B，与 PPM 路径逐字节同级**（无额外损失） |
| EXIF | ✅ 完整搬运（29,662 B，ISO=400 可读） |
| 容器 | ✅ cjxl 自动改出带容器产物（因有元数据） |
| 光子噪声 | ✅ 仍可用（仍走 cjxl） |

⇒ 即 **PNG 中转同时拿到 B1 与 B2 的"有"侧**，代价是：一次 16-bit PNG 临时文件（6000×4000 ≈ 144 MB）、多一次编解码。
⚠ **风险**：项目历史坑 —— PNG 表达不了 PQ/HLG 浮点，`docs/archive/JXL_ISSUES_HANDOVER.md` P0-2 记录的正是"HDR 经 PNG 中转削顶"。
RAW 的中间 TIFF 是 16-bit gamma（非真 HDR float）⇒ 本场景安全；**真 HDR 源须改用 PFM 中转**，不能一律 PNG。

### B4 · 第三条路（**不可行，需额外开发**）：`cjxl -x exif=` 注入

- cjxl 侧能力具备（`-x exif=` / `-x xmp=` / `-x jumbf=`），但**取不到合法 blob**：
  - `exiftool -b -EXIF <TIFF>` ⇒ **0 字节**（TIFF 没有独立的 APP1 段，EXIF 嵌在 IFD 结构里）
  - `exiftool -b -EXIF:all <TIFF>` ⇒ 61,153 B，但内容是**文本拼接**（前 48 字节为 `06000400016 16 1612…`），不是可注入的二进制
- ⇒ 要落地需自写 TIFF IFD 解析器重建 EXIF blob。投入明显大于 B3，除非有理由禁用 PNG 中转。

---

## 7. 已排除的怀疑（好消息，勿再投入）

| 怀疑项 | 实测结论 |
|---|---|
| ffmpeg PPM 编码器是否把 16-bit 写成 `MAXVAL 255` | **否**。实测头部 `P6\n256 256\n65535\n`，负载 393,216 B = 256×256×3×2 ✅ |
| 管道喂 16-bit 是否丢失位深 | **否**。`cat t16.ppm \| cjxl -` 产物 8,481 B 与文件输入**逐字节等长**，jxlinfo 报 `16-bit RGB` ✅ |
| cjxl 是否拒收 `RGB_D65_*_Rel_Lin` | **否**，rc=0 ✅ |
| dngtool 是否弄丢 ISO | **否**，中间 TIFF 有 `ISO=400` ✅ |
| RAW→JXL 是否编不出来 | **否**，18 s 出图 ✅ |

---

## 8. 建议修复顺序（1-3 已落地，见 §0b）

1. ✅ **A（已修）**：`ExifToolService.ReadIsoAsync` 改多标签回退，优先 `-ISO`。
2. ✅ **C（已修）**：换用官方 distance 公式并收敛到单一真值。实测默认档 507 KB → **800 KB**（+57.6%），与官方 `-q 75` 仅差 2.2%。
   - 采用「改公式」而非「直接传 `-q`」的理由：`-q` 与 `-d` 互斥，而 ffmpeg libjxl 后端**只吃 `-distance`**；统一发 `-d` 才能保证两条后端 A/B 对拍一致。
3. ✅ **B（已修）**：后端非 cjxl 时在 `QueueProcessor` 点名（CLI/预设路径），UI 侧本就由面板可见性挡住。
4. ✅ **D（元数据）已修**（同日实施，见 §0b 与 §6b）：
   - 引擎 cjxl 出口改为双分支：**SDR 且源在盘 → PNG 落盘中转**（`ffmpeg → <tmp>.png`（第二输入 `-map_metadata 1`）`→ cjxl <tmp>.png`）；**alpha → PAM 管道、真 HDR 目标 → PPM 管道**（均点名"源 EXIF/XMP 不保留"）。
   - PNG 落盘走 `FfmpegRunner`（-progress 心跳守卫），cjxl 走 `RunWithOptionsAsync`（新增 3 个可选 override 参数直透 `BuildCjxlArguments`）；临时 PNG `finally` 清理。
   - **实施前提（实测推翻了一条旧结论）**：`RawColorPipeline` / `verify-color-wiring.ps1` 旧注释「PNG 等容器输入 `-x` 被忽略」**已过期**——按 `#44` 的精确规则，决定因素是**输入是否自带色彩元数据**，与容器/裸流无关。ffmpeg 从 rawvideo 写出的 PNG **无色彩 chunk**（实测仅 IHDR/pHYs/eXIf/IDAT/IEND）⇒ `-x color_space=` 与 `-x icc_pathname=` 全部照常生效（bt2020+linear / sRGB / P3 ICC 三组对照，jxlinfo 读回与 PPM 逐字一致）。
   - 实机端到端验收（headless CLI，`DSC00121.ARW → .jxl`，20 s）：**ISO=400 / Model=ILCE-7M2 / DateTimeOriginal / LensModel 全保留**，XMP 在（xml box 2.7 KB），6024×4024 **16-bit** 不变，产物带 ISOBMFF 容器（Exif box 由库精简至 734 B，核心字段完整）。
   - 门禁 `verify-color-wiring.ps1` **PASS=142 FAIL=0**：含改后的 SDR-PNG 断言、新增"HDR 仍走 PPM"正控、新增"PNG 中转未失败"负控；原有 (10a)-(10g) 全场景（ICC/sRGB/PQ/alpha/PAM/photon）全绿。
5. **E（色彩标注）**：按 `RAW_PIPELINE_AUDIT.md §3.5` 的既有结论修正 `trc` 注入。⚠ 该文档明确警告：当前「意图与实现错位」是**自洽的**，照注释去「修正 linear」而不同步改源探测，会**破坏**现有平衡 —— 必须与源探测一起改，不能单边动。

---

## 9. 诚实边界（未验证 / 勿当结论）

- 本轮素材**只有 Sony 一家**（ARW + Sony 出的 TIFF）。`-ISO` 回退链对 Canon/Nikon/Fuji 是否同样命中未实测。
- **未验证 GUI 实际执行路径**：本轮是手工复刻代码会生成的命令，没有跑 GUI 本身（无实机队列日志取证）。「项目实际发的是哪条分支」仍应跑一次真实队列任务确认。
- **未验证 `-q` 直传后 UI 与预设的兼容性**（`CjxlPhotonNoiseIso` 等字段的历史预设回放口径）。
- 未验证 16-bit TIFF 输入（非 RAW）在引擎里探测不到色彩时的**实际默认标注**是什么 —— 素材三标签全 unknown，这条留待后续。
- 未做**画质客观评测**（SSIM/Butteraugli），体积差 38% 只作为「量化策略被改变」的强指示，不等同于画质差的定量结论。

---

## 10. 与本仓历史坑的对照（供后续判断「是否老坑复发」）

| 本轮问题 | 对应的历史模式 |
|---|---|
| A（tag 选错导致功能静默失效） | P9「降级/拒绝不点名」+ 新变体「单标签假设过强」 |
| B（参数在一条后端上根本不存在） | P8「空枪/死控件」 |
| C（两处各写一遍公式） | P1「同一份参数两处独立拼装」 |
| D（管道中间格式不带元数据 + 补写失败） | P4「中间产物格式偷换」 |
| E（标注与事实不符） | P6「宣称 ≠ 交付」 |
