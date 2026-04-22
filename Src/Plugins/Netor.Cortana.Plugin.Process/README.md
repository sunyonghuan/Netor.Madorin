# Netor.Cortana.Plugin.Process

Cortana Process 通道插件开发框架的运行时库，负责宿主通信、消息循环、工具调用和运行时配置注入。

## 功能

- `ProcessPluginHost` - 承载基于 stdin/stdout 的消息循环，处理 get_info、init、invoke、destroy 请求
- `PluginDebugger` - 提供强类型调试入口，便于本地调试和自动化测试
- `PluginSettingsAccessor` - 管理宿主下发的初始化配置并暴露运行时设置
- `ToolInvoker` - 统一工具调用委托签名，供 Generator 生成的路由字典使用
- `FileLogger` - 提供插件侧文件日志能力，便于定位运行时问题

## 安装

```shell
dotnet add package Netor.Cortana.Plugin.Process
```

## 快速开始

Process 通道插件通常由业务代码声明 `[Plugin]` / `[Tool]`，再由 `Netor.Cortana.Plugin.Process.Generator` 自动生成入口代码。

`Netor.Cortana.Plugin.Process` 本身已经内置 `[Plugin]`、`[Tool]`、`[Parameter]` 和 `PluginSettings`，
消费方无需再额外引用旧的 Abstractions 包；正常情况下只引用 `Netor.Cortana.Plugin.Process` 一个包即可，Generator 会随包自动带上。

### 1. 声明插件入口

```csharp
using Microsoft.Extensions.DependencyInjection;
using Netor.Cortana.Plugin;

[Plugin(Id = "sample_process", Name = "示例 Process 插件", Version = "1.0.0")]
public static partial class Startup
{
	public static void Configure(IServiceCollection services)
	{
		// 注册自定义服务
	}
}
```

### 2. 声明工具类

```csharp
using Netor.Cortana.Plugin;
[Tool]
public sealed class EchoTools
{
	[Tool(Description = "回显输入内容")]
	public string Echo([Parameter(Description = "输入文本")] string message)
		=> message;
}
```

### 3. 使用运行时配置

```csharp
using Netor.Cortana.Plugin;

public sealed class WorkspaceTools(PluginSettings settings)
{
	[Tool(Description = "获取当前工作目录")]
	public string GetWorkspaceDirectory() => settings.WorkspaceDirectory;
}
```

### 4. 启动入口

```csharp
using SampleProcessPlugin;

await Startup.RunPluginAsync();
```

## 运行机制

- 宿主通过 stdin/stdout 与插件进程通信
- `ProcessPluginHost.RunAsync` 负责请求反序列化、方法分派、异常兜底和响应写回
- `init` 阶段将宿主配置写入 `PluginSettingsAccessor`，之后工具类可通过 `PluginSettings` 读取
- 每次 `invoke` 调用都会在作用域内解析工具实例，兼容 DI 和 AOT 场景

## 主要命名空间

- `Netor.Cortana.Plugin.Process.Hosting` - 进程宿主与工具调用基础设施
- `Netor.Cortana.Plugin.Process.Debugging` - 调试器与调试参数模型
- `Netor.Cortana.Plugin.Process.Protocol` - 宿主请求、响应和插件元数据协议模型
- `Netor.Cortana.Plugin.Process.Settings` - 初始化配置和运行时设置访问器
- `Netor.Cortana.Plugin.Process.Logging` - 文件日志实现

## 依赖关系

- 通常与 `Netor.Cortana.Plugin.Process.Generator` 配合使用，由 Generator 自动生成入口代码
- 基于 `Microsoft.Extensions.DependencyInjection` 与 `Microsoft.Extensions.Logging` 构建运行时能力

## 要求

- .NET 10+
- 推荐与 `Netor.Cortana.Plugin.Process.Generator` 一起使用
- 若目标是 Native AOT，请保持工具方法和依赖注册采用 AOT 友好写法