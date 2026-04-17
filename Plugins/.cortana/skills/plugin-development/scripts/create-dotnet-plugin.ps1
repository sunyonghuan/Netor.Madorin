<#
.SYNOPSIS
    创建 Dotnet 托管插件项目脚手架。
.PARAMETER Name
    项目名称（PascalCase），如 MyPlugin。
.PARAMETER Id
    插件 ID（reverse-domain 格式），如 com.example.my-plugin。
.PARAMETER Description
    插件描述。
#>
param(
    [Parameter(Mandatory=$true)][string]$Name,
    [Parameter(Mandatory=$true)][string]$Id,
    [string]$Description = "插件描述"
)

$ErrorActionPreference = 'Stop'
$SolutionDir = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$ProjectDir = Join-Path $SolutionDir "Samples\$Name"
$AbstractionsPath = "..\..\Src\Plugins\Netor.Cortana.Plugin.Abstractions\Netor.Cortana.Plugin.Abstractions.csproj"

if (Test-Path $ProjectDir) {
    Write-Host "❌ 目录已存在：$ProjectDir" -ForegroundColor Red
    exit 1
}

Write-Host "=== 创建 Dotnet 插件: $Name ===" -ForegroundColor Cyan

New-Item -ItemType Directory -Path $ProjectDir -Force | Out-Null

# 1. csproj
$csproj = @"
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="$AbstractionsPath" />
  </ItemGroup>

</Project>
"@
Set-Content -Path (Join-Path $ProjectDir "$Name.csproj") -Value $csproj -Encoding UTF8

# 2. plugin.json
$pluginJson = @"
{
  "id": "$Id",
  "name": "$Name",
  "version": "1.0.0",
  "description": "$Description",
  "runtime": "dotnet",
  "assemblyName": "$Name.dll",
  "minHostVersion": "1.0.0"
}
"@
Set-Content -Path (Join-Path $ProjectDir "plugin.json") -Value $pluginJson -Encoding UTF8

# 3. Plugin 类
$pluginClass = @"
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin.Abstractions;

namespace $Name;

public sealed class ${Name}Plugin : IPlugin
{
    private readonly List<AITool> _tools = [];
    private ILogger? _logger;

    public string Id => "$Id";
    public string Name => "$Name";
    public Version Version => new(1, 0, 0);
    public string Description => "$Description";
    public IReadOnlyList<string> Tags => [];
    public IReadOnlyList<AITool> Tools => _tools;
    public string? Instructions => null;

    public Task InitializeAsync(IPluginContext context)
    {
        _logger = context.LoggerFactory.CreateLogger<${Name}Plugin>();

        _tools.Add(AIFunctionFactory.Create(
            Hello, "hello", "返回问候语"));

        _logger.LogInformation("$Name 初始化完成");
        return Task.CompletedTask;
    }

    private string Hello(string name)
    {
        return `$"你好, {name}!";
    }
}
"@
Set-Content -Path (Join-Path $ProjectDir "${Name}Plugin.cs") -Value $pluginClass -Encoding UTF8

Write-Host "✅ 项目已创建: $ProjectDir" -ForegroundColor Green
Write-Host ""
Write-Host "后续步骤:" -ForegroundColor Cyan
Write-Host "  1. cd $ProjectDir"
Write-Host "  2. dotnet build          # 验证编译"
Write-Host "  3. dotnet publish -c Release  # 发布"
