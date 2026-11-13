# 工作区审计（2026-11-13）

> 审计对象：`C:\PLAN\ffmpegPictureUI`；HEAD `2c6bba2`（`main` = `origin/main`，0 ahead / 0 behind）。
> 审计只改文档、测试归置与无引用残留；**未改 `src/`，未改任何门禁判据**。
> 全部读数与命令输出存 `tests/output/_tmp/workspace-audit-20261113/`：
> `workspace-metrics.txt`（构成与现值）、`tag-table.md` / `tag-audit.txt`（标签核对）、
> `commit-stats.txt`（提交粒度）、`commit-prefixes.txt`（消息类型）、`top-files.txt`（产物区大件）、
> `hygiene/`（从仓库根移出的日志与旧诊断脚本）。
> 按龄清扫的删除清单：`tests/output/_layout/sweep-runs-20261113-113430.tsv`（2522 行）。

## 1. 工作区构成

**tracked 文件 = 552**；磁盘体量按层如下（`src` / `tests/projects` 含 `bin`/`obj`，所以磁盘文件数远大于 tracked）。

| 层 | tracked | 磁盘读数 | 判断 |
|---|---:|---|---|
| `src/FfmpegGui/` | 105 | 1,470.5 MB / 1,087 文件 | 产品源码 + 本地构建产物；tracked 只 105 |
| `tests/projects/` | 103 | 2,354.5 MB / 3,076 文件 | 13 个测试工程；`bin`/`obj` 占大头（`tests` 全层 tracked 288 = 103 + 175 + 7 + 3） |
| `tests/scripts/` | 175（`tests` 全层） | 4.1 MB / 181 文件 | 104 条门禁 + 探针/库；平铺 |
| `tests/data/` + `tests/fixtures/` | 7 + 3 | 0.2 + 0.2 MB | 小体量夹具 |
| `tests/output/` | 0（整树 gitignore） | **41.38 GB → 36.40 GB**；78,912 → 60,637 文件 | 产物区；本次按龄清 5.0 GB |
| `docs/` | 127 | 4.7 MB / 127 文件 | 设计与账本；`TESTING.md` 730 KB、`HANDOVER.md` 441 KB、`IMPLEMENT_PLAN_2026-10-11.md` 501 KB |
| `tools/` | 10 | 1,629.7 MB / 23,748 文件 | vendored 上游 + 本地构建依赖；tracked 只 10 |
| `publish/` | 0（整树 gitignore） | 662.2 MB / 1,175 文件 | 只留 beta13 双包 + `PLAN` + `build` |
| `licenses/` | 9 | 0.1 MB | 第三方许可 |
| 仓库根 | 13 | — | 法律/说明/规范 + `pack.ps1` + `global.json` + `_guishot.ps1`（gitignore，按 §7 留根） |

`git worktree list` 只有主工作树；`.qoder/worktrees` 为空目录，IDE 管理，未动。

## 2. 测试层归置

### 2.1 清单与实际文件一致

- `$scriptList` **104 个唯一项**，`$expectedScriptCount = 104`；104 个文件全部存在，**无缺失**。
- 磁盘上 `verify-*.ps1` **78 个**，**全部在册**（无清单外 verify 脚本）。
- 13 个测试工程（11 Exe 宿主 + `Tests.Shared` / `UiHostShared` 两个 classlib）。
- `_module-registry.json` **67 条目**；UI 三把尺 = `473` / `126` / `473`（与 `verify-ui-host.ps1`、`verify-ui-param-matrix.ps1`、`verify-ui-split-union.ps1` 源码同值）。
- 宿主 TEMP 站点表 **18 文件 / 40 站点**（`_probe-host-temp-sites.ps1` 静态核对 40==40）。

### 2.2 发现并已归置的违例

| 位置 | 问题 | 处置 |
|---|---|---|
| `tests/scripts/_archived_diag/` | 3 个 **tracked** 旧诊断脚本（`final-verify` / `regtest-fix` / `verify-remaining`），含 `C:\temp\...` 绝对路径与已不存在的 `publish/build/FFmpegPictureUI-dev-*`；子目录使归属探针（非递归扫描）看不见它们 | 移到 `tests/output/_tmp/workspace-audit-20261113/hygiene/legacy-diag-2026-09/`（内容保留），从 `tests/scripts` 移除 |
| `tests/scripts/__pycache__/` | Python 缓存（6 个 `.pyc`） | 删除（可再生，gitignore） |
| `tests/tests/output/_tmp/round-tmp/` | 空目录（2026-11-04 残留） | 删除 |
| 仓库根 `$root/tests/output/_tmp/heic-fix-r2-20261109/` | 变量未展开造成的字面 `$root` 目录；4 个 0 字节文件 | 删除（0 数据） |
| 仓库根 `NVIDIA Corporation/umdlogs/` | 空目录（驱动日志，gitignore） | 删除 |
| 仓库根 `debug.log` | 50,401 B，搜狗输入法日志（非本仓产物） | 移到 `hygiene/debug-sogou-ime.log` |
| 仓库根 `gude-2026-10-24.log` | 468 B，WinHTTP 报错（非本仓产物） | 移到 `hygiene/gude-winhttp-error-2026-10-24.log` |

清理后 `tests/scripts` 下**无子目录**；`tests/` 下不再套 `tests/`；仓库根只剩白名单文件（`_guishot.ps1` 是项目规则明确允许的例外）。

> 本次提交后 `tests/scripts` tracked 175 → **172**、`tests` 全层 288 → **285**、`docs/` 127 → **129**；§1 表与本节按审计时（提交前）读数记。

### 2.3 产物区按龄清扫

按项目自有规则执行：

```powershell
py tests/scripts/_sweep-output.py --runs --max-age-days 7 --apply
```

- 候选 2,522 个轮次目录 = 5.32 GB；实际删除 **2,518 个**（4 个未删，未逐项排查，manifest 保留完整清单），释放约 **5.0 GB**。
- 保护面：**89 个**被在册文本点名的目录、**525 个**保留期内目录不动；顶层日志（`tests/output/*.log`、`_gate_*.txt` 等）**不在**该档清理范围。
- 结果：`tests/output` 从 **41.38 GB / 78,912 文件** 降到 **36.40 GB / 60,637 文件**。
- 未做 `--unreferenced`（零引用档）：项目规则明确警告它会删掉“手工跑出来的活线产物”（2026-10-22 已有不可复原的教训）。

### 2.4 未误动的测试内容

- `tests/projects/**/*.cs` 全部留在指定工程目录；`tests/output` 里的 `*.cs` 是变异备份/历史快照（gitignore 的产物），不是第二份测试源码树。
- `.workbuddy-ai/`、`.zwork/`、`.arts/`、`.codeartsdoer/`、`.vscode/` 是 IDE/工具状态（gitignore），未按“测试文件”处理。
- `tests/data/synth_heic_alpha.heic`、`tests/output/corpus/gainmap-iso21496/` 等被门禁点名的夹具/语料保持原位。

## 3. 记忆文件精简（AGENTS.md）

| 指标 | 精简前 | 精简后 |
|---|---:|---:|
| 字节 | 81,940 | **18,824** |
| 行数（总 / 非空） | 729 / 699 | **194 / 162** |
| 降幅 | — | **约 77%** |

- 保留：§1 目录契约、§2 门禁登记、§3 测试硬规矩、§4 验收配方、§5 高危区、§6 文档口径、§8 Git 历史纪律（章节号不变，外部引用的 `AGENTS §7` / `§8` 不悬空）。
- §7 由“按时间堆叠的 65 KB 未了结账本”收成：**现行测试/门禁现值** + **产品口径（会直接影响判据的 9 条）** + **未了结（7 条）**。
- 旧 §7 全文 **41,166 字符 / 65,491 B 逐字快照**存 `docs/review-2026-11-13/AGENTS_HISTORY_2026-11-13.md`（只作参考，不是当前口径）。

### 3.1 已消除的“左右脑互搏”

| 旧陈述（同一文件内互相矛盾） | 2026-11-13 实测 | 处置 |
|---|---|---|
| “脚本清单现值 = 99 条”（2026-11-10），随后又写“权威值 = 100”，再往后写 102/103/104 | `$scriptList` 104、`$expectedScriptCount` 104 | §2 只留 104；历史读数进快照 |
| UI 锁“合计 430”/“446”/“454”/“458”/“464”，同文件又写“473” | `verify-ui-host` = 84+59+89+169+72 = **473** | §7 只留 473；旧值进快照 |
| 宿主 TEMP“39 站点 / 7 文件”，后文又写“41 vs 39”“40 == 40” | 站点表 **18 文件 / 40 站点** | §3 只留 40；旧值进快照 |
| “产物区 16 GB / 215 项”（2026-10-22） | 审计时 **41.38 GB / 78,912 文件** | 审计文档记当日读数；AGENTS 不再写易变体量 |
| §4 验收配方让直接跑 `_run-step3-gates.ps1`，§3 却规定“无选择器 = `exit 2`” | 运行器确实拒跑 | §4 改为 `-ListUnits` / `-Domain` / `-Scripts`，`-FullBaseline` 只在用户明确要求时用 |
| §4 写“整轮墙钟未在 104/104 下重测（94 条时代 ~60 分钟）”，§7 又写全量 104 条 ≈ 2 h | 项目记录的 104 条全量 ≈ **2 h**（本审计未重跑全量） | 统一为 104 条 ≈ 2 h |
| §8 依据“159 提交 / 3 merge / 33 tag” | 214 提交 / 3 merge / 35 tag | §8 更新审计基线 |
| 多处“94/94 全绿” | 当前 104 条 | 旧读数进快照，不再作为当前口径 |

## 4. Git 历史与版本 TAG

### 4.1 总量

- **214 提交**（first-parent 206）、**3 merge**、**35 tag**（远端 34）。
- 提交粒度：`1 文件` 38 笔、`2–5` 83 笔、`6–20` 69 笔、`21–50` 13 笔、`>50` 11 笔。
  **57%（121/214）只改 ≤5 个文件**；>50 文件的 11 笔集中在测试工程物理拆分、仓库追踪补齐、版本发布等里程碑。
- 消息前缀：`docs` 42、`feat` 39、`fix` 26、`test` 19、`release` 14、`chore` 4、`build` 3、`perf` 2、`merge` 2、`wip` 1；
  **62 笔无标准前缀**（含 `build+docs:`、`feat+test+docs:` 这类复合前缀与中文标题）。
- 月度：2026-05 11、06 25、07 19、08 6、09 6、10 90、11 57。

### 4.2 TAG 核对（完整表见附录 A）

| 结论 | 证据 |
|---|---|
| **release 提交洁净度**：`BETA8`/`BETA12`/`BETA13` 各 **3 文件**（版本位 + 双语 CHANGELOG）；`BETA12`/`BETA13` 是 §8 立规后的合规样本。`BETA7` 5 文件（另加 2 份 docs）、`BETA9` 37 文件（版本位 + CHANGELOG + 产品/门禁/tool 多主题）——均在 2026-11-02 立规前 | `git show --stat <tag>` |
| **版本位与 tag 不一致 2 处**：`1.4.5` 锚 `1.4.4`；`1.5.0BETA3` 锚 `1.5.0-beta2` | `git show <tag>:src/FfmpegGui/FfmpegGui.csproj` |
| `1.0.0` 锚初始提交（无 `<Version>`，无法核对） | 同上 |
| **tag 锚非 release 提交 3 处（1.6.0 系）**：`BETA5`（fix gamutfit）、`BETA6`（fix gate）、`BETA10`（fix gui），均在 2026-11-02 §8 立规**之前** | `git log -1 --format=%s <tag>` |
| `BETA10` 的 annotated 消息自称“**第四次重出**：同一版本号不升版” | `git tag -n99 1.6.0BETA10` |
| **`1.6.0BETA12` 只在本地，未推送远端**（本地 35 tag / 远端 34） | `git ls-remote --tags origin`；`git cat-file -t` = tag |
| 编号缺口：1.6.0 系无 `BETA4`/`BETA11`；`BETA11` 在 CHANGELOG/账本有出包记录但无 tag；`1.5.2` 无 tag | `git tag` 列表 + `CHANGELOG.md` |

### 4.3 提交笔数是否合理

**结论：笔数合理，粒度总体偏细；问题不在数量，在 tag 卫生与消息结构。**

- 1.6.0 系 **10 个 tag 之间的 9 个间隔提交数**（含本 tag 指向的提交）：`1 / 25 / 5 / 2 / 29 / 4 / 22 / 19 / 39`；中位数 19，最大 39（`BETA12→BETA13`，9 天 38 笔工作提交 + 1 笔 release）。
- 没有“1 笔提交就发版”的异常（`BETA3` 间隔 1 是因为它紧接 1.6.0 后只装版本位与 CHANGELOG，属正常 release 提交）。
- 3 个较大的批次（`BETA3→BETA5` 24 笔工作、`BETA7→BETA8` 28 笔、`BETA12→BETA13` 38 笔）都对应跨主题的工程批（门禁拆分、测试模块化、检测一致性），符合 §8.5“批次收口必须自带分节清单”的代价条款。
- 可读性欠账：62/214 无标准前缀；`1.6.0` release 提交改了 **123 文件**（+15,246/−1,494），是“release 提交只装版本位 + CHANGELOG”规则立规前的反例。`BETA12`/`BETA13` 已按新规执行（各 3 文件）。

**建议（未执行，涉及共享远端/历史，需明确授权）**：

1. 推送 `1.6.0BETA12` 到远端（当前 `git push origin 1.6.0BETA12` 即可；这是唯一一个本地多出的 tag）。
2. 不修 `1.4.5` / `1.5.0BETA3` / `BETA5/6/10`——按 §8“历史本体不回写”，只在账本留更正条目（本文件即该条目）。
3. 后续 release 继续按 `BETA12`/`BETA13` 形态：annotated tag 锚“只改版本位 + 双语 CHANGELOG”的 release 提交，且 tag 与版本位同名同号。

## 5. 保留未动（有参考价值）

- `tests/output` 剩余 **36.40 GB / 60,637 文件**：`results`（6.2 GB）、`validate`（2.7 GB）、`png-opt-bench`（2.2 GB）、`rawg`（2.0 GB）、`raw-probe-20261026`（2.0 GB）、`raw_samples*`、`corpus/` 等。这些含手工基线、实拍语料与红轮证据，**未做零引用清仓**；要再降体量须逐目录读清单（项目规则：零引用 ≠ 无用）。
- `docs/` 127 份文档（4.7 MB）：`TESTING.md`、`HANDOVER.md`、`IMPLEMENT_PLAN_2026-10-11.md` 等是承重账本，未删未挪。
- `publish/` 只留 beta13 双包 + `PLAN` + `build`（符合 §1“只留最新一档”）；`publish/rescued-*` 未动。
- 仓库根 `_guishot.ps1`（gitignore，§7 明确不许挪进 `tests/scripts`）。
- `tests/projects/GainMapTestHost/PLAN` 的 junction 未动（§5 高危区）。

## 6. 复核命令

```powershell
# 构成 / 清单
git -C C:\PLAN\ffmpegPictureUI ls-files | Measure-Object -Line
git -C C:\PLAN\ffmpegPictureUI log --oneline | Measure-Object -Line
Select-String tests/scripts/_run-step3-gates.ps1 -Pattern '\$expectedScriptCount'
# 标签
git -C C:\PLAN\ffmpegPictureUI tag --sort=creatordate
git -C C:\PLAN\ffmpegPictureUI ls-remote --tags origin
# 产物区
py tests/scripts/_sweep-output.py --runs --max-age-days 7          # dry-run
```

---

## 附录 A：TAG 完整核对表（2026-11-13）

| Tag | Type | Date | Version@tag | Match | release? | commits since prev | Subject |
|---|---|---|---|---|---|---|---|
| 1.0.0 | commit | 2026-05-17 | `` | — | N | 1 | Add project.nuget.cache file with package dependencies and vulnerabili |
| 1.0.1 | commit | 2026-05-18 | `1.0.1` | OK | N | 5 | Merge branch 'main' of https://github.com/luoye-cpu/ffmpegPictureUI |
| 1.1.0 | commit | 2026-05-18 | `1.1.0` | OK | N | 1 | feat: release v1.1.0 with cjxl integration for ultra-fast JPEG to JXL  |
| 1.2.1 | commit | 2026-05-25 | `1.2.1` | OK | N | 1 | Update FfmpegGui to version 1.2.1 with various improvements and binary |
| 1.2.2 | commit | 2026-05-26 | `1.2.2` | OK | N | 1 | Update FfmpegGui to version 1.2.2 and add Microsoft.NET.ILLink.Tasks p |
| 1.2.3 | commit | 2026-05-26 | `1.2.3` | OK | N | 1 | Bump version to 1.2.3 and update assembly info, dependencies, and proj |
| 1.3.0 | commit | 2026-05-26 | `1.3.0` | OK | N | 1 | 1.3.0 |
| 1.3.1 | commit | 2026-06-02 | `1.3.1` | OK | N | 2 | Remove obsolete NuGet cache file for VerifyQueue project |
| 1.3.2 | commit | 2026-06-03 | `1.3.2` | OK | N | 2 | 添加 GNU 通用公共许可证 (GPL) 版本 3 文档 |
| 1.3.3 | commit | 2026-06-04 | `1.3.3` | OK | N | 2 | feat: 更新 v1.3.3 更新日志，添加队列进度显示和智能回退功能 |
| 1.3.4 | commit | 2026-06-04 | `1.3.4` | OK | N | 2 | feat: 更新元数据编辑器，增加字段和类别，修复拖放功能，提升界面体验，版本更新至 1.3.4 |
| 1.4.0 | commit | 2026-06-04 | `1.4.0` | OK | N | 2 | feat: release v1.4.0 BETA with animated format support and UI enhancem |
| 1.4.1 | commit | 2026-06-05 | `1.4.1` | OK | N | 2 | feat: 更新 v1.4.1 BETA，修复动图编码，增强透明通道处理，移除 avifenc 集成，优化元数据保留 |
| 1.4.2 | commit | 2026-06-06 | `1.4.2` | OK | N | 1 | feat: 更新至 v1.4.2，修复动图质量分析与编码选项，增强 JXL 动画支持 |
| 1.4.3 | commit | 2026-06-08 | `1.4.3` | OK | N | 5 | feat: 添加 HEIC 和 DNG 输入支持，优化 DNG 转换失败提示 |
| 1.4.4 | commit | 2026-06-12 | `1.4.4` | OK | N | 1 | feat: Enhance FfmpegGui with new features and improvements |
| 1.4.5 | commit | 2026-06-20 | `1.4.4` | MISMATCH | N | 1 | feat: Add support for Ultrahdr and Jxr encoding in QueueProcessor |
| 1.5.0BETA | commit | 2026-07-14 | `1.5.0-beta` | OK | N | 12 | feat: Enhance format capabilities and ICC profile handling |
| 1.5.0BETA2 | commit | 2026-07-14 | `1.5.0-beta2` | OK | N | 4 | feat: Implement Simple Mode for streamlined file conversion |
| 1.5.0BETA3 | commit | 2026-07-15 | `1.5.0-beta2` | MISMATCH | N | 1 | Add color management tests and project configurations |
| 1.5.0 | commit | 2026-07-23 | `1.5.0` | OK | N | 4 | Refactor technical roadmap and update UI components |
| 1.5.1 | commit | 2026-07-27 | `1.5.1` | OK | N | 3 | 打包相关规范更新 |
| 1.5.3 | commit | 2026-08-15 | `1.5.3` | OK | N | 3 | feat：md |
| 1.5.4 | commit | 2026-08-16 | `1.5.4` | OK | N | 1 | feat：1.5.4 |
| 1.5.5 | commit | 2026-08-30 | `1.5.5` | OK | N | 2 | feat：1.5.5 |
| 1.6.0 | tag | 2026-09-28 | `1.6.0` | OK | Y | 6 | release: v1.6.0 正式版（色彩矩阵正确性修复 + GainMap 加固 + 双语本地化） |
| 1.6.0BETA3 | commit | 2026-10-01 | `1.6.0-beta3` | OK | Y | 1 | release: v1.6.0-beta3（门禁假绿机制修复 + 首批 L3 出货包判据 + pack.ps1 完整性闸） |
| 1.6.0BETA5 | tag | 2026-10-06 | `1.6.0-beta5` | OK | N | 25 | fix(gamutfit): 小样本下 99.9 分位退化成 max ⇒ **静默误判「装得下 bt709」** |
| 1.6.0BETA6 | tag | 2026-10-08 | `1.6.0-beta6` | OK | N | 5 | fix(gate): verify-jxl-probe-timeout 把「墙钟下界」改为「行为证据优先」⇒ 修并发假红 |
| 1.6.0BETA7 | tag | 2026-10-10 | `1.6.0-beta7` | OK | Y | 2 | release: v1.6.0-beta7 版本号 + 双语 CHANGELOG + 两份实现文档（成因分层与性能账） |
| 1.6.0BETA8 | tag | 2026-10-23 | `1.6.0-beta8` | OK | Y | 29 | release: v1.6.0-beta8（厂商裁剪进 DefaultCrop + 逐实例账本收口 + 测试工程迁入 tests/proje |
| 1.6.0BETA9 | tag | 2026-10-27 | `1.6.0-beta9` | OK | Y | 4 | release: v1.6.0-beta9（AVIF 出口 ICC 诚实性收口 + GainMap 元数据三态 + bench 自钉快核簇； |
| 1.6.0BETA10 | tag | 2026-10-30 | `1.6.0-beta10` | OK | N | 22 | fix(gui): 把 JPEG XR 加回输出格式下拉（2026-08-12 efc0622 误删，JXR 出口在 GUI 里断头）+ 补 |
| 1.6.0BETA12 | tag | 2026-11-04 | `1.6.0-beta12` | OK | Y | 19 | release: v1.6.0-beta12（TG 视频贴纸 + 托盘黑洞原生修复 + GainMap 批次 E） |
| 1.6.0BETA13 | tag | 2026-11-13 | `1.6.0-beta13` | OK | Y | 39 | release: v1.6.0-beta13（检测一致性 + JXL 增益图两侧 + .NET 11 GA + 测试模块化 94→104） |

> 读法：`Version@tag` 取该 tag 指向提交的 `src/FfmpegGui/FfmpegGui.csproj`；`Match` 为 tag 名与版本位是否同名同号；
> `commits since prev` 含本 tag 指向的提交（首个 tag 为到该提交的全量提交数）；`release?` = 提交消息是否以 `release:` 开头。

## 附录 B：按龄清扫清单

- 工具：`tests/scripts/_sweep-output.py`（不跟随 reparse point；在册文本点名保护）。
- dry-run 读数：`run-dirs: 陈旧可删 2522 个 = 5.32 GB ｜ 保留期内 525 ｜ 被在册文本点名 89`
- 实际：`applied run-dir sweep: 2518 removed`
- 清单：`tests/output/_layout/sweep-runs-20261113-113430.tsv`（首行注释 + 2522 行 `相对路径<TAB>字节`）。
- 大头示例：`_tmp/w3c` 375.2 MB、`_tmp/jpegli-src` 327.2 MB、`_manual_heic_gm/ff_ccb85eb0` 320.4 MB、`_tmp/dng_new` 126.0 MB、`jxr_*` 多个 114.8 MB stub。
