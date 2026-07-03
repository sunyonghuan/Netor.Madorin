param(
    [string]$SkillsRoot = "",
    [string]$OutputRoot = "",
    [switch]$RegenerateMetadata
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($SkillsRoot)) {
    $SkillsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}

$SkillsRoot = [IO.Path]::GetFullPath($SkillsRoot)
$installScript = Join-Path $SkillsRoot "skill-plugin-installation\scripts\install-package.ps1"

if (-not (Test-Path -LiteralPath $installScript -PathType Leaf)) {
    throw "未找到技能安装校验脚本：$installScript"
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $SkillsRoot "packages"
}

$stagingRoot = Join-Path $SkillsRoot ".pack"
$excludedDirectories = @(
    [IO.Path]::GetFullPath($OutputRoot),
    [IO.Path]::GetFullPath($stagingRoot)
)

function Test-ExcludedSkillDirectory {
    param([string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    foreach ($excluded in $excludedDirectories) {
        if ($fullPath.StartsWith($excluded, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Get-SkillDirectories {
    param([string]$Root)

    return Get-ChildItem -LiteralPath $Root -Recurse -Directory |
        Where-Object {
            (Test-Path -LiteralPath (Join-Path $_.FullName "SKILL.md") -PathType Leaf) -and
            -not (Test-ExcludedSkillDirectory -Path $_.FullName)
        } |
        Sort-Object FullName
}

function Get-TopLevelSkillDirectories {
    param([string]$Root)

    return Get-ChildItem -LiteralPath $Root -Directory |
        Where-Object {
            (Test-Path -LiteralPath (Join-Path $_.FullName "SKILL.md") -PathType Leaf) -and
            $_.Name -notin @("packages", ".pack")
        } |
        Sort-Object Name
}

function Get-RelativeSkillPath {
    param(
        [string]$BasePath,
        [string]$TargetPath
    )

    if ([string]::IsNullOrWhiteSpace($BasePath)) {
        throw "BasePath 不能为空。"
    }
    if ([string]::IsNullOrWhiteSpace($TargetPath)) {
        return ""
    }

    $normalizedRoot = [IO.Path]::GetFullPath($BasePath).TrimEnd([char[]]@([char]'\', [char]'/'))
    $normalizedPath = [IO.Path]::GetFullPath($TargetPath)

    if ($normalizedPath.StartsWith($normalizedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        $relative = $normalizedPath.Substring($normalizedRoot.Length).TrimStart([char[]]@([char]'\', [char]'/'))
        if (-not [string]::IsNullOrWhiteSpace($relative)) {
            return $relative.Replace("\", "/")
        }
    }

    return [IO.Path]::GetFileName($normalizedPath)
}

function Get-SkillFrontmatter {
    param([string]$SkillFile)

    $content = Get-Content -LiteralPath $SkillFile -Raw -Encoding UTF8
    if ($content -notmatch '(?s)^---\s*(.*?)\s*---') {
        throw "技能文件缺少 YAML 头：$SkillFile"
    }

    $frontmatter = $Matches[1]
    $nameMatch = [regex]::Match($frontmatter, '(?m)^name\s*:\s*(.+?)\s*$')
    $descriptionMatch = [regex]::Match($frontmatter, '(?m)^description\s*:\s*(.+?)\s*$')

    if (-not $nameMatch.Success) {
        throw "技能文件缺少 name：$SkillFile"
    }

    if (-not $descriptionMatch.Success) {
        throw "技能文件缺少 description：$SkillFile"
    }

    return [pscustomobject]@{
        Name = $nameMatch.Groups[1].Value.Trim(" ", "'", '"')
        Description = $descriptionMatch.Groups[1].Value.Trim(" ", "'", '"')
    }
}

function Convert-ToTitleText {
    param([string]$Value)

    $smallWords = @("and", "or", "to", "up", "with")
    $parts = $Value -split "-"
    $formatted = for ($index = 0; $index -lt $parts.Length; $index++) {
        $part = $parts[$index].Trim()
        if ([string]::IsNullOrWhiteSpace($part)) {
            continue
        }

        $lower = $part.ToLowerInvariant()
        if ($index -gt 0 -and $smallWords -contains $lower) {
            $lower
            continue
        }

        switch ($lower) {
            "api" { "API"; continue }
            "mcp" { "MCP"; continue }
            "ssh" { "SSH"; continue }
            "csharp" { "CSharp"; continue }
            default {
                $lower.Substring(0, 1).ToUpperInvariant() + $lower.Substring(1)
            }
        }
    }

    return ($formatted -join " ").Trim()
}

function Get-DisplayName {
    param(
        [string]$Root,
        [string]$SkillPath
    )

    $relativePath = Get-RelativeSkillPath -BasePath $Root -TargetPath $SkillPath
    if ([string]::IsNullOrWhiteSpace($relativePath)) {
        $relativePath = [IO.Path]::GetFileName($SkillPath)
    }

    $segments = $relativePath -split "/"
    $significantSegments = $segments | Where-Object { $_ -ne "subskills" }
    $formatted = $significantSegments | ForEach-Object { Convert-ToTitleText -Value $_ }
    return ($formatted -join " ").Trim()
}

function Get-ShortDescription {
    param([string]$DisplayName)

    $description = "Help with $DisplayName workflows"
    if ($description.Length -gt 64) {
        $description = "Help with $DisplayName tasks"
    }
    if ($description.Length -gt 64) {
        $description = "$DisplayName helper"
    }
    if ($description.Length -gt 64) {
        $maxNameLength = 64 - " helper".Length
        $trimmedName = $DisplayName.Substring(0, [Math]::Max(1, $maxNameLength)).Trim()
        $description = "$trimmedName helper"
    }
    if ($description.Length -lt 25) {
        $description = "Help with $DisplayName tasks and workflows"
    }
    if ($description.Length -gt 64) {
        $description = $description.Substring(0, 64).Trim()
    }

    return $description
}

function Get-DefaultPrompt {
    param(
        [string]$SkillName,
        [string]$DisplayName
    )

    return ("Use $" + $SkillName + " to help with $DisplayName tasks.")
}

function Ensure-OpenAiYaml {
    param(
        [string]$Root,
        [string]$SkillPath
    )

    $skillFile = Join-Path $SkillPath "SKILL.md"
    $frontmatter = Get-SkillFrontmatter -SkillFile $skillFile
    $displayName = Get-DisplayName -Root $Root -SkillPath $SkillPath
    $shortDescription = Get-ShortDescription -DisplayName $displayName
    $defaultPrompt = Get-DefaultPrompt -SkillName $frontmatter.Name -DisplayName $displayName
    $openAiYamlPath = Join-Path $SkillPath "agents\openai.yaml"

    if (-not $RegenerateMetadata.IsPresent -and (Test-Path -LiteralPath $openAiYamlPath -PathType Leaf)) {
        return
    }

    $agentsDirectory = Split-Path -Parent $openAiYamlPath
    New-Item -ItemType Directory -Path $agentsDirectory -Force | Out-Null

    $escape = {
        param([string]$Value)
        return $Value.Replace("\", "\\").Replace('"', '\"')
    }

    $yaml = @(
        "interface:"
        ("  display_name: ""{0}""" -f (& $escape $displayName))
        ("  short_description: ""{0}""" -f (& $escape $shortDescription))
        ("  default_prompt: ""{0}""" -f (& $escape $defaultPrompt))
    ) -join [Environment]::NewLine

    Set-Content -LiteralPath $openAiYamlPath -Value ($yaml + [Environment]::NewLine) -Encoding UTF8
}

function Add-PackageSkillMarkdown {
    param([string]$SkillDirectory)

    $allSkillDirs = @($SkillDirectory) + @(
        Get-ChildItem -LiteralPath $SkillDirectory -Recurse -Directory |
            Where-Object {
                $_.FullName -ne $SkillDirectory -and
                (Test-Path -LiteralPath (Join-Path $_.FullName "SKILL.md") -PathType Leaf)
            } |
            Select-Object -ExpandProperty FullName
    )

    foreach ($dir in $allSkillDirs | Select-Object -Unique) {
        $source = Join-Path $dir "SKILL.md"
        $target = Join-Path $dir "skill.md"
        if (-not ([IO.Path]::GetFullPath($source).Equals([IO.Path]::GetFullPath($target), [StringComparison]::OrdinalIgnoreCase))) {
            Copy-Item -LiteralPath $source -Destination $target -Force
        }
    }
}

function Add-SkillManifestJson {
    param(
        [string]$SkillDirectory,
        [string]$Root,
        [string]$SkillSlug,
        [string]$SkillName,
        [string]$SkillDescription
    )

    $skillFile = Join-Path $SkillDirectory "SKILL.md"
    $relativeSkillPath = Get-RelativeSkillPath -BasePath $Root -TargetPath $SkillDirectory
    $displayName = Get-DisplayName -Root $Root -SkillPath (Join-Path $Root $SkillSlug)
    $description = $SkillDescription
    $version = "1.0.0"
    $rawContent = Get-Content -LiteralPath $skillFile -Raw -Encoding UTF8
    $versionMatch = [regex]::Match($rawContent, '(?m)^version\s*:\s*(.+?)\s*$')
    if ($versionMatch.Success) {
        $parsedVersion = $versionMatch.Groups[1].Value.Trim(" ", "'", '"')
        if (-not [string]::IsNullOrWhiteSpace($parsedVersion)) {
            $version = $parsedVersion
        }
    }

    $author = "Netor"
    if ($rawContent -match '(?m)^publisher\s*:\s*(.+?)\s*$') {
        $parsedPublisher = $Matches[1].Trim(" ", "'", '"')
        if (-not [string]::IsNullOrWhiteSpace($parsedPublisher)) {
            $author = $parsedPublisher
        }
    }

    $manifest = [ordered]@{
        name = $displayName
        slug = $SkillName
        version = $version
        description = $description
        author = $author
        tags = @("skill", "codex", "madorin")
        readme = $description
        releaseNotes = "标准化打包并补齐 skill.json 清单。"
        entry = "SKILL.md"
        assetType = "skill"
        packagePath = $relativeSkillPath.Replace("\", "/")
    }

    $manifestPath = Join-Path $SkillDirectory "skill.json"
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
}

function New-SkillPackage {
    param(
        [string]$SourceDirectory,
        [string]$StageRoot,
        [string]$PackageRoot,
        [string]$SkillName,
        [string]$SkillDescription
    )

    $stageDirectory = Join-Path $StageRoot ([IO.Path]::GetFileName($SourceDirectory))
    Copy-Item -LiteralPath $SourceDirectory -Destination $stageDirectory -Recurse -Force

    Add-PackageSkillMarkdown -SkillDirectory $stageDirectory
    Add-SkillManifestJson -SkillDirectory $stageDirectory -Root $SkillsRoot -SkillSlug ([IO.Path]::GetFileName($SourceDirectory)) -SkillName $SkillName -SkillDescription $SkillDescription

    $zipPath = Join-Path $PackageRoot ([IO.Path]::GetFileName($SourceDirectory) + ".zip")
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -LiteralPath $stageDirectory -DestinationPath $zipPath -CompressionLevel Optimal
    return $zipPath
}

Write-Host "扫描技能目录..." -ForegroundColor Cyan
$allSkillDirectories = Get-SkillDirectories -Root $SkillsRoot
$topLevelSkills = Get-TopLevelSkillDirectories -Root $SkillsRoot

if ($allSkillDirectories.Count -eq 0) {
    throw "未找到任何包含 SKILL.md 的技能目录。"
}

foreach ($skillDir in $allSkillDirectories) {
    $skillPath = [string]$skillDir.FullName
    Ensure-OpenAiYaml -Root $SkillsRoot -SkillPath $skillPath
}

if (Test-Path -LiteralPath $OutputRoot) {
    Remove-Item -LiteralPath $OutputRoot -Recurse -Force
}
if (Test-Path -LiteralPath $stagingRoot) {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null

$packages = New-Object System.Collections.Generic.List[object]
foreach ($skillDir in $topLevelSkills) {
    Write-Host "打包技能: $($skillDir.Name)" -ForegroundColor Yellow
    $sourceDirectory = [string]$skillDir.FullName
    $frontmatter = Get-SkillFrontmatter -SkillFile (Join-Path $sourceDirectory "SKILL.md")
    $zipPath = New-SkillPackage -SourceDirectory $sourceDirectory -StageRoot $stagingRoot -PackageRoot $OutputRoot -SkillName $frontmatter.Name -SkillDescription $frontmatter.Description
    $relativeSkillPath = Get-RelativeSkillPath -BasePath $SkillsRoot -TargetPath $sourceDirectory
    $packages.Add([pscustomobject]@{
        Skill = $relativeSkillPath
        ZipPath = $zipPath
        FileName = [IO.Path]::GetFileName($zipPath)
        Size = (Get-Item -LiteralPath $zipPath).Length
    })
}

$manifestPath = Join-Path $OutputRoot "manifest.json"
$manifest = [pscustomobject]@{
    GeneratedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    TotalSkillsWithMetadata = $allSkillDirectories.Count
    TotalPackages = $packages.Count
    Packages = $packages
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host "校验技能包结构..." -ForegroundColor Cyan
$validationRoot = Join-Path $stagingRoot "validation"
$validation = & $installScript -PackageType skill -SourcePath $OutputRoot -InstallDirectory $validationRoot -Force
if ($validation.Failed -gt 0) {
    throw "技能包校验失败，失败数量：$($validation.Failed)"
}

if (Test-Path -LiteralPath $validationRoot) {
    Remove-Item -LiteralPath $validationRoot -Recurse -Force
}
if (Test-Path -LiteralPath $stagingRoot) {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
}

Write-Host "完成。输出目录：$OutputRoot" -ForegroundColor Green
$packages | Sort-Object FileName | Format-Table FileName, Size, Skill -AutoSize
