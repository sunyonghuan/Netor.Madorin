param(
    [Parameter(ValueFromRemainingArguments = $true)][object[]]$Args
)

$scriptPath = Join-Path $PSScriptRoot '..\..\..\scripts\create-native-plugin.ps1'
& $scriptPath @Args
exit $LASTEXITCODE