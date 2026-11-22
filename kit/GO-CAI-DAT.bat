@echo off
title Scam With Your Friends - go mod tieng Viet
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0mod\uninstall.ps1" %*
echo.
pause
