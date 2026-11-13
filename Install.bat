@echo off
setlocal EnableDelayedExpansion
title vPilot Tablet Dashboard - 1-Click Installer

echo ========================================================================
echo            vPilot Tablet Dashboard ^& Virtual EFB Mirror
echo                         1-Click Installer
echo ========================================================================
echo.

set "SRC_DIR=%~dp0"
set "PLUGIN_SRC=%SRC_DIR%Plugin"
set "DASH_SRC=%SRC_DIR%Dashboard"
set "VPILOT_DIR=%LOCALAPPDATA%\vPilot"
set "VPILOT_PLUGINS=%VPILOT_DIR%\Plugins"

:: 1. Verify Plugin source folder exists
if not exist "%PLUGIN_SRC%\vPilotTabletMirror.dll" (
    echo [ERROR] Could not find "Plugin\vPilotTabletMirror.dll" next to this installer.
    echo Please make sure you extracted the entire ZIP file before running Install.bat!
    echo.
    pause
    exit /b 1
)

:: 2. Check if vPilot is installed
if not exist "%VPILOT_DIR%" (
    echo [WARNING] vPilot folder not found at "%VPILOT_DIR%".
    echo Please ensure vPilot is installed before using this plugin.
    echo.
)

:: 3. Check if vPilot.exe is currently running
tasklist /FI "IMAGENAME eq vPilot.exe" 2>NUL | find /I "vPilot.exe" >NUL
if "%ERRORLEVEL%"=="0" (
    echo [NOTICE] vPilot is currently running.
    echo Closing vPilot so plugin files can be updated cleanly...
    taskkill /IM vPilot.exe >NUL 2>&1
    timeout /t 2 /nobreak >NUL
)

:: 4. Unblock all extracted files to remove Windows "Mark of the Web" security block
echo [1/4] Removing Windows security blocks (Mark of the Web) from files...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -LiteralPath $env:SRC_DIR -Recurse -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue"

:: 5. Create vPilot\Plugins directory if missing and copy DLLs
echo [2/4] Installing plugin DLLs to "%VPILOT_PLUGINS%"...
if not exist "%VPILOT_PLUGINS%" mkdir "%VPILOT_PLUGINS%"

copy /Y "%PLUGIN_SRC%\vPilotTabletMirror.dll" "%VPILOT_PLUGINS%\" >NUL
if errorlevel 1 (
    echo [ERROR] Failed to copy vPilotTabletMirror.dll. Please close vPilot and try again.
    pause
    exit /b 1
)
copy /Y "%PLUGIN_SRC%\Fleck.dll" "%VPILOT_PLUGINS%\" >NUL
copy /Y "%PLUGIN_SRC%\Newtonsoft.Json.dll" "%VPILOT_PLUGINS%\" >NUL

:: Unblock destination DLLs as well for certainty
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -LiteralPath $env:VPILOT_PLUGINS -Filter '*.dll' -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue"

:: 6. Configure dashboard_path.txt if Dashboard folder exists
echo [3/4] Configuring dashboard path...
if exist "%DASH_SRC%\index.html" (
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Set-Content -LiteralPath (Join-Path $env:VPILOT_PLUGINS 'dashboard_path.txt') -Value $env:DASH_SRC -NoNewline"
)

:: 7. Detect local IPv4 address for tablet connection
echo [4/4] Checking your local network IP address...
set "LOCAL_IP="
for /f "usebackq delims=" %%I in (`powershell -NoProfile -Command "(Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -notmatch 'Loopback|vEthernet|WSL|Bluetooth' -and $_.IPAddress -notmatch '^169\.254\.' -and $_.PrefixOrigin -ne 'WellKnown' } | Select-Object -First 1 -ExpandProperty IPAddress)"`) do set "LOCAL_IP=%%I"

echo.
echo ========================================================================
echo                      INSTALLATION COMPLETE!
echo ========================================================================
echo.
echo  Next steps:
echo   1. Start vPilot (the web dashboard server starts automatically - no Python needed!).
echo   2. Connect using your browser:
echo.
echo      * Virtual EFB / Same PC:
echo        http://localhost:8085
echo.
if defined LOCAL_IP goto :show_ip
echo      * Physical Tablet / iPad / Phone (Same Wi-Fi):
echo        Run "ipconfig" to find your IPv4 address, then open:
echo        http://^<YOUR-PC-IP^>:8085
goto :after_ip

:show_ip
echo      * Physical Tablet / iPad / Phone (Same Wi-Fi):
echo        http://%LOCAL_IP%:8085

:after_ip
echo.
echo  * TIP: If your tablet cannot connect, allow TCP ports 8085 and 9090
echo         through Windows Defender Firewall for Private Networks.
echo ========================================================================
echo.
pause
