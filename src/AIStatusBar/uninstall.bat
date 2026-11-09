@echo off
setlocal
cd /d "%~dp0"
title AI Status Bar uninstaller
"%~dp0AIStatusBar.exe" --uninstall
echo.
pause
