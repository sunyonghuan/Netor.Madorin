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
. $PSScriptRoot\PluginDev.Common.ps1
$SolutionDir = Get-PluginDevSolutionDir -ScriptRoot $PSScriptRoot
$ProjectDir = Join-Path $SolutionDir "Samples\$Name"
$AbstractionsPath = "..\..\Src\Plugins\Netor.Cortana.Plugin.Abstractions\Netor.Cortana.Plugin.Abstractions.csproj"

if (Test-Path $ProjectDir) {
    Write-Host "❌ 目录已存在：$ProjectDir" -ForegroundColor Red
    exit 1
}

Write-Host "=== 创建 Dotnet 插件: $Name ===" -ForegroundColor Cyan

New-Item -ItemType Directory -Path $ProjectDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $ProjectDir 'Application') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $ProjectDir 'Composition') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $ProjectDir 'Tools') -Force | Out-Null

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
        <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.5" />
        <PackageReference Include="Microsoft.Extensions.Logging" Version="10.0.5" />
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

# 3. 组合根
$composition = @"
using Microsoft.Extensions.DependencyInjection;
using Netor.Cortana.Plugin.Abstractions;

namespace $Name;

internal static class PluginComposition
{
    public static ServiceProvider Build(IPluginContext context)
    {
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddSingleton(context.LoggerFactory);
        services.AddSingleton(context.HttpClientFactory);
        services.AddSingleton<${Name}GreetingService>();
        services.AddSingleton<${Name}Tools>();
        return services.BuildServiceProvider();
    }
}
"@
Set-Content -Path (Join-Path $ProjectDir 'Composition\PluginComposition.cs') -Value $composition -Encoding UTF8

# 4. 应用服务
$appService = @"
namespace $Name;

public sealed class ${Name}GreetingService
{
    public string CreateGreeting(string name)
    {
        var normalizedName = string.IsNullOrWhiteSpace(name) ? "匿名用户" : name.Trim();
        return $"你好, {normalizedName}!";
    }
}
"@
Set-Content -Path (Join-Path $ProjectDir 'Application\${Name}GreetingService.cs') -Value $appService -Encoding UTF8

# 5. 工具类
$toolClass = @"
using Microsoft.Extensions.Logging;

namespace $Name;

public sealed class ${Name}Tools(${Name}GreetingService greetingService, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<${Name}Tools>();

    public string Hello(string name)
    {
        _logger.LogInformation("执行 hello 工具，输入长度：{Length}", name?.Length ?? 0);
        return greetingService.CreateGreeting(name);
    }
}
"@
Set-Content -Path (Join-Path $ProjectDir 'Tools\${Name}Tools.cs') -Value $toolClass -Encoding UTF8

# 6. Plugin 类
$pluginClass = @"
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin.Abstractions;

namespace $Name;

public sealed class ${Name}Plugin : IPlugin
{
    private readonly List<AITool> _tools = [];
    private ILogger? _logger;
    private ServiceProvider? _serviceProvider;

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
        _serviceProvider = PluginComposition.Build(context);
        var tools = _serviceProvider.GetRequiredService<${Name}Tools>();

        _tools.Add(AIFunctionFactory.Create(
            tools.Hello, "hello", "返回问候语"));

        _logger.LogInformation("$Name 初始化完成");
        return Task.CompletedTask;
    }
}
"@
Set-Content -Path (Join-Path $ProjectDir "${Name}Plugin.cs") -Value $pluginClass -Encoding UTF8

Write-Host "✅ 项目已创建: $ProjectDir" -ForegroundColor Green
Write-Host ""
Write-Host "后续步骤:" -ForegroundColor Cyan
Write-Host "  1. cd $ProjectDir"
Write-Host "  2. 如需外部包，先运行 resolve-package-version.ps1"
Write-Host "  3. 按 Composition/Application/Tools 扩展"
Write-Host "  4. dotnet build          # 验证编译"
Write-Host "  5. publish-dotnet-plugin.ps1 -ProjectDir Samples\$Name -CreateZip"
