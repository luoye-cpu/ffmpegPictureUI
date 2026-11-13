# ffmpegPictureUI 项目规范（本仓）

跨项目的协作规矩在全局 `~/.qoder-cn/AGENTS.md`；这里只写本仓特有的构成、归置契约与验收口径。
定位：Avalonia 桌面图片/动图转换器（`FfmpegGui`，WinExe + headless CLI），封装 ffmpeg 与外部编码器，
内置纯托管色彩管线。三层构成：**UI 层**（视图 + `MainWindow.xaml.cs`）· **管线层**（`Services/`）·
**色彩引擎层**（`Services/ColorMapping/`，对 UI 零依赖）。

---

## 1. 目录契约（唯一归置规则）

⚠ **工作区边界（绝对，任何理由不构成绕过）**：一切写盘（建文件/建目录、下载、解压、生成临时件、缓存）
目标必须在**本工作区** `C:\PLAN\ffmpegPictureUI\` 内。**禁止**在工作区外创建任何文件或目录——包括 `%TEMP%`、
`C:\Temp`、`C:\PLAN\` 下的同级目录、用户主目录新建目录。
下文凡出现「仓库外 / 仓外」作**要求**的条款（对照臂 worktree、历史提交构建、冻结副本）一律**降为待批**：
先单独申请、写清路径与清理方式、得到明确点头才可动手；**不得推定授权，不得沿用上一批的点头**。
外部素材（第三方样本、语料）默认落 `tests/output/_tmp/<批次>/corpus/`；判据进 `tests/scripts`（平铺登记），产物进 `tests/output`。
已违规 ⇒ 立刻停、清理自造仓外产物、在回复里如实点名路径与文件（`Get-Item` 的 FullName / 清理后 `Test-Path` = False）。

| 路径 | 放什么 | 不许放什么 |
|---|---|---|
| 仓库根 | 法律/说明/规范类 `.md`（`LICENSE` `NOTICE` `THIRD-PARTY-NOTICES.md` `README*.md` `CHANGELOG*.md` `PACKAGING_SPEC.md` `AGENTS.md`）、`ffmpegPictureUI.sln`、`global.json`、`pack.ps1`，以及 `src/ tests/ tools/ docs/ licenses/ publish/` | 任何脚本落盘产物、一次性调试残留、IDE 日志 |
| `src/FfmpegGui/` | 产品源码：`Controls/` `Models/` `Resources/` `Services/` `Services/ColorMapping/` | 测试宿主、探针 |
| `tests/projects/<工程名>/` | **13 个测试工程**（11 个 Exe 宿主 + `Tests.Shared`/`UiHostShared` 两个 classlib）。测试 `.cs` 只住这里 | 手改生成物（见 §3）；散在 `tests/` 顶层 |
| `tests/scripts/` | 门禁与探针脚本，**必须平铺**：`verify-*` = 门禁，`_lib-*` = 被 dot-source 的公共库，`_probe-*`/`_check-*`/`_make-*`/`_mutate-*`/`_readback-*` = 探针与取证工具 | 子目录——归属探针用 `Get-ChildItem tests/scripts -Filter *.ps1`（非递归）统计清单外脚本，分子目录会当场把覆盖判定变成盲区 |
| `tests/data/` `tests/fixtures/` | 手工造的小体量夹具（含其生成 `.py`），由 `$PSScriptRoot`/`RepoRoot` 推路径 | 绝对路径 `C:\PLAN\...`（唯一已知例外：`_probe-gpu-stage3.ps1` 里那串 `k.cl`） |
| `tests/output/` | **一切产物**：转换结果、日志、临时工作目录、scratch。整树 gitignore。**每个父目录下一轮一份 GUID/版本目录**（防上一轮残留让本轮空跑假绿）。清理命令：`py tests/scripts/_sweep-output.py`（默认 dry-run）与 `py tests/scripts/_sweep-output.py --runs --max-age-days 7 --apply`（按龄清轮次目录；在册文本点名的名字受保护，红轮证据在保留期内不动） | 判据文件、被跟踪的脚本、第二份测试源码树 |
| `docs/` | 设计与账本，见 §6 | 与代码冲突时以代码为准 |
| `tools/src/` | vendored 上游源码与自建小工具（`pngcicp`/`dngjxlprobe`/`dngtool` 进 sln） | 本地构建依赖（仅 10 个文件被跟踪） |
| `tools/std/` | ITU 标准原文（docx/txt），色彩常数取数的一手依据 | 派生结论 |
| `publish/` | 打包输出与随包工具链，整树 gitignore。**只留最新一档** | 手工编辑、旧版本包 |

新文件的归置问题一律先回答“它是**判据**还是**产物**”：判据进 `tests/scripts`（平铺）并登记，产物进 `tests/output`。

⚠ **出包只留最新一档：旧版本的包直接删掉。** 出新包即删 `publish/FFmpegPictureUI-v<旧版>-x64*.7z`
**连同** `publish/build/FFmpegPictureUI-v<旧版>-x64*` 解包目录；`publish/PLAN`（随包工具链）与
`publish/rescued-*`（存证 bundle/diff）不在删除范围。理由不是省盘：旧包在场，“这条验包/像素读数打在哪个包上”
就可能答错。**要留证据就记日志与哈希（`ProductVersion`、包内 exe `sha256[:16]`），不留包。**
删前先扫 reparse point（`os.walk(followlinks=False)` + `FILE_ATTRIBUTE_REPARSE_POINT`）：
`publish/build/*-full/` 里若出现指向 `publish/PLAN` 的 junction，递归删会连随包工具链一起没。

⚠ **零引用 ≠ 无用**：`_sweep-output.py --unreferenced` 删的是“名字在在册文本里一次都没出现过”的条目，
而**手工跑出来的活线产物正属此类**（2026-10-22 就这样误删过不可复原的 `jxr-probe` 与 `jxr_engine_probe`）。
用这一档之前先读一遍待删清单；`--runs`（按年龄）那一档不受此限，因为轮次目录本就由在册脚本重建。

## 2. 门禁登记：清单本体就是运行器

`tests/scripts/_run-step3-gates.ps1` 是清单，不是索引外部文件。**当前 104 条**
（`$scriptList` 104 个唯一项、`$expectedScriptCount = 104`；2026-11-13 复核：104 个文件全在，
磁盘上 78 个 `verify-*.ps1` 全部在册）。加/删/改名一条门禁必须**同批**改齐四处：

1. `$scriptList` 数组；
2. `$expectedScriptCount` 常量（同文件）；
3. runner 里复述条数的**散文陈述**——`Test-RunnerShape` 第 ⑰ 针按三种形态各扫一遍并要求与常量相等；
4. 新的 ServiceProbe mode 还要进 `tests/projects/Tests.Shared/TestGroups.cs` 的组表，
   由 `_probe-test-group-coverage.ps1` 认领（档① 运行器起 / 档② 在册脚本按参数调用 / 档③④ 算未覆盖并转红）。

裸跑单个 `verify-*.ps1` 会绕开仓库锁、单步超时、STALE 新鲜度闸与产物指纹核对——只适合调试，不算验收。

## 3. 测试层硬规矩

- **⑥ 不许跑全量（2026-11-13 立规；运行器层面拒跑）**：`_run-step3-gates.ps1` **没有选择器就直接 `exit 2`**，连锁都不拿。
  理由不是“省时间”：全量 104 条本机 ≈ **2 h**，读数不可归因、会掩盖局部回归（改 A 因 B 早已红而 rc=1，
  “这次改动有没有引入回归”无从判断）。修一个 bug 的验证只需 1~3 条针对性门禁（秒级~数分钟）。
  `-FullBaseline` 是唯一全量入口，**只允许在用户明确要求时使用**；Agent 不得自行决定。
  该闸由形态自检 **第 ⑳ 针**盯着（标记 / 开关 / 条件片段 / 选择器变量四形），删掉或放宽即红。
  ⚠ `-Domain` 的域 → 门禁映射取自账本 `entries[].carriers[].gate`，**不是**手写表 ⇒ 新增门禁只要在账本里挂 carrier 就自动进域。
- **生成物不手改**：`tests/projects/ui-*/<name>Group.cs` 由 `_gen-ui-split.ps1` 从 `UiTestHost/Program.cs`
  按行区间切片生成（断言字符串必须逐字保留）。改断言改单体，然后重跑生成器；
  `verify-ui-split-reproducible`（重跑两次 + 逐字节对撞）与 `verify-ui-split-union`（并集双向差集）会红。
- **镜像不许再生**：`verify-probe-mirror-parity` 是存在锁（`tests/**/Modes/*.cs`、被删目录、git 索引三处查），
  并把 `ServiceProbe` 每个派发 mode 的断言条数**逐名钉死**、要求“执行面每个 `Probe*` 只许一份定义”。
  想加第二份拷贝 = 当场红；合并掉一处也要回来改登记表。
- **承重探针必须入库**：被在册脚本调用的 `_*.py`/`_*.ps1` 必须是 tracked 文件。
- **产物只有一份，且不落 `%TEMP%`**：测试宿主的产物路径一律取 `OutputDir`（`tests/output/<组名>`）；
  **临时件根由宿主自己钉死在 `tests/output/_tmp`**（`Harness.PinScratchRoot()` 设 `TMP/TEMP/TMPDIR`，
  四个引导点：`Harness.InitExternalTools` / `ServiceProbe.InitExternalTools` / `UiHost.Bootstrap` / `GainMapTestHost.Main`），
  子进程（ffmpeg/dngtool/exiftool）继承之。**不许再现场发明 TMP 根**（历史反例：`gate_tmp` `gtmp` `roundtmp`
  `_ramdisk` `faketemp` 等 12 个根，把同一份 187.6 MB 的废弃 `cap.dng` 乘成 12 份）。
  `Path.GetTempPath()` 只许放“用完即删”的中间件，**写完必须 `finally { File.Delete(...) }`**。
  第 94 条 `_probe-host-temp-sites.ps1` 冻结站点表（**18 文件 / 40 站点**；2026-11-13 全量轮 40==40）。
- **一轮一目录是设计，不是缺陷**：GUID/版本 run-dir 防“上一轮残留让本轮空跑假绿”；沉积用年龄清扫，
  不许用“绿才删”当唯一出口（红轮永不删）。
- **冻结副本不进产物区**：`tests/output/**/` 里出现第二份 `tests/` 源码树即违规；要对着历史提交构建，
  按 §1 先申请仓外 worktree。

## 4. 验收配方

```powershell
$env:LC_ALL=$null
dotnet build ffmpegPictureUI.sln -c Release          # 13 个测试工程；沙箱内多节点 MSB1025 ⇒ 用 -m:1
pwsh -NoProfile -File tests/scripts/_run-step3-gates.ps1 -ListUnits
pwsh -NoProfile -File tests/scripts/_run-step3-gates.ps1 -Domain ui
pwsh -NoProfile -File tests/scripts/_run-step3-gates.ps1 -Scripts verify-ui-host.ps1,verify-package-gui-starts.ps1
```

- **无选择器 = `exit 2` 拒跑**；`-FullBaseline` 只在用户明确要求时用（104 条 ≈ 2 h）。
- 宣布“全绿”必须绑定被测产物：exe/DLL 指纹前 16 与当轮日志同段。**比对用 `FfmpegGui.dll` / `ServiceProbe.dll`**——
  `*.exe` 是 apphost（IL 在 dll 里），两棵树编出来的 `.exe` 哈希相同，拿它判定会误以为“跑的还是同一份”。
- 起轮之后不许再动 `src/` 与运行器；计时类红要用旧二进制做对照臂（worktree 仓外，先申请）。
- 裸跑单条门禁调试要自己把 `TMP/TEMP/TMPDIR` 钉到 `tests/output/_tmp/round-tmp`（子进程继承），
  否则 `verify-color-wiring` 这类会批量假红。
- `dotnet test` 打在 Exe 工程上只还原不跑还返回 0；改动只跑所在链路，一块一个测试，分开跑分开报。

## 5. 高危区（动之前先读）

- `tests/projects/GainMapTestHost/PLAN` 及其 `bin/**` 副本是 **junction**，指向 `publish/PLAN`。
  `os.walk` 会钻进去；拆链接用 `os.rmdir`，别 `rm -rf`（会连目标一起删）。
- 产物区**不许**挂 git worktree。清 `tests/output` 前先 `git worktree list` 确认——
  历史教训是 `git worktree remove` 不钻 junction。
- MSYS bash 会静默改坏生成的 Windows 脚本（`/p:`→`p:`、`nul`→`/dev/null`、`\1` 被转义）⇒
  批量改写用 `py`，MSBuild 传参配 `MSYS2_ARG_CONV_EXCL='*'`。
- 编辑工具会把整个文件换成 CRLF 而 `--numstat` 不报 ⇒ 判据是与 HEAD blob 做字节层 EOL 对撞。
  本仓 `AGENTS.md` / `docs/INDEX.md` 是 LF；`.ps1` 多为 CRLF；改前先量。
- `git` 的 `quotepath` 会把含 CJK 的路径写成八进制转义 ⇒ 用文件名匹配的门禁要先确认命中，别用裸串。
- **改判据后还原源码必须 `touch`**：`Copy-Item`/`cp -p` 会带回旧 mtime，MSBuild 判 up-to-date、拿变异体跑出假红。

## 6. 文档口径

- **目录地图在 `docs/INDEX.md`**：承重（位置固定）/ 分批审查（`review-*`，被门禁按名点名）/ 归档（`archive/`）。
  判断一份文档能不能挪：数**仓外**（`src` `tests/scripts` `pack.ps1` README CHANGELOG）对它的命中数，
  零命中才可进 `archive/`；非零就要同批改引用点。**数命中要用去掉尾部日期的前缀名**——
  代码注释里常写短名（`RAW2TIF_QA_DEFECT §7.5`），按全名数会漏并把引用变成悬空。
- **承重文档**（被代码/脚本/打包引用，位置与章节不可随意动）：`docs/TESTING.md`（引用最多）、
  `docs/HANDOVER.md`、`COLOR_MATRIX_FIX_PLAN_2026-09-24`、`JPEGLI_GAINMAP_PANEL_PLAN_2026-10-09`、
  `PIPELINE_TEST_AUDIT_2026-10-01`、`IMPLEMENT_PLAN_2026-10-11`（现行账本）、`FIX_PLAN_2026-10-14`、
  `GAINMAP_ROUTES_AND_PATCH_PLAN`、`TEST_SPLIT_TEMPLATE`。
- **带日期的账本（`docs/review-*/`、`docs/archive/`、旧 `HANDOVER_*`、`CHANGELOG*`）是时间点记录**：
  布局迁移、改名这类变更**不回改**其中的旧路径，以免把历史读数改成假的当前态。要更正就新写一条更正条目。
- 文档里每个数字都是**读数**，必须能指到某条命令的日志；没跑过的批次不写进结论。
- **`docs/TESTING.md` 的模块索引是生成物**：`verify-module-registry.ps1 -EmitIndex` 渲染
  `<!-- module-index:begin/end -->` 段；默认跑 = 逐字节对撞并报差异。改索引内容改账本，不手改生成段。

## 7. 现行口径与未了结

**测试/门禁现值（2026-11-13 复核）**：门禁 **104** 条，13 个测试工程，模块账本 **67** 条。
UI 三把尺：`verify-ui-host` 五件 **84+59+89+169+72 = 473**；`verify-ui-param-matrix` J **126**；
`verify-ui-split-union` **473**（与 `_module-registry.json locks` 同源）。宿主 TEMP 站点 **18 文件 / 40 站点**。

**产品口径（会直接影响判据，别按旧账读）**：

- **pngopt 默认档 = `max`**（`FfmpegOptions.cs`、GUI 组合框 `SelectedIndex=2`、`QueueProcessor` 直传；CLI 不传参也落 max）。
  `verify-color-wiring` 的 q60/q100 体积对比必须显式 `--png-optimizer off`，否则两档被无损重压成同体积。
  旧预设回放不能把档位带回 `off`（`FfmpegOptions.cs` 仅 `!= null` 覆盖 ⇒ null 不覆盖、停在 max）。
- **色彩范围**：WebP 出口恒 `tv`（显式 `--color-range pc` 也压成 tv 并出声；容器写不进范围标记）。
  JPEG auto 档压回 `pc`（`yuvj` 满量程 = JPEG 事实标准）；显式 `--color-range tv` 才走 `limited + -strict unofficial`，
  且 `EmitWarn` 点名 libjpeg/WIC 会发灰。AVIF 静态出口**写不进 ICC**（`PlanPolicy.ExitWritesIcc=false`）：
  可 CICP 命名 ⇒ 零改动携带；不可命名 ⇒ S2 携带契约拒绝（exit 91，零产物）。
- **显式输出矩阵**：`ResolveOutMatrixCapability` 是唯一真值。显式 H.273 与推导值跨族 ⇒ Rejected exit 91 + 模板 4；
  601 族 `{bt470bg↔smpte170m}` 互通；推导 null 放行。GUI 下拉只列非 Rejected 的档位。
- **ACR 路线只覆盖 DNG**：`.dng` 走 `dngtool -rdp`（DNG SDK 全 Opcode），其余 RAW 走 LibRaw；
  LibRaw 臂固定恒等 gamm（真线性 16-bit）。增强图 IFD 的 DNG 直接拒绝；DNG 输入上 `-H/-q/-W/-threads` 不生效；
  耗时约 LibRaw 的 2.2×。
- **托盘**：Windows 用原生 `Shell_NotifyIcon`，`NIM_ADD` 返回值 = `_trayAvailable` 唯一真源（Avalonia `TrayIcon` 自报 `IsVisible` 不可信）。
  提权/受限会话加图标一律 `err=5`；`verify-package-gui-starts` ⑥ 只在「提权 且 `tray.log` 明写 err=5」时 **SKIP**，
  其余（含非提权 err=5）照旧 FAIL。
- **工具检测**：GUI ctor 与 headless 同走 `PlatformServices.EnsureFfmpegDirectoryResolved()` 单点；
  `AppSettings.FfmpegDirectoryAuto` 与显式值分离（失效时 Auto 持久化、显式值只会话内改用不覆写），
  `Save()` 的手写 clone 表必须同批加行。来源标签五档（PLAN/manual/app-adjacent/extended-search/PATH）进 GUI 与 headless 报告。
  `FFMPEGGUI_FFMPEG_DIR` **分隔符敏感**：runner 已改 Join-Path，产品侧归一化仍是未了结。
- **ISO-BMFF**：D5 截断预检 fail-closed，**>256 MB 一律拒绝**；`Result.TruncationIndeterminate` 与“文件不完整（截断）”文案尚未分家。
- **UI 执行面**：5 条 UI 门禁已切拆分件；`verify-ui-split-union` 已改全组跑（不传 mode）。
- **C4 超时表**：单一真值 = `_module-registry.json` 的 `locks."script-timeouts".table`
  （`verify-color-strategy` / `verify-png-optimizer` 各 1200 s；表外全局默认 900；表只许抬）。装载失败必须 `exit 2` 拒跑。

**未了结（按优先级）**：

- `verify-package-gui-starts` ⑥ 的 **PASS 形态尚未本地验证**：本机直接跑与 `schtasks` 都是 High，需普通权限会话确认。
- **出包版本口径冲突**：`HANDOVER-NET11-GA-PACKAGING-2026-11-11.md` 曾裁定 `1.6.0-rc.1`，beta13 实际按 beta 出；
  下一档前须重新拍板。
- pngopt 专用通路缺 `exitCode==0` 守卫（主分支 `QueueProcessor.cs` 有、专用通路没有）。
- `FFMPEGGUI_FFMPEG_DIR` / `FFMPEGGUI_PLAN_DIR` 的正斜杠产品侧归一化未做（runner 侧已 Join-Path）。
- 色彩范围口径的实测只覆盖 WebP 与 jpg；AVIF/mkv/mp4 为容器机制推断，jpeg 别名未单测。
- `bench` 四条计时断言无停顿/成对采样保护（阈值按维护者口径不动，负载高时可能假红）。
- `verify-color-engine-xcheck` 的库侧 `ColorGainKit` 已修（grid 候选/载荷 + 21 字段对撞 + 3 条正控）；
  跨仓改动仍须在 `C:\PLAN\ColorGainKit` 维护。

## 8. Git 历史纪律（2026-11-02 立规）

**历史本体不回写**：共享史已推送，且全仓账本/日志按提交哈希绑定读数——改写历史 = 在册读数全部变悬空指针。以下规矩只管未来：

1. **tag 一经推送即冻结，不许重指**：重出（同内容重新打包）一律**升版本号**、新打 annotated tag。
2. **版本位只进不退**：版本号唯一来源 = `FfmpegGui.csproj` 的 `<Version>`；`pack.ps1` 默认读它，显式 `-Version` 必须一致。
3. **release 提交语义分离**：release 提交只装「版本位 + CHANGELOG + tag 指向」；修复独立成 fix 提交。
4. **动版本位必同批记 CHANGELOG（zh/en 同批）**。
5. **批次提交自封顶**：一次提交主题数尽量 ≤3；确需批次收口时 body 必须自带分节清单、每主题在账本有独立条目。
6. **纠偏靠更正条目，不靠撤回提交**：已推送内容有错就按 §6 新写更正条目，不 revert、不改史。

**审计基线（2026-11-13，只记录不改写）**：`214` 提交 / `3` merge / `35` tag（远端 34，`1.6.0BETA12` 仅本地）。
版本位与 tag 不一致 2 处：`1.4.5` 锚 `1.4.4`、`1.5.0BETA3` 锚 `1.5.0-beta2`；`1.0.0` 锚初始提交（无版本位）。
1.6.0 系里 tag 锚非 release 提交 3 处：`BETA5`/`BETA6`/`BETA10`（均在 §8 立规前）；`BETA11` 无 tag 但账本有出包记录；
`1.6.0` release 提交改了 123 文件（立规前），`BETA12`/`BETA13` 的 release 提交各 3 文件合规。
历史快照见 `docs/review-2026-11-13/AGENTS_HISTORY_2026-11-13.md`；工作区审计见 `docs/review-2026-11-13/WORKSPACE_AUDIT_2026-11-13.md`。
