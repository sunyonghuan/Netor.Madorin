@echo off
chcp 65001 >nul 2>&1
title NativeTestPlugin Publish
echo.
echo ============================================
echo   NativeTestPlugin AOT Publish
echo ============================================
echo.

where pwsh >nul 2>nul
if %errorlevel% equ 0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0nativetest.publish.ps1" %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0nativetest.publish.ps1" %*
)

echo.
pause
