@echo off
cd /d "%~dp0"
rem 一键出配对二维码：服务没在跑就后台悄悄拉起来，然后在默认浏览器里打开 /pair。
rem 端口如果改过 bedremote.json，把下面三处 8765 一起改掉。
curl -s -m 2 http://127.0.0.1:8765/health | findstr /c:"ok" >nul 2>&1
if errorlevel 1 (
  wscript //nologo run-hidden.vbs
  timeout /t 2 /nobreak >nul
)
start "" http://127.0.0.1:8765/pair
