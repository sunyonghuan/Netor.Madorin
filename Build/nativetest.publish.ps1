<#
.SYNOPSIS
    Publish NativeTestPlugin AOT output to Realases\native.
.PARAMETER OutputRoot
    Custom output directory. Defaults to <repo>\Realases\native.
#>
param(
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'

$BuildDir = $PSScriptRoot
$SolutionDir = (Resolve-Path (Join-Path $BuildDir '..')).Path
$ProjectDir = Join-Path $SolutionDir 'Plugins\Samples\NativeTestPlugin'
$PublishDir = Join-Path $ProjectDir 'bin\Release\net10.0\win-x64\publish'
$TargetDir = if ($OutputRoot) { $OutputRoot } else { Join-Path $SolutionDir 'Realases\native' }

Write-Host '=== NativeTestPlugin AOT Publish ===' -ForegroundColor Cyan
Write-Host "ProjectDir: $ProjectDir"
Write-Host "OutputDir:  $TargetDir"

Write-Host "`n[1/2] Publishing AOT..." -ForegroundColor Yellow
dotnet publish (Join-Path $ProjectDir 'NativeTestPlugin.csproj') -c Release -r win-x64 --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'NativeTestPlugin AOT publish failed.' }

Write-Host '[2/2] Deploying files...' -ForegroundColor Yellow
if (Test-Path $TargetDir) {
    Remove-Item $TargetDir -Recurse -Force
}
New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null

Copy-Item (Join-Path $PublishDir 'NativeTestPlugin.dll') $TargetDir -Force
Copy-Item (Join-Path $ProjectDir 'plugin.json') $TargetDir -Force

$dllSize = (Get-Item (Join-Path $TargetDir 'NativeTestPlugin.dll')).Length / 1KB
Write-Host "`n=== Deploy completed ===" -ForegroundColor Green
Write-Host "DLL size: $([math]::Round($dllSize, 1)) KB"
Write-Host "Output:   $TargetDir"
