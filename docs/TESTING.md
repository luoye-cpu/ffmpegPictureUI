# 🧪 FFmpegPictureUI 测试规范

> 版本: 1.0 | 最后更新: 2026-09-06 | 适用于 v1.6.0+

---

## 一、测试目录约定

**所有测试产生的文件必须放入 `tests/output/` 目录**，该目录已被 `.gitignore` 忽略，不会进入版本库。

```
ffmpegPictureUI/
├── tests/
│   ├── output/           ← ⚠️ 测试产物目录（.gitignore 忽略，不提交）
│   │   ├── *.jpg/png/jxl ← 生成的测试图片与编码产物
│   │   ├── *.icc         ← ICC 提取测试文件
│   │   └── *.log         ← 测试日志
│   └── scripts/          ← ✅ 测试脚本目录（可提交，可重复执行）
│       └── *.ps1         ← 自动化测试脚本
├── docs/
│   └── TESTING.md        ← 本规范
└── publish/PLAN/         ← 测试所需的工具（ffmpeg/cjxl/exiftool 等）
```

### 规则
1. **测试输入**：临时生成的测试图片 → `tests/output/`（如 `tests/output/src_*.png`）
2. **测试产物**：编码/解码输出 → `tests/output/`（如 `tests/output/result_*.jxl`）
3. **禁止**在仓库根目录、`src/`、`docs/` 下生成任何测试文件
4. **测试后清理**：测试结束后删除 `tests/output/` 下的临时文件（`.gitkeep` 保留）

---

## 二、测试环境准备

### 2.1 工具路径

测试使用 `publish/PLAN/` 下的工具（打包前的组件源目录）：

| 工具 | 路径 |
|---|---|
| ffmpeg / ffprobe | `publish/PLAN/ffmpeg-full/`（目录名可含日期变体，如 `ffmpeg-full-2026.7.24`） |
| cjxl / djxl / cjpegli / jxlinfo | `publish/PLAN/jxl/bin/` |
| exiftool | `publish/PLAN/exiftool/` |

> 提示：用 PowerShell 变量定位，避免硬编码具体目录名：
> ```powershell
> $ffmpeg = (Get-ChildItem publish/PLAN/ffmpeg-full*/ffmpeg.exe | Select-Object -First 1).FullName
> $cjxl   = "publish/PLAN/jxl/bin/cjxl.exe"
> $exif   = "publish/PLAN/exiftool/exiftool.exe"
> ```

### 2.2 生成测试图片

```powershell
$out = "tests/output"
New-Item -ItemType Directory -Force -Path $out | Out-Null

# 8-bit PNG
& $ffmpeg -y -f lavfi -i testsrc2=size=320x240:duration=0.1 -frames:v 1 "$out/src_8bit.png" 2>$null
# 16-bit PNG（高位深）
& $ffmpeg -y -f lavfi -i testsrc2=size=320x240:duration=0.1 -frames:v 1 -pix_fmt rgb48le "$out/src_16bit.png" 2>$null
# JPEG（含 ICC 写入测试）
& $ffmpeg -y -f lavfi -i testsrc2=size=320x240:duration=0.1 -frames:v 1 "$out/src.jpg" 2>$null
& $exif "-icc_profile<=C:\Windows\System32\spool\drivers\color\sRGB Color Space Profile.icm" "$out/src.jpg" 2>$null
# 带 alpha 的 PNG
& $ffmpeg -y -f lavfi -i "color=red:size=64x64:rate=1" -vf "format=rgba" -frames:v 1 "$out/src_alpha.png" 2>$null
```

---

## 三、核心测试矩阵

### 3.1 编码测试（→ JXL）

| 场景 | 命令 | 预期 |
|---|---|---|
| JPEG→JXL 无损重封装 | `cjxl in.jpg out.jxl -d 0 -e 3 --lossless_jpeg=1` | 输出 "lossless transcode" |
| JPEG→JXL 关闭重封装 | `cjxl in.jpg out.jxl -d 2 -e 3 --lossless_jpeg=0` | 正常有损编码 |
| PNG→JXL 无损 | `cjxl in.png out.jxl -d 0 -e 3` | 正常 |
| PNG→JXL 有损 + 光子噪声 | `cjxl in.png out.jxl -d 2 -e 3 -x "icc_pathname=..."` | 正常 |
| TIFF/WebP→JXL | 直接失败 → **必须走 ffmpeg PPM 管道** | 管道成功 |
| 16-bit TIFF + ICC→JXL | `ffmpeg -i in.tiff -pix_fmt rgb48le -f image2pipe -c:v ppm - | cjxl - out.jxl -d 2 -e 3 -x "icc_pathname=icc"` | 输出含 "ICC profile" |

### 3.2 解码测试（JXL → 其他）

| 场景 | 命令 | 预期 |
|---|---|---|
| JXL→PNG (djxl) | `djxl in.jxl out.png` | 正常 |
| JXL→JPEG (djxl) | `djxl in.jxl out.jpg` | 正常 |
| JXL 无损往返 | `djxl in.jxl out.png` 后对比 MD5 | 位深/像素保留 |

### 3.3 色彩验证

```powershell
# 检查 JXL 色彩语义（ICC 是否嵌入：rendering intent 应为 Perceptual）
jxlinfo out.jxl | Select-String "Color space"

# 检查像素格式/位深
ffprobe -v error -select_streams v:0 -show_entries stream=pix_fmt -of csv=p=0 out.png
```

### 3.4 色彩 / 增益图专项回归（改色彩管线必跑）

| 脚本 | 覆盖内容 | 关键断言方式 |
|---|---|---|
| `run-color-regress.ps1` | 一次跑完下面所有 `verify-*.ps1` 并汇总 | 各脚本自身的 `PASS=n FAIL=n` |
| `run-pipeline-tests.ps1` | 主编码/解码/后端矩阵 | 80 项，含 PSNR/SSIM 阈值 |
| `verify-color-strategy.ps1` | 4 种色彩策略 × {SDR/广色域/HDR 源} × 4 格式；**2026-09-17（S6）起还含「两条管线的裁决对照」** | 输出非空 + 记录 CICP/ICC 供比对；**71/0**（2026-09-26 17:35 整轮实测；演进 58/0（S7）→ 63/0（S6）→ **70/1**（09-26 新增 ③f「位深播报锁」+8 条，其中 F3 故意留红，钉 #35 那笔"webp/avif 位深升降零播报"的账）→ **71/0**（同日 #35 修完：请求档穿过两处钳制带进规划层，**判据一字未放宽**，红的成因改为 F0a–F0e 五条合成自测 + F2 真日志负控来咬）→ **77/0**（同日深夜 #39/#41 加 F4/F5 共 6 条：F4 `avif + Display P3 + --bit-depth 12` 必须**真交付 12bit**、F5 请求 16 必须落到编码器上限 12 且恰好播一次；钉的是**删掉"AVIF+P3 高位深 ⇒ 8bit"无据特例之后**的新契约 ⇒ 是**收紧**不是放宽，特例加回来 F4 立刻红。⚠ F4 的出口 `-pix_fmt` 必须取日志里**最后一个**匹配：引擎路线前面那条 `rgb48le` 是喂编码器的中间件格式，取第一个会把正确产物判成红 —— 本轮实测踩过。读数 `tests/output/t39/strategy_after2.log`，DLL `1881E2F76539CAEF`））。S6 新增（+5）：③d(d) **legacy 在 Reject 格同样拒绝**（非零退出码 + `Reason` / `建议` / `可替代方案：Kind/Value`）；③e **裁决 parity** —— 同一输入**真实 CLI 跑两遍**（默认引擎 / `--color-engine legacy`），逐格比 `Verdict.Kind` **且比结局**（`Reject` ⇒ 零产物；非 `Reject` ⇒ 有产物），外加**覆盖格数下限 12**（防"两边都读不到 ⇒ 全 skip"的空断言）。⚠ 判据一律是**稳定枚举**（`VerdictKind` / `DegradationCodes.*` / `AlternativeKind`），禁止自由文本；⚠ ② 段 `expectReject` 由**读裁决**决定、不再硬编码（详见 §6 第 79 条） |
| `verify-widegamut-regression.ps1` | ⚠ **只验「广色域源在各格式/各目标下能否产出非空产物」**（18 条 = 18 格产物存在性） | **仅看产物存在且非空**；`ffprobe` 读到的 CICP **只回显不判**。**不做任何像素级断言**（既不验"不偏色/不削色"，也没有 ICC 度量与亮度统计）⇒ 广色域保真/削色必须看 `verify-prophoto-faithful.ps1`（raw 样本逐位一致）与 `verify-color-wiring.ps1`（PSNR/ICC 一致性） |
| `verify-format-regression.ps1` | 各格式能力约束（钳位/置灰/失败旁路） | 退出码 + 产物尺寸 |
| `verify-prophoto-faithful.ps1` | **ProPhoto 像素原样 + 仅 ICC 保真路径** | 输出与源 **RGB 样本逐位一致**（raw 哈希）、ICC 描述=ProPhoto、无误导性 CICP、AVIF 不误用保真、生成 ICC 色度 == 已发布 ProPhoto(D50) 矩阵 |
| `verify-gainmap-managed.ps1` | **GainMap 纯托管编解码（ISO 21496-1 / MPF）**；⚠ **「BT.2100 PQ HDR 底」契约已从产品移除**（2026-09-19 核实：`JpegGainMapBaseHdr` / `jpeg-gain-map-base-hdr` 在 `src/` 下**零引用**；`GainMapEncoder` 恒写 `BaseRenditionIsHDR=False`）—— 该脚本现断言废弃开关**为 no-op** | ISO 数值 == **可推导期望值**（`max == log2(1000/203) ≈ 2.3005`，容差 0.05；⚠ 原「与 exiftool 的 hdrgm XMP 交叉校验」已改为**推导期望值**）、`min=0 / gamma=1 / channels=1` 符合单通道 SDR 底契约、MPF 副图偏移命中 SOI、普通 JPEG 不误判、带/不带废弃开关的产物降级后 PSNR ≥ 50dB。**20/0**（2026-09-19 实测复核） |
| `verify-gainmap-engine.ps1` | **GainMap 走色彩引擎出口的专项门禁**：引擎决策/引擎出口（含「显式 `legacy` + GainMap 仍走引擎」）、`--jpeg-gain-map-base-gamut` 与 `-multi-channel` 透传、预缩放不得静默退化为 SDR、tonemap 误伤修复、GainMap + 最长边缩放新契约、`isHdrInput` 判据换源、`--jpeg-progressive` 三态 × GainMap 双路径（§10 结构锁） | 真实 CLI 端到端 + **第三方可证**判据（不靠产品日志散文）：`exiftool` 读底图**内嵌 ICC 色度**（bt2020 红原色 **0.70799 0.29201** vs 默认 sRGB **0.64 0.33**）· `exiftool` 读 XMP hdrgm `Channels`（multi=**3** / 默认=**1**）· ffmpeg 解**主图 raw 比 MD5**（证明 bt2020 底是**真像素转换**、非只改标签）· 非法 `--jpeg-gain-map-base-gamut bogus` ⇒ **exit 2 + 零产物 + 点名「取值无效」** · `isHdrInput` 换源：cICP=PQ + 内嵌 sRGB ICC ⇒ 实测走 **SDR**（`GainMapMax=0.00`），去 ICC 对照 ⇒ **HDR**（`2.30`）—— ⚠ 该行为与 PNG 第三版规范（cICP 优先级 1 > iCCP 2）**矛盾**，属**疑似缺陷**，断言锁的是**当前行为**（详见 §6 第 52 条后的更正）。**117/0**（2026-09-19 实测；新增 §10「`--jpeg-progressive` 三态 × GainMap 双路径」**+33 条** ⇒ **84 → 117**；**7 组变异全部有牙**；**PS 5.1 与 PS 7 双宿主各一次，均 `PASS=117 FAIL=0`**）**；#20(0919)（心跳判据锁）后整轮实测仍 `117/0`**（源 `tests/output/step3-round-1127.log:524`；✅ 有效性前置**已满足**：整轮汇总 `全部通过` ⟺ `failedItems` 为空 ⟺ 源码漂移检查通过（双路互证），详见 §6 基线行） |
| `verify-gamut-map.ps1` | **色域映射（GMO，`--color-gamut-map`）的端到端门禁**（**36/0**；2026-09-17 实测更正，原写 26/0） | 引擎 `off` 与**默认（不传）**产物**逐字节相同**；`on` 与 `off` **不同**且决策行 `ColorLoss=GamutCompression` + `H1(需 GMO 色域压缩)`；**no-op 负控**（sRGB→Display P3：未越界但**确实发生映射**）`on`≡`off` **且不得启用 GMO 后端**；**显式 `legacy` + `on` ⇒ 硬失败并点名且不产出**（对照 `legacy`+`off` 正常产出）；默认 `auto` 下引擎不可走时**只告警、不拦任务**；非法取值 `yes` ⇒ 硬报错不产出 |
| `verify-webp-hdr-fix.ps1` / `verify-tiff-icc.ps1` / `verify-remaining.ps1` | TIFF ICC 策略、WebP HDR 降级、DNG→各格式 | 人工读数脚本（无硬断言，看 EXIT/尺寸/YAVG）；⚠ 其中 `verify-tiff-icc` / `verify-webp-hdr-fix` **2026-09-19 起被运行器打 `← 表格型：无 PASS/FAIL 契约` 标签**（与 `verify-colorfmt-matrix` 同列，见 §6 第 62 条）—— **不含**同行的 `verify-remaining.ps1`（它**不在** `$tableOnly` 内） |
| `verify-decision-delivery.ps1` | **步骤 3 单一裁决点的交付端门禁**：广色域/tonemap 后跑真实 CLI，逐项读**产物**；还覆盖**专用通路**（现造带 P3 ICC 的 `.jxl` 走 `ProcessJxlInputAsync`）、**同源结构锁**（直接调 `BuildArguments` 的位置必须恰好三处，新开通路不登记就红）、以及“两条路径像素必须相同”的 P7d 回归锁 | 产物 ICC 色度量得（P3 红 x=0.680 / sRGB 0.640）、ffprobe 读回 cICP 与出口语义一致、“该不该后置嵌 ICC”与容器实测一致；专用通路不得静默交无标注产物；同一内容走主分支与 JXL 管道的 webp **像素逐字节相同**；P3 目标格以**全帧 PSNR** 证明像素**真的**落到 P3 域 —— 判据是**相对差**（按 P3 解 − 按 sRGB 解 **≥ 15dB**；饱和素材实测 38.196 − 14.146 = **24.05dB**，余量 ~9dB；只贴标签不动像素时差 ≈ 0）；⚠ 该判据**要求素材含饱和色**（当前 `sdr_plain.png` 96.41% 的像素色差 ≥128），换素材须同步复核，否则会**假红**（详见 §6 第 57 条 ④）。2026-09-17 由「匹配 zscale 命令行」改为像素/色度度量（该格默认走引擎 H1，命令行里没有 zscale）；跑不通的路径只能 SKIP、不能给绿（共 **81** 项、0 SKIP。条数演进：**51**(09-17) → 59(步骤3 收口轮) → 63(09-26 #26② 的 ⑮「链末曲线==产物标签」锁) → 73(09-26 #36 的 ⑯「AVIF 出口位深==实际会跑的编码器能力」锁，10 条：2 条取外部权威/尺子自证、5 条量产品交付、3 条结构锁) → **77**(09-27 零点 #39 的 ⑰「HDR 源 → AVIF + 显式广色域目标」锁，4 条：交付必须还是请求的 12bit、primaries 必须是 smpte432、换成 sRGB 目标必须读回 bt709（对照组，防"恒等于某串"）、trc 不许仍是 PQ/HLG）。→ **81**(09-27 #43 的 ⑰b「同一组合改走 `--color-engine legacy`，两路一起锁」4 条：legacy×显式 P3 必须出货（改前是 `exit=1` + 只挂 iccgen + 零产物）、产物必须 `smpte432` + trc `iec61966-2-1`、legacy×sRGB 对照必须读回 `bt709`、**legacy×auto 命令行不许出现 tonemap 且 trc 必须仍是 `smpte2084`** —— 最后那条是本次唯一能抓住"豁免谓词写反"的读数，见 `docs/COLOR_MATRIX_FIX_PLAN_2026-09-24.md` 的 #43 收口节)。⚠ ⑰ 当年只锁能出货的引擎路，是因为同组合下 legacy 本就硬失败（只挂 iccgen 不走 tonemap ⇒ ffmpeg "Not yet implemented"）——那是 #43 的缺陷，**修好后由 ⑰b 把两路一起锁上**，不是把失败焊成"必须失败"。⚠ ⑯ 的三条结构锁**匹配前先剥整行注释**，否则解释修复的注释自己会把门禁判红（本轮踩过）；09-17 原写 44 项：新增 ⑪ 组＝真透明动图两步法的帧数守恒 + alpha 交付 + 两条素材对照组，另有 P7f/P7g/P7h 结构锁） |
| `verify-color-engine-xcheck.ps1` | **引擎 vs 外部参照（libzimg/zscale）逐像素互测**：2 图 × 5 组转换 = 10 格 | 每格 `PSNR ≥ 55dB` / `maxDiff ≤ 1500/65535` / `峰值 ΔE*ab ≤ 1.5`；6 格**已知非缺陷**（zimg 参照口径分歧）走显式白名单豁免，其余 4 格坏了照常红。**退出码语义**：除白名单外任何一格失败 ⇒ `exit 1`（2026-09-17 前全文无 `exit` ⇒ 恒 `exit 0`，详见 §6 第 58 条） |
| `verify-colorfmt-matrix.ps1` | 跨格式「目标 = Display P3(SDR)」可用性矩阵：png/jpg/webp/tiff/jxl/avif 六格 —— ICC / CICP 到底有没有进产物（判据全部来自第三方工具 exiftool/ffprobe/jxlinfo，不用产品自己的解析器自证） | **表格型：无 PASS/FAIL 契约**。它只打印对照表（`✅ ICC=P3` / `⚠ 有 ICC 但非 P3 参照` / `❌ 产物无任何 P3 描述`）；全文的 `exit 1` 只用于「缺 exe/ffmpeg/ffprobe/exiftool」与「源生成失败」这类**前置条件**，**不用于断言** ⇒ `exit=0` 只表示「跑完了」，**不等于**断言全绿。它的价值是**给出第三方读数**（其 584B 读数曾被拿去与 `verify-color-engine-xcheck` 的失败项交叉印证，见 `docs/archive/AGENT_TASK_SPLIT.md:48`）；**不要**给它硬造断言 —— 尤其「有 ICC 但非 P3 参照」需人判断（独立生成的合法 P3 ICC 未必与参照逐字节相同，硬断言会误伤）。运行器 `_run-step3-gates.ps1` 已显式标注它（**2026-09-19 起共 4 条**：本条 + `verify-tiff-icc` + `verify-webp-hdr-fix` + `_probe-jpg-p3-icc`，详见 §6 第 62 条） |
| `verify-ps-compat.ps1` | **门禁脚本自身的宿主兼容性** —— 防「门禁跑不起来 = 假绿」 | 语料 ≥30 个 `.ps1`；逐文件用**当前宿主**解析器验证可解析；剥离字符串/注释后正文不得出现 PS7+ 专属语法（三元 `? :` / `??` / `?.` / `&&` / `\|\|`）；8 条负控证明探针有牙且**不误伤**字符串/注释/here-string；点名回归锁 `verify-color-token-normalize.ps1` 与 `_probe-icc-affects-decode.ps1` |
| `verify-color-token-normalize.ps1` | 色彩 token 规范化（CLI 别名 / GUI 矩阵词表 / 输入矩阵白名单） | A–E 五节；不可用矩阵必须**出声**且**不失败**（此前是静默忽略或 -40 零产物）；GUI 词表每个选项都必须在 `FfmpegInputMatrixNames` 内（"提供了但不可用"即红） |
| `verify-hlg-route.ps1` | HLG 走 OOTF 分支的**接线级**覆盖（类级绿 ≠ 接线级绿） | 素材自检（两源 md5 必须**不同** + ffprobe transfer 分别为 arib-std-b67/smpte2084）；HLG→SDR 与 PQ→SDR 峰值不同且 PSNR **有限**；同源两次 ⇒ PSNR=inf（控制组） |
| `verify-geometry-engine.ps1` | **色彩引擎「最长边几何缩放」路径**（`RawColorPipeline.cs:94-129`）—— ⚠ 该段在**默认路径上是死代码**：`QueueProcessor.cs:429-496` 先预缩放并改写输入（`:487`）⇒ 引擎拿到时**已不超限** ⇒ 引擎那段**永不执行** | 只跑 `ServiceProbe geometry`（**直调引擎**、绕过 `QueueProcessor` ⇒ 那段才真的跑到），并对探针的**结构化行**独立复读（不只看 `PROBE RESULT`）：5 个用例的**产出尺寸**与 **Stats 反推尺寸**都必须等于实测期望（含纵向与**非整数边**分支）、负控（不超限）尺寸不变且日志**不得**出现 `几何缩放（最长边限制）`、引擎缩放尺寸 == `QueueProcessor` 预缩放尺寸、**像素级**「今天(预缩放→映射) vs 删后(引擎内缩放→映射)」PSNR ≥ 40dB（实测 61.55dB / maxDiff 280/65535）、探针自清（GUID 目录必须消失）。⚠ 边界坑：512x384 套 `scale=-2:32` 实测 **42x32**，自己算 42.67 会得 43/44。共 **23/0**（配套探针 `geometry` **52** 条；⚠ 原记 **35**，2026-09-18 由 `gainmap-merge-partA` 实测复核更正 —— 专项门禁自身 **23/0** 复核仍准确） |
| `verify-geometry-orientation.ps1` | **取向标签（TIFF `Orientation`）下的「显示尺寸」口径**——ffmpeg 会 autorotate，而探测读的是 coded 尺寸 ⇒ 引擎把旋正后的 raw 按未旋正尺寸标注 ⇒ **产物按行错位重排** | 只跑 `ServiceProbe orientation`（**直调引擎**）。夹具自检三针：coded 必须 64x32 而 `rotation` 必须 90（两口径**轴向不同**才有效，方图/180° 都会让门禁退化成恒真）、负控夹具必须 `rotation=0`、参照臂（ffmpeg 直解 PNG）必须**不带** rotation（否则对拍的是「两次旋转是否抵消」）。判据：`ProbeSizeAsync` 报 32x64 而非 coded 64x32、引擎产出几何 == 显示尺寸、16-bit 出口与独立参照 **psnr=inf**（逐字节等）、8-bit 出口 ≥45dB（实测 58.38）、`+最长边限制 16` 时滤镜必须是 `scale=-2:16`（纵向）且产出 8x16、负控不得凭空旋转。共 **18/0**（配套探针 **15** 条）。⚠ 变异验牙：把 `ImageGeometry.ApplyRotation` 改恒等（= 退回 coded）⇒ **7 条 FAIL**，读数含 `engine=64x32`、`psnr=图不可比:Width and height of input videos must be same`、`filter="scale=16:-2"`（锁错边 ⇒ 产物 16x32，最长边**突破**用户上限） |
| `verify-gainmap-host.ps1` | **把 `tests/GainMapTestHost` 全量套件接进清单**（第 60 条，2026-09-30）—— 此前该宿主的 exe **不被任何门禁执行**（运行器自己登记着这条缺口）⇒ 09-20「SDR ⇒ 普通 JPEG」裁定作废的 4 条 `sdr` 格期望在树里**红了 10 天**，只有手工跑宿主才看得见 | 跑宿主全量（A 编码 / B 解码闭环 / C 结构 / D PNG3.0 / 像素内核），判据不只看汇总：① `通过 N 失败 0 已知问题 0` 且 **N ≥103**（基线演进 86 →（2026-10-01 补取向标签 E1 九条 + E2 八条）103；掉了=有用例被跳过/删）；② **汇总行之前**的逐条 `❌` 数必须等于失败数（宿主在汇总后会把失败清单**重打一遍** ⇒ 全文计数恒为 2×，见 `Program.cs:71-73`）；③ 零余量（SDR）契约的 **6 条逐条点名在位** + 一条**反向锁**（`sdr` 格不许再断言 `MPImage2 存在`）；④ 取向标签（`Orientation=8`）的 **4 条承重断言点名在位**（`E1f` 解码器报显示尺寸 / `E1i` 显示几何 PSNR≥40dB / `E2c` 不得报「分辨率不一致」 / **`E2g2` 反证臂：轴向真不一致仍必须被拒 ⇒ 挡住「用放宽判据换绿灯」**）；⑤ exe 缺失即判红，并打印被测产物与构建类型。**18/0**。⚠ 变异验牙：把宿主两处 `hdrPeak <= KSdrWhiteNits` 分支禁掉 ⇒ **PASS=3 FAIL=11**，且 ② 当场报出 `失败数 4`、`通过数 80`（= 回到遗留前的状态），反向锁也响；还原以 md5 自证 |
| `_run-step3-gates.ps1` | 一次跑完所有 `ServiceProbe <模式>` 门禁 + 主要 `verify-*.ps1`，只回显汇总行与失败明细 | `-Probes a,b,c` / `-SkipScripts` 可只跑子集 |
| `_lib-color-assets.ps1` | ⚠ **共享库、非门禁**（不产 PASS/FAIL、**不登记进** `_run-step3-gates.ps1` 的脚本清单） | 只在 dot-source 时定义 `New-P3IccAsset`（ffmpeg `zscale+iccgen` 造 P3 PNG + exiftool 提取 `p3.icc`）；**无顶层副作用**（否则谁 dot-source 谁跑一遍素材生成）。存在理由是消掉「一条门禁依赖另一条门禁的遗留产物」：`verify-color-caps.ps1` 与 `verify-color-wiring.ps1` 两边**都调用它自备**，不新增第二套生成口径（详见 §6 第 64 条） |

> 这些脚本均依赖 headless 的项级日志，因此必须带 `--log-level debug`（脚本已内置）。
> 新增色彩分支时，**优先往这些脚本里补一条断言**，而不是只修代码。
>
> ⚠ **日志标签分工：`[cmd]` / `[ffmpeg-meta]`（命令行回显）与 `[色彩裁决]`（裁决回显）不是一回事，不要混用**
> （2026-09-17 厘清「命令行回显族」，`hdr-sdr-primaries-fix`；2026-09-17 新增「裁决回显族」，S6）：
>
> **① 命令行回显族 —— 载荷是 `<exe> <args>`**
> `[cmd] ffmpeg …` = **转码**（真的跑了一遍解码/滤镜/编码）；`[ffmpeg-meta] ffmpeg …` = **重封装**
> （`-c copy`，只搬容器与元数据）。**两者都可观测**（都写进 `item.Log`），断言时**按语义选**：
> 验「色彩变换真的执行了」用前者，验「元数据被搬运」用后者。`QueueProcessor.cs:1296` 的
> `[ffmpeg-meta]` **有意不统一**成 `[cmd]` —— 标签差异本身就是信息（一眼可辨转码 vs 重封装）。
> 另：`[cmd] ffmpeg` 现已覆盖全仓 **25 处** `await FfmpegRunner.RunAsync`（2026-09-17 补齐最后 14 处；
> 2026-10-02 第 25 处 = U1 后半给 float JXL 补的"ffmpeg 直读"回退支，它同样先写 `item.Command` 再打 `[cmd]`。
> ⚠ 这个数由 `verify-ffmpeg-heartbeat.ps1` ① 的结构锁钉住 ⇒ 新增调用点必须**同时**改那一处断言）。
> 此前 `ProcessJxrAsync` 只写 `item.Command`（UI 字段、**不进日志**）⇒ 门禁按 `[cmd]` 去取只能拿到空串，
> 这正是「必红断言」的**土壤**（同一概念在同一代码库里只记一半），详见 §6 第 65 条。
>
> **② 裁决回显族（本轮新增）—— 载荷是规划层产物，不是命令行**
> `[色彩裁决]` = **裁决回显**：打 `Kind` / `Degradations` / `Alternatives` 等**规划层**结论。
> ⚠ 它**不是**命令行回显族的「第三个成员」—— 载荷里**没有 exe、没有 args** ⇒ 不要按「哪条命令」去理解它。
> 5 种载荷形态（`QueueProcessor.cs`；⚠ 标签字面量**已冻结**，见下）：
> （⚠ **行号是快照、不是稳定引用**：下列 `QueueProcessor.cs` 行号取自 **2026-09-18 00:33:29** 的那一版
> （S6 落地后；核对于 `00:44`）—— 代码一改就漂 ⇒ **引用时以「锚点文本」为准，行号只作定位**，见 §6 第 78 条。）
> - `[色彩裁决] 裁决={Kind}` —— 引擎 `:905`、legacy `:614`（锚点：`plan.Verdict?.Kind` / `legacyVerdict.Kind`）
> - `[色彩裁决] ⚠️ 降级（{Code}）：{Text}` —— legacy `:618`（锚点：`legacyVerdict.Degradations[i].Code`）
> - `[色彩裁决] ❌ 契约拒绝（不出产物）：{Reason}` —— legacy `:621`（锚点：`legacyReject`）
> - `[色彩裁决]    建议：{Advice}` —— legacy `:623`（锚点：`legacyVerdict.Advice`）
> - `[色彩裁决]    可替代方案：{Kind}/{Value} —— {Why}` —— legacy `:625`（经 `LogAlternatives(…, "[色彩裁决]")`）
>
> ⚠ **只有 `[色彩裁决] 裁决={Kind}` 这一行是「两管线同标签 + 同格式」**（引擎 `:905` 与 legacy `:614` 都打）
> ⇒ **parity 断言可以直接比字符串**：`verify-color-strategy.ps1:174` 就是按
> `'\[色彩裁决\] 裁决=([A-Za-z]+)'` 取的（函数 `GetVerdictKind` 在 `:172`）。
> **判定点**（真正读裁决行的四处）：`:140`（无输出格必须给出 Reject 裁决）/ `:215`（`ExpectRejectFromVerdict`）/
> `:369`（legacy 拒绝断言）/ `:391`·`:393`（parity 循环逐格取 Kind）—— ⚠ `:146` 只是 **FAIL 文案行**、
> `:379` 是**注释行**，都**不是**判定点。
> ⚠ **其余 4 行是「载荷格式逐字一致、但标签按管线而异」**：引擎侧打 **`[色彩引擎]`**
> （`:912` 降级 / `:915` 契约拒绝 / `:916` 建议 / `:920` 可替代方案 —— 后者走 2 参重载，标签字面量在 `:813-814`），
> legacy 侧打 **`[色彩裁决]`**（`:618` / `:621` / `:623` / `:625`）—— 即 `:616` 注释说的「格式逐字一致，只换日志标签」。
> ⇒ **不要写「降级行也能跨管线直接对拍」的断言 —— 那条永远对不上。**
> ⚠ **S6 新增的「有意去重」**：legacy 侧现为 `if (legacyVerdict != null && engPlan == null)`（`:612`；
> 理由注释 `:606-611`）⇒ **引擎侧已经打过的那份裁决与同一批降级码，legacy 侧不再重复打**。
> ⇒ 所以上面「载荷一致」是**按行格式**成立；在**引擎路径**上 legacy 侧**可能一行都不打** ——
> 这是**有意去重**（避免同一条损失在日志里出现两次、被误读成两处损失），**不是「漏打」**，
> ⚠ **不要当 bug 去「补回来」**。
>
> ⚠ **标签字面量是门禁判据、不是自由文案**：`verify-color-strategy.ps1:174` 正在匹配
> `[色彩裁决] 裁决=` ⇒ **能改的只有标签之后的 payload，标签本身冻结**；若要改标签，必须同步改该正则并重跑门禁。
>
> ⚠ **与既有断言的相容性**：`[色彩裁决]` **不含子串 `色彩引擎`** ⇒ 在 `verify-color-wiring.ps1:110`（裸子串
> `色彩引擎`）与 `verify-gainmap-engine.ps1:171`（`\[色彩引擎\]`）下**都过** ⇒ 那两条「显式 legacy 一行引擎
> 日志都不该出现（逃生门零污染）」断言**不需要改、也仍然有牙**（它们拦的是「legacy 偷偷走引擎**执行**」；
> 裁决是**规划层**产物 ⇒ 分标签后该断言继续为真）。**为什么必须分标签**见 `QueueProcessor.cs` 的
> `LogAlternatives` 换标签说明（锚点文本；行号快照 `:816-825`）。
>
> ⚠ **本节边界**：只登记「与 ffmpeg / 色彩裁决相关、且**被门禁当判据用**」的标签（`[cmd]` / `[ffmpeg-meta]` /
> `[色彩裁决]`，外加 `[色彩引擎]` 用于解释「零污染」）。全仓另有 30+ 个 `[tag]`（`[jxr]` / `[cjxl]` / `[保真]` …）
> —— **本文不做标签字典**：字典会立刻过期，只有被门禁当判据的标签才需要「不许混用」的约束。
>
> ⚠ **并发**：本表脚本已按**运行级隔离（GUID）**自保（**35 个**，见 §6 第 66 条；2026-09-18 由
> `verify-anim-static-target.ps1` 与 `verify-jxl-probe-timeout.ps1` 计入 —— 两个脚本都用运行级 GUID 目录 + 绿时自清），但**同一脚本仍不要
> 起两个实例** —— 隔离尚未覆盖全部脚本（2026-09-17 实测**仍有 7 个**在写固定输出目录，
> 但**必跑清单内已无未隔离项** —— 清单内最后一个 `verify-gif-avif-framelist.ps1` 已修；
> 另有 runner 类 + **6 组共享目录簇**，
> 后者的处置已裁定为 (b)「保持固定目录 + runner 串行」；三类清单与裁定理由均见 §6 第 66 条）。
> ⚠ 另一类与并发无关的假绿：**消费者读不到前置产物就静默跳过**（`verify-png3-interop` 实测
> 缺参照时 `PASS` 9 → 8 而 `exit` 仍为 0）—— 见 §6 第 68 条。

### 3.5 服务级探针 `tests/ServiceProbe`

不跑完整队列、直接对真实服务实现做单元级断言（色彩管线类改动定位问题最快）：

```powershell
dotnet build tests/ServiceProbe/ServiceProbe.csproj -c Debug
$p = "tests/ServiceProbe/bin/Debug/net11.0/win-x64/ServiceProbe.exe"

& $p faithful "ProPhoto RGB" yes     # 保真 ICC 可用性 + 确定性 + ICC 结构/tag 序
& $p gainmap  in.jpg [out-sec.jpg]   # Ultra HDR 容器：顶层图/MPF/ISO 元数据+副图切割
& $p icc-dump some.icc rXYZ rTRC    # ICC 头 + tag 表 + 指定 tag 摘要（校准参考/自检）
& $p icc      "Display P3"           # 解析/生成命名 ICC 并校验
& $p decision                          # 步骤 3 验收门禁：宣称值 ↔ 命令行 ↔ ICC 后置判据（60 格）
& $p color    in.png png [exiftool]  # 源色彩探测结果 + 保真判定
& $p pq                              # ST 2084 EOTF/OETF 锚点值与互逆自检
& $p colormath                       # 原色表色度常数 ↔ 双锚（zimg 矩阵 + lcms chrm）逐元素对拍；含 null/重复对白名单
& $p tooldetect                      # 外部工具探测：开销 / 诚实性 / 可注入性（由 `_probe-tool-detect.ps1` 编排）
& $p thumbnail                       # 缩略图（EXIF IFD1）：逐容器「声明=数据闭合」+ 悬空负样本 + 诚实性/隐私/像素不变对照
```

⚠ **`tooldetect` 有意不进 `$Probes` 默认清单**（上面的「23 个 mode」因此**不含**它）：它必须自己
设 `FFMPEGGUI_EXT_SEARCH_DIRS` 把「系统扩展搜索」那一级钉成可控的临时根，否则判据会随本机
装过什么应用而翻转 ⇒ 放进默认清单等于把一条**环境依赖**的断言混进确定性批次。跑法见该门禁脚本。

输出为 `PASS/FAIL` + `key=value` 行，末行 `PROBE RESULT: pass=N fail=N`，退出码 0/1，便于脚本 grep。

> ⚠️ 该探针必须与主项目同为 `SelfContained + win-x64`（被引用的 FfmpegGui 是自包含 WinExe），
> 否则 `AssemblyLoadContext` 加载失败；与 `tests/UiTestHost` 保持同一套属性。

**当前基线（2026-09-19 复核；口径 = 运行器 `$Probes` 默认清单里各 mode 的 pass 之和）**：
**22 个 mode**（⚠ 2026-09-26 把 `colormath` 接进默认清单 ⇒ 第 22 个；下面那个 **546** 是**前 21 个** mode 的求和，
**不含** `colormath`；`colormath` 单跑实测 `12/0`，合计要等下一次整轮才并进去，此处**不预先宣布新基线**）·
⚠ **2026-10-02 把 `thumbnail` 接进默认清单 ⇒ 第 23 个 mode**（单跑实测 `40/0`，2026-10-02 本机；覆盖 jpg/png/apng/webp/avif/jxl 六格逐一真跑，
它守的是「产物声称有 EXIF IFD1 缩略图而图像数据缺席」这类**其他判据都不会响**的回归 —— 悬空指针的
成因是 ffmpeg 的元数据 muxer 保留 IFD1 的 Offset/Length 标签却丢掉数据，实测声明 1980 B / 实取 0 B，
既有的元数据与色彩门禁对此完全无感。变异验牙两次（改坏 `src/` 再原样改回，还原后回 `40/0`）：
**M1** 让嵌入步直接返回成功 ⇒ `pass=33 fail=7`（T11 六格 `rc=0/declared=0` + T17）；
**M2** 把能力表吹大给 `gif` 开一条实测走不通的路 ⇒ `pass=38 fail=2`，且 T13 读数
`rc=0 / declared=0` 顺带暴露 **exiftool 对 TIFF 是"0 image files updated 但退出码 0"的静默空转**。
**546 那个求和不含 `thumbnail`**，同样等下一次整轮才并）·
**前 21 个 mode 合计 546 / fail=0**（**2026-09-24 逐 mode 实测求和**：原 542 + `settings` **7 → 11**（+4 = 全字段落盘往返锁，见 §6 第 110 条）；
⚠ 下面这些是**当时**那批数字的溯源链，**不重述**：`geometry` = **52**；`metaraw` = **17**（RAW 元数据保留：显示名 + 补漏参数形态，**不依赖素材**）；`verdict` **36 → 41**，GainMap 格新增断言；
**`runner` 2 → 8**（#20(0919) 心跳判据锁，见下方溯源）；
`_run-step3-gates.ps1` 的 `$Probes` 默认值已含 `geometry` ⇒ 20 个 mode）。
⚠ **取值时刻 / 取值方式（按 §6 第 78 条）**：**取值时刻 ~2026-09-18 01:14**（⚠ 以下三条描述的是 **519** 那次实测；**525** 为 2026-09-19 #20(0919) 后的追加，见下方溯源）；
**取值方式 = 直调 `ServiceProbe` 逐 mode 实测求和**；**当时产物比源码新 ⇒ 读数新鲜**（`QueueProcessor.cs` 当时仍是 `00:50:16`，
它在 `01:16:21` 之后才被改 ⇒ 从那一刻起产物才变旧）。
⚠ **溯源**：**496** 由 **team-lead** 在**静止窗口**下按 20 个 mode 逐一求和复核**两次**（`DRIFT_WARN=0`）；
`i18n-keys-fix` 亦独立复跑一致（2026-09-17 23:58，跑前已验产物新鲜度、无 STALE）。
**501** = 496 **+** `verdict` 的 **+5**（`gainmap-merge-partA` 加 GainMap 格断言后自报 36 → 41）
⇒ 由 **team-lead 直调 `ServiceProbe verdict` 实测复核**（`PROBE RESULT: pass=41 fail=0`）。
⇒ **2026-09-18 追加（不改上面的溯源链，只在其后追加）**：**501 → 519**（`geometry` **35 → 52**，**+17**）。
⚠ **对账差 1（曾出现，2026-09-18 已对平）**：曾按旧行推算 `501 + 17 = 518`，与**直测 519** 差 **1**。
⇒ 差的那 **1** 已定位 = **`wire` 71 → 72**（另一项 `geometry` 35 → 52）⇒ **+1 + 17 = +18** ⇒ `501 + 18 = 519` ✓
（`team-lead` 给出逐 mode 数据；`gainmap-merge-partA` 用 `awk` 独立复核：20 mode 求和 = **519**、反算旧基线 = **501**）。
⚠ **`wire` 71 → 72 的 +1 来源未溯源**（不知是哪一轮加的）—— 照实留档，不编。
⚠ **本行保留「曾差 1」这个事实**：它记录的是**坑** —— **不能拿旧行的分项数去推算新总数**（当时正是这样推出 518 的），
必须**逐 mode 实测求和**；**本行一律以直测 519 为准**（该链为 2026-09-18 的 519 档）。
⇒ **2026-09-19 追加（不改上面的溯源链，只在其后追加）**：**519 → 525** 的 **+6** = **`runner` 2 → 8**（#20(0919) 心跳判据锁：非 ffmpeg 子进程不再被注入 `-progress pipe:1`，2026-09-19）。
⚠ 复核动作（下次谁重算都能复现）：`ServiceProbe geometry <临时目录>` 实测得 `PROBE RESULT: pass=52 fail=0`
（本行 2026-09-18 由 `gainmap-merge-partA` 复核；⚠ 传**临时目录**而不是 `tests/output/validate`，避免污染共享目录）。
⚠ **本处只记「mode 数 + 总数」这个口径，不逐 mode 列计数**
—— 计数会漂（`wire` 与 `verdict` 尤其）。⚠ **改任一 mode 的断言后必须重算总和**：
历史上出现过「`contract` 100 → 107 改了、总数漏改、仍写 372」的错值，由复核方用「各 mode 求和 ≠ 372」抓出。
⚠ **某个 mode 若不在 `_run-step3-gates.ps1` 的 `$Probes` 默认值里，说明运行器清单与文档不同步**
（可据此核对「mode 齐不齐」）。`_run-step3-gates.ps1` 的**脚本门禁清单**当前 **65 条**（2026-10-01 再接线：首批 5 条 **L3/产物级**套件 `verify-package-smoke.ps1` + `verify-jbrd-e2e.ps1` + `verify-orientation-rewrap.ps1` + `verify-gainmap-thirdparty.ps1` + `verify-encoder-knob-liveness.ps1`，由 60 → 65；再往前同日接的是 `verify-geometry-orientation.ps1` + `_probe-geometry-single-source-scan.ps1` + `verify-gainmap-host.ps1`（⇒ 60）；权威复现口径 = 运行器**自己打印的** `script-list=N 条（受管基线 N）` 那一行，它比任何外部正则都可靠 —— 本文原来给的 `(?ms)^\$scriptList...` 正则实测命中 0（数组内含大量注释行，闭合 `)` 不在行首），**别再拿它当复现命令**）
（**2026-09-24 第六次接线后实测数组条目数** —— 复现命令必须**按数组锚定**（不用行号、也不用全文件 grep）：
`$c = Get-Content -Raw tests/scripts/_run-step3-gates.ps1; [regex]::Matches([regex]::Match($c,'(?ms)^\$scriptList\s*=\s*@\((.*?)\)\r?\n\$actualScriptCount').Groups[1].Value,"'[^']+\.ps1'").Count` = **54**；
⚠ 本行原先给的 `grep -oE "'[^']+\.ps1'" … | sort -u | wc -l` **现已失真**：2026-09-22 实测它得 **53**，
因为运行器别处还有 `'generate-sources.ps1'` / `'ensure-fixtures.ps1'` 两个引号串被算进来 ⇒ **不要**再用它取当前值。）；
⚠ 演进（**只有这三档可逐字复核**，更早的链在 `§6` 与本行之间历史上曾不同步 ⇒ 不重述）：
**49 条**（2026-09-20，`docs/HANDOVER.md` 第四/五轮验收行 `script-list=49 条（受管基线 49）`）
→ **50 条**（2026-09-22，`docs/HANDOVER_2026-09-22.md §0` 记 `$expectedScriptCount = 50`，+`verify-engine-alpha-preserve.ps1`）
→ **51 条**（2026-09-22，+`_probe-tool-detect.ps1`：外部工具探测的**开销/诚实性/可注入性**三合一锁）
→ **52 条**（2026-09-23，+`verify-startup-warmup.ps1`：启动期「环境分析 / 预热」七缺陷锁，结构锁 + 一次真跑）
→ **53 条**（2026-09-23，+`verify-ipc-extra-options.ps1`：IPC `ExtraOptions` 透传轴回归锁 —— 2026-09-19 就写好、自带变异验证，却从未上清单 ⇒ 「有锁但不上锁」）
→ **54 条**（2026-09-24，+`_probe-settings-clone-coverage.ps1`：`Save()` 手写克隆表的结构锁 —— 它 2026-09-15 就写好、**接入当天就抓到一个长期缺陷**（`RenderingMode` 每次 Save 被重置成默认值）⇒ 与上一条同属"有锁但不上锁"，见 §6 第 110 条）。
→ **57 条**（2026-09-27 第九次接线，+`verify-simd-switch-fallback.ps1`：面板「SIMD 优化」复选框**终于有消费者**的锁 ——
  修前它是「有声明、无实现」（两条语言的 tooltip 都承诺「关闭后逐像素回退」，而 `PsnrCalculator`/`SimdPixelOps` 无条件看 ISA）。
  四段：① 行为（`ServiceProbe simdswitch` 关/开两跑，**pass>=25 下限**防整段没跑）② `FFMPEGGUI_AUTO_SIMD` 两端到端（关⇒scalar、开⇒非 scalar、两态必须不同 ⇒ **修复前必红**）③④ 结构（`.IsSupported` 与消费端集合只此一份、内核分派表只一张、**空集合也判红**）。
  实测 **17/0/1SKIP**；变异 = 把 `ResolvePixelKernelPath` 的守卫反向 ⇒ **PASS 17→13、FAIL 0→4**，还原后源文件逐字节一致
→ **56 条**（2026-09-27 第八次接线，+`_probe-stderr-merge-scan.ps1`：任务 #51 的**合并取数结构锁**——把
  「stdout+stderr 拼起来返回」的 helper 被用来读**工具的值**（exiftool 标签 / ffprobe 字段）的站点扫出来并锁成债账
  （债账起 **84** ⇒ **同日逐条分诊到 0**，见 §6 第 114 条；实测 0.7 s。⚠ 该锁自己有一处**造假的假红**：`-ManagedOnly` 口径下共现基数只有 21，而共现下限原本钉死 30 ⇒ 现按口径分档 30 / 18）。它同日收了五处口并各补**正对照**，见 §6 第 113 条）。
→ **55 条**（2026-09-27，+`_probe-matrix-structure.ps1`：色彩矩阵清单的**行锚定取证自检**（A1..A31 + B1..B7 + 本门禁自己的变异注入），单遍实测 5.5 s ⇒ 两遍 ≈ 9 s。**理由不是"又想到一条"，是账已经错过两次**：折叠表/矩阵每条带 `Evidence = 路径:行号|字面片段`，而核对它的 `verify-oracle-matrix.ps1` **有意不入列**（2.5 h）⇒ 2026-09-27 上午核账发现 **6/19 条早就漂了**，接线**当天**又漂一条（#39/#43 改了 `FfmpegCommandBuilder.cs` 的行 ⇒ `metadata-core` 2011 → 2041）。见 §6 第 112 条）。
⚠ **每次增删必须重新数，不要沿用旧值**；
⚠⚠ **增删一条门禁要动的「账」全清单**（2026-09-24 汇总；下面每一项**漏掉之后都真实发生过**，不是假想后果）：
  ① `$scriptList` 数组加/删条目 ⇒ ② `$expectedScriptCount` 常量（**数组 ≠ 常量 ⇒ 运行时条数锁响亮红**）⇒
  ③ 运行器**散文里的现量陈述**（三处形态见 §6 第 107 条。**漏改本来不会红** —— 接第 53 条时就漏过两处、
    之后照常全绿无人发现；09-24 起由**第 ⑰ 针**钉住 ⇒ 现在漏改 = 运行器**拒绝跑**）⇒
  ④ 本章「4 + 7 + N = 总数」的分类分解，以及 `docs/HANDOVER.md` / `docs/HANDOVER_2026-09-22.md` §0 /
    `.workbuddy-ai/memory/MEMORY.md` 里的同数陈述 ⇒
  ⑤ 若新脚本**无 PASS/FAIL 契约**或**只有退出码契约** ⇒ 还要进 `$tableOnly` / `$noSelfSummary`；
    而这两个数组的**条数**由第 13/14 针钉死为 **4 / 7** ⇒ 动了数组必须同时改那两针的期望值（否则形态自检拒绝跑）。
  ⚠ `-Scripts` 子集跑**不能**用来宣布基线（汇总行自带「不构成基线」标记）；宣布新基线要跑全量，
    且**开始前**就停止改 `src` 与运行器（`Check-SrcDrift`/`Check-SelfDrift` 把中途改动判作废，注释也算）。

⚠⚠ **未受管脚本台账（2026-09-24 全量盘点；此前有人记成"只有 6 条孤儿门禁"—— 那个数太小了）**：
  口径 = AST 取 `$scriptList`（**55**）与 `tests/scripts/*.ps1` 全量（**95**）的差 ⇒ **未受管 40 个**。
  （2026-09-27 重测：上一档记的是 54 / 93 / 39；其间进了 `_probe-matrix-structure.ps1`（受管 +1），
  全量 +2 = 该新门禁 + `verify-color-matrix-full.ps1` 落入这份台账的统计口径 ⇒ 未受管净 +1。）
  分类规则是**按文件名前缀**（确定性的，不靠人工判读；`insert_cicp.ps1` 归 B 就是因为这条规则）：
  · **A 库 = 5**（`_lib-axes` / `_lib-color-assets` / `_lib-matrix` / `_lib-oracle` / `_lib-reject`）
    ⇒ **永远不该**进清单：它们是被 dot-source 的共享库，进去就会被当门禁跑 ⇒ 撞今天的「零断言 ⇒ 红」闸。
  · **B 编排 / 脚本工具 = 13**（运行器自己 `_run-step3-gates`、素材生产者 `generate-sources` / `ensure-fixtures`、
    清理 `cleanup-structure` / `archive-diagnostics`、fixture 构造 `insert_cicp`、以及 6 个 `run-*` 旧编排器）
    ⇒ **有意不入列**。⚠ 其中 `run-color-regress.ps1` / `run-full-matrix-test*.ps1` 是**清单机制出现之前**的
    并行跑法 ⇒ 与运行器职责重复（归档要点头，不擅自删）。
  · **C 一次性取证 `_probe-*` = 16**（gpu-stage1..3 / zimg-* / sws-* / ztf-locate / p7d-bisect /
    jxl-webp-pixels / hdr-peak-oracle / icc-affects-decode / caps-e1-e2 / png-cicp-production …）
    ⇒ 都是某次定性调查留下的**证据脚本**，**绝大多数没有 `PASS/FAIL` 契约** ⇒ 接进清单等于改判据形状，
    所以不入列是合适的。**但要说清它们得到什么保护、没得到什么**：
    ✅ `verify-ps-compat.ps1` 的语料是 `tests/scripts/*.ps1` **全量** ⇒ **语法层（5.1 兼容）已被覆盖**；
    ❌ **语义 / 可跑性无人管**（引用的产品路径、参数、exe 位置变了都不会红）—— 本轮就实撞到一次：
    `_probe-settings-clone-coverage.ps1` 早在 2026-09-15 就写好、能抓 `Save()` 漏字段，
    但**不在清单 ⇒ 没人跑 ⇒ 期间真的又漏了一个 `RenderingMode`**（见 §6 第 110 条）。
    ⇒ 处置口径：**凡是"能判红产品"的取证脚本，要么升级为带契约的门禁接进清单，要么在 D 类里点名它已腐坏。**
  · **D 其他 = 6**：`verify-oracle-matrix.ps1`（**有意**只手工长跑它的 5010 例执行层，≈ 2.5 h，见上方与 §6 第 92 条邻域）·
    `probe-gainmap-tiff.ps1`（GainMap/TIFF 取证，无汇总契约）·
    `final-verify.ps1` / `regtest-fix.ps1` / `verify-remaining.ps1` ⇒ **实测已腐坏**：三条都指向
    `publish/build/FFmpegPictureUI-dev-x64-full/FfmpegGui.exe`，而 `publish/build/` **在本仓不存在**
    （`ls publish/build` 无此目录），其中两条还把日志写到仓库外的 `C:\temp\*.log`
    ⇒ 建议**归档到 `tests/scripts/_archived_diag/`**（仓库里 `archive-diagnostics.ps1` 就是干这个的，
    但它从未跑过 —— `_archived_diag` 目录不存在）或删掉；**删/移文件要维护者点头，本轮只登记**。
⚠ **判文件编码不要看 Bash 的 `grep` 输出**（2026-09-19 实测）：Git Bash 终端会把 UTF-8 中文按 GBK 渲染 ⇒ 看起来像整个文件被写成 GBK（`：` 显示为 `锛`），**实际文件是合法 UTF-8**。判编码要用 `new TextDecoder('utf-8', {fatal:true}).decode(buf)`（严格解码，抛异常才是真坏），或直接用 Read 工具读。
⚠ 本行此前写「当前 33 条」并给了一条 `sed -n '235,242p' …` 的复现命令 —— 那是**行号锚点**，注释一增行即失效，
现改为与 `docs/HANDOVER.md` 同一句的**全文件 grep** 形态）。

---

## 四、自动化测试脚本模板

将可重复执行的测试写成 `tests/scripts/*.ps1`，统一约定：

```powershell
# 模板：tests/scripts/run-xxx-tests.ps1
$ErrorActionPreference = "Stop"
$out = "tests/output"
New-Item -ItemType Directory -Force -Path $out | Out-Null

$ffmpeg = (Get-ChildItem "publish/PLAN/ffmpeg-full*/ffmpeg.exe" | Select-Object -First 1).FullName
$cjxl = "publish/PLAN/jxl/bin/cjxl.exe"

function Test-Step($name, $script) {
    Write-Host "=== $name ===" -ForegroundColor Cyan
    & $script
    if ($LASTEXITCODE -ne 0) { throw "测试失败: $name" }
    Write-Host "✅ $name" -ForegroundColor Green
}

# 示例测试步骤...
Test-Step "JPEG→JXL 无损重封装" { & $cjxl "$out/src.jpg" "$out/result.jxl" -d 0 -e 3 --lossless_jpeg=1 2>&1 | Out-Null }

Write-Host "`n🎉 全部测试通过!" -ForegroundColor Green
# 结束前清理产物（可选）
# Remove-Item "$out/*" -Exclude ".gitkeep" -Force
```

---

## 五、.gitignore 相关

已忽略的测试路径（见仓库根 `.gitignore`）：

```
tests/output/
tests/tmp/
tests/*.log
test_output/
test-output/
```

新增测试目录时，请同步更新 `.gitignore` 与本规范。

---

## 六、注意事项

1. **PowerShell 重定向陷阱**：`>` 会破坏二进制流（ICC/PNG 管道输出）。提取二进制时用 `cmd /c "exe args > file"` 或在 C# 中重定向 BaseStream
2. **exiftool 无法写入 JXL 的 ICC**：JXL 的 ICC 只能由 cjxl `-x icc_pathname`（PPM/PAM 管道）或输入 PNG 内嵌 ICC 携带
3. **ffprobe 字段顺序**：`-show_entries stream=A,B,C` 逗号分隔仅最后一个字段生效，输出固定为 `pix_fmt,color_space,color_primaries,color_transfer`（注意：`color_transfer` 在 `color_primaries` **之前**）
4. **测试日志**：长测试建议输出到 `tests/output/test.log` 便于排查
5. **iccgen 生成的 ICC 并不一定会落盘**（2026-09-14 实测，ffmpeg-full 2026.9.2 + exiftool 13.59，
   同一 Display-P3 源同一 `-vf iccgen` 链）：PNG(iCCP) / JPEG(APP2) / TIFF **能**落盘，
   **WebP 不能**（libwebp 丢弃，产物 RIFF 内根本没有 ICCP 块）⇒ 必须走 exiftool 后置嵌入。
   写这类断言时请读**产物**，不要拿“命令行了有 iccgen”当交付证据。
6. **本构建的 ffmpeg PNG 编码器已经会自己写 cICP**（tonemap 后 rgb24 产物含 cICP=bt709/bt709，
   16-bit HDR 直通产物含 cICP=bt2020/smpte2084）——旧结论“PNG 编码器不写 cICP”只对早期构建成立，
   `PngCicpService` 后置补写现已退化为兜底（已有 cICP 时不重写）。
7. **量像素的测量口径**（跳不过去就拿到假数据，三条全是实测踩的）：`-vf` 必须写 `format=rgb24,crop=1:1:0:0`——
   先 crop 到 1×1 再从 yuv420p 转 RGB 会输出 **0 字节**；裸 `.raw` 后缀 ffmpeg 认不出 muxer，必须显式 `-f rawvideo`；
   exiftool 的 `-o FILE` 要**分成两个参数**传（`-o$path` 被当成无效 TAG 名）。
   实测：**给产物嵌 ICC 不改变读到的像素值** ⇒ 不管 ICC 在场还是不在场，像素读数可比。
8. **P7d（已修 2026-09-14，回归锁在 `verify-decision-delivery.ps1` ⑨）**：同一份 Display P3 内容，
   主分支 `p3.png→webp` 角像素 = `116,16,237`，而 JXL 输入专用通路 `p3.jxl→webp` = `94,22,238`。
   定性过程全靠度量，两个被**排除**的解释：“djxl 把像素转了”（它解出的 ppm/png 与源逐字节相同
   ⇒ 给它后置嵌 P3 ICC 语义正确）、“ICC 在场改变了测量值”（嵌与不嵌读数相同）。
   开关隔离定位到唯一变量：同一份 PPM，不加 `-pix_fmt` → `94,22,238`（精确复现缺陷），
   加 `-pix_fmt yuv420p` → `116,16,237`（精确等于主分支）。
   **根因是结构性的**：`-pix_fmt` 的通用分支靠“探测输入位深”决定，而管道输入是 stdin `-`
   ⇒ 探测必然失败 ⇒ 整条 `-pix_fmt` 被**静默跳过**，像素格式交给编码器自选。
   修法：`BuildArguments(..., probeInputPath)`——stdin 时把“喂进管道的真实源文件”当探测替代
   （`PipeDjxlToFfmpegAsync` / `BuildFfmpegPipeArguments` 已传；预览路径同）。
   ⚠ 注意：`DecideOutputColor` 仍按 `inputPath` 探测（管道已把色彩元数据注入 options），两处不同源是故意的。
9. **webp 的默认编码器是 `libwebp_anim`，不是 `libwebp`**（实测：同一张 P3 PNG、同一批参数，
   不写 `-c:v` 与写 `-c:v libwebp_anim` 都得 `116,16,237`，写 `-c:v libwebp` 得 `97,23,247`）。
   ⇒ 拿手敲命令去对照产品产物时，**方才不写 `-c:v` 才是同源对照**；多写一个就会造出“像素不一致”的对照伪影
   （本轮就差点把它当产品缺陷去修）。像素对照一律用 `_probe-jxl-webp-pixels.ps1`（它会用 djxl 自解值做仲裁，
   能区分“ICC 贴错”与“编码往返漂”）。
10. **P7e（已修 2026-09-14）：动画 AVIF → GIF/WebP 两步法曾经在本构建根本跑不通**。
    真因不是“参数解析失败”（那句 `-22 (EINVAL)` 只是 ffmpeg 对不存在的流映射的间接报错），
    而是 `ProcessAvifToGifWebpAsync` 的 Step1 **硬编码了流号** `-map 0:2`（颜色）/`-map 0:3`（alpha）。
    实测本工具链（ffmpeg git-2026-09-01 + avifenc 产物 `anim_gif2avif.avif`）：该文件只有**两条**流（`0=av1, 1=av1`），
    `-map 0:0` → 只出 1 帧；`-map 0:1` → 出 **10 帧**；`-map 0:2` → exit=-22。
    两条流都是彩色 yuv420p 且 **SATAVG 相同（104.603）** ⇒ 1 帧那条是首帧副本，**该素材根本无 alpha 平面**。
    修法：`ProbeAvifStreamRolesAsync` 用 `ffprobe -count_frames` 量每条流帧数，
    **帧数最多的当颜色（并列取 index 最小）、另一条帧数与颜色完全相同的才当 alpha**；
    探不到流则回退旧的 (2,3)。该规则对旧 4 流布局仍选出 0:2/0:3 ⇒ **向后兼容零行为变化**。
    回归锁在 `verify-decision-delivery.ps1` ⑩（产物必须 ≥ 2 帧、帧数对齐源、SATAVG>10 非灰度、日志有“选流”行）。
    ✅ 变异测试已做：强制选“首帧副本”流后，“产物≥ 2 帧且对齐源”那条当场转红（报“1 帧动画（源最大流 10 帧）”）；
    而“非灰度”那条仍绿——**符合设计**：三条锁各防不同失效模式（单帧副本 / 挑错 alpha / 静默猜定），不是冗余。
    ✅ **P7f（已修 2026-09-14）：便携包布局下 GIF→AVIF 两步法永远跑不到，并静默回退**。
    根因是**同一件事有三份拷贝、口径还不一**：
    ① 队列分派 `QueueProcessor.HasAvifencAvailable()`——查 PLAN（判为可用，于是进入两步法）；
    ② 执行方法 `ProcessGifToAvifViaAvifencAsync` 内部**重找一遍**，只查
       `AvifencPath → WindowsArtifactsDir → FfmpegDir`，**漏了 PLAN/artifacts** ⇒ 立即报“avifenc.exe 未找到”并回退；
    ③ UI 面板 `MainWindow.HasAvifencAvailable()`——含 PLAN，但**漏了手动 AvifencPath**。
    ⇒ 对用户就是“工具面板说 avifenc 可用、队列却默默改用 ffmpeg”。
    实际 `publish/PLAN/artifacts/avifenc.exe`（12.4MB）与两侧 `bin/*/…/win-x64/PLAN` junction 均在位，
    所以既不是环境缺工具、也不是“产品丢透明”（这两个错结论都被排除过）。
    修法（最终形态，共 4 处收敛 + 1 处行为纠正）：
    ① 全局唯一定义 `PlatformServices.ResolveAvifencPath()`（手动 → artifacts → **PLAN** → ffmpeg 同目录）；
    ② 执行方法直接用它的返回值；③ 面板 `ExternalToolsDetector` 的 avifenc 条目（第 4 份拷贝，
       它经 `FindInArtifactsOrPlan` 额外包含“去扩展名”兜底 ⇒ 反向假报：无后缀文件面板说可用但 Windows 下不可执行）也改用它；
    ④ UI 的 `MainWindow.HasAvifencAvailable()` 副本删除，两处状态展示直接问唯一入口；
    ⑤ **最关键**：分派条件里的 `&& HasAvifencAvailable()` 整个移除——它才是真正会导致静默回退的地方：
       avifenc 缺失时 `gif→avif` 会绕过两步法、静默落到后面的通用 ffmpeg 分支（上面注释自己写着“丢 alpha”），
       而用户看到“已完成”。现在意图总是路由到两步法，由方法内部**显式失败**（`失败 (avifenc 不可用，未静默回退)`，
       并给出三条补救指引）——这是“可用性诚实”原则的落实，不再出现“(ffmpeg 回退)”这种假成功状态。
    锁：`verify-decision-delivery.ps1` 新增两条“P7f”结构断言：全仓 `TryFindInPlanFolder(Avifenc)` 与
    `ResolveAvifencPath` 定义各必须为 1；且定义必须在 `PlatformServices.cs`，不得再出现任何“第二口径”形状
    （`HasAvifencAvailable` 无论定义还是调用、以及 `FindInArtifactsOrPlan(Avifenc)`）。**两条锁都已证明有牙**：
    第一条当场拓到我写的“薄委托”重复定义（报定义数=2）；第二条用临时插入一行 `HasAvifencAvailable` 定义做变异，
    报 `FAIL … HasAvifencAvailable=1` 并指名 `QueueProcessor.cs:L3583`（验后已撤销并重建）。
    ⚠ 锁自身也曾造出两个假象：（a）它把**注释里提到的方法名**也计入 ⇒ 必须只数代码行；
    （b）过滤后重新编号导致报出的行号是假的 ⇒ 行号必须用文件真实行号；
    （c）`@() -eq 0` 在 PowerShell 里是逐元素比较、恒为假 ⇒ 数组判定必须写 `.Count -eq 0`。
    验证：修复后日志从“未找到、回退”变为真正进入 `Step1: 提取 GIF 帧为 RGBA PNG...`；
    连带回归全绿（decision-delivery 29/0、pipeline 80/0、caps 12/0、wiring 26/0、format-regression 19/0、widegamut 18/0，Debug+Release 0 错误）。
    ✅ **同类问题已系统扫过其余工具**（cjxl/cjpegli/djxl/exiftool/JxrEnc/dngtool）：它们均已是
    “单一解析器 + 缓存 `_detectedPath`、执行处只用缓存值”的健康形态（手动路径优先、含 PLAN），
    avifenc 是唯一一处“执行时重算路径”⇒ 本轮不再动它们（避免无必要重构）。
    ⚠ **本环境验不了这一步的产物**：两步法要 spawn 外部进程，沙箱以
    `UnauthorizedAccess_IODenied_Path, \\.\pipe\LOCAL\dotnet_*` 拒绝 ⇒ 任务在 Step1 失败。
    所以“修复后真能产出带 alpha 的动图 AVIF”这一条只有**分支进入**的间接证据，**产物未实测**（待环境解锁或手动跑）。
    同类分叉已扫：`QueueProcessor` 里 `Has*Available` 只此一例（JxrDec 那处本来就是单一入口风格）。
    ✅ 另：“产物只有 1 帧/112B”那条疑点已用真样本重做，**它不是伪问题而是另一个缺陷，已登记为 P7g**（见下 11）。
    当时我拿的是一个源文件已被删、跨多次构建混合状态的旧产物 ⇒ 结论不成立也不代表没问题，重做才对。
    ⇒ 当初卡住“非灰度”那条锁的素材缺口已不存在：现已能用产品自己的 `GIF→AVIF` 生成真 anim+真 alpha AVIF
    （四条流、含 `gray` alpha 平面）⇒ P7e 的选流规则已拿到真样本验证，同时暴露了 P7g（见下 11）。
    它不是色彩缺陷，但曾**遮住依赖它的断言**：⑩ 的“未登记通路必须显式记‘裁决点未参与’”在 P7e 修复前只能报 SKIP。
    ⚠ 该断言早期版本是“日志里没有嵌 ICC 就算通过”，而任务失败时确实没嵌 ⇒ **假绿**；
    现在改成“跑不成就是无法评估，不给绿”。写这类“某件事没发生”型断言时，必须先证明确实执行到了那一步。
11. **P7g 已闭环（根因＝帧序号空洞 ⇒ image2 静默截断；“不透明”那半是素材伪证）：AVIF → 动图 WebP 的“有 alpha”分支**。
    根因与定性（2026-09-15 实测，两条分开看）：
    ① **“单帧”＝帧序号空洞导致 image2 静默截断。** Step1 原写 `-frame_pts 1`，即**用 PTS 当文件序号**；
       一旦某条流的 PTS 间隔 >1，写出的就是带空洞的序号，而 Step2 按 `%04d` 顺序读，**遇到第一个空洞即停**。
       隔离实测：9 个文件、第 4 个位置挖掉一个洞 ⇒ 产物只有 **3 帧**，而 ffmpeg **退出码仍是 0**、任务状态“已完成”。
       修法：Step1 改 `-start_number 0` 连续编号，Step2 读取侧显式给同一基号 ⇒ 序号不再依赖 PTS 值。
       ⚠ 由此得一条通用教训：**动图/序列化的通路不能靠退出码判成败**，必须量产物帧数（⑩/⑪ 均已如此断言）。
    ② **“不透明”＝素材伪证，不是产品缺陷。** 上一轮的透明 GIF 其实不透明：
       `drawbox=…:color=black@0.0:t=fill` 的 alpha=0 是 **0% 混合**，等于什么都没画（实测该帧 alpha 恒 YMIN=255）。
       改用 `geq` 直接写 alpha 通道造出真透明（YMIN=0）后，同一条通路产物为 `argb / 10 帧 / alpha YMIN=0` ⇒ 透明是交付了的。
    现在 `verify-decision-delivery.ps1` 的 ⑪ 组把这条通路按度量锁住，并且**自带对照组**：
    素材 GIF 自身 YMIN=0、产品 GIF→AVIF 后 gray alpha 流仍 YMIN<255（否则后面的 webp 断言就是在替上游背锅），
    再断言 AVIF→webp 产物帧数 == 源颜色流帧数、pix_fmt 含 alpha、alpha YMIN<255。
    为何以前没被抓到：⑩ 的素材（`results/anim_gif2avif.avif`）是两条流无 alpha 的 ffmpeg 产物 ⇒ `hasAlpha` 分支零覆盖。
12. **写对照实验时先自检实验本身**：曾有一次重跑 Step1 变体时五个组合全报 -22，差点得出
    “整个两步法坏了”的结论——其实是**测试脚本自己忘了给输出名加 `.png`**（image2 muxer 认不出扩展名）。
    加了扩展名后，同样的命令就能区分出“只有 `-map 0:2` 坏、其他映射全好”。⇒ 对照全红时先验测量口径。
    同一类的另一次：本轮发现“验不了产物”的归因也不准——`UnauthorizedAccess_IODenied_Path` 是**偶发**，
    重试同一条命令就完整跑通了并拿到了真素材。⇒ 把失败归因为“环境不允许”之前，先重试几次。
13. **重要口径纠正：`run-pipeline-tests.ps1` 并不驱动产品 `FfmpegGui.exe`**（它直接 `& $ffmpeg -c:v libaom-av1 …`
    造素材/跑用例），所以它的 80/0 **不能作为产品代码的回归证据**，只能证明工具链与素材可用。
    我在之前多轮报告里把它列为“产品连带回归全绿”的一环，属**高估证据力**；产品级覆盖应看
    `verify-decision-delivery.ps1` / `verify-color-wiring.ps1` / `verify-format-regression.ps1`（它们确实仍用 Debug 输出）。
    另：`final-verify.ps1` / `regtest-fix.ps1` / `run-full-matrix-test*.ps1` / `verify-remaining.ps1` 指向的是
    `publish/build/FFmpegPictureUI-dev-x64-full/FfmpegGui.exe`（可能陈旧），用它们作证前必须先确认那个二进制是新构建的。
    ⚠ 同一条也适用于 `docs/HANDOVER.md`（2026-09-14 那份）：它把 `run-pipeline-tests.ps1 80/80` 当作“产品无回归”的证据，
    而该脚本从不启动 `FfmpegGui.exe`；产品级证据只有驱动 headless 的那几个脚本。
14. **P7h（已修 2026-09-15）：avifenc 1.4.2 在 `-j 1` 且帧数 ≥4 时段错误** ⇒ 动画 `GIF→AVIF` 在 headless 下**必崩**
    （`失败 (avifenc 退出码 -1073741819)`，即 0xC0000005）。标定实测：同一批 10 帧
    `-j 1` 崩、`-j 2/4/8/省略 -j` 全好；4 帧起崩、3 帧好；**与 alpha 无关**（纯不透明帧同样崩）、与 `--repetition-count` 无关。
    产品侧成因是 `Math.Max(1, item.Options.Threads)`，而 headless 的 `Threads=0`（auto）恒等于 1。
    修法：`Threads<=0` 时**不传** `-j`（让 avifenc 自适应）；用户显式要 1 线程时抬到 2 并在日志写明是上游缺陷（不静默改行为）。
    锁：`verify-decision-delivery.ps1` ⑪ 组（产品自造 10 帧素材 ⇒ avifenc 一崩就红）+ 一条禁止 `Math.Max(1, item.Options.Threads)` 复现的文本锁。
15. **P0-1（已修 2026-09-15）：headless 的 120 秒曾是作用于整条队列的硬超时** —— `WaitForCompletionAsync` 到点即
    `Stop()` + 抛 `TimeoutException`，于是**批量转换必然失败**，且已成功的项被一起判失败（退出码 1）。
    现改为**停滞看门狗**：10 分钟窗口内没有任何项目进入终态才算卡死，连续 3 个窗口才动手。
    实测：20 项 1080p→AVIF 跑 **381 秒**、`exit=0`、20/20 产物、日志“完成 20 项, 失败 0 项”。
    变异验证：把窗口临时改成 5 秒/1 次 ⇒ 6 秒即中止、`exit=1`、只剩 2 个半截 `.avif` ⇒ 断言确为承重。
    ⚠ 顺带暴露 P1-4 的实证：中止后留下**半截产物冒充合法输出**（中止/失败路径不做原子替换）。
16. **设置落盘是手写克隆，漏字段=静默失效**：`AppSettingsService.Save()` 逐字段 copy，HEAD 版漏了
    `IccDirectory`/`EnableIpcServer`（另有 `EnableMaxDimension`/`MaxLength` 也是后补的）——每次改设置都会把它们抹回默认值。
    校验器：`tests/scripts/_probe-settings-clone-coverage.ps1`（比对模型里非 `[Obsolete]`/非 `[JsonIgnore]` 的持久化属性与克隆项）。
    对照组 = 用 `-RefFile` 喂 HEAD 导出的旧文件，它报“漏 4 项”；现版本 22/22 报 PASS ⇒ 校验器不是空转。
17. **P7a-3 第三轮（已完成 2026-09-15）：色彩后置六步收敛为唯一入口 `QueueProcessor.ApplyColorPostStepsAsync`**
    （统一元数据恢复之后的 PNG sBIT / PNG cICP / 引擎 ICC / 裁决点 ICC 后置 / TIFF 目标 ICC / 超广保真 ICC）。
    主分支与**登记过的**专用通路都调它，`opts`/`probeInput` 由调用方按同源登记传入 ⇒ 判据不存在第二份。
    实测鉴别用例（门禁 ⑫）：`JXL(无 ICC 的 sRGB 源) → TIFF + 显式 P3 目标`，走 djxl→ffmpeg 管道这条登记通路。
    抽取前该通路的日志里**没有** `[tiff] 已嵌入目标色彩空间 ICC` 这一步（结构事实：那四步写在主分支体内）；
    抽取后出现，且产物 ICC 实测红原色 x=0.680。
    ⚠ 我一度把结论写成“抽取前产物完全没有 ICC”——那**没有实测支撑**（当时的 exiftool 字段名取错了值），
    产物到底带不带 ICC 还取决于 iccgen 是否进了命令行 + 容器编码器是否落盘（见下 18）。只报量到的那部分。
    ⚠ **对照组设计教训**：⑫ 最早用“P3 源 → P3 目标”，前后两态都能量到 x=0.680（元数据恢复回带的源 ICC
    恰好等于目标 ICC）⇒ 缺陷被掩盖、断言不承重。换成**源自身无 ICC**后排除了“回带源 ICC”这条来源；
    但它**排不掉** iccgen + 编码器落盘（见下 18）⇒ 真正承重的是“这一步有没有跑”的日志断言，色域复核只负责
    确认落下来的 ICC 与出口语义一致。一条控制组只能排除它所能排除的那一种解释。
    顺带修掉一条与实测不符的宣称：TIFF 的 `[tiff] … 将保留源文件 ICC 配置` 在源无 ICC 时是假话，
    且出口 ICC 随后会被覆盖 ⇒ 改为只陈述两种情形都成立的事实。
18. **本构建的 TIFF 编码器会落盘 iccgen 生成的 ICC**（2026-09-15 实测，与 §6.6 的 PNG cICP 属同一类过期纠正）：
    `src_hdr.png(BT.2020/PQ) → tiff` 的真实命令行是
    `… tonemap=hable … zscale=…:p=bt709:t=iec61966-2-1:m=bt709,iccgen -pix_fmt rgb48le -map_metadata -1 …`，
    `-map_metadata -1` 已排除一切源元数据、且这条 auto 目标根本不进 `[tiff]` 后置步骤，
    产物 TIFF 却带 ICC，实测红原色 `0.64 0.33` 正等于出口 `p=bt709` ⇒ 这张 ICC 只可能来自 iccgen + 编码器落盘。
    推论：① 裁决点落盘表把 tiff 记为“编码器自己写”与实测相符，**不改判据**；
    ② `[tiff] 目标色彩空间 ICC` 那一步在显式目标下是**冗余但同语义**的覆盖（再贴一次目标 ICC），保留无害；
    ③ 该步注释里“实测 ffmpeg tiff 编码器不写 iccgen 生成的 ICC”只对早期构建成立，已按本条更新。
19. **P1-3 / P1-4（已修 2026-09-15）：失败与中止不再留下“看着像成功”的文件**
    - **P1-4 半成品产物**：所有通路都是**直写最终路径 + 无条件 `-y`**，于是失败/被停止时目标路径上会留下 0 字节或截断的文件。
      现在在任务级 `finally` 里处置：**只删本任务新建的**输出（执行前已存在 ⇒ 已被 `-y` 冲掉，再删属二次销毁 ⇒ 只显式告警）。
      实测是四次才收敛的（每一步都量了才改下一步）：
      ① 判据用 `!IsCompleted`、不加宽限 ⇒ 残留 **0**，但该判据把“不算成功”当“失败”，遇到只设 `Status` 不设 `ExitCode`
         的成功通路就会**误删有效产物**，不能留；
      ② 判据换成「确证失败」、仍无宽限 ⇒ 残留 **1**（m_001 那一刻还没落到终态 ⇒ 清理被合理跳过，但文件留下了）；
      ③ 于是在 `TimeoutException` 前加收尾宽限、拿 `_processor.IsRunning` 当判据 ⇒ **无效**，仍残留 1
         （消费循环先退出，`IsRunning` 早已 false，而各项还在收尾）；
      ④ 宽限改按“终态计数不再增长”判定（静默约 1 秒放行、最长 5 秒封顶）⇒ 残留 **0**、日志 **2 行 `[清理]`**。
      ⇒ **结论：保守判据必须配收尾宽限才成立**；单看第①步的“0 残留”会误以为严格判据更好，其实它是以误删为代价。
      双向验证 = 中止场景（删得掉）+ 全套产品门禁（44/19/26/9 逐条读产物文件，删错了必红）。
    - **P1-3 元数据回退的非原子替换**：`File.Delete(output)` + `File.Move(temp, output)` 中间一步失败 ⇒ 产物**整个丢失**
      （注释却写着“原子替换”）。改为 `TryAtomicReplace`：同卷一步 `File.Move(…, overwrite:true)`（真原子改名），
      跨卷抛 IOException 时退回“复制到目标目录旁路件 + 同目录原子改名”；任何失败路径原产物都完整在位。
    - **未做**（结构级，留给用户拍板）：让每条通路都先写临时件、成功后原子替换最终路径。那要改动所有通路的输出路径与
      `-y` 语义（≈15 处 + 命令行拼装），属“不得改变软件结构”的边界；本次只补了事后处置。
20. **P1-1 / P1-2（已修 2026-09-15）：外部进程守卫**
    - `CjxlService` 两处原来是裸 `await process.WaitForExitAsync()`（连 `ct` 都不接）⇒ 点“停止”或 cjxl 挂死时
      该项永不结束、并发槽位永久泄漏。现统一走 `WaitGuardedAsync`（linked CTS + `Kill(entireProcessTree)`，
      与 Djxl/Cjpegli 同形；阈值取 30 分钟而非二者的 5 分钟，因为 cjxl 在 effort 9/10 + 大图上合法耗时更高）。
      `RunWithOptionsAsync` 新增可选 `ct` 形参，四处调用点已全部接上。
    - `FfmpegRunner` 原来只有取消、没有超时。现加**挂死守卫**：判据是「既无输出、**且**每轮询周期 CPU 增长 <1 秒」
      持续 30 分钟 ⇒ 杀进程树、写 `[超时]` 日志、返回 `HangExitCode = -124`（区别于真实编码失败）。
      为什么不能只看输出：`RawColorPipeline` 等调用带 `-loglevel error`，全程可能一条输出都没有；
      为什么不能只看总时长：合法的慢编码真的会很久。
    - 两次失败尝试（都是先量出来才改对的）：
      ① 用 `cmd /c timeout /t N` 当挂死替身子进程 **不成立** —— `timeout.exe` 在 stdin 被重定向时立刻报错退出
         （实测退出码 125、耗时 0 秒），换成 `ping -n 600 127.0.0.1 >nul` 才是“静默 + 不耗 CPU”的等待型进程；
      ② CPU 判据最初写成“有任何增长就算在干活” ⇒ 等待型进程每周期也有几毫秒，守卫**永远不会触发**；
         改成“单周期 ≥1 秒”后才正确。
    - 探针：`ServiceProbe runner`（正常阈值下跑，两条快检：静默但正常退出不被误杀、取消按时传播）（⚠ #20(0919) 后为 **8** 条；本行"两条"是**当时**的叙述，不改写）；
      `ServiceProbe runner --hang` 会真等满阈值，**只在把阈值临时调低的变异构建下跑**
      （实测：阈值 1 分钟 ⇒ 60 秒整终止、退出码 -124、日志含 `[超时]`，三项全 PASS）。
21. **P1-5 / P1-6（已修 2026-09-15）：临时件不再泄漏、项结束后路径可重跑**
    - **P1-6**：UltraHDR 解码产生的 `uhdr_linear_*` 目录原来只塞进 `_pendingRawDirs`，而该列表**只在 `Stop()` 里处理**；
      headless 的成功路径根本不调 `Stop()` ⇒ 正常跑完一批，目录全部留在盘上。现在由
      `DecodeUltraHdrToLinearAsync` 把 `tempDir` 随返回值交给调用方，登记进**本项**的 `tempDirsToCleanup`（项级 `finally` 删除），
      `_pendingRawDirs` 字段与 `CleanupPendingDirs()` 一并删除（已无使用者）。
      双向实测：把登记那一行注释掉 ⇒ 一次 UltraHDR→PNG 后残留 **1** 个目录；恢复登记 ⇒ 残留 **0**。
    - **P1-5**：预处理（UltraHDR PFM / RAW TIFF / MaxDimension 预缩放）会**改写** `captured.InputPath`，
      临时目录又随项删除 ⇒ `RequeueStoppedAndFailed` 拿着已消失的临时路径重跑必失败。
      现在任务级 `finally`（在临时目录删除之后、`_onItemUpdated` 之前）把 `InputPath` 还原为进入本项时的真实源路径。
    - ⚠ **本次没有一起修掉的既存边界**（说清楚，别以为顺带解决了）：
      ① 终态 `Status` 是在处理链**内部**设置的，而还原发生在 `finally` ⇒ 完成行仍会显示临时名
        （实测：`[1/1] hdr.pfm → 已完成`），要改成显示原名得把终态回调挪到 finally 之后；
      ② `RestoreMetadataAsync` 用的也是**改写后**的输入 ⇒ UltraHDR 输入的 EXIF 回填实际取自临时 PFM（无 EXIF），
        这是既有设计问题，属色彩/元数据链路，未在本次动。
    - ⚠ 另记一条测量口径教训：我曾试图在 `ServiceProbe` 里直接驱动真实队列来测这两条，结果项卡在 GainMap 解码里
      3 分钟不到终态——**产品代码没问题**（同一个 Debug 构建用 headless CLI 2 秒跑通），是探针宿主缺工具链路径状态
      （`InitExternalTools` 只回填 ffmpeg/exiftool）。该模式已撤回，改用 CLI 做双向实测。
22. **S-P1-1 / S-P0-2（已修 2026-09-15）：设置落盘改成"原子写 + 可回滚 + 失败必须出声"**
    - `Save()` 由 `void` 改为 `bool`，并新增 `LastSaveIssue` / `LastSavedTo` / `LastLoadIssue`：
      旧写法是 `File.WriteAllText(exe目录\settings.json, json)` 外加 `catch { }` ⇒ ①写到一半崩溃/磁盘满会打断**唯一一份**设置；
      ②装到只读目录时 `UnauthorizedAccessException` 被吞，用户以为保存了。
    - 现在：临时件 → 备份旧版为 `.bak` → 原子改名（任何一步失败，目标文件都还是原来那份完整内容），
      并按 **`_loadedFrom` 写回本次的读取来源**（修“Load 能从 %APPDATA% 读、Save 只会写 exe 目录”的不对称）；
      便携目录不可写时回退用户配置目录并**把回退写进 LastSaveIssue**；
      `--settings` 点名的文件**绝不回退**（写不进去就报失败——偷偷换个地方存更糟）。
    - headless 侧新增两处出声：设置文件解析失败 ⇒ `⚠ …已按默认值继续`（旧的是静默重置后跑完整批）；
      PLAN 回填 ffmpeg 目录那次 `Save()` 失败 ⇒ 说明“本次回填只在当前进程有效”。
    - 背书探针：`ServiceProbe settings`（7 条，已纳入 `_run-step3-gates.ps1` 默认清单）。
      覆盖：首存无告警 / 不留 `.tmp` / 落盘内容==内存值 / 二存留下 `.bak` 且内容是上一版 /
      目标被独占锁住时 `Save()==false` 且给出原因 / **失败后目标文件一字未动** / 坏文件产生 `LastLoadIssue`。
      ⚠ 探针自己也踩过一处：拿着 `FileShare.None` 的句柄再去 `File.ReadAllText(同一文件)` 必抛 IOException（实测），
      要“失败后内容未变”就得先把内容取出来做参照、锁块内只管断言失败。
23. **P2-3 / P2-1 均已修（2026-09-15）：子进程两条流与日志并发写**
    - **P2-3（已修，实测确认是死锁不是理论）**：热点探测里 `RedirectStandardError = true` 却从不读 stderr。
      管道缓冲（Windows 约 4KB）写满后子进程阻塞在写上，而我们 `ReadToEnd()` stdout 永等 ⇒ 该项卡死、并发槽位永久占住。
      现在 QueueProcessor 的四处（位深 / 尺寸 / `avif` 流角色 / `IsAnimatedAvifAsync`）统一走
      `ReadStdoutDrainingStderrAsync`（两条流并发读干再 `WaitForExit`）。
      证据（`ServiceProbe procstreams`，2 条断言）：用 `cmd` 往 stderr 灌 ~199KB 时，
      “只读 stdout”的写法 10 秒不返回（判定卡死），并发排空则正常读完。
      **全仓已扫完（当前 64 处）**：`RedirectStandardError = true` 共 64 处声明（`ExifToolService` 10、`QualityAnalysisService` 4、
      `Cjxl/Djxl/Cjpegli/MediaInfo/IccProfile/GainMapDecoder/EncoderDetection/PlatformServices/MainWindow/FfmpegCommandBuilder` 等），
      逐方法补上排空后，结构锁 `tests/scripts/_probe-stderr-drain-scan.ps1` 报 PASS，并已纳入 `_run-step3-gates.ps1` 的脚本清单
      ⇒ 以后新开通路忘了排空会当场变红。
      ⚠ 事实核对：纳入批跑后它**立刻又抓到第 64 处**（`QueueProcessor.IsAnimatedJxl`，该方法是本次会话期间被**并发编辑**加进来的，
      不是我改的）⇒ 两点结论：锁确实有效；但“已扫完 N 处”这类计数只对当时那一版源码成立，重跑才算数。
      ⚠ 本轮两次真实自纠（都记下来免得重犯）：
      ① 一次性补丁器把 `X.BeginErrorReadLine()` 插进了 `Process.Start(new ProcessStartInfo { **这里** })` 的初始化器内部，
        以及插在“无花括号 `if (p != null)` 头 + 它的 `{}` 块”之间（等于把 if 体摘成无条件执行）——**是编译器抓到的**（3 处语法错），
        逐处复核后才修好；结论：批量插行必须让编译器当第一道闸，别只看替换条数。
      ② 审计初版把我写在 `RedirectStandardError = true` 行尾的注释“排空见下方 BeginErrorReadLine”当成了排空证据 ⇒ **自己喂绿**；
        加固为剥掉整行/行尾注释后只认代码，重跑仍 PASS 才算数（这也是本仓反复出现的“文本锁被注释满足”坑）。
    - **P2-1（已修 2026-09-15，零调用点改动）**：`item.Log += s` 是「读快照 → 拼接 → 写回」，同一项由多线程写
      （本项线程 + ffmpeg/cjxl 的 stdout/stderr 回调），实测 8×400 次追加 **3200 → 433 行**（后写者整段覆盖）。
      最终修在 `QueueItem` 属性内部：getter 顺手把“本次交给本线程的快照长度”记进 `[ThreadStatic]` 字段，
      setter 在锁内只把 `value` **超出该快照长度的部分**接上 ⇒ 无损、不重复，241 处 `X.Log += …` 一行都不用改
      （`X.Log += e` 的 get/拼接/set 必在同一条线程上，所以这个通道是可靠的；全仓已核实无“整体赋值 Log”的用法）。
      验证：修好后 `ServiceProbe procstreams` ①报 **3200/3200 PASS**；**变异验证**——把 `snapshotUsable` 强行置 false
      （退回旧的整段覆盖）⇒ 同一断言立刻变红（**2499/3200**）⇒ 这条锁确实承重。
      ⚠ 保留一条反面教训：先试过“按最长公共前缀缝合”，**不成立**——纯字符串不带快照长度，两次追加共享前缀时
      （`t1-23;` 与 `t1-6;` 公共前缀到 `t1-`）会被缝成一行：分隔符计数 3200 全对，完整 token 只剩 2398。
      **损坏比丢行更糟**，所以快照通道对不上时（整体赋值、或刚截断过）宁可退回旧语义，也绝不猜。
24. **P2-4 / P2-2 已修（2026-09-15）：GIF→AVIF 帧表撑爆命令行 + 失败项被汇总吞掉**
    - **P2-4（已修，比审查报告的估计严重一个数量级）**：两步法把**每帧绝对路径**拼进 avifenc 命令行。
      Windows `CreateProcess` 命令行总长上限 32767 字符，默认临时目录下一帧约 98 字符 ⇒ **约 330 帧就满**，
      实测 **420 帧**的普通 GIF 直接 `ErrorStartingProcess … 文件名或扩展名太长`（退出码 1、一个字节产物都没有）。
      修法：① 帧文件只传**相对文件名** + `WorkingDirectory = 帧目录`（一帧 11 字符 ⇒ 上限约 2900 帧，≈8.5 倍）；
      ② 输出路径显式 `Path.GetFullPath`，与“相对化”解耦；③ 启动前按命令行字符数**预检**，
      超限就给出帧数/字符数/上限/两条可行做法并显式失败（不再让 Win32 抛一句用户没法办的话）；
      ④ 日志回显改成「首帧 … 末帧（共 N 帧）」摘要——旧写法把整条命令行灌进 `item.Log`
      （420 帧实测那一行约 4.2 万字符；4200 帧的独立实测为 390715 字符）。
      前后对照（同一 420 帧素材）：修前 `失败: ErrorStartingProcess … 文件名或扩展名太长`；
      修后 `已完成 (avifenc 两步法)`，3000 帧那条则命中预检报「33209 字符 > 32767」。
    - ⚠ **测动图 AVIF 帧数别用 `-select_streams v:0`**：实测它对 420 帧和 72 帧产物**都只报 1**（那是主图），
      会把成功误判成“只编了一帧”。正确口径：`-select_streams v -count_frames` 后取**各流最大值**（本例 2/3 号流各 420）。
    - **P2-2（已修，用户可见）**：`ConsoleQueueObserver.OnItemCompleted` 把 `_failedCount++` 写在
      “算得出耗时才进”的 `if (elapsedSec.HasValue)` 分支里，而专项通路（gif→avif 等）在**子方法内**就发布了终态，
      此时 `QueueProcessor` 还没写 `CompletedAt` ⇒ 失败项走 else 分支：**既不计入失败、也按 Info 打印**。
      实测一条失败任务汇总成「完成 1 项, 失败 0 项」（退出码却是对的 1，因为 Program 另按 `items` 统计）。
      修法：计数与耗时解耦、两个分支共用同一 `level`；并发自增改 `Interlocked`、读取改 `Volatile.Read`。
      ⚠ `Console.ForegroundColor` 那一半其实已被 `_writeLock` 覆盖（`WriteToOutputs` 只在锁内调用），审查报告按行号列的“交错”已不成立。
    - 回归锁：`tests/scripts/verify-gif-avif-framelist.ps1`（16 条，已进 `_run-step3-gates.ps1` 默认清单）。
      含混合队列（1 成功 + 1 失败 ⇒ 必须报「完成 2 项, 失败 1 项」）与“失败后不留半成品”（与 P1-4 口径对齐）。
      ⚠ 本锁的 ② 必须用 `--log-level debug` 跑：**成功项的 `item.Log` 只在 Debug 级刷出**（失败项才按 Error 刷），
      Info 级下这两条断言会空转（第一版就是这么抓到 1 条假红的）。
    - 顺手清掉一条长期假红：`peak` mode 需要文件参数，批量清单里裸跑只打 usage ⇒ 记 `fail=1`。
      已从 `_run-step3-gates.ps1` 默认探针清单移除（真实峰值断言在 `verify-color-peak.ps1`，25/0）。
25. **P2-5 已修（2026-09-15）：子进程输出编码统一为显式 UTF-8（52 个重定向块 / 92 行）**
    - **缺陷机制**：`StandardOutputEncoding`/`StandardErrorEncoding` 为 `null` 时，.NET 用**宿主进程的
      `Console.OutputEncoding`** 解码子进程输出；zh-CN 控制台（代码页 936）等非 UTF-8 宿主下，
      含非 ASCII 的路径/日志被解成乱码。审查报告只列了 `CjpegliService`/`DjxlService` 两个文件，
      **实测远不止**：结构锁一次扫出 **70 个 `ProcessStartInfo` 启动块中 52 个**未声明编码
      （重定向 stdout 66 处、stderr 63 处）。
    - 修法：按块补齐两条 `…Encoding = Encoding.UTF8`（**18 文件 / 92 行**），与仓内既有先例一致
      （`cjxl`/`ffmpeg`/`exiftool` 一族早已是 UTF-8）；无 `using System.Text;` 的 6 个文件用
      `System.Text.Encoding.UTF8` 全限定名，不动 using。
    - **背书探针**：新增 `ServiceProbe encoding`（6 条，已进 `_run-step3-gates.ps1` 默认清单）——
      ① 子进程产出的**原始字节**逐字节等于 UTF-8 编码（证明“声明 UTF-8”是正确选择，不是随手挑的）；
      ② 声明 UTF-8 后 stdout/stderr 两条流逐字还原（中文 + `·` + 希腊字母）；
      ③ **负控**：同批字节按非 UTF-8 单字节代码页解必不还原 ⇒ ② 的断言能识别解码错误，不是空跑；
      ④ **现场复现**：宿主输出编码切到非 UTF-8 且**不声明**解码编码 ⇒ 实测 `OUT:ä¸­æè·¯å¾Â·æµè¯_å¾ç-Î±Î²Î³.png`
      ⇒ 确认 .NET 的默认回退**就是**宿主 `Console.OutputEncoding`，缺陷真实存在（不是从旧框架时代继承的推断）；
      ⑤ 探针自身仍以 UTF-8 输出（否则门禁日志自身会乱码）。
      ⚠ ⑤ 初版写成 `Console.OutputEncoding == Encoding.UTF8` **假红**：该属性返回的 `UTF8Encoding` 实例带控制台
      默认回退字符，与 `Encoding.UTF8` 静态实例的 `EncoderFallback` 不同 ⇒ `Encoding.Equals` 为 false；
      改为比 `CodePage`（65001）。
    - **结构锁**：`tests/scripts/_probe-proc-encoding-scan.ps1`（已进 `_run-step3-gates.ps1` 脚本清单）。
      判据以 **`ProcessStartInfo` 对象初始化块**为单位（比方法级精确，不会漏掉“块外消费”的写法）：
      重定向某条流 ⇒ 同块内必须声明对应编码；只认代码不认注释（沿用 stderr 排空锁的加固）。
      刻意不区分“文本读取”与“BaseStream 二进制搬运”——后者该设置无副作用，而分类判据脆弱（消费点常在块外）
      ⇒ 一律要求声明，换取锁的不可绕过性。
      **变异验证**：删掉 `CjpegliService` 里一行 `StandardOutputEncoding` ⇒ 锁立刻报
      `CjpegliService.cs:141 缺 stdout 编码`；恢复后复跑转绿。
    - ⚠ **本轮两处真实自纠（批量插行的坑，务必记住）**：
      ① 补丁器把编码行插到**单行初始化块**（`new ProcessStartInfo { … };` 全在一行）的 `};` **之后** ⇒ 落在块外。
      这次是**结构锁**先报出来的（`ExternalToolsDetector.cs:288` 仍缺 stdout 编码）。
      ② 更隐蔽的一类：`RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true`
      —— 重定向属性**行尾没有逗号**（后续属性还在同一行），插在行后同样越出属性列表 ⇒ **4 处 CS1003**；
      这次结构锁抓不到（块内确实有编码声明），是**编译器**抓到的。
      结论：批量插行必须让 ① 结构锁 ② 编译器 两道闸都过；只数“插了几行”不算验证。
      收尾自查口径（可复用）：逐条检查编码行的**前一行（去注释后）是否以 `,` 结尾**。
    - 回归：`dotnet build ffmpegPictureUI.sln` **0 警告 0 错误**；`_probe-stderr-drain-scan.ps1` 仍 PASS
      （本次未新增重定向）；headless 实机冒烟——中文名素材 `中文路径·测试_图片-αβγ.png` → jpg 成功
      （控制台与产物文件名均无乱码，`ffprobe` 复核 `mjpeg 320x200`）。
    - ⚠ 环境坑（本机）：`dotnet` 还原要求 `ProgramData`/`APPDATA` 存在，沙箱内二者为空 ⇒
      `NuGet.targets(796,5) error : Value cannot be null. (Parameter 'path1')`。绕过：`dotnet build --no-restore`。
    - ⚠ 顺带复验（交接文档 §C.1 的“引用前重跑”同样适用于**“未修”结论**）：
      `JXL_ISSUES_HANDOVER.md` 列的 7 项 P2 遗留里，`JxrService` 超时守卫、`RawService` 取消杀孤儿进程、
      `CjxlService.RunAsync` 死代码**均已修**（现读源码可证）；而“`DjxlService`/`CjpegliService` 5 分钟硬超时偏短”
      **不是缺陷**——`CjxlService` 注释明确记录了“30 分钟给 cjxl、5 分钟给 djxl/cjpegli”的有意设计
      （“本守卫的目标是杜绝永久挂死，不是砍掉慢编码”）⇒ 不要“顺手改回”。仍开放：24 处
      `FfmpegRunner.RunAsync` 调用点**全部未传 `ct`**（`ct` 是可选参数），“停止”后 ffmpeg 子进程仍跑到自然结束。
      （**该条已在第 26 条修掉**；另发现第 27 条的 APNG 静默损坏，见下。）
26. **取消传播已补齐（2026-09-15）：`FfmpegRunner.RunAsync` 的 `ct` 覆盖 23/24 + 1 处有意豁免**
    - **先纠正一个统计口径错误**：上一轮按行 `grep "ct"` 得出「24 处全部未传」，**是错的**——
      多行调用的 `ct` 在下一行（如 `ProcessGainMapJpegAsync` 的 `ffmpegPath, ct)`），按行匹配必然漏计。
      改用**括号配平**取参数列表重扫：**24 个调用点里 14 个早已传 ct，真正未传的是 10 个**。
      ⇒ 教训：判断“有没有传某参数”必须按调用点解析，不能按行 grep。
    - 修法：给 9 处补 `, ct`（`ProcessCjxlAsync` / `ProcessCjpegliAsync` / `ProcessJxrAsync` /
      `PreConvertToPngAsync` / `ProcessAvifToGifWebpAsync`×4 / `ProcessGifToAvifViaAvifencAsync`）。
    - **唯一豁免：`RestoreMetadataViaFfmpegAsync`（有意不传）**。理由（读代码得出，非推测）：
      它跑在**编码已经成功之后**，是秒级 `-c copy` 重封装；传 ct 后用户在收尾瞬间按「停止」会走到
      `catch (OperationCanceledException) → Status="已停止"`（`QueueProcessor.cs:602`），
      而 `finally` 对「已停止」会调 `TryDiscardPartialOutput`（`:614`）**把已完成的合法产物删掉**
      ⇒ 那是把「修取消传播」变成「按停止就丢成品」的**回归**。已在源码处写明理由，防止后人“顺手补上”。
    - **结构锁** `tests/scripts/_probe-cancel-propagation-scan.ps1`（已进门禁脚本清单）：
      以**括号配平**解析每个调用点的实参列表，要求出现独立 `ct` 标识符；
      豁免名单**硬编码在脚本里**（不是注释标记，避免被注释喂绿），新增豁免必须改脚本、评审可见。
      **变异验证**：删掉一处 `, ct` ⇒ 锁立刻报 `QueueProcessor.cs:2419 ProcessAvifToGifWebpAsync`。
    - ⚠ 写锁时踩到两个 PowerShell 坑（都记下）：① 方法名提取要先剥泛型尖括号
      （`Task<(string a, string b)> Foo(` 里的括号会被当成参数列表）；② **双引号字符串里的弯引号
      `“…”` 会被 PowerShell 当作字符串定界符**，导致消息被截断在 `（` 处 —— 统一改用 `「」`。
    - 回归：headless 实机跑通 7 条通路（png→jxl / png→jpg / gif→avif / avif→gif / jxr→png / jxl→jpg /
      png→avif），全部「完成 1 项, 失败 0 项」且产物经 ffprobe 复核。
27. **【数据完整性】PNG 产物曾无签名（2026-09-15 发现并修复）：`-c:v apng` 少了配套的 `-f apng`**
    - **现象**：`JXR → PNG` 产出的 `src.png` 只有 2404 字节、**首字节即 `fcTL`**，既无 PNG 8 字节签名、
      也无 `IHDR`、也无 `IEND`；`ffprobe` 报 `Invalid PNG signature`，任何解码器都读不了
      ——而软件**退出码 0、状态「已完成」**，属**静默损坏**（应用层完全没有感知）。
    - **根因（三层定位，全部实测）**：
      ① 手工复现软件日志里的那条命令 ⇒ 产物与软件输出**逐字节一致**（同样 2404 字节、同样只有 fcTL+IDAT）
         ⇒ 排除应用后置链（元数据恢复 / `PngCicpService`）嫌疑；`PngCicpService` 本身有
         `TryParseHeader` 前置校验，损坏文件会被它拒绝，不可能是它写的。
      ② 手工对照四种命令（同一素材、同一 ffmpeg 构建 git-2026-09-01-c27482a18d）：
         `-c:v apng`（**无** `-f`）⇒ **缺签名**；`-c:v apng -f apng` ⇒ 正常；
         `-c:v png` ⇒ 正常；默认 ⇒ 正常。
      ③ 源码：`QueueProcessor.CloneOptionsForFfmpeg` 把 `"png" or "apng" => "apng"` 都映射成 apng 编码器
         （为让 `.png` 容器能承载动画），但 `-f apng` **只**写在 `FfmpegCommandBuilder` 的 `case "apng"` 分支里
         ——`png` 目标走的是 `case "png"`，拿不到那一句 ⇒ 编码器是 apng、封装器却是 image2/png。
         受影响通路正是所有经 `CloneOptionsForFfmpeg` 的调用点：`ProcessCjxlAsync`/`ProcessCjpegliAsync`
         的 ffmpeg 回退、JXR→ffmpeg 标准编码。
    - 修法：在 `BuildArguments` 的**输出路径装配之前**，按「**实际编码器**」而非「目标格式」判定：
      若 `options.Encoder == "apng"` 且参数里没有 `-f apng` 就补上（`case "apng"` 已加过则不重复）。
      修后 `jxr2png` 产物签名正常（2432 字节、结构完整），`gif→apng` 仍产出合法 10 帧 APNG。
    - **门禁** `tests/scripts/verify-png-signature.ps1`（已进门禁脚本清单）：断言 PNG/APNG 产物
      **①以 PNG 签名为首 ②含 IHDR ③含 IEND**；并带**负控**——手工跑 `-c:v apng`（不带 `-f apng`）
      必须被判为缺签名 ⇒ 证明断言能识别该损坏、不是空跑。
      **变异验证**：把修复条件临时置 `false` 后重建 ⇒ 门禁报 `FAIL jxr2png —— 缺 PNG 签名（首字节 0x00）`、退出 1；
      恢复后 4/4 PASS。**另一处真实红**：门禁优先用 Release 产物，而 Release 还是修复前的旧二进制 ⇒
      同样报缺签名（说明该门禁对“改了源码没重建”也敏感）。
    - ⚠ 门禁自身踩的三个坑（都已修，留作模板）：
      ① **`& exe` 在某些宿主里静默起不来**（无输出、也不生成文件）⇒ 调外部 exe 一律 `Start-Process -Wait`（本仓范式）。
      ② **`Run-Exe $ff @('a','b')` 这种「函数 + 数组实参」绑定会把数组元素当成多个位置参数**，
         `$argv` 只拿到第一个 ⇒ ffmpeg 实际只收到 `-y`，**静默什么都不生成**。改用单个参数字符串。
      ③ **固定工作目录会串味**：上一轮的输出子目录被占用时 `Remove-Item -Recurse` 只删掉根下文件、
        子目录残留，导致「素材看起来生成了又消失」的假象 ⇒ 改为每次运行用 GUID 工作目录。
      ④ `Write-Host` 不进可捕获输出流（`| Out-File` 抓不到）⇒ 门禁统一 `Write-Output`（与 `_probe-*.ps1` 一致）。
28. **P2-8 已修（2026-09-15）：每任务内存预留由拍脑袋常数改为按队列内容分档（附实测模型）**
    - **先实测**（Release 构建，40ms 采样进程 `WorkingSet64`，素材为 16-bit + bt2020/PQ 的 PNG）：

      | 场景 | 像素 | 峰值工作集 | 峰值私有提交 |
      |---|---|---|---|
      | GainMap 纯托管编码 4K | 8.29 MP | **680.3 MB** | 657.3 MB |
      | GainMap 纯托管编码 1080p | 2.07 MP | **218.8 MB** | 189.4 MB |
      | 普通 ffmpeg 通路 4K | 8.29 MP | **53.7 MB** | 22.8 MB |

      ⇒ GainMap 通路线性拟合 **≈ 65 MB + 74 MB/百万像素**（≈64 B/像素 × 4 份 `float[像素×4]`
      线性缓冲，加 JPEG/PFM 缓冲）；普通通路全程流式，不进托管线性缓冲。
    - **结论**：旧值「200 MB/任务」对 4K GainMap **低估 3.4 倍**（高并发 + 大图可 OOM），
      对普通通路又**高估 3.7 倍**（白白压低并发）。
    - 修法：`ClampConcurrencyByMemory(requested, perTaskMB)` 改为接收每任务预留，由
      `EstimatePerTaskMemoryMB()` 按队列内容分两档给出：**plain 64 MB / linear 768 MB**
      （实测 680.3 + ~13% 余量）。判据只看**入队期已知的选项标志**
      （`Options.JpegGainMap` 或 `RawService.IsRawFile`），**不做文件嗅探**（读盘/调 ffprobe 不该在 `Start()` 里）。
    - ⚠ **已知缺口（已写进源码注释）**：Ultra HDR JPEG 作为**输入**（解码侧）没有便宜的入队期标志，
      仍按普通档预留；该通路同样会开 4K 量级线性缓冲。要补应在 `Add()` 时做轻量头部嗅探（读几十字节即可）。
    - **门禁** `tests/scripts/verify-gainmap-memory.ps1`（已进门禁脚本清单，4 条）：
      ① 从源码解析 `plainMB`/`linearMB`，要求**实测峰值 ≤ 声明值**（预留不得低于现实）；
      ② 普通通路实测 ≤ 普通档预留；
      ③ GainMap 实测 > 普通档预留（证明两档划分有依据；哪天 GainMap 变省内存了这条会红，提示合并档位）；
      ④ **负控**：用 64 MB 去套 GainMap 实测必须判为「预留不足」⇒ 断言①能识别低估，不是空跑。
      实跑：`声明 64/768 · GainMap 679.7MB · 普通 54.4MB · PASS=4 FAIL=0`。
      ⚠ 断言①是「实测 ≤ 声明」的**严格**比较 ⇒ 声明值必须留真实余量：
      首版写死 680 而实测 679.6，余量仅 0.4 MB，必然假红。
    - ⚠ 环境提示：在被沙箱接管的 agent 环境里 `Remove-Item -Recurse` 会被拦截并**静默失败**
      ⇒ 门禁「成功即清理工作目录」不生效，`tests/output/gmmem_*` 会累积（每个约 70MB，含 4K 素材）；
      正常 shell 下无此问题。
29. **S-P1-3/4/5/6 已修（2026-09-15）：IPC 簇（服务端循环 / UI 线程编组 / 缓存并发 / 客户端超时）**
    - **S-P1-4（服务端循环，含一个更严重的连带缺陷）**：旧写法在循环**外**先
      `await server.WaitForConnectionAsync(token)` 把第 1 个连接建立起来，循环里又调**阻塞的**
      `server.WaitForConnection()` —— 此刻管道**已经处于已连接状态**，该调用抛 `InvalidOperationException`；
      异常从 fire-and-forget 的任务里逃出去（无人 await）⇒ **第一个客户端到来时服务器就死了**，
      之后所有连接静默失联，调用方（App 启动处）看不到任何错误。
      **直接证据（修复前实测）**：客户端连上后发 Ping，客户端侧报 `IOException: Pipe is broken`
      —— 即服务端已随异常退出（`using var server` 释放 ⇒ 对端读到断管）。
      修法：统一在循环内 `await WaitForConnectionAsync(token)`；**每个连接各自 try/catch**
      （单个客户端读写失败只断开它自己，不再整体失联）+ `finally { Disconnect() }` 准备下一个连接。
      修复后客户端**连接成功**。
      ⚠ **根因已定位并修复其一；往返仍未闭环（两项独立问题）**：
      **① 根因（已修）：IPC 协议是 JSON Lines，但消息被序列化成了多行 JSON。**
      全局 `AppJsonContext` 开了 `[JsonSourceGenerationOptions(WriteIndented = true)]`
      （settings.json / 预设文件需要缩进以便人读，不能动），而 `IpcService` 两端都用它序列化 IPC 消息
      ⇒ 一条消息变成 `{` / `  "Id": 1,` / `  "Type": 6` / `}` 这样的四行。
      接收端按行读只能拿到碎片，`JsonSerializer.Deserialize` 全部失败并被 `catch { continue; }` 静默吞掉
      ⇒ **服务端永不回包**，客户端只能一路等到超时。
      **实测证据**：把服务端临时改成裸字节读写后，追踪到 `raw read n=30`（确实收到客户端 30 字节），
      紧接着被切成 4 段（长度 2/11/12/1）——正是多行 JSON 的形状。
      ⇒ 结论：**IPC 从来没有真正工作过**（两端各自的代码看着都没问题，症状只表现为"客户端超时"）。
      修法：新增 `IpcJsonContext`（`WriteIndented = false`，与全局上下文同类型、只改这一项），
      `IpcService` 全部切到它（`AppJsonContext.Default.Ipc*` → `IpcJsonContext.Default.Ipc*`）。
      ⚠ 旁证：`IpcService` 里本就有一个
      `private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };`
      —— **声明了却从未被使用**，正是"本意要给 IPC 用非缩进选项"的残留。
      **② 第二个根因（已修）：服务端在管道上用 `StreamReader`/`StreamWriter` 会抛异常并被静默吞掉。**
      证据链：a) 裸字节客户端「刚连上就写失败：`Pipe is broken`」⇒ 服务端 accept 之后很快**关闭了读端**；
      b) 换成裸字节服务端后，服务端**确实收到了**客户端请求字节（追踪到 `raw read n=30`）；
      c) **控制组**（自包含的裸管道：同样 4 实例 / `ConnectAsync` / `PipeOptions.Asynchronous`）
      往返完全正常 ⇒ 环境、连接方式、实例数三项均已排除；
      d) 管道名按用户 SID 派生，已确认没有其它进程占用（无 `FfmpegGui.exe` 在跑、`\\.\pipe\` 下无同名管道）。
      ⇒ 结论：`new StreamReader/StreamWriter(server, …)` 这一步在这条管道上抛异常，被
      `catch (Exception) { }` 吞掉，随后 `using var server` 释放 ⇒ 对端看到断管、往返永远拿不到响应。
      修法：服务端改用**裸字节读写 + 手工分行**（`IndexOfNewline` + `StringBuilder` 累积），协议仍是 JSON Lines。
      **③ 结果（已闭环）**：跨进程实测 `ipc-client` **0.07 秒**拿到 `pong`；裸字节客户端收到
      `{"Id":7,"Success":true,"Message":"pong"}`（**单行**，佐证 ① 的修复生效）。
      `ServiceProbe ipc` 的往返断言（同进程）**0.01 秒**通过。
      **变异验证**：把 `IpcJsonContext` 的 `WriteIndented` 改回 `true` ⇒ 该断言立刻转红
      （`FAIL IPC 往返（同进程）… 实耗 5.01s，实得「请求超时」`）⇒ 锁承重。
      ⚠ 往返断言已进 `ipc` 探针（在门禁默认清单里），但它用**按用户 SID 派生的固定管道名** ⇒
      跑门禁时**不能**有正在运行的 GUI 实例。诊断工具 `ipc-server [秒]` / `ipc-client` / `ipc-raw-client` /
      `piperaw-server` / `piperaw-client`（后两个是**环境对照**用的自包含裸管道）均已保留。
    - **S-P1-6（客户端超时，已运行验证）**：旧写法三层都无界 ——
      ① `reader.ReadLineAsync()` 完全没传 token，签名上的 `timeout` 形参也**从未被使用**（内部硬编码 10 秒）；
      ② 只给 `ReadLineAsync` 传 token **仍不够**：管道读一旦在飞行中，取消不保证能中断它；
      ③ **实测最要命的是写入**：对端接受连接但不消费时 `WriteAsync` 一直挂着
         ⇒ 读阶段的超时**根本没机会生效**（实测：静默对端 + 2 秒超时仍然挂死）。
      修法：写 / 刷 / 读三段共用同一 deadline，全部用 `Task.WhenAny(task, Task.Delay(remaining))` 有界化；
      `timeout ≤ 0` 回退 10 秒；半开连接转成「连接已断开: …」而非超时。
      **背书**：`ServiceProbe ipc` ①——对端接受连接但永不回包，客户端 **2.0 秒**返回「请求超时」
      （旧写法此处无限挂起）。
    - **S-P1-5（能力缓存并发，已运行验证）**：`FormatCapabilitiesService._cache` 是普通 `Dictionary`，
      写入发生在 `InitializeAsync`（后台初始化线程）、读取发生在 UI 线程 ⇒ 并发读写可抛异常/读到损坏状态；
      且 `DetectLocalFfmpegCapabilitiesAsync` **原地 Add** 到已发布对象的 `SupportedColorSpaces`
      （`List<T>` 非线程安全）。修法：① 字段改 `ConcurrentDictionary`；
      ② `ToLower()` → `ToLowerInvariant()`（缓存键全是 ASCII 小写字面量，土耳其语等区域会把 "I"
      变成无点 "ı" ⇒ 查不到键）；③ 色域列表改 **copy-on-write**（在**新列表**上累加后整体替换属性
      ⇒ 读者只会看到完整的新旧两态，不见中间态）。
      **背书**：`ServiceProbe ipc` ②③④——字段类型反射断言 + 8 线程 × 400 次并发读与初始化写并发
      （异常 0 次）+ 大写「PNG」命中缓存。
    - **S-P1-3（UI 线程编组，代码级修复，无运行时门禁）**：IPC 服务器跑在线程池线程上，而 `MainWindow`
      是 Avalonia 控件树（**非线程安全**）；旧写法直接 `AddItemsToQueue` / `PauseQueue` / `ResumeQueue` /
      `Close`（6 处调用点）。修法：`HandleRequestAsync` 在
      `mainWindow != null && Avalonia.Application.Current != null` 时经 `OnUiThreadAsync`
      （`Dispatcher.Post` + `TaskCompletionSource`）编组到 UI 线程；无 Avalonia（headless/探针）直接执行，
      保持原语义。⚠ 该条**没有**运行时断言（需要真 UI 线程 + 窗口），属**代码审查结论**——引用时请注明。
    - **仍未做（需拍板）**：S-P1-2「单实例未接线」（`TryConnectAsync` 全仓无调用点 ⇒ 第二次启动真双开）。
      接线会改变启动行为（第二个实例改为转发给第一个），属行为/结构变更，等用户定。
    - ⚠ 探针自身的坑：往返用例里我用了**同步** `client.Write(...)` 发坏数据，那一步**无界**，
      是当时探针挂死的帮凶之一 ⇒ 探针里所有可能阻塞的点都必须有界。
    - ⚠ 本轮还踩到两个「静默失败」陷阱（写探针时记住）：
      ① **`Console.WriteLine` 的输出在重定向到文件时可能丢失**（被 `timeout` 硬杀时缓冲区没落盘）
         ⇒ 定位卡点要靠**直接 `File.AppendAllText`** 写追踪文件，别信 Console；
      ② **编译失败时 `dotnet build | tail` 很容易被忽略**，于是跑的是**旧二进制**、
         得到与上次完全一样的结果 ⇒ 一度把我带偏两轮。看构建输出必须显式 grep `error`。
30. **IPC 请求处理器修复（2026-09-15）：响应 `Id` 未回显 + 谎报成功**
    - 传输打通（第 29 条）之后，处理器层的缺陷才变得可观测：
      **`HandleGetStatus` / `Pause` / `Resume` / `Stop` 五处响应把 `Id` 写死成 `0`**，
      而客户端是按 `resp.Id == req.Id` 匹配响应的 ⇒ 这四类请求**永远匹配不上**，
      客户端只能一路读到超时（服务端其实已经把动作做了）。
      症状是「Pause/Resume/Stop/GetStatus 总是超时」，极易被误判成传输问题（本轮之前一直在被误判）。
    - 顺带修**谎报成功**：`HandlePauseAsync` / `HandleStopAsync` 在既无 `MainWindow` 也无 `orchestrator`
      时仍返回 `Success = true`（`"Paused (orchestrator)"` / `"Stopped"`），实际什么都没做。
      谎报成功比报错更糟（客户端会以为生效了）⇒ 改为显式失败并说明原因；
      `HandleStopAsync` 的 Message 同时明示「MainWindow 侧只做了 Pause：它没有公开的 StopQueue」。
    - `HandleGetStatus` 的**假数据**：`IQueueOrchestrator` 只暴露 `IsRunning`，没有队列长度/完成数/失败数，
      旧写法把三个计数写死 0 却报 `Success = true` / `"ok"`。协议字段不凭空改语义（仍保持 0），
      但把「计数不可用」写进 Message，避免客户端把 0 当成真实状态。
      ⚠ 要真正提供计数需给 `IQueueOrchestrator` 加成员（**结构性改动，未做**）。
    - **背书**：`ServiceProbe ipc` ⑥⑦⑧（发 Id = 7/8/9，断言「回显 Id + 拿到真实响应 + 不谎报成功」）。
    - ⚠ **变异验证抓出一个空断言（务必记住这条）**：首版断言只查 `Id == req.Id`，
      把 `Id = req.Id` 改回 `0` 之后**依然全绿** —— 因为 `SendRequestAsync` 超时返回的**合成响应**
      也带 `req.Id`（走超时兜底时 Id 恰好等于 `req.Id`，看起来"回显正确"）。
      ⇒ **断言必须排除"兜底路径"的等价输出**（这里要同时校验 `Success` 与 `Message` 是处理器给的）。
      收紧后同一变异立刻转红：`FAIL GetStatus … Success=False「请求超时」`。
31. **P2-7 实测结论（2026-09-15）：不是缺陷，不要动**
    - 审查报告的担忧是「`ProcessAsync` 200ms 轮询 + `tasks` 列表 `%10` 条件清理 ⇒ 长队列列表持续增长 / 吞吐受限」。
      **实测与读码都不支持这两个担忧**：
    - **轮询不是派发节流**：`await Task.Delay(200, ct)` 只出现在**空队列**的 `else` 分支；
      队列非空时派发路径上没有任何 delay（读码确认：从 `%10` 检查到 `tasks.Add(t)` 之间没有 Delay 语句）。
    - **吞吐实测**（101 张 64×64 PNG → jpg，Release 构建，同一批素材只改并发数）：

      | 并发 | 总耗时 | 每项 |
      |---|---|---|
      | 1 | **267.8 s** | 2651 ms |
      | 8 | **50.4 s** | 499 ms |

      加速比 **5.31×**（理想 8×，效率 66%）⇒ **并发有效扩展，派发侧不是瓶颈**。
      若 200ms 真的限流，8 并发的总耗时会被钉在 101 × 0.2s ≈ 20s 附近且**与并发数无关**——实测不是。
    - **`tasks` 列表有界**：`tasks.Count` 每派发一项 +1，而 `%10` 检查在每次循环开头执行 ⇒
      计数必然经过每个 10 的倍数、清理稳定触发；且 `sem.WaitAsync(ct)` 在并发饱和时会阻塞派发循环
      ⇒ 列表规模上界约「并发数 + 10」，**不是无界泄漏**。
    - ⇒ **结论：P2-7 不需要改动**（盲改反而有回归风险）。若将来真要优化吞吐，瓶颈在**别处**：
      单项固定开销实测 **2.65 s（而且只是 64×64 的小图！）**，debug 日志显示每项要起
      **1 次 ffmpeg（带 `-vf iccgen`）+ 2 次 exiftool（元数据恢复、隐私清理各一次）+ 若干 ffprobe**
      ⇒ 成本主要是**外部进程启动**。候选优化：把两次 exiftool 合并成一次调用
      （属后置链改动，需单独评估并配回归，本轮未做）。
32. **元数据隐私门禁（2026-09-15 新增）：默认剥 GPS 的隐私承诺此前**没有任何覆盖****
    - 背景：`FfmpegOptions.StripExifGps` 默认 true ⇒「默认剥掉 GPS」是面向用户的**隐私承诺**；
      同一条后置链还负责「把源文件的描述性元数据复制回来」。两者都会**静默失效**
      （不报错、退出码 0、日志照样写"隐私清理完成"）⇒ 既存门禁只查色彩标签，没人查这两件事。
    - 新增 `tests/scripts/verify-metadata-privacy.ps1`（已进门禁清单，3 条）：
      ① 默认转换：输出**无 GPS**；
      ② 默认转换：输出**仍有**描述性元数据（ImageDescription / Make）⇒ 证明不是"把元数据全剥了"；
      ③ **负控**：`--no-strip-gps` 时输出**有 GPS** ⇒ 证明 ① 的断言能识别 GPS 的有无（非空跑）。
      实跑：`PASS=3 FAIL=0`（素材 GPS = 39°54'15.12"）。
    - **变异验证**：把默认改成不剥 ⇒ ① 立刻转红（`FAIL ① 默认输出仍有 GPS=…`、exit=1）。
      ⚠ **变异的第一版打错了层**：先改 `FfmpegOptions.StripExifGps = false`，门禁**依然全绿** ——
      因为 CLI 路径会用 `CliParser` 自己那份默认值（`Options.StripGps = true`）**覆盖**模型默认值；
      改在 `CliParser` 那层才转红。⇒ 这条记录两件事：
（a）门禁确实承重；（b）**隐私默认值目前有两处独立真值**（`FfmpegOptions.StripExifGps`
与 `CliParser.Options.StripGps`），只改一处不生效 —— 属潜在隐患，建议合并为单一真值（未做）。
⚠ **2026-09-21 复核：结论不变，但「形态」已变** —— `Options.StripGps` 现已改为 **`bool?` 三态**
（`null` = 用户未显式指定），故它**不再是**「第二个默认值」；但**转换点仍硬编码**
`StripExifGps = opts.StripGps ?? true`（`CliParser.cs:519`）⇒ **第二个真值仍在**，
改 `FfmpegOptions.StripExifGps` 的默认值依旧不生效 ⇒ **隐患与「未做」结论均成立**。
33. **IPC SubmitJob 端到端（2026-09-15 新增）：IPC 的核心功能此前从未被验证过**
    - 背景：IPC 的设计用途是「把任务提交给**正在运行的实例**」，但 IPC 传输本身此前从未跑通
      （见第 29/30 条）⇒ `HandleSubmitJob` → `ExpandInput` → `BuildOutputPath` → orchestrator 交接
      这条**核心链路从未被任何测试覆盖**。
    - `ServiceProbe ipc` 新增 ⑨⑩：
      ⑨ `SubmitJob` 受理（发 Id=11，断言回显 + `Success` + Message 含 `Submitted`）；
      ⑩ **端到端产物**：轮询等待输出文件出现并断言非空 ⇒ 真正验证
        「IPC → 编排器 → 队列 → ffmpeg → 文件」。
      实跑：`PASS SubmitJob 受理（Id=11 Success=True「Submitted 1 jobs」）` +
      `PASS SubmitJob 端到端产出文件（…/ipcsubmit_*/out/a.jpg）`。
    - 实现要点：无 `MainWindow` 时处理器走 orchestrator 分支，而 `DefaultQueueOrchestrator` 是 **internal**
      ⇒ 探针用**反射**构造并转成公开的 `IQueueOrchestrator`。
      ⚠ 最初想用 `InternalsVisibleTo`（仓库对 `GainMapTestHost` 已有此惯例），但**改 csproj 会触发 NuGet 还原**
      —— 本环境还原不可用（§C.4 的 `ProgramData` 坑）⇒ 反射是这里唯一不动生产配置的路子。
    - ⚠⚠ **那次 csproj 改动一度把构建彻底搞坏**（`NETSDK1060 读取资产文件时出错 … Value cannot be null (path1)`），
      连 Debug 都编不过；**把内容逐字恢复后仍然坏** —— 因为 MSBuild 是按 **mtime** 判定"需要还原"的。
      解法：`touch -d "2026-09-01 00:00:00" src/FfmpegGui/FfmpegGui.csproj`，把 mtime 调得**早于所有还原产物**
      （`obj/project.assets.json` 是 09-08、`obj/project.nuget.cache` 是 09-15 02:12 ⇒ 要取 09-01 才同时满足两种配置）。
      ⚠ 只调到"早于 `nuget.cache`"能让 Debug 过，**Release 仍红**；必须早于 assets 文件。详见 HANDOVER §C.4。
34. **后置链性能：把隐私清理并入元数据复制（2026-09-15，实测提速 15%）**
    - **先把瓶颈量准**：exiftool 在本机**单次启动就要 ~850ms**（`-ver` 也要 850ms；用自带
      `exiftool_files\perl.exe` 直接跑 `exiftool.pl` 同样 ~850ms ⇒ 慢的是 **Perl 启动本身**，
      不是自解压包装器），而元数据复制/隐私清理各自只需 ~50-180ms ⇒
      每项两次 exiftool 调用里 **约 1.7s 全是启动开销**，占单项 2.65s 的约 **70%**。
    - 修法（**保守版**）：默认只开 `StripExifGps`，而 GPS 与 XMP **只可能来自源文件复制**
      （ffmpeg 主路径带 `-map_metadata -1`，不会往产物写元数据）⇒ 把「复制后剥掉」改成
      **复制时排除**（`--GPS:all` / `--XMP:all`），并在调用方跳过那次单独调用。
      `ExifToolService.CanAbsorbStrip()` 只吸收这两项；`time`/`camera`/`exif:all` 是**成组标签**、
      逐条排除容易漏 ⇒ **保持原两步路径不动**。
    - **实测**（101 项 64×64 PNG → jpg，并发 8）：**50,434 ms → 42,764 ms，快 15.2%**（省 7.7s）。
      单项耗时噪声大（~2.9–3.2s），故用批量对比。
    - ⚠ **门禁当场抓出一个实现错误**：第一版只把 `-GPS:all` 从**包含列表**里去掉，
      结果元数据隐私门禁 ① 立刻红（`默认输出仍有 GPS`）—— 因为 **GPS 标签属于 EXIF 组**，
      `-EXIF:all` 已经把它们带进来了，必须用 `--GPS:all` **显式排除**。
      这正是「先写门禁再优化」的价值：没有那条断言，这个错误会以「日志说清理完成、实际没清」的形式静默上线。
    - ⚠ **覆盖限制（如实记录）**：另一条复制路径 `CopyMetadataAsync`（完整复制，只在
      backend ∈ {Cjpegli, Dng} 时走到）的同类改动**没有运行期断言** —— 可达的两条路都不好用：
      `--format jpegli` 本身是坏的（见第 35 条），`--format dng` 走 raw 专用链路。
    - 顺带记录：exiftool 的 `-stay_open True -@ argfile`（常驻进程）能把启动开销降到接近 0
      （可再省 ~850ms/项），但属**架构级改动**，未做。
35. **【已修】`--format jpegli` 产物扩展名非法 ⇒ 转换必然失败**
    - 实测（默认选项，输入 jpg）：`--format jpegli` 生成的输出路径是 `…\src.jpegli`，
      ffmpeg 报 `Unable to choose an output format for '…src.jpegli'`、退出码 **-22**、**无任何产物**。
    - 依据：`jpegli` 在 `FfmpegCommandBuilder.cs:1333` 与 `FfmpegOptions.cs:389` 里**被当作合法格式**
      （有质量映射），但输出扩展名是按 `"." + format` 拼的 ⇒ `.jpegli` 不是 ffmpeg 认识的扩展名。
    - 与第 34 条的改动**无关**（未触碰输出命名）；属**既有 bug**，未修 —— 需先定 `jpegli` 的语义：
      它是**格式**还是**编码器后端**？若算格式，输出扩展名应为 `.jpg`。
    - ✅ **2026-09-21 复核：已修**（本条的「未修」已过期）。**实测**（`Canon_EOS40D.CR2 --format jpegli`）：
      `rc=0`、队列「完成 1 项, 失败 0 项」、产物 **`Canon_EOS40D.jpg`**（扩展名合法）。
      判据落在 `EncoderDetectionService.cs:38`：`EncoderBackend.Cjpegli => fmt is "jpg" or "jpeg" or "jpegli"`
      ⇒ `jpegli` 现按 **`jpg` 格式的编码器别名**处理（即本条提出的语义问题的答案：**算编码器后端，不算格式**）。
      ⚠ 但 `verify-metadata-privacy.ps1` 末尾的附注仍以「`--format jpegli` 是坏的」为由**排除**一条通路
      ⇒ 该附注**同样过期**（2026-09-21 已同步更正；见该脚本 `附注` 段）。
36. **GUI 实机验证（2026-09-15 新增）：GUI 能启动 + 其 IPC 可用 + 提交的任务真的被处理**
    - 背景：交接文档把「GUI 启动 + 鼠标操作路径未测」列为**提交前的前置条件**。本轮至少把
      **GUI 启动 + IPC 全链路**这一段跑通了（**鼠标交互仍未测**）。
    - 步骤（可复现）：
      ① GUI 的 IPC 服务端由 `EnableIpcServer` 控制，而该设置**默认 false**（`AppSettings.cs:72`）
         ⇒ 要测就先在**构建输出目录**的 `settings.json` 里改成 true（那是构建产物、不是用户安装目录；
         **改前备份、测完还原**）；
      ② 启动 `FfmpegGui.exe`（不带 `--headless`）；
      ③ `ServiceProbe ipc-client` —— 连真实 GUI 的管道并发 Ping；
      ④ `ServiceProbe ipc-submit <输入文件> <输出目录>` —— 提交任务并等产物。
    - **实测结果**：
      · GUI 启动成功并存活（进程可见、工作集 ~200–257 MB）；
      · ③ `PASS 跨进程 Ping 往返拿到 pong（实耗 0.05s）` ⇒ **App 侧接线正确**；
      · ④ `PASS 向运行中实例 SubmitJob 受理（Success=True「Submitted 1 jobs」）` +
        `PASS GUI 队列真的处理并产出（…\g.jpg，3527 字节）`。
    - ⇒ **S-P1-3（UI 线程编组）由此获得运行期证据**（此前只有代码审查）：`HandleSubmitJob` 走的是
      `mainWindow != null` 分支，`AddItemsToQueue` / `StartProcessing` 必须跑在 UI 线程上才能把项排进队列
      并启动处理；编组坏了就不会有产物。
    - ⚠⚠ **重要修正（同日，此前写错过一次）**：本节初版把「GUI 存活约十几秒后自行退出」记成
      **"本环境是非交互会话"** —— 这是**错的**。用截屏核实后发现：该会话就是**用户正在使用的实时桌面**
      （截屏里能看到浏览器/IM/助手等真实窗口）。
      ⇒ 更可能的解释是：**弹出的窗口被用户关掉了**（而不是"环境限制"）。
      ⇒ 由此得两条硬约束（务必遵守）：
        ① **不要为了测试在用户的实时桌面上反复弹窗**——那是打扰；
        ② **不要用鼠标自动化去驱动用户的实时桌面**（会点到他真实的窗口里）——鼠标交互路径应由**用户自己**测，
           或在**专用/隔离会话**里测。
      ⇒ 因此本节的验证范围到此为止：**启动 + IPC 全链路**已验，**鼠标交互不测**（改由用户执行）。
      ⇒ 截屏文件已删除（含用户隐私窗口），不留存、不作为交付物。
37. **`verify-gainmap-managed` 从「长期红」修成 20/0（2026-09-15）—— 先定性、再换等价断言，不是放宽**
    - **先定性（关键一步）**：本宿主里该门禁报 **PASS=13 / FAIL=18**。逐条查下来，18 条里 **12 条是宿主伪影**：
      §1b 的 5 条 + §2 的 6 条都走**调用运算符 `& <exe>`**（`& $probe` / `& $et`），
      而本宿主的 `& exe` **静默失败** ⇒ 输出为空 ⇒ 断言全假红。
      **证据**：同一条命令（`ServiceProbe gainmap <jpg> <sec.jpg>`）直接跑得到 `pass=6 fail=0`，
      含 `images=2`、`mpf[1] … soi=True`、`iso min=…`、`secondary_written=… bytes=1471`。
      ⇒ 把门禁里的 `& exe` 全部改成 `Start-Process`（新增 `Exec` 助手）后：**PASS=13/18 → 19/12**。
    - **剩下 12 条全是陈旧契约**（6 条 §2 XMP 交叉校验 + 6 条 §5 PQ 底）。两条都用**代码证据**定性，不靠猜：
      · **PQ 底已移除**：`JpegGainMapBaseHdr` / `jpeg-gain-map-base-hdr` / `BaseHdr` 在 `src/FfmpegGui` 下
        **零引用**（grep 无任何命中），且 `GainMapEncoder` 只产出 SDR 底（sRGB / Rec.2020 SDR）；
        实测 `--jpeg-gain-map-base-hdr true` 被**静默接受且无任何效果**（也没有未知/忽略提示）。
      · **XMP 数据源不存在**：现行实现只写 **ISO 21496-1**，exiftool 读不到任何 hdrgm 标签
        ⇒ 原「ISO vs XMP」交叉校验无从比对。
    - **换成的断言（等价或更强）**：
      · §2 → 用**可推导的期望值**校验：源峰值 1000 nits / 白点 203 nits ⇒ `max` 应 = log2(1000/203) ≈ 2.3004；
        并要求 `min=0 / gamma=1 / channels=1` 符合「单通道 SDR 底」契约；
      · §5 → 断言**当前真实行为**：带该废弃开关的产物与不带它的产物**降级后逐像素一致**
        （实测 PSNR=99 dB）⇒ 既把「开关已废弃」钉成可回归契约，又天然构成负控（一旦有差异就红）。
    - **负控验证**：把 §2 的期望白点临时改成 100 ⇒ 立刻转红
      （`FAIL max=2.3 与推导值 3.3219 不一致`，PASS=19 FAIL=1）；恢复后 **PASS=20 FAIL=0**。
    - ⇒ 结论：**该门禁不再需要拍板**（「PQ 底是否受支持」已由代码证据回答：已移除），
      且从此在本类宿主里也能跑（不再依赖 `& exe`）。
38. **性能追查：单项耗时的真正构成 —— 瓶颈是「本机的每进程创建开销 ~800ms」，不是软件设计**
    - **单项拆解**（320×200 PNG → jpg，Release，本机）：

      | 组成 | 耗时 |
      |---|---|
      | 应用自身启动（`--help` 实测） | **~800 ms** |
      | 工具检测（启动后那 6 个工具的探测） | 只加 **~30 ms**（说明检测是快的/有缓存） |
      | ffmpeg 转码（含 `-vf iccgen`） | **~770 ms** |
      | ffprobe 色彩元数据探测 | **~800 ms** |
      | exiftool 元数据复制（已并入隐私清理） | **~935 ms** |
      | 合计 | **≈ 3.3 s**（与实测单项 3176 ms 吻合） |

    - **关键对照（决定性的那一测）**：`where.exe cmd` —— **一个工作量≈0 的极简原生 exe** ——
      实测 **858 / 799 / 859 / 823 ms**；`ffmpeg -version`（只启动不做事）**812 / 808 ms**。
      ⇒ **本机每创建一个进程就要 ~800 ms**（极可能是实时杀毒逐个扫描新进程映像），
        与工具本身无关、与应用设计无关。
      ⇒ 也解释了为什么连"转一张 320×200 小图"的 ffmpeg 也要 770 ms —— 那 770 ms 几乎全是启动。
    - ⇒ **由此得出最高杠杆的优化方向：减少"每项的进程创建次数"**。
      当前每项 **3 次**（ffmpeg + ffprobe + exiftool；应用启动是每批 1 次）⇒
      第 34 条的 exiftool 合并（3→2）在本机单项上值 **~800 ms**，批量并发 8 实测省 **15.2%**。
      **下一个候选：把 ffprobe 色彩探测改成进程内读取**（PNG 的 cICP/IHDR 本可像
      `PngCicpService` 那样直接解析字节，不必起进程）—— 但那会动到**色彩决策的输入来源**，
      属高风险改动，**未做**（需单独评估 + 配回归）。
    - ⚠ 这条也解释了两件事：
      ① 为什么门禁套件"全量一次跑 >300s" —— 每个脚本内部都在反复起进程；
      ② 为什么并发能显著提速（50.4s→42.8s 那类）—— 进程创建延迟可被并行掩盖。
      ⇒ 评估本仓任何性能数字时，**先确认是不是这台机器的每进程开销在主导**。

39. **门禁运行器自身的三处缺陷 + 一个探针假红根因（2026-09-15 第三轮）——「假红 / 假未执行」比红更危险**

    起因：核对 §B 基线表时，同一套探针在**同一份工作树**上跑出两种结果。逐一定性如下。

    **(a) 陈旧 Release 产物 ⇒ 同时假红 + 假「未执行」**
    `_run-step3-gates.ps1` 优先取 Release 探针（fallback 才用 Debug），但**从不校验新鲜度**。
    实测：Release `ServiceProbe.exe` 比源码旧 ⇒ `encoding`/`ipc` 两个新 mode **在旧二进制里不存在**，
    表现为 `exit=2 ⚠未执行(未知 mode)`；同时旧 `FfmpegGui.dll` 让 `runner` 两条断言转红（退出码 1 / 0.2s）。
    **两种症状都裹在"看起来很正常的输出"里**，极易被当成真实结论写进基线表。
    ⇒ 已加**新鲜度硬闸**：跑前按**项目配对**比较 mtime（`src/FfmpegGui/**` ↔ `FfmpegGui.dll`、
    `tests/ServiceProbe/**` ↔ `ServiceProbe.exe`），陈旧就 `exit 2` + 打印新旧时间戳 + 重建命令。
    ⚠ 必须**配对**：`FfmpegGui.dll` 只在 src 改动时才更新，拿它与探针的 `Program.cs` 比会**永久误报 STALE**（本轮踩到过）。
    ⚠ **【现值演进，2026-09-24】**上面这两对是**当时**的覆盖面；现已扩到 **4 对**（补 `src/**/FfmpegGui.exe`、
      `tests/UiTestHost/**` ↔ `UiTestHost.exe`）⇒ 见本节第 106 条。**"按项目配对"这条纪律不变**，新增两对同样只与自己项目的源码比。

    **(b) `Start-Process -FilePath "pwsh"` ⇒ 整批脚本门禁从未执行**
    `Get-Command pwsh` ⇒ **NOTFOUND**（宿主自身是 PowerShell 7.6.6，位于 `C:\Program Files\PowerShell\7`）。
    ⇒ 已改用 `(Get-Process -Id $PID).Path`（实测拿到 `pwsh.EXE`，起子脚本 `exit=0` 且正常 PASS）。
    ⚠ 该坑在 §C.4 早已"记录在案"，但**记录 ≠ 修复** —— 脚本一直没改，所以那 23 个脚本门禁此前**全部没跑**。

    **(c) 并发编辑会话 ⇒ 基线是移动靶**
    实测跑基线期间 `QueueItem.cs` 在 14:00:39 被改（我 14:00:38 刚构建完，差 **1 秒**）；
    另见 `QueueProcessor.cs` 13:55:55、三个 JXL 服务 13:56:07。
    ⇒ 已加**跑后源码漂移复核**：跑完再取一次最新 mtime，变了就打
    `[WARN] 跑期间源码被改动 ⇒ 本次结论不对应当前工作区（不要把它当基线）`。

    **(d) 探针假红根因：替身用了裸命令名 `ping`**
    同一 exe、同一断言：**bash 直接跑 `pass=2`**，**经 `Start-Process` 跑 `fail=2`**（退出码 1 / 0.2s）。
    排除法：不重定向同样红（⇒ 不是重定向）→ 重建后仍红（⇒ 不是产物陈旧）→
    `procstreams` 探针同样用 `cmd.exe` 却 pass（⇒ 不是 cmd 不可用）。
    最后给断言补上**子进程输出**，一次拿到 cmd 原文：
    **`'ping' is not recognized as an internal or external command, operable program or batch file.`**
    `where.exe ping` 在 `Start-Process` 侧 **exit=1**，而宿主里 `Get-Command ping.exe` 能找到
    ⇒ **本宿主 `Start-Process` 传给子进程的环境块不含 System32**（`$env:PATH` 是 PowerShell 变量，
    与进程实际环境块不一致）。
    **修法**：替身改绝对路径 `C:\Windows\System32\PING.EXE`（**断言语义一字未改**）⇒ 两种执行方式结果一致。
    **生产不受影响**：`FfmpegRunner` 用的是 `AppSettingsService.Current.FfmpegPath` 绝对路径。
    ⚠ 副产物：`ProbeRunnerGuard` 现在失败时会带上子进程输出（`Dump()`）——
    只报「退出码 1」无法区分「守卫误杀」与「子进程自己报错」，这个诊断缺口本轮耽误了最多时间。

    **修正后的探针基线（2026-09-15，Release 产物新鲜）**：16 个 mode 合计 **353 pass / 0 fail**
    （contract 95 · wire 26 · selftest 36 · iccname 10 · plan 39 · curve 39 · matrix 15 · ootf 11 ·
    bt2446 18 · decision 4 · engine 32 · runner 2 · settings 7 · procstreams 3 · encoding 6 · ipc 10）。

    **(e) 脚本门禁：经运行器批量跑会被 `& exe` 伪影污染（本轮确证）**
    运行器用 `Start-Process` 起**子 pwsh 进程**，而 `& exe` 在这类受限宿主里**静默失败**
    （不报错、不生成文件、退出码还好看）⇒ 依赖它的断言全部假红。
    **A/B 对照（同机同时刻，`verify-color-caps.ps1` 的前置素材步骤）**：

    | 写法 | p3.png | p3.icc |
    |---|---|---|
    | `& $ff` / `& $et`（脚本原写法） | **未生成**（0 B） | **0 B** |
    | `Start-Process -Wait -PassThru` | **4125 B** | **584 B**（>200 B 阈值 ⇒ 断言本应 PASS） |

    （584 B 与 `verify-colorfmt-matrix` 实测的 `584B` 一致，交叉印证。）
    ⇒ 本轮 caps 6/6 · prophoto-faithful 0/1 · png3-interop 1/8 · tonemap 0/2 ·
    decision-delivery 34/5 · gif-avif-framelist 16/1 **全部是伪影，不是回归**；
    也**不要**拿它们与上一轮 §B 的数字比（上一轮在能跑 `& exe` 的宿主里跑，两边都"对"，差异全来自宿主）。
    ⇒ 结论：**包 1（`& exe` → `Start-Process`，28 脚本 ~90 处）完成前，经运行器批量跑脚本门禁不可用**。
    ⚠ 该失败**与 PATH 无关**（脚本里用的是绝对路径），与 (d) 的「环境块缺 System32」是**两个独立的坑**，别混。

    **(f) 跑期间源码漂移（本轮实际发生）**
    完整门禁跑到 14:11 时告警：`CliParser.cs`（**包 2 的文件**）被改动 ⇒
    本次脚本结论**不对应当前工作区**，已按 (c) 的规则如实标注，不作基线。

40. **HLG OOTF 接线级门禁 + H1 值域修复（2026-09-16）——「类正确」不等于「接线生效」**

    **起因**：`JXL_ISSUES_HANDOVER.md` 声称「✅ P1-① HLG OOTF 接线全部完成」，核查后发现：
    ① HLG 字段与 `Bt2100Ootf.Apply/ApplyInverse` **只加在 `ColorKernels` 的 float RGBA 路径**
    （`TransformRgba`/`TransformRgba8`），而该路径在 `src/` 中**零调用点**（仅探针使用）；
    ② 生产路径 `TransformRgb48leCore` 与 `MeasurePeakLinearRgb48` **完全没有 HLG 分支**；
    ③ 实测 HLG→SDR 走 **H2（zscale）**，所以主路径结果**是对的** —— 缺陷被「另一条后端」掩盖了。

    **为什么既有门禁抓不到**：`ServiceProbe ootf`（11 条）只测 `Bt2100Ootf` **类本身**（数学），
    `engine`（32 条）只测 **P3→sRGB（SDR）**；两者都不覆盖「HLG 在真实内核路径上生效」。
    ⇒ **新增 `ServiceProbe hlgwire`（3 条）**，判据不依赖实现细节：
    HLG 0.75 码值是 BT.2100 的漫反射白约定 ⇒ 场景线性 0.26496 ⇒ 经 OOTF(1000nit) 得 **203.2 nits**
    ⇒ 归一化到「1.0 = 源峰值」即 **0.2032**（与 `ootf` 第 11 条锚点交叉自洽）。

    **修复前的实测（缺陷确证）**：
    ```
    HLG 0.75 → 场景线性 0.26496 → OOTF 显示 203.2 nits → 归一 0.20315
    FAIL ① 峰值测量路径应用了 OOTF（期望 0.2032，实得 0.2650）
    FAIL ② 主转换路径应用了 OOTF（期望线性 0.2032，实得 0.2650）
    ```
    两条路径都给出 **0.2650 = 场景光原值** ⇒ 完全漏了 OOTF。

    **修法（3 处，PQ/SDR 零影响）**：
    - `TransformRgb48leCore`：解码后、`* LinearScale` **之前**施加 `Bt2100Ootf.Apply` 并 `/Peak` 归一化；
      表快路径加 `&& !spec.SrcIsHlgScene`（OOTF 非查表可表达）。
    - `MeasurePeakLinearRgb48`：HLG 时**禁用表路径**（OOTF 依赖**三元组** Y_S，不是逐通道查表），
      同样 Apply + 归一化。
    - `DecodeToLinearInPlace`（float 路径）：原实现**顺序错**（OOTF 排在 `* scale` 之后）且**缺归一化**，
      一并修正以与 H1 对齐。
    ⚠ **顺序不可颠倒的根据**：`Apply` 的 α=L_W 使输出成为 **nits 绝对值**，且它**对输入缩放敏感**
    （输入 ×s ⇒ 输出 ×s^γ）⇒ 先乘 `LinearScale` 会把 L_W 的语义搞错。

    **验证**：`hlgwire` **3/3**（三条路径 0.2031/0.2031/0.2032，一致）；
    全量探针 **356 pass / 0 fail**，其余 16 个 mode 数字**逐一不变** ⇒ **PQ/SDR 零回归**。
    「修复前红 → 修复后绿」即**变异验证**（断言承重，非空断言）。

    ⚠ 遗留：H1 路径的**目标侧**（`DstIsHlgScene`，即输出 HLG）未实现（H1 默认不可达），
    已在代码注释标明，属开放项。

41. **色彩 token 规范化缺陷（CLI 别名 + GUI 矩阵词表）——「面向用户的词表」与「ffmpeg 的常量名」是两套（2026-09-16）**

    **缺陷 A（CLI，P1）**：`--help` 明确宣传 `--color-trc <linear|srgb|pq|hlg|...>`，但
    `CliParser.ApplyExtraOption` 把值**原样**存进 `FfmpegOptions.ColorTrc`，
    而 `BuildColorArgsSplit` → `DecideOutputColor` → `BuildArguments` 把它**原样**拼成
    `-color_trc <值>`（放在 `-i` 之前作输入色彩声明）。ffmpeg 的 `-color_trc` **只认自己的常量名**：

    ```
    $ FfmpegGui.exe --headless -i in.png -o out -f png -q 100 --color-primaries bt2020 --color-trc hlg
    [png] Unable to parse "color_trc" option value "hlg"
    [in#0/png_pipe] Failed to open codec in avformat_find_stream_info
    ❌ 失败 (退出码 -22)          ← 零产物，且报错只提解码器，用户看不出是 token 不被支持
    ```
    实测词表：`srgb` / `pq` / `hlg` **全部失败**（`linear` 侥幸通过——它是两边唯一同名的值）。

    **缺陷 B（GUI，P1）**：`ColorMatrixCombo` 的 10 个选项里 **7 个是坏的** ——
    `bt2020ncl`/`bt2020cl`/`ycgcocn`/`ycgcocs`（zimg 风格名）、`bt2020`（裸族名）、
    `smpte432`/`smpte431`（**基准名**被误当矩阵）。更糟的是 `FfmpegCommandBuilder` 的白名单
    `validColorspaces` **照抄了这份脏词表** ⇒ 不但不拦，还保证它们被写进 `-colorspace`。

    **实测（ffmpeg git-2026-09-01-c27482a18d，`publish/PLAN/ffmpeg-full`）**：
    | 值 | `-colorspace` | zscale `min=/m=` |
    |---|---|---|
    | `bt709` `bt2020nc` `bt2020c` `ycgco` | ✅ | ✅ |
    | `gbr` | ❌（ffmpeg 只认 `rgb`） | ✅ |
    | `bt2020ncl` `bt2020cl` `ycgcocn` `ycgcocs` `bt2020` `smpte432` `smpte431` | ❌ | ❌ |

    **修法（单一真值，规范化在**选项构造边界**完成）**：
    - `ColorSpaceRegistry` 新增 `NormalizeTrcToken` / `NormalizePrimariesToken` / `NormalizeMatrixToken`
      / `ToFfmpegColorSpaceName` / `FfmpegColorSpaceArg` / `FfmpegColorSpaceNames`（实测枚举）。
    - `FfmpegOptions.ColorPrimaries/ColorTrc/ColorMatrix` 改为**在属性 setter 里规范化**
      ⇒ CLI / GUI / 预设 JSON / 程序内注入**四条路径一次覆盖**，模型内恒为规范名。
    - `FfmpegCommandBuilder` 的白名单换成 `FfmpegColorSpaceNames`（实测枚举），并保留
      「RGB 矩阵族不写给 YUV 编码器」的旧口径；`QueueProcessor` 两处 `-colorspace {ColorMatrix}`
      改走 `FfmpegColorSpaceArg`（否则 `gbr` 仍会漏进命令）。
    - `ColorMatrixCombo` 词表收敛为 `bt709 / bt2020nc / bt2020c / ycgco / gbr`（ffmpeg 与 zscale 两侧都成立）。

    **验证**：新增门禁 `verify-color-token-normalize.ps1`（**18/0**）：
    ① 白名单自身逐项对真实 ffmpeg 探测；② GUI 词表逐项对 ffmpeg **与** zscale 探测；
    ③ CLI 别名必须产出原生名 + **扫描整条命令行**确认每个 `-color_primaries/-color_trc/-colorspace`
    值都被 ffmpeg 接受（这条是与"矩阵取什么值"无关的核心不变量）；
    ④ 负控：别名与原生名必须产出**完全相同**的命令行。
    **变异验证**：把 `ColorTrc` 的 setter 规范化注释掉 ⇒ **18/0 → 11/7**，失败信息精确指出
    `-color_trc hlg 不被 ffmpeg 接受`；改回即 18/0。

    ⚠ **两个脚本自身的坑**（写门禁时踩到，记以免重复）：
    - `"...zscale=min=$value:m=$value"` —— PowerShell 把 `$value:m` 当**作用域限定变量**解析
      ⇒ 参数退化成 `min=`，zscale 报 `Unable to parse "min" option value "="`，
      看上去像"zscale 不支持 bt709"这种荒谬结论。必须写 `${value}`。
    - 用 `-f png` 测 `-colorspace` **永远拿到空值**：png/tiff/apng/jxl 是 RGB 原生容器，
      主路径 `isRgbNativeFmt` 会跳过 `-colorspace` 写入 ⇒ 必须用 jpg 之类 YUV 原生格式。

42. **「HLG OOTF headless 未触发」结论被推翻 —— 是测试素材缺陷，不是产品缺陷（2026-09-16）**

    **上一手交接的结论**（`docs/HANDOVER.md` 交接清单第 1 项，标记为唯一 ⚠️ 遗留）：
    「HLG 与 PQ 源同图转 SDR 像素逐位一致（PSNR=inf）——headless 路由未走进程内 ColorKernels 的 OOTF 分支」。

    **复核：该结论不成立**。实测交接所用素材 `<test-dir>/{hlg_src,pq_src}.png`：
    ```
    md5(hlg_src.png) = 2a037da450f73c5dffa27bdb8253109f
    md5(pq_src.png)  = 2a037da450f73c5dffa27bdb8253109f   ← 完全相同
    ffprobe hlg_src.png ⇒ color_transfer=unknown, color_primaries=unknown
    ffprobe pq_src.png  ⇒ color_transfer=unknown, color_primaries=unknown
    ```
    ⇒ 两个"源"其实是**同一个无标签文件**：跑两次当然逐位一致。**PSNR=inf 与产品行为无关。**

    **素材缺陷的成因**：本机 ffmpeg 的 **PNG 编码器不写 cICP**——
    `ffmpeg -color_trc arib-std-b67 ... out.png` 产物的 `color_transfer` 仍是 `unknown`
    （实测；同一命令产 AVIF 也只有 `color_space` 落盘）。PNG 的 CICP 必须由应用侧
    `PngCicpService` 写入（实测应用产物 cICP 字节：HLG=`09 12 00 01`、PQ=`09 10 00 01`，
    即 primaries=9/transfer=18|16/matrix=0/full=1）。

    **用真标签素材重做（同像素、仅传递函数不同）**：
    | 用例 | 产物标签 | 实测峰值 | PSNR vs 源 |
    |---|---|---|---|
    | HLG(arib-std-b67)→SDR，引擎 + hable | 决策=Map，后端=**H2(zscale)** | **962 nit** | 25.67 dB |
    | PQ(smpte2084)→SDR，引擎 + hable | 决策=Map，后端=H2(zscale) | **10000 nit** | 18.75 dB |

    **HLG→SDR 与 PQ→SDR 的 PSNR = 23.89 dB（不是 inf）** ⇒ HLG 分支**确实被走**，
    且两条曲线各自生效（若 HLG 被当 PQ 解码，同像素必然得到同一个实测峰值）。
    ⇒ 交接项 1 **关闭为"测试素材缺陷"**；H1 值域问题已由第 40 条修复，主路径（H2）本就正确。

    **新增门禁 `verify-hlg-route.ps1`（13/0）——补上一直缺失的「接线级」覆盖**：
    - 第 0 节**素材自检**：两源 md5 必须不同 + ffprobe 必须读出 `arib-std-b67`/`smpte2084`
      （这条就是本次素材缺陷的**直接回归锁**）；
    - 第 1 节：两条路径都成功 + 日志含「决策：Map」「后端=H2」+ 实测峰值不同 + PSNR 有限且 < 40dB；
    - 第 2 节**控制组（有牙）**：同一 HLG 源跑两次 ⇒ PSNR 必须 = inf（证明第 1 节的 PSNR 判据
      能识别"逐位一致"，非空跑）；另锁「无标签(SDR)源 + `--color-tone-map` 必须明确报错
      （`tone-map=hable 需要 HDR 源（当前 sRGB）`）」的可用性诚实行为。
    **变异验证**：把 HLG 素材生成去掉打标参数（即复现原缺陷）⇒ **13/0 → 5/2**，红在
    `HLG 源的 color_transfer=unknown`；改回即 13/0。

43. **查证：`--color-matrix` 是否「静默无效」—— 结论是**符合设计**，不是缺陷（2026-09-16）**

    **起因**：修第 41 条时用 dry-run 做对照，发现 HDR→SDR 场景下
    `--color-matrix bt709` 与 `--color-matrix bt2020nc` 产出**逐字节相同**的命令行
    ⇒ 一度怀疑"用户选的矩阵被静默丢弃"（本项目最忌讳的一类问题）。

    **对照实验**（源：带标签 AVIF `bt2020nc/smpte2084/bt2020`；目标 jpg）：

    | 用例 | 源/目标 | `--color-matrix` | 实际命令行里的矩阵 |
    |---|---|---|---|
    | A/B/C | HDR 源 → jpg（**有** tonemap 链） | `bt709` / `bt2020nc` / 不传 | 三者相同：`-colorspace bt709`（**裁决层**按 SDR 出口决定） |
    | D | SDR 源 → jpg（**无**链） | `bt2020nc` | `-colorspace bt2020nc` ✓ **生效** |
    | E | SDR 源 → jpg（无链） | `ycgco` | `-colorspace ycgco` ✓ **生效** |
    | F | SDR 源 → jpg（无链） | `gbr` | **不写** `-colorspace` ✓（RGB 矩阵族不写给 YUV 编码器，旧口径） |

    **定性**：`--color-matrix` 是**输入声明**；输出矩阵由 `DecideOutputColor` 这一**单一裁决点**决定
    （`BuildArguments` 顶部注释即写明"这一次任务的色彩输出决定只算一次"）。有像素链时链决定实际输出矩阵，
    用户值被取代 —— 这是设计，不是丢参数。

    **唯一窄缺口**（记为开放项，**未擅自修改**）：
    `-i` 前只声明 `-color_primaries` / `-color_trc`，**从不声明 `-colorspace`**
    （`BuildColorArgsSplit` 算出的 matrix 只用于推导输出标签）⇒ 对**未打标**的 YUV 源，
    YUV→RGB 会用 swscale 默认矩阵，用户显式声明的输入矩阵**不参与输入解读**。
    影响面小（HDR 源几乎总带标签），且下传 `-colorspace` 到 `-i` 前属**行为变更**，需拍板。

44. **本会话宿主限制：门禁套件跑法的两个坑（2026-09-16）**

    1. **脚本内 `Start-Process pwsh` 被安全策略拦截** ⇒ `_run-step3-gates.ps1` 的**脚本阶段整批跑不了**
       （探针阶段 `-SkipScripts` 正常）。变通：**逐个** `& 'tests/scripts/xxx.ps1'` 跑。
    2. **宿主批量删除守卫**：删除 >50 文件会打
       `[safe-delete][SAFE_DELETE_BULK_CONFIRM_REQUIRED]`，计数**按会话累积**；
       守卫自身的错误会被抛进脚本上下文（报错文本里的 `Xthrow` 属**宿主实现**，
       项目脚本里并没有这个函数——据此可区分"宿主伪影"与"脚本真失败"）⇒
       `verify-decision-delivery.ps1` / `verify-colorfmt-matrix.ps1` / `verify-color-engine-xcheck.ps1`
       在本会话**跑不完**，**与产品无关**。**建议在新会话开头（尚未累积删除时）先跑这三个。**
       （部分脚本可靠**预清空其工作目录**绕过：`ls -A | head -45 | xargs -r rm -rf`，
       实测 `verify-color-caps.ps1` 因此从"跑不完"转为 **12/0**。）

45. **`Resources/icon.ico` 从未被打包 ⇒ 托盘图标实际不显示（2026-09-16）**

    **发现方式**：给未跟踪文件分类时，发现 `icon.ico` 被 `App.xaml.cs:76` 以
    `avares://FfmpegGui/Resources/icon.ico` 引用，于是核对"它到底进没进程序集"。

    **判据（字节比对法，可复用）**：把资源文件的**头部字节**拿去在被检产物里搜——
    比"看文件在不在输出目录"更可靠（`avares` 只认程序集清单，物理文件在不在都无关）。

    ```
    icon.ico 1378 B / icon_raw.png 911 B
    FfmpegGui.dll 含 icon.ico 头部(32B): False
    FfmpegGui.exe 含 icon.ico 头部(32B): False
    FfmpegGui.dll 内 'icon' 出现 0 次、'.ico' 出现 0 次、'avares' 出现 0 次
    构建输出 bin/Release/net11.0/win-x64/Resources/ 下只有 Locales/（无 icon.ico）
    ```
    ⇒ `AssetLoader.Exists(...)` 必为 false ⇒ `LoadTrayIcon()` 走 `return null`
    ⇒ **托盘图标无图标**（代码注释自认"图标缺失不影响功能"，故长期未被发现）。

    **根因**：csproj 的资源项只有 `<AvaloniaResource Include="**/*.xaml" />` ——
    **不含 `.ico`**；Avalonia 的默认资源 glob 也不覆盖 `Resources/*.ico`（默认覆盖 `Assets/**`）。
    另核实：**无** `ApplicationIcon`、**无** 根/`src` 级 `Directory.Build.props`
    ⇒ 不存在第二条打包通路。

    **修法**（**未实施**）：csproj 增一行 `<AvaloniaResource Include="Resources/icon.ico" />`。
    ⚠ 属 csproj 改动 ⇒ 本环境会触发 restore 失败（见 `BUILD_HANDOVER.md`），
    **必须在可正常 restore 的机器上做**；已在 `HANDOVER.md` 交接清单第 3 条记为开放项。

46. **色彩矩阵可用性全量审查 —— 根因是「角色错位」（2026-09-16）**

    **起因**：要求"确保色彩矩阵有足够的可用性"。做法 = 把矩阵的**用户可见词表**与**全部内部通路**
    逐一实测（token × 目标格式 全扫描 + 原始 ffmpeg 对照归因），而不是只读代码。

    **① 事实：矩阵标注的"承载通路"按格式差异极大**（源：无标签 YUV `y4m`；`-q 95`）：

    | token | jpg | webp | avif | tiff | png |
    |---|---|---|---|---|---|
    | `bt709` | 0 / **bt470bg** | 0 / **bt470bg** | 0 / `bt709` ✅ | 0 / unknown | 0 / gbr |
    | `bt2020nc` | 0 / **bt470bg** | 0 / **bt470bg** | 0 / `bt2020nc` ✅ | 0 / unknown | 0 / gbr |
    | `bt2020c` | **1 (-40)** | **1 (-40)** | **1 (-40)** | 0 / unknown | 0 / gbr |
    | `ycgco` | **1 (-40)** | **1 (-40)** | **1 (-40)** | 0 / unknown | 0 / gbr |
    | `gbr` | 0 / bt470bg | 0 / bt470bg | 0 / unknown | 0 / unknown | 0 / gbr |

    （"0/1"=退出码；`-40` ⇒ **零产物**）
    ⇒ **只有 AVIF 真正承载矩阵标签**（nclx `matrix_coefficients`）。JPEG 无 CICP（exiftool 也读不到
    `ColorSpace`）、WebP 的 RIFF 无 CICP、TIFF 走 ICC（而 ICC 无"矩阵"概念）、PNG/JXL 是 RGB 原生
    ⇒ 这些格式上矩阵**没有承载通路**，而 jpg/webp 的标签恒为编码器默认 `bt470bg`（**宣称≠交付**）。

    **② 归因（原始 ffmpeg 对照，排除应用自身缺陷）**：
    - `-i` 后 `-colorspace bt709|bt2020nc` + mjpeg ⇒ 产物仍 `bt470bg` ⇒ **mjpeg 编码器忽略该选项**（固有）；
    - `-i` 后 `-colorspace bt2020c|ycgco` + mjpeg/libwebp ⇒
      `Impossible to convert between the formats supported by the filter` ⇒ **swscale 不支持这两个矩阵的转换**（固有）；
    - ⇒ 属工具链固有限制，但**应用不该让用户选到它们**。

    **③ 根因（应用侧，三条叠加）**：
    - **角色错位**：`BuildColorArgsSplit` 把用户的矩阵当**输入声明**返回，但 `BuildArguments` 的
      `-i` 前声明块**只发 primaries/trc、从不发 matrix**；该值只被写进 `dec.MatrixForOutput` 当**输出标签**。
      ⇒ 实测无标签 YUV 源下 `--color-matrix bt2020nc` 与 `bt709` 的产物 **md5 相同、PSNR=inf**
      （YUV→RGB 走 swscale 默认矩阵）⇒ 该下拉**唯一的语义（声明源矩阵）100% 未落实**。
      ⇒ 也是"三个分量里 2 个生效、1 个不生效"的不一致。
    - **能力表未施加于用户值**：`GetFormatColorCapabilities` 明确给出 `capM`（jpg/webp = `"bt709"`），
      但 `capM` **只用于像素链的目标矩阵**（`Decision.cs:237/271`），从不施加于 `options.ColorMatrix`
      ⇒ 用户的 `bt2020nc` 被原样写进 jpg 的输出标签（实测 dry-run：`-colorspace bt2020nc`）。
    - **词表含"必败值"**：`bt2020c`/`ycgco` 在**两个角色**里都不可用（作输入声明令 swscale 失败；
      作输出标注被 mjpeg/libwebp/libaom 拒绝），却留在 GUI 词表里 ⇒ 选中即零产物。

    **④ 修法（4 处，`2026-09-16` 实施）**：
    1. `ColorSpaceRegistry` 新增 `FfmpegInputMatrixNames`（**swscale 可转换**的实测白名单：
       `bt709 bt2020nc bt470bg smpte170m smpte240m fcc rgb`）与 `ToFfmpegInputMatrixName`
       （`gbr→rgb`；不在白名单 ⇒ **返回 null**）。⚠ 与 `FfmpegColorSpaceNames`（"ffmpeg 能解析"）**不是同一集合**。
    2. `OutputColorDecision` 新增 `DeclMatrix`；`BuildArguments` 在 `-i` 前发出 `-colorspace <白名单值>`
       ⇒ 与 primaries/trc **同位置同口径**，补上"三个分量一致"。
    3. 输出标注改为 `dec.CapMatrix ?? 决策矩阵` ⇒ 服从容器能力（jpg/webp 恒 `bt709`）。
    4. GUI 词表收敛为 `bt709 / bt2020nc / bt470bg / smpte170m / gbr`：移除两个必败值，
       新增两个**两个角色都可用**的 BT.601 系矩阵（对老标清素材有实际意义）。
       另加**可用性诚实**：CLI 传入不可用矩阵时 `Program.RunHeadless` 打 `⚠` 并列出可用值
       （`ValidateCicpTriple` 同步给 GUI 提示）。

    **⑤ 修复后实测**：
    - 输入声明下发：`-color_primaries bt709 -color_trc iec61966-2-1 -colorspace bt2020nc -i …` ✓
    - **像素随声明变化**：无标签 YUV→png，`bt2020nc` / `bt709` / `smpte170m` 三种 **md5 互不相同** ✓
      （修复前 PSNR=inf）
    - 输出标注服从 capM：jpg 上恒 `-colorspace bt709` ✓
    - 必败值不再失败：`--color-matrix ycgco|bt2020c -f jpg` ⇒ **exit=0 且有产物** + 明确 `⚠` 告知 ✓

    **⑥ 门禁（`verify-color-token-normalize.ps1` 扩到 33 条）**：新增第 E 节 ——
    E1 输入声明在 `-i` 前（4 token）、E2 输出标注服从 capM（4 token）、
    E3 不可用矩阵不失败且出声（2 token）、E4 词表每项都在输入白名单内；
    并把 `bt2020c/ycgco/chroma-derived-*/ictcp` 纳入"已从词表移除"断言。
    **变异验证**：同时停用输入声明与 capM 优先 ⇒ **33/0 → 24/9**（E1 全红、E2 非 bt709 项全红、
    E3"不再零产物"红 ⇒ **精确复现原始缺陷**）；改回即 33/0。

    **⑦ 回归**：探针 **356/0**（与基线逐一相同）；`wiring 26 · format-regression 19 · widegamut 18 ·
    prophoto 26 · tonemap 15 · strategy 16 · hlg-route 13 · colorfmt-matrix(exit 0)` 全部与基线一致，
    且多数产物**字节数完全相同** ⇒ 对既有路径零影响。
    ⚠ `run-pipeline-tests` 本次报 **75 通过 / 5 失败**，5 项全在 cjxl 管道段，失败文本**就是宿主
    `[safe-delete]` 策略消息**（宿主伪影，见第 44 条）；已用独立命令复核：PNG→JXL（显式 `-e Cjxl`
    与默认后端）均 exit=0、产出 6551 B、日志零策略消息 ⇒ **非回归**。

    **⑧ 遗留（开放项，非本轮范围）**：
    - **JPEG/WebP/TIFF/PNG 上的矩阵仍无承载通路** ⇒ 这些格式选矩阵只是"声明源"，产物不会有矩阵标注。
      若要真标注：JPEG 需 exiftool 写 EXIF `ColorSpace`（语义有限）、WebP 需自定义 chunk。
      **未做**（属新增能力，需拍板）。
    - `dec.CapMatrix` 对 **AVIF 为 null**（不约束）⇒ AVIF 仍可被用户标成任意 ffmpeg 合法矩阵；
      实测 `bt2020c`/`ycgco` 在 AVIF 上会失败，但词表已不含它们 ⇒ 当前无入口，风险低。

47. **4 个色彩策略模式的实现审查 —— 深度实现存在，但 GUI 走的是浅层近似（2026-09-16）**

    **审查对象**：`ColorStrategy` 恰好 4 个模式（枚举注释即写"策略1/2/3/4"）。

    **① 用户可见入口**
    | 入口 | 位置 | 形态 |
    |---|---|---|
    | GUI | `MainWindow.xaml:447-463` | 4 个 `RadioButton`（推荐/携带/仅CICP/手动），文案与 tooltip 走 Loc |
    | CLI | `CliParser.cs:537` | `--color-strategy recommended\|carry\|cicp-only\|manual`（另有别名 `auto`/`carry-icc`/`expert`/中文/数字） |
    | CLI 兼容 | `CliParser.cs:533` | 旧参数 `--icc-mode` 经 `ColorStrategyMapper.FromIccMode` 迁移 |

    **② 设计语义（枚举注释 + 契约）**
    | 模式 | 语义 | 结局 |
    |---|---|---|
    | `Recommended` | 按**无损阶梯**自动决策（能携带就携带 → 否则归入含源的**最窄可命名超集**） | L1 携带 → L2 超集归一 → 映射；低置信度只保标注不改像素；HDR 守卫 |
    | `CarryIcc` | **色彩变换零改动**，携带源 ICC | ① 附源 ICC ② 源无 ICC 但有 CICP ⇒ 继承标签 ③ 隐含 sRGB+容器未标注语义明确 ⇒ 无标注直通 ④ 其余 ⇒ **Reject** |
    | `BakeCicpOnly` | **禁 iccgen**，仅 CICP | 容器无 CICP ⇒ 仅"未标注即 sRGB"可直通，否则 **Reject** |
    | `Manual` | 完全按用户指定（空间 + 三元组） | 无目标 ⇒ **Reject**；容器既不能写该 CICP 又不能携带 ICC ⇒ **Reject** |

    **③ 关键发现：4 模式被实现了两遍，深度不同**
    | 层 | 位置 | 实现深度 |
    |---|---|---|
    | **引擎层**（唯一真值） | `ColorMapping/ColorStrategyPlanning.cs`（~800 行） | `Plan()` 分派 → `PlanRecommended`/`PlanCarry`/`PlanCicpOnly`/`PlanManual`；另有 `ResolveStrategy`（S2 覆盖：GainMap / HDR 无 ICC 通路 / 容器不能携带 ICC）、`OverrideReason`、`Reject(reason, advice)`、`EnsureAnnotationNotSilentlyMissing`、`EnforceContainerAnnotationRules`、`ColorLoss/CodecLoss/Annot/OutBitDepth` 分类 |
    | **legacy 层**（GUI 实际走） | `FfmpegCommandBuilder.Decision.cs:145-158` | **只有两个布尔**：`preserveSource = (strategy == CarryIcc)`、`stripIcc = (strategy == BakeCicpOnly)`；`preserveSource` 用于 5 处守卫（跳过 tonemap / 目标色域转换 / D1 归一 / ICC 后置），`stripIcc` 用于 4 处（不附 ICC）。**无 Reject、无超集归一、无 OverrideReason、无 CICP/ICC 择一** |

    **④ 实测证据（决定性；源 `src_8bit.png`，输出 png，`-q 100`）**
    | 用例 | legacy（GUI 实际路径） | 引擎（`--color-engine engine`） |
    |---|---|---|
    | `recommended`，无目标 | 592110 B / md5 `e3aa3a77…` | 592110 B / md5 **`e3aa3a77…`（完全一致）** |
    | **`manual`，无目标** | **592110 B / md5 与上行完全相同** ← 等价于推荐 | **exit=1、无产物**（Reject：`S4 手动：未提供任何目标色彩描述`） |
    | `carry`，源**有** CICP 标签 | 592811 B | 592811 B（一致） |
    | **`carry`，源**完全无标签**** | **exit=0、592738 B（静默产出）** | **exit=1、无产物**（Reject：`S2 携带：源无 ICC 可携带，且不满足"隐含 sRGB + 容器未标注语义明确"` + 建议） |

    ⇒ **legacy 把 4 个模式压成 2 个自由度**：`Recommended ≡ Manual`（逐字节相同）；且**没有拒绝语义**。
    ⇒ 两条路径在"能判定的情形"下一致（592811 / 592110 逐字节相同）⇒ 差异**只**出现在拒绝类用例上。

    **⑤ 可达性（最严重）**：`FfmpegOptions.ColorEngine` 默认 **`"legacy"`**，而
    - GUI **无任何开关**（`MainWindow.xaml`/`.xaml.cs` 全量搜 `color.?engine` ⇒ **0 命中**）；
    - **预设不带**该字段（`PresetData`/`PresetManagerService` 0 命中）；
    - `--help` **未列出** `--color-engine`（只有 `CliParser.cs:465` 的解析分支）；
    - 全仓唯一设置点就是那一行 CLI 解析。
    ⇒ **正常使用（GUI）下，4 模式只有 legacy 的浅层近似**；而 UI tooltip 描述的却是**引擎语义**
    （如"不做转换，完整保留源文件的色彩信息与配置文件"）。这是"宣称 ≠ 交付"。
    ⚠ 代码自注：`ColorEngineRouter.Requested` 的注释写明「**默认 legacy；默认值切换需先有 A/B 数据**」
    ⇒ 切默认值**不是随手能改的**，需先产出 A/B 数据。

    **⑥ 覆盖空洞**
    - `verify-color-strategy.ps1`（16 条）**不带** `--color-engine` ⇒ 测的是 **legacy**；
      它的用例无法发现"Recommended ≡ Manual"或"缺 Reject"（实测 4 模式全绿）。
    - `ServiceProbe contract`（95 条）直接调 `ColorMappingEngine.Plan` **纯函数** ⇒ 测的是**引擎**。
    - ⇒ **没有任何门禁断言 legacy 路径上 4 模式的行为**，即"策略 × 执行路径"矩阵有覆盖空洞。

    **⑦ 附带发现：legacy 的 HDR 守卫不生效**
    `PlanRecommended` 的 `HDR 源 → SDR 目标需显式请求色调映射（否则高光整体压暗）` Reject **只在引擎路径**。
    legacy 由 `FfmpegOptions.TonemapCurve` 默认 `"hable"` 驱动 ⇒ 实测 `PQ 源 → jpg` **不加任何参数**
    即产出 bt709/SDR（`-vf … tonemap=hable:param=0.5 …`），日志无任何"已自动 tonemap"提示。

    **⑧ 修复选项（均未实施，需拍板）**
    | 选项 | 内容 | 风险/前提 |
    |---|---|---|
    | A | **把引擎设为默认**（`ColorEngine` 默认 `"engine"`） | 代码自注要求**先有 A/B 数据**；且引擎编码出口尚未承接质量/子采样/DPI 等参数（`UnconsumedOutputSettings` 会拒绝）⇒ 直接切会大面积拒绝 |
    | B | **GUI 加引擎开关**（不改默认） | 让用户能 opt-in；但会暴露 A 中的"未承接参数即拒绝"摩擦 |
    | C | **补齐 legacy 的拒绝语义** | 让 GUI 路径也 Reject 而不是静默产出；改动 `Decision.cs`，影响面大，需逐条对齐引擎契约 |
    | D | **补门禁：断言两条路径在"可判定情形"下逐字节一致** | 低成本、能锁住"将来把差异扩大"；但会锁住当前"拒绝类不一致"的现状（需先决定现状是否可接受） |

48. **清理传统管线 · 增量 1：引擎编码出口承接参数 + A/B 基线数据（2026-09-16）**

    **背景**：第 47 条查明 4 模式的深度实现只在引擎路径、而引擎不可达。用户指令：**清理传统管线，
    以自研管线为准完整实现**。本增量做**硬前置**——先让引擎吃得下用户参数，否则切默认会大面积拒绝。

    **① 硬前置的成因**：`ColorEngineRouter.UnconsumedOutputSettings` 原先把
    「质量 / 无损 / 色度子采样 / PNG pred+DPI / WebP preset+压缩级别 / TIFF 压缩算法+DPI /
    JPEG huffman+dct」整批列为"引擎未承接"并**拒绝任务**；而 `RawColorPipeline.BuildEncodeArgs`
    是**硬编码**的（png 恒 `-compression_level 100`、webp 恒 `-quality 90`、jpg 恒 `-q:v 2`、
    tiff 恒 `-compression deflate`）⇒ 引擎在 GUI 默认设置下几乎不可用。

    **② 做法**：新增 **`Services/ColorMapping/ImageEncoderArgs.cs`** 作为编码器参数的**单一真值**，
    语义与 `FfmpegCommandBuilder` 对应分支**逐条对齐**（切换管线时产物必须可 A/B 对拍）：
    - `jpg/jpeg`：`-q:v MapJpegQuality(q)`（Cjpegli 后端走 `-distance`）、`-huffman`、`-dct`（非 auto）
    - `png/apng`：`-compression_level`（Lossless→0，否则 `MapPngCompression(q)`）、`-pred`、`-dpi`
    - `webp`：Lossless→`-lossless 1 -q:v 100 -compression_level clamp(0..6)`（preset 仅 default/none）；
      否则 `-q:v q`（preset 非 none 时带上）
    - `tiff/tif`：`-compression_algo`（Lossless→raw，否则用户值）、`-dpi`
    - 色度：`MapEnginePixFmt`（仅 jpg/jpeg，按 chroma+range 出 `yuvj/yuv` + 420/422/444；
      其余格式**故意**返回 null，避免 libwebp 写 4:4:4 失败）
    - 三个质量映射（`MapJpegQuality`/`MapPngCompression`/`MapJpegliDistance`）**从 `FfmpegCommandBuilder`
      迁入本类**，原处改为转发 ⇒ 数值单点，杜绝两处漂移。
    - `TransformFileAsync` 新增 `options` 参数（唯一调用点 `QueueProcessor:732` 传 `item.Options`）；
      `options == null`（探针等内部调用）时**保持历史固定值**，行为不变。

    **③ 实测（引擎 + 此前全部被拒的参数组合）**：
    | 用例 | 结果 |
    |---|---|
    | `-q 60 -f png` | ✅ 33453 B（此前拒绝） |
    | `--lossless -f webp` | ✅ 21116 B |
    | `-q 95 --chroma 4:4:4 -f jpg` | ✅ 26510 B |
    | `-q 100 --png-dpi 300 --png-pred paeth` | ✅ 592505 B |
    | `--tiff-compression deflate` | ✅ 34822 B |
    | `-q 95 --jpeg-huffman optimal` | ✅ 26510 B |
    | **负控** `--chroma 4:4:4 -f webp` | ✅ 仍拒绝（libwebp 无 4:4:4） |
    | **负控** `--max-dimension-enabled true` | ✅ 仍拒绝并点名「最大边长缩放（引擎不做几何变换）」 |

    **④ 契约更新（改门禁必须补负控）**：`ServiceProbe wire` 原有 2 条断言锁的是**旧契约**
    （"质量/色度未承接 ⇒ 必须拒绝"）。已改写为**新契约 + 有牙断言**：
    ① 质量/色度**不再拒绝**；② 且必须**真的落到编码器参数**（`-compression_level MapPngCompression(60)`、
    `-pix_fmt yuvj420p`）；③ **负控**：libwebp 的 4:4:4 仍必须拒绝。
    ⇒ `wire` **26 → 29 条**，全绿。

    **⑤ 回归**：探针合计 **359 pass / 0 fail**（其余 16 个 mode 数字逐一不变）；
    `verify-color-wiring` **26/0**、`verify-color-strategy` **16/0**，且产物字节数与改前**完全一致**。

    **⑥ A/B 基线数据（切换默认的依据，代码自注要求）** —— 同源（P3 ICC PNG）→ `--color-space sRGB`：

    | 格式 | legacy | 引擎 | **差异根因** |
    |---|---|---|---|
    | png | `rgb48be` 1184448 B | `rgb24` 592505 B | **位深**：legacy 保 16-bit，引擎恒 8-bit |
    | tiff | `rgb48le` 1186980 B | `rgb24` 595646 B | 同上 |
    | jpg | `yuvj420p` 17837 B | `yuvj444p` 26510 B | **色度默认**：引擎喂 RGB 使 mjpeg 自选 444 |
    | webp | `yuv420p` 14200 B | `yuv420p` 13146 B | 编码参数差异（PSNR 30.8dB） |

    **⑦ 结论：切换默认前必须先修掉这 2 个语义差异（下一步）**
    - **位深**：`ColorStrategyPlanning` 的 `OutBitDepth` 规则用 `pol.TargetBitDepth`，
      而它 = `o.BitDepth ?? 8` ⇒ **用户未指定位深时恒 8-bit**，PNG/TIFF（容器上限 16）被**静默降位**。
      legacy 是「探测源位深并传递（按容器上限钳）」。修法：未指定时跟随源位深（按 `CapMaxBitDepth` 钳）。
    - **色度**：引擎把 `rgb24/rgb48le` 喂给 mjpeg，ffmpeg 选最高质量 444；legacy 从解码帧出发选 420。
      修法：`chroma=auto` 时显式给出与 legacy 一致的默认（jpg→`yuvj420p`）。
    ⚠ 这两项属**行为变更**，且会直接改变用户产物 ⇒ 修完需重跑 A/B 确认逐字节一致，再谈切默认。

49. **清理传统管线 · 增量 2：A/B 差异修复 —— 8-bit 源已达逐字节等价（2026-09-16）**

    承接第 48 条的 A/B 数据，逐项定位并修掉差异。**结论：对 8-bit 源（绝大多数实际用例），
    两条管线现已完全等价（png/tiff 逐字节相同、jpg PSNR=inf）。**

    **① 位深（静默降位）**
    - 根因：`ColorIntentFactory` 的 `TargetBitDepth = o.BitDepth ?? 8` ⇒ **用户未指定时恒 8-bit**，
      而 Plan 的 `OutBitDepth` 规则读它 ⇒ 16-bit 源在 PNG/TIFF（容器上限 16）上被**静默降位**。
    - 证据：同源 16-bit → PNG，legacy `rgb48be` 1184448B vs 引擎 `rgb24` 592505B（PSNR 51.5dB ≈ 8-bit 量化噪声）。
    - 修法：对齐 legacy 的 `EffectiveBitDepth()` ——
      `Math.Min(o.BitDepth ?? (meta.bitDepth > 8 ? meta.bitDepth : 8), caps.MaxBitDepth)`。

    **② 色度默认（mjpeg 自选 4:4:4）**
    - 根因：`MapEnginePixFmt` 原先在 `chroma=auto` 时返回 null（不写 `-pix_fmt`）⇒ 引擎喂的是**RGB 原始帧**，
      mjpeg 自选**最高质量**的 `yuvj444p`；而 legacy 从已解码的 YUV 帧出发、其 `MapPixFmt` 在 auto 下落到 `yuvj420p`。
    - 证据：legacy `yuvj420p` 17837B vs 引擎 `yuvj444p` 26510B（PSNR 22.4dB）。
    - 修法：`auto` 也**显式给出** `-pix_fmt yuvj420p`（420 亦是 JPEG 业界惯例）。

    **③ 转换矩阵（最关键的一处）**
    - 根因：Plan 对**非 CICP 容器**（jpg/webp）会把 `OutMatrix` **清空**（标注改由 ICC 承担），
      但**矩阵在这里同时是 RGB→YUV 的转换参数**，不能跟着标注一起清掉 ⇒ 引擎的编码命令里
      完全没有 `-colorspace`，swscale 遂按图像尺寸取默认（SD ⇒ **bt601**），而 legacy 用 **bt709**。
    - 证据：逐字对比命令 —— legacy `… -q:v 3 -pix_fmt yuvj420p -colorspace bt709`；
      引擎 `… -pix_fmt yuvj420p -q:v 3`（无 `-colorspace`）。这是 22.4dB 的**主因**（补上后 → 37dB）。
      （排查过程本身暴露了可追溯性缺口：引擎**不记录 ffmpeg 命令行**，见 ⑤。）
    - 修法：`BuildEncodeArgs` 对 **YUV 原生输出**（jpg/webp）在 `plan.OutMatrix` 为空时补
      `-colorspace bt709` —— 取值与 legacy 的 `capM`（jpg/webp = bt709）同源。

    **④ 出口位深规则补 `RgbNative` 条件**：YUV 容器（jpg/webp）的 8-bit 只是**编码结果**，
    中间件降 8-bit 会在 RGB→YUV 之前白丢精度（legacy 保 16-bit 到编码器）。
    实测对 A/B 无影响（30.28 → 30.31dB），但更正确，保留。

    **⑤ 顺带修可追溯性缺口**：`RawColorPipeline.RunToEndAsync` 现在记录完整 ffmpeg 命令行。
    此前引擎不记录命令 ⇒ 第 ③ 项那种"两条管线到底差在哪个参数"的问题**无法对拍**，
    只能靠重建命令猜（本次排查确实先猜错了两轮）。

    **⑥ A/B 复测（同源 → `--color-space sRGB`）**

    | 源 | 格式 | legacy | engine | 结果 |
    |---|---|---|---|---|
    | **8-bit** | png | 27369 B | 27369 B | **逐字节相同** ✅ |
    | **8-bit** | jpg | 24350 B | 24350 B | **PSNR=inf（逐位一致）** ✅ |
    | **8-bit** | tiff | 594838 B | 594838 B | **逐字节相同** ✅ |
    | 16-bit（P3 ICC） | png | 1184448 B | 1184142 B | PSNR 85.8dB（16-bit 舍入级） |
    | 16-bit | tiff | 1186980 B | 1186954 B | PSNR 85.8dB |
    | 16-bit | jpg | 17837 B | 18331 B | PSNR 36.96dB（有损放大） |
    | 16-bit | webp | 14200 B | 12970 B | PSNR 30.76dB（有损放大） |

    **⑦ 对 16-bit 源残余差异的定性（实测，不夸大）**：两条管线的 **RGB 像素输入**在 8-bit 意义上
    中位差 **0.012**、p99 **0.05**、max **1.00** ⇒ 实质等价。残余产物差异来自
    ① RGB 舍入差（zscale vs 引擎内核）② RGB→YUV 的**入口不同**（legacy 从 zscale 的 `rgb48le` 直连，
    引擎从 `rawvideo` 中间件），二者都是**舍入级**，被 JPEG/WebP 的有损量化放大。
    旁证：legacy 的 jpg 也无法由其自身 png 复现（36.98dB），而引擎的 jpg 可由其自身 png 复现（**inf**）。

    **⑧ 回归**：探针 **359 pass / 0 fail**；`verify-color-wiring` **26/0**（含「不选引擎 ⇒ 行为逐字节不变」）、
    `verify-color-strategy` **16/0**、`verify-format-regression` **19/0** —— 产物字节数与基线一致。

    ⇒ **切默认的前置条件已满足**（8-bit 源逐字节等价）。下一步：任务 #13（决策层统一到
    `ColorStrategyPlanning`）+ #14（切默认 + 删 legacy 执行体 + 更新门禁）。

50. **切默认到自研引擎 + 暴露出的 4 个集成问题 + 一个**门禁完整性缺陷**（2026-09-16）**

    **① 切默认（三值语义）**：`FfmpegOptions.ColorEngine` 默认 `"legacy"` → **`"auto"`**。
    - `auto`（默认）：优先引擎；该次任务**不在引擎能力内**时**优雅回退传统管线**并记一条说明。
    - `engine`（显式）：不可走时**硬失败并点名**（可用性诚实，绝不静默回退）。
    - `legacy`（显式）：逃生门，A/B 与排查用。
    - 新增 `ColorEngineRouter.ExplicitlyRequested`（`=="engine"`）与 `Requested`（`!="legacy"`）区分二者。
    ⚠ **不分这两种情况会让默认切引擎后所有非引擎格式硬失败**（实测 avif/gif 全红、退出码 90）。

    **② 集成问题 1：GUI 的 tonemap 入口缺失** ⇒ HDR→JPEG 无路可走（format-regression 16 项全红）。
    GUI 按设计不再暴露 tonemap 曲线（各处 `TonemapCurve = null`，"内部固定 hable"），既有行为由
    **传统链的内部默认**承担；引擎则要求"显式请求"。修法：`ColorIntentFactory` 在
    **HDR 源 + 目标容器不支持 HDR** 时**自动**采用内部默认曲线 `hable`，并置 `ToneMapAuto` 标记
    ⇒ 决策日志写明「算子=自动（容器不支持 HDR ⇒ 内部默认曲线；用 --color-tone-map 可指定）」，
    用户显式指定时仍以其为准（**不是静默降级**）。

    **③ 集成问题 2：PNG 的 CICP 能力被低估** ⇒ S2/S3 对 HDR→PNG **误拒**。
    `FormatColorCaps["png"].CanCicp` 原为默认 `false`，但应用**确实**由 `PngCicpService` 后置写入
    cICP（实测产物 `09 12 00 01`）。已改为 `true`（png/apng）。**契约探针 `contract` 95/0 未受影响**。

    **④ 集成问题 3（已闭环）**：`p3pq-png-pq`（P3-PQ 源 → `--color-space "BT.2020 PQ"` → png）曾 Reject，
    报「HDR 源 → SDR 目标需显式请求色调映射」。
    **根因**：`ColorIntent.HdrOutput` **从未被装配**（只有字段声明与 `ToPolicy` 传递），
    而 `ColorStrategyPlanning` 只在 `it.Target == null` 的 auto 分支里补 `HdrOutput` ⇒
    **显式 HDR 目标走不到**，于是 HDR 守卫 `srcHdr && !pol.HdrOutput && !ToneMapRequested ⇒ Reject`
    对"HDR 源 + HDR 目标"也成立（自相矛盾：目标明明是 HDR）。
    **修法**：`ColorIntentFactory` 在 `!dstSdr` 时置 `it.HdrOutput = true`。
    **验证**：引擎 exit=0，产物 cICP = `smpte2084/bt2020`（与 legacy 一致）；`widegamut` **18/0**。

    **⑤ ⚠⚠ 门禁完整性缺陷（本轮最重要发现，已修）**：**12 个门禁脚本把 `bin/Debug` 排在 `bin/Release` 之前**
    ⇒ 它们跑的是 **09-15 17:09 的 Debug 旧二进制**（比当天全部改动早 14 小时）。
    受影响：`verify-color-wiring` / `verify-color-strategy` / `verify-format-regression` /
    `verify-widegamut-regression` / `verify-prophoto-faithful` / `verify-gainmap-managed` /
    `verify-tiff-icc` / `verify-webp-hdr-fix` / `verify-decision-delivery` + 3 个探针。
    - **后果**：此前记录的这些门禁数字（含"19/0 基线"）**都不对应当前代码**；`verify-color-wiring` 的
      第 17-19 行甚至写了两次 Debug（第三行才是 Release）。
    - **修法**：全部改为 **Release 优先 + Debug 兜底**（与 `verify-metadata-privacy.ps1` 的既有正确写法一致）。
    - ⚠ 运行器的"二进制新鲜度硬闸"只比较**探针 bin 目录**里的 `FfmpegGui.dll`，**看不到**子脚本用的 Debug exe ⇒ 没拦住。

    **⑥ 门禁契约更新（改门禁必补负控）**：
    - `verify-color-wiring`：第 (1) 节原为「不传参 ⇒ legacy」——默认改了之后会变成**引擎 vs 引擎的空断言**
      ⇒ 改为「显式 legacy vs 显式 engine 逐字节相同」+ 新增「**默认 ⇒ 走引擎**（与显式 engine 逐字节相同）」；
      第 (4) 节原断言「质量未承接 ⇒ 拒绝」（旧契约）⇒ 改为「**已承接 ⇒ 不再拒绝**」+
      「**质量确实生效**（q60 24305B ≠ q100 592110B）」。⇒ **28/0**。
    - `verify-color-strategy`：新增 `$expectReject` 断言（**不得产出文件 + 必须给出拒绝原因**），
      用于 3 格按契约必须拒绝的用例（`carry` 在无标签源上无可携带；`manual` 未给目标）。
      ⇒ **16/0**（拒绝格现在会把原因打出来，可审计）。
    - `ServiceProbe wire`：新增「**默认（不传 --color-engine）⇒ 路由到引擎**」断言（默认值即契约）。
      ⇒ **30/0**。

    **⑦ 本轮门禁实测**：探针 **contract 95 / wire 30 / selftest 36 / iccname 10 / plan 39 / curve 39 /
    matrix 15 / ootf 11 / hlgwire 3 / bt2446 18 / decision 4 / engine 32 / runner 2 / settings 7 /
    procstreams 3 / encoding 6 / ipc 10 ⇒ 全部 fail=0**；
    `verify-color-wiring` **28/0** · `verify-color-strategy` **16/0** · `verify-format-regression` **19/0** ·
    `verify-widegamut-regression` **17/1**（唯一红格见 ④）。

    **⑧ 待办**：④ 的目标解析 + 复跑其余 5 个受影响门禁
    （`prophoto-faithful` / `gainmap-managed` / `tiff-icc` / `webp-hdr-fix` / `decision-delivery`）。

51. **超广色域保真路径接入色彩引擎（原 🔴【待实现】→ ✅ 已实现，2026-09-16）**

    **定性**：超广色域（ProPhoto / ROMM 等超出 BT.2020 的色域）的**保真路径**
    （语义 = **像素零改动 + 附着源 ICC**）**只在传统管线实现**：
    `FfmpegCommandBuilder.DecideOutputColor` 的 `Faithful` 分支 + `QueueProcessor` 的后置附着。
    **规划层没有 `Faithful` 概念**（`grep Faithful ColorStrategyPlanning.cs` 为空；
    `ColorTransformPlan.cs` 只有一句注释提到"ProPhoto 等超广保真路径即此"）。
    ⇒ 它属**待实现内容**，不是"已可用但表现不佳"。

    **为什么必须显式拦截**（默认切引擎后暴露）：引擎会把它当普通任务处理 ⇒
    ① 像素被**归一化**（原本要求零改动）② **源 ICC 丢失** ③ 还会写上误导性的 CICP `bt709,bt709`。
    实测 `verify-prophoto-faithful` 由 **26/0** 退化为 **13/13**（像素改动 / 缺 ProPhoto ICC /
    日志无保真附着 / 输出带 CICP）。

    **修法（本轮）**：`ColorEngineRouter.BlockReason` 新增可选参数 `inputPath`；
    给出时调用 `FfmpegCommandBuilder.DecideOutputColor(o, inputPath).Faithful` 判定，
    命中即返回带 **`🔴【待实现】`** 前缀的原因 ⇒ 走既有的"不可走 ⇒ 优雅回退传统管线"机制
    （`auto` 默认记说明；显式 `--color-engine engine` 下**硬失败并点名**，让它成为**可见的待实现项**，
    而不是"看起来能用"的静默降级）。`QueueProcessor` 已传入 `inputPath`。

    **验证**：`verify-prophoto-faithful` 恢复 **26/0**（像素逐位一致 + ProPhoto ICC 完整 + 保真日志）。

    **归类与可见性（用户要求）**：`docs/HANDOVER.md` **顶部**新增
    **「🔴【待实现】功能清单（未实现 —— 请勿当作已可用）」** 表（超广保真列为第 1 项），
    并配「✅ 已实现」对照表；`MANUAL_VERIFICATION.md` 同步标注 ⇒ 已实现与待实现一眼可分。

    **本项状态**：✅ **已闭环（2026-09-16 当日接入引擎）** —— 见下方 ⑨。

    **⑨ ✅ 接入引擎（同日完成）**：既然规划层缺的就是 `Faithful` 语义，直接补上，而不是长期依赖路由拦截。
    - **判定（同源）**：把 `ResolveFaithfulUltraWideIcc` 抽出**不依赖 `FfmpegOptions` 的重载**
      （`(ColorStrategy, colorSpace, srcGamutName, srcTrc, format)`），`ColorIntentFactory` 用它判定并写入
      `ColorIntent.FaithfulIccPath` ⇒ **两条管线同一个函数**，口径一致、可 A/B 对拍。
    - **规划**：`PlanPolicy.FaithfulIccPath`（非空 ⇒ 走保真）；`Plan()` 里**优先于一切策略**分派到
      `PlanFaithful`（与传统管线 `DecideOutputColor` 的顺序一致：先判保真）。
      `PlanFaithful` 给出 `Action=CarryIcc` + `AttachIcc=true` + `IccPath` + `ColorLoss=None`，
      且 **`Out* = null`（不写 CICP）** —— 写 bt709 会与 ProPhoto ICC 冲突、写 bt2020 是削色后的假标签。
    - **执行**：`TryRunColorEngineAsync` 把 `CarryIcc` 与 `None` **同等处理**（引擎不参与像素变换），
      ICC 由**既有**的后置附着机制嵌入（传统发射器会在同一份 options 上做同源判定并置 `FaithfulIccPath`）。
    - **移除路由拦截**：原先那段 `🔴【待实现】` 的 `BlockReason` 分支已删除（连同其 `inputPath` 形参，
      以免留下误导性 API）。
    - **验证**：`verify-prophoto-faithful` **26/0**；实测日志
      `决策：CarryIcc … L0 超广色域保真：像素零改动 + 仅靠源 ICC 描述（named_prophoto-rgb.v2.icc）`
      → `[保真] 超广色域像素原样保真` → `[保真] 已附着超广色域命名 ICC … 像素未转换`。
    - **文档归类同步**：`HANDOVER.md` 顶部清单里该项已从「🔴【待实现】」移入「✅ 已实现」；
      `MANUAL_VERIFICATION.md` 同步。**其余待实现项（GainMap / 几何 / 外部 ICC / 非引擎格式 / 决策层统一 / 删 legacy）保持不变。**

    **⑩ 顺带修正：PNG 的 `CanHaveBothIccAndCicp` 应为 true**（同日）
    - 起因：为修 S2/S3 的 HDR→PNG 误拒，把 `png.CanCicp` 置 true；但这让规划层从"ICC 优先"变成
      "**择一**" ⇒ 对 sRGB 目标选 CICP 而**丢掉 ICC** ⇒ `verify-color-wiring` 的 (2)(6) 两格转红
      （它们断言"目标 ICC 已后置补齐"）。**这是真实的观感降级**（老查看器只认 ICC）。
    - 定性：PNG 的 `iCCP`（exiftool/iccgen 后置）与 `cICP`（`PngCicpService` 后置）是**不同的 chunk，
      可以共存** ⇒ `CanHaveBothIccAndCicp` 本就该是 true（该类默认值）。
    - 修法：png/apng 置 `CanHaveBothIccAndCicp = true`（同时保留 `CanCicp = true`）。
      验证：`wiring` **28/0**、`strategy` **16/0**、`format-regression` **19/0**、`widegamut` **18/0**、
      `prophoto-faithful` **26/0**、`gainmap-managed` **20/0**、探针 **360/0**。

---

52. **并行推进（2 子 agent）：几何缩放 + 外部 ICC 接入引擎；传统管线策略来源统一（2026-09-16）**

    **背景**：`HANDOVER.md` 顶部「🔴【待实现】功能清单」共 7 项。本轮用**两个子 agent 并行**消掉其中
    2 项 + 完成第 6 项的一半。两包按**文件归属**隔离（同文件并发编辑是本项目的既定禁忌）。

    **① 几何缩放（最长边）接入引擎** —— `ColorEngineRouter` 删掉 `EnableMaxDimension` 拦截；
    `RawColorPipeline` 在**解码步**复用**同一个** `FfmpegCommandBuilder.BuildMaxDimensionScaleFilter`
    （不写第二份几何口径）。**关键坑**：编码步用 `-video_size {w}x{h}` 喂原始帧，若仍用原始宽高就会读错数据
    ⇒ 缩放后尺寸**由解码产物长度反推**（rawvideo 无行填充 ⇒ L = w·h·6 精确）。自算必漂：512×384 限 32 的
    纵向 `scale=-2:32` 实得 **42×32**，按比例算会取 43/44。
    **验证**：`wiring` 第 (7) 节 —— 512×384 限 32 ⇒ ffprobe 读回 **32,24**；负控：未启用 ⇒ 512,384、
    限值 4096 > 最长边 ⇒ 512,384。
    ⚠ **诚实边界**：几何缩放的**主执行体其实是队列的统一预处理层**（`QueueProcessor:382-447`，"所有后端共用"）
    ⇒ 引擎通常拿到已合规输入。真正被修的缺陷是那条拦截：它让「开限值 + 显式 `engine`」**直接硬失败（退出码 90）**，
    属纯误伤；引擎侧加的缩放是**兜底层**。另与 legacy 有**顺序差异**（legacy「变换→缩放」vs 引擎兜底「缩放→变换」，
    只差"降采样 × 非线性曲线"的交换律二阶量）。

    **② 外部 ICC（`--icc-file`）接入引擎** —— `ColorIntentFactory` 的源描述符优先级改为
    **外部 ICC → 源内嵌 ICC → CICP 标签 → 惯例假定**（`IsValidIccProfile` 校验；解析不出回落，不冒认）。
    **刻意不把外部 ICC 当 `SourceIccPath` 往下传**：那会触发 CarryIcc 类计划，而队列侧只对**源文件内嵌**的 ICC
    做附着 ⇒ 会留下「计划说携带了 ICC、产物里其实没有」的静默失配。只取其**度量结果**。
    **验证**：`wiring` 第 (8) 节 —— 无 ICC ⇒ `Action=None` 直通；有效 ICC + `--color-space sRGB` ⇒ `Map` +
    日志 `源=Display P3 (ICC 识别…) [--icc-file]`，**像素 PSNR 35.36dB**（真变像素，非只改标签）；无效 ICC ⇒ 回落。
    ⚠ **附带发现**：传统管线侧 `--icc-file` 的两个消费者（`ResolveIccSourceParams` / `GetIccSourceDisplayName`）
    **全仓无调用点** ⇒ 该选项此前在两条管线上**实际都是死的**；本次接入的是引擎侧。

    **③ 传统管线的策略来源统一** —— 新增 `FfmpegCommandBuilder.EffectiveColorStrategy(options, inputPath)`：
    走 `ColorIntentFactory.FromOptions` → `Plan(intent).EffectiveStrategy`（与引擎**同一份裁决**）；
    带 `[ThreadStatic]` 重入保护（切断 `FromOptions → EffectiveOutputColorTokens → DecideOutputColor` 回调递归）、
    10s TTL 缓存、装配失败回退声明值，以及**成本闸**（`declared != CarryIcc ⇒ 直接返回声明值` ⇒ 非 S2 零开销）。
    `DecideOutputColor` 的 `preserveSource`/`stripIcc` 由它推导；`QueueProcessor` 里按策略决定标注的 5 处
    （PNG cICP 判据 / `DecideLabel` 入参 / S3 残留 ICC 告警 / TIFF 目标 ICC / 元数据恢复的源 ICC 回带）
    一并改用生效值。
    ⚠ **顺手补了一个死代码**：`ColorIntentFactory.FromOptions` **从不写 `GainMapRequested`** ⇒
    `ResolveStrategy` 的 S2 覆盖①（GainMap 必须映射底图）此前**永不触发**。已补
    `GainMapRequested = o.JpegGainMap`（两条管线与契约探针现在拿到同一份意图）。
    **验证**：`verify-color-strategy` **40/0**（新增传统管线对照矩阵 + **13 格 parity**：两管线共同产出格
    CICP/ICC 全一致）+ 6 条传统管线语义断言 + 1 条 `L-S2≠S1(策略生效)`。
    ⚠ **诚实边界**：该推导在**现有可达输入**下是**结构性**的而非行为性的 —— 能改变 `EffectiveStrategy` 的
    只有 `declared==CarryIcc && GainMapRequested`，而 GainMap 走独立分支 ⇒ 改动前后传统管线产物逐格相同
    （这正是四个门禁数字不变的原因）。价值在"规划层以后加/改覆盖，传统管线自动跟随"。
    **性能**：非 S2 策略 0；S2 首次 **+250ms/图**（一次 `exiftool -b -icc_profile`），同图后续命中缓存 ⇒ 0。

    **④ 集成阶段我修掉的 3 处**（都是并行改动**互相暴露**出来的）：
    - **`png.CanHdr` 未设（false）** ⇒ `ColorIntentFactory` 的"HDR 源 + 容器不支持 HDR ⇒ 自动 tonemap"
      **误触发** ⇒ 引擎把 HDR→PNG **降成 SDR**，而传统管线**保 HDR**（实测 `recommended-hdr-png`：
      引擎 53926B/ICC=True vs legacy 43821B/ICC=False）。PNG 经 **cICP** 就能承载 PQ/HLG
      （两条管线产物都写着 `smpte2084,bt2020`）⇒ 置 `CanHdr = true` 后两格**逐字节相同**，
      该格已从"已登记差异"**并入 13 格严格 parity**（`$knownDivergence` 现已清空，留空表是有意的）。
    - **自动 tonemap 的判据漏了一支** ⇒ 上一条修完立刻回归：HDR 源 + **显式 sRGB 目标** + png
      会落到规划层的"HDR 源 → SDR 目标需显式请求 tonemap" ⇒ **拒绝**（`hdr8-png-tgt` 转红）。
      判据必须两支：`targetExplicit || !caps.CanHdr`
      （①用户显式要 SDR ⇒ 必须映射；②auto 且容器承载不了 HDR ⇒ 只能降 SDR；反之 auto + 容器可承载 ⇒ 保 HDR）。
    - **两处锁旧契约的探针断言**：`wire`「引擎不做几何变换 ⇒ 拒绝」与 `contract`「HLG × png ⇒ Reject」
      ⇒ 分别改为「已承接 ⇒ 不再拒绝」与「png/apng **有** cICP 通路 ⇒ 不得 Reject + `CanHdr` 须为 true」。

    **⑤ 本轮门禁（Release 产物）**：探针 **363 pass / 0 fail**（contract **98** · wire 30 · selftest 36 ·
    iccname 10 · plan 39 · curve 39 · matrix 15 · ootf 11 · hlgwire 3 · bt2446 18 · decision 4 · engine 32 ·
    runner 2 · settings 7 · procstreams 3 · encoding 6 · ipc 10）；
    `verify-color-strategy` **40/0** · `verify-color-wiring` **39/0** · `verify-format-regression` **19/0** ·
    `verify-widegamut-regression` **18/0** · `verify-prophoto-faithful` **26/0**。
    ⚠ **2026-09-19 实测复核更正**：上一段是本轮（2026-09-16）的**快照，不是现状** ——
    `verify-color-strategy` 现为 **63/0**、`verify-color-wiring` 现为 **104/0**
    （2026-09-19 由 `general-purpose-3` 以 PS 5.1 宿主实跑：`PASS=63 FAIL=0` / `PASS=104 FAIL=0`）。
    同日复核**与快照 / §3.4 表一致、无需改**的：`verify-gamut-map` **36/0**、`verify-geometry-engine` **23/0**
    （探针 `geometry` **52**）、`verify-gainmap-managed` **20/0**、`verify-ps-compat` **13/0**、
    `verify-format-regression` **19/0**、`verify-widegamut-regression` **18/0**、`verify-prophoto-faithful` **26/0**、
    `run-pipeline-tests` **通过 80 / 失败 0**（以上均 2026-09-19 实跑复核）。
    ⚠ **未能复核 1 条**：`verify-decision-delivery` 实跑 `pass=46` + **1 SKIP**（缺 `results/*.avif`
    动图素材 ⇒ ⑩ 段未评估）⇒ 与本文其它处「51 项 / 0 SKIP」**不可直接比对**，留待素材齐后重测（**不臆改**）。

    **⑥ 变异验证**（由子 agent 执行并回报）：几何拦截塞回 ⇒ **36/1**；关掉 ICC 分支 ⇒ **35/4**；
    策略推导被吞（强制 `eff=Recommended`）⇒ **39/1**（红在 `L-S2≠S1`；⚠ 12 格 parity 在变异下**仍全绿**
    ⇒ 光靠 parity 抓不到这类缺陷，那条断言是必需的）。三处均已回滚并复跑全绿。

    **⑦ 仍未做（见 `HANDOVER.md` 顶部清单）**：GainMap 接入引擎 · AVIF/GIF/JXL/JXR/DNG 输出的色彩执行 ·
    渐进 JPEG（工具链不支持）· 把 S2/S3 的 **Reject 结局**搬进传统管线 · 删 legacy 执行体。
    ⚠ **2026-09-17 更正（本条 ⑦ 是当时的快照，不要当现状读）**：AVIF / GIF / GainMap 均已接入；
    **JXL 按后端分级**接入（`backend==Ffmpeg` 已进白名单，`backend==Cjxl` 仍走 `ProcessCjxlAsync`）
    ⇒ 仍在出口外的只剩 **JXL 的 Cjxl 后端 / JXR / DNG**。以 `HANDOVER.md` 顶部清单与
    `MANUAL_VERIFICATION.md` 的对照表为**单一真值**。
    ⚠ **2026-09-17 再次更正（cjxl 出口轮，(b)）**：上面那句"Cjxl 仍走 `ProcessCjxlAsync`"**已失效** ——
    `jxl + Cjxl` 现由引擎的 **cjxl 执行出口**接管（计划位 `ColorTransformPlan.ExternalEncoderExit`，
    路由门 `ColorEngineRouter.BlockReason` 对 `jxl && backend==Cjxl` 放行，出口见
    `RawColorPipeline.EncodeJxlViaCjxlAsync`/`PipeToEncoderAsync`：变换后的 raw → `ffmpeg -f image2pipe -c:v ppm|pam -`
    → `cjxl - out.jxl -x color_space=|icc_pathname= --intensity_target=`，不落中间文件）。
    ⇒ 仍在出口外的只剩 **JXR / DNG**（以及动画输入、GainMap+最长边缩放两条组合）。
    ⚠ **2026-09-21 再次更正：上一行的 `JXR` 也已失效** —— JXR 现已接入引擎
    （复用 `ColorTransformPlan.ExternalEncoderExit` + `RawColorPipeline.EncodeJxrViaJxrEncAppAsync`，
    中转容器由 `plan.OutBitDepth` 决定：8-bit ⇒ BMP / >8-bit ⇒ TIFF；alpha 走第二输入不拍平）
    ⇒ **仍在引擎出口外的只剩 `DNG`**（外加动画输入、GainMap+最长边缩放两条组合）。
    单一真值以 `HANDOVER.md` 顶部清单为准（那里已写「**只剩 DNG**」）。
    ⚠ 出口内任何一环失败（cjxl 缺失 / 要 ICC 拿不到 / 语义落不进 cjxl 枚举）一律**显式失败**，
    **绝不回退** ffmpeg 的 libjxl。⚠ 本轮同时修掉一个由该出口**暴露**的引擎缺陷：
    `ColorTransformPlan.ToSpec()` 对 `CarryIcc`/`None` 计划回落 `?? Linear`/`?? Srgb` 猜测式默认
    ⇒ 变换把「像素零改动」的产物整幅提亮（实测逐像素 == `lin2srgb`，均值 0.600→0.713）；
    现按 Action 短路成恒等，回归锁见 `ServiceProbe wire` 与 `verify-decision-delivery.ps1` ⑬ 段。

---

53. **门禁脚本自身的宿主兼容性缺陷 —— 门禁跑不起来 = 假绿（2026-09-16）**

    **背景**：本轮收尾复跑门禁时，`verify-color-token-normalize.ps1` 出现 **exit=1 且无任何
    PASS/FAIL 汇总行**，与登记的 33/0 不符。逐字读输出才发现是**解析错误**：
    `L215 Unexpected token '?' in expression or statement`。

    **① 三元运算符 `? :` 是 PowerShell 7+ 专属** —— `verify-color-token-normalize.ps1:215` 写了
    `($_ -eq 'gbr' ? 'rgb' : $_)`。运行器 `_run-step3-gates.ps1` 取**当前宿主**作为子脚本解释器
    （`$psExe = (Get-Process -Id $PID).Path`）⇒ **子脚本跑 PS 7 还是 PS 5.1，取决于运行器是怎么启动的**。
    在 `powershell.exe`（**Windows PowerShell 5.1**）下该脚本**解析失败、一行都不执行**。
    此前登记的 33/0 是在 PS 7 宿主下取得的 ⇒ **数字不可跨宿主复用**。
    **修法**：改用 `if` 语句块（`$n = $_; if ($n -eq 'gbr') { $n = 'rgb' }`），PS 5.1 / 7 双通。
    ⚠ **本条的定性在收尾时被自我更正过一次**：初稿沿用了运行器里「本机没有 pwsh（`Get-Command pwsh`
    ⇒ NOTFOUND）」的旧注释；实测该注释**已过期** —— `pwsh` 确实在 PATH 上
    （`C:\Program Files\PowerShell\7\pwsh.exe`，7.6.6），`powershell.exe`（5.1）也在。
    但**缺陷本身成立、且比初稿的表述更值得警惕**：真实约束不是「没有 pwsh」，而是
    「**宿主随启动方式而变**」⇒ 脚本必须**双宿主兼容**。已同步更正运行器与 `verify-ps-compat.ps1` 的注释。

    **② 全角弯引号 `“ ”` 在 PS 里也是字符串定界符** —— `_probe-icc-affects-decode.ps1:34,35` 在
    **普通双引号串内部**写了 `“…”`，PS 把 `“` 当**闭合引号** ⇒ 字符串提前结束 ⇒
    `Unexpected token '无'`，该诊断脚本**完全无法解析**（L1/L5/L6 的弯引号在注释里，无害）。
    **修法**：串内改用项目惯用的 `「」`。
    ⚠ 本项目文档大量使用弯引号，**从文档复制到脚本**就会踩（我写诊断命令时也当场踩了一次）。

    **为什么值得单列一条**：这两处的表现都是**门禁静默失效** —— 不产出 PASS/FAIL、不写日志，
    运行器只看到「没有汇总行」。它比断言失败更危险：**假绿**。与第 50 条（门禁完整性缺陷）同类。

    **③ 固化为常驻门禁 `verify-ps-compat.ps1`（13/0）**：语料自检（≥30 个 `.ps1`，防扫空目录式假绿）
    + 逐文件用**当前宿主**解析器验证可解析 + 剥离字符串/注释后正文不得出现 PS7+ 专属语法
    （三元 / `??` / `?.` / `&&` / `||`）+ 8 条负控 + 2 条点名回归锁。已登记进 `_run-step3-gates.ps1`
    脚本清单**首位**（零依赖、最快，能在跑重活之前先抓出脚本列表本身的语法问题）。
    **范围刻意只查语法级**：PS7 专属 **cmdlet/参数**（如 `ForEach-Object -Parallel`）在 5.1 下抛
    `CommandNotFound`，是**响亮**失败而非静默假绿，不属本门禁职责（且文本匹配误报率高）。

    **变异验证 · 双宿主**（新断言必须有牙，且两宿主下都要有牙）：
    ① PS 7.6.6 宿主 —— 解析检查**通过**（0 错），但**文本扫描命中 1 处** ⇒ 转红 **11/2**；
    ② PS 5.1 宿主 —— 解析检查**失败 1 个** + 文本扫描命中 1 处 ⇒ 转红 **11/2**。
    ⇒ 两种机制**互补**：PS 7 下解析器抓不到三元，正是文本扫描补上；回滚后两宿主均 **13/0**。
    另：`color-token-normalize` 在两宿主下均 **33/0**。

    **变异验证**（新断言必须有牙）：把三元塞回 `verify-color-token-normalize.ps1:215`
    ⇒ `ps-compat` **11/2**（语料级「失败 1 个」+ 点名回归锁「解析错 5 / 语法 1」**双双转红**），
    exit=1；回滚后 **13/0**。另：该门禁自身初版因写 `'a'$([char]10)'b'`（PowerShell **没有**
    相邻字符串自动拼接）而解析失败 ⇒ 已改为数组 `-join`，并把这条坑写进脚本注释。

    **④ 顺手修的文档缺陷**：`docs/TESTING.md` 的 §7 标题与 §7.1 标题**粘在同一行**
    （`## 七、UI 验证方法论（2026-08-16 沉淀）### 7.1 静态完整性检查（编译前）`）⇒ 已拆行，
    §7 及其 4 个子节现在都是独立标题。

    **⑤ 附记（工具坑）**：本文件是 **CRLF**，但 `pathlib.read_text()` 默认做 universal-newline
    转换（`\r\n` → `\n`），会让人误判为 LF；写回时又按 `os.linesep` 转回 CRLF，故行尾风格能保持。
    **判断行尾必须读 bytes**（`read_bytes()` / 数 `\r\n`），别用 `read_text()` 后的 `'\r\n' in s`。
    本轮据此修正了插入脚本，并核到全文件 **1615 行零孤立 LF**。

    **⑥ 本轮门禁**：`ps-compat` **13/0** · `color-token-normalize` **33/0**（恢复登记值，
    且**首次真正能在回退宿主下跑起来**）· 其余全绿（探针 **363/0**、`strategy` **40/0**、
    `wiring` **39/0**、`format-regression` **19/0**、`widegamut` **18/0**、`prophoto-faithful` **26/0**、
    `hlg-route` **13/0**）。

---

54. **GainMap（Ultra HDR JPEG）接入色彩引擎（原 🔴【待实现】→ ✅ 已实现，2026-09-16）**

    **背景**：GainMap 原是一条**完全独立的专用管线**，完全绕开引擎 —— `QueueProcessor.cs:479-484`
    直接分派到 `ProcessGainMapJpegAsync`（`:1508-1675`），且 `ColorEngineRouter.cs:97` 有
    `if (o.JpegGainMap) bad.Add("GainMap（属 P6）")` 把它拦在引擎外。本轮让**引擎拥有决策与执行**。

    **① 关键判断：规划层早已是 GainMap 感知的，缺的只是「执行层看不到它」** ——
    `ColorStrategyPlanning` 已有两条专门逻辑：`:27-35`（非 JPEG ⇒ Reject + 建议）、
    `ResolveStrategy` 覆盖①（`GainMapRequested` ⇒ 生效策略被覆盖）。`ColorIntent.GainMapRequested`
    与 `PlanPolicy.GainMapRequested` 都已存在。**但 `ColorTransformPlan` 本身不持有 policy**
    ⇒ 执行层无从得知 ⇒ 新增 `ColorTransformPlan.GainMap`，并在 `Plan(...)` 外层统一
    `p.GainMap = pol.GainMapRequested;`（覆盖**所有**返回分支，比塞在某一段更彻底）。

    **② 引擎执行层新增 GainMap 出口** —— `RawColorPipeline.TransformFileAsync` 在**入口**分流
    （`plan.GainMap` ⇒ `TransformGainMapAsync`），而不是把 GainMap 硬塞进 `rgb48le` 那条整数通路：
    **那条通路没有 >1.0 的余量，而 GainMap 的输入契约是线性 HDR 浮点（1.0 = 峰值 nits）**。
    出口三步：zscale 线性化解码（`gbrpf32le`，失败回退直解以适配已是线性的输入）→ 平面交错成
    `float[] rgba` → `GainMapEncoder.EncodeAsync`，**跳过** `BuildEncodeArgs` 与 ffmpeg 编码。
    失败一律抛异常（与既有语义一致 ⇒ 调用方按「引擎失败」回退）。

    **③ 单一真值 `GainMapJpegCodec`（新文件）** —— 解码滤镜串、回退解码串、平面→RGBA 交错、
    编码参数解析**全部集中在一处**，旧专用管线与引擎出口**都只调这里**
    （`QueueProcessor.cs:1565/1580/1588/1615`）。理由：两边各写一份必然出现「同一素材走两条路径
    得到不同 headroom（偏暗或增益图范围错）」，且极难定位。
    ⚠ 平面序是 **G,B,R**（ffmpeg 的 `gbr` 命名，不是 RGB）—— 理解错只会表现为 R/B 互换，
    **大图上才显形**。

    **④ 三处「不修就会静默出错」的细节**（理由都写进了代码注释）：
    - **清空计划层标注**（`RawColorPipeline.cs:241-244`）：底图 ICC 由 `GainMapEncoder` 自写
      （对齐 libultrahdr 的 `writeIccProfile`），而计划层的目标空间是**由源推导**的
      （如 bt2020 + sRGB 传递），二者不是一回事。保留 `AttachIcc` ⇒ 后置步骤会用目标空间 ICC
      **覆盖底图 ICC**（底图是 sRGB/Rec.2020 的 SDR 图 ⇒ 错标）；更危险的是**改写 JPEG 的 APP2 段
      可能破坏 MPF 偏移**，而增益图定位完全依赖它。
    - **`Action==None` 不等于「不需要 GainMap 出口」**（`QueueProcessor.cs:740-747`）：GainMap 的
      `Action=None` 只说明「像素无需色彩映射」。缺了这条守卫，直通的 GainMap 任务会掉进 ffmpeg 分支，
      产出**没有增益图的普通 JPEG**（静默降级）。
    - **标注一致性守卫对 GainMap 跳过**（`:762-768`）：该出口的描述由底图 ICC / ISO 元数据承担，
      与计划层 `OutPrimaries` 本就不该相等 ⇒ 比出来是假告警。

    **⑤ `S3/S4 + JPEG + GainMap` 的策略覆盖（超出原规格的一处改动，已由 lead 拍板保留）** ——
    接入引擎后这两格会被规划层 Reject（S3：JPEG 无 CICP 承载通路；S4：无显式目标），
    而基线（旧专用管线）**有产出** ⇒ 属功能回退。故把 §2 覆盖① 从「仅 S2」扩展到**三条非 S1 策略**，
    留痕不变（`Override=GainMapRequiresBaseMapping`、`EffectiveStrategy=Recommended`、
    `DeclaredStrategy` 原样保留）。
    依据：`OverrideReason.GainMapRequiresBaseMapping` 的存在本身表明「GainMap 是**功能要求**、可覆盖策略」；
    `Models/ColorStrategy.cs` 文档原文即「功能性需求可覆盖本策略（如 GainMap 底图必须映射）」；
    且 JPEG **结构上不可能兑现 S3**。

    **⑥ 几何缩放的诚实拒绝** —— 「GainMap + 最长边缩放」**本增量不承接**：增益图按底图尺寸降采样、
    MPF 偏移与两图尺寸强耦合，把 `scale` 插进这条链是一条**未被任何门禁覆盖的新几何路径**。
    路由层显式拦（`ColorEngineRouter.cs:109-111`）+ 执行层防御性抛错（`RawColorPipeline.cs:182-185`），
    **不静默忽略**。⚠ 诚实边界：回退到的旧专用管线**同样忽略缩放**（既有缺口，本次未修）。

    **⑦ 验证**：
    - 门禁：探针 **372/0**（contract **100** · wire **37** · engine 32；⚠ 此为**当时值** —— 其后为「策略覆盖留痕」新增 7 条 contract 断言 ⇒ contract 增至 **107**、总数增至 **379**）·
      `verify-gainmap-managed` **20/0**（**且实测确认这次编码走的是引擎出口**）·
      新增 `verify-gainmap-engine` **25/0**（2026-09-19 已增至 **84/0 → 117/0**，见本节末 ⑧b）· `verify-color-wiring` **44/0** ·
      `verify-color-strategy` **40/0** · `verify-format-regression` **19/0** ·
      `verify-widegamut-regression` **18/0** · `verify-prophoto-faithful` **26/0** ·
      `verify-hlg-route` **13/0** · `verify-color-token-normalize` **33/0** · `verify-ps-compat` **13/0**。
      构建 **0 警告 0 错误**。
    - **独立产物验证（读容器，不看退出码）**：引擎产物 `images=2`、MPF 副图
      `size=1471 offset=9242 abs=11631 soi=True`、ISO 21496-1 `min=0 max=2.3 gamma=1 ch=1`；
      exiftool 交叉验证 `MPImageLength=1471 / GainMapMin=0.00 / GainMapMax=2.30`。
      `GainMapMax=2.30` 正是 `log2(1000/203)=2.3005`，与声明的 headroom 吻合。
      **反证组**（同源不开 GainMap）⇒ `images=1 / iso=null / pass=1 fail=5`
      ⇒ 证明探针**能区分**「真 Ultra HDR」与「普通 JPEG 改名」，绿灯不是空转。
    - 变异验证：测试侧 6 组，含「把**错误实现**的期望值装进负控」以证明负控不是空转。

    **⑧ 仍未做 / 未验证面**：`plan` 被执行层改写（清空标注）属**约定**而非类型强制，将来新增读 `plan` 标注的后置步骤必须同样尊重。

    **⑧b ✅ 2026-09-19 补覆盖：原「未验证面」的 C⑥ / C⑦ 已闭环**（判据一律**第三方可证**，不看产品日志散文）：
    - **C⑥**：`--jpeg-gain-map-base-gamut bt2020` / `--jpeg-gain-map-multi-channel` 在**引擎出口**下已有门禁
      （`verify-gainmap-engine.ps1` 新增 §5b / §5b' / §5c）：`exiftool` 读**底图内嵌 ICC 色度**
      （bt2020 红原色 x=**0.70799 0.29201** vs 默认 sRGB **0.64 0.33**）· `exiftool` 读 XMP hdrgm `Channels`
      （multi=**3** / 默认=**1**）· ffmpeg 解**主图 raw 比 MD5**（SDR 源 + base=bt2020 vs srgb ⇒
      底图像素**真被转换**，非只改标签）· 非法取值 `bogus` ⇒ **exit 2 + 零产物 + 点名「取值无效」**。
      ⚠ 原 §5 对 bt2020 只有**产品日志散文** + `channels=1`（= 默认值 ⇒ **无判别力**），属空转绿。
    - **C⑦**：`isHdrInput` 判据换源（ICC 感知的 `ProbeInputColorMetadata`）已**实测** —— 构造
      「cICP=PQ + 内嵌 sRGB ICC」的 PNG ⇒ 引擎走 **SDR**（XMP `GainMapMax=0.00`）；**去掉 ICC 的对照 ⇒ HDR**
      （`2.30`）—— 唯一变量是 ICC。⚠ **诚实登记：此行为属疑似缺陷** —— PNG 第三版规范 §4.3 / Table 1
      规定 **cICP(优先级 1) > iCCP(优先级 2)**（「优先级数字最低者优先」）⇒ 该素材按规范应判 **HDR(PQ)**。
      断言锁的是**当前真实行为**，注释已标「将来按规范修成 HDR 会转红 = 预期红」。
    - **变异验证（7 组，真脚本未动、用 scratch 副本）**：bt2020→srgb **74/3** · multi true→false **75/2** ·
      素材去 ICC **74/3** · `bogus`→`bt2020` **77/3** · SDR 例去掉 base-gamut **83/1**（恰 1 条红、两路 MD5 变相同）；
      §10 落地后**新增 2 组**（由 `general-purpose-1` 实测）：`ProgressiveLevelOf` 恒 `-1` ⇒ **106/9**（9 条红**全在 §10**）·
      去掉 `ColorEngineRouter` 的 `&& !gainMapJpeg` ⇒ **114/3**。
      ⚠ **跑本脚本别用 `-p:OutputPath=<临时目录>` 构建**：临时目录里的 exe 找不到 `cjpegli` ⇒ 静默回退 ffmpeg mjpeg ⇒
      ⑤b×3 底图 ICC 读空 + ⑨×2 `ISO max=2.3`，共 **5 条与代码无关的假红**；必须用共享 `src/FfmpegGui/bin/Release/net11.0/win-x64/`。
      ⚠ **宿主差异（PS 7 独有）**：脚本结尾的「GUID 目录自清」在 **PS 7 下会被宿主 safe-delete 守卫 fail-closed 拦下**
      （日志出现 `[safe-delete][SAFE_DELETE_BULK_CONFIRM_REQUIRED]`）⇒ `tests/output/validate/gmeng_<guid>` **会遗留**
      （PS 5.1 子进程里不被拦）⇒ 只可说「成功时自清；**PS 7 下可能遗留 GUID 目录**」，**不得**写「跑完自动清干净」。
    - **基线**：`verify-gainmap-engine` **25/0 → 84/0 → 117/0**（2026-09-19 实测；**PS 5.1 与 PS 7 双宿主各跑一次** —— 84/0 时均 `PASS=84 FAIL=0`；§10 扩展后均 `PASS=117 FAIL=0` / `exit=0`，同一 Release 产物 `10:40:13`、同一 `FFMPEGGUI_FFMPEG_DIR`）。
    - **⚠ #20(0919)（心跳判据锁）后复验（2026-09-19）**：整轮实测 `tests/output/step3-round-1127.log:524` =
      `[verify-gainmap-engine.ps1       ] exit=0  ===== gainmap-engine: PASS=117 FAIL=0 =====`
      ⇒ **仍 117/0**，与 #20(0919) 之前一致 ⇒ 心跳默认开**未扰动** gainmap 断言。
      ✅ **有效性前置已满足**（双路互证）：`Check-SrcDrift`（`_run-step3-gates.ps1` 函数定义 `:500`）检出漂移时**同时**做两件事 ——
      `:509` 打印 `[WARN] 跑期间源码被改动…` **且** `:512` `$failedItems.Add("源码漂移：… ⇒ 本轮结论作废")`；
      而 `Exit-WithSummary`（定义 `:222`）的汇总行文字由 `failedItems.Count` 决定（`:225-226` 失败 / `:231` 全部通过）⇒ **`全部通过` ⟺ `failedItems` 为空** ⟺ 漂移检查通过。
      本轮 `step3-round-1127.log:544` = `===== 运行器汇总：全部通过 =====` ∧ `WARN` 命中 **0** ⇒ 两路一致。
      ⚠ 该函数**干净时零输出** ⇒「日志里搜不到 `SrcDrift` 行」是**预期**，**不能**当作「没跑」。
      ⚠ 上列行号**锚在运行器快照**（`SHA256=C999C35BF6C49506…` 16 位前缀 / 57518 B / mtime `2026-09-19 12:15:44` / 671 行，**已冻结**）——
      **该指纹一变，上列数字即作废**（`#22(0919)` 落盘使 `:335-350` / `:344` / `:347` / `:215-221` / `:419` / `:464-479` / `:473` / `:476` / `:599` / `:600` 全部失效；
      `#23(0919)` 落盘又使**调用点** `:604` / `:605` 失效，现为 **`:670`** / **`:671`**）⇒ **一律以函数名与判据原文为准**，行号只作快照读。
      ⚠ **再失效一次（2026-09-19 复核，我一手实测）**：`#28(0919)` + 第 15 / 16 针落盘后，现役运行器 =
      **792 行 / 66961 B / mtime `2026-09-19 12:49:09` / `SHA256=7C7A47252B40E1E7…`（16 位前缀）**
      ⇒ 上列行号**全部作废**；实测**现值**：`Check-SrcDrift` 定义 **`:583`**（原 `:500`）、`[WARN]` 打印 **`:592`**（原 `:509`）、
      `failedItems.Add("源码漂移…` **`:595`**（原 `:512`）；而 `Exit-WithSummary` 的 **`:222`（定义）/ `:225`–`:229`（失败分支）/ `:231`（全部通过）/ `:232`（`exit 0`）** 位置**未动**。
      ⚠ **同一结论、且这是第二次重演**：**以函数名与判据原文为准，行号只作快照读**（见第 78 条「引用行号必须带快照时刻」）。
      ⚠ `#NN` 为**会话内**任务号；**跨会话引用一律带日期后缀**（自 2026-09-19 起；理由与二义实例见第 78 条）。
      ⚠ 上列 **7 组变异读数均为 #20(0919) 之前**（#20(0919) 的 `FfmpegRunner.cs` mtime = **11:17:23**）。#20(0919) 后未复测 —— **但不要求复测**：
      这 7 组的靶点是 `ColorEngineRouter` / `QueueProcessor` 的 guard 与渐进级别，与 #20(0919) 改动（`FfmpegRunner` 的**心跳注入**）**正交**；
      且 post-#20(0919) 整轮已复绿（`117/0`，`step3-round-1127.log:524`）。**若将来 `FfmpegRunner` 的心跳注入方式再变，才需连带复测这 7 组。**

---

55. **普通通路的「每任务内存预留」自引擎成为默认起失效 —— 门禁抓到的真实内存安全缺口（2026-09-16）**

    **发现路径**：本轮 GainMap 接入后跑全量门禁，`verify-gainmap-memory.ps1` 实测 **3/2**（基线 4/0）。
    逐条定性后拆成**两件事**：一件是门禁自身的假红，一件是**真实的产品缺陷**。

    **① 门禁假红（已修）**：脚本用 `$rGm.Exit` 判"转换是否成功"，而该值在本宿主下取到 `$null`
    ⇒ 把"其实成功"误判为"转换未成功"（产物与日志都证明成功：`完成 1 项, 失败 0 项`）。
    **修法**：成功判据改为**看产物**（存在且非空），退出码仅作参考回显。
    教训：断言必须看**交付物**，不要看间接信号。

    **② 真实缺陷（已修）**：`plainMB = 64` 的标定前提是「普通 ffmpeg 通路 4K 峰值仅 **53.7MB**，
    **全程流式、不进托管线性缓冲**」—— 那是**传统管线**的数字。**自第七轮「切默认到自研引擎」起该前提失效**：
    引擎的普通出口同样要先把整帧解码成 `rgb48le` 原始缓冲再做托管变换。
    同一 4K 素材 A/B 实测（Release，同机同轮，采样 `WorkingSet64`）：

    | 通路 | 4K 峰值 |
    |---|---|
    | 普通 + 引擎（**默认**） | **151.5 MB** |
    | 普通 + 传统（`--color-engine legacy`） | **51.9 MB** |
    | GainMap（引擎） | **684.2 MB** |

    差异来源：4K ⇒ `3840×2160×6 ≈ 49.8MB` 入 + `24.9MB` 出，加 GC/文件缓冲。
    ⇒ 旧值对现在的默认通路**低估 2.4 倍** ⇒ `ClampConcurrencyByMemory` 会**高估可用并发**，
    高并发 + 大图**可 OOM** —— 正是这条门禁当初存在的理由。
    **修法**：`plainMB = 192`（实测 151.5MB 留 ~27% 余量），并把 A/B 数据写进代码注释。

    ⚠ **为什么值得单列一条**：这条缺陷**静默存在了多轮**（自第七轮起），
    因为门禁本身是**红的**，而红的门禁最容易被当成「老问题」跳过。**门禁长期红 = 没有门禁**。

    **验证**：修后 `verify-gainmap-memory` **5/0**（新增「两条通路都产出非空产物」这条计数）；
    `verify-ps-compat` **13/0**（脚本改动后必跑）；构建 **0/0**。

---

56. **`ServiceProbe plan` 的 ICC 断言依赖 `%TEMP%` 缓存 —— 根因是探针的载体 PNG 坏了（2026-09-17 登记，同日修正根因并修复）**

    **现象**：`plan` 基线 **39/0**；若 `%TEMP%`（`PlatformServices.GetTempDir()`）下缺
    `stdicc_*.icc`，该门禁就转红。**2026-09-17 更正**：缺哪个就红哪条 ——
    清空全部 3 个（srgb / p3 / b2020）⇒ **`pass=24 fail=3`**
    （`FAIL iccgen 未产出 srgb|p3|b2020 ICC`，每 tag 另有 4 条子断言被 `continue` 跳过）；
    只缺 `stdicc_bt2020_iec61966-2-1_bt2020nc.icc` ⇒ **`pass=34 fail=1`**（原登记形态）。

    **真根因（原登记的第 ③ 段是错的，此处更正）**：**不是**"iccgen 生不出
    `bt2020/iec61966-2-1/bt2020nc`"，而是 `tests/ServiceProbe/Program.cs` 里**硬编码的 1×1 PNG
    载体 base64 本身就是坏的** —— ffmpeg 解码它就报 `[png] inflate returned error -3`
    （实测 exit=69、零产物）⇒ `GetOrGenerateStandardIcc` 拿不到中间 PNG ⇒ 恒返回 null。
    于是那三条断言**只能靠 `%TEMP%` 缓存蒙对**，与代码正确性无关。
    实测（2026-09-17）：把载体换成 ffmpeg 现场产出的**有效** 1×1 PNG（90 B）后，**三组都能现场生成**
    （bt709 / smpte432 / bt2020 全部 exit=0、各产出 584 B ICC；缓存清空也能重建）。
    `iccname` 模式一直是绿的，正是因为它走 `ResolveNamedIcc(spec, carrier: null, …)` ⇒
    命中 `GetOrGenerateStandardIcc` 内部的 **lavfi 兜底**（`-f lavfi -i color=c=gray:s=2x2`），
    根本不碰那个坏 PNG —— 这也是"坏载体"长期没被发现的原因。

    **修正**：`tests/ServiceProbe/Program.cs` 的载体 base64 换成有效 PNG，并就地写明根因。
    **产品代码未改**（`GetOrGenerateStandardIcc` 的 lavfi 兜底与真实载体路径本来就正常）。

    **验证**：清空 `%TEMP%/stdicc_*` 后 `plan` **39/0**，且**当场重建**出 3 个 584 B 缓存
    （含此前"重建不了"的 `stdicc_bt2020_iec61966-2-1_bt2020nc.icc`）；常态复跑 **39/0**；
    探针 19 mode 合计 **427**。**变异**：把载体改回坏 base64 + 清缓存 ⇒ **`pass=24 fail=3` / exit=1**，
    且**一个缓存都没重建**；回滚后复验 **39/0**。
    另注：重新生成的 ICC 与旧缓存**只差 header 的 12 字节 date/time 字段（offset 24–35）**，
    色度学内容逐字节相同 ⇒ 换载体不改变产物语义。

    ⚠ **为什么值得单列一条**：这是「门禁的隐式前置条件」里一个**可修**的样本。
    原处置是"只登记、不改"，理由是"iccgen 生不出该组合"——而那个理由**本身是错的**
    （错在把"载体解不开"当成了"iccgen 不支持"）。正确做法是修掉坏载体（一行），
    门禁从此与机器状态解耦；**不要**把断言改成"缓存缺失就 SKIP"（那是为了绿而放宽）。

---

57. **`verify-decision-delivery` 的三处修复（2026-09-17）：PS 5.1 `Sort-Object` 陷阱 + `:148` 判据从「命令行」改为「像素」+ 宿主批量删除加固**

    **① PS 5.1 的 `Sort-Object <属性名>` 对 Hashtable 的键不做属性适配 —— 两条素材自检恒红**

    **现象**：该门禁首跑 **34/5**，其中两条与产品完全无关的红是
    `FAIL 素材：GIF→AVIF 产物有 0 帧颜色流（≥4…）` 与 `FAIL 素材：AVIF 带同帧数的 gray alpha 平面`。
    手动 `ffprobe` 该产物显示它**确有** 4 条流（`0,yuv444p,1 / 1,gray,1 / 2,yuv444p,10 / 3,gray,10`），
    与断言预期完全相符 ⇒ 是**门禁自己读错了行**。

    **根因链**（三段，缺一不可）：
    ① `:295` 的 `$probe -split "`r?`n"` 产生 **6 行**（含 2 个空行）⇒ `$rows` 里混入
       `idx=0, pix='', n=0` 这一条空行；
    ② `:297` 的 `Sort-Object n -Descending` —— **Windows PowerShell 5.1 对 Hashtable 的键
       不做属性适配**（PS 7 会）⇒ 拿不到键值、降序也失效；
    ③ `Select-Object -First 1` 于是取到那条空行（`n=0`）⇒ `$srcColor.n -ge 4` 恒假。

    **实测对照**（同一份 `$rows`）：
    | 宿主 | `Sort-Object n -Descending` 结果 | `Select-Object -First 1` |
    |---|---|---|
    | Windows PowerShell **5.1** | `0,0,1,10`（另一轮实测 `1,0,0,10`，**最大项排在最后**） | **0**（错） |
    | PowerShell **7**（7.6.6） | `10,1,0,0` | **10**（对） |
    | 两者 · 修复写法 `Sort-Object { $_.n } -Descending` | — | **10**（都对） |

    **修法**：`:302` 的排序键改为 **scriptblock** `Sort-Object { $_.n } -Descending`（附 6 行注释说明本坑）。
    修后该组 7 条转绿。

    ⚠ **与第 53 条同族，但不同层**（务必交叉阅读）：第 53 条讲的是
    「**宿主随启动方式而变** ⇒ 脚本必须双宿主兼容」，抓的是**语法级**失效（PS 7 专属语法 / 全角弯引号
    ⇒ **解析失败**、一行都不执行）；本条是**语义级**失效 —— 语法在两宿主下都通，但**同一个 cmdlet
    对同一数据的行为不同** ⇒ 静默错序、断言恒红，而 `verify-ps-compat.ps1` 的范围**刻意只查语法级**
    （见第 53 条 ③），**抓不到这一类** ⇒ 只能靠「两宿主实测对照」发现。
    排查建议：门禁里凡是对 **Hashtable / PSCustomObject 混合集合**排序的，一律用 scriptblock 键。

    **② `:148` 断言的判据从「命令行匹配」改为「像素/色度度量」**

    旧写法 `$r5.log -match 'zscale=[^"]*p=smpte432'` 描述的是**传统管线 H2 形态**（命令行里出现 zscale 滤镜）。
    默认走自研引擎后，这一格由 **H1 进程内内核**完成像素变换
    （`RawColorPipeline.TransformFileAsync` **不生成任何 zscale 滤镜**，且 `VerifyPixelColorEntry`
    的结构不变量禁止 H2 命令行出现第二条色彩入口）⇒ 该断言在现行架构下**不可满足**，
    它会把一个**已经兑现**的格判红（同组另一条断言当时就 PASS：ICC 红原色 x=0.67999 = Display P3）。

    **新判据（只看产物；2026-09-17 第三轮改为相对判据）**：把源按标准 sRGB→Display P3 变换后，
    与**交付像素**做**全帧 PSNR**（用 `zscale` 作独立参照口径，与本仓库既有互测一致）。
    同时量**两个**参照下的绝对值，判据取**两者的差**：

    | 量 | 定义 | 实测（饱和素材 `sdr_plain.png`） |
    |---|---|---|
    | 按 P3 解 `$p3Psnr` | PSNR(交付, 源→Display P3) | **38.196 dB** |
    | 按 sRGB 解 `$noopPsnr` | PSNR(交付, 源) | **14.146 dB** |
    | **差 `$p3Gain`** | 上两者之差 | **24.050 dB**（余量 ~9 dB） |
    | 只贴 P3 标签而**像素没动** | 差 | **≈ 0 dB**（两个参照都≈源） |

    ⇒ 判据 `$p3Gain -ge 15`（`verify-decision-delivery.ps1:180-181`），**两个绝对值同时回显**便于排查。
    **为什么不用绝对阈值**（第二轮曾取 30 dB）：绝对阈值与 WebP 的**有损编码质量**绑定 ——
    `$p3Psnr` 的天花板就是有损编码的噪声底（实测 ~38 dB），将来若默认质量下调，`$p3Psnr` 会跟着掉，
    **真绿会转红**；相对判据自校准（像素真转 P3 ⇒ 按 P3 解远好于按 sRGB 解；只贴标签 ⇒ 两者几乎相同）。
    ⚠ 两个实测必需口径：`zscale` 的 `min`/`m` 只认 **`gbr`**（写 `rgb` 会被当表达式 ⇒ `Invalid argument`）；
    且 zscale 输出是 **gbrp 平面序**，比较前必须再 `format=rgb24`，否则三通道错位。

    **变异验证**（新断言必须有牙）：
    ① 把预言机的目标原色由 `smpte432` 改成 `bt709`（= 交付像素其实不在 P3 域）⇒ 差 **0 dB** ⇒ 转红 **43/1**；
    ② 把期望阈值由 `15.0` 改成 `100.0` ⇒ **24.05 < 100** ⇒ 转红 **43/1**。

    **④ ⚠ 相对判据的前提：素材必须含饱和色**（2026-09-17 实测登记 —— 这是**换素材时的隐性陷阱**）。
    `$p3Gain` 的上限由 **P3 变换本身的像素幅度**决定：素材越中性，sRGB 与 Display P3 两个参照越接近
    ⇒ 差被压小 ⇒ 假红。实测「源 vs 源→P3」的 PSNR（= 该素材能达到的差的上限）：

    | 素材（RGB 通道色差 max−min） | 源 vs 源→P3 PSNR |
    |---|---|
    | `sdr_plain.png`：**96.41%** 的像素色差 ≥128，左上角 `127,0,254` | **14.07 dB** |
    | 合成灰渐变，色差 64 | 36.01 dB |
    | 合成灰渐变，色差 32 | 41.44 dB |
    | 合成灰渐变，色差 8 | 52.90 dB |
    | 纯灰（色差 0） | `inf`（判据里折成哨兵 99.0） |

    **端到端验证（真实 CLI，非推算）**：把素材换成「色差 64」的低饱和渐变，同一条命令
    （`--headless -i <src> -o <out> -f webp --color-space "Display P3"`）跑真实产品 ——
    产品**行为正确**（像素确实转了 P3：按 P3 解 42.042 dB 高于按 sRGB 解 35.716 dB），
    但**差只有 6.326 dB < 15 ⇒ 判红**。⇒ 这就是**假红**：低饱和素材把 `$noopPsnr` 抬到接近
    有损噪声底，差被压没。**⇒ 换素材时必须同步复核此断言**：新素材的「源 vs 源→P3」PSNR
    须明显低于「有损噪声底 − 15dB」（当前 ≈ 23 dB），否则该格会假红。

    **⑤ `-EA SilentlyContinue` 的代价（有意接受，2026-09-17 登记，同日更正一次）**：它会
    **掩盖真实的删除失败**。当前可接受的依据是两条：① 本宿主**批量删除守卫**抛的是**伪错误** ——
    删除其实成功（多次 `Test-Path` 实测为 `False`），报错文本里的 `Xthrow` 是宿主实现
    （项目脚本里没有这个函数，据此可区分伪影与真失败，见第 44 条）；② 输出目录**每次运行都重建**，
    残留不跨轮累积。
    **将来可选加固**：把清理改成 `Remove-TreeVerified`（删完 `Test-Path` 断言为空，失败即出声）——
    属独立改动，本轮不做。

    **⑥ ⚠ 沙箱宿主限制：`| Remove-Item`（管道喂路径）静默失效**（2026-09-17 登记，定性经复核更正）

    **现象**：在**本沙箱 agent 宿主**内，把路径**经管道**交给 `Remove-Item`，**一个文件都不会删**；
    **加 `-EA SilentlyContinue` 就完全静默**（零输出、零报错），不加则抛
    「**输入对象无法绑定到该命令的任何参数，因为该命令不接受管道输入**」。
    起因是本轮**我自己**用 `$g | Remove-Item -Force -ErrorAction SilentlyContinue` 清临时文件，
    4 个文件一个都没删掉、且零输出，靠 `Test-Path` 复查才发现。受控对照（各造 3 个文件）：

    | 形态（宿主内实跑） | 残留 |
    |---|---|
    | A `Get-ChildItem … \| Remove-Item -Force -EA SilentlyContinue` | **3 / 3（静默不删）** |
    | B `(…).FullName \| Remove-Item -Force -EA SilentlyContinue` | **3 / 3（静默不删）** |
    | C `foreach { Remove-Item -LiteralPath $f.FullName -Force -EA SilentlyContinue }` | **0 / 3** ✓ |
    | D `… \| Remove-Item -Force`（**不加** `-EA`） | 抛「**该命令不接受管道输入**」、残留 **3 / 3** |
    | E `foreach { Remove-Item -Path $_.FullName -Force -EA SilentlyContinue }` | **0 / 3** ✓ |
    | F 同 C 但不加 `-EA`（`-ErrorAction Stop`） | **0 / 3**，报错 0 条 ✓ |

    （另一种调用形态下宿主另打过 `Xthrow 'Remove-Item: missing path operand'` —— 同属 shim 的拒绝信息。）

    ⇒ **根因不是**删除量阈值，也不是脚本写错：**宿主 shim 的 `Remove-Item` 不支持管道输入**
    （`-Path` / `-LiteralPath` **显式传参**则完全正常）。**加 `-EA` 会把这条拒绝彻底静默** ——
    这是「`-EA` 掩盖真实失败」的**活体样本**；也说明第 44 条那句「守卫的删除其实成功」
    **只适用于非管道形态**。

    **⚠ 范围：仅沙箱 agent 宿主**。在**普通 shell**（用户真正跑 `pack.ps1` / `tools/build_tools.ps1`
    的地方）里，`$x | Remove-Item` 是 PowerShell 的**惯用写法且行为正常** ⇒
    **不要把本条当成脚本缺陷**。

    **对门禁的影响：无**。本轮**再次 grep**（`tests/scripts/**/*.ps1`，模式 `\|\s*Remove-Item`）
    命中 **0 处** ⇒ 门禁的清理全部是**显式传参**形态（第 57 条 ③ 的加固有效）。

    **对产品脚本的结论**：仓根 2 处管道形态 —— `pack.ps1:69`（`$pdbFiles | Remove-Item -Force`）
    与 `tools/build_tools.ps1:454`（`Get-ChildItem … -Directory | Remove-Item -Recurse -Force -EA SilentlyContinue`）
    —— **不是缺陷、不修**（为绕开沙箱伪影去改正确的产品脚本是错的，还会掩盖真正的边界）。
    **正确用法提示**：若**在沙箱内**跑这两个脚本，注意管道形态**不会生效**
    （`pack.ps1` 会中止、`build_tools.ps1` 的 build 目录清理会静默跳过）—— 这是**环境限制**，不是脚本问题。

    **给 agent 的操作约定**：**在沙箱内做清理/删除，一律用 `foreach + -LiteralPath`（显式传参），
    不要用管道形态**；且删除后**用 `Test-Path` / 计数复核**，不要依赖 `-EA SilentlyContinue` 的静默。

    **③ 宿主批量删除加固（环境必需；同日已扩到全仓）**：本文件 `:31` / `:87` / `:341` 的 `Remove-Item` 补 `-EA SilentlyContinue`。⚠ 同日**全量扫过 `tests/scripts/*.ps1`**，共加固 **19 个脚本 / 21 处**（`verify-color-caps` / `verify-color-peak` / `verify-color-tonemap` / `verify-colorfmt-matrix` / `verify-png3-interop` / `verify-color-engine-xcheck` / `verify-decision-delivery` + 13 个 `_probe-*.ps1`）。**判据**：只加固**清理/删除**类调用，**断言路径上的删除不加**（那会掩盖真实失败）—— 全仓 `Remove-Item` 现已 **0 处未加固**。对照证据（`verify-color-tonemap`，`tmv` 预填充 66 文件）：加固前 = `Xthrow [safe-delete]`、**无汇总行**、exit=1；加固后 = 汇总行 `PASS=15 FAIL=0`、exit=0。
    本宿主的批量删除守卫在累计删除量超阈值时会往脚本上下文抛
    `[safe-delete][SAFE_DELETE_BULK_CONFIRM_REQUIRED]`（**删除其实已经成功**，见第 44 条与
    `docs/HANDOVER.md` 的宿主限制一节）⇒ 不加这句，门禁会**跑到一半崩掉**、报不出 PASS/FAIL 汇总
    （实测崩在 `Px()` 的临时文件删除处）。

    **本轮结果**：**43/1 → 44/0**（exit=0）。

---

58. **`verify-color-engine-xcheck` 恒 `exit 0` —— 一个永远不会失败的门禁 = 没有门禁（2026-09-17）**

    **根因**：该脚本 47 行**全文没有任何 `exit`**，`$allOk` 只决定**打印文案** ⇒ 10 格用例里
    有 **6 格 FAIL**，进程仍然 `exit 0`。运行器按退出码判读 ⇒ 这条门禁**从来没有拦住过任何东西**。
    与第 50 条（门禁完整性缺陷）同类，但方向相反：第 50 条是「门禁没输出 ⇒ 假绿」，
    本条是「门禁有输出但退出码恒定 ⇒ 同样假绿」。

    **修法**（两件事**必须同时做**，否则补上退出码后会**立刻长期红**，正是第 55 条警告的模式 ——
    把「已定性的口径分歧」当缺陷修，要么白改、要么把参照口径改坏）：
    ① 末尾补 `if (-not $allOk) { exit 1 }`；
    ② 把 6 格**已知非缺陷**做**显式白名单**豁免（键 = 「图 / 用例」二元组，共 6 条，**不含任何通配**）。
       依据原文（两处，均为本项目既有结论）：
       - `docs/MANUAL_VERIFICATION.md:223-224` —— 「**已知非缺陷**：`verify-color-engine-xcheck` 的
         6 格 FAIL，经控制组实验判定为 **zimg 参照口径分歧**（zimg 的 bt709 是纯 γ2.4 / PQ 归一约定不同），
         **不是引擎缺陷**（详见 `HANDOVER §D`）」
       - `docs/archive/BUILD_HANDOVER.md:123-124` —— 「⚠ 唯一 FAIL：`verify-color-engine-xcheck` ——
         **已知非缺陷**（zimg 参照口径分歧，经控制组实验判定，见 `HANDOVER §D`）」；
         `:151` —— 「本环境的 `verify-color-engine-xcheck` FAIL **不是新问题**，不要试图「修绿」它 ——
         它是刻意保留的对照标记（口径分歧已定性）」
    ③ 另补一条：解析不到 `PROBE RESULT` 行（探针崩溃 / 参数错）也**判红** ——
       不给「没输出 = 没失败」式假绿。

    **豁免的 6 格**（实测失败格，逐一对齐）：`bars16.png` 与 `t2.png` 各 3 格 ——
    `BT.2020→sRGB`、`sRGB→BT.2020`、`PQ(BT.2020)→sRGB`；PASS 的 4 格
    （两图的 `sRGB→DisplayP3` / `DisplayP3→sRGB`）**不在白名单内**，坏了照常红。

    **最终退出码语义**：**除白名单外的任何一格失败 ⇒ `exit 1`**；白名单内那 6 格不判红。
    ⇒ 现状（6 格全豁免）**`exit 0`**；出现**新增**失败（新图、新用例、或这 6 格之外的任何格）⇒ `exit 1`。

    **变异验证**（证明豁免是**精确的**、不是「全豁免」）：
    ① 把白名单里 `t2.png/PQ(BT.2020)→sRGB` 一条改名 ⇒ 该格脱离豁免 ⇒ **豁免 5 格 + 判红 1 格 ⇒ exit=1**；
    ② 把白名单判断整体失效（`if ($false)`）⇒ 6 格全判红 ⇒ **豁免 0 格 ⇒ exit=1**。

    ⚠ 顺带修：`:8` 的目录清理补 `-EA SilentlyContinue`（同第 57 条 ③ 的宿主批量删除加固）。

---

59. **HDR→SDR 的传递曲线「按容器分档」是有意的（2026-09-17）—— 不要当缺陷去「统一」**

    **现状**：同一个 HDR 源输出到 8bit SDR 容器时，**原色已统一为 `bt709`（三容器一致）**，
    但**传递曲线按容器分档**：
    | 容器 | 出口原色 | 出口传递 | 出处 |
    |---|---|---|---|
    | **png / apng** | `bt709` | **`bt709`** | 传统 tonemap 链末级 `zscale=p=bt709:t=bt709`（PNG 无 `capTrc` 钳制） |
    | **jpg / jpeg / webp / tiff** | `bt709` | **`iec61966-2-1`** | `GetFormatColorCapabilities` 的 `capTrc` 恒为 `iec61966-2-1`，`tmDstT = capT ?? "bt709"` |

    **为什么不把 jpg/webp 也改成 `bt709` 曲线**（三条理由，逐条有出处）：
    1. **仓库惯例**：`NameableSupersetFor` 的 SDR 候选表把 `("bt709","iec61966-2-1")` 排在**第一位**
       （其次才是 `("bt709","bt709")`）；
    2. **容器惯例**：`FfmpegCommandBuilder.GetFormatColorCapabilities` 对 jpg/jpeg/webp/tiff 的
       `capTrc` 恒为 `iec61966-2-1`，而传统 tonemap 链的出口 `tmDstT = capT ?? "bt709"` 用的正是它；
    3. **语义必要（决定性）**：只有 `iec61966-2-1` 让出口**等价 sRGB**，才能兑现
       「未标注即 sRGB」容器的**无 ICC 直通**契约 —— `verify-decision-delivery:159` 锁的就是
       `hdr/webp` **不带 ICC**。若改用 `bt709` 曲线，webp 出口不再等价 sRGB ⇒ 被迫后置嵌一张**重复的 ICC**
       ⇒ 该断言立刻转红。
    4. **独立佐证**：`ColorSpaceRegistry` 记录 zimg 把 `t=bt709` 实现为**纯 γ2.4**、与定义式分歧
       （见 `PlanCore` 的 `zimgDivergent` 黑名单）⇒ 引擎侧更不该拿它当 SDR 标定。

    **为什么 PNG 那一格不跟着引擎走**：`verify-decision-delivery:126-128` 锁定 `hdr/png8` 必须是
    `bt709/bt709`；而引擎的 SDR 标定是 `bt709/iec61966-2-1`（sRGB 曲线）—— 两者不同，
    **让引擎接管会改变交付**。所以该格的正确结局是「**显式交回传统管线 + 如实声明出口语义**」，
    由 `ColorStrategyPlanning.PlanDelegatedHdrSdr` 落实（旧实现宣称 `bt2020/smpte2084` 而交付是
    `bt709/bt709`，正是「计划 ≠ 交付」）。

    **实测证据**（同一 HDR 源 `tests/output/validate/src_hdr.png`，16bit `rgb48be` / `bt2020` / `smpte2084`）：
    | 容器 | 引擎计划（决策行） | 交付事实（产物量测） | 原色语义 |
    |---|---|---|---|
    | `png8`（`--bit-depth 8`） | `None`，出口语义 = `bt709/bt709` | `[PNG-cICP] ColorPrimaries=BT.709, TransferCharacteristics=BT.709`；无 ICC | **bt709** |
    | `jpg8`（`--bit-depth 8`） | `Map`，ICC = `bt709/iec61966-2-1` | ICC 红原色 **x=0.640 y=0.330**（sRGB/bt709 色度） | **bt709** |
    | `webp`（auto） | `Map`，出口语义 = 容器未标注默认（sRGB）⇒ **不附 ICC** | 无 ICCP 块、无 cICP（未标注即 sRGB） | **bt709** |

    ⇒ **三容器的原色语义一致 = `bt709`**；曲线分档是**有意设计**，不是遗漏。
    若将来要连曲线也统一，须**同时**改 `verify-decision-delivery:126-128` 与 `:159` 两条锁定断言，
    并重新评估「未标注即 sRGB」的无 ICC 直通契约是否还成立。

---

60. **4 个硬件后端面板的标签/复选框「用了别人的键」——译文对、键名错（2026-09-17）—— ✅ 已修（含通用键统一 + TIFF DPI）**

    **背景**：补齐缺失键时逐行核对每个 `{ext:Loc}` 的**键名**与其**所在控件的用途**，发现 4 处错配。
    共同特征：**渲染出来是对的**（译文恰好合适）⇒ 肉眼、以及任何「键存在 + 译文非空」式门禁
    （含 `_probe-i18n-scan.ps1` 与 `ServiceProbe i18n`）**全都看不见**。危害在将来：任何人按
    「键名语义」去改译文，都会改错一个**不相干**的控件。

    **证据（结构对照法）**：AVIF 的 4 个硬件后端面板是**结构平行**的 ——
    `NvencAvifPanel`(`MainWindow.xaml:561-570`) / `QsvAvifPanel`(`:572-576`) /
    `VaapiAvifPanel`(`:578-582`) / `AmfAvifPanel`(`:584-587`)，每个 = 「预设标签 `TextBlock`
    + 预设 `ComboBox`」（QSV/VAAPI 另有一个「低功耗模式」`CheckBox`）。把 4 个面板的**同位元素**
    排成表，错配立刻显形：

    | 面板 | 预设标签（`:行`） | 低功耗复选框（`:行`） |
    |---|---|---|
    | NVENC | `svt.quality.preset`（`:562`）OK | 无（该面板是 `avif.spatial.aq`，`:569`） |
    | QSV | `svt.quality.preset`（`:573`）OK | **`nvenc.low.power`（`:575`）** 错 |
    | VAAPI | **`nvenc.compression.level`（`:579`）** 错 | **`qsv.low.power`（`:581`）** 错 |
    | AMF | **`qsv.quality`（`:585`）** 错 | 无 |

    **4 个错配键的「全仓唯一使用点」全部落在错误的面板上**（`Grep` 全仓
    `src/FfmpegGui/**/*.{xaml,axaml,cs}` 实测，每个键各只出现 1 次）：

    | 键名 | 字面含义 | 全仓唯一使用点 | 实际归属 |
    |---|---|---|---|
    | `nvenc.compression.level` | NVENC 压缩级别 | `MainWindow.xaml:579` | **VAAPI** 面板的预设标签 |
    | `nvenc.low.power` | NVENC 低功耗 | `MainWindow.xaml:575` | **QSV** 面板的低功耗复选框 |
    | `qsv.low.power` | QSV 低功耗 | `MainWindow.xaml:581` | **VAAPI** 面板的低功耗复选框 |
    | `qsv.quality` | QSV 编码质量预设 | `MainWindow.xaml:585` | **AMF** 面板的预设标签 |

    ⇒ 即「每个键都只被用一次，且**没有一次**用在其名字所指的后端上」。

    **其中两处不只是键名错，内容也错（用户可见）**：
    - `:579` VAAPI 标签渲染「压缩级别 (level)」/「Compression Level」，但它下面 `VaapiPresetCombo`
      的选项是 `vaapi.1`..`vaapi.7`（「1（最快）」…「7（最优）」，见 `MainWindow.xaml.cs:912`）
      ⇒ **标签与下拉内容语义不符**，两种语言下都错。
    - `:585` AMF 标签渲染「编码质量预设」/「Encoding Quality Preset」，下面 `AmfPresetCombo`
      是 `amf.speed`/`amf.balanced`/`amf.quality`（`MainWindow.xaml.cs:914`）⇒ 标签**语义是对的**，
      只有键名错。

    **为什么另两处肉眼看不见（两条独立的遮蔽）**：
    ① `nvenc.low.power` 与 `qsv.low.power` 的译文**逐字相同**（zh「低功耗模式 (low_power)」/
       en「Low Power Mode (low_power)」）⇒ `:575` ↔ `:581` 的**交叉错配**在两种语言下渲染成同一句话；
    ② `qsv.quality` 与 `svt.quality.preset` 的译文也相同（「编码质量预设」/「Encoding Quality Preset」），
       而 NVENC/QSV 两个**正确**的面板（`:562`/`:573`）用的正是 `svt.quality.preset`
       ⇒ `:585` 与它们并排也看不出差别。

    **第 5 处：键名 vs 位置错配（TIFF）**：`MainWindow.xaml:724` 的 `tiff.compression.none`
    （「无压缩」/「No compression」）渲染在 **TIFF DPI 数值框 `TiffDpiBox`(`:723`) 的右侧**，
    而 TIFF 的压缩下拉在 `:716-719`（`raw`/`lzw`/`deflate`/`packbits`，**本身没有 "none" 这一项**），
    其标签是 `:715` 的 `tiff.compression`。⇒ 键名说的是「压缩」，位置却在「DPI」组
    （`:720-726`，该组 2026-08-16 新增，见 `:720` 注释），且文案对 DPI 是**误导**
    （DPI=0 的含义是「不写入 DPI」，不是「无压缩」）。修的时候应**新增** `tiff.dpi.hint` 之类的键，
    而不是复用压缩键。

    **当时为何未修（历史记录）**：登记那一轮只补缺键、不动 XAML 引用。修 ①②③④ + 第 5 处要动
    `MainWindow.xaml` **5 行**，且目标键**是否已存在需先确认**：`amf.quality` 存在，但它只作为
    `MainWindow.xaml.cs:914` 的 `FillComboByLoc` **选项**键，不是标签键；`vaapi.preset` **不存在**
    （`vaapi.*` 现有键只有 `vaapi.1`..`vaapi.7` 这 7 个选项键）⇒ 改键名必须**同时**新增标签键，
    否则 UI 会开始显示裸键名（正是第 61 条要防的那类回归）。
    登记当轮**未改任何 XAML 引用**，那 5 行与本条登记时**逐字相同**。

    ---

    **✅ 修复记录（2026-09-17，两轮完成）**

    ⚠ 本条**正文里的行号是「登记当时」的行号**。修复后 4 个面板整体下移（新增了提示行与注释），
    当前行号见下面两张表 —— 正文与修复记录的行号**不要混用**。

    **第 1 轮（5 行）**：按「谁在用谁」改名，`MainWindow.xaml` 改 5 行，另新增 4 个键：

    | 原键（登记时行号） | 新键 | 依据 | 修复后行号 |
    |---|---|---|---|
    | `nvenc.compression.level`（`:579`，渲染「压缩级别 (level)」） | `vaapi.preset` | 正下方是 `VaapiPresetCombo`（选项 `vaapi.1..7`） | `:584` |
    | `qsv.quality`（`:585`） | `amf.preset` | 是 `AmfPresetCombo` 的标签 | `:592` |
    | `nvenc.low.power`（`:575`） | `qsv.low.power`（**已存在**） | 勾选框在 `QsvAvifPanel` 内 | `:580` |
    | `qsv.low.power`（`:581`） | `vaapi.low.power` | 勾选框在 `VaapiAvifPanel` 内 | `:588` |
    | `tiff.compression.none`（`:724`） | `tiff.dpi.hint` | 位置是 `TiffDpiBox` 右侧**提示**，不是压缩下拉 | `:733`（因上方 3 处 `hw.hint` 插入而**下移 9 行**） |

    **第 2 轮（通用键统一 + `hw.hint` 接线 + `tiff.dpi`）**：
    - 复查时发现仓库里**早已存在一对为本场景写好的通用键、却从未接线**：
      `hw.quality.preset`（zh「编码质量预设」/ en「Encoding Quality Preset」，`:129`）与
      `hw.hint`（zh「硬件编码器仅支持预设调节，质量由上方滑块控制」，`:130`）—— 两键**都是孤立键**，
      且 `hw.quality.preset` 的译文与第 1 轮新加的 `vaapi.preset`/`amf.preset` **逐字相同**
      ⇒ 显然当初规划过「硬件面板通用文案」但没接线。
    - 4 个面板的预设标签**统一**为 `hw.quality.preset`。理由：4 个面板的预设文案**本就相同**，
      用一个**不带后端前缀**的通用键在语义上比「各自后端前缀」更正确；而原 NVENC/QSV 面板用的
      `svt.quality.preset` **指向 SVT 后端**，正是本条要修的**同一类缺陷**。
      现为 `:562` NVENC / `:576` QSV / `:584` VAAPI / `:592` AMF。
      ⚠ **低功耗勾选框的键不动**（`qsv.low.power` / `vaapi.low.power` 各自前缀是对的，分工清晰）。
    - `hw.hint` **接线到 3 个面板**（QSV `:578` / VAAPI `:586` / AMF `:594`）。
      **NVENC 刻意不接**，两条独立理由：① 该面板有 `-aq-strength`（`:566`）与 `-spatial-aq`（`:569`）
      两个**质量相关**控件 ⇒「**仅**支持预设调节」在那里是**假话**（有代码依据：
      `FfmpegCommandBuilder.cs:409-414` 确实会为 `av1_nvenc` 下发这两个参数）；
      ② 它的提示位已被 `avif.aq.hint`（`:567`）占用。
      另已验证 `hw.hint` 后半句为真：`FfmpegCommandBuilder.cs:285-286` 对**所有** AVIF 编码器
      （含 4 个硬件后端）都下发 `-crf MapAvifCrf(options.Quality)` ⇒「质量由上方滑块控制」成立。
    - 顺带修 **`Text="DPI"` 的硬编码**（登记时 `:721`，修复后 `:730`）：新增 `tiff.dpi`
      （zh「打印分辨率 (DPI)」/ en「Print Resolution (DPI)」，与 `png.dpi` 一致）并改为 `{ext:Loc tiff.dpi}`。
      ⚠ 「DPI」**不是中文** ⇒ `_probe-cjk-hardcode-scan.ps1` 的 XAML 计数**不会**因它变化，**这条门禁抓不到它**。

    **修复后的孤立键账**（口径：定义在 `zh-CN.json`，但在 `src/FfmpegGui/**/*.{xaml,axaml,cs}` 里
    找不到 `"键"` 或 `{ext:Loc 键}` 字面引用）：**第 2 轮结束时**为总键 **578**、孤立键 **104**
    （登记时为 103）。**第 3 轮后为总键 579、孤立键 105** —— 第 3 轮新增 `nvenc.aq.strength`（在用）
    且 `svt.aq.strength` 转为孤立，净 +1（见第 63 条）。
    ⚠ 这两个数**必须能自证口径**，可复现命令（PowerShell 5.1 / 7 均可，直接粘贴）：
    ```powershell
    $root = '<repo-root>\src\FfmpegGui'
    $json = Get-Content -Raw -Encoding UTF8 "$root\Resources\Locales\zh-CN.json"
    $keys = ([regex]::Matches($json, '(?m)^\s*"([A-Za-z0-9._]+)"\s*:')) |
            ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    $corpus = (Get-ChildItem $root -Recurse -File -Include *.xaml, *.axaml, *.cs |
              Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
              ForEach-Object { Get-Content -Raw -Encoding UTF8 $_.FullName }) -join "`n"
    $orph = @($keys | Where-Object {
        -not ($corpus.Contains('"' + $_ + '"') -or $corpus.Contains('{ext:Loc ' + $_ + '}')) })
    "键 $($keys.Count) / 孤立 $($orph.Count)"
    ```
    实测输出：`键 579 / 孤立 105`。
    ⚠ **偏差方向**：本命令把「引号包住的字面量」也算引用 ⇒ 引用检测**偏宽松** ⇒ **105 是下界**
    （真实孤立数只可能 ≥ 105）；换更严的检测**只会让数变大**，原因见第 63 条 ② 的口径对照表。
    - **回收 2 个**：`hw.quality.preset`、`hw.hint`（原孤立，现已接线）。
    - **新增 3 个孤立**：`svt.quality.preset`、`vaapi.preset`、`amf.preset` —— 都因**引用被统一**而失去使用点。
      其中 `vaapi.preset`/`amf.preset` 是第 1 轮刚加的，现在**纯属废键**；`svt.quality.preset` 的译文
      与 `hw.quality.preset` **逐字相同** ⇒ 三者删掉都**不影响任何渲染**。
      ⚠ 本轮按裁定**暂不删**，建议在「孤立键门禁」那一轮一并处理。
    - `tiff.compression.none` 仍是孤立键，但它**可以正当用于** TIFF 压缩下拉（`raw` 即无压缩）⇒ 先留。

    **验收（两轮共同）**：`_probe-i18n-scan.ps1` **9/0**（缺失 0 ⇒ 无裸键名）
    —— ⚠ **该基线已变**：脚本后来补了 2 条「无重复键」断言，现在是 **11/0**（见第 63 条）；
    `_probe-cjk-hardcode-scan.ps1` **11/0**、XAML **13** / C# **1135**（**持平** —— 本轮改的是键名
    与 `DPI` 字面量，都不含中文）；两工程 **0 警告 0 错误**（改 XAML 后**已重建**）；
    `verify-ps-compat.ps1` **13/0**。
    **人眼验收**见 `docs/MANUAL_VERIFICATION.md` §9 —— 这类错配**现有门禁抓不到**
    （译文非空、键都存在、扫描器只看「键→译文」映射）。

---

61. **UI 里有一批中文「压根没走 Loc」—— 漏译 ≠ 缺键（2026-09-17）—— 翻译本身只登记不修；口径 ② 已锁为门禁**

    **现状**：`_probe-i18n-scan.ps1` 现有 **11 条 `CK` 断言**，其中能判红的**内容缺陷有 3 类**
    （2026-09-17 复核更正 —— 原文写「能判红的只有一件事」，那是补「无重复键」断言**之前**的说法）：

    | 类别 | 断言位置 | 判据 | 性质 |
    |---|---|---|---|
    | **① 缺键** | `:101` | `{ext:Loc}` / 索引器 / `FillComboByLoc` 引用到的 key 在两种语言里**都有定义**（缺失 0） | **内容缺陷** |
    | **② 键集不一致** | `:50` / `:51` | 一种语言有、另一种语言没有（多出 0 个） | **内容缺陷** |
    | **③ 重复键** | `:44` / `:45` | 同一语言文件内同一 key 出现**两次** | **内容缺陷**（2026-09-17 新增） |
    | 语料自检 ① | `:29` / `:30` | zh-CN / en-US 各解析到 **> 0** 个 key | 防假绿 |
    | 语料自检 ② | `:68` | 扫到 **> 0** 个 XAML/AXAML 文件 | 防假绿 |
    | 语料自检 ③ | `:72` | XAML 引用到 **> 0** 个 Loc key | 防假绿 |
    | 语料自检 ④ | `:83` | C# 索引器引用到 **> 0** 个 Loc key | 防假绿 |
    | 语料自检 ⑤ | `:97` | `FillComboByLoc` 引用到 **> 0** 个 Loc key | 防假绿 |

    ⇒ **3 类内容缺陷（占 5 条 `CK`）+ 6 条语料自检 = 11 条**。语料自检判红意味着**扫描器自己失效**
    （glob 或正则断了 ⇒ 假绿），**不是**内容缺陷 —— 两者不要混为一谈。
    ⚠ **「③ 重复键」补的是一个真实溜过去的缺陷**：两个写者并发把 `nvenc.aq.strength`
    插在同一位置 ⇒ 当时门禁**全绿**（键集一致、缺失为 0，两条断言都看不见重复），
    而 `System.Text.Json` 反序列化进 Dictionary 时**后者覆盖前者** ⇒ **改错时没有任何信号**。

    ⚠ **但这 3 类全部只覆盖「键 ↔ 译文」这一层**。另一类问题它**最多只打印、永不判红**
    （`_probe-i18n-scan.ps1:103-127` 的 ③④ 是 `ℹ` 行）：
    文案**根本没写 Loc，直接把中文写死在 XAML/C# 里**。
    这类文案切到 English 后**仍然是中文**，但门禁**全绿** —— 因为压根没有 key 可查，
    而且 ③④ 的覆盖范围**远小于**真实存量（见下「两个口径的关系」）。
    ⇒ 与第 42/45 条修掉的「缺键」是**两类问题**，不能靠补键解决。

    **实测口径 ①（门禁自报，权威）**：`_probe-i18n-scan.ps1` 自己**已经**在打印这两个数
    （`:88-92` 的 ③，`ℹ` 行、**只报告不判红**）：
    ```
    ℹ 硬编码中文（未走 Loc ⇒ 切英文后仍显示中文）：XAML 9 处 / C# 216 处
    ```
    其正则**逐字**如下（`-AllMatches` + `.Count` ⇒ 数的是**命中行数**，不是命中次数）：
    - XAML（`:89-90`）：**只扫 `MainWindow.xaml` 一个文件**，
      `(Text|Content|Header|ToolTip\.Tip|PlaceholderText)="[^"{]*[\u4e00-\u9fa5]`
      —— `[^"{]*` 会把「中文之前已出现 `{`（如 `{DynamicResource …}`）」的行排除；
    - C#（`:91-92`）：**只扫 `MainWindow.xaml.cs` 一个文件**，`"[^"]*[\u4e00-\u9fa5][^"]*"`
      —— **不剥注释**，注释行里的引号中文也会计入。

    **实测口径 ②（本轮全仓重扫，用于看真实规模）**：把范围放到 `src/FfmpegGui` **全部**
    `*.xaml`/`*.axaml`/`*.cs`（排除 `bin`/`obj`），并**剥掉注释**（`<!-- -->` 含跨行、`/* */`、`//` 行），
    再数「仍含 `[\u4e00-\u9fff]` 的位置」：

    | 层 | 命中 | 说明 |
    |---|---|---|
    | `src/FfmpegGui/**/*.xaml` + `*.axaml` | **13 行** | 剥掉 `<!-- -->`（含跨行）后仍含中文的行 |
    | 其中「用户可见文案」 | **9 行** | 见下表（**注意**：这 9 行的成员与口径 ① 的 9 **不同**，见下） |
    | 其中 `<sys:String>` 死内容 | 3 行 | `MainWindow.xaml:231-233`（「静态图片模式」/「动图模式」/「RAW 图片」）；`:283` 注释已说明改由 `FillComboByLoc` 填充 ⇒ 已失效，**非**缺陷 |
    | 其中语言菜单 | 1 行 | `MainWindow.xaml:89` `Header="中文"`（语言名自指，**可接受**） |
    | `src/FfmpegGui/**/*.cs` 字符串/字符字面量 | **1135 行** | 剥掉注释后，`"…"` / `@"…"` / `'…'` 中含中文的行 |
    | 其中 `[tag]` 前缀的编码日志 | 342 行 | 形如 `"[cjxl] …"`，进 `ProgressWindow` 的日志面板 + 控制台 ⇒ **也是用户可见** |
    | 其中其余 | 793 行 | 窗口标题、菜单、对话框、进度标签、预设名、EXIF 字段名、`--help` 文本… |

    ⚠ **两个口径的关系（不要当成互相矛盾）**：
    - **XAML 侧同为 9，但成员不同（巧合）**：
      口径 ① 的 9 = 8 个占位 `TextBlock`（`MainWindow.xaml:349/798/830/899/901/977/993/1032`）
      **+ 语言菜单 `:89` 的 `Header="中文"`**（`Header` 在该正则的候补里）；
      它**看不见** `<sys:String>` 元素内容（探针 `:96-104` 的 ④ 单独统计，实测 **3 处**）。
      口径 ② 的 9 = **同样的 8 个占位 `TextBlock`** + **`ProgressWindow.xaml:53`**
      （口径 ① 扫不到这个文件）。
      ⇒ 交集是那 8 个占位 `TextBlock`；**其余成员各差一个**。
    - **C# 侧 216 vs 1135 是覆盖范围差**：口径 ① **只扫 1 个文件**（`MainWindow.xaml.cs`）
      且不剥注释；口径 ② 扫**全部 `.cs`**。⇒ 口径 ① 对
      `Services/QueueProcessor.cs`(299)、`Services/ExifToolService.cs`(110)、
      `Services/ColorMapping/ColorStrategyPlanning.cs`(87)、`ProgressWindow.xaml.cs`(33)
      等**全部视而不见**。
    - **口径 ① 另有一处结构性盲区**：它只认 `Text|Content|Header|ToolTip.Tip|PlaceholderText`
      五个属性，且**只 glob `MainWindow.xaml`** ⇒ `ProgressWindow.xaml`、4 个 `.axaml` 窗口
      （`FormatFilterWindow`/`ImageDetailWindow`/`PresetManagerWindow`/`Controls/MetadataEditor`）
      **一个都没进扫描**。

    ⇒ **本条的「存量规模」以口径 ② 为准（XAML 13 / C# 1135）**；
    口径 ① 的 `9/216` 是**门禁当前打印值**，改的时候**必须同步扩宽探针 ③ 的 glob 与正则**，
    否则改完 UI 而门禁数字不动，看起来像「没改」。**两个数都要保留在案**。

    **✅ 口径 ② 已固化为门禁（2026-09-17，本轮）**：`tests/scripts/_probe-cjk-hardcode-scan.ps1`
    （已登记进 `_run-step3-gates.ps1` 的脚本清单，与其余 `_probe-*-scan.ps1` 并列）。
    - **基线**（写死在脚本里，改动口径须同步更新）：**XAML/AXAML 13 行 / C# 1203 行**
      （C# 再分两桶：`[tag]` 编码日志 **370** + 其余 **833**）。
      ⚠⚠ **2026-09-18 第五次抬基线（接手会话）：1180 → 1203 / 356 → 370 / 824 → 833（+23）** ——
      **这次与前四次性质不同，是「两笔账」，登记全文在 `_probe-cjk-hardcode-scan.ps1` 头部**：
      · **第一笔 = 恢复性 +16（tag+9/其余+7）**：该门禁**自第 31 轮抬完基线后再没被跑过**（根因 = 运行器**尾部无 `exit`** 吞掉子门禁退出码，已于 2026-09-18 修复，见第 86 条）⇒ 第 32~58 轮的合法开发累积成漂移。
        **分桶账精确闭合**（`_g4_` 快照带分桶：`1142/342/800` ⇒ 抬前实测 `1196/365/831` ⇒ delta `+54/+23/+31`；扣已登记的 raise#3 `+29(+11/+18)` 与 raise#4 `+9(+3/+6)` ⇒ 余 **+16 = tag+9/其余+7**）。**11/16 行已逐行归属**（S6/S7/R1 的 `[色彩裁决]`/`降级（`/`可替代方案：`/`[MaxDimension]` 等），**1 行不可判定**（候选池 4 个全是 `ColorStrategyPlanning.cs` 的同形降级码文案）。
      · **第二笔 = 本会话增量净 +7**：`+8`（`#15(0919)`/`#21(0919)` 的心跳守卫新增 7 行 + `QueueProcessor.cs` 1 行）`−1`（消掉 `MainWindow.xaml.cs:5570` 的行尾注释假阳性，与第 31 轮修 `RawColorPipeline.cs:593` 同处置）。
      ⚠ **「`QueueProcessor.cs` 那 +1」未能定位到确切行 ⇒ 如实标注为"未定位"**，不编。
      ⚠ **留给维护者的结构性提醒（未处置）**：本门禁的「其余」桶（现 833 行）**绝大多数是引擎/CLI 诊断串，而不是该桶描述里列的 UI 面** ⇒ 口径与描述已不匹配；一天内抬了 5 次、本会话内又漂 +7。**建议择一**：① 接受周期性抬基线；② **收紧口径**（把 `Services/ColorMapping/**` 与 `QueueProcessor` 的诊断串排除出计数）—— ⚠ 按本脚本头既有约定，收紧口径**必须同时改基线与本条**，属独立决定。
      ⚠ **可一键回退**：把脚本里四个常量改回 `13 / 1180 / 356 / 824` 即恢复本次之前的状态。
      ⚠⚠ **2026-09-18 第六次抬基线（接手会话，同日）：1203 → 1215 / 370 → 381 / 833 → 834（+12）** —— 登记在脚本头部。
      **增量几乎全在 `tag` 桶**，且**几乎全来自 `#24`**：把 10 处「同步读架空超时」修好时，**为每处补了一条"点名超时日志"**。
      ⚠⚠ **这条增量是本门禁"口径需要重定"的**决定性实证**（不是抱怨，是证据）**：
      **本会话内它红了两次，两次的原因都不是"有人塞了 UI 文案"，而是"有人在修缺陷"** ——
      `#24` 修的正是「**超时静默**」（本仓价值序：**静默 < 响亮但看不懂 < 响亮且点名**），
      而**让它响亮就必须加中文串** ⇒ **本门禁在惩罚"把静默改成响亮"**。
      ⇒ 现状：**一天内抬了 6 次**（1180 → 1203 → 1215），每次都是"修缺陷的副产品"。
      ⇒ **强烈建议按上面的口径 ② 收紧**（把 `Services/ColorMapping/**` 与 `QueueProcessor` 的诊断串排除出计数），
      否则这把锁会持续**惩罚正确的改动**，最终被训练成"**看到它红就忽略**" —— 而那**正是**第 31~58 轮漂移无人发现的成因。
      ✅ **2026-09-18 维护者裁定并已实施：②-b 起步 → ②-c 终局。**
      **判据现在是：`XAML ≤ 13` 且 `CsUi ≤ 832`**（登记全文在 `_probe-cjk-hardcode-scan.ps1` 头部；
      ⚠ 2026-09-19 第七次抬基线前为 **824**，该次逐行登记见下方）。
      · **②-b**：`[tag]` 编码日志桶与「C# 总计」**退出判据**（仍以 ℹ 打印，保留可见性）——
        该桶**整体是诊断串**，而「总计」含它 ⇒ **两者必须同时退**，否则 TAG 一涨总计仍涨。
      · **②-c（终局）**：「其余」桶再按**是否走诊断 sink** 细分 ⇒ 判据只看 **`CsUi`（UI 面）**；
        `CsDiag`（`log?.Invoke(` / `Log(` / `.Log +=` / `Trace|Debug|Console.Write*`）只作 ℹ。
        ⚠ **fail-closed**：**认不出**的形态一律算 UI ⇒ 新增一种 UI 写法**不会**被静默放过。
      · **实测**：其余 **849** ⇒ **UI 824** + 诊断 **25**；⇒ **判据基线 824 比旧口径的 849 更紧（净收紧 25）**。
      · **为什么不采用「按文件排除」那种 ②-a**：`QueueProcessor.cs` 的 `item.Log` 串**是显示给用户的** ⇒
        按文件排除会**漏掉真 UI 文案**；**按 sink 分才是真判据**（按文件只是代理判据）。
      · **变异验证（本轮实测，已回滚）**：往 `src/FfmpegGui` 注入 1 行真 UI 硬编码 ⇒ `UI 面 825 > 824` ⇒
        **转红 `PASS=13 FAIL=1`**；删除注入文件 ⇒ **`PASS=14 FAIL=0`**。
      · **新增三条反控**：① 判据段**不得**再含 `$m.Cs`/`$m.CsTag` 的比较（防旧口径被静默塞回）；
        ② 判据段**必须**含 XAML 与 `$m.CsUi` 各 1 条（防判据被清空成恒绿）；
        ③ ②-c 负控：诊断 sink **3 条不算 UI**、UI 写法 **2 条必须算 UI**（判据按 sink 分，不按文件/桶猜）。
      ⚠ **可一键回退**：判据段改回四条 `CK` + 基线改回 `13 / 1215 / 381 / 834`（**不推荐**）。
      ⚠⚠ **2026-09-19 第七次抬基线（P1-E CLI 严格化轮）：`CsUi` 824 → 832（判据项净 +8）；
      ℹ 桶一并按实测对齐：`CS` 1215 → 1258 / `CS_TAG` 381 → 401 / `CS_OTHER` 849 → 857。**
      登记全文在 `_probe-cjk-hardcode-scan.ps1` 头部。要点：
      · **增量全部在 `src/FfmpegGui/CliParser.cs`，没有第二个文件**；**+10 新增行 − 2 消失行 = +8**：
        新增 `:592` `支持 auto | engine | legacy`（`--color-engine` 白名单）、`:607` `支持 off | on`、
        `:652` `支持 srgb | bt2020`、`:685` `支持 None | CarryIcc | BakeToStandard | BakeOnly`（`--icc-mode`）、
        `:694` `支持 recommended | carry | cicp-only | manual（别名 …）`（`--color-strategy`）、
        `:756` `BadValue` 统一模板 `--{key} 取值无效: '{value}'（{expected}）`、
        `:762` `需要整数` / `:770` `:778` `需要数值` / `:785` `需要 true | false`（`*Strict` 家族）。
        消失的两行是原先**内联**的 `--color-gamut-map 取值无效: …（支持 off | on）` 与
        `--jpeg-gain-map-base-gamut 取值无效: …（支持 srgb | bt2020）`，已被 `:756` 的统一模板取代。
      · **取证法（可复现，且不依赖任何源码快照）**：把候选中文串编成 **UTF-16LE 字节**，在**三个不同时刻的
        `FfmpegGui.dll`** 上做**存在性检索** —— `tests/output/archive/runner-pre-fix-10.40.13/`（10:40:13）、
        `tests/GainMapTestHost/bin/Release/net11.0/win-x64/`（11:48:53 = 本会话改动前）、
        `src/FfmpegGui/bin/Release/net11.0/win-x64/`（16:54:22 = 当前）。
        配合 11:21 的历史读数（`tests/output/cjk_fix_ps7.log`：`UI 824 / TAG 401 / 其余 849 / 总 1250`）
        把增量窗口夹定为 **11:21 → 16:54**，再用 mtime 证明窗口内 `src/FfmpegGui` 只有 7 个文件被写过。
      · **处置理由**：与既有同类 —— `CliParser.cs:162` `未知编码器: {v}`、`:425` `未找到匹配的输入文件…`、
        `:470` / `:475` 的外部工具缺失提示**本就在 UI 桶里**且已被 824 吸收；新增 10 行是同一类
        （CLI 解析期的「取值域提示 / 失败点名」）。
      · ⚠ **为什么不改 ASCII**：`取值无效` 被**两条别的门禁的断言直接匹配**
        （`verify-gamut-map.ps1:143`、`verify-gainmap-engine.ps1:262`），且本条 `:1828` 同样点名它
        ⇒ 改 ASCII 需同步改 3 处，且会让同一文件内出现中英混排。
      · ⚠ `CS_TAG` 381 → 401（+20）属**更早会话的未登记漂移**（该桶自 ②-b 起已退出判据、只作 ℹ），
        本次一并按实测对齐；如需逐行归因，可用同一取证法重做。
      · ⚠ 余量 0 ⇒ 已被精确吸收；后续若再涨仍会判红。
      ⚠ **上表 `1135 / 793` 是 2026-09-17 首测值；基线此后被四次「与既有同类 ⇒ 允许抬高」抬过**，
      每次都在脚本里登记了**确切行号 + 理由**（不静默抬高）：
        · **1135 → 1137 / 793 → 795（+2）**：AVIF 接入引擎轮 —— `ImageEncoderArgs.cs:487-488`
          的 libsvtav1 色度子采样拒绝理由串（与同文件 `:479` 的 libwebp 同类）。
        · **1137 → 1142 / 795 → 800（+5）**：JXL 接入引擎轮 —— `RawColorPipeline.cs:74-76`
          （jxl + AttachIcc 的出口拒绝理由串）+ `ImageEncoderArgs.cs:640-641`
          （JXL 的 JPEG 无损重封装拒绝理由串）。两类都在 `ColorMapping/*.cs`（该目录 0 处 `loc[...]`），
          与周边 20+ 条引擎侧诊断串同类 ⇒ 只给它们改走 Loc 会与邻居口径不一致。
        · **1142 → 1171 / 800 → 818 / tag 342 → 353（+29）**：cjxl 执行出口轮（同日第三轮）——
          全部集中在 `RawColorPipeline.cs` 的新出口 `EncodeJxlViaCjxlAsync` / `PipeToEncoderAsync`，
          是「任何一环失败都显式说出原因、绝不静默回退」这条硬要求的**文字载体**：
          `tag` 桶 +11（`:410 :454 :455 :456 :475 :506 :516 :529 :530 :548 :549` 的 `[色彩] …` 日志）、
          其余桶 +18（`:180 :335 :341-342 :374-377 :402-404 :411 :457 :465 :476-477 :545 :551`
          的异常消息与其续行片段）。**单轮最大一次抬升**，故逐条列出。
          ⚠ 这些串同时是 `verify-color-wiring.ps1` (10) 段与三条变异验证的**匹配关键词**
          ⇒ 改成英文或走 Loc 会让那批断言的**口径一起失效**（不是"顺手改善"）。
        · **1171 → 1180 / 818 → 824 / tag 353 → 356（+9）**：JXR 执行出口轮（同日第四轮）——
          全部在 `RawColorPipeline.cs` 的新出口 `EncodeJxrViaJxrEncAppAsync`，与兄弟出口 cjxl **逐条对位**
          （`tag` +3：`:579` ffmpeg 侧命令行 / `:590` `JxrEncApp 命令行:` / `:594` 出口形态；
          其余 +6：`:528 :535-536 :540 :583 :595`）。
          ⚠ 抬升前**先试过并入既有行**：本轮只消掉一处**计数假阳性**——`:593` 的**行尾注释**里写了
          `"编码失败 (exit …)"`（带直引号）；本门禁只剥块注释、只跳过整行 `//`，**行尾注释不剥**
          ⇒ 注释里的引号串被当成文案计数。已把该注释的直引号改成 `「」`，该行不再计数。
          余下 9 行逐条都是「出口显式失败 / 可追溯」的判据载体，且其中两条正是
          `verify-color-wiring.ps1` (11) 段的**匹配关键词** ⇒ 改英文或走 Loc 会让 (11) 段断言一起失效。
      ⇒ 读本节时**以脚本里的基线为准**，上表只作历史测量记录。
    - **判据是「不得增加」，不是「必须为 0」**：断言 `实测 <= 基线`；**降了也不判红**
      （正向改进不惩罚），但会打印「距基线还有 N 的余量」，余量长期为正时应主动收紧基线。
      理由同第 55 条：存量清零是大工程，若断言「必须为 0」会立刻长期红。
    - **该脚本自身带负控（有牙）**，不需要人工构造：临时在 `$env:TEMP` 的 GUID 目录里造
      `.cs` / `.xaml` 语料，断言「含 2 行中文 ⇒ 恰为 2」「注释里的中文**不得**计入」
      「空语料 ⇒ 0/0」。⇒ 它不可能像第 50/58 条那样「恒绿」。
    - **人工变异验证**（本轮实测，均已回滚且字节级还原）：往 `.cs` 加 1 行硬编码中文 ⇒
      C# **1136** > 1135 ⇒ `FAIL`（PASS=9 FAIL=2，exit 1）；往 `.xaml` 加 1 行 ⇒ XAML **14** > 13 ⇒
      `FAIL`（PASS=10 FAIL=1，exit 1）；把一条日志的 `[cjpegli]` 前缀去掉（总数仍 1135，
      只把 1 行从 `[tag]` 桶挪到其余桶）⇒ 其余 **794** > 793 ⇒ `FAIL`（PASS=10 FAIL=1，exit 1）
      —— 最后一条证明**分桶断言是独立活的**，不是摆设。
    - 同时把 `_probe-i18n-scan.ps1` ③ 的 `ℹ` 行措辞改为「口径①（窄，只报告不判红）……
      权威口径②见 `_probe-cjk-hardcode-scan.ps1`」，**计数逻辑未动**。

    **XAML 侧口径 ② 的那 9 行（用户可见）**：

    | 位置 | 控件 | 写死的内容 |
    |---|---|---|
    | `MainWindow.xaml:349` | `QualityValue` | `质量: 75` |
    | `MainWindow.xaml:798` | `FileCountLabel` | `总文件: 0` |
    | `MainWindow.xaml:830` | `QueueProgressLabel` | `队列: 0 / 0` |
    | `MainWindow.xaml:899` | `ConcurrencyLabel` | `(同时 2 个任务)` |
    | `MainWindow.xaml:901` | `QueueCountLabel` | `队列: 0` |
    | `MainWindow.xaml:977` | `SimpleProgressLabel` | `队列: 0/0` |
    | `MainWindow.xaml:993` | （无名 `TextBlock`） | `0 项` |
    | `MainWindow.xaml:1032` | （无名 `TextBlock`） | `0 个文件` |
    | `ProgressWindow.xaml:53` | （无名 `TextBlock`） | `执行日志`（带 📜） |

    前 6 个是**占位文本**：`x:Name` 都在 code-behind 里 `FindControl` 到
    （`MainWindow.xaml.cs:311`/`:342`/`:482`/`:485`/`:811`/`:947`）并在运行时覆写 `.Text`
    ⇒ 只在**启动瞬间**闪现中文。**但覆写用的也全是写死的中文**
    （`MainWindow.xaml.cs:1319`/`:1733`/`:1735`/`:1745`/`:1794`/`:4640`，如
    `$"队列: {_queueView.Count} 项"`）⇒ 切到 English 后**依旧是中文** —— 这正是本条要登记的主体。
    `ProgressWindow.xaml:53` 的「📜 执行日志」是**永久**写死的标题，无人覆写。

    **C# 侧最集中的几处**（按文件，含 `[tag]` 日志）：`Services/QueueProcessor.cs` **299**（其中
    193 条 `[tag]` 日志）、`MainWindow.xaml.cs` **205**、`Services/ExifToolService.cs` **110**
    （EXIF 字段名表，如「标题」「作者」「相机制造商」…）、`Services/ColorMapping/ColorStrategyPlanning.cs` **87**、
    `Services/ColorMapping/ColorTransformPlan.cs` **35**、`Services/PresetManagerService.cs` **34**
    （预设名，如「📸 JPEG LI — 高质量」）、`ProgressWindow.xaml.cs` **33**、`Services/ColorMapping/RawColorPipeline.cs` **33**。

    **还有第三种形态（顺带记录）**：`Services/LocalizationService.cs:208-296` 的
    `LoadEmbeddedFallback()` 里另有一张 **80+ 条英文硬编码字典**（`app.title`/`format.jpeg`/…），
    它是「外部 json 与内嵌资源全缺失」时的最后兜底 ⇒ **只有英文、没有中文**，
    且与两个 json **无任何一致性校验**（加键时极易漏改，静默降级）。

    **为什么本轮不修**：1135 + 13 行的改造量远超「补缺键」的范围，且**不能只做翻译搬运**——
    至少要先定三件事：
    ① 分层口径：哪些是**产品文案**（该进 Loc）、哪些是**诊断日志**（可只走英文或保留中文，
       但必须写成明确规则，不能默认「日志就该是中文」）；
    ② `MainWindow.xaml.cs` 里的**格式串**（`$"队列: {n} 项"`）需要拆成「模板 key + 参数」，
       而 `LocalizationService` 目前**只有 `this[string key]` 索引器，没有格式化重载**
       （见 `LocalizationService.cs:52-60`）⇒ 要先扩 API；
    ③ 语言**运行时切换**时这些写死文案不会跟着变：`RefreshVersion`（`LocalizationService.cs:44-49`）
       只递增并抛 `PropertyChanged`，只能驱动 XAML 绑定，**不会重跑 code-behind 里的一次性 `.Text` 赋值**
       ⇒ 需要一份「切换语言后必须重新赋值的文案」清单（或改为绑定）。
    **另需同步做**：扩宽探针 ③（`_probe-i18n-scan.ps1:103-127`）的 glob 到全部 `.xaml`/`.axaml`/`.cs`
    并改为剥注释，否则改完 UI 门禁数字不动（见上面「两个口径的关系」）。
    ⇒ 独立立项，本轮**只登记**。

---

62. **`verify-colorfmt-matrix` 是「表格型 / 无断言」脚本，不是硬门禁（2026-09-17 定性登记）**

    **背景**：它与 `verify-color-engine-xcheck` 同族 —— 都在运行器 `_run-step3-gates.ps1` 的清单里，
    但**结构上无法失败**。xcheck 那半已修（补 `exit 1` + 白名单，见第 58 条）；这一半的定性是
    **「它本来就不该有断言」**，因此**不补断言**，改为**显式标注**。

    **定性依据（五条，全部指向「诊断工具」而非「门禁」）**：
    1. **自述**：头部注释写「可用性**矩阵**：ICC / CICP **到底有没有进产物**」，且
       「判据全部来自第三方工具（exiftool / ffprobe / jxlinfo），**不用产品自己的解析器自证**」
       ⇒ 定位是**测量/取证**。
    2. **无汇总契约**：全文没有 `PASS=` / `FAIL=` / 计数 / 用于断言的 `exit`；`exit 1` 只出现在
       「缺 `exe`/`ffmpeg`/`ffprobe`/`exiftool`」（`:20`）与「源生成失败」（`:38`）两处
       —— 都是**前置条件**，不是断言。
    3. **最像断言的那条也不判红**：`❌ 产物无任何 P3 描述（会被按 sRGB 误读）` 只写进表格的
       「判定」列，**不改变退出码**。
    4. **它的判定含需要人判断的中间态**：`⚠ 有 ICC 但非 P3 参照` —— 独立生成的**合法** P3 ICC
       未必与参照 ICC 逐字节相同 ⇒ 硬断言会**误伤**（与 xcheck 的 6 格白名单属同一类问题）。
    5. **既有用法就是「当仪器」**：`docs/archive/AGENT_TASK_SPLIT.md:48` 用它量到的 `584B`
       去交叉印证 `verify-color-engine-xcheck` 的失败项 ⇒ 它的产出是**第三方读数**，不是判红结论。

    **处置（二选一里的「诊断」分支）**：
    - **运行器显式标注**：`_run-step3-gates.ps1` 增 `$tableOnly = @('verify-colorfmt-matrix.ps1')`，
      命中时在该行末尾打 `← 表格型：无 PASS/FAIL 契约（exit=0 仅表示跑完）`。
      **理由**：源码里的注释在**运行时看不见**，而误读恰恰发生在读运行器输出时
      （`[verify-colorfmt-matrix.ps1] exit=0` 会被当成「门禁绿」）。
    - **`TESTING.md §3.4` 表**：新增该脚本一行，「关键断言方式」栏写明
      **「表格型：无 PASS/FAIL 契约」**（此前该表**没有它的行**，等于默认它与别的 `verify-*.ps1` 同类）。
    - **不硬造断言**：若将来要升级成硬门禁，需先拍板判据与素材 —— 至少要有
      ① 一份**受控的**参照 P3 ICC（色度已核）；② 「ICC 色度在容差内 = P3」而非「逐字节相同」的判据
      （否则第三方 ICC 版本差异会误伤）；③ 六格式各自的**已知非缺陷**清单。

    **验证（本轮实测）**：受限清单副本（只留 `verify-ps-compat.ps1` + `verify-colorfmt-matrix.ps1`）
    实跑运行器，输出中前者**无**标记、后者**带** `← 表格型…` 标记（见本轮报告）。

    **⚠ 2026-09-19 补：`$tableOnly` 的判定口径 + 全量 48 条 A/B/C 分类（现只标 1 条 ⇒ 漏标 3 条）**

    **口径（已裁定，**定稿版**）**：
    > **`$tableOnly`（脚本标签）⟺ 该脚本没有「断言级」非零退出路径。**
    > ⚠ **仅用于前置条件（缺 exe / 素材 / 输入）的 `exit 1` 不算断言通道** —— 它只说明「**没跑起来**」，不说明「跑起来后全过」。
    > ⚠ **`PASS=n FAIL=m` 汇总行与 `$tableOnly` 的判定无关** —— 理由见下条「不对称」。

    **⚠⚠ 记账通道不对称（本条最容易搞错的地方）**：
    - **探针循环**（`foreach ($m in $probeList)`；判据原文 `} elseif (($rc -ne 0) -or ($sum -match 'fail=[1-9]')) {`）⇒ **两条通道**：退出码 **或** 汇总里的非零 `fail`。
    - **脚本循环**（`foreach ($s in $scriptList)`；判据原文 `} elseif ($rc2 -ne 0) {`）⇒ **只有一条「断言级」通道：退出码**。`$sum` 在该循环里**只用于回显**。
      ⚠ 该循环另有 `if ($r2.TimedOut) { … }` 分支（`#22(0919)` 加的超时击杀），但那是**进程级**失败、**不反映断言** ⇒ 不计入「断言通道」（该分支的 tag 逐字原文、以及它**会进 `$failedItems`** 这一点，见下「超时击杀 · 第三条 tag」）。
    ⇒ **对脚本，`PASS=n FAIL=m` 是「纯显示」，不参与记账。** 把「两通道」框架套到脚本上会算错：
      按**计数**通道复算 ⇒ `$tableOnly` 得 **10**；按**退出码**通道才是 **4**。
    ⚠ **行号口径（2026-09-19 推广为「指纹锚定」）**：本条**不引任何运行器行号**（两条判据的原文已逐字引在上面）⇒ **天然免疫漂移**；
      本条出现的行号（`:22` / `:40` / `:31` / `:35` / `:25` / `:23` / `:38` / `:27` 等）**全是脚本自身的**，与本运行器无关。
      本节其余处引用的**运行器**行号，一律锚在快照 `SHA256=C999C35BF6C49506…` / 57518 B / mtime `2026-09-19 12:15:44` / 671 行上，**指纹一变即作废**。

    **全量 53 条分类（下表逐条判定的是 2026-09-19 的 **48 条**快照（含当日 P4-A 接线 +6）；其后 +5 ⇒ 现 53 条：2026-09-20 `verify-gainmap-isobmff.ps1` → 49、2026-09-22 `verify-engine-alpha-preserve.ps1` → 50、2026-09-22 `_probe-tool-detect.ps1` → 51、2026-09-23 `verify-startup-warmup.ps1` → 52、2026-09-23 `verify-ipc-extra-options.ps1` → 53 ⇒ 这 5 条不在下表内，归属均为 C 类（各有自身计数汇总 + 断言驱动退出码）**：
    - **A 类 · 无契约 = 4 条（应进 `$tableOnly`）**：`verify-colorfmt-matrix.ps1`（非零 `exit 1` 只在 `:22` 缺 exe / `:40` 生成源失败 ⇒ 前置条件）· `verify-tiff-icc.ps1`（全文**无 `exit` token**）· `verify-webp-hdr-fix.ps1`（全文**无 `exit` token**，`:35` 只打印 `[EXIT] $($p.ExitCode)`）· `_probe-jpg-p3-icc.ps1`（非零 `exit 1` 只在 `:31`「缺输入且自备失败」⇒ 前置条件）。
      ⇒ **运行器打的标签（逐字原文，含前导 2 空格）**：`  ← 表格型：无 PASS/FAIL 契约（exit=0 仅表示跑完）`
      —— 由 `$tableOnly` 数组决定（**2026-09-19 实测 4 条，与本类逐条一致**）；
      ⚠ **日志里的出现顺序 ≠ 数组声明序** —— 它按 **`$scriptList` 执行序**（实测该轮为 `verify-tiff-icc` → `verify-webp-hdr-fix` → `_probe-jpg-p3-icc` → `verify-colorfmt-matrix`），
      而 `$tableOnly` 的声明序是 `verify-colorfmt-matrix` 打头 ⇒ **两者不同，别拿顺序当判据，只拿集合**。
      ⚠ **`_probe-jpg-p3-icc.ps1` 是最易漏的一条**：它输出里有 `PROBE RESULT: pass=1 fail=0`，但那是 `:25` 调**子进程** `ServiceProbe png3` 打出来的，**不是脚本自身的计数汇总** ⇒ 不得据此把它划出 A 类。
    - **B 类 · 有断言退出码、无自身计数汇总 = 7 条（不得进 `$tableOnly`）**：`_probe-cancel-propagation-scan.ps1:100` · `_probe-ct-chain-closure-scan.ps1:420` · `_probe-proc-encoding-scan.ps1:103` · `_probe-stderr-drain-scan.ps1:46/:49` · `_probe-sync-read-before-wait-scan.ps1:407` · `run-pipeline-tests.ps1:425` · `verify-color-engine-xcheck.ps1:104`。
      ⇒ **运行器打的标签（逐字原文，含前导 2 空格）**：`  ← 有退出码契约（exit=0 即无失败）；无自身计数汇总`
      —— 由 **`$noSelfSummary`** 数组决定（2026-09-19 新增；**实测 7 条，与本类逐条一致**）。
      ⚠ **该数组与 `$tableOnly` 互斥**（team-lead 实测**交集 = 0**）；分支顺序 = `TimedOut` → `$tableOnly` → `$noSelfSummary`（**三条 tag 见下**）
      ⇒ 同一条脚本**至多命中一个 tag**（读日志时不会出现两个标签同时挂在一行）。
      ⚠ 把后两条标成「`exit=0` 仅表示跑完」，等于**抹掉 §6 第 58 条刚补上的退出码语义** ⇒ 制造**反向假绿**（这两条正是第 58 条的**正主**）。
    - **超时击杀 · 第三条 tag（`#22(0919)` 加，同属上面那条 `elseif` 链；**不是**第四类）**：判据 `$r.TimedOut` / `$r2.TimedOut` ⇒ 逐字原文（含前导 2 空格）`  ⚠超时击杀（未完成）`
      ⚠ **与 A/B 语义正交**：A/B 是「**跑完了**，只是没有断言契约」，本条是「**根本没跑完**」⇒ **看到它一律当红**；且它**确实进 `$failedItems`**（`probe:$m (超时 $($ProbeTimeoutSec)s，已击杀进程树)` / `script:$s (超时 $($ScriptTimeoutSec)s，已击杀进程树)`）⇒ 整轮红，**不是只打个标签**。
      ⇒ **真实日志实证（可 grep）**：`tests/output/_runner_v14_timeout_ps51.log`（**已静止**：851 B / 10 行 / mtime `12:16:56`，含汇总行）——
      `:4` = `[runner    ] exit=-1  （超时击杀，汇总解析已抑制）  1.9s  ⚠超时击杀（未完成）`（**末尾恰好 2 空格**）；
      `:8` = `===== 运行器汇总（-SkipScripts，仅探针）：失败 1 项 =====`、`:9` = `    probe:runner (超时 1s，已击杀进程树)`
      ⇒ **上面两句话（tag 逐字 / 确实进 `$failedItems` ⇒ 整轮红）由同一份日志同时坐实。**
      ⚠ **两个超时阈值 = 受管不变量（勿调参）**：`[int]$ProbeTimeoutSec = 120,`（**探针段**，`Invoke-StepWithTimeout … -TimeoutSec $ProbeTimeoutSec`）/ `[int]$ScriptTimeoutSec = 480)`（**脚本段**，同参数传 `$ScriptTimeoutSec`）。
      —— 二者被运行器**自身形态自检逐字钉住**：自检输出 `shape-selfcheck=OK（14 针：… / 探针超时默认值 / 脚本超时默认值 / …）` 中**第 11 针 = 探针超时默认值**、**第 12 针 = 脚本超时默认值**；源码定义处注释亦写明「探针段 = **第 11 针**、脚本段 = **第 12 针**」，并点明动机是「防按各自基准悄悄调参」（正是「60 vs 120」往返多轮的根因）；自检失败文案为 `探针超时默认值针 = …（须 1：默认值必须逐字钉住，防按各自基准悄悄调参）`。
      ⇒ 改这两个数**必须同步改自检针**，否则自检失败 ⇒ 运行器**拒绝跑**（`[SHAPE]` ⇒ `exit 2`，**不产出读数**）；它们**只决定「等多久判死」**，与断言判据正交。
    - **C 类 · 有自身计数汇总 = 42 条**（2026-09-23 第五次接线后实测；= 清单 53 − `$tableOnly` 4 − `$noSelfSummary` 7），且**42/42 都有断言驱动的非零退出**（`if ($fail -gt 0) { exit 1 }` / `exit $(if ($fail -eq 0) { 0 } else { 1 })` / `if ($fail -ne 0) { exit 1 }`）⇒ **无「计数显示红、退出码却恒 0」的假绿**。
      ⚠ **2026-09-19 P4-A 接线 +6（31 → 37）** —— 新增 6 条**逐条归属 C 类**：各自**有自身计数汇总**（`PASS=n FAIL=m`）**与退出码契约**（显式 `exit 1` / `else exit 0`）⇒ **都不进** `$tableOnly` / `$noSelfSummary`（那两个数组的条数锁 **4 / 7 不变**）。下表 6 行 = **本轮实跑读数**：

      | # | 脚本（= 清单第 43~48 条） | 自身计数汇总行（实测） | 退出码 | 时长 |
      |---|---|---|---|---|
      | 43 | `verify-ui-host.ps1` | `UIHOST RESULT: pass=302 fail=0` | 0 | 6.4 s |
      | 44 | `verify-ui-param-matrix.ps1` | `UIPARAM RESULT: pass=302 fail=0` | 0 | 6.3 s |
      | 45 | `verify-ui-strategy-map.ps1` | `UISTRAT RESULT: pass=302 fail=0` | 0 | 6.1 s |
      | 46 | `verify-ui-param-defects.ps1` | `UIPARAMDEF RESULT: pass=302 fail=0` | 0 | 6.3 s |
      | 47 | `verify-cli-strict.ps1` | `CLISTRICT RESULT: pass=302 fail=0` | 0 | 6.0 s |
      | 48 | `verify-png-structure.ps1` | `PNGSTRUCT RESULT: pass=32 fail=0` | 0 | 1.4 s |

      ⇒ **6 条合计 32.4 s**（对整轮 ~950 s 是 **+3.4%**）⇒ 时长预算无碍。⚠ 前 5 条都跑同一个 `tests/UiTestHost.exe`（各跑一遍**全量**宿主，只在锚点断言上分工）⇒ 有**受控冗余**（换来的是「某个分组被静默删掉」能被各自锚点抓住）。
      ⚠ `verify-cli-strict.ps1` **有意放清单末位**：它含「共享语料目录里不得残留 `_*.png`」的验收，末位才能把**前面所有脚本**的残留都纳入观测。
      ⚠⚠ **一并如实登记的覆盖缺口（未处置，另议）**：① **`tests/UiTestHost` 是第三个项目**，而运行器的新鲜度（STALE）闸**只覆盖 `src/FfmpegGui` + `tests/ServiceProbe`** ⇒ 只重建那两者时，前 5 条会拿**陈旧的 `UiTestHost.exe`** 跑出「绿」；当前靠**手工**保证三者同批重建。② **`verify-oracle-matrix.ps1`**（本轮新建，5010 条组合用例 + `_lib-oracle.ps1` 独立裁判，实测 **1.6~2.0 s/用例 ⇒ 全量约 2.5 h**）**有意不入清单**（远超 `ScriptTimeoutSec 480`），按**手工长跑**执行：`-Layer L1` ≈ 6 min、`-Layer L2` ≈ 5 min、`-Smoke` ≈ 8 s。
      ⚠ **【2026-09-24 更新：上面 ① 已处置，② 不变】**STALE 闸由 2 对扩到 **4 对** —— `UiTestHost.exe`（5 条 UI 门禁执行的产物）与被清单内 28 条端到端门禁执行的 `src/**/FfmpegGui.exe`（AST 实测）都已进闸 ⇒ 缺口 ① 闭环，取证与变异验证见本节第 106 条；`verify-oracle-matrix.ps1` **仍是有意的**手工长跑件（未接入清单，本轮未改）。
    - **合计 4 + 7 + 46 = 57** ✓（与运行器清单条数一致；2026-09-27 第九次接线 `verify-simd-switch-fallback.ps1` 归 **C 类** ⇒ `4 + 7` 不动、`45 → 46`）；2026-09-24 第六次接线把 `_probe-settings-clone-coverage.ps1` 并入 **C 类** ⇒ `42 → 43`；2026-09-27 第七次接线把 `_probe-matrix-structure.ps1` 并入 **C 类** ⇒ `43 → 44`；同日第八次接线把 `_probe-stderr-merge-scan.ps1` 并入 **C 类** ⇒ `44 → 45`；三次三个标签数组都不动 ⇒ 第 13/14 针仍是 4 / 7）
      ⚠ **本表条数受 `$expectedScriptCount` 硬保护**（`_run-step3-gates.ps1` 里 `$expectedScriptCount = 54` 与 `@($scriptList).Count` 比对，
      不等即打 `[FAIL] 脚本清单条数 = …，受管基线 = … ⇒ 基线漂移（增删条目必须同步四处文档）` **且**进 `$failedItems` ⇒ 整轮红）。
      ⇒ **双向闭环**：**增删脚本** ⇒ 本表的 `4 / 7 / 42` 分解必须与那个常量**一起改**；**反之运行器报「基线漂移」⇒ 就是来找本表**。
      ⚠ 此处**只引符号名不引行号**（常量名 / 变量名对漂移免疫；行号口径见本节上文「锚在运行器快照」那段）。

    **形态描述（5 类，**仅供人读**，不用于 `$tableOnly` 判定）**：`_probe-*-scan` 诊断型 ×5 · 表格型 ×1（`verify-colorfmt-matrix`）· `===== DONE =====` ×2（`verify-tiff-icc` / `verify-webp-hdr-fix`）· 自有汇总口径 ×1（`run-pipeline-tests`，`通过: N 失败: M`）· 自有总结口径 ×1（`verify-color-engine-xcheck`，`===== xcheck 总结 =====`）。
    ⚠ 其中**末行以 `PASS ` 开头却无 `=n FAIL=m`** 的两条（`_probe-proc-encoding-scan` / `_probe-stderr-drain-scan`）人读运行器日志时**极易当成绿** —— 但它们属 B 类（有退出码契约），故**不**进 `$tableOnly`，而是另立标签 **`$noSelfSummary`** ⇒ 日志里会带 `  ← 有退出码契约（exit=0 即无失败）；无自身计数汇总`（**这条 tag 本身就是「别把它当绿」的提示**）。

    **判据实现（可复现）**：
    ```powershell
    $lines = Get-Content "tests/scripts/$n"
    $code  = ($lines | Where-Object { $_.Trim() -notmatch '^#' }) -join "`n"   # 剔整行注释，避免注释里的 exit 被误当通道
    $hasCount = $code -match '(?i)\bpass\s*=\s*\d+'                            # 自身计数汇总（仅用于分 B/C，与 $tableOnly 判定无关）
    # 「断言级非零退出路径」必须**人工判**：纯正则分不出「前置条件 exit」与「断言 exit」——
    # 机械判定会给 B=9，人工剔除 verify-colorfmt-matrix / _probe-jpg-p3-icc 两处「前置条件 exit」后才得 A=4 / B=7。
    ```

    **⚠ 同批登记（不在本条范围内，另开任务，**只登记不派工**）**：
    - **6 条「前置条件 `exit 0`」脚本**（缺依赖/造素材失败时也 `exit 0` ⇒ **运行器完全看不到**，属第 68 条同族）：`verify-color-token-normalize.ps1:42/:43` · `verify-gainmap-memory.ps1:43/:44/:63` · `verify-hlg-route.ps1:67/:68` · `verify-metadata-privacy.ps1:36/:37/:46` · `verify-png-signature.ps1:41/:42/:57` · `verify-gif-avif-framelist.ps1:46`。
    - **`_probe-jpg-p3-icc.ps1` 不检查子进程退出码**（`:23`/`:25` 用 `Start-Process … | Out-Null`，无失败判定；`:38` 只是**打印**）⇒ 子进程失败只在「没产出」时被 `:27` 的 `Test-Path $in` 兜住。属诊断型脚本，风险低。
    - **运行器 `$sum` 正则的「碰运气命中」**：`run-pipeline-tests.ps1` 的真正汇总行 `通过: 80 失败: 0`（`:413`）**不在** `$sum` 正则（`pass=|PASS=|^PASS |^FAIL |=====|合计|RESULT|SKIP|声明：`）里；它能显示出来纯属 `产物在 tests/output/**results**/` 的 `results` **意外命中 `RESULT`**（大小写不敏感）⇒ 改一句文案就变空白。⇒ 结论：**「日志里出现了某行」≠「某行被正确解析」**。本次**不动**该正则（风险高于 `$tableOnly`）。

    **口径补充（四态 / SKIP）**：

    - **四态口径（原文）**：① `FAIL>0` ⇒ **真红**；② `PASS>0 且 FAIL=0` ⇒ **真绿**；③ `PASS=0 且 FAIL=0`（空断言）⇒
      **不得计入 PASS 总数**；④ `SKIP>0` ⇒ **部分未评估，必须单列**。
      ⚠ **「不在非零退出列表」≠ 绿，只等于「无 FAIL」** —— 标记缺失时读者**没有任何线索**。
    - **本轮 SKIP 实测 4 条**（不是 5）：`verify-color-caps.ps1:33`（heic/heif ICC）· `verify-color-caps.ps1:34`（jxr）·
      `verify-color-tonemap.ps1:29`（BT.2390）· `verify-geometry-engine.ps1:64`（`r1-s2-shape`，`SKIP=1`）。
      ⚠ 前 3 条**同时有契约**（`caps 12/0` · `tonemap 15/0` · `geometry-engine 23/0`）⇒ 属第 ④ 类「**部分未评估**」，
      **必须单列、不得并进 PASS 总数**。⚠ 探针 `[geometry]` 行的 `SKIP=1` 与脚本 `:64` 是**同一项**，**不得重复计数**。
      ⚠ 三个**非独立**候选（不得计入）：`verify-color-tonemap.ps1:28` 的 `=== SKIP（未实现，不给绿灯）===` 是**表头**
      （与 `:29` 同节）· `verify-gif-avif-framelist.ps1:27` 的 `SKIP=0` 是**显式零** ·
      早前快照里 `verify-decision-delivery` 的 1 条 SKIP 本轮已**消失**（实测 `pass=51 fail=0`）。
    - **关联**：本条是第 76 条 ②「跳过退化」与第 81 条在**运行器层面**的同类形态 —— 那两条讲**脚本自己**不把 SKIP 进汇总；
      本条讲**运行器**不把「整脚本无契约」标出来。处置沿用本条原判：**不硬造断言，改为显式标注**（`$tableOnly` 清单 **1 → 4**，运行器侧修）。

---

63. **`svt.*` 键名残留 + 「孤立键门禁」的前置条件（2026-09-17）**

    **① `svt.*` 家族被误用（已修）** —— 与第 60 条**同一类缺陷**（键名指向一个后端、却用在另一个上）：

    | 键 | 译文语义 | 全仓唯一使用点 | 实际归属 | 处置 |
    |---|---|---|---|---|
    | `svt.quality.preset` | 「编码质量预设」 | 原 `MainWindow.xaml:562` / `:573` | **NVENC / QSV** 面板 | 第 60 条已统一为 `hw.quality.preset` |
    | `svt.aq.strength` | 「AQ 强度（0=关闭 15=最强）」 | `MainWindow.xaml:564` | **NVENC** 面板的 `AvifNvencAqBox` | 本轮改为 **`nvenc.aq.strength`**（新增，与 `nvenc.*` 家族一致） |

    关键证据：**SVT 面板（`:552-559`）里根本没有 AQ 控件**（只有 preset / tune / still-picture）
    ⇒ 这个键**从来就不是给 SVT 写的**。
    ⚠ 顺带核对：`svt.tune`（`:556`）**是正确的**（SVT 面板确实有 `SvtTuneCombo`）⇒ 本轮只动 `:564`，
    **未动 `:554`/`:556`**。

    **② 「孤立键门禁」的前置条件（未实现，先登记）** —— 孤立键 = 定义在 `zh-CN.json`、但
    `src/FfmpegGui/**/*.{xaml,axaml,cs}` 里找不到引用的键。现状 **105 个（≈579 的 18.1%）**，
    且**没有任何门禁覆盖** ⇒ 属系统性漂移，值得一条锁（与 `_probe-cjk-hardcode-scan.ps1` 同款：
    锁基线 + 负控 + 断言「不得增加」）。

    ⚠⚠ **但在实现之前，必须先验证「引用检测方法」**：同一仓库、**同一时刻**，不同口径相差 **2.7 倍**
    ⇒ 说明**至少有一方的引用检测在漏检**。三种口径实测如下（2026-09-17 逐一实跑）：

    | 口径 | 引用判定 | 扫描范围 | 孤立键 | 评价 |
    |---|---|---|---|---|
    | **A** | `"键"`（引号字面量）**或** `{ext:Loc 键}` | 全部 `.xaml` / `.axaml` / `.cs` | **105** | ✅ **建议采信**；偏宽松 ⇒ 105 是**下界** |
    | **B** | **只认** `{ext:Loc 键}` | **只扫** `.xaml` / `.axaml` | **296** | ❌ **完全无视 C# 引用**（C# 侧有 170 个索引器键 + 76 个 `FillComboByLoc` 键）⇒ 高报约 191 |
    | **C** | 只认「引用位」正则：`["k"]` / `("k"` / `, "k"` / `= "k"` | 全部 | **130** | ❌ **含 25 个假阳性**（见下） |

    口径 A 的可复现命令见第 60 条（实测输出 `键 579 / 孤立 105`）。
    ⚠ 口径 B（只认 `{ext:Loc}`、只扫 xaml/axaml）得 **296**；**273 那个数已无法复现**（产生它的脚本未留存）—— **两者同属「漏掉 C# 引用」这一类**，但**不能断言二者口径相同**。

    **口径 C 的假阳性是活证据**（下面这些键**真在用**，却被判为孤立）：
    `nvenc.p1` / `priority.normal` / `amf.speed` / `dng.bitdepth.8` / `avif.tune.default` …
    **根因**：`MainWindow.xaml.cs:907` 是
    ```
                    "nvenc.p1", "nvenc.p2", "nvenc.p3", ...
    ```
    —— `FillComboByLoc` 的实参**换行后位于行首**，前面是**空白而不是 `, `**
    ⇒ 只认「引用位」的正则**漏检** ⇒ 把这些键判成了孤立。

    **⚠ 危害方向必须说清**：漏检的后果**不是"多报几个孤立键"**，而是
    **「把在用的键误报为孤立」⇒ 会误导人删掉正在用的键**（删掉后 UI 立刻显示裸键名）。
    这比多报严重得多 —— 多报只是噪音，**误报是破坏性的**。
    ⇒ 实现该门禁时**方法必须先自证**：
    1. 拿若干**已知在用**的键，**覆盖每一种引用形态**（`{ext:Loc k}` / `Instance["k"]` /
       `FillComboByLoc(…, "k")` / 以及 `LocExtension` 之外的其它形态），验证它们**不被判为孤立**
       —— 上面口径 C 的 25 个假阳性就是**必须通过的反例集**；
    2. 拿若干**已知孤立**的键（如 `hw.hint` 接线前的状态），验证它们**被判为孤立**；
    3. 两种形态都要有**正/负控**，**才允许锁基线**。

64. **一条门禁依赖「另一条门禁的遗留产物」⇒ 它的绿不来自自身（2026-09-17 登记并修复）**

    **现象**：`verify-color-wiring.ps1` 的 (8) 段要 `tests/output/validate/caps/p3.icc`，
    而该素材由 `verify-color-caps.ps1` 生成；运行器清单（`_run-step3-gates.ps1`）却把
    `verify-color-wiring.ps1` 排在 `verify-color-caps.ps1` **之前**。后果：
    - **干净 `tests/output` 上 (8) 段必红**（素材不存在）；
    - 更坏的是**"单跑绿、全量红"**：单跑时目录里还留着上一轮的素材 ⇒ 绿；清一次输出目录再全量跑 ⇒ 红。
      **这种红极易被误判成产品回归**（本轮实测踩到：`wiring` 全量跑出 **73/1**
      `FAIL (8) 缺 P3 ICC 素材`，而单独复跑是 **80/0**）。

    **根因**：两层叠加 —— ① **顺序**（素材的生产者排在消费者之后）；② **素材是外部状态**
    （脚本自己不产出它，只读别人的输出目录）。

    ⚠ **结论（与第 55/58 条同源）**：这类门禁的**绿不可信** —— 它的通过取决于
    "输出目录里恰好有别人留下的文件"，而不是它自己验证了什么。**"靠顺序才绿"与"长期红"一样不可信。**

    **修法（两层都做了）**：
    1. **根修：消费者自备素材** —— `verify-color-wiring.ps1` 的 (8) 段缺素材（或素材退化成
       ≤200B）时**自己生成**。生成机制**抽成唯一一份** `tests/scripts/_lib-color-assets.ps1`
       的 `New-P3IccAsset`（两个门禁 dot-source 同一个函数）⇒ **不新增第二套口径**
       （否则又是"两处口径"的老问题）。该文件**不是门禁**：不产 PASS/FAIL、不登记进运行器。
    2. **纵深：运行器调序** —— `_run-step3-gates.ps1` 把 `verify-color-caps.ps1` 提到
       `verify-color-wiring.ps1` **之前**，常见路径下连自备逻辑都不必触发。

    **⚠ 抽函数时踩到的坑（值得记）**：exiftool 的 `-w` **不覆盖已存在的输出文件**。
    caps 因为开头就整目录删过，所以从没暴露；而"缺则自备"的调用方会遇到
    **0B 残留 ⇒ 自备变成空操作 ⇒ 素材仍是 0B ⇒ 照旧红**。
    ⇒ 共享函数**必须自己保证幂等**（`New-P3IccAsset` 生成前先删旧 `p3.icc`）。

    **验收（必须在"素材缺失"条件下也绿）**：删掉 `p3.icc` 单独跑 `wiring` ⇒ **80/0**
    （自备 584B）；再跑一次 ⇒ 80/0；把素材换成 0B ⇒ 自备救回 ⇒ 80/0；
    **变异**：短路自备逻辑（`if ($false)`）+ 删素材 ⇒ **73/1**（复现原缺陷，证明断言有牙）；
    `caps` 改用共享函数后仍 **12/0** 且素材同为 **584B**（抽取行为等价）。

---

65. **门禁断言的「前提」本身写错了 —— 三条 FAIL 全是断言 bug，不是产品缺陷（2026-09-17，JXR 出口轮）**

    **现象**：`verify-color-wiring.ps1` 新增的 (11) 段（JXR 执行出口）首跑 **89/3**，三条红看起来都像产品回归：
    `FAIL (11) 8-bit 源走 BMP 中转`、`FAIL (11) 容器像素格式回读为 24-bit BGR`（实得 `48-bit RGB`）、
    `FAIL (11) 对照：legacy 仍走硬编码 zscale`。

    **逐条定性（三条都是断言错，实现是对的）**：
    1. **把「源位深」当成了「出口位深」的判据**。(11a) 用 8-bit 的 `sdr_plain.png` 就断言「必走 BMP」。
       但中转容器的判据是 `plan.OutBitDepth`：直通/携带**绝不降位**（`ColorStrategyPlanning.cs:245-252`）
       ⇒ 该用例出口位深是 **16** ⇒ TIFF。而 `JxrEncApp` 自述 `bmp: <=8bpc` ⇒ 48-bit 中转**只能**走 TIFF，
       改实现去迁就断言就等于**静默把 16-bit 降成 8-bit**（本项目最忌）。且 legacy 同请求也是 `rgb48le` TIFF。
       **改断言**：直通用例钉 TIFF + 显式断言「不是 BMP」；另加一条 `--bit-depth=8`（Map+Clipping ⇒
       TargetBitDepth=8）**专门覆盖 BMP 分支**，避免它变成死代码。
       ⚠ 顺带更正：断言里写 `-pix_fmt bgr24` 也是错的 —— 出口两条分支都用「变换后 raw 的像素格式」
       （`pixOut = pixIn`），BMP 的 BGR 字节序由 ffmpeg 的 BMP 编码器负责，**由独立回读
       （exiftool `PixelFormat: 24-bit BGR` + JxrDecApp `-c 0`）钉住**比断言 ffmpeg 入参更硬。
    2. **对照断言的判据选了「本路径根本不产出」的日志行**。原断言取 `[cmd] ffmpeg` 再匹配 `zscale`，
       但 legacy 的 `ProcessJxrAsync` **从不记这行**（只写 `item.Command`）⇒ 取到空串，**必红**，
       且这个红会被读成「legacy 被改动了」（本轮 team-lead 就是这么怀疑的）。
       也**不能**改用 `[jxr] JxrEncApp.exe` —— `JxrService.RunAsync` 两侧都记（引擎出口内部也调它）。
       真正两侧互斥的只有 `[jxr] Step 1:`（legacy 专用）与 `[色彩] JxrEncApp 命令行:`（引擎出口）。
       **改断言**：改用这两个互斥标记，并补一条 legacy 硬编码转换的**可观测效果**
       （源标 `smpte432` ⇒ 中转 TIFF 被改标 `bt709`），比读源码自证更硬。**legacy 侧源码一字未改。**
    3. **计数口径的假阳性**：`RawColorPipeline.cs:593` 的**行尾注释**里写了带直引号的 `"编码失败 (exit …)"`；
       `_probe-cjk-hardcode-scan.ps1` 只剥**块注释**、只跳过**整行 `//`** ⇒ **行尾注释不剥**，
       注释里的引号串被当成硬编码文案计数。已改成 `「」`（该行不再计数）。

    ⚠ **可复用的教训**：门禁断言必须钉**计划/契约字段**或**两侧互斥的可观测标记**，
    不要钉「素材的偶然属性」（源位深 ≠ 出口位深）或「另一条路径里并不存在的日志行」。
    前者会让实现被迫向错误前提妥协（或长期假红），后者会**必红且归因错误** ——
    两种都比"没有断言"更坏，因为它们的红**看起来像产品缺陷**。

66. **共享输出目录 ⇒ 并发同名门禁互相清空 ⇒ 假红（2026-09-17 系统性修复 12 个脚本）**

    **现象**：**同一脚本连跑多次，`PASS=n` 总数都会变** —— `verify-color-wiring.ps1` 实测 `94/4` 与
    `98/0` **交替**出现；FAIL **恒为日志类断言**，而**文件类断言照常绿**（产物确实存在、第三方工具也能读回）。
    ⚠ 单看某条 FAIL 极易误判成**产品回归**：本项目已因此误判两次（worker 报 94/4、lead 报 87/7 与 93/4）。

    **根因**：脚本把 `$out` 写成**固定路径**（如 `tests/output/validate/wire`），且**开头整目录删**
    （`Remove-Item -Recurse -Force $out`）。两个同名实例并发时互删对方的中途产物：A 实例清掉目录后，
    B 实例 `RunCli` 读到的 `$out/<tag>.o` 是**空的** ⇒ 依赖该日志的断言齐红，而按文件路径取产物的断言
    仍拿到（对方或自己刚写的）真文件 ⇒ 表现为「红得没道理、且每次红的条数还不一样」。

    **识别方法（关键）**：**「总数变化」是最强信号** —— 同一份代码上门禁的总 `PASS` 数波动，就应立刻
    怀疑自并发/共享目录，而**不是**先去读产品代码。辅证：FAIL 全集中在日志类断言；
    以及**在你没跑门禁时** `tests/output/**` 仍有新写入（`Get-ChildItem -Recurse | Sort LastWriteTime`）。

    **修法（照既有范式，不要另创）**：**运行级隔离（GUID）** + **结尾自清** ——
    `$out = "$val/<name>_$([guid]::NewGuid().ToString('N').Substring(0, 8))"`，结尾
    `if ($fail -eq 0) { Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue }`
    （宿主批量删除守卫会拦整目录删 ⇒ 必须 `-EA SilentlyContinue`，见第 57 条 ③；**失败则保留**供排查）。
    诊断型脚本（无 `pass/fail` 计数）改为**结尾无条件自清**。⚠ 共享**素材**目录（`$val` 本身、
    `validate/caps`、`results/color`）**不要** GUID 化 —— 它们是被读的输入，不是被写的产物。

    ⚠ **另一条同样非显然、必须写下来的纪律：自清只删「自己的 GUID 目录」，绝不对共享素材目录做整目录删。**
    共享目录里放的是**别的脚本**的产物 —— 实测 `tests/output/validate/` **顶层**就有 **15 个文件**
    （`tm_in.png` / `tm_on.png` / `src_hdr.png` / `src_hdr8.png` / `sdr_plain.png` / `bit_8.png` / `bit_16.png` /
    `bit_in.png` / `_icc*.txt` …），它们**分属不同脚本**（例如 `verify-color-peak.ps1:46` 读 `src_hdr.png`）。
    ⇒ 对这类目录做 `Remove-Item -Recurse -Force` 会**连带删掉别人的东西**（**这不是假设**：上列文件确实
    分属不同脚本）⇒ **只清自己的 GUID 目录是「有意的」，不是「漏了」**。
    例：`verify-gif-avif-framelist.ps1` 的自清只删 `$work`（`tests/output/gifavif_<guid8>`），
    **完全不碰**共享素材目录（该脚本全文不引用 `$val` / `results/color` / `sources` / `validate/`）。
    同理，GUID 化**不迁移**旧固定目录里的历史产物 —— 那些目录保持原样（见第 66 条末尾「顺带」一段）。

    **本轮范围**：12 个「固定 `$out` + 开头整目录删」的脚本 —— `_probe-png-cicp-production` /
    `probe-gainmap-tiff` / `verify-color-strategy` / `verify-color-wiring` / `verify-format-regression` /
    `verify-gainmap-engine` / `verify-gainmap-managed` / `verify-gamut-map` / `verify-prophoto-faithful` /
    `verify-tiff-icc` / `verify-webp-hdr-fix` / `verify-widegamut-regression`。
    **验收（唯一硬证据）**：**同时起两个同名实例 ⇒ 两个都 `FAIL=0`，且 `PASS` 数完全相同**。
    实测 **12/12 通过**：`wiring 98/0 ×2`、`color-strategy 53/0 ×2`、`gamut-map 36/0 ×2`、
    `gainmap-engine 36/0 ×2`（2026-09-19 现为 **84/0 → 117/0**）、`prophoto 26/0 ×2`、`gainmap-managed 20/0 ×2`、`format-regression 19/0 ×2`、
    `widegamut 18/0 ×2`，另 4 个诊断型脚本 `DONE×2` 且无 `[FAIL]`/`NO OUTPUT` 行。

    ⚠ **判据本身也要防假绿（2026-09-17 补两条限定）**：
    ① 只判 `FAIL=0` **不够** —— 两实例各漏跑一段、都恰好 `FAIL=0` 就会**假通过** ⇒ 必须**同时**要求 `PASS` 数相同。
    ② **无计数（表格型 / 诊断型）脚本**用等价判据：**归一化运行期变化 token（GUID）后**输出**逐字相同**。
       ⚠ 措辞**不能漏「归一化」**：输出里可能含运行期生成的 GUID 目录名，直接比「逐字相同」会**恒红**；
       照着写的人会写出一条永远红的断言。
       ⚠ 且该脚本输出**不得含耗时/性能字段**（`ms` / `MPix/s` / `ProcessMs` / `Elapsed` / `Stopwatch` 之类），
       否则逐字相同会因抖动**假红** ⇒ 必须先归一化再比。
       已核实（grep 过）：`verify-colorfmt-matrix`（md5 / 字节数 / CICP 令牌）与 `_probe-ztf-locate`
       （RMS / max / 占比 / 抽样码值表）**都不含**耗时字段 ⇒ 该等价判据成立；实测归一化后**逐字相同 = True**（8 行 / 38 行）。
    ③ 第二轮实测：`verify-color-peak` **25/0 ×4**（两实例各 2 次）、`verify-gif-avif-framelist`
       **17/0/0 ×4**（两实例各 2 次，两实例各 14/15 s ⇒ 真重叠）。

    **残留清单（2026-09-17 第二轮收尾，逐脚本 grep 实测，**不是估计**）**：
    上一轮点名的「`$d` 形态 **15 个**」（`verify-colorfmt-matrix` / `verify-color-tonemap` / `verify-color-peak` /
    `verify-color-engine-xcheck` / `verify-color-caps` / `verify-png3-interop` / `_probe-gpu-stage1` /
    `_probe-gpu-stage2` / `_probe-caps-e1-e2` / `_probe-jpg-p3-icc` / `_probe-sws-downshift` /
    `_probe-sws-yuv` / `_probe-zimg-tagged` / `_probe-zimg-transfer-table` / `_probe-ztf-locate`）
    **已结清**：其中 **11 个已隔离**（累计免疫 32 个 = 原 21 + 11），**4 个有意保持固定路径**
    （`_probe-jpg-p3-icc` / `verify-color-caps` / `_probe-sws-yuv` / `_probe-gpu-stage2`，
    理由见上表「性质」列与第 68 条 —— 它们是**生产者**，消费者把它们的路径**硬编码**了：
    `_probe-sws-pos.ps1:19` 读 `swsyuv/in.raw`；`_probe-gpu-stage3.ps1:9` 把 `gpu2/k.cl` 硬编码成 `$kesc`
    并在 `:39/:40/:41/:55` 拼进 `program_opencl=source=`）。

    ⚠ **但「15 个」这个数本身就是不完整的** —— 逐脚本 grep（把「赋值指向 `tests/output` 固定路径
    且**不含** `NewGuid`」的行全部列出）实测出**另外 8 个仍在写固定输出目录**的门禁/探针；
    其中**必跑清单内的那 1 个已修**（见下），**现仍剩 7 个未隔离**（加上 4 个有意排除的 = 11 个）：
    | 优先级 | 脚本:行 | 固定目录 | 性质 |
    |---|---|---|---|
    | ~~① **在必跑清单内**~~ **已修** | ~~`verify-gif-avif-framelist.ps1:21`~~ | ~~`tests/output/gifavif`~~ | **已按 A 范式 GUID 化**（`$work = tests/output/gifavif_<guid8>` + 按 `$fail` 自清，两处提前 `exit` 也各自先自清）；已核实**不是生产者**（全仓 `grep gifavif` 无第二方读它）；代价实测：3000 帧素材重造 **0.1 s**、420 帧 **0.03 s**（GUID 化会让 `Make-Gif` 的跨运行复用失效）⇒ 可忽略 |
    | ② C 类簇成员 | `_probe-sws-pos.ps1:6` | `validate/swsyuv` | swsyuv **读+写**（须与生产者同批处置） |
    | ② | `_probe-gpu-stage3.ps1:8` | `validate/gpu2` | gpu2 **读+写**（同上） |
    | ② **跨簇写入** | `_probe-icc-affects-decode.ps1:10` | `validate/jxl-webp-pixels` | ⚠ **不是「只读」**：读该簇产物，又往**同一目录**写 `extracted.icc` 与临时 `_px.raw` ⇒ **跨簇写入**，独立 GUID 化会打断那条链 ⇒ 与生产者同批处置 |
    | ③ **低价值未隔离** | `_probe-hdr-peak-oracle.ps1:14` | `tests\output\tmp\peak-oracle` | 已核实**不在必跑清单** + **无 `CK`/无 `pass`/`fail` 计数** ⇒ 不参与并发竞争、红了也没人看 ⇒ **登记不派工** |
    | ③ | `_probe-p7d-bisect.ps1:19` | `validate/p7d` | 同上 |
    | ③ | `_probe-zimg-noop-check.ps1:5` | `validate/cmsx` | 同上；且是**死诊断**（读的 `A|B/zimg.png` 全仓无写者） |
    | ④ 生产者（保持固定） | `_probe-jxl-webp-pixels.ps1:23` | `validate/jxl-webp-pixels` | 该簇**生产者**，按 C 类裁定**不动** |

    ⚠ 判据只算**写**：**只读**固定共享路径的不算自并发隐患（只读不互删）——
    `verify-color-peak.ps1:46` 读 `validate/src_hdr.png`、`verify-colorfmt-matrix.ps1:18` 与
    `verify-png3-interop.ps1:103` 读 `validate/jpgp3/p3.icc`、`verify-gamut-map.ps1:44` 读
    `sources/src_8bit.png`、`_probe-png-cicp-production.ps1:14/:73` 读 `gainmap/png3_cicp.png` + `validate/jpgp3/p3.jpg`。
    ⚠ **grep 数数时的反例**：`verify-png-signature.ps1:18` 的 `$val = …/pngsig` 是**死残留赋值**
    （同名变量在 `:46` 已被 GUID 覆盖）⇒ **不是**未隔离项。只按「有没有固定路径字面量」数会多算。
    另有 runner 类（`run-*-tests.ps1` / `run-full-matrix-test*.ps1` / `_run-step3-gates.ps1`，
    写 `tests/output/results/` 或 `_gate_*.txt`，是顶层入口、天然不是并发自保对象）。

    ⚠ **另一类更隐蔽的「不能各自 GUID 化」：共享目录簇（生产者 → 消费者）**。本轮 ④ 盘点实测出
    **6 组**（`jxl-webp-pixels` 那对只是其中之一）。⚠ 其中第 1 组（`caps`）经本轮**实测已解除单向依赖**
    （见下方「第 1 组的实测结论」）⇒ **真正的「生产者遗留产物依赖」只剩 5 组**，`caps` 降级为「双向共享写」：

    | 目录 | 生产者（写 + 开头整目录删） | 消费者（读它的产物） | 性质 |
    |---|---|---|---|
    | `validate/caps` | `verify-color-caps:11`（`:12` 删） | `verify-color-wiring`（经 `_lib-color-assets.ps1` 的 `New-P3IccAsset` **自备**，`wiring:234-242`） | **双向共享写**（非单向依赖） |
    | `validate/jpgp3` | `_probe-jpg-p3-icc:8`（`:9` 删）⚠**不在必跑清单** | `verify-png3-interop:102`(p3.icc) ⚠**在必跑清单** · `verify-colorfmt-matrix:17`(p3.icc) ⚠**在必跑清单** · `_probe-png-cicp-production:73`(p3.jpg) | 生产者遗留依赖 |
    | `validate/jxl-webp-pixels` | `_probe-jxl-webp-pixels:23`（`:24` 删） | `_probe-icc-affects-decode:10`（读 `main/a2_srgb_p3.webp`、`v_*.webp`）· `_probe-p7d-bisect:77`（读 `src_p3.jxl`，**缺则自备**） | 生产者遗留依赖 |
    | `validate/cmsx` | `verify-color-engine-xcheck:15`（`:19` 删）⚠**在必跑清单** | `_probe-zimg-noop-check:5`（读 `A/zimg.png`、`bars16.png`） | 生产者遗留依赖 |
    | `validate/swsyuv` | `_probe-sws-yuv:6`（`:7` 删） | `_probe-sws-pos:6`（读 `in.raw`） | 生产者遗留依赖 |
    | `validate/gpu2` | `_probe-gpu-stage2:5`（`:6` 删） | `_probe-gpu-stage3:8`（且 `:10` 的 `$kesc` 把 `gpu2/k.cl` **硬编码**进 ffmpeg 参数，改目录须连带改） | 生产者遗留依赖 |

    ⚠ **上表的行号与「在/不在清单」标注是 2026-09-17 早间的快照**，同日第二轮改完后已有位移
    ⇒ **以本节上方的「残留清单」为准**。其中两处**已过期，必须点明**（否则同文档内自相矛盾）：
    ① `_probe-jpg-p3-icc` 那一格的「⚠**不在必跑清单**」**已不成立** —— 它已按第 68 条加进清单
    （索引 **[19]**）并**前置于**两个消费者；
    ② `verify-color-engine-xcheck` 那一格的「`:19` 删」**已不成立** —— 它的 `$d` 已 GUID 化（现 `:20`），
    开头整目录删已移除 ⇒ 该行的「生产者遗留依赖」对本轮之后**不再适用**（消费者是**死诊断**，见上方 ②(c)）。
    另：`verify-png3-interop` 的读点现为 `:103`、`verify-colorfmt-matrix` 现为 `:18`
    （各 +1 行，因本轮给它们加了隔离注释）。

    **第 1 组的实测结论（2026-09-17）**：删掉 `validate/caps/p3.icc`（原 584B）⇒ 单独跑
    `verify-color-wiring.ps1` ⇒ **`PASS=99 FAIL=0`、`exit=0`**，且跑完 `p3.icc` **被重新生成为 584B**
    ⇒ 第 64 条的「自备素材」修法**确实覆盖到了**，`wiring` **不再依赖 caps 的遗留产物**。
    ⚠ 但**共享写仍在**：`wiring:239` 自备时是 `New-P3IccAsset -Dir "$val/caps"`，即**写进同一个共享目录**；
    若与 `verify-color-caps.ps1`（开头**整目录删**）并发 ⇒ caps 会删掉 wiring 刚生成/正要用的 `p3.icc`
    ⇒ wiring (8) 段红。**失败形态是假红（响的），不是假绿** ⇒ 可接受，但仍在「同一时刻只跑一个实例」的约束内。

    ⇒ 这类**严禁「各脚本各自 GUID 化」**：生产者一换路径，消费者的 `Test-Path` 立刻失败，
    而消费者的写法多为 `if (-not (Test-Path $z)) { … continue }` ⇒ **静默跳过 ⇒ 假绿**（比假红更坏，
    已单独登记为**第 68 条**）。
    可行方案只有两种：
    **(a) 指针法** —— 生产者 GUID 化后把实际路径写进指针（env var 或 `validate/.ptr/<name>`），
    消费者**读指针**且**读不到就红**（禁止 skip）；
    **(b) 保持固定目录 + 由 runner 保证串行**，并登记为「已知非并发自保」。

    **裁定：选 (b)（2026-09-17，team-lead）**。理由：
    ① 6 组里 5 组的生产者/消费者是 `_probe-*` 诊断脚本（**不在 `_run-step3-gates.ps1` 的必跑清单**）
    ⇒ 并发风险低；唯一在正式链路上的 `caps → wiring` 已由第 64 条（自备素材）+ runner 顺序管住。
    ② **更根本**：**指针机制本身就是一种新的「跨脚本隐式耦合」** —— 而本项目这几轮一直在拆的正是这类
    隐式耦合（第 64 条「依赖别脚本遗留产物」、本条「共享目录互删」）。为几个诊断脚本引入指针，
    等于**用一个新的隐式耦合去换掉一个已知的隐式耦合**，净收益为负。⇒ 宁可**显式登记「已知非并发自保」**。

    ⚠ **但有一条硬前提**：**若将来要把这些脚本纳入必跑清单，必须先做 (a)**（或先做第 68 条的
    「读不到就红」改造）—— 因为必跑清单里「消费者在、生产者不在」就是**必然的静默假绿**，
    而 `jpgp3` 组**当前已经是这个状态**（见第 68 条实测）。

    ⇒ 综上（**已隔离 33 个** + **实测仍有 7 个**在写固定输出目录，其中 **3 个登记为「低价值未隔离」** + 6 组共享目录簇），
    （⚠ 上面这个 **33** 是**该轮普查的日期读数**，与 §1 表头的计数**不是同一套口径**（后者 2026-09-18 为 35 个）；
    2026-09-18 又新增两个 GUID 自保脚本（`verify-anim-static-target.ps1` / `verify-jxl-probe-timeout.ps1`）⇒ 两套口径各自都要 +2，
    **本条不改写该日期读数**，只标注它已过期。）
    **在它们被隔离/定案前，「同一脚本一次只跑一个实例」依然是硬约束**
    （`_run-step3-gates.ps1` 顶部的并发约束段已同步改写）。
    本轮实测的代价：lead 与 worker 并发跑 `verify-color-strategy` ⇒ 全量扫描读到 **49/4**，
    而单独复跑是 **53/0 `exit=0`** —— ⚠ 又一次「**总数变化 = 最强信号**」（53 → 49，FAIL 0 → 4）。

    **顺带**：旧固定目录（`validate/wire`、`validate/strat`、`validate/gmap` …）在新运行后**不再被写入**，
    但**目录与历史产物还在**。⚠ 它们会**冒充当前产物** —— 本轮排查时我就被 `validate/wire/eng_jxr_sdr.o`
    里的旧日志误导过一次 ⇒ 建议单独清理，**排查时不要引用旧固定目录**。

    ⚠ **判「残留」的正确口径（2026-09-17 更正；本轮同一误判踩了两次）**：
    **GUID 目录 ≠ 残留** —— **活跃运行会正常产生 GUID 目录**（这正是「运行级隔离」的设计，不是异常）。
    ⇒ 判残留**必须同时看「有没有进程在写它」**，**不能只看名字形态**。可用判据（按可靠性排序）：
    ① 目录内文件的 `LastWriteTime` **是否仍在推进**（隔几秒再取一次最新 mtime，变了就是在跑）；
    ② 进程表里有无门禁子进程（`ffmpeg` / `exiftool` / `cjxl` / `djxl` / `jxlinfo` / `ServiceProbe` / `FfmpegGui`）；
    ③ 运行级隔离的语义是**跑绿自清、跑红才保留** ⇒ 目录还在只有两种可能：
    **（a）正在跑；（b）跑完了且 `$fail > 0`**。
    两种情况**都不该直接删**：（a）删了会破坏别人的运行；（b）是别人的排查证据（且它正是「有红」的信号）。
    **本轮两次实证**：① `strat_4b8ca19b` —— worker 判为「残留」，实为 **lead 自己的活跃运行**留下的；
    ② `wire_5be1b91d` —— worker 判为「残留」，两次探测后确认是 **`engine-block-s2` 正在跑
    `verify-color-wiring`**（观察到它从 `_probe.txt` 推进到 `eng_cjxl_pq`，条目数 39 → 78，
    `FfmpegGui`/`exiftool` 随之启动）⇒ **未删、未干扰**，并给该 worker 发了 FYI。
    ⇒ **不要用「`grep` 到 GUID 形态目录数 = 0」当作「残留自证」**（这条断言本身就是错的）；
    残留自证应写成「**在无进程写它的前提下**，`validate/` 下无 GUID 形态目录」。
    ⚠ 推论：若某个 GUID 目录**长时间不再推进且无进程**，它才**可能**是（b）——此时**先读它的产物**
    （往往就是某条门禁的失败现场），**不要先删**。

67. **清理步的副作用会污染 `$LASTEXITCODE` ⇒ 已绿的格子被误判为红（2026-09-17）**

    **现象**：`run-pipeline-tests.ps1` **第五部分 cjxl 5 格恒红**，每格报
    `[safe-delete] … (exit 2)`。⚠ 注意它**不是断言失败** —— 是**收尾的清理动作**把退出码搞坏了；
    五格全红且红法完全一致，是「脚本自身缺陷」而非「产品回归」的典型指纹。

    **根因**：那 5 格收尾用 `Remove-Item "$res/_pipe.pm" -Force -ErrorAction SilentlyContinue`。
    宿主批量删除守卫在累计删除量超阈值后，会把 `[safe-delete][SAFE_DELETE_BULK_CONFIRM_REQUIRED]`
    作为 **terminating error 抛进脚本上下文**；`-EA SilentlyContinue` **挡不住它**（它不走 `-EA` 通道），
    **且它会把 `$LASTEXITCODE` 污染为 2**。于是「断言全过 + 清理报错」⇒ 该格被判红。

    **`try/catch` 为何无效（本轮实测，反直觉）**：用 `try { Remove-Item … } catch { }` 包起来，
    异常确实被吞掉、脚本不中断，但 **`$LASTEXITCODE` 仍已被写成 2** ⇒ 运行器只读退出码 ⇒ **照样判红**。
    ⇒ **判断「清理是否安全」不能看有没有抛异常，要看退出码有没有被动过。**

    **修法**：清理步改用 **`.NET API`**（绕过守卫）：`[System.IO.File]::Delete("$res/_pipe.pm")`
    （目录用 `[System.IO.Directory]::Delete($p, $true)`）。实测 **`75/5` → `80/0`，`exit=0`**。

    **通用教训（本条的真正价值）**：
    ① **清理/收尾动作与断言同等重要** —— 它在断言**之后**执行，坏了照样能把绿的格子判红；
    ② 判据是**退出码**而不是「有没有报错」，所以 `-EA SilentlyContinue` / `try/catch` 都不构成保护；
    ③ 批量删除场景**优先用 `.NET API`**，不要指望 `Remove-Item` 的开关（详见第 57 条 ③）。

    **附：给门禁补退出码时，`else` 是必需的（同一轮的另一处隐蔽坑）**
    本仓库有一批门禁**全文无 `exit`** ⇒ 恒 `exit 0` ⇒ **它红了运行器也不知道**（同类见第 58 条）。
    补法必须写**完整 if/else**：`if ($fail -gt 0) { exit 1 } else { exit 0 }`。
    ⚠ **不能只写前半句** —— 因为上面那条守卫会污染 `$LASTEXITCODE`（实测 2），只写
    `if ($fail -gt 0) { exit 1 }` 时，**全绿路径会以被污染的 2 退出 ⇒ 假红**（把「修假绿」修成「造假红」）。
    本轮实测：`verify-format-regression` 断言改恒假 ⇒ **`PASS=0 FAIL=19 exit=1`**；
    `verify-widegamut-regression` 同法 ⇒ **`PASS=0 FAIL=18 exit=1`**（回滚后恢复 19/0、18/0 且 `exit=0`）。
    同理，**退出码要放在结尾自清之后**（`verify-color-strategy.ps1` 本轮即按此调整）。

68. **消费者「读不到前置产物就静默跳过」⇒ 假绿（与并发无关，2026-09-17 登记 + 实测）**

    **为什么单列一条**：它**不依赖并发**。只要前置产物因**任何**原因缺失（生产者没跑、目录被清、
    路径被改、上一步失败），消费者就**静默给绿**。⚠ **这比假红更坏** —— 假红至少有人去查。

    **实测（本轮，硬证据）**：`verify-png3-interop.ps1` 的 ④ 段读 `validate/jpgp3/p3.icc`，
    写法是 `if (Test-Path $icc) { … } else { Write-Output "  ④ SKIP：缺 …" }`（**无 `CK $false`**）。
    把 `validate/jpgp3` 整个移走后再跑：

    | 条件 | 结果 | 退出码 |
    |---|---|---|
    | 有 `jpgp3/p3.icc`（584B） | `PASS=9 FAIL=0` | **0** |
    | 缺 `jpgp3`（移走） | `PASS=8 FAIL=0`，只多一行 `④ SKIP：缺 …` | **0** |

    ⇒ **退出码完全不变**，只有**总数 9 → 8**。运行器只看退出码 ⇒ **它不知道少验了一条**。
    ⚠ 这正是「**总数变化 = 最强信号**」（第 66 条）在**非并发**场景下的另一种表现：
    **总数掉了但退出码没掉 = 有一条断言被静默吞了**。

    **为什么这条现在就必须登记（不是"将来再说"）**：`jpgp3` 组**当前就是「消费者在必跑清单、
    生产者不在」的状态**（「当前」指**登记当时**；本轮已修，见下段）—— `_run-step3-gates.ps1:143-146` 的
    `validate/jpgp3` 组注释里写着 `verify-png3-interop.ps1`
    与 `verify-colorfmt-matrix.ps1`（**两个消费者**）在清单、`_probe-jpg-p3-icc.ps1`（**唯一生产者**）不在
    ⇒ 在干净的 `tests/output` 上，这两个消费者**必然静默降级**（png3-interop 少 1 条 ④；
    colorfmt-matrix 打印「无参照」，而它本就是「表格型无断言」脚本，见第 62 条）。
    ⚠ **行号更正（2026-09-19）**：上句原引 `_run-step3-gates.ps1:142-147`，实测该注释在 **`:143-146`**
    （`:142` 是另一条并行条款的结尾）。**非本轮引入的既有失真，一并修正**；行号锚在运行器快照
    `SHA256=C999C35BF6C49506…` / 57518 B / mtime `2026-09-19 12:15:44` / 671 行，**指纹一变即作废**。

    **规则（本项目的硬口径）**：**消费者读不到前置产物时必须红，不得 `continue` / 不得只打印 SKIP。**

    **本轮已修（2026-09-17）—— 选 (ii)：生产者入清单 + 前置，且生产者「自备输入」**：

    - **裁定**：把生产者 `_probe-jpg-p3-icc.ps1` 加进 `_run-step3-gates.ps1` 的必跑清单，
      **前置于**两个消费者（`verify-colorfmt-matrix.ps1` / `verify-png3-interop.ps1`）。
      不选「把消费者的 SKIP 改成 `CK $false`」的理由：④ 段（iCCP/cICP 共存）是**有意义的观察项**，
      改成必红等于**主动放弃一条覆盖**；补生产者只是**修复清单的遗漏**。
    - ⚠ **实施时发现的阻塞（差点把「静默假绿」换成「需手工前置步骤的红」）**：该生产者的输入
      `tests/output/gainmap/png3_cicp.png` **不是链内任何脚本产的** —— 它来自**链外的
      `GainMapTestHost`**（`tests/GainMapTestHost/Program.cs:83-136`），仓库里那份是 2026-09-15 的**遗留文件**；
      生产者原本写着 `if (-not (Test-Path $in)) { … exit 1 }` ⇒ **直接入清单会让 runner 在干净环境上
      出现一个 `exit=1`**（虽不致命——`_run-step3-gates.ps1` 不聚合子脚本退出码，只打印——但属于新增噪声，
      且要求人工跑一个链外 host）。
    - **修法（同第 64 条的口径：谁需要谁自备，共用唯一一份生成实现）**：生产者改为**自备输入**——
      `ffmpeg -f lavfi -i "testsrc2=size=128x96:duration=0.1" -frames:v 1 -update 1 -c:v png -pix_fmt rgb48be`
      生成基准 PNG，再用 **`ServiceProbe png3 <file> 10 9 16`**（调**生产代码** `PngCicpService`）
      写 cICP(9,16)+sBIT(10)。**与 `GainMapTestHost` 逐字同构，不新增第二套口径**；
      并保留兜底：ServiceProbe 不可用时退回链外遗留素材，两者都没有才 `exit 1`（**明确红，不静默**）。
      ⇒ 实测生成物 `src_cicp.png` **4668B**，与 `GainMapTestHost` 的原产物**同尺寸**（同口径的旁证）。
    - **干净状态实测（移走整个 `validate/jpgp3` 后按新顺序跑）**：

      | 脚本（新顺序） | 结果 | 说明 |
      |---|---|---|
      | `_probe-jpg-p3-icc.ps1` | `pass=1 fail=0`、`exit=0` | 自备输入成功；`srgb.jpg`/`p3.jpg` 各带 **584B** ICC |
      | `verify-colorfmt-matrix.ps1` | 参照可用 | 打印 `参照 P3 ICC md5 = E9CFC276（来自 _probe-jpg-p3-icc.ps1，色度已核）` ⇒ **不再「无参照」** |
      | `verify-png3-interop.ps1` | **`PASS=9 FAIL=0`、`exit=0`** | ④ 段**真跑**（此前干净状态是 **8/0** 静默 SKIP） |

      ⇒ **干净环境上从「8/0 静默假绿」变成「9/0 真覆盖」，且无新增红。**
    - ⚠ **依赖声明（重要）**：`validate/jpgp3` 是**固定共享目录** ⇒ 本组现在是**清单内有序链**，
      其正确性**依赖 runner 串行**（这正是 (b) 裁定的适用范围）。**若将来 runner 改成并行，
      本组必须先改成「指针 / 按会话隔离」**（即 §6 第 66 条的方案 (a)），否则会退化成
      「生产者与消费者互相清目录」⇒ 假红（甚至假绿）。

    **其余 C 类组的核查结论（2026-09-17 逐个核过，均无「消费者在清单、生产者不在」这一形态）**：

    | 组 | 生产者 | 消费者 | 结论 |
    |---|---|---|---|
    | `validate/caps` | `verify-color-caps.ps1` **在清单** | `verify-color-wiring.ps1` **在清单** | **都在**且已调序（caps 前置）⇒ 清单内有序链 |
    | `validate/cmsx` | `verify-color-engine-xcheck.ps1` **在清单** | `_probe-zimg-noop-check.ps1` **不在** | **反向形态**（消费者根本不跑）⇒ 无隐患 |
    | `validate/jxl-webp-pixels` | `_probe-jxl-webp-pixels.ps1` 不在 | `_probe-icc-affects-decode.ps1` 不在 · `_probe-p7d-bisect.ps1` 不在 | 双方都不在 ⇒ 无隐患 |
    | `validate/swsyuv` | `_probe-sws-yuv.ps1` 不在 | `_probe-sws-pos.ps1` 不在 | 双方都不在 ⇒ 无隐患 |
    | `validate/gpu2` | `_probe-gpu-stage2.ps1` 不在 | `_probe-gpu-stage3.ps1` 不在 | 双方都不在 ⇒ 无隐患 |

    ⇒ **只有 `jpgp3` 组**是「消费者在清单、生产者不在」，已修；其余 4 组**无需处理**
    （它们只在手工诊断时跑，届时「一次一个实例」已足够）。

    形态对照（**#2/#3 已随上表修复；#4–#9 仍按「只登记不逐个改」处理**）：

    | # | 消费者（文件:行号） | 前置产物 | 当前形态 | 是否在必跑清单 |
    |---|---|---|---|---|
    | 1 | `verify-color-wiring.ps1:237-262` | `caps/p3.icc` | ✅ **已硬失败**（缺则**自备**；自备仍失败 ⇒ `CK $false`，`:262`） | 是 |
    | 2 | `verify-png3-interop.ps1:103-109` | `jpgp3/p3.icc` | ✅ **已修（本轮）** —— 不再是静默 SKIP：生产者入清单且**自备输入** ⇒ ④ 段在干净环境上**真跑**（9/0） | 是 |
    | 3 | `verify-colorfmt-matrix.ps1:39` | `jpgp3/p3.icc` | ✅ **已修（本轮，随 #2）** —— 参照在干净环境上**可用**（不再「无参照」）；该脚本本就是表格型无断言（第 62 条） | 是 |
    | 4 | `_probe-png-cicp-production.ps1:74-85` | `jpgp3/p3.jpg` | ❌ **静默 SKIP**（`:85`） | 否 |
    | 5 | `_probe-icc-affects-decode.ps1:22-25` | `jxl-webp-pixels/main/*.webp`、`v_*.webp` | ❌ **静默 SKIP**（`:25` `continue`；且 `:22` 直接抽 ICC **不检查存在**） | 否 |
    | 6 | `_probe-p7d-bisect.ps1:78` | `jxl-webp-pixels/src_p3.jxl` | ✅ **缺则自备**（自己用 ffmpeg 生成） | 否 |
    | 7 | `_probe-zimg-noop-check.ps1:19` | `cmsx/A/zimg.png` | ❌ **静默 SKIP**（`:19` `continue`） | 否 |
    | 8 | `_probe-sws-pos.ps1:28` | `swsyuv/in.raw` | ❌ **静默 SKIP**（`:28` 「跳过（不可判读）」+ `return`） | 否 |
    | 9 | `_probe-gpu-stage3.ps1:9` | `gpu2/k.cl` | ⚠ **无检查**（`$kesc` 硬编码进 ffmpeg 参数，缺则 ffmpeg 报错，但脚本无断言、无 `exit`） | 否 |

    ⚠ 注意 #4–#9 那 6 个脚本**全文无 `exit`**（诊断型）⇒ 它们「静默」是**设计如此**（不产 PASS/FAIL）；
    **在必跑清单里的两处（#2/#3）本轮已修**，#1 早已修、#6 早已自备。
    ⇒ 剩余静默项**全部不在必跑清单**，故按「只登记不逐个改」处理。

    **⚠ 2026-09-19 补：另一类同族形态 —— 「前置条件失败时 `exit 0`」（6 个脚本 / 14 处，**全部在必跑清单**）**

    **机制**：不是 `SKIP` / `continue`，而是**显式 `exit 0`** —— 前置条件（缺工具 / 素材生成失败）不满足时，脚本**主动以退出码 0 结束**。
    ⇒ 运行器对**脚本**的记账**只看退出码**（见第 62 条「记账通道**不对称**」）⇒ **`failedItems` 完全看不到**，该门禁在本轮**等于没跑**。
    ⚠ 与上表 #4–#9 的关键区别：**这 6 个全部在必跑清单里**（#4–#9 那 6 个**都不在**）⇒ 不受「剩余静默项全部不在必跑清单」那句结论的保护。

    **⚠ 定性取证（2026-09-19 逐处核过 —— 结论与「比静默 SKIP 更退一步」相反）**：
    **14 处 `exit 0` 路径全部先打印 `SKIP …`**（`verify-gif-avif-framelist.ps1:46` 还额外打印完整 `===== PASS=$pass FAIL=$fail SKIP=$skip =====` 汇总行）。
    ⇒ **与 #4–#9 的「静默 SKIP」同级**（都是「**日志可见、但不计入**」），**不是**「更退一步」。
    ⚠ 但仍有一处更危险：读者若只看 `exit=0`、或只看「不在失败列表里」，就会**把它当绿** —— 这正是第 76 条 ②「**SKIP 若不进汇总，读者看到'全绿'会以为四格都过了**」在**运行器层面**的形态。

    | # | 脚本（文件:行号） | 打印的 `SKIP` 文案 | 判断 |
    |---|---|---|---|
    | 1 | `verify-color-token-normalize.ps1:42` | `SKIP FfmpegGui.exe 未构建（先 dotnet build -c Release --no-restore）` | **待定** |
    | 2 | `verify-color-token-normalize.ps1:43` | `SKIP 缺 ffmpeg（publish/PLAN/ffmpeg-full）` | **待定** |
    | 3 | `verify-gainmap-memory.ps1:43` | `SKIP FfmpegGui.exe 未构建（先 dotnet build）` | **待定** |
    | 4 | `verify-gainmap-memory.ps1:44` | `SKIP 缺 ServiceProbe（png3 打标要用）` | **待定** |
    | 5 | `verify-gainmap-memory.ps1:63` | `SKIP 4K 素材生成失败` | **待定** |
    | 6 | `verify-hlg-route.ps1:67` | `SKIP FfmpegGui.exe 未构建（先 dotnet build -c Release --no-restore）` | **待定** |
    | 7 | `verify-hlg-route.ps1:68` | `SKIP 缺 ffmpeg（publish/PLAN/ffmpeg-full）` | **待定** |
    | 8 | `verify-metadata-privacy.ps1:36` | `SKIP FfmpegGui.exe 未构建（先 dotnet build）` | **待定** |
    | 9 | `verify-metadata-privacy.ps1:37` | `SKIP 缺 exiftool（publish/PLAN/exiftool）` | **待定** |
    | 10 | `verify-metadata-privacy.ps1:46` | `SKIP 素材写入 GPS 失败（exiftool 未生效）` | **待定** |
    | 11 | `verify-png-signature.ps1:41` | `SKIP FfmpegGui.exe 未构建（先 dotnet build）` | **待定** |
    | 12 | `verify-png-signature.ps1:42` | `SKIP 缺 JxrEncApp（publish/PLAN/artifacts）` | **待定** |
    | 13 | `verify-png-signature.ps1:57` | `SKIP 素材生成不完整（src.png / anim.gif / src.jxr 三缺一）` | **待定** |
    | 14 | `verify-gif-avif-framelist.ps1:46` | `SKIP avifenc.exe 不在位 ⇒ 两步法无法执行，本门禁整批跳过（不给假绿）` **+ 完整汇总行** | **疏漏** ⚠ |

    **「有意 / 疏漏 / 待定」的依据**：
    - **#14 判「疏漏」** —— 它**自述**「本门禁整批跳过（**不给假绿**）」，而 `exit 0` 在**运行器层面**恰恰**就是**假绿 ⇒ **文案与行为直接矛盾**，属**文本级证据**，不靠推断。
    - **#1–#13 判「待定」** —— 措辞**显式写 `SKIP`**，像**有意**；但**本仓必跑脚本里只有这 6 个**在缺依赖时 `exit 0`（其余走 `exit 1` 或**自备**输入；⚠ 该结论的审计口径 = **2026-09-19 P4-A 接线前**的 **42 条**，接线后清单为 **48 条**，**新增 6 条的缺依赖行为未逐条复核**）⇒ 属**孤例**，无法从既有约定推出「有意」；且本条（第 68 条）的**硬口径**认为「读不到前置条件**应红**」。
      ⇒ **需人裁定**：统一成 `exit 1`（本仓硬口径），**还是**明确登记为「**有意的 SKIP 门禁**」并让运行器把 SKIP **计入**（第 76 条 ② 的 `PASS=n FAIL=0 SKIP=m` 口径）。
      ⚠ **修之前先问 team-lead** —— 涉及 6 个脚本 × 14 处，且会**改变干净环境下的整轮结果**。

    **处置：只登记，不派工**（沿用本条与第 81 条口径；`#25(0919)` 仅登记，**不动脚本**）。

    **同批登记的第三项（机制不同，一并列出）**：`_probe-jpg-p3-icc.ps1` **不检查子进程退出码** ——
    `:23`（ffmpeg 造源）与 `:25`（`ServiceProbe png3` 打标）都用 `Start-Process … | Out-Null`，**既无 `-PassThru` 也不判退出码**；`:27` 的 `Test-Path $in` **只兜住「没产出」这一种失败**。
    `:37-38` 那处**有** `-PassThru`，并把 `ffmpeg exit=$($pr.ExitCode)` **打印**出来 —— 但**只打印、不判**。
    ⇒ 定性 **「待定」**（它是**诊断型**脚本，定位是「给人读数」而非门禁）；⚠ **风险已被第 62 条缓解** —— 它现在进 `$tableOnly`，运行器会打 `← 表格型：无 PASS/FAIL 契约（exit=0 仅表示跑完）` 标签，读者**已被告知**。

69. **上游预缩放中继 ⇒ 下游死代码（一类盲区）（2026-09-17 登记；2026-09-18 重构为机制条目）**

    **机制（一句话）**：`QueueProcessor.cs:421-496` 有一道**所有后端共用**的预缩放中继，它**在格式分发之前**
    用**同一个** `FfmpegCommandBuilder.BuildMaxDimensionScaleFilter` 把超限图缩到临时文件，并在 `:487`
    把 `captured.InputPath` **改写**成缩放产物。⇒ **下游任何「用输入路径去探尺寸」的判据，恒为「不超限」。**

    **实例 ①（引擎侧几何段）**：`RawColorPipeline.cs:94-129`（引擎的「最长边几何缩放」）在**默认执行路径**上
    **永不执行** —— 引擎拿到时输入已被中继缩过 ⇒ `BuildMaxDimensionScaleFilter` 返回 **null**
    ⇒ 那段代码**从未被任何门禁走到**（改动它不会让任何断言变红）。

    **实例 ②（路由层拦截）**：`ColorEngineRouter.cs:341-348` 的 `GeometryWithGainMap` 拦截
    （`o.JpegGainMap && o.EnableMaxDimension && o.MaxDimension > 0`）**前置条件在产品里恒不成立**
    ⇒ 该拦截**永不触发**，`RawColorPipeline.cs:228-234` 那个「GainMap + 缩放」的 `throw` 亦**不可达**。
    ⚠ **表述准确性**：截至本次核对（`ColorEngineRouter.cs` mtime `2026-09-18 00:34:30`）**拦截仍在代码里**；
    正确说法是「**仍在代码里，但前置条件恒不成立 ⇒ 永不触发**」，**不是**「已被删除」。
    删它是排期中的 Part C；`ColorEngineRouter.cs:322-340` 已记录实测依据（临时把判据置 false ⇒ 走引擎 GainMap
    出口 ⇒ 退出码 0 / 4621B / `images=2` / 真 Ultra HDR）⇒ **删拦截无行为风险**。

    **反面对照（判据「活」的那一半）**：**动图跳过中继**（`:427-430` `maxDimSkipAnimated`：输出 gif/apng、
    `AnimationFps` 有值、输入 `.gif`/`.apng`、动图 `.jxl`/`.avif`）⇒ 下游 `BuildMaxDimensionScaleFilter`
    **照常返回非 null** ⇒ 引擎自身的几何段（实例 ①）与专用管线的缩放**真的会跑**。

    **实测锚点**（全部是可观测事实）：512x384@128 → **128x96**；@32 → **32x24**；384x512@32 → **24x32**；
    512x300@100 → **100x58**；负控（不超限）→ 尺寸不变且**无** `几何缩放（最长边限制）` 日志。
    ⚠ **边界坑**（源码注释已写、此前无门禁）：512x384 套**纵向**滤镜 `scale=-2:32` 实测 **42x32**，
    而按 `512*32/384 = 42.67` 自己算会得 **43/44** ⇒ 另一条边**只能**由解码产物长度反推，自己算必与 ffmpeg 漂开。

    **为什么危险**：死代码是「**将来**才承重」的代码 —— 一旦 legacy 的预缩放执行体被删除（这正是清理传统管线的
    下一步），实例 ① 立刻变成**唯一**的几何缩放实现，而它此前**零覆盖**。

    ⚠ **连带（教训从「一对」推广为「一组」）**：**上游中继一变，下游一组的生死同时翻转** ——
    同一个中继同时决定「实例 ① 死 / 实例 ② 死 / 那个 `throw` 不可达」，也同时决定「动图那一支活」。
    ⇒ **任何改动上游中继（`:421-496`）的清单，必须一并跑 `tests/scripts/verify-maxdim-coverage.ps1`**，
    不能只看被改的那几行。

    **通用技术（可复用，用于给死代码补覆盖）**：把「被测单元」从整条流水线里**摘出来直调**，并**自备素材**。本例 =
    `ServiceProbe geometry` 直接调用 `ColorMapping.RawColorPipeline.TransformFileAsync(input, output, plan.ToSpec(), plan, log, ct, 1, ff, options)`
    （`EnableMaxDimension=true` / `MaxDimension=N`），**绕过 `QueueProcessor`**；素材用 `lavfi testsrc2` 现造，
    **不依赖别的门禁的遗留产物**；输出走 GUID 运行级隔离（§6 第 66 条）+ 结尾按计数自清。
    ⇒ 断言 29 → **35**（2026-09-18 已增至 **52**）；配套专项门禁 `tests/scripts/verify-geometry-engine.ps1` **23/0**（已在运行器清单内）。

70. **「日志标记」单独不足以做门禁 —— 变异 M2 实证（2026-09-17）**

    **背景**：`geometry` 探针里有一条断言是「日志出现 `几何缩放（最长边限制）`」⇒ 证明那段代码真的跑了。

    **变异 M2**：把 `RawColorPipeline.cs:97` 的 `scaleFilter` 从解码 `-vf` 里**摘掉**（改成 `string vf = "format=rgb48le";`），
    模拟「缩放被静默丢掉」。**结果**：**「日志出现几何缩放」与「`Stats` 反推尺寸 == 实际产物尺寸」这两条仍然 PASS**
    —— 因为日志行由 `:128` 打、`Stats` 由 `:120-129` 按解码产物长度反推，两者**同源**；缩放丢了它们**一起**退化
    （都按「未缩放」自洽）。**只有尺寸断言转红**：探针 **23/12**（`psnr=5.44dB maxDiff=65293`）、门禁 **9/13**。

    ⇒ **口径**：**「日志有标记」是必要不充分条件**。尺寸/像素这类**外部可观测量**必须**成对**上断言
    （「日志 + 尺寸」两条都要有），否则「代码没跑」与「跑了但结果错」在门禁上**不可区分**。

71. **顺序更正（本轮最重要）：legacy 的「映射→缩放」今天是休眠的 —— 删预缩放才会唤醒它（2026-09-17）**

    **旧口径（错，已推翻）**：「真正的反序只出现在 legacy 路径，**与删不删预缩放无关**」。

    **正确口径**：legacy 的反序确实写在代码里 —— `FfmpegCommandBuilder.cs:121` 先 `AppendVideoFilter` 色彩链、
    `:530-536` 再追加 `scale`，而 `AppendVideoFilter` 对 `-vf` 是**往同一个字符串后面追加**（`:1573-1599`）
    ⇒ 得到 **映射→缩放**。**但今天它不生效**：预缩放已经把 `captured.InputPath` 换成缩放产物
    ⇒ `:535` 的 `BuildMaxDimensionScaleFilter` 返回 **null** ⇒ `:535` **从不被调用**。
    ⇒ **删掉 `QueueProcessor.cs:429-496` 才会唤醒它**，非引擎通路的顺序随即翻转为 **映射→缩放**。

    **顺序是主导项（2×2 隔离；同源 512x384、`MaxDimension=128`、lanczos；SDR 用例 = sRGB→Display P3）**：

    | 对 | 含义 | PSNR | maxDiff |
    |---|---|---|---|
    | c1 ↔ c2 | **顺序**（缩放→映射 vs 映射→缩放） | **36.47 dB** | **14417**/65535 |
    | c3 ↔ c4 | **顺序** | **36.49 dB** | **13749**/65535 |
    | c2 ↔ c4 | 量化往返（同序） | 53.87 dB | 1164/65535 |
    | c1 ↔ c3 | 量化往返（同序） | 61.75 dB | 282/65535 |

    ⇒ 顺序差 ≈ **36.5 dB / ~1.4 万（8-bit 下 ≈ 55 LSB）**；量化往返只值 **1–4.5 LSB** ⇒ **顺序是主导因素**。
    ⇒ `c1 ↔ c3 = 61.75dB / 282` 与引擎探针自测的 `61.55dB / 280` **相互印证**（同量级、同方向）
    ⇒ **反证引擎的两条路径没有反序**（都是 缩放→映射）。

    **HDR→SDR 复测（2026-09-17 本轮；源 = PQ/BT.2020 512x384、`--color-tone-map hable`、`MaxDimension=128`、lanczos）**
    —— 与上表**同构**，只把色彩链换成 tonemap 链（链取自产品自身 legacy 路径对**真实 HDR→SDR 计划**产出的 `-vf`：
    `format=yuv444p,tonemap=hable:param=0.5,format=rgb48le,zscale=pin=bt2020:tin=linear:min=gbr:p=bt709:t=iec61966-2-1:m=bt709`）：

    | 对 | 含义 | PSNR | maxDiff |
    |---|---|---|---|
    | c1 ↔ c1b | 确定性控制（同链重跑） | **∞** | **0**/65535 |
    | c1 ↔ c2 | **顺序** | **35.96 dB** | **12227**/65535 |
    | c3 ↔ c4 | **顺序 + 量化** | **35.99 dB** | **12239**/65535 |
    | c2 ↔ c4 | 量化往返（同序） | 57.33 dB | 831/65535 |
    | c1 ↔ c3 | 量化往返（同序） | 58.72 dB | 2874/65535 |

    ⇒ **HDR→SDR 的顺序量级（35.96dB / 12227）与 SDR（36.47dB / 14417）同量级** ⇒ 结论不因 HDR 而改变。

    ⚠ **该对照的成立边界（重要）**：上面两个 2×2 都是**「同一 tonemap 算子、只换顺序」的纯顺序隔离**。
    **不能**拿「引擎产物 vs legacy 产物」的 PSNR 去量顺序 —— 那**在本仓不成立**，原因是**算子不同**：
    引擎的 Hable 是**进程内**实现、且用**整帧实测峰值**
    （日志原文：`峰值来源=整帧实测：10000nit ⇒ tonemap=Hable peak=10000nit white=203nit headroom=49.26`），
    而 legacy 用 ffmpeg `tonemap=hable:param=0.5`（**不带** `peak=`，走 ffmpeg 自身默认）
    ⇒ 两路产物的差里**混着算子差**。另：**今天两条产品路径都已经是「缩放→映射」**
    （legacy 的 `scale` 被预缩放挪进了**另一条** ffmpeg 调用）⇒ 默认路径上**本来就没有顺序差可测**。

    **结论句**：**删 `QueueProcessor.cs:429-496` **不是**纯引擎侧改动；非引擎通路的顺序翻转需**先统一顺序**（目标 = 引擎的 `scale→map`）**再删**，或由维护者接受该像素变化。**
    ⚠ **不要**把它读成「可以安全删除」—— 该判断在本仓不成立。

72. **`verify-color-wiring.ps1:160`「分不清是哪一层缩放」—— ✅ 2026-09-19 复核：担忧不成立，且已改为打印实命中层**

    ⚠ **复核结论（原担忧不成立）**：原登记说「本条用 `MaxDimension|几何缩放` 做**或**匹配
    ⇒ 分不清是哪一层 ⇒ 删了预缩放、但引擎那段没跑也会假绿」。
    但**紧邻的 `:158` 已断言实测宽高 == 32,24**（源 512x384、最长边锁 32）
    ⇒ 若引擎几何段没跑**且**预缩放被删，尺寸会是 512x384 ≠ 32,24 ⇒ **`:158` 必红**
    ⇒ **假绿的路径不存在**；本条只需保证「可追溯」。

    ⚠ **今天（预缩放还在）不能收紧为「必须命中 `[色彩] 几何缩放`」**：默认路径下缩放由**预缩放层**完成、
    引擎几何段**不跑**（日志只有 `[MaxDimension]`）⇒ 收紧会**假红**。⇒ **真正的收紧时机 = 删预缩放时**。

    **✅ 2026-09-19 已改**：保持判据不变（仍只要求「命中其一」），但改为**打印实命中的层**：
    `$geoLayer = if ($r2.log -match '\[MaxDimension\]') { 'prescale' } elseif (... ) { 'engine' } else { 'none' }`
    ⇒ 实测输出「**实命中层: prescale**」（证实了上面的分析），且 `verify-color-wiring` **104/0** 不变。

    `:160` 用 `MaxDimension|几何缩放` 做**或**匹配 ⇒ **预缩放层**与**引擎层**都能让它绿
    ⇒ 它**证明不了**缩放发生在哪一层。**在删预缩放之前**应把它收紧为**必须命中 `[色彩] 几何缩放`**
    （引擎层日志的前缀），否则「删了预缩放、但引擎那段没跑」也会**假绿**。

73. **今天的顺序按分支而异（登记；删预缩放前必须先统一）**

    | 情形 | 预缩放 | 顺序 |
    |---|---|---|
    | 预缩放**成功**（默认路径、非动图输出） | 跑 | **缩放→映射**（两条管线一致） |
    | 预缩放**失败**（`QueueProcessor.cs:492`） | 失败、输入**不变** | **引擎**：缩放→映射（解码 `-vf "format=rgb48le,scale=…"`）；**legacy**：**映射→缩放**（`:535` 被唤醒 ⇒ `-vf "…色彩链…,scale=…"`） |
    | 动图输出（gif/apng） | **从不跑**（`IsAnimated` 判的是**输出**格式） | **缩放→映射**（引擎几何段**今天就是活的**） |

    **实测证据（2026-09-17）**：
    - **预缩放失败**（载体 = 截断的 16-bit PQ PNG：ffprobe 仍读得到 `512x384 / rgb48be / smpte2084 / bt2020`，
      但 ffmpeg 解码失败 ⇒ 预缩放退出码 69）：日志 `[MaxDimension] ⚠️ 预缩放失败 (退出码 69)，将尝试原图继续` 之后，
      **legacy** 主命令 = `-vf format=yuv444p,tonemap=hable:param=0.5,format=rgb48le,zscale=…,iccgen,scale=128:-2:flags=lanczos`
      ⇒ **映射→缩放**；**engine** 主命令 = `-vf "format=rgb48le,scale=128:-2:flags=lanczos"` ⇒ **缩放→映射**。
      （这是 `QueueProcessor.cs:492` 那条分支**首次实测**；此前只有读码推断。）
    - **gif 输出**（带 alpha 的 P3 PNG → GIF，引擎出口）：日志里**没有** `[MaxDimension]` 行（预缩放被跳过），
      而 `[色彩] 几何缩放（最长边限制）：512x384 → 128x96（scale=128:-2:flags=lanczos）` **在** ⇒ 引擎几何段今天就在跑。
      最终命令行（原样照抄）：
      `-filter_complex "[1:v]scale=128:-2:flags=lanczos,format=rgba,alphaextract[a];[0:v]format=rgb24[b];[b][a]alphamerge,split[s0][s1];[s0]palettegen=reserve_transparent=1[p];[s1][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle"`
      ⇒ 这里的 `scale=` 落在 `[1:v]`（**alpha 分支**，只为与已变换的 128x96 RGB 对齐尺寸；
      见 `RawColorPipeline.cs:823-835` 的注释「几何缩放在解码步已作用于变换后的像素 ⇒ alpha 分支必须套**同一个** scale」），
      **不是**第三套色彩顺序；色彩像素仍走解码步 `-vf "format=rgb48le,scale=…"`（缩放→映射）⇒ **与引擎一致**。

74. **断言「载体」选错 ⇒ 命中另一个合法 token ⇒ 断言恒真（比恒红更危险）**（2026-09-17 登记；顺序统一落盘时的前置约束）

    同族三条，全部经读码确认，且**会在同一批改动里同时存在**：

    | 形态 | 具体 token | 为什么裸判据会误命中 |
    |---|---|---|
    | **同一 token 内的子串** | `zscale=` | `zscale=` 含子串 `scale=` ⇒ 裸 `IndexOf("scale=")` 恒真 |
    | **另一个合法的 `scale=` token** | `Decision.cs:316` 的 `preScale`（`scale=ceil(iw/2)*2:ceil(ih/2)*2:flags=lanczos`，输出矩阵为 bt709/bt2020* 时插入）与 `ImageEncoderArgs.cs:227` 的 `scale={AnimationScaleW}:-1:flags=lanczos` | 它们**也是** `scale=…:flags=lanczos` ⇒ 「`scale=` 出现一次」「`flags=lanczos` 出现一次」两种写法**都会误红** |
    | **同一串里的另一段** | 引擎 gif 的 `-filter_complex` 里 `[1:v]scale=128:-2:flags=lanczos`（alpha 分支尺寸对齐，见 `RawColorPipeline.cs:823-835`） | 它**完整匹配**最长边缩放的判别式 ⇒ 若断言去 complex 里找 `-2` scale，命中的是 alpha 分支 ⇒ **顺序断言恒真** |

    ⇒ **规矩**（顺序统一的门禁按此写）：
    ① **断言不许写「某串出现 N 次」**，只能写「**判别式命中的是哪一段**」；
    ② **载体按管线显式分派**，不许「有 `-vf` 就用、否则用 complex」一刀切 ——
       引擎侧载体 = **解码 `-vf`**（引擎 gif 命令行里 `-vf` 与 `-filter_complex` **同时存在**）；
       legacy 侧载体 = **`-filter_complex` 的 head**（且先剥掉字面双引号）；
    ③ **每条正向断言都要配一条反向断言**（"另一个为什么不该被取"）：例如对引擎 gif，
       断言那个 `-2` scale **出现在 `[1:v]` 段、不属于色彩链**。
       —— 正向只证明"取到了对的载体"，**反向才证明"我知道另一个为什么不该取"**；
    ④ **判别式用数值形态**：`^scale=\d+:-2:flags=lanczos$` / `^scale=-2:\d+:flags=lanczos$`
       （`BuildMaxDimensionScaleFilter` 的两种输出）。**`-2` 是分水岭**：对 `-1`（`AnimationScaleW`）
       与表达式形态（`preScale`）都能分开；
    ⑤ **变异里必须有 M-e**：故意让断言去命中引擎 gif 的 alpha 分支那个 `-2` scale ⇒ **必须仍为绿**
       （证明载体分派有效，而不是靠运气）。

    ⚠ **本条与第 65、70 条是三个不同的失效面，不要混**：
    - 第 65 条 = **断言的前提本身写错**（把事实写错了）；
    - 第 70 条 = **判据来源选错**（该读命令行却读了日志标记）；
    - 本条 = **来源对了、载体选错**（在正确的命令行里取了另一个合法 token）⇒ 断言**恒真**。
    ⇒ 「恒真」比「恒红」更危险：恒红至少有人看见，恒真会一直**装作在守门**。

    ⚠ **状态**：顺序统一（把 legacy 的 `映射→缩放` 改为 `缩放→映射`，规则 R1 = 插在链首
    `format=<…>,` 之后；**不是**插到串首 —— 插串首会让缩放落在解码器原生像素格式上，
    与引擎在 rgb48le 上缩放**不是同一个像素结果**）**尚未落盘**（队列第三位）
    ⇒ 本条现在是**前置约束**，不是"已实现"。落盘时按上列 ①~⑤ 实现 8 条断言 + M-a~M-e 五个变异。

75. **清单外矩阵脚本依赖外部素材 `<local-dir>`（人工验收环境前提，非缺陷）**

    `verify-remaining.ps1:39`（`$dng = "<local-dir>\RGBS6695-HDR\RGBS6695-HDR.dng"`，`:41-46` 六格 DNG→各格式）、
    `run-full-matrix-test.ps1:140-152`（10 项素材清单）、`run-full-matrix-test-safe.ps1:165/167/169/171/173/206/209`、
    `final-verify.ps1:65-66`（L4-404 用同一 DNG）。

    ⚠ **这 4 个都不在 `_run-step3-gates.ps1` 的 53 条必跑清单内** ⇒ **常规门禁批量跑不受影响**；受影响的是"全量 / 矩阵级"**手动验收**。
    （⚠ 演进：原写 **32 条** → 2026-09-18 更正为 **33 条**（+`verify-engine-firstframe.ps1`）→ **34 条**
    （+`verify-jxl-codestream-route.ps1`）→ **35 条**（+`verify-anim-static-target.ps1`）→ **36 条**
    （+`verify-jxl-probe-timeout.ps1`）→ **37 条**（+`verify-ffmpeg-heartbeat.ps1`）→ **38 条**
    （+`_probe-sync-read-before-wait-scan.ps1`）→ **39 条**（+`verify-jxr-anim-input.ps1`）→ **42 条**（+`verify-webp-anim-probe.ps1`）
    （+`_probe-ct-chain-closure-scan.ps1`）→ 现 **48 条**（**2026-09-19 P4-A 接线 +6**：`verify-ui-host.ps1` / `verify-ui-param-matrix.ps1` / `verify-ui-strategy-map.ps1` / `verify-ui-param-defects.ps1` / `verify-cli-strict.ps1` / `verify-png-structure.ps1`）
    → **49 条**（2026-09-20 +`verify-gainmap-isobmff.ps1`）→ **50 条**（2026-09-22 +`verify-engine-alpha-preserve.ps1`）→ **51 条**（2026-09-22 +`_probe-tool-detect.ps1`）→ **52 条**（2026-09-23 +`verify-startup-warmup.ps1`）→ **53 条**（2026-09-23 +`verify-ipc-extra-options.ps1`）；**每次增删必须重新数**。
    ⚠ **2026-09-22 复核时新发现一处疑似漏接线（未处置，待维护者定）**：`verify-ipc-extra-options.ps1`
      文件头自述「P1-C … 回归锁（2026-09-19 新增）」，且形态上是完整的 C 类契约（自身 `PASS=` 汇总 +
      `RESULT` 行 + 断言驱动的 `exit 1`），但它**既不在**同日 P4-A 的 +6 名单里、文档中也**查不到任何**
      "有意不入列"的登记 ⇒ 无法区分"漏接线"与"有意手跑但忘了记"。⇒ 本轮**未擅自接线**（接一条 = 清单 52、
      且要先实测它的时长与稳定性），只在此点名。核对方法：`powershell -File tests/scripts/verify-ipc-extra-options.ps1` 跑通后按 §6 的 C 类口径登记。））
    ⚠ **维护者已裁定：不参数化**（本机硬编码路径保留）⇒ 它们是**人工验收脚本**，不是常规门禁。
    ⚠ 与 §6 已登记的「**清单内**脚本缺素材时仍 80/0」**不是一回事**：清单内脚本**自备素材**（`ensure-fixtures.ps1` / `_lib-color-assets.ps1`）；
    **清单外**这 4 个依赖 `<local-dir>`，且**没有任何一处登记过"缺该目录时的预期行为"（skip 还是红）** —— 这是本条要补的**具体缺口**。

76. **改动前先做「回归预判」—— 两个方向：会不会弄红别人的门禁 / 新门禁会不会自己假绿（2026-09-18 登记）**

    规矩：任何"**往命令行里插一个滤镜 / 改一条命令行**"的改动，**落盘前**先只读回答两问。

    **① 会不会弄红已绿的门禁？** —— 要看**判据的形状**，不是看它"在不在"。
    实例（往 legacy JXR 的 `-vf` 里插最长边缩放）：`verify-color-wiring.ps1:569-571` 的判据是
    `Where-Object { $_ -match '\[cmd\] ffmpeg' -and $_ -match 'zscale=primariesin=bt2020' }` 再 `CK ($x2ff.Count -ge 1)`
    ⇒ **对整行做子串匹配** ⇒ 把 `scale=…` 插到 `zscale=` **之前**，子串 `zscale=primariesin=bt2020` **仍在** ⇒ **保持绿**；
    `:560-562` 读的是 ffmpeg 的**流信息行**（`Video: tiff, rgb48le(pc, gbr/bt709`），加一个 scale 滤镜不改像素格式标注 ⇒ **不受影响**。
    ⇒ 结论可以是"不会弄红"，但**必须核过再说** —— "跑完发现红了再解释"会白烧一整轮。

    **② 新门禁会不会自己假绿？** —— 每条断言都要问"**什么情况下它会无条件成立**"。三个实测形态：
    - **素材退化**：`产物最长边 == 64` 在**素材最长边恰好 == 64** 时，**完全不缩也满足** ⇒ 素材是**自备**的，
      将来有人改素材尺寸，断言就**静默变成恒真**（"恒真比恒红更危险"）。⇒ 必须加**素材自检**
      （`源最长边 == 256`、`!= 64`、`> 64`）+ 成对断言 `产物尺寸 != 源尺寸`。
    - **析取式退化**：`(缩了) or (硬失败并点名)` 在**产品因别的原因全失败**时**也成立** ⇒ 必须加**伴生正面断言**
      （`exit == 0 && 尺寸 == 64`），证明基线走的是"缩了"那一支，而不是靠"硬失败"那一支凑过规则。
    - **跳过退化**：SKIP 若不进汇总，读者看到"全绿"会以为**四格都过了** ⇒ 汇总行必须形如 `PASS=n FAIL=0 SKIP=2`，
      且注释写明"**SKIP=2 时本门禁只覆盖 2/4 格**"、原因**逐格**打印（不合并成一句）。

    ⚠ **非退化必须用数值判，不能用字符串比**（`verify-gainmap-engine.ps1:291-295` 的实例）：
    探针格式是 `0.####` ⇒ SDR（headroom=1）会打成 **`0`** 而**不是** `0.000` ⇒ 写成 `$relayMax -ne "0.000"` 会**漏判**
    （正好把这条锁的牙拔掉）；正解是 `[double]::TryParse` 后判 `-gt 0`。
    ⇒ **同类提前排雷**：`ServiceProbe psnr` 对**逐位相同**的两图打印 `psnr=inf`，而 .NET 的
    `[double]::TryParse("inf")` **返回 false**（只认 `"Infinity"`）⇒ 将来若有门禁把 PSNR **数值化**，会被当成
    "读不到"而**静默降级**（同一种拔牙）。⇒ 处置：要么先 `-eq 'inf'` 判等再 `TryParse`，要么**只做字符串相等**
    （`c1`/`c1b` 这类控制组本就是"必须完全相等"）。

    ⚠ 与第 65、70、74 条的区别：本条讲的是**改动前的动作缺失**（不做预判），不是断言本身的写法。

77. **位置断言必须先断言「形态」—— 分支载体 ≠ token 载体（2026-09-18 登记）**

    规矩：**凡要断言"某个滤镜插在链里的位置"，必须先断言"我命中的是预期的链形态"**；否则形态一变，
    断言会落到**另一条分支**上并通过。

    实例（legacy JXR 格）：该格命令行形态**有两种**，取决于 `ProcessJxrAsync:1890-1896` 的 `zscaleFilter` 是否为 null：
    - 16-bit + auto ⇒ `zscaleFilter != null` ⇒ `-vf "format=rgb48le,zscale=…,format={pixFmt}"` ⇒ 链里**有**色彩变换
      ⇒ 位置判据落在 `mapIdx >= 0` 分支；
    - 8-bit（或 HDR 且 `colorArgs` 非空）⇒ `zscaleFilter == null` ⇒ **原本没有 `-vf`** ⇒ 注入后是裸 `-vf "scale=…"`
      ⇒ 位置判据落在 `mapIdx == -1` 分支（此时要求 `scaleIdx == 0`）。
    ⇒ 若素材从 16-bit 换成 8-bit（**自备素材，将来完全可能**），断言会**静默换分支**并在错的那一支上通过。
    ⇒ 正解：该格**先断言形态**（命令行必须含 `zscale=primariesin=bt2020`，证明落在 `mapIdx >= 0` 分支），**形态不符即红**。
    ⇒ 同一批的顺序统一改动（S1/S2/S3 各格）也要照此加一句**形态自检**。

    同族补充实例：**子串判据对位置完全不敏感** —— `verify-color-wiring.ps1:569-571` 是 `-match` 整行 + `-ge 1`
    ⇒ 把 `scale=…` 插到**串首或链尾**它**照样绿** ⇒ 它**既不查尺寸、也不查位置**，**不能**当位置判据用
    （要断言位置，必须用**位置**判据，而不是子串判据）。

    ⚠ 与第 74 条的**区别**（**并列，不合并**）：第 74 条讲 **token 载体** —— 选错了**同一串里的哪一段**
    （`zscale=` 的子串 / 另一个合法 `scale=` token / alpha 分支那段）；本条讲 **分支载体** —— 落在了**哪一条代码分支**上。

78. **「可核对记录」的两个过期陷阱：位移不得跨轮相减；引用行号必须带快照时刻（2026-09-18 登记）**

    **概念**：**记录本身会撒谎** —— 一个关于**数字**（位移记账），一个关于**引用**（行号）。
    两者补救形状同源：**不要相信会过期的产物，锚到稳定的东西上**。

    **陷阱 ①（位移记账）**：判断一次落盘的位移，**不得**用「上一轮记的字节数/行数」相减 —— 那组基线**本身会过期**
    （别人改过、或上一轮快照略旧）。
    实例：第 69 条重构为机制条目时，上一轮基线 `284524 B / 3080 LF`，而 `284524 + 1876 = 286400 ≠ 286403`、
    `3080 + 20 = 3100 ≠ 3101` ⇒ 差 **3 B / 1 行**，落在**第 70–74 条之间**，与本次改动无关。
    ⇒ **正确做法（无基线依赖）**：以「**我替换掉的那一整块 vs 新块**」自证
    （旧块 `2171 B / 20 行` → 新块 `4048 B / 41 行`）；
    **成对**再做第二个独立检查：**「被替换块之下各条目的位移 == 该差值」**（一个看字节、一个看条目位置）。

    ⚠ **本陷阱会诱发的有害动作（门禁诚信问题，不是记账精度问题）**：
    基线过期时，**正确**的落盘会报出**假的「账不平」** ⇒ 注意力被引向不存在的差额；
    更坏的是若有人**为了让账平而去改基线** —— 那就是「**改账本止红**」，
    与「**为了绿而放宽断言**」是**同一个动作**，只是换了个对象（账本 vs 断言）。

    **陷阱 ②（引用行号）**：引用**他人（以及自己早前）写的行号**时，那是**核对时刻的快照**，一改就漂。
    实例：第 69 条实例 ② 引用 `gainmap-merge-partA` 的 `ColorEngineRouter.cs:341-348`（拦截）
    与 `:322-340`（实测更正注释），其 Part C 一落地这两处**必然移动**。
    ⚠ 对**自己**同样成立 —— 第 69 条正文里引用的行号，在下次编辑该文件时同样会漂；
    不要以为「自己写的行号是可靠的」。
    ⇒ **做法**：① 引用时**并列写 mtime / 快照时刻**；② 给**锚点文本**（如「`GeometryWithGainMap` 拦截」）
    而不只给行号；③ 条目里显式写「**行号是快照，编辑时以锚点为准**」。
    ⚠ **做法 ③ 的最强例证（2026-09-19 实测，**自指性**）**：**写下「记录该坑的那段文字」这一动作本身，就把该段里的行号推走了** ——
    当天我在本节新增「`#NN` 的 5 类例外」那段时，段内写了一个**指向本文件内逐字引文 `// #15 心跳站点 <X>` 的行号**；
    而**那段文字自身让文件 +7 行** ⇒ 我写下的那个行号**当场失效**（该逐字引文实际被推到更下面）。
    ⇒ 结论：**越是在讲「行号会漂」的文字里，越不能写行号**，只能给**锚点文本**。
    （本次已照此改：该段现在**只给锚点**、不给行号；段尾留了同因说明句，两处**互指**。）

    **⚠ 2026-09-19 补：上「做法 ①」**不足**，已升级；并新增**第三个**易失标识符（**编号**）**：
    - **「日期快照」不能标识版本 ⇒ 改用「指纹锚定」**：写「2026-09-19 快照」时那个版本**只存在了几分钟**，
      读者**无法判断自己那份是不是同一版**（本会话真实踩到：落盘那一刻行号就已失效）。⇒ 并列写
      **`SHA256 前 16 位 + 字节数 + mtime + 行数`**，并声明「**指纹一变，下列行号即作废**」。
      实例：运行器 `SHA256=C999C35BF6C49506…` / 57518 B / mtime `2026-09-19 12:15:44` / 671 行（**已冻结**）。
      ⚠ **指纹锚定还顺带定位影响面**：同日一次 +19 行的改动**全落在某函数之后** ⇒ 该函数内那 5 个行号**一个都没动**、
      只有调用点变 ⇒ **不必重写整段**。**这是「日期快照」给不了的信息。**
    - **`#NN` 编号也是易失标识符，且会跨会话二义**：实测 `#22` / `#23` 在 **2026-09-18**（gp-1 的只读审计 /
      `_probe-sync-read-before-wait-scan.ps1`）与 **2026-09-19**（运行器 watchdog / `$tableOnly` 补全）**指两件完全不同的事**。
      ⇒ **约定（2026-09-19 定，team-lead 裁定）**：**`#NN` 为会话内任务号。自 2026-09-19 起，文档中的 `#NN` 引用
      一律带日期后缀（如 `#20(0919)`），无需先判断是否存在同名者；此前的历史节保持原样。**
      ⚠ **但「`#NN` 要加后缀」只对**会话任务号**一义成立** —— 实测**有 5 类不得加**（加即改坏语义）：
      ① **表格内的行号 / 项号**（如第 68 条 14 行表里的 `#1`–`#14`、第 65 条附近的 `#2/#3` 与 `#4–#9`）；
      ② **ffmpeg 流索引**（如 `TESTING.md` 里 `[in#0/png_pipe]` 的 `#0`）；
      ③ **基线抬升号 `raise#N`**（如 `TESTING.md` 里的 `raise#3` / `raise#4`）；
      ④ ⚠ **逐字引文（源码注释原文）—— 5 类里优先级最高**：已知**两处** —— `TESTING.md` 与 `HANDOVER.md` 各有一处
      `// #15 心跳站点 <X>`（**源码注释的逐字引文**）⇒ **不得加后缀**，否则读者照它去 grep 源码就找不到了；
      另加**本条「正面写法 ②」所引的 ⑬⑭ 注释**里含裸 `#23`（同为源码注释原文）⇒ **同样不加**（该处已就地声明）。
      ⇒ **冲突时引文保真优先于消歧**（与「引文必须逐字」同源，**这是例外里的最高优先**）。
      ⚠ **本类易漏**：每新增一段源码注释引文，都可能带进一个新的裸 `#NN` ⇒ **引文落地时要顺手登记**，否则本清单自己就变成「**不完整 ⇒ 自相矛盾**」。
      ⑤ **待办 / 未完成的任务**：所指**工作尚未发生** ⇒ 无「工作所在会话」可言（如 `HANDOVER.md` 里的 `#16`「探测/降级语义三态化」，
      0918 已记「本轮**未动**」）⇒ **不加**。
      ⚠ 本段**刻意只给锚点文本、不给行号** —— 因为**写下这段的那一刻，我自己的这次编辑就把行号推走了**（同一任务内「改文件」与「引行号」互相踩）。
      ⇒ **这正是本条陷阱 ② 的「做法 ③」的最强例证**（**自指**：在记录该坑的段落里踩同一个坑）—— 详见上文「做法 ③ 的最强例证」那段，**两处互指**。
      ⇒ 执行口径：**先判「这个 `#NN` 是不是会话任务号、且其工作已完成」，是才加后缀**（这一步判断无法免除，因为上列 5 类会被加错）。
      ⇒ **并推荐：能写描述性名字就不写编号**（「运行器 watchdog」而非「`#22(0919)`」）—— 与「**能引符号就不引行号**」**同一条原则**：
      **编号 / 行号 / 日期快照都是易失标识符；符号名、判据原文、文件指纹才是稳定的。**

    **⚠ 正面写法（2026-09-19 补，team-lead 裁定收录）：上文全是「别做什么」，此处补「该做什么」** ——
    **① ②** 出自运行器**自身形态自检**（`Test-RunnerShape`，`#22(0919)` / `#23(0919)`）、**③ ④** 出自本次文档工作**自己的实测踩坑**。
    ①② 与第 74 条**同族**（**断言恒真 / 恒红**）；区别在**命中的是谁**：第 74 条命中**另一个合法 token**（`zscale=` ⊃ `scale=`），
    ①② 命中**判据自己** —— 因为自检读的就是**运行器自身原文（含注释）** ⇒ **判据与被判据对象是同一份文本**。
    （下引源码注释均为**原话**；已略去 `#` 前缀与换行。⚠ 引文内出现的 `#23` 是**注释原文**，按**本条第 ④ 类例外**，**不加日期后缀**。）

    - **正面 ①：扫描「自身源码」时，判据串一律用拼接构造（自匹配抑制）**
      - **反例**：自检里直接写 `$t.IndexOf('$p.ExitCode')` —— 该字面量**就在被扫描的同一个文件里** ⇒
        即使源码里根本没有那种取法，**判据自己的字面量**也把它满足了 ⇒ 「不得出现」型**恒红**、「必须出现」型**恒绿**。
      - **正确写法**：拼接，让判据在文本里**不连续出现** —— 实测该函数**每一针**都这么写
        （**有效窗口：2026-09-19 12:19 那次读数，共 10 处**；⚠ **该计数随针数增长而失效** —— 第 15 / 16 针落盘后已**不是 10**；
        **不要据此重数**，针数以运行器打印的 `shape-selfcheck=OK（N 针…）` 为准：2026-09-19 13:46 权威整轮 = **16 针**）：
        `'$' + 'p.ExitCode'` · `'$' + 'proc.ExitCode'` · `'$' + 'child.ExitCode'` · `'Write' + 'AllText'` ·
        `'ReadAllText($' + 'rcFile'` · `'function Invoke-Step' + 'WithTimeout'` · `'task' + 'kill'` ·
        `'汇总解析' + '已抑制'` · `'[int]$ProbeTimeoutSec' + ' = 120'` · `'[int]$ScriptTimeoutSec' + ' = 480'`。
      - **⭐ 活的实例（2026-09-19 实测，两条计数即可复算）**：上文是**事后举例**，这里补一条**当下仍在源码里**的 —— **第 15 针（读失败不沿用）自己就是证据**：
        在运行器原文里，`输出文件读取失败`（**裸串**）命中 **3** 处，`输出文件读取失败，汇总不可得`（**连续串**）命中 **2** 处；
        差额那 **1** 处正是**针自己的定义**，写作拼接式 `$nReadFail = '输出文件读取失败' + '，汇总不可得'`。
        ⇒ 判据若写成**连续串**，就会被**自己的定义**满足（「必须出现」型**恒绿**）；实测该针用的是**拼接式** ⇒ **有牙**。
        ⚠ 复算方式：对 `tests/scripts/_run-step3-gates.ps1` 跑两条 `grep -c -F` 比**差值**（**不引行号** —— 行号是易失标识符，见本条陷阱 ②）。
      - **为什么（源码注释原话）**：「本函数读**运行器自身原文（含注释，不剥注释 —— 故意比 ps-compat 更严）**，所以**所有针都必须用拼接构造**：否则针的字面量会被本函数自己的源码满足 ⇒ 断言退化成同义反复（「不得出现」型恒红、「必须出现」型恒绿）。⚠ 同理，注释里也不得写出那些针的**连续**形式」。
        ⇒ **连带约束**：注释里也不能把那些针写成**连续**形式（否则注释本身就把针满足了）。
      - **变体（同一原则的第二种实现）：在正则里插入「不会自匹配的量词」** —— 实测另有两针**未用拼接**、改用量词：
        `'=\s*Invoke-StepWithTimeout\s'`（须 ==2）/ `'-TimeoutSec\s+\$'`（须 ==2）。
        理由：这两个**字面量自身**就在源码里，而 `=` / `-TimeoutSec` 之后紧跟的是 `\`（反斜杠）、**不是空白** ⇒
        `\s*` 只能匹配 **0 个**、`\s+` 需 **≥1 个** ⇒ **自匹配失败** ✓
        ⇒ **实证**：自检**要求**其计数 ==2，而运行器**实测通过**（`shape-selfcheck=OK（14 针…）`）⇒ 真实匹配数就是 **2**（两处调用点），**没有被自己的字面量顶成 3**。

    - **正面 ②：在「自身源码」里数条目时，先取【数组字面量区间】、再在区间内计数**（`#23(0919)` 的第 ⑬⑭ 针）
      - **反例**：在全文中数 `\.ps1'` ⇒ 会被**自检函数自己的源码**（正则字面量、注释里的示例）污染 ⇒ 计数虚高。
      - **正确写法（判据原文）**：先 `[regex]::Match($t, '(?ms)^[ \t]*\$tableOnly\s*=\s*@\((.*?)\)')` 取出**区间**，
        再**只在** `$mTable.Groups[1].Value` **内**数 `"\.ps1'"`（`$noSelfSummary` 同型）——
        源码注释原话：「⚠ ⑬⑭ 的理由（#23 的**全部意义**）：这两个数组就是**防读者误读**用的；删掉数组里一条 ⇒ 该条的标签**静默消失** ⇒ 读者又会误读 ⇒ 与「防误读」的初衷相反。⚠ 这两针**在数组字面量内部计数**（先正则取出 `@( … )` 区间，再数区间内的 `.ps1'`）⇒ **天然不受本函数自身源码影响**，不存在「针自己的字面量把自己满足」的同义反复问题」。
      - **为什么**：把**扫描域**缩到**被断言的区域** —— 等价于「**只数该数的地方**」；且取不到区间时 `else { -1 }`
        让「**没取到**」**响亮失败**，而不是静默算成 `0` ⇒ 假绿。
      - ⚠ **配套坑（控制组实测抓到）**：`(?m)` **不够** —— `$noSelfSummary` 的声明**跨 3 行**，`(.*?)` 要跨行**必须 `(?ms)`**
        （只有 `(?s)` 才给 dotall）；只写 `(?m)` ⇒ 该正则**永不匹配** ⇒ 计数恒 **-1** ⇒ **未变异的原文也会拒绝跑**。
        ⇒ **第二条纪律：锁写好后，必须拿「未变异的原文」跑一次控制组** —— 否则会把「**锁写坏了**」误当「**变异被抓到**」。

    - **正面 ③：核对一个对象时，把「属性」逐项列明 —— 核了集合 ≠ 核了顺序**（2026-09-19 实测踩到）
      - **实例**：我把 A 类 4 条与 `$tableOnly` 对齐时只核了**集合**，却在文档里顺手写了「**顺序亦一致**」；
        用真实日志交叉验证才发现**日志出现序 ≠ 数组声明序** —— 日志按 **`$scriptList` 执行序**，而数组声明序是 `verify-colorfmt-matrix` 打头。
      - **教训**：**集合相等 ≠ 顺序相等 ≠ 条数相等** ⇒ 把**关心的属性逐项列出**，核过哪一项就只声明哪一项，**不得由「核了一项」推出「全部一致」**。
      - ⇒ **一句话**：**核对是一种「抽样」，声明只能是「样本内的」** —— 核了集合，就只能声明集合。

    - **正面 ④：任何取证，先做一次「对象是否稳定」的前置验证**（2026-09-19 实测踩到；team-lead 裁定**升格为独立判据**）
      - **实例**：我用 `step3-round-final-121906.log` 做 A/B tag 的交叉验证、报 B 类 = **3**；再读一次变成 **5** ——
        **两次读数都不错，是那份日志当时正在被写入**（545 行 → 552 行；隔 3 秒复测仍在增长）。
      - **判据**：**文件已停止增长？进程已退出？锁已释放？** 任一未定 ⇒ **不得**据此下结论，只能标为「**瞬时值，不可复现**」。
      - ⇒ 与陷阱 ② 的关系：陷阱 ② 讲「引用要带快照」；本条**更尖锐** —— **引用还在变的对象时，连「快照」都不存在**。
      - ⇒ **同型**：与正面 ② 的配套坑「**锁写好后必须拿未变异原文跑控制组**」同型 —— **都是「取证前先做一次前置验证」**。

    ⇒ 小结（**统一原则，team-lead 裁定升格**）：**陷阱讲「别做什么」，本小节讲「该做什么」**。
    ①② 是**判据侧**：**判据必须能在文本里「缺席」**（拼接让它**不连续出现** / 量词让它**自匹配失败** / 取区间让它**落在扫描域外**）；
    ③④ 是**取证侧**：**只声明核过的那一项**、**且先证明对象已静止**。

    ⚠ **与第 76、77 条的区别（三条并列，不合并）**：第 76 条讲「**改动前**先做回归预判」（动作缺失）；
    第 77 条讲「位置断言**先断形态**」（断言写法）；本条讲「**记录本身**是否可核对」（记账与引用）。
    ⇒ 受害面也不同：前两条的受害面是**门禁/代码**，本条的受害面是**人** ——
    让人**误判一次正确的落盘**，或在文档里留下**会骗下一个人的行号**。

79. **legacy 消费裁决：veto 只能挂在「所有路径的唯一汇合点」上；只比 `Kind` 的 parity 会漏掉「没执行」（2026-09-18 登记）**

    **概念**：维护者裁定「**拒绝**，用自研管线完成色彩映射」⇒ 传统管线（legacy）不再自决结局，
    而是**消费与引擎同一份** `ColorTransformPlan.Verdict`（`docs/COLOR_ENGINE_INTERFACE_PLAN.md` §3 P3「单一消费接口」）：
    `Reject` ⇒ **不产出**；`ProceedWithDegradation` ⇒ 产出 + 逐条打**稳定码**；`Proceed` ⇒ 不变。
    ⇒ 于是"要不要让 legacy 也拒"这个问题被**替换**成"同一模型、两种消费"。

    **① veto 的挂载点：必须是「两条入口的唯一汇合点」，不是「离原因最近的地方」**
    `QueueProcessor.cs` 里 `if (engUsed) … else { … }` 的 **else 体**（`:580-643`，**2026-09-18 快照**）——
    它是**三条**入口的汇合点：① 显式 `--color-engine legacy`（`TryRunColorEngineAsync:774` 早退 `(false,0,null)`）；
    ② auto 默认 + 引擎不可走（`:818` 的 block 分支早退 `(false,0,null)`）；
    ③ auto + 计划为直通/保真（`:869`）⇒ **已带回 `plan`**，白捡一个裁决。
    ⚠ **不要**挂进 `:810-818` 的 block 分支内部（那是"离原因最近"的地方，但**是错的**）：
    它在**算 intent 之前**就 `return`（拿不到裁决）、且 explicit-engine 那条是**硬失败 90**
    （「路由前置条件不满足」≠「任务级终局」，见 `ColorVerdict.cs` 的 ⚠；混用会让调用方把「回退」误读为「失败」）；
    更要命的是它**覆盖不到显式 legacy**（`BlockReason` 对 legacy 恒 `null`）。
    ⇒ 判据：**一个 veto 点必须能拦住"所有会产生该结局的路径"** —— 拦不住的 veto 等于没拦。

    **② 退出码必须与引擎侧同码（`91`）**：同一条裁决在两条管线上要给出**同一个可机读结局**，
    否则调用方（脚本 / CI）无法统一处理。⚠ 且**不能**只写 `item.Status`：headless 的 `Program.cs`
    判失败用的是 `HasError`（`Status.StartsWith("失败")`），故 `91` 必须落进 `Item.ExitCode`
    才能让**进程**退出码非 0（实测：进程 `exit 1`、项级 `失败 (退出码 91)`、产物 **0** 个）。

    **③ 只消费、不新增 Reject 格（边界）**：本次**一个字没改** `ColorStrategyPlanning.cs`
    ⇒ 规划层的 Reject 格集合**未变**；变的只是 legacy 对那 4 个**本来就是引擎侧 Reject** 的格
    从"产出"改为"不产出"。⚠ 尤其**不许**把 `gif` / `jxr` / `ppm` 这类 `HasColorPath=false` 的格式
    收窄成 `Reject` —— 它们的含义是"容器不能**携带**色彩信息"，**不等于不能转换**
    （`docs/COLOR_ENGINE_INTERFACE_PLAN.md` §5-B 裁定，继续有效）。
    算不出裁决时（`ResolveLegacyVerdictPlan` 返回 `null`：DNG/PPM/BMP 这类**连色彩通路都没有**的出口）
    **保持既有行为**，不猜一个裁决出来（实测 `--format ppm` 无裁决行、照常产出）。

    ⚠ **④ 教训（本条最该记住的一条）：只比 `Verdict.Kind` 的 parity，测的是「有没有算」，不是「有没有做」**
    变异 M1（`legacyReject = false`，即 legacy **忽略**裁决、照常产出）实测：
      · 5 条「期望契约拒绝却产出」转红 ✅；
      · **`裁决parity(N 格)` 仍绿** ❌ —— 裁决**照样被算出来并打进日志**，只是**没被执行**。
    ⇒ 补「**结局 parity**」（`Reject` ⇒ 零产物；非 `Reject` ⇒ 有产物）后，同一次变异才直接打在 parity 上
    （红行：`carry-sdr-png: 传统裁决=Reject 但产出=True`）。
    ⇒ **通则**：「**值相同**」不等于「**行为相同**」——凡断言"两条路径结局一致"，必须**同时断"值"与"产物/副作用"**。
    （与第 70 条「日志标记单独不足以做门禁」、第 74 条「载体选错 ⇒ 断言恒真」同族：都在防"**断言没有牙**"。）

    **⑤ 覆盖度也会静默缩水**：parity 读的是**日志**，而成功项的 `item.Log` **默认不回显**
    （`ConsoleQueueObserver.cs:151`；仅 `--log-level Debug` 或失败项才输出）
    ⇒ ① ② 两段都必须显式带 `--log-level Debug`，否则只有"拒绝"的格有日志、
    parity 覆盖从 16 格掉到 **4 格**而断言**看起来通过**（实测踩到：`裁决parity(4 格)` + 覆盖格数转红）。
    ⇒ 故另加**覆盖格数下限断言**（`-ge 12`；变异 M2 破坏裁决行格式后实测 `6 < 12` 转红）。
    ⚠ 取**下限**而非精确值：`--jpeg-gain-map` 是否恒判"要引擎"（`ColorEngineRouter.Requested`）
    会让 GainMap 格落在 12 或 16 之间 ⇒ 取精确值会对其中一种口径**假红**（判据不应比失效模式更紧）。

    ⚠ **与第 78 条的呼应（本条正好撞上它落地）**：上面 `:580-643` / `:610` / `:614` 等行号是
    **2026-09-18 的快照**（S6 落盘时又在 `:610` 之前删了 4 行 ⇒ 引用整体 +4）。
    ⇒ 编辑 `QueueProcessor.cs` 时**以锚点文本为准**（「`legacyReject` 判定」「`[色彩裁决] 裁决=`」），
    行号只作定位参考 —— 即第 78 条陷阱 ② 的又一活例。

80. **跨脚本共享 fixture 并发污染 —— 触发条件从「同名脚本」扩大到「任意两个用同一 fixture 的脚本」（2026-09-18 登记）**

    **现象（同一份二进制、同一命令、连跑多次）**：`verify-color-wiring.ps1` 连跑三次得到
    **`99/0`** / **`95/2`** / **`12/53`** 三种结果。`12/53` 那次的形态是**几乎全部断言报「产物 0B」**，
    且失败**散布在 (1)~(11) 全部段** —— 含 JXR 出口 / cjxl 出口 / PAM / 外部 ICC
    这些**与当次改动毫无关系**的段。
    ⚠ 现象与第 66 条同款（**总数会变**），但**根因不同**（见下）⇒ 第 66 条的修法对本条**无效**。

    **与第 66 条的区别（本条要新增的正是这一条）**
    第 66 条管的是「**同一个**脚本的两个实例」：双方 `$out` 是**同一个固定路径**且**开头整目录删**
    ⇒ 互删对方的**中途产物**。
    本条是「**两个不同名**的脚本」：各自 `$out` **已 GUID 化**、**互不删对方目录**，
    但它们**读同一批共享 fixture**（`tests/output/validate/` 顶层的 `src_hdr.png` / `sdr_plain.png`，
    以及 `validate/caps/*` 等；顶层那批文件的分属关系见第 66 条末尾）
    ⇒ 一方**清空 / 重写**该 fixture 时，另一方**读不到**（`Test-Path` 失败，或 `RunCli` 拿到 **0B** 产物）。
    ⇒ **GUID 化（第 66 条的修法）对这类污染完全无效** —— 它隔离的是「**写**」，
    而本条污染走的是「**共享的读**」。
    ⇒ 触发条件因此从「**同名**脚本并发」扩大为「**任意两个用同一 fixture 的脚本**并发」
    （不必同名、不必同时起，只要**窗口重叠**）。

    **三条举证（2026-09-18，Part C 交付现场）**
    ① `tests/output/validate/` 里 10 分钟内出现**别人的** `wire_*` / `strat_*` / `s6cell_*` / `animrecon_*`
       目录 ⇒ 有 teammate 在**并发跑门禁**（不是"我以为只有我在跑"）。
    ② 构建报 **15 个警告**，明细全是 `MSB3026` / `MSB3061`，明确写着「文件被 **`ServiceProbe` (58108)** 锁定」
       ⇒ 有 teammate 在**并发跑 ServiceProbe**；**等它退出后重建 ⇒ `0 警告 0 错误`**
       ⇒ 那 15 个警告**全部是并发噪声**，不是代码问题。
    ③ `FfmpegGui.exe` 的 `mtime` 在跑门禁期间**没有变化**（无重建竞争），而失败形态是「产物生成不出来」
       ⇒ 排除「二进制被换掉」，指向**共享 fixture 被并发清空 / 重写**。

    **识别方法（关键，按可靠性排序）**
    ① **同一份二进制、同一命令、连跑多次 ⇒ `PASS` 总数会变** —— 最强信号（与第 66 条同款）。
       ⚠ 一次跑红**不足以**定性：必须**原地复跑**（**二进制不变**）再下结论。
    ② **失败是否散布到「与本次改动无关的段」** —— 这是与「产品回归」的**决定性区别**：
       改的是 GainMap / 路由，却红在 JXR / cjxl / PAM ⇒ 不可能是逻辑错。
    ③ 构建日志里有无 `MSB3026` / `MSB3061` **且点名被某个 `ServiceProbe` / `FfmpegGui` 进程锁定**
       ⇒ 有并发运行（这条同时解释「为什么我的构建警告数会跳」）。
    ④ 辅证（第 66 条已有）：「我没在跑门禁，但 `tests/output/**` 仍有新写入」。

    ⚠ **纪律一：不要去"修"这种红**。原地复跑（二进制不变）变绿 ⇒ **记作假红**，复跑一次即可收工。
    把假红当缺陷去改代码，会把一个**不存在**的回归"修"成**真**回归（与第 76 条「改动前先做回归预判」同族）。

    ⚠ **纪律二：它对变异验证是双向污染** —— 假红会被误读成「变异有牙」（其实是被别的东西弄红的）、
    假绿会被误读成「变异没牙」。⇒ **变异验证的每次读数都要复跑一次，并在报告里写明"复跑是否一致"**；
    只报单次读数的变异结论**不可采信**。

    **现状（2026-09-18，同日更新）**：**审计已完成**（结论见第 83 条的成因表）；**改造只做了"生产者"一侧**
    （`run-color-tests.ps1` 不再写共享路径，见第 83 条成因 3）—— **消费者一侧刻意不做**，
    因为审计证明消费者对共享素材**只 `Test-Path`、从不生成** ⇒ **并发读无害**，只需处理**生产者**与**清单外写者**。
    ⚠ 改造**延后**到正在跑变异验证的两位 worker 收工之后 —— 此刻改脚本会把他们的读数变成**移动靶**
    （**这条警告在 2026-09-18 01:16~01:20 被现实验证了**：见第 83 条成因 6）。

---

81. **`verify-color-peak.ps1:75-76` 静默 SKIP ⇒ 断言数可变 —— 第 68 条的漏网实例（2026-09-18 登记）**

    **形态**：`(d)` 段开头是
    `if (-not (Test-Path $hdrPng)) { Write-Output "  SKIP (d)：缺 $hdrPng（先跑 ensure-fixtures.ps1）" } else { …4 条 CK… }`
    ⇒ 缺素材时那 **4 条 CK 根本不执行**：`$pass` 不变、`$fail` 不变、`exit 0` 不变。
    ⇒ **同一份二进制、同一条命令，`PASS` 总数在 25 与 21 之间变**（实测计数：全文 `CK` 共 **25** 条，(d) 段 **4** 条），
    而**退出码恒 0**。

    **为什么单列一条**：这是本次共享 fixture 审计里**唯一会改变断言强度**的一处 ——
    其余问题（素材从哪来、谁写共享路径）都只改"**素材来源**"，不改"**验几条**"。
    其余形态是"断言红了"（**可见**）；本条是"断言**不存在了**"（**不可见**）⇒ 前者骗不过人，后者能。

    **与既有两条规矩的关系（两条都沾，且都比它们更退一步）**
    · **第 68 条**（消费者读不到前置产物**必须红**，不得 `continue` / 不得只打印 SKIP）——
      本条是它的**漏网实例**：第 68 条末尾那张"静默 SKIP 清单"列了
      `_probe-png-cicp-production` / `_probe-icc-affects-decode` / `_probe-zimg-noop-check` / `_probe-sws-pos`，
      **没有 `verify-color-peak.ps1`**。而 `$hdrPng = validate/src_hdr.png` 正是**跨脚本前置产物**
      （生产者 `ensure-fixtures.ps1:31`）⇒ 本就落在第 68 条的适用范围内。
    · **第 76 条 ② 的「跳过退化」形态**（SKIP 必须**进汇总**、形如 `PASS=n FAIL=0 SKIP=2`，原因逐格打印）——
      本条比它**更退一步**：连 `SKIP` 这一行都**不进汇总**（只有一句 `Write-Output`、**没有 `CK`**）
      ⇒ 汇总行看起来仍是"全绿"，读者**没有任何线索**知道少了 4 条。
    ⇒ 两处修法取其一（按第 68 条硬口径**应改成红**；若坚持保留 SKIP 语义，则按第 76 条**让 SKIP 进汇总并计数**）。

    **现状**：✅ **已修（2026-09-21）**，取**第 76 条口径**（让 SKIP 进汇总并计数），而非第 68 条的硬红。
    **为什么取 76 而非 68**：该素材 `tests/output/validate/src_hdr.png`（生产者 `ensure-fixtures.ps1`）
    **不受版本控制**（`tests/output/**` 被 `.gitignore`），且**运行器不跑 `ensure-fixtures`**
    ⇒ **干净检出上它必然缺失** ⇒ 若改成硬红，门禁会在干净检出上**永久红**（那是"环境未就绪"，不是缺陷）。
    而本条的**真正病灶是"不可见"**（登记原文：「其余形态是"断言红了"（**可见**）；本条是"断言**不存在了**"（**不可见**）」）
    ⇒ **修"可见性"即治本**。
    **修法**：加 `$skip` 计数器 + `function SKIP([string]$what,[int]$n=1)`（与 `verify-gif-avif-framelist.ps1` **同一形态**），
    (d) 段改为 `SKIP "…以下 4 条断言**本轮未运行**，不是通过" 4`，汇总行改为 `PASS=$pass FAIL=$fail SKIP=$skip`。
    **负控实测（2026-09-21）**：临时移走素材 ⇒ 打印
    `SKIP (4 条) (d) 段整段：缺 …—— 以下 4 条断言**本轮未运行**，不是通过`，
    汇总行 **`PASS=21 FAIL=0 SKIP=4`**（**修复前**是 `PASS=25 FAIL=0` —— 读者**看不出**少了 4 条）；
    还原素材后 **`PASS=25 FAIL=0 SKIP=0`**（哈希 `f168fcb080e0151f` 复原一致）。
    ⚠ **② 已做（2026-09-21）**：对 `tests/scripts/*.ps1` 做了**两轮**普查 ——
    ⚠ **第一轮用字符串 grep（`Write-Output "…SKIP…"`）有假阴性**（漏掉 `Say "SKIP …"` 这类辅助函数包装）；
    **第二轮改用语义判据**（「**首条断言之前**是否 `exit 0`」）⇒ 共命中 **5 个脚本 / 14 处跳过点**：
    · **真缺陷**（汇总看起来完整、实际少跑 ⇒ 已修）：`verify-color-peak.ps1`（(d) 段 4 条）·
    `verify-decision-delivery.ps1`（⑫外层 4 / ⑫内层 3 / ⑩ 5 / ⑪ 9 条，汇总加 `skip=`）；
    · **口径统一**（整门禁跳过**本就可见** —— 该轮只有一句 SKIP、运行器展示正则含 `SKIP`、第 62 条已要求单列；
    补汇总行是为**统一形态**）：`verify-png-signature.ps1` · `verify-metadata-privacy.ps1` · `verify-gainmap-memory.ps1`
    （各 3 条前置守卫 ⇒ `SkipAll` + `===== PASS=0 FAIL=0 SKIP=1（⚠ 整门禁未运行，不是通过）=====`）；
    · **判定为不是缺陷**：`verify-color-caps.ps1`（在「(g) **未实测项（必须显式标注）**」小节，那些断言本就不存在）·
    `verify-color-tonemap.ps1`（有**独立黄字小节** `=== SKIP（未实现，不给绿灯）===`）。
    ⚠ **2026-09-27 更新（#52）**：上面那句"caps 的断言本就不存在"**只对当时成立** —— `verify-color-caps.ps1`
    那行 jxr 的纯打印 SKIP（原文写「本构建无 ICC/CICP 写通路」）已被实测否证并换成 (g) 段**三条真断言**
    （ffmpeg `-c:v libjxr` 可编 / exiftool 写 ICC 能逐字节读回 / 嵌后 `JxrDecApp` 仍可解）⇒ 该门禁
    **12/0 → 15/0**；脚本段 SKIP 由 4 条降为 **3 条**（清点见 `.workbuddy-ai/memory/COLOR_TRAPS.md` §七）。
    **C# 侧**（`ServiceProbe` / `UiTestHost`）**已普查 ⇒ 无同类缺陷**（SKIP 全带显式标记且被运行器捕获）。
    变异验证：**M5**（`if ($true)`）⇒ `pass=48 fail=0 **skip=3**` · **M6**（移走 `JxrEncApp.exe`）⇒
    `PASS=0 FAIL=0 SKIP=1（⚠ 整门禁未运行，不是通过）` · **M7**（`verify-gainmap-memory` 强制跳过）⇒ 同上；
    三处源文件均**用「编辑后」备份**还原并核验「功能仍在」。
    ⚠ **仍未做**：`ensure-fixtures.ps1` **未接线进运行器** —— ⚠ **查明这不是疏漏而是锁设计使然**
    （运行器 `:460` 取 `tests/output/.gates.lock`、`:783` 才跑脚本循环，而该脚本用**同一把锁** ⇒ 接进循环必然 `exit 9`）
    ⇒ 三种改法（循环前先跑 / 加 `-LockAlreadyHeld` 旁路 / 运行器自产 fixture）**均属设计决策，待维护者拍板**。

82. **`validate/cmsx` 已成死链：生产者已改名、消费者仍读旧目录、运行器注释过期（2026-09-18 登记；按「红了也没人看」口径不派工）**

    三处事实（2026-09-18 实测 grep）：
    ① `verify-color-engine-xcheck.ps1:20` 的 `$d` 已是 `validate/cmsx_<guid8>`（GUID 运行级隔离）
       ⇒ 它**不再生产** `validate/cmsx`。
    ② `_probe-zimg-noop-check.ps1:5` 的 `$d` 仍是**固定**的 `validate/cmsx`，且 `:19` 是**静默 SKIP**
       ⇒ 干净环境下它读的是一个**没人创建**的目录 ⇒ 永远 SKIP（第 68 条清单里已列过它）。
    ③ `_run-step3-gates.ps1:150-154` 的注释仍写着「生产者 `verify-color-engine-xcheck.ps1` 在清单、
       消费者 `_probe-zimg-noop-check.ps1` 不在 ⇒ 反向形态，消费者根本不跑 ⇒ **无隐患**」
       （⚠ **行号更正（2026-09-19）**：原引 `:100`；**非本轮引入的既有失真，一并修正**。行号锚在运行器快照
       `SHA256=C999C35BF6C49506…` / 57518 B / mtime `2026-09-19 12:15:44` / 671 行，**指纹一变即作废**）
       ⇒ 前半句**已过期**（生产者早已改名）；结论"无隐患"**碰巧仍成立**（消费者确实不跑），但**理由错了**
       —— 这正是第 78 条说的"引用行号/事实必须带快照时刻"那类过期记录。

    ⇒ 定性：**死诊断**（诊断型脚本、无 pass/fail 计数、红了也没人看）。
    ⇒ 处置：**只登记，不派工**（与第 68 条清单里那 4 个 `_probe-*` 同族）。
    ⚠ 将来若有人要重新启用它，**先修 `:5` 的路径**；注意"指回生产者的 GUID 目录"**做不到**（GUID 每轮不同）
    ⇒ 只能改成**自备素材**。

83. **「读数可变」成因归类 —— 下一个人先看这一条，再看细节（2026-09-18 汇总）**

    **现象的统一口径**：同一份二进制、同一条命令、连跑多次 ⇒ `PASS` / `FAIL` 总数不同。
    已实测到 **9 种互不相同**的成因（1/2 此前已登记；3~7 为本轮新增；8/9 由 team-lead 复核补入）：

    | # | 成因 | 机制 | 登记处 |
    |---|---|---|---|
    | 1 | **同名脚本并发** | 双方 `$out` 同一固定路径 + 开头整目录删 ⇒ 互删对方**中途产物** | 第 66 条 |
    | 2 | **任意两个用同一 fixture 的脚本并发** | `$out` 已 GUID 化、互不删，但**共享的读**同一批 fixture；一方重写 ⇒ 另一方 `Test-Path` 失败或拿到 **0B** | 第 80 条 |
    | 3 | **双配方生产者**（`a2_srgb_p3.png`）| 两个脚本**同路径、不同配方**、谁后跑谁赢；**不是等价配方** | 本条（下详） |
    | 4 | **取任意残留产物当输入** | `Get-ChildItem … -Recurse \| Select -First 1`（**无排序**）⇒ 输入随机 | 本条（下详） |
    | 5 | **静默 SKIP** | 缺素材时若干条 CK **不执行**且**不进汇总** ⇒ 总数变、退出码不变 | 第 81 条 |
    | 6 | **门禁脚本自身被并发修改** | 脚本在两次运行之间被改 ⇒ 同一二进制、同一命令，判据本身变了 | 本条（下详） |
    | 7 | **运行器自身产物的 write-write** | 运行器把每个门禁的输出写到**固定路径** `tests/output/_gate_script_<script>.txt` ⇒ 两个 runner 实例并发时**这两个文件本身**互相覆盖（**运行器自己的产物**，与任何 fixture 无关）| 第 86 条（加锁后顺带消失；不加锁则应单独修）|
    | 8 | **被测 exe 正在被重建**（判据的**载体**被换）| 重建期间 `Release` 不存在 ⇒ 门禁**静默回退 Debug exe** ⇒ 两次读数测的是**两个不同的二进制** | 第 84 条 |
    | 9 | **断言自身越界 ⇒ 整段静默不执行** | 抛异常被 `Main` 的 `catch` 吃掉 ⇒ 计 **1** 个 fail、打 `exception:`（**无 `FAIL` 前缀**），而**整段其余断言全没跑**、汇总行照常打印 | 第 85 条 |

    ⚠ 九种成因的**修法互不相同**，不要互相套用：1/2 靠隔离或"只留生产者"；3 靠"别写共享路径"；
    4 靠"自备确定的输入"；5 靠"必须红或必须计数"；6 **无脚本内修法**，只能靠**流程**（见下）；
    7 靠运行器级锁（第 86 条）；8 靠"回退必须可见、或干脆红"；9 靠"catch 必须打 `FAIL` 前缀 / 断言前先做边界自检"。

    **成因 3（双配方生产者）—— 实测与修法**
    同路径 `tests/output/results/color/a2_srgb_p3.png`，两个生产者：
    · `ensure-fixtures.ps1:64`（`-pix_fmt rgb48le`）+ `:71`（exiftool **附 P3 ICC**）
    · `run-color-tests.ps1:77`（`-c:v png -pix_fmt rgb48be` + `-color_primaries smpte432`，**从不附 ICC**）

    **"消费方需要哪一种"是实测确证的，不是推断**（把两份各造一份到 `%TEMP%`，用真实引擎 CLI 跑同一命令）：

    | 对照项 | 带 ICC 那份 | 无 ICC 那份 |
    |---|---|---|
    | `ServiceProbe color` | `extractedIcc=… desc=RGB built-in`；**`spaceName=Display P3`** | **`extractedIcc=(null)`；`spaceName=(null)`** |
    | 引擎决策 | **`CarryIcc`**（像素零改动，携带源 ICC） | **`None`**（无目标色彩空间 → 沿用源描述） |
    | cjxl 命令行 | **`-x icc_pathname=…icc`** | **`-x color_space=DisplayP3`** |
    | 产物 | 9115 B | 9124 B |

    ⇒ 门禁实测（`verify-color-wiring.ps1`）：把共享 fixture 换成**无 ICC** 形态后
    **`99/0` → `95/4`**，其中 **2 条可唯一归因于 ICC**（`(10) 携带源 ICC 的计划走 -x icc_pathname=`、
    `(10) 已跳过 exiftool 后置`）—— 判据是这 2 条在还原成带 ICC 后**消失**。
    ⚠ 另 2 条 `(11)` 段红**与 ICC 无关**（成因 6，见下）⇒ **归因必须逐条做，不能只看总数**。
    ⇒ **修法（已落地）**：让 `run-color-tests.ps1` **别写共享路径**（它**不在** `_run-step3-gates.ps1` 的清单里，
    本不该影响门禁）—— 改名 `$res/rc_a2_srgb_p3.png`，并把该脚本 D1~D4 的**输入**一并改成 `$o2`
    （此前它们读的是**门禁的**那份；改后自洽）。**不削弱任何断言**：
    `run-color-tests.ps1:79` 的 A2 只断言 CICP、**从不断言 ICC**；
    D1 的判据是 `-match "RGB|P3"`（该行注释本就写明"P3 输入无 ICC 时 jxl 标 sRGB"）；
    D3/D4 的注释本就写着"**P3 源无 ICC 时 iccgen 补偿**"⇒ 用无 ICC 的输入**反而更贴合其自述意图**。
    **`ensure-fixtures.ps1` 不动**（它是带 ICC 的正解；改它会把 `(10)` 段两条断言削掉）。

    **成因 4（取任意残留产物当输入）—— 形态与修法（尚未落地）**
    `verify-decision-delivery.ps1:260`：
    `$avif = Get-ChildItem "$root/tests/output/results" -Recurse -Include *.avif | Where-Object { $_.Length -gt 3000 } | Select-Object -First 1`
    ⇒ 输入的 avif 是**仓库里残留的任意一张**（`Select -First 1` **无排序**）。
    而 `run-color-tests.ps1` 会往 `results/color` 写 `a1_p3.avif` / `a3_p3pq.avif` / `a4_hdr2sdr.avif`
    ⇒ ⑩ 段在 **`SKIP`**（无素材）与 **`+4 条 CK`**（有素材）之间摇摆；**同二进制同命令，总数不同**。
    ⇒ **修法（已定，待放行）**：改成**自备素材**（该脚本已在 `$out` 里造 avif），
    **保留 SKIP 语义**（自造失败才 SKIP），只把**触发条件**从"取决于别人写过什么"变成"取决于它自己能不能造"。
    ⚠ **不要**用"排序后取第一个"敷衍 —— 那只是把随机变成"取决于别人写了什么"，输入仍不确定。

    **成因 6（门禁脚本自身被并发修改）—— 这是第 80 条 ③ 的反面补充**
    第 80 条 ③ 的排查动作是"查 `FfmpegGui.exe` 的 mtime 未变 ⇒ 排除二进制被换"。
    **本条要求同时查"门禁脚本自己的 mtime"** —— 2026-09-18 实测：
    `verify-color-wiring.ps1` 的四次读数 **`99/0` → `95/4` → `74/15` → `97/2`**，
    而 `verify-color-wiring.ps1` 的 mtime = **01:20:12**、`FfmpegGui.exe` = **01:19:19**
    ⇒ **脚本在我两次运行之间被改过**（硬证据：(11) 段断言文本从「…仍在生效」/「…**直接**含 zscale=primariesin=bt2020」
    改成了「…**不再**硬编码改标」/「…**不再**含 zscale=primariesin=bt2020」，即 P9 口径回填）
    ⇒ 那 2 条 `(11)` 段红是**"脚本已改成新契约、二进制还没跟上"**，与我的改动无关。
    ⇒ **纪律三（新增）：复跑不变绿时，先查"脚本 mtime / 二进制 mtime"再查逻辑。**
    若两者在窗口内变过 ⇒ 本次读数**作废**，等对方收工后重取；
    **不要**把"别人在途的半成品状态"当成自己的回归去"修"（与纪律一同族）。

    **成因 6 的一个推论（对"移动靶"的具体含义）**：第 80 条末尾那句"此刻改脚本会把他们的读数变成移动靶"
    —— 反过来也成立：**别人改脚本会把我的读数变成移动靶**。⇒ 结论是**双向**的：
    任何跨 worker 共享的门禁脚本，**编辑与运行不能并发**，且编辑方应**在报告里写明"我改了哪个脚本、何时"**。

---

84. **被测 exe 正在被重建 ⇒ 门禁静默回退 Debug —— 共享的是「判据的载体」，与成因 6 必须分开（2026-09-18 登记）**

    **证据链（S6 闭合，`gainmap-merge-partA` 记录）**：
    `Release` mtime `01:05:58` → S6 `01:06:13` 查过 → 第 5 轮整轮期间 `Release` **又被重建**（`01:09:21 → 01:11:45`）
    → **第 4 轮开跑时 `Release` 不存在** ⇒ 脚本**静默回退 Debug exe**。
    我复核（快照 **2026-09-18 01:25**）：`Release/FfmpegGui.exe` mtime = **01:20:55**、
    `Debug/FfmpegGui.exe` mtime = **01:13:17**、`verify-color-strategy.ps1` mtime = **01:14:17**。
    ⚠ team-lead 引的是「脚本 `:91-92`」，而在我这份快照里该行是 **`verify-color-strategy.ps1:93-94`**
    （`:93` 指 Release、`:94` 是回退）—— 差 2 行是因为该脚本在 `01:14:17` 又被编辑过
    ⇒ 正是**第 78 条**说的"行号只作定位参考，以**锚点文本**为准"（锚点：`bin/Release/net11.0/win-x64/FfmpegGui.exe` 与 `bin/Debug/…`）。

    **静默回退的实现（同一行、遍布仓库）**：
    `if (-not (Test-Path $exe)) { $exe = "$root/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe" }`
    ⇒ 我 grep 到 **21 处**（举证：`verify-color-wiring.ps1:21`、`verify-color-strategy.ps1:94`、
    `verify-decision-delivery.ps1:19`、`verify-gainmap-engine.ps1:49`、`verify-engine-firstframe.ps1:42` …）。

    ⚠ **与成因 6 的区别（务必保持区分，不要合并）**：
    · 成因 6 共享的是**脚本** —— 判据的**写法**变了；
    · 本条共享的是**被测可执行文件本身** —— 判据的**载体**被换。
    两者的排查动作不同：成因 6 查 `*.ps1` 的 mtime；本条查 **`Release`/`Debug` 两个 exe 的 mtime**。

    **两条附带（必须一起记住）**
    ① **Debug 回退是静默的** ⇒ 门禁可能测的**不是你以为的那个二进制**，而它**跑完不会告诉你它测了谁**。
    ② **同一份源码、两个不同 exe 的"绿"不是同一个读数** —— `Release`/`Debug` 是两次独立构建产物
       （Debug 未优化、且路径/时序行为可能不同）⇒ **不能拿"Debug 绿"去宣布"Release 绿"**。

**处置建议**：✅ **① 已落地（2026-09-21）**：全部 **38 处**回退点（`$exe` 28 处 + `$pr`/`$prExe`/`$prb` 等 10 处，分布于 **33 个脚本**）
在回退判定之后**一律打印** `[gate] exe=<实际路径> (Release|Debug fallback)` ⇒ 读数可追溯。
⚠ 实现纪律：**只插入新行、不改动既有行**（逐文件核验插入数 = 匹配数）；
新行用 `$(if (…) {…} else {…})`（**PS5.1 兼容**，非 `?:`）；新串**纯 ASCII**（不碰 cjk 锁）。
实测：`verify-ps-compat` **13/0**；`verify-color-peak` 打印 `…ServiceProbe.exe (Release)`、`verify-metadata-privacy` 打印 `…FfmpegGui.exe (Release)`。
② **更硬的做法（仍开放）**：**找不到 Release 就红**（与第 68 条同族：前置产物缺失**必须红**，不得静默降级）——
⚠ 属**行为变更**（会让「只构建了 Debug」的既有用法直接转红）⇒ 留给维护者拍板。

85. **断言自身越界 ⇒ 整段静默不执行，且 `exception:` 行不带 `FAIL` 前缀（2026-09-18 登记）**

    **实测（批次 1，`gainmap-merge-partA` 记录）**：`si == -1` ⇒ `t[-2]` 抛 `IndexOutOfRangeException`。
    **机制（我复核 `tests/ServiceProbe/Program.cs`，快照 2026-09-18）**：
    · `:162-167` `catch (Exception ex)` ⇒ `Console.WriteLine($"exception: {ex.GetType().Name}: {ex.Message}")` + `StackTrace`，`_fail++`
    · `:168` `Console.WriteLine($"PROBE RESULT: pass={_pass} fail={_fail}")` —— **汇总行照常打印**
    · `:172-176` `Check` 才打 `PASS` / `FAIL` 前缀 ⇒ **`exception:` 这一行没有 `FAIL` 前缀**

    **读数**：`pass` **52 → 36**（**16 条整段没跑**），而 `fail` 只 **+1**，且汇总行照常打印
    ⇒ **只看汇总行的人会以为"只坏了 1 条"**。

    ⚠ **可判别指纹（本条的核心，务必当判据用）**：**`fail` 与 `pass` 的变化量不匹配** ——
    `fail=1` 却 `pass` 骤降（本例 **−16**）。
    ⇒ 这比"要按 `exception:` 过滤"**更有用**：因为**复读的人可能根本不会去看逐行输出**（只看汇总行）。
    ⇒ **它是"读门禁的人"的口径，不只是"写断言的人"的自觉**：任何读数异常（尤其 `fail` 小得可疑），
      都要去 `PROBE RESULT` **之前**找 `exception:`。

    **处置建议**：✅ **① 已落地（2026-09-21）**：`tests/ServiceProbe/Program.cs` 的主 `catch` 改打
**`FAIL exception: …`**（带前缀）⇒ 它与 `Check` 的失败行**同形**，凡按 `^FAIL` 过滤的读者都能看见，
不会再出现「`pass` 骤降而只看汇总行的人以为只坏了 1 条」。
⚠ 影响面先查过：全仓只有 2 个脚本出现 `exception:` 字样（`verify-ipc-extra-options.ps1` / `verify-oracle-matrix.ps1`），
且都指**它们自己的**异常，**不解析探针输出** ⇒ 加前缀**无副作用**；探针基线复核 **仍 542/0**。
⚠ **本条更重要的遗产**（登记者自评「比按 `exception:` 过滤更有用」）：**判据是「`fail` 与 `pass` 的变化量不匹配」** ——
`fail=1` 却 `pass` 骤降（实测 −16）⇒ **任何读数异常（尤其 `fail` 小得可疑）都要去 `PROBE RESULT` 之前找 `exception:`**。
    ② 更好的是**断言前先做边界自检**（`if ($si -lt 2) { Check($false, "…索引不足，本段无法评估…"); return }`）
    ⇒ 把"**没跑**"变成"**明确红**"（与第 68 条同族：不得静默少验）。

86. **运行器级互斥（#147 C(0919)）—— ✅ 2026-09-19 已实施；四条覆盖盲区**

    **目的**：消除成因 **1**（同名脚本并发）与成因 **2**（共享 fixture 并发读）的**最大窗口**（两个 runner 实例并发），
    并**顺带**消除成因 **7**（`_gate_script_*.txt` 的 write-write）。

    **方案要点（已批准）**：**仓库级单锁** `tests/output/.gates.lock`
    （为什么是单锁：污染源是**共享 fixture 目录** ⇒ 任何两个会**写**它的进程都必须互斥，单锁最简且足够）；
    获取用 `[System.IO.File]::Open($p, [FileMode]::CreateNew, [FileAccess]::Write, [FileShare]::None)`；
    锁内容写 JSON `{pid, host, startedAt, script, cmdline}`；
    **被占用时明确报错并退出非 0（建议 `9`，与现有 0/1、探针 90/91/92 都不撞）** ——
    **不静默跳过、不等待、不超时后继续**；
    **stale 锁不自动删**（读锁里的 `pid`，`Get-Process -Id` 失败 ⇒ 仍报错，只把措辞改成"持有者进程已不存在（疑似残留锁）"，
    并提示可**手工**删除）—— 自动删会引入"我删了别人的锁"的**新竞态**（第 66 条同族），让**人**决定。

    ⚠ **前置条件（必须先修，否则锁的报错会被吞掉）**：`_run-step3-gates.ps1` **尾部只有 `Check-SrcDrift`、没有 `exit`**
    ⇒ 若只在锁冲突处 `exit 9`，**运行器进程仍可能以 0 结束** ⇒ 必须在尾部补 `exit 0` 并把非 0 **冒泡**出去。

    ⚠ **四条覆盖盲区（不要以为有了锁就万事大吉）**
    1. **有人直接跑单个 `verify-*.ps1`**（不走运行器）⇒ **完全绕开锁**。给 12+ 个脚本各加锁**不做** ——
       那会把"共享文件面"从 1 个扩大到 12+ 个（每个都成了"可能被别人并发改"的文件）⇒ 反而**制造**成因 6。
    2. **清单外写者**：`ensure-fixtures.ps1`（**唯一残留**的共享 fixture 生产者）⇒ **给它加同一把锁**（~10 行）；
       `generate-sources.ps1` 写 `tests/output/sources`、而门禁**只读它、从不覆盖** ⇒ **不加**。
       （`run-color-tests.ps1` 本也要加，但成因 3 的修法已让它**不再写共享路径** ⇒ 不需要了。）
    3. ⚠ **本锁不声称覆盖构建** —— `dotnet build` / `MSBuild` **不受**本锁约束，
       而第 80 条 ② 那 15 个 `MSB3026` 正是"**构建 × 并发跑 ServiceProbe**"。
       要覆盖得把锁加到构建入口（`pack.ps1` / `build_tools.ps1`）—— **属另一条线，本方案不声称覆盖**。
    4. **进程外手段**（手工往 `validate/` 拷文件、同步盘/编辑器触碰该目录等）⇒ 锁管不到。

    **✅ 现状（2026-09-19）**：**已实施**（team-lead 于 2026-09-19 显式通知后落地）。实施范围与验证矩阵见下方「实施记录」。⚠ 原文的「实施时机由 team-lead 显式通知」——
    `_run-step3-gates.ps1` 是共享文件，此刻动它又是成因 6；**不要靠观察 mtime 判断"没人跑"**。

    **⚠⚠ 2026-09-18 实证（接手会话首次全量串行跑通时取得）—— 上面那条「前置条件」不是将来时，是现在时的活缺陷**：
    全量 34 条串行跑（11m53s）时 `_probe-cjk-hardcode-scan.ps1` 返回 **`exit=1`**，而**运行器进程整体 `EXITCODE=0`**。
    ⇒ 它**不只是**「锁冲突处 `exit 9` 会被吞」—— **任何**子门禁失败都不会体现在运行器退出码里。
    ⇒ 在补 `exit 0` + 非 0 冒泡**之前**：**不要用运行器退出码当判据**，必须读日志里的 `[<script>] exit=N` 行。
    ⇒ 处置建议：**与 #147 C(0919) 的该条前置条件合并实施**（同一处改动，不要分两次动 `_run-step3-gates.ps1`）。

    **✅ 2026-09-18 已修（接手会话；只改「退出码」这一半，未动 #147 C(0919) 的锁）**：
    在 `_run-step3-gates.ps1` 里加了 `$failedItems` 记账 + `Exit-WithSummary` 函数，**尾部显式 `if/else` 退出**：
    · **探针循环**：`$p.ExitCode -ne 0` **或** 汇总行出现 `fail=[1-9]` ⇒ 记 `probe:<mode>`；
    · **脚本循环**：`$proc.ExitCode -ne 0` ⇒ 记 `script:<name>`；
    · **`Check-SrcDrift`**：跑期间源码被改 ⇒ 记「源码漂移 ⇒ 本轮结论作废」；
    · `-SkipScripts` 分支也走同一套汇总（原来是裸 `return` ⇒ 恒 exit 0）。
    ⚠ 退出写成**完整 `if/else`**（只写 `if ($fail) { exit 1 }` 会让全绿路径以被宿主污染的 `$LASTEXITCODE` 退出 ⇒ 把"修假绿"修成"造假红"，见第 58 条）。
    **验证（双宿主、红绿两路径，均实测）**：
    | 用例 | PS 7 | PS 5.1 |
    |---|---|---|
    | 红：`-SkipScripts -Probes contract,nosuchmode`（未知 mode ⇒ probe `exit=2`） | **EXITCODE=1** | **EXITCODE=1** |
    | 绿：`-SkipScripts`（全绿） | **EXITCODE=0** | **EXITCODE=0** |
    | 整轮 36 条（真实红门禁 `_probe-cjk-hardcode-scan.ps1`） | **EXITCODE=1** + 汇总列出 `script:_probe-cjk-hardcode-scan.ps1 (exit=1)` | — |
    ⇒ **该门禁红在 27 轮里无人发现的根因（运行器吞退出码）已消除。**
    ⚠ 当时仍**未**做的是 **#147 C(0919) 的运行器级锁**（另一个改动面）；其「前置条件」现已满足 ⇒ **已于 2026-09-19 实施，见下方「实施记录」。**


    **✅ 实施记录（2026-09-19，接手会话）**

    **落点**：`_run-step3-gates.ps1`（**获取**在 `$root` 赋值之后、任何写操作之前；**释放**在 `Exit-WithSummary` 内
    ⇒ **所有退出路径都经过它**）+ `ensure-fixtures.ps1`（**同一把锁**；获取在 `$root` 之后、释放在**末尾**）。

    **实现要点（与上面方案逐条对齐）**：
    · `[System.IO.File]::Open($p, CreateNew, Write, FileShare::None)` 获取；锁内容 = `ConvertTo-Json -Compress`
      的 `[ordered]@{pid, host, startedAt, script, cmdline}`；
    · ⚠ **写入后立即 `Dispose()` 句柄** —— 锁的**存在性**由 `CreateNew` 语义保证（文件已存在 ⇒ 抛异常），
      **不**靠长期持有独占句柄；否则别的实例**读不到锁内容** ⇒ **stale 检测（读 pid）结构性失效**。
      这是实施期发现并修正的一处设计细节，**方案原文没写**；
    · 冲突 ⇒ 报错 + `exit 9`；`stale`（`Get-Process` 失败）⇒ 措辞改成「疑似残留锁」+ 提示**手工**删，**仍 `exit 9`**；
    · 释放**只删自己写的锁**（`pid` 字段必须等于本进程）⇒ 防「我删了别人的锁」；
    · 注释里**逐条写明**「本锁不声称覆盖构建 / 直接跑单个脚本 / 进程外手段」+ 刻意不给 12+ 个 `verify-*.ps1` 加锁的理由；
    · ⚠ **顺带修**：`ensure-fixtures.ps1` 原先**无退出码契约** ⇒ `$LASTEXITCODE` 会**残留上一次**的值
      （实测：上一次是锁冲突的 `9` ⇒ 它明明跑完了却报 9）⇒ 末尾补显式 `exit 0`（语义仅「跑完了」，
      素材失败仍只打 ❌、**不改**退出码 —— 它**不是**门禁）。

    **验证矩阵（均实测）**：

    | 用例 | 期望 | PS 7.6.6 | PS 5.1 |
    |---|---|---|---|
    | 锁被**活着的**进程持有 | `exit 9` + 「另一个门禁运行器正在跑」+ **不启动**探针 | ✅ 9 | ✅ 9 |
    | 锁的 `pid` **不存在**（stale） | `exit 9` + 「**疑似残留锁**…手工删除」+ 锁**仍在** | ✅ 9 | ✅ 9 |
    | 无锁（绿路径） | 获取锁 ⇒ 跑完 ⇒ **释放**（锁消失） | ✅ 0 | ✅ 0 |
    | `ensure-fixtures.ps1` 遇锁 | `exit 9` + 点名 | ✅ 9 | ✅ 9 |
    | `ensure-fixtures.ps1` 正常 | 获取 ⇒ 生成素材 ⇒ **释放** | ✅ | ✅ 0 |
    | `verify-ps-compat`（静态） | 13/0 | ✅ | — |

    ✅ **双宿主覆盖已补齐（2026-09-19 当日复核）**：原先「`stale` 与 `ensure-fixtures` 只在 PS 7 下实测」的缺口
    已消除 —— 三项在 **PS 5.1 下逐一实跑**均通过（stale ⇒ `exit 9` + 「疑似残留锁」+ **锁仍在**；
    `ensure-fixtures` 遇锁 ⇒ `exit 9` + 点名；正常 ⇒ 获取 + 生成素材 + **释放**，`last=0`）。
    ✅ **整轮也已双宿主跑通（2026-09-19）**：PS 5.1 下完整 **42 条 + 20 探针全部通过**
    （`PS51_RUNNER_EXITCODE=0` · 探针 **519/0**（⚠ 该轮在 **#20(0919) 之前** ⇒ 当时基线的正确值就是 519；#20(0919) 后为 **525**）· 脚本 42/42 · **跑后锁残留=False** · **29m28s**，
    比 PS 7 的 14m30s 慢约 **2 倍**）⇒ **未发现任何 PS 5.1 专属缺陷**。（⚠ 该轮跑时**当时为 42 条**；**2026-09-19 P4-A 接线后为 48 条**。）
    **📌 实例（2026-09-19，真实拦住一次并发整轮 —— 首次由真实场景而非构造用例触发）**：
    第二个整轮实例（`pid 19928`，`CreationDate 10:58:23`）在 `10:58:37` 前**已不存在**；
    同期 `tests/output/.gates.lock` 的 holder **恒为** `pid 52348` @ `10:47:01`（`_run-step3-gates.ps1`）
    ⇒ **符合「被本锁拒退」**。按锁规格与上方验证矩阵，拒退行为 = **`exit 9` + 点名 + 不等待、不静默跳过**
    （⚠ **该次实例的退出码未直接取得**：它是独立进程、stdout 不经过观测者会话 ⇒ 本条为**推断**；
    **证据类型 = 进程表快照 + 锁文件内容**，**非被拒方输出**）⇒ 只可写"**符合拒退特征**"，**不得**写"已确认拒退"。
    ⇒ 本条**意义**：把锁从"设计 + 构造用例"升级为"**真实场景拦住过一次**"（此前 `exit 9` 只在构造用例下验过）。
    ⇒ 该实例**顺带复核**了本条的三个要点（均见上方，非新信息）：锁路径 `tests/output/.gates.lock` ·
    `ensure-fixtures.ps1` **共用同一把锁** · **stale 不自动删**（`pid 19928` 消失后锁 holder 仍是 `52348`，
    **未被任何进程自动清掉** —— 与"stale 只报错、由人手工删"的设计一致）。
    ⚠ **本实例不声称覆盖「四条盲区」**：直接跑单个 `verify-*.ps1` 仍**完全绕开锁**；构建**不受**锁约束。
87. **门禁的「措辞」比它的「扫描面」大（2026-09-18 登记，`_probe-cancel-propagation-scan.ps1`）**

    **现象**：该脚本首行注释与判据段**写清了**扫描面 = `FfmpegRunner.RunAsync` 的调用点，
    但它打印的 PASS 文案是「**PASS 每个调用点都能收到取消令牌**」—— **把限定条件丢了**。
    读者（或下一个人）会把它读成「所有子进程调用点都受控」。

    **实测规模**：全仓直连 `Process.Start(` 有 **65 处**（`QueueProcessor.cs` 11 · `ExifToolService.cs` 10 ·
    `ExternalToolsDetector.cs` 6 · `QualityAnalysisService.cs` 4 · `GainMapDecoder.cs` 4 ·
    `ColorMapping/RawColorPipeline.cs` 4 · …），**全部不在该门禁的扫描面内**。
    ⚠ **这不等于「这 65 处都有问题」** —— 那需要独立审计；本条只登记「**门禁的措辞比它的扫描面大**」
    （与第 50/58 条同族：**门禁让人以为有覆盖，而实际覆盖面更小**）。

    **已发现的一个真实受害者（同轮，见 `docs/HANDOVER.md` 的 2026-09-18 🔵 小节 ④）**：
    `QueueProcessor.cs:4293-4297` 的 `IsAnimatedJxl` 直连 `Process.Start`，
    **既无 `CancellationToken` 形参，其 30 秒超时也因写在同步 `ReadToEnd()` 之后而不可达**
    ⇒ 实测 `.jxl` + 1 字节（首字节 `00`/`FF`）可让队列**永久挂死**、「停止」也杀不掉。

    **修法（两条都要）**：
    ① PASS 文案补上限定词（如「`FfmpegRunner.RunAsync` 的每个调用点…」），**不得让措辞大于判据**；
    ② 若要真正覆盖直连站点，应**另立一条**独立锁（不要把它塞进本脚本 —— 判据不同，混在一起会让两者都变模糊）。

    **✅ 2026-09-18 已修 ①（接手会话）**：PASS 文案改为
    「PASS **`FfmpegRunner.RunAsync`** 的每个调用点都能收到取消令牌…」，并在其下**追加一行显式声明扫描面局限**：
    「⚠ 本结论**只覆盖 `FfmpegRunner.RunAsync` 调用点**；全仓直连 `Process.Start(` 的 65 处**不在扫描面内**（另有独立风险，需单独审计）」；
    FAIL 文案同样补上限定词；脚本内以注释记下**已知的真实受害者**（`IsAnimatedJxl` 曾直连 `Process.Start`、无 ct、超时不可达 ⇒ 队列永久挂死）。
    **验证**：`verify-ps-compat.ps1` **13/0**；门禁本体 **PS 7 `exit=0`** 与 **PS 5.1 `exit=0`**（真跑，非静态扫描），
    读数 `FfmpegRunner.RunAsync 调用点：24 个（已传 ct 23 / 豁免 1 / 漏传 0）` —— **判据一条未动，只改措辞**。
    ⚠ **② 仍未做**（直连 `Process.Start` 站点的独立锁）⇒ 保持为待办。
    **✅ 2026-09-18 ② 已落地**：新增 `tests/scripts/_probe-sync-read-before-wait-scan.ps1`
    （判据只取「同一进程变量上，同步 `ReadToEnd()` 不得早于 `WaitForExit(...)`」；
    A 类直连 63 处 + B 类实例化后 `.Start()` 7 处，分开统计；基线「不得增加」= 10 / 0）
    —— 详见 **第 93 条**。

88. **「去掉一个参数后逐字重跑」做 no-op 证明时，逐字节比会被**后处理**判成假红 —— 用 PSNR（2026-09-18 登记，`verify-anim-static-target.ps1`）**

    **场景**：要证明「给单帧输入插 `-frames:v 1` 是 no-op」，最直接的办法是把该参数从
    **实际执行过的命令行**里去掉、**逐字重跑**，再比两次产物。

    **陷阱**：比 **SHA256 会假红**。实测 `single.tiff → --format tiff`（同一二进制、同一命令）：
      · exe 产物 `594242 B` / `sha256=761DCCBC261F…`
      · 去参数重跑 `594241 B` / `sha256=C0C77B5DFC91…`
      · `cmp -l` 报 **402606 字节不同** ⇒ 逐字节断言**必红**；
      · 但**解码后**的 `rgb24` 原始像素 `md5` **完全相同**（`edc97f089d8bc43095fa89904cab45d7`）。
    **成因（本会话实测到根因，不是推测）**：产物里嵌的 ICC profile 带 **`ProfileDateTime` = 编码时的墙钟**
    （`iccgen` 生成）。实测同一二进制、同一输入、同一命令**连跑两次** `single.png → --format jpg`：
      · `17350 B` / `sha256=D8B60BC44F93…`，ICC `ProfileDateTime = 2026:09:17 22:32:59`
      · `17350 B` / `sha256=4DA925D6CAC5…`，ICC `ProfileDateTime = 2026:09:17 22:33:01`
      · 两者**像素** `PSNR average:inf`（`ffmpeg -i A -i B -lavfi psnr`）
    ⇒ **「逐字节比」在这条管线上根本不成立**（不是"有时不成立"）：**同一份代码的两次运行**就不同字节。
    ⚠ 素材侧是确定的（`testsrc2=s=512x384` 连造两次 PNG 的 sha 完全相同 `5FC46E03258A…`）⇒ 非确定性**只在产物侧**。
    ⚠ 由此可解释一个极易误判的现象：跨秒运行 ⇒ 字节不同；**同一秒内**跑完的两个文件（如 exe 产物 vs 手工重跑）
    字节**恰好相同** ⇒ 只看那一次会得出**相反**的结论（同轮 `single.png→jpg` / `single.jpg→png` 两格就是这样"相同"的，
    而 `single.tiff→tiff` 跨秒 ⇒ 402606 字节不同）。

    **判据**：改用**像素级**同一性 —— `ffmpeg -hide_banner -i A -i B -lavfi psnr -f null -`，
    只有**逐像素相同**才报 `average:inf`；`sha256` **只打印供人核对，不作断言**。

    **同族**：第 79 条「判据不应比失效模式更紧」的又一活例；也与第 50/58/74 条同族
    （**都在防"断言没有牙"，本条防的是"断言过紧 ⇒ 把正确判成回归"**）。
    ⚠ **通则**：凡「重跑同一条命令再比对」的 no-op 证明，先问一句**产物里有没有非确定性成分**
    （时间戳 / 后处理 / 元数据），有则必须降到**语义层**（像素 / PSNR）比。

---

89. **进程挂住时，CLI 的 stdout 日志**不是**可用的判据 —— 块缓冲 + 事件驱动的增量转储（2026-09-18 登记，`verify-jxl-probe-timeout.ps1`）**

    **场景**：要给「畸形 `.jxl` 让探测挂死」写门禁，最自然的断言是「应用必须在限时内打印
    `[jxl] 帧数探测超时（<N>s）⇒ …` 这行日志」（`<N>` = 实际生效的超时秒数：生产默认 120 s，
    门禁为控制耗时注入 `FFMPEGGUI_JXL_PROBE_TIMEOUT_SEC=5`，日志行**打印实际值**，不写死）。

    **陷阱**：**这行日志会生成，但看不见**。实测（`--headless --log-level debug`，
    `-RedirectStandardOutput <file>`，输入 = 1 字节 `FF` 的 `.jxl`）：
      · `t=3 s … t=42 s` 每 3 s 采一次，stdout 文件**恒为 259 字节**（只有启动横幅 + 两行 `[1/1] …`），
        全程 `超时行=False`；
      · 同一轮结束（被杀）后读，仍只有 259 字节 ⇒ **不是"读早了"，是真的没落盘**。
    **成因（两条叠加，都实测确认）**：
      ① 应用 stdout 重定向到**文件**时是**块缓冲**（不是行缓冲）；
      ② `ConsoleQueueObserver` 只在**下一次进度事件**才转储 `item.Log` 的**增量**
         （`ConsoleQueueObserver.cs:160-173`：`tail = log.Substring(seen)`）。
      ⇒ 挂住时既没有新输出、也没有新的进度事件 ⇒ **缓冲永不刷新**。
      对照：一旦 ffmpeg 起来吐 banner（会触发进度事件），先前**积压**的 `[jxl] …` 行会**一次性**刷出来
      ⇒ 于是「日志行可见」这件事**与「执行推进到了下一阶段」耦合**，而不是与「日志被生成」耦合。

    **后果（为什么这条重要）**：若门禁只断言日志行，则**两种完全不同的缺陷**会产生**同一个红**：
      · 站点 1 没修（日志根本没生成）↔ 站点 2 没修（日志生成了但推不到下一阶段 ⇒ 刷不出来）
      ⇒ 门禁**失去定位能力**。实测第一次写出来的版本就是这样：M1（回退站点 1）与 M2（回退站点 2）
      读数**逐字相同**（都是 `PASS=18 FAIL=9`）。

    **判据**：门禁的**主证据必须是子进程命令行**（`Get-CimInstance Win32_Process` 按 `ParentProcessId`
    取直接子进程，用参数区分站点：`-count_frames` vs `-show_entries`），断言「出现 → 限时内消失」；
    日志行只作**辅证**（它确实回答"响亮不响亮"，但要如实标注它与阶段推进耦合）。
    ⚠ 采样要**连续 2 次缺失**才算「消失」：`Get-CimInstance` 偶发取不到进程列表，单次缺失会把消失时刻记早。
    ⚠ 同一采样分辨率还会造成**反方向的假阴性**：合法/截断 JXL 的探测只要 ~0.1 s（短于 500 ms 采样周期）
    ⇒ 负控里 `站点1出现` 会是 `False`，那是**采不到**、不是「探测没被走到」⇒ 负控**不得**断言「探测出现过」。
    ⚠ 另一条更省事的思路「用 `--log-file` 拿实时落盘的日志」**实测不通**：
    `ConsoleQueueObserver.cs:68` 的 `_logWriter` 确实 `AutoFlush = true`，但它写的是**同一条增量转储路径**
    ⇒ 实测日志文件同样停在 62 字节，`[jxl] …` 一行都没进去。
    ⚠ 还有一条「用 `Start-Process pwsh` 起一个带超时的包装」**被宿主安全策略拦**，不可用。

    **同族**：第 87 条（门禁的措辞比扫描面大）—— 都在防「断言看起来在测 X，实际测的是 Y」；
    也与第 66 条同族（**"读数会变"是最强信号**）。

---

90. **把「长超时」做成可注入后，必须再用一条「源码形态自检」钉住生产默认值（2026-09-18 登记，`verify-jxl-probe-timeout.ps1`）**

    **场景**：站点 1（`IsAnimatedJxl`）的超时是**实测选定**的 **120 s**（依据 = 大动画 JXL 的
    `-count_frames` 实测耗时：1080p ≈0.023 s/帧、4K ≈0.089、8K ≈0.393）。若门禁按真实值跑，
    3 正例 × 3 个变异态 ≈ **20+ 分钟**，运行器预算撑不住 ⇒ 注入 `FFMPEGGUI_JXL_PROBE_TIMEOUT_SEC=5`。

    **陷阱**：**注入之后，门禁就再也测不到那个常量了**。「注入路径全绿」与「生产默认值本身合理」
    是两件事：若有人把默认改回 20 s、或把注入值接错、或压根没接进 `WaitForExit`，
    **注入态仍然全绿** ⇒ 典型的**假绿**。这与第 87 条同源，但触发面是"**测试替身覆盖了被测常量**"。

    **做法（两条一起用）**：
      ① 日志行打印**实际生效**的秒数（`$"…超时（{timeoutSec}s）…"`，不写死），门禁的 needle 由**注入值算出**
         ⇒ 顺带证明"注入真的生效了"（M2 态实测 `sawTimeout=False`，正是因为它按注入值去匹配）；
      ② 补一条**源码形态自检**：默认常量 == `120`、`WaitForExit(<变量> * 1000)` 真的用了它、
         目标方法体内**不得**再有同步 `StandardOutput.ReadToEnd()`、`Kill(entireProcessTree: true)` 必须在位。
         ⇒ 断言计数 **40 → 41**，且 M1 态因此多红 1 条（19 而不是 18）。
    ⚠ 形态自检必须**先去掉整行注释**再匹配：目标方法的注释里**正引用了旧代码的同步 `ReadToEnd()`**
      （作为"为什么不能这么写"的说明）⇒ 不去注释会**自证假红**。
    ⚠ 注入值**不能太小**：负控（截断真 JXL）也要在同一注入值下跑完 `-count_frames`。
      实测 5 s 够（负控 1.6 s 结束），再小就有假红风险。
    ⚠ 由此留下一条**已登记的覆盖缺口**：本门禁**没有**「大动画 JXL 的探测能在超时内跑完」这条 ——
      构造它需要真多帧 JXL，而注入 5 s 时 1080p 只够 ~200 帧，反而会把合法动画判超时。
      生产默认 120 s 的依据由 `IsAnimatedJxl` 的 `<remarks>`（实测 s/帧）承担，**不由此门禁承担**。

    **同族**：第 87 条（措辞比扫描面大）、第 89 条（同一条缺陷的另一个门禁教训）——
    都在防「断言看起来在测 X，实际测的是 Y」；本条是"X 被测试替身换掉了"这一变体。

---

91. **`verify-ps-compat.ps1` 是静态扫描：PS5.1 上的两个「API/语义差异」它抓不到，必须**真在 PS5.1 里跑**（2026-09-18 登记，`verify-jxl-probe-timeout.ps1`）**

    **场景**：新门禁在 pwsh 7 下全绿（41/0）。按惯例跑了 `verify-ps-compat.ps1` —— **13/0**，
    看起来"双宿主没问题"。**但真在 Windows PowerShell 5.1 里跑，门禁 10+ 分钟不返回。**

    **两个缺陷（都只在 PS5.1 上出现，都是实测确认）**：
      ① **`Process.Kill($true)` 不存在**。PS5.1 跑在 .NET Framework 上，`[System.Diagnostics.Process]`
         **只有无参 `Kill()`**（`GetMethods() | ? Name -eq 'Kill'` 只回 `Void Kill()`）。
         ⇒ `try { $p.Kill($true) } catch { }` **静默失败**、子进程根本没被杀 ⇒ 宿主因
         `-RedirectStandardOutput` 的异步读收尾而**永久挂住**，残留 3 个被测应用 + 3 个 ffmpeg。
         ⇒ 修法：自写 `KillTree`（按 `ParentProcessId` **先子后父**递归，只用无参 `Kill()`），
         并加一段兜底清扫（**只杀自己起过的 PID**，不碰用户可能在跑的 GUI 实例）。
      ② **轮询式 `.ExitCode` 恒为 `$null`**。`Start-Process -PassThru`（**不带 `-Wait`**）的 `.ExitCode`
         在轮询到 `HasExited` 之后读仍是 `<null>`，`Refresh()` 无效（对照：`-Wait -PassThru` ⇒ `1` (Int32)）。
         ⚠⚠ 而 **`[int]$null` 在 PowerShell 里是 `0`** ⇒ 顺手写成 `[int]$p.ExitCode` 会把
         「读不到」伪装成「exit 0」⇒ **假绿，比不测更坏**。
         ⇒ 修法：轮询路径**保留 `$null` 且绝不强转**；需要退出码的断言**单独**用一次 `-Wait -PassThru`
         跑（只对"会在限时内自行结束"的用例可行）；其余断言改用**宿主无关判据**。

    **为什么 `verify-ps-compat.ps1` 抓不到**：它扫的是**语法**（三元 `? :`、`??`、`?.`、`&&`、`||`、
    双引号串里的全角弯引号）—— 那类问题**解析期**就炸。而上面两条是**运行时 API 面/语义差异**：
    代码在 PS5.1 上**能解析、能跑**，只是行为不同。⇒ **静态扫描与真跑不可互相替代。**
    ⚠ 更一般的教训：**「注入一个替身/用某个宿主跑通」不等于「被测的那条路径也被跑通」**（与第 90 条同源）。

    **顺带更正一处旧口径**：新门禁 `②「畸形输入绝不静默成功」` 原先写「若结束则**退出码须非 0** 且零产物」——
    退出码在 PS5.1 上读不到 ⇒ 那条在 PS5.1 上是**真空绿**。现改为宿主无关判据
    「**若它自行结束了，就不得有产物**」。

    **做法（可复用）**：门禁写完先跑 pwsh7，**再真跑一次 PS5.1**（`-ExecutionPolicy Bypass`；
    ⚠ 本机 LocalMachine 执行策略为 Undefined ⇒ **所有**门禁在 PS5.1 下不加 Bypass 都会被拒，
    那是**环境属性**、不是脚本缺陷 —— 实测既有 `verify-ps-compat.ps1` 同样被拒），
    并核对「跑完**零残留进程**」。

---

92. **「活性判据」不能用 CPU 用量，也不能用 `out_time` 推进；双向门禁的负控必须「有牙」且不得真空绿；结构断言的边界不得由被断言属性本身定位（2026-09-18 登记，`verify-ffmpeg-heartbeat.ps1`）**

    **场景**：`FfmpegRunner` 的挂死守卫原为**双信号** —— `lastActivityTicks`（有输出）+
    `cpuAdvanced`（每 30 s 周期累计 CPU ≥ 1 s）。而**忙等型**挂死（畸形 `.jxl` 让 ffmpeg 的
    JXL 解复用器空转）的特征恰好是「**两个流都 0 字节** + **持续烧 CPU**（≈90% 单核）」
    ⇒ `cpuAdvanced` 恒真 ⇒ 不断重置 `lastActivityTicks` ⇒ `InactivityTimeoutMinutes = 30`
    那条分支**结构性不可达**。

    **陷阱 1（先诊断对，再选方案）**：本能反应是「输出不够 ⇒ 加 `-stats` / `-stats_period`」。
    **治不了** —— 问题不在「输出太少」，而在「`cpuAdvanced` 有否决权」。且实测本仓 24 个
    `RunAsync` 调用点**都没有 `-loglevel error`**（默认 loglevel 的 stderr 每帧 `\r` 统计行
    本来就在喂 `NoteActivity()`：8 s 内 60 次事件、18 次含 `frame=`）⇒ 「沉默」时钟一直在被喂，
    加更多输出只会**更吵**。⇒ **先确认失效的那一环，再动它。**

    **陷阱 2（判据选错会误杀合法任务）**：`-progress` 的块里 `out_time_us=` 看着像天然进度，
    于是很自然会写「`out_time` 停走 = 死」。**实测反例**：合法 4K 单帧 libjxl `-effort 9`
    （总耗时 10.3 s）全程 `frame=` **恒 0**、`out_time_us=` **恒 40000** —— 它**不动**。
    ⇒ 用 `out_time` 会把**合法的单帧长编码误杀**。正确判据是「**进度块到达**」（该块由 ffmpeg
    的进度线程**周期性**发出，与帧号推进**解耦**）：实测首块 **t ≈ 0.018 s**、
    间隔 min 0.437 / p50 0.544 / **max 0.574 s**；而忙等型挂死 15 s 内 **0 字节 / 0 块**。
    ⚠ 这是**一般教训**：判据必须**实测**过「合法长任务在最坏情况下它仍然成立」。

    **陷阱 3（心跳信号本身会污染日志）**：`-progress` **默认不限速** —— 合法 4K 单帧编码
    10.3 s 出 **21 块**（≈0.5 s/块、每块 11 行）⇒ 一个 10 分钟的编码 ≈ **8800 行**，
    而 23/24 个回调**每行**都触发 `_onItemUpdated`。⇒ 心跳必须由 `FfmpegRunner` **自己解析、
    不上抛**（本门禁 ③ 直接断言「`-progress` 专属块行 0 条」+「既有 stderr 统计行仍在」）。

    **陷阱 4（门禁自己抓到的真 bug —— 键形态过滤器漏了数字）**：解析端按「键是小写字母/下划线」
    过滤。但 `-progress` 的键里有 **`stream_0_0_q`**（**含数字**）⇒ 那些行**漏进队列日志**，
    实测 **21 行**。⇒ 键形态必须允许数字，并在源码形态自检里**钉住这一条**
    （`c < '0' || c > '9'`）。
    ⚠ 这条是**门禁的实证价值**：读码推断看不出来，只有真跑一次才暴露。

    **陷阱 5（双向门禁的负控必须「有牙」，且必须配「真开了心跳」的自检）**：只写「忙等要在限时内
    判死」是不够的 —— 一个「按**总墙钟**判死」的错误实现也能让它绿，同时把合法长任务**全部误杀**。
    ⇒ 负控必须让注入阈值**远小于**合法任务的总耗时：本门禁注入 **2 s**，三条负控实测
    ③ 15.7 s / ③-d 9.4 s / ③-e 4.4 s（≈8× / 5× / 2×）⇒ 「按总墙钟判死」的实现在负控上**必红**。
    ⚠ 另一件**同等重要**的事：负控只证明「没被误杀」；**漏开站点**会让该负控**真空绿**（合法任务本来
    就会正常结束）⇒ 每条负控都必须再断言「心跳**真的开在这一条上**」（`[心跳] 已追加`）**且**「真的
    走到了该站点」（用**该站点专属的日志行**，如站点 B 的「动图 JXL 不进行 djxl→PNG 中间解码」，
    并断言**不是**兄弟站点的行）。实测：M3/M4（各只回退一个站点）的行为面**全绿**，只有这两条自检红。

    **陷阱 6（形态自检要剥注释 —— 同第 90 条）**：心跳分支的注释里**正写着**
    「这里**故意**不看 `cpuAdvanced`」⇒ 不剥整行注释，那条「分支内不得出现 `cpuAdvanced`」
    的自检会**被自己的注释判红**。

    **陷阱 7（结构断言的**边界**不能是被断言的那个属性本身 —— 循环定义 ⇒ 假绿）**：门禁 ① 要断言
    「心跳分支内不出现 `cpuAdvanced`」，原实现却用 `IndexOf('if (cpuAdvanced)')` 当**段尾标记**
    ⇒ 只要把 `if (cpuAdvanced) continue;` 插在**原来那句之前**，段就被**截短**到突变行之前，
    该断言**反而通过**（M2 首跑实测：门禁 54/5 转红，但这一条竟是**绿**）。
    ⇒ 段尾改用分支的**末句** `return HangExitCode`（它不参与被断言的属性），并加两条**抽取器自检**：
    段内**必须**有该分支专属文案（「无进度心跳」）、**必须没有**旧分支文案（「既无输出也不消耗 CPU」）
    ⇒ 段被截短或越界都会红。
    ⚠ **一般教训**：断言「X 不在区间 [a,b] 内」时，**b 不能由 X 本身定位**；且区间抽取器必须自带
    「抽到的确实是我要的那一段」的自检（否则断言的**对象**悄悄换了，断言还在绿）。

    **陷阱 8（写门禁脚本时的两个静默事故 —— 都是本轮实测踩到的）**：
    - **反引号 + 双引号 = 转义**：PowerShell 双引号串里 `` `" `` 是**转义的双引号**，不是「行内代码
      的收尾反引号 + 字符串收尾引号」。门禁文案里写 `` `-progress`" `` 会让字符串**不在此行闭合**
      ⇒ 吞掉后续若干行，**报错位置在几十行之后**（实测报在 `Write-Host` 那一行，且报的是「数组索引
      表达式缺失或无效」这种**与真实原因无关**的消息）。⇒ 行内代码后面**别紧贴**字符串收尾引号
      （补一个字/空格，如 `` `-progress` 选项``）。同理 `` `$ `` 会把 `$` 转义掉 ⇒ 断言消息里的
      插值**静默失效**（显示字面 `$x` 而不是值）。
    - **`sed -i` 会静默改写行尾**：本仓 `src/*.cs` 是 **CRLF**，Git Bash 的 `sed -i` 把整个文件写成
      **LF** ⇒ 一次 `sed` 就让文件 md5 变了、`git diff` 变成「全文件重写」。⇒ **仓库源文件的变异/还原
      一律用编辑器工具，不用 `sed`**；若已用，务必用「CRLF 计数 + md5 与还原前一致」双重核对
      （⚠ `grep -c $'\r'` 在 Git Bash 的某些 shim 下**不生效**，会假报 0 ⇒ 用 PowerShell 读字节核对。⚠ **这条事实的权威登记在 `.workbuddy-ai/memory/COLOR_TRAPS.md §A`**（`S6` 更早一轮已实测，本轮是**再次踩到**）⇒ 此处只作交叉引用、**不重复表述**（同一个事实写两遍迟早会分叉））。

    **做法（2026-09-19 #15-b(0919) 收尾后为最终形态）**：`RunAsync(..., bool heartbeat = true)` ——
    ⚠ **默认开（opt-out）**，**不是**默认关：类级根因是「未显式开启的调用点一旦忙等就永久挂死」
    ⇒ 逐站点 opt-in 只保护了 4 处，**其余 20 处仍失明**。改默认后**全部 24 处**自动获得心跳；
    把数据写 stdout 的调用由 `CanInjectProgressPipe` **拒绝注入并响亮登记**（不静默降级）。
    JXL 链的 **4** 处 `heartbeat: true` 字面量**保留**（现为**文档锚点**：① 的站点集合断言仍钉住它们）。
    心跳分支的活性判据**收紧为进度块专属**（`lastProgressTicks`；旧的「任意输出」`lastActivityTicks`
    只留给「拒绝注入」的降级路径）⇒ 一行 stderr 噪声不能给忙等续命。心跳模式下**故意忽略** `cpuAdvanced`；
    阈值 `DefaultHeartbeatTimeoutSec = 120` + `FFMPEGGUI_HEARTBEAT_TIMEOUT_SEC` 可注入
    （⇒ 默认值由 ① 的源码形态自检**逐字**钉住 `= 120;`，且断言在**剥注释后的代码**上 —— 第 90 条）。
    ⚠ 开启点用**站点标签注释**（`// #15 心跳站点 <X>`）登记，① 断言的是**站点集合**而不是计数 ——
    计数说不出「是不是这 N 个」（漏开一个 + 误开一个，计数照样对）。
    ⚠ **默认开把残余风险放大了**：120 s 现在由**全部**调用点承担 ⇒ 若某**合法**任务存在
    「> 120 s 无进度块」的窗口就会被误杀。该风险已由 ⑤ 钉住（见下），**不是**靠「我们以为安全」。

    **⑤ 二分性 + 合法无块窗口的实测上界（2026-09-19 新增，4 条）**：N1a 进度流形态自检
    （continue 块 ≥5 / 收尾块 ≥1 / 含 `out_time_us`）；N1b **平均块间隔 ≤ 1.0 s**（实测 0.524–0.553 s
    ⇒ 对 120 s 默认阈值 **≥217× 余量**，直接量本仓 ffmpeg 的 `-progress`，不经被测应用）；
    N2a 两臂注入阈值**相同**且 == 注入值（从**各自日志**解析）；N2b **二分性**（无块臂判死 ∧
    有块臂存活）。⚠ 用「块数 / 总耗时」**比值**而不是「块到达时刻」：ffmpeg 的进度线程**按时间**发块，
    机器越慢块也按比例变少 ⇒ 比值对负载免疫；按时刻采样会抖 ⇒ 假红。

    **变异读数（同一份门禁）**：M1（四处 `heartbeat: false`）**51/8**；M2（把 `cpuAdvanced`
    续命搬进心跳分支内，复现根因形态）**57/4**；M3（只回退站点 A）**57/2**；
    M4（只回退站点 B）**57/2**；交付态 **62/0**（#15(0919) 首轮）。双宿主 pwsh 7.6.6 与 PS5.1 均 **62/0**（第 91 条）。
    **#15-b(0919) 收尾轮新增**：M1′（默认改回 `false`）**62/1**（唯一红 = 默认行为断言）；M2′（活性换回
    `lastActivityTicks`）**62/1**（唯一红 = 进度块专属断言）；M4′（默认 `120 → 5`）**66/1**
    （唯一红 = 逐字防调参锁）；**行为级反事实**：删站点 D 的 `heartbeat: true`（默认=true）⇒ ② 仍**自行判死 13.7 s**；
    同变异再把默认改回 `false` ⇒ ② **挂死 40.1 s 被门禁 Kill**（**原缺陷复现**）⇒ 默认开启是**承重**的。
    交付态 **68/0**（pwsh 7.6.6 与 PS5.1 各一次，均 exit 0）。

    **「回归面为零」必须用实测 + 断言双证，不能靠读码推断**：所有 `[cmd] ffmpeg {args}` 行
    由**调用点**用**自己的局部 `args`** 书写（`QueueProcessor.cs` 25 处）⇒ 在 `RunAsync`
    **内部**追加 `-progress` 后 `[cmd]` 行字节**完全不变**。实测两条会走到该路径的既有门禁：
    `verify-jxl-probe-timeout.ps1` **41/0**（走站点 D）、`verify-jxl-codestream-route.ps1`
    **44/0**（走站点 C），双宿主各一次；再补断言钉住「`[cmd]` 行不含 `-progress`」。

    **同族**：第 90 条（注入后必须钉默认值）、第 91 条（双宿主真跑）、第 87 条（措辞比扫描面大）
    —— 都在防「断言看起来在测 X，实际测的是 Y」；本条新增的变体是
    **「判据本身会误杀合法输入」**、**「负控没有牙 / 负控真空绿」**、
    **「结构断言的边界由被断言属性本身定位（循环定义）」**与**「脚本写作/变异工具引入的静默改动」**。

93. **第 87 条 ② 的落地：直连进程启动站点的独立结构锁（2026-09-18 登记，`_probe-sync-read-before-wait-scan.ps1`）**

    **本条就是第 87 条结尾「② 仍未做」那件事**：为**直连 `Process.Start(`** 的站点另立一条独立锁
    （不塞进 `_probe-cancel-propagation-scan.ps1` —— 判据不同，混在一起会让两者都变模糊）。

    **判据只取一条**（刻意不扩）：在同一**进程变量**上，「同步 `ReadToEnd()`」不得**早于** `WaitForExit(...)`。
    - 只认 `StandardOutput.ReadToEnd()` / `StandardError.ReadToEnd()`（**同步**重载）；`ReadToEndAsync()` 不参与。
    - ⚠ **另外三条属性（超时后是否 `Kill` / 是否杀进程树 / 有无取消通道 / 失败是否可见）一律不进判据** ——
      一条源码扫描同时判四件事只会让断言变糊（第 74 条同族：断言载体选错 ⇒ 恒真）。
    - 覆盖面 **70 处**，**两类分开统计、分开打印**：A 类直连 `Process.Start(` **63** 处；
      B 类 `new Process { ... }` + `<var>.Start()` **7** 处（口径不同，不能混成一个数）。
      ⚠ 登记时为 **72 = 65 + 7**；2026-09-18 `#29` 删掉 2 处**仓内不可达**的残留实现
      （`CjpegliService.TryFindInPath` / `DjxlService.TryFindInPath`，零调用方，见 HANDOVER ⑨）
      ⇒ 降为 **70 = 63 + 7**（删除的 2 处只影响 A 类）。

    **基线口径 = 「不得增加」，不是「必须为 0」**（第 55 条同族：长期红的锁会被无视）：
    A 类基线 **0**（2026-09-18 只读审计实测 **12** 处高危；其中 2 处经 `#27` 实测为**仓内不可达**
    （零调用方）⇒ `#29` 直接删除、基线收紧 **12 → 10**；`#24` 把剩下 10 处**全部修好**
    ⇒ 收尾再收紧 **10 → 0**，team-lead 裁定），断言 `实测 <= 基线`；
    **降了不判红**，但打印余量，余量 > 0 时提示把基线收紧。
    ⚠ 在 **0** 处，「不得增加」**等价于「必须为 0」**：这 10 处是**已修好的真实站点**
    （不是长期红的债）⇒ 一旦回归必须立刻判红。这正是「先登记债 → 修 → 收紧基线」这条流程的终点形态。
    B 类实测 **0** ⇒ 基线 0（它本来就绿，不是长期红）。

    **⚠ 写这条锁时踩到的三个坑（都是实测，不是推演）**：
    - **坑 1（假绿，最隐蔽）**：变量名抽取正则写成 `([A-Za-z_]\w*)\s*=\s*[A-Za-z_][\w\.]*\s*$`
      （`=` 右侧类型路径**必选**）⇒ `using var p = Process.Start(psi);` 里 `=` 与 `Process.Start(` 之间
      **是空的** ⇒ **58/65** 个站点取不到变量名被静默跳过（这是**当时**的语料数；`#29` 后语料为 63），读数只剩「命中 2 处」——
      看起来像「锁很松、债很少」，实际是**判据没跑到**。修法：类型路径必须**可空**（`(?:...)?`）。
      ⚠ 一般教训：**扫描器的「跳过数」必须与「命中数」一起打印**（本锁打印「参与判据 64 / 无进程变量跳过 1」），
      否则「跳过」会伪装成「干净」。
    - **坑 2（假绿，判据退化成「方法内第一次」）**：若判据写成「方法内**第一次**同步读 vs **第一次**等待」，
      则 `ExifToolService.TryFindInPath` 会被漏掉 —— 那里 `p.WaitForExit(5000)`（`:287`）**早于**
      `wp.StandardOutput.ReadToEnd()`（`:306`），取「第一次」会判它干净。⇒ 必须**按变量名配对**（`\b` 词边界，
      故 `p` 不与 `wp`/`proc`/`p2` 互串）。负控 `twoprocs.cs` 专门钉住这一点（只许命中 1 处）。
      ⚠ **该真实实例已被 `#24` 修好**（`ExifToolService.TryFindInPath` 现在 `p` / `wp` 两处都是异步读）
      ⇒ 本坑的**行号引用（`:287` / `:306`）随之作废**，且**真实语料里已无此反例**；
      判据仍由**负控 `twoprocs.cs`** 钉住（负控未随 `#24` 改动，保留原样）。
    - **坑 3（假红）**：`IsAnimatedJxl` 的注释里**正逐字引用**了旧坏代码
      （`StandardOutput.ReadToEnd()` 与 `WaitForExit(30000)`，文本顺序恰好是「读在前」）⇒
      **不剥注释**会把它判成假红（第 90 条同族：形态自检必须剥注释）。负控 `comment.cs` / `trailing.cs` 钉住。

    **负控 8 条（必须「有牙」且「不误伤」，第 92 条）**：坏形态必被抓到（A 类 / B 类各 1）；
    异步读、读在等待之后、整行注释内、行尾注释内 ⇒ **一处都不许命中**；双进程只命中 1；空语料 ⇒ 0/0。
    ⚠ 负控里的 `twoprocs.cs` **本身有 1 处合法命中** ⇒「不得误报」的断言**不能**写成「总数 == 1」，
    必须**按文件点算**（本脚本初版就写错过这一条）。

    **读数（双宿主真跑，非静态扫描）**：pwsh 7.6.6 `exit=0` 与 PS 5.1 `exit=0`，
    均为「扫描 .cs **78** 个（已排除 `bin/` `obj/`，实测不过滤会多算 6 个生成物）·
    A 类 **63** 处（参与判据 **62** / 无进程变量跳过 **1**）· B 类 **7** 处 · 命中 **0 / 0** · 负控 **8/8**」；
    `verify-ps-compat.ps1` **13/0**（本脚本在语料内，解析错 0 / PS7 语法 0）。
    ⚠ 这是 `#24` 修完 10 处形态后的**重跑值**；登记时为 A 类 65（64/1）· 命中 12 / 0，`#29` 后为 63 · 命中 10 / 0。

    **本锁的「有牙」证据（`#24` 后已变化，照实写）**：① 真实语料上**曾**命中 12 处（登记时）→ 10 处（`#29` 后）
    → **0 处**（`#24` 修完后）⇒ **真实语料这条证据当前为零**，「有牙」现**只**由 ② 承担：
    ② 临时语料 8 条负控（证明判据能区分好/坏形态）。⚠ 「扫到了 63 处站点」只证明**扫描面接线通到 `src/`**
    （不是空跑），**不**等于判据有牙 —— 这两件事此前被混在一句话里，此处拆开。
    ⚠ 反向证据（判据认账「修好的形态」）见下方「修形态 ⇒ 计数降」那条。

    **⚠ 两个已登记的缺口（本锁措辞不得大于扫描面，第 87 条）**：
    - ① 若等待被抽到 helper，方法体内就没有 `x.WaitForExit(` ⇒ 该站点判据**落空**、本锁**不表态**。
      实测实例：`CjxlService.cs:226`（`process.Start()` 之后直接 `return await WaitGuardedAsync(process, ct, logCallback);`，
      30 分钟守卫在 helper 里）。
    - ② 本锁只查**源码形态**、不运行任何 exe ⇒ 它**不能**证明任何一处「会挂死」，
      只能证明「**具备该形态的站点数没有增加**」。
    - ✅ **「修形态 ⇒ 计数降」已实测（`#24`，2026-09-18）**：把 10 处命中站点的同步 `ReadToEnd()`
      改成「先挂 `ReadToEndAsync()` → 可达 `WaitForExit(...)` → 超时 `Kill(entireProcessTree: true)`」后，
      本锁读数由 **命中 10 → 命中 0**（pwsh 7.6.6 与 PS 5.1 一致）⇒ **判据确实认账「修好的形态」**。
      ⚠ 登记当轮标为「未实测」的就是这一条；`#29` 的 12 → 10 是**删除**（站点整体消失），不算这条证据。
      ⚠ `$BaseDirect` 已由 **10 收紧为 0**（`#24` 收尾，team-lead 裁定）⇒ 门禁现打印「命中 0 / 余量 0」。
      收紧后**双宿主重跑仍 `exit=0`**（pwsh 7.6.6 与 PS 5.1 读数一致）⇒ 收紧**没有**造出假红。

    **同族**：第 87 条（措辞不得大于扫描面）、第 90 条（剥注释 / 钉默认值）、第 92 条（负控要有牙）、
    第 74 条（判据载体选错 ⇒ 恒真）、第 55 条（长期红的锁会被无视）。

---

94. **「中转产物是固定文件名」的站点：多帧输入必失败；恢复源码做变异时 `Copy-Item` 会保留 mtime ⇒ MSBuild 跳过重建（2026-09-18 登记，`verify-jxr-anim-input.ps1`）**

    **缺陷形态（与第 88 条 / #139 同族，但载体不同）**：ffmpeg 的 `image2` 复用器按**固定文件名**写盘
    ⇒ 任何「把中转产物写成一个**固定路径**的 BMP/TIFF/PNG」的站点，遇到**多帧输入**都会报
    `Cannot write more than one file with the same name. Are you missing the -update option or a sequence pattern?`
    + `Error submitting a packet to the muxer: Invalid argument`，以 **-22** 退出（症状是**整条任务失败、零产物**）。
    JXR 出口有两处这样的站点：`QueueProcessor.ProcessJxrAsync` 的 `input.bmp`/`input.tiff`（legacy）
    与 `RawColorPipeline.EncodeJxrViaJxrEncAppAsync` 的 **alpha 分支** `in.bmp`/`in.tiff`（引擎）。
    ⚠ 引擎的**非 alpha 分支没有**这个问题（唯一输入是一条 rawvideo）⇒ 排查时**先确认走的是哪一支**，
    不要一概而论成「引擎 JXR 出口坏了」。

    **判据（与 #139 同一条）**：**目标容器能不能装多帧**。中转目标恒为单帧容器 ⇒ 恒插 `-frames:v 1`，
    且必须紧贴**输出路径之前**。
    ⚠ `-frames:v` 是**输出侧**选项：放在**第二个 `-i` 之前**会被 ffmpeg 拒绝（实测 `exit=-22`、**无产物**、
    stderr 含 `cannot be applied to input`）⇒「限制 alpha 源那一路」这条修法**结构上不可行**，
    只能限制**输出**（或改滤镜图为 `[1:v]trim=end_frame=1,…`；实测与前者产物**同尺寸**、等价，但更易错）。
    ⚠「加 `-frames:v 1` 会不会让 alpha 与底图**错位**」必须**实测**：该参数取的是**输出**第 0 帧
    = `[0:v]` 帧0 + `[1:v]` 帧0（**同帧号**）。用「3 帧、后两帧全透明」的 alpha 源验证：产物 alphaYMIN16 是
    **不透明值**（取到帧0）而不是 0。
    ⚠ 对单帧输入该参数是 **no-op**，但**不要靠"看起来一样"**：直接对 ffmpeg 两跑对拍、比中转文件**逐字节**
    （`verify-jxr-anim-input.ps1` ③-c 三形态：8bit BMP / 16bit TIFF / alpha TIFF）。
    ⚠ **真空绿守卫**：站点 B 只在「多帧输入 + 未被 `IsAnimated` 拦下 + 带 alpha」时才被走到
    （实测 `.webp` 会走、`.apng` 被 `ColorEngineRouter` 的 `BlockReason=AnimatedInput` 拦在引擎外）
    ⇒ 门禁必须**另断一条**「该格确实走了引擎出口」（两侧互斥标签：有 `[色彩] ffmpeg`、无 `[jxr] Step 1:`），
    否则哪天路由一变，站点 B 的覆盖会**静默消失**而整段仍全绿。
    （本条门禁读数：交付态 **55/0**；变异 M1 仅回退 legacy 站点 **40/14**、M2 仅回退引擎 alpha 分支 **48/6**；
    双宿主 pwsh 7.6.6 与 PS 5.1 均 **55/0**。）

    **陷阱 1（转义陷阱复发 —— 第 92 条陷阱 8 已登记，本轮**又**踩）**：门禁消息里用「反引号 + 选项名 +
    反引号」做行内代码时，若**反引号紧贴收尾双引号**，它会被解析成**转义的双引号** ⇒ 字符串**不闭合**、
    吞掉后续 20+ 行。最坑的是 `[Parser]::ParseFile` 仍报 **0 个错误**（引号整体配平、只是位置全错），
    运行期才在**很远处**报「术语 … 不会被识别为 cmdlet」，指向的行号与被改的那行**毫不相干**。
    定位手法：`ParseFile` 取 token 流，找**跨多行的 `Generic` token**（正常 bareword 只占一行）
    —— 它的起点就是字符串错位的起点。
    ⇒ 新门禁加了「**本脚本自身**卫生自检」（④-b）：非注释行不得出现「反引号紧贴双引号」
    （判据串用 `[char]0x60 + [char]0x22` 拼，否则这一行自己就会命中）。

    **陷阱 2（`Copy-Item` 保留 mtime ⇒ MSBuild 跳过重建，直接制造"读数与源码不符"）**：
    做变异验证时若用 `Copy-Item` 从备份恢复源码，**文件 mtime 被保留为原值** ⇒ 它**早于**变异版的产物
    ⇒ MSBuild 判定「已是最新」**不重建**，你后面的读数来自**变异版二进制**。
    实测：恢复后 4 个 `dotnet build` 各 ~0.7 s（**假成功**），Release DLL 时间戳仍是 M2 那一次。
    ⇒ 变异流程**必须**用 `-t:Rebuild`（或把源文件 mtime 置为当前），并在跑门禁前**核对产物时间戳晚于源码**。
    这一条与第 84 条互补：第 84 条防「跑**中途**被换」，本条防「**你以为重建了、其实没有**」。

    **同族**：第 88 条（no-op 证明用逐字节/PSNR）、第 84 条（mtime 与读数有效性）、第 90 条（形态自检）、
    第 92 条（陷阱 8 转义 + 负控要有牙 + 真空绿）。

---

95. **`ct` 链闭合的独立结构锁：判据只取「近端持有者漏传」，隔跳 ③a 与 ③b **明示在判据面外**（2026-09-18 登记，`_probe-ct-chain-closure-scan.ps1`）**

    **为什么需要（扫描面缺口）**：`#23` 只读审计实测 `src/FfmpegGui` 下**直连 `Process.Start(` 65 处**，
    其中 **18 处**是「**调用点所在方法自己持有 `CancellationToken`**（形参 `ct` / 体内 `linkedToken`），
    却在调用下一跳时**没把 `ct` 交出去**」⇒ 用户按「停止」按不动那一跳里的子进程（清单见 `HANDOVER` ⑨ ③）。
    ⚠ 而当时**没有任何一条锁**管这件事：`_probe-cancel-propagation-scan.ps1` 的扫描面**只有**
    `FfmpegRunner.RunAsync` 调用点；`_probe-sync-read-before-wait-scan.ps1`（第 93 条）管的是
    「同步读有没有架空超时」。本锁补这一格（`#26` 的验收物）。

    **判据（只有一条，三条合取 ⇒ 命中）**：同一文件内，
    ① 被调方法在**同文件内**有**唯一**同名定义，且形参表含 `CancellationToken`；
    ② 该调用的**实参表**里**没有**交出 `ct` / `linkedToken` / `*.Token` / `token`（含具名实参 `ct: ct`）；
    ③ 该调用点**所在方法自己持有** ct —— 形参表含 `CancellationToken`，**或**体内声明了
    `var linkedToken = ...` / 调过 `CreateLinkedTokenSource`。

    ⚠ **条件 ③ 是有意收窄，不是疏漏**，必须照实说清它把什么排除在外：
    - **③b（全链无 `ct`）在面外**：`Program.Main` / Avalonia 事件处理器 / 同步命令生成链
      `BuildArguments` ⇒ 那些地方**没有 ct 可交**，把「漏传」算到它们头上会让断言变糊（第 74 条）。
    - **隔跳 ③a 在面外**：持有者在**更上一跳**、而中间那一跳无形参的形态
      （实测实例：`IsAnimatedJxl` ← 无形参的 `InputMayHaveMultipleFrames` ← `ProcessAsync(ct)`）。
      本锁**不做跨跳上溯**（把整个调用图搬进 pwsh 只会让判据变糊）⇒ 这类**不表态**，另在文档里登记。
    - **同名方法不唯一 ⇒ 判不了**：记入**跳过数**、不参与命中（负控 `skip_overload.cs` 钉住）。
    - 判据只认**代码、不认注释**（`#26` 在源码里逐字引用了「旧写法没传 ct」这类文本 ⇒ 不剥注释会造假红）。

    **基线口径**：命中 **0** 处（「不得增加」在 0 处等价于「必须为 0」）—— 见下方读数与收紧过程。
    降了不判红，但打印余量。

    **负控（8 条临时语料 + 空语料，必须「有牙」且「不误伤」）**：
    坏形态（持有者漏传）**必须**被抓到；交了 `ct` / 具名 `ct: ct` / 交 `linkedToken` /
    **调用者不持有 ct（③b）** / **目标无 ct 形参** / **同名重载（判不了）** / **坏形态只在注释里**
    ⇒ **一处都不许命中**；空语料 ⇒ 0 文件 / 0 判据面内站点 / 0 命中。
    ⚠ 语料里**只有 `bad_holdnotpass.cs` 一处合法命中** ⇒ 「不得误报」的断言按**文件**点算
    （与第 93 条 `twoprocs.cs` 同一个教训：把「总数 == 1」当判据会在负控自带合法命中时写错）。

    **读数（双宿主真跑，非静态扫描）**：pwsh 7.6.6 `exit=0` 与 PS 5.1 `exit=0`，读数**逐字一致**：
    「扫描 .cs **78** 个（排除 `bin/` `obj/`）· 同文件唯一「有 ct 形参」的方法名 **948** 个 ·
    判据面内站点 **53** 处 · 命中 **0 / 0** · 负控 **7/7 断言通过（8 条语料）**」；
    跳过数（**必须打印**，否则跳过会伪装成干净）：目标无 ct 形参 **1472** · 调用者不持有 ct（③b/跨跳）**15** ·
    同名不唯一 **14** 个名字 · 方法头自身（定义处）**966** · 实参表括号未配平（判不了）**89**。
    `verify-ps-compat.ps1` **13/0**。

    **⚠ 写这条锁时踩到的坑（实测，1 条）**：
    **形参名 `$args` 与 PowerShell 自动变量撞名 ⇒ 绑定失效、判据恒真**。`function Test-PassedCt([string]$args)`
    里 `$args` 是 PowerShell 的**自动变量**（未绑定实参数组）⇒ 传进来的实参表**根本没绑上**，
    函数里读到的是空数组 ⇒ 「交没交 ct」恒为「没交」⇒ **真实语料 53/53 全判命中**（假红 100%），
    且**负控也全红**（好形态被误报）—— 而「坏形态必被抓到」那条**照样 PASS**，
    ⇒ 若只看「有牙」那一条会误以为锁是好的。修法：形参改名 `$argText`、局部改名 `$callArgs`。
    ⚠ 一般教训：**「有牙」与「不误伤」必须同时断言**；只有「有牙」时，一个恒真判据也是「有牙」的
    （第 92 条同族）。第二教训：**负控的价值在于它先红** —— 这次是负控（而不是真实语料）暴露了假红。

    **本锁的实战价值（一次真实捕获）**：写完首次双宿主跑出**唯一 1 处命中** ——
    `GainMapDecoder.DecodeToLinearRawAsync` 里调 `DetectBasePrimaries(...)` 没交 `ct`。
    那处**此前已"改过"、工具也回报成功**，但实际**没落盘**（同文件并发编辑被覆盖，见 `HANDOVER` ⑨ 的坑）。
    ⇒ 修掉后读数归 **0/0**。⚠ 这条证明的是「**锁能抓到漏传**」，不是「取消一定生效」（见下方缺口）。

    **⚠ 缺口（本锁措辞不得大于扫描面，第 87 条）**：
    - ① **形态判定，未实证取消生效**：本锁不跑任何 exe ⇒ 它**不能**证明「用户按停止时子进程一定会被终止」，
      只能证明「**「近端持有者漏传 ct」的站点数没有增加**」。
    - ② 隔跳 ③a / ③b / 同名不唯一 / 跨行调用（括号未配平 89 处）**都不在判据面内**（见上）。
    - ③ 已知**留在面外的 ③a 残留**（`#26` 未接，逐条登记，不靠本锁兜）：
      · `QueueProcessor.IsAnimatedJxl`（**隔跳** ⇒ 不在本锁判据面内；其「**永久缓存会被取消毒化**」的设计岔口
        已按 team-lead 裁定落地为「**只缓存『确定』的探测结果**」，证据见 `HANDOVER` ⑨ 与 `§6` 第 96 条）；
      · `FfmpegCommandBuilder.DecideOutputColor(options, inputPath, assignFaithfulPath)` 内的 3 处
        `ProbeInputColorMetadata(...)`（该方法是**同步、无形参 ct**，调用方含 UI 线程的 `BuildArguments`；
        但它**也被**已持有 ct 的 `QueueProcessor.EmbedDroppedFilterIccAsync` 调用 ⇒ 是 ③a，**未接**）；
      · `QualityAnalysisService.AnalyzeAsync` / `GainMapJpegCodec.ResolveEncodeParams` /
        `ColorIntentFactory.FromOptions` 内的 `ProbeInputColorMetadata(...)`（同步签名无 ct ⇒ ③b）；
      · `PlanFolderDetector.Apply` 内的 `ExifToolService.Detect()`（**拿不到 sink** ⇒ 登记为缺口，
        且该类在 `.cs` 内**零命中引用**，见 `HANDOVER` ⑨）。

    **同族**：第 93 条（结构锁 / 跳过数必须打印 / 负控要有牙）、第 87 条（措辞不得大于扫描面）、
    第 92 条（负控要有牙且不得真空绿）、第 74 条（判据载体选错 ⇒ 恒真）、第 90 条（剥注释）。

---

96. **「只缓存**确定**的结果」：一个『缓存负结果』的缺陷，用「逐字日志行的**条数**」而不是「是否出现」来抓（2026-09-18 登记，`verify-jxl-probe-timeout.ps1`）**

    **背景**：`QueueProcessor.IsAnimatedJxl` 用 `AnimatedJxlCache`（**无 TTL、进程内永久**）缓存探测结果，
    而旧实现是 `AnimatedJxlCache.GetOrAdd(inputPath, p => …)` —— **无条件写**。畸形 `.jxl` 的探测会**超时**
    （120 s，见第 89 条同族的实测），此时工厂返回 `false` 并被**永久**写进缓存 ⇒
    把「**我不知道**」固化成「**它不是动画**」：后续所有帧走单帧路径，且**再也不会重新探测**（静默丢帧）。
    （team-lead 裁定：**只缓存「确定」的结果** —— ffprobe **正常退出且拿到帧数**才算确定；
    超时 / 被取消 / 起不来 / 输出不可解析 ⇒ **不写缓存**。）

    **本条的教训（可迁移）**：

    - **① 「缓存了负结果」与「探测失败」在日志上一模一样**，只看「超时行**是否出现**」的断言**恒真**——
      两条路都至少会打 1 条。必须改成**数条数**：该路径一次运行被探测几次，就应有几条。
      （实测：畸形 `.jxl` 路径一次运行被探测 **2 次** ⇒ 修复态 2 条、毒化态 1 条。）
    - **② 判据的载体必须能区分两种实现**。`>= 2` 这个阈值来自**实测的调用次数**，不是拍的；
      故断言文案里**明写**「若将来调用次数降到 1，本断言会因**载体**失效而红（不是缺陷），
      那时须重取载体，**不得**放宽成 `>= 1`」（第 74 条：载体选错 ⇒ 恒真）。
    - **③ 一个缺陷用两个独立载体**：形态自检（缓存写入必须被 `determinate` 守卫、且已无 `GetOrAdd(`）
      + 行为计数（超时行 ≥2 条）。变异时**两条同时转红**，互为交叉验证。
    - **④ 行为载体会反过来暴露**既有断言的**余量不足**：站点 1 现在被探测 2 次且两次**无缝**，
      采样器（500 ms）**采不到**第一次的消失 ⇒ 记录到的「消失时刻」其实是**第二次**的（实测 12.7~13.0 s），
      而原上限正好是 **13 s** ⇒ 差 0.1 s 就假红。⇒ 把上限放宽到 **20 s**，并在注释里写明「记的是第二次」。
      ⚠ 这类「改了被测行为 ⇒ 既有断言的**时间窗**要重新推导」的坑，**静态扫描抓不到**。

    **变异证据（双宿主真跑）**：把守卫行改回 `AnimatedJxlCache[inputPath] = animated;`（无条件写）⇒
    重建后 `verify-jxl-probe-timeout.ps1` **PASS=40 FAIL=4**，红的恰好是新增的 4 条（1 形态 + 3 行为，
    三条行为实测均为「超时行 **1** 条」）；回滚 + **重建**（不能靠 `Copy-Item` 还原 —— 它保留 mtime，
    MSBuild 会跳过重建）⇒ **PASS=45 FAIL=0**（pwsh 7.6.6 与 PS 5.1 各一次，均 45/0）。

    **⚠ 有意付出的代价（已实测、已登记）**：畸形 `.jxl` 的探测**次数 ×2** ⇒ 该路径挂起时长 ×2
    （注入 5 s 时 12.7~13.0 s；生产默认 120 s 时 ≈240 s）。按本仓价值序「**静默远比响亮失败糟**」，
    这是有意的取舍；要压回 1 次需「按队列项作用域」的短命缓存（属新设计，**未拍**）。

    **同族**：第 89 条（块缓冲 ⇒ 别只靠日志行）、第 90 条（注入后必须钉默认值）、
    第 92 条（负控要有牙且不得真空绿）、第 74 条（载体选错 ⇒ 恒真）、第 87 条（措辞不得大于扫描面）。

---

97. **`Start-Process -PassThru`（不带 `-Wait`）的 `.ExitCode` 在 PS 5.1 上恒为 `$null` —— 写成 `-eq 0` 假绿、写成 `-ne 0` 假红（2026-09-18 登记，`verify-avif-anim-probe.ps1`）**

    **现象（本轮首跑实测）**：新门禁在 pwsh 7.6.6 上 **17/0**、在 PS 5.1 上 **14/2**，红的两条是
    `still.avif … exit == 0` 与 `③ 注入 1 s … exit==0`。打印出来是 `exit=`（**空**）⇒ 不是产品缺陷，是**宿主差异**。

    **根因**：Windows PowerShell 5.1 下 `Start-Process -PassThru`（**不带 `-Wait`**）返回的 `Process` 对象
    在子进程结束后 `.ExitCode` **仍是 `$null`**。⚠ 与第 91 条是**同一族**（「静态语法扫描抓不到 API/语义差异」），
    但本条是**新的一处**：`WaitForExit()` 与 `Refresh()` **都救不回来**（实测：`poll_read=[]` / `after_WaitForExit=[]` /
    `after_Refresh=[]`；同一脚本在 pwsh 下三处都是 `1`）。
    ⇒ `[int]$null == 0` 在 PowerShell 里恒真 ⇒ `-eq 0` 把「读不到」伪装成「exit 0」= **假绿**；
    `-ne 0` 则把正常路径判红 = **假红**。

    **修法（本门禁已按此改）**：
    - 判据**一律**换成宿主无关的「**产物数 + 日志形状**」：正常路径 ⇒ `产物 ≥1`；畸形输入 ⇒
      「**若自行结束则不得有产物**」（与 `verify-jxl-probe-timeout.ps1` 的既有口径一致）；
    - 确需退出码的那**一条**（`still.avif` 的回归钉）**单独**用 `-Wait -PassThru` 再跑一次
      （照 `verify-jxl-probe-timeout.ps1` 对 `real.jxl` 的既有做法），并写明「敢用无超时的 `-Wait` 是因为
      它是唯一会自行结束的用例；若将来它开始挂住 ⇒ 那是**真信号**不是假红」。

    **一般教训**：**「跨宿主行为门禁」里凡是用到 `Process` 对象的属性，都要先问「这个属性在 .NET Framework 上
    有没有被填」**。退出码不是唯一一个（第 91 条已登记 `Kill($true)` 重载在 5.1 不存在）—— 判据要挑
    **两个宿主都稳定可读**的量（产物数、文件字节、日志文本），而不是「离得最近的那个属性」。

    **同族**：第 91 条（PS5.1 的 API/语义差异静态扫描抓不到，必须真跑）、第 92 条（负控要有牙且不得真空绿）、
    第 74 条（载体选错 ⇒ 恒真）、第 87 条（措辞不得大于扫描面）。

98. **变异验证（或任何「改源码 ⇒ 重建 ⇒ 跑门禁」的循环）必须独占 build 输出 —— 并发 actor 下两个方向的读数会互相作废（2026-09-18 登记，`#136` 切片 B2）**

    **现象（本轮 B2 交付期间实测；只记可核对的量，不记归属）**：
    - 交付物 mtime 在我**没有写入**的时刻前进：`verify-webp-anim-probe.ps1`（15:05:36 / 15:07:42 /
      15:07:58，新增 `-q:v 75` 加固块、反引号→「」清理、文件头纪律）、`QueueProcessor.cs`（15:10:58）；
    - 门禁用的 exe 在 15:09:51 被重建，且该 build **不含 B2** ⇒ 我当时的 21 红/4 红读数
      **等价于一次「B2 缺失」变异**，读数本身是**正确**的，但**不是**产品回归；
    - `tests/output/validate/` 在 15:10:29→15:27:17 累积了 **6 个非我创建**的 `webpprobe_*` +
      1 个 `avifprobe_37da7af4` ⇒ **同时有别人在跑同一条门禁**。

    ⚠ **归属不做取证**（team-lead 已裁定「归属取证永久停止」；同队 actor 与第三方在**现象层无法区分**，
    继续纠缠只会重复历史上已结案的「幻影写者」误判）。本条登记的是**纪律**，不是**责任人**。

    **要害**：我的变异循环（改成单帧判据 ⇒ 重建 ⇒ 跑门禁）与**并发的**门禁运行**交叉**：
    我的重建让**他们**的门禁读到「exe 中途被重建」⇒ 得到一条**正确的** `④ exe mtime` 红；
    他们的重建又让**我**的变异读数不可信。⇒ **同一份 build 输出不能同时承载「变异体」与「被验体」**。

    **纪律**：
    - 变异验证开工前**确认无并发门禁/重建**；做不到就用**独立输出目录**
      （`dotnet build -p:OutputPath=…` 或把 exe 复制到运行级 GUID 目录）隔离变异体；
    - 门禁里一切「**产物 mtime 新鲜度**」类断言（本仓 `_run-step3-gates.ps1` 的 `[STALE]` 闸、
      各脚本的 `④ exe mtime`）在并发重建下**必然**被污染 ⇒ 这类红**不得**当产品回归；
    - 清理**只用运行时记录的精确路径，禁通配**（本轮 6 个 `webpprobe_*` 只删自己创建的 6 个）。

    **同族**：第 80 条（跨脚本共享 fixture 并发污染）、第 84 条（被测 exe 正在被重建 ⇒ 判据载体被共享）、
    第 83 条（「读数可变」成因归类）、第 86 条（运行器级互斥，**✅ 2026-09-19 已实施**）。

99. **`Start-Process -RedirectStandardOutput` 的重定向文件在子进程退出瞬间仍可能被占用 ⇒ `ReadAllText` 抛 `IOException` ⇒ 日志形状判据**假红**；必须重试，并把「读失败」与「内容不符」分开断言（2026-09-18 登记，`verify-webp-anim-probe.ps1`）**

    **现象**：`③ anim→png` **间歇**假红 —— 同一脚本、同一素材、同一 build，连跑结果不同
    （同一次运行内**自相矛盾**：exe 确实产出了那行 `AnimatedInput` 日志）。

    **根因**：`Start-Process … -RedirectStandardOutput $o` 返回后**立即**
    `[System.IO.File]::ReadAllText($o)`，子进程刚退出时重定向文件**句柄尚未释放** ⇒ 偶发 `IOException`
    ⇒ `$text` 为空 ⇒ 依赖日志文本的 `BlockedAnimated` 判否 ⇒ **假红**。

    **修法**：`ReadTextRetry`（最多 20 次 × 200 ms）**并且**把「读取成功」本身做成一条**点名断言**
    （`readOk`）—— 否则「读失败」会继续**伪装成「内容不符」**，让下一个调查者去改**产品代码**。

    **与第 97 条的关系**：两条都是「PS 5.1 上子进程句柄/属性语义差异」，但**根因不同** ——
    第 97 条是 `Process.ExitCode` 恒 `$null`（**属性没被填**）；本条是**重定向文件被占用**（**文件读不到**）。
    两者的**共同修法**是同一条：判据要挑**两个宿主都稳定可读**的量，且**读失败必须响亮**。

    **同族**：第 97 条、第 83 条（读数可变）、第 85 条（断言自身越界 ⇒ 静默不执行）。

100. **`.ps1` 双引号串内的两种「静默吃字」：反引号被当转义、裸 ASCII 双引号提前闭合 —— `verify-ps-compat.ps1` 抓不到（2026-09-18 登记）**

    **两种形态**（本轮写门禁时实际命中，落盘前已清除）：

    | 形态 | 写法 | 实际结果 |
    |---|---|---|
    | ① 反引号行内代码进双引号串 | `` `format_name` `` | `FF` + `ormat_name`（`` `f `` = 换页） |
    | | `` `nb_read_packets` `` | `LF` + `b_read_packets`（`` `n `` = 换行） |
    | | `` `v:0` `` | `VT` + `:0`（`` `v `` = 垂直制表） |
    | ② 双引号串内嵌裸 ASCII `"` | `CK($ok, "前半句 "后半句"")` | tokenizer 把**一行**切成 3 token ⇒ `CK` 只收到**前半句**，后半句**静默丢弃** |

    **为何危险**：形态 ② **0 解析错误、0 运行错误、断言照常 PASS** ⇒ 门禁的**措辞与它的实际判据不一致**
    而**没人会发现**（同第 74 条：载体选错 ⇒ 恒真；这里更隐蔽 —— 连判据都还在，只是**说明**丢了）。

    **为何门禁抓不到**：`verify-ps-compat.ps1` 是**静态语法扫描**（针对三元 / `??` / `?.` / `&&` / `||`
    等 PS 5.1 不兼容**语法**），而这两类是**字符串语义**问题 —— **语法完全合法**。

    **纪律**：`.ps1` 的**双引号串内**①不得用反引号做行内代码（改用「」）；②不得出现裸 `"`
    （需要引号时用「」或改用单引号串拼接）；③**全角弯引号同样禁用**（跨宿主编码差异）。

    **现状（`#58` 只读扫描）**：**85 个 `.ps1` 上两类命中均为 0** ⇒ 当前语料**干净**。
    ⇒ 本条登记的是**面向未来写者的陷阱**（本轮命中出现在**草稿**里，落盘前已清除），**不是**活缺陷。

101. **读取工具的输出经渲染后可能**凭空插入空白** ⇒ 不得以「打印出来的文本」做逐字比较（2026-09-19 登记）**

    **现象**：**读取工具的输出经渲染**后可能**凭空插入空白**，而**被读的文件里并没有**该空白。
    实测**两处独立**复现（运行器日志 `tests/output/_runner_shape16_smoke.log:3` 与运行器源码 `_run-step3-gates.ps1:573`）：
    两处的 `自身指纹）` 在渲染输出里都显示成 `自身指纹 ）`；**码点级实测**尾部 5 个字符 =
    `33258,36523,25351,32441,65289`（= 自 身 指 纹 ））⇒ **5 个码点中没有 `32`（空格）**
    ⇒ **文件里无空格，空格是渲染产物**。
    ⇒ 另有一处同型实测：`grep -n` 把 `探针超时默认值` 显示成 `探针 超时默认值`，
    而字节级 `grep -c -F` 计「**有空格**」= **0** / 「**无空格**」= **2** ⇒ 结论同上。

    **判据**：**逐字比较一律在脚本内完成、且只输出布尔或计数** —— `-ceq` / `Compare-Object` /
    `grep -c -F "<原串>"` / `od -c`（码点或字节级）。
    ⚠ **任何「先把文本打印出来、再靠人眼比对」的做法，不是「容易看错」，而是「通道不合法」**
    ⇒ **该做法本身不构成证据**（即便两个人看得一致也一样）。

    **有效窗口**：本现象在**跨工具输出**上**均可出现**（实测复现于 `grep` 与 `cat -n`；`Read` 的输出
    **同样经过渲染** ⇒ 亦不可作逐字依据）⇒ 与「用哪个命令」**无关**，**不要**以为换一个工具就安全。

    **与第 78 条「正面 ④」的分工（正交、缺一不可）**：**正面 ④ 管「对象是否静止」**（取证前先证明
    对象没在变）；**本条管「读取通道是否保真」**（读出来的东西是不是原样）。**两者互不替代** ——
    **对象静止但通道失真，取证仍然是错的**。

    ⚠ **与既有「`grep` 只用于定位、定性必读上下文」**不是同一个坑**：那条讲**语义**（命中不等于结论），
    本条讲**字符保真**（命中的**字面**是否原样）⇒ **另立本条，不并入**。

    **本条自身的有效窗口（自指，但必须写）**：本现象由 **2026-09-19** 的实测发现并登记；
    **此前若某核验只依赖渲染输出，其「逐字」性质不受本条的追溯保证** —— 已登记的具体欠账见
    第 78 条的「方法欠账」声明（`Exit-WithSummary` 的 4 个锚点在 **2026-09-19 之前仅经渲染输出核验**，
    **结论未变**，但方法当时不满足本条）。

102. **「运行器自指纹」只报 16 位十六进制前缀 ⇒ 外部对撞判据写成「严格相等」会每轮误报「运行器被改」；且 `Check-SrcDrift` 覆盖不到运行器脚本自身（2026-09-19 登记，`_run-step3-gates.ps1`）**

    **背景（机制，一手读码核实）**：运行器有两条**覆盖面不同**的漂移检查 ——
    · `Check-SrcDrift`（现役 `:583`）**只覆盖** `src/FfmpegGui` 与 `tests/ServiceProbe` **两个源码根**
      ⇒ **运行器脚本自身不在其中**（源码注释原话：「`Check-SrcDrift` 覆盖不到运行器自己」）；
    · `Get-SelfFingerprint`（`:449`）+ `Check-SelfDrift`（`:606`）补上这一格：**跑前**记录自身指纹（打印点 `:578`）、
      **跑后**重测比对（打印点 `:608`），不一致 ⇒ **计入 `$failedItems`**（与 `Check-SrcDrift` **同一处置口径**，不另立宽松档）。
    ⇒ 整轮实测：启动 / 收尾两行**逐位一致**（`runner-self-sha256=7C7A47252B40E1E7 lines=792 bytes=66961`）⇒ 通过。
    ⚠ **它是「运行器脚本」的覆盖，不是「源码」的覆盖** —— 两者**不可互相替代**（`Check-SrcDrift` 仍必须存在）。

    **⚠⚠ 本条登记的陷阱（实测判据缺陷）**：`Get-SelfFingerprint` 打印的是 `$h.Substring(0, 16)`（`:452`）
    ⇒ **只有 16 位十六进制（64 bit）**，**不是**完整 SHA256；而**字段名里含 `sha256`** ⇒ **极易被读成完整值**。
    ⇒ 外部（或另一 agent）拿自己的全量 SHA256 去「对撞」时，**判据必须写成**：
      **「外部全量 SHA256 以自报的 16 位前缀开头（`OrdinalIgnoreCase`）」∧「`lines` 与 `bytes` 逐字相等」**。
    ⇒ ⚠ 若写成**字符串严格相等**（16 位比 64 位）⇒ **每一轮都必然误报「运行器被改」** ⇒ **假红**。
    ⚠ **字段名与值长度不一致是「有意截断」，不是打印 bug**（`Substring(0, 16)` 就在源码里）⇒ **不要「修」它，要修判据**。

    **本条自身的有效窗口**：机制由 **2026-09-19** 落盘（`#28(0919)`）；上列行号锚在运行器快照
    **792 行 / 66961 B / `SHA256=7C7A4725…`（16 位前缀）/ mtime `2026-09-19 12:49:09`** ⇒ **指纹一变即作废**（见第 78 条）。
    配套实测：`shape-selfcheck=OK（**16 针**…）` —— **第 16 针即「自身指纹」**（第 15 针为「读失败不沿用」，登记见 `COLOR_TRAPS §B.9` / 本条上方第 100 条邻域）。

103. **时间型负控的下限如果没有"同夹具实测读数"背书就是许愿；而"归因到自己刚做的改动"的那种解释，落笔前必须先做一票区分的实验（2026-09-23 登记，`verify-ffmpeg-heartbeat.ps1` ③-e）**

    **现象**：整轮 52 条里唯一一条红 = `③-e **负控有牙**：实测总耗时 2.8 s ≥ 3 s`。
    这条的作用是让「按总墙钟判死」的错误实现**照样转红** ⇒ 它要求合法慢任务**明显跑得比注入阈值（2 s）久**。

    **⚠⚠ 我第一版给这条写的归因是错的，被独立复核推翻 —— 错法本身比错更值得留档**：
    我原写"掉的 1 s 正是本班修掉的『启动期同一条 5 级链跑两遍』，因为 `RunApp` 量的是整个应用进程、含启动探测"，
    还拿 `hb_final_ps7.log` 的 **3.8 s** 当"同一夹具的前值"。两条都不成立：
    · `ExternalToolsDetector.ProbeAllTools` **全仓唯一调用点是 `MainWindow.xaml.cs:1248`（GUI Step3）**，
      而 headless 走的是另一个函数（`Program.cs:267` 调 `EnsureAllDetected`）⇒ **那条修复根本不在这条 `elapsed` 的路上**；
    · `hb_final_ps7.log` 是 **2026-09-19** 的 transcript（文件第 3 行自报开始时间），且 HEAD 版 ③-e 素材是
      `testsrc2=s=3840x2160` **不带 noise**、`RunApp` 也**没传 `--quality 0`** ⇒ **不是同一夹具**，两个数不能对撞。

    **真正的结论（这次有落盘背书）**：`≥ 3 s` 这条下限**从来没有一次同夹具的实测读数为它背书** ——
    它写在"4K 噪声 ≈ 3.0 s"那句估算旁边，第一次在整轮里跑就量到 **2.8 s ⇒ 红**。
    ⇒ 可迁移的硬规矩：**给时间型判据设下限时，必须同批留一条"该素材自己的独立耗时"作为背书**
      （最好就是门禁自己打印的那行耗时，且**跑过一次**），否则阈值是许愿不是判据。
    ⚠⚠ 另一条更该记住的：**这个假归因对我的改动有利**（它把一条红解释成"我把启动修快了"的副产品，
      于是既不用改产品也不用承认判据薄）。**归因到自己改动的红，落笔前必须先做一次能一票区分的实验**
      —— 这里那一刀就是"`ProbeAllTools` 到底被谁调用"，`grep` 一条就够，成本几秒。

    **同一根因的前一次（仅系该脚本注释自述，全库无落盘读数 ⇒ 别当实测引用）**：注释写 2026-09-22 之前本条
    实测 **20.7 s**、其中 ~19 s 是探测递归扫全机的开销，探测修快后塌成 **1.3 s**，当时靠"把熵拉满"补回。

    **第一次处置（2026-09-23，做对了三分之一）**：阈值一律不动、**把真实工作量做实** ——
    素材 `testsrc2` 从 `3840x2160` 提到 `7680x4320`（熵已是 `noise=alls=60` ⇒ 像素数回到**线性**杠杆），
    单跑实测 **9.1 s**、空闲机复跑 10.3 s ⇒ 对 `≥ 3 s` 有 ≥3× 余量。`hbSec = 2` 全程未改
    （改阈值 = 用放宽判据换绿灯，本仓禁止）。⚠ 素材形态有硬约束：目标格式必须 **png + `--quality 0`**，
    改成 `--format jxl` 会让本臂从**站点 A** 漂到**站点 C** ⇒ 覆盖不到要覆盖的那个入口分支。

    **第二次翻车与真正的结论（2026-09-24）**：8K 版在**下一次整轮**里又红了，而且不是"下限不够"——
    `③-e 日志不含「无进度心跳」` 与 `产物 1 个（实 0）` 双双转红，读数 **8.8 s / 产物 0**；
    同一条门禁**单跑两次都是 68/0**（③-e 10.3 s / 产物 1），而同一轮里 ③ 由 16.0 s 涨到 23.3 s、
    ③-d 由 10.1 s 涨到 19.4 s ⇒ **是整轮负载，不是抖动**（判据：单跑 vs 整轮同夹具同代码，只差负载）。
    **机制（这条臂的原理性限制）**：站点 A 的目标是**单帧** png + zlib 9 ⇒ ffmpeg 一次编码
    几乎只发一个统计块 ⇒ 「最大无进度窗口」≈「总时长」。于是
    `阈值 > 窗口`（合法任务别被误杀）与 `阈值 < 时长`（墙钟判死的实现必须被抓）变成**互斥**，
    加大工作量只是把两个数一起推大 ⇒ **单帧臂上挂墙钟判据不可能稳**。第一次的红（2.8 s）和这次的红（误杀）
    其实是同一个设计问题的两种表现。
    ⇒ 维护者拍板（2026-09-24）：**③-e 只保留站点 A 的覆盖**，夹具回落到 4K 噪声以缩短窗口；
    "按总墙钟判死必被抓住"这条能力交给 **③ / ③-d 两条多帧臂**的 `elapsed -ge 6`
    （块流持续 ⇒ 与负载解耦；实测 16.0 / 10.1 s ⇒ 余量 2.7–3.8×，**比原来 ③-e 的 1.2× 更强**），
    并在 ③-e 上留一条**形状锁**断言"该能力仍有两处载体"（防搬运变成哪儿都没判）。

    **可迁移的推论（下一个人先按这三条自查）**：
    ① 写「跑得比阈值久」这类**时间型**判据时，余量必须由**与固定开销无关的真实工作量**提供，
      并且**门禁要把 elapsed 打印出来**（本条一直有「耗时=」读数行 ⇒ 才可能对撞出 2.8 / 8.8 / 10.3）；
      但更要紧的是：**先问这项工作的"进度信号节奏"是否独立于它的总时长** ——
      单帧/无中间产出的活儿做不到，只能换成多帧负载（同一族的 ①f 则是换成**计数**判据，
      见 `HANDOVER_2026-09-22 §3.4`）。
    ② **改动任何启动/初始化路径的固定开销之后，回看所有以墙钟计时为牙的判据**（本仓已知只有这一处）。
    ③ 红的时候先做**单跑 vs 整轮**那一对读数：两者同代码同夹具，唯一差别是负载 ⇒
      一次就能把"产品回归 / 判据脆弱 / 机器负载"三者分开（这次靠它避免去"修"一个根本没坏的产品行为）。

    **同一次跑出的另一条读数陷阱（顺手钉住）**：③-e 红时该门禁的**断言总数会从 68 变 67** ——
    因为 ④「运行级目录已清零」那条 `CK` 被包在 `if ($fail -eq 0) { … } else { 只 Write-Host 保留现场 }` 里
    （现役 `verify-ffmpeg-heartbeat.ps1:555-560`）⇒ **红的时候少发一条断言**。
    ⇒ 拿「两次跑出的 PASS 条数对不对得上」当自证之前，先确认这条**条件分支**；本轮它就是被误当成
    "第二个异常"查了一轮，真因是上面那一条红的连带效应。

104. **"只写进备忘录、让消费者去查"式的修复会被形状锁全绿放过 —— 必须有一跑能观察到行为差异（2026-09-23 登记，`RawService.Detect` / `verify-startup-warmup` S8f/S8g）**

    **现象**：给"起不来的 exe 不得算可用"补收口时，第一版做法是让 `ExternalToolsDetector.ProbeAndVersion`
    把 `LaunchFailed` 结论写进 `PlatformServices` 的启动性备忘录，再由 `RawService.Detect` 的六级去查它。
    五条形状锁（记录点在不在、每级查没查、复位调用有没有）**全绿**，`S8f` 行为锁**当场红**：
    把 `FFMPEGGUI_DNGTOOL_PATH` 指到一个只有 `MZ` 头的垃圾 exe、其余各级都换成空目录 ⇒ 面板仍报 `dngtool: ✅`。

    **为什么形状锁抓不到**：`RawService.Detect()` 开头是 `if (_detected) return _detectedPath != null;`
    —— 一次性缓存。GUI Step1 的 `Mk("dngtool", …)` 先跑完并置位 ⇒ 之后 `ProbeAllTools`（Step3）探出的反例
    **永远回流不到队列所读的那个值**。所以"每级都写了 `!IsToolUnlaunchable(...)`"这句话**逐字为真**，
    却在真实时序下恒为"未知 ⇒ 放行"。⇒ **形状锁证明的是"代码写了"，不是"代码会生效"。**

    **正确形态**：收口要落在**消费者自己探测**那一层 —— `Detect()` 内按优先级枚举六级候选、
    逐个 `ProbeExecutable(path, 2000)`、`LaunchFailed` 就换下一级；备忘录降级成"同一路径每进程最多探一次"的优化。

    **这条对判据设计的可迁移结论**：
    ① 凡是"加一个过滤/缓存/注册表"式的修复，落锁时必须有一条**能观察到结果差异**的行为断言，
      且它要构造出**过滤器唯一一次生效的场景**（这里 = 其余各级都给空目录，逼出"唯一候选是坏文件"）。
    ② 行为对要**单变量**：`S8f`（垃圾 MZ ⇒ ❌）与 `S8g`（同一套空目录环境、只换成起得来的真 PE ⇒ ✅）
      共用环境，只差"文件起不起得来" ⇒ 才能排除"名字不认/文件不在"这类混淆解释。
      ⚠ `S8g` 借的是 `ffmpeg.exe` —— 它**不是** dngtool，却必须仍判 ✅；这一条同时锁住"收口判的是可启动性"。
    ③ ⚠ 反过来的坑也踩过：`S8f` 第一版没掐其余各级 ⇒ PLAN 里有好副本 ⇒ 期望 ❌ 却读到 ✅ ⇒
      差点把**正确行为**（跳过坏候选、继续下一级）当成 bug 去"修"。**先确认测量能区分，再怀疑实现。**

    **同批顺带修掉的一处口径不一致**：headless 报告的 `dngtool` 行读 `RawService.IsAvailable`，
    而 `ProbeAllTools` 的 dngtool 行自己拼"手动 → artifacts/PLAN"⇒ 两者级别集合不同。
    本次没有强行统一（属既有分歧、改动面大），只在 §0-B ⑧ 与 §4 第 4 条留痕。

105. **运行器补两项"每步验证"能力：`-Scripts` 子集开关 + 「零断言 ⇒ 红」闸（2026-09-24 登记，`_run-step3-gates.ps1`）**

    **为什么要加（两个都已付出代价的事实）**：
    ① 此前只有"整轮 53 条 ≈ 20 min"与"裸跑单个 `verify-*.ps1`"两个选项，而**裸跑绕开运行器五项保护**
      （仓库锁 / 单步硬超时 / STALE 新鲜度闸 / 双漂移复核 / `_gate_script_*.txt` 留痕）
      ⇒ 结果是一晚启动 6 次整轮（2 次自己中止、1 次被残留锁拒起）≈ 80–100 分钟墙钟。
    ② C 类门禁只要 `exit 0` 就算过，**没人检查它到底跑没跑断言** ⇒ 审计点名 6 条
      "前置缺失即零断言绿灯"（`verify-color-token-normalize.ps1:45-46` 等），与 §6 第 68 条
      「缺参照时 PASS 9→8 而 exit 仍 0」同族。

    **`-Scripts a.ps1,b.ps1` 的三条语义（不要凭直觉改）**：
    · 五项保护**照常生效**，但汇总行强制标注 `（-Scripts 子集 N/M 条 ⇒ 不构成基线）`
      ⇒ 子集绿**不能**当基线用，宣布新基线必须跑全量（这条写在运行器自己的打印里，不靠人记）；
    · 名字不在受管清单内 ⇒ **拒绝跑**（不是静默忽略）——静默忽略正好制造"以为跑过了其实没跑"；
    · 条数锁仍对**全数组**生效（它校验清单本身的完整性，与本次跑几条无关）；
      且 `-SkipScripts` 与 `-Scripts` 同时给出 ⇒ 拒绝跑（前者在脚本段之前就早退）。

    **「零断言 ⇒ 红」闸的判据形状**：取该门禁输出里**所有** `PASS=<n>`（大小写均可）的**最大值**，
    为 0 或一个都找不到 ⇒ 计入 `$failedItems`。
    ⚠ 为什么取 max 而不是"第一条/最后一条"：一条门禁常有多处计数（子步骤 + 总计 + per-probe），
    取第一条会撞上小的子计数、取最后一条会撞上探针读数，**只有 max 回答"到底跑没跑过断言"**。
    ⚠ 两类契约例外**不判**（它们各自有别的承诺）：`$tableOnly`（无 PASS/FAIL 契约）、
    `$noSelfSummary`（只认退出码）；超时与输出读失败也**不重复判**（上面已单独记账，一次故障只该红一条）。

    **两闸都已做过变异验证（不是"写了就算"）**：
    · 子集开关：正常 10 条抽样无一条误伤（含 2 条 `$tableOnly` + 2 条 `$noSelfSummary` 控件）；
      非法名字 ⇒ `RC=1` 点名；冲突 ⇒ `RC=1` 点名；且**顺带验证 STALE 在子集路径上真的生效**
      （第一次子集跑就被 `FfmpegGui.dll < src` 拒起，正是变异脚本"还原晚于重建"造成的真陈旧）。
    · 零断言闸：把 `verify-png3-interop.ps1` 临时改成"`exit 0` + 汇总 `PASS=0`"（模拟缺件静默跳过）
      ⇒ 运行器 `RC=1` 并打两行（现场提示 + `$failedItems` 条目）；变异文件**逐字节还原一致**。
    ⚠ 顺带一条通用坑（本次自己踩的）：**变异/试验脚本的"还原"必须在"重建"之前**，
      否则留下"源码比二进制新"的真 STALE，下一次任何跑动都会被新鲜度闸拒起。

106. **新鲜度（STALE）闸由 2 对扩到 4 对：配对清单的来源是「谁被门禁执行」，不是「有几个 csproj」（2026-09-24 登记，`_run-step3-gates.ps1`）**

    **触发事实（两处，都不是推测）**：
    ① 运行器**自己**在清单注释里把 `tests/UiTestHost` 登记成「已知覆盖缺口（未处置，另议）⇒ 靠手工」
      —— **两处镜像**：本节第 62 条末尾那句"① `tests/UiTestHost` 是第三个项目…"（历史记录**不改写**，
      只在其后加了一条【2026-09-24 更新：① 已处置】指针），以及 `_run-step3-gates.ps1` 清单注释里的同一条
      （后者属"现行说明书"⇒ 已就地改写为「曾有的缺口 + 唯余 GainMapTestHost」）
      ⇒ 5 条 UI 门禁执行的 `UiTestHost.exe` **从来没有被任何新鲜度判据看过一眼**。
    ② **同族事故早就登记过**：§6 第 50 条 ⑤「12 个门禁脚本把 `bin/Debug` 排在 Release 之前 ⇒ 跑的是
      14 小时前的旧二进制；而运行器的硬闸只比探针 bin 目录里那份 `FfmpegGui.dll`，**看不到子脚本用的 exe**
      ⇒ 没拦住」。当时修的是"脚本取哪份 exe"，**闸本身没跟着补** ⇒ 清单内 28 条端到端门禁执行的
      `src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe` 至今不在判据内。

    **配对清单怎么来的（这是本条的可迁移部分）**：先取**受管清单**（用 PowerShell **AST** 解析
    `$scriptList` 的数组字面量，`StringConstantExpressionAst` ⇒ 53 条，避免正则把注释里的名字也数进去），
    再逐条扫该门禁源码里出现的**产物路径字面量**：
    · 引用 `src/**/win-x64/FfmpegGui.exe` ⇒ **28** 条；· 引用 `tests/UiTestHost/**/UiTestHost.exe` ⇒ **5** 条；
    · 引用 `bin/Debug/**/FfmpegGui.exe`（Release 缺位时的兜底）⇒ **27** 条。
    ⚠ 我**先**在运行器消息里凭上一轮记忆写了「被 **24** 条端到端门禁执行」，AST 一数是 **28** ⇒
    措辞已改为**不带条数**（`FfmpegGui.exe(端到端门禁实际执行的产物)`），条数只留在本节与运行器注释里并标"实测"。
    ⇒ **运行时打印的判据文本里不要嵌会漂移的计数**：它既不会被任何锁校验，又比注释更容易被人当成权威口径。

    **最终 4 对判据**（`[STALE]` 段，符号 `$stale` / `Get-NewestSrc`）：
    `src/FfmpegGui/**` ↔ ①探针 bin 里的 `FfmpegGui.dll` ②`src/**/FfmpegGui.exe`；
    `tests/ServiceProbe/**` ↔ `ServiceProbe.exe`；`tests/UiTestHost/**` ↔ `UiTestHost.exe`。
    ⚠ **仍按项目配对**（第 39 条 (a) 的纪律）：`UiTestHost` 只与**自己项目**的源码比 —— 拿它的源码 mtime
    去比 `src` 会造出与产品无关的**假 STALE**（该注释就写在代码里）。

    **变异验证（6 次跑动，全部走子集开关，未跑整轮）**：夹具 = `-Probes selftest -Scripts verify-ps-compat.ps1`
    （单跑 3–13 s），mtime 变异用 `.LastWriteTime=` 赋值 + **逐 tick 还原**，**不用 `touch`**（MSYS 的 `touch`
    在本仓踩过截成 0 字节的坑），还原后同时核对**内容 SHA256 未变**：
    · **A 对照**（工作树本来就新鲜）⇒ `rc=0` / 13.2 s / **0** 条 `[STALE]` / `ps-compat PASS=13 FAIL=0`；
    · **B** 只把 `tests/UiTestHost/Program.cs` 往前推、**不重建** ⇒ `rc=2` / 3.1 s，**只**点名
      `UiTestHost.exe 2026/9/24 4:46:20 < tests/UiTestHost 源码 …（Program.cs）` ⇒ 另三对**无误伤**；
    · **C** 只把 `src/FfmpegGui/Services/RawService.cs` 往前推 ⇒ `rc=2` / 2.7 s，一次点出**两行**
      （`FfmpegGui.dll …` 与 `FfmpegGui.exe(端到端门禁实际执行的产物) …`），且 `UiTestHost.exe` **不**误报；
    · **D** 还原后再跑对照 ⇒ 重新 `rc=0` 无 STALE（证明还原彻底，没留第 105 条那个"还原晚于重建"的尾巴）；
    · **E** 单独复核退出码与锁 ⇒ 拒跑时 `rc=2` 且 `.gates.lock` **无残留**（第 86 条 #147 C(0919) 里
      "STALE 拒跑也必须自己放锁"那条修复仍生效）；
    · **F** `-SkipScripts` 对照 ⇒ `rc=0` / 58.2 s / **0** 条 STALE ⇒ 新配对不误伤"只跑探针"模式。

    **本闸的两条语义边界（写在这里以免被高估）**：
    · mtime 配对只回答「**产物不比源码旧**」，**不**回答「这次改动真的进了产物」。
      ⚠ 该边界在**另一会话**里以"增量构建可能留下旧产物 mtime、要靠 dll 的 SHA 才能证明改动进了二进制"的形式
      出现过 ⇒ 但**本仓尚无专条取证**，所以此处只登记为**待证边界**（下次做 dll SHA 对撞时顺手验一下）。
    · 四对都写成 `if ($src -and $exe -and …)` ⇒ **产物缺失时该对静默跳过**（"缺失"不等于"被拦住"）。
      现状可接受的理由：Release exe 缺位时门禁不会红，而是**换一份产物跑** —— 清单内 **27** 条内嵌
      `bin/Debug/**/FfmpegGui.exe` 兜底路径（AST 实测），而 **Debug 那份根本不在闸内** ⇒ 这正是第 50 条 ⑤ 的族裔。
      ⚠ 已有一半天数：全仓 `tests/scripts/*.ps1` 里 **35 个文件**打印 `[gate] exe=… (Release | Debug fallback)`
      （实测 `grep -l '\[gate\] exe='` 计数，含运行器自己）⇒ **可追溯**，但仍然**不拦**
      （读数只到"知道自己在跑哪份"，不到"跑的是对的那份"）。
      ⇒ 是否改成「缺失即拒跑」是个**取舍**（会连带挡掉"这台机器没建 `UiTestHost`、只想跑 `-Probes`"的合法用法），
      已作为待决策登记在 `docs/HANDOVER_2026-09-22.md` §4 第 9 行。
    · **第三条算是本轮自打**：`tests/GainMapTestHost.exe` 为什么不在闸内 —— 判据取**赋值形态**
      （`tests/scripts/*.ps1` 的**非注释行**里 `$x = … GainMapTestHost` = **0** 处；**同形态**搜 `UiTestHost` =
      **6** 处：运行器 + 5 条 UI 门禁 ⇒ 对照证明这条负断言**有牙**，不是正则写错造成的"看着像 0"）。
      ⚠ 我最初写的是 `grep GainMapTestHost.exe tests/scripts/*.ps1` = 0 命中，**随后被自己推翻**：
      运行器新增的那段注释里就含 `GainMapTestHost.exe` 这个串 ⇒ **负断言被自己的文字满足**，命令不再等于 0。
      ⇒ 可迁移：**文件内容型负断言必须 (a) 把注释/自身文字排除在来源之外，(b) 配一条同形态的正对照**；
      只在注释里解释"为什么 0"是不够的 —— 注释本身就是新的命中来源。

107. **「人读的现量陈述」也要钉到唯一真值源：形态自检第 ⑰ 针（2026-09-24 登记，`_run-step3-gates.ps1`）**

    **已付出的代价（不是假想）**：接第 53 条门禁时，我把 `$expectedScriptCount`、`$scriptList` 数组、
    `docs/TESTING.md` 的「4 + 7 + 42 = 53」都改了，**漏改运行器自己注释里的两处条数散文** ⇒
    之后跑的整轮**照常全绿、无人报错**。原因很机械：**条数锁只比「数组 vs 常量」**，散文不在任何锁的射程里；
    而散文恰恰是后来的人（和我自己）当成"权威口径"直接引用的那一行 ⇒ **它漂移比常量错更害人**，因为没人会去复核注释。

    **判据形状（第 ⑰ 针，接在原 16 针之后）**：先从运行器全文取 `常量定义`，**必须恰好 1 处**（写两处 ⇒ 谁生效取决于顺序），
    取其数字为真值；再要求**三种"现量陈述"形态各命中 ≥1** 且其中数字 == 该真值。
    ⚠ 为什么**不**做成"扫全文所有 `N 条`"：那会撞上历史演进链里的 `42 / 48 / 49 / 50 / 51 / 52` 等**旧读数**
    （历史链按"逐字保留 + 另加现值指针"的规矩**不许改写**）⇒ 只钉**现量陈述**的形态，
    新增一处现量陈述时**必须把它的形态加进这一针**，否则那一处仍然无锁（这一条写在针的注释里）。
    ⚠ 沿用本函数头注释里那条老纪律：本函数读的是**自己的源码** ⇒ 所有 token 一律**拼接**构造，注释里也不得写出连续形式，
    否则"必须出现"型会被针自己满足掉（同义反复）。

    **变异验证（4 次跑动，全走 `-Probes selftest -Scripts verify-ps-compat.ps1` 子集；每次 3–13 s）**：
    · **A 控制** ⇒ `rc=0`，打印 `shape-selfcheck=OK（17 针：… / 自身指纹 / 散文条数）`；
    · **B 改散文数字**（`清单条数以实测为准 = 53 条` → `= 52 条`）⇒ `rc=2` + 点名
      `散文条数与常量不符：清单条数…以实测为准 = 52（常量 = 53）` ⇒ **有牙**；
    · **C 改掉散文形态**（`全跑 53 条(≈20min)` → `全跑 N 条(≈20min)`）⇒ `rc=2` + 点名
      `全跑 N 条 命中 0（散文被删或改了形态 ⇒ 该处失去保护）` ⇒ 证明"命中 ≥1"那一半**不是装饰**
      （只做数字相等的话，删掉散文反而会让针"变干净"）；
    · **D 还原后控制** ⇒ 重新 `rc=0`；三次变异/还原全部用 `ReadAllBytes`/`WriteAllBytes` **逐字节**回滚，
      收尾核对 `byteIdentical=True` 且 SHA `B6C3A927E6CE5965` 未变（不用 `WriteAllText` —— 它会吞 BOM，见下一条 §6 第 108 条）。
      ⚠ 该 SHA 只是**那四次跑动期间**的指纹；做完镜像同步后又动了一次运行器注释 ⇒ 现值以交接文档 §0 那行为准。
    · 四次的 `.gates.lock` 残留均为 **False**（`[SHAPE]` 拒跑路径与 `[STALE]` 一样必须先 `Release-GatesLock`）。

    **镜像同步（针数是"打印出来的字符串"而不是任何判据 ⇒ 全仓 `grep -n '16 针'` 才是权威清单）**：
    本轮实动 **7 处** —— 运行器内 4 处（头部注释「（16 针）」、针清单行首、`shape-selfcheck=OK` 打印串、
    以及给 `[SHAPE]` **新增一行专指散文条数的修法提示**，否则读者照着"退出码取法"那条修法去查会白跑）
    \+ 文档 3 处（交接 §0 的基线行、§3 的待办改闭结、`COLOR_TRAPS` 的针清单）。
    而 `docs/TESTING.md` 第 102 条与 §6 第 92 条邻域里带日期限定的「**2026-09-19 13:46 权威整轮 = 16 针**」是
    **历史读数** ⇒ 保留原文不动（那两处本来就把"以运行器打印的 `OK（N 针…）` 为准"写在句子里，自漂移免疫）。

    **可迁移的一条**：**每一条"现量陈述"要么钉在唯一真值源上，要么就承认它是会漂移的散文**。
    本仓已有三件同族工具（条数锁 = 数组 vs 常量、第 13/14 针 = 数组内部计数、第 ⑰ 针 = 散文 vs 常量），
    前两件都只管机器读的结构 ⇒ 第三件补的正是"人读的那一行"这个空档。

108. **批量改文件的 harness 会自己造错：`WriteAllText` 吞 BOM、"手工去 BOM"吃掉首字符 —— 两处已造成本仓真实损失（2026-09-24 登记）**

    **发生在本轮的两次（都是我自己的 harness，不是被测代码）**：
    · `[System.IO.File]::WriteAllText($p, $txt)` 用的是 **UTF-8 无 BOM** ⇒ 原本带 BOM 的文件被**静默去 BOM**；
    · 为了"补回 BOM"而写的 `ReadAllText` + **手工 `Substring(1)`** 更糟 —— `ReadAllText` **本来就已经剥掉**了 BOM，
      再切一位就切到了真正的首字符：`tests/ServiceProbe/Program.cs` 的 `using` 被削成 `sing`（**23 个 CS 编译错误**），
      而**未跟踪**的 `docs/HANDOVER_2026-09-22.md` 丢了开头的 `# 交` —— 后者**没有 git 副本可回滚**（只能手工重打）。
    · 同一次脚本还给 `Program.cs` **加了** BOM（与"去 BOM"相反的另一个方向）⇒ **两个方向都会错**，
      说明问题不在"猜哪种编码对"，而在**用文本 API 做本不该由它做的字节级事情**。

    **规矩（本轮之后一律照此）**：
    · 批量/变异改文件优先用 **Edit 工具**（逐处替换、不改整文件编码）；必须用脚本时，
      **`ReadAllBytes` → 检测前三字节 `EF BB BF` → `WriteAllBytes` 时按原样写回**，全程不碰文本 API；
    · **还原后必须核对**：`byteIdentical`（逐字节）或至少 **SHA256 与动手前一致**（本轮第 107 条的变异夹具就是这么收口的）；
    · 未跟踪文件**没有回滚路径** ⇒ 动它之前先复制一份到 `tests/output/`（该目录被 ignore，不会污染工作树）。

    **现状核对（2026-09-24 实测，首 6 字节）**：`tests/ServiceProbe/Program.cs` = `75 73 69 6E 67 20`（`using `，无 BOM、
    首字符完好）· `docs/HANDOVER_2026-09-22.md` = `EF BB BF 23 20 E4`（BOM 之后紧跟被回填的 `# 交`）·
    `_run-step3-gates.ps1` = `23 20 5F 72 75 6E`（无 BOM，且第 107 条三次字节级回滚后仍如此）。
    ⚠ 这三行只证明**当下**完好，不证明"中间没被弄坏过"——损失（23 个编译错误 + 一次手工重打文档）确实发生过并被修掉。

    **同一条纪律的**反方向**代价（本轮实测一次，值得记）**：变异跑完后按备份**逐字节还原**
    `ExternalToolsDetector.cs`（内容已等于原版），紧接着的子集跑被 **`[STALE]` 拒了** ——
    `FfmpegGui.dll 06:20:28 < src 06:21:21（ExternalToolsDetector.cs）`。
    ⇒ 这不是判据坏了，而是它**只看 mtime** 的必然结果：**内容相同、mtime 变新 ⇒ 保守误拒**（假红方向，
    代价 = 多跑一次构建；比"假绿"便宜得多，所以**不要**为了好看去放宽它）。
    处置 = 按交接文档 §1 用 **`-t:Rebuild` 同批重建四项目**（`0 警告 0 错误`），复跑子集 4 条全绿
    （`tooldetect 26/0 SKIP=0` · ps-compat 13/0 · cjk 14/0 · startup-warmup 47/0），
    且四份 `FfmpegGui.dll` 全部回到 **`1CD0098625C684C4`**（= 变异前的指纹 ⇒ 产物里没有变异码）。
    ⚠ **别用 `Copy-Item` 拷产物来"修 mtime"** —— 那会撞上 `COLOR_TRAPS §H` 那条反向坑（恢复保留旧 mtime
    ⇒ MSBuild 判"已是最新"跳过重建 ⇒ 后续读数其实来自变异版二进制），比这次误拒危险得多。

109. **SIMD 标签选路从"零判据"到 8 条：变异实测"删掉整段排序，四条老门禁全绿"（2026-09-24 登记，`tooldetect` ⑥）**

    **动因（是归类审计的结果，不是假想）**：`ExternalToolsDetector.ChooseBestExecutable` 里那段
    "按 `CpuFeatureService.GetSimdPriorityTags()` 逐个标签用 `name.Contains(tag)` 收拢候选 → 剩余追加 →
    `generic` 兜住全部"的**选路逻辑此前零判据**。把整段删掉，`tooldetect` 的 ①/②/③/⑤ **一条都不会红**
    —— 因为它们要么只喂单元素候选、要么喂不带特征后缀的文件名 ⇒ 属"有代码没锁"。

    **补的 8 条（⑥a1/a2/a3/premise/b/c/d/e）与设计取舍**：
    · **不重算产品的排序**（那只会做成"构造恒等"自检，产品错我也跟着错）⇒ 断言**外部可观察后果**：
      ① 输入顺序无关（三种顺序喂同一组候选都必须选高优先那份）② 拿掉高优先后必须落到次高
      ③ 高优先位置换成**起不来的**垃圾 ⇒ 必须落到次高（标签优先级不得盖过"起不起来"）
      ④ 全是"带后缀 + 起不来" ⇒ 必须 `null`（末尾兜底不得把带特征后缀的垃圾报成工具）。
    · **夹具前提单独断言**（`⑥-premise`）：从标签表里挑一对**互不为子串**的 `(hi, lo)`，并要求三个文件名
      都不命中比 `hi` 更早的标签、`plain` 不命中任何实标签 —— 否则"期望 = hi 那份"根本不可计算
      （`avx` ⊂ `avx2` 这类包含关系会让判据**静默失去方向**）。本机读数：表 `[avx2, avx, sse4, sse2, generic]`、
      `hi=avx2`、`lo=sse4`、`CPU=x64 v3 avx2`、**SKIP=0**。
      ⚠ 若某台机器的标签表里**找不到**互不为子串的一对，⑥b–⑥e 走**点名 SKIP**（打印整张表），**不算通过**。
    · 空标签这一类失效模式单独钉（`⑥a2`）：判据是 `Contains(tag)` ⇒ 一个空串会让第一轮吞掉全部候选并按
      **逆序**返回（`for i = Count-1 downto 0`）⇒ 选路静默退化成"最后入列者优先"，不抛错、不打日志。

    **变异验证（真改产品码，跑门禁看红）**：把 `var priorityTags = CpuFeatureService.GetSimdPriorityTags();`
    换成 `System.Array.Empty<string>()`（= 选路整段失效）⇒ 重建 `0 警告 0 错误` ⇒
    `tooldetect` **PASS 26 → 24、FAIL=2**，红的正是 ⑥b 与 ⑥c，且失败读数自带"返回第一个"的指纹：
    `实得 zzprobe-sse4.exe / zzprobeplain.exe / zzprobe-avx2.exe`（三种输入顺序给出三个不同答案）。
    ⚠ **⑥d / ⑥e 在该变异下仍绿** —— 它们咬的是"起不起来"，不是顺序 ⇒ 能力归属如实登记，
    别让"两条没红"被误读成"两条无牙"（同 §6 第 ②d 条"受两道冗余保护"那条教训）。
    还原方式 = 动手前把源文件**逐字节备份**、收尾按备份 `WriteAllBytes` 回写并核对
    （`byteIdentical=True`、`ExternalToolsDetector.cs` SHA `083FAC9B3685C863`）；
    还原后重建 ⇒ 四份 `FfmpegGui.dll` 全部回到 **`1CD0098625C684C4`**（与改动前同一指纹 ⇒ 变异没漏进最终产物），
    复跑 `tooldetect` **PASS=26 FAIL=0 SKIP=0**。

    **同一次审计顺手抓到的另一格（本轮**没**动产品）**：`AutoUseSimdBinaries` 这个设置
    ① 由复选框写入并持久化、② 启动时回填复选框，**但没有任何消费者** ——
    `grep -rn 'AutoUseSimd' src tests` 只命中设置模型 / 持久化 / UI 三处；`PsnrCalculator.cs:193-197`
    一律按 `Avx512BW/Avx2/Sse2.IsSupported` 无条件选路。而两条本地化文案都在**承诺**一个回退
    （`tip.simd`："关闭后逐像素回退，仅用于排查" / "turning it off falls back to scalar code"）
    ⇒ **声明与实现冲突**，且该复选框在门禁里同样**零断言**。已登记为 `docs/HANDOVER_2026-09-22.md` §4 第 10 行
    （两档修法 + 代价；本轮不自作主张改产品）。

    **镜像同步**：`tooldetect` 读数 **18/0 → 26/0** —— 交接文档 §0 的"现树定向验证"行与 §7 速查表已改为 26/0；
    §0 的 09-23 晚班单跑行、§2 表格行、§3.4 的"回退后控制组 18/0"三处是**带日期的历史读数** ⇒ 逐字保留。
    ⚠ `tooldetect` **不在**运行器 `$Probes` 默认 21 个 mode 里（由第 51 条门禁自己起）⇒ "探针 21 mode 合计 542"
    这条基线**不受影响**，别去动它。

    **一条刻意留空的判据（登记清楚，别以后当"已覆盖"）**：⑥e 只钉住"带特征后缀 + **起不来** ⇒ 兜底不得交出"。
    另一半**没有锁**：某候选若"**起得来、但探测判它不可用**"，末尾兜底 `launchable[0]` 仍会把带后缀的那份交出去。
    这**不是**缺陷：`ProbeExecutable` 只把"启动抛异常 / `Start` 返 null / 被加载器当场杀（`0xC0000135`/`0xC000007B`/
    `0xC0000142`）"记成 `LaunchFailed`，**超时不算** —— `exiftool(-k).exe` 这类"打印完等你按键"的恒超时工具
    全靠这条活着 ⇒ 一旦改成"探测不可用 ⇒ 不返回"就砍掉一条合法安装形态（同 §6 里那条"超时≠起不来"的决定）。
    ⚠ 为什么本轮**没**给它写判据：要造出"`IsRunnable` 的补集"需要一个"**退出码非 0 且 stdout/stderr 都为空
    且不是加载器错误**"的确定性 exe（`IsRunnable = ExitCode==0 || 两路任一非空`，见 `ExternalToolsDetector.cs:162`），
    本轮没找到这种现成 exe ⇒ **不做半截夹具**（照抄某个系统程序的行为会随版本翻红，那比没判据更糟）。
    要补的话，先解决"合法超时形态"与"收紧兜底"这对矛盾，再谈判据。

110. **`Save()` 的手写克隆表把漏掉的字段"落盘成默认值"——`RenderingMode` 因此丢了渲染后端；两把锁与"有锁但不上锁"第二次（2026-09-24 登记）**

    **症状与机理（一句话）**：`AppSettingsService.Save()` 不序列化 `_current`，而是
    `var clone = new AppSettings { 逐字段 = _current.X }` 再序列化那个 clone（源码注释给了理由：
    不序列化已迁移的 `[Obsolete]` 字段）。⇒ **新加的持久化属性只要忘了进这张表，就不会"丢一次保存"，
    而是被写成模型默认值** —— 用户改完立刻 `Save()`，**值当场被打回默认**。
    ⚠ 这点反直觉：**"文件里明明有这个键"不能否定缺陷**（键在、值是默认）⇒ 只看键名的判据必然漏。

    **实撞到的是 `RenderingMode`**（`Models/AppSettings.cs:70`，默认 `"auto"`）：
    菜单 `MainWindow.xaml.cs:5239` 写它 → `:5243` **立刻 `Save()`** → 启动 `Program.cs:127` 读它选渲染后端；
    而 `grep -c RenderingMode src/FfmpegGui/Services/AppSettingsService.cs` = **0**、
    `git log -S RenderingMode -- .../AppSettingsService.cs` **为空** ⇒ 该字段**从未**被保存过。
    ⇒ 用户选 vulkan 后重启，拿到的是 `auto` 的回退链（`Program.cs:153` = AngleEgl→Vulkan→Software）
    ⇒ **"我选了 Vulkan，它其实先用 ANGLE"**；且同一次点击还会同步 `GpuAcceleration`（那个在表里）⇒
    症状被半掩盖：选 software 看起来有效，选 angle/vulkan 无效。
    ⚠ 缺陷进入代码的确切日期**无法从版本历史确定**（`git log -S` 只追到"补齐版本控制追踪"那次提交）。

    **两把锁（成对，各管一半，缺一半就复发）**：
    · **结构锁 = 第 54 条 `_probe-settings-clone-coverage.ps1`**（纯源码扫描，~1 s，零依赖）：
      比对"模型里真正持久化的属性集合"与"`Save()` 克隆体里的赋值集合"，**双向**判（漏项 + 陈旧项）。
      这次重写成带契约的门禁时补了两条它原先没有的东西：
      **下限断言**（属性数 / 克隆项数各 ≥15 ⇒ 否则"正则失效 ⇒ 集合空 ⇒ 恒绿"这条空断言通道无人守）与
      **有牙自证**（内存里删掉一行赋值再比对）。⚠ 自证的判据写成"**相对基线恰好多报出被删的那一个**"，
      不是"漏项总数 == 1" —— 主缺陷未修时基线漏项本来就非空，写成 `==1` 会让自证与主判据**互相绑死**
      （实测正是这种形态：基线 `{RenderingMode}` + 删掉的 `{FfmpegDirectory}` ⇒ 2 个）。
    · **行为锁 = `ServiceProbe settings` 新增的"全字段落盘往返"**（该 mode **7 → 11** 条 ⇒ 探针基线 542 → **546**）：
      反射枚举"可持久化"属性（排除 `[JsonIgnore]` 计算属性与 `[Obsolete]` 迁移输入），逐个**设成非默认哨兵**
      → `Save()` → 读回 → **逐字段相等**；外加"候选属性 ≥ 20"的反射口径自检与"类型不认识即点名判红"。
      ⚠ 读回**不用** `AppJsonContext`（internal，探针看不见），也不用 `AAS.Load`
      （它会被 `FFMPEGGUI_*` 环境变量覆盖路径/队列/主题 ⇒ 测的就不是落盘了）。

    **变异证据（真改产品码，两次重建）**：删掉 `RenderingMode = _current.RenderingMode,` 这一行 ⇒
    · 结构锁红：`FAIL 每个持久化属性都被 Save() 写回落盘（漏项 = RenderingMode）`（`PASS=7 FAIL=1`、`exit=1`），
      而它内部的**有牙自证仍绿** ⇒ 两条判据各自独立报账；
    · 行为锁红：`FAIL 全部 23 个持久化属性都真的落盘并能读回（不同源 = RenderingMode: 写入 zz-RenderingMode-7 / 读回 auto）`
      （`PROBE RESULT: pass=10 fail=1`）⇒ 这一行读数把"落盘成默认值"的机理**直接印在日志上**。
    还原后：结构锁 **8/0**、行为锁 **11/0**、四项目 `-t:Rebuild` **0 警告 0 错误**、
    四份 `FfmpegGui.dll` 一致 = **`7123B5983DCCAB8A`**（产品码真的变了 ⇒ 与旧基线 `1cd0098625c684c4` 不同，见交接 §0）。

    **两把锁的口径互相印证**：结构锁用**正则扫源码**得"持久化属性 = **23** 个"，行为锁用**反射**
    （`BindingFlags.Public|Instance`，排除 `[JsonIgnore]` 计算属性与 `[Obsolete]` 迁移输入）得候选属性 = **23** 个
    —— 两条完全独立的路径数出同一个数 ⇒ "属性被漏掉"之外还防住"某一方的口径自己失效"。
    ⚠ 但**别把这种一致当成充分**：如果模型某天加一个正则与反射**都**认不出的写法（例如属性拆成多行），
    两边会**一起**变少 ⇒ 数数不会报错。所以两边各自都带了下限断言（结构锁 ≥15+≥15、行为锁 ≥20），
    下限的绝对值来自本次实测，改模型写法时**要重数**而不是放阈值。

    ⚠ 结构锁那条"有牙自证"自身的限制（写清楚，别当成万能）：变异体是**按整行正则删一行赋值**构造的，
      同一行文本若在文件里出现两次会被**一起删掉** ⇒ 计数差值不为 1 ⇒ 自证**响亮报错**（不是静默通过）。
      当前 `AppSettingsService.cs` 里这类赋值行恰好唯一，所以读数可信；改 `Save()` 写法时要记着这条前提。
    **为什么这是"有锁但不上锁"的第二次**：第一次是 `verify-ipc-extra-options.ps1`（2026-09-19 写好、
    09-23 才接线，见 §6 第 92 条邻域与运行器清单注释）。本条脚本 2026-09-15 就存在，
    **和它一样的命运**：写它的那次确实抓到了当时的漏项（`IccDirectory` / `EnableIpcServer`，即 S-P0-1），
    修完就把脚本放回原处 ⇒ **不进清单 ⇒ 之后新增字段没人再跑它** ⇒ 同一个洞又漏了一次。
    ⇒ 可迁移的一条：**"我修好这个缺陷了"不等于"这类缺陷不会再出现"**；防它复发的那条判据
    必须进**每次都跑**的地方（受管清单），否则它只是"修完那一刻的验尸报告"。

111. **简洁模式自带第二套选项真值 ⇒ 降级为覆盖层；顺手把"控件→FfmpegOptions"的 4 份副本收成 1 份（2026-09-26 登记）**

    **机理（一句话）**：同一个"把高级模式控件读成 `FfmpegOptions`"的动作在 `MainWindow.xaml.cs` 里
    有**四份互不相干的拷贝** —— `AddSingleToQueue`（真入队）、`RegenerateCommand`（实时预览）、
    `BuildCommand_Click`（生成命令按钮）、`CreateQueueItemFromSimplePreset`（简洁模式，从 `PresetData`
    手工搬字段）。⇒ 每加一个选项要改四处，历史上确实只补齐过其中两处（代码里
    `2026-08-16 修复: 此前入队路径缺失导致仅预览生效` 与 `2026-08-16 补齐: 此前简洁模式预设丢失以下字段`
    两条注释就是那两次漏改留下的疤）。**用户看到"选项没生效"取决于他走的是哪一份。**
    ⚠ 该缺陷本仓**早已知情**：`IsAnimationMode()` 的注释（2026-09-19，P1-E 任务 C）写明
    "三处 opts 构造一律调它，禁止各自内联索引比较" —— 那是给四处副本之一打的补丁，没收成一处。

    **修法（三件事必须同批，分批必退化）**：
    ① **唯一采集点** `CollectOptionsFromUiAsync(inputPath, logNotices)`：入队与两条预览都调它，
       预览侧传 `logNotices: false`（否则每次改控件重复打那两条 jxl 提示）。
       ⚠ 统一的前提是先给 `EncoderDetectionService.SupportsJxlLosslessJpegAsync()` 加**按 ffmpeg 路径为键**
       的缓存（并挂进 `ClearCache()`）：它每次调用起一个进程，而 `RegenerateCommand` 有 **109** 个触发点
       ⇒ 不缓存就是"改一个下拉框起一个 ffmpeg"。
    ② **简洁模式不再有第二套真值**：删 `CreateQueueItemFromSimplePreset`（**123 行 / 6194 字符**），
       两个入队 handler 改为 `await AddSingleToQueue(path)` ⇒ "简洁模式跟随高级模式"从此是**结构性**的。
    ③ **预设成为完整快照**（否则 ② 必静默退化：简洁模式以前直接读 `PresetData` 就能拿到、
       而现在要经"回放成控件"才拿得到的字段，回放侧根本没写控件）。实际缺口：
       · `PresetData` 补 **4** 条缺失轴：`ConversionMode`（静态/动图/RAW）、`EncoderName`、
         `ColorGamutMap`、`CjxlEffort`（cjxl 面板 effort 与 `JxlEffort` 按后端分叉，此前只存一份）；
       · `BuildPresetData` 补 **13** 项从不写入的（上列 4 轴 + `TiffDpi` + 动图 4 + GIF 2 + 最长边 2）；
       · `ApplyPresetData` 补 **14** 处从不回放的控件（上列 + `AppendPngExtension` + `JpegDct` +
         **`JpegGainMapEnableCheck`** ⇒ 从不回放 GainMap **开关**，预设里的 GainMap 在高级模式永远开不起来）。
       ⚠ 两条硬顺序约束：**模式必须早于格式**回放（`ConversionMode_SelectionChanged` 会 `Items.Clear()`
         重建 `FormatCombo`，三个模式的格式候选集**互斥**），且新增回放块必须排在色彩策略单选**之前**
         （该单选按既有 D-1 约定须是最后一次写入）。

    **旧预设的兼容不是可选的**：`ModeOfPresetFormat()` 在 `ConversionMode` 缺席时按格式推导
    （`GIF` 只存在于 `AnimatedFormats`、`DNG` 只在 RAW ⇒ 推 1 / 2，其余 0）。磁盘上既有的用户预设
    **全部**没有这条轴，不推导就得到"切了 GIF 预设、格式下拉却**静默**保持原值"
    （静态模式没有 GIF 这一项，`SetComboByValue` 找不到就什么都不做）。

    **判据（`UiTestHost`，宿主汇总 340 → **353** 条、`353 PASS / 0 FAIL`）**：
    · **E4（5 条，行为）**：简洁模式选 GIF 内置预设 ⇒ 模式推到动图 **且** 格式真选中 `GIF`；
      再选 PNG 预设 ⇒ 模式拉回静态。E4e 用 `BuildPresetData` 快照 / `ApplyPresetData` 还原，
      避免本组污染后面的组 —— **第一次跑就因为漏了还原把 `J14a-1` 顶红**（预设把 `PngPred` 写成 `mixed`，
      而该断言量的是"面板默认值进命令"）⇒ 顺序耦合真实存在，这条是踩出来的。
    · **E5（5 条，结构锁）**：`CreateQueueItemFromSimplePreset` 必须**零命中**；采集签名
      `UseAdvancedColorParameters = useAdv` 必须**恰好吃到 1 处**（长出第二份采集就红）；采集点引用 ≥4 处；
      两个入队 handler 的**函数体内**必须有 `await AddSingleToQueue(` 且不得自己 `_queueProcessor.Add(`。
      ⚠ ② 之后"入队选项等于高级模式选项"这条行为断言已恒真，所以锁的是"别长回去"而不是"现在对不对"。
    · **N1b（4 条，重写）**：原先这条反射调用那个被删的方法；现在走
      `预设 → ApplyPresetData(真实控件) → CollectOptionsFromUiAsync` ⇒ 同一条锁改量"预设能否完整描述
      高级模式状态"，比原来更强。⚠ 反射拿到的 async 方法用 `Pump` 轮询收敛，**不** `.Wait()`
      （会饿死 UI 线程续体）。

    **变异证据**：把 `ApplyPresetData` 的模式回放改成 `if (false && presetMode.HasValue ...)`、Release
    重建 ⇒ `E4b mode=0`、`E4c fmt=JPEG XL`（静态模式候选集里没有 GIF ⇒ 下拉**停在原选中项**）两条转红
    （`348 PASS / 2 FAIL`、`exit=2`）⇒ 新锁有牙，且把"静默选不中"的形态直接印在读数上。

    **其余读数**：`_probe-cjk-hardcode-scan` UI 面 **833 ≤ 基线 837**（净 **−4**：删掉的映射带的中文
    字面量比新增的多；新增说明一律写成整行 `//` 注释，该门禁剥注释 ⇒ 不计）。
    **整轮绑定**：`_run-step3-gates.ps1` 在 pwsh **7.6.6** 下 54 条**全部通过**、STALE **0**、
    宿主 **353/0**；四份 `FfmpegGui.dll` 一致 = **`354ed9319506e91f`**（本轮读数绑这个指纹；
    追加修正前的中间一轮是 `d3d90c04191bbc34`，`54` 条同样全绿，可当同向对照）。
    运行器自哈希未变 = `FE846C90C18EB85A lines=1075` ⇒ 本轮**没有**动过运行器
    （其中"宿主断言总数 340"那句注释**故意留在原值未改** —— 改它会让自哈希校验在下一轮报"运行器被改过"，
    那句注释该由下一次动运行器的人一并校准）。
    `verify-ui-host` / `verify-ui-param-matrix` / `verify-ui-param-defects` / `verify-ui-strategy-map` /
    `verify-cli-strict` 五条单跑亦 `exit=0`。

    **同批的三处追加修正（第一版漏掉的，都是"补全反而引入的新差异"）**：
    · **`PresetData.AnimationLoop` 改成 `int?`**。它是**既有字段**、schema 默认 `-1`（无限循环），
      而循环框出厂是**空**（采集侧 `ParseInt(text, 0, …)` ⇒ 空 = 0）。给"从不回放"的字段补上回放之后，
      **老预设**（JSON 里根本没有这个键）会从"不动控件"变成"写 `-1`" ⇒ 产物从播一次变成不停播。
      ⇒ 判据 **E6（3 条）**：手工构造 `{"format":"GIF","quality":85}` 走真实 `FromJson` → `ApplyPresetData`，
      要求循环框与 fps 框**仍是空**，外加"旧预设的该字段解析为 null"这条前提锁。
      ⚠ 同一族还有 `GifPaletteOptimize` / `AppendPngExtension` / `EnableMaxDimension` / `MaxDimension`：
      它们的 schema 默认值与 XAML 默认值**恰好相同** ⇒ 无条件回放在今天无差异，故**没有**跟着改可空
      （改了只多一层噪音）；哪天两者不一致，就要按 `AnimationLoop` 这条路处理。
      ⚠ 顺带统一了 CLI 侧 `FfmpegOptions.ApplyPresetData`（原来无条件 `AnimationLoop = p.AnimationLoop`，
      现在与上一行 `AnimationFps` 一样带 `HasValue` 门）。
    · **「生成命令」按钮改回带提示**（`logNotices: true`）：统一采集后它继承了 RAW 守卫，
      原先在 RAW 模式选非 RAW 文件会**静默什么都不做**；`RegenerateCommand` 仍传 `false`
      （它挂在 109 个触发点上，会重复打那两条 jxl 提示）。
    · **BOM 被自己的批量脚本吞掉**：脚本用 `UTF8Encoding($true)` + `WriteAllBytes($enc.GetBytes(..))`
      还原 BOM，而 .NET 的 `GetBytes` **永远不带 preamble** ⇒ `MainWindow.xaml.cs` 首 3 字节没了；
      更糟的是脚本打印的 `bom_preserved=True` 取自**它读到的输入标志**，不是输出 ⇒ 自报假证。
      编译器与门禁全都无感（BOM 不参与编译语义）。已按字节补回并**独立**用 `head -c 16` 与
      `git show HEAD:…` 的首行逐字节核对（`efbbbf 7573696e67` = BOM + `using`）。
      ⇒ 教训与 §6 第 110 条同族：**harness 的自检读数必须取自输出，不能取自"我打算怎么做"的那个参数。**

    **明确未收的账（记在这儿，别当成已完事）**：
    ① 预览侧 `jxlLosslessJpeg` 的判定式与采集侧仍**不完全同**（少一层 `useAdvancedCodec` 门、不含 cjxl
      直连分支），而它还驱动 `LockLosslessForJxl` / `RestoreLosslessAndQuality` 反向改控件 ⇒ 本轮
      **保留预览原行为**（在 `RegenerateCommand` 里显式覆盖 `opts.JxlLosslessJpeg`），没敢同批改；
    ② `SvtStillPictureCheck` 是**死控件**：XAML 里有、只挂 `RegenerateCommand` 回调、全仓无 `?.IsChecked`
      消费方 ⇒ SVT 面板那个"单图"复选框点了什么都不改（本次审计抓到，未动）；
    ③ `PresetData.Concurrency` 与 `MaxQueueSize` 被 `BuildPresetData` 写成**同一个值**（并发数），
      而 `ApplyPresetData` 只读 `MaxQueueSize` ⇒ 前者是死字段；
    ④ 简洁模式预设下拉停留在"最后点的那条"，用户回高级模式手改后不会变成"自定义" ⇒ 真值已是控件状态
      （正确），只是**标签滞后**，属显示问题不是正确性问题；
    ⑤ 内置 30 条预设仍不显式携带 `ConversionMode`，靠格式推导兜底（其中只有 GIF 一条需要非静态模式）。

112. **第 55 条 `_probe-matrix-structure.ps1`：矩阵清单的行锚定取证必须秒级上清单，不能绑在 2.5 h 长跑上（2026-09-27 登记，任务 #42）**

    **事实链**：`_lib-matrix.ps1` 的折叠表与语义矩阵每条都带 `Evidence = '<repo 相对路径>:<行号>|<字面片段>'`，
    `Test-MatrixStructure` 的 A21（折叠 11 条）/A30（矩阵 10 条）会**真的去读那一行**核对片段还在不在。
    但调用它的只有 `Invoke-MatrixSelfTest`，而那个此前**只有 `verify-oracle-matrix.ps1` 会跑** ——
    一条**有意不入清单**的 2.5 h 长跑门禁 ⇒ 行号漂移要等某个手动跑它的人偶然看见。
    两笔账：**2026-09-27 上午核账发现 6/19 条早就漂了**（已逐条修复并登记）；**接线当天又漂一条** ——
    #39/#43 往 `FfmpegCommandBuilder.cs` 插注释 ⇒ `metadata-core` 的 pin 从 2011 抬到 2041，
    当场被 A30 报成 `literal not on ... :2011 (found at line 2041)`。**这就是这条门禁存在的理由，不是假想**。

    **接法（最小改动）**：不复制判据 —— 新门禁只 dot-source `_lib-matrix.ps1`（库注释保证 dot-source 零副作用，
    含 `_lib-axes.ps1` 的 param 覆盖陷阱处置）并调 `Invoke-MatrixSelfTest`，
    成本实测**单遍 5.5 s / 两遍 ≈ 9 s**，不起产品进程、不产生任何转换。

    **三处 fail-closed 守卫**（都是本仓踩过的形态）：① 结果行 `[MATRIX RESULT: pass=… fail=…]` 缺失 ⇒
    直接红并**中止**（不许"读不到就当过"，因为库内那个 param 覆盖陷阱的历史症状恰好就是"静默零输出 + 退出码 0"）；
    ② 自检断言总数下限 30（现测 38 = A1..A31 + B1..B7）⇒ 有人删检查项而不是删被检查的东西时会响；
    ③ A21/A30 的 `checked=` 必须 ≥ 1 —— 两条都是**负断言**，取证清单为空时它们**恒绿**。
    另加两本账对账：打印出的汇总 vs 库内 `$script:MatrixSelfTestResult`（同一事实两个写者，不等即红）。

    **有牙证明**（当场跑过，不是引用库内 B 段）：
    变异 = 把 `metadata-core` 的行号从 2041 改成 2040 ⇒ 本门禁 `pass=7 fail=1`、红的正是"结构自检零红"那条、
    **退出码 1**；改回 ⇒ `8/0`。库自带的变异注入（删一对两两覆盖 ⇒ A7 点名 `missing=14`）由本门禁的第二遍把守，
    它红 ⇒ 本门禁红。

    ⚠ 接线时同步的四处账（漏任何一处都会被判漂移，见 §3.4 的"增删一条门禁要动的账"全清单）：
    `$scriptList` +1 ⇒ `$expectedScriptCount` 54 → 55 ⇒ 运行器三处散文条数（第 ⑰ 针）⇒
    本章 `4 + 7 + N` 分解（`43 → 44`）与未受管台账（54/93/39 → **55/95/40**，D 类 5 → 6）。
    `$tableOnly` / `$noSelfSummary` **不动**（新门禁自带 `PASS/FAIL` 契约 ⇒ C 类）。
    ⚠ **接线必然改运行器自身** ⇒ 运行器自哈希 `FE846C90C18EB85A lines=1075 bytes=95990` →
    **`27D6616B146A0725 lines=1092 bytes=98049`**（2026-09-27 那轮 55/55 起/收尾逐位一致）。
    历史各轮记录的是**各自当时**的哈希 ⇒ 拿现值去比对旧行的"同一把尺"前提**不再成立**，引用要连哈希一起引。

113. **第 56 条 `_probe-stderr-merge-scan.ps1`：合并式取数（stdout+stderr）读"工具的值"必须是显式例外，不能是默认（2026-09-27 登记，任务 #51）**
    **症状形状**（不是"跑不过"，是"跑过了但结论是假的"）：随包 exiftool 是 **perl 打包**的，进程环境里带着本机 perl
    不认的 `LC_ALL` / `LANG`（从 MSYS/bash 侧起门禁就会出现 `C.UTF-8`）⇒ **每次调用先往 stderr 喷 locale warning**。
    于是一个"把 stdout 与 stderr 拼起来返回"的 fetch helper，读回来的**值**其实是警告文本。两种坏法都实测出现过：
    ① **假绿** —— 负断言被撑成恒真：`'' -notmatch 'a'`（"素材确实不透明"在读不到时照样通过）、
    两侧同污时 `-ne` 变成"两条警告互比"（`run-pipeline-tests.ps1` 的 `ColorMatrix1` 就是这个形状）；
    ② **假红** —— 值前面多一行警告，归一化没剥干净 ⇒ 两格对照判"不同"。
    **本轮收口的五处**（各配**正对照**：同一把尺必须先在"真有值"的素材上读到东西，负断言才不是空转）：
    `verify-engine-alpha-preserve.ps1`（`Get-PixFmt` 只读 stdout + 空值返回 `$null`，三处消费点先验 `$null`）·
    `run-pipeline-tests.ps1`（`ColorMatrix1` 两行改用文件内既有的 `ExifVal`；四行 ffprobe 改 `ExecStdoutOnly`；
    **取数为空即 throw**）· `verify-color-caps.ps1`（新增 `ExecOut`，`IccOf`/`CicpOf` 改只读 +
    「前置：IccOf 在已知带 ICC 的素材上读得到」**12/0 → 16/0**）· `verify-color-strategy.ps1`
    （新增 `ExecOut`，`$cicp`/`$hasIcc`/`Lock-DeliveredDepth` 改只读 + 「前置-取数尺有牙」**77/0 → 78/0**）。
    ⚠ **运行器另加一段**：整轮起头清 `LC_ALL`/`LANG`/`LC_CTYPE` 并打印 `env-cleaned=…` —— 但那是**减少触发面**，
    **不是修法**：门禁仍可能在别的宿主下被起（清单外手工跑、别的 shell），所以判据必须落在**取数那层**。
    **这把尺的能力边界（必须写清，别当数据流分析）**：只做**文件级共现** ——「同文件里有合并 fetch」+「用该合并
    helper 调 exiftool/ffprobe」⇒ 记一个站点。它**不追踪变量流向**，所以"读了值但只打印/只当副作用"也会被计入 ⇒
    基线因此是**债账**不是达标线：**实测 84**（`-ManagedOnly` 口径 43），本轮收口前是 89。
    判据三档：① 站点数 **≤ 84**（棘轮，新增必红，除非调用行留字面『合并取数已论证』说明为什么值只在 stderr）；
    ② **已收口四文件的逐文件残留数**（caps=5、strategy=0、run-pipeline=0、alpha-preserve=0）——
    残留逐个在注释里写明"只打印 / 写入 / 抽取到文件、返回值不喂断言"；**改少了也红** ⇒ 名单不许静默腐烂；
    ③ 枚举下限 40 文件 + 共现基数下限 30 文件（正则一旦失效，站点数会**假降**成"零违规" ⇒ 由这两个下限拦死）。
    **有牙自证**（留痕 `tests/output/t51/teeth.log`）：造一个含合并 helper + `Exec $et` 取值的临时脚本 ⇒
    被点名（`_tmp-t51-teeth.ps1:11`）、站点数 **84→85** 判红；删掉夹具 ⇒ 回 **84** 判绿。
    ⚠ **同时纠正一条我自己早前的判断**：清单里 `verify-gainmap-isobmff.ps1:429-431` 那组**不是**假绿 ——
    它的两条都是正向形态自检（`-match 'unknown'` / `-match 'ColorSpace'`，读不到即红），只是被共现尺子计入债账。
    逐条分诊 84 处仍是**未做的后续项**（分"值只在 stderr 的合法合并"与"该改只读"两档）。

114. **合并取数债账 84 ⇒ 0 的逐条分诊，以及这把尺自己的两处盲区（2026-09-27，任务 #54）**
    分诊口径两档：**值确实只在 stderr**（ffmpeg 的 `psnr` 汇总行、`signalstats` 的 `YMIN=`/`SATAVG=`、`^frame=`、`Corrupted`、产品日志、`-ver` 摘要、`-icc_profile<=` 写入、`-b -w` 抽取到文件）⇒ 调用行留字面『合并取数已论证』并写明为什么；
    **值是工具写在 stdout 的** ⇒ 改 `ExecOut`/`ExecStdoutOnly`/`ExifVal`。跑完 **站点 0**（全仓与 `-ManagedOnly` 两个口径都是 0）⇒ 判据从「债不得增加」升级为**零未分诊站点**：新增一行裸合并取值即红。
    ⚠ **第一轮盲区**：`reValRead` 只认 `$et`/`$fp` 等变量名 ⇒ 补齐 `$ex`（exiftool 在 5 个脚本里叫这个）/`$ji`/`$jxlinfo` 后**当场新冒出 3 处**，其中 1 处是真喂断言的：
    `verify-color-strategy.ps1` 里 jxlinfo 的 `N-bit RGB` 读数直接进 F1「播报位深 == 实测位深」⇒ 已改 `ExecOut`；剩两处按档一登记（摘要打印 / 写操作）。
    ⚠ **第二轮盲区（这把尺自己造的假红）**：共现基数下限原本全局钉 30，而 `-ManagedOnly` 只看清单内 43 条门禁 ⇒ 实测共现 **21** 个文件 ⇒ 那一档**恒红**。下限现按口径分档（全局 30 / 受管 18），两个实测数写在注释里。
    **有牙自证**：剥掉 `verify-png3-interop` 一处标记 ⇒ 站点 1 条并点名 `:131`、判红；还原后与备份**逐字节一致**、回到 6/0。
    ⚠ **有意不纳入** `$pr`/`$sp`（ServiceProbe，.NET）：本尺打的是「perl 打包工具往 stderr 喷 locale warning 污染值」这一型；.NET 探针只在异常时写 stderr，且异常行已加 `FAIL ` 前缀 ⇒ 由 `verify-simd-switch-fallback` 的「pass>=25 + 拒绝 unknown command」下限锁住，不在这里造 25 条假债。

115. **两处「静默错读数」的尺子缺陷（`verify-decision-delivery` ⑩/⑪，2026-09-27）**
    ① **多行工具输出先 `-join` 再 `[int]`/`-match`**：`ffprobe -of csv -select_streams v -count_frames` 在多流产物上**逐流一行**（实测动图 AVIF = `1` 与 `10` 两行）⇒ 旧写法 `[int]((… -join '').Trim())` 读成 **1010**；换成 `-join ','` 再 `[int]`，zh-CN 下 `[int]"1,1"` = **11**（逗号是千分位）——**两种都不抛异常**，帧数判据就此静默读成千位数。
    同族两处：建表那行 `@{ n = [int]$c[2] }` 遇到不含逗号的行会拿到 `$null` ⇒ `[int]$null` = **0 帧的一行**（整表错位也不报错）；`-join ''` 用在**字符串**判据上会把 `yuva420p`+`yuv420p` 拼成一个仍 `-match 'yuva'` 的串 ⇒ 把「透明被丢了」读成「透明在」。
    修法：新增 `ProbeInts`（逐行 `^-?\d+$` 才转数，读不出返回 `$null` ⇒ 调用方判红），比较前取 `Maximum`；pix_fmt 改成**逐条流全查**并列出违规者。两条自证：合成「两行同值」输入必须解析成 2 条、最大 10（旧尺必得 1010）+ 换 `-of json` 当**独立尺**对最大值（实测该源 2 条流、两把尺都是 10）。
    ② **从共享产物目录按枚举顺序挑夹具**：⑩ 段用 `Get-ChildItem results -Recurse -Include *.avif | Where Length -gt 3000 | First 1` 挑「动图 AVIF」，而 `results/` 是**所有门禁共用的出口** —— 色彩矩阵 22:08 往里放了一张 4719 B 的**静帧** ⇒ 20:28 那一轮全绿的同一条判据，22:20 单跑变 **4 红**（帧数=1，且两条负断言被静帧走到的另一分支翻转）。
    修法：逐候选**量判据所需的那个属性**（帧数 ≥2）再选中，并把选中的素材名与帧数打进日志。⇒ 凡是"枚举第一个当夹具"的写法都要问一句：这条判据依赖它的哪个属性，那个属性量过吗？
    改后单跑 **84/0**（原 81/0：+读数器自检、+独立尺对照、+逐流表合法性）。

116. **判据词形要回工具实测，不能照抄记忆里的错误文案（`verify-prophoto-faithful`，2026-09-27）**
    两处 `-notmatch "Corrupted"` 的词是**当年凭印象写的**：实测 exiftool 面对坏 ICC 从不打 `Corrupted` —— 嵌在容器里是 **stdout** 的 `[ExifTool] Warning : Bad length ICC_Profile (length 4776)`，裸 `.icc` 是 **stderr** 的 `Error : Truncated ICC profile` ⇒ 那两条负断言**恒真**，一直在给「生成器把 mluc 写坏了」发绿。
    修法：词形换成实测集 `Bad length ICC_Profile|Truncated ICC profile|Error\s*:.*ICC`，并**补同址正向锁** —— 把本次生成的 ProPhoto ICC 截掉一半、嵌进 `src_prophoto.png` 的副本、用**同一把尺**读，必须命中 ⇒ 从此"读不到"才是真信号。夹具落在 `$out` **之外**（`tests/output/validate/prophoto-diag`），别污染按目录递归挑产物的段落。单跑 **27/0**。
    ⇒ 推论：**凡是拿「工具会不会说 X」当判据的，先造一个「它必然说 X」的夹具量一次**；量不到就说明这条判据没有牙。

117. **「有声明、无实现」的能力位：面板「SIMD 优化」此前零消费者（第 57 条，2026-09-27）**
    三条曾同时成立：① `AutoUseSimdBinaries` 由复选框写入并持久化；② zh/en 两条 `tip.simd` 都在**承诺**「关闭后逐像素回退 / falls back to scalar code」；③ 但 `grep AutoUseSimd src` 只命中声明/UI/持久化三处，**没有任何代码读它决定走不走 SIMD** ⇒ 用户关开关毫无效果、门禁对它也零断言。
    按「降级不是修复」把实现提到声明的水位（判据单点 `SimdKernelRouting`），而不是把 tooltip 改弱。默认（开）路径**逐位不变**的实证 = 探针 S2/S2b/S3（两态 SSE 相等 + 与第三份独立标量实现相等）+ S4/S6e 的错位负控（防"两边同一个错值"）。
    ⇒ 可复用的鉴别法：**界面或文档里每一个承诺，都要能指到一处读它的代码**；指不到就是死开关。

118. **取向标签 ⇒「coded 尺寸标注旋正像素」：一条缺陷同时坏掉产物与质量分析（第 58 条，2026-09-30）**
    用户素材 `DSC00118.TIF`（SONY ILCE-7M2，6000×4000、16-bit 无压缩 RGB、`Orientation=8`、无 ICC）转 JXL 出花图。
    实机读数（随包 ffmpeg git-2026-09-26）：`ffprobe stream=width,height` = **6000,4000**，而解码帧挂
    `3x3 displaymatrix rotation=90` ⇒ ffmpeg **autorotate**，帧实际是 **4000×6000**。
    引擎据此把 144,000,000 B 的 raw 按 `-video_size 6000x4000` 声明 ⇒ **每行错位重排**：
    产物 vs 真值 **PSNR 7.97 dB**；同一段 raw 改按 4000×6000 重排再 `transpose=1` vs 真值 **inf**（逐字节等）
    ⇒ 错的是几何标注，不是像素处理。**与目标格式无关**（PNG 出口同样 7.97 dB），legacy 直通 ffmpeg 正确（4000×6000）。
    两处守卫都是**盲**的：`rawLen < w*h*6` 与 `h = px/w` 反推在**转置下字节数恒等** ⇒ 已把前者改成精确等式。
    同一条根因的第二个症状在质量分析：`CheckResolutionMatchAsync` 也比 coded 尺寸 ⇒ ①对**正确**的产物报
    「源图 (6000x4000) 与输出图 (4000x6000) 分辨率不一致」而拒绝对拍；②对**错的**产物预检反而通过，
    直到 ffmpeg `ssim` 报 `Width and height of input videos must be same`，用户只拿到 stderr 尾巴。
    **不要拿 `-noautorotate` 当修法**：`ExifToolService.CopyRawCaptureFieldsAsync` 与 `QueueProcessor` 的
    `Orientation` 排除理由都是「像素**已**由解码器旋正」，关掉旋正会让元数据策略与像素两头同时对不上。
    另记一条"有时好有时坏"的成因：开「最长边限制」时上游中继 PNG 由 ffmpeg 写出、像素已旋正且**不带**
    orientation 标签（实测 coded 2000×3000、`rotation` 空）⇒ coded==display，缺陷自动消失；默认不开 ⇒ 必坏。
119. **宽高探测此前在 `src/` 里有四份实现 ⇒ 收成 `ImageGeometry` 一份 + 结构锁（第 59 条，2026-09-30）**
    修的时候发现"探宽高"各自写了四份，全部只读 coded：`RawColorPipeline.ProbeSizeAsync`、
    `GainMapDecoder.ProbeSizeAsync`、`QualityAnalysisService.GetResolutionAsync`、`QueueProcessor.ProbeImageSizeAsync`
    （第四份是**跑结构锁时才发现的**，不在最初的排查清单里）。⇒ 修一处漏三处，下次还会漏。
    现四处一律转发 `Services/ImageGeometry.cs`（coded 尺寸 + 帧级 `displaymatrix rotation`，±90 交换宽高），
    并由 `_probe-geometry-single-source-scan.ps1` 锁住：旧形态 `stream=width,height -of` 在 `src/` 代码中
    命中数必须为 **0**（剥掉整行注释再匹配）、`ImageGeometry` 必须仍同时含 `frame_side_data_list`/`displaymatrix`/`ApplyRotation`
    （否则"唯一实现被删"时前者会假绿）、四个消费者必须仍引用 `ImageGeometry.DisplaySizeAsync`。
    ⚠ 这把锁做过变异：往 `QualityAnalysisService` 里塞一行含旧形态的字符串 ⇒ **红态退出码 1**、还原后 **0**，
    且还原以 md5 逐字节自证。
    ⚠ 造夹具时的实测坑：`exiftool -Orientation=8` **不带 `-n`** 会写成 **3**（PrintConv 反查失败后落默认值，
    还只报一句 `Can't convert IFD0:Orientation (not in PrintConv)` 就"Nothing to do"）⇒ 180° **不改变轴向**，
    整条门禁会静默失牙。必须 `exiftool -n -Orientation=8`，并在门禁里断言 `rotation == 90`。

120. **一条产品裁定让测试端红了 10 天没人看见：根因是"套件不在闸内"，不是"没人写测试"（第 60 条，2026-09-30）**
    `GainMapTestHost` 稳定报 **80/4**（`sdr` 格：`MPImage2 存在` / `hdrgm GainMapMin/Max 存在` /
    `Channels 期望 1` / `解码成功`）。逐条追下去：产品侧 `GainMapEncoder.cs:188-197` 有 **2026-09-20 的裁定**
    —— `headroom ≤ 1`（增益区间为 0）时**不写增益图**，直接以底图 JPEG 交付并**点名**原因
    （写一张 `GainMapMin=GainMapMax=0` 的"自称 Ultra HDR、实际零增益"属「宣称≠交付」）。
    ⇒ 红的是**测试端的过期期望**，不是产物。归属做过单变量 A/B：把本次几何修复在 `GainMapDecoder` 里的
    那一处退回 HEAD 重建 ⇒ **同样 80/4** ⇒ 与取向标签那条缺陷无关。
    **真正的遗留是"这个宿主的 exe 不被任何门禁执行"**（运行器 `_run-step3-gates.ps1` 自己登记着这句话，
    且当年还用"非注释行里 `$x = … GainMapTestHost` = 0 处"当负断言）⇒ 套件红了没有任何闸会响。
    处置：接成清单第 60 条 + 补 STALE 第 ⑤ 对（该 exe ↔ 自己的源码），并把 `sdr` 格那 3 条过期期望
    换成 **6 条更严的契约断言**（降级必须点名 / 产物必须是完整 JPEG SOI+EOI / **不得**残留 MPImage2 与
    hdrgm（半写 MPF 比无增益图更坏）/ 底图尺寸不得变 / 解码器必须把它判为**非 UltraHDR**）⇒ **86/0**。
    ⚠ 换期望不等于放宽：判据从"有没有增益图"变成"降级是否诚实且不留假痕迹"，条数 3 → 6。
    同日再补**取向标签两条用例**（`E1` 九条 + `E2` 八条 ⇒ 宿主 **103/0**、门禁 **18/0**）：`E1` 钉 `GainMapDecoder` 走显示几何（coded 256×192 ⇒ 显示 192×256；线性产物 vs ffmpeg 独立参照 **46.22 dB**，而同一份参照按 coded 步长重排只有 **12.95 dB** ⇒ 用例自带反证臂）；`E2` 钉 `QualityAnalysisService.AnalyzeAsync`（无损臂 PSNR=∞ 且**不报**「分辨率不一致」、有损臂必须给**有限**值、另有一条 `E2g2` 反证：轴向**真**不一致的产物仍要被拒）。
    ⚠ 两处实测踩到的尺子缺陷：① 宿主在汇总**之后**把失败清单**重打一遍**（`Program.cs:71-73`）
      ⇒ 门禁若按全文数 `❌`，计数恒为 2× 失败数 ⇒ 必须只数汇总行之前那段；
    ② `sdr_decoded.rgba` 是 **09-19** 那批"SDR 也写假增益图"时期的**遗留产物**，而 `tests/output/gainmap/`
      是跨轮复用的固定目录 ⇒ 新加的**负**断言被这个旧文件当场判红（正向断言则会因它**假绿**）。
      修法是在解码前删掉同名旧产物（clean slate），不是删文件了事。
    ⚠ **两条新量到的工具事实**（已写进 `E2g1` 注释）：① ffmpeg 会把**帧级 displaymatrix / EXIF 取向透传进 PNG 产物**，且 `-map_metadata -1` **剥不掉** ⇒ 一个"只加了 `-noautorotate` 的错轴产物"在显示口径下**仍与源同轴**，所以造"轴向真不一致"的反证臂必须再用 `exiftool -all=` 剥标签，并**先自证其显示尺寸 == coded**；② 该宿主的 `D13`/`D16a-c` 四条**依赖 CWD**（从 `bin` 目录跑会红）⇒ 门禁一律先 `Set-Location $root` 再跑，这也是"裸跑宿主"与"门禁跑宿主"读数会不一致的原因。
121. **第二个取向缺陷：几何修好了，元数据恢复又把 `Orientation` 抄回产物 ⇒ 查看器二次旋转（2026-10-01）**
    第 118 条修完像素后，用**产品自己的恢复调用**实测：对 4000×6000 的无标签产物跑
    `exiftool -overwrite_original -m -TagsFromFile 源 -all:all -ICC_Profile 产物` ⇒ 产物读回 `Orientation : 8`
    ⇒ 像素对、标签错，合规查看器仍会再转一次。根因是**两条**恢复通路都不排除该标签：
    `ExifToolService.CopyMetadataAsync`（`-all:all`）与 `CopyMetadataSafeAsync`（`-EXIF:all`，
    它已排除 `StripOffsets/RowsPerStrip/PhotometricInterpretation…` 这一整族"几何结构"标签，
    **唯独漏了同族里唯一会让查看器重转的 `Orientation`**）。当年"只用白名单、绝不 `-all:all`"的
    纪律只写在 RAW 拍摄字段补回那条（`CopyRawCaptureFieldsAsync`），管不到这两条主路。
    ⚠ **排除式的形态是量出来的，不是推的**：照抄同文件 GPS/XMP 的组通配写成 `--Orientation:all`
    **实测不生效**（同一命令、同一夹具仍读回 8），裸 `--Orientation` 与 `--EXIF:Orientation` 才有效
    ⇒ 组通配对**整组**标签有效，对**单个可跨组重名**的标签无效。
    ⚠ **例外必须留**：JPEG→JXL 免解码重封装（`--lossless_jpeg=1`，像素原样搬 DCT 系数）的产物
    取向标签仍然成立 ⇒ 无条件剥掉会把这一类做坏。判据收在 `CjxlService.UsesLosslessJpegRewrap`，
    与 cjxl 命令行里那个 `--lossless_jpeg=1` 的判据**同一份实现**（结构锁：全仓该串只允许一处发射点）。
    载体：`ServiceProbe orientmeta`（8 条，含 M2 控制臂"Make/Model 必须仍到产物"——否则 M1 会被
    "整块复制根本没跑"假绿；M4 反证臂"传 `dropOrientation:false` 时标签必须留得住"）。
    ⚠ 变异验牙：把排除式退回空串 ⇒ `pass=6 fail=2`，红的正是 M1/M3 两条，M4 不受影响。
    ⚠ **`rc=0` 不是证据**：排除式不生效时 exiftool 照样返回 0。

122. **判据住在 gitignored 目录 = 没有判据；接入时的「两档被测 + 规范汇总」约定（2026-10-01，清单 60→65）**
    第 120 条讲的是"套件在树里但没人跑"；本轮是它的**加强版**：五套产物级/L3 判据（包级冒烟、JPEG↔JXL
    往返、取向×免解码重封装、GainMap 第三方、旋钮活性）整个住在 `tests/output/t{69,76,77,79}/`，
    而 `.gitignore:78` 的 `output/` 把整棵目录挡住 ⇒ ① 不进版本控制（换机器/重 clone 就没了），
    ② 一次 `git clean -xdf` 全没，③ 不在任何轮里被跑 —— 它们的读数**只存在于我手抄进结论的那几行**。
    这正是第 120 条那句"根因是套件不在闸内"的极端形态，只不过这次连文件本身都不在仓里。
    - **接入时的四点硬约定**（缺一都会造出新的假绿形状）：
      ① 路径一律 `(Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path` 运行时推导（原写死 `C:\PLAN\...`）；
      ② **被测两档**：默认 = 刚构建的 Release 产物（⇒ **每轮都跑**），`$env:PKG_VER` 有值时才打
         `publish/build/FFmpegPictureUI-v<ver>-x64-full`（L3）。两档**都缺 ⇒ `exit 2` 不产出任何读数**，
         **绝不回退 Debug**；包内 `ProductVersion` 与期望不符也 `exit 2` —— 写死版本号 = 下一版的读数
         打在上一版的包上（本轮 beta3 那批差点就这么犯，靠"按版本解析 + 不符即拒"堵住）；
      ③ **必须有 `PASS=n FAIL=m` 形状的汇总行**：运行器的**零断言闸**只认 `PASS\s*=\s*(\d+)`（取最大值），
         只有"合计=N"这类自定义措辞的套件会被记成"零断言 ⇒ 可能一条都没跑"（旋钮活性套件原本就是这一形状）；
      ④ **不判红的桶不许用 `FAIL` 前缀**：运行器把 `^ *FAIL|^FAIL` 当失败明细回显 ⇒ 绿的一步下面挂红字，
         读者只能按"红了"理解（`越界被拒`/`未判` 一律改 `WARN`）。
    - **条数同步是四件事不是两件**：`$expectedScriptCount` 常量 + 运行器里**三种散文形态**
      （`清单条数以实测为准 = N 条` / `受管基线：脚本清单 = **N** 条` / `全跑 N 条`，由第 ⑰ 针逐形态命中并
      与常量对账）+ `docs/TESTING.md` §3.5 与 `docs/HANDOVER.md` 的清单行。
      最便宜的自证 = `pwsh -File _run-step3-gates.ps1 -Scripts verify-ps-compat.ps1 -Probes selftest`
      —— **条数锁在子集解析之前执行**，所以一条子集就能同时验"形态 17 针 + 条数对账"，约 1 min。
    - ⚠ **副作用要当场结算**：这 5 条是"真跑转换"的套件 ⇒ 清单墙钟远超旧散文里的 ≈20min（已把分钟数
      从散文里撤掉，改以本轮日志的耗时为准）。单步超时默认值 `$ScriptTimeoutSec = 480` 由第 ⑫ 针逐字钉住，
      **不许悄悄改**：要放宽就按标定规则"实测 max × ≈3"重标默认值 + 同步改第 ⑫ 针，并把实测分布写进注释。
    - ⚠ **同批一处自我纠正**（写下来防再犯，见 §6 第 121 条的同类）：旋钮活性套件把 `--jpeg-dct` 那条臂
      标成"面板值域被编码器拒绝 = A-20 复现"，而 A-20 修复时 `float` **已从下拉移除**
      （`MainWindow.xaml:725-728` 只剩 auto/int/fastint）⇒ 现在只有 CLI/历史预设能送进来，
      实测 `lo rc=0 / hi rc=1` = 响亮拒绝、**合法出口**，不是缺陷复现。
      判据自带的**定性词**比数值更容易被读者当事实 ⇒ 引用前必须回读产品实况。
    - `tests/scripts/verify-release-package.ps1`（出包后的独立验收，原 `tests/output/t83/verify-package.ps1`）
      **有意不进清单**（与 `verify-oracle-matrix.ps1` 同类）：它的前置条件是磁盘上已有本次要发的包。
      同时**去掉了写死的默认版本** —— 没给 `PKG_VER` 直接 `exit 2`（实测输出那两行拒绝文本 + 退出码 2），
      任何默认版本都会在下一个发布日变成"拿 n 包的读数宣布 n+1 已验"。

---

## 七、UI 验证方法论（2026-08-16 沉淀）

### 7.1 静态完整性检查（编译前）

```powershell
# ① XAML 控件名 vs FindControl 引用对比（找缺失/死引用）
$xaml = Get-Content src/FfmpegGui/MainWindow.xaml -Raw
$cs = Get-Content src/FfmpegGui/MainWindow.xaml.cs -Raw
$xamlNames = [regex]::Matches($xaml, 'x:Name="([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
$findRefs  = [regex]::Matches($cs, 'FindControl<[^>]+>\("([^"]+)"\)') | ForEach-Object { $_.Groups[1].Value }
$findRefs | Where-Object { $_ -notin $xamlNames }  # 引用了不存在的控件

# ② XAML 事件处理器 vs code-behind 方法
$events = [regex]::Matches($xaml, '(?:Click|SelectionChanged|IsCheckedChanged|TextChanged|ValueChanged)="([A-Za-z_]\w*)"') | ForEach-Object { $_.Groups[1].Value }
$methods = [regex]::Matches($cs, '(?:private|public)\s+(?:async\s+)?(?:void|Task|bool|int|string)\s+([A-Za-z_]\w*)\s*\(') | ForEach-Object { $_.Groups[1].Value }
$events | Where-Object { $_ -notin $methods }

# ③ 本地化 key 完整性（XAML `{ext:Loc xxx}` vs zh-CN.json/en-US.json）
```

### 7.2 运行时验证（UIA 边界框 = 最精确）

```powershell
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$proc = Get-Process -Name FfmpegGui
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "OutputDirBox")))
$el.Current.BoundingRectangle  # L/R/T/B 物理像素（注意 150% DPI = 逻辑×1.5）
```

- 元素 `IsOffscreen` = 被裁剪/滚动出可视区；不在树中 = `IsVisible=false`
- 验证重叠：对比两个元素 `BoundingRectangle` 的 L/R 是否交叉
- `SelectionPattern` 可读 ComboBox 当前值；`TogglePattern` 可切换 CheckBox（不稳定时用鼠标模拟）

### 7.3 截图 + 视觉模型（交叉验证）

- **正确流程**：`SetWindowPos(HWND_TOPMOST)` 强制置顶 → `CopyFromScreen` 屏幕捕获
  （`SetForegroundWindow` 常被 Windows 前台锁定阻止，不可靠）
- **陷阱**：`PrintWindow` 对 Skia/GPU 渲染窗口可能捕获残缺（黑色区域）——视觉模型如实报告残缺画面会被误判为"模型误报"
- 视觉模型（SenseNova 6.8 Flash Lite）能力已验证：可读微小文字（窗口标题版本号）、可发现真实布局缺陷（如 39px 按钮重叠，UIA 实测证实）
- **原则**：视觉模型报告与 UIA 边界框必须交叉印证，不轻易否定任一来源

### 7.4 交互测试清单（发布前）

- [ ] 切换全部格式：编码器列表刷新、色度/位深选项按编码器过滤、ColorRange 按格式显示/隐藏
- [ ] 勾选高级色彩/高级编码：面板展开、控件可交互
- [ ] 切换主题（深/浅）、语言（中/英）：控件文字完整无豆腐块
- [ ] 调整窗口尺寸（含最小尺寸）：无元素溢出/重叠（重点：顶部路径行、底部并发栏）
- [ ] 简洁模式往返：预设加载、队列显示、自动编码开关
- [ ] 双击文件打开详情窗口：媒体信息/命令/日志显示
- [ ] 完整转换流程：添加文件→队列→开始→完成（含 16-bit 输入、HDR 输入、动图输入）
