# _run-step3-gates.ps1 —— 步骤 3 改动后的门禁批量执行（只回显每个门禁的汇总行 + 失败明细）
# 用法：pwsh -NoProfile -File tests/scripts/_run-step3-gates.ps1 [-Probes a,b,c] [-SkipScripts] [-Scripts a,b]
#   ⚠ `-Scripts` = **脚本级子集**（2026-09-24 新增）。动机：此前只有"全跑 68 条"与
#     "裸跑单个 verify-*.ps1"两种选择，而裸跑会绕开本文件的**仓库锁 / 单步超时 / STALE 新鲜度闸 /
#     双漂移复核 / `_gate_script_*.txt` 留痕**五项保护 ⇒ 增量验证要么太贵要么不可信。
#     ⚠ 全跑墙钟不再写死分钟数：2026-10-01 起清单含 5 条 **L3/产物级**套件（第 61~65 条），
#       原散文里的 ≈20min 只覆盖 60 条那批，接进 L3 后已过期 ⇒ 以本轮日志末尾的耗时汇总为准。
#     子集跑**保留**上述五项，但**不构成基线**（汇总行会点名），宣布新基线仍需一次全量。
# ⚠ `colormath`（2026-09-26 接进默认清单，第 22 个 mode）——**必须**在默认清单里，理由是它守的是
#   「原色表被悄悄改回缺陷值」这类**没有任何其他判据会响**的回归：`matrix` 只锁标签选路、
#   `plan/curve` 只看传递函数，把 `ColorSpaceRegistry` 的色度常数抄错，像素会**静默偏色**而全绿。
#   接进来之前先做了变异验牙（两次都是**改坏 `ColorSpaceRegistry.cs` 再逐字节还原**，还原后
#   `sha256=b8175f16ffeb129b` 与变异前一致、重建后 `colormath` 回 `12/0`）：
#     · M1 把 `bt470bg` 的 G 从 (0.290,0.600) 塌回 bt709 的 (0.300,0.600)（= 修复前「多 token 共用一套常数」
#       的缺陷形态）⇒ `pass=11 fail=1`，红条读数 `实差 4.40E-002`；
#     · M2 把已**故意不收录**的 `smpte428` 按旧写法补成 `smpte431` 的字节级副本 ⇒ `pass=10 fail=2`，
#       两条各自咬一个方向：`实差 1.99E+000`（≈199% 满量程，正是"拿 DCI-P3 矩阵乘像素"的量级）
#       + 「白名单外重复对 `smpte428≡smpte431`」。
#   ⇒ 两条都有牙 ⇒ 才允许进默认清单。0.4 s、纯进程内、不依赖素材与外部 exe。
# ⚠ `thumbnail`（2026-10-02 接进默认清单，第 23 个 mode；实测 40/0，本机 10 s）——它守的是
#   「产物**声称**有 EXIF IFD1 缩略图、而图像数据一个字节都没有」：判据是
#   `exiftool -b -ThumbnailImage` 取回的字节数 **== `ThumbnailLength` 声明值**，不看退出码。
#   覆盖容器 = jpg / png / apng / webp / avif / jxl（六格逐一真跑）；tiff / gif / jxr / dng 走出声拒绝支。
#   变异验牙（两次都改坏 `src/` 再原样改回，还原后重跑回 40/0）：
#     · M1 让嵌入步直接返回 0（模拟"编码成功但一个缩略图都没写"）⇒ `pass=33 fail=7`，
#       红的是 T11 六格（全部 `rc=0` 却 `declared=0`）+ T17；T12/T13/T14/T15 保持绿。
#     · M2 把能力表吹大（给 **gif** 开一条实测走不通的路）⇒ `pass=38 fail=2`，
#       红在 T8 + T13，且 T13 读数 `rc=0 / declared=0` 正是 **exiftool 对走不通的容器
#       「0 image files updated 但退出码 0」的静默空转** ⇒ 能力位是唯一的闸、回读字节才是判据。
#   ⚠ 该 mode 依赖 publish/PLAN 的 ffmpeg/exiftool/avifenc，缺席时逐条 **SKIP 点名**（不静默消失）。
# ⚠ `oog`（2026-10-03 接进默认清单，第 24 个 mode；单跑实测 6/0，进程内 ~1 s）——它钉的是
#   「**越界带 H1 内核 ≡ zimg 参照**」：两路产物过有损编码后聚合 PSNR 会被**共享量化噪声**主导
#   （实测同一对像素：进编码器前 85.9 dB、过 `-d 2.4` 后 36.9 dB），所以那条真不变量只能放在
#   **无损通道**上测（rawvideo/rgb48，不经任何有损档）。来历见 `docs/HANDOVER_2026-10-02.md` 第十/十一节。
# ⚠ `avifgm`（2026-10-03 接进默认清单，第 25 个 mode）——它钉的是 **AVIF 增益图写出侧** 的
#   `colr.full_range`：`IsoBmffGainMapWriter.PatchColr` 曾把底图 colr 的该位**硬写成 full(0x80)**，
#   而 ffmpeg(libaom-av1) 对 yuv420p 实产的是 **tv(0x00)** ⇒ 每条 AVIF 增益图产物的主图都被贴错 range。
#   ⚠ 该缺陷长期静默的**结构性原因**：仓库内此前**没有任何判据覆盖这条写出路径**
#   （`gainmap-isobmff` 只测**解码**侧、`gainmap` 测 **JPEG** Ultra HDR 容器）⇒ 补此 mode 作永久锁。
#   三段：① 反射直调 `PatchColr` 断言「只读继承、双向」；② 反射读 `Build` 的 `baseFullRangeFallback` 默认值必须 false；
#   ③ 端到端（真 ffmpeg + 产品 `EncodeAsync(container:"avif")`）断言产物底图 colr 末字节 = 0x00。
#   ⚠ 依赖 `publish/PLAN/ffmpeg-full/ffmpeg.exe`（由 `InitExternalTools` 定位）；缺席时 ③ **SKIP 点名**，① ② 仍跑。
param([string]$Probes = 'contract,wire,verdict,selftest,iccname,plan,curve,matrix,colormath,ootf,hlgwire,bt2446,decision,engine,runner,settings,procstreams,encoding,ipc,i18n,geometry,metaraw,thumbnail,oog,avifgm,gamutfit',
      [switch]$SkipScripts,
      # ⚠ 逗号分隔的**文件名**（含 .ps1），顺序不敏感；名字不在清单内 ⇒ 响亮报红而不是静默忽略。
      #   `pwsh -File` 下 `-Scripts a,b` 只会作为一个参数传入（与 $Probes 同一坑，见 :455），故这里自己切。
      [string]$Scripts = '',
      # ⚠ #22（2026-09-19）：单步硬超时。标定依据 = 2026-09-19 整轮**全量实测分布**
      #   （探针 20 条 max 10.4 s / 合计 43.4 s；子脚本 42 条 max 158.9 s / 合计 848.6 s；总墙钟 892 s）
      #   ⇒ 脚本段取 max × ≈3 向上取整（480）。**探针段刻意宽**（2026-09-19 team-lead 定稿 = 120）：
      #   对**历史最慢项** `geometry` **33.8 s** = **3.55×**；对本轮 max 10.4 s = **11.5×**。
      #   为什么宽优于紧：探针是**进程内断言**，超时只会**误杀**、不会漏抓；
      #   **挂死永不返回，宽超时照样抓到**（挂死 = 无限），而**紧超时在负载下会假红**。
      #   ⚠ 两个默认值都由 `Test-RunnerShape` **逐字钉住**：探针段 = **第 11 针**、脚本段 = **第 12 针**
      #     （防按各自基准悄悄调参 —— 本次即为「60 vs 120」往返多轮的根因）。
      #   ⚠ **2026-10-02 重标：480 → 900**（同一条"max × ≈3 向上取整"规则，换了实测分布）。
      #     依据 = 本轮整轮（清单 65 条，`tests/output/t84/round-beta4.log`）子脚本读数：
      #       max **243.8 s** `verify-color-strategy` ｜ **163.5 s** `verify-jbrd-e2e`（第 62 条，本批新接的 L3）
      #       ｜ **144.9 s** `verify-color-wiring` ｜ **120.4 s** `verify-encoder-knob-liveness`（第 65 条）
      #       ｜ 脚本段合计 ≈**2015 s**（≈34 min）。243.8 × 3 = 731 ⇒ 向上取整 900（对最慢项 = 3.7×）。
      #     ⚠ 旧的 480 早已不是"max×3"：它按 09-19 的 max 158.9 s 定，而 `verify-color-strategy` 后来长到
      #       243.8 s ⇒ 480 只剩 2.0×，按本段自己的话"紧超时在负载下会假红"，它**会咬合法慢门禁**。
      #       接 L3 五套只是把次高项抬了一档，触发的是**重新结算**，不是给新条目开后门。
      #     ⇒ 第 ⑫ 针同步改成 900（两处不一致 = 运行器拒绝跑）。
      [int]$ProbeTimeoutSec = 120,
      [int]$ScriptTimeoutSec = 900)
# ⚠ geometry mode（2026-09-17 新增）**必须**在默认清单里，理由是一条实测缺陷：
#   `RawColorPipeline.cs:94-129` 的「引擎几何缩放」在默认路径上**永不执行** ——
#   `QueueProcessor.cs:429-496` 的**预缩放**在**路由之前**跑，并在 `:487` 改写 `captured.InputPath`
#   ⇒ 引擎拿到的是已缩过的输入 ⇒ `BuildMaxDimensionScaleFilter` 返回 null ⇒ 那段是**死代码**。
#   只有**直调** `RawColorPipeline.TransformFileAsync`（本 mode 的做法）才能让它跑起来。
#   ⇒ 不加进清单，这段就**永远零覆盖**；将来删预缩放执行体会让它**突然承重**。
#   断言含 5 个用例 + 边界（`scale=-2:32` ⇒ 42x32，自算会漂到 43/44）+ 负控 + 逐例「引擎尺寸 == 预缩放尺寸」。
# ⚠ peak mode 需要文件参数（ServiceProbe peak <file> …），裸跑只打 usage 并记 fail=1 ⇒ 不进默认清单；
#   峰值断言由 verify-color-peak.ps1（25 条，带参数与素材）执行。
# ⚠ i18n mode（2026-09-17 新增）无参数、纯进程内，进默认清单：它锁的是「双语字典真的被加载且取值命中」，
#   与 `_probe-i18n-scan.ps1`（只锁「json 里定义了 key」）互补 —— 二者之间隔着
#   「Resources/Locales 是否随构建拷进输出目录」这道坎，只跑后者会漏掉**产物陈旧**导致的假绿。
#
# ── ⚠ 并发约束（2026-09-16 对清单内 28 个 .ps1 逐一普查写入路径后更正措辞；
#    2026-09-17 新增 verify-gamut-map.ps1；**同日按「运行级隔离（GUID）」改写，见 §6 第 66 条**；
#    同日再新增生产者 `_probe-jpg-p3-icc.ps1`，理由见下方 ②(b) 与调序注释；
#    同日再新增 `verify-geometry-engine.ps1`（**几何缩放死代码**的专项门禁，理由见 `$Probes` 的 `geometry` 注释））──
#    ⚠ **清单条数以实测为准 = 68 条**（⚠ 2026-09-19 P4-A 前为 42 条；2026-09-23 由 48 经 49/50/51 到 52，2026-09-23/24 再接 `verify-ipc-extra-options.ps1` ⇒ 53，2026-09-24 再接 `_probe-settings-clone-coverage.ps1` ⇒ 54，2026-09-27 再接 `_probe-matrix-structure.ps1`（任务 #42，秒级结构自检）⇒ 55，同日再接 `_probe-stderr-merge-scan.ps1`（任务 #51，合并取数结构锁）⇒ 56，同日再接 `verify-simd-switch-fallback.ps1`（面板「SIMD 优化」声明/实现冲突 ⇒ 判据单点 `SimdKernelRouting`）⇒ 57，2026-09-30 再接 `verify-geometry-orientation.ps1` + `_probe-geometry-single-source-scan.ps1`（取向标签 ⇒ **显示尺寸**口径的专项门禁 + 唯一实现结构锁）⇒ 59，同日再接 `verify-gainmap-host.ps1`（把 `GainMapTestHost` 套件接进清单 + STALE 第 ⑤ 对）⇒ 60，2026-10-01 再接**首批 L3/产物级套件**5 条（`verify-package-smoke` + `verify-jbrd-e2e` + `verify-orientation-rewrap` + `verify-gainmap-thirdparty` + `verify-encoder-knob-liveness`；原居 gitignored 的 `tests/output/t{69,76,77,79}`，`git clean -xdf` 一次就没、也不在任何轮里被跑）⇒ 65，2026-10-04 再接 `_probe-jxl-input-target.ps1` + `_probe-raw-default-tier.ps1`（色彩审查两个 P1 的判据）⇒ 67，同日再接 `_probe-gui-control-wiring.ps1`（72h 审查 P0：GUI 控件「声明了却没接线」结构锁）⇒ 68）（`foreach` 数组里的 `.ps1` 条目数）。
#      原注释写「29 个」「31 条」均与实测不符（**历史偏差**，非本轮引入），已按实测更正。
#      2026-09-18 新增 `verify-engine-firstframe.ps1`（引擎多帧解码只取首帧，理由见该脚本头注释）
#      ⇒ 32 → 33，已重新数过。
#      2026-09-18 再新增 `verify-jxl-codestream-route.ps1`（JXL 裸码流判型死分支 ⇒ 静默换编码器）
#      ⇒ 33 → 34，已重新数过。
#      2026-09-18 再新增 `verify-anim-static-target.ps1`（动图 webp/apng 写单帧容器 ⇒ 零产物；
#      判据改为「目标容器能否装多帧」，理由见该脚本头注释）⇒ 34 → 35，已重新数过。
#      2026-09-18 再新增 `verify-jxl-probe-timeout.ps1`（畸形 `.jxl` 让 ffprobe 帧数探测永不返回
#      ⇒ 队列永久挂死；**两处**探测站点都要超时可达 + Kill 进程树 + 响亮日志，理由见该脚本头注释）
#      ⇒ 35 → 36，已重新数过。
#      2026-09-18 再新增 `verify-ffmpeg-heartbeat.ps1`（**忙等型挂死**：`FfmpegRunner` 的旧守卫要求
#      「无输出 **且** 无 CPU 增长」，而忙等把 CPU 喂饱 ⇒ 30 分钟分支结构性不可达。心跳判据改用
#      `-progress pipe:1` 的**进度块到达**，由 `FfmpegRunner` 内部消化、不改 `[cmd]` 行）
#      ⇒ 36 → 37，已重新数过。
#      2026-09-18 再新增 `_probe-sync-read-before-wait-scan.ps1`（**直连进程启动站点的独立结构锁**：
#      「同步 `ReadToEnd()` 不得早于 `WaitForExit(...)`」—— 这是「超时参数是死代码 ⇒ 子进程不关管道就
#      永不返回」的共同形态，`IsAnimatedJxl` 挂死就是它；实测 A 类直连 65 处里 **12 处**命中，**不得增加**）
#      ⇒ 37 → 38，已重新数过。⚠ 它**纯源码扫描、不需要 exe、0.8 秒**。
#      2026-09-18 再新增 `verify-jxr-anim-input.ps1`（**JXR 中转路径的多帧缺口**：动图输入 → 固定文件名中转
#      ⇒ `Cannot write more than one file` ⇒ 零产物；站点 A = legacy 解码中转、站点 B = 引擎 JXR 出口的
#      **alpha 分支**（把原文件当第二输入喂 `alphaextract`）；判据与 #139 同一条 = 目标中转容器能否装多帧）
#      ⇒ 38 → 39，已重新数过。
#      2026-09-18 再新增 `_probe-ct-chain-closure-scan.ps1`（**取消令牌链路闭合**：同一文件内，目标方法有形参 `ct`
#      **且**调用点所在方法自己持有 `ct`，而这一跳**没交** ⇒ 命中。判据面内 **53** 处、命中 **0/0**；
#      ⚠ 它**必须打印跳过数**（目标无 ct 形参 1472 / 调用者不持有 15 / 同名不唯一 14 …）否则跳过会伪装成干净）
#      ⇒ 39 → 40，已重新数过。⚠ 它**纯源码扫描、不需要 exe、约 4 秒**。
#      2026-09-18 再新增 `verify-avif-anim-probe.ps1`（**AVIF 动画探测的三态纪律**：`IsAnimatedAvifAsync`
#      原本无超时、无三态，且**全部失败路径都 `return false`** ⇒ 把「不知道」当成「不是」⇒ 引擎按单帧
#      处理 ⇒ **静默丢帧**。本次补 20 s 可注入超时 + 三态 `(animated, determinate)` + **不确定 ⇒ 保守按多帧**）
#      ⇒ 40 → 41，已重新数过。
#      2026-09-18 再新增 `verify-webp-anim-probe.ps1`（**webp 动画探测的「合取判据 + 三态纪律」**：
#      `InputMayHaveMultipleFrames` 在「输出不是 gif/apng」时**直接回落** `IsAnimated`，而后者对
#      `.webp`/`.avif` **恒 false**（不探测帧数）⇒ **多帧 webp + 非 gif/apng 目标**被判成单帧
#      ⇒ 引擎按单帧处理 ⇒ **静默丢帧**（`#43` 登记的「路由轴未保守化」）。判据 = `format_name == webp_anim`
#      **且** `max(nb_read_packets) > 1`（⚠ **合取必需**：单用 `format_name` 会把合法的「1 帧动画容器」
#      `VP8X(0x02)+ANIM+1×ANMF` 误判成多帧；单用帧数又抓不住 `VP8X(0x02)` 无 `ANMF` 的非法文件）；
#      不确定 ⇒ **保守按多帧**。门禁第一验收点 = **素材前提断言**（实际帧数 + `format_name` 都先断言，
#      **不信任 `-frames:v`**））
#      ⇒ 41 → 42，已重新数过。
#      ⚠ **`#136` 收尾轮更新（2026-09-18）**：上面那句「`IsAnimated` 对 `.webp` 恒 false」**已过期** ——
#      `IsAnimated` 的**输入侧**已补 `.webp`（读 `AnimatedWebpCache`，由 `IsAnimatedWebpAsync` 在确定时写），
#      现在只剩 **`.avif`** 恒 false；且**预缩放跳过判定**已由 `IsAnimated(captured)` 改为直接消费
#      `ProcessAsync` 单点求值的 `inputMayHaveMultipleFrames`（`maxDimSkipAnimated`）⇒ 该变量**四处**复用。
#      ⚠ 每次增删脚本**必须重新数**，不要沿用上一轮的数字。
# ⚠ 原注释写「这些门禁必须**串行执行**」是**过强的断言**，且给的两条理由均不成立：
#     • `tests/output/validate/strat` 只被 `verify-color-strategy.ps1` 一个脚本使用（不构成跨脚本冲突）；
#     • **没有任何脚本**写全局 `tests/output/_exec.out` —— `run-pipeline-tests.ps1` 写的是
#       `tests/output/results/_exec.out`，其余脚本的 `_exec.out` 都在各自的输出目录内。
#   普查结论：清单内**不存在两个脚本写同一个固定路径**。
#
# 真正的约束只有两条，这才是"不要并发"的依据：
#   ① **同一脚本不得有两个并发实例**（自并发）。**2026-09-17 已把 12 个**「固定 `$out` + 开头
#      整目录删」的脚本改成 GUID 运行级隔离（`_probe-png-cicp-production` / `probe-gainmap-tiff` /
#      `verify-color-strategy` / `verify-color-wiring` / `verify-format-regression` /
#      `verify-gainmap-engine` / `verify-gainmap-managed` / `verify-gamut-map` /
#      `verify-prophoto-faithful` / `verify-tiff-icc` / `verify-webp-hdr-fix` /
#      `verify-widegamut-regression`），加上**既有**的 6 个（verify-ps-compat /
#      verify-png-signature / verify-gainmap-memory / verify-metadata-privacy /
#      verify-color-token-normalize / verify-hlg-route）与 3 个纯读扫描
#      （_probe-stderr-drain-scan / _probe-i18n-scan / _probe-cjk-hardcode-scan）
#      ⇒ **这 21 个对并发免疫**（⚠ 是"12+6+3"的 21，**不是**旧注释里"21 个固定路径"的那个 21）。
#      ⚠ **2026-09-17 同日第二轮**再隔离 **11 个**（A 类「固定 `$d` + 开头整目录删」形态）：
#      `verify-colorfmt-matrix` / `verify-color-tonemap` / `verify-color-peak` / `verify-png3-interop` /
#      `verify-color-engine-xcheck`（**以上 5 个在必跑清单内**）+ `_probe-gpu-stage1` / `_probe-caps-e1-e2` /
#      `_probe-sws-downshift` / `_probe-ztf-locate` / `_probe-zimg-tagged` / `_probe-zimg-transfer-table`
#      ⇒ 累计 **32 个对并发免疫**（21 + 11）。
#      ⚠ 同日第三轮再补 1 个：`verify-gif-avif-framelist.ps1`（**在必跑清单内**，见下方 ① 的残留清单）
#      ⇒ 累计 **33 个**。
#      ⚠ 2026-09-18 再补 1 个：`verify-anim-static-target.ps1`（**在必跑清单内**；运行级 GUID 隔离 +
#      绿时自清）⇒ 累计 **34 个对并发免疫**。
#      ⚠ 2026-09-18 再补 1 个：`verify-jxl-probe-timeout.ps1`（**在必跑清单内**；运行级 GUID 隔离 +
#      绿时自清）⇒ 累计 **35 个对并发免疫**。
#      ⚠ **有意排除 4 个**（照 §6 第 66 条「C 类共享目录簇严禁各自 GUID 化」；隔离只会把
#      「静默假绿」换成「永久静默 skip」，净收益为负，见 ②(c) 与 §6 第 68 条）。
#      **逐条写清「谁读谁、读哪个路径」**（行号为 2026-09-17 实测）：
#      · `_probe-jpg-p3-icc.ps1:16`（`validate/jpgp3`，**生产者**）：本轮刚把它加进清单建立**清单内有序链**，
#        两个消费者都在本清单内 —— `verify-colorfmt-matrix.ps1:18`（`$refIcc = …/jpgp3/p3.icc`）与
#        `verify-png3-interop.ps1:103`（`$icc = …/jpgp3/p3.icc`）⇒ 生产者改 GUID 会同时打断**清单内**两条读。
#      · `verify-color-caps.ps1:11`（`validate/caps`）：与 `verify-color-wiring` **双向共享写**（caps 已前置在清单内）。
#      · `_probe-sws-yuv.ps1:6`（`validate/swsyuv`，**生产者**）：`_probe-sws-yuv.ps1:18` 写 `swsyuv/in.raw`，
#        而消费者 `_probe-sws-pos.ps1:6` 的 `$d` 是**同一个固定目录**，并在 `_probe-sws-pos.ps1:19`
#        以 `-i `"$d/in.raw`"` 读它 ⇒ 生产者改 GUID ⇒ 消费者 `CmpFiles` 只会打印
#        「有文件缺失，跳过（不可判读）」⇒ 手工链**静默断裂**（不是红，是无声无息）。
#      · `_probe-gpu-stage2.ps1:5`（`validate/gpu2`，**生产者**）：`_probe-gpu-stage2.ps1:81` 写 `gpu2/k.cl`；
#        消费者 `_probe-gpu-stage3.ps1:9` 把 `c\:/PLAN/ffmpegPictureUI/tests/output/validate/gpu2/k.cl`
#        **硬编码**成 `$kesc`，并在 `_probe-gpu-stage3.ps1:39/:40/:41/:55` 以 `program_opencl=source=$kesc`
#        直接拼进 ffmpeg 参数；`_probe-gpu-stage3.ps1:8` 的 `$d` 也是同一个固定目录 ⇒ 同样**静默断链**。
#      ⚠ **实测残留（2026-09-17 逐脚本 grep，**不是估计**）**：仍**写**固定输出目录的门禁/探针共 **7 个**
#      （加上上面 4 个有意排除的 = 11 个未隔离）：
#        ✅ **原「① 在必跑清单内」的唯一一个已修**：`verify-gif-avif-framelist.ps1` 已按 A 范式 GUID 化
#           （`$work = tests/output/gifavif_<guid8>` + 按 `$fail` 自清，另两处提前 `exit` 也各自先自清）。
#           已核实**不是生产者**：全仓 `grep gifavif` 只命中它自己 ⇒ **无第二方读它**；
#           代价实测（GUID 化会让 `Make-Gif` 的跨运行复用失效）：3000 帧素材重造 **0.1 s**、420 帧 **0.03 s** ⇒ 可忽略。
#           ⇒ **必跑清单内已无未隔离项**。
#        ② C 类簇成员 / **跨簇写入**（**必须与生产者同批处置**，单独改它没意义）：
#           `_probe-sws-pos.ps1:6`（读+写 `validate/swsyuv`）、`_probe-gpu-stage3.ps1:8`（读+写 `validate/gpu2`）、
#           `_probe-icc-affects-decode.ps1:10` —— ⚠ 后者**不是「只读」**：它读 `validate/jxl-webp-pixels` 的产物，
#           又往**同一目录**写 `extracted.icc` 与临时 `_px.raw` ⇒ 形态是**跨簇写入**，独立 GUID 化会打断那条链。
#        ③ **低价值未隔离**（已核实**不在必跑清单** 且 **无 `CK`/无 `pass`/`fail` 计数** ⇒ 不参与并发竞争、
#           红了也没人看 ⇒ **登记不派工**）：`_probe-hdr-peak-oracle.ps1:14`（`tests\output\tmp\peak-oracle`）、
#           `_probe-p7d-bisect.ps1:19`（`validate/p7d`）、`_probe-zimg-noop-check.ps1:5`（`validate/cmsx`，**死诊断**）。
#        ④ 仍保持固定的**生产者**：`_probe-jxl-webp-pixels.ps1:23`（`jxl-webp-pixels` 簇，按 C 类裁定不动）。
#      ⚠ 上表只算**写**固定目录的。**只读**固定共享路径的**不算**自并发隐患（只读不互删）：
#        `verify-color-peak.ps1:46` 读 `validate/src_hdr.png`、`verify-colorfmt-matrix.ps1:18` /
#        `verify-png3-interop.ps1:103` 读 `validate/jpgp3/p3.icc`、`verify-gamut-map.ps1:44` 读
#        `sources/src_8bit.png`、`_probe-png-cicp-production.ps1:14/:73` 读 `gainmap/png3_cicp.png` + `validate/jpgp3/p3.jpg`。
#        （⚠ 反例提醒：`verify-png-signature.ps1:18` 的 `$val = …/pngsig` 是**死残留赋值** —— 同名变量在
#         `:46` 已被 GUID 覆盖，**不是**未隔离项。靠 grep 数数时必须排除这种形态，否则会多算。）
#      ⇒ 在①②③被隔离前，「同一脚本只跑一个实例」**依然是硬约束**。完整分类见 §6 第 66 条。
#      ⚠ _probe-cjk-hardcode-scan 的负控只写 $env:TEMP 下的 GUID 目录（自建自删），不碰仓库。
#   ② **清单内的跨脚本读写交叉**（2026-09-17 逐个核过 C 类 6 组，见 §6 第 66/68 条）：
#      (a) `verify-decision-delivery.ps1` 用 `Get-ChildItem tests/output/results -Recurse -Include *.avif`
#          **挑选**素材，而 `run-pipeline-tests.ps1` 恰好写 `tests/output/results/*.avif` ⇒ 二者并发时
#          前者可能读到半写/不确定的素材。**这两个脚本不得并行**。
#      (b) ⚠ **`validate/jpgp3` 组（本轮修）**：生产者 `_probe-jpg-p3-icc.ps1` 原本**不在清单**，
#          而消费者 `verify-png3-interop.ps1` 与 `verify-colorfmt-matrix.ps1` **在清单** ⇒
#          干净 `tests/output` 上消费者必然**静默降级**（实测 `PASS` 9→8 而 `exit` 仍为 0）。
#          **修法**：把生产者加进清单并**前置于两个消费者**（见下方调序注释）。
#      (c) 其余 4 组的核查结论（**均无此隐患**，无需处理）：
#          · `validate/caps`：生产者 `verify-color-caps.ps1` 与消费者 `verify-color-wiring.ps1`
#            **都在清单**，且已**调序**（caps 前置）⇒ 清单内有序链；
#          · `validate/cmsx`：生产者 `verify-color-engine-xcheck.ps1` 在清单、消费者
#            `_probe-zimg-noop-check.ps1` **不在** ⇒ 反向形态，消费者根本不跑 ⇒ 无隐患。
#            ⚠ 同日第二轮把**生产者**改成 GUID 隔离（它在清单内，自并发是**真实**风险）：
#            清单内**无**消费者读 `cmsx`；该清单外消费者读 `$d/A|B/zimg.png`，而**全仓无任何脚本写这个路径**
#            （`grep zimg.png` 只命中它自己）⇒ 它本就恒 `continue`（**死诊断**）⇒ 隔离**不损失任何活链**。
#          · `validate/jxl-webp-pixels` / `validate/swsyuv` / `validate/gpu2`：生产与消费**双方都不在清单**
#            ⇒ 无隐患（它们只在手工诊断时跑，届时「一次一个实例」已足够）。
#            ⚠ 其中 `swsyuv` / `gpu2` 的**生产者**本轮**有意不隔离**：两条链都还**活着**
#            （`_probe-sws-pos` 读 `swsyuv/in.raw`；`_probe-gpu-stage3` 读 `gpu2/src.png` 并把 `gpu2/k.cl`
#            硬编码进 ffmpeg 参数）⇒ 隔离生产者会**静默打断手工链**（两个消费者都是"缺文件就跳过"）。
#
# 实测证据：①（2026-09-16）同一门禁**两实例并发** ⇒ **假红 36/4**（失败项全是「读到的不是自己写的
#   那一份」，与色彩策略逻辑无关）；**单独复跑**同一门禁 ⇒ **40/0**。
#   ⇒ 差异 100% 来自自并发，不是代码缺陷。
#   ②（2026-09-17）`verify-color-wiring.ps1` 自并发 ⇒ **94/4 与 98/0 交替**：FAIL **恒为日志类**断言
#   （`RunCli` 写死的 `$out/<tag>.o` 被另一实例清空），而**文件类断言仍绿**；且**两次运行的 PASS
#   总数都会变** —— 「总数变化」是识别它的**最强信号**（单看某条 FAIL 极易误判成产品回归）。
# 结论：批量跑门禁时**同一脚本一次只跑一个实例**；`verify-decision-delivery` 与
#   `run-pipeline-tests` 不得并行；确需并行，必须先把输出目录与中间文件改成按 PID/会话隔离，
#   否则并行结论一律不可采信。
#
# ── 并发验收判据（2026-09-17 裁定；**判据本身也要防假绿**）─────────────────────────
#   ① **有 `pass/fail` 计数的脚本**：同时起两个实例 ⇒ **两个都 `FAIL=0`，且 `PASS` 数完全相同**。
#      ⚠ 只判 `FAIL=0` **不够** —— 两实例各漏跑一段、都恰好 `FAIL=0` 就会**假通过**。
#   ② **无计数（表格型 / 诊断型）脚本**：判据是「**归一化运行期变化 token（GUID）后**输出**逐字相同**」。
#      ⚠ **措辞不能漏「归一化」** —— 输出里可能含运行期生成的 GUID 目录名，直接比「逐字相同」会**恒红**；
#        照着写的人会写出一条永远红的断言。归一化范围：把 `\w+_[0-9a-f]{8}` 之类的运行期 token 换掉。
#      ⚠ **该脚本输出不得含耗时/性能字段**（`ms` / `MPix/s` / `ProcessMs` / `Elapsed` / `Stopwatch` 之类），
#        否则逐字相同会因抖动**假红** ⇒ 必须先归一化再比。
#        已核实（grep 过）：`verify-colorfmt-matrix`（md5 / 字节数 / CICP 令牌）与
#        `_probe-ztf-locate`（RMS / max / 占比 / 抽样码值表）**都不含**耗时字段。
#   实测（2026-09-17）：`verify-colorfmt-matrix` 归一化后逐字相同 = **True**（8 行表）；
#     `_probe-ztf-locate` 归一化后逐字相同 = **True**（38 行）；`verify-color-peak` **25/0 ×4**（两实例各 2 次）；
#     `verify-gif-avif-framelist` **17/0/0 ×4**（两实例各 2 次，两实例各 14/15 s ⇒ 真重叠）。
$ErrorActionPreference = "Continue"

# ── 失败汇总 + 显式退出（2026-09-18 新增）────────────────────────────────────────
# ⚠⚠ **此前本脚本尾部没有 `exit`** ⇒ 它以 `$LASTEXITCODE` 退出 ⇒ 2026-09-18 实测：
#   `_probe-cjk-hardcode-scan.ps1` 返回 `exit=1`，而**运行器整体 `EXITCODE=0`**
#   ⇒ **任何**子门禁失败都不体现在退出码里（`docs/TESTING.md §6 第 86 条` 只把这件事登记成
#   #147 C 的「前置条件」；实证表明它是**今天就在生效**的活缺陷）。
#   代价实测：`cjk-hardcode` 那条红因此**从第 31 轮挂到第 58 轮**没人发现（27 轮）。
# ⇒ 现在逐项记账，尾部**显式 if/else 退出**。
#   ⚠ 必须写**完整 if/else**：宿主批量删除守卫会污染 `$LASTEXITCODE`（实测 2）⇒
#   只写 `if ($fail) { exit 1 }` 会让**全绿路径**以被污染的值退出 ⇒ 把"修假绿"修成"造假红"（§6 第 58 条）。
$failedItems = New-Object System.Collections.Generic.List[string]

# ── ⚠⚠ #147 C：仓库级单锁的**释放**（2026-09-19 实施；**获取**见下方 `$root` 之后）──────────────
# ⚠ 只在**自己写的**锁上删：锁内容里的 pid 必须等于本进程 ⇒ 防「我删了别人的锁」（§6 第 66 条同族）。
# ⚠ 删除用 **`.NET API`**（宿主批量删除守卫会污染 `$LASTEXITCODE`，见 §6 第 67 条）。
$script:gatesLockPath = ""
$script:gatesLockStream = $null
function Release-GatesLock {
  if ($script:gatesLockStream) {
    try { $script:gatesLockStream.Dispose() } catch { }
    $script:gatesLockStream = $null
  }
  if (-not $script:gatesLockPath) { return }
  try {
    if (Test-Path $script:gatesLockPath) {
      $txt = [System.IO.File]::ReadAllText($script:gatesLockPath)
      if ($txt -match '"pid"\s*:\s*(\d+)') {
        if ([int]$Matches[1] -eq $PID) { [System.IO.File]::Delete($script:gatesLockPath) }
      }
    }
  } catch { }
}

function Exit-WithSummary([string]$scope) {
  Write-Output ""
  Release-GatesLock
  if ($failedItems.Count -gt 0) {
    Write-Output ("===== 运行器汇总{0}：失败 {1} 项 =====" -f $scope, $failedItems.Count)
    $failedItems | ForEach-Object { Write-Output ("    " + $_) }
    Write-Output  "⇒ 退出码 1（2026-09-18 之前本脚本尾部无 exit ⇒ 失败会被吞掉）"
    exit 1
  }
  Write-Output ("===== 运行器汇总{0}：全部通过 =====" -f $scope)
  exit 0
}

# ── ⚠ #22（2026-09-19）：单步硬超时执行 ─────────────────────────────────────────
# 为什么必须有：本脚本原用 `Start-Process … -Wait`，**没有任何超时** ⇒ 任一步「挂死而非失败」
#   都会让**整轮永久卡住**（探针段与子脚本段都只在进程返回后才记账，唯一迹象是 mtime 停住）。
#   实测形态（2026-09-19）：把 `FfmpegRunner.RunAsync` 的 `bool heartbeat` 默认值翻成 false ⇒
#   `ServiceProbe runner` 的忙等臂永不返回 ⇒ **整轮会卡在它上面**（而脚本段
#   `verify-ffmpeg-heartbeat` 反被它自己的源码形态自检先短路，只转红不挂死）⇒ 挂死型失效必须兜。
# ⚠⚠ 2026-09-19 实测更正：本设计第一版**不足**，被 PS5.1 冒烟抓住（记录在此以免后人重踩）：
#   第一版去掉 `-Wait` 后用 `WaitForExit($ms)` 判超时、再读进程对象的退出码。实测：
#   **PS 5.1 下 `Start-Process -PassThru`（不带 `-Wait`）返回的对象，其退出码恒为 null** ——
#   补调无参 `WaitForExit()` 也**救不回来**（实测：`cmd /c exit 7`，`WaitForExit(10000)` 返回 True 后
#   仍为 null，再调无参重载仍为 null）。后果：`exit=` 打空 ⇒ 探针与脚本**每一条都被记成失败** ⇒
#   **整轮假红**（PS5.1 冒烟实测「失败 2 项」，而那两条的 PROBE RESULT 都是 pass>0 / fail=0）。
#   ⇒ 结论：**不要读进程对象的退出码**。改为「包装层」：用当前宿主把目标当**原生进程**调起，
#   由包装层把退出码变量写进一个 `.rc` 文件，本函数读那个文件。实测**双宿主同结果**：
#   PS7 与 PS5.1 各跑 4 例（正常 exe / 非零 exe / 脚本 / 超时）⇒ 退出码与超时判定完全一致。
# 五条已知陷阱与对策：
#   ① `-Wait` 没有超时 ⇒ 去掉 `-Wait`，改用 `$child.WaitForExit($ms)` 的**返回值**判超时；
#   ② ⚠ 退出码**一律从 `.rc` 文件读**，任何宿主都不读进程对象的退出码（理由见上）；
#      顺带把「`$null -ne 0` 在 PS5.1 里为 `$true`」那类假红一并消掉；
#   ③ ⚠ PS5.1 **没有** `Kill($true)` 重载（那是 .NET Core 3.0+）⇒ 先用系统自带的进程树杀法
#      （该命令名按 `'task' + 'kill'` 拼接书写：下方形态自检要求它**恰好出现 1 次**且只允许在
#      代码里，注释里也写拼接形式，以免注释把该针满足掉），失败再退回无参 `.Kill()`。
#   ④ 超时后必须 **fail-loud**：响亮日志 + 计入 `$failedItems`，绝不静默继续。且超时后输出文件是
#      **半截**的 ⇒ 调用方**必须抑制汇总解析**，否则会打出「看起来跑完了且干净」的假汇总。
#   ⑤ 双宿主可用：只用 5.1 也有的语法与重载（无三元 / 空合并 / 空条件 / 管道链；
#      `verify-ps-compat.ps1` 会把本文件正文一起扫）。
# ⚠ 进程对象在本函数内**一律命名 `$child`**（不沿用 `$p` / `$proc`）—— `Test-RunnerShape` 据此把
#   「不得出现裸退出码取法」做成**全文件计数 == 0**，无需切分作用域（少一处切分就少一类自我命中）。
function Invoke-StepWithTimeout {
  param(
    [string]$FilePath,
    [string[]]$Arguments,
    [string]$OutFile,
    [string]$ErrFile,
    [int]$TimeoutSec,
    [string]$Label,
    [string]$HostExe
  )
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  $rcFile = $OutFile + ".rc"
  if (Test-Path $rcFile) { [System.IO.File]::Delete($rcFile) }
  # 包装层内层命令：把目标当**原生进程**调起（原生子进程的退出码变量才可靠），跑完把退出码写进 rc 文件。
  # ⚠ 目标与每个参数都做**单引号转义**（内层单引号翻倍），避免参数里的引号/空格改变命令结构。
  $q = foreach ($a in $Arguments) { "'" + ($a -replace "'", "''") + "'" }
  $inner = "& '" + ($FilePath -replace "'", "''") + "' " + ($q -join ' ') + `
    "; [System.IO.File]::WriteAllText('" + ($rcFile -replace "'", "''") + "', [string]`$LASTEXITCODE)"
  $child = $null
  $timedOut = $false
  try {
    $child = Start-Process -FilePath $HostExe -ArgumentList @('-NoProfile', '-Command', $inner) -NoNewWindow -PassThru `
      -RedirectStandardOutput $OutFile -RedirectStandardError $ErrFile
  } catch {
    Write-Output ("[{0}] ⚠ Start-Process 抛异常：{1}" -f $Label, $_.Exception.Message)
    return @{ ExitCode = -1; TimedOut = $false; ElapsedSec = [math]::Round($sw.Elapsed.TotalSeconds, 1) }
  }
  $exited = $false
  try { $exited = $child.WaitForExit($TimeoutSec * 1000) } catch { $exited = $false }
  if (-not $exited) {
    $timedOut = $true
    Write-Output ("[{0}] ⚠⚠ 超时 {1}s ⇒ 击杀进程树（挂死型失效，响亮报红；不是产品回归）" -f $Label, $TimeoutSec)
    try { $null = & taskkill /PID $child.Id /T /F 2>&1 } catch { }
    try { $null = $child.WaitForExit(5000) } catch { }
    if (-not $child.HasExited) {
      try { $child.Kill() } catch { }
      try { $null = $child.WaitForExit(5000) } catch { }
    }
  }
  # ⚠ 退出码**只**从 rc 文件读（进程对象那条路在 PS5.1 下不可用，见函数头注释）；文件缺失 ⇒ -1（响亮失败）
  $rc = -1
  if (Test-Path $rcFile) {
    try { $rc = [int]([System.IO.File]::ReadAllText($rcFile).Trim()) } catch { $rc = -1 }
  }
  return @{ ExitCode = $rc; TimedOut = $timedOut; ElapsedSec = [math]::Round($sw.Elapsed.TotalSeconds, 1) }
}

# ── ⚠ 第 15 针的落点：**共享读 + 短重试**（2026-09-19，gp-1 查出、team-lead 采纳）─────────────
# 为什么必须有：探针 / 子脚本可能派生**孙进程**，父进程返回后**孙进程仍持有继承的 stdout 句柄**
#   ⇒ 输出文件短暂被占用 ⇒ `[System.IO.File]::ReadAllText` 抛**非终止性** `MethodInvocationException`。
#   原写法的两个后果（实测，`[ipc]` 行）：
#     ① 异常**只打印、不记账**（`exit=0`、未进 `$failedItems`）；
#     ② ⚠ **`$txt` 保留上一轮的值** ⇒ `$sum` 变成**上一条的别名**
#        （实测 `[ipc]` 打出 `pass=6` = `encoding` 的读数；`fail=0` 时无害，但**真失败会被上一轮的 `fail=0` 掩盖**）。
#   本函数：① `FileShare.ReadWrite` 打开（允许别人同时持有写句柄 ⇒ 从根上避免该竞态）；
#   ② 失败重试 5 次 × 200 ms；③ 仍失败 ⇒ 返回 `$null` —— **由调用方显式记 FAIL 并清空 `$txt`**，
#   **绝不允许沿用上一轮的值**（这条纪律由第 15 针钉住）。
function Read-TextShared([string]$path) {
  for ($i = 1; $i -le 5; $i++) {
    $fs = $null; $sr = $null
    try {
      $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
      $sr = New-Object System.IO.StreamReader($fs)
      return $sr.ReadToEnd()
    } catch {
      if ($i -lt 5) { Start-Sleep -Milliseconds 200 }
    } finally {
      if ($sr) { $sr.Dispose() }
      if ($fs) { $fs.Dispose() }
    }
  }
  return $null
}

# ── ⚠ #22 / #23 / 第 15·16·17 针：运行器**自身形态**自检（17 针）──────────────────
# 为什么必须有：上面「退出码只从包装层写出的 rc 文件读」这条纪律，若将来有人回退成「读进程对象的
#   退出码」，PS5.1 下它恒 null ⇒ **整轮每一条都假红**，而现象看起来像产品回归 ⇒ 必须钉成形态断言。
# 17 针：① helper 定义点 ==1；② 调用点 ==2；③ 带超时调用点 ==2（两个调用点都必须把超时当变量传，
#   不得写字面量）；④⑤ 裸取退出码（`$p.` / `$proc.`）计数 ==0；⑥ 进程对象的退出码取法计数 ==0；
#   ⑦ 包装层写 rc 点 ==1；⑧ helper 读 rc 点 ==1；⑨ 进程树杀法 token ==1；⑩ 超时抑制点 ==2；
#   ⑪ 探针超时**默认值**逐字钉住 ==1（定稿 = **120**，变异方向 = 改成 60 ⇒ 必须红）；
#   ⑫ 脚本超时**默认值**逐字钉住 ==1（= **480**，变异方向 = 改成 420 ⇒ 必须红）；
#   ⑬ `$tableOnly` **条数** ==4；⑭ `$noSelfSummary` **条数** ==7；
#   ⑮ 读失败分支 ==2 且 共享读引用 ==3（异常路径**不得沿用上一轮 `$txt`**）；
#   ⑯ 运行器**自身指纹**：打印点 ==2（启动 + 收尾）· 自漂移复核引用 ==3 · 归属不明告警 ==2；
#   ⑰ 清单**散文条数** == `$expectedScriptCount` 常量（常量定义点 ==1；三种散文形态各命中 ≥1 且数字相同）。
# ⚠ ⑯ 的理由（2026-09-19 #28，gp-1 与 gp-3 各自独立查出）：`Check-SrcDrift` **只覆盖
#   `src/FfmpegGui` 与 `tests/ServiceProbe`** ⇒ **运行器自身既不在覆盖范围、也不哈希自身**。
#   有人在整轮进行中改运行器 ⇒ 那一轮读数**归属不明**（跑的是旧版还是新版？）而**无任何告警**。
#   ⚠ 不是假想：2026-09-19 实测运行器在 12:37:02 → :16 → :37 → :49 被改**四次**（671 → 734 行），
#     发现方式只是**手工测指纹**。⚠ 与 STALE 闸分工不同：STALE 管**被测产物**，本条管**运行器自己**。
# ⚠ ⑪⑫ 的理由：本项目已有先例 —— 心跳默认 `120` 由「**逐字锁**」防调参
#   （见 docs/HANDOVER.md：旧写法不钉 `;` ⇒ 注释里写一句、或改成 `= 1200` 都能假绿）。
#   两个超时默认值同样**不能没有锁**：否则任何人都能按自己的基准**悄悄改**（本次即为「60 vs 120」
#   往返多轮的根因）。⑫ 是 2026-09-19 补的：⑪ 先只锁了探针段，脚本段成为**已知的不对称**，
#   既然已在注释里登记，就一并锁掉 —— 同类不变量不该只锁一半。
# ⚠ ⑬⑭ 的理由（#23 的**全部意义**）：这两个数组就是**防读者误读**用的；删掉数组里一条 ⇒ 该条的
#   标签**静默消失** ⇒ 读者又会误读 ⇒ 与「防误读」的初衷相反。⚠ 这两针**在数组字面量内部计数**
#   （先正则取出 `@( … )` 区间，再数区间内的 `.ps1'`）⇒ **天然不受本函数自身源码影响**，
#   不存在「针自己的字面量把自己满足」的同义反复问题。
# ⚠ ⑮ 的理由（2026-09-19，gp-1 查出、team-lead 采纳）：读输出文件抛**非终止性**异常时，
#   原写法只打印、**不记账**，且 `$txt` **保留上一轮的值** ⇒ `$sum` 变成**上一条的别名**
#   （实测 `[ipc]` 打出 `pass=6` = `encoding` 的读数）。探针段 `$sum` **参与记账**
#   ⇒ **真失败会被上一轮的 `fail=0` 掩盖**（结构性假绿通道）；脚本段 `$sum` 只回显 ⇒ 后果是**误导显示**。
#   ⇒ 修法 = 共享读（`FileShare.ReadWrite`）+ 短重试 + **读失败显式记 FAIL 并清空 `$txt`**。
# ⚠ ⑰ 的理由（2026-09-24）：**运行时那条条数锁只比「数组 vs 常量」** ⇒ 散文里的条数**从来没有锁**。
#   实证：接第 53 条时我改完了常量与数组、**漏改两处散文**，之后跑的整轮**照常全绿、无人报错**
#   （散文是读者当权威口径引用的那个数 ⇒ 它漂移比常量错更害人，因为没人会去复核注释）。
#   ⚠ 本针**不能**改成"扫所有出现的条数"——那会撞上历史演进链里的 `42 / 48 / 52` 等**旧读数**
#   （历史链按规矩逐字保留）⇒ 只钉**三种"现量陈述"形态**，新增现量陈述时必须把形态加进这一针。
# ⚠ 本函数读**运行器自身原文（含注释，不剥注释 —— 故意比 ps-compat 更严）**，所以**所有针都必须
#   用拼接构造**：否则针的字面量会被本函数自己的源码满足 ⇒ 断言退化成同义反复
#   （「不得出现」型恒红、「必须出现」型恒绿）。⚠ 同理，注释里也不得写出那些针的**连续**形式。
function Test-RunnerShape {
  $t = [System.IO.File]::ReadAllText($PSCommandPath)
  $bad = New-Object System.Collections.Generic.List[string]
  $nBareP = '$' + 'p.ExitCode'
  $nBareProc = '$' + 'proc.ExitCode'
  $nChildExit = '$' + 'child.ExitCode'
  $nWr = 'Write' + 'AllText'
  $nRd = 'ReadAllText($' + 'rcFile'
  $nDef = 'function Invoke-Step' + 'WithTimeout'
  $nKillTok = 'task' + 'kill'
  $nSupTok = '汇总解析' + '已抑制'
  $cDef = ([regex]::Matches($t, [regex]::Escape($nDef))).Count
  if ($cDef -ne 1) { $bad.Add("函数定义点 = $cDef（须 1：只允许 helper 的定义行）") }
  $cCall = ([regex]::Matches($t, '=\s*Invoke-StepWithTimeout\s')).Count
  if ($cCall -ne 2) { $bad.Add("调用点 = $cCall（须 2：探针段 1 + 子脚本段 1）") }
  $cTo = ([regex]::Matches($t, '-TimeoutSec\s+\$')).Count
  if ($cTo -ne 2) { $bad.Add("带超时的调用点 = $cTo（须 2：两个调用点都必须把超时当变量传，不得写字面量）") }
  if ($t.IndexOf($nBareP) -ge 0) { $bad.Add("出现裸退出码取法（A）—— PS5.1 下恒 null ⇒ 全轮假红") }
  if ($t.IndexOf($nBareProc) -ge 0) { $bad.Add("出现裸退出码取法（B）—— 同上") }
  if ($t.IndexOf($nChildExit) -ge 0) { $bad.Add("出现进程对象的退出码取法 —— PS5.1 下它恒 null ⇒ 全轮假红") }
  $cWr = ([regex]::Matches($t, [regex]::Escape($nWr))).Count
  if ($cWr -ne 1) { $bad.Add("包装层写 rc 点 = $cWr（须 1：退出码的唯一来源）") }
  $cRd = ([regex]::Matches($t, [regex]::Escape($nRd))).Count
  if ($cRd -ne 1) { $bad.Add("helper 读 rc 点 = $cRd（须 1：否则退出码恒 -1）") }
  $cKill = ([regex]::Matches($t, [regex]::Escape($nKillTok))).Count
  if ($cKill -ne 1) { $bad.Add("进程树杀法 token = $cKill（须 1：PS5.1 无 Kill(true) 重载的兜底）") }
  $cSup = ([regex]::Matches($t, [regex]::Escape($nSupTok))).Count
  if ($cSup -ne 2) { $bad.Add("超时抑制点 = $cSup（须 2：两个调用段各一，只删一个也能发现）") }
  $nProbeTo = '[int]$ProbeTimeoutSec' + ' = 120'
  $cPTo = ([regex]::Matches($t, [regex]::Escape($nProbeTo))).Count
  if ($cPTo -ne 1) { $bad.Add("探针超时默认值针 = $cPTo（须 1：默认值必须逐字钉住，防按各自基准悄悄调参）") }
  $nScriptTo = '[int]$ScriptTimeoutSec' + ' = 900'
  $cSTo = ([regex]::Matches($t, [regex]::Escape($nScriptTo))).Count
  if ($cSTo -ne 1) { $bad.Add("脚本超时默认值针 = $cSTo（须 1：同第 11 针，两个默认值都不许悄悄改）") }
  # ⚠ ⑬⑭ 只数**数组字面量区间内**的条目 ⇒ 不受本函数自身源码影响（见上方头部注释）。
  # ⚠⚠ 必须用 `(?ms)`：`(?s)`（dotall）才能让 `(.*?)` 跨行 —— `$noSelfSummary` 声明**跨 3 行**，
  #   只写 `(?m)` 时该正则**永不匹配** ⇒ 计数恒 -1 ⇒ **未变异的原文也会拒绝跑**（控制组实测抓到）。
  $mTable = [regex]::Match($t, '(?ms)^[ \t]*\$tableOnly\s*=\s*@\((.*?)\)')
  $cTable = if ($mTable.Success) { ([regex]::Matches($mTable.Groups[1].Value, "\.ps1'")).Count } else { -1 }
  if ($cTable -ne 4) { $bad.Add("tableOnly 条数 = $cTable（须 4：删一条 ⇒ 该条标签静默消失 ⇒ 读者又会误读）") }
  $mNoSum = [regex]::Match($t, '(?ms)^[ \t]*\$noSelfSummary\s*=\s*@\((.*?)\)')
  $cNoSum = if ($mNoSum.Success) { ([regex]::Matches($mNoSum.Groups[1].Value, "\.ps1'")).Count } else { -1 }
  if ($cNoSum -ne 7) { $bad.Add("noSelfSummary 条数 = $cNoSum（须 7：同上，标签静默消失即假绿）") }
  # ⑮ 异常路径不得沿用上一轮 `$txt`（2026-09-19，gp-1 查出、team-lead 采纳）。两个计数：
  #   ① 读失败分支标记 ==2（探针段 + 子脚本段各一）—— 少一个 ⇒ 那一段读失败时会**沿用上一轮 `$txt`**；
  #   ② 共享读函数引用 ==3（定义 1 + 两处调用）—— 回退成裸 `[System.IO.File]::ReadAllText($o)` 会掉到 2。
  $nReadFail = '输出文件读取失败' + '，汇总不可得'
  $cReadFail = ([regex]::Matches($t, [regex]::Escape($nReadFail))).Count
  $nSharedRead = 'Read-Text' + 'Shared'
  $cSharedRead = ([regex]::Matches($t, [regex]::Escape($nSharedRead))).Count
  if ($cReadFail -ne 2 -or $cSharedRead -ne 3) {
    $bad.Add("读失败分支 = $cReadFail（须 2）· 共享读引用 = $cSharedRead（须 3：定义 1 + 调用 2）⇒ 否则读失败会沿用上一轮 `$txt`（假绿通道）")
  }
  # ⑯ 运行器**自身指纹**（2026-09-19 #28）。三个计数：
  #   ① 自指纹打印点 ==2（启动 + 收尾）—— 少一个 ⇒ 「跑期间被改」无从比对；
  #   ② 自漂移复核引用 ==3（定义 1 + 两处退出点）—— 少一个 ⇒ 那条退出路径不检自身；
  #   ③ 归属不明告警 ==2（响亮 `[WARN]` + 计入 `$failedItems`）—— 少一个 ⇒ 要么不响、要么不记账。
  $nSelfPrint = 'runner-self-' + 'sha256='
  $cSelfPrint = ([regex]::Matches($t, [regex]::Escape($nSelfPrint))).Count
  $nSelfDrift = 'Check-Self' + 'Drift'
  $cSelfDrift = ([regex]::Matches($t, [regex]::Escape($nSelfDrift))).Count
  $nSelfWarn = '本轮读数' + '归属不明'
  $cSelfWarn = ([regex]::Matches($t, [regex]::Escape($nSelfWarn))).Count
  if ($cSelfPrint -ne 2 -or $cSelfDrift -ne 3 -or $cSelfWarn -ne 2) {
    $bad.Add("自指纹打印点 = $cSelfPrint（须 2）· 自漂移复核引用 = $cSelfDrift（须 3）· 归属不明告警 = $cSelfWarn（须 2）⇒ 否则运行器自身被改时无法检出（读数归属不明）")
  }
  # ⑰ 清单**散文条数**与常量对账（2026-09-24）。为什么必须有：条数锁只管「数组 vs 常量」，
  #   **散文一行都不在锁里** ⇒ 接第 53 条时我漏改了两处散文，整轮照常全绿、无人报错
  #   （而散文恰恰是读者当"权威口径"引用的那个数，它漂移比常量错更害人）。
  #   判据 = 常量定义点恰好 1 处；下列三种形态各须命中 ≥1 且其中数字 == 常量。
  #   ⚠ token 一律**拼接**构造（本函数读的是自己的源码），注释里也不得写出连续形式；
  #     新增别的条数散文表述时，把它的形态加进下面这个列表，否则那处仍然无锁。
  $nConstLine = 'expectedScriptCount' + ' = (\d+)'
  $nProseA = '清单条数' + '以实测为准 = (\d+) 条'
  $nProseB = '受管基线：脚本清单 = \*\*' + '(\d+)\*\* 条'
  $nProseC = '全跑 ' + '(\d+) 条'
  $mConst = @([regex]::Matches($t, $nConstLine))
  $proseBad = @()
  if ($mConst.Count -ne 1) {
    $proseBad += "常量定义点 = $($mConst.Count)（须 1：写两处则谁生效取决于顺序）"
  } else {
    $constVal = [int]$mConst[0].Groups[1].Value
    $proseSpec = @(
      @{ Name = '清单条数…以实测为准'; Pat = $nProseA },
      @{ Name = '受管基线：脚本清单';  Pat = $nProseB },
      @{ Name = '全跑 N 条';          Pat = $nProseC }
    )
    foreach ($spec in $proseSpec) {
      $ms = @([regex]::Matches($t, $spec.Pat))
      if ($ms.Count -lt 1) { $proseBad += "$($spec.Name) 命中 0（散文被删或改了形态 ⇒ 该处失去保护）" }
      foreach ($m in $ms) {
        if ([int]$m.Groups[1].Value -ne $constVal) { $proseBad += "$($spec.Name) = $($m.Groups[1].Value)（常量 = $constVal）" }
      }
    }
  }
  if ($proseBad.Count -gt 0) { $bad.Add("散文条数与常量不符：" + ($proseBad -join ' · ')) }
  return $bad
}

# ── ⚠ #28（2026-09-19）：运行器**自身指纹**（防「跑期间自己被改 ⇒ 读数归属不明」）──────────
# 为什么必须有：`Check-SrcDrift` **只覆盖两个目录**（`src/FfmpegGui` 与 `tests/ServiceProbe`）
#   ⇒ **运行器脚本自身既不在覆盖范围内、也不哈希自身**。若有人在**整轮进行中**改运行器，
#   那一轮的读数**归属不明**（跑的是旧版还是新版？），而现有机制**无任何告警**。
#   ⚠ **不是假想**：2026-09-19 实测运行器在 12:37:02 → :16 → :37 → :49 被改了**四次**（671 → 734 行），
#     发现方式只是**手工测指纹**，**不是机制检出**。
#   ⚠ 与「新鲜度闸（STALE）」分工不同：STALE 管**被测产物**；本条管**运行器自己**。
#   ⚠ 与「正面 ④ 取证前先证明对象已静止」互补：那条是**取证侧**（人做），本条是**机制侧**（运行器自己做）。
# ⚠ 日志里**出现 hash 是预期的**（启动与收尾各打一行自指纹）—— 这是**有意打印**，
#   不是异常；将来不要把它当噪声删掉（删了就失去可核对性）。
function Get-SelfFingerprint {
  $h = (Get-FileHash $PSCommandPath -Algorithm SHA256).Hash
  $i = Get-Item $PSCommandPath
  return @{ Sha = $h.Substring(0, 16); Lines = @(Get-Content $PSCommandPath).Count; Bytes = $i.Length }
}

# pwsh -File 下 `-Probes a,b,c` 只会作为**一个**参数传入（不会被拆成数组）⇒ 曾经整批空跑只留 exit=2。
$probeList = @($Probes -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($probeList.Count -eq 0) { Write-Output "无探针 mode，拒绝空跑"; exit 2 }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $root

# ── 前置：派生 fixture 的**按需补建**（2026-09-22；`HANDOVER §33` 项 3）─────────────────────────
# 为什么需要：`tests/output/**` **不受版本控制**（`.gitignore:78`）⇒ **干净检出上**派生 fixture 全缺
#   ⇒ 相关门禁会**跳过**部分断言（现已显示 `SKIP=` 计数，但**仍然是没跑**）。
# ⚠⚠ **必须在取锁之前跑**：两个生产者都用**同一把**仓库级锁（`#147 C`），而下面几行就要取锁
#   ⇒ 把它们放进脚本循环内**必然 exit 9**。
# ⚠ **只在确实缺时跑**（正常机器上零开销）；两个生产者各查一个代表性产物，且**有先后依赖**
#   （`ensure-fixtures` 要读 `sources/src_8bit.png`）⇒ 数组顺序不可颠倒。
# ⚠ **刻意不取退出码**：本文件的自形态检查把「进程对象的退出码取法」钉成**全文件计数 == 0**
#   （PS5.1 下恒 null ⇒ 全轮假红）⇒ 这里改用**语义判据**：跑完看**素材是否到位**。
#   若生产者因「锁被别的实例占用」而 `exit 9` ⇒ 本轮**中止**（并发禁止，语义与下方一致）；
#   其它失败 ⇒ 只警告不中止 —— 受影响的门禁会各自以可见的 `SKIP` 记账。
$fixtureProducers = @(
    @{ Need = "$root/tests/output/sources/src_8bit.png"; Producer = 'generate-sources.ps1' },
    @{ Need = "$root/tests/output/validate/src_hdr.png"; Producer = 'ensure-fixtures.ps1'  }
)
foreach ($fp in $fixtureProducers) {
    if (Test-Path $fp.Need) { continue }
    $sp = Join-Path $PSScriptRoot $fp.Producer
    if (-not (Test-Path $sp)) {
        Write-Output "⚠ 缺派生素材 $($fp.Need)，且找不到生产者 $sp ⇒ 相关断言会以 SKIP 记账"
        continue
    }
    Write-Output "⚠ 缺派生素材 $($fp.Need) ⇒ 先跑 $($fp.Producer)（干净检出/素材被清后的一次性补建）"
    & $sp
    if (Test-Path $fp.Need) { Write-Output "✅ 已补建 $($fp.Need)" }
    else { Write-Output "⚠ 跑完仍缺 $($fp.Need) ⇒ 相关断言会以 SKIP 记账（不中止本轮）" }
}

# ── ⚠⚠ #147 C：仓库级单锁（2026-09-19 实施；方案与验收见 docs/TESTING.md §6 第 86 条）──────────
# 目的：消除成因 1（同名脚本并发）与成因 2（共享 fixture 并发读写）的**最大窗口**（两个 runner 实例并发），
#   并顺带消除成因 7（_gate_script_*.txt 的 write-write）。
# ⚠⚠ **本锁不声称覆盖**（三条，逐条来自第 86 条的「覆盖盲区」）：
#   ① **构建**：dotnet build / MSBuild **不受**本锁约束 —— §6 第 80 条 ② 那 15 个 MSB3026 正是
#      「构建 × 并发跑 ServiceProbe」；要覆盖得把锁加到构建入口（pack.ps1 / build_tools.ps1）⇒ **属另一条线**。
#   ② 有人**直接跑单个** verify-*.ps1（不走运行器）⇒ **完全绕开锁**。
#   ③ **进程外手段**（手工往 validate/ 拷文件、同步盘/编辑器触碰该目录等）⇒ 锁管不到。
#   ⚠ 刻意**不**给 12+ 个 verify-*.ps1 各加锁：那会把「共享文件面」从 1 个扩到 12+ 个
#     （每个都成了「可能被别人并发改」的文件）⇒ 反而**制造**成因 6。
# 行为（三条都是**刻意**的取舍）：
#   · **被占用 ⇒ 明确报错 + exit 9**（不静默跳过、不等待、不超时后继续）；9 与现有 0/1 及探针 90/91/92 都不撞。
#   · **stale 锁不自动删**：读锁里的 pid，Get-Process 失败也只把措辞改成「疑似残留锁」并提示**手工**删。
#     理由：自动删会引入「我删了别人的锁」的**新竞态** ⇒ 让**人**决定比「自动做对」更安全。
#   · 释放走 Exit-WithSummary（所有退出路径都经过它）⇒ 正常结束删锁；**异常中断则残留**（符合上一条）。
# ⚠ 实现要点：**写入后立即 Dispose 句柄** —— 锁的**存在性**由 CreateNew 语义保证（文件已存在 ⇒ 抛异常），
#   而**不**靠长期持有独占句柄；否则别的实例**读不到锁内容** ⇒ stale 检测（读 pid）**结构性失效**。
$script:gatesLockPath = "$root/tests/output/.gates.lock"
try {
  $script:gatesLockStream = [System.IO.File]::Open($script:gatesLockPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
} catch {
  $holder = "(锁内容读不到)"
  try { $holder = [System.IO.File]::ReadAllText($script:gatesLockPath) } catch { }
  $stale = $false
  if ($holder -match '"pid"\s*:\s*(\d+)') {
    $hpid = [int]$Matches[1]
    $hproc = $null
    try { $hproc = Get-Process -Id $hpid -ErrorAction SilentlyContinue } catch { $hproc = $null }
    if (-not $hproc) { $stale = $true }
  }
  Write-Output ""
  Write-Output "===== 运行器拒绝启动：仓库级锁被占用（#147 C）====="
  Write-Output "  锁文件：$script:gatesLockPath"
  Write-Output "  持有者：$holder"
  if ($stale) {
    Write-Output "  ⚠ 持有者进程已不存在 ⇒ **疑似残留锁**。请**手工**确认后删除该文件再重试（本脚本不会自动删）。"
  } else {
    Write-Output "  ⚠ 另一个门禁运行器正在跑 ⇒ 本轮**不启动**（并发会互删共享 fixture ⇒ 读数不可信）。"
  }
  Write-Output "  ⚠ 本锁**不**覆盖：构建（dotnet/MSBuild）、直接跑单个 verify-*.ps1、进程外手段。"
  exit 9
}
# ⚠ cmdline 必须**截断**（2026-09-19 实施期发现）：`[Environment]::CommandLine` 在**宿主注入环境**下
#   会返回**整段沙箱 wrapper 脚本**（实测锁文件因此膨胀到 **20640 B**）。功能无碍（JSON 仍合法、
#   `pid` 仍在最前 ⇒ stale 检测不受影响），但锁文件应当保持**小而易读**（它是给人看的现场证据）。
$gatesCmdRaw  = ([Environment]::CommandLine) -replace '\s+', ' '
$gatesCmdTrim = $gatesCmdRaw.Substring(0, [Math]::Min(300, $gatesCmdRaw.Length))
$gatesLockObj = [ordered]@{
  pid       = $PID
  host      = ("{0} {1}" -f $PSVersionTable.PSVersion, $PSVersionTable.PSEdition)
  startedAt = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
  script    = "_run-step3-gates.ps1"
  cmdline   = $gatesCmdTrim
}
$gatesLockJson  = ($gatesLockObj | ConvertTo-Json -Compress)
$gatesLockBytes = [System.Text.Encoding]::UTF8.GetBytes($gatesLockJson)
$script:gatesLockStream.Write($gatesLockBytes, 0, $gatesLockBytes.Length)
$script:gatesLockStream.Flush()
$script:gatesLockStream.Dispose()
$script:gatesLockStream = $null
Write-Output "gates-lock=$script:gatesLockPath (pid=$PID)"

$env:FFMPEGGUI_FFMPEG_DIR = "$root/publish/PLAN/ffmpeg-full"

# ── 起手清 perl locale 环境变量（2026-09-27，#51 第 0 项）─────────────────────────────
# 随包 exiftool 是 perl 打的：只要子进程继承 `LC_ALL`/`LANG` = C.UTF-8，它每次往 **stderr** 喷
# 4-6 行 "Setting locale failed" warning，而**退出码仍是 0**。全仓有 43 处"合并 stdout+stderr"取数点，
# 其中 18 处读的是 **FfmpegGui 自己的日志** —— 而产品 `ExifToolService.cs:906-909` 把子进程 stderr
# 原样转投进应用日志 ⇒ 那 18 处**在脚本侧分流救不了**，只能在源头掐掉。
# ⚠ 这条**不替代**逐点改造（"exiftool 起不来 / 文件不存在 ⇒ 两路同为不可用"是另一回事，
#   会造假绿，登记在 `tests/output/t51` 白名单第 1-9 项）。
foreach ($v in @('LC_ALL', 'LANG', 'LC_CTYPE')) { Remove-Item "Env:$v" -ErrorAction SilentlyContinue }
# 打印一行，让"这一轮是在干净环境下跑的"成为可追溯读数（与 `[gate] exe=` 同族）。
Write-Output "env-cleaned=LC_ALL,LANG,LC_CTYPE (exiftool 的 perl locale warning 源头掐掉；本轮起有效)"
$pr = "$root/tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
if (-not (Test-Path $pr)) { $pr = "$root/tests/ServiceProbe/bin/Debug/net11.0/win-x64/ServiceProbe.exe" }
# ⚠ 2026-09-21（TESTING.md 第 84 条处置建议 ①）：**回退是静默的** —— 门禁可能测的
#   不是你以为的那个二进制。⇒ 一律**打印被测 exe 与构建类型**，让读数可追溯。
Write-Output ("[gate] exe=" + $pr + $(if ($pr -like '*[/\]Debug[/\]*') { " (Debug fallback)" } else { " (Release)" }))
Write-Output "probe-host=$pr"

# ── 二进制新鲜度闸（2026-09-15 新增）───────────────────────────────────────────
# 探针优先用 Release 产物；若 Release 比源码旧，整批结论会**同时**假红与假“未执行”，
# 而且输出看起来完全正常。实测：旧 exe 缺 encoding/ipc 两个 mode ⇒ exit=2「未知 mode」；
# 旧 FfmpegGui ⇒ runner 两条断言转红（退出码 1 / 0.2s，看着像逻辑坏了，其实是旧码）。
# 结论：宁可拒绝跑，也不产出不可信的基线。
# ⚠ 必须**按项目配对**比较：FfmpegGui.dll 只在 src 改动时才更新，若拿它与 ServiceProbe 的
#   Program.cs 比，只改探针就会永久误报 STALE（实测踩到过）。
$binDir = Split-Path $pr -Parent
function Get-NewestSrc([string]$p) {
  Get-ChildItem -Path $p -Recurse -File -Include *.cs, *.csproj, *.json -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
}
$guiSrc = Get-NewestSrc "$root/src/FfmpegGui"
$prbSrc = Get-NewestSrc "$root/tests/ServiceProbe"
$guiDll = Get-Item (Join-Path $binDir 'FfmpegGui.dll') -ErrorAction SilentlyContinue
$prbExe = Get-Item $pr -ErrorAction SilentlyContinue
# ── 2026-09-24 补两对配对（此前 STALE 只管两对 ⇒ 另两个"真正被执行的产物"靠手工）──────────
#   ① `UiTestHost.exe`：**5** 条 UI 门禁（ui-host / param-matrix / strategy-map / param-defects /
#      cli-strict）跑的就是它（清单内引用数按 AST 实测）。此前只重建 src+ServiceProbe 时，这 5 条会拿
#      **陈旧宿主**跑出「绿」—— 运行器自己在清单注释里把这条登记成"未覆盖缺口 ⇒ 靠手工"，现在补上。
#   ② `src/FfmpegGui/bin/**/FfmpegGui.exe`：清单内 **28** 条端到端门禁执行的是这个 **exe**
#      （AST 取 `$scriptList` 后逐条扫引用，2026-09-24 实测；原注释凭印象写"24 条"已作废 ⇒ 见 §6 第 106 条）。
#      ⚠ 它补的**不是**"dll 新、exe 旧"那种边角状态，而是**这个文件此前根本不在任何判据里**：
#      旧配对只比探针 bin 目录下那份 `FfmpegGui.dll`（`$guiDll`，另一条构建输出路径上的副本），
#      而 28 条门禁执行的是 `src/**/win-x64/FfmpegGui.exe` —— **不同目录、不同文件**。
#      ⚠ 别把这条读成"exe 与 dll 的 mtime 会各自漂移"：本轮未做该实验，也**不需要**它成立，
#      缺判据这件事本身就够严重（§6 第 50 条 ⑤ 已经因为"闸看不到子脚本 exe"赔过一次整批数字）。
#   ⚠ 本闸的语义边界（不要高估）：mtime 配对只回答"**产物不比源码旧**"，**不**回答"这次改动真的进了产物"
#      （增量构建可能保留旧 mtime ⇒ 要证后者得拿 dll 的 SHA 与参照对撞）。见 §6 第 106 条末段。
$uiSrc  = Get-NewestSrc "$root/tests/UiTestHost"
$uiExe  = Get-Item "$root/tests/UiTestHost/bin/Release/net11.0/win-x64/UiTestHost.exe" -ErrorAction SilentlyContinue
$guiExe = Get-Item "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe" -ErrorAction SilentlyContinue
$stale = @()
if ($guiSrc -and $guiDll -and $guiDll.LastWriteTime -lt $guiSrc.LastWriteTime) {
  $stale += ("FfmpegGui.dll {0} < src {1}（{2}）" -f $guiDll.LastWriteTime, $guiSrc.LastWriteTime, $guiSrc.Name)
}
if ($guiSrc -and $guiExe -and $guiExe.LastWriteTime -lt $guiSrc.LastWriteTime) {
  $stale += ("FfmpegGui.exe(端到端门禁实际执行的产物) {0} < src {1}（{2}）" -f $guiExe.LastWriteTime, $guiSrc.LastWriteTime, $guiSrc.Name)
}
if ($prbSrc -and $prbExe -and $prbExe.LastWriteTime -lt $prbSrc.LastWriteTime) {
  $stale += ("ServiceProbe.exe {0} < src {1}（{2}）" -f $prbExe.LastWriteTime, $prbSrc.LastWriteTime, $prbSrc.Name)
}
# ⚠ UiTestHost **只与自己的源码比**：它引用 FfmpegGui，但 src 侧的陈旧由上面两对负责；
#   拿 `tests/UiTestHost` 的源码去比 src 的 mtime 会造出与产品无关的假 STALE。
if ($uiSrc -and $uiExe -and $uiExe.LastWriteTime -lt $uiSrc.LastWriteTime) {
  $stale += ("UiTestHost.exe {0} < tests/UiTestHost 源码 {1}（{2}）" -f $uiExe.LastWriteTime, $uiSrc.LastWriteTime, $uiSrc.Name)
}
# ── 第 ⑤ 对（2026-09-30）：`GainMapTestHost.exe` ↔ `tests/GainMapTestHost/**` ─────────────
#   动机就是本次要处理的遗留：该宿主的套件此前**不被任何门禁执行**（运行器自己在下面一行登记着这件事），
#   于是 09-20「SDR ⇒ 普通 JPEG」裁定作废的 4 条期望在树里**红了 10 天**没人被拦。
#   现已接成清单第 60 条 `verify-gainmap-host.ps1` ⇒ 它执行的产物必须进新鲜度闸，
#   否则该门禁会拿**陈旧宿主**报绿（与 09-24 给 UiTestHost 补对同族）。
$gmSrc = Get-NewestSrc "$root/tests/GainMapTestHost"
$gmExe = Get-Item "$root/tests/GainMapTestHost/bin/Release/net11.0/win-x64/GainMapTestHost.exe" -ErrorAction SilentlyContinue
if ($gmSrc -and $gmExe -and $gmExe.LastWriteTime -lt $gmSrc.LastWriteTime) {
  $stale += ("GainMapTestHost.exe {0} < tests/GainMapTestHost 源码 {1}（{2}）" -f $gmExe.LastWriteTime, $gmSrc.LastWriteTime, $gmSrc.Name)
}
# ── 2026-10-01 补：**不存在**也必须拒（此前只判"旧"）──────────────────────────────────
#   动因是本轮实测到的假绿链：`bin/Release` 的产物会被外部 `dotnet build`/清理整时段打回，而 `bin/`
#   在 .gitignore 里 ⇒ 这种打回**git 看不见**；35 个子脚本的 exe 解析是「Release 缺 ⇒ 静默换 Debug」，
#   且那行构建类型标注用的是 `'*\Debug\*'` 去比**正斜杠拼出来的路径** ⇒ 恒假，日志反而写 "(Release)"。
#   实测读数：`verify-gainmap-managed.ps1` 报 PASS=22 FAIL=0，被测的是 09-30 22:11 的 Debug 产物。
#   旧闸用 `-and` 短路，产物为 $null 时整条判据被跳过 ⇒「不存在」比「旧」更容易蒙混过关。
if (-not $guiDll) { $stale += 'FfmpegGui.dll 不存在（src/FfmpegGui/bin/Release）⇒ 被测产物无从判定，本轮不可绑定任何指纹' }
if (-not $guiExe) { $stale += 'FfmpegGui.exe 不存在 ⇒ 端到端门禁会静默回退到 Debug 产物（本轮实测踩过）' }
if (-not $prbExe) { $stale += 'ServiceProbe.exe 不存在 ⇒ 探针类门禁静默回退或整段跳过' }
if (-not $uiExe)  { $stale += 'UiTestHost.exe 不存在 ⇒ UI 门禁静默回退或整段跳过' }
if (-not $gmExe)  { $stale += 'GainMapTestHost.exe 不存在 ⇒ GainMap 宿主门禁静默回退或整段跳过' }
if ($stale.Count -gt 0) {
  Write-Output "[STALE] 被执行的产物缺失或比它的源码旧，拒绝跑（否则结论不可信）"
  $stale | ForEach-Object { Write-Output ("        " + $_) }
  Write-Output  "        重建（同批、各自 -c Release）：src/FfmpegGui | tests/ServiceProbe | tests/UiTestHost | tests/GainMapTestHost"
  Write-Output  "        ⚠ tests/GainMapTestHost 自 2026-09-30 起**已进闸**（清单第 60 条 `verify-gainmap-host.ps1` + 上面第 ⑤ 对）"
  # ⚠ 2026-09-19 修（实施 #147 C 锁时漏掉的退出点）：此处**必须**先释放仓库级锁 ——
  #   否则「正常拒绝」也会留下残留锁（实测：STALE 拒跑后 `跑后锁残留=True`，下次运行会被
  #   自己的锁挡住并报「疑似残留锁」）。`Release-GatesLock` 只删**自己写的**锁（pid 校验）。
  Release-GatesLock
  exit 2
}

# ── ⚠ #22：运行器自身形态自检（**跑在任何一步之前**）──────────────────────────────
# 为什么放这里：形态坏了 ⇒ 退出码读数不可信（PS5.1 下会**全轮假红**）⇒ 与上面的 STALE 闸同族：
#   宁可**拒绝跑**（不产出读数），也不产出不可信的基线。
$shapeBad = @(Test-RunnerShape)
if ($shapeBad.Count -gt 0) {
  Write-Output "[SHAPE] 运行器自身形态自检失败，拒绝跑（否则退出码读数不可信）"
  $shapeBad | ForEach-Object { Write-Output ("        " + $_) }
  Write-Output  "        修法：退出码一律取 Invoke-StepWithTimeout 的返回值；进程对象在 helper 内命名 child"
  Write-Output '        修法（若红的是「散文条数」那一针）：把现量陈述里的条数改成与 $expectedScriptCount 一致；新增现量陈述时把它的形态加进 Test-RunnerShape 第 ⑰ 针'
  Release-GatesLock
  exit 2
}
Write-Output "shape-selfcheck=OK（17 针：定义点 / 调用点 / 带超时调用点 / 裸取 A / 裸取 B / 进程对象取码 / 写 rc 点 / 读 rc 点 / 杀树 token / 抑制点 / 探针超时默认值 / 脚本超时默认值 / tableOnly 条数 / noSelfSummary 条数 / 读失败不沿用 / 自身指纹 / 散文条数）"

# ── ⚠ #28：启动时记录**运行器自身**指纹（收尾前重测 ⇒ 不一致则响亮告警）────────────────
# ⚠ 这一行**有意打印**（收尾还有一行）：日志里出现 hash 是**预期**的，不是异常。
$selfFp0 = Get-SelfFingerprint
Write-Output ("runner-self-sha256=" + $selfFp0.Sha + " lines=" + $selfFp0.Lines + " bytes=" + $selfFp0.Bytes + "  (启动时记录；有意打印)")

# ── 2026-10-01 补：把**被测产物**指纹钉进本轮日志头（取代"人工记 FfmpegGui.dll 前 16"的旧约定）──
#   为什么现在补：历史基线一直靠人在结论里手抄 DLL 前 16 位（易腐、事后无从核对），
#   而本轮抓到的假绿链恰恰是"这轮跑的到底是哪份二进制"答不出来 —— `bin/Release` 被外部
#   `dotnet build` 打回（`bin/` 在 .gitignore 里 ⇒ git 看不见），35 个子脚本静默换 Debug 产物。
#   上面的闸已 fail-closed；这一行把"是哪一份"变成日志里可事后比对的读数，不是口头声明。
$artFp = @()
foreach ($ap in @(
    @{ n = 'FfmpegGui.dll';         p = (Join-Path $binDir 'FfmpegGui.dll') },
    @{ n = 'FfmpegGui.exe';         p = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe" },
    @{ n = 'ServiceProbe.exe';      p = $pr },
    @{ n = 'UiTestHost.exe';        p = "$root/tests/UiTestHost/bin/Release/net11.0/win-x64/UiTestHost.exe" },
    @{ n = 'GainMapTestHost.exe';   p = "$root/tests/GainMapTestHost/bin/Release/net11.0/win-x64/GainMapTestHost.exe" })) {
  $h = ''
  try { if (Test-Path $ap.p) { $h = (Get-FileHash $ap.p -Algorithm SHA256 -ErrorAction Stop).Hash.Substring(0, 16) } } catch { $h = '读失败' }
  $artFp += ('{0}={1}' -f $ap.n, $(if ($h) { $h } else { '缺失' }))
}
Write-Output ("artifact-sha16: " + ($artFp -join '  ') + "  (被测产物；跑后不改则可事后追溯到这一份)")

# ── 跑后源码漂移复核（2026-09-15 新增）────────────────────────────────────────
# 本仓库存在**并发编辑会话**：跑的过程中源码可能被改，此时上面每一行结论都
# 不再对应当前工作区。显式告警，避免把“别人的中途状态”当成自己的基线。
function Check-SrcDrift {
  $guiAfter = Get-NewestSrc "$root/src/FfmpegGui"
  $prbAfter = Get-NewestSrc "$root/tests/ServiceProbe"
  $pairs = [System.Collections.Generic.List[object]]::new()
  $pairs.Add(@($guiSrc, $guiAfter))
  $pairs.Add(@($prbSrc, $prbAfter))
  foreach ($pair in $pairs) {
    $b = $pair[0]; $a = $pair[1]
    if ($b -and $a -and $a.LastWriteTime -ne $b.LastWriteTime) {
      Write-Output ("[WARN] 跑期间源码被改动：{0} {1} → {2} {3}" -f `
        $b.Name, $b.LastWriteTime, $a.Name, $a.LastWriteTime)
      Write-Output  "       本次结论不对应当前工作区 ⇒ 必须重建后重跑（不要把它当基线）"
      $script:failedItems.Add("源码漂移：$($b.Name) 跑期间被改 ⇒ 本轮结论作废")
    }
  }
}

# ── ⚠ #28：跑后**运行器自身**漂移复核（`Check-SrcDrift` 覆盖不到运行器自己）──────────────────
# ⚠ **为什么计入 `$failedItems`（不是只告警）**：判据是「**归属不明**」与「**读数作废**」同级 ——
#   上面的 `Check-SrcDrift` 命中时也是**计入**的（`$script:failedItems.Add("源码漂移：…")`），
#   本条与其同族（都是「本轮结论不对应当前工作区」）⇒ 保持**同一处置口径**，不另立宽松档。
#   ⚠ 若只告警不计入，则「跑的是旧版还是新版？」这个疑问会**降级成日志噪声**，与 STALE 闸的
#     「宁可拒绝跑，也不产出不可信基线」自相矛盾。
function Check-SelfDrift {
  $a = Get-SelfFingerprint
  Write-Output ("runner-self-sha256=" + $a.Sha + " lines=" + $a.Lines + " bytes=" + $a.Bytes + "  (收尾时重测；有意打印)")
  if ($a.Sha -ne $selfFp0.Sha -or $a.Lines -ne $selfFp0.Lines -or $a.Bytes -ne $selfFp0.Bytes) {
    Write-Output ("[WARN] 跑期间运行器自身被改动 ⇒ 本轮读数归属不明（启动 {0} / {1} 行 / {2} B → 收尾 {3} / {4} 行 / {5} B）" -f `
      $selfFp0.Sha, $selfFp0.Lines, $selfFp0.Bytes, $a.Sha, $a.Lines, $a.Bytes)
    Write-Output  "       本轮结论作废：必须用**同一份**运行器重跑（与 STALE 闸同级的「不产出不可信基线」）"
    $script:failedItems.Add("运行器自身被改：$($selfFp0.Sha) → $($a.Sha) ⇒ 本轮读数归属不明")
  }
}
# ── ⚠ #22：宿主 exe（探针段与子脚本段都要用）────────────────────────────────────
# 子脚本必须用**当前宿主**的 PowerShell 启动（`$PID` 的 exe 路径），不要硬编码解释器名。
# ⚠ 2026-09-16 更正：旧注释写「本机没有名为 pwsh 的 PATH 命令（实测 Get-Command pwsh ⇒ NOTFOUND）」，
#   该结论**已过期** —— 实测 `Get-Command pwsh` 命中 `C:\Program Files\PowerShell\7\pwsh.exe`（7.6.6），
#   同时 `powershell.exe`（Windows PowerShell 5.1）也在。硬编码解释器名依然是错的，但理由是
#   **宿主会随启动方式而变**：用 pwsh 启动运行器 ⇒ 子脚本跑 PS 7；用 powershell.exe 启动 ⇒ 子脚本跑 PS 5.1。
#   ⇒ 因此 `tests/scripts/*.ps1` 必须**双宿主兼容**（门禁：`verify-ps-compat.ps1`）。
# ⚠ #22 再补一层：`Invoke-StepWithTimeout` 的**包装层**也用这个 exe，且探针段就要用 ⇒ 计算必须早于探针段。
$psExe = $null
try { $psExe = (Get-Process -Id $PID).Path } catch { }
if (-not $psExe -or -not (Test-Path $psExe)) {
  $cand = Join-Path $PSHOME 'pwsh.exe'
  $psExe = if (Test-Path $cand) { $cand } else { 'powershell.exe' }
}
foreach ($m in $probeList) {
  $o = "$root/tests/output/_gate_$m.txt"
  $r = Invoke-StepWithTimeout -FilePath $pr -Arguments @($m) -OutFile $o -ErrFile "$o.err" -TimeoutSec $ProbeTimeoutSec -Label $m -HostExe $psExe
  $rc = $r.ExitCode
  $sum = ''
  $bad = @()
  $readFail = $false
  if ($r.TimedOut) {
    # ⚠ 超时后 $o 是**半截文件**：照常解析会打出「看起来跑完了且干净」的假汇总 ⇒ 必须抑制解析
    $sum = '（超时击杀，汇总解析已抑制）'
  } else {
    $txt = Read-TextShared $o
    if ($null -eq $txt) {
      # ⚠⚠ 第 15 针：读失败**禁止沿用上一轮 `$txt`** ⇒ 显式清空 + **显式记 FAIL**（原写法只打印异常、不记账）
      $readFail = $true
      $txt = ''
      $sum = '（⚠ 输出文件读取失败，汇总不可得）'
      $failedItems.Add("probe:$m (输出读取失败 ⇒ 汇总不可得；见 $o)")
    } else {
      $sum = ($txt -split "`n" | Select-String -Pattern 'PROBE RESULT|合计|缺素材|SKIP' | ForEach-Object { $_.Line.Trim() }) -join ' ; '
      $bad = ($txt -split "`n" | Select-String -Pattern '^FAIL|≠' | Select-Object -First 6 | ForEach-Object { "      " + $_.Line.Trim() })
    }
  }
  $flag = ''
  if ($r.TimedOut) { $flag = '  ⚠超时击杀（未完成）' }
  elseif ($readFail) { $flag = '  ⚠输出读取失败' }
  elseif ($rc -eq 2 -or $sum -notmatch 'PROBE RESULT') { $flag = '  ⚠未执行(未知 mode 或无 PROBE RESULT 汇总)' }
  Write-Output ("[{0,-10}] exit={1}  {2}  {3:N1}s{4}" -f $m, $rc, $sum, $r.ElapsedSec, $flag)
  # 记账（2026-09-18）：退出码非 0，**或**汇总行里出现非零 fail（防止"有 fail 但 exit=0"）⇒ 都算失败。
  # 记账（#22）：超时走**同一条**记账 ⇒ fail-loud，不静默继续。
  if ($r.TimedOut) {
    $failedItems.Add("probe:$m (超时 $($ProbeTimeoutSec)s，已击杀进程树)")
  } elseif (($rc -ne 0) -or ($sum -match 'fail=[1-9]')) {
    $failedItems.Add("probe:$m (exit=$rc)")
  }
  $bad | ForEach-Object { Write-Output $_ }
  if ($r.TimedOut) {
    Write-Output "      ↳ 超时现场（输出文件末 5 行）："
    Get-Content $o -Tail 5 -ErrorAction SilentlyContinue | ForEach-Object { Write-Output ("        " + $_) }
  }
}
if ($SkipScripts) {
  # ⚠ `-SkipScripts` 在脚本段之前就早退 ⇒ 同时给 `-Scripts` 时那些子集**根本不会被跑**。
  #   静默忽略会变成"以为跑过了其实没跑"，这里点名拒绝。
  if (-not [string]::IsNullOrWhiteSpace($Scripts)) {
    Write-Output "[FAIL] -SkipScripts 与 -Scripts 互斥（前者在脚本段前就早退 ⇒ 子集不会跑）。去掉 -SkipScripts。"
    $failedItems.Add("runner:-SkipScripts 与 -Scripts 同时给出")
    Exit-WithSummary "（参数冲突，拒绝跑）"
  }
  Check-SrcDrift; Check-SelfDrift; Exit-WithSummary "（-SkipScripts，仅探针）"
}

# ⚠ #22（2026-09-19）：`$psExe` 的计算已**上移**到探针段之前 —— 理由：`Invoke-StepWithTimeout` 的
#   **包装层**需要一个宿主 exe，而探针段就要用它（原位置在探针段之后，本轮上移）。原注释随之上移。
Write-Output "script-host=$psExe"
# ⚠ 「表格型 / 无断言」脚本：只打印对照表，**不产 PASS/FAIL 契约** —— 它们的 exit=0 仅表示「跑完了」，
#   不等于「断言全绿」。清单里显式标出来（行尾会打 ← 提示），避免把 exit=0 误读成门禁通过。
#   登记见 docs/TESTING.md §3.4 表「表格型：无 PASS/FAIL 契约」与 §6 第 62 条。
#
# ⚠ #23（2026-09-19）：1 → 4 条。口径取 gp-3 的**定稿版**（docs/TESTING.md §6 第 62 条
#   「⚠ 2026-09-19 补」段，逐条读源码判定）：
#       **`$tableOnly` ⟺ 该脚本没有「断言级」非零退出路径。**
#   ⚠ **仅用于前置条件**（缺 exe / 素材 / 输入）的 `exit 1` **不算**断言通道 —— 它只说明
#     「没跑起来」，不说明「跑起来后全过」。
#   ⚠⚠ 本口径**刻意不用**「输出是否含 `PASS=n FAIL=m`」判：运行器的**脚本循环**只有**一条**
#     记账通道 —— **退出码**（见下方 `} elseif ($rc2 -ne 0) {`）；`$sum` 在脚本循环里**只用于回显**。
#     若套用探针循环的「两通道」框架复算，会得 **10** —— 那会把 7 条**有断言退出码**的脚本
#     （B 类：5 条 `_probe-*-scan` + `run-pipeline-tests.ps1` + `verify-color-engine-xcheck.ps1`）
#     标成「exit=0 仅表示跑完」，**抹掉 §6 第 58 条刚补上的退出码语义 ⇒ 制造反向假绿**。
#     故 B 类**不进本数组** —— 它们改由**另一个数组** `$noSelfSummary` 打**另一条**标签
#     （正确读法是「有退出码契约、但无自身计数汇总」，见 §6 第 62 条形态描述）。
#   ⚠ `_probe-jpg-p3-icc.ps1` 是最易漏的一条：它输出里确实有 `PROBE RESULT: pass=1 fail=0`，
#     但那是它 `:25` 调**子进程** `ServiceProbe png3` 打出来的，**不是它自己的**计数汇总
#     ⇒ 它自身**无**断言退出码 ⇒ 仍属「无契约」，必须标。
#   ⚠ 本条**只影响显示标签**，**不参与** `$failedItems` 记账 ⇒ 标 4 条还是 10 条**都不改变**
#     整轮红绿；它防的是「人读日志时把 `exit=0` 当成门禁绿」。
#   ⚠ 别与**记账口径**混：§6 第 62 条「四态 / SKIP」要求「无 `PASS=n FAIL=m` 的一律**单列、
#     不计入 PASS 总数**」—— 那条覆盖 **10** 条，与这里的 **4** 条是**两件事**，各自成立。
#   ⚠ 本数组**条数由 `Test-RunnerShape` 第 13 针钉住**（删一条 ⇒ 该条标签静默消失 ⇒ 读者又会误读）。
$tableOnly = @('verify-colorfmt-matrix.ps1', 'verify-tiff-icc.ps1', 'verify-webp-hdr-fix.ps1', '_probe-jpg-p3-icc.ps1')

# ⚠ #23（2026-09-19，team-lead 定稿）：B 类 —— **有**「断言级」退出码、但**无自身计数汇总**。
#   为什么**必须与 `$tableOnly` 分开（不复用）**：B 类**有**退出码契约 ⇒ 若套上「无 PASS/FAIL 契约
#   （exit=0 仅表示跑完）」，那是**事实错误**（把「有契约」说成「无契约」）⇒ 制造**反向假绿** ——
#   正是 §6 第 58 条刚补上的退出码语义被抹掉的那一类。
#   它要防的误读**与上一条相反**：这 7 条里有的末行以 `PASS ` 开头却**没有** `=n FAIL=m`
#   （`_probe-proc-encoding-scan` / `_probe-stderr-drain-scan`）⇒ 人读日志时**极易当成绿**，
#   而它们**只**靠退出码记账 ⇒ 必须显式提示「别按 PASS= 行判读」。
#   清单 = docs/TESTING.md §6 第 62 条「B 类 · 有断言退出码、无自身计数汇总 = 7 条」。
#   ⚠ 本数组**条数由 `Test-RunnerShape` 第 14 针钉住**（同第 13 针的理由）。
$noSelfSummary = @('_probe-cancel-propagation-scan.ps1', '_probe-ct-chain-closure-scan.ps1', '_probe-proc-encoding-scan.ps1',
                   '_probe-stderr-drain-scan.ps1', '_probe-sync-read-before-wait-scan.ps1',
                   'run-pipeline-tests.ps1', 'verify-color-engine-xcheck.ps1')
# ⚠ 2026-09-17 调序：`verify-color-caps.ps1` 提到 `verify-color-wiring.ps1` **之前**。
#   原顺序（wiring 先）让 wiring (8) 段依赖 **caps 的遗留产物** `validate/caps/p3.icc`
#   ⇒ 干净 `tests/output` 上必红，且"单跑绿、全量红"易被误判成产品回归（本轮实测踩到）。
#   根修是 wiring **自备素材**（`_lib-color-assets.ps1`，见 docs/TESTING.md §6）；
#   这里调序是**纵深**：常见路径下连自备逻辑都不必触发。
# ⚠ 2026-09-17 调序（二）：`_probe-jpg-p3-icc.ps1`（生产者）加进清单，且**前置于**它的两个消费者
#   `verify-colorfmt-matrix.ps1` 与 `verify-png3-interop.ps1`（二者都读 `validate/jpgp3/p3.icc`）。
#   起因：消费者在清单、生产者不在 ⇒ 干净 `tests/output` 上消费者**静默降级**
#   （实测 `verify-png3-interop` 缺参照时 `PASS` 9→8 而 **`exit` 仍为 0** ⇒ 运行器不知道少验了一条）。
#   为什么选「补生产者」而不是「把消费者的 SKIP 改成 `CK $false`」：④ 段（iCCP/cICP 共存）是**有意义的
#   观察项**，改成必红等于**主动放弃一条覆盖**；补生产者只是**修复清单的遗漏**。
#   ⚠ **本组正确性依赖 runner 串行**（`validate/jpgp3` 是固定共享目录）——
#   若将来 runner 改成并行，本组必须先改成「指针/按会话隔离」（§6 第 68 条）。
# ⚠ 受管基线：脚本清单 = **68** 条（`.workbuddy-ai/memory/MEMORY.md` / `docs/HANDOVER.md` /
#   `docs/TESTING.md` §3.5 同记此数）。增删条目必须**同时**改此常量 + 四处文档；
#   否则下面的条数锁会响亮报红（防「删掉一条 ⇒ 静默变 47 ⇒ 日志照常、无人发现」）。
#   ⚠ 本锁在 `-SkipScripts` 下**不执行**（该模式走上面的早退，清单根本没被使用）——
#     这是**显式的取舍**，不是偶然漏跑；若将来要两种模式都校验，把数组定义与本锁一起提到早退之前。
#
# ⚠⚠ **2026-09-19 P4-A 接线：42 → 48（+6）** —— 把本轮新建的 6 条门禁接进清单（此前它们
#   **存在但从未被整轮跑到** ⇒ 属「有锁但不上锁」的静默覆盖缺口）。新增 6 条与实测（本轮实跑）：
#     · 第 43 条 = `verify-ui-host.ps1`          ⇒ `UIHOST RESULT: pass=302 fail=0`（6.4 s）
#     · 第 44 条 = `verify-ui-param-matrix.ps1`  ⇒ `UIPARAM RESULT: pass=302 fail=0`（6.3 s）
#     · 第 45 条 = `verify-ui-strategy-map.ps1`  ⇒ `UISTRAT RESULT: pass=302 fail=0`（6.1 s）
#     · 第 46 条 = `verify-ui-param-defects.ps1` ⇒ `UIPARAMDEF RESULT: pass=302 fail=0`（6.3 s）
#     · 第 47 条 = `verify-cli-strict.ps1`       ⇒ `CLISTRICT RESULT: pass=302 fail=0`（6.0 s）
#     · 第 48 条 = `verify-png-structure.ps1`    ⇒ `PNGSTRUCT RESULT: pass=32 fail=0`（1.4 s）
#       ⚠ 上面 5 条 UI 门禁的 `pass=302` 是 **2026-09-19 那一轮的实跑读数**，按"历史链逐字保留"的规矩不改写；
#       现值演进：302 → 328（2026-09-22）→ **340**（2026-09-24，`UiTestHost` 加 `GroupP_StartupDetectLog` +12 条）。
#       ⇒ 想核对当期值：看整轮日志里 `UIHOST RESULT: pass=` 那一行，别拿这段历史注释当现值。
#   ⇒ 6 条**合计仅 32.4 s**（对整轮 ~950 s 是 +3.4%）⇒ 时长预算无碍。
#   ⚠ 这 6 条**都带自身计数汇总**（`PASS=n FAIL=m` + 显式 `exit 1/else exit 0`）
#     ⇒ **都不进** `$tableOnly` / `$noSelfSummary`（第 13/14 针的条数锁因此不变：4 / 7）。
#   ⚠ 前 5 条都跑同一个 `tests/UiTestHost.exe`（各跑一遍**全量**宿主，只在锚点断言上分工）
#     ⇒ 有**受控冗余**（4 次重复执行宿主 ≈ 24 s），换来的是「某个分组被静默删掉」能被各自锚点抓住。
#   ⚠⚠ **曾有的覆盖缺口（2026-09-24 已处置）**：`tests/UiTestHost` 是**第三个项目**，而本运行器的
#     新鲜度（STALE）闸原先**只覆盖 `src/FfmpegGui` + `tests/ServiceProbe` 两个** ⇒ 只重建那两者时，
#     这 5 条会拿**陈旧的 `UiTestHost.exe`** 跑出「绿」，当时靠**手工**保证三者同批重建。
#     ⇒ 现已把 `UiTestHost.exe ↔ tests/UiTestHost/**` 与被 28 条端到端门禁执行的
#       `src/**/FfmpegGui.exe ↔ src/FfmpegGui/**` 两对补进同一条闸（见上方 `[STALE]` 段）；
#       ⚠ **【2026-09-30 更新】该宿主当时"仍不在闸内"，现已进闸**：接成清单第 60 条
#       `verify-gainmap-host.ps1`，并补了上面 STALE 的**第 ⑤ 对**（`GainMapTestHost.exe` ↔ 自己的源码）。
#       ⇒ 上面那条负断言（"非注释行里 `$x = … GainMapTestHost` = **0** 处"）**据此作废**，同一判据
#         现命中 `verify-gainmap-host.ps1:26-27` 与本文件 `:676-679`。
#       接闸的动机就是本次处理的遗留：09-20「SDR ⇒ 普通 JPEG」裁定作废了 `sdr` 格的 4 条 UltraHDR 期望，
#       而**没有门禁跑这个宿主** ⇒ 那 4 条红在树里稳定存在 10 天，只有手工跑宿主才偶然看得见。
#       （"取赋值形态而非搜文件名"这条纪律仍然成立：本文件注释里就含该文件名，搜文件名会被自己的
#         文字满足 ⇒ 新门禁特意保留了这条判据形态。见 §6 第 106 条末段、第 120 条。）
#     ⚠ 但"扩到 4 对"**不等于**"所有被执行产物都进了闸"：门禁在 Release exe 缺位时会**兜底跑 Debug 那份**
#       （清单内 27 条内嵌该路径；会打印 `(Debug fallback)` 因而**可追溯**，但**不拦**），而 **Debug 不在本闸内**，
#       且本闸各对（2026-09-30 起为**五对**）都是"产物不存在 ⇒ 该对跳过" ⇒ 「缺失即拒跑」与否是**待决策**，
#       见 `docs/HANDOVER_2026-09-22.md` §4 第 9 行与 `docs/TESTING.md` §6 第 106 条末段。
#   ⚠ 不接入的长跑门禁：`verify-oracle-matrix.ps1`（本轮新建，5010 条组合 + 独立裁判）
#     实测 **1.6~2.0 s/用例 ⇒ 全量约 2.5 h**，远超 `ScriptTimeoutSec 480` ⇒ **有意不入清单**，
#     按**手工长跑**执行（`-Layer L1` ≈ 6 min / `-Layer L2` ≈ 5 min / `-Smoke` ≈ 8 s）。
# ⚠⚠ **2026-09-22 接线：49 → 50（+1）** —— `verify-engine-alpha-preserve.ps1`
#   （管线审查 §3.4 的修复锁；见清单内该条目的登记与变异验证）。
# ⚠⚠ **2026-09-22 第二次接线：50 → 51（+1）** —— `_probe-tool-detect.ps1`
#   （外部工具探测的**开销/诚实性/可注入性**三合一锁；起因是本轮两条门禁红 + 一次实测 29 分钟挂死，
#     细节写在该脚本文件头。断言本体在 `ServiceProbe tooldetect`，本条目只做编排。）
# ⚠⚠ **2026-09-23 第四次接线：51 → 52（+1）** —— `verify-startup-warmup.ps1`
#   （启动期「环境分析 / 预热」的七缺陷锁：检测了不报告、GUI 空槽位 + 异常零记录、同一条 5 级
#     路径链一次启动跑两遍、扫描窗口内的假阴、exiftool 每次调用重扫、换环境不失效、
#     声明级数 ≠ 实现级数。结构锁 + 一次真跑（headless `--dry-run` 报告 8 行）。
#     变异验证 13 例全部有牙（逐例清单见该脚本文件头的「变异验证登记」段，含两条 harness 踩坑告示）。）
# ⚠⚠ **2026-09-24 第六次接线：53 → 54（+1）** —— `_probe-settings-clone-coverage.ps1`
#   （`AppSettingsService.Save()` 手写克隆表的**结构锁**。**接入当天就抓到一个长期缺陷**：
#     `AppSettings.RenderingMode` 从没进过这张表 ⇒ 每次 `Save()` 都把它写成**模型默认值 "auto"**
#     （菜单 `MainWindow.xaml.cs:5239` 刚改完、`:5243` 立刻 Save ⇒ 用户选的渲染后端当场丢失，
#     启动 `Program.cs:127` 读到的永远是 auto）。该脚本 2026-09-15 就写好了，
#     但**和 `verify-ipc-extra-options` 一样属于"有锁但不上锁"** ⇒ 脚本自 2026-09-15 起就没人跑过，
#     而缺陷进入代码的确切日期**未能从版本历史确定**（`git log -S RenderingMode` 只追到补齐追踪那次提交）。
#     配套还给了 `ServiceProbe settings` 一条**行为锁**（设非默认哨兵 → Save → 读回逐个比对），
#     两条独立成对：结构锁管"有没有漏字段"，行为锁管"值真的出去了没有"。
#     分类账随之变成 **4 + 7 + 43 = 54**（`$tableOnly` / `$noSelfSummary` 两个数组**不动**）。
#     变异证据：删掉 `RenderingMode = _current.RenderingMode,` 那一行 ⇒ 本条红「漏项 = RenderingMode」
#     且 `settings` 红「写入 zz-RenderingMode-7 / 读回 auto」（实测读数见 §6 第 110 条）。）
# ⚠⚠ **2026-09-23 第五次接线：52 → 53（+1）** —— `verify-ipc-extra-options.ps1`
#   （IPC **ExtraOptions 透传轴**的回归锁（P1-C）。该脚本 2026-09-19 就写好了、且自带变异验证，
#     却从未进清单 ⇒ 属「有锁但不上锁」的静默覆盖缺口（与 2026-09-19 P4-A 那批 +6 同性质）。
#     白班 §4 第 6 条当时记的是"接一条 = 清单 52"—— 那个数已过期：接它时清单已经是 52 ⇒ **现 53**，
#     分类账随之变成 **4 + 7 + 42 = 53**（`$tableOnly` / `$noSelfSummary` 两个数组**不动**）。
#     接入前实测单跑：`PASS=20 FAIL=0`、`exit=0`、stderr 0 字节。
#     ⚠ 它要**独占 GUI 实例 + 命名管道**（脚本 :52-56 有实例即判红）⇒ 依赖运行器**串行**，
#       且必须排在 `verify-cli-strict.ps1` **之前**（见下方清单内登记与末位约定）。）
# ⚠⚠ **2026-10-01 第六次接线：60 → 65（+5）** —— 首批 **L3/产物级**套件（清单末尾第 61~65 条）。
#   动机不是"再加几条断言"，而是**这批判据此前根本不在版本控制里**：它们住在 `tests/output/t{69,76,77,79}/`，
#   而 `.gitignore:78` 的 `output/` 把整棵目录挡住 ⇒ 一次 `git clean -xdf` 就全没，也不在任何轮里被跑；
#   它们跑过的读数只能靠人在结论里手抄（09-30/10-01 两轮都是这样）。
#   迁入时改了三件事：① 路径由 `$PSScriptRoot` 运行时推导（原写死 `C:\PLAN\ffmpegPictureUI`）；
#   ② 被测由"只打出货包"改成**两档**（默认 Release 产物 ⇒ 每轮都跑；`$env:PKG_VER` 时升 L3），
#      两档都缺 ⇒ `exit 2` 不产出读数、**绝不回退 Debug**；③ 每条都补齐 `PASS=n FAIL=m` 规范汇总
#      （运行器的**零断言闸**只认这个形状）。⇒ 分类账 **4 + 7 + 57 = 68**，两个数组照旧不动。
# ⚠⚠ **2026-10-04 第七次接线：65 → 67（+2）** —— 「色彩实现完整审查」的两个 P1 修后补判据：
#   第 66 条 `_probe-jxl-input-target.ps1`（JXL 作为**输入**时用户显式目标必须生效；正控 + **负控**）
#   第 67 条 `_probe-raw-default-tier.ps1`（RAW 默认档按**实际交付档**判 HDR 承载力；含 webp 反控）
#   两条都**自带汇总**（`PASS=n FAIL=m` + 断言驱动 exit）⇒ 分类账的「其余」由 54 → 56，
#   `$tableOnly`（4）与 `$noSelfSummary`（7）两个数组不动。
# ⚠⚠ **2026-10-04 第八次接线：67 → 68（+1）** —— 72h 完整审查发现的 P0 补回归锁：
#   第 68 条 `_probe-gui-control-wiring.ps1`：**结构锁** —— 「XAML 里声明了、但既无初始选中、
#   又无 `SelectionChanged` 处理器」的 GUI 下拉必须为 **0**。立锁动机是实测的 P0：
#   新增 `--raw-color-target` 时加了 `RawColorTargetCombo`，但它**没有初始选中**
#   ⇒ `SelectedItem` 恒 null ⇒ `RawColorTarget` 恒 null ⇒ `QueueProcessor` 的 `?? "rec2020"`
#   永远生效 ⇒ **GUI 选 `auto`/`rec709` 完全无效**（而 CLI 同参数正常）—— 典型「宣称≠交付」，
#   且**任何既有门禁都抓不到**（没人去点这个下拉，所以"控件是死的"不产生红）。
#   该锁接受本仓**三条**合法接线路径（.cs 直赋 / `FillComboByLoc` / XAML 属性），
#   带"判据非空跑"反控（白名单外 combo ≥ 8）与 `RawColorTargetCombo` 点名锁。
#   ⚠ 自带汇总（`PASS=n FAIL=m` + 断言驱动 exit）⇒ 分类账「其余」56 → 57，两个数组仍不动。
#   ⚠ 实测（2026-10-04）：变异（删掉该控件的初始选中 + 处理器）⇒ **73/4 转红**，恢复 ⇒ **77/0**。
$expectedScriptCount = 68
$scriptList = @('verify-ps-compat.ps1','verify-color-caps.ps1','verify-color-wiring.ps1','verify-color-strategy.ps1','verify-format-regression.ps1',
                 'verify-widegamut-regression.ps1','verify-tiff-icc.ps1','verify-webp-hdr-fix.ps1','verify-decision-delivery.ps1',
                 '_probe-stderr-drain-scan.ps1','_probe-proc-encoding-scan.ps1','_probe-cancel-propagation-scan.ps1','_probe-i18n-scan.ps1','_probe-cjk-hardcode-scan.ps1','verify-png-signature.ps1','verify-gainmap-memory.ps1','verify-metadata-privacy.ps1','verify-gif-avif-framelist.ps1','verify-color-peak.ps1',
                 '_probe-jpg-p3-icc.ps1','verify-colorfmt-matrix.ps1','verify-prophoto-faithful.ps1',
                 'verify-gainmap-managed.ps1','verify-gainmap-engine.ps1','verify-gainmap-host.ps1',
                 # ── 第 49 条：2026-09-20 接线（`verify-gainmap-isobmff.ps1`）──────────────────────────
                 #   覆盖 **AVIF/HEIC（ISO-BMFF）增益图的识别 + 解码**：此前 `GainMapDecoder` 只认 JPEG 容器
                 #   ⇒ 增益图被**静默丢弃无点名**；且旧动画判据（`videoCount >= 2`）把**静态增益图 AVIF**与
                 #   **静态 AVIF+alpha**（两者 ffprobe 形态完全相同）**一律误判成动画**（引擎被绕过 + 走错两步法）。
                 #   ⚠ 该门禁**依赖**库内 `tools/src/libavif/{tests/data,build/Release}`（样本 + `avifenc`），
                 #     缺任一即 **fail-closed 判红**（不静默跳过）；详见其文件头的前置依赖与变异验证登记。
                 'verify-gainmap-isobmff.ps1',
                 'verify-gamut-map.ps1',
                 'verify-color-token-normalize.ps1','verify-hlg-route.ps1',
                 'run-pipeline-tests.ps1','verify-png3-interop.ps1','verify-color-tonemap.ps1','verify-color-engine-xcheck.ps1',
                 'verify-geometry-engine.ps1','verify-engine-firstframe.ps1','verify-geometry-orientation.ps1','_probe-geometry-single-source-scan.ps1','verify-jxl-codestream-route.ps1','verify-anim-static-target.ps1',
                 'verify-jxl-probe-timeout.ps1','verify-ffmpeg-heartbeat.ps1','_probe-sync-read-before-wait-scan.ps1','verify-jxr-anim-input.ps1','_probe-ct-chain-closure-scan.ps1','verify-avif-anim-probe.ps1','verify-webp-anim-probe.ps1',
                 # ── 第 50 条：2026-09-22 接线（`verify-engine-alpha-preserve.ps1`）────────────────────
                 #   覆盖「**透明输入 + 色彩变换** ⇒ alpha 被静默拍平」的修复（管线审查 §3.4）。
                 #   ⚠ 它的素材必须**真透明**（`colorchannelmixer=aa=0` 自造，并在门禁内**自检** alpha==0）——
                 #     只用 `sources/src_alpha.png` 是**不够**的（它实测「RGBA 容器但全不透明」，会让断言恒真）。
                 #   ⚠ 变异验证：把 `RawColorPipeline.BuildEncodeArgs` 的 alpha 判据改成 `false` ⇒
                 #     该门禁 `PASS=9 FAIL=7`（② 的 6 条 + ③ 的正例 1 条）⇒ **有牙**。
                 #   ⚠ 放在 `verify-cli-strict.ps1` **之前**：后者含「共享语料目录不得残留 `_*.png`」的验收，
                 #     必须在**所有**脚本之后跑（既有约定）。
                 'verify-engine-alpha-preserve.ps1',
                 # ── 第 51 条：2026-09-22 接线（`_probe-tool-detect.ps1`）─────────────────────────────
                 #   锁三件实测缺陷：① 递归扫 PATH 每个根 ⇒ 遇用户配置文件根目录这类大树**永不返回**
                 #   （实测 60 s 时句柄 9 万 / 单核满载 / 一次降级路径挂 29 分钟）；② 兜底把「已证实
                 #   起不来」的候选报成可用工具（WindowsApps 私有 djxl.exe = Access Denied、
                 #   Prefetch 的 `EXIFTOOL.EXE-*.pf` 被 `*exiftool.exe*` 命中）；③ 降级路径的门禁无法
                 #   确定性地关掉系统搜索根 ⇒ 判据随本机装过什么应用而翻转（本轮 ①②就是那么翻红的）。
                 '_probe-tool-detect.ps1',
                 # ── 第 52 条：2026-09-23 接线（`verify-startup-warmup.ps1`）───────────────────────────
                 #   锁启动期「环境分析 / 预热」的七处实测缺陷（细节在其文件头 ①~⑦）。两类锁并存：
                 #   ①**结构锁**（读源码文本，确定性）：`ProbeAllTools` 内不得再有无条件 `Xxx.Detect();`、
                 #     四个 service 的 `lock (_gate)` ≥ 3、exiftool 的 `_attempted` 短路、GUI 的失效调用点
                 #     （GPU / 格式能力 / PLAN 闩）、RawService 六级逐形态锁。
                 #   ②**行为锁**（真跑一次 headless `--dry-run`）：报告必须 8 行且与 `ProbeAllTools` 同名同数
                 #     —— 只有①会退化成字符串存在性，抓不住「函数存在但没人调 / 检测了但不报告」。
                 #   ⚠ 变异验证：13 例逐条注入（含删掉 djxl 那一行报告后**重建 exe**）⇒ 13/13 转红，无空转。
                 #   ⚠ 必须排在 `verify-cli-strict.ps1` **之前**（末位约定见上）。
                 'verify-startup-warmup.ps1',
                 # ── 第 43~48 条：2026-09-19 P4-A 接线（见上方 `$expectedScriptCount` 处的登记）──
                 # ⚠ `verify-cli-strict.ps1` **有意放最后**：它含「共享语料目录里不得残留 `_*.png`」的验收，
                 #   放在末位才能把**前面所有脚本**的残留都纳入观测。
                 # ── 第 53 条：2026-09-23 接线（`verify-ipc-extra-options.ps1`）────────────────────────
                 #   锁 IPC 侧 `ExtraOptions` **真的被应用**（复用 CLI 的 key→字段映射，不另写平行表）：
                 #   3 条正控走**真实命名管道 + 真实 GUI 实例 + ffprobe 回读产物 pix_fmt**（chroma 4:2:0 →
                 #   `yuvj420p` / 4:2:2 → `yuvj422p` / bit-depth 16 → `rgb48*`），2 条向后兼容（缺键 vs 空字典
                 #   **是两件事，两条都锁**），1 组非法取值必须**显式失败且点名 key** 且不产出文件。
                 #   ⚠ 归 **C 类**：自身有 `PASS=/FAIL=` 汇总 + `RESULT` 行 + 断言驱动 `exit 1`，前置缺件 fail-closed
                 #     ⇒ **不进** `$tableOnly` / `$noSelfSummary`。其文件头自带变异验证登记（删掉 ApplyExtraOption
                 #     调用 ⇒ B3/C3/D3 + G1/G2 转红）。
                 #   ⚠ 独占 GUI + 命名管道 ⇒ 必须在 `verify-cli-strict.ps1` **之前**、且依赖运行器串行。
                 'verify-ipc-extra-options.ps1',
                 # ── 第 54 条：2026-09-24 接线（`_probe-settings-clone-coverage.ps1`）───────────────────
                 #   锁 `AppSettingsService.Save()` 的**手写克隆表**是否覆盖全部持久化属性。
                 #   为什么这条必须进清单（**它进来当天就抓到一个长期缺陷**）：`Save()` 序列化的是
                 #   `new AppSettings{ 逐字段 = _current.X }` ⇒ 漏一个字段不是"不落盘"，而是**落盘成模型默认值**
                 #   （键在、值被打回默认）⇒ 用户改完立刻 Save 就当场丢失。实测命中
                 #   `RenderingMode`（菜单改它 → 立刻 Save → 启动读它选渲染后端），而该脚本
                 #   **早已存在但不在清单里** ⇒ 谁也不跑 ⇒ 缺陷一直躺着。
                 #   两把锁各自独立：本条是**结构锁**（读源码比对字段集合，零依赖、~1 s），
                 #   `ServiceProbe settings` 里同期补的"全字段落盘往返"是**行为锁**（设哨兵 → Save → 读回比对）。
                 #   变异证据：删掉 `RenderingMode = _current.RenderingMode,` 那一行 ⇒
                 #     本条红（"漏项 = RenderingMode"）+ `settings` 也红（"写入 zz-RenderingMode-7 / 读回 auto"）；
                 #     本条内部的「有牙自证」用**相对基线多报一个**的判据，避免与主判据互相绑死。
                 #   ⚠ 归 **C 类**：自身有 `PASS=/FAIL=` 汇总 + 断言驱动 `exit 1` ⇒ **不进** `$tableOnly` /
                 #     `$noSelfSummary`（第 13/14 针的 4 / 7 不变）。纯源码扫描，不需要 exe、不动任何产物。
                 '_probe-settings-clone-coverage.ps1',
                 # ── 第 55 条：2026-09-27 接线（`_probe-matrix-structure.ps1`，任务 #42）─────────────────
                 #   跑 `_lib-matrix.ps1` 的**结构自检**（A1..A31 + 库自带 B1..B7 正/负控）+ 本门禁自己的
                 #   变异注入（删一对两两覆盖 ⇒ A7 必须点名转红）。
                 #   为什么必须进清单：折叠表 / 语义矩阵每条都带 `Evidence = '<路径>:<行号>|<字面片段>'`，
                 #   A21/A30 会真去读那一行；但此前只有 `verify-oracle-matrix.ps1` 跑它，而那条是
                 #   **有意不入列**的 2.5 h 长跑 ⇒ 行号漂移没人看见。实测账：**6/19 条早就腐烂**，
                 #   接线当天（#39/#43 改了 `FfmpegCommandBuilder.cs` 的行）又当场漂一条
                 #   （`metadata-core` 2011 → 2041）——**这就是这条门禁存在的理由，不是假想**。
                 #   成本：两遍自检 ≈ 11 s（单遍实测 5.5 s），纯数据 + 读源码，**不起产品进程、不动产物**。
                 #   ⚠ 归 **C 类**：自身有 `MATRIXSTRUCT RESULT: pass=/fail=` 汇总 + 断言驱动
                 #     `exit 1`/`exit 0` 成对 ⇒ **不进** `$tableOnly` / `$noSelfSummary`（第 13/14 针不变）。
                 #   有牙自证三处：① 结果行缺失 ⇒ 直接红并中止（不许"读不到就当过"）；
                 #     ② 断言总数下限 30（防有人删检查项而不是删被检查的东西）；
                 #     ③ A21/A30 的 `checked=` 必须 ≥1（负断言在空清单上恒绿 ⇒ 拦死）。
                 #   变异证据（当场跑过）：把 `metadata-core` 的行号改成 2040 ⇒ `pass=7 fail=1`、
                 #     红的正是「结构自检零红」那条，退出码 1；改回 2041 ⇒ 8/0。
                 '_probe-matrix-structure.ps1',
                 # ── 第 56 条：2026-09-27 接线（`_probe-stderr-merge-scan.ps1`，任务 #51）──────────────
                 #   扫「把 stdout+stderr 拼起来返回」的取数 helper 被用来读**工具的值**（exiftool 标签 /
                 #   ffprobe 字段）的站点。动机是实测的：随包 exiftool 是 perl 打的，环境带本机 perl 不认的
                 #   `LC_ALL/LANG`（从 MSYS/bash 起门禁就会出现）⇒ 每次调用先喷 locale warning 到 stderr，
                 #   于是"值"变成警告文本。两类坏法都真发生过：负断言被撑成恒真（`'' -notmatch 'a'` ⇒
                 #   "素材确实不透明"假绿；两侧同污时 `-ne` 变成两条警告互比），以及值前多一行 ⇒ 假红。
                 #   本轮据此收了五处口（alpha-preserve / run-pipeline / caps / strategy ×2），并给每处补了
                 #   **正对照**（同一把尺必须先在"真有值"的素材上读到东西）。
                 #   ⚠ 这把尺只做**文件级共现**，不做数据流 ⇒ 它锁的是"债不得增加 + 已收口五处不得回退"，
                 #     不是"这些站点都对"。债账基线 = **84**（本轮收口前实测 89），逐条分诊仍是后续项。
                 #   ⚠ 归 **C 类**：自身有 `STDERRMERGE RESULT: pass=/fail=` 汇总 + 断言驱动 `exit 1`/`0`
                 #     ⇒ **不进** `$tableOnly` / `$noSelfSummary`（第 13/14 针不变）。
                 #   有牙自证两处（当场跑过，留痕 `tests/output/t51/teeth.log`）：① 造一个含合并 helper +
                 #     `Exec $et` 取值的临时脚本 ⇒ 它被点名（`_tmp-t51-teeth.ps1:11`）、站点数 **84→85** 判红；
                 #     删掉夹具 ⇒ 回到 84 判绿；
                 #     ② 枚举下限 40 + 共现基数下限 30（正则失效会伪装成"零违规"，那两个下限把它拦住）。
                 '_probe-stderr-merge-scan.ps1',
                 # ── 第 57 条：2026-09-27 接线（`verify-simd-switch-fallback.ps1`，面板「SIMD 优化」消费者）──
                 #   立锁动机是一条**已核实的声明/实现冲突**：`AppSettings.AutoUseSimdBinaries` 由复选框
                 #   写入并持久化，zh/en 两条 `tip.simd` 都在承诺「关闭后逐像素回退 / falls back to scalar
                 #   code」，但修复前**全仓没有任何代码读它决定走不走 SIMD**（`PsnrCalculator.SquaredDiffSum`
                 #   无条件按 ISA 优先级、`SimdPixelOps.FloatToSrgb8` 无条件看 `Avx2.IsSupported`）。
                 #   ⇒ 按「降级不是修复」把实现提到声明的水位：判据单点 `SimdKernelRouting`。
                 #   四段判据：① 行为（`ServiceProbe simdswitch`，进程内确定性合成像素，S0..S6）+
                 #   **pass>=25 下限**（防"mode 整段没跑 ⇒ exit 0"）；② `FFMPEGGUI_AUTO_SIMD` 两跑端到端
                 #   （关⇒scalar / 开⇒非 scalar / 两态必须不同 ⇒ **修复前必红**）；③④ 结构锁（`.IsSupported`
                 #   与消费端集合只此一份、内核分派表只有一张；**空集合也判红**）。
                 #   牙的证据（主循环 2026-09-27 实跑，非内存变异）：把 `ResolvePixelKernelPath` 的守卫
                 #   反向（`if (!UserAllowsSimd)` → `if (UserAllowsSimd)`）⇒ 本条 **PASS=17→13 / FAIL=0→4**
                 #   （②c 关⇒avx2、②d 开⇒scalar、①/①b 的 pass 下限同时跌破），还原后逐字节一致、17/0/1SKIP。
                 #   ⚠ 归 **C 类**：自带 `===== … PASS= FAIL= SKIP= =====` 汇总 + 断言驱动 `exit 1`/`0`
                 #     ⇒ **不进** `$tableOnly` / `$noSelfSummary`（第 13/14 针条数不变：4 / 7）。
                 'verify-simd-switch-fallback.ps1',
                 'verify-ui-host.ps1','verify-ui-param-matrix.ps1','verify-ui-strategy-map.ps1','verify-ui-param-defects.ps1','verify-cli-strict.ps1','verify-png-structure.ps1',
                 # ── 第 61~65 条：2026-10-01 首批 **L3 判据**接进清单（原居 `tests/output/t{69,76,77,79}/`，
                 #    被 `.gitignore:78 output/` 挡在版本控制外 ⇒ 一次 `git clean -xdf` 就没了，也不在任何轮里跑）。
                 #    ⚠ 迁入后**被测档改成两档**：默认 = 本轮刚构建的 Release 产物（每轮都跑，L2+），
                 #      设 `$env:PKG_VER` 时才打 `publish/build/FFmpegPictureUI-v<ver>-x64-full`（L3）；
                 #      两档都**缺即 exit 2 不产出读数、绝不回退 Debug**（异构/陈旧二进制的读数不可信）。
                 #    ⚠ 五条**都不进** `$tableOnly` / `$noSelfSummary`：各自都有 `PASS=n FAIL=m` 汇总
                 #      + 断言驱动 `exit 1` ⇒ 归 C 类（第 13/14 针条数不变：4 / 7）。
                 #    ⚠ `verify-gainmap-thirdparty.ps1` 还额外依赖**本机构建的第三方参照**
                 #      `tools/src/libultrahdr/build/Release/ultrahdr_app.exe`（不在包内、不进版本控制）⇒
                 #      缺它时该条判红是**有意的 fail-closed**，不是产品缺陷；修法见该脚本头部。
                 'verify-package-smoke.ps1','verify-jbrd-e2e.ps1','verify-orientation-rewrap.ps1',
                 'verify-gainmap-thirdparty.ps1','verify-encoder-knob-liveness.ps1',
                 # ── 第 66~67 条：2026-10-04 接线（「色彩实现完整审查」的两个 P1 修后补判据）───────
                 #   第 66 条 `_probe-jxl-input-target.ps1`：锁「JXL 作为**输入**时，用户显式指定的目标必须生效」。
                 #     病因（已修）：SDR-JXL 的「输入色声明」原先写进 `ColorPrimaries/ColorTrc`，而那对字段在
                 #     `DecideOutputColor` 里是**输出目标** ⇒ `--color-space "Display P3"` 被**静默吞掉**。
                 #     断言：正控（JXL + `--color-space "Display P3"` ⇒ 产物 `color_primaries=smpte432`）
                 #     + **负控**（同输入不带目标 ⇒ 产物**不得**是 smpte432，防断言恒真）。
                 #   第 67 条 `_probe-raw-default-tier.ps1`：锁「RAW 默认档按**实际交付档**判 HDR 承载力」。
                 #     病因（已修）：旧判据 `BitDepth ?? 8`（**请求**档）把 16-bit 可承载 HDR 的容器误判成 8-bit
                 #     ⇒ ARW→PNG 默认档错误地降到 `bt709/sRGB`（产物却是 `rgb48be`）。
                 #     断言：png ⇒ `rgb48* + bt2020 + smpte2084`；webp（8-bit 上限）⇒ **不得** bt2020（防一刀切升档）；
                 #     tiff ⇒ `16 16 16` + 有目标 ICC + 日志目标 `bt2020/iec61966-2-1`。
                 #   ⚠ 两条都**自带汇总**（`PASS=n FAIL=m` 行 + 断言驱动 `exit 1`）⇒ **不进**
                 #     `$tableOnly` / `$noSelfSummary`；素材缺失时 **fail-closed**（点名 + 红），不静默 SKIP。
                 '_probe-jxl-input-target.ps1',
                 '_probe-raw-default-tier.ps1',
                 # ── 第 68 条：2026-10-04 接线（72h 完整审查的 P0 回归锁）────────────────────
                 #   立锁动机（**实测 P0**）：新增 `--raw-color-target` 时加了 GUI 下拉
                 #   `RawColorTargetCombo`，但它**既没有初始选中、也没有 `SelectionChanged`**
                 #   ⇒ `SelectedItem` 恒 null ⇒ `RawColorTarget` 恒 null ⇒ `QueueProcessor` 的
                 #     `?? "rec2020"` 永远生效 ⇒ **GUI 选 `auto`/`rec709` 完全无效**；
                 #   而 **CLI 同参数正常**（实测产物 BT.709 ICC）⇒ 缺陷**只在 GUI**。
                 #   ⚠ 为什么既有门禁抓不到：`verify-ui-*` 跑的是 UiTestHost 行为断言，
                 #     **没人去点这个下拉** ⇒「控件是死的」不产生任何红 ⇒ 属结构性盲区。
                 #   本锁 = 静态结构锁（毫秒级、不驱动 GUI）：XAML 里每个 `x:Name` 下拉必须
                 #     在 **三条合法路径**之一被接线 —— ① `.cs` 直赋 `SelectedIndex` +
                 #     `SelectionChanged +=`；② `FillComboByLoc(...)`（本地化下拉的初始化，
                 #     其内 `combo.SelectedIndex = idx`）；③ **XAML 属性**
                 #     `SelectedIndex=` / `SelectionChanged=`。
                 #   ⚠ 三条都要认：只认 ① 会把 21 个**完全合法**的控件判红（本锁开发期实测），
                 #     那是**过严的假红** —— 「不得为绿而放宽」的另一面是「不得把对的判红」。
                 #   ⚠ 带反控：白名单外 combo **≥ 8**（掉下去 = 提取器空跑 ⇒ 判红）；
                 #     另有 `RawColorTargetCombo` 点名锁，即使被塞进白名单也照样盯着。
                 #   ⚠ 自带汇总（`PASS=n FAIL=m` + 断言驱动 exit）⇒ **不进**
                 #     `$tableOnly` / `$noSelfSummary`；读不到源文件时 fail-closed（判红）。
                 #   变异验证（2026-10-04 实测）：删掉该控件的初始选中 + 处理器 ⇒ **73/4 转红**；
                 #     恢复 ⇒ **77/0**。⇒ 锁确有牙，且正对本次 P0。
                 '_probe-gui-control-wiring.ps1')
$actualScriptCount = @($scriptList).Count
if ($actualScriptCount -ne $expectedScriptCount) {
  Write-Output ("[FAIL] 脚本清单条数 = $actualScriptCount，受管基线 = $expectedScriptCount ⇒ 基线漂移（增删条目必须同步四处文档）")
  $failedItems.Add("runner:脚本清单条数 $actualScriptCount ≠ 基线 $expectedScriptCount")
}
Write-Output "script-list=$actualScriptCount 条（受管基线 $expectedScriptCount）"
# ── `-Scripts` 子集解析（2026-09-24）──────────────────────────────────────────────
# 条数锁**始终对全数组生效**（它校验的是清单本身，与本次跑几条无关）；子集只影响下面跑哪些。
$subsetTag = ''
$runList = $scriptList
if (-not [string]::IsNullOrWhiteSpace($Scripts)) {
  $want = @($Scripts -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
  $unknown = @($want | Where-Object { $scriptList -notcontains $_ })
  if ($unknown.Count -gt 0) {
    # 打错名字静默忽略 ⇒ "看着跑过了其实没跑"，正是本仓最忌的假绿 ⇒ 响亮拒绝。
    Write-Output ("[FAIL] -Scripts 里有 $($unknown.Count) 个名字不在受管清单内：$($unknown -join ', ')")
    $failedItems.Add("runner:-Scripts 名字不在清单（$($unknown -join ', ')）")
    Exit-WithSummary "（-Scripts 参数非法，拒绝跑）"
  }
  $runList = @($scriptList | Where-Object { $want -contains $_ })
  $subsetTag = "（-Scripts 子集 $($runList.Count)/$actualScriptCount 条 ⇒ **不构成基线**）"
  Write-Output ("[SUBSET] 只跑 $($runList.Count) 条：$($runList -join ', ')")
  Write-Output "[SUBSET] 仓库锁 / 单步超时 / STALE / 双漂移 / 逐条留痕 **仍然全部生效**；宣布新基线需跑全量。"
}
foreach ($s in $runList) {
  $p2 = Join-Path "tests/scripts" $s
  if (-not (Test-Path $p2)) { Write-Output "[SKIP] $s 不存在"; continue }
  $o = "$root/tests/output/_gate_script_$s.txt"
  $r2 = Invoke-StepWithTimeout -FilePath $psExe -Arguments @('-NoProfile','-File',$p2) -OutFile $o -ErrFile "$o.err" -TimeoutSec $ScriptTimeoutSec -Label $s -HostExe $psExe
  $rc2 = $r2.ExitCode
  $sum = ''
  $bad = @()
  $readFail = $false
  if ($r2.TimedOut) {
    # ⚠ 超时后 $o 是**半截文件**：照常解析会打出「看起来跑完了且干净」的假汇总 ⇒ 必须抑制解析
    $sum = '（超时击杀，汇总解析已抑制）'
  } else {
    $txt = Read-TextShared $o
    if ($null -eq $txt) {
      # ⚠⚠ 第 15 针：同探针段 —— 读失败**禁止沿用上一轮 `$txt`**（脚本段 `$sum` 只回显，但**显示成上一条的汇总**同样是误导）
      $readFail = $true
      $txt = ''
      $sum = '（⚠ 输出文件读取失败，汇总不可得）'
      $failedItems.Add("script:$s (输出读取失败 ⇒ 汇总不可得；见 $o)")
    } else {
      $sum = ($txt -split "`n" | Select-String -Pattern 'pass=|PASS=|^PASS |^FAIL |=====|合计|RESULT|SKIP|声明：' | Select-Object -Last 4 | ForEach-Object { $_.Line.Trim() }) -join ' ; '
      $bad = ($txt -split "`n" | Select-String -Pattern '^ *FAIL|^FAIL' | Select-Object -First 6 | ForEach-Object { "      " + $_.Line.Trim() })
    }
  }
  $tag = ''
  if ($r2.TimedOut) { $tag = '  ⚠超时击杀（未完成）' }
  elseif ($readFail) { $tag = '  ⚠输出读取失败' }
  elseif ($tableOnly -contains $s) { $tag = '  ← 表格型：无 PASS/FAIL 契约（exit=0 仅表示跑完）' }
  elseif ($noSelfSummary -contains $s) { $tag = '  ← 有退出码契约（exit=0 即无失败）；无自身计数汇总' }
  # ── 「零断言 ⇒ 红」闸（2026-09-24）──────────────────────────────────────────────
  #   挡的是这一类：**前置素材缺失 ⇒ 一条断言都没跑，但 exit=0 且汇总"看起来干净"**。
  #   历史同族：`verify-png3-interop` 缺参照时 PASS 9→8 而 exit 仍 0（§6 第 68 条），
  #   以及审计点名的 6 条"缺件即零断言绿灯"形态（verify-color-token-normalize:45 等）。
  #   取**所有匹配里的最大值**：一条门禁常有多处 `PASS=n`（子步骤 + 总计），
  #   拿"第一条"会撞上小的子计数、拿"最后一条"会撞上 per-probe 读数 ⇒ max 才回答"到底跑没跑断言"。
  #   ⚠ 例外两类各有自己的契约（$tableOnly 无 PASS/FAIL 契约、$noSelfSummary 只认退出码），
  #     且超时 / 读失败已在上面单独记账 ⇒ 这里不重复判，避免一次故障刷两条红。
  if (-not $r2.TimedOut -and -not $readFail -and ($tableOnly -notcontains $s) -and ($noSelfSummary -notcontains $s)) {
    $passNums = @([regex]::Matches([string]$txt, '(?i)PASS\s*=\s*(\d+)') | ForEach-Object { [int]$_.Groups[1].Value })
    if ($passNums.Count -eq 0) {
      Write-Output "      ⚠零断言闸：本门禁输出里**找不到任何 `PASS=n` 计数** ⇒ 无法证明它跑过断言"
      $failedItems.Add("script:$s (零断言：无 PASS=n 汇总 ⇒ 可能一条断言都没跑)")
    } elseif (($passNums | Measure-Object -Maximum).Maximum -eq 0) {
      Write-Output "      ⚠零断言闸：`PASS=0` ⇒ 一条断言都没跑（多半是前置缺失被静默跳过）"
      $failedItems.Add("script:$s (零断言：PASS=0)")
    }
  }
  Write-Output ("[{0,-32}] exit={1}  {2}  {3:N1}s{4}" -f $s, $rc2, $sum, $r2.ElapsedSec, $tag)
  if ($r2.TimedOut) {
    $failedItems.Add("script:$s (超时 $($ScriptTimeoutSec)s，已击杀进程树)")
  } elseif ($rc2 -ne 0) {
    $failedItems.Add("script:$s (exit=$rc2)")
  }
  $bad | ForEach-Object { Write-Output $_ }
  if ($r2.TimedOut) {
    Write-Output "      ↳ 超时现场（输出文件末 5 行）："
    Get-Content $o -Tail 5 -ErrorAction SilentlyContinue | ForEach-Object { Write-Output ("        " + $_) }
  }
}
Check-SrcDrift
Check-SelfDrift
Exit-WithSummary $subsetTag
