#!/usr/bin/env pwsh
# Publish after adding Visual Studio Installer tools to PATH.

param(
    [string]$BrandDesktopAssemblyName = 'Madorin',
    [string]$BrandAssemblyTitle = 'Madorin',
    [string]$BrandNativeHostAssemblyName = 'Madorin.NativeHost',
    [string]$BrandApplicationIcon = 'Assets\logo.200.ico',
    [string]$BrandNativeHostIcon = 'logo.200.ico',
    [string]$BrandReleaseDirectoryName = 'Cortana',
    [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'

$BuildDir = $PSScriptRoot
$vswhereCommand = Get-Command 'vswhere.exe' -ErrorAction SilentlyContinue
if ($vswhereCommand) {
    Write-Host '[+] vswhere.exe is already available in PATH.' -ForegroundColor Green
} else {
    $programFilesX86 = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::ProgramFilesX86)
    $vswherePath = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'

    if (Test-Path $vswherePath) {
        $env:PATH = (Split-Path $vswherePath -Parent) + ';' + $env:PATH
        Write-Host '[+] Added the Visual Studio Installer directory to PATH.' -ForegroundColor Green
    } else {
        Write-Host '[!] vswhere.exe was not found. Native AOT linking may fail.' -ForegroundColor Yellow
    }
}

$publishArgs = @{
    BrandDesktopAssemblyName = $BrandDesktopAssemblyName
    BrandAssemblyTitle = $BrandAssemblyTitle
    BrandNativeHostAssemblyName = $BrandNativeHostAssemblyName
    BrandApplicationIcon = $BrandApplicationIcon
    BrandNativeHostIcon = $BrandNativeHostIcon
    BrandReleaseDirectoryName = $BrandReleaseDirectoryName
}
if ($Rebuild) {
    $publishArgs.Rebuild = $true
}

& (Join-Path $BuildDir 'ui.publish.ps1') @publishArgs
exit $LASTEXITCODE
