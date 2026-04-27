@echo off
chcp 65001 >nul 2>&1
title NativeTestPlugin 发布
echo.
echo ============================================
echo   NativeTestPlugin AOT Publish
echo ============================================
echo.
pwsh -NoProfile -ExecutionPolicy Bypass -Command "Set-Location -Path '%~dp0..'; & '.\nativetest.publish.ps1'" %*
echo.
pause