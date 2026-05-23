# Netor.Madorin.Plugin.Native

Madorin 原生插件开发框架的**运行时库**，提供 Attribute 标记和运行时类型，配合 `Netor.Madorin.Plugin.Native.Generator` 源码生成器使用。

## 功能

- **`[Plugin]`** — 标记插件入口类，声明插件 Id、名称、版本、描述等元数据
- **`[Tool]`** — 标记工具类和工具方法，声明工具名称和描述
- **`[Parameter]`** — 标记工具方法参数的描述信息和是否必填
- **`PluginSettings`** — 插件运行时配置（数据目录、工作区目录、WebSocket 端口），由宿主注入

## 安装

```shell
dotnet add package Netor.Madorin.Plugin.Native
```

## 快速开始

### 1. 创建插件入口

```csharp
using Microsoft.Extensions.DependencyInjection;
using Netor.Madorin.Plugin.Native;

[Plugin(
    Id = "my_plugin",
    Name = "我的插件",
    Version = "1.0.0",
    Description = "插件描述")]
public static partial class Startup
{
    public static void Configure(IServiceCollection services)
    {
        // 注册自定义服务
    }
}
```

### 2. 编写工具类

```csharp
using Netor.Madorin.Plugin.Native;

[Tool]
public class MyTools
{
    [Tool(Description = "回显消息")]
    public string Echo(
        [Parameter(Description = "要回显的内容")] string message)
    {
        return $"[回显] {message}";
    }
}
```

### 3. 注入运行时配置

```csharp
[Tool]
public class MyTools
{
    private readonly PluginSettings _settings;

    public MyTools(PluginSettings settings)
    {
        _settings = settings;
    }

    [Tool(Description = "获取数据目录")]
    public string GetDataDir() => _settings.DataDirectory;
}
```

## Attribute 参考

### `[Plugin]`

| 属性 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `Id` | `string` | ✅ | 插件唯一标识，仅允许小写字母、数字和下划线 |
| `Name` | `string` | ✅ | 插件名称 |
| `Version` | `string` | — | 插件版本，默认 `"1.0.0"` |
| `Description` | `string` | — | 插件描述 |
| `Tags` | `string[]` | — | 分类标签 |
| `Instructions` | `string?` | — | AI 系统指令片段 |

### `[Tool]`

| 属性 | 类型 | 说明 |
|------|------|------|
| `Name` | `string?` | 工具名称，不填则从方法名自动转换 PascalCase → snake_case |
| `Description` | `string` | 工具描述 |

### `[Parameter]`

| 属性 | 类型 | 说明 |
|------|------|------|
| `Name` | `string?` | 参数名称，不填则使用方法参数名 |
| `Description` | `string` | 参数描述 |
| `Required` | `bool` | 是否必填，默认 `true` |

### `PluginSettings`

| 属性 | 类型 | 说明 |
|------|------|------|
| `DataDirectory` | `string` | 插件专属的数据存储目录 |
| `WorkspaceDirectory` | `string` | 当前工作区目录 |
| `WsPort` | `int` | WebSocket 服务器端口 |

## 工具命名规则

生成的工具名格式为 `{plugin_id}_{method_snake_case}`，例如：

- 插件 Id: `ntest`，方法 `EchoMessage` → 工具名 `ntest_echo_message`
- 插件 Id: `ntest`，方法 `MathAdd` → 工具名 `ntest_math_add`

## 要求

- .NET 10+
- 配合 `Netor.Madorin.Plugin.Native.Generator` 使用
- 项目需设置 `<PublishAot>true</PublishAot>` 以支持原生 AOT 发布
