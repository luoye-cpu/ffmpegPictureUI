# ffmpegPictureUI 项目规范（本仓）

跨项目的协作规矩在全局 `~/.qoder-cn/AGENTS.md`；这里只写**本仓特有**的构成、归置契约与验收口径。
定位：Avalonia 桌面图片/动图转换器（`FfmpegGui`，WinExe + headless CLI），封装 ffmpeg 与外部编码器，
内置纯托管色彩管线。三层构成：**UI 层**（视图 + `MainWindow.xaml.cs`）· **管线层**（`Services/`）·
**色彩引擎层**（`Services/ColorMapping/`，对 UI 零依赖，`using Avalonia` 实测 0 处）。

---

## 1. 目录契约（唯一归置规则）

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

1. `$scriptList` 数组（94 条）；
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
pwsh -NoProfile -File tests/scripts/_run-step3-gates.ps1   # 94 条，整轮 ~60 分钟
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
- **整轮 ≠ 全判据面（N/N 要按这三点读）**：①5 条 UI 门禁的负控块包在 `if ($SelfCheck)` 里、
  `$ncOk` 无条件默认 true，runner 不给它们传 `-SelfCheck` ⇒ **整轮读数只有正向臂**，负控证据
  全来自手跑（`tests/output/ui-host/*/out_*.txt` 的 `NC:` 行）；②第 92 条 `verify-oracle-matrix`
  整轮只跑 `-Smoke` **3 条代表用例**（自述 L1=194/L2=172）；③第 91 条 `verify-color-matrix-full`
  整轮默认 `quick` = **72 格**（2 格式 × 2 位深），名带 `-full` 不等于全网格。
  ⇒ 宣布「94/94 全绿」时不许顺带说「色彩/oracle 覆盖面一次到位」。
- **分层**：`Services` 对 UI 零依赖，唯一的代码级倒灌是 `IpcService.cs` 认 `MainWindow` 类型（13 处）；
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
  旧臂重建 2 次 + 同窗对照 1 次共 **9 次 37/0**，定性为负载/计时噪声成立（阈值仍不动）。
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
- **`colorRange` 回拷漏项（潜伏，未修）**：`FfmpegCommandBuilder.cs` 的缓存回拷段（2026-11-01 只补了
  `pixFmt`）仍**不拷** `colorRange` ⇒ 命中缓存的路径上 8-bit 尺可能取错。要有自带判据臂的独立一批。
- **ACR 路线只覆盖 DNG（2026-10-24）**：`.dng` 走 `dngtool -rdp`（Adobe DNG SDK 的 `dng_render`，
  OpcodeList1/2/3 全施加），其余 RAW 仍走 LibRaw（**SDK 只读 DNG**，读不了 ARW/CR2/NEF…）。
  渲染空间按下游目标选（bt709 出口 ⇒ `srgb`，其余 ⇒ `rec2020`），`ColorSourceDeclPrimaries` 与它同源。
  **LibRaw 臂的中间件自 2026-10-26 起固定恒等 gamm（真线性 16-bit）**：LibRaw 默认 `gamm={0.45,4.5}`，
  `write_ppm_tiff` 会套 dcraw 曲线，与产品声明的 `linear` 冲突（二次 gamma，实测 ARW→PNG 中位 0.41 vs
  参考 0.32）；修法在 `dngtool.cpp` 解码臂，判据 = 第 79 条 ⑫（同一 DNG 的 `-d`/`-rd` 中位比 0.35~1.5）。
  已知边界：enhanced-image IFD 的 DNG 直接点名拒绝；DNG 输入上 `-H/-q/-W/-threads` 不生效（日志点名）；
  耗时约为 LibRaw 的 2.2×（24MP 5.1 s vs 2.3 s）。要"全量"须先 LibRaw 解包成 CFA DNG 再 SDK 渲染（另立一批）。
  门禁 = 第 79 条 `verify-acr-route.ps1`。
- **托盘 / 关窗契约（2026-11-01 定，由第 85 条 `verify-package-gui-starts.ps1` ⑥⑦ 保证）**：
  托盘图标随启动即常驻（跟 `AppSettings.CloseToTray` 走，默认 `true`），不是「关窗时才点亮」；
  **只有「设置要托盘 且 托盘真可用」才拦关窗**，否则关窗即真退出（不许藏成不可见进程）；
  退出唯一实现是 `App.ExitApp()`（托盘菜单 / ⚙「退出程序」/ `Closed` 兜底三处共用，`_exiting` 闸防重入）。
  托盘失败一律落 `tray.log`（`FFMPEGGUI_DIAG_DIR` > 缓存目录 > exe 同目录），不再 `Debug.WriteLine` 无痕。
  ⚠ **不要改 `ShutdownMode` 为 `OnExplicitShutdown`** —— `IpcService.cs` 的 `AllowFullExit=true; Close()`
  会变成僵尸进程。⚠ 「备份→还原→跑门禁」必须 touch 源文件：`copy2`/`cp -p` 带回旧 mtime 会让 MSBuild
  跳过编译、拿变异体跑出**假红**（实测踩过）。⚠ `MenuFlyout` 里的 `x:Name` **不生成字段**，取控件用 `FindMenuByTag`。
- **AVIF 的静态出口写不进 ICC（2026-10-26 实测）**：ffmpeg 的 AVIF muxer 不写 ICC，exiftool 后置
  也报「0 image files updated」而**退出码仍是 0**（672 B ProPhoto ICC：产物 `avifdec --info` 报
  `ICC Profile: Absent`，旧日志照打「ICC Profile 已恢复」）。`avifenc --icc` 能写，但它只用于
  GIF→AVIF 动画两步法 ⇒ `PlanPolicy.ExitWritesIcc=false`（由 `ColorIntentFactory` 按格式置位）：
  - 源空间**可 CICP 命名** ⇒ 零改动携带、以 nclx 描述（`AttachIcc=false`）；
  - 不可命名（ProPhoto/Adobe/自定义）⇒ S2 携带**契约拒绝**（exit 91，零产物）；S1 推荐落
    「最宽可命名目标 BT.2020/SDR + `GamutClipped` + `NoIccCarrier` 播报」。
  判据 = `verify-prophoto-faithful.ps1` §6 的 ⑥a–⑥f。
