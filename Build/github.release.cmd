@echo off
chcp 65001 >nul
title Netor.Cortana GitHub Release

where pwsh >nul 2>nul
if %errorlevel% equ 0 (
    pwsh -ExecutionPolicy Bypass -NoProfile -File "%~dp0github.release.ps1" %*
) else (
    powershell -ExecutionPolicy Bypass -NoProfile -File "%~dp0github.release.ps1" %*
)

if errorlevel 1 exit /b 1
pause
