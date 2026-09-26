@echo off
cd /d "%~dp0"
if not exist bedremote.exe call build.cmd
bedremote.exe %*
rem 双击运行时窗口默认会一闪就没：启动失败时停住，让人看得见上面写了什么。
if errorlevel 1 (
  echo.
  pause
)
