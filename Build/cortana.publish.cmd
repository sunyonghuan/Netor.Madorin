@echo off
chcp 65001 >nul
title Netor.Cortana 一键发布
cd /d "%~dp0"
pwsh -ExecutionPolicy Bypass -NoProfile -Command "Set-Location -Path '%~dp0..'; & '.\publish.ps1'" %*
if errorlevel 1 (
    echo.
    echo 发布失败，请检查上方错误信息。
    pause
    exit /b 1
)
pause
