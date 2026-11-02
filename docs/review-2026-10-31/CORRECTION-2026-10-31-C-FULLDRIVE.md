# 更正条目 C：全量推进未了结项（2026-11-01 批次）

> 这是一份**追加更正**，不是改写。被更正的三份原档一字未动：
> `AUDIT-独立性审查-2026-10-31.md`、`CORRECTION-2026-10-31-S8.md`、`CORRECTION-2026-10-31-B-2REDS.md`。
> 带日期的账本是时间点记录，要更正就新写一条——这条规矩对本文件同样适用。
>
> 本批的输入是 `docs/review-2026-10-31/HANDOVER-2026-10-31.md`（写就时在仓库根、未跟踪，2026-11-02 按 §1 移入本目录）与 `docs/HANDOVER.md` 的 H-20，目标是把 AGENTS §7 上
> 列出的**全部**未了结项一次性推进（不含需逐项裁决的审查档 §7.2 / §7.3 的 8 项）。
> 每个数字都绑定一条实跑日志；**没跑过的格子写「待测」，不写结论**。

---

## 0. 一句话结论

本批把 AGENTS §7 的 11 条未了结压到 **3 条**（ACR 全量、`colorRange` 漏拷、`FfmpegGui.Core` 抽取），
其余 8 条全部落地并各带判据。整轮脚本清单 **87 → 94 条**，UI 执行面 5 条从单体重合改为 5 个拆分件、
条数锁全部由下界收紧为精确等值。整轮读数见 §9。

---

## 1. 清单侧：8 支清单外脚本处置完毕（⑪-d WARN 清零）

`_probe-test-group-coverage.ps1` 的 ⑪-d 长期只 WARN 不判红，登记的是「有判据却不进整轮」的脚本。
本批先取到确切名单（8 支），再按性质分两类：

| 脚本 | 性质 | 处置 |
|---|---|---|
| `run-color-tests.ps1` | 真门禁（旧驱动：直驱 PLAN 工具，不起产品） | **接进清单**，第 88 条 |
| `run-quality-tests.ps1` | 同上 | **接进清单**，第 89 条 |
| `run-thread-tests.ps1` | 同上 | **接进清单**，第 90 条 |
| `verify-color-matrix-full.ps1` | 真门禁（源×目标×位深×容器网格） | **接进清单**，第 91 条（受轮跑默认 quick 档） |
| `verify-oracle-matrix.ps1` | 真门禁（执行层） | **接进清单**，第 92 条（经 `$scriptArgs` 只跑 `-Smoke`） |
| `verify-release-package.ps1` | 真门禁（出包独立验收） | **接进清单**，第 93 条 |
| `_accept-ui-split.ps1` | **验收脚手架**（不是判据，是被别的门禁临时用的辅助） | ⑪-d 有据豁免 |
| `_gen-ui-split.ps1` | **生成器**（生成物不手改的出口） | ⑪-d 有据豁免 |

三支旧驱动补的是 house 契约，不是判据：①末行必须是 `PASS=n FAIL=m`、fail ⇒ exit 1；
②前置钉扎 `TMP/TEMP/TMPDIR` 与 `DOTNET_ROOT`（AGENTS §4）。**不补这两条它们就不是门禁**——
一条不 return 非零的脚本永远绿，一条被沙箱拦 `%TEMP%` 的脚本永远红。

豁免必须**带牙**：`$debtWaived` 表里每一项要写 `CoveredBy`（引用它的受管门禁名列表），
⑪-pre 逐项核对这些门禁仍在 `$scriptList` 里；引用不到受管门禁 ⇒ 当场红，
防止「豁免表」退化成「免死金牌」。

实测（逐支手跑快照，**无落盘日志**；所以下面只以计数为结论，整轮 run d 的计数与它们一致）：
`run-color-tests` **24/0 @21.5 s**、`run-quality-tests` **18/0 @110 s**、
`run-thread-tests` **11/0 @45 s**、`verify-color-matrix-full`（quick）**24/0 @257 s**、
`verify-oracle-matrix -Smoke` **15/0 @7.5 s**、`verify-release-package` **16/0 @1.0 s**。
⚠ 整轮里只有 quality（92.2 s）与 thread（56.1 s）会打秒；其余四项不打，故 TESTING §8.4 已改为只记计数。
⇒ ⑪-d **WARN 清零**，脚本清单 **94 条**，`shape-selfcheck=OK（17 针）`。

### 1.1 三支旧驱动的入列前病灶（都是「尺子坏了」不是「产品坏了」）

- **`DOTNET_ROOT` 被劫持**：本机上 `DOTNET_ROOT` 默认指向 AutoClaw 自带的 dotnet 8.0.31，
  `pngcicp.exe`（net11.0）起不来 ⇒ 整支假红。修法 = 脚本自修复：若 `DOTNET_ROOT` 指向的
  `dotnet.exe` 不存在或主版本 < 所需，就改指 `C:\Program Files\dotnet`，并在 stdout 点名改过。
- **`%TEMP%` 被沙箱拦**：直接跑 `FfmpegGui.exe` 会 `Permission denied` → `exit -13`，且**批量**发生。
  修法 = 前置 `TMP/TEMP/TMPDIR` 全部指向工作区内的 run-dir。
- **release 残留**：`verify-release-package` 缺省 `PKG_VER` 时原来靠人传；改为从盘上归档名反推，
  多版本歧义或无归档 ⇒ fail-closed（宁红不猜）。

### 1.2 三处「裁判侧漂移」（入列前必须先修，否则新门禁一进门就红）

| 病灶 | 现象 | 修法 |
|---|---|---|
| `_lib-oracle.ps1` 的 `Get-ColorMetadata` | jxlinfo 新版把 `primaries` / `transfer` 拆成**独立行**，旧解析取不到 | 补**行级回退**（新词形 P3 / Rec.2100 / sRGB / 709 本就在共享词形表里） |
| LEGACY 的 jxl 标签对照 | 走了分家字面量，与共享词形表分叉 | 改回共享词形表，新增 `Get-TargetTransferRx` |
| LEGACY 像素参照（bt709 档） | 用 zimg 纯 γ 当参照，同产物对撞只有 **23.36 dB** | 换 **BT.709-6 定义式 OETF** ⇒ **50.92 dB**；恒等格（源域==目标域）改钉「不得乱动像素」按容器阈值 png 40 / 其余 26 |

第三行是典型的**裁判比被测更松**：23.36 dB 会让「真做错了」也过。换参照后同一产物 50.92 dB，
说明产品本来是对的，是尺子错了。

---

## 2. 宿主 TEMP 站点：第 94 条文本级锁

AGENTS §7 记的是「宿主 TEMP 站点无源级锁，本轮只修了写了又从不删的 4 处」。
本批先把三个固定名 scratch（`ServiceProbe` 的 `tooldetect_<guid>` / `cmsbench` / `cmsx`）逐一查生命周期，
结论是**它们不需要改码**（不乘份数、尺寸小、`tooldetect_` 有 `finally` 删、`cmsbench` 是有意缓存），
所以处置是「以生命周期注记进登记表」，而不是硬改产品。

新门禁 `_probe-host-temp-sites.ps1`（第 94 条，**5/0**）三把尺全部 fail-closed：

1. **站点数冻结**：每文件 `GetTempPath` / `GetTempFileName` 站点数合计 **39 站点 / 7 文件**。
   新增站点必须同批改表，并在表里写明生命周期（删/缓存/手工档）。
2. **坏根名黑名单**：`gate_tmp|gtmp|roundtmp|_ramdisk|faketemp`。历史上有 12 个自造 TMP 根，
   把同一份 187.6 MB 废弃 `cap.dng` 乘成 12 份。`Harness.cs:67` 的记录是注释 ⇒ 先剥注释行再判。
   收紧过程有两次假阳性：`pngTmp` 与 `FFMPEGGUI_RAMDISK` 两族撞名 ⇒ 改为**大小写敏感 + 路径段分隔**。
3. **扫描面自证**：文件总数 = 0 或与对账数不符即红；对账函数另用合成输入自检（防止对账本身写错）。

⚠ 边界如实记：这条锁只回答「站点在哪、根名对不对」，**不回答「写完删没删」**。
后者要数据流分析，属独立一批。

---

## 3. 白点内核（S8）的三笔账

### 3.1 `pixFmt` 回拷漏项（本批已修）

`FfmpegCommandBuilder.cs` 的缓存回拷段漏拷 `pixFmt`（也漏 `colorRange`）⇒ 命中缓存时 `pixFmt == null`
⇒ 8-bit 源恒按 **65283** 尺归一 ⇒ 16-bit 交付顶码 **65532**（应为 65535）。
这一笔同时把 H-19 记的**敞口①**（gray8 / 8-bit JPEG 顶码 65280 ⇒ 16-bit 交付 65532）收口，两处改动缺一不可：

1. **`pixFmt` 回拷**（`:1654` 插 4 行）：`pixFmt` 只在 `ProbeWithFfprobeOnce` 的**局部结构**里被赋值，
   从来没拷回外层 `meta` ⇒ `ProbeInputPixFmtForEngine` 恒得 `""` ⇒ **任何**载体细分都不可能生效
   （这也是为什么"细分代码写了却不生效"值得单独记账：没有回拷，代码看着是对的）。
2. **载体细分**（新常量 `TransformSpec.SrcCodeMaxGray8 = 65280.0`）：`ResolveSrcCodeMax` 由
   「只看位深」改为「位深 + `pix_fmt` 载体」两维，`gray*` / `yuvj*` 用 65280，其余 ≤8-bit 仍用 65283。
   探测失败 / 管道输入 ⇒ `pf=""` ⇒ 落 RGB 尺，即**不猜**（= 修改前行为）。

第 87 条复跑 **35/0**（旧 27 条读数未变，B4 组 8 条为本批新增臂）：

| 臂 | 未修件（H-19 那版，`f89b247b60dcf1ef`） | 本批 |
|---|---|---|
| A 组 `K8_p3_to_srgb` 8-bit 交付白 | **254** | **255** |
| B3 `K8_to_png16`（8-bit 源 → 16-bit 交付） | **65283** | **65535** |
| B4 `K8gray_to_png16`（gray8 源 → 16-bit 交付） | 65532（载体系数错） | **65535** |
| B4 `K8jpg_to_png16`（yuvj444p 源 → 16-bit） | 65532 | **65535** |
| B4 机制臂 `w8_gray.png` 过 `rgb48le` 的顶码 | — | **65280**（载体前提，非交付读数） |
| B4 机制臂 `w8_yuvj.jpg` 同上 | — | **65280** |

⚠ 读法：B4 里 `w8_gray.png max=65280` / `w8_yuvj.jpg max=65280` 是**机制臂**（源穿过 `format=rgb48le`
的顶码），不是交付读数——它们的 16-bit 交付在 `K8gray_to_png16` / `K8jpg_to_png16` 两臂里，都是 65535。
两族差 3 码（0xFF00 vs 0xFF03），混起来看会以为"修了还是 65280"。

`grayLike` 只认 `gray*` / `yuvj*` 两族，**不外推**：`yuv420p`（受限范围）/ `pal8` 等其余 8-bit 载体的
扩位顶码未实测 ⇒ 维持 RGB 尺并在注释里点名。

### 3.2 `colorRange` 漏拷（**本批不修，已登记**）

同一个回拷段、同一个坑：`colorRange` 也不在里面。它不在本批修，理由是它需要**自带判据臂**
（pc / tv 两种 range 的顶码与脚码读数各一组），否则改了也没人看着。
已在 AGENTS §7 记一条，须独立一批。

### 3.3 10/12-bit 载体是否有同类敞口（读数结案，不改码）

问题是「bd>8 是否也该按 65535？如果是，S8 的敞口不止 8-bit」。实测：

| 载体 | 探针 | 读数 |
|---|---|---|
| `w10.avif`（yuv444p10le tv crf0） | 转 rgb48be 后取样 | **max=65506**，uniq=1 |
| 同上，其 yuv10 取样 | `ffprobe`/解码侧 | **max=943** |
| `w10_pc.avif` | 同法 | **1023 / 65535** |
| `w12_pc.avif` | 同法 | **65535** |
| `w10.jxl` | `djxl` → rgb48be | **65535**，uniq=1 |

`w10.avif` 的 65506 差额归因**编码有损**（其 yuv10 取样顶码 943 已说明源侧就没到 1023），
不是内核按错尺。产品三臂 `avif10tv` 65506 / `avif10pc` 65530 / `jxl10` 65535 与探针一致；
但 `jxl10` 臂的日志自证「`.jxl` 专用输入管线…色彩引擎尚未接入该路径」⇒ **JXL 输入不走内核**，
它的 65535 不能当内核判据。
⇒ **bd>8 按 65535 的假设在 10/12-bit pc 载体上成立，无 S8 类敞口。**
H-19 记的**敞口②**（"10/12-bit 扩位顶码未实测"）由此以**读数**收口，不需要改码。

### 3.4 `65283` 判尺的适用面（全库对撞）

`65283` 这个数很反直觉（≈×256 定点 + ≤3 码抖动，不是 257 的整数倍），所以必须证明
「`BitsPerSample ≤ 8` 且 ffprobe 载体 > 8-bit」这个组合真的存在，否则这条判尺是空转。

对撞 1246 份 DNG/TIFF/RAW：**0 份**满足（exiftool 首处 `BitsPerSample ≤ 8` 且 `ffprobe` 载体 > 8-bit）。
构造多 IFD 件（IFD0 8-bit 缩略图 / IFD1 16-bit 主图）：exiftool 读 `8 8 8`，而 `ffprobe` 与解码器
**同样**只看到 16×16 缩略图、出口 16×16 ⇒ 载体确为 8-bit，判 65283 正确。
另记一处：`"8 8 8"` 这种三分量串在 `FfmpegCommandBuilder.cs:1547` 的 `int.TryParse` 会失败，该分支本就不生效。

⇒ **最小复现夹具不存在**：触发需「exiftool 单值 ≤8」+「ffprobe 载体 >8-bit」+「解码侧真 16-bit 且不经
`dngtool` 中转」三者同时成立，而这三者互斥。这条结论**替代**了「造个小文件试试」的思路——
造不出来不是没试，是结构上不可能。

---

## 4. `.xaml` 的 `x:Name` 为什么不生成字段（机制查清）

`_probe-i18n-scan` 按「根目录下的 `.axaml` 窗口」点名，而仓里 `.xaml` / `.axaml` 并存是迁移化石。
本批先查机制再动：

- Avalonia 的**隐式 glob 只收** `**/*.axaml` 与 `**/*.paml`
  （`avalonia/12.1.4/build/AvaloniaBuildTasks.props:8-9`）。
- ⇒ `.xaml` 只被当**资源**、不做 XAML 编译 ⇒ 不生成 `x:Name` 字段 ⇒ 引用即 `CS0103`。
- `MainWindow.xaml` 之所以能工作：`MainWindow.xaml.cs:7379` 有**手写**的 `InitializeComponent()`，
  262 个控件全部由 `FindControl<T>` 取。`ProgressWindow.xaml` 同款（12 个 `x:Name` / 11 个字段）。

`.xaml` 全盘点：`App.xaml`(0) · `MainWindow.xaml`(handLoader, 262) · `ProgressWindow.xaml`(handLoader, 12) ·
`Controls/MetadataEditor.xaml`(6) · `FormatFilterWindow.xaml`(1) · `ImageDetailWindow.xaml`(29) ·
`PresetManagerWindow.xaml`(4)。

修法（`FfmpegGui.csproj`）：

```xml
<AvaloniaXaml Include="**/*.xaml" Exclude="MainWindow.xaml;ProgressWindow.xaml" />
<AvaloniaResource Include="**/*.xaml" Exclude="@(AvaloniaXaml)" />
```

选「统一 `.xaml` 并显式 Include」而不是「全改回 `.axaml`」：改名会动 8 个文件路径 + `_probe-i18n-scan`
的点名规则，属独立一批。两个手写 loader 的窗口必须 `Exclude`，否则生成字段与手写字段 `CS0102` 撞名。

整建过程三轮才绿：首轮 18 个 `CS0103` → 只加 `AvaloniaXaml` 后 11 个 `CS0102` → 加 `Exclude` 后 0 警 0 错（50.81 s —— ⚠ 该读数**无落盘日志、不作判据**，2026-11-01 复审订正）。
dll 资源实测：7 个 `.xaml` 各 1 份、所有 `.axaml` **0 份**。全仓 `.cs` 无 `avares://…axaml` 引用
（唯一 `avares` 是 `App.xaml.cs:78` 的 `icon_raw.png`）⇒ 去掉 `.axaml` 的 XAML 编译没有断引用。
双探针回归：`_probe-i18n-scan` **11/0**、`_probe-cjk-hardcode-scan` **14/0**。

---

## 5. UI 执行面迁移：单体 → 5 个拆分件（本批完成）

### 5.1 账上「7 条」是误记，真实构成是 5 + 1 + 1

`rg -l UiTestHost tests/scripts` 命中 15 支，逐支定性：

- **5 条已切**：`verify-ui-host` / `verify-ui-param-matrix` / `verify-ui-strategy-map` /
  `verify-ui-param-defects` / `verify-cli-strict`；
- **1 条有意不切**：`verify-ui-split-union`（单体是它的参照臂，切了就没有对撞对象）；
- **1 条误记**：`verify-png-structure.ps1` 里对单体只有一句历史**注释**、零处调用；
- 其余 8 支是生成/验收脚手架（`_accept-ui-split` / `_gen-ui-split` / `verify-ui-split-{reproducible,union,verbatim}`）
  或只是注释（`_probe-gui-control-wiring.ps1`）。

### 5.2 mode→工程映射与条数锁

| 门禁 | 执行体 | 臂 | 锁 | 实测 |
|---|---|---|---|---|
| `verify-ui-host` | 5 件 | 5 | 逐件 **75/53/89/141/72** + 合计 **430** | 430/0 @52.3 s |
| `verify-ui-param-matrix` | `Tests.UiSweep` | `ui-sweep` | J **98** | 98/0 |
| `verify-ui-strategy-map` | `Tests.UiColor` | `ui-strategy-map` | K **27** | 27/0 |
| `verify-ui-param-defects` | `Tests.UiSweep` | `ui-param-defects`+`ui-sweep` | L **43** + J14d **9** | 52/0 |
| `verify-cli-strict` | `Tests.UiCli` + `Tests.UiColor` | `ui-cli-strict`+`ui-color`+`ui-icc` | M **60** | 87/0 |

**条数锁由下界收紧为精确等值**是这一步的核心。旧写法 `-ge 88` 对「等于当前实测 98」的集合没有牙：
删掉 10 条断言仍然绿。精确等值把「断言被删」变成红。

**合计锁是第二道**：只锁逐件，则「同时改小某件期望值 + 删该件断言」会自洽通过；加上合计 430 就露馅。

**`-SelfCheck` 必须逐臂**：只验一臂的话，其余臂的解析器坏了不报警。

### 5.3 负控先行（4-2 顺序）

先给 `UiHostShared` 加 `NegCtl` 静态位 + `--negctl` 参数分支（分支必须**先于**「未知参数」解析）+
`RunNegativeControlIfRequested()`（读环境变量 `UIHOST_NEGCTL=="1"`、打印 `########## NC: …`、
**逐字复用单体**的 NC1/NC2），5 个拆分件在 `UiHost.Finish()` 前调用它。
这样切执行体之后，负控仍然证明「判据不是恒真」。

实测：`Tests.UiSweep.exe <src> ui-param-defects` + `UIHOST_NEGCTL=1` ⇒ **44 PASS / 1 FAIL**、exit=1，
NC 行与单体一致。

### 5.4 文件锁假红（跨件门禁的通病，根因已定）

**现象**：`summary=False pass=-1 fail=-1`，重定向的输出文件读不出来。

**根因**：`Start-Process -RedirectStandardOutput` 的句柄会被**遗留孙进程**继承。
实测是 `ffmpeg`（pid 82716）：宿主 exit 之后它还在跑约 1 s，期间父进程 `ReadAllText` 抛 `IOException`。
`$p.Dispose()` **无效**——句柄在孙进程手里，父进程放不掉。

**修法**：`Read-TextShared()`——`[System.IO.File]::Open(Open, Read, ReadWrite)` + `StreamReader` +
25×200 ms 重试，失败返回 `''`（让「汇总行缺失」照常判红，不吞错）。
5 支门禁统一改；不删子进程、不回退 `%TEMP%`、不用 `Dispose`。

### 5.5 临时目录进产物区

5 条 UI 门禁的 run-dir 由 `GetTempPath()` 改为 `tests/output/<组名>/<guid>`
（`ui-host` / `ui-param-matrix` / `ui-strategy-map` / `ui-param-defects` / `ui-cli-strict`）。
两条理由：①本机沙箱按进程拦 `%TEMP%` 写 ⇒ 宿主与 `ffmpeg` 批量 `exit -13` 假红；
②AGENTS §3 的「产物只有一份、且受 `_sweep-output.py --runs` 管辖」。

### 5.6 runner 同批改齐：第 ⑨–⑬ 对

执行面进了新 exe，新鲜度闸必须同时进，否则会拿**陈旧拆分件**报绿，而读数被算成本轮实测。
新增 `$uiSplitShares`（`Tests.Shared` + `UiHostShared` 两个共享库的最新源）+ `$uiSplitPairs`（5 件）
+ `$uiSplitExes`，逐件比 mtime 且**不存在即拒**。

⚠ 源码面**必须含两个共享库**：拆分件的大部分判据体在这两者里，只比 `ui-*/**` 会漏掉
「改了共享库但没重编拆分件」——这正是这对要防的那件事。

`artifact-sha16:` 行也追加 5 个拆分件 exe 的 `sha16`（现共 10 项）。

---

## 6. 谁看着这些改动（判据清单）

| 改动 | 判据 |
|---|---|
| 5 条 UI 门禁切拆分件 | 各自的 `-SelfCheck`（逐臂负控）+ 本轮整轮 |
| 条数锁精确化 | `-SelfCheck` 报 `expected N` vs 实测，任一侧偏即红 |
| `NegCtl` | `-SelfCheck` 的 `exit=1 fail=1` 逐臂 |
| `Read-TextShared` | 假红消失（`pass=-1` 不再出现）；崩溃路径仍有牙（返回 `''` ⇒ 汇总行缺失 ⇒ 红） |
| runner 第 ⑨–⑬ 对 | 删掉一个拆分件 exe ⇒ 拒跑；改共享库不重编 ⇒ 拒跑 |
| 三支旧驱动入列 | 末行 `PASS=n FAIL=m` + fail⇒exit 1（house 契约） |
| `_probe-host-temp-sites` | 站点数/黑名单/扫描面自证三把尺 |
| `pixFmt` 回拷 | 第 87 条 B4 臂（`K8*_to_png16` = 65535） |
| `.xaml` → `AvaloniaXaml` | 整建 0/0 + `_probe-i18n-scan` 11/0 + `_probe-cjk-hardcode-scan` 14/0 |
| ⑪-d 清零 | `_probe-test-group-coverage` 输出（`debtWaived` 带牙） |

---

## 7. 本批**没做**的事（明确记账）

1. **AUDIT §7.2 / §7.3 的 8 项**：需逐项裁决，不属「推进未了结」。
2. **`FfmpegGui.Core` 抽取**：会改出货件程序集构成与指纹，是独立决策。分层本身没退步
   （`Services` 对 UI 零依赖仍成立，唯一倒灌仍是 `IpcService.cs` 认 `MainWindow` 类型 13 处）。
3. **`docs/TESTING.md` 旧路径改写**：正文 38 处 `tests/ServiceProbe` 等按原样保留，顶部读法说明已在。
   逐条改写会连带改动同段历史读数。本批只在**文件末尾**追加 §八（见 §8）。
4. **`colorRange` 漏拷**：见 §3.2。
5. **`_guishot.ps1`** 留在仓库根（gitignore），不挪进 `tests/scripts`（会被算成清单外未登记脚本）。
6. **`.axaml` → `.xaml` 改名**：本批只修机制，不动文件路径。
7. **`tests/output/review/`**：名字是常用词而被引用判定永久保护，没删。
8. **`-Probes ''`**：会给「无探针 mode，拒绝空跑」早退（exit=2）。冒烟至少给一个快 mode。

---

## 8. 文档侧同批改动

- `docs/TESTING.md`：**只在末尾追加 §八**（8.1~8.6）。行号锚不动是有原因的：
  `_probe-test-group-coverage.ps1:756` 引用 `docs/TESTING.md:1115`、
  `_probe-cjk-hardcode-scan.ps1:250` 引用 `docs/TESTING.md:1828`
  ⇒ 在中间插入任何内容都会让这两条门禁的锚点失效。
- `AGENTS.md` §7：执行面迁移改写成「已完成（2026-10-31）」并附读数；宿主 TEMP 站点改写成「第 94 条已上锁」；
  视图扩展名条目补 W3d 机制；新增 `colorRange` 漏拷一条。
- 本文件 + `docs/ACR_FULL_ROUTE_PLAN.md`（ACR 全量的**设计**，不含实施）。
- `docs/INDEX.md`：`review-2026-10-31` 计数 3 → 4（本文件）+ 承重表新增 `ACR_FULL_ROUTE_PLAN.md`。
- `docs/HANDOVER.md`：追加 **H-21**。

---

## 9. 整轮读数

见 `docs/HANDOVER.md` H-21 的「整轮」段与本文件末段的实测追加。**（本文件首次落盘时整轮尚未跑完；
跑完后在下方追加结果行，未跑完不写结论。）**

### 9.1 run a（`round-full-20261101`）—— **作废**

`verify-color-wiring` 由基线 **153/0** 掉到 **60/56**。取证 `tests/output/validate/wire_b2df2ab4/eng_p3srgb.o`：
ffmpeg 写 `%TEMP%\cms_8fc8….rgb48` → `Permission denied` → `exit -13` → 产物 0 B。
定性 = 本机沙箱**按进程**拦「子进程向仓库外写」：ffmpeg 写 `%TEMP%` / `C:\Temp` 均 `exit -13`，写仓库内
`tests/output/_tmp/` `exit=0`；而 `.NET` / python 写 `%TEMP%` 成功 ⇒ 只拦 ffmpeg 那类。**非代码缺陷**，读数作废。

### 9.2 run c（`round-full-20261101c`）—— 4 红

| 红项 | 定位 | 处置 |
|---|---|---|
| `_probe-matrix-structure.ps1` | 内层 `pass=37 fail=1`：`FAIL A30 … not on FfmpegCommandBuilder.cs:2408 (found at line 2422)` —— W3a 的 +14 行导致行号漂移 | pin `2408 → 2422`（改前核对 `:2422` 确含目标字面）⇒ 单跑 **38/0** |
| `verify-ipc-extra-options.ps1` | 外部 `FfmpegGui` 实例 pid 79076（`publish/build/FFmpegPictureUI-v1.6.0-beta10-x64-full`）占管道名 | `taskkill /F /PID 79076` ⇒ run d **27/0** |
| `verify-release-package.ps1` | `full 磁盘目录与归档同数 目录 584 vs 归档 582` | 见 9.3 |
| `bench`（ServiceProbe mode） | `pass=36 fail=1` | 见 9.4 |

### 9.3 584 vs 582 = 外部实例残留（**不改判据、不重出包**）

`_diffpkg.py` 对撞：磁盘侧多出 **2 项** —— `debug.log`（3,384 B）与 `settings.json.bak`（1,270 B），
mtime `2026-11-01 18:33:12 / 18:33:39`，正是 pid 79076 那次启动（18:33:11）运行时写入。
已移出解包目录并留证 `tests/output/_tmp/pkg_residue_20261101c/`：
`debug.log` sha256 `74FD150F…EF0E9`、`settings.json.bak` sha256 `16C44FA0…C5048`。
包本身完整；run d **回 582 对齐、`16/0` 绿**。

### 9.4 run d（`round-full-20261101d`）—— **93/94**，唯一红 = `bench`（负载型，非回归）

- `runner-self-sha256=FC66A0A36C0311B7 lines=1775 bytes=181569`，**启动与收尾两处同值**（整轮期间运行器未被改动）；
  `script-list=94 条（受管基线 94）`、`shape-selfcheck=OK（17 针）`。
- 起轮前先钉 TMP：`env-temp-before=(空) | C:\Users\…\AppData\Local\Temp` →
  `env-temp-pinned=…\tests\output\_tmp\round-tmp` → `env-temp-writable=OK`。
  **`verify-color-wiring` 回 `PASS=153 FAIL=0`（74.1 s）⇒ 9.1 的修法在整轮内生效。**
- 绿项（节选，全为 run d 实测）：`verify-ui-host 430/0`、`verify-ui-param-matrix 98/0`、`verify-ui-strategy-map 27/0`、
  `verify-ui-split-union new=430 old=430`、`verify-probe-mirror-parity`（61 个 `Probe*` 零第二份定义）、
  `_probe-matrix-structure 38/0`、`verify-ipc-extra-options 27/0`、`verify-release-package 16/0`、
  `_probe-host-temp-sites 5/0`（39 站点/7 文件冻结）、`_probe-white-point-kernel 35/0`（顶码 65535 / 直通段 252 码）、
  `run-quality-tests 18/0`、`run-thread-tests 11/0`、`run-color-tests 24/0`、`verify-color-matrix-full 24/0（quick，307 s）`、
  `verify-oracle-matrix -Smoke cases 3/3`、`verify-usability-e2e 84/0`、`verify-gainmap-isobmff 80/0`、
  `verify-color-strategy 78/0`、`verify-simd-switch-fallback 17/0/1skip`、`verify-acr-route 31/0`（⑫ 中位比 0.5978）。
- 红项：`[bench] exit=1 PROBE RESULT: pass=35 fail=2  73.7s`
  - `FAIL PQ→sRGB(变曲线+裁切) 多核扩展比 ≥4×（实 3.5×）`
  - `FAIL 8b sRGB→P3（纯 SDR 快路径） 单核 ≥25MPix/s（实 22.0）`

### 9.5 `bench` 定性：负载型计时红 —— 两条判据的证据强度不一样

按 AGENTS §4「计时类红要用旧二进制做对照臂」，建**仓库外** worktree（`f778280`，detach）。
⚠ 臂身份只能拿 **dll** 哈希判：`*.exe` 是 apphost，两棵树编出来同值 `93BF03789E69EC6F`。

| 判据 | A 本轮二进制（`ServiceProbe.dll C0EA213F057F6AA2`） | B 旧二进制（HEAD `f778280`） |
|---|---|---|
| `8b sRGB→P3 单核 ≥25MPix/s` | 整轮 **22.0 FAIL**；静置 4 次 **27.1 / 26.7 / 35.6 / 27.8 全 PASS** | **25.0 FAIL**（`bench_control.log:205`） |
| `PQ→sRGB 多核扩展比 ≥4×` | 整轮 **3.5 FAIL**；静置 4 次 **7.5 / 7.4 / 5.9 / 6.3 全 PASS** | **5.8 / 6.9 / 6.9 / 8.6 全 PASS**（`:165/:175/:185/:196`） |
| 其它 | 静置 4 次全 `PROBE RESULT: pass=37 fail=0` | `pass=34 fail=2`；除 `25.0` 外还有 `:208 端到端基准图生成失败` = 环境性红（`:109` 无 `publish/PLAN`） |

- **两条红要分开写**：`8b ≥25MPix/s` 是**新旧两臂都会红**（本机对该地板只余 0~10% 余量）；
  `多核 ≥4×` 旧臂全 PASS、只有整轮那一次红 ⇒ 后者是**负载敏感项**。首版把两者写成「旧臂同族红」是错的。
- **`3.9×` 不是任何一方的 FAIL**：它是 `bench_control.log:17` 里**本轮二进制**的小帧扩展比「仅记录」行。
- 两臂**负载不等价**：静置 4 次分档内负载读数 9%~22%（`load=100%` 的是 WMI 的 `LoadPercentage`，
  与分档内 CPU 增量不是同一口径）；旧臂被「未找到 `publish/PLAN`」拖到 ≈58%（`:110`）。
- 读数组：静置第 1 次与旧臂 = `tests/output/_tmp/bench_control.log`（`:100` / `:80`；旧臂 `:205`、`:165-196`），
  后三次 = `tests/output/_tmp/bench_static3.log`（`:98`/`:78`、`:202`/`:182`、`:306`/`:286`）。
  另 run a 尾部 `[bench] exit=0 pass=37 fail=0 79.9s` 是整轮内跑出的第三条 37/0 证据。

代码面同时排除回归：`bench` 只调 `ColorKernels.TransformRgb48le` / `TransformRgb48leToRgb8`；
本批 `ColorKernels.cs` 的 diff **只有两段注释 + 一个 `const double SrcCodeMaxGray8`**，零热路径改动；
`RawColorPipeline.ResolveSrcCodeMax` 是**每任务一次**的分档，不进像素循环。
⇒ 结论：**负载/计时噪声，不是 W3a 回归**。阈值与判据**一律不动**（维护者在 `Program.cs:8497-8501` 已定纪律：
绝对地板与快路径对账档不依赖重复间稳定性，不因停顿赦红）。

**因此本轮不宣布「全绿」**：run d = **93/94**，唯一红为负载型计时红，已用旧二进制对照臂定性并留证。
未做、值得独立一批：`多核扩展比` 与 `8b 绝对地板` 这两档没有成对采样/停顿保护（只有「关表档增益比」有），
要治须先由维护者给口径 —— 本批不擅自放宽、不擅自加 Skip。
### 9.6 追记：run e 复跑 = 94/94 全绿（§9.5 的 93/94 就此结案）

§9.5 是 run d 时间点的记录，**不回改**；其上「本轮不宣布全绿」的结论由本节取代。

| 项 | run e 读数 | 依据 |
|---|---|---|
| 起轮 | `start=2026-11-01T21:23:10`，收尾 `22:23:50` ⇒ **60.7 min** | `tests/output/round-full-20261101e/env.txt`；`run_full_94e.log` |
| 汇总 | **94 条 gate 行全 `exit=0`**，`===== 运行器汇总：全部通过 =====`，`runner-exit=0` | 同上，**144 行**（`143` 是写就时数错，`wc -l` 实测 144） |
| 运行器自钉 | `runner-self-sha256=F79AB14DAE19B78B lines=1780 bytes=182154`（启动与收尾同值） | `run_full_94e.log` 的 **`:9`（启动）/`:140`（收尾）** 两行（`:853`/`:908` 是**运行器源码**位置，误当成日志行号） |
| 形状自检 | `shape-selfcheck=OK（17 针）`、`script-list=94 条（受管基线 94）`、`env-temp-pinned=…/tests/output/_tmp/round-tmp`、`env-temp-writable=OK` | 同上 |
| 产物同一性 | `artifact-sha16` 10 项与 run d **逐项相同**（`FfmpegGui.dll=653F89FA8473BD5C`、`ServiceProbe.exe=93BF03789E69EC6F`、`Tests.UiCore.exe=FA32A91FB15724B0` …） | 两轮日志同段对撞 |
| `bench` | **`[bench] exit=0 PROBE RESULT: pass=37 fail=0 80.5 s`**（轮内，`:43`） | `run_full_94e.log:43` |

- **run d 的单红复现不了**：同一份产物、同一批门禁、同一天内紧接复跑即 37/0 ⇒ §9.5 的「负载型计时红」定性成立，
  无需再改阈值、无需加 Skip。连同 ARM-A 控制臂 1 次 + 静置 3 次 + run a 轮内 1 次，`bench` 累计 **6 次 37/0**。
- **本批最终读数 = 94/94 全绿**，绑定 `runner-self-sha256=F79AB14DAE19B78B` 与上表 `artifact-sha16` 10 项。
- ⚠ 该绑定只对 run e 时的二进制与运行器成立：run e 之后若再动 `src/` 或 `tests/scripts/_run-step3-gates.ps1`，须重跑整轮再谈绿；
  仅改文档（且不改被门禁读取的锚行号 `docs/TESTING.md:1115` / `:1828`）不破坏该绑定。

### 9.7 二次追记（2026-11-01 复审后）：§9.5「两条都红」被证伪，`bench` 实为**四条**计时判据

§9.5 仍按原样保留为 run d 时间点的判断，**不回改**；本节取代其中两个结论。

1. **`8b ≥25MPix/s` 不是「新旧两臂都会红」**。原旧臂证据（`bench_control.log:205` 的 `25.0 FAIL`）不可复核：
   当次 worktree 已拆、日志只留 apphost 值、生成脚本未入库。2026-11-01 用 `git worktree add --detach … f778280`
   **重建**同一 HEAD 的臂（仓库外 `..\ffmpegPictureUI-wt-bench2`，`publish/PLAN` 可用），2 次全 `37/0`，
   8b 读数 **42.8 / 43.9 全 PASS**（`bench_oldarm_20261101.log:1` 臂 dll `FF05421132B2542B`；`:3`、`:107` 两次读数）。
   ⇒ 那次 25.0 的成因是**采样环境**（该轮 `publish/PLAN` 缺失 `:109` 叠加 ≈58% 负载），不是臂身份差异。
2. **判据条数是四条，不是两条**。`Program.cs` 实读：`8b ≥25`（`:8603`）、快路径 `单核 ≥40MPix/s`
   （`:8488` `floorMpix = fastCell ? 40.0 : 3.0`，判在 `:8490`）、多核扩展比 `≥4×`（`:8544`）、
   端到端 `H1 ≤ H2×2.0`（`:8665`）。**run c 唯一那条红实为快路径档**：`run_full_94c.log:41`
   `FAIL sRGB→P3(矩阵同曲线) 单核 ≥40MPix/s（快路径适用，实 38.5）`——`floorMpix` 在本机取 40.0 而非 3.0，
   故 38.5 落在 40 档之下；§9.5 把它归到 25 档的写法不成立。
3. **端到端档也会红**：同窗对照第 2 次 `FAIL 端到端 H1 ≤ H2×2.0（实 2.69×）`（`bench_oldarm_20261101.log:418`）。
4. 四条**都负载敏感、没有一条是「两臂必红」**；阈值与判据一律不动。run e 复跑 `pass=37 fail=0` 仍成立，
   §9.5 的「负载型计时红」定性不变，只是理由从「两条」改为「四条」。

留证：`tests/output/_tmp/bench_oldarm_20261101.log`（419 行，`:1` 指纹 / `:3`、`:107` 旧臂 / `:212`、`:316` 对照 / `:418` 红行）；
同窗对照臂 `ServiceProbe.dll=C0EA213F057F6AA2`、`FfmpegGui.dll=653F89FA8473BD5C`。

### 9.8 追记（2026-11-01）：托盘/关窗修复 —— 新插问题插队，run f 作废

维护者在 run f 之后报了两个现象（托盘图标找不到 / 关窗后进程仍在），并直接下令「计划一个完整修复，开始修复」。
按维护者的**「先写正文」**口径，本节先记修复与判据，整轮读数在 §9.9。

1. **run f 读数作废（`tests/output/round-full-20261101f/run_full_94f.log`）**：尾部 `runner-exit=1`、
   `===== 运行器汇总：失败 3 项 =====`，三项为 `probe:bench (exit=1, 36/1, 93.7 s)`（未点名哪一条计时判据）、
   `script:verify-encoder-knob-liveness.ps1`（**零断言**：全文无 `PASS=n` 汇总行）与其 `exit=-1`（疑单步超时）。
   这三条**没有定位**。随后维护者下令开修托盘 ⇒ 按「起轮之后不许再动 `src/`」的反向约束，该轮读数作为
   「修复前基线」作废，只留作「修复前确实有 3 项非绿」的证据。**不许**把 94/94 的旧读数（run e）拿来顶替。

2. **修复内容**见 `docs/HANDOVER.md` H-22（六条修法 + 改动面 + 两处编译期教训）。

3. **判据**：第 85 条 `verify-package-gui-starts.ps1` 新增 ⑥（托盘真出现：`tray.log` `create=ok` + `IsVisible=True`）
   与 ⑦（设关臂：`FFMPEGGUI_CLOSE_TO_TRAY=false` ⇒ 图标不亮 + 关窗 6 s 内真退出），④ 由无条件改为条件契约。
   尺的牙用**变异**自证：M1（`IsVisible=false`）打红 ⑥、M2（`Closing` 无条件 cancel）打红 ⑦，还原后 7/0。

4. **判据断言的边界（如实登记）**：
   - ⑥ 断言的是「`tray.log` 说图标亮了」，不是「Shell 里真有图标」。托管侧读不到 `NotifyIcon` 状态；
     `NotifyIconSettings` 注册表实测对功能正常的探针程序**也无条目** ⇒ 不能当判据。
   - ⑦ 断言的是「关窗后进程在 6 s 内消失且 `exit=0`」，不是「托盘图标消失」（进程退了图标必退）。
   - 两条都依赖 `FFMPEGGUI_DIAG_DIR`：门禁把诊断目录钉进 `tests/output/validate/gui_start/<臂>`；
     出货环境不设该变量时日志落缓存目录 —— 判据只在门禁内成立，不是产品契约。

5. **`shutil.copy2` 保留旧 mtime ⇒ MSBuild 跳过编译 ⇒ 假红**：还原变异体那次 `⑦ FAIL` 不是回归，
   是源文件 mtime（00:52:03）比 dll（00:57:08）旧。`(Get-Item …).LastWriteTime = Get-Date` 后重编即恢复。
   这是**流程坑**，记在此处供后来者：任何「备份→还原→跑门禁」都必须 touch 源文件。

6. **连带转红并同批修掉的两条**：`_probe-settings-clone-coverage`（`Save()` 克隆漏 `CloseToTray`）、
   `_probe-cjk-hardcode-scan`（UI 面 922 > 918 ⇒ 新增文案迁 Loc + 托盘诊断行由调用方带 `[tray]` 前缀落 `[tag]` 桶，实测 918 ≤ 918）。

### 9.9 整轮 run g 读数（`tests/output/round-full-20261101g/run_full_94g.log`；⚠ 该轮无 `env.txt`、日志也无起止行，
时间只能给文件时间：Creation `01:05:36` → LastWrite `02:27:22`（81.7 min，含复跑之外的收尾）。**旧稿的「01:07–02:03」终点无据**，
只有「01:07–01:09」对 bench 那一格有硬证据）

**两把尺**：清单 **94 条脚本全 `exit=0`**；整轮 **121 个 gate 行**（94 条脚本 + 27 个 `ServiceProbe` mode）里唯一非绿
= `probe:bench (exit=1)`（`PROBE RESULT: pass=35 fail=2`，100.1 s）⇒ `runner-exit=1`。⚠ `bench` 是 **mode 不是清单条**，
不按「94 条里 93 绿」记账。

含本批新改的三条：第 85 条 `gui-starts: PASS=7 FAIL=0`（`log:114` 只回显 ⑦ 两臂与负控；⑥ 的断言在
`tests/output/validate/gui_start/gui/tray.log:1`：`create=ok IsVisible=True closeToTray=True icon=loaded`）、
`_probe-cjk-hardcode-scan` 14/0、`_probe-gui-control-wiring` 103/0。

被测产物 `artifact-sha16`（`log:10`，10 项）：`FfmpegGui.dll=BC3D7D62682A46E5`、`FfmpegGui.exe=69D0EEA8A7B7FCE5`、
`ServiceProbe.exe=93BF03789E69EC6F`……（跑后未改 `src/`，可追溯）。⚠ 该段**不含 `ServiceProbe.dll`**，
且 `ServiceProbe.exe` 是 apphost（旧臂同值 `93BF03789E69EC6F`，对臂身份零信息量）
⇒ **臂身份按 dll 判**（H-21 已立此口径）：本批现测 `ServiceProbe.dll=3C36FDFCE451CF8A`，mtime `01:04:36`
早于起轮 `01:05:36`、复跑到完（02:32）未再变 ⇒ 整轮与两次复跑是同一份 dll（推断链：文件从未被重写）。

**bench 两条红的定性（计时抖动，非回归）**：

| 臂 | `ServiceProbe.dll` | 负载读数 | 8b PQ→sRGB | 8b sRGB→P3 | PQ→sRGB 扩展比 | 端到端 H1/H2 | 汇总 |
|---|---|---|---|---|---|---|---|
| run g（01:07–01:09，`_gate_bench.txt`） | `3C36FDFCE451CF8A` | ≈6% | 37.2 | **23.7 FAIL** | **3.4× FAIL** | 1.98×（差 1% 就红） | 35/2 |
| 复跑 r1（Creation `02:29:43` / LastWrite `02:31:04`，`_tmp/bench_rerun/r1.txt`） | 同件 | ≈7% | 62.1 | 38.3 | 5.8× | 1.76× | 37/0 |
| 复跑 r2（Creation `02:31:42` / LastWrite `02:32:55`，`_tmp/bench_rerun/r2.txt`） | 同件 | ≈15% | 59.6 | 44.3 | 4.9× | 1.72× | 37/0 |
| 旧臂 OLDARM-1（11-01 23:14:09，HEAD `f778280` worktree 重建，`:3`） | `FF05421132B2542B` | ≈60% | 57.9 | 42.8 | 5.0× | 1.76× | 37/0（`:106`） |
| 旧臂 OLDARM-2（11-01 23:15:31，`:107`） | `FF05421132B2542B` | ≈50% | 59.3 | 43.9 | 5.4× | 1.81× | 37/0（`:210`） |
| 同窗对照 CUR-2（11-01 23:18:33，`:316`） | `C0EA213F057F6AA2` | ≈8% | 44.5 | 40.6 | 5.9× | **2.69× FAIL**（`:416`/`:418`） | 36/1（`:419`） |

- **旧稿把这四格张冠李戴了**：`36/1`、`44.5/40.6`、`7.4×`、`2.69×` **全部出自 CUR-2，不是旧臂**；旧臂两次都是 **37/0**、
  端到端 `1.76 / 1.81`（对阈值 2.0 余量 ~12%）。旧二进制臂并不比本批差。
- **「同一二进制」的旧证据不成立**：旧稿拿 `ServiceProbe.exe=93BF03789E69EC6F` 当臂身份，可那个值在旧臂日志头也一样出现，
  且 `r1.txt` / `r2.txt` 内**没有任何指纹行**。可复核的只有盘上 dll：`3C36FDFCE451CF8A`（mtime `01:04:36`，早于 r1 创建 `02:29:43`）。
  ⇒ 「同一份 dll」是**推断**（文件未重写），不是日志直读——本文按推断写。
- 复跑 r2 的线程钉法与 r1 不完全同构（`bench-affinity=0xFFE` 11/20 线程、未用逻辑核含 `[0]`；r1 = `0xFFF` 12/20），
  「同条件」只能算近似，不影响「同 dll 差异 1.6~1.9×」这一结论。
- 与托盘改动无关（托盘不在色彩内核路径上；本批色彩改动只有 `ResolveSrcCodeMax` 的一次性探测，不经内层循环）。
- 处置口径照 bench 自己的注释：**静机复跑 + 不许下调阈值**（L-7）。没有改任何阈值、没有放宽判据。
- 如实登记：run g 的 `probe:bench` 记账**仍是红**（`_gate_bench.txt.rc`=1 B、mtime `01:09:27`），本批**没有**重跑整轮去把它刷绿；
  两次复跑是**定向取证**，不是「整轮绿」。

### 9.10 复核修正（两路审阅者，2026-11-02）

代码侧 4 项与文档侧 4+4 项见 `docs/HANDOVER.md` **H-23**。与读数相关的三条要点：

- **旧臂不是「也红」**：`36/1`/`44.5/40.6`/`7.4×`/`2.69×` 全出自同窗对照 CUR-2；旧臂两读 `37/0`（§9.9 表已逐段重写）。
- **臂身份按 dll**：`ServiceProbe.dll=3C36FDFCE451CF8A`（mtime `01:04:36` < 起轮 `01:05:36`，复跑后未变）；
  `ServiceProbe.exe` 是 apphost、旧臂同值，不作判据。
- **复核后重编**：`FfmpegGui.dll` `BC3D7D62`→`6B76A091`，`ServiceProbe.dll` 未变 ⇒ 本文件 §9.9 的 bench 读数仍绑同一份 dll；
- **M1 补了结构锁**：`_probe-gui-control-wiring.ps1` 新增第 ⑧ 组（`FindMenuByTag` 顶层 `Tag` 比较），103/0 → **107/0**；变异（删掉那行）⇒ `106/1`，还原后逐字节相等。仍属源码形状锁，「勾选态真的点亮」没有运行时判据。
  受 M1 影响的 8 条定向门禁复跑全绿（`tests/output/_tmp/postfix_gates.log`），**整轮未重跑**。