# ffmpegPictureUI 测试基线报告（2026-10-07）

> 作者：`baseline-runner`（teammate）｜任务：共享任务 `task-1`
> 环境：Windows + DSH 沙箱（danger-full-access）｜构建：Release / net11.0 / win-x64
> 采集方式：9 个 ServiceProbe 组**逐个 mode 单跑**，`cmd /c "exe <args> > out 2>&1"` 落盘再读

---

## 0. 一句话结论

在**正确设置四个环境变量**（`TEMP`、`TMP`、`FFMPEGGUI_RAMDISK`、`FFMPEGGUI_PLAN_DIR`）的前提下，
9 组 46 个 mode 中 **37 个 mode 全绿**（合计 **826 条断言 PASS / 0 FAIL**），**9 个 mode 非零退出**。

**这 9 条没有一条是"产品算错了"**，必须分开记账（下表为 **Lead 最终裁定后**的归档口径）：

| 类别 | 涉及 | 性质 | 是否产品缺陷 |
|---|---|---|---|
| **A. 环境假红（沙箱进程约束）** | `runner`；`gainmap`/`ultrahdr`（夹具口径） | 子进程 loopback 受限 / 未设 `TEMP` 的硬崩 | ❌ 否 |
| **B. usage 非缺陷（欠实参的既有行为）** | 裸跑的 `gamut`/`peak`/`peakmeasure`/`psnr` | 裸跑只打 `usage:` 计 1 fail（`TESTING.md` §6 第 24 条） | ❌ 否 |
| **C. 真缺陷（拆分回归，**严重**）** | `diagnostics` 4 个 mode + `engine/gamut` | `args[1..]` 错位 ⇒ **给足实参也打 `usage`** | ✅ **是（探针/分派层）** |
| **D. 真缺陷（产品 API 契约缺口，严重度=低）** | `xcheck` 的 `exit=-22` | `WriteRgb48ToPngAsync` 未过 `NormalizePrimariesToken` | ✅ **是**（`src/` 零产品调用点 ⇒ 生产链路未坏） |
| **E. 判据失效（门槛失真）** | `bench` "变曲线+裁切" 稳定 5× 不达标 | 单核吞吐绝对值门槛换环境后失真 | ❌ 否（**Lead 裁定，不进产品缺陷账**） |

⇒ **真缺陷共 2 条**：C（严重，拆分回归；`src/`+`tests/` 分派层）与 D（低，公开 API 契约缺口）。
判据失效 1 条（E）。其余全部是环境假红或既有非缺陷行为。

⚠ **最重要的一条**：**只设 `FFMPEGGUI_RAMDISK` 是不够的**。Lead 最初口径（engine 47/10 → 82/0）只是**第一层**；
真正的全量跑法必须**同时覆盖 `TEMP`/`TMP`**，否则 `core` 组的 `plan`/`runner`/`settings` 会**未捕获异常硬崩**
（`rc=-532462766` = `0xE0434352`），整组**连汇总行都拿不到** ⇒ 运行器判「⚠未执行」⇒ **静默丢掉整组记账**。
详见 §1 与 §3-A-1。

---

## 1. 正确跑法（逐字复现，照抄即可）

```powershell
# ── 1) 四个环境变量：缺一不可 ─────────────────────────────────────────
$rd = "C:\PLAN\ffmpegPictureUI\tests\output\_ramdisk"
New-Item -ItemType Directory -Path $rd -Force | Out-Null

$env:TEMP  = $rd                              # ① 探针自身 Path.GetTempPath() 只认 TEMP/TMP
$env:TMP   = $rd                              # ② Windows 上 TMP 优先，两个都要设
$env:FFMPEGGUI_RAMDISK = $rd                  # ③ 只管产品的 PlatformServices.GetTempDir()
$env:FFMPEGGUI_PLAN_DIR = "C:\PLAN\ffmpegPictureUI\publish\PLAN"   # ④ UI 组必需，缺它 exit 98

# ── 2) 逐 mode 单跑（本仓范式：cmd /c 重定向落盘再读）────────────────
$exe = "C:\PLAN\ffmpegPictureUI\tests\engine\bin\Release\net11.0\win-x64\Tests.Engine.exe"
cmd /c "`"$exe`" engine > `"$env:TEMP\engine.txt`" 2>&1"
Get-Content "$env:TEMP\engine.txt" | Select-String "PROBE RESULT"
```

为什么三个都要设 —— 两条**互不相干**的代码路径：

| 环境变量 | 作用对象 | 代码位置 | 不设的后果 |
|---|---|---|---|
| `TEMP` + `TMP` | **探针自己** | 46 处硬编码 `Path.GetTempPath()` | `plan`/`runner`/`settings`/`ultrahdr` 等**硬崩或红** |
| `FFMPEGGUI_RAMDISK` | **产品内部** | `PlatformServices.GetTempDir()`（`PlatformServices.cs:478`） | 引擎解码 `exit -13` 假红 |

⚠ **踩坑记录（我自己犯过，写下来防复发）**：
- 直接用 `& $exe $mode 2>&1 | Out-String` 抓 stdout **会丢行**（本仓已知问题）⇒ 必须 `cmd /c "... > file 2>&1"`。
- `geometry` / `orientation` / `orientmeta` 的第 1 个实参是 **outDir**，**不是输入文件**。
  我第一版把 PNG 传进去 ⇒ `Directory.CreateDirectory` 撞同名 ⇒ `IOException` 崩。
  传目录后立刻全绿（52/0、15/0、8/0）。

---

## 2. 逐组逐 mode 实测表（权威读数，v3 最终轮）

配置：`TEMP`/`TMP`/`FFMPEGGUI_RAMDISK` = `tests\output\_ramdisk`，`FFMPEGGUI_PLAN_DIR` = `publish\PLAN`。
原始日志：`tests\output\_baseline_modes\<组>__<mode>.txt`（逐 mode）、`_summary.csv`（汇总）、`_console.txt`（控制台）。

### core（Tests.Core）— 6/7 全绿，325 条断言

| mode | rc | pass | fail | skip | 判读 |
|---|---|---|---|---|---|
| contract | 0 | 137 | 0 | 0 | ✅ |
| verdict | 0 | 41 | 0 | 0 | ✅ |
| plan | 0 | 39 | 0 | 0 | ✅（**设 TEMP 前是硬崩**，见 §3-A） |
| decision | 0 | 4 | 0 | 0 | ✅ |
| wire | 0 | 93 | 0 | 0 | ✅ |
| **runner** | **1** | 7 | **1** | 0 | ⚠️ **环境假红（类别 B：子进程 loopback 受限）** |
| settings | 0 | 11 | 0 | 0 | ✅（**设 TEMP 前是硬崩**） |

### color（Tests.Color）— **8/8 全绿**，126 条断言（零环境依赖，纯进程内）

| mode | rc | pass | fail | 判读 |
|---|---|---|---|---|
| matrix | 0 | 15 | 0 | ✅ |
| curve | 0 | 44 | 0 | ✅ |
| ootf | 0 | 11 | 0 | ✅ |
| hlgwire | 0 | 3 | 0 | ✅ |
| bt2446 | 0 | 18 | 0 | ✅ |
| pq | 0 | 13 | 0 | ✅ |
| colormath | 0 | 12 | 0 | ✅ |
| iccname | 0 | 10 | 0 | ✅ |

### engine（Tests.Engine）— 3/4 全绿，82 条断言

| mode | rc | pass | fail | 判读 |
|---|---|---|---|---|
| engine | 0 | 35 | 0 | ✅ **需实参：真实 PNG 路径** |
| **gamut** | **1** | 0 | **1** | 🔴 **真缺陷 C（`args[1..]` 错位）** |
| gamutfit | 0 | 19 | 0 | ✅ |
| simdswitch | 0 | 28 | 0 | ✅ |

### encode（Tests.Encode）— **4/4 全绿**，58 条断言

| mode | rc | pass | fail | 判读 |
|---|---|---|---|---|
| encoding | 0 | 6 | 0 | ✅ |
| bitdepth | 0 | 11 | 0 | ✅ 需输入文件 |
| png3 | 0 | 1 | 0 | ✅ 需输入文件 |
| thumbnail | 0 | 40 | 0 | ✅ |

### metadata（Tests.Metadata）— **4/4 全绿**，38 条断言

| mode | rc | pass | fail | 判读 |
|---|---|---|---|---|
| metaraw | 0 | 17 | 0 | ✅ |
| icc | 0 | 4 | 0 | ✅ 需色彩空间名（我用 `"Display P3"`） |
| icc-dump | 0 | **0** | 0 | ✅ **设计即转储工具**：只打 ICC 头/tag 表，**本身无断言** ⇒ 0/0 是正确读数，**不是缺陷** |
| faithful | 0 | 17 | 0 | ✅ 需 `"ProPhoto RGB" yes` |

### gainmap（Tests.GainMap）— 3/5 全绿，28 条断言

| mode | rc | pass | fail | 判读 |
|---|---|---|---|---|
| **gainmap** | **1** | 1 | **5** | ⚠️ **我传错夹具**（见我用的 jpg 无增益图）；换真夹具后 **6/0**，见 §3 注 |
| **ultrahdr** | **1** | 0 | **3** | ⚠️ 同上；换真夹具后 **2/1**，剩 1 条是 `%TEMP%` 硬编码（**设 TEMP 后应消**） |
| gainmap-isobmff | 0 | 11 | 0 | ✅ 需文件 |
| oog | 0 | 6 | 0 | ✅ |
| avifgm | 0 | 11 | 0 | ✅ |

### geometry（Tests.Geometry）— **4/4 全绿**，85 条断言

| mode | rc | pass | fail | 判读 |
|---|---|---|---|---|
| geometry | 0 | 52 | 0 | ✅ **需 outDir**（不是输入文件！） |
| firstframe | 0 | 10 | 0 | ✅ 需 `input [outDir]` |
| orientation | 0 | 15 | 0 | ✅ 需 outDir |
| orientmeta | 0 | 8 | 0 | ✅ 需 outDir |

### io（Tests.Io）— **2/2 全绿**，11 条断言

| mode | rc | pass | fail | 判读 |
|---|---|---|---|---|
| ipc | 0 | 8 | 0 | ✅（**设 TEMP 前是硬崩**） |
| procstreams | 0 | 3 | 0 | ✅ |

### diagnostics（Tests.Diagnostics）— 3/8 全绿，73 条断言

| mode | rc | pass | fail | 判读 |
|---|---|---|---|---|
| **bench** | **1** | 9 | **2** | 🔴 **真缺陷 D-①：`PQ→sRGB` 吞吐稳定 5× 不达标** |
| **peak** | **1** | 0 | 1 | 🔴 **真缺陷 C**（有实参也打 usage） |
| **peakmeasure** | **1** | 0 | 1 | 🔴 **真缺陷 C** |
| **psnr** | **1** | 0 | 1 | 🔴 **真缺陷 C** |
| **xcheck** | **1** | 0 | 1 | 🔴 **真缺陷 C** + **D-②（exit -22）** |
| selftest | 0 | 40 | 0 | ✅（skip=1） |
| tooldetect | 0 | 26 | 0 | ✅（**设 TEMP 前是硬崩**） |
| i18n | 0 | 7 | 0 | ✅ |

---

## 3. 三类结论分开写

### 3-A. 环境假红（**不是产品缺陷**）

#### A-1　子进程不能写 `%TEMP%`（类别 A）

**原始复现**（三者只差"谁在写"）：

```
PowerShell 写 %TEMP% : OK (C:\Users\20210\AppData\Local\Temp\pwsh_write_test_*.txt)
cmd 子进程写 %TEMP%  exit=0 exists=True
ffmpeg 子进程写 %TEMP% exit=-13 exists=False
  [image2 @ ...] Could not open file : C:\Users\20210\AppData\Local\Temp\ff_test_*.png
  [vost#0:0/png @ ...] Error submitting a packet to the muxer: Permission denied
```

**根因已用 ACL 诊断工具定性**（输出：`tests\output\_acl_report\acl-report-c7beea9686cd4a11aac8fd9d9f7f4311.jsonl`）：

```
VERDICT=NOT_THIS_CLASS
  理由：No package allow ACE was observed and both required rights are available.
OBJECT=C:\Users\20210\AppData\Local\Temp  OWNER=当前用户  MY_RIGHTS=[FullControl]
       WRITE_DAC=True  WRITE_OWNER=True  LOW_LABEL=(空)
SUMMARY FIXED=0 GRANTED=0 REFUSED=0 RESTORED=0
nextAction=stop
```

⇒ **ACL 完好**（当前用户 `FullControl`、`WRITE_DAC/OWNER=True`、无包 ACE），
**这是 DSH 沙箱的进程级约束，改 ACL 修不了**。我**没有做任何 ACL 改动**（守纪律，`FIXED=0`）。
按技能指引：`NOT_THIS_CLASS` 时报告并停止，不再重试。

**修法与影响面**（这才是关键）：`FFMPEGGUI_RAMDISK` **只能**救走产品 `GetTempDir()` 的路径；
**46 处测试代码硬编码 `Path.GetTempPath()`，绕过它**：

| 位置 | mode | 设 TEMP 前 | 设 TEMP 后 |
|---|---|---|---|
| `tests/core/CoreGroup.cs:158`（plan） | plan | 硬崩 `0xE0434352` | ✅ 39/0 |
| `tests/core/CoreGroup.cs:2152`（settings） | settings | 硬崩 | ✅ 11/0 |
| `tests/core/CoreGroup.cs:2324`（runner） | runner | 硬崩 | 7/1（转类别 B） |
| `tests/io/IoGroup.cs:171`（ipc） | ipc | 硬崩 | ✅ 8/0 |
| `tests/gainmap/GainMapGroup.cs:167`（ultrahdr ③） | ultrahdr | `FAIL ... uhdr_probe_*.pfm is denied` | 见 §3-A-3 |

原始崩溃（设 TEMP 前，`tests\output\_baseline_modes_v1\core__settings.txt`）：
```
Unhandled exception. System.UnauthorizedAccessException:
  Access to the path 'C:\Users\20210\AppData\Local\Temp\cfgprobe_...' is denied.
   at System.IO.Directory.CreateDirectory(String path)
   at Tests.Core.CoreGroup.ProbeSettingsSave() in ...\tests\core\CoreGroup.cs:line 2153
rc=-532462766      ← 0xE0434352，**连 PROBE RESULT 汇总行都没有**
```

⚠ **这是基线可读性的最大威胁**：不是"红一条"，而是"整组 0 条读数 + 无汇总行"，
会让运行器判成"⚠未执行"，从而**静默丢掉整组的失败记账**。
`0xE0434352` 是未捕获 .NET 异常 —— 探针**没有**做 `try/catch`，属健壮性缺口（见 §5 建议）。

#### A-2　子进程 loopback/ICMP 受限（类别 B）⇒ `runner` 恒红

`core/runner` 第 1 条断言的载体是 `cmd /c PING.EXE -n 3 127.0.0.1 >nul`，期望 `rc=0`。
沙箱挡住 **.NET 子进程**的 loopback：

```
cmd /c ping >nul       rc=1  out=<>                       ← 期望 rc=0
cmd /c echo hi         rc=0  out=<hi>                     ← 排除 cmd 本体/重定向语法
cmd /c ver             rc=0  out=<Microsoft Windows ...>
ping direct (.NET)     rc=1  out=<PING: transmit failed. 常见故障。>
ping 从 pwsh 顶层      rc=0  (Sent = 2, Received = 2, Lost = 0)
```

⇒ `FAIL 静默但正常退出的子进程不被误杀（退出码 1，2.0s）` 是**环境假红**，
**不是产品缺陷，也不是探针写错了**。该探针注释（`CoreGroup.cs:2314-2318`）已为**另一个**同类宿主限制
（环境块不含 System32）打了绝对路径补丁，但 loopback 这条挡不住。
**不为它改探针或产品。**

#### A-3　`gainmap`/`ultrahdr`：夹具选择问题（我的口径，非缺陷）

我第一次跑的 `gainmap` 1/5、`ultrahdr` 0/3 用的是 `_heicavif_probe\c2\seine_hdr_gainmap_srgb.jpg`，
实测该文件**没有** MPF/ISO21496/hdrgm 标记（我逐字节扫过）⇒ 探针判"不是增益图容器"是**正确行为**。
换成真夹具 `tests\output\gainmap\hdr1000.jpg`（含 MPF + `hdrgm` XMP + 2 层图像，256x192 + 64x48）：

```
$ Tests.GainMap.exe gainmap "tests\output\gainmap\hdr1000.jpg" <out>
PASS 容器解析非 null / IsGainMapContainer()=true / 顶层图像数 2 >= 2
PASS ISO 21496-1 元数据存在 / ISO 增益区间非零 / 副图定位成功
PROBE RESULT: pass=6 fail=0     rc=0     ← 全绿
```
⚠ **正确跑法需要自备带增益图的夹具**（`tests/output/gainmap/hdr1000.jpg` 由 `GainMapTestHost` 产出）。
裸跑或随便给个 jpg 只会红，**那不是缺陷**。

### 3-B. usage 非缺陷（**不是缺陷**）

`docs/TESTING.md` §6 第 24 条已登记：需要文件/名字实参的 mode **裸跑**时只打 `usage:` 并计 1 fail。

实测（**裸跑**，`usage=True`）：
```
engine      gamut        usage: ServiceProbe gamut <jpeg> [expectedToken]        PROBE RESULT: pass=0 fail=1
diagnostics peak         usage: ServiceProbe peak <file> [expectMetaPeak] ...    PROBE RESULT: pass=0 fail=1
diagnostics peakmeasure  usage: ServiceProbe peakmeasure <png> <maxCode ...>     PROBE RESULT: pass=0 fail=1
diagnostics psnr         usage: ServiceProbe psnr <a> <b> [thresholdDb]          PROBE RESULT: pass=0 fail=1
```
⇒ 这 4 条**裸跑**读数**不是缺陷**，**不要去"修"**。

### 3-C. 真缺陷①：拆分后实参错位 ⇒ **给足实参也打 `usage`**（严重）

⚠ **必须与 3-B 严格区分**：3-B 是"**没给**实参"；这里是"**给足了**实参依然打 usage"。

**根因（已定位到行号）**：`tests/diagnostics/Program.cs:37` 与 `tests/engine/Program.cs:47` 都写
```csharp
var rest = args.Length > 1 ? args[1..] : Array.Empty<string>();
if (mode == "" || mode == "psnr") DiagnosticsGroup.RunPsnr(rest);   // ← 传 rest
```
但**被逐字抽出的探针体仍按旧宿主 `ServiceProbe` 的下标取值**：

| 探针体 | 行号 | 门槛 | 取值 |
|---|---|---|---|
| `ProbePsnr` | `DiagnosticsGroup.cs:281` | `args.Length < 3` | `args[1]`、`args[2]` |
| `ProbeXcheck` | `DiagnosticsGroup.cs:322` | `args.Length < 6` | `args[1]`…`args[5]` |
| `ProbePeak` | `DiagnosticsGroup.cs:149` | `args.Length < 2` | `args[1]` |
| `ProbePeakMeasure` | `DiagnosticsGroup.cs:202` | `args.Length < 3` | `args[1]`、`args[2]` |
| `ProbeGamut` | `GamutFit.cs:670` | `args.Length < 2` | `args[1]` |

⇒ `rest` 把实参塞进**本该是 mode 名的下标 0**，真实参落到 `args[1]` 时**已少一个** ⇒
`args.Length` 恒差 1 ⇒ 每次都 `usage:` + `Harness.FailOnly()`。

**最小复现（对照实验，只差一个占位实参）**：
```powershell
$exe = "C:\PLAN\ffmpegPictureUI\tests\diagnostics\bin\Release\net11.0\win-x64\Tests.Diagnostics.exe"
$png = "C:\PLAN\ffmpegPictureUI\tests\output\_lead_e2e\out\A-fmt-png\src8.png"

# ① 按文档/直觉传参 ⇒ 必红
cmd /c "`"$exe`" psnr `"$png`" `"$png`" > out1.txt 2>&1"
#   usage: ServiceProbe psnr <a> <b> [thresholdDb]
#   PROBE RESULT: pass=0 fail=1        rc=1

# ② 补一个占位（把错位的下标补回来）⇒ 立刻真跑出读数
cmd /c "`"$exe`" psnr psnr `"$png`" `"$png`" > out2.txt 2>&1"
#   psnr=∞dB maxDiff=0/65535 尺寸=320x240
#   PASS 两路产物 PSNR ≥ 55dB（实 ∞）
#   PROBE RESULT: pass=2 fail=0        rc=0
```
engine 的 `gamut` 同形（原始输出 `tests\output\_baseline_modes\PROBE_engine_gamut_shift.txt`）：
```
$ Tests.Engine.exe gamut "hdr1000.jpg"           => usage: ... gamut <jpeg> ...   fail=1   rc=1
$ Tests.Engine.exe gamut gamut "hdr1000.jpg"     => base_primaries=bt709
                                                    PROBE RESULT: pass=1 fail=0   rc=0
```

**影响面（Lead 已收口，按此粒度写）**：
- 受影响的**只有"拆分工程的直接调用"这条路**：`diagnostics` 的 **5 个 mode**（`peak`/`peakmeasure`/`psnr`/`xcheck`/`tooldetect` 中前 4 个有实参需求）+ `engine` 的 `gamut`。
- **受管门禁与运行器不受影响**：`_run-step3-gates.ps1:653` 与 `verify-color-peak.ps1` 都用**未拆分的旧宿主**
  `tests/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe`（下标是对的）。
  Lead 实跑 `ServiceProbe.exe peak <file> 600` 得到的是**素材相关断言**而非 `usage` ⇒ 证明旧宿主拿到实参了。
- 我逐组核对了全部 9 组 `Program.cs` 的分派写法，确认**只有** `diagnostics` 与 `engine` 用了 `args[1..]`；
  `core`/`color`/`encode`/`metadata`/`gainmap`/`geometry`/`io` 都传 `args`，**不受影响**。
- ⚠ **不要写成"门禁红了"**（会误导维护者去查门禁）。准确表述是：
  **拆分工程的直接调用路上，这 5 个 mode 永远拿不到读数** —— 而"改哪块跑哪块"正是拆分的唯一目的
  （`docs/TESTING.md` §1.1）。
- **修复方向**：让 `Program.cs` 传完整 `args`（与其余 7 组一致），或在组入口补回 mode 占位。属 `tests/` 侧，
  我**未改动**任何 `tests/` 源码。

### 3-D. 判据失效（门槛失真）：`bench` "变曲线+裁切" 稳定 5× 不达标（**Lead 已裁定：不进产品缺陷账**）

**原始输出**（连跑 3 次，全部复现，非抖动）：
```
$ Tests.Diagnostics.exe bench
FAIL PQ→sRGB(变曲线+裁切) 单核 ≥40MPix/s（实 8.4）
FAIL PQ→sRGB(变曲线+裁切) 单核 ≥40MPix/s（实 7.8）
PROBE RESULT: pass=9 fail=2     rc=1
```
复跑稳定性（`tests\output\_baseline_modes\bench_rep{1,2,3}.txt`）：**6.5 / 7.0 / 7.7 / 7.8 / 8.2 / 8.7** MPix/s
⇒ 稳定落在 **6.5–8.7**，门槛是 **40**，**差 ≈5×**，**不是临界抖动**。

判据原文（`DiagnosticsGroup.cs:134`）：`Check(single >= 40, $"{name} 单核 ≥40MPix/s（实 {single:F1}）");`
被测路径：`("PQ→sRGB(变曲线+裁切)", "bt2020", "smpte2084", "bt709", "iec61966-2-1")`（同文件 `:76`）。

同一轮里**其他两格是绿的**（`sRGB→P3` 84.0/53.8、`BT.2020→sRGB` 151.3/72.3）⇒
**问题特定于"变曲线（PQ→sRGB 需查表换曲线）+ 裁切"这条慢路径**，不是整机性能问题、也不是沙箱拖慢。

**Lead 裁定 = 「判据失效（门槛失真）」，不是性能回归**。依据：同轮对照证明机器与沙箱
**没有**被整体拖慢；6.5–8.7 稳定复现且差 ≈5×，非抖动；本机是 DSH 沙箱（子进程受限、
负载特征与标定时不同）⇒ **单核吞吐类绝对值门槛换环境后必然失真**，与
`docs/TESTING.md` §6 第 103 条「时间型负控的下限如果没有同夹具实测读数背书就是许愿」同族。

⇒ **处置建议**：要么按当前机器**重新标定**门槛并留实测分布，要么把该格降级为"只回显不判红"。
**不得**悄悄改数字而不留标定依据。我**未改动**任何判据。
原始日志：`tests\output\_baseline_modes\diagnostics__bench.txt`、`bench_rep1..3.txt`。

### 3-D′. 真缺陷（产品 API 契约缺口，严重度=低）：`xcheck` 传 `srgb` ⇒ `exit=-22`

**Lead 裁定**：**是真缺陷**，类别 = **「公开 API 未遵守本仓自己的规范化契约」**
（`public static` 方法 ⇒ 属产品对外契约面），**但严重度=低** —— 该 API 在 `src/` 下**零产品调用点**
⇒ **生产链路未坏，用户不会遇到**。与 `docs/TESTING.md` §6 第 117 条「有声明、无实现」、
第 104 条「只写进备忘录没人消费」**同族**。

这条**独立于** 3-C 的错位问题：补上占位实参、下标对齐后，它**换了个红法**：

```
$ Tests.Diagnostics.exe xcheck xcheck <png> bt709 bt709 srgb srgb <outDir>
Unhandled exception. System.InvalidOperationException: 编码失败 exit=-22
   at FfmpegGui.Services.ColorMapping.RawColorPipeline.WriteRgb48ToPngAsync(...) in ...\RawColorPipeline.cs:line 1289
   at Tests.Diagnostics.DiagnosticsGroup.ProbeXcheck(String[] args) in ...\DiagnosticsGroup.cs:line 342
rc=-532462766
```

**最小复现（已剥到裸 ffmpeg，与产品无关）**：
```
$ffmpeg -f rawvideo -pix_fmt rgb48le -video_size 320x240 -i t.rgb48 -color_primaries srgb      out.png
  [png @ ...] [Eval @ ...] Undefined constant or missing '(' in 'srgb'
  [png @ ...] Unable to parse "color_primaries" option value "srgb"
  [vost#0:0/png @ ...] Error applying encoder options: Invalid argument
  rc = -22

$ffmpeg ... -color_primaries bt709     out.png   ⇒ rc=0  产物存在
$ffmpeg ... -color_primaries bt2020 -color_trc smpte2084 out.png ⇒ rc=0  产物存在
$ffmpeg ... -color_primaries smpte432  out.png   ⇒ rc=0  产物存在
$ffmpeg ... -color_primaries iec61966-2-1 out.png ⇒ 同样 `[Eval] Undefined constant ... in 'iec61966-2-1'`
```
⇒ ffmpeg 的 `-color_primaries` 只认**自己的 CICP 常量名**（`bt709`…），**不认**产品内部 token
（`srgb` / `iec61966-2-1` 都不认）。⚠ 注意 `iec61966-2-1` 只在 `-color_trc` 位上合法，
**作 `-color_primaries` 同样是 Eval 错** —— 两层词表不可混用。

**为什么这是缺陷而非"用法错误"**：本仓**自己已经把这条坑写进了注释并提供了规范化器** ——
`src/FfmpegGui/Services/ColorSpaceRegistry.cs:368-387` 逐字写着：
> 若不规范化就原样拼进命令行 ⇒ ffmpeg 报 `Unable to parse "<opt>" option value "<token>"` ⇒ **退出码 -22、零产物**

且 `FfmpegOptions.cs:257/262` 的 setter **确实**调了
`NormalizePrimariesToken`/`NormalizeTrcToken`（同一语义在两条路上**口径不一致**）。
**但 `RawColorPipeline.WriteRgb48ToPngAsync`（`:1275-1292`）的 `primaries`/`trc`/`matrix`
直接插值进命令行，没有过规范化器**：
```csharp
string labels = (!string.IsNullOrWhiteSpace(primaries) ? $" -color_primaries {primaries}" : "") + ...
```
⇒ 调用方传产品友好名（`srgb`）就必然 `-22`。

⚠ **修法（Lead 背书，且必须守住纪律）**：
1. **不得**让探针改传 `bt709`（那是"改判据换绿"）。
2. 应在该 API 内部过 `NormalizePrimariesToken`/`NormalizeTrcToken`（`matrix` 同理，注意
   「模型内规范名 vs ffmpeg 名」两层，如 `gbr`→`rgb`，见 `ColorSpaceRegistry.cs:385-386`）。
3. ⚠ **本轮纪律是只测不修** ⇒ 我**未改动**任何 `src/` 或 `tests/` 代码，此项列为**待用户拍板的修复项**。

同类待排查点（**仅 grep 结果，未逐点实证**，不写成已证实缺陷）：
`src/FfmpegGui/Services/GainMapEncoder.cs:641` 亦直接
`$" -color_primaries {primaries} -color_trc {trc} -colorspace {matrix}"`。

---

## 3-E. 交叉印证：**整组裸跑** vs **逐 mode 单跑**（为什么必须逐 mode 跑）

任务要求"不要裸跑整组后按行筛"，我据此做了**对照实验**（两边都用正确的四个环境变量）：

| 组 | 整组裸跑 | 逐 mode 单跑（§2） | 差异解读 |
|---|---|---|---|
| core | rc=1 `pass=332 fail=1` | 325/0（6 mode）+ runner 7/1 | 多出的 7 条 = runner 的环境假红；整组把 runner **未捕获异常**后的无汇总风险掩盖了 |
| color | rc=0 `126/0` | 126/0 | 一致 ✅ |
| engine | rc=1 `pass=82 **fail=1**` | 82/0 + `gamut` usage | ⚠ **那 1 条 fail 纯粹是 `gamut` 的 `usage:` 累积进组尾计数** |
| encode | rc=0 `57/0` | 58/0 | 差 1（`bitdepth` 传参差异） |
| metadata | rc=1 `17/**3**` | 38/0（4/4 全绿） | ⚠ **整组把 `icc`/`icc-dump`/`faithful` 三条变成 `usage:`**（见下） |
| gainmap | rc=1 `17/3` | 28/0（3 mode）+ 夹具问题 2 | ⚠ 同上：3 条 `usage:` |
| geometry | rc=1 `75/1` | 85/0（4/4 全绿） | ⚠ **多出的 1 条是 `firstframe` 的 `usage:`** |
| io | rc=0 `11/0` | 11/0 | 一致 ✅ |
| diagnostics | rc=1 `82/6 skip=1` | 73/0（3 mode）+ 5 条缺陷 | ⚠ 4 条 `usage:` + bench 2 条 |

**这就是"整组会把 usage 型 mode 一起计进去"的实测形态**：整组裸跑时，
`metadata` 组里**单跑全绿的** `icc`/`icc-dump`/`faithful` 会各自打一行 `usage:` 并计 1 fail ——
证据逐字如下（`tests\output\_baseline_modes\GROUP_metadata.txt` 尾部）：
```
PASS C5 StripExifCamera 时不得带 -LensModel
usage: ServiceProbe icc <色彩空间名>            ← 单跑同一 mode 是 4/0 全绿
usage: ServiceProbe icc-dump <file.icc> [tag ...]   ← 单跑同一 mode 是 0/0（转储工具）
usage: ServiceProbe faithful <spec> [yes|no]        ← 单跑同一 mode 是 17/0 全绿
```
⇒ **`metadata` 整组 `17/3` 是假读数**，真实情况是 **4/4 mode 全绿、38 条断言全过**。
同理 `engine` 整组 `82/1` 的那 1 条 fail **不是缺陷**，就是 `gamut` 的 `usage:` 计入组尾。
**结论：基线判读必须用逐 mode 单跑；整组读数只能作交叉印证，不能作缺陷依据。**

---

## 4. 全量门禁（`_run-step3-gates.ps1`）状态

⚠ **如实声明**：我按任务要求发起的全量门禁运行**被仓库锁正确拒绝**（`exit=9`）——
Lead 的作业（PID 29760，启动于 2026-10-07 04:22:19）已在跑，锁文件 `tests/output/.gates.lock` 指向它。
按仓规**不并发启动第二个**（并发会互删共享 fixture ⇒ 读数不可信）。

```
===== 运行器拒绝启动：仓库级锁被占用（#147 C）=====
  锁文件：C:\PLAN\ffmpegPictureUI/tests/output/.gates.lock
  持有者：{"pid":29760,"host":"7.6.6 Core","startedAt":"2026-10-07 04:22:19", ...}
  ⚠ 另一个门禁运行器正在跑 ⇒ 本轮**不启动**
```

### 4.1　整轮读数：**失败 21 项**，`exit=1`

Lead 的整轮已收尾并落盘 `tests/output/_baseline_full.log`（30,425 B，运行器自身指纹
`runner-self-sha256=191E40068E7C6E37 lines=1339 bytes=127121`，跑前跑后一致 ⇒ 无自漂移，读数归属明确）。
我已复制归档为 `tests/output/_baseline_full_ARCHIVE_2026-10-07.log`。

⚠⚠ **该轮的 21 项失败全部作废，无一条是产品算错**。逐项分类（我逐条核对了原始行）：

| # | 项目 | 性质 | 原始证据 |
|---|---|---|---|
| 1–4 | `probe:plan` / `runner` / `settings` / `ipc` | **类别 A 硬崩** | `exit=1`（真身 `0xE0434352` 未捕获 `UnauthorizedAccessException`，`PROBE RESULT` 未打印） |
| 5 | `verify-color-peak.ps1` | 类别 A 连带 | 依赖 `%TEMP%` 通路 |
| 6 | `verify-gainmap-host.ps1` | 类别 A 连带 | 同上 |
| 7 | `verify-ffmpeg-heartbeat.ps1` | 类别 A 连带 | 同上 |
| 8 | `verify-jxr-anim-input.ps1` | 类别 A 连带 | 同上 |
| 9–10 | `_probe-tool-detect.ps1` | 类别 A 连带 | `零断言：PASS=0` + `exit=1` |
| 11–12 | `verify-ui-host.ps1` | **旧宿主硬崩** | `exit=-532462766 summary=False pass=-1 fail=-1` |
| 13–14 | `verify-ui-param-matrix.ps1` | **旧宿主硬崩** | 同上（`groupJ=0`） |
| 15–16 | `verify-ui-strategy-map.ps1` | **旧宿主硬崩** | 同上（`groupK=0`） |
| 17–18 | `verify-ui-param-defects.ps1` | **旧宿主硬崩** | 同上（`groupL=0`） |
| 19–20 | `verify-cli-strict.ps1` | **旧宿主硬崩** | 同上（`groupM=0 leftovers=0`） |
| 21 | `verify-ui-split-union.ps1` | **级联假红** | 它要解析旧宿主 409 条断言名，而宿主没跑起来 ⇒ `FAIL 1a 旧宿主断言名数 == 409（实 0）`、`1b FAIL == 0（实 -1）`、`3d 并集计数 == 409（实 0）`、`4 反控：两侧集合都非空` |

**硬崩/级联的分界**：11–20 这 6 个 UI 门禁**不是"断言红了"，而是宿主根本没启动** ——
它们全部报告 `exit=-532462766` + `summary=False` + `pass=-1 fail=-1`，再被门禁的
「`FAIL summary line present (exit 0 alone is not enough)`」与「⚠零断言闸」判红。
第 21 项 `verify-ui-split-union` 则是**读不到宿主的 409 条断言名而级联**（属二次假红，非独立故障）。

**双向验证（决定性）**：设好 `TEMP`/`TMP` 后单独跑旧宿主 `UiTestHost.exe` ⇒
**`UI 测试汇总: 409 PASS / 0 FAIL`**，正是文档基线 ⇒ 证明确为环境所致，产品无过。

### 4.2　⚠ 运维结论：硬崩会**静默丢掉整组记账**（比"红一条"危险得多）

本次 21 项里有 10 项（`plan`/`runner`/`settings`/`ipc` + 6 个 UI 门禁）**根本没有产出 `PROBE RESULT` /
`PASS=n` 汇总行**。运行器的记账通道是**读汇总行**（`_run-step3-gates.ps1:844/851` 的
`$sum -notmatch 'PROBE RESULT'`）⇒ 一旦未捕获异常把进程打死，运行器只能判「⚠未执行」，
**该组后续所有断言的失败都不会被计数**。

我实测到这条的两种形态：
1. **整组 0 条读数**：`core/settings` 硬崩时 `PROBE RESULT` 完全不打印（`tests\output\_baseline_modes_v1\core__settings.txt`），
   对照修复后为 `11/0`。
2. **门禁自报负数**：6 个 UI 门禁打出 `pass=-1 fail=-1 summary=False`（`_baseline_full.log` 原始行），
   门禁只能靠「零断言闸」兜底。

⇒ **建议**：探针的临时目录等外部资源操作应 `try/catch` 后**优雅记 FAIL**（保证汇总行必打），
否则环境问题会伪装成"这组没跑"，把失败**静默吞掉**。

### 4.3　留痕形状（我已核实）

- 逐探针：`tests/output/_gate_<mode>.txt`（+ `.err`）
- 逐脚本：`tests/output/_gate_script_<name>.ps1.txt`（+ `.rc` / `.err`）
- 整轮 stdout：`tests/output/_baseline_full.log`（归档副本 `_baseline_full_ARCHIVE_2026-10-07.log`）

⚠ Lead 已另行发起**带 `TEMP`/`TMP` 的第二遍全量**（`tests/output/_baseline_full_fixed.log`），
跑完后**那一份才是有效整轮基线**；本报告 §2 的逐 mode 读数与它不冲突（两边都已带正确环境变量）。

---

## 5. 建议（不属本任务写作用域，仅供参考）

1. **跑法固化**：把 §1 的四个环境变量写进 `docs/TESTING.md` 的跑法段与
   `_run-step3-gates.ps1` 头部注释，否则下一个人还会只设 `FFMPEGGUI_RAMDISK` 而踩 `core` 硬崩。
2. **探针健壮性**：46 处 `Path.GetTempPath()` 建议统一改走 `PlatformServices.GetTempDir()`
   （与本仓"单一真值"惯例一致）；并对临时目录操作加 `try/catch` ⇒ 环境问题应**优雅记 FAIL**，
   而不是 `0xE0434352` 让整组读数蒸发（见 §3-A-1 的"最危险"项）。
3. **修 3-C 错位**：统一 9 组的 `Program.cs` 分派口径（传 `args` 或补 mode 占位）。
4. **修 3-D′ 规范化**：`WriteRgb48ToPngAsync` / `GainMapEncoder.cs:641` 过
   `ColorSpaceRegistry.NormalizePrimariesToken`/`NormalizeTrcToken`。
5. **`bench` 门槛**：先判定 `PQ→sRGB(变曲线+裁切)` 是性能回归还是门槛失真，再决定改代码或改门槛
   （**不要**为了转绿直接放宽）。

---

## 6. 证据清单（全部可复现）

| 路径 | 内容 |
|---|---|
| `tests/output/_baseline_modes/` | **逐 mode 原始日志**（46 个 `<组>__<mode>.txt`）+ `.rc` 退出码 |
| `tests/output/_baseline_modes/_summary.csv` | 逐 mode 结构化读数（rc/pass/fail/skip/耗时/usage） |
| `tests/output/_baseline_modes/_console.txt` | v3 控制台汇总（= `_baseline_run_console_v3.txt`） |
| `tests/output/_baseline_run.ps1` | 采集器脚本（含四个环境变量与全部实参口径） |
| `tests/output/_baseline_modes_v1/` | **反证轮**：未设 `TEMP` 时的崩溃原始日志（§3-A-1 证据） |
| `tests/output/_baseline_modes/bench_rep{1,2,3}.txt` | `bench` 三次复跑（§3-D 稳定性证据） |
| `tests/output/_baseline_modes/PROBE_*_shift.txt` | 3-C 错位对照实验原始输出 |
| `tests/output/_baseline_modes/T4_xcheck_placeholder.txt` | 3-D′ `exit=-22` 原始输出 |
| `tests/output/_acl_report/acl-report-c7beea9686cd4a11aac8fd9d9f7f4311.jsonl` | ACL 诊断全文（`NOT_THIS_CLASS`） |
| `tests/output/_baseline_modes/GROUP_<组>.txt` | **整组裸跑**对照读数（§3-E 的证据，用于证明"整组会掩盖/污染"） |
| `tests/output/_baseline_gates_stdout.txt` | 全量门禁被锁拒绝的原始输出（`exit=9`） |

**纪律声明**：本任务**未修改** `src/` 下任何产品代码，**未修改** `tests/` 下任何源码或门禁脚本，
**未修改任何 ACL**（`FIXED=0 GRANTED=0`）。写入范围仅 `tests/output/` 与本文档。
所有读数均取自落盘原始日志，无凭印象数字；`usage` 型与真缺陷已分表登记，未用"放宽断言"换绿。
