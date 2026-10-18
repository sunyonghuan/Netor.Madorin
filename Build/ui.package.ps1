<#
.SYNOPSIS
    Package existing UI release output.
.DESCRIPTION
    Creates zip and sha256 files from a branded UI release directory.
#>

param(
    [Parameter(Position = 0)]
    [string]$Brand,
    [string]$Version,
    [string]$BrandReleaseDirectoryName = 'Cortana',
    [string]$BrandPackageName = 'Netor.Cortana',
    [string]$SourceDir,
    [string]$OutputDir = 'Realases',
    [string]$PackageName
)

$ErrorActionPreference = 'Stop'
$BuildDir = $PSScriptRoot
$SolutionDir = (Resolve-Path (Join-Path $BuildDir '..')).Path

# Existing publish callers supply release properties directly.
if ($PSBoundParameters.ContainsKey('Brand') -or
    (-not $PSBoundParameters.ContainsKey('BrandReleaseDirectoryName') -and
     -not $PSBoundParameters.ContainsKey('SourceDir'))) {
    $brandsDir = Join-Path $BuildDir 'brands'
    $availableBrands = @(Get-ChildItem $brandsDir -Directory | Where-Object {
        Test-Path (Join-Path $_.FullName "$($_.Name).json") -PathType Leaf
    } | Sort-Object Name | ForEach-Object { $_.Name })

    if ([string]::IsNullOrWhiteSpace($Brand) -or $Brand -eq 'list') {
        Write-Host 'Usage: ui.package.cmd <list|brand|all> [-Version version]'
        Write-Host 'Available brands:'
        $availableBrands | ForEach-Object { Write-Host "  $_" }
        return
    }

    if ($Brand -ne 'all' -and $Brand -notin $availableBrands) {
        throw "Unknown brand: $Brand. Available brands: $($availableBrands -join ', ')"
    }

    $selectedBrands = @($availableBrands | Where-Object { $Brand -eq 'all' -or $_ -eq $Brand })
    if ($selectedBrands.Count -eq 0) { throw 'No packageable brands were found.' }
    foreach ($selectedBrand in $selectedBrands) {
        $configPath = Join-Path $brandsDir "$selectedBrand\$selectedBrand.json"
        $config = [IO.File]::ReadAllText($configPath) | ConvertFrom-Json
        $releaseName = if ($config.releaseDirectoryName) { $config.releaseDirectoryName } else { $config.englishName }
        $archiveName = if ($config.packageName) { $config.packageName } else { $config.englishName }
        if (-not $releaseName -or -not $archiveName) {
            throw "Brand config is missing release or package names: $configPath"
        }
        $packageArgs = @{
            BrandReleaseDirectoryName = $releaseName
            BrandPackageName = $archiveName
            OutputDir = $OutputDir
        }
        foreach ($option in 'Version', 'SourceDir', 'PackageName') {
            if ($PSBoundParameters.ContainsKey($option)) {
                $packageArgs[$option] = $PSBoundParameters[$option]
            }
        }
        Write-Host "=== Packaging brand: $selectedBrand ===" -ForegroundColor Cyan
        & $PSCommandPath @packageArgs
    }
    return
}

function Resolve-WorkspacePath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $SolutionDir $Path
}

function Get-ProjectVersion {
    $projectFile = Join-Path $SolutionDir 'Src\Netor.Cortana.UI\Netor.Cortana.UI.csproj'
    $projectContent = Get-Content -Path $projectFile -Raw
    $versionMatch = [regex]::Match($projectContent, '<Version>([^<]+)</Version>')
    if (-not $versionMatch.Success) {
        throw "Version was not found in project file: $projectFile"
    }

    return $versionMatch.Groups[1].Value
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-ProjectVersion
}

if ([string]::IsNullOrWhiteSpace($SourceDir)) {
    $SourceDir = Join-Path 'Realases' $BrandReleaseDirectoryName
}

if ([string]::IsNullOrWhiteSpace($PackageName)) {
    $PackageName = $BrandPackageName
}

$resolvedSourceDir = Resolve-WorkspacePath -Path $SourceDir
$resolvedOutputDir = Resolve-WorkspacePath -Path $OutputDir

if (-not (Test-Path $resolvedSourceDir)) {
    throw "Source directory not found: $resolvedSourceDir"
}

if (-not (Test-Path $resolvedOutputDir)) {
    New-Item -ItemType Directory -Path $resolvedOutputDir -Force | Out-Null
}

$zipFileName = "$PackageName-v$Version-win-x64.zip"
$hashFileName = "$PackageName-v$Version-win-x64.sha256"
$zipPath = Join-Path $resolvedOutputDir $zipFileName
$hashPath = Join-Path $resolvedOutputDir $hashFileName

$packageItems = @(Get-ChildItem -Path $resolvedSourceDir -Force |
    Where-Object { $_.Extension -notin @('.zip', '.sha256') })
if ($packageItems.Count -eq 0) {
    throw "Source directory is empty: $resolvedSourceDir"
}

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
if (Test-Path $hashPath) { Remove-Item $hashPath -Force }

Compress-Archive -Path $packageItems.FullName -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash *$zipFileName" | Set-Content -Path $hashPath -Encoding Ascii

$zipSizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
Write-Host "Source:  $resolvedSourceDir" -ForegroundColor Cyan
Write-Host "Zip:     $zipPath" -ForegroundColor Green
Write-Host "Size:    ${zipSizeMb} MB" -ForegroundColor Green
Write-Host "SHA256:  $hashPath" -ForegroundColor Green
