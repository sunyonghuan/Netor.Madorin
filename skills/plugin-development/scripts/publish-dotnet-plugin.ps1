<#
.SYNOPSIS
    发布 Dotnet 托管插件并部署到插件目录。
.DESCRIPTION
    1. 编译 Dotnet 插件
    2. 部署 DLL + deps.json + plugin.json + 私有依赖到插件目录
.PARAMETER ProjectDir
    插件项目相对于解决方案根目录的路径，如 Samples\SamplePlugins。
.PARAMETER PluginName
    插件部署目录名（默认使用项目名的 kebab-case）。
.PARAMETER PluginRoot
    自定义插件部署根目录。
#>
param(
    [Parameter(Mandatory=$true)][string]$ProjectDir,
    [string]$PluginName,
    [string]$PluginRoot,
    [switch]$SkipDeploy,
    [switch]$CreateZip,
    [string]$PackageOutputDir
)

$ErrorActionPreference = 'Stop'
. $PSScriptRoot\PluginDev.Common.ps1
$SolutionDir = Get-PluginDevSolutionDir -ScriptRoot $PSScriptRoot
$FullProjectDir = Join-Path $SolutionDir $ProjectDir

if (-not (Test-Path $FullProjectDir)) {
    Write-Host "❌ 项目目录不存在：$FullProjectDir" -ForegroundColor Red
    exit 1
}

$csproj = Get-PluginProjectFile -ProjectDir $FullProjectDir
if (-not $csproj) {
    Write-Host "❌ 未找到 .csproj 文件" -ForegroundColor Red
    exit 1
}

$ProjectName = $csproj.BaseName
if (-not $PluginName) {
    $PluginName = ConvertTo-KebabCase -Value $ProjectName
}

$PublishDir = Join-Path $FullProjectDir "bin\Release\net10.0\publish"

# 部署目标：开发环境部署到 Cortana Debug 输出目录
# 运行时插件目录为 {WorkspaceDirectory}\.cortana\plugins\，由软件自行管理
$DeployTargets = @()
if (-not $SkipDeploy) {
    if ($PluginRoot) {
        $DeployTargets += $PluginRoot
    } else {
        $DeployTargets += Join-Path $SolutionDir "Src\Netor.Cortana\bin\Debug\net10.0-windows\.cortana\plugins"
    }
}

Write-Host "=== Dotnet 插件发布: $ProjectName ===" -ForegroundColor Cyan

# 1. 发布
Write-Host "`n[1/2] 编译发布..." -ForegroundColor Yellow
dotnet publish $csproj.FullName -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ 编译失败" -ForegroundColor Red
    exit 1
}

# 2. 组装最小运行产物
Write-Host "[2/2] 组装运行产物..." -ForegroundColor Yellow

# 宿主共享程序集列表（不要复制这些）
$SharedAssemblies = @(
    'Netor.Cortana.Plugin.Abstractions',
    'Microsoft.Extensions.AI.Abstractions',
    'Microsoft.Extensions.Logging.Abstractions',
    'Microsoft.Extensions.DependencyInjection.Abstractions',
    'Microsoft.Extensions.Http'
)

$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString('N'))
$stagingDir = Join-Path $stagingRoot $PluginName
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

# 复制发布产出（排除宿主共享程序集 + .xml/.pdb 等无用文件）
Get-ChildItem -Path $PublishDir -File | Where-Object {
    $baseName = $_.BaseName
    $ext = $_.Extension.ToLower()
    $ext -notin @('.xml', '.pdb') -and
    -not ($SharedAssemblies | Where-Object { $baseName -eq $_ })
} | Copy-Item -Destination $stagingDir -Force

$projJson = Join-Path $FullProjectDir 'plugin.json'
if (Test-Path $projJson) {
    Copy-Item $projJson $stagingDir -Force
}

foreach ($target in $DeployTargets) {
    $targetDir = Join-Path $target $PluginName

    if (Test-Path $targetDir) {
        Remove-Item $targetDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null

    Get-ChildItem -LiteralPath $stagingDir -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $targetDir -Recurse -Force
    }

    Write-Host "  ✓ $targetDir" -ForegroundColor Green
}

if ($CreateZip) {
    if (-not $PackageOutputDir) {
        $PackageOutputDir = Join-Path $SolutionDir 'Realases\PluginPackages'
    }

    [xml]$projectXml = Get-Content $csproj.FullName -Encoding UTF8
    $version = $projectXml.Project.PropertyGroup.Version | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($version)) {
        $version = '1.0.0'
    }

    $zipPath = New-PluginPackageZip -SourceDirectory $stagingDir -PackageName $PluginName -OutputDirectory $PackageOutputDir -Version $version
    Write-Host "ZIP 包: $zipPath" -ForegroundColor Green
}

$fileCount = (Get-ChildItem -Path $stagingDir -File -ErrorAction SilentlyContinue).Count

if (Test-Path $stagingRoot) {
    Remove-Item $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "`n=== 发布完成 ===" -ForegroundColor Green
Write-Host "部署文件数: $fileCount"
Write-Host "部署到 $($DeployTargets.Count) 个目录"
