@echo off
setlocal
echo Building current Android APK with ior-core Rust...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-Android.ps1" -OutputDirectory "%~dp0distandroid"
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
