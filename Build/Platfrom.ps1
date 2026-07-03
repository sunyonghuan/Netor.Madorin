#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Publish Netor Cortana platform projects for Linux.

.DESCRIPTION
    Publishes Admin / Api / Web to Realases\Admin, Realases\Api and Realases\Web.
    The script intentionally keeps console output ASCII-only to avoid mojibake in Windows PowerShell.
#>

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "linux-x64",
    [switch]$SelfContained
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$BuildDir = $PSScriptRoot
$SolutionDir = (Resolve-Path (Join-Path $BuildDir "..")).Path
$ReleaseRoot = Join-Path $SolutionDir "Realases"

$Projects = @(
    @{
        Name = "Admin"
        Project = Join-Path $SolutionDir "Src\Netor.Cortana.Platform\Netor.Cortana.Platform.Admin\Netor.Cortana.Platform.Admin.csproj"
    },
    @{
        Name = "Api"
        Project = Join-Path $SolutionDir "Src\Netor.Cortana.Platform\Netor.Cortana.Platform.Api\Netor.Cortana.Platform.Api.csproj"
    },
    @{
        Name = "Web"
        Project = Join-Path $SolutionDir "Src\Netor.Cortana.Platform\Netor.Cortana.Platform.Web\Netor.Cortana.Platform.Web.csproj"
    }
)

function Clear-Directory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (Test-Path $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Stop-PlatformProcesses {
    $processNames = @(
        "Netor.Cortana.Platform.Admin",
        "Netor.Cortana.Platform.Api",
        "Netor.Cortana.Platform.Web"
    )

    foreach ($name in $processNames) {
        Get-Process -Name $name -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
    }
}

function Remove-PublishJunk {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $filePatterns = @(
        "*.pdb",
        "*.xml",
        "*.Development.json",
        "*.runtimeconfig.dev.json"
    )

    foreach ($pattern in $filePatterns) {
        Get-ChildItem -Path $Path -Filter $pattern -File -Recurse -ErrorAction SilentlyContinue |
            Remove-Item -Force
    }

    $cultureNames = @(
        "af", "ar", "az", "bg", "bn", "ca", "cs", "da", "de", "el", "es", "et", "fa", "fi", "fr",
        "he", "hi", "hr", "hu", "id", "it", "ja", "kk", "ko", "lt", "lv", "ms", "nb", "nl", "pl",
        "pt", "pt-BR", "ro", "ru", "sk", "sl", "sr", "sv", "th", "tr", "uk", "vi", "zh-Hans", "zh-Hant"
    )

    Get-ChildItem -Path $Path -Directory -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $cultureNames -contains $_.Name } |
        Sort-Object FullName -Descending |
        Remove-Item -Recurse -Force
}

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Cortana Platform Linux Publish" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "Configuration: $Configuration"
Write-Host "Runtime: $Runtime"
Write-Host "Deployment: $(if ($SelfContained) { "SelfContained" } else { "FrameworkDependent" })"
Write-Host "Output: $ReleaseRoot"
Write-Host ""

foreach ($item in $Projects) {
    if (-not (Test-Path $item.Project)) {
        Write-Error "Project file was not found: $($item.Project)"
    }
}

Stop-PlatformProcesses

foreach ($item in $Projects) {
    $outputDir = Join-Path $ReleaseRoot $item.Name

    Write-Host "------------------------------------------------------------" -ForegroundColor DarkGray
    Write-Host "Publishing $($item.Name) -> $outputDir" -ForegroundColor Green

    Clear-Directory -Path $outputDir

    $publishArgs = @(
        "publish", $item.Project,
        "-c", $Configuration,
        "-r", $Runtime,
        "-o", $outputDir,
        "--self-contained", $SelfContained.IsPresent.ToString().ToLowerInvariant(),
        "--disable-build-servers",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-p:GenerateDocumentationFile=false",
        "-p:PublishDocumentationFile=false",
        "-p:SatelliteResourceLanguages=en"
    )

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Error "$($item.Name) publish failed. Exit code: $LASTEXITCODE"
    }

    Remove-PublishJunk -Path $outputDir

    $fileCount = (Get-ChildItem -Path $outputDir -File -Recurse | Measure-Object).Count
    $sizeBytes = (Get-ChildItem -Path $outputDir -File -Recurse | Measure-Object -Property Length -Sum).Sum
    $sizeMb = [math]::Round(($sizeBytes / 1MB), 2)

    Write-Host "$($item.Name) publish completed: $fileCount files, $sizeMb MB" -ForegroundColor Green
}

Write-Host ""
Write-Host "All projects published." -ForegroundColor Green
Write-Host "Path: $ReleaseRoot"
