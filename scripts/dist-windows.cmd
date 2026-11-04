@echo off
if not defined IOR_DIST_STAGE exit /b 1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-WindowsDistribution.ps1" -OutputDirectory "%IOR_DIST_STAGE%"
exit /b %ERRORLEVEL%
