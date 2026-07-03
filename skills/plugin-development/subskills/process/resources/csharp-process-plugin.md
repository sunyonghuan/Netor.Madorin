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
scripts\create-process-plugin.ps1 -Name MyPlugin -Id my_plugin
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

if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
{
#if DEBUG
    return await SelfTest.RunAsync();
#else
    Console.Error.WriteLine("Self-test is only available in Debug builds.");
    return 2;
#endif
}

await Startup.RunPluginAsync();
return 0;
```

### SelfTest.cs

```csharp
namespace MyPlugin;

#if DEBUG
internal static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        await using var debugger = StartupDebugger.Create();
        await debugger.InitAsync();

        var failed = false;

        try
        {
            var result = await debugger.HelloAsync("Copilot");
            Console.WriteLine($"[PASS] HelloAsync => {result}");
        }
        catch (Exception ex)
        {
            failed = true;
            Console.Error.WriteLine($"[FAIL] HelloAsync => {ex.Message}");
        }

        return failed ? 1 : 0;
    }
}
#endif
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

## 五、测试与调试方式

### 1. 正常编译

```powershell
dotnet build
```

### 2. 使用插件项目内置自测入口

优先在插件项目自身内完成测试，不要额外创建 ps1、额外控制台工程或独立测试项目。

推荐命令：

```powershell
dotnet run -- --self-test
```

`Program.cs` 只负责做模式分流：

- 默认模式走 `Startup.RunPluginAsync()`
- `--self-test` 模式走 `SelfTest.RunAsync()`
- `SelfTest.cs` 必须放在 `#if DEBUG` 里，避免进入 Release / AOT 发布路径

### 3. 在 SelfTest.cs 里使用生成的强类型 Debugger

构建后，Generator 会生成 `{PluginClass}Debugger`。
如果入口类叫 `Startup`，则调试器名就是 `StartupDebugger`。

示例：

```csharp
using MyPlugin;

await using var debugger = StartupDebugger.Create();
await debugger.InitAsync();

var result = await debugger.HelloAsync("Copilot");
Console.WriteLine(result);
```

这一步不是“可选演示”，而是 `SelfTest.RunAsync()` 里的最低测试入口。
如果插件里声明了多个 `[Tool]` 方法，要用同样方式把每个工具都调一遍，不能只验证一个示例方法。

优点：

- 不需要手写 `HostRequest` JSON
- 不需要启动真实子进程
- 覆盖的仍然是同一条消息循环代码路径

### 4. 最低测试要求

发布前至少完成以下检查：

1. `dotnet build` 成功。
2. `dotnet run -- --self-test` 成功完成。
3. `StartupDebugger` 可以成功 `InitAsync()`。
4. 每一个 `[Tool]` 方法至少执行一条成功路径。
5. 有输入约束的工具，再补至少一条边界值或失败路径。
6. 返回自定义对象的工具，检查返回值或 JSON 是否符合预期，必要时反序列化验证字段。
7. 任一工具测试失败时，进程返回非 0 退出码。

如果只验证插件能启动、却没有逐个调用工具方法，测试仍然算不完整。

### 5. 查看生成物

构建后重点看：

- `obj/.../generated/.../Program.g.cs`
- `obj/.../generated/.../{PluginClass}Debugger.g.cs`
- `bin/.../plugin.json`

## 六、发布命令

只有在上面的逐工具测试完成后，才进入发布步骤。

### 默认：JIT self-contained

```powershell
scripts\publish-process-plugin.ps1 -ProjectDir Samples\MyPlugin
```

### framework-dependent

```powershell
scripts\publish-process-plugin.ps1 -ProjectDir Samples\MyPlugin -FrameworkDependent
```

### AOT exe

```powershell
scripts\publish-process-plugin.ps1 -ProjectDir Samples\MyPlugin -Aot
```

## 七、AI 编码规则

- 不要手写 `ProcessPluginHost`、`HostRequest`、`HostResponse`、`ToolInvoker` 这类框架层代码。
- 不要手写 `plugin.json`。
- `Program.cs` 是唯一入口文件；默认走 `await Startup.RunPluginAsync();`，测试时仅增加 `--self-test` 分支。
- 优先在插件项目自身内补 `SelfTest.cs`；除非用户明确要求，否则不要创建外部脚本、额外控制台工程或独立测试工程。
- `SelfTest.cs` 必须放在 `#if DEBUG` 条件编译里，避免污染 Release / AOT 发布路径。
- 返回自定义对象时，先补 `PluginJsonContext`，再写工具实现。
- 调试优先用 `StartupDebugger`，不要自己拼 invoke 协议 JSON。
- 测试优先执行 `dotnet run -- --self-test`，并用退出码表达通过/失败。
- 需要完整 JIT 生态时优先走默认 JIT 发布；确实要极致启动速度或单文件部署时再评估 `-Aot`。
