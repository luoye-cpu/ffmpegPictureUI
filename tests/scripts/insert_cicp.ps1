# 构造带 cICP chunk 的 PNG 并验证 ffmpeg 解码端识别
# 用法: powershell -File insert_cicp.ps1
param(
    [string]$Src = "tests/output/results/png10_sbit.png",
    [string]$Dst = "tests/output/results/png_cicp_insert.png"
)

$b = [System.IO.File]::ReadAllBytes((Resolve-Path $Src))

# PNG 签名 8 字节后第一个 chunk 必为 IHDR
$sigLen = 8
$ihdrLen = [System.Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($b, $sigLen))
$ihdrEnd = $sigLen + 4 + 4 + $ihdrLen + 4   # len + type + data + crc

Write-Host "IHDR length: $ihdrLen, chunk end at: $ihdrEnd"

# cICP chunk: len(4)=4, type "cICP", data: BT.2020 primaries=9, PQ transfer=16, matrix=0, full-range=1
$cicpData = [byte[]]@(0x09, 0x10, 0x00, 0x01)
$chunkType = [System.Text.Encoding]::ASCII.GetBytes("cICP")
$lenBytes = [BitConverter]::GetBytes([int]4)
[Array]::Reverse($lenBytes)  # big-endian

# CRC32 (IEEE, 多项式 0xEDB88320 反射表)
function Get-Crc32([byte[]]$data) {
    $crc = [uint32]0xFFFFFFFF
    foreach ($byte in $data) {
        $crc = $crc -bxor [uint32]$byte
        for ($k = 0; $k -lt 8; $k++) {
            if ($crc -band 1) { $crc = (0xEDB88320 -bxor ($crc -shr 1)) } else { $crc = $crc -shr 1 }
        }
    }
    return (-bnot $crc)
}

$crcVal = Get-Crc32 ($chunkType + $cicpData)
$crcBytes = [BitConverter]::GetBytes([int32]$crcVal)
[Array]::Reverse($crcBytes)

# 组装新文件: 签名 + IHDR chunk + cICP chunk + 其余
$list = [System.Collections.Generic.List[byte]]::new()
$list.AddRange($b[0..($ihdrEnd - 1)])
$list.AddRange($lenBytes)
$list.AddRange($chunkType)
$list.AddRange($cicpData)
$list.AddRange($crcBytes)
$list.AddRange($b[$ihdrEnd..($b.Length - 1)])

[System.IO.File]::WriteAllBytes($Dst, $list.ToArray())
Write-Host "Written: $Dst ($($list.Count) bytes)"

# 验证 chunk 顺序
$txt = [System.Text.Encoding]::ASCII.GetString($list.ToArray())
Write-Host "Contains cICP: $($txt.Contains('cICP'))"
