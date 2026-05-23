---
name: process
description: 'Madorin Process 插件开发子技能。位置：subskills/process。用于开发以独立 exe 进程运行的插件，支持 JIT self-contained 和 AOT exe 两种发布方式，通过 stdin/stdout JSON 协议与宿主通信。触发关键词：Process 插件、进程插件、exe 插件、JIT 插件、进程隔离插件。'
version: 3
user-invocable: true
---

# Plugin Development Process

Process 通道启动一个独立的 exe 子进程作为插件。宿主通过 stdin/stdout 单行 JSON 协议通信。
插件无需 AOT 发布，可以使用完整 .NET JIT 生态（反射、Roslyn、动态编译等）。
对于 C# Process 插件，不要手写协议层，直接使用 `Netor.Cortana.Plugin.Process` 框架和 `create-process-plugin.ps1` 脚手架。
框架会在编译时自动生成消息循环、`plugin.json` 和强类型 Debugger。

## Flow

1. 运行 `scripts/create-process-plugin.ps1 -Name MyPlugin -Id my_plugin` 生成 C# Process 插件工程。
2. 在 `Startup.cs` 中声明 `[Plugin]` 入口并注册依赖，在 `Tools/` 中编写 `[Tool]` 类和方法。
3. `Program.cs` 作为唯一入口：默认执行 `await Startup.RunPluginAsync();`，仅允许增加一个 Debug 条件编译的 `--self-test` 分支用于本地自测；不要手写 stdin/stdout 循环，也不要额外新建脚本或测试工程。
4. 如果工具返回自定义对象，为该类型声明 `PluginJsonContext`。
5. 先执行 `dotnet build`，确认 `obj/.../generated/.../Program.g.cs`、`{PluginClass}Debugger.g.cs` 和 `bin/.../plugin.json` 都已生成。
6. 在插件项目自身内执行 `dotnet run -- --self-test`，由内置自测入口使用生成的 `{PluginClass}Debugger` 调用 `InitAsync()`，然后对每一个 `[Tool]` 方法逐个执行本地测试。
7. 每个工具至少覆盖一条成功路径；如果工具有边界条件或失败分支，再补至少一条边界/失败用例，并检查参数绑定、返回值和序列化结果是否符合预期。
8. 自测必须在失败时返回非 0 退出码；未完成逐工具测试前，不允许直接发布或安装。
9. 所有工具都通过本地测试后，才使用 `publish-process-plugin.ps1` 发布为 JIT self-contained、framework-dependent 或 AOT exe。
10. 发布完成后，如需安装到运行时环境，再切到 publish-install 子技能。

## Rules

- C# Process 插件默认引用 `Netor.Cortana.Plugin.Process` 包；该包会自动带上 Generator，不需要再单独手写协议代码。
- 入口类必须是 `public static partial class`，带 `[Plugin]` 特性，并提供 `Configure(IServiceCollection services)`。
- `Program.cs` 是插件唯一入口文件；默认走 `RunPluginAsync()`，测试时只允许增加一个 Debug 条件编译的 `--self-test` 分支。
- 自测代码必须放在插件项目自身内，优先使用 `Program.cs` + `SelfTest.cs` 模板；除非用户明确要求，否则不要创建外部脚本、额外控制台工程或独立测试工程。
- `SelfTest.cs` 必须放在 `#if DEBUG` 条件编译内，避免把测试入口带入 Release / AOT 发布路径。
- `plugin.json` 由 Generator 自动输出到 build/publish 目录，不手写、不维护副本。
- 工具参数仅使用框架当前支持的标量类型：`string`、`int`、`long`、`double`、`float`、`decimal`、`bool`。
- 工具返回自定义对象或集合时，必须给这些返回类型提供 `PluginJsonContext` 条目。
- 调试优先使用 Generator 生成的 `{PluginClass}Debugger`，不要手工拼 JSON 调试 invoke 请求。
- 发布前必须完成本地测试，不能“写完就安装”。
- 每一个 `[Tool]` 方法都必须至少执行一次本地调用验证；只测插件启动、不测工具调用，视为测试不完整。
- 工具有输入约束时，除成功路径外，至少再测一条边界值或失败路径。
- 自测入口应通过 `dotnet run -- --self-test` 执行；有任一工具测试失败时，进程必须返回非 0 退出码。
- stderr 输出会被宿主收集并写入日志，可用于调试；stdout 只允许协议响应。

## Test

最低测试流程如下：

1. 运行 `dotnet build`，确保 Generator 产物和 `plugin.json` 已生成。
2. 编写一个本地调试入口，使用 `StartupDebugger.Create()` 创建调试器。
3. 先执行 `await debugger.InitAsync();`，再按工具方法逐个调用。
4. 对每个工具至少验证：
	- 能成功调用
	- 参数绑定正确
	- 返回值/JSON 序列化正确
5. 对有约束的工具，再补至少一条边界或失败用例。
6. 全部工具测试通过后，才能进入 publish / install。

推荐最小测试模板：

```csharp
await using var debugger = StartupDebugger.Create();
await debugger.InitAsync();

var result = await debugger.HelloAsync("Copilot");
Console.WriteLine(result);
```

如果插件里有多个工具方法，就按同样方式把每个方法都调用一遍，不要只测第一个示例工具。

## Protocol

协议详细定义和 C# 框架用法见：

- resources/csharp-process-plugin.md

## Publish

两种发布方式均支持，无需选边：

| 方式 | 命令 | 特点 |
|------|------|------|
| JIT self-contained | `dotnet publish -r win-x64 --self-contained` | 包含 Runtime，无外部依赖，~60-100 MB |
| AOT exe | `dotnet publish -r win-x64 /p:PublishAot=true` | 启动更快，体积更小，但有 AOT 约束 |

框架发布脚本：

- `scripts/publish-process-plugin.ps1 -ProjectDir Samples\MyPlugin`
2. 在插件项目自身内保留一个 `Program.cs` 自测分支，并把具体测试逻辑放到 `SelfTest.cs`。
3. 运行 `dotnet run -- --self-test`，在自测分支里使用 `StartupDebugger.Create()` 创建调试器。
4. 先执行 `await debugger.InitAsync();`，再按工具方法逐个调用。
5. 对每个工具至少验证：
	- 能成功调用
	- 参数绑定正确
	- 返回值/JSON 序列化正确
6. 对有约束的工具，再补至少一条边界或失败用例。
7. 任一测试失败时返回非 0 退出码，全部通过后才能进入 publish / install。

推荐最小模板：

```csharp
// Program.cs
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

```csharp
// SelfTest.cs
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

如果插件里有多个工具方法，就在 `SelfTest.RunAsync()` 里按同样方式把每个方法都调用一遍，不要只测第一个示例工具。
