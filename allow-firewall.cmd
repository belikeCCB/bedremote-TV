@echo off
rem 给 bedremote 放行入站端口。只需要做一次；不想用这个文件的话，
rem 第一次启动 bedremote.exe 时 Windows 会弹防火墙询问，勾上"专用网络"再点"允许访问"也一样。
setlocal
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
netsh advfirewall firewall add rule name=bedremote dir=in action=allow protocol=TCP localport=8765 profile=private,domain program="%~dp0bedremote.exe" enable=yes
netsh advfirewall firewall add rule name=bedremote-8799 dir=in action=allow protocol=TCP localport=8799 profile=private,domain program="%~dp0bedremote.exe" enable=yes
echo.
echo 已放行 TCP 8765 / 8799（仅专用+域网络，公网不放行）。这个窗口可以关了。
pause
