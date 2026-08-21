# Netor.Madorin.Plugin.Process

Madorin Process 通道插件开发框架的运行时库，负责宿主通信、消息循环、工具调用和运行时配置注入。

## 功能

- `ProcessPluginHost` - 承载基于 stdin/stdout 的消息循环，处理 get_info、init、invoke、destroy 请求
- `PluginDebugger` - 提供强类型调试入口，便于本地调试和自动化测试
- `PluginSettingsAccessor` - 管理宿主下发的初始化配置并暴露运行时设置
- `ToolInvoker` - 统一工具调用委托签名，供 Generator 生成的路由字典使用
- `FileLogger` - 提供插件侧文件日志能力，便于定位运行时问题
- `[RequiredHostCapability]` / `[PluginSetting]` - 声明宿主能力申请与设置界面 schema，由 Generator 输出到 `plugin.json` / `get_info`

## 安装

```shell
dotnet add package Netor.Madorin.Plugin.Process
```

## 快速开始

Process 通道插件通常由业务代码声明 `[Plugin]` / `[Tool]`，再由 `Netor.Madorin.Plugin.Process.Generator` 自动生成入口代码。

`Netor.Madorin.Plugin.Process` 本身已经内置 `[Plugin]`、`[Tool]`、`[Parameter]` 和 `PluginSettings`，
消费方无需再额外引用旧的 Abstractions 包；正常情况下只引用 `Netor.Madorin.Plugin.Process` 一个包即可，Generator 会随包自动带上。

### 1. 声明插件入口

```csharp
using Microsoft.Extensions.DependencyInjection;
using Netor.Madorin.Plugin;

[Plugin(Id = "sample_process", Name = "示例 Process 插件", Version = "1.0.0")]
[RequiredHostCapability(
    Id = "host.fs.workspace.read.v1",
    Purpose = "context.load",
    Required = true,
    Reason = "需要读取工作区中的文件。")]
[PluginSetting(
    Key = "api_key",
    Label = "API Key",
    Description = "第三方服务访问密钥",
    Type = PluginSettingType.Secret,
    Required = true,
    Sensitive = true,
    Scope = ["user"],
    RestartRequired = true)]
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
using Netor.Madorin.Plugin;
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
using Netor.Madorin.Plugin;

public sealed class WorkspaceTools(PluginSettings settings)
{
	[Tool(Description = "获取当前工作目录")]
	public string GetWorkspaceDirectory() => settings.WorkspaceDirectory;
}
```

### 4. 使用强类型调试器

```csharp
await using var debugger = StartupDebugger.Create();
await debugger.InitAsync();

var result = await debugger.EchoAsync("hello");
Console.WriteLine(result);
```

### 5. 启动入口

```csharp
using SampleProcessPlugin;

await Startup.RunPluginAsync();
```

## 运行机制

- 宿主通过 stdin/stdout 与插件进程通信
- `ProcessPluginHost.RunAsync` 负责请求反序列化、方法分派、异常兜底和响应写回
- `init` 阶段将宿主配置写入 `PluginSettingsAccessor`，之后工具类可通过 `PluginSettings` 读取
- 每次 `invoke` 调用都会在作用域内解析工具实例，兼容 DI 和 AOT 场景
- `get_info` / `plugin.json` 都会携带插件元数据，包括 `providedCapabilities`、`requiredHostCapabilities`、`settingsSchema`

## 主要命名空间

- `Netor.Madorin.Plugin.Process.Hosting` - 进程宿主与工具调用基础设施
- `Netor.Madorin.Plugin.Process.Debugging` - 调试器与调试参数模型
- `Netor.Madorin.Plugin.Process.Protocol` - 宿主请求、响应和插件元数据协议模型
- `Netor.Madorin.Plugin.Process.Settings` - 初始化配置和运行时设置访问器
- `Netor.Madorin.Plugin.Process.Logging` - 文件日志实现

## 依赖关系

- 通常只需引用 `Netor.Madorin.Plugin.Process`；该包会自动带上 Generator analyzer 与 `buildTransitive` targets
- 基于 `Microsoft.Extensions.DependencyInjection` 与 `Microsoft.Extensions.Logging` 构建运行时能力

## 元数据协议

通过入口类声明以下元数据：

- `[Plugin].Capabilities`：插件提供给宿主/模型层的能力声明，会输出为 `capabilities` 与 `providedCapabilities`
- `[RequiredHostCapability]`：插件向宿主申请的能力，会输出为 `requiredHostCapabilities`
- `[PluginSetting]`：宿主设置界面 schema，会输出为 `settingsSchema`

`PluginSettingType` 当前支持：

- `String`
- `Secret`
- `Number`
- `Boolean`
- `Enum`
- `Path`
- `Json`

`PluginSettings` 运行时可读取的关键字段包括：

- `DataDirectory`
- `WorkspaceDirectory`
- `PluginDirectory`
- `WsPort`
- `Extensions`
- `ChatWsEndpoint`
- `ConversationFeedEndpoint`
- `ConversationFeedProtocol`
- `ConversationFeedVersion`
- `ConversationFeedPort`

注意：

- `RequiredHostCapability` 是申请声明，不代表宿主已授权。
- 依赖 `PluginSettings` 的逻辑必须在 `init` 之后运行；调试时要先调用 `InitAsync()`。

## 要求

- .NET 10+
- 推荐直接引用 `Netor.Madorin.Plugin.Process`，无需再额外单独引用 Generator 包
- 若目标是 Native AOT，请保持工具方法和依赖注册采用 AOT 友好写法
