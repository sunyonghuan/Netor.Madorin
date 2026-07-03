param(
    [Parameter(Mandatory = $true)][string]$ProjectDir
)

$ErrorActionPreference = 'Stop'

$requiredDirs = @('Tools', 'Application')
$optionalDirs = @('Composition', 'Infrastructure', 'Contracts', 'Domain')
$missing = @()

foreach ($dir in $requiredDirs) {
    if (-not (Test-Path (Join-Path $ProjectDir $dir))) {
        $missing += $dir
    }
}

$hasAnyOptional = $false
foreach ($dir in $optionalDirs) {
    if (Test-Path (Join-Path $ProjectDir $dir)) {
        $hasAnyOptional = $true
        break
    }
}

$hasJsonContext = @(Get-ChildItem -Path $ProjectDir -Filter '*JsonContext.cs' -Recurse -ErrorAction SilentlyContinue).Count -gt 0

[pscustomobject]@{
    ProjectDir = $ProjectDir
    MissingRequiredDirectories = $missing
    HasOptionalArchitectureDirectory = $hasAnyOptional
    HasJsonContext = $hasJsonContext
    Passed = ($missing.Count -eq 0 -and $hasAnyOptional)
}