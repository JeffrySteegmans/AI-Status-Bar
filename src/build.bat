@echo off
rem Builds both projects into .\dist (bar: WinUI 3, self-contained; hook: tiny single-file exe)
setlocal
cd /d "%~dp0"
dotnet publish AIStatusBar\AIStatusBar.csproj -c Release -o dist || goto :err
dotnet publish AIStatusBarHook\AIStatusBarHook.csproj -c Release -o dist || goto :err
echo.
echo Built. Run dist\install.bat to install.
goto :eof
:err
echo Build failed.
exit /b 1
