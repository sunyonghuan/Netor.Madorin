# Process 插件开发指南

## 一、创建项目

```powershell
dotnet new console -n Your.Process.Plugin
```

项目应引用 `Netor.Cortana.Plugin.Process`。这个包会自动带上 Generator 和 buildTransitive 目标，不需要再单独引用 Generator 包。

## 二、入口

```csharp
using Microsoft.Extensions.DependencyInjection;
using Netor.Cortana.Plugin;

namespace Your.Process.Plugin;

[Plugin(
    Id = "your_process_plugin",
    Name = "Your Process Plugin",
    Version = "1.0.0",
    Description = "提供示例 Process 工具。",
    Tags = ["sample"],
    Capabilities = ["sample.process"])]
public static partial class Startup
{
    public static void Configure(IServiceCollection services)
    {
        services.AddSingleton<SampleService>();
    }
}
```

`Program.cs` 默认只保留：

```csharp
using Your.Process.Plugin;

await Startup.RunPluginAsync().ConfigureAwait(false);
return 0;
```

## 三、工具和配置

工具和 `PluginSettings` 的写法与 Native 一致。Process 插件也可以申请 `host.llm.invoke.v1`，然后通过宿主 PluginBus 的 `model` topic 调用授权大模型。

## 四、资源

模型、模板、配置文件放到项目目录，并在 csproj 中设置复制到输出和发布目录。

## 五、自测

Process 插件必须在项目内部提供 `--self-test` 分支，使用生成的 `{PluginClass}Debugger` 完成：

1. `InitAsync()`
2. 每个 `[Tool]` 至少调用一次
3. 有边界条件的工具补失败或边界用例

## 六、发布

```powershell
dotnet build Your.Process.Plugin.csproj
dotnet publish Your.Process.Plugin.csproj -c Release -o publish/Your.Process.Plugin
```
