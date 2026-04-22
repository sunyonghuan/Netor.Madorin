# C# Process 插件开发指南

## 当前推荐路径

对于 C# Process 插件，直接使用 `Netor.Cortana.Plugin.Process` 框架。

不要再手写这些胶水代码：

- stdin/stdout 消息循环
- `get_info / init / invoke / destroy` 分派
- `plugin.json` 输出
- 调试阶段的手工 JSON 拼接

框架会在编译时自动生成：

- `Program.g.cs`：标准消息循环入口
- `{PluginClass}Debugger.g.cs`：强类型调试器
- `plugin.json`：输出到 build/publish 目录

## 一、脚手架命令

```powershell
.\skills\plugin-development\scripts\create-process-plugin.ps1 -Name MyPlugin -Id my_plugin
```

生成结构：

```text
Samples/MyPlugin/
├── MyPlugin.csproj
├── Program.cs
├── Startup.cs
├── PluginJsonContext.cs
├── Application/
│   └── MyPluginGreetingService.cs
├── Contracts/
│   └── HelloResult.cs
└── Tools/
    └── MyPluginTools.cs
```

## 二、框架包引用

### 常规开发：引用 NuGet 包

只需要引用一个包：

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.Extensions.Logging" Version="10.0.5" />
  <PackageReference Include="Netor.Cortana.Plugin.Process" Version="1.0.16" />
</ItemGroup>
```

`Netor.Cortana.Plugin.Process` 包内部已经带上 Generator 和 buildTransitive 目标。
因此不需要再额外引用 `Netor.Cortana.Plugin.Process.Generator`，也不需要手写 `plugin.json`。

### 在当前仓库内联调：引用本地项目

如果你就在本仓库里开发，可改成：

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.Extensions.Logging" Version="10.0.5" />
  <ProjectReference Include="..\..\Src\Plugins\Netor.Cortana.Plugin.Process\Netor.Cortana.Plugin.Process.csproj" />
</ItemGroup>
```

## 三、最小可编译模板

### Program.cs

```csharp
using MyPlugin;

await Startup.RunPluginAsync();
```

### Startup.cs

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin;

namespace MyPlugin;

[Plugin(
    Id = "my_plugin",
    Name = "MyPlugin",
    Version = "1.0.0",
    Description = "插件描述")]
public static partial class Startup
{
    public static void Configure(IServiceCollection services)
    {
        services.AddLogging();
        services.AddSingleton<MyPluginGreetingService>();
    }
}
```

### Tools/MyPluginTools.cs

```csharp
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin;

namespace MyPlugin;

[Tool]
public sealed class MyPluginTools(MyPluginGreetingService greetingService, ILogger<MyPluginTools> logger)
{
    [Tool(Description = "返回问候语")]
    public HelloResult Hello([Parameter(Description = "输入名称")] string name)
    {
        logger.LogInformation("执行 hello 工具，输入长度：{Length}", name?.Length ?? 0);
        return greetingService.CreateGreeting(name);
    }
}
```

### Contracts/HelloResult.cs

```csharp
namespace MyPlugin;

public sealed record HelloResult(string Message, DateTimeOffset GeneratedAt);
```

### PluginJsonContext.cs

```csharp
using System.Text.Json.Serialization;

namespace MyPlugin;

[JsonSerializable(typeof(HelloResult))]
internal partial class PluginJsonContext : JsonSerializerContext;
```

## 四、为什么不需要手写协议层

框架消费方只写业务声明：

- `[Plugin]`：插件元数据
- `[Tool]`：工具类与工具方法
- `Configure(IServiceCollection)`：DI 注册

编译后会自动生成：

- `Startup.RunPluginAsync()`：标准 Process 消息循环入口
- `StartupDebugger.Create()`：强类型调试器入口
- `plugin.json`：位于 `bin/.../plugin.json` 和 publish 目录

这意味着 AI 在补代码时，应该把精力放在：

- 业务服务
- 工具参数与返回模型
- DI 注册
- 调试与发布命令

而不是重新实现宿主协议。

## 五、调试方式

### 1. 正常编译

```powershell
dotnet build
```

### 2. 使用生成的强类型 Debugger

构建后，Generator 会生成 `{PluginClass}Debugger`。
如果入口类叫 `Startup`，则调试器名就是 `StartupDebugger`。

示例：

```csharp
using MyPlugin;

await using var debugger = StartupDebugger.Create();
await debugger.InitAsync();

var resultJson = await debugger.HelloAsync("Copilot");
Console.WriteLine(resultJson);
```

优点：

- 不需要手写 `HostRequest` JSON
- 不需要启动真实子进程
- 覆盖的仍然是同一条消息循环代码路径

### 3. 查看生成物

构建后重点看：

- `obj/.../generated/.../Program.g.cs`
- `obj/.../generated/.../{PluginClass}Debugger.g.cs`
- `bin/.../plugin.json`

## 六、发布命令

### 默认：JIT self-contained

```powershell
.\skills\plugin-development\scripts\publish-process-plugin.ps1 -ProjectDir Samples\MyPlugin
```

### framework-dependent

```powershell
.\skills\plugin-development\scripts\publish-process-plugin.ps1 -ProjectDir Samples\MyPlugin -FrameworkDependent
```

### AOT exe

```powershell
.\skills\plugin-development\scripts\publish-process-plugin.ps1 -ProjectDir Samples\MyPlugin -Aot
```

## 七、AI 编码规则

- 不要手写 `ProcessPluginHost`、`HostRequest`、`HostResponse`、`ToolInvoker` 这类框架层代码。
- 不要手写 `plugin.json`。
- `Program.cs` 只保留 `await Startup.RunPluginAsync();`。
- 返回自定义对象时，先补 `PluginJsonContext`，再写工具实现。
- 调试优先用 `StartupDebugger`，不要自己拼 invoke 协议 JSON。
- 需要完整 JIT 生态时优先走默认 JIT 发布；确实要极致启动速度或单文件部署时再评估 `-Aot`。