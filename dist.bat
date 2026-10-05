@echo off
setlocal

set "ROOT=%~dp0"
set "DIST=%ROOT%dist"
set "DIST10=%DIST%\windows-10"
set "DIST11=%DIST%\windows-11"
set "PROJECT=%ROOT%HappBox.csproj"
set "CORE10=%ROOT%core\win10\sing-box.exe"
set "CORE11=%ROOT%core\win11\sing-box.exe"

if not exist "%DIST%" mkdir "%DIST%"

tasklist /FI "IMAGENAME eq ior.exe" 2>nul | find /I "ior.exe" >nul
if not errorlevel 1 (
    echo ior.exe is running.
    echo Close ior before building dist, then run this file again.
    goto error
)

where dotnet >nul 2>nul
if errorlevel 1 (
    echo dotnet was not found. Install .NET 8 SDK first.
    goto error
)

where npm >nul 2>nul
if errorlevel 1 (
    echo Node.js and npm were not found. Install the current Node.js LTS first.
    goto error
)

echo.
echo Cleaning obsolete Rust debug build cache...
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\Clean-RustDebugCache.ps1" -Root "%ROOT%."
if errorlevel 1 (
    echo Rust debug cache cleanup failed. Packaging was stopped for safety.
    goto error
)

echo.
echo Building React interface...
pushd "%ROOT%design"
call npm run build
if errorlevel 1 (
    popd
    echo React interface build failed.
    goto error
)
popd

where cargo >nul 2>nul
if not errorlevel 1 (
    echo.
    echo Building experimental native ior-core...
    pushd "%ROOT%ior-core"
    cargo build --release --offline --locked
    if errorlevel 1 (
        popd
        echo Rust core build failed.
        goto error
    )
    popd
) else (
    echo Cargo was not found in PATH. Packaging sing-box without the ior-core preview binary.
)

dotnet --list-sdks | findstr /R "^8\." >nul
if errorlevel 1 (
    echo .NET 8 SDK was not found. Install .NET 8 SDK first.
    echo Download: https://dotnet.microsoft.com/download/dotnet/8.0
    goto error
)

if not exist "%CORE10%" (
    echo Windows 10 sing-box core was not found:
    echo %CORE10%
    goto error
)

if not exist "%CORE11%" (
    echo Windows 11 sing-box core was not found:
    echo %CORE11%
    goto error
)

if exist "%DIST%" rmdir /s /q "%DIST%"
mkdir "%DIST%"

echo.
echo Building Windows 10 package...
dotnet publish "%PROJECT%" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    /p:DebugType=None ^
    /p:DebugSymbols=false ^
    -o "%DIST10%"

if errorlevel 1 (
    echo Windows 10 publish failed.
    goto error
)

if not exist "%DIST10%\core" mkdir "%DIST10%\core"
copy /Y "%CORE10%" "%DIST10%\core\sing-box.exe" >nul
if errorlevel 1 (
    echo Failed to copy Windows 10 sing-box core.
    goto error
)
break > "%DIST10%\ior.win10"

echo.
echo Building Windows 11 package...
dotnet publish "%PROJECT%" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    /p:DebugType=None ^
    /p:DebugSymbols=false ^
    -o "%DIST11%"

if errorlevel 1 (
    echo Windows 11 publish failed.
    goto error
)

if not exist "%DIST11%\core" mkdir "%DIST11%\core"
copy /Y "%CORE11%" "%DIST11%\core\sing-box.exe" >nul
if errorlevel 1 (
    echo Failed to copy Windows 11 sing-box core.
    goto error
)

echo.
echo Done.
echo Output: %DIST%
echo Windows 10 app: %DIST10%\ior.exe
echo Windows 11 app: %DIST11%\ior.exe
echo Windows 10 core: %DIST10%\core\sing-box.exe
echo Windows 11 core: %DIST11%\core\sing-box.exe
echo.
echo Note: these builds are portable and reliable on paths with Cyrillic characters.
echo Keep all files inside each windows-* folder together when moving the app.

pause
exit /b 0

:error
echo.
echo Build was not completed.
echo Press any key to close this window.
pause >nul
exit /b 1

endlocal
