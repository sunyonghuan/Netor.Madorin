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
. $PSScriptRoot\PluginDev.Common.ps1

# 验证 Id 格式
if ($Id -notmatch '^[a-z][a-z0-9_]*$') {
    Write-Host "❌ Id 格式错误：只允许小写字母、数字、下划线，且以字母开头" -ForegroundColor Red
    exit 1
}

$SolutionDir = Get-PluginDevSolutionDir -ScriptRoot $PSScriptRoot
$ProjectDir = Join-Path $SolutionDir "Samples\$Name"
$PackageVersion = Get-PluginDevRepoPackageVersion -SolutionDir $SolutionDir

if (Test-Path $ProjectDir) {
    Write-Host "❌ 目录已存在：$ProjectDir" -ForegroundColor Red
    exit 1
}

Write-Host "=== 创建 Native 插件: $Name ===" -ForegroundColor Cyan

# 1. 创建项目目录
New-Item -ItemType Directory -Path $ProjectDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $ProjectDir 'Application') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $ProjectDir 'Contracts') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $ProjectDir 'Tools') -Force | Out-Null

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
        <PackageReference Include="Microsoft.Extensions.Logging" Version="10.0.5" />
        <PackageReference Include="Netor.Cortana.Plugin.Native" Version="$PackageVersion" />
        <PackageReference Include="Netor.Cortana.Plugin.Native.Generator" Version="$PackageVersion"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false" />
  </ItemGroup>

</Project>
"@
Set-Content -Path (Join-Path $ProjectDir "$Name.csproj") -Value $csproj -Encoding UTF8

# 3. 生成 Startup.cs
$startup = @"
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        services.AddLogging();
        services.AddSingleton<${Name}GreetingService>();
    }
}
"@
Set-Content -Path (Join-Path $ProjectDir "Startup.cs") -Value $startup -Encoding UTF8

# 4. 生成应用服务
$service = @"
namespace $Name;

public sealed class ${Name}GreetingService
{
    public HelloResult CreateGreeting(string name)
    {
        var normalizedName = string.IsNullOrWhiteSpace(name) ? "匿名用户" : name.Trim();
        return new HelloResult($"你好, {normalizedName}!", DateTimeOffset.Now);
    }
}
"@
Set-Content -Path (Join-Path $ProjectDir "Application\${Name}GreetingService.cs") -Value $service -Encoding UTF8

# 5. 生成契约模型
$contract = @"
namespace $Name;

public sealed record HelloResult(string Message, DateTimeOffset GeneratedAt);
"@
Set-Content -Path (Join-Path $ProjectDir 'Contracts\HelloResult.cs') -Value $contract -Encoding UTF8

# 6. 生成 JSON 上下文
$jsonContext = @"
using System.Text.Json.Serialization;

namespace $Name;

[JsonSerializable(typeof(HelloResult))]
internal partial class PluginJsonContext : JsonSerializerContext;
"@
Set-Content -Path (Join-Path $ProjectDir 'PluginJsonContext.cs') -Value $jsonContext -Encoding UTF8

# 7. 生成示例工具类
$tools = @"
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin.Native;

namespace $Name;

[Tool]
public class ${Name}Tools(${Name}GreetingService greetingService, ILogger<${Name}Tools> logger)
{
    [Tool(Name = "hello", Description = "返回问候语")]
    public HelloResult Hello(
        [Parameter(Description = "你的名字")] string name)
    {
        logger.LogInformation("执行 hello 工具，输入长度：{Length}", name?.Length ?? 0);
        return greetingService.CreateGreeting(name);
    }
}
"@
Set-Content -Path (Join-Path $ProjectDir "Tools\${Name}Tools.cs") -Value $tools -Encoding UTF8

Write-Host "✅ 项目已创建: $ProjectDir" -ForegroundColor Green
Write-Host ""
Write-Host "后续步骤:" -ForegroundColor Cyan
Write-Host "  1. cd $ProjectDir"
Write-Host "  2. 如需外部包，先运行 resolve-package-version.ps1 + test-aot-package.ps1"
Write-Host "  3. 按 Application/Contracts/Tools 继续扩展"
Write-Host "  4. dotnet build          # 验证编译"
Write-Host "  5. publish-native-plugin.ps1 -ProjectDir Samples\$Name -CreateZip"
