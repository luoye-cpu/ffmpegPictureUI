# 更新日志

本项目所有值得注意的变更都记录在此文件。

> ℹ️ 各版本条目中记录的「探针 mode 数 / 门禁脚本数」为**当次发布时的基线**。
> 这些数量随开发持续增长，**实时权威值**以 `tests/scripts/_run-step3-gates.ps1` 中的
> `$Probes` 与 `$expectedScriptCount` 为准。

[English](CHANGELOG.en.md)

---

## 📝 更新日志

### v1.6.0-beta7 (2026-10-09) — JPEG LI「选了渐进式却拿到 baseline」四因分层 + 冲突出声 + 每图探测链合并

> **谱系**：`src/` 下 **13 个产品文件**改动（+189/−17 行）；测试侧 **4 个文件**（+124/−15 行，
> UiTestHost 与 `uicoreGroup.cs` 各新增 4 条冲突提示断言 ⇒ UI 并集断言数 **409 → 413**）；
> 受管门禁**条数不变（73 条）**，但 `_lib-matrix.ps1` 里 **7 处行锚**随源码位移做了修正。
> 全部改动都带**产物字节级**判据（直接读 JPEG 的 SOF 标记：SOF0=baseline / SOF1=extended-sequential /
> SOF2=progressive）。整轮（`TMP/TEMP` 重定向臂）**73 条受管脚本逐条 `exit=0`**，26 个探针 mode 里只剩
> `probe:runner` 一条红（本机沙箱对进程树的限制，与 beta6 定性同一条）。⚠ 默认 `%TEMP%` 臂会**成片假红**
> —— 本会话沙箱禁止工具进程写临时目录 ⇒ `exit -13`；负/正/刷臂三侧对照与处方记在
> `docs/JPEGLI_GAINMAP_PANEL_PLAN_2026-10-09.md §14`（同一份读数顺带**撤销**了一条被登记成"既有红"的
> `I3 ProPhoto 保真`：重定向臂下 UI 宿主 **413 PASS / 0 FAIL**，与旧读数 412/1 总数守恒）。

**🔴 选「渐进式」却输出非渐进式 —— 四个互相独立的原因（按影响排序 R0 > R1 > R2 > R3）**

- **R0 后端推断空档（静默、影响最大）**：`InferDefaultBackend` 没有 jpg/jpeg/jpegli 分支 ⇒ 用户**没显式**
  `-e Cjpegli` 时任务实际走 ffmpeg 的 `mjpeg`，而 mjpeg **根本没有渐进式能力**。面板的选择被
  **完整接收、无声丢弃**。CLI 侧有 `EncoderBackendExplicit` 守卫所以不受影响 —— 这也解释了为什么
  先前一次带 `-e Cjpegli` 的复测**没能重现**它（那次测的是回退出口，不是 GUI 默认路径）。
- **R1** `--animation-fps` 一类的动图参数 ⇒ 明确回落 mjpeg（**有播报**，不属静默失效）。
- **R2 固定码表 + 渐进式的静默降级**：`--fixed_code` 要求 `-p 0`，而界面上「优化哈夫曼编码」关掉
  就等价于 `--fixed_code` ⇒ 此前代码**直接把 `-p 2` 改成 `-p 0` 且不留一字**。
- **R3** 增益图采集器把隐藏的 `JpegProgressiveId` 钉为 0 ⇒ 面板值进不去。⚠ 两个出口都**如实消费**了
  那个 0（实测三种面板设置下增益图产物 md5 **逐字节相同**）⇒ 这是采集层的口径，不是出口缺陷。
- **本轮落地的修法**：
  ① `CjpegliProgressiveId` 默认由 `2` 改为 `-1`（=「用户未表达」）⇒ 消掉一条**恒响的告警**
  （恒响＝没有告警）；
  ② R2 的降级**出声**：按 `CjxlService` 的 `log:` 形参约定走**单一通道**，`QueueProcessor` 与
  `JxlPipelineService` 两条管道路由同步接入 ⇒ headless 与 GUI 都看得到；
  ③ 新增第三条告警：`--cjpegli-progressive` 遇上非 JPEG LI 后端时点名「本任务由 ffmpeg mjpeg 编码，
  它没有渐进式能力，产物是 baseline」，并直接给解法（`-e Cjpegli`）。
- ⚠ **R0 本身本轮未动**：补齐 jpg 的默认后端推断属色彩管线 P7a-3「单一裁决点」的范围，
  已登记在 `docs/JPEGLI_GAINMAP_PANEL_PLAN_2026-10-09.md` 的账本里。

**🟠 GUI：渐进式与「优化哈夫曼编码」同时勾选时，就地给出冲突提示**

- `JpegliProgressiveCombo` 下方新增 `JpegliProgConflictHint`（默认隐藏）。条件 = 高级面板在位
  **且** 哈夫曼优化未勾 **且** 渐进式不是「顺序」档；三处刷新（`RegenerateCommand` 头部、
  面板可见性尾部、`UseAdvancedCodec` 切换）。
- ⚠ 首版走 `Console.WriteLine` + 中继 ⇒ 实测每个任务多打 **3 行重复 stdout**（会破坏依赖 stdout 文本的
  门禁 grep）。改回 `log:` 单通道后 **stdout 行数与日志条数一致**。

**🟠 「基线/标准(兼容性)」这个档名是假承诺 ⇒ 下拉只写身份，解释移到灰字**

- 实测 `-p 0` 在**高熵内容**上产出的是 **SOF1**（extended-sequential），不是它承诺的 baseline。
  机制在上游：jpegli 的 `is_baseline` 由**实际发出的霍夫曼槽位**决定（内容相关），
  而 libjpeg-turbo 只看声明的 `tbl_no` —— 所以同一档位两种后端可以给出不同 SOF。
- **修法**：`jpeg.progressive.baseline` → 「顺序 (sequential)」/「Sequential (non-progressive)」；
  SOF0/SOF1 的边界说明移进新 tooltip `tip.jpeg.progressive.mode`。
- ⚠ **待补**：SOF 三态的**门禁断言**本轮没进清单（新增断言会作废正在跑的整轮读数），已记账。

**🟢 性能：每图一次独立的 exiftool 探测并进既有 argv（省 0.170 s/图）**

- 探测链里 ICC 提取原本是**第二次**独立 exiftool 调用，外面还套着一个恒真条件
  `!string.IsNullOrWhiteSpace(exifColorSpace) || true`（死代码，恰好掩盖了「其实每次都跑」）。
- **修法**：把 `-b -icc_profile` 合进同一条 argv，新增 `IccProfileService.ExtractIccFromBase64`
  直接吃返回值（长度下界 + `IsValidIccProfile` 双闸，坏值一律当「无 ICC」而不是猜）。
- **实测**：`--dry-run` 单图墙钟 **0.797 → 0.644 s**；ICC 读回**逐字节相同**；受影响门禁 A/B 计数一致。
- 完整口径与其余待做项（P2 后置链合并 0.34 s/图、P4 引擎路 `-threads` 本机无判据）见
  `docs/PIPELINE_PERF_ANALYSIS_2026-10-09.md`。

**📌 门禁基线（本次发布时）**：受管脚本 **73 条**；`ServiceProbe` 探针默认清单 **26 个 mode**。
⚠ 实时权威值以 `_run-step3-gates.ps1` 的 `$Probes` 与 `$expectedScriptCount` 为准。

---

### v1.6.0-beta6 (2026-10-07) — 4 个静默缺陷修复（含「给了参数却被否认给了」）+ 高级编码选项全量核查 + 测试二分细化（门禁 72→73）

> **谱系**：`src/` 下 **11 个产品文件**改动（+346/−49 行）；测试侧一次**二级细分**
> （5 个巨型组文件 → 31 个「一个 mode 一个文件」）；新增门禁 **第 73 条**。
> 所有修复均有**产物级 / 字节级**判据背书（全量 73 条门禁：仅 `probe:runner` 因
> **本机沙箱限制**失败，已完整定性为**非产品缺陷**）。

**🔴 `manual` 色彩策略下「只给 primaries」被误拒，且报错文案与事实不符（含**静默不交付**）**

- 用户可见症状（`--color-strategy manual --color-primaries bt2020`）：命令被拒、零产物，
  且提示「**未提供任何目标色彩描述**」—— 而用户**明明给了** primaries。
- 根因**三层**，逐层实测：
  ① `ColorIntentFactory` 的判据在 `HasExplicitColorTarget`（CP **或** CT 非空）成立时**只**看 `ColorTrc`
  ⇒ 只给 primaries 时 `Target=null` ⇒ 规划层立即 `Reject`；
  ② `FfmpegCommandBuilder.Decision` 的高级分支同样**只**看 `ColorTrc`
  ⇒ 即便放行，也只是"命令被接受"而**产物标注仍是源的 P3**（`smpte432`）——
  **静默不交付**，比误拒更隐蔽；
  ③ 放宽判据后，描述符要求**两半都能命名** ⇒ 只给一半会报 `目标语义 bt2020/ 无法命名`。
- **修法**：判据改为「CP 或 CT 任一在位即算显式目标」+ 缺失的一半按**源语义**补齐；
  两处判据同源。`ManualIntent` 的装配同时接通（此前 `ManualIntent.Create` **全仓零调用点**，
  属"有声明、无实现"）。
- **实测**：`--color-primaries bt2020` ⇒ 产物 `color_primaries=bt2020`（修前 rc=1 零产物）；
  `bt709`/`smpte432` 各自兑现；`--color-trc linear` ⇒ `jxlinfo` 报 *Linear transfer function*。
  ⇒ **不止放行，而是真兑现**。

**🔴 `--dry-run` 预览与真实执行不同源 ⇒ 预览**少报真实编码器**（会导致审计假阴性）**

- 实测两处不一致：① `-f jxr -e Jxr` 预览只写 `ffmpeg`，真实核心编码器是 **JxrEncApp**；
  ② `-f dng --dng-bit-depth 8` 预览**漏 `-4`**、漏 `-jxlq`，且用了**已废弃的 `-q`**
  （`-q` 是 demosaic 质量，被 dngtool 当成 JXL 质量 3 ⇒ **严重有损**）。
- **影响**：任何以 dry-run 为准的自动化审计都会得到**假阴性**
  —— 实测若只信 dry-run，会误报两条**不存在**的缺陷（「dng-bit-depth 失效」「JXR 后端没接上」）。
- **修法 = 结构级单一真源**（不是补两行参数）：抽出 `RawService.BuildEncodeToDngArguments`
  （预览+执行 2→1 处）与 `JxrService.ResolveQuality`（两个真实出口+预览 3→1 处）
  ⇒ **结构上不可能再漂移**。

**🟠 14 条「高级编码选项被忽略/降级」的告警在 **GUI 里完全不可见****

- 产品是 `WinExe`（GUI 无控制台），而这些告警全走 `Console.WriteLine`
  ⇒ headless 用户看得到、**GUI 用户看不到**（属"门禁看得见、用户看不见"的盲区）。
- **修法**：保留 Console 输出（headless 与门禁依赖其文本，**逐字未变**），
  另加 `AsyncLocal<Action<string>?> WarnSink` + `EmitWarn()` 转发到 GUI 面板
  `EncoderOptionWarning`。14 处调用点全部改走 `EmitWarn`，**告警文案一字未改**。
- ⚠ 选 `AsyncLocal` 而非普通 static：队列项**多线程并发**，ambient 值会把 A 项的告警串进 B 项。
- ⚠ **已知边界**：转换**执行期**（queue-run）的告警仍只在 headless stdout + 详情窗口
  —— 接到 GUI 面板会让 `item.Log` 经 `FlushItemLog` **回显到 stdout ⇒ 告警翻倍**、破坏门禁 grep
  （实测后主动回退，`QueueProcessor` 最终 0 diff）。

**🟠 两个 API 未过色彩 token 规范化器 ⇒ 传用户友好名直接 `exit -22`、零产物**

- `RawColorPipeline.WriteRgb48ToPngAsync` 与 `GainMapEncoder.EncodeAvifFileAsync` 直接把
  `primaries/trc/matrix` 拼进命令行，**没过** `ColorSpaceRegistry` 的规范化器
  —— 而本仓自己在注释里逐字写着「不规范化 ⇒ ffmpeg 报 `Unable to parse` ⇒ **退出码 -22、零产物**」，
  且 `FfmpegOptions` 的 setter **确实**调了规范化器 ⇒ 属**口径不一致**。
- **实测**：`-color_primaries srgb` ⇒ `[Eval] Undefined constant … in 'srgb'`、零产物；
  过规范化器（`srgb→bt709`）后 ⇒ rc=0、产物正常。修复后 `xcheck` 的 SDR 格从**硬崩**变 **73.19 dB 全绿**。

**🚦 门禁 72 → 73 条：新增 `verify-dryrun-parity.ps1`**

- 锁「`--dry-run` 预览 ↔ 真实执行」的**同源性**：判据 **A 多报 / B 少报（只认编码器类工具）/
  C 实参级逐字同源** + 负控 + 各臂「真实侧非空」反控；4 臂（jxr/png/jpg/dng）。实测 **16/0**、~8 s。
- ⚠ **接线时发现并封住门禁自身的假阴性**：初版头注释声称锁**两处**缺陷，但**无 dng 臂**
  ⇒ 判据②**无人锁**。变异实测（dng 预览改成恒 16 位）当时仍 `pass=10 fail=0` **全绿**；
  补 dng 臂 + 判据 C 后同一变异 ⇒ `pass=15 fail=1` **转红**。
- ⚠ 边界（如实登记）：`JxrEncApp` **无实参级锁**（真实侧中转是运行时临时名，逐字比会假红）、
  gif/webp/apng/tiff **暂无臂**。依赖仓内夹具 `tests/fixtures/raw_src_8bit.dng`，缺件 ⇒ SKIP 点名（fail-closed）。

**🧪 测试工程**二级细分**：一个 mode 一个文件**

- 动机（用户原话）：「测试应该是一个个项目，这样可以单独测试部分项目避免一点更改就需要跑一大堆测试」。
- 5 个巨型组文件（**2437 / 1274 / 1076 / 728 / 693 行**）→
  **5 个 53~136 行的共享底座 + 31 个 `Modes/<mode>.cs`**；类改 `partial`
  ⇒ **`Program.cs` 与全部调用点一字未动**。
- **验收三道**：① **逐字性** —— `Modes/*.cs` 的每一个代码行都能在改前文件里找到（零改写）；
  ② **读数零漂移** —— `core 332/1`、`color 126/0`、`engine 82/1`、`geometry 75/1`、`diagnostics 82/6` 逐字一致；
  ③ **结构锁全绿**。拆分后单 mode 可独立跑（实测 113–288 ms/个）。

**🔧 其它**

- `_lib-matrix.ps1` 的 A30 取证锚点因源码插入 31 行而漂移 ⇒ **重 pin**（`821→852`、`773→804`），
  并以变异验牙证明锁仍有牙（改回旧行号 ⇒ `pass=7 fail=1` 转红）。
- `verify-ui-split-verbatim.ps1` 引入**已登记偏离组**（恰 1 项 `GroupJ_`），
  其余 16 组仍逐字比较；变异验牙通过（改 `GroupL_` 真断言 ⇒ 立刻红）。
- **订正**两处此前报告的结论：NVENC `-rc` 的"三档产物差异"在本机**不可复核**
  （`av1_nvenc` 报 `No capable devices found`，且证据里无原始日志）⇒ 由 P0 降 P1；
  avifenc `--target-size` 实测呈**阶梯且不命中**目标（2000→3570、10000→7930、60000→7930）⇒ 由 P0 降 P2。

**🔧 构建卫生**

- 恢复 **0 警告 / 0 错误** 构建：L-1 修复改动可空流分析状态后，`ColorIntentFactory` 的
  `src.Curve` 报出 `CS8602`。根因是**作者侧**两处：① 早退保证 `src` 非空、但分析器对
  「跨多个 if 重赋值 + 早退」不收敛；② 同一函数里我写的 `src?.PrimariesToken`（空条件运算符）
  会把 `src` **重新放宽为可能为 null**。修法 = 去掉不必要的 `?.` + 用一次 `!`（null-forgiving）
  把已知不变量写给编译器 —— **运行时不改行为**。
  ⚠ 刻意**不**新增 `if (src == null) { … }` 判空：那会重复一条既有中文文案，而
  `_probe-cjk-hardcode-scan` 的「C# UI 面」桶**按行计数、余量为 0** ⇒ 会顶成 834 > 833（实测踩过）。

**🐛 修一处「断言名随环境抖动」导致的门禁周期性假红**

- `verify-ui-split-union.ps1` 硬要求「旧宿主断言名并集 == 409 且 extra == 0」。
  上一版把 J1z 写成 `if (hw == null) Skip(...) else Check(...)` ⇒ **断言名随环境出现/消失**
  ⇒ 并集计数在 **409 / 408 间抖动**（实测：早先 409，之后连跑 5/5 次都走 SKIP ⇒ 408）⇒ 周期性假红。
- 根因是**作者侧设计缺陷**：同组 `L1a`/`L1b` 之所以稳定，是因为它们**无条件** `Check(...)`。
- 修法 = 两条都**无条件**发出（与 `L1a`/`L1b` 同形）：`Skip` 仍只在前置不满足时点名"未验证"，
  `Check` 无条件发、判据设计成"前置不满足时判通过"（**没有硬件项就不存在图标污染**，这本身是
  符合预期的事实，不是"没测"）⇒ 名字恒定、计数稳定；**既不谎报红、也不静默消失**。
- 实测：`ui-sweep` 连跑 3 次均 **139/0 skip=1** 且断言名每次都在；union **11/0（new=409 old=409）**。

**📌 门禁基线（本次发布时）**：受管脚本 **73 条**；`ServiceProbe` 探针默认清单 **26 个 mode**。
⚠ 实时权威值以 `_run-step3-gates.ps1` 的 `$Probes` 与 `$expectedScriptCount` 为准。

---

### v1.6.0-beta5 (2026-10-05) — 10 个 P0 修复（含 2 个静默数据/隐私缺陷）+ 测试工程物理拆分（门禁 68→72）

> **谱系**：本条是**大量真行为变更** —— `src/` 下 11 个产品文件被改动（+547/−31 行），
> 且**测试与门禁结构**发生一次大改（三个测试宿主 → 17 个子系统工程）。所有修复均有产物级判据背书。

**🔴 隐私：JXL 默认输出曾**静默保留 GPS**（P0）**

- 根因（三层，逐条实测）：① `jxl` 是默认输出格式，`--jxl-lossless-jpeg`（`jbrd` 无损重封装）默认开启，
  而它**逐字复制源 JPEG 码流**（含 EXIF/GPS）；② 默认路径走 `CopyMetadataSafeAsync` 的安全复制，
  其剥离分支只覆盖 `png/apng`；③ 即便补上，**排除式 `--GPS:all` 在 JXL 上无效**
  （exiftool 报"1 image files updated"但 GPS 仍在），**只有删除式 `-GPS:all=` 有效**。
- **修法**：复制后补一次**显式删除**；剥离触发条件由"仅 `--strip-metadata`"扩到"任一剥离开关"。
- ⚠ **由此暴露的设计矛盾已显式化（不静默）**：jbrd 要保住"可逐字节还原原始 JPEG"就**不能**动容器元数据，
  而 GPS 正在那里 ⇒ **二者结构上不可兼得**。按「无损重封装是绝对默认」的裁定，
  **默认保住可逆性**并在**状态串（默认级别可见）**上点名"未剥除 EXIF/GPS"及两条替代路径；
  用户显式给 `--strip-gps`/`--strip-metadata` 时才**放弃重封装并真正剥除**。

**🔴 无损可逆性被自身步骤摧毁 + djxl 静默降级被谎报（P0）**

- 根因：正向 `cjxl` 产出带 `jbrd` 的 JXL 后，**元数据恢复步骤改写了容器** ⇒ `djxl` **拒绝无损重构**，
  转而**静默降级**为 `--pixels_to_jpeg` 像素重编码，**退出码仍为 0** ⇒ 状态写成"已完成 (djxl 重构 JPEG)"，
  **与事实相反**；后果是往返不再逐字节相同、色度 `yuvj420p → yuvj444p`。
- **修法**：真走 `jbrd` 时**跳过元数据恢复**；反向**捕获 djxl 的降级信号**并点名，不再声称"重构"。
- 实测：`verify-jbrd-e2e` **52/8 → 100/0**；变异验证（改回原样）⇒ 立刻 97/3 转红。

**🔴 8-bit 出口静默丢弃色调映射等四算子（P0，像素静默错误）**

- `TransformRgb48leToRgb8` 此前只读 `SrcCurve`+矩阵+`GamutMap`+`Clamp`，**从不读**
  `Tone`/`LinearScale`/`EncodeScale`/`SrcIsHlgScene` ⇒ `-f gif`/`-f jpg`/`--bit-depth 8`
  等**默认可达**路径上 HDR→SDR **不做色调映射**（高光截顶、约亮 57%），HLG 源不做 BT.2100 OOTF。
  实测：`--bit-depth 8` 下 `hable`/`none`/`reinhard` 三者产物**同一 SHA**，而日志仍打"激活=是"。
- **修法**：按 16-bit 兄弟实现的**同一顺序**补齐（HLG OOTF → LinearScale → ToneMap →
  矩阵 → GMO → EncodeScale → 逆向 OOTF → 钳位），并让表快路径在这些算子启用时回退解析式。
  验证：8-bit 三档 tonemap 均值 **73.8/91.6/92.6** ≈ 16-bit **74/92/92.9**（修前三者同值）。
- 顺带修：**显式 `--color-tone-map none` 被自动兜底静默升级为 Hable**（值与"未指定"同形）
  ⇒ 新增显式标志，让显式选择**优先于**自动兜底（`none` 现走既有的诚实拒绝，exit 91）。

**🔴 其余 P0（均实测复现后修）**

- **非法 CLI 取值导致进程崩溃**：`-q abc` 等原本退出 `-532462766`（CLR 未处理异常），
  而非契约的 `2`。修法：唯一入口接住 `ArgumentException` ⇒ `错误: …` + exit 2（**只捕参数错**，不吞真崩溃）。
- **重复 `-i` 静默丢弃前面的输入**：`-i a -i b -i a` 原本"找到 **1** 个"（逗号形式给 3）⇒ 改为**追加**。
- **输出目录在输入根之下 ⇒ 自我吞噬**：同一命令重跑会把上一轮产物当输入（找到 1→2→3，滚出 `pic_1/pic_2.png`）
  ⇒ 枚举时**跳过输出子树**。
- **工具缺失时启动停顿 135.8s**（像卡死）：递归扫描把 PATH 里每个目录都扫一遍（实测 67,558 个目录）
  ⇒ PATH 目录**不进递归面**（另有非递归解析 PATH 的路径），`Program Files` 只保留具名子目录，
  并把单根预算改为"剩余总预算"⇒ 实测 **135.8s → 9.2s**，8 个工具仍全部检出。
- **已有产物被静默覆盖**（不可逆）：新增 `--no-overwrite`（存在即**拒绝并点名**、计入失败、exit 1）；
  ⚠ 默认**保持覆盖**（既有语义）。
- **"绿在错误后端上"的假绿**：缺 `settings.json`（被 `.gitignore` 忽略，装的是本机绝对路径）时
  `JxlLibDir` 为空 ⇒ 编码器**静默退回 ffmpeg 的 mjpeg**；该组**仍报全绿、汇总数字不变**，
  只有断言里的实测值（2694B→2553B）暴露它。修法：`JxlLibDir`/`WindowsArtifactsDir`
  **从仓库根推导** ⇒ 新克隆者开箱即得同一编码器。

**🧪 测试工程物理拆分（用户指令：「一个部分一个测试组，不再改一点就跑整个项目」）**

- 三个巨型宿主（`ServiceProbe` 577 KB / `UiTestHost` 238 KB / `GainMapTestHost` 98 KB）
  拆成 **17 个独立工程** + 2 个共享底座（`Tests.Shared` / `UiHostShared`），每个工程**自带 Main**、
  可单独 `dotnet run`，支持 `--list` 与单 mode 运行，未知 mode **exit 2 fail-closed**。
- **读数守恒（主 Agent 独立复核）**：`UiTestHost` 409/409（missing=0 extra=0，连续 5 轮 + 交替顺序稳定）；
  `GainMapTestHost` **103 ✅ = 103 ✅ 逐行差异 0**；`ServiceProbe` 逐 mode 一致。
- ⚠ **三个旧宿主全部保留**作兼容层（**29 个门禁脚本**硬编码其 exe，且运行器 STALE 闸比对 4 个产物新鲜度）。
- 拆出的 4 条**「顺序运气」隐性依赖**已记账并显式化为**有名字的入口前置**（如
  `UiHost.ApplyToolPaths()` 必须**先于** `BootWindow()`）—— 这类依赖**不表现为红、而是少跑几条断言**，
  只看汇总数字发现不了。

**🚦 门禁 68 → 72 条**

- **第 69 条** `_probe-test-group-coverage.ps1`：组表（`TestGroups`）↔ 运行器 `$Probes` **覆盖一致性**。
  含第 ⑤ 条"每个声明的 mode 必须**真的进了分派链**"—— 立锁动机是审查实测的**假覆盖**：
  `engine` 组声明 4 个 mode 却只实现 1 个 ⇒ `engine` 35/0→**0/0**、`simdswitch` 28/0→**0/0**，
  共 **63 条断言静默消失**而门禁全绿。
- **第 70/71 条**：UiTestHost 拆分**逐字锁**（17 个组方法体逐行逐字，2991 行；19/0）与
  **并集锁**（旧宿主断言名集合 == 5 工程并集，双向差集为空；9/0）。
- **第 72 条** `verify-usability-e2e.ps1`：**用户可感知可用性**实机端到端矩阵
  （A 格式互转 8×6 + B 色彩目标兑现 + C 批量与目录 + D 动图帧数，81 格）。
  ⚠ 它**早已存在却不在清单** ⇒ 谁也不跑 ⇒ 自身累积 2 个缺陷无人发现
  （`ffprobe -of csv=p=0` **列序与请求顺序相反** ⇒ 12 条 HDR 格假红；
  `Start-Process -ArgumentList <数组>` **不逐元素加引号** ⇒ `"Display P3"` 被拆开 ⇒ 3 格恒红）。
  **两处都是闸自身缺陷，产品侧无问题。**

**🐛 修一处「断言名随环境抖动」导致的门禁周期性假红**

- `verify-ui-split-union.ps1` 硬要求「旧宿主断言名并集 == 409 且 extra == 0」。
  上一版把 J1z 写成 `if (hw == null) Skip(...) else Check(...)` ⇒ **断言名随环境出现/消失**
  ⇒ 并集计数在 **409 / 408 间抖动**（实测：早先 409，之后连跑 5/5 次都走 SKIP ⇒ 408）⇒ 周期性假红。
- 根因是**作者侧设计缺陷**：同组 `L1a`/`L1b` 之所以稳定，是因为它们**无条件** `Check(...)`。
- 修法 = 两条都**无条件**发出（与 `L1a`/`L1b` 同形）：`Skip` 仍只在前置不满足时点名"未验证"，
  `Check` 无条件发、判据设计成"前置不满足时判通过"（**没有硬件项就不存在图标污染**，这本身是
  符合预期的事实，不是"没测"）⇒ 名字恒定、计数稳定；**既不谎报红、也不静默消失**。
- 实测：`ui-sweep` 连跑 3 次均 **139/0 skip=1** 且断言名每次都在；union **11/0（new=409 old=409）**。

**📌 门禁基线（本次发布时）**

- 受管脚本 **72 条**、探针默认清单 **26 个 mode**；权威全量门禁 **exit=0「全部通过」**。
- ⚠ 各版本条目里记录的数量为**当次发布基线**；实时权威值以 `_run-step3-gates.ps1` 的
  `$Probes` 与 `$expectedScriptCount` 为准。

### v1.6.0-beta4 (2026-10-01) — 增益图 JPEG 不再冒充无损 + float JXL 能取回来 + `--jxl-modular` 接上；首批 L3 判据进门禁清单（60→65）

> **谱系**：与 beta3 不同，本条**是真行为变更** —— `src/` 下 3 个产品文件在 beta3 出包后被改动
> （`CjxlService.cs` / `QueueProcessor.cs` / `ImageEncoderArgs.cs`），下面四条都各有产物级判据背书。
> ⚠ 2026-10-02 追加：**新增**一节（缩略图）也落在 beta4 里，产品侧再 +1 个文件 `ThumbnailService.cs`
> （新）+ `ExifToolService.cs` / `FormatCapabilitiesService.cs` / `QueueProcessor.cs` / `CliParser.cs` / 模型两处。

**🆕 新选项（2026-10-04）：`--raw-color-target` / GUI「RAW 输出色域模式」三档**

- **`rec2020`（默认）= 既有行为一字不改**（Rec.2020 表达优先：能承载 HDR 传递的容器与 TIFF ⇒ `bt2020`，其余 ⇒ `bt709`）。
- **`auto`**：在默认档之上叠加**内容判定** —— 若该图**实际用色未超出 bt709 范围**，就不用 Rec.2020 表达。
  ⚠ **作用域仅限「SDR 传递的 bt2020 档位」**（当前 = TIFF 例外档）；**HDR 传递档（avif / jxl / png-16bit）不参与** ——
  bt2020 是 BT.2100/PQ 的组成部分，只降原色会产生 `bt709 + PQ` 这个合法但**非标准**的组合（日志显式点名该档位不降级）。
- **`rec709`**：强制 `bt709 + sRGB`（最大兼容性），覆盖容器能力。
- **判据**：经 `bt2020 → bt709` 矩阵后取最小分量，**只统计亮度 ≥ 0.25 × Y(p99.9) 的像素** ——
  暗部噪声会把色度甩到色域外但完全不可见，必须排除（实测：A7II 样本的"超界"像素全在最暗两档，过地板后为 **0**；
  而黄色花卉样本集中在最亮档 **20.98%**）。阈值 = 越界像素 **< 0.1%** 且最负超出 **> −0.5%**。
  ⚠ **三态**：测不出（尺寸不符 / 地板以上无像素）⇒ **保守保持 `bt2020`**，绝不把"测不出"当成"装得下"。
- 选项**进预设**；GUI 位于「高级色彩参数」面板内（取值用 ASCII 记号 + 本地化标签/提示）。
- **实测**（ARW → TIFF，A7II 白纸黑线稿样本）：默认 ⇒ `bt2020`（**回归锁：默认档未变**）；
  `auto` ⇒ 实测超出 bt709 = **0.0000%** ⇒ **`bt709`**；`rec709` ⇒ `bt709`；非法取值 ⇒ **退出码 2** 并点名取值域。
- 新增 `ServiceProbe gamutfit`（**15 条**，合成数据、不依赖素材）：矩阵**同源锁**（本内核的 `M2020To709` 派生自
  `ColorSpaceRegistry`，两处独立维护同一对常数时**实测差 2.7e-4** ⇒ 会让"判据说装得下、实际却裁切"）、正反互逆、
  阈值形态、**三态**、`EpsNeg` 与**亮度地板负控**。已**变异验证**（`FloorFactor=0` + `EpsNeg=0` ⇒ `pass=11 fail=4`，
  红的正是行为断言 (d)(e)；还原后回 `15/0`）⇒ 探针默认清单 **25 → 26 个 mode**，整轮合计 **673 → 688 / 0 fail / 1 skip**
  （⚠ 2026-10-04 审查订正：原写 `669 → 688` 差 4。`688 − 15(gamutfit) = 673`；
   `669` 是同日更早（03:10）的陈旧读数，其间 `wire` 探针自身 **89 → 93（+4）**，与本次改动无关）。
- 维护：CJK 闸判据项 `CsUi` **822 → 828**（+6，成因 = 同日上午更晚一批**未登记**的 UI 桶中文；已用可回滚对照实验
  证明与本次改动无关 —— 注释掉本次新增的 6 条 `[RAW]` 日志后 `[tag] 493 → 487` 而 `CsUi` 不变）；
  `_lib-matrix.ps1` 的 4 处 `FfmpegCommandBuilder.cs` 行号锚重钉 **+4**（977→981 / 987→991 / 1019→1023 / 2061→2065）。

**🔧 行为变更（RAW 默认输出目标，2026-10-03）**

- **RAW 输入未指定目标色域时的输出目标改为三档**（此前一律 `bt709|bt2020 + linear`，等于把**线性**中间件原样交付）：
  - 容器能承载 HDR **传递**（`avif` / `jxl` / `png`-16bit…）⇒ **Rec.2020 + PQ**；
  - **TIFF 为唯一例外** ⇒ **16-bit + Rec.2020 + SDR**（bt2020 原色 + sRGB 曲线，经生成的 2020 ICC 描述）；
    ⚠ 位深默认 **16-bit** 于 2026-10-04 **显式钉住** —— 此前只是"恰好"跟随源探测位深，换一台 12-bit 的机器就会变 12-bit；
  - 其余（`webp` / `jpg` / `jpegli` / `gif` / `jxr` / `ppm`…）⇒ **bt709 + sRGB**（默认路线）。
  用户显式选择目标色域时仍以用户为准，并同样受容器能力钳制（`GetFormatColorCapabilities` 单一真值）。
- **顺带修复：RAW 任务的色彩引擎「直通」缺陷**。`ColorPrimaries/ColorTrc` 此前**同时**充当「输入声明」
  （`BuildColorArgsSplit`，写在 `-i` 之前）与「输出语义」（`DecideOutputColor`），而 dngtool 的中间件是
  **线性**的 ⇒ 一对字段表达不了两个值：输入被正确标成 `linear`、输出却被同一份 `linear` 拖住
  ⇒ **像素一个都没动**（实测 ARW→TIF 产物与中间件 `cmp` 逐字节相同）。现拆为：新增**运行时**字段
  `ColorSourceDeclPrimaries/Trc` 只喂输入侧与引擎源描述符（不再兼作目标），`ColorPrimaries/ColorTrc`
  **在 RAW / JXL 这两条自产中间件的路径上**从此只表示输出目标；源描述符改用 `FromCicp(...)` ⇒ 置信度 `CicpTag` 恰达闸门阈值 ⇒ 映射被放行。
  ⚠ 2026-10-04 审查收窄措辞：该拆分**只覆盖 RAW / JXL 路径**。`FfmpegCommandBuilder.cs:832-852` 的
  分支在**没有** `ColorSourceDecl*` 时**仍**把 `ColorPrimaries/ColorTrc` 当作 `-i` 前的**输入声明**
  （见 `:813-822` 的注释与 `docs/review-2026-10-04/audit-B-执行落盘.md` 的同类登记）⇒ 原措辞
  「从此只表示输出目标」若脱离上下文单读，是**过宽的全称断言**。
  实测：ARW→TIF `决策：Map`、产物 `bt2020/iec61966-2-1`；ARW→AVIF 产物
  `yuv420p12le + color_primaries=bt2020 + color_transfer=smpte2084`。
- **修复：`--color-space`（非高级模式）此前会被 RAW 默认目标覆盖**（2026-10-04 修）。RAW 默认块的「用户是否已选
  目标」判据原先只查 `UseAdvancedColorParameters` + `ColorPrimaries`/`ColorTrc`，**漏了非高级模式下的 `ColorSpace`**
  （CLI `--color-space`、GUI 色空间友好名走的正是这条）⇒ 用户显式选过目标时 RAW 默认块**照样触发并覆盖**
  （实测 `--color-space sRGB` 交付 `bt2020 + sRGB`，而用户要的是 bt709 原色）。现新增 `rawUserChoseTarget` 判定：
  用户显式指定**任一**色彩目标轴（色空间友好名 `ColorSpace` / `ColorPrimaries` / `ColorTrc`）都不再被 RAW 默认值覆盖；
  同时把**输入声明** `ColorSourceDecl*` 移出该判定（**恒写** —— 用户选了目标时 dngtool 中间件**仍是线性**）。
- **RAW 三档的 HDR 档判据由 `CanHdrWith(JpegGainMap)` 改用 `CanHdr`**：`jpg`+GainMap **不再**算作 HDR 档
  ⇒ 这类目标的默认输出由 **Rec.2020 + PQ** 变为 **bt709 + sRGB**（Ultra HDR 的**底图**必须是 SDR；把 `jpg`+GainMap
  算进 HDR 档会让底图写成 PQ，属语义错）。

**🔧 行为变更（精确色彩参数生效 + 复选框语义，2026-10-04）**

- **命令行的 `--color-primaries` / `--color-trc` 此前是"死开关"**。这两个开关（以及 GUI 高级面板里的同名下拉）
  只有在 `UseAdvancedColorParameters` 为真时才算「输出目标」，而**那个布尔只有 GUI 会置位、`CliParser` 从不设置**
  ⇒ 命令行传它们只落到 `-i` 之前的**输入声明**，改不了输出色域（实测：ARW→TIF 传与不传都交付 `bt2020`，
  用户要的 `bt709` 从未生效）。现把**语义判据一律改为值派生**（新增 `FfmpegOptions.HasExplicitColorParams`
  = 原色 / 传递函数 / 矩阵任一非空），12 处语义读点不再读那个 UI 布尔，并按职责分工：
  **判「目标是谁」用 `HasExplicitColorTarget`**（只认原色 / 传递函数 —— `--color-matrix` 是**输入矩阵声明**、
  不是目标轴），**判「是否给了精确参数 / 输入声明」才用 `HasExplicitColorParams`**（含矩阵）。
  实测 ARW→TIF：`--color-primaries bt709 --color-trc srgb` ⇒ 产物 ICC 实测 **BT.709**
  （`rXYZ=[0.4360,0.2225,0.0139]`）。
- **「高级色彩参数 ⚙」复选框明确为纯 UI 开关**（面板可见性 / 能否选模式 2、3、4），**不再参与色彩决策** ——
  勾选与不勾选**不再造成策略差别**。三个精确参数下拉（原色 / 传递函数 / 矩阵）新增首项 **`auto`（= 不指定）**
  并作为默认：**模式 1（Recommended）** 允许 `auto`（走自动默认：容器能承载 BT.2020 ⇒ BT.2020，否则 sRGB/BT.709），
  **模式 2/3/4 必须手动指定**（缺项时界面点名）。此前"勾选但没动下拉"会提交 `bt709/iec61966-2-1`，与"不勾选"
  的目标不同；现提交 `null`，两者**完全一致**。
- **TIFF 的目标色彩声明补齐**：经精确参数指定目标的 TIFF 此前**不嵌目标 ICC**（后置门控只看色空间友好名）
  ⇒ 交付物零色彩声明；**RAW 默认档**（未指定目标）同样被排除 ⇒ ARW→TIF 默认档像素已被 zscale 转到 `bt2020`、
  产物却**不声明任何色彩**（下游按 sRGB 误读 —— 属"日志宣称 bt2020 而产物不声明"）。现两路都嵌目标 ICC。
  实测三格（ARW→TIF）：精确参数 ⇒ 584 B **BT.709**；`--color-space sRGB` ⇒ 584 B **BT.709**；默认档 ⇒ 584 B **BT.2020**。

**🔧 行为变更（完整审查修复：JXL 输入目标 / RAW 默认档位深 / Ultra HDR 峰值，2026-10-04）**

- **JXL 作为输入时，用户显式指定的目标色域此前被静默吞掉**（2026-10-04 完整审查 线B P1-1 修）。
  SDR JXL 的「输入色声明」原先写进 `ColorPrimaries/ColorTrc`，而这对字段在 `DecideOutputColor` 里是
  **输出目标** ⇒ `-i x.jxl -f png --color-space "Display P3"` 产物 CICP 仍是 `bt709`（P3 被吞、无任何告警）。
  现改走 `ColorSourceDeclPrimaries/Trc`（与 RAW 同一条路）⇒ 实测产物 `smpte432` ✓。
  同批把 `ColorIntentFactory` 的「自产中间件」分支收紧为**只认 RAW 与 Ultra HDR 解码**两条路，
  命令行/其它路径设的 `ColorSourceDecl*` 不再被误当"已知源"。
- **RAW 默认档的「HDR 承载力」判据用错了位深**（同审查 线D P1-2 修）：旧判据取 `BitDepth ?? 8`（**请求**档），
  而实际交付位深**跟随源**（RAW 中间件恒 16-bit）⇒ 16-bit 可承载 HDR 的容器被误判成 8-bit
  ⇒ 默认档降到 `bt709/sRGB`，产物是 `rgb48be` 却标 SDR。现按**实际交付档**判 ⇒
  **ARW→PNG / JXL 等 16-bit 容器的默认档变为 `Rec.2020 + PQ`**（实测 `rgb48be, smpte2084, bt2020`）；
  `webp` / `jpg`（8-bit 上限）仍为 `bt709/sRGB`；**TIFF 例外档仍为 `16-bit + Rec.2020 + SDR`**。
- **Ultra HDR 增益图解码路径：峰值接入意图 + 补输入侧声明**（同审查 线D P1-1 部分修）。
  解码产物是**线性** HDR（1.0 = SDR 白点 203 nits），此前既无输入侧色彩声明、也没把
  `DecodedUltraHdrPeakNits` 接进 `it.HdrPeakNits` ⇒ 用户给的目标会被置信度闸门挡住、headroom 恒为 1。
  现两者都补 ⇒ **用户显式目标在 Ultra HDR 输入下生效**（实测 `--color-space sRGB` ⇒ `决策：Map`）。
  ⚠ **仍未解**：**未给目标时**该路径仍无默认目标（`决策：None` 直通）；且因源曲线声明为 `linear`、
  而引擎的 HDR 认定是**曲线类型**（PQ/HLG），`Ultra HDR → SDR` 仍**不挂色调映射**（>1.0 截顶）。
  需在**规划层**决定「线性 HDR 中间件」的 HDR 认定与默认目标。

**🔧 行为变更（GUI「RAW 输出色域模式」下拉此前**完全无效**，2026-10-04 修）**

- **`--raw-color-target` 的 GUI 下拉 `RawColorTargetCombo` 此前是"死的控件"**（P0，宣称≠交付）：
  它在 `MainWindow.xaml` 里声明、在 `.cs` 里 `FindControl` 并被两处读取，
  但**既没有初始选中、也没有 `SelectionChanged` 处理器**（其 8 个兄弟下拉**都有**初始选中）。
  ⇒ Avalonia 的 `ComboBox` 无选中时 `SelectedItem == null` ⇒ `RawColorTarget` 恒为 **null**
  ⇒ `QueueProcessor` 的 `?? "rec2020"` **永远生效** ⇒ **用户在 GUI 里选 `auto` / `rec709` 完全不起作用**，
  且命令预览不刷新。⚠ **CLI 同参数一直正常**（实测 `rec709` 产物为 BT.709 ICC）⇒ 缺陷**仅在 GUI**。
  现补 `SelectedIndex = 0`（= `rec2020` 默认档，不改变既有行为）+ `SelectionChanged` 处理器。
  **永久锁**：新增门禁第 68 条 `_probe-gui-control-wiring.ps1` —— 结构锁：XAML 里每个 `x:Name` 下拉
  必须在**三条合法路径**之一被接线（`.cs` 直赋 / `FillComboByLoc` / XAML 属性），
  带反控（白名单外 combo ≥ 8，防提取器空跑）与 `RawColorTargetCombo` 点名锁。
  变异验证：删掉该控件的初始选中 + 处理器 ⇒ **77/0 → 73/4 转红**；恢复 ⇒ **77/0**。
- **`--raw-color-target rec709` 与显式目标色域同时给出时，不再被静默丢弃**（P1）：
  `rawUserChoseTarget` 门会挡住整块默认档逻辑（含 `rec709`），旧日志只说「用户已指定目标色域 ⇒
  保留用户配置」、**一个字都不提 rec709** ⇒ 用户以为 `rec709` 生效了，实际被丢弃，
  且与 `CliParser` 帮助文本（"覆盖容器能力"）矛盾。语义裁定**用户显式目标优先**（不改既有优先级），
  但**必须点名冲突**并给出可执行建议（去掉显式目标色域参数）。⚠ 仅 `rec709` 有冲突：
  `rec2020` 即默认档、`auto` 的作用域只在 TIFF 那条 SDR 例外档 ⇒ 二者**不点名**（避免噪声）。
- **动图回退路径的克隆漏抄 3 个字段（P1，已修）**：`CloneOptionsForFfmpeg` 逐字段手抄，
  漏了本批新增的 `ColorSourceDeclPrimaries/Trc` 与 `RawColorTarget`（另有既有的 `ColorFromRawDefault`）。
  不补 ⇒ 动图回退时 `ColorSourceDecl*` 为空 ⇒ `BuildColorArgsSplit` 回落到**拿
  `ColorPrimaries/ColorTrc`（现为输出目标）当 `-i` 前输入声明**的分支 —— 正是这对新字段要消灭的缺陷
  ⇒ 修复此前只覆盖非动图路径。现已补齐 4 个字段。

**🔧 行为变更（HDR→SDR 色调映射被静默跳过时**如实点名**，2026-10-04）**

- **无 HDR 余量时不再"静默 off"，而是出声说明"这是恒等、不是丢弃你的请求"**。
  机制：`ToneMapParams.Active = Mode != None && Headroom > 1+1e-6`，而 `Headroom = PeakNits/SdrWhiteNits`；
  当**整帧实测内容峰值 ≤ SDR 参考白（203nit）**时，`ApplyMeasuredPeak` 的钳制把 `PeakNits` 拉到正好 203
  ⇒ `Headroom == 1.0` ⇒ `Active=false` ⇒ 内核 `toneOn=false` ⇒ **用户显式 `--color-tone-map hable` 整段不执行**。
  ⚠ 这在数学上**是正确的**（无余量时 Hable 在该区间恒等），所以**没有改算法**；
  但此前日志自相矛盾（同一份日志里一处打 `tonemap=off`、另一处打 `tonemap=Hable`）⇒ **无法归因**。
  现新增一条点名日志：`⚠️ 实测内容峰值 199nit ≤ SDR 参考白 203nit ⇒ 本图无 HDR 余量，色调映射（Hable）
  在此为**恒等**、按无余量直通处理（**不是**丢弃用户请求；Headroom=1.00）`。
- **新增「内核状态」审计日志**（可审计性缺口）：HLG 的 BT.2100 OOTF 是否真的施加，**此前任何日志都拿不到**
  （排查只能靠反推，曾导致误判）。现每次输出源/目标曲线名、`HLG源OOTF=开/关(γ)`、`HLG目标逆OOTF=开/关`、
  `tonemap` 的**模式 / 激活 / headroom / 采用峰值**。⚠ 模式与激活**分开打**：`激活=否` 有
  「用户没要」与「要了但本图无余量⇒恒等」两种相反含义，只打一个无法区分。
- ⚠ 附带**记账诚实化**（文案，非功能）：计划层 `Backend=H2(FfmpegFilter)` 目前**全仓无执行体**
  （实际恒由 H1 进程内内核兑现），原日志单方面声称"由 zscale 执行"与事实不符；现如实加注
  `[⚠ 本档暂无 H2 执行体，实际由 H1 进程内内核兑现（记账项）]`。**真正的 H2 执行支属独立任务**。

**🔧 行为变更（AVIF 增益图主图的 range 标签，2026-10-03）**

- **AVIF 增益图产物的主图 range 标签不再被写死为 full**（`IsoBmffGainMapWriter.PatchColr`）：此前 `PatchColr`
  按调用方参数**改写**底图 `colr` 盒的 `full_range` 位，且 `Build()` 的 `baseFullRangeFallback` 默认 `true`（full）
  ⇒ **每条 AVIF 增益图产物的主图**都被贴上 full range 标签，而像素实为 **limited/tv**（ffmpeg 实产 `color_range=tv`、
  容器内 `colr` 末字节 `0x00`）⇒ 下游按 full 解读会把 Y∈[16,235] 当成 [0,255]，表现为**黑阶抬起、对比度压缩**。
  现改为**只读继承底图原 `colr` 的 `full_range` 位**（新增 `ReadNclxFullRange`），默认兜底值 `true → false`
  （limited/tv，与 ffmpeg 实产一致）；底图自带 nclx 时一律以原 `colr` 为准，且继承是**双向**的
  （底图真为 full 时仍写 full）。
  ⚠ 这是**已交付产物的标签发生变化**：此前产出的 AVIF 增益图文件，其主图 range 标注与像素不符 ⇒ 需重新导出。
  永久锁：新增 `ServiceProbe avifgm` 模式（默认清单第 25 个），端到端校验产物主图 `colr` 末字节与底图一致
  （三段：① 单元级反射直调 `PatchColr`，验证只读继承且双向；② `Build` 的 `baseFullRangeFallback` 默认值必须 `false`；
  ③ 真 ffmpeg 编 tv 底图经产品写出器后，产物底图 `colr` 仍为 `0x00`）。

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
