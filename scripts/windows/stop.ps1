$ErrorActionPreference = "Stop"
Get-Process -Name "NandFlashTester.Api" -ErrorAction SilentlyContinue | Stop-Process
Write-Output "已请求停止本机上位机进程。"
