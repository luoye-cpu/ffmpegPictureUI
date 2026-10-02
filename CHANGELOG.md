# 更新日志

本项目所有值得注意的变更都记录在此文件。

> ℹ️ 各版本条目中记录的「探针 mode 数 / 门禁脚本数」为**当次发布时的基线**。
> 这些数量随开发持续增长，**实时权威值**以 `tests/scripts/_run-step3-gates.ps1` 中的
> `$Probes` 与 `$expectedScriptCount` 为准。

[English](CHANGELOG.en.md)

---

## 📝 更新日志

### v1.6.0-beta4 (2026-10-01) — 增益图 JPEG 不再冒充无损 + float JXL 能取回来 + `--jxl-modular` 接上；首批 L3 判据进门禁清单（60→65）

> **谱系**：与 beta3 不同，本条**是真行为变更** —— `src/` 下 3 个产品文件在 beta3 出包后被改动
> （`CjxlService.cs` / `QueueProcessor.cs` / `ImageEncoderArgs.cs`），下面四条都各有产物级判据背书。
> ⚠ 2026-10-02 追加：**新增**一节（缩略图）也落在 beta4 里，产品侧再 +1 个文件 `ThumbnailService.cs`
> （新）+ `ExifToolService.cs` / `FormatCapabilitiesService.cs` / `QueueProcessor.cs` / `CliParser.cs` / 模型两处。

**✨ 新增**

- **缩略图（EXIF IFD1）：`--thumbnail` / `--thumbnail-size` / `--thumbnail-quality`**。高级编码面板加一只
  `ThumbnailPanel`（勾选 + 长边 + 质量），只对**实测能兑现**的容器开放：`jpg / png / apng / webp / avif / jxl` 六格
  （判据 = 写完能用 `exiftool -b -ThumbnailImage` **取回与 `ThumbnailLength` 等长的图像数据**，apng 另用
  `cjxl`+`jxlinfo` 做第二个独立读者交叉核过：两者同为 2230 B，而 ffmpeg 造的悬空件只有 250 B）；
  `tiff / gif / jxr / dng` 实测均被 exiftool 拒（「0 image files updated」**而退出码仍是 0** ⇒ 只看 rc 会假绿），
  勾选状态下走这些格式会**出声拒绝**而非静默出一张没缩略图的图。GIF 结构上没有 EXIF 槽位，JXR 收得下 ICC 却收不下 IFD1。
  实现是**后置单通道**：从**最终产物**缩一张 8-bit JPEG（`scale` 两维都钳长边、只缩不放、取偶），
  再走 `exiftool -ThumbnailImage<=` 嵌入 ⇒ 不碰 cjxl/avifenc 命令行，也不新增第二份探测口径。
  调用点收在**元数据阶段**（`RestoreMetadataAsync` 内、色彩后置之前），维持本仓「元数据先写、
  色彩标签最后定」的不变量；`StripAll` / `StripExifAll` 时缩略图**让位**（隐私不能让新功能破口）。
  门禁：新增 `ServiceProbe thumbnail` 模式 **40/0**（2026-10-02 本机实测，10 s；两次变异验牙
  `M1 33/7`、`M2 38/2`），已接进 `$Probes` 默认清单（第 23 个 mode）。
  ⚠ **未判**：缩略图在各查看器里是否**显示**（本机未在资源管理器/第三方 viewer 里逐项看过）；
  广色域产物的小预览因 EXIF 无 ICC 槽位会按 sRGB 解读而略欠饱和。

**🐛 修复**

- **U1 前半｜携带增益图的 JPEG 不再被算成"免解码重封装"**：源 JPEG 内含 Ultra HDR / ISO 21496-1 增益图时，
  `--lossless_jpeg=1` 的"无损"宣称会**静默退化**成 float 重编码 —— 增益图被抹平、原 JPEG 再也取不回来，
  而日志照常报"无损"。单一裁决点 `CjxlService.UsesLosslessJpegRewrap` 从 3 条加到 **5 条**
  （新增"源不携带增益图"“未强制模块化"；增益图由 `GainMapDecoder.JpegContainer` 读文件头 256 KB 判定，
  带 512 项按路径缓存）。判据补在**这个谓词**而不是只补在命令行里，是因为它同时决定两件事：
  命令行发不发 `--lossless_jpeg=1`，**以及**元数据恢复要不要把源的取向标签带走 —— 漏掉它，一条
  "像素其实已被解成 float"的路线仍会被当作"像素一个没动"而保留 `Orientation=8` = 查看器二次旋转
  （A-28 的同族）。`BuildCjxlArguments` 另加一条播报，点名"这份 JPEG 带增益图 ⇒ 不走无损重封装"。
- **U1 后半｜float JXL 输入不再静默失败**：`djxl` 的 PPM/PNM 输出不支持 float 图像 ⇒ 中转必然非零退出，
  旧代码在这里 `return false`（任务标失败、不给第二条路）。现改走 **ffmpeg 直读 `.jxl`**，
  终态串点名用的是哪条路（`已完成 (ffmpeg 直读 JXL)`），失败时把两个退出码都报出来。
- **U5｜`--jxl-modular` 从空枪接成实参**：该选项此前被 UI 采集、进模型、再不被任何消费者读取 ⇒
  勾了等于没勾。现在发 `--modular=1`，且它与 jbrd 无损是**结构互斥**的（jbrd 依赖 varloss 通道），
  两者同时给出时 modular 获胜并播报"这一把覆盖了无损重封装"。
- **U4b｜libaom-av1 承接不了的 tune 档位不再静默丢（同日补齐第二半）**：`--avif-tune MS_SSIM`（SVT 独有）走 libaom 时
  旧实现**既不发 tune 参数、也不说一句** ⇒ `rc=0`、产物照常出，用户以为调了（这是从代码直接可证的：
  该值折成 `ms_ssim` 后落进 switch 的空档）。现按本仓既有原则"不可用要显式播报"打印原因与可用档位；
  用户没选（折成空串）不播报，免得把"没选"说成"失败"。
  **第二半**是同一个洞的另一半：`NormalizeTuneToken` 此前把**认不出**的显示串也折成空串，
  而空串同时是"没选/选了默认档" ⇒ `--avif-tune film` 这类越界值到了分发层也是"不发也不说"；
  另外那个 switch 只有 libaom 与 SVT 两支 ⇒ 硬件 AVIF 后端（nvenc/qsv/amf/vaapi）上任何已选 tune
  都被整支跳过。⚠ 这条**面板直接可达**：那把 tune 下拉的默认选中项是 `avif.tune.iq`
  （`MainWindow.xaml.cs:916-918`，`FillComboByLoc(…, 4, …)`），而它人在 `LibaomAvifPanel` 里
  （`MainWindow.xaml:551-555`）—— 换到硬件后端后面板被藏起来、采集侧却仍按它的 `SelectedItem` 取值
  （`MainWindow.xaml.cs:2840`，只受"高级编码选项"总开关管）⇒ 用户什么都没"选"，`tune=IQ` 就已经
  被静默吞掉了；旧预设回放同理（`Models/PresetData.cs:168` 带 `EncoderName`）。
  **CLI 到不了这一支**：`-e/--encoder` 收的是 EncoderBackend 枚举，没有任何旗标能设
  `FfmpegOptions.Encoder`；传 `-e av1_nvenc` 会按既有约定**硬失败**（`未知编码器: av1_nvenc`、
  rc≠0、零产物 —— 实测 2026-10-02，`tests/output/t84/nvenc_probe.err`）。
  现在：默认档与空串才返回空串，其余认不出的原样返回并由分发层点名；tune switch 之后另加
  一条中心播报，兜住"这条后端整支没有 tune 通路"的形状。
  判据 = 旋钮活性套件（清单第 65 条）的 **N 段命名臂**：不可表达的值要么非零退出、要么有播报，
  只有"`rc=0` 且全程没一句话"才判红（本批新增 `--avif-tune film` 一臂）。
  ⚠ **诚实声明**：硬件后端那一支的播报**目前没有闸内判据**（CLI 走不到，只有 GUI 采集/预设回放到得了；
  我试过用 `-e av1_nvenc` 写一臂，量出来的是"无效后端值硬失败"而不是这条播报 ⇒ 已把那条臂撤掉）。
  ⇒ 登记为 §5.2 的缺口项，补法与命令写在 `docs/HANDOVER_2026-10-02.md`。

**✅ 测试**

- **`ServiceProbe` 新增 8-bit 降位恒等断言**（`PROBE RESULT: pass=39 fail=0`）：8-bit 源经 `format=rgb48le`
  升位后的码值恒为 **257 的整数倍**（这正是"可精确还原"的前提），断言把 256 级斜坡按 `v*257` 直接摆进 16-bit
  缓冲、再过内核的 16→8 出口，要求**逐码值恒等**；扫 16 个抖动相位 × {解析式, 表驱动} 两条出口，
  实测最大偏差 **0/255**。带两条反真空对照（夹具与产物都须是满值域斜坡、去重数 ≥250；与 `want+1` 比的
  最小偏差须恰为 1 —— 读到 0 就说明这条恒等判据其实没在比东西）。
  ⇒ 这条把 #12（`BayerCenter` 居中）从"读过代码认为它对"变成**有断言背书** —— 其实现随 v1.6.0 已出货，
  本条补的是判据。旧写法减 `2/16` 会让 16 相位里的 6 个把本可精确表示的码值抬 +1。
- **首批 L3 判据从 gitignored 目录迁入受管清单（60 → 65）**：`t69/pkg-smoke3`、`t76/jbrd-e2e`、
  `t76/orientation-rewrap`、`t77/gainmap-thirdparty`、`t79/knob-liveness` 此前只活在 `tests/output/`
  （`.gitignore:78`）⇒ 一次 `git clean -xdf` 就没了，也不在任何轮里被跑。现全部落 `tests/scripts/`：
  路径改为运行时推导（`$PSScriptRoot`），默认被测 = **刚构建的 Release 产物**（每轮都跑，缺则拒绝跑、
  不回退 Debug），设 `$env:PKG_VER` 时才升 L3 打出货包；包不在位或包内 `ProductVersion` 与期望不符
  ⇒ `exit 2` 且**不产出任何读数**（防"beta4 的读数打在 beta3 包上"）。

### v1.6.0-beta3 (2026-10-01) — 两台"假绿机器"修掉 + 首批打出货包（L3）的判据；含 09-30 的取向标签与几何探测批次

> **谱系（重要，别把两版当成两次行为变更）**：`src/` 下自 beta2 出包（10-01 08:23）后**零个产品源文件被改动**
> （`find src \( -name '*.cs' -o -name '*.xaml' \) -newermt "2026-10-01 08:23"` 实测命中 **0**）⇒
> **beta3 与 beta2 的产品行为差异只有版本号一处**。DLL 指纹确实变了
> （`8FF98B12BE0B71CD` → `41b44697a8289d44`），原因正是版本串被编译进 assembly metadata，
> 不是代码变更 —— 这一点别拿指纹当"改了实现"的证据。本批其余变化全在测试/门禁/文档侧。
> 下面「2026-09-30 第二段」那组取向标签/几何探测改动**已由 beta2 包携带**，本条是把它正式计入版本记录。

**🐛 修复（全部在测试/门禁侧，产品行为未变）**

- **35 个门禁的"构建类型标注"恒假**（40 处）：标注行用 `-like '*\Debug\*'` 去比一条**正斜杠**拼出来的路径
  ⇒ 永不命中，于是 `Release 缺 ⇒ 静默换 Debug` 这条回退在日志里被写成 `(Release)`。
  实测抓到一条 `PASS=22 FAIL=0` 的门禁，被测的是 `09-30 22:11` 的 Debug 产物（早于本批所有改动）。
  模式改为同时认两种分隔符 `'*[/\\]Debug[/\\]*'`（PS 5.1 / 7 双宿主实测语义）。
- **整轮不再容忍"被测产物缺失"**：`bin/` 在 `.gitignore` 里 ⇒ 产物被外部构建打回时 git 完全看不见；
  STALE 闸原先只比 mtime 且用 `-and` 短路，产物为 `$null` 时整条判据被跳过。现补 5 条**缺席即拒跑**，
  并把被测产物指纹钉进日志头（`artifact-sha16: FfmpegGui.dll=… FfmpegGui.exe=… …`），
  取代"人工在结论里抄 DLL 前 16 位"的旧约定。本轮整轮 **83 步全部通过**，
  绑定 `FfmpegGui.dll=8FF98B12BE0B71CD`（与出货包内那份同指纹）。

**✅ 测试（此前清单里 L3=0：没有一条门禁打的是出货包）**

- 新增五套**产物级/出货包**判据：`t76/jbrd-e2e.ps1`（JPEG↔JXL 无损互转换，**89/2**，含逐字节往返、
  反真空、三类参数冲突、后端翻转、手工独立参照）、`t76/orientation-rewrap.ps1`（取向标签 × 免解码重封装，14/0）、
  `t77/gainmap-thirdparty.ps1`（**用 libultrahdr 自己认证 Ultra HDR** 并把文件里的逐项数值与产品声明的
  log2 余量对账，13/0；此前 GainMap 数值只从产品自己的日志正则取，属 L1）、
  `t69/pkg-smoke3.ps1`（包级冒烟 17/0）、`t79/knob-liveness.ps1`（**逐格式高级编码选项"活性"实测**，
  18 臂 = 活 15 / 死 1 / 被拒 1 / 未判 1）。
- 台账全文见 **`docs/PIPELINE_TEST_AUDIT_2026-10-01.md`**：证据等级分类（60 条 = L3 0 / L2+ 24 / L2 17 / L1 19）、
  逐格式"缺失 / 不可用 / 死控件"矩阵（含 5 个本构建根本不存在的编码器候选名）、应覆盖范围建议。

**🐛 已知未修（本轮新抓，都有复现）**

- **增益图 JPEG → JXL**：`--lossless_jpeg=1` 对带 MPF/APP2 的 JPEG **静默退化**成
  `lossy, 32-bit float`（应点名"做不到"），且该产物**取不回来**（`djxl` 路 0 产物、rc=1）。
- **`--jxl-modular`** 在 cjxl 后端**两端都不下发**（ffmpeg-libjxl 路线有效）⇒ 同一选项两条通路一条活一条静默死。
- **`--avif-tune` 域外值被静默丢**（白名单外返回空串，不报错也不播报）。

**🐛 修复（打包工具侧：一个能产出"残缺发行件"的 fail-open）**

- **`pack.ps1` 在清理旧输出目录失败时不中止**：`Remove-Item` 报"文件被另一进程占用"后脚本继续走压缩，
  照样打印 `✅ 压缩完成` 与 `🎉 全部 2 个版本打包完成!`。实测该次磁盘上的产物目录只剩 **58 个文件**
  （`PLAN/exiftool/`、`PLAN/artifacts/` 全缺），而归档却是完整的 580 文件 ⇒ 目录与归档两种状态并存。
  现：① 清理失败即 `throw`（绝不拿旧目录出包）；② 压缩**前**加完整性闸 —— 必需成员逐项 `Test-Path`
  （`FfmpegGui.exe`/三份法律文件/`licenses/`，完整版再加 ffmpeg、cjxl、djxl、jxlinfo、exiftool、
  JxrEncApp、JxrDecApp、avifenc、dngtool 与四个 PLAN 子目录）+ 总文件数门槛（full ≥ 560、app ≥ 8），
  不达标直接拒绝出包。本次两档均通过并打印 `完整性闸通过：17 / 580 个文件`。
- **新增独立出包验收 `tests/output/t83/verify-package.ps1`**（16/0）：不信脚本自己打的"完成"，
  验归档条目数、必需成员、**目录与归档对账**、六件工具与仓内 `publish/PLAN` 逐字节同指纹、包内 `ProductVersion`。
- ⚠ 取证口径教训（全文见 `docs/PIPELINE_TEST_AUDIT_2026-10-01.md §7.2`）：`7z l` 用**反斜杠**打印路径，
  拿正斜杠去 grep 会让六件工具恒 0 命中 —— 我因此一度误报"beta3 包里缺 exiftool/artifacts"。
  缺席类结论必须先对已知存在的一条命中过才算数。

**🐛 修复（本批第二段：取向标签与几何探测，2026-09-30）**

- **手机/相机直出的 TIFF（`Orientation=8`）转 JXL/PNG 会产出"错位条纹"图**：ffmpeg 对取向标签会
  **自动旋正**（实测：6000×4000 的 16-bit TIFF，解码帧挂 `displaymatrix rotation=90` ⇒ 实际是 4000×6000），
  而色彩引擎读的是 `ffprobe stream=width,height`（= **coded** 6000×4000）⇒ 编码段用未旋正的几何去声明
  已旋正的像素，**每一行都被错位重排**。实测该产物 vs 真值 **PSNR 7.97 dB**；把同一段像素改按 4000×6000
  重排再旋回则 **inf**（逐字节等）⇒ 错的是几何标注，与 JXL 无关（PNG 出口同样 7.97 dB），走「最长边限制」
  时反而正常（中继产物 coded 与显示已一致）。**与目标格式无关，legacy 直通路线一直是正确的。**
- **质量分析对这类图两头都错**：预检比的也是 coded 尺寸 ⇒ ①对**正确**的产物直接报
  「源图 (6000x4000) 与输出图 (4000x6000) 分辨率不一致」而拒绝对拍；②对**几何错**的产物预检反而通过，
  最后只把 ffmpeg 的报错尾巴丢给用户。现预检与帧长计算统一按**显示**尺寸，报错文案点名"含取向旋正"。
- **宽高探测在仓库里有四份实现，全部只读 coded 尺寸**（色彩引擎 / GainMap 解码 / 质量分析 / 队列的
  GainMap 专用管线）⇒ 修一处漏三处。现统一到一个 `ImageGeometry`（coded 尺寸 + 帧级 `displaymatrix`
  旋转角，±90 交换宽高），并由结构锁禁止再长出第五份。
- **修好像素后仍会横过来：元数据恢复把源的 `Orientation` 抄到了已旋正的产物上**——两条恢复通路（`-all:all` 全量复制与安全模式 `-EXIF:all`）都不排除该标签，实测对 4000×6000 的无标签产物跑一次恢复 ⇒ 产物读回 `Orientation : 8` ⇒ 合规查看器**再转一次**（像素对、标签错）。现两条通路都剥掉它；唯一例外是 **JPEG→JXL 免解码重封装**（像素原样搬 DCT 系数 ⇒ 标签仍然成立、必须保留），该判据与 cjxl 命令行里 `--lossless_jpeg=1` 的判据是同一份实现。⚠ 排除式形态是量出来的：照抄 GPS/XMP 的组通配写成 `--Orientation:all` **实测不生效**。
- **解码产物的长度自检由"下界"改为"精确等式"**：旧写法 `rawLen < w*h*6` 对转置**完全盲**
  （旋正前后字节数恒等），是本次缺陷能静默出厂的直接原因之一。

**🧪 测试**

- 新增探针 `ServiceProbe orientmeta`（8 条）：M1/M3 两条恢复通路各自剥标签、M2 控制臂（Make/Model 必须仍到产物，
  否则"整块复制没跑"会让 M1 假绿）、M4 反证臂（重封装时标签必须留得住）、M5 判据三格；
  并入 `verify-geometry-orientation.ps1` 第 ⑥ 段 + 三条结构锁。变异验牙：排除式退回空串 ⇒ `pass=6 fail=2`。


- 新增门禁 `verify-geometry-orientation.ps1` **18/0**（配套探针 `ServiceProbe orientation` 15 条）：
  夹具自检（coded 64×32 而 `rotation` 必须 90 ⇒ 两口径轴向不同才有效）、16-bit 出口与独立参照
  **逐字节等**、8-bit 出口 ≥45 dB、`+最长边限制 16` 时滤镜必须是纵向分支 `scale=-2:16` 且产出 8×16、
  无标签负控不得凭空旋转。⚠ 变异验牙：把旋转补偿改回恒等（= 退回 coded）⇒ **7 条 FAIL**。
- 新增结构锁 `_probe-geometry-single-source-scan.ps1`（清单第 59 条）；红/绿两态退出码 1/0 均经实测。
- **把 `GainMapTestHost` 全量套件接进门禁清单**（第 60 条 `verify-gainmap-host.ps1`，并补 STALE 第 ⑤ 对）：
  该宿主的 exe 此前**不被任何门禁执行**，于是 2026-09-20「SDR ⇒ 普通 JPEG」裁定作废的 4 条 `sdr` 格期望
  在树里红了 10 天无人被拦。测试期望已按裁定换成 **6 条更严的契约断言**（降级必须点名、产物必须是完整
  JPEG、**不得**残留 MPImage2/hdrgm、底图尺寸不得变、解码器必须判为**非 UltraHDR**）⇒ 套件 **86/0**、新门禁 **14/0**（变异验牙：禁掉零余量分支 ⇒ `PASS=3 FAIL=11`）。**产物侧无改动**——那 4 条是测试写旧了。
- 另补**取向标签（`Orientation=8`）两条用例**（`E1` 九条 + `E2` 八条 ⇒ 套件 **103/0**、门禁 **18/0**）：`E1` 钉 GainMap 解码走**显示**几何（自带反证臂：显示参照 46.22 dB vs coded 步长重排 12.95 dB），`E2` 钉质量分析对取向源不再误拒、且 `E2g2` 保证**真**轴向不一致仍被拒（挡住"用放宽判据换绿灯"）。

### v1.6.0-beta2 (2026-09-30) — cjxl 三项修复：自动光子噪声端到端 / 无损体积（16-bit 载体）/ JPEG→JXL 重封装激活

**🐛 修复**

- **勾了「自动读取 EXIF ISO」的 JXL 任务在默认路线上必失败**：`--photon_noise_iso` 的 UI 预览占位串 `auto`
  被直接发给 cjxl（它的解析器只吃浮点，`ParseFloat && >= 0`）⇒ 编码器在读 stdin 之前就退出 1、零产物，
  界面停在「处理中」，30 分钟后只报「超时或已取消」。根因是「从 EXIF 解析 ISO」这一步内联在旧的分派分支里，
  引擎接管后那条分支不再执行 ⇒ 解析上提到分叉之前，三条 cjxl 出口共用一份已解析参数；
  命令行构造器另加 fail-closed 兜底（真执行时宁可省略该 flag 也绝不发占位串，并点名原因）。
- **无损（distance=0）叠加光子噪声会静默破坏逐比特无损**：实测 891×981 16-bit，`-d 0` 往返逐字节相同，
  而 `-d 0 --photon_noise_iso=400` 有 **99.46%** 的 16-bit 样本偏离源（平均偏差 30875/65535），
  而 jxlinfo 对两者都报 `(possibly) lossless`（噪声由解码端按帧头参数合成，体积只多 10 B ⇒ 从文件大小查不出来）
  ⇒ 该组合不再发给编码器；面板上勾选无损时光子噪声两项置灰（撤掉无损即恢复，不吞用户意图）。
- **外部编码器提前退出后不再挂 30 分钟**：全仓 **5 条**双进程管道（引擎 cjxl 出口、传统外部编码器管道、
  djxl→cjxl、djxl→ffmpeg、djxl→cjpegli）统一为同一契约 —— 传输一失败就杀掉写侧进程（写侧会永久堵在
  无人读的 stdout 上），且**半截流不再可能被算成功**（原先两条 djxl 管道只看读侧退出码，
  传输抛异常也能判「已完成」并恢复元数据）；异常文本带上编码器自己的 stderr —— 此前用户看到的是
  broken pipe 这个症状，不是真因。`verify-color-wiring.ps1 (10h)` 逐条锁这套要求，并带一条
  「进程→进程管道条数」发现闸（新增第 6 条而没入表 ⇒ 先红）。

- **8-bit 源的无损 JPEG XL 被按 16-bit 交付，产物比源 PNG 还大**：引擎的直通/携带计划恒用 16-bit
  承载（那是**中间件**精度），而 cjxl 出口把它当成了**交付**样本深度 ⇒ 同一份像素 8-bit 交付
  11,485.5 kB、16-bit 交付 **20,233.6 kB（+76.2%）**，对源 PNG（12,163.9 kB）从"小 6%"变成"大 66%"。
  修复在出口：PPM/PAM 的样本深度改按 `plan.TargetBitDepth` + 容器自己的 `MapPixFmt` 取档，
  降位交给 ffmpeg 的 `format=rgb24` 做（实测逐比特精确）。
  ⚠ 弯路记一笔：先试过在规划层把直通支改成 8-bit，结果引擎内核 `EncodeTo8` 的
  `code16/65535*255` 在 float 下对 ×257 码值不是恒等（0..91 恒等、92 起约半数偏低 1）
  ⇒ **把无损改成了 ±1 有损**。同一份素材 A/B（旧二进制当对照臂）：**8,206 kB → 4,584 kB（−44.1%）**，
  两侧往返都逐比特相同。
- **JPEG→JPEG XL 无损快速重封装（jbrd）从未被激活**：三道独立失效叠加——数据模型默认 `false` +
  采集式「面板不可见 ⇒ false」+ 与一只**默认勾着**的「保留 Ultra HDR 增益图」无条件相与 ⇒
  默认配置下 JPEG→JXL 走的其实是 `-d 3.8` 的**有损**像素重编码（用户以为拿到无损），
  而全仓 95 个门禁脚本没有一条问过"app 自己发没发 `--lossless_jpeg=1`"。
  现在：**开关默认启用**（`FfmpegOptions.DefaultJxlLosslessJpeg = true`），JPEG 输入 + JXL 输出
  一律优先重封装；实测产物 3,042 kB / 源 JPEG 3,209 kB（0.95×），jxlinfo 报
  `JPEG bitstream reconstruction data available`。
  同时把「开关」与「本次有效值」分成两件事（有效值必须与"输入真是 JPEG"相与，否则
  `ImageEncoderArgs` 会把 **PNG→JXL 整批误挡出色彩引擎**——它只看布尔、看不到输入格式）。
- **JPEG 与 JPEG XL 两格共用同一只「无损重压缩 JPEG」开关**（新面板 `JxlLosslessJpegPanel`，
  与 `JpegGainMapPanel` 同一条理由放在按格式切换的面板之外；两只控件 = 两个真值）。
- **开关默认启用后，`-e Ffmpeg` 后端的质量档被 `-distance 0` 整条吞掉**：那条传统管线特例把
  「用户要 jbrd」翻译成「恒发 `-distance 0`」，而后端根本做不到重封装 ⇒ 同一 JPEG 在
  `q30/q50/q75/q90` 四档产物**逐字节同尺寸**（59,685 B，取证 `tests/output/t70/`）——滑块动了、产物不动。
  现在删除该特例，jxl 的质量档一律走 `BuildJxlOptions` 这一根天平（探针 `qa-①` 锁 q30⇒`-d 10.5`、
  q90⇒`-d 1.5`），「这个后端做不到 jbrd」改由任务日志点名，而不是用一个参数冒充。
- **同一类「中间件精度被当成交付深度」的缺陷在 JXR 出口也存在**：中转容器、`-pix_fmt`、TIFF 附加项
  三处都按 `plan.OutBitDepth` 选档 ⇒ 8-bit 素材也被按 16-bit 交付。同素材 A/B（**旧发布包当对照臂**，
  取证 `tests/output/t71/jxr-exact.ps1`）：**877.2 kB → 186.8 kB（−78.7%）**，两侧解码回来
  **逐比特相同** ⇒ 改的是载体档位，不是像素。
- 顺手两处不诚实：`生成命令` 按钮此前**无条件**打印 `--lossless_jpeg=1`（不看开关、不看质量档，
  是一条永远不会执行的命令）⇒ 改与实跑同一个构造器；`-e Ffmpeg` 后端做不到 jbrd 此前只写在
  源码注释里 ⇒ 现在打进任务日志。

**🧪 门禁**（每组都做过变异验牙）

- `ServiceProbe wire` +7 条：命令行构造规则（不发 `auto`、预览保留 `auto`、distance=0 不发噪声、正控）
  ＋ `qa-①②③` 三条质量轴（开关开着也不吞质量档、真要无损由 `Lossless` 决定、`-modular` 跟着单一真值）
- `verify-color-wiring.ps1` 新增 `(10g)` 段 +9 条（真产品 CLI 端到端 + 三条源码结构锁）、
  `(10h)` 段（5 条双进程管道的杀写侧/半截流不判成功）、`(10j)` 段 +3 条（cjxl/JXR 出口的交付档取自
  `TargetBitDepth` 而非中间件档）
- `UiTestHost` 新增 `J9d-a~d` +4 条（无损/非无损下光子噪声控件的可用性状态机）、`J16a/b`（共用开关面板的采集）
- `verify-jxl-codestream-route.ps1` 新增 `⑫`（4 条）：jbrd 的**文件级**判据 —— JPEG→JXL→JPEG 与源
  **逐字节相同**（实测同一 SHA256 前 16 位 19EA0030240ACB77），并带反真空臂（显式关掉 jbrd ⇒ 往返
  **不再**逐字节相同）。此前全仓只判过"正向产物含 reconstruction 盒"，从没比过往返文件本身。
- `verify-color-wiring.ps1` 新增 `(12)`（8 条 ⇒ 该门 140/0）：取向自洽 —— 先自检夹具（coded=640x480 且
  Orientation=8，不成立就整段判红而不给绿），再判 jpg/png/webp 三格"像素被旋正 ⇒ 标签必须丢"、
  jxl(jbrd) 一格"像素没动 ⇒ 取向必须被带走"，最后一格反真空（jxl 关掉 jbrd ⇒ 标签反过来必须丢）。
  ⚠ 该段第一版用 `ffprobe` 读 JXL 尺寸 ⇒ 假红一条：ffprobe 对 JXL 返回的是**显示**尺寸（它自己应用了
  容器 Orientation），coded 尺寸只能从 `jxlinfo` 第一行读；改用后者后同一条判据成立。
- `verify-jxl-codestream-route.ps1` 新增 `⑩`（app 自己发没发 `--lossless_jpeg=1`，含 jxlinfo 独立读回 +
  PNG 输入负控 + XAML 单份结构锁）与 `⑪`（`EncoderUnsupportedSetting` 的 jxl 格首次被**真跑**驱动：
  四条臂两两只差一个变量 —— G1↔G3 差输入扩展名、G1↔G4 差开关值，故拦截判据"吃输入、也吃那个值"
  不需要改源码做变异就能证伪）
- `_lib-reject.ps1`：**26 处 `file:line` 定位重新锚到当前树**（`ColorVerdict.cs`/`ColorEngineRouter.cs`/
  `ColorStrategyPlanning.cs` 都漂移过 ⇒ 自检由 34/6 回到 **40/0**，变异臂 `pass=35 fail=6` 与文件头声明一致）。
  ⚠ 该库**不在 60 条门禁清单内**（`_lib-*` 只被 dot-source），所以它的定位漂移不会让任何整轮变红 ——
  这条事实已写进其 NOTES N7，并顺手更正 `EncoderUnsupportedSetting` 的 jxl 子格前置条件（需 JPEG 输入）。
- **同批附带修掉一处"假文案"（打包冒烟实测抓到）**：JXR 出口的进度播报与失败异常都用 `out8`（盘上中间件
  档位）判断中转容器，而容器实际由 `deliver8`（交付档位）选 ⇒ 8-bit 直通源写的是 `in.bmp`，日志却说
  「raw → TIFF」。同一段里两个谓词各说一件事，正是本仓反复出现的"两处独立推导"形状。
  现在两处都读 `deliver8`，并由 `verify-color-wiring.ps1 (11)` 新增一条判据钉住
  （**播报的容器名必须与命令行的中转文件名一致**）。
- **⚠ 本包曾带一处取向回归，已由产物级测试抓到并修掉（2026-10-01）**：上面那条"开关默认启用"把
  `JxlLosslessJpeg` 的**有效值**在**所有** JPEG 输入上都置成了 true，而同一个布尔又被元数据恢复当作
  「这次像素没动」的唯一判据（`CjxlService.UsesLosslessJpegRewrap` 当时只与"输入是 JPEG"）⇒
  **JPEG→JPEG/PNG/WebP/AVIF** 也自认"没动像素"，把源的 `Orientation=8` 留在已被解码器旋正的像素上
  ⇒ 查看器二次旋转。出货包实测（源 coded=640×480 / Orientation=8）：四格产物 coded=480×640 且
  标签仍为 8。修法是给该判据与两处归一各补上"目标格式必须是 jxl"这个本来就隐含在语义里的条件；
  修后四格标签均被丢弃，而 jxl(jbrd) 仍正确带走取向（EXIF=8 且容器 Orientation=8，两者一致）。
- 另两处**未修**、但已被本次审查登记（附复现，见 `docs/ENCODING_OPTIONS_AUDIT_2026-09-26.md` 第六节）：
  ① app 自产的 **HDR/float JXL 回灌失败**：`Ultra HDR JPEG → JXL` 得到 `32-bit float` 的 JXL，
     再转任何目标都死在 `djxl 解码退出码 1`；手跑 `djxl → .png` 成功、`→ .ppm` 报
     `JxlDecoderSetImageOutBitDepth failed`，而随包 ffmpeg 能正常解出 `rgbf32le`
     ⇒ 是反向管道的中间格式选择问题，不是产物坏了。② `--jxl-modular` 在 **cjxl 后端**下被静默丢弃
     （`CjxlService` 里该字段命中 0 次；面板 `JxlFfmpegPanel` 已按后端收窄，所以只有 CLI 这条路会
     拿到"设了但不生效、且一句话都没有"的结果）。
- `verify-color-wiring.ps1` 第 (11) 段与 `verify-jxr-anim-input.ps1` 第 ③ 段是 JXR 改动的**镜像断言**，
  原先钉的是"中转恒 48-bit"：已按新契约重写并**加**了判据（两档 2×2 实测：8-bit 无 alpha `24-bit BGR`／
  16-bit `48-bit RGB`／8-bit alpha `32-bit BGRA`／16-bit alpha `64-bit RGBA`，码表逐字取自 JxrDecApp 自己的
  usage；解码码改由**产物 PixelFormat** 现推，档位再漂时红在"期望 vs 实测不一致"那句，
  而不是红在"产物解不出来"这种会被误读成坏文件的假红）。同批新增体积锚：
  同一素材 8-bit 交付 30,925 B < 16-bit 交付 151,899 B（alpha 格 8,292 B < 81,655 B）。
  两部门禁现 **129/0** 与 **59/0**。

**ℹ️ 本包另含工作树中尚未提交的 `--preserve-structure` 与同批输出冲突防护改动（2026-09-28 那批，非本轮所写）。**

### v1.6.0 (2026-09-28) — 双语完整本地化 + UI 可用性修复 + 全局异常兜底 + 色彩矩阵正确性修复

**🌐 双语本地化补全（i18n）**
- 硬编码中文 **122 → 9** 处（剩余均为动态占位值与语言自称，按设计不迁移）；`<sys:String>` 选项 **67 → 0**
- 新增 `FillComboByLoc` 机制：ComboBox 选项改由 C# 填充，支持语言切换实时刷新
  （⚠ `<sys:String>` 是元素内容，`{ext:Loc}` 在那里不生效，此前整类选项在英文界面下仍是中文）
- 覆盖主窗口 + 6 个独立窗口（ImageDetail / MetadataEditor / PresetManager / FormatFilter / Progress）
- 语言文件 key **223 → 568**，两语言集合始终一致
- 新增结构锁 `_probe-i18n-scan.ps1`（两语言 key 一致性 + XAML 引用完整性），已纳入门禁套件

**🖥️ UI 可用性修复**
- 左侧面板 **276 → 300px**：内容可用宽 ~236 → ~260，消除多处「恰好用满」的临界裁剪
- 修复多处横向 StackPanel + 长文本导致的**静默裁剪**（改为折行 / 拆行）
- `NumericUpDown` 组件级修复：统一字号 13 与 `MinWidth=88`，微调按钮收窄至 18px，把宽度让给数值本身
- 右上角新增 **⚙ 设置菜单**：渲染后端（手动选 GPU）/ 主题 / 语言 / 缓存目录
- 新增 `AppSettings.RenderingMode`（auto / angle / vulkan / software），每种模式都保留软件渲染兜底

**🛡️ 稳定性**
- 新增**全局异常兜底**：订阅 Avalonia `Dispatcher` + `AppDomain` + `TaskScheduler`，
  把 28 处 `async void` 的未处理异常从「进程无声消失」降级为「记录 + 继续」，异常落盘至缓存目录 `crash.log`

**🎨 色彩矩阵正确性修复（2026-09-28）**

- **PQ 传递函数常数修正** — `m1` 由 `2615/16384` 修正为 `2610/16384`（SMPTE ST 2084 标准值）。
  两处实现（`TransferCurve` / `SimdPixelOps`）曾**同时写错**，导致互检式测试必然通过、长期未被发现
- **SMPTE 240M 传递函数修正** — 幂 `0.44 → 0.45`、编码域断点 `0.07611 → 0.0912`（与 zimg / libplacebo 一致）；
  该曲线在 H2 口径黑名单内只走进程内路径，此前无外部参照可校
- **H1 亮度域换算** — 新增编码侧 `EncodeScale`，与解码侧 `LinearScale` 构成完整换算链。修复：
  SDR→PQ **过曝 49×**、PQ↔HLG 亮度差 **10×**、无 tone mapping 的 PQ→SDR 整体 **暗 49×**
- **HLG 目标侧补上 BT.2100 逆向 OOTF** — 48-bit 主循环原先**整段缺失**，float 路径还存在「把归一值当 nits」的错位
- **nits 语义按曲线定义兜底** — PQ/HLG 的「1.0 = 10000 / 1000 nits」不再依赖调用方是否记得传参
- 实测效果：PQ(BT.2020)→sRGB 与 zimg 对拍 **PSNR 7.26dB → 23.20dB**，平均 ΔE **46.5 → 3.30**

**🖼️ GainMap（Ultra HDR / ISO 21496-1）修复**

- **XMP 元数据读取限定增益图命名空间** — 此前按标签后缀匹配，`EXIF:Gamma`（相机 JPEG 常见，典型值 2.2）
  会被误当成增益图 gamma，导致**旧版 Ultra HDR 文件整图增益错乱**
- **底图 SOS 定位失败不再静默产出「假 Ultra HDR」** — 改为宣告失败，不产出自称 Ultra HDR 的普通 JPEG
- **JPEG 段扫描正确处理无长度字段标记**（TEM / RSTn / 填充字节），此前会跳位错乱而找不到 SOS
- **解码健壮性** — 尺寸运算改 `long` 并设单帧像素上限（防整数溢出绕过长度自检）、
  采样索引改按**解码缓冲区**钳位（此前只钳 x1/y1，x0/y0 漏钳）
- **峰值亮度改用 `alternateHdrHeadroom`** — 第三方文件（Adobe 等）不再因 `gainMapMax` 更大而高估峰值

**🧪 测试门禁加固**

- **PQ 绝对锚点**改为 zimg 实测码值（`0.580694` / `0.751812`），容差由 `1/1/3` 收紧到 `0.5/0.5/1.0`，
  使锚点真正能抓住常数级错误（旧锚点码值精度不足 + 容差过松，正是 m1 错误长期存活的原因）
- **修正两处探针绕过生产换算的构造点**（`xcheck` / `hlgwire`）— 此前它们手搓 spec，测的不是真实引擎路径，
  其中一个还把真实缺陷误定性为「已知非缺陷的口径分歧」而永久豁免
- 补充记录：zimg 的 `smpte240m` 与 `bt709` 输出**逐位相同**（均为纯 γ2.4），本仓按 SMPTE 240M-1995 定义式实现，
  与 zimg 形态不同构 —— 已写入代码注释，防止被误「对齐」成 zimg

**✅ 验证**
- 全层网格 **full 360 格全绿**（`PASS=24 FAIL=0`）；`color-strategy` 78/0、`decision-delivery` 84/0、
  `color-wiring` 105/0、`gamut-map` 36/0、`prophoto-faithful` 27/0、`gainmap-managed` 22/0、
  `gainmap-engine` 138/0、`gainmap-isobmff` 52/0
- 探针 `contract` 136/0、`selftest` 36/0、`curve` 44/0、`hlgwire` 3/3；编译 0 警告 0 错误

### v1.5.6 (2026-09-06) — 完整实机测试 + 格式色彩能力约束 + HDR 全链路修复

**🔧 HDR 全链路修复（10 个根因修复）**
- **HDR→TIFF sRGB tonemap 修复** — RGB 原生格式不再跳过 tonemap，TIFF/PNG 16bit HDR→SDR 正确转换
- **iccgen HDR 兼容** — 高位深 YUV 输入前强制 `format=rgb48le`（规避 iccgen 上游 "Not yet implemented" 限制）
- **AVIF 奇数尺寸修复** — zscale 前 `scale=ceil(iw/2)*2:ceil(ih/2)*2` 避免 libzimg 1027 错误
- **GIF→静态目标首帧** — `insertFramesV1` 标记 + `-frames:v 1` 取首帧
- **静态 AVIF→WebP/GIF 两步法** — `IsAnimatedAvifAsync()` 探测动画 AVIF，静态失败回退普通 ffmpeg
- **`-vsync 0` → `-fps_mode passthrough`** — 新版 ffmpeg 兼容
- **RAW 预处理高级色彩注入** — dngtool 线性 TIFF 设置 `UseAdvancedColorParameters=true` + `ColorPrimaries=bt709` + `ColorTrc=linear`
- **DNG HDR→各格式全通** — jpg/png/webp/avif/tiff/jxl 全部通过，色彩元数据正确传递（`rgb48le,linear,bt709`）
- **AnimationLoop 默认值 0→-1** — 防止静态图被当动画处理

**📂 目录结构规范化**
- 根目录散落临时目录清理（`temp_e2e/`、`temp_headless_test/`、`output/`），统一规范到 `tests/output/`
- 一次性诊断脚本（32 个）归档到 `tests/scripts/_archived_diag/`（git 忽略）
- 从 git 索引移除被误跟踪的 40+ 外部工具二进制（`tests/GainMapTestHost/PLAN/`）
- 移除嵌套 git 仓库 gitlink（`tools/src/aom` 等 4 个）
- `.gitignore` 全面重写，覆盖所有二进制/第三方源码/临时产物路径

### v1.5.5 (2026-08-30) — 原生 JXL 转 PNG 管道传输修复

**🐛 管道传输修复（实测驱动）**
- **JXL→PNG/WebP/AVIF/TIFF/GIF 管道 PAM 识别失败** — `djxl` 解码含 alpha 通道的 JXL 时输出 PAM 流，但 ffmpeg 使用 `-f image2pipe` 无法识别 PAM（`Stream #0:0: Video: none, none` / `Could not find codec parameters`）。按流类型改用专用 demuxer：PAM→`pam_pipe`、PPM→`ppm_pipe`，两者均实测 exit=0 正常产出
- **覆盖全部走 `PipeDjxlToFfmpegAsync` 的转换路径** — 无 alpha（PPM）不受影响，含 alpha 的 JXL 全链路修复

**✅ 验证**
- 项目编译通过（0 错误）
- 端到端实测：含 alpha 的 JXL `djxl --output_format=pam` → `ffmpeg -f pam_pipe -i -` → PNG，exit=0，图像正确产出

### v1.5.4 (2026-08-16) — 色彩管线全面修复 + UI 选项生效性审查 + 布局重构

**🎨 色彩管线修复（实测驱动）**
- **色度子采样全位深生效** — 修复 10/12/16-bit 输出恒为 4:2:0 的 Bug（`MapPixFmt` 高位深分支忽略 Chroma），4:4:4/4:2:2 在全部位深下正确映射（libaom 实测 `yuv444p10le`/`yuv422p10le`）
- **PC 范围 TIFF → TV 范围 AVIF 修复** — 输出侧补 `-color_range` 声明，RGB 全范围输入不再被 limited 矩阵压缩（实测 Y 域：pc=10-239 / tv=26-220）
- **TV/PC 范围自动跟随输入** — pix_fmt 推断（RGB/yuvj→pc，yuv limited→tv），输入 TV 范围图片 → 输出 TV，数据零拉伸（PSNR 53dB）
- **AVIF 位深按编码器 clamp** — libaom/NVENC→12-bit，SVT/QSV/AMF→10-bit（防 16-bit 输入编码失败）
- **UI 按编码器过滤选项** — `UpdateChromaBitDepthOptions()`：SVT/QSV/AMF/WebP 仅显示 auto/4:2:0；libaom/NVENC 显示 8/10/12 位深
- **ColorRange 控件按格式隐藏** — 不支持的格式（JPEG/WebP/PNG 等）整组隐藏而非仅禁用，切换格式自动复位 auto

**🔧 选项生效性全面修复（预览与入队行为一致）**
- 新增 TIFF DPI 控件（`TiffDpiBox`，实测 300dpi 正确写入）
- 简洁模式预设补齐字段（cjpegli 后端/PSNR、cjxl 系列、JxlEffort/Modular、WebpCompressionLevel、TiffDpi）
- WebP 无损压缩级别仅无损模式显示

**🎯 默认值与内置预设重做**
- 模型默认值统一：`Chroma=auto`、`StripExifGps=true`、`CjpegliChromaSubsampling=auto`、`CjpegliProgressiveId=2`（渐进实测压缩率更高：体积小 5-38%）
- WebP/TIFF 下拉默认对齐高级面板关闭时的值（picture/lzw）
- 29 个内置预设全面重做：GIF 预设格式修正（原为 JPEG）、SVT/QSV/WebP 色度 444→420（实测仅支持 420）、JXL 色度→auto、极限压缩启用 cjpegli 渐进、PNG 快速存档补无损

**🖥 顶部布局重构**
- FFmpeg 目录 + 输出目录合并一行（Grid 弹性列宽，输出目录 1.4 倍权重）
- 修复 Grid 列错位（输出目录标签曾落入 12px 固定列导致残字/塌陷）
- 窗口默认 1100×850（适配 150% DPI 1080p 屏），右侧区域行比例 2:1:2（队列优先）

**🌍 其他**
- cjpegli 参数适配当前 libjxl 版本（`--fixed_code`/`--noadaptive_quantization`，移除已失效的 `--jpeg_encoder`/`--psnr`）
- 窗口标题版本号自动同步程序集版本（`NormalizeAppTitleVersion`），发版不再遗漏
- UI 测试方法论：UIA 边界框 + 视觉模型（SenseNova 6.8 Flash Lite）交叉验证

### v1.5.3 (2026-08-15) — 原生 PSNR + 目标域质量分析 + SIMD 加速

**📊 .NET 原生 PSNR（替代 ffmpeg psnr filter）**
- 新增 `PsnrCalculator.cs` — 纯 .NET 实现，**无外部依赖**，60MP 大图 **20× 加速**（359ms→17.6ms）
- 自动 dispatch: AVX512BW → AVX2 → SSE2 → 标量回退
- 位深归一化: `MaxValue = (1 << bitsPerSample) - 1`，8/10/16-bit 统一计算
- 多帧语义: `average` = 全局 MSE 聚合，`min`/`max` = 逐帧极值（与 ffmpeg 一致）

**🎯 目标域质量分析（彻底解决跨格式域偏差）**
- RGB 系格式（PNG/TIFF/JXL/APNG/GIF/BMP）→ RGB 域（rgb24/rgb48le）
- YUV 系格式（JPEG/WebP/AVIF/HEIC/JXR）→ YUV 域（yuv444p/yuv444p16le）
- 与 ffmpeg psnr filter **逐位一致（0.0000dB，D37 断言）**
- `scale=out_range=pc` 统一 full range（消除 limited vs full 值域错乱）
- UI 质量分析结果标注 `(RGB)` / `(YUV)` 域

**⚡ GainMap SIMD 加速**
- 新增 `SimdPixelOps.cs` — 4 个热点，实测数据驱动取舍
- `FloatToSrgb8` AVX2 **2.5×**（0.15→0.06ms，1024 项 LUT + gather 插值）
- 已集成 GainMapEncoder（ReinhardToSdr / WriteBgra8PngAsync 批量转换 / ComputeGainMap 灰度）
- 已集成 GainMapDecoder（SrgbToLinearRgba）

### v1.5.2 (2026-08-13) — 管线修复与元数据增强

**🔧 管线修复**
- **修复 ffprobe 色彩字段错位解析** — `-show_entries` 逗号分隔仅最后一个字段生效，实际输出 `pix_fmt,color_space,color_primaries,color_transfer`（无 bits_per_raw_sample），此前 primaries/transfer 一直被交换。修正解析 + 位深解析器重写（yuvj420p→8、yuv420p10le→10、rgb48le→16 全覆盖）
- **修复 RAW 预处理临时目录泄漏** — `raw_{GUID}` 目录任务完成后统一清理
- **JXR 命令显示过期** — 显示实际 PPM/PAM 管道命令

**🖼 编码增强**
- **JPEG XL 无损重封装开关** — 高级选项可独立关闭（关闭时显式 `--lossless_jpeg=0` 重新编码）
- **光子噪声自动 ISO** — 可选从每张输入照片 EXIF 自动读取 ISO（每图独立，比固定值准确）

**🏗 平台**
- **NativeAOT 打包** — 2 版本发布（单文件版 / 完整版含 PLAN），全部 NativeAOT 编译，无需 .NET Runtime
- **PLAN ffmpeg 目录模糊匹配** — 目录名包含 "ffmpeg-full" 即自动识别（如 ffmpeg-full-2026.7.24）

---

### v1.5.1 (2026-07-27) — 色彩管线修复

**🎨 色彩管理修复**
- **修复 SDR→HDR 像素未转换** — 16-bit TIFF 等无元数据输入手动指定 BT.2020 PQ/HLG 时，输出仅有 HDR 标签但像素未做电光转换（画面偏暗）。`BuildColorArgsSplit` 简化模式改为返回实际输入色彩，新增通用目标色域 zscale 转换逻辑
- **修复 JXL→PNG 卡死** — 管道模式下 `inputPath="-"` 被传给 ffprobe 探测函数导致无限阻塞。三个探测函数添加管道守卫
- **HDR→SDR 排除判断** — 简化模式 zscale 转换排除 HDR→SDR 场景（交给 tonemap 处理，避免高光裁剪）

---

### v1.5.0 (2026-07-23) — 正式版

**🎛 编码器与质量**
- **AVIF 深度优化** — libaom 新增 aq-mode（自适应量化，Variance/Complexity）、CDEF 方向增强滤波、帧内块复制(intrabc)、胶片颗粒合成(denoise 0-50)；NVENC 新增 aq-strength(0-15)+空间自适应量化(spatial-aq)；QSV/VAAPI 新增低功耗模式(low_power)
- **硬件编码器 7 档精细预设** — NVENC(p1~p7) / QSV(veryfast~veryslow) / VAAPI(compression_level 1~7) 独立面板，默认最高质量(p7/veryslow/7)
- **29 个内置预设** — 覆盖 AOM×4 / SVT×4 / NVENC×3 / QSV×3 / JPEG LI×3 / JXL×4 / WebP×3 / PNG×2 / TIFF / Ultra HDR / GIF，全部使用最新参数
- **智能默认值** — 不勾选"高级编码选项"即可获得优化输出，所有参数内置高质量默认（cpu-used=4, still-picture=1, aq-mode=variance, huffman=optimal...）
- **PNG 增强** — 6 种预测模式带中文场景说明 + DPI 打印分辨率（默认不设，纯可选）

**🌐 界面与体验**
- **双语界面** — 中文 / English 一键切换，右上角按钮即时生效，JSON 资源文件
- **简洁模式** — 同窗口极简覆盖视图，拖放文件直接入队，自动编码开关
- **GPU UI 加速** — Windows ANGLE/D3D11 渲染，GPU/CPU 按钮可切换
- **便携化部署** — 配置与预设存于 exe 同目录，零 %AppData% 依赖，拷贝即用

**🎨 色彩管理**
- **ICC 系统重写** — 4 种新模式：①无ICC(CICP) ②携带ICC ③烘焙+嵌入 ④仅烘焙；zscale 像素烘焙；iccgen 自动生成标准 ICC
- **CICP 始终启用** — H.273 色彩标记在所有模式生效；非 CICP 格式非 sRGB 时自动嵌入 ICC
- **HDR→SDR 自动降级** — 输出格式不支持 HDR 时自动色调映射；双重转换冲突检测与锁定
- **色彩空间快速选择** — sRGB / BT.709 / BT.2020 PQ / BT.2020 HLG；BT.2020 自动 ≥10-bit

**🔧 工具与架构**
- **工具面板重构** — 3 列水平布局（JXL库|exiftool|artifacts）；紧凑状态栏后台检测完自动显示 ✅/❌；PLAN 便携包自动识别
- **GPU 编码器检测** — 启动时自动检测 NVENC/QSV/AMF 可用性并逐编码器运行时验证
- **检测模块重写** — 全异步后台管线，真实超时保护，增量日志
- **打包优化** — 精简调试符号 ~100MB；Resources/Locales/ 多语言资源自动包含

**🐛 修复与优化**
- 消除重复 settings.json I/O；7 个外部工具并行检测
- Windows 搜索拖放路径正确解析
- 输出类型 WinExe，无 CMD 窗口

<details>
<summary>v1.5.0 Beta 版本详情</summary>

### v1.5.0 BETA3 (2026-07-15)
- **色彩空间重构** — 简化选择器：sRGB / BT.709 / BT.2020 PQ / BT.2020 HLG；移除 BT.601；选择后自动填充 primaries/trc/matrix；BT.2020 根据源位深自动 ≥10-bit
- **ICC 系统重写** — 4 种新模式（无ICC / 携带ICC / 烘焙+嵌入 / 仅烘焙）；zscale 像素烘焙；iccgen 自动生成标准 ICC；烘焙目标跟随色彩空间选择
- **CICP 始终启用** — H.273 标记在所有模式生效；非 CICP 格式非 sRGB 时自动嵌入 ICC
- **HDR→SDR 降级** — 输出格式不支持 HDR 时自动 zscale+tonemap；位深比较警告
- **双重转换锁定** — ICC 烘焙时锁定手动色彩参数，6 种冲突场景检测
- **打包优化** — 移除原生 .pdb 调试符号 ~100MB；55 项管线测试矩阵全部通过

### v1.5.0 BETA2 (2026-07-14)
- **简洁模式** — 同窗口极简覆盖视图；拖放直接入队；自动编码开关；预设同步主界面
- **GPU 编码器检测** — 启动时自动检测 QSV/NVENC/AMF；逐编码器运行时验证；✅⚡⚠️❌ 彩色状态提示
- **GPU UI 加速** — ANGLE/D3D11 渲染（Windows）；GPU/CPU 一键切换；`--no-gpu` 命令行回退
- **启动优化** — 消除重复 I/O；7 个外部工具 Task.WhenAll 并行检测；GPU 验证延迟到后台
- **便携化部署** — 配置/预设存于 exe 同目录 `presets/`；零 %AppData% 依赖；拷贝即用

### v1.5.0 BETA (2026-07-14)
- **ICC 色彩管理 v1** — 外部 .icc/.icm 加载；exiftool/iccgen 嵌入；zscale 像素烘焙；sRGB~Rec.2100 完整色彩空间映射
- **预设系统 v2.0** — 24 内置预设 + 二级管理窗口 + 用户 JSON 预设 CRUD
- **工具面板重构** — 3 列水平布局；紧凑状态栏后台检测完自动显示 ✅/❌；PLAN 便携包自动识别
- **检测模块重写** — 全异步 3 阶段后台管线；每步 8s 超时；增量 Dispatcher 日志
- **AVIF 编码器面板** — AOM/SVT/NVENC/QSV/AMF 各自独立选项，编码器切换时动态切换面板
- **动图与 RAW** — 视频转动图时长限制；dngtool RAW 解码自动检测；扩展 RAW 格式支持

</details>

<details>
<summary>v1.4.5 及更早</summary>

- **v1.4.5** — Windows 搜索结果拖放路径正确解析（Shell 命名空间）；JXL 无损 JPEG 重封装（直接复制 DCT 系数，5-10× 速度）；Linux ARM 迁移技术分析完成
- **v1.3.0** — UI 卡片化重构（圆角阴影卡片容器）；深色/浅色双主题一键切换；GridSplitter 弹性三区布局；ExifTool 隐私清理（GPS/时间/相机/EXIF/XMP 选择性删除）
- **v1.2.0** — 批量队列引擎（ConcurrentQueue + 并发 1-128 + 失败重试）；元数据编辑器（~90 字段 9 大分类 + 双击编辑）；格式筛选窗口；预设系统 v1.0；CPU SIMD 指令集自动检测
- **v1.0.0** — 初始发布：JPEG/PNG/WebP/AVIF/JXL/TIFF 多格式编码；质量滑块；外部编码器集成（ffmpeg/cjxl/cjpegli）；命令行构建与预览

</details>
