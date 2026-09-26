@echo off
rem 开机自启（写当前用户的 Run 键，不需要管理员）。
rem 不想用命令行的话，等价做法：把 run-hidden.vbs 的快捷方式丢进 shell:startup。
powershell -NoProfile -Command "Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name bedremote -Value ('wscript.exe \"%~dp0run-hidden.vbs\"'); '已加入开机自启：' + (Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name bedremote).bedremote"
echo.
echo 提醒：开机自启等于开机起就一直开着控制端口。要么在 bedremote.json 里填 token，要么别自启。
pause
