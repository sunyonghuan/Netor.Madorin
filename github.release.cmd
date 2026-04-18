@echo off
chcp 65001 >nul
title Netor.Cortana GitHub Release
cd /d "%~dp0"
pwsh -ExecutionPolicy Bypass -NoProfile -File "%~dp0github.release.ps1" %*
if errorlevel 1 exit /b 1
pause