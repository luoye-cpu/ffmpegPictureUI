# ═══════════════════════════════════════════════════════════════════════════
# _probe-color-matrix-scan.ps1 — 全格式矩阵承载扫描（第 98 条，2026-11-10 接线，T1）
#
# 判什么：color-matrix-options 计划 C-SCAN@1 契约的交付物生成器——对全部输出格式
#   （jpg/jpegli、png/apng、webp、avif、jxl、tiff、gif、dng、jxr、ppm ×
#   ffmpeg/引擎/cjxl/cjpegli/avifenc/JxrEncApp/dngtool 出口）完成**只读代码级**盘点，
#   产出「格式 × 出口 × 承载矩阵值」报告（report.md，人读）与机读事实表
#   （scan-facts.json，T5/T6/T7 消费）。**不修改任何 src/ 生产代码**；
#   **不跑长转码矩阵**（依据 = 代码阅读 + 工具 `-h` 自报表 + 在册门禁/AGENTS 读数）。
#
# 三把尺（全部 fail-closed）：
#   ① 代码锚点命中：事实表的每一处 file:line 证据都来自**当轮实读**的锚点
#      （Select-String 在当轮源码上动态解析行号——工作树未提交改动使行号漂移，
#      照抄计划书/上一轮行号属伪造证据）。任一锚点 miss ⇒ 红。
#   ② 工具自报表重跑：ffmpeg -h encoder=…（×5）+ cjxl --help -v -v + cjpegli --help -v -v
#      在当轮重新打印并落扫描目录；关键行（像素格式清单 / -xyb AVOption / --modular）
#      必须命中，工具缺失 ⇒ 红（同 verify-tool-detection 的 fail-closed 先例——
#      publish 整树 gitignore，缺失时点名重取方式）。
#   ③ 事实表键完整性：每行 {format, exit_path, matrix_support[], xyb_switch,
#      xyb_default, evidence[]} 六键齐、数组非空；JSON 写入后**回读**再验一遍；
#      格式覆盖集 == 11 格式（jpg/jpegli、png、apng、webp、avif、jxl、tiff、gif、
#      dng、jxr、ppm）。缺键/空数组/格式缺失 ⇒ 红。
#
# 附加条件断言（快照语义锁）：
#   ④ cjpegli 出口 --xyb 的**接线形态**（2026-11-13 重校准）：`sb.Append(" --xyb")` 恰好 1 处
#      + auto 守卫行 `var outMatrixTok = opts.ColorOutMatrix;` 在场。
#      ⚠ 旧判据是「CjpegliService.cs 里 'xyb' 命中 == 0」（锁「未接线」），其自身约定即
#        「T6 接线后重跑会红 = 预期行为」；T6 已于 0f6c11a（2026-11-11）落地 ⇒ 本轮按约定重校准。
#        重校准后**不用计数锁**（注释改动会误伤、接线后也没语义），改锁形态：
#        0 处 = 接线被删而事实表仍声明已接线；≥2 处 = 重复接线；守卫行缺失 = auto 档可能被污染。
#   ⑤ cjxl --help（-v -v）里 '--xyb' 命中数 == 0 —— 锁「cjxl v0.12.0 无显式 xyb
#      开关」。升级 cjxl 后重跑变红同理。
#
# 产物（AGENTS §1 归置契约：判据在 tests/scripts 平铺、产物在 tests/output）：
#   tests/output/_tmp/color-matrix-scan-20261110/report.md        （主报告，人读）
#   tests/output/_tmp/color-matrix-scan-20261110/scan-facts.json  （机读事实表）
#   tests/output/_tmp/color-matrix-scan-20261110/*.txt            （工具自报表日志）
#
# 临时件：TMP/TEMP/TMPDIR 钉到 tests/output/_tmp/round-tmp（AGENTS §3；裸跑调试
#   也必须钉同一个根——ffmpeg 子进程向系统临时目录写会被本机沙箱拦 exit -13）。
# ═══════════════════════════════════════════════════════════════════════════

$ErrorActionPreference = 'Continue'
Set-Location (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))   # → 仓库根
$repoRoot = (Get-Location).Path

$pass = 0; $fail = 0
function CK($ok, $msg) {
    if ($ok) { $script:pass++; Write-Output "  PASS $msg" }
    else     { $script:fail++; Write-Output "  FAIL $msg" }
}

# ── 产物目录 + TEMP 钉扎（幂等：运行器已钉时同根重钉无害）──────────────────────
$scanDir = Join-Path $repoRoot 'tests/output/_tmp/color-matrix-scan-20261110'
New-Item -ItemType Directory -Force -Path $scanDir | Out-Null
$roundTmp = Join-Path $repoRoot 'tests/output/_tmp/round-tmp'
New-Item -ItemType Directory -Force -Path $roundTmp | Out-Null
$env:TMP = $roundTmp; $env:TEMP = $roundTmp; $env:TMPDIR = $roundTmp

# ── HEAD（元数据，非判据；失败不致命）─────────────────────────────────────────
$headShort = 'unknown'
try { $headShort = (git rev-parse --short HEAD).Trim() } catch { }

# ═════════════════════════════════════════════════════════════════════════════
# ① 工具自报表（当轮重打印，落扫描目录；fail-closed）
# ═════════════════════════════════════════════════════════════════════════════
Write-Output '── ① 工具自报表（ffmpeg -h encoder / cjxl --help / cjpegli --help）──'
$ffPath  = Join-Path $repoRoot 'publish/PLAN/ffmpeg-full/ffmpeg.exe'
$cjxlPath    = Join-Path $repoRoot 'publish/PLAN/jxl/bin/cjxl.exe'
$cjpegliPath = Join-Path $repoRoot 'publish/PLAN/jxl/bin/cjpegli.exe'

$toolsOk = $true
foreach ($t in @(@('ffmpeg', $ffPath), @('cjxl', $cjxlPath), @('cjpegli', $cjpegliPath))) {
    if (-not (Test-Path $t[1])) {
        CK $false "工具在位: $($t[0]) → 缺失 $($t[1])（publish 整树 gitignore ⇒ fail-closed；重取见 pack.ps1 / 使用说明.txt）"
        $toolsOk = $false
    } else {
        CK $true "工具在位: $($t[0])"
    }
}

$ffVer = ''
if ($toolsOk) {
    # ffmpeg 编码器自报表 ×5（libjxl / mjpeg / libwebp / libaom-av1 / libsvtav1）
    foreach ($enc in @('libjxl', 'mjpeg', 'libwebp', 'libaom-av1', 'libsvtav1')) {
        $out = Join-Path $scanDir "ffmpeg-h-encoder-$enc.txt"
        & $ffPath -hide_banner -h "encoder=$enc" 2>&1 | Set-Content -LiteralPath $out -Encoding UTF8
    }
    & $ffPath -version 2>&1 | Select-Object -First 3 | Set-Content -LiteralPath (Join-Path $scanDir 'ffmpeg-version.txt') -Encoding UTF8
    $ffVer = ((Get-Content -LiteralPath (Join-Path $scanDir 'ffmpeg-version.txt') -TotalCount 1) -replace '^ffmpeg version ', '').Trim()
    # cjxl / cjpegli 自报表
    & $cjxlPath --version 2>&1    | Set-Content -LiteralPath (Join-Path $scanDir 'cjxl-version.txt') -Encoding UTF8
    & $cjxlPath --help -v -v 2>&1 | Set-Content -LiteralPath (Join-Path $scanDir 'cjxl-help-vv.txt') -Encoding UTF8
    & $cjxlPath --help 2>&1       | Set-Content -LiteralPath (Join-Path $scanDir 'cjxl-help.txt') -Encoding UTF8
    & $cjpegliPath --help -v -v 2>&1 | Set-Content -LiteralPath (Join-Path $scanDir 'cjpegli-help-vv.txt') -Encoding UTF8
    $cjxlVer = ((Get-Content -LiteralPath (Join-Path $scanDir 'cjxl-version.txt') -TotalCount 1) -replace '\s+\[.*$', '').Trim()
    CK ($cjxlVer -like 'cjxl v*') "cjxl 版本自报: $cjxlVer"
}

# ═════════════════════════════════════════════════════════════════════════════
# ② 代码锚点扫描（当轮实读行号；工作树行号漂移 ⇒ 一律动态定位，不照抄计划书）
# ═════════════════════════════════════════════════════════════════════════════
Write-Output '── ② 代码锚点扫描（动态行号定位）──'

# 每个锚点：file（仓库根相对路径）+ pattern（正则）+ desc（证据描述）
$Anchors = [ordered]@{
    # ── ColorSpaceRegistry.cs ──
    containeryuvmatrix_def = @{ file='src/FfmpegGui/Services/ColorSpaceRegistry.cs'; pattern='public static string\? ContainerYuvMatrix'; desc='ContainerYuvMatrix 定义（RGB→YUV 转换矩阵的容器兑现表）' }
    containeryuv_constant  = @{ file='src/FfmpegGui/Services/ColorSpaceRegistry.cs'; pattern='"jpg" or "jpeg" or "webp" \? "bt470bg" : null'; desc='jpg/jpeg/webp 恒 bt470bg、其余容器 null' }
    ffmpeg_colorspace_names= @{ file='src/FfmpegGui/Services/ColorSpaceRegistry.cs'; pattern='public static readonly string\[\] FfmpegColorSpaceNames'; desc='ffmpeg -colorspace 实际接受的常量名全集（含 ycgco/rgb）' }
    ffmpeg_input_names     = @{ file='src/FfmpegGui/Services/ColorSpaceRegistry.cs'; pattern='public static readonly string\[\] FfmpegInputMatrixNames'; desc='swscale 输入声明可安全使用的矩阵名白名单' }
    swscale_input_reject   = @{ file='src/FfmpegGui/Services/ColorSpaceRegistry.cs'; pattern='Impossible to convert between the formats'; desc='swscale 输入侧实测：ycgco/bt2020c/chroma-derived/ictcp 转换不可行' }
    swscale_input_accept   = @{ file='src/FfmpegGui/Services/ColorSpaceRegistry.cs'; pattern='bt470bg.+smpte170m.+rgb'; desc='swscale 输入侧实测：bt470bg(=smpte170m=rgb) 同组转换结果（5 组互异 md5 注记）' }
    normalize_out_matrix   = @{ file='src/FfmpegGui/Services/ColorSpaceRegistry.cs'; pattern='public static string\? NormalizeOutMatrixToken'; desc='输出矩阵 token 归一函数（bt601→smpte170m；auto/空白→null）——工作树当轮已在位' }
    primaries_bt709        = @{ file='src/FfmpegGui/Services/ColorSpaceRegistry.cs'; pattern='\["bt709"\] = new\(\) \{ Token = "bt709"'; desc='PrimariesTable：sRGB/BT.709 目标的 PrimariesToken="bt709"（⇒ 引擎 png 出口 plan.OutMatrix 非空）' }
    # ── FfmpegCommandBuilder.cs（legacy）──
    capm_output            = @{ file='src/FfmpegGui/Services/FfmpegCommandBuilder.cs'; pattern='var outMatrixToken = dec\.CapMatrix \?\? outputColorSpace'; desc='legacy 输出侧 -colorspace 的唯一取值点（capM 优先）' }
    isrgb_native           = @{ file='src/FfmpegGui/Services/FfmpegCommandBuilder.cs'; pattern='var isRgbNativeFmt = fmt0 is "png" or "tiff" or "apng" or "jxl"'; desc='RGB 原生格式名单（png/tiff/apng/jxl 跳过输出 -colorspace 块）' }
    input_decl             = @{ file='src/FfmpegGui/Services/FfmpegCommandBuilder.cs'; pattern='ToFfmpegInputMatrixName\(dec\.DeclMatrix\)'; desc='输入矩阵声明（-i 前 -colorspace，语义=源怎么读，本特性不动）' }
    pngcicp_matrix0_note   = @{ file='src/FfmpegGui/Services/FfmpegCommandBuilder.cs'; pattern='PngCicpService 对 PNG 恒写 matrix=0'; desc='PNG cICP 矩阵恒 0 的在册注记' }
    # ── RawColorPipeline.cs（引擎）──
    convmatrix_engine      = @{ file='src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs'; pattern='var convMatrix = plan\.OutMatrixVerdict == OutMatrixCapability\.Carried'; desc='引擎出口矩阵唯一取值点（Carried 决议 → plan.OutMatrix；否则 ContainerYuvMatrix 优先、回退 plan 推导）' }
    pixin_rgb              = @{ file='src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs'; pattern='string pixIn = plan\.OutBitDepth == 8 \? "rgb24" : "rgb48le"'; desc='引擎 raw 通路中间件恒 rgb24/rgb48le' }
    avif_label_split       = @{ file='src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs'; pattern='bool labelOnInput = ext is "avif" or "jxl"'; desc='AVIF/JXL 的 CICP 标签拆侧（矩阵留输出侧=转换系数+nclx；前置会被忽略）' }
    avif_pixfmt_gate       = @{ file='src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs'; pattern='if \(ext == "avif"\)'; desc='引擎 AVIF 出口的 -pix_fmt 判据块（chromaSet/bitDepth/srcBd>8 才写 YUV pix_fmt）' }
    jxl_native_note        = @{ file='src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs'; pattern='矩阵对 JXL 无意义'; desc='JXL RgbNative ⇒ OutMatrix 通常为空、留输出侧无副作用' }
    gif_chain              = @{ file='src/FfmpegGui/Services/ColorMapping/RawColorPipeline.cs'; pattern='GIF（2026-09-17 接入）'; desc='引擎 GIF 出口（调色板链 + alpha 第二输入）' }
    # ── ImageEncoderArgs.cs ──
    mapenginepixfmt_def    = @{ file='src/FfmpegGui/Services/ColorMapping/ImageEncoderArgs.cs'; pattern='public static string\? MapEnginePixFmt'; desc='引擎出口 pix_fmt 覆盖（只覆盖 jpg/jpeg；其余 null ⇒ rgb24/rgb48le 直喂）' }
    engine_jpg_only_note   = @{ file='src/FfmpegGui/Services/ColorMapping/ImageEncoderArgs.cs'; pattern='只覆盖 mjpeg'; desc='MapEnginePixFmt 只覆盖 mjpeg 的在册注记' }
    mappixfmt_def          = @{ file='src/FfmpegGui/Services/ColorMapping/ImageEncoderArgs.cs'; pattern='public static string MapPixFmt'; desc='legacy/AVIF 位的 pix_fmt 选择器（色度×位深→yuv420p/422/444[×位深]）' }
    avif_effective_bitdepth= @{ file='src/FfmpegGui/Services/ColorMapping/ImageEncoderArgs.cs'; pattern='public static int AvifEffectiveBitDepth'; desc='AVIF 有效位深（源位深驱动）' }
    unsupported_webp       = @{ file='src/FfmpegGui/Services/ColorMapping/ImageEncoderArgs.cs'; pattern='libwebp 仅接受 4:2:0 / bgra'; desc='libwebp 色度承接边界（无 4:4:4）' }
    unsupported_svt        = @{ file='src/FfmpegGui/Services/ColorMapping/ImageEncoderArgs.cs'; pattern='libsvtav1 仅接受 yuv420p / yuv420p10le'; desc='libsvtav1 像素格式边界（静默降级 4:4:4 ⇒ 引擎拒绝）' }
    engine_encodable       = @{ file='src/FfmpegGui/Services/ColorMapping/ImageEncoderArgs.cs'; pattern='public static bool IsEngineEncodable'; desc='引擎编码出口覆盖格式清单（唯一真值）' }
    # ── ColorTransformPlan.cs ──
    plan_outmatrix_assign  = @{ file='src/FfmpegGui/Services/ColorMapping/ColorTransformPlan.cs'; pattern='p\.OutMatrix = MatrixTokenFor\(dst\.PrimariesToken\)'; desc='plan.OutMatrix 由目标 primaries 推导（从不读用户值）' }
    matrix_token_for_def   = @{ file='src/FfmpegGui/Services/ColorMapping/ColorTransformPlan.cs'; pattern='private static string\? MatrixTokenFor'; desc='MatrixTokenFor 推导表（bt2020→bt2020nc；bt709/smpte432/smpte431→bt709；470bg/170m/470m/p22→smpte170m）' }
    # ── ColorStrategyPlanning.cs ──
    r4_noop_note           = @{ file='src/FfmpegGui/Services/ColorMapping/ColorStrategyPlanning.cs'; pattern='--color-matrix=\{it\.Manual\.Matrix\} 在本次交付中无承载'; desc='R4：用户显式 --color-matrix（输入声明）在引擎路径无承载的点名' }
    r4_engine_rgb_note     = @{ file='src/FfmpegGui/Services/ColorMapping/ColorStrategyPlanning.cs'; pattern='整个链路无 YUV 中转'; desc='引擎路径输入恒为已解码 RGB 像素（矩阵声明没有作用对象）' }
    # ── 外部编码器服务 ──
    cjpegli_build_args     = @{ file='src/FfmpegGui/Services/CjpegliService.cs'; pattern='public static string BuildCjpegliArguments'; desc='cjpegli 参数拼装（含显式输出矩阵分支，见 cjpegli_xyb_append）' }
    # 2026-11-13 重校准：T6（0f6c11a，2026-11-11「输出矩阵模式 + 一致性校验」）已在 cjpegli 出口接线 --xyb，
    #   旧事实表「零引用 = 未接线」过期 ⇒ 本锚点把接线点本身变成可引用的证据行。
    cjpegli_xyb_append     = @{ file='src/FfmpegGui/Services/CjpegliService.cs'; pattern='sb\.Append\(" --xyb"\)'; desc='cjpegli 出口 --xyb 接线点（仅 outMatrix=Cxyb 且 ResolveOutMatrixCapability=Carried 时发射）' }
    cjxl_modular_wiring    = @{ file='src/FfmpegGui/Services/CjxlService.cs'; pattern='opts\.JxlModular == true'; desc='cjxl --modular 接线点（--jxl-modular → --modular=1）' }
    cjxl_colorspace_map    = @{ file='src/FfmpegGui/Services/CjxlService.cs'; pattern='ColorEncodingHelper\.MapToCjxlColorSpace\(hdrMeta\)'; desc='cjxl 出口色彩描述（-x color_space，非 YUV 矩阵）' }
    jxr_build_args         = @{ file='src/FfmpegGui/Services/JxrService.cs'; pattern='public static string BuildArguments\('; desc='JxrEncApp 参数全集构造（-i/-o/-q/-d/-p/-l，无矩阵项）' }
    jxr_no_icc_note        = @{ file='src/FfmpegGui/Services/QueueProcessor.cs'; pattern='JxrEncApp 不支持嵌入色彩配置'; desc='JXR 色彩描述只能 exiftool 后置' }
    avifenc_args_build     = @{ file='src/FfmpegGui/Services/QueueProcessor.cs'; pattern='avifencArgs\.Append\(headArgs\)'; desc='gif→avif 两步法 avifenc 参数拼装（-q/-s/-j…，无矩阵项）' }
    jxr_avif_args          = @{ file='src/FfmpegGui/Services/QueueProcessor.cs'; pattern='jxr→avif\] Step 2: avifenc'; desc='jxr→avif 两步法（-d 10/-y 420，无矩阵项）' }
    # ── 模型 / CLI ──
    options_matrix_doc     = @{ file='src/FfmpegGui/Models/FfmpegOptions.cs'; pattern='的文档语义是\*\*输入矩阵声明\*\*'; desc='--color-matrix 文档语义=输入矩阵声明（非目标轴）' }
    cli_color_matrix_case  = @{ file='src/FfmpegGui/CliParser.cs'; pattern='case "color-matrix": options\.ColorMatrix = value; break;'; desc='CLI case "color-matrix"（新参数 --color-out-matrix 的邻区落点）' }
    # ── dngtool ──
    dng_colormatrix_sem    = @{ file='tools/src/dngtool/dngtool.cpp'; pattern='DNG 规范: 彩色 DNG 必需 ColorMatrix'; desc='DNG ColorMatrix1/2 语义（XYZ D50→参考相机空间，非 H.273）' }
    dng_rdspace            = @{ file='tools/src/dngtool/dngtool.cpp'; pattern='-rdspace <name>\s+-rdp final space'; desc='-rdp 渲染空间选择（srgb|p3|rec2020，RGB 线性空间）' }
    # ── PNG cICP ──
    pngcicp_matrix0_def    = @{ file='src/FfmpegGui/Services/PngCicpService.cs'; pattern='matrix 对 PNG 恒为 0'; desc='cICP 写入点：matrix 恒 0（RGB）、full 恒 1' }
}

$anchorLines = @{}
foreach ($k in $Anchors.Keys) {
    $a = $Anchors[$k]
    $full = Join-Path $repoRoot $a.file
    $line = $null
    if (Test-Path -LiteralPath $full) {
        $m = Select-String -LiteralPath $full -Pattern $a.pattern -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($m) { $line = [int]$m.LineNumber }
    }
    $anchorLines[$k] = $line
    if ($line) { CK $true ("锚点 {0} → {1}:{2}（{3}）" -f $k, $a.file, $line, $a.desc) }
    else       { CK $false ("锚点 {0} miss：pattern「{1}」在 {2} 零命中 ⇒ 事实表证据失效（代码被改须重校准）" -f $k, $a.pattern, $a.file) }
}

# ── 工具日志锚点 ──
$ToolAnchors = [ordered]@{
    libjxl_pixfmt  = @{ log='ffmpeg-h-encoder-libjxl.txt';   pattern='Supported pixel formats: rgb24 rgba rgb48le';        desc='libjxl 编码器像素格式全 RGB/gray 族（无 YUV 输入格式）' }
    libjxl_xyb     = @{ log='ffmpeg-h-encoder-libjxl.txt';   pattern='Use XYB-encoding for lossy images .*default 1';      desc='libjxl -xyb AVOption 存在且 default 1' }
    libjxl_modular = @{ log='ffmpeg-h-encoder-libjxl.txt';   pattern='-modular\s+<int>.*default 0';                        desc='libjxl -modular AVOption default 0' }
    mjpeg_pixfmt   = @{ log='ffmpeg-h-encoder-mjpeg.txt';    pattern='Supported pixel formats: yuvj420p yuvj422p yuvj444p yuv420p yuv422p yuv444p'; desc='mjpeg 编码器仅 YUV 族（无 RGB pix_fmt）' }
    libwebp_pixfmt = @{ log='ffmpeg-h-encoder-libwebp.txt';  pattern='Supported pixel formats: bgra yuv420p yuva420p';     desc='libwebp 仅 bgra/yuv420p/yuva420p' }
    libaom_pixfmt  = @{ log='ffmpeg-h-encoder-libaom-av1.txt'; pattern='Supported pixel formats: yuv420p yuv422p yuv444p gbrp'; desc='libaom 像素格式（YUV 420/422/444 + gbrp RGB 直喂族）' }
    svt_pixfmt     = @{ log='ffmpeg-h-encoder-libsvtav1.txt'; pattern='Supported pixel formats: yuv420p yuv420p10le';       desc='libsvtav1 仅 yuv420p/yuv420p10le' }
    cjxl_modular   = @{ log='cjxl-help-vv.txt';              pattern='Default = encoder chooses';                          desc='cjxl --modular：0=VarDCT 1=modular，默认 encoder chooses' }
    cjxl_version   = @{ log='cjxl-version.txt';              pattern='cjxl v0\.12\.0';                                     desc='cjxl v0.12.0（在册随包版本）' }
    cjpegli_xyb    = @{ log='cjpegli-help-vv.txt';           pattern='Convert to XYB colorspace';                          desc='cjpegli --xyb 旗标存在（opt-in）' }
    ppm_nocolor    = @{ log='cjxl-help-vv.txt';              pattern='do not store colorspace information';                desc='cjxl help：PPM 等 raw 格式不存色彩空间信息' }
}
$toolAnchorLines = @{}
if ($toolsOk) {
    foreach ($k in $ToolAnchors.Keys) {
        $a = $ToolAnchors[$k]
        $full = Join-Path $scanDir $a.log
        $line = $null
        if (Test-Path -LiteralPath $full) {
            $m = Select-String -LiteralPath $full -Pattern $a.pattern -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($m) { $line = [int]$m.LineNumber }
        }
        $toolAnchorLines[$k] = $line
        if ($line) { CK $true ("工具锚点 {0} → {1}:{2}（{3}）" -f $k, $a.log, $line, $a.desc) }
        else       { CK $false ("工具锚点 {0} miss：pattern「{1}」在 {2} 零命中" -f $k, $a.pattern, $a.log) }
    }
    # 条件断言 ⑤：cjxl 无显式 --xyb 开关
    $cjxlXybHits = @(Select-String -LiteralPath (Join-Path $scanDir 'cjxl-help-vv.txt') -Pattern '--xyb' -ErrorAction SilentlyContinue).Count
    CK ($cjxlXybHits -eq 0) "cjxl v0.12.0 help（-v -v）中 '--xyb' 命中 = $cjxlXybHits（须 0：无显式 xyb 开关；>0 ⇒ cjxl 已升级，事实表须重校准）"
} else {
    foreach ($k in $ToolAnchors.Keys) { $toolAnchorLines[$k] = $null }
}
# 条件断言 ④' cjpegli 出口 xyb 接线形态锁（2026-11-13 重校准）
#   ⚠ 旧判据是「CjpegliService.cs 里 'xyb' 命中 == 0」，其自身文档写明「T6 接线后重跑会红，
#     这是**预期行为**」。T6 已于 0f6c11a（2026-11-11）接线 ⇒ 旧判据按契约转红，本轮据此重校准。
#   重校准后锁的是**接线形态**而不是计数（计数锁会被任意注释改动误伤，且在接线后失去语义）：
#     ① `sb.Append(" --xyb")` 恰好 1 处 —— 0 ⇒ 接线被删而事实表仍声明「已接线」；≥2 ⇒ 重复接线；
#     ② auto 守卫行 `var outMatrixTok = opts.ColorOutMatrix;` 在场 —— 空 token 整段不进，
#        CM-REQ-08「auto 档命令行逐字节不变」的代码面锚点。
$cjpegliSrcPath = Join-Path $repoRoot 'src/FfmpegGui/Services/CjpegliService.cs'
$cjpegliXybAppend = @(Select-String -LiteralPath $cjpegliSrcPath -Pattern 'sb\.Append\(" --xyb"\)' -ErrorAction SilentlyContinue).Count
CK ($cjpegliXybAppend -eq 1) "CjpegliService 的 --xyb 接线点 = $cjpegliXybAppend（须 1：T6 于 0f6c11a 接线 ⇒ 事实表已重校准为「已接线」；0 ⇒ 接线被删/2+ ⇒ 重复接线）"
$cjpegliXybGuard = @(Select-String -LiteralPath $cjpegliSrcPath -Pattern 'var outMatrixTok = opts\.ColorOutMatrix' -ErrorAction SilentlyContinue).Count
CK ($cjpegliXybGuard -eq 1) "CjpegliService 的 auto 守卫行 = $cjpegliXybGuard（须 1：outMatrixTok 为空则整段不进 ⇒ auto 档命令行逐字节不变，CM-REQ-08）"

# ═════════════════════════════════════════════════════════════════════════════
# ③ 事实表（C-SCAN@1 契约行结构）
# ═════════════════════════════════════════════════════════════════════════════
$agentRange = 'AGENTS.md §7（2026-11-03 colorRange 批次在册：webp 容器写不进范围标记，显式 pc ⇒ 存储按 pc 摆/解码端按 limited 拉 = 3.66dB 丢失；JPEG JFIF 无任何范围标记位）'
$gateJpgWebp = 'tests/scripts/verify-jpg-webp-matrix.ps1（在册门禁 84：jpg/webp YUV 矩阵口径，legacy/engine 两侧同锁）'
$gateTokenNorm = 'tests/scripts/verify-color-token-normalize.ps1（在册门禁：F 段 R0-R6 colorRange 回拷判据）'
$gateAcr = 'tests/scripts/verify-acr-route.ps1（在册门禁 79：DNG ACR 路线 dngtool -rdp）'

# 证据渲染：ev_anchors → "file:line (desc)"；ev_static → 原样（门禁名 / 日志锚点）
function Render-Evidence([string[]]$evKeys, [string[]]$evStatic) {
    $out = @()
    foreach ($k in $evKeys) {
        $line = $anchorLines[$k]
        $a = $Anchors[$k]
        if ($line) { $out += ("{0}:{1}（{2}）" -f $a.file, $line, $a.desc) }
        else       { $out += ("{0}（锚点 {1} miss）" -f $a.file, $k) }
    }
    foreach ($k in $evStatic) {
        if ($ToolAnchors.Contains($k)) {
            $line = $toolAnchorLines[$k]
            $a = $ToolAnchors[$k]
            if ($line) { $out += ("tests/output/_tmp/color-matrix-scan-20261110/{0}:{1}（{2}）" -f $a.log, $line, $a.desc) }
            else       { $out += ("tests/output/_tmp/color-matrix-scan-20261110/{0}（工具锚点 {1} miss）" -f $a.log, $k) }
        } else { $out += $k }
    }
    return $out
}

$Facts = @(
    @{ format='jpg/jpegli'; exit_path='ffmpeg mjpeg（legacy 传统管线）'
       support=@(
         'bt470bg — 恒定落地：RGB→YUV 转换系数 = BT.601（匹配 libjpeg 解码端固定假设）；JFIF 容器无矩阵标注位 ⇒ 标注不可写'
         'smpte170m/bt601 — 冗余等值：swscale 中 bt470bg=smpte170m 同组转换结果（ColorSpaceRegistry 实测注记）；归一后与 auto 同值'
         'bt709/smpte240m/fcc/bt2020nc/ycgco — 拒绝：转换系数偏离解码端固定 BT.601 假设 ⇒ 饱和色偏（2026-10-25 实测纯红 255,0,0→232,0,1）；对齐 2026-11-03 colorRange 出口侧口径'
         'rgb — 拒绝：mjpeg 编码器仅收 YUV pix_fmt（yuvj420p/422p/444p 族）'
       )
       xyb_switch='不适用（mjpeg 无 XYB 概念）'; xyb_default='不适用'
       ev_anchors=@('containeryuvmatrix_def','containeryuv_constant','capm_output','swscale_input_accept','mappixfmt_def')
       ev_static=@($agentRange, $gateJpgWebp, 'mjpeg_pixfmt', 'ffmpeg-h-encoder-mjpeg.txt:4（Supported pixel formats 全 YUV 族）') }
    @{ format='jpg/jpegli'; exit_path='引擎 mjpeg（RawColorPipeline raw 通路）'
       support=@(
         'bt470bg — 恒定落地（与 legacy 同值）：convMatrix = ContainerYuvMatrix(jpg) ?? plan.OutMatrix；显式矩阵若覆写 convMatrix 即真转 YUV（MapEnginePixFmt 对 jpg 恒显式给 YUV pix_fmt ⇒ 转换必然发生）'
         'smpte170m/bt601 — 冗余等值（同 601 族系数）'
         'bt709/smpte240m/fcc/bt2020nc/ycgco — 拒绝（同 legacy：解码端固定 BT.601）'
         'rgb — 拒绝（mjpeg 仅收 YUV；引擎对该出口恒显式 yuvj*/yuv* pix_fmt）'
       )
       xyb_switch='不适用'; xyb_default='不适用'
       ev_anchors=@('convmatrix_engine','pixin_rgb','mapenginepixfmt_def','engine_jpg_only_note')
       ev_static=@($gateJpgWebp, 'mjpeg_pixfmt') }
    @{ format='jpg/jpegli'; exit_path='cjpegli（EncoderBackend.Cjpegli）'
       support=@(
         '无 YUV 矩阵承载：JPEG 容器无矩阵标注位；cjpegli 输入为 RGB（PPM/PNG 直编），色彩描述走 -x color_space=（ColorEncoding 枚举）与 -x icc_pathname='
         'rgb — 冗余（像素全程 RGB；产物为标准 JPEG，解码端无 YUV 解读步骤可影响）'
         'YUV 族（bt709/bt470bg/…）— 不承载（无转换发生点：cjpegli 内部从 RGB 直编，矩阵选择不暴露为 CLI 参数）'
       )
       xyb_switch='--xyb（CLI 旗标，opt-in；**已接线**：T6（0f6c11a，2026-11-11）在 BuildCjpegliArguments 里对 jpg×cjpegli×xyb=Carried 追加 --xyb；产物仍是标准 JPEG、解码端无感）'
       xyb_default='不传 --xyb 即不启用（默认 OFF；help 无默认值说明）。auto 档（ColorOutMatrix 空）整段不进 ⇒ 命令行逐字节不变（CM-REQ-08）'
       ev_anchors=@('cjpegli_build_args','cjpegli_xyb_append')
       ev_static=@('cjpegli_xyb', 'cjpegli-help-vv.txt（--xyb: Convert to XYB colorspace）') }
    @{ format='png'; exit_path='ffmpeg png（legacy 传统管线）'
       support=@(
         'rgb — 冗余（PNG 本就是 RGB 容器；cICP chunk 的 matrix 字段恒写 0=RGB/identity）'
         'YUV 族（bt709/bt470bg/smpte170m/smpte240m/fcc/bt2020nc/ycgco）— 拒绝（RGB 容器无 YUV 矩阵概念；isRgbNativeFmt 跳过输出 -colorspace 块）'
       )
       xyb_switch='不适用（PNG 无 XYB 概念）'; xyb_default='不适用'
       ev_anchors=@('pngcicp_matrix0_def','pngcicp_matrix0_note','isrgb_native')
       ev_static=@('tests/scripts/verify-png-structure.ps1 族（PNG 容器结构在册门禁面）') }
    @{ format='png'; exit_path='引擎 png（RawColorPipeline raw 通路）'
       support=@(
         'rgb — 冗余（同上）'
         'YUV 族 — 拒绝（同上）'
         '附注：sRGB 目标的 plan.OutMatrix 推导值 = bt709（PrimariesToken=bt709 → MatrixTokenFor）非空 ⇒ 引擎 png 出口会拼出 -colorspace bt709 帧属性，但 PNG RGB 容器不承载矩阵标注（cICP 恒 matrix=0）、swscale rgb→rgb 不使用矩阵 ⇒ 零像素/零标注效果 = 形式声明；显式 rgb 与该现状同为冗余档'
       )
       xyb_switch='不适用'; xyb_default='不适用'
       ev_anchors=@('convmatrix_engine','primaries_bt709','plan_outmatrix_assign','matrix_token_for_def','pngcicp_matrix0_def')
       ev_static=@() }
    @{ format='apng'; exit_path='ffmpeg apng（legacy，-c:v apng -f apng）'
       support=@(
         'rgb — 冗余（apng 在 isRgbNativeFmt 名单内；与 png 同容器语义）'
         'YUV 族 — 拒绝（同 png）'
       )
       xyb_switch='不适用'; xyb_default='不适用'
       ev_anchors=@('isrgb_native')
       ev_static=@() }
    @{ format='webp'; exit_path='ffmpeg libwebp（legacy 传统管线）'
       support=@(
         'bt470bg — 恒定落地：仅作为 RGB→YUV 转换系数（有损路径 yuv420p）；WebP 容器无矩阵标注位（无 CICP）⇒ 标注不可写；libwebp 解码端固定 BT.601 假设'
         'smpte170m/bt601 — 冗余等值（swscale 与 bt470bg 同系数；解码端假设同为 BT.601 族）'
         'bt709/smpte240m/fcc/bt2020nc/ycgco — 拒绝：标注写不进容器 + 转换偏离解码端固定假设 = 人为色偏（对齐 2026-11-03 colorRange 出口侧口径：写不进的容器不发）'
         'rgb — 拒绝（有损路径仅 yuv420p；无损路径 bgra 是编码器内部表示而非矩阵语义）'
       )
       xyb_switch='不适用（libwebp 无 XYB 概念）'; xyb_default='不适用'
       ev_anchors=@('containeryuv_constant','containeryuvmatrix_def','capm_output','unsupported_webp')
       ev_static=@($agentRange, $gateJpgWebp, $gateTokenNorm, 'libwebp_pixfmt') }
    @{ format='webp'; exit_path='引擎 libwebp（RawColorPipeline raw 通路）'
       support=@(
         'bt470bg — 恒定落地（与 legacy 同值；convMatrix 同一取值点）'
         'smpte170m/bt601 — 冗余等值；其余 YUV 族 — 拒绝（同 legacy）'
         'rgb — 拒绝（libwebp 有损路径仅 yuv420p；无 4:4:4 承接）'
       )
       xyb_switch='不适用'; xyb_default='不适用'
       ev_anchors=@('convmatrix_engine','pixin_rgb','unsupported_webp')
       ev_static=@($gateJpgWebp, 'libwebp_pixfmt') }
    @{ format='avif'; exit_path='ffmpeg libaom-av1（legacy 默认编码器 + 引擎出口）'
       support=@(
         'bt709/bt470bg/smpte170m/smpte240m/bt2020nc — 可承载：nclx matrix_coefficients 可写 + 输出侧 -colorspace 驱动 RGB→YUV 转换（矩阵必须留在 -i 之后，前置会被完全忽略——在册实测）；smpte240m = AV1 枚举 7 可写；bt2020nc = 枚举 9；bt470bg = 枚举 5；smpte170m = 枚举 6；bt709 = 枚举 1'
         'rgb/identity — 可承载：nclx matrix=0（libaom 支持 gbrp RGB 直喂像素格式，该组合语义自洽）'
         'ycgco — 待验证/建议收窄：AV1 枚举 8 存在，但 swscale 对 ycgco 的转换能力被输入侧实测否定（Impossible to convert）⇒ 输出侧转换未证；保守按拒绝或需实测背书'
         'fcc — 拒绝（推断）：AV1 bitstream matrix_coefficients=3/4 为 Reserved，nclx 写不进；标注写了=解码端不知道（无本批实测支撑，按容器机制推断——同 AGENTS §7 AVIF 口径标注方式）'
         '耦合前提：显式 YUV 矩阵 ⇒ 输出 pix_fmt 必须是 YUV 族。引擎通路 pix_fmt 判据（chromaSet/bitDepth/srcBd>8）未命中时不写 -pix_fmt ⇒ libaom 自选 gbrp = RGB 直喂，矩阵标签成为无转换的孤立标注 ⇒ Requires-PixFmt-Switch（未确认耦合前 Carried 应收窄）'
       )
       xyb_switch='不适用（AV1/AVIF 无 XYB 概念）'; xyb_default='不适用'
       ev_anchors=@('avif_label_split','avif_pixfmt_gate','avif_effective_bitdepth','mappixfmt_def','convmatrix_engine','swscale_input_reject','swscale_input_accept','pixin_rgb')
       ev_static=@('libaom_pixfmt', 'ffmpeg-h-encoder-libaom-av1.txt:74（-aom-params 无 colorspace/matrix AVOption ⇒ nclx 来自帧属性）', 'AGENTS.md §7（AVIF 静态出口 ExitWritesIcc=false 批次在册）') }
    @{ format='avif'; exit_path='ffmpeg libsvtav1（用户显式选编码器）'
       support=@(
         'bt709/bt470bg/smpte170m/smpte240m/bt2020nc — 可承载（同 libaom；编码器结构保证 YUV：仅收 yuv420p/yuv420p10le，RGB→YUV 转换必然发生 ⇒ 矩阵必为转换系数）'
         'rgb — 拒绝（无 gbrp 支持位；4:4:4 亦会被 ffmpeg 静默降为 4:2:0 ⇒ 引擎按不可承接拒绝）'
         'ycgco — 待验证/建议收窄（同 libaom 行）；fcc — 拒绝（同 libaom 行）'
       )
       xyb_switch='不适用'; xyb_default='不适用'
       ev_anchors=@('unsupported_svt','avif_label_split')
       ev_static=@('svt_pixfmt') }
    @{ format='avif'; exit_path='avifenc（gif→avif / jxr→avif 两步法）'
       support=@(
         '现状零矩阵通路：本仓两步法参数拼装（-q/-s/-j + -d/-y）无任何矩阵项 ⇒ nclx matrix 落 avifenc 自身默认（不指定即 unspecified）；显式矩阵承载需新增 --matrix-coefficients 传递（avifenc 1.4.2 支持该参数，未接线）'
         'rgb/identity — 可承载（avifenc 支持 identity 标注；需接线后生效）'
       )
       xyb_switch='不适用（avifenc 无 XYB 概念）'; xyb_default='不适用'
       ev_anchors=@('avifenc_args_build','jxr_avif_args')
       ev_static=@('QueueProcessor.cs:4596-4611（-q 20..100 钳制与 headArgs 拼装，无矩阵项）') }
    @{ format='jxl'; exit_path='cjxl（EncoderBackend.Cjxl，legacy + 引擎 cjxl 出口）'
       support=@(
         'rgb — 冗余（JXL 是 RGB 容器 RgbNative；色彩描述走 -x color_space（CICP 枚举 sRGB/DisplayP3/Rec2100PQ/Rec2100HLG）与 ICC）'
         'YUV 族（bt709/bt470bg/…）— 拒绝（RgbNative，无 YUV 承载位）'
       )
       xyb_switch='无显式 CLI 开关（cjxl v0.12.0 --help -v -v 实测 0 处 --xyb）；XYB 由 VarDCT 模式隐式承载；--modular=1 语义排除 XYB（本仓 --jxl-modular 接线点：modular 优先并播报）'
       xyb_default='VarDCT（lossy 路径，-m 0 / encoder chooses 默认）= XYB；modular（-m 1）= 非 XYB'
       ev_anchors=@('cjxl_modular_wiring','cjxl_colorspace_map','jxl_native_note')
       ev_static=@('cjxl_modular', 'cjxl_version') }
    @{ format='jxl'; exit_path='ffmpeg libjxl（静帧 ffmpeg 路线）'
       support=@(
         'rgb — 冗余（编码器像素格式全 RGB/gray 族；JXL 容器本身 RGB 语义）'
         'YUV 族 — 拒绝（编码器无 YUV 输入格式，矩阵无转换作用点）'
       )
       xyb_switch='-xyb <int>（AVOption，0/1 显式可选）'
       xyb_default='1（默认开启：-h encoder=libjxl 实测 default 1）'
       ev_anchors=@()
       ev_static=@('libjxl_pixfmt', 'libjxl_xyb', 'libjxl_modular') }
    @{ format='tiff'; exit_path='ffmpeg tiff（legacy + 引擎 raw 通路）'
       support=@(
         'rgb — 冗余（RGB 原生容器，isRgbNativeFmt 名单；色彩描述走 cHRM/ICC/tiff tags）'
         'YUV 族 — 拒绝（无 YUV 承载位）'
       )
       xyb_switch='不适用'; xyb_default='不适用'
       ev_anchors=@('isrgb_native','containeryuvmatrix_def')
       ev_static=@() }
    @{ format='gif'; exit_path='ffmpeg gif（legacy）+ 引擎调色板链（palettegen/paletteuse）'
       support=@(
         '全拒绝（pal8 调色板容器，无 YUV 矩阵概念；无色彩标注位承载 H.273 矩阵）'
         'rgb — 冗余（引擎链像素 rgb24/rgb48le → 输出 pal8；矩阵无作用点）'
       )
       xyb_switch='不适用'; xyb_default='不适用'
       ev_anchors=@('gif_chain','engine_encodable')
       ev_static=@() }
    @{ format='dng'; exit_path='dngtool（-rdp / -rd / -d；ACR 路线）'
       support=@(
         '不适用（契约外概念）：DNG 的 ColorMatrix1/2 = 相机校准矩阵（XYZ D50→参考相机空间，illuminant A/D65），与 H.273 YUV 矩阵族无对应关系'
         '-rdp 渲染空间由 -rdspace 选择（srgb|p3|rec2020，均为 RGB 线性空间，按下游目标选）——属色彩空间转换而非矩阵承载'
       )
       xyb_switch='不适用（DNG 无 XYB 概念）'; xyb_default='不适用'
       ev_anchors=@('dng_colormatrix_sem','dng_rdspace')
       ev_static=@($gateAcr) }
    @{ format='jxr'; exit_path='JxrEncApp（按设计固定；ffmpeg libjxr 无质量 AVOption）'
       support=@(
         '不适用：JXR 内部色彩变换（YCoCg 族）是编码器内部实现，不暴露为 CLI 选择（BuildArguments 参数全集 = -i/-o/-q/-d/-p/-l，无矩阵/色彩空间项）'
         '色彩描述仅 ICC/EXIF 后置（JxrEncApp 不支持嵌入色彩配置）；rgb — 形式上冗余（JXR 内部语义不属本契约）'
       )
       xyb_switch='不适用'; xyb_default='不适用'
       ev_anchors=@('jxr_build_args','jxr_no_icc_note')
       ev_static=@('AGENTS.md §7（JXR 批次在册：JxrEncApp 只吃 BMP/TIFF）') }
    @{ format='ppm'; exit_path='ffmpeg ppm（legacy；另为 cjpegli/cjxl 管道中间件载体）'
       support=@(
         '全拒绝（PPM 纯 RGB 头部容器，无色彩字段——cjxl help 原话：raw 格式不存色彩空间信息）'
         'rgb — 冗余（像素全程 RGB）'
       )
       xyb_switch='不适用'; xyb_default='不适用'
       ev_anchors=@('containeryuvmatrix_def')
       ev_static=@('ppm_nocolor') }
)

# ── 键完整性断言 ──
Write-Output '── ③ 事实表键完整性断言 ──'
$expectedFormats = @('jpg/jpegli','png','apng','webp','avif','jxl','tiff','gif','dng','jxr','ppm')
CK ($Facts.Count -eq 18) "事实表行数 = $($Facts.Count)（须 18：11 格式 × 出口面拆分）"
$seenFormats = @{}
$rowIdx = 0
foreach ($f in $Facts) {
    $rowIdx++
    $keysOk = ($f.Keys -contains 'format') -and ($f.Keys -contains 'exit_path') -and ($f.Keys -contains 'support') `
              -and ($f.Keys -contains 'xyb_switch') -and ($f.Keys -contains 'xyb_default') `
              -and ($f.Keys -contains 'ev_anchors') -and ($f.Keys -contains 'ev_static')
    CK $keysOk "行 $rowIdx 键完整（format/exit_path/support/xyb_switch/xyb_default/ev_*）"
    CK ([string]::IsNullOrWhiteSpace($f.format) -eq $false) "行 $rowIdx format 非空（$($f.format)）"
    CK ([string]::IsNullOrWhiteSpace($f.exit_path) -eq $false) "行 $rowIdx exit_path 非空"
    CK ($f.support.Count -ge 1) "行 $rowIdx matrix_support 非空数组（$($f.support.Count) 条）"
    foreach ($s in $f.support) { CK ([string]::IsNullOrWhiteSpace($s) -eq $false) "行 $rowIdx matrix_support 条目非空" }
    CK (($f.xyb_switch -ne $null) -and ($f.xyb_default -ne $null)) "行 $rowIdx xyb_switch/xyb_default 已声明"
    $ev = Render-Evidence $f.ev_anchors $f.ev_static
    CK ($ev.Count -ge 1) "行 $rowIdx evidence 非空（$($ev.Count) 条）"
    foreach ($e in $ev) { CK ([string]::IsNullOrWhiteSpace($e) -eq $false) "行 $rowIdx evidence 条目非空" }
    $missCnt = @($ev | Where-Object { $_ -like '*锚点*miss*' }).Count
    CK ($missCnt -eq 0) "行 $rowIdx evidence 无 miss 占位符（miss=$missCnt）"
    $f.ev_rendered = $ev
    if (-not $seenFormats.ContainsKey($f.format)) { $seenFormats[$f.format] = $true }
}
foreach ($fmt in $expectedFormats) {
    CK ($seenFormats.ContainsKey($fmt)) "格式覆盖: $fmt"
}
CK ($seenFormats.Count -eq $expectedFormats.Count) "格式覆盖集恰好 = $($expectedFormats.Count)（实测 $($seenFormats.Count)）"

# ── JSON 生成 + 回读验证 ──
Write-Output '── ④ scan-facts.json 生成 + 回读验证 ──'
$jsonRows = @()
foreach ($f in $Facts) {
    $jsonRows += [ordered]@{
        format         = $f.format
        exit_path      = $f.exit_path
        matrix_support = $f.support
        xyb_switch     = $f.xyb_switch
        xyb_default    = $f.xyb_default
        evidence       = $f.ev_rendered
    }
}
$factsPath = Join-Path $scanDir 'scan-facts.json'
$jsonRows | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $factsPath -Encoding UTF8
CK ((Get-Item -LiteralPath $factsPath).Length -gt 1000) "scan-facts.json 已写出（$((Get-Item -LiteralPath $factsPath).Length) bytes）"

try {
    $rt = Get-Content -LiteralPath $factsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    CK (@($rt).Count -eq $Facts.Count) "JSON 回读行数 = $(@($rt).Count)（须 $($Facts.Count)）"
    $rtBad = 0
    foreach ($r in $rt) {
        if (-not $r.format -or -not $r.exit_path -or -not $r.matrix_support -or $null -eq $r.xyb_switch `
            -or $null -eq $r.xyb_default -or -not $r.evidence) { $rtBad++ }
    }
    CK ($rtBad -eq 0) "JSON 回读六键逐行完整（bad=$rtBad）"
} catch {
    CK $false "JSON 回读解析失败：$($_.Exception.Message)"
}

# ═════════════════════════════════════════════════════════════════════════════
# ⑤ report.md 生成（人读主报告）
# ═════════════════════════════════════════════════════════════════════════════
function L([string]$k) {   # 锚点 → "file:line"
    $line = $anchorLines[$k]
    if ($line) { return "{0}:{1}" -f $Anchors[$k].file, $line }
    return "{0}:?" -f $Anchors[$k].file
}
function TL([string]$k) {  # 工具锚点 → "日志:line"
    $line = $toolAnchorLines[$k]
    if ($line) { return "tests/output/_tmp/color-matrix-scan-20261110/{0}:{1}" -f $ToolAnchors[$k].log, $line }
    return "tests/output/_tmp/color-matrix-scan-20261110/{0}:?" -f $ToolAnchors[$k].log
}

$tblRows = foreach ($f in $Facts) {
    $sup = ($f.support -join '；')
    "| $($f.format) | $($f.exit_path) | $sup | $($f.xyb_switch) | $($f.xyb_default) |"
}

$evLines = foreach ($f in $Facts) {
    "### $($f.format) × $($f.exit_path)"
    ""
    foreach ($e in $f.ev_rendered) { "- $e" }
    ""
}

$report = @"
# 全格式矩阵承载扫描报告（C-SCAN@1，T1）

- **生成时间**：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
- **仓库**：``$repoRoot``（HEAD=$headShort，工作树含未提交改动——全部 file:line 为**当轮实读**，由 _probe-color-matrix-scan.ps1 动态解析）
- **工具版本**：ffmpeg $ffVer；cjxl/cjpegli 见 $(TL 'cjxl_version') 与扫描目录日志
- **生成器**：``tests/scripts/_probe-color-matrix-scan.ps1``（只读；重跑幂等——行号随当轮代码漂移自动更新）
- **依据面**：代码阅读 + 工具 ``-h`` 自报表（当轮重打印）+ 在册门禁名 + AGENTS §7 在册读数；**未跑任何长转码矩阵**

## 0. 摘要（六项必含事实的结论一句话版）

1. **矩阵可落地面极窄**：11 格式里只有 avif（libaom/libsvtav1）真正承载显式 H.273 输出矩阵（nclx + 转换系数双落地）；jpg/webp 恒 bt470bg（仅转换系数，容器无标注位）；png/apng/tiff/jxl/gif/ppm 是 RGB 原生（无矩阵概念）；dng/jxr 的"矩阵"是**契约外概念**（相机校准矩阵 / 编码器内部变换）。
2. **XYB 三路径**：cjxl v0.12.0 **无显式开关**（VarDCT 隐式承载，--modular=1 排除）；ffmpeg libjxl 有 ``-xyb``（**default 1 已开**）；cjpegli 有 ``--xyb`` 旗标（opt-in，默认 OFF；**本仓 T6 / 0f6c11a 起已接线**——jpg×cjpegli×xyb=Carried 时追加，auto 档不进）。
3. **webp/jpg 无矩阵标记**：AGENTS §7 2026-11-03 在册实测（webp 无 CICP 写不进范围标记；JPEG JFIF 无标记位）⇒ 矩阵标注同样不可写，显式矩阵只能作用于转换系数且必须与解码端固定 BT.601 假设一致。
4. **avif nclx 全集**：bt709/bt470bg/smpte170m/smpte240m/bt2020nc 可写（枚举 1/5/6/7/9）+ rgb（枚举 0）；**fcc 不可写**（AV1 枚举 3/4=Reserved，推断）；**ycgco 待验证**（枚举 8 存在但 swscale 转换能力被输入侧实测否定）；avifenc 两步法**现状零矩阵通路**。
5. **dng/jxr 概念确认**：dngtool ColorMatrix1/2 = 相机校准矩阵（XYZ D50→相机空间）；-rdspace = RGB 线性渲染空间；JxrEncApp 参数全集无矩阵项（内部 YCoCg 不暴露）——两者均**不属本契约**。
6. **耦合点**：显式 YUV 矩阵的落地与否取决于出口 pix_fmt——引擎 raw 通路中间件恒 rgb24/rgb48le；jpg 出口（MapEnginePixFmt 恒显式 YUV）真转 YUV；avif 出口 pix_fmt 判据未命中时 libaom 自选 gbrp（RGB 直喂）⇒ 矩阵标签孤立；png 出口的 -colorspace 是零效果形式声明。**结论：avif 引擎通路的显式矩阵 = Requires-PixFmt-Switch**。

## 1. 格式 × 出口 × 承载矩阵值 完整表（18 行）

| 格式 | 出口 | 可接受的输出矩阵值（H.273 名 + 容器标注位） | xyb 开关 | xyb 默认 |
|---|---|---|---|---|
$($tblRows -join "`n")

## 2. XYB 三路径开关与默认（必含事实 ②）

| 路径 | 开关 | 默认 | 证据 |
|---|---|---|---|
| cjxl v0.12.0 | **无显式开关**（help -v -v 实测 0 处 ``--xyb``）；XYB 由 VarDCT 模式隐式承载；``--modular=1`` 语义排除 XYB | VarDCT（lossy 默认）= XYB；modular = 非 XYB | $(TL 'cjxl_modular')；$(TL 'cjxl_version')；$(L 'cjxl_modular_wiring') |
| ffmpeg libjxl | ``-xyb <int>``（AVOption） | **1（已开）**；``-modular`` default 0 | $(TL 'libjxl_xyb')；$(TL 'libjxl_modular') |
| cjpegli | ``--xyb``（CLI 旗标，opt-in） | 不传即不启用（默认 OFF）；**本仓已接线**（T6 / 0f6c11a：jpg×cjpegli×xyb=Carried ⇒ 追加 ``--xyb``；auto 档不进该分支） | $(TL 'cjpegli_xyb')；$(L 'cjpegli_xyb_append')；$(L 'cjpegli_build_args') |

## 3. webp/jpg 容器无矩阵标记的证据（必含事实 ③，引用在册读数不重跑）

- $($agentRange)
- $($gateJpgWebp)
- $($gateTokenNorm)
- 代码层同源口径：$(L 'containeryuvmatrix_def')（ContainerYuvMatrix：jpg/jpeg/webp ⇒ bt470bg，注释原文「这两个容器写不进矩阵字段，标准解码器按 BT.601 解」）；$(L 'capm_output')（legacy capM 优先同口径）。

## 4. avif nclx 可写矩阵全集（必含事实 ④，D4 决策点校准依据）

| 矩阵 | AV1 枚举值 | 可写性 | 依据 |
|---|---|---|---|
| identity/rgb | 0 | 可写（libaom gbrp 直喂自洽；libsvtav1 无 gbrp ⇒ 不可） | $(TL 'libaom_pixfmt') / $(TL 'svt_pixfmt') |
| bt709 | 1 | 可写 | 输出侧 -colorspace 帧属性 → 编码器读帧属性写 nclx（$(L 'avif_label_split')） |
| bt470bg / smpte170m | 5 / 6 | 可写 | 同上；swscale 转换实测可行（$(L 'swscale_input_accept')） |
| smpte240m | 7 | 可写 | 同上 |
| fcc | 4（H.273） | **不可写（推断）**：AV1 bitstream 枚举 3/4 = Reserved | AV1 规范枚举（推断，无本批实测——同 AGENTS §7 对 AVIF 容器机制的推断标注方式） |
| ycgco | 8 | **待验证/建议收窄**：枚举存在，但 swscale 转换能力被输入侧实测否定（$(L 'swscale_input_reject')） | 输出侧未实测；保守按 Rejected 或要求实测背书 |
| bt2020nc | 9 | 可写 | 同上（$(L 'swscale_input_accept')） |
| avifenc 通路 | — | **现状零矩阵通路**（参数面未接线） | $(L 'avifenc_args_build') / $(L 'jxr_avif_args') |

- 工具自报表反证：libaom/libsvtav1 的 ``-h encoder`` 输出里**没有任何 colorspace/matrix AVOption**（nclx 全部来自输入帧属性）⇒ $(TL 'libaom_pixfmt')、ffmpeg-h-encoder-libaom-av1.txt:74。

## 5. dng/jxr 矩阵概念确认（必含事实 ⑤）

- **dngtool**：ColorMatrix1/2 = DNG 规范相机校准矩阵（XYZ D50 → 参考相机空间，illuminant A/D65；$(L 'dng_colormatrix_sem')）；``-rdp`` 渲染空间 = ``-rdspace srgb|p3|rec2020``（RGB 线性空间，$(L 'dng_rdspace')）。**均不属 H.273 YUV 矩阵族** ⇒ 本契约对 dng = 无承载。
- **JxrEncApp**：参数全集 ``-i/-o/-q/-d/-p/-l``（$(L 'jxr_build_args')），无矩阵/色彩空间项；内部 YCoCg 族变换不暴露为可选参数；色彩描述仅 ICC/EXIF 后置（$(L 'jxr_no_icc_note')）⇒ 本契约对 jxr = 无承载。

## 6. YUV 矩阵显式选择与 pix_fmt 选择器的耦合点（必含事实 ⑥）

| # | 耦合点 | 结论 | 证据 |
|---|---|---|---|
| 6.1 | ``ImageEncoderArgs.MapPixFmt``（legacy/AVIF 位） | 色度×位深 → yuv420p/422/444[位深]le；**显式矩阵不改变该函数**——矩阵决定转换系数，pix_fmt 决定是否真转 YUV，两者共同决定出口像素面 | $(L 'mappixfmt_def') |
| 6.2 | ``ImageEncoderArgs.MapEnginePixFmt``（引擎位） | **只覆盖 jpg/jpeg**（恒显式 yuvj*/yuv*）⇒ jpg 引擎出口显式矩阵**真转 YUV 生效**；其余格式返回 null ⇒ 像素留 rgb24/rgb48le 直喂 | $(L 'mapenginepixfmt_def')；$(L 'engine_jpg_only_note') |
| 6.3 | ``RawColorPipeline.BuildEncodeArgs`` convMatrix | ``ContainerYuvMatrix(ext) ?? plan.OutMatrix``——显式矩阵落地 = 覆写该取值点；png 出口 plan.OutMatrix 推导值非空（bt709）但 RGB 容器零效果 | $(L 'convmatrix_engine')；$(L 'primaries_bt709')；$(L 'plan_outmatrix_assign') |
| 6.4 | ``FfmpegCommandBuilder`` 输出侧 | ``dec.CapMatrix ?? outputColorSpace``——legacy 唯一输出矩阵取值点；RGB 原生（png/tiff/apng/jxl）由 isRgbNativeFmt 跳过 | $(L 'capm_output')；$(L 'isrgb_native') |
| 6.5 | **引擎 raw 通路恒 rgb24/rgb48le 时显式 bt709 会发生什么** | 分出口三种结局：①jpg（MapEnginePixFmt 已 YUV）⇒ swscale 真按 bt709 转 YUV = 像素真变；②avif（pix_fmt 判据未命中时 gbrp 直喂）⇒ 矩阵标签孤立（nclx 写上但像素是 RGB = 语义错位）⇒ **Requires-PixFmt-Switch**；③png/jxl/tiff ⇒ RGB 容器零转换零标注 = no-op 形式声明 | $(L 'pixin_rgb')；$(L 'avif_pixfmt_gate')；$(L 'r4_engine_rgb_note') |
| 6.6 | 输入声明侧（对照，本特性不动） | ``--color-matrix`` 文档语义 = 输入矩阵声明（-i 前）；引擎路径输入恒已解码 RGB ⇒ 用户值无承载（R4 已点名） | $(L 'input_decl')；$(L 'options_matrix_doc')；$(L 'r4_noop_note') |

## 7. C-CAPS@1 初版判定表校准清单（T5 据此修订）

> 图例：**[冲突]** = 计划值 ≠ 扫描值（须改判定表）；**[确认]** = 扫描证实计划值；**[条件]** = 方向确认但落地条件需补进判定函数。

| # | 格子 | 计划值（plan.md §2.2） | 扫描值 | 证据 | 一句话结论 |
|---|---|---|---|---|---|
| C1 | webp × YUV 族（bt470bg） | **[冲突]** Carried | **Redundant** | §1 表第 7/8 行 | auto 恒 bt470bg ⇒ 显式值与推导值相同，产物应逐字节一致；按 CM-REQ-05 属「冗余但一致」档（接受 + 点名仅登记），不是落地生效 |
| C2 | jpg × YUV 族（bt470bg） | **[冲突]** Carried | **Redundant** | §1 表第 1/2 行 | 同 C1（ContainerYuvMatrix 恒 bt470bg，无差异可落地） |
| C3 | webp/jpg × smpte170m/bt601 | **[冲突]** Rejected（「仅 bt470bg Carried；其余 Rejected」） | **Redundant** | $(L 'swscale_input_accept') | swscale 实测 bt470bg=smpte170m=rgb 同组转换结果 ⇒ 601 族别名与 auto 同值；收窄集应重划为「601 族 = Redundant，709/240m/fcc/2020nc/ycgco = Rejected」 |
| C4 | avif × ycgco | **[冲突]** Carried（D4 待确认） | **Rejected（或待实测背书）** | $(L 'swscale_input_reject') | swscale 输入侧实测 ycgco 不可转换；输出侧同引擎未证 ⇒ 保守收窄（D4 裁定） |
| C5 | avif × fcc | **[冲突]** Carried（D4 待确认） | **Rejected** | §4 表 | AV1 bitstream 枚举 3/4 = Reserved，nclx 写不进（推断，无本批实测） |
| C6 | avif × avifenc 出口 | **[冲突]** Carried | **Not-wired（现状不承载）** | $(L 'avifenc_args_build') | 两步法参数面无矩阵项；avifenc 1.4.2 支持 --matrix-coefficients 但本仓未接线 ⇒ 显式矩阵要么拒绝要么先接线 |
| C7 | avif × libaom/libsvtav1（引擎/legacy）YUV 族 | **[条件]** Carried | **Carried + Requires-PixFmt-Switch** | $(L 'avif_pixfmt_gate')；$(L 'pixin_rgb') | 显式矩阵必须同时写 YUV pix_fmt；引擎通路 pix_fmt 判据未命中时 gbrp 直喂 ⇒ 标签孤立 = 语义错位；判定函数须把「切 pix_fmt」并入落地决议（计划 §2.3 第 3 条同向） |
| C8 | jxl × cjxl × xyb | **[条件]** Carried | **Carried（无开关可发）** | $(TL 'cjxl_modular') | cjxl v0.12.0 无显式 xyb 开关 ⇒ 「显式确认」只能播报确认 + 与 --modular 的冲突点名（计划 R4 验收示例语义与此一致） |
| C9 | jpg × cjpegli × xyb | **[条件]** Carried | **Carried（已落地：T6 / 0f6c11a）** | $(L 'cjpegli_xyb_append') | ``--xyb`` 已由 BuildCjpegliArguments 在 Carried 档追加（auto 档不进）⇒ 条件已满足，本条从「需接线」转「已接线」；重校准日期 2026-11-13 |
| C10 | ffmpeg libjxl × xyb | **[确认]** Carried | **Carried（-xyb default 1）** | $(TL 'libjxl_xyb') | 实测确认：AVOption 存在且默认已开 |
| C11 | png/apng/tiff/bmp/ppm/gif × 全部 | **[确认]** Rejected/Redundant | 同 | §1 表 | RGB 原生/调色板/无头容器，无矩阵概念 |
| C12 | dng/jxr | **[确认]** Rejected | 同 | $(L 'dng_colormatrix_sem')；$(L 'jxr_build_args') | 「矩阵」是契约外概念（相机校准矩阵 / 编码器内部变换） |
| C13 | 引擎 raw 通路（恒 rgb24/rgb48le）上显式 YUV 矩阵的 Carried | **[冲突]**（计划允许在耦合点确认前收窄） | **收窄为 Requires-PixFmt-Switch 或 Rejected** | $(L 'pixin_rgb')；§6.5 | 计划 §2.3 第 3 条「宁可先窄后宽」的执行依据：唯一真转 YUV 的引擎子出口是 jpg；png 是 no-op 形式声明；avif 是孤立标签 |

**冲突格统计**：硬冲突 6 处（C1/C2/C3/C4/C5/C6）+ 条件补充 3 处（C7/C8/C9）+ 确认 4 处（C10-C13 中确认类）。T5 修订判定表时按本清单逐格改，修订记录留在判定表内。

## 8. 证据索引（当轮实读行号）

$($evLines -join "`n")
## 9. 归置与重跑

- 判据（本脚本）：``tests/scripts/_probe-color-matrix-scan.ps1``（平铺，已登记 _run-step3-gates.ps1 第 98 条）
- 产物（本报告与日志）：``tests/output/_tmp/color-matrix-scan-20261110/``（gitignore；重跑即重建）
- 重跑：``pwsh -NoProfile -File tests/scripts/_probe-color-matrix-scan.ps1``（无参数；TMP 自动钉 tests/output/_tmp/round-tmp）
- 快照语义：脚本锁「当轮事实」（如 cjpegli 出口 --xyb 的**接线形态**、cjxl 无 --xyb）。代码改到这些事实后重跑若红 = 事实表过期提示，须人工重校准后再更新本报告——这是设计行为，不是误报。**2026-11-13 重校准一次**：④ 从「CjpegliService 零 xyb 引用」改为「接线点恰好 1 处 + auto 守卫行在场」（T6 于 0f6c11a 落地后旧判据按其自身约定转红）。
"@

$reportPath = Join-Path $scanDir 'report.md'
Set-Content -LiteralPath $reportPath -Value $report -Encoding UTF8
CK ((Get-Item -LiteralPath $reportPath).Length -gt 5000) "report.md 已写出（$((Get-Item -LiteralPath $reportPath).Length) bytes）"

# ═════════════════════════════════════════════════════════════════════════════
# ⑥ 汇总
# ═════════════════════════════════════════════════════════════════════════════
Write-Output ''
Write-Output "COLOR-MATRIX-SCAN RESULT: PASS=$pass fail=$fail"
Write-Output "report=$scanDir/report.md"
Write-Output "facts=$factsPath"
if ($fail -gt 0) { exit 1 }
exit 0