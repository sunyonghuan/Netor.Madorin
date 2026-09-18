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
- `category`
- `riskLevel`
- `idempotent`
- `tags`
- `searchHints`
- `capabilities`
- `providedCapabilities`
- `instructions`
- `tools`

说明：

- `Capabilities` 表示“插件提供给宿主或模型可调用层”的能力声明。
- 生成产物中会同时出现历史兼容字段 `capabilities` 和首选字段 `providedCapabilities`。
- `Category` / `RiskLevel` / `Idempotent` / `Tags` / `SearchHints` 是插件级默认值，所有工具默认继承；个别工具可在 `[Tool]` 上覆盖。
- 未写的可选字段不会出现在 `plugin.json` 里，不要为了“完整”去猜默认值。

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

## 5. 工具清单 Tools

AI 上下文里只能看到工具，看不到插件。因此 `plugin.json` 必须带 `tools` 数组；分类信息默认写在插件上，生成器在发布时写出标准清单。

工具自身字段（每个工具都有）：

- `name`：方法名转成的 snake_case 短名
- `description`：`[Tool(Description = ...)]`
- `inputSchema`：由参数生成的一层 JSON Schema（`type: object` + `properties`），不展开嵌套类型

插件默认、工具可覆盖：

- `category`
- `riskLevel`
- `idempotent`
- `tags`
- `searchHints`

生效规则：工具显式值 → 插件默认值 → 未写则没有。

覆盖是字段级的。清单里工具对象只写出被覆盖的字段，不要把插件默认值复制到每个 tool 上。

```csharp
[Plugin(
    Id = "my_plugin",
    Name = "My Plugin",
    Version = "1.0.0",
    Category = "development",
    RiskLevel = ToolRiskLevel.Low,
    Tags = ["development", "automation"],
    SearchHints = ["build", "deploy"])]
public static partial class Startup { }

[Tool]
public sealed class FileTools
{
    [Tool(Description = "Create a file")]
    public string CreateFile(string path) => path;

    [Tool(
        Description = "Delete everything",
        RiskLevel = ToolRiskLevel.Destructive,
        SearchHints = ["delete", "remove", "cleanup"])]
    public string DeleteAll() => "ok";
}
```

对应 `plugin.json`：

```json
{
  "id": "my_plugin",
  "category": "development",
  "riskLevel": "Low",
  "tags": ["development", "automation"],
  "searchHints": ["build", "deploy"],
  "tools": [
    {
      "name": "create_file",
      "description": "Create a file",
      "inputSchema": { "type": "object", "properties": { "path": { "type": "string" } }, "required": ["path"] }
    },
    {
      "name": "delete_all",
      "description": "Delete everything",
      "inputSchema": { "type": "object", "properties": {} },
      "riskLevel": "Destructive",
      "searchHints": ["delete", "remove", "cleanup"]
    }
  ]
}
```

## 6. ToolRiskLevel

`[Plugin]` / `[Tool]` 使用 `ToolRiskLevel` 声明风险级别，生成器按枚举成员名写入 `plugin.json`（例如 `"Low"`、`"Destructive"`）。

| 值 | 说明 | 适用场景 | 示例 |
| --- | --- | --- | --- |
| Low | 低风险（只读操作） | 读取文件、查询数据、列表操作 | read_file, list_directory, get_user_info |
| SensitiveRead | 敏感读取 | 读取密码、密钥、私有数据 | read_credentials, get_api_key, read_env |
| Write | 写操作（可逆） | 创建/修改文件、写入数据库 | write_file, create_directory, update_record |
| Destructive | 破坏性操作（不可逆） | 删除文件、清空数据、格式化 | delete_file, drop_table, format_disk |
| Process | 进程操作 | 启动/停止进程、执行命令 | run_command, kill_process, spawn_process |
| PowerShell | PowerShell 脚本执行 | 执行 PowerShell 脚本 | run_powershell, execute_script |
| Network | 网络操作 | HTTP 请求、网络连接 | http_request, download_file, connect_to_server |

## 7. AI 编码约束

- 不要手写 `plugin.json` 里的 `requiredHostCapabilities`、`settingsSchema`、`providedCapabilities`、`tools`。
- 不要把“插件提供能力”和“插件申请宿主能力”混成同一个字段。
- 不要把权限申请写进工具参数或自定义配置文件，除非框架当前协议明确不支持。
- 当用户要求“在宿主设置界面渲染配置项”时，优先想到 `[PluginSetting]`。
- 当用户要求“向宿主申请某个权限/能力”时，优先想到 `[RequiredHostCapability]`。
- 工具分类字段优先写在 `[Plugin]` 上；只有个别工具例外时才在 `[Tool]` 上覆盖。
