@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-Android.ps1"
if errorlevel 1 (
    echo Android build failed.
    pause
    exit /b 1
)
pause
