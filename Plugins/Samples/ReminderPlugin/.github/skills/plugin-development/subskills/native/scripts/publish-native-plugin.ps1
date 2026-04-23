param(
    [Parameter(ValueFromRemainingArguments = $true)][object[]]$Args
)

$scriptPath = Join-Path $PSScriptRoot '..\..\..\scripts\publish-native-plugin.ps1'
& $scriptPath @Args
exit $LASTEXITCODE