# _probe-env-trust-baseline.ps1 —— 环境可信基线（仪器的自证，不立产品判词）
#
# 为什么必须有这条门禁（三条 2026-10-31 实机结算的"读数被静默作废"通道）：
#   ① 按**映像路径**降完整性：同一份 whoami.exe（字节全等），image 落在仓库树下自报 Low(S-1-16-4096)，
#      落在 System32 自报 High(S-1-16-12288)。项目内改名无效、同记录硬链接挪出去也无效（判定认的是文件
#      记录的**名字集合**），只有整份挪出项目才恢复 ⇒ Low 进程对 Medium 标签对象的写权被 MAC 剥夺
#      （与 DACL 无关）⇒ **只在"创建新文件"这一步失败**，读输入/算像素/编码全正常 ⇒ 批量 exit -13/-102
#      长得像产品缺陷（17-lane 审计里三条 lane 据此记过假判词）。
#   ② 工具树静默换根：FFMPEGGUI_FFMPEG_DIR 与 %APPDATA%\FFmpegPictureUI\settings.json 都能把解析指到
#      另一棵树（或一条被钉住的文件记录）⇒ "读数登记在 A 树、实跑在 B 树"，同字节在别处能写、这里写不出。
#   ③ 无效写盘臂：`& exe 1>file` 与父侧 Set-Content 里，文件是**父 PowerShell** 先创建的 ⇒ 子进程的写权
#      从未被验过；经 MSYS/bash 直接派发原生件还会伪造 -13。
# 本探针只回答"这一轮的环境能不能信"：FAIL 一律点名路径 + 完整性级别；缺件一律**计数** SKIP，绝不静默丢。
# 用法：pwsh -NoProfile -File tests/scripts/_probe-env-trust-baseline.ps1
#      （Windows PowerShell 5.1 同形；必须由 PowerShell 直接派发，见 ③）
# 退出码：0 = 无 FAIL；1 = 有 FAIL。**必须产出 PASS=/FAIL=/SKIP= 汇总行**（缺行按"无契约"记红）。
# 排查钩子（出读数但不作验收）：$env:FFMPEGGUI_ENVTRUST_CHILD_DIR=<目录> 把 ③ 的写盘目标换到别处；
#      生效时响亮播报，且该目录**不在**本脚本的自清范围（绝不递归删仓库外的东西）。
$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root
$env:LC_ALL = $null   # 仓内既有口径：LC_ALL 改变排序/解析。whoami 文案随 locale 变 ⇒ 判据只取 SID 字面量

$script:pass = 0; $script:fail = 0; $script:skip = 0
function CK([bool]$ok, [string]$what) {
  if ($ok) { $script:pass++; Write-Host "PASS $what" -ForegroundColor Green }
  else { $script:fail++; Write-Host "FAIL $what" -ForegroundColor Red }
}
function SKIP([string]$what) { $script:skip++; Write-Host "SKIP $what" -ForegroundColor Yellow }
function SAY([string]$what) { Write-Output $what }
function WARN([string]$what) { Write-Output ("[warn] " + $what) }
function Full([string]$p) { try { return [System.IO.Path]::GetFullPath($p) } catch { return $p } }
function InsideRepo([string]$p) {
  if ([string]::IsNullOrWhiteSpace($p)) { return $false }
  $f = Full $p
  $r = Full $root
  if (-not $r.EndsWith([System.IO.Path]::DirectorySeparatorChar)) { $r = $r + [System.IO.Path]::DirectorySeparatorChar }
  return $f.StartsWith($r, [System.StringComparison]::OrdinalIgnoreCase)
}
function Sha16([string]$p) {
  try { return (Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash.Substring(0, 16) } catch { return "" }
}
function SidRid([string]$sid) {
  $parts = $sid.Split("-")
  try { return [int]$parts[$parts.Count - 1] } catch { return -1 }
}
function LevelName([string]$sid) {
  if ($sid -eq "S-1-16-4096")  { return "Low" }
  if ($sid -eq "S-1-16-8192")  { return "Medium" }
  if ($sid -eq "S-1-16-12288") { return "High" }
  if ($sid -eq "S-1-16-16384") { return "System" }
  return "Unknown"
}

# ── 抛掷目录 + TMP 钉住（必须在起任何子进程之前完成）─────────────────────────────
# ⚠ 递归删除只允许作用于这一个 GUID 目录：AGENTS §5 那族教训是 Remove-Item -Recurse 指错地方会顺着
#   tests/projects/GainMapTestHost/PLAN 这个 junction 把 publish/PLAN 随包工具链一起没。本脚本全程
#   不 glob 仓库树、不碰任何重解析点，只在自己的抛掷目录里读写。
$guid = [guid]::NewGuid().ToString("N")
$work = Join-Path $root ("tests/output/_tmp_envtrust_" + $guid)
$tmpBefore = $env:TMP
SAY ("[gate] root=" + $root)
SAY ("[gate] scratch=" + $work)
if (-not (InsideRepo $work)) {
  SAY ("FAIL ⓪ 抛掷目录不在仓库树内（" + $work + "）⇒ 拒绝继续，绝不把产物往仓库外/系统 TEMP 落")
  $script:fail++
  SAY ("===== env-trust: PASS=" + $script:pass + " FAIL=" + $script:fail + " SKIP=" + $script:skip + " =====")
  exit 1
}
try { New-Item -ItemType Directory -Force -Path $work | Out-Null } catch { WARN ("建目录抛异常：" + $_.Exception.Message) }
if (-not (Test-Path -LiteralPath $work)) {
  SAY ("FAIL ⓪ 抛掷目录建不出来（" + $work + "）⇒ 本轮没有任何可信落点，其余臂一律不给绿")
  $script:fail++
  SAY ("===== env-trust: PASS=" + $script:pass + " FAIL=" + $script:fail + " SKIP=" + $script:skip + " =====")
  exit 1
}
$env:TMP = $work; $env:TEMP = $work; $env:TMPDIR = $work
SAY ("[env] 宿主=" + $(if ($PSVersionTable.PSEdition -eq "Core") { "pwsh" } else { "powershell" }) +
     " " + $PSVersionTable.PSVersion + " | TMP(钉住前)=" + $tmpBefore)
$hostExe = ""
try { $hostExe = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName } catch { }
if ($hostExe) {
  SAY ("[env] 宿主 image=" + $hostExe + " inRepo=" + [bool](InsideRepo $hostExe))
  # ⚠ §8.1 的派发宿主陷阱只跟着"谁拼 argv"走：经 bash 直接派发原生件会伪造 -13。父进程形似 bash/sh
  #    时不判红（子件仍由 PowerShell 派发），但必须播报 —— 补测的 `& exe 1>file` 形态当场不可信。
  try {
    $ppid = (Get-CimInstance -ClassName Win32_Process -Filter ("ProcessId = " + $PID) -ErrorAction Stop).ParentProcessId
    $pp = (Get-CimInstance -ClassName Win32_Process -Filter ("ProcessId = " + $ppid) -ErrorAction SilentlyContinue).Name
    if ($pp -match "^(bash|sh|msys-.+)\.exe$") { WARN ("父进程=" + $pp + " ⇒ 本门禁的读数只认 Start-Process 那几条；任何 `& exe 1>file` 形态的补测都不作数") }
  } catch { }
}

# ── 唯一的子进程派发形态：Start-Process + -Wait + 重定向（`& exe` 在部分宿主静默失败）──
$script:runSeq = 0
function Run-Child([string]$exe, [string]$argStr, [string]$wd) {
  # ⚠ 本函数内把错误转成**终止性**：Start-Process 的 -WorkingDirectory/-Redirect 参数无效时默认只打
  #   非终止性错误就继续（实测 Q:\nope 那一轮 launched 仍被置真 ⇒ 派发失败会被记成"执行到手"的假绿）。
  $ErrorActionPreference = "Stop"
  $script:runSeq++
  $o = Join-Path $work ("_r" + $script:runSeq + ".out")
  $e = Join-Path $work ("_r" + $script:runSeq + ".err")
  $r = @{ launched = $false; code = ""; out = ""; err = ""; threw = "" }
  try {
    $p = Start-Process -FilePath $exe -ArgumentList $argStr -WorkingDirectory $wd -Wait -NoNewWindow -PassThru `
         -RedirectStandardOutput $o -RedirectStandardError $e
    $r.launched = ($null -ne $p)
    if ($r.launched) {
      try { $r.code = $p.ExitCode } catch { }
      try { $r.out = [System.IO.File]::ReadAllText($o) } catch { }
      try { $r.err = [System.IO.File]::ReadAllText($e) } catch { }
    }
  } catch { $r.threw = $_.Exception.Message }
  # ⚠ $o/$e 由**父侧**创建 ⇒ 它们只证明父能写，从不作为子进程写权的证据（③ 点名的无效臂形态）。
  return $r
}

$tp = [System.IO.Path]::GetTempPath()
CK ((InsideRepo $tp) -and ([bool]$tp)) ("④a 钉住后 Path.GetTempPath() 落在仓库产物区内：" + $tp + "（谁都不许往系统 %TEMP% 写）")

$sysRoot = $env:SystemRoot
if ([string]::IsNullOrWhiteSpace($sysRoot)) { $sysRoot = $env:WinDir }
$whoamiSys = Join-Path $sysRoot "System32\whoami.exe"
$cmdSys    = Join-Path $sysRoot "System32\cmd.exe"
$whoamiCop = Join-Path $work "whoami_copy.exe"
$cmdCop    = Join-Path $work "cmd_copy.exe"
# ⚠ 仪器陷阱（FREEZE §4 那个 PATH 上的 `convert` 同族）：本机 PATH 的 whoami.exe 实测是 Git 的 GNU 版
#   （它不认 /groups ⇒ 裸 whoami 的读数会静默变空）。本脚本一律走 System32 绝对路径，并在此点名来源。
try {
  $pathWhoami = (Get-Command whoami.exe -ErrorAction SilentlyContinue | Select-Object -First 1).Path
  if ($pathWhoami -and ((Full $pathWhoami) -ne (Full $whoamiSys))) {
    WARN ("PATH 上的 whoami.exe=" + $pathWhoami + "（非 System32 那份）⇒ 判据只用绝对路径 " + $whoamiSys)
  }
} catch { }

try {
  # ══ ① 我在用哪一棵工具树测量 ════════════════════════════════════════════════════
  # 防的失效：门禁静默解析到与读数登记**不同**的那棵树（env 只覆盖 ffmpeg 一项，其余字段走 %APPDATA%
  # 的手动设置层 ⇒ 两者可以指向完全不同的树，而日志里看不出来）。
  $planRoot = Join-Path $root "publish/PLAN"; $planSrc = "仓默认 publish/PLAN"
  if ($env:FFMPEGGUI_PLAN_DIR) { $planRoot = $env:FFMPEGGUI_PLAN_DIR; $planSrc = "env FFMPEGGUI_PLAN_DIR" }
  $ffRoot = Join-Path $planRoot "ffmpeg-full"; $ffSrc = ($planSrc + " 下的 ffmpeg-full")
  if ($env:FFMPEGGUI_FFMPEG_DIR) { $ffRoot = $env:FFMPEGGUI_FFMPEG_DIR; $ffSrc = "env FFMPEGGUI_FFMPEG_DIR" }
  SAY ("[toolchain] plan根=" + $planRoot + "  (来源: " + $planSrc + " ; inRepo=" + [bool](InsideRepo $planRoot) + ")")
  SAY ("[toolchain] ffmpeg根=" + $ffRoot + "  (来源: " + $ffSrc + " ; inRepo=" + [bool](InsideRepo $ffRoot) + ")")

  $tools = @(
    @{ n = "ffmpeg.exe";    sub = "";          useFf = $true  },
    @{ n = "ffprobe.exe";   sub = "";          useFf = $true  },
    @{ n = "cjxl.exe";      sub = "jxl/bin";   useFf = $false },
    @{ n = "djxl.exe";      sub = "jxl/bin";   useFf = $false },
    @{ n = "cjpegli.exe";   sub = "jxl/bin";   useFf = $false },
    @{ n = "djpegli.exe";   sub = "jxl/bin";   useFf = $false },
    @{ n = "jxlinfo.exe";   sub = "jxl/bin";   useFf = $false },
    @{ n = "avifenc.exe";   sub = "artifacts"; useFf = $false },
    @{ n = "dngtool.exe";   sub = "artifacts"; useFf = $false },
    @{ n = "JxrEncApp.exe"; sub = "artifacts"; useFf = $false },
    @{ n = "JxrDecApp.exe"; sub = "artifacts"; useFf = $false },
    @{ n = "exiftool.exe";  sub = "exiftool";  useFf = $false }
  )
  if (-not (Test-Path -LiteralPath $planRoot)) {
    # publish/PLAN 整树 gitignore ⇒ 干净克隆无根：三条各计一次 SKIP，不给"没输出=没失败"。
    SKIP ("①a 随包工具链根不在位（" + $planRoot + "）⇒ 解析根存在性未量")
    SKIP "①b 逐工具 sha256[:16] 播报未量"
    SKIP "①c 机器级 settings.json 未量（工具树来源不明，别把本轮读数当验收基线）"
  } else {
    $hashed = 0
    foreach ($t in $tools) {
      $dir = $(if ($t.useFf) { $ffRoot } else { Join-Path $planRoot $t.sub })
      $path = Join-Path $dir $t.n
      if (Test-Path -LiteralPath $path) {
        $h = Sha16 $path
        if ($h) { $hashed++ }
        SAY ("[tool] " + $t.n + " | " + $path + " | sha256[:16]=" + $(if ($h) { $h } else { "<取不到>" }) +
             " | inRepo=" + [bool](InsideRepo $path))
      } else {
        SAY ("[tool] " + $t.n + " | " + $path + " | 不在位")
      }
    }
    CK (Test-Path -LiteralPath $ffRoot) ("①a 解析出的 ffmpeg 根**真实存在**：" + $ffRoot + "（env 指到幽灵目录 = 后面所有读数无根）")
    CK ($hashed -ge 1) ("①b 至少一个在册工具取到 sha256[:16] 并播报了驻留树（命中 " + $hashed + "/" + $tools.Count + " ⇒ 扫描面非空，防空表恒绿）")

    $sj = ""
    if ($env:APPDATA) { $sj = Join-Path $env:APPDATA "FFmpegPictureUI\settings.json" }
    if ($sj -and (Test-Path -LiteralPath $sj)) {
      SAY ("[toolchain] 机器级钉住文件=" + $sj)
      $obj = $null
      try { $obj = ([System.IO.File]::ReadAllText($sj) | ConvertFrom-Json) } catch { WARN ("settings.json 解析失败：" + $_.Exception.Message) }
      if ($obj) {
        foreach ($k in @("FfmpegDirectory", "JxlLibDir", "WindowsArtifactsDir", "ExifToolPath", "DngToolPath", "OutputDirectory")) {
          $v = ""
          try { if ($obj.$k) { $v = [string]$obj.$k } } catch { }
          SAY ("[settings] " + $k + "=" + $v)
          if ($v -and (InsideRepo $v)) { WARN ($k + " 钉在仓库树内 ⇒ 按 ① 的机制，从这里派生的子进程可能被降为 Low") }
        }
        $sf = ""
        try { if ($obj.FfmpegDirectory) { $sf = [string]$obj.FfmpegDirectory } } catch { }
        if ($sf -and ((Full $sf) -ne (Full $ffRoot))) {
          WARN ("两个来源不同树：本门禁的 ffmpeg 根=" + $ffRoot + " vs settings.json=" + $sf + " ⇒ 实跑用哪个取决于有没有 --settings/env（env 只覆盖 ffmpeg 一项）")
        }
      }
      CK ([bool]$obj) ("①c 机器级 settings.json 解析成功并逐项播报（$sj ⇒ 工具树来源可追溯）")
    } else {
      SKIP "①c 无 %APPDATA%\FFmpegPictureUI\settings.json（来源只有 env + 仓默认，已在上两行播报）"
    }
  }

  # ══ ② 仓库内映像的完整性级别 ════════════════════════════════════════════════════
  # 防的失效：读者把"降完整性导致的写失败"当成产品缺陷。级别差**本身不判红**（那是本机文档态），
  # 判红的是"级别没读到"或"级别读数与写盘对照臂互相矛盾"。
  $baseSid = ""; $baseRid = -1; $copySid = ""; $copyRid = -1
  $whoamiMissing = $false
  if (-not (Test-Path -LiteralPath $whoamiSys)) {
    $whoamiMissing = $true
    SKIP ("②a/②b 缺 " + $whoamiSys + "（whoami 不在位 ⇒ 完整性级别未量，②c 随之跳过）")
  } else {
    $rb = Run-Child $whoamiSys "/groups" $work
    $mb = [regex]::Match($rb.out, "S-1-16-\d+")
    if ($mb.Success) { $baseSid = $mb.Value; $baseRid = SidRid $baseSid }
    CK ($baseRid -gt 0) ("②a System32 基线映像自报的完整性 SID 解析到手：" + $(if ($baseSid) { (LevelName $baseSid) + " (" + $baseSid + ")" } else { "<stdout 里没有 S-1-16-* ; exit=" + $rb.code + " " + $rb.threw + ">" }))

    try { [System.IO.File]::Copy($whoamiSys, $whoamiCop, $true) } catch { WARN ("whoami 副本创建失败：" + $_.Exception.Message) }
    if (Test-Path -LiteralPath $whoamiCop) {
      SAY ("[env] baseline(System32 映像)   = " + $(if ($baseSid) { (LevelName $baseSid) + " (" + $baseSid + ")" } else { "<未解析>" }))
      $rc = Run-Child $whoamiCop "/groups" $work
      $mc = [regex]::Match($rc.out, "S-1-16-\d+")
      if ($mc.Success) { $copySid = $mc.Value; $copyRid = SidRid $copySid }
      SAY ("[env] repo-copy(仓库树内映像)   = " + $(if ($copySid) { (LevelName $copySid) + " (" + $copySid + ")" } else { "<未解析>" }))
      CK ($copyRid -gt 0) ("②b 仓库内副本映像自报的完整性 SID 解析到手：" + $(if ($copySid) { (LevelName $copySid) + " (" + $copySid + ")" } else { "<stdout 里没有 S-1-16-* ; exit=" + $rc.code + " " + $rc.threw + ">" }))
    } else {
      SKIP ("②b whoami 拷不进 " + $whoamiCop + " ⇒ 仓库内映像级别未量")
    }
  }

  # ══ ③ 子进程真的能在本轮写盘目标里创建文件？（阳性对照 = 硬闸）═════════════════════
  # 防的失效：父侧建的"重定向件"被当证据（§9 点名的无效臂）；上一轮残留同名件让本轮空跑假绿。
  # ⇒ **先删再验**，且让 cmd 自己把 `whoami /groups` 重定向进目标件：文件由**子映像** open，
  #   内容里那个 SIL SID 就是**写手自身的级别** —— 既证写权、又证是谁在写，父 PowerShell 从没碰过该路径。
  $childDir = $work
  if ($env:FFMPEGGUI_ENVTRUST_CHILD_DIR) {
    $childDir = $env:FFMPEGGUI_ENVTRUST_CHILD_DIR
    WARN ("③ 写盘目标被 FFMPEGGUI_ENVTRUST_CHILD_DIR 改写为 " + $childDir + " ⇒ 本次读数只作排查，不作验收基线；本脚本不删该目录")
  }
  $childFile = Join-Path $childDir "child_written.txt"
  $demFile = Join-Path $childDir "demoted_written.txt"
  SAY ("[env] ③ 写盘目标目录=" + $childDir)
  $arm3a = $false; $arm3b = $false; $wrote3b = $false; $cmdMissing = $false
  $writerSidA = ""; $writerSidB = ""
  if (-not (Test-Path -LiteralPath $cmdSys)) {
    $cmdMissing = $true
    SKIP ("③a/③b 缺 " + $cmdSys + " ⇒ 写盘对照臂未评估（缺件不等于通过，已计数）")
  } else {
    if (Test-Path -LiteralPath $childFile) { Remove-Item -LiteralPath $childFile -Force -ErrorAction SilentlyContinue }
    # argv 形态是实测出来的：**绝对路径的 exe + 相对路径的目标**（WorkingDirectory 定位）。
    #   · `"/abs/whoami.exe" /groups> "/abs/target"` ⇒ cmd 把两个被引号包的记号读坏（实测 "syntax is incorrect"）。
    #   · 裸 `whoami` ⇒ 本机 PATH 上是 Git 的 GNU whoami（实测报 "extra operand '/groups'"），必须走绝对路径。
    $w3 = Run-Child $cmdSys ('/c "' + $whoamiSys + '" /groups> child_written.txt') $childDir
    $arm3a = [bool]$w3.launched
    $why = ""
    if (-not $arm3a) { $why = "子进程没能派发（WorkingDirectory/重定向不可用 ; " + $w3.threw + "）" }
    elseif (-not (Test-Path -LiteralPath $childFile)) { $why = "文件不存在 = 子进程创建不了它（exit=" + $w3.code + "）" }
    elseif ((Get-Item -LiteralPath $childFile) -is [System.IO.DirectoryInfo]) { $why = "目标路径是个**目录**（Test-Path 对目录也为真 ⇒ 必须点名，别把目录当写成功的证据）" }
    elseif ((Get-Item -LiteralPath $childFile).Length -le 0) { $why = "0 字节（建出来了没写进内容 ; exit=" + $w3.code + "）" }
    else {
      try {
        $mA = [regex]::Match([System.IO.File]::ReadAllText($childFile), "S-1-16-\d+")
        if ($mA.Success) { $writerSidA = $mA.Value } else { $why = "非空但内容里没有 S-1-16-* ⇒ 不能证明这段是子进程写的（残留件/父侧代写？）" }
      } catch { $why = "写出来的文件读不到：" + $_.Exception.Message }
    }
    CK ($arm3a -and ($why -eq "")) ("③a 阳性对照：System32 的 cmd.exe **自己**创建并写入 " +
        $childFile + "（写手自报=" + $(if ($writerSidA) { (LevelName $writerSidA) + " (" + $writerSidA + ")" } else { "<无 SID>" }) +
        " ; 宿主基线=" + $(if ($baseSid) { LevelName $baseSid } else { "未量" }) + "）" + $(if ($why) { " ⇒ " + $why } else { "" }))
    if (-not ($arm3a -and ($why -eq ""))) {
      SAY "  ⇒ 本轮**任何**像素/内容/标签读数都不作数：写盘基线断了，exit -13/-102 属环境而非产品；先按 FREEZE §9 定性再重跑。"
    }

    # ③b 判别量对照：**同一份字节**换成仓库内的副本再写一次（唯一变量 = image 驻留的树）。
    #     成败本身是"读数"，但**执行与否 + 是谁在写必须计数** —— 没执行就等于让读者继续信任被降级的子进程。
    if (Test-Path -LiteralPath $demFile) { Remove-Item -LiteralPath $demFile -Force -ErrorAction SilentlyContinue }
    $copOk = $false
    try { [System.IO.File]::Copy($cmdSys, $cmdCop, $true); $copOk = Test-Path -LiteralPath $cmdCop } catch { WARN ("cmd 副本创建失败：" + $_.Exception.Message) }
    if ($copOk) {
      $w4 = Run-Child $cmdCop ('/c "' + $whoamiSys + '" /groups> demoted_written.txt') $childDir
      $arm3b = [bool]$w4.launched
      if (Test-Path -LiteralPath $demFile) {
        try {
          if ((Get-Item -LiteralPath $demFile) -isnot [System.IO.DirectoryInfo] -and (Get-Item -LiteralPath $demFile).Length -gt 0) {
            # "写得出来"必须**可归因**：内容里有 SIL SID 才算这个映像真写了盘（同 ③a 的口径）。
            $mB = [regex]::Match([System.IO.File]::ReadAllText($demFile), "S-1-16-\d+")
            if ($mB.Success) { $writerSidB = $mB.Value; $wrote3b = $true }
          }
        } catch { }
      }
      SAY ("[env] repo-child-write=" + $(if ($wrote3b) { "WRITABLE" } elseif (-not $arm3b) { "未派发（③b 没跑成）" } else { "无可归因写入（建不出文件，或内容没有 SID = §9 的形状）" }) +
           " ; 写手自报=" + $(if ($writerSidB) { (LevelName $writerSidB) + " (" + $writerSidB + ")" } else { "<无 SID>" }) + " ; exit=" + $w4.code)
      CK $arm3b ("③b 判别量对照臂执行到手（仓库内映像 " + $cmdCop + " 被派发并回报；与 ② 的级别读数联读）")
    } else {
      SKIP ("③b cmd 副本没建起来（" + $cmdCop + "）⇒ 判别量对照臂未评估")
    }
  }

  # ②c 一致性归因：级别差本身不判红（那是本机文档态），**判红的是"写不出/写得进无法归因到写手级别"**。
  # ⚠ 别把这条改成"ACTIVE 就必须写不出"：2026-10-31 本机实测是 ACTIVE **且** 仓库内 scratch 对 Low 放行
  #   （③b 写手自报 Low 却写出 3.6 KB）—— 拦截发生在**项目外**的目标（§9 那批 -13 全在那儿）。
  $demoted = $false; $levelKnown = (($baseRid -gt 0) -and ($copyRid -gt 0))
  if ($levelKnown) { $demoted = ($copyRid -lt $baseRid) }
  SAY ("[env] LOW_DEMOTION=" + $(if (-not $levelKnown) { "UNKNOWN" } elseif ($demoted) { "ACTIVE（仓库内映像 = " + (LevelName $copySid) + "）" } else { "INACTIVE（副本与基线同级）" }))
  if ($whoamiMissing -or $cmdMissing) {
    SKIP "②c 级别与写盘对照臂缺前置（whoami/cmd 不在位）⇒ 一致性核对未量"
  } elseif (-not $levelKnown) {
    CK $false "②c 完整性级别没解析到手 ⇒ 环境不可判（fail-closed：没读数不等于没问题）"
  } elseif (-not $arm3b) {
    CK $false ("②c 有级别读数（baseline=" + (LevelName $baseSid) + " / repo-copy=" + (LevelName $copySid) +
               "）但 ③b 没被执行 ⇒ " + '"写不出"和"没派发"分不清，不给绿')
  } elseif ($demoted -and (-not $wrote3b)) {
    CK $true "②c 自洽：降完整性 ACTIVE + 仓库内映像没有产出**可归因**的写入（建不出文件，或写出的内容里没有 SID）⇒ 判别量已到手（本轮凡仓库内工具报 -13/-102 先按环境定性，产物读数须指向项目外副本）"
    SAY "  ⇒ 环境判别量已到手：仓库树内映像在本写盘目标里**没有产出可归因的新文件**，这类失败不是产品缺陷。"
  } elseif ((-not $demoted) -and (-not $wrote3b)) {
    CK $false "②c 矛盾：副本与基线同级（没降完整性），仓库内映像却写不出，而 System32 映像写得出 ⇒ 拦截另有其因（文件记录/ACL），本轮读数不可归因"
  } else {
    $wr = SidRid $writerSidB
    $attested = $(if ($demoted) { (($wr -gt 0) -and ($wr -lt $baseRid)) } else { (($wr -gt 0) -and ($wr -ge $baseRid)) })
    if ($demoted -and $wrote3b) {
      WARN ("降完整性 ACTIVE，但仓库内映像仍写得出（写手自报=" + (LevelName $writerSidB) + "）⇒ 本写盘目标对 Low 放行" +
            "（§9 记的唯一逃生门是 icacls /setintegritylevel (OI)(CI)L）；**仓库外目录未量，别外推**")
    }
    CK $attested ("②c 写手自报级别与 ② 的映像级别自洽：LOW_DEMOTION=" + $(if ($demoted) { "ACTIVE" } else { "INACTIVE" }) +
                  " ; 写手=" + $(if ($writerSidB) { LevelName $writerSidB } else { "<无 SID>" }) + " ; 基线=" + (LevelName $baseSid))
  }
} catch {
  # 未捕获异常必须转红并点名 —— 静默中断就是"无输出 = 无失败"那条假绿通道。
  $script:fail++
  SAY ("FAIL 探针自身抛异常（本轮读数作废）：" + $_.Exception.Message)
} finally {
  # 自清：只删自己那一个 GUID 抛掷目录（守卫见顶部 InsideRepo 校验）；钩子指向外部目录时不碰它。
  if ((InsideRepo $work) -and ($work -match "_tmp_envtrust_")) {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
  } else {
    WARN ("拒绝递归删除非抛掷目录：" + $work)
  }
}

CK (-not (Test-Path -LiteralPath $work)) ("④b 抛掷目录已删净（残留会让下一轮空跑假绿）：" + $work)
CK ((InsideRepo ([System.IO.Path]::GetTempPath()))) ("④c 收尾时 TMP/TEMP/TMPDIR 仍钉在产物区（中途没被改回系统 TEMP=" + $tmpBefore + " ⇒ 没有件漏进系统临时目录）")

SAY ("===== env-trust: PASS=" + $script:pass + " FAIL=" + $script:fail + " SKIP=" + $script:skip + " =====")
if ($script:fail -gt 0) { exit 1 }
exit 0
