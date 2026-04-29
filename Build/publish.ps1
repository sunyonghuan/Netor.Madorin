<#
.SYNOPSIS
    Compatibility wrapper for Build\cortana.publish.ps1.
.DESCRIPTION
    This legacy entry point is kept for old shortcuts. It delegates to cortana.publish.ps1.
#>
param(
    [switch]$SkipNativePlugin
)

$ErrorActionPreference = 'Stop'
$BuildDir = $PSScriptRoot
$Script = Join-Path $BuildDir 'cortana.publish.ps1'

if (-not (Test-Path $Script)) {
    throw "Publish script not found: $Script"
}

& $Script @PSBoundParameters
exit $LASTEXITCODE
