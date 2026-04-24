#!/usr/bin/env pwsh
# Cortana AvaloniaUI Native AOT publish script
# Output directory: .\Realases\Cortana

$ErrorActionPreference = 'Stop'

$SolutionDir = $PSScriptRoot
$ProjectFile = Join-Path $SolutionDir 'Src\Netor.Cortana.AvaloniaUI\Netor.Cortana.AvaloniaUI.csproj'
$NativeHostProjectFile = Join-Path $SolutionDir 'Src\Plugins\Netor.Cortana.NativeHost\Netor.Cortana.NativeHost.csproj'
$OutputDir = Join-Path $SolutionDir 'Realases\Cortana'

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Cortana AvaloniaUI Native AOT Publish" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Project: $ProjectFile"
Write-Host "Host: $NativeHostProjectFile"
Write-Host "Output: $OutputDir"
Write-Host ""

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$running = Get-Process -Name 'Cortana' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "[*] Stopping running Cortana process..." -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

$cleanExts = @('*.exe', '*.dll', '*.json', '*.config', '*.manifest')
foreach ($ext in $cleanExts) {
    Get-ChildItem -Path $OutputDir -Filter $ext -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

Write-Host "[1/3] Publishing AvaloniaUI (Release | win-x64 | AOT)..." -ForegroundColor Green

dotnet publish $ProjectFile `
    -c Release `
    -o $OutputDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "AvaloniaUI publish failed, exit code: $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "[2/3] Publishing NativeHost (Release | win-x64 | AOT)..." -ForegroundColor Green

dotnet publish $NativeHostProjectFile `
    -c Release `
    -o $OutputDir

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

$exePath = Join-Path $OutputDir 'Cortana.exe'
$nativeHostExePath = Join-Path $OutputDir 'Cortana.NativeHost.exe'
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
        Write-Host "Warning: Cortana.NativeHost.exe was not found. Check publish output." -ForegroundColor Yellow
    }
} else {
    Write-Host "Warning: Cortana.exe was not found. Check publish output." -ForegroundColor Yellow
}