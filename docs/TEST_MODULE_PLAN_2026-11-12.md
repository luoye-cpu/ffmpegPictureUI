# 测试模块化方案（TEST_MODULE_PLAN，2026-11-12 立项）

> 目标（维护者 2026-11-12 原话）：「整个测试我希望拆的比较碎构成一个个模块，针对于不同情况，
> 保证测试不落后和测试覆盖范围正确。」——拆碎成模块 · 不落后 · 覆盖范围正确，三件事。
> 本文是方案与分批账本；调研证据绑定 2026-11-12 时点（HEAD `854be79`，GA 工具链）。

---

## 0. 已裁定的决策

| 决策点 | 裁定 | 出处 |
|---|---|---|
| ServiceProbe 拆分深度 | **源级 partial class 按域拆文件**（exe 不变、门禁不改、STALE/mirror-parity 不动）；exe 级拆工程留作可选后续 | 用户 2026-11-12 |
| 批次顺序 | **两线并行**：机制线（批 1）与高危盲区线（批 2）同推，互不阻塞 | 用户 2026-11-12 |
| 出包 | 版本号已定 **1.6.0-rc.1**；不急出包，顺延至本方案批后（见 `HANDOVER-NET11-GA-PACKAGING-2026-11-11.md` §8） | 用户 2026-11-12 |

## 1. 调研底账（三路只读调研，2026-11-12）

### 1.1 架构（子 agent A）

- 13 测试工程 = 11 Exe 宿主 + Tests.Shared/UiHostShared classlib；
  **ServiceProbe 是最后巨石**：单文件 ≈10.7k 行、顶层 65 mode 大 switch（`Program.cs:105-209`，
  71 case 其中 6 个 pngopt 二级）；mode 参数 schema 只写在方法体，调用方按位置参数硬编码。
- **目标形态样板已存在**：9 个拆分件（ui-*×5 + gainmap-*×3）+ `Tests.Shared/TestGroups.cs`
  组表（单一真值，9 组键 / 74 mode 并集）+ fail-closed 入口（未知 mode 退 2）+ 三重守卫
  （verbatim 逐行对撞 / reproducible 重跑逐字节 / union 双向差集，`$expectedOldCount=458`）。
- UiTestHost 单体已降级为「生成真值」（`_gen-ui-split.ps1` 按行区间切 5 份，组体逐字搬运）——
  真值与 5 件是同一逻辑模块的两份物理存在。
- **门禁→mode 映射无机读账本**：覆盖一致性靠 `_probe-test-group-coverage.ps1` ⑥ 的 AST 启发式反推，
  其注释自载漏判/误判史（「都不是证据」一串）。
- 断言数锁散在三把尺：verify-ui-host `$arms`（`verify-ui-host.ps1:74-79`）、param-matrix
  `$jExpected=126`、union `$expectedOldCount=458`——抬数需同批手工摸三处。
- 死支：ServiceProbe ipc-*×6 无在册调用（官方登记手动诊断，`docs/TESTING.md:164,1262`）；
  GainMapTestHost `gainmath avifgm` 子支注释过期（声称 verify-gainmap-isobmff §5 调用，实无）。

### 1.2 覆盖（子 agent B）

E2E 面扎实（格式矩阵/色彩/GainMap/RAW/动图/pngopt/线程/打包冒烟均有运行时判据）。
盲区 12 条（档位=本批新登记的 Tier 依据）：

| # | 盲区 | 风险 | 现状证据 |
|---|---|---|---|
| 1 | `--overwrite/--no-overwrite` 防覆盖契约 | **高** | grep `no-overwrite\|AllowOverwrite` 于 tests 全 0 命中（`CliParser.cs:187-192`） |
| 2 | 用户取消/停止路径 | **高** | 仅 `_probe-cancel-propagation-scan.ps1`(#14) 静态锁 ct 传参 |
| 3 | 损坏/截断/超大输入 | **高** | grep `truncat\|corrupt` 0 命中；畸形 JXL 只覆盖挂死不覆盖数据正确性 |
| 4 | GUI 子窗口（ImageDetail/FormatFilter/PresetManager/Progress/MetadataEditor） | 中 | tests/projects 0 引用 |
| 5 | IPC 单实例契约（二次启动→转发→退出） | 中 | 仅 #51 验 ExtraOptions；`App.xaml.cs:420,435` 路径无判据 |
| 6 | 设置重启恢复（GUI 端到端） | 中 | 服务层 roundtrip 有（ServiceProbe settings）；GUI 端无 |
| 7 | CLI `--preset` 回放 | 中 | verify-*/run-* 中 0 命中；GroupN 只测 UI 快照往返 |
| 8 | 非 DNG RAW 真机品牌（CR3/NEF/ARW） | 中 | 夹具以 DNG 为主 |
| 9 | `--log-file/--log-format JsonLines` 格式正确性 | 中 | log-file 仅当输出通道被引用 |
| 10 | PsRenderService/PlanFolderDetector/MediaInfoService | 低 | 测试面 0 引用 |
| 11 | GUI i18n 渲染/切换 | 低 | 服务层探针+静态扫描 |
| 12 | 硬件编码器真跑 | 低 | 旋钮活性差分有，GPU 缺席即 SKIP |

静态-only 门禁（不跑产品进程、只扫源码，现在账本**不区分**档位）14 支：
`_probe-cancel-propagation-scan`(#14)、`_probe-settings-clone-coverage`(#52)、
`_probe-stderr-drain-scan`(#10)、`_probe-stderr-merge-scan`(#56)、`_probe-proc-encoding-scan`(#11)、
`_probe-sync-read-before-wait-scan`(#42)、`_probe-ct-chain-closure-scan`(#45)、`_probe-i18n-scan`(#13)、
`_probe-cjk-hardcode-scan`(#5)、`_probe-gui-control-wiring`(#70)、`_probe-geometry-single-source-scan`(#37)、
`_probe-tilegrid-chain-lock`(#77)、`_probe-matrix-structure`(#53)、`_probe-host-temp-sites`(#93)；
另加混合型 6 支（startup-warmup/raw-default-tier/jxl-input-target/jpg-p3-icc/color-matrix-scan/white-point-kernel）。
清单外旧驱动 4 支：run-color-regress.ps1、run-full-matrix-test.ps1、run-full-matrix-test-safe.ps1、run-ps-verify.ps1。

### 1.3 机制（子 agent C）

- runner 1893 行；`$expectedScriptCount=100`（`:1221`）+ `$scriptList`（`:1222-1804`，74 verify-* +
  22 _probe-* + 4 run-*）；三处散文 `:3/:89/:1096`；⑱针 `$scriptArgs` 8 条目/7 个 `-SelfCheck`（`:1054-1082`，
  针 `:534-557`）。18 支形态针（`:438-559`）。**加/删门禁四处同步全有针兜住**。
- **兜不住的三处**：① docs/TESTING.md 接线台账无针（TESTING.md:601 自记「散文不在锁里的老病」）；
  ② 新 exe 进 STALE 闸（`:717-860`）无针（历史上两次「进了执行面没进闸」假绿）；③ ⑪-d 对
  _probe-/_make- 族新脚本只 WARN（`:1152`）。
- **per-script 超时表确认未做**（只有两条全局默认 `:70-71`；AGENTS「C4 未了结」属实）。
- **账本更正（本批核实）**：AGENTS §7 ⑪-d 条目写「要收紧须复用 AST 调用点索引建『CoveredBy ⇒ 调用点』
  双向检查（独立一批）」——**已过时**。2026-11-02 已落地：`_probe-test-group-coverage.ps1:1008-1088`
  调用点索引（AST 级，注释非 StringConstant 骗不到）+ D1–D4 双向判据（`:1142-1146`，自证 `:1171-1223`）
  ——Executed⇒每条 CoveredBy 真有站点、Superseded⇒恰零站点。⇒ ⑪-d 豁免表**已是强校验**，
  本方案批 1 的 ⑪-d 项只剩「_probe-/_make- 族新脚本从 WARN 收紧」这一半。

## 2. 分批计划（两线并行：批 1 机制线 ‖ 批 2 盲区线；批 3–5 顺承）

### 批 1（机制线）：机读模块账本 + 不落后针

**产物**：
1. `tests/scripts/_module-registry.json`（机读账本，判据文件 → 进 tests/scripts 平铺）：
   每模块一条：`{id, domain, capability, carrier[](门禁/宿主mode), tier(E2E|E2E-UI|static|service), evidence[](file:line), locks{}}`。
   首版登记面：CLI 全选项（CliParser.cs 逐 case）+ 13 能力域（B 报告 §一）+ 12 盲区（tier=blind 显式登记）+
   14 静态-only 门禁（tier=static 如实标）。**盲区/静态在账本里可见本身就是「覆盖范围正确」的第一步**。
2. 新门禁 `verify-module-registry.ps1`（清单 100→101，四处同步同批）：
   - 判据①（不落后针）：CliParser 解析出的**每个选项名**必须在账本有条目且 carrier 非空——
     新增选项不登记覆盖 = 整轮红（负控：删任一条目必须红）；
   - 判据②（不撒谎针）：tier=E2E 的条目，其 carrier 门禁/宿主源码里必须能找到该能力的运行时痕迹
     （选项字面量/宿主 mode 名落在执行面）——tier 标错=红；
   - 判据③：账本引用的 carrier 必须在 `$scriptList` 在册（防指向幽灵门禁）。
3. 三把尺收敛（同批或紧随小刀）：verify-ui-host `$arms` / param-matrix `$jExpected` / union
   `$expectedOldCount` 改从账本 `locks` 读（单点真值；跑三门禁验证等值）。

**验收**：新门禁受管绿 + 负控红 + 整轮不因登记缺漏假红（首版账本必须全绿起步）。

### 批 2（盲区线）：三个高危盲区 E2E 模块

| 模块 | 形态 | 判据草案 |
|---|---|---|
| 防覆盖契约 | headless CLI E2E（真 exe，verify-png-optimizer 模式） | `--no-overwrite` 遇既有产物：跳过+如实计数+旧产物字节不动；`--overwrite`：真覆盖；默认档语义钉死；进程退出码对账 |
| 取消路径 | UI headless 拆分件（Avalonia.Headless 真点停止） | 启动长任务→点停止→ffmpeg 子进程真死（无遗留句柄）→状态=已停止→无静默半截产物 |
| 损坏/截断输入 | ServiceProbe mode + headless E2E | 截断 PNG/JXL/HEIC：fail-closed 报错、零坏产物、队列失败计数如实；截断预检（>256MB 拒绝族）已有 D5 判据，补「文件级损坏」 |

每模块 = 新 verify-* 门禁（登记四处 + TESTING.md + **从出生就进账本**）。
**验收**：各自 E2E 绿 + 负控红 + mirror-parity/test-group-coverage 不因新增 mode 红。

### 批 3：ServiceProbe 源级 partial 拆分

- `ServiceProbe/Program.cs`（10.7k 行）按域拆成 `Modes/*.Domain.cs` partial class（B 快报里
  `tests/**/Modes/*.cs` 是 mirror-parity 的存在锁路径——**注意**：AGENTS §3 镜像锁钉的就是这个 glob，
  拆分前先读 verify-probe-mirror-parity 的锁面，避免同名冲突）；
- 拆分铁律：`Probe*` 方法仍**每派发 mode 唯一一份**（mirror-parity 逐名钉死）、大 switch 保留薄派发层、
  组表不动、**exe 哈希前后可比对**（纯源码搬家，IL 面不变 ⇒ dll sha16 应等值，这是拆分无行为漂移的硬证据）；
- 期望断言数登记表（mirror-parity :99-144，60 格）原样保留。

**验收**：sln 0 错（允许既有告警）；`ServiceProbe.dll` sha16 拆分前后**逐字节等值**；mirror-parity /
test-group-coverage / 受管子集全绿。

### 批 4：中/低盲区逐个补（每块独立小刀）

**执行序（2026-11-12 按价值/自洽性排定）**：
1. `blindspot.log-jsonlines`（最廉自洽：headless `--log-format JsonLines` 逐行 JSON 解析 + 必需字段锁，新 verify 门禁）；
2. `blindspot.ipc-single-instance`（真进程 E2E：二次启动→转发→退出，参照 verify-package-gui-starts 的 CreateProcess 驱动范式）；
3. `blindspot.settings-gui-restart`（headless UI：改选项→重建窗口→断言生效）；
4. `blindspot.gui-subwindows`（UiHostShared 扩展：四子窗口 headless 打开/关闭冒烟）；
5. `blindspot.render-plan-mediainfo`（三服务：ServiceProbe mode 或 headless 臂，需同批组表/parity）。
**维持 blind（有据，账本可见即可）**：`raw-non-dng-brands`（样本许可未清 ⇒ 不入仓，语料须落 `tests/output/_tmp/<批次>/corpus/`）、`hwencoder-real`（环境依赖，无恒真 GPU 臂）。
每块：先在账本把 tier 从 blind 改目标档，再实现，再跑双臂（正+负控）。

### 批 5：卫生与文档

- TESTING.md 逐条索引**从账本生成**（生成脚本 + 漂移闸：手工改生成段=红）；
- per-script 超时表（C4：钉进账本 locks，runner 读表）；
- 死支处置：ipc-*×6 在组表显式登记「手动诊断域」（账目守恒豁免）、`gainmath avifgm` 过期注释修正、
  清单外 4 支旧 run-* 驱动（登记或删除，走 ⑪-d 表）；
- AGENTS §7 ⑪-d 过时句更正（新写更正条目，不回改原文）。

## 3. 风险与铁律

1. **首版账本必须全绿**：先登记现状（含 blind/static 如实标），再上判红针——顺序反了第一轮就满屏假红。
2. **选项提取的边界**：CliParser 有别名/归一（jpegli→jpg）/泛型 `--key=value` 透传/负数取值——
   选项集合必须逐 case 数清，别把透传泛型当成具名选项误报。
3. **mirror-parity 的 glob 就是 `tests/**/Modes/*.cs`**：批 3 拆分文件命名要么避开要么先改锁（同批）。
4. **两条线都碰 runner/清单**：接线改动各自独立成刀，绝不与批 3 源码搬家混批（§8.5 主题封顶）。
5. 整轮基线：批 1+2 落地后跑一次 100→102 条整轮，作为 rc.1 出包前的最后基线。

## 4. 进度账

| 日期 | 批 | 进展 |
|---|---|---|
| 2026-11-12 | — | 立项；三路调研完成；决策裁定（partial/两线并行/rc.1 顺延）；⑪-d 更正核实 |
| 2026-11-12 | 批 1 | `_module-registry.json`（66 条目）+ 第 101 条 `verify-module-registry` 落地；受管 10/0 含负控；首轮负控抓出 ② 全局语料缺陷 → 改逐条目语料 3/3 |
| 2026-11-12 | 批 2·模块 1 | 第 102 条 `verify-overwrite-contract`（防覆盖契约 E2E）落地；受管 18/0 含负控、裸跑正向 17/0；产品行为取证 `tests/output/_tmp/overwrite-probe/` |
| 2026-11-12 | 登记 | 清单 100→102 四处同步 + ⑱针 8→10/7→9；TESTING.md 第二十九次接线；AGENTS §2/§4/§7 同批；受管子集 `t102-subset.log` rc=0 |
| 2026-11-12 | 批 2·模块 3 | #103 `verify-corrupt-input`（103 条）+ **真缺陷修复**：三处英文失败状态词 → 失败前缀中文（HasError 契约，exit 矛盾消除）；CJK 第二十四次抬表 1060→1063；实测 29/0 含负控 |
| 2026-11-12 | 批 2·模块 2 | G2 取消路径进单体（+6，按 InputPath 锁定断言）；三把尺 464/59 + 账本 locks；ui-host **464/0**、union 464/464、verbatim 22/0、reproducible 19/0 |
| 2026-11-12 | 批 3 | ServiceProbe 拆分（48 行壳 + Parts/65 文件；重建逐字节相等；nodebug 成对构建 **7770FDE924BB1995** 等值）；⚠ 验收前提更正：plain dll 哈希等值原理不可达（PDB 派生字节）；同批迁锁三处：mirror-parity 90/0、coverage 40/0、temp-sites 5/0；STALE 自适应 |
| 2026-11-12 | 批 4·1 | 第 104 条 `verify-log-jsonlines`（盲区#9 收口）：JsonLines 全行 schema 锁 + Info 过滤 + 双写等价 + fail-closed/不崩两边界；受管 30/0 含负控；清单 103→104；⚠ 判读链三处 pwsh 教训（ConvertFrom-Json 自动转 DateTime / 整数落 long / -in 大小写不敏感）+ ⑰针当场抓第四处散文漏改；账本：blindspot.log-jsonlines→cap.log-jsonlines、**blindspot.ipc-single-instance 改记产品缺口**（TryConnectAsync 生产侧 0 调用点 = S-P1-2 待拍板，先接线后立判据） |
| 2026-11-12 | 批 4·3+4 | N8 设置重启（Save 落盘/清缓存重读/LoadSettings 推控件；不 new 第二 MainWindow——托盘线程生命周期不受 Bootstrap 管）+ N9 四子窗口冒烟（真实构造→Show→控件在位→Close；不碰 ShowDialog 模态）进 GroupN +9 ⇒ ui-core 84/合计 **473**；实测单体 473/0、verbatim 22/0、reproducible 19/0、union 11/0（裸）、ui-host 473/0（受管）、module-registry 10/0（受管）；盲区 6/12 收口；⚠ 顺带钉出三既有问题：① FFMPEGGUI_FFMPEG_DIR 正斜杠混合路径 ⇒ 硬件编码器检测静默归零（产品缺陷 B1 家族，runner 已改反斜杠，产品归一化独立一批）② union 按 mode 过滤序列下 G2 确定性红（E,F,G-only ⇒ workers=[]+muxer EINVAL -22；全序列绿；union 改全组跑候选，独立一批）③ 宿主退出路径重写 exe 侧 settings.json（在场无害，登记机制） |
| 2026-11-12 | 批 4·5 | 三服务收口 = ServiceProbe 新 mode `mediasvc`（65_ProbeMediaSvc.cs，17 源文本调用位入 mirror-parity）+ verify-tool-detection §5 载体（5 CK）；media/plan/ps 三段锁面（否定路径契约 + 条件不变式 + 缓存幂等）；实测 mediasvc 9/0、VTD 24/0（负控按契约红）、mirror-parity 91/0、coverage 40/0、registry 9/0（domain 白名单教训）；盲区 7/12 收口，批 4 完成 |
| 2026-11-12 | 账本 | 4/12 盲区收口（no-overwrite/cancel/corrupt/--preset 更正）；cap.cancel-path/cap.corrupt-input 入账；指纹 F15B1AED2557040D / E62837233822D552 |
| 2026-11-12 | 收口 | 批 4·3+4 发现②收口：union 改全组跑（5 进程替代 19，与 ui-host 同形；判据八条不动；`$work` 迁产物区）；受管子集 ui-host 473/0、**union 11/0 @99.7s**（G2 确定性红消失）、registry 10/0；⚠ STALE 当场拦下旧件（TestGroups.cs 05:11 > 五件 exe 03:57）⇒ 重建后复跑——裸跑读数不作验收；取证 `tests/output/_tmp/union-fix/` |
| 2026-11-12 | 批 5·死支处置 | 三删一留（`git rm` run-color-regress / run-full-matrix-test(-safe)；run-ps-verify 留为手动 ACR 目检）+ `gainmath avifgm` 过期注释更正 + registry +1 条目 66→67（manual.serviceprobe-diag-modes，非覆盖缺口）；⑥ **fullrun 证据链**落地（(1b) 循环变量片段映射 + (5b) 无过滤全组作证 + union `$modes` 表改纯 exe 字面路径）；⚠ ⑥-d 回归 14 红 = union 表键字面量经 varMap 注入整条命令文本误判「带 mode 起件」（DBG5b 钉死，只有 ui-color/ui-sweep 两组组名==表键名故中招）⇒ modeInArgv 改**只扫实参元素**（cmdlet 形态 j≥2 / call-operator 形态 j≥1）；实测探针 **40/0**、union **11/0 @473**、registry **9/0**（裸跑自钉 TMP）；取证 `tests/output/_tmp/batch5-deadbranch/` |
| 2026-11-13 | 批 5·C4 | per-script 超时表落地：单一真值 = 账本 `locks."script-timeouts".table`（2 条 × 1200 s；实测 243.8 s / 748.6 s 的 ≥2× 余量），runner 在 `$scriptList` **之后**装载（幽灵键校验要对清单）+ 逐条查表覆写 `$_sTimeout` + 结果行 `to=<n>s` 读数；表只许抬（<900 = 门禁 ⑤ 判红）。⚠ **交接件里的 fail-closed 实为 fail-open**：装载失败原调 `Exit-WithSummary`，该函数在 `$failedItems` 空时打「全部通过」并 `exit 0` ⇒ 删键后整轮照绿；改 `Release-GatesLock; exit 2`（实测删键 ⇒ `registry-timeouts=FAIL` + rc=2 + 零脚本执行，还原后 JSON 哈希相等）。runner 形态自检 18→**19 针**（⑲ 五形 + 装载块位置；后两形是变异逼出来的：只改 `to=` 的 `-f` 实参时 ⑲ 曾全绿）。实测：`registry-timeouts=OK（2 条）`、color-strategy **78/0 @267.1 s to=1200s**、registry **12/0**（5/5 负控）、script-list 104、错名仍拒（rc=1）；四支变异各红各条 |
| 2026-11-13 | 批 5·索引 | TESTING.md 逐条索引从账本生成：`verify-module-registry.ps1 -EmitIndex` 渲染 `<!-- module-index:begin/end -->` 段（**§九、文末追加**——中间插入会挪 coverage/cjk 两条门禁的行号锚）；默认跑 = ⑥ 漂移闸逐字节对撞并报「段内第 N 行：现存[…] vs 生成[…]」；现量 **67 条 / 101 行 / 8777 B**，按 domains 声明序分组、组内序数排序、carrier 按名排序 ⇒ 两次生成哈希不变；实文件变异两支（手改生成段 / 改账本不重生成）各抓到；两坑：`[Array]::Sort(keys, object[] items, cmp)` keys 排 items 不动（改索引表）、`Sort-Object` 走文化排序（改序数） |

| 2026-11-13 | 批 5·余波（全量轮） | 全量 104 条 2h00m53s / rc=1 ⇒ 抓到三类陈账：**① 门禁污染产品设置**（startup-warmup S8f/S8g 的 env 覆盖被 `EnsureFfmpegDirectory` 的自动发现分支 `AppSettingsService.Save()` 落盘 ⇒ `DngToolPath=ffmpeg.exe` + `WindowsArtifactsDir=门禁临时目录`，下游 `metadata-privacy` ⑥ 假红）；修法 = `--settings <临时副本>` 隔离 + 新判据 **S8h**（前后 sha 对撞），变异验证 = 摘掉 `--settings` 则 S8h 红且污染逐字复现。**② 判据锚点漂移**（startup-warmup 状态行正则不认 ` [PLAN]` 来源后缀 ⇒ 10 条假红；heartbeat `RunAsync` 26→27，新增点 `QueueProcessor.cs:6814`@e592227；`_lib-matrix` 六锚 1156→1329/1168→1341/852→878/2563→2767/804→830/1200→1373，`git log -S` 证字面量未变）。**③ 剩余 8 红属别的批**（xcheck=库侧滞后；ui-host/ui-split-union=G2 `workers=[]`；package-gui-starts ⑥=提权会话托盘族；color-token-normalize=整轮内 900 s 挂死但单跑 52 s 绿；release-package=包内工具指纹滞后；host-temp-sites=2 新站点未入表；color-matrix-scan=锚点 miss + `xyb` 命中 6）。取证 `tests/output/_tmp/fullrun-1113/` |
