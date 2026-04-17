param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $scriptDir "Office.Test.csproj"
$reportDir = Join-Path $scriptDir "Reports"
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$reportPath = Join-Path $reportDir "office-tool-test-$timestamp.log"

New-Item -ItemType Directory -Force -Path $reportDir | Out-Null

Write-Host "Project: $projectPath"
Write-Host "Report : $reportPath"
Write-Host ""

dotnet run --project $projectPath -c $Configuration -- --test 2>&1 | Tee-Object -FilePath $reportPath

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Report saved to: $reportPath"