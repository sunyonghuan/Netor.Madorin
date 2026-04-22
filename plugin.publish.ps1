#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Cortana 插件 NuGet 打包 & 推送一键脚本。

.DESCRIPTION
    运行此脚本会自动递增版本号（默认 Patch +1），然后完成
    「递增版本 → 构建 → 打包 → 推送」全流程。

    ╔══════════════════════════════════════════════════════════════╗
    ║  版本号遵循 SemVer：Major.Minor.Patch                        ║
    ║  默认每次发布自动 Patch +1  (如 1.0.3 → 1.0.4)              ║
    ║  可通过 -Bump 参数控制递增级别：                              ║
    ║    -Bump patch  → Patch +1  (如 1.0.3 → 1.0.4)  [默认]      ║
    ║    -Bump minor  → Minor +1  (如 1.0.4 → 1.1.0)              ║
    ║    -Bump major  → Major +1  (如 1.1.0 → 2.0.0)              ║
    ╚══════════════════════════════════════════════════════════════╝

         脚本会自动扫描 Src\Plugins 下的项目目录，
         对其中所有可打包且非 Exe 的项目执行打包与推送。

.PARAMETER Bump
    版本递增级别：patch(默认)、minor、major。

.PARAMETER NuGetSource
    NuGet 服务器地址，默认 http://nuget.netor.me/v3/index.json

.PARAMETER ApiKey
    NuGet 推送 API Key，默认从环境变量 NUGET_API_KEY 读取，
    未设置则使用内置默认值。

.PARAMETER SkipPush
    仅打包不推送，用于本地验证。

.PARAMETER Configuration
    构建配置，默认 Release。
#>

[CmdletBinding()]
param(
    [ValidateSet("patch", "minor", "major")]
    [string]$Bump = "patch",
    [string]$NuGetSource = "http://nuget.netor.me/v3/index.json",
    [string]$ApiKey = "",
    [switch]$SkipPush,
    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ScriptDir = $PSScriptRoot
$SolutionRoot = $ScriptDir
$PluginsDir = Join-Path $SolutionRoot 'Src\Plugins'
$PropsFile = Join-Path $PluginsDir 'Directory.Build.props'

if (-not (Test-Path $PropsFile)) {
    Write-Error "找不到 Directory.Build.props: $PropsFile"
    exit 1
}

function Get-PackProjects([string]$pluginsDir) {
    $projects = New-Object System.Collections.Generic.List[string]

    foreach ($projectDir in (Get-ChildItem -Path $pluginsDir -Directory | Sort-Object Name)) {
        $csproj = Get-ChildItem -Path $projectDir.FullName -Filter "*.csproj" -File | Select-Object -First 1
        if (-not $csproj) {
            continue
        }

        [xml]$projectXml = Get-Content $csproj.FullName -Encoding UTF8
        $isPackableNode = $projectXml.SelectSingleNode("//PropertyGroup/IsPackable")
        $outputTypeNode = $projectXml.SelectSingleNode("//PropertyGroup/OutputType")

        if ($isPackableNode -and [string]::Equals($isPackableNode.InnerText.Trim(), "false", [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        if ($outputTypeNode -and [string]::Equals($outputTypeNode.InnerText.Trim(), "Exe", [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $projects.Add($csproj.FullName)
    }

    return $projects
}

$Projects = @(Get-PackProjects -pluginsDir $PluginsDir)
if ($Projects.Count -eq 0) {
    Write-Error "未在 $PluginsDir 下找到可打包的项目"
    exit 1
}

[xml]$propsXml = Get-Content $PropsFile -Encoding UTF8
$versionNode = $propsXml.SelectSingleNode("//Version")
if (-not $versionNode) {
    Write-Error "Directory.Build.props 中未找到 <Version> 节点"
    exit 1
}
$CurrentVersion = $versionNode.InnerText

$versionParts = $CurrentVersion.Split('.')
if ($versionParts.Length -ne 3) {
    Write-Error "版本号格式不正确，预期 Major.Minor.Patch，实际: $CurrentVersion"
    exit 1
}

$major = [int]$versionParts[0]
$minor = [int]$versionParts[1]
$patch = [int]$versionParts[2]
$OldVersion = $CurrentVersion

switch ($Bump) {
    "major" { $major++; $minor = 0; $patch = 0 }
    "minor" { $minor++; $patch = 0 }
    "patch" { $patch++ }
}

$NewVersion = "$major.$minor.$patch"

$versionNode.InnerText = $NewVersion
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$writer = New-Object System.IO.StreamWriter($PropsFile, $false, $utf8NoBom)
$propsXml.Save($writer)
$writer.Close()

$CurrentVersion = $NewVersion

$outputNode = $propsXml.SelectSingleNode("//PackageOutputPath")
if ($outputNode) {
    $PackageOutputPath = $outputNode.InnerText
} else {
    $PackageOutputPath = Join-Path $SolutionRoot 'Realases\Nupkgs'
}

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    $ApiKey = $env:NUGET_API_KEY
}
if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    $ApiKey = "buday123!@#"
}

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║        Cortana 插件 NuGet 打包 & 推送脚本              ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "  版本号:    $OldVersion → $NewVersion ($Bump +1)" -ForegroundColor Yellow
Write-Host "  配置:      $Configuration"
Write-Host "  输出目录:  $PackageOutputPath"
Write-Host "  NuGet源:   $NuGetSource"
Write-Host "  推送:      $(if ($SkipPush) { '否 (仅打包)' } else { '是' })"
Write-Host "  项目数:    $($Projects.Count)"
Write-Host ""

Write-Host "  自动发现的可打包项目:" -ForegroundColor DarkGray
foreach ($proj in $Projects) {
    Write-Host "    - $([System.IO.Path]::GetFileNameWithoutExtension($proj))" -ForegroundColor DarkGray
}
Write-Host ""

Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor DarkGray
Write-Host "  [1/2] 打包 $($Projects.Count) 个项目 (v$CurrentVersion)" -ForegroundColor Green
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor DarkGray
Write-Host ""

$packSuccess = $true
foreach ($proj in $Projects) {
    $projName = [System.IO.Path]::GetFileNameWithoutExtension($proj)
    Write-Host "  📦 打包 $projName ..." -ForegroundColor White

    dotnet pack $proj -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  ❌ 打包失败: $projName" -ForegroundColor Red
        $packSuccess = $false
        break
    }

    $nupkgFile = Join-Path $PackageOutputPath "$projName.$CurrentVersion.nupkg"
    if (Test-Path $nupkgFile) {
        $size = [math]::Round((Get-Item $nupkgFile).Length / 1KB, 1)
        Write-Host "  ✅ $projName.$CurrentVersion.nupkg (${size} KB)" -ForegroundColor Green
    }
    Write-Host ""
}

if (-not $packSuccess) {
    Write-Host "打包过程中出现错误，已中止。" -ForegroundColor Red
    exit 1
}

if ($SkipPush) {
    Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor DarkGray
    Write-Host "  已跳过推送 (-SkipPush)" -ForegroundColor Yellow
    Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor DarkGray
} else {
    Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor DarkGray
    Write-Host "  [2/2] 推送到 NuGet 服务器" -ForegroundColor Green
    Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor DarkGray
    Write-Host ""

    $pushSuccess = $true
    foreach ($proj in $Projects) {
        $projName = [System.IO.Path]::GetFileNameWithoutExtension($proj)
        $nupkgFile = Join-Path $PackageOutputPath "$projName.$CurrentVersion.nupkg"

        if (-not (Test-Path $nupkgFile)) {
            Write-Host "  ⚠  找不到包: $nupkgFile" -ForegroundColor Red
            $pushSuccess = $false
            break
        }

        Write-Host "  🚀 推送 $projName.$CurrentVersion.nupkg ..." -ForegroundColor White
        dotnet nuget push $nupkgFile --source $NuGetSource --api-key $ApiKey --skip-duplicate
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  ❌ 推送失败: $projName" -ForegroundColor Red
            $pushSuccess = $false
            break
        }
        Write-Host "  ✅ 推送成功" -ForegroundColor Green
        Write-Host ""
    }

    if (-not $pushSuccess) {
        Write-Host "推送过程中出现错误。" -ForegroundColor Red
        exit 1
    }
}

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "║  ✅ 全部完成！版本 $OldVersion → $NewVersion 已发布" -ForegroundColor Green
Write-Host "║  Directory.Build.props 已自动更新为 $NewVersion         ║" -ForegroundColor Green
Write-Host "╚══════════════════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host ""