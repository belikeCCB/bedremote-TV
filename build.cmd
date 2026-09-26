@echo off
rem 改了 src\BedRemote.cs 之后跑这个重新编译（真正的编译逻辑在 build.ps1）
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
