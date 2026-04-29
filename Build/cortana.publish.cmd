@echo off
setlocal
chcp 65001 >nul

title Netor.Cortana Publish

where pwsh >nul 2>nul
if %errorlevel% equ 0 (
    pwsh -ExecutionPolicy Bypass -NoProfile -File "%~dp0cortana.publish.ps1" %*
) else (
    echo [WARN] pwsh.exe was not found, falling back to Windows PowerShell.
    powershell -ExecutionPolicy Bypass -NoProfile -File "%~dp0cortana.publish.ps1" %*
)

set "EXITCODE=%ERRORLEVEL%"
if not "%EXITCODE%"=="0" (
    echo.
    echo Publish failed. Please check the messages above.
    pause
    exit /b %EXITCODE%
)

pause
exit /b 0
