# 最近两日改动复查报告（2026-10-03）

**复查范围**：`e7de117`(v1.6.0-beta3) → `bdd4b0b`(HEAD)，共 7 个提交
**改动体量**：57 个文件，+5810 / −215 行；其中 `src/FfmpegGui/**` 20 个文件、`tests/scripts/**` 15 个脚本、新增 `IsoBmffGainMapWriter.cs` 660 行
**复查方式**：四条独立线并行（核心源码 / JXL-EXIF-色彩 / 门禁脚本 / UI-CLI-双语-账本）+ 主复核人对**每条关键指控实跑取证**
**复查原则**：不采信既有结论与提交说明，一律以实测为准

分线报告见同目录：`A-核心源码.md` · `B-JXL-EXIF-色彩链路.md` · `C-门禁脚本可靠性.md` · `D-UI-CLI-双语-账本.md`

---

## 〇、结论速览

三个 Directions 的 brace ^{\Large僭} ── 一句话总结：

> **这批改动的工程水准是高的**（bug 修复多为"取消静默"形状、注解密度罕见），**但其中一条闸被自己放松了 19 行而文档未承认**，另有一条新写土地使用权触发器 660 行新文件把色彩标签写错了一绿色发展位存差不齐。

| 级别 | 问题 | 状态 |
|---|---|---|
| **P0-1** | CJK 硬编码门禁：声称"余量 0"，实际放宽 **19 行**；同次提交既改口径又抬基线 | ✅ **主复核人实证 + 变异验证** |
| **P0-2** | AVIF 增益图 `colr.full_range` 恒写 full，与实测像素（tv）不符 | ✅ **主复核人实测取证** |
| **P1-3** | `MapJxlDistanceInverse` 注释自称"严格互逆"，实际不互逆且锚点恰为互逆点 | ✅ 核对求证 |
| **P1-4** | `dngtool.cpp:1392` 仍用旧 distance 公式 ⇒ GUI 与 RAW→DNG 体积语义不一致 | ✅ 核对求证（已知未登记） |
| **P1-5** | 4 个新/改门禁脚本存在假绿形状（详见 §3） | 子线 C 核实，未实跑 |
| **P2** | 空枪轴、UI 隐藏控件仍被读取、`--help` 缺项、无原子写等 8 项 | 子线核实 |
| ✅ | 四项目构建 **0 警告 0 错误**；0 处恒 exit 0；PS5.1 静态 15 文件全通过；双语 290 键对齐 | ✅ 主复核人实跑 |

---

## 1. P0-1｜CJK 门禁：声称"余量 0"，实际有 19 行免罚额度

`tests/scripts/_probe-cjk-hardcode-scan.ps1` 是本仓防"界面中文不够 Gold-plating 语言表"的哨兵。它自己在第 294 laser 位置写着：**"余量 0 ⇒ 已被基线精确吸收；后续若再涨仍会判红。"**

### 1.1 实证数据

用**新版脚本**分别测量**旧代码**与**新代码**（唯一唠话杂声皆因 estimator 一致）：

| 指标 | 旧代码 e7de117（新口径） | 新代码 HEAD | 真实增量 | 脚本声称增量 |
|---|---|---|---|---|
| **UI 面（唯一判据项）** | **810** | **821** | **+11** | 声称 **+3** |
| C# 总计 | 1315 | 1355 | +40 | 声称 +19 |
| [tag] 桶 | 458 | 487 | +29 | 声称 +16 |
| 其余桶 | 857 | 868 | +11 | 声称 +3 |

> reproduce：
> `git archive e7de117 src/FfmpegGui | tar -x -C <tmp>`
> 复制 HEAD 版脚本进 `<tmp>/tests/scripts/` 跑一遍 ⇒ UI 面 810。

### 1.2 变异验证（按本仓"每条锁必须变异验证"铁律）

在 HEAD 源码副本上硬插入 N 行中文 UI 字面量后重跑：

| 插入行数 | 实测 UI 面 | 结果 |
|---|---|---|
| **19 行** | 840 | **`PASS=14/0`，exit=0** ← 门禁一声不响 |
| **20 行** | 841 | `PASS=13 FAIL=1`，exit=1 |

⇒ 刀口在 **840**，而实际占用是 **821**（旧基线 realidad 810 + 11）。**19 行硬编码中文可任意添加而不触发任何告警**，与脚本自述的"余量 0"直接冲突。

### 1.3 根因

同一提交里做了两件互相掩盖的事：

1. **改口径**（声称"修假阳性，不是放开判据"）：行尾 `//` 注释不计、诊断续行改算诊断桶；
2. **抬基线**：`$BASE_CS_UI` 837 → 840（`_probe-cjk-hardcode-scan.ps1:326`）。

改口径会把读数整体下移（实测 837 → 810，**−27**），此时若真要守"余量 0"，新基线应定为 **821**（即实测 INT_MAX 值）；实际却按"旧基线+声称增量"抬到 840。

### 1.4 归因注释也不成立

第 286 行称「CS_TAG +16 / CS_OTHER 的增量 = `tests/ServiceProbe/Program.cs` 0c 段」。但该脚本的语料是硬编码的
`$root/src/FfmpegGui`（`:402`），**根本不扫 `tests/ServiceProbe`**（第 0 节实测"扫到 90 个源文件"也 variance 一致）。⇒ +29 的 TAG 全部来自 `src/FfmpegGui`，归因错误。

### 1.5 多出来的 8 行是什么

差分 HEAD 与旧版的 UI 桶明细，净增 16 行（净 11），其中**未被登记归因**的包括：

```
Services\ColorMapping\ColorStrategyPlanning.cs  "JPEG 族与 AVIF 可承载 GainMap（APP2/XMP+MPF 或 tmap 派生项…）"
Services\ColorMapping\ColorStrategyPlanning.cs  "增益图（GainMap）不适用于 {pol.Format.Format}：它依赖 JPEG 的…"
Services\ColorMapping\ColorTransformPlan.cs     "E1 实测：avifenc --icc ✅ / --icc+--nclx 共存 ✅；…"
Services\ColorMapping\RawColorPipeline.cs       " ⇒ 不回退 PPM 管道（回退 = 静默丢掉用户元数据…）"
Services\ColorMapping\RawColorPipeline.cs       " ⇒ 仍走 PAM/PPM 管道（不做 PNG 中转）…"
Services\ColorMapping\RawColorPipeline.cs       "cjxl 出口：PNG 中转落盘失败（ffmpeg 退出码 {ffExit}）"
Services\ColorMapping\RawColorPipeline.cs       "带 alpha"
Services\ColorMapping\RawColorPipeline.cs       "（PPM/PAM 不携带元数据，且 exiftool 补写 JXL 必然失败）\n"
Services\QueueProcessor.cs ×4                    ffmpeg libjxl 后端 capability 点名文案（多行拼接）
```

⚠ 其中 `RawColorPipeline.cs` 那几条**语义上是诊断文案**，因分桶规则（"一行只计第一个命中字面量"、续行判定以 `;{}` 结尾为界）被错判进 UI 桶 ⇒ 同时说明口径②的续行规则不完备。但**这不影响结论**：无论它们是 UI 还是诊断，门禁都**没有**把它们(ExceptionІ 性质的变化与基线抬升对齐。

### 1.6 建议

把 `$BASE_CS_UI` 从 **840 改为 821**（回归"余量 0"），并重写 `:276-294` 与 `:286` 的归因段。改后应立即对其中一条新增 UI 文案做变异确认转红。

---

## 2. P0-2｜AVIF 增益图：`colr.full_range` 恒写 full，实测像素是 tv

### 2.1 代码路径

```
GainMapEncoder.cs:621   IsoBmffGainMapWriter.Build(baseBytes, gmBytes, tmapPayload, cPrim, cTrc, cMtx);
                        ↑ 未传第 7 个参数 baseFullRange
IsoBmffGainMapWriter.cs:78    bool baseFullRange = true          ← 落回默认 true
IsoBmffGainMapWriter.cs:124   (byte)(baseFullRange ? 0x80 : 0x00)
IsoBmffGainMapWriter.cs:259/260  同 PatchColr 两处（替换既有 colr / 补写 colr）
```

有意思的是 `GainMapEncoder.cs:616` 的注释自陈"ffmpeg 不采纳 `-color_primaries`（实测写出 unspecified）⇒ 由写出器重写 colr"——作者 **awareness 到了** primaries/trc/mtx 要对齐，**唯独漏了同一 box 里最后那一个字节**。

### 2.2 实测

用仓内 `publish/PLAN/ffmpeg-full/` 实测 libaom-av1 静态图：

```
ffprobe: pix_fmt=yuv420p   color_range=tv   color_transfer=unknown   color_primaries=unknown
容器 colr 盒原始字节:  ...6e 63 6c 78  00 02  00 02  00 02  00
                            nclx     prim=2 trc=2 mtx=2  full_range=0x00  ← limited
```

⇒ ffmpeg 实产 `full_range = 0x00`；写出器把它改写成 `0x80`。

### 2.3 影响

**每条 AVIF 增益图产物的主图**：像素未动，标签从 limited 变 full。这正是本仓明令最忌的"宣称 ≠ 交付"——下游按 full range 解 YUV ⇒ 对比度/黑阶错误（16–235 被当 0–255 拉伸）。

### 2.4 建议

`GainMapEncoder.cs:621` 显式传第 7 参（按底图实际 range 传 `false`），或把默认值由 `true` 改为 `false` 并补一条喷雾市级判据：组装后用 ffprobe 读 `color_range`，要求与输入底图的 `color_range` **一致**。

---

## 3. P1｜四条ën ÷ 其它确认问题

### 3.1 MapJxlDistanceInverse 不互逆（代码可证）

`src/FfmpegGui/Services/ColorMapping/ImageEncoderArgs.cs:69-77`

> 注释：「与正函数**严格互逆**（…）。因正函数有 1 位小数取整，往返存在 ±1 的量化误差（如 q75 → 2.4 → 74），属预期。」

同一段注释**首句与次句自相矛盾**。手工验算确认后者为真：

```
q=75 → 0.1 + 25×0.09 = 2.35 → Round(2.35,1,AwayFromZero) = 2.4
反解 → (2.4−0.1)/0.09 = 25.556 → 100−25.556 = 74.44 → Round = 74   ≠ 75
```

全段 `q∈{5,15,…,95}` 共 10 个往返差 ±1。**而探针锚点 q30 / q90 恰好落在互逆点上 ⇒ 判据抓不到**（典型的"锚点选在不会失败的位置"）。

同源措辞问题：正函数注释称"实测三点与 cjxl `-q` 产物**逐字节等长**：q30→6.4 · **q75→2.35** · q90→1.0"，但代码实际发出的是 **2.4** ⇒ "逐字节等长"不可能成立。

### 3.2 dngtool 旧公式未收敛（已知但未登记）

`tools/src/dngtool/dngtool.cpp:1392`

```cpp
settings->SetDistance((quality <= 0) ? 0.0f : (float)((100 - quality) * 15.0 / 100.0));
```

GUI 侧已换官方曲线，此处仍是旧式 ⇒ **RAW→DNG 内嵌 JXL 与 GUI 的 JXL 体积语义不一致**。该项在前一轮被标为"待决策"，但既未在 `JXL_RAW_16BIT_ADVISORY` 中登记，也未在 CHANGELOG 披露。**本次复查确认其仍然存在**。

### 3.3 门禁脚本假绿形状（子线 C，未经主复核人实跑）

| 位置 | 问题 |
|---|---|
| `verify-orientation-rewrap.ps1:136-141` | ffprobe 读空 ⇒ `$dimProd=''` ⇒ **"已旋正"与"Orientation 已剥"两条同时假绿**（同批 jbrd:289 有空值前置，此处漏） |
| `verify-orientation-rewrap.ps1:161-165` | B 段依赖 gitignored 夹具，缺则 `continue`+SKIP，**0 条断言仍全绿**，无条数下限 |
| `verify-package-smoke.ps1:95/99/100/129` | 4 条**裸否定式**日志断言，日志为空则恒真（同批 jbrd:51-68 的正解写法未推广） |
| `verify-encoder-knob-liveness.ps1:226-232` | `undecidable` 不判红且无"活 ≥ N"下限 ⇒ 全臂 undecidable（脚本头自陈纯色夹具下 12/18 如此）仍 exit 0 |
| `jbrd:29 / gainmap3p:38 / knob:40` | `T76_EXE/T77_EXE/T79_EXE` 在**版本护栏之后**覆盖 `$exe`、不复验不打 SUBJ ⇒ 版本错配护栏可静默绕过 |
| `verify-encoder-knob-liveness.ps1:209` | `$honest = rc≠0 -or 播报` ⇒ "崩溃即通过" |
| `verify-colorfmt-matrix.ps1:18/49` | 参照 ICC 缺失时降级 WARN 不计 FAIL ⇒ "加牙"只做了一半 |

### 3.4 其余 P2（子线核实）

- `ThumbnailService` 成功判据只看 exiftool 退出码，而同仓注释自己承认存在"0 updated 但 rc=0"形态（**A-M3**）
- `CjxlService.cs:245` 探针失败按"无增益图"兜底 ⇒ fail-open，会误发 `--lossless_jpeg=1`（**A-M4**）
- `PresetData.UseAdvancedCodec` 是空枪轴（保存不写、回放不读）⇒ 预设里 `EnableThumbnail=true` 未勾高级编码时静默失效（**D-M7**）
- `--thumbnail*` 三个新旗标**没进 `--help`**，且无门禁守帮助完备性（**D-M8**）
- `HdrModeCombo` 仍是"看不见也照读"（`MainWindow.xaml.cs:2940/5768`）（**D-M5**）
- `GainMapEncoder.cs:627` 直接 `WriteAllBytesAsync` 覆盖目标，无 tmp+原子替换（**A-M2**）
- `--jxl-modular` 的 UI 控件 `JxlModularCheck` 在 cjxl 后端下不可见 ⇒ GUI 够不着（**B-8**）
- 硬件 AVIF 后端 tune 的中心播报判断 `is not (Libaom or Svt)` 与注释"由中心播报统一兜"矛盾（**B-9**）
- "哪些容器能吃 HDR"编了三份，第三份 AVIF 仍 `false` 且零消费者（**D-M6**）
- `docs/HANDOVER_2026-09-22.md:49` 缺前导 `|` 破表，且残留"两仓"旧措辞（**D-轻微**）

---

## 4. 已核实**无问题**的部分

这些是本复查亲自跑过或四线交叉确认过的，可放心：

- ✅ **构建面干净**：`src/FfmpegGui` + `ServiceProbe` + `UiTestHost` + `GainMapTestHost` 四项目 `-c Release` **全部 0 警告 0 错误**。
  ⇒ 本次动了 `FfmpegGui.csproj`（+13 行：Version→beta4、`ApplicationIcon`、一条 `AvaloniaResource icon_raw.png`）与 `icon.ico`(1378→4971 B)，**没有触发记忆中 NETSDK1060 的老雷**。
  ⚠ 遗留观察：新 `icon.ico` 的 5 档条目载荷仍是 PNG（`89 50 4E 47`），与注释自称"真 ICO"不符；当前 csc 解析通过，**属于侥幸而非保证**。
- ✅ **无恒 exit 0**：15 个新/改脚本全部检查，失败分支均真实 exit 非零。
- ✅ **PS5.1 静态解析全通过**：15 文件全 PARSEOK（`&&`/`||` 均出现在字符串内）。
- ✅ **路径无硬编码本机路径**，全 `$PSScriptRoot` 推导；10 处 `Start-Process` 全带 `-Wait`；无裸十六进制 `-band`；dot-source 变量覆盖护栏有效。
- ✅ **双语对齐**：290 键中英全对齐，新增 UI 文案无硬编码中文（XAML 13/13 未增），两处逐字段克隆点齐全。
- ✅ **JXL distance 收敛真的做到了**（除 `dngtool.cpp`）：6 个调用点 + 1 个发送点全部指向单一真值，生产代码无第二份算式。
- ✅ **ISO 多标签回退、RAW→JXL PNG 中转（4 项）、5 条 `UsesLosslessJpegRewrap` 判据、modular 与 jbrd 互斥** 均已兑现。
- ✅ **BMFF 二进制格式核心项**：无 `BitConverter` 机器序直写（全手写大端）、box 长闭合（实测 36+462+597=1095 且末项尾 = 文件尾）、`FullBox` v/f、`infe` v2 空名、`iloc` 0x44/0x00 半字节打包、2GiB 闸均正确；两遍法长度一致且 fail-closed。**这部分写得相当扎实。**
- ✅ 增益图缓存用的是 `ConcurrentDictionary`（非 Dictionary）、512 上限有淘汰、key 按 FullName+OrdinalIgnoreCase 归一 ⇒ 并发与一致性均无问题。
- ✅ `ThumbnailService` 数值边界正确：钳制 0→160 / 1→16 / 9999→512、只缩不放、8-bit、tmp 在 finally 清理、调用时序正确。
- ✅ L3 判据 60 → 65 条属实且均已接线。

---

## 5. 建议处置顺序

1. **修 P0-2**：`GainMapEncoder.cs:621` 补传 `baseFullRange` + 加一条"组装后 color_range 与底图一致"的判据（改动最小、影响面是每条 AVIF 增益图产物）。
2. **修 P0-1**：`$BASE_CS_UI` 840 → 821，重写归因注释，并做变异确认转红。
3. **修 P1-3**：注释改为"近似互逆（±1）"，并把探针锚点挪到一个**非互逆点**让锚点有判别力，或显式断言"±1 内的往返"这一弱性质。
4. **登记 P1-4**：把 `dngtool.cpp:1392` 的公式不一致写进 `JXL_RAW_16BIT_ADVISORY` 的"刻意未动"清单，避免下一个会话重复发现。
5. **修 §3.3 的 6 条假绿**，尤其 `verify-encoder-knob-liveness`（全 undecidable 仍 exit 0）和 T76/77/79 的 `$exe` 覆盖顺序。

---

## 6. 本次复查的方法论备注

- 主复核人对 P0 两条都做了**端到端实跑取证**（临时副本 + 变异验证 + ffprobe/字节级取证），不是只读推演。
- 临时取证目录 `/tmp/rv-base`、`/tmp/rv-var` 与临时脚本 `rv-dump.ps1` 均在系统 Temp 下，**未污染仓库**；全过程中对 `src/`、`tests/scripts/` **零改动**，未执行任何 `git add/commit`。
- 子线 A/B/C/D 的结论**未经主复核人逐条实跑**的部分已在文中标注,建议按其各自报告的行号复核。
