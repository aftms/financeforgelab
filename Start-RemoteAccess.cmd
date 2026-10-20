@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Start-RemoteAccess.ps1" -InstallTask -OpenBrowser
if errorlevel 1 pause
