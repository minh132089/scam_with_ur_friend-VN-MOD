@echo off
title Scam With Your Friends - cai mod tieng Viet
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0mod\install.ps1" %*
if errorlevel 1 (
  echo.
  echo  Cai dat KHONG thanh cong. Doc dong mau do phia tren, sua roi chay lai file nay.
)
echo.
pause
