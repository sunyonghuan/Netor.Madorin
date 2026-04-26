# MCP 服务器集成指南

> 通道类型：`mcp` | 隔离级别：进程级 / 网络级

> 当前状态：推荐通道。对于新的跨语言工具集成和远程服务接入，优先使用 MCP；不要优先回到旧 Dotnet 插件模式。

## 概述

MCP（Model Context Protocol）通道允许 Cortana 作为 MCP 客户端连接到外部 MCP Server，将远程服务的工具能力无缝集成到 AI Agent 的工具链中。

与 Dotnet 和 Native 通道不同，MCP 通道不需要编写插件代码，只需在 UI 中配置 MCP Server 的连接信息即可。Cortana 通过 `ModelContextProtocol` SDK（1.2.0）自动发现并注册远程工具。

### 适用场景

- 接入社区生态中已有的 MCP Server（GitHub、文件系统、数据库等）
- 跨语言工具集成（Python、Node.js、Go 等编写的工具服务）
- 远程服务器上运行的工具（通过 SSE / Streamable HTTP）
- 需要独立部署和扩展的工具服务

### 支持的传输类型

| 传输类型 | 协议 | 适用场景 |
|---------|------|---------|
| `stdio` | 标准输入/输出 | 本地命令行工具，如 `npx`、`uvx`、`python` |
| `sse` | Server-Sent Events (HTTP) | 远程/本地 HTTP 服务（单向流） |
| `streamable-http` | HTTP Streaming | 远程/本地 HTTP 服务（双向流，推荐） |

### 架构流程

```
Cortana 宿主进程
┌──────────────────────────────┐
│  McpContextProvider          │──→ AI Agent 工具链
│    └── McpServerHost         │
│          ├── McpClient       │
│          │   └── Tools[]     │
│          └── Transport       │
│              ├── stdio ──────│──→ npx / uvx / python 子进程
│              ├── sse ────────│──→ HTTP SSE 服务器
│              └── streamable──│──→ HTTP Streaming 服务器
└──────────────────────────────┘

数据库（McpServerEntity）
  ├── 服务器 1: GitHub MCP (stdio, npx)
  ├── 服务器 2: 文件系统 MCP (stdio, python)
  └── 服务器 3: 远程 API MCP (streamable-http)
```

## 配置说明

MCP 服务器通过数据库实体 `McpServerEntity` 进行配置，在设置界面（SettingsWindow）中管理。每条记录代表一个 MCP Server 连接。

### 字段说明

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `Name` | string (64) | ✅ | 显示名称，用于 UI 识别 |
| `TransportType` | string (32) | ✅ | 传输类型：`stdio` / `sse` / `streamable-http` |
| `Command` | string (512) | stdio ✅ | 启动命令，如 `npx`、`python`、`uvx` |
| `Arguments` | List\<string\> | stdio | 启动参数列表 |
| `Url` | string (512) | http ✅ | SSE / Streamable HTTP 服务器地址 |
| `ApiKey` | string (256) | 否 | 认证密钥（Bearer Token） |
| `EnvironmentVariables` | Dict\<string, string?\> | 否 | 环境变量键值对（stdio 模式） |
| `Description` | string (1024) | 否 | 服务器能力描述 |
| `IsEnabled` | bool | — | 是否启用（默认 `true`） |

### 传输模式配置

#### stdio 模式

适用于本地命令行工具。Cortana 以子进程方式启动 MCP Server，通过 stdin/stdout 通信。

**配置要点：**
- `Command` — 启动命令（如 `npx`、`uvx`、`python`、`node`）
- `Arguments` — 命令参数列表
- `EnvironmentVariables` — 传递给子进程的环境变量（如 API Token）

**示例：GitHub MCP Server**

| 字段 | 值 |
|------|-----|
| Name | GitHub MCP |
| TransportType | `stdio` |
| Command | `npx` |
| Arguments | `["-y", "@modelcontextprotocol/server-github"]` |
| EnvironmentVariables | `{"GITHUB_TOKEN": "ghp_xxxxxxxxxxxx"}` |

**示例：文件系统 MCP Server**

| 字段 | 值 |
|------|-----|
| Name | 文件系统 |
| TransportType | `stdio` |
| Command | `npx` |
| Arguments | `["-y", "@modelcontextprotocol/server-filesystem", "C:\\Users\\me\\Documents"]` |

**实现细节：** Cortana 使用 `StdioClientTransport` 创建传输层：

```csharp
var options = new StdioClientTransportOptions
{
    Command = config.Command,          // "npx"
    Arguments = config.Arguments,      // ["-y", "@modelcontextprotocol/server-github"]
    Name = config.Name                 // "GitHub MCP"
};

// 合并环境变量到子进程
if (config.EnvironmentVariables.Count > 0)
{
    options.EnvironmentVariables = new Dictionary<string, string?>(config.EnvironmentVariables);
}

var transport = new StdioClientTransport(options, loggerFactory);
```

#### SSE 模式

适用于通过 Server-Sent Events 提供服务的远程 MCP Server。

**配置要点：**
- `Url` — SSE 端点地址（如 `http://localhost:3000/sse`）
- `ApiKey` — 可选的 Bearer Token 认证

**示例：本地 SSE 服务**

| 字段 | 值 |
|------|-----|
| Name | 本地 SSE 服务 |
| TransportType | `sse` |
| Url | `http://localhost:3000/sse` |

#### Streamable HTTP 模式

推荐的 HTTP 传输方式，支持双向流通信。

**配置要点：**
- `Url` — HTTP 端点地址
- `ApiKey` — 可选的 Bearer Token 认证

**示例：远程 API 服务**

| 字段 | 值 |
|------|-----|
| Name | 远程数据分析 |
| TransportType | `streamable-http` |
| Url | `https://mcp.example.com/api` |
| ApiKey | `sk-xxxxxxxxxxxx` |

**HTTP 传输实现细节：**

```csharp
var options = new HttpClientTransportOptions
{
    Endpoint = new Uri(config.Url),
    Name = config.Name,
    // 根据 TransportType 选择 SSE 或 Streamable HTTP 模式
    TransportMode = config.TransportType == "sse"
        ? HttpTransportMode.Sse
        : HttpTransportMode.StreamableHttp
};

// 添加 Bearer Token 认证头
if (!string.IsNullOrWhiteSpace(config.ApiKey))
{
    options.AdditionalHeaders = new Dictionary<string, string>
    {
        ["Authorization"] = $"Bearer {config.ApiKey}"
    };
}

var transport = new HttpClientTransport(options, loggerFactory);
```

## 连接生命周期

### McpServerHost 生命周期

每个启用的 MCP Server 对应一个 `McpServerHost` 实例，管理连接的完整生命周期：

```
1. 创建 McpServerHost(config, loggerFactory)
      ↓
2. ConnectAsync()
   ├── 根据 TransportType 创建传输层（stdio / sse / streamable-http）
   ├── McpClient.CreateAsync(transport, options, loggerFactory)
   └── ListToolsAsync() → 获取工具列表
      ↓
3. 工具可用 → McpContextProvider 注入 AI Agent
      ↓
4. RefreshToolsAsync()（可选，响应工具变更通知）
      ↓
5. DisposeAsync() → 断开连接，释放资源
```

### 工具发现与注册

连接成功后，Cortana 自动调用 `ListToolsAsync()` 获取远程工具列表。每个工具以 `McpClientTool`（实现 `AITool` 接口）的形式注册到 AI Agent 的工具链中。

```csharp
// McpServerHost.ConnectAsync 核心逻辑
_client = await McpClient.CreateAsync(
    transport,
    new McpClientOptions
    {
        ClientInfo = new()
        {
            Name = "Netor.Cortana",
            Version = "1.0.0",
            Title = "Netor.Cortana"
        }
    },
    _loggerFactory,
    cancellationToken);

// 自动发现远程工具
_tools = await _client.ListToolsAsync(cancellationToken: cancellationToken);
```

### McpContextProvider 集成

`McpContextProvider` 继承自 `AIContextProvider`，将 `McpServerHost` 的工具暴露给 AI Agent：

```csharp
internal sealed class McpContextProvider : AIContextProvider
{
    private readonly McpServerHost _host;

    // 使用 MCP Server 的数据库 ID 作为唯一 state key
    public override IReadOnlyList<string> StateKeys => [$"mcp:{_host.Id}"];

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(new AIContext
        {
            Tools = _host.Tools  // 直接暴露 MCP 工具列表
        });
    }
}
```

AI Agent 在处理用户请求时，会自动获取所有已连接 MCP Server 提供的工具，并根据工具描述决定何时调用。

## 与其他通道的对比

| 对比维度 | Dotnet 通道 | Native 通道 | MCP 通道 |
|---------|------------|------------|---------|
| 当前定位 | 历史兼容 | 当前推荐的本地通道 | 当前推荐的远程通道 |
| 开发语言 | C# / .NET | C/C++/Rust/C# AOT | 任意（Python/Node/Go 等） |
| 部署方式 | 复制 DLL 到插件目录 | 复制原生 DLL 到插件目录 | 配置连接信息即可 |
| 配置方式 | `plugin.json` | `plugin.json` | 数据库 + UI 配置 |
| 通信方式 | 进程内直接调用 | stdin/stdout JSON | MCP 协议（stdio/HTTP） |
| 隔离级别 | ALC 隔离 | 子进程隔离 | 进程/网络隔离 |
| 热插拔 | ✅ FileSystemWatcher | ✅ FileSystemWatcher | ✅ UI 启用/禁用 |
| 工具发现 | `IPlugin.Tools` | 导出函数 `get_info` | `ListToolsAsync()` 自动发现 |
| 适合场景 | 历史插件兼容 | 高性能原生计算 | 集成现有 MCP 生态 |

## 常见 MCP Server

以下是社区中常用的 MCP Server，均可通过 stdio 模式接入：

| MCP Server | 命令 | 参数 | 说明 |
|-----------|------|------|------|
| GitHub | `npx` | `["-y", "@modelcontextprotocol/server-github"]` | GitHub 仓库操作 |
| 文件系统 | `npx` | `["-y", "@modelcontextprotocol/server-filesystem", "<path>"]` | 本地文件读写 |
| PostgreSQL | `npx` | `["-y", "@modelcontextprotocol/server-postgres", "<conn>"]` | 数据库查询 |
| Puppeteer | `npx` | `["-y", "@modelcontextprotocol/server-puppeteer"]` | 浏览器自动化 |
| Brave Search | `npx` | `["-y", "@modelcontextprotocol/server-brave-search"]` | 网页搜索 |

> 完整的 MCP Server 列表请参阅：[MCP Servers Directory](https://github.com/modelcontextprotocol/servers)

## 注意事项

1. **Node.js 环境** — 大多数社区 MCP Server 基于 Node.js，使用 stdio 模式前需确保系统已安装 Node.js 和 npm
2. **环境变量安全** — API Token 等敏感信息通过 `EnvironmentVariables` 传递给子进程，存储在本地数据库中，请注意保护数据库文件
3. **连接超时** — 如果 MCP Server 启动缓慢（如 npx 首次下载包），连接可能超时，建议先手动运行一次确保包已缓存
4. **工具刷新** — 支持通过 `RefreshToolsAsync()` 刷新工具列表，适用于 MCP Server 动态添加/移除工具的场景
5. **错误处理** — 连接失败时会记录错误日志但不会影响其他 MCP Server 的连接，每个服务器独立管理
6. **资源释放** — `McpServerHost` 实现 `IAsyncDisposable`，应用退出时自动断开所有 MCP 连接




