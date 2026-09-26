@echo off
powershell -NoProfile -Command "Stop-Process -Name bedremote -Force -ErrorAction SilentlyContinue; if ($?) { 'bedremote 已停止' } else { '本来就没在跑' }"
echo.
echo 如果上面没提示"已停止"，去任务管理器找 bedremote.exe 结束它。
