param(
    [Parameter(ValueFromRemainingArguments = $true)][object[]]$Args
)

$scriptPath = Join-Path $PSScriptRoot '..\..\..\scripts\setup-dev-environment.ps1'
& $scriptPath @Args
exit $LASTEXITCODE