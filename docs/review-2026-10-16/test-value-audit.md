# 测试价值普查 2026-10-16 —— 哪些门禁有用、哪些是死重

口径先说死：**"有用"= 它能对某个真实失效形态判红**（不是"它绿着"，也不是"它主题听起来重要"）。
本篇只给取数与分档，**不删任何东西**；每条判死候选都带删除后果与验证步骤，等一句点头。

## 0. 取数口径（四条独立轴，都可复跑）

| 轴 | 取法 | 现值 |
| --- | --- | --- |
| 成本 | `tests/output/tmp/round-full14.log` 的结果行（每条脚本自己最后一个 `PASS=n FAIL=m` + 尾秒数） | 计时合计 **39.3 分**（墙钟 42 分 20 秒），断言合计 **4,986** |
| 判据摸得到什么 | 逐脚本扫非注释行：起进程/调工具 vs 正则/AST 比源码 | **37 产物级 / 22 混合 / 15 纯源码形状 / 2 元锁** |
| 历史红过 | `tests/output/**/*.log` 3,546 份里 `] exit=≠0` 的结果行（只取行首那个 exit 令牌） | **54 条红过 / 22 条一次没红过** |
| 素材依赖 | 逐脚本点名 `publish/PLAN`、`tools/src`、`tests/output/sources`、`tests/data`… | **61/76 条点名外部素材**；33 条依赖 `publish/PLAN`，3 条依赖 `tools/src` |

辅助读数：9 条脚本不自我汇总（`PASS=` 缺失），**全部**落在运行器登记的 `$tableOnly`(4)+`$noSelfSummary`(7) 内 ⇒ 没有"静默零断言"的门禁。

## 1. A 档：死重，可以判死（有实测依据，不是推测）

**A-1 四份镜像工程的 134 条断言，从来没人执行。**

| 工程 | `*Group.cs` 行数 | `Check(` 调用 | 谁执行它的 exe |
| --- | --- | --- | --- |
| `tests/encode` | 627 | 38 | **无** |
| `tests/gainmap` | 466 | 43 | **无** |
| `tests/io` | 365 | 18 | **无** |
| `tests/metadata` | 315 | 35 | **无** |
| 小计（只算组体） | **1,773** | **134** | 0 |
| **实删总数（`git diff --cached --numstat` 逐名核）** | **12 个文件 / 1,889 行**（差额 = 四份 `Program.cs` + 四份 `.csproj`） | | |

证据链三条，各自可验：
① 这四份工程**每批都被重建** —— `tests/output/f14/rebuild-18.ps1` 的 targets 里就有 `tests/{encode,gainmap,io,metadata}`；
② 全仓 76 条在册脚本里，**只有 `verify-probe-mirror-parity.ps1` 的登记表 3 提到它们**，且是把它们当**文本参照**
   （`'ProbeBitDepth' = @('tests/encode/EncodeGroup.cs')` 这类映射），没有任何一处 `Start-Process` 它们的 exe；
③ 该锁的判据本身写死了「**镜像独有断言 = 0**」⇒ 这 134 条按定义**全部是执行那份的复制品**。

⇒ 结论：镜像的作用域已经被 #76 自己否定（"不许有第二份拷贝"是它的立锁动机），拷贝却还留着并每批编译。
**删除不是 rm -rf 一步**，前置是一件账：这四份工程在组表里各占一个组
（`TestGroups.cs:51/54/57/68` ⇒ `encode`=4 mode、`metadata`=4、`gainmap`=5、`io`=2，共 **15 个 mode 名**，
且这 15 名**一个都不在** `["ServiceProbe"]` 组里 ⇒ 必须先把它们并进那个组，
该目录存在、`Program.cs` 里 `case` 分派齐全 ⇒ ⑤ 满足），再删目录。否则 #69 的
① 「`$Probes` 每个 mode 都有组认领」当场红。
⚠ 别把这 15 个 **mode** 当死件跟着杀：它们全都活着，只是**执行体是 ServiceProbe 而不是镜像 exe** ——
其中 7 个在 `$Probes` 默认清单里由运行器直接起（`encoding`/`ipc`/`procstreams`/`metaraw`/`thumbnail`/`oog`/`avifgm`），
另外 8 个由在册门禁按参数位置调用（`bitdepth`←format-regression:78、`png3`←_probe-jpg-p3-icc:28、
`icc`/`icc-dump`/`faithful`←prophoto、`gainmap`←color-wiring:290、`gainmap-isobmff`←isobmff:264、`ultrahdr`←managed:385）。
删除后果（其余）：`$dups` 登记表 3 的 15 名塌到 1 名（只剩 `ProbeCodedSizeForTestAsync` 那个同名 helper），
`$zeroMirrorCells`（`ProbeIccDump` 塌 0 那格）连同它的逐名钉一起消失，parity 的 `vendor-hdr` 等 **mode 条数锁不受影响**
（它钉的是 ServiceProbe 侧源文本调用位，不是镜像）。重建目标 18 → 14。
验证步骤（删后必须全跑）：`_probe-test-group-coverage` → `verify-probe-mirror-parity` → `verify-ps-compat` → 一次整轮。

✅ **A-1 已执行（2026-10-16，用户授权"执行 A-1/A-2 删除并跑整轮验证"）** —— 落账如下：

| 动作 | 读数 |
| --- | --- |
| 删四份镜像工程 | `git rm -r tests/{encode,io,metadata,gainmap}`（12 个跟踪文件全清，目录已不在盘）；删前把**唯一一处工作树改动**（`tests/gainmap/GainMapGroup.cs` +3 行注释）存成 `tests/output/lanes-2026-10-16-value/A1-deleted-mirror-local-mod.patch` |
| 组表并账 | `TestGroups.cs` 的 `["ServiceProbe"]` 组 +15 个 mode 名，删 4 个组键 ⇒ **组表并集 67 → 67（守恒）**，`_probe-test-group-coverage` **20/0** |
| #76 换形 | `$deadDirs` 5→9、④ 登记表 16→1 名、⑤ 从方向锁换成「只此一份锁」、撤下 ⑤-a/b/c 与 ④-z、删掉随死的 `$zeroMirrorCells` 与 `Cmp-Multiset`；① 扫描域阈值 28→20（现 23） ⇒ **82/0**（换形前 109/0） |
| 新 ⑤ 的牙 | 往 `tests/gainmap-decode/GainMapDecodeGroup.cs` 追加同名 `ProbeOog` ⇒ `⑤ 长出第二份定义 = 1（实 ProbeOog@…）` + `⑤-z 实 1` ⇒ **80/2 转红**；逐字节还原 + mtime 复位后 `82/0`（`RESTORED identical=True mtime_back=True`） |
| 重建 | `tests/output/f14/rebuild-14.ps1`（targets 18→**14**）；`targets=14 build-failures=0`，域内 14 枚 `FfmpegGui.dll` 同一前 16 `47BBC139FA38F619`，域外 4 枚 Debug 件逐名点名 |
| 行为证据 | 被删工程那几个 mode 在**唯一执行面**上照跑：`ServiceProbe thumbnail` **40/0**、`ServiceProbe bitdepth` **11/0** ⇒ 删的确实只是拷贝 |
| 注释/文档同步 | `ServiceProbe/Program.cs`、`verify-gainmap-isobmff.ps1`、`verify-gainmap-host.ps1`、`Tests.GainMapEncode.csproj` 里指向镜像工程与旧 ⑤-c 的说法就地改注；`docs/TESTING.md` §1.1 的"现存 4 份"与日常用法命令示例（`tests/encode` ⇒ `tests/gainmap-decode`，`--list` 已实测支持）同步 |
| 我自己造的一次破坏 | Edit 又把 `TestGroups.cs` 整文件转成 **CRLF**（HEAD 是纯 LF）⇒ `tr -cd '\r'` 对撞后归一；本轮改过的其余文件 EOL/BOM 全部核过未变 |

⚠ **A-2 不执行**，理由与它错在哪条 grep 上，见下面那一节的撤回记录（同一句"不可达"里有两个方向不同的错）。

**A-2 【撤回 —— 我原来的说法是错的，动手前被自己的取证否证】**

原判死语："四个 mode（`color`/`ipc-server`/`piperaw-server`/`tonemapcurve`）不可达 ⇒ 死分支"。
实测否证两处：

① **`tonemapcurve` 有活调用点**：`tests/scripts/verify-color-tonemap.ps1:36`
   `Exec $pr "tonemapcurve "$d/ours_$name.csv" …"` ⇒ 它就在在册门禁的执行路径里。
   我当初那句"无在册调用点"来自一条**形不对的 grep**（只搜了独立引号形 `"tonemapcurve"`，
   没搜"引号开头 + 空格 + 参数"这种同一条串里的用法）⇒ **grep 命中 0 ≠ 没有调用点**，与本仓
   已立的"0 命中先问我的模式与目标写法同不同形"是同一条规矩，我又踩了一次。
② **另外三个是明文保留的手工诊断工具，不是漏网死码**：
   `docs/TESTING.md:1115` 写「诊断工具 `ipc-server [秒]` / `ipc-client` / `ipc-raw-client` /
   `piperaw-server` / `piperaw-client`（后两个是**环境对照**用的自包含裸管道）均已保留」；
   `.workbuddy-ai/memory/2026-09-15.md:151` 更早写过理由「**不要**把往返用例加进门禁（会挂死整条门禁）」；
   `color` 的 `ProbeColor` 第一行是 `usage: ServiceProbe color <file> [fmt] [exiftool]`，
   且 #76 头注把这三个连同 `icc-dump` 一起登记为「取数器 / 驻留端」（判据在调用它们的 `.ps1` 里）。
   ⇒ 它们**不进整轮是设计**，删掉等于拆掉一条排障路；本仓规矩「降级不是修复」也反对这么做。

⇒ **本条不执行**。同时留下一条**真缺陷**（下一件事该修它）：
   #69 的可见性只覆盖 `TestGroups` **声明过**的 mode ⇒ 一个 `case` 分支若既不进 `$Probes`、也不写进组表，
   就永远不被 ⑥ 的"零调用点"账目看见（`tonemapcurve`/`color`/`ipc-server`/`piperaw-server`/`icc-client`/
   `ipc-raw-client`/`piperaw-client`/`memadmit`/`ipc-submit` 等都在这个盲区里）。
   修法不是把它们塞进组表（那会立刻造出一批真零调用点的红），而是给 #69 加一条**账目锁**：
   `ServiceProbe` 的派发 mode 全集（`$disp`）与「`$Probes` ∪ 组表声明 ∪ 在册脚本参数位置命中」三者之差
   必须**逐名等于一张显式登记表**（登记表里写明每一项为什么在门外）⇒ 新增一个"门外的 mode"当场要点名，
   而不是靠人想起来去 grep。

**A-3 一条扫描器不是判据，别按判据供着。**
`_probe-stderr-drain-scan.ps1`（49 行，0.0 s）自己 FAIL 文案写的是「**需逐个复核：可能是真缺陷，也可能是排空写在别处/由助手完成**」
⇒ 它是启发式**人工复核清单**，不是能判产品死刑的断言。处置建议：**降级登记**（在清单注释里写明"复核清单，红不拦交付"），
不建议删（成本 0）。

## 2. B 档：不是无用，是噪声源 —— 它们的红从来没有一次指向产品缺陷

| 门禁/探针 | 历史红次 | 红的时候说的是什么 |
| --- | --- | --- |
| `probe:runner` | **26 / 26 份日志** | 载体 `cmd /c ping` 自己退 1；今天复测同一参数在 pwsh 父进程里退 **0** ⇒ 旧"loopback 被挡"根因**已否证**，属未归因环境红 |
| `probe:bench` | 5 | 计时臂在轮内被相邻脚本负载拖慢（1.4× > 1.3 阈值），**独跑三次全 37/0** |
| `verify-ffmpeg-heartbeat` | 4 | 其中一次是 ⑤-N2a「两臂注入阈值 == 2 s」的夹具阈值，不是像素 |

⇒ 处方不是删：`bench`/`runner` 这类墙钟/进程敏感臂应当**独跑复验后才允许定性**，并把它们从"整轮必须全绿"
的宣布口径里显式豁免（豁免要写名，不许靠放宽阈值）。今天 full14 的宣布口径已经是"唯一红 = `probe:runner`（既有环境红）"。

## 3. C 档：便宜但薄，砍了省不到钱

15 条纯源码形状锁合计 **26.7 s = 全轮 1.1%**。逐条秒数最大的是 `verify-probe-mirror-parity` 10.6 s，
其余 5 条 `_probe-*-scan` 是 **0.0 s**。⇒ **以省时为目的砍它们 = 零收益**。

它们的真实代价是**维护税**：形状锁会因改名/挪行而红。全仓行号 pin 只有 **26 处且集中在 `_lib-matrix.ps1`**（一份文件），
其余靠符号存在性/计数 ⇒ 想降税就做两件事，都不必删门禁：
① `_probe-geometry-single-source-scan.ps1` 的 ② 目前是「ImageGeometry.cs 同时含 某三个符号」= 存在性断言，
   改名即红却测不到行为（本仓已立的规矩：**"保护真起作用"= 析取式 + 源形锁**）；
② 行号 pin 一律改走 `_lib-matrix` 的 `路径:行|字面片段` 形态（它已经这样做了），别在别处新增裸行号。

## 4. D 档：证明过有用，别动（红过 54 条里最典型的几个）

| 门禁 | 历史红次 | 抓到的是不是真东西 |
| --- | --- | --- |
| `_probe-test-group-coverage` | **17** | 是。最后一次：零调用点 7 个逐名点名（`bench`/`bitdepth`/`gamut`/`gainmap-{encode,decode,struct}`/`pq`/`ultrahdr`），本批 20/0 收口 |
| `verify-ui-split-union` | 8 | **混合**：一次是真丢断言（`new=398 old=413` ⇒ 拆分侧静默少跑 15 条），其余是 `$expectedOldCount` 漂移（419→421） |
| `verify-gainmap-host` | 6 | 是。把三段拆分件接进执行面当场量到 `58/1` 的过期参照（`D30 maxDiff=6, >1LSB=194/1024`） |
| `verify-decision-delivery` | 红过 | 本批刚做变异证明：89/3 ⇄ 92/0 |
| `verify-encoder-knob-liveness` | 文档 62 行提及 | 每轮给出 活/死/被拒/未判/缺口 五桶明细（本轮 活=19 缺口=1） |

## 5. 未评声明

- 一条**逐门禁牙账**（76 条 × "有无变异记录 + 记录里给没给读数"）由并行只读 lane 在算，本篇只覆盖了
  22 条从未红过的（词法代理 + 我实读了 4 条）；lane 落盘后可能把 D/C 档里的个别的改判。
- **非沙箱对照臂未跑**：A 档的判死结论不依赖它；但 B 档 `probe:runner` 的"环境"定性，严格说只在
  「历史沙箱轮全红、09-2x 非沙箱轮全绿」这一旁证上，今天**没有**再取一次非沙箱读数。
- 从未红过的 22 条里，"出生即红"（修好后锁住缺席）只在这几条**文件内**留了记账字样：
  `verify-simd-switch-fallback`(5)、`verify-engine-alpha-preserve`(3)、`verify-engine-firstframe`(1)、
  `verify-avif-anim-probe`(1)，其余 18 条文件内无记账 ⇒ 不能据此判它们无牙，只能说**证据不在文件里**。
