# 交接：.NET 11 正式版升级 + 打包任务（停止点 2026-11-11 23:2x）

> 交接对象：接手「.NET 11 GA 升级收尾 + 出包」的会话/人。
> 本文件是**状态与步骤**，不是发布声明；文中所有读数都指明出处，未跑过的步骤写「未做」。
> 依据用户指示：**立即停止操作，打包任务交接**。以下操作已全部停止，无后台进程残留。

---

## 0. 一句话接力棒

**GA SDK 已生效、Release 自包含构建已用 GA 运行时（`Microsoft.NETCore.App 11.0.0`）；还没做 AOT 发布验证、
门禁回归、提交与出包。** 工作区只有一个未提交新文件 `global.json`（其余干净，HEAD=`88844ec`）。

接手者第一件事：按 §4 第 2 步在**scratch 目录**跑一次 NativeAOT 发布验证（**不要**先动 `publish/`）。

---

## 1. 为什么停在这里（时间线，均为本机实测）

| 时间 | 事件 | 读数/出处 |
|---|---|---|
| 11-11 19:18 | 最后一次旧工具链构建（当时 SDK 仍是 RC.3） | `bin/Release/net11.0/win-x64/FfmpegGui.runtimeconfig.json` → `Microsoft.NETCore.App = 11.0.0-rc.3.26522.148`；`obj/project.assets.json` → `Microsoft.NET.ILLink.Tasks/11.0.0-rc.3.26522.148` |
| 11-11 23:07 | 用户安装 .NET 11 **GA** | `C:\Program Files\dotnet\sdk\11.0.100` 创建于 23:07:55，`.version` = `11.0.100-rtm.26529.118`；`shared\Microsoft.NETCore.App\11.0.0` 创建于 23:07:10 |
| 11-11 23:1x | 本次升级操作（已停止，见 §3） | restore/build 日志在 `tests/output/_tmp/net11-ga/` |
| 11-11 23:2x | 用户指示停止并交接 | 本文 |

环境现状（`dotnet --info`）：SDK `11.0.100`（RTM）+ `10.0.403`；运行时 `11.0.0` / `10.0.14` / `9.0.22` / `8.0.33` 均在位。

---

## 2. 当前状态：已完成 / 未完成

### 已完成（可复核）
1. 新增仓库根 [global.json](global.json)（**未提交**，`git status` = `?? global.json`）：
   ```json
   { "sdk": { "version": "11.0.100", "rollForward": "latestFeature", "allowPrerelease": false } }
   ```
   目的：钉 GA SDK，避免以后装回预览/SDK 回退到 10.0.403；`allowPrerelease:false` 保证不再解析预览版。
2. `dotnet restore ffmpegPictureUI.sln --force` → rc=0（`tests/output/_tmp/net11-ga/restore.log`）。
   restore 后 `obj/project.assets.json` 的 ILLink = `Microsoft.NET.ILLink.Tasks/11.0.0`（GA）。
3. `dotnet build ffmpegPictureUI.sln -c Release --no-restore` → **rc=0，0 错误，4 警告**
   （`tests/output/_tmp/net11-ga/build.log`）。4 警告全部同码同文件、属既有告警，需接手者复核是否与本次无关：
   - `Win32TrayIcon.cs(150) IL3002`、`Win32TrayIcon.cs(78) CA1416`（AGENTS §7 已登记为既有两条）
   - `RawColorPipeline.cs(640) CS8604`、`QueueProcessor.cs(1557) CS8602`（nullable 警告，**本批未验证是否既有**）
4. 构建产物确认 GA 运行时：`src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.runtimeconfig.json`
   → `Microsoft.NETCore.App = 11.0.0`（GA，非 rc.3）；该文件 mtime `11/11 23:16:54`。
   其余 12 个测试宿主的 Release 构建同批重建（sln 全量构建），但**未逐一读它们的 runtimeconfig**——接手者复核。

### 未完成（接手者继续）
- [ ] 全宿主 runtimeconfig 逐一核对（`src/FfmpegGui/bin` + `tests/projects/*/bin` 的 `Release/net11.0/win-x64`），
      确认无 `11.0.0-rc*`。⚠ **不要**拿 `bin/Debug/**`、`bin/Release/net11.0/FfmpegGui.runtimeconfig.json`（RID-less）、
      `bin/x64/Debug/**` 当证据——它们是旧构建残留，实测仍为 rc.3 / rc.1 / framework-dependent。
- [ ] **NativeAOT 发布验证**（本批最关键、且历史最脆弱的一步，见 §4.2）。
- [ ] 相关链路门禁回归（建议清单见 §4.3）。
- [ ] 提交 `global.json`（是否与 AOT/门禁修复合批提交，由接手者定；建议单独一刀「build: pin .NET 11 GA SDK」）。
- [ ] 出包（版本号需用户决定；见 §4.4）与 `#93` 解红。
- [ ] 文档/账本：README 可选加「.NET 11 **正式版** 11.0.0+」；AGENTS §7 新条目；`docs/HANDOVER.md:3671`
      的「无 global.json」是**历史时间点记录**，按 §6/§8.6 只许新增更正条目，**不许回改**。

---

## 3. 已做的操作清单（便于审计/回滚）

| 操作 | 目标 | 结果 |
|---|---|---|
| 新建文件 | `global.json`（仓库根，未提交） | 已存在 |
| `dotnet restore --force` | 全解决方案 | rc=0 |
| `dotnet build -c Release --no-restore` | `ffmpegPictureUI.sln` | rc=0，0 错 4 警 |
| 只读检查 | runtimeconfig / assets / SDK 版本 / publish 目录 | 见 §1、§2 |

**没有动过**：`src/`、`tests/`、`pack.ps1`、`publish/`、`docs/`（除本文件）、git 提交、tag。
唯一工作区变化 = 新增未跟踪的 `global.json`（外加构建产生的 gitignored `bin/obj` 刷新）。

---

## 4. 接手步骤（按顺序）

### 4.1 起步自检
```powershell
dotnet --version        # 必须 11.0.100（GA/RTM）
git status --porcelain  # 只有 ?? global.json
git log -1 --oneline    # 88844ec（beta12 后审查修复批账本）
```

### 4.2 NativeAOT 发布验证（**先 scratch，不碰 publish/**）
`pack.ps1` 的 AOT 路径（`pack.ps1:96-107`）有历史硬约束：**必须先 build、再 `publish -p:NoBuild=true`**，
否则丢 Avalonia 资源表 ⇒ 包内 exe 起 GUI 时 `No precompiled XAML found` 自杀（beta9/beta10 两次事故）。
建议先原样复刻到 scratch 目录：

```powershell
$out = "tests/output/_tmp/net11-ga/app-aot"
dotnet build  .\src\FfmpegGui\FfmpegGui.csproj -c Release --nologo
dotnet publish .\src\FfmpegGui\FfmpegGui.csproj -c Release -r win-x64 `
  -p:PublishAot=true -p:NoBuild=true -p:SelfContained=true -p:PublishSingleFile=true `
  -p:IlcOptimizationPreference=Speed -p:InvariantGlobalization=true `
  -o $out
```

验收点（全部要落日志）：
1. `dotnet publish` rc=0；产物 `FfmpegGui.exe` 尺寸 ≈ **23.9 MB 级**（beta12 实测 23,923,712 B；明显偏小 ≈8 MB 是资源表丢失的信号）。
2. exe 字节里 `!AvaloniaResources`、`!XamlIlPopulate` **各命中 ≥1**（用 `Select-String -Encoding Byte` 或 python 扫）。
3. 启动 smoke：`FfmpegGui.exe --version`（或 `--help`）有正常输出；`--headless` 转一张小 PNG 产物非空。
4. GUI smoke（可选但强烈建议）：启动后 `Wait-Process`/取进程 `MainWindowHandle != 0`，再正常退出；
   提权会话下托盘相关判据可能红，属真读数（AGENTS §7 托盘条目）。
5. 记下 AOT 产物 exe `sha256[:16]` 与 `ProductVersion`，写进账本。

⚠ 历史坑：`.NET 11 rc.2` 时代 AOT 曾"自重编丢资源表"，已用 build→publish 规避；**GA 下该约束是否仍必要未验证**，
建议保持原命令不变先跑通，再谈是否简化。

### 4.3 门禁回归（SDK 升级属全局改动，建议跑代表性链路而非只跑一条）
最低集（本批审查批刚全绿，可作对照基线；dll sha16 `782F970073393F9C` 是**旧工具链**读数，GA 下应变化）：
- `tests/scripts/verify-ps-compat.ps1`（脚本双宿主兼容，13/0 基线）
- `tests/scripts/verify-color-wiring.ps1`（153/0）
- `tests/scripts/verify-gainmap-engine.ps1`（221/0）
- `tests/scripts/verify-decision-delivery.ps1`（94/0）
- `tests/scripts/verify-ui-host.ps1`（Avalonia UI 在 GA 下的最直接信号）
- 如要出包：`tests/scripts/verify-release-package.ps1`（#93）与整轮 `_run-step3-gates.ps1`（100 条）
裸跑必须自钉 TMP：`$env:TMP=$env:TEMP=$env:TMPDIR='<repo>\tests\output\_tmp\round-tmp'`、`$env:LC_ALL=$null`。

### 4.4 出包（需用户先拍版本号）
- 版本唯一来源 = `src/FfmpegGui/FfmpegGui.csproj` 的 `<Version>`；当前 `1.6.0-beta12`（tag `1.6.0BETA12` 锚 `54c31c0`）。
- §8 纪律：**版本位只进不退、tag 一经推送即冻结**；release 提交只装「版本位 + CHANGELOG(zh/en) + tag」，
  修复独立 fix 提交。出包前删旧包（`publish/FFmpegPictureUI-v<旧版>-x64*.7z` 与 `publish/build/FFmpegPictureUI-v<旧版>-x64*`），
  `publish/PLAN` 与 `publish/rescued-*` 不删；删前扫 reparse point。
- 现状：`publish/` 仍是 **11-04 的 beta12** 双包（`...-beta12-x64.7z` 11,423,133 B / `...-beta12-x64-full.7z` 55,859,128 B）
  + 两个解包目录，**都是 RC.3 工具链产物**；`#93 verify-release-package` 本就红（包内 exiftool 13.60/dngtool 旧，
  落后 `publish/PLAN` 13.61），重出包时一并解。
- `pack.ps1`：`-Mode app`（AOT 单文件，不含 PLAN）/ `-Mode full`（AOT + PLAN）/ `-Mode all`；版本号默认读 csproj。

### 4.5 文档/账本
- AGENTS §7 追加一条「.NET 11 GA 升级」：SDK 11.0.100-rtm、运行时 11.0.0、build 读数、AOT 读数、门禁读数、指纹。
- `docs/HANDOVER.md:3671`「无 global.json」与 CHANGELOG 里 rc.* 版本串都是**历史读数**，新增更正/条目，不回改。
- 本交接文件若长期保留，按 `docs/INDEX.md` 惯例登记一行。

---

## 5. 风险与已知坑（接手必读）

1. **AOT 资源表丢失**：必须 build→publish `-p:NoBuild=true`（§4.2）。
2. **陈旧运行时证据**：`bin/Debug/**`（rc.3，11-10 01:12）、`bin/x64/Debug/**`（rc.1，10-17）、
   `bin/Release/net11.0/FfmpegGui.runtimeconfig.json`（framework-dependent，11-11 01:27）都是残留，**不是当前产物**。
   要核对 GA，只认 `bin/Release/net11.0/win-x64/`（本次 23:16:54 重建）与 AOT 产物。
3. **自包含构建的 RID**：csproj 只写 `<RuntimeIdentifiers>win-x64;linux-x64;linux-arm64</RuntimeIdentifiers>` 与
   `Condition=IsTargetFrameworkCompatible(net11.0)` 下的 `<SelfContained>true</SelfContained>` ⇒ 普通 build 也会带 RID 产物；
   `dotnet restore` 不带 RID 时不会拉 runtime pack（本次 assets 里查不到 `Microsoft.NETCore.App.Runtime.*` 属正常）。
4. **不要在 `publish/` 里做试验**：仓库规则「出包只留最新一档」；先用 `tests/output/_tmp/net11-ga/` scratch。
5. **#85 提权红**：本会话是提权上下文，跑 `verify-package-gui-starts` 的 ⑥ 会红（真读数，不是假红）。
6. **NuGet 离线风险**：GA runtime pack（`Microsoft.NETCore.App.Runtime.win-x64/11.0.0`）首次需要下载；
   本次 restore 已成功（`restore.log` rc=0），说明本机可获取。
7. **工作区边界**：一切产物写 `tests/output/_tmp/`；禁止仓外写盘。
8. **停止时的未提交变更**：仅 `global.json`；若接手者决定不用它，`Remove-Item global.json` 即可回滚（无其它改动）。

---

## 6. 关键文件索引

| 文件 | 用途 |
|---|---|
| [global.json](global.json) | 本次新增，钉 GA SDK（未提交） |
| [pack.ps1](pack.ps1) | 出包脚本；AOT 命令在 `:96-107`，PLAN 校验在 `:132+` |
| [src/FfmpegGui/FfmpegGui.csproj](src/FfmpegGui/FfmpegGui.csproj) | TFM `net11.0`、`<Version>1.6.0-beta12`、RID、SelfContained/AOT 属性 |
| `tests/output/_tmp/net11-ga/restore.log` | `restore --force` 读数 |
| `tests/output/_tmp/net11-ga/build.log` | GA 全量构建读数（4 警告明细） |
| `tests/output/_tmp/beta12-fix-verify/gates.txt` | 上一批（RC.3 工具链）门禁基线读数，供 GA 对照 |
| [docs/INDEX.md](docs/INDEX.md) | 文档登记处（本文件按需登记） |
| [CHANGELOG.md](CHANGELOG.md) / [CHANGELOG.en.md](CHANGELOG.en.md) | 发布记录；rc.* 历史串属时间点，勿回改 |

---

## 7. 交接完成标志（Definition of Done）

1. AOT scratch 发布成功且 §4.2 的 5 项验收全落日志；
2. 代表性门禁在 GA 下全绿（读数绑定新 dll/exe 指纹）；
3. `global.json` 已提交（或明确决定不提交并说明理由）；
4. 用户已拍版本号 → 正式出包 + `#93` 复跑绿 + 旧包删除；
5. AGENTS §7 / CHANGELOG 条目落地，本交接文件归档或登记进 `docs/INDEX.md`。

---

## 8. 接手进展（2026-11-12 追加；§1–§7 为前会话停止点原貌，未回改）

- **§4.1 起步自检**：`dotnet --version`=11.0.100、HEAD=`88844ec`、未跟踪仅 `global.json` + 本文件。✅
- **§2 未完成项 1（runtimeconfig 核对）**：`bin/Release/net11.0/win-x64/` 下 **25/25** 份
  （FfmpegGui + 12 测试宿主，自包含 ⇒ 结构是 `includedFrameworks` 非 `framework`）全
  `Microsoft.NETCore.App 11.0.0`，0 份 rc.*；mtime 全部 11-11 23:16:5x–23:17:0x。
  `obj/project.assets.json` ILLink=`11.0.0`；当前 Release 产物与全部 assets 无 `11.0.0-rc` 串。
  ⚠ 交接文 §1/§2 里「runtimeconfig → Microsoft.NETCore.App = 11.0.0」读法成立，但字段位置是
  `includedFrameworks[0]`（当时描述略粗，此处补准）。✅
- **§2 警告复核**：4 条全部与 GA 升级无关——IL3002/CA1416 是 AGENTS §7 既有两条；
  CS8604/CS8602 两行最后触碰分别是 `0f6c11a`（RawColorPipeline.cs:640）与 `c1b87d3`
  （QueueProcessor.cs:1557），GA 会话源码零改动。RC.3 时代的 `beta12-fix-verify/build.log`
  是增量构建（0 警告）不可作对照，改用 git 取证。✅
- **§4.2 AOT scratch 五项验收全过**（证据汇总 `tests/output/_tmp/net11-ga/aot-acceptance.md`）：
  ① rc=0，exe **24,219,136 B**（beta12 = 23,923,712 B，+1.2%，非丢资源表的偏小信号）；
  ② `!AvaloniaResources`×4 / `!XamlIlPopulate`×2；③ `--help` rc=0 + headless jxl（2,838 B）/
  png（1,189 B）两臂 rc=0（`FFMPEGGUI_PLAN_DIR=C:\PLAN\ffmpegPictureUI\publish\PLAN` 定位，
  工具链全 ✅ [PLAN]）；④ GUI `MainWindowHandle=2427750`（1s）+ `CloseMainWindow` → **exit=0**；
  ⑤ exe sha16 **`BBF3299E6C3435FF`**、ProductVersion `1.6.0-beta12+88844ec…`。✅
  - **新坑（提权会话）**：`Start-Process`（ShellExecute）弹「打开文件 - 安全警告」模态框永久阻塞
    driver；exe 无 Zone.Identifier ⇒ 非 MOTW，是提权 + ShellExecute 行为。修法 =
    `ProcessStartInfo.UseShellExecute=$false`，已固化进 `gui-smoke.ps1`。
  - `--version` 不在 CLI 面（help 无此 token）⇒ smoke 用 `--help`；
    `[dngtool] unknown option: --version` 是检测探测的良性输出。
- **§4.3 代表门禁 GA 下全绿**（裸跑自钉 TMP=round-tmp、LC_ALL 清空）：ps-compat **13/0**、
  color-wiring **153/0**、gainmap-engine **221/0**、decision-delivery **94/0**、ui-host **458/0**
  （72s，SKIP=1 = J1z 无 GPU 检测，既有口径）。读数绑定 `FfmpegGui.dll` sha16
  **`525450CF9EE1F34A`**（RC.3 时代 `782F970073393F9C`，如预期变化）、
  `ServiceProbe.dll` `87E4412B289FC29C`。✅
- **§4.4 版本号已裁定：`1.6.0-rc.1`**（用户 2026-11-12 拍板）。但用户同时指示**不急着出包**：
  先行「测试拆碎成模块、针对于不同情况、保证测试不落后与覆盖范围正确」任务（用户自评：复杂任务），
  出包顺延到其后；届时按 §4.4/§8 流程走（rc.1 升版本位 + CHANGELOG zh/en + tag + 删旧包 + `#93` 复跑）。
- **§2 未完成项 4（提交 global.json）**：已提交（本批 build+docs 一刀，见 git log）。
- **§2 未完成项 6（文档/账本）**：AGENTS §7 新条目已落（含本节读数与坑）、本文已登记
  `docs/INDEX.md` §1。CHANGELOG 未动（版本位未变，rc.1 出包批再记）。
- **未做（显式登记）**：100 条整轮（`_run-step3-gates.ps1`）GA 下未跑——上一修复批顺延的
  「整轮基线重测」仍然悬着，归入出包批或测试模块化批之后的发布轮。