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

⚠ **零引用 ≠ 无用**：`_sweep-output.py --unreferenced` 删的是"名字在在册文本里一次都没出现过"的条目，
而**手工跑出来的活线产物正属此类**——2026-10-22 就这样误删了 `jxr-probe`(627 MB) 与
`jxr_engine_probe`(556 MB)，两者没有任何在册脚本能重生成，也不在 git/回收站/卷影里，不可复原。
用这一档之前先读一遍待删清单；`--runs`（按年龄）那一档不受此限，因为轮次目录本就由在册脚本重建。

## 2. 门禁登记：清单本体就是运行器

`tests/scripts/_run-step3-gates.ps1` 是清单，不是索引外部文件。加/删/改名一条门禁必须**同批**改齐四处：

1. `$scriptList` 数组（84 条）；
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
pwsh -NoProfile -File tests/scripts/_run-step3-gates.ps1   # 整轮 43~52 分钟
```

- 宣布"全绿"必须绑定被测产物：exe/DLL 指纹前 16 与当轮日志同段；`dotnet test` 打在 Exe 工程上只还原不跑还返回 0。
- 起轮之后不许再动 `src/` 与运行器——整轮读数作废。计时类红要用旧二进制做对照臂，别拿单次 LoadPercentage 当"机器空闲"。
- 本机 ffmpeg 被沙箱拦 `%TEMP%` 写会批量 `exit -13` 假红：先重定向 `TMP/TEMP/TMPDIR` 进工作区再定性。
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

- **执行面迁移**：7 条在册门禁仍执行单体 `UiTestHost.exe`（`verify-ui-host`、`verify-ui-param-matrix`、
  `verify-ui-param-defects`、`verify-ui-strategy-map`、`verify-cli-strict`、`verify-png-structure`、
  `verify-ui-split-union`——最后这条同时跑单体与 5 个拆分件做并集）；`gainmap-*` 拆分件只被
  `verify-gainmap-host` 按表调用。并集/逐字节全等已证明拆分件等价（双向差集 0），
  但把执行体切过去要同时动这 7 条的调用点，属独立一批。
- 本机专用脚本 `_guishot.ps1` 留在仓库根（已 gitignore），**不要**挪进 `tests/scripts`：
  归属探针扫的是该目录下所有 `.ps1`（非递归、不看 git），进去会被算成"清单外未登记脚本"。
- `docs/TESTING.md` 正文里 38 处旧路径（`tests/ServiceProbe` 等）**按原样保留**，顶部加了读法说明；
  逐条改写会连带改动同段的历史读数，属独立一批。
- **清单外脚本**：`_probe-test-group-coverage` ⑪-d 长期 WARN 8 支"有判据却不进整轮"（`run-color-tests`、
  `run-quality-tests`、`run-thread-tests`、`verify-color-matrix-full`、`verify-oracle-matrix`、`verify-release-package` 等），
  判据是"接进清单，或按先证明再逐支删"，现状只警告不判红。
- **分层**：`Services` 对 UI 零依赖，唯一的代码级倒灌是 `IpcService.cs` 认 `MainWindow` 类型（13 处）；
  抽 `FfmpegGui.Core` 会改出货件程序集构成与指纹，属独立决策。
- **产物区残留（2026-10-22 清）**：`tests/output` 19.73 GB/1357 项 → **16 GB/215 项**。
  拆掉 3 个 worktree（存证见 §5）、零引用条目 1135 项 2.0 GB、>7 天轮次目录 4156 项 8.17 GB、
  宿主泄漏件 47 项 2.03 GB。留下的都在册文本里点名过。
  已知小怪：`tests/output/review/`（456 KB 日志与 diff）因为名字是常用词而被引用判定永久保护，没删。
- **宿主 TEMP 站点无源级锁**：本轮只修了"写了又从不删"的 4 处（`cap.dng`×2 在单体与镜像件、`gm_manual_*.rgba`×2）。
  `ServiceProbe` 的 `tooldetect_<guid>` / `cmsbench` / `cmsx` 三个固定名 scratch 仍在（不乘份数，尺寸小），
  要判红"宿主向 `%TEMP%` 写产物"得新增一条文本级探针——属独立一批。
- 视图扩展名 `.xaml`/`.axaml` 并存（迁移化石），`_probe-i18n-scan` 按"根目录下的 .axaml 窗口"点名，改名要同步它。
- **ACR 路线只覆盖 DNG（2026-10-24）**：`.dng` 走 `dngtool -rdp`（Adobe DNG SDK 的 `dng_render`，
  OpcodeList1/2/3 全施加），其余 RAW 仍走 LibRaw（**SDK 只读 DNG**，读不了 ARW/CR2/NEF…）。
  渲染空间按下游目标选（bt709 出口 ⇒ `srgb`，其余 ⇒ `rec2020`），`ColorSourceDeclPrimaries` 与它同源。
  **LibRaw 臂的中间件自 2026-10-26 起固定恒等 gamm（真线性 16-bit）**：LibRaw 默认 `gamm={0.45,4.5}`，
  `write_ppm_tiff` 会套 dcraw 曲线，与产品声明的 `linear` 冲突（二次 gamma，实测 ARW→PNG 中位 0.41 vs
  参考 0.32）；修法在 `dngtool.cpp` 解码臂，判据 = 第 79 条 ⑫（同一 DNG 的 `-d`/`-rd` 中位比 0.35~1.5）。
  已知边界：enhanced-image IFD 的 DNG 直接点名拒绝；DNG 输入上 `-H/-q/-W/-threads` 不生效（日志点名）；
  耗时约为 LibRaw 的 2.2×（24MP 5.1 s vs 2.3 s）。要"全量"须先 LibRaw 解包成 CFA DNG 再 SDK 渲染（另立一批）。
  门禁 = 第 79 条 `verify-acr-route.ps1`。
- **AVIF 的静态出口写不进 ICC（2026-10-26 实测）**：ffmpeg 的 AVIF muxer 不写 ICC，exiftool 后置
  也报「0 image files updated」而**退出码仍是 0**（672 B ProPhoto ICC：产物 `avifdec --info` 报
  `ICC Profile: Absent`，旧日志照打「ICC Profile 已恢复」）。`avifenc --icc` 能写，但它只用于
  GIF→AVIF 动画两步法 ⇒ `PlanPolicy.ExitWritesIcc=false`（由 `ColorIntentFactory` 按格式置位）：
  - 源空间**可 CICP 命名** ⇒ 零改动携带、以 nclx 描述（`AttachIcc=false`）；
  - 不可命名（ProPhoto/Adobe/自定义）⇒ S2 携带**契约拒绝**（exit 91，零产物）；S1 推荐落
    「最宽可命名目标 BT.2020/SDR + `GamutClipped` + `NoIccCarrier` 播报」。
  判据 = `verify-prophoto-faithful.ps1` §6 的 ⑥a–⑥f。
