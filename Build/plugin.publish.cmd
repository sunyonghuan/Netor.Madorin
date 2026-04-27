@echo off
cd /d "%~dp0"
powershell -ExecutionPolicy Bypass -File ".\plugin.publish.ps1"
if %ERRORLEVEL% neq 0 (
    echo.
    echo Error occurred. Please check the messages above.
    pause
)
