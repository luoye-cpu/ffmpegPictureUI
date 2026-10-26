# 交接文档：beta9 出包任务 — 第二轮中止快照（复核已完成，验证未开始）

> 生成时间：2026-10-26 09:xx（GMT+8）
> 触发：用户明确要求 **"停止你手里面的所有操作，完整打包目前任务进度和相关内容，做一个交接文档给其他agent"**
> 范围：本文件是对"第一份 STOP 文档（HANDOVER-BETA9-STOP-2026-10-26.md）之后 → 两份复核完成（文档、src） / tests-tools 复核未完成 / 环境核实"这一增量会话的完整打包。
> 证据纪律：凡本会话（2026-10-26 09:xx 实测）亲眼核实的，标 ✅；凡来自上一份 STOP 文档或子 agent 报告（未重跑），标 ⚠️（待复核）。
> **打包收口（最终态）**：本文件 + 第一份 STOP 文档 + `tests/output/beta9-review-reports/` 两份子 agent 报告全文 = 本次交接的全部内容；第一份 STOP 文档头部已加续接指针指向本文件；工作树 33 文件未动。

---

## 0. 一句话状态

**本会话完成了文档组、src 组两份子 agent 复核（结论：可放行进入整轮验证，附带 2+1 非阻断建议），tests/tools 复核因并发/配额限制未跑完。** 环境核实发现 `.gates.lock` 残留（与第一份 STOP 文档 §6 矛盾，pid 39764 已死、本会话已删）。整轮门禁**从未在此会话跑过**；上一轮 `full-round-20261026d.log`（7:55 启动、8:04 中断）仍是最后一次完整运行，bench 7 失败仍待裁定。**出包前必须先跑完 tests/tools 复核、应用微修（见 §6），再跑一次干净整轮。**

---

## 1. 与上一份 STOP 文档的关系（本会话增量一览）

| 项 | 第一份 STOP 文档（HANDOVER-BETA9-STOP-2026-10-26.md） | 本会话增量/更正 |
|---|---|---|
| `.gates.lock` | 声称"已确认无残留" | **实测存在**（pid 39764，07:55:56 启动、进程已死），本会话已删 ✅ |
| 整轮日志 | 读取了尾部 53 行 | **完整读取**（同上，无新增内容） |
| 文档组复核 | 未做 | **已完成**（agent_2f450df1），结论见 §3 |
| src 组复核 | 未做 | **已完成**（agent_0d051ec6），结论见 §3 |
| tests/tools 复核 | 未做 | **启动两次均失败**（并发超限、配额超限），**未完成** ⚠️ |
| 微修清单 | §9 仅列"修 §8 文档矛盾" | **细化到代码行级**（QueueProcessor:2401 注释 90/91、IccProfileService BOM、AGENTS.md:36 78→84、_probe-cjk L454 880/881、IMPLEMENT_PLAN §47.9 更正条目、B3-1 登记） |
| bench 裁定 | 列为未决 | **保持未决**，需干净整轮裁定 |
| 子 agent 状态 | 无 | **两个已完成 agent（agent_2f450df1、agent_0d051ec6）可复用**，第三个因配额未成 |
| **证据落盘** | 无 | **已生成**：`tests/output/beta9-review-reports/docs-20261026.md`、`src-20261026.md`（两份子 agent 报告全文） |
| **续接指针** | 无 | **已加**：第一份 STOP 文档头部新增指向本文件的指针 |

---

## 2. 当前精确状态（本会话实测）

| 项 | 实测结果 | 来源 |
|---|---|---|
| 版本号 | `<Version>1.6.0-beta9</Version>` | `src/FfmpegGui/FfmpegGui.csproj:19` ✅ |
| 工作树改动 | **33 个文件已修改、1 个未跟踪**（第一份 STOP 文档） | `git status --short` ✅ |
| 计划任务 | 无 `FfmpegGui` 相关 schtasks | `schtasks /query` ✅ |
| 门禁进程 | 无 ServiceProbe/UiTestHost/GainMapTestHost/dotnet/pwsh 门禁进程 | `tasklist` ✅ |
| `.gates.lock` | **不存在**（本会话删除了残留） | `ls tests/output/.gates.lock` ✅ |
| 整轮日志 | `tests/output/full-round-20261026d.log`（5663 B，08:04 mtime），**未完成 111/111**，末尾 `^C` | 读日志全文 ✅ |
| 七项目二进制 | 未在本会话重建（上一份 STOP 声称重建完成） | ⚠️ 待复核 |
| CJK 棘轮基线 | 901（上一份 STOP 声称） | ⚠️ 待复核（本会话未重测） |
| tags | `1.6.0BETA8` 存在；下一个打 `1.6.0BETA9` | `git tag --list` ✅ |
| CHANGELOG | `CHANGELOG.md`、`CHANGELOG.en.md` 存在根目录，**门禁脚本不读它们** | `ls` + `grep` ✅ |

---

## 3. 已完成的两份复核结论（可放行/需修清单）

> **证据附件**（只读，落盘供下一 agent 复查）：
> - 文档组全文：`tests/output/beta9-review-reports/docs-20261026.md`
> - src 组全文：`tests/output/beta9-review-reports/src-20261026.md`
> （注：`tests/output/` 受 `_sweep-output.py` 管辖；若被 `--unreferenced` 清理，可用子 agent ID 重查：文档组 `agent_2f450df1-f66b-461e-ab32-ee75ae86c3cd`、src 组 `agent_0d051ec6-c5b9-4798-bb5f-bde40c8a7131`）

### 3.1 文档组复核 ✅（agent_2f450df1）

| 文件 | 结论 | 关键发现 |
|---|---|---|
| `AGENTS.md` | ❌ **陈旧** | L36 仍写 "`$scriptList` 数组（78 条）"，运行器已是 84 条；需同步到 84 |
| `docs/TESTING.md` | ✅ 一致 | L590 "脚本门禁清单当前 **84 条**" 与运行器吻合 |
| `IMPLEMENT_PLAN` | ✅ 追加更多 | 本轮实际追加：§46.8/§46.9 + §48 + §49.1-49.7 + **§49.8 五小节**（非仅 §49.8） |
| `§47.9` 矛盾 | ⚠️ **坐实** | L3763-3764 称 "#81–#84 未跟踪/未提交"，但 `0b07b1c --stat` 含全部五支门禁；按带日期账本不回改 → **加更正条目（如 §49.9）** |
| CJK 880/881 | ⚠️ **算术错在脚本 L454** | `_probe-cjk-hardcode-scan.ps1:454` 头部注释 "+4 ⇒ 880" 与自身 881 链（L466/L474/L488 求和=901）冲突；正确链 876→881（+5）→885（+4）→901（+16），IMPLEMENT_PLAN:3522/HANDOVER:3563 均在 881 侧。**修法：仅改 L454 头部为 "+5 ⇒ 881"** |
| `THIRD-PARTY-NOTICES` | ✅ 格式 OK | 本轮仅版本升级（Avalonia 12.1.4 等），DNG SDK/LibRaw 条目早已存在 |
| B3-1 | ⚠️ **仅存活于第一份 STOP 文档** | 仓内无 B3-1 任何 ID；事实：GUI 默认 JPG→cjpegli/JXL→cjxl，CLI→Ffmpeg；#82 门禁钉 JPG 默认=Ffmpeg、JXL 默认=Cjxl。需作者裁定后登记，**本批不改代码** |
| `HANDOVER.md` | ✅ H-9~H-13 | 口径自洽（84 受管 + 27 探针 = 111） |

### 3.2 src 产品代码组复核 ✅（agent_0d051ec6）

| 文件 | 结论 | 关键发现 |
|---|---|---|
| `FfmpegGui.csproj` | ✅ 符合 | 仅版本号 beta8→beta9；全 CRLF（122/122），无整文件翻转 |
| `ColorIntentFactory.cs` | ✅ 符合 | **唯一真值** `ExitWritesIccFor(fmt) => fmt != "avif"`（:26）；装配处调用之；**src 内零副本**（全仓 grep 命中仅该行 + Program.cs 注释 + Program.cs expect 独立推导） |
| `ColorIntent/ColorTransformPlan/ColorStrategyPlanning/TransferCurve` | ✅ 符合 | AVIF ICC 收口：`PlanPolicy.ExitWritesIcc=false`（仅 avif）；四处携带闸收紧、Reason 点名；S2 拒绝 exit 91 零产物；S1 回退 bt2020/bt709、GamutClipped+NoIccCarrier 播报；无放宽判据 |
| 服务层六件 | ✅ 自洽 | ExifToolService 双趟 20s 超时、NoWriteApplied(-3)；QueueProcessor 元数据失败仅传播 ICC 硬失败；GainMap 三态 iso/xmp/both 协议自洽；**无静默丢弃** |
| **QueueProcessor.cs:2401** | ⚠️ **建议修** | 兜底删除产物设 `ExitCode=90` 但注释称"与引擎侧契约拒绝同码 90"，**引擎侧实际是 91**（ColorStrategyPlanning:1618 返回 `(true, 91, null)`）—**改注释为 91**（数字，不计 CJK 棘轮） |
| **IccProfileService.cs** | ⚠️ **建议修** | **工作树新增 UTF-8 BOM**（HEAD 无），仅字节差异，建议去除对齐 |
| UI 五件 | ✅ 符合红线 | 新文案全走 locales（12 key 成对增、3 死 key 成对删、4 key 去 emoji）；无硬编码 CJK；CLI `--gainmap-metadata` 词表校验同批棘轮登记 |
| **JPG/JXL 默认后端**（B3-1 事实） | ✅ 查证 | 模型默认 `EncoderBackend=Ffmpeg`；GUI 有效默认 JPG→cjpegli(可用)/mjpeg、JXL→cjxl/libjxl；CLI `--encoder` 缺省→Ffmpeg（mjpeg/libjxl）；**本批 diff 未改任何选择逻辑** |

### 3.3 总结论（src + 文档）

> **可放行进入整轮验证**。红线全守住：版本号单行、唯一真值无第二副本、拒绝/回退路径点名、UI 文案全走资源、默认后端逻辑未动。**两个非阻断代码修（QueueProcessor:2401 注释 90→91、IccProfileService 去 BOM）+ 三个文档修（AGENTS.md 78→84、_probe-cjk L454 880→881、IMPLEMENT_PLAN §47.9 更正条目）建议在整轮前以字节级工具（Python）逐一应用**，避免 EOL 翻转或 CJK 棘轮误伤。

---

## 4. 整轮 20261026d 日志读数（完整逐项）

```
START 周一 2026/10/26  7:55:55.14
gates-lock=C:\PLAN\ffmpegPictureUI/tests/output/.gates.lock (pid=39764)
env-cleaned=LC_ALL,LANG,LC_CTYPE
[gate] exe=.../ServiceProbe.exe (Release)
probe-host=.../ServiceProbe.exe
shape-selfcheck=OK（17 针）
runner-self-sha256=68B63CA442962086 lines=1626 bytes=165742
artifact-sha16: FfmpegGui.dll=DC9587599E8D3DC9  FfmpegGui.exe=D4FAD60A006DCA6F
  ServiceProbe.exe=0B2B8F6E542C9A2E  UiTestHost.exe=AECDB8B487B6E2AD
  GainMapTestHost.exe=DB0CA48528576263

[contract  ] exit=0  PROBE RESULT: pass=160 fail=0  1.0s
[wire      ] exit=0  PROBE RESULT: pass=98 fail=0  4.5s
[verdict   ] exit=0  PROBE RESULT: pass=41 fail=0  0.8s
[selftest  ] exit=0  SKIP 1 ...  pass=40 fail=0 skip=1  0.8s
[iccname   ] exit=0  PROBE RESULT: pass=17 fail=0  0.6s
[plan      ] exit=0  PROBE RESULT: pass=39 fail=0  0.6s
[curve     ] exit=0  PROBE RESULT: pass=44 fail=0  0.5s
[matrix    ] exit=0  PROBE RESULT: pass=15 fail=0  0.6s
[colormath ] exit=0  PROBE RESULT: pass=19 fail=0  0.6s
[ootf      ] exit=0  PROBE RESULT: pass=11 fail=0  0.5s
[hlgwire   ] exit=0  PROBE RESULT: pass=3 fail=0  0.5s
[bt2446    ] exit=0  PROBE RESULT: pass=18 fail=0  0.6s
[decision  ] exit=0  PROBE RESULT: pass=4 fail=0  2.1s
[engine    ] exit=0  PROBE RESULT: pass=35 fail=0  7.6s
[runner    ] exit=0  PROBE RESULT: pass=8 fail=0  9.8s
[settings  ] exit=0  PROBE RESULT: pass=11 fail=0  0.5s
[procstreams] exit=0  PROBE RESULT: pass=3 fail=0  11.0s
[encoding  ] exit=0  PROBE RESULT: pass=6 fail=0  0.9s
[ipc       ] exit=0  PROBE RESULT: pass=10 fail=0  4.4s
[i18n      ] exit=0  PROBE RESULT: pass=7 fail=0  0.6s
[geometry  ] exit=0  SKIP 1 (S2 tonemap 不可达)  pass=52 fail=0  6.9s
[metaraw   ] exit=0  PROBE RESULT: pass=20 fail=0  1.2s
[thumbnail ] exit=0  PROBE RESULT: pass=40 fail=0  19.3s
[oog       ] exit=0  PROBE RESULT: pass=6 fail=0  0.9s
[avifgm    ] exit=0  PROBE RESULT: pass=14 fail=0  1.3s
[gamutfit  ] exit=0  PROBE RESULT: pass=19 fail=0  0.5s
[bench     ] exit=1  PROBE RESULT: pass=30 fail=7  121.3s   ← **关键：7 失败**
[verify-ps-compat]     exit=0  PASS=13  4.2s
[verify-color-caps]    exit=0  PASS=16  7.8s
[verify-color-wiring]  exit=0  PASS=153  98.3s
^C
```

> 备注：verify-acr-route 等后续脚本**未运行**（^C 在 wiring 之后）；84 受管脚本中仅跑了 3 个。

---

## 5. bench 7 失败详情与分析

| # | 判据 | 阈值 | 实测 | 备注 |
|---|---|---|---|---|
| 1 | sRGB→PQ(目标域换算 ⇒ 关表) 单核 | ≥3 MPix/s | **2.6** | 快路径被合法关闭（解析式） |
| 2 | sRGB→P3(矩阵同曲线) 单核 | ≥40 MPix/s | **28.5** | 快路径适用 |
| 3 | BT.2020→sRGB(含裁切) 单核 | ≥40 MPix/s | **29.5** | 快路径适用 |
| 4 | PQ→sRGB(变曲线+裁切) 单核 | ≥40 MPix/s | **34.7** | 快路径适用 |
| 5 | sRGB→PQ(目标域换算 ⇒ 关表) | ≥3 MPix/s | **2.6** | 重复行 |
| 6 | 8b PQ→sRGB（HDR 源走主力 8-bit） | ≥25 MPix/s | **21.4** | 日志附注：修前实测 16.4~22.7，"白算的 FromLinear 一回来就红" |

**关键观察**：
- 6 个不重复失败中，**5 个是快路径适用但未达阈值**（矩阵/裁切/变曲线场景），**1 个是解析式场景**（sRGB→PQ 关表）也未达阈值。
- 上一份 STOP 文档 §4 "上一轮摘要声称 bench 37/0" 与本轮日志**直接矛盾**；两种可能：(a) 摘要的 37/0 来自更早轮次，(b) 20261026d 真实回归。**本会话未重跑**，必须由干净整轮裁定（两轮对照臂互证，不可靠"上一轮说全绿"放行）。

**建议裁定流程**：
1. 微修后 `dotnet build -c Release` → 二进制指纹更新。
2. 单独跑 `ServiceProbe.exe bench` 2–3 次（预热+正式），记录 pass/fail 与 MPix/s 数值。
3. 若同一机器连续两轮均 ≤ pass 阈值 → 判定**真实回归**，需定位（可能是 FromLinear/ToLinear 表格生成、SIMD 路径、CPU 频率抖动、后台负载）；若仅首轮红第二轮绿 → 判定**抖动**，记录两轮数值互证后放行。

---

## 6. 待办修复清单（动手前必读）+ 修复要领

**顺序建议**：先跑完 tests/tools 复核（确认无判据破坏） → 应用微修 → `dotnet build` → bench 预检 → 整轮。

### 6.1 代码微修（3 处，均非 CJK、可字节级安全替换）

| 文件 | 行/位置 | 旧 → 新 | 工具/命令示例 |
|---|---|---|---|
| `src/FfmpegGui/Services/QueueProcessor.cs` | ~2401 | 注释 `同码 90` → `同码 91`（引擎实际返回 91） | `py -c "import re; t=open(r'src/FfmpegGui/Services/QueueProcessor.cs','rb').read(); t=re.sub(rb'同码 90', rb'同码 91', t); open(r'src/FfmpegGui/Services/QueueProcessor.cs','wb').write(t)"` |
| `src/FfmpegGui/Services/IccProfileService.cs` | 文件头 | 去除 UTF-8 BOM（EF BB BF） | `py -c "t=open(r'src/FfmpegGui/Services/IccProfileService.cs','rb').read(); t=t[3:] if t.startswith(b'\xef\xbb\xbf') else t; open(r'src/FfmpegGui/Services/IccProfileService.cs','wb').write(t)"` |
| `src/FfmpegGui/FfmpegGui.csproj` | 仅版本号行 | 确保仍为 CRLF（已是），不改其他 | 只读确认即可 |

### 6.2 文档微修（3 处）

| 文件 | 行/位置 | 旧 → 新 | 备注 |
|---|---|---|---|
| `AGENTS.md` | 36 | `（78 条）` → `（84 条）` | 同步运行器口径 |
| `tests/scripts/_probe-cjk-hardcode-scan.ps1` | 454 | `+4 ⇒ 880` → `+5 ⇒ 881` | **LF 文件**，Python 字节替换；后续 881→885→901 链自洽无需动 |
| `docs/IMPLEMENT_PLAN_2026-10-11.md` | §47.9 后 | 追加 **§49.9 更正条目**：记录 0b07b1c 已提交 #80–#84，§47.9 为时点快照已证伪 | 按带日期账本规矩：不改原文、加更正条目 |

### 6.3 B3-1 登记（不改代码）

在 `docs/IMPLEMENT_PLAN` 或 `docs/HANDOVER.md` 新增条目：
> **B3-1 后端默认值登记（待作者裁定）**：产品模型默认 `EncoderBackend=Ffmpeg`。GUI 有效默认经 `EncoderDetectionService.GetDefaultEncoder`：JPG→cjpegli(检测到)/mjpeg；JXL→cjxl(可用)/libjxl。CLI `--encoder` 缺省→Ffmpeg（JPG=mjpeg、JXL=libjxl）。#82 `verify-jxr-backend-choice` 钉 ② JPG 默认臂=Ffmpeg、③ JXL 默认臂=Cjxl vs 显式 Ffmpeg。第一份 STOP 文档称"B3-1 默认臂由 cjpegli/cjxl 变 ffmpeg"与 #82 JXL 默认臂期望相抵触，**需作者确认事实口径后再定稿登记**。

### 6.4 修复要领（避坑）

- **EOL 保真**：所有 .ps1 与 Program.cs 为 LF，csproj/ColorIntentFactory 为 CRLF。**禁止**用文本编辑器整文件保存（会翻转）。必须用 Python `rb`/`wb` 字节级替换或 `sed -i`（注意 MSYS bash 会改坏路径/反斜杠）。
- **CJK 棘轮安全**：修改仅涉及 ASCII 数字/字母（90/91、78/84、880/881），**不改变任何 CJK 字符串**，棘轮计数自动不受影响。
- **STALE 闸**：`_run-step3-gates.ps1` 要求**七项目**全部重建（`expectedScriptCount=84`），源码 mtime < 二进制 mtime。修改源码后必须 `dotnet build -c Release` 全量重建（13 个测试工程在 sln 里），否则 STALE 闸判红。
- **CHANGELOG 可随时改**：grep 全仓门禁脚本零引用，安全改动区。
- **验包锚定指纹**：宣布全绿必须绑定 `artifact-sha16`（FfmpegGui.dll/exe + 4 宿主 exe）与当轮日志同段。

---

## 7. 未完成的 tests/tools 复核（任务书）

下一 agent **必须先完成**此复核（或等配额恢复重试子 agent），再进入构建/验证。文件清单（共 8 个）：

| 文件 | 核查重点 |
|---|---|
| `tests/projects/ServiceProbe/Program.cs` | MakeIntent 调 `ExitWritesIccFor`；无第二份 `fmt != "avif"` 副本；C5 expect 独立推导（`!string.Equals(fmt,"avif",OrdinalIgnoreCase)`）且注释"两侧必须独立" |
| `tests/scripts/verify-acr-route.ps1` | ⑦/⑧/⑨ 各+1 CK 下发点日志断言（总数 28→31），解析日志并判红（非纯打印） |
| `tests/scripts/_probe-cjk-hardcode-scan.ps1` | 确认 L454 头部 880/881 矛盾、$BASE_CS_UI=901、换回法归因注释在场 |
| `tests/scripts/verify-jxl-probe-timeout.ps1` | `$probe2MaxSec=30`（原 15）+ ①-⑦ 源码形态自检；15→30 有归因注释 |
| `tests/scripts/_lib-matrix.ps1` | 行锚 `:2386` → `:2400`，仅数字改、指向代码仍正确 |
| `tests/scripts/_run-step3-gates.ps1` | `expectedScriptCount=84`，$scriptList 84 条，Test-RunnerShape 散文条数、TestGroups.cs 组表**四处同步** |
| `tests/scripts/verify-gainmap-compose.ps1` | 两图 GainMap 合成收口断言，失败点名 |
| `tests/scripts/verify-prophoto-faithful.ps1` | §6 新增 ⑥a–⑥f（AVIF ICC 诚实性六臂），有牙（改回旧行为红）、失败输出点名 |
| `tools/src/dngtool/dngtool.cpp` | LibRaw 解码臂固定恒等 gamma（真线性 16-bit），判据=第 79 条 ⑫（同一 DNG `-d`/`-rd` 中位比 0.35~1.5） |

**红线提醒**：不许放宽判据换绿；同一公式不许第二副本；失败必须点名；EOL 逐文件报告是否整文件翻转。

---

## 8. 遗留未决项（合并两轮）

| 编号 | 项 | 状态/来源 | 处置 |
|---|---|---|---|
| 1 | 账本 §47.9 "#81–#84 未跟踪" | ⚠️ 第一份 STOP §8.1 + 本会话文档组复核确认矛盾 | 加更正条目 §49.9 |
| 2 | CJK 880/881 算术 | ⚠️ 第一份 STOP §8.2 + 本会话确认错在 L454 头部 | 改 L454 为 +5/881 |
| 3 | B3-1 默认臂后端登记 | ⚠️ 第一份 STOP §8.3 + 本会话 src 组查证事实 | 登记事实+待作者裁定，**本批不改代码** |
| 4 | 第 79 条 ⑦⑧⑨ 下发点断言 | ⚠️ 第一份 STOP §8.4 + 本会话日志显示 verify-acr-route 未跑 | 整轮跑完后验证 |
| 5 | C5 ExitWritesIccFor 变异验证 | ⚠️ 第一份 STOP §8.5 | 重跑：产品真值改 `=> true` 期望 contract 158/2（红） |
| 6 | **bench 7 失败** | ✅ 本会话日志实测 + 第一份 STOP §8.6 | **干净整轮裁定**（两轮对照臂） |

---

## 9. 出 beta9 的精确下一步（修订版）

> **前置**：tests/tools 复核完成、微修（§6）应用、七项目全量重建。

1. **重跑一次干净整轮**（schtasks 脱离进程树，≥60 min 预算）：
   ```powershell
   $env:LC_ALL=$null
   dotnet build ffmpegPictureUI.sln -c Release   # 13 项目编不过当场暴露
   # 创建一次性 schtasks（如 FfmpegGuiGates20261026e），指向 tests/scripts/_run-step3-gates.ps1
   # 日志落 tests/output/full-round-20261026e.log
   ```
   **重点盯**：bench 7 失败是真实回归还是抖动（两轮互证）；verify-acr-route 等脚本全跑；111/111 成立。

2. **若 bench 确为回归** → 定位修复（可能涉及 ColorMapping 快路径/解析式分支、SIMD、CPU 频率、后台负载）；**若为抖动** → 记录两轮数值互证后放行（不可仅靠"上一轮说全绿"）。

3. **补齐 CHANGELOG**（三段：DNG/ACR 路线、GainMap+JXR+BT.601、AVIF ICC），根目录两份成对。

4. **修 §8 三处文档矛盾**（账本更正、CJK 880/881、B3-1 登记）。

5. **打包验收**：
   ```powershell
   .\pack.ps1 -Mode all
   .\tests/scripts/verify-release-package.ps1   # 验包
   # L3 五套件指向新包（publish/PLAN 升 beta9，beta8 就地覆盖须升）
   ```

6. **提交 + 打 tag**：
   ```bash
   git add <33 files + CHANGELOG edits + 更正条目 + 两份 STOP 文档>
   git commit -m "release: v1.6.0-beta9（...）"
   git tag 1.6.0BETA9   # 现有 tags 命名规律：1.6.0BETA8
   ```

---

## 10. 本会话已执行 / 未执行清单

### 已执行（仅只读核查 + 清理残留 + 子 agent 调度 + 交接打包）
- 读取第一份 STOP 文档、git status、schtasks、tasklist、lock 文件、整轮日志全文 ✅
- 删除残留 `.gates.lock`（pid 39764 确认死）✅
- 派发 3 个子 agent 并行复核（文档组 ✅、src 组 ✅、tests/tools 组 ✗ 两次失败）✅
- 记录子 agent 发现（§3）✅
- **打包**：写本文件（STOP2）；两份子 agent 报告全文落盘 `tests/output/beta9-review-reports/`；第一份 STOP 文档头部加续接指针 ✅
- **最终核查**：git status = 33 修改 + 2 未跟踪（两份 STOP 文档）；无 `.gates.lock`；无门禁进程 ✅

### 未执行（遵守用户"停止一切操作"）
- 任何 `dotnet build`、整轮门禁、bench 预检、微修代码/文档、CHANGELOG 编写、`pack.ps1`、验包、L3、提交、打 tag

---

## 11. 环境坑 & 判据红线（复制自第一份 + 本会话新增）

### 环境坑（必须带入下一手）
- **55 分钟后台长任务会被基础设施杀**（exit `0x40010004` / 1073807364）⇒ **必须用 schtasks 一次性任务脱离 WorkBuddy 进程树**启动。
- **异常终止残留 `.gates.lock`** ⇒ 确认占用 pid 死亡后手工删（本会话已做一次）。
- **TMP 重定向**到 `tests/output/_tmp`（Harness/ServiceProbe/UiHost/GainMapTestHost 四处 `PinScratchRoot()` 覆盖 TMP/TEMP/TMPDIR）。
- **沙箱注入致 `dotnet test` 必失败**（testhost 加载 hostfxr.dll 报 0x80070005）⇒ 同样用 schtasks 绕；`dotnet build` 不受影响。
- **EOL 约定**：多数 `.ps1` 与 `Program.cs` 纯 LF；`ColorIntentFactory.cs`、`FfmpegGui.csproj` 为 CRLF。编辑一律用 Python 字节级替换保 EOL，禁止混用；**判据是与 HEAD blob 做字节层 EOL 对撞**。
- **STALE 闸**：运行器要求**七项目**全部重建（非四项目），源码 mtime 必须早于二进制；子集自陈"N/84 ⇒ 不构成基线"。
- **子 agent 并发/配额限制**：本会话两次启动 tests/tools 复核均因 "exceed quota limit" 失败。下一 agent 优先内联完成或等配额恢复再派 agent。

### 判据红线（不可为出包而破）
- 不许放宽判据换绿；判据必须"改回旧行为会红"（变异验证 / 反真空 / 负控）。
- 同一公式/映射不许有第二处副本（JXL distance 曾 5 处副本的教训）。
- 失败/回退必须点名（P9 型静默丢弃坑）。
- 抬基线（CJK 棘轮）必须用**换回法**实测归因（`git archive HEAD` 导出仓库外跑探针），禁止为撑棘轮改代码。

---

## 12. 决策与上下文索引

- 用户主指令（本会话）：停止任务、只出交接文档。
- beta9 设计决策来源：`docs/IMPLEMENT_PLAN_2026-10-11.md` §49（尤其是 §49.8）、`docs/HANDOVER.md` H-9~H-13。
- 门禁体系：`tests/scripts/_run-step3-gates.ps1`（84 受管脚本 + 27 探针 = 111 条）。
- CJK 棘轮：`tests/scripts/_probe-cjk-hardcode-scan.ps1`（`$BASE_CS_UI=901`，换回法归因）。
- 单一真值：`ContainerYuvMatrix`（`ColorSpaceRegistry.cs:671`，BT.601）；`ExitWritesIccFor`（`ColorIntentFactory.cs:26`，AVIF 静态出口写不进 ICC）。
- 子 agent 可复用 ID（若配额恢复）：
  - 文档组：`agent_2f450df1-f66b-461e-ab32-ee75ae86c3cd`
  - src 组：`agent_0d051ec6-c5b9-4798-bb5f-bde40c8a7131`
- 关键二进制指纹（20261026d 轮，重建后将变）：
  `FfmpegGui.dll=DC9587599E8D3DC9  FfmpegGui.exe=D4FAD60A006DCA6F  ServiceProbe.exe=0B2B8F6E542C9A2E  UiTestHost.exe=AECDB8B487B6E2AD  GainMapTestHost.exe=DB0CA48528576263`
- runner 自我哈希：`68B63CA442962086`（1626 行 165742 字节）
- 上一轮全绿声称的 bench 37/0 与本轮 30/7 矛盾，必须由干净整轮裁定。