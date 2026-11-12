# 交接：测试模块化批 5·死支处置（在途）+ 覆盖探针 fullrun 证据链（2026-11-12）

> 接续 `docs/TEST_MODULE_PLAN_2026-11-12.md`（批 1–4 已完成入库）。本文交接**批 5 第一个子批
> （死支处置）的在途状态**与**它拖出来的覆盖探针 ⑥-d 回归修复战况**——后者是当前唯一拦路项。
> ⚠ **状态更新（同日接手批收口）**：§3.4 最后一步已落、§3.5 收尾清单已全项完成（探针 **40/0** /
> union **11/0 @473** / registry **9/0**，裸跑自钉 TMP，取证 `tests/output/_tmp/batch5-deadbranch/`）；
> 账本三份（TESTING.md 批 5 段 / AGENTS §7 条目 / TEST_MODULE_PLAN §4 行）随批提交。
> 接手人读完 §3 即可继续；不需要重做任何调研。

---

## 1. 已入库（不需再动）

| 提交 | 内容 |
|---|---|
| `743cc72` | 批 2 模块 2+3 + 批 3 ServiceProbe 拆分（清单 102→103） |
| `8d6fdd1` | 批 4·1 第 104 条 `verify-log-jsonlines`（清单 103→104） |
| `c604208` | 批 4·3+4 N8/N9 进 GroupN（三把尺 464→473） |
| `1825181` | 批 4·5 ServiceProbe `mediasvc` + VTD §5（盲区 7/12 收口） |
| `c2ef35a` | **union 改全组跑**（收口批 4·3+4 发现②）：`verify-ui-split-union.ps1` 逐工程全组跑（19→5 进程）；受管子集实测 ui-host 473/0、**union 11/0 @99.7s**（G2 确定性红消失）、registry 10/0；`$work` 迁 `tests/output/ui-split-union/`；runner :1436 补机制更正注。取证 `tests/output/_tmp/union-fix/` |

**里程碑**：盲区 7/12 收口；rc.1 出包前唯一结构性拦路（union 受管红）已清。104/104 整轮基线仍未重测
（下次发布批跑）。

---

## 2. 工作树在途（批 5·死支处置，**未提交**；`git status` 即见）

| 文件 | 改动 | 状态 |
|---|---|---|
| `tests/projects/GainMapTestHost/Program.cs` | 三处 `gainmath avifgm` 过期注释更正（isobmff 门禁实际不调它；AVIF 写出侧锁由 ServiceProbe `avifgm` mode 承载） | ✅ 已重建 GainMapTestHost（STALE 闸要求） |
| `tests/scripts/run-color-regress.ps1` | **已 `git rm`**（纯聚合器：8 个被调者全部在册、零自有判据） | 删除已在暂存区 |
| `tests/scripts/run-full-matrix-test.ps1` | **已 `git rm`**（被测路径 `publish\build\FFmpegPictureUI-dev-x64-full\` 按 pack.ps1 模板任何版本都不会产生；语料 `D:\TEST2` 已不在盘，实测 ls 不可达） | 同上 |
| `tests/scripts/run-full-matrix-test-safe.ps1` | **已 `git rm`**（同上） | 同上 |
| `tests/scripts/run-ps-verify.ps1` | **保留**；coverage 探针 `$offExplicit` 理由改写为「手动 ACR 目检工具」（不再写「待证后删」） | ✅ |
| `tests/scripts/_probe-test-group-coverage.ps1` | ① `$offExplicit` 8→5 行；② **⑥ 调用点索引新增 fullrun 证据链**（§3）；③ 新 SYNTH 有牙 case；④ ⚠ **(5b) 里有一行 DBG5b 调试输出，提交前必须删**（§3.5） | 🔶 ⑥-d 还差一步（§3.4） |
| `tests/scripts/verify-ui-split-union.ps1` | `$modes` 表改为**纯 exe 字面相对路径**（mode 清单删除——组归属唯一真值是 TestGroups.cs，勿再抄第二份）+ `Invoke-Exe (Join-Path $root $modes[$proj]) …` 内联起件 | 🔶 与探针 (5b) 联动（§3.4） |
| `tests/scripts/_module-registry.json` | +1 条目 `manual.serviceprobe-diag-modes`（66→67；ipc 6 mode + piperaw 2 + tonemap 的手动诊断域登记，carrier=$modeExempt/⑨-b，非覆盖缺口） | ✅ registry 门禁实测 **9/0** |
| `tests/scripts/_probe-jxl-webp-pixels.ps1` | :31 缺素材提示改指 `ensure-fixtures`/在册 `run-color-tests`（原指已删的 run-color-regress） | ✅ |
| `docs/TESTING.md` | §3.4 表删 run-color-regress 行 + 分类段（:866 区）追加 2026-11-12 处置段（三删一留 + 理由 + ⑪-e 32→29 读数） | ✅（批 5 总结段待 §3 收口后补） |

已验证读数（本批内）：coverage 探针 **⑨-b / ⑪-d / ⑪-e 全绿**（清单外 32→29，显式表 8→5，未归属 0）；
registry 门禁 9/0。⚠ 三支删除后**没有任何在册脚本引用悬空**（全仓 grep 过；只有 tests/output 产物区
旧拷贝命中，不算）。

---

## 3. 覆盖探针 ⑥-d 回归与修复战况（当前唯一拦路项）

### 3.1 根因链（已钉死）

`c2ef35a` 把 union 从逐 mode 传参改成全组跑 ⇒ union 脚本里**不再出现 mode 名实参** ⇒
`_probe-test-group-coverage.ps1` ⑥ 的调用点索引（只认「mode 名落在起宿主参数子树/成链数据表」）
对 ui mode 断供 ⇒ **⑥-d 当场红 14 个 ui mode**（唯一调用点只剩清单外 `_accept-ui-split.ps1`）。
这不是产品/门禁行为问题，是**证据链模型没跟上全组执行形态**（ui-host 2026-11-01 全组化时就欠着，
当时被 union 的表证据掩盖）。

### 3.2 已落的修复（在工作树里）

`_probe-test-group-coverage.ps1` 的 `Get-CallSites`：
- **(1b)** foreach 循环变量 → 集合根变量的片段映射（`foreach ($mm in $modes.list)` 里 `@($mm)`
  能解析出 mode 名/路径——filtered 判定与 named 点名都靠它）；
- **(5b)** fullrun 证据：在册脚本起**拆分件宿主**（解析文本命中 `tests/projects/<组>/` 或该件
  AssemblyName.exe，缓存 `$groupHostExe`）且**没有任何一条起宿主命令的解析文本带该组任一 mode 名**
  ⇒ 无过滤全组执行 ⇒ 按组表给该组全部 mode 记 `kind='fullrun'` 证据。ServiceProbe 显式排除
  （其执行契约就是按 mode/-Probes 传参）；
- 新 SYNTH 有牙 case（full-group 正例 + loop 喂 mode 负例）。

`verify-ui-split-union.ps1`：`$modes` 表只存 5 条 exe 字面相对路径 + 调用点内联 `Join-Path`
（让调用点 AST 子树经 varMap 展开能解析出字面工程路径）。

### 3.3 修复过程中已修掉的两个 bug（都实测踩过，教训已存 memory）

1. **OrderedDictionary 整数下标按「插入位置」不按键**：`$cmdArgv[$line] = @(...)` 存取 argv 行 ⇒
   全部静默丢失（36/35 大面积假红，不抛异常）。已回退为原写法。教训存
   `~/.zcode/cli/memories/projects/ffmpegpictureui-.../memory/pwsh-ordered-dict-int-index.md`（含
   MEMORY.md 索引行）。
2. **组名自撞**：mode `ui-color` 以整 token 形态出现在路径 `tests/projects/ui-color/` 里 ⇒
   modeInArgv 误判「带 mode 起件」。已修：modeInArgv 判定前先剥掉该组路径段与 exe 名
   （`$txtPathless`）。

### 3.4 当前读数与**最后一步**（接手人从这里继续）

- 当前探针 **39/1**：唯一红 = ⑥-d 的 **ui-encoder-args、ui-icc**（组 ui-color）。
- 已加 DBG5b 调试行并实测（输出存于会话临时日志，读数如下——**这是钉死根因的关键证据**）：

  ```
  DBG5b verify-ui-split-union.ps1 g=ui-sweep  named=True modeInArgv=True hostCmds=2
  DBG5b verify-ui-split-union.ps1 g=gainmap-encode named=False modeInArgv=False hostCmds=2
  DBG5b verify-ui-split-union.ps1 g=ui-color  named=True modeInArgv=True hostCmds=2
  （ui-core / ui-mode 不打印 = 已被 union 正常记账；ui-sweep/ui-color 被自己的表键挡住）
  ```

- **根因（实测钉死）**：union `$modes` 表的**键名**（'ui-color'、'ui-sweep'）被 Get-Fragments
  当字符串字面量收进 varMap['modes'] 片段 ⇒ 起宿主命令解析文本注入整个 blob ⇒ 组名恰好等于本组
  某 mode 名的组（**只有 ui-color→'ui-color'、ui-sweep→'ui-sweep' 两组**）被误判「带 mode 起件」。
  ui-core/ui-mode/ui-cli 的键名不是本组 mode 名 ⇒ 幸免——与 DBG 完全吻合。
- **修法（✅ 2026-11-12 已落盘并实测）**：modeInArgv 从「整条命令解析文本」改为**只扫实参元素**——
  起点按形态分：cmdlet/launcher 形态（`GetCommandName()` 非 null）**j≥2**（j=1 是 exe 路径）、
  call-operator 形态（`& $prb 'memadmit'`，j=0 就是 exe 路径）**j≥1**（实仓验算新增的一类，
  `verify-gainmap-memory` 真实存在此形态）；namedPiece 仍用全文。原 `$txtPathless` 剥路径段逻辑随
  实参元素方案移除（实参扫描天然看不见 exe 路径段）。五个形状实测全成立：union 全组（不误滤）｜
  SYNTH case4/case5 与 param 系门禁的 `@($mm)` 循环喂 mode（照样滤）｜ui-host 自检臂（照样滤）｜
  gainmap-host 表喂 mode（照样滤）｜`& $prb 'memadmit'`（照样滤）。
- 备选（未采用）：union 表键改名（'core'/'mode'/…）——能跑但是 whack-a-mole，不如形状级修法。

### 3.5 收尾清单（修完 §3.4 后按序）

1. 删掉 (5b) 里那行 `DBG5b` 调试（恢复为纯 `if (-not $namedPiece -or $modeInArgv) { continue }`）；
2. 探针裸跑期望 **40/0**（⑥-d 14→0；⑥-g「≥26」恢复；⑦ 档② +12~14 属预期，账目自证自动重算）；
3. `verify-ui-split-union.ps1` 裸跑期望 **11/0 @473**（表形状改过，必须复跑验证门禁本体没破）；
4. `verify-module-registry.ps1` 裸跑 9/0（registry 67 条目那版已验过，改探针不影响它，可跳过或抽查）；
5. TESTING.md 补批 5 段（死支处置 + fullrun 证据链 + 39/1→40/0 战况与 DBG 读数）、AGENTS §7 批 5 条目、
   TEST_MODULE_PLAN 进度账一行；
6. 提交（单主题：死支处置 + 覆盖探针 fullrun 证据链；两份账本随批）。

### 3.6 本批顺带确认的事实（写进文档时可用）

- `verify-cli-strict` 对 ui-color/ui-icc/ui-cli-strict 的 argv 证据**本来就没被索引识别**（它的传参
  形态索引不认）——此前一直被 union 的全 mode 表证据掩盖；fullrun 链立起来后 12/14 由
  ui-host+union 记账，cli-strict 的证据缺口不再是活问题，但**别把这 2 个 mode 的绿记在 cli-strict 头上**。
- STALE 闸活例子（已写进 c2ef35a 账本）：五件 exe 03:57 < TestGroups.cs 05:11 ⇒ 受管子集首跑被拒 ⇒
  重建后复跑。改 Tests.Shared 后必须重建五个拆分件。

---

## 4. 批 5 剩余（死支处置之后）

1. **TESTING.md 逐条索引从账本生成**（生成脚本 + 漂移闸：手工改生成段=红）——调研 §1.3 的「散文不在锁里」老病；
2. **per-script 超时表（C4）**：钉进 `_module-registry.json` locks，runner 读表（现只有两条全局默认）；
3. AGENTS §7 ⑪-d 条目里「_probe-/_make- 族新脚本从 WARN 收紧」是剩余半句（可选）。

## 5. 独立刀（产品侧，各自独立一批）

1. `FFMPEGGUI_FFMPEG_DIR` 分隔符敏感 ⇒ 硬件编码器检测静默归零（产品归一化：env 覆盖应用处
   `Path.GetFullPath`；B1/B5 检测一致性家族；runner 侧已改 Join-Path）。⚠ `_probe-jxl-webp-pixels.ps1:26`
   的 `"$root/publish/PLAN/ffmpeg-full"` 正斜杠写法是同族（探针自用，非产品路径，顺带可改）；
2. `FFMPEGGUI_PLAN_DIR` 正斜杠同族排查。

## 6. 顺延（用户已裁定不急）

- 出包 **1.6.0-rc.1**（csproj Version + CHANGELOG zh/en 同批 + annotated tag + 删旧 beta12 包 + #93 复跑）；
- **104/104 整轮基线重测**（整轮 ~1h+；起轮后不许动 src/ 与运行器）。

## 7. 验收口径提醒（本窗口实测强化过的几条）

- 裸跑单条门禁前自钉 TMP：`TMP/TEMP/TMPDIR = C:\PLAN\ffmpegPictureUI\tests\output\_tmp\round-tmp`
  （**Windows 风格绝对路径**，git-bash 的 `/c/...` 会被子进程解析到盘根）；
- runner `-Scripts` 参数必须带 `.ps1` 全名（校验发生在探针阶段**之后**，传错名白跑 ~4 分钟）；
- 探针/coverage 裸跑输出是 GBK（`pwsh -File` 直管道），读数用 python `decode('gbk')` 或重定向后解码；
- 提权会话跑受管轮：门禁 85 ⑥④ 红是真读数；`Start-Process` 一律 `UseShellExecute=$false`。

## 8. 本窗口新增/更新的 memory

- `pwsh-ordered-dict-int-index.md`（新）：OrderedDictionary int 下标按位置不按键；
- `MEMORY.md` 加了对应索引行。既有 memory（json 三坑、$args 陷阱、NIM_ADD 矩阵、提权弹窗）未动。
