param(
    [Parameter(ValueFromRemainingArguments = $true)][object[]]$Args
)

$scriptPath = Join-Path $PSScriptRoot '..\..\..\..\skill-plugin-installation\scripts\install-package.ps1'
if (-not (Test-Path $scriptPath)) {
    Write-Error "未找到安装脚本：$scriptPath"
    exit 1
}

& $scriptPath @Args
exit $LASTEXITCODE
