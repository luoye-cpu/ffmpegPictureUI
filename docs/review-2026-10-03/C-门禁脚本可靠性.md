# C — 门禁脚本可靠性独立复查（假绿 / 假红专项）

- 复查基准：`git diff e7de117..HEAD -- tests/scripts/`，HEAD = `bdd4b0b`，基准 `e7de117`（v1.6.0-beta3）。
- 本次复查**只读**：未修改任何 `.ps1` 源码、未 `git add/commit`。
- 需要落地执行的取证（PS5.1 解析 / 语义探测 / 两个探针实跑）全部写在 `C:\PLAN\_gate_review_tmp\`（仓库外），
  跑完已删除；**没有**做任何"就地改坏再还原"的变异，全部结论来自**静态分析 + 代码取证 + 只读运行**。
- 本机宿主实测 `PSVersion = 7.6.6`；PS5.1（`5.1.26100.9549`）用于**解析与语义取证**（见 §五）。
- 结论口径：每条给「文件:行号」；凡实测过的条目都附上实测数字。

---

## 一、严重（会导致假绿）

### S1. `verify-orientation-rewrap.ps1:136-141` —— ffprobe 读不到 ⇒ 空串 ⇒ 两臂**同时**假绿

```powershell
136  $dimProd = ((X $fp '... -show_entries stream=width,height ...').text -join '').Trim()
138  $pixRotated = ($dimProd -ne $dimRaw)
140  CK (...：产物像素已被旋正（码流尺寸 640,480 → 480,640）) $pixRotated ...
141  CK (...：旋正后必须丢弃/归一 Orientation...) (($o -eq '<无>') -or ($o -eq '1')) ...
```

- 判据的**正向属性**（"像素已旋正"）是由 `$dimProd` 与源尺寸**是否不等**定义的。当 ffprobe 调用失败
  （工具链不在位、stdout 重定向文件为空、进程被杀）时 `$dimProd = ''`，于是 `'' -ne '640,480'` = **True**
  ⇒ "已被旋正" 记 **PASS**；同一格 `Orient`（:68-74）在同样失败下返回 `'<无>'`，:141 的
  `($o -eq '<无>')` 也成立 ⇒ 第二条 **PASS**。
- 即：**一次读取失败 = 该格两条硬判据同时绿**。这正是清单第 6 项「边界由被断言的属性本身定位」＋
  第 4 项「失败沿用默认值」的叠加形态。
- 同文件里 `jxlinfo` 缺件是**有**护栏的（:126 显式 `CK $false`），说明作者知道要防，但 ffprobe 这一路没有。
- 对照：`verify-jbrd-e2e.ps1:289` 的同类判据写了 `($dS -ne '') -and ($dR -ne '') -and ...` 的空值前置，
  **同一批提交里两种写法并存**，可判定为本条的修法已在本仓存在、只是没落到这个文件。

### S2. `verify-orientation-rewrap.ps1:164-187` —— B 段（TIFF DPI）夹具缺失 ⇒ 整段 0 断言仍全绿

```powershell
161  @{ n='8bit';  src="$root\tests\output\sources\src_8bit.png" },
162  @{ n='16bit'; src="$root\tests\output\sources\src_16bit.png" }
165  if (-not (Test-Path $c.src)) { '  SKIP  B ' + $c.n + '：夹具缺失 ' + $c.src; continue }
```

- 两个夹具都在 `tests/output/sources/`，而 `.gitignore:64` 把 `tests/output/` 整棵挡在版本控制外
  ⇒ 换机或 `git clean -xdf` 后必然不在位 ⇒ B 段 `continue` 掉、**一条断言都不产生**。
- 该 `foreach` 没有任何「条数 ≥ N」下限断言（清单第 5 项），B 段静默消失不会让 `$bad`（:189）变大。
- 与 A 段对照：A 段的 `$rows`（:107-114）是硬编码 6 行，不依赖外部文件 ⇒ 只有 B 段有这个洞。

### S3. `verify-package-smoke.ps1:95 / :99 / :100 / :129` —— 否定式日志断言**没有**"取到命令行"前置

```powershell
95   CK 'B4 负控：显式关掉 => 不再发 =1'       ($c.log -notmatch '--lossless_jpeg=1') ...
99   CK 'C1 命令行不含 =auto'                  ($e.log -notmatch 'photon_noise_iso=auto') ''
100  CK 'C2 无「传输错误」/无「超时」'          (($e.log -notmatch '传输错误') -and ($e.log -notmatch '超时或已取消')) ...
129  CK 'E3 命令行不含 -distance 0 的冒充'     ($h1.log -notmatch '-d 0' -and $h1.log -notmatch '-distance 0') ''
```

- 这四条都是**否定式**判据，而 `$log`（:56）可能为空（日志捕获口径变了、产品不再回显命令行、
  重定向文件被占用/截短、PS5.1 下的编码问题）。空串上 `-notmatch` **恒真** ⇒ 四条同时假绿。
- 本仓对这个陷阱的**正解就写在同批的另一个文件里**：`verify-jbrd-e2e.ps1:51-68`（`CmdOf` + `Seen`）与
  :307/:317/:344 的 `(Seen $cmd) -and (...)` 合取写法，并在 :52-53 明确注释"否定式必须与『取到了命令行』
  这个前置合取"。⇒ **同一批提交里，jbrd 收了这个口，package-smoke 没收**。
- 同理 `verify-orientation-rewrap.ps1` 没有这类断言（不中），`verify-gainmap-thirdparty.ps1:141` 中（见 M6）。

### S4. `verify-encoder-knob-liveness.ps1:226-232` —— `undecidable` 桶不判红，且**没有**「活 ≥ N」下限

```powershell
226  $nFail = $c['inert'] + $c['missing'] + $c['nocmd']
230  if ($res.Count -ne $wantArms) { ... exit 1 }
232  if ($hard.Count) { ... exit 1 }
233  exit 0
```

- 六态里 `undecidable`（:189，"两极端值产物相同 ⇒ 本夹具无判别力"）**完全不计入** `$nFail`，也不进 `$hard`。
  :229-230 的下限只保证**臂数**（`$res.Count == $rows.Count + $naming.Count`），**不保证任何一条臂有判别力**。
- ⇒ 若夹具退化成纯色图（脚本头 :102-103 **自陈**："用纯色渐变时 18 条里 12 条报『产物相同』"），
  19 条臂会变成 18~19 条 `undecidable` + 1 条 `live` ⇒ `$nFail=0`、臂数吻合 ⇒ **exit 0 全绿**，
  而汇总行 :227 打着 `PASS=1 FAIL=0 未判(不计入)=18`。脚本头 :10-13 声称"不许把判不出编成结论"，
  **退出码这一层没有兑现**。
- 部分缓解：运行器 `_run-step3-gates.ps1:1201-1208` 的零断言闸在 `PASS=0` 时会拦；但只要**混进 1 条 live**
  （例如 JXR 那臂 :140 恒活）就不拦 ⇒ 缓解不覆盖全形状。
- 建议：把 `undecidable` 数上限写进断言（如 `undecidable ≤ 臂数/3`）或加 `live ≥ N`。

### S5. `verify-jbrd-e2e.ps1:29` / `verify-gainmap-thirdparty.ps1:38` / `verify-encoder-knob-liveness.ps1:40`
      —— `T76_EXE / T77_EXE / T79_EXE` 在**版本护栏之后**覆盖 `$exe`，护栏被静默绕过

```powershell
jbrd     15-28  if ($env:PKG_VER) { ... exit 2 ... } else { ... exit 2 ... }
jbrd     29     if ($env:T76_EXE) { $exe = $env:T76_EXE }
```

- 覆盖发生在两档护栏之后，**且覆盖后不再做任何 ProductVersion 校验、也不重新打印 SUBJ 行**
  （Release 档的 `sha=` 是 :27 打印的，打的是**被覆盖前**的那份）。⇒ 读数可以打在任意异构/陈旧二进制上，
  日志里的 SUBJ 还指向另一份 —— 这正是清单第 12 项要防的"beta4 的读数打在 beta3 包上"，
  只是触发方式从"目录写死"换成"环境变量"。
- 三个文件形态一致（jbrd:29 / gainmap3p:38 / knob:40）；`verify-package-smoke.ps1` 无此覆盖（不受影响）。

---

## 二、中等

### M1. `_probe-cjk-hardcode-scan.ps1:299-315` —— 声称"余量 0 ⇒ 已被基线精确吸收"，**实测余量 19**

- 实跑 `pwsh -File tests/scripts/_probe-cjk-hardcode-scan.ps1`（只读，exit 0 / PASS=14 FAIL=0）：

  | 桶 | 基线 | 实测 | 余量 |
  |---|---|---|---|
  | XAML | 13（:299） | 13 | 0 |
  | **CsUi（唯一判据桶，:314 = 840，判据在 :421）** | **840** | **821** | **+19** |
  | Cs（ℹ，:300 = 1320） | 1320 | 1355 | **−35（超基线）** |
  | CsTag（ℹ，:301 = 459） | 459 | 487 | **−28（超基线）** |
  | CsOther（ℹ，:302 = 861） | 861 | 868 | −7 |

- 头部注释（diff 新增段）写"判据项净 +3""余量 0 ⇒ 已被基线精确吸收"⇒ **与实测不符**：
  判据桶实际多出 **19 行**余量，即此后新增 19 行硬编码中文 UI 文案都会被静默吸收、不判红。
- 成因可解释（同一 diff 里的两处口径修正——行尾 `//` 剥离 :361-363、诊断续行归类 :393-395——会**减少**计数，
  基线却按修正前的口径抬的），但结论不变：**基线抬过头了，判据被放宽**。
- ℹ 桶超基线不影响判据（②-b 口径 :210-212 有意如此），仅记录。

### M2. `verify-colorfmt-matrix.ps1:18 / :49 / :72` —— 参照 ICC 缺失 ⇒ ICC 臂降级为 WARN，**判据失去判别力仍 exit 0**

```powershell
18   $refIcc  = "$root/tests/output/validate/jpgp3/p3.icc"
49   $refMd5 = if (Test-Path $refIcc) { (...) } else { "无参照" }
72   $verdict = if ($m -eq $refMd5) { $pass++; "✅ ..." } else { $warn++; "⚠ 有 ICC 但非 P3 参照..." }
```

- `$refIcc` 是**另一条门禁**（`_probe-jpg-p3-icc.ps1`）的遗留产物，且落在被 gitignore 的 `tests/output/` 下。
- 缺失时 `$refMd5` 取默认值 `"无参照"`，于是**每一格**只要产物带 ICC 就落到 `⚠`（`$warn++`），
  而 `⚠` **不计入 FAIL**（:94 汇总、:96 判 exit）⇒ 该格判据事实上失效却不算失败。
  这是清单第 3 项（依赖上一轮遗留产物）+ 第 4 项（失败沿用默认值）的组合形态。
- 只有落进 :85-88 的"无 ICC 且无 CICP"才计 FAIL ⇒ 参照缺失时**不会自动红**。

### M3. `verify-colorfmt-matrix.ps1:21-31 / :94-96` —— "加牙"后仍保留一条结构性绿灯

- 头部自认：`⚠` 分支是"**已知假警报**"，故不计入 FAIL。⇒ 只要 6 格全部落 `⚠`，得
  `PASS=0 FAIL=0 WARN=6` + `exit 0`；唯一能拦的是运行器零断言闸（`PASS=0` ⇒ 红）。
  若其中 1 格走 CICP 命中 ⇒ `PASS=1 FAIL=0` ⇒ **运行器也不拦**，脚本绿灯退出。
- 即：本条声称修掉的"表格型 ⇒ 结构上永不可能失败"**只修了一半**（另一半押在运行器的 PASS=0 闸上）。

### M4. `verify-jbrd-e2e.ps1:240` —— `Pix` 读不到时两侧同空 ⇒ 恒真 PASS

```powershell
240  CK (...：色度采样/编码模式未被改) ($pRt -eq $pSrc) "$pSrc -> $pRt"
```

- `Pix`（:88-91）在 ffprobe 无输出时返回 `$null/''` ⇒ 两侧同空 ⇒ `-eq` 恒真 ⇒ PASS。
- 同文件 :244-245 对元数据标签集**有** `tagVacuous` 空值护栏（"两侧标签集均空 ⇒ 记未判"），
  ⇒ 同一函数族里两种写法并存，本条是漏掉的那个。

### M5. `verify-jbrd-e2e.ps1:163-164` —— ultrahdr 夹具 0 产物时把 `$null` 写进 `$made`，"集合完整"判据被绕

```powershell
163  $made['ultrahdr'] = $u.file.FullName      # $u.file 为 $null ⇒ $null.FullName = $null
195  CK ('夹具集合完整（应 {0} 种形态）' ...) ($lack.Count -eq 0) ...
```

- `$made.Contains('ultrahdr')` 仍为 True ⇒ :195 判"齐"，而该格实际拿着 `$null` 进 A 段。
- 下游（:201 `App` + :202 `count -ne 1`）会撞出红，所以不是静默假绿 —— 但**靠的是下游侥幸**，
  不是 :195 这条下限判据本身有效（`Contains` 不等于"值有效"）。

### M6. `verify-gainmap-thirdparty.ps1:141` —— 否定式断言无"读到了"前置

```powershell
141  CK 'B2 exiftool 也读不到第二子图' (($eb.text -notmatch 'MPImage')) ...
```

- 与 S3 同族：exiftool 输出为空 ⇒ 恒真 PASS。A11（:131）是肯定式（`|` 或），不受影响。

### M7. `verify-encoder-knob-liveness.ps1:204-212` —— N 段"点名"判据退化为"崩溃即通过"

```powershell
209  $honest = ($z.code -ne 0) -or $named
210  Rec (...) $(if ($honest) { 'live' } else { 'inert' }) ...
```

- 只要子进程**非零退出**就记 `live`/PASS，与"有没有把值点名"无关 ⇒ 产品因为**任何**原因
  （参数解析崩、PLAN 缺件、夹具路径错）在 `--avif-tune film` 上 rc≠0，这一臂都记 **PASS**。
- 该段的语义（:206-207）本应是"要么响亮失败、要么播报"，把两者**或**起来等于把判据交给了 `rc`。

### M8. 日志措辞断言清单（第 7 项：断言日志出现 ≈ 只证明"推进到下一事件"）

| 文件:行 | 断言 |
|---|---|
| `verify-jbrd-e2e.ps1:220` | `$j.log -match 'gain map\|增益图'`（"被点名"） |
| `verify-jbrd-e2e.ps1:226-228` | `$b.log -match 'ffmpeg 直读\|PNM 中转失败'` |
| `verify-jbrd-e2e.ps1:279` | `$b.log -match '降级\|无色彩标注\|Unlabeled\|不补 ICC\|色彩后置'`（**且它是 ICC 保留臂的"或"分支** ⇒ 只要日志碰巧含"降级"四字，ICC 静默丢失也判 PASS） |
| `verify-jbrd-e2e.ps1:319/331/346` | `无损` / `jbrd` / `structurally exclusive\|modular mode wins` |
| `verify-package-smoke.ps1:128` | `$h1.log -match 'jbrd'` |
| `verify-encoder-knob-liveness.ps1:208` | `$z.log -match $n.pat`（见 M7） |

- 这些都属于"证明产品说了这句话"，不证明产物里真的有对应字节；其中 jbrd:279 最具风险（它是 OR 的右臂）。

### M9. `verify-decision-delivery.ps1:641` —— ⑬ 阈值 40 → 25 dB，松弛无本轮变异证据

- 新阈值 25 dB 与声称的旧缺陷读数（14.74 dB）只隔 ~10 dB；支撑它的"无损臂 ⑬c ≥ 40 dB"（:652）
  是**新加**的，本轮未见其变异验牙记录（既没看到 ⑬c 在缺陷态下转红的实测，也没看到 ⑬ 在 25 dB 下
  仍能红的实测）⇒ 属于"声称已验证、本轮无证据"。
- 结构上是安全的：⑬c 与 ⑬ 都在 `if (-not (Test-Path $jxlinfo)) { CK $false ... } else { ... }`
  （:599-601）内 ⇒ jxlinfo 缺件必红，不会静默跳过。

### M10. `verify-orientation-rewrap.ps1:58` —— `throw` 让一格失败炸掉整脚本（与同批修法相反）

```powershell
58   if ($f.Count -ne 1) { throw ("{0} 产物数={1}（[{2}]）" -f $tag, $f.Count, $extra) }
```

- `$ErrorActionPreference = 'Stop'`（:14）⇒ 任一格 0/多产物即整个脚本中断，**后面所有格一条都不跑**，
  且**不打印** `PASS=n FAIL=m` 汇总行（:190）⇒ 运行器看到的是"无 PASS=n" ⇒ 记零断言红 + exit 非零。
- 不会假绿（是假红/信息丢失），但同批 `verify-jbrd-e2e.ps1:75-79` **已经明确改掉**这个写法
  （注释："本轮第一版就是 throw 掉后面四段，导致 B~F 一个都没跑"）⇒ orientation 没收这处返工。

### M11. `verify-release-package.ps1:71` —— "目录数 == 归档数" 在两者都为 0 时成立

```powershell
71   CK "$($m.tag) 磁盘目录与归档同数" ($dirFiles -eq $arcFiles) "目录 $dirFiles vs 归档 $arcFiles"
```

- `0 -eq 0` ⇒ PASS。兜底靠 :56 的条目数下限（≥560 / ≥8），是**间接**兜底；建议直接加 `$arcFiles -gt 0`。

### M12. 依赖 gitignored 遗留产物（第 3 项总表）

| 文件:行 | 依赖 | 缺件行为 | 判定 |
|---|---|---|---|
| `verify-encoder-knob-liveness.ps1:104-105` | `tests/output/sources/src_8bit.png` | `exit 2` + 记 `missing` | **fail-closed ✔** |
| `verify-gainmap-thirdparty.ps1:78 / :134` | `src_hdr_pq.png` / `src_8bit.png` | 无 `Test-Path`，但 `count -ne 1` ⇒ `CK $false` + `exit 1`（:80/:136） | 红 ✔（靠下游） |
| `verify-jbrd-e2e.ps1:153 / :159 / :167` | `srgb.icc` / `src_hdr_pq.png` / `src_photo.jpg` | :148/:160/:168 分别记 `CK $false` 或靠 :195 集合下限兜 | 红 ✔ |
| `verify-orientation-rewrap.ps1:161-165` | `src_8bit.png` / `src_16bit.png` | `continue` + `SKIP`，**不记 FAIL** | **✘ 见 S2** |
| `verify-colorfmt-matrix.ps1:18/49` | `validate/jpgp3/p3.icc` | 降级为 `⚠` WARN | **✘ 见 M2** |

（`tests/output/` 由 `.gitignore:64` 排除在版本控制外 ⇒ 这些夹具在干净 checkout 上**全部不存在**。）

---

## 三、轻微

- **L1** `verify-package-smoke.ps1:132`：只有 `if ($bad -gt 0) { exit 1 }`，无显式 `exit 0`（靠落穿）。
  语义等价，但同批 `verify-colorfmt-matrix.ps1:97-98` 已示范"退出码成对给出"，此处不一致。
- **L2** `verify-encoder-knob-liveness.ps1:56` vs `:226/:231`：`nocmd` 的**行前缀**打成 `WARN`，
  但记账口径把它算进 `$nFail` ⇒ 输出行写着 WARN、实际会 `exit 1`，读者会误判。
- **L3** SKIP 行不计入也不设下限：`verify-jbrd-e2e.ps1:247` / `:291`（元数据臂、取向自洽臂）
  与 `verify-encoder-knob-liveness.ps1:149`（`gap` 臂）。整维度 SKIP 时静默消失（清单第 5 项）。
- **L4** `verify-gainmap-thirdparty.ps1:73-76`：`if (-not (Test-Path $uhdr))` 在 :43-48 **已经 `exit 1`**
  之后 ⇒ 该分支不可达（死代码）；两处的处置口径还不一致（:47 `exit 1` vs :75 `exit 2`）。
- **L5** `_probe-cjk-hardcode-scan.ps1:361-363`：行尾 `//` 剥离用 `IndexOf('//')`，会把含 `//` 的
  **字符串字面量**（URL 等）一并截断 ⇒ 该行判据内容被削（偏"假阴"方向）。

---

## 四、已核实无问题（逐条对应清单）

| 清单项 | 结论 | 证据 |
|---|---|---|
| **1 恒 exit 0** | **无** | 6 个新脚本均无"无条件 `exit 0`"：`verify-jbrd-e2e.ps1:373`、`verify-orientation-rewrap.ps1:195`、`verify-package-smoke.ps1:132`、`verify-release-package.ps1:94`、`verify-gainmap-thirdparty.ps1:146`、`verify-encoder-knob-liveness.ps1:233` 的 `exit 0` 都在 `if ($bad -gt 0) ... else` / 两道闸之后。所有失败分支显式 `exit 1`/`exit 2`（jbrd:18/20/25/372；orientation:22/24/29/194；package-smoke:26/28/33/132；release-package:29/35/94；gainmap3p:47/75/80/146；knob:29/31/36/105/230/232）。吞错误的情形：4 个脚本用 `$ErrorActionPreference='Continue'`（package-smoke:14? 实为 'Stop'；**Continue 的是** gainmap3p:14、knob:19、release-package:21、colorfmt:4），但它们的判据值都走显式 `Test-Path` / 哨兵（`Sha` → `'<无>'` knob:51、`'<缺失>'` release-package:41）⇒ 未发现"错误被吞 ⇒ 假绿"的实例。 |
| **2 PS 5.1 兼容性** | **静态通过** | 用 PS **5.1.26100.9549** 的 `[System.Management.Automation.Language.Parser]::ParseFile` 逐个解析 15 个受检文件 ⇒ **全部 PARSEOK**（0 解析错误）。出现的 `&&`/`\|\|` 均在**字符串字面量**内（`verify-encoder-knob-liveness.ps1:86` 的 `-join ' && '`、`verify-jbrd-e2e.ps1:245/276` 的 `' \|\| '`），不是运算符；无三元 `? :` / `??` / `??=` / `?.` / `?[`。运行时语义另测：`$x = if(...){}else{}`（orientation:169）在 5.1 下**可用**（实测返回 `A`）⇒ 不构成风险。 |
| **3 路径运行时推导** | **无硬编码** | 6 个新脚本全部 `$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path`（jbrd:9 / orientation:15 / package-smoke:15 / release-package:22 / gainmap3p:15 / knob:20）。对 `C:\PLAN`、`C:/PLAN` 全库 grep 新脚本 ⇒ **0 命中**。 |
| **4 安全读函数** | **大部分有** | `Sha`（knob:51 → `'<无>'`）、`ValIn`（knob:98/100 → `'<无命令通道>'` / `'<未下发>'`）、`RawHash`（jbrd:84 → `'<undecodable>'`）、`FileHash`（jbrd:87 → `'<无>'`）、`TagSet`（jbrd:94 → `'<无文件>'`）、`Sha`（release-package:41 → `'<缺失>'` 且 :84 额外判 `-ne '<缺失>'`）。缺口见 S1/M4/M6。 |
| **5 动态枚举下限** | **部分** | 有：`verify-jbrd-e2e.ps1:193-195`（9 种形态齐备）、`verify-encoder-knob-liveness.ps1:229-230`（臂数 == 应有臂数）、`_probe-cjk-hardcode-scan.ps1:405`（XAML≥5 且 C#≥30，实测 7/83）。无：`verify-orientation-rewrap.ps1` B 段（S2）、`verify-colorfmt-matrix.ps1` 的 6 格循环（硬编码列表，风险低）、`verify-jbrd-e2e.ps1:180-190` 的 `$metaKeys`（无下限，靠 :189 逐条 `CK $false` 兜）。 |
| **6 边界自证** | **1 处** | S1（orientation:136-141）。 |
| **7 日志/Write-Host** | **部分** | `Write-Host` 在本机 PS5.1 下**能**被 stdout 重定向捕获（实测 `Write-Host` 与 `Write-Output` 两个标记都在），故不构成"读数落不了盘"；真正的风险是**断言日志措辞本身**（M8）与**否定式无前置**（S3/M6）。6 个新脚本均不用 `Start-Transcript`，但也不用 `Write-Host` 承载判据（全部走裸字符串/`Write-Output`）⇒ 落盘不成问题。进程自匹配 `CommandLine -like`：新脚本无 `Get-CimInstance Win32_Process` / `$PID` 排除需求 ⇒ 不适用。 |
| **8 子进程退出码** | **无问题** | 新脚本共 10 处 `Start-Process`，**全部带 `-Wait`**（jbrd:47/72、orientation:49/55/170-171、package-smoke:54-55/107/112、gainmap3p:61/66-67、knob:68-69）⇒ 无 PS5.1 "不带 -Wait ⇒ ExitCode 恒 `$null`" 的坑。无一处用 `$LASTEXITCODE` 取 `.ps1` 子进程的退出码。运行器自身也已知该坑并改走 `.rc` 文件（`_run-step3-gates.ps1:287-291` 注释、:328-330、:356-362）。 |
| **9 十六进制字面量** | **无问题** | 新脚本内 `0x*` 只作为 **ffmpeg 滤镜参数字符串**出现（`verify-jbrd-e2e.ps1:114-118/145`、`verify-package-smoke.ps1:65-66`），没有裸 `0xFFFFFFFF`/`0xEDB88320` 参与 `-band`/`-bxor`。PS5.1 实测确认陷阱真实（`0xFFFFFFFF` → `Int32 -1`，`-band 5` → `5`，即空操作）；本仓存量形态在 `_lib-oracle.ps1:900-908`（已改 `[long]`）与 `insert_cicp.ps1:25`（`[uint32]` 强转）。 |
| **10 dot-source 覆盖** | **已有护栏且实测有效** | `_lib-matrix.ps1:100-115`（快照 `MatrixRunSelfTest` / dot-source 后还原 `SelfTest`）。PS5.1 下复现确认该覆盖真实存在（库 `param([switch]$SelfTest)` 把调用方的 `$true` 覆盖成 `False`）。实测 `pwsh -File tests/scripts/_lib-matrix.ps1 -SelfTest` ⇒ **46 行输出**、`MATRIX RESULT: pass=38 fail=0`、exit 0 ⇒ 未出现"零行输出 + exit 0"。 |
| **11 新脚本是否接线** | **已核实 = 65，5 条全部在内** | 从 `_run-step3-gates.ps1` 的 `$scriptList` 数组字面量按 AST 抽取条目 ⇒ **count = 65、无重复**；末 5 位正是 `verify-package-smoke.ps1 / verify-jbrd-e2e.ps1 / verify-orientation-rewrap.ps1 / verify-gainmap-thirdparty.ps1 / verify-encoder-knob-liveness.ps1`。`$expectedScriptCount = 65`（:1003）与 `@($scriptList).Count`（:1138）一致 ⇒ 条数锁绿。`verify-release-package.ps1` / `probe-gainmap-tiff.ps1` **不在**清单内 —— 与前者头部 :8-12「有意不进受管清单（前置 = 磁盘上已有本次要发的包）」的声明一致，非遗漏。 |
| **12 产物/版本错配护栏** | **已实现（一处绕过）** | 5 个脚本都实现"包不在位 ⇒ exit 2 / 版本串不符 ⇒ exit 2"：`verify-jbrd-e2e.ps1:15-28`、`verify-orientation-rewrap.ps1:19-32`、`verify-package-smoke.ps1:23-36`、`verify-gainmap-thirdparty.ps1:23-36`、`verify-encoder-knob-liveness.ps1:26-39`；`verify-release-package.ps1:25-29`（不给默认版本）+ `:88`（`ProductVersion -like "$ver*"`）。**无一处回退 Debug**。绕过口见 S5（`T76_EXE/T77_EXE/T79_EXE`）。 |
| **附带核实：行号锚** | **全部对得上** | `_lib-matrix.ps1` 抬号后的 `ImageEncoderArgs.cs:773`（`AvifMaxBitDepthForEncoder`）、`:821`（`if (fmt is "png" or "tiff" or "apng" or "jxl")`）、`ColorStrategy.cs:75/77` —— 逐行 `grep -n` 命中 ✔。`verify-ffmpeg-heartbeat.ps1:269` 的 `RunAsync` 计数 24→25：`grep -c 'FfmpegRunner\.RunAsync' QueueProcessor.cs` = **25** ✔（不是"抬数变绿"）。 |

---

## 五、PS5.1 双宿主：**静态兼容 ≠ 可用**，以下必须在 PS5.1 下实测

本机宿主是 **PS 7.6.6**，运行器取自身进程路径作为子进程宿主
（`_run-step3-gates.ps1:811-815`，`Get-Process -Id $PID).Path`）⇒ 按 `pwsh -File` 跑时**整轮都在 PS7 下**，
PS5.1 分支从未被真正执行过。静态解析全部通过，但以下 4 类只能在 PS5.1 下跑出来：

1. **中文日志的编码**：`Start-Process -RedirectStandardOutput` 在 PS5.1 下按控制台代码页写文件，
   而脚本用 `[IO.File]::ReadAllText`（默认 UTF-8）回读。凡**匹配中文**的判据
   （jbrd:220 `增益图`、:226 `ffmpeg 直读`、:279 `降级|无色彩标注|不补 ICC`、:319 `无损`、
   knob:82 `命令行`、:208 的播报正则）在 PS5.1 下可能读成乱码 ⇒ 肯定式**假红**、否定式**假绿**。
   ⇒ **必须**在 PS5.1 下跑 `verify-jbrd-e2e.ps1` 与 `verify-encoder-knob-liveness.ps1` 各一次并核对读数。
2. **`$x = if (...) {} else {}`**（`verify-orientation-rewrap.ps1:169`）：已实测 PS5.1 可用（返回 `A`）✔，
   但整脚本未在 5.1 下跑过。
3. **`-in` / `[ordered]@{}` / `Get-FileHash -LiteralPath`** 等（5.1 全有）⇒ 静态已排除，
   但 `verify-orientation-rewrap.ps1:172` 的 `$_.Extension -in '.tif','.tiff'` 建议随 (1) 一起实测。
4. **`Start-Process -RedirectStandardError` 与 stdout 指向不同文件**（所有新脚本都是两路分文件）
   ⇒ PS5.1 与 PS7 的文件编码/换行差异会影响 `:split "`r?`n"`（jbrd:56 等）⇒ 同一批实测项。

---

## 六、owner 声称已验证过 —— 复核结论

| # | owner 声称 | 复核 | 依据 |
|---|---|---|---|
| 1 | 清单 60 → **65** 条，首批 L3 五条已接线 | **✔ 成立** | `$scriptList` 实测 65、无重复、5 条全在末 5 位；常量与条数锁一致 |
| 2 | "包不在位 / ProductVersion 不符 ⇒ exit 2 且不产出读数" | **△ 部分成立** | 5 个脚本都实现了，但 `T76_EXE/T77_EXE/T79_EXE`（jbrd:29 / gainmap3p:38 / knob:40）在护栏**之后**覆盖 `$exe` 且不重新校验/不打 SUBJ ⇒ 可静默绕（S5） |
| 3 | cjk 第九次抬基线"逐条归因 +3、余量 0、已被精确吸收" | **✘ 不成立** | 实跑：判据桶 CsUi 实测 **821** vs 基线 **840** ⇒ 余量 **19**；Cs 总计还**超**基线 35（M1） |
| 4 | heartbeat `RunAsync` 计数 24 → 25 "按实测新增一个调用点" | **✔ 成立** | `grep -c` = 25 |
| 5 | `_lib-matrix.ps1` 行号锚"已 `grep -n` 复核实况、全文件只出现一次" | **✔ 成立** | 773 / 821 / 75 / 77 逐行命中 |
| 6 | colorfmt "加牙后不再『结构上不可能失败』" | **△ 部分成立** | 计数+退出码补上了，但 `⚠` 明写不计入 FAIL，且参照 ICC 缺失时整臂降级为 `⚠` ⇒ 仍可 exit 0（M2/M3） |
| 7 | decision-delivery ⑬ "25 dB 仍能抓住 14.74 dB 的旧缺陷"（依据 = 13c 无损臂 ≥40 dB） | **△ 无本轮证据** | 未见本轮对 ⑬/⑬c 的变异验牙（既无"缺陷态转红"也无"25 dB 阈值有牙"的实测）；结构上有 jxlinfo 缺件必红的兜底（M9） |
| 8 | jbrd "否定式必须与『取到了命令行』前置合取（Seen）" | **✔ 在 jbrd 内成立**，**✘ 未推广** | `verify-package-smoke.ps1:95/99/100/129` 仍是裸否定式（S3）；`verify-gainmap-thirdparty.ps1:141` 同（M6） |
| 9 | 同批"不 throw、改成逐格记 FAIL" | **✔ jbrd 内成立**，**✘ orientation 未返工** | `verify-orientation-rewrap.ps1:58` 仍 `throw`（M10） |

---

## 七、取证环境说明（可复现）

- 临时目录：`C:\PLAN\_gate_review_tmp\`（仓库外，**已删除**）。
- PS5.1 解析取证：`powershell.exe -NoProfile -File` + `Parser::ParseFile($p, [ref]$toks, [ref]$errs)`，15 个文件全 PARSEOK。
- PS5.1 语义取证：`$x = if(...){}else{}` → `A`；dot-source 带 `param([switch]$SelfTest)` 的库 ⇒ 调用方 `$SelfTest` 被覆盖成 `False`（确认第 10 项陷阱真实）；
  `0xFFFFFFFF` → `Int32 -1`、`-band 5` → `5`（确认第 9 项陷阱真实）；`& child.ps1` 后 `$LASTEXITCODE = 7`；
  `Write-Host` 在 stdout 被重定向时**可**被捕获。
- 只读实跑：`_lib-matrix.ps1 -SelfTest`（46 行 / pass=38 fail=0 / exit 0）、`_probe-cjk-hardcode-scan.ps1`（PASS=14 FAIL=0 / exit 0，读数见 M1）。
- **未**实跑的长脚本（`verify-jbrd-e2e` ≈164 s、`verify-encoder-knob-liveness` ≈120 s、`verify-package-smoke`、
  `verify-orientation-rewrap`、`verify-gainmap-thirdparty`、`verify-release-package`）：按约束改为静态分析 + 读码取证。
