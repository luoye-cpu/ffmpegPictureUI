# RC2 重出包报告（2026-11-25，AVIF M 修复补入）

## 1. 结论

版本位保持 `1.6.0-rc2`，按“重出包”处理。旧 RC2 两档（2026-11-24 14:55/14:59）是在 AVIF legacy 矩阵修复 `e28bd29` 之前打出的，包内候选 DLL 为 `2B90DE92DA2F937B`；本轮从 `b04ec86` 重新构建、重跑门禁并重出两档，新包候选 DLL 为 `2BC80D80748C6968`，已包含 AVIF legacy 路径的 BT.601/SMPTE170M 矩阵兜底。

`e28bd29` 的修复范围：当 `fmt0 == "avif"`、源为 RGB 族、命令已有 `-pix_fmt yuv*` 且 `outMatrixToken` 为空时，在 legacy 出口补 `-colorspace smpte170m`，避免 libaom 在 AVIF nclx 落 `M=2`。`gbrp` identity 路径和显式矩阵路径不动。

## 2. 版本与产物

- 版本源：`src/FfmpegGui/FfmpegGui.csproj` = `1.6.0-rc2`。
- 源提交：`b04ec8608cf861327ab97b6b6afcc8201406df1e`（含 `e28bd29`）。
- 候选 `FfmpegGui.dll`：`2BC80D80748C6968`（2,215,936 B），ProductVersion `1.6.0-rc2+b04ec86…`。

| 档位 | 文件 | 字节 | SHA-256 |
|---|---|---:|---|
| 单文件 app | `publish/FFmpegPictureUI-v1.6.0-rc2-x64.7z` | 12,117,401 | `CCEFA26462AA7B0F9DE17D445578D6AFD739795AC37B912CE66696AB23FEE5BC` |
| 完整 full | `publish/FFmpegPictureUI-v1.6.0-rc2-x64-full.7z` | 56,518,339 | `068A1D8AC10F2AF19A7BDDDD20D4B9AA8B9D9034AF0E13A6E2CB972C8B651117` |

包内 exe：

- app：`E112776DA7923050`（25,099,264 B）
- full：`F42733913C8B80CB`（25,099,264 B）

两档 `ProductVersion` 均为 `1.6.0-rc2+b04ec86…`。`7z t` 两档均 `rc=0`：app 19 文件，full 584 文件。

## 3. 门禁与回归

本轮只跑重出包链路，不跑 104 条全量；受管运行器使用 `-Probes contract` + 11 条脚本子集。

| 门禁 | 读数 |
|---|---|
| `verify-color-wiring` | 162 PASS / 0 FAIL |
| `verify-color-strategy` | 87 PASS / 0 FAIL / 3 SKIP |
| `verify-decision-delivery` | 109 PASS / 0 FAIL |
| `verify-prophoto-faithful` | 42 PASS / 0 FAIL（复跑） |
| `verify-gainmap-isobmff` | 174 PASS / 0 FAIL / 9 SKIP |
| `verify-ui-host` | 520 PASS / 0 FAIL（复跑） |
| `verify-ui-split-union` | 11 PASS / 0 FAIL（并集 520=520） |
| `verify-ui-split-reproducible` | 19 PASS / 0 FAIL |
| `verify-probe-mirror-parity` | 93 PASS / 0 FAIL |
| `verify-color-out-matrix` | 249 PASS / 0 FAIL |
| `verify-module-registry` | 12 PASS / 0 FAIL |
| `verify-release-package` | 16 PASS / 0 FAIL |
| `verify-package-gui-starts` | 6 PASS / 0 FAIL / 1 SKIP（提权托盘 err=5，不计绿） |

`verify-color-out-matrix` 直接锁住本次 AVIF 修复：

- A11：`legacy AVIF` YUV420/10 auto 产出后回读 `yuv420p10le,pc,smpte170m`，且命令行含 `-colorspace smpte170m`，不是仅改标签。
- A12：同路径 `gbrp` identity 回读 `gbrp,pc,gbr`，且命令行没有 `-colorspace`，证明修复没有误伤 identity 分支。

**首轮设备噪声（已保留原日志，不使用失败日志入清单）**：

- `verify-prophoto-faithful` 首轮 41/1：截短 ICC 的对照夹具在 exiftool 写回时未产生预期诊断；同一条单独复跑为 42/0。原日志在 `first-run/verify-prophoto-faithful.out.txt`。
- `verify-ui-host` 首轮退出码 1：常规五臂 aggregate 仍为 520/0，但 `UIHOST_NEGCTL=1` 的 `ui-mode` 反向控制在 121 s 超时，未产出汇总；单独复跑为 520/0（244.6 s）。原日志在 `first-run/verify-ui-host.out.txt`。

首轮 11 条脚本运行器汇总为 9/11 绿；两条单独复跑通过后，最终 `tests/output/release-gates.json` 登记 11 条设计面 + 2 条包级，共 **13 条全绿**，`_release-gate-check` 退出码 0。

## 4. 包级验收

- `verify-release-package`：16 PASS / 0 FAIL；app/full 归档、条目数、必需成员、目录与归档同数均对账通过。
- full 六件工具（ffmpeg/cjxl/exiftool/JxrEncApp/avifenc/dngtool）与 `publish/PLAN` 逐字节同指纹。
- full `verify-package-gui-starts`：存活 12 s，窗口句柄 `0x2B18B8`；6 PASS / 0 FAIL / 1 SKIP。
- app 独立 GUI 冒烟：存活 12 s，窗口句柄 `0x13E176A`，`CloseMainWindow` 后 `exit=0`。
- SKIP ⑥ 仍是提权会话 `Shell_NotifyIcon err=5`，按既定口径不计绿；普通权限会话仍需复核 PASS 形态。

## 5. 边界与清理

- 未运行 104 条全量基线；未运行 `probe:bench` 性能预检，因此本报告不宣称性能稳定。
- QSV/AMF/VAAPI 无实机编码证据；少数 engine JXR 跨色域组合与 BT.2390 仍为显式拒绝。
- `publish/` 只保留本次 RC2 两档；2026-11-24 旧两档已被覆盖。旧报告仍作为时间点记录保留，但其中旧包哈希不得再当作现行产物凭据。

## 6. 证据入口

- 报告与证据根：`docs/review-2026-11-25/`
- 原始运行目录：`tests/output/_tmp/release-rc2-reissue-20261125-r1/`
- 构建日志：`docs/review-2026-11-25/rc2-reissue-evidence/logs/build.log`
- 打包日志：`docs/review-2026-11-25/rc2-reissue-evidence/logs/pack-all.log`
- 首轮 11 条脚本汇总：`docs/review-2026-11-25/rc2-reissue-evidence/logs/runner-release-gates-firstrun.log`
- prophoto 复跑：`docs/review-2026-11-25/rc2-reissue-evidence/logs/retry-prophoto.log`
- ui-host 复跑：`docs/review-2026-11-25/rc2-reissue-evidence/logs/retry-ui-host.log`
- 最终门禁清单：`docs/review-2026-11-25/rc2-reissue-evidence/logs/release-gates.json`
- 最终门禁复核：`docs/review-2026-11-25/rc2-reissue-evidence/logs/release-gate-check.log`
- 包指纹表：`docs/review-2026-11-25/rc2-reissue-evidence/logs/fingerprints.csv`
- 独立验包：`docs/review-2026-11-25/rc2-reissue-evidence/logs/verify-release-package.out.txt`
- full GUI：`docs/review-2026-11-25/rc2-reissue-evidence/logs/verify-package-gui-starts.out.txt`
- app GUI：`docs/review-2026-11-25/rc2-reissue-evidence/logs/app-gui-smoke.log`
