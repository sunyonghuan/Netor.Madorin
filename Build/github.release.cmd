@echo off
rem Usage: github.release.cmd brand [-Tag vX.Y.Z] [-ValidateOnly]
rem Tag is optional and comes from the newest package for that brand.
chcp 65001 >nul
title Madorin GitHub Release

where pwsh >nul 2>nul
if %errorlevel% equ 0 (
    pwsh -ExecutionPolicy Bypass -NoProfile -File "%~dp0github.release.ps1" %*
) else (
    powershell -ExecutionPolicy Bypass -NoProfile -File "%~dp0github.release.ps1" %*
)

if errorlevel 1 exit /b 1
pause
