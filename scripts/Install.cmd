@echo off
if not "%~1"=="" goto cli
start "" "%~dp0app\QuickShot.exe" --install-ui
exit /b
:cli
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
pause

