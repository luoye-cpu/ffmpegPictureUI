[CmdletBinding()]
param([switch]$SelfCheck)
# 保持 runner 的 `pwsh -File` 入口稳定；判据正文与自检锚点在同一平铺库文件中。
. (Join-Path $PSScriptRoot '_lib-gui-color-combinations.ps1') -SelfCheck:$SelfCheck
