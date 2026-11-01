# 更正条目 2026-10-31-B：两条既存红真修（㉛ 版本函数化 · 第 56 条两站点收窄）

> 承接 [CORRECTION-2026-10-31-S8.md](CORRECTION-2026-10-31-S8.md) 与 [AUDIT-独立性审查-2026-10-31.md](AUDIT-独立性审查-2026-10-31.md)。
> 那两份里把这两条红判为「既存红 / 与产品无关 / 建议独立一批」。本条**不开独立批，就在本批真修完**，
> 并把当时给出的两个处置选项落实为可执行判据。历史读数与旧结论**按原样保留**（`docs/review-*` 不回改），
> 存在差异处以本条为准。

---

## 1. 一句话

| 红 | 修前 | 修后 | 修法性质 |
|---|---|---|---|
| 第 56 条 `_probe-stderr-merge-scan.ps1` | `pass=5 fail=1`（站点 2：`verify-jxr-highbitdepth.ps1:527`、`:648`） | **`pass=6 fail=0`**（站点 0，基线 0） | **真收窄**：合并取数 → 只读 stdout |
| ㉛ `verify-dng-metadata-transport.ps1` | 104/1（`total=9526（期望 9517）`） | **105/0**（`exiftool 13.60 · files=44（期望 44）· total=9526（期望 9526）`） | **判据函数化**：期望总数由 `exiftool -ver` 查表，表外版本判红 |

两条都不是靠放宽判据换绿：一条把取数面收窄到唯一正确的那条流，一条把「常数期望」换成「带牙的版本函数 + 母体件数断言」。

---

## 2. 第 56 条：两处合并取数站点改为只读 stdout

### 2.1 站点与值所在流

两处都是 ffprobe 的收尾自证（⑥ stub 还原本证、⑩ legacy 还原本证），断言 `($rXX.code -eq 0) -and ($rXX.text -match 'af16')`：

```powershell
# 修前（:527 / :648 同款）
$r7back = Exec $fp ('-v error -select_streams v:0 -show_entries stream=pix_fmt -of csv=p=0 "' + "$work/hp_f16.jxr" + '"') 'stub_restore'
```

`-of csv` 的 pix_fmt 值**只走 stdout**；stderr 只装 `-v` 级诊断。用合并流（`Exec`）取，等于把「探测失败时写进 stderr 的噪声」也算进断言面——一旦 stderr 出现任何含 `af16` 的提示，红判会假绿；反之探测失败 + stderr 无输出时，失败原因又被合并流抹平。两边都不对。

同文件 `:75-81` 早已定义只读版 `ExecOut`（`text = ReadAllText($o)`，文件不存在时返回 `""`，保留 `code`），`:82-86` 的 `PixFmt()` 就用它跑同一套 ffprobe 参数。所以本次不是新造轮子，而是**把两处漏网的站点并入既有正确取数方式**。

```powershell
# 修后（:527 / :648 同款；保留 'stub_restore' / 'leg_restore' tag 与两行下游断言不改）
$r7back = ExecOut $fp ('-v error -select_streams v:0 -show_entries stream=pix_fmt -of csv=p=0 "' + "$work/hp_f16.jxr" + '"') 'stub_restore'
```

### 2.2 为什么不用注释豁免

`_probe-stderr-merge-scan.ps1` 给了三个豁免口（`:91` 行内留「合并取数已论证」、`:92` 已有纯版且非 `Exec $`、`:93` 改用 `ExecOut|ExecStdoutOnly|ExifVal`）。前两个都是「声明这处没问题」，只有第三个是**改代码**。值在 stdout 这一点是可验证的（`PixFmt()` 同参数已在用），没有「值只在 stderr」的正当理由，所以走第三条。第 56 条自身的豁免文案也写着「要么改用只读 stdout，要么……并说明为什么值在 stderr」——这里是前者。

### 2.3 读数

```text
=== 合并式取数扫描（第 56 条）===
  枚举脚本 123 个；其中定义合并 fetch 的 40 个
  PASS 合并取数读工具值的站点 0 条 ≤ 债账基线 0
  PASS 收口锁：verify-color-caps.ps1 合并取工具值 = 0（基线 0）、只读取数在位=True
  PASS 收口锁：verify-color-strategy.ps1 合并取工具值 = 0（基线 0）、只读取数在位=True
  PASS 收口锁：run-pipeline-tests.ps1 合并取工具值 = 0（基线 0）、只读取数在位=True
  PASS 收口锁：verify-engine-alpha-preserve.ps1 合并取工具值 = 0（基线 0）、只读取数在位=True
  PASS 合并 fetch 的共现基数被真正量到（实得 40 个文件，下限 30 ⇒ 掉了说明正则失效，此时站点数会假降 ⇒ 本条判红）
STDERRMERGE RESULT: pass=6 fail=0
```

共现基数仍是 40（未随站点下降而假降），说明正则还在工作、`0` 是真 0。

### 2.4 该文件自身回归

`verify-jxr-highbitdepth.ps1` 改后整支复跑：**`PASS=118 FAIL=0 SKIP=1`**，`EXIT=0`。两处站点位于同一条 `hp_f16.jxr` 的收尾自证链上，⑥ 与 ⑩ 均 PASS，证明 `ExecOut` 取到的 `text` 仍能命中 `af16`、`code` 仍为 0。

---

## 3. ㉛：期望总数从常数改为 exiftool 版本的函数

### 3.1 红因（继承前一份的定性，此处只补闭环）

`_audit-dng-meta-carry.py` 全文只有两处 `subprocess.run`，都调 exiftool（`:368` `-s3 -Make -Model`、`:372` `-a -u -G1 -s -e`），**零接触产品二进制**。同语料 44 件、同脚本、只换 exiftool：

| exiftool | 来源 | ledger 行数 | 对原期望 9517 |
|---|---|---|---|
| 13.50 | MetaCPAN `Image-ExifTool-13.50.tar.gz`，用随包 `perl.exe` 跑 | 9513 | −4 |
| **13.55** | 同源 | **9517** | **±0 ⇒ 原期望就是用它量的** |
| **13.60** | 随包现行（`publish/PLAN/exiftool/exiftool.exe -ver`） | **9526** | **+9** |

差 +9 全部是厂商 MakerNote 枚举面（`Canon:FirmwareInfo` / `TimeStamp` / `ExitPupilCode` / `Extender` / `Lens*` 新解出，`RAF:RAFVersion` → `FirmwareVersion` 改名），且新解出的 9 条**全落在 unaudited**（`audited=2367` 与三项自洽不变）。

**三版 ledger 原件本轮已逐一核在位**（此前一度以为随清扫丢失）：

```
tests/output/_tmp/etcmp/audit1350/ledger.csv   2,992,822 B   rows=9513
tests/output/_tmp/etcmp/audit1350/summary.json     4,077 B
tests/output/_tmp/etcmp/audit1355/ledger.csv   2,993,745 B   rows=9517
tests/output/_tmp/etcmp/audit1355/summary.json     4,077 B
```

13.60 一版即 `tests/output/results/dng-meta/dng-audit/ledger.csv`（**2,996,492 B / rows=9526**，每轮门禁现场重算）。三版 CSV 的列名逐字相等（`header_equal=True` 实测，列序 `file,make,model,group,tag,value,value_sha8,cls,carrier,map,reader,apply,disposition,reason,evidence,level,rule,b_recoverable`）⇒ 行数差异是**实例集合差异**，不是列错位。**期望表因此有实测出处，不是猜的。**

### 3.2 修法：版本查表 + 母体件数断言 + 表外判红

```powershell
$exVer  = (ExecStdoutOnly $ex '-ver').Trim()
$expTab = @{ '13.50' = 9513; '13.55' = 9517; '13.60' = 9526 }
$wantFiles = 44
$expTot = if ($expTab.ContainsKey($exVer)) { [int]$expTab[$exVer] } else { -1 }
...
$mTot = [regex]::Match($auTx, 'files=(\d+) total=(\d+) audited=(\d+)')
$nFil = ...; $tot = ...; $aud = ...
CK ($au[0] -eq 0 -and $nFil -eq $wantFiles -and $expTot -ge 0 -and $tot -eq $expTot -and
    $aud -gt 0 -and $aud -lt $tot -and $auTx -match "all_equal_total': True") `
   ("㉛ 逐实例账本自洽：exiftool $exVer · files=$nFil（期望 $wantFiles）· total=$tot（期望 $expTot）· audited=$aud · 三类和相等：$auLn")
```

四条设计决定，逐条对应一个失败模式：

1. **期望值随版本走**，不写死。换 exiftool 时不再"工具升级把门禁顶红、红因却长得像产品回归"。
2. **表外版本判红**（`$expTot = -1` ⇒ `-ge 0` 不成立），并在同段点名「先复算一版读数并入表（已有 13.50/13.55/13.60）」。**不许**为了变绿改一个自己都不知道出处的数——这正是原先 9,517 的毛病。
3. **`files=44` 也进断言**（新增）。只钉总数时，语料少一件而 exiftool 恰好少解若干条，会互相抵消成"总数看着对"。母体件数变了该改的是 `$wantFiles`，不是那张版本表——两者混为一谈就等于把"语料变量"和"工具变量"重新搅在一起。
4. **保留 `all_equal_total': True` 与 `aud < tot`**。前者保证处置/等级/类三个和仍等于 total（账本可复算），后者保留原有的「防把 unaudited 改名成已审计」的假绿面。

**没有选「直接改成 9526」**：那只是把下一次漂移推迟到下一版 exiftool，且同样留一个无出处的常数。

### 3.3 读数

```text
PASS ㉛ 逐实例账本自洽：exiftool 13.60 · files=44（期望 44）· total=9526（期望 9526）· audited=2367 · 三类和相等：
  out=.../tests/output/results/dng-meta/dng-audit files=44 total=9526 audited=2367 (24.9%)
  自洽: {'dispositions_sum': 9526, 'levels_sum': 9526, 'classes_sum': 9526, 'all_equal_total': True}

PASS=105 FAIL=0
```

**CK 总条数前后不变**：修前 `104/1`、修后 `105/0`，两侧 `pass+fail = 105` 相同 ⇒ 变化是「㉛ 这一条从 fail 转 pass」，不是新增了判据条目。新增的 `files=44` 断言与 `expTot` 条件合并在**同一个 CK** 里，不单独计数。

---

## 4. 回归面（本轮实跑）

| 项 | 命令 | 读数 |
|---|---|---|
| 语法解析 | `[Parser]::ParseFile` 两文件 | `parse_errors=0` / `0` |
| 第 56 条 | 裸跑（调试）＋ 运行器 `-Scripts` | `pass=6 fail=0` / `pass=6 fail=0` |
| ㉛ 所在门禁 | 裸跑 | `PASS=105 FAIL=0`，`EXIT=0` |
| ⑩ 所在门禁 | 裸跑 | `PASS=118 FAIL=0 SKIP=1`，`EXIT=0` |
| 文本级横切 | `-Scripts` 5 条（i18n / cjk / test-group-coverage / mirror-parity / stderr-merge） | 11/0、14/0、37/0、87/0、6/0，`EXIT=0`；`script-list=87 条（受管基线 87）`，`runner-self-sha256=8711987BFBC52A01` |
| **整轮 87 条** | `pwsh -NoProfile -File tests/scripts/_run-step3-gates.ps1` | **87 条全绿**，末行 `===== 运行器汇总：全部通过 =====`；非零 exit **0 条**；exit 行 114 = 27 探针 + 87 脚本；墙钟 **41.5 min**（`08:17:55 → 08:59:25`）。日志 `tests/output/_tmp/run_full_87c.log`（32,171 B） |

EOL/BOM 按仓库规矩先量后写：

| 文件 | 修前 | 修后 |
|---|---|---|
| `verify-jxr-highbitdepth.ps1` | 62,566 B / 785 LF / 785 CRLF / 无 BOM | 62,847 B / **788 CRLF** / 无 BOM |
| `verify-dng-metadata-transport.ps1` | 138,773 B / 1,777 LF / 0 CRLF / 无 BOM | 140,017 B / **1,791 LF** / 0 CRLF / 无 BOM |

两份各自保持原有 EOL 族（前者 CRLF、后者 LF），无 BOM 翻转。

### 4.1 为什么能说「这两个绿是修出来的」

当轮产物指纹 `FfmpegGui.dll=97E2C47D2B42B8E9` / `FfmpegGui.exe=D32D5A1D3BB8A686` / `ServiceProbe.exe=97179BAF0B6BD8CF` /
`UiTestHost.exe=F0BC7FC29B12F292` / `GainMapTestHost.exe=D2F3AE6B58EC70B5` 与上一轮（85/87 那一轮 `run_full_87b.log`）**逐字相同**，
即被测二进制没换过；`runner-self-sha256=8711987BFBC52A01 lines=1665 bytes=171169` 启动与收尾一致，即运行器也没动。
⇒ 从 85/87 到 87/87，变化面只有这两支被判据扫描的脚本。

---

## 5. 口径纪律

- **`docs/IMPLEMENT_PLAN_2026-10-11.md`、`CHANGELOG.md`、`docs/HANDOVER.md` 里的 9,517 不回改**：它们是 13.55 时期的时点读数，按仓规（带日期的账本是时间点记录）更正只新写条目。现行判据口径已改为版本函数，见 §3。
- **本条不重述历史结论的对错**：前一份里「本轮不改 / 建议独立一批」是当时判断，本批直接做完，差异以本条为准。
- 三个版本号的官方发布时间线（13.60 = 2026-10-28）见前一份 `:187`，此处不重复引用。
