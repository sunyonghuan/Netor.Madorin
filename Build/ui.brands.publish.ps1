#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Publish and package all configured UI brands with one shared version.
.DESCRIPTION
    Updates the UI project version, publishes each brand configuration, and
    optionally creates a zip and SHA256 file for every brand.
#>

[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet('patch', 'minor', 'major')]
    [string]$Bump = 'patch',
    [string[]]$BrandConfigPaths = @('brands\madorin\madorin.json', 'brands\sreamx\sreamx.json'),
    [switch]$Package,
    [switch]$UseVsWhereFix,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'

$BuildDir = $PSScriptRoot
$SolutionDir = (Resolve-Path (Join-Path $BuildDir '..')).Path
$ProjectFile = Join-Path $SolutionDir 'Src\Netor.Cortana.UI\Netor.Cortana.UI.csproj'
$BrandPublishScript = Join-Path $BuildDir 'brands\publish.ps1'

function Get-ProjectVersion {
    $content = Get-Content -Path $ProjectFile -Raw -Encoding UTF8
    $match = [regex]::Match($content, '(?m)^\s*<Version>(?<version>[^<]+)</Version>\s*$')
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
        $pattern = $replacement.Key
        $matches = [regex]::Matches($updated, $pattern)
        if ($matches.Count -ne 1) {
            throw "Expected exactly one $($replacement.Key) tag in project file, found $($matches.Count)."
        }
        $updated = $updated.Replace($matches[0].Value, $replacement.Value)
    }

    if ($updated -eq $Content) {
        throw "No version fields were updated in project file: $ProjectFile"
    }

    return $updated
}

function Get-BrandConfigValue {
    param(
        [Parameter(Mandatory = $true)]
        [psobject]$Brand,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $property = $Brand.PSObject.Properties[$Name]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        throw "Brand config is missing required value: $Name"
    }

    return [string]$property.Value
}

function Resolve-BrandConfigPath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return (Resolve-Path $Path).Path
    }

    $buildPath = Join-Path $BuildDir $Path
    if (Test-Path $buildPath) {
        return (Resolve-Path $buildPath).Path
    }

    return (Resolve-Path (Join-Path $SolutionDir $Path)).Path
}

$originalProjectSource = Get-Content -Path $ProjectFile -Raw -Encoding UTF8
$currentVersion = Get-ProjectVersion
$targetVersion = if ([string]::IsNullOrWhiteSpace($Version)) {
    Get-NextVersion -CurrentVersion $currentVersion -BumpKind $Bump
} else {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version must use Major.Minor.Patch format: $Version"
    }
    $Version
}

$resolvedBrandConfigs = @($BrandConfigPaths | ForEach-Object { Resolve-BrandConfigPath $_ })
if ($resolvedBrandConfigs.Count -eq 0) {
    throw 'At least one brand configuration is required.'
}

Write-Host "Project:       $ProjectFile" -ForegroundColor Cyan
Write-Host "Version:       $currentVersion -> $targetVersion" -ForegroundColor Cyan
Write-Host "Brands:        $($resolvedBrandConfigs.Count)" -ForegroundColor Cyan
Write-Host "Package:       $Package" -ForegroundColor Cyan

foreach ($config in $resolvedBrandConfigs) {
    $brand = Get-Content -Path $config -Raw -Encoding UTF8 | ConvertFrom-Json
    $null = Get-BrandConfigValue -Brand $brand -Name 'id'
    $null = Get-BrandConfigValue -Brand $brand -Name 'englishName'
    Write-Host "  - $config" -ForegroundColor DarkGray
}

if ($ValidateOnly) {
    Write-Host 'Validation completed. Publish was not executed.' -ForegroundColor Yellow
    return
}

$publishSucceeded = $false
try {
    $updatedProjectSource = Set-ProjectVersion -Content $originalProjectSource -NewVersion $targetVersion
    [System.IO.File]::WriteAllText($ProjectFile, $updatedProjectSource, [System.Text.UTF8Encoding]::new($false))

    foreach ($config in $resolvedBrandConfigs) {
        $brand = Get-Content -Path $config -Raw -Encoding UTF8 | ConvertFrom-Json
        $brandId = Get-BrandConfigValue -Brand $brand -Name 'id'

        Write-Host ''
        Write-Host "=== Publishing $config (v$targetVersion) ===" -ForegroundColor Green
        $args = @{
            Brand = $brandId
            Version = $targetVersion
            UseVsWhereFix = $UseVsWhereFix
        }
        if ($Package) {
            $args.Package = $true
        }

        & $BrandPublishScript @args
        if ($LASTEXITCODE -ne 0) {
            throw "Brand publish failed for $config, exit code: $LASTEXITCODE"
        }
    }

    $publishSucceeded = $true
    Write-Host ''
    Write-Host "All brands published successfully at v$targetVersion." -ForegroundColor Green
} finally {
    if (-not $publishSucceeded) {
        [System.IO.File]::WriteAllText($ProjectFile, $originalProjectSource, [System.Text.UTF8Encoding]::new($false))
        Write-Host 'Publish failed. The UI project version was restored.' -ForegroundColor Yellow
    }
}
