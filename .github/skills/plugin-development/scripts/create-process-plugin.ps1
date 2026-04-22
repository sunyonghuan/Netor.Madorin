param(
    [Parameter(Mandatory = $true)][string]$Name,
    [Parameter(Mandatory = $true)][string]$Id,
    [string]$Description = "插件描述"
)

$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))) 'skills\plugin-development\scripts\create-process-plugin.ps1'
& $scriptPath @PSBoundParameters
exit $LASTEXITCODE
