# Agent 任务拆分与协作约定（2026-09-15）

> 用途：把当前工作树里**可独立交付**的部分拆成包，交给另一个 agent 并行做。
> **先读 `docs/HANDOVER.md`**（尤其顶部「提交约定」与 §C 环境坑），再读本文。
> 本文件由第二轮会话的 agent 维护；各包完成后请**告知维护者统一登记**，不要各自改 `HANDOVER.md`/`TESTING.md`。

---

## 0. 全局约束（三个包都适用）

1. **不要提交**。用户明确要求：未经实机测试不得提交、每个版本一个提交、未明确要求时不主动提交。
2. **不要改 `src/FfmpegGui/FfmpegGui.csproj`**。本环境改它会让 `dotnet build --no-restore` 也失败
   （`NETSDK1060 … Value cannot be null (path1)`），且**把内容改回来也救不回来**（MSBuild 按 mtime 判定）。
   修法见 `HANDOVER §C.4`（`touch -d` 把 mtime 调早于还原产物）。要访问 internal 成员请用**反射**。
3. **不要在用户的实时桌面上弹窗、更不要做鼠标自动化**。本会话就是用户的实时桌面（截屏核实过）。
   GUI 相关验证由用户自己做。
4. **每条新断言/新锁都必须做变异验证**（改动源码让断言转红，再改回）。
   本会话在这一步抓到过 3 次「空断言 / 变异打错层」——见 `TESTING.md §6` 第 27、30、37 条。
5. 改任何**门禁断言**都属于「改门禁」：**必须同时补负控，不得为了绿而放宽**。
6. 文档/记忆由维护者统一写；你只需在完成时报告：改了哪些文件、跑了什么、结果数字。

---

## 包 1（✅ **2026-09-16 已完成**，测试侧，零生产风险）：门禁脚本 `& exe` → `Start-Process` 统一改造

> **完成度核验（维护者 2026-09-16）**：`grep -l '& \$' tests/scripts/*.ps1` ⇒ **仅剩 2 个**，
> 且两处均为**合法**用法（`_probe-ztf-locate.ps1:78` 的 `& $pred` 是调用 `[scriptblock]`；
> `run-pipeline-tests.ps1:35` 的 `& $script` 是调用子脚本）⇒ **28 个脚本的 exe 调用已全部改造完毕**。
> **效果验证**：`verify-color-caps.ps1` 由改造前的 **PASS=6 FAIL=6** 转为 **PASS=12 FAIL=0**，
> 且 12/0 与 `HANDOVER §B` 上一轮记录一致 ⇒ 交叉印证了「6/6 是宿主伪影」的定性。

### 为什么
门禁脚本用**调用运算符 `& <exe>`** 起外部程序（`& $probe` / `& $ff` / `& $et` / `& $fp`）。
`& exe` 在**部分宿主**（含本 agent 宿主）会**静默失败**：不报错、输出为空
⇒ 依赖它的断言全部**假红或空值**。本会话实测：
- `verify-gainmap-managed.ps1` 曾因此报 **13/18**，其中 **12 条是伪影**；
  把 `& exe` 改成 `Start-Process` 后立刻变成 **19/12**，再换掉 6 条陈旧断言后 **20/0**。
- `verify-prophoto-faithful.ps1` 报 `FAIL 无法定位纯托管生成的 ProPhoto ICC`（同一命令直接跑是**通过**的）；
- `verify-tiff-icc.ps1` 打出 `ICC=[]`（空值）。

**第三轮 A/B 硬证据（同机同时刻，`verify-color-caps.ps1` 的前置素材步骤）**：

| 写法 | p3.png | p3.icc |
|---|---|---|
| `& $ff` / `& $et`（脚本原写法） | **未生成**（0 B） | **0 B** |
| `Start-Process -Wait -PassThru` | **4125 B** | **584 B**（>200 B 阈值 ⇒ 断言本应 PASS） |

⇒ 该脚本 6 条 FAIL **全部是伪影**（584 B 与 `verify-colorfmt-matrix` 实测的 `584B` 一致，交叉印证）。

⚠⚠ **本包现在有了新的紧迫性**：`_run-step3-gates.ps1`（第三轮已修好运行器自身）用 `Start-Process`
起**子 pwsh 进程**来跑这些脚本，而 `& exe` 正是在这类受限宿主里静默失败 ⇒
**经运行器批量跑的脚本门禁结果全部不可信**。第三轮实测（**全是伪影，不是回归**）：
caps 6/6 · prophoto-faithful 0/1 · png3-interop 1/8 · tonemap 0/2 · decision-delivery 34/5 · gif-avif-framelist 16/1。
⇒ **本包完成前，「全量门禁」不能作为可用的验收手段**；各包验收请以**单脚本 + 能跑 `& exe` 的宿主**为准
（也**不要**拿经运行器跑出的数字去比 `HANDOVER §B` 上一轮的数字 —— 两边都"对"，差异全来自宿主）。

### 改造面（实测枚举，28 个脚本、约 90 处）
```
verify-color-caps.ps1 11   verify-prophoto-faithful.ps1 9   verify-decision-delivery.ps1 8
_probe-caps-e1-e2.ps1 8    verify-png3-interop.ps1 7        _probe-zimg-tagged.ps1 5
_probe-zimg-noop-check.ps1 4  _probe-p7d-bisect.ps1 3  _probe-png-cicp-production.ps1 3
probe-gainmap-tiff.ps1 3   run-quality-tests.ps1 3          verify-color-engine-xcheck.ps1 2
verify-color-strategy.ps1 2  verify-gif-avif-framelist.ps1 2  verify-tiff-icc.ps1 2
（其余各 1 处：_probe-gpu-stage2/3、_probe-sws-downshift、_probe-ztf-locate、final-verify、
 regtest-fix、run-color-tests、run-full-matrix-test、verify-color-tonemap、verify-color-wiring、
 verify-colorfmt-matrix、verify-remaining）
```

### 起手位置（**照抄现成范式**）
`tests/scripts/verify-gainmap-managed.ps1` 里已有一个改好的 `Exec` 助手（本会话加的），直接复用：
```powershell
# 取子进程 stdout 一律走 Start-Process，不要用 `& exe`
function Exec([string]$file, [string]$argStr){
  $o = "$out/_exec.out"; $e = "$out/_exec.err"
  Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
  return ([System.IO.File]::ReadAllText($o) + "`n" + [System.IO.File]::ReadAllText($e))
}
```
调用点改法（注意用**单个参数字符串**，见 `HANDOVER §C.5` 的坑）：
```powershell
# 改前：$x = (& $et -json -G1 -a "-GainMapMax" $f 2>$null) | ConvertFrom-Json
# 改后：$x = Exec $et "-json -G1 -a `"-GainMapMax`" `"$f`"" | ConvertFrom-Json
# 改前：$ps = (& $ff -i "$a" -i "$b" -lavfi psnr -f null - 2>&1 | Out-String)
# 改后：$ps = Exec $ff "-i `"$a`" -i `"$b`" -lavfi psnr -f null -"
```

### 验收标准
1. 每个改过的脚本**在受限宿主里能跑出有效结果**（不再是空值/假红）：
   判据 = 与「直接用同样命令跑一次」的结果一致；
2. **只改"怎么起进程"，不要动任何断言/阈值**（否则会污染门禁语义）；
3. 报告格式：`脚本名 · 改前输出特征（如 ICC=[] / 假红条数）· 改后结果 · 是否与直跑一致`。

### 边界
- **不要改** `verify-gainmap-managed.ps1`（维护者本轮刚改完）。
- 不确定某个脚本"改了会不会动到断言"时，先只改一处、跑一次对比，再继续。

---

## 包 2：CLI 侧收敛（`--format` 校验 + 隐私默认值单一真值）

### 2a. `--format` 未校验 ⇒ 产物扩展名可能非法（既有 bug，实测）
```
FfmpegGui.exe --headless -i in.jpg --format jpegli --output OUT
→ 产物路径被拼成 …\src.jpegli
→ ffmpeg: Unable to choose an output format for '…src.jpegli'；退出码 -22；**无任何产物**
```
依据：`jpegli` 在 `FfmpegCommandBuilder.cs:1333` 与 `FfmpegOptions.cs:389` 里被当作合法格式（有质量映射），
但它**不在** `AppSettings.AllImageFormats`（app 的格式词表）里；而输出扩展名是按 `"." + format` 拼的
（`CliParser.BuildOutputPath:541`）。⇒ 它是**编码器后端**，不是格式。

**修法方向（需先定语义，二者选一）**：
- (i) 把 `--format jpegli` 规范化为 `jpg`（用户意图显然是"用 jpegli 编 JPEG"）；
- (ii) 在 CLI 里**校验格式 token**，未知值给出明确报错并列出可用值（可用性诚实原则）。
  ⚠ **风险：误拒有效格式** ⇒ 必须先枚举**当前所有可用的 format token**（从
  `FfmpegCommandBuilder` 的 `fmt` switch + 各专用通路 gif/apng/avif/jxl/jxr/dng/raw + GUI 词表交叉核对），
  并**逐个实测**，确认校验后没有原本能用的 token 被拒。

### 2b. 隐私默认值有两处独立真值 ⇒ 只改一处不生效（实测）
- `src/FfmpegGui/Models/FfmpegOptions.cs:297` → `StripExifGps { get; set; } = true`
- `src/FfmpegGui/CliParser.cs:48` → `Options.StripGps { get; set; } = true`，
  再由 `CliParser.cs:409` 覆盖模型默认值。
**证据**：变异测试时只改 `FfmpegOptions` 那处 ⇒ `verify-metadata-privacy.ps1` **依然全绿**；
改 `CliParser` 那处才转红。（`TESTING.md §6` 第 32 条）
**修法方向**：把 CLI 侧改成三态（`bool?`）+ 未显式指定时不覆盖模型默认值；
注意 `--strip-gps` / `--no-strip-gps` / `--preserve-metadata`（`CliParser:163-164` 会置 false）三者的交互。

### 验收标准
- 2a：`--format` 全部合法 token 实测仍可用；非法 token 要么被规范化、要么明确报错（不再产出坏扩展名）；
- 2b：`tests/scripts/verify-metadata-privacy.ps1` **3/0**（默认剥 GPS / 描述性元数据保留 / 负控 `--no-strip-gps` 保留 GPS）；
- 两者都要有**变异验证**（改回旧写法应当转红）。

### 边界（文件所有权）
只动 `src/FfmpegGui/CliParser.cs` 与 `src/FfmpegGui/Models/FfmpegOptions.cs`（必要时 `Models/PresetData.cs`）。
**不要动** `Services/QueueProcessor.cs` / `Services/ExifToolService.cs`。

---

## 包 3（可选）：P2-6 终态判定集中化（中改造，需谨慎）

### 问题
终态/成功/失败判定依赖**中文魔法字符串**，散落在多处且**各处的组合语义都略有不同**（实测约 24 个判定点）：
- `Models/QueueItem.cs:173` `HasError` → `Status.StartsWith("失败")`
- `Models/QueueItem.cs:176` `IsCompleted` → `StartsWith("已完成") && ExitCode == 0`（注意：这是"成功"，不是"终态"）
- `Services/DefaultQueueOrchestrator.cs:162-168` `IsCompletedOrTerminal` → `已完成/失败/已停止/已删除`（**只看状态**）
- `Services/ConsoleQueueObserver.cs:188` → `Status.Contains("失败") || Status.Contains("错误")`
  —— 全仓唯一用 `Contains` 的地方（别处都是 `StartsWith`），且 `"错误"` 不是任何状态前缀 ⇒ **需定性**（潜在误判）
- `Services/QueueProcessor.cs:114` / `:644` 各有**自定义组合**（如 `HasError || Status=="已停止" || ExitCode!=0`）
- `MainWindow.xaml.cs` / `ProgressWindow.xaml.cs` 里多处 `Status == "处理中"/"待处理"/"已删除"`

### 目标与约束
- 把判定收敛到 `QueueItem` 上的**单一谓词集合**（如 `IsTerminal` / `IsSuccess` / `IsFailure` / `IsStopped` / `IsDeleted`）；
- **行为必须等价**，或对每处语义差异**显式说明改变了什么**（不要"顺手统一"）；
- `QueueProcessor.cs:114/644` 那两处是自定义组合，**先读懂再动**（`:644` 是 P1-4 的"确证失败才丢弃半成品"判据）。

### ⚠ 独占声明
本包**必须独占 `src/FfmpegGui/Services/QueueProcessor.cs`**（维护者本轮不再动它）。
若维护者需要再动该文件，会先在本文件顶部登记。

### 验收标准
- `dotnet build` 0 警告 0 错误；
- 全量门禁与探针与改前一致（`ServiceProbe selftest/procstreams/runner/settings/encoding/ipc` +
  `verify-decision-delivery` / `verify-gif-avif-framelist` / `verify-format-regression`）；
- 若统一了 `ConsoleQueueObserver:188` 的 `Contains`，需给出"旧写法会误判/不会误判"的**实测结论**。

---

## 文件所有权矩阵（防并发冲突）

| 文件/目录 | 归属 |
|---|---|
| `src/FfmpegGui/Services/QueueProcessor.cs` | 维护者 / 包 3（**同一时间只能一方动**） |
| `src/FfmpegGui/Services/ExifToolService.cs`、`AppJsonContext.cs`、`Services/IpcService.cs`、`FormatCapabilitiesService.cs` | 维护者 |
| `tests/scripts/verify-gainmap-managed.ps1` | 维护者（本轮刚改完） |
| `tests/scripts/` 其余 27 个含 `& exe` 的脚本 | **包 1** |
| `src/FfmpegGui/CliParser.cs`、`Models/FfmpegOptions.cs`、`Models/PresetData.cs` | **包 2** |
| `Models/QueueItem.cs`、`Services/DefaultQueueOrchestrator.cs`、`Services/ConsoleQueueObserver.cs`、`ProgressWindow.xaml.cs`、`Program.cs`、`MainWindow.xaml.cs` | **包 3** |
| `docs/HANDOVER.md`、`docs/TESTING.md`、`.workbuddy-ai/memory/` | 维护者（各包完成后**报告**，由维护者统一登记） |

---

## 维护者保留（不拆分，原因）

| 项 | 原因 |
|---|---|
| P1-4 输出原子性（先写临时件、成功后原子替换，约 15 个落盘点） | 属"改变软件结构"，**需用户拍板** |
| S-P1-2 IPC 单实例接线（`TryConnectAsync` 全仓无调用点 ⇒ 第二次启动真双开） | 会改变启动行为，**需用户拍板**（IPC 往返现已验证可用，前提具备） |
| HLG 接 BT.2100 OOTF（`ColorStrategyPlanning.cs:80` 自注"尚未接入像素养分路径"） | 实质色彩管线改动，风险高，**需拍板** |
| exiftool `-stay_open` 常驻进程（可再省 ~850ms/项） | 架构级改动，**需拍板** |
| 把 ffprobe 色彩探测改成**进程内读取** | 本轮量化后的**最高杠杆**性能项（每项省 ~800ms），但它动的是**色彩决策的输入来源**，高风险，**需拍板**。依据见 `TESTING.md §6` 第 38 条 |
| 提交执行（`tests/output/_commit_plan.txt` 的 3–4 个提交） | 用户约定：实机测试通过前不提交 |
| GUI 鼠标交互实机测试 | **由用户执行**（不要在用户桌面上做鼠标自动化） |

---

## 环境坑速查（都是本会话实测踩出来的）

| 坑 | 表现 | 应对 |
|---|---|---|
| 沙箱缺 `ProgramData`/`APPDATA` | `dotnet restore` 报 `Value cannot be null (path1)` | `dotnet build --no-restore` |
| 改过 csproj | 连 `--no-restore` 都坏，且改回内容也不恢复 | 不要改 csproj；必要时 `touch -d` 调早 mtime（§C.4） |
| ✅ `pwsh` 不在 PATH | `Start-Process pwsh` 整批失败 | **第三轮已修**：`_run-step3-gates.ps1` 改用 `(Get-Process -Id $PID).Path` |
| **`& exe` 静默失败** | 输出为空 ⇒ 假红/空值 | 一律 `Start-Process`（包 1 就是在治这个） |
| 子进程输出看不到 | 工具里读不到 stdout | 让脚本自己落盘（`-Report` 或重定向）再读 |
| 固定工作目录串味 | 上一轮残留子目录删不掉 ⇒ 素材"生成了又消失" | 每次运行用 GUID 工作目录 |
| `Write-Host` 不可捕获 | `\| Out-File` 抓不到 | 用 `Write-Output` |
| 双引号里的弯引号 `“…”` | 字符串被静默截断 | 用 `「」` |
| ✅ 改了源码只建 Debug | 门禁优先用 Release 产物 ⇒ 跑到旧二进制（表现为假红或假「未执行」） | **第三轮已加硬闸**：跑前按**项目配对**比 mtime，陈旧就 `exit 2` |
| ⚠ **`Start-Process` 子进程的环境块可能缺 System32** | 子进程里**裸命令名**（`ping`/`where`）解析失败，而宿主里 `Get-Command` 却能找到 ⇒ 同一探针「直跑绿、经门禁脚本跑红」 | 脚本/探针里**一律用绝对路径**；生产代码 `FfmpegRunner` 本就用 `AppSettingsService.Current.FfmpegPath` |
| 断言的兜底路径 | 超时返回的合成响应也带 `req.Id` ⇒ `Id==req.Id` 恒真 | 断言要同时校验 `Success`/`Message`（§6 第 30 条） |

---

## 维护者本轮改动（2026-09-15 第三轮，各包请知悉）

改动集中在**门禁运行器**与**探针自身**，不触碰任何包的所有权文件。对协作的实际影响：

1. **`tests/scripts/_run-step3-gates.ps1`（维护者所有，不在包 1 的 27 个脚本清单内）** 三处修复：
   - 新增**二进制新鲜度硬闸**：跑前按项目配对比较 mtime（`src/FfmpegGui/**` ↔ `FfmpegGui.dll`、
     `tests/ServiceProbe/**` ↔ `ServiceProbe.exe`），陈旧就 `exit 2`。
     ⇒ **包 1/2/3 改完源码后必须先 `dotnet build tests/ServiceProbe/ServiceProbe.csproj -c Release`**，
     否则运行器会直接拒绝跑。这是有意为之：防止拿旧产物当基线。
   - 新增**跑后源码漂移复核**：跑期间若源码被改，会打
     `[WARN] 跑期间源码被改动 ⇒ 本次结论不对应当前工作区`。
     ⚠ 本仓库有并发编辑会话 ⇒ **看到这条告警时，你这次的数字不能引用**。
   - 子脚本宿主由硬编码 `"pwsh"` 改为 `(Get-Process -Id $PID).Path`。
     ⇒ 此前**经运行器批量执行的脚本门禁从未真正跑起来**（`Start-Process -FilePath "pwsh"` 在本机必然失败）；
     那些脚本若此前被**单独**跑过，数字仍可能有效 —— 但**不要**把「批量全绿」当成已发生的事实。
     各包**首次**经运行器跑出来的脚本数字才是可复现基线。
2. **`tests/ServiceProbe/Program.cs` 的 `runner` mode**：替身由裸命令名 `ping` 改为绝对路径
   `C:\Windows\System32\PING.EXE`（**断言语义一字未改**）。此前它经 `Start-Process` 跑必假红（fail=2），
   直接跑却绿（pass=2）—— 原因见上表「`Start-Process` 子进程的环境块」。
   ⇒ **包 3 的验收标准里含 `ServiceProbe runner`，现在两种跑法结果一致（均 2/0）**。
3. `ProbeRunnerGuard` 失败时会带上子进程输出（`Dump()`），便于区分「守卫误杀」与「子进程自己报错」。

**修正后的探针基线（Release 产物新鲜，2026-09-15）**：16 mode 合计 **353 pass / 0 fail**
（contract 95 · wire 26 · selftest 36 · iccname 10 · plan 39 · curve 39 · matrix 15 · ootf 11 ·
bt2446 18 · decision 4 · engine 32 · runner 2 · settings 7 · procstreams 3 · encoding 6 · ipc 10）。

> 各包完成时请**报告**（改了哪些文件、跑了什么、结果数字），由维护者统一登记到 `HANDOVER.md` / `TESTING.md`。
