# ═══════════════════════════════════════════════════════════
# PS 一键验证: dngtool 输出 DNG → Photoshop (ACR) 打开验证
# 2026-08-15: 利用本地 Photoshop 验证 RAW 管线产物的 ACR 兼容性
#
# 用法:
#   .\run-ps-verify.ps1                          # 用默认样本 (dngtool 产物)
#   .\run-ps-verify.ps1 -InputDng path.dng       # 指定 DNG
#   .\run-ps-verify.ps1 -SkipRender              # 仅打开验证不渲染 PNG
#
# 输出: tests/output/psverify/ps_open_*.txt (打开结果)
# ═══════════════════════════════════════════════════════════
param(
    [string]$InputDng = "",
    [switch]$SkipRender
)
$ErrorActionPreference = "Stop"

Write-Host "════════ PS (ACR) 兼容性验证 ════════" -ForegroundColor Yellow

# ── 1. 查找 Photoshop ──
$psCandidates = @(
    "$env:ProgramFiles\Adobe\Adobe Photoshop 2026\Photoshop.exe",
    "$env:ProgramFiles\Adobe\Adobe Photoshop 2025\Photoshop.exe",
    "$env:ProgramFiles\Adobe\Adobe Photoshop 2024\Photoshop.exe",
    "$env:ProgramFiles\Adobe\Adobe Photoshop 2023\Photoshop.exe",
    "$env:ProgramFiles\Adobe\Adobe Photoshop 2022\Photoshop.exe"
)
$psPath = $psCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $psPath) {
    # 注册表检测
    $reg = Get-ItemProperty "HKLM:\SOFTWARE\Adobe\Photoshop\*" -ErrorAction SilentlyContinue
    if ($reg) {
        foreach ($p in $reg) {
            $cand = Join-Path $p.ApplicationPath "Photoshop.exe"
            if (Test-Path $cand) { $psPath = $cand; break }
        }
    }
}
if (-not $psPath) { Write-Host "❌ 未检测到 Photoshop" -ForegroundColor Red; exit 1 }
Write-Host "✅ Photoshop: $psPath" -ForegroundColor Green

# ── 1.5 清理残留 PS 进程 ──
# PS 的 app.quit() 不会终止进程; 残留实例会导致新脚本连接旧实例排队超时
$existing = Get-Process Photoshop -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "⚠️ 检测到残留 Photoshop 进程 ($($existing.Id -join ','))，正在清理..." -ForegroundColor Yellow
    $existing | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

# ── 2. 确定输入 DNG ──
$outDir = Join-Path (Get-Location) "tests\output\psverify"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
if (-not $InputDng) {
    # 默认用管线测试的 CFA 保留产物
    $candidates = @(
        "tests\output\results\raw_bayer_cfa.dng",
        "tests\output\results\raw_reenc.dng",
        "tests\output\rawcheck-live\live_cfa.dng",
        "tests\output\rawcheck-live\live_jxl.dng"
    )
    $InputDng = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $InputDng -or -not (Test-Path $InputDng)) {
    Write-Host "❌ 未找到 DNG 样本。请用 -InputDng 指定。" -ForegroundColor Red
    exit 1
}
Write-Host "📷 输入 DNG: $InputDng" -ForegroundColor Cyan

# ── 3. 生成 ExtendScript ──
$fsIn = $InputDng.Replace("\", "/")
$resultFile = Join-Path $outDir ("ps_open_" + [IO.Path]::GetFileNameWithoutExtension($InputDng) + ".txt")
$fsResult = $resultFile.Replace("\", "/")
$scriptPath = Join-Path $outDir ("ps_verify_" + [guid]::NewGuid().ToString("N") + ".jsx")

$renderBlock = ""
if (-not $SkipRender) {
    $pngOut = Join-Path $outDir ("ps_render_" + [IO.Path]::GetFileNameWithoutExtension($InputDng) + ".png")
    $fsPng = $pngOut.Replace("\", "/")
    $renderBlock = @"
    doc.bitsPerChannel = BitsPerChannelType.EIGHT;
    var pngOpts = new PNGSaveOptions();
    pngOpts.compression = 6;
    var outFile = new File("$fsPng");
    doc.saveAs(outFile, pngOpts, true, Extension.LOWERCASE);
    log.writeln("SAVED " + outFile.fsName);
"@
}

$jsx = @"
#target photoshop
var f = new File("$fsIn");
var log = new File("$fsResult");
log.encoding = "UTF-8";
log.open("w");
log.writeln("fsName=" + f.fsName + " exists=" + f.exists);
if (!f.exists) { log.writeln("OPEN_FAIL: not found"); log.close(); app.quit(); }
try {
    var doc = app.open(f);
    if (!doc) { log.writeln("OPEN_FAIL: null"); log.close(); app.quit(); }
    log.writeln("OPEN_OK " + doc.width.value + "x" + doc.height.value + " bits=" + doc.bitsPerChannel);
$renderBlock
    doc.close(SaveOptions.DONOTSAVECHANGES);
} catch (e) {
    log.writeln("OPEN_FAIL: " + e.message);
}
log.writeln("DONE");
log.close();
app.quit();
"@
[IO.File]::WriteAllText($scriptPath, $jsx, [Text.UTF8Encoding]::new($true))
Write-Host "📜 脚本: $scriptPath"

# ── 4. 调用 PS（阻塞等待脚本完成）──
Write-Host "⏳ 启动 Photoshop (ACR 打开可能需要 20-60s)..." -ForegroundColor Cyan
$proc = Start-Process -FilePath $psPath -ArgumentList "`"$scriptPath`"" -PassThru
$timeout = 180
$deadline = (Get-Date).AddSeconds($timeout)
# 等待结果文件写入完成（包含 DONE 标记；仅 Test-Path 会读到 0 字节半成品）
$resultReady = $false
while (-not $resultReady) {
    if ((Get-Date) -gt $deadline) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        Write-Host "❌ PS 超时 ($timeout s)" -ForegroundColor Red
        exit 1
    }
    if (Test-Path $resultFile) {
        try { if ((Get-Content $resultFile -Raw -ErrorAction Stop) -match "DONE") { $resultReady = $true } } catch { }
    }
    if (-not $resultReady) { Start-Sleep -Seconds 2 }
}
# PS 进程可能已退出（脚本 app.quit()）；已退出则忽略
try { Wait-Process -Id $proc.Id -Timeout 30 -ErrorAction Stop } catch { }

# ── 5. 解析结果 ──
Write-Host "`n════════ 验证结果 ════════" -ForegroundColor Yellow
$lines = Get-Content $resultFile
$ok = $false
foreach ($line in $lines) {
    Write-Host "  $line"
    if ($line -like "OPEN_OK*") { $ok = $true }
}
if ($ok) {
    Write-Host "`n✅ PS 打开验证通过!" -ForegroundColor Green
    exit 0
} else {
    Write-Host "`n❌ PS 打开验证失败" -ForegroundColor Red
    exit 1
}
