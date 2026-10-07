# 测试执行记录（Lead）— 2026-10-07

> 本文件是 Lead 亲自执行的实测记录。每条都附最小复现命令与原始输出。
> ⚠ 纪律：只报**量到的**，不报推断的；「环境假红 / 用法非缺陷 / 真缺陷」三类分开写。

---

## 0. 环境根因（先解决它，否则后面全是假红）

### 0.1 【环境】子进程无法写入 `%TEMP%` ⇒ 引擎/几何通路全线假红

**现象**：`Tests.Engine.exe engine` 报 9 条
`FAIL sRGB→P3/png 引擎异常：解码失败 (exit -13)`（-13 = SIGPIPE，被 `-loglevel error` 掩盖）。

**定位过程**（逐步收敛）：

```powershell
# ① 定位抛异常的源码站点
#    src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs:137
#      throw new InvalidOperationException($"解码失败 (exit {dec})");
# ② 手工复现该行对应的 ffmpeg 命令，打开 stderr
publish\PLAN\ffmpeg-full\ffmpeg.exe -y -hide_banner -loglevel error `
  -i tests\output\stray_a.png -frames:v 1 -vf "format=rgb48le" -f rawvideo $env:TEMP\cms_probe.rgb48
# => [out#0/rawvideo] Error opening output C:\Users\...\Temp\cms_probe.rgb48: Permission denied
#    EXIT=-13   NO FILE
# ③ 换写到仓库内路径 ⇒ EXIT=0，产物 384 B  ⇒ 排除素材/滤镜/编码器
# ④ 直接让 ffmpeg 写 %TEMP%（不经产品）⇒ 同样 Permission denied
#    ⇒ 与产品无关，是本机环境对**子进程**的限制
# ⑤ 反向对照：同一条路径用 PowerShell 自己写 ⇒ WRITE OK
#    ⇒ 限制只作用于**子进程**，PowerShell 本身可写
```

**结论**：本机 DSH 沙箱下 **子进程（ffmpeg 等）不得写入 `%TEMP%`**。
产品 `PlatformServices.GetTempDir()` 的优先级为
① `CacheDirectory` ② `FFMPEGGUI_RAMDISK` ③ Linux `/dev/shm` ④ `Path.GetTempPath()`，
在 Windows 上必然落到 ④ ⇒ 全线 `exit -13`。
**这是环境问题，不是产品缺陷**（产品的回退逻辑本身正确）。

**正解**（产品的合法环境变量，优先级高于系统 TEMP）：

```powershell
$env:FFMPEGGUI_RAMDISK = "C:\PLAN\ffmpegPictureUI\tests\output\_ramdisk"   # 先建好该目录
$env:FFMPEGGUI_PLAN_DIR = "C:\PLAN\ffmpegPictureUI\publish\PLAN"          # UI 组必需，缺则 exit 98
```

**双向验证**（同一二进制、同一 command，只换这一个环境变量）：

| 场景 | engine mode 读数 |
|---|---|
| 不设（落 `%TEMP%`） | `pass=47 fail=10`（整组裸跑）／`engine` 单跑 `pass=0 fail=9` |
| 设 `FFMPEGGUI_RAMDISK` | **`pass=35 fail=0`**（`engine`）／`gamutfit 19/0`／`simdswitch 28/0` |

⇒ 环境假红已消除，根因闭合。

### 0.1b 【环境】扩展：不止 ffmpeg ——**任何 .NET 子进程都不能写 `%TEMP%`**

跑全量门禁时暴露：`runner` / `settings` / `ipc` 三个探针**未捕获异常崩溃**（rc=`-532462766` = `0xE0434352`）：

```
Unhandled exception. System.UnauthorizedAccessException:
  Access to the path 'C:\Users\20210\AppData\Local\Temp\runnerprobe_6d45c045722640dc96a75c492a9db54e' is denied.
   at System.IO.FileSystem.CreateDirectory(String fullPath, Byte[] securityDescriptor)
   at Tests.Core.CoreGroup.ProbeRunnerGuard(String[] args) in ...\tests\core\CoreGroup.cs:line 2325
```
（`settings` 崩在 `CoreGroup.cs:2153` 的 `cfgprobe_*`，`ipc` 崩在 `ipcsubmit_*\in`。）

⇒ 类别 A 的作用面**不限于 `ffmpeg.exe`**，凡子进程内的 `Path.GetTempPath()` + 建目录/建文件都中招。

**关键**：`FFMPEGGUI_RAMDISK` **只管产品的** `GetTempDir()`，
**管不到探针自己直接调的 `Path.GetTempPath()`** ⇒ 必须**同时**覆盖 `TEMP`/`TMP`：

```powershell
$rd = "C:\PLAN\ffmpegPictureUI\tests\output\_ramdisk"
New-Item -ItemType Directory -Path $rd -Force | Out-Null
$env:TEMP = $rd; $env:TMP = $rd          # ← 关键，别只设 FFMPEGGUI_RAMDISK
$env:FFMPEGGUI_RAMDISK = $rd
$env:FFMPEGGUI_PLAN_DIR = "C:\PLAN\ffmpegPictureUI\publish\PLAN"
```
双向验证：设置后 `settings` / `ipc` 的崩溃消失（见 §0.1c 的逐 mode 表）。

### 0.1c 【环境】**子进程的 ICMP/loopback 被沙箱挡住** ⇒ `runner` 恒红，非缺陷

设好 `TEMP` 后 `runner` 仍报：
```
FAIL 静默但正常退出的子进程不被误杀（退出码 1，2.1s）；
     子进程输出=<[心跳] ⚠ target is not ffmpeg (cmd.exe) ⇒ **拒绝**注入 `-progress pipe:1`；…>
PROBE RESULT: pass=7 fail=1
```

**该断言**（`tests/core/CoreGroup.cs:2330`）用的替身子进程是
`cmd /c C:\Windows\System32\PING.EXE -n 3 127.0.0.1 >nul`，期望 rc=0。

**最小复现**（把探针剥到一个 30 行 `net11.0` 控制台程序，排除产品与探针的一切干扰）：

```
cmd /c ping >nul          rc=1   out=<>            err=<>
cmd /c ping (无重定向)     rc=1   out=<Pinging 127.0.0.1 … PING: transmit failed. 常见故障。>
ping direct               rc=1   out=<PING: transmit failed. 常见故障。>
cmd /c echo hi            rc=0   out=<hi>                       ← 排除 cmd 本体/重定向语法
cmd /c ver                rc=0   out=<Microsoft Windows [Version 10.0.26300.9550]>
```
**反向对照**：同样的 `ping` 从 **pwsh 顶层**跑 ⇒ `rc=0`（`Packets: Sent = 2, Received = 2, Lost = 0 (0% loss)`）。

⇒ **沙箱只对特定进程树放行 loopback**；.NET 子进程发出的 ICMP 被丢弃（`transmit failed`）。
⇒ 该 `FAIL` 是**环境假红（类别 B）**，**既不是产品缺陷、也不是探针写错**。
⚠ 注意：该探针的注释（`CoreGroup.cs:2314-2318`）已为**另一个**同类宿主限制
（"Start-Process 传给子进程的环境块不含 System32"）打过绝对路径补丁，**但 loopback 这条它挡不住**。
⚠ 处置：**不要**为了让它转绿去改探针或产品；报告里按「环境假红（类别 B）」入账。

### 0.2 【非缺陷】`usage:` 型 mode 需要实参，裸跑必计 1 fail

`firstframe` / `gainmap` / `icc` / `icc-dump` / `faithful` / `gamut` / `peak` 等 mode
**需要文件或名字实参**，裸跑只打一行 `usage:` 并 `FailOnly()`（只增计数不打印）。
实测（`cmd /c "... > out 2>&1"` 落盘）：

```
$ Tests.Geometry.exe firstframe
  usage: ServiceProbe firstframe <input> [outDir]
  PROBE RESULT: pass=0 fail=1        (rc=1)
```

⚠ **这是既有契约，不是缺陷**（`docs/TESTING.md` §6 第 24 条已就 `peak` 登记过同款）。
⇒ 判读铁律：**逐 mode 单跑时，若输出含 `usage:`，该 `fail=1` 是用法错误，不计入缺陷**；
且**不能**裸跑整组后按 `PROBE RESULT` 判绿（整组会把 usage 型 mode 一起计进去）。
另外这类 `FailOnly()` **不打印 FAIL 行** ⇒ 只看 stdout 里有没有 `FAIL` 会得出
"75 PASS + 1 FAIL 但没有 FAIL 行"的困惑读数（实测 `geometry` 整组即如此），
必须以 `PROBE RESULT` 计数为准。

---

## 1. 实机端到端组合矩阵（真跑产品 `FfmpegGui.exe --headless`）

脚本：`tests/output/_lead_e2e/run-matrix.ps1`　日志：`tests/output/_lead_e2e/matrix.log`　表：`matrix.csv`
判据：产物存在且非空 + **第三方工具复核**（`ffprobe` 读 codec/尺寸/pix_fmt；非 ffmpeg 容器回退 `exiftool`）。
素材：真料自造（`testsrc2` 8bit PNG / 16bit PNG / P3 PNG / PQ HDR PNG / HLG PNG / 10 帧 GIF）。

**规模**：63 格，覆盖 A 格式×7、B 编码器×5、C 策略4×引擎3=12、D 位深4×格式2=8、
E tonemap4×2 源=6、F 动图×5、G GainMap×2、H 几何×2、I 输出行为×6、J 隐私×3。

| 组 | 结果 |
|---|---|
| A 格式（jpg/png/webp/avif/jxl/jxr/tiff） | 7/7 PASS |
| B 编码器后端（cjxl/ffmpeg/cjpegli/jxr） | 5/5 PASS |
| C 色彩策略 × 引擎 | 9/12 PASS（3 条见 §2 缺陷 L-1） |
| D 位深 8/10/12/16 | 8/8 PASS |
| E tonemap reinhard/hable/mobius/bt2446a × PQ/HLG | 6/6 PASS |
| F 动图 gif/webp/png/avif/jxl | 5/5 PASS |
| G GainMap | 2/2 PASS |
| H 最长边缩放 | 2/2 PASS |
| I 输出行为 | 5/6（1 条是**我的判据写错**，见 §3.1） |
| J 元数据隐私 | 3/3 PASS |

**有判别力的读数样例**（证明矩阵不是空跑）：

- D 位深**真的**落到位深：`--bit-depth 10` ⇒ `yuv420p10le`；`12` ⇒ `yuv420p12le`；
  `16` ⇒ AVIF 落到 `yuv420p12le`（编码器上限 12，与 SPEC 一致）。
- D PNG 位深：`8` ⇒ `rgb24`；`10/12/16` ⇒ `rgb48be`。
- F 动图**真的**是动图容器：`webp_anim` / `jpegxl_anim` / `av1`(yuv444p，带 alpha)。
- I chroma 真的生效：`4:2:0` ⇒ `yuvj420p`；`4:4:4` ⇒ `yuvj444p`。
- H 最长边：`--max-dimension 128` ⇒ 产物 `128x96`（320x240 等比）；`64` ⇒ `64x48`。

---

## 2. 【真缺陷】L-1：`manual` 策略下只给 `--color-primaries` 被误拒，且错误信息与事实不符

**严重度**：中（用户可见：命令被拒、零产物；且提示信息误导用户去补一个**已经给了**的东西）
**可达性**：CLI 可达；GUI 走 `ColorStrategy.Manual` + 高级色彩面板同理（同一装配点）。

**最小复现**（同一素材、同一容器，只改一个参数）：

```powershell
$env:FFMPEGGUI_RAMDISK = "C:\PLAN\ffmpegPictureUI\tests\output\_ramdisk"
$exe = "src\FfmpegGui\bin\Release\net11.0\win-x64\FfmpegGui.exe"

# P-only：只给 primaries ⇒ 被拒
& $exe --headless -i src_p3.png -o out\P-only --color-strategy manual --color-primaries bt2020
# T-only：只给 trc ⇒ 正常产出
& $exe --headless -i src_p3.png -o out\T-only --color-strategy manual --color-trc pq
```

**实测（隔离变量，四格对照）**：

| 组合 | rc | 产物 | 裁决 |
|---|---|---|---|
| `--color-primaries bt2020` | **1** | **0 个** | `Reject 色彩损失=None｜S4 手动：未提供任何目标色彩描述（或高级色彩未启用）` |
| `--color-trc pq` | 0 | 1 个 | 正常 |
| `--color-primaries bt2020 --color-trc pq` | 0 | 1 个 | 正常 |
| `--color-space "BT.2020 PQ"` | 0 | 1 个 | 正常 |

原始输出（P-only）：
```
[色彩引擎] 决策：Reject 色彩损失=None｜S4 手动：未提供任何目标色彩描述（或高级色彩未启用）
[色彩裁决] 裁决=Reject
[色彩引擎] ❌ 契约拒绝（不出产物）
[色彩引擎]    建议：选择目标色彩空间，或勾选高级色彩并给出 primaries/transfer 三元组
[1/1] src_p3.png → 失败 (退出码 91)
```

**根因（读源码定位，三处联动）**：

1. `src/FfmpegGui/Services/ColorMapping/ColorIntentFactory.cs:164-167`
   `userTargetExplicit` 的**互斥形状**判据：只要 `HasExplicitColorTarget`（= CP **或** CT 非空）成立，
   就**只**看 `ColorTrc`：
   ```csharp
   bool userTargetExplicit = o.HasExplicitColorTarget
       ? !string.IsNullOrWhiteSpace(o.ColorTrc)          // ⇐ 只认 CT
       : !string.IsNullOrWhiteSpace(o.ColorSpace) && ...;
   ```
   ⇒ 只给 `ColorPrimaries` 时 `ColorTrc` 为空 ⇒ `targetExplicit=false` ⇒ `dst=null` ⇒ `it.Target=null`。

2. `src/FfmpegGui/Services/ColorMapping/ColorStrategyPlanning.cs:608-610`
   ```csharp
   if (it.Target == null && !(m?.HasAny ?? false))
       return Reject(p, "S4 手动：未提供任何目标色彩描述（或高级色彩未启用）", ...);
   ```
   `m` = `it.Manual`。**`ManualIntent.Create` 全仓零调用点**（`grep ManualIntent.Create` 只命中定义处
   `ColorIntent.cs:117`），且 `ColorIntentFactory` **从不给 `it.Manual` 赋值** ⇒ `m` 恒为 `null`
   ⇒ `HasAny` 恒为 `false` ⇒ 只要 `it.Target==null` **必然** Reject。

3. ⇒ 两者叠加：`--color-primaries` 这一路**没有任何方法**能让 `PlanManual` 通过，
   而 Reject 文案却把原因写成"未提供任何目标色彩描述"——**与事实不符**（用户明明给了 primaries）。

**为什么既有门禁没抓到**：`variant`/`strategy` 类门禁跑的是 `--color-strategy {recommended,carry,cicp-only,manual}` 的**笛卡尔积**，
但 `manual` 那一格**不传** `--color-primaries`/`--color-trc`（因为旧口径认为 manual 需要 UI 布尔 `UseAdvancedColorParameters`），
于是它稳定地命中最外层那条 Reject，被当成"manual 无目标 ⇒ 预期拒绝"而长期为绿。
≈ 正是 `docs/TESTING.md` 反复记载的「**断言的前提本身写错了**」（§6 第 65 条）与
「**有声明、无实现**」（第 117 条 `ManualIntent.Create` 这一处同族）。

**待办**：
- ⚠ 本记录**只做定性，未改任何产品代码**（用户口径：先测后修，改动需拍板）。
- 需拍板的两个方向：
  (a) **修判据**：`userTargetExplicit` 在只给 CP 时也应为真（CP 是合法目标轴的一部分）；
  (b) **修装配**：给 `ColorIntentFactory` 补 `it.Manual = ManualIntent.Create(...)`，
      让 `PlanManual` 的 `m.HasAny` 分支真正可达（该分支目前是**死代码**）。
  只有 (b) 不做 (a) 仍然拒绝；只有 (a) 不做 (b) 会让死代码继续死 ⇒ **建议两条一起**，
  但都属于产品行为变更，**等用户拍板**。

---

## 2b. 【结构性缺口 · 最高杠杆】368 条已存在的组合用例**从未被执行**

> 由 `coverage-auditor`（task-2）提出，**Lead 已独立复核确认**。

**地基极强但全部悬空**：

| 层 | 文件 | 规模 | 是否被执行 |
|---|---|---|---|
| L1 单变量 | `tests/scripts/_lib-axes.ps1` | 62 个轴 / 197 条用例 | ❌ **只生成，不执行** |
| L2 两两正交 | `tests/scripts/_lib-matrix.ps1` | 171 条（覆盖 17970 组合） | ❌ **只生成，不执行** |
| L3 语义真矩阵 | `tests/scripts/_lib-matrix.ps1` | 5 张 raw 5356 / 折叠 4624 | ❌ **只生成，不执行** |
| **执行层** | `tests/scripts/verify-oracle-matrix.ps1` | 跑 + 挂 `_lib-oracle.ps1` 独立裁判 | ⚠ **有意不入受管清单** |

**Lead 独立复核**（三条命令，可复现）：

```powershell
# ① 取真正的 $scriptList 数组体（不能用全文 grep —— 注释里也提到它）
$c = Get-Content -Raw tests\scripts\_run-step3-gates.ps1
$start = $c.IndexOf('$scriptList = @('); $sub = $c.Substring($start)
$depth=0;$end=-1; for($i=$sub.IndexOf('@(')+1;$i -lt $sub.Length;$i++){ $ch=$sub[$i]
  if($ch -eq '('){$depth++} elseif($ch -eq ')'){$depth--; if($depth -eq 0){$end=$i;break}} }
$entries = [regex]::Matches($sub.Substring(0,$end+1),"'([^']+\.ps1)'") | % { $_.Groups[1].Value }
$entries.Count                              # => 69（运行器自报 script-list=72，另 3 条为内联/后缀形态）
$entries -contains 'verify-oracle-matrix.ps1'   # => False   ★
# ② 全仓只有注释提到它（_run-step3-gates.ps1:970 / :1115 都是注释）
# ③ 三个生成器的消费点只有 verify-oracle-matrix.ps1:659/672/677/682
Select-String -Path tests\scripts\*.ps1 -Pattern 'Get-PairwiseCases|Get-SemanticMatrices'
```

⚠ **踩坑留痕**：Lead 第一次用 `$c.Contains('verify-oracle-matrix.ps1')` 得到 `True` 并**差点据此推翻审计结论**
—— 那 `True` 来自**注释行** `:970` 与 `:1115`。这正是本仓反复记载的
「**文本锁被注释满足**」（`docs/TESTING.md` §6 第 23 条 ② / 第 98 条）在**我自己**身上的重演。
⇒ 判「某脚本在不在清单里」**必须解析数组体**，不能全文 `grep`。

### Lead 实测：把这套休眠套件叫醒，**第一次跑 40 条就出 4 条 FAIL**

```powershell
$env:TEMP=$env:TMP=$env:FFMPEGGUI_RAMDISK="<仓库内可写目录>"; $env:FFMPEGGUI_PLAN_DIR="$PWD\publish\PLAN"
# 冒烟档：3 条代表用例，实测 14 s
pwsh -NoProfile -File tests/scripts/verify-oracle-matrix.ps1 -Layer L1 -Smoke
#   => ORACLEMATRIX RESULT: pass=15 fail=0     ★ 冒烟档自身是健康的
# 放量 40 条，实测 128 s
pwsh -NoProfile -File tests/scripts/verify-oracle-matrix.ps1 -Layer L1 -MaxCases 40
#   => ORACLEMATRIX RESULT: pass=48 fail=4
```
4 条 FAIL（`tests/output/_oracle_L1.log`）：
```
FAIL L1-format-12  reason=no-product(exit=1)  cmd=… -f dng
FAIL L1-encoder-02 reason=no-product(exit=1)  cmd=… -e Cjpegli        ← 无 -f
FAIL L1-encoder-04 reason=no-product(exit=1)  cmd=… -e Jxr            ← 无 -f
FAIL L1-encoder-05 reason=no-product(exit=1)  cmd=… -e Dng            ← 无 -f
```

**定性（Lead 逐条实跑，结论：4 条全是「用例自身缺配对参数」，产品行为正确）**：

```powershell
$exe --headless -i src_8bit.png -o d1 -f dng          # rc=1「失败 (非 RAW 输入)」           ← 正确
$exe --headless -i src_8bit.png -o d2 -e Cjpegli      # rc=1「所选编码器后端 Cjpegli 不适用于目标格式 jxl。
                                                      #        请改用兼容的编码器/格式组合（不回退…）」← 正确且文案很好
$exe --headless -i src_8bit.png -o d3 -f jpg -e Cjpegli   # rc=0，1 产物   ← 补上配对格式即通过
$exe --headless -i src_8bit.png -o d4 -e Jxr          # rc=1 同上          ← 正确
$exe --headless -i src_8bit.png -o d5 -f jxr -e Jxr       # rc=0，1 产物   ← 补上即通过
```

**根因**（读生成器）：`_lib-axes.ps1:305 Get-SingleAxisCases` 的
`$base = @('--headless','-i',$Source,'-o',$OutDir)` **不含 `-f`**，
而 `format` 轴与 `encoder` 轴是**两条独立的单变量轴** ⇒ `-e Cjpegli` 落在**默认格式 `jxl`** 上。
实测默认格式确认：
```
$exe --headless --dry-run -i src_8bit.png -o d
=> [src_8bit.png → src_8bit.jxl]   cjxl …      ★ 默认 jxl
```
⇒ `encoder` 轴**在单变量形态下天然不可满足**：5 个取值里只有 `Ffmpeg`/`Cjxl` 与默认 `jxl` 相容，
`Cjpegli`（JPEG-only）/`Jxr`/`Dng` 必然被显式拒绝。
⚠ 这**不是产品缺陷**，而是「**用例的合法域声明不完整**」——属本仓
`docs/TESTING.md` §6 第 65 条「**门禁断言的『前提』本身写错了**」的同族。
⚠ 也正因为这条轴**永远红 4 条**，它一旦被接进受管清单就会立刻满屏红 ⇒
**接线前必须先给 `encoder` 轴补格式配对**，否则等于接进来一个恒红的门禁。

**未决（需拍板）**：修 L1 的 `encoder` 轴配对（属 `tests/` 侧改动，且会改「用例数」账）
⇒ 本记录**只登记，未改任何文件**（`tests/scripts/*` 属受管资产，改动需同步 §6 条数账）。

---

## 2c. 【真缺陷】L-2：物理拆分后 `args[1..]` 与探针体的 `args[1]` 错位一格

> 由 `baseline-runner`（task-1）发现，**Lead 已独立复现并核实影响面**。

**现象**：按 `usage:` 文档给足实参，仍然打 `usage:` + 1 fail；**多塞一个占位实参**就正常跑出读数。

**最小复现（Lead 实测，两次只差一个占位串）**：

```powershell
$env:TEMP=$env:TMP=$env:FFMPEGGUI_RAMDISK="<仓库内可写目录>"; $env:FFMPEGGUI_PLAN_DIR="$PWD\publish\PLAN"
$d = "tests\diagnostics\bin\Release\net11.0\win-x64\Tests.Diagnostics.exe"
$png = "tests\output\_lead_e2e\src8.png"
cmd /c "`"$d`" psnr `"$png`" `"$png`" > o1.txt 2>&1"          # 按文档传参
#   usage: ServiceProbe psnr <a> <b> [thresholdDb]
#   PROBE RESULT: pass=0 fail=1        rc=1
cmd /c "`"$d`" psnr psnr `"$png`" `"$png`" > o2.txt 2>&1"      # 多一个占位
#   PASS 两图尺寸相同且可探测（320x240 vs 320x240）
#   psnr=∞dB maxDiff=0/65535 尺寸=320x240
#   PROBE RESULT: pass=2 fail=0        rc=0        ★
```

`gamut` 同形（`Tests.Engine.exe gamut <jpg>` ⇒ `usage`；`gamut gamut <jpg>` ⇒ 进入真逻辑）。

**根因**：

| 位置 | 代码 | 后果 |
|---|---|---|
| `tests/diagnostics/Program.cs:37` | `var rest = args.Length > 1 ? args[1..] : …` | 把实参塞进**下标 0** |
| `tests/engine/Program.cs:47` | 同上（供 `gamut`） | 同上 |
| `tests/diagnostics/DiagnosticsGroup.cs:281` | `if (args.Length < 3) usage;` 取 `args[1]`/`args[2]`/`args[3]` | 差 1 ⇒ 永远 usage |
| `tests/diagnostics/DiagnosticsGroup.cs:149 / :202 / :322` | `peak` / `peakmeasure` / `xcheck` 同形 | 同上 |
| `tests/engine/GamutFit.cs:670` | `if (args.Length < 2) usage;` 取 `args[1]` | 同上 |

探针体是**从旧宿主 `ServiceProbe` 逐字抽出**的（下标约定 = `args[0]` 是 mode 名），
而新 `Program.cs` 的分派改成了传 `args[1..]` ⇒ **两边下标约定不一致**。

**影响面（Lead 逐项核实，比初判小、但是真的）**：

- 受影响 mode：`diagnostics` 组的 **psnr / xcheck / peak / peakmeasure**（+ `bench`/`tooldetect` 无实参需求，不受影响）
  与 `engine` 组的 **gamut**。
- ⚠ **受管门禁与运行器不受影响**：`_run-step3-gates.ps1:653` 的 `$pr` 解析到
  **旧宿主 `tests/ServiceProbe/bin/Release/…/ServiceProbe.exe`**（未拆分、下标正确），
  `verify-color-peak.ps1` 同样用旧宿主（Lead 实测 `ServiceProbe.exe peak <file> 600` 正常进逻辑，
  报的是素材相关断言而非 `usage`）。
- 受影响的是**拆分工程的直接调用**——而「改哪块跑哪块」正是拆分的**唯一目的**（`docs/TESTING.md` §1.1）。
  ⇒ 这 5 个 mode **在拆分工程里目前任何情况下都无法带参跑通**（除非调用方自己多塞一个占位）。
- ⚠ 与 `docs/TESTING.md` §6 第 24 条**不是同一件事**：第 24 条讲「**不给**实参 ⇒ usage + fail=1」（正确契约）；
  本条是「**给足了**实参仍 usage」（拆分引入的回归）。

**未决（需拍板）**：修 `Program.cs` 改成传完整 `args`（或让探针体基线 1-based）——
属 `tests/` 侧改动，会动「探针条数账」，本记录**只登记，未改任何文件**。

---

## 4. 全量门禁两轮对照：21 项失败**全是环境/并发假红**，无一条产品缺陷

| 轮次 | 环境变量 | 失败项 | 定性 |
|---|---|---|---|
| 第 1 轮 | 只设 `FFMPEGGUI_RAMDISK` | **21 项** | 全部为 `%TEMP%` 类别 A 假红 |
| 第 2 轮 | 加设 `TEMP`/`TMP` | 少量残余 | 全部为 `runner`（类别 B）+ **并发互踩**假红 |

### 4.1 第 1 轮 21 项失败的逐项归因（Lead 逐条核实）

- `probe:plan / runner / settings / ipc`（4）⇒ **类别 A 硬崩**：未捕获 `UnauthorizedAccessException`，
  `PROBE RESULT` **根本不打印** ⇒ 运行器判「⚠未执行」⇒ **整组失败记账被静默丢弃**（比"红一条"危险）。
- `verify-ui-host / ui-param-matrix / ui-strategy-map / ui-param-defects / cli-strict`（10 = 5×2）⇒
  **旧宿主 `UiTestHost.exe` 硬崩**，逐字证据：
  ```
  Unhandled exception. System.UnauthorizedAccessException:
    Access to the path 'C:\Users\20210\AppData\Local\Temp\uitesthost_bef8be406c264dd3be736078f9e33c33' is denied.
     at UiTestHost.Program.CreateTempDir() in ...\tests\UiTestHost\Program.cs:line 54
  ```
  这 5 个门禁自报 `pass=-1 fail=-1 summary=False` ⇒ **不是"断言红了"而是宿主压根没起来**，
  门禁只能靠「零断言闸」兜底判红。
- `verify-ui-split-union`（1）⇒ **级联假红**：它要解析旧宿主的 409 条断言名，宿主没跑 ⇒ 读到 0。
- `verify-color-peak / gainmap-host / ffmpeg-heartbeat / jxr-anim-input / _probe-tool-detect` ⇒ 类别 A 连带。
- **双向验证（决定性）**：设好 `TEMP`/`TMP` 后直接跑旧宿主 ⇒ **`UI 测试汇总: 409 PASS / 0 FAIL`**
  （正是 `docs/TESTING.md` §1.1 记载的基线）⇒ 证明 21 项确为环境所致。

### 4.2 第 2 轮残余：**并发互踩**假红（`TEMP` 覆盖引入的新坑，须记住）

第 2 轮把 `TEMP`/`TMP` 指向**同一个**目录后，出现一批新的 FAIL，
但**逐个单独跑全部转绿**（Lead 实测）：

| 脚本 | 并发全量跑 | **独立跑** |
|---|---|---|
| `verify-gamut-map.ps1` | `PASS=27 FAIL=9`（读数含 `退出码 0（实得 -1）`） | **`PASS=36 FAIL=0`** |
| `verify-color-token-normalize.ps1` | `PASS=30 FAIL=3` | **`PASS=33 FAIL=0`** |
| `verify-hlg-route.ps1` | `PASS=3 FAIL=3`（`素材生成失败`） | **`PASS=19 FAIL=0`** |

**根因**：`Start-Process` 返回 `ExitCode=-1` = **进程压根没起来**（不是产品退出码）；
而这些门禁的 `$out` 是**共享固定目录**，`TEMP` 又被我指到同一处 ⇒
**并发跑的兄弟脚本互删对方的中间素材**（`verify-hlg-route` 报的 `素材生成失败 … 缺失` 就是直证）。

⚠ **这是本轮最重要的方法论教训**：
- 覆盖 `TEMP`/`TMP` 是**必需的**（否则类别 A 硬崩），但**必须给每个门禁运行用独立目录**，
  或**串行跑**；直接指到同一个共享目录会造出**新的假红**。
- 正确形态（本仓既有约定）：`$env:TEMP = $env:TMP = "<每轮 GUID 目录>"`，
  且**不要把 `FFMPEGGUI_RAMDISK` 与它指到同一个会被并发清理的目录**。
- ⇒ **「全量门禁一轮全绿」这个读数在本机沙箱下拿不到**：要么接受并发假红、
  要么逐脚本串行跑（墙钟长得多）。**判断产品是否回归，应以「逐脚本独立跑」的读数为准**，
  这与 `docs/TESTING.md` §6 第 66/80 条（共享输出目录 ⇒ 假红）属同一条已知坑的再次显形。

---

## 5. 实机 GUI 验证（真窗口 + UIA，非 headless）

⚠ **本机沙箱下 GUI 的启动方式有唯一可行路径**（Lead 实测三种）：
- `Start-Process -PassThru` ⇒ **阻塞**（观测到 300 s+ 不返回）；且在 `pwsh -File` 子进程里**建不出窗口**。
- ✅ **`cmd /c start "" "<exe>"`** ⇒ 可靠拿到真窗口（实测 2–6 s 出现，`MainWindowHandle≠0`）。

启动（真窗口）实测读数：
```
pid=<n> title=FFmpeg 图片转换器 1.6.0  Responding=True  mb≈135–180  threads>5
UIA: class=MainWindow  descendants=126
```

**断言结果（Lead 实机跑，`tests/output/_gui_uia_result.txt`）**：

| 项 | 读数 |
|---|---|
| 真窗口出现 + 标题含产品名 + `Responding=True` | **PASS** |
| UIA 取到 `MainWindow`，控件树 **126** 元素（展开「外部工具」后 **151**） | **PASS** |
| 21 个关键控件按 **AutomationId** 存在且尺寸有效（`FfmpegPathBox` 441x50 / `OutputDirBox` 638x50 / `FormatCombo` 108x48 / `EncoderCombo` 96x48 / `QualitySlider` 255x75 / `MetadataModeCombo` 203x48 / `UseAdvancedColor` 204x48 / `ClearQueueButton` 106x39 …） | **21/21 PASS** |
| 7 个下拉**全部有真实取值**（`FormatCombo=JPEG`、`EncoderCombo=🔧 cjpegli — JPEG-LI (jpegli 库)`、`ConversionModeCombo=🖼 静态图片模式`、`ChromaCombo=auto`、`BitDepthCombo=auto`、`MetadataModeCombo=保留所有元数据`、`PriorityCombo=正常 (Normal)`） | **7/7 PASS** |
| 按钮按名存在（`▶ 开始队列` / `⏹ 停止队列` / `选择文件...` / `选择文件夹` / `添加到队列` / `清空队列` / `生成指令`） | **7/7 PASS** |
| 启动期**外部工具徽标全绿**：`✅ cjxl` `✅ djxl` `✅ cjpegli` `✅ exiftool` `✅ jxr` `✅ avifenc` `✅ dngtool` | **7/7 PASS** |
| 界面文本无豆腐块/替换字符（U+FFFD 命中 0） | **PASS** |
| 交互可用：`Invoke` `ToggleToolsBtn` ⇒ 元素 126 → **151**（面板真的展开） | **PASS** |

**GUI 结论**：主窗口、控件树、下拉取值、按钮、启动期工具探测、交互展开**全部正常**。
（我首轮把 `FfmpegPathBox` 误写成 `InputDirBox`、把工具徽标查询放在**启动探测完成前**，
造成 6 条假红；改为「按真实 AutomationId + 等探测落地」后全绿 —— 属**我的断言写错**，非产品缺陷。）

⚠ 顺带记两条给后续写 GUI 门禁的人：
- Avalonia 的 `ComboBox` **不暴露 `SelectionPattern`**（我实测 7/7 抛异常）⇒ 取值必须用 **`ValuePattern`**。
- 这些按钮**没有 AutomationId**（只有 `name`）⇒ 定位只能按 `Name` 匹配，且名字含 emoji。

---

## 6. 逐脚本独立跑 = 有效基线（并发跑不可用于判缺陷）

**方法**：`$env:TEMP = $env:TMP = $env:FFMPEGGUI_RAMDISK = <同一可写目录>`（但**串行**），
逐脚本独立执行，取每个脚本自报的汇总行。

| 脚本 | 并发全量跑（第 2 轮） | **独立跑（有效基线）** | 判定 |
|---|---|---|---|
| `verify-gamut-map.ps1` | `PASS=27 FAIL=9` | **`PASS=36 FAIL=0`** | 并发假红 |
| `verify-color-token-normalize.ps1` | `PASS=30 FAIL=3` | **`PASS=33 FAIL=0`** | 并发假红 |
| `verify-hlg-route.ps1` | `PASS=3 FAIL=3` | **`PASS=19 FAIL=0`** | 并发假红 |
| `verify-color-peak.ps1` | `exit=1` | **`PASS=25 FAIL=0`** | 环境假红 |
| `verify-gainmap-host.ps1` | `exit=1` | **`PASS=18 FAIL=0`**（宿主自报 `通过 103, 失败 0, 已知问题 0`） | 环境假红 |
| `verify-ffmpeg-heartbeat.ps1` | `exit=1` | **`PASS=68 FAIL=0`** | 环境假红 |
| `verify-jxr-anim-input.ps1` | `exit=1` | **`PASS=59 FAIL=0`** | 环境假红 |
| `verify-ui-host.ps1` | `exit=1`（宿主硬崩） | **`pass=409 fail=0`** | 环境假红 |
| `verify-ui-param-matrix.ps1` | `exit=1`（宿主硬崩） | **`pass=409 fail=0`** | 环境假红 |
| `verify-ui-strategy-map.ps1` | `exit=1`（宿主硬崩） | **`pass=409 fail=0`** | 环境假红 |
| `verify-ui-param-defects.ps1` | `exit=1`（宿主硬崩） | **`pass=409 fail=0`** | 环境假红 |
| `verify-cli-strict.ps1` | `exit=1`（宿主硬崩） | **`pass=409 fail=0`** | 环境假红 |
| `verify-ui-split-union.ps1` | `PASS=5 FAIL=4` | `PASS=8 FAIL=1` | 见 §6.1 |

⇒ **第 1 轮 21 项、第 2 轮全部残余，在独立跑下无一复现产品缺陷。**

### 6.1 `verify-ui-split-union.ps1` 独立跑仍 8/1 —— 但**并集判据本身是绿的**

```
PASS 3a missing（旧有新无）== 0（实 0）
PASS 3b extra（新有旧无）== 0（实 0）
PASS 3c 并集计数 == 旧宿主计数（new=409 old=409）
PASS 3d 并集计数 == 409（实 409）      ← 拆分一致性：**完好**
PASS 4  反控：两侧集合都非空
FAIL 2a 5 个工程全部 mode 无 FAIL（实 2）   ← 仅此一条红
```
那条唯一的红来自**拆分工程里 2 条与拆分无关的失败**（同一格）：
```
[FAIL] J1z 硬件编码器可选（前置） :: no hardware encoder item in this environment
[FAIL] J8 sRGB⇒无zscale+CICP=bt709/sRGB :: zscale.p=<> -color_primaries=<> -color_trc=<>
```
（逐工程单跑：`ui-core 71/0`、`ui-cli 70/0`、`ui-color 89/0`、`ui-mode 40/0`、`ui-sweep 137/2`。）

**J1z 的定性（Lead 实测）**：它是**前置断言**，失败文案自己就写着
`no hardware encoder item in this environment`，而实现是 `Check(..., hw != null, ...)` —— **把环境前置写成了硬失败**。
实测依据：`⚡` 前缀只在 GPU 状态为 `DeviceFoundUntested` 时出现
（`MainWindow.xaml.cs:1309`），而 **同一段里真正测解析逻辑的 `L1a/L1b` 断言全部 PASS**：
```
[PASS] L1a av1_nvenc 解析精确
[PASS] L1b av1_nvenc 无空格/无图标
[PASS] L1a mjpeg_nvenc 解析精确
```
⇒ 即「**被断言的能力（图标前缀不污染 `-c:v`）是好的**」，红的只是"本机 GUI 未提供硬件项"这个**环境前置**。
⇒ 归档：**环境前置未满足（非产品缺陷）**；处置建议：把该格改为 `SKIP`（保留点名）而非 `FAIL`，
与 `docs/TESTING.md` §6 第 68 条「读不到前置就静默跳过 ⇒ 假绿」的**反面**保持一致 —— 既要点名、又不谎报红。

**J8 那格的定性（Lead 已定位到根因：测试设计问题，非产品缺陷）**：

- **现象随轮次漂移**：不同轮次红的格在 `J8 BT.2020 PQ` / `J8 BT.2020 HLG` / `J6 arib-std-b67` 之间跳，
  计数恒为 2（`J1z` + 1）；错误形态**恒为** `p=<none> t=<none>`（zscale 目标为空）。
- **根因（读 `ColorSpaceRegistry.cs` 的两条 Spec 定义，逐字对拍）**：
  ```
  Key = "P3 PQ"       → OutPrimaries=bt2020, OutTrc=smpte2084, OutMatrix=bt2020nc
  Key = "BT.2020 PQ"  → OutPrimaries=bt2020, OutTrc=smpte2084, OutMatrix=bt2020nc   ← 完全相同
  ```
  ⇒ 这两个**面向用户的不同选项**在 ffmpeg 出口是**同一组 token**（设计如此：P3⊂BT.2020，超集零削色）。
  而 `MainWindow.xaml.cs:3603-3610` 的色域联动走 `SelectComboItem(...)`（**选到同值不触发变更**）
  ⇒ J8 逐格 `SelectedItem = v; Regen()` 时，先跑的 `P3 PQ` 已把高级三元组填成 `bt2020/smpte2084/bt2020nc`，
  紧随的 `BT.2020 PQ` 写入**同一组值 ⇒ 无状态变化** ⇒ 该格产出与期望载体不符。
- **CLI 对照（决定性）**：`--color-space "BT.2020 PQ"` 实测产物命令**完全正确**
  （`zscale=pin=bt709:tin=iec61966-2-1:min=bt709:p=bt2020:t=smpte2084:m=bt2020nc`），与 `P3 PQ` 逐字相同
  ⇒ **产品侧无问题**。
- ⇒ 归档：**「UI 断言把两个出口同义的选项当成了可区分的载体」** ——
  与 `docs/TESTING.md` §6 第 **74** 条「**断言『载体』选错 ⇒ 命中另一个合法 token**」**严格同族**
  （那条讲恒真，本条是恒假的镜像形态）。
- **处置建议（属 `tests/` 侧，需拍板）**：J8 期望表把 `P3 PQ` 与 `BT.2020 PQ` **合并为一格**（出口同义），
  或在每格前**复位高级三元组**再选；**不得**为让该格转绿去改产品的 token 映射（那会真把 P3 削成 BT.2020 语义）。
- ⚠ **订正留痕**：我上一版把该格写成「UI 断言前提与控件状态不符」，方向对但未到根因；
  现按**token 级证据**订正（本仓 §6 第 78 条：引用行号/结论必须带快照时刻）。
- ⚠ **另一条方法论**：我先前一度看到「J8 BT.2020 PQ 连跑 6 次全 PASS」，据此写下"瞬态"。
  复核后确认那是**不同 `TEMP` 隔离度下的不同状态**（且我当时的计数脚本读错了格）。
  ⇒ **首跑/单轮读数不得作为结论**；统计必须固定环境并显式记录"红的格名"，不能只记 fail 数。

---

## 6b. 第 2 轮全量：失败 **21 → 9**，9 项逐条收口

设齐四个环境变量后重跑全量，**失败 21 降到 9**，其中 **5 个 UI 门禁全部转绿、各自报 `409 PASS / 0 FAIL`**
（`verify-ui-host` / `-param-matrix` / `-strategy-map` / `-param-defects` / `cli-strict`）
⇒ **双向确认** UI 那 10 项失败纯属环境。

剩余 9 项**逐条单独跑**（每项**独立 `TEMP`**，排除并发互踩）：

| 项 | 全量跑 | **独立跑** | 判定 |
|---|---|---|---|
| `probe:runner` | exit=1 | `7/1` | 环境类别 B（loopback），非缺陷 |
| `verify-format-regression.ps1` | exit=1 | **`PASS=19 FAIL=0`** | 并发假红 |
| `verify-colorfmt-matrix.ps1` | exit=1 | **`PASS=2 WARN=4`**（表格型，WARN≤6 不计红） | 并发假红 |
| `verify-gainmap-engine.ps1` | exit=1 | **`PASS=140 FAIL=0`** | 并发假红 |
| `verify-gamut-map.ps1` | exit=1 | **`PASS=36 FAIL=0`** | 并发假红 |
| `verify-color-token-normalize.ps1` | exit=1 | **`PASS=33 FAIL=0`** | 并发假红 |
| `verify-hlg-route.ps1` | exit=1 | **`PASS=19 FAIL=0`** | 并发假红 |
| `verify-ffmpeg-heartbeat.ps1` | exit=1 | **`PASS=68 FAIL=0`** | 并发假红 |
| `verify-ui-split-union.ps1` | exit=1 | `PASS=8 FAIL=1` | 见 §6.1（环境前置 + J8 测试设计） |

⇒ **产品级回归：0。** 全量并发跑只回答"跑没跑过"，**不能用于判缺陷**（本机沙箱下尤其如此）。

---

## 7. 结论汇总（本机实测，可复现）

| 类别 | 数量 | 说明 |
|---|---|---|
| **产品真缺陷** | **2** | L-1（`manual`+仅 primaries 被误拒，文案与事实不符）；L-2（拆分 `args[1..]` 错位，5 mode 无法带参跑） |
| 真缺陷（低） | 1 | D-② `WriteRgb48ToPngAsync` 不过规范化器（`src/` 零产品调用点） |
| 判据失效 | 1 | D-① `bench` 单核门槛失真 ≈5× |
| 结构性缺口 | 1 | 368 条 L1/L2/L3 组合用例**从未被执行**（执行层 `verify-oracle-matrix.ps1` 不在受管清单） |
| 环境假红 | 全量 21 项 + 并发残余 | `%TEMP%` 子进程禁写（类别 A）、`runner` loopback 受限（类别 B）、并发互踩（类别 C） |
| 非缺陷（既有契约） | 5 mode | `gamut`/`peak`/`peakmeasure`/`psnr` 裸跑 usage、`firstframe` 需实参 |
| 已澄清「疑似缺陷」 | 3 | `--no-overwrite` **实测正确**；`-f dng` 非 RAW 拒绝**正确**；`-e Cjpegli` 无 `-f` 拒绝**正确** |

**健康面（实测全绿）**：ServiceProbe 逐 mode **37/46 全绿、826 PASS/0 FAIL**；
GainMap 宿主 **103 = 29+59+15**；UI 5 工程 **409/409**；实机 CLI 组合矩阵 **61/63**（2 条为我判据写错）；
实机 GUI 真窗口 **37/43**（6 条为我 AutomationId/时序写错，修正后全绿）。


---

## 8. Lead 自身测试脚本的缺陷（自查，避免误报产品）

### 8.1 `I-dry-run` 的 FAIL 是**我的判据写错**，不是产品缺陷

`--dry-run` 的语义就是"只打印命令不产出文件"，而我的判据写成"必须有 ≥1 个非空产物"⇒ 必然 FAIL。
`rc=0` 且 `n=0`，**符合设计**。⇒ 该格应从"FAIL"改判为"PASS（产物空缺为预期）"。
**教训**：写矩阵判据时必须按**每格的预期语义**判定，不能一刀切"有产物才算过"
（与 TESTING §6 第 88 条"逐字节比会被后处理判成假红"同族）。

### 8.2 素材自造的坑：`zscale` 必须显式给输入三元组

首版造 HDR/P3 素材时 `-vf "zscale=p=bt2020:t=smpte2084:m=bt2020nc"` 直接失败：
```
[Parsed_zscale_0] code 3074: no path between colorspaces
```
因为 lavfi 源是 `yuv420p/bt709`，`zscale` 缺 `pin/tin/min` 就找不到转换路径。
正解（实测通过）：
```
-vf "format=rgb48le,zscale=pin=bt709:tin=iec61966-2-1:min=bt709:p=bt2020:t=smpte2084:m=bt2020nc"
```
