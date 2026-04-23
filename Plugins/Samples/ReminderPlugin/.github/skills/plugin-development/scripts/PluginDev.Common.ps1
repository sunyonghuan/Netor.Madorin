Set-StrictMode -Version Latest

function Get-PluginDevSolutionDir {
    param([string]$ScriptRoot)

    return (Split-Path -Parent (Split-Path -Parent $ScriptRoot))
}

function Get-PluginDevRepoPackageVersion {
    param(
        [string]$SolutionDir,
        [string]$DefaultVersion = '1.0.8'
    )

    $propsPath = Join-Path $SolutionDir 'Src\Plugins\Directory.Build.props'
    if (-not (Test-Path $propsPath)) {
        return $DefaultVersion
    }

    try {
        [xml]$xml = Get-Content $propsPath -Encoding UTF8
        $version = $xml.Project.PropertyGroup.Version | Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($version)) {
            return $DefaultVersion
        }

        return $version
    }
    catch {
        return $DefaultVersion
    }
}

function Get-PluginProjectFile {
    param([string]$ProjectDir)

    return Get-ChildItem -Path $ProjectDir -Filter '*.csproj' | Select-Object -First 1
}

function ConvertTo-KebabCase {
    param([string]$Value)

    return (($Value -creplace '([A-Z])', '-$1').Trim('-').ToLowerInvariant())
}

function New-PluginPackageZip {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$PackageName,
        [Parameter(Mandatory = $true)][string]$OutputDirectory,
        [string]$Version = '1.0.0'
    )

    if (-not (Test-Path $SourceDirectory)) {
        throw "源目录不存在：$SourceDirectory"
    }

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

    $stageRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString('N'))
    $stagePackageDir = Join-Path $stageRoot $PackageName
    $zipName = "$PackageName.$Version.zip"
    $zipPath = Join-Path $OutputDirectory $zipName

    try {
        New-Item -ItemType Directory -Path $stagePackageDir -Force | Out-Null
        Get-ChildItem -LiteralPath $SourceDirectory -Force | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $stagePackageDir -Recurse -Force
        }

        if (Test-Path $zipPath) {
            Remove-Item $zipPath -Force
        }

        Compress-Archive -Path $stagePackageDir -DestinationPath $zipPath -CompressionLevel Optimal
        return $zipPath
    }
    finally {
        if (Test-Path $stageRoot) {
            Remove-Item $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}