# verify-encoder-knob-liveness.ps1 —— **旋钮活性**判据（清单第 65 条；
#   2026-10-01 由 `tests/output/t79/knob-liveness.ps1` 迁入）：
#   高级编码选项逐个"打两个极端值 ⇒ 产物必须变"，否则该旋钮在这条通路上是死的。
# 为什么只做这个：现行门禁大量判"命令里有没有 -xxx"（L1），而 L1 判不出两类真缺陷：
#   ① 参数被发给了**根本不认它的编码器**；② 参数被认下但**取值范围/映射错**（产物恒定）。
#   09-26 审计的 A-3（硬件质量滑块）/A-4（NVENC aq）/A-13（GIF 关抖动）/A-15（WebP compression_level）/
#   A-18（JXL 质量域）全是这一类 ⇒ 用"两极端值差分"一次性把整类判据建起来。
# 被测：两档（默认 Release 产物 / `$env:PKG_VER` 时出货包 L3，见 `SUBJ` 段）。
#   产物一律按**字节/大小差分**，不读产品自报措辞。
# 六态判决（不许把判不出编成结论）：`live` 活 / `inert` 两端都没发 ⇒ **判红** /
#   `rejected` 越界值被编码器**响亮**拒绝 ⇒ 本臂没有两极端值差分可用（WARN，不计入 PASS/FAIL；
#     `--jpeg-dct float` 属此类 —— A-20 修复时 float 已从下拉移除，只剩 CLI/历史预设能送进来）/
#   `undecidable` 夹具两极端值产物相同 ⇒ 这一臂没判别力（WARN，不计入 PASS/FAIL）/
#   `nocmd` 读不到命令行 ⇒ **判红**（读不到不等于没发）/ `missing` 两端都没产物 ⇒ **判红** /
#   `gap` **登记在清单里的覆盖缺口**（该臂本批没做转换，例如 `--avif-tune` 在 **SVT 后端**上的活性只有
#     09-26 的一次性手工读数、没有闸内臂）⇒ 不跑、不计数、只打一行 SKIP，防"未验证"被同支的另一后端读数冒充。
# 退出码：0 = 全绿，1 = 有失败**或臂数与应有数不符**（有臂没判 ⇒ 不给绿），2 = 被测二进制不在位/版本串不符。
# ⚠ 双宿主兼容（PS 7 / PS 5.1）：禁三元 `? :` / `??` / `?.` / `&&` / `||`；双引号串内禁全角弯引号（改用 「」）。
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
# ── 被测二进制两档（2026-10-01 接进门禁清单时从"只打出货包"改成两档）───────────────────
#   默认 = 本轮**刚构建的 Release 产物**（L2+，每轮都跑）；设 `$env:PKG_VER` 时升 **L3** 打出货包。
#   ⚠ 缺即 exit 2、不产出任何读数，**绝不回退 Debug**；包内版本串与期望不符同样 exit 2
#     （写死目录 ⇒ "beta4 的读数打在 beta3 包上"，那就是一台假绿机器）。
#   ⚠ `T79_EXE` 只换被测产品二进制（改动还没进包时先用开发产物验），PLAN 工具链仍取上面解析出的那一份。
if ($env:PKG_VER) {
  $pkgVer = $env:PKG_VER
  $pkg = "$root\publish\build\FFmpegPictureUI-v$pkgVer-x64-full"
  if (-not (Test-Path "$pkg\FfmpegGui.exe")) { "FAIL 被测包不在位：$pkg\FfmpegGui.exe ⇒ 先跑 pack.ps1 -Mode full，或用 PKG_VER 指到实际发包版本"; exit 2 }
  $pkgPV = (Get-Item "$pkg\FfmpegGui.exe").VersionInfo.ProductVersion
  if ($pkgPV -notlike "$pkgVer*") { "FAIL 包内版本串与期望不符：期望 $pkgVer* 实得 $pkgPV"; exit 2 }
  $exe = "$pkg\FfmpegGui.exe"; $plan = "$pkg\PLAN"
  "SUBJ(被测) = v$pkgVer (L3 出货包)  ProductVersion=$pkgPV"
} else {
  $exe = "$root\src\FfmpegGui\bin\Release\net11.0\win-x64\FfmpegGui.exe"
  if (-not (Test-Path $exe)) { "FAIL Release 被测产物不在位：$exe ⇒ 先构建 Release；本门禁不回退 Debug"; exit 2 }
  $plan = "$root\publish\PLAN"
  'SUBJ(被测) = Release 开发产物 (L2+)  sha=' + (Get-FileHash $exe -Algorithm SHA256).Hash.Substring(0, 16)
}
# ⚠ 2026-10-03 修（复查 S5）：旧写法 `if ($env:T79_EXE) { $exe = $env:T79_EXE }` 在护栏**之后**覆盖，
#   盖完不复验 ProductVersion、也不重打 SUBJ（下面 G0 行打的是覆盖**后**的 sha，但版本串没重验过）
#   ⇒ 版本错配（`PKG_VER` + `T79_EXE` 指向另一份）可静默绕护栏。
#   ⇒ 覆盖后重跑同一套存在性 + 版本串校验，并补打 `SUBJ(被测,覆盖后)`。
#   变异验证：① `T79_EXE=<PLAN\ffmpeg.exe>` + `PKG_VER=1.6.0-beta4` ⇒ 修复后 exit 2 并点名；
#     ② 改回单行覆盖 ⇒ 同组环境变量下无任何红字 ⇒ 反证修复有牙。
if ($env:T79_EXE) {
  $exe = $env:T79_EXE
  if (-not (Test-Path $exe)) { "FAIL T79_EXE 覆盖的被测二进制不在位：$exe ⇒ 拒绝跑（不给异构二进制出读数）"; exit 2 }
  $ovPV = (Get-Item $exe).VersionInfo.ProductVersion
  if ([string]::IsNullOrWhiteSpace($ovPV)) { "FAIL T79_EXE 覆盖件读不到 ProductVersion：$exe ⇒ 无从证明被测是哪一份，拒绝跑"; exit 2 }
  if ($env:PKG_VER) {
    if ($ovPV -notlike "$pkgVer*") { "FAIL T79_EXE 覆盖件的版本串与 PKG_VER 期望不符：期望 $pkgVer* 实得 $ovPV（$exe）"; exit 2 }
  }
  'SUBJ(被测,覆盖后) = ' + $exe + '  ProductVersion=' + $ovPV + '  sha=' + (Get-FileHash $exe -Algorithm SHA256).Hash.Substring(0, 16)
}
$env:FFMPEGGUI_PLAN_DIR = $plan
$d    = "$root\tests\output\validate\l3_knob"
if (Test-Path $d) { Remove-Item -Recurse -Force $d }
New-Item -ItemType Directory -Force -Path $d | Out-Null
# ⚠ 累加器**不能叫 $R**：PS 变量名大小写不敏感，`foreach ($r in $rows)` 会把累加器打成哈希表，
#   后续 `$R += [pscustomobject]` 直接抛"一个哈希表只能添加到另一个哈希表"（09-30 实测踩过，读数就此失真）。
$res = New-Object System.Collections.ArrayList
# ⚠ `$p` 会是 `$null`：某一端**没有产物**时（越界值被拒 ⇒ rc≠0 且零产物）`$b.file` 就是 null，
#   裸 `Test-Path -LiteralPath $null` 会往 stderr 打一条 `Value cannot be null` 的红字错误 ——
#   门禁本身照绿，但日志里挂着错误行就给了读者"这一格坏了"的错觉（09-30 实测到该形状）。
function Sha($p) { if ($p -and (Test-Path -LiteralPath $p)) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.Substring(0, 16) } else { '<无>' } }
function Rec($n, $st, $det) {
  $null = $res.Add([pscustomobject]@{ n = $n; st = $st; det = $det })
  # `rejected` 的前缀**不能写 FAIL**：它是"越界值被响亮拒绝"这个**合法出口**（见 $rows 处 A-20 的措辞纠正），
  #   而运行器把 `^ *FAIL|^FAIL` 的行**当失败明细回显** ⇒ 写成 FAIL 会让读者在**绿的一步**下面看到红字。
  # `crashed`（2026-10-03 加）："进程没成功且说不出为什么" ⇒ **判红**（见 N 段 M7 修复注释）
  # ⚠ `nocmd` 前缀 = **FAIL**（2026-10-04 修 L2）：旧写法写 'WARN' 却把 nocmd 计入 `$nFail` 且进 `$hard`
  #   ⇒ 输出行看着是"警告不判色"，实际会 exit 1 ⇒ 标签与后果不一致。脚本头 `:14` 已明写
  #   「`nocmd` 读不到命令行 ⇒ **判红**（读不到不等于没发）」⇒ 措辞应服从该语义，改成 FAIL 对齐后果。
  $pre = @{ live = 'PASS'; inert = 'FAIL'; rejected = 'WARN'; undecidable = 'WARN'; nocmd = 'FAIL'; missing = 'FAIL'; crashed = 'FAIL'; gap = 'SKIP' }[$st]
  '{0}  {1}   [{2}] {3}' -f $pre, $n, $st, $det
}
# 子进程输出：`--log-file` **不够** —— 实测 `ImageEncoderArgs` 那批"这个值本后端承接不了"的播报走的是
#   `Console.WriteLine`（stdout），不进 item.Log/log-file ⇒ 只看 log-file 会把"其实播了"误判成"静默"。
#   所以三个来源都要收：log-file + stdout + stderr。
function Conv($tag, $src, $fmt, $extra) {
  $o = "$d\o_$tag"; New-Item -ItemType Directory -Force -Path $o | Out-Null
  $lg = "$d\lg_$tag.txt"
  $a = @('--headless', '--log-level', 'debug', '-i', ('"' + $src + '"'), '--format', $fmt,
         '--output', ('"' + $o + '"'), '--log-file', ('"' + $lg + '"'))
  if ($extra) { $a += $extra }
  $p = Start-Process -FilePath $exe -ArgumentList $a -Wait -NoNewWindow -PassThru `
       -RedirectStandardOutput "$d\so_$tag.out" -RedirectStandardError "$d\se_$tag.err"
  $allTxt = ''
  foreach ($f in @($lg, "$d\so_$tag.out", "$d\se_$tag.err")) {
    if (Test-Path -LiteralPath $f) { try { $allTxt += [IO.File]::ReadAllText($f) } catch { } }
  }
  $f = @(Get-ChildItem $o -Recurse -File | Where-Object { $_.Length -gt 0 } | Select-Object -First 1)
  $cmd = ''
  if (Test-Path -LiteralPath $lg) {
    # ⚠ 只认**真命令行**：产品日志里有一句散文也含 "[cmd] 行" 字样（`已追加 -progress pipe:1（不改动 [cmd] 行）`），
    #   按 `[cmd]` 取末条会把那句散文当成命令行 ⇒ 所有旋钮都读成"<未下发>"，把"活"误判成"死"（09-30 实测踩过）。
    #   ⚠ 各后端的日志前缀不同：ffmpeg 是 `[cmd] ffmpeg …`，cjxl 是 `cjxl 命令行: …`，
    #   avifenc/JxrEncApp 同理 ⇒ 只认 `[cmd]` 会让整条 JXL/AVIF-avifenc/JXR 通路"读不到命令"，
    #   而"读不到"既不能算死也不能算活 ⇒ 下面用 nocmd 单列这种状态，不拿它编结论。
    $cmdLines = @(([IO.File]::ReadAllText($lg)) -split "`r?`n" | Where-Object { ($_ -match '\[cmd\]' -and $_ -match ' -i ') -or $_ -match '(cjxl|djxl|avifenc|cjpegli|JxrEncApp|dngtool)[^\r\n]{0,16}(命令行|command line)' })
    if ($cmdLines.Count) {
      # ⚠ **全部**命令行都参与搜索：两趟式路线（先解码再编码）会有第二条不含该旋钮的命令，
      #   只取末条 ⇒ 把确实下发了的旋钮读成"<未下发>"（09-30 实测：`-row-mt 1` 在第一条里、末条没有）。
      $cmd = (($cmdLines | ForEach-Object { $_ -replace '^.*(\[cmd\]|命令行\s*[:：])', '' }) -join ' && ').Trim()
    }
  }
  if ($f.Count -eq 0) { return @{ code = $p.ExitCode; file = $null; len = -1; cmd = $cmd; log = $allTxt } }
  return @{ code = $p.ExitCode; file = $f[0].FullName; len = $f[0].Length; cmd = $cmd; log = $allTxt }
}
# 从两端的命令行里取"该旋钮实际被下发的值"（判"没发出去"与"发出去但无效"的**唯一**区分手段）。
# ⚠ $pat 是**正则**（带 1 个捕获组），不是字面量：同一个语义在不同后端形态不同 ——
#   实测 `--avif-tune psnr` 发的是 AVOption `-tune psnr`，而 `--avif-tune IQ` 走的是 `-aom-params tune=iq`。
#   按字面量找 `-tune ` 会把后者误判成"没下发"。
function ValIn($cmd, $pat) {
  if (-not $pat) { return '' }
  if (-not $cmd) { return '<无命令通道>' }   # ⚠ 与"<未下发>"是两件事：前者是**我读不到**，后者是**产品没发**
  $m = [regex]::Match($cmd, $pat)
  return $(if ($m.Success) { $m.Groups[1].Value } else { '<未下发>' })
}
# 夹具：**必须有细节**的 8-bit 照片样。09-30 实测：用纯色渐变时 18 条里 12 条报"产物相同"，
#   换成照片样只剩 2 条 ⇒ 有损编码器对纯色不敏感，活性判据拿纯色夹具会成批假红。
$src = "$root\tests\output\sources\src_8bit.png"
if (-not (Test-Path $src)) { Rec 'G0 夹具在位' 'missing' "缺 $src"; exit 2 }
"G0 被测=$exe sha=$((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.Substring(0,16)) 夹具=$(Sha $src)"
''
# 取值域一律按 **UI/CLI 实际提供的选项**取（不是凭印象）：
#   tiff-compression 面板项 = raw|lzw|deflate|packbits（`MainWindow.xaml:764-767`）⇒ "none" 不在域内，
#     上一版拿 none 做两端 ⇒ ffmpeg 报 `Error setting option compression_algo to value none`，那种红是我的错。
#   avif-tune 的白名单 = iq|ms_ssim|vmaf|psnr|ssim（`ImageEncoderArgs.cs:354-360`）⇒ "film" 会被
#     NormalizeTuneToken **静默丢成空串**（无播报，属诚实性缺陷），拿它做一端同样是我的错。
#   jpeg-dct 的 `float` 是**只能由 CLI/历史预设**送进来的越界值（面板三项 = auto/int/fastint，
#   A-20 修复时已把 float 从下拉移除，见 `MainWindow.xaml:725-728`）⇒ 用 expect='rejected' 明写：
#   这一臂要判的是"越界值必须被**响亮**拦下"，而不是两极端值差分（响亮性由下面 N 段专臂判）。
$rows = @(
  @{ fmt='jpg';  lab='--jpeg-huffman';       pat='-huffman ([^\s"]+)';               lo=@('--jpeg-huffman','default');  hi=@('--jpeg-huffman','optimal'); expect='live' },
  @{ fmt='jpg';  lab='--jpeg-dct';           pat='-dct ([^\s"]+)';                   lo=@('--jpeg-dct','int');          hi=@('--jpeg-dct','float');      expect='rejected' },
  @{ fmt='jpg';  lab='-q';                   pat='-q:v ([^\s"]+)';                   lo=@('-q','10');                   hi=@('-q','95');                expect='live' },
  @{ fmt='png';  lab='--png-pred';           pat='-pred ([^\s"]+)';                  lo=@('--png-pred','none');         hi=@('--png-pred','paeth');     expect='live' },
  @{ fmt='png';  lab='--png-dpi';            pat='-dpi ([^\s"]+)';                   lo=@('--png-dpi','72');            hi=@('--png-dpi','1200');       expect='live' },
  @{ fmt='webp'; lab='--webp-preset';        pat='-preset ([^\s"]+)';                lo=@('--webp-preset','default');   hi=@('--webp-preset','photo');  expect='live' },
  @{ fmt='webp'; lab='--webp-compression';   pat='-compression_level ([^\s"]+)';     lo=@('--webp-compression','0');    hi=@('--webp-compression','6'); expect='live' },
  @{ fmt='tiff'; lab='--tiff-compression';   pat='-compression_algo ([^\s"]+)';      lo=@('--tiff-compression','raw');  hi=@('--tiff-compression','lzw'); expect='live' },
  @{ fmt='tiff'; lab='--tiff-dpi';           pat='-dpi ([^\s"]+)';                   lo=@('--tiff-dpi','72');           hi=@('--tiff-dpi','1200');      expect='live' },
  @{ fmt='jxl';  lab='--jxl-effort';         pat='(?:-e|--effort) ([^\s"]+)';        lo=@('--jxl-effort','1');          hi=@('--jxl-effort','9');       expect='live' },
  @{ fmt='jxl';  lab='--jxl-modular';        pat='(?:-modular|--modular|modular=)([^\s"]*)'; lo=@('--jxl-modular','false'); hi=@('--jxl-modular','true'); expect='live' },
  @{ fmt='avif'; lab='--avif-cpu-used';      pat='-cpu-used ([^\s"]+)';              lo=@('--avif-cpu-used','0');       hi=@('--avif-cpu-used','8');    expect='live' },
  @{ fmt='avif'; lab='--avif-row-mt';        pat='-row-mt ([^\s"]+)';                lo=@('--avif-row-mt','false');     hi=@('--avif-row-mt','true');   expect='live' },
  @{ fmt='avif'; lab='--avif-still-picture'; pat='-still-picture ([^\s"]+)';         lo=@('--avif-still-picture','false'); hi=@('--avif-still-picture','true'); expect='live' },
  @{ fmt='avif'; lab='--avif-tune';          pat='(?:-tune |tune=)([^\s"]+)';        lo=@('--avif-tune','psnr');        hi=@('--avif-tune','iq');       expect='live' },
  # ⚠ 登记一条**已知覆盖缺口**（2026-10-02）：上面那臂跑的是默认后端 = **libaom**，SVT 后端的 tune
  #   （`-svtav1-params tune=`，实测域 0..5）**没有任何臂把 `--encoder libsvtav1` 与 tune 一起下发**
  #   ⇒ "SVT 上 tune 真的改了产物"目前只有 09-26 那批的一次性手工读数背书（`tune=0/1/2` 无损成立、
  #   `tune=4/5` 打掉无损），不在闸内。没有顺带验，是因为两趟 SVT 编码要占 200 s+ ⇒ 留作下一批补做，
  #   但**先在这里留名**，免得"未验证"被表上的 `-avif-tune 活` 冒充过去。
  @{ fmt='avif'; lab='--avif-tune@libsvtav1（闸内缺口，待补）'; pat=''; lo=@(); hi=@(); expect='gap' },
  @{ fmt='gif';  lab='--gif-dither';         pat='-gifflags ([^\s"]+)';              lo=@('--gif-dither','false');       hi=@('--gif-dither','true');   expect='live' },
  @{ fmt='gif';  lab='--gif-palette';        pat='-gifflags ([^\s"]+)';              lo=@('--gif-palette','false');      hi=@('--gif-palette','true');  expect='live' },
  @{ fmt='jxr';  lab='JXR 质量轴(面板无控件)'; pat='-q ([^\s"]+)';                     lo=@();                            hi=@('-q','30');              expect='live' }
)
'=== 逐旋钮：两极端值产物必须不同；相同则用**命令行**区分"没发出去"与"发出去但此夹具判不出" ==='
foreach ($r in $rows) {
  $slug = ($r.lab -replace '[^a-zA-Z0-9]', '')
  if ($r.expect -eq 'gap') {
    # 登记在**清单里的覆盖缺口**：不跑转换（$lo/$hi 是空 ⇒ 跑了也是白跑两趟），只留一行可 grep 的 SKIP。
    #   为什么要专门留这一行：本套件的表看起来"每个旋钮都有一臂"，缺哪一格若不点名，就会被
    #   同一支上别的后端读数冒充过去（"未验证"与"验证过且通过"在汇总里长得一样）。
    Rec ('K ' + $r.fmt + ' ' + $r.lab) 'gap' '本臂不做转换：这是**登记在清单里的覆盖缺口**，下一批补做时把 expect 改成 live 并填 lo/hi'
    continue
  }
  $a = Conv ('lo_' + $slug) $src $r.fmt $r.lo
  $b = Conv ('hi_' + $slug) $src $r.fmt $r.hi
  $name = 'K ' + $r.fmt + ' ' + $r.lab
  $vLo = ValIn $a.cmd $r.pat; $vHi = ValIn $b.cmd $r.pat
  $det = "$($a.len)B($(Sha $a.file)) vs $($b.len)B($(Sha $b.file))｜$($r.pat)=$vLo/$vHi"
  if ($r.expect -eq 'rejected') {
    # 这一臂的"高值"是**越界值**（编码器不认），所以它天然没有"两极端值差分"可用；
    #   要判的是"越界值必须被**响亮**拦下"（rc≠0 或产物取不回来），响亮性本身由下面 N 段专臂断言。
    #   ⚠ 措辞纠正（2026-10-01）：旧注释/旧标签把它写成"面板值域被编码器拒绝 = A-20 复现"，
    #     而 A-20 修复时 float 已从下拉移除（`MainWindow.xaml:725-728` 只剩 auto/int/fastint）⇒
    #     现在只有 CLI/历史预设能把 float 送进来，`rc=1` 是**正确行为**，不是缺陷复现。
    $rej = @($a, $b | Where-Object { $_.code -ne 0 -or -not $_.file }).Count
    if ($rej -gt 0) {
      Rec ($name + '：越界值被编码器响亮拒绝（本臂两极端值差分不适用；响亮性由 N 段专臂判）') 'rejected' `
        $("lo rc=$($a.code) hi rc=$($b.code)；$det")
      continue
    }
    # 没被拒 ⇒ **不许**因为 expect=rejected 就直接记"活"：落回常规差分（两端同产物仍会记未判/死）。
  }
  if ($a.code -ne 0 -or $b.code -ne 0 -or -not $a.file -or -not $b.file) {
    Rec ($name + '：两端都成功产出') 'missing' "lo rc=$($a.code) len=$($a.len) / hi rc=$($b.code) len=$($b.len)；$det"; continue
  }
  if ((Sha $a.file) -ne (Sha $b.file)) { Rec ($name + '：旋钮是活的') 'live' $det; continue }
  # 产物相同 ⇒ 用命令行定位是哪一种。**三种情形不许混成一种**（09-30 实测）：
  #   ① 两端都没出现该参数 ⇒ 真死（例：`--jxl-modular` 在 cjxl 路线上两端都查无此参，源码也 0 引用）。
  #   ② 只有真端下发、假端"不发=编码器默认" ⇒ 布尔旋钮的**合法写法**，产物相同只说明该夹具判不出
  #      （例：`-row-mt` 要有多个 tile 行才有差别）⇒ 记未判；把它记成"死"就是误伤产品。
  #   ③ 两端都下发了不同值仍同产物 ⇒ 该夹具无判别力，记未判。
  if ($vLo -eq '<无命令通道>' -or $vHi -eq '<无命令通道>') {
    # ④ 该通路的命令行**我读不到**（前缀不认/日志没落）⇒ 这既不是"没发"也不是"发了无效"，
    #    不许拿它当证据；记未判并去补读取口径。
    Rec ($name + '：产物相同但这条通路的命令行读不到 ⇒ 无从定性，记未判') 'nocmd' $det
  } elseif ($vLo -eq '<未下发>' -and $vHi -eq '<未下发>') {
    Rec ($name + '：旋钮是死的（两端都没发到编码器）') 'inert' $det
  } elseif ($vLo -eq '<未下发>' -or $vHi -eq '<未下发>') {
    Rec ($name + '：一端按"不发=默认"、另一端已下发 ⇒ 本夹具判不出，记未判') 'undecidable' $det
  } else {
    Rec ($name + '：两极端值产物相同但参数确已分别下发 ⇒ 本夹具无判别力，记未判') 'undecidable' $det
  }
}
''
'=== 域外值必须**点名**（不许静默不发）：面板/命令行收得下、编码器承接不了的值 ==='
# 动因（U4b）：`NormalizeTuneToken` 对不认识的值返回空串 ⇒ 旧行为是"什么都不发、也什么都不说"，
#   实测 `--avif-tune MS_SSIM` 走 libaom 时产物与不选 tune **逐字节相同**、命令行查不到任何 tune 痕迹。
#   面板承诺过的取值，落在某条后端上不可表达时必须说出来（本仓既有原则：不可用要显式报错/播报）。
$naming = @(
  @{ fmt='avif'; lab='--avif-tune MS_SSIM'; a=@('--avif-tune','MS_SSIM'); pat='not expressible on libaom-av1|tune=MS_SSIM|is not available' },
  # U4b 第二半（2026-10-02）：**认不出**的 tune 串（不是"认得出但这条后端不支持"）以前折成空串 ⇒
  #   与"用户没选"同形 ⇒ 不发也不说。现在必须点名。面板给不出这个值，只有 CLI/旧预设能送进来。
  @{ fmt='avif'; lab='--avif-tune film';    a=@('--avif-tune','film');      pat='is not expressible|tune=|no tuning flag' },
  @{ fmt='jpg';   lab='--jpeg-dct float';    a=@('--jpeg-dct','float');      pat='not supported|Unsupported|Ignoring|拒绝|不支持' }
)
foreach ($n in $naming) {
  $z = Conv ('nm_' + ($n.lab -replace '[^a-zA-Z0-9]', '')) $src $n.fmt $n.a
  # 语义：**要么响亮失败（rc≠0 且说得清为什么），要么 rc=0 但把"这个值没生效"说出来**。
  #   只有"rc=0 且全程没一句话"才是静默欺骗 —— 那才是要打红的形状（把 rc≠0 也判红会逼产品去"假装成功"）。
  #
  # ⚠ 2026-10-03 修 M7：**崩溃 ≠ 诚实**。旧写法 `$honest = ($z.code -ne 0) -or $named`
  #   把判据整个交给了 rc ⇒ 产品因为**任何**原因（参数解析崩、PLAN 缺件、夹具路径错、
  #   `-2147450750` 这种"进程根本没起来"的码、三路输出全空）非零退出，这一臂都记 **PASS**。
  #   ⇒ 现在把 rc≠0 拆成两半，只有"**说得出为什么**的非零退出"才算诚实拒答：
  #     · `$named`         = 产品自己的播报命中本臂的 `$n.pat`；
  #     · `$rejectEvidence`= 三路输出里有编码器/产品给出的**拒绝证据行**（实测 `--jpeg-dct float`
  #                          这一臂正是这种形状：`Unable to parse "dct" option value "float"` /
  #                          `Error setting option dct to value float.`，而它不命中 `$n.pat`）；
  #     · `$crashed`       = 三路输出**全空**（连一句都没有）／退出码是**异常值**（非 0 且非 1，
  #                          如 -2147450750）／rc≠0 却**没有任何**拒绝证据 ⇒ 记红并点名，不算 honest。
  #   变异验证：把这一臂的 `$z` 换成 `code=-2147450750 / log=''`（崩溃形状）⇒ 旧写法记 PASS（崩溃即通过），
  #     新写法记 `crashed`/FAIL 并点名 ⇒ 转红；改回后该臂回到 live（PASS）⇒ 转绿。
  $named = ($z.log -match $n.pat)
  $rejectEvidence = ($z.log -match '(?i)(unable to parse|error setting option|invalid argument|unsupported|not supported|ignoring|unknown option|无法解析|不支持|不接受|拒绝|非法|无效)')
  $emptyOut = [string]::IsNullOrWhiteSpace($z.log)
  $abnormalRc = (($z.code -ne 0) -and ($z.code -ne 1))
  $crashed = ($emptyOut -or $abnormalRc -or (($z.code -ne 0) -and (-not $named) -and (-not $rejectEvidence)))
  Rec ('N ' + $n.fmt + ' ' + $n.lab + '：不可表达/被拒时要么有说法地非零退出、要么有播报（崩溃与静默成功都判红）') `
    $(if ($crashed) { 'crashed' } else { $(if ($named) { 'live' } else { $(if ($z.code -ne 0) { 'live' } else { 'inert' }) }) }) `
    ("rc=$($z.code) 播报匹配 $($n.pat) = $named｜拒绝证据 = $rejectEvidence｜输出全空 = $emptyOut｜异常退出码 = $abnormalRc" + `
     $(if ($crashed) { '｜崩溃/静默失败 ≠ 诚实拒答：进程没成功且说不出为什么，不给 PASS' } else { $(if (-not $named -and $z.code -eq 0) { '｜日志里没有一句话说明这个值没生效（产物 rc=0 = 用户以为调了）' } else { '' }) }))
}
''
$c = @{ live = 0; inert = 0; rejected = 0; undecidable = 0; nocmd = 0; missing = 0; crashed = 0; gap = 0 }
foreach ($x in $res) { $c[$x.st] = $c[$x.st] + 1 }
'---- 旋钮活性明细桶: 活={0} 死(两端都没发)={1} 被拒(已播报)={2} 未判(夹具无判别力)={3} 未判(读不到命令行)={4} 两端未产出={5} 缺口(未做)={6} 合计={7} ----' -f `
  $c['live'], $c['inert'], $c['rejected'], $c['undecidable'], $c['nocmd'], $c['missing'], $c['gap'], $res.Count
# 规范汇总行（接进清单时必须有一条 `PASS=n FAIL=m`：运行器的**零断言闸**只认这个形状，
#   否则"一条断言都没跑但 exit=0"和"跑了 20 臂"在日志里长得一样）。口径与上面的记账一一对应：
#   FAIL = 会 exit 1 的四桶（死 / 两端未产出 / 读不到命令行 / **崩溃≠诚实**（2026-10-03 加））
#          + 下面那两条下限/比例闸（活臂数下限、未判比例上限）；
#   越界被拒 = rejected（高值是编码器不认的**越界值**且被响亮拒绝 ⇒ 本臂无差分可用，单列不判色；
#     A-20 修复时 float 已从下拉移除，只剩 CLI/历史预设能送进来，见 `MainWindow.xaml:725-728`）；
#   未判 = undecidable（夹具两极端值产物相同 ⇒ 这一臂没有判别力，不拿它编结论）；
#   缺口 = gap（**登记在清单里的覆盖缺口**，本批没做那两趟转换 ⇒ 既不绿也不红，只许被看见）。
# ⚠ **下限/比例闸**（2026-10-03 修 S4）：上面那道"臂数 == 应有臂数"只保证**每条臂都判了**，
#   不保证**任何一条臂有判别力**。旧写法下，若夹具退化成纯色图（脚本头 :102-103 自陈"18 条里
#   12 条报产物相同"）⇒ 十几条全落 `undecidable` + JXR 那臂恒活 ⇒ `PASS=1 FAIL=0` 就 **exit 0**，
#   而汇总行还写着"未判(不计入)=18"，属"把判不出编成结论"。
#   ⇒ 补两条闸：① 活臂数 ≥ `$minLive`（取值口径见下面 `$minLive` 处的专段：**实测值 − 明示余量**，
#     不许贴着实测写）⇒ 全未判不再算绿；② 未判(夹具无判别力) 数 ≤ 已判臂数的 1/3 ⇒ 未判不许**淹没**判据
#     （这条是**比例闸**而不是绝对数 ⇒ 天然带余量，不需要再留额外的绝对余量）。
#     两条都计入 `$nFail` ⇒ 汇总行与 exit code 一致。
#   变异验证：把夹具换成纯色 PNG（脚本头自陈的退化形状）⇒ 旧写法 exit 0（PASS=n FAIL=0，假绿），
#     加闸后 ⇒ "活臂数下限" 与/或 "未判比例上限" 记 FAIL 并 exit 1 ⇒ 确实转红。
$judgedArms = $c['live'] + $c['inert'] + $c['rejected'] + $c['undecidable'] + $c['nocmd'] + $c['missing']
# ⚠ `$minLive` 取值口径（2026-10-04 修 P1-5；**改这条之前必读，不在别处重复本段**）──────────────
#   本闸 2026-10-03 引入时写的是 `$minLive = 19`，**恰等于当时实测的活臂数 ⇒ 余量 0**
#   ⇒ 与本轮刚治好的 CJK 门禁（`$BASE_CS_UI` 贴着实测写、换个机子/换个 ffmpeg 版本就假红）是**同一个形状**。
#   实测后果：把夹具换成脚本头 :118-119 自陈的纯色退化形状后，活臂掉到 **18** ⇒ 这条闸立刻红
#   （`FAIL 活臂数下限：活=18 < 19`），尽管当时红的**原因本身是真的**（gif 臂真 inert），
#   它仍说明这条闸没有为正常的臂数波动留任何余量。
#   ⇒ 现改为「**实测值 − 明示余量**」：
#       实测活臂数  = 19（2026-10-04 实测，本机 Release 产物 + `publish/PLAN` 工具链 + `src_8bit.png`
#                     照片样夹具；同批读数：被拒 1、未判 1、缺口 1、合计 22）
#       明示余量    = 2
#       $minLive    = 19 − 2 = **17**
#     余量为什么留 2：本套件共 22 臂（20 旋钮臂 + 3 点名臂 − 1 缺口），**单臂/成对波动**的来源有三类 ——
#       ① 后端/工具链换版：同一旋钮的命令行形态会变（实测 `--avif-tune IQ` 走 `-aom-params tune=iq`，
#          另 `--avif-tune psnr` 走 `-tune psnr`），libaom/cjxl 换版后某臂的 `$pat` 取不到值即少一条活臂；
#       ② 编码器默认值随版本变：原本能被本夹具区分的两端变成同产物 ⇒ 该臂从 live 落 undecidable；
#       ③ JXL / GIF / AVIF 这类对输入内容敏感的编码器：换一张**合法**的照片样夹具就可能掉 1 条。
#     2 条足够吸收上面这些正常漂移；但**远小于整批塌方**的幅度（真事故一次掉 5 条以上，那时**应该**红）
#     ⇒ 这是一条既不许喊狼来了、也不许被当成免罚额度的刀口。
#   ⚠ **改夹具 / 换机 / 换 PLAN 工具链 / 加臂减臂时必须先看这里**：先在新环境量出实测活臂数，
#     再按「实测 − 2」重算；**严禁**把 `$minLive` 直接改成"刚才跑出来的那个数"（那又是余量 0）。
#     红的时候先读上面三类来源判性质：是正常漂移（⇒ 调夹具/重新登记），还是有臂真死了（⇒ 修产品，不是抬闸）。
#   ⚠ 同类自查（2026-10-04 全文扫过一遍）：本脚本里其它写死的数（`-q 10/95`、`--webp-compression 0/6`、
#     `--png-dpi 72/1200` 等）是**夹具取值**、不是通过阈值；`$maxUndecidable` 与 `$wantArms` 是**按结构算出**
#     （已判臂数的 1/3 / `$rows.Count + $naming.Count`）⇒ 不属"照实测写死"的形状，**唯一一处需留余量的就是本值**。
$minLive = 17
$maxUndecidable = [int][math]::Floor($judgedArms / 3)
$boundFail = 0
if ($c['live'] -lt $minLive) {
  $boundFail = $boundFail + 1
  'FAIL  活臂数下限：活={0} < {1} ⇒ 判据整体失去判别力（全未判不许算绿）' -f $c['live'], $minLive
}
if ($c['undecidable'] -gt $maxUndecidable) {
  $boundFail = $boundFail + 1
  'FAIL  未判比例上限：未判={0} > 已判臂数 {1} 的 1/3（={2}）⇒ 未判正在淹没判据' -f $c['undecidable'], $judgedArms, $maxUndecidable
}
$nFail = $c['inert'] + $c['missing'] + $c['nocmd'] + $c['crashed'] + $boundFail
'---- 旋钮活性: PASS={0} FAIL={1} 越界被拒(不计入)={2} 未判(不计入)={3} 缺口(未做)={4} 合计={5} ----' -f `
  $c['live'], $nFail, $c['rejected'], $c['undecidable'], $c['gap'], $res.Count
$wantArms = $rows.Count + $naming.Count
if ($res.Count -ne $wantArms) { "⚠ 判据条数 $($res.Count) != 应有臂数 $wantArms ⇒ 有臂没判（记红，不给绿）"; exit 1 }
$hard = @($res | Where-Object { $_.st -eq 'inert' -or $_.st -eq 'missing' -or $_.st -eq 'nocmd' -or $_.st -eq 'crashed' })
if ($hard.Count -or $boundFail) {
  '--- 判红/待补明细 ---'
  foreach ($x in $hard) { '   ' + $x.n + ' :: ' + $x.det }
  if ($boundFail) { '   （下限/比例闸，见上方 FAIL 行）' }
  exit 1
}
exit 0
