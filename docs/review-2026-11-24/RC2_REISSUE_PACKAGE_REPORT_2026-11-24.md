# RC2 重出包报告（2026-11-24）

## 1. 结论

按“仍为 RC2、只重出包”的要求，版本位保持 `1.6.0-rc2`，RC3 误产物、目录与证据已清除。本轮 RC2 重出包已重新构建、重新跑门禁并重新验包，替代同日首轮 RC2 的两档归档。

唯一产品改动是执行日志从虚拟化 `ListBox` 恢复为可见只读多行 `TextBox`：`Ctrl+A` / `Ctrl+C` 可直接全选复制，上限 2,000,000 字符；仍保留 `UiLogService` 单点入队、每批最多 250 行刷新，写回 `Text` 时保存/恢复用户选区。

## 2. 版本与产物

- 版本源：`src/FfmpegGui/FfmpegGui.csproj` = `1.6.0-rc2`。
- 候选 `FfmpegGui.dll`：`2B90DE92DA2F937B`（2,215,936 B），ProductVersion `1.6.0-rc2+b085facabeb4ceece785dc39d49447e2d6ba57a1`。
- ⚠ `b085fac` 只是 Git HEAD，不是未提交工作树内容的哈希；未提交、未打 tag、未推送。

| 档位 | 文件 | 字节 | SHA-256 |
|---|---|---:|---|
| 单文件 app | `publish/FFmpegPictureUI-v1.6.0-rc2-x64.7z` | 12,113,010 | `0C81AD770BE5FD46618CB4F04BC8774967EBCC30EF58ABD36D7228124F64A615` |
| 完整 full | `publish/FFmpegPictureUI-v1.6.0-rc2-x64-full.7z` | 56,504,303 | `1292DCB41FE571B846EFDE0685CAAA9DD0E9C25C0A050647B4339C41DF846216` |

包内 exe：

- app：`2BED21EA87154EBF`（25,099,264 B）
- full：`FCE289566F6A3F79`（25,099,264 B）

## 3. 回归与门禁

- 单宿主 UI 全组：**520 PASS / 0 FAIL**；`[PASS] P0 面板控件 LogText 可见且为可选只读多行文本（本组前提）`。
- `verify-ui-host`：520/0；`verify-ui-split-union`：11/0（并集 520=520）；`verify-ui-split-reproducible`：19/0。
- 其余 release-gate：color-wiring 162/0、color-strategy 87/0/SKIP 3、decision-delivery 109/0、prophoto-faithful 42/0、gainmap-isobmff 174/0/SKIP 9、probe-mirror-parity 93/0、module-registry 12/0。
- `tests/output/release-gates.json` 已重绑本次候选：10 条设计面门禁指向 `FfmpegGui.dll` `2B90DE92DA2F937B`，包 GUI 条指向 full 包内 exe `FCE289566F6A3F79`；`_release-gate-check` **11 条全绿**。
- 受管运行器公共预检 `probe:bench` 在本机负载下仍有 4 条性能断言红（37.3/40、3.0/3、22.2/25、3.72×/2.0×）。这是既有负载敏感项，不属于本清单 10 条脚本；10 条脚本本身均 `exit=0`，未掩盖该读数。
- 首轮 RC2 曾跑过的 `verify-ui-param-matrix` 126/0、`verify-color-matrix-full` 29/0、`verify-color-out-matrix` 241/0 未在本次重出候选上重跑；首轮记录保留在 `RC2_PACKAGE_REPORT_2026-11-24.md`。

## 4. 归档与包内 GUI

- `7z t`：app 19 文件、full 584 文件，均 `Everything is Ok`。
- `verify-release-package`：**16 PASS / 0 FAIL**；full 的 ffmpeg/cjxl/exiftool/JxrEncApp/avifenc/dngtool 六件工具与 `publish/PLAN` 逐字节同指纹。
- full `verify-package-gui-starts`：**6 PASS / 0 FAIL / 1 SKIP**；存活 12 s，窗口句柄 `0x2451934`。唯一 SKIP 是提权会话 `Shell_NotifyIcon err=5`，不计绿，普通权限会话仍须复核。
- app 独立 GUI 冒烟：存活 12 s，窗口句柄 `0x237158E`，`CloseMainWindow` 后退出 `0x0`。

## 5. 边界与清理

- 未运行 104 条全量基线。
- 无人工桌面截图/鼠标点验；证据是真实起窗、存活、窗口句柄与关窗退出。
- QSV/AMF/VAAPI 无实机编码证据；少数 engine JXR 跨色域组合与 BT.2390 仍显式拒绝。
- RC3 的归档、解包目录、报告和 rc3-evidence 已全部删除；`publish` 和 `publish/build` 只保留本次 RC2 两档。
- 同日首轮 RC2 报告与 `rc2-evidence` 保留为前一轮读数，但其中旧包哈希已被本轮重出包替换，不得再当作现行产物凭据。

## 6. 证据入口

- 原始证据根：`tests/output/_tmp/release-rc2-reissue-20261124-140359-24da1a8d/`
- 固化日志：`docs/review-2026-11-24/rc2-reissue-evidence/logs/`
- 指纹表：`docs/review-2026-11-24/rc2-reissue-evidence/logs/fingerprints.csv`
- 构建日志：`docs/review-2026-11-24/rc2-reissue-evidence/logs/build.log`
- 打包日志：`docs/review-2026-11-24/rc2-reissue-evidence/logs/pack-all.log`
- UI P0 / 全组：`docs/review-2026-11-24/rc2-reissue-evidence/logs/mono-ui-host.log`
- 独立验包：`docs/review-2026-11-24/rc2-reissue-evidence/logs/verify-release-package.out.txt`
- full GUI：`docs/review-2026-11-24/rc2-reissue-evidence/logs/verify-package-gui-starts.out.txt`
- app GUI：`docs/review-2026-11-24/rc2-reissue-evidence/logs/verify-package-gui-starts-app.out.txt`
- release-gate 复核：`docs/review-2026-11-24/rc2-reissue-evidence/logs/release-gate-check.log`
