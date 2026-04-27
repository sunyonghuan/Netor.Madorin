@echo off
setlocal

cd /d "%~dp0"

where pwsh >nul 2>nul
if %errorlevel% equ 0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -Command "Set-Location -Path '%~dp0..'; & '.\avaloniaui.package.ps1'" %*
) else (
    echo [WARN] pwsh.exe was not found, falling back to Windows PowerShell.
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Set-Location -Path '%~dp0..'; & '.\avaloniaui.package.ps1'" %*
)

set "EXITCODE=%ERRORLEVEL%"
pause
exit /b %EXITCODE%