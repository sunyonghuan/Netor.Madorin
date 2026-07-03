# Plugin Metadata Protocol

Native / Process 通道的插件共享一套“声明式元数据 + 运行时注入”协议。AI 在补代码时应优先复用这套协议，不要另造自定义清单结构。

## 1. 基础元数据

入口类使用类级别 `[Plugin]`：

```csharp
[Plugin(
    Id = "memory_helper",
    Name = "Memory Helper",
    Version = "1.0.0",
    Description = "示例插件",
    Tags = ["memory", "assistant"],
    Capabilities = ["memory.extract"],
    Instructions = "仅在用户明确要求时写入长期记忆。")]
public static partial class Startup
{
}
```

生成器会把它映射到：

- `id`
- `name`
- `version`
- `description`
- `tags`
- `capabilities`
- `providedCapabilities`
- `instructions`

说明：

- `Capabilities` 表示“插件提供给宿主或模型可调用层”的能力声明。
- 生成产物中会同时出现历史兼容字段 `capabilities` 和首选字段 `providedCapabilities`。

## 2. 宿主能力申请

如果插件希望宿主提供某项能力，在入口类上叠加 `[RequiredHostCapability]`：

```csharp
[RequiredHostCapability(
    Id = "host.llm.invoke.v1",
    Purpose = "memory.extract",
    Required = true,
    Reason = "需要调用宿主 LLM 提取结构化记忆。")]
[RequiredHostCapability(
    Id = "host.fs.workspace.read.v1",
    Purpose = "context.load",
    Required = false,
    Reason = "可选读取工作区上下文以增强回答质量。")]
public static partial class Startup
{
}
```

生成器会输出 `requiredHostCapabilities` 数组，每项字段为：

- `id`
- `purpose`
- `required`
- `reason`

规则：

- 这是“申请声明”，不是实际授权结果。
- `Required = true` 表示核心功能依赖该能力；若宿主不授予，插件应拒绝相关功能或在启动/调用时给出明确错误。
- `Required = false` 表示增强能力；若宿主不授予，插件应优雅降级。
- `Reason` 面向用户和宿主设置界面，应写成可读的授权说明，不要写内部实现细节。

## 3. 设置界面 Schema

如果插件需要让宿主在设置界面渲染配置项，在入口类上叠加 `[PluginSetting]`：

```csharp
[PluginSetting(
    Key = "api_key",
    Label = "API Key",
    Description = "第三方服务访问密钥",
    Type = PluginSettingType.Secret,
    Required = true,
    Sensitive = true,
    Scope = ["user"],
    RestartRequired = true)]
[PluginSetting(
    Key = "endpoint",
    Label = "服务地址",
    Description = "第三方 API 根地址",
    Type = PluginSettingType.String,
    DefaultValue = "https://api.example.com",
    ValidationPattern = "^https://",
    Scope = ["user", "workspace"])]
[PluginSetting(
    Key = "mode",
    Label = "执行模式",
    Type = PluginSettingType.Enum,
    Options = ["fast", "balanced", "accurate"],
    DefaultValue = "balanced")]
public static partial class Startup
{
}
```

生成器会输出 `settingsSchema`，每项可能包含：

- `key`
- `label`
- `description`
- `type`
- `defaultValue`
- `required`
- `sensitive`
- `scope`
- `options`
- `validation`
- `restartRequired`

`PluginSettingType` 当前支持：

- `String`
- `Secret`
- `Number`
- `Boolean`
- `Enum`
- `Path`
- `Json`

验证规则映射：

- `ValidationMin` → `validation.min`
- `ValidationMax` → `validation.max`
- `ValidationMinLength` → `validation.minLength`
- `ValidationMaxLength` → `validation.maxLength`
- `ValidationPattern` → `validation.pattern`

规则：

- `Key` 在插件内必须唯一，且应稳定，避免后续改名导致宿主侧配置迁移困难。
- 敏感值必须同时满足 `Type = Secret` 或 `Sensitive = true` 的语义要求，并且业务代码不得打印到日志。
- `Options` 只适用于 `Enum`。
- `DefaultValue` 会按声明类型输出成对应 JSON 值，不要自己拼引号或额外转义。

## 4. 运行时注入配置

宿主不会把 `settingsSchema` 自动展开成强类型属性；插件运行时统一通过 `PluginSettings` 获取宿主注入上下文。

当前内置运行时字段包括：

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

用法示例：

```csharp
public sealed class WorkspaceTools(PluginSettings settings)
{
    [Tool(Description = "返回运行时上下文")]
    public object GetContext()
        => new
        {
            settings.DataDirectory,
            settings.WorkspaceDirectory,
            settings.PluginDirectory,
            settings.Extensions
        };
}
```

规则：

- 插件代码只读取 `PluginSettings`，不要自己解析原始 `init` JSON。
- `Extensions` 用于宿主后续扩展参数传递；访问前先判断键是否存在。
- Process 调试时必须先 `InitAsync()`，否则依赖 `PluginSettings` 的工具会因尚未注入而失败。

## 5. AI 编码约束

- 不要手写 `plugin.json` 里的 `requiredHostCapabilities`、`settingsSchema`、`providedCapabilities`。
- 不要把“插件提供能力”和“插件申请宿主能力”混成同一个字段。
- 不要把权限申请写进工具参数或自定义配置文件，除非框架当前协议明确不支持。
- 当用户要求“在宿主设置界面渲染配置项”时，优先想到 `[PluginSetting]`。
- 当用户要求“向宿主申请某个权限/能力”时，优先想到 `[RequiredHostCapability]`。
