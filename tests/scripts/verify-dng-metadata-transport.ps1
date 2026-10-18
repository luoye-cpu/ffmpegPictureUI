# ═══════════════════════════════════════════════════════════
#  verify-dng-metadata-transport.ps1 — 第 77 条（2026-10-17 接线）
#  被测：`dngtool -e` 的**源 RAW 保真**三段（拍摄字段 / 规范 OriginalRawFileData 嵌入原文件 / 内嵌预览）。
#  ⚠ 载体必须是规范标签, 不是自造容器：DNGPrivateData 规范写明是"相机厂商给自己的 raw 转换器
#     留的私有数据", 且要求块内偏移一律相对该块起点 —— 原文件的 TIFF 偏移不满足 ⇒ 不适用。
#     （2026-10-17 把自造的 'DMSL' 块换成 50828/50829/50973, 本文件的 ② 组随之换形。）
#  ⚠ ⑮ 组锁的是"GPS 不许把整条搬运降级"：源含 GPS 时三段照搬 + 容器层无 GPS + stderr 点名,
#     而 -no-raw-meta 臂点名必须消失。夹具由 exiftool 现场注入 GPS 合成（四件真件全无 GPS）。
#
#  动机（不是"再加几条断言"）：2026-10-16 的索尼 ARW→DNG 缺陷审计发现
#    · ARW 里 Imaging Edge 写的 IDC 版本栈、厂商注记、镜头校正表、预览/缩略图在转换后
#      **一条不剩**（exiftool 读到的条目数 420 → 66）；
#    · 且**在册 76 条里没有一条把非 DNG 的 RAW 喂给 `dngtool -e`** ⇒ 该缺陷没有任何判据看着它。
#  本条把这格的判据接进清单：三段搬运各有一判据，并配**会红的负控**（⑤）。
#
#  ⚠ 素材是**链外**真实相机 RAW（`tests/output/raw_samples/Sony_ILCE7S.ARW`，与
#    `_probe-raw-default-tier.ps1:69` 同一份）⇒ 缺失即点名红，不降级成 SKIP。
#  ⚠ 第 ⑦ 组另需一份**带 IDC 版本栈**的索尼 RAW：`Sony_ILCE7M2_IDC.ARW`。
#    7S 那一份不能用 —— 它的 SR2 目录里 0x7240 指向一个**空目录**、0x7241=0
#    （exiftool -v3: "SonyIDC directory with 0 entries"，`-SonyIDC:all` 命中 0 条）
#    ⇒ 拿它当 ⑦ 的源臂, 恢复判据永远只在"两边都读不出"那一侧成立 = 恒真。
#  ⚠ 判据全部走"按区间算哈希"，读数由 exiftool 独立给出，不采信 dngtool 自己的日志串。
#  ⚠ 不得用 `$arr[$a..$b]` 切大区间：PowerShell 会把整段展开成对象数组（本文件要切 0.9MB ⇒ 卡死）。
#    一律用 SHA256.ComputeHash(byte[], offset, count) 的区间重载。
# ═══════════════════════════════════════════════════════════
$ErrorActionPreference = 'Continue'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root

$dng  = "$root/publish/PLAN/artifacts/dngtool.exe"
$ex   = "$root/publish/PLAN/exiftool/exiftool.exe"
$arw  = "$root/tests/output/raw_samples/Sony_ILCE7S.ARW"
$idcArw = "$root/tests/output/raw_samples/Sony_ILCE7M2_IDC.ARW"
$d    = "$root/tests/output/results/dng-meta"
Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $d | Out-Null

# ── fail-closed：缺工具/缺素材直接点名红 ─────────────────────────────
$oracle = "$root/tests/scripts/_crosscheck-idc-changed.py"
$maker  = "$root/tests/scripts/_make-synth-idc-revert.py"
$rdp    = "$root/tests/scripts/_readback-dng-profile.py"
$opcRd  = "$root/tests/scripts/_readback-dng-opcodes.py"
$abRd   = "$root/tests/scripts/_dng-opcode-render-ab.py"
$lensRd  = "$root/tests/scripts/_readback-dng-lens.py"
foreach ($p in @(@('dngtool', $dng), @('exiftool', $ex),
                 @('RAW 素材（链外，无法自备）', $arw),
                 @('带 IDC 版本栈的 RAW 素材（链外，⑦ 组唯一可用源）', $idcArw),
                 @('changed 面的独立读手', $oracle),
                 @('抹平单项改动的夹具生成器', $maker),
                 @('ProfileToneCurve 的独立读手', $rdp),
                 @('OpcodeList3 的独立读手', $opcRd),
                 @('渲染 A/B 对账手（⑯e 重组用）', $abRd),
                 @('镜头 opcode 的独立读手（⑱ 组用）', $lensRd))) {
    if (-not (Test-Path $p[1])) {
        Write-Output "FAIL 缺 $($p[0]): $($p[1])"
        Write-Output "PASS=0 FAIL=1"
        exit 1
    }
}

$script:pass = 0; $script:fail = 0
function CK([bool]$ok, [string]$msg) {
    if ($ok) { $script:pass++; Write-Output "PASS $msg" }
    else     { $script:fail++; Write-Output "FAIL $msg" }
}
# 子进程取数一律 Start-Process（`& exe` 在部分宿主静默失败 ⇒ 空值假红）
function ExecStdoutOnly([string]$file, [string]$argStr) {
    $o = "$d/_o.txt"; $e = "$d/_e.txt"
    Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow `
        -RedirectStandardOutput $o -RedirectStandardError $e | Out-Null
    return [System.IO.File]::ReadAllText($o)
}
# 需要**退出码**的臂（⑦ 的负控、⑧ 的中文名臂、⑨..⑪ 的 py 对账）：Start-Process 只有 -PassThru 才给 rc。
# 返回 @(rc, stdout, stderr) —— 前面的逗号是防止 PowerShell 把数组当管道展开。
function ExecRC([string]$file, [string]$argStr) {
    $o = "$d/_r_o.txt"; $e = "$d/_r_e.txt"
    Remove-Item $o, $e -Force -ErrorAction SilentlyContinue
    $p = Start-Process -FilePath $file -ArgumentList $argStr -Wait -NoNewWindow -PassThru `
        -RedirectStandardOutput $o -RedirectStandardError $e
    $so = if (Test-Path $o) { [System.IO.File]::ReadAllText($o) } else { '' }
    $se = if (Test-Path $e) { [System.IO.File]::ReadAllText($e) } else { '' }
    return ,@([int]$p.ExitCode, $so, $se)
}
# ⚠ `-n -q -q` 输出仍是 "Tag : value"（-q 只压描述, 不压标签）⇒ 取裸值必须 -valueOnly,
#    否则 [int64] 转换在 "White Level  : 15360" 上炸, 判据假红（本文件第一版就踩了）。
function TagVal([string]$tag, [string]$path) {
    # exiftool 的 "只出值" 档位在本机 13.59 上仍带 "Tag Name  : " 前缀 ⇒ 手工剥第一个冒号
    # （标签名不含冒号，值里的冒号如 "2017:07:13" 在第二个之后，用 -split ':',2 才安全）
    $t = (ExecStdoutOnly $ex "-n -s -s -$tag `"$path`"").Trim()
    if ($t -eq '') { return '' }
    $p2 = $t -split ':', 2
    if ($p2.Count -ge 2) { return $p2[1].Trim() }
    return $t
}
# 同一标签在多 IFD 各有一个值时按 -G1 分组取（预览在 IFD0、备用预览在 SubIFD1）
function FirstNum([string]$v) { if ($v -match '^-?\d+') { return [int64]$Matches[0] } return -1 }
function TagByGroup([string]$grp, [string]$tag, [string]$path) {
    $t = ExecStdoutOnly $ex "-a -G1 -n -s `"$path`""
    foreach ($L in ($t -split "`r?`n")) {
        if ($L -match "^\[$([regex]::Escape($grp))\]\s+$([regex]::Escape($tag))\s*:\s*(.+)$") {
            return $Matches[1].Trim()
        }
    }
    return ''
}
$script:sha = [Security.Cryptography.SHA256]::Create()
function Hex([byte[]]$b) { (($b | ForEach-Object { $_.ToString('x2') }) -join '') }
function RangeSha([byte[]]$arr, [int64]$off, [int64]$len) {
    if ($len -le 0 -or $off + $len -gt $arr.Length) { return '' }
    return (Hex $script:sha.ComputeHash($arr, [int]$off, [int]$len))
}
function FileBytes([string]$p) { return [IO.File]::ReadAllBytes($p) }

# ── 两个臂：搬运开 / 搬运关（负控就在这对上）─────────────────────────
$on  = "$d/on.dng"; $off = "$d/off.dng"
ExecStdoutOnly $dng "-e -lossless -i `"$arw`" -O `"$on`"" | Out-Null
ExecStdoutOnly $dng "-e -lossless -no-raw-meta -i `"$arw`" -O `"$off`"" | Out-Null
$both = (Test-Path $on) -and (Test-Path $off)
CK $both "①前置 两臂都有产物（on=$((Get-Item $on).Length) off=$((Get-Item $off).Length)）"
if (-not $both) { Write-Output "PASS=$pass FAIL=$($fail + 1)"; exit 1 }

# ── ② 规范载体 OriginalRawFileData：由 exiftool 自己的解码器证明逐字节还原 ──
# 为什么这条比"我方读手说一致"硬：解码方是**第二个独立实现**（exiftool DNG.pm 的
# ProcessOriginalRaw），它按规范 50828 的 8 块/大端/每 64K 一个独立 zlib 流解出来的
# 字节若与源同哈希 ⇒ 编码合规、且厂商数据一个字节都没丢；编码写错它就直接红。
$srcB = FileBytes $arw
$recFile = "$d/recovered.arw"
Start-Process -FilePath $ex -ArgumentList "-overwrite_original -b -OriginalRawImage `"$on`"" `
    -Wait -NoNewWindow -RedirectStandardOutput $recFile | Out-Null
$recLen = if (Test-Path $recFile) { (Get-Item $recFile).Length } else { 0 }
CK ($recLen -eq $srcB.Length -and $recLen -gt 0) `
   "②a exiftool 从 50828 解出的原文件长度与源相同（源=$($srcB.Length) 解回=$recLen）"
$shaSrc = RangeSha $srcB 0 $srcB.Length
$shaRec = if ($recLen -eq $srcB.Length) { RangeSha (FileBytes $recFile) 0 $recLen } else { '' }
CK ($shaSrc -ne '' -and $shaSrc -eq $shaRec) `
   "②b 解回的原文件与源 SHA256 全等（$shaSrc）⇒ 编码符合规范、厂商注记/SR2/IDC 零丢失"
$origName = TagVal 'OriginalRawFileName' $on
$origDig  = TagVal 'OriginalRawFileDigest' $on
$privOn   = TagVal 'DNGPrivateData' $on
$baseName = Split-Path $arw -Leaf
CK ($origName -eq $baseName -and $origDig -ne '' -and [string]::IsNullOrWhiteSpace($privOn)) `
   "②c OriginalRawFileName=[$origName](应=$baseName) + Digest=[$origDig] 都在，且**不再用 DNGPrivateData**（=[$privOn]）"


# ②d 重编码不能把保真通道丢了：上一轮嵌进 50828 的原文件必须跟着走，
#      且仍由第三方解码器证明解回逐字节等于源。
$on2    = "$d/on-reencode.dng"
$rec2   = "$d/recovered2.arw"
ExecStdoutOnly $dng "-e -lossless -i `"$on`" -O `"$on2`"" | Out-Null
Start-Process -FilePath $ex -ArgumentList "-overwrite_original -b -OriginalRawImage `"$on2`"" `
    -Wait -NoNewWindow -RedirectStandardOutput $rec2 | Out-Null
$shaRec2 = if ((Test-Path $rec2) -and (Get-Item $rec2).Length -eq $srcB.Length) { RangeSha (FileBytes $rec2) 0 $srcB.Length } else { '' }
CK ($shaSrc -ne '' -and $shaSrc -eq $shaRec2) `
   "②d DNG→DNG 重编码后 50828 仍可解回且与源 SHA256 全等（重编码件=$(if (Test-Path $on2) { (Get-Item $on2).Length } else { 0 }) B）"

# ── ③ 预览与缩略图：与源同区间逐字节 ⇒ 证明未重编码 ─────────────────
$spS = [int64](TagByGroup 'IFD0' 'PreviewImageStart' $arw)
$spL = [int64](TagByGroup 'IFD0' 'PreviewImageLength' $arw)
$stS = [int64](TagVal 'ThumbnailOffset' $arw)
$stL = [int64](TagVal 'ThumbnailLength' $arw)
$dpS = [int64](TagByGroup 'IFD0' 'PreviewImageStart' $on)
$dpL = [int64](TagByGroup 'IFD0' 'PreviewImageLength' $on)
$dtS = [int64](TagByGroup 'SubIFD1' 'PreviewImageStart' $on)
$dtL = [int64](TagByGroup 'SubIFD1' 'PreviewImageLength' $on)
if ($spL -gt 0 -and $dpL -eq $spL) {
    CK ((RangeSha $srcB $spS $spL) -eq (RangeSha (FileBytes $on) $dpS $dpL)) `
       "③a 预览 JPEG 与源逐字节相同（$spL B 原字节入档，未重编码）"
} else { CK $false "③a 预览长度不符或未写（源 spL=$spL 产物 dpL=$dpL）" }
if ($stL -gt 0 -and $dtL -eq $stL) {
    CK ((RangeSha $srcB $stS $stL) -eq (RangeSha (FileBytes $on) $dtS $dtL)) `
       "③b 缩略图 JPEG 与源逐字节相同（$stL B）"
} else { CK $false "③b 缩略图长度不符或未写（源 stL=$stL 产物 dtL=$dtL）" }

# ── ④ 拍摄字段逐条对齐（源有值才要求）───────────────────────────────
$onB = FileBytes $on
foreach ($f in @('ExposureTime','FNumber','ISO','FocalLength','DateTimeOriginal',
                 'MaxApertureValue','LensModel')) {
    $s = TagVal $f $arw; $o = TagVal $f $on
    # 空 / 全横线 / 数值 0 都算"源没这项"（本夹具无镜头 ⇒ Lens=----、Focal=0）
    if ($s -match '^\s*$' -or $s -match '^-+$' -or $s -match '^(0|0\.0+)(\s|$)') {
        Write-Output "SKIP $f 源无有效值（=[$s]，不要求）"; continue
    }
    CK ($s -eq $o) "④ $f 搬运后与源一致（源=[$s] 产物=[$o]）"
}

# ── ⑤ 负控：关掉搬运的臂三段全空 ⇒ ②③④ 不是恒真；两臂像素摘要相同 ──
$offPriv = TagVal 'OriginalRawFileName' $off
$offExif = @(ExecStdoutOnly $ex "-a -G1 -s `"$off`"" | Select-String -Pattern '\[ExifIFD\]').Count
$offPrev = [int64](TagByGroup 'IFD0' 'PreviewImageLength' $off)
CK ([string]::IsNullOrWhiteSpace($offPriv) -and $offExif -eq 0 -and $offPrev -eq 0) `
   "⑤a 负控：-no-raw-meta 臂原文件未嵌/ExifIFD/预览全为零（ExifIFD=$offExif 预览=$offPrev 嵌入名=[$offPriv]）⇒ ②③④ 有牙"
$digOn  = TagVal 'NewRawImageDigest' $on
$digOff = TagVal 'NewRawImageDigest' $off
CK (-not [string]::IsNullOrWhiteSpace($digOn) -and $digOn -eq $digOff) `
   "⑤b 两臂原始像素摘要相同（搬运只动元数据，不动像素）digest=[$digOn]"

# ── ⑥ 白电平口径：非 DNG 输入取源声明的线性上限；DNG→DNG 分支未受影响 ─
$wlOn = FirstNum (TagVal 'WhiteLevel' $on)
CK ($wlOn -gt 0) "⑥a 非 DNG 臂 WhiteLevel=$wlOn（读数由 exiftool 独立给出）"
$dngFix = "$root/tests/fixtures/raw_src_8bit.dng"
$reenc  = "$d/reenc.dng"
if (Test-Path $dngFix) {
    ExecStdoutOnly $dng "-e -lossless -i `"$dngFix`" -O `"$reenc`"" | Out-Null
    $wlRe = FirstNum (TagVal 'WhiteLevel' $reenc)
    $prRe = TagVal 'DNGPrivateData' $reenc
    $orRe = TagVal 'OriginalRawFileName' $reenc      # 输入本身没有 50828 ⇒ 搬运分支必须什么也不造
    CK ((Test-Path $reenc) -and $wlRe -gt 0 -and [string]::IsNullOrWhiteSpace($prRe) `
        -and [string]::IsNullOrWhiteSpace($orRe)) `
       "⑥b DNG→DNG 不落入非 DNG 搬运分支（WhiteLevel=$wlRe 私有数据=[$prRe] 无中生有的嵌入名=[$orRe]）"
} else { CK $false "⑥b 缺 DNG 夹具 $dngFix" }

# ── ⑦ IDC 语义恢复：不止"字节在位", 还要能按索尼的链读出同一份语义 ──
# 对账面 = 去掉 {"origin":"..."} 之后的**整段** JSON。实测两臂连 sr2Dir/idcDir 两个
# 绝对偏移都相同（49300 / 49577984）⇒ 解回的原文件与源在同一地址空间里；
# 寻址或解析任何一处走样, 这段就不等 —— 比只比条目数严。
$iOn  = "$d/idc-on.dng"; $iOff = "$d/idc-off.dng"
ExecStdoutOnly $dng "-e -lossless -i `"$idcArw`" -O `"$iOn`"" | Out-Null
ExecStdoutOnly $dng "-e -lossless -no-raw-meta -i `"$idcArw`" -O `"$iOff`"" | Out-Null
$ra = ExecRC $dng "-idc `"$idcArw`""
$rb = ExecRC $dng "-idc `"$iOn`""
$mark = ',"sr2Dir":'
$ka = $ra[1].IndexOf($mark); $kb = $rb[1].IndexOf($mark)
$entSeg = $rb[1].Substring($rb[1].IndexOf('"entries":['))   # changed 面也含 "tag":" ⇒ 只能数 entries 段
$tagN = ([regex]::Matches($entSeg, '"tag":"')).Count
CK ($ra[0] -eq 0 -and $rb[0] -eq 0 -and $ka -gt 0 -and $kb -gt 0 -and
    $ra[1].Substring($ka) -ceq $rb[1].Substring($kb)) `
   "⑦a -idc 在源 RAW 与产物嵌入的原文件里读出逐字节相同的语义（origin 之后全等, 含 sr2/idc 偏移; rc=$($ra[0])/$($rb[0])）"
CK ($tagN -ge 40) "⑦b 生效版本条目数 $tagN ≥ 40（防空跑: 两臂同时读空也会让 ⑦a 相等）"
$rz = ExecRC $dng "-idc `"$iOff`""
CK ($rz[0] -ne 0) "⑦c 负控：-no-raw-meta 臂没嵌原文件 ⇒ -idc 非零退出（实测 rc=$($rz[0])）⇒ ⑦a 读到的不是别处的副本"

# ── ⑧ 非 ASCII 输入名：LibRaw 的窄路径重载把 char 逐个零扩展成 wchar_t ⇒ 中文名打不开 ──
# 名字用码点拼, 不写字面量 —— 本文件的编码不该成为一个判据的输入。
$cnBase = -join ([char]0x4E2D, [char]0x6587, [char]0x540D, [char]0x6D4B, [char]0x8BD5)
$cnArw  = Join-Path $d ($cnBase + '.ARW')
$cnDng  = Join-Path $d 'cn-name.dng'
Copy-Item -Force $idcArw $cnArw
$nonAscii = ((-join ($cnArw.ToCharArray() | Where-Object { [int]$_ -gt 127 })) -ne '')
$rc8 = ExecRC $dng "-e -lossless -i `"$cnArw`" -O `"$cnDng`""
CK ($nonAscii -and $rc8[0] -eq 0 -and (Test-Path $cnDng)) `
   "⑧a 中文名输入能转出 DNG（rc=$($rc8[0]) 产物=$(if (Test-Path $cnDng) {(Get-Item $cnDng).Length} else {0}) B; 名字含非 ASCII=$nonAscii）"
$digIdc = TagVal 'NewRawImageDigest' $iOn
$digCn  = TagVal 'NewRawImageDigest' $cnDng
CK (-not [string]::IsNullOrWhiteSpace($digIdc) -and $digIdc -eq $digCn) `
   "⑧b 中文名产物与 ASCII 名产物（同一源文件）原始像素摘要相同 digest=[$digCn]"
$rc8n = ExecRC $dng "-idc `"$cnDng`""
$kn8  = $rc8n[1].IndexOf($mark)
CK ($rc8n[0] -eq 0 -and $kn8 -gt 0 -and $rc8n[1].Substring($kn8) -ceq $rb[1].Substring($kb)) `
   "⑧c 中文名产物里嵌入的原文件同样读得出同一份 IDC 语义（rc=$($rc8n[0])）"

# ── ⑨⑩⑪ "源到底被改了什么"：changed 面由 exiftool 独立复核，日志计数被它钉住 ──
# changed = 生效版本目录 vs 初始版本目录(hop 0)的逐项差异 —— 本机实测四件 ARW 的编辑就落在
# 这一对里(V0 全是机身初值 0/1、V1 是导出后的值)，所以"改了什么"不需要任何标定就能读出来。
# ⚠ exiftool 对 0x0201/0x0202 会按 IsOffset 把"存 0"解析成各自目录的基址 ⇒ 造一条伪差异，
#    读手显式排除并在行里点名（这是它自己的口径，不是我们的差异项）。
$cr9 = ExecRC 'py' "`"$oracle`" `"$idcArw`""
$refN = -1; $mineN = -1
if ($cr9[1] -match 'REF=(\d+)\s+MINE=(\d+)') { $refN = [int]$Matches[1]; $mineN = [int]$Matches[2] }
CK ($cr9[0] -eq 0 -and $refN -eq $mineN -and $refN -ge 10) `
   "⑨ changed 面与 exiftool 的 V0/V1 差异逐 tag 同集合同数值（REF=$refN MINE=$mineN；rc=$($cr9[0])；$(($cr9[1] -split "`r?`n")[0])）"
$enc9 = ExecRC $dng "-e -lossless -i `"$idcArw`" -O `"$d/enc9.dng`""
$logN = -1
if ($enc9[2] -match 'idc-changed=(\d+)') { $logN = [int]$Matches[1] }
CK ($logN -eq $refN) "⑩ 编码器日志点名的计数 $logN == exiftool 侧差异 $refN ⇒ 那行日志不是自说自话（键 idc-changed 是 ASCII，防非 UTF-8 码页假红）"
$rvSrc = "$d/revert.ARW"; $rvDng = "$d/revert.dng"
$mk11 = ExecRC 'py' "`"$maker`" `"$idcArw`" `"$rvSrc`" 0x8016"
$cr11 = ExecRC 'py' "`"$oracle`" `"$rvSrc`" --expect-absent 0x8016"
$rvN = -1
if ($cr11[1] -match 'REF=(\d+)\s+MINE=(\d+)') { $rvN = [int]$Matches[2] }
$enc11 = ExecRC $dng "-e -lossless -i `"$rvSrc`" -O `"$rvDng`""
$lg11 = -1
if ($enc11[2] -match 'idc-changed=(\d+)') { $lg11 = [int]$Matches[1] }
$cp11 = ExecRC $dng "-idc `"$rvDng`""
$cn11 = -1
if ($cp11[1] -match '"changedCount":(\d+)') { $cn11 = [int]$Matches[1] }
CK ($mk11[0] -eq 0 -and $cr11[0] -eq 0 -and $rvN -eq ($mineN - 1) -and $lg11 -eq $rvN -and $cn11 -eq $rvN) `
   "⑪ 精度臂：抹平 0x8016 后 changed 由 $mineN 降到 $rvN、日志计数 $lg11、产物里嵌入的原文件读回 $cn11 三处同步 ⇒ 判据读的是数据不是常数"

# ── ⑫⑬⑭ 标准位落地：ProfileToneCurve 写进 IFD0 的主 profile, 由独立读手核对 ──
# 读手按规范自己解 TIFF(exiftool 对本机产物的 profile 直接 "Bad format ... parsing aborted"),
# 判据是**标签集合的差**而不是"某条标签存在" —— 同时排除"挤掉别的标签"与"多写别的标签"两个方向。
$specCurve = '0:0 0.25:0.2 0.5:0.45 1:1'
$specOk = "$d/look.txt"
Set-Content -Path $specOk -Encoding ascii -Value "curve=$specCurve"
$baseDng = "$d/look-base.dng"; $lookDng = "$d/look-on.dng"
ExecStdoutOnly $dng "-e -lossless -i `"$idcArw`" -O `"$baseDng`"" | Out-Null
$r12 = ExecRC $dng "-e -lossless -idc-look `"$specOk`" -i `"$idcArw`" -O `"$lookDng`""
$c12 = ExecRC 'py' "`"$rdp`" `"$lookDng`" --expect-curve `"$specCurve`" --against `"$baseDng`""
CK ($r12[0] -eq 0 -and $c12[0] -eq 0) `
   "⑫ -idc-look 的曲线逐点落进 IFD0 的 ProfileToneCurve 且标签差恰好只多 0xC6FC（dng rc=$($r12[0]) 读手 rc=$($c12[0])）"
$c13 = ExecRC 'py' "`"$rdp`" `"$baseDng`" --expect-no-tone"
CK ($c13[0] -eq 0) "⑬ 不加 -idc-look 的产物里不得出现 ProfileToneCurve ⇒ ⑫ 不是恒真（读手 rc=$($c13[0])）"
$badSpecs = @('curve=0:0 0.5:0.4 0.25:0.2 1:1', 'policy=0', "curve=0:0 1:1`nbogus=1")
$badWhy   = @('x 不递增', '缺 curve 行', '不认识的键')
$badFail  = @()
for ($k = 0; $k -lt $badSpecs.Count; $k++) {
    $sp = "$d/bad$k.txt"; $ob = "$d/bad$k.dng"
    Set-Content -Path $sp -Encoding ascii -Value $badSpecs[$k]
    if (Test-Path $ob) { Remove-Item $ob -Force }
    $rb = ExecRC $dng "-e -lossless -idc-look `"$sp`" -i `"$idcArw`" -O `"$ob`""
    if (-not ($rb[0] -ne 0 -and -not (Test-Path $ob))) { $badFail += $badWhy[$k] }
}
CK ($badFail.Count -eq 0) `
   "⑭ 三张坏规格一律非零退出且**不产出**（fail-closed）；漏网的: $(if ($badFail) { $badFail -join ',' } else { '无' })"

# ── ⑮ GPS 臂：源含 GPS 不得把整条保真搬运降级, 但代价必须响亮点名 ──────
# 裁法与本仓对 cjxl/jbrd 的既有裁定一致（QueueProcessor.DngCarriesRawMetadata 的注释）：
# 只有"整组描述性元数据都不要"才关搬运; 仅 GPS 时**照搬**, 由工具点名"嵌入件里带着 GPS"。
# 为什么要有这一组：2026-10-17 我一度在 dngtool 里做成"源含 GPS ⇒ 放弃整条搬运"，
# 那是拿降级当修复（三样一起丢），且与产品日志"本次仍搬运"的宣称相互矛盾 —— 这组判据把
# 正确的那一侧钉住：搬运照旧、原件逐字节、容器层无 GPS、点名有键、关掉搬运时键必须消失。
# 夹具是**合成件**（本机四件 ARW 全无 GPS，实测 -GPSLatitude 读回为空）⇒ 前置条件本身要判红。
$gpsArw = "$d/gps.ARW"; $gpsOn = "$d/gps-on.dng"; $gpsOff = "$d/gps-off.dng"
Copy-Item -Force $arw $gpsArw
$gwArgs = '-overwrite_original -GPSLatitude=35.6812 -GPSLongitude=139.7671 ' +
          '-GPSLatitudeRef=N -GPSLongitudeRef=E "{0}"' -f $gpsArw
$gw = ExecRC $ex $gwArgs
$gpsSrc = TagVal 'GPSLatitude' $gpsArw
CK ($gw[0] -eq 0 -and $gpsSrc -ne '') `
   "⑮前置 GPS 夹具注入成功（rc=$($gw[0]) 源可读回 Latitude=[$gpsSrc]）⇒ 后四条不是拿无 GPS 的源空跑"
if ($gpsSrc -eq '') { Write-Output "PASS=$pass FAIL=$($fail + 1)"; exit 1 }

$r15 = ExecRC $dng "-e -lossless -i `"$gpsArw`" -O `"$gpsOn`""
$gEmb = "$d/gps-recovered.ARW"
Start-Process -FilePath $ex -ArgumentList "-overwrite_original -b -OriginalRawImage `"$gpsOn`"" `
    -Wait -NoNewWindow -RedirectStandardOutput $gEmb | Out-Null
$gb   = FileBytes $gpsArw
$shaG = Hex ($script:sha.ComputeHash($gb))
$grb  = if (Test-Path $gEmb) { FileBytes $gEmb } else { [byte[]]@() }
$shaGr = if ($grb.Length -gt 0) { Hex ($script:sha.ComputeHash($grb)) } else { '' }
CK ($r15[0] -eq 0 -and $shaG -eq $shaGr) `
   "⑮a 源含 GPS 时搬运照旧：产物在（rc=$($r15[0])）且解回的嵌入原件与 GPS 源 SHA256 全等（$shaG）⇒ 没有一个标签能把三段搬运关掉"

$gpsVis = TagVal 'GPSLatitude' $gpsOn
$key15  = if ($r15[2] -match 'gps-in-embedded-original=1') { 1 } else { 0 }
CK ([string]::IsNullOrWhiteSpace($gpsVis) -and $key15 -eq 1) `
   "⑮b 产物可见标签无 GPS（=[$gpsVis]）而 stderr 用 ASCII 键点名了嵌入件内含 GPS（命中=$key15）⇒ 代价不静默"

$gpsInEmb = TagVal 'GPSLatitude' $gEmb
CK ($gpsInEmb -ne '' -and $gpsInEmb -eq $gpsSrc) `
   "⑮c 解回的嵌入件里 GPS 与源同值（=[$gpsInEmb] vs 源[$gpsSrc]）⇒ ⑮b 那句点名描述的是实际字节，不是模板话"

$r15n = ExecRC $dng "-e -lossless -no-raw-meta -i `"$gpsArw`" -O `"$gpsOff`""
$nmOff = TagVal 'OriginalRawFileName' $gpsOff
$key15n = if ($r15n[2] -match 'gps-in-embedded-original=1') { 1 } else { 0 }
CK ($r15n[0] -eq 0 -and [string]::IsNullOrWhiteSpace($nmOff) -and $key15n -eq 0) `
   "⑮d 负控：关掉搬运的臂既不嵌原件也不发那句点名（嵌入名=[$nmOff] 键命中=$key15n）⇒ ⑮b 的键不是恒打"

# ── ⑯ 分通道曲线的规范位：OpcodeList3(51022) 的 MapTable，每支只管一个 plane ──────
# 为什么这一组值得锁：本轮实测到 **SDK 的读者不接受空 area**。头文件与 Overlap() 的注释都说
# "空 area = 整幅"（dng_misc_opcodes.cpp:213-219），我第一版就按这句写 ⇒ dng_area_spec::GetData
# 先按 fArea.H()/W()（空矩形=0）钳制 pitch 把 1 钳成 0，再被 IsEmpty 分支判坏 ⇒ Adobe
# dng_validate 与自家 `dngtool -verify` 双双报 error 100006。修法 = 写**具体整幅矩形**。
# 读手 `_readback-dng-opcodes.py` 是第二个实现，钉四件事：解完正好落在标签 byteCount 上、
# area 非空、pitch=1、表内容与规格逐点回算相符。
$specCh3 = "$d/opc-ch3.txt"
Set-Content -Path $specCh3 -Encoding ascii -Value @('curveR=0:0 0.5:0.40 1:1',
                                                    'curveG=0:0 0.5:0.55 1:1',
                                                    'curveB=0:0 0.5:0.30 1:1')
$opcOn = "$d/opc-on.dng"; $opcNone = "$d/opc-none.dng"; $opcIdent = "$d/opc-ident.dng"
$r16  = ExecRC $dng "-e -lossless -idc-look `"$specCh3`" -i `"$idcArw`" -O `"$opcOn`""
$rd16 = ExecRC 'py' "`"$opcRd`" `"$opcOn`" --expect-spec `"$specCh3`" --expect-planes 0,1,2"
$chKey = if ($r16[2] -match 'opcode-channels=3') { 1 } else { 0 }
CK ($r16[0] -eq 0 -and $chKey -eq 1 -and $rd16[0] -eq 0) `
   "⑯a 三支非恒等表落进 51022（编码 rc=$($r16[0]) ASCII 键命中=$chKey 独立读手 rc=$($rd16[0])）；读手同时判 落边界/area 非空/pitch=1/表逐点回算"
ExecStdoutOnly $dng "-e -lossless -i `"$idcArw`" -O `"$opcNone`"" | Out-Null
$rd16b = ExecRC 'py' "`"$opcRd`" `"$opcNone`" --expect-none"
CK ($rd16b[0] -eq 0) "⑯b 反控：同输入不加 -idc-look 的产物里 51022 必须缺席（读手 rc=$($rd16b[0])）⇒ ⑯a 不是恒在场"
$specId = "$d/opc-ident.txt"
Set-Content -Path $specId -Encoding ascii -Value @('curveR=0:0 1:1', 'curveG=0:0 1:1', 'curveB=0:0 1:1')
$r16c = ExecRC $dng "-e -lossless -idc-look `"$specId`" -i `"$idcArw`" -O `"$opcIdent`""
$skipKey = if ($r16c[2] -match 'opcode-skipped=3') { 1 } else { 0 }
$rd16c = ExecRC 'py' "`"$opcRd`" `"$opcIdent`" --expect-none"
CK ($r16c[0] -eq 0 -and $skipKey -eq 1 -and $rd16c[0] -eq 0) `
   "⑯c 恒等臂：编码照成功（rc=$($r16c[0])）但按 ASCII 键点名跳过（opcode-skipped 命中=$skipKey），且产物里确实没有 51022（读手 rc=$($rd16c[0])）⇒ 『不造观感』这条有牙"
$bad16Spec = @('curve=0:0 1:1' + "`n" + 'curveR=0:0 0.5:0.4 1:1', 'curveQ=0:0 1:1')
$bad16Why  = @('curve 与 curveR 混给（读者会施加两次）', 'curveQ 是不认识的键')
$bad16Fail = @()
for ($k = 0; $k -lt $bad16Spec.Count; $k++) {
    $sp = "$d/bad16-$k.txt"; $ob = "$d/bad16-$k.dng"
    Set-Content -Path $sp -Encoding ascii -Value $bad16Spec[$k]
    if (Test-Path $ob) { Remove-Item $ob -Force }
    $rb = ExecRC $dng "-e -lossless -idc-look `"$sp`" -i `"$idcArw`" -O `"$ob`""
    if (-not ($rb[0] -ne 0 -and -not (Test-Path $ob))) { $bad16Fail += $bad16Why[$k] }
}
CK ($bad16Fail.Count -eq 0) `
   "⑯d 两张坏规格必须非零退出且**不产出**；漏网的: $(if ($bad16Fail) { $bad16Fail -join ',' } else { '无' })"

# ⑯e 重臂（DNGOPC_HEAVY=1）：像素级证明"读者真把三支表施加了"。三臂 = 基准 / 恒等 / 非恒等,
# 各用 Adobe dng_validate 渲染 stage-3 再转 rgb48 交给对账手：恒等臂必须与基准**逐字节相同**,
# 非恒等臂必须不同且等于按规格算出的 LUT（容差来路写在对账手注释里：地板 1 码、判据 2 码）。
if ($env:DNGOPC_HEAVY -eq '1') {
    $dv = "$root/tools/src/dngtool/build/Release/dng_validate.exe"
    $ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
    $abWhy = @(); $abDim = ''; $abLine = ''
    if (-not (Test-Path $dv)) { $abWhy += "缺 dng_validate: $dv" }
    if (-not (Test-Path $ff)) { $abWhy += "缺 ffmpeg: $ff" }
    if ($abWhy.Count -eq 0) {
        foreach ($it in @(@('A', $opcNone), @('B', $opcIdent), @('C', $opcOn))) {
            $tif = "$d/render-$($it[0]).tif"; $raw = "$d/render-$($it[0]).raw"
            $rv = ExecRC $dv "-3 `"$tif`" `"$($it[1])`""
            if ($rv[0] -ne 0 -or -not (Test-Path $tif)) { $abWhy += "dng_validate $($it[0]) rc=$($rv[0]) 有产物=$(Test-Path $tif)" }
            $fv = ExecRC $ff "-y -loglevel error -i `"$tif`" -pix_fmt rgb48le -f rawvideo `"$raw`""
            if ($fv[0] -ne 0) { $abWhy += "ffmpeg $($it[0]) rc=$($fv[0])" }
        }
        $abDim = (TagVal 'ImageWidth' "$d/render-A.tif") + 'x' + (TagVal 'ImageHeight' "$d/render-A.tif")
        $rdab = ExecRC 'py' "`"$abRd`" `"$d/render-A.raw`" `"$d/render-B.raw`" `"$d/render-C.raw`" $abDim `"$specCh3`""
        $abLine = ($rdab[1] -split "`r?`n" | Where-Object { $_ -match 'RENDER-AB' } | Select-Object -First 1)
        if ($rdab[0] -ne 0) { $abWhy += "对账手 rc=$($rdab[0])" }
        foreach ($f in @("$d/render-A.tif", "$d/render-B.tif", "$d/render-C.tif",
                         "$d/render-A.raw", "$d/render-B.raw", "$d/render-C.raw")) {
            if (Test-Path $f) { Remove-Item $f -Force }
        }
    }
    CK ($abWhy.Count -eq 0) ("⑯e 重臂（像素级：恒等臂逐字节==基准、非恒等臂不同且==LUT）尺寸 $abDim · " +
                             $(if ($abLine) { $abLine } else { '未取到对账读数' }) + " · " +
                             $(if ($abWhy) { ($abWhy -join ' | ') } else { '三臂渲染与清理完成' }))
} else {
    Write-Output 'SKIP ⑯e 像素级「读者真施加」重臂未跑（要 DNGOPC_HEAVY=1；默认档不判 ⇒ 记未判，不记绿）'
}

# ── ⑰ IDC 版本时间戳 → 标准 XMP 位（ApplicationNotes/700）的产品链锁 ──────────
# 这是"零换算"的那一条：IDC 的 0xD100/0xD101 存的就是 20 字符 XMP 日期形状文本，
# 所以搬运不需要标定 —— 与档位整数那族（必须标定）分开对待。
# 落点在产品侧（dngtool 写不了 XMP：本仓 SDK 以 qDNGUseXMP=0 配，dng_xmp_sdk 要 Adobe Toolkit）。
# ⇒ 这一组既锁**工具链**（读得出、写得进、读完仍是合法 DNG），也锁**产品接线**（源形锁）。
$r17 = ExecRC $dng "-idc `"$idcArw`""
$d17 = $null
if ($r17[0] -eq 0) { $d17 = ($r17[1] | ConvertFrom-Json) }
function IdcDate([object]$doc, [string]$tag) {
    if (-not $doc) { return '' }
    foreach ($e in $doc.entries) { if ($e.tag -eq $tag) { return [string]$e.text } }
    return ''
}
$reDate = '^\d{4}:\d{2}:\d{2} \d{2}:\d{2}:\d{2}$'
$cd17 = IdcDate $d17 '0xD100'; $md17 = IdcDate $d17 '0xD101'
CK ($r17[0] -eq 0 -and $cd17 -match $reDate -and $md17 -match $reDate) `
   "⑰a 源侧读得出两条版本时间戳且形状就是 XMP 日期（create=[$cd17] modify=[$md17]，rc=$($r17[0])）⇒ 零换算，不做任何数值变换"
$xTarget = "$d/xmp-chain.dng"
Copy-Item -Force $opcNone $xTarget
$w17 = ExecRC $ex ('-overwrite_original "-XMP:CreateDate=' + $cd17 + '" "-XMP:ModifyDate=' + $md17 + '" "' + $xTarget + '"')
$rdC = TagVal 'XMP:CreateDate' $xTarget
$rdM = TagVal 'XMP:ModifyDate' $xTarget
CK ($w17[0] -eq 0 -and $rdC -eq $cd17 -and $rdM -eq $md17) `
   "⑰b 按产品同法写进产物后，独立读手取回的值逐字相等（读回 create=[$rdC] modify=[$rdM]，rc=$($w17[0])）⇒ 落的是 ApplicationNotes/700 里的 xmp:CreateDate/ModifyDate"
$v17 = ExecRC $dng "-verify -i `"$xTarget`""
CK ($v17[0] -eq 0) `
   "⑰c 加了 XMP 包之后 SDK 读者仍解析通过（dngtool -verify rc=$($v17[0])）⇒ 写 700 不破坏 DNG 合规性"
$r17n = ExecRC $dng "-idc `"$arw`""
CK ($r17n[0] -ne 0) `
   "⑰d 负控：无 IDC 链的那份（7S）`-idc` 非零退出（实测 rc=$($r17n[0])）⇒ 产品这条链在'读不到'时不写，⑰a 不是在任意文件上都成立"
$qp = Get-Content -Raw "$root/src/FfmpegGui/Services/QueueProcessor.cs"
$et = Get-Content -Raw "$root/src/FfmpegGui/Services/ExifToolService.cs"
$rs = Get-Content -Raw "$root/src/FfmpegGui/Services/RawService.cs"
$shape17 = @()
if ($qp -notmatch '(?ms)if \(success && carryRawMetadata && !item\.Options\.StripExifTime\)\s*\r?\n\s*await WriteIdcVersionDatesAsync\(') {
    $shape17 += '双条件守卫（carryRawMetadata ∧ ¬StripExifTime）没有紧贴调用点' }
if (([regex]::Matches($qp, 'WriteIdcVersionDatesAsync')).Count -lt 2) { $shape17 += 'QueueProcessor 里定义+调用少于 2 处' }
if ($et -notmatch 'RawService\.IsXmpDateForm') { $shape17 += 'ExifToolService 没做操作数形状复检' }
if ($rs -notmatch 'IsXmpDateForm\(txt\)')     { $shape17 += 'RawService 解析时没按形状过滤' }
CK ($shape17.Count -eq 0) `
   "⑰e 产品接线的源形锁：$(if ($shape17) { '破坏: ' + ($shape17 -join ' | ') } else { '隐私优先的双条件、形状复检、"读不到⇒不写"三件事都还在位' })"


# ── ⑱ 镜头三表 → 规范 opcode：换算等价 + 形状锁 + 两支验牙 ──────────────────────
# 这条通道动的是**像素**，所以"标签在场"只是最弱的一层。四条臂各钉一件事：
#   ⑱a 产物里那组系数**逐结点回算**等于源 ARW 的 0x7032/0x7037/0x7035 —— 由第二个独立实现
#      `_readback-dng-lens.py` 自己解小端 TIFF 与大端 opcode 列表，不 import 写侧任何逻辑；
#      它同时钉 flags（14 必须 optional、1 与 3 必须 required）、[14] 与 [1] **相邻**（规范 p.105
#      的跳过规则只在相邻时生效，否则两遍 warp 会被施加两次）、以及 v1 支不得含奇次项
#      （PutData 只取 fData[0,2,4,6]，dng_lens_correction.cpp:1908-1911）。
#   ⑱b 反控：同一输入不加 -lens-corr ⇒ 产物里不得有镜头 opcode（⑱a 不是恒在场）
#   ⑱c 负控键：无索尼三表的输入必须点名 lens-tables=absent 且不产出镜头项
#   ⑱d 验牙：把暗角最高次项放大 20% 的副本必须被 ⑱a 那条残差判据判红（判据不是装饰）
#   ⑱e 共存：与 -idc-look 同时开时，同一列表里 6 支（MapTable×3 + 镜头×3）两份读手都要过
#   ⑱f 重臂（DNGLENS_HEAVY=1）：Adobe dng_validate 渲染"基准 vs 施加"，角/中增益比必须上升，
#      且提升倍数与**源表**角部增益相差 ≤3%（容差的来路：末结点落在角外 r≈1.033，
#      结点映射 A↔B 的本机实测差 ≤0.010 EV = 0.7%，见 docs/DNG_SONY_IDC.md §5e）
$lensOn = "$d/lens-on.dng"; $lensOff = "$d/lens-off.dng"; $lensNa = "$d/lens-nonsony.dng"
$r18   = ExecRC $dng "-e -lossless -lens-corr -i `"$idcArw`" -O `"$lensOn`""
$rd18  = ExecRC 'py' "`"$lensRd`" `"$lensOn`" `"$idcArw`""
$key18 = 0
if ($r18[2] -match 'lens-vignette=applied' -and $r18[2] -match 'lens-warp2=applied') { $key18 = 1 }
$res18 = ''
if ($rd18[1] -match '残差=([0-9.]+)') { $res18 = $Matches[1] }
CK ($r18[0] -eq 0 -and $key18 -eq 1 -and $rd18[0] -eq 0) `
   "⑱a 镜头表换算落进 51022（编码 rc=$($r18[0]) ASCII 键命中=$key18 独立读手 rc=$($rd18[0]) 暗角残差=[$res18]）：读手同时判 逐结点回算/flags/[14][1] 相邻/v1 无奇次项"
ExecStdoutOnly $dng "-e -lossless -i `"$idcArw`" -O `"$lensOff`"" | Out-Null
$rd18b = ExecRC 'py' "`"$lensRd`" `"$lensOff`" `"$idcArw`" --expect-absent"
CK ($rd18b[0] -eq 0) `
   "⑱b 反控：同输入不加 -lens-corr 的产物里镜头 opcode 必须缺席（读手 rc=$($rd18b[0])）⇒ ⑱a 不是恒在场"
$r18c = ExecRC $dng "-e -lossless -lens-corr -i `"$lensOff`" -O `"$lensNa`""
$key18c = 0
if ($r18c[2] -match 'lens-tables=absent') { $key18c = 1 }
$rd18c = ExecRC 'py' "`"$lensRd`" `"$lensNa`" `"$idcArw`" --expect-absent"
CK ($r18c[0] -eq 0 -and $key18c -eq 1 -and $rd18c[0] -eq 0) `
   "⑱c 负控：拿 DNG 当输入（没有索尼三张表）时 rc=$($r18c[0])、absent 键命中=$key18c、产物仍无镜头项 ⇒ 那条点名不是恒打"
$lensMut = "$d/lens-mutant.dng"
$mk18 = ExecRC 'py' "`"$lensRd`" `"$lensOn`" `"$idcArw`" --mutant-out `"$lensMut`""
$rd18d = ExecRC 'py' "`"$lensRd`" `"$lensMut`" `"$idcArw`""
CK ($mk18[0] -eq 0 -and (Test-Path $lensMut) -and $rd18d[0] -ne 0) `
   "⑱d 验牙：把暗角 k4 放大 20% 的副本必须判红（造件 rc=$($mk18[0]) 判据 rc=$($rd18d[0])，期望非 0）⇒ ⑱a 的残差判据真有牙"
$lensBoth = "$d/lens-both.dng"
$r18e  = ExecRC $dng "-e -lossless -idc-look `"$specCh3`" -lens-corr -i `"$idcArw`" -O `"$lensBoth`""
$rd18e1 = ExecRC 'py' "`"$lensRd`" `"$lensBoth`" `"$idcArw`""
$rd18e2 = ExecRC 'py' "`"$opcRd`" `"$lensBoth`" --expect-spec `"$specCh3`" --expect-planes 0,1,2"
$bothKey = 0
if ($r18e[2] -match 'lens-vignette=applied' -and $r18e[2] -match 'opcode-channels=3') { $bothKey = 1 }
CK ($r18e[0] -eq 0 -and $bothKey -eq 1 -and $rd18e1[0] -eq 0 -and $rd18e2[0] -eq 0) `
   "⑱e 两条通道共存（-idc-look + -lens-corr 同时开）：编码 rc=$($r18e[0]) 两把 ASCII 键命中=$bothKey，镜头读手 rc=$($rd18e1[0]) 表读手 rc=$($rd18e2[0]) ⇒ 同一列表里 6 支互不遮挡"

if ($env:DNGLENS_HEAVY -eq '1') {
    $dv = "$root/tools/src/dngtool/build/Release/dng_validate.exe"
    $ff = "$root/publish/PLAN/ffmpeg-full/ffmpeg.exe"
    $why18f = @(); $dim18f = ''
    if (-not (Test-Path $dv)) { $why18f += "缺 dng_validate: $dv" }
    if (-not (Test-Path $ff)) { $why18f += "缺 ffmpeg: $ff" }
    if ($why18f.Count -eq 0) {
        $rawOf = @{}
        foreach ($it in @(@('base', $lensOff), @('corr', $lensOn))) {
            $tif = "$d/lensr-$($it[0]).tif"; $raw = "$d/lensr-$($it[0]).raw"
            $rv = ExecRC $dv "-3 `"$tif`" `"$($it[1])`""
            if ($rv[0] -ne 0 -or -not (Test-Path $tif)) { $why18f += "dng_validate $($it[0]) rc=$($rv[0]) 有产物=$(Test-Path $tif)" }
            $fv = ExecRC $ff "-y -loglevel error -i `"$tif`" -pix_fmt rgb48le -f rawvideo `"$raw`""
            if ($fv[0] -ne 0) { $why18f += "ffmpeg $($it[0]) rc=$($fv[0])" }
            $rawOf[$it[0]] = $raw
        }
        $dim18f = (TagVal 'ImageWidth' "$d/lensr-base.tif") + 'x' + (TagVal 'ImageHeight' "$d/lensr-base.tif")
        $rd18f = ExecRC 'py' "`"$lensRd`" --render `"$($rawOf['base'])`" `"$($rawOf['corr'])`" $dim18f --source `"$idcArw`""
        $line18f = ($rd18f[1] -split "`r?`n" | Where-Object { $_ -match 'LENS-RENDER|LENS-READ FAIL' } | Select-Object -First 1)
        # 空跑守卫：这一臂必须**拿到读数本身**才算过。上一版只查 rc ⇒ 实测过一次"日志里 ⑱f 是 PASS
        # 而整轮输出里没有任何 LENS-RENDER 行"（成因未判定：读手按当时那份 argv 取位必然崩，
        # 崩了却没把臂带红）⇒ 判据改成三件事同时成立：rc=0、读数非空、读数含 "LENS-RENDER OK"，
        # 任何一件不成立都点名红。不再依赖"rc 非 0 一定会红"这一条未复现的推定。
        if ($rd18f[0] -ne 0) { $why18f += "读手 rc=$($rd18f[0]) $($line18f)" }
        if ([string]::IsNullOrWhiteSpace($line18f)) { $why18f += '读数缺席（空跑）' }
        elseif ($line18f -notmatch 'LENS-RENDER OK') { $why18f += '读数里没有 LENS-RENDER OK' }
        foreach ($f in @("$d/lensr-base.tif", "$d/lensr-corr.tif", $rawOf['base'], $rawOf['corr'])) {
            if (Test-Path $f) { Remove-Item $f -Force }
        }
        CK ($why18f.Count -eq 0) ("⑱f 重臂（像素级：Adobe 读者真把增益施加了）尺寸 $dim18f · " + $line18f +
                                   $(if ($why18f.Count) { ' | 破坏: ' + ($why18f -join ' | ') } else { '' }))
    } else {
        CK $false ('⑱f 重臂前置缺失：' + ($why18f -join ' | '))
    }
} else {
    Write-Output 'SKIP ⑱f 像素级「读者真施加」重臂未跑（要 DNGLENS_HEAVY=1；默认档不判 ⇒ 记未判，不记绿）'
}
Write-Output ''
Write-Output "PASS=$pass FAIL=$fail"
if ($fail -gt 0) { exit 1 } else { exit 0 }
