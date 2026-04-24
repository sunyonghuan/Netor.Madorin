<#
.SYNOPSIS
    Package existing AvaloniaUI release output.
.DESCRIPTION
    Creates zip and sha256 files from Realases\Cortana only.
#>

param(
    [string]$Version,
    [string]$SourceDir = 'Realases\Cortana',
    [string]$OutputDir = 'Realases',
    [string]$PackageName = 'Netor.Cortana'
)

$ErrorActionPreference = 'Stop'
$SolutionDir = $PSScriptRoot

function Resolve-WorkspacePath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $SolutionDir $Path
}

function Get-ProjectVersion {
    $projectFile = Join-Path $SolutionDir 'Src\Netor.Cortana.AvaloniaUI\Netor.Cortana.AvaloniaUI.csproj'
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

$packageItems = @(Get-ChildItem -Path $resolvedSourceDir -Force)
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