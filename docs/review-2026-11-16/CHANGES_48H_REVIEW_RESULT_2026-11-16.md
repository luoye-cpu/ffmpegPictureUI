# 48h 变更审查结果（`a9e2830..957cfa8`）· 统一裁决

- 审查对象：`ffmpegPictureUI` 仓库 48 小时内 18 个提交 / 75 个文件（16 新增 / 59 修改，+8777 −1095）
- 被测提交：`957cfa8`（HEAD，tag `1.6.0BETA17`，已推 origin）
- 审查方式：五路互斥并行（色彩语义 / 格式出口与回读 / UI与队列 / 构建打包检测 / 门禁与文档），总控统一裁决跨模块项
- 只读证明：`git status --porcelain -uno` 为空；HEAD 与 tag 未变；本轮全部新增文件仅 `docs/review-2026-11-16/review-evidence/`（未跟踪）
- 报告与证据根目录：`docs/review-2026-11-16/review-evidence/`；转码产物落在 `tests/output/_tmp/review-2026-11-16/<lane>/`（gitignore 区）

---

## 0. 裁决摘要

| 级别 | 条数 | 说明 |
|---|---|---|
| CRITICAL（P0） | **0** | 未发现默认路径上的静默色彩错误、数据损坏或不可交付产物 |
| HIGH（P1） | **3** | `COLOR-01` `COLOR-07`（carry 通路丢 ICC，其中 WebP 条为本批新引入）、`MA-01`（beta17 发布提交上两条在册门禁为红且未登记） |
| MEDIUM | 12 | 门禁口径漂移 4、文档口径反向 2、队列停止语义 2、门禁盲区 1、其它 3 |
| LOW | 18 | 文案/泄漏/可追溯性/环境摩擦 |
| INFO | 1 | 双写一致性正控通过 |

**三句话结论**

1. **默认路径（engine/auto）是好的**：CICP/ICC 双写、ICC v4.4 重写、容器与 ICC 内部 `cicp` 标注逐值一致（正控 `COLOR-04` 通过），AVIF 16bit 无静默降位，`oxipng` 不剥离也不改写 `iCCP`/`cICP`（ICC 字节 sha256 逐位相同）。
2. **回退路径有真洞**：`--color-engine legacy` 或任何回退到 legacy/直连的通路上，显式 `--color-strategy carry` 会把源 ICC **整条丢掉**（PNG 只剩 cICP、WebP 零标注），P3 像素被查看器按 sRGB 解读。
3. **治理层比代码层更值得先修**：beta17 发布提交上至少 2 条在册门禁是红的（`verify-decision-delivery` 78/9、`verify-probe-mirror-parity` 89/2），而 release 说明与 CHANGELOG 都没有登记红态；其中 `⑭` 命中的正是第 2 条那个新回归。

**beta17 处置建议**：**修复后按 beta18 重发**（现有 tag `1.6.0BETA17` 不动、不重指；详见 §9）。

---

## 1. 审查设置

| 项 | 值 |
|---|---|
| 范围 | `a9e2830..957cfa8`（18 提交） |
| 文件 | 75（26 色彩 / 16 格式 / 9 UI / 8 构建 / 16 门禁文档） |
| 环境 | Windows + PowerShell；git `C:\Program Files\Git\bin\git.exe`；dotnet SDK 11.0.100 + 10.0.403 |
| 回读工具 | ffmpeg/ffprobe、exiftool、avifenc 1.4.2、oxipng 10.2.1、dngtool、pngcicp、jxl v0.12.0 |
| 被测算物 | `publish/build/FFmpegPictureUI-v1.6.0-beta17-x64{,-full}/FfmpegGui.exe` |
| 验证门禁 | 只跑变更链路针对性门禁，**未跑 104 条全量** |

**覆盖存证**（机械核对，非人工声明）：把 75 个改动文件的叶子名逐个与五份子报告 `Select-String` 对撞，结果 `files=75 covered=75 missing=0`。第一步曾漏 7 份 review 文档（退回复审后补齐），这说明"看过 diff"与"进过覆盖表"不是一回事。

---

## 2. 统一发现表（34 条）

> 证据列给出锚点；完整日志路径见 §7，产物见 §7 产物区。`ver` = 实测 / 代码推断 / 未验证。

### 2.1 HIGH（P1）

| ID | 位置 | 现象 | 证据 | ver |
|---|---|---|---|---|
| COLOR-01 | `Services/FfmpegCommandBuilder.Decision.cs:569-582` | 显式 `--color-strategy carry` 且 `--color-engine legacy` 时，P3→PNG **丢 ICC**：engine/auto 双写（36540B 含 iCCP 405B + cICP 12/13/0/1），legacy 只写 cICP（36123B 无 iCCP）。`pngCicpFirst` 分支无条件 `AttachIcc=false`，不看 `DeclaredStrategy` | `_tmp/.../color/out/{png-carry-engine,png-carry-legacy}/a2_srgb_p3.png`；pngcicp 读数 | 实测 |
| COLOR-07 | `Services/QueueProcessor.cs:3020-3047` | **本批新引入**。`planPromisesGeneratedIcc`（`AttachIcc:true`+`IccPrimaries` 非空+`IccPath:null`）把 legacy 直连 WebP 的 carry **误判为"计划自生成 ICC"**，跳过源 ICC 回带、defer 到只在 engine 路径跑的后置 ⇒ 产物零 ICC 零 CICP；P3 像素被当 sRGB | `_tmp/.../color/out/repro-14-legacy/`（webp 6366B，ffprobe `primaries=unknown/transfer=unknown`）vs `repro-14-engine/`（7008B，exiftool ICC 红原色 0.67999）；`repro-14-legacy.stdout` 含"跳过源 ICC 回带" | 实测 |
| MA-01 | `tests/scripts/verify-decision-delivery.ps1`；`tests/scripts/verify-probe-mirror-parity.ps1` | **beta17 发布提交上两条在册门禁为红**且 release 说明未登记：`decision-delivery` 78P/9F(rc=1)、`probe-mirror-parity` 89P/2F(rc=1)。红里既有真实新回归（`⑭`＝COLOR-07），也有未随新路由更新的过期断言 | `logs/color/verify-decision-delivery.log`；`logs/gate/mirror-parity-baseline.txt`（总控复核：`FAIL ③-e 实取 1006 == 登记 1005`） | 实测 |

### 2.2 MEDIUM

| ID | 位置 | 现象 | 证据 | ver |
|---|---|---|---|---|
| COLOR-02 | `verify-decision-delivery.ps1:962-983` | 本批新增的 avif16 `--depth 12,4` 路由让 ⑯×2 / ⑰b×4 / ⑱-4×1 共 7 条 FAIL：门禁仍找 ffmpeg 位深标记与 `-color_trc` 在 `-i` 前的位置，avifenc 路由不产出这些标记；⑰b 仍期待 legacy 12bit AVIF `exit=0`，产品已改为 fail-closed `exit 91` | 同上日志尾；`repro-17b.stdout` 含拒绝文案 | 实测 |
| COLOR-03 | `docs/review-2026-11-14/ICC_VERSION_COMPARISON.md:54,111,212`（+5 份，见 §4.4） | 仍写"AVIF 静态出口不写 ICC"，而 `928ec89` 已把 `ExitWritesIccFor` 改成 `fmt is not ("webm")`，AVIF 经 avifenc 同写 ICC+CICP | `_tmp/.../color/out/avif-rec/a2_srgb_p3.avif`：exiftool 同见 nclx 与 v4.4 ICC；`avif-rec.stdout` 含 `--cicp 12/13/0 --icc …` | 实测 |
| COLOR-06 | `verify-color-strategy.ps1:385-393` | 门禁不交叉校验"ICC 内部原色 == 容器 CICP"：一侧用 exiftool 判 ICC 有无、另一侧用 ffprobe 判 CICP，从不数值对撞 ⇒ 二者漂移时门禁不会红 | `logs/color/verify-color-strategy.log`（78P/0，全绿但无此格） | 代码推断 |
| COLOR-08 | `Services/ColorMapping/ColorStrategyPlanning.cs:1593-1594` | 链末曲线 `iec61966-2-1` 与产物标签 `transfer=bt709` 不一致（`PlanDelegatedHdrSdr` 写死 bt709）。**存量问题**（a9e2830 已存在），本批未触及 | `_tmp/.../color/out/repro-15/`；`repro-15.stdout` 命令行 `zscale=…:p=bt709:t=iec61966-2-1:m=bt709` | 实测 |
| UI-01 | `Services/QueueProcessor.cs:4835`；`Models/QueueItem.cs:224` | 管道分支（cjxl/cjpegli/djxl）取消时返回 `"已停止/超时"`，而 `IsStopped => Status == "已停止"` 是精确匹配 ⇒ `RequeueStoppedAndFailed`(:186) 不重排它，反而被 `ApplyErrorOnlyFilter`(:3755) 收进「仅显示报错」。`c213a51` 只补了主 ffmpeg 分支 | 总控复核：`4835` 与 `224` 两处原文对撞 | 代码推断 |
| UI-02 | `Services/QueueProcessor.cs:35,259-262` | 优雅停止（StopAfterCurrentQueue）走 `break` 从不 `Cancel` ⇒ `IsRunning`（`_cts!=null && !_cts.IsCancellationRequested`）恒真；简洁模式 `:5089` 与 `CheckAutoEncode` `:5386` 因此误拒启动 | 代码通读 | 代码推断 |
| UI-04 | `MainWindow.xaml.cs:2802-2811`；`MainWindow.xaml:459` | **门禁盲区**：`c5f18e7` 把 oxipng 面板移到 `StillOptionsPanel` 顶层并加"静态 PNG 显示/APNG 隐藏"判定，但全 `tests/projects` 树零 UI 断言（仅 ServiceProbe 服务层 `48_ProbePngOpt.cs`）；ui-core 84 条日志检索 `oxipng` 零命中 | `logs/ui/ui-core.out.txt`（84P/0）+ 全树 Grep | 实测 |
| BUILD-04 | `Services/PngOptimizerService.cs:81-87`；`Program.cs:293-303` | app 包（`-beta17-x64`，11.6MB）**无 PLAN/无 oxipng.exe**，而默认 `pngopt=max`。实测行为是**显式降级不静默失败**：打 `[pngopt] 未检测到 oxipng，已跳过优化` 后 rc 仍 0，产物是 ffmpeg 原生 PNG；但启动横幅 8 个工具**不点名 oxipng**、GUI 下拉仍显示 max ⇒ 首启无预警 | `logs/build/pack-beta17.out.txt`；`PngOptimizerService.cs:85` 文案；`MainWindow.xaml:461 SelectedIndex=2` | 实测 |
| GATE-01 | `verify-ui-split-union.ps1`；`UiTestHost/Program.cs`；`UiHostShared/UiHost.cs` | `verify-ui-split-union` 7P/4F 的根因是**旧参照宿主测量失效**，非口径漂移、非真实回归（详见 §4.3） | `logs/gate/split-union-1344.txt`；`logs/gate/old-host-direct.txt`（P1 原文"用时 45.0s；结论行 0"） | 实测（2/2 复现） |
| GATE-02 | `verify-probe-mirror-parity.ps1`；`Parts/17_ProbeContract.cs` | HEAD 基线即红：`③ contract 实取 102 vs 登记 101`、`③-e 总量 1006 vs 1005`。`928ec89` 给 ProbeContract 加了 1 个 Check 调用位，未同批抬 `$counts['contract']` | `logs/gate/mirror-parity-baseline.txt`；负控 `mirror-parity-negctrl.txt`（注入第二份 `ProbeWire` ⇒ ⑤/⑤-z 当场红，89P/2F→87P/4F，rc=1） | 实测 |
| MA-02 | `AGENTS.md §7`；6 份 `docs/review-2026-11-14/15/` | "AVIF 静态出口写不进 ICC / 可 CICP 命名即零改动携带 / 不可命名即 exit 91"这一整族口径已被本批代码推翻并在 6 份文档中重复，**且已反向误导本次审查任务书的前提** | §4.4 表；`COLOR-03` 证据 | 实测 |
| MA-03 | `BUILD-04` + `UI-04` + `COLOR` 采集侧 | 默认 `max` 的三方口径未对齐：能力侧（app 包缺 exe）、提示侧（启动横幅不点名）、验证侧（面板显隐零断言） | 同名各条证据 | 实测/推断 |

### 2.3 LOW

| ID | 位置 | 现象 |
|---|---|---|
| COLOR-05 | `CliParser.cs:85-91` | 兼容性正控通过：`--icc-profile-version v9` 被拒 rc=2；未知参数告警后忽略 rc=0；WebP 恒 tv、JPEG auto 压回 pc 未漂移（记录用） |
| FMT-01 | `ExifToolService`/产物目录 | 产物目录残留 `*_exiftool_tmp` 临时件 |
| FMT-02 | `tests/data/synth_heic_grid_aux_noxmp.heic` | 夹具注释语义与探针结果不符（注释称 X，实际行为 Y） |
| UI-03 | `QueueProcessor.cs:66-70` | 重启时旧 CTS 只 `Cancel` 不 `Dispose`（`RebuildSemaphore` 有 Dispose，此处无） |
| UI-05 | `MainWindow.xaml.cs:3571,3645` | 用户点停止后日志连出"队列已停止"与"队列已完成"两行矛盾文案 |
| UI-06 | `MainWindow.xaml.cs:4315-4340` | 旧代位深探测只丢结果不取消 ffprobe；回灌 `Post` 无 try/catch |
| BUILD-01 | `pack.ps1:179` | 全档模式裸调 `robocopy`（无全路径）：PATH 不含 System32 的会话在 Step 2 即中止（loud failure，不产残缺包） |
| BUILD-02 | `FfmpegGui.csproj:52-76` | app/full 两包 exe sha256 不同（`40266D90…` vs `3AB17BF2…`），逐字节仅差 6B（bundle header nonce）⇒ AGENTS §4"用 dll 对账"对 AOT 单包不适用 |
| BUILD-03 | 5 条构建警告 | 全部存量（CS8604、CS8602×2、CA1416、IL3002），本批重写**零新增**；无 IL2026/IL2075 |
| BUILD-05 | `THIRD-PARTY-NOTICES.md` | oxipng 10.2.1 / avifenc 1.4.2 / cjxl v0.12.0 声明与实际二进制对得上；app 包含声明但不含 PLAN 二进制属过度声明（合规无害） |
| BUILD-06 | `FfmpegGui.csproj:68`；`pack.ps1:105` | ISA 口径对撞一致：两侧均 `IlcInstructionSet=x86-64-v3`、`IlcOptimizationPreference=Speed`、RID `win-x64` |
| BUILD-07 | `AppSettingsService.cs:111-124` | `FFMPEGGUI_FFMPEG_DIR` 正斜杠归一化**已在基线存在**（`Norm()`），AGENTS §7 把它列为"未了结"属口径滞后 |
| BUILD-08 | `CpuFeatureService.cs:160-289`；`GpuCapabilityService.cs` | **AOT 下检测不失真**：硬件位走 `X86Base.CpuId` 直读（非 `IsSupported`），`IsSupported` 结果显式分流到 `Runtime*` 字段，反射探针只在 CPUID 不可用的回退分支且被 `TrimmerRootAssembly`+`UnconditionalSuppressMessage` 守住；GPU 全程 `Process.Start` 调 ffmpeg、零反射 |
| GATE-03 | `docs/INDEX.md` | 磁盘上 `docs/review-2026-11-15/` 3 篇未在 INDEX 登记（14/16 已登记） |
| GATE-04 | `CHANGELOG.md`/`CHANGELOG.en.md` | 可追溯：487/0、ui-startup-detect 12/0、ui-cli-strict 60/0、split-reproducible 19/0 均对上；缺口：beta17"5 warnings"在本轮日志复现为 0（增量构建抑制）、beta16 微基准无原始日志、beta15"contract 164/0"是运行次数口径（与调用位 102 不矛盾但不可直接对账）、beta15/16/17 段无包大小/sha256 |
| GATE-05 | `_run-step3-gates.ps1` | 环境摩擦：runner 被残留死锁（已死 pid 26836）与 STALE mtime 闸（`FfmpegGui.dll 13:33 < csproj 13:49`，内容未变）挡住；未重建共享 bin，故 runner 式 rc 本轮未取到 |
| GATE-06 | `AGENTS.md §7`；`docs/TESTING.md` | 473（2026-11-13 快照）与现值 487/103 不符；自动生成的 module-index 段与账本一致（`verify-module-registry` 默认模式 11P/0） |
| MA-04 | `Decision.cs`；`QueueProcessor.cs`；`ColorStrategyPlanning.cs` | "carry" 在 engine / legacy / 直连三处的实现不共用单一真值（`COLOR-01`+`COLOR-07` 的共同结构成因），建议抽一处判定 |

### 2.4 INFO

| ID | 位置 | 结论 |
|---|---|---|
| COLOR-04 | `IccProfileBuilder.cs:124,173-181` | 正控通过：PNG/AVIF 双写时 ICC 内部 `cicp`（primaries/transfer/matrix/FullRange）与容器 cICP/nclx **逐值一致**；版本解析修复实测 exiftool 报 `4.4.0`。**"播放器行为分叉"这一 CRITICAL 候选不成立** |

---

## 3. 四路（五份）子报告索引

| 报告 | 覆盖 | 最高级别 | 一句话 |
|---|---|---|---|
| `01-color-core-report.md` | 26 文件 | HIGH | 默认路径正确，回退路径丢 ICC；一条红门禁含 2 个产品缺陷 + 7 个过期断言 |
| `02-format-exits-report.md` | 16 文件 | LOW | 各格式真实出口与回读全部符合既有口径；oxipng 不动色彩载体；HEIC 三夹具不误判 |
| `03-ui-queue-report.md` | 9 文件 | MEDIUM | 卡死修复与诊断对得上；队列"停止"状态谓词不唯一；oxipng 面板显隐零断言 |
| `04-build-aot-pack-report.md` | 8 文件 | LOW(+1 待裁) | AOT 检测未失真、ISA 两侧一致、5 条警告全存量；app 包缺 oxipng 是唯一实质风险 |
| `05-gates-docs-report.md` | 16 文件 | MEDIUM | split-union 7P/4F＝旧参照宿主测量失效；mirror-parity 基线即红；文档数字大体可追 |

---

## 4. 关键问题裁决（总控）

### 4.1 "carry" 语义在三条通路分裂 —— HIGH 的共同成因

| 通路 | 显式 carry 的期望 | 实测结果 |
|---|---|---|
| engine/auto（默认） | 双写 ICC+cICP | PNG 双写（iCCP 405B + cICP）；AVIF 经 avifenc 双写 ✔ |
| legacy（PNG/APNG） | 携带源 ICC | **只写 cICP，iCCP 缺席**（`COLOR-01`） |
| 直连/legacy（WebP 等） | 携带源 ICC | **零 ICC、零命名**（`COLOR-07`，本批新引入） |

裁决：这不是"策略选择不同"，是同一用户意图在三条代码路径上给了三种结果。`COLOR-07` 的引入点在本范围内（`QueueProcessor.cs` 新增的 `planPromisesGeneratedIcc` 短路），`COLOR-01` 的根因在 `Decision.cs` 的 `pngCicpFirst` 分支不看 `DeclaredStrategy`。二者应共用一处"是否携带源 ICC"的真值判定（`MA-04`）。

风险面：GUI 默认 auto 走 engine，**普通用户不会中**；但 `--color-engine legacy`、JXR/DNG/动画/JXL-cjxl 等回退路径的用户会中，且症状是"文件能开、颜色不对"——最难被用户自己发现的那一类。

### 4.2 两条红门禁的二分（`MA-01`）

`verify-decision-delivery` 9 条 FAIL 的归类（色彩路与总控一致）：

| 归类 | 条数 | 具体断言 |
|---|---|---|
| **产品缺陷** | 2 | `⑭ 直连内嵌源 ICC`（＝COLOR-07，本批新引入）、`⑮ 链末曲线==标签`（＝COLOR-08，存量） |
| **门禁未随新 avif16 路由更新** | 7 | `⑯`×2（命令行位深标记、降档点名）、`⑰b`×4（legacy 12bit AVIF 已 fail-closed 91）、`⑱-4`×1（`-color_trc` 位置） |

`verify-probe-mirror-parity`：`contract` 源码调用位 102 vs 登记 101、总量 1006 vs 1005，纯登记表漏抬（`928ec89` 加断言未同批抬表）。该门禁**有牙**（负控注入第二份 `ProbeWire` 即红并点名文件）。

裁决：两条红门禁在 beta17 上是"真实的红"，不能以"环境/裸跑"解释（两条都在本机稳定复现，且 `mirror-parity` 的红是登记表，与 exiftool 无关）。发布说明把它们当作已通过属口径失真。

### 4.3 `verify-ui-split-union` 7 PASS / 4 FAIL 的定性 = **(c) 旧参照宿主测量失效**

三条判据（GATE 路给出，总控采纳）：

1. **非 (a) 口径漂移**：三处名字真值一致——`verify-ui-split-union.ps1` `$expectedOldCount=487`、`verify-ui-host` 五件臂 84+59+103+169+72=**487**、`_module-registry.json` locks `ui-split-union=487`。P2–P5 的 Check 调用位在单体 `Program.cs` 里确实存在。
2. **非 (b) 真实回归**：拆分侧五件 **487/0 全绿**，2a=PASS、0 FAIL；无任何拆分侧断言失败或被削弱。4 条 FAIL（1a/1b/3b/3c）全是"旧宿主 vs 并集"的比较类。
3. **根因**：旧单体裸 `W.Show()` 后紧跟 `GroupP`，缺少拆分侧 `Bootstrap()` 在开窗前 `ApplyToolPaths()` 预置工具路径的稳态 ⇒ `P1` 轮询 45s 拿到 **0 条 `[detect]` 行**（测试自己标"★测量失败（不是产品失败）★"），按设计 `if(verdicts.Count != names.Length) return` 早返回，吞掉 P2/P3×7/P4/P5 共 10 个名字 ⇒ 旧宿主吐 477 名、并集 487。复现 **2/2**（13:44 人工轮 + 14:29 直跑）。

结论：这 7P/4F **不是产品回归**，是参照宿主自身的启动顺序缺陷把测量结果吞了。修法不是抬数字，而是把旧单体 `Main()` 的 bootstrap 与 `UiHostShared.Bootstrap()` 对齐（开窗前先 `ApplyToolPaths`），或让 `P1` 早返回时把 P2–P5 记为 SKIP 而非"缺名"。

### 4.4 "AVIF 不写 ICC" 口径反向 —— 6 份文档 + `AGENTS.md §7`

本批 `928ec89` 把 `PlanPolicy.ExitWritesIccFor` 从 `fmt is not ("avif" or "webm")` 改成 `fmt is not ("webm")`，AVIF 经 `avifenc --icc` 真写进 ICC。实测 `a2_srgb_p3.avif` 里 nclx 与 v4.4 ICC（P3/sRGB）**同时存在且逐值一致**。但下列文档仍断言"AVIF 写不进 ICC"：

| 文档 | 被判 |
|---|---|
| `docs/review-2026-11-14/COLOR_ANNOTATION_PRIORITY.md` | 过期（§67 把 avif 与 png 并列"只写 CICP"） |
| `docs/review-2026-11-14/COLOR_MATRIX_IO_ANALYSIS_2026-11-14.md` | 过期（§27 行9 `ExitWritesIcc=false`） |
| `docs/review-2026-11-14/COLOR_MATRIX_IO_FIX_PLAN_2026-11-14.md` | 过期（抬头已自更，正文未改） |
| `docs/review-2026-11-14/COLOR_UI_LINKAGE_AUDIT.md` | 过期（§48） |
| `docs/review-2026-11-14/ICC_VERSION_COMPARISON.md` | 过期（:54,111,212） |
| `docs/review-2026-11-15/COLOR_MATRIX_IO_ANALYSIS_2026-11-15.md` | 过期且**自相矛盾**（§12-13 已更，§37-38 仍写不写 ICC） |
| `docs/review-2026-11-15/ICC_STANDARD_AUDIT_2026-11-15.md` | 过期（§37/§163 为 11-15 时间点读数；ICC 结构结论仍成立） |
| `AGENTS.md §7` | 过期（"AVIF 静态出口写不进 ICC（`ExitWritesIcc=false`）"） |

裁决要点：按 AGENTS §6，带日期的账本是"时间点记录"，**不回改**是正确的；但 `AGENTS.md §7` 是"现行口径"，它写错会长久误导——本次审查任务书里"AVIF 静态出口写不进 ICC"就是被这处口径带偏的，直到实测才纠正。建议 §7 新写一条更正条目（保留旧读数、注明 `928ec89` 起 AVIF 双写），而不是原地改历史。

### 4.5 默认 `max` 的三方对齐（`MA-03`）

| 侧 | 事实 | 判定 |
|---|---|---|
| 能力侧（打包） | app 包无 PLAN/无 `oxipng.exe`；full 包有 | app 是"自带工具链缺失"的 SKU |
| 行为侧（代码） | `PngOptimizerService` 探测失败 ⇒ 打一行 `[pngopt] 未检测到 oxipng，已跳过优化`，**rc 仍 0、产物照出** | 显式降级，不是静默失败 |
| 提示侧（UI/启动） | 启动横幅 8 个工具**不列 oxipng**；GUI 下拉仍显示 `max`（`SelectedIndex=2`） | 首次使用无预警 |
| 验证侧（门禁） | oxipng 面板显隐**零 UI 断言**（`UI-04`） | 门禁盲区 |

**跨模块结论（总控裁决）**：`oxipng` 对色彩载体**是安全的**（`FMT` 实测：`-o max` 后 cICP 12/13/0/1 在、iCCP 在、解包 ICC sha256 与源逐位相同；端到端里 iCCP 缺席是 CICP 优先策略使然，不是 oxipng 剥离）。因此 `MA-03` 的实质风险不是"优化把色彩搞坏"，而是"app 包默认档位名不副实 + 无预警 + 无断言"——定为 MEDIUM。

### 4.6 队列停止语义（`UI-01`/`UI-02`/`UI-03`/`UI-05`）

`c213a51` 的语义是"用户停止不再写成失败状态"，实际只改到了主 ffmpeg 分支（+4 行）。管道分支仍返回 `"已停止/超时"`，与 `IsStopped` 的精确匹配不一致 ⇒ 该项在"重排已停止/失败项"里被漏掉、在"仅显示报错"里被错收。`UI-02` 更隐蔽：优雅停止后 `IsRunning` 恒真，简洁模式会报"队列已在运行中"。两者都是**代码推断**（未在真实队列上跑复现），故按 MEDIUM 登记而不是 HIGH。

### 4.7 AOT 检测真实性（`BUILD-08`）—— 一处重要的"否定结论"

预设怀疑是"AOT 下 `IsSupported`/反射失效 ⇒ 检测恒为最低档"。核对结论：**不成立**。`CpuFeatureService` 用 `X86Base.CpuId` 直读硬件位，`IsSupported` 结果被显式分流到 `Runtime*` 字段并标注"AOT 基线 v2"；反射探针只在 CPUID 不可用的回退分支，且由 `TrimmerRootAssembly` + `[UnconditionalSuppressMessage]` 守住；`GpuCapabilityService` 全程 `Process.Start` ffmpeg，零反射。构建日志无 `IL2026/IL2075`。**未验证**：headless 无 `--diagnose` 开关，`FullReport()` 的实际 stdout 未取到（建议补一个 headless 诊断开关，使这条断言可被门禁锁定）。

---

## 5. 门禁实测汇总（本批次）

| 门禁 | 读数 | rc | 是否权威 | 备注 |
|---|---|---|---|---|
| `verify-color-strategy` | 78P / 0F | 0 | 是 | 不覆盖 p3→png carry（见 COLOR-06） |
| `verify-decision-delivery` | **78P / 9F** | **1** | 是 | 2 产品缺陷 + 7 过期断言（§4.2） |
| `verify-probe-mirror-parity` | **89P / 2F** | **1** | 是 | 登记表漏抬；负控有牙 |
| `verify-ui-host` | 487P / 0F | 0 | 引用已知事实 | 本轮未重跑 |
| `verify-ui-split-reproducible` | 19P / 0F | 0 | 引用已知事实 | 本轮未重跑 |
| `verify-ui-split-union` | 7P / 4F | — | 定性见 §4.3 | runner 被死锁+STALE 闸挡，改用直跑 |
| `verify-module-registry`（默认模式） | 11P / 0F | 0 | 是 | 索引段与账本一致（8777B/101 行/67 条） |
| `verify-startup-warmup` | 负控 rc=1 | — | 负控有效 | 移走 Release exe ⇒ "FAIL 缺被测 exe" |
| `verify-gainmap-isobmff` | 164P/7F/9S | — | **否** | 裸跑 + 本机 exiftool `perl532.dll` code 126 噪声 |
| `verify-prophoto-faithful` | 41P/1F | — | **否** | 同上 |
| `verify-color-wiring` | 159P/3F | — | **否** | 同上 |
| `verify-ui-host` · ui-mode 臂 | 59P / 0F | 0 | 是 | UI 路 |
| `verify-ui-host` · ui-core 臂 | 84P / 0F | 0 | 是 | UI 路 |

⚠ **读数归因提醒**：本机 `exiftool` 存在 `perl532.dll` 加载失败（code 126）的间歇故障，导致三条门禁裸跑时回读为空/产物数为 0。这三组 FAIL **不得**写成产品缺陷，也不得写成通过——只能写"环境噪声，需在 runner 环境复核"。这也是 `FMT` 路最高级别被压到 LOW 的原因。

---

## 6. beta15 → beta16 → beta17 行为漂移

| 版本 | 提交 | 引入的语义变化 | 随之带来的漂移 |
|---|---|---|---|
| beta15 | `928ec89` | AVIF ICC 经 avifenc；ICC+CICP 双写 | `ProbeContract` 加 1 个 Check **未抬登记表** ⇒ `mirror-parity` 红；AVIF 能力反转但 6 份文档未更 ⇒ `MA-02` |
| （同批更早） | `c7ac5ab` | CICP/ICC 统一收口 | `planPromisesGeneratedIcc` 短路 ⇒ legacy/直连 carry 丢 ICC（`COLOR-07`） |
| beta16 | `39ed867`/`1a2c0da`/`e2b9d2e` | 日志虚拟化、格式切换卡死修复、GPU 检测收口 | 诊断与修复对得上；oxipng 面板显隐无断言（`UI-04` 在 beta17 才成型） |
| beta17 | `c5f18e7`/`c213a51`/`ee25a16` | oxipng 独立档位（默认 max）；停止诚实 | 停止语义只改主分支（`UI-01`）；app 包缺 oxipng（`BUILD-04`） |

**漂移的本质**：三次 release 各自只验局部链路，**没有任何一次把"整批注册门禁"作为出包前置**。于是两条红从 beta15 一路带到 beta17，而每版 release 说明都能自证"我这版的门禁是绿的"。这与 AGENTS §3 第 ⑥ 条"不许跑全量"并不矛盾——问题不在"不全跑"，在**没有一份按变更链路圈定的必跑清单**且红态没进 release 记录。

---

## 7. 证据清单

### 7.1 报告与 findings（`docs/review-2026-11-16/review-evidence/`）

| 文件 | 大小 |
|---|---|
| `_spec.md`（本批次统一口径，总控） | 6,846 B |
| `01-color-core-report.md` / `findings-COLOR.csv` | 12,941 B / 6,720 B |
| `02-format-exits-report.md` / `findings-FMT.csv` | 13,315 B / 2,135 B |
| `03-ui-queue-report.md` / `findings-UI.csv` | 13,730 B / 5,740 B |
| `04-build-aot-pack-report.md` / `findings-BUILD.csv` | 17,683 B / 4,148 B |
| `05-gates-docs-report.md` / `findings-GATE.csv` | 17,148 B / 5,208 B |

### 7.2 日志（`review-evidence/logs/`）

| 目录 | 文件数 / 字节 | 关键文件 |
|---|---|---|
| `build/` | 3 / 17,011 | `hashes.txt`（两包 exe sha256 与 6B 差定位）、`pack-beta17.out.txt`（5 警告/0 错误） |
| `color/` | 11 / 188,359 | `verify-color-strategy.log`、`verify-decision-delivery.log`（9 FAIL 全文）、`repro-17b.stdout`、`repro-14-legacy.stdout`、7 份 diff-*.txt |
| `fmt/` | 32 / 276,261 | `ox_max.out`/`ox_off.out`、`avif_p3.out`、`gate_prophoto.txt`、HEIC 三夹具读数 |
| `gate/` | 9 / 438,018 | `mirror-parity-baseline.txt`、`mirror-parity-negctrl.txt`、`split-union-1344.txt`、`old-host-direct.txt`、`verify-ui-host-beta17.txt` |
| `ui/` | 4 / 44,964 | `ui-mode.out.txt`、`ui-core.out.txt` |

### 7.3 转码产物（`tests/output/_tmp/review-2026-11-16/<lane>/`，gitignore 区）

| lane | 文件数 / 字节 |
|---|---|
| `build/` | 25 / 43,723,939 |
| `color/` | 268 / 101,490,171 |
| `fmt/` | 403 / 72,908,640 |
| `gate/` | 14 / 3,564,139 |

关键产物：`color/out/png-carry-{engine,legacy}/a2_srgb_p3.png`、`color/out/avif-rec/a2_srgb_p3.avif`、`color/out/repro-14-legacy/`、`fmt/oxsrc_oximax.png`。

---

## 8. 未验证清单（合并五路，逐条带原因）

1. **GUI 真实桌面复现格式切换卡死** —— 无自动化桌面会话；以无头宿主 O4/O5 代际测试 59 条全绿替代，GPU 帧时间未测。
2. **`UI-01`/`UI-02`/`UI-03`/`UI-05`/`UI-06` 运行时复现** —— 代码推断；未在真实队列上跑"管道编码中点停止"与"优雅停止后切简洁模式"。
3. **`CpuFeatureService.FullReport()` 实际 stdout** —— headless 无 `--diagnose` 开关；`BUILD-08` 结论为代码层面推断。
4. **GPU 编码器 Verified 矩阵** —— 本机 Intel UHD + RTX 4060 在线，但 headless 报告不含 GPU 行。
5. **"全新用户首启、无 ffmpeg、无 settings"的 app 包路径** —— 本会话 `%APPDATA%` 有历史 settings 污染，`BUILD-04` 的"首启无预警"是代码+横幅结构推断，非首启实测。
6. **beta16 五项微基准（424ms/871ms/2740ms/3996 字符/1.09ms）** —— 本轮无原始日志可对账。
7. **beta17 全量构建"5 warnings"** —— 本轮只能引出增量构建（0 警告），5 条警告的原始读数未复现（`BUILD-03` 的"全存量"结论来自 pack 日志比对，属侧证）。
8. **beta15 "contract 164/0"** —— 与 `mirror-parity` 的"调用位 102"是运行次数 vs 调用位两种口径，不可直接对账。
9. **`COLOR_MATRIX_9X9_2026-11-14.csv` 的矩阵取值** —— 未独立重推导，本批只判色彩语义。
10. **ProPhoto（非 D65）源 → AVIF 的 `--cicp 2/2/0` 兜底分支与 D50 适应烘焙** —— 缺夹具，未实测。
11. **HDR（PQ/HLG）源在 AVIF 双写下 ICC `cicp` tag 与容器 nclx 的一致性** —— 只测了 SDR P3。
12. **exiftool 重写 iCCP 后 cICP 是否逐块保留** —— 本机 `perl532.dll` code 126 卡住直跑；CICP 不可命名 ⇒ exit 91 零产物也未亲手复跑（由 color-wiring §(14) 覆盖）。
13. **APNG 帧保持数帧** —— 代码结构级识别 `acTL` 跳过，未实测。
14. **runner 式门禁 rc** —— 被残留死锁 + STALE mtime 闸挡住，未重建共享 bin。
15. **bundle header 6 字节差的确切字段** —— 按经验是 timestamp/build-id，未精确取证。

---

## 9. beta17 处置建议

**结论：修复后按 beta18 重发**（现有 tag `1.6.0BETA17` **不动、不重指、不删**）。

### 理由

- 它**不是**"必须撤回"的级别：无 CRITICAL；默认路径（engine/auto）色彩、AVIF、oxipng、UI 均正确；两个包能跑，"app 包缺 oxipng"是显式降级且有日志。
- 它**不能**按现状"接受为可交付 beta"：P1 里有两条静默丢色标的通路（`COLOR-07` 是本批新引入），而且发布提交上两条在册门禁是红的、release 说明没登记红态。以 beta 名义分发一份"自己的门禁说不合格"的包，是把问题推给用户去发现。
- 因此既不是"仅保留源码"（包本身可用、没有必要作废），也不是"接受"（红门禁 + 新回归未修）。

### 建议修复顺序（对应 beta18，按性价比排序）

1. **`COLOR-07`**（P1，本批新引入，最优先）：`QueueProcessor.cs:3020-3047` 的 `planPromisesGeneratedIcc` 不该把 legacy 直连的 carry 认成"计划自生成"；判定需区分"计划将自生成"与"计划要求携带源"。
2. **`COLOR-01`**（P1）：`Decision.cs:569-582` 的 `pngCicpFirst` 分支要看 `DeclaredStrategy`，显式 carry 时保留 iCCP。
3. **`MA-01`**（P1，治理）：把 `verify-decision-delivery` 与 `verify-probe-mirror-parity` 修绿并**同批**抬登记表（`$counts['contract']` 101→102、总量 1005→1006），出包前置加一份"本批变更链路必跑门禁清单"，并把红态写进 release 说明。**红门禁不允许随包发布**。
4. **`COLOR-02`**：`verify-decision-delivery` 的 ⑯/⑰b/⑱-4 随 avif16 路由改写断言（fail-closed 91 是被设计行为，断言应期待拒绝而非 exit=0）。
5. **`UI-01`/`UI-02`**：把"停止"收敛为单一状态真值（返回串与 `IsStopped` 同源；优雅停止退出前清 `_cts`），并给 G2 类断言补管道支路。
6. **`MA-02`**：`AGENTS.md §7` 新写一条更正条目（AVIF 双写、473→487），6 份带日期账本**不回改**，只在 INDEX 或 §7 里指一条"已过期"的注记。
7. **`BUILD-04`+`UI-04`+`MA-03`**：启动横幅补 oxipng 一档（缺失时明确写"未检测到，pngopt 将跳过"）；`ui-panel-visibility` 补 2 条断言（静态 PNG 显示 / APNG 隐藏 / 默认 max）；`pack.ps1` 对 app 档加一份显式依赖清单（至少列出 Plan 缺失的后果）。
8. **`BUILD-01`/`BUILD-02`/`GATE-03`/`GATE-04`/`GATE-06`/`UI-03`/`UI-05`/`UI-06`/`COLOR-06`/`FMT-01`/`FMT-02`**：随 beta18 顺带清理（全档 `robocopy` 全路径、AOT 包哈希口径写清、INDEX 补 11-15、CHANGELOG 补读数路径、CTS Dispose、文案矛盾、探测取消、门禁互相对撞）。

### 禁止动作（本轮已遵守，后续同样适用）

禁用 tag 移动、禁止改版本位、禁止重新出包、禁止 `git commit/push`、禁止改 `src`/`tests`/`pack.ps1`/`CHANGELOG`/`docs` 正文。beta18 的修复与整改应在**下一轮**作为独立批次落地，并为每条修复配一条能"真红"的负控。

---

## 10. 复现入口（最小）

```powershell
$env:TMP=$env:TEMP=$env:TMPDIR='C:\PLAN\ffmpegPictureUI\tests\output\_tmp\review-2026-11-16\rerun\tmp'
pwsh -NoProfile -File tests\scripts\_run-step3-gates.ps1 -Scripts verify-decision-delivery.ps1
pwsh -NoProfile -File tests\scripts\_run-step3-gates.ps1 -Scripts verify-probe-mirror-parity.ps1
pwsh -NoProfile -File tests\scripts\verify-ui-split-union.ps1
```

单条门禁裸跑只适合调试，不算验收；出包前置应以 runner 为准。
