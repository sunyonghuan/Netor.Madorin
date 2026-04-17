<#
.SYNOPSIS
    创建 Native AOT 插件项目脚手架。
.PARAMETER Name
    项目名称（PascalCase），如 MyPlugin。
.PARAMETER Id
    插件 ID（小写字母+数字+下划线），如 my_plugin。
.PARAMETER Description
    插件描述。
#>
param(
    [Parameter(Mandatory=$true)][string]$Name,
    [Parameter(Mandatory=$true)][string]$Id,
    [string]$Description = "插件描述"
)

$ErrorActionPreference = 'Stop'

# 验证 Id 格式
if ($Id -notmatch '^[a-z][a-z0-9_]*$') {
    Write-Host "❌ Id 格式错误：只允许小写字母、数字、下划线，且以字母开头" -ForegroundColor Red
    exit 1
}

$SolutionDir = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$ProjectDir = Join-Path $SolutionDir "Samples\$Name"

if (Test-Path $ProjectDir) {
    Write-Host "❌ 目录已存在：$ProjectDir" -ForegroundColor Red
    exit 1
}

Write-Host "=== 创建 Native 插件: $Name ===" -ForegroundColor Cyan

# 1. 创建项目目录
New-Item -ItemType Directory -Path $ProjectDir -Force | Out-Null

# 2. 生成 csproj
$csproj = @"
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <PublishAot>true</PublishAot>
    <OutputType>Library</OutputType>
    <AssemblyName>$Name</AssemblyName>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Netor.Cortana.Plugin.Native" Version="1.0.3" />
    <PackageReference Include="Netor.Cortana.Plugin.Native.Generator" Version="1.0.3"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false" />
  </ItemGroup>

</Project>
"@
Set-Content -Path (Join-Path $ProjectDir "$Name.csproj") -Value $csproj -Encoding UTF8

# 3. 生成 Startup.cs
$startup = @"
using Microsoft.Extensions.DependencyInjection;
using Netor.Cortana.Plugin.Native;

namespace $Name;

[Plugin(
    Id = "$Id",
    Name = "$Name",
    Version = "1.0.0",
    Description = "$Description")]
public static partial class Startup
{
    public static void Configure(IServiceCollection services)
    {
    }
}
"@
Set-Content -Path (Join-Path $ProjectDir "Startup.cs") -Value $startup -Encoding UTF8

# 4. 生成示例工具类
$tools = @"
using Netor.Cortana.Plugin.Native;

namespace $Name;

[Tool]
public class ${Name}Tools
{
    [Tool(Name = "hello", Description = "返回问候语")]
    public string Hello(
        [Parameter(Description = "你的名字")] string name)
    {
        return `$"你好, {name}!";
    }
}
"@
Set-Content -Path (Join-Path $ProjectDir "${Name}Tools.cs") -Value $tools -Encoding UTF8

Write-Host "✅ 项目已创建: $ProjectDir" -ForegroundColor Green
Write-Host ""
Write-Host "后续步骤:" -ForegroundColor Cyan
Write-Host "  1. cd $ProjectDir"
Write-Host "  2. dotnet build          # 验证编译"
Write-Host "  3. dotnet publish -c Release  # AOT 发布"
