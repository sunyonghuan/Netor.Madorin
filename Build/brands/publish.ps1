#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Publish one branded desktop application.
.DESCRIPTION
    Copies the selected brand assets into the project resource locations,
    invokes the branded Native AOT publish flow, and restores the original
    project assets when publishing finishes.
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$Brand,

    [string]$Version,
    [switch]$Package,
    [switch]$UseVsWhereFix,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'

$BrandsDir = $PSScriptRoot
$BuildDir = Split-Path $BrandsDir -Parent
$SolutionDir = (Resolve-Path (Join-Path $BuildDir '..')).Path

$BrandDir = Join-Path $BrandsDir $Brand
$BrandJson = Join-Path $BrandDir "$Brand.json"
if (-not (Test-Path $BrandDir -PathType Container)) {
    throw "Brand folder not found: $BrandDir"
}
if (-not (Test-Path $BrandJson -PathType Leaf)) {
    throw "Brand config not found: $BrandJson"
}

$UiAssetsDir = Join-Path $SolutionDir 'Src\Netor.Cortana.UI\Assets'
$NativeHostDir = Join-Path $SolutionDir 'Src\Plugins\Netor.Cortana.NativeHost'
$uiIconDest = Join-Path $UiAssetsDir 'brand.ico'
$uiLogoDest = Join-Path $UiAssetsDir 'brand.png'
$hostIconDest = Join-Path $NativeHostDir 'brand.ico'

$srcIcon = Join-Path $BrandDir 'brand.ico'
$srcLogo = Join-Path $BrandDir 'brand.png'
if (-not (Test-Path $srcIcon -PathType Leaf)) {
    throw "Brand icon not found: $srcIcon"
}
if (-not (Test-Path $srcLogo -PathType Leaf)) {
    throw "Brand logo not found: $srcLogo"
}

$backupUiIcon = if (Test-Path $uiIconDest) { [IO.File]::ReadAllBytes($uiIconDest) } else { $null }
$backupUiLogo = if (Test-Path $uiLogoDest) { [IO.File]::ReadAllBytes($uiLogoDest) } else { $null }
$backupHostIcon = if (Test-Path $hostIconDest) { [IO.File]::ReadAllBytes($hostIconDest) } else { $null }

function Restore-File {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [byte[]]$Bytes
    )

    if ($null -ne $Bytes) {
        [IO.File]::WriteAllBytes($Path, $Bytes)
    } elseif (Test-Path $Path) {
        Remove-Item $Path -Force
    }
}

try {
    if (-not $ValidateOnly) {
        Write-Host "Syncing brand assets: '$Brand'..." -ForegroundColor Cyan
        Copy-Item $srcIcon $uiIconDest -Force
        Copy-Item $srcIcon $hostIconDest -Force
        Copy-Item $srcLogo $uiLogoDest -Force
        Write-Host '  brand.ico -> UI/Assets + NativeHost' -ForegroundColor DarkCyan
        Write-Host '  brand.png -> UI/Assets' -ForegroundColor DarkCyan
    }

    $publishScript = Join-Path $BuildDir 'ui.brand.publish.ps1'
    $passArgs = @{ BrandConfigPath = $BrandJson }
    if ($Version) { $passArgs.Version = $Version }
    if ($Package) { $passArgs.Package = $true }
    if ($UseVsWhereFix) { $passArgs.UseVsWhereFix = $true }
    if ($ValidateOnly) { $passArgs.ValidateOnly = $true }

    & $publishScript @passArgs
    $publishExitCode = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    if ($publishExitCode -ne 0) {
        throw "Publish failed, exit code: $publishExitCode"
    }
} finally {
    if (-not $ValidateOnly) {
        Write-Host 'Restoring default brand assets...' -ForegroundColor Yellow
        Restore-File -Path $uiIconDest -Bytes $backupUiIcon
        Restore-File -Path $uiLogoDest -Bytes $backupUiLogo
        Restore-File -Path $hostIconDest -Bytes $backupHostIcon
        Write-Host 'Brand assets restored.' -ForegroundColor Green
    }
}
