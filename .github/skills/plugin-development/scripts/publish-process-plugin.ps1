param(
    [Parameter(Mandatory = $true)][string]$ProjectDir,
    [string]$PluginName,
    [string]$PluginRoot,
    [switch]$SkipDeploy,
    [switch]$CreateZip,
    [string]$PackageOutputDir,
    [switch]$FrameworkDependent,
    [switch]$Aot
)

$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))) 'skills\plugin-development\scripts\publish-process-plugin.ps1'
& $scriptPath @PSBoundParameters
exit $LASTEXITCODE
