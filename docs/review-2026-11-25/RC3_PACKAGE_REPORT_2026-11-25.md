# RC3 出货报告（2026-11-25）

## 1. 结论

- **其他色彩空间没有该问题。** 12 个具名目标空间在 engine / legacy / `--color-strategy manual` 三条路径上的输出矩阵与 primaries/transfer 均按目标派生；不存在“只有 AVIF 保留 601、其他空间也各自留一张矩阵表”的第二份真值。
- 审查中发现的两处**残留**已修复：高级 CP/CT legacy 出口继承输入 `gbr`，以及 `--color-matrix` 只声明输入时吞掉目标 P/T。修复提交 `0a1457e`、`2ed805b`；产品源码未再改动。
- RC3 已出包并验收：两档归档 `7z t` 通过，包级 `verify-release-package` **16/0**，full 包 GUI **6/0/1 SKIP**，app 包 GUI 独立冒烟存活 12 s / 句柄 `0x390D5A` / 关窗 `exit=0`。
- 13 条发布清单全绿（`_release-gate-check.ps1` rc=0）。104 条全量基线未跑；`probe:bench` 未纳入发布清单，一次默认探针轮 H1 为 `2.01×`（阈值 `2.0`），按已知负载敏感项处理，不据此声明性能稳定。

## 2. 全空间审查结果

审查口径是“显式目标空间 ⇒ 实际输出矩阵 + CICP P/T + 像素路径”，不是只看命令行标签。engine / legacy / manual 三路同跑，AVIF 输出读回 nclx 与像素格式。

| 目标空间 | 输出矩阵 | 说明 |
|---|---|---|
| sRGB | `bt709` | P/T 为 `bt709` + `iec61966-2-1`；不是 601 |
| BT.709 | `bt709` | P/T `bt709` + `bt709` |
| Display P3 | `bt709` | P3 原色 + sRGB TRC，矩阵按 709 亮度系数；P/T 保留 `smpte432` + `iec61966-2-1` |
| DCI-P3 | `bt2020nc` | P3 影院原色；矩阵随目标原色走 2020 非恒定亮度 |
| P3 PQ | `bt2020nc` | P3 原色 + PQ |
| P3 HLG | `bt2020nc` | P3 原色 + HLG |
| BT.2020 | `bt2020nc` | SDR 传递 |
| BT.2020 PQ | `bt2020nc` | `smpte2084` |
| BT.2020 HLG | `bt2020nc` | `arib-std-b67` |
| Adobe RGB | `bt2020nc` | 宽色域目标，不再落编码器默认 601 |
| ProPhoto RGB | `bt2020nc` | 同上；`verify-prophoto-faithful` 42/0 另锁保真 |
| ColorMatch RGB | `bt2020nc` | 同上 |
| 601 族（BT.601 / SMPTE 170M） | `smpte170m` | **唯一**允许 601 的族；不是遗漏 |

单一真值仍是 `ColorSpaceRegistry.OutputMatrixTokenForPrimaries()`：engine 与 legacy 的矩阵都从目标原色派生，`FfmpegCommandBuilder.Decision` 只在空值处兜底，没有第二张按空间硬编码的矩阵表。门禁 `verify-color-out-matrix` 的 A16（高级 CP/CT × 5 空间 × engine/legacy，逐臂断言 exit/M/P/T）与 A17（`--color-matrix bt2020nc --color-space sRGB` × engine/legacy，完整三元组）锁定该结论，最终 **327/0**。

> 601 只出现在 `auto`/无显式目标的 AVIF YUV 出口（A11 锁定），这是既有策略边界；一旦给了显式目标，矩阵与 P/T 都按目标空间走。

## 3. 两处残留与修复

### 3.1 高级 CP/CT legacy 出口继承输入 `gbr`

`--color-primaries/--color-trc` 的 legacy 路径不经目标 zscale，`outMatrix` 从 PNG 探测值 `gbr` 起算；`Decision` 最终只在空值派生 ⇒ 非空 `gbr` 被保留，`BuildArguments` 又因 RGB 族跳过 `-colorspace`，AVIF 真实 YUV 面落 nclx `M=2`（601）。

修复（`FfmpegCommandBuilder.cs`，`2ed805b`）：

- AVIF YUV 检测从 `fmt0=="avif" && srcIsRgb` 放宽为 `fmt0=="avif"`，扫描命令里的 `-pix_fmt yuv*`；
- 当 `outMatrixToken` 为空/`rgb`/`gbr` 且 `dec.OutPrimaries` 非空时，按目标原色派生矩阵；
- `gbrp` identity 出口仍不写 YUV 矩阵，不误伤直通。

修复后 engine/legacy 高级 sRGB、P3、BT.2020 SDR/PQ、601 均回读 `yuv420p10le,pc,正确 M`，P/T 同步正确。

### 3.2 `--color-matrix` 只声明输入时吞掉目标标签

`--color-matrix bt2020nc --color-space sRGB` 在 engine/legacy 均为 `matrix=bt709`，但 P/T 为空（旧行为全 `unknown`）——矩阵裁决对了，标签没落地。

修复（`FfmpegCommandBuilder.cs`，`2ed805b`）：

- 无标签源 + 显式目标 + `dec.PixelsUnchanged` + `dec.DeclPrimaries/DeclTrc` 为空时，在 AVIF YUV 输出加 `setparams=color_primaries=…:color_trc=…:colorspace=…`；
- 像素不动，仅把已裁决的 P/T/M 落到帧标签；读回 `yuv420p10le,pc,bt709` + `bt709/iec61966-2-1`。

该修复窄化在 AVIF YUV 面；auto/无显式目标 legacy AVIF 仍 `smpte170m`，A11 继续锁边界。

## 4. 版本与产物

- 版本位：`src/FfmpegGui/FfmpegGui.csproj` = `1.6.0-rc3`。
- 功能源提交：`2ed805b`（含 `0a1457e` 的矩阵统一与 AVIF legacy 兜底）。
- 构建源提交：`5b92323`（G2 测试夹具校正；产品源码未动）。
- 发布元数据提交：`a92c88a`（版本位 + 中英 CHANGELOG）。
- 候选 `FfmpegGui.dll`：`4023A589109AA2DC`（2,216,960 B），`ProductVersion=1.6.0-rc3+5b92323…`。
- 包内 `FfmpegGui.exe` 的 `ProductVersion` 同为 `1.6.0-rc3+5b92323…`；发布元数据提交晚于构建，故版本串记录的是构建源而非 `a92c88a`。

| 档位 | 文件 | 字节 | SHA-256 | 包内 exe sha16 |
|---|---|---:|---|---|
| 单文件 app | `publish/FFmpegPictureUI-v1.6.0-rc3-x64.7z` | 12,117,553 | `CF8733E817E3CE25C1D8551684B9DC1222F650DA672FE9FD65E09A96B3F5F32B` | `B696B25101BF8A01` |
| 完整 full | `publish/FFmpegPictureUI-v1.6.0-rc3-x64-full.7z` | 56,508,167 | `62ED2D81D9B3D39F7CD6B3C0E7D1CC505D2E52A8EF3AE182DB204E32A9D543D1` | `4FC60ADF01F824E8` |

两档 `7z t` 均 `rc=0`：app 19 文件 / full 584 文件。完整版六件工具与 `publish/PLAN` 逐字节同指纹（ffmpeg/cjxl/exiftool/JxrEncApp/avifenc/dngtool）。

## 5. 门禁读数

最终候选 `4023A589109AA2DC`，9 条设计面在完整构建后重跑；UI 两条在同一候选上复跑；包级两条在归档上跑。

| 门禁 | 读数 |
|---|---|
| `verify-color-wiring` | 162 PASS / 0 FAIL |
| `verify-color-strategy` | 87 PASS / 0 FAIL / 3 SKIP |
| `verify-decision-delivery` | 109 PASS / 0 FAIL |
| `verify-prophoto-faithful` | 42 PASS / 0 FAIL |
| `verify-gainmap-isobmff` | 174 PASS / 0 FAIL / 9 SKIP |
| `verify-ui-host` | **520 PASS / 0 FAIL** |
| `verify-ui-split-union` | 11 PASS / 0 FAIL（并集 520=520） |
| `verify-ui-split-reproducible` | 19 PASS / 0 FAIL |
| `verify-probe-mirror-parity` | 93 PASS / 0 FAIL |
| `verify-color-out-matrix` | 327 PASS / 0 FAIL |
| `verify-module-registry` | 12 PASS / 0 FAIL |
| `verify-release-package` | 16 PASS / 0 FAIL |
| `verify-package-gui-starts` | 6 PASS / 0 FAIL / 1 SKIP |

`release-gates.json` 13 条，`_release-gate-check.ps1` 输出“全绿（13 条），允许出包”。

### 5.1 G2 测试夹具校正（产品未改）

首轮 UI 门禁的两条红都在 G2 停止用例，不是色彩改动触碰 PNG 路径：

- 旧 G2 的输入与输出都在 `TempDir`；产物名恒为“输入基名 + 目标扩展名” ⇒ 6000×4000 `g2_in.png` 的 PNG→PNG 输出路径就是输入本身。ffmpeg 报 `Output ... same as Input #0 - exiting`，退出码 `-22`；worker 在 150 ms 轮询前已死，表现为 `workers=[]` / `status=失败 (退出码 -22)`。
- 修正：输出改到独立 `g2_out/` 子目录，并按实际产物名 `g2_out/g2_in.png` 断言；慢任务从 24MP 提到 48MP，`IsProcessing` 等待与 worker 扫描合到同一 40 s 窗口，避免 Stop 撞完成边界。
- 裸跑 `ui-mode` **66/0**；`verify-ui-host` **520/0**；`verify-ui-split-union` **11/0**（520=520）。该修正是测试夹具校正（`5b92323`），产品行为没有为门禁让步。

## 6. 包级验收

- `verify-release-package`：16/0；两档归档、条目数、必需成员、目录/归档同数、六件工具指纹、包内版本串全部通过。
- full `verify-package-gui-starts`：存活 12 s，窗口句柄 `0x2B0AEC`，6/0/1；⑥ 是提权会话托盘 `err=5` 的既定 SKIP，不计绿。
- app 包独立冒烟：存活 12 s，句柄 `0x390D5A`，`CloseMainWindow` 后 `exit=0`。
- SKIP ⑥ 仍未在普通权限会话验证 PASS 形态；这是已知未了结，不影响本次其它判据。

## 7. 边界

- `auto`/无显式目标的 AVIF YUV 保留 `smpte170m`（A11）；本轮不改该策略边界。
- 未运行 104 条全量基线；未宣称全仓所有链路。
- `probe:bench` 未纳入 13 条发布清单。一次默认探针轮 H1 端到端 `2.01×` 略超 `2.0` 阈值；该项已知负载敏感、无成对采样保护，不作为 RC3 性能结论。
- QSV/AMF/VAAPI 无实机编码证据；engine JXR 少数跨色域组合与 BT.2390 仍是显式拒绝。
- 未创建/推送 rc tag；现有 tag 最新仍为 `1.6.0BETA17`。如需 tag，按既有风格 `1.6.0RC3`，指向发布元数据提交。

## 8. 证据入口

- 报告与证据根：`docs/review-2026-11-25/rc3-evidence/`
- 最终门禁清单：`docs/review-2026-11-25/rc3-evidence/logs/release-gates.json`
- 最终闸复核：`docs/review-2026-11-25/rc3-evidence/logs/release-gate-check-final.log`
- 9 条设计面最终轮：`docs/review-2026-11-25/rc3-evidence/logs/runner-design-final.log`
- UI host 复跑：`docs/review-2026-11-25/rc3-evidence/logs/retry-ui-host.log`
- UI split union 复跑：`docs/review-2026-11-25/rc3-evidence/logs/retry-ui-split-union.log`
- G2 夹具裸跑：`docs/review-2026-11-25/rc3-evidence/logs/ui-mode-g2-retry.log`
- 构建日志：`docs/review-2026-11-25/rc3-evidence/logs/build.log`
- 打包日志：`docs/review-2026-11-25/rc3-evidence/logs/pack-all.log`
- 7z 校验：`docs/review-2026-11-25/rc3-evidence/logs/7z-test-app.log`、`7z-test-full.log`
- 独立验包：`docs/review-2026-11-25/rc3-evidence/logs/verify-release-package.out.txt`
- full GUI：`docs/review-2026-11-25/rc3-evidence/logs/verify-package-gui-starts.out.txt`
- app GUI：`docs/review-2026-11-25/rc3-evidence/logs/app-gui-smoke.out.log`
- 指纹表：`docs/review-2026-11-25/rc3-evidence/logs/fingerprints.csv`
