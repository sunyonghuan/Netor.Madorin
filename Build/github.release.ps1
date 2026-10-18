<#
.SYNOPSIS
    Create a GitHub Release from existing artifacts.
.DESCRIPTION
    Handles tag creation, release notes, and asset upload only.
    Does not run dotnet publish, build archives, or change versions.
.EXAMPLE
    .\github.release.ps1 madorin
.EXAMPLE
    .\github.release.ps1 madorin -ValidateOnly
.EXAMPLE
    .\github.release.ps1 -Tag v1.3.18 -ValidateOnly
#>

param(
    [Parameter(Position = 0)]
    [string]$Brand,

    [string]$Tag,

    [string]$Title,
    [string]$NotesFile,
    [string[]]$AssetPaths,
    [string]$BrandPackageName,
    [string]$BrandReleaseTitlePrefix,
    [string]$TargetCommitish = 'HEAD',
    [string]$RemoteName = 'github',
    [string]$Repo = 'sunyonghuan/Netor.Madorin',

    [switch]$Draft,
    [switch]$Prerelease,
    [switch]$OpenPage,
    [switch]$SkipTagPush,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$BuildDir = $PSScriptRoot
$SolutionDir = (Resolve-Path (Join-Path $BuildDir '..')).Path
Set-Location $SolutionDir

if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

function Get-BaseVersionFromTag {
    param([string]$ReleaseTag)

    $trimmed = $ReleaseTag.TrimStart('v')
    return ($trimmed -split '-')[0]
}

function Get-AvailableBrands {
    $brandsDir = Join-Path $BuildDir 'brands'
    return @(Get-ChildItem $brandsDir -Directory | Where-Object {
        Test-Path (Join-Path $_.FullName "$($_.Name).json") -PathType Leaf
    } | Sort-Object Name | ForEach-Object { $_.Name })
}

function Show-ReleaseUsage {
    Write-Host 'Usage: github.release.cmd <brand> [-Tag vX.Y.Z] [-ValidateOnly]'
    Write-Host 'Tag is optional. It is taken from the newest win-x64 package for the brand.'
    Write-Host 'Available brands:'
    Get-AvailableBrands | ForEach-Object { Write-Host "  $_" }
}

function Get-BrandReleaseConfig {
    param([string]$BrandName)

    $configPath = Join-Path $BuildDir "brands\$BrandName\$BrandName.json"
    if (-not (Test-Path $configPath -PathType Leaf)) {
        throw "Unknown brand: $BrandName. Available brands: $((Get-AvailableBrands) -join ', ')"
    }

    return [IO.File]::ReadAllText($configPath) | ConvertFrom-Json
}

function Get-NewestPackageVersion {
    param([string]$PackageName)

    $packages = @(Get-ChildItem (Join-Path $SolutionDir 'Realases') -File -Filter "$PackageName-v*-win-x64.zip" -ErrorAction SilentlyContinue)
    if ($packages.Count -eq 0) {
        throw "No release package found for ${PackageName}: Realases\$PackageName-v*-win-x64.zip"
    }

    $newest = $packages | Sort-Object {
        if ($_.Name -match '-v([0-9]+(?:\.[0-9]+){1,3})-win-x64\.zip$') {
            [version]$Matches[1]
        } else {
            [version]'0.0'
        }
    }, LastWriteTime -Descending | Select-Object -First 1

    if ($newest.Name -notmatch '-v([0-9]+(?:\.[0-9]+){1,3})-win-x64\.zip$') {
        throw "Cannot read version from package name: $($newest.Name)"
    }

    return $Matches[1]
}

function Resolve-WorkspacePath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $SolutionDir $Path
}

function Test-CommandAvailable {
    param([string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Missing command: $Name"
    }
}

$env:Path = [System.Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
    [System.Environment]::GetEnvironmentVariable('Path', 'User')

Test-CommandAvailable git
Test-CommandAvailable gh

if ([string]::IsNullOrWhiteSpace($Brand) -or $Brand -eq 'list') {
    if ([string]::IsNullOrWhiteSpace($Tag)) {
        Show-ReleaseUsage
        return
    }
} else {
    $brandConfig = Get-BrandReleaseConfig -BrandName $Brand
    if ([string]::IsNullOrWhiteSpace($BrandPackageName)) {
        $BrandPackageName = if ($brandConfig.packageName) { $brandConfig.packageName } else { $brandConfig.englishName }
    }
    if ([string]::IsNullOrWhiteSpace($BrandReleaseTitlePrefix)) {
        $BrandReleaseTitlePrefix = if ($brandConfig.englishName) { $brandConfig.englishName } else { $BrandPackageName }
    }
}

if ([string]::IsNullOrWhiteSpace($BrandPackageName)) {
    $BrandPackageName = 'Madorin'
}
if ([string]::IsNullOrWhiteSpace($BrandReleaseTitlePrefix)) {
    $BrandReleaseTitlePrefix = $BrandPackageName
}
if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = 'v' + (Get-NewestPackageVersion -PackageName $BrandPackageName)
}

$baseVersion = Get-BaseVersionFromTag -ReleaseTag $Tag
if ([string]::IsNullOrWhiteSpace($Title)) {
    $Title = "$BrandReleaseTitlePrefix $Tag"
}

if ([string]::IsNullOrWhiteSpace($NotesFile)) {
    $noteCandidates = @(
        "Docs\release-notes\v$baseVersion\RELEASE.md",
        "Docs\release-notes\$baseVersion\RELEASE.md",
        "Docs\发布归档\备档\release-notes\v$baseVersion\RELEASE.md",
        "Docs\发布归档\备档\release-notes\$baseVersion\RELEASE.md"
    )

    $versionNotesFile = $null
    foreach ($candidate in $noteCandidates) {
        $candidatePath = Join-Path $SolutionDir $candidate
        if (Test-Path $candidatePath) {
            $versionNotesFile = $candidatePath
            break
        }
    }

    if (-not $versionNotesFile) {
        $changelogDir = Join-Path $SolutionDir 'Docs\更新日志'
        if (Test-Path $changelogDir) {
            $versionNotesFile = Get-ChildItem $changelogDir -File -Filter "*v$baseVersion*.md" |
                Sort-Object Name -Descending |
                Select-Object -First 1 -ExpandProperty FullName
        }
    }

    if ($versionNotesFile) {
        $NotesFile = $versionNotesFile
    }
}

if (-not $AssetPaths -or $AssetPaths.Count -eq 0) {
    $AssetPaths = @(
        "Realases\$BrandPackageName-v$baseVersion-win-x64.zip",
        "Realases\$BrandPackageName-v$baseVersion-win-x64.sha256"
    )
}
$resolvedNotesFile = $null
if (-not [string]::IsNullOrWhiteSpace($NotesFile)) {
    $resolvedNotesFile = Resolve-WorkspacePath -Path $NotesFile
}
if ([string]::IsNullOrWhiteSpace($resolvedNotesFile) -or -not (Test-Path $resolvedNotesFile -PathType Leaf)) {
    $generatedNotes = Join-Path ([IO.Path]::GetTempPath()) "github-release-$($Tag.TrimStart('v')).md"
    $noteText = @"
# $Title

Windows x64 release package.

- $BrandPackageName-v$baseVersion-win-x64.zip
- $BrandPackageName-v$baseVersion-win-x64.sha256
"@
    [IO.File]::WriteAllText($generatedNotes, $noteText.Trim() + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    $resolvedNotesFile = $generatedNotes
}
$resolvedAssets = @($AssetPaths | ForEach-Object { Resolve-WorkspacePath -Path $_ })

foreach ($asset in $resolvedAssets) {
    if (-not (Test-Path $asset)) {
        throw "Release asset not found: $asset"
    }
}

try {
    gh auth status *> $null
} catch {
}

if ($LASTEXITCODE -ne 0) {
    throw 'gh is not authenticated. Run gh auth login first.'
}

$localTagExists = -not [string]::IsNullOrWhiteSpace((git tag -l $Tag))
$remoteTagOutput = git ls-remote --tags $RemoteName $Tag
$remoteTagExists = -not [string]::IsNullOrWhiteSpace($remoteTagOutput)

$releaseExists = $false
try {
    gh release view $Tag --repo $Repo --json tagName,name,url *> $null
    $releaseExists = $LASTEXITCODE -eq 0
} catch {
    $releaseExists = $false
}

if ($releaseExists) {
    throw "Release already exists for tag: $Tag"
}

if ($SkipTagPush -and -not $remoteTagExists) {
    throw "Remote tag does not exist, cannot use -SkipTagPush: $Tag"
}

if ($ValidateOnly) {
    Write-Host "Tag:        $Tag" -ForegroundColor Cyan
    Write-Host "Title:      $Title" -ForegroundColor Cyan
    Write-Host "NotesFile:  $resolvedNotesFile" -ForegroundColor Cyan
    Write-Host "Assets:     $($resolvedAssets.Count) files" -ForegroundColor Cyan
    Write-Host "LocalTag:   $localTagExists" -ForegroundColor DarkYellow
    Write-Host "RemoteTag:  $remoteTagExists" -ForegroundColor DarkYellow
    Write-Host 'Validation completed. GitHub Release was not executed.' -ForegroundColor Yellow
    return
}

if (-not $localTagExists) {
    git tag -a $Tag $TargetCommitish -m "Release $Tag"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create local tag: $Tag"
    }
}

if (-not $SkipTagPush -and -not $remoteTagExists) {
    git push $RemoteName $Tag
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to push tag to remote '$RemoteName': $Tag"
    }
}

$ghArgs = @(
    'release', 'create', $Tag,
    '--repo', $Repo,
    '--verify-tag',
    '--title', $Title,
    '--notes-file', $resolvedNotesFile
)

if ($Draft) { $ghArgs += '--draft' }
if ($Prerelease) { $ghArgs += '--prerelease' }
foreach ($asset in $resolvedAssets) { $ghArgs += $asset }

Write-Host "Tag:        $Tag" -ForegroundColor Cyan
Write-Host "Title:      $Title" -ForegroundColor Cyan
Write-Host "NotesFile:  $resolvedNotesFile" -ForegroundColor Cyan
Write-Host "Assets:     $($resolvedAssets.Count) files" -ForegroundColor Cyan

& gh @ghArgs
if ($LASTEXITCODE -ne 0) {
    throw "GitHub Release creation failed for tag: $Tag"
}

$releaseInfo = gh release view $Tag --repo $Repo --json url,tagName,name,isDraft,isPrerelease
if ($LASTEXITCODE -ne 0) {
    throw "Failed to read GitHub Release after creation: $Tag"
}

Write-Host ''
Write-Host 'Release created successfully.' -ForegroundColor Green
Write-Host $releaseInfo

if ($OpenPage) {
    Start-Process "https://github.com/$Repo/releases/tag/$Tag"
}
