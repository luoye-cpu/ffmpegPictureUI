# 管线审查与测试覆盖审计（2026-10-01）

范围：HEAD `cbcf11a`（v1.6.0 正式版）之上的**整批未提交工作树改动**（34 改 + 5 新 + 5 未跟踪脚本），
以及本审计轮新增的判据与被判据抓出的缺陷。被测口径：**L0 无 / L1 命令行或日志文本 / L2 随包工具读产物 /
L2+ 独立读者（exiftool·jxlinfo·第三方库·哈希对撞）/ L3 出货包**。

**两轮整轮读数（都记下来，别只留最后一轮）**

| 轮次 | 时间 | 被测 `FfmpegGui.dll` | 结果 | 日志 |
|---|---|---|---|---|
| 第一轮（beta2 树） | 08:53–09:24 | `8FF98B12BE0B71CD` | 83 步全通过 | `tests/output/t78/round-R2.log` |
| 发布轮（beta3 树） | 20:20–20:48 | `41B44697A8289D44` | 83 步全通过、零非零退出 | `tests/output/t83/round-beta3.log` |

两轮的差异只有 `<Version>` 一处源码改动（`find src … -newermt` 实测 0 个产品源文件），
包内工具链与 `publish/PLAN` **逐字节相同**（cjxl/ffmpeg/exiftool/JxrEncApp/avifenc/dngtool 六件全等）
⇒ 本审计里所有"工具自报参数面"的取证对两个版本同样成立。

---

## 一、判词：这批提交可用性

**判词：可以提交、可以推送，但不能据此宣布"管线已被实机验证"。**

理由分三层，每层都给可核对的读数：

1. **功能层（成立）**：1.6.0-beta2 的全部改动是测试与文档，除一处刻意的 `csproj`（`DebugType=none`，随包不带 pdb）；
   自 09-29 起唯一的产品代码变化就是取向标签（Orientation）修复，落点只有 4 个文件。
   该修复在**出货包**上验：`t76/orientation-rewrap.ps1` 14/0，四格格式（jpg/avif/webp/heic）× 判据
   "码流尺寸被旋正 ⇒ Orientation 必须丢弃或归一"。
2. **证据层（不足）**：本轮整轮实际执行的 **60 条门禁里没有一条打的是出货包**（L3=0）；
   24 条 L2+、17 条 L2、**19 条只有 L1**（只看命令行/日志措辞/源码结构，名单见 §三）。
   清单取自 `round-R2.log` 的 `[name.ps1]` 去重（=60，与运行器散文声明一致），分类脚本 `tests/output/t79/evidence-levels.ps1`。
3. **harness 层（本轮抓出的假绿机制见 §二：2 条在现行门禁里、3 条在我新写的判据里，都已修）**：
   **这些会让"历史上任何一次全绿"的口径需要重述** —— 不是"改错了"，是"绿得比实际弱"。

---

## 二、本轮抓出的假绿机制（2.1/2.2 是门禁体系的，2.3 是我新建判据自己的；都已修）

### 2.1 35 个门禁的"构建类型标注"恒假 ⇒ 被测可能是三天前的 Debug 产物，日志却写 `(Release)`

- 机制：exe 解析是 `Release 缺 ⇒ 静默换 Debug`，而标注行用 `-like '*\Debug\*'` 比一条**正斜杠拼出来的路径**
  （`$root/src/FfmpegGui/bin/Debug/...`）⇒ 永不命中。
- 实测（同一台机、同一命令、前后两次）：
  - 修前：`verify-gainmap-managed.ps1` 报 `PASS=22 FAIL=0`，日志首行
    `exe=C:\PLAN\ffmpegPictureUI/src/FfmpegGui/bin/Debug/net11.0/win-x64/FfmpegGui.exe (Release)`
    —— 被测的 Debug 产物 mtime `09-30 22:11`，**早于本轮所有产品改动**。
  - 修后（`-like '*[/\\]Debug[/\\]*'`，同时认两种分隔符；PS 7.6.6 与 5.1.26100 两个宿主实测语义：
    旧式对 Debug 路径 = False、新式 = True、对 Release 路径 = False）：
    补齐 `bin/Release` 产物后同一条门禁 `PASS=22 FAIL=0`，标注 `(Release)` 且路径确为 Release ⇒ **结论未变，但这次是真的**。
- 影响面：`grep -rlF '*\Debug\*' tests/scripts/*.ps1` = **35 个文件、40 处**（含运行器自己）。
  逐字节批改（保 BOM/CRLF，含读回断言），`verify-ps-compat` 13/0，7 个被改脚本语法树解析错 = 0。

### 2.2 `bin/` 被整树打回时，60 条门禁全部静默降级，而 git 完全看不见

- 机制：`.gitignore:8` = `bin/` ⇒ 构建产物不在版本控制视野内；`git status` 干净与"产物存在"**是两件事**。
  本轮实测到 `src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe` 与 `publish/.../FfmpegGui.dll` **两度消失**
  （一次在一轮门禁跑到 49/60 时，一次在启动包内冒烟时），且 `dotnet build` 报"成功"却不再产出 apphost。
- **10-01 11:0x 把"消失"这一半定位到了**：beta3 重建时四个工程全报
  `MSB3027/MSB3021：无法把 obj\...\apphost.exe 复制到 bin\...\FfmpegGui.exe … 文件被 "FfmpegGui (17672)" 锁定`
  （重试 10 次后放弃）。占着的是**一个还开着的 GUI 实例**（pid 17672，Session 1，窗口标题
  `FFmpeg 图片转换器 1.6.0`，路径正是 `bin/Release/net11.0/win-x64/FfmpegGui.exe`）。
  ⇒ 观测事实：**目标被锁时这次构建不产出 apphost，而 DLL 已在位 ⇒ 半残状态**（`git status` 依旧干净，
  因为 `bin/` 被忽略）。⇒ 跑整轮/发包前先确认没有活着的 `FfmpegGui.exe`；见到 MSB3027 就是这件事，
  **不要当成"构建偶发失败"重跑一遍了事**（重跑只会再次把产物打成半残）。
- 修法：运行器 `[STALE]` 闸原先只判"旧"，现补判"**不存在**"（`FfmpegGui.dll/exe`、`ServiceProbe.exe`、
  `UiTestHost.exe`、`GainMapTestHost.exe` 五项，任一缺失即拒绝跑，不产出读数）；
  并把**被测产物指纹钉进日志头**（取代"人工记 DLL 前 16 位"的旧约定）：
  `artifact-sha16: FfmpegGui.dll=…  FfmpegGui.exe=…  ServiceProbe.exe=…  UiTestHost.exe=…  GainMapTestHost.exe=…`
  实测本轮头一行读数：`FfmpegGui.dll=8FF98B12BE0B71CD`（与出货包内那份同指纹，重建后确定性构建仍为同值）。

> ⚠ 另记两条**本轮新增判据自己踩的坑**（与 M-1 同族：harness 才是最大错误源）。
>
> **① exiftool 的 ICC 标签名必须 CamelCase。** 我为了把"ICC 是否随往返保留"这一维做进判据，
> 先写 `-icc_profile:profile_description`（**该标签在本 exiftool 里不存在**，恒读空），
> 再改 `-icc_profile:color_space_data`（下划线式，**同样恒读空且不报错**）——
> 于是连续两轮得出"ICC 写不进去/随往返丢了"，并一度怀疑**出货包里那份 exiftool 是坏的**。
> 实测否证：`publish/PLAN/exiftool` 与包内那份 **sha256 相同**（`68C079C32FDAE0D6`，同一文件）；
> 用 `-icc_profile:ColorSpaceData`（CamelCase）读同一个文件即命中 `[ICC-header] Color Space Data : RGB`，
> 且产物大小 630 B → 3742 B 证明**写入一直是成功的**。
> ⇒ 缺席结论前必须先用同一条命令对**已知存在**的样本量到过一次；否则红的是读法。
>
> **② 两趟式路线不能只取末条命令。** 判"参数有没有发到编码器"时我按 `[cmd]` 取**末条**，
> 而 AVIF 这类"先解码再编码"的通路会有第二条不含该旋钮的命令 ⇒ `-row-mt 1` 明明在第一条里却被读成
> "<未下发>"，把**活旋钮误判成死旋钮**。现改为该臂全部 `[cmd]` 行合并后搜索。
> 另一个同族坑：产品日志里有一句散文含 "[cmd] 行" 字样（`已追加 -progress pipe:1（不改动 [cmd] 行）`），
> 按 `[cmd]` 裸匹配会把散文当命令行 ⇒ 全部旋钮读成"<未下发>"。现用 ` -i ` 作真命令锚。

### 2.3 我自己新建的两条判据里，有三处"静默没跑"（都已改成响亮）

- `t76/jbrd-e2e.ps1` 的 `icc_exif` 夹具**连续两轮静默没产出**（代码写的是 `if (Test-Path $p) {…} else { 什么都不做 }`）
  ⇒ C 段那条"无损 × 强制色彩变换"的 icc_exif 臂**一次都没跑过**，而日志里零条红。
  现改为：生成失败即 `CK` 记红并附 ffmpeg 退出码与原话，另加一条**夹具集合完整性**断言
  （应 9 种形态，缺任何一臂即红）⇒ 终轮该格正常产出并 5 项判据全绿（含 ICC+Orientation 原样保留）。
- "两侧标签集均空"这种格子我加了 fail-closed 守卫，**当场抓到自己 7 格没有判别力**（见 §3.4）。
- 判"参数有没有发到编码器"的锚点原先只认 `[cmd]`（ffmpeg 前缀）⇒ cjxl/avifenc/JxrEncApp 通路一律"读不到"，
  会被当成"没发"。现认多后端前缀并单列 `nocmd` 状态。

> 这三条的共同点：**harness 少跑了一格不会自己报警**。所以本轮起，凡是"集合/计数/两端"型判据，
> 都要把"应有 N 格"写成断言，而不是信任循环跑到了就是跑全了。

---

## 三、测试覆盖面分析（按用户点名的三条线）

### 3.1 GainMap

| 判据 | 现状级别 | 问题 |
|---|---|---|
| `verify-gainmap-managed.ps1:66` 逐项数值 | **L1** | 正则取的是**产品自己的日志行**，只证明"产品想写什么"，不证明"文件里写了什么" |
| 容器结构（两子图/MPF 偏移） | L2+ | `ServiceProbe gainmap` 探针自述 **pass=0 fail=0**，但门禁只判 `exit=0` ⇒ **绿洞**（缺"非零"下界） |
| 第三方认证 | **无** | 仓里有 `tools/src/libultrahdr/build/Release/ultrahdr_app.exe`（09-01 构建，实测可用），但现行门禁不引用它 |

本轮新增（`tests/output/t77/gainmap-thirdparty.ps1`，被测=**出货包**）：用 libultrahdr 自己的 `-m 1 -P -j` 独立解析
ISO 21496-1，并与产品日志逐项对账 + exiftool 独立读 MPF + **反真空臂**（SDR 源 ⇒ 第三方必须说"不含增益图"）。

实测读数：

```
Ultra HDR Image: Yes
--maxContentBoost 4.92458 --minContentBoost 1 --gamma 1 --offsetSdr 0.015625
--hdrCapacityMin 1 --hdrCapacityMax 4.92458 --useBaseColorSpace 1
产品日志：参数: HDR峰值 1000nits, SDR白点 203nits, headroom 4.93 (2.30 log2), 底图=sRGB (SDR)
exiftool：[MPF0] NumberOfImages : 2  [MPImage1] …
反真空臂：SDR 源 + 同一开关 ⇒ "does not contain gainmap image" ⇒ A 组不是恒真
```

⇒ **文件里的值 = 声明的值**（4.92458 vs 2^2.30），第三方与 exiftool 两个独立读者都认，且 SDR 反例被抓住。
这条应接进 `verify-gainmap-managed.ps1`（不进清单计数，作专项臂），因为它把 L1 换成了 L2+/L3。

### 3.2 JPEG ↔ JPEG XL 无损互转换（产物级，被测=出货包）

`t76/jbrd-e2e.ps1`：**9 种输入形态 × 5 判据 + 反真空 + 3 类冲突 + 后端翻转 + 手工独立参照**。
本轮把"元数据标签集不变"这一格从**没有牙**做成有牙（见 §3.4），并补了夹具集合完整性断言。

- 终轮读数：**`PASS=89 FAIL=2`**（`tests/output/t78/jbrd-e2eA.log`，被测=出货包 `sha=516D3E74427AA497`）。
  演进过程本身就是审计内容：`55/9`（判据无牙 + 7 格恒空）→ `69/2`（EXIF 打标后）→ `79/3`（补"假端不发=默认"这类误判前）
  → `55/11`（把 ICC 拉进判据、但读法用了不存在的下划线标签名 ⇒ 全红）→ `89/9`（读法改 CamelCase 后，
  暴露出 8 格取向判据是我写错的期望）→ **`89/2`**。剩下的 2 条是**真缺陷**（§五 U1），不是判据坏。
- 逐字节相同的往返：**8 格**（`baseline420 / chroma444 / chroma422 / grayscale / large2400 / progressive / photoicc / icc_exif`）
  全部 `JPEG→JXL→JPEG` **逐字节相同**（不是"像素相同"这种弱判据）。
  其中 `icc_exif` 那格源文件带 **ICC(sRGB) + Make/Model + Orientation=8**，往返后四组标签**一并原样保留**
  （`src=[[ICC-header] ColorSpaceData: RGB | [IFD0] Make… | Orientation…]` 两侧同集）⇒
  "免解码重封装不动元数据"这一条终于有了正向证据；反向证据是同一素材关掉 jbrd 后 ICC/取向按上面那条规则变化。
- 反真空臂：同一素材关掉 jbrd ⇒ 逐比特/逐字节两条必须不成立（成立则说明 A 组恒真）。
- 手工独立参照臂 F：不经被测链路、用随包 `cjxl --lossless_jpeg=1` 直接造的正向产物，也必须逐字节回去。
- 关键坑（已修进门禁）：判"像素有没有被旋正"**不能用整图哈希**（有损重编码本身就让哈希不同）；
  且 JXL 的 `ffprobe` 报的是**显示尺寸**（它应用了容器自己的 Orientation），码流尺寸只能读 `jxlinfo`。

### 3.3 参数冲突与参数传递

`t76/jbrd-e2e.ps1` 的 C/D/E/F 段 + `verify-jxl-codestream-route.ps1 ⑪`（四臂差分）+ `verify-color-wiring (12)`：

- C：无损重封装 × 强制色彩变换 ⇒ 命令**不得**含 `--lossless_jpeg=1`，且产物**必须**无重构盒（两向都要判，
  单向判会被"命令发了但没生效"骗过）。
- D：`--lossless true × --jxl-lossless-jpeg false` ⇒ 显式关掉时不得仍发 `=1`，取舍必须被点名（不许静默改写）。
- E：后端翻到 ffmpeg-libjxl ⇒ 不发 `=1`、产物无盒、**质量档必须真的改变产物**（q30=59251 B vs q90=243357 B）、
  做不到 jbrd 要由日志点名。
- F：手工 cjxl 产物作独立参照（同码流不同来源）。

### 3.4 本轮把自己的 harness 判红的一次（值得记下来）

`t76/jbrd-e2e.ps1` 首轮跑完 **9 条红**，归因如下（三种性质不同，不能混着报）：

1. **7 条是我的判据没有牙**：夹具只在专门做元数据的那一格打了标签，其余格两侧标签恒空 ⇒
   `src=[] || rt=[]` 恒成立，是假绿；我加了"两侧都空 ⇒ 记未判（红）"的守卫后**当场被自己抓住**。
   修法：所有夹具统一打元数据（exiftool 写 ICC + Make + Model），并把判据**移到有关节的那一格**——
   A 段走 jbrd、逐字节全等已经蕴含元数据不变；真正有牙的是 **B 段（关 jbrd ⇒ 真重编码）**。
   改后终轮 **`PASS=89 FAIL=2`**：EXIF 维（Make/Model）在 A（jbrd）与 B（真重编码）两段共 8 格全绿且非空；
   ICC 维拆成独立一格 —— 实测**重编码路上产物确实不再带 ICC**，但产品日志点名了降级
   （`[色彩后置] ⚠️ 本通路的命令行不由 FfmpegCommandBuilder 产出 ⇒ 裁决点未参与（不猜标签、不补 ICC）`
   与 `[色彩引擎] ⚠️ 降级（UnlabeledAssumedSrgb）：产物无色彩标注`），按本仓既有裁定
   "**sRGB 目标的无标注是合法出口，只有静默无标注才可打红**" ⇒ 这一格记绿并附播报原文；
   若哪天播报没了，同一格会立刻转红。
2. **2 条是真缺陷**（U1）。
3. **1 处是我自己写错的判据**（`t77` 首版按 `verify-gainmap-managed` 的旧措辞取正则，
   而出货包里根本没有"元数据: min=…"这行 ⇒ A3 恒红）。**改判据前必须先读被检对象**，这次是先改了措辞才对上。

---

## 四、逐格式"高级编码器设置"可用性与缺失矩阵

判据口径：**不用 help 文本做集合差**（实测否证：`-h encoder=png` 只列 `dpi/dpm/pred`，但
`-compression_level` **确实被接受并生效**；`libjxl` 的 AVOptions 里没有 `-lossless`，实测同样接受
⇒ 以 help 全集做差会造出一批假缺失）。改用两把尺：
① **参数面可达性**（实测：随包工具对每个候选值的接受/拒绝）；② **旋钮活性**（同一通路两个极端值 ⇒ 产物字节必须变，
`tests/output/t79/knob-liveness.ps1`，被测=出货包）。

### 4.1 编码器存在性（决定性事实，实测 `-h encoder=`）

| 候选表里写的名字 | 随包 ffmpeg 是否认识 | 备注 |
|---|---|---|
| `mjpeg` | ✅ | 21 个 AVOptions（含 `-huffman`；**无** `-progressive`、**无** `-dct`） |
| `mjpeg_qsv` | ✅ | **0 个 AVOptions** ⇒ JPEG 硬件后端上"质量/预设/ICQ"类参数**无处安放** |
| `mjpeg_vaapi` / `mjpeg_nvenc` / `mjpeg_amf` | ❌ `Codec '…' is not recognized` | 三个名字在本构建里**根本不存在** |
| `librav1e` / `av1_vaapi` | ❌ 同上 | AVIF 候选表里的两个死名字 |
| `libaom-av1` | ✅ 58 个 | 含 `aom-params`（透传口） |
| `libsvtav1` | ✅ **只有 5 个**：`crf dolbyvision preset qp svtav1-params` | ⇒ SVT 的细粒度旋钮**只能**走 `svtav1-params` 字符串；`--avif-tune` 在 SVT 上是**死参数** |
| `av1_nvenc` / `av1_amf` | ✅ 45 / 47 个 | |
| `libjxl` / `libjxl_anim` | ✅ | `distance effort modular xyb`（+ 通用 `-lossless`） |
| `tiff` | ✅ 2 个 | `compression_algo dpi` |
| `png` / `apng` | ✅ 3 个 | `dpi dpm pred`；`-compression_level` 实测接受 |
| `libwebp` / `libwebp_anim` | ✅ 4 个 | `quality preset lossless cr_size cr_threshold`；`-compression_level` 实测接受 |
| `gif` | ✅ 3 个 | `gifflags global_palette`（+`gifimage`）⇒ **没有** "关抖动"这一档的独立旋钮 |
| `libjxr` | ✅ | 无质量 AVOption、字节序与 jxrlib 不一致（既有结论） |

`EncoderDetectionService.cs:190-194, 220-227` 的候选表含 **5 个本构建不存在的名字**
（`mjpeg_vaapi/mjpeg_nvenc/mjpeg_amf/librav1e/av1_vaapi`）⇒ 面板把它们列成可选硬件路线、
执行时静默回退，属"可用性诚实"缺陷（同族：A-3/A-25）。

### 4.2 外部工具的真实参数面 vs 应用下发面

| 工具 | 工具自报参数面（实测） | 应用实际下发 | **可达但没接（缺失）** |
|---|---|---|---|
| `cjxl`（JXL 默认后端） | `-h -v -v -v` 共 40+：`distance quality effort lossless_jpeg allow_jpeg_reconstruction modular xyb alpha_distance epf gaborish noise resampling upsampling_mode ec_resampling override_bitdepth brotli_effort codestream_level num_threads progressive progressive_ac progressive_dc qprogressive_ac intensity_target photon_noise_iso premultiply keep_invisible dots patches center_x/y group_order frame_indexing compress_boxes container allow_expert_options streaming_input/output num_reps` | `-e --num_threads -d --lossless_jpeg --progressive --photon_noise_iso -x icc_pathname -x color_space --intensity_target` | **`--modular`、`--alpha_distance`、`--epf`、`--gaborish`、`--noise`、`--resampling`、`--upsampling_mode`、`--ec_resampling`、`--override_bitdepth`、`--brotli_effort`、`--codestream_level`、`--premultiply`、`--keep_invisible`、`--progressive_ac/dc`、`--qprogressive_ac`**（其中多数属 expert 类，需同时带 `--allow_expert_options` ⇒ 这是"面板零控件"的可补清单） |
| `cjpegli`（JPEG 默认后端） | `quality distance chroma_subsampling progressive_level target_size adaptive_q fixed_code optimize psnr verbose` | `-quality -distance -chroma_subsampling -progressive_level -psnr -adaptive_q/-noadaptive_quantization -fixed_code -jpeg_encoder -optimize` | **`--target_size`**（按目标字节数反压质量，是唯一"能直接交付体积上限"的旋钮） |
| `JxrEncApp` | 实测 18 个：`-q -c -d -l -p -f -b -a -U -V -H -t -v -i -o …` | 只有 `-i -o -q`（+ 出口内部用的 `-c` 解码码） | **`-d`（色度子采样）、`-l`（重叠块）、`-p`（顺序/渐进）、`-f`（频域序）、`-b`（黑白）、`-a`（alpha 平面/交织）、`-U/-V/-H`（分块）** ⇒ 印证 A-17"面板零控件"，且这条通路补起来**不需要新后端** |
| `avifenc`（AVIF 主路径） | `--qalpha --quality --speed --jobs --tilecolslog2 --tilerowslog2 --autotiling --sharpyuv --cicp --icc --xmp --exif --min/maxalpha --input-format --progressive --layered …` | `-q -s -j`（动图路径见 `QueueProcessor.cs:3510-3519`） | **`--sharpyuv`、`--autotiling/--tilecolslog2/--tilerowslog2`、`--qalpha`、`--min/maxalpha`、`--progressive/--layered`** |
| `dngtool`（DNG） | `-d -info -engines -i -O -o -q -W -H -4 -6 -T -v -e -lossless -jxl -effort -decode_speed -linear` | 已接 `-e/-d/-T/-o/-q/-W/-H/-6/-jxl/-lossless/-effort` | `-4`（**8-bit DNG**）、`-info`（独立读元数据）、`-engines`（能力自检） ⇒ 与 A-21/A-22 同源 |
| ffmpeg `mjpeg` | `huffman lmin lmax noise_reduction chroma_elim_threshold …`（21 个） | `-qscale/-huffman/-dct`（`-dct float` **实测被拒**：`Undefined constant or missing '(' in 'float'`） | `-lmin/-lmax`、`-noise_reduction`、`-chroma_elim_threshold` 等 **16 个未接**；`--jpeg-progressive`/`--lossless` 无宿主（已有显式播报，合规） |

### 4.3 死控件与"恒不成立"的门（逐条给可复现锚点）

| # | 位置 | 状态 | 锚点（grep 可复现） |
|---|---|---|---|
| D-1 | `MainWindow.xaml.cs` `HwPreset*` | **面板上不存在该控件**：代码后置 19 处引用、`MainWindow.xaml` **0 命中** | `grep -c 'HwPreset'` 两个文件对照 |
| D-2 | `CjpegliMultiThreadAvailable` | **恒 false** ⇒ `CjpegliService.cs:264` 的线程门永不成立；三处写者全写死 false（`MainWindow.xaml.cs:1452/2902`、`QueueProcessor.cs:4864`），模型默认也 false（`FfmpegOptions.cs:368`） | `grep -rn 'CjpegliMultiThreadAvailable'` |
| D-3 | GainMap 质量"跟随主图" | 语义是**硬编码 75**（`CliParser.cs:867` 注释自述 + `QueueProcessor.cs` 的 `-1⇒75` 哨兵）；用户改主图质量，增益图质量不跟随 | `grep -n 'JpegGainMapQuality' CliParser.cs` |
| D-4 | TIFF"强制无损" | 引擎路线恒发 `-compression_algo raw` ⇒ 用户的 `--tiff-compression` 被覆盖（不是不可用，是**被上层否决**） | CHANGELOG 09-30 条目 + `verify-gif-avif-framelist`/`verify-tiff-icc` |
| D-5 | `--jxl-modular` | ffmpeg 路线**接受**（实测 `-modular` 在 `libjxl` AVOptions 里），cjxl 路线 **0 引用** ⇒ 同一旋钮两条路一条活一条死（U3） | `grep -c modular Services/CjxlService.cs` = 0 |
| D-6 | GIF"关闭抖动" | ffmpeg `gif` 编码器**没有**该旋钮（只有 `gifflags/global_palette/gifimage`）⇒ 面板承诺无宿主 | `-h encoder=gif` 实测 |

### 4.4 旋钮活性实测（18 个旋钮 × 两个极端值，被测=出货包）

`t79/knob-liveness.ps1`：**四态判词**（活 / 死=两端都没把参数发到编码器 / 被拒=面板给了编码器不认的值 /
未判=参数确已下发但该夹具判不出，或该通路命令行读不到），
每条臂同时读该产品自己记录的命令行（走 `--log-file`，不用 shell 重定向），
这样"字节相同"不会一律算成死旋钮。

终轮读数（`tests/output/t78/knob-live6.log`，被测=出货包）：
**`活=15 死(两端都没发)=1 被拒=1 未判(夹具无判别力)=1 未判(读不到命令行)=0 合计=18`**
⇒ 全表只有 `--jxl-modular` 一只真死旋钮、`--jpeg-dct float` 一只被拒、`--avif-row-mt` 一只判不出。

首版（09:2x）的两处自纠，读数都留在 `tests/output/t78/knob-live.log` 里可查：

- **判据条数假象**：累加器叫 `$R`、循环变量叫 `$r` ⇒ PS 变量名大小写不敏感，`$r`（哈希表）把累加器打掉，
  `CK` 当场抛「一个哈希表只能添加到另一个哈希表」，于是**18 条臂全打印了、汇总却只数到 4 条**。
  这类红不会指向产品，只会让结论失去意义 ⇒ 已改 `ArrayList` + 末行断言「判据条数 != 行数即记红」。
- **12 条"产物相同"是夹具假红**：首版用纯色渐变，换成 23964 B 的 8-bit 照片样后只剩个位数红
  ⇒ 有损编码器对纯色不敏感，**活性判据的夹具必须有细节**。
- **两处取值域是我写错**：`--tiff-compression none` 与 `--avif-tune film` 都**不在面板/白名单域内**
  （TIFF 面板项 = `raw|lzw|deflate|packbits`，`MainWindow.xaml:764-767`；
  tune 白名单 = `iq|ms_ssim|vmaf|psnr|ssim`，`ImageEncoderArgs.cs:354-360`）。
  用域外值做两端得到的红与产品无关（`none` 那条还会让 ffmpeg 报 `Error setting option compression_algo to value none`），
  已换成域内两极 ⇒ 现 `--tiff-compression raw/lzw` 明确是**活的**（590658 B vs 52708 B）。
- **两处定性错误在终轮前被推翻**（都写进了判据本体，防下次重犯）：
  ① **布尔旋钮的"假端不显式下发"是合法写法**（false ⇒ 不发 = 编码器默认）。我第一版把
     "只有一端出现参数"直接判成"死旋钮"，于是 `-row-mt` 被误报成缺陷；实测 `-row-mt 1` 确实在真端下发了，
     产物相同只因该夹具只有一个 tile 行 ⇒ 现在记 **未判（夹具无判别力）**，只有**两端都没发**才记死。
  ② **各后端日志前缀不同**：ffmpeg 是 `[cmd] ffmpeg …`，cjxl 是 `cjxl 命令行: …`。只认 `[cmd]`
     会让整条 JXL/avifenc/JXR 通路"读不到命令行"，而"读不到"绝不能拿来当"没发"的证据
     ⇒ 现单列 **nocmd** 状态（本轮实测 `nocmd=0`，说明口径已覆盖到 cjxl 通路）。
- ⚠ 通用口径：**缺席类结论（"没这个标签/没发这个参数"）必须先用同一条命令对已知存在的一份样本量到过一次。**
  本轮我两次因为"读法本身不存在"而得到假缺席（`profile_description`、下划线式 `color_space_data`），
  一次因为"锚只覆盖 ffmpeg"而得到假未下发。

**已确证的活性/惰性读数**（照片样夹具、出货包）：

| 旋钮 | 两端 | 结果 |
|---|---|---|
| `--jpeg-huffman` | default / optimal | **活**：20901 B vs 17351 B（`-huffman 0/1` 均下发） |
| `-q` | 10 / 95 | **活**：9482 B vs 27169 B |
| `--png-pred` | none / paeth | **活**：30531 B vs 27059 B |
| `--png-dpi` | 72 / 1200 | **活**：同为 27059 B 但 SHA 不同（元数据块改变） |
| `--webp-preset` | default / photo | **活**：7482 B vs 7348 B |
| `--webp-compression` | 0 / 6 | **活**：9868 B vs 7378 B（`-compression_level` 在 libwebp 上确实生效） |
| `--tiff-dpi` | 72 / 1200 | **活**：同大小、SHA 不同 |
| `--jxl-effort` | 1 / 9 | **活**：9332 B vs 9255 B |
| `--jxl-modular` | false / true | **死**：9165 B、SHA **完全相同**，且 45 条 `[cmd]` 里 `modular` **命中 0** ⇒ 参数根本没发到 cjxl（= U5/A-30 的 L3 实机版） |
| `--avif-cpu-used` | 0 / 8 | **活**：10852 B vs 11428 B |
| `--avif-still-picture` | false / true | **活**：`-still-picture 0/1` 均下发，产物不同 |
| `--avif-row-mt` | false / true | **未判**：`-row-mt 0/1` **都下发了**，但 512×384 单 tile 行下产物同字节 ⇒ 该夹具没有判别力，需换带 `tile-rows>1` 的大图才能判（不能算成产品缺陷） |
| `--avif-tune` | psnr / iq | 两条不同通路：`psnr` 走 AVOption `-tune psnr`，`iq` 走 `-aom-params tune=iq` ⇒ 用字面量找 `-tune ` 会把后者误判成"没下发"（判据取值方式已改成正则） |
| `--gif-dither` / `--gif-palette` | false / true | **活**：16425 vs 17037、18719 vs 17037（但 A-13 记的是"语义/循环"层问题，字节动了不等于语义对） |
| JXR 质量轴（面板无控件） | 默认 / `-q 30` | **活**：30925 B vs 11354 B ⇒ A-17 是 **UI 缺控件**，不是编码器缺旋钮（补面板即可，不需换后端） |
| `--jpeg-dct float` | int / float | **被拒**：`[mjpeg] Error setting option dct to value float` → `Error opening output files: Invalid argument`，产品 rc=1 ⇒ A-20 在 L3 的原文复现 |


---

## 五、未修缺陷清单（本轮新抓，全部有复现）

- **U1（L2+，出货包）HDR/增益图 JPEG → JXL：无损宣称静默退化，且产物取不回来。**
  复现：`cjxl --lossless_jpeg=1` 吃 `src_hdr_pq.jpg`（带 MPF/APP2）⇒ `jxlinfo` 报
  `512x384, lossy, 32-bit float (8 exponent bits) RGB`（**没有** reconstruction 盒）；
  再把这个 `.jxl` 转回 `jpg` ⇒ 产物数 0、rc=1（`djxl` PPM 路 `JxlDecoderSetImageOutBitDepth failed`）。
  同一 jxl 用 `.png` 目标和 ffmpeg 直读都正常 ⇒ 是**PPM 出口的位深**问题，不是容器问题。
- **U2（L2）`--tiff-dpi` 在引擎路线上被静默降为 72。**
  复现：`--color-engine engine` 下请求 300 dpi，产物 `Xres=72` 而 `Yres=300`（**自相矛盾**）；
  legacy 引擎正常。`XResolution` 与 `YResolution` 分属两个标签，单值比较会看漏。
  性质：用户参数被吃，且产物内部不一致 ⇒ 不只是"降级"，是"降级 + 破口"。
- **U3（L2）`-lossless` 在 `libjxl` 路线上的语义与用户宣称不对齐**（既有 A-18/A-27 的同族延伸）：
  面板"无损"映射为 `distance 0`，但 `-lossless 1` 实测**可用**且与 `--lossless_jpeg` 不同轴 ⇒ 需要在裁决点显式化。
- **U4（L3，判据缺口）`--avif-row-mt` 的活性当前**无法判定**：`-row-mt 0/1` 确实都下发到 libaom，
  但 512×384 单 tile 行的产物字节不受它影响 ⇒ 需要一张 `tile-rows>1` 的大图夹具才能出判词。
  这不是产品缺陷，是**判据缺口**——把它记成"活"或"死"都是假绿/假红。
- **U4b（L3，诚实性）`--avif-tune` 域外值被**静默**丢弃**：白名单 = `iq|ms_ssim|vmaf|psnr|ssim`
  （`ImageEncoderArgs.cs:354-360`），`NormalizeTuneToken` 对不认识的值返回**空串**⇒ 既不报错也不播报
  （实测 `--avif-tune film` 产物正常生成、命令行里没有任何 tune 痕迹）。本仓既有原则是
  "编码器不可用要显式报错而非静默回退"，这一处是它的漏网。
- **U5（既有）`--jxl-modular` 在 cjxl 后端被静默丢弃**（D-5；§4.4 已给 L3 字节证据：
  两端产物 SHA 相同 `97AFF2ED823C1FC2`，且 45 条 `[cmd]` 里 `modular` 命中 0）。
- **U6（撤销）`--jpeg-huffman` "无效果"的初判是错的**：换照片样夹具后该臂明确是**活的**
  （default 20901 B vs optimal 17351 B）⇒ 上一轮"唯一死旋钮"的读数来自纯色夹具，不成立。

---

## 六、建议的覆盖范围（下一步该测什么，按"能不能抓出错"排）

1. **把 L1 判据换成 L2+**：凡"参数是否生效"，一律做**两极端值字节差分**（`t79/knob-liveness` 的形），
   不看命令行措辞、不看日志。L1 的 19 条里除结构锁外都应升级。
2. **往返判据优先于单向**：`JPEG→JXL→JPEG` 用**逐字节相同**（最强）；任何"重编码通路"必须补
   `源 → 产物 → 回读` 的闭环（本轮 U1 就是只有单向时看不见的）。
3. **第三方读者常态化**：GainMap→`ultrahdr_app`；JXL→`jxlinfo`；MPF/ICC→`exiftool`；JXR→`JxrDecApp`；
   TIFF 内部一致性→**分标签读** `Xres/Yres`（单值比较会漏）。
4. **反真空臂是必选项**：每条"文件里有 X"必须配一条"不该有 X 的输入确实没有 X"
   （t77 的 SDR 臂、t76 的 B 段同族）。没有反真空臂的判据一律按 L1 记。
5. **harness 自我约束**：① 判据"两侧都空"必须记未判而不是绿；② 夹具**必须有细节**（纯色会让活性判据假红）；
   ③ 改判据前先读被检对象；④ 产物缺失要让运行器拒绝跑，而不是静默换 Debug（§二已落地）。
6. **面板能力诚实性**：候选表里 5 个不存在的编码器名、6 类死控件（§4.3）要么补后端要么撤控件；
   "编码器不可用要显式报错"是本仓既有原则，这几条是它的漏网。

---

## 七、v1.6.0-beta3 出包过程记录（同日，含一次我自己造成的虚惊）

| 步骤 | 读数 | 取证 |
|---|---|---|
| 版本号 | `<Version>1.6.0-beta2 → 1.6.0-beta3`（保持 SemVer 连字符；`1.6.0BETA1` 那种写法会让 restore 直接失败） | `src/FfmpegGui/FfmpegGui.csproj:19` |
| 产品源码是否变化 | `find src \( -name '*.cs' -o -name '*.xaml' \) -newermt "2026-10-01 08:23"` 命中 **0** ⇒ beta3 与 beta2 只差版本号；包体大小同为 22,683,136 B | 同上 |
| 发布轮整轮 | **83 步全部通过、零非零退出**，`artifact-sha16: FfmpegGui.dll=41B44697A8289D44` | `tests/output/t83/round-beta3.log` |
| 出包 | `pack.ps1 -Mode all` → `-x64.7z` 11,093,184 B / `-x64-full.7z` 55,319,824 B；包内 `ProductVersion=1.6.0-beta3+cbcf11ae`，`exe sha=E50D57D6C4C9D754` | `tests/output/t83/pack-beta3-r2.log` |
| 独立验收（不信脚本自己打的"完成"） | **16/0**：归档条目数 app=17 / full=580；14 项必需成员齐备；**磁盘目录与归档同数**；六件工具与仓内 `publish/PLAN` **逐字节同指纹** | `tests/output/t83/verify-package.ps1` |

### 7.1 这一轮里被修掉的一个**出包侧 fail-open**

我第一次跑 `pack.ps1` 时中途把它 kill 了（想补日志重定向）。后果不是"重跑一遍就好"：

- 残留进程锁住输出目录 ⇒ `pack.ps1:72` 的 `Remove-Item` 报
  `The process cannot access the file … being used by another process`，**但脚本继续往下走**；
- 于是压缩照做、还一路打印 `✅ 压缩完成` 与 `🎉 全部 2 个版本打包完成!`；
- 实况是磁盘上的产物目录被清成 **58 个文件**（`PLAN/exiftool/`、`PLAN/artifacts/` 全没了），
  而**归档却是完整的 580 个文件** ⇒ 目录与归档处于两种不同状态。

修了两处（`pack.ps1`）：

1. 清理旧目录失败 ⇒ **`throw` 中止**，不再拿旧目录继续出包；
2. 压缩**前**加完整性闸：必需成员（`FfmpegGui.exe`、三份法律文件、`licenses/`，完整版再加
   ffmpeg/cjxl/djxl/jxlinfo/exiftool/JxrEncApp/JxrDecApp/avifenc/dngtool 与四个 PLAN 子目录）
   逐项 `Test-Path`，且总文件数低于门槛（full=560 / app=8）即 `throw`。
   本次实测两档都通过并打印 `✅ 完整性闸通过：17 / 580 个文件`。

### 7.2 我在同一件事上连犯的两次读数错（写下来防再犯）

- **`7z l` 用反斜杠打印路径**，我拿正斜杠式去 grep 归档清单 ⇒ 六件工具**全部恒 0 命中**，
  于是把"完整归档"误报成"包内缺 exiftool/artifacts"。缺席类判据必须先对已知存在的一条命中过才算数
  （`verify-package.ps1` 里已改成反斜杠式，并加了"必需项齐备 = N 项全在"的正向打印）。
- **拿磁盘目录的状态去推归档的状态**：两者当时恰恰不一致（目录残缺、归档完整）。
  ⇒ 出包验收必须**分别验目录与归档并对账条目数**，这也是 `verify-package.ps1` 现在的结构。
- 另外"我 kill 了进程 ⇒ 目录残缺"这条因果是我先说成"打包脚本的回归"，实测才归对到位移；
  结论从"pack.ps1 产出残缺包"改成"pack.ps1 在清理失败时不中止"（真缺陷）+ "我中断了运行"（操作因）。
