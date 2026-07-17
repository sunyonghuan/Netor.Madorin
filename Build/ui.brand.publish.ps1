#!/usr/bin/env pwsh
<#
.SYNOPSIS
    根据品牌 JSON 发布桌面端程序。
.DESCRIPTION
    本脚本用于同一套源码发布不同品牌版本。

    工作流程：
    1. 读取 Build/brands 目录下的品牌 JSON。
    2. 发布前临时把 JSON 中的品牌值写入 AppBranding.cs。
    3. 调用 ui.publish.ps1 或 ui.publish.fix.ps1 执行 Native AOT 发布。
    4. 如指定 -Package，则继续调用 ui.package.ps1 打 zip 和 sha256。
    5. 无论发布成功或失败，最后都会恢复原始 AppBranding.cs。

    注意：
    - 品牌 JSON 内的 desktopAssemblyName 会决定主程序 exe 名称。
    - resourceAssemblyName 必须与 desktopAssemblyName 保持一致，否则 Avalonia 资源地址会找不到。
    - 当前 JSON 支持应用名称、程序集名、发布目录、包名、官网 Host、Logo/ICO 路径、平台 API 默认地址。
    - 如果只想测试配置是否能被读取，不要直接发布，请先使用 -ValidateOnly。
    - 如果本机 Native AOT 链接需要 vswhere.exe，请加 -UseVsWhereFix。

.PARAMETER BrandConfigPath
    品牌 JSON 路径。可以写相对 Build 目录的路径，例如 brands\sreamx\sreamx.json；
    也可以写相对仓库根目录或绝对路径。推荐使用 brands\publish.ps1 代替直接调用本脚本。
.PARAMETER Version
    打包时使用的版本号。只在 -Package 时传给 ui.package.ps1。
    不传则由 ui.package.ps1 从 UI 项目文件读取 Version。
.PARAMETER Package
    发布完成后立即打包 zip 和 sha256。
.PARAMETER UseVsWhereFix
    使用 ui.publish.fix.ps1 发布，会先把 Visual Studio Installer 目录加入 PATH，
    用于修复部分机器 Native AOT 找不到 vswhere.exe 的问题。
.PARAMETER ValidateOnly
    只读取并打印品牌配置，不执行发布，不修改 AppBranding.cs。

.EXAMPLE
    # 校验当前默认品牌 Madorin，不发布（推荐通过 brands\publish.ps1 调用）
    powershell -NoProfile -ExecutionPolicy Bypass -File .\brands\publish.ps1 -Brand madorin -ValidateOnly

.EXAMPLE
    # 发布当前默认品牌 Madorin，输出到 Realases\Madorin
    powershell -NoProfile -ExecutionPolicy Bypass -File .\brands\publish.ps1 -Brand madorin -UseVsWhereFix

.EXAMPLE
    # 校验第二品牌 SreamX，不发布
    powershell -NoProfile -ExecutionPolicy Bypass -File .\brands\publish.ps1 -Brand sreamx -ValidateOnly

.EXAMPLE
    # 发布第二品牌 SreamX，输出到 Realases\SreamX
    powershell -NoProfile -ExecutionPolicy Bypass -File .\brands\publish.ps1 -Brand sreamx -UseVsWhereFix

.EXAMPLE
    # 发布并打包第二品牌 SreamX，生成 Realases\SreamX-v版本号-win-x64.zip 和 sha256
    powershell -NoProfile -ExecutionPolicy Bypass -File .\brands\publish.ps1 -Brand sreamx -UseVsWhereFix -Package
#>

param(
    [string]$BrandConfigPath = 'brands\madorin\madorin.json',
    [string]$Version,
    [switch]$Package,
    [switch]$UseVsWhereFix,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'

$BuildDir = $PSScriptRoot
$SolutionDir = (Resolve-Path (Join-Path $BuildDir '..')).Path
$AppBrandingFile = Join-Path $SolutionDir 'Src\Netor.Cortana.Entitys\AppBranding.cs'

function Resolve-WorkspacePath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    $buildRelativePath = Join-Path $BuildDir $Path
    if (Test-Path $buildRelativePath) {
        return (Resolve-Path $buildRelativePath).Path
    }

    return Join-Path $SolutionDir $Path
}

function Get-BrandValue {
    param(
        [Parameter(Mandatory = $true)]
        [psobject]$Brand,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [string]$DefaultValue,

        [switch]$Required
    )

    $property = $Brand.PSObject.Properties[$Name]
    if ($property -and -not [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        return [string]$property.Value
    }

    if ($Required) {
        throw "Brand config is missing required value: $Name"
    }

    return $DefaultValue
}

function Convert-ToCSharpStringLiteral {
    param([string]$Value)

    if ($null -eq $Value) {
        $Value = ''
    }

    $escaped = $Value.
        Replace('\', '\\').
        Replace('"', '\"').
        Replace("`r", '\r').
        Replace("`n", '\n').
        Replace("`t", '\t')

    return '"' + $escaped + '"'
}

function New-AppBrandingSource {
    param([hashtable]$BrandValues)

    $displayName = Convert-ToCSharpStringLiteral $BrandValues.DisplayName
    $assistantSubtitle = Convert-ToCSharpStringLiteral $BrandValues.AssistantSubtitle
    $resourceAssemblyName = Convert-ToCSharpStringLiteral $BrandValues.ResourceAssemblyName
    $desktopAssemblyName = Convert-ToCSharpStringLiteral $BrandValues.DesktopAssemblyName
    $nativeHostAssemblyName = Convert-ToCSharpStringLiteral $BrandValues.NativeHostAssemblyName
    $releaseDirectoryName = Convert-ToCSharpStringLiteral $BrandValues.ReleaseDirectoryName
    $packageName = Convert-ToCSharpStringLiteral $BrandValues.PackageName
    $applicationIconPath = Convert-ToCSharpStringLiteral $BrandValues.ApplicationIconPath
    $logoImageFileName = Convert-ToCSharpStringLiteral $BrandValues.LogoImageFileName
    $websiteHost = Convert-ToCSharpStringLiteral $BrandValues.WebsiteHost
    $platformBaseUrlSettingKey = Convert-ToCSharpStringLiteral $BrandValues.PlatformBaseUrlSettingKey
    $localPlatformApiBaseUrl = Convert-ToCSharpStringLiteral $BrandValues.LocalPlatformApiBaseUrl
    $productionPlatformApiBaseUrl = Convert-ToCSharpStringLiteral $BrandValues.ProductionPlatformApiBaseUrl
    $platformBaseUrlDescription = Convert-ToCSharpStringLiteral $BrandValues.PlatformBaseUrlDescription

    return @"
namespace Netor.Cortana.Entitys;

/// <summary>Application brand defaults for branded publish.</summary>
public static class AppBranding
{
    /// <summary>Application display name.</summary>
    public const string DisplayName = $displayName;

    /// <summary>Application subtitle.</summary>
    public const string AssistantSubtitle = $assistantSubtitle;

    /// <summary>Avalonia resource assembly name.</summary>
    public const string ResourceAssemblyName = $resourceAssemblyName;

    /// <summary>Desktop assembly name without extension.</summary>
    public const string DesktopAssemblyName = $desktopAssemblyName;

    /// <summary>Windows desktop executable file name.</summary>
    public const string DesktopExecutableFileName = DesktopAssemblyName + ".exe";

    /// <summary>Native plugin host assembly name.</summary>
    public const string NativeHostAssemblyName = $nativeHostAssemblyName;

    /// <summary>Windows native plugin host executable file name.</summary>
    public const string NativeHostExecutableFileName = NativeHostAssemblyName + ".exe";

    /// <summary>Default release directory name.</summary>
    public const string ReleaseDirectoryName = $releaseDirectoryName;

    /// <summary>Default package name.</summary>
    public const string PackageName = $packageName;

    /// <summary>Application icon path.</summary>
    public const string ApplicationIconPath = $applicationIconPath;

    /// <summary>Application bitmap logo file name.</summary>
    public const string LogoImageFileName = $logoImageFileName;

    /// <summary>Brand website host.</summary>
    public const string WebsiteHost = $websiteHost;

    /// <summary>Brand website HTTPS base URL.</summary>
    public const string WebsiteBaseUrl = "https://" + WebsiteHost;

    /// <summary>System setting key for the platform API address.</summary>
    public const string PlatformBaseUrlSettingKey = $platformBaseUrlSettingKey;

    /// <summary>Local development platform API base URL.</summary>
    public const string LocalPlatformApiBaseUrl = $localPlatformApiBaseUrl;

    /// <summary>Production platform API base URL.</summary>
    public const string ProductionPlatformApiBaseUrl = $productionPlatformApiBaseUrl;

    /// <summary>Legacy production platform API base URL.</summary>
    public const string LegacyProductionPlatformApiBaseUrl = ProductionPlatformApiBaseUrl;

    /// <summary>Description shown for the platform API base URL setting.</summary>
    public const string PlatformBaseUrlDescription = $platformBaseUrlDescription;

    /// <summary>Builds an Avalonia asset URI from the brand resource assembly.</summary>
    public static string AssetUri(string assetPath)
    {
        var normalizedPath = assetPath.Replace('\\', '/').TrimStart('/');
        const string assetsPrefix = "Assets/";
        if (normalizedPath.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalizedPath = normalizedPath[assetsPrefix.Length..];
        }

        return "avares://" + ResourceAssemblyName + "/Assets/" + normalizedPath;
    }
}
"@
}

$resolvedBrandConfigPath = Resolve-WorkspacePath -Path $BrandConfigPath
if (-not (Test-Path $resolvedBrandConfigPath)) {
    throw "Brand config file not found: $resolvedBrandConfigPath"
}

$brand = Get-Content -Path $resolvedBrandConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
$brandValues = @{
    Id = Get-BrandValue -Brand $brand -Name 'id' -Required
    EnglishName = Get-BrandValue -Brand $brand -Name 'englishName' -Required
    DisplayName = Get-BrandValue -Brand $brand -Name 'displayName' -Required
    AssistantSubtitle = Get-BrandValue -Brand $brand -Name 'assistantSubtitle' -DefaultValue "$($brand.englishName) AI Assistant"
    WebsiteHost = Get-BrandValue -Brand $brand -Name 'websiteHost' -DefaultValue ''
    DesktopAssemblyName = Get-BrandValue -Brand $brand -Name 'desktopAssemblyName' -DefaultValue $brand.englishName
    AssemblyTitle = Get-BrandValue -Brand $brand -Name 'assemblyTitle' -DefaultValue $brand.englishName
    ResourceAssemblyName = Get-BrandValue -Brand $brand -Name 'resourceAssemblyName' -DefaultValue $brand.englishName
    NativeHostAssemblyName = Get-BrandValue -Brand $brand -Name 'nativeHostAssemblyName' -DefaultValue "$($brand.englishName).NativeHost"
    ReleaseDirectoryName = Get-BrandValue -Brand $brand -Name 'releaseDirectoryName' -DefaultValue $brand.englishName
    PackageName = Get-BrandValue -Brand $brand -Name 'packageName' -DefaultValue $brand.englishName
    ApplicationIconPath = Get-BrandValue -Brand $brand -Name 'applicationIconPath' -DefaultValue 'Assets\logo.200.ico'
    NativeHostIcon = Get-BrandValue -Brand $brand -Name 'nativeHostIcon' -DefaultValue 'logo.200.ico'
    LogoImageFileName = Get-BrandValue -Brand $brand -Name 'logoImageFileName' -DefaultValue 'logo.200.png'
    PlatformBaseUrlSettingKey = Get-BrandValue -Brand $brand -Name 'platformBaseUrlSettingKey' -DefaultValue 'Platform.BaseUrl'
    LocalPlatformApiBaseUrl = Get-BrandValue -Brand $brand -Name 'localPlatformApiBaseUrl' -DefaultValue 'http://localhost:5190'
    ProductionPlatformApiBaseUrl = Get-BrandValue -Brand $brand -Name 'productionPlatformApiBaseUrl' -DefaultValue 'https://platform.netor.me'
    PlatformBaseUrlDescription = Get-BrandValue -Brand $brand -Name 'platformBaseUrlDescription' -DefaultValue 'Platform API address used by the store.'
}

Write-Host "Brand config: $resolvedBrandConfigPath" -ForegroundColor Cyan
Write-Host "Brand:        $($brandValues.DisplayName) / $($brandValues.EnglishName)" -ForegroundColor Cyan
Write-Host "WebsiteHost:  $($brandValues.WebsiteHost)" -ForegroundColor Cyan
Write-Host "Assembly:     $($brandValues.DesktopAssemblyName)" -ForegroundColor Cyan
Write-Host "NativeHost:   $($brandValues.NativeHostAssemblyName)" -ForegroundColor Cyan
Write-Host "ReleaseDir:   Realases\$($brandValues.ReleaseDirectoryName)" -ForegroundColor Cyan
Write-Host "PackageName:  $($brandValues.PackageName)" -ForegroundColor Cyan
Write-Host "API:          $($brandValues.ProductionPlatformApiBaseUrl)" -ForegroundColor Cyan

if ($ValidateOnly) {
    Write-Host 'Validation completed. Publish was not executed.' -ForegroundColor Yellow
    return
}

$originalAppBrandingSource = Get-Content -Path $AppBrandingFile -Raw -Encoding UTF8
$published = $false

try {
    $generatedSource = New-AppBrandingSource -BrandValues $brandValues
    Set-Content -Path $AppBrandingFile -Value $generatedSource -Encoding UTF8

    $publishScript = if ($UseVsWhereFix) {
        Join-Path $BuildDir 'ui.publish.fix.ps1'
    } else {
        Join-Path $BuildDir 'ui.publish.ps1'
    }

    & $publishScript `
        -BrandDesktopAssemblyName $brandValues.DesktopAssemblyName `
        -BrandAssemblyTitle $brandValues.AssemblyTitle `
        -BrandNativeHostAssemblyName $brandValues.NativeHostAssemblyName `
        -BrandApplicationIcon $brandValues.ApplicationIconPath `
        -BrandNativeHostIcon $brandValues.NativeHostIcon `
        -BrandReleaseDirectoryName $brandValues.ReleaseDirectoryName `
        -Rebuild

    if ($LASTEXITCODE -ne 0) {
        throw "Brand publish failed, exit code: $LASTEXITCODE"
    }

    $published = $true

    if ($Package) {
        $packageScript = Join-Path $BuildDir 'ui.package.ps1'
        $packageArgs = @{
            BrandReleaseDirectoryName = $brandValues.ReleaseDirectoryName
            BrandPackageName = $brandValues.PackageName
        }

        if (-not [string]::IsNullOrWhiteSpace($Version)) {
            $packageArgs.Version = $Version
        }

        & $packageScript @packageArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Brand package failed, exit code: $LASTEXITCODE"
        }
    }
} finally {
    [System.IO.File]::WriteAllText(
        $AppBrandingFile,
        $originalAppBrandingSource,
        [System.Text.UTF8Encoding]::new($false))

    if ($published) {
        Write-Host "AppBranding.cs restored after publish." -ForegroundColor Green
    } else {
        Write-Host "AppBranding.cs restored." -ForegroundColor Yellow
    }
}
