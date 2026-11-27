# RC3 重出报告（2026-11-27）

## 结论

`1.6.0-rc3` 已在 `9cc5a83` 重出，app/full 两档归档均通过 `7z t`、独立验包 **16/0**、包内 GUI **6/0/1**（⑥ 提权会话托盘 `err=5` 按既定口径 SKIP，不计绿）；app 包另做独立起窗冒烟，存活 12 s、窗口句柄 `0xE1482`。源码门禁 11 条在 pack 前候选 `5650FF1173F30085` 上全绿，包级两条门禁在包内 exe 上全绿。11-25 首包读数保留为时间点记录，下载身份以本报告的两档哈希为准。

## 源码提交

| 提交 | 主题 | 内容 |
|---|---|---|
| `1dae2ca` | `fix(gainmap): correct Apple XMP headroom and rebuild private LHDR masks` | Apple v2 `HDRGainMapHeadroom` 改线性倍率；Apple aux HEIC 删除“128 中性”分支；OnePlus/OPPO `local.hdr.linear.mask` 走 `VendorHdrLhdrCurve` 重建 ISO 21496-1 增益图；重建仅 `gmFromVendorMask` 启用；exiftool 126 重试 |
| `3fdffe7` | `fix(ui): restore queue error and warning colors` | `QueueItem.StatusColorKey` + `Status` setter/`MarkWarning`/`ClearWarnings` 显式通知；`ErrorToColorConverter` 接受字符串键；`MainWindow.xaml` 6 处绑定改为 `StatusColorKey` |
| `9cc5a83` | `test(color): add real cross-format matrix coverage` | 活 GUI 静态格式/编码器状态 × auto/engine/legacy × `bt709`/`bt2020nc`/`smpte170m` 的真实 `QueueProcessor` 路由与独立像素判定；JXR 后端识别修复；106 条 runner 登记、UI 520 锁、HEIC 夹具 |

## 出货包

| 档位 | 文件 | 字节 | SHA-256 | 包内文件 | 包内 exe sha16 |
|---|---|---:|---|---:|---|
| app | `publish/FFmpegPictureUI-v1.6.0-rc3-x64.7z` | 12,112,727 | `B165AA058432B844B78CDA8415234DDAB1DBBC74D6AD35E12517C16EDFD36A58` | 19 | `2E136823C9B3EF28` |
| full | `publish/FFmpegPictureUI-v1.6.0-rc3-x64-full.7z` | 56,508,950 | `E030F9C243191F45EDB3F991ACB3A5ED3C9FFB4AC30341BDE16A300C44729FD9` | 584 | `7D07E0F62CFDC46F` |

两档 `ProductVersion=1.6.0-rc3+9cc5a83c8244d5fc41abec596090791ea2e55c20`。`7z t` 均 exit 0；full 包 128 个文件夹 / 584 个文件、解压 314,167,865 B。`publish/` 当前只保留本版两档，以及 `build/`、`PLAN/`。

## 门禁读数

### 源码门禁（pack 前候选 `FfmpegGui.dll` `5650FF1173F30085`）

| 门禁 | 读数 |
|---|---|
| `verify-gainmap-engine` | **225 PASS / 0 FAIL** |
| `verify-gainmap-isobmff` | **185 PASS / 0 FAIL / 0 SKIP** |
| `verify-ui-host` | **520 PASS / 0 FAIL** |
| `verify-ui-split-union` | **11/0**（并集 520=520） |
| `verify-ui-split-reproducible` | **19/0** |
| `verify-probe-mirror-parity` | **93/0** |
| `verify-module-registry` | **12/0** |
| `verify-gui-color-combinations` | **54/0**（36 真实转换 + 18 独立像素判定） |
| `verify-cross-format-matrix` | **270/0**（135 真实转换 + 135 独立矩阵判定；`MinCorrectPsnr=23.137904`） |
| `verify-color-wiring` | **162/0** |
| `verify-decision-delivery` | **109/0** |

首轮两处红均未放宽断言：

- `verify-gainmap-engine` 6 条失败全部是 exiftool Perl 启动器瞬断 `Failed to load Perl DLL "perl532.dll" code 126`；手工对同一产物可复现“第二次 126、第三次正常”。`ExifTag` / `ExtractIcc` / `Invoke-ExifWrite` 仅在 stderr 命中 `Failed to load Perl DLL|code 126` 时重试 3 次（间隔 250 ms），ICC 内容、XMP 九项、MapSecondary、⑨ SDR 路径判据原样保留。最终 225/0。
- `verify-ui-host` 的 `J6 arib-std-b67` 一次失败，属已知异步色彩下拉复位漂移；单独复跑 `Tests.UiSweep.exe` 得 **169 PASS / 0 FAIL**，整链复跑 520/0。

### 包级门禁

| 门禁 | 读数 | 绑定 |
|---|---|---|
| `verify-release-package` | **16 PASS / 0 FAIL** | 两档归档；app 19=19、full 584=584；六件工具指纹一致；包内版本串 `1.6.0-rc3+9cc5a83…` |
| `verify-package-gui-starts` | **6 PASS / 0 FAIL / 1 SKIP** | full 包 exe `7D07E0F62CFDC46F`；⑥ 提权 `err=5`，未判定 |
| app 包 GUI 冒烟 | 存活 12 s；句柄 `0xE1482` | app 包 exe `2E136823C9B3EF28` |

`tests/output/release-gates.json` 已追加 `verify-release-package` 与 `verify-package-gui-starts`，`_release-gate-check.ps1` 在 pack 起始时对 11 条源码门禁读出 `5650ff1173f30085` 并放行；pack 后复跑 13 条清单读出源码项为 `f518136978a63ebe`、包项为 `e030f9c243191f45` / `7d07e0f62cfdc46f`，全绿（13 条）。

**指纹口径说明**：`pack.ps1 -Mode all` 的 NativeAOT `dotnet publish` 会重写 `src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.dll`（pack 后实测 `F518136978A63EBE`）。因此源码门禁读数绑定 pack 前 `5650FF1173F30085`，包级读数绑定包内 exe；两者都指向同一源码提交 `9cc5a83`，不把 pack 后的 managed DLL 冒充为源码门禁被测件。

## 根因与修复要点

### Apple / 私有 HEIC GainMap

- Apple v2 `HDRGainMapHeadroom` 是线性倍率，不是 log2；旧解析把 4.53 当头room本值会算错增益。
- Apple aux HEIC 旧代码在无依据的“128 中性”分支上分叉；现统一 0 基 Rec.709 公式。
- OnePlus/OPPO 文件尾 `local.hdr.linear.mask` 不是标准 ISO 增益图。现按 `float[0]` EDR version、`float[32]` raw gain、`float[33]` EDR scale 与 LUT 链重建为标准 ISO 21496-1 增益图；仅 `gmFromVendorMask` 时启用，避免把 Apple aux 或标准 tmap 的增益图再叠一层曲线。

### 队列红/黄着色

- `42a0880` 把 `Foreground="{Binding HasError, Converter=…}"` 改成无路径 `{Binding Converter=…}`。Avalonia 只在 DataContext 换对象时求值，`Status` 变更和 `MarkWarning()` 不触发重算。
- 新增 `QueueItem.StatusColorKey`（`error`/`warning`/`normal`），在 `Status` setter、`MarkWarning`、`ClearWarnings` 显式通知；转换器接受字符串键；XAML 6 处绑定改为 `StatusColorKey`。真实 GUI 已复验失败红字与 `[warning]` 黄字。

### 跨格式矩阵覆盖

- 新增 `Tests.Shared/GuiCombinationCoverage` 的活 GUI 状态枚举与 `QueueProcessor` 路由，覆盖 15 个格式/编码器状态 × 3 条策略 × 3 个目标矩阵 = 135 条真实转换；另有 135 条独立 best-matrix 判定，`MinCorrectPsnr=23.137904`。
- JXR 后端识别修复后，JXR 组合不再因后端名解析失败被跳过；`verify-gui-color-combinations` 另跑 36 条真实转换 + 18 条像素判定。

## 残余风险

- `verify-package-gui-starts` ⑥ 的 PASS 形态仍未在普通权限会话本地验证；本机直接跑与 `schtasks` 都是 High，故本轮按既定口径 SKIP。
- 未运行 104 条全量基线；本轮只跑 RC3 变更链路 11 条源码门禁 + 2 条包级门禁。
- `probe:bench` 未纳入发布清单，不声明性能稳定。
- AVIF `auto`/无显式目标的 `smpte170m` 兜底仍为已知策略边界（A11 锁定）；未实测硬件编码器与部分 JXR/BT.2390 组合仍为显式拒绝。
- 窗口句柄与 app 冒烟是单次成功读数，不等于所有桌面会话/DPI/多屏环境均已覆盖。

## 证据

- 门禁与 runner 日志：`docs/review-2026-11-27/rc3-repack-evidence/logs/`
- `7z t`：`7z-test-.log`（app）、`7z-test-full.log`（full）
- 独立验包：`verify-release-package.out.txt`
- 包内 GUI：`verify-package-gui-starts.out.txt`
- app 冒烟：`docs/review-2026-11-27/rc3-repack-evidence/logs/app-gui-smoke.txt`
- 打包日志：`docs/review-2026-11-27/rc3-repack-evidence/logs/pack-all.log`
- 出包闸清单：`docs/review-2026-11-27/rc3-repack-evidence/logs/release-gates.json`
- pack 后 13 条闸复查：`docs/review-2026-11-27/rc3-repack-evidence/logs/release-gate-check-after-pack.out.txt`

## 发布

- 仓库：`https://github.com/luoye-cpu/ffmpegPictureUI`
- tag：`1.6.0RC3`
- Release：`https://github.com/luoye-cpu/ffmpegPictureUI/releases/tag/1.6.0RC3`
- 资产：app 与 full 两档 `.7z`，名称与上表一致。
