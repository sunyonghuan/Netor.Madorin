<#
.SYNOPSIS
    发布基于 Netor.Cortana.Plugin.Process 框架的 C# Process 插件并部署到插件目录。
.DESCRIPTION
    1. 发布 Process 插件（JIT self-contained / framework-dependent / AOT）
    2. 组装 exe + plugin.json + 运行时依赖
    3. 可选部署到 Cortana 调试目录，可选打 zip 包
.PARAMETER ProjectDir
    插件项目相对于解决方案根目录的路径，如 Samples\MyPlugin。
.PARAMETER PluginName
    插件部署目录名（默认使用项目名的 kebab-case）。
.PARAMETER PluginRoot
    自定义插件部署根目录。
.PARAMETER SkipDeploy
    只发布不部署。
.PARAMETER CreateZip
    输出运行时 zip 包。
.PARAMETER PackageOutputDir
    zip 输出目录。
.PARAMETER FrameworkDependent
    生成 framework-dependent 发布物（需要目标机器安装 .NET 10 Runtime）。
.PARAMETER Aot
    以 Native AOT 模式发布 exe。
#>
param(
    [Parameter(Mandatory = $true)][string]$ProjectDir,
    [string]$PluginName,
    [string]$PluginRoot,
    [switch]$SkipDeploy,
    [switch]$CreateZip,
    [string]$PackageOutputDir,
    [switch]$FrameworkDependent,
    [switch]$Aot
)

$ErrorActionPreference = 'Stop'
. $PSScriptRoot\PluginDev.Common.ps1

if ($FrameworkDependent -and $Aot) {
    Write-Host "❌ -FrameworkDependent 与 -Aot 不能同时使用" -ForegroundColor Red
    exit 1
}

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

$PublishDir = Join-Path $FullProjectDir 'bin\Release\net10.0\win-x64\publish'
$DeployTargets = @()
if (-not $SkipDeploy) {
    if ($PluginRoot) {
        $DeployTargets += $PluginRoot
    } else {
        $DeployTargets += Join-Path $SolutionDir 'Src\Netor.Cortana\bin\Debug\net10.0-windows\.cortana\plugins'
    }
}

$publishArgs = @($csproj.FullName, '-c', 'Release', '-r', 'win-x64', '--nologo', '-v', 'quiet')
$modeLabel = 'JIT self-contained'

if ($Aot) {
    $publishArgs += @('--self-contained', 'true', '/p:PublishAot=true')
    $modeLabel = 'Native AOT'
} elseif ($FrameworkDependent) {
    $publishArgs += @('--self-contained', 'false')
    $modeLabel = 'framework-dependent'
} else {
    $publishArgs += @('--self-contained', 'true')
}

Write-Host "=== Process 插件发布: $ProjectName ===" -ForegroundColor Cyan
Write-Host "模式: $modeLabel" -ForegroundColor Yellow

Write-Host "`n[1/2] 发布编译..." -ForegroundColor Yellow
dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ 发布失败" -ForegroundColor Red
    exit 1
}

$exe = Get-ChildItem -Path $PublishDir -Filter '*.exe' | Select-Object -First 1
if (-not $exe) {
    Write-Host "❌ 发布产出中没有 .exe：$PublishDir" -ForegroundColor Red
    exit 1
}

$pluginJson = Join-Path $PublishDir 'plugin.json'
if (-not (Test-Path $pluginJson)) {
    Write-Host "❌ 发布产出中没有 plugin.json：$pluginJson" -ForegroundColor Red
    exit 1
}

$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString('N'))
$stagingDir = Join-Path $stagingRoot $PluginName
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

Write-Host "[2/2] 组装运行产物..." -ForegroundColor Yellow
Get-ChildItem -Path $PublishDir -File | Where-Object {
    $_.Extension -notin '.pdb', '.xml'
} | ForEach-Object {
    Copy-Item $_.FullName $stagingDir -Force
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

if (Test-Path $stagingRoot) {
    Remove-Item $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "`n=== 发布完成 ===" -ForegroundColor Green
Write-Host "EXE: $($exe.Name)" -ForegroundColor Green
Write-Host "模式: $modeLabel" -ForegroundColor Green
Write-Host "部署到 $($DeployTargets.Count) 个目录" -ForegroundColor Green