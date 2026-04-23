<#
.SYNOPSIS
    安装 Cortana 技能或插件。
.DESCRIPTION
    支持从单个 zip 文件或目录中的多个 zip 文件安装技能或插件。
    安装前由调用方决定安装到用户数据目录还是当前工作目录。
    安装后会强制校验技能或插件目录结构。
.PARAMETER PackageType
    安装类型：skill 或 plugin。
.PARAMETER SourcePath
    待安装的 zip 文件路径，或包含多个 zip 文件的目录路径。
.PARAMETER InstallDirectory
    目标安装目录。由 AI 直接传入最终目录。
.PARAMETER Force
    如果目标目录已存在，则先删除再覆盖安装。
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('skill', 'plugin')]
    [string]$PackageType,

    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [Parameter(Mandatory = $true)]
    [string]$InstallDirectory,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Test-ValidZip {
    param([string]$ZipPath)

    try {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
        $archive.Dispose()
        return $true
    }
    catch {
        return $false
    }
}

function Get-PackageItems {
    param([string]$ExtractRoot)

    $files = @(Get-ChildItem -LiteralPath $ExtractRoot -File -Force)
    $directories = @(Get-ChildItem -LiteralPath $ExtractRoot -Directory -Force)

    if ($files.Count -eq 0 -and $directories.Count -eq 1) {
        return [pscustomobject]@{
            PackageRoot = $directories[0].FullName
            PackageName = $directories[0].Name
            UsedInnerRoot = $true
        }
    }

    return [pscustomobject]@{
        PackageRoot = $ExtractRoot
        PackageName = $null
        UsedInnerRoot = $false
    }
}

function Test-SkillManifest {
    param([string]$ManifestPath)

    $content = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8
    if ([string]::IsNullOrWhiteSpace($content)) {
        return 'SKILL.md 或 skill.md 内容不能为空。'
    }

    $hasHeading = $content -match '(?m)^#\s+\S+'
    $hasFrontMatter = $content -match '(?s)^---\s*.+?\s+---'
    $hasName = $content -match '(?m)^name\s*:'
    $hasDescription = $content -match '(?m)^description\s*:'

    if (-not $hasHeading) {
        return 'SKILL.md 或 skill.md 必须包含 Markdown 标题。'
    }

    if ($hasFrontMatter -and (-not $hasName -or -not $hasDescription)) {
        return 'SKILL.md 或 skill.md 的 YAML 头至少需要 name 和 description。'
    }

    return $null
}

function Test-PackageStructure {
    param(
        [string]$Type,
        [string]$PackageRoot
    )

    if (-not (Test-Path -LiteralPath $PackageRoot -PathType Container)) {
        return '解压结果不是有效目录。'
    }

    if ($Type -eq 'skill') {
        $manifestCandidates = @(
            (Join-Path $PackageRoot 'SKILL.md'),
            (Join-Path $PackageRoot 'skill.md')
        )
        $manifestPath = $manifestCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
        if (-not $manifestPath) {
            return '技能根目录缺少 SKILL.md 或 skill.md。'
        }

        return (Test-SkillManifest -ManifestPath $manifestPath)
    }

    $pluginJsonPath = Join-Path $PackageRoot 'plugin.json'
    if (-not (Test-Path -LiteralPath $pluginJsonPath -PathType Leaf)) {
        $nestedPluginJson = @(Get-ChildItem -LiteralPath $PackageRoot -Filter 'plugin.json' -File -Recurse -Force | Where-Object { $_.FullName -ne $pluginJsonPath })
        if ($nestedPluginJson.Count -gt 0) {
            return 'plugin.json 不在插件根目录，插件目录被额外嵌套了一层。'
        }

        return '插件根目录缺少 plugin.json。'
    }

    try {
        $null = Get-Content -LiteralPath $pluginJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        return "plugin.json 不是有效的 JSON：$($_.Exception.Message)"
    }

    return $null
}

function Install-PackageArchive {
    param(
        [System.IO.FileInfo]$Archive,
        [string]$Type,
        [string]$TargetRoot,
        [bool]$Overwrite
    )

    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString('N'))
    $extractRoot = Join-Path $tempRoot 'extract'
    New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null

    try {
        [System.IO.Compression.ZipFile]::ExtractToDirectory($Archive.FullName, $extractRoot)

        $packageItems = Get-PackageItems -ExtractRoot $extractRoot
        $packageRoot = $packageItems.PackageRoot
        $packageName = if ([string]::IsNullOrWhiteSpace($packageItems.PackageName)) { $Archive.BaseName } else { $packageItems.PackageName }
        $targetDir = Join-Path $TargetRoot $packageName

        $validationError = Test-PackageStructure -Type $Type -PackageRoot $packageRoot
        if ($null -ne $validationError) {
            return [pscustomobject]@{
                Name = $Archive.Name
                Success = $false
                TargetDirectory = $targetDir
                Message = $validationError
            }
        }

        if (Test-Path -LiteralPath $targetDir) {
            if (-not $Overwrite) {
                return [pscustomobject]@{
                    Name = $Archive.Name
                    Success = $false
                    TargetDirectory = $targetDir
                    Message = '目标目录已存在。请使用 -Force 覆盖安装。'
                }
            }

            Remove-Item -LiteralPath $targetDir -Recurse -Force
        }

        New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
        Get-ChildItem -LiteralPath $packageRoot -Force | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $targetDir -Recurse -Force
        }

        $installedValidationError = Test-PackageStructure -Type $Type -PackageRoot $targetDir
        if ($null -ne $installedValidationError) {
            Remove-Item -LiteralPath $targetDir -Recurse -Force -ErrorAction SilentlyContinue
            return [pscustomobject]@{
                Name = $Archive.Name
                Success = $false
                TargetDirectory = $targetDir
                Message = "安装后校验失败：$installedValidationError"
            }
        }

        return [pscustomobject]@{
            Name = $Archive.Name
            Success = $true
            TargetDirectory = $targetDir
            Message = '安装成功。'
        }
    }
    catch {
        return [pscustomobject]@{
            Name = $Archive.Name
            Success = $false
            TargetDirectory = $null
            Message = $_.Exception.Message
        }
    }
    finally {
        if (Test-Path -LiteralPath $tempRoot) {
            Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

$resolvedSourcePath = [System.IO.Path]::GetFullPath($SourcePath)
if (-not (Test-Path -LiteralPath $resolvedSourcePath)) {
    throw "源路径不存在：$resolvedSourcePath"
}

if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    throw '必须提供 InstallDirectory。'
}

$targetRoot = [System.IO.Path]::GetFullPath($InstallDirectory)
New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null

Write-Host "安装类型: $PackageType" -ForegroundColor Cyan
Write-Host "目标目录: $targetRoot" -ForegroundColor Cyan

$archives = @()
$item = Get-Item -LiteralPath $resolvedSourcePath
if ($item.PSIsContainer) {
    $archives = @(Get-ChildItem -LiteralPath $resolvedSourcePath -File -Filter '*.zip' | Sort-Object Name)
    if ($archives.Count -eq 0) {
        throw '目录中未找到任何 zip 压缩包。'
    }
}
else {
    if ($item.Extension -ne '.zip') {
        throw '输入文件必须是 .zip 压缩包。'
    }

    $archives = @($item)
}

$results = New-Object System.Collections.Generic.List[object]
foreach ($archive in $archives) {
    Write-Host "`n处理压缩包: $($archive.FullName)" -ForegroundColor Yellow

    if (-not (Test-ValidZip -ZipPath $archive.FullName)) {
        $results.Add([pscustomobject]@{
            Name = $archive.Name
            Success = $false
            TargetDirectory = $null
            Message = '不是有效的标准 zip 压缩包。'
        })
        Write-Host '  ✗ 不是有效的标准 zip 压缩包。' -ForegroundColor Red
        continue
    }

    $result = Install-PackageArchive -Archive $archive -Type $PackageType -TargetRoot $targetRoot -Overwrite:$Force.IsPresent
    $results.Add($result)

    if ($result.Success) {
        Write-Host "  ✓ $($result.TargetDirectory)" -ForegroundColor Green
    }
    else {
        Write-Host "  ✗ $($result.Message)" -ForegroundColor Red
    }
}

$successResults = @($results | Where-Object { $_.Success })
$failedResults = @($results | Where-Object { -not $_.Success })

Write-Host "`n安装完成。" -ForegroundColor Cyan
Write-Host "成功: $($successResults.Count)" -ForegroundColor Green
Write-Host "失败: $($failedResults.Count)" -ForegroundColor Red

if ($failedResults.Count -gt 0) {
    Write-Host "`n失败明细:" -ForegroundColor Yellow
    foreach ($failed in $failedResults) {
        Write-Host "- $($failed.Name): $($failed.Message)" -ForegroundColor Yellow
    }
}

[pscustomobject]@{
    PackageType = $PackageType
    TargetRoot = $targetRoot
    SourcePath = $resolvedSourcePath
    Total = $results.Count
    Succeeded = $successResults.Count
    Failed = $failedResults.Count
    Results = $results
}
