@echo off
setlocal
cd /d "%~dp0"
title AI Status Bar installer
"%~dp0AIStatusBar.exe" --install %*
echo.
pause
