@echo off
powershell -NoProfile -Command "Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name bedremote -ErrorAction SilentlyContinue; '已取消开机自启'"
pause
