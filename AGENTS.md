# ffmpegPictureUI 项目规范（本仓）

跨项目的协作规矩在全局 `~/.qoder-cn/AGENTS.md`；这里只写**本仓特有**的构成、归置契约与验收口径。
定位：Avalonia 桌面图片/动图转换器（`FfmpegGui`，WinExe + headless CLI），封装 ffmpeg 与外部编码器，
内置纯托管色彩管线。三层构成：**UI 层**（视图 + `MainWindow.xaml.cs`）· **管线层**（`Services/`）·
**色彩引擎层**（`Services/ColorMapping/`，对 UI 零依赖，`using Avalonia` 实测 0 处）。

---

## 1. 目录契约（唯一归置规则）

⚠ **工作区边界（绝对，任何理由不构成绕过；细则见全局 §W）**：一切写盘（建文件/建目录、下载、解压、生成临时件、缓存）
目标必须在**本工作区** `C:\PLAN\ffmpegPictureUI\` 内。**禁止**在工作区外创建任何文件或目录——包括 `%TEMP%`、`C:\Temp`、
`C:\PLAN\` 下的同级目录（如语料库、对照臂 worktree、临时取件区）、用户主目录新建目录。
下文凡出现「仓库外 / 仓外」作**要求**的条款（§3 冻结副本、§4 对照臂 worktree、§5 历史提交构建）一律**降为待批**：
先单独申请、写清路径与清理方式、得到明确点头才可动手；**不得推定授权，不得沿用上一批的点头**。
外部素材（第三方样本、语料）默认落 `tests/output/_tmp/<批次>/corpus/`，与判据分离：判据进 `tests/scripts`（平铺登记），产物进 `tests/output`。
已违规 ⇒ 立刻停、清理自造仓外产物、在回复里如实点名路径与文件（`Get-Item` 的 FullName / 清理后 `Test-Path` = False）。

| 路径 | 放什么 | 不许放什么 |
|---|---|---|
| 仓库根 | 法律/说明/规范类 `.md`（`LICENSE` `NOTICE` `THIRD-PARTY-NOTICES.md` `README*.md` `CHANGELOG*.md` `PACKAGING_SPEC.md` `AGENTS.md`）、`ffmpegPictureUI.sln`、`pack.ps1`，以及 `src/ tests/ tools/ docs/ licenses/ publish/` | 任何脚本落盘产物、一次性调试残留 |
| `src/FfmpegGui/` | 产品源码：`Controls/` `Models/` `Resources/` `Services/` `Services/ColorMapping/` | 测试宿主、探针 |
| `tests/projects/<工程名>/` | **13 个测试工程**（11 个 Exe 宿主 + `Tests.Shared`/`UiHostShared` 两个 classlib）。测试 `.cs` 只住这里 | 手改生成物（见 §3）；散在 `tests/` 顶层 |
| `tests/scripts/` | 门禁与探针脚本，**必须平铺**：`verify-*` = 门禁，`_lib-*` = 被 dot-source 的公共库，`_probe-*`/`_check-*`/`_make-*`/`_mutate-*`/`_readback-*` = 探针与取证工具 | 子目录——归属探针用 `Get-ChildItem tests/scripts -Filter *.ps1`（非递归）统计清单外脚本，分子目录会当场把覆盖判定变成盲区 |
| `tests/data/` `tests/fixtures/` | 手工造的小体量夹具（含其生成 `.py`），由 `$PSScriptRoot`/`RepoRoot` 推路径 | 绝对路径 `C:\PLAN\...`（唯一已知例外：`_probe-gpu-stage3.ps1` 里那串 `k.cl`） |
| `tests/output/` | **一切产物**：转换结果、日志、临时工作目录、scratch。整树 gitignore。**每个父目录下一轮一份 GUID/版本目录**（隔离是必要的，防上一轮残留让本轮空跑假绿）。两条清理命令：`py tests/scripts/_sweep-output.py`（清顶层陈旧无引用条目+空目录，默认 dry-run）与 `py tests/scripts/_sweep-output.py --runs --max-age-days 7`（按年龄清陈旧轮次目录；红轮证据在保留期内不动） | 判据文件、被跟踪的脚本、第二份测试源码 |
| `docs/` | 设计与账本，见 §6 | 与代码冲突时以代码为准 |
| `tools/src/` | vendored 上游源码与自建小工具（`pngcicp`/`dngjxlprobe`/`dngtool` 进 sln） | 只有 10 个文件被跟踪，其余是本地构建依赖 |
| `tools/std/` | ITU 标准原文（docx/txt），色彩常数取数的一手依据 | 派生结论 |
| `publish/` | 打包输出与随包工具链，整树 gitignore | 手工编辑 |

新文件的归置问题一律先回答"它是**判据**还是**产物**"：判据进 `tests/scripts`（平铺）并登记，产物进 `tests/output`。

⚠ **出包只留最新一档：旧版本的包直接删掉。** 维护者 2026-10-28 的原话：「应该删掉旧版本的包，只留新包」
（此前我给的重出处置是"旧归档挪进 `publish/_void-<日期>/` 不删"，被当场否掉）。做法：出新包即删
`publish/FFmpegPictureUI-v<旧版>-x64*.7z` **连同** `publish/build/FFmpegPictureUI-v<旧版>-x64*` 解包目录；
`publish/PLAN`（随包工具链）与 `publish/rescued-*`（存证 bundle/diff）不在删除范围。
理由不是省盘：旧包在场，"这条 L3/验包/像素读数打在哪个包上"就可能答错——2026-10-28 实况是 `publish/` 里同时堆着
beta7–beta11 的 10 个归档 + 8 个解包目录（1.7 GB）。**要留证据就记日志与哈希（`ProductVersion`、包内 exe
`sha256[:16]`），不留包。**
删前先扫 reparse point：`os.walk(followlinks=False)` + `st_file_attributes & FILE_ATTRIBUTE_REPARSE_POINT`
（§5 那族教训——`publish/build/*-full/` 里若出现指向 `publish/PLAN` 的 junction，递归删会连随包工具链一起没；
2026-10-28 实测那批 2936 项里 0 个 reparse point，`pack.ps1` 是实拷贝，但仍要每次先扫）。

⚠ **零引用 ≠ 无用**：`_sweep-output.py --unreferenced` 删的是"名字在在册文本里一次都没出现过"的条目，
而**手工跑出来的活线产物正属此类**——2026-10-22 就这样误删了 `jxr-probe`(627 MB) 与
`jxr_engine_probe`(556 MB)，两者没有任何在册脚本能重生成，也不在 git/回收站/卷影里，不可复原。
用这一档之前先读一遍待删清单；`--runs`（按年龄）那一档不受此限，因为轮次目录本就由在册脚本重建。

## 2. 门禁登记：清单本体就是运行器

`tests/scripts/_run-step3-gates.ps1` 是清单，不是索引外部文件。加/删/改名一条门禁必须**同批**改齐四处：

1. `$scriptList` 数组（104 条）；
2. `$expectedScriptCount` 常量（同文件）；
3. runner 里复述条数的**散文陈述**——`Test-RunnerShape` 第 ⑰ 针按三种形态各扫一遍并要求与常量相等；
4. 新的 ServiceProbe mode 还要进 `tests/projects/Tests.Shared/TestGroups.cs` 的组表，
   由 `_probe-test-group-coverage.ps1` 认领（档① 运行器起 / 档② 在册脚本按参数调用 / 档③④ 算未覆盖并转红）。

裸跑单个 `verify-*.ps1` 会绕开仓库锁、单步超时、STALE 新鲜度闸与产物指纹核对——只适合调试，不算验收。

## 3. 测试层三条硬规矩

- **生成物不手改**：`tests/projects/ui-*/<name>Group.cs` 由 `_gen-ui-split.ps1` 从 `UiTestHost/Program.cs`
  按行区间切片生成（断言字符串必须逐字保留）。改断言改单体，然后重跑生成器；
  `verify-ui-split-reproducible`（重跑两次 + 逐字节对撞）与 `verify-ui-split-union`（并集双向差集）会红。
- **镜像不许再生**：`verify-probe-mirror-parity` 是存在锁（`tests/**/Modes/*.cs`、被删目录、git 索引三处查），
  并把 `ServiceProbe` 每个派发 mode 的断言条数**逐名钉死**、要求"执行面每个 `Probe*` 只许一份定义"。
  想加第二份拷贝 = 当场红；合并掉一处也要回来改登记表。
- **承重探针必须入库**：被在册脚本调用的 `_*.py`/`_*.ps1` 必须是 tracked 文件。
  clone 后跑不全门禁，就是这条没守住（2026-10-22 补登记 17 支）。

- **产物只有一份，且不落 `%TEMP%`**：测试宿主的产物路径一律取 `OutputDir`（`tests/output/<组名>`）；
  **临时件根由宿主自己钉死在 `tests/output/_tmp`**（`Harness.PinScratchRoot()` 设 `TMP/TEMP/TMPDIR`，
  四个引导点全覆盖：`Harness.InitExternalTools` / `ServiceProbe.InitExternalTools` / `UiHost.Bootstrap` /
  `GainMapTestHost.Main`），子进程（ffmpeg/dngtool/exiftool）继承之 ⇒ 谁都不许再往系统临时目录写，
  也不许再现场发明 TMP 根（历史反例：`gate_tmp` `gtmp` `roundtmp` `_ramdisk` `faketemp` 等 12 个根，
  把同一份 187.6 MB 的废弃 `cap.dng` 乘成 12 份）。`_tmp` 在产物区内 ⇒ 受 `_sweep-output.py --runs` 管辖。
  `Path.GetTempPath()` 只许放"用完即删"的中间件，**写完必须 `finally { File.Delete(...) }`**。
- **一轮一目录是设计，不是缺陷**：GUID/版本 run-dir 防"上一轮残留让本轮空跑假绿"；沉积用年龄清扫
  （`--runs --max-age-days`），不许用"绿才删"当唯一出口（红轮永不删）。
- **冻结副本不进产物区**：对着历史提交构建用的 git worktree 要放仓库外（如 `..\ffmpegPictureUI-wt\`）；
  `tests/output/**/` 里出现第二份 `tests/` 源码树即违规（2026-10-22 已清空，现存 0 个）。

## 4. 验收配方

```powershell
# 必须 pwsh7；LC_ALL 会改变排序/解析行为 ⇒ 清掉
$env:LC_ALL=$null
dotnet build ffmpegPictureUI.sln -c Release          # 13 个测试工程都在 sln 里，编不过当场暴露
pwsh -NoProfile -File tests/scripts/_run-step3-gates.ps1   # 104 条；整轮墙钟未在 104/104 下重测（94 条时代 ~60 分钟）
```

- 宣布"全绿"必须绑定被测产物：exe/DLL 指纹前 16 与当轮日志同段；`dotnet test` 打在 Exe 工程上只还原不跑还返回 0。
- 起轮之后不许再动 `src/` 与运行器——整轮读数作废。计时类红要用旧二进制做对照臂，别拿单次 LoadPercentage 当"机器空闲"。
  - 对照臂的 worktree 必须建在**仓库外**；比对时看 `ServiceProbe.dll` / `FfmpegGui.dll` 的哈希——
    `*.exe` 是 apphost（IL 在 dll 里），两棵树编出来的 `.exe` 哈希**相同**，拿它判定会误以为"跑的还是同一份"。
    臂日志**必须记 dll 哈希**（`bench_oldarm_20261101.log:1` 那种段头行），否则事后不可复核——
    本仓 2026-11-01 就吃过一次：旧臂那次读数只留了 apphost 值，结论差点钉死在错的理由上。
- 本机沙箱**按进程**拦子进程向仓库外写：ffmpeg 写 `%TEMP%` / `C:\Temp` 一律 `exit -13`（`.NET`/python 写 `%TEMP%` 成功）。
  已固化为运行器自钉：起轮时把 `TMP/TEMP/TMPDIR` 钉到 `tests/output/_tmp/round-tmp`，并打印
  `env-temp-pinned=` / `env-temp-writable=` 两行读数（子进程继承）。**裸跑单条门禁调试时要自己钉同一个根**，
  否则 `verify-color-wiring` 这类会批量假红（2026-11-01 实例：不钉 60/56，钉后 153/0）。
- 改动只跑所在链路；一块一个测试，分开跑分开报。

## 5. 高危区（动之前先读）

- `tests/projects/GainMapTestHost/PLAN` 及其 `bin/**` 副本是 **junction**，指向 `publish/PLAN`。
  `os.walk` 会钻进去；拆链接用 `os.rmdir`，别 `rm -rf`（会连目标一起删）。
- 产物区**不许**挂 git worktree（2026-10-22 已清：`_wt_gm3`/`_wt_verify`/`review/wt_head4` 全拆，分支
  `gainmap-metadata-spec-fix` 的 4 个独有提交存在 `publish/rescued-20261022/*.bundle`，未提交改动存在同目录
  `wt_verify-uncommitted.diff`）。要对着历史提交构建，就建在仓库外（如 `..\ffmpegPictureUI-wt\`）。
  清 `tests/output` 前仍要先 `git worktree list` 确认——历史教训是 `git worktree remove` 不钻 junction。
- MSYS bash 会静默改坏生成的 Windows 脚本（`/p:`→`p:`、`nul`→`/dev/null`、`\1` 被转义）⇒ 批量改写用 `py`，MSBuild 传参配 `MSYS2_ARG_CONV_EXCL='*'`。
- 编辑工具会把整个文件换成 CRLF 而 `--numstat` 不报 ⇒ 判据是与 HEAD blob 做字节层 EOL 对撞。本仓 `.ps1` 多为 CRLF、部分 `.cs` 工作树是 LF（git 会提示将转 CRLF），改前先量。
- `git` 的 `quotepath` 会把含 CJK 的路径写成八进制转义 ⇒ 用文件名匹配的门禁要先确认命中，别用裸串。

## 6. 文档口径

- **目录地图在 `docs/INDEX.md`**：承重（位置固定）/ 分批审查（`review-*`，被门禁按名点名）/ 归档（`archive/`）。
  判断一份文档能不能挪：数**仓外**（`src` `tests/scripts` `pack.ps1` README CHANGELOG）对它的命中数，
  零命中才可进 `archive/`；非零就要同批改引用点。**数命中要用去掉尾部日期的前缀名**——代码注释里
  常写短名（`RAW2TIF_QA_DEFECT §7.5`），按全名数会漏并把引用变成悬空。打包只随附根目录三份法律文件，不 glob `docs/`。

- **承重文档**（被代码/脚本/打包引用，位置与章节不可随意动）：`docs/TESTING.md`（引用最多）、
  `docs/HANDOVER.md`（被 6 条门禁引用）、`COLOR_MATRIX_FIX_PLAN_2026-09-24`、`JPEGLI_GAINMAP_PANEL_PLAN_2026-10-09`、
  `PIPELINE_TEST_AUDIT_2026-10-01`、`IMPLEMENT_PLAN_2026-10-11`（现行账本）、`FIX_PLAN_2026-10-14`、
  `GAINMAP_ROUTES_AND_PATCH_PLAN`、`TEST_SPLIT_TEMPLATE`。
- **带日期的账本（`docs/review-*/`、`docs/archive/`、旧 `HANDOVER_*`、`CHANGELOG*`）是时间点记录**：
  布局迁移、改名这类变更**不回改**其中的旧路径，以免把历史读数改成假的当前态。要更正就新写一条更正条目。
- 文档里每个数字都是**读数**，必须能指到某条命令的日志；没跑过的批次不写进结论。

## 7. 未了结

- **执行面迁移已完成（2026-11-01）**：5 条 UI 门禁的执行体已从单体 `UiTestHost.exe` 切到拆分件
  ——`verify-ui-host`（5 件逐件跑全组，**合计锁 430** = 75+53+89+141+72）、`verify-ui-param-matrix`
  （`Tests.UiSweep`/`ui-sweep`，J **精确 98**）、`verify-ui-strategy-map`（`Tests.UiColor`/`ui-strategy-map`，
  K **精确 27**）、`verify-ui-param-defects`（两 mode：`ui-param-defects` L=43 + `ui-sweep` J14d=9）、
  `verify-cli-strict`（三 mode / 两 exe：`ui-cli-strict` 60 + `ui-color` 15 + `ui-icc` 12 = 87）。
  条数锁一律由「下界 `-ge 基线`」收紧为**精确等值**（下界对「等于当前实测」的集合没有牙）。
  - **旧账里「7 条」是误记**：`verify-png-structure` 源码里对单体只有一句历史注释、**零处调用**，
    从来不在执行面上；`verify-ui-split-union` 也**有意不切**——单体是它的参照臂。
    ⇒ 真实是「5 条已切 + 1 条参照臂 + 1 条误记」。
  - 跨件/多 mode 门禁的判据形态：把各臂 stdout 拼成 `$allText` 再统一数前缀，`-SelfCheck` **逐臂**验
    （只验一臂则其余臂的解析器坏了不报警）。
  - **文件锁假红（跨件门禁的通病，已修）**：`Start-Process -RedirectStandardOutput` 的句柄会被**遗留孙进程**
    （实测 `ffmpeg`）继承，宿主 exit 后 ~1 s 才释放 ⇒ `ReadAllText` 抛 `IOException`，表现为
    `summary=False pass=-1 fail=-1`，且 `$p.Dispose()` **无效**。修法 = `FileShare.ReadWrite` 容错读 +
    重试（`Read-TextShared`），不删子进程、不回退 `%TEMP%`。
  - 门禁临时目录同期从 `GetTempPath()` 迁进 `tests/output/<组名>/<guid>`（AGENTS §3：产物只有一份、不落 `%TEMP%`）。
- `gainmap-*` 拆分件只被 `verify-gainmap-host` 按表调用（执行面**未**迁移，与上条同族但独立一批）。
- 本机专用脚本 `_guishot.ps1` 留在仓库根（已 gitignore），**不要**挪进 `tests/scripts`：
  归属探针扫的是该目录下所有 `.ps1`（非递归、不看 git），进去会被算成"清单外未登记脚本"。
- `docs/TESTING.md` 正文里 38 处旧路径（`tests/ServiceProbe` 等）**按原样保留**，顶部加了读法说明；
  逐条改写会连带改动同段的历史读数，属独立一批。
- **清单外脚本**：⑪-d 的 8 支欠账已于 2026-11-01 清零——6 支真门禁接进清单（第二十三次接线：
  `run-color-tests`/`run-quality-tests`/`run-thread-tests`/`verify-color-matrix-full`/`verify-oracle-matrix`/`verify-release-package`）；
  `_accept-ui-split`/`_gen-ui-split` 两支（验收脚手架/生成器，接清单是语义错误）落 ⑪-d **有据豁免表**
  （豁免带牙：CoveredBy 必须全部是受管门禁，否则当场红）。WARN 现值 0。
  ⚠ 但「受管门禁」只校名字，**不校那条门禁真会跑它**——任何豁免都能指向任意受管门禁而过关；
  要收紧须复用 AST 调用点索引建「CoveredBy ⇒ 调用点」双向检查（独立一批）。
- **整轮 ≠ 全判据面（N/N 要按这三点读）**：①~~5 条 UI 门禁的负控块包在 `if ($SelfCheck)` 里、
  `$ncOk` 无条件默认 true，runner 不给它们传 `-SelfCheck`~~ ⇒ **2026-11-02 已接线**：`$scriptArgs`
  给 `verify-ui-host` / `verify-ui-param-matrix` / `verify-ui-strategy-map` / `verify-ui-param-defects` /
  `verify-cli-strict` 各传 `-SelfCheck`（**零门禁改码**），整轮因此多跑负控，代价实测
  139.4 → **285.4 s = +146.0 s**（`t3-subset.log`）；五条负控臂全部按契约转红（ui-color 90/1、
  ui-sweep 142/1、ui-cli 73/1、J 99/1、K 28/1、param-defects 44/1、cli-strict 61/1 + color 16/1 +
  icc 13/1）。防复发靠**第 ⑱ 形态针**：`$scriptArgs` 条目数 ==6、表内 `-SelfCheck` 字面量 ==5、
  5 条 UI 键逐一在位 —— 删掉任一行，整轮当场红（此前无针，删一行照样全绿 ⇒「只有正向臂」会静默复发）。
  ⚠ `$ncOk` 在这些门禁里是**初值 true + `if ($SelfCheck)` 内重算**，所以「传 `-SelfCheck` 后门禁
  自己不会因负控转红而误红」（`$ok` 不含 `-not $SelfCheck` 项，与 `verify-png-structure`
  「`-SelfCheck` 期望本门禁转红」的语义**不同**，别照抄那一条的读法）；②第 92 条 `verify-oracle-matrix`
  整轮只跑 `-Smoke` **3 条代表用例**（自述 L1=194/L2=172）；③第 91 条 `verify-color-matrix-full`
  整轮默认 `quick` = **72 格**（2 格式 × 2 位深），名带 `-full` 不等于全网格。
  ⇒ 宣布「94/94 全绿」时不许顺带说「色彩/oracle 覆盖面一次到位」（②③两点**仍成立**）。
- **分层**：`Services` 对 UI 零依赖，唯一的代码级倒灌是 `IpcService.cs` 认 `MainWindow` 类型
  （2026-11-02 重测：大小写不敏感命中 44 行 / 出现 54 次，其中非注释 39 行；旧记「13 处」有误）；
  抽 `FfmpegGui.Core` 会改出货件程序集构成与指纹，属独立决策。
- **产物区残留（2026-10-22 清）**：`tests/output` 19.73 GB/1357 项 → **16 GB/215 项**。
  拆掉 3 个 worktree（存证见 §5）、零引用条目 1135 项 2.0 GB、>7 天轮次目录 4156 项 8.17 GB、
  宿主泄漏件 47 项 2.03 GB。留下的都在册文本里点名过。
  已知小怪：`tests/output/review/`（456 KB 日志与 diff）因为名字是常用词而被引用判定永久保护，没删。
- **宿主 TEMP 站点已上文本级锁（2026-11-01）**：第 94 条 `_probe-host-temp-sites.ps1` 冻结站点清单
  （**39 站点 / 7 文件**）＋坏根名黑名单（`gate_tmp|gtmp|roundtmp|_ramdisk|faketemp`）＋扫描面自证与
  对账自检带牙。此前修的「写了又从不删」4 处（`cap.dng`×2、`gm_manual_*.rgba`×2）已覆盖。
  `ServiceProbe` 的 `tooldetect_<guid>` / `cmsbench` / `cmsx` 三个固定名 scratch **经查无需改码**，
  以「生命周期注记」进该探针的登记表——**注意**：这条锁冻结的是「站点数与根名」，不回答
  「写完删没删」；要判后者得另立一条判据（仍是独立一批）。
- **`bench` 的计时断言没有停顿/成对采样保护（未了结）**：共**四条**——`多核扩展比 ≥4×`（`Program.cs:8544`）、
  8-bit 档绝对地板 `≥25MPix/s`（`:8603`）、快路径地板 `≥40MPix/s`（`:8488` 的
  `floorMpix = fastCell ? 40.0 : 3.0`，判在 `:8490`）、端到端 `H1 ≤ H2×2.0`（`:8665`）——
  都只做 best-of-3，**不**适用「关表档增益比」那套交错成对采样 + 极差/最优停顿赦红 ⇒ 负载高时假红。
  2026-11-01 实测分布：run d 整轮 `3.5×` / `22.0`；run c 整轮 `sRGB→P3(矩阵同曲线) 单核 ≥40MPix/s（实 38.5）`
  （`run_full_94c.log:41`）；同窗对照第 2 次 `端到端 H1 ≤ H2×2.0（实 2.69×）`（`bench_oldarm_20261101.log:418`）。
  而同机静置复跑 **4 次全 37/0**（`bench_control.log` + `bench_static3.log`，各次 8b 读数
  27.1 / 26.7 / 35.6 / 27.8，多核比 7.5 / 7.4 / 5.9 / 6.3，分档内负载 9%~22%），
  **旧二进制（HEAD `f778280`，仓库外 worktree 重建，dll `FF05421132B2542B`）2 次也全 37/0**
  （8b 42.8 / 43.9，多核比 5.0~8.2）⇒ **没有一条判据是「两臂必红」**。
  ⚠ 两侧读数不许混写：旧臂的“多核扩展比”与 8b 地板**都 PASS**；原「旧臂 8b 25.0 FAIL」事后被证为
  该次运行时 `publish/PLAN` 缺失叠加 ≈58% 负载的采样环境，**不是臂身份差异**（`bench_oldarm_20261101.log`）。
  **run d 的单红在整轮内也复现不了**：run e（同产物、同门禁，当天紧接复跑）整轮 94/94，其中
  `[bench] exit=0 pass=37 fail=0 80.5 s`（`run_full_94e.log:43`）⇒ 连静置 4 次（含 ARM-A）+ run a 轮内 1 次 +
  旧臂重建 2 次 + 同窗对照 1 次共 **9 次 37/0**，定性为负载/计时噪声成立（阈值仍不动）。2026-11-03 run j 轮内再 36/1（多核比 4.0×，撞 ≥4× 边界），
  同二进制静置单跑 **37/0**（第 10 次）⇒ 同族，口径仍不动。
  ⚠ 控制臂日志 `bench_control.log:17` 的 `3.9×` 是**本轮二进制**的小帧扩展比「仅记录」行，不是任何一方 FAIL。
  判据与阈值**一律不动**（维护者口径：绝对地板与快路径对账档不因停顿赦红）；要治须先给口径，属独立一批。
- 视图扩展名**已统一为 `.xaml`**（2026-11-01：`MetadataEditor` / `FormatFilterWindow` / `ImageDetailWindow` /
  `PresetManagerWindow` 四对改名，`git ls-files 'src/**/*.axaml'` = 0，`.xaml` = 7）。`_probe-i18n-scan`
  用 `-Include *.xaml, *.axaml` 递归收，**没有按名点名的清单** ⇒ 后续改名无需同步它
  （其 `:55-58` 只是历史教训注释：早先 glob 太窄曾漏掉根目录窗口）。
  机制：Avalonia 的隐式 glob **只**收 `**/*.axaml` 与 `**/*.paml`
  （`avalonia/12.1.4/build/AvaloniaBuildTasks.props:8-9`）⇒ `.xaml` 只当资源、不做 XAML 编译、不生成
  `x:Name` 字段。现已在 `FfmpegGui.csproj` 显式 `<AvaloniaXaml Include="**/*.xaml"
  Exclude="MainWindow.xaml;ProgressWindow.xaml" />`（这两个窗口靠 `MainWindow.xaml.cs` 里**手写**的
  `InitializeComponent()` + `FindControl<T>` 取控件，纳入编译会 `CS0102` 撞名）。新增 `.xaml` 视图
  走 `<AvaloniaResource Include="**/*.xaml" Exclude="@(AvaloniaXaml)" />` 兜底。
- **`colorRange` 回拷已补齐（2026-11-02），但「8-bit 尺取错」不成立**：`FfmpegCommandBuilder.cs:1714`
  （写这条时是 `:1664`，此后 577 区三次重写各把它下移 7 / 16 / 27 行）现无条件回拷 `meta.colorRange`（与
  `pixFmt :1708` 同段），判据 = `verify-color-token-normalize.ps1` F 段 R0–R6 共 10 条（`PASS=43 FAIL=0`；
  变异 MU1 实测 `38/5`，复原后 `FfmpegGui.dll` 指纹 `950F46373774BCE3`）。**实测澄清**（本批重查）：
  `MapPixFmt` 取的是**用户值**、从不读探测结果，且 8-bit 尺由 `ColorMetadata` 的位深推导而来 ⇒ 旧记
  「命中缓存的路径上 8-bit 尺可能取错」是**推断，非实测**；漏拷的真实后果只有三处**意图死支**（缓存
  命中路径上 `colorRange` 停在默认值，导致 `setparams` 的 `range=` 与色彩转换矩阵选择少一条区分支）。
  像素与 tag 自洽，**没有**像素错。
  ⚠ **但回拷的副作用必须配出口侧口径**（2026-11-03 补，`verify-decision-delivery` ⑨ 当场红出来的）：
  回拷后 webp 出口的 auto 档从「不发 `-color_range`」变成 `-color_range pc`（跟随 rgb48 源的 pc），
  而 WebP 容器**写不进范围标记**（ffprobe 读那份带 pc 的产物仍报 `color_range=tv`）⇒ 存储按 pc 摆、
  解码端按 limited 拉 = **像素错位**。同一源三档实测（512×384 左上 1px，读数用**随包 ffmpeg**
  `crop=1:1:0:0` 取，不是 PIL——PIL 读 rgb48 会给出 `116,19,243` 这种偏移值；源 `a2_srgb_p3.png`
  左上 = `116,20,244`）：不发 ⇒ `116,16,237`（对源 PSNR **31.88 dB**）；发 `tv` ⇒ 逐像素等价
  （31.88 dB）；发 `pc` ⇒ `104,9,240`（**28.22 dB**，丢 **3.66 dB**）。原始日志
  `tests/output/_tmp/corpus-1102/measure_webp_range.log`。修法 = `FfmpegCommandBuilder.cs` 的 webp 出口
  恒 `tv`（用户显式 `--color-range pc` 也不发：容器无法告知解码端，出口语义由后置 ICCP 块精确描述）；
  旧代码那条「webp 且 outRange=tv ⇒ 防御性回退 pc」方向相反，**已删**。
  ⚠ **同批 jpg/jpeg 两步定稿（2026-11-03）**：第一步收窄 webp 分支时丢了这层保护 ⇒ `mjpeg` 对 limited
  YUV 直接报 `Non full-range YUV is non-standard` 且 **exit=1、零产物**；第二步的「`-strict unofficial`
  放行」被审阅 M1 推翻一半——`ResolveOutputColorRange` 的 auto 档跟随输入推断（任何 `yuv*` 输入 ⇒ tv）
  且放行不分显式/自动 ⇒ **默认参数下 webp/avif/视频帧 → jpg 全部落进 limited 存储**。JPEG 的载体事实：
  JFIF/EXIF **没有任何范围标记**（marker 实测只有 COM/DQT/DHT/SOF0/SOS，无 APP0/APP2/APP14/EXIF），
  ffprobe 的 `color_range=tv` 只是从 pix_fmt 反推 ⇒ limited 只对 ffmpeg 家族自洽，libjpeg/WIC/浏览器
  按满量程直取 ⇒ 发灰（审阅实测 webp(tv)→jpg 默认参数：PIL 对源 **45.16 → 25.67 dB**）。**最终口径**：
  **auto 档压回 pc**（HEAD 语义，yuvj 满量程 = JPEG 事实标准，产物 `yuvj420p`/`pc`、读回 `116,17,239`
  一类）；**显式 `--color-range tv` 才放行**（`tv` + `-strict unofficial`，产物真写 limited
  `yuv420p`/`color_range=tv`/读回 `120,16,245`）且**必须出声**（`EmitWarn` 点名 libjpeg/WIC 会发灰）；
  webp 出口显式 `--color-range pc` 被压成 tv 同样出声。旧记「jpg 三档同值 `118,17,243`、不需豁免」
  **作废**（不同参数下的读数）。
  判据 = 同脚本 G 段 R7/R7b/R7b'/R8-PSNR（**PSNR 由门禁自己用 ffmpeg `psnr` 滤镜现算并打印**，
  不再引用无当轮出处的 dB 数）+ H 段 R9a–R9f（零产物负控 + 「放行必须出声」播报锁）+ I 段
  R10a–R10d（auto 压回 pc：命令行/载体/像素三层 + 重标尺负控；像素判据 = 同文件「普通解码 vs
  `setparams=range=pc` 重标解码」恒等 inf⇒99，**与 4:2:0 抽样损失解耦**——首版「对源 ≥38 dB」判据
  作废，正确产物也只有 33.52 dB），整脚本 **64/0**；`verify-decision-delivery` ⑨ 另加整图 PSNR 兜底
  （**94/0**）。
  ⚠ **通用口径**：只有**能写范围标记**的容器（AVIF 的 CICP `full_range_flag`、mkv/mp4 的 yuv*）
  才该发 `pc`；**写不进的**（WebP；**JPEG 连标记位都没有** ⇒ auto 一律满量程 yuvj，显式 tv 才
  limited + strict）必须与解码端的固定假设一致（limited 存储 + limited 解读），
  否则「声明」只改存储不改解读，等于人为制造色偏。⚠ **本批读数只覆盖 WebP 与 jpg**；AVIF/mkv/mp4
  是按容器机制**推断**，无本批实测支撑（AVIF 的静态出口另有 `ExitWritesIcc=false` 一条独立口径）。
  ⚠ jpeg 别名（`.jpeg`）走同一代码路径（`fmt is "jpg" or "jpeg"`），但**本批只单测了 jpg**。
  ⚠ 同族边界（2026-11-03 登记）：**引擎 route**（`ImageEncoderArgs.MapEnginePixFmt:975-988` 不发
  `-strict`/输出侧 range，与 legacy 口径不一致；`RawColorPipeline.cs:1018/1040/1119` 对显式 tv/pc 只把
  `o.ColorRange` 透传进 pix_fmt，无放行/播报契约——批前既有）、**动图回退**（`QueueProcessor.cs:6427`）
  与**预设**入口可达同一分支，但本批无这三条的端到端像素读数——CLI 默认路径（legacy，普通任务经
  `QueueProcessor.cs:1622-1648` 直通回 legacy）已被 R10 判据直接覆盖，引擎侧仅经
  `ColorEngineRouter.cs:40` 的默认路由间接覆盖；要收紧须各立像素臂（独立一批）。
- **ACR 路线只覆盖 DNG（2026-10-24）**：`.dng` 走 `dngtool -rdp`（Adobe DNG SDK 的 `dng_render`，
  OpcodeList1/2/3 全施加），其余 RAW 仍走 LibRaw（**SDK 只读 DNG**，读不了 ARW/CR2/NEF…）。
  渲染空间按下游目标选（bt709 出口 ⇒ `srgb`，其余 ⇒ `rec2020`），`ColorSourceDeclPrimaries` 与它同源。
  **LibRaw 臂的中间件自 2026-10-26 起固定恒等 gamm（真线性 16-bit）**：LibRaw 默认 `gamm={0.45,4.5}`，
  `write_ppm_tiff` 会套 dcraw 曲线，与产品声明的 `linear` 冲突（二次 gamma，实测 ARW→PNG 中位 0.41 vs
  参考 0.32）；修法在 `dngtool.cpp` 解码臂，判据 = 第 79 条 ⑫（同一 DNG 的 `-d`/`-rd` 中位比 0.35~1.5）。
  已知边界：enhanced-image IFD 的 DNG 直接点名拒绝；DNG 输入上 `-H/-q/-W/-threads` 不生效（日志点名）；
  耗时约为 LibRaw 的 2.2×（24MP 5.1 s vs 2.3 s）。要"全量"须先 LibRaw 解包成 CFA DNG 再 SDK 渲染（另立一批）。
  门禁 = 第 79 条 `verify-acr-route.ps1`。
- **第三方 GainMap 语料已进判据（2026-11-02，第 13 条 `verify-gainmap-engine.ps1` §13 / 16 条断言，
  整脚本 `PASS=199 FAIL=0`）**：三件实拍件落**工作区内** `tests/output/corpus/gainmap-iso21496/`
  （`DSC00037.jpg` 921,235 B / `DSC01058.jpg` 842,338 B / `seine_sdr_gainmap_srgb.jpg` 142,972 B，
  sha256[:16] 逐一钉死），整树 `.gitignore:78 output/` ⇒ **永不随仓分发**，故 §13 写成
  fail-closed 前提断言 + 缺失时打印 `Invoke-WebRequest` 取回命令（许可：DSC 两件 GPL-3.0-only、
  seine BSD-2-Clause，再分发许可未逐一确认 ⇒ 不入仓）。覆盖点：ISO 21496-1 APP2 正剂量
  （`maxLinear=4.1263` / `peakNits=880`）、**负 min**（DSC01058 `min=-0.005`，切到 0 后 `切割 (333451 B)`）、
  剂量分叉（10.1899 > 1.5×4.1263 且 cap 更大）；`seine` 是 **hdrgm-XMP 负控**——它不得被认成
  「元数据来源: ISO 21496-1 APP2」。⚠ 本机 `curl.exe --http1.1 -C -` 取该站报 exit 56/23 且落
  0 字节 ⇒ 取回命令一律用 `Invoke-WebRequest`。
- **托盘 / 关窗契约（2026-11-01 定，由第 85 条 `verify-package-gui-starts.ps1` ⑥⑦ 保证）**：
  托盘图标随启动即常驻（跟 `AppSettings.CloseToTray` 走，默认 `true`），不是「关窗时才点亮」；
  **只有「设置要托盘 且 托盘真可用」才拦关窗**，否则关窗即真退出（不许藏成不可见进程）；
  退出唯一实现是 `App.ExitApp()`（托盘菜单 / ⚙「退出程序」/ `Closed` 兜底三处共用，`_exiting` 闸防重入）。
  托盘失败一律落 `tray.log`（`FFMPEGGUI_DIAG_DIR` > 缓存目录 > exe 同目录），不再 `Debug.WriteLine` 无痕。
  ⚠ **不要改 `ShutdownMode` 为 `OnExplicitShutdown`** —— `IpcService.cs` 的 `AllowFullExit=true; Close()`
  会变成僵尸进程。⚠ 「备份→还原→跑门禁」必须 touch 源文件：`copy2`/`cp -p` 带回旧 mtime 会让 MSBuild
  跳过编译、拿变异体跑出**假红**（实测踩过）。⚠ `MenuFlyout` 里的 `x:Name` **不生成字段**，取控件用 `FindMenuByTag`。
- **托盘黑洞修复（2026-11-04）：Windows 侧换原生 `Shell_NotifyIcon`，Avalonia `TrayIcon` 弃用**：
  维护者报障「关窗后无托盘图标、进程常驻」——UIA 枚举任务栏+溢出区（展开弹窗全名单）证实
  **Avalonia 12.1.4 的 TrayIcon 在本机（Win11 26300）从不真正向 shell 注册**（standalone、
  `TrayIcon.Icons` 附加集合两路皆 0 命中，最小探针应用复证），而其 `IsVisible` **自报 True**；
  旧实现拿自报值当 `_trayAvailable`（tray.log `create=ok` 与门禁 ⑥ 都是被这份自证喂绿的，
  `tests/output/_tmp/tray.log` 里 180+ 条历史 `create=ok` 全是假读数）。
  对照实测：裸 `Shell_NotifyIcon(NIM_ADD)` 从**正常 medium-IL token**（explorer 代启）成功（按钮数 +1）、
  从**提权**与**受限**（runas `/trustlevel:0x20000`）上下文一律 **err=5 ACCESS_DENIED**。
  修法：新增 `src/FfmpegGui/Win32TrayIcon.cs`（STA 泵线程 + 隐藏窗口 + TrackPopupMenu 菜单 +
  TaskbarCreated 重挂 + `SHDefExtractIconW` 取 exe 内嵌图标），**NIM_ADD 返回值 = `_trayAvailable`**，
  失败 ⇒ `create=failed` + 关窗真退出；非 Windows 保留 Avalonia 路径。托盘线程事件必须
  marshal 回 UI 线程（`Dispatcher.UIThread.Post`），泵线程异常降级不崩进程。
  ⚠ **门禁 85 的 ⑥/④ 从此依赖运行上下文**：提权整轮（如 ZCode 会话内跑）⑥ 会红——真读数，
  不是假红；正常终端（medium-IL）跑才绿。④ 已于同批增补**契约第三态**判据（退出 + tray.log `create=failed`
  ⇒ PASS；退出 + `create=ok` ⇒ 仍 FAIL），⑦ 首断言靠 `create=failed` 行内联 `IsVisible=False` 变成上下文无关。
  探针与读数存 `tests/output/_tmp/tray-debug/`（UIA 枚举/Win32 NIM_ADD 对照/run3..run5 端到端日志）。
  beta12 出包读数（2026-11-04）：`pack.ps1 -Mode all` 双包；第 93 条验包 **16/0**（包内 exe sha16
  `2B4D42B03A50EEF6`）；第 85 条打包体 **6/1**（唯一红 ⑥=提权拒绝，真读数）；beta11 双归档 + 两个解包目录
  已删（删前 732 项 0 reparse point）。⚠ 更正：`4044f76` 声称的「标签 1.6.0BETA11」本地/远端都不存在。
  已知后续项：`Win32TrayIcon.cs` 两条分析器告警（CA1416 平台守卫、IL3002 单文件 GetHINSTANCE）——功能已被
  包体门禁实测覆盖，收口属独立一批。
  提交后重出（同日，§8 流程落地）：5 提交 `82976cf`（批次 E）→ `e592227`（TG 贴纸）→ `ef898e9`（托盘）→
  `14c3482`（本账本）→ `54c31c0`（release），tag `1.6.0BETA12` 锚 `54c31c0`；重打包 ProductVersion=
  `1.6.0-beta12+54c31c0…`，门禁 93 复跑 **16/0**、门禁 85 复跑 **6/1**（⑥ 提权拒绝真读数）；
  sha16 app/full/exe = `D8E3276728E88C79` / `38E52D2F528ACB35` / `EA8569DD5D8D3FD5`。
  评审备忘（Lead 复核 2026-11-04）：`Win32TrayIcon` 两处边缘点——`GetMessageW` 以 bool 签名收 -1
  （错误态会成 busy-loop，正常 hwnd 不触发）与 `Start()` 的 5s 启动竞态（超时即判不可用而泵仍可能后到）
  ——不影响本批功能（端到端实测通过），修不修属独立一批。
- **检测一致性修复（2026-11-04，维护者报「很多设备上检测不一致」）**：全链复盘六条检测链
  （ffmpeg 本体 / PLAN 定位 / 编码器枚举 / GPU 报告 / 外部工具五级搜索 / 能力表），定位六处放大器并同批修：
  - **B1（头号）settings 失效路径钉死**：旧逻辑只在 `FfmpegDirectory` 为空时回填并持久化 ⇒ 首启后
    settings 永远非空，路径失效（移包/换盘/拷贝 settings 到别的机器）后**永不重发现**，退化成裸名
    `ffmpeg.exe` 靠子进程 PATH 撞 ⇒ 编码器/能力/GPU 报告整体换血。修法 =
    `PlatformServices.EnsureFfmpegDirectoryResolved()` 单点（GUI ctor 与 headless 同源）+ 新增
    `AppSettings.FfmpegDirectoryAuto`（Auto/显式分离：失效时 Auto 值持久化、显式值只会话内改用不覆写）。
    ⚠ **`Save()` 的手写 clone 表必须同批加行**（漏加 ⇒ 落盘被打回 false；第 95 条 §1 当场抓到，
    `_probe-settings-clone-coverage` 是它的常设判据）。
  - **B2** 编码器/muxer 清单按 ffmpeg 分档（与 2026-10-29 解码器同族）+ `GetDefaultEncoder(fmt, enumerated)`
    去时序化；**B3** GPU 报告分档 + 不缓存空报告 + `-hwaccels` 降为信息展示（旧写法经「避免重复」
    守卫短路掉 `-init_hw_device` 实测 ⇒ 编译 cuda 无 N 卡也报设备可用）；**B4** `LooksLikePlan`
    候选合法性（非 env 候选须含 ffmpeg-full/jxl/exiftool/artifacts）+ `PlanFolderSource` 可见；
    **B5** `ToolCapability.Source`/`ClassifyToolSource` 五档来源标签（PLAN/manual/app-adjacent/
    extended-search/PATH）进 GUI 与 headless 报告。
  - 门禁：**第 95 条 `verify-tool-detection.ps1`**（清单 94→95，四处散文+常量同批；受管执行
    **19/0**/41.7 s；负控 = 判据打在有效路径夹具上必须不命中，`-SelfCheck` 期望红即红）。
    相关链路复跑：clone-coverage 8/0、CJK 14/0、矩阵 8/0、sln 0 警告 0 错误（Win32TrayIcon 两条既有
    告警除外）。实机取证 `tests/output/_tmp/detect_evidence/`（A1/A2/B 组/C 组）。
  - 已知边界：B2 的 in-process 分档无端到端闸（两进程演示只覆盖解析层，GUI 换目录走全量 ClearCache）；
    GUI ctor 解析无 GUI 级点击实测（与 headless 同走单点）；`-hwaccels` 信息行为新展示面、深色主题未核。
- **AVIF 的静态出口写不进 ICC（2026-10-26 实测）**：ffmpeg 的 AVIF muxer 不写 ICC，exiftool 后置
  也报「0 image files updated」而**退出码仍是 0**（672 B ProPhoto ICC：产物 `avifdec --info` 报
  `ICC Profile: Absent`，旧日志照打「ICC Profile 已恢复」）。`avifenc --icc` 能写，但它只用于
  GIF→AVIF 动画两步法 ⇒ `PlanPolicy.ExitWritesIcc=false`（由 `ColorIntentFactory` 按格式置位）：
  - 源空间**可 CICP 命名** ⇒ 零改动携带、以 nclx 描述（`AttachIcc=false`）；
  - 不可命名（ProPhoto/Adobe/自定义）⇒ S2 携带**契约拒绝**（exit 91，零产物）；S1 推荐落
    「最宽可命名目标 BT.2020/SDR + `GamutClipped` + `NoIccCarrier` 播报」。
  判据 = `verify-prophoto-faithful.ps1` §6 的 ⑥a–⑥f。
- **脚本清单现值 = 99 条（2026-11-10，color-matrix-options 批 T1+T7）**：第二十六次接线（批 T1）97 → 98
  `_probe-color-matrix-scan.ps1`（C-SCAN@1 扫描交付物生成器）；第二十七次（批 T7）98 → 99
  `verify-color-out-matrix.ps1`（显式输出矩阵模式 `--color-out-matrix` 的端到端判据面——avif 引擎通路
  nclx 读回（bt2020nc = H.273 枚举 9 + pixfmt-switch 兑现）、五格 fail-closed 任务级 exit 91、webp/jpg
  601 族冗余三态同值（CM-REQ-08）、cjpegli `--xyb` 接线证据、C-REPORT@1 三模板锁、`-SelfCheck` 负控臂；
  实测 **57/0** 正向 / **58/0** 含负控）。同批：`$scriptArgs` 负控键 5→6（第 ⑱ 针同批 7 条目/6 字面量/
  6 键按名点）；ui-sweep J 段 114→122（J19 八条）、ui-host 合计 446→454、union 446→454、CJK CsUi
  952→1041（EmitWarn 面 0 违规）。抬表链与取证见 `docs/TESTING.md` §3.5 同日追加段 +
  `tests/output/_tmp/color-matrix-t7-probe/`。⚠ git-bash 会话钉 TMP 必须写 Windows 风格绝对路径
  （`$PWD` 展开的 `/c/...` 会被 Windows 子进程解析挂到盘根，实测误建 `C:\c\` 已清理）。
- **CMX 一致性批（2026-11-11，用户裁决「色彩矩阵应该根据各个编码器的能力，禁止乱塞入」全量实现）**：
  A1 严口径落地（`ApplyExplicitOutMatrix` 跨族拒绝：显式 H.273 ≠ 推导值 ⇒ Rejected exit 91 + 模板 4；
  601 族 {bt470bg↔smpte170m} 互通；推导 null 放行）+ B1 GUI 过滤（`RefillOutMatrixComboForFormat`
  消费 `ResolveOutMatrixCapability` 唯一真值，非 Rejected 才进下拉）。**CMX-3 门禁抬表（无接线，
  清单条数不动；受管基线 = pngopt 批接线后的 100 条）**：
  - `verify-color-out-matrix.ps1` 同批改造：A1 群 5 条 + A7 群 2 条旧判据与 CMX-1 口径冲突
    （Display P3 目标推导 bt709 + 显式 bt2020nc = 跨族，基线轮实测 50/7）⇒ 改锚同族正控
    （显式 bt709 同值 Carried，nclx readback bt709 + 模板 1 逐字；A7 换同值后孤立点名逐字保留），
    跨族拒绝由新 **I 组七格**接管（I0 avifenc --cicp 广色域源运行时构造 / I1·I2 跨族双向拒绝
    模板 4+Reason 逐字 / I3·I4 601 互通双向 / I5 像素对拍 PSNR=inf / I6 无标注源同值 Carried /
    I7 webp auto sha16=9B6B2B1587840E05 绝对锚 + bt2020 源 auto 零播报）+ G5/G6 模板 4 常量锁
    + `-SelfCheck` 跨族第二负控臂。实测 **79/0** 正向。
  - ⚠ **实测修正**：判定表第 5 行「Reason 点名『源 primaries 未标注…』」端到端**不可达**
    （NormalizePrimaries 把无标注归一 bt709；ProPhoto 走 S1 推荐落点 dst=BT.2020）⇒
    推导 null 真执行证明保留在 CMX-1 selfcheck ⑤（Add-Type 反射 16/0），端到端改锁同值 Carried。
  - ui-sweep J19 段按 CMX-2 过滤行为重写（基线轮 116/6：J19a count=2、旧固定 index 全失效）
    + 新增 J19i-l 四条过滤断言 ⇒ **J 122→126**；三把尺同批：ui-host 165→**169**、合计 454→**458**、
    union 454→**458**；CJK **CsUi 1041→1047（第二十二次抬基线，+6 = CMX-1 中文同义句）**、
    探针复跑 14/0。生成器重跑幂等（reproducible 19/0）。
  - 受管子集与抬表取证：`tests/output/_tmp/cmx3-gate/`（delivery_evidence.md 为 CMX-3 交付证据）。
  - ⚠ **2026-11-11 更正**：本段标题「脚本清单现值 = 99 条」是 2026-11-10 时点读数；pngopt 第 100 条接线（`0f6c11a`）后
    实时权威值 = `$expectedScriptCount=100`（实测数组/常量/三散文/⑱ 针全 100），本句不再读作「现值」。
- **pngopt 集成 + 默认档变更（2026-11-10..11，另一工作线；清单 99→100）**：`PngOptimizerService` + CLI `--png-optimizer`
  五档 `off|default|max|zopfli15|zopfli30` + GUI `AdvancedCodecPanel` 联动（复合判定 `UseAdvancedCodec`）+ oxipng 10.2.1
  随包（MIT，`licenses/MIT.txt`）。**默认档 = `max`**（`FfmpegOptions.cs:331`、GUI `MainWindow.xaml` 组合框 `SelectedIndex=2`、
  `QueueProcessor` 直传；CLI 不传参也落 max）——`0f6c11a` 提交消息第 6 条写「默认 off 零漂移」与代码**相反**，以本条为准；
  off 档确实零开销（`PngOptimizerService.cs:45-47` 进程动作前短路）、oxipng 缺失的机器零字节漂移。
  吞吐取舍（24MP，`tests/output/pngopt-def-t4/<guid>/t4-final-report.md`）：off 7.65s / default 10.1s（-23.8%）/
  max 41.8s（-24.4%）/ zopfli30 600s 超时回落（T9：604.5s 墙钟、播报逐字、产物原样完整）。组合框藏在折叠的
  `AdvancedCodecPanel` 里——不勾高级面板的用户不可见却生效。门禁 **#100 `verify-png-optimizer`**（受管 61/0 / 748.6s；
  default 判据四条 + timeout case 真跑 600s）。旧预设回放无法把档位带回 off（`FfmpegOptions.cs:65` 仅 `!= null` 覆盖
  ⇒ null 不覆盖、停在 max）——与「默认 max」自洽，但「旧用户零漂移」此路不通。
  已知待批（2026-11-11 审查）：专用通路缺 `exitCode==0` 守卫（主分支 `QueueProcessor.cs:1444` 有、专用通路 `:1467-1470` 无）。
- **beta12 后内容审查读数与更正（2026-11-11，性能/色彩/GainMap/打包 四维度）**：`54c31c0..0f6c11a` 实测 **12 提交**
  （旧记「13 提交」把 release 提交自身也计入了）/ 80 文件 / +12469−322。四维度均**无 P0**；P1 共 11 条（性能 3：
  pngopt 默认档、ps-compat 提交态、pngopt 专用通路守卫；打包 3：#93 包-工具漂移、#1 提交态、AGENTS 条数；
  色彩 2：两步法/无色彩通路格式的显式矩阵静默失效；GainMap 3：AVIF alpha、八项零新增判据、engine §14d 红）。
  **「全部门禁验证」不成立**：HEAD 无 100/100 整轮基线（可查最近两次受管运行均为 `-Scripts` 子集：t3 2/100、t8 14/100，
  均注明「不构成基线」）；本轮裸跑确定性红 = `verify-color-wiring` **152/1**（pngopt 默认 max 使 PNG 质量档
  q60/q100 产物同为 20729B；`--png-optimizer off` 后恢复 24305 vs 592110B）、`verify-gainmap-engine` **219/1**
  （§14d：D6/D7 把「按政策忽略元数据⇒SDR 交付」判成降级⇒失败 → `Program.cs:367-368` exit 1，而队列汇总仍「失败 0 项」）、
  `#93` **14/2**（beta12 包内 exiftool 13.60 / dngtool 旧、落后 `publish/PLAN` 13.61 / 新 dngtool）、`#1` 提交态
  **12/1**（工作区 13/0；已由 `4d64ebd` 提交修复）。
  ⚠ **指纹更正**：`HANDOVER.md:4082` / `TESTING.md:594` 记 `FfmpegGui.dll=ADFC50DBEFE9C169` 不可复现；同 HEAD 源码
  确定性重建两次均为 `D9CF55784AA7BD02`（`dotnet build -t:Rebuild` 两次同哈希）⇒ 该段读数（94/0、81/0、90/0）已在
  D9CF 上复现，但「读数绑定被测件」形式要件不成立。卫生：`probe_v1`（2981 B HEVC 裸码流残留，sha16
  `8BE5D52692C12487`）已从仓库根移入 `tests/output/_tmp/beta12-review-hygiene/`。证据：
  `tests/output/_tmp/beta12-review-{package,color,gainmap}/`（三份 report.md + 日志）。

- **beta12 后审查修复批（2026-11-11，用户批准 ①–③ 后执行；本批共 7 提交：门禁/收口 2 + 修复 3 + 账本 2）**：
  - **① 立即收口**：`4d64ebd`（`_probe-png-opt-bench.ps1` 7 处 PS7 三元 ⇒ 门禁 #1 提交态 12/1→13/0）、
    `8456a69`（AGENTS 条数 99/94→100 + pngopt 账本 + beta12 审查更正条目 + INDEX 补登
    `HANDOVER-JXL-GAINMAP-2026-11-08.md` + NOTICES ExifTool 13.60→13.61 实测 `-ver` 对齐）、
    `probe_v1` 移入 `tests/output/_tmp/beta12-review-hygiene/`（sha16 `8BE5D52692C12487`，不删留证）。
  - **② 两条确定性红收口**（`6d9ead3`）：`verify-color-wiring` (4) 的 q60/q100 对比显式加
    `--png-optimizer off`（pngopt 默认 max 会把两档无损重压成同体积 20729B）⇒ **152/1→153/0**；
    `verify-gainmap-engine` 14d 按 D6/D7 契约（`Program.cs:367-368`：状态含「降级」⇒ 进程码非 0）
    改锁「降级出图 + code≠0 + 产物非空」⇒ **219/1→221/0**；`ConsoleQueueObserver` 汇总补
    「降级 N 项（降级按 D6/D7 契约计入进程退出码非 0）」，消除「失败 0 项 + exit 1」矛盾。
    E2E：DSC00037 的 min_version=1 变异件 ⇒ exit=1、`完成 1 项, 失败 0 项, 降级 1 项`。
  - **③ 修复线 A（`c1b87d3`，色彩/合成）**：显式 `--color-out-matrix` 在非承载出口不再静默——
    GIF→AVIF 两步法按 avifenc `--cicp` **只携带实测安全档**（smpte170m⇒`1/13/6`、bt470bg⇒`1/13/5`、
    rgb⇒`1/13/0 -y 444`；601 族与 avifenc 默认逐像素 PSNR=inf），bt709/bt2020nc 因 avifenc 的
    RGB→YUV 恒 libyuv BT.601（实测 43.76→42.26 / 38.60→38.45 dB）**不携带只播报**；bmp/ppm/dng/webm
    等出口收尾兜底点名（auto 零播报负控）。JXL 两图合成接线放开（`-f jxl` 承载 = jhgm box；
    `--gainmap-encoder` 对 jxl 建队拒绝；回读自检改 `JxlGainMapProbe`+ffprobe 尺寸，修掉「读不到
    jhgm 就删合法产物」）。门禁：color-out-matrix **98/0**（+19）/ `-SelfCheck` **100/0**、
    gainmap-jxl **33/0**、gainmap-compose 85/0。
  - **修复线 B（`9c67701`，GainMap/HEIC）**：AVIF alpha URN 补认 `AVIF_URN_ALPHA0`
    （`urn:mpeg:mpegB:cicp:systems:auxiliary:alpha`，保留 `auxid:1`）；`FfmpegCommandBuilder`
    补「无 -vf 时 aux alpha 仍 alphamerge」（旧实现嵌在已有 -vf 里 ⇒ 只改探针会得 rgba 但 alpha
    全 255）⇒ AVIF alpha→PNG `rgb24→rgba`、alphaextract `255/255→0/255`。D5 截断预检改
    fail-closed（`Result.TruncationIndeterminate`；**>256MB ISO-BMFF 现一律拒绝**，旧放行）；
    GainMapDecoder 真值探测改 `FileInfo.Length`（12MP 级少 139.6 MiB 分配）。新夹具
    `tests/data/synth_heic_alpha.heic`（77,237 B，sha16 `3873a32390124eb0`）。门禁：
    isobmff **167/0** SKIP=0、avif-alpha 16/0、engine-alpha-preserve 29/0、gif-avif-framelist 36/0。
  - **修复线 C（`e7d1292`，性能小批）**：PngOptimizerService 去整文件副本（三臂产物逐字节一致）；
    ExternalToolsDetector 根表/版本探测/artifacts-PLAN 解析缓存（空 PLAN 场景 stat 225→45，
    正常 PLAN 0/0 不夸大）；旧预设 pngopt 缺席⇒`off`（零漂移，新默认 max 不变）；#100 timeout
    余量只分析（受管 748.6s/900s = 151s）。门禁：tool-detection 19/0、pngopt **69/0**（含 600s
    timeout case 真跑）。
  - **CJK 第二十三次抬基线 1047→1060（`_probe-cjk-hardcode-scan.ps1`）**：归因 = 本批 +11
    （`IsoBmffGainMapProbe` 的 D5 Detail/return 文案，fail-closed 落 UI 桶）+ 先前 `6d9ead3` +2
    （队列汇总/状态文案）。取证 = 直接复用探针 `Measure-Cjk` 对 `git archive HEAD` 与工作树逐文件
    对撞（HEAD 1049→1060，11 条全部在 IsoBmffGainMapProbe），脚本
    `tests/output/_tmp/beta12-fix-verify/attr-cjk2.ps1`；探针复跑 **14/0**。
  - **验收口径**：本批**未跑 100/100 整轮**（用户偏好「非出包只跑相关链路」；且 `#93` 依赖下次出包
    才能绿）。受影响链路 13 条裸跑全绿，读数绑定 dll sha16 **`782F970073393F9C`**
    （sln Release 0 错误 0 警告）：ps-compat 13/0、color-out-matrix 98/0(+SelfCheck 100/0)、
    gainmap-jxl 33/0、isobmff 167/0、compose 85/0、avif-alpha 16/0、engine-alpha-preserve 29/0、
    gif-avif-framelist 36/0、color-wiring 153/0、gainmap-engine 221/0、tool-detection 19/0、
    decision-delivery 94/0、pngopt 69/0。证据 `tests/output/_tmp/beta12-fix-{color,gainmap,perf}/`
    与 `_tmp/beta12-fix-verify/`（build.log / gates.txt / cjk2.log）。
  - **未完成/风险**：`#93` 仍红（包内 exiftool 13.60/dngtool 旧，须升版本重打包解）；整轮基线仍未
    重测（下次发布批）；>256MB/链断时 QueueProcessor 文案仍写「文件不完整（截断）」（§1k 判据锁该
    文案，要区分「无法判定」须同批改产品与判据）；dng/webm 显式矩阵只有 bmp/ppm 有门禁臂；
    C3 GUI 点击级、C2 版本探测去重无 E2E 读数；C4（runner per-script 超时表/确定性挂死件）、
    `ResolveAvifencPath` 自身缓存、非 Apple aux grid（B4）属后续批。
- **.NET 11 GA 升级收尾（2026-11-11..12，交接批 `docs/HANDOVER-NET11-GA-PACKAGING-2026-11-11.md`）**：
  仓库根新增 `global.json`（SDK `11.0.100`，`rollForward=latestFeature`、`allowPrerelease=false`；
  用户 11-11 23:07 装 GA）。全量 restore+build rc=0（0 错 4 警：IL3002/CA1416 两条既有 +
  CS8604 `RawColorPipeline.cs:640` / CS8602 `QueueProcessor.cs:1557`——两行最后触碰分别是
  `0f6c11a`/`c1b87d3`，GA 会话源码零改动 ⇒ 4 条均与升级无关）。25/25 runtimeconfig 全
  `Microsoft.NETCore.App 11.0.0`（只认 `bin/Release/net11.0/win-x64/`；`bin/Debug`、RID-less、
  `bin/x64/Debug` 三处 rc.3/rc.1/framework-less 是陈旧残留）；assets ILLink=`11.0.0`，全树无
  `11.0.0-rc` 残留。**AOT scratch 五项验收全过**（`tests/output/_tmp/net11-ga/`，未动 `publish/`）：
  exe 24,219,136 B（beta12 RC.3 = 23,923,712 B，+1.2%）、`!AvaloniaResources`×4 / `!XamlIlPopulate`×2
  在位、`--help`/headless jxl+png 三臂 rc=0（`FFMPEGGUI_PLAN_DIR` 指 PLAN）、GUI
  `MainWindowHandle≠0` + 关闭 exit=0、exe sha16 `BBF3299E6C3435FF`。代表门禁 GA 下全绿
  （裸跑自钉 TMP）：ps-compat 13/0、color-wiring 153/0、gainmap-engine 221/0、decision-delivery 94/0、
  ui-host **458/0**（SKIP=1 J1z 既有口径）——绑定 `FfmpegGui.dll` sha16 **`525450CF9EE1F34A`**
  （RC.3 时代 `782F970073393F9C`）、`ServiceProbe.dll` `87E4412B289FC29C`。
  ⚠ **提权会话 GUI 冒烟坑**：`Start-Process`（ShellExecute）弹「打开文件 - 安全警告」模态框把
  driver 永久阻塞（exe **无** Zone.Identifier，非 MOTW）⇒ 必须
  `[System.Diagnostics.ProcessStartInfo]::UseShellExecute=$false`（裸 CreateProcess），
  修法已固化在 `tests/output/_tmp/net11-ga/gui-smoke.ps1`。出包版本已裁定 **1.6.0-rc.1**；
  用户指示不急着出包，先行「测试拆碎成模块 + 覆盖不落后」任务，出包顺延（`#93` 随重出包解）。
- **测试模块化批 1+批 2 模块 1（2026-11-12，方案 = `docs/TEST_MODULE_PLAN_2026-11-12.md`）**：
  **机读模块账本** `tests/scripts/_module-registry.json`（66 条目：33 具名选项 + 10 透传代表 +
  13 能力域 + 10 盲区；tier 四档；locks = 三把尺现值 458/126/458）+ 第 101 条
  `verify-module-registry.ps1`（判据①CLI 选项全覆盖双向差集=「不落后」针 / ②档位不撒谎（逐条目
  carrier 语料）/ ③carrier 在册 / ④结构锁+src ±3 行活见证+三把尺对账；不跑 exe 不进 STALE；
  负控三内存变异）+ 第 102 条 `verify-overwrite-contract.ps1`（**盲区#1 防覆盖契约 E2E**：no-overwrite
  拒绝逐字+旧产物 sha 不动+零改名件 / overwrite 真覆盖 / 默认档=覆盖钉死 / 退出码对账 1/0/0）。
  清单 100→**102**（四处同步同批：数组/常量/三散文/⑱针 8→10 条目 7→9 字面量）。
  实测：受管子集 module-registry **10/0**、overwrite-contract **18/0**（各含 -SelfCheck）；
  overwrite-contract 裸跑正向 17/0。⚠ 两笔实测教训：**①批 1 首轮负控抓出真缺陷**——② 首版全局
  共享语料让字面量经别的条目 carrier 漏进判据（M2 变异 3 抓 2）⇒ 改逐条目语料后 3/3（负控价值的
  实证）；**②调研更正两条**——B 报告盲区#7（--preset）部分过期（pngopt §2d 已是 E2E）、盲区#1
  （--no-overwrite）本批收口；--help/--version/--gui 三个选项级 blind 是新发现的真缺口。
  ⚠ **AGENTS §7 ⑪-d 条目更正**：「CoveredBy⇒调用点双向检查待做」已过时——2026-11-02 已落地
  （`_probe-test-group-coverage.ps1:1008-1088` AST 调用点索引 + D1–D4 判据），⑪-d 剩余工作只有
  「_probe-/_make- 族新脚本从 WARN 收紧」。⚠ `-Scripts` 子集参数必须带 `.ps1` 全名（不带则
  响亮拒绝，且该校验发生在探针阶段之后——传错名会白跑 ~4 分钟探针）。
- **测试模块化批 2 完成 + 批 3 落地（2026-11-12，`docs/TEST_MODULE_PLAN_2026-11-12.md`；清单 102→103）**：
  - **批 2 模块 1+3（防覆盖/损坏输入）**：#102/#103 两门禁 + **附带真缺陷修复**——`QueueProcessor.cs`
    三处英文失败状态词不满足 `HasError`（StartsWith("失败")）⇒「条目失败+汇总失败 1 项+exit=0」并存
    （D6/D7 同族漏网）⇒ 改失败前缀中文；CJK 第二十四次抬基线 1060→1063。实测 corrupt-input **29/0**、
    overwrite-contract **18/0**（各含 -SelfCheck）。
  - **批 2 模块 2（取消路径 G2）**：单体 GroupG +6（按 InputPath 锁定断言——`AddToQueue_Click`
    批量添加陷阱见 TESTING.md 第三十次接线段）；**ui-mode 53→59 / 合计 458→464**（三把尺+账本 locks 同批）；
    ui-host **464/0**、union 464/464、verbatim 22/0、reproducible 19/0。
  - **批 3（ServiceProbe 源级 partial 拆分）**：10,671 行 → 48 行壳 + `Parts/` 65 文件；重建审计逐字节
    相等；**方案验收前提更正：plain dll 哈希等值原理不可达**（PDB 派生字节 ≈71B/5 簇随任何源触碰变化）⇒
    硬证据 = `-p:DebugType=none` 成对构建 **7770FDE924BB1995** 等值（IL+成员序零漂移）；编译序实证
    Parts 先于壳 ⇒ 头部入 00_Head.cs。**同批迁锁三处**：mirror-parity **90/0**（`$SPFiles` 拼接解析面）、
    test-group-coverage **40/0**（`$spProgram`/`$spText`/`$groupProgram['ServiceProbe']` 三处）、
    host-temp-sites **5/0**（11 站点逐行迁表，总量守恒）；STALE 自适应无需改。
  - 指纹：`FfmpegGui.dll=F15B1AED2557040D`、`ServiceProbe.dll=E62837233822D552`（拆分终态）。
  - ⚠ 教训族：门禁驱动函数参数**严禁 `$args`/`$input`**（PowerShell 自动变量撞名静默换值——
    corrupt-input 一天踩两次：夹具 0 字节 / exe 秒退 exit=2 零输出）；`StringBuilder.Append` 不 `[void]`
    溢出输出管道使语料 `.Contains` 全 false。已存项目记忆。
  - 盲区账本现值：12 条中 **4 条已收口**（no-overwrite/cancel/corrupt + --preset 部分过期更正），
    账本条目 66（两盲区转 `cap.cancel-path`/`cap.corrupt-input`）。
- **测试模块化批 4·1（2026-11-12，清单 103→104）**：第 104 条 `verify-log-jsonlines.ps1`——
  `--log-format JsonLines` 格式正确性 E2E（调研盲区#9 收口）：全行 schema 锁（t=ISO round-trip **+00:00**
  非 Z、在**原始行**上锁——`ConvertFrom-Json` 会把 ISO8601 串自动转本地时区 DateTime；l 小写枚举
  （`-ccontains`，`-in` 大小写不敏感是漏网口）；m 非空；c∈实跑全集）+ Info 阈值过滤兑现 + 汇总行 p/tot
  （pwsh JSON 整数落 [long]，`-is [int]` 会误杀）+ 双写等价（文件行 ⊆ stdout）+ debug 解锁 + 默认 Text
  负控 + 非法档位 exit 2 + 文件打不开不崩；中文 `\uXXXX` 转义 ⇒ 一律 parse 后比对。实测受管 **30/0**
  含 -SelfCheck（首轮 21/7，三处都是判据实现错、产品无罪）；⑰针当场抓到第四处散文（文件头「全跑 103 条」）
  未同步拒跑。账本：`blindspot.log-jsonlines`→`cap.log-jsonlines`；**`blindspot.ipc-single-instance` 改记
  产品缺口**——「二次启动→转发→退出」链路产品不存在（`TryConnectAsync` 生产侧 0 调用点，无互斥锁），
  接线与否是 S-P1-2 行为决策（TESTING.md「仍未做（需拍板）」），先接线后立判据。
- **测试模块化批 4·3+4（2026-11-12，无新门禁；三把尺 464→473）**：单体 GroupN 增 **N8(5)+N9(4)**
  ⇒ ui-core 75→**84**、合计 **473**（=84+59+89+169+72）。N8 = 设置改→重启→GUI 生效（Save 落盘
  （exe 侧 settings.json 快照/还原）→清 `_current` 缓存重读→`LoadSettings()` 推进控件；**有意不 new 第二个
  MainWindow**——其 ctor 起托盘线程/检测，LoadSettings = ctor `:1333` 同款「应用」段）。N9 = 四子窗口
  headless 冒烟（FormatFilter/PresetManager/ImageDetail/Progress：真实构造→Show→控件在位→Close；
  不碰 ShowDialog 模态路径——headless 模态泵无人应答，构造路径与模态共享 InitializeComponent）。
  实测：单体 473/0、verbatim 22/0、reproducible 19/0、union 11/0（473/473，裸跑）、ui-host **473/0** @63s
  （受管）、module-registry 受管 10/0。
  ⚠ **本批顺带钉出三个既有问题（各自独立处置）**：① `FFMPEGGUI_FFMPEG_DIR` **分隔符敏感** ⇒ UI 宿主硬件
  编码器检测静默归零（正斜杠混合路径零 ⚡ 项、全反斜杠 473/0，两向实测钉死，非间歇性）——runner 已改
  Join-Path；产品侧归一化（env 覆盖应用处 GetFullPath）独立一批（B1/B5 家族；L1e「间歇性」旧读数疑为
  此机制+负载噪声混记）。② **union 受管轮 G2 确定性红**（**已于同日收口**，见下方「union 改全组跑」条目）：
  union 按 mode 传参（只跑 E,F,G）⇒ G2
  `workers=[]`+`失败 (退出码 -22)`（muxer EINVAL）2/2；全序列（不传 mode）六条全绿——union 自批 2 起
  从未受管跑过带 G2 的清单（批 2 的 464/464 是裸跑），候选修法 = union 改全组跑（与 ui-host 同形），
  独立一批。③ 宿主退出路径重写 exe 侧 settings.json（N8 快照/还原管不到；文件在场无害——C 轮实测绿）。
- **测试模块化批 4·5（2026-11-12，无新门禁；ServiceProbe 新 mode `mediasvc` + VTD §5）**：
  三服务（MediaInfo/PlanFolderDetector/PsRender）0 测试引用收口 = ServiceProbe
  `Parts/65_ProbeMediaSvc.cs`（17 源文本调用位入 mirror-parity 表）+ verify-tool-detection §5 载体。
  锁面：media（ffprobe JSON/缺文件不崩）+ plan（否定路径契约 + IsValid⇒四目录不变式）+ ps
  （条件不变式 + ClearCache 幂等——本机装有 PS 2026 ⇒ DetectedPath 在盘被锁）。实测：mediasvc 9/0、
  VTD **24/0**（-SelfCheck 负控按契约 exit=1）、mirror-parity **91/0**、coverage 40/0、registry 9/0
  （⚠ registry domain 必须在白名单内，首登 'services' 被当场抓红）。盲区 7/12 收口。
- **测试模块化批 4 完成**。剩余独立刀候选：① FFMPEGGUI_FFMPEG_DIR 分隔符产品归一化 ② union 改全组跑
  （**已收口**，见下条）③ FFMPEGGUI_PLAN_DIR 正斜杠同族排查（三条见批 4·3+4 条目）。
- **union 改全组跑（2026-11-12，收口批 4·3+4 发现②；无新门禁，清单条数不动）**：
  `verify-ui-split-union.ps1` 第 2 步「逐 mode 起进程（19 次）」→「逐工程**全组**跑（5 次，不传 mode，
  与 ui-host 同形）」——G2 确定性红的根因（按 mode 过滤的 E,F,G-only 序列，产品无此执行形态）整体移除；
  判据八条逐字未动；`$work` 同批迁 `tests/output/ui-split-union/<guid>`（§3）；runner :1436 旧「各 mode」
  描述旁补更正注。实测：**受管子集**（ui-host+union+registry）——ui-host **473/0** @155.3 s（-SelfCheck
  负控臂按契约红）、**union 11/0 @99.7 s**（473/473，5 工程全组 84+59+89+169+72 与 ui-host 五件锁逐一
  相等）⇒ 受管轮 G2 红消失（批 4·3+4 同环境 2/2 复现）；registry 10/0。子集 rc=1 唯一因 probe:bench
  35/2（§7 计时噪声族，当轮有并行重建负载，阈值不动）。⚠ **STALE 闸活例子**：本批首跑受管子集被
  STALE 当场拒（五件 exe 03:57 < TestGroups.cs 05:11——批 4·5 动了 Tests.Shared 而五件没重建），
  重建五件后复跑才绿；同期一次裸跑 union 11/0 用的是重建前旧件 ⇒ 裸跑读数不作验收。
  取证：`tests/output/_tmp/union-fix/`。剩余独立刀：① FFMPEGGUI_FFMPEG_DIR 分隔符产品归一化
  ③ FFMPEGGUI_PLAN_DIR 正斜杠同族排查。
- **测试模块化批 5·死支处置 + 覆盖探针 fullrun 证据链（2026-11-12，无新门禁，清单 104 条不动）**：
  ①死支三删一留：`git rm` `run-color-regress.ps1`（纯聚合器，8 个被调者全部在册、零自有判据）/
  `run-full-matrix-test.ps1`·`-safe.ps1`（被测路径按 pack.ps1 模板任何版本都不会产生、语料 `D:\TEST2`
  已不在盘）；`run-ps-verify.ps1` **留**为手动 ACR 目检工具（理由改写、不再写「待证后删」）；全仓 grep
  零在册引用悬空。同批：`gainmath avifgm` 三处过期注释更正（isobmff 门禁实际不调它）、
  `_probe-jxl-webp-pixels.ps1` 缺素材提示改指在册脚本、coverage 显式表 8→5（清单外 32→29）、
  registry +1 条目 66→67（`manual.serviceprobe-diag-modes`：ipc 6 + piperaw 2 + tonemap 手动诊断域，
  carrier=$modeExempt/⑨-b，非覆盖缺口）。
  ②⑥ 调用点索引增 **fullrun 证据链**：union 改全组跑后 mode 名实参消失 ⇒ ⑥-d 曾当场红 **14 个
  ui mode**（唯一调用点只剩清单外 `_accept-ui-split.ps1`）——证据链模型没跟上全组执行形态
  （ui-host 2026-11-01 全组化时就欠着，当时被 union 的表证据掩盖）。修法 = (1b) foreach 循环变量→
  集合根变量片段映射 + (5b) 「在册脚本起拆分件宿主 且 起宿主命令的**实参元素**无一解析出该组任一
  mode 名 ⇒ 按组表给该组全部 mode 记 kind='fullrun'」（ServiceProbe 显式除外）+ union `$modes` 表改
  纯 exe 字面相对路径（mode 清单删除——组归属唯一真值是 TestGroups.cs，勿抄第二份）。
  ⚠ **根因（DBG5b 实测钉死）**：union 表**键名**（'ui-color'/'ui-sweep'）被 Get-Fragments 当字符串
  字面量收进 varMap ⇒ 注入整条命令解析文本 ⇒ 组名==本组某 mode 名的两组被误判「带 mode 起件」
  （ui-core/ui-mode/ui-cli 键名非本组 mode 名故幸免）⇒ modeInArgv **只扫实参元素**（cmdlet/launcher
  形态 `GetCommandName()` 非 null ⇒ j≥2，j=1 是 exe 路径；call-operator 形态 `& $prb 'memadmit'` ⇒
  j≥1；namedPiece 仍用全文）。四个形状验算全成立（union 全组不误滤｜SYNTH case4/5 与 param 系循环喂
  mode 照滤｜ui-host 自检臂照滤｜gainmap-host 表喂照滤）。
  ③实测：探针 39/1→**40/0**（⑥-d 14→0、档②=52，12/14 ui mode 由 ui-host+union fullrun 记账）、
  union **11/0 @473**（表形状改过，复跑验门禁本体没破）、registry **9/0**（裸跑自钉 TMP）。
  ⚠ `verify-cli-strict` 对 ui-color/ui-icc/ui-cli-strict 的 argv 证据**本来就没被索引识别**（传参形态
  索引不认），此前一直被 union 全 mode 表证据掩盖；fullrun 链立起后缺口不再是活问题，但**别把这
  2 个 mode 的绿记在 cli-strict 头上**。⚠ 修复过程工具坑：OrderedDictionary 整数下标按**插入位置**
  不按键（`$cmdArgv[$line]=@(...)` 全部静默丢失 ⇒ 36/35 大面积假红，教训存 memory）；组名自撞
  （mode 名整 token 住在工程路径里）随实参元素方案天然消失。取证 `tests/output/_tmp/batch5-deadbranch/`。
- **测试模块化批 5 收口·C4 超时表 + 索引生成（2026-11-13，清单 104 条不动）**：
  ① **C4 per-script 超时表**：单一真值 = `_module-registry.json` 的 `locks."script-timeouts".table`
  （`verify-color-strategy` / `verify-png-optimizer` 各 1200 s；表外仍用全局默认 900；表只许**抬**，
  低于 900 = 门禁 ⑤ 红）。runner 装载点必须在 `$scriptList` **之后**（幽灵键校验要对清单）、逐条查表
  覆写 `$_sTimeout`、结果行打 `to=<n>s`。⚠ **交接件里的 fail-closed 实为 fail-open**：装载失败分支
  原调 `Exit-WithSummary`，而它在 `$failedItems` 为空时打「全部通过」并 `exit 0` ⇒ 删掉账本键后整轮
  照绿；改 `Release-GatesLock; exit 2`（实测删键 ⇒ FAIL 读数 + rc=2 + 零脚本执行）。形态自检 18→**19 针**：
  ⑲ 管**五形 + 装载块位置**（后两形由变异逼出——只把 `to=` 的 `-f` 实参换回 `$ScriptTimeoutSec` 时 ⑲ 曾全绿）。
  读数：color-strategy **78/0 @267.1 s to=1200s**、registry **12/0**（5/5 负控）、script-list=104、错名 rc=1 仍拒。
  ② **TESTING.md 逐条索引从账本生成（批 5 剩余项②）**：`verify-module-registry.ps1 -EmitIndex` 渲染
  `<!-- module-index:begin/end -->` 段（**§九、文末追加**——中间插入会挪 coverage 探针 `:1115` /
  cjk 扫描 `:1828` 两条行号锚，见 `docs/HANDOVER.md:3984`）；默认跑 = ⑥ 逐字节对撞并报「段内第 N 行：
  现存[…] vs 生成[…]」。现量 **67 条 / 101 行 / 8777 B**，两次生成文件哈希不变（幂等）。
  ⚠ 两个工具坑：`[Array]::Sort(keys, object[] items, cmp)` **keys 排了、items 没动**（改索引表排序）；
  `Sort-Object` 走当前文化（`-` 权重随区域设置变）⇒ 排序一律序数比较。取证 `tests/output/_tmp/batch5-c4/`。
- **全量轮（2026-11-13，104 条 / 2h00m / rc=1）抓到三类陈账：门禁状态污染 · 判据锚点漂移 · 环境读数**：
  ① **门禁污染产品设置（最严重，已修）**：`verify-startup-warmup` 的 S8f/S8g 用 `FFMPEGGUI_DNGTOOL_PATH` /
  `FFMPEGGUI_ARTIFACTS_DIR` 覆盖跑 headless，并故意把 `FFMPEGGUI_FFMPEG_DIR` 指空目录 ⇒ 子进程
  `EnsureFfmpegDirectory` 走「重新发现 PLAN」分支并 `AppSettingsService.Save()`，而 Save 序列化整份
  `_current`（含 env 覆盖值）⇒ **便携 `settings.json` 被写成 `DngToolPath=<ffmpeg.exe>`、
  `WindowsArtifactsDir=<门禁临时空目录>`**。后果：同一轮里 `verify-metadata-privacy` ⑥ 报「RAW→PNG 未产出
  png」，其日志里 dngtool 打的是 ffmpeg 的 banner + `Unrecognized option 'o'`。修法两条：`--settings <临时副本>`
  （Save 写 `_loadedFrom` 且 explicit 分支不回退）+ 新判据 **S8h**（前后对撞便携 settings.json sha256）。
  变异验证：去掉 `--settings` ⇒ S8h 转红且设置**逐字复现**上述污染（取证 `tests/output/_tmp/fullrun-1113/`）。
  ⚠ 通用教训：**门禁用 env 覆盖跑产品时，必须同时隔离"产品会写回的文件"**——env 覆盖是内存态，任何
  一次 Save 都会把它固化成用户设置。产品侧 `Save()` 仍会落盘覆盖值（本轮只在门禁侧隔离，未改产品语义）。
  ② **判据锚点漂移（已修三处）**：`verify-startup-warmup` 的 S1/S1b/S8f/S8g 正则要求行尾即 ✅/❌，而
  `Program.cs` 自 `e7d1292`（2026-11-11，工具报告来源可见）起追加 ` [PLAN]/[manual]/[app-adjacent]/…`
  ⇒ 10 条假红（报告读成 0 行）；`verify-ffmpeg-heartbeat` 的 `RunAsync` 调用点锁 26 → **27**（新增点
  `QueueProcessor.cs:6814`，`e592227` 的 Telegram 贴纸降码率重编码）；`_lib-matrix` 六处行号锚（1156→1329 /
  1168→1341 / 852→878 / 2563→2767 / 804→830 / 1200→1373）随 11-11 起三笔 src 批漂移（`git log -S` 证字面量未变）。
  ③ **本轮剩余 8 红（与批 5 无关，另起一轮）**：`verify-color-engine-xcheck`（**库侧**：ColorGainKit 的
  `IsoBmffGainMapProbe` 停在 2026-10-12，709 行 vs 宿主 1407 行，读 `ref_libavif_solo_gainmapgrid.avif`
  得 dimg 候选 0 ⇒ 11 处字段不等，需在 `C:\PLAN\ColorGainKit` 处理）、`verify-color-token-normalize`
  （**900 s 超时挂死**，R5 之后无输出）、`verify-ui-host` + `verify-ui-split-union`（同一根因：ui-mode G2
  `workers=[]`，前提「24MP 条目开始处理」30 s 未达成）、`verify-package-gui-starts` ⑥（托盘
  `Shell_NotifyIcon(NIM_ADD) denied err=5` = 提权/受限会话族）、`verify-release-package`（出包 `exiftool.exe` /
  `dngtool.exe` 与 `publish/PLAN` 指纹不等）、`_probe-host-temp-sites`（实扫 41 vs 表 39，
  `65_ProbeMediaSvc.cs` 新增 2 站点未入表）、`_probe-color-matrix-scan`（`convmatrix_engine` 锚点 miss +
  `CjpegliService` 的 `xyb` 命中 6 须 0）。**基线判读口径**：本轮 rc=1 由这 8 条构成，批 5 的
  `verify-module-registry` / `verify-color-strategy` / 19 针 / `registry-timeouts` 全绿。
- **全量轮 8 红处置（2026-11-13 余波二；清单 104 条不动、无新门禁）**：3 条真漂移修绿 + 1 条加诊断 + 1 条属于包过期 + 1 条跨仓
  ① `_probe-host-temp-sites` **41 vs 39**：探针 ① 扫「含 `GetTempPath` 的行」**不剥注释**，而同文件 ③（坏根名黑名单）
  早剥了（自注「与 _probe-cjk-hardcode-scan 同口径」）⇒ `65_ProbeMediaSvc.cs:21` 的说明注释被当第 2 个站点。
  修 = 口径统一 + 补表项（`mediasvc_<8hex>`，`:33` 建/finally 删）+ 更正两处失效行号（`Program.cs:7823` 自批 3 拆分起不存在；
  `PlatformServices.cs:501-552` → `:645-695`）。现 **40 == 40**；变异：活代码加站点 ⇒ 红、**仅注释提及** ⇒ 绿。
  ② `_probe-color-matrix-scan` 按**自身约定**重校准（T6 `0f6c11a` 已接线 `--xyb`，旧 ④「命中==0」必红）：锚点重钉
  `RawColorPipeline.cs:1023` 新形态 + 新增 `cjpegli_xyb_append` 证据锚 + ④ 由计数锁改**形态锁**（append 恰 1 + auto 守卫行
  在场；计数锁会被注释改动误伤、接线后无语义）。**331/0**。
  ③ `verify-color-token-normalize` 轮内 900 s 挂死**未复现**（单跑 65/0 @46 s）：真缺陷是 `Run-Exe` 裸 `Start-Process -Wait`
  **无 timeout** ⇒ 改 `WaitForExit(180s)` + `taskkill /T /F` + 末尾断言「子进程无挂死」点名完整命令。变异 `$runTimeoutSec=1`
  ⇒ 3 次超时、41 s 内 rc=1 并打出命令行（原行为 = 挂到 900 s 且日志尾部丢失）。
  ④ ui-mode **G2** 间歇（3 绿 1 红，单跑/子集均 473/0）：两个等待原**共用一个 40 s 预算**（前一个耗满 30 s ⇒ 后一个只剩 10 s）
  ⇒ 各自独立计时 + 读数带 `processing/procWait/workerWait/status/log`（变异实测打出 `processing=True … status=[处理中]`）。
  ⚠⚠ **变异后还原的 mtime 陷阱**：`Copy-Item` 还原源文件会把 mtime 恢复成旧值 ⇒ MSBuild 判 up-to-date、
  `dotnet build` 报 0 错但**没重编** ⇒ 下一步验证跑的是变异二进制（本轮实测：两支门禁双双红在 `workers=[2]`，
  即 `2 > 100` 的变异条件，一度误判成回归）。**还原后 `touch` 源文件再 build**，并核对「产物 mtime > 源 mtime」。
  ⑤ `verify-release-package` 2 红 = **包过期**（包 11-04 / exiftool 11-05 / dngtool 11-09）⇒ 下次出包重切，**不在轮内改判据**；
  同批更正脚本头「有意不进受管清单」的失效陈述。
  ⑥ `verify-color-engine-xcheck` = **库侧滞后**（宿主 `41375a2` 2026-11-02 起把增益图候选分 codec / **grid** 两类；库
  `79c3f4b` 2026-10-12 只认 codec ⇒ `solo_gainmapgrid` 夹具候选 0）。修在 `C:\PLAN\ColorGainKit`，**工作区外**。

---

## 8. Git 历史纪律（2026-11-02 立规；依据＝提交历史审查 159 提交/3 merge/33 tag）

**历史本体不回写**：共享史已推送，且全仓账本/日志按提交哈希绑定读数（`753cb33`/`5fd3996`/
`f778280` 等）——改写历史 = 在册读数全部变悬空指针。以下规矩只管未来：

1. **tag 一经推送即冻结，不许重指**：重出（同内容重新打包）一律**升版本号**、新打 annotated
   tag。beta10 时代 `1.6.0BETA10` 曾四次重指、现指非 release 提交 `5fd3996`，「tag=可重现出包点」
   已失效一次，不收拾第二次。
2. **版本位只进不退**：升了就升了，不回头（10-28 当天 beta10→beta11→beta10 的反转不再发生）。
   版本号唯一来源 = `FfmpegGui.csproj` 的 `<Version>`；pack.ps1 默认读它，显式 `-Version` 必须与之一致。
3. **release 提交语义分离**：release 提交只装「版本位 + CHANGELOG + tag 指向」；修复独立成 fix
   提交（9ea68f3 教训：名为 release 实改 3 文件，消息声称的修复在包内容 diff 里而非本提交）。
4. **动版本位必同批记 CHANGELOG（zh/en 同批）**：首发与重出都记（重出 #2/#3/#4 当时漏记，
   事后靠 2026-11-02 轨迹补记收口）。
5. **批次提交自封顶**：一次提交主题数尽量 ≤3；确需批次收口（753cb33：61 文件 ≥6 主题）时，
   body 必须自带分节清单、每主题在账本有独立条目，并接受「单主题回滚不可行」的代价。
6. **纠偏靠更正条目，不靠撤回提交**：写进文档的数字发布前当场核对读数（近 40 条提交里
   「撤回/更正」类 ≥12 条，首发读数屡错的成本已付过）；已推送内容有错就按 §6 新写更正条目，
   不 revert、不改史。
