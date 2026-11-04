@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-DesktopDistribution.ps1" %*
set "RESULT=%ERRORLEVEL%"
pause
exit /b %RESULT%
