# 🧪 FFmpegPictureUI 测试规范

> 版本: 1.0 | 最后更新: 2026-08-13 | 适用于 v1.5.2+

---

## 一、测试目录约定

**所有测试产生的文件必须放入 `tests/output/` 目录**，该目录已被 `.gitignore` 忽略，不会进入版本库。

```
ffmpegPictureUI/
├── tests/
│   ├── output/           ← ⚠️ 测试产物目录（.gitignore 忽略，不提交）
│   │   ├── *.jpg/png/jxl ← 生成的测试图片与编码产物
│   │   ├── *.icc         ← ICC 提取测试文件
│   │   └── *.log         ← 测试日志
│   └── scripts/          ← ✅ 测试脚本目录（可提交，可重复执行）
│       └── *.ps1         ← 自动化测试脚本
├── docs/
│   └── TESTING.md        ← 本规范
└── publish/PLAN/         ← 测试所需的工具（ffmpeg/cjxl/exiftool 等）
```

### 规则
1. **测试输入**：临时生成的测试图片 → `tests/output/`（如 `tests/output/src_*.png`）
2. **测试产物**：编码/解码输出 → `tests/output/`（如 `tests/output/result_*.jxl`）
3. **禁止**在仓库根目录、`src/`、`docs/` 下生成任何测试文件
4. **测试后清理**：测试结束后删除 `tests/output/` 下的临时文件（`.gitkeep` 保留）

---

## 二、测试环境准备

### 2.1 工具路径

测试使用 `publish/PLAN/` 下的工具（打包前的组件源目录）：

| 工具 | 路径 |
|---|---|
| ffmpeg / ffprobe | `publish/PLAN/ffmpeg-full/`（目录名可含日期变体，如 `ffmpeg-full-2026.7.24`） |
| cjxl / djxl / cjpegli / jxlinfo | `publish/PLAN/jxl/bin/` |
| exiftool | `publish/PLAN/exiftool/` |

> 提示：用 PowerShell 变量定位，避免硬编码具体目录名：
> ```powershell
> $ffmpeg = (Get-ChildItem publish/PLAN/ffmpeg-full*/ffmpeg.exe | Select-Object -First 1).FullName
> $cjxl   = "publish/PLAN/jxl/bin/cjxl.exe"
> $exif   = "publish/PLAN/exiftool/exiftool.exe"
> ```

### 2.2 生成测试图片

```powershell
$out = "tests/output"
New-Item -ItemType Directory -Force -Path $out | Out-Null

# 8-bit PNG
& $ffmpeg -y -f lavfi -i testsrc2=size=320x240:duration=0.1 -frames:v 1 "$out/src_8bit.png" 2>$null
# 16-bit PNG（高位深）
& $ffmpeg -y -f lavfi -i testsrc2=size=320x240:duration=0.1 -frames:v 1 -pix_fmt rgb48le "$out/src_16bit.png" 2>$null
# JPEG（含 ICC 写入测试）
& $ffmpeg -y -f lavfi -i testsrc2=size=320x240:duration=0.1 -frames:v 1 "$out/src.jpg" 2>$null
& $exif "-icc_profile<=C:\Windows\System32\spool\drivers\color\sRGB Color Space Profile.icm" "$out/src.jpg" 2>$null
# 带 alpha 的 PNG
& $ffmpeg -y -f lavfi -i "color=red:size=64x64:rate=1" -vf "format=rgba" -frames:v 1 "$out/src_alpha.png" 2>$null
```

---

## 三、核心测试矩阵

### 3.1 编码测试（→ JXL）

| 场景 | 命令 | 预期 |
|---|---|---|
| JPEG→JXL 无损重封装 | `cjxl in.jpg out.jxl -d 0 -e 3 --lossless_jpeg=1` | 输出 "lossless transcode" |
| JPEG→JXL 关闭重封装 | `cjxl in.jpg out.jxl -d 2 -e 3 --lossless_jpeg=0` | 正常有损编码 |
| PNG→JXL 无损 | `cjxl in.png out.jxl -d 0 -e 3` | 正常 |
| PNG→JXL 有损 + 光子噪声 | `cjxl in.png out.jxl -d 2 -e 3 -x "icc_pathname=..."` | 正常 |
| TIFF/WebP→JXL | 直接失败 → **必须走 ffmpeg PPM 管道** | 管道成功 |
| 16-bit TIFF + ICC→JXL | `ffmpeg -i in.tiff -pix_fmt rgb48le -f image2pipe -c:v ppm - | cjxl - out.jxl -d 2 -e 3 -x "icc_pathname=icc"` | 输出含 "ICC profile" |

### 3.2 解码测试（JXL → 其他）

| 场景 | 命令 | 预期 |
|---|---|---|
| JXL→PNG (djxl) | `djxl in.jxl out.png` | 正常 |
| JXL→JPEG (djxl) | `djxl in.jxl out.jpg` | 正常 |
| JXL 无损往返 | `djxl in.jxl out.png` 后对比 MD5 | 位深/像素保留 |

### 3.3 色彩验证

```powershell
# 检查 JXL 色彩语义（ICC 是否嵌入：rendering intent 应为 Perceptual）
jxlinfo out.jxl | Select-String "Color space"

# 检查像素格式/位深
ffprobe -v error -select_streams v:0 -show_entries stream=pix_fmt -of csv=p=0 out.png
```

---

## 四、自动化测试脚本模板

将可重复执行的测试写成 `tests/scripts/*.ps1`，统一约定：

```powershell
# 模板：tests/scripts/run-xxx-tests.ps1
$ErrorActionPreference = "Stop"
$out = "tests/output"
New-Item -ItemType Directory -Force -Path $out | Out-Null

$ffmpeg = (Get-ChildItem "publish/PLAN/ffmpeg-full*/ffmpeg.exe" | Select-Object -First 1).FullName
$cjxl = "publish/PLAN/jxl/bin/cjxl.exe"

function Test-Step($name, $script) {
    Write-Host "=== $name ===" -ForegroundColor Cyan
    & $script
    if ($LASTEXITCODE -ne 0) { throw "测试失败: $name" }
    Write-Host "✅ $name" -ForegroundColor Green
}

# 示例测试步骤...
Test-Step "JPEG→JXL 无损重封装" { & $cjxl "$out/src.jpg" "$out/result.jxl" -d 0 -e 3 --lossless_jpeg=1 2>&1 | Out-Null }

Write-Host "`n🎉 全部测试通过!" -ForegroundColor Green
# 结束前清理产物（可选）
# Remove-Item "$out/*" -Exclude ".gitkeep" -Force
```

---

## 五、.gitignore 相关

已忽略的测试路径（见仓库根 `.gitignore`）：

```
tests/output/
tests/tmp/
tests/*.log
test_output/
test-output/
```

新增测试目录时，请同步更新 `.gitignore` 与本规范。

---

## 六、注意事项

1. **PowerShell 重定向陷阱**：`>` 会破坏二进制流（ICC/PNG 管道输出）。提取二进制时用 `cmd /c "exe args > file"` 或在 C# 中重定向 BaseStream
2. **exiftool 无法写入 JXL 的 ICC**：JXL 的 ICC 只能由 cjxl `-x icc_pathname`（PPM/PAM 管道）或输入 PNG 内嵌 ICC 携带
3. **ffprobe 字段顺序**：`-show_entries stream=A,B,C` 逗号分隔仅最后一个字段生效，输出固定为 `pix_fmt,color_space,color_primaries,color_transfer`
4. **测试日志**：长测试建议输出到 `tests/output/test.log` 便于排查
