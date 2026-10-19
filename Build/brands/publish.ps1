#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Publish one branded desktop application.
.DESCRIPTION
    Copies the selected brand assets into the project resource locations,
    invokes the branded Native AOT publish flow, and restores the original
    project assets when publishing finishes.
    Unless -Version or -NoBump is set, the UI project patch version is
    increased and written back before compilation.
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$Brand,

    [string]$Version,
    [ValidateSet('patch', 'minor', 'major')]
    [string]$Bump = 'patch',
    [switch]$NoBump,
    [switch]$Package,
    [switch]$UseVsWhereFix,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'

$BrandsDir = $PSScriptRoot
$BuildDir = Split-Path $BrandsDir -Parent
$SolutionDir = (Resolve-Path (Join-Path $BuildDir '..')).Path
$ProjectFile = Join-Path $SolutionDir 'Src\Netor.Cortana.UI\Netor.Cortana.UI.csproj'

$BrandDir = Join-Path $BrandsDir $Brand
$BrandJson = Join-Path $BrandDir "$Brand.json"
if (-not (Test-Path $BrandDir -PathType Container)) {
    throw "Brand folder not found: $BrandDir"
}
if (-not (Test-Path $BrandJson -PathType Leaf)) {
    throw "Brand config not found: $BrandJson"
}

$UiAssetsDir = Join-Path $SolutionDir 'Src\Netor.Cortana.UI\Assets'
$NativeHostDir = Join-Path $SolutionDir 'Src\Plugins\Netor.Madorin.Plugin.NativeHost'
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

function Get-ProjectVersion {
    param([string]$Content)

    $match = [regex]::Match($Content, '(?m)^\s*<Version>(?<version>[^<]+)</Version>\s*$')
    if (-not $match.Success) {
        throw "Version was not found in project file: $ProjectFile"
    }

    return $match.Groups['version'].Value.Trim()
}

function Get-NextVersion {
    param([string]$CurrentVersion, [string]$BumpKind)

    $parts = $CurrentVersion.Split('.')
    if ($parts.Count -ne 3 -or @($parts | Where-Object { $_ -notmatch '^\d+$' }).Count -gt 0) {
        throw "Version must use Major.Minor.Patch format: $CurrentVersion"
    }

    $major = [int]$parts[0]
    $minor = [int]$parts[1]
    $patch = [int]$parts[2]
    switch ($BumpKind) {
        'major' { $major++; $minor = 0; $patch = 0 }
        'minor' { $minor++; $patch = 0 }
        default { $patch++ }
    }

    return "$major.$minor.$patch"
}

function Set-ProjectVersion {
    param([string]$Content, [string]$NewVersion)

    $updated = $Content
    $replacements = @{
        '<Version>[^<]+</Version>' = "<Version>$NewVersion</Version>"
        '<AssemblyVersion>[^<]+</AssemblyVersion>' = "<AssemblyVersion>$NewVersion.0</AssemblyVersion>"
        '<FileVersion>[^<]+</FileVersion>' = "<FileVersion>$NewVersion.0</FileVersion>"
        '<InformationalVersion>[^<]+</InformationalVersion>' = "<InformationalVersion>$NewVersion</InformationalVersion>"
    }

    foreach ($replacement in $replacements.GetEnumerator()) {
        $matches = [regex]::Matches($updated, $replacement.Key)
        if ($matches.Count -ne 1) {
            throw "Expected exactly one version tag for pattern $($replacement.Key), found $($matches.Count)."
        }

        $updated = $updated.Replace($matches[0].Value, $replacement.Value)
    }

    return $updated
}

$originalProjectSource = Get-Content -Path $ProjectFile -Raw -Encoding UTF8
$currentVersion = Get-ProjectVersion -Content $originalProjectSource
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version must use Major.Minor.Patch format: $Version"
    }

    $targetVersion = $Version
} elseif ($NoBump) {
    $targetVersion = $currentVersion
} else {
    $targetVersion = Get-NextVersion -CurrentVersion $currentVersion -BumpKind $Bump
}

Write-Host "Version: $currentVersion -> $targetVersion" -ForegroundColor Cyan

$publishSucceeded = $false
$versionWritten = $false
try {
    if (-not $ValidateOnly) {
        if ($targetVersion -ne $currentVersion) {
            $updatedProjectSource = Set-ProjectVersion -Content $originalProjectSource -NewVersion $targetVersion
            [System.IO.File]::WriteAllText($ProjectFile, $updatedProjectSource, [System.Text.UTF8Encoding]::new($false))
            $versionWritten = $true
            Write-Host "Updated project version to $targetVersion" -ForegroundColor Green
        }

        Write-Host "Syncing brand assets: '$Brand'..." -ForegroundColor Cyan
        Copy-Item $srcIcon $uiIconDest -Force
        Copy-Item $srcIcon $hostIconDest -Force
        Copy-Item $srcLogo $uiLogoDest -Force
        Write-Host '  brand.ico -> UI/Assets + NativeHost' -ForegroundColor DarkCyan
        Write-Host '  brand.png -> UI/Assets' -ForegroundColor DarkCyan
    }

    $publishScript = Join-Path $BuildDir 'ui.brand.publish.ps1'
    $passArgs = @{ BrandConfigPath = $BrandJson }
    $passArgs.Version = $targetVersion
    if ($Package) { $passArgs.Package = $true }
    if ($UseVsWhereFix) { $passArgs.UseVsWhereFix = $true }
    if ($ValidateOnly) { $passArgs.ValidateOnly = $true }

    & $publishScript @passArgs
    $publishExitCode = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    if ($publishExitCode -ne 0) {
        throw "Publish failed, exit code: $publishExitCode"
    }

    $publishSucceeded = $true
} finally {
    if (-not $ValidateOnly) {
        Write-Host 'Restoring default brand assets...' -ForegroundColor Yellow
        Restore-File -Path $uiIconDest -Bytes $backupUiIcon
        Restore-File -Path $uiLogoDest -Bytes $backupUiLogo
        Restore-File -Path $hostIconDest -Bytes $backupHostIcon
        Write-Host 'Brand assets restored.' -ForegroundColor Green
    }

    if ($versionWritten -and -not $publishSucceeded) {
        [System.IO.File]::WriteAllText($ProjectFile, $originalProjectSource, [System.Text.UTF8Encoding]::new($false))
        Write-Host 'Publish failed. The UI project version was restored.' -ForegroundColor Yellow
    }
}
