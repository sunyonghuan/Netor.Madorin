<#
.SYNOPSIS
    Create a GitHub Release from existing artifacts.
.DESCRIPTION
    Handles tag creation, release notes, and asset upload only.
    Does not run dotnet publish, build archives, or change versions.
.EXAMPLE
    .\github.release.ps1 -Tag v1.1.6-r2
.EXAMPLE
    .\github.release.ps1 -Tag v1.1.6-r2 -ValidateOnly
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [string]$Title,
    [string]$NotesFile,
    [string[]]$AssetPaths,
    [string]$TargetCommitish = 'HEAD',
    [string]$RemoteName = 'github',
    [string]$Repo = 'sunyonghuan/Netor.Cartana',

    [switch]$Draft,
    [switch]$Prerelease,
    [switch]$OpenPage,
    [switch]$SkipTagPush,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$SolutionDir = $PSScriptRoot

if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

function Get-BaseVersionFromTag {
    param([string]$ReleaseTag)

    $trimmed = $ReleaseTag.TrimStart('v')
    return ($trimmed -split '-')[0]
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

$baseVersion = Get-BaseVersionFromTag -ReleaseTag $Tag
if ([string]::IsNullOrWhiteSpace($Title)) {
    $Title = "Netor.Cortana $Tag"
}

if ([string]::IsNullOrWhiteSpace($NotesFile)) {
    $versionNotesFile = Join-Path $SolutionDir "Docs\release-notes\v$baseVersion\RELEASE.md"
    if (-not (Test-Path $versionNotesFile)) {
        $versionNotesFile = Join-Path $SolutionDir "Docs\release-notes\$baseVersion\RELEASE.md"
    }

    $NotesFile = $versionNotesFile
}

if (-not $AssetPaths -or $AssetPaths.Count -eq 0) {
    $AssetPaths = @(
        "Realases\Netor.Cortana-v$baseVersion-win-x64.zip",
        "Realases\Netor.Cortana-v$baseVersion-win-x64.sha256"
    )
}

$resolvedNotesFile = Resolve-WorkspacePath -Path $NotesFile
$resolvedAssets = @($AssetPaths | ForEach-Object { Resolve-WorkspacePath -Path $_ })

if (-not (Test-Path $resolvedNotesFile)) {
    throw "Release notes file not found: $resolvedNotesFile"
}

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
