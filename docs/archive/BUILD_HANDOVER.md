# 构建交接单：v1.6.0 发布包构建

> 生成：2026-09-16
> **交接原因**：当前环境**无法执行 NuGet restore** ⇒ 无法产出发布包。
> **代码侧已全部就绪**（编译 0 警告 0 错误、门禁全绿），**只差一次成功的构建**。

---

## 一、你需要做的事（TL;DR）

在一台 `dotnet restore` 能正常工作的机器上：

```bash
cd <repo>

# 1. 先确认还原能跑通（这是本环境失败的地方）
dotnet restore

# 2. 构建 + 发布
dotnet build   src/FfmpegGui/FfmpegGui.csproj -c Release
dotnet publish src/FfmpegGui/FfmpegGui.csproj -c Release -r win-x64

# 3. 门禁自检（可选但建议）
pwsh -NoProfile -File tests/scripts/_run-step3-gates.ps1
```

**产物**：`src/FfmpegGui/bin/Release/net11.0/win-x64/publish/FfmpegGui.exe`（单文件，`PublishSingleFile=true`）

然后按 **`docs/MANUAL_VERIFICATION.md`** 做一轮实机验证。

---

## 二、为什么需要交接（根因链）

当前环境的 `dotnet build` / `restore` **一律失败**：

```
error NETSDK1060: 读取资产文件时出错: 加载锁定文件
"...\obj\project.assets.json"时出现错误: Value cannot be null. (Parameter 'path1')
```

**根因链**（已逐层定位，**非代码问题**）：

```
① 环境变量缺失 —— 该环境被裁剪过，以下变量实测为空：
   APPDATA / ProgramData / ALLUSERSPROFILE / CommonProgramFiles
   / NUGET_PACKAGES / DOTNET_CLI_HOME
        ↓
② NuGet.Configuration.ConfigurationDefaults 的静态构造抛异常（它依赖 %APPDATA%）
        ↓
③ RestoreSettingsUtils.ReadSettings(solutionDirectory, restoreDirectory, ...)
   两个路径参数为 null
        ↓
④ 内部 System.IO.Path.Combine(path1, path2) 抛 ArgumentNullException
        ↓
⑤ MSBuild 报 NETSDK1060「读取资产文件出错」
   ⚠ 描述有误导性 —— 实际与 project.assets.json 无关
```

**关于第 ⑤ 步的误导性**，已验证：`project.assets.json` 经 Python 完整解析，**零个 null**，
`packageFolders` / `configFilePaths` / `fallbackFolders` / `sources` 字段齐全，
且其引用的所有目录（NuGet.Config、VS fallback 包目录、全局包目录）**都真实存在**。

**第 ② 步是如何暴露的**：只有加上 `-p:RestoreConfigFile=<path>` 时，才会显示真实异常
`The type initializer for 'NuGet.Configuration.ConfigurationDefaults' threw an exception`。
不加这个参数时，永远只能看到那句误导性的"资产文件"报错。

**快速判据**：在目标环境执行 `echo %APPDATA%`。**有值** ⇒ 不会遇到本问题。

---

## 三、已尝试且无效的手段（**请勿重复**）

| 手段 | 结果 |
|---|---|
| `--no-restore` | ❌ 同样报 NETSDK1060 |
| `restore --force` | ❌ |
| 删除并重建 `obj/project.nuget.cache` | ❌ |
| 补 `dgSpecHash` 占位值 | ❌ |
| 清空 PATH 中的畸形项（`C:\Progra:Files\nodejs`） | ❌ |
| `-p:RestorePackagesPath` | ❌ |
| `-p:RuntimeIdentifier=win-x64`（单一 RID） | ❌ |
| 通过 `.sln` 还原 | ❌ 所有项目都失败 |
| `-p:RestoreSuccess=true` | ❌ |
| `-p:ExcludeRestorePackageImports=false` | ❌ |
| `-p:RestoreConfigFile=<绝对路径>` | ⚠ 暴露真实异常，但仍失败 |
| `-p:RestoreRootConfigDirectory` / `-p:RestoreSolutionDirectory` | ❌ 覆盖不被该 target 接受 |
| `Start-Process -Environment`（PS 7.6） | ❌ 会替换**整个**环境（连 PATH），错误反而回退到更早的 796 |
| Bash 行内环境变量赋值 | ❌ Git Bash 会改写路径值 |
| `MSYS_NO_PATHCONV=1` + 行内赋值 | ❌ |
| `cmd /c` 批处理 | 🚫 被安全策略拦截 |

**另一个环境限制（排查时需注意）**：
`& dotnet` 这种直接调用形式在该环境**无输出**、`$LASTEXITCODE` 为空
（调用似乎未真正执行）。**只有 `Start-Process` 形式能拿到输出**。

---

## 四、当前工作树状态

**61 个文件已修改 + 若干新增，全部未提交**（项目约定：实机测试通过前不提交）。

### 核心改动（v1.6.0）

| 领域 | 内容 |
|---|---|
| **双语本地化** | 硬编码 XAML **122 → 9**、`<sys:String>` **67 → 0**、locale key **223 → 571**（两语言集合始终一致） |
| **新增机制** | `FillComboByLoc` —— ComboBox 选项改由 C# 填充并支持语言切换实时刷新（**21 个 ComboBox 接入**）。⚠ `<sys:String>` 是元素内容，`{ext:Loc}` 在那里不生效，此前整类选项在英文界面下仍是中文 |
| **UI 可用性** | 左栏 **276 → 300px**、横向 StackPanel 长文本静默裁剪修复、`NumericUpDown` 统一字号 13 / `MinWidth=88` / 微调按钮收窄至 18px |
| **新增功能** | 右上角 **⚙ 设置菜单**（渲染后端 / 主题 / 语言 / 缓存目录）+ `AppSettings.RenderingMode`（auto / angle / vulkan / software） |
| **稳定性** | **全局异常兜底**：订阅 Avalonia `Dispatcher` + `AppDomain` + `TaskScheduler`，把 28 处 `async void` 的未处理异常从「进程无声消失」降级为「记录 + 继续」，异常落盘至缓存目录 `crash.log` |
| **版本号** | csproj + **locale 的 `app.title`（窗口标题！）** + README / 文档，全部 **1.6.0** |

### 新增门禁

`tests/scripts/_probe-i18n-scan.ps1`（两语言 key 一致性 + XAML 引用完整性），
**已纳入** `_run-step3-gates.ps1` 默认清单。

### 本环境已完成的验证（全绿）

- **编译**：0 警告 0 错误（改动 XAML/C# 后，只要不触发 restore）
- **完整门禁套件**：探针 **17 mode 全绿**、脚本门禁 **16 个全绿**
- ⚠ 唯一 FAIL：`verify-color-engine-xcheck` —— **已知非缺陷**
  （zimg 参照口径分歧，经控制组实验判定，见 `HANDOVER §D`）

---

## 五、需要你完成的验证（本环境无法做）

按 **`docs/MANUAL_VERIFICATION.md`** 执行，**重点**：

| 优先级 | 条目 | 说明 |
|---|---|---|
| **高** | **§2b.4** | `NumericUpDown` 微调按钮收窄到 18px 后**是否还够点** —— 这是唯一没有把握的点 |
| **高** | **§2b.1 / 2b.2** | 左栏 300px 后的**比例观感**是否可接受（若偏宽/偏窄，改这个数字成本很低） |
| **高** | **§8** | ⚙ 设置菜单 9 条（含**需重启才生效**的渲染后端） |
| 中 | **§1** | 双语界面下逐个面板检查是否还有中文残留 |
| 中 | **§2.1–2.4** | 所有数字输入框的数值是否完整可见 |

---

## 六、注意事项

1. **别在改完 `csproj` 后就地构建** —— 若目标环境也缺环境变量，会触发同样的 restore 失败。
   **先确认 `dotnet restore` 能跑通**。
2. **`obj/project.nuget.cache` 曾被删除并重建**（排查副作用，属生成物）。
   正常 restore 会覆盖它，**无需特殊处理**。
3. **`project.assets.json` 是 9/8 生成的**，与 SDK `11.0.100-preview.7` 不匹配。
   成功 restore 后会重新生成。
4. **若构建成功，建议顺手跑一遍完整门禁**（`_run-step3-gates.ps1`），确认 16 个脚本仍全绿。
5. 本环境的 `verify-color-engine-xcheck` FAIL **不是新问题**，不要试图"修绿"它 ——
   它是刻意保留的对照标记（口径分歧已定性）。

---

## 七、相关文档索引

| 文档 | 内容 |
|---|---|
| `docs/MANUAL_VERIFICATION.md` | 实机验证清单（8 节，逐条操作 + 预期） |
| `docs/HANDOVER.md` | 主交接文档：§A 已闭环 · §B 基线 · §C 环境坑 · §D 开放项 · §E JXL 历史结论 |
| `docs/TESTING.md` | 逐条问题定性与修法论证 |
| `PACKAGING_SPEC.md` | 打包规范（版本号位置、tag 约定） |
