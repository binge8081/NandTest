param([string]$Version = "0.1.0")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$api = Join-Path $root "src/NandFlashTester.Api/build-info.json"
$uiDir = Join-Path $root "web/public"
$ui = Join-Path $uiDir "build-info.json"
if ($env:NAND_BUILD_INFO_LOCK -eq "1" -and (Test-Path $api) -and (Test-Path $ui)) { exit 0 }
New-Item -ItemType Directory -Force -Path $uiDir | Out-Null
$commit = "unknown"
try { $commit = (git -C $root rev-parse HEAD).Trim() } catch { }
$stamp = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
$json = @"
{
  "product": "领存NFA1000闪存测试仪",
  "version": "$Version",
  "commit": "$commit",
  "buildTimeUtc": "$stamp",
  "packagedMode": "Production",
  "entry": "http://127.0.0.1:5080/"
}
"@
[System.IO.File]::WriteAllText($api, $json.Trim() + "`n")
[System.IO.File]::WriteAllText($ui, $json.Trim() + "`n")
