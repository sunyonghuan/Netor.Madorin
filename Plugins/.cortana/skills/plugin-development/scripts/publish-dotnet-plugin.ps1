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
    [string]$PluginRoot
)

$ErrorActionPreference = 'Stop'
$SolutionDir = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$FullProjectDir = Join-Path $SolutionDir $ProjectDir

if (-not (Test-Path $FullProjectDir)) {
    Write-Host "❌ 项目目录不存在：$FullProjectDir" -ForegroundColor Red
    exit 1
}

$csproj = Get-ChildItem -Path $FullProjectDir -Filter "*.csproj" | Select-Object -First 1
if (-not $csproj) {
    Write-Host "❌ 未找到 .csproj 文件" -ForegroundColor Red
    exit 1
}

$ProjectName = $csproj.BaseName
if (-not $PluginName) {
    $PluginName = ($ProjectName -creplace '([A-Z])', '-$1').Trim('-').ToLower()
}

$PublishDir = Join-Path $FullProjectDir "bin\Release\net10.0\publish"

# 部署目标：开发环境部署到 Cortana Debug 输出目录
# 运行时插件目录为 {WorkspaceDirectory}\.cortana\plugins\，由软件自行管理
$DeployTargets = @()
if ($PluginRoot) {
    $DeployTargets += $PluginRoot
} else {
    $DeployTargets += Join-Path $SolutionDir "Src\Netor.Cortana\bin\Debug\net10.0-windows\.cortana\plugins"
}

Write-Host "=== Dotnet 插件发布: $ProjectName ===" -ForegroundColor Cyan

# 1. 发布
Write-Host "`n[1/2] 编译发布..." -ForegroundColor Yellow
dotnet publish $csproj.FullName -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ 编译失败" -ForegroundColor Red
    exit 1
}

# 2. 部署
Write-Host "[2/2] 部署文件..." -ForegroundColor Yellow

# 宿主共享程序集列表（不要复制这些）
$SharedAssemblies = @(
    'Netor.Cortana.Plugin.Abstractions',
    'Microsoft.Extensions.AI.Abstractions',
    'Microsoft.Extensions.Logging.Abstractions',
    'Microsoft.Extensions.DependencyInjection.Abstractions',
    'Microsoft.Extensions.Http'
)

foreach ($target in $DeployTargets) {
    $targetDir = Join-Path $target $PluginName

    if (Test-Path $targetDir) {
        Remove-Item $targetDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null

    # 复制发布产出（排除宿主共享程序集 + .xml/.pdb 等无用文件）
    Get-ChildItem -Path $PublishDir -File | Where-Object {
        $baseName = $_.BaseName
        $ext = $_.Extension.ToLower()
        $ext -notin @('.xml', '.pdb') -and
        -not ($SharedAssemblies | Where-Object { $baseName -eq $_ })
    } | Copy-Item -Destination $targetDir

    # 复制 plugin.json
    $projJson = Join-Path $FullProjectDir "plugin.json"
    if (Test-Path $projJson) {
        Copy-Item $projJson $targetDir
    }

    Write-Host "  ✓ $targetDir" -ForegroundColor Green
}

$fileCount = (Get-ChildItem -Path (Join-Path $DeployTargets[0] $PluginName) -File).Count
Write-Host "`n=== 发布完成 ===" -ForegroundColor Green
Write-Host "部署文件数: $fileCount"
Write-Host "部署到 $($DeployTargets.Count) 个目录"
