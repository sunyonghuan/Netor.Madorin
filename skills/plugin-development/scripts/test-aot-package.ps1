<#
.SYNOPSIS
    使用临时项目验证外部 NuGet 包是否能通过 Native AOT 发布。
.DESCRIPTION
    1. 解析包版本（未指定时取最新稳定版）
    2. 生成最小临时 AOT 项目
    3. 执行 dotnet publish
    4. 输出是否通过与日志路径
.PARAMETER PackageId
    包 ID。
.PARAMETER Version
    包版本；为空时自动查询最新稳定版。
.PARAMETER TargetFramework
    目标框架，默认 net10.0。
.PARAMETER RuntimeIdentifier
    RID，默认 win-x64。
.PARAMETER Source
    NuGet V3 service index。
.PARAMETER KeepTemp
    保留临时目录用于排查。
#>
param(
    [Parameter(Mandatory = $true)][string]$PackageId,
    [string]$Version,
    [string]$TargetFramework = 'net10.0',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$Source = 'https://api.nuget.org/v3/index.json',
    [switch]$KeepTemp
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Version)) {
    $resolver = Join-Path $PSScriptRoot 'resolve-package-version.ps1'
    $resolved = & $resolver -PackageId $PackageId -Source $Source
    $Version = $resolved.LatestVersion
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("aot-pkg-" + [System.Guid]::NewGuid().ToString('N'))
$publishLog = Join-Path $tempRoot 'publish.log'

New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

$csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>$TargetFramework</TargetFramework>
    <PublishAot>true</PublishAot>
    <RuntimeIdentifier>$RuntimeIdentifier</RuntimeIdentifier>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="$PackageId" Version="$Version" />
  </ItemGroup>
</Project>
"@

$program = @"
using System;

Console.WriteLine("AOT package probe for $PackageId $Version");
"@

Set-Content -Path (Join-Path $tempRoot 'Probe.csproj') -Value $csproj -Encoding UTF8
Set-Content -Path (Join-Path $tempRoot 'Program.cs') -Value $program -Encoding UTF8

Push-Location $tempRoot
try {
    $publishArgs = @(
        'publish',
        'Probe.csproj',
        '-c', 'Release',
        '-r', $RuntimeIdentifier,
        '--nologo',
        '-v', 'minimal'
    )

    if (-not [string]::IsNullOrWhiteSpace($Source)) {
        $publishArgs += @('--source', $Source)
    }

    $output = & dotnet @publishArgs 2>&1
    $output | Out-File -FilePath $publishLog -Encoding utf8
    $success = $LASTEXITCODE -eq 0

    [pscustomobject]@{
        PackageId = $PackageId
        Version = $Version
        TargetFramework = $TargetFramework
        RuntimeIdentifier = $RuntimeIdentifier
        Source = $Source
        Success = $success
        LogPath = $publishLog
        TempPath = $tempRoot
    }
}
finally {
    Pop-Location
    if (-not $KeepTemp -and (Test-Path $tempRoot)) {
        Remove-Item $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}