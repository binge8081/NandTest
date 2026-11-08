@echo off
setlocal
cd /d "%~dp0"
if not exist "NandFlashTester.Api.exe" (
  echo 未找到 NandFlashTester.Api.exe。请先使用已发布的 win-x64 文件夹。
  exit /b 1
)
start "" /b NandFlashTester.Api.exe
timeout /t 2 /nobreak >nul
start "" http://127.0.0.1:5080/
echo 已尝试在本机浏览器打开 http://127.0.0.1:5080/
echo 停止请运行 stop.ps1。本脚本不访问 D:\NandFlashTester。
