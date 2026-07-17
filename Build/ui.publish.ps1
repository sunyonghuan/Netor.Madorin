#!/usr/bin/env pwsh
# UI Native AOT publish script

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
$SolutionDir = (Resolve-Path (Join-Path $BuildDir '..')).Path
$ProjectFile = Join-Path $SolutionDir 'Src\Netor.Cortana.UI\Netor.Cortana.UI.csproj'
$NativeHostProjectFile = Join-Path $SolutionDir 'Src\Plugins\Netor.Cortana.NativeHost\Netor.Cortana.NativeHost.csproj'
$OutputDir = Join-Path $SolutionDir (Join-Path 'Realases' $BrandReleaseDirectoryName)

function Stop-DotNetBuildProcesses {
    Get-Process -Name 'dotnet', 'MSBuild', 'VBCSCompiler' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

function Clear-NuGetHttpCache {
    dotnet nuget locals http-cache --clear | Out-Host
}

function Clear-ProjectReleaseCache {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath
    )

    $projectDirectory = Split-Path $ProjectPath -Parent
    foreach ($directoryName in 'bin\Release', 'obj\Release') {
        $cacheDirectory = Join-Path $projectDirectory $directoryName
        if (Test-Path $cacheDirectory) {
            Remove-Item $cacheDirectory -Recurse -Force
        }
    }
}

function Invoke-DotNetPublishWithRetry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath,

        [Parameter(Mandatory = $true)]
        [string]$OutputPath,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [string[]]$Properties = @()
    )

    if ($Rebuild) {
        Clear-ProjectReleaseCache -ProjectPath $ProjectPath
    }

    $publishArgs = @('publish', $ProjectPath, '-c', 'Release', '-o', $OutputPath, '--disable-build-servers') + $Properties
    if ($Rebuild) {
        $publishArgs += '-t:Rebuild;Publish'
    }
    & dotnet @publishArgs

    if ($LASTEXITCODE -eq 0) {
        return
    }

    Write-Host "$Name publish failed, retrying after clearing build servers and NuGet HTTP cache..." -ForegroundColor Yellow
    Stop-DotNetBuildProcesses
    Clear-NuGetHttpCache

    & dotnet @publishArgs
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  $BrandAssemblyTitle UI Native AOT Publish" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Project: $ProjectFile"
Write-Host "Host: $NativeHostProjectFile"
Write-Host "Output: $OutputDir"
Write-Host "Brand:  $BrandDesktopAssemblyName"
Write-Host ""

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$running = Get-Process -Name $BrandDesktopAssemblyName -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "[*] Stopping running $BrandDesktopAssemblyName process..." -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

$cleanExts = @('*.exe', '*.dll', '*.json', '*.config', '*.manifest', '*.db', '*.db-shm', '*.db-wal')
foreach ($ext in $cleanExts) {
    Get-ChildItem -Path $OutputDir -Filter $ext -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

Write-Host "[1/3] Publishing UI (Release | win-x64 | AOT)..." -ForegroundColor Green

Invoke-DotNetPublishWithRetry `
    -ProjectPath $ProjectFile `
    -OutputPath $OutputDir `
    -Name 'UI' `
    -Properties @(
        "-p:BrandDesktopAssemblyName=$BrandDesktopAssemblyName",
        "-p:BrandAssemblyTitle=$BrandAssemblyTitle",
        "-p:BrandApplicationIcon=$BrandApplicationIcon"
    )

if ($LASTEXITCODE -ne 0) {
    Write-Host "UI publish failed, exit code: $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "[2/3] Publishing NativeHost (Release | win-x64 | AOT)..." -ForegroundColor Green

Invoke-DotNetPublishWithRetry `
    -ProjectPath $NativeHostProjectFile `
    -OutputPath $OutputDir `
    -Name 'NativeHost' `
    -Properties @(
        "-p:BrandNativeHostAssemblyName=$BrandNativeHostAssemblyName",
        "-p:BrandNativeHostIcon=$BrandNativeHostIcon"
    )

if ($LASTEXITCODE -ne 0) {
    Write-Host "NativeHost publish failed, exit code: $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "[3/3] Cleaning extra files..." -ForegroundColor Green
$junkExts = @('*.pdb', '*.xml', '*.deps.json', '*.runtimeconfig.dev.json')
foreach ($ext in $junkExts) {
    Get-ChildItem -Path $OutputDir -Filter $ext -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

$exePath = Join-Path $OutputDir "$BrandDesktopAssemblyName.exe"
$nativeHostExePath = Join-Path $OutputDir "$BrandNativeHostAssemblyName.exe"
if (Test-Path $exePath) {
    $size = [math]::Round((Get-Item $exePath).Length / 1MB, 2)
    Write-Host ""
    Write-Host "Publish completed." -ForegroundColor Green
    Write-Host "  Path: $exePath"
    Write-Host "  Size: ${size} MB"

    if (Test-Path $nativeHostExePath) {
        $nativeHostSize = [math]::Round((Get-Item $nativeHostExePath).Length / 1MB, 2)
        Write-Host "  Host: $nativeHostExePath"
        Write-Host "  Host Size: ${nativeHostSize} MB"
    } else {
        Write-Host "Warning: $BrandNativeHostAssemblyName.exe was not found. Check publish output." -ForegroundColor Yellow
    }
} else {
    Write-Host "Warning: $BrandDesktopAssemblyName.exe was not found. Check publish output." -ForegroundColor Yellow
}
