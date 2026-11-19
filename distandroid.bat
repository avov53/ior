@echo off
setlocal
echo Building Android distribution...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-AndroidDistribution.ps1" %*
if errorlevel 1 (
    echo.
    echo Android build failed. See the error above.
    pause
    exit /b 1
)
echo.
echo Ready: %~dp0distandroid\ior-android-arm64.apk
echo Copy this APK to your Android phone and install it.
pause
exit /b 0
