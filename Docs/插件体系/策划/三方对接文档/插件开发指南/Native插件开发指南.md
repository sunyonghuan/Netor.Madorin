# Native 插件开发指南

## 一、创建项目

```powershell
dotnet new classlib -n Your.Native.Plugin
```

项目应引用 `Netor.Cortana.Plugin.Native`。正常消费场景不需要单独引用 Generator 包，`Netor.Cortana.Plugin.Native` 已经带上对应 analyzer 和 buildTransitive 目标。

## 二、入口

```csharp
using Microsoft.Extensions.DependencyInjection;
using Netor.Cortana.Plugin.Native;

namespace Your.Native.Plugin;

[Plugin(
    Id = "your_native_plugin",
    Name = "Your Native Plugin",
    Version = "1.0.0",
    Description = "提供示例 Native 工具。",
    Tags = ["sample"],
    Capabilities = ["sample.native"])]
public static partial class Startup
{
    public static void Configure(IServiceCollection services)
    {
        services.AddSingleton<SampleService>();
    }
}
```

## 三、工具

```csharp
[Tool]
public sealed class SampleTools(SampleService service)
{
    [Tool(Name = "sample_echo", Description = "回显输入文本。")]
    public string Echo(
        [Parameter(Name = "text", Description = "要回显的文本。")] string text)
        => service.Echo(text);
}
```

## 四、运行时配置

```csharp
public sealed class RuntimeTools(PluginSettings settings)
{
    [Tool(Description = "获取运行上下文。")]
    public string GetPaths()
        => $"data={settings.DataDirectory}; workspace={settings.WorkspaceDirectory}; plugin={settings.PluginDirectory}; ws={settings.WsPort}";
}
```

## 五、宿主能力申请

如果 Native 插件要调用宿主授权的大模型，使用：

```csharp
[RequiredHostCapability(
    Id = "host.llm.invoke.v1",
    Purpose = "summary.generate",
    Required = true,
    Reason = "需要调用宿主授权的大模型生成摘要。")]
```

## 六、发布和调试

```powershell
dotnet build Your.Native.Plugin.csproj
dotnet publish Your.Native.Plugin.csproj -c Release -o publish/Your.Native.Plugin
```

调试时使用：

```powershell
dotnet run --project Src/Plugins/Netor.Cortana.Plugin.Native.Debuger/Netor.Cortana.Plugin.Native.Debugger.csproj -- publish/Your.Native.Plugin/Your.Native.Plugin.dll
```
