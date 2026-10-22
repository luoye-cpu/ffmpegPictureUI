# ═══════════════════════════════════════════════════════════
#  verify-dng-metadata-transport.ps1 — 第 77 条（2026-10-17 接线）
#  被测：`dngtool -e` 的**源 RAW 保真**三段（拍摄字段 / 规范 OriginalRawFileData 嵌入原文件 / 内嵌预览）。
#  ⚠ 载体必须是规范标签, 不是自造容器：DNGPrivateData 规范写明是"相机厂商给自己的 raw 转换器
#     留的私有数据", 且要求块内偏移一律相对该块起点 —— 原文件的 TIFF 偏移不满足 ⇒ 不适用。
#     （2026-10-17 把自造的 'DMSL' 块换成 50828/50829/50973, 本文件的 ② 组随之换形。）
#  ⚠ ⑮ 组锁的是"GPS 不许把整条搬运降级"：源含 GPS 时三段照搬 + **GPS 落进容器标准位** + 搬不动才点名,
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
$lensMutTool = "$root/tests/scripts/_mutate-dng-lens.py"
$origChk = "$root/tests/scripts/_check-dng-original-digest.py"
$planeCmp = "$root/tests/scripts/_cmp-raw-plane-identity.py"
$covTool  = "$root/tests/scripts/_check-exif-standard-coverage.py"
$privTool = "$root/tests/scripts/_check-dng-private-carry.py"
$badRd    = "$root/tests/scripts/_readback-dng-badpix.py"
$badMk    = "$root/tests/scripts/_make-badpix-fixture.py"
$leafSrc  = "$root/tests/output/raw_samples3/Leaf_Credo_40_7.iiq"
$badProbe = "$root/tests/scripts/_probe-badpix-realphase.py"
# ㉖ F 类（次级 IFD 静默丢位）的两件：夹具生成器 + 输入↔输出三元组对撞判据
$secMk  = "$root/tests/scripts/_make-secondary-ifd-fixture.py"
$secPr  = "$root/tests/scripts/_probe-dng-secondary-ifd-carry.py"
# ㉗ E 类（Foveon X3F）：能开 + 电平对 + 渲染一致 + 负控，全由 rawpy（独立 LibRaw 构建）给数
$x3fPr  = "$root/tests/scripts/_probe-x3f-carry.py"
# ㉘ DNG 出口的 XMP(700)：dngtool 写不了（qDNGUseXMP=0）⇒ 判产品侧那条 exiftool 后置串。
#   串由 ServiceProbe（直接调产品代码）给，本闸不手抄参数；测试宿主没构建 ⇒ 记未判，不记绿。
$xmpPr  = "$root/tests/scripts/_probe-dng-xmp-product.py"
$svcPrb = "$root/tests/projects/ServiceProbe/bin/Release/net11.0/win-x64/ServiceProbe.exe"
$iiqSrc   = "$root/tests/output/raw_samples/PhaseOne_P40.IIQ"
foreach ($p in @(@('dngtool', $dng), @('exiftool', $ex),
                 @('RAW 素材（链外，无法自备）', $arw),
                 @('带 IDC 版本栈的 RAW 素材（链外，⑦ 组唯一可用源）', $idcArw),
                 @('changed 面的独立读手', $oracle),
                 @('抹平单项改动的夹具生成器', $maker),
                 @('ProfileToneCurve 的独立读手', $rdp),
                 @('OpcodeList3 的独立读手', $opcRd),
                 @('渲染 A/B 对账手（⑯e 重组用）', $abRd),
                 @('镜头 opcode 的独立读手（⑱ 组用）', $lensRd),
                 @('镜头 opcode 的变异件生成器（⑱d 十支验牙 + ⑱h 源开关清零真素材用）', $lensMutTool),
                 @('嵌入原件的完整性校验手（②e/②f 用）', $origChk),
                 @('源与产物 sensor 平面的逐位对撞手（⑲d/⑳d 用）', $planeCmp),
                 @('标准位覆盖对撞手（⑳ 组用，exiftool 的第二个口径）', $covTool),
                 @('私有件携带/丢弃锁（㉑ 组用，厂商注记组逐条对撞）', $privTool),
                 @('坏点表的独立读手（㉒ 组用，自己走 IIQ 再自己解 OpcodeList1）', $badRd),
                 @('坏点表相位的真素材分叉探针（㉒d 用）', $badProbe),
                 @('Phase One IIQ 素材（链外，㉒ 组源）', $iiqSrc),
                 @('Leaf IIQ 素材（链外，㉒e 夹具的底件）', $leafSrc),
                 @('㉒e 夹具生成器（只改缺陷表、不动像素）', $badMk),
                 @('㉖ F 类次级 IFD 夹具生成器（自写 TIFF/DNG 布局）', $secMk),
                 @('㉖ F 类次级 IFD 搬运判据（Adobe 读者像素级对撞）', $secPr),
                 @('㉗ E 类 X3F 判据（rawpy 独立读数）', $x3fPr),
                 @('㉘ DNG 出口 XMP 的产品侧判据（走产品真实参数串）', $xmpPr))) {
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
# 2026-10-19 政策：默认**不嵌**整个原文件（303 条厂商私有项无标准位、LibRaw/rawpy 实测 0 命中）。
# 50828 这条规范载体本身还得有人看着，所以另做一件显式归档档 `-embed-raw` ⇒ ② 组判在它身上。
$onE = "$d/on-embed.dng"
ExecStdoutOnly $dng "-e -lossless -embed-raw -i `"$arw`" -O `"$onE`"" | Out-Null
ExecStdoutOnly $dng "-e -lossless -no-raw-meta -i `"$arw`" -O `"$off`"" | Out-Null
$both = (Test-Path $on) -and (Test-Path $off)
CK $both "①前置 两臂都有产物（on=$((Get-Item $on).Length) off=$((Get-Item $off).Length)）"
if (-not $both) { Write-Output "PASS=$pass FAIL=$($fail + 1)"; exit 1 }

# ── ② 规范载体 OriginalRawFileData（归档档 `-embed-raw`）：由 exiftool 自己的解码器证明逐字节还原 ──
# 为什么这条比"我方读手说一致"硬：解码方是**第二个独立实现**（exiftool DNG.pm 的
# ProcessOriginalRaw），它按规范 50828 的 8 块/大端/每 64K 一个独立 zlib 流解出来的
# 字节若与源同哈希 ⇒ 编码合规、且厂商数据一个字节都没丢；编码写错它就直接红。
$srcB = FileBytes $arw
$recFile = "$d/recovered.arw"
Start-Process -FilePath $ex -ArgumentList "-overwrite_original -b -OriginalRawImage `"$onE`"" `
    -Wait -NoNewWindow -RedirectStandardOutput $recFile | Out-Null
$recLen = if (Test-Path $recFile) { (Get-Item $recFile).Length } else { 0 }
CK ($recLen -eq $srcB.Length -and $recLen -gt 0) `
   "②a exiftool 从 50828 解出的原文件长度与源相同（源=$($srcB.Length) 解回=$recLen）"
$shaSrc = RangeSha $srcB 0 $srcB.Length
$shaRec = if ($recLen -eq $srcB.Length) { RangeSha (FileBytes $recFile) 0 $recLen } else { '' }
CK ($shaSrc -ne '' -and $shaSrc -eq $shaRec) `
   "②b 解回的原文件与源 SHA256 全等（$shaSrc）⇒ 编码符合规范、厂商注记/SR2/IDC 零丢失"
$origName = TagVal 'OriginalRawFileName' $onE
$origDig  = TagVal 'OriginalRawFileDigest' $onE
$privOn   = TagVal 'DNGPrivateData' $on
$baseName = Split-Path $arw -Leaf
CK ($origName -eq $baseName -and $origDig -ne '' -and [string]::IsNullOrWhiteSpace($privOn)) `
   "②c OriginalRawFileName=[$origName](应=$baseName) + Digest=[$origDig] 都在，且**不再用 DNGPrivateData**（=[$privOn]）"


# ②d 重编码不能把保真通道丢了：上一轮嵌进 50828 的原文件必须跟着走，
#      且仍由第三方解码器证明解回逐字节等于源。
$on2    = "$d/on-reencode.dng"
$rec2   = "$d/recovered2.arw"
ExecStdoutOnly $dng "-e -lossless -embed-raw -i `"$onE`" -O `"$on2`"" | Out-Null
Start-Process -FilePath $ex -ArgumentList "-overwrite_original -b -OriginalRawImage `"$on2`"" `
    -Wait -NoNewWindow -RedirectStandardOutput $rec2 | Out-Null
$shaRec2 = if ((Test-Path $rec2) -and (Get-Item $rec2).Length -eq $srcB.Length) { RangeSha (FileBytes $rec2) 0 $srcB.Length } else { '' }
CK ($shaSrc -ne '' -and $shaSrc -eq $shaRec2) `
   "②d DNG→DNG 重编码后 50828 仍可解回且与源 SHA256 全等（重编码件=$(if (Test-Path $on2) { (Get-Item $on2).Length } else { 0 }) B）"

# ②e 嵌入件的**完整性**：规范 p.55 定义 50973 = "This tag is an MD5 digest of the data **stored in**
#      the OriginalRawFileData tag" ⇒ 校验对象是**存储的那串字节**（含 50828 自己的压缩块序列），
#      不是解回后的原件。执行者是第二个实现 `_check-dng-original-digest.py`：它自己走 IFD0、
#      对 count>4 的条目按**偏移**取值（按内联取会读到条目区尾巴 ⇒ 本轮实测错过一次，读出 d4135b02…）。
# ②f 这条锁的牙：把载荷里一个字节翻掉（文件总长不变）⇒ 同一把尺必须判红。
#      同场登记一个实测事实（2026-10-19）：Adobe `dng_validate -v` 与自家 `dngtool -verify` 对**同一坏件**
#      都仍 rc=0 并照打 digest ⇒ 这条没人替我们查，所以必须自己钉，不能拿"官方会查"当理由。
$rc2e = ExecRC 'py' "`"$origChk`" `"$onE`""
$line2e = ($rc2e[1] -split "`r?`n" | Where-Object { $_ -match 'ORIG-DIGEST' } | Select-Object -First 1)
CK ($rc2e[0] -eq 0) `
   "②e 50828 存储字节的 MD5 与 50973 声明逐字节相等（独立校验手 rc=$($rc2e[0])）：$line2e"
$badOrig = "$d/orig-corrupt.dng"
$mk2f = ExecRC 'py' "`"$origChk`" `"$onE`" --mutant-out `"$badOrig`""
$rc2f = ExecRC 'py' "`"$origChk`" `"$badOrig`""
$dvv = "$root/tools/src/dngtool/build/Release/dng_validate.exe"
$dvWord = 'dng_validate 缺席 ⇒ 官方行为未判'
if (Test-Path $dvv) {
    $dvOut = (@(& $dvv -v $badOrig 2>&1 | Where-Object { $_ -match 'Validation complete|rror' }) | Select-Object -First 1)
    $dvWord = $(if ($dvOut) { 'Adobe dng_validate 对同一坏件说「' + ($dvOut.ToString().Trim()) + '」' } else { 'Adobe dng_validate 没报任何 Validation/Error 行' })
}
Remove-Item $badOrig -Force -ErrorAction SilentlyContinue
CK ($mk2f[0] -eq 0 -and $rc2f[0] -ne 0) `
   ("②f 牙：翻掉嵌入载荷一个字节必须判红（造件 rc=$($mk2f[0]) 校验手 rc=$($rc2f[0])，期望非 0）· $dvWord")

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
$iOn  = "$d/idc-on.dng"; $iOff = "$d/idc-off.dng"; $iOnE = "$d/idc-on-embed.dng"
$rIOn = ExecRC $dng "-e -lossless -i `"$idcArw`" -O `"$iOn`""      # 默认档（丢弃）：stderr 要交给 ⑲a
ExecStdoutOnly $dng "-e -lossless -embed-raw -i `"$idcArw`" -O `"$iOnE`"" | Out-Null
ExecStdoutOnly $dng "-e -lossless -no-raw-meta -i `"$idcArw`" -O `"$iOff`"" | Out-Null
$ra = ExecRC $dng "-idc `"$idcArw`""
$rb = ExecRC $dng "-idc `"$iOnE`""
$mark = ',"sr2Dir":'
$ka = $ra[1].IndexOf($mark); $kb = $rb[1].IndexOf($mark)
$entSeg = $rb[1].Substring($rb[1].IndexOf('"entries":['))   # changed 面也含 "tag":" ⇒ 只能数 entries 段
$tagN = ([regex]::Matches($entSeg, '"tag":"')).Count
CK ($ra[0] -eq 0 -and $rb[0] -eq 0 -and $ka -gt 0 -and $kb -gt 0 -and
    $ra[1].Substring($ka) -ceq $rb[1].Substring($kb)) `
   "⑦a -idc 在源 RAW 与**归档档**产物嵌入的原文件里读出逐字节相同的语义（origin 之后全等, 含 sr2/idc 偏移; rc=$($ra[0])/$($rb[0])）"
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
$rc8 = ExecRC $dng "-e -lossless -embed-raw -i `"$cnArw`" -O `"$cnDng`""
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
$enc11 = ExecRC $dng "-e -lossless -embed-raw -i `"$rvSrc`" -O `"$rvDng`""
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
$gpsArw = "$d/gps.ARW"; $gpsOn = "$d/gps-on.dng"; $gpsOff = "$d/gps-off.dng"; $gpsE = "$d/gps-on-embed.dng"
Copy-Item -Force $arw $gpsArw
$gwArgs = '-overwrite_original -GPSLatitude=35.6812 -GPSLongitude=139.7671 ' +
          '-GPSLatitudeRef=N -GPSLongitudeRef=E "{0}"' -f $gpsArw
$gw = ExecRC $ex $gwArgs
$gpsSrc = TagVal 'GPSLatitude' $gpsArw
CK ($gw[0] -eq 0 -and $gpsSrc -ne '') `
   "⑮前置 GPS 夹具注入成功（rc=$($gw[0]) 源可读回 Latitude=[$gpsSrc]）⇒ 后四条不是拿无 GPS 的源空跑"
if ($gpsSrc -eq '') { Write-Output "PASS=$pass FAIL=$($fail + 1)"; exit 1 }

$r15 = ExecRC $dng "-e -lossless -i `"$gpsArw`" -O `"$gpsOn`""
ExecStdoutOnly $dng "-e -lossless -embed-raw -i `"$gpsArw`" -O `"$gpsE`"" | Out-Null
$gEmb = "$d/gps-recovered.ARW"
Start-Process -FilePath $ex -ArgumentList "-overwrite_original -b -OriginalRawImage `"$gpsE`"" `
    -Wait -NoNewWindow -RedirectStandardOutput $gEmb | Out-Null
$gb   = FileBytes $gpsArw
$shaG = Hex ($script:sha.ComputeHash($gb))
$grb  = if (Test-Path $gEmb) { FileBytes $gEmb } else { [byte[]]@() }
$shaGr = if ($grb.Length -gt 0) { Hex ($script:sha.ComputeHash($grb)) } else { '' }
CK ($r15[0] -eq 0 -and $shaG -eq $shaGr) `
   "⑮a 源含 GPS 时搬运照旧：产物在（rc=$($r15[0])）且解回的嵌入原件与 GPS 源 SHA256 全等（$shaG）⇒ 没有一个标签能把三段搬运关掉"

$gpsVis = TagVal 'GPSLatitude' $gpsOn
$key15  = if ($r15[2] -match 'gps-not-carried=1') { 1 } else { 0 }
CK (-not [string]::IsNullOrWhiteSpace($gpsVis) -and $gpsVis -eq $gpsSrc -and $key15 -eq 0) `
   "⑮b 默认档：GPS 已落进容器的标准位（产物=[$gpsVis] 源=[$gpsSrc] 逐字相等）且点名键不再发（命中=$key15，应为 0）" +
   " ⇒ 2026-10-19 之前这条判据是反的（当时要求「产物无 GPS + 键必须响」，把没搬写成格式边界；" +
   "GPS 有 EXIF 标准位 ⇒ 按「能被正确解码的一律搬」改成搬成，键降级成「搬不动才响」的缺陷报警器）"

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


# ── ⑱ 镜头三表 → 规范 opcode：换算等价 + 形状锁 + 十支变异验牙 + 真素材同尺 ──────────
# 这条通道动的是**像素**，所以"标签在场"只是最弱的一层。九条臂（⑱a–⑱h + ⑱g2）各钉一件事：
#   ⑱a 产物里那组系数**逐结点回算**等于源 ARW 的 0x7032/0x7037/0x7035 —— 由第二个独立实现
#      `_readback-dng-lens.py` 自己解小端 TIFF 与大端 opcode 列表，不 import 写侧任何逻辑；
#      它同时钉 flags（14 必须 optional、1 与 3 必须 required —— required 是本通道政策而非规范强制，
#      Adobe 自带样本也是 required）、[14] 与 [1] **相邻**（规范 p.105 的跳过规则只在相邻时生效，
#      否则两遍 warp 会被施加两次）、镜头支不重复，以及 v1 的四项偶次模型能复现源表畸变
#      （v1 载荷里本就没有奇次位，radial[1/3/5…] 是读手自己填的 0 ⇒ 只有残差能识破"错抄 v2 全阶"；
#      SDK 侧 PutData 只取 fData[0,2,4,6]，dng_lens_correction.cpp:1908-1911）。
#      写侧 stderr 一并交给读手（--stderr）⇒ **点名过的跳过**合法，**静默缺席**才判红。
#   ⑱b 反控：同一输入不加 -lens-corr ⇒ 产物里不得有镜头 opcode（⑱a 不是恒在场）
#   ⑱c 负控键：无索尼三表的输入必须点名 lens-tables=absent 且不产出镜头项
#   ⑱d 验牙：十支变异**各打各的判据 + 三支反向放行** —— 造件交给 `_mutate-dng-lens.py`（造件与
#      判件分家：混在同一个入口时，判据走偏造件也跟着走偏，⑱f 的空跑就是这么溜过去的），注入
#      k4/v1val/flags14/vigopt/center/order/dup14/dropvig/dropw1/dropboth 十类缺陷，读手必须判红
#      **且红的是那一条**（只要求"非零"会被无关破坏蒙过；⑯ 组的两支变异就是按"只红自己那条"判的）；
#      摘支的三件（dropvig/dropw1/dropboth）再各配一份**点名过**的 stderr 必须判绿 ⇒ 读手"点名过的
#      缺席合法"这条新分支两个方向都有活格子，不是只写了会红的一半
#   ⑱e 共存：与 -idc-look 同时开时，同一列表里 6 支（MapTable×3 + 镜头×3）两份读手都要过
#   ⑱f 重臂（DNGLENS_HEAVY=1）：Adobe dng_validate 渲染"基准 vs 施加"，r∈[0.90,0.98) 环带的
#      三通道增益比必须 >1.02（方向）且与**源表**角部增益相差 ≤5%（量级不离谱），第三条判据是
#      "抽样里确有像素不同"防空跑。本臂**不锁幅度精确性**（那条由 ⑱a 的逐结点回算锁）；
#      最外一圈不测 —— 施加 warp 之后那是重采样的未定义边界，量的是边界填充策略不是校正幅度。
#   ⑱g 产品入口的源形锁（六处接线缺一即红）+ ⑱g2 自验牙：六处各删一次，必须只红自己那条
#   ⑱h 把**源** ARW 的机内开关标签清零造合法变体 ⇒ 写侧必须点名 switch-off 且不写那一支、读侧必须
#      用同一把尺（不点名时必须红）⇒ 两侧同尺由真写侧证明，不靠合成的点名串
$lensOn = "$d/lens-on.dng"; $lensOff = "$d/lens-off.dng"; $lensNa = "$d/lens-nonsony.dng"
$r18   = ExecRC $dng "-e -lossless -lens-corr -i `"$idcArw`" -O `"$lensOn`""
# 把写侧 stderr 落盘交给读手：它据此把"点名过的跳过"与"静默缺席"分开判（前者合法、后者判红），
# 于是读手不需要假设"三支恒在场"—— 而那正是本夹具当前恰好成立的事。
[System.IO.File]::WriteAllText("$d/lens-on.err", $r18[2])
$rd18  = ExecRC 'py' "`"$lensRd`" `"$lensOn`" `"$idcArw`" --stderr `"$d/lens-on.err`""
$key18 = 0
if ($r18[2] -match 'lens-vignette=applied' -and $r18[2] -match 'lens-warp2=applied') { $key18 = 1 }
$res18 = ''
if ($rd18[1] -match '残差=([0-9.]+)') { $res18 = $Matches[1] }
CK ($r18[0] -eq 0 -and $key18 -eq 1 -and $rd18[0] -eq 0) `
   "⑱a 镜头表换算落进 51022（编码 rc=$($r18[0]) ASCII 键命中=$key18 独立读手 rc=$($rd18[0]) 暗角残差=[$res18]）：读手同时判 逐结点回算/flags/[14][1] 相邻/支不重复/v1 偶次残差"
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
# ⑱d：十类缺陷各对应一条判据 —— 只要求"非零"的话，任何无关破坏都能冒充"这条判据有牙"。
#       所以造件用独立工具，判件必须红在**指定那一条**的点名文字上，且必须有 FAIL 行（红了不点名
#       = 崩溃/异常，同样算没验到）。
$arms18d = @(
    @('k4',       '暗角逐结点回算残差'),
    @('v1val',    'WarpRectilinear 对畸变表回算残差'),
    @('flags14',  'WarpRectilinear2 没标 optional'),
    @('vigopt',   'FixVignetteRadial 被标 optional'),
    @('center',   'warp 与暗角的光心不一致'),
    @('order',    'ID=1 不紧跟 ID=14'),
    @('dup14',    '镜头支重复'),
    @('dropvig',  '暗角支缺席且写侧没点名'),
    @('dropw1',   '却无 1.3 兜底支'),
    @('dropboth', '畸变两支 warp 全缺席且写侧没点名'))
$bad18d = @(); $dent18d = @()
foreach ($m in $arms18d) {
    $mp = "$d/lens-mutant-$($m[0]).dng"
    $mk = ExecRC 'py' "`"$lensMutTool`" `"$lensOn`" `"$mp`" $($m[0])"
    if ($mk[0] -ne 0 -or -not (Test-Path $mp)) { $bad18d += "$($m[0]) 造件失败 rc=$($mk[0])"; continue }
    $rd = ExecRC 'py' "`"$lensRd`" `"$mp`" `"$idcArw`" --stderr `"$d/lens-on.err`""
    $why = ($rd[1] -split "`r?`n" | Where-Object { $_ -match 'LENS-READ FAIL' } | Select-Object -First 1)
    if ($rd[0] -eq 0) { $bad18d += "$($m[0]) 被放过（读手 rc=0）" }
    elseif ([string]::IsNullOrWhiteSpace($why)) { $bad18d += "$($m[0]) 红了但没点名（无 FAIL 行 ⇒ 崩溃不是判据）" }
    elseif ($why.IndexOf($m[1]) -lt 0) { $bad18d += "$($m[0]) 红得不是那条：[$why]（期望含 [$($m[1])]）" }
    else { $dent18d += "$($m[0])→$($m[1])" }
    # 每件 68 MB（整幅无损 DNG）⇒ 判完立刻删，峰值只多一件
    Remove-Item $mp -Force -ErrorAction SilentlyContinue
}
# 放行臂：摘掉支的那三件，配一份**点名过**的 stderr 必须判绿 —— 否则读手新加的"点名过的缺席合法"
# 分支只有一半被跑过（会红 ≠ 该放时真放）。三件各点自己那一支的名。
$rel18d = @(
    @('dropvig',  'lens-skipped=vignette:identity（写了不改变任何像素）', '暗角: 缺席（写侧已点名跳过）'),
    @('dropw1',   'lens-skipped=warp-legacy:residual 超偶次拟合水位', 'LENS-READ OK'),
    @('dropboth', 'lens-skipped=distortion:switch-off', 'warp: 缺席（写侧已点名跳过）'))
$relOK18d = @()
foreach ($r in $rel18d) {
    $rp = "$d/lens-mutant-$($r[0]).dng"
    $mr = ExecRC 'py' "`"$lensMutTool`" `"$lensOn`" `"$rp`" $($r[0])"
    $ne = "$d/lens-named-$($r[0]).err"
    [System.IO.File]::WriteAllText($ne, "[dngtool-e] $($r[1])`n" + $r18[2])
    $rr = @(1, '', '')
    if (Test-Path $rp) { $rr = ExecRC 'py' "`"$lensRd`" `"$rp`" `"$idcArw`" --stderr `"$ne`"" }
    Remove-Item $rp, $ne -Force -ErrorAction SilentlyContinue
    if ($mr[0] -eq 0 -and $rr[0] -eq 0 -and ($rr[1] -match [regex]::Escape($r[2]))) { $relOK18d += $r[0] }
    else { $bad18d += "放行臂 $($r[0])：点名后必须判绿并含「$($r[2])」（造件 rc=$($mr[0]) 读手 rc=$($rr[0])）" }
}
$msg18d = "⑱d 验牙：十支变异各打各的判据 + 三支点名放行，打中 $(@($dent18d).Count)/10 与 $(@($relOK18d).Count)/3"
if ($bad18d.Count -eq 0) { $msg18d += ' · ' + ($dent18d -join ', ') + ' · 放行 ' + ($relOK18d -join ',') }
else { $msg18d += ' · 破坏: ' + ($bad18d -join ' | ') }
CK ($bad18d.Count -eq 0) $msg18d

# ── ⑱h 机内开关"明确关"的**真素材变体**：写侧必须点名跳过，读侧必须用同一把尺 ──────────
# 为什么 ⑱d 的放行臂不够：它喂给读手的是**合成**的点名文本，只证明读手认得那个串。这一臂把源 ARW
# 图像 SubIFD 里的开关标签（type=3/count=1，值内联）清零 ⇒ 走真写侧 ⇒ 同时锁两件事：
#   · 写侧遇"明确 0"必须**点名** lens-skipped=<项>:switch-off 且真的不写那一支（静默跳过 = 不可诊断）；
#   · 读侧遇同一条点名必须不再期望那一支/那一份 CA 因子。
# 本臂的由头是本轮修掉的一条潜在假红：源有 CA 表但开关明确为 0 时，读手原来仍按"表在 ⇒ 期望 CA"算，
# 于是对侧写对了也判红。读数上的判别特征：CA 在场时三支残差各不相同（0.000058/0.000074/0.000175），
# CA 关掉后三支恒等（0.000074/0.000074/0.000074）⇒ 不是同一把尺就露出来了。
$h18 = @(
    @('arw-off-0x7031', 'lens-skipped=vignette:switch-off', '暗角: 缺席（写侧已点名跳过）',
      '暗角支缺席且写侧没点名'),
    @('arw-off-0x7034', 'lens-skipped=chromatic-aberration:switch-off', 'ca=off', ''))
$bad18h = @(); $ok18h = @()
foreach ($h in $h18) {
    $srcH = "$d/src-$($h[0]).ARW"; $dngH = "$d/lens-switchoff-$($h[0]).dng"; $errH = "$d/lens-switchoff-$($h[0]).err"
    $mf = ExecRC 'py' "`"$lensMutTool`" `"$idcArw`" `"$srcH`" $($h[0])"
    if ($mf[0] -ne 0 -or -not (Test-Path $srcH)) { $bad18h += "$($h[0]) 造源件失败 rc=$($mf[0])"; continue }
    $encH = ExecRC $dng "-e -lossless -lens-corr -i `"$srcH`" -O `"$dngH`""
    [System.IO.File]::WriteAllText($errH, $encH[2])
    if ($encH[0] -ne 0 -or $encH[2].IndexOf($h[1]) -lt 0) {
        $bad18h += "$($h[0])：写侧 rc=$($encH[0]) 且没点名「$($h[1])」⇒ 跳过必须说得出，不许静默"
    }
    $rd1 = ExecRC 'py' "`"$lensRd`" `"$dngH`" `"$srcH`" --stderr `"$errH`""
    if ($rd1[0] -ne 0 -or $rd1[1].IndexOf($h[2]) -lt 0) {
        $bad18h += "$($h[0])：读手拿到同一份点名仍判红或没含「$($h[2])」（rc=$($rd1[0])）⇒ 两侧不同尺"
    } else {
        if ($h[3] -eq '') { $ok18h += "$($h[0])→$($h[2])" }
        else {
            $rd2 = ExecRC 'py' "`"$lensRd`" `"$dngH`" `"$srcH`""
            if ($rd2[0] -eq 0 -or $rd2[1].IndexOf($h[3]) -lt 0) {
                $bad18h += "$($h[0])：不点名时读手必须红「$($h[3])」（实得 rc=$($rd2[0])）⇒ 静默丢项没人管"
            } else { $ok18h += "$($h[0])→$($h[2]) 且静默时红" }
        }
    }
    Remove-Item $srcH, $dngH, $errH -Force -ErrorAction SilentlyContinue
}
$msg18h = "⑱h 开关清零的真素材两格：写侧点名跳过 + 读侧同一把尺（打中 $(@($ok18h).Count)/2）"
if ($bad18h.Count -eq 0) { $msg18h += ' · ' + ($ok18h -join ' | ') } else { $msg18h += ' · 破坏: ' + ($bad18h -join ' | ') }
CK ($bad18h.Count -eq 0) $msg18h
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
# ── ⑱g 产品入口的源形锁：勾选项/CLI 必须接到那条"唯一真源" ─────────────────
# 为什么值得锁：产品侧的 DNG 实参由 RawService.BuildEncodeToDngArguments 一处生成，
# `--dry-run` 预览与真实执行都调它（2026-10-07 D-1 就是这么收口的）。入口一旦只写进一侧
# （例如只在 QueueProcessor 传了参数，预览侧漏传），表现就是"预览声称的命令与实际跑的不一样"，
# 而这正是本仓最忌讳的宣称≠事实。⇒ 四处接线 + 两份词条一起钉住，缺一即红。
# ⚠ 判据抽成**纯函数**：⑱g 用它判真源码，⑱g2 用同一份判据去判六个"各删一处"的内存副本。
#   摊在脚本里的六条 if 没法喂变异件 ⇒ 这条锁过去从没被验过牙（和 ⑯/⑱d 那条规矩不一致）。
function Get-LensPlumbingBreaks([string]$rs, [string]$qp, [string]$mw, [string]$cp,
                                [string]$zh, [string]$en) {
    $b = @()
    if ($rs -notmatch '(?ms)if \(lensCorrection\)\s*\r?\n\s*args \+= " -lens-corr";') {
        $b += '唯一真源里没有 -lens-corr 的追加分支' }
    # ⚠ 只匹配"转到真源里的那两个实参"本身，不匹配后面的闭括号 —— 2026-10-20 加 -no-gps 时实参表
    #   尾巴变了（carryRawMetadata, lensCorrection, suppressGps);），把 `);` 写进锁里等于"以后每加
    #   一个参数都要来改这把锁"，改漏一次就是一片级联红（⑱g2 实测 0/6，六条全被这一条带红）。
    if (([regex]::Matches($rs, 'carryRawMetadata, lensCorrection')).Count -lt 1) {
        $b += '异步通道没把 lensCorrection 转给唯一真源' }
    if ($qp -notmatch 'lensCorrection: item\.Options\.DngLensCorrection') {
        $b += '执行侧调用点漏传' }
    if ($mw -notmatch 'lensCorrection: item\.Options\.DngLensCorrection') {
        $b += '--dry-run 预览侧调用点漏传 ⇒ 预览与实参会分叉' }
    if ($cp -notmatch 'case "dng-lens-correction"') { $b += 'CLI 无该入口' }
    if (-not ($zh.Contains('"dng.lens"') -and $zh.Contains('"dng.lens.hint"') -and
              $en.Contains('"dng.lens"') -and $en.Contains('"dng.lens.hint"'))) {
        $b += '两份词条不齐（zh/en 都要有标签与说明）' }
    # ⚠ 返回用逗号技巧且调用点**不再包 @()**：包了会把"空数组"变成一个"含空数组的 1 元数组"，
    #   于是 .Count 恒为 1、字符串比较退化成数组比较 ⇒ 这条锁会永远红、而自验牙永远"打中"（本轮实测撞见）。
    return ,$b
}
$grs = Get-Content -Raw "$root/src/FfmpegGui/Services/RawService.cs"
$gqp = Get-Content -Raw "$root/src/FfmpegGui/Services/QueueProcessor.cs"
$gmw = Get-Content -Raw "$root/src/FfmpegGui/MainWindow.xaml.cs"
$gcp = Get-Content -Raw "$root/src/FfmpegGui/CliParser.cs"
$gzh = Get-Content -Raw "$root/src/FfmpegGui/Resources/Locales/zh-CN.json"
$gen = Get-Content -Raw "$root/src/FfmpegGui/Resources/Locales/en-US.json"
$shape18 = Get-LensPlumbingBreaks $grs $gqp $gmw $gcp $gzh $gen
CK ($shape18.Count -eq 0) ("⑱g 产品入口的源形锁：" + $(if ($shape18) { '破坏: ' + ($shape18 -join ' | ') } else {
   '真源追加、异步转参、执行侧与预览侧两处调用、CLI 入口、双语词条六件事都在位' }))

# ⑱g2 自验牙：六处各删一次 ⇒ 锁必须只红对应那一条（红 0 条 = 删了也没人管；红 >1 条 = 判据糊）
$gMut18 = @(
    @('真源追加分支', 0, '(?ms)if \(lensCorrection\)\s*\r?\n\s*args \+= " -lens-corr";',
      '唯一真源里没有 -lens-corr 的追加分支'),
    @('异步转参',     0, 'carryRawMetadata, lensCorrection',
      '异步通道没把 lensCorrection 转给唯一真源'),
    @('执行侧调用点', 1, 'lensCorrection: item\.Options\.DngLensCorrection',
      '执行侧调用点漏传'),
    @('预览侧调用点', 2, 'lensCorrection: item\.Options\.DngLensCorrection',
      '--dry-run 预览侧调用点漏传 ⇒ 预览与实参会分叉'),
    @('CLI 入口',     3, 'case "dng-lens-correction"',
      'CLI 无该入口'),
    @('en 词条',      5, '"dng\.lens\.hint"',
      '两份词条不齐（zh/en 都要有标签与说明）'))
$bad18g = @(); $hit18g = @()
foreach ($g in $gMut18) {
    $txt = @($grs, $gqp, $gmw, $gcp, $gzh, $gen)
    if (([regex]::Matches($txt[$g[1]], $g[2])).Count -lt 1) {
        $bad18g += "$($g[0])：源码里找不到要删的那段（$($g[2])）⇒ 变异件造不出来，锁无法自验"; continue }
    $txt[$g[1]] = [regex]::Replace($txt[$g[1]], $g[2], '')
    $nb = Get-LensPlumbingBreaks $txt[0] $txt[1] $txt[2] $txt[3] $txt[4] $txt[5]
    if ($nb.Count -eq 1 -and $nb[0] -eq $g[3]) { $hit18g += $g[0] }
    else { $bad18g += "$($g[0])：期望只红「$($g[3])」，实得 $($nb.Count) 条 [$($nb -join ' | ')]" }
}
$msg18g2 = "⑱g2 这条源形锁自己的牙：六处各删一次，各只红对应那一条，打中 $(@($hit18g).Count)/6"
if ($bad18g.Count -eq 0) { $msg18g2 += ' · ' + ($hit18g -join ', ') } else { $msg18g2 += ' · 破坏: ' + ($bad18g -join ' | ') }
CK ($bad18g.Count -eq 0) $msg18g2

# ── ⑱x/⑱y 别家私有镜头表（2026-10-20）：Fuji RAF 三表 / Panasonic RW2 畸变 ──────────
# 语义与换算的一手来源 = darktable master（Fuji: src/common/exif.cc:1408-1495 +
# src/iop/lens.cc:2099-2133；Panasonic: src/common/exif.cc:1541-1597 +
# src/iop/lens.cc:2445-2493；同为索尼通路引用的同一份实现）。
# 判据两层：① Adobe 读者解析出对应 opcode（载体形状）；② 本仓独立读手
# `_readback-dng-lens.py` 直接从源表复算（Fuji: FujiIFD 三表；Panasonic: 0x0119 + 两点不动点），
# 归一化基数由**产物自身**的 ActiveArea/DefaultCrop* 复算，再与产物系数逐结点对撞。
# 两个负控必须零支（不加 -lens-corr；Panasonic 的机内开关 word7&0x0f != 1）。
# 像素级"读者真施加"已由 ⑱f 对同三类 opcode 证明。
$fujiRaw = "$root/tests/output/raw_samples/Fujifilm_XS1.RAF"
$panaOn  = "$root/tests/output/raw_samples/Leica_DLUX5.RWL"
$panaOff = "$root/tests/output/raw_samples/Panasonic_LX7.RW2"
$dvLens  = "$root/tools/src/dngtool/build/Release/dng_validate.exe"
if (-not (Test-Path $fujiRaw) -or -not (Test-Path $panaOn) -or -not (Test-Path $panaOff) -or -not (Test-Path $dvLens)) {
    Write-Output 'SKIP ⑱x/⑱y 别家镜头表（Fuji/Panasonic 素材或 dng_validate 不在位）⇒ 记未判，不记绿'
} else {
    $fxOn = "$d/lens-fuji-on.dng"; $fxOff = "$d/lens-fuji-off.dng"
    $rfx = ExecRC $dng "-e -lossless -lens-corr -i `"$fujiRaw`" -O `"$fxOn`""
    ExecStdoutOnly $dng "-e -lossless -i `"$fujiRaw`" -O `"$fxOff`"" | Out-Null
    $vfx   = ExecRC $dvLens "-v `"$fxOn`""
    $vfx0  = ExecRC $dvLens "-v `"$fxOff`""
    $txfx  = [string]($vfx[1]) + "`n" + [string]($vfx[2])
    $txfx0 = [string]($vfx0[1]) + "`n" + [string]($vfx0[2])
    $hitFx = [int]($txfx -match 'Opcode: FixVignetteRadial' -and $txfx -match 'Opcode: WarpRectilinear2' -and $txfx -match 'Opcode: WarpRectilinear,')
    $negFx = [int]($txfx0 -notmatch 'Opcode: FixVignetteRadial' -and $txfx0 -notmatch 'Opcode: WarpRectilinear2')
    $rbFx  = ExecRC 'py' "`"$lensRd`" `"$fxOn`" `"$fujiRaw`""
    $rbFx0 = ExecRC 'py' "`"$lensRd`" `"$fxOff`" `"$fujiRaw`" --expect-absent"
    $rbFxL = [string](($rbFx[1] -split "`r?`n" | Where-Object { $_ -match '^LENS-READ OK' } | Select-Object -First 1))
    CK ($rfx[0] -eq 0 -and $hitFx -eq 1 -and $negFx -eq 1 -and $rbFx[0] -eq 0 -and $rbFx0[0] -eq 0) `
       "⑱x Fuji RAF 三表：暗角 + warp(2/1) 三支被 Adobe 解析命中=$hitFx，负控（不加 -lens-corr）零镜头 opcode=$negFx · 独立读手逐结点复算 rc=$($rbFx[0])/反控 rc=$($rbFx0[0]) · $rbFxL · 换算一手来源=darktable"
    $pnOnD = "$d/lens-pana-on.dng"; $pnOffD = "$d/lens-pana-off.dng"
    $rpn  = ExecRC $dng "-e -lossless -lens-corr -i `"$panaOn`" -O `"$pnOnD`""
    $rpn0 = ExecRC $dng "-e -lossless -lens-corr -i `"$panaOff`" -O `"$pnOffD`""
    $vpn  = ExecRC $dvLens "-v `"$pnOnD`""
    $vpn0 = ExecRC $dvLens "-v `"$pnOffD`""
    $txpn  = [string]($vpn[1]) + "`n" + [string]($vpn[2])
    $txpn0 = [string]($vpn0[1]) + "`n" + [string]($vpn0[2])
    $hitPn = [int]($txpn -match 'Opcode: WarpRectilinear2')
    $offKey = [int]([string]($rpn0[2]) -match 'switch-off')
    $offNo  = [int]($txpn0 -notmatch 'Opcode: WarpRectilinear')
    $rbPn  = ExecRC 'py' "`"$lensRd`" `"$pnOnD`" `"$panaOn`""
    $rbPn0 = ExecRC 'py' "`"$lensRd`" `"$pnOffD`" `"$panaOff`" --expect-absent"
    $rbPnL = [string](($rbPn[1] -split "`r?`n" | Where-Object { $_ -match '^LENS-READ OK' } | Select-Object -First 1))
    CK ($rpn[0] -eq 0 -and $hitPn -eq 1 -and $rpn0[0] -eq 0 -and $offKey -eq 1 -and $offNo -eq 1 `
        -and $rbPn[0] -eq 0 -and $rbPn0[0] -eq 0) `
       "⑱y Panasonic RW2：开关 on 的件被解析出 WarpRectilinear2=$hitPn；开关 off（LX7）点名 switch-off=$offKey 且零 opcode=$offNo · 独立读手逐结点复算 rc=$($rbPn[0])/反控 rc=$($rbPn0[0]) · $rbPnL"
}

# ── ㉙ Sony 旧制式（SR2Private→SR2SubIFD）第二条镜头表路由（2026-10-22，账本 §41）──────
# 动因：Hasselblad Lunar（索尼机身、旧制式）在图像 SubIFD 里确实没有 0x7032/35/37 ⇒ 旧 exe 点名 absent，
#   而同一件的三张表**在场**，只是住在 IFD0 0xC634 → SR2Private(0x7200/0x7201/0x7221) → key-stream 解密
#   → SR2SubIFD 0x797D/0x7980/0x7982。判词「PS 能正确解码就必须搬」在这里的答案是"能"（Adobe 读者
#   认出我们由它发出的 FixVignetteRadial），所以这条不是可选项。
# 为什么沿用现成换算不算自造约定：**同一件 ILCE-7M2 两种存放同时在场**，实测 SR2SubIFD 那三份与
#   图像 SubIFD 的 0x7032/35/37 整段逐字节相等、三个开关也逐个相等 ⇒ 同一张表换了存放位。
# 判据三层：① 工具真取到（点名 lens=SR2SubIFD + 暗角 applied）；② Adobe 读者解析出该 opcode；
#   ③ 计数 nc 由**独立实现**（_probe-sony-enciphered-lens.py，与 exiftool 逐值对撞通过过）钉住，
#   不许 dngtool 自己报一个数自己比。
# 另外两条"不许静默"：机内开关声明畸变=关 ⇒ 必须点名 switch-off；CA 表在场却没发 ⇒ 必须点名原因。
# ㉙d/㉙e（2026-10-23 补）：畸变开关=0 但 CA 开关=1 的格子不是"跳过"而是**要搬**（账本 §43）——
#   CA 走 WarpRectilinear2 的「几何恒等 + 逐平面缩放」；两条负控各自对一支修前 exe，
#   系数再由独立解码器给的 CA 表逐结点对撞（不采信 dngtool 自己报的 maxres）。
$lunarRaw = "$root/tests/output/raw_samples3/HASSELBLAD_Lunar_4.arw"
$dngOld   = "$root/tests/output/results/_main/dngtool.before-sr2route.exe"    # 无 SR2 路由（HEAD 源重建，sha16 见账本）
$dngCaOld = "$root/tests/output/results/_main/dngtool.before-ca-standalone.exe" # 有 SR2、无 CA 独立发射（发布件 172b…）
$sonyPrb  = "$root/tests/scripts/_probe-sony-enciphered-lens.py"
$sonyRd   = "$root/tests/output/sony-enciph/readout.txt"
if (-not (Test-Path $lunarRaw) -or -not (Test-Path $dvLens) -or -not (Test-Path $sonyPrb)) {
    Write-Output 'SKIP ㉙ Sony 旧制式 SR2 路由（Lunar 素材 / dng_validate / 独立解码实现 有缺）⇒ 记未判，不记绿'
} else {
    $luOn  = "$d/lens-sr2-on.dng"
    $rlu   = ExecRC $dng "-e -lossless -lens-corr -i `"$lunarRaw`" -O `"$luOn`""
    $tlu   = ([string]($rlu[1]) + "`n" + [string]($rlu[2]))
    $hitRt = [int]($tlu -match 'lens=SR2SubIFD')
    $hitVg = [int]($tlu -match 'lens-vignette=applied')
    $offKey= [int]($tlu -match 'lens-skipped=distortion:switch-off')
    $caStd = [int]($tlu -match 'lens-ca=standalone')
    $caW2  = [int]($tlu -match 'lens-warp2=applied planes=3 sw=0 ca=per-plane geo=identity')
    $oldSk = [int]($tlu -match 'distortion-off-and-no-standalone-warp')   # 修好后这个点名必须消失
    $ncM   = [regex]::Match($tlu, 'lens-vignette=applied nc=(\d+)')
    $vlu   = ExecRC $dvLens "-v `"$luOn`""
    $tvl   = ([string]($vlu[1]) + "`n" + [string]($vlu[2]))
    $hitOp = [int]($tvl -match 'Opcode: FixVignetteRadial')
    $hitW2 = [int]($tvl -match 'Opcode: WarpRectilinear2')
    # ③ 第二实现给的计数：独立解码器读 SR2SubIFD 的 vig，首项就是条数
    $rpb   = ExecRC 'py' "`"$sonyPrb`" `"$lunarRaw`""
    $ncPrb = -1
    if (Test-Path $sonyRd) {
        $vl = ((Get-Content $sonyRd -Raw) -split "`r?`n" |
               Where-Object { $_ -match 'SR2SubIFD\[TIFFabs\]\|797d\|vig' } | Select-Object -First 1)
        $m2 = [regex]::Match([string]$vl, '取 \d+ 项 = (\d+)')
        if ($m2.Success) { $ncPrb = [int]$m2.Groups[1].Value }
    }
    CK ($rlu[0] -eq 0 -and $hitRt -eq 1 -and $hitVg -eq 1 -and $offKey -eq 1 -and $caStd -eq 1 -and
        $caW2 -eq 1 -and $oldSk -eq 0 -and $vlu[0] -eq 0 -and $hitOp -eq 1 -and $hitW2 -eq 1) `
       ("㉙a Sony 旧制式 SR2 路由：三表取到且暗角进标准位=$hitVg、Adobe 解析出 FixVignetteRadial=$hitOp /" +
        " WarpRectilinear2=$hitW2；畸变 switch-off=$offKey 仍点名，但 CA 不再静默地跟着丢" +
        "（lens-ca=standalone=$caStd、warp2 geo=identity=$caW2、旧『no-standalone-warp』点名已消失=$([int]($oldSk -eq 0))）")
    CK ($ncM.Success -and $ncPrb -ge 5 -and [int]$ncM.Groups[1].Value -eq $ncPrb) `
       ("㉙b 计数由第二实现钉住：dngtool 报 nc=" + $(if ($ncM.Success) { $ncM.Groups[1].Value } else { '取不到' }) +
        "，独立解码器（与 exiftool 逐值对撞通过）读 SR2SubIFD vig 首项 = $ncPrb ⇒ 不等即有一方读错位")
    # 负控①：SR2 路由之前那份 exe 过同一条判据必须**不成立**（它只会点名 absent）
    if (-not (Test-Path $dngOld)) {
        Write-Output 'SKIP ㉙c 负控（无 SR2 路由的修前 exe 不在位）⇒ 该臂记未判，不记绿'
    } else {
        $rlu0  = ExecRC $dngOld "-e -lossless -lens-corr -i `"$lunarRaw`" -O `"$d/lens-sr2-before.dng`""
        $tlu0  = ([string]($rlu0[1]) + "`n" + [string]($rlu0[2]))
        $absent0 = [int]($tlu0 -match 'lens-tables=absent')
        $noRt0   = [int](-not ($tlu0 -match 'lens=SR2SubIFD'))
        CK ($absent0 -eq 1 -and $noRt0 -eq 1) `
           ("㉙c 负控（㉙a 的绿是这条路由修出来的，不是尺子松出来的）：无 SR2 路由的 exe 同一输入必须仍点名" +
            " absent=$absent0 且不出现 SR2 路由=$noRt0")
    }
    # 负控②：CA 独立发射之前那份 exe 必须仍点名旧跳过、且产物里没有 WarpRectilinear2
    if (-not (Test-Path $dngCaOld)) {
        Write-Output 'SKIP ㉙d 负控（无 CA 独立发射的修前 exe 不在位）⇒ 该臂记未判，不记绿'
    } else {
        $rca0 = ExecRC $dngCaOld "-e -lossless -lens-corr -i `"$lunarRaw`" -O `"$d/lens-ca-before.dng`""
        $tca0 = ([string]($rca0[1]) + "`n" + [string]($rca0[2]))
        $old0 = [int]($tca0 -match 'distortion-off-and-no-standalone-warp')
        $new0 = [int]($tca0 -match 'lens-ca=standalone')
        $vca0 = ExecRC $dvLens "-v `"$d/lens-ca-before.dng`""
        $tcaV = ([string]($vca0[1]) + "`n" + [string]($vca0[2]))
        $w20  = [int]($tcaV -match 'Opcode: WarpRectilinear2')
        CK ($rca0[0] -eq 0 -and $old0 -eq 1 -and $new0 -eq 0 -and $w20 -eq 0) `
           ("㉙d 负控（㉙a 里 CA 那半边的绿是这条发射路修出来的）：CA 独立发射之前的 exe 同一输入仍点名旧跳过=$old0、" +
            " 无新点名=$([int]($new0 -eq 0))、产物无 WarpRectilinear2=$([int]($w20 -eq 0))")
    }
    # ⑤ 系数对撞：dng_validate 打出的逐平面径向系数，在每个结点上必须等于独立解码器给的 CA 表换算值。
    #   期望 s_i = 1 + ca[i]/2^21（前 nc 个 = R、后 nc 个 = B）；拟合容差 5e-5 + 打印 6 位小数 ⇒ 闸门 1e-4。
    #   副产物：绿平面必须**严格恒等**（几何不动的证据），否则这条 CA-only warp 会把几何也一起动了。
    $line_dist = ((Get-Content $sonyRd -Raw) -split "`r?`n" |
                  Where-Object { $_ -match 'SR2SubIFD\[TIFFabs\]\|7982\|dist' } | Select-Object -First 1)
    $line_ca   = ((Get-Content $sonyRd -Raw) -split "`r?`n" |
                  Where-Object { $_ -match 'SR2SubIFD\[TIFFabs\]\|7980\|ca' } | Select-Object -First 1)
    $mDist = [regex]::Match([string]$line_dist, '取 \d+ 项 = ([-\d ]+)')
    $mCa   = [regex]::Match([string]$line_ca,   '取 \d+ 项 = ([-\d ]+)')
    $ncD = 0; $caV = @()
    if ($mDist.Success) { $ncD = [int](($mDist.Groups[1].Value.Trim() -split '\s+')[0]) }
    if ($mCa.Success)   { $caV = @($mCa.Groups[1].Value.Trim() -split '\s+' | ForEach-Object { [int]$_ }) }
    $seg2 = ''
    $iW2 = $tvl.IndexOf('Opcode: WarpRectilinear2')
    if ($iW2 -ge 0) {
        $seg2 = $tvl.Substring($iW2)
        $iNext = $seg2.IndexOf('Opcode:', 10)
        if ($iNext -gt 0) { $seg2 = $seg2.Substring(0, $iNext) }
    }
    $planeCo = @{}
    foreach ($mp in [regex]::Matches($seg2, 'Plane (\d):\s*Radial params:\s*([-\d.,\s]+)')) {
        $vals = @()
        foreach ($v in ($mp.Groups[2].Value -split ',')) {
            if ($v.Trim() -ne '') { $vals += [double]::Parse($v.Trim(), [Globalization.CultureInfo]::InvariantCulture) }
        }
        $planeCo[[int]$mp.Groups[1].Value] = $vals
    }
    $coRes = $false; $coMsg = '取不到'
    if ($ncD -ge 3 -and $caV.Count -ge (2 * $ncD + 1) -and $planeCo.ContainsKey(0) -and
        $planeCo.ContainsKey(1) -and $planeCo.ContainsKey(2)) {
        $maxErr = 0.0
        for ($i = 0; $i -lt $ncD; $i++) {
            $r = ($i + 0.5) / ($ncD - 1)
            foreach ($plt in 0, 2) {
                if ($plt -eq 0) { $ci = 1 + $i } else { $ci = 1 + $ncD + $i }
                $exp = 1.0 + [double]$caV[$ci] / 2097152.0
                $co = $planeCo[$plt]; $vv = 0.0
                for ($q = 0; $q -lt $co.Count; $q++) { $vv += $co[$q] * [math]::Pow($r, $q) }
                $e = [math]::Abs($vv - $exp); if ($e -gt $maxErr) { $maxErr = $e }
            }
        }
        $g = $planeCo[1]; $gIdent = ($g.Count -gt 0 -and [math]::Abs($g[0] - 1.0) -lt 1e-9)
        for ($q = 1; $q -lt $g.Count; $q++) { if ([math]::Abs($g[$q]) -gt 1e-9) { $gIdent = $false } }
        $coRes = ($maxErr -le 1e-4 -and $gIdent)
        $coMsg = ('逐点最大偏差={0:E2}（闸 1e-4）、绿平面恒等={1}' -f $maxErr, $gIdent)
    }
    CK $coRes ("㉙e CA 系数由独立表钉住（不只钉条数）：$coMsg；期望由独立解码器的 SR2SubIFD 0x7980 表 + 1+x/2^21 算出，" +
               "不采信 dngtool 自己报的 maxres")
}

# ── ⑲ 丢弃政策锁：默认不嵌原文件，但标准位一条都不许掉 ────────────────────────
# 政策（2026-10-19 用户定）：**既不在官方规范、普通软件又完全读不到的东西 ⇒ 丢弃**，
# 不靠 50828 偷渡厂商私有件（本机实测 303 条 Sony/IDC/SR2 在可见层 0 命中、LibRaw/rawpy 也 0 命中）。
# 这条锁管四件事，缺一即红：
#   ⑲a 默认产物**确实没有** 50828/50973/OriginalRawFileName，且 stderr 点名 original-embed=off（丢弃不静默）
#   ⑲b 该留下的仍然留下：ExifIFD 拍摄字段 + 预览 + 镜头 OpcodeList3 ⇒ 关的是嵌入件，不是整条搬运
#   ⑲c 默认产物上 `-idc` 必须非零退出**且点名缺 50828** ⇒ 丢弃真发生，不是"嵌了但某个读者读不出"
#   ⑲d 图像不受影响：源与默认产物的 sensor 平面**逐位相同**（独立实现 LibRaw 量，不同点数必须为 0）
$nr19 = TagVal 'OriginalRawFileName' $iOn
$nd19 = TagVal 'OriginalRawFileDigest' $iOn
$nk19 = if ($rIOn[2] -match 'original-embed=off') { 1 } else { 0 }
CK ([string]::IsNullOrWhiteSpace($nr19) -and [string]::IsNullOrWhiteSpace($nd19) -and $nk19 -eq 1) `
   "⑲a 默认档真丢弃嵌入原件：OriginalRawFileName=[$nr19] Digest=[$nd19]（在场即红）而 stderr 点名命中=$nk19 ⇒ 代价不静默"
$miss19 = @()
foreach ($t in @('ExposureTime', 'ISO', 'DateTimeOriginal', 'FNumber')) {
    if ([string]::IsNullOrWhiteSpace((TagVal $t $iOn))) { $miss19 += $t }
}
$prev19  = TagVal 'PreviewImageLength' $iOn
$opc19   = TagVal 'OpcodeList3' $lensOn
$nameL19 = TagVal 'OriginalRawFileName' $lensOn
CK ($miss19.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace($prev19) `
    -and -not [string]::IsNullOrWhiteSpace($opc19) -and [string]::IsNullOrWhiteSpace($nameL19)) (
   "⑲b 政策只砍嵌入件、标准位一条没掉：" + $(if ($miss19) { '拍摄字段缺 [' + ($miss19 -join ',') + ']' } else { 'ExifIFD 四项在场' }) +
   " 预览长度=[$prev19] 镜头 OpcodeList3 在场=$(-not [string]::IsNullOrWhiteSpace($opc19))" +
   " 且 -lens-corr 产物也不带嵌入名=[$nameL19]")
$rc19 = ExecRC $dng "-idc `"$iOn`""
$tx19 = ([string]($rc19[1]) + ' ' + [string]($rc19[2]))
CK ($rc19[0] -ne 0 -and $tx19.IndexOf('50828') -ge 0) `
   "⑲c 默认档上 -idc 必须非零退出且点名缺 50828（实 rc=$($rc19[0])）⇒ 丢弃是真的，不是「嵌了但某个读者读不出」"
$rp19 = ExecRC 'py' "`"$planeCmp`" `"$idcArw`" `"$iOn`""
$line19 = ($rp19[1] -split "`r?`n" | Where-Object { $_ -match 'RAW-PLANE' } | Select-Object -First 1)
CK ($rp19[0] -eq 0 -and ($line19 -match 'OK')) `
   "⑲d 承重事实由独立实现量出来（LibRaw 逐像素）：rc=$($rp19[0]) · $line19 ⇒ 丢弃嵌入件不动图像"

# ── ⑳ 标准位覆盖锁：源里"有标准位、普通软件能解码"的，产物一条都不许掉 ──────────
# 规矩（2026-10-19 用户定，**取代同一轮先前的"先不补"**）：能被 PS/Camera Raw 正确解码的一律搬进
# 标准位；没办法被正确解码的（厂商私有件）一律不进产物。⑲ 管"丢弃"那一半，这组管"补全"那一半。
# ⚠ 读数一律由**第二个独立实现**给出：exiftool（按 tag ID 档 + 逐字值档）、LibRaw(rawpy) 解像素、
#   Adobe dng_validate 判合规 ⇒ 不采信 dngtool 自己打的 exif-std=N/M（那条只在 ⑳e 当反证用）。
# 本轮实测补上的四处漏项（每一处都由这把尺当场抓到，不是先想出来再验证的）：
#   ① 43 条标准位里手抄映射表只覆盖 21 条 ⇒ 改成把每条喂给 **Adobe 自己的** dng_exif::ParseTag；
#   ② GPS 整档原来根本不搬（旧 ⑮b 把"没搬"写成"格式边界"）⇒ 现已搬且逐字同值；
#   ③ SDK 的 DNG 出口对 10 条标准位**没有发射点**（分辨率三件只在 WriteTIFFWithProfile 里、
#      0x9101/0x9102/0xA000/0xA002/0xA003 无 Add 调用、Interop 整档不写）⇒ 由产品后置从源件
#      抄（值由 exiftool 自己读，我方不拼任何来自源文件内容的字符串 ⇒ 没有注入面）；
#   ④ Orientation(0x0112) 恒被 SDK 写成 1 ⇒ 实测源=6 的件转出来方向错，改走 BaseOrientation。
#   ⑤ 0x0213 YCbCrPositioning 被 SDK 无条件写成 0（EXIF 只允许 1/2），源声明的 2 被丢。
# ⚠ 还有第 ⑥ 条是**本轮自己引入又当场抓出的回归**：GPS 一旦按标准位搬进产物，产品的
#   隐私默认（`StripExifGps` 默认 true）就被打穿 —— 端到端首跑读到默认档产物里 GPS 在场。
#   修法不是回退搬运，而是"不搬必须显式请求"：dngtool 增 `-no-gps`，产品按选项传入并点名
#   gps-suppressed=1（⑳m 两臂分叉，⑳h 锁住四处接线）。
# 还有一个**我自己的实现缺陷**也记在这：初版自建 TIFF 类型尺寸表把 ttSRational 抄成 4 字节
#   ⇒ "内联还是外置"判反，产物里写进去的是源件的指针(4240)与下一条的 tag+type(0x000A9204)。
#   ⑳d 的值档第一次跑出这条红；改成只用 SDK 的 TagTypeSize()，并在 dngtool 里立了"不许自建表"的话。
if (-not (Test-Path $covTool)) {
    Write-Output "FAIL 缺 标准位覆盖对撞手: $covTool"
    Write-Output "PASS=$pass FAIL=$($fail + 1)"; exit 1
}
# 后置的 10 条标签名在闸里**另写一遍**是故意的：判据不许复用被测那份清单，否则产品把名单抄错时
# 这把尺跟着一起错（同源口径的反面）。名单是否漂移由 ⑳h 的源形锁负责，不是由"共用一份"负责。
$std7 = ('-XResolution -YResolution -ResolutionUnit -ComponentsConfiguration ' +
         '-CompressedBitsPerPixel -FlashPixVersion -ExifImageWidth -ExifImageHeight ' +
         '-InteropIndex -InteropVersion')
$stdPost = "$d/std-post.dng"
Copy-Item -Force $iOn $stdPost
$w20 = ExecRC $ex ('-overwrite_original -TagsFromFile "{0}" {1} "{2}"' -f $idcArw, $std7, $stdPost)
$c20 = ExecRC 'py' "`"$covTool`" `"$idcArw`" `"$stdPost`" --values"
$t20 = ([string]($c20[1]) + "`n" + [string]($c20[2]))
$nMiss20 = [regex]::Match($t20, '缺\(不豁免\)=(\d+)')
$nBad20  = [regex]::Match($t20, '不等=(\d+)')
$nCmp20  = [regex]::Match($t20, 'EXIF-VALUES 交集逐字对撞 比较=(\d+)')
# ⚠ 反真空：值档的"不等=0"在交集为空时也成立 ⇒ 必须同时命中一个**够大的比较数**
#   （本机三份夹具实测 47 / 43 / 50 ⇒ 下限取 30，读手口径一变就红，而不是静默放行）。
CK ($w20[0] -eq 0 -and $c20[0] -eq 0 -and $nMiss20.Success -and [int]$nMiss20.Groups[1].Value -eq 0 `
    -and $nBad20.Success -and [int]$nBad20.Groups[1].Value -eq 0 `
    -and $nCmp20.Success -and [int]$nCmp20.Groups[1].Value -ge 30) (
   "⑳a 补全后的产物：源标准位逐条在场且逐字同值（后置 rc=$($w20[0]) 尺 rc=$($c20[0]) · " +
   "缺(不豁免)=$($nMiss20.Groups[1].Value) 值不等=$($nBad20.Groups[1].Value) 比较=$($nCmp20.Groups[1].Value)（下限 30））· " +
   (($t20 -split "`r?`n" | Where-Object { $_ -match 'EXIF-COVERAGE 源标准位' } | Select-Object -First 1)))

# ⑳b 前置对照臂：没跑后置的那份必须**正好缺这 10 条** ⇒ "后置"不是空转，且缺的正是这 10 条。
$c20b = ExecRC 'py' "`"$covTool`" `"$idcArw`" `"$iOn`" --values"
$t20b = ([string]($c20b[1]) + "`n" + [string]($c20b[2]))
$miss20b = @([regex]::Matches($t20b, 'FAIL 缺 0x([0-9A-Fa-f]{4})') | ForEach-Object { $_.Groups[1].Value.ToUpper() })
$want20b = @('011A', '011B', '0128', '9101', '9102', 'A000', 'A002', 'A003', '0001', '0002')
$extra20b = @($miss20b | Where-Object { $want20b -notcontains $_ })
$less20b  = @($want20b | Where-Object { $miss20b -notcontains $_ })
CK ($c20b[0] -ne 0 -and $extra20b.Count -eq 0 -and $less20b.Count -eq 0) (
   "⑳b SDK 自己的 DNG 出口确实写不出这 10 条（未后置件 rc=$($c20b[0]) 缺=$($miss20b.Count) 条 =[$($miss20b -join ','))] " +
   "⇒ ⑳a 的后置不是空转；多缺=[$($extra20b -join ',')] 少缺=[$($less20b -join ',')]")

# ⑳c GPS：本机四件 ARW 全无 GPS ⇒ 夹具由 exiftool 注入（⑮ 已建的那份），同一把尺要求整档在场且同值。
$gpsArw20 = "$d/gps.ARW"
if (-not (Test-Path $gpsArw20)) { Copy-Item -Force $arw $gpsArw20 }
$w20c = ExecRC $ex ('-overwrite_original -GPSLatitude=35.6812 -GPSLongitude=139.7671 ' +
                    '-GPSLatitudeRef=N -GPSLongitudeRef=E -GPSAltitude=45 -GPSDateStamp="2017:07:13" "{0}"' -f $gpsArw20)
$gpsDng20 = "$d/std-gps.dng"
$r20c = ExecRC $dng "-e -lossless -i `"$gpsArw20`" -O `"$gpsDng20`""
ExecRC $ex ('-overwrite_original -TagsFromFile "{0}" {1} "{2}"' -f $gpsArw20, $std7, $gpsDng20) | Out-Null
$c20c = ExecRC 'py' "`"$covTool`" `"$gpsArw20`" `"$gpsDng20`" --values"
$t20c = ([string]($c20c[1]) + "`n" + [string]($c20c[2]))
$nG20 = [regex]::Match($t20c, 'GPS 源=(\d+)\(空/零值跳过=\d+\) 产物=\d+ 缺=(\d+)')
$nc20c = if ($r20c[2] -match 'gps-not-carried') { 1 } else { 0 }
CK ($w20c[0] -eq 0 -and $c20c[0] -eq 0 -and $nG20.Success -and [int]$nG20.Groups[1].Value -ge 5 `
    -and [int]$nG20.Groups[2].Value -eq 0 -and $nc20c -eq 0) (
   "⑳c GPS 整档搬进容器且逐字同值（夹具注入 rc=$($w20c[0]) 尺 rc=$($c20c[0]) · GPS 源=$($nG20.Groups[1].Value) 缺=$($nG20.Groups[2].Value)）" +
   " ⇒ 旧 ⑮b 把'没搬'写成格式边界的那条已改成搬成；本产物 stderr 出现 gps-not-carried=$nc20c（应为 0）")

# ⑳d 后置不伤像素、不伤合规：LibRaw 逐位 + Adobe dng_validate + 自家 -verify 三把尺同场。
$rp20 = ExecRC 'py' "`"$planeCmp`" `"$idcArw`" `"$stdPost`""
$line20 = ($rp20[1] -split "`r?`n" | Where-Object { $_ -match 'RAW-PLANE' } | Select-Object -First 1)
$v20 = ExecRC $dng "-verify -i `"$stdPost`""
$dv20 = 'Adobe dng_validate 缺席 ⇒ 官方口径未判'
$dvOk = $true
if (Test-Path $dvv) {
    $dvOut = (@(& $dvv -v $stdPost 2>&1 | Where-Object { $_ -match 'Validation complete|rror' }) | Select-Object -First 1)
    $dv20 = [string]($dvOut).ToString().Trim()
    $dvOk = ($dv20 -match 'Validation complete') -and ($dv20 -notmatch 'rror')
}
CK ($rp20[0] -eq 0 -and ($line20 -match 'OK') -and $v20[0] -eq 0 -and $dvOk) (
   "⑳d 后置是元数据级改写：平面逐位相同 rc=$($rp20[0]) · $line20 ｜ dngtool -verify rc=$($v20[0]) ｜ $dv20")

# ⑳e 负控：同一把尺量"关掉搬运的产物"必须判红 ⇒ ⑳a 的绿不是恒真。
$stdOff = "$d/std-off.dng"
ExecRC $dng "-e -lossless -no-raw-meta -i `"$idcArw`" -O `"$stdOff`"" | Out-Null
$c20e = ExecRC 'py' "`"$covTool`" `"$idcArw`" `"$stdOff`" --values"
$t20e = ([string]($c20e[1]) + "`n" + [string]($c20e[2]))
$nMiss20e = [regex]::Match($t20e, '缺\(不豁免\)=(\d+)')
CK ($c20e[0] -ne 0 -and $nMiss20e.Success -and [int]$nMiss20e.Groups[1].Value -gt 0) (
   "⑳e 牙：关掉搬运的同一把尺判红（尺 rc=$($c20e[0]) 缺=$($nMiss20e.Groups[1].Value) 条）⇒ ⑳a 的 0 不是空集也不是恒绿")

# ⑳f 分叉轴是"值"不是"标签"：往源件注入非零 FNumber/FocalLength/FocalLengthIn35mm，
# 这三条必须在产物在场且同值；不注入时它们走"按 unknown 省略"点名名单（SDK 的 0=unknown 约定）。
$lensArw20 = "$d/lensval.ARW"
Copy-Item -Force $arw $lensArw20
$w20f = ExecRC $ex ('-overwrite_original -FNumber=8.0 -FocalLength=35 -FocalLengthIn35mmFormat=50 -n "{0}"' -f $lensArw20)
$lensDng20 = "$d/std-lensval.dng"
$r20f = ExecRC $dng "-e -lossless -i `"$lensArw20`" -O `"$lensDng20`""
ExecRC $ex ('-overwrite_original -TagsFromFile "{0}" {1} "{2}"' -f $lensArw20, $std7, $lensDng20) | Out-Null
$c20f = ExecRC 'py' "`"$covTool`" `"$lensArw20`" `"$lensDng20`" --values"
$t20f = ([string]($c20f[1]) + "`n" + [string]($c20f[2]))
$g20f = [regex]::Match($t20f, '空/零值跳过=(\d+)')
$omit20 = TagVal 'ComponentsConfiguration' $stdPost   # 顺带确认后置抄进来的那条"1 2 3 0"能被读手解释
CK ($w20f[0] -eq 0 -and $c20f[0] -eq 0 -and $g20f.Success -and [int]$g20f.Groups[1].Value -eq 2) (
   "⑳f 注入非零镜头值后同一把尺判绿（注入 rc=$($w20f[0]) 尺 rc=$($c20f[0]) · 空/零值跳过=$($g20f.Groups[1].Value) 条" +
   " ⇒ 只剩 0x010E/0x9286 两条空串，注入的 FNumber=8.0/FocalLength=35/In35mm=50 已进分母）⇒ 轴是'源值是否为零'，不是'这些标签本来就不管'；" +
   " 后置件里 ComponentsConfiguration=[$omit20]")

# ⑳g 方向位：源 Orientation=6 ⇒ 产物=6（旧行为恒写 1）；未注入的默认件=1 ⇒ 两臂真的分叉。
$oriArw20 = "$d/ori.ARW"
Copy-Item -Force $arw $oriArw20
$w20g = ExecRC $ex ('-overwrite_original -Orientation=6 -n "{0}"' -f $oriArw20)
$oriDng20 = "$d/std-ori.dng"
$r20g = ExecRC $dng "-e -lossless -i `"$oriArw20`" -O `"$oriDng20`""
$oDst20 = (TagVal 'Orientation' $oriDng20) -replace '\D', ''
$oDflt20 = (TagVal 'Orientation' $iOn) -replace '\D', ''
CK ($w20g[0] -eq 0 -and $r20g[0] -eq 0 -and $oDst20 -eq '6' -and $oDflt20 -eq '1') (
   "⑳g 方向位搬运：源=[$(TagVal 'Orientation' $oriArw20)] 产物=[$(TagVal 'Orientation' $oriDng20)] " +
   "而未注入件的产物=[$(TagVal 'Orientation' $iOn)]（两臂分叉）⇒ 不再恒写 1")

# ⑳h 产品接线的源形锁：这四处一旦被改坏，上面的读数就失去"产品真这么做"的资格。
$svcCs = "$root/src/FfmpegGui/Services/ExifToolService.cs"
$qCs   = "$root/src/FfmpegGui/Services/QueueProcessor.cs"
$dtCpp = "$root/tools/src/dngtool/dngtool.cpp"
$bad20h = @()
$svcTxt = if (Test-Path $svcCs) { [IO.File]::ReadAllText($svcCs) } else { '' }
$qTxt   = if (Test-Path $qCs)   { [IO.File]::ReadAllText($qCs) }   else { '' }
$dtTxt  = if (Test-Path $dtCpp) { [IO.File]::ReadAllText($dtCpp) } else { '' }
# 名单本身不再由闸手抄第二遍（那是 ⑳a 的同源风险）—— 闸只锁**形状**：
#   五个分组数组都在、条目数不低于实测下限、每条都带组限定、隐私开关各有一道、
#   SDK 有发射点的 APEX 那一族**不许**被塞回后置（塞回来就把逐位相同的值换成连分数近似）。
$carryRegion = [regex]::Match($svcTxt, '(?s)private static readonly string\[\] DngCarryIfd0.+?DngCarryInterop = \{[^}]*\}')
if (-not $carryRegion.Success) { $bad20h += 'DngCarry* 分组数组整段没了（后置名单失去唯一载体）' }
else {
    $nm20h = @([regex]::Matches($carryRegion.Value, '"([A-Za-z0-9]+)"') | ForEach-Object { $_.Groups[1].Value })
    if ($nm20h.Count -lt 55) { $bad20h += "后置名单条目塌到 $($nm20h.Count) 条（实测 61 条，下限 55）" }
    foreach ($t in @('XResolution','YResolution','ResolutionUnit','ComponentsConfiguration',
                     'CompressedBitsPerPixel','FlashPixVersion','ExifImageWidth','ExifImageHeight',
                     'InteropIndex','InteropVersion','OwnerName','SerialNumber','SensitivityType',
                     'RecommendedExposureIndex','GainControl','Rating','UserComment','ColorSpace')) {
        if ($nm20h -notcontains $t) { $bad20h += "后置名单少了 $t" }
    }
    foreach ($t in @('ShutterSpeedValue','ApertureValue','BrightnessValue','MaxApertureValue')) {
        if ($nm20h -contains $t) { $bad20h += "APEX 件 $t 被塞回后置名单（SDK 有发射点，覆盖只会把逐位相同换成近似）" }
    }
}
if ($svcTxt -notmatch '"\<"[\s\S]{0,40}Append\(group\)') { $bad20h += 'AppendCarryNames 不再拼 <组:名 ⇒ 源侧不限定就会取错那一份（CR3 的 ModifyDate 实测差 8 小时）' }
if ($svcTxt -notmatch '!options\.StripExifGps[\s\S]{0,80}GPS:all') { $bad20h += '后置不再按 StripExifGps 排除 GPS ⇒ 隐私请求会被这次搬运装回去' }
if ($svcTxt -notmatch '!options\.StripExifTime') { $bad20h += '后置不再按 StripExifTime 排除时间位' }
if ($svcTxt -notmatch '!options\.StripExifCamera') { $bad20h += '后置不再按 StripExifCamera 排除镜头位' }
if ($svcTxt -notmatch 'BuildDngStdTagsCarryArgs')          { $bad20h += 'ExifToolService 的 Args 构造器没了（判据无从拼）' }
if ($svcTxt -notmatch '-TagsFromFile')                     { $bad20h += 'ExifToolService 改成自己拼值 ⇒ 注入面回来了' }
if ($qTxt   -notmatch 'CarryStdTagsUnwritableBySdkAsync')  { $bad20h += 'QueueProcessor 没调用后置（DNG 出口断线）' }
if ($qTxt   -notmatch 'carryRawMetadata[\s\S]{0,120}CarryStdTagsUnwritableBySdkAsync') { $bad20h += '后置没绑在搬运档上（整组剥除时会破口）' }
if ($qTxt   -notmatch 'CarryStdTagsToDngAsync\(inputPath, outputPath, item\.Options') { $bad20h += '后置没拿到 item.Options ⇒ 单项隐私开关对它不可见（会把刚剥掉的 GPS/时间/镜头位装回产物）' }
if ($qTxt   -notmatch 'SkippedNoExiftool')                 { $bad20h += 'exiftool 缺席不再与"成功"区分（跳过会被读成绿）' }
if ($dtTxt  -notmatch 'TagTypeSize')                       { $bad20h += 'dngtool 又不走 SDK 的 TagTypeSize（自建表抄错的那次会复发）' }
if ($dtTxt -match 'static const uint32 kTypeSize|kTypeSize\s*\[') { $bad20h += 'dngtool 里那张手抄类型尺寸表复活了（本轮实测把 SRational 抄成 4 字节）' }
if ($dtTxt  -notmatch 'SetBaseOrientation')                { $bad20h += 'dngtool 不再设方向位（Orientation 会退回恒 1）' }
if ($dtTxt  -notmatch 'ParseTag')                          { $bad20h += 'dngtool 不再用 Adobe 的 ParseTag 路由（回到手抄 21 条表）' }
if ($dtTxt  -notmatch 'gps-suppressed')                    { $bad20h += 'dngtool 的 -no-gps 不再点名（隐私抑制会变成静默）' }
$rawCs = "$root/src/FfmpegGui/Services/RawService.cs"
$rawTxt = if (Test-Path $rawCs) { [IO.File]::ReadAllText($rawCs) } else { '' }
if ($rawTxt -notmatch '" -no-gps"')                       { $bad20h += 'RawService 不再传 -no-gps（默认档会公开坐标）' }
if ($rawTxt -notmatch 'suppressGps')                      { $bad20h += 'RawService 的 suppressGps 形参没了（隐私请求无从表达）' }
if ($qTxt   -notmatch 'suppressGps: item\.Options\.StripExifGps') { $bad20h += '执行侧没把 StripExifGps 传给 dngtool' }
$mwCs = "$root/src/FfmpegGui/MainWindow.xaml.cs"
$mwTxt = if (Test-Path $mwCs) { [IO.File]::ReadAllText($mwCs) } else { '' }
if ($mwTxt -notmatch 'suppressGps: item\.Options\.StripExifGps') { $bad20h += '预览侧没传（--dry-run 与真实执行会分叉）' }
# 复制后那次"显式删除"必须**只此一份实现**，且两个复制变体都调它。
# 立锁原因（本轮实测）：补删原来只在 CopyMetadataSafeAsync 里，而 DNG 出口 protectColorMetadata
# 为假时走 CopyMetadataAsync ⇒ 默认档 `-f dng` 的产物里 GPS 与 93 条 MakerNotes 全在 ⇒
# `StripExifGps` 的隐私默认被打穿。"政策各写一遍"就是这个形状的漂移。
$implN = ([regex]::Matches($svcTxt, 'private static async Task<int> ApplyAbsorbStripAfterCopyAsync')).Count
$callN = ([regex]::Matches($svcTxt, 'await ApplyAbsorbStripAfterCopyAsync\(')).Count
if ($implN -ne 1)                          { $bad20h += "补删实现不是恰好 1 份（实得 $implN）" }
if ($callN -ne 2)                          { $bad20h += "两个复制变体不是都调它（调用点实得 $callN，期望 2）" }
if (([regex]::Matches($svcTxt, 'StripExifGps\) del\.Append')).Count -ne 1) { $bad20h += 'GPS 显式删除被抄了第二份（应只活在共用实现里那一处）' }
$msg20h = "⑳h 产品接线的源形锁：$(if ($bad20h) { '破坏: ' + ($bad20h -join ' | ') } else { '名单形状与条数下限、组限定、三道隐私开关、搬运档绑定、跳过≠成功、TagTypeSize、方向位、ParseTag 路由、-no-gps 四处接线、补删只此一份且两变体都调 都还在位' })"
if (-not $bad20h) { $msg20h += " · 点名 18 条必须在名单内、4 条 APEX 必须不在名单内" }
CK ($bad20h.Count -eq 0) $msg20h

# ⑳m GPS 隐私默认：工具的默认是**搬**（GPS 有标准位），产品的默认是**不写**（StripExifGps=true）。
# 这一格是本轮自己抓出来的回归：加完搬运后首跑端到端，默认档产物里 GPS 在场 ⇒ 隐私默认被这次修复打穿。
# 两臂必须分叉：同一份 GPS 源件，带 -no-gps 的产物读不回 GPS 且 stderr 点名；不带的产物 GPS 在场且不点名。
$gpsNo = "$d/gps-sup.dng"; $gpsKeep = "$d/gps-keep.dng"
$r20m1 = ExecRC $dng "-e -lossless -no-gps -i `"$gpsArw20`" -O `"$gpsNo`""
$r20m2 = ExecRC $dng "-e -lossless -i `"$gpsArw20`" -O `"$gpsKeep`""
$gNo   = TagVal 'GPSLatitude' $gpsNo
$gKeep = TagVal 'GPSLatitude' $gpsKeep
$kSup  = if ($r20m1[2] -match 'gps-suppressed=1') { 1 } else { 0 }
$kSup2 = if ($r20m2[2] -match 'gps-suppressed=1') { 1 } else { 0 }
CK ($r20m1[0] -eq 0 -and $r20m2[0] -eq 0 -and [string]::IsNullOrWhiteSpace($gNo) -and $kSup -eq 1 `
    -and -not [string]::IsNullOrWhiteSpace($gKeep) -and $kSup2 -eq 0) (
   "⑳m GPS 隐私默认两臂分叉：-no-gps 产物 GPS=[$gNo] 且点名命中=$kSup（应 1）；" +
   "默认档产物 GPS=[$gKeep] 且点名命中=$kSup2（应 0）⇒ 不搬是显式请求且有声，不是静默")

# ⑳j 被"字节布局自指"档放行的那两条（0x0201/0x0202）：不许只是放行 ⇒ 同一内容必须由产物
# 自己的结构位承载。偏移天生文件本地（照抄就是撒谎），而**长度**必须逐字相等。
$plSrc = TagByGroup 'IFD0' 'PreviewImageLength' $idcArw
$plDst = TagByGroup 'IFD0' 'PreviewImageLength' $stdPost
$psSrc = TagByGroup 'IFD0' 'PreviewImageStart'  $idcArw
$psDst = TagByGroup 'IFD0' 'PreviewImageStart'  $stdPost
CK ($plSrc -ne '' -and $plSrc -eq $plDst -and $psSrc -ne $psDst) (
   "⑳j 预览字节长度由产物自己的结构位承载：源=[$plSrc] 产物=[$plDst]（相等）而偏移源=[$psSrc] 产物=[$psDst]" +
   "（不等才对——照抄偏移会让产物指到自己文件的错误位置）")

# ⑳k 0x0213 YCbCrPositioning：本轮实测到的第 5 个真缺陷 —— SDK 对 YCbCr 预览**无条件**写这条
# （dng_preview.cpp:346 取 dng_ifd::fYCbCrPositioning，而那个字段默认 0）⇒ 产物里两处都写着
# "Unknown (0)"，而 EXIF 只允许 1/2；源件两张 JPEG 声明的是 2(Co-sited)，被静默丢掉。
$ypSrcI = TagByGroup 'IFD0'   'YCbCrPositioning' $idcArw
$ypDstI = TagByGroup 'IFD0'   'YCbCrPositioning' $stdPost
$ypDstT = TagByGroup 'SubIFD1' 'YCbCrPositioning' $stdPost
$ypNum  = @($ypDstI, $ypDstT) | ForEach-Object { ($_ -replace '\D', '') }
CK ($ypSrcI -match '2' -and $ypDstI -eq $ypSrcI -and ($ypNum -join ',') -match '^(2|1),2$') (
   "⑳k 预览定位位搬对且落在 EXIF 合法域：源 IFD0=[$ypSrcI] 产物 IFD0=[$ypDstI] 产物 SubIFD1=[$ypDstT]" +
   "（旧行为两处都写 0=EXIF 不允许的值，且源声明被丢）")

# ⑳l 后置对**每一条**都单独有牙：只把 0x011A 那一条从名单里拿掉，尺必须只报那一条缺。
$stdPost9 = "$d/std-post9.dng"
Copy-Item -Force $iOn $stdPost9
$w20l = ExecRC $ex ('-overwrite_original -TagsFromFile "{0}" {1} "{2}"' -f $idcArw,
                    ($std7 -replace '-XResolution ', ''), $stdPost9)
$c20l = ExecRC 'py' "`"$covTool`" `"$idcArw`" `"$stdPost9`" --values"
$t20l = ([string]($c20l[1]) + "`n" + [string]($c20l[2]))
$miss20l = @([regex]::Matches($t20l, 'FAIL 缺 0x([0-9A-Fa-f]{4})') | ForEach-Object { $_.Groups[1].Value.ToUpper() })
CK ($w20l[0] -eq 0 -and $c20l[0] -ne 0 -and $miss20l.Count -eq 1 -and $miss20l[0] -eq '011A') (
   "⑳l 单条抽取的牙：后置名单里少抄 -XResolution ⇒ 尺只报 011A 一条缺（实报=[$($miss20l -join ','))] rc=$($c20l[0])）" +
   " ⇒ ⑳a 的绿不是「整档都在或都不在」那种粗判")

# ── ⑳n 产品端到端（默认档）：让**产品自己**执行它那份名单，尺只读产物 ──────────────
# 为什么非加不可：⑳a 的绿建立在"闸里手抄的那 10 条名单"上 ⇒ 产品名单与它分叉（少抄/多抄/
#   抄错组）时 ⑳a 照样绿、真实产物照样在丢标准位。跨厂商跑量（14 件）撞出的两类缺口
#   —— 非 TIFF 头容器（CR3/RAF/MRW：dngtool 的遍历直接 notTiff 回退）与嵌套 IFD（RW2/RWL）——
#   在 dngtool 层面**无解**，只有产品那条后置通道能补 ⇒ 判据必须跑产品，否则等于没判。
$appExe = "$root/src/FfmpegGui/bin/Release/net11.0/win-x64/FfmpegGui.exe"
function Run-AppInto([string]$srcArg, [string]$outDir, [string]$extra) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $r = ExecRC $appExe ("--headless -i `"$srcArg`" --format dng --output `"$outDir`" " +
                         "--log-file `"$outDir/log.txt`" $extra")
    $f = @(Get-ChildItem "$outDir/*.dng" -File -ErrorAction SilentlyContinue)
    return ,@($r[0], $(if ($f.Count) { $f[0].FullName } else { '' }), $(if (Test-Path "$outDir/log.txt") { [IO.File]::ReadAllText("$outDir/log.txt") } else { '' }))
}
function Cov-Line([string]$srcP, [string]$dngP) {
    $c = ExecRC 'py' "`"$covTool`" `"$srcP`" `"$dngP`" --values"
    ,@($c[0], ([string]($c[1]) + "`n" + [string]($c[2])))
}
if (-not (Test-Path $appExe)) {
    Write-Output 'SKIP ⑳n/⑳o 产品端到端未跑（FfmpegGui.exe 未构建 ⇒ 记未判，不记绿；先 dotnet build）'
} else {
    $nApp = Run-AppInto $gpsArw20 "$d/app-default" ''
    $c20n = Cov-Line $gpsArw20 $nApp[1]
    $t20n = $c20n[1]
    $m20n = [regex]::Match($t20n, '缺\(不豁免\)=(\d+)')
    $g20n = [regex]::Match($t20n, 'GPS 源=(\d+)\(空/零值跳过=\d+\) 产物=\d+ 缺=(\d+)')
    $b20n = [regex]::Match($t20n, '不等=(\d+)')
    $p20n = [regex]::Match($t20n, '比较=(\d+)')
    $nBad20n = if ($b20n.Success) { [int]$b20n.Groups[1].Value } else { 99 }
    CK ($nApp[0] -eq 0 -and $nApp[1] -ne '' -and $c20n[0] -eq 0 -and $m20n.Success -and [int]$m20n.Groups[1].Value -eq 0 `
        -and $g20n.Success -and [int]$g20n.Groups[1].Value -ge 5 -and [int]$g20n.Groups[2].Value -eq 0 `
        -and $nBad20n -eq 0 -and $p20n.Success -and [int]$p20n.Groups[1].Value -ge 30 `
        -and -not [string]::IsNullOrWhiteSpace((TagVal 'XResolution' $nApp[1])) `
        -and -not [string]::IsNullOrWhiteSpace((TagVal 'InteropIndex' $nApp[1]))) (
       "⑳n 产品端到端（默认档 + 含 GPS 的夹具）：缺(不豁免)=$($m20n.Groups[1].Value) GPS 源=$($g20n.Groups[1].Value) 缺=$($g20n.Groups[2].Value) " +
       "值不等=$nBad20n 比较=$($p20n.Groups[1].Value)（下限 30）· 后置确实执行：0x011A=[$(TagVal 'XResolution' $nApp[1])] 与 Interop 0x0001=[$(TagVal 'InteropIndex' $nApp[1])] 都在场（这两条 SDK 没有发射点、只有后置能写 ⇒ 在场就是后置跑过的证据，不读日志串）· app rc=$($nApp[0]) 尺 rc=$($c20n[0]) ⇒ 名单漂移当场会红")

    # ⑳o 产品在 --strip-gps 下：GPS 一条不许回来，而**其余标准位照抄**（两轴分开才证明不是"整个搬运关掉"）。
    $oApp = Run-AppInto $gpsArw20 "$d/app-stripgps" '--strip-gps'
    $c20o = Cov-Line $gpsArw20 $oApp[1]
    $t20o = $c20o[1]
    $m20o = [regex]::Match($t20o, '缺\(不豁免\)=(\d+)')
    $g20o = [regex]::Match($t20o, 'GPS 源=(\d+)\(空/零值跳过=\d+\) 产物=(\d+) 缺=(\d+)')
    $srcG20o = if ($g20o.Success) { [int]$g20o.Groups[1].Value } else { 0 }
    $dstG20o = if ($g20o.Success) { [int]$g20o.Groups[2].Value } else { -1 }
    CK ($oApp[0] -eq 0 -and $oApp[1] -ne '' -and $m20o.Success -and [int]$m20o.Groups[1].Value -eq 0 `
        -and $srcG20o -ge 5 -and $dstG20o -eq 0 `
        -and -not [string]::IsNullOrWhiteSpace((TagVal 'XResolution' $oApp[1])) `
        -and -not [string]::IsNullOrWhiteSpace((TagVal 'InteropIndex' $oApp[1]))) (
       "⑳o 产品 + --strip-gps：GPS 源=$srcG20o 条而产物=[$dstG20o] 条（0 才对），同时非 GPS 的标准位缺=$($m20o.Groups[1].Value) 且" +
       " 0x011A=[$(TagVal 'XResolution' $oApp[1])] / Interop 0x0001=[$(TagVal 'InteropIndex' $oApp[1])] 仍在场" +
       " ⇒ 隐私请求没被打穿，也不是靠关掉整条搬运换来的（两轴分开量、都不读日志）· app rc=$($oApp[0])")

    # ⑳p 跨厂商（**只在素材在位时启用**；素材未受版本控制 ⇒ 缺席点名 SKIP，不给绿灯）。
    #    只取三个类各一件：非 TIFF 头（CR3 与 RAF 的容器结构不同，各一件）、大端 TIFF（PEF/ORF/SRW）。
    $vs = "$root/tests/output/raw_samples"
    $vSel = @()
    foreach ($pat in @('*.cr3', '*.raf', '*.pef', '*.orf')) {
        $one = @(Get-ChildItem $pat -Path $vs -File -ErrorAction SilentlyContinue | Sort-Object Length | Select-Object -First 1)
        if ($one.Count) { $vSel += $one[0] }
    }
    if ($vSel.Count -eq 0) {
        Write-Output 'SKIP ⑳p 跨厂商端到端未跑（tests/output/raw_samples/ 无 CR3/RAF/PEF/ORF 素材 ⇒ 记未判，不记绿）'
    } else {
        $vBad = @(); $vRead = @()
        foreach ($f in $vSel) {
            $od = "$d/vend-$($f.BaseName)"
            $a = Run-AppInto $f.FullName $od ''
            $c = Cov-Line $f.FullName $a[1]
            $mm = [regex]::Match($c[1], '缺\(不豁免\)=(\d+)')
            $pc = [regex]::Match($c[1], '比较=(\d+)')
            $okOne = ($a[0] -eq 0 -and $a[1] -ne '' -and $mm.Success -and [int]$mm.Groups[1].Value -eq 0 `
                      -and $pc.Success -and [int]$pc.Groups[1].Value -ge 15)
            $vRead += "$($f.BaseName) 缺=$($(if ($mm.Success) { $mm.Groups[1].Value } else { '?' }))/比=$($(if ($pc.Success) { $pc.Groups[1].Value } else { '?' }))/rc=$($a[0]),$($c[0])"
            if (-not $okOne) {
                $vBad += "$($f.BaseName): " + (($c[1] -split "`r?`n" | Where-Object { $_ -match 'FAIL 缺|EXIF-COVERAGE 源标准位' } | Select-Object -First 4) -join ' ⏎ ')
            }
        }
        CK ($vBad.Count -eq 0) ("⑳p 跨厂商端到端（非 TIFF 头 + 大端各一件，共 $($vSel.Count) 件）标准位一条不掉：" +
            ($vRead -join ' | ') + $(if ($vBad) { ' ⇒ ' + ($vBad -join ' || ') } else { ' ⇒ CR3/RAF 这类 dngtool 读不出 EXIF 的容器，由产品后置补齐' }))
    }
}

# ── ㉑ 私有件与合规的在册锁（重臂，DNGPRIV_HEAVY=1；素材不在位 ⇒ 点名 SKIP）───────
# 全部跑**产品链路**：实测 dngtool-only 的 Canon 产物里 Canon 组条目 = 0，而产品 = 267
#   ⇒ 厂商注记是**复制阶段**带进来的，拿 dngtool 产物判这件事等于没判。
#   ㉑a Adobe 官方校验器逐厂商：不合格者必须**只在点名表里**才放行 ⇒ 新增一个就红。
#       2026-10-20 起 Nikon Coolscan 不再是"点名放行的 rc=106"：源件是 Nikon Scan 的
#       3 样点 RGB TIFF（非 CFA、cam_xyz 全零 ⇒ 没有 ColorMatrix1），旧行为会写出
#       3 平面 LinearRaw 让 dng_validate 判 106（「Missing or invalid SamplesPerPixel」）。
#       现在 dngtool 在写件前按规范 p.33「ColorMatrix1 对所有非单色 DNG 必需」拦住
#       （exit 9 + refuse-linearraw-without-colormatrix）⇒ 本组断言"拒绝出件、无产物、点名"。
#   ㉑b 私有件携带锁：带过来的厂商注记组必须**同名键同值、组内不缺**；整组没到的要么是
#       工具自己回话"没有可写目标"的格式边界（点名条数），要么就是新增丢失 ⇒ 红。
$dvExe = "$root/tools/src/dngtool/build/Release/dng_validate.exe"
$vs = "$root/tests/output/raw_samples"
if ($env:DNGPRIV_HEAVY -ne '1') {
    Write-Output 'SKIP ㉑ 私有件与合规重臂未跑（要 DNGPRIV_HEAVY=1 ⇒ 记未判，不记绿）'
} else {
    $pext = @('*.cr2', '*.cr3', '*.arw', '*.nef', '*.orf', '*.rw2', '*.raf', '*.pef')
    $pSel = @()
    foreach ($pat in $pext) {
        $one = @(Get-ChildItem $pat -Path $vs -File -ErrorAction SilentlyContinue | Sort-Object Length | Select-Object -First 1)
        if ($one.Count) { $pSel += $one[0] }
    }
    if ($pSel.Count -lt 6 -or -not (Test-Path $dvExe) -or -not (Test-Path $appExe)) {
        Write-Output ("SKIP ㉑ 素材/工具不足（件数=$($pSel.Count) dv=$(Test-Path $dvExe) app=$(Test-Path $appExe)）⇒ 记未判，不记绿")
    } else {
        $badQ = @(); $readQ = @(); $warnQ = 0; $measQ = @()
        foreach ($f in $pSel) {
            $od = "$d/priv-$($f.BaseName)"
            $a = Run-AppInto $f.FullName $od ''
            if ($f.Name -eq 'Nikon_CoolscanV.NEF') {
                # 2026-10-20：这条从"点名放行的 rc=106"改成**必须拒绝出件**。
                # 源件是 Nikon Scan 的 3 样点 RGB TIFF（非 CFA、无 ColorMatrix1），
                # 旧行为会写出 3 平面 LinearRaw 让 Adobe dng_validate 直接判 106。
                # 现在 dngtool 在写件前按规范 p.33 拦住（exit 9）⇒ 断言三件事：
                # 非零 rc + 没有产物 + 日志点名 refuse-linearraw-without-colormatrix。
                $ref = ($a[0] -ne 0 -and $a[1] -eq '' -and $a[2] -match 'refuse-linearraw-without-colormatrix')
                if (-not $ref) { $badQ += ("$($f.BaseName) 期望拒绝出件，实得 rc=$($a[0]) 产物=$([bool]$a[1]) 点名=$([bool]($a[2] -match 'refuse-linearraw'))") }
                $readQ += "$($f.BaseName) refuse=$([int]$ref)"
                continue
            }
            if ($a[0] -ne 0 -or $a[1] -eq '') { $badQ += "$($f.BaseName) 产品未产出 rc=$($a[0])"; continue }
            $rv = ExecRC $dvExe "-3 `"$od/r.tif`" `"$($a[1])`""
            $dvOk = ($rv[0] -eq 0)
            # ㉑b 的到达数**不写常数**（账本 §34：旧文案里那句"93/198"是硬编码字符串）：
            # 交给 _check-dng-private-carry.py 现场量，量的是**产品实际发出的那条命令形状**
            # （ExifToolService.cs:510-516 逐字顺序），基线用同一件源的 dngtool 直出件（未跑后置）⇒
            # 同一把尺在基线上必须报"缺=全部"（负控），两个读数都必须在场才算测到。
            $tOnly = "$d/priv-$($f.BaseName)-toolonly.dng"
            $rt = ExecRC $dng "-e -lossless -i `"$($f.FullName)`" -O `"$tOnly`""
            $cv = ExecRC 'py' "`"$privTool`" `"$($f.FullName)`" `"$($a[1])`" `"$tOnly`""
            $tx = ([string]($cv[1]) + "`n" + [string]($cv[2]))
            if ($tx -match 'WARN 可修缺口') {
                $warnQ++
                $measQ += (($tx -split "`r?`n" | Where-Object { $_ -match '现场测量' } | ForEach-Object { $_.Trim() }) -join ' ⏎ ')
                # 牙：WARN 必须带**两个**现场读数（未后置侧 + 产品命令侧），否则就是常数/空跑
                if ($tx -notmatch 'measured=\d+/\d+（缺=\d+）· 产品命令形状后置侧 measured=\d+/\d+' -or
                    $tx -notmatch '基线=dngtool 直出件\(未后置\)') {
                    $badQ += "$($f.BaseName) 可修缺口的到达数不是现场读数（measured 对或基线形状缺失；dngtool 直出 rc=$($rt[0])）"
                }
            }
            if (-not $dvOk -and $f.Name -ne 'Nikon_CoolscanV.NEF') { $badQ += "$($f.BaseName) dng_validate rc=$($rv[0])（不在点名表里）" }
            if ($cv[0] -ne 0) { $badQ += "$($f.BaseName) 私有件锁 rc=$($cv[0]) :: " + (($tx -split "`r?`n" | Where-Object { $_ -match 'FAIL' } | Select-Object -First 2) -join ' ⏎ ') }
            $readQ += "$($f.BaseName) dv=$($rv[0])/priv=$($cv[0])"
        }
        CK ($badQ.Count -eq 0 -and $warnQ -ge 1) ("㉑ 私有件与合规（$($pSel.Count) 件，产品链路）：$($readQ -join ' | ') " +
            "· 已点名的可修缺口 WARN=$warnQ 条，到达数=现场测量（不是常数）：$($measQ -join ' ⏎ ') " +
            "· Nikon_CoolscanV 已按规范 p.33 拒绝出件（3 平面非 CFA 无 ColorMatrix1 ⇒ 不再产出 Adobe 判 106 的件）" +
            $(if ($badQ) { ' ⇒ ' + ($badQ -join ' || ') } else { '' }))
    }
}

# ── ㉒ 源声明的传感器坏点表 → 规范 OpcodeList1(51008)（2026-10-21）───────────────
# 政策依据是用户那条"如果 PS 能正确识别就搬"：本机 ACR（`Camera Raw.8bi` 235 MB）的实现里带
# `FixBadPixelsList` 这条 opcode 的反序列化构造串（字节层扫出，与 FixVignetteRadial/WarpRectilinear2
# 同表）；Adobe 参考读者 dng_validate 解析并**真施加**（账本 §26/§27）。与 -lens-corr 的区别：
# 镜头表是风格选择（默认关），这张表是"本次拍摄的传感器内容"——不写就等于让机身会藏住的点在 DNG 里显形。
# 四条臂：内容逐条来自源 / 不认的类型不许偷带 / Adobe 认这条 opcode / 非 IIQ 源不许写。
$bpOn = "$d/badpix-p40.dng"; $bpFuji = "$d/badpix-fuji.dng"
$rbP = ExecRC $dng "-e -lossless -v -i `"$iiqSrc`" -O `"$bpOn`""
$rbF = ExecRC $dng "-e -lossless -i `"$fujiRaw`" -O `"$bpFuji`""
$rdP = ExecRC 'py' "`"$badRd`" `"$bpOn`" `"$iiqSrc`""
$rdF = ExecRC 'py' "`"$badRd`" `"$bpFuji`" `"$fujiRaw`" --expect-absent"
$lineP = (([string]($rdP[1]) -split "`r?`n" | Where-Object { $_ -match '^BADPIX-READ' } | Select-Object -First 2) -join ' ⏎ ')
CK ($rbP[0] -eq 0 -and $rdP[0] -eq 0 -and ($rbP[2] -match 'badpix-list=applied') -and ($rbP[2] -match 'badpix-unknown-type=2')) `
   ("㉒a IIQ SensorDefects 逐条进标准位：" + $lineP +
    " · 写侧点名 applied + 不认类型=2（本机 P40 的 type=138 两条不猜语义、一条不写）")
$vp  = ExecRC $dvLens "-v `"$bpOn`""
$txv = ([string]($vp[1]) + "`n" + [string]($vp[2]))
CK ($vp[0] -eq 0 -and $txv -match 'Opcode: FixBadPixelsList' -and $txv -match 'Bayer Phase' `
    -and $txv -match 'Validation complete') `
   ("㉒b Adobe 读者认这条 opcode 并读到位：dng_validate -v 打印 'Opcode: FixBadPixelsList' + 'Bayer Phase'" +
    " 且 Validation complete（rc=$($vp[0])）⇒ 不是只有我们能解")
CK ($rbF[0] -eq 0 -and $rdF[0] -eq 0) `
   "㉒c 负控（不是逢件就写）：Fuji RAF 产物里没有 FixBadPixelsList（读手 rc=$($rdF[0])）⇒ 这条通道只搬源真声明的内容"

# ㉒f DNG→DNG 再编码不得丢掉 opcode 列表（2026-10-21 实测的缺陷：以前 51008/51022 全静默消失，
#      即 ⑯ 曲线 / ⑱ 镜头校正 / ㉒ 坏点表在"再编码一次"之后就没了）。判三件事：
#      ①日志点名 preserved ②列表字节数与支数**不变**（防重复追加）③独立读手在再编码件上仍判 OK。
$bpRe = "$d/badpix-p40-reenc.dng"
$rbR = ExecRC $dng "-e -lossless -v -i `"$bpOn`" -O `"$bpRe`""
$rdR = ExecRC 'py' "`"$badRd`" `"$bpRe`" `"$iiqSrc`""
# 条数从**独立读手的读数**里取（不用 exiftool 打 23 KB 的标签值）：两边 pCount/rCount 相等才算"没丢也没重复"
$pcA = [regex]::Match([string]($rdP[1]), 'pCount=(\d+) rCount=(\d+)')
$pcB = [regex]::Match([string]($rdR[1]), 'pCount=(\d+) rCount=(\d+)')
$keep = ($rbR[2] -match 'dng-opcode-preserved=OpcodeList1\(51008\) bytes=23056 支数=1')
$sameA = ($pcA.Success -and $pcB.Success -and $pcA.Value -eq $pcB.Value)
CK ($rbR[0] -eq 0 -and $rdR[0] -eq 0 -and $keep -and $sameA) `
   "㉒f DNG→DNG 不丢也不重复：读手读数 源=[$($pcA.Value)] 再编码后=[$($pcB.Value)]（相等才对）· 点名 preserved=$([int]$keep) · 读手 rc=$($rdR[0])"
if ($env:DNGBADPIX_HEAVY -eq '1') {
    $rp = ExecRC 'py' "`"$badProbe`" `"$bpOn`" `"$iiqSrc`" `"$dvLens`" `"$d/badpix-real`""
    $rl = (([string]($rp[1]) + "`n" + [string]($rp[2])) -split "`r?`n" |
           Where-Object { $_ -match '^BADPIX-REAL' } | Select-Object -Last 3) -join ' ⏎ '
    CK ($rp[0] -eq 0) ("㉒d 真素材上的相位分叉（重臂）：" + $rl)

    # ㉒e 真素材**没有**的分支：坏列(rect) 与"越界坐标必须丢"。本机三件 IIQ 里没有一条 type=131/137，
    # 这两支在真件上永远走不到 ⇒ 用只改缺陷表、不动像素的 Leaf 夹具补活格子（生成器自带直方图自检）。
    $mk = ExecRC 'py' "`"$badMk`" `"$leafSrc`" `"$d/fixture-leaf.iiq`""
    $bpf = "$d/badpix-leaf.dng"
    $rbL = ExecRC $dng "-e -lossless -v -i `"$d/fixture-leaf.iiq`" -O `"$bpf`""
    $rdL = ExecRC 'py' "`"$badRd`" `"$bpf`" `"$d/fixture-leaf.iiq`""
    $vlL = ExecRC $dvLens "-v `"$bpf`""
    $txL = ([string]($vlL[1]) + "`n" + [string]($vlL[2]))
    $lineL = [string](([string]($rdL[1]) -split "`r?`n" | Where-Object { $_ -match '^BADPIX-READ' } | Select-Object -First 1))
    CK ($mk[0] -eq 0 -and $rbL[0] -eq 0 -and $rdL[0] -eq 0 `
        -and ($rbL[2] -match 'rects=2') -and ($rbL[2] -match '丢弃越界=2') -and ($rbL[2] -match '丢弃不认类型=1') `
        -and $vlL[0] -eq 0 -and $txL -match 'Opcode: FixBadPixelsList') `
       ("㉒e 坏列/越界分支的活格子（重臂，夹具只改缺陷表）：$lineL " +
        "· 写侧 rects=2 丢弃越界=2 丢弃不认类型=1 都点名 · Adobe 仍 Validation（rc=$($vlL[0])）")
} else {
    Write-Output 'SKIP ㉒d/㉒e 坏点相位分叉与坏列/越界分支夹具未跑（要 DNGBADPIX_HEAVY=1 ⇒ 记未判，不记绿）'
}

# ══ ㉓ Phase One/Leaf 的黑电平口径：源 0x021D → 标准位 BlackLevel，且证「只动声明不动像素」══
# 成因（账本 §30）：LibRaw 把 0x021D 存着不扣（扣减在 raw2image 的 phase_one_subtract_black），
# 却把 maximum 写成 0xfffc−t_black。我们走 unpack() ⇒ 平面仍带偏置，旧产物按「扣减后」的跨度
# 声明 WhiteLevel 而 BlackLevel 留 0 ⇒ Adobe 的读者把整幅抬 1040/65535（=1.59% 满量程）并把
# 样本>64508 的 17 个像素削成饱和（这两数由 --render-ab 重臂当场量出，不是推算）。
$lvPr = "$root/tests/scripts/_probe-p1-levels.py"
if (-not (Test-Path $lvPr)) {
    CK $false "㉓ 缺黑电平读手 $lvPr"
} else {
    # 复用 ㉒a 那件 IIQ 产物（同一把尺、同一次编码，避免两组各编一遍导致口径漂移）
    $lvOn = ExecRC 'py' "`"$lvPr`" `"$bpOn`" `"$iiqSrc`""
    $lvTx = ([string]($lvOn[1]) + "`n" + [string]($lvOn[2]))
    $lvLn = (($lvTx -split "`r?`n" | Where-Object { $_ -match '^LEVELS-READ' } | Select-Object -First 2) -join ' ⏎ ')
    CK ($lvOn[0] -eq 0 -and $lvTx -match 'LEVELS-OK') `
       ("㉓a 声明落在标准位且与平面口径一致：$lvLn")

    $lvWr = ExecRC $dng "-e -lossless -v -i `"$iiqSrc`" -O `"$d/levels-p40.dng`""
    $lvBl = FirstNum (TagVal 'BlackLevel' "$d/levels-p40.dng")
    $lvWh = FirstNum (TagVal 'WhiteLevel' "$d/levels-p40.dng")
    $lvRd = (TagVal 'BlackLevelRepeatDim' "$d/levels-p40.dng")
    CK ($lvWr[0] -eq 0 -and ($lvWr[2] -match 'p1-levels') -and $lvBl -eq 1024 -and $lvWh -eq 65532 `
        -and ($lvRd -replace '\s', '') -eq '11') `
       ("㉓b 写侧点名 p1-levels 且 exiftool 独立读到 BlackLevel=$lvBl/WhiteLevel=$lvWh/" +
        "RepeatDim=[$lvRd]（1×1 才不会被读者按 2×2 取到另三个通道的 0）")

    $lvPl = ExecRC 'py' "`"$planeCmp`" `"$iiqSrc`" `"$d/levels-p40.dng`""
    $lvPlLn = [string](([string]($lvPl[1]) -split "`r?`n" | Where-Object { $_ -match '^RAW-PLANE' } | Select-Object -First 1))
    CK ($lvPl[0] -eq 0) "㉓c 像素逐位未动（声明修补只动元数据）：$lvPlLn"

    # 负控：这条通道只认「源自己声明了 0x021D 且 LibRaw 没替我们扣过」的件，非 IIQ 源不得触发
    $lvArw = ExecRC $dng "-e -lossless -v -i `"$arw`" -O `"$d/levels-arw.dng`""
    $lvABl = FirstNum (TagVal 'BlackLevel' "$d/levels-arw.dng")
    $lvAWh = FirstNum (TagVal 'WhiteLevel' "$d/levels-arw.dng")
    CK ($lvArw[0] -eq 0 -and -not ($lvArw[2] -match 'p1-levels') -and $lvABl -eq 512 -and $lvAWh -eq 15360) `
       ("㉓d 负控：Sony ARW 产物未触发这条改写（键缺席），仍按源侧口径 BlackLevel=$lvABl/WhiteLevel=$lvAWh")

    # DNG→DNG 不得二次加工（黑电平已在平面口径里 ⇒ 再加一次就会把黑场挖空）
    $lvRe = ExecRC $dng "-e -lossless -v -i `"$d/levels-p40.dng`" -O `"$d/levels-p40-re.dng`""
    $lvRBl = FirstNum (TagVal 'BlackLevel' "$d/levels-p40-re.dng")
    $lvRWh = FirstNum (TagVal 'WhiteLevel' "$d/levels-p40-re.dng")
    CK ($lvRe[0] -eq 0 -and -not ($lvRe[2] -match 'p1-levels') -and $lvRBl -eq 1024 -and $lvRWh -eq 65532) `
       ("㉓e DNG→DNG 只搬运不叠加（键缺席，BlackLevel=$lvRBl/WhiteLevel=$lvRWh 未再抬一次）")

    # ㉓g 8-bit 出口的黑/白电平必须走**同一条缩放**（旧代码把 WhiteLevel 写死 255 而黑电平照样 >>8 ⇒
    # 分母被吹大约 4 倍，Adobe 把整幅渲染得暗 3.7-4.2 倍；账本 §37）。期望值不写常数：
    # 先编一臂 16-bit 取它自己的 BlackLevel/WhiteLevel，再按 (x+128)/256 推 8-bit 应声明什么。
    $lv16 = "$d/levels-idc16.dng"; $lv8 = "$d/levels-idc8.dng"
    $r16 = ExecRC $dng "-e -lossless -i `"$idcArw`" -O `"$lv16`""
    $r8  = ExecRC $dng "-e -lossless -4 -v -i `"$idcArw`" -O `"$lv8`""
    $b16 = FirstNum (TagVal 'BlackLevel' $lv16); $w16 = FirstNum (TagVal 'WhiteLevel' $lv16)
    $b8  = FirstNum (TagVal 'BlackLevel' $lv8);   $w8  = FirstNum (TagVal 'WhiteLevel' $lv8)
    $cap = [Math]::Min(255, [int][Math]::Floor(($w16 + 128) / 256))
    $exp8 = [int][Math]::Floor(($b16 + 128) / 256)
    CK ($r16[0] -eq 0 -and $r8[0] -eq 0 -and ($r8[2] -match 'bps-downgrade') -and $w16 -gt 0 `
        -and $b8 -eq $exp8 -and $w8 -eq $cap) `
       ("㉓g 8-bit 出口黑/白同尺：16-bit 声明 $b16/$w16 ⇒ 8-bit 必须声明 $exp8/$cap，" +
        "实得 $b8/$w8（键 bps-downgrade 在场；向下取整与工具侧整数除法同形）")

    if ($env:DNGLEVEL_HEAVY -eq '1') {
        $lvAb = ExecRC 'py' "`"$lvPr`" `"$bpOn`" `"$iiqSrc`" --render-ab `"$dvLens`" `"$d/levels-ab`""
        $lvAbTx = ([string]($lvAb[1]) + "`n" + [string]($lvAb[2]))
        $lvAbLn = ((($lvAbTx -split "`r?`n" | Where-Object { $_ -match '^LEVELS-RENDER' } | Select-Object -Last 3) -join ' ⏎ '))
        CK ($lvAb[0] -eq 0) ("㉓f Adobe 自己给出的影响数（重臂，等长变异件只动 2 个字节）：$lvAbLn")
    } else {
        Write-Output 'SKIP ㉓f 渲染 A/B 未跑（要 DNGLEVEL_HEAVY=1 ⇒ 记未判，不记绿）'
    }
}

# ── ㉖ F 类：次级 IFD 在 DNG→DNG 再编码里是否被静默丢掉（账本 §36/§39/§40）────────────
# ① 夹具成立 = Adobe 自己认这些位（NSFT 4/8/65540 + MaskedAreas + XMP(700) + 预览）；
# ② base → dngtool 重编码 → **Adobe 读者解出的遮罩/深度逐像素比** + 语义/MaskedAreas 值比。
# 判据自带验牙：自我对撞（base↔base）读不动 ⇒ 退 2（防「恒红」）；任何一项缺席/像素不同 ⇒ 退 1。
# 两条合法出口都有臂、都有负控：
#   臂 A rc=0 且真搬运（像素级）；臂 B 非零退出 + 点名 refuse-secondary-ifd-loss + 无产物。
#   没有写通路的两档（预览族 / XMP）必须被**点名**才算「在册欠账」，点名缺席 ⇒ 退 1（静默丢位）。
#   崩溃（0xC0000409）与缺 dng_validate ⇒ 只记未判，不记绿。
$secBase = "$d/sec-base.dng"
$secMkR  = ExecRC 'py' "`"$secMk`" `"$secBase`" `"$dvLens`""
$secMkLn = [string](([string]($secMkR[1]) -split "`r?`n" | Where-Object { $_ -match '^SEC-FIXTURE' } | Select-Object -Last 1))
CK ($secMkR[0] -eq 0) "㉖a F 类夹具成立（自写布局，Adobe 认这些次级 IFD）：$secMkLn"
$secCar  = ExecRC 'py' "`"$secPr`" `"$secBase`" `"$dng`" `"$d/sec-carry`" `"$dvLens`""
$secTx   = ([string]($secCar[1]) + "`n" + [string]($secCar[2]))
# 探针全文落盘：㉖b 的 PASS 行只摘最后 4 行，50830-only 那条在中间 ⇒ 证据不能只活在控制台
if (Test-Path "$d/sec-carry") { $secTx | Set-Content -Encoding utf8 "$d/sec-carry/probe.log" }
$secLn   = ((($secTx -split "`r?`n" | Where-Object { $_ -match 'SEC-CARRY|SEC-PROBE' } | Select-Object -Last 4) -join ' ⏎ '))
CK ($secCar[0] -eq 0) ("㉖b DNG→DNG 次级 IFD：要么真搬（遮罩/深度像素级相同、语义与 MaskedAreas 值相同），" +
       "要么非零退出+点名+无产物 ⇒ 不静默丢位（判据 rc=$($secCar[0])：0=成 1=静默丢位/像素不同/点名不全 2=跑不动/崩）：$secLn")

# ── ㉖c/㉖d/㉖e 崩溃防回归：最小触发集 = **「输出文件打不开」这一条，与输入内容无关** ────
# 修前读数（一手）：`dngtool -e -lossless -i <任意输入> -O <父目录不存在的输出>` ⇒ rc=3221226505
#   = 0xC0000409 = __fastfail，无产物；`-O` 指到**已存在的目录名**上同样崩。
#   无次级 IFD 的件（p40.dng / raw_src_8bit.dng）一样崩 ⇒ 不是 F 类件的性质。
# 根因（文件:行）：dngtool.cpp 里 `dng_file_stream stream(outputPath, true)` 落在包写件的 try
#   **之外**（try 从 writer.WriteDNG 才开始）；本构建带 qDNGValidate=1（CMakeLists.txt:60）⇒
#   dng_file_stream.cpp:39-41 打不开就 ReportError + ThrowSilentError() ⇒ dng_exceptions.h:151
#   Throw_dng_error(dng_error_silent) ⇒ throw dng_exception；而 dng_exception 不派生自
#   std::exception（dng_exceptions.h:51），main 又没有任何 try ⇒ 未捕获 C++ 异常
#   ⇒ std::terminate ⇒ UCRT abort() ⇒ __fastfail。机制用独立最小复现件量出来的：
#   tests/output/results/_lane/probe/t.cpp（同样形状）⇒ rc=-1073740791，同码；
#   对照组 t2.cpp（catch 住再 return）⇒ rc=6。
# 修后要求：非零退出（6）+ 无产物 + 点名 ASCII 键 refuse-output-unopenable。
$ffS = -1073740791; $ffU = 3221226505
function CrashArm([string]$tag, [string]$srcPath, [string]$outArg, [string]$dirOf) {
    $a  = ExecRC $dng "-e -lossless -i `"$srcPath`" -O `"$outArg`""
    $tx = ([string]($a[1]) + "`n" + [string]($a[2]))
    $has = Test-Path $outArg
    $clean = ($a[0] -ne 0 -and $a[0] -ne $ffS -and $a[0] -ne $ffU -and -not $has `
              -and $tx -match 'refuse-output-unopenable')
    CK $clean ("$tag 输出打不开时干净退出并点名（实得 rc=$($a[0]) 产物=$([bool]$has) " +
               "点名=$([bool]($tx -match 'refuse-output-unopenable'))；0xC0000409=崩溃，必须不再出现）")
    return $tx
}
# ㉖c 最小触发集第一面：DNG 输入（无次级 IFD，排除"F 类件才崩"的旧归因）
$_cDir = "$d/sec-crash-nodir-c"                       # ← 父目录**故意不建**
$null = CrashArm '㉖c' $dngFix "$_cDir/o.dng" $_cDir
# ㉖d 第二面：厂商 RAW 输入 ⇒ 证明触发条件只在输出侧，与输入类型无关
$_dDir = "$d/sec-crash-nodir-d"
$null = CrashArm '㉖d' $arw "$_dDir/o.dng" $_dDir
# ㉖e 第三面：`-O` 指到一个**已存在的目录**上（同一 fopen 失败的另一形状）
$_eDir = "$d/sec-crash-isdir"
New-Item -ItemType Directory -Force -Path "$_eDir/sub" | Out-Null
$aE = ExecRC $dng "-e -lossless -i `"$dngFix`" -O `"$_eDir/sub`""
$txE = ([string]($aE[1]) + "`n" + [string]($aE[2]))
CK ($aE[0] -ne 0 -and $aE[0] -ne $ffS -and $aE[0] -ne $ffU -and $txE -match 'refuse-output-unopenable') `
   ("㉖e -O 指到已存在目录时同样干净退出并点名（实得 rc=$($aE[0]) 点名=$([bool]($txE -match 'refuse-output-unopenable'))）")

# ── ㉖f/㉖g 负控：拿**修前的 exe** 在同两条臂上必须给出不同读数（否则臂是恒真的） ──────
# 修前件 = 本轮动手前另存的 build/Release 件（sha 前 16 = 76c834fb9745，与在册闸当时被测的
# publish 件同值）。不在位 ⇒ 记未判（不记绿）。
$secOld = "$root/tests/output/results/_main/dngtool.before-crashfix.exe"
if (Test-Path $secOld) {
    $oC = ExecRC $secOld "-e -lossless -i `"$dngFix`" -O `"$d/sec-crash-nodir-old/o.dng`""
    $oTx = ([string]($oC[1]) + "`n" + [string]($oC[2]))
    CK ($oC[0] -eq $ffS -or $oC[0] -eq $ffU) `
       ("㉖f 负控（崩溃臂有牙）：修前 exe 在同一臂上必须**就是那个 fastfail**（实得 rc=$($oC[0])，" +
        "期望 $ffU=0xC0000409）⇒ ㉖c/㉖d/㉖e 的绿是修出来的，不是尺子松出来的")
    $oP = ExecRC 'py' "`"$secPr`" `"$secBase`" `"$secOld`" `"$d/sec-carry-old`""
    CK ($oP[0] -ne 0) `
       ("㉖g 负控（F 类臂有牙）：修前 exe 过同一条判据必须**不成立**（实得 rc=$($oP[0])，" +
        "1=静默丢位 2=跑不动）⇒ ㉖b 的绿不是恒真")
} else {
    Write-Output '未判 ㉖f/㉖g 负控用的修前 exe 不在位（tests/output/results/_main/dngtool.before-crashfix.exe）'
}

# ── ㉗ E 类：Foveon X3F（账本 §35/§40）────────────────────────────────────────────
# 四条读数全由 **rawpy（另一份 LibRaw 构建）** 给：四件都能开 ⇒ DP1 的 white_level 与源相同
# ⇒ 三个存储平面逐位相同 ⇒ postprocess 渲染 max|Δ|=0；再用修前 exe 复现"按 65535 归一化 ⇒
# 只剩 3%"当负控（没有负控 ⇒ 记未判，不记绿）。判据见 tests/scripts/_probe-x3f-carry.py。
$x3fR   = ExecRC 'py' "`"$x3fPr`" `"$dng`" `"$d/x3f-carry`" `"$secOld`""
$x3fTx  = ([string]($x3fR[1]) + "`n" + [string]($x3fR[2]))
$x3fLn  = ((($x3fTx -split "`r?`n" | Where-Object { $_ -match 'X3F-CARRY' } | Select-Object -Last 3) -join ' ⏎ '))
if ($x3fR[0] -eq 2) {
    Write-Output ("未判 ㉗ E 类 X3F 判据跑不动（rc=2：缺 rawpy/素材/修前 exe ⇒ 不记绿）：$x3fLn")
} else {
    CK ($x3fR[0] -eq 0) ("㉗a 四件 X3F 全部能开出件，且 DP1 的 white_level/存储平面/渲染与源一致" +
           "（判据 rc=$($x3fR[0])：0=成 1=有齿项不成立 2=跑不动）：$x3fLn")
    CK (($x3fTx -match '负控\(修前 exe\)：white_level=65535') -and ($x3fTx -match 'max\|Δ\|=0')) `
       ("㉗b 负控（㉗a 的绿是修出来的）：修前 exe 同一输入必须复现假声明 white_level=65535 且渲染明显变暗" +
        "（读数见 ㉗a 行尾）")
}

# ── ㉘ DNG 出口的 XMP(700)：产品侧后置链（账本 §40.9）──────────────────────────────
# dngtool 构建 qDNGUseXMP=0 ⇒ XMP 只能靠 BuildDngStdTagsCarryArgs 那条 exiftool 后置。
# 两条串由 ServiceProbe 直接调产品代码打印（本闸不手抄）；默认串必须把 CreatorTool/Rating/Subject
# 抄回且 dngtool -verify 仍 rc=0；--strip-xmp 串必须一个都不抄（两臂真分叉）。
if (-not (Test-Path $svcPrb)) {
    Write-Output "未判 ㉘ DNG 出口 XMP：ServiceProbe 没构建（$svcPrb）⇒ 产品侧判据未跑，不记绿"
} else {
    $xmpR  = ExecRC 'py' "`"$xmpPr`" `"$svcPrb`" `"$dng`" `"$ex`" `"$d/xmp-product`""
    $xmpTx = ([string]($xmpR[1]) + "`n" + [string]($xmpR[2]))
    $xmpLn = ((($xmpTx -split "`r?`n" | Where-Object { $_ -match 'XMP-PROD' } | Select-Object -Last 3) -join ' ⏎ '))
    CK ($xmpR[0] -eq 0) ("㉘a DNG 出口 XMP：产品默认串把标准 XMP 抄回且 -verify 通过（判据 rc=$($xmpR[0])：" +
           "0=成 1=有齿项不成立 2=跑不动）：$xmpLn")
    CK (($xmpTx -match "default 串 rc=0 读回=\{'XMP:CreatorTool': 'fixturetool'") -and
        ($xmpTx -match "stripxmp 串 rc=0 读回=\{'XMP:CreatorTool': ''")) `
       ("㉘b 两臂真分叉：默认串抄回 CreatorTool，--strip-xmp 串为空（读数见 ㉘a 行尾）")
    # ㉘c：这条臂**直接起 ServiceProbe**（不经过 py 取数器）——覆盖账 #69 的 ⑨ 只认「在册脚本里
    #   真看得到调用点」的 mode，py 内部的 subprocess 对它不可见 ⇒ 判据面必须留一条直调，
    #   否则 `dng-xmp-args` 会变成一个「实现了但没人能跑到」的 mode（#76 ⑩ 抓的那一类）。
    $xaR   = ExecRC $svcPrb 'dng-xmp-args'
    $xaTx  = ([string]($xaR[1]) + "`n" + [string]($xaR[2]))
    $xaLs  = ($xaTx -split "`r?`n")
    $xaD   = ($xaLs | Where-Object { $_ -match '^DNG-XMP-ARGS default=' } | Select-Object -First 1)
    $xaS   = ($xaLs | Where-Object { $_ -match '^DNG-XMP-ARGS stripxmp=' } | Select-Object -First 1)
    CK ($xaR[0] -eq 0 -and ($xaD -match '-XMP:all') -and (-not ($xaS -match '-XMP:all'))) `
       ("㉘c 产品构建器直调：默认串含 -XMP:all 且 --strip-xmp 串不含（探针 rc=$($xaR[0])，" +
        "两行读数各取一行：default 命中=$([int]($xaD -match '-XMP:all')) stripxmp 命中=$([int]($xaS -match '-XMP:all'))）")
}

# ── ㉚ 裁剪/有效区：厂商内嵌裁剪 → DefaultCrop（2026-10-23，账本 §44）────────────────
# 语义：ActiveArea = LibRaw 可见区（存储平面里属于图像的部分），DefaultCrop = 机身默认取景框。
# 两者**不必相等**：Adobe 官方样例 03_jxl_bayer_raw_integer.dng 就是 ActiveArea 9600x6376、
# DefaultCrop 9504x6336@(32,20)。旧实现把 DefaultCrop 钉成整个 ActiveArea ⇒ 比机身 JPEG
# 多出 4~28 px 边框（像素没丢，取景框这件事实丢了）。
# 判据三层：
#   ① 写侧：DefaultCrop 值 = LibRaw raw_inset_crops[0]（厂商标签），且日志点名 vendor；
#   ② Adobe 读者（dng_validate -tif）：默认渲染尺寸 = DefaultCrop ⇒ 不是只有写入侧自说自话；
#   ③ LibRaw 读者（rawpy）：sizes.crop_* = DefaultCrop（说明标签被解析），而 postprocess
#      仍按 ActiveArea 出图 ⇒ 把"预览/默认处理 ≠ 裁剪语义"这件事钉在读数上。
# 负控两条（不许猜）：Sony 0xb02b 只给宽高 ⇒ 原点无来源，必须拒绝并保持满幅；
# Canon EOS R6 的内嵌裁剪 3408x2272 比 ActiveArea 3407x2271 大 1 px ⇒ 必须拒绝。
$cropFiles = @(
    @('Canon', "$root/tests/output/raw_samples/Canon_EOS40D.CR2",     '1936x1288@4,3',  '1944x1296'),
    @('Fuji',  "$root/tests/output/raw_samples/Fujifilm_XS1.RAF",     '2816x2116@16,14','2848x2144'),
    @('Pana',  "$root/tests/output/raw_samples/Panasonic_LX7.RW2",    '1368x1368@8,8',  '1384x1384'),
    @('Sony',  "$root/tests/output/raw_samples/Sony_ILCE7M2_IDC.ARW", '6024x4024@0,0',  '6024x4024'),
    @('R6',    "$root/tests/output/raw_samples/Canon_EOSR6.CR3",      '3407x2271@0,0',  '3407x2271')
)
$dvCrop = "$root/tools/src/dngtool/build/Release/dng_validate.exe"
$cropMissing = @($cropFiles | Where-Object { -not (Test-Path $_[1]) })
if ($cropMissing.Count -gt 0 -or -not (Test-Path $dvCrop)) {
    Write-Output 'SKIP ㉚ 裁剪/有效区（素材或 dng_validate 不在位）⇒ 记未判，不记绿'
} else {
    $cropBad = @(); $cropHit = @(); $cropNeg = @()
    foreach ($cf in $cropFiles) {
        $tag = $cf[0]; $src = $cf[1]; $wantCrop = $cf[2]; $wantActive = $cf[3]
        $out = "$d/crop-$tag.dng"
        $cr  = ExecRC $dng "-e -lossless -i `"$src`" -O `"$out`""
        $err = [string]($cr[2])
        if ($cr[0] -ne 0 -or -not (Test-Path $out)) { $cropBad += "$tag 转不出件 rc=$($cr[0])"; continue }
        $j = (ExecRC $ex "-json -s -DefaultCropOrigin -DefaultCropSize -ActiveArea `"$out`"")[1] | ConvertFrom-Json
        $gotCrop = (([string]$j.DefaultCropSize) -replace ' ', 'x') + '@' + (([string]$j.DefaultCropOrigin) -replace ' ', ',')
        $aa = @(([string]$j.ActiveArea) -split ' ')
        $gotActive = "$([int]$aa[3] - [int]$aa[1])x$([int]$aa[2] - [int]$aa[0])"
        # Adobe 读者：默认渲染必须按 DefaultCrop 出图
        $rbase = "$d/crop-$tag-render"
        ExecRC $dvCrop "-tif `"$rbase`" `"$out`"" | Out-Null
        $rj = (ExecRC $ex "-json -s -ImageWidth -ImageHeight `"$rbase.tif`"")[1] | ConvertFrom-Json
        $gotRender = "$($rj.ImageWidth)x$($rj.ImageHeight)"
        # LibRaw 读者：crop_* 认了标签，但 postprocess 仍出 ActiveArea
        $pyOne = 'import rawpy,sys;s=rawpy.imread(sys.argv[1]).sizes;print(str(s.crop_width)+chr(120)+str(s.crop_height));print(str(s.width)+chr(120)+str(s.height))'
        $rr = ExecRC 'py' "-c `"$pyOne`" `"$out`""
        $rl = ($rr[1] -split "`r?`n")
        $gotLrCrop = [string]$rl[0]; $gotLrVis = [string]$rl[1]
        if ($tag -in @('Sony', 'R6')) {
            $why = if ($tag -eq 'Sony') { 'vendor-wh-only(no origin)' } else { 'outside-active' }
            if ($gotCrop -eq $wantCrop -and $gotCrop -eq "$wantActive@0,0" -and $err -match [regex]::Escape("rejected: $why")) {
                $cropNeg += $tag
            } else { $cropBad += "$tag 负控没成立：DefaultCrop=$gotCrop err=$(($err -split "`r?`n" | Select-Object -Last 1))" }
        } else {
            if ($gotCrop -eq $wantCrop -and $gotActive -eq $wantActive -and $gotRender -eq ($wantCrop.Split('@')[0]) -and
                $gotLrCrop -eq ($wantCrop.Split('@')[0]) -and $gotLrVis -eq $wantActive -and $err -match 'default-crop=vendor') {
                $cropHit += "$tag $gotCrop"
            } else {
                $cropBad += ("$tag 期望 crop=$wantCrop active=$wantActive，实得 crop=$gotCrop active=$gotActive " +
                             "render=$gotRender libraw_crop=$gotLrCrop libraw_post=$gotLrVis")
            }
        }
    }
    CK ($cropHit.Count -eq 3 -and $cropBad.Count -eq 0) `
       ("㉚a 厂商裁剪进 DefaultCrop 且三家读者对得上（写侧点名 vendor / Adobe 渲染 / LibRaw 解析）：" +
        "命中 $($cropHit.Count)/3 [$(($cropHit) -join ', ')]" +
        $(if ($cropBad.Count) { ' · 破坏: ' + ($cropBad -join ' | ') } else { '' }))
    CK (@($cropNeg).Count -eq 2) `
       ("㉚b 负控（不许猜原点/不许越界）：Sony wh-only 与 EOS R6 越界都必须拒绝并保持 ActiveArea=" +
        "满幅（命中 $(@($cropNeg).Count)/2: $(($cropNeg) -join ', ')）")
    # ㉚c 负控（㉚a 的绿是修出来的）：把厂商裁剪搬进 DefaultCrop 之前的 exe 在同一输入上必须
    # 仍写满幅（Canon 1944x1296@0,0）、且日志里没有 default-crop=vendor。
    $cropOld = "$root/tests/output/results/_main/dngtool.before-croproute.exe"
    if (-not (Test-Path $cropOld)) {
        Write-Output 'SKIP ㉚c 负控（无裁剪路由的修前 exe 不在位）⇒ 该臂记未判，不记绿'
    } else {
        $oldOut = "$d/crop-old-canon.dng"
        $oc = ExecRC $cropOld "-e -lossless -i `"$root/tests/output/raw_samples/Canon_EOS40D.CR2`" -O `"$oldOut`""
        $oj = (ExecRC $ex "-json -s -DefaultCropOrigin -DefaultCropSize `"$oldOut`"")[1] | ConvertFrom-Json
        $oldCrop = (([string]$oj.DefaultCropSize) -replace ' ', 'x') + '@' + (([string]$oj.DefaultCropOrigin) -replace ' ', ',')
        $oldErr = [string]($oc[2])
        CK ($oc[0] -eq 0 -and $oldCrop -eq '1944x1296@0,0' -and $oldErr -notmatch 'default-crop=vendor') `
           ("㉚c 负控（㉚a 的绿是这条映射修出来的）：修前 exe 同一 Canon 输入仍写满幅（实得 $oldCrop，" +
            "vendor 点名=$([int]($oldErr -match 'default-crop=vendor'))）")
    }
    # ㉚d 原点不是猜的：Canon 的 (4,3) 是 LibRaw 把 AspectInfo(0,0) + SensorInfo 相加合成出来的，
    # 必须用**机身 JPEG** 当对照臂验证。预览 1936x1288 与裁剪框同尺寸 ⇒ 去马赛克后滑窗，
    # 正确原点应显著胜出（本机 (4,3)=0.942、(5,3)=0.870）。
    $alignPrb = "$root/tests/scripts/_probe-crop-preview-align.py"
    if (-not (Test-Path $alignPrb)) {
        Write-Output 'SKIP ㉚d 裁剪原点对拍（探针不在位）⇒ 该臂记未判，不记绿'
    } else {
        $al = ExecRC 'py' "`"$alignPrb`" `"$root/tests/output/raw_samples/Canon_EOS40D.CR2`" --expect 4,3 --out `"$d/crop-align`""
        $alTx = ([string]($al[1]) + "`n" + [string]($al[2]))
        $alLn = (($alTx -split "`r?`n" | Where-Object { $_ -match '^ALIGN-' } | Select-Object -Last 2) -join ' ⏎ ')
        if ($al[0] -eq 2) {
            Write-Output "未判 ㉚d 裁剪原点对拍跑不动（rc=2：缺 rawpy/Pillow/预览）⇒ 不记绿：$alLn"
        } else {
            CK ($al[0] -eq 0 -and $alTx -match '最优=\(4,3\)') `
               ("㉚d 裁剪原点由机身 JPEG 对拍钉住（不是 LibRaw 合成怪癖）：$alLn")
        }
    }
}

# ── ㉛ 逐实例账本自洽（2026-10-23，账本 §44）──────────────────────────────────────
# 账本的可复算性本身就是判据：总数 = 处置和 = 等级和 = 类和；处置/等级不许出现表外值。
# 本轮母体固定（44 件厂商样本、9,517 条厂商组实例）⇒ 总数钉死；未审计数是进度读数，
# 只要求 < 总数（防"把 unaudited 改名成已审计"的假绿）。
$auditPrb = "$root/tests/scripts/_audit-dng-meta-carry.py"
if (-not (Test-Path $auditPrb)) {
    Write-Output 'SKIP ㉛ 逐实例账本（审计脚本不在位）⇒ 记未判，不记绿'
} else {
    $au = ExecRC 'py' "`"$auditPrb`" --out `"$d/dng-audit`""
    $auTx = ([string]($au[1]) + "`n" + [string]($au[2]))
    $auLn = ((($auTx -split "`r?`n" | Where-Object { $_ -match 'total=|自洽' }) -join ' ⏎ '))
    $mTot = [regex]::Match($auTx, 'total=(\d+) audited=(\d+)')
    $tot = if ($mTot.Success) { [int]$mTot.Groups[1].Value } else { -1 }
    $aud = if ($mTot.Success) { [int]$mTot.Groups[2].Value } else { -1 }
    CK ($au[0] -eq 0 -and $tot -eq 9517 -and $aud -gt 0 -and $aud -lt $tot -and
        $auTx -match "all_equal_total': True") `
       ("㉛ 逐实例账本自洽：total=$tot（期望 9517）· audited=$aud · 三类和相等：$auLn")
}

# ── ㉜ 色彩标准位是"活的"：ASN/ForwardMatrix 开/关对撞（2026-10-23，账本 §44）────────
# 只判"标签在场"会被"写了但读者不用"骗过。这里同长就地改三处并对渲染取逐像素 MAD：
#   AsShotNeutral R×2 ⇒ 必须大改；删 ForwardMatrix1/2 ⇒ 回退到 ColorMatrix，必须显著改；
#   只改 ColorMatrix1 ⇒ 必须**远小于**删 FM（证明 DNG SDK 实际用的是 FM，不是 CM）。
# 读者口径：dng_validate 1.7.1 默认 8-bit sRGB（不是 ACR 全路径，见 §44 读者矩阵）。
$colorPrb = "$root/tests/scripts/_probe-dng-color-effect.py"
$colorDng = "$d/crop-Sony.dng"
if (-not (Test-Path $colorPrb)) {
    Write-Output 'SKIP ㉜ 色彩标准位对撞（探针不在位）⇒ 记未判，不记绿'
} elseif (-not (Test-Path $colorDng)) {
    Write-Output 'SKIP ㉜ 色彩标准位对撞（㉚ 的 Sony 产物不在位）⇒ 记未判，不记绿'
} else {
    $cl = ExecRC 'py' "`"$colorPrb`" `"$colorDng`" --out `"$d/color-effect`""
    $clTx = ([string]($cl[1]) + "`n" + [string]($cl[2]))
    $clLn = ((($clTx -split "`r?`n" | Where-Object { $_ -match '^COLOR-' }) -join ' ⏎ '))
    if ($cl[0] -eq 2) {
        Write-Output "未判 ㉜ 色彩标准位对撞跑不动（rc=2：缺 dng_validate/依赖/标签）⇒ 不记绿：$clLn"
    } else {
        CK ($cl[0] -eq 0 -and $clTx -match 'COLOR-OK') `
           ("㉜ 色彩标准位是活的：ASN 与 ForwardMatrix 各自改一处，渲染逐像素差按预期分叉：$clLn")
    }
}

Write-Output ''
Write-Output "PASS=$pass FAIL=$fail"
if ($fail -gt 0) { exit 1 } else { exit 0 }
