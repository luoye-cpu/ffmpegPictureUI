# JPEG LI 渐进档拿不到 —— 成因分层与完整修复方案（2026-10-09，重查版）

## 0. 目标与完成判据（一句话）

**用户在任何一条正常入口（GUI / CLI / 预设 / 简洁模式）把 JPEG LI 的渐进档设成"渐进"，产物就必须是渐进；凡是本软件兑现不了的，必须响亮点名是哪一路、为什么。**

> 本文第一版把主因写成"GainMap 路吞掉 JPEG LI 面板"，那是**用错了实验条件**得到的定性（详见 §11）。重查后的排序是：**R0 后端推断缺 jpg 分支**（静默）＞ R1 `--animation-fps` 误判（响亮）＞ R2 固定码表降级（本批已修）＞ R3 GainMap 路。

完成判据（全部要在本机实跑取数，不接受"代码看起来对"）：

- J1 面板选「渐进」⇒ GainMap 底图产物 SOF=**0xC2**；选「基线」⇒ SOF≠0xC2。两条都由 `verify-gainmap-engine.ps1` 的 `SofMarker` 读数判定。
- J2 面板四旋钮各打一档极端值 ⇒ GainMap 路产物**必须变**（接入 `verify-encoder-knob-liveness.ps1` 的臂表，判据沿用它的产物字节差分）。
- J3 每条被忽略/被改写的用户选项，日志里必须有一句**点名来源**（"值来自面板 / 来自 `--jpeg-progressive` 显式覆盖 / 被固定码表约束降级"）。
- J4 `verify-gainmap-engine`、`verify-gainmap-thirdparty`、`verify-ui-host`、`verify-ui-split-union`、`_probe-cjk-hardcode-scan`、`_probe-i18n-scan` 六条在本批收口时全绿（`ui-icc` 的 I3 既有红除外，见 §11 账本）。
- J5（**2026-10-09 重查后新增，也是本卷最高优先项**）：`--format jpg` / `--format jpegli` 在未显式 `-e Cjpegli` 时，**要么真用上 cjpegli**（产物 SOF2），**要么响亮点名**（"此路是 ffmpeg mjpeg，无渐进能力，你设的 `--cjpegli-progressive` 未生效"）。实测现状是**零告警出基线**（§1 的 A1/A2/A9）。

## 1. 现状读数（本轮实测，日志在 `tests/output/jpegli-prog/`）

### 1.0 成因分层（2026-10-09 重查后的排序；上一版把 GainMap 当主因是**定性错误**，修正见 §11）

| 序号 | 成因 | 出声？ | 实机证据 |
|---|---|---|---|
| **R0** | **`InferDefaultBackend` 没有 jpg/jpeg/jpegli 分支** ⇒ 不显式 `-e Cjpegli` 的 jpg/jpegli 任务恒走 ffmpeg mjpeg（无渐进能力）；而 mjpeg 侧"无渐进"告警只检查 `JpegProgressiveId`，**全仓无人在 `backend==Ffmpeg` 时检查 `CjpegliProgressiveId`** ⇒ 用户设的 JPEG LI 渐进档无声消失 | **否** | `recheck.out` A1（`--format jpegli` 无 `-e`）与 A2（`--format jpg --cjpegli-progressive 2` 无 `-e`）→ 命令行是 `ffmpeg … -q:v 5`，产物 **Baseline**，MSG 里没有任何 progressive/fixed_code 点名；A3 加 `-e Cjpegli` → 同素材 **Progressive**。A9（webp 无 `-e`）同形 |
| **R1** | `--animation-fps` 使**静帧**被 `IsAnimated` 判成动图 ⇒ cjpegli 退出码≠0 后落 mjpeg 回退 | **是**（`动图/不支持格式（退出码 1），回退到 FFmpeg mjpeg 编码器`，状态 `已完成 (cjpegli→ffmpeg 回退)`） | `recheck2b.out` A7 vs A8：**同素材同选项，只差 `--animation-fps 10`** ⇒ A8 Progressive(cjpegli 管道)、A7 Baseline(mjpeg) |
| **R2** | 固定码表（勾掉「优化 Huffman 编码」）逼出 `-p 0` 的降级 | 原为**否**，**本批已修**（§10） | `warn3.out` 5 臂 |
| **R3** | GainMap/Ultra HDR 路：渐进由**采集侧恒 0** 的 `JpegProgressiveId` 决定；色度/固定码表/自适应量化在 `GainMapEncoder.cs:500` 被硬编码 | 部分（只提 `--jpeg-progressive=0`，不提面板值被忽略） | `e2e3`（②专用管线三档 md5 全等）、`e2e5b`（面板四旋钮全打满仍 `YUV420 … p0 OPT`） |

### 1.0b 两条要单独定性的发现

- **A5（未归因，本批不修）**：`--jpeg-gain-map true` **不带** `-e`（＝GUI 默认配置）在 SDR 输入上 **rc=1、零产物**，日志 `❌ 执行失败：InvalidOperationException: GainMap 线性解码失败 (exit -13)`。⇒ GUI 默认的增益图常态出口（引擎出口①）我**从未真正跑通过**；上一轮我带着 `-e Cjpegli` 测到的其实是**回退路径②**。这条要么是我夹具不适配（SDR/无峰值标注），要么是真缺陷，需单独一轮用 HDR 素材复现。
- **后置链假设已被否证**（我曾怀疑 ICC 后置/缩略图/元数据恢复把渐进图重编成基线）：审计结论是这些动作全部只做 exiftool marker 手术、或用 `-c copy` 流复制、或写独立临时件，**没有**"读回再编 JPEG"的实现 ⇒ 记录在 §11，避免下次再当候选。

### 1.1 GainMap 路读数（R3 的细节）

| 路 | 面板请求 | 工具自报 | 产物 | 播报 |
|---|---|---|---|---|
| 普通 cjpegli（`e2e/`） | 渐进 `-p 2` | `Encoding [… p2 OPT]` | SOF2 渐进 | — |
| 普通 cjpegli（`e2e3/`） | 基准 `-p 0` | `p0` | SOF0/SOF1 非渐进 | — |
| GainMap（`e2e3/lg_gm_panel_*.txt`） | 自动 / 基准 / 渐进 三档 | 恒 `Encoding [YUV420 d2.500 AQ p0 OPT]` | **三档 md5 全等 `BDC3766CB318`** | 只提 `--jpeg-progressive=0` |
| GainMap（`e2e5b/lg.txt`） | 色度 444 + 渐进 + 不优化 + 不AQ | 同上，逐字相同 | 同一 md5 | — |
| GainMap（`e2e2/lg_gm_li_prog2_jp1.txt`） | `--jpeg-progressive 1` | `p2` | **SOF2 渐进** ⇒ 非渐进不是容器的硬要求 | — |

代码事实（改动落点；行号以 2026-10-09 工作树为准）：

- **R0 的两处**：① `Services/EncoderDetectionService.cs:50-60` `InferDefaultBackend` 的 switch **只有 `jxl` / `jxr` / `dng` 三支**，`_ => EncoderBackend.Ffmpeg` ⇒ jpg/jpeg/**jpegli** 一律推断成 mjpeg（`CliParser.cs:189` 先把 `jpegli` 归一成 `jpg`，`:521-523` 再拿它推断）。而 `CliParser.cs:187-188` 的注释**明写**"jpg 目标在 cjpegli 可用时自动走 jpegli 编码（QueueProcessor 路由）"⇒ **声明与实现冲突**，按本仓「降级不是修复」的规矩，要把实现提到声明的水位，不许反过来改注释。② mjpeg"无渐进"告警（`FfmpegCommandBuilder.cs:158-160`、`ColorMapping/ImageEncoderArgs.cs:124-131`）的判据是 `JpegProgressiveId > 0`，**从不检查 `CjpegliProgressiveId`** ⇒ JPEG LI 那只旋钮在 mjpeg 路上消失得毫无痕迹。
  （对照：GUI 侧 `Services/EncoderDetectionService.cs:415-431` `GetDefaultEncoder("jpg")` 在 cjpegli 可用时**确实**默认选 `cjpegli`（`MainWindow.xaml.cs:2090-2093` 据此设 SelectedIndex）⇒ R0 主要是 **CLI/批处理/未显式 `-e` 的入口**中招；GUI 只在 cjpegli 探测不到时才落 mjpeg，且下拉里根本没有 JPEG LI 那一项、`RefreshEncoderListAsync` 全程零日志。）
- **R1**：`Services/QueueProcessor.cs:5859` `IsAnimated(item)` 的第一条判据是 `item.Options.AnimationFps.HasValue` ⇒ 静帧只要带 `--animation-fps` 就被判动图；cjpegli 直编失败后即走 `:2575-2590` 的 mjpeg 回退（`CloneOptionsForFfmpeg` 把 `CjpegliProgressiveId` 抹成 -1，`:5292`）。回退**有播报**，但措辞是"动图/不支持格式"，用户看不出"是我加的 `--animation-fps` 触发的"。
- **R3 的三处**：`Services/GainMapEncoder.cs:490-517` `EncodeJpegFileAsync` 自己拼 cjpegli 参数：`--distance {d} --chroma_subsampling 420{progArg}`，**不调** `CjpegliService.BuildCjpegliArguments`；无 `--fixed_code`、无 `--noadaptive_quantization`、无 `--num_threads`。底图（:174）与增益图（:201）共用同一条串，只差 distance。
- `Services/ColorMapping/GainMapJpegCodec.cs:174-175` `ProgressiveLevelOf(o) => o.JpegProgressiveId switch { 1 => 2, 0 => 0, _ => -1 }` —— 读的是 GUI 里 `IsVisible="False"` 的隐藏下拉（`MainWindow.xaml:763`）。**注意定性**：两条 GainMap 出口（引擎出口① `RawColorPipeline.cs:360` 与专用管线② `QueueProcessor.cs:2720`）**都如实消费**这个映射 ⇒ 问题不在"出口不读面板"，而在**采集侧把档位钉死成 0**：`MainWindow.xaml.cs:2980` `JpegProgressiveId = useAdvCodec ? ParseJpegProgressiveId() : 0`，勾选时取隐藏下拉默认 index 1 ⇒ 也是 0（`ParseJpegProgressiveId` :5867-5878）。
- `Models/FfmpegOptions.cs:440` `CjpegliProgressiveId` 的取值域**本来就是** cjpegli 的 `-p N`（-1/0/2），不需要换算；而 `Models/FfmpegOptions.cs:92` `ApplyPresetData` 对它**无条件覆写**（`PresetData.cs:109` 默认 -1 vs 模型默认 2）⇒ 任何预设回放都把面板默认档改成"不传 `-p`"。

- J6（R1 收口判据）：同一素材、同一选项，**只差 `--animation-fps 10`** 时，A7/A8 两臂要么都是 SOF2，要么 A7 必须点名"jpg 目标不装动画 ⇒ AnimationFps 被忽略/被拒"，**不许**以"动图/不支持格式"的措辞把原因推给格式。

## 1c. R0 / R1 —— 排在 GainMap 之前的两件事

### R0-a 后端推断补 jpg 支（**先量影响面，再决定走哪一档**）

```
// Services/EncoderDetectionService.cs:50-60
"jpg" or "jpeg" or "jpegli" => CjpegliService.IsAvailable ? EncoderBackend.Cjpegli : EncoderBackend.Ffmpeg,
```

- 只改"未显式指定 `-e`"那一支：`CliParser.cs:210` 的 `EncoderBackendExplicit` 与 `:528-531` 的分流已经存在 ⇒ 用户显式选的 `Ffmpeg` 必须原样保留（不许反向覆盖）。
- 判据：A1 / A2 / A9 三条臂复跑 ⇒ 命令行变成 `cjpegli.exe … -p 2`、产物 SOF2；再补一条 `-e Ffmpeg --format jpg` 臂，产物必须**仍是** Baseline（证明没把显式选择改掉）。
- ⚠ **成本与影响面（这一条会改变所有未显式指定后端的 jpg 任务的默认编码器）**：体积、色彩标注（ICC/JFIF）、EXIF/缩略图后置行为都会跟着变。实施前必须先出两个数字并贴进账本——① 受影响的调用面（未显式 `-e` 的 jpg 出口有多少处）；② 哪些门禁在观测 jpg 默认产物（至少 `verify-encoder-knob-liveness`、`verify-dryrun-parity`、`verify-metadata-privacy`、`verify-decision-delivery`、`verify-jxl-codestream-route`）。**量出来撑不住，就走 R0-b 那档并明说它不满足注释承诺。**
- 未证边界：`cjpegli` 直出的 JPEG **不带 JFIF/ICC**（`QueueProcessor.cs:1906-1918` 附近已有登记），把默认后端切过去会让"没做色彩后置的 jpg 任务"从"隐含 sRGB"变成"无标注" ⇒ 这是 R0-a 的真正风险点，必须先实测一张再定，不能凭推断。

### R0-b 最小且不撒谎的一档（若 R0-a 影响面撑不住）——**2026-10-09 已落地**

实测把 R0-a 挡在前面的数字（不是推测）：`InferDefaultBackend` 只有 **1 个调用点**（`CliParser.cs:530`），调用面很小；但同素材（`flat.png`，源本身无 ICC）对照下 —— 不带 `-e`（mjpeg）的产物带 **584 B ICC**（`Profile Description: RGB built-in`）+ **JFIF 1.01**，带 `-e Cjpegli` 的产物 **两者皆无**（exiftool 与 `jpegspec.py` 双读者一致）。
⚠ **条件要写清**（2026-10-09 复测纠正，原先我把它写成无条件）：cjpegli 路**不经过 `iccgen`/色彩后置**，所以"有没有标注"取决于源是否自带 ICC —— 换 `wide.png`（源带 584 B ICC）时**两条后端都保住了 ICC**（`mx_icc/{mj,li}/wide.jpg`）。⇒ 缺口是"**源无标注时，mjpeg 会拿到 iccgen 生成的 sRGB ICC，而 cjpegli 什么都没有**"，不是"cjpegli 一律无标注"。⇒ 直接把默认后端换成 cjpegli 会让**所有未显式指定的 jpg 任务丢掉色彩标注**，前置条件是 P7a-3（把 cjpegli 出口接进色彩后置）⇒ 本轮只做 R0-b。

已做四处：

1. `FfmpegCommandBuilder.cs` 的 jpg 分支新增 **③ 告警**：`options.CjpegliProgressiveId > 0 && options.EncoderBackend != EncoderBackend.Cjpegli` ⇒ ASCII 点名"`--cjpegli-progressive` 落在此路兑现不了，请用 `-e Cjpegli`"。**只出声不拦截**：扩 `ColorEngineRouter` 的未承接判据会改变路由语义（把整批 jpg 任务从引擎路踢回传统路），不属本轮。
   - 护栏是必需的：`BuildArguments` 也会为 backend=Cjpegli 的任务生成命令行（预览/`:165` 的 `-distance` 分支），不加护栏会把"真用上了 JPEG LI"的任务也报成兑现不了。
2. `CliParser.cs` 的过期承诺改正：原文"jpg 目标在 cjpegli 可用时自动走 jpegli 编码（QueueProcessor 路由）"**与实现不符**，已改为"归一只针对容器名，不改后端选择"，并写明 R0-a 的前置是 P7a-3。
3. `Models/FfmpegOptions.cs:440` 的 `CjpegliProgressiveId` 默认值 **2 → -1**（= 未表达）。这是 ③ 告警的必要配套：默认仍是 2 的话，每一个没碰过该选项的 CLI jpg 任务都会被判成"用户显式要渐进"而报警 ⇒ 恒响的告警等于没有告警。
   - **产物不变**，有实测：同素材三臂（省略 `-p` / `-p 2` / `--cjpegli-progressive -1`）md5 **全等** `9EE10590C96A079C`（`r0b.out` 的 A12 vs A3 比对，以及 `e2e/o_{default,prog2,progAuto}` 三臂）⇒ cjpegli 自身默认就是 `-p 2`，2026-08-16 定"默认 2"的意图（默认拿渐进、体积小 5-38%）继续成立。
   - 顺带消掉一处默认值不一致：`PresetData.cs:109` 一直是 -1 而模型是 2，且 `FfmpegOptions.cs:92` 无条件覆写。
4. 锚平移（本批插行造成的行号漂移，机械操作不改判据方向）：`_lib-matrix.ps1` 内 6 条 —— `CliParser.cs:189→196`、`:265→272`；`FfmpegCommandBuilder.cs:981→1000`、`:991→1010`、`:1023→1042`、`:2065→2084`。**另有 1 条与本批无关的既有漂移** `ColorStrategy.cs:75→76`（该文件工作树未修改 ⇒ 漂移在 HEAD 就存在），同批平移并在此点名，免得被误记成本批后果。

判据（`tests/output/jpegli-prog/r0b.out`，8 臂 + 1 项比对，FAIL=0）：

| 臂 | 期望 | 实测 |
|---|---|---|
| A2 `--format jpg --cjpegli-progressive 2`（无 `-e`） | ③ 告警响 | **响**，产物 Baseline |
| A1 `--format jpegli` / A11 `--format jpg` 裸 | 不响（没人设过） | **0 条** |
| A3 `-e Cjpegli --cjpegli-progressive 2` | 不响（真用上） | **0 条**，Progressive，命令行含 `-p 2` |
| A12 `-e Cjpegli` 裸（默认 -1 ⇒ 不发 `-p`） | 不响，且产物 ≡ A3 | **0 条**，Progressive，md5 与 A3 相同 |
| A13 `-e Cjpegli --cjpegli-progressive 2 --cjpegli-optimize false` | 固定码表告警响 | **响**，产物 Baseline |
| A14/A15 选基线（`0`，两后端各一） | 都不响 | **0 条** |

### R0-a 收口前必须先满足的前提（留给下一轮）

- P7a-3：cjpegli 出口接进色彩后置（ICC/JFIF 标注），否则切默认值就是拿色彩正确性换便利。
- 满足后再做：`InferDefaultBackend` 补 `"jpg" or "jpeg" or "jpegli" => CjpegliService.IsAvailable ? Cjpegli : Ffmpeg`，并保留一条"显式 `-e Ffmpeg` 仍是 Baseline"的对照臂。

### R1 `AnimationFps` 不该伪装成"动图"触发编码器回退

- 现状：`QueueProcessor.cs:5859` 用 `AnimationFps.HasValue` 当"这是动图"的**唯一**依据，jpg 目标 + 该旗标 ⇒ 一旦 cjpegli 直编失败（`IsAnimated` 只在退出码≠0 后才起作用）就落 mjpeg，回退文案还是"动图/不支持格式"。
- 改法：把"输出容器能不能装多帧"这条判据（`FfmpegCommandBuilder.cs:140-142` 已有 `targetHoldsMultipleFrames` 的同源写法）前移——jpg 目标带 `AnimationFps` 应在裁决/选项校验处点名或被忽略，而不是让它在执行期变成"格式不支持"。回退文案改成点名真实触发原因。
- 判据：J6。

## 2. S0 —— 前置实验（先做，它决定 S1 的方向）

要回答的问题：**渐进底图的 Ultra HDR 容器，读回器认不认？**（若认 ⇒ S1 直接兑现面板；若不认 ⇒ 只能"GainMap 恒基线 + 面板该项灰化 + 出声"，那是降级，需单独授权。）

做法（全部复用现成夹具，不新建工程）：

1. `tests/GainMapTestHost/Program.cs:45-49` 的 `EncodeTestAsync(outDir,"hdr1000",hdrPeak:1000f,…)` 已是真 HDR 输入（本轮 SDR 素材按 2026-09-20 裁定不写增益图，不能用它验容器）。给 `EncodeTestAsync` 增一档 `jpegProgressiveLevel`（:660 那处 `EncodeAsync` 调用传下去），产出 `hdr1000_prog.jpg`。
2. 对该产物跑同文件里已有的两道读回：`DecodeRoundTripAsync`（:55-57）与 `StructureCheckAsync`（:62，exiftool `MPImage2` / `hdrgm` 标签）。
3. 再加一个**与产出方无关**的第二读者：`djpegli`/`ffprobe` 解底图 + exiftool 读 `MPF:All`（判据见下）。

判据（任一不过就停在这里改方向，不许带着红往下做）：

- 底图 SOF=0xC2；`MPImageCount≥2` 且 `MPImage2` 偏移仍能指到增益图；`DecodeRoundTripAsync` 的 PSNR/尺寸读数与基线档同量级（不许"能解但读错"）；第三方结构检查项全部与 `hdr1000.jpg` 同集合。
- 命令：`dotnet build tests/GainMapTestHost -c Release` → 跑该 host → 读 `tests/output/gainmap/` 产物（归处按 `.gitignore` 已定的统一目录，不在仓库外建文件）。

## 3. S1 —— 渐进档的单一裁决点

规则（定死，不留两处口径）：

```
ResolveJpegliProgressive(FfmpegOptions o) =>
    o.JpegProgressiveIdExplicit ? MapLegacy(o.JpegProgressiveId)   // 保留 2026-09-19 对外契约
                                : o.CjpegliProgressiveId           // 面板值，域已是 -p N
MapLegacy(x) = x switch { 1 => 2, 0 => 0, _ => -1 }                // 原 ProgressiveLevelOf 逐字保留
```

- 新增 `FfmpegOptions.JpegProgressiveIdExplicit`（默认 false），**只由两处置位**：`CliParser.cs:889`（`--jpeg-progressive` 被用户显式给出时）与 `Models/FfmpegOptions.cs:40 ApplyPresetData`（预设携带该字段时）。GUI 采集侧不置位 ⇒ 默认态一律跟面板。
- `GainMapJpegCodec.ProgressiveLevelOf` 保留为 `MapLegacy` 的实现体（不动签名，避免牵动 6 条源码锁里的文本锁），新增 `ResolveJpegliProgressive` 作为 GainMap 侧唯一入口；`QueueProcessor.cs:2720` 与 `ColorMapping/RawColorPipeline.cs:360` 两处 `gmProgLv` 改调它。
- 行为变化：GUI 默认（面板=渐进、未显式给 `--jpeg-progressive`）从「非渐进」变「渐进」；`--jpeg-progressive 0/1` 的既有语义逐字不变。
- **必须同步重钉的一条行为锁**：`verify-gainmap-engine.ps1:496`「不传 ≡ 传 0」在新语义下不再成立 ⇒ 改为「不传 ≡ 面板值」，并新增一条「面板=基线 且 不传 ⇒ 与非渐进等价」。这是**改锚不是放宽**：新旧两条判据都得有牙，收口时要在无修复的构建上先跑一次要求它红（本仓 §"判据先在无修复构建上跑一次"规矩）。

## 4. S2 —— 其余三旋钮 + 线程

- 给 `CjpegliService.BuildCjpegliArguments` 增加 `double? distanceOverride = null`（GainMap 的底图/增益图各有自己的 butteraugli distance，不走 `opts.Quality` 换算），其余映射原样复用。
- `EncodeJpegFileAsync` 改为调用 `BuildCjpegliArguments(pngPath, jpgPath, opts, distanceOverride: distance, log: log)`，删掉手拼串 ⇒ 色度/`--fixed_code`/`--noadaptive_quantization`/线程一次性同源带回。本批刚加的固定码表冲突播报（见 §10）会自动在 GainMap 路生效，不需要第二份文案。
- **增益图那张的色度单独裁定，不跟面板**：底图跟面板（含 `auto`）；增益图仍恒 420，并把理由写成注释。理由要在 S0 同一次实验里量出来（420 vs 面板 444 两张增益图分别用现有 `DecodeRoundTripAsync` 读回，取"读回一致且更小"的一侧）——本轮未量，不许凭"业界惯例"写死。
- `--num_threads`：`CjpegliMultiThreadAvailable` 目前恒 false（已登记为 D-2，三处写者写死），本批**不动它**，只把 `opts.Threads` 透传给 `BuildCjpegliArguments` 既有的门；能生效与否由那条登记负责，避免在本批里悄悄改变并发形态。

## 5. S3 —— 出声与预览

- 日志：`[GainMap] JPEG progressive level -p N (来源: 面板 | --jpeg-progressive 显式覆盖 | 固定码表约束)` 一行，N 与来源都由 `ResolveJpegliProgressive` 的返回值给出，不在调用方复算。文案纯 ASCII（`_probe-cjk-hardcode-scan` 的 `CsUi` 面余量 0）。
- 预览：`MainWindow.xaml.cs:3400` 那条 `[纯托管] GainMapEncoder … prog -p {…}` 改为引用同一个 `ResolveJpegliProgressive`，并把来源一起打出（现在只显示数字、不显示它从哪来，正是这次"看不出面板值被忽略"的原因）。

## 6. 门禁与账

**不新建门禁脚本**（新建一条要同步 `_run-step3-gates.ps1` 的 `$scriptList`、`$expectedScriptCount`、三处散文计数，以及 `docs/TESTING.md` / `docs/HANDOVER.md` 的账，成本远大于收益）。判据落在已有两条里：

1. `verify-gainmap-engine.ps1`
   - 源码锁重钉（⑩ 段，逐条给出改后正则）：
     - `:539` 仍锁 `MapLegacy` 映射体不变：`=> o\.JpegProgressiveId switch \{ 1 => 2, 0 => 0, _ => -1 \};`
     - 新增：`ResolveJpegliProgressive` 是唯一 GainMap 入口 `'\?\s*ResolveJpegliProgressive\(FfmpegOptions'`；
     - `:546` 由 `ProgressiveLevelOf\(item\.Options\)` 改锁 `ResolveJpegliProgressive\(item\.Options\)`；`:547/:549` 的 `jpegProgressiveLevel: gmProgLv,` 两条**不动**（具名传参这条不变量仍然成立）。
     - `:545`（`EncodeAsync` 形参顺序锁）**不动**：`EncodeJpegFileAsync` 换实现不改 `EncodeAsync` 签名。
   - 行为锁新增 4 条（复用 `:440-453` 的 `SofMarker`，不引入 exiftool 新取数站点 ⇒ 不触 `_probe-stderr-merge-scan` 的债账）：面板渐进 ⇒ `SOF2`；面板基线 ⇒ `≠SOF2`；面板色度 444 vs 420 ⇒ 底图 bytes 不等；面板勾掉优化 ⇒ 日志含 `fixed_code requires`（把本批问题2 的播报也钉上）。
   - 判据口径：**SOF 三态**（0xC0 基线 / 0xC1 扩展顺序 / 0xC2 渐进）。本轮实测 `--chroma_subsampling 420` + `-p 0` 出 **0xC1** 而非 0xC0 ⇒ 任何"非渐进就是 0xC0"的写法都是错的尺子。
2. `verify-encoder-knob-liveness.ps1`：`$rows`（:135-160，字段 fmt/lab/pat/lo/hi/expect）加 4 条 `-e Cjpegli` 臂（chroma / progressive / optimize / adaptive-quant）+ 1 条同旋钮在 `--jpeg-gain-map true` 下的臂。六态里**不许**把 GainMap 臂写成 `gap`（那是"本批没做"的登记位，做了就要给判决）。
3. `verify-ui-host` / `verify-ui-split-union`：本批已把断言基数从 409 抬到 **413**（唯一出处 `verify-ui-split-union.ps1` 顶部 `$expectedOldCount`），后续再加 UI 断言只改那一处。

## 7. 三面行为矩阵（改动前 ⇒ 改动后）

| 面 | 场景 | 现在 | 收口后 |
|---|---|---|---|
| CLI | `--format jpg` / `jpegli`，**不带** `-e` | 恒 mjpeg ⇒ **Baseline，零告警**（A1/A2/A9） | R0-a 走 cjpegli ⇒ SOF2；或 R0-b 至少响亮点名 |
| CLI | 显式 `-e Ffmpeg --format jpg` | Baseline | **不变**（显式选择不得被反向覆盖，作为对照臂） |
| CLI | `-e Cjpegli` + `--animation-fps`（静帧） | mjpeg 回退，措辞"动图/不支持格式"（A7） | 前移点名（R1 / J6） |
| GUI | 编码器默认项（cjpegli 可用） | `GetDefaultEncoder` 已默认 `cjpegli` ⇒ 渐进 | 不变 |
| GUI | cjpegli 探测不到（`JxlLibDir` 缺、`FFMPEGGUI_PLAN_DIR` 未设） | 下拉里**没有** JPEG LI 项，静默改选 mjpeg，`RefreshEncoderListAsync` 零日志 | 必须点名"JPEG LI 未检测到 ⇒ 本路无渐进能力" |
| GUI | GainMap ON + 面板渐进 | 非渐进（采集侧恒 0） | 面板兑现 + 日志点名来源 |
| GUI | GainMap ON + 面板基准 | 非渐进 | 非渐进（来源=面板） |
| GUI | GainMap ON + 面板 444 | 恒 420 | 底图 444；增益图按 §4 实验结论 |
| CLI | `--jpeg-progressive 0/1`（任意 GainMap 态） | 生效 | **逐字不变**（explicit 优先） |
| CLI | `--cjpegli-progressive N` + GainMap | 被忽略 | 生效 |
| 预设 | 预设含 `CjpegliProgressiveId=2`，回放后开 GainMap | 被忽略 | 生效 |
| 预设 | 老预设不含 `JpegProgressiveId`（默认 0，无条件拷贝 `FfmpegOptions.cs:73`） | GainMap 恒非渐进 | explicit=false ⇒ 跟面板；**这条要单独加一条回放锁** |

## 8. 分批与每批验收命令

- B1（S0 实验 + S1 裁决点 + GainMap 底图渐进）：`dotnet build src/FfmpegGui/FfmpegGui.csproj -c Release` → GainMapTestHost 跑 S0 → `pwsh -NoProfile -File tests/scripts/verify-gainmap-engine.ps1` → `pwsh -NoProfile -File tests/scripts/verify-gainmap-thirdparty.ps1`。
- B2（S2 四旋钮同源 + 距离覆盖）：同 B1 + `pwsh -NoProfile -File tests/scripts/verify-encoder-knob-liveness.ps1`。
- B3（S3 来源播报 + 预览）：`pwsh -NoProfile -File tests/scripts/verify-dryrun-parity.ps1` + `pwsh -NoProfile -File tests/scripts/verify-ui-host.ps1`。
- 每批只做一件事、只跑所在链路，收口前用同一条 `SofMarker` 读数证明"修复前该判据是红的"。

## 9. 风险与回退

- R1 S1 改变 GainMap 默认产物字节 ⇒ 文档里钉过的历史 md5（如 `GainMapJpegCodec.cs:170` 的 `59e1644…`/`c8a07d4…`）会过期。**处置**：那两处是变异实验的散文读数，不是判据；收口时在账本里标"已被 2026-10-09 S1 取代"，不改判据方向。
- R2 渐进底图可能被某个客户解码器拒收（Android 老版本对 MPF + 渐进的组合兼容性未证）⇒ 由 S0 把关，不通过就走 §2 末的降级路线并**先请示**。
- R3 `verify-gainmap-engine.ps1` ⑩ 段共 6 把源码锁 + 4 把行为锁，改动过程会出现"锁红但实现是对的"⇒ 逐条按 §6 的新正则同步，不许先把锁拆掉再补实现。

## 10. 本批已落地（问题2：冲突提示）

- `Services/CjpegliService.cs`：固定码表逼出的 `-p 0` 降级，现随 `BuildCjpegliArguments(…, log:)` 播报（新增 `log` 形参，约定与 `CjxlService` 的 `forPreview`/`log` 相同：只有真执行侧传，预览不传 ⇒ 一个任务一条）。
- 三个真执行入口把 log 接上：`CjpegliService.RunWithOptionsAsync`、`QueueProcessor.PipeFfmpegToCjpegliAsync`、`JxlPipelineService.TryPipeDjxlToCjpegliAsync`。
- `MainWindow.xaml` + `MainWindow.xaml.cs`：渐进下拉下方新增 `JpegliProgConflictHint`（橙色灰底小字，双语键 `jpeg.progressive.fixedcode.conflict`），由 `UpdateJpegliProgressiveConflictHint()` 按「高级面板已开 ∧ 优化被取消 ∧ 档≠基线」显隐，刷新点 = `RegenerateCommand` 首行（早退之前）/ `UseAdvancedCodec_IsCheckedChanged` / `UpdateCodecPanelVisibility` 末尾。
- 断言：`tests/UiTestHost/Program.cs` GroupC 新增 C6/C6b/C6c/C6d（三条对照臂 + 一条冲突臂），并按生成器同步到 `tests/ui-core/uicoreGroup.cs`（两份逐字 `cmp` 通过）。
- 实机读数：`tests/output/jpegli-prog/warn3.out` —— 5 臂 0 红；冲突臂产物仍 Baseline 且日志含播报，对照臂无播报。

## 11. 账本（依据 / 纠正 / 未证边界）

- **纠正（定性层面，本轮重查后）**：第一版把主因写成"启用 GainMap 时 JPEG LI 面板被吞"。两处错误：
  ① **实验条件不代表默认路径**——我所有 GainMap 臂都带 `-e Cjpegli`，而 `-e Cjpegli` 恰好命中 `ColorEngineRouter.cs:224-226`（外部后端 ⇒ `ShouldRoute=false`）⇒ 测的是**回退路径②（专用管线）**，GUI 默认（不带 `-e`）走的是**引擎出口①**。⇒ 措辞必须改为"采集侧 `JpegProgressiveId` 恒 0"+"出口 `GainMapEncoder.cs:500` 硬编码色度/固定码表/AQ"，这是**两件事**，不是"出口不读面板"一件事。
  ② **漏了能解释"全都非渐进"的那条**——`InferDefaultBackend` 缺 jpg 分支（R0）。我第一轮实跑时其实已经打出过 A1/A2 同形的读数（`-e Cjpegli` 加与不加的差异），但没有把它当成候选去追，直接跳到 GainMap。
- **撤回（我自己的假设）**：曾怀疑"ICC 后置 / 缩略图 / 元数据恢复把渐进图重编成基线"。审计否证：这些动作只做 exiftool marker 手术、`-c copy` 流复制（`RestoreMetadataViaFfmpegAsync`，`QueueProcessor.cs:2107-2125`）或写独立临时件，**没有**读回再编 JPEG 的实现。该候选从此不再列入。
- **未归因（⚠ 已于 2026-10-10 撤销，见 §17）**：A5 —— `--jpeg-gain-map true` **不带** `-e`（GUI 默认）+ SDR 输入 ⇒ `rc=1`、零产物、`GainMap 线性解码失败 (exit -13)`。可能原因是夹具（SDR／无峰值标注），也可能是真缺陷；在把它定性之前，**任何关于"GUI 默认 GainMap 行为"的结论都不成立**（包括本文 §1.1 的 GainMap 表格，它全部取自 `-e Cjpegli` 的②路径）。
- **既有红（⚠ 已于 2026-10-10 撤销，见 §14）**：`ui-icc` 模式下 `I3 ProPhoto保真` FAIL=1。当时做的 A/B 归因——改动前的旧宿主二进制（408/1）与改动后的新宿主（412/1）都是同一条、同一详情（`named_prophoto-rgb.v2.icc` / `faithful=False`），本批未触碰 ICC 路径 ⇒ 判为既有红，不在本批修，需单独开一条（`docs/` 与 CHANGELOG 里查不到它的登记）。
- **本轮踩到并已回退**：`tests/scripts/_gen-ui-split.ps1` 整批重跑会把 `tests/ui-sweep/uisweepGroup.cs` 里 2026-10-07 的 J1z 修复（82 行）连同 4 个 UiHost 包装方法一起**删掉** ⇒ 说明旧宿主 `Program.cs` 与拆出的 ui-sweep 组体已不一致；`verify-ui-split-verbatim.ps1` 当场 20/0 没抓到这件事（判据盲区）。已 `git checkout` 恢复，本批只手工同步 ui-core。**这是独立的一条债**：生成器有损 + verbatim 有盲区，需在 J1z 那条 lane 里收口，本批不动。
- **纠正**：本轮最初把降级播报写成 `Console`+relay 双通道，实测一个任务在 stdout 刷 3 条（预览/命令串那几遍调用也各发一次）⇒ 改为本文件 §10 的 `log:` 单通道，改后 stdout 条数 == 日志条数（`warn3.out`）。
- **未证边界**：① `-p 0` 何时出 SOF0、何时出 SOF1 的确切触发条件（两例实测：444+`-p 0`=SOF0、420+`-p 0`=SOF1、PPM 管道路 `-p 0`=SOF1，样本太少不下结论）；② 增益图那张用 444 是否安全（§4 要求 S0 量出来后才定）；③ GUI 提示行的**视觉**落点未验（只验了 `IsVisible` 逻辑，headless 宿主拿不到布局像素）；④ 老预设无条件拷贝 `JpegProgressiveId`（§7 最后一行）的行为差异未实跑复现。

## 12. English summary

Root cause, re-ranked after the second investigation: the dominant, **silent** one is R0 -- `EncoderDetectionService.InferDefaultBackend` has no `jpg`/`jpeg`/`jpegli` branch, so any job that does not pass `-e Cjpegli` is encoded by ffmpeg's mjpeg (no progressive capability), while the "mjpeg has no progressive" warning only ever checks `JpegProgressiveId`, never `CjpegliProgressiveId`. Verified on this machine: `--format jpegli` and `--format jpg --cjpegli-progressive 2` (no `-e`) both produce **Baseline** artifacts with zero notices; adding `-e Cjpegli` yields Progressive for the same input. R1 (`--animation-fps` makes `IsAnimated` treat a still frame as animation, falling back to mjpeg) is real but **loud**. R2 (fixed Huffman tables forcing `-p 0`) is already shipped in this batch. R3 (Gain Map) is two separate facts, not one: the GUI collector pins `JpegProgressiveId` to 0, and `GainMapEncoder.cs:500` hard-codes chroma/fixed-table/adaptive-quant -- both exits consume the mapping faithfully, so the earlier wording "the Gain Map path ignores the panel" was wrong.
First fix to take: R0-a (add the jpg branch) after measuring blast radius; R0-b (at minimum, announce the ignored knob and correct the CLI comment that promises auto-routing) if the radius is too large.
Blocked/open: Gain Map with default backend on an SDR input returns `rc=1, "GainMap 线性解码失败 (exit -13)"`, no artifact -- until that is attributed, every Gain Map conclusion in this document is from the fallback exit only.
Withdrawn hypothesis: post-encode passes (ICC embed, thumbnail, metadata restore) do NOT re-encode pixels -- exiftool marker surgery, `-c copy`, or separate temp files.
Done when: J1-J6, with J5 (route or announce) as the top-priority criterion.

## 13. 产物级矩阵复测（2026-10-09 收口后；R0-b + P1-a 已在被测件内）

方法：18 个设置组合走产品 CLI（`-e Cjpegli`，640×480），产物**直接解析**（`jpegspec.py` 自写解析器 + exiftool 双读者对账）；另 16 臂直跑 `cjpegli.exe` 只换输入内容。先锚后读：PIL 参考件必须给出 `SOF0 scans=1` / `SOF2 scans=10`、采样 `2x2,1x1,1x1` —— 对齐后才读本次产物（解析器中途有四处错都被这一步抓到：SOS 计数的熵数据跳过、分量偏移差 3 字节、APP2 的 `ICC_PROFILE` 比较切片长度、以及 `o_p2.jpg` 消失导致的空读）。

| 组合（面板/CLI 值） | 命令行 | 产物 | 判定 |
|---|---|---|---|
| 默认（不发 `-p`）／`2`／`-1` | `--distance 2.5 [-p 2]` | **SOF2 scans=13** | ✅ 三者一致，渐进兑现 |
| `0` + 各色度 444/420/422/440 | `-p 0 [chroma]` | SOF0 scans=1，采样 `1x1` / `2x2` / `2x1` / `1x2` | ✅ 色度旋钮确实落到产物 |
| `2` + 取消 Huffman 优化 | `-p 0 --fixed_code` | SOF0 | ⚠ 降级，但**已出声**（本批 §10） |
| `2` + 自适应量化关 | `-p 2 --noadaptive_quantization` | SOF2 | ✅ 不动扫描结构 |
| `2` + Gain Map | 面板值不下发 | **SOF1 scans=1** | ❌ 面板渐进档在这条路惰性（R3） |
| `2` + webp 输入（管道路） | `-p 2` | SOF2 | ✅ 管道与直编同源 |
| `2` 但不带 `-e` | `ffmpeg … -q:v 5` | SOF0 | ✅ ③ 告警已响（R0-b） |
| **`0`（选"基线"）+ 高熵素材** | `-p 0` | **SOF1** | ❌ 见下 |

### 新缺陷：「基线/标准 (兼容性)」这一档名不副实

同一 `-p 0`：低熵素材出 SOF0、高熵素材出 **SOF1（extended sequential）**。16 臂直跑进一步证明它**只与像素内容有关** —— png 与 ppm 结果逐字节同大小（容器无关），444 与 420 同样出 SOF1（子采样无关）。源码依据（本地 checkout `tools/src/jpegli`）：

- 裁决点 `lib/jpegli/bitstream.cc:137-139`：`progressive_mode ? 0xC2 : is_baseline ? 0xC0 : 0xC1`；
- `is_baseline` 的否决项之一是**实际写出的 Huffman slot id 超出 {0,1,0x10,0x11}**（`bitstream.cc:174-179`），而 slot 由优化器聚类决定 ⇒ 数据相关；对照 libjpeg-turbo 只看分量声明的 `tbl_no`（`third_party/libjpeg-turbo/jcmarker.c:522-524`），所以 turbo 的 4:2:0 非渐进恒出 SOF0；
- 兼容性风险有上游原话：`lib/jpegli/entropy_coding.cc:588-591`「并非所有解码器支持 0xff 0xc1」，故 jpegli 自己把顺序模式的聚类钳到 2 个 slot —— 但仍会溢出。

⇒ 正确措辞：「顺序（sequential，非渐进）；产物多为基线 SOF0，高熵内容下可能是扩展顺序 SOF1」。现在 `Resources/Locales/zh-CN.json` 的 `jpeg.progressive.baseline` 写的是"基线/标准 (兼容性)"，把一个**不保证**的属性写成了保证。

### 扫描数与公式对账

实测 `-p 1`=7、`-p 2`=13，与 `encode.cc:113-158` 的 `Σ ceil(分量数 / 每扫描分量数)` 一致。旁注：调查线按该公式预测 13 时，曾判我最初读到的 12 "与 checkout 不符" —— 错在我的坏解析器，不在公式；修好后读数即为 13。

### 本轮被这条矩阵推翻的两条旧说法

1. §1.1 与 §1c 里我写过"带 `-e Cjpegli` 的产物零色彩标注" —— 无条件形式**不成立**：源自带 ICC 时（`p1aw/wide.png`，584 B）两条后端都保住了 ICC（`mx_icc/{mj,li}`）。真实缺口是"**源无标注时，mjpeg 会拿到 `iccgen` 生成的 sRGB ICC，而 cjpegli 什么都没有**"。§1c 已就地改写。
2. 上一版把 SOF1 归因给"4:2:0 + `-p 0`" —— 矩阵否证：444 与 420 在高熵下都出 SOF1，低熵下都出 SOF0。

### 未证边界

- SOF1 在真实解码器上的失败率未测（本机无设备/无参考解码器矩阵）；
- GainMap 底图取 SOF1 是否被 Android 侧拒收仍未测（A5 已于 §17 判为**环境假红并撤销**，与本条无关；
  本条缺的是"真实设备/参考解码器矩阵"，本机没有）；
- 4:2:0 × 高熵 × `-p 0` 之外未再细分象限（本轮矩阵已覆盖 444/420/422/440 × 两种熵）。

## 14. 整轮红的环境归因（2026-10-10，含可复现判据与处方）

**结论一句话**：本轮所有 `exit -13` / `UnauthorizedAccessException` 的红都来自**本会话沙箱禁止工具进程写
`%TEMP%`**，与产品无关；把 `TMP/TEMP` 指到工作区内目录后同一条探针当场由红转绿。

两侧读数（同一台机、同一份二进制，命令可直接复跑）：

| 臂 | 命令 | 结果 |
|---|---|---|
| 负（默认 `%TEMP%`） | `publish/PLAN/ffmpeg-full/ffmpeg.exe … -frames:v 1 %TEMP%/qff_probe.png` | `Permission denied`，进程退出非 0 |
| 正（工作区内） | 同一条命令，输出改 `tests/output/jpegli-prog/tmpctl/qff_probe.png` | **exit 0**，产物 174 B |
| 刷臂（决定性的那一条） | `TMP=TEMP=tests/output/jpegli-prog/faketemp ServiceProbe.exe plan` | **pass=39 fail=0**（默认环境下 `plan` 是 `pass=24 fail=1`，失败详情正是 `Temp\cms_*.png … denied`）|

⇒ 性质是**按进程**的限制而非按路径：`py` 能写 `%TEMP%`、`ffmpeg.exe` 不能；被拦的都是往工作区外落盘的
子进程。因此"红"集中在**引擎/像素路**（`iccname`/`plan`/`engine`/`geometry`/`thumbnail`/`oog`/
`verify-color-wiring`），而纯静态与 argv 断言类（`verify-ps-compat`/`verify-color-caps`/`_probe-*` 扫描）不受影响。

**处方（下一轮出包前的固定动作）**：整轮用 `TMP=TEMP=<工作区内目录>` 起跑；日志头要写明这是重定向臂，
并保留一份默认环境臂的红清单做归因（本轮那份在 `tests/output/gates-round-beta7.log`）。

**重定向臂的完整读数**（`tests/output/gates-round-beta7-redirect.log`，2026-10-10 00:10 起跑，
绑定 `artifact-sha16 FfmpegGui.dll=263FF7977EA29D9C`，运行器 `05AE64C1617CCBC5 lines=1359`，73 条脚本**逐条 exit=0**）：

- 受管脚本 **73/73 `exit=0`**；26 个探针 mode 里只剩 **`runner` 一条红**（`pass=7 fail=1`，
  详情是「静默但正常退出的子进程不被误杀」＋`target is not ffmpeg (cmd.exe)`），与 CHANGELOG v1.6.0-beta6
  定性的**同一条沙箱限制**，不是 TEMP 引起的。
- `iccname` 由默认臂的 `SKIP×7 + fail=1` ⇒ **`pass=10 fail=0`**；`verify-color-wiring` 由 `56/50` ⇒ **`142/0`**；
  `engine`/`geometry`/`thumbnail`/`oog`/`plan` 同样整批转绿。
- **撤销 §11 的「既有红 I3 ProPhoto 保真」**：那条记的是「旧宿主 408/1、新宿主 412/1，同一条同一详情」。
  本轮 `verify-ui-host` 打的是 **413 PASS / 0 FAIL** —— 412+1 与 413 **总数守恒**且失败数归零
  ⇒ 它是同一个 `%TEMP%` 限制，不是产品缺陷，也不是"本批未触碰 ICC 路径所以留着"的那条债。

**顺带定性的两条**：
- §11 的 **A5**（`--jpeg-gain-map true` 不带 `-e` + SDR 输入 ⇒ `GainMap 线性解码失败 (exit -13)`）
  的 `-13` 与此同源 ⇒ **已由 §17 判结**：重定向臂上用出货 full 包复跑 rc=0、产物齐备 ⇒ 环境假红，撤销。
- `probe:runner` 那条红（"静默但正常退出的子进程不被误杀"，详情写着 `target is not ffmpeg (cmd.exe)`）
  **不是** TEMP 引起的，是沙箱对进程树的限制，属既有非产品红（与 CHANGELOG beta6 的定性一致）。

## 15. 欠的一条门禁断言（本轮有意未做）

SOF 三态目前只有**散文与一次性实验**背书，门禁清单里没有一条会因"渐进式请求退化成 SOF0/SOF1"而红。
最省的做法不是新开第 74 条，而是把断言加进既有的 `verify-encoder-knob-liveness.ps1`（它已经在解析
`cjpegli` 真命令行），补一条"打产物字节"的判据：
①`-p 2` + 优化哈夫曼 ⇒ 产物必须 SOF2；②`-p 0` ⇒ 产物必须 `SOF0 或 SOF1`（**绝不能 SOF2**）；
③面板未表达（`CjpegliProgressiveId=-1`）且未关优化 ⇒ 产物 SOF2（cjpegli 自己的默认是 `-p 2`）。
本轮没做的原因是：**改门禁脚本会让正在跑的整轮读数作废**（运行器逐条读 `tests/scripts`），
而收口需要的是一条绑到出货 DLL 指纹的完整绿轮。⇒ 记为下一批的第一件事，不随包出。

## 16. 收口时发现的措辞欠账（改它会作废整轮绑定，故登记不随包）

③ 那条告警现在写的是 `Pass -e Cjpegli to encode with JPEG LI and keep progressive output.`。
- 事实层面**成立**：GUI 默认采集把面板第 3 档（渐进）映射成 `CjpegliProgressiveId=2`
  （`MainWindow.xaml.cs:1494/2996/5979`），而 R0 让未显式选后端的 jpg 任务落 mjpeg ⇒ 这句话只在
  「开了高级编码选项 + 没换后端」的用户身上响，**不是恒响**（面板关闭时采集给 `-1` ⇒ 条件 `> 0` 不成立）。
- 但 `-e Cjpegli` 是 CLI 措辞，GUI 用户的可行动作是「把编码器后端选成 JPEG LI」。
  ⇒ 下一批把它写成两条路都点名（改文案属纯字符串、不增行 ⇒ `_lib-matrix.ps1` 的行锚不受影响，
  但它会改 DLL 指纹，必须与下一条门禁断言同批重建、同批绑轮）。

## 17. v1.6.0-beta7 出货读数（2026-10-10，日志 `tests/output/jpegli-prog/{pack,verify}-beta7*.log`）

- **pack**：`-Mode all`，`PACK_EXIT=0`；app 归档 11 164 615 B / full 归档 55 955 831 B（580 文件，
  完整性闸 580 齐备）。两档包内 `FfmpegGui.exe` 同为 `sha16=EFF9A3EB5D687396`（22 921 728 B），
  `ProductVersion=1.6.0-beta7+ba146517…`。
- **口径更正（我自己写过的一条）**：app 档是 NativeAOT ⇒ 归档内**没有** `FfmpegGui.dll`
  （成员只有 `FfmpegGui.exe` + 散装 `Resources/Locales/*.json` 等），所以"包内程序集与整轮绑定的
  `263FF7977EA29D9C` 逐字节对账"**不适用** ⇒ 出货件的覆盖只能来自 L3（打的就是包内 exe）。
  代之以一条真能做的对账：从归档解出 `Resources/Locales/{zh-CN,en-US}.json`，实测两语都含本批新 key
  `tip.jpeg.progressive.mode`，且 `jpeg.progressive.baseline` 就是「顺序 (sequential)」/「Sequential (non-progressive)」
  ⇒ **文案确实进件**（XAML 文本走 `.xbf`，不在这里判）。
- **独立验包 `verify-release-package.ps1`**：`PASS=16 FAIL=0`（五组判据：归档在位/必需成员/
  目录与归档同数 17↔17 与 580↔580/六件工具与 `publish/PLAN` 逐字节同指纹/包内版本串）。
- **五条 L3 打出货包**：包级冒烟 **17/0**、JPEG↔JXL **100/0**、取向×免解码重封装 **28/0**、
  GainMap 第三方 **13/0**、旋钮活性 **19/0**（另有"越界被拒 1 / 夹具无判别力未判 1 / 登记缺口 1"，
  两条都是 avif 侧的既有桶，与本批无关）。六套首行都打印 `SUBJ(被测) = v1.6.0-beta7 (L3 出货包)`
  ⇒ 不是拿 Release 档冒充包。合计 **193 条断言 0 红**。
- **A5 撤销（本批最后一条未归因红）**：旧读数 `--jpeg-gain-map true` 不带 `-e` + SDR 输入 ⇒ `rc=1`、
  零产物、`GainMap 线性解码失败 (exit -13)`。在 §14 的重定向臂上用**出货 full 包**复跑三臂：
  `noE_sdr` **rc=0、9370 B**；`withE` **rc=0、9370 B**，且与 `noE_sdr` **md5 逐字节相同**（`e6d985b74ee52cb0`）
  ⇒ `-13` 就是同一条沙箱限制，**A5 不是产品缺陷**，登记撤销。
  `--log-level debug` 的 item 日志同时给出机制证据：`[GainMap] JPEG progressive level -p 0 (--jpeg-progressive=0)`
  ⇒ R3 用的是隐藏字段 `JpegProgressiveId`（不是面板那个），且**有播报不静默**；SDR 源走
  `headroom 1.00` ⇒ 增益图退化为恒等、产物只有一张子图（exiftool 读回 `MPImage2/NumberOfImages` 均缺席）。
- ⚠ 一条**读数坑**顺带记牢：headless 的 `--log-file` 只写**汇总两行**，成功项的 item.Log 不回显
  （`logfile.txt` 实测 89 B）⇒ 想拿"有没有播报"当判据必须带 `--log-level debug`，否则会把
  "没打印"误读成"没播报"（这条 headless 行为的旧登记只说了"失败才回显"，没说清 log-file 与 stdout 的区别）。

### 17b §0 五条完成判据的逐条对账（**没有全兑现**，别把上面任何一句读成"J1–J4 已过"）

- **J1 半兑现**：非增益图路成立（§13 实测 `-p 2` ⇒ SOF2、`-p 1`/`-p 0` 各落其位）；
  **增益图路不成立** —— 面板值在采集侧被钉成 `JpegProgressiveId=0`，§17 的 debug 读数
  `[GainMap] JPEG progressive level -p 0 (--jpeg-progressive=0)` 直接印证，产物永远到不了 `0xC2`。
- **J2 未兑现**（增益图路）：三种面板设置下底图 md5 逐字节相同（§1.1/§13）；"四旋钮各打极端值"的逐档臂本批**没跑**。
- **J3 兑现**：本批新增的两条出声（cjpegli 固定码表降级、非 JPEG LI 后端忽略 `--cjpegli-progressive`），
  加上 GainMap 出口那句点名 progressive 值来源的 `[GainMap]` 行。
- **J4 兑现**：六条在重定向臂全绿 —— `verify-ui-host` **413/0**、`verify-ui-split-union` **new=413 old=413**、
  `verify-gainmap-thirdparty` **13/0**、`_probe-cjk-hardcode-scan`/`_probe-i18n-scan`/`verify-gainmap-engine` 均 exit=0；
  且 §11 那条「I3 既有红」已按 §14 撤销。
- **J5 半兑现**："响亮点名"做到了（③ 告警 + GUI 就地提示），"要么真用上 cjpegli"**没做**（R0-a 未动）。
- 未随包出的三项：§15 的 SOF 三态门禁断言、§16 的 GUI 措辞、R0-a。
