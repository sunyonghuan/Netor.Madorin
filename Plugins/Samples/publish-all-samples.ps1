#!/usr/bin/env pwsh
param(
    [string]$PluginName,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$SamplesDir = $PSScriptRoot
$SolutionDir = Split-Path -Parent $SamplesDir
$ReleaseRoot = Join-Path $SolutionDir 'Realases'

function Get-TargetFramework {
    param([string]$ProjectFile)

    [xml]$projectXml = Get-Content $ProjectFile -Encoding UTF8
    return $projectXml.Project.PropertyGroup.TargetFramework | Select-Object -First 1
}

function Get-PluginRuntime {
    param([string]$ProjectDir, [string]$ProjectFile)

    $manifestPath = Join-Path $ProjectDir 'plugin.json'
    if (Test-Path $manifestPath) {
        $manifestContent = Get-Content $manifestPath -Raw -Encoding UTF8
        if (-not [string]::IsNullOrWhiteSpace($manifestContent)) {
            try {
                $manifest = $manifestContent | ConvertFrom-Json
                if (-not [string]::IsNullOrWhiteSpace($manifest.runtime)) {
                    return $manifest.runtime
                }
            }
            catch {
            }
        }
    }

    [xml]$projectXml = Get-Content $ProjectFile -Encoding UTF8
    $nativePackage = $projectXml.Project.ItemGroup.PackageReference |
        Where-Object { $_.Include -eq 'Netor.Cortana.Plugin.Native' } |
        Select-Object -First 1

    if ($nativePackage) {
        return 'native'
    }

    return 'dotnet'
}

function Copy-PublishOutput {
    param(
        [string]$Runtime,
        [string]$ProjectDir,
        [string]$PublishDir,
        [string]$TargetDir
    )

    if (Test-Path $TargetDir) {
        Remove-Item $TargetDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null

    if ($Runtime -eq 'native') {
        Copy-Item (Join-Path $PublishDir '*.dll') $TargetDir -Force
        Copy-Item (Join-Path $PublishDir 'plugin.json') $TargetDir -Force
        return
    }

    Get-ChildItem $PublishDir -File |
        Where-Object { $_.Extension -notin '.pdb', '.xml' } |
        Copy-Item -Destination $TargetDir -Force

    $manifestPath = Join-Path $ProjectDir 'plugin.json'
    if (Test-Path $manifestPath) {
        Copy-Item $manifestPath $TargetDir -Force
    }
}

function Assert-PublishDirectory {
    param([string]$PublishDir, [string]$ProjectName)

    if (-not (Test-Path $PublishDir)) {
        throw "发布输出目录不存在：$ProjectName -> $PublishDir"
    }
}

$projectDirs = Get-ChildItem $SamplesDir -Directory |
    Where-Object { -not $PluginName -or $_.Name -eq $PluginName } |
    Sort-Object Name

if ($projectDirs.Count -eq 0) {
    throw '未找到可发布的插件目录。'
}

Write-Host '=== Samples 插件批量发布 ===' -ForegroundColor Cyan
Write-Host "Samples 目录: $SamplesDir"
Write-Host "输出目录:   $ReleaseRoot"

foreach ($projectDir in $projectDirs) {
    $projectFile = Get-ChildItem $projectDir.FullName -Filter '*.csproj' | Select-Object -First 1
    if (-not $projectFile) { continue }

    $tfm = Get-TargetFramework $projectFile.FullName
    $runtime = Get-PluginRuntime $projectDir.FullName $projectFile.FullName
    $targetDir = Join-Path $ReleaseRoot $projectDir.Name

    Write-Host "`n[$($projectDir.Name)] runtime=$runtime" -ForegroundColor Yellow

    if ($runtime -eq 'native') {
        dotnet publish $projectFile.FullName -c $Configuration -r win-x64 --nologo -v quiet
        $publishDir = Join-Path $projectDir.FullName "bin\$Configuration\$tfm\win-x64\publish"
    }
    else {
        dotnet publish $projectFile.FullName -c $Configuration --nologo -v quiet
        $publishDir = Join-Path $projectDir.FullName "bin\$Configuration\$tfm\publish"
    }

    if ($LASTEXITCODE -ne 0) {
        throw "发布失败：$($projectDir.Name)"
    }

    Assert-PublishDirectory -PublishDir $publishDir -ProjectName $projectDir.Name
    Copy-PublishOutput -Runtime $runtime -ProjectDir $projectDir.FullName -PublishDir $publishDir -TargetDir $targetDir
    Write-Host "已输出到: $targetDir" -ForegroundColor Green
}

Write-Host "`n全部发布完成。" -ForegroundColor Green