@echo off
chcp 65001 >nul
title Netor.Cortana GitHub Release
cd /d "%~dp0"
pwsh -ExecutionPolicy Bypass -NoProfile -Command "Set-Location -Path '%~dp0..'; & '.\github.release.ps1'" %*
if errorlevel 1 exit /b 1
pause