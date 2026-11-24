# FFmpegPictureUI v1.6.0-rc2 出包结果（2026-11-24）

## 1. 结论

RC2 的 app 与 full 两档包已生成、归档完整性校验通过，包内 GUI 能真实起窗。RC1 旧包已按“只留最新一档”清除。当前产物可直接用于 RC2 验收；未提交、未打 tag。

## 2. 产物

- `publish/FFmpegPictureUI-v1.6.0-rc2-x64.7z` — 12,120,505 B，SHA-256 `70584E1513F6E8D59178A50E7EB00A97E2368E0AFE9AC33C107B3C346C26F53A`
- `publish/FFmpegPictureUI-v1.6.0-rc2-x64-full.7z` — 56,514,331 B，SHA-256 `599B96C30DB6427B2CED1D073B4EC5A95A184B7F245342528C1632CE3CA5EAA1`

包内可执行文件：

- app：`FfmpegGui.exe` SHA-256 前 16 `704D1B39445B8558`，25,100,800 B
- full：`FfmpegGui.exe` SHA-256 前 16 `4FAB428D7C977BEB`，25,100,800 B
- 两者 ProductVersion：`1.6.0-rc2+b085facabeb4ceece785dc39d49447e2d6ba57a1`

⚠ `b085fac` 是出包时的 Git HEAD，不是未提交修复内容的哈希。当前工作树包含 70+ 个未提交变更；本包是“RC2 版本位 + 当前工作树”的快照，不能把该短哈希当作源码内容的完整追溯凭据。未提交、未打 tag、未推送。

## 3. 构建

- 全解决方案 Release 构建：**0 警告 / 0 错误**。
- RC2 候选 `FfmpegGui.dll` SHA-256 前 16：`AA84239F5F077B4A`。
- RC2 候选 `ServiceProbe.dll` SHA-256 前 16：`508952A5ADA0B28D`；`ServiceProbe.exe`：`64793E92B76FA0B3`。
- 产品代码在进入打包链路后未再修改；`verify-gainmap-isobmff` 的改动仅为测试取证工具的有界重试，不改变产品断言。

## 4. 出包门禁

runner 使用 10 条定向脚本 + matrix 探针，均为受管批次（不是 104 条全量）：

- `verify-color-wiring` — 162 PASS / 0 FAIL
- `verify-color-strategy` — 87 PASS / 0 FAIL / 3 SKIP（legacy 16-bit 合理拒绝，无产物可比较，不计绿）
- `verify-decision-delivery` — 109 PASS / 0 FAIL / 0 SKIP
- `verify-prophoto-faithful` — 42 PASS / 0 FAIL
- `verify-gainmap-isobmff` — 174 PASS / 0 FAIL / 9 SKIP
- `verify-ui-host` — 520 PASS / 0 FAIL（五件套 520）
- `verify-ui-split-union` — 11 PASS / 0 FAIL，并集 520/520
- `verify-ui-split-reproducible` — 19 PASS / 0 FAIL
- `verify-probe-mirror-parity` — 93 PASS / 0 FAIL
- `verify-module-registry` — 12 PASS / 0 FAIL
- release-gate 红态闸：11 条全绿，允许出包

### gainmap 门禁的唯一红与处置

首次受管批次 173 PASS / 1 FAIL：§3f-1 通过 ExifTool 读取 AVIF nclx 为空 `//`。根因不是缺标签：

- `nx_pq.err` 明确为 `Failed to load Perl DLL "perl532.dll" code 126`
- 产品同一文件用 ffprobe 得 `bt2020/smpte2084`，重复 ExifTool 读取得 `9/16/0`
- 本机 60 次串行 ExifTool 调用命中 1 次 code 126，属打包版 13.61 启动器/杀软扫描抖动
- 产品 `ExifToolService` 早已对 code 126 做有界重试；门禁原先没有

处置仅限测试取证层：`verify-gainmap-isobmff.ps1` 的 ExifTool 读取增加最多 3 次、只对 `Failed to load Perl DLL`/`code 126` 的重试；最终 `9/16/0`、`bt2020/smpte2084` 等断言不变，三次仍失败照旧红。重跑为 174/0。

## 5. 归档与包内 GUI 验收

- `7z t` app：19 文件，Everything is Ok
- `7z t` full：584 文件，Everything is Ok
- `verify-release-package`：**16 PASS / 0 FAIL**
  - app 归档 19/19，full 归档 584/584
  - full 六件工具（ffmpeg/cjxl/exiftool/JxrEncApp/avifenc/dngtool）与 `publish/PLAN` 逐字节同指纹
- `verify-package-gui-starts`（PKG_VER=1.6.0-rc2）：**6 PASS / 0 FAIL / 1 SKIP**
  - 包内 full `FfmpegGui.exe` 不带 `--headless` 存活 12 s
  - 主窗口句柄 `0x370C18` 非 0
  - 托盘点亮为 **SKIP**：当前为提权 High 会话，`Shell_NotifyIcon` 返回 `err=5`；不得把此 SKIP 计作 PASS，普通权限会话仍需复验
  - 负控 `--headless -i 不存在` 快速退出，证明存活尺有效
- app 包独立 GUI 冒烟：另一份 AOT `FfmpegGui.exe`（`704D1B39445B8558`）存活 12 s，主窗口句柄 `0xC41782`，关窗后以 `0x00000000` 退出。该 app 冒烟为本轮独立取证，不等于 managed 门禁的 full 结果。

## 6. 明确边界

- 未运行 104 条全量基线；未提交、未打 tag、未推送。
- 没有人工桌面截图/鼠标点验；已有的是包内 GUI 自动启动、存活与窗口句柄证据。
- QSV/AMF/VAAPI 无实机编码证据。
- 少数 engine JXR 跨色域组合与 BT.2390 仍为显式拒绝。
- `verify-package-gui-starts` ⑥ 的普通权限 PASS 形态仍未本地验证。

## 7. 证据

- 证据根：`tests/output/_tmp/release-rc2-20261124-114030-37fdce04/`
  - `build.log`、`pack-all.log`、`verify-release-package.log`、`verify-package-gui-starts.log`、`archive-integrity.log`
  - `runner-release-gates.log`、`runner-regate-gainmap.log`
- 受管门禁拷贝：`docs/review-2026-11-24/rc2-evidence/logs/`
- release gate 清单：`tests/output/release-gates.json`（11 条）
