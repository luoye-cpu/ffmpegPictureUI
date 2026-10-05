# 测试物理拆分方案（2026-10-05，按用户口径：一个子系统一个独立工程）

> 用户裁定：**物理拆分**（每子系统一个 `.csproj`）+ **更细粒度**（接近一个 mode 一组）。
> 本文件是实现前的**最终方案**（替代 `TEST_SPLIT_DESIGN_2026-10-05.md` 里"宿主内分组"的初版）。

---

## 0. 硬约束（不可违反）

1. **既有 68 条门禁 + 20+ 个直调脚本必须零改动**。实测调用面：
   `ServiceProbe.exe <mode> [args]`、`UiTestHost.exe`、`GainMapTestHost.exe`
   ⇒ 三个 exe 的**文件名、参数、stdout 形状（`PASS n`/`FAIL n`/`SKIP`）、退出码**逐字保留。
2. **`_run-step3-gates.ps1` 的 STALE 闸**显式比对 4 份产物的新鲜度
   （`src/**/FfmpegGui.exe`、`ServiceProbe.exe`、`UiTestHost.exe`、`GainMapTestHost.exe`）
   ⇒ 这四个**必须继续产出**，否则 STALE 闸 `exit 2`。
3. **构建顺序**：改 `src/FfmpegGui` 必须重建依赖它的全部测试工程（否则 STALE 拦）。

---

## 1. 目标结构

```
tests/
  Tests.Shared/              ← 共享底座：Harness（计数/输出/子进程/工具定位）+ TestGroups（组表）
  core/                      ← 决策层：契约/路由/裁决/计划/落盘
  color/                     ← 色彩基元：矩阵/曲线/OOTF/HLG/BT.2446/PQ
  engine/                    ← 自研引擎：engine/gamut/gamutfit/simdswitch
  encode/                    ← 编码参数与产物：encoding/bitdepth/faithful/png3/thumbnail
  metadata/                  ← 元数据与 ICC：metaraw/icc/icc-dump/faithful(exif)
  gainmap/                   ← 增益图/Ultra HDR：gainmap/ultrahdr/gainmap-isobmff/oog/avifgm
  geometry/                  ← 几何：geometry/firstframe/orientation/orientmeta
  io/                        ← 进程与 IPC：ipc*/procstreams/encoding
  diagnostics/               ← 诊断/性能：bench/peak/peakmeasure/psnr/xcheck/selftest/tooldetect/i18n
  ui/                        ← GUI（原 UiTestHost 的 13 组按子系统再分）
  ServiceProbe/  UiTestHost/ GainMapTestHost/   ← **保留为"聚合入口"**（契约兼容层）
```

**关键设计**：每个子系统工程 = **一个可独立 `dotnet run` 的测试组**，**自带 `Main`**，
可单独跑、单独看结果、单独判绿。同时三个旧宿主**保留**，其 `Main` 变为
"按组调用各子系统"，从而**既有门禁零改动**。

---

## 2. 为什么保留旧宿主（而非直接删）

- 20+ 个门禁脚本硬编码了 `ServiceProbe.exe <mode>` 路径与形状；
  直接删 = 一次性打碎门禁体系（本仓最忌"为重构而打碎判据"）。
- 保留后：**新结构用于日常开发**（改哪块跑哪块），**旧入口用于门禁兼容**。
  这是"加法式重构"：任何时刻旧行为都还在。

---

## 3. 分组表（`Tests.Shared/TestGroups.cs`，单一真值）

| 工程 | 组名 | 覆盖的 ServiceProbe mode |
|---|---|---|
| `core` | `core` | `contract` `verdict` `plan` `decision` `wire` `runner` `settings` |
| `color` | `color` | `matrix` `curve` `ootf` `hlgwire` `bt2446` `pq` `colormath` `iccname` |
| `engine` | `engine` | `engine` `gamut` `gamutfit` `simdswitch` |
| `encode` | `encode` | `encoding` `bitdepth` `png3` `thumbnail` |
| `metadata` | `metadata` | `metaraw` `icc` `icc-dump` `faithful` |
| `gainmap` | `gainmap` | `gainmap` `ultrahdr` `gainmap-isobmff` `oog` `avifgm` |
| `geometry` | `geometry` | `geometry` `firstframe` `orientation` `orientmeta` |
| `io` | `io` | `ipc` `procstreams` |
| `diagnostics` | `diagnostics` | `bench` `peak` `peakmeasure` `psnr` `xcheck` `selftest` `tooldetect` `i18n` |

**一致性自检（新增门禁断言）**：上表覆盖的 mode 集合 **必须等于** `_run-step3-gates.ps1`
的 `$Probes` 默认清单（多一个/少一个都转红）—— 防"新增 mode 忘了归组"导致该 mode
**永远不被任何组跑到**（= 假覆盖，本仓有第 54 条"存在但没上清单 ⇒ 谁也不跑"的教训）。

---

## 4. 实施顺序（每步都可独立验收，不做"大爆炸"切换）

| 步 | 内容 | 验收 |
|---|---|---|
| **S1** | 建 `Tests.Shared`（Harness + TestGroups） | 库单独 `build` 0/0 |
| **S2** | 建 `core` 组工程（先挑依赖最少的一组打样） | 该工程单独跑 == 旧 `ServiceProbe contract` 的读数**逐条一致** |
| **S3** | 其余 8 组工程按同一模板铺开 | 每组单独跑；全组并集 == 旧全量读数 |
| **S4** | 旧 `ServiceProbe/Main` 改为按组委派（**外观与退出码不变**） | 既有 68 条门禁**全绿**（零脚本改动） |
| **S5** | `UiTestHost` / `GainMapTestHost` 同法 → `ui/*`、`gainmap/*` | 同上 |
| **S6** | 新增"组表 ↔ `$Probes` 一致性"门禁 + 变异验证 | 删一个 mode ⇒ 转红 |
| **S7** | 文档 + `docs/TESTING.md` 更新 | — |

⚠ **S4 之前不动旧宿主** —— 保证任何中断点上仓库都是可用的。

---

## 5. 已知风险与对策

| 风险 | 对策 |
|---|---|
| 46 个 `ProbeXxx` 共享 `Program` 的私有静态字段（`_pass/_fail/_skip`） | 已实测**只有这 3 个计数器**是共享状态 ⇒ 用 `Harness` 承接，各组 `Check()` 调它 |
| 拆出去后 `_run-step3-gates.ps1` 的 STALE 闸找不到 `ServiceProbe.exe` | 保留旧宿主工程，其输出路径与文件名**一字不改** |
| 组表与 `$Probes` 漂移 | S6 的一致性门禁（**必须变异验证**） |
| 构建时间因工程数增加而变长 | 实测对比"单组构建 vs 全解决方案构建"并记入文档；必要时加 `tests/All.slnf`（解决方案筛选器）只建所需子集 |
| 拆分期间误改产品代码 | 本拆分**只动 `tests/**`**；`src/FfmpegGui` 仅包含本轮 P0 修复（CLI 崩溃） |

---

## 6. 本次已顺带修出的产品缺陷（管线审查产物，独立于拆分）

**P0：非法 CLI 取值崩溃而非 exit 2**（已修，实测）
```
FfmpegGui.exe --headless -i a.png -o out -f png -q abc
  修前: Unhandled exception. ArgumentException: --quality 取值无效…  ⇒ exit -532462766 (0xE0434352)
  修后: 错误: --quality 取值无效: 'abc'（需要整数）                ⇒ exit 2  ✅
```
同族：`-j/--jobs`、`--log-level`、`--log-format`、`-e/--encoder`。
根因：`CliParser.Parse` 抛 `ArgumentException`（有意的 fail-closed），但 `Program.Main`
**没有 try/catch** 接住（`RunHeadless` 的 catch 只包更晚的 `BuildJobSpecs`）。
