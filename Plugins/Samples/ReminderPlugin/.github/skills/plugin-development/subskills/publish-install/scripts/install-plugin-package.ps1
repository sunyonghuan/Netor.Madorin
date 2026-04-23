param(
    [Parameter(ValueFromRemainingArguments = $true)][object[]]$Args
)

$scriptPath = Join-Path $PSScriptRoot '..\..\..\..\skill-plugin-installation\scripts\install-package.ps1'
& $scriptPath @Args
exit $LASTEXITCODE