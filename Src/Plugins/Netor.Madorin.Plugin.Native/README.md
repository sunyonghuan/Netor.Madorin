# Netor.Madorin.Plugin.Native

Madorin 原生插件开发框架的**运行时库**，提供 Attribute 标记和运行时类型，配合 `Netor.Madorin.Plugin.Native.Generator` 源码生成器使用。

## 功能

- **`[Plugin]`** — 标记插件入口类，声明插件 Id、名称、版本、描述等元数据
- **`[RequiredHostCapability]`** — 声明插件希望宿主提供的能力申请，用于授权摘要和设置界面
- **`[PluginSetting]`** — 声明插件设置项 schema，由宿主渲染配置界面并保存配置
- **`[Tool]`** — 标记工具类和工具方法，声明工具名称和描述
- **`[Parameter]`** — 标记工具方法参数的描述信息和是否必填
- **`PluginSettings`** — 插件运行时配置（目录、端口、扩展字段、插件总线端点等），由宿主注入

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
| `Capabilities` | `string[]` | — | 插件提供给宿主/模型层的能力声明 |
| `Instructions` | `string?` | — | AI 系统指令片段 |

### `[RequiredHostCapability]`

| 属性 | 类型 | 说明 |
|------|------|------|
| `Id` | `string` | 宿主能力 ID，例如 `host.llm.invoke.v1` |
| `Purpose` | `string?` | 能力用途 token，例如 `memory.extract` |
| `Required` | `bool` | 是否为核心功能必需能力 |
| `Reason` | `string?` | 给用户看的申请原因 |

### `[PluginSetting]`

| 属性 | 类型 | 说明 |
|------|------|------|
| `Key` | `string` | 插件内唯一配置键 |
| `Label` | `string?` | 设置界面显示名称 |
| `Description` | `string?` | 给用户看的说明 |
| `Type` | `PluginSettingType` | 字段类型 |
| `DefaultValue` | `string?` | 默认值，Generator 会按类型输出 JSON |
| `Required` | `bool` | 是否必填 |
| `Sensitive` | `bool` | 是否敏感，敏感值不得回显 |
| `Scope` | `string[]` | 支持的配置作用域 |
| `Options` | `string[]` | 枚举选项 |
| `ValidationMin` / `ValidationMax` | `double` | 数值区间校验 |
| `ValidationMinLength` / `ValidationMaxLength` | `int` | 字符串长度校验 |
| `ValidationPattern` | `string?` | 正则校验表达式 |
| `RestartRequired` | `bool` | 修改后是否要求重启生效 |

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

### `PluginSettingType`

- `String`
- `Secret`
- `Number`
- `Boolean`
- `Enum`
- `Path`
- `Json`

### `PluginSettings`

| 属性 | 类型 | 说明 |
|------|------|------|
| `DataDirectory` | `string` | 插件专属的数据存储目录 |
| `WorkspaceDirectory` | `string` | 当前工作区目录 |
| `PluginDirectory` | `string` | 插件目录 |
| `WsPort` | `int` | WebSocket 服务器端口 |
| `Extensions` | `IReadOnlyDictionary<string, string>` | 宿主 init 扩展参数 |
| `PluginBusEndpoint` | `string` | 内部插件总线端点 |
| `PluginBusProtocol` | `string` | 内部插件总线协议名 |
| `PluginBusVersion` | `string` | 内部插件总线协议版本 |
| `PluginBusPort` | `int` | 内部插件总线端口 |
| `ChatWsEndpoint` | `string` | 兼容旧调用点的总线端点别名 |
| `ConversationFeedEndpoint` | `string` | 兼容旧调用点的总线端点别名 |
| `ConversationFeedProtocol` | `string` | 兼容旧调用点的协议别名 |
| `ConversationFeedVersion` | `string` | 兼容旧调用点的版本别名 |
| `ConversationFeedPort` | `int` | 兼容旧调用点的端口别名 |

## 元数据输出

Generator 会把上述声明同步输出到：

- `plugin.json`：宿主静态发现和设置界面使用
- `cortana_plugin_get_info`：Native 宿主加载后的完整元数据 JSON

其中：

- `[Plugin].Capabilities` 会输出为 `capabilities` 与 `providedCapabilities`
- `[RequiredHostCapability]` 会输出为 `requiredHostCapabilities`
- `[PluginSetting]` 会输出为 `settingsSchema`

注意：

- `RequiredHostCapability` 是“申请声明”，不等于宿主已经授权。
- 插件运行时代码不要自己解析原始 `init` JSON，统一通过 `PluginSettings` 读取。

## 工具命名规则

生成的工具名格式为 `{plugin_id}_{method_snake_case}`，例如：

- 插件 Id: `ntest`，方法 `EchoMessage` → 工具名 `ntest_echo_message`
- 插件 Id: `ntest`，方法 `MathAdd` → 工具名 `ntest_math_add`

## 要求

- .NET 10+
- 引用 `Netor.Madorin.Plugin.Native` 即可自动获得 Generator 与 `buildTransitive` 行为；通常不需要额外单独引用 Generator 包
- 项目需设置 `<PublishAot>true</PublishAot>` 以支持原生 AOT 发布
