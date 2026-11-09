@echo off
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0stop.ps1"
echo 若窗口一闪而过，说明停止脚本已执行。没有找到进程时也会这样结束。
