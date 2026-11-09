$ErrorActionPreference = "Stop"
$procs = Get-Process -Name "NandFlashTester.Api" -ErrorAction SilentlyContinue
if (-not $procs) {
  Write-Output "没有找到正在运行的上位机进程。"
  exit 0
}
$procs | Stop-Process
Write-Output "已请求停止本机上位机进程。"
