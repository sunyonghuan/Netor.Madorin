# Netor.Cortana.Plugin.Native.Debugger

> Netor Cortana 原生插件调试器模块 — 为插件开发者提供本地交互式调试能力

## 📖 简介

`Netor.Cortana.Plugin.Native.Debugger` 是一个 **类库**（非可执行程序），用于在开发阶段对 Cortana 原生插件进行本地交互式调试。

它通过一个控制台宿主模拟真实运行环境，让开发者可以在 **不启动主程序** 的情况下：

- 🔍 **自动发现插件** — 扫描当前 AppDomain 中已加载的程序集，识别 `[Plugin]` 标记的入口类
- 🛠️ **自动注册工具** — 扫描所有 `[Tool]` 标记的方法，构建工具注册表
- 💬 **交互式调试循环** — 在控制台输入工具名称和 JSON 参数，即时查看执行结果
- 🔌 **完整 DI 支持** — 自动构建服务容器，支持依赖注入、HTTP 客户端、日志等
- 📋 **上下文模拟** — 提供 `DebugPluginContext` 模拟真实宿主环境（数据目录、工作区、WS 端口等）

## 📦 安装

### NuGet 安装

```bash
dotnet add package Netor.Cortana.Plugin.Native.Debugger
```

### PackageReference

```xml
<PackageReference Include="Netor.Cortana.Plugin.Native.Debugger" Version="1.0.0" />
```

## 🚀 快速开始

### 1. 创建调试控制台项目

```bash
dotnet new console -n MyPlugin.Debug
cd MyPlugin.Debug
dotnet add reference ../MyPlugin/MyPlugin.csproj
dotnet add package Netor.Cortana.Plugin.Native.Debugger
```

### 2. 编写入口代码

```csharp
using Netor.Cortana.Plugin.Native.Debugger;

// 创建调试宿主（自动发现 AppDomain 中的唯一插件）
var host = PluginDebugRunner.CreateHost();

// 进入交互式调试循环
await PluginDebugRunner.RunInteractiveAsync(host);
```

### 3. 运行调试

```bash
dotnet run
```

进入交互模式后，按格式输入：

```
Debug> <ToolName> [JSON参数]
```

示例：

```
Debug> GetWeather {"city": "Beijing"}
⏳ 执行中...

🟢 返回结果:
{
  "temperature": 22,
  "condition": "Sunny"
}

Debug> exit
👋 调试器已退出。
```

## 🏗️ 架构说明

### 项目结构

```
Netor.Cortana.Plugin.Native.Debuger/
├── PluginDebugRunner.cs              # 入口工具类（静态）
├── Discovery/
│   ├── PluginScanner.cs              # 插件扫描器 + PluginMetadata
│   └── ToolScanner.cs                # 工具扫描器 + ToolRegistry + ToolMetadata
├── Hosting/
│   ├── DebugPluginContext.cs         # 调试上下文（模拟 IPluginContext）
│   └── DebugPluginHost.cs            # 调试宿主（加载插件、管理工具）
├── Invocation/
│   └── ToolInvoker.cs                # 工具调用器（DI 实例化 + 参数绑定 + 执行）
└── README.md
```

### 依赖关系

```
Netor.Cortana.Plugin.Native.Debugger
├── Netor.Cortana.Plugin.Native       ([Plugin]、[Tool] 特性定义)
├── Netor.Cortana.Plugin              (IPluginContext 接口)
├── Microsoft.Extensions.Hosting      (宿主框架)
├── Microsoft.Extensions.Logging.Console (控制台日志)
└── Microsoft.Extensions.Http         (HTTP 客户端)
```

### 核心类说明

| 类名 | 命名空间 | 职责 |
|------|----------|------|
| `PluginDebugRunner` | `Netor.Cortana.Plugin.Native.Debugger` | 静态入口类，提供 `DiscoverPlugins()`、`CreateHost()`、`RunInteractiveAsync()` |
| `PluginScanner` | `...Debugger.Discovery` | 扫描程序集中的 `[Plugin]` 标记类，验证唯一性 |
| `ToolScanner` | `...Debugger.Discovery` | 扫描程序集中的 `[Tool]` 标记方法，构建工具注册表 |
| `DebugPluginHost` | `...Debugger.Hosting` | 调试宿主，负责服务容器构建、插件加载、工具调用 |
| `DebugPluginContext` | `...Debugger.Hosting` | 调试上下文，实现当前宿主侧的 `IPluginContext` 接口 |
| `ToolInvoker` | `...Debugger.Invocation` | 工具调用器，处理 DI 实例化、JSON 参数绑定、async/await 执行 |

### 执行流程

```
1. PluginDebugRunner.CreateHost()
   │
   ├─ DiscoverPlugins() → 扫描 AppDomain 中的程序集
   │   └─ PluginScanner.TryScan() → 查找 [Plugin] 标记类
   │
   └─ new DebugPluginHost(assembly, context)
       │
       ├─ PluginScanner.Scan() → 验证插件入口唯一性
       ├─ ToolScanner.Scan() → 扫描所有 [Tool] 方法
       ├─ 构建 ServiceCollection
    │   ├─ 注册宿主侧 IPluginContext
       │   ├─ 注册 ILoggerFactory / IHttpClientFactory
       │   ├─ 调用插件的 Configure(IServiceCollection) 静态方法
       │   └─ 注册所有包含 Tool 方法的类为单例
       └─ 创建 ToolInvoker

2. PluginDebugRunner.RunInteractiveAsync(host)
   │
   └─ 循环读取控制台输入
       ├─ 解析 ToolName 和 JSON 参数
       └─ host.InvokeToolAsync(toolName, jsonArgs)
           └─ ToolInvoker.InvokeAsync()
               ├─ 从 DI 获取实例
               ├─ 绑定 JSON 参数
               ├─ 执行方法（支持 async/await）
               └─ 序列化返回结果
```

## ⚙️ 进阶用法

### 指定程序集创建宿主

```csharp
var assembly = typeof(MyPlugin.MyPluginEntry).Assembly;
var host = PluginDebugRunner.CreateHost(assembly);
await PluginDebugRunner.RunInteractiveAsync(host);
```

### 自定义服务配置

```csharp
var host = PluginDebugRunner.CreateHost(services =>
{
    services.AddSingleton<IMyCustomService, MyCustomServiceImpl>();
    services.Configure<MyOptions>(options =>
    {
        options.Timeout = TimeSpan.FromSeconds(30);
    });
});
await PluginDebugRunner.RunInteractiveAsync(host);
```

### 自定义调试上下文

```csharp
var context = new DebugPluginContext(
    dataDirectory: "./my_debug_data",
    workspaceDirectory: "./my_workspace",
    wsPort: 8080);

var host = PluginDebugRunner.CreateHost();
// 注意：CreateHost() 内部会创建自己的 context，如需自定义 context，
// 请使用 DebugPluginHost 构造函数直接创建
var customHost = new DebugPluginHost(
    typeof(MyPluginEntry).Assembly,
    context);
await PluginDebugRunner.RunInteractiveAsync(customHost);
```

## 📋 插件开发要求

使用此调试器时，插件项目需要满足以下条件：

1. **有且仅有一个** 类标记 `[Plugin]` 特性
2. 工具方法标记 `[Tool]` 特性，支持以下签名：
   - `public Task<T> ToolName(ParametersDto args)` — 异步，带参数
   - `public T ToolName(ParametersDto args)` — 同步，带参数
   - `public Task ToolName()` — 异步，无参数
   - `public void ToolName()` — 同步，无参数
3. 可选提供静态配置方法：`public static void Configure(IServiceCollection services)`

## 📄 许可证

MIT License — Copyright © Netor Team 2025