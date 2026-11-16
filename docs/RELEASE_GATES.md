# RELEASE_GATES — 出包前红态闸（MA-01 / GATE-02）

> 状态：**机制就位，pack.ps1 尚未接线**。由后续打包批次（批次4）在 `pack.ps1` 出包前调用本闸。
> 本文件只定义"出包前必须跑哪些门禁、红了会怎样、清单长什么样"。

## 1. 规则（一句话）

**红态禁止发布。** 出包前，按"本批改了哪条链路"跑完对应门禁，把每条的 `(name, rc, log, artifact)`
写进 `tests/output/release-gates.json`，再由 `tests/scripts/_release-gate-check.ps1` 做 fail-closed 对账：
**任一 `rc != 0`（或清单缺失/非法）⇒ 脚本 `exit 1`，禁止出包。**

- 本闸**不重跑**门禁（重跑是 `tests/scripts/_run-step3-gates.ps1` 的事），只对账清单里登记的退出码。
- `rc` 字段缺失、不是整数、或 ≠0，一律按红处理（fail-closed，不猜默认值）。
- `artifact` 建议填被测 dll/exe 的相对路径；脚本打印其 `sha256[:16]`，让"这个包是在哪份二进制上验的"可追溯。
- 不许跑全量 104 条（≈2h）：**只跑本批改动链路**对应的门禁（见下）。`-FullBaseline` 仅在明确要求时用。

## 2. 按变更链路的必跑门禁

| 改了什么 | 出包前必跑（选你这条，不是全跑） |
|---|---|
| 色彩语义 / CICP / ICC / carry / 命令行决策 | `verify-color-strategy.ps1`、`verify-decision-delivery.ps1` |
| ServiceProbe 派发包 / Check 条数 | `verify-probe-mirror-parity.ps1` |
| PNG 出口 / pngopt / 双写 iCCP+cICP | `verify-png-optimizer.ps1`、`verify-color-wiring.ps1` |
| UI 执行面 / 拆分件 | `verify-ui-host.ps1`、`verify-ui-split-union.ps1`、`verify-package-gui-starts.ps1` |
| GainMap / UltraHDR / ISO-BMFF | `verify-gainmap-engine.ps1`、`verify-gainmap-isobmff.ps1` |

> 完整门禁清单与分组以 `tests/scripts/_run-step3-gates.ps1` 的 `$scriptList` 为唯一真值；
> 本表只列"按链路选哪几条"，不是登记表。加/删门禁仍须同批改 `$scriptList` 与 `$expectedScriptCount`。

## 3. 机器可读清单格式

`tests/output/release-gates.json`（UTF-8，**不要 BOM** 给解析器；人可看）：

```json
{
  "gates": [
    {
      "name": "verify-color-strategy",
      "rc": 0,
      "log": "docs/review-YYYY-MM-DD/review-evidence/logs/color/verify-color-strategy.log",
      "artifact": "src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.dll"
    }
  ]
}
```

- `name`：门禁脚本名（人读 + 点名红行用）。
- `rc`：该门禁进程退出码。**0=绿；非 0=红**。
- `log`：日志路径（相对仓库根），本闸不解析内容，只在红行里点名。
- `artifact`：被测产物相对路径；脚本打印 `sha256[:16]`。

## 4. 用法（打包批次接线时）

```powershell
# 1) 跑本批链路门禁，记下各自退出码与日志路径，写 tests/output/release-gates.json
# 2) 最后一道闸：
pwsh -NoProfile -File tests/scripts/_release-gate-check.ps1
#    exit 0 = 全绿，可继续 pack；exit 1 = 有红，中止出包
```

## 5. 负控（已自证）

`_release-gate-check.ps1` 自带 fail-closed 语义：喂一份含 `rc=1` 的 gates 数组 ⇒ 脚本必须
`exit 1` 并把那行 RED 点名。喂空 `gates`、喂非法 JSON、喂缺 `rc` 字段 ⇒ 也必须 `exit 1`。
这条负控在本批（2026-11-16）已实测，见提交② body。
