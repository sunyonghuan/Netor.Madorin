@echo off
setlocal

where pwsh >nul 2>nul
if %errorlevel% equ 0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0plugin.publish.ps1" %*
) else (
    echo [WARN] pwsh.exe was not found, falling back to Windows PowerShell.
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0plugin.publish.ps1" %*
)

set "EXITCODE=%ERRORLEVEL%"
if not "%EXITCODE%"=="0" (
    echo.
    echo Error occurred. Please check the messages above.
    pause
)
exit /b %EXITCODE%
