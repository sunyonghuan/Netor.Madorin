@echo off
setlocal

cd /d "%~dp0"

where pwsh >nul 2>nul
if %errorlevel% equ 0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-all-samples.ps1" %*
 ) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-all-samples.ps1" %*
)

set "EXITCODE=%ERRORLEVEL%"
pause
exit /b %EXITCODE%