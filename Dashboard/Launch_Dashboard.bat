@echo off
cd /d "%~dp0"

:: Start Python web server on port 8085 (supports py launcher and python)
where py >nul 2>&1
if %ERRORLEVEL% equ 0 (
    py -m http.server 8085
) else (
    python -m http.server 8085
)
