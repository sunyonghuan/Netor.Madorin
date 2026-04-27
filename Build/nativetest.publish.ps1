<#
.SYNOPSIS
    将 NativeTestPlugin AOT 发布产出部署到根目录 Realases\native。
.DESCRIPTION
    1. AOT 编译 NativeTestPlugin
    2. 将产物部署到根目录 Realases\native
.PARAMETER OutputRoot
    自定义输出目录。默认使用根目录 Realases\native。
#>
param(
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
$SolutionDir = $PSScriptRoot
$ProjectDir = Join-Path $SolutionDir 'Samples\NativeTestPlugin'
$PublishDir = Join-Path $ProjectDir 'bin\Release\net10.0\win-x64\publish'
$TargetDir = if ($OutputRoot) { $OutputRoot } else { Join-Path $SolutionDir 'Realases\native' }

Write-Host "=== NativeTestPlugin AOT 发布 ===" -ForegroundColor Cyan
Write-Host "项目目录: $ProjectDir"
Write-Host "输出目录: $TargetDir"

Write-Host "`n[1/2] AOT 编译..." -ForegroundColor Yellow
dotnet publish "$ProjectDir\NativeTestPlugin.csproj" -c Release -r win-x64 --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "AOT 编译失败" }

Write-Host "[2/2] 部署文件..." -ForegroundColor Yellow
if (Test-Path $TargetDir) {
    Remove-Item $TargetDir -Recurse -Force
}
New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null

Copy-Item (Join-Path $PublishDir 'NativeTestPlugin.dll') $TargetDir
Copy-Item (Join-Path $ProjectDir 'plugin.json') $TargetDir

$dllSize = (Get-Item (Join-Path $TargetDir 'NativeTestPlugin.dll')).Length / 1KB
Write-Host "`n=== 部署完成 ===" -ForegroundColor Green
Write-Host "DLL 大小: $([math]::Round($dllSize, 1)) KB"
Write-Host "部署到: $TargetDir"