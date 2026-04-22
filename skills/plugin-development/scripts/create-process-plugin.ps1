<#
.SYNOPSIS
    创建基于 Netor.Cortana.Plugin.Process 框架的 C# Process 插件脚手架。
.PARAMETER Name
    项目名称（PascalCase），如 MyPlugin。
.PARAMETER Id
    插件 ID（小写字母+数字+下划线），如 my_plugin。
.PARAMETER Description
    插件描述。
#>
param(
    [Parameter(Mandatory = $true)][string]$Name,
    [Parameter(Mandatory = $true)][string]$Id,
    [string]$Description = "插件描述"
)

$ErrorActionPreference = 'Stop'
. $PSScriptRoot\PluginDev.Common.ps1

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

Write-Host "=== 创建 Process 插件: $Name ===" -ForegroundColor Cyan

New-Item -ItemType Directory -Path $ProjectDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $ProjectDir 'Application') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $ProjectDir 'Contracts') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $ProjectDir 'Tools') -Force | Out-Null

$csproj = @"
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging" Version="10.0.5" />
    <PackageReference Include="Netor.Cortana.Plugin.Process" Version="$PackageVersion" />
  </ItemGroup>

</Project>
"@
Set-Content -Path (Join-Path $ProjectDir "$Name.csproj") -Value $csproj -Encoding UTF8

$program = @"
using $Name;

await Startup.RunPluginAsync();
"@
Set-Content -Path (Join-Path $ProjectDir 'Program.cs') -Value $program -Encoding UTF8

$startup = @"
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin;

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
Set-Content -Path (Join-Path $ProjectDir 'Startup.cs') -Value $startup -Encoding UTF8

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

$contract = @"
namespace $Name;

public sealed record HelloResult(string Message, DateTimeOffset GeneratedAt);
"@
Set-Content -Path (Join-Path $ProjectDir 'Contracts\HelloResult.cs') -Value $contract -Encoding UTF8

$jsonContext = @"
using System.Text.Json.Serialization;

namespace $Name;

[JsonSerializable(typeof(HelloResult))]
internal partial class PluginJsonContext : JsonSerializerContext;
"@
Set-Content -Path (Join-Path $ProjectDir 'PluginJsonContext.cs') -Value $jsonContext -Encoding UTF8

$tools = @"
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin;

namespace $Name;

[Tool]
public sealed class ${Name}Tools(${Name}GreetingService greetingService, ILogger<${Name}Tools> logger)
{
    [Tool(Description = "返回问候语")]
    public HelloResult Hello([Parameter(Description = "输入名称")] string name)
    {
        logger.LogInformation("执行 hello 工具，输入长度：{Length}", name?.Length ?? 0);
        return greetingService.CreateGreeting(name);
    }
}
"@
Set-Content -Path (Join-Path $ProjectDir "Tools\${Name}Tools.cs") -Value $tools -Encoding UTF8

$readme = @"
# $Name

这是基于 Netor.Cortana.Plugin.Process 的 C# Process 插件脚手架。

## 开发步骤

1. 在 Tools/ 中继续添加 [Tool] 方法。
2. 返回自定义对象时，把类型补到 PluginJsonContext。
3. 执行 dotnet build，检查 obj/generated 和 bin/plugin.json。

## 调试

构建后会自动生成 StartupDebugger，可直接这样调用：

```csharp
await using var debugger = StartupDebugger.Create();
await debugger.InitAsync();
var result = await debugger.HelloAsync("Copilot");
```
"@
Set-Content -Path (Join-Path $ProjectDir 'README.md') -Value $readme -Encoding UTF8

Write-Host "✅ 项目已创建: $ProjectDir" -ForegroundColor Green
Write-Host ""
Write-Host "后续步骤:" -ForegroundColor Cyan
Write-Host "  1. cd $ProjectDir" -ForegroundColor Gray
Write-Host "  2. dotnet build          # 验证 Generator 输出与 plugin.json" -ForegroundColor Gray
Write-Host "  3. 查看 obj\...\generated 中的 Program.g.cs 和 StartupDebugger.g.cs" -ForegroundColor Gray
Write-Host "  4. publish-process-plugin.ps1 -ProjectDir Samples\$Name" -ForegroundColor Gray