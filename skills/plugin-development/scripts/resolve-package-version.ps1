<#
.SYNOPSIS
    查询 NuGet 包最新稳定版本。
.DESCRIPTION
    1. 查询 NuGet V3 service index 中的 PackageBaseAddress
    2. 获取包全部版本
    3. 默认返回最新稳定版本；可选包含预发布版本
.PARAMETER PackageId
    包 ID。
.PARAMETER Source
    NuGet V3 service index，默认 nuget.org。
.PARAMETER IncludePrerelease
    是否允许返回预发布版本。
#>
param(
    [Parameter(Mandatory = $true)][string]$PackageId,
    [string]$Source = 'https://api.nuget.org/v3/index.json',
    [switch]$IncludePrerelease
)

$ErrorActionPreference = 'Stop'

function Get-PackageBaseAddress {
    param([string]$ServiceIndex)

    $index = Invoke-RestMethod -Uri $ServiceIndex
    $resource = $index.resources | Where-Object { $_.'@type' -like 'PackageBaseAddress*' } | Select-Object -First 1
    if (-not $resource) {
        throw '未找到 PackageBaseAddress 资源。'
    }

    return $resource.'@id'
}

function Get-LatestVersion {
    param(
        [string[]]$Versions,
        [bool]$AllowPrerelease
    )

    $filtered = if ($AllowPrerelease) {
        $Versions
    } else {
        $Versions | Where-Object { $_ -notmatch '-' }
    }

    if (-not $filtered -or $filtered.Count -eq 0) {
        throw '没有符合条件的版本。'
    }

    return $filtered |
        Sort-Object 
            @{ Expression = { [version]($_ -replace '-.*$', '') } },
            @{ Expression = { $_ } } |
        Select-Object -Last 1
}

$normalizedPackageId = $PackageId.ToLowerInvariant()
$baseAddress = Get-PackageBaseAddress -ServiceIndex $Source
$indexUrl = "$baseAddress$normalizedPackageId/index.json"
$packageIndex = Invoke-RestMethod -Uri $indexUrl
$latestVersion = Get-LatestVersion -Versions $packageIndex.versions -AllowPrerelease:$IncludePrerelease.IsPresent

[pscustomobject]@{
    PackageId = $PackageId
    Source = $Source
    LatestVersion = $latestVersion
    IncludePrerelease = $IncludePrerelease.IsPresent
}