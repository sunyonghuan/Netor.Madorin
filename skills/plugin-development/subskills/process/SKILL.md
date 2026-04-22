---
name: process
description: 'Cortana Process 插件开发子技能。位置：plugin-development/subskills/process。用于开发以独立 exe 进程运行的插件，支持 JIT self-contained 和 AOT exe 两种发布方式，通过 stdin/stdout JSON 协议与宿主通信。触发关键词：Process 插件、进程插件、exe 插件、JIT 插件、进程隔离插件。'
version: 1
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
3. `Program.cs` 只保留 `await Startup.RunPluginAsync();`，不要手写 stdin/stdout 循环。
4. 如果工具返回自定义对象，为该类型声明 `PluginJsonContext`。
5. `dotnet build` 验证后，使用 `publish-process-plugin.ps1` 发布为 JIT self-contained、framework-dependent 或 AOT exe。
6. 运行时安装切到 publish-install 子技能。

## Rules

- C# Process 插件默认引用 `Netor.Cortana.Plugin.Process` 包；该包会自动带上 Generator，不需要再单独手写协议代码。
- 入口类必须是 `public static partial class`，带 `[Plugin]` 特性，并提供 `Configure(IServiceCollection services)`。
- `Program.cs` 只负责调用 Generator 生成的 `RunPluginAsync()`；不要手写 `get_info/init/invoke/destroy`。
- `plugin.json` 由 Generator 自动输出到 build/publish 目录，不手写、不维护副本。
- 工具参数仅使用框架当前支持的标量类型：`string`、`int`、`long`、`double`、`float`、`decimal`、`bool`。
- 工具返回自定义对象或集合时，必须给这些返回类型提供 `PluginJsonContext` 条目。
- 调试优先使用 Generator 生成的 `{PluginClass}Debugger`，不要手工拼 JSON 调试 invoke 请求。
- stderr 输出会被宿主收集并写入日志，可用于调试；stdout 只允许协议响应。

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
- `scripts/publish-process-plugin.ps1 -ProjectDir Samples\MyPlugin -FrameworkDependent`
- `scripts/publish-process-plugin.ps1 -ProjectDir Samples\MyPlugin -Aot`

## Resources

- resources/csharp-process-plugin.md
