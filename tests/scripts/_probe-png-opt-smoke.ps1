# =============================================================================
# _probe-png-opt-smoke.ps1 — PNG 极限压缩 14 臂冒烟探针（plan §3.1 T1 / R1.4）
#
# 契约互指：
#   C-PIXEL@1     像素逐位等价判据 —— 本脚本是其「冒烟内联简化版」：
#                 `ffmpeg -v error -i <png> -f md5 -` 对输入与输出各跑一次，
#                 解码帧 md5 逐字符相等 => pixel_equal=true；ffmpeg exit≠0 或
#                 stderr 出现错误 => false。IHDR 不变判据（bit depth/color type）
#                 与第二解码器 Pillow 判据属 C-PIXEL@1 完整版，由 T4 全量实现，
#                 本冒烟不重复。
#   C-TOOLLIST@1  14 臂定义与 tools.json schema —— 本脚本输出的
#                 smoke-result.json 的每条记录对应 tools.json 的 `smoke` 字段。
#   C-REFILTER@1  对照臂 harness CLI —— 臂 12/13/14（libdeflate-12/zlib9/zlibng）
#                 在本脚本中以「内联简化 harness」执行（幂等产物：只支持冒烟
#                 素材形态 8-bit RGB 非隔行；正式 harness 由 T3 交付
#                 tests/scripts/_make-png-refilter.py，T5 全量前端到端覆盖）。
#                 harness 重打包语义（unfilter→paeth→重压→非 IDAT 原样保留）
#                 与 C-REFILTER@1 一致；正确性由判据③（解码 md5 等价）直接证明。
#   plan.md §3.1 T1 / R1.2–R1.4 / R5.1–R5.2
#
# 三判据（R1.4）：①exit 0 ②产物合法 PNG（签名+IHDR+IEND 结构完整）
#                ③解码 md5 逐位等价。三判据全过才允许进入全量 benchmark。
#
# 落盘纪律（R5.1）：素材、每臂工作目录、harness 副本、ffmpeg stderr 取证
#   全部落 tests/output/_tmp/pngopt-b1/smoke/（产物区）；绝不写 %TEMP%/C:\Temp。
#   本脚本为 tracked 候选判据件（tests/scripts 平铺，_probe-* 前缀，R5.2；
#   本轮 benchmark 属探针取证性质，不接 _run-step3-gates.ps1 清单，AGENTS §2）。
#
# 退出码：0 = 无 fail（pass+skip 覆盖 14 臂）；2 = 至少一臂 fail。
# 用法：pwsh -NoProfile -File tests/scripts/_probe-png-opt-smoke.ps1
#       [-ToolsRoot <dir>] [-SmokeDir <dir>] [-Ffmpeg <path>]
# =============================================================================
param(
    [string]$ToolsRoot = "",
    [string]$SmokeDir = "",
    [string]$Ffmpeg = ""
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path

# ---- 路径解析（R5.1：批次根 plan 钉死 tests/output/_tmp/pngopt-b1） ----
if (-not $ToolsRoot) { $ToolsRoot = Join-Path $RepoRoot 'tests/output/_tmp/pngopt-b1/tools' }
if (-not $SmokeDir)  { $SmokeDir  = Join-Path $RepoRoot 'tests/output/_tmp/pngopt-b1/smoke' }
if (-not $Ffmpeg)    { $Ffmpeg    = Join-Path $RepoRoot 'publish/PLAN/ffmpeg-full/ffmpeg.exe' }

if (-not (Test-Path $Ffmpeg)) { Write-Error "随包 ffmpeg 不存在: $Ffmpeg"; exit 2 }
New-Item -ItemType Directory -Force -Path $SmokeDir | Out-Null

# ---- msys2 运行时 dll 注入 PATH（optipng/pngcrush/zopflipng 依赖） ----
$rtDir = Join-Path $ToolsRoot '_msys2-runtime'
if (Test-Path $rtDir) { $env:PATH = "$rtDir;" + $env:PATH }

# ---- 冒烟素材：8-bit RGB 512x384（R1.4 指定形态），存在则复用，否则 lavfi 生成 ----
$smokePng = Join-Path $SmokeDir 'B01-smoke.png'
if (-not (Test-Path $smokePng)) {
    # 确定性：testsrc2 固定内容 + 只取第 1 帧（本机随包 ffmpeg 的 testsrc2 无 n 选项，
    # 帧数由 -frames:v 1 控制；与 C-PMATRIX@1 的 n=1 写法语义等价，见 tools.json 注记）
    $genArgs = @('-y', '-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=512x384',
                 '-frames:v', '1', '-pix_fmt', 'rgb24', '-c:v', 'png', '-compression_level', '9', $smokePng)
    & $Ffmpeg @genArgs 2>&1 | ForEach-Object { Write-Output "ffmpeg(gen): $_" }
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $smokePng)) {
        Write-Output "[material] exit=$LASTEXITCODE valid_png=0 pixel_equal=0  (ffmpeg lavfi 生成失败)"
        exit 2
    }
}
$smokeHash = (Get-FileHash $smokePng -Algorithm SHA256).Hash.Substring(0, 16)
$smokeBytes = (Get-Item $smokePng).Length
Write-Output "[material] file=$smokePng sha16=$smokeHash bytes=$smokeBytes"

# ---- PNG 结构校验（判据②）：签名 + IHDR 首块 + chunk 链自洽 + IEND 收尾 ----
function Test-ValidPng {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return $false }
    $b = [IO.File]::ReadAllBytes($Path)
    $sig = [byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
    if ($b.Length -lt 45) { return $false }
    for ($i = 0; $i -lt 8; $i++) { if ($b[$i] -ne $sig[$i]) { return $false } }
    # 首 chunk 必须 IHDR（len=13）
    if ($b[8] -ne 0 -or $b[9] -ne 0 -or $b[10] -ne 0 -or $b[11] -ne 13) { return $false }
    $type0 = [Text.Encoding]::ASCII.GetString($b, 12, 4)
    if ($type0 -ne 'IHDR') { return $false }
    # 遍历 chunk 链，要求终块 IEND 且恰好在文件尾
    $pos = 8
    while ($true) {
        if ($pos + 8 -gt $b.Length) { return $false }
        $len = ([uint32]$b[$pos] -shl 24) -bor ([uint32]$b[$pos+1] -shl 16) -bor ([uint32]$b[$pos+2] -shl 8) -bor [uint32]$b[$pos+3]
        $typ = [Text.Encoding]::ASCII.GetString($b, $pos + 4, 4)
        $pos += 12 + $len
        if ($typ -eq 'IEND') { break }
        if ($pos -gt $b.Length) { return $false }
    }
    return ($pos -eq $b.Length)
}

# ---- 解码 md5 对撞（判据③，C-PIXEL@1 内联简化版） ----
function Get-DecodeMd5 {
    param([string]$Path, [string]$ErrLog)
    $out = & $Ffmpeg '-v' 'error' '-i' $Path '-f' 'md5' '-' 2>$ErrLog
    if ($LASTEXITCODE -ne 0) { return $null }
    $line = ($out | Select-Object -First 1)
    if ($line -match 'MD5=([0-9a-fA-F]{32})') { return $Matches[1].ToLower() }
    return $null
}

# ---- 内联对照臂 harness（C-REFILTER@1 冒烟简化版，Python 3.12 标准库 + ctypes） ----
# 写盘位置在产物区（R5.1），不落 %TEMP%。
$RefilterPy = Join-Path $SmokeDir '_refilter_inline.py'
$refilterCode = @'
# C-REFILTER@1 冒烟内联简化版 harness（正式版由 T3 交付 _make-png-refilter.py）
# 用法: python _refilter_inline.py --in X.png --out Y.png --impl zlib9|libdeflate12|zlibng --dll-root DIR
# 语义: 解析 PNG(chunk CRC 校验) -> 拼 IDAT 解压 -> unfilter -> 统一 paeth 重滤
#       -> --impl 指定 deflate 实现重压 -> 重写 PNG(IHDR/非 IDAT 原样保留, IDAT 合一, IEND)
# 冒烟版边界: 只支持 8-bit RGB(colortype 2) 非隔行；其余形态 exit 2。
# 退出码: 0 成功; 2 输入非法/形态不支持; 3 dll 缺失或符号不可用; 4 CRC/结构损坏; 5 压缩异常
import sys, os, zlib, struct, ctypes

def die(code, msg):
    sys.stderr.write(msg + "\n")
    sys.exit(code)

def argget(name, default=None):
    a = sys.argv
    if name in a:
        return a[a.index(name) + 1]
    return default

inp = argget("--in"); outp = argget("--out"); impl = argget("--impl"); dllroot = argget("--dll-root", "")
if not inp or not outp or not impl:
    die(2, "missing --in/--out/--impl")
if impl not in ("zlib9", "libdeflate12", "zlibng"):
    die(2, "unknown impl: " + impl)

try:
    data = open(inp, "rb").read()
except OSError as e:
    die(2, "cannot read input: %s" % e)
if data[:8] != b"\x89PNG\r\n\x1a\n":
    die(2, "not a PNG signature")
pos = 8
ihdr = None
non_idat = []
idat = bytearray()
while True:
    if pos + 8 > len(data):
        die(4, "truncated chunk header")
    ln = struct.unpack(">I", data[pos:pos+4])[0]
    typ = data[pos+4:pos+8]
    if pos + 12 + ln > len(data):
        die(4, "truncated chunk body")
    payload = data[pos+8:pos+8+ln]
    crc = struct.unpack(">I", data[pos+8+ln:pos+12+ln])[0]
    if zlib.crc32(typ + payload) & 0xFFFFFFFF != crc:
        die(4, "chunk CRC mismatch at %s" % typ)
    if typ == b"IHDR":
        ihdr = payload
    elif typ == b"IDAT":
        idat += payload
    elif typ == b"IEND":
        if pos + 12 + ln != len(data):
            die(4, "bytes after IEND")
        break
    else:
        non_idat.append((typ, payload))
    pos += 12 + ln
if ihdr is None or len(ihdr) != 13:
    die(2, "missing/short IHDR")
w, h, bd, ct, comp, filt, inter = struct.unpack(">IIBBBBB", ihdr)
if bd != 8 or ct != 2 or inter != 0:
    die(2, "smoke-inline harness only supports 8-bit RGB non-interlaced "
           "(formal C-REFILTER@1 harness covers full matrix)")
bpp = 3
try:
    raw = zlib.decompress(bytes(idat))
except zlib.error as e:
    die(4, "IDAT decompress failed: %s" % e)
stride = w * bpp
if len(raw) != (stride + 1) * h:
    die(4, "raw scanline size mismatch")

def paeth(a, b, c):
    p = a + b - c
    pa = abs(p - a); pb = abs(p - b); pc = abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    if pb <= pc:
        return b
    return c

# unfilter（filter type 0-4 全支持）
out = bytearray()
prev = bytearray(stride)
for r in range(h):
    ft = raw[r * (stride + 1)]
    line = bytearray(raw[r*(stride+1)+1 : r*(stride+1)+1+stride])
    if ft == 0:
        pass
    elif ft == 1:
        for i in range(bpp, stride):
            line[i] = (line[i] + line[i-bpp]) & 0xFF
    elif ft == 2:
        for i in range(stride):
            line[i] = (line[i] + prev[i]) & 0xFF
    elif ft == 3:
        for i in range(stride):
            a = line[i-bpp] if i >= bpp else 0
            line[i] = (line[i] + ((a + prev[i]) >> 1)) & 0xFF
    elif ft == 4:
        for i in range(stride):
            a = line[i-bpp] if i >= bpp else 0
            c = prev[i-bpp] if i >= bpp else 0
            line[i] = (line[i] + paeth(a, prev[i], c)) & 0xFF
    else:
        die(4, "bad filter type %d at row %d" % (ft, r))
    out += bytes(line)
    prev = line

# 统一 paeth 重滤
filt_out = bytearray()
prev = bytearray(stride)
for r in range(h):
    line = out[r*stride:(r+1)*stride]
    row = bytearray(stride + 1)
    row[0] = 4
    for i in range(stride):
        a = line[i-bpp] if i >= bpp else 0
        c = prev[i-bpp] if i >= bpp else 0
        row[1+i] = (line[i] - paeth(a, prev[i], c)) & 0xFF
    filt_out += row
    prev = bytearray(line)

# deflate 重压（三实现）
src = bytes(filt_out)
if impl == "zlib9":
    co = zlib.compressobj(9)
    z = co.compress(src) + co.flush()
elif impl == "libdeflate12":
    if not dllroot:
        die(3, "--dll-root required for libdeflate12")
    p = os.path.join(dllroot, "libdeflate.dll")
    try:
        dll = ctypes.CDLL(p)
    except OSError as e:
        die(3, "cannot load libdeflate.dll: %s" % e)
    try:
        alloc = dll.libdeflate_alloc_compressor
        zc = dll.libdeflate_zlib_compress
        free = dll.libdeflate_free_compressor
    except AttributeError as e:
        die(3, "libdeflate symbol missing: %s" % e)
    alloc.restype = ctypes.c_void_p; alloc.argtypes = [ctypes.c_int]
    zc.restype = ctypes.c_size_t
    zc.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_size_t, ctypes.c_char_p, ctypes.c_size_t]
    free.argtypes = [ctypes.c_void_p]
    comp = alloc(12)  # level 12（libdeflate 压缩级别 1..12）
    if not comp:
        die(3, "libdeflate_alloc_compressor failed")
    buf = ctypes.create_string_buffer(len(src) + 1024)
    n = zc(comp, src, len(src), buf, len(buf))
    free(comp)
    if n == 0:
        die(5, "libdeflate_zlib_compress failed")
    z = buf.raw[:n]
else:  # zlibng（zlib-ng ZLIB_COMPAT 构建，zlib 兼容符号；Win64 sizeof(z_stream)=88，zlibVersion='1.3.1.zlib-ng'）
    if not dllroot:
        die(3, "--dll-root required for zlibng")
    p = os.path.join(dllroot, "zlib1.dll")
    try:
        dll = ctypes.CDLL(p)
    except OSError as e:
        die(3, "cannot load zlib-ng zlib1.dll: %s" % e)
    for sym in ("deflateInit2_", "deflate", "deflateEnd", "zlibVersion"):
        if not hasattr(dll, sym):
            die(3, "zlib-ng compat symbol missing: " + sym)
    class ZStream(ctypes.Structure):
        _fields_ = [
            ("next_in", ctypes.c_void_p), ("avail_in", ctypes.c_uint), ("total_in", ctypes.c_ulong),
            ("next_out", ctypes.c_void_p), ("avail_out", ctypes.c_uint), ("total_out", ctypes.c_ulong),
            ("msg", ctypes.c_char_p), ("state", ctypes.c_void_p),
            ("zalloc", ctypes.c_void_p), ("zfree", ctypes.c_void_p), ("opaque", ctypes.c_void_p),
            ("data_type", ctypes.c_int), ("adler", ctypes.c_ulong), ("reserved", ctypes.c_ulong),
        ]
    dll.deflateInit2_.restype = ctypes.c_int
    dll.deflateInit2_.argtypes = [ctypes.POINTER(ZStream), ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                  ctypes.c_int, ctypes.c_int, ctypes.c_char_p, ctypes.c_int]
    dll.deflate.restype = ctypes.c_int
    dll.deflate.argtypes = [ctypes.POINTER(ZStream), ctypes.c_int]
    dll.deflateEnd.restype = ctypes.c_int
    dll.deflateEnd.argtypes = [ctypes.POINTER(ZStream)]
    dll.zlibVersion.restype = ctypes.c_char_p
    s = ZStream()
    rc = dll.deflateInit2_(ctypes.byref(s), 9, 8, 15, 8, 0, dll.zlibVersion(), ctypes.sizeof(ZStream))
    if rc != 0:
        die(3, "zlib-ng deflateInit2_ failed rc=%d" % rc)
    srcbuf = ctypes.create_string_buffer(src)
    outbuf = ctypes.create_string_buffer(len(src) + 1024)
    s.next_in = ctypes.cast(srcbuf, ctypes.c_void_p); s.avail_in = len(src)
    s.next_out = ctypes.cast(outbuf, ctypes.c_void_p); s.avail_out = len(outbuf)
    rc = dll.deflate(ctypes.byref(s), 4)  # Z_FINISH
    total = s.total_out
    dll.deflateEnd(ctypes.byref(s))
    if rc != 1:
        die(5, "zlib-ng deflate rc=%d" % rc)
    z = outbuf.raw[:total]

# 重写 PNG：签名 + IHDR 原样 + 非 IDAT 原序原样 + 单 IDAT + IEND
res = bytearray(b"\x89PNG\r\n\x1a\n")
def chunk(typ, payload):
    res.extend(struct.pack(">I", len(payload)))
    res.extend(typ)
    res.extend(payload)
    res.extend(struct.pack(">I", zlib.crc32(typ + payload) & 0xFFFFFFFF))
chunk(b"IHDR", ihdr)
for typ, payload in non_idat:
    chunk(typ, payload)
chunk(b"IDAT", z)
chunk(b"IEND", b"")
try:
    open(outp, "wb").write(bytes(res))
except OSError as e:
    die(5, "cannot write output: %s" % e)
print("refilter[%s] ok: out=%d bytes" % (impl, len(res)))
'@
[IO.File]::WriteAllText($RefilterPy, $refilterCode.Replace("`r`n", "`n"))

# ---- 14 臂定义（C-TOOLLIST@1；CLI flag 均经 --help/--usage 实证锁定） ----
$oxipng   = Join-Path $ToolsRoot 'oxipng/extract/oxipng-10.2.1-x86_64-pc-windows-msvc/oxipng.exe'
$ect      = Join-Path $ToolsRoot 'ect/extract/ect.exe'
$optipng  = Join-Path $ToolsRoot 'optipng/extract/mingw64/bin/optipng.exe'
$pngcrush = Join-Path $ToolsRoot 'pngcrush/extract/mingw64/bin/pngcrush.exe'
$advpng   = Join-Path $ToolsRoot 'advancecomp/extract/advpng.exe'
$pngout   = Join-Path $ToolsRoot 'pngout/pngout.exe'
$pingo    = Join-Path $ToolsRoot 'pingo/extract/pingo.exe'
$zopflipng= Join-Path $ToolsRoot 'zopfli/extract/mingw64/bin/zopflipng.exe'
$libdefDll= Join-Path $ToolsRoot 'libdeflate/extract/libdeflate.dll'
$zlibngDll= Join-Path $ToolsRoot 'zlibng/extract/bin/zlib1.dll'

$arms = @(
    @{ id='oxipng-default';  exe=$oxipng;    args=@('<in>','--out','<out>'); inplace=$false; impl=$null },
    @{ id='oxipng-max';      exe=$oxipng;    args=@('<in>','-o','max','--out','<out>'); inplace=$false; impl=$null },
    @{ id='oxipng-zopfli15'; exe=$oxipng;    args=@('<in>','-o','max','-z','--zi','15','--out','<out>'); inplace=$false; impl=$null },
    @{ id='oxipng-zopfli30'; exe=$oxipng;    args=@('<in>','-o','max','-z','--zi','30','--out','<out>'); inplace=$false; impl=$null },
    @{ id='zopfli-png';      exe=$zopflipng; args=@('<in>','<out>','-y'); inplace=$false; impl=$null },
    @{ id='ect-9';           exe=$ect;       args=@('-9','<in>'); inplace=$true; impl=$null },
    @{ id='pngout';          exe=$pngout;    args=@('<in>','<out>'); inplace=$false; impl=$null },
    @{ id='pingo';           exe=$pingo;     args=@('-lossless','<in>'); inplace=$true; impl=$null },
    @{ id='optipng-o7';      exe=$optipng;   args=@('<in>','-o7'); inplace=$true; impl=$null },
    @{ id='pngcrush-brute';  exe=$pngcrush;  args=@('-brute','<in>','<out>'); inplace=$false; impl=$null },
    @{ id='advcomp-4';       exe=$advpng;    args=@('-4','-z','<in>'); inplace=$true; impl=$null },
    @{ id='libdeflate-12';   exe=$null;      args=$null; inplace=$false; impl='libdeflate12' },
    @{ id='zlib9';           exe=$null;      args=$null; inplace=$false; impl='zlib9' },
    @{ id='zlibng';          exe=$null;      args=$null; inplace=$false; impl='zlibng' }
)

# ---- 对照臂 impl 的 dll 可用性前置检查（缺件 => 该臂 SKIP，R1.3） ----
function Test-DllPresent { param([string]$Path)
    if ($Path -and (Test-Path $Path)) { return $true } else { return $false }
}

$results = @()
$failCount = 0; $skipCount = 0; $passCount = 0

foreach ($arm in $arms) {
    $id = $arm.id
    $workDir = Join-Path $SmokeDir $id
    if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $workDir | Out-Null
    $errLog = Join-Path $workDir 'stderr.log'
    $inCopy = Join-Path $workDir 'in.png'
    Copy-Item $smokePng $inCopy -Force

    $notes = ''
    $exitCode = $null; $validPng = $false; $pixelEqual = $false
    $skip = $null; $skipReason = $null
    $outPath = $null

    try {
        if ($arm.impl) {
            # 对照臂（C-REFILTER@1 冒烟内联简化版）
            $dllOk = $true; $why = ''
            if ($arm.impl -eq 'libdeflate12') { if (-not (Test-DllPresent $libdefDll)) { $dllOk = $false; $why = "libdeflate.dll missing at $libdefDll" } }
            if ($arm.impl -eq 'zlibng')       { if (-not (Test-DllPresent $zlibngDll)) { $dllOk = $false; $why = "zlib-ng zlib1.dll missing at $zlibngDll" } }
            if (-not $dllOk) {
                $skip = $true; $skipReason = $why
            } else {
                $outPath = Join-Path $workDir 'out.png'
                $dllRoot = if ($arm.impl -eq 'libdeflate12') { Split-Path $libdefDll } elseif ($arm.impl -eq 'zlibng') { Split-Path $zlibngDll } else { '' }
                $pyArgs = @($RefilterPy, '--in', $inCopy, '--out', $outPath, '--impl', $arm.impl)
                if ($dllRoot) { $pyArgs += @('--dll-root', $dllRoot) }
                $pyOut = & python @pyArgs 2>$errLog
                $exitCode = $LASTEXITCODE
                if ($pyOut) { $notes = (($pyOut | Out-String).Trim()) }
            }
        }
        elseif (-not (Test-Path $arm.exe)) {
            $skip = $true; $skipReason = "tool binary missing at $($arm.exe)"
        }
        else {
            # 实例化 args_template：原位臂只有 <in>；两文件臂 <in>/<out>
            $realArgs = @()
            $outPath = Join-Path $workDir 'out.png'
            foreach ($a in $arm.args) {
                if ($a -eq '<in>') { $realArgs += $inCopy }
                elseif ($a -eq '<out>') { $realArgs += $outPath }
                else { $realArgs += $a }
            }
            if ($arm.inplace) { $outPath = $inCopy }  # 原位臂产物 = 工作副本本体
            $stdOutLog = Join-Path $workDir 'stdout.log'
            & $arm.exe @realArgs 1>$stdOutLog 2>$errLog
            $exitCode = $LASTEXITCODE
            $so = (Get-Content $stdOutLog -Raw -ErrorAction SilentlyContinue)
            if ($so) { $notes = $so.Trim() }
        }

        # 三判据（R1.4）
        if ($null -ne $skip) {
            $skipCount++
            Write-Output "[$id] exit=N/A valid_png=0 pixel_equal=0 SKIP: $skipReason"
        }
        else {
            $okExit = ($exitCode -eq 0)
            $validPng = if ($outPath -and (Test-Path $outPath)) { Test-ValidPng $outPath } else { $false }
            if (-not $validPng -and $outPath -and (Test-Path $outPath)) { $notes = ($notes + ' | out exists but PNG structure invalid').Trim(' |') }
            if (-not (Test-Path $outPath)) { $notes = ($notes + ' | output file not written (tool declined write / kept input)').Trim(' |') }
            if ($validPng) {
                $md5In = Get-DecodeMd5 -Path $smokePng -ErrLog (Join-Path $workDir 'md5-in.err.log')
                $md5Out = Get-DecodeMd5 -Path $outPath -ErrLog (Join-Path $workDir 'md5-out.err.log')
                if ($md5In -and $md5Out -and ($md5In -eq $md5Out)) { $pixelEqual = $true }
                elseif (-not $md5In -or -not $md5Out) { $notes = ($notes + ' | ffmpeg decode md5 failed (see *-md5-*.err.log)').Trim(' |') }
                else { $notes = ($notes + " | md5 mismatch in=$md5In out=$md5Out").Trim(' |') }
            }
            $verdict = if ($okExit -and $validPng -and $pixelEqual) { 'PASS' } else { 'FAIL' }
            if ($verdict -eq 'PASS') { $passCount++ } else { $failCount++ }
            Write-Output ("[$id] exit=$exitCode valid_png=$(if($validPng){1}else{0}) pixel_equal=$(if($pixelEqual){1}else{0}) $verdict" + $(if ($notes) { "  notes: $notes" }))
        }
    }
    catch {
        $failCount++
        Write-Output "[$id] exit=N/A valid_png=0 pixel_equal=0 FAIL  notes: exception $($_.Exception.Message)"
        $exitCode = $null
    }

    $results += [ordered]@{
        arm_id = $id
        exit_code = $exitCode
        valid_png = $validPng
        pixel_equal = $pixelEqual
        skip = $skip
        skip_reason = $skipReason
        notes = $notes
        ran_at = (Get-Date -Format 'yyyy-MM-ddTHH:mm:ss')
    }
}

# ---- 汇总与读数落盘（tools.json 的 smoke 字段来源） ----
$summary = "smoke summary: pass=$passCount fail=$failCount skip=$skipCount total=$($arms.Count)"
Write-Output $summary
$resultPath = Join-Path $SmokeDir 'smoke-result.json'
$results | ConvertTo-Json -Depth 4 | Set-Content -Path $resultPath -Encoding utf8
Write-Output "[result] file=$resultPath"

if ($failCount -gt 0) { exit 2 } else { exit 0 }