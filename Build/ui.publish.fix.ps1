#!/usr/bin/env pwsh
# 修复版发布脚本：自动添加 vswhere.exe 所在目录到 PATH，确保 AOT 链接成功

param(
    [string]$BrandDesktopAssemblyName = 'Madorin',
    [string]$BrandAssemblyTitle = 'Madorin',
    [string]$BrandNativeHostAssemblyName = 'Madorin.NativeHost',
    [string]$BrandApplicationIcon = 'Assets\logo.200.ico',
    [string]$BrandNativeHostIcon = 'logo.200.ico',
    [string]$BrandReleaseDirectoryName = 'Cortana'
)

$ErrorActionPreference = 'Stop'

$BuildDir = $PSScriptRoot

$vswhereCommand = Get-Command 'vswhere.exe' -ErrorAction SilentlyContinue
if ($vswhereCommand) {
    Write-Host "[+] vswhere.exe 已在 PATH 中" -ForegroundColor Green
} else {
    $programFilesX86 = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::ProgramFilesX86)
    $vswherePath = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'

    if (Test-Path $vswherePath) {
        $env:PATH = (Split-Path $vswherePath -Parent) + ';' + $env:PATH
        Write-Host "[+] 已添加 vswhere.exe 到 PATH" -ForegroundColor Green
    } else {
        Write-Host "[!] 未找到 vswhere.exe，AOT 链接可能失败" -ForegroundColor Yellow
    }
}

& (Join-Path $BuildDir 'ui.publish.ps1') `
    -BrandDesktopAssemblyName $BrandDesktopAssemblyName `
    -BrandAssemblyTitle $BrandAssemblyTitle `
    -BrandNativeHostAssemblyName $BrandNativeHostAssemblyName `
    -BrandApplicationIcon $BrandApplicationIcon `
    -BrandNativeHostIcon $BrandNativeHostIcon `
    -BrandReleaseDirectoryName $BrandReleaseDirectoryName
